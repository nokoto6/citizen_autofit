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
	const float FootCover = 0.002f * Units.Metre;  // how far past the body's foot a garment on it reaches at least
	const float FootSearch = 0.25f;               // how much further than the moved skin a foot is looked for, as a share of its size
	const float FootSole = 0.35f;                 // how high up a foot its footprint is read from, as a share of its height
	const float FootTurn = 0.35f;                 // the most a foot is turned to point the body's way, radians (20 degrees)
	const float FootLong = 2f;                    // how many times longer than wide (in spread) a footprint must be to point
	const float FootOn = 0.67f;                   // how much of the body's foot, along it and across, a garment covers already to be stretched over the rest (a heeled boot on a flat foot 0.75, trousers 0.6 at most)
	const float AffineRidge = 0.05f;              // how hard the fit on a stiff bone holds on to its shape, as a share of how its skin spreads
	const float LayerGap = 0.012f * Units.Metre;  // room a loose garment keeps over the skin, for whatever is worn under it
	const float RoomStep = 0.001f * Units.Metre;  // a garment surface closer to a vertex than this is the vertex's own
	const float GapCap = 0.05f * Units.Metre;    // gaps up to this size are restored, so a jacket stays above the shirt under it
	const float MaxPush = 0.05f * Units.Metre;   // the collision pass is a touch-up; anything further is a misread
	const float Contact = 0.03f * Units.Metre;   // a garment this close to the skin counts as resting on it
	const float CoverReach = 0.02f * Units.Metre; // a skin vertex further out of the cloth than this is a bad match, not a bump
	const float CoverTurn = 0.5f;                 // so is skin whose outward direction turned further than this (cosine: 60 degrees)
	const float Lowered = 0.002f * Units.Metre;  // cloth taken further down a short neck than this was lowered (see Neck)
	const float Hidden = 0.005f * Units.Metre;   // further under the stock skin than this was never meant to be seen
	const float ClearGap = 0.004f * Units.Metre;  // what cloth that used to hide the skin is held above it once the skin shows
	const float ClearDepth = 0.015f * Units.Metre; // how far under the skin such cloth may have dipped (a crease) and still be lifted
	const float OnSkinCopy = 0.002f * Units.Metre; // a draw call this close to the garment's skin copy is a layer painted on it
	const float TuckGap = 0.005f * Units.Metre;    // how far under a hat's surface hair that came through it is put back
	const float TuckDepth = 0.002f * Units.Metre;  // how far under the skin the way out to a bit of hair starts; deeper, it would cross the edge of a hat sunk into the head
	const float TuckFar = 0.4f * Units.Metre;      // hair further than this from the body can't be under a hat
	const float Touching = 0.002f * Units.Metre; // rigid pieces this close together are parts of one object
	const float SolidClear = 0.003f * Units.Metre;  // how far under a worn solid's surface the skin it covers is kept, unless it was made to sit closer
	const float SolidPoke = 0.005f * Units.Metre;   // skin further out through a solid than this sticks out on its own (an ear through a headphone pad), it isn't the curve of the skin between the solid's vertices
	const float BulgeReach = 0.05f * Units.Metre;   // how far round a spot of skin that comes up through a solid the solid gives way
	const float ThinPart = 0.02f * Units.Metre;     // skin with more of the body this close under it is a thin part (an ear, a brow), not something to bulge a solid over
	const float StrapThinPart = 0.004f * Units.Metre; // the same for a strap on the face: the cheek it crosses has the eye under it closer than ThinPart, only the rim of an ear or a lash is thinner
	const int BulgePasses = 6;                      // rounds of finding the skin that comes through a solid and bulging it
	const int LoosePieces = 16;                  // more rigid pieces than this in one heap is hair or the like, not an object
	const float LooseLength = 0.15f * Units.Metre; // a loose piece longer than this bends like cloth rather than moving as one
	const float BeadSize = 0.06f * Units.Metre;   // a piece no bigger than this corner to corner
	const float BeadShape = 0.5f;                 // and at least this thick for its length is a bead

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
	/// <param name="thickness"><see cref="BodyMap.Thickness"/>: per stock vertex, how many times as thick the body is there. Cloth over a slimmer body is taken in by it, see <see cref="TakeUpRoom"/>.</param>
	/// <param name="clothOut">If given, set for the vertices fitted as cloth: not hair and not part of a solid object.</param>
	/// <param name="neck">The neck on the stock body and on this one (<see cref="Repose.Neck"/>). Cloth over it goes no further up this body's neck than it went up the stock body's.</param>
	/// <param name="onSkin"><see cref="BodyMap.OnSkin"/>: where each stock vertex lies on this body's skin for paint on it, the face lined up by its features. Without it paint goes by <paramref name="skinMove"/>.</param>
	/// <param name="paintOut">If given, set for the vertices of paint on the skin (a tattoo, makeup, brows). They go back to the body's rest pose with the skin under them, see <see cref="Repose.OntoSkin"/>.</param>
	/// <param name="features">The face's features on the stock body and on this one (<see cref="Repose.Features"/>). Lashes go by the lines round the eyes.</param>
	/// <param name="ears">The ears on both bodies (<see cref="Repose.Ears"/>): what is worn on one goes by it.</param>
	/// <param name="beardOut">If given, set for the vertices of a beard, which grows from the face, in pieces to be taken back to the body each as a whole (all but its long locks), see <see cref="Repose.SkinUnder"/>.</param>
	/// <param name="trunk">This body's trunk in the stock pose and in its rest pose (<see cref="Repose.Trunk"/>). Cloth on its lower part is as far up it, as a share of it, in the rest pose as in the stock pose.</param>
	public static Vec3[] Fit( Vec3[] verts, int[] tris, int[] boneIndex, float[] boneWeight, TriMesh old, TriMesh other, Vec3[] skinMove, SkinByBone byBone, bool[] skinFound = null, bool clearSkin = false, Vec3[] givenMove = null, TriMesh under = null, bool[] solidOut = null, (Vec3 Stock, Vec3 Body)[] eyes = null, int[] stiff = null, Vec3[] landmarked = null, (Vec3 Stock, Vec3 Body)[] face = null, float[] thickness = null, bool[] clothOut = null, Neck neck = null, Vec3[] onSkin = null, bool[] paintOut = null, Trunk trunk = null, List<(string Name, Vec3 Stock, Vec3 Body)> features = null, bool[] beardOut = null, List<(string Name, Vec3 Stock, Vec3 Body)> ears = null )
	{
		float hidden = clearSkin ? ClearDepth : Hidden;
		if ( beardOut != null ) Array.Clear( beardOut );
		var weld = MeshTools.Weld( verts, out int n );
		var firstOf = new int[n];
		for ( int i = verts.Length - 1; i >= 0; i-- )
			firstOf[weld[i]] = i;

		var pts = new Vec3[n];
		for ( int i = 0; i < n; i++ ) pts[i] = verts[firstOf[i]];

		Vec3 SkinMoveAt( Vec3 s, int tri ) => FieldAt( skinMove, s, tri );

		float ThicknessAt( Vec3 s, int tri )
		{
			int ia = old.Tris[tri * 3], ib = old.Tris[tri * 3 + 1], ic = old.Tris[tri * 3 + 2];
			Barycentric( s, old.Verts[ia], old.Verts[ib], old.Verts[ic], out float u, out float v, out float w );
			return thickness[ia] * u + thickness[ib] * v + thickness[ic] * w;
		}

		Vec3 FieldAt( Vec3[] field, Vec3 s, int tri )
		{
			int ia = old.Tris[tri * 3], ib = old.Tris[tri * 3 + 1], ic = old.Tris[tri * 3 + 2];
			Barycentric( s, old.Verts[ia], old.Verts[ib], old.Verts[ic], out float u, out float v, out float w );
			return field[ia] * u + field[ib] * v + field[ic] * w;
		}

		// Each vertex follows the closest stock skin, as long as that skin belongs to the bones
		// the vertex is weighted to or their neighbours. When it doesn't (the underside of a
		// sleeve is closest to the ribs), the vertex follows its own bones' skin instead.
		var move = new Vec3[n];
		var gap0 = new float[n];
		var away = new Vec3[n];   // direction from the skin to the vertex
		var slimmer = new float[n];   // how many times as thick as the stock one the body is under the vertex, at most 1
		var skinAt = new Vec3[n]; // the stock skin the vertex follows
		var skinTri = new int[n];  // and its triangle
		for ( int i = 0; i < n; i++ )
		{
			Vec3 x = pts[i];
			int src = firstOf[i];
			float distance = old.Nearest( x, out var s, out int tri );
			Vec3 from = s;
			int fromTri = tri;
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
					if ( w > best ) { best = w; from = own; fromTri = ownTri; distance = ownDistance; }
				}
				if ( total > 0 ) move[i] = sum / total;
			}

			slimmer[i] = thickness != null && fromTri >= 0 ? MathF.Min( ThicknessAt( from, fromTri ), 1f ) : 1f;
			skinAt[i] = from;
			skinTri[i] = fromTri;
			gap0[i] = old.Inside( x ) ? -distance : distance;
			if ( distance > 1e-7f * Units.Metre )
				away[i] = (x - from) / distance * (gap0[i] >= 0 ? 1f : -1f);
			if ( givenMove != null ) move[i] = givenMove[src];
		}

		// Paint on the skin (a tattoo, makeup, brows) goes onto this body's skin and nowhere else,
		// see PaintOn. Fitted as cloth it was held off the skin by every pass made for cloth.
		// Lashes grow from the lids instead, see LashesOn.
		var usedPaint = new bool[n];
		foreach ( int v in tris ) usedPaint[weld[v]] = true;
		if ( features != null && LashesOn( verts, tris, weld, pts, usedPaint, move, features, solidOut, clothOut, paintOut ) is Vec3[] lashes )
			return lashes;
		if ( Painted( usedPaint, gap0 ) || Brows( usedPaint, pts, gap0, old, eyes ) )
			return PaintOn( verts, weld, pts, usedPaint, gap0, move, old, other, skinMove, onSkin, solidOut, clothOut, paintOut );

		// Under the stock skin on purpose: caps that close a sleeve from the inside, a hem turned
		// in. That is deeper than the hidden depth or, with the skin to be cleared, a quarter of
		// the way through the body part. A crease may dip deeper than the middle of a finger and
		// still be lifted: the caps closing a glove's fingers, 8 to 10 mm down, were lifted out
		// through the fingers and took the hems along as spikes.
		var buried = new bool[n];
		var through = new List<(float T, bool Leaving, int Tri)>();
		for ( int i = 0; i < n; i++ )
		{
			buried[i] = gap0[i] < -hidden;
			if ( buried[i] || !clearSkin || gap0[i] >= -Hidden ) continue;
			old.Crossings( pts[i], away[i] * -1f, -3f * gap0[i], through );
			buried[i] = through.Count > 0 && through[0].Leaving;
		}

		if ( stiff != null && givenMove == null ) Stiffen( pts, firstOf, boneIndex, boneWeight, move, old, other, skinMove, byBone, stiff );

		// Loose cloth hanging between two limbs flips between them from vertex to vertex.
		// A little smoothing evens that out without washing away real detail.
		var edges = MeshTools.Edges( tris, weld );
		var overFace = new bool[n];
		var faceMove = givenMove == null && onSkin != null ? OnTheFaceCloth( pts, tris, weld, edges, firstOf, boneIndex, boneWeight, old, other, skinAt, skinTri, move, skinMove, onSkin, face, eyes != null && eyes.Length == 2 ? EyeMiddles( eyes, features ) : null, ears, overFace ) : null;
		var all = new bool[n];
		Array.Fill( all, true );
		if ( givenMove == null ) MeshTools.Relax( move, all, edges, 2, 0.5f );

		// A loose garment doesn't move out when the body under it grows: the growth takes up its
		// room first (a bust in a jacket made for a flat chest), and only what is left over pushes
		// the cloth out. Without this the cloth keeps its whole gap over the new bump, all round
		// it, and the bump looks that much bigger.
		var wanted = MeshTools.Copy( gap0 );
		var takeIn = new float[n];
		var roomy = new Vec3[n];
		Array.Copy( move, roomy, n );
		if ( givenMove == null ) TakeUpRoom( verts, tris, pts, move, gap0, away, wanted, slimmer, takeIn );

		var fitted = new Vec3[n];
		for ( int i = 0; i < n; i++ ) fitted[i] = pts[i] + move[i];

		// outward is the direction from the point's own skin to the point on the stock body.
		// A push that goes against it comes from some other body part the point has ended up
		// inside of (the underside of a sleeve inside a wide torso). Following it would glue
		// the cloth to the wrong part, so it is ignored.
		bool Push( Vec3 p, float oldGap, Vec3 outward, out Vec3 push ) => PushOff( p, oldGap, outward, clearSkin, out push );

		// A solid can't be held off the skin on every side at once. A hem ring made tight round a
		// glove's thumb, wanting the clearing gap all round it, was pushed by each side in turn and
		// walked 2 cm off its place. Like any solid, it only keeps from going deeper than it was made.
		bool PushSolid( Vec3 p, float oldGap, Vec3 outward, out Vec3 push ) => PushOff( p, oldGap, outward, false, out push );

		bool PushOff( Vec3 p, float oldGap, Vec3 outward, bool clear, out Vec3 push )
		{
			push = Vec3.Zero;

			// Garments have geometry that was under the skin all along: caps that close a
			// sleeve from the inside, hair roots, the tips of a pair of glasses. It has no gap
			// to keep, and dragging it out takes the visible cloth around it along.
			if ( oldGap < -hidden ) return false;
			float gap = other.SignedGap( p, out var s );
			float want = MathF.Min( oldGap, GapCap );
			if ( clear ) want = MathF.Max( want, ClearGap );
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

		// Connected pieces, of the vertices the triangles use. A garment fitted without its copy
		// of the skin still has that copy's vertices, each a piece of its own: twenty of them on
		// a glove's thumb bone made a heap of cards, and the whole glove was fitted as hair.
		var used = new bool[n];
		foreach ( int v in tris ) used[weld[v]] = true;
		var piece = Islands( n, edges );
		var members = new Dictionary<int, List<int>>();
		for ( int i = 0; i < n; i++ )
		{
			if ( !used[i] ) continue;
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

		// A garment of bits of cloth no bigger than a bead and solid objects (a chain of links with
		// a pendant) isn't cut to the body: it hangs on it, over whatever is worn under
		// it, and the room it was made with is room for those clothes. It keeps all of it, and a
		// bust that grows pushes it out instead of filling that room. Taken in like cloth, the
		// swag chain's links went from 21 mm off the human's skin to 11-15 on Aurora and into
		// her shirt, and its pendant, held back by the room, came apart from the block it hangs from.
		bool cards = false;
		foreach ( var parts in solids.Values ) cards |= parts.Count > LoosePieces;
		if ( givenMove == null && !cards && OnlySmallCloth( members, firstOf, boneIndex, boneWeight, pts ) )
		{
			Array.Copy( roomy, move, n );
			Array.Copy( gap0, wanted, n );
			Array.Clear( takeIn );
			for ( int i = 0; i < n; i++ ) fitted[i] = pts[i] + move[i];
		}

		var middles = eyes != null && eyes.Length == 2 ? EyeMiddles( eyes, features ) : eyes;
		var bends = new HashSet<int>();   // pieces that go the way of cloth after all
		var loose = new List<int>();      // vertices of the cards in a heap: hair
		bool hair = false;
		var isSolid = new bool[n];
		// Cloth of the garment, what a bead on a string is threaded on.
		var clothPts = new List<Vec3>();
		foreach ( var (_, idx) in members )
			if ( !IsRigid( idx, firstOf, boneIndex, boneWeight ) ) foreach ( int i in idx ) clothPts.Add( pts[i] );
		foreach ( var heap in solids.Values )
		{
			var parts = heap;
			// Cards hanging off a garment's cloth (the fraying threads along a denim vest's edges)
			// are fringe, carried by the cloth (see Fringe). Taken for hair, they took the whole
			// vest the way of hair, its lining with no care for the shell over it.
			if ( parts.Count > LoosePieces && Fringed( parts, rigid, pts, members, pieceTris, firstOf, boneIndex, boneWeight, clothPts.Count, eyes ) )
			{
				foreach ( int r in parts ) bends.Add( piece[rigid[r][0]] );
				continue;
			}
			// Dozens of pieces in a heap are not one object. That is hair cards, feathers or
			// scales, which lie loosely on the body and follow it one by one, the way cloth
			// would. A long one (a dreadlock from the crown down to the chest) doesn't even
			// move as one: moved whole, it takes the average of what the skin did along its
			// length and comes off the head when only the chest changed. It bends instead.
			if ( parts.Count > LoosePieces && !OneObject( parts, rigid, lo, hi, clothPts, out parts, loose ) )
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
			var solidTris = new List<int>();
			foreach ( int r in parts )
				if ( pieceTris.TryGetValue( piece[rigid[r][0]], out var mine ) ) solidTris.AddRange( mine );
			bool wraps = Wraps( whole, solidTris, pts, gap0, away );
			bool onEyes = eyes != null && OnTheEyes( whole, pts, eyes );
			bool[] marks = null;
			bool onFace = wraps && !onEyes && eyes != null && eyes.Length == 2 && face != null && face.Length == 4 && OnTheFace( solidTris, pts, old, eyes, face, out marks );
			// A hat or a helmet: round the head and resting on its crown, the skin above the eyes.
			float crown = eyes != null && eyes.Length == 2 ? MathF.Max( eyes[0].Stock.Z, eyes[1].Stock.Z ) : float.NaN;
			bool hat = wraps && !onEyes && !onFace && !float.IsNaN( crown ) && OnTheCrown( whole, solidTris, pts, gap0, crown ) && !OverTheFace( solidTris, pts, face );
			FitRigid( whole, pts, move, gap0, away, fitted, PushSolid, false, wraps, hat ? crown : float.NaN );
			if ( onEyes )
			{
				var front = PutOnTheEyes( whole, pts, fitted, middles );
				GoRound( whole, front, middles, fitted, gap0, away, other, PushSolid );
				PushOut( whole, fitted, gap0, away, PushSolid, false );
			}
			else if ( onFace )
			{
				PutOnTheFace( whole, pts, fitted, eyes, face, marks );
				var band = LetOutBand( whole, pts, move, fitted, gap0, away, PushSolid, edges, piece, eyes, face[3].Stock );
				if ( band.Count > 0 )
				{
					// The skin coming up between the strap's vertices: a strap made to lie a millimetre
					// off a smooth head lies as close over a head of bigger facets.
					var inBand = new HashSet<int>( band );
					var bandTris = new List<int>();
					for ( int t = 0; t < solidTris.Count; t += 3 )
						if ( inBand.Contains( solidTris[t] ) && inBand.Contains( solidTris[t + 1] ) && inBand.Contains( solidTris[t + 2] ) )
							bandTris.AddRange( solidTris.GetRange( t, 3 ) );
					Bulge( band, bandTris, fitted, gap0, away, other, hidden, float.MinValue, StrapThinPart );
				}
				PushOut( whole, fitted, gap0, away, PushSolid, false );
			}
			else if ( wraps && eyes != null && eyes.Length == 2 )
			{
				Bulge( whole, solidTris, fitted, gap0, away, other, hidden, 0.5f * (eyes[0].Body.Z + eyes[1].Body.Z), ThinPart );
				FaceParts( parts, rigid, lo, hi, pts, fitted, gap0, away, middles, face, PushSolid );
			}
			else if ( !wraps && ears != null && OnAnEar( whole, pts, ears, out var ear ) )
			{
				PutOnTheEar( whole, pts, fitted, gap0, other, ears, ear );
				PushOut( whole, fitted, gap0, away, PushSolid, false );
			}
			foreach ( int i in whole ) isSolid[i] = true;
		}

		if ( loose.Count > 0 )
		{
			// A heap of cards is hair, and all of it goes the hair's way: the long locks that
			// bend like cloth and whatever cap mesh sits under the cards too.
			loose.Clear();
			for ( int i = 0; i < n; i++ ) if ( !isSolid[i] && used[i] ) loose.Add( i );
			bends.Clear();
			hair = true;
			// Hair isn't painted on the face: where the skin slides along it with the eyes (a
			// landmark), the hair over it doesn't. A fringe that followed the eyes apart and
			// down came out as cards stretched sideways over the cheeks. The whole heap, the
			// long locks that bend like cloth included.
			// A beard is the other way round: it grows on the face and goes where the face goes,
			// every bit of it with the skin it grows from. Kept from sliding with the eyes, a
			// moustache made for a face with its eyes 5 cm higher sat between Aurora's eyes, and
			// moved as a whole (a lock's move) it kept the stock face's size on a smaller face,
			// with no room left under it.
			bool beard = Beard( loose, pts, old, eyes );
			if ( beard && beardOut != null )
			{
				// Its pieces that go back to the body whole: all but the long locks, which hang
				// down over the chest and bend with it (a wizard's beard, its strands 30 cm long,
				// stood 6 cm off the chest when they went back whole).
				var low = new Dictionary<int, Vec3>();
				var high = new Dictionary<int, Vec3>();
				foreach ( int i in loose )
				{
					int k = piece[i];
					var p = pts[i];
					low[k] = low.TryGetValue( k, out var l ) ? new Vec3( MathF.Min( l.X, p.X ), MathF.Min( l.Y, p.Y ), MathF.Min( l.Z, p.Z ) ) : p;
					high[k] = high.TryGetValue( k, out var h ) ? new Vec3( MathF.Max( h.X, p.X ), MathF.Max( h.Y, p.Y ), MathF.Max( h.Z, p.Z ) ) : p;
				}
				var whole = new bool[n];
				foreach ( int i in loose ) whole[i] = (high[piece[i]] - low[piece[i]]).Length() <= LooseLength;
				for ( int i = 0; i < verts.Length; i++ ) beardOut[i] = whole[weld[i]];
			}
			if ( beard && givenMove == null && face != null && face.Length == 4 )
				FaceWarp( loose, pts, move, old, other, face );
			else if ( landmarked != null && givenMove == null && !beard )
				for ( int i = 0; i < n; i++ )
					if ( !isSolid[i] && old.Nearest( pts[i], out var s, out int tri ) < SkinReach && tri >= 0 ) move[i] -= FieldAt( landmarked, s, tri );
			FitLoose( loose, pts, move, wanted, away, fitted, Push, beard ? BeardReach : LockReach );
			if ( beard && givenMove == null )
			{
				// Back onto the skin after the heap was evened out: averaged with the cards round
				// it, and pushed off with them, a card's root came away from the skin again.
				OntoTheSkin( loose, pts, fitted, gap0, piece, pieceTris, old, other, wanted, away, Push );
			}
		}

		var clothTris = new List<int>();
		// Each piece of cloth is worked on its own, but pieces share no edges, so the smoothing
		// in the middle is done for all of them at once: one pass over the edges instead of one
		// per piece, and no garment-sized scratch arrays per piece (hair is thousands of pieces).
		var cloth = new List<(int Root, List<int> Idx)>();
		foreach ( var (root, idx) in members )
			if ( !hair && (bends.Contains( root ) || !IsRigid( idx, firstOf, boneIndex, boneWeight )) ) cloth.Add( (root, idx) );

		// Cloth is taken in over a slimmer body. Hair and solid things (glasses, a hat, a
		// button) keep their size: they aren't cut to the body.
		foreach ( var (_, idx) in cloth )
		{
			foreach ( int i in idx )
			{
				if ( takeIn[i] <= 0 ) continue;
				move[i] -= away[i] * takeIn[i];
				fitted[i] = pts[i] + move[i];
				if ( wanted[i] > LayerGap ) wanted[i] = LayerGap + (wanted[i] - LayerGap) * slimmer[i];
			}
		}

		// Cloth on the lower trunk goes back to the body's rest pose as far up its trunk as it is
		// in the stock pose (see Trunk): the hem of a shirt over the hips, a waistband. Not hair.
		if ( trunk != null && givenMove == null && !hair && cloth.Count > 0 )
		{
			var rise = new Vec3[n];
			foreach ( var (_, idx) in cloth )
				foreach ( int i in idx )
				{
					float w = trunk.Zone( skinAt[i] );
					if ( w > 0 ) rise[i] = new Vec3( 0, 0, w * trunk.Shift( pts[i] + move[i], trunk.Share( pts[i] ) ) );
				}

			// Evened out over the neighbours, the cloth moves in one piece.
			MeshTools.Relax( rise, all, edges, 2, 0.5f );
			foreach ( var (_, idx) in cloth )
				foreach ( int i in idx )
				{
					move[i] += rise[i];
					fitted[i] = pts[i] + move[i];
				}
		}

		// Cloth over the neck goes no further up this body's neck than it went up the stock
		// body's (see Neck): on a shorter neck, the top of a collar comes down with it. Not hair:
		// it grows where the scalp is.
		bool[] lowered = null;
		if ( neck != null && givenMove == null && !hair && cloth.Count > 0 )
		{
			var beforeNeck = MeshTools.Copy( move );
			var hooded = new List<(int Root, List<int> Idx)>();
			var drop = new Vec3[n];
			foreach ( var (root, idx) in cloth )
			{
				// Cloth pulled over the face and down the neck (a balaclava) hangs from the head, it
				// has no top on the neck to keep down it. Lowered down a neck half as long, its neck
				// bunched up under Akira's chin and showed its inside.
				if ( OverFaceAndNeck( idx, pts, eyes ) ) { hooded.Add( (root, idx) ); continue; }
				foreach ( int i in idx )
				{
					float w = neck.Zone( skinAt[i] );
					if ( w > 0 ) drop[i] = new Vec3( 0, 0, w * neck.Lower( pts[i] + move[i], pts[i], skinAt[i] ) );
				}
			}

			// Each vertex works its drop out on its own. Evened out over its neighbours, the cloth
			// comes down in one piece.
			MeshTools.Relax( drop, all, edges, 2, 0.5f );
			lowered = new bool[n];
			for ( int i = 0; i < n; i++ ) lowered[i] = drop[i].Z < -Lowered;
			foreach ( var (_, idx) in cloth )
				foreach ( int i in idx )
				{
					move[i] += drop[i];
					fitted[i] = pts[i] + move[i];
				}

			// Then in round the neck where it now is, as far off it as off the stock skin: a
			// collar, a tube, a necklace. Not a strap hanging from a hat and crossing in front of
			// the neck under the chin: taken in to a thin neck, a skater helmet's twisted round on
			// itself.
			var inward = new Vec3[n];
			var neckHits = new List<(float T, bool Leaving, int Tri)>();
			foreach ( var (_, idx) in cloth )
			{
				var sectors = new bool[NeckSectors];
				foreach ( int i in idx )
					if ( neck.ZoneIn( skinAt[i] ) > 0 ) sectors[(int)((neck.Around( pts[i] ) + MathF.PI) / (2 * MathF.PI) * NeckSectors) % NeckSectors] = true;
				int covered = 0;
				foreach ( bool on in sectors ) if ( on ) covered++;
				if ( covered < NeckRound * NeckSectors && FromTheHead( idx, pts, eyes ) ) continue;
				foreach ( int i in idx )
				{
					float w = neck.ZoneIn( skinAt[i] + drop[i] );
					if ( w > 0 ) inward[i] = w * neck.TakeIn( fitted[i], gap0[i], other, neckHits );
				}
			}
			MeshTools.Relax( inward, all, edges, 2, 0.5f );
			foreach ( var (_, idx) in cloth )
				foreach ( int i in idx )
				{
					move[i] += inward[i];
					fitted[i] = pts[i] + move[i];
				}
			if ( hooded.Count > 0 ) Unfold( hooded, pieceTris, pts, beforeNeck, move, fitted );
		}

		foreach ( var (root, idx) in cloth )
		{
			// Interpolation and smoothing can leave a vertex a little under the new skin.
			for ( int pass = 0; pass < 2; pass++ )
			{
				int moved = 0;
				foreach ( int i in idx )
				{
					if ( buried[i] || !Push( fitted[i], wanted[i], away[i], out var p ) ) continue;
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
					if ( !buried[i] && Push( pts[i] + move[i] + pushField[i], wanted[i], away[i], out var p ) ) pushField[i] += p;
		}
		foreach ( var (_, idx) in cloth )
			foreach ( int i in idx ) fitted[i] = pts[i] + move[i] + pushField[i];

		var heldOff = HeldOffTheHead( pts, gap0, piece, eyes, face );
		var beforeLifts = MeshTools.Copy( fitted );
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
					if ( buried[mine[t]] || buried[mine[t + 1]] || buried[mine[t + 2]] ) continue;
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
		TakeBack( pts, fitted, beforeLifts, clothTris, heldOff );

		CoverSkin( pts, fitted, buried, clothTris, old, skinMove, skinFound, clearSkin, thickness, heldOff, givenMove == null ? neck : null );
		Untangle( pts, fitted, tris, weld, edges, piece, cloth, heldOff, lowered );
		if ( faceMove != null ) KeepOver( pts, fitted, clothTris, overFace, buried, other, old, faceMove );
		if ( !hair ) Carry( pts, fitted, members, pieceTris, isSolid, gap0, away, other, buried, takeIn );
		if ( !hair ) KeepUnder( fitted, clothTris, Layers( pts, clothTris, cloth, gap0, away, isSolid, buried ) );
		if ( !hair ) Fringe( pts, fitted, members, pieceTris );
		if ( under != null )
		{
			Tuck( fitted, tris, weld, edges, under, other );
			PressAtRim( fitted, under, other );
		}
		if ( !hair ) Seams( pts, fitted, tris, weld, edges, piece );
		if ( !hair ) Beads( pts, fitted, members, isSolid );

		var result = new Vec3[verts.Length];
		for ( int i = 0; i < verts.Length; i++ ) result[i] = fitted[weld[i]];
		if ( solidOut != null )
			for ( int i = 0; i < verts.Length; i++ ) solidOut[i] = isSolid[weld[i]];
		if ( clothOut != null )
		{
			var isCloth = new bool[n];
			foreach ( var (_, idx) in cloth )
				foreach ( int i in idx ) isCloth[i] = true;
			for ( int i = 0; i < verts.Length; i++ ) clothOut[i] = isCloth[weld[i]];
		}
		return result;
	}

	const float LashReach = 0.25f;   // a card rooted this close to the line over or under an eye, as a share of the eye's width, grows from the lid
	const float LashSpan = 1f;       // and no part of it further off than this
	const float LashShare = 0.9f;    // a garment this much of whose cards do is lashes

	/// <summary>
	/// Lashes: cards that grow from the edge of a lid, the line over an eye's opening or under it
	/// (<see cref="FaceMarks"/>). They stand well off the skin, so laid on the skin like paint
	/// or hair they went wherever the skin round them went: on a face whose eyes are bigger, set
	/// lower or shaped otherwise, over the brows (Aurora) or under the eyes (the humans). Each
	/// card goes with the spot of the lid it is rooted at instead: the same share of the way
	/// along this face's lid line from the inner corner to the outer one, turned with the line
	/// there, and grown or shrunk as the eye is wider or narrower. Told from brows by where they
	/// grow: brows start well above the lid line.
	/// </summary>
	/// <returns>The garment fitted, or null if it isn't lashes.</returns>
	static Vec3[] LashesOn( Vec3[] verts, int[] tris, int[] weld, Vec3[] pts, bool[] used, Vec3[] move, List<(string Name, Vec3 Stock, Vec3 Body)> features, bool[] solidOut, bool[] clothOut, bool[] paintOut )
	{
		// The lines over and under each eye, on both faces, from the inner corner to the outer one.
		var lines = new List<(Vec3[] Stock, Vec3[] Body, float Wide, float Scale)>();
		foreach ( string eye in new[] { "eyeL", "eyeR" } )
			// The marks along the line over the eye and the one under it (FaceMarks).
			foreach ( var names in new[] { new[] { ".in", ".top.1", ".top.2", ".top.3", ".out" }, new[] { ".in", ".bottom.1", ".bottom.2", ".bottom.3", ".out" } } )
			{
				var stock = new Vec3[names.Length];
				var body = new Vec3[names.Length];
				bool all = true;
				for ( int k = 0; k < names.Length && all; k++ )
				{
					all = false;
					foreach ( var (name, at, there) in features )
						if ( name == eye + names[k] ) { stock[k] = at; body[k] = there; all = true; break; }
				}
				if ( !all ) continue;
				float wide = (stock[^1] - stock[0]).Length();
				if ( wide < 1e-4f * Units.Metre ) continue;
				lines.Add( (stock, body, wide, (body[^1] - body[0]).Length() / wide) );
			}
		if ( lines.Count == 0 ) return null;

		var piece = Islands( pts.Length, MeshTools.Edges( tris, weld ) );
		var cards = new Dictionary<int, List<int>>();
		for ( int i = 0; i < pts.Length; i++ )
		{
			if ( !used[i] ) continue;
			if ( !cards.TryGetValue( piece[i], out var list ) ) cards[piece[i]] = list = new List<int>();
			list.Add( i );
		}

		// Each card's root: its vertex nearest to a lid line, and that line. A card grows from the
		// lid when it is rooted at the line and all of it stays near it: a shell painted over the
		// whole face touches the lid line too.
		var roots = new List<(List<int> Card, int Line, float Along)>();
		int rooted = 0;
		foreach ( var card in cards.Values )
		{
			float best = float.MaxValue, along = 0;
			int line = -1;
			foreach ( int i in card )
				for ( int k = 0; k < lines.Count; k++ )
				{
					float d = AlongLine( lines[k].Stock, pts[i], out float t );
					if ( d < best ) { best = d; line = k; along = t; }
				}
			float furthest = 0;
			foreach ( int i in card ) furthest = MathF.Max( furthest, AlongLine( lines[line].Stock, pts[i], out _ ) );
			if ( best <= LashReach * lines[line].Wide && furthest <= LashSpan * lines[line].Wide ) rooted++;
			roots.Add( (card, line, along) );
		}
		if ( rooted < LashShare * cards.Count ) return null;

		var fitted = new Vec3[pts.Length];
		for ( int i = 0; i < pts.Length; i++ ) fitted[i] = pts[i] + move[i];
		foreach ( var (card, k, along) in roots )
		{
			var (from, to, _, scale) = lines[k];
			var (a, ta, ua, fa) = LineFrame( from, along );
			var (b, tb, ub, fb) = LineFrame( to, along );
			foreach ( int i in card )
			{
				var d = pts[i] - a;
				fitted[i] = b + (tb * Vec3.Dot( d, ta ) + ub * Vec3.Dot( d, ua ) + fb * Vec3.Dot( d, fa )) * scale;
			}
		}

		var result = new Vec3[verts.Length];
		for ( int i = 0; i < verts.Length; i++ ) result[i] = fitted[weld[i]];
		if ( solidOut != null ) Array.Clear( solidOut );
		if ( clothOut != null ) Array.Clear( clothOut );
		if ( paintOut != null ) Array.Clear( paintOut );
		return result;
	}

	// How far a point is from a line through points, and how far along it (0..1 by length) its nearest point is.
	static float AlongLine( Vec3[] line, Vec3 p, out float along )
	{
		float total = 0;
		for ( int k = 1; k < line.Length; k++ ) total += (line[k] - line[k - 1]).Length();
		float best = float.MaxValue, run = 0;
		along = 0;
		for ( int k = 1; k < line.Length; k++ )
		{
			Vec3 a = line[k - 1], ab = line[k] - a;
			float length = ab.Length();
			float t = length > 1e-9f ? Math.Clamp( Vec3.Dot( p - a, ab ) / (length * length), 0f, 1f ) : 0f;
			float d = (p - (a + ab * t)).Length();
			if ( d < best ) { best = d; along = total > 1e-9f ? (run + t * length) / total : 0; }
			run += length;
		}
		return best;
	}

	// The point a share of the way along a line, and a frame there: along the line, up, forward.
	static (Vec3 At, Vec3 Along, Vec3 Up, Vec3 Forward) LineFrame( Vec3[] line, float along )
	{
		float total = 0;
		for ( int k = 1; k < line.Length; k++ ) total += (line[k] - line[k - 1]).Length();
		float want = along * total, run = 0;
		int seg = line.Length - 2;
		for ( int k = 1; k < line.Length; k++ )
		{
			float length = (line[k] - line[k - 1]).Length();
			if ( run + length >= want ) { seg = k - 1; break; }
			run += length;
		}
		var ab = line[seg + 1] - line[seg];
		float l = ab.Length();
		var at = line[seg] + (l > 1e-9f ? ab * ((want - run) / l) : Vec3.Zero);
		// The tangent turns smoothly from one corner of the line to the next, so cards either side of a corner don't jump apart.
		float f = l > 1e-9f ? Math.Clamp( (want - run) / l, 0f, 1f ) : 0f;
		var tangent = Corner( line, seg ) * (1 - f) + Corner( line, seg + 1 ) * f;
		tangent = tangent.Length() > 1e-9f ? tangent / tangent.Length() : new Vec3( 0, 1, 0 );
		var forward = new Vec3( 1, 0, 0 ) - tangent * tangent.X;
		forward /= MathF.Max( forward.Length(), 1e-9f );
		return (at, tangent, Vec3.Cross( forward, tangent ), forward);
	}

	// The direction of a line through points at one of them, halfway between the pieces either side.
	static Vec3 Corner( Vec3[] line, int k )
	{
		var sum = Vec3.Zero;
		if ( k > 0 ) { var d = line[k] - line[k - 1]; float l = d.Length(); if ( l > 1e-9f ) sum += d / l; }
		if ( k < line.Length - 1 ) { var d = line[k + 1] - line[k]; float l = d.Length(); if ( l > 1e-9f ) sum += d / l; }
		return sum;
	}

	const float PaintGap = 0.003f * Units.Metre;     // a garment this close to the stock skin all over is painted on it
	const float PaintRelief = 0.012f * Units.Metre;  // or one that close for the most part and nowhere further than this, shaped on the skin (plump lips)
	const float BrowShare = 0.9f;                    // a garment this much of which grows from the skin over the eyes is brows
	const float BrowLow = 0.01f * Units.Metre;       // the skin over the eyes: from this far below their front
	const float BrowHigh = 0.12f * Units.Metre;      // to this far above it
	const float BrowBack = 0.06f * Units.Metre;      // no further back than this behind it
	const float BrowSide = 0.06f * Units.Metre;      // nor further out to the side than this past it
	const float BrowOff = 0.03f * Units.Metre;       // and standing no further off the skin than this

	/// <summary>
	/// Whether a garment is painted on the skin: a tattoo, makeup, freckles, a pair of brows drawn
	/// on. It is a shell a fraction of a millimetre over the skin, the texture on it drawing what
	/// shows, so where its vertices land is where the drawing is. Also a shell shaped on the skin a
	/// little further off in places (plump lips).
	/// </summary>
	static bool Painted( bool[] used, float[] gap0 )
	{
		var gaps = new List<float>();
		for ( int i = 0; i < used.Length; i++ ) if ( used[i] ) gaps.Add( MathF.Abs( gap0[i] ) );
		gaps.Sort();
		return LikePaint( gaps );
	}

	// From how far a garment's vertices are off the skin, smallest first: whether it lies on it
	// like paint (see Painted).
	static bool LikePaint( List<float> gaps ) =>
		gaps.Count > 0 && (gaps[^1] <= PaintGap || (gaps[gaps.Count / 2] <= PaintGap && gaps[^1] <= PaintRelief));

	/// <summary>
	/// Whether a garment is a pair of brows: cards that grow from the skin right over the eyes,
	/// and the patch of skin they are drawn on. Hair isn't painted on the face and doesn't slide
	/// with the eyes (see the hair in <see cref="Fit"/>), but brows are and do: kept to the stock
	/// face's height, they sat on Aurora's forehead, her eyes being 5 cm lower.
	/// </summary>
	static bool Brows( bool[] used, Vec3[] pts, float[] gap0, TriMesh old, (Vec3 Stock, Vec3 Body)[] eyes )
	{
		if ( eyes == null || eyes.Length != 2 ) return false;
		Vec3 a = eyes[0].Stock, b = eyes[1].Stock;
		float middle = 0.5f * (a.Y + b.Y), side = 0.5f * MathF.Abs( a.Y - b.Y ) + BrowSide;
		float front = MathF.Min( a.X, b.X ) - BrowBack, low = MathF.Min( a.Z, b.Z ) - BrowLow, high = MathF.Max( a.Z, b.Z ) + BrowHigh;
		int count = 0, over = 0;
		for ( int i = 0; i < used.Length; i++ )
		{
			if ( !used[i] ) continue;
			count++;
			if ( MathF.Abs( gap0[i] ) > BrowOff ) continue;
			old.Nearest( pts[i], out var s, out _ );
			if ( s.X >= front && s.Z >= low && s.Z <= high && MathF.Abs( s.Y - middle ) <= side ) over++;
		}
		return count > 0 && over >= BrowShare * count;
	}

	/// <summary>
	/// Puts paint (<see cref="Painted"/>, <see cref="Brows"/>) on this body's skin: every vertex
	/// goes to where the stock skin under it lies on this body (<see cref="BodyMap.OnSkin"/>, the
	/// face lined up by its features, so a line round the stock eye goes round this one), onto
	/// the skin itself, and as far off it, along it, as it was off the stock skin. None of what
	/// holds cloth off the skin applies: the shell was cut to the skin and is drawn on it. Lifted
	/// a few millimetres, a tattoo floats; sunk, it is gone.
	/// </summary>
	static Vec3[] PaintOn( Vec3[] verts, int[] weld, Vec3[] pts, bool[] used, float[] gap0, Vec3[] move, TriMesh old, TriMesh other, Vec3[] skinMove, Vec3[] onSkin, bool[] solidOut, bool[] clothOut, bool[] paintOut )
	{
		var skin = other.Skin();
		var normal = new Vec3[skin.Verts.Length];
		for ( int t = 0; t < skin.TriCount; t++ )
			for ( int k = 0; k < 3; k++ ) normal[skin.Tris[t * 3 + k]] += skin.TriNormals[t];

		// Per stock vertex, whether the map puts it under the skin (paint the stock body hides, see
		// BodyMap.OnSkin): 0 not worked out yet, 1 no, 2 yes. Told by the vertex itself, not by a
		// point between vertices: between a brow ridge and the eye socket under it, that is deep
		// in the head.
		var under = new byte[old.Verts.Length];
		bool Under( int k )
		{
			if ( under[k] == 0 )
			{
				skin.Nearest( onSkin[k], out var on, out int t );
				var n = skin.TriNormals[t];
				under[k] = (byte)(t >= 0 && Vec3.Dot( onSkin[k] - on, n ) < -0.5f * BodyMap.Buried * n.Length() ? 2 : 1);
			}
			return under[k] == 2;
		}

		var fitted = new Vec3[pts.Length];
		for ( int i = 0; i < pts.Length; i++ )
		{
			if ( !used[i] ) { fitted[i] = pts[i] + move[i]; continue; }
			old.Nearest( pts[i], out var s, out int tri );

			int a = old.Tris[tri * 3], b = old.Tris[tri * 3 + 1], c = old.Tris[tri * 3 + 2];
			Barycentric( s, old.Verts[a], old.Verts[b], old.Verts[c], out float u, out float v, out float w );
			var target = onSkin != null ? onSkin[a] * u + onSkin[b] * v + onSkin[c] * w : s + skinMove[a] * u + skinMove[b] * v + skinMove[c] * w;
			bool hidden = onSkin != null && (Under( a ) ? u : 0) + (Under( b ) ? v : 0) + (Under( c ) ? w : 0) >= 0.5f;
			skin.Nearest( target, out var q, out int on );
			int sa = skin.Tris[on * 3], sb = skin.Tris[on * 3 + 1], sc = skin.Tris[on * 3 + 2];
			Barycentric( q, skin.Verts[sa], skin.Verts[sb], skin.Verts[sc], out u, out v, out w );
			var up = normal[sa] * u + normal[sb] * v + normal[sc] * w;
			float length = up.Length();
			if ( length < 1e-12f ) up = skin.TriNormals[on] / MathF.Max( skin.TriNormals[on].Length(), 1e-12f );
			else up /= length;
			fitted[i] = q + up * (hidden ? -BodyMap.Buried : gap0[i]);
		}

		var result = new Vec3[verts.Length];
		for ( int i = 0; i < verts.Length; i++ ) result[i] = fitted[weld[i]];
		if ( solidOut != null ) Array.Clear( solidOut );
		if ( clothOut != null )
			for ( int i = 0; i < verts.Length; i++ ) clothOut[i] = used[weld[i]];
		if ( paintOut != null )
			for ( int i = 0; i < verts.Length; i++ ) paintOut[i] = used[weld[i]];
		return result;
	}

	/// <summary>
	/// A bead (a pearl on a string, a knot) is as thick as it is long and small, and keeps its
	/// shape: it goes as a whole, by the average of where its vertices went, and back to the
	/// body's rest pose as a whole too, as a solid. Pushed off the skin vertex by vertex like cloth,
	/// a pearl on a necklace was squashed on the side towards the neck, and taken back by the
	/// skin under each of its vertices, the ones on the neck grown less than those on the chest,
	/// every pearl came out twice as tall as wide.
	/// </summary>
	static void Beads( Vec3[] pts, Vec3[] fitted, Dictionary<int, List<int>> members, bool[] isSolid )
	{
		foreach ( var idx in members.Values )
		{
			if ( isSolid[idx[0]] ) continue;
			var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( int i in idx )
			{
				lo = new Vec3( MathF.Min( lo.X, pts[i].X ), MathF.Min( lo.Y, pts[i].Y ), MathF.Min( lo.Z, pts[i].Z ) );
				hi = new Vec3( MathF.Max( hi.X, pts[i].X ), MathF.Max( hi.Y, pts[i].Y ), MathF.Max( hi.Z, pts[i].Z ) );
			}
			var size = hi - lo;
			if ( size.Length() > BeadSize || MathF.Min( size.X, MathF.Min( size.Y, size.Z ) ) < BeadShape * MathF.Max( size.X, MathF.Max( size.Y, size.Z ) ) ) continue;
			var shift = Vec3.Zero;
			foreach ( int i in idx ) shift += fitted[i] - pts[i];
			shift /= idx.Count;
			foreach ( int i in idx )
			{
				fitted[i] = pts[i] + shift;
				isSolid[i] = true;
			}
		}
	}

	const float MouthBelowEyes = 0.06f * Units.Metre;   // the mouth is this far under the eyes: hair and beards reach down to it or lower, fringe on a garment and linings worked on hang lower
	const float RimTurn = 0.3f;          // cloth whose faces round a vertex turn further than this from each other (cosine: 73 degrees) is going round its edge there
	const float BackToBackTurn = 0.25f;  // cloth behind a lining faces the same way as it to within this (cosine: 75 degrees)
	const float Squeezed = 0.6f;    // cloth shrunk to less than this share of its size, across, was squeezed (a collar lowered down a short neck), not just taken in
	const float RimSpill = 0.25f;   // the edge of a lining is over the shell spot it is kept behind to within this share of that triangle off its side
	const float LiningDeep = 0.015f * Units.Metre;   // a lining is kept at most this deep under the cloth over it: made deeper it hangs loose behind it rather than lying on it (a collar's stand under its flap), and pressed back that deep where the two had come closer, the stand of a vest's collar squeezed down Akira's short neck stuck up out of the collar
	const float KeepDepth = 0.5f;   // cloth made under other cloth stays at least this share of as far under it as it was made

	/// <summary>
	/// Cloth made under other cloth of the garment, straight out from the skin (a lining under its
	/// shell, the inside of a collar under its outside, the side of a jacket under its sleeve),
	/// with the spot of the cloth over it, so that it can be kept under it (KeepUnder), pressed
	/// back in where it came out through it. Each vertex is fitted on
	/// its own, and lifting cloth over the skin (CoverSkin) lifts the cloth nearest the skin,
	/// the lining, and not the shell over it: the puffer jacket's lining came out through the
	/// shell along its zip and up the inside of its collar on Aurora and Akira, and showed as
	/// black patches. Pressed in to half as deep under it as it was made, it is hidden by what
	/// was made over it, edges included: just under it, the facing behind the zip of the
	/// puffer's left front still showed along the zip and in a crease down from the collar.
	/// </summary>
	static List<(int Vertex, int Tri, float[] Bary, float Side, float Depth)> Layers( Vec3[] pts, List<int> clothTris, List<(int Root, List<int> Idx)> cloth, float[] gap0, Vec3[] away, bool[] isSolid, bool[] buried )
	{
		var under = new List<(int Vertex, int Tri, float[] Bary, float Side, float Depth)>();
		if ( clothTris.Count == 0 ) return under;
		var made = new TriMesh( pts, clothTris.ToArray() );
		var hits = new List<(float T, bool Leaving, int Tri)>();

		foreach ( var (_, idx) in cloth )
			foreach ( int i in idx )
			{
				if ( isSolid[i] || buried[i] || gap0[i] < 0 ) continue;
				made.Crossings( pts[i] + away[i] * LayerSeam, away[i], LayerReach, hits );
				foreach ( var (along, _, t) in hits )
				{
					int a = clothTris[t * 3], b = clothTris[t * 3 + 1], c = clothTris[t * 3 + 2];
					if ( a == i || b == i || c == i ) continue;
					// Which side of that triangle is out, as made: the side away from this vertex.
					float side = Vec3.Dot( Vec3.Cross( pts[b] - pts[a], pts[c] - pts[a] ), away[i] ) >= 0 ? 1f : -1f;
					under.Add( (i, t, Bary( pts[i] + away[i] * (LayerSeam + along), made, t ), side, MathF.Max( LayerSlack, KeepDepth * (LayerSeam + along) ) ) );
					break;
				}
			}
		return under;
	}

	/// <summary>Presses cloth made under other cloth (see Layers) back under it, at least KeepDepth as deep as it was made.</summary>
	static void KeepUnder( Vec3[] fitted, List<int> clothTris, List<(int Vertex, int Tri, float[] Bary, float Side, float Depth)> under )
	{
		for ( int pass = 0; pass < 3; pass++ )
		{
			int pressed = 0;
			foreach ( var (i, t, bw, side, depth) in under )
			{
				int a = clothTris[t * 3], b = clothTris[t * 3 + 1], c = clothTris[t * 3 + 2];
				var normal = Vec3.Cross( fitted[b] - fitted[a], fitted[c] - fitted[a] );
				float length = normal.Length();
				if ( length < 1e-12f ) continue;
				normal *= side / length;
				var over = fitted[a] * bw[0] + fitted[b] * bw[1] + fitted[c] * bw[2];
				float through = Vec3.Dot( fitted[i] - over, normal ) + depth;
				if ( through <= 0 || through > LayerReach ) continue;
				fitted[i] -= normal * through;
				pressed++;
			}
			if ( pressed == 0 ) break;
		}
	}

	/// <summary>
	/// Cloth made back to back with other cloth of the garment and nearer the skin (see
	/// KeepLinings): per such vertex, the spot of the cloth behind it and how deep under it it is
	/// to stay. Found along the back of the cloth as made, not out from the skin.
	/// </summary>
	/// <param name="pts">Welded vertices as made.</param>
	/// <param name="clothTris">Triangles of the cloth, by welded vertex.</param>
	/// <param name="inward">Per welded vertex, whether it faces the skin as made.</param>
	/// <param name="above">Cloth higher than this, as made, is left out (see <see cref="Mouth"/>).</param>
	static List<(int Vertex, int Tri, float[] Bary, float Depth)> BackToBack( Vec3[] pts, List<int> clothTris, TriMesh skin, out bool[] inward, float above )
	{
		int n = pts.Length;
		inward = new bool[n];
		var under = new List<(int Vertex, int Tri, float[] Bary, float Depth)>();
		if ( clothTris.Count == 0 ) return under;
		var normal = new Vec3[n];
		for ( int t = 0; t < clothTris.Count; t += 3 )
		{
			int a = clothTris[t], b = clothTris[t + 1], c = clothTris[t + 2];
			var area = Vec3.Cross( pts[b] - pts[a], pts[c] - pts[a] );
			normal[a] += area; normal[b] += area; normal[c] += area;
		}
		var mesh = new TriMesh( pts, clothTris.ToArray() );
		var hits = new List<(float T, bool Leaving, int Tri)>();
		var skinHits = new List<(float T, bool Leaving, int Tri)>();

		// Pieces no bigger than a bead aren't layers: the near side of a pearl isn't a lining
		// of its far side.
		var piece = Islands( n, MeshTools.Edges( clothTris.ToArray(), Identity( n ) ) );
		var low = new Dictionary<int, Vec3>();
		var high = new Dictionary<int, Vec3>();
		foreach ( int v in clothTris )
		{
			int k = piece[v];
			low[k] = low.TryGetValue( k, out var l ) ? new Vec3( MathF.Min( l.X, pts[v].X ), MathF.Min( l.Y, pts[v].Y ), MathF.Min( l.Z, pts[v].Z ) ) : pts[v];
			high[k] = high.TryGetValue( k, out var h ) ? new Vec3( MathF.Max( h.X, pts[v].X ), MathF.Max( h.Y, pts[v].Y ), MathF.Max( h.Z, pts[v].Z ) ) : pts[v];
		}
		bool Layer( int v ) => low.TryGetValue( piece[v], out var l ) && (high[piece[v]] - l).Length() > BeadSize;

		// The cloth back to back with a vertex, along a way back from it: that cloth faces away
		// from it, about the same way, was made further out, and no part of the body is between
		// them (the far side of a sleeve round an arm).
		bool Behind( int v, Vec3 back, float depth, out (int Vertex, int Tri, float[] Bary, float Depth) found )
		{
			found = default;
			var p = pts[v];
			mesh.Crossings( p, back, LayerReach, hits );
			foreach ( var (along, leaving, t) in hits )
			{
				if ( clothTris[t * 3] == v || clothTris[t * 3 + 1] == v || clothTris[t * 3 + 2] == v ) continue;
				if ( !Layer( clothTris[t * 3] ) || !leaving ) return false;
				var behind = Vec3.Cross( pts[clothTris[t * 3 + 1]] - pts[clothTris[t * 3]], pts[clothTris[t * 3 + 2]] - pts[clothTris[t * 3]] );
				if ( Vec3.Dot( behind, back ) < BackToBackTurn * behind.Length() ) return false;
				var at = p + back * along;
				if ( skin.SignedGap( at, out _ ) <= depth ) return false;
				skin.Crossings( p, back, along, skinHits );
				foreach ( var hit in skinHits )
					if ( !hit.Leaving ) return false;
				found = (v, t, Bary( at, mesh, t ), MathF.Max( LayerSlack, along ));
				return true;
			}
			return false;
		}

		var depthOf = new float[n];
		for ( int v = 0; v < n; v++ )
		{
			float length = normal[v].Length();
			depthOf[v] = float.NaN;
			if ( length < 1e-12f || !Layer( v ) || pts[v].Z > above ) continue;
			float depth = skin.SignedGap( pts[v], out var onSkin );
			if ( depth < -Hidden ) continue;
			depthOf[v] = depth;
			inward[v] = Vec3.Dot( normal[v], pts[v] - onSkin ) * depth < 0;
			if ( Behind( v, normal[v] * (-1f / length), depth, out var found ) ) under.Add( found );
		}

		// The edge a lining folds round into its shell (the opening edge of a zip's facing) has
		// none of the shell straight behind it: it is kept as far behind the shell spot the lining
		// next to it lies under as it was made, where it is over that spot and not off to its side,
		// or else behind the shell it meets going the way that lining goes back. Left out, the
		// facing's edge stood out past the puffer's opening on Aurora's bust and the opening
		// rolled over.
		var spotOf = new Dictionary<int, int>();
		foreach ( var (v, t, _, _) in under ) spotOf[v] = t;
		var next = new Dictionary<int, List<int>>();
		foreach ( var (a, b) in MeshTools.Edges( clothTris.ToArray(), Identity( n ) ) )
			foreach ( var (v, u) in new[] { (a, b), (b, a) } )
			{
				if ( spotOf.ContainsKey( v ) || !spotOf.ContainsKey( u ) || float.IsNaN( depthOf[v] ) ) continue;
				if ( !next.TryGetValue( v, out var list ) ) next[v] = list = new List<int>();
				list.Add( u );
			}
		foreach ( var (v, lining) in next )
		{
			(int Tri, float[] Bary, float Depth) best = (-1, null, 0);
			float bestInside = -RimSpill;
			var back = Vec3.Zero;
			foreach ( int u in lining )
			{
				back -= normal[u] * (1f / normal[u].Length());
				int t = spotOf[u], ta = clothTris[t * 3], tb = clothTris[t * 3 + 1], tc = clothTris[t * 3 + 2];
				if ( ta == v || tb == v || tc == v ) continue;
				var facing = Vec3.Cross( pts[tb] - pts[ta], pts[tc] - pts[ta] );
				float length = facing.Length();
				if ( length < 1e-12f ) continue;
				facing *= 1f / length;
				float behind = Vec3.Dot( pts[ta] - pts[v], facing );
				if ( behind <= LayerSlack || behind >= LayerReach ) continue;
				var bw = Bary( pts[v] + facing * behind, mesh, t );
				float inside = MathF.Min( bw[0], MathF.Min( bw[1], bw[2] ) );
				if ( inside < bestInside ) continue;
				bestInside = inside;
				best = (t, bw, behind);
			}
			if ( best.Tri >= 0 ) under.Add( (v, best.Tri, best.Bary, best.Depth) );
			else if ( back.Length() > 1e-6f && Behind( v, back * (1f / back.Length()), depthOf[v], out var found ) )
			{
				// As deep as it is behind that cloth, square to it: the way there is aslant.
				int ta = clothTris[found.Tri * 3], tb = clothTris[found.Tri * 3 + 1], tc = clothTris[found.Tri * 3 + 2];
				var facing = Vec3.Cross( pts[tb] - pts[ta], pts[tc] - pts[ta] );
				float behind = Vec3.Dot( pts[ta] - pts[v], facing * (1f / facing.Length()) );
				under.Add( (v, found.Tri, found.Bary, MathF.Max( LayerSlack, behind )) );
			}
		}
		return under;
	}

	// Area-weighted vertex normals of a welded mesh.
	static Vec3[] Normals( Vec3[] pts, List<int> tris )
	{
		var normal = new Vec3[pts.Length];
		for ( int t = 0; t < tris.Count; t += 3 )
		{
			int a = tris[t], b = tris[t + 1], c = tris[t + 2];
			var area = Vec3.Cross( pts[b] - pts[a], pts[c] - pts[a] );
			normal[a] += area; normal[b] += area; normal[c] += area;
		}
		return normal;
	}

	static int[] Identity( int n )
	{
		var result = new int[n];
		for ( int i = 0; i < n; i++ ) result[i] = i;
		return result;
	}

	/// <summary>
	/// Cloth made back to back with other cloth of the garment and nearer the skin (see
	/// KeepLinings), a lining or a facing: per vertex, the spot of the cloth over it, as three
	/// vertices and their weights, the first of them -1 where it isn't a lining. Holding the cloth
	/// out over the body with its arms down (<see cref="Repose.CoverInPose"/>) moves these with
	/// the cloth over them. Lifted on their own, the lining at the back of a vest's armholes took
	/// the shell over it with it vertex by vertex and turned it over on Akira; left where it was,
	/// the bottom of the armhole, made a millimetre under its rim, was pressed 2 cm after the rim
	/// gone out and turned over instead.
	/// </summary>
	public static (int[] Over, float[] Weights) Linings( Vec3[] made, int[] tris, bool[] cloth, bool[] paint, TriMesh skin, (Vec3 Stock, Vec3 Body)[] eyes )
	{
		var over = new int[made.Length * 3];
		var weights = new float[made.Length * 3];
		Array.Fill( over, -1 );
		var (weld, first, pts, clothTris) = ClothAsMade( made, tris, cloth, paint );
		var spot = new Dictionary<int, (int Tri, float[] Bary)>();
		foreach ( var (v, t, bw, _) in BackToBack( pts, clothTris, skin, out _, Mouth( eyes ) ) ) spot[v] = (t, bw);
		for ( int i = 0; i < made.Length; i++ )
		{
			if ( !spot.TryGetValue( weld[i], out var at ) ) continue;
			for ( int k = 0; k < 3; k++ )
			{
				over[i * 3 + k] = first[clothTris[at.Tri * 3 + k]];
				weights[i * 3 + k] = at.Bary[k];
			}
		}
		return (over, weights);
	}

	// The garment as made, welded, and its triangles of cloth (not paint) by welded vertex.
	static (int[] Weld, int[] First, Vec3[] Pts, List<int> ClothTris) ClothAsMade( Vec3[] made, int[] tris, bool[] cloth, bool[] paint )
	{
		var weld = MeshTools.Weld( made, out int n );
		var first = new int[n];
		for ( int i = made.Length - 1; i >= 0; i-- ) first[weld[i]] = i;
		var pts = new Vec3[n];
		for ( int v = 0; v < n; v++ ) pts[v] = made[first[v]];
		var clothTris = new List<int>();
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			int a = weld[tris[t]], b = weld[tris[t + 1]], c = weld[tris[t + 2]];
			if ( !cloth[tris[t]] || !cloth[tris[t + 1]] || !cloth[tris[t + 2]] || a == b || b == c || a == c ) continue;
			if ( paint != null && (paint[tris[t]] || paint[tris[t + 1]] || paint[tris[t + 2]]) ) continue;
			clothTris.Add( a ); clothTris.Add( b ); clothTris.Add( c );
		}
		return (weld, first, pts, clothTris);
	}

	/// <summary>
	/// Cloth made back to back with other cloth of the garment and nearer the skin (a lining
	/// behind its shell, the facing behind a zip, the inside of a waistband or of a turned-up
	/// cuff), kept behind it as deep as it was made once the garment is back in the body's rest
	/// pose. KeepUnder does this in the stock pose, half as deep, for cloth found straight out from
	/// the skin. That misses the inside of a fold the skin isn't under (lifting the cloth off the
	/// top of the foot turned the inside of the loose trousers' cuffs out past the cuffs on every
	/// body), cloth made just under the skin (moving each layer with the skin nearest it took the
	/// inside of the standard skirt's waistband 6 mm out past the outside across Aurora's back)
	/// and what going back to the rest pose does, each layer by the skin under it (the inside of
	/// the puffer's hem came out through its back over Aurora's hollow lower back, fine in the
	/// stock pose). Half as deep, the facing behind the puffer's zip still showed along its
	/// opening. Here a layer is found behind each vertex, along the back of the cloth as made,
	/// and the vertex is pressed back behind it wherever it came closer than that.
	/// </summary>
	/// <param name="made">The garment as made, on the stock body.</param>
	/// <param name="rest">The garment in the body's rest pose. Changed in place, and returned.</param>
	/// <param name="cloth">The vertices fitted as cloth (see <see cref="Fit"/>).</param>
	/// <param name="paint">The vertices painted on the skin (see <see cref="Fit"/>), or null. They lie on the skin, each where the skin under it went.</param>
	/// <param name="skin">The stock body's skin.</param>
	/// <param name="eyes">Where the eyes are, on the stock body and on this one. Hats and helmets, on the head above the mouth, are left as they are: the shell of a hard hat, pressed behind its outer face, went out through the band round the head inside it.</param>
	public static Vec3[] KeepLinings( Vec3[] made, Vec3[] rest, int[] tris, bool[] cloth, bool[] paint, TriMesh skin, (Vec3 Stock, Vec3 Body)[] eyes )
	{
		var (weld, first, pts, clothTris) = ClothAsMade( made, tris, cloth, paint );
		int n = pts.Length;
		var under = BackToBack( pts, clothTris, skin, out var inward, Mouth( eyes ) );
		if ( under.Count == 0 ) return rest;

		var now = new Vec3[n];
		for ( int v = 0; v < n; v++ ) now[v] = rest[first[v]];
		var madeNormal = Normals( pts, clothTris );
		var crossed = new List<(float T, bool Leaving, int Tri)>();
		for ( int pass = 0; pass < 3; pass++ )
		{
			int pressed = 0;
			var worn = new TriMesh( MeshTools.Copy( now ), clothTris.ToArray() );
			var wornNormal = Normals( now, clothTris );
			foreach ( var (v, t, bw, depth) in under )
			{
				int a = clothTris[t * 3], b = clothTris[t * 3 + 1], c = clothTris[t * 3 + 2];
				var facing = Vec3.Cross( now[b] - now[a], now[c] - now[a] );
				float length = facing.Length();
				if ( length < 1e-12f ) continue;
				// Only while the two are still back to back, the cloth over it facing away from
				// it: turned past each other, the way behind it is the wrong way. A collar squeezed
				// down Akira's short neck had its flap turned over, and the stand under it, pressed
				// "behind" it, was pushed 2 cm up out of the collar.
				var madeFacing = Vec3.Cross( pts[b] - pts[a], pts[c] - pts[a] );
				if ( Vec3.Dot( madeFacing, madeNormal[v] ) < 0 && Vec3.Dot( facing, wornNormal[v] ) > 0 ) continue;
				// As deep as it was made, less where the cloth over it was squeezed: a collar
				// lowered down a short neck into a third of its height has no room for its lining
				// that deep, pressed in it went out through the other side of the collar.
				float squeezed = MathF.Min( MathF.Sqrt( length / Vec3.Cross( pts[b] - pts[a], pts[c] - pts[a] ).Length() ) / Squeezed, 1f );
				facing *= 1f / length;
				var over = now[a] * bw[0] + now[b] * bw[1] + now[c] * bw[2];
				float through = Vec3.Dot( now[v] - over, facing ) + MathF.Max( LayerSlack, MathF.Min( depth, LiningDeep ) * squeezed );
				if ( through <= 0 || through > LayerReach ) continue;
				// Never through a third layer on the way: the inside of a hard hat has a band
				// round the head under its shell, and the shell's inner face, pressed behind the
				// outer one, went out through the band.
				worn.Crossings( now[v], facing * -1f, through, crossed );
				foreach ( var (along, _, other) in crossed )
				{
					int oa = clothTris[other * 3], ob = clothTris[other * 3 + 1], oc = clothTris[other * 3 + 2];
					if ( oa == v || ob == v || oc == v || other == t || oa == a || oa == b || oa == c || ob == a || ob == b || ob == c || oc == a || oc == b || oc == c ) continue;
					through = MathF.Min( through, along - LayerSlack );
					break;
				}
				if ( through <= 0 ) continue;
				now[v] -= facing * through;
				pressed++;
			}
			if ( pressed == 0 ) break;
		}
		for ( int v = 0; v < n; v++ ) now[v] -= rest[first[v]];

		// Along the fold of a hem the cloth facing the skin has none of the garment straight behind
		// it, only the edge of the fold: it goes with the cloth next to it that does, fading out
		// over a few rings. Left where it was, the bottom row of the inside of the trousers' cuffs
		// still hung out under the cuffs.
		var done = new bool[n];
		foreach ( var (v, _, _, _) in under ) done[v] = true;
		var carried = MeshTools.Copy( now );
		var edges = MeshTools.Edges( clothTris.ToArray(), Identity( n ) );
		for ( int ring = 1; ring <= SeamFade; ring++ )
		{
			float keep = 1f - (ring - 1) / (float)SeamFade;
			var sum = new Dictionary<int, (Vec3 Shift, int Count)>();
			foreach ( var (a, b) in edges )
			{
				if ( done[a] && !done[b] && inward[b] ) { var (s, k) = sum.GetValueOrDefault( b ); sum[b] = (s + carried[a], k + 1); }
				if ( done[b] && !done[a] && inward[a] ) { var (s, k) = sum.GetValueOrDefault( a ); sum[a] = (s + carried[b], k + 1); }
			}
			if ( sum.Count == 0 ) break;
			foreach ( var (v, (shift, count)) in sum )
			{
				carried[v] = shift / count;
				now[v] = carried[v] * keep;
				done[v] = true;
			}
		}
		for ( int i = 0; i < rest.Length; i++ ) rest[i] += now[weld[i]];
		return rest;
	}

	/// <summary>
	/// The pieces of one garment are fitted each on its own, and each is pushed off the body by
	/// what is under it, so a bow over a bodice or a ribbon round a stocking could end up inside
	/// the piece it was made to lie on, or that piece could come through it. So the smaller of
	/// two pieces lying on each other is carried by the bigger one: it moves exactly as the
	/// spot of the bigger piece next to it moves, and stays where it was sewn on, over it or
	/// under it, at the distance it was made with. Biggest pieces go first, so a bow on an apron
	/// on a dress rides on both. A piece lying only partly on another is carried where it lies
	/// on it and keeps its own fit past the reach. Which of two pieces is the outer one is told
	/// by the skin, not by their normals: a lined waistcoat has them pointing both ways.
	/// </summary>
	static void Carry( Vec3[] pts, Vec3[] fitted, Dictionary<int, List<int>> members, Dictionary<int, List<int>> pieceTris, bool[] isSolid, float[] gap0, Vec3[] away, TriMesh body, bool[] buried, float[] takeIn )
	{
		var roots = new List<int>();
		foreach ( var (root, idx) in members )
			if ( pieceTris.ContainsKey( root ) && idx.Count >= LayerMinVerts ) roots.Add( root );
		if ( roots.Count < 2 || roots.Count > LayerMaxPieces ) return;

		// Biggest first. A piece is carried only by pieces before it.
		roots.Sort( ( a, b ) => members[a].Count != members[b].Count ? members[b].Count.CompareTo( members[a].Count ) : a.CompareTo( b ) );
		var made = new Dictionary<int, TriMesh>();
		foreach ( int root in roots ) made[root] = new TriMesh( pts, pieceTris[root].ToArray() );

		var shift = new Vec3[pts.Length];
		for ( int k = 1; k < roots.Count; k++ )
		{
			int root = roots[k];
			foreach ( int i in members[root] )
			{
				if ( buried[i] ) continue;
				// The nearest bigger piece, as made.
				float best = LayerReach, apart = 0;
				int on = -1, onTri = -1;
				Vec3 at = default;
				for ( int j = 0; j < k; j++ )
				{
					int other = roots[j];
					float d = made[other].Nearest( pts[i], out var q, out int tri );
					if ( tri < 0 || d >= best ) continue;
					best = d; on = other; onTri = tri; at = q;
					apart = gap0[i] - Blend( gap0, pieceTris[other], tri * 3, Bary( q, made[other], tri ) );
				}
				if ( on < 0 ) continue;

				// Full where it lies on the other piece, none at the edge of the reach, and eased in
				// where the two were made at nearly the same height (a seam rather than a layer).
				float w = Math.Clamp( 2f - 2f * best / LayerReach, 0f, 1f ) * Math.Clamp( MathF.Abs( apart ) / LayerSeam, 0f, 1f );
				var bw = Bary( at, made[on], onTri );
				var tris = pieceTris[on];
				var carried = pts[i] + Blend( fitted, tris, onTri * 3, bw ) - Blend( pts, tris, onTri * 3, bw );
				// Never closer to the skin than its own fit had it: the bigger piece may be down in
				// the skin where it is hidden, and the own fit has been lifted over bumps of the body
				// that come up between vertices. What lies under the bigger piece is lifted no
				// further than to just under it.
				float carriedGap = body.SignedGap( carried, out _ );
				float sunk = body.SignedGap( fitted[i], out _ ) - carriedGap;
				// A solid (a button) isn't taken in with the cloth over a slimmer body, so its own fit
				// stands off by what the cloth was taken in: by that much less. Held at its own fit,
				// a button on a vest open over Aurora's front stood 1-3 cm off the placket.
				if ( isSolid[i] ) sunk -= MathF.Max( takeIn[i], 0 );
				if ( apart < 0 )
					sunk = MathF.Min( sunk, body.SignedGap( Blend( fitted, tris, onTri * 3, bw ), out _ ) - LayerSlack - carriedGap );
				if ( sunk > 0 ) carried += away[i] * sunk;
				shift[i] = (carried - fitted[i]) * w;
			}

			// A rigid piece (a button) is moved as a whole, by its average.
			var rigid = Vec3.Zero;
			int rigidCount = 0;
			foreach ( int i in members[root] )
				if ( isSolid[i] ) { rigid += shift[i]; rigidCount++; }
			if ( rigidCount > 0 ) rigid /= rigidCount;

			// Each piece is moved before a smaller one looks at it.
			foreach ( int i in members[root] )
			{
				fitted[i] += isSolid[i] ? rigid : shift[i];
				shift[i] = Vec3.Zero;
			}
		}

		// The other way round: a bigger piece made under a smaller one (a bodice under a belt)
		// can still come up through it between the smaller piece's vertices, where the bigger
		// one is curved or denser. There it is pressed back under the smaller piece; it is
		// hidden by it, so the dent doesn't show.
		var gap = new float[pts.Length];
		foreach ( int root in roots )
			foreach ( int i in members[root] ) gap[i] = body.SignedGap( fitted[i], out _ );
		var now = new Dictionary<int, TriMesh>();
		foreach ( int root in roots ) now[root] = new TriMesh( fitted, pieceTris[root].ToArray() );
		for ( int k = 0; k < roots.Count - 1; k++ )
		{
			foreach ( int i in members[roots[k]] )
			{
				if ( isSolid[i] || buried[i] ) continue;
				float best = LayerReach, apart = 0;
				int over = -1;
				for ( int j = k + 1; j < roots.Count; j++ )
				{
					int other = roots[j];
					float d = made[other].Nearest( pts[i], out var q, out int tri );
					if ( tri < 0 || d >= best ) continue;
					best = d; over = other;
					apart = gap0[i] - Blend( gap0, pieceTris[other], tri * 3, Bary( q, made[other], tri ) );
				}
				if ( over < 0 || apart > -LayerSeam ) continue;

				var mesh = now[over];
				float dNow = mesh.Nearest( fitted[i], out var at, out int atTri );
				if ( atTri < 0 || dNow > LayerReach ) continue;
				float sep = gap[i] - Blend( gap, pieceTris[over], atTri * 3, Bary( at, mesh, atTri ) );
				float w = Math.Clamp( 2f - 2f * best / LayerReach, 0f, 1f );
				if ( sep > -LayerSlack ) fitted[i] -= away[i] * ((sep + LayerSlack) * w);
			}
		}
	}

	/// <summary>
	/// A piece too small to be a layer (a card of fraying threads along a denim vest's edges, a
	/// loose thread) hangs from the piece it was made on. Fitted on its own, every card took the
	/// move of the skin nearest it and was pushed off the body by itself, while the edge it hangs
	/// from was taken in with the rest of the cloth: along the front of a vest open over a narrow
	/// body the threads hung up to 2 cm out in the air and fanned apart. So a piece made touching
	/// a layer goes with the spots of that layer under it, as a whole: carried vertex by vertex
	/// like <see cref="Carry"/>'s passengers, a card on a shoulder whose cloth was squeezed in
	/// came out twice as long and stood up from it like a fin.
	/// </summary>
	static void Fringe( Vec3[] pts, Vec3[] fitted, Dictionary<int, List<int>> members, Dictionary<int, List<int>> pieceTris, Func<int, bool> isLayer = null )
	{
		var layers = new List<int>();
		foreach ( var (root, idx) in members )
			if ( pieceTris.ContainsKey( root ) && idx.Count >= LayerMinVerts && (isLayer == null || isLayer( root )) ) layers.Add( root );
		if ( layers.Count == 0 ) return;
		var made = new Dictionary<int, TriMesh>();
		foreach ( int layer in layers ) made[layer] = new TriMesh( pts, pieceTris[layer].ToArray() );

		// How much a layer grew, by its area.
		var growth = new Dictionary<int, float>();
		float Growth( int layer )
		{
			if ( growth.TryGetValue( layer, out float g ) ) return g;
			var tris = pieceTris[layer];
			double before = 0, after = 0;
			for ( int t = 0; t < tris.Count; t += 3 )
			{
				before += Vec3.Cross( pts[tris[t + 1]] - pts[tris[t]], pts[tris[t + 2]] - pts[tris[t]] ).Length();
				after += Vec3.Cross( fitted[tris[t + 1]] - fitted[tris[t]], fitted[tris[t + 2]] - fitted[tris[t]] ).Length();
			}
			return growth[layer] = before > 1e-12 ? Math.Clamp( (float)Math.Sqrt( after / before ), 0.5f, 2f ) : 1f;
		}

		var moved = new Dictionary<int, Vec3>();
		foreach ( var (root, idx) in members )
		{
			if ( idx.Count >= LayerMinVerts ) continue;

			// The layer it hangs from: the nearest one to any of its vertices.
			float best = FringeReach;
			int on = -1;
			foreach ( int i in idx )
				foreach ( int layer in layers )
				{
					float d = made[layer].Nearest( pts[i], out _, out int tri );
					if ( tri < 0 || d >= best ) continue;
					best = d; on = layer;
				}
			if ( on < 0 ) continue;

			// The spots of that layer nearest its vertices, and which way the cloth faces there.
			var tris = pieceTris[on];
			Vec3 was = Vec3.Zero, now = Vec3.Zero;
			var spots = new List<(Vec3 From, Vec3 To, Vec3 Up0, Vec3 Up1)>();
			foreach ( int i in idx )
			{
				made[on].Nearest( pts[i], out var q, out int tri );
				if ( tri < 0 ) continue;
				var bw = Bary( q, made[on], tri );
				int a = tris[tri * 3], b = tris[tri * 3 + 1], c = tris[tri * 3 + 2];
				var up0 = Vec3.Cross( pts[b] - pts[a], pts[c] - pts[a] );
				var up1 = Vec3.Cross( fitted[b] - fitted[a], fitted[c] - fitted[a] );
				if ( up0.Length() < 1e-12f || up1.Length() < 1e-12f ) continue;
				var to = Blend( fitted, tris, tri * 3, bw );
				spots.Add( (q, to, up0 / up0.Length(), up1 / up1.Length()) );
				was += q;
				now += to;
			}
			if ( spots.Count == 0 ) continue;
			was /= spots.Count;
			now /= spots.Count;

			// It keeps its shape: it goes where those spots went, turned as the cloth there turned
			// (the best turn of the spots and of the cloth's facing, as threads along an edge lie
			// on a line), and grown as the whole layer grew, not as the cloth under it stretched.
			float length = 0;
			foreach ( int i in idx ) length = MathF.Max( length, (pts[i] - was).Length() );
			var h = new float[9];
			foreach ( var (from, to, up0, up1) in spots )
			{
				Cross( h, to - now, from - was );
				Cross( h, up1 * length, up0 * length );
			}
			var turn = Rotation( h );
			float grow = Growth( on );
			foreach ( int i in idx )
			{
				var d = pts[i] - was;
				if ( turn != null ) d = new Vec3( turn[0] * d.X + turn[1] * d.Y + turn[2] * d.Z, turn[3] * d.X + turn[4] * d.Y + turn[5] * d.Z, turn[6] * d.X + turn[7] * d.Y + turn[8] * d.Z );
				moved[i] = now + d * grow;
			}
		}
		foreach ( var (i, p) in moved ) fitted[i] = p;
	}

	/// <summary>
	/// The same again when the fit goes back to the body's rest pose: each vertex goes back by
	/// the skin under it, and a card hanging off the cloth out over a belly took other weights
	/// than the edge it hangs from (its own skin, not evened out along the cloth), so the threads
	/// came off the edge and apart a second time, 3 mm on average, up to 2 cm. Here they go with
	/// their cloth from the fit in the stock pose to the rest pose. Only off cloth (hair has none),
	/// and not paint: brows lie on the skin, each hair of them where the skin under it went.
	/// </summary>
	/// <param name="stockFit">The garment as fitted, in the stock pose.</param>
	/// <param name="rest">The garment in the body's rest pose. Changed in place, and returned.</param>
	/// <param name="cloth">The vertices fitted as cloth (see <see cref="Fit"/>).</param>
	/// <param name="paint">The vertices painted on the skin (see <see cref="Fit"/>), or null.</param>
	public static Vec3[] Fringe( Vec3[] stockFit, Vec3[] rest, int[] tris, bool[] cloth, bool[] paint )
	{
		var weld = MeshTools.Weld( stockFit, out int n );
		var first = new int[n];
		for ( int i = stockFit.Length - 1; i >= 0; i-- ) first[weld[i]] = i;
		var pts = new Vec3[n];
		var fitted = new Vec3[n];
		for ( int i = 0; i < n; i++ ) { pts[i] = stockFit[first[i]]; fitted[i] = rest[first[i]]; }

		var piece = Islands( n, MeshTools.Edges( tris, weld ) );
		var members = new Dictionary<int, List<int>>();
		var pieceTris = new Dictionary<int, List<int>>();
		var used = new bool[n];
		foreach ( int v in tris ) used[weld[v]] = true;
		for ( int i = 0; i < n; i++ )
		{
			if ( !used[i] || (paint != null && paint[first[i]]) ) continue;
			if ( !members.TryGetValue( piece[i], out var list ) ) members[piece[i]] = list = new List<int>();
			list.Add( i );
		}
		var seen = new HashSet<(int, int, int)>();
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			int a = weld[tris[t]], b = weld[tris[t + 1]], c = weld[tris[t + 2]];
			if ( a == b || b == c || a == c || !seen.Add( Sorted( a, b, c ) ) ) continue;
			if ( !pieceTris.TryGetValue( piece[a], out var list ) ) pieceTris[piece[a]] = list = new List<int>();
			list.Add( a ); list.Add( b ); list.Add( c );
		}

		var before = MeshTools.Copy( fitted );
		Fringe( pts, fitted, members, pieceTris, root => cloth[first[root]] );
		for ( int i = 0; i < rest.Length; i++ )
			if ( (fitted[weld[i]] - before[weld[i]]).LengthSquared() > 0 ) rest[i] = fitted[weld[i]];
		return rest;
	}

	// Adds q times p transposed to a cross covariance (row-major).
	static void Cross( float[] h, Vec3 q, Vec3 p )
	{
		h[0] += q.X * p.X; h[1] += q.X * p.Y; h[2] += q.X * p.Z;
		h[3] += q.Y * p.X; h[4] += q.Y * p.Y; h[5] += q.Y * p.Z;
		h[6] += q.Z * p.X; h[7] += q.Z * p.Y; h[8] += q.Z * p.Z;
	}

	const float FringeReach = 0.005f * Units.Metre;   // a small piece whose root is this close to a layer hangs from it

	// Barycentric weights of a point on a triangle of a mesh.
	static float[] Bary( Vec3 q, TriMesh mesh, int tri )
	{
		Barycentric( q, mesh.Verts[mesh.Tris[tri * 3]], mesh.Verts[mesh.Tris[tri * 3 + 1]], mesh.Verts[mesh.Tris[tri * 3 + 2]], out float u, out float v, out float w );
		return new[] { u, v, w };
	}

	const float SeamReach = 0.002f * Units.Metre;    // open edges of two pieces this close are sewn together
	const int SeamFade = 3;                          // rings of vertices a seam's closing fades out over

	/// <summary>
	/// A garment made of separate pieces (a sleeve set into a bodice, the two halves of a back)
	/// has them meeting edge to edge. Each piece is fitted on its own, so where the body under
	/// a seam changes shape the two edges go different ways and the seam opens: skin showed
	/// through the maid dress's shoulders and down its middle. Edges of two pieces that met on
	/// the stock garment are moved back together, each by half of what parted them, and the
	/// closing fades out over a few rings of vertices into each piece so no crease is left.
	/// </summary>
	static void Seams( Vec3[] pts, Vec3[] fitted, int[] tris, int[] weld, List<(int A, int B)> edges, int[] piece )
	{
		// Open edges: used by one triangle.
		var uses = new Dictionary<long, int>();
		for ( int t = 0; t < tris.Length; t += 3 )
			for ( int k = 0; k < 3; k++ )
			{
				int a = weld[tris[t + k]], b = weld[tris[t + (k + 1) % 3]];
				if ( a == b ) continue;
				if ( a > b ) (a, b) = (b, a);
				long key = ((long)a << 32) | (uint)b;
				uses[key] = uses.GetValueOrDefault( key ) + 1;
			}
		var open = new HashSet<int>();
		foreach ( var (key, count) in uses )
			if ( count == 1 ) { open.Add( (int)(key >> 32) ); open.Add( (int)(key & 0xffffffff) ); }
		if ( open.Count == 0 ) return;

		// Open edges of other pieces within reach, by a grid of cells as big as the reach.
		float cell = SeamReach;
		(int, int, int) Cell( Vec3 p ) => ((int)MathF.Floor( p.X / cell ), (int)MathF.Floor( p.Y / cell ), (int)MathF.Floor( p.Z / cell ));
		var grid = new Dictionary<(int, int, int), List<int>>();
		foreach ( int v in open )
		{
			var c = Cell( pts[v] );
			if ( !grid.TryGetValue( c, out var list ) ) grid[c] = list = new List<int>();
			list.Add( v );
		}
		var shift = new Dictionary<int, Vec3>();
		foreach ( int v in open )
		{
			var (cx, cy, cz) = Cell( pts[v] );
			var sum = Vec3.Zero;
			int count = 0;
			for ( int dx = -1; dx <= 1; dx++ )
				for ( int dy = -1; dy <= 1; dy++ )
					for ( int dz = -1; dz <= 1; dz++ )
					{
						if ( !grid.TryGetValue( (cx + dx, cy + dy, cz + dz), out var list ) ) continue;
						foreach ( int w in list )
						{
							if ( piece[w] == piece[v] || (pts[w] - pts[v]).LengthSquared() > SeamReach * SeamReach ) continue;
							// Half of how much further the other edge moved than this one.
							sum += ((fitted[w] - pts[w]) - (fitted[v] - pts[v])) * 0.5f;
							count++;
						}
					}
			if ( count == 0 ) continue;
			shift[v] = sum / count;
		}
		if ( shift.Count == 0 ) return;

		// Fading into each piece: every ring carries on the mean of the closing its neighbours in
		// the ring before carry, and takes less of it the further in it is.
		var near = new Dictionary<int, List<int>>();
		foreach ( var (a, b) in edges )
		{
			if ( piece[a] != piece[b] ) continue;
			if ( !near.TryGetValue( a, out var la ) ) near[a] = la = new List<int>();
			if ( !near.TryGetValue( b, out var lb ) ) near[b] = lb = new List<int>();
			la.Add( b ); lb.Add( a );
		}
		var done = new Dictionary<int, Vec3>( shift );
		var ring = new List<int>( shift.Keys );
		for ( int r = 1; r <= SeamFade; r++ )
		{
			float keep = 1f - r / (SeamFade + 1f);
			var next = new Dictionary<int, (Vec3 Sum, int Count)>();
			foreach ( int v in ring )
			{
				if ( !near.TryGetValue( v, out var list ) ) continue;
				foreach ( int w in list )
				{
					if ( done.ContainsKey( w ) ) continue;
					var (sum, count) = next.GetValueOrDefault( w );
					next[w] = (sum + shift[v], count + 1);
				}
			}
			ring.Clear();
			foreach ( var (w, (sum, count)) in next )
			{
				shift[w] = sum / count;
				done[w] = shift[w] * keep;
				ring.Add( w );
			}
		}
		foreach ( var (v, d) in done ) fitted[v] += d;
	}

	const float LayerReach = 0.03f * Units.Metre;    // another piece this close is a layer this one may lie on
	const float LayerSeam = 0.001f * Units.Metre;    // pieces made closer in height than this meet at a seam
	const float LayerSlack = 0.001f * Units.Metre;   // what a piece under another keeps between them
	const int LayerMinVerts = 12;                    // smaller pieces (a stray triangle) are not layers
	const int LayerMaxPieces = 40;                   // more pieces than this is scales or feathers, not layers

	/// <summary>
	/// A heap of cards (hair) goes over the body as one soft mass. Each card fitted on its own
	/// took the skin's move at its roots and kept its own tilt, so where the head's shape
	/// changed (a brow further out, a flatter face) neighbouring cards swung different ways:
	/// gaps in the fringe, cards through a cheek. Here every card vertex moves with the skin
	/// nearest to it, and the moves and the pushes off the body are averaged with whatever
	/// other cards lie within reach, so the heap bends smoothly and no card leaves its
	/// neighbours.
	/// </summary>
	static void FitLoose( List<int> loose, Vec3[] pts, Vec3[] move, float[] wanted, Vec3[] away, Vec3[] fitted, PushTest push, float lockReach )
	{
		// The skin's move is averaged over a lock's length: a card whose root took the
		// scalp's move and whose tip took the face's turned to point out of the face. Pushes
		// off the body are averaged over a card's width, enough to keep neighbours together.
		var smooth = new Vec3[pts.Length];
		var scratch = new Vec3[pts.Length];
		foreach ( int i in loose ) smooth[i] = move[i];
		var wide = new Neighbourhood( loose, pts, lockReach );
		for ( int pass = 0; pass < LoosePasses; pass++ )
		{
			wide.Blur( smooth, scratch );
			foreach ( int i in loose ) smooth[i] = scratch[i];
		}

		var near = new Neighbourhood( loose, pts, LooseReach );
		var pushField = new Vec3[pts.Length];
		for ( int round = 0; round < PushRounds; round++ )
		{
			near.Blur( pushField, scratch );
			foreach ( int i in loose ) pushField[i] = scratch[i];
			foreach ( int i in loose )
				if ( push( pts[i] + smooth[i] + pushField[i], wanted[i], away[i], out var p ) ) pushField[i] += p;
		}
		foreach ( int i in loose ) fitted[i] = pts[i] + smooth[i] + pushField[i];
	}

	// Who is within reach of whom among a set of points, by a grid of cells one reach wide,
	// and a Gaussian blur of a field over them.
	sealed class Neighbourhood
	{
		readonly List<int> points;
		readonly int[][] near;
		readonly float[][] weight;

		public Neighbourhood( List<int> points, Vec3[] pts, float reach )
		{
			this.points = points;
			var cells = new Dictionary<(int, int, int), List<int>>();
			(int, int, int) CellOf( Vec3 p ) => ((int)MathF.Floor( p.X / reach ), (int)MathF.Floor( p.Y / reach ), (int)MathF.Floor( p.Z / reach ));
			foreach ( int i in points )
			{
				var key = CellOf( pts[i] );
				if ( !cells.TryGetValue( key, out var list ) ) cells[key] = list = new List<int>();
				list.Add( i );
			}
			near = new int[pts.Length][];
			weight = new float[pts.Length][];
			float sigma2 = 2f * (reach * 0.5f) * (reach * 0.5f);
			var found = new List<int>();
			var weights = new List<float>();
			foreach ( int i in points )
			{
				found.Clear();
				weights.Clear();
				var (cx, cy, cz) = CellOf( pts[i] );
				for ( int dx = -1; dx <= 1; dx++ )
					for ( int dy = -1; dy <= 1; dy++ )
						for ( int dz = -1; dz <= 1; dz++ )
						{
							if ( !cells.TryGetValue( (cx + dx, cy + dy, cz + dz), out var list ) ) continue;
							foreach ( int j in list )
							{
								float d2 = (pts[j] - pts[i]).LengthSquared();
								if ( d2 > reach * reach ) continue;
								found.Add( j );
								weights.Add( MathF.Exp( -d2 / sigma2 ) );
							}
						}
				near[i] = found.ToArray();
				weight[i] = weights.ToArray();
			}
		}

		public void Blur( Vec3[] field, Vec3[] into )
		{
			foreach ( int i in points )
			{
				var sum = Vec3.Zero;
				float total = 0;
				for ( int k = 0; k < near[i].Length; k++ ) { sum += field[near[i][k]] * weight[i][k]; total += weight[i][k]; }
				into[i] = total > 0 ? sum / total : field[i];
			}
		}
	}

	const float LooseReach = 0.03f * Units.Metre;   // cards this close to each other are pushed together
	const float LockReach = 0.08f * Units.Metre;    // and this close take one move: about a lock's length
	const float SkinReach = 0.08f * Units.Metre;    // hair this far from the skin still took the skin's move
	const int LoosePasses = 2;
	const float BeardTop = 0.01f * Units.Metre;     // hair that grows this far below the eyes and lower
	const float BeardBack = 0.09f * Units.Metre;    // and no further back than this behind them is on the face
	const float BeardShare = 0.7f;                  // a heap of cards this much of which is on the face is a beard
	const float BeardReach = 0.015f * Units.Metre;  // what a beard's move is averaged over: a few cards, not a lock

	/// <summary>
	/// A beard laid onto another face by the face's own marks rather than by the skin under each
	/// card, which rays from the skull carry at the same height on the other head: on a face
	/// with its features lower and closer together the stock upper lip lands on the nose. Down
	/// the face, a point between two marks (the eyes, the nose tip, the mouth, the chin) goes
	/// to the same place between them on the other face, so the beard takes the other face's
	/// proportions. Across, it is narrowed or widened as the face is at that height. The pushes
	/// off the skin that follow keep it lying on the face.
	/// </summary>
	static void FaceWarp( List<int> idx, Vec3[] pts, Vec3[] move, TriMesh stock, TriMesh body, (Vec3 Stock, Vec3 Body)[] face )
	{
		float stockSpan = face[0].Stock.Z - face[3].Stock.Z, bodySpan = face[0].Body.Z - face[3].Body.Z;
		if ( stockSpan <= 0 || bodySpan <= 0 ) return;
		var stockWidth = FaceWidths( stock, face[0].Stock, face[3].Stock.Z - FaceBelow, FaceBack );
		var bodyWidth = FaceWidths( body, face[0].Body, face[3].Body.Z - FaceBelow * bodySpan / stockSpan, FaceBack * bodySpan / stockSpan );
		foreach ( int i in idx )
		{
			var p = pts[i];
			// Between which two marks, and how far between.
			Vec3 shift;
			if ( p.Z >= face[0].Stock.Z ) shift = face[0].Body - face[0].Stock;
			else if ( p.Z <= face[3].Stock.Z ) shift = face[3].Body - face[3].Stock;
			else
			{
				int k = 0;
				while ( k < 2 && p.Z < face[k + 1].Stock.Z ) k++;
				float span = face[k].Stock.Z - face[k + 1].Stock.Z;
				float t = span > 1e-6f ? (face[k].Stock.Z - p.Z) / span : 0;
				shift = (face[k].Body - face[k].Stock) * (1 - t) + (face[k + 1].Body - face[k + 1].Stock) * t;
			}
			var to = p + shift;
			float ws = WidthAt( stockWidth, face[0].Stock.Z, p.Z ), wb = WidthAt( bodyWidth, face[0].Body.Z, to.Z );
			float across = ws > 0 && wb > 0 ? Math.Clamp( wb / ws, 0.5f, 1.5f ) : 1f;
			to = new Vec3( to.X, face[0].Body.Y + (p.Y - face[0].Stock.Y) * across, to.Z );
			move[i] = to - p;
		}
	}

	/// <summary>
	/// The marks carry a beard down and across the face, but in depth only by the profile down
	/// its middle, and the heap is evened out and pushed off the skin as a whole: on a face
	/// whose chin and jaw go back from the mouth sooner and more steeply (an anime face), the
	/// cards came out standing off the chin by 7-10 mm and off the jaw by 4-7 mm where the
	/// human's grow from the skin, and the pushes only ever push out. Each card goes back onto
	/// the skin it lands over: its root (the vertex nearest the stock skin) as far off that skin
	/// as it was off the stock one, and the card turned with the skin there, as the stock skin's
	/// normal at its root turns into this one's, so it keeps its length and its slant to the
	/// face. Both are evened out over the cards round it. A long card (a braid) turns no more
	/// than swings its end by a couple of centimetres, a piece lying on the skin all over goes
	/// on vertex by vertex, and a piece that doesn't grow from the skin (a lock of a braid, a
	/// bead on it) moves with the cards round it.
	/// </summary>
	static void OntoTheSkin( List<int> idx, Vec3[] pts, Vec3[] fitted, float[] gap0, int[] piece, Dictionary<int, List<int>> pieceTris, TriMesh stock, TriMesh body, float[] wanted, Vec3[] away, PushTest push )
	{
		var (cards, lies) = PutOnTheSkin( idx, pts, fitted, gap0, piece, stock, body );
		PushOutCards( cards, lies, pieceTris, fitted, wanted, away, push );
	}

	/// <summary>
	/// Whatever the turn onto the skin put under it comes out. Vertex by vertex, so a card lying
	/// along the skin bends over it and its root stays where it was put; but a card standing up
	/// from the skin had its sunk vertices pushed along it, past the ones above them, and folded
	/// at its root (a hundred triangles turned over on Aurora's decorated beard). A card that
	/// would turn any of its triangles over comes out whole instead, by the most any vertex of it
	/// needs. Pushed out whole, every card stood off the jaw 3-7 mm where it had been put at 2.
	/// </summary>
	static void PushOutCards( Dictionary<int, List<int>> cards, HashSet<int> lies, Dictionary<int, List<int>> pieceTris, Vec3[] fitted, float[] wanted, Vec3[] away, PushTest push )
	{
		var before = new List<Vec3>();
		foreach ( var (card, members) in cards )
		{
			before.Clear();
			foreach ( int i in members ) before.Add( fitted[i] );
			bool moved = false;
			foreach ( int i in members )
				if ( push( fitted[i], wanted[i], away[i], out var p ) ) { fitted[i] += p; moved = true; }
			if ( !moved || lies.Contains( card ) || !pieceTris.TryGetValue( card, out var tris ) || !TurnsOver( tris, members, before, fitted ) ) continue;

			for ( int k = 0; k < members.Count; k++ ) fitted[members[k]] = before[k];
			// Twice: the push off one part of the skin can put another part of the card under it.
			for ( int round = 0; round < 2; round++ )
			{
				var most = Vec3.Zero;
				foreach ( int i in members )
					if ( push( fitted[i], wanted[i], away[i], out var p ) && p.LengthSquared() > most.LengthSquared() ) most = p;
				if ( most.LengthSquared() < 1e-12f ) break;
				foreach ( int i in members ) fitted[i] += most;
			}
		}
	}

	// Whether any of a piece's triangles faces the other way now than before (members[k] was at before[k]).
	static bool TurnsOver( List<int> tris, List<int> members, List<Vec3> before, Vec3[] now )
	{
		var was = new Dictionary<int, Vec3>( members.Count );
		for ( int k = 0; k < members.Count; k++ ) was[members[k]] = before[k];
		for ( int t = 0; t + 2 < tris.Count; t += 3 )
		{
			int a = tris[t], b = tris[t + 1], c = tris[t + 2];
			if ( !was.TryGetValue( a, out var a0 ) || !was.TryGetValue( b, out var b0 ) || !was.TryGetValue( c, out var c0 ) ) continue;
			var n0 = Vec3.Cross( b0 - a0, c0 - a0 );
			var n1 = Vec3.Cross( now[b] - now[a], now[c] - now[a] );
			if ( n0.LengthSquared() > 1e-14f && Vec3.Dot( n0, n1 ) < 0 ) return true;
		}
		return false;
	}

	/// <summary>The moves of <see cref="OntoTheSkin"/>. Returns the beard's pieces, and the ones of them lying on the skin all over.</summary>
	static (Dictionary<int, List<int>> Cards, HashSet<int> Lies) PutOnTheSkin( List<int> idx, Vec3[] pts, Vec3[] fitted, float[] gap0, int[] piece, TriMesh stock, TriMesh body )
	{
		var cards = new Dictionary<int, List<int>>();
		var rootOf = new Dictionary<int, int>();
		foreach ( int i in idx )
		{
			if ( !cards.TryGetValue( piece[i], out var list ) ) cards[piece[i]] = list = new List<int>();
			list.Add( i );
			if ( !rootOf.TryGetValue( piece[i], out int r ) || MathF.Abs( gap0[i] ) < MathF.Abs( gap0[r] ) ) rootOf[piece[i]] = i;
		}

		// A piece lying on the skin all over and too big to move as one (a shadow of stubble
		// under the cards) goes on vertex by vertex, each as far off the skin as it was.
		var moved = new Vec3[pts.Length];
		var grown = new List<int>();   // vertices put back on the skin, for the pieces that don't grow to find
		var lies = new HashSet<int>();
		var flat = new List<int>();
		foreach ( var (card, members) in cards )
		{
			float most = 0, length = 0;
			var root = pts[rootOf[card]];
			foreach ( int i in members ) { most = MathF.Max( most, MathF.Abs( gap0[i] ) ); length = MathF.Max( length, (pts[i] - root).Length() ); }
			if ( most > RootGrows || length < BeardReach ) continue;
			lies.Add( card );
			flat.AddRange( members );
		}
		if ( flat.Count > 0 )
		{
			var lift = new Vec3[pts.Length];
			var found = new Vec3[pts.Length];   // X: 1 where the skin was found
			foreach ( int i in flat )
			{
				if ( body.Nearest( fitted[i], out var s, out int t ) > RootReach || t < 0 ) continue;
				lift[i] = s + Unit( body.TriNormals[t] ) * gap0[i] - fitted[i];
				found[i] = new Vec3( 1, 0, 0 );
			}
			var along = new Neighbourhood( flat, pts, BeardReach );
			var spare = new Vec3[pts.Length];
			var both = new[] { lift, found };
			for ( int pass = 0; pass < LoosePasses; pass++ )
				foreach ( var field in both )
				{
					along.Blur( field, spare );
					foreach ( int i in flat ) field[i] = spare[i];
				}
			foreach ( int i in flat )
			{
				if ( found[i].X < 1e-3f ) continue;
				moved[i] = lift[i] / found[i].X;
				fitted[i] += moved[i];
				grown.Add( i );
			}
		}

		var roots = new List<int>();
		foreach ( var (card, r) in rootOf )
			if ( !lies.Contains( card ) ) roots.Add( r );
		var shift = new Vec3[pts.Length];
		var stockUp = new Vec3[pts.Length];
		var bodyUp = new Vec3[pts.Length];
		var weight = new Vec3[pts.Length];   // X: 1 where the card's skin was found
		var grows = new HashSet<int>();      // cards growing from the skin
		foreach ( int r in roots )
		{
			if ( stock.Nearest( pts[r], out _, out int t0 ) > RootGrows || t0 < 0 ) continue;
			var p = fitted[r];
			if ( body.Nearest( p, out var s1, out int t1 ) > RootReach || t1 < 0 ) continue;
			var n0 = Unit( stock.TriNormals[t0] );
			var n1 = Unit( body.TriNormals[t1] );
			// Skin facing another way is some other part of the face (the neck under a chin).
			if ( Vec3.Dot( n0, n1 ) < 0 ) continue;
			shift[r] = s1 + n1 * gap0[r] - p;
			stockUp[r] = n0;
			bodyUp[r] = n1;
			weight[r] = new Vec3( 1, 0, 0 );
			grows.Add( piece[r] );
		}

		// Evened out over the cards round each one.
		var near = new Neighbourhood( roots, pts, BeardReach );
		var scratch = new Vec3[pts.Length];
		var fields = new[] { shift, stockUp, bodyUp, weight };
		for ( int pass = 0; pass < LoosePasses; pass++ )
			foreach ( var field in fields )
			{
				near.Blur( field, scratch );
				foreach ( int r in roots ) field[r] = scratch[r];
			}

		// Each growing card's move: about its root, by so much, turned from one way to the other.
		var moves = new Dictionary<int, (Vec3 Root, Vec3 By, Vec3 From, Vec3 To)>();
		foreach ( int card in grows )
		{
			int r = rootOf[card];
			var d = shift[r] / weight[r].X;
			if ( d.Length() > RootReach ) continue;
			float length = 0;
			foreach ( int i in cards[card] ) length = MathF.Max( length, (pts[i] - pts[r]).Length() );
			var from = Unit( stockUp[r] );
			var to = Limit( from, Unit( bodyUp[r] ), MathF.Min( MaxRootTurn, length > 0 ? RootSwing / length : MaxRootTurn ) );
			moves[card] = (fitted[r], d, from, to);
		}
		foreach ( var (card, m) in moves )
			foreach ( int i in cards[card] )
			{
				var to = m.Root + m.By + Turn( fitted[i] - m.Root, m.From, m.To );
				moved[i] = to - fitted[i];
				fitted[i] = to;
				grown.Add( i );
			}
		if ( grown.Count == 0 ) return (cards, lies);

		// A piece that doesn't grow from the skin is carried along whole by what the cards round
		// it did, so the pieces of a braid stay together: the moves of the card vertices near
		// its root, or of the nearest one.
		float sigma2 = 2f * (BeardReach * 0.5f) * (BeardReach * 0.5f);
		foreach ( var (card, members) in cards )
		{
			if ( moves.ContainsKey( card ) || lies.Contains( card ) ) continue;
			var at = pts[rootOf[card]];
			var sum = Vec3.Zero;
			float total = 0, nearestD = float.MaxValue;
			int nearest = -1;
			foreach ( int j in grown )
			{
				float dd = (pts[j] - at).LengthSquared();
				if ( dd < nearestD ) { nearestD = dd; nearest = j; }
				if ( dd > BeardReach * BeardReach ) continue;
				float w = MathF.Exp( -dd / sigma2 );
				sum += moved[j] * w;
				total += w;
			}
			var by = total > 1e-6f ? sum / total : moved[nearest];
			foreach ( int i in members ) fitted[i] += by;
		}
		return (cards, lies);
	}

	const float RootGrows = 0.01f * Units.Metre;    // a card whose root is this far off the stock skin or closer grows from it
	const float RootReach = 0.03f * Units.Metre;    // and one landing this far off the other skin has lost it
	const float RootSwing = 0.02f * Units.Metre;    // and the skin's turn swings a card's far end no further than this
	static readonly float MaxRootTurn = MathF.PI / 4; // the skin under a root turns no further than this from face to face

	static Vec3 Unit( Vec3 v )
	{
		float l = v.Length();
		return l > 1e-12f ? v / l : v;
	}

	// b, turned back towards a until it is no more than angle off it.
	static Vec3 Limit( Vec3 a, Vec3 b, float angle )
	{
		float c = Vec3.Dot( a, b );
		if ( c >= MathF.Cos( angle ) ) return b;
		var side = Unit( b - a * c );
		return a * MathF.Cos( angle ) + side * MathF.Sin( angle );
	}

	// v turned the way that turns unit a into unit b, about their common perpendicular.
	static Vec3 Turn( Vec3 v, Vec3 a, Vec3 b )
	{
		var k = Vec3.Cross( a, b );
		float c = Vec3.Dot( a, b );
		if ( c <= -0.999f ) return v;
		return v + Vec3.Cross( k, v ) + Vec3.Cross( k, Vec3.Cross( k, v ) ) * (1f / (1f + c));
	}

	const float FaceSlice = 0.002f * Units.Metre;   // the face's width is measured in slices this high
	const float FaceBelow = 0.08f * Units.Metre;    // and down to this far below the chin (a beard hangs)
	const float FaceBack = 0.07f * Units.Metre;     // of the head no further back than this behind the eyes (not the ears)

	// Half the face's width in slices from the eyes down, by the skin in front of the ears.
	static float[] FaceWidths( TriMesh mesh, Vec3 eyes, float bottom, float back )
	{
		int slices = Math.Max( 1, (int)((eyes.Z - bottom) / FaceSlice) + 1 );
		var width = new float[slices];
		foreach ( var v in mesh.Verts )
		{
			if ( v.Z > eyes.Z || v.Z < bottom || v.X < eyes.X - back ) continue;
			int k = Math.Min( slices - 1, (int)((eyes.Z - v.Z) / FaceSlice) );
			width[k] = MathF.Max( width[k], MathF.Abs( v.Y - eyes.Y ) );
		}
		return width;
	}

	static float WidthAt( float[] width, float top, float z )
	{
		int k = Math.Clamp( (int)((top - z) / FaceSlice), 0, width.Length - 1 );
		// The widest of a few slices round it: a slice can catch only a few vertices.
		float most = 0;
		for ( int j = Math.Max( 0, k - 2 ); j <= Math.Min( width.Length - 1, k + 2 ); j++ ) most = MathF.Max( most, width[j] );
		return most;
	}

	/// <summary>
	/// Whether a heap of cards grows on the face (a beard, a moustache, mutton chops) rather
	/// than the scalp: most of it lies by skin below the eyes and in front, not behind them.
	/// </summary>
	static bool Beard( List<int> loose, Vec3[] pts, TriMesh old, (Vec3 Stock, Vec3 Body)[] eyes )
	{
		if ( eyes == null || eyes.Length == 0 || loose.Count == 0 ) return false;
		var eye = Vec3.Zero;
		foreach ( var (stockEye, _) in eyes ) eye += stockEye;
		eye /= eyes.Length;
		int onFace = 0, near = 0;
		foreach ( int i in loose )
		{
			if ( old.Nearest( pts[i], out var s, out _ ) >= SkinReach ) continue;
			near++;
			if ( s.Z < eye.Z - BeardTop && s.X > eye.X - BeardBack ) onFace++;
		}
		return near > 0 && onFace >= near * BeardShare;
	}

	/// <summary>
	/// A foot changes as a whole from one body to another: longer, wider, flatter. Followed point
	/// by point, a shoe picks up every difference between two sets of toes and comes out
	/// crumpled. On a stiff part the move is the one affine change that best carries that part's
	/// stock skin onto the new skin, smooth across the whole garment.
	/// </summary>
	static void Stiffen( Vec3[] pts, int[] firstOf, int[] boneIndex, float[] boneWeight, Vec3[] move, TriMesh old, TriMesh body, Vec3[] skinMove, SkinByBone byBone, int[] stiff )
	{
		var fits = new Dictionary<int, Affine>();
		var on = new Dictionary<int, List<(int Vertex, float Share)>>();
		for ( int i = 0; i < pts.Length; i++ )
		{
			int src = firstOf[i];
			float total = 0, onStiff = 0, most = 0;
			int main = -1;
			var sum = Vec3.Zero;
			for ( int j = 0; j < 4; j++ )
			{
				int bone = boneIndex[src * 4 + j];
				float w = boneWeight[src * 4 + j];
				if ( bone < 0 || w <= 0 ) continue;
				total += w;
				int group = bone < stiff.Length ? stiff[bone] : -1;
				if ( group < 0 ) continue;
				if ( !fits.TryGetValue( group, out var fit ) ) fits[group] = fit = Affine.Fit( old, body, skinMove, byBone, stiff, group );
				if ( !fit.Valid ) continue;
				sum += (fit.Apply( pts[i] ) - pts[i]) * w;
				onStiff += w;
				if ( w > most ) { most = w; main = group; }
			}
			if ( onStiff <= 0 || total <= 0 ) continue;
			float share = onStiff / total;
			move[i] = move[i] * (1 - share) + sum / onStiff * share;
			if ( !on.TryGetValue( main, out var list ) ) on[main] = list = new List<(int, float)>();
			list.Add( (i, share) );
		}

		// A garment on a foot is often its only visible foot (stockings, shoes hide the body's
		// feet), and a stocking made a little smaller than the stock foot comes out too short or
		// too narrow for a foot of another shape: the toes look cut off. Along the ground the
		// garment is stretched until it covers the body's foot from heel to toe tip and side to
		// side. A shoe already bigger than the foot is left as it is.
		foreach ( var (group, list) in on )
		{
			if ( !fits.TryGetValue( group, out var fit ) || !fit.HasFoot ) continue;
			// Along the foot and across it, in the body's foot's own frame.
			float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
			foreach ( var (i, share) in list )
			{
				if ( share < 0.5f ) continue;
				var p = Turn( pts[i] + move[i], -fit.FootYaw );
				x0 = MathF.Min( x0, p.X ); x1 = MathF.Max( x1, p.X ); y0 = MathF.Min( y0, p.Y ); y1 = MathF.Max( y1, p.Y );
			}
			if ( x1 <= x0 || y1 <= y0 ) continue;
			float tx0 = MathF.Min( x0, fit.FootLo.X - FootCover ), tx1 = MathF.Max( x1, fit.FootHi.X + FootCover );
			float ty0 = MathF.Min( y0, fit.FootLo.Y - FootCover ), ty1 = MathF.Max( y1, fit.FootHi.Y + FootCover );
			if ( tx0 == x0 && tx1 == x1 && ty0 == y0 && ty1 == y1 ) continue;
			// Only a garment that is on the foot. A trouser leg's hem round the ankle covers a bit
			// of it, and stretched to the toes it stood out over the instep; with three vertices
			// on the ankle bone, a pair of workshop trousers had its hem stretched 77 times over
			// and thrown a metre out in front.
			if ( x1 - x0 < FootOn * (tx1 - tx0) || y1 - y0 < FootOn * (ty1 - ty0) ) continue;
			float sx = (tx1 - tx0) / (x1 - x0), sy = (ty1 - ty0) / (y1 - y0);
			foreach ( var (i, share) in list )
			{
				var p = Turn( pts[i] + move[i], -fit.FootYaw );
				var to = new Vec3( tx0 + (p.X - x0) * sx, ty0 + (p.Y - y0) * sy, p.Z );
				move[i] += (Turn( to, fit.FootYaw ) - Turn( p, fit.FootYaw )) * share;
			}
		}
	}


	// Turned about the vertical by an angle in radians.
	static Vec3 Turn( Vec3 p, float angle )
	{
		float c = MathF.Cos( angle ), s = MathF.Sin( angle );
		return new Vec3( p.X * c - p.Y * s, p.X * s + p.Y * c, p.Z );
	}

	// x -> Linear * (x - From) + To
	struct Affine
	{
		public bool Valid;
		public bool HasFoot;     // the body's skin of this part was found, and where it spreads along the ground
		public Vec3 FootLo, FootHi;   // in the foot's frame: turned by -FootYaw about the vertical
		public float FootYaw;    // which way the body's foot points, radians from forward
		public Vec3 From, To;
		public float[] Linear;   // 3x3, row by row

		public Vec3 Apply( Vec3 p )
		{
			var d = p - From;
			return To + new Vec3( Linear[0] * d.X + Linear[1] * d.Y + Linear[2] * d.Z, Linear[3] * d.X + Linear[4] * d.Y + Linear[5] * d.Z, Linear[6] * d.X + Linear[7] * d.Y + Linear[8] * d.Z );
		}

		// Least squares over the skin the part's bones drive most, held a little to its shape so
		// a thin patch of skin can't fold the garment flat.
		public static Affine Fit( TriMesh skin, TriMesh body, Vec3[] skinMove, SkinByBone byBone, int[] stiff, int group )
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

			var moved = new Dictionary<int, Vec3>();
			foreach ( int v in used ) moved[v] = skin.Verts[v] + skinMove[v];
			fit.HasFoot = OntoBody( moved, body, out fit.FootLo, out fit.FootHi, out fit.FootYaw, out bool pointed );
			// How far the stock foot turns to point the body's way (the skin's move has already
			// turned it some of the way).
			float turn = 0;
			if ( pointed )
			{
				float low = float.MaxValue, high = float.MinValue;
				foreach ( int v in used ) { low = MathF.Min( low, skin.Verts[v].Z ); high = MathF.Max( high, skin.Verts[v].Z ); }
				var stock = new List<Vec3>();
				foreach ( int v in used ) stock.Add( skin.Verts[v] );
				if ( Pointing( stock, low + (high - low) * FootSole, out float stockYaw ) )
					turn = Math.Clamp( fit.FootYaw - stockYaw, -FootTurn, FootTurn );
			}

			Vec3 from = Vec3.Zero, to = Vec3.Zero;
			foreach ( int v in used ) { from += skin.Verts[v]; to += moved[v]; }
			from /= used.Count;
			to /= used.Count;

			var spread = new double[9];   // sum of d d^T over the stock skin
			var carried = new double[9];  // sum of e d^T, e where d went
			// The stock skin is turned first, so what holds the fit to its shape holds it to the
			// turned shape: held to the unturned one, a foot (narrow, so held hardest across)
			// turned only half way.
			foreach ( int v in used )
			{
				var d = Turn( skin.Verts[v] - from, turn );
				var e = moved[v] - to;
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
			var linear = new double[9];
			for ( int r = 0; r < 3; r++ )
				for ( int c = 0; c < 3; c++ )
					linear[r * 3 + c] = carried[r * 3] * inverse[c] + carried[r * 3 + 1] * inverse[3 + c] + carried[r * 3 + 2] * inverse[6 + c];
			// And the turn put back in: Linear = linear * turn.
			double cos = Math.Cos( turn ), sin = Math.Sin( turn );
			fit.Linear = new float[9];
			for ( int r = 0; r < 3; r++ )
			{
				fit.Linear[r * 3] = (float)(linear[r * 3] * cos + linear[r * 3 + 1] * sin);
				fit.Linear[r * 3 + 1] = (float)(-linear[r * 3] * sin + linear[r * 3 + 1] * cos);
				fit.Linear[r * 3 + 2] = (float)linear[r * 3 + 2];
			}
			fit.From = from;
			fit.To = to;
			fit.Valid = true;
			return fit;
		}

		// The skin's move can leave part of a foot off the new body: toes carried along with the
		// toe bone keep the stock length and stand out in front of shorter toes, in the air. The
		// moved skin is pulled in along the ground (forward and sideways) to the extent of the new
		// body's skin around it, which is the foot it has to fit.
		// The skin's move also keeps the stock foot's turn: the stock feet point out by 7 degrees,
		// Aurora's by 2, and a shoe left turned out stuck the body's toes and heel through its
		// sides. The moved skin is first turned about the vertical to point where the body's foot
		// does, and the extents are taken along the foot and across it.
		static bool OntoBody( Dictionary<int, Vec3> moved, TriMesh body, out Vec3 blo, out Vec3 bhi, out float yaw, out bool pointed )
		{
			blo = new( float.MaxValue, float.MaxValue, 0 );
			bhi = new( float.MinValue, float.MinValue, 0 );
			yaw = 0;
			pointed = false;
			Vec3 lo = new( float.MaxValue, float.MaxValue, float.MaxValue ), hi = new( float.MinValue, float.MinValue, float.MinValue );
			static Vec3 Min( Vec3 a, Vec3 b ) => new( MathF.Min( a.X, b.X ), MathF.Min( a.Y, b.Y ), MathF.Min( a.Z, b.Z ) );
			static Vec3 Max( Vec3 a, Vec3 b ) => new( MathF.Max( a.X, b.X ), MathF.Max( a.Y, b.Y ), MathF.Max( a.Z, b.Z ) );
			foreach ( var p in moved.Values ) { lo = Min( lo, p ); hi = Max( hi, p ); }
			var size = hi - lo;
			if ( size.X <= 0 || size.Y <= 0 ) return false;

			var feet = new List<Vec3>();
			float bodyLow = float.MaxValue;
			foreach ( var p in body.Verts )
			{
				if ( p.Z < lo.Z || p.Z > hi.Z ) continue;
				if ( p.X < lo.X - size.X * FootSearch || p.X > hi.X + size.X * FootSearch ) continue;
				if ( p.Y < lo.Y - size.Y * FootSearch || p.Y > hi.Y + size.Y * FootSearch ) continue;
				feet.Add( p );
				bodyLow = MathF.Min( bodyLow, p.Z );
			}
			if ( feet.Count < 8 ) return false;

			// Which way each foot points, by its sole: the skin low on it, where the ankle and the
			// shin don't round the shape off.
			float sole = size.Z * FootSole;
			if ( Pointing( feet, bodyLow + sole, out float bodyYaw ) && Pointing( moved.Values, lo.Z + sole, out float skinYaw ) )
			{
				float turn = Math.Clamp( bodyYaw - skinYaw, -FootTurn, FootTurn );
				var middle = (lo + hi) * 0.5f;
				foreach ( var v in new List<int>( moved.Keys ) )
					moved[v] = Turn( moved[v] - middle, turn ) + middle;
				yaw = skinYaw + turn;
				pointed = true;
			}

			// Extents along the foot and across it.
			lo = new( float.MaxValue, float.MaxValue, float.MaxValue );
			hi = new( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var p in moved.Values ) { var q = Turn( p, -yaw ); lo = Min( lo, q ); hi = Max( hi, q ); }
			size = hi - lo;
			foreach ( var p in feet ) { var q = Turn( p, -yaw ); blo = Min( blo, q ); bhi = Max( bhi, q ); }

			// Only ever pulled in. A foot that comes out shorter than the body's is a foot posed
			// differently (a heeled shoe's tilted foot over a flat one), not skin left in the air.
			float sx = (bhi.X - blo.X) / size.X, sy = (bhi.Y - blo.Y) / size.Y;
			bool inX = sx < 1f, inY = sy < 1f;
			if ( !inX && !inY ) return true;
			foreach ( var v in new List<int>( moved.Keys ) )
			{
				var p = Turn( moved[v], -yaw );
				moved[v] = Turn( new Vec3( inX ? blo.X + (p.X - lo.X) * sx : p.X, inY ? blo.Y + (p.Y - lo.Y) * sy : p.Y, p.Z ), yaw );
			}
			return true;
		}

		// The way a footprint (the points below top) is longest, radians from forward, by its
		// spread along the ground. Only a footprint clearly longer than wide has a way it points.
		static bool Pointing( IEnumerable<Vec3> pts, float top, out float yaw )
		{
			yaw = 0;
			double n = 0, mx = 0, my = 0, xx = 0, xy = 0, yy = 0;
			foreach ( var p in pts ) { if ( p.Z >= top ) continue; n++; mx += p.X; my += p.Y; xx += p.X * p.X; xy += p.X * p.Y; yy += p.Y * p.Y; }
			if ( n < 8 ) return false;
			mx /= n; my /= n;
			xx = xx / n - mx * mx; xy = xy / n - mx * my; yy = yy / n - my * my;
			double mean = (xx + yy) / 2, d = Math.Sqrt( (xx - yy) * (xx - yy) / 4 + xy * xy );
			if ( mean - d <= 0 || (mean + d) / (mean - d) < FootLong ) return false;
			// The long axis, taken pointing forward.
			yaw = (float)(0.5 * Math.Atan2( 2 * xy, xx - yy ));
			return true;
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
	///
	/// On a body thinner than the stock one the cloth is taken in as well, the way a garment is
	/// cut a size smaller: how far it stands off the skin past <see cref="LayerGap"/>, its room
	/// and its padding alike, shrinks as much as the body is thinner there. The skeleton only
	/// tells a body's lengths, and a girl's waist on a spine as long as a man's is two thirds as
	/// deep. Following her skin in with every centimetre it stood off his, a Santa jacket cut
	/// for a man's belly hung off her like a pot belly. Worked out here, put on the cloth only
	/// once it is known which of the garment is cloth.
	/// </summary>
	/// <param name="slimmer">Per vertex, how many times as thick as the stock one the body under it is, at most 1.</param>
	/// <param name="takeIn">Out: per vertex, how much closer to the skin it goes for that.</param>
	static void TakeUpRoom( Vec3[] verts, int[] tris, Vec3[] pts, Vec3[] move, float[] gap0, Vec3[] away, float[] wanted, float[] slimmer, float[] takeIn )
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
			float taken = 0;
			if ( room > 0 )
			{
				wanted[i] = gap0[i] - room;
				float grow = Vec3.Dot( move[i], away[i] );
				if ( grow > 0 ) taken = MathF.Min( grow, room );
				if ( taken > 0 ) move[i] -= away[i] * taken;
			}
			takeIn[i] = (gap0[i] - LayerGap - taken) * (1f - slimmer[i]);
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
	///
	/// Only hair the hat covers is moved: hair whose line out from the head meets the hat, and
	/// hair along its edge. Hair in sight further below and beside it stays where it is, or the
	/// smoothing that keeps a card from kinking drags it in after the part that went under, and
	/// skin shows between hair and hat.
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
		// Hair right at the hat moves too: a card lying across the edge has corners just past
		// it, and held still they keep the card through it or stick it out under the edge.
		var covered = new bool[fitted.Length];
		for ( int i = 0; i < fitted.Length; i++ )
			covered[i] = Covered( fitted[i], under, body, hits ) || under.Nearest( fitted[i], out _, out _ ) < HatNear;
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
				// A triangle reaching out into sight: its corners there stay, so a push worked
				// out for all three would throw the others too far.
				if ( !covered[a] || !covered[b] || !covered[c] ) continue;
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
			// Hair in sight holds still, and holds the cards it is part of: a push it can't take
			// would otherwise be handed on to its covered neighbours by the smoothing, pass after
			// pass, and throw them out the far side of the hat.
			for ( int i = 0; i < fitted.Length; i++ )
				if ( !covered[i] ) push[i] = Vec3.Zero;
			if ( pass < 5 ) MeshTools.Relax( push, all, edges, 1, 0.5f );
			for ( int i = 0; i < fitted.Length; i++ )
				if ( covered[i] ) fitted[i] += push[i];
		}
	}

	const float RimPress = 0.0015f * Units.Metre;  // how far off the skin hair along a hat's rim is pressed
	const float RimBand = 0.02f * Units.Metre;     // hair this close to the rim, either side of it, is pressed all the way
	const float RimFade = 0.015f * Units.Metre;    // and further out it goes back to its own shape over this much
	const float RimContact = 0.025f * Units.Metre; // a hat this close over the skin is resting on the head
	const float RimDeep = 0.025f * Units.Metre;    // hair further off the skin than this is left alone, it is in a lock of its own
	const float RimSunk = 0.01f * Units.Metre;     // nor hair this far into the head: made there, or measured from the wrong side of an ear
	const float RimClear = 0.0005f * Units.Metre;  // what pressed hair keeps between itself and a hat resting on it

	/// <summary>
	/// Hair along the line where a hat's rim meets the head is pressed onto the scalp. The tuck
	/// takes back under the hat what came through it, which on a hat that sits tight leaves the
	/// hair at its edge sunk into the head or pulled up, and a puffed out style stands off the
	/// head just below the rim: either way there is a band of bare scalp between hat and hair.
	/// A real hat squashes the hair along its edge flat, and so does this: in a band a few cm
	/// wide on both sides of the rim, each bit of hair goes along the skin's normal to just off
	/// the skin, all the way near the rim and less further away, back to the hair's own shape.
	/// Hair standing out further than a lock lying on the head is left as it is.
	/// </summary>
	static void PressAtRim( Vec3[] fitted, TriMesh under, TriMesh body )
	{
		var skin = body.Skin();
		var normal = new Vec3[skin.Verts.Length];
		var used = new bool[skin.Verts.Length];
		for ( int t = 0; t < skin.TriCount; t++ )
			for ( int c = 0; c < 3; c++ )
			{
				int v = skin.Tris[t * 3 + c];
				normal[v] += skin.TriNormals[t];
				used[v] = true;
			}

		// Skin the hat rests on, near the hat: the rim is where that ends.
		var hits = new List<(float T, bool Leaving, int Tri)>();
		var near = new bool[skin.Verts.Length];
		var on = new bool[skin.Verts.Length];
		float reach = RimBand + RimFade + RimContact;
		for ( int v = 0; v < skin.Verts.Length; v++ )
		{
			if ( !used[v] || under.Nearest( skin.Verts[v], out _, out _ ) > reach ) continue;
			float length = normal[v].Length();
			if ( length < 1e-12f ) continue;
			near[v] = true;
			on[v] = HatOver( skin.Verts[v], normal[v] / length, under, hits, out _ );
		}
		var rim = new List<Vec3>();
		for ( int t = 0; t < skin.TriCount; t++ )
			for ( int c = 0; c < 3; c++ )
			{
				int a = skin.Tris[t * 3 + c], b = skin.Tris[t * 3 + (c + 1) % 3];
				if ( a < b && near[a] && near[b] && on[a] != on[b] ) rim.Add( (skin.Verts[a] + skin.Verts[b]) * 0.5f );
			}
		if ( rim.Count == 0 ) return;

		float far = RimBand + RimFade;
		for ( int i = 0; i < fitted.Length; i++ )
		{
			var p = fitted[i];
			if ( skin.Nearest( p, out var at, out int tri ) > RimDeep || tri < 0 ) continue;
			var n = skin.TriNormals[tri];
			float area = n.Length();
			if ( area < 1e-12f ) continue;
			n /= area;
			float gap = Vec3.Dot( p - at, n );
			if ( gap > RimDeep || gap < -RimSunk ) continue;

			float closest = float.MaxValue;
			foreach ( var r in rim ) closest = MathF.Min( closest, (r - at).LengthSquared() );
			closest = MathF.Sqrt( closest );
			if ( closest >= far ) continue;

			float w = 1f - Smooth( RimBand, far, closest );
			w *= 1f - Smooth( 0.6f * RimDeep, RimDeep, gap );
			w *= 1f - Smooth( 0.6f * RimSunk, RimSunk, -gap );
			// Under a hat resting closer than that, hair is only brought up out of the head, to just
			// under the hat, never pushed in: there it is out of sight, and what the tuck sank at the
			// edge is what leaves the scalp bare between the rim and the hair below it.
			float target = RimPress;
			if ( HatOver( at, n, under, hits, out float hat ) && hat < RimPress + RimClear )
			{
				target = MathF.Max( 0, hat - RimClear );
				if ( gap >= target ) continue;
			}
			// A part of the body standing off the skin between it and the hair (a brow of its own,
			// an ear) stays under the hair: it is pressed onto that part instead.
			if ( gap > target )
			{
				body.Crossings( at + n * RimClear, n, gap - RimClear, hits );
				foreach ( var h in hits ) target = MathF.Max( target, h.T + RimClear + RimPress );
				if ( target >= gap ) continue;
			}
			fitted[i] = p + n * ((target - gap) * w);
		}
	}

	// Whether a hat rests on the skin at a point, and how far out along its normal it is.
	static bool HatOver( Vec3 at, Vec3 n, TriMesh under, List<(float T, bool Leaving, int Tri)> hits, out float distance )
	{
		under.Crossings( at - n * TuckDepth, n, RimContact + TuckDepth, hits );
		distance = hits.Count > 0 ? hits[0].T - TuckDepth : 0;
		return hits.Count > 0;
	}

	static float Smooth( float from, float to, float x )
	{
		float t = Math.Clamp( (x - from) / (to - from), 0f, 1f );
		return t * t * (3f - 2f * t);
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

	const float HatReach = 0.3f * Units.Metre;  // how far out past a bit of hair a hat can be and still cover it
	const float HatNear = 0.03f * Units.Metre;  // hair this close to a hat goes under it with the rest

	// Whether a hat is in the way of a bit of hair, seen from the head: the line from inside the
	// head out through it meets the hat, before it or past it.
	static bool Covered( Vec3 p, TriMesh under, TriMesh body, List<(float T, bool Leaving, int Tri)> hits )
	{
		if ( !InsideHead( p, body, out var from ) ) return false;
		var way = p - from;
		float length = way.Length();
		if ( length < 1e-6f * Units.Metre ) return false;
		under.Crossings( from, way / length, length + HatReach, hits );
		return hits.Count > 0;
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
	/// <param name="thickness">Per stock vertex, how many times as thick the body is there (<see cref="BodyMap.Thickness"/>), or null. Cloth over a slimmer body was taken in, and keeps the skin covered from that much closer.</param>
	static void CoverSkin( Vec3[] pts, Vec3[] fitted, bool[] buried, List<int> clothTris, TriMesh old, Vec3[] skinMove, bool[] skinFound, bool clearSkin, float[] thickness, bool[] heldOff, Neck neck )
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
		var layerHits = new List<(float T, bool Leaving, int Tri)>();
		for ( int v = 0; v < old.Verts.Length; v++ )
		{
			if ( skinFound != null && !skinFound[v] ) continue;   // where this vertex went is a guess

			float before = outBefore[v].Length(), after = outAfter[v].Length();
			if ( before < 1e-12f || after < 1e-12f ) continue;
			outBefore[v] /= before;
			outAfter[v] /= after;

			// Skin that turned round on its way to the other body was matched to some other bit
			// of it: an ear the rays found at another angle, facing sideways where the stock ear
			// faced back and down. Holding the cloth off it the way it was held off the stock ear
			// pushed a collar's corner out sideways into a horn.
			if ( Vec3.Dot( outBefore[v], outAfter[v] ) < CoverTurn ) continue;

			// Cloth over a short neck was lowered and taken in round it (see Neck): it is no longer
			// over the skin it was over, and held as far off as it was, a collar stood out round
			// Aurora's neck as a funnel again and went back up towards her head.
			if ( neck != null && neck.Clearance( old.Verts[v] ) < 1f ) continue;

			float distance = cloth.Nearest( old.Verts[v], out var above, out int tri );
			if ( tri < 0 || distance > GapCap ) continue;

			// Straight above, not off to the side: the nearest cloth to skin just past a hem is
			// the hem's edge, and that skin was never covered. Skin that came through the cloth
			// on the stock body (cloth straight below it) only counts when the skin is to be
			// cleared, and then it wants the clearing gap like everything else.
			float gap = Vec3.Dot( above - old.Verts[v], outBefore[v] );
			if ( MathF.Abs( gap ) < 0.7f * distance ) continue;
			if ( gap <= 0 && (!clearSkin || gap < -ClearDepth) ) continue;
			if ( thickness != null && gap > LayerGap && thickness[v] < 1f ) gap = LayerGap + (gap - LayerGap) * thickness[v];
			if ( clearSkin ) gap = MathF.Max( gap, ClearGap );

			int a = clothTris[tri * 3], b = clothTris[tri * 3 + 1], c = clothTris[tri * 3 + 2];
			if ( buried[a] || buried[b] || buried[c] ) continue;
			Barycentric( above, pts[a], pts[b], pts[c], out float bu, out float bv, out float bw );
			under.Add( (v, tri, bu, bv, bw, gap) );

			// And the cloth made over that, each layer at its own height: lifted alone, the
			// nearest layer (a lining) went out through the shell over it.
			if ( gap <= 0 ) continue;
			float scale = gap / MathF.Max( Vec3.Dot( above - old.Verts[v], outBefore[v] ), 1e-6f );
			cloth.Crossings( old.Verts[v] + outBefore[v] * (Vec3.Dot( above - old.Verts[v], outBefore[v] ) + LayerSeam), outBefore[v], GapCap, layerHits );
			float first = Vec3.Dot( above - old.Verts[v], outBefore[v] ) + LayerSeam;
			foreach ( var (along, _, outer) in layerHits )
			{
				if ( outer == tri ) continue;
				int oa = clothTris[outer * 3], ob = clothTris[outer * 3 + 1], oc = clothTris[outer * 3 + 2];
				if ( buried[oa] || buried[ob] || buried[oc] ) continue;
				var at = old.Verts[v] + outBefore[v] * (first + along);
				Barycentric( at, pts[oa], pts[ob], pts[oc], out float ou, out float ov, out float ow );
				under.Add( (v, outer, ou, ov, ow, (first + along) * scale) );
			}
		}

		var unlifted = MeshTools.Copy( fitted );
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

				// The point above the skin goes out by what it needs, shared between the corners by
				// how much of the point each one makes: a corner it is barely on hardly moves. With
				// every corner taking the whole need, a corner with a seventh of a say in the point
				// went out by all of it, again on every pass, and was drawn out into a spike.
				Vec3 push = outAfter[skin] * (need / (bu * bu + bv * bv + bw * bw));
				Lift( lift, a, push * MathF.Max( bu, 0 ) );
				Lift( lift, b, push * MathF.Max( bv, 0 ) );
				Lift( lift, c, push * MathF.Max( bw, 0 ) );
			}

			if ( lift.Count == 0 ) break;
			foreach ( var (vertex, push) in lift ) fitted[vertex] += push;
		}

		TakeBack( pts, fitted, unlifted, clothTris, heldOff );
	}

	const int UnliftPasses = 8;

	/// <summary>
	/// Each vertex of the cloth goes by the skin closest to it, and on a garment held off the
	/// face over its hollows and creases (a crumpled mask over the eyes and the mouth of a face
	/// whose features are set elsewhere) neighbouring vertices go by bits of skin that moved
	/// apart: the cloth's own folds crossed and showed their insides. Whatever the fit left
	/// turned over on the head is evened out with the cloth round it until it isn't. Not cloth
	/// lowered down a short neck (see Neck): a collar squeezed into a third of its height turns
	/// its quilting over on purpose, and evened out with the jacket under it, it went back up
	/// Aurora's neck by 6 mm.
	/// </summary>
	static void Untangle( Vec3[] pts, Vec3[] fitted, int[] tris, int[] weld, List<(int A, int B)> edges, int[] piece, List<(int Root, List<int> Idx)> cloth, bool[] heldOff, bool[] lowered )
	{
		if ( cloth.Count == 0 || heldOff == null ) return;
		var inCloth = new HashSet<int>();
		foreach ( var (root, _) in cloth ) inCloth.Add( root );
		var skip = new bool[tris.Length / 3];
		for ( int t = 0; t < skip.Length; t++ )
		{
			int a = weld[tris[t * 3]], b = weld[tris[t * 3 + 1]], c = weld[tris[t * 3 + 2]];
			skip[t] = !inCloth.Contains( piece[a] ) || !AnyOn( heldOff, a, b, c ) || AnyOn( lowered, a, b, c );
		}
		var shift = new Vec3[pts.Length];
		foreach ( var (_, idx) in cloth )
			foreach ( int i in idx ) shift[i] = fitted[i] - pts[i];
		if ( !MeshTools.Untangle( pts, tris, weld, edges, shift, null, skip ) ) return;
		foreach ( var (_, idx) in cloth )
			foreach ( int i in idx ) fitted[i] = pts[i] + shift[i];
	}

	const float BelowChin = 0.02f * Units.Metre;    // cloth down to this far below the chin is on the head
	const float ChinBelowEyes = 0.12f * Units.Metre; // where the chin is taken to be on a face whose mouth wasn't found
	const float HeldOff = 0.005f * Units.Metre;     // a piece of cloth lying this far off the stock skin, halfway through, is held off it

	/// <summary>
	/// Per welded vertex, whether it is cloth held off the head, from the chin up. A head is
	/// where a body's features can be set far from the stock ones (an anime face has its eyes
	/// 5 cm lower and its mouth by the chin), so skin a few centimetres apart moves by very
	/// different amounts there, over the face and round its sides, and cloth spanning it can
	/// fold onto itself. A piece lying on the skin (paint on a face) goes with the skin under
	/// it and isn't counted, and neither is anything off the head, where the skin moves
	/// smoothly. Null if the face isn't known.
	/// </summary>
	static bool[] HeldOffTheHead( Vec3[] pts, float[] gap0, int[] piece, (Vec3 Stock, Vec3 Body)[] eyes, (Vec3 Stock, Vec3 Body)[] face )
	{
		float chin;
		if ( face != null && face.Length == 4 ) chin = face[3].Stock.Z;
		else if ( eyes != null && eyes.Length > 0 )
		{
			chin = 0;
			foreach ( var (stock, _) in eyes ) chin += stock.Z / eyes.Length;
			chin -= ChinBelowEyes;
		}
		else return null;

		var gaps = new Dictionary<int, List<float>>();
		for ( int i = 0; i < pts.Length; i++ )
		{
			if ( pts[i].Z < chin - BelowChin ) continue;
			if ( !gaps.TryGetValue( piece[i], out var list ) ) gaps[piece[i]] = list = new List<float>();
			list.Add( gap0[i] );
		}
		var held = new HashSet<int>();
		foreach ( var (root, list) in gaps )
		{
			list.Sort();
			if ( list[list.Count / 2] >= HeldOff ) held.Add( root );
		}

		var result = new bool[pts.Length];
		for ( int i = 0; i < pts.Length; i++ ) result[i] = pts[i].Z >= chin - BelowChin && held.Contains( piece[i] );
		return result;
	}

	static bool AnyOn( bool[] on, int a, int b, int c ) => on != null && (on[a] || on[b] || on[c]);

	/// <summary>
	/// A lift that turns the cloth over on the head does more harm than the skin it covers: on a
	/// crumpled mask the cloth closest over a bit of skin can be the inner layer of a fold, and
	/// lifted on its own it comes out through the outer one. Such lifts are taken back.
	/// </summary>
	/// <param name="unlifted">Where the cloth was before the lifts.</param>
	/// <param name="clothTris">The triangles, as welded vertex indices.</param>
	/// <param name="heldOff">See <see cref="HeldOffTheHead"/>.</param>
	static void TakeBack( Vec3[] pts, Vec3[] fitted, Vec3[] unlifted, List<int> clothTris, bool[] heldOff )
	{
		if ( heldOff == null ) return;
		for ( int pass = 0; pass < UnliftPasses; pass++ )
		{
			bool any = false;
			for ( int t = 0; t < clothTris.Count; t += 3 )
			{
				int a = clothTris[t], b = clothTris[t + 1], c = clothTris[t + 2];
				if ( !AnyOn( heldOff, a, b, c ) || !TurnedOver( pts, fitted, a, b, c ) || TurnedOver( pts, unlifted, a, b, c ) ) continue;
				fitted[a] = unlifted[a]; fitted[b] = unlifted[b]; fitted[c] = unlifted[c];
				any = true;
			}
			if ( !any ) break;
		}
	}

	static bool TurnedOver( Vec3[] before, Vec3[] after, int a, int b, int c )
	{
		var n0 = Vec3.Cross( before[b] - before[a], before[c] - before[a] );
		var n1 = Vec3.Cross( after[b] - after[a], after[c] - after[a] );
		float l0 = n0.Length(), l1 = n1.Length();
		return l0 >= 1e-12f && (l1 <= 1e-12f || Vec3.Dot( n0, n1 ) < MeshTools.Turned * l0 * l1);
	}

	// A vertex lifted for several bits of skin takes the biggest lift.
	static void Lift( Dictionary<int, Vec3> lift, int vertex, Vec3 push )
	{
		if ( push.LengthSquared() <= 0 ) return;
		if ( !lift.TryGetValue( vertex, out var have ) || push.LengthSquared() > have.LengthSquared() ) lift[vertex] = push;
	}

	const float BindTurnSlack = 0.02f;                     // radians (about a degree): bind poses turned less than this apart are the same
	const float BindMoveSlack = 0.002f * Units.Metre;      // and moved less than this
	const float OwnFrameTurn = 0.17f;                      // radians (about 10 degrees): a skeleton turned this far as a whole was exported in a frame of its own
	const float OwnFrameMove = 0.05f * Units.Metre;        // or moved this far

	/// <summary>
	/// Puts a garment exported in a frame of its own into the stock body's. Bone merging hangs
	/// each vertex off the body's bone the way it hung off the garment's own one, so in the game
	/// a cigar exported with its whole skeleton turned a quarter round about the vertical still
	/// sits in the mouth. The fit works in model space and took the vertices where they lie in
	/// the file: to the side of the head. Here the garment goes where bone merging puts it on
	/// the stock body. Only a garment whose bones are all off by one and the same turn and shift,
	/// and by a lot, is moved. Stock garments made for an older citizen have skeletons a few
	/// millimetres or degrees off, as a whole or bone by bone, and keep the fit they always had.
	/// </summary>
	/// <returns>False if the garment was left as it is.</returns>
	public static bool ToStockBind( SkinnedGeometry garment, SkinnedGeometry stock )
	{
		var byName = new Dictionary<string, int>();
		for ( int i = 0; i < stock.BoneNames.Length; i++ ) byName[stock.BoneNames[i]] = i;
		int bones = garment.BoneNames.Length;
		var used = new bool[bones];
		for ( int k = 0; k < garment.BoneIndex.Length; k++ )
			if ( garment.BoneIndex[k] >= 0 && garment.BoneIndex[k] < bones && garment.BoneWeight[k] > 0 ) used[garment.BoneIndex[k]] = true;

		// What takes each bone the vertices hang off onto the stock bone of its name.
		float[] turn = null;
		var shift = Vec3.Zero;
		for ( int i = 0; i < bones; i++ )
		{
			if ( !used[i] || !byName.TryGetValue( garment.BoneNames[i], out int s ) ) continue;
			var mine = Quat.Multiply( stock.BoneRotations[s], Quat.Inverse( garment.BoneRotations[i] ) );
			var at = stock.BonePositions[s] - Quat.Rotate( mine, garment.BonePositions[i] );
			if ( turn == null ) { turn = mine; shift = at; continue; }
			float apart = MathF.Abs( turn[0] * mine[0] + turn[1] * mine[1] + turn[2] * mine[2] + turn[3] * mine[3] );
			if ( 2f * MathF.Acos( Math.Clamp( apart, 0f, 1f ) ) > BindTurnSlack ) return ByBones( garment, stock, byName, used );
			if ( (Quat.Rotate( turn, garment.BonePositions[i] ) + shift - stock.BonePositions[s]).Length() > BindMoveSlack ) return ByBones( garment, stock, byName, used );
		}
		if ( turn == null ) return false;
		float angle = 2f * MathF.Acos( Math.Clamp( MathF.Abs( turn[3] ), 0f, 1f ) );
		bool moved = false;
		for ( int i = 0; i < bones && !moved; i++ )
			moved = used[i] && (Quat.Rotate( turn, garment.BonePositions[i] ) + shift - garment.BonePositions[i]).Length() > OwnFrameMove;
		if ( angle < OwnFrameTurn && !moved ) return ByBones( garment, stock, byName, used );

		for ( int v = 0; v < garment.Positions.Length; v++ )
		{
			garment.Positions[v] = Quat.Rotate( turn, garment.Positions[v] ) + shift;
			if ( garment.Normals != null && v < garment.Normals.Length ) garment.Normals[v] = Quat.Rotate( turn, garment.Normals[v] );
		}
		for ( int i = 0; i < bones; i++ )
		{
			garment.BonePositions[i] = Quat.Rotate( turn, garment.BonePositions[i] ) + shift;
			garment.BoneRotations[i] = Quat.Multiply( turn, garment.BoneRotations[i] );
			// Relative to the parent nothing changed, only the roots moved.
			if ( garment.BoneLocalPositions != null && garment.BoneParents[i] < 0 )
			{
				garment.BoneLocalPositions[i] = garment.BonePositions[i];
				garment.BoneLocalRotations[i] = garment.BoneRotations[i];
			}
		}
		garment.BindTurn = turn;
		return true;
	}

	/// <summary>
	/// A layer painted on the skin made for an older citizen (a lipstick, face tattoos) has bones
	/// a couple of centimetres off the stock ones, each by a little more or less, and bone merging
	/// puts each vertex where its own bones say. Taken where it lies in the file, it was fitted
	/// two centimetres up the face from where it is drawn on the stock body: the lipstick on the
	/// skin under the nose. So it is moved bone by bone the way bone merging moves it, but only
	/// when that is what puts it on the stock skin (<see cref="LikePaint"/>) and it isn't there
	/// already. Anything else made for an older citizen keeps the fit it always had.
	/// </summary>
	static bool ByBones( SkinnedGeometry garment, SkinnedGeometry stock, Dictionary<string, int> byName, bool[] used )
	{
		int bones = garment.BoneNames.Length;
		var turns = new float[bones][];
		var shifts = new Vec3[bones];
		bool off = false, oneTurn = true;
		float[] first = null;
		for ( int i = 0; i < bones; i++ )
		{
			int b = i;
			while ( b >= 0 && !byName.ContainsKey( garment.BoneNames[b] ) ) b = garment.BoneParents[b];
			if ( b < 0 ) continue;
			int s = byName[garment.BoneNames[b]];
			turns[i] = Quat.Multiply( stock.BoneRotations[s], Quat.Inverse( garment.BoneRotations[b] ) );
			shifts[i] = stock.BonePositions[s] - Quat.Rotate( turns[i], garment.BonePositions[b] );
			if ( !used[i] ) continue;
			off |= 2f * MathF.Acos( Math.Clamp( MathF.Abs( turns[i][3] ), 0f, 1f ) ) > BindTurnSlack || (Quat.Rotate( turns[i], garment.BonePositions[i] ) + shifts[i] - garment.BonePositions[i]).Length() > BindMoveSlack;
			first ??= turns[i];
			float apart = MathF.Abs( first[0] * turns[i][0] + first[1] * turns[i][1] + first[2] * turns[i][2] + first[3] * turns[i][3] );
			oneTurn &= 2f * MathF.Acos( Math.Clamp( apart, 0f, 1f ) ) <= BindTurnSlack;
		}
		// Morphs are turned with the whole garment (BindTurn), so the bones have to agree on that.
		if ( !off || !oneTurn ) return false;

		var merged = new Vec3[garment.Positions.Length];
		for ( int v = 0; v < merged.Length; v++ )
		{
			var sum = Vec3.Zero;
			float total = 0;
			for ( int j = 0; j < 4; j++ )
			{
				int b = garment.BoneIndex[v * 4 + j];
				float w = garment.BoneWeight[v * 4 + j];
				if ( b < 0 || b >= bones || w <= 0 || turns[b] == null ) continue;
				sum += (Quat.Rotate( turns[b], garment.Positions[v] ) + shifts[b]) * w;
				total += w;
			}
			merged[v] = total > 0 ? sum / total : garment.Positions[v];
		}
		var skin = new TriMesh( stock.Positions, stock.Indices );
		if ( !LikePaint( Gaps( skin, garment.Indices, merged ) ) || LikePaint( Gaps( skin, garment.Indices, garment.Positions ) ) ) return false;

		for ( int v = 0; v < merged.Length; v++ )
		{
			garment.Positions[v] = merged[v];
			if ( garment.Normals != null && v < garment.Normals.Length ) garment.Normals[v] = Quat.Rotate( first, garment.Normals[v] );
		}
		for ( int i = 0; i < bones; i++ )
		{
			if ( turns[i] == null ) continue;
			garment.BonePositions[i] = Quat.Rotate( turns[i], garment.BonePositions[i] ) + shifts[i];
			garment.BoneRotations[i] = Quat.Multiply( turns[i], garment.BoneRotations[i] );
		}
		// Each bone moved by a little more or less than its parent: the local frames changed too.
		if ( garment.BoneLocalPositions != null )
			for ( int i = 0; i < bones; i++ )
			{
				int p = garment.BoneParents[i];
				if ( p < 0 ) { garment.BoneLocalPositions[i] = garment.BonePositions[i]; garment.BoneLocalRotations[i] = garment.BoneRotations[i]; continue; }
				var back = Quat.Inverse( garment.BoneRotations[p] );
				garment.BoneLocalPositions[i] = Quat.Rotate( back, garment.BonePositions[i] - garment.BonePositions[p] );
				garment.BoneLocalRotations[i] = Quat.Multiply( back, garment.BoneRotations[i] );
			}
		garment.BindTurn = first;
		return true;
	}

	// How far each vertex the triangles use is from a surface, smallest first.
	static List<float> Gaps( TriMesh surface, int[] tris, Vec3[] positions )
	{
		var seen = new HashSet<int>();
		var gaps = new List<float>();
		foreach ( int v in tris )
			if ( seen.Add( v ) ) gaps.Add( surface.Nearest( positions[v], out _, out _ ) );
		gaps.Sort();
		return gaps;
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

	const float WrapContact = 0.5f;   // a piece whose resting side faces out on average less than this goes all the way round the body
	const float WrapArea = 0.35f;     // or by area, less than this
	const float StretchRidge = 0.1f;  // how hard a piece stretched to a part of the body holds on to one scale every way, as a share of its spread
	const float MostStretch = 1.25f;  // and the most it is stretched one way more than another, either side of that scale

	/// <summary>
	/// Whether a solid goes all the way round a part of the body (a hat, glasses, a bracelet):
	/// the parts of it resting on the skin face out every way, not to one side as a sword's or a
	/// backpack's do. Counted by vertex, or by area: a watch's face is cut much finer than its
	/// strap, and counted by vertex it faced out the way its face does.
	/// </summary>
	static bool Wraps( List<int> idx, List<int> tris, Vec3[] pts, float[] gap0, Vec3[] away )
	{
		var meanAway = Vec3.Zero;
		int touching = 0;
		foreach ( int i in idx )
			if ( gap0[i] < Contact ) { meanAway += away[i]; touching++; }
		if ( touching < 8 ) return false;
		if ( (meanAway / touching).Length() < WrapContact ) return true;

		var area = new Dictionary<int, float>();
		for ( int t = 0; t + 2 < tris.Count; t += 3 )
		{
			float a = Vec3.Cross( pts[tris[t + 1]] - pts[tris[t]], pts[tris[t + 2]] - pts[tris[t]] ).Length();
			for ( int c = 0; c < 3; c++ ) area[tris[t + c]] = (area.TryGetValue( tris[t + c], out float have ) ? have : 0) + a;
		}
		var byArea = Vec3.Zero;
		float total = 0;
		foreach ( int i in idx )
			if ( gap0[i] < Contact && area.TryGetValue( i, out float a ) ) { byArea += away[i] * a; total += a; }
		return total > 0 && (byArea / total).Length() < WrapArea;
	}

	/// <param name="keepGaps">Also restore the gap each vertex had over the skin, the way cloth does. For loose pieces like hair cards.</param>
	/// <param name="wraps">Whether the piece goes all the way round the body (<see cref="Wraps"/>), and so grows with it.</param>
	/// <param name="crown">
	/// For a hat or a helmet (<see cref="OnTheCrown"/>), the height of the eyes on the stock body,
	/// NaN for anything else. A hat takes the shape of the head it goes round, stretched along each
	/// way as far as the skin under it went that way and bent to the crown (<see cref="Reshape"/>).
	/// A hat on a head deeper from front to back than the stock one: grown the same every way, its
	/// front and back cut into the forehead and the back of the head, and the push out of them
	/// lifted the whole of it until its band sat on the top of the head, 2.5 cm higher than where
	/// a human's version of the same cap sits.
	/// </param>
	static void FitRigid( List<int> idx, Vec3[] pts, Vec3[] move, float[] gap0, Vec3[] away, Vec3[] fitted, PushTest push, bool keepGaps, bool wraps, float crown )
	{
		bool shaped = !float.IsNaN( crown );
		// Trust the vertices that actually rest on the body. The far end of a sword says
		// nothing about how the back under it moved.
		var w = new float[idx.Count];
		float total = 0;
		for ( int k = 0; k < idx.Count; k++ )
		{
			w[k] = MathF.Exp( -MathF.Max( gap0[idx[k]], 0 ) / Contact );
			total += w[k];
		}

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

		Vec3 Turned( Vec3 p ) => new( rotation[0] * p.X + rotation[1] * p.Y + rotation[2] * p.Z, rotation[3] * p.X + rotation[4] * p.Y + rotation[5] * p.Z, rotation[6] * p.X + rotation[7] * p.Y + rotation[8] * p.Z );

		// Stretched along each way of the body's frame (forward, sideways, up) by as much as the
		// skin under it went, turned first: a least squares scale per way, held to the one scale
		// for ways the piece hardly spreads along (a pair of goggles is flat top to bottom).
		var stretch = new Vec3( scale, scale, scale );
		if ( rotation != null && shaped && spread > 1e-12f )
		{
			Vec3 moved = Vec3.Zero, square = Vec3.Zero;
			for ( int k = 0; k < idx.Count; k++ )
			{
				Vec3 p = Turned( pts[idx[k]] - sourceMean ), q = pts[idx[k]] + move[idx[k]] - targetMean;
				float f = w[k] / total;
				moved += new Vec3( q.X * p.X, q.Y * p.Y, q.Z * p.Z ) * f;
				square += new Vec3( p.X * p.X, p.Y * p.Y, p.Z * p.Z ) * f;
			}
			float ridge = StretchRidge * spread / 3f;
			float Way( float m, float s ) => Math.Clamp( (m + ridge * scale) / (s + ridge), scale / MostStretch, scale * MostStretch );
			stretch = new Vec3( Way( moved.X, square.X ), Way( moved.Y, square.Y ), Way( moved.Z, square.Z ) );
		}

		foreach ( int i in idx )
		{
			Vec3 p = pts[i] - sourceMean;
			if ( rotation != null )
			{
				p = Turned( p );
				p = new Vec3( p.X * stretch.X, p.Y * stretch.Y, p.Z * stretch.Z );
			}
			fitted[i] = targetMean + p;
		}

		if ( shaped ) Reshape( idx, pts, move, gap0, away, w, fitted, push, crown );
		PushOut( idx, fitted, gap0, away, push, keepGaps );
	}

	const float CrownShare = 0.8f;    // a solid round the head with this much of what rests on it above the eyes (by area) is a hat or a helmet

	/// <summary>
	/// Whether a solid round the head rests on its crown, the skin above the eyes, for the most
	/// part: a hat, a helmet, a costume head. Not a cigar, rolled round in the mouth, nor a mask.
	/// </summary>
	static bool OnTheCrown( List<int> idx, List<int> tris, Vec3[] pts, float[] gap0, float crown )
	{
		// By area: a strap under the chin is finely cut and has as many vertices as a crown.
		var area = new Dictionary<int, float>();
		for ( int t = 0; t + 2 < tris.Count; t += 3 )
		{
			float a = Vec3.Cross( pts[tris[t + 1]] - pts[tris[t]], pts[tris[t + 2]] - pts[tris[t]] ).Length();
			for ( int c = 0; c < 3; c++ ) area[tris[t + c]] = (area.TryGetValue( tris[t + c], out float have ) ? have : 0) + a;
		}
		float touching = 0, above = 0;
		foreach ( int i in idx )
		{
			if ( gap0[i] >= Contact || !area.TryGetValue( i, out float a ) ) continue;
			touching += a;
			if ( pts[i].Z >= crown ) above += a;
		}
		return touching > 0 && above >= CrownShare * touching;
	}

	/// <summary>
	/// Whether a solid is in front of the nose or the mouth (<see cref="Repose.Face"/>): a helmet
	/// with a visor or a chin guard, a costume head. Those go on by the face they cover as a whole,
	/// not bent to the crown like a hat: bent to it, a frog's head had its mouth 4 cm off the face's.
	/// </summary>
	static bool OverTheFace( List<int> tris, Vec3[] pts, (Vec3 Stock, Vec3 Body)[] face )
	{
		if ( face == null || face.Length != 4 || tris.Count == 0 ) return false;
		var solid = new TriMesh( pts, tris.ToArray() );
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int k = 1; k <= 2; k++ )
		{
			solid.Crossings( face[k].Stock, new Vec3( 1, 0, 0 ), FaceCover, hits );
			if ( hits.Count > 0 ) return true;
		}
		return false;
	}

	const float ReshapeReach = 0.015f * Units.Metre;   // how far round a bit of a solid resting on the skin the skin's own move carries it

	/// <summary>
	/// A solid that goes round a part of the body, made to the shape of the stock body there,
	/// bent to the shape of this one: where it rests on the skin it goes where that skin went,
	/// over and above the one move and stretch of the whole of it, and the rest of it (a brim, a
	/// crown standing off the head) goes the way of the resting parts nearest to it. A human's
	/// forehead stands out 4 cm further than the stock one's at the brow and no further at the
	/// crown; a cap moved and stretched as a whole met it with its band and was lifted off it.
	/// The skin's moves are smooth over a head, and so is the bend: what is sewn or riveted on
	/// moves with the hat under it.
	/// </summary>
	/// <param name="weight">Per vertex of <paramref name="idx"/>, how much it rests on the skin (FitRigid's).</param>
	static void Reshape( List<int> idx, Vec3[] pts, Vec3[] move, float[] gap0, Vec3[] away, float[] weight, Vec3[] fitted, PushTest push, float crown )
	{
		var resting = new List<(Vec3 At, Vec3 Off, float Weight)>();
		for ( int k = 0; k < idx.Count; k++ )
		{
			int i = idx[k];
			if ( gap0[i] >= Contact || pts[i].Z < crown ) continue;
			resting.Add( (pts[i], pts[i] + move[i] - fitted[i], weight[k]) );
		}
		if ( resting.Count == 0 ) return;

		var bend = new Vec3[idx.Count];
		float reach2 = ReshapeReach * ReshapeReach;
		for ( int k = 0; k < idx.Count; k++ )
		{
			var sum = Vec3.Zero;
			float total = 0;
			foreach ( var (at, off, w) in resting )
			{
				// Falls off with distance, slowly enough that a brim far out in front still goes
				// the way of the band it is sewn to rather than of nothing.
				float d2 = (pts[idx[k]] - at).LengthSquared() / reach2;
				float f = w / (1f + d2 * d2);
				sum += off * f;
				total += f;
			}
			if ( total > 0 ) bend[k] = sum / total;
		}
		for ( int k = 0; k < idx.Count; k++ ) fitted[idx[k]] += bend[k];

		// What still cuts into the skin is pushed out where it does, and the solid round it
		// with it, less and less further off: pushed out as a whole, a band cutting into a
		// forehead lifted the whole hat.
		var digs = new List<(Vec3 At, Vec3 Push)>();
		for ( int round = 0; round < PushRounds; round++ )
		{
			digs.Clear();
			foreach ( int i in idx )
				if ( pts[i].Z >= crown && push( fitted[i], gap0[i] < Contact ? gap0[i] : MathF.Min( gap0[i], 0 ), away[i], out var p ) ) digs.Add( (pts[i], p) );
			if ( digs.Count == 0 ) break;
			for ( int k = 0; k < idx.Count; k++ )
			{
				var best = Vec3.Zero;
				foreach ( var (at, p) in digs )
				{
					float x = (pts[idx[k]] - at).Length() / (2f * ReshapeReach);
					if ( x >= 1 ) continue;
					var lift = p * (1 - x * x * (3 - 2 * x));
					if ( lift.LengthSquared() > best.LengthSquared() ) best = lift;
				}
				bend[k] = best;
			}
			for ( int k = 0; k < idx.Count; k++ ) fitted[idx[k]] += bend[k];
		}
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

	const float EyewearFront = 0.01f * Units.Metre;   // what of a piece of eyewear is no further back than this behind the eyes is its front
	const float RoundGrowth = 1.5f;                    // a band round the head is let out at most this much at the back to clear it
	const float RoundStep = 0.04f;
	const float RoundRamp = 0.04f * Units.Metre;       // and is let out in full from this far behind its front

	/// <summary>
	/// The middle of each eye's opening (<see cref="FaceMarks.Outline"/>) on the stock body and on
	/// this one, in the order of <paramref name="eyes"/>, or the fronts of the eyes when the
	/// openings weren't found on both faces. The front of an eye is what its bone drives, and on
	/// an anime face that is the iris painted high in a tall opening: Akira's is 10 mm over the
	/// middle of his eye, Aurora's 6 mm, a man's cornea 3 mm. Lenses go on the middle of the opening.
	/// </summary>
	static (Vec3 Stock, Vec3 Body)[] EyeMiddles( (Vec3 Stock, Vec3 Body)[] eyes, List<(string Name, Vec3 Stock, Vec3 Body)> features )
	{
		if ( features == null || eyes.Length != 2 ) return eyes;
		int left = eyes[0].Stock.Y >= eyes[1].Stock.Y ? 0 : 1;
		var middles = new (Vec3 Stock, Vec3 Body)[2];
		for ( int e = 0; e < 2; e++ )
		{
			string side = e == left ? "eyeL" : "eyeR";
			Vec3 stock = Vec3.Zero, body = Vec3.Zero;
			int count = 0;
			foreach ( var (name, at, there) in features )
			{
				if ( !name.StartsWith( side ) || name.Length == side.Length || name[side.Length] != '.' ) continue;
				stock += at; body += there; count++;
			}
			if ( count != FaceMarks.Outline.Length ) return eyes;
			middles[e] = (stock / count, body / count);
		}
		return middles;
	}

	const float EyewearLeast = 0.7f;   // eyewear is sized by the eyes' spacing, to no less than this
	const float EyewearMost = 1.4f;    // and no more than this

	/// <summary>
	/// Moves eyewear onto this body's eyes the way it sat on the stock body's, sized to their
	/// spacing: the lenses' middles onto the eyes' middles, as far in front of them as they were
	/// made to be. The skin it rests on (nose bridge, temples) doesn't say where the eyes are; on
	/// a face with eyes set lower the lenses ended up on the brows (reading glasses 3 cm over
	/// Aurora's eyes), and grown with the whole head, a pair of VR glasses came out a quarter
	/// bigger on her though her eyes are only 7% further apart than a man's.
	/// What goes on the eyes is the front of it, the part level with them or ahead: of an
	/// eyepatch on a string round the head most of the piece is string, and put by the middle
	/// of all of it, the patch rode down to the mouth once the fit had tipped the loop to follow
	/// a face lower on the head. The turn is the fit's. Whatever it still cuts into, the push out
	/// of the skin takes care of, and the band behind the front goes round the head on its own
	/// (<see cref="GoRound"/>).
	/// </summary>
	/// <param name="eyes">The middles of the eyes (<see cref="EyeMiddles"/>).</param>
	/// <returns>The middle of the front, where it is now, as far forward as the back of the front.</returns>
	static Vec3 PutOnTheEyes( List<int> idx, Vec3[] pts, Vec3[] fitted, (Vec3 Stock, Vec3 Body)[] eyes )
	{
		Vec3 stockMid = 0.5f * (eyes[0].Stock + eyes[1].Stock), bodyMid = 0.5f * (eyes[0].Body + eyes[1].Body);
		float ratio = EyeRatio( eyes );
		float level = MathF.Min( eyes[0].Stock.X, eyes[1].Stock.X ) - EyewearFront;
		var front = new List<int>();
		foreach ( int i in idx ) if ( pts[i].X >= level ) front.Add( i );
		if ( front.Count == 0 ) front = idx;
		var at = PutBy( idx, front, pts, fitted, stockMid, bodyMid, ratio, idx );
		foreach ( int i in front ) at.X = MathF.Min( at.X, fitted[i].X );
		return at;
	}

	/// <summary>How many times as far apart this body's eyes are as the stock body's, within what eyewear is sized by.</summary>
	static float EyeRatio( (Vec3 Stock, Vec3 Body)[] eyes )
	{
		float apart = (eyes[1].Stock - eyes[0].Stock).Length();
		return apart > 1e-4f * Units.Metre ? Math.Clamp( (eyes[1].Body - eyes[0].Body).Length() / apart, EyewearLeast, EyewearMost ) : 1f;
	}

	/// <summary>
	/// Puts some of a solid where a mark of the stock face went on this one: the middle of
	/// <paramref name="anchor"/> as made goes as far from <paramref name="to"/> as it was from
	/// <paramref name="from"/>, turned the way the fit turned the solid and scaled by
	/// <paramref name="ratio"/>, and <paramref name="idx"/> goes with it at that size.
	/// </summary>
	/// <param name="whole">The whole solid, what the fit's turn and scale are read from.</param>
	/// <returns>Where the anchor is now.</returns>
	static Vec3 PutBy( List<int> idx, List<int> anchor, Vec3[] pts, Vec3[] fitted, Vec3 from, Vec3 to, float ratio, List<int> whole )
	{
		var made = Centre( anchor, pts );
		Similarity( whole, pts, fitted, out var centre, out var placed, out var turn, out float scale );
		Vec3 Turned( Vec3 v ) => turn == null ? v : new( turn[0] * v.X + turn[1] * v.Y + turn[2] * v.Z, turn[3] * v.X + turn[4] * v.Y + turn[5] * v.Z, turn[6] * v.X + turn[7] * v.Y + turn[8] * v.Z );
		Vec3 now = turn == null ? Centre( anchor, fitted ) : placed + Turned( made - centre ) * scale;
		Vec3 want = to + Turned( made - from ) * ratio;
		float resize = turn == null ? 1f : ratio / scale;
		foreach ( int i in idx ) fitted[i] = want + (fitted[i] - now) * resize;
		return want;
	}

	const float FaceCover = 0.08f * Units.Metre;   // a solid this close in front of a mark of the face covers it
	const float FaceTopCover = 0.5f;               // a solid over this much of the top of the head is a helmet, not a mask
	const float FaceTopAbove = 0.03f * Units.Metre; // the top of the head: skin this far above the eyes and higher

	/// <summary>
	/// A solid worn on the face: in front of the nose or the mouth (a respirator, a mask), and
	/// leaving the top of the head bare but for its straps. A helmet with a visor covers the
	/// face too, but it is fitted by the head it goes round. A tube pulled up over the nose from
	/// the neck (a gaiter) is worn on the face too: fitted by the neck it goes round, its top
	/// edge came out under Aurora's nose with her cheeks through its sides. Its tube goes round
	/// the neck the way a strap goes round the head (<see cref="LetOutBand"/>).
	/// </summary>
	/// <param name="tris">The solid's triangles.</param>
	/// <param name="marks">Which of the face marks (<see cref="Repose.Face"/>) it covers.</param>
	static bool OnTheFace( List<int> tris, Vec3[] pts, TriMesh old, (Vec3 Stock, Vec3 Body)[] eyes, (Vec3 Stock, Vec3 Body)[] face, out bool[] marks )
	{
		marks = new bool[face.Length];
		if ( tris.Count == 0 ) return false;
		var solid = new TriMesh( pts, tris.ToArray() );
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int k = 0; k < face.Length; k++ )
		{
			solid.Crossings( face[k].Stock, new Vec3( 1, 0, 0 ), FaceCover, hits );
			marks[k] = hits.Count > 0;
		}
		if ( !marks[1] && !marks[2] ) return false;

		// How much of the top of the head it is over, straight out of the skin.
		var outward = new Vec3[old.Verts.Length];
		for ( int t = 0; t < old.TriCount; t++ )
			for ( int c = 0; c < 3; c++ ) outward[old.Tris[t * 3 + c]] += old.TriNormals[t];
		float top = MathF.Max( eyes[0].Stock.Z, eyes[1].Stock.Z ) + FaceTopAbove;
		int skin = 0, under = 0;
		for ( int v = 0; v < old.Verts.Length; v++ )
		{
			float length = outward[v].Length();
			if ( old.Verts[v].Z < top || length < 1e-12f ) continue;
			skin++;
			solid.Crossings( old.Verts[v], outward[v] / length, FaceCover, hits );
			if ( hits.Count > 0 ) under++;
		}
		return skin > 0 && under < FaceTopCover * skin;
	}

	/// <summary>
	/// Puts a solid worn on the face onto this body's face the way it sat on the stock one, by
	/// the marks of the face it covers (between the eyes, the nose tip, the mouth, the chin), and
	/// sizes it to the face. The skin it rests on doesn't say where those are: an anime face has
	/// its eyes 4 cm lower, a nose that hardly stands out and the mouth near the chin, so a
	/// respirator fitted by the skin under it sat with its cup off the nose and its bottom under
	/// the chin, at the size of the head its straps go round. Its turn stays the fit's.
	/// </summary>
	static void PutOnTheFace( List<int> idx, Vec3[] pts, Vec3[] fitted, (Vec3 Stock, Vec3 Body)[] eyes, (Vec3 Stock, Vec3 Body)[] face, bool[] marks )
	{
		// The face's size: its height from the eyes to the chin and its width between the eyes.
		float stockHeight = (face[0].Stock - face[3].Stock).Length(), bodyHeight = (face[0].Body - face[3].Body).Length();
		float stockWidth = (eyes[1].Stock - eyes[0].Stock).Length(), bodyWidth = (eyes[1].Body - eyes[0].Body).Length();
		if ( stockHeight < 1e-4f * Units.Metre || stockWidth < 1e-4f * Units.Metre ) return;
		float ratio = Math.Clamp( MathF.Sqrt( bodyHeight / stockHeight * (bodyWidth / stockWidth) ), 0.5f, 2f );

		Vec3 stockMid = Vec3.Zero, bodyMid = Vec3.Zero;
		int count = 0;
		for ( int k = 0; k < face.Length; k++ )
		{
			if ( !marks[k] ) continue;
			stockMid += face[k].Stock; bodyMid += face[k].Body; count++;
		}
		stockMid /= count;
		bodyMid /= count;

		// Where the fit took the stock marks, and how big it made the solid: it is moved and
		// turned as a whole, so one turn and scale carry every point of it.
		Similarity( idx, pts, fitted, out var from, out var to, out var turn, out float scale );
		if ( turn == null ) return;
		var d = stockMid - from;
		var anchor = to + new Vec3( turn[0] * d.X + turn[1] * d.Y + turn[2] * d.Z, turn[3] * d.X + turn[4] * d.Y + turn[5] * d.Z, turn[6] * d.X + turn[7] * d.Y + turn[8] * d.Z ) * scale;
		foreach ( int i in idx ) fitted[i] = bodyMid + (fitted[i] - anchor) * (ratio / scale);
	}

	const float BandBehind = 0.07f * Units.Metre;   // a piece reaching this far behind the eyes goes round the head
	const int BandSmoothing = 8;                    // passes of evening out the strap's move along it

	/// <summary>
	/// The straps of a solid worn on the face, put onto a head bigger for its face than the stock
	/// one (Aurora's: the respirator sized to her face had its straps through her skull and her
	/// ears). The front stays where it was put. A piece that goes round the head is let out
	/// behind the front where it cuts into the head, the way an elastic strap stretches over a
	/// bigger head: each bit of it as far as keeps the room it had over the stock skin, the push
	/// spread along the strap so it bends smoothly from where it leaves the front. Pieces that
	/// don't go round the head (the filters on the sides of a respirator) keep their shape.
	/// </summary>
	/// <returns>The vertices of the strap, the ones that were let out.</returns>
	/// <param name="chin">The stock body's chin. A tube pulled up from the neck over the nose (a gaiter), a piece that goes round the back of the neck under it, goes round the neck under there the way it goes round the head behind the front: put on by the face, a gaiter's tube came out sized to the face round a neck half as thick, and stood 2 cm off it.</param>
	static List<int> LetOutBand( List<int> idx, Vec3[] pts, Vec3[] move, Vec3[] fitted, float[] gap0, Vec3[] away, PushTest push, List<(int A, int B)> edges, int[] piece, (Vec3 Stock, Vec3 Body)[] eyes, Vec3 chin )
	{
		float level = MathF.Min( eyes[0].Stock.X, eyes[1].Stock.X ) - EyewearFront;
		float neck = chin.Z - BelowChin;
		var reach = new Dictionary<int, float>();
		var roundNeck = new HashSet<int>();
		foreach ( int i in idx )
		{
			reach[piece[i]] = reach.TryGetValue( piece[i], out float x ) ? MathF.Min( x, pts[i].X ) : pts[i].X;
			if ( pts[i].Z < neck && pts[i].X < chin.X - BandBehind ) roundNeck.Add( piece[i] );
		}
		var free = new Dictionary<int, bool>();
		foreach ( int i in idx ) free[i] = (pts[i].X < level || (pts[i].Z < neck && roundNeck.Contains( piece[i] ))) && reach[piece[i]] < level - BandBehind;

		var shift = new Dictionary<int, Vec3>();
		var sum = new Dictionary<int, Vec3>();
		var count = new Dictionary<int, int>();

		// Evens a move out along the strap. The front doesn't move, so the strap leaves it smoothly.
		void Spread( int passes )
		{
			for ( int pass = 0; pass < passes; pass++ )
			{
				sum.Clear(); count.Clear();
				foreach ( var (a, b) in edges )
				{
					if ( !shift.ContainsKey( a ) || !shift.ContainsKey( b ) ) continue;
					sum[a] = (sum.TryGetValue( a, out var sa ) ? sa : Vec3.Zero) + shift[b]; count[a] = (count.TryGetValue( a, out int ca ) ? ca : 0) + 1;
					sum[b] = (sum.TryGetValue( b, out var sb ) ? sb : Vec3.Zero) + shift[a]; count[b] = (count.TryGetValue( b, out int cb ) ? cb : 0) + 1;
				}
				foreach ( int i in idx )
					if ( free[i] && count.TryGetValue( i, out int c ) && c > 0 ) shift[i] = 0.5f * shift[i] + 0.5f * sum[i] / c;
			}
		}

		// Behind the front the strap goes the way the skin under it went, the way cloth does,
		// over the ears where it went over the stock ones. It leaves the front in a ramp.
		foreach ( int i in idx )
		{
			float w = free[i] ? Math.Clamp( MathF.Max( level - pts[i].X, roundNeck.Contains( piece[i] ) ? neck - pts[i].Z : 0f ) / RoundRamp, 0f, 1f ) : 0f;
			shift[i] = (pts[i] + move[i] - fitted[i]) * w;
		}
		Spread( BandSmoothing );
		foreach ( int i in idx )
		{
			fitted[i] += shift[i];
			shift[i] = Vec3.Zero;
		}

		for ( int round = 0; round < PushRounds; round++ )
		{
			bool any = false;
			foreach ( int i in idx )
			{
				if ( !free[i] || !push( fitted[i] + shift[i], gap0[i], away[i], out var p ) ) continue;
				shift[i] += p;
				any = true;
			}
			if ( !any ) break;
			Spread( 2 );
		}
		// A push goes last, so nothing is left in the head.
		var band = new List<int>();
		foreach ( int i in idx )
		{
			if ( !free[i] ) continue;
			if ( push( fitted[i] + shift[i], gap0[i], away[i], out var p ) ) shift[i] += p;
			fitted[i] += shift[i];
			band.Add( i );
		}
		return band;
	}

	static Vec3 Centre( List<int> idx, Vec3[] p )
	{
		var sum = Vec3.Zero;
		foreach ( int i in idx ) sum += p[i];
		return sum / idx.Count;
	}

	/// <summary>
	/// The turn (row-major) and scale that take a solid as made to where it is: to + turn * (p - from) * scale.
	/// Null turn if the points are too flat to say.
	/// </summary>
	static void Similarity( List<int> idx, Vec3[] pts, Vec3[] fitted, out Vec3 from, out Vec3 to, out float[] turn, out float scale )
	{
		from = Centre( idx, pts );
		to = Centre( idx, fitted );
		var h = new float[9];
		float spread = 0;
		foreach ( int i in idx )
		{
			Vec3 p = pts[i] - from, q = fitted[i] - to;
			h[0] += q.X * p.X; h[1] += q.X * p.Y; h[2] += q.X * p.Z;
			h[3] += q.Y * p.X; h[4] += q.Y * p.Y; h[5] += q.Y * p.Z;
			h[6] += q.Z * p.X; h[7] += q.Z * p.Y; h[8] += q.Z * p.Z;
			spread += p.LengthSquared();
		}
		turn = spread > 1e-12f ? Rotation( h ) : null;
		scale = 1f;
		if ( turn == null ) return;
		float trace = 0;
		for ( int k = 0; k < 9; k++ ) trace += turn[k] * h[k];
		scale = trace / spread;
		if ( scale < 1e-3f ) turn = null;
	}

	const float RoundLeast = 0.7f;                     // and taken in at most this much where it stood off the head
	const float HugSlack = 0.001f * Units.Metre;       // a band taken in may come this much closer to the head than it was made to be
	const float HugDig = 30f;                          // and coming closer than that counts this many times as much as standing off: the push out of the head afterwards moves the whole of it, the front off the eyes
	const int HugSamples = 400;                        // at most this many of a band's vertices are tried against the head

	/// <summary>
	/// The band round the head behind the front of a piece of eyewear (the string of an eyepatch,
	/// the strap of a pair of goggles, the arms of a pair of glasses), once the front has gone onto
	/// the eyes. The fit grew the whole piece with the head, so behind the front the band cuts
	/// into a skull bigger for its face than the stock one, or stands off one smaller: grown with
	/// Aurora's head, an eyepatch's string went round it as a wide oval, a finger's width off it.
	/// Pushed out as a whole, a loop slides down the head and takes the front off the eyes. So
	/// the band is let out or taken in the way a string would stretch: the front stays on the
	/// eyes, and behind it the band is widened and deepened, or narrowed and shortened, more the
	/// further back it goes, to keep as far off this head as it was made to keep off the stock
	/// one, as near as any size gets.
	/// </summary>
	static void GoRound( List<int> idx, Vec3 front, (Vec3 Stock, Vec3 Body)[] eyes, Vec3[] fitted, float[] gap0, Vec3[] away, TriMesh body, PushTest push )
	{
		var origin = new Vec3( front.X, 0.5f * (eyes[0].Body.Y + eyes[1].Body.Y), front.Z );
		var placed = new Vec3[idx.Count];
		float back = 0;
		for ( int k = 0; k < idx.Count; k++ )
		{
			placed[k] = fitted[idx[k]];
			back = MathF.Max( back, origin.X - placed[k].X );
		}
		if ( back <= 1e-4f * Units.Metre ) return;

		float ramp = MathF.Min( back, RoundRamp );
		Vec3 LetOut( Vec3 p, float deeper, float wider )
		{
			var d = p - origin;
			float f = Math.Clamp( -d.X / ramp, 0f, 1f );
			return origin + new Vec3( d.X * (1f + (deeper - 1f) * f), d.Y * (1f + (wider - 1f) * f), d.Z );
		}

		// What is behind the front: only what letting out moves, the front can be in the face for
		// other reasons (lashes). Not what was made deep in the head.
		var resting = new List<int>();
		for ( int k = 0; k < idx.Count; k++ )
			if ( placed[k].X < origin.X - 0.5f * ramp && gap0[idx[k]] >= -Hidden ) resting.Add( k );
		if ( resting.Count == 0 ) return;
		int stride = Math.Max( 1, resting.Count / HugSamples );
		// How far each comes off the gap it was made with over the head: standing further off it,
		// or, counted HugDig times over, closer to it or into it.
		float Miss( float deeper, float wider )
		{
			float miss = 0;
			for ( int r = 0; r < resting.Count; r += stride )
			{
				int k = resting[r];
				float want = MathF.Min( gap0[idx[k]], GapCap ), gap = body.SignedGap( LetOut( placed[k], deeper, wider ), out _ );
				miss += gap > want ? MathF.Min( gap - want, GapCap ) : HugDig * MathF.Max( 0, want - gap - HugSlack );
			}
			return miss;
		}

		// Deeper and wider each on its own: the arms of a pair of glasses round a wider head only
		// need to go wider, not longer. The size that misses least, and of those the nearest to
		// as it was: on the head it was made for, as it was.
		(float Deeper, float Wider) best = (1f, 1f);
		float smallest = float.MaxValue;
		int fewest = (int)MathF.Ceiling( (RoundLeast - 1f) / RoundStep - 1e-3f ), most = (int)MathF.Floor( (RoundGrowth - 1f) / RoundStep + 1e-3f );
		for ( int a = fewest; a <= most; a++ )
			for ( int b = fewest; b <= most; b++ )
			{
				float deeper = 1f + a * RoundStep, wider = 1f + b * RoundStep;
				float score = Miss( deeper, wider ) + HugSlack * (MathF.Abs( deeper - 1f ) + MathF.Abs( wider - 1f ));
				if ( score < smallest ) { smallest = score; best = (deeper, wider); }
			}
		for ( int k = 0; k < idx.Count; k++ ) fitted[idx[k]] = LetOut( placed[k], best.Deeper, best.Wider );
	}

	const float UnderEyes = 0.03f * Units.Metre;   // cloth in front of the face this far under the eyes is over the face

	// Whether a piece of cloth reaches up over the top of the head.
	static bool FromTheHead( List<int> idx, Vec3[] pts, (Vec3 Stock, Vec3 Body)[] eyes )
	{
		if ( eyes == null || eyes.Length != 2 ) return false;
		float eyeZ = MathF.Min( eyes[0].Stock.Z, eyes[1].Stock.Z );
		foreach ( int i in idx ) if ( pts[i].Z > eyeZ + EyewearHeight ) return true;
		return false;
	}

	// Whether a piece of cloth goes over the face, in front of it under the eyes between them,
	// and over the top of the head: worn on the head, pulled down over the face.
	static bool OverFaceAndNeck( List<int> idx, Vec3[] pts, (Vec3 Stock, Vec3 Body)[] eyes )
	{
		if ( eyes == null || eyes.Length != 2 ) return false;
		float level = MathF.Min( eyes[0].Stock.X, eyes[1].Stock.X ) - EyewearFront;
		float eyeZ = MathF.Min( eyes[0].Stock.Z, eyes[1].Stock.Z ), midY = 0.5f * (eyes[0].Stock.Y + eyes[1].Stock.Y);
		float half = 0.5f * MathF.Abs( eyes[0].Stock.Y - eyes[1].Stock.Y );
		bool face = false, top = false;
		foreach ( int i in idx )
		{
			var p = pts[i];
			face |= p.X >= level && p.Z < eyeZ - UnderEyes && MathF.Abs( p.Y - midY ) < half;
			top |= p.Z > eyeZ + EyewearHeight;
			if ( face && top ) return true;
		}
		return false;
	}

	const int UnfoldRounds = 6;   // rounds of halving a move that turned cloth over

	/// <summary>
	/// Takes back, by halves, a move of the cloth that turned some of it over: where a triangle
	/// faces the other way after the move than before it, its corners go back half of the way
	/// to where they were, round after round. Taken in round a thin neck vertex by vertex, a
	/// balaclava's neck folded over on itself under Akira's chin and showed its inside.
	/// </summary>
	/// <param name="before">The move before it.</param>
	static void Unfold( List<(int Root, List<int> Idx)> cloth, Dictionary<int, List<int>> pieceTris, Vec3[] pts, Vec3[] before, Vec3[] move, Vec3[] fitted )
	{
		var turned = new HashSet<int>();
		for ( int round = 0; round < UnfoldRounds; round++ )
		{
			turned.Clear();
			foreach ( var (root, _) in cloth )
			{
				if ( !pieceTris.TryGetValue( root, out var mine ) ) continue;
				for ( int t = 0; t < mine.Count; t += 3 )
				{
					int a = mine[t], b = mine[t + 1], c = mine[t + 2];
					Vec3 pa = pts[a] + before[a], pb = pts[b] + before[b], pc = pts[c] + before[c];
					var was = Vec3.Cross( pb - pa, pc - pa );
					var now = Vec3.Cross( fitted[b] - fitted[a], fitted[c] - fitted[a] );
					if ( Vec3.Dot( was, now ) < 0 ) { turned.Add( a ); turned.Add( b ); turned.Add( c ); }
				}
			}
			if ( turned.Count == 0 ) return;
			foreach ( int i in turned )
			{
				move[i] = 0.5f * (move[i] + before[i]);
				fitted[i] = pts[i] + move[i];
			}
		}
	}

	const int NeckSectors = 12;   // the way round the neck in this many sectors
	const float NeckRound = 0.75f; // cloth over the neck in this share of them goes round it, else hanging from a hat it only crosses it

	const float EarFade = 0.02f * Units.Metre;    // skin this far round an ear goes partly the ear's way

	/// <summary>
	/// Cloth worn over the face (a balaclava, a cloth mask: in front of the nose or the mouth)
	/// goes onto it by the face's features, the way paint on the skin does (<see cref="BodyMap.OnSkin"/>):
	/// the edge of an eye hole round the stock eye's opening onto the line round this one's,
	/// the mouth onto the mouth. Carried by the skin under it, as other cloth is, the rays from
	/// the skull put the stock face's holes where an anime face has its forehead and its cheeks:
	/// a balaclava's eye holes came out as one slit across Aurora's eyes, their lower halves
	/// under the cloth. And round the ears it goes the way the ears went (<see cref="Repose.Ears"/>):
	/// the room it has for a man's ears onto Aurora's, 2 cm lower and a third narrower, that
	/// came through its side crooked.
	/// </summary>
	/// <returns>Where the stock skin went, as the cloth over the face goes by it (what tells the skin it covers, see <see cref="KeepOver"/>), or null if no cloth is over the face.</returns>
	static Vec3[] OnTheFaceCloth( Vec3[] pts, int[] tris, int[] weld, List<(int A, int B)> edges, int[] firstOf, int[] boneIndex, float[] boneWeight, TriMesh old, TriMesh body, Vec3[] skinAt, int[] skinTri, Vec3[] move, Vec3[] skinMove, Vec3[] onSkin, (Vec3 Stock, Vec3 Body)[] face, (Vec3 Stock, Vec3 Body)[] eyes, List<(string Name, Vec3 Stock, Vec3 Body)> ears, bool[] over )
	{
		if ( face == null || face.Length != 4 ) return null;
		int n = pts.Length;
		var island = Islands( n, edges );
		var islandTris = new Dictionary<int, List<int>>();
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			int a = weld[tris[t]], b = weld[tris[t + 1]], c = weld[tris[t + 2]];
			if ( a == b || b == c || a == c ) continue;
			if ( !islandTris.TryGetValue( island[a], out var list ) ) islandTris[island[a]] = list = new List<int>();
			list.Add( a ); list.Add( b ); list.Add( c );
		}
		var members = new Dictionary<int, List<int>>();
		for ( int i = 0; i < n; i++ )
		{
			if ( !members.TryGetValue( island[i], out var list ) ) members[island[i]] = list = new List<int>();
			list.Add( i );
		}

		// Glass over an eye, held in the cloth (the windows of a gas mask): such cloth keeps the
		// shape of the stock face round its windows, they don't stretch to an anime eye.
		var glass = new List<(Vec3 Lo, Vec3 Hi)>();
		foreach ( var (root, idx) in members )
		{
			if ( !IsRigid( idx, firstOf, boneIndex, boneWeight ) ) continue;
			var glo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var ghi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( int i in idx )
			{
				glo = new Vec3( MathF.Min( glo.X, pts[i].X ), MathF.Min( glo.Y, pts[i].Y ), MathF.Min( glo.Z, pts[i].Z ) );
				ghi = new Vec3( MathF.Max( ghi.X, pts[i].X ), MathF.Max( ghi.Y, pts[i].Y ), MathF.Max( ghi.Z, pts[i].Z ) );
			}
			if ( eyes == null || ghi.Z - glo.Z > EyewearHeight ) continue;
			foreach ( var (eye, _) in eyes )
				if ( eye.Y >= glo.Y && eye.Y <= ghi.Y && eye.Z >= glo.Z && eye.Z <= ghi.Z && ghi.X >= eye.X - EyewearFront ) { glass.Add( (glo, ghi) ); break; }
		}

		// The cloth over the face.
		var hits = new List<(float T, bool Leaving, int Tri)>();
		bool any = false;
		foreach ( var (root, idx) in members )
		{
			if ( !islandTris.TryGetValue( root, out var mine ) || IsRigid( idx, firstOf, boneIndex, boneWeight ) ) continue;
			var clo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var chi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( int i in idx )
			{
				clo = new Vec3( MathF.Min( clo.X, pts[i].X ), MathF.Min( clo.Y, pts[i].Y ), MathF.Min( clo.Z, pts[i].Z ) );
				chi = new Vec3( MathF.Max( chi.X, pts[i].X ), MathF.Max( chi.Y, pts[i].Y ), MathF.Max( chi.Z, pts[i].Z ) );
			}
			bool framesGlass = false;
			foreach ( var (glo, ghi) in glass )
				framesGlass |= glo.Y >= clo.Y && ghi.Y <= chi.Y && glo.Z >= clo.Z && ghi.Z <= chi.Z;
			if ( framesGlass ) continue;
			var cloth = new TriMesh( pts, mine.ToArray() );
			bool covers = false;
			for ( int k = 1; k <= 2 && !covers; k++ )
			{
				cloth.Crossings( face[k].Stock, new Vec3( 1, 0, 0 ), FaceCover, hits );
				covers = hits.Count > 0;
			}
			if ( !covers ) continue;
			foreach ( int i in idx ) over[i] = true;
			any = true;
		}
		if ( !any ) return null;

		// Where the stock skin goes: by the face's features, and round the ears by the ears.
		// Skin the stock face hides (under a lid, inside the lips) is put under this one's for
		// paint; cloth made over it goes by the skin's own move instead.
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		for ( int i = 0; i < n; i++ )
		{
			if ( !over[i] ) continue;
			lo = new Vec3( MathF.Min( lo.X, pts[i].X ), MathF.Min( lo.Y, pts[i].Y ), MathF.Min( lo.Z, pts[i].Z ) );
			hi = new Vec3( MathF.Max( hi.X, pts[i].X ), MathF.Max( hi.Y, pts[i].Y ), MathF.Max( hi.Z, pts[i].Z ) );
		}
		var field = new Vec3[old.Verts.Length];
		for ( int v = 0; v < field.Length; v++ )
		{
			field[v] = onSkin[v] - old.Verts[v];
			var p = old.Verts[v];
			if ( p.X < lo.X - GapCap || p.X > hi.X + GapCap || p.Y < lo.Y - GapCap || p.Y > hi.Y + GapCap || p.Z < lo.Z - GapCap || p.Z > hi.Z + GapCap ) continue;
			if ( body.SignedGap( onSkin[v], out _ ) < -0.5f * BodyMap.Buried ) field[v] = skinMove[v];
		}
		if ( ears != null )
		{
			foreach ( string side in new[] { "earL", "earR" } )
			{
				Vec3 middle = default, middleNow = default, canal = default, canalNow = default, top = default, topNow = default, bottom = default, bottomNow = default, front = default, frontNow = default, back = default, backNow = default;
				int found = 0;
				foreach ( var (name, at, there) in ears )
				{
					if ( name == side ) { middle = at; middleNow = there; found++; }
					else if ( name == side + ".canal" ) { canal = at; canalNow = there; found++; }
					else if ( name == side + ".top" ) { top = at; topNow = there; found++; }
					else if ( name == side + ".bottom" ) { bottom = at; bottomNow = there; found++; }
					else if ( name == side + ".front" ) { front = at; frontNow = there; found++; }
					else if ( name == side + ".back" ) { back = at; backNow = there; found++; }
				}
				if ( found < 6 ) continue;
				float tall = top.Z - bottom.Z, deep = front.X - back.X;
				if ( tall < 1e-4f * Units.Metre || deep < 1e-4f * Units.Metre ) continue;
				float taller = Math.Clamp( (topNow.Z - bottomNow.Z) / tall, EarLeast, EarMost );
				float deeper = Math.Clamp( (frontNow.X - backNow.X) / deep, EarLeast, EarMost );
				float sideways = Math.Sign( canal.Y );
				for ( int v = 0; v < field.Length; v++ )
				{
					var p = old.Verts[v];
					if ( (p.Y - canal.Y) * sideways < -EarFade ) continue;
					float outX = MathF.Max( 0, MathF.Max( back.X - p.X, p.X - front.X ) ), outZ = MathF.Max( 0, MathF.Max( bottom.Z - p.Z, p.Z - top.Z ) );
					float w = 1f - Math.Clamp( MathF.Sqrt( outX * outX + outZ * outZ ) / EarFade, 0f, 1f );
					if ( w <= 0 ) continue;
					w = w * w * (3 - 2 * w);
					var there = new Vec3( middleNow.X + (p.X - middle.X) * deeper, p.Y + canalNow.Y - canal.Y, middleNow.Z + (p.Z - middle.Z) * taller );
					field[v] = field[v] * (1 - w) + (there - p) * w;
				}
			}
		}

		for ( int i = 0; i < n; i++ )
		{
			if ( !over[i] || skinTri[i] < 0 ) continue;
			int ia = old.Tris[skinTri[i] * 3], ib = old.Tris[skinTri[i] * 3 + 1], ic = old.Tris[skinTri[i] * 3 + 2];
			Barycentric( skinAt[i], old.Verts[ia], old.Verts[ib], old.Verts[ic], out float u, out float v, out float w );
			move[i] = field[ia] * u + field[ib] * v + field[ic] * w;
		}
		return field;
	}

	const float OverReach = 0.005f * Units.Metre;  // skin out through the cloth over the face by up to this much is put back under it; deeper, it was matched to some other part of the face

	/// <summary>
	/// The skin of this face that comes out through the cloth over it: a chin more pointed than
	/// the stock one, a nose, between the cloth's vertices or past where the stock skin under them
	/// went (Akira's chin came out through the bottom of a balaclava by a millimetre or two). Each
	/// skin vertex with the cloth behind it and none in front lifts the triangle behind it out past
	/// it, by its corners' shares of the spot, the way <see cref="CoverSkin"/> lifts it over the
	/// stock skin.
	/// </summary>
	/// <param name="over">Per welded garment vertex, whether it is cloth over the face.</param>
	/// <param name="stock">The stock skin, and <paramref name="stockMove"/> where each of its vertices went: only skin where the stock skin it came from was under the cloth as made is kept under it, not an eye in an eye hole, nor what came through the cloth on the stock body already.</param>
	/// <param name="buried">Per welded vertex, whether it was made deep under the stock skin, never to be seen (a cap closing an eye hole inside the head): it is left there.</param>
	static void KeepOver( Vec3[] pts, Vec3[] fitted, List<int> clothTris, bool[] over, bool[] buried, TriMesh body, TriMesh stock, Vec3[] stockMove )
	{
		var faceTris = new List<int>();
		for ( int t = 0; t < clothTris.Count; t += 3 )
		{
			int a = clothTris[t], b = clothTris[t + 1], c = clothTris[t + 2];
			if ( over[a] && over[b] && over[c] && !buried[a] && !buried[b] && !buried[c] ) faceTris.AddRange( clothTris.GetRange( t, 3 ) );
		}
		if ( faceTris.Count == 0 ) return;
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( int i in faceTris )
		{
			lo = new Vec3( MathF.Min( lo.X, fitted[i].X ), MathF.Min( lo.Y, fitted[i].Y ), MathF.Min( lo.Z, fitted[i].Z ) );
			hi = new Vec3( MathF.Max( hi.X, fitted[i].X ), MathF.Max( hi.Y, fitted[i].Y ), MathF.Max( hi.Z, fitted[i].Z ) );
		}
		var normals = new Vec3[body.Verts.Length];
		for ( int t = 0; t < body.TriCount; t++ )
			for ( int c = 0; c < 3; c++ ) normals[body.Tris[t * 3 + c]] += body.TriNormals[t];
		var hits = new List<(float T, bool Leaving, int Tri)>();

		// The stock skin the cloth covered as made, where it went.
		var made = new TriMesh( pts, faceTris.ToArray() );
		var stockOut = new Vec3[stock.Verts.Length];
		for ( int t = 0; t < stock.TriCount; t++ )
			for ( int c = 0; c < 3; c++ ) stockOut[stock.Tris[t * 3 + c]] += stock.TriNormals[t];
		var covered = new List<Vec3>();
		var bare = new List<Vec3>();
		var madePoked = new HashSet<int>();
		for ( int v = 0; v < stock.Verts.Length; v++ )
		{
			var q = stock.Verts[v] + stockMove[v];
			if ( q.X < lo.X - OverReach || q.X > hi.X + OverReach || q.Y < lo.Y - OverReach || q.Y > hi.Y + OverReach || q.Z < lo.Z - OverReach || q.Z > hi.Z + OverReach || stockOut[v].LengthSquared() < 1e-20f ) continue;
			var up = stockOut[v] / stockOut[v].Length();
			if ( Poking( made, stock.Verts[v], up, hits ) )
			{
				// The stock skin came through here as made: so it may on this body.
				madePoked.Add( hits[0].Tri );
				bare.Add( q );
				continue;
			}
			made.Crossings( stock.Verts[v], up, GapCap, hits );
			(hits.Count > 0 ? covered : bare).Add( q );
		}
		bool Covered( Vec3 p )
		{
			float best = float.MaxValue;
			bool under = false;
			foreach ( var q in covered ) { float d = (q - p).LengthSquared(); if ( d < best ) { best = d; under = true; } }
			foreach ( var q in bare ) { float d = (q - p).LengthSquared(); if ( d < best ) { best = d; under = false; } }
			return under;
		}

		var skin = new List<int>();
		for ( int v = 0; v < body.Verts.Length; v++ )
		{
			var p = body.Verts[v];
			if ( p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y && p.Z >= lo.Z && p.Z <= hi.Z && normals[v].LengthSquared() > 1e-20f && Covered( p ) ) skin.Add( v );
		}
		var lift = new Dictionary<int, Vec3>();
		var tris = faceTris.ToArray();
		for ( int pass = 0; pass < 4; pass++ )
		{
			var cloth = new TriMesh( fitted, tris );
			lift.Clear();
			foreach ( int v in skin )
			{
				var p = body.Verts[v];
				var up = normals[v] / normals[v].Length();
				if ( !Poking( cloth, p, up, hits ) || madePoked.Contains( hits[0].Tri ) ) continue;
				int t = hits[0].Tri, a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
				Barycentric( p - up * hits[0].T, fitted[a], fitted[b], fitted[c], out float bu, out float bv, out float bw );
				var push = up * ((hits[0].T + ClearGap) / MathF.Max( bu * bu + bv * bv + bw * bw, 0.5f ));
				Lift( lift, a, push * MathF.Max( bu, 0 ) );
				Lift( lift, b, push * MathF.Max( bv, 0 ) );
				Lift( lift, c, push * MathF.Max( bw, 0 ) );
			}
			if ( lift.Count == 0 ) break;
			foreach ( var (vertex, push) in lift ) fitted[vertex] += push;
		}
	}

	// Whether skin at p, facing up, is out through the cloth: no cloth just over it, and the
	// outside of the cloth just behind it, facing out the way the skin does. What faces the other
	// way is cloth the skin is meant to be in front of: the lining turned in round an eye hole,
	// behind the lids. Leaves the hit behind it first in the list.
	static bool Poking( TriMesh cloth, Vec3 p, Vec3 up, List<(float T, bool Leaving, int Tri)> hits )
	{
		cloth.Crossings( p, up, OverReach, hits );
		if ( hits.Count > 0 ) return false;
		cloth.Crossings( p, -up, OverReach, hits );
		return hits.Count > 0 && Vec3.Dot( cloth.TriNormals[hits[0].Tri], up ) > 0;
	}

	const float EarItem = 0.12f * Units.Metre;    // a solid no bigger than this corner to corner, with an ear inside it, is worn on that ear
	const float EarRoom = 0.01f * Units.Metre;    // inside it, or this close
	const float EarLeast = 0.6f;                  // an ear is taken for no less than this many times the size of the stock one
	const float EarMost = 1.6f;                   // and no more than this

	/// <summary>
	/// Whether a small solid that doesn't go round anything is worn on an ear: the ear (<see
	/// cref="Repose.Ears"/>) is inside what it takes up. An earpiece, an earring, a pencil behind an ear.
	/// </summary>
	/// <param name="ear">Which ear: "earL" or "earR".</param>
	static bool OnAnEar( List<int> idx, Vec3[] pts, List<(string Name, Vec3 Stock, Vec3 Body)> ears, out string ear )
	{
		ear = null;
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( int i in idx )
		{
			lo = new Vec3( MathF.Min( lo.X, pts[i].X ), MathF.Min( lo.Y, pts[i].Y ), MathF.Min( lo.Z, pts[i].Z ) );
			hi = new Vec3( MathF.Max( hi.X, pts[i].X ), MathF.Max( hi.Y, pts[i].Y ), MathF.Max( hi.Z, pts[i].Z ) );
		}
		if ( (hi - lo).Length() > EarItem ) return false;
		foreach ( var (name, at, _) in ears )
		{
			if ( name != "earL" && name != "earR" ) continue;
			if ( at.X < lo.X - EarRoom || at.X > hi.X + EarRoom || at.Y < lo.Y - EarRoom || at.Y > hi.Y + EarRoom || at.Z < lo.Z - EarRoom || at.Z > hi.Z + EarRoom ) continue;
			ear = name;
			return true;
		}
		return false;
	}

	/// <summary>
	/// Moves something worn on an ear onto this body's ear the way it sat on the stock one's. The
	/// skull doesn't say where the ears are: Aurora's are 2 cm lower and a third narrower than a
	/// man's, and an earpiece put on by her skull sat 6 mm off the bowl of her ear (2 mm on a
	/// man's). What sits in or on the ear (the part of it within the ear's reach seen from the
	/// side) keeps where it was on the ear, as a share of the ear's height and depth, and as far
	/// out of the bowl in its middle; the solid goes along as it was fitted.
	/// </summary>
	static void PutOnTheEar( List<int> idx, Vec3[] pts, Vec3[] fitted, float[] gap0, TriMesh body, List<(string Name, Vec3 Stock, Vec3 Body)> ears, string ear )
	{
		(Vec3 Stock, Vec3 Body) Mark( string name )
		{
			foreach ( var (n, s, b) in ears ) if ( n == name ) return (s, b);
			return (default, default);
		}
		var (middle, middleNow) = Mark( ear );
		var (canal, canalNow) = Mark( ear + ".canal" );
		if ( canal.Y == 0 && canalNow.Y == 0 ) (canal, canalNow) = (middle, middleNow);
		var (top, topNow) = Mark( ear + ".top" );
		var (bottom, bottomNow) = Mark( ear + ".bottom" );
		var (front, frontNow) = Mark( ear + ".front" );
		var (back, backNow) = Mark( ear + ".back" );
		float tall = top.Z - bottom.Z, deep = front.X - back.X;
		if ( tall < 1e-4f * Units.Metre || deep < 1e-4f * Units.Metre ) return;
		float taller = Math.Clamp( (topNow.Z - bottomNow.Z) / tall, EarLeast, EarMost );
		float deeper = Math.Clamp( (frontNow.X - backNow.X) / deep, EarLeast, EarMost );

		// What of it is on the ear: within the ear's reach seen from the side.
		var on = new List<int>();
		foreach ( int i in idx )
			if ( pts[i].Z <= top.Z && pts[i].Z >= bottom.Z && pts[i].X <= front.X && pts[i].X >= back.X ) on.Add( i );
		if ( on.Count == 0 ) on = idx;
		var made = Centre( on, pts );
		var off = made - middle;
		var want = new Vec3( middleNow.X + off.X * deeper, canalNow.Y + made.Y - canal.Y, middleNow.Z + off.Z * taller );
		var shift = want - Centre( on, fitted );
		foreach ( int i in idx ) fitted[i] += shift;

		// An ear standing out from the side of the head at another angle leaves it off the ear:
		// in towards the head, until it comes as close to the skin as it was made to.
		float made0 = float.MaxValue;
		foreach ( int i in idx ) made0 = MathF.Min( made0, gap0[i] );
		var inward = new Vec3( 0, ear == "earL" ? -1 : 1, 0 );
		for ( int round = 0; round < EarSteps; round++ )
		{
			float now = float.MaxValue;
			foreach ( int i in idx ) now = MathF.Min( now, body.SignedGap( fitted[i], out _ ) );
			if ( now - made0 < EarSettle ) break;
			foreach ( int i in idx ) fitted[i] += inward * (now - made0);
		}
	}

	const int EarSteps = 4;                          // rounds of moving something worn on an ear in to the head
	const float EarSettle = 0.0005f * Units.Metre;   // until it is this close to as near as it was made

	const float GogglesRest = 0.01f * Units.Metre;  // a piece of a helmet in front of the eyes this close to the face rests on it, goggles; further off, the helmet holds it there, a visor
	const float MouthReach = 0.03f * Units.Metre;   // a piece of a hat with a vertex this close in front of the mouth is held to it: the end of a mic's boom
	const float ArmRest = 0.005f * Units.Metre;     // a piece as close as this to the stock skin rests on the head, it doesn't hang off the boom
	const float BoomLength = 0.15f * Units.Metre;  // a mic's boom with its tip is no longer than this corner to corner
	const float BoomLeast = 0.75f;                   // a boom is shortened to no less than this
	const float BoomMost = 1.33f;                    // and lengthened to no more than this

	/// <summary>
	/// The parts of a hat or a helmet that go with the face rather than the skull, put onto this
	/// face by its marks. The helmet goes on by the skull, and on a face whose eyes and mouth are
	/// set lower on the head (an anime face: Akira's eyes are 4 cm lower under the crown than a
	/// man's) whatever was made to sit on the face comes out above it: a tactical helmet's
	/// goggles sat on Akira's forehead with their bottom rim across the top of his eyes, and its
	/// mic ended at the side of his nose.
	///  - Goggles: a piece of it in front of the eyes, across one of them, no taller than
	///    eyewear and resting on the face (not a visor the helmet holds off it), goes onto the
	///    eyes like a pair of glasses (<see cref="PutOnTheEyes"/>), with what lies across the
	///    other eye.
	///  - A mic: the piece of it right in front of the mouth, below the eyes and no bigger than a
	///    boom (not a visor), and what holds it out there (the pieces it touches that don't rest
	///    on the head), turn about the end furthest from the mouth, where the boom is mounted,
	///    until the tip is as far in front of this mouth as it was in front of the stock one; the
	///    boom is made as much longer or shorter as that takes.
	/// </summary>
	/// <param name="parts">The rigid pieces of the hat, as indices into <paramref name="rigid"/>.</param>
	/// <param name="eyes">The middles of the eyes (<see cref="EyeMiddles"/>).</param>
	static void FaceParts( List<int> parts, List<List<int>> rigid, Vec3[] lo, Vec3[] hi, Vec3[] pts, Vec3[] fitted, float[] gap0, Vec3[] away, (Vec3 Stock, Vec3 Body)[] eyes, (Vec3 Stock, Vec3 Body)[] face, PushTest push )
	{
		int main = parts[0];
		foreach ( int r in parts ) if ( rigid[r].Count > rigid[main].Count ) main = r;
		var whole = new List<int>();
		foreach ( int r in parts ) whole.AddRange( rigid[r] );

		float level = MathF.Min( eyes[0].Stock.X, eyes[1].Stock.X ) - EyewearFront;
		var onEyes = new HashSet<int>();
		var goggles = new List<int>();
		foreach ( int r in parts )
		{
			if ( r == main || hi[r].Z - lo[r].Z > EyewearHeight || hi[r].X < level ) continue;
			float closest = float.MaxValue;
			foreach ( int i in rigid[r] ) closest = MathF.Min( closest, gap0[i] );
			if ( closest >= GogglesRest ) continue;
			foreach ( var (eye, _) in eyes )
			{
				if ( eye.Y < lo[r].Y || eye.Y > hi[r].Y || eye.Z < lo[r].Z || eye.Z > hi[r].Z ) continue;
				onEyes.Add( r );
				goggles.AddRange( rigid[r] );
				break;
			}
		}
		if ( goggles.Count > 0 )
		{
			var front = new List<int>();
			foreach ( int i in goggles ) if ( pts[i].X >= level ) front.Add( i );
			if ( front.Count == 0 ) front = goggles;
			PutBy( goggles, front, pts, fitted, 0.5f * (eyes[0].Stock + eyes[1].Stock), 0.5f * (eyes[0].Body + eyes[1].Body), EyeRatio( eyes ), whole );
			PushOut( goggles, fitted, gap0, away, push, false );
		}

		if ( face == null || face.Length != 4 ) return;
		var (mouth, mouthNow) = face[2];
		int tip = -1;
		float nearest = MouthReach, eyesLow = MathF.Min( eyes[0].Stock.Z, eyes[1].Stock.Z );
		foreach ( int r in parts )
		{
			// The end of a boom, not a visor or a chin guard reaching up over the eyes.
			if ( r == main || onEyes.Contains( r ) || hi[r].Z >= eyesLow || (hi[r] - lo[r]).Length() > BoomLength ) continue;
			foreach ( int i in rigid[r] )
			{
				float d = (pts[i] - mouth).Length();
				if ( d < nearest && pts[i].X >= mouth.X ) { nearest = d; tip = r; }
			}
		}
		if ( tip < 0 ) return;
		var arm = new List<int>( rigid[tip] );
		foreach ( int r in parts )
		{
			if ( r == tip || r == main || onEyes.Contains( r ) ) continue;
			if ( lo[r].X > hi[tip].X + Touching || lo[tip].X > hi[r].X + Touching || lo[r].Y > hi[tip].Y + Touching || lo[tip].Y > hi[r].Y + Touching || lo[r].Z > hi[tip].Z + Touching || lo[tip].Z > hi[r].Z + Touching ) continue;
			float closest = float.MaxValue;
			foreach ( int i in rigid[r] ) closest = MathF.Min( closest, gap0[i] );
			if ( closest >= ArmRest ) arm.AddRange( rigid[r] );
		}

		// Where the tip goes: as far from this mouth as from the stock one, turned with the hat
		// and scaled by the face, the way a mask is (PutOnTheFace).
		float stockHeight = (face[0].Stock - face[3].Stock).Length(), bodyHeight = (face[0].Body - face[3].Body).Length();
		float stockWidth = (eyes[1].Stock - eyes[0].Stock).Length(), bodyWidth = (eyes[1].Body - eyes[0].Body).Length();
		if ( stockHeight < 1e-4f * Units.Metre || stockWidth < 1e-4f * Units.Metre ) return;
		float ratio = Math.Clamp( MathF.Sqrt( bodyHeight / stockHeight * (bodyWidth / stockWidth) ), 0.5f, 2f );
		var tipMade = Centre( rigid[tip], pts );
		Similarity( whole, pts, fitted, out _, out _, out var turn, out _ );
		var offset = tipMade - mouth;
		if ( turn != null ) offset = new( turn[0] * offset.X + turn[1] * offset.Y + turn[2] * offset.Z, turn[3] * offset.X + turn[4] * offset.Y + turn[5] * offset.Z, turn[6] * offset.X + turn[7] * offset.Y + turn[8] * offset.Z );
		var want = mouthNow + offset * ratio;

		// The mount: the end of the arm furthest from the tip.
		int mount = arm[0];
		foreach ( int i in arm ) if ( (pts[i] - tipMade).LengthSquared() > (pts[mount] - tipMade).LengthSquared() ) mount = i;
		var pivot = fitted[mount];
		Vec3 v = Centre( rigid[tip], fitted ) - pivot, w = want - pivot;
		float lv = v.Length(), lw = w.Length();
		if ( lv < 1e-4f * Units.Metre || lw < 1e-4f * Units.Metre ) return;
		float stretch = Math.Clamp( lw / lv, BoomLeast, BoomMost );
		Vec3 along = v / lv, to = w / lw;
		var axis = Vec3.Cross( along, to );
		float sin = axis.Length(), cos = Vec3.Dot( along, to );
		if ( sin > 1e-6f ) axis /= sin;
		foreach ( int i in arm )
		{
			var d = fitted[i] - pivot;
			d += along * (Vec3.Dot( d, along ) * (stretch - 1f));
			if ( sin > 1e-6f ) d = d * cos + Vec3.Cross( axis, d ) * sin + axis * (Vec3.Dot( axis, d ) * (1f - cos));
			fitted[i] = pivot + d;
		}
		PushOut( arm, fitted, gap0, away, push, false );
	}

	/// <summary>
	/// A hat is fitted as a whole and kept out of the skin by its vertices, so where this head
	/// is shaped differently from the stock one (a rounder crown) the skin can still come up
	/// through the middle of its big flat triangles. The cloth passes that catch that don't run
	/// on solids, and the stock skin mapped onto this body is no help on a head of another
	/// shape. So this goes over the body's own skin under the solid: around every vertex of it
	/// that is out through the solid, or closer under it than <see cref="SolidClear"/>, the
	/// solid bulges out over it. Everything within <see cref="BulgeReach"/> moves, the outer
	/// surface and the lining under it alike, less and less towards the edge. Lifting only the
	/// triangles over the skin pushed the outer surface through the lining. The rest of the
	/// solid keeps its fit.
	///
	/// Only the top of the head counts, the skin above the eyes. Lower down, what comes through
	/// a solid is a part that sticks out on its own (an ear through the cup of a pair of
	/// headphones, a cheek under a visor), and a bulge over it bends a hard shape out of true.
	/// </summary>
	/// <param name="above">How high up the skin has to be to count: the eyes.</param>
	/// <param name="thin">Skin with more of the body this close under it sticks out on its own (<see cref="ThinPart"/>).</param>
	static void Bulge( List<int> idx, List<int> tris, Vec3[] fitted, float[] gap0, Vec3[] away, TriMesh body, float hidden, float above, float thin )
	{
		// Only the part of the solid that shows has skin to cover. What was deep under the stock
		// skin (the bottom of a cap, closed across the head) was never meant to cover anything.
		var shown = new List<int>();
		for ( int t = 0; t < tris.Count; t += 3 )
		{
			if ( gap0[tris[t]] < -hidden || gap0[tris[t + 1]] < -hidden || gap0[tris[t + 2]] < -hidden ) continue;
			shown.Add( tris[t] ); shown.Add( tris[t + 1] ); shown.Add( tris[t + 2] );
		}
		if ( shown.Count == 0 ) return;
		var shownTris = shown.ToArray();

		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( int i in shownTris )
		{
			lo = new Vec3( MathF.Min( lo.X, fitted[i].X ), MathF.Min( lo.Y, fitted[i].Y ), MathF.Min( lo.Z, fitted[i].Z ) );
			hi = new Vec3( MathF.Max( hi.X, fitted[i].X ), MathF.Max( hi.Y, fitted[i].Y ), MathF.Max( hi.Z, fitted[i].Z ) );
		}
		lo -= new Vec3( SolidPoke, SolidPoke, SolidPoke );
		hi += new Vec3( SolidPoke, SolidPoke, SolidPoke );
		lo = new Vec3( lo.X, lo.Y, MathF.Max( lo.Z, above ) );

		// The skin near the solid, and which way is out of it at each vertex.
		var outward = new Dictionary<int, Vec3>();
		for ( int t = 0; t < body.TriCount; t++ )
		{
			for ( int k = 0; k < 3; k++ )
			{
				int v = body.Tris[t * 3 + k];
				Vec3 p = body.Verts[v];
				if ( p.X < lo.X || p.Y < lo.Y || p.Z < lo.Z || p.X > hi.X || p.Y > hi.Y || p.Z > hi.Z ) continue;
				outward[v] = (outward.TryGetValue( v, out var sum ) ? sum : Vec3.Zero) + body.TriNormals[t];
			}
		}

		// An ear or a brow pokes out of the head on its own, with the body's far side right under
		// it. Which way is out of it says nothing about where a hat over it should go.
		var hits = new List<(float T, bool Leaving, int Tri)>();
		var skin = new List<(Vec3 At, Vec3 Out)>();
		foreach ( var (v, sum) in outward )
		{
			float length = sum.Length();
			if ( length < 1e-12f ) continue;
			Vec3 n = sum / length;
			body.Crossings( body.Verts[v], -n, thin, hits );
			if ( hits.Count == 0 ) skin.Add( (body.Verts[v], n) );
		}
		if ( skin.Count == 0 ) return;

		var sites = new List<(Vec3 At, Vec3 Out, float Need)>();
		var lift = new Vec3[idx.Count];
		var start = new Vec3[idx.Count];
		for ( int k = 0; k < idx.Count; k++ ) start[k] = fitted[idx[k]];
		for ( int pass = 0; pass < BulgePasses; pass++ )
		{
			var surface = new TriMesh( fitted, shownTris );
			sites.Clear();
			foreach ( var (at, n) in skin )
			{
				// How far under the solid the skin is: under its outermost layer straight above it,
				// or, if there is none, its nearest one straight below, the skin having come
				// through. Skin through a layer with another one over it (the pad of a headphone,
				// the lining of a hat) doesn't show.
				float clear;
				int tri;
				surface.Crossings( at, n, GapCap, hits );
				if ( hits.Count > 0 )
				{
					clear = hits[^1].T;
					tri = hits[^1].Tri;
					if ( clear >= SolidClear ) continue;
				}
				else
				{
					surface.Crossings( at, -n, SolidPoke, hits );
					if ( hits.Count == 0 ) continue;
					clear = -hits[0].T;
					tri = hits[0].Tri;
				}

				// Skin that faces another way than the solid sits off the head there is in a crease
				// (where an ear meets the head), and the solid only has to bridge it. A solid made to
				// sit closer to the stock skin there (a rim sunk into the head) can sit as close to
				// this one.
				int a = shownTris[tri * 3], b = shownTris[tri * 3 + 1], c = shownTris[tri * 3 + 2];
				Vec3 spot = at + n * clear;
				Barycentric( spot, fitted[a], fitted[b], fitted[c], out float u, out float bv, out float bw );
				Vec3 off = away[a] * u + away[b] * bv + away[c] * bw;
				if ( Vec3.Dot( off, n ) < 0.5f * off.Length() ) continue;
				float need = MathF.Min( SolidClear, gap0[a] * u + gap0[b] * bv + gap0[c] * bw ) - clear;
				if ( need > 1e-4f * Units.Metre ) sites.Add( (spot, n, need) );
			}
			if ( sites.Count == 0 ) break;

			// Each vertex takes the biggest of the bulges it is in reach of, and goes its own way
			// out of the skin. Moved all the way the skin under the bump faces, the parts of the
			// solid off to its side went into the skin next to it. Only the side of the solid
			// facing the same way as the skin goes along, not a brim or a far side within reach.
			// Skin that a bulge doesn't clear would have it grow pass after pass, so no vertex
			// goes further from its fit than the most any skin can ask for.
			for ( int k = 0; k < idx.Count; k++ )
			{
				int i = idx[k];
				var best = Vec3.Zero;
				foreach ( var (spot, n, need) in sites )
				{
					float x = (fitted[i] - spot).Length() / BulgeReach;
					if ( x >= 1 ) continue;
					float facing = Math.Clamp( Vec3.Dot( away[i], n ), 0, 1 );
					Vec3 p = away[i] * (need * facing * (1 - x * x * (3 - 2 * x)));
					if ( p.LengthSquared() > best.LengthSquared() ) best = p;
				}
				lift[k] = best;
			}
			for ( int k = 0; k < idx.Count; k++ )
			{
				Vec3 total = fitted[idx[k]] + lift[k] - start[k];
				float length = total.Length();
				if ( length > SolidClear + SolidPoke ) total *= (SolidClear + SolidPoke) / length;
				fitted[idx[k]] = start[k] + total;
			}
		}
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
	internal static float[] Rotation( float[] m )
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

	const float ObjectBiggest = 0.08f;   // a heap of rigid pieces whose biggest piece has this share of its vertices
	const float ObjectCore = 0.5f;       // and whose big pieces have this share between them is one object after all
	const int ObjectBig = 8;             // a big piece of it: no more than this many times smaller than the biggest

	/// <summary>
	/// Whether a heap of more rigid pieces than an object usually has is one object after all:
	/// hair cards are many pieces of about one size, none of them more than a few in a hundred
	/// of the heap, where an object is a few big pieces with small ones fixed to them. A
	/// tactical helmet is thirty pieces (a shell, goggles, ear cups, a mic and its boom, straps
	/// and buckles), its shell a fifth of it, and fitted as hair, its goggles went wherever the
	/// skin under them went, by bits. Small pieces threaded on the garment's cloth (beads on the
	/// cord of a pair of reading glasses) aren't part of it: they and the cord go the way they
	/// went with the heap, as hair.
	/// </summary>
	/// <param name="clothPts">The garment's vertices that aren't rigid.</param>
	/// <param name="parts">The pieces of the object: the heap but for those on the cloth.</param>
	/// <param name="loose">Where the vertices of the pieces on the cloth are added, to go the way of hair.</param>
	static bool OneObject( List<int> heap, List<List<int>> rigid, Vec3[] lo, Vec3[] hi, List<Vec3> clothPts, out List<int> parts, List<int> loose )
	{
		parts = heap;
		int total = 0, biggest = 0;
		foreach ( int r in heap ) { total += rigid[r].Count; biggest = Math.Max( biggest, rigid[r].Count ); }
		int core = 0;
		foreach ( int r in heap ) if ( rigid[r].Count * ObjectBig >= biggest ) core += rigid[r].Count;
		if ( biggest < ObjectBiggest * total || core < ObjectCore * total ) return false;

		parts = new List<int>();
		foreach ( int r in heap )
		{
			bool threaded = false;
			if ( rigid[r].Count * ObjectBig < biggest )
			{
				var middle = 0.5f * (lo[r] + hi[r]);
				float reach = MathF.Max( 0.5f * (hi[r] - lo[r]).Length(), Touching );
				foreach ( var p in clothPts ) if ( (p - middle).LengthSquared() <= reach * reach ) { threaded = true; break; }
			}
			if ( threaded ) loose.AddRange( rigid[r] );
			else parts.Add( r );
		}
		return parts.Count > 0;
	}

	/// <summary>How high the mouth is on the stock body, by the eyes; infinity when they aren't known.</summary>
	static float Mouth( (Vec3 Stock, Vec3 Body)[] eyes )
	{
		if ( eyes == null || eyes.Length == 0 ) return float.PositiveInfinity;
		float mouth = 0;
		foreach ( var (stock, _) in eyes ) mouth += stock.Z / eyes.Length;
		return mouth - MouthBelowEyes;
	}

	/// <summary>
	/// Whether a heap of small rigid pieces is fringe along an edge of the garment's cloth: all of
	/// it below the mouth, more cloth than heap, and every piece within reach of the cloth with
	/// most of them nearest a rim of it, where the cloth turns round its edge (the fraying threads
	/// round a denim vest's armholes, front and hem: 9 in 10 of them, against 1 in 10 of the
	/// pirate outfit's tassels, which hang off its coat and stay hair). Hair and beards grow from
	/// the head, from a cap or not, and reach up to the mouth or higher (a moustache,
	/// sideburns): taken for fringe, a beard's cards went with the patch of skin they lie on as
	/// cloth would, and came apart. The threads round a vest's collar are below the chin, not
	/// far: they come up the back of the neck.
	/// </summary>
	static bool Fringed( List<int> heap, List<List<int>> rigid, Vec3[] pts, Dictionary<int, List<int>> members, Dictionary<int, List<int>> pieceTris, int[] firstOf, int[] boneIndex, float[] boneWeight, int clothCount, (Vec3 Stock, Vec3 Body)[] eyes )
	{
		float mouth = Mouth( eyes );
		if ( float.IsPositiveInfinity( mouth ) ) return false;
		int total = 0;
		foreach ( int r in heap )
		{
			total += rigid[r].Count;
			foreach ( int i in rigid[r] ) if ( pts[i].Z > mouth ) return false;
		}
		if ( total >= clothCount ) return false;

		// The cloth's vertices, and which of them are on a rim: the faces round it turn further
		// than RimTurn from each other.
		var faces = new Dictionary<int, List<Vec3>>();
		foreach ( var (root, idx) in members )
		{
			if ( IsRigid( idx, firstOf, boneIndex, boneWeight ) || !pieceTris.TryGetValue( root, out var tris ) ) continue;
			for ( int t = 0; t < tris.Count; t += 3 )
			{
				var normal = Vec3.Cross( pts[tris[t + 1]] - pts[tris[t]], pts[tris[t + 2]] - pts[tris[t]] );
				float length = normal.Length();
				if ( length < 1e-12f ) continue;
				normal *= 1f / length;
				for ( int k = 0; k < 3; k++ )
				{
					if ( !faces.TryGetValue( tris[t + k], out var list ) ) faces[tris[t + k]] = list = new List<Vec3>();
					list.Add( normal );
				}
			}
		}
		var rim = new Dictionary<int, bool>();
		foreach ( var (v, list) in faces )
		{
			bool turns = false;
			for ( int x = 0; x < list.Count && !turns; x++ )
				for ( int y = x + 1; y < list.Count && !turns; y++ ) turns = Vec3.Dot( list[x], list[y] ) < RimTurn;
			rim[v] = turns;
		}

		int onRim = 0;
		foreach ( int r in heap )
		{
			float best = LayerReach * LayerReach;
			int nearest = -1;
			foreach ( int i in rigid[r] )
				foreach ( int v in faces.Keys )
				{
					float d = (pts[v] - pts[i]).LengthSquared();
					if ( d <= best ) { best = d; nearest = v; }
				}
			if ( nearest < 0 ) return false;
			if ( rim[nearest] ) onRim++;
		}
		return onRim * 2 > heap.Count;
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

	// Whether there are pieces that aren't rigid and all of them are no bigger than a bead.
	static bool OnlySmallCloth( Dictionary<int, List<int>> members, int[] firstOf, int[] boneIndex, float[] boneWeight, Vec3[] pts )
	{
		bool small = false;
		foreach ( var idx in members.Values )
		{
			if ( IsRigid( idx, firstOf, boneIndex, boneWeight ) ) continue;
			Vec3 lo = pts[idx[0]], hi = pts[idx[0]];
			foreach ( int i in idx )
			{
				lo = new Vec3( MathF.Min( lo.X, pts[i].X ), MathF.Min( lo.Y, pts[i].Y ), MathF.Min( lo.Z, pts[i].Z ) );
				hi = new Vec3( MathF.Max( hi.X, pts[i].X ), MathF.Max( hi.Y, pts[i].Y ), MathF.Max( hi.Z, pts[i].Z ) );
			}
			if ( (hi - lo).Length() > BeadSize ) return false;
			small = true;
		}
		return small;
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

	internal static int[] Islands( int n, List<(int A, int B)> edges )
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

	static float Blend( float[] values, List<int> tris, int t, float[] w ) =>
		values[tris[t]] * w[0] + values[tris[t + 1]] * w[1] + values[tris[t + 2]] * w[2];

	static Vec3 Blend( Vec3[] values, List<int> tris, int t, float[] w ) =>
		values[tris[t]] * w[0] + values[tris[t + 1]] * w[1] + values[tris[t + 2]] * w[2];

	public static void Barycentric( Vec3 p, Vec3 a, Vec3 b, Vec3 c, out float u, out float v, out float w )
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
