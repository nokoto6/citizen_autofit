using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// Works out where every skin vertex of the stock body ends up on another body.
///
/// Two ways of finding the matching point, tried in this order:
///  - by UV. As long as the other body keeps the stock UV layout (sculpting, moving vertices,
///    adding loops all do), the same UV is the same bit of skin. Exact.
///  - by bone rays. The bodies share a skeleton, so shoot a ray from the bone axis through the
///    skin vertex and see where the other body's skin is along that same ray. Works for any
///    mesh, but only knows "further from the bone", so skin that slides along the surface is
///    not tracked.
/// This is done once per body. Every garment then reuses the result.
/// </summary>
public sealed class BodyMap
{
	/// <summary>Displacement of each stock body vertex.</summary>
	public Vec3[] SkinMove;

	/// <summary>The part of <see cref="SkinMove"/> that came from landmarks (skin sliding along the face with the eyes), per stock vertex. Hair is fitted without it.</summary>
	public Vec3[] Landmarked;

	/// <summary>
	/// True for the vertices whose place on the other body was actually found. The rest got
	/// their displacement from their neighbours, which is a guess: good enough to carry cloth
	/// along, not to be taken for a point on the other body's surface.
	/// </summary>
	public bool[] Found;

	/// <summary>
	/// Per stock vertex, how many times as thick the other body is there as the stock one,
	/// through the body from one side to the other (see <see cref="MeasureThickness"/>). What
	/// cloth is taken in by on a slimmer body.
	/// </summary>
	public float[] Thickness;

	/// <summary>
	/// Per stock vertex, where it lies on the other body's skin for something painted on it (a
	/// tattoo, makeup, brows): on the skin itself, not on the lashes, brows or eyes over it, and
	/// over the face lined up by the face's features (<see cref="Repose.Features"/>): the line
	/// round the stock eye's opening onto the line round this one's, the corners of the mouth onto
	/// its corners. Away from the face it is <see cref="SkinMove"/>, put on the skin. Null unless
	/// asked for.
	/// </summary>
	public Vec3[] OnSkin;

	/// <summary>
	/// <see cref="OnSkin"/> with the neck and the lower face gone over by the neck's own landmarks
	/// (<see cref="NeckChart"/>): what a drawing on the citizen's neck goes by (see <see cref="PaintIslands"/>).
	/// Null when <see cref="OnSkin"/> is, or the faces have no mouth and nose to go by.
	/// </summary>
	public Vec3[] OnNeck;

	NeckChart stockNeck;

	/// <summary>How high the stock body's jaw line is round its neck at a point of its skin (the line between its neck and its face). NaN where it isn't known.</summary>
	public float JawHeight( Vec3 stockPoint ) => stockNeck?.JawAt( stockPoint ) ?? float.NaN;

	public int ByUv, ByRays, NoMatch, Outliers;

	const float LookBeyond = 0.5f * Units.Metre;    // how far past the old skin we look for the new one
	const float UvMaxMove = 0.15f * Units.Metre;    // a UV match further away than this is a different UV layout
	const int MinOwnTriangles = 8;                  // a bone needs this many skin triangles of its own to get an axis
	const float MinWeight = 0.05f;
	const float NearestShare = 0.05f;   // skin this much nearer its bone than on the stock body is a ray that started on the skin
	const float ThroughStart = 0.01f * Units.Metre;   // how far off the skin a measure through the body starts
	const float MinThrough = 0.01f * Units.Metre;     // the stock body thinner than this somewhere says nothing about its size
	const int ThicknessSmoothing = 3;                 // passes of evening out the measures over the skin

	public static BodyMap Build( SkinnedGeometry stock, TriMesh old, TriMesh other, Vec2[] otherUvs ) =>
		Build( stock, old, other, otherUvs, null, null );

	/// <param name="otherSkin">
	/// The other body's skin by stock bone, if it is skinned. A ray from a bone then only counts
	/// hits on skin that bone or a bone near it drives, so a ray from the spine can't slip
	/// between two ribs and land on an arm.
	/// </param>
	/// <param name="landmarks">Landmarks from <see cref="Repose"/> (eyes, the front of the chest): skin within reach of each one shifts along the surface with it.</param>
	/// <param name="features">The face's features on both bodies (<see cref="Repose.Features"/>). Given, <see cref="OnSkin"/> is worked out too.</param>
	/// <param name="ears">The ears on both bodies (<see cref="Repose.Ears"/>): paint on the neck reaches up to under them.</param>
	public static BodyMap Build( SkinnedGeometry stock, TriMesh old, TriMesh other, Vec2[] otherUvs, SkinByBone otherSkin, List<(Vec3 At, Vec3 Shift, float Reach)> landmarks, List<(string Name, Vec3 Stock, Vec3 Body)> features = null, List<(string Name, Vec3 Stock, Vec3 Body)> ears = null )
	{
		var result = new BodyMap();
		var weld = MeshTools.Weld( old.Verts, out int n );
		var firstOf = new int[n];
		for ( int i = old.Verts.Length - 1; i >= 0; i-- )
			firstOf[weld[i]] = i;

		BoneAxes( stock, old, out var axisA, out var axisB );
		var uv = otherUvs != null ? new UvLookup( other, otherUvs ) : null;

		var oldNormals = new Vec3[old.Verts.Length];
		for ( int t = 0; t < old.TriCount; t++ )
			for ( int k = 0; k < 3; k++ )
				oldNormals[old.Tris[t * 3 + k]] += old.TriNormals[t];

		// A body with its own UV layout still gets the odd accidental match. Only trust UVs
		// when they line up for a good part of the skin.
		if ( uv != null )
		{
			int matches = 0;
			for ( int i = 0; i < n; i++ )
				if ( UvMatch( uv, stock, old, oldNormals, firstOf[i], out _ ) ) matches++;
			if ( matches < 0.3f * n ) uv = null;
		}

		var move = new Vec3[n];
		var landmarked = new Vec3[n];
		var valid = new bool[n];
		var exact = new bool[n];
		var hits = new List<(float, bool, int)>();
		var targets = new Vec3[4];

		for ( int i = 0; i < n; i++ )
		{
			int v = firstOf[i];
			Vec3 s = old.Verts[v];

			if ( uv != null && UvMatch( uv, stock, old, oldNormals, v, out var p ) )
			{
				move[i] = p - s;
				valid[i] = exact[i] = true;
				result.ByUv++;
				continue;
			}

			float total = 0;
			var sum = Vec3.Zero;
			int answers = 0;
			for ( int j = 0; j < 4; j++ )
			{
				int bone = stock.BoneIndex[v * 4 + j];
				float w = stock.BoneWeight[v * 4 + j];
				if ( bone < 0 || w < MinWeight ) continue;
				if ( !FollowSkin( old, other, otherSkin, bone, axisA[bone], axisB[bone], s, hits, out var target ) ) continue;
				sum += (target - s) * w;
				total += w;
				targets[answers++] = target;
			}

			if ( total > 1e-6f )
			{
				move[i] = sum / total;
				valid[i] = true;
				result.ByRays++;

				// Each ray lands on the skin, but rays from two bones may land on two parts of the
				// body (the neck and the top of the shoulder), and the point between them is in the
				// air over the crease. It goes onto the skin there.
				if ( answers > 1 ) OntoSkin( other, oldNormals[v], s, targets, answers, ref move[i] );

				// Rays only move skin towards or away from the bone. Near a landmark the skin
				// also slides along the surface with it (eyes sitting lower on the face, say).
				if ( landmarks != null )
				{
					Vec3 normal = oldNormals[v] / MathF.Max( oldNormals[v].Length(), 1e-20f );
					// Where several landmarks reach, their shifts are averaged, not added: between
					// the eyes the skin went down by both eyes' drop at once.
					var shifts = Vec3.Zero;
					float weights = 0;
					foreach ( var (at, shift, reach) in landmarks )
					{
						float d = (s - at).Length() / reach;
						float w = MathF.Exp( -0.5f * d * d );
						if ( w <= 0.01f ) continue;
						shifts += shift * w;
						weights += w;
					}
					if ( weights > 0 )
					{
						var shift = shifts / MathF.Max( weights, 1f );
						var slide = shift - normal * Vec3.Dot( normal, shift );
						move[i] += slide;
						landmarked[i] += slide;
					}
				}
			}
			else
			{
				result.NoMatch++;
			}
		}

		var edges = MeshTools.Edges( old.Tris, weld );
		var kept = MeshTools.DropOutliers( move, valid, edges );
		for ( int i = 0; i < n; i++ )
			if ( valid[i] && !kept[i] ) result.Outliers++;

		var found = MeshTools.Copy( move );
		var known = MeshTools.Copy( kept );
		MeshTools.Relax( move, known, edges, 2, 0.5f );

		// Ray results are noisy and want the smoothing. UV matches are exact, leave them be.
		for ( int i = 0; i < n; i++ )
			if ( exact[i] && kept[i] ) move[i] = found[i];

		// Whatever is still unknown sits on a patch nothing reached. Let it spread in.
		for ( int pass = 0; pass < 50 && Array.IndexOf( known, false ) >= 0; pass++ )
			MeshTools.Relax( move, known, edges, 1, 0f );

		result.SkinMove = new Vec3[old.Verts.Length];
		result.Landmarked = new Vec3[old.Verts.Length];
		result.Found = new bool[old.Verts.Length];
		for ( int i = 0; i < old.Verts.Length; i++ )
		{
			result.SkinMove[i] = move[weld[i]];
			result.Landmarked[i] = landmarked[weld[i]];
			result.Found[i] = kept[weld[i]];
		}

		var thickness = MeasureThickness( stock, old, other, oldNormals, weld, firstOf, move, edges );
		result.Thickness = new float[old.Verts.Length];
		for ( int i = 0; i < old.Verts.Length; i++ ) result.Thickness[i] = thickness[weld[i]];

		if ( features != null )
		{
			var (onSkin, onNeck, chart) = PaintedOn( stock, old, other, weld, firstOf, move, features, ears );
			result.OnSkin = new Vec3[old.Verts.Length];
			for ( int i = 0; i < old.Verts.Length; i++ ) result.OnSkin[i] = onSkin[weld[i]];
			if ( onNeck != null )
			{
				result.OnNeck = new Vec3[old.Verts.Length];
				for ( int i = 0; i < old.Verts.Length; i++ ) result.OnNeck[i] = onNeck[weld[i]];
				result.stockNeck = chart;
			}
		}
		return result;
	}

	const float FaceFull = 1f;      // the face's features carry the skin in full this far from their middle, as a share of how far out they reach
	const float FaceFade = 1.5f;    // and none of it from this far
	const float FaceTie = 1.8f;     // skin between the two is where the features' warp is tied to the rays' map
	const float TieCell = 0.25f;    // one tie per cell this big

	/// <summary>
	/// <see cref="OnSkin"/> per welded stock vertex. The rays' map is good on the round of the
	/// head, the neck and the body, and lost on a face whose features are set elsewhere than the
	/// stock one's: from the skull, the stock eyes land on a forehead and the mouth on a nose. So
	/// over the face the skin goes by a smooth warp (<see cref="Warp"/>) that takes each feature of
	/// the stock face onto the same feature of this one, tied to the rays' map round the face, and
	/// is faded over into the rays' map from there. Either way the point is then put on the skin:
	/// both only come near it. Also <see cref="OnNeck"/>: the same with the neck and what is
	/// under the mouth going by the neck's own landmarks (<see cref="NeckChart"/>): the warp only
	/// knows the face seen from the front, and on an anime body whose face sits 11 cm lower the
	/// rays put the citizen's throat on its chin and its chin on its mouth.
	/// </summary>
	static (Vec3[] OnSkin, Vec3[] OnNeck, NeckChart Chart) PaintedOn( SkinnedGeometry stock, TriMesh old, TriMesh other, int[] weld, int[] firstOf, Vec3[] move, List<(string Name, Vec3 Stock, Vec3 Body)> features, List<(string Name, Vec3 Stock, Vec3 Body)> ears )
	{
		var whole = other.Skin();
		var skin = whole;   // on the face, only what can be seen from the front (below)
		Vec3 OnTheSkin( Vec3 p ) { skin.Nearest( p, out var q, out _ ); return q; }
		int n = firstOf.Length;
		var onSkin = new Vec3[n];
		var (neckFrom, neckTo) = NeckCharts( stock, old, whole, features, ears );
		var onNeck = neckFrom != null ? new Vec3[n] : null;
		var hits = new List<(float T, bool Leaving, int Tri)>();
		var forward = new Vec3( 1, 0, 0 );   // the way a body faces in the stock pose
		Vec3 ByTheNeck( Vec3 s, Vec3 p )
		{
			if ( neckFrom == null ) return p;
			var q = NeckChart.Map( neckFrom, neckTo, s, out float w );
			return w > 0 ? p * (1 - w) + q * w : p;
		}

		// The face: how far its features reach from their middle. The crown is far above it,
		// a tie of its own.
		var centre = Vec3.Zero;
		int count = 0;
		foreach ( var (name, at, _) in features ) if ( name != "crown" ) { centre += at; count++; }
		Warp warp = null;
		float reach = 0;
		if ( count >= 4 )
		{
			centre /= count;
			foreach ( var (name, at, _) in features ) if ( name != "crown" ) reach = MathF.Max( reach, (at - centre).Length() );

			// The warp is over the face seen from the front, so one tie per cell of it: the
			// foremost skin there.
			var points = new List<(Vec3 From, Vec3 To)>();
			foreach ( var (_, at, there) in features ) points.Add( (at, there) );
			var ties = new Dictionary<(int, int), int>();
			float cell = TieCell * reach;
			for ( int i = 0; i < n; i++ )
			{
				var s = old.Verts[firstOf[i]];
				float d = (s - centre).Length();
				if ( d < FaceFade * reach || d > FaceTie * reach ) continue;
				var key = ((int)MathF.Floor( s.Y / cell ), (int)MathF.Floor( s.Z / cell ));
				if ( !ties.TryGetValue( key, out int have ) || s.X > old.Verts[firstOf[have]].X ) ties[key] = i;
			}
			foreach ( int i in ties.Values ) points.Add( (old.Verts[firstOf[i]], OnTheSkin( old.Verts[firstOf[i]] + move[i] )) );
			warp = Warp.Fit( points );

			// On the face, the skin paint goes onto is what can be seen from the front, not a
			// surface buried in the head behind it: the part of an anime eye's white that goes on
			// under the lid, the inside of the mouth. That is skin with skin facing forward right
			// in front of it. (Some surfaces come in two sheets facing each other, the white of an
			// eye: the sheet facing back doesn't count.)
			var open = new List<int>();
			for ( int t = 0; t < skin.TriCount; t++ )
			{
				Vec3 a = skin.Verts[skin.Tris[t * 3]], b = skin.Verts[skin.Tris[t * 3 + 1]], c = skin.Verts[skin.Tris[t * 3 + 2]];
				Vec3 middle = (a + b + c) / 3f;
				bool buried = false;
				if ( (middle - centre).Length() < FaceFade * reach )
				{
					skin.Crossings( middle + forward * HiddenStart, forward, Covering, hits );
					foreach ( var hit in hits )
					{
						var normal = skin.TriNormals[hit.Tri];
						if ( normal.X > Facing * normal.Length() ) { buried = true; break; }
					}
				}
				if ( !buried ) { open.Add( skin.Tris[t * 3] ); open.Add( skin.Tris[t * 3 + 1] ); open.Add( skin.Tris[t * 3 + 2] ); }
			}
			skin = new TriMesh( skin.Verts, open.ToArray() );
		}

		// The stock skin seen through an eye's opening from the front is hidden behind the eye:
		// the walls of the socket round an eyeball that stands out of the face. Paint on it is
		// never seen. On a face whose eyes don't stand out it would land on the eye, so it is put
		// under the skin there.
		var outlines = new List<(Vec3[] Outline, float Front, float Depth)>();
		foreach ( string side in new[] { "eyeL", "eyeR" } )
		{
			var outline = new Vec3[FaceMarks.Outline.Length];
			bool all = true;
			for ( int k = 0; k < FaceMarks.Outline.Length && all; k++ )
			{
				all = false;
				foreach ( var (name, at, _) in features )
					if ( name == side + FaceMarks.Outline[k] ) { outline[k] = at; all = true; break; }
			}
			if ( !all ) continue;
			float front = 0, low = float.MaxValue, high = float.MinValue;
			foreach ( var p in outline ) { front += p.X / outline.Length; low = MathF.Min( low, p.Y ); high = MathF.Max( high, p.Y ); }
			outlines.Add( (outline, front, HiddenDepth * (high - low)) );
		}

		for ( int i = 0; i < n; i++ )
		{
			var s = old.Verts[firstOf[i]];
			var p = s + move[i];
			bool hidden = false;
			foreach ( var (outline, front, depth) in outlines )
				hidden |= s.X > front - depth && InFront( outline, s );
			if ( warp != null )
			{
				float d = (s - centre).Length();
				float w = Math.Clamp( (FaceFade * reach - d) / ((FaceFade - FaceFull) * reach), 0f, 1f );
				w = w * w * (3 - 2 * w);
				if ( w > 0 ) p = p * (1 - w) + warp.Apply( s ) * w;
				// So is skin on the face that the stock face covers from the front: under a lid
				// standing out over the eye, inside the lips.
				if ( d < FaceFull * reach && !hidden )
				{
					old.Crossings( s + forward * HiddenStart, forward, HiddenReach * reach, hits );
					hidden = hits.Count > 0;
				}
			}
			Vec3 Place( Vec3 at )
			{
				if ( !hidden ) return OnTheSkin( at );
				skin.Nearest( at, out var q, out _ );
				whole.Nearest( q, out _, out int tri );   // whose normals face out of the whole body
				var up = whole.TriNormals[tri];
				return q - up / MathF.Max( up.Length(), 1e-12f ) * Buried;
			}
			onSkin[i] = Place( p );
			if ( onNeck == null ) continue;
			var byNeck = warp != null ? ByTheNeck( s, p ) : p;
			onNeck[i] = byNeck.X == p.X && byNeck.Y == p.Y && byNeck.Z == p.Z ? onSkin[i] : Place( byNeck );
		}
		return (onSkin, onNeck, neckFrom);
	}

	/// <summary>
	/// The neck and the lower face (<see cref="NeckChart"/>) of the stock body and of this one, both
	/// in the stock pose, round the stock neck's axis and from its base at the clavicles' joints.
	/// Nulls when a face has no mouth or nose to go by.
	/// </summary>
	static (NeckChart From, NeckChart To) NeckCharts( SkinnedGeometry stock, TriMesh old, TriMesh skin, List<(string Name, Vec3 Stock, Vec3 Body)> features, List<(string Name, Vec3 Stock, Vec3 Body)> ears )
	{
		int neck = Array.IndexOf( stock.BoneNames, "neck_0" ), head = Array.IndexOf( stock.BoneNames, "head" ), clavicle = Array.IndexOf( stock.BoneNames, "clavicle_L" );
		if ( neck < 0 || head < 0 || clavicle < 0 ) return (null, null);
		Vec3? mouthS = null, mouthB = null, noseS = null, noseB = null;
		foreach ( var (name, at, there) in features )
		{
			if ( name == "mouth.mid" ) { mouthS = at; mouthB = there; }
			if ( name == "nose" ) { noseS = at; noseB = there; }
		}
		if ( mouthS == null || noseS == null ) return (null, null);
		Vec3 low = stock.BonePositions[neck], high = stock.BonePositions[head];
		float baseZ = stock.BonePositions[clavicle].Z;
		float leftS = float.NaN, rightS = float.NaN, leftB = float.NaN, rightB = float.NaN;
		if ( ears != null )
			foreach ( var (name, at, there) in ears )
			{
				if ( name == "earL.bottom" ) { leftS = at.Z; leftB = there.Z; }
				if ( name == "earR.bottom" ) { rightS = at.Z; rightB = there.Z; }
			}
		var from = NeckChart.Build( old.Skin(), low, high, baseZ, mouthS.Value.Z, noseS.Value.Z, leftS, rightS );
		var to = NeckChart.Build( skin, low, high, baseZ, mouthB.Value.Z, noseB.Value.Z, leftB, rightB );
		return from == null || to == null ? (null, null) : (from, to);
	}

	/// <summary>How far under the skin paint goes that the stock body hides (see <see cref="OnSkin"/>).</summary>
	public const float Buried = 0.005f * Units.Metre;
	const float HiddenDepth = 0.5f;   // what lies behind an eye's outline is hidden down to this deep, as a share of the eye's width
	const float HiddenStart = 0.0005f * Units.Metre;   // what covers skin from the front is looked for from this far in front of it
	const float HiddenReach = 0.5f;   // and this far, as a share of how far the face's features reach
	const float Covering = 0.02f * Units.Metre;   // skin on the face with more of it this close in front is buried under it
	const float Facing = 0.5f;                     // if that skin faces forward at least this much (cosine)

	// Whether a point is within an outline seen from the front (along X).
	static bool InFront( Vec3[] outline, Vec3 p )
	{
		bool inside = false;
		for ( int a = 0, b = outline.Length - 1; a < outline.Length; b = a++ )
		{
			Vec3 pa = outline[a], pb = outline[b];
			if ( (pa.Z > p.Z) != (pb.Z > p.Z) && p.Y < pa.Y + (pb.Y - pa.Y) * (p.Z - pa.Z) / (pb.Z - pa.Z) ) inside = !inside;
		}
		return inside;
	}

	/// <summary>
	/// How many times thicker the other body is than the stock one, per welded stock vertex.
	/// The skeleton tells how long each part of a body is, not how thick: a girl's waist on a
	/// spine as long as a man's is two thirds as deep. Each skin vertex is measured straight
	/// through the body along its normal, from the front of a waist to its back, say, on both
	/// bodies (the other one where that skin went), and the ratios are smoothed over the skin:
	/// a measure that slipped between two fingers or grazed an ear is one wild value among
	/// sensible neighbours. Where nothing could be measured the neighbours' value spreads in.
	///
	/// The head and the feet count as no thinner than the stock ones. They are sized as a whole
	/// already: <see cref="Repose"/> measures the head on its skin, and a foot changes as one
	/// piece (see GarmentFit's Stiffen). Nor do lines through them say much: eyes, a mouth and
	/// ears break up the inside of a head, and under a shoe is a sole, not room.
	/// </summary>
	static float[] MeasureThickness( SkinnedGeometry stock, TriMesh old, TriMesh other, Vec3[] oldNormals, int[] weld, int[] firstOf, Vec3[] move, List<(int A, int B)> edges )
	{
		int bones = stock.BoneNames.Length;
		var whole = new bool[bones];
		for ( int b = 0; b < bones; b++ )   // parents come first
		{
			string name = stock.BoneNames[b];
			int parent = stock.BoneParents[b];
			whole[b] = name == "head" || name.StartsWith( "ankle_" ) || (parent >= 0 && whole[parent]);
		}

		int n = firstOf.Length;
		var normals = new Vec3[n];
		for ( int v = 0; v < weld.Length; v++ ) normals[weld[v]] += oldNormals[v];
		var ratio = new Vec3[n];
		var known = new bool[n];
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int i = 0; i < n; i++ )
		{
			int v = firstOf[i];
			float onWhole = 0;
			for ( int j = 0; j < 4; j++ )
			{
				int bone = stock.BoneIndex[v * 4 + j];
				if ( bone >= 0 && whole[bone] ) onWhole += stock.BoneWeight[v * 4 + j];
			}
			if ( onWhole >= 0.5f )
			{
				ratio[i] = new Vec3( 1, 0, 0 );
				known[i] = true;
				continue;
			}

			float length = normals[i].Length();
			if ( length < 1e-20f ) continue;
			Vec3 normal = normals[i] / length;
			float ours = Through( old, old.Verts[v], normal, hits );
			if ( ours < MinThrough ) continue;
			float theirs = Through( other, old.Verts[v] + move[i], normal, hits );
			if ( theirs <= 0 ) continue;
			ratio[i] = new Vec3( theirs / ours, 0, 0 );
			known[i] = true;
		}

		MeshTools.Relax( ratio, known, edges, ThicknessSmoothing, 0.5f );
		for ( int pass = 0; pass < 50 && Array.IndexOf( known, false ) >= 0; pass++ )
			MeshTools.Relax( ratio, known, edges, 1, 0f );

		var result = new float[n];
		for ( int i = 0; i < n; i++ )
			result[i] = known[i] ? Math.Clamp( ratio[i].X, 0.4f, 2.5f ) : 1f;
		return result;
	}

	/// <summary>
	/// How far a line into the body from a point on its skin, against the normal, runs inside
	/// it. Pieces stuck into each other (a limb into a torso, an eyeball into a head) count as
	/// one. 0 if the line doesn't start on the skin.
	/// </summary>
	static float Through( TriMesh body, Vec3 p, Vec3 normal, List<(float T, bool Leaving, int Tri)> hits )
	{
		body.Crossings( p + normal * ThroughStart, normal * -1f, Units.Metre, hits );
		if ( hits.Count == 0 || hits[0].Leaving || hits[0].T > 2f * ThroughStart ) return 0;
		int depth = 0;
		foreach ( var hit in hits )
		{
			depth += hit.Leaving ? -1 : 1;
			if ( depth == 0 ) return hit.T - hits[0].T;
		}
		return 0;
	}

	static bool UvMatch( UvLookup uv, SkinnedGeometry stock, TriMesh old, Vec3[] oldNormals, int v, out Vec3 p )
	{
		if ( !uv.Find( stock.Uvs[v], out p, out var normal ) ) return false;

		// Same UV only means same skin if the layout is the same. A point that lands far away
		// or faces the other way came from someone else's layout.
		float facing = Vec3.Dot( normal, oldNormals[v] ) / (normal.Length() * oldNormals[v].Length() + 1e-20f);
		return (p - old.Verts[v]).Length() <= UvMaxMove && facing > 0.3f;
	}

	/// <summary>
	/// One segment per bone, lying inside the body. It starts at the joint and runs through the
	/// centre of the skin that bone drives, about as far again. For a limb that lands on the
	/// next joint, for the head it gives a vertical axis through the skull. Bones that drive too
	/// little skin stay a single point.
	/// </summary>
	static void BoneAxes( SkinnedGeometry stock, TriMesh body, out Vec3[] a, out Vec3[] b )
	{
		int bones = stock.BoneNames.Length;
		a = MeshTools.Copy( stock.BonePositions );
		b = MeshTools.Copy( stock.BonePositions );

		// Twist and other helper bones (children with no children of their own) take skin
		// away from the bone they help. Count their skin as the parent's too.
		var hasChild = new bool[bones];
		for ( int i = 0; i < bones; i++ )
			if ( stock.BoneParents[i] >= 0 ) hasChild[stock.BoneParents[i]] = true;
		var owner = new int[bones];
		for ( int i = 0; i < bones; i++ )
			owner[i] = stock.BoneParents[i] >= 0 && !hasChild[i] ? stock.BoneParents[i] : -1;

		// Area-weighted, so a densely modelled face doesn't drag the head's centre forward.
		var area = new float[bones];
		var centre = new Vec3[bones];
		var triangles = new int[bones];
		var weights = new Dictionary<int, float>();
		for ( int t = 0; t < body.TriCount; t++ )
		{
			int dominant = MeshTools.DominantBone( stock, body.Tris, t, weights );
			if ( dominant < 0 ) continue;
			float size = 0.5f * body.TriNormals[t].Length();
			Vec3 mid = (body.Verts[body.Tris[t * 3]] + body.Verts[body.Tris[t * 3 + 1]] + body.Verts[body.Tris[t * 3 + 2]]) / 3f;
			for ( int bone = dominant; bone >= 0; bone = bone == dominant ? owner[dominant] : -1 )
			{
				area[bone] += size;
				centre[bone] += mid * size;
				triangles[bone]++;
			}
		}

		for ( int bone = 0; bone < bones; bone++ )
		{
			if ( triangles[bone] < MinOwnTriangles || area[bone] < 1e-9f ) continue;
			Vec3 c = centre[bone] / area[bone];
			if ( !body.Inside( c ) ) continue;
			float radius = body.Nearest( c, out _, out _ );

			var ends = new[] { stock.BonePositions[bone], c * 2f - stock.BonePositions[bone] };
			for ( int k = 0; k < 2; k++ )
			{
				// Pull the end towards the centre until it is properly inside. Rays that
				// start on the skin are useless.
				for ( int tries = 0; tries < 12; tries++ )
				{
					if ( body.Inside( ends[k] ) && body.Nearest( ends[k], out _, out _ ) >= 0.5f * radius ) break;
					ends[k] = c + (ends[k] - c) * 0.75f;
				}
			}

			a[bone] = ends[0];
			b[bone] = ends[1];
		}
	}

	/// <summary>
	/// Puts a point blended from several bones' answers (<paramref name="move"/> from <paramref name="s"/>)
	/// onto the other body's skin when it ended up outside it: no further than half the distance
	/// between the answers it was blended from, the most a blend can come off, and only onto skin
	/// facing the way the stock skin does, not some other part of the body nearby.
	/// </summary>
	static void OntoSkin( TriMesh other, Vec3 oldNormal, Vec3 s, Vec3[] targets, int count, ref Vec3 move )
	{
		float spread = 0;
		for ( int a = 0; a < count; a++ )
			for ( int b = a + 1; b < count; b++ )
				spread = MathF.Max( spread, (targets[a] - targets[b]).Length() );
		if ( spread < 1e-4f * Units.Metre ) return;

		Vec3 p = s + move;
		float distance = other.Nearest( p, out var on, out int tri );
		if ( tri < 0 || distance > 0.5f * spread || other.Inside( p ) ) return;
		Vec3 normal = other.TriNormals[tri];
		if ( Vec3.Dot( normal, oldNormal ) < SameWay * normal.Length() * oldNormal.Length() ) return;
		move = on - s;
	}

	const float SameWay = 0.5f;   // skin facing within 60 degrees of the stock skin's way is the same side of the body

	/// <summary>
	/// Where the skin point s of the old body sits on the other body, seen from one bone.
	///
	/// The ray runs from the bone axis through s. Usually s is where the ray finally leaves
	/// the body, but not always: on an ear it leaves twice, inside the mouth it may be entering
	/// rather than leaving. So find which crossing s is on the old body and take the matching
	/// one on the other body.
	/// </summary>
	static bool FollowSkin( TriMesh old, TriMesh other, SkinByBone otherSkin, int bone, Vec3 a, Vec3 b, Vec3 s, List<(float T, bool Leaving, int Tri)> hits, out Vec3 target )
	{
		target = s;
		Vec3 q = MeshTools.ClosestOnSegment( s, a, b );
		Vec3 v = s - q;
		float r0 = v.Length();
		if ( r0 < 1e-6f * Units.Metre ) return false;
		Vec3 d = v / r0;
		float reach = r0 + LookBeyond;

		old.Crossings( q, d, Units.Metre * 10f, hits );
		int at = -1;
		for ( int i = 0; i < hits.Count; i++ )
			if ( MathF.Abs( hits[i].T - r0 ) <= 0.001f * Units.Metre && (at < 0 || MathF.Abs( hits[i].T - r0 ) < MathF.Abs( hits[at].T - r0 )) ) at = i;
		if ( at < 0 ) return false;   // the ray never registered s, most likely it ran along the surface

		bool leaving = hits[at].Leaving;
		bool outer = leaving && Exterior( hits, at );
		int order = 0, oldCount = 0;
		for ( int i = 0; i < hits.Count && hits[i].T <= reach; i++ )
		{
			if ( !Matches( hits, i, leaving, outer ) ) continue;
			if ( i < at ) order++;
			oldCount++;
		}

		other.Crossings( q, d, Units.Metre * 10f, hits );
		var same = new List<float>();
		for ( int i = 0; i < hits.Count && hits[i].T <= reach; i++ )
		{
			if ( !Matches( hits, i, leaving, outer ) ) continue;
			if ( otherSkin != null && otherSkin.OwnsNearby( hits[i].Tri, bone ) < 0.3f ) continue;   // some other limb
			same.Add( hits[i].T );
		}
		if ( same.Count == 0 ) return false;

		float r1 = same[0];
		if ( same.Count == oldCount && order < same.Count )
		{
			r1 = same[order];
		}
		else
		{
			foreach ( float t in same )
				if ( MathF.Abs( t - r0 ) < MathF.Abs( r1 - r0 ) ) r1 = t;
		}

		// A ray can slip through a gap in the other body (between two boxes at an elbow, say)
		// and land on some far away part. Skin doesn't end up several times further from its bone.
		// It can end up a lot nearer: an anime shoulder lies a centimetre over the collarbone
		// where the stock one has 3 to 7 cm of muscle on it. Turned away as a slip (at a fifth
		// of the way), the top of the shoulder took the neck's ray alone and went onto the side
		// of the neck, and a strap over it with it.
		if ( r1 > 3f * r0 + 0.05f * Units.Metre || r1 < NearestShare * r0 ) return false;

		target = q + d * r1;
		return true;
	}

	/// <summary>
	/// True if the ray is outside the body right after hit number `at`. On a body made of
	/// overlapping pieces most surfaces are buried inside another piece and don't count as skin.
	/// </summary>
	static bool Exterior( List<(float T, bool Leaving, int Tri)> hits, int at )
	{
		int depth = 0;
		for ( int i = at + 1; i < hits.Count; i++ )
			depth += hits[i].Leaving ? 1 : -1;
		return depth <= 0;
	}

	static bool Matches( List<(float T, bool Leaving, int Tri)> hits, int i, bool leaving, bool outer )
	{
		if ( hits[i].Leaving != leaving ) return false;
		return !outer || Exterior( hits, i );
	}

	/// <summary>Finds the 3D point on a body for a UV coordinate.</summary>
	sealed class UvLookup
	{
		const int Cells = 128;
		readonly TriMesh body;
		readonly Vec2[] uvs;
		readonly List<int>[] grid = new List<int>[Cells * Cells];
		readonly float minU, minV, scaleU, scaleV;

		public UvLookup( TriMesh body, Vec2[] uvs )
		{
			this.body = body;
			this.uvs = uvs;
			float maxU = float.MinValue, maxV = float.MinValue;
			minU = minV = float.MaxValue;
			foreach ( var uv in uvs )
			{
				minU = MathF.Min( minU, uv.X ); maxU = MathF.Max( maxU, uv.X );
				minV = MathF.Min( minV, uv.Y ); maxV = MathF.Max( maxV, uv.Y );
			}
			scaleU = Cells / MathF.Max( maxU - minU, 1e-6f );
			scaleV = Cells / MathF.Max( maxV - minV, 1e-6f );

			for ( int t = 0; t < body.TriCount; t++ )
			{
				Vec2 a = uvs[body.Tris[t * 3]], b = uvs[body.Tris[t * 3 + 1]], c = uvs[body.Tris[t * 3 + 2]];
				int u0 = Cell( MathF.Min( a.X, MathF.Min( b.X, c.X ) ), minU, scaleU ), u1 = Cell( MathF.Max( a.X, MathF.Max( b.X, c.X ) ), minU, scaleU );
				int v0 = Cell( MathF.Min( a.Y, MathF.Min( b.Y, c.Y ) ), minV, scaleV ), v1 = Cell( MathF.Max( a.Y, MathF.Max( b.Y, c.Y ) ), minV, scaleV );
				for ( int v = v0; v <= v1; v++ )
					for ( int u = u0; u <= u1; u++ )
						(grid[v * Cells + u] ??= new List<int>()).Add( t );
			}
		}

		static int Cell( float value, float min, float scale ) => Math.Clamp( (int)((value - min) * scale), 0, Cells - 1 );

		public bool Find( Vec2 uv, out Vec3 point, out Vec3 normal )
		{
			point = normal = default;
			var cell = grid[Cell( uv.Y, minV, scaleV ) * Cells + Cell( uv.X, minU, scaleU )];
			if ( cell == null ) return false;

			foreach ( int t in cell )
			{
				int ia = body.Tris[t * 3], ib = body.Tris[t * 3 + 1], ic = body.Tris[t * 3 + 2];
				Vec2 a = uvs[ia], b = uvs[ib], c = uvs[ic];
				float e0x = b.X - a.X, e0y = b.Y - a.Y, e1x = c.X - a.X, e1y = c.Y - a.Y, px = uv.X - a.X, py = uv.Y - a.Y;
				float den = e0x * e1y - e1x * e0y;
				if ( MathF.Abs( den ) < 1e-14f ) continue;
				float v = (px * e1y - e1x * py) / den;
				float w = (e0x * py - px * e0y) / den;
				if ( v < -1e-4f || w < -1e-4f || v + w > 1.0001f ) continue;

				point = body.Verts[ia] * (1f - v - w) + body.Verts[ib] * v + body.Verts[ic] * w;
				normal = body.TriNormals[t];
				return true;
			}

			return false;
		}
	}
}

/// <summary>
/// Shared mesh helpers: welding, edges, smoothing a per-vertex field.
/// </summary>
public static class MeshTools
{
	/// <summary>
	/// Vertices split along seams share a position. Returns, for each vertex, the index of the
	/// welded vertex it belongs to.
	/// </summary>
	public static int[] Weld( Vec3[] verts, out int count )
	{
		float scale = 1f / (1e-5f * Units.Metre);
		var seen = new Dictionary<(long, long, long), int>();
		var map = new int[verts.Length];
		for ( int i = 0; i < verts.Length; i++ )
		{
			var key = ((long)MathF.Round( verts[i].X * scale ), (long)MathF.Round( verts[i].Y * scale ), (long)MathF.Round( verts[i].Z * scale ));
			if ( !seen.TryGetValue( key, out int index ) )
			{
				index = seen.Count;
				seen[key] = index;
			}
			map[i] = index;
		}

		count = seen.Count;
		return map;
	}

	/// <summary>Unique edges between welded vertices.</summary>
	public static List<(int A, int B)> Edges( int[] tris, int[] weld )
	{
		var seen = new HashSet<long>();
		var edges = new List<(int, int)>();
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			for ( int k = 0; k < 3; k++ )
			{
				int a = weld[tris[t + k]], b = weld[tris[t + (k + 1) % 3]];
				if ( a == b ) continue;
				if ( a > b ) (a, b) = (b, a);
				if ( seen.Add( ((long)a << 32) | (uint)b ) ) edges.Add( (a, b) );
			}
		}

		return edges;
	}

	static void NeighbourMean( Vec3[] field, bool[] valid, List<(int A, int B)> edges, Vec3[] mean, int[] counts )
	{
		Array.Clear( mean, 0, mean.Length );
		Array.Clear( counts, 0, counts.Length );
		foreach ( var (a, b) in edges )
		{
			if ( valid[b] ) { mean[a] += field[b]; counts[a]++; }
			if ( valid[a] ) { mean[b] += field[a]; counts[b]++; }
		}

		for ( int i = 0; i < mean.Length; i++ )
			if ( counts[i] > 0 ) mean[i] = mean[i] / counts[i];
	}

	/// <summary>
	/// A ray that slips between two fingers or grazes an ear gives a wild value. Throws out
	/// anything that disagrees badly with its neighbours; smoothing fills it back in.
	/// </summary>
	public static bool[] DropOutliers( Vec3[] field, bool[] valid, List<(int A, int B)> edges )
	{
		var kept = MeshTools.Copy( valid );
		var mean = new Vec3[field.Length];
		var counts = new int[field.Length];
		for ( int pass = 0; pass < 2; pass++ )
		{
			NeighbourMean( field, kept, edges, mean, counts );
			for ( int i = 0; i < field.Length; i++ )
				if ( counts[i] >= 2 && (field[i] - mean[i]).Length() > 0.01f * Units.Metre + 0.75f * mean[i].Length() ) kept[i] = false;
		}

		return kept;
	}

	/// <summary>
	/// Laplacian smoothing of a per-vertex vector field, in place. Vertices without a value
	/// pick one up from their neighbours and are marked known.
	/// </summary>
	public static void Relax( Vec3[] field, bool[] known, List<(int A, int B)> edges, int iterations, float amount )
	{
		var mean = new Vec3[field.Length];
		var counts = new int[field.Length];
		for ( int pass = 0; pass < iterations; pass++ )
		{
			NeighbourMean( field, known, edges, mean, counts );
			for ( int i = 0; i < field.Length; i++ )
			{
				if ( counts[i] == 0 ) continue;
				field[i] = known[i] ? field[i] + (mean[i] - field[i]) * amount : mean[i];
			}

			for ( int i = 0; i < field.Length; i++ )
				if ( counts[i] > 0 ) known[i] = true;
		}
	}

	const float SkinShare = 0.05f;   // a piece of the body with less than this share of the biggest one's area is on the skin, not skin

	/// <summary>
	/// Per triangle of a body, whether it is skin and not something on it: lashes, brows, a
	/// painted line round an eye, the eyes themselves, the inside of the mouth. Those are pieces
	/// of their own, small next to the skin.
	/// </summary>
	public static bool[] SkinTriangles( Vec3[] positions, int[] tris )
	{
		var weld = Weld( positions, out int count );
		var piece = GarmentFit.Islands( count, Edges( tris, weld ) );
		var pieceArea = new Dictionary<int, float>();
		var triArea = new float[tris.Length / 3];
		for ( int t = 0; t < triArea.Length; t++ )
		{
			Vec3 a = positions[tris[t * 3]], b = positions[tris[t * 3 + 1]], c = positions[tris[t * 3 + 2]];
			triArea[t] = 0.5f * Vec3.Cross( b - a, c - a ).Length();
			int p = piece[weld[tris[t * 3]]];
			pieceArea[p] = (pieceArea.TryGetValue( p, out float have ) ? have : 0) + triArea[t];
		}
		float biggest = 0;
		foreach ( float a in pieceArea.Values ) biggest = MathF.Max( biggest, a );

		var result = new bool[triArea.Length];
		for ( int t = 0; t < triArea.Length; t++ )
			result[t] = pieceArea[piece[weld[tris[t * 3]]]] >= SkinShare * biggest;
		return result;
	}

	/// <summary>A copy of an array. (Array.Clone is not on the s&box whitelist.)</summary>
	public static T[] Copy<T>( T[] source )
	{
		var copy = new T[source.Length];
		Array.Copy( source, copy, source.Length );
		return copy;
	}

	public static Vec3 ClosestOnSegment( Vec3 p, Vec3 a, Vec3 b )
	{
		Vec3 ab = b - a;
		float den = ab.LengthSquared();
		if ( den < 1e-12f ) return a;
		return a + ab * Math.Clamp( Vec3.Dot( p - a, ab ) / den, 0f, 1f );
	}

	public const float Turned = 0.2f;     // a triangle turned this far over (cosine) is inside out
	const int EvenPasses = 24;
	const int EvenRings = 3;              // how far round a fold the evening out reaches, in rings of neighbours
	const int AlikePasses = 16;

	/// <summary>
	/// Keeps a displacement of a surface from turning any of its triangles inside out. Where it
	/// does, the displacement is evened out over the fold and a few rings of neighbours round
	/// it, and if that isn't enough, the corners of each such triangle are moved alike, which
	/// no triangle can turn over with. Everywhere else it is left as it is.
	/// </summary>
	/// <param name="points">Per welded vertex, where it is before the displacement.</param>
	/// <param name="weld">Per vertex of <paramref name="tris"/>, its welded vertex.</param>
	/// <param name="shift">Per welded vertex, the displacement. Changed in place.</param>
	/// <param name="alongside">Per welded vertex, something that goes with the displacement and is evened out with it (a morph's turn of the normals), or null.</param>
	/// <param name="skip">Per triangle, whether to leave it out (null for none).</param>
	/// <returns>Whether anything had to change.</returns>
	public static bool Untangle( Vec3[] points, int[] tris, int[] weld, List<(int A, int B)> edges, Vec3[] shift, Vec3[] alongside, bool[] skip )
	{
		int count = points.Length;
		var flagged = new bool[count];
		var region = new bool[count];
		var reach = new bool[count];
		bool changed = false;
		for ( int pass = 0; pass < EvenPasses + AlikePasses; pass++ )
		{
			bool alike = pass >= EvenPasses;
			Array.Clear( flagged, 0, count );
			bool any = false;
			for ( int t = 0; t < tris.Length; t += 3 )
			{
				if ( skip != null && skip[t / 3] ) continue;
				int a = weld[tris[t]], b = weld[tris[t + 1]], c = weld[tris[t + 2]];
				if ( a == b || b == c || a == c ) continue;
				var before = Vec3.Cross( points[b] - points[a], points[c] - points[a] );
				var after = Vec3.Cross( points[b] + shift[b] - points[a] - shift[a], points[c] + shift[c] - points[a] - shift[a] );
				float lb = before.Length(), la = after.Length();
				if ( lb < 1e-12f ) continue;
				if ( la > 1e-12f && Vec3.Dot( before, after ) >= Turned * lb * la ) continue;
				flagged[a] = flagged[b] = flagged[c] = true;
				any = true;
				if ( !alike ) continue;
				var mean = (shift[a] + shift[b] + shift[c]) / 3f;
				shift[a] = shift[b] = shift[c] = mean;
				if ( alongside == null ) continue;
				var meanAlong = (alongside[a] + alongside[b] + alongside[c]) / 3f;
				alongside[a] = alongside[b] = alongside[c] = meanAlong;
			}
			if ( !any ) break;
			changed = true;
			if ( alike ) continue;

			for ( int i = 0; i < count; i++ ) region[i] |= flagged[i];
			Array.Copy( region, reach, count );
			for ( int ring = 0; ring < EvenRings; ring++ )
			{
				var was = Copy( reach );
				foreach ( var (a, b) in edges )
					if ( was[a] || was[b] ) reach[a] = reach[b] = true;
			}
			Even( shift, reach, edges );
			if ( alongside != null ) Even( alongside, reach, edges );
		}
		return changed;
	}

	// Values in the region halfway to their neighbours' average.
	static void Even( Vec3[] field, bool[] region, List<(int A, int B)> edges )
	{
		var sum = new Vec3[field.Length];
		var count = new int[field.Length];
		foreach ( var (a, b) in edges )
		{
			if ( region[a] ) { sum[a] += field[b]; count[a]++; }
			if ( region[b] ) { sum[b] += field[a]; count[b]++; }
		}
		for ( int i = 0; i < field.Length; i++ )
			if ( count[i] > 0 ) field[i] += (sum[i] / count[i] - field[i]) * 0.5f;
	}

	/// <summary>The bone with the most weight over a triangle's three vertices, or -1.</summary>
	public static int DominantBone( SkinnedGeometry geo, int[] tris, int tri, Dictionary<int, float> scratch )
	{
		scratch.Clear();
		for ( int k = 0; k < 3; k++ )
		{
			int v = tris[tri * 3 + k];
			for ( int j = 0; j < 4; j++ )
			{
				int bone = geo.BoneIndex[v * 4 + j];
				float w = geo.BoneWeight[v * 4 + j];
				if ( bone < 0 || w <= 0 ) continue;
				scratch[bone] = scratch.TryGetValue( bone, out float sum ) ? sum + w : w;
			}
		}

		int best = -1;
		float most = 0;
		foreach ( var pair in scratch )
			if ( pair.Value > most ) { most = pair.Value; best = pair.Key; }
		return best;
	}
}
