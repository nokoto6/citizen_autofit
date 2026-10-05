using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// Closest-skin lookup that only considers the skin a given bone drives.
///
/// A sleeve hangs right next to the ribs, and the closest skin to its underside is often the
/// torso. But the sleeve is skinned to the arm and has to follow the arm.
/// </summary>
public sealed class SkinByBone
{
	readonly TriMesh body;
	readonly Dictionary<int, float>[] triWeights;
	readonly int[][] family;
	readonly int[][] wideFamily;
	readonly Dictionary<int, (TriMesh Mesh, int[] Tris)> trees = new();

	public SkinByBone( TriMesh body, SkinnedGeometry geo ) : this( body, geo.BoneIndex, geo.BoneWeight, geo.BoneParents )
	{
	}

	/// <param name="boneIndex">Four bone slots per body vertex.</param>
	/// <param name="parents">Parent of each bone the slots refer to, -1 for roots.</param>
	public SkinByBone( TriMesh body, int[] boneIndex, float[] boneWeight, int[] parents )
	{
		this.body = body;
		triWeights = new Dictionary<int, float>[body.TriCount];
		for ( int t = 0; t < body.TriCount; t++ )
		{
			var w = new Dictionary<int, float>();
			for ( int k = 0; k < 3; k++ )
			{
				int v = body.Tris[t * 3 + k];
				for ( int j = 0; j < 4; j++ )
				{
					int bone = boneIndex[v * 4 + j];
					float weight = boneWeight[v * 4 + j] / 3f;
					if ( bone < 0 || weight <= 0 ) continue;
					w[bone] = w.TryGetValue( bone, out float sum ) ? sum + weight : weight;
				}
			}
			triWeights[t] = w;
		}

		// A bone's skin includes its parent's and children's: weights blend across joints, and
		// twist helpers share the limb with the bone they help.
		int bones = parents.Length;
		var children = new List<int>[bones];
		for ( int i = 0; i < bones; i++ ) children[i] = new List<int>();
		for ( int i = 0; i < bones; i++ )
			if ( parents[i] >= 0 ) children[parents[i]].Add( i );

		family = new int[bones][];
		for ( int i = 0; i < bones; i++ )
		{
			var set = new HashSet<int> { i };
			foreach ( int c in children[i] ) set.Add( c );
			int parent = parents[i];
			if ( parent >= 0 )
			{
				set.Add( parent );
				foreach ( int sibling in children[parent] )
					if ( children[sibling].Count == 0 ) set.Add( sibling );
			}
			family[i] = new int[set.Count];
			set.CopyTo( family[i] );
		}

		// One step further out: the families of the family.
		wideFamily = new int[bones][];
		for ( int i = 0; i < bones; i++ )
		{
			var set = new HashSet<int>();
			foreach ( int member in family[i] )
				foreach ( int next in family[member] ) set.Add( next );
			wideFamily[i] = new int[set.Count];
			set.CopyTo( wideFamily[i] );
		}
	}

	/// <summary>
	/// Like <see cref="Owns"/>, but reaching two bones away. Loose enough to cross a joint,
	/// tight enough that the spine doesn't claim an arm.
	/// </summary>
	public float OwnsNearby( int tri, int bone )
	{
		if ( bone < 0 || bone >= wideFamily.Length ) return 0;
		float sum = 0;
		foreach ( int member in wideFamily[bone] )
			if ( triWeights[tri].TryGetValue( member, out float w ) ) sum += w;
		return sum;
	}

	/// <summary>How much of a body triangle belongs to this bone, its parent or its children. 0..1.</summary>
	public float Owns( int tri, int bone )
	{
		if ( bone < 0 || bone >= family.Length ) return 0;
		float sum = 0;
		foreach ( int member in family[bone] )
			if ( triWeights[tri].TryGetValue( member, out float w ) ) sum += w;
		return sum;
	}

	/// <summary>The part of the body this bone, its parent and its children drive. Null if there is none.</summary>
	public TriMesh Mesh( int bone ) => Entry( bone ).Mesh;

	/// <summary>Closest point on that bone's skin. False if the bone drives no skin.</summary>
	public bool Nearest( Vec3 p, int bone, out Vec3 point, out int tri, out float distance )
	{
		point = p; tri = -1; distance = 0;
		var entry = Entry( bone );
		if ( entry.Mesh == null ) return false;
		distance = entry.Mesh.Nearest( p, out point, out int local );
		tri = entry.Tris[local];
		return true;
	}

	(TriMesh Mesh, int[] Tris) Entry( int bone )
	{
		if ( bone < 0 || bone >= family.Length ) return default;

		// Built on first use, and several garments can be fitted at once on different threads.
		lock ( trees )
		{
			if ( !trees.TryGetValue( bone, out var entry ) )
			{
				var own = new List<int>();
				for ( int t = 0; t < body.TriCount; t++ )
					if ( Owns( t, bone ) >= 0.5f ) own.Add( t );

				if ( own.Count >= 4 )
				{
					var tris = new int[own.Count * 3];
					for ( int i = 0; i < own.Count; i++ )
						Array.Copy( body.Tris, own[i] * 3, tris, i * 3, 3 );
					entry = (new TriMesh( body.Verts, tris ), own.ToArray());
				}
				trees[bone] = entry;
			}

			return entry;
		}
	}
}

/// <summary>
/// Moves a garment so it sits on another body the way it sat on the stock one.
///
/// Every garment vertex follows the stock skin it is closest to, using the body map to know
/// where that skin went. The garment is handled one connected piece at a time. A piece whose
/// vertices all have the same skin weights cannot bend when the character animates, so it is
/// rigid (sword, button, hat, glasses) and is moved as a whole, together with any other rigid
/// pieces it is part of an object with. Everything else is cloth and is moved per vertex.
/// </summary>
public static class GarmentFit
{
	const float GapCap = 0.05f * Units.Metre;    // gaps up to this size are restored, so a jacket stays above the shirt under it
	const float MaxPush = 0.05f * Units.Metre;   // the collision pass is a touch-up; anything further is a misread
	const float Contact = 0.03f * Units.Metre;   // a garment this close to the skin counts as resting on it
	const float Hidden = 0.005f * Units.Metre;   // further under the stock skin than this was never meant to be seen
	const float Touching = 0.002f * Units.Metre; // rigid pieces this close together are parts of one object
	const int LoosePieces = 16;                  // more rigid pieces than this in one heap is hair or the like, not an object

	// Points inside each triangle to test: centre, edge midpoints and one near each corner.
	static readonly float[][] Samples =
	{
		new[] { 1 / 3f, 1 / 3f, 1 / 3f }, new[] { 0.5f, 0.5f, 0f }, new[] { 0f, 0.5f, 0.5f }, new[] { 0.5f, 0f, 0.5f },
		new[] { 0.7f, 0.15f, 0.15f }, new[] { 0.15f, 0.7f, 0.15f }, new[] { 0.15f, 0.15f, 0.7f },
	};

	/// <param name="boneIndex">Four bone slots per garment vertex, as indices into the stock body's bones (-1 for none).</param>
	/// <param name="skinMove">What <see cref="BodyMap"/> worked out for this pair of bodies.</param>
	public static Vec3[] Fit( Vec3[] verts, int[] tris, int[] boneIndex, float[] boneWeight, TriMesh old, TriMesh other, Vec3[] skinMove, SkinByBone byBone )
	{
		var weld = MeshTools.Weld( verts, out int n );
		var firstOf = new int[n];
		for ( int i = verts.Length - 1; i >= 0; i-- )
			firstOf[weld[i]] = i;

		var pts = new Vec3[n];
		for ( int i = 0; i < n; i++ ) pts[i] = verts[firstOf[i]];

		Vec3 SkinMoveAt( Vec3 s, int tri )
		{
			int ia = old.Tris[tri * 3], ib = old.Tris[tri * 3 + 1], ic = old.Tris[tri * 3 + 2];
			Barycentric( s, old.Verts[ia], old.Verts[ib], old.Verts[ic], out float u, out float v, out float w );
			return skinMove[ia] * u + skinMove[ib] * v + skinMove[ic] * w;
		}

		// Each vertex follows the closest stock skin, as long as that skin belongs to the bones
		// the vertex is weighted to or their neighbours. When it doesn't (the underside of a
		// sleeve is closest to the ribs), the vertex follows its own bones' skin instead.
		var move = new Vec3[n];
		var gap0 = new float[n];
		var away = new Vec3[n];   // direction from the skin to the vertex
		for ( int i = 0; i < n; i++ )
		{
			Vec3 x = pts[i];
			int src = firstOf[i];
			float distance = old.Nearest( x, out var s, out int tri );
			Vec3 from = s;
			move[i] = SkinMoveAt( s, tri );

			float mine = 0, share = 0;
			for ( int j = 0; j < 4; j++ )
			{
				float w = boneWeight[src * 4 + j];
				if ( w < 0.1f ) continue;
				mine += w;
				share += w * byBone.Owns( tri, boneIndex[src * 4 + j] );
			}

			if ( mine > 0 && share / mine < 0.3f )
			{
				float total = 0, best = 0;
				var sum = Vec3.Zero;
				for ( int j = 0; j < 4; j++ )
				{
					float w = boneWeight[src * 4 + j];
					if ( w < 0.1f || !byBone.Nearest( x, boneIndex[src * 4 + j], out var own, out int ownTri, out float ownDistance ) ) continue;
					sum += SkinMoveAt( own, ownTri ) * w;
					total += w;
					if ( w > best ) { best = w; from = own; distance = ownDistance; }
				}
				if ( total > 0 ) move[i] = sum / total;
			}

			gap0[i] = old.Inside( x ) ? -distance : distance;
			if ( distance > 1e-7f * Units.Metre )
				away[i] = (x - from) / distance * (gap0[i] >= 0 ? 1f : -1f);
		}

		// Loose cloth hanging between two limbs flips between them from vertex to vertex.
		// A little smoothing evens that out without washing away real detail.
		var edges = MeshTools.Edges( tris, weld );
		var all = new bool[n];
		Array.Fill( all, true );
		MeshTools.Relax( move, all, edges, 2, 0.5f );

		var fitted = new Vec3[n];
		for ( int i = 0; i < n; i++ ) fitted[i] = pts[i] + move[i];

		// outward is the direction from the point's own skin to the point on the stock body.
		// A push that goes against it comes from some other body part the point has ended up
		// inside of (the underside of a sleeve inside a wide torso). Following it would glue
		// the cloth to the wrong part, so it is ignored.
		bool Push( Vec3 p, float oldGap, Vec3 outward, out Vec3 push )
		{
			push = Vec3.Zero;

			// Garments have geometry that was under the skin all along: caps that close a
			// sleeve from the inside, hair roots, the tips of a pair of glasses. It has no gap
			// to keep, and dragging it out takes the visible cloth around it along.
			if ( oldGap < -Hidden ) return false;
			float gap = other.SignedGap( p, out var s );
			float need = MathF.Min( oldGap, GapCap ) - gap;
			if ( need <= 1e-4f * Units.Metre || need > MaxPush ) return false;
			Vec3 v = p - s;
			float length = v.Length();
			if ( length < 1e-7f * Units.Metre ) return false;
			Vec3 direction = v / length * (gap >= 0 ? 1f : -1f);
			if ( Vec3.Dot( direction, outward ) < -0.25f ) return false;
			push = direction * need;
			return true;
		}

		// Connected pieces.
		var piece = Islands( n, edges );
		var members = new Dictionary<int, List<int>>();
		for ( int i = 0; i < n; i++ )
		{
			if ( !members.TryGetValue( piece[i], out var list ) ) members[piece[i]] = list = new List<int>();
			list.Add( i );
		}

		var pieceTris = new Dictionary<int, List<int>>();
		var seenTris = new HashSet<(int, int, int)>();
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			int a = weld[tris[t]], b = weld[tris[t + 1]], c = weld[tris[t + 2]];
			if ( a == b || b == c || a == c ) continue;
			var key = Sorted( a, b, c );
			if ( !seenTris.Add( key ) ) continue;
			if ( !pieceTris.TryGetValue( piece[a], out var list ) ) pieceTris[piece[a]] = list = new List<int>();
			list.Add( a ); list.Add( b ); list.Add( c );
		}

		// Rigid pieces that touch and share their skinning are one solid object: the frame and
		// the lenses of a pair of glasses, the blade and the guard of a sword. Fitted one by one
		// they drift apart, so they are moved together.
		var rigid = new List<List<int>>();
		var rigidKey = new List<int>();
		var keyIds = new Dictionary<string, int>();
		foreach ( var (root, idx) in members )
		{
			if ( !IsRigid( idx, firstOf, boneIndex, boneWeight ) ) continue;
			string key = WeightKey( firstOf[idx[0]], boneIndex, boneWeight );
			if ( !keyIds.TryGetValue( key, out int id ) ) keyIds[key] = id = keyIds.Count;
			rigid.Add( idx );
			rigidKey.Add( id );
		}

		var lo = new Vec3[rigid.Count];
		var hi = new Vec3[rigid.Count];
		for ( int r = 0; r < rigid.Count; r++ )
		{
			lo[r] = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			hi[r] = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( int i in rigid[r] )
			{
				lo[r] = new Vec3( MathF.Min( lo[r].X, pts[i].X ), MathF.Min( lo[r].Y, pts[i].Y ), MathF.Min( lo[r].Z, pts[i].Z ) );
				hi[r] = new Vec3( MathF.Max( hi[r].X, pts[i].X ), MathF.Max( hi[r].Y, pts[i].Y ), MathF.Max( hi[r].Z, pts[i].Z ) );
			}
		}

		// Bounding boxes are enough to tell touching from apart: a lens sits inside its frame,
		// two buttons on a shirt are nowhere near each other.
		var solid = new int[rigid.Count];
		for ( int r = 0; r < solid.Length; r++ ) solid[r] = r;
		int SolidOf( int r )
		{
			while ( solid[r] != r ) { solid[r] = solid[solid[r]]; r = solid[r]; }
			return r;
		}

		for ( int r = 0; r < rigid.Count; r++ )
		{
			for ( int q = r + 1; q < rigid.Count; q++ )
			{
				if ( rigidKey[r] != rigidKey[q] ) continue;
				if ( lo[r].X > hi[q].X + Touching || lo[q].X > hi[r].X + Touching || lo[r].Y > hi[q].Y + Touching || lo[q].Y > hi[r].Y + Touching || lo[r].Z > hi[q].Z + Touching || lo[q].Z > hi[r].Z + Touching ) continue;
				int a = SolidOf( r ), b = SolidOf( q );
				if ( a != b ) solid[a] = b;
			}
		}

		var solids = new Dictionary<int, List<int>>();
		for ( int r = 0; r < rigid.Count; r++ )
		{
			int key = SolidOf( r );
			if ( !solids.TryGetValue( key, out var parts ) ) solids[key] = parts = new List<int>();
			parts.Add( r );
		}

		foreach ( var parts in solids.Values )
		{
			// Dozens of pieces in a heap are not one object. That is hair cards, feathers or
			// scales, which lie loosely on the body and follow it one by one, the way cloth
			// would.
			if ( parts.Count > LoosePieces )
			{
				foreach ( int r in parts ) FitRigid( rigid[r], pts, move, gap0, away, fitted, Push, true );
				continue;
			}

			var whole = new List<int>();
			foreach ( int r in parts ) whole.AddRange( rigid[r] );
			FitRigid( whole, pts, move, gap0, away, fitted, Push, false );
		}

		foreach ( var (root, idx) in members )
		{
			if ( IsRigid( idx, firstOf, boneIndex, boneWeight ) ) continue;

			// Interpolation and smoothing can leave a vertex a little under the new skin.
			for ( int pass = 0; pass < 2; pass++ )
			{
				int moved = 0;
				foreach ( int i in idx )
				{
					if ( !Push( fitted[i], gap0[i], away[i], out var p ) ) continue;
					fitted[i] += p;
					moved++;
				}
				if ( moved == 0 ) break;
			}

			// Each push is decided for one vertex at a time, which leaves spikes where a vertex
			// was pushed and its neighbours weren't (armpits, mostly). Spread the pushes over
			// the neighbours so the cloth stays smooth.
			var pushField = new Vec3[n];
			var mask = new bool[n];
			foreach ( int i in idx ) { pushField[i] = fitted[i] - (pts[i] + move[i]); mask[i] = true; }
			MeshTools.Relax( pushField, mask, edges, 2, 0.5f );
			foreach ( int i in idx ) fitted[i] = pts[i] + move[i] + pushField[i];

			// The skin can still come through the middle of a triangle, typically a hard edge
			// of the body between two cloth vertices. Check a few points inside each triangle
			// and lift its corners. This goes last and is not smoothed: it is what keeps edges
			// covered.
			if ( !pieceTris.TryGetValue( root, out var mine ) ) continue;

			// Measure the same points on the stock body first. A flat triangle over curved skin
			// dips under it in the middle even when its corners don't, and that is how the
			// garment was made, not something to fix.
			var before = new float[mine.Count / 3 * Samples.Length];
			for ( int t = 0; t < mine.Count; t += 3 )
				for ( int k = 0; k < Samples.Length; k++ )
					before[t / 3 * Samples.Length + k] = old.SignedGap( Blend( pts, mine, t, Samples[k] ), out _ );

			var lift = new Dictionary<int, Vec3>();
			for ( int pass = 0; pass < 4; pass++ )
			{
				lift.Clear();
				for ( int t = 0; t < mine.Count; t += 3 )
				{
					// A triangle with a corner deep under the stock skin dives into the body on purpose.
					if ( gap0[mine[t]] < -Hidden || gap0[mine[t + 1]] < -Hidden || gap0[mine[t + 2]] < -Hidden ) continue;
					for ( int k = 0; k < Samples.Length; k++ )
					{
						var bw = Samples[k];
						if ( !Push( Blend( fitted, mine, t, bw ), before[t / 3 * Samples.Length + k], Blend( away, mine, t, bw ), out var p ) ) continue;
						for ( int c = 0; c < 3; c++ )
						{
							if ( bw[c] <= 0 ) continue;
							int vi = mine[t + c];
							if ( !lift.TryGetValue( vi, out var have ) || p.LengthSquared() > have.LengthSquared() ) lift[vi] = p;
						}
					}
				}
				foreach ( var (vi, p) in lift ) fitted[vi] += p;
				if ( lift.Count == 0 ) break;
			}
		}

		var result = new Vec3[verts.Length];
		for ( int i = 0; i < verts.Length; i++ ) result[i] = fitted[weld[i]];
		return result;
	}

	/// <summary>
	/// A garment's vertices index the garment's own skeleton. Returns the same four slots per
	/// vertex as indices into the body's bones. A bone the body doesn't have (older garments
	/// carry a few) stands in for the nearest ancestor the body does have.
	/// </summary>
	public static int[] BonesOnBody( SkinnedGeometry garment, SkinnedGeometry body )
	{
		var byName = new Dictionary<string, int>();
		for ( int i = 0; i < body.BoneNames.Length; i++ )
			byName[body.BoneNames[i]] = i;

		var map = new int[garment.BoneNames.Length];
		for ( int i = 0; i < map.Length; i++ )
		{
			int bone = i;
			while ( bone >= 0 && !byName.ContainsKey( garment.BoneNames[bone] ) )
				bone = garment.BoneParents[bone];
			map[i] = bone >= 0 ? byName[garment.BoneNames[bone]] : -1;
		}

		var result = new int[garment.BoneIndex.Length];
		for ( int i = 0; i < result.Length; i++ )
			result[i] = garment.BoneIndex[i] >= 0 ? map[garment.BoneIndex[i]] : -1;
		return result;
	}

	delegate bool PushTest( Vec3 p, float oldGap, Vec3 outward, out Vec3 push );

	/// <param name="keepGaps">Also restore the gap each vertex had over the skin, the way cloth does. For loose pieces like hair cards.</param>
	static void FitRigid( List<int> idx, Vec3[] pts, Vec3[] move, float[] gap0, Vec3[] away, Vec3[] fitted, PushTest push, bool keepGaps )
	{
		// Trust the vertices that actually rest on the body. The far end of a sword says
		// nothing about how the back under it moved.
		var w = new float[idx.Count];
		float total = 0;
		var meanAway = Vec3.Zero;
		int touching = 0;
		for ( int k = 0; k < idx.Count; k++ )
		{
			int i = idx[k];
			w[k] = MathF.Exp( -MathF.Max( gap0[i], 0 ) / Contact );
			total += w[k];
			if ( gap0[i] < Contact ) { meanAway += away[i]; touching++; }
		}

		// A piece that goes all the way round the body (hat, glasses, bracelet) has to grow
		// with it. One that sits on one side (sword, backpack, button) only moves and turns.
		bool wraps = touching >= 8 && (meanAway / touching).Length() < 0.5f;

		var sourceMean = Vec3.Zero;
		var targetMean = Vec3.Zero;
		for ( int k = 0; k < idx.Count; k++ )
		{
			sourceMean += pts[idx[k]] * (w[k] / total);
			targetMean += (pts[idx[k]] + move[idx[k]]) * (w[k] / total);
		}

		// Cross covariance of the two point sets; its rotation part is the best rigid turn.
		var h = new float[9];
		float spread = 0;
		for ( int k = 0; k < idx.Count; k++ )
		{
			Vec3 p = pts[idx[k]] - sourceMean, q = pts[idx[k]] + move[idx[k]] - targetMean;
			float f = w[k] / total;
			h[0] += f * q.X * p.X; h[1] += f * q.X * p.Y; h[2] += f * q.X * p.Z;
			h[3] += f * q.Y * p.X; h[4] += f * q.Y * p.Y; h[5] += f * q.Y * p.Z;
			h[6] += f * q.Z * p.X; h[7] += f * q.Z * p.Y; h[8] += f * q.Z * p.Z;
			spread += f * p.LengthSquared();
		}

		float[] rotation = total >= 6f ? Rotation( h ) : null;
		float scale = 1f;
		if ( rotation != null && wraps && spread > 1e-12f )
		{
			float trace = 0;
			for ( int k = 0; k < 9; k++ ) trace += rotation[k] * h[k];
			scale = Math.Clamp( trace / spread, 0.5f, 2f );
		}

		foreach ( int i in idx )
		{
			Vec3 p = pts[i] - sourceMean;
			if ( rotation != null )
				p = new Vec3( rotation[0] * p.X + rotation[1] * p.Y + rotation[2] * p.Z, rotation[3] * p.X + rotation[4] * p.Y + rotation[5] * p.Z, rotation[6] * p.X + rotation[7] * p.Y + rotation[8] * p.Z ) * scale;
			fitted[i] = targetMean + p;
		}

		// If it still digs in somewhere, shift the whole piece out. A solid object only has to
		// stop going deeper than it used to. How far off the skin it floats is the fit's
		// business; restoring that here would lift glasses off a flat face by the depth of the
		// eye sockets they were made for.
		//
		// One shift has to serve every vertex that digs in. Going by the worst one alone goes
		// wrong when two of them want opposite things (both arms of a pair of glasses on a
		// wider head): the piece gets thrown to one side. So step by the average of what is
		// still wanted. That settles in the middle of a conflict and ends up clear otherwise.
		var wanted = new List<(Vec3 Direction, float Distance)>();
		for ( int pass = 0; pass < 6; pass++ )
		{
			wanted.Clear();
			foreach ( int i in idx )
			{
				if ( !push( fitted[i], keepGaps ? gap0[i] : MathF.Min( gap0[i], 0 ), away[i], out var p ) ) continue;
				float distance = p.Length();
				wanted.Add( (p / distance, distance) );
			}
			if ( wanted.Count == 0 ) break;

			var shift = Vec3.Zero;
			for ( int step = 0; step < 32; step++ )
			{
				var sum = Vec3.Zero;
				int open = 0;
				foreach ( var (direction, distance) in wanted )
				{
					float left = distance - Vec3.Dot( direction, shift );
					if ( left < 1e-4f * Units.Metre ) continue;
					sum += direction * left;
					open++;
				}
				if ( open == 0 ) break;
				shift += sum / open;
			}

			foreach ( int i in idx ) fitted[i] += shift;
		}
	}

	/// <summary>
	/// The rotation closest to a 3x3 matrix (row-major), or null if the matrix is too flat to
	/// say (points on a line, or a mirror image).
	/// </summary>
	static float[] Rotation( float[] m )
	{
		var r = MeshTools.Copy( m );
		float norm = 0;
		foreach ( float v in r ) norm += v * v;
		if ( norm < 1e-20f ) return null;
		norm = MathF.Sqrt( norm );
		for ( int k = 0; k < 9; k++ ) r[k] /= norm;

		// Average the matrix with its inverse transpose until it stops changing.
		for ( int pass = 0; pass < 30; pass++ )
		{
			float det = r[0] * (r[4] * r[8] - r[5] * r[7]) - r[1] * (r[3] * r[8] - r[5] * r[6]) + r[2] * (r[3] * r[7] - r[4] * r[6]);
			if ( det < 1e-6f ) return null;
			var it = new[]
			{
				(r[4] * r[8] - r[5] * r[7]) / det, (r[5] * r[6] - r[3] * r[8]) / det, (r[3] * r[7] - r[4] * r[6]) / det,
				(r[2] * r[7] - r[1] * r[8]) / det, (r[0] * r[8] - r[2] * r[6]) / det, (r[1] * r[6] - r[0] * r[7]) / det,
				(r[1] * r[5] - r[2] * r[4]) / det, (r[2] * r[3] - r[0] * r[5]) / det, (r[0] * r[4] - r[1] * r[3]) / det,
			};
			float change = 0;
			for ( int k = 0; k < 9; k++ )
			{
				float next = 0.5f * (r[k] + it[k]);
				change += MathF.Abs( next - r[k] );
				r[k] = next;
			}
			if ( change < 1e-6f ) break;
		}

		return r;
	}

	static bool IsRigid( List<int> idx, int[] firstOf, int[] boneIndex, float[] boneWeight )
	{
		string first = null;
		foreach ( int i in idx )
		{
			string key = WeightKey( firstOf[i], boneIndex, boneWeight );
			first ??= key;
			if ( key != first ) return false;
		}
		return true;
	}

	static string WeightKey( int vertex, int[] boneIndex, float[] boneWeight )
	{
		var parts = new List<(int, int)>();
		for ( int j = 0; j < 4; j++ )
		{
			float w = boneWeight[vertex * 4 + j];
			if ( w >= 0.025f ) parts.Add( (boneIndex[vertex * 4 + j], (int)MathF.Round( w * 20 )) );
		}
		parts.Sort();
		return string.Join( ",", parts );
	}

	static int[] Islands( int n, List<(int A, int B)> edges )
	{
		var parent = new int[n];
		for ( int i = 0; i < n; i++ ) parent[i] = i;

		int Find( int x )
		{
			while ( parent[x] != x ) { parent[x] = parent[parent[x]]; x = parent[x]; }
			return x;
		}

		foreach ( var (a, b) in edges )
		{
			int ra = Find( a ), rb = Find( b );
			if ( ra != rb ) parent[ra] = rb;
		}

		for ( int i = 0; i < n; i++ ) parent[i] = Find( i );
		return parent;
	}

	static (int, int, int) Sorted( int a, int b, int c )
	{
		if ( a > b ) (a, b) = (b, a);
		if ( b > c ) (b, c) = (c, b);
		if ( a > b ) (a, b) = (b, a);
		return (a, b, c);
	}

	static Vec3 Blend( Vec3[] values, List<int> tris, int t, float[] w ) =>
		values[tris[t]] * w[0] + values[tris[t + 1]] * w[1] + values[tris[t + 2]] * w[2];

	static void Barycentric( Vec3 p, Vec3 a, Vec3 b, Vec3 c, out float u, out float v, out float w )
	{
		Vec3 v0 = b - a, v1 = c - a, v2 = p - a;
		float d00 = Vec3.Dot( v0, v0 ), d01 = Vec3.Dot( v0, v1 ), d11 = Vec3.Dot( v1, v1 );
		float d20 = Vec3.Dot( v2, v0 ), d21 = Vec3.Dot( v2, v1 );
		float den = d00 * d11 - d01 * d01;
		if ( MathF.Abs( den ) < 1e-18f ) { u = 1; v = w = 0; return; }
		v = (d11 * d20 - d01 * d21) / den;
		w = (d00 * d21 - d01 * d20) / den;
		u = 1f - v - w;
	}
}
