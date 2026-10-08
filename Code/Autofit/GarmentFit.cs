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

	/// <summary>How much of a body triangle this bone drives itself. 0..1.</summary>
	public float Weight( int tri, int bone ) => triWeights[tri].TryGetValue( bone, out float w ) ? w : 0;

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
	const int PushRounds = 6;                     // rounds of spreading the collision pushes and pushing again
	const float AffineRidge = 0.05f;              // how hard the fit on a stiff bone holds on to its shape, as a share of how its skin spreads
	const float LayerGap = 0.012f * Units.Metre;  // room a loose garment keeps over the skin, for whatever is worn under it
	const float RoomStep = 0.001f * Units.Metre;  // a garment surface closer to a vertex than this is the vertex's own
	const float GapCap = 0.05f * Units.Metre;    // gaps up to this size are restored, so a jacket stays above the shirt under it
	const float MaxPush = 0.05f * Units.Metre;   // the collision pass is a touch-up; anything further is a misread
	const float Contact = 0.03f * Units.Metre;   // a garment this close to the skin counts as resting on it
	const float CoverReach = 0.02f * Units.Metre; // a skin vertex further out of the cloth than this is a bad match, not a bump
	const float Hidden = 0.005f * Units.Metre;   // further under the stock skin than this was never meant to be seen
	const float ClearGap = 0.004f * Units.Metre;  // what cloth that used to hide the skin is held above it once the skin shows
	const float ClearDepth = 0.015f * Units.Metre; // how far under the skin such cloth may have dipped (a crease) and still be lifted
	const float OnSkinCopy = 0.002f * Units.Metre; // a draw call this close to the garment's skin copy is a layer painted on it
	const float TuckGap = 0.005f * Units.Metre;    // how far under a hat's surface hair that came through it is put back
	const float TuckDepth = 0.002f * Units.Metre;  // how far under the skin the way out to a bit of hair starts; deeper, it would cross the edge of a hat sunk into the head
	const float TuckFar = 0.4f * Units.Metre;      // hair further than this from the body can't be under a hat
	const float Touching = 0.002f * Units.Metre; // rigid pieces this close together are parts of one object
	const int LoosePieces = 16;                  // more rigid pieces than this in one heap is hair or the like, not an object
	const float LooseLength = 0.15f * Units.Metre; // a loose piece longer than this bends like cloth rather than moving as one

	// Points inside each triangle to test: centre, edge midpoints and one near each corner.
	static readonly float[][] Samples =
	{
		new[] { 1 / 3f, 1 / 3f, 1 / 3f }, new[] { 0.5f, 0.5f, 0f }, new[] { 0f, 0.5f, 0.5f }, new[] { 0.5f, 0f, 0.5f },
		new[] { 0.7f, 0.15f, 0.15f }, new[] { 0.15f, 0.7f, 0.15f }, new[] { 0.15f, 0.15f, 0.7f },
	};

	/// <param name="boneIndex">Four bone slots per garment vertex, as indices into the stock body's bones (-1 for none).</param>
	/// <param name="skinMove">What <see cref="BodyMap"/> worked out for this pair of bodies.</param>
	/// <param name="skinFound"><see cref="BodyMap.Found"/>, or null if every vertex was found.</param>
	/// <summary>
	/// Moves a rough version of a garment the way its detailed version moved: each vertex
	/// takes the displacement of the nearest point of the detailed one. The rough LODs of a
	/// garment are fitted on their own first, to be worn sooner, then brought in line with the
	/// detailed fit this way so that they don't pop against it.
	/// </summary>
	/// <param name="detailedOld">The detailed version as it was.</param>
	/// <param name="detailedNew">The detailed version as fitted.</param>
	/// <summary>
	/// Which of a garment's draw calls are its copy of the body's skin. The ones in a skin
	/// material, and the layers painted over those (a cap of chest hair a millimetre above
	/// the skin copy, a tattoo): they were made to sit on the garment's skin, not the body's.
	/// </summary>
	/// <param name="isSkinMaterial">Whether a material is one of the body's skin materials.</param>
	public static bool[] SkinCopies( SkinnedGeometry garment, Func<string, bool> isSkinMaterial )
	{
		var result = new bool[garment.Draws.Count];
		var skinTris = new List<int>();
		for ( int d = 0; d < result.Length; d++ )
		{
			result[d] = isSkinMaterial( garment.Draws[d].Material );
			if ( !result[d] ) continue;
			for ( int i = garment.Draws[d].First; i < garment.Draws[d].First + garment.Draws[d].Count; i++ ) skinTris.Add( garment.Indices[i] );
		}
		if ( skinTris.Count == 0 ) return result;

		var copy = new TriMesh( garment.Positions, skinTris.ToArray() );
		for ( int d = 0; d < result.Length; d++ )
		{
			if ( result[d] ) continue;
			var (first, count, _) = garment.Draws[d];
			int onCopy = 0, seen = 0;
			var done = new HashSet<int>();
			for ( int i = first; i < first + count; i++ )
			{
				int v = garment.Indices[i];
				if ( !done.Add( v ) ) continue;
				seen++;
				if ( copy.Nearest( garment.Positions[v], out _, out _ ) < OnSkinCopy ) onCopy++;
			}
			result[d] = seen > 0 && onCopy >= 0.9f * seen;
		}

		return result;
	}

	/// <returns>Per vertex, how far to move it. Goes into <see cref="Fit"/> as the given move.</returns>
	public static Vec3[] Follow( Vec3[] verts, Vec3[] detailedOld, int[] detailedTris, Vec3[] detailedNew )
	{
		var surface = new TriMesh( detailedOld, detailedTris );
		var result = new Vec3[verts.Length];
		for ( int i = 0; i < verts.Length; i++ )
		{
			surface.Nearest( verts[i], out var point, out int tri );
			int a = detailedTris[tri * 3], b = detailedTris[tri * 3 + 1], c = detailedTris[tri * 3 + 2];
			Barycentric( point, detailedOld[a], detailedOld[b], detailedOld[c], out float u, out float v, out float w );
			result[i] = (detailedNew[a] - detailedOld[a]) * u + (detailedNew[b] - detailedOld[b]) * v + (detailedNew[c] - detailedOld[c]) * w;
		}

		return result;
	}

	/// <param name="clearSkin">
	/// Keep every bit of the cloth above the skin, even where it sat level with it or dipped
	/// under. For a garment that hid the body part under it and so was never made to clear it:
	/// a shirt whose creases dent into the arms. Once the body part is shown after all, it
	/// would come through.
	/// </param>
	/// <param name="givenMove">
	/// Where each vertex goes, worked out elsewhere (a rough level following a detailed one).
	/// Only the collision passes run then: the given moves put the cloth in the right place,
	/// but a coarse triangle still cuts through a bump the detailed level goes around.
	/// </param>
	/// <param name="under">A garment this one is worn under (hair under a hat), in the same stock pose. Whatever comes through it is put back under.</param>
	/// <param name="eyes">Where the eyes are, on the stock body and on this one (in the stock pose). Glasses go by them.</param>
	/// <param name="solidOut">If given, set for the vertices of solid objects (glasses, a sword), which were moved as a whole and should be taken back to the body as a whole.</param>
	/// <param name="stiff">Per stock bone, the part it belongs to that changes as a whole from body to body (a foot: its ankle bone), -1 for none. Garment on such a part follows one smooth change of it instead of every point of the skin.</param>
	public static Vec3[] Fit( Vec3[] verts, int[] tris, int[] boneIndex, float[] boneWeight, TriMesh old, TriMesh other, Vec3[] skinMove, SkinByBone byBone, bool[] skinFound = null, bool clearSkin = false, Vec3[] givenMove = null, TriMesh under = null, bool[] solidOut = null, (Vec3 Stock, Vec3 Body)[] eyes = null, int[] stiff = null )
	{
		float hidden = clearSkin ? ClearDepth : Hidden;
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
			if ( givenMove != null ) move[i] = givenMove[src];
		}

		if ( stiff != null && givenMove == null ) Stiffen( pts, firstOf, boneIndex, boneWeight, move, old, skinMove, byBone, stiff );

		// Loose cloth hanging between two limbs flips between them from vertex to vertex.
		// A little smoothing evens that out without washing away real detail.
		var edges = MeshTools.Edges( tris, weld );
		var all = new bool[n];
		Array.Fill( all, true );
		if ( givenMove == null ) MeshTools.Relax( move, all, edges, 2, 0.5f );

		// A loose garment doesn't move out when the body under it grows: the growth takes up its
		// room first (a bust in a jacket made for a flat chest), and only what is left over pushes
		// the cloth out. Without this the cloth keeps its whole gap over the new bump, all round
		// it, and the bump looks that much bigger.
		var wanted = MeshTools.Copy( gap0 );
		if ( givenMove == null ) TakeUpRoom( verts, tris, pts, move, gap0, away, wanted );

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
			if ( oldGap < -hidden ) return false;
			float gap = other.SignedGap( p, out var s );
			float want = MathF.Min( oldGap, GapCap );
			if ( clearSkin ) want = MathF.Max( want, ClearGap );
			float need = want - gap;
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

		var bends = new HashSet<int>();   // pieces that go the way of cloth after all
		var loose = new List<int>();      // vertices of the cards in a heap: hair
		var isSolid = new bool[n];
		foreach ( var parts in solids.Values )
		{
			// Dozens of pieces in a heap are not one object. That is hair cards, feathers or
			// scales, which lie loosely on the body and follow it one by one, the way cloth
			// would. A long one (a dreadlock from the crown down to the chest) doesn't even
			// move as one: moved whole, it takes the average of what the skin did along its
			// length and comes off the head when only the chest changed. It bends instead.
			if ( parts.Count > LoosePieces )
			{
				foreach ( int r in parts )
				{
					if ( (hi[r] - lo[r]).Length() > LooseLength ) bends.Add( piece[rigid[r][0]] );
					else loose.AddRange( rigid[r] );
				}
				continue;
			}

			var whole = new List<int>();
			foreach ( int r in parts ) whole.AddRange( rigid[r] );
			FitRigid( whole, pts, move, gap0, away, fitted, Push, false );
			if ( eyes != null && OnTheEyes( whole, pts, eyes ) )
			{
				PutOnTheEyes( whole, pts, fitted, eyes );
				PushOut( whole, fitted, gap0, away, Push, false );
			}
			foreach ( int i in whole ) isSolid[i] = true;
		}

		if ( loose.Count > 0 ) FitLoose( loose, pts, move, wanted, away, fitted, Push );

		var clothTris = new List<int>();
		// Each piece of cloth is worked on its own, but pieces share no edges, so the smoothing
		// in the middle is done for all of them at once: one pass over the edges instead of one
		// per piece, and no garment-sized scratch arrays per piece (hair is thousands of pieces).
		var cloth = new List<(int Root, List<int> Idx)>();
		foreach ( var (root, idx) in members )
			if ( bends.Contains( root ) || !IsRigid( idx, firstOf, boneIndex, boneWeight ) ) cloth.Add( (root, idx) );

		foreach ( var (root, idx) in cloth )
		{
			// Interpolation and smoothing can leave a vertex a little under the new skin.
			for ( int pass = 0; pass < 2; pass++ )
			{
				int moved = 0;
				foreach ( int i in idx )
				{
					if ( !Push( fitted[i], wanted[i], away[i], out var p ) ) continue;
					fitted[i] += p;
					moved++;
				}
				if ( moved == 0 ) break;
			}
		}

		// Each push is decided for one vertex at a time, which leaves spikes where a vertex
		// was pushed and its neighbours weren't (armpits, mostly), and lumps where the new body
		// pushed out a whole patch of cloth (a bust bigger than the stock one). Spread the pushes
		// over the neighbours and push again, a few times over, so the cloth goes over what is
		// under it in one smooth sweep. A push goes last: smoothing alone would pull the top of
		// a bump back under the cloth.
		var pushField = new Vec3[n];
		var mask = new bool[n];
		foreach ( var (_, idx) in cloth )
			foreach ( int i in idx ) { pushField[i] = fitted[i] - (pts[i] + move[i]); mask[i] = true; }
		for ( int round = 0; round < PushRounds; round++ )
		{
			MeshTools.Relax( pushField, mask, edges, 2, 0.5f );
			foreach ( var (_, idx) in cloth )
				foreach ( int i in idx )
					if ( Push( pts[i] + move[i] + pushField[i], wanted[i], away[i], out var p ) ) pushField[i] += p;
		}
		foreach ( var (_, idx) in cloth )
			foreach ( int i in idx ) fitted[i] = pts[i] + move[i] + pushField[i];

		foreach ( var (root, idx) in cloth )
		{
			// The skin can still come through the middle of a triangle, typically a hard edge
			// of the body between two cloth vertices. Check a few points inside each triangle
			// and lift its corners. This goes last and is not smoothed: it is what keeps edges
			// covered.
			if ( !pieceTris.TryGetValue( root, out var mine ) ) continue;
			clothTris.AddRange( mine );

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
					if ( gap0[mine[t]] < -hidden || gap0[mine[t + 1]] < -hidden || gap0[mine[t + 2]] < -hidden ) continue;
					for ( int k = 0; k < Samples.Length; k++ )
					{
						var bw = Samples[k];
						float room = wanted[mine[t]] * bw[0] + wanted[mine[t + 1]] * bw[1] + wanted[mine[t + 2]] * bw[2];
						if ( !Push( Blend( fitted, mine, t, bw ), MathF.Min( before[t / 3 * Samples.Length + k], room ), Blend( away, mine, t, bw ), out var p ) ) continue;
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

		CoverSkin( pts, fitted, gap0, clothTris, old, skinMove, skinFound, clearSkin );
		if ( under != null ) Tuck( fitted, tris, weld, edges, under, other );

		var result = new Vec3[verts.Length];
		for ( int i = 0; i < verts.Length; i++ ) result[i] = fitted[weld[i]];
		if ( solidOut != null )
			for ( int i = 0; i < verts.Length; i++ ) solidOut[i] = isSolid[weld[i]];
		return result;
	}

	/// <summary>
	/// A heap of cards (hair) goes over the body as one soft mass. Each card fitted on its own
	/// took the skin's move at its roots and kept its own tilt, so where the head's shape
	/// changed (a brow further out, a flatter face) neighbouring cards swung different ways:
	/// gaps in the fringe, cards through a cheek. Here every card vertex moves with the skin
	/// nearest to it, and the moves and the pushes off the body are averaged with whatever
	/// other cards lie within reach, so the heap bends smoothly and no card leaves its
	/// neighbours.
	/// </summary>
	static void FitLoose( List<int> loose, Vec3[] pts, Vec3[] move, float[] wanted, Vec3[] away, Vec3[] fitted, PushTest push )
	{
		// Neighbours within reach, by a grid of cells one reach wide.
		var cells = new Dictionary<(int, int, int), List<int>>();
		(int, int, int) CellOf( Vec3 p ) => ((int)MathF.Floor( p.X / LooseReach ), (int)MathF.Floor( p.Y / LooseReach ), (int)MathF.Floor( p.Z / LooseReach ));
		foreach ( int i in loose )
		{
			var key = CellOf( pts[i] );
			if ( !cells.TryGetValue( key, out var list ) ) cells[key] = list = new List<int>();
			list.Add( i );
		}
		var near = new List<int>[pts.Length];
		var nearWeight = new List<float>[pts.Length];
		float sigma2 = 2f * (LooseReach * 0.5f) * (LooseReach * 0.5f);
		foreach ( int i in loose )
		{
			var (cx, cy, cz) = CellOf( pts[i] );
			near[i] = new List<int>();
			nearWeight[i] = new List<float>();
			for ( int dx = -1; dx <= 1; dx++ )
				for ( int dy = -1; dy <= 1; dy++ )
					for ( int dz = -1; dz <= 1; dz++ )
					{
						if ( !cells.TryGetValue( (cx + dx, cy + dy, cz + dz), out var list ) ) continue;
						foreach ( int j in list )
						{
							float d2 = (pts[j] - pts[i]).LengthSquared();
							if ( d2 > LooseReach * LooseReach ) continue;
							near[i].Add( j );
							nearWeight[i].Add( MathF.Exp( -d2 / sigma2 ) );
						}
					}
		}

		void Blur( Vec3[] field, Vec3[] into )
		{
			foreach ( int i in loose )
			{
				var sum = Vec3.Zero;
				float total = 0;
				for ( int k = 0; k < near[i].Count; k++ ) { sum += field[near[i][k]] * nearWeight[i][k]; total += nearWeight[i][k]; }
				into[i] = total > 0 ? sum / total : field[i];
			}
		}

		var smooth = new Vec3[pts.Length];
		var scratch = new Vec3[pts.Length];
		foreach ( int i in loose ) smooth[i] = move[i];
		for ( int pass = 0; pass < LoosePasses; pass++ )
		{
			Blur( smooth, scratch );
			foreach ( int i in loose ) smooth[i] = scratch[i];
		}

		var pushField = new Vec3[pts.Length];
		for ( int round = 0; round < PushRounds; round++ )
		{
			Blur( pushField, scratch );
			foreach ( int i in loose ) pushField[i] = scratch[i];
			foreach ( int i in loose )
				if ( push( pts[i] + smooth[i] + pushField[i], wanted[i], away[i], out var p ) ) pushField[i] += p;
		}
		foreach ( int i in loose ) fitted[i] = pts[i] + smooth[i] + pushField[i];
	}

	const float LooseReach = 0.03f * Units.Metre;   // cards this close to each other move together
	const int LoosePasses = 2;

	/// <summary>
	/// A foot changes as a whole from one body to another: longer, wider, flatter. Followed point
	/// by point, a shoe picks up every difference between two sets of toes and comes out
	/// crumpled. On a stiff part the move is the one affine change that best carries that part's
	/// stock skin onto the new skin, smooth across the whole garment.
	/// </summary>
	static void Stiffen( Vec3[] pts, int[] firstOf, int[] boneIndex, float[] boneWeight, Vec3[] move, TriMesh old, Vec3[] skinMove, SkinByBone byBone, int[] stiff )
	{
		var fits = new Dictionary<int, Affine>();
		for ( int i = 0; i < pts.Length; i++ )
		{
			int src = firstOf[i];
			float total = 0, onStiff = 0;
			var sum = Vec3.Zero;
			for ( int j = 0; j < 4; j++ )
			{
				int bone = boneIndex[src * 4 + j];
				float w = boneWeight[src * 4 + j];
				if ( bone < 0 || w <= 0 ) continue;
				total += w;
				int group = bone < stiff.Length ? stiff[bone] : -1;
				if ( group < 0 ) continue;
				if ( !fits.TryGetValue( group, out var fit ) ) fits[group] = fit = Affine.Fit( old, skinMove, byBone, stiff, group );
				if ( !fit.Valid ) continue;
				sum += (fit.Apply( pts[i] ) - pts[i]) * w;
				onStiff += w;
			}
			if ( onStiff <= 0 || total <= 0 ) continue;
			float share = onStiff / total;
			move[i] = move[i] * (1 - share) + sum / onStiff * share;
		}
	}

	// x -> Linear * (x - From) + To
	struct Affine
	{
		public bool Valid;
		public Vec3 From, To;
		public float[] Linear;   // 3x3, row by row

		public Vec3 Apply( Vec3 p )
		{
			var d = p - From;
			return To + new Vec3( Linear[0] * d.X + Linear[1] * d.Y + Linear[2] * d.Z, Linear[3] * d.X + Linear[4] * d.Y + Linear[5] * d.Z, Linear[6] * d.X + Linear[7] * d.Y + Linear[8] * d.Z );
		}

		// Least squares over the skin the part's bones drive most, held a little to its shape so
		// a thin patch of skin can't fold the garment flat.
		public static Affine Fit( TriMesh skin, Vec3[] skinMove, SkinByBone byBone, int[] stiff, int group )
		{
			var bones = new List<int>();
			for ( int b = 0; b < stiff.Length; b++ )
				if ( stiff[b] == group ) bones.Add( b );
			var used = new HashSet<int>();
			for ( int t = 0; t < skin.TriCount; t++ )
			{
				float share = 0;
				foreach ( int b in bones ) share += byBone.Weight( t, b );
				if ( share >= 0.5f )
					for ( int k = 0; k < 3; k++ ) used.Add( skin.Tris[t * 3 + k] );
			}
			var fit = new Affine();
			if ( used.Count < 8 ) return fit;

			Vec3 from = Vec3.Zero, to = Vec3.Zero;
			foreach ( int v in used ) { from += skin.Verts[v]; to += skin.Verts[v] + skinMove[v]; }
			from /= used.Count;
			to /= used.Count;

			var spread = new double[9];   // sum of d d^T over the stock skin
			var carried = new double[9];  // sum of e d^T, e where d went
			foreach ( int v in used )
			{
				var d = skin.Verts[v] - from;
				var e = skin.Verts[v] + skinMove[v] - to;
				for ( int r = 0; r < 3; r++ )
					for ( int c = 0; c < 3; c++ )
					{
						spread[r * 3 + c] += Component( d, r ) * Component( d, c );
						carried[r * 3 + c] += Component( e, r ) * Component( d, c );
					}
			}
			double ridge = (spread[0] + spread[4] + spread[8]) * AffineRidge;
			for ( int k = 0; k < 3; k++ ) { spread[k * 4] += ridge; carried[k * 4] += ridge; }

			var inverse = Invert( spread );
			if ( inverse == null ) return fit;
			fit.Linear = new float[9];
			for ( int r = 0; r < 3; r++ )
				for ( int c = 0; c < 3; c++ )
					fit.Linear[r * 3 + c] = (float)(carried[r * 3] * inverse[c] + carried[r * 3 + 1] * inverse[3 + c] + carried[r * 3 + 2] * inverse[6 + c]);
			fit.From = from;
			fit.To = to;
			fit.Valid = true;
			return fit;
		}

		static float Component( Vec3 v, int axis ) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

		static double[] Invert( double[] m )
		{
			double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
			double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
			if ( Math.Abs( det ) < 1e-12 ) return null;
			double s = 1 / det;
			return new[] { (e * i - f * h) * s, (c * h - b * i) * s, (b * f - c * e) * s, (f * g - d * i) * s, (a * i - c * g) * s, (c * d - a * f) * s, (d * h - e * g) * s, (b * g - a * h) * s, (a * e - b * d) * s };
		}
	}

	/// <summary>
	/// The room a garment keeps over the skin once the body grows into it: enough for whatever
	/// is worn under it, measured from the garment's inside layer, so a thick garment (a padded
	/// apron) keeps its thickness instead of its outside sinking into its inside. What the
	/// body's outward move takes up of the room beyond that is taken off the cloth's move, and
	/// the collision pass holds the cloth no further out than the room it has left.
	/// </summary>
	static void TakeUpRoom( Vec3[] verts, int[] tris, Vec3[] pts, Vec3[] move, float[] gap0, Vec3[] away, float[] wanted )
	{
		TriMesh own = null;
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int i = 0; i < pts.Length; i++ )
		{
			if ( gap0[i] <= LayerGap ) continue;

			// The furthest in of the garment's own surfaces between this vertex and the skin.
			own ??= new TriMesh( verts, tris );
			own.Crossings( pts[i] - away[i] * RoomStep, away[i] * -1f, gap0[i], hits );
			float inside = 0;
			foreach ( var hit in hits )
				if ( hit.T > RoomStep ) inside = MathF.Max( inside, hit.T + RoomStep );

			float room = gap0[i] - inside - LayerGap;
			if ( room <= 0 ) continue;
			wanted[i] = gap0[i] - room;
			float grow = Vec3.Dot( move[i], away[i] );
			if ( grow > 0 ) move[i] -= away[i] * MathF.Min( grow, room );
		}
	}

	/// <summary>
	/// Hair under a hat. The engine won't wear the two together because hair comes up through
	/// a hat, so this puts whatever comes through back under it. Hair grows out of the head, so
	/// a bit of hair is through the hat when the way to it from inside the head crosses the hat:
	/// it is pulled back along that way to just short of the hat. Hair deep inside the crown and
	/// hair beside the hat (past its edge, below the brim) have nothing in the way and are left
	/// alone. Going by the hat's own sides instead fails on a tall or floppy hat, whose middle
	/// is far from the head, and on a brim, whose top and underside are both its outside.
	/// </summary>
	static void Tuck( Vec3[] fitted, int[] tris, int[] weld, List<(int A, int B)> edges, TriMesh under, TriMesh body )
	{
		var start = new Vec3[fitted.Length];
		var known = new bool[fitted.Length];
		var own = new Vec3[fitted.Length];
		var through = new bool[fitted.Length];
		var push = new Vec3[fitted.Length];
		var all = new bool[fitted.Length];
		Array.Fill( all, true );
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int pass = 0; pass < 6; pass++ )
		{
			Array.Clear( push );
			int moved = 0;

			// Where each corner's way starts, and whether the corner itself is through. Finding
			// the start is the costly part, so it is found for the corners only.
			for ( int i = 0; i < fitted.Length; i++ )
			{
				known[i] = InsideHead( fitted[i], body, out start[i] );
				through[i] = false;
				if ( !known[i] || !Through( fitted[i], start[i], under, hits, out var way, out float stop ) ) continue;
				through[i] = true;
				own[i] = start[i] + way * stop - fitted[i];
			}

			// The middle of a card can come through where the hat folds inwards between its
			// corners. A point inside a triangle starts its way from the blend of its corners' starts.
			for ( int t = 0; t < tris.Length; t += 3 )
			{
				int a = weld[tris[t]], b = weld[tris[t + 1]], c = weld[tris[t + 2]];
				if ( !known[a] || !known[b] || !known[c] ) continue;
				foreach ( var bw in Between )
				{
					var p = fitted[a] * bw[0] + fitted[b] * bw[1] + fitted[c] * bw[2];
					var from = start[a] * bw[0] + start[b] * bw[1] + start[c] * bw[2];
					if ( !Through( p, from, under, hits, out var way, out float stop ) ) continue;
					var d = from + way * stop - p;
					if ( d.LengthSquared() > push[a].LengthSquared() ) push[a] = d;
					if ( d.LengthSquared() > push[b].LengthSquared() ) push[b] = d;
					if ( d.LengthSquared() > push[c].LengthSquared() ) push[c] = d;
					moved++;
				}
			}

			// A corner that is through itself goes where its own way says.
			for ( int i = 0; i < fitted.Length; i++ )
			{
				if ( !through[i] ) continue;
				push[i] = own[i];
				moved++;
			}

			if ( moved == 0 ) break;
			// Smoothed, so a card bends rather than kinks; the last pass goes in full, so
			// nothing stubborn is left sticking out.
			if ( pass < 5 ) MeshTools.Relax( push, all, edges, 1, 0.5f );
			for ( int i = 0; i < fitted.Length; i++ ) fitted[i] += push[i];
		}
	}

	// Points inside a triangle to test, its corners being tested on their own: the centre and
	// the edge midpoints.
	static readonly float[][] Between =
	{
		new[] { 1 / 3f, 1 / 3f, 1 / 3f }, new[] { 0.5f, 0.5f, 0f }, new[] { 0f, 0.5f, 0.5f }, new[] { 0.5f, 0f, 0.5f },
	};

	/// <summary>
	/// Whether a point of hair is through a hat or closer than <see cref="TuckGap"/> under it.
	/// If so, <paramref name="from"/> + <paramref name="way"/> * <paramref name="stop"/> is
	/// where it belongs.
	/// </summary>
	public static bool PastHat( Vec3 p, TriMesh under, TriMesh body, List<(float T, bool Leaving, int Tri)> hits, out Vec3 from, out Vec3 way, out float stop )
	{
		way = Vec3.Zero;
		stop = 0;
		return InsideHead( p, body, out from ) && Through( p, from, under, hits, out way, out stop );
	}

	// A point just under the skin nearest to a bit of hair: where the way out to it starts.
	static bool InsideHead( Vec3 p, TriMesh body, out Vec3 from )
	{
		from = Vec3.Zero;
		if ( body.Nearest( p, out var skin, out int tri ) > TuckFar || tri < 0 ) return false;
		var n = body.TriNormals[tri];
		float area = n.Length();
		if ( area < 1e-12f ) return false;
		from = skin - n / area * TuckDepth;
		return true;
	}

	// Whether the way from inside the head to a point crosses the hat, and where to stop short of it.
	static bool Through( Vec3 p, Vec3 from, TriMesh under, List<(float T, bool Leaving, int Tri)> hits, out Vec3 way, out float stop )
	{
		stop = 0;
		way = p - from;
		float length = way.Length();
		if ( length < 1e-6f * Units.Metre ) return false;
		way /= length;
		under.Crossings( from, way, length + TuckGap, hits );
		if ( hits.Count == 0 ) return false;
		stop = MathF.Max( 0, hits[0].T - TuckGap );
		return stop < length;
	}

	/// <summary>
	/// The passes above keep the cloth out of the skin by testing points of the cloth. That
	/// misses the opposite case: a vertex of the skin coming up between those points, the tip
	/// of a bump the cloth lies flat across. So this goes over the skin instead. Every skin
	/// vertex that sat under the cloth on the stock body has to be at least as far under it on
	/// the new one, and the triangle above it is lifted until it is.
	/// </summary>
	static void CoverSkin( Vec3[] pts, Vec3[] fitted, float[] gap0, List<int> clothTris, TriMesh old, Vec3[] skinMove, bool[] skinFound, bool clearSkin )
	{
		if ( clothTris.Count == 0 ) return;
		var cloth = new TriMesh( pts, clothTris.ToArray() );

		// Which way is out of the skin at each of its vertices, before and after.
		var outBefore = new Vec3[old.Verts.Length];
		var outAfter = new Vec3[old.Verts.Length];
		for ( int t = 0; t < old.TriCount; t++ )
		{
			int a = old.Tris[t * 3], b = old.Tris[t * 3 + 1], c = old.Tris[t * 3 + 2];
			Vec3 raw = Vec3.Cross( old.Verts[b] - old.Verts[a], old.Verts[c] - old.Verts[a] );
			Vec3 moved = Vec3.Cross( old.Verts[b] + skinMove[b] - old.Verts[a] - skinMove[a], old.Verts[c] + skinMove[c] - old.Verts[a] - skinMove[a] );
			if ( Vec3.Dot( raw, old.TriNormals[t] ) < 0 ) moved = -moved;
			outBefore[a] += old.TriNormals[t]; outBefore[b] += old.TriNormals[t]; outBefore[c] += old.TriNormals[t];
			outAfter[a] += moved; outAfter[b] += moved; outAfter[c] += moved;
		}

		// Skin vertices with cloth right above them, and how far above.
		var under = new List<(int Skin, int Tri, float U, float V, float W, float Gap)>();
		for ( int v = 0; v < old.Verts.Length; v++ )
		{
			if ( skinFound != null && !skinFound[v] ) continue;   // where this vertex went is a guess

			float before = outBefore[v].Length(), after = outAfter[v].Length();
			if ( before < 1e-12f || after < 1e-12f ) continue;
			outBefore[v] /= before;
			outAfter[v] /= after;

			float distance = cloth.Nearest( old.Verts[v], out var above, out int tri );
			if ( tri < 0 || distance > GapCap ) continue;

			// Straight above, not off to the side: the nearest cloth to skin just past a hem is
			// the hem's edge, and that skin was never covered. Skin that came through the cloth
			// on the stock body (cloth straight below it) only counts when the skin is to be
			// cleared, and then it wants the clearing gap like everything else.
			float gap = Vec3.Dot( above - old.Verts[v], outBefore[v] );
			if ( MathF.Abs( gap ) < 0.7f * distance ) continue;
			if ( gap <= 0 && (!clearSkin || gap < -ClearDepth) ) continue;
			if ( clearSkin ) gap = MathF.Max( gap, ClearGap );

			int a = clothTris[tri * 3], b = clothTris[tri * 3 + 1], c = clothTris[tri * 3 + 2];
			float hidden = clearSkin ? ClearDepth : Hidden;
			if ( gap0[a] < -hidden || gap0[b] < -hidden || gap0[c] < -hidden ) continue;
			Barycentric( above, pts[a], pts[b], pts[c], out float bu, out float bv, out float bw );
			under.Add( (v, tri, bu, bv, bw, gap) );
		}

		var lift = new Dictionary<int, Vec3>();
		for ( int pass = 0; pass < 4; pass++ )
		{
			lift.Clear();
			foreach ( var (skin, tri, bu, bv, bw, gap) in under )
			{
				int a = clothTris[tri * 3], b = clothTris[tri * 3 + 1], c = clothTris[tri * 3 + 2];
				Vec3 above = fitted[a] * bu + fitted[b] * bv + fitted[c] * bw;
				float need = gap - Vec3.Dot( above - (old.Verts[skin] + skinMove[skin]), outAfter[skin] );
				if ( need <= 1e-4f * Units.Metre || need > (clearSkin ? ClearDepth + ClearGap : CoverReach) ) continue;

				Vec3 push = outAfter[skin] * need;
				if ( bu > 0.05f && (!lift.TryGetValue( a, out var have ) || push.LengthSquared() > have.LengthSquared()) ) lift[a] = push;
				if ( bv > 0.05f && (!lift.TryGetValue( b, out have ) || push.LengthSquared() > have.LengthSquared()) ) lift[b] = push;
				if ( bw > 0.05f && (!lift.TryGetValue( c, out have ) || push.LengthSquared() > have.LengthSquared()) ) lift[c] = push;
			}

			if ( lift.Count == 0 ) break;
			foreach ( var (vertex, push) in lift ) fitted[vertex] += push;
		}
	}

	/// <summary>
	/// A garment's vertices index the garment's own skeleton. Returns the same four slots per
	/// vertex as indices into the body's bones. A bone the body doesn't have (older garments
	/// carry a few) stands in for the nearest ancestor the body does have.
	/// </summary>
	/// <summary>Per garment bone, the body's bone of the same name, or the nearest ancestor the body has. -1 for none.</summary>
	public static int[] BoneMap( SkinnedGeometry garment, SkinnedGeometry body )
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

		return map;
	}

	public static int[] BonesOnBody( SkinnedGeometry garment, SkinnedGeometry body )
	{
		var map = BoneMap( garment, body );
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

		PushOut( idx, fitted, gap0, away, push, keepGaps );
	}

	const float EyewearHeight = 0.08f * Units.Metre;   // a solid across both eyes no taller than this is eyewear, not a helmet

	/// <summary>
	/// A solid object worn across the eyes: it spans both of them side to side, the eyes are
	/// within its height, and it isn't tall. Glasses, goggles, a visor band.
	/// </summary>
	static bool OnTheEyes( List<int> idx, Vec3[] pts, (Vec3 Stock, Vec3 Body)[] eyes )
	{
		if ( eyes.Length != 2 ) return false;
		float minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
		foreach ( int i in idx )
		{
			minY = MathF.Min( minY, pts[i].Y ); maxY = MathF.Max( maxY, pts[i].Y );
			minZ = MathF.Min( minZ, pts[i].Z ); maxZ = MathF.Max( maxZ, pts[i].Z );
		}
		float eyeLo = MathF.Min( eyes[0].Stock.Y, eyes[1].Stock.Y ), eyeHi = MathF.Max( eyes[0].Stock.Y, eyes[1].Stock.Y );
		float eyeZ = 0.5f * (eyes[0].Stock.Z + eyes[1].Stock.Z);
		return minY <= eyeLo && maxY >= eyeHi && minZ <= eyeZ && maxZ >= eyeZ && maxZ - minZ <= EyewearHeight;
	}

	/// <summary>
	/// Moves eyewear up or down and sideways so it sits on this body's eyes the way it sat on
	/// the stock body's. The skin it rests on (nose bridge, temples) doesn't say where the eyes
	/// are; on a face with eyes set lower or higher the lenses would end up on the brows.
	/// How far in front of the face it sits is left to the fit and the push out of the skin.
	/// </summary>
	static void PutOnTheEyes( List<int> idx, Vec3[] pts, Vec3[] fitted, (Vec3 Stock, Vec3 Body)[] eyes )
	{
		Vec3 stockMid = 0.5f * (eyes[0].Stock + eyes[1].Stock), bodyMid = 0.5f * (eyes[0].Body + eyes[1].Body);
		float apart = (eyes[1].Stock - eyes[0].Stock).Length();
		float ratio = apart > 1e-4f * Units.Metre ? (eyes[1].Body - eyes[0].Body).Length() / apart : 1f;
		var made = Vec3.Zero;
		var now = Vec3.Zero;
		foreach ( int i in idx ) { made += pts[i]; now += fitted[i]; }
		made /= idx.Count;
		now /= idx.Count;
		Vec3 want = bodyMid + (made - stockMid) * ratio;
		var shift = new Vec3( 0, want.Y - now.Y, want.Z - now.Z );
		foreach ( int i in idx ) fitted[i] += shift;
	}

	static void PushOut( List<int> idx, Vec3[] fitted, float[] gap0, Vec3[] away, PushTest push, bool keepGaps )
	{
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
