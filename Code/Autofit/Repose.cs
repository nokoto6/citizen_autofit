using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// Puts a body into the stock citizen's bind pose and proportions, and takes a fitted garment
/// back out of them.
///
/// A model can use the citizen's bone names with a different rest pose: taller, longer legs,
/// arms at another angle, even facing another way. When clothing is bone-merged onto it, the
/// engine moves every garment vertex from the citizen's rest pose to that model's. So the two
/// bodies can only be compared after undoing that: each body vertex is carried from its own
/// skeleton's rest pose to the citizen's, bone by bone, blended by the body's skin weights.
///
/// Bone merging doesn't scale anything, though. On a model one and a half times the citizen's
/// size every part of the body is one and a half times too big for the citizen's skeleton.
/// So each bone also gets a scale (how much longer it is than the citizen's), the body is
/// shrunk by it on the way in, and the fitted garment is grown by it on the way out.
///
/// A body that already shares the citizen's skeleton comes out unchanged.
/// </summary>
public sealed class Repose
{
	/// <summary>The body's vertices in the stock pose and proportions.</summary>
	public Vec3[] Positions;

	/// <summary>How many of the stock skeleton's bones the body has. Too few and clothing can't follow it at all.</summary>
	public int SharedBones;

	/// <summary>Average distance the vertices were moved by.</summary>
	public float Moved;

	/// <summary>For each stock bone, how many times bigger the body is around it. 1 where the body has no such bone.</summary>
	public float[] BoneScale;

	/// <summary>
	/// For each stock bone, how many times longer the body is along it. Same as <see cref="BoneScale"/>
	/// except for a bone measured on its skin (the head), which can be wider and no taller.
	/// </summary>
	public float[] BoneLengthScale;

	// Each stock bone's own direction in the stock bind pose, what BoneLengthScale goes along.
	Vec3[] stockAxes;

	/// <summary>An offset from a stock bone's joint, grown to the body's size around that bone (or shrunk, with <paramref name="shrink"/>).</summary>
	Vec3 Scaled( Vec3 offset, int bone, bool shrink )
	{
		float across = BoneScale[bone], along = BoneLengthScale[bone];
		if ( shrink ) { across = 1f / across; along = 1f / along; }
		Vec3 axis = stockAxes[bone];
		float d = Vec3.Dot( offset, axis );
		return axis * (d * along) + (offset - axis * d) * across;
	}

	/// <summary>
	/// Face landmarks: where each of the citizen's eyes is, and how far this body's eye is from
	/// there once the head has been lined up. Rays from the bones can't tell where a face's
	/// features are, the eye bones can.
	/// </summary>
	public List<(Vec3 At, Vec3 Shift, float Reach)> Landmarks = new();

	/// <summary>The neck on the stock body and on this one, to keep cloth over it at the share of the neck it covers. Null when the chin or the nape wasn't found.</summary>
	public Neck Neck;

	/// <summary>The trunk from the crotch up on this body, in the stock pose and in its rest pose, to keep cloth on its lower part at the share of the trunk it covers. Null when there is no crotch to find.</summary>
	public Trunk Trunk;

	// The body in its rest pose and its triangles, and its skin in the stock pose: what
	// OntoSkin takes paint back by.
	Vec3[] bodyRest;
	int[] bodyIndices;
	TriMesh posedSkin;
	TriMesh restBody;   // the body in its rest pose, what cloth hung from the hips stays out of

	// The body in the stock pose and proportions, with its skin weights as stock bones and
	// without the eyes: how cloth lying on its skin goes back to its rest pose (RestOf).
	TriMesh skinMesh;
	int[] skinBones;
	float[] skinWeights;

	/// <summary>The front of each eye, on the stock body and on this one (in the stock pose). What glasses go by.</summary>
	public List<(Vec3 Stock, Vec3 Body)> Eyes = new();
	/// <summary>The face down its middle, top to bottom: between the eyes, the tip of the nose, the mouth, the chin. Empty when a face has no mouth to find.</summary>
	public List<(Vec3 Stock, Vec3 Body)> Face = new();
	/// <summary>
	/// The features of the face found on both bodies (<see cref="FaceMarks"/>): the openings of the
	/// eyes, where the brows start, the mouth's corners, the nose, the chin, the crown. On the stock
	/// body and on this one, in the stock pose. What paint on the skin goes by.
	/// </summary>
	public List<(string Name, Vec3 Stock, Vec3 Body)> Features = new();

	const float BrowLength = 1.4f;     // a citizen's brows are as long as this many times its eye's width
	const float BrowHeight = 0.7f;     // as tall as this share of it
	const float BrowInward = 0.1f;     // and their middle is this share of it in from the eye's
	const float EyeReach = 0.07f * Units.Metre;    // how far around an eye the skin slides with it
	const float BustReach = 0.08f * Units.Metre;   // how far around the front of the chest
	const float FaceReach = 0.03f * Units.Metre;   // how far around the nose, the mouth and the chin
	const float NapeReach = 0.05f * Units.Metre;   // how far around the nape: the back of the neck, short of the ears and the jaw
	const float NapeDepth = 0.003f * Units.Metre;  // the nape: this close to the foremost point of the back of the neck
	const float NapeTurn = 0.005f * Units.Metre;   // how far back the profile goes above and below a nape
	const float NapeSpan = 0.05f * Units.Metre;    // within this far up and down
	const float SkullBack = 0.005f * Units.Metre;  // the skull begins where the back of the head is this far behind the nape
	const float MouthFrontDepth = 0.004f * Units.Metre;  // how deep the front of the mouth is taken
	const float ProfileHalfWidth = 0.006f * Units.Metre; // how far either side of the middle the profile is read
	const float ProfileDepth = 0.17f * Units.Metre;      // how far below the eyes
	const float ProfileSlice = 0.003f * Units.Metre;
	const float NoseDepth = 0.09f * Units.Metre;         // the nose is no further below the eyes than this
	const float ChinBack = 0.045f * Units.Metre;         // the profile this far behind the nose tip is the neck
	const float MinBust = 0.02f * Units.Metre;     // a chest front standing out less than this is flat
	const float BustDepth = 0.1f * Units.Metre;    // how far under the front of the chest to look for the fold under a bust
	const float MinTipAcross = 0.4f;                // a bust tip is at least this far out to the shoulder

	/// <summary>
	/// The body's rest pose, per stock bone: where that bone is on the body and which way it
	/// points, in the body's model space. A stock bone the body doesn't have is placed under
	/// the nearest one it does have, where the stock skeleton puts it.
	/// </summary>
	public Vec3[] RestPositions;
	public float[][] RestRotations;

	/// <summary>Per stock bone, whether the body has a bone of that name.</summary>
	public bool[] BodyHas;

	// Per stock bone, whose place the skin it drives is moved by, in and out of the stock pose.
	int[] follow;

	// Per stock bone, the top bone of the leg it is part of (its hip joint), -1 for none. And per
	// side the hip, the knee and the ankle, for cloth hanging round both legs (Hanging).
	int[] hipOf;
	int[] hips, knees, ankles;

	/// <summary>
	/// A bone at the end of a chain (the toes, the tip of a finger) has nothing to tell which
	/// way it points: the body puts it wherever its joint is, the stock skeleton somewhere
	/// else. Moved to its own stock place, its skin is sheared off the bone before it (toes a
	/// couple of centimetres to the side of the foot), and a shoe fitted there comes back
	/// creased at the joint. Its skin moves with the bone before it instead. Helpers along a
	/// limb (twists) and bones that drive no skin keep their own place.
	/// </summary>
	static int[] Follow( SkinnedGeometry stock )
	{
		int count = stock.BoneNames.Length;
		var follow = new int[count];
		var hasChild = new bool[count];
		for ( int b = 0; b < count; b++ )
		{
			follow[b] = b;
			if ( stock.BoneParents[b] >= 0 ) hasChild[stock.BoneParents[b]] = true;
		}
		for ( int b = 0; b < count; b++ )
		{
			string name = stock.BoneNames[b];
			if ( hasChild[b] || stock.BoneParents[b] < 0 || name.StartsWith( "eye_" ) || name.Contains( "twist" ) || name.Contains( "helper" ) || name.Contains( "IK" ) || name.Contains( "ikrule" ) || name.StartsWith( "hold_" ) || name.StartsWith( "aim_" ) ) continue;
			follow[b] = stock.BoneParents[b];
		}
		return follow;
	}

	// Per stock bone: the stock bind pose, to take things out of, and the rotation that
	// carries something placed in it over to the body's rest pose.
	Vec3[] stockPositions;
	float[][] stockRotations;
	int[] stockParents;
	float[][] carry;

	public static Repose ToStockPose( SkinnedGeometry body, SkinnedGeometry stock )
	{
		var result = new Repose();
		var stockBones = new Dictionary<string, int>();
		for ( int i = 0; i < stock.BoneNames.Length; i++ )
			stockBones[stock.BoneNames[i]] = i;

		// For each body bone: its counterpart on the stock skeleton. A bone the citizen
		// doesn't have rides on the nearest ancestor that it does have.
		int bones = body.BoneNames.Length;
		var from = new int[bones];   // body bone whose rest pose to undo
		var to = new int[bones];     // stock bone whose rest pose to apply
		for ( int i = 0; i < bones; i++ )
		{
			if ( stockBones.ContainsKey( body.BoneNames[i] ) ) result.SharedBones++;
			int bone = i;
			while ( bone >= 0 && !stockBones.ContainsKey( body.BoneNames[bone] ) )
				bone = body.BoneParents[bone];
			from[i] = bone;
			to[i] = bone >= 0 ? stockBones[body.BoneNames[bone]] : -1;
		}

		// Scale of each bone: how far its children are from it, compared to the citizen.
		// A bone with nothing to measure takes its parent's.
		var scale = new float[bones];
		var ratios = new List<float>[bones];
		for ( int i = 0; i < bones; i++ )
		{
			int parent = body.BoneParents[i];
			if ( parent < 0 || from[i] != i || from[parent] != parent ) continue;
			// An eye bone is the pivot of an eyeball, put wherever the eye is built to turn about:
			// on the face for the humans, deep in the head for a VRoid eye. It says nothing about
			// how big the head is.
			if ( body.BoneNames[i].StartsWith( "eye_" ) ) continue;
			float theirs = (body.BonePositions[i] - body.BonePositions[parent]).Length();
			float ours = (stock.BonePositions[to[i]] - stock.BonePositions[to[parent]]).Length();
			if ( ours < 0.02f * Units.Metre ) continue;   // too short to say anything
			(ratios[parent] ??= new List<float>()).Add( theirs / ours );
		}

		// A bone with skin but nothing to measure it by (the head, once the eyes don't count) is
		// measured on the skin itself rather than taking its parent's scale: an anime head is
		// big on a short neck.
		// Such a bone is also measured along itself, to the top of the skin: a head can be wider
		// than the citizen's and no taller, and scaled the same both ways about a joint at its
		// bottom, a hat on it rises off the top.
		TriMesh bodyMesh = null, stockMesh = null;
		var lengthRatio = new float[bones];
		for ( int i = 0; i < bones; i++ )
		{
			if ( ratios[i] != null || from[i] != i || body.BoneParents[i] < 0 || !HasChildren( body, i ) ) continue;
			bodyMesh ??= new TriMesh( body.Positions, body.Indices );
			stockMesh ??= new TriMesh( stock.Positions, stock.Indices );
			float theirs = Thickness( body, bodyMesh, i, out float theirLength );
			if ( theirs <= 0 ) continue;
			float ours = Thickness( stock, stockMesh, to[i], out float ourLength );
			if ( ours <= 0 ) continue;
			ratios[i] = new List<float> { theirs / ours };
			lengthRatio[i] = Math.Clamp( theirLength / ourLength, 0.4f, 2.5f );
		}

		var lengthScale = new float[bones];
		for ( int i = 0; i < bones; i++ )   // parents come first
		{
			int parent = body.BoneParents[i];
			scale[i] = parent >= 0 ? scale[parent] : 1f;
			lengthScale[i] = parent >= 0 ? lengthScale[parent] : 1f;
			if ( ratios[i] == null ) continue;
			ratios[i].Sort();
			scale[i] = Math.Clamp( ratios[i][ratios[i].Count / 2], 0.4f, 2.5f );
			lengthScale[i] = lengthRatio[i] > 0 ? lengthRatio[i] : scale[i];
		}

		int stockCount = stock.BoneNames.Length;
		var bodyBone = new int[stockCount];   // per stock bone, the body's bone of that name
		Array.Fill( bodyBone, -1 );
		for ( int i = 0; i < bones; i++ )
			if ( from[i] == i ) bodyBone[to[i]] = i;

		// Per stock bone the body has: the rotation from the stock bind pose to the body's
		// rest pose, the difference of the two bone frames. It is exactly what bone merging
		// applies to a garment made in the stock pose.
		result.carry = new float[stockCount][];
		for ( int s = 0; s < stockCount; s++ )
			if ( bodyBone[s] >= 0 ) result.carry[s] = Quat.Multiply( body.BoneRotations[bodyBone[s]], Quat.Inverse( stock.BoneRotations[s] ) );

		// Per body bone: rotation, scale and offset that take a point from the body's rest
		// pose to the stock one.
		var turn = new float[bones][];
		var shrink = new float[bones];
		var offset = new Vec3[bones];
		result.BoneScale = new float[stockCount];
		result.BoneLengthScale = new float[stockCount];
		Array.Fill( result.BoneScale, 1f );
		Array.Fill( result.BoneLengthScale, 1f );
		result.stockAxes = new Vec3[stockCount];
		for ( int s = 0; s < stockCount; s++ )
			result.stockAxes[s] = Quat.Rotate( stock.BoneRotations[s], new Vec3( 1, 0, 0 ) );
		for ( int i = 0; i < bones; i++ )
		{
			if ( to[i] < 0 ) continue;
			turn[i] = Quat.Inverse( result.carry[to[i]] );
			shrink[i] = 1f / scale[from[i]];
			offset[i] = stock.BonePositions[to[i]] - Quat.Rotate( turn[i], body.BonePositions[from[i]] ) * shrink[i];
			if ( from[i] == i ) { result.BoneScale[to[i]] = scale[i]; result.BoneLengthScale[to[i]] = lengthScale[i]; }
		}

		// The body's rest pose in the stock skeleton's terms. Clothing is built in this pose,
		// so it is bone-merged onto the body as if it had been made for it. A garment made in
		// the citizen's bind pose and merged onto a body with another one comes out
		// mangled once the body animates, even with every bone name matching.
		result.RestPositions = new Vec3[stockCount];
		result.RestRotations = new float[stockCount][];
		result.stockPositions = stock.BonePositions;
		result.stockRotations = stock.BoneRotations;
		result.stockParents = stock.BoneParents;
		result.BodyHas = new bool[stockCount];
		for ( int s = 0; s < stockCount; s++ )   // parents come first here too
		{
			int b = bodyBone[s];
			if ( b >= 0 )
			{
				result.BodyHas[s] = true;
				result.RestPositions[s] = body.BonePositions[b];
				result.RestRotations[s] = body.BoneRotations[b];
				continue;
			}

			int parent = stock.BoneParents[s];
			if ( parent < 0 )
			{
				result.carry[s] = new[] { 0f, 0f, 0f, 1f };
				result.RestPositions[s] = stock.BonePositions[s];
				result.RestRotations[s] = stock.BoneRotations[s];
				continue;
			}

			// Where the parent went, the child goes too.
			result.carry[s] = result.carry[parent];
			result.RestPositions[s] = result.RestPositions[parent] + Quat.Rotate( result.carry[s], stock.BonePositions[s] - stock.BonePositions[parent] );
			result.RestRotations[s] = Quat.Multiply( result.carry[s], stock.BoneRotations[s] );
		}

		// The skin an eye bone drives goes the way of the head. The eye bone is a pivot,
		// somewhere else on every face; carried by it, a painted iris lands off the face.
		// The skin of a bone at the end of a chain (the toes, a fingertip) goes the way of the
		// bone before it, see Follow.
		result.follow = Follow( stock );
		result.hipOf = new int[stockCount];
		for ( int s = 0; s < stockCount; s++ )   // parents come first
		{
			int parent = stock.BoneParents[s];
			result.hipOf[s] = stock.BoneNames[s] is "leg_upper_L" or "leg_upper_R" ? s : parent >= 0 ? result.hipOf[parent] : -1;
		}
		result.hips = new[] { Array.IndexOf( stock.BoneNames, "leg_upper_L" ), Array.IndexOf( stock.BoneNames, "leg_upper_R" ) };
		result.knees = new[] { Array.IndexOf( stock.BoneNames, "leg_lower_L" ), Array.IndexOf( stock.BoneNames, "leg_lower_R" ) };
		result.ankles = new[] { Array.IndexOf( stock.BoneNames, "ankle_L" ), Array.IndexOf( stock.BoneNames, "ankle_R" ) };
		var carrier = new int[bones];
		for ( int i = 0; i < bones; i++ )
		{
			carrier[i] = i;
			int parent = body.BoneParents[i];
			if ( parent < 0 || turn[parent] == null ) continue;
			if ( body.BoneNames[i].StartsWith( "eye_" ) || (to[i] >= 0 && result.follow[to[i]] != to[i] && to[parent] == result.follow[to[i]]) )
				carrier[i] = parent;
		}

		result.Positions = new Vec3[body.Positions.Length];
		double total = 0;
		for ( int v = 0; v < result.Positions.Length; v++ )
		{
			Vec3 p = body.Positions[v];
			var sum = Vec3.Zero;
			float weight = 0;
			for ( int j = 0; j < 4; j++ )
			{
				int bone = body.BoneIndex[v * 4 + j];
				float w = body.BoneWeight[v * 4 + j];
				if ( bone < 0 || w <= 0 || turn[bone] == null ) continue;
				bone = carrier[bone];
				sum += (stock.BonePositions[to[bone]] + result.Scaled( Quat.Rotate( turn[bone], p - body.BonePositions[from[bone]] ), to[bone], true )) * w;
				weight += w;
			}

			result.Positions[v] = weight > 1e-6f ? sum / weight : p;
			total += (result.Positions[v] - p).Length();
		}

		result.Moved = result.Positions.Length > 0 ? (float)(total / result.Positions.Length) : 0;

		// Face landmarks. Where an eye is on the face is told by the eye itself: the front of
		// what its bone drives (an eyeball, a painted iris). The bone is only the pivot the eye
		// turns about, on the face for the humans, deep in the head for a VRoid eye.
		for ( int i = 0; i < bones; i++ )
		{
			if ( !body.BoneNames[i].StartsWith( "eye_" ) || from[i] != i ) continue;
			int s = to[i];
			if ( !EyeFront( stock, stock.Positions, s, out var ours ) ) continue;
			if ( !EyeFront( body, result.Positions, i, out var theirs ) )
			{
				// Nothing on this face moves with the eye; the pivot is all there is to go by.
				int parent = body.BoneParents[i];
				if ( parent < 0 || turn[parent] == null ) continue;
				theirs = Quat.Rotate( turn[parent], body.BonePositions[i] ) * shrink[parent] + offset[parent];
				ours = stock.BonePositions[s];
			}
			Vec3 shift = theirs - ours;
			if ( shift.Length() < 0.15f * Units.Metre )
			{
				result.Landmarks.Add( (ours, shift, EyeReach) );
				result.Eyes.Add( (ours, theirs) );
			}
		}

		TriMesh reposedMesh = null;
		float napeHeight = float.NaN;
		Vec3? chinAtRest = null;   // the bottom of this body's chin in its rest pose, when the stock body has none
		float skullStock = float.NaN, skullRest = float.NaN;   // where the head begins behind, on the stock body and on this one in its rest pose

		// The lower face: the tip of the nose, the mouth and the chin. Skin found by rays from
		// the skull lands at the same angle on the other face, and a face with its features
		// set lower and closer together (an anime face, the eyes big and low, the mouth near the
		// chin) then has the stock upper lip on its nose: a moustache sat there.
		if ( result.Eyes.Count > 0 )
		{
			Vec3 eyeOurs = Vec3.Zero, eyeTheirs = Vec3.Zero;
			foreach ( var (ours, theirs) in result.Eyes ) { eyeOurs += ours; eyeTheirs += theirs; }
			eyeOurs /= result.Eyes.Count;
			eyeTheirs /= result.Eyes.Count;
			bool stockLower = LowerFace( stock, stock.Positions, eyeOurs, out var stockFace );

			// The body's own neck, read in its rest pose: taken into the stock proportions a face
			// and a neck are stretched with them and a sparse profile breaks up.
			var eyeRest = Vec3.Zero;
			int restEyes = 0;
			for ( int i = 0; i < bones; i++ )
				if ( body.BoneNames[i].StartsWith( "eye_" ) && from[i] == i && EyeFront( body, body.Positions, i, out var front ) ) { eyeRest += front; restEyes++; }
			// The back of a neck read from the collarbones up: from its joint up, a short neck has
			// too little of it left for the nape to turn in.
			int neckOurs = Array.IndexOf( stock.BoneNames, "neck_0" ), neckTheirs = Array.IndexOf( body.BoneNames, "clavicle_L" );
			if ( restEyes > 0 )
			{
				var restMesh = new TriMesh( body.Positions, body.Indices );
				eyeRest /= restEyes;
				// A stock face with no chin to find (the citizen's goes straight down into its
				// chest) leaves nothing to put this one's by, but its chin still tops its neck.
				if ( !stockLower && Chin( restMesh, eyeRest, out var restChin ) ) chinAtRest = restChin;
				if ( neckTheirs >= 0 && neckOurs >= 0 && SkullStart( restMesh, eyeRest, body.BonePositions[neckTheirs].Z, false, out float rest ) )
				{
					skullRest = rest;
					stockMesh ??= new TriMesh( stock.Positions, stock.Indices );
					if ( SkullStart( stockMesh, eyeOurs, stock.BonePositions[neckOurs].Z, true, out float ours ) ) skullStock = ours;
				}
			}
			if ( stockLower && LowerFace( body, result.Positions, eyeTheirs, out var bodyFace ) )
			{
				result.Face.Add( (eyeOurs, eyeTheirs) );
				for ( int k = 0; k < stockFace.Length; k++ )
				{
					result.Face.Add( (stockFace[k], bodyFace[k]) );
					Vec3 shift = bodyFace[k] - stockFace[k];
					if ( shift.Length() < 0.15f * Units.Metre )
						result.Landmarks.Add( (stockFace[k], shift, FaceReach) );
				}
			}

			// The nape, where the back of the neck goes into the skull. Every rig puts its head
			// bone somewhere else along the neck: the citizens well down it, a VRoid body at the
			// bottom of the skull. Rays from the bones only move skin towards or away from them,
			// so the stock nape lands that much higher on such a body, up the back of its head,
			// and a stand-up collar that reaches the citizen's nape reached Aurora's ears. Only
			// the height is taken: how far back the nape is, the rays tell.
			int neck = Array.IndexOf( stock.BoneNames, "neck_0" );
			stockMesh ??= new TriMesh( stock.Positions, stock.Indices );
			reposedMesh ??= new TriMesh( result.Positions, body.Indices );
			if ( neck >= 0 && Nape( stockMesh, eyeOurs, stock.BonePositions[neck].Z, out var napeOurs ) && Nape( reposedMesh, eyeTheirs, stock.BonePositions[neck].Z, out var napeTheirs ) )
			{
				var shift = new Vec3( 0, 0, napeTheirs.Z - napeOurs.Z );
				if ( shift.Length() < 0.15f * Units.Metre )
				{
					result.Landmarks.Add( (napeOurs, shift, NapeReach) );
					napeHeight = napeOurs.Z;
				}
			}
		}

		// The features of the face, seen from the front.
		if ( result.Eyes.Count == 2 )
		{
			stockMesh ??= new TriMesh( stock.Positions, stock.Indices );
			reposedMesh ??= new TriMesh( result.Positions, body.Indices );
			int l = result.Eyes[0].Stock.Y >= result.Eyes[1].Stock.Y ? 0 : 1;
			var ours = FaceMarks.Find( stock, stock.Positions, stockMesh, result.Eyes[l].Stock, result.Eyes[1 - l].Stock );
			var theirs = FaceMarks.Find( body, result.Positions, reposedMesh, result.Eyes[l].Body, result.Eyes[1 - l].Body );
			foreach ( var (name, at) in ours )
				if ( theirs.TryGetValue( name, out var there ) ) result.Features.Add( (name, at, there) );
			// Brows go onto the brows a face has of its own: the brow line, from its inner end to
			// its outer one and from its lower edge to its upper one. A face without them (the
			// citizen's, whose brows are garments) wears them over its lids, where its brow
			// garments draw them: from the top of the lid up, about as long as 1.4 of its eye and
			// 0.7 of it tall, a little in from the eye's middle. That box goes onto the other
			// face's brow, so a brow of the usual size covers it, a bushy one stays bushier and a
			// thin anime brow makes them thinner.
			float middle = 0.5f * (result.Eyes[0].Stock.Y + result.Eyes[1].Stock.Y);
			foreach ( string side in new[] { "L", "R" } )
			{
				string eye = "eye" + side, brow = "brow" + side;
				if ( ours.ContainsKey( brow ) || !ours.TryGetValue( "lid" + side, out var lid ) ) continue;
				if ( !ours.TryGetValue( eye + ".in", out var inS ) || !ours.TryGetValue( eye + ".out", out var outS ) ) continue;
				if ( !theirs.TryGetValue( brow, out var bottom ) || !theirs.TryGetValue( brow + ".top", out var top ) || !theirs.TryGetValue( brow + ".in", out var inner ) || !theirs.TryGetValue( brow + ".out", out var outer ) ) continue;
				float wide = MathF.Abs( outS.Y - inS.Y );
				float inward = middle > 0.5f * (inS.Y + outS.Y) ? 1f : -1f;
				float centre = 0.5f * (inS.Y + outS.Y) + inward * BrowInward * wide;
				float length = BrowLength * wide, tall = BrowHeight * wide;
				result.Features.Add( (brow, new Vec3( lid.X, centre, lid.Z ), bottom) );
				result.Features.Add( (brow + ".top", new Vec3( lid.X, centre, lid.Z + tall ), top) );
				result.Features.Add( (brow + ".in", new Vec3( lid.X, centre + inward * 0.5f * length, lid.Z + 0.5f * tall ), inner) );
				result.Features.Add( (brow + ".out", new Vec3( lid.X, centre - inward * 0.5f * length, lid.Z + 0.5f * tall ), outer) );
			}
		}

		// The front of the chest on each side: a bust sits higher or lower on one body than on
		// another, and rays from the spine only move skin towards or away from it. Cloth made to
		// bulge over the citizen's chest would bulge where the chest isn't.
		// Only a chest with a bust has a front to go by. On a flat one the foremost point is
		// anywhere across it and jumps between two bodies of the same shape.
		var fronts = new List<(Vec3 Ours, Vec3 Theirs)>();
		float bust = 0;
		var tips = new List<Vec3>();
		for ( int side = -1; side <= 1; side += 2 )
		{
			stockMesh ??= new TriMesh( stock.Positions, stock.Indices );
			reposedMesh ??= new TriMesh( result.Positions, body.Indices );
			if ( !ChestFront( stock, stock.Positions, stockMesh, side, out var ours, out float stands ) ) continue;
			if ( !ChestFront( stock, result.Positions, reposedMesh, side, out var theirs, out float theirBust ) ) continue;
			bust = MathF.Max( bust, stands );
			fronts.Add( (ours, theirs) );
			if ( theirBust >= MinBust ) tips.Add( theirs );
		}
		if ( bust >= MinBust )
		{
			foreach ( var (ours, theirs) in fronts )
			{
				Vec3 shift = theirs - ours;
				if ( shift.Length() < 0.15f * Units.Metre )
					result.Landmarks.Add( (ours, shift, BustReach) );
			}
		}
		else if ( tips.Count == 2 )
		{
			// A flat chest with a bust to go to: a man's chest still has its front, the tip of
			// each pectoral over the fold under it, and clothing for him is cut to it (a bra made
			// for the human male has its cups there). That goes onto the bust. Without it a bra for
			// the man sat on Aurora with its cups 13 mm over her breasts and its band 36 mm over
			// the fold under them, her lower breasts bare. A chest with no fold anywhere (the
			// citizen's) has nothing to go by.
			if ( Pectoral( stock, stockMesh, out var pec, out float centre ) )
				foreach ( var tip in tips )
				{
					// The pectoral on the tip's side, mirrored across the middle.
					var mine = tip.Y >= centre ? pec : new Vec3( pec.X, 2 * centre - pec.Y, pec.Z );
					Vec3 shift = tip - mine;
					if ( shift.Length() < 0.15f * Units.Metre )
						result.Landmarks.Add( (mine, shift, BustReach) );
				}
		}

		// Last: everything above goes by the skin as it is.
		if ( tips.Count > 0 ) Drape( result.Positions, body, to, stock, tips );
		result.bodyRest = body.Positions;
		result.bodyIndices = body.Indices;

		// The neck and the trunk go by the skin as draped too: the skin the clothes are fitted
		// to, and the skin under them that they go back to the rest pose by.
		result.skinMesh = new TriMesh( result.Positions, body.Indices );
		result.skinBones = new int[body.BoneIndex.Length];
		result.skinWeights = new float[body.BoneWeight.Length];
		for ( int k = 0; k < body.BoneIndex.Length; k++ )
		{
			int bone = body.BoneIndex[k];
			int s = bone >= 0 && bone < bones ? to[bone] : -1;
			bool eye = s >= 0 && stock.BoneNames[s].StartsWith( "eye_" );
			result.skinBones[k] = eye ? -1 : s;
			result.skinWeights[k] = eye || s < 0 ? 0 : body.BoneWeight[k];
		}
		stockMesh ??= new TriMesh( stock.Positions, stock.Indices );
		if ( !float.IsNaN( napeHeight ) && result.Face.Count == 4 )
			result.Neck = Neck.Find( result, stock, stockMesh, result.Face[3].Body, skullStock, skullRest, false, result.Face[3].Stock.Z, napeHeight );
		else if ( chinAtRest is Vec3 chin )
			result.Neck = Neck.Find( result, stock, stockMesh, chin, skullStock, skullRest, true );
		result.Trunk = Trunk.Find( result, stock, result.skinMesh );

		return result;
	}

	/// <summary>
	/// Cloth over a bust hangs from its tips: straight down to the ribs and straight across
	/// between the two. It never goes into the fold under a breast or between them. Cloth
	/// fitted to the skin does, wraps each breast on its own and makes the bust look blown up.
	/// So the body the clothes are fitted to has those hollows filled: the front of the torso
	/// is held no further back than a slope down and across from the skin around each tip.
	/// The skin itself is not changed, only what the clothes are fitted to.
	/// </summary>
	static void Drape( Vec3[] positions, SkinnedGeometry body, int[] toStock, SkinnedGeometry stock, List<Vec3> tips )
	{
		var torso = new bool[stock.BoneNames.Length];
		for ( int b = 0; b < torso.Length; b++ )
			torso[b] = stock.BoneNames[b] == "pelvis" || stock.BoneNames[b].StartsWith( "spine_" );
		int middle = Array.IndexOf( stock.BoneNames, "spine_1" );
		if ( middle < 0 ) return;
		Vec3 spine = stock.BonePositions[middle];
		float centre = spine.Y;
		float across = 0;
		foreach ( var tip in tips ) across = MathF.Max( across, MathF.Abs( tip.Y - centre ) );

		int n = positions.Length;

		// The torso's skin, and of it what is around the tips: what the rest hangs from.
		var onTorso = new bool[n];
		var held = new List<Vec3>();
		for ( int v = 0; v < n; v++ )
		{
			float share = 0;
			for ( int j = 0; j < 4; j++ )
			{
				int bone = body.BoneIndex[v * 4 + j];
				if ( bone >= 0 && bone < toStock.Length && toStock[bone] >= 0 && torso[toStock[bone]] ) share += body.BoneWeight[v * 4 + j];
			}
			onTorso[v] = share >= 0.9f;
			if ( !onTorso[v] ) continue;
			foreach ( var tip in tips )
				if ( (positions[v] - tip).Length() < DrapeFrom ) { held.Add( positions[v] ); break; }
		}
		if ( held.Count == 0 ) return;

		for ( int v = 0; v < n; v++ )
		{
			if ( !onTorso[v] ) continue;
			// Only the front half of the chest, and no further out to the side than the tips:
			// past them cloth falls to the sides of the bust, not across it. The underside of
			// a breast faces down, but it is part of the fold too.
			if ( positions[v].X <= spine.X ) continue;
			float amount = Math.Clamp( (across + DrapeSide - MathF.Abs( positions[v].Y - centre )) / DrapeSide, 0f, 1f );
			if ( amount <= 0 ) continue;

			var p = positions[v];
			float hung = float.MinValue;
			foreach ( var h in held )
				hung = MathF.Max( hung, h.X - DrapeAcross * MathF.Abs( p.Y - h.Y ) - DrapeDown * MathF.Abs( p.Z - h.Z ) );
			if ( hung > p.X ) positions[v] = new Vec3( p.X + (hung - p.X) * amount, p.Y, p.Z );
		}
	}

	const float DrapeFrom = 0.04f * Units.Metre;   // skin this close to the tip of a bust is what cloth hangs from
	const float DrapeDown = 0.6f;                  // how steeply cloth drops off it, per unit up or down
	const float DrapeAcross = 0.25f;               // and per unit across
	const float DrapeSide = 0.03f * Units.Metre;   // how far past the tips, to the side, the hanging fades out

	const float EyeWeight = 0.2f;              // a vertex this much on the eye bone is part of the eye
	const float EyeFrontDepth = 0.003f * Units.Metre;   // the front of an eye: this close to its foremost point

	/// <summary>
	/// The middle of the front of an eye: the vertices the eye bone drives that are furthest
	/// forward (+X, the way the stock body faces), in the given positions.
	/// </summary>
	/// <summary>
	/// The tip of the nose, the middle of the mouth and the bottom of the chin, in positions in
	/// the stock pose (forward is +x). The mouth is the front of what is drawn with a mouth
	/// material (the inside of the mouth, the teeth): a face paints its lips, so they can't be
	/// told from the skin. The nose and the chin are read off the face's profile down its
	/// middle: the nose stands out most below the eyes, and the chin is where the profile,
	/// going down, drops away to the neck.
	/// </summary>
	static bool LowerFace( SkinnedGeometry geo, Vec3[] positions, Vec3 eye, out Vec3[] marks )
	{
		marks = null;
		var mouth = new HashSet<int>();
		foreach ( var (first, count, material) in geo.Draws )
			if ( material != null && material.Contains( "mouth", StringComparison.OrdinalIgnoreCase ) )
				for ( int k = first; k < first + count; k++ ) mouth.Add( geo.Indices[k] );
		if ( mouth.Count == 0 ) return false;
		float most = float.MinValue;
		foreach ( int v in mouth ) most = MathF.Max( most, positions[v].X );
		var mouthFront = Vec3.Zero;
		int inFront = 0;
		foreach ( int v in mouth )
			if ( positions[v].X >= most - MouthFrontDepth ) { mouthFront += positions[v]; inFront++; }
		mouthFront /= inFront;

		// The profile: the foremost point in each slice of height, down the middle of the face.
		int slices = (int)(ProfileDepth / ProfileSlice);
		var front = new Vec3[slices];
		var found = new bool[slices];
		foreach ( var p in positions )
		{
			if ( MathF.Abs( p.Y - eye.Y ) > ProfileHalfWidth || p.Z >= eye.Z || p.Z <= eye.Z - ProfileDepth ) continue;
			int k = Math.Min( slices - 1, (int)((eye.Z - p.Z) / ProfileSlice) );
			if ( !found[k] || p.X > front[k].X ) { front[k] = p; found[k] = true; }
		}
		int nose = -1;
		for ( int k = 0; k < slices && (k + 1) * ProfileSlice <= NoseDepth; k++ )
			if ( found[k] && (nose < 0 || front[k].X > front[nose].X) ) nose = k;
		if ( nose < 0 ) return false;
		int chin = nose, gap = 0;
		TriMesh mesh = null;
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int k = nose + 1; k < slices && gap <= 2; k++ )
		{
			if ( !found[k] || front[k].X < front[nose].X - ChinBack )
			{
				// A slice down the middle of a face can have no vertex on the front at all, only
				// one on the back of the head: Akira's chin, rebuilt finer, ended at such a slice
				// 28 mm above its bottom and a beard laid on by it stopped short of her chin. A
				// ray from the front tells the face from the back of the head.
				mesh ??= new TriMesh( positions, geo.Indices );
				var start = new Vec3( eye.X + Units.Metre, eye.Y, eye.Z - (k + 0.5f) * ProfileSlice );
				mesh.Crossings( start, new Vec3( -1, 0, 0 ), 2 * Units.Metre, hits );
				if ( hits.Count > 0 && start.X - hits[0].T >= front[nose].X - ChinBack )
				{
					front[k] = start - new Vec3( hits[0].T, 0, 0 );
					found[k] = true;
				}
			}
			if ( !found[k] ) { gap++; continue; }
			if ( front[k].X < front[nose].X - ChinBack ) break;
			chin = k;
			gap = 0;
		}
		// A chin has to be below the mouth, or the profile ran off somewhere else.
		if ( front[chin].Z >= mouthFront.Z ) return false;
		marks = new[] { front[nose], mouthFront, front[chin] };
		return true;
	}

	/// <summary>
	/// The bottom of the chin, as <see cref="LowerFace"/> finds it, from a profile read with rays
	/// from the front: a sparse mesh has no vertex in most slices of it, and the foremost one left
	/// in a slice can be on the back of the head.
	/// </summary>
	static bool Chin( TriMesh mesh, Vec3 eye, out Vec3 chin )
	{
		chin = Vec3.Zero;
		int slices = (int)(ProfileDepth / ProfileSlice);
		var front = new Vec3[slices];
		var found = new bool[slices];
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int k = 0; k < slices; k++ )
		{
			var start = new Vec3( eye.X + Units.Metre, eye.Y, eye.Z - (k + 0.5f) * ProfileSlice );
			mesh.Crossings( start, new Vec3( -1, 0, 0 ), 2 * Units.Metre, hits );
			if ( hits.Count == 0 ) continue;
			front[k] = start - new Vec3( hits[0].T, 0, 0 );
			found[k] = true;
		}
		int nose = -1;
		for ( int k = 0; k < slices && (k + 1) * ProfileSlice <= NoseDepth; k++ )
			if ( found[k] && (nose < 0 || front[k].X > front[nose].X) ) nose = k;
		if ( nose < 0 ) return false;
		int last = nose;
		for ( int k = nose + 1; k < slices; k++ )
		{
			if ( !found[k] ) continue;
			if ( front[k].X < front[nose].X - ChinBack )
			{
				chin = front[last];
				return last > nose;
			}
			last = k;
		}
		return false;
	}

	/// <summary>
	/// Where the head begins behind: up the middle of the back of the neck from its foremost
	/// point (the nape), the first height where the profile has gone <see cref="SkullBack"/> back
	/// from it, out over the skull. A stand-up collar ends under it. The neck under the nape may
	/// go straight down a long way (Aurora's does), so only the skull has to turn; with
	/// <paramref name="strict"/>, as for the stock body, the nape has to be one <see cref="Nape"/>
	/// finds, which a head and neck in one gentle curve (the citizen's) has not.
	/// </summary>
	static bool SkullStart( TriMesh mesh, Vec3 eye, float bottom, bool strict, out float height )
	{
		height = float.NaN;
		if ( strict && !Nape( mesh, eye, bottom, out _ ) ) return false;
		int slices = (int)((eye.Z - bottom) / ProfileSlice);
		if ( slices < 3 ) return false;
		var back = new float[slices];
		var found = new bool[slices];
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int k = 0; k < slices; k++ )
		{
			var start = new Vec3( eye.X - Units.Metre, eye.Y, bottom + (k + 0.5f) * ProfileSlice );
			mesh.Crossings( start, new Vec3( 1, 0, 0 ), 2 * Units.Metre, hits );
			if ( hits.Count == 0 ) continue;
			back[k] = start.X + hits[0].T;
			found[k] = true;
		}
		int nape = -1;
		for ( int k = 0; k < slices; k++ )
			if ( found[k] && (nape < 0 || back[k] > back[nape]) ) nape = k;
		if ( nape < 0 ) return false;
		for ( int k = nape + 1; k < slices; k++ )
			if ( found[k] && back[k] <= back[nape] - SkullBack ) { height = bottom + (k + 0.5f) * ProfileSlice; return true; }
		return false;
	}

	/// <summary>
	/// The nape, on a body in the stock pose: the foremost point of the back's profile down the
	/// middle, between the bottom of the neck and the eyes. Below it the neck slopes back to the
	/// shoulders, above it the skull bulges out. Averaged over the stretch of the profile within
	/// a few millimetres of it: the back of a neck can be straight for a while. The profile is
	/// found with rays from behind, a sparse mesh has no vertex on most of that line.
	/// </summary>
	static bool Nape( TriMesh mesh, Vec3 eye, float bottom, out Vec3 nape )
	{
		nape = Vec3.Zero;
		int slices = (int)((eye.Z - bottom) / ProfileSlice);
		if ( slices < 3 ) return false;
		var back = new Vec3[slices];
		var found = new bool[slices];
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int k = 0; k < slices; k++ )
		{
			var start = new Vec3( eye.X - Units.Metre, eye.Y, eye.Z - (k + 0.5f) * ProfileSlice );
			mesh.Crossings( start, new Vec3( 1, 0, 0 ), 2 * Units.Metre, hits );
			if ( hits.Count == 0 ) continue;
			back[k] = start + new Vec3( hits[0].T, 0, 0 );
			found[k] = true;
		}
		int most = -1;
		for ( int k = 0; k < slices; k++ )
			if ( found[k] && (most < 0 || back[k].X > back[most].X) ) most = k;
		if ( most < 0 ) return false;

		// The profile has to turn there: go back into the skull above and down the neck below.
		// A back of the head and neck in one gentle curve (the citizen's) has no nape to go by,
		// its foremost point is anywhere along it.
		int span = (int)(NapeSpan / ProfileSlice);
		bool turnsAbove = false, turnsBelow = false;
		for ( int k = Math.Max( 0, most - span ); k < most; k++ )
			turnsAbove |= found[k] && back[k].X <= back[most].X - NapeTurn;
		for ( int k = most + 1; k < Math.Min( slices, most + span + 1 ); k++ )
			turnsBelow |= found[k] && back[k].X <= back[most].X - NapeTurn;
		if ( !turnsAbove || !turnsBelow ) return false;

		var sum = Vec3.Zero;
		int count = 0;
		for ( int step = -1; step <= 1; step += 2 )
		{
			for ( int k = step < 0 ? most : most + 1; k >= 0 && k < slices; k += step )
			{
				if ( !found[k] ) continue;
				if ( back[k].X < back[most].X - NapeDepth ) break;
				sum += back[k];
				count++;
			}
		}
		nape = sum / count;
		return true;
	}

	static bool EyeFront( SkinnedGeometry geo, Vec3[] positions, int eyeBone, out Vec3 front )
	{
		front = Vec3.Zero;
		float most = float.MinValue;
		for ( int pass = 0; pass < 2; pass++ )
		{
			int count = 0;
			var sum = Vec3.Zero;
			for ( int v = 0; v < positions.Length; v++ )
			{
				bool onEye = false;
				for ( int j = 0; j < 4 && !onEye; j++ )
					onEye = geo.BoneIndex[v * 4 + j] == eyeBone && geo.BoneWeight[v * 4 + j] >= EyeWeight;
				if ( !onEye ) continue;
				if ( pass == 0 ) most = MathF.Max( most, positions[v].X );
				else if ( positions[v].X >= most - EyeFrontDepth ) { sum += positions[v]; count++; }
			}
			if ( pass == 1 )
			{
				if ( count == 0 ) return false;
				front = sum / count;
			}
			else if ( most == float.MinValue ) return false;
		}
		return true;
	}

	/// <summary>
	/// The foremost point of the chest on one side (+1 left, -1 right), in positions that are in
	/// the stock pose: between the middle spine bone and the collarbones in height, between the
	/// middle and the shoulder joint across. Averaged over what is within a few millimetres of it.
	/// </summary>
	/// <param name="bust">How far the front stands out over the fold under it.</param>
	static bool ChestFront( SkinnedGeometry stock, Vec3[] positions, TriMesh mesh, int side, out Vec3 front, out float bust )
	{
		front = Vec3.Zero;
		bust = 0;
		int low = Array.IndexOf( stock.BoneNames, "spine_1" ), high = Array.IndexOf( stock.BoneNames, side > 0 ? "clavicle_L" : "clavicle_R" );
		int shoulder = Array.IndexOf( stock.BoneNames, side > 0 ? "arm_upper_L" : "arm_upper_R" );
		if ( low < 0 || high < 0 || shoulder < 0 ) return false;
		float z0 = stock.BonePositions[low].Z, z1 = stock.BonePositions[high].Z;
		float centre = stock.BonePositions[low].Y, reach = MathF.Abs( stock.BonePositions[shoulder].Y - centre ) * 0.8f;
		float most = float.MinValue;
		foreach ( var p in positions )
		{
			float across = (p.Y - centre) * side;
			if ( p.Z < z0 || p.Z > z1 || across < 0.15f * reach || across > reach ) continue;
			most = MathF.Max( most, p.X );
		}
		if ( most == float.MinValue ) return false;
		int count = 0;
		foreach ( var p in positions )
		{
			float across = (p.Y - centre) * side;
			if ( p.Z < z0 || p.Z > z1 || across < 0.15f * reach || across > reach || p.X < most - 0.004f * Units.Metre ) continue;
			front += p;
			count++;
		}
		front /= count;

		// How far the front hangs over the skin straight under it: the deepest the chest goes in
		// below it, a bust-depth down at most. A belly further down doesn't count. The skin is
		// found with rays from the front, a sparse mesh has no vertex on most of that line.
		const float step = 0.005f * Units.Metre;
		var hits = new List<(float T, bool Leaving, int Tri)>();
		float deepest = float.MaxValue;
		for ( float down = step; down <= BustDepth; down += step )
		{
			var start = new Vec3( front.X + Units.Metre, front.Y, front.Z - down );
			mesh.Crossings( start, new Vec3( -1, 0, 0 ), 2 * Units.Metre, hits );
			if ( hits.Count > 0 ) deepest = MathF.Min( deepest, start.X - hits[0].T );
		}
		bust = deepest == float.MaxValue ? 0 : front.X - deepest;

		// A bust stands out to the side of the breastbone. A foremost point next to the middle
		// is the breastbone itself, on a barrel chest or a man's pecs.
		if ( (front.Y - centre) * side < MinTipAcross * reach ) bust = 0;
		return true;
	}

	const float MinPectoral = 0.004f * Units.Metre;   // a chest front standing out this much over the fold under it is a pectoral
	const float PectoralAcross = 0.75f;               // and no further out to the side than this share of the way to the shoulder

	/// <summary>
	/// The tip of a pectoral on a flat chest: of the front of the chest, read off with rays, the
	/// foremost point that stands out over a fold just under it, a fold the front comes out of
	/// again below (a belly standing out further lower down doesn't hide it). Out at the side of
	/// the chest the front only falls away round to the arm; that is no fold and no tip. Read off
	/// both sides at once, the front averaged across the middle: on the human male the fold under
	/// the right pectoral is a millimetre shallower than the left one's, too shallow on its own.
	/// </summary>
	/// <param name="tip">The left pectoral's tip; the right one is its mirror image across <paramref name="centre"/>.</param>
	static bool Pectoral( SkinnedGeometry stock, TriMesh mesh, out Vec3 tip, out float centre )
	{
		tip = Vec3.Zero;
		centre = 0;
		int low = Array.IndexOf( stock.BoneNames, "spine_1" ), high = Array.IndexOf( stock.BoneNames, "clavicle_L" );
		int shoulder = Array.IndexOf( stock.BoneNames, "arm_upper_L" );
		if ( low < 0 || high < 0 || shoulder < 0 || mesh == null ) return false;
		float z0 = stock.BonePositions[low].Z, z1 = stock.BonePositions[high].Z;
		centre = stock.BonePositions[low].Y;
		float reach = MathF.Abs( stock.BonePositions[shoulder].Y - centre ) * 0.8f;
		const float step = 0.005f * Units.Metre;
		int columns = (int)MathF.Ceiling( reach * (PectoralAcross - MinTipAcross) / step ) + 1;
		int rows = (int)MathF.Ceiling( (z1 - z0) / step ) + 1;
		var front = new float[columns, rows];
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int c = 0; c < columns; c++ )
			for ( int r = 0; r < rows; r++ )
			{
				float sum = 0;
				int count = 0;
				for ( int way = -1; way <= 1; way += 2 )
				{
					var start = new Vec3( Units.Metre, centre + way * (MinTipAcross * reach + c * step), z0 + r * step );
					mesh.Crossings( start, new Vec3( -1, 0, 0 ), 2 * Units.Metre, hits );
					if ( hits.Count > 0 ) { sum += start.X - hits[0].T; count++; }
				}
				front[c, r] = count == 2 ? sum / 2 : float.NaN;
			}

		float most = float.MinValue;
		bool found = false;
		int depth = (int)(BustDepth / step);
		for ( int c = 0; c < columns; c++ )
			for ( int r = 1; r < rows; r++ )
			{
				float x = front[c, r];
				if ( float.IsNaN( x ) ) continue;
				// Down to the fold: the lowest the front goes before it comes out past this point
				// again, and how far it comes out again under the fold.
				float fold = x, after = float.MinValue;
				for ( int d = r - 1; d >= 0 && r - d <= depth; d-- )
				{
					if ( float.IsNaN( front[c, d] ) || front[c, d] > x ) break;
					if ( front[c, d] < fold ) { fold = front[c, d]; after = fold; }
					else after = MathF.Max( after, front[c, d] );
				}
				if ( x - fold < MinPectoral || after - fold < MinPectoral || x <= most ) continue;
				most = x;
				tip = new Vec3( x, centre + MinTipAcross * reach + c * step, z0 + r * step );
				found = true;
			}
		return found;
	}

	static bool HasChildren( SkinnedGeometry geo, int bone )
	{
		for ( int i = 0; i < geo.BoneParents.Length; i++ )
			if ( geo.BoneParents[i] == bone ) return true;
		return false;
	}

	/// <summary>
	/// How thick the body is around a bone with no child to measure it by: the bone runs along
	/// itself up to where it leaves the skin, and rays straight out from it at a quarter, half
	/// and three quarters of that, twelve around at each, leave the skin at a median distance.
	/// Geometry only, so two bodies with different skin weights are measured the same way.
	/// 0 if too few rays got out.
	/// </summary>
	static float Thickness( SkinnedGeometry geo, TriMesh mesh, int bone, out float length )
	{
		length = 0;
		Vec3 start = geo.BonePositions[bone];
		Vec3 axis = Quat.Rotate( geo.BoneRotations[bone], new Vec3( 1, 0, 0 ) );
		var hits = new List<(float T, bool Leaving, int Tri)>();
		mesh.Crossings( start, axis, Units.Metre, hits );
		int leave = hits.FindIndex( h => h.Leaving );
		if ( leave < 0 || hits[leave].T < 0.02f * Units.Metre ) return 0;
		length = hits[leave].T;

		Vec3 side = MathF.Abs( axis.Z ) < 0.9f ? Vec3.Cross( axis, new Vec3( 0, 0, 1 ) ) : Vec3.Cross( axis, new Vec3( 1, 0, 0 ) );
		side /= side.Length();
		Vec3 other = Vec3.Cross( axis, side );
		var found = new List<float>();
		foreach ( float t in new[] { 0.25f, 0.5f, 0.75f } )
		{
			Vec3 at = start + axis * (length * t);
			for ( int k = 0; k < 12; k++ )
			{
				float a = k * MathF.PI / 6f;
				mesh.Crossings( at, side * MathF.Cos( a ) + other * MathF.Sin( a ), Units.Metre, hits );
				leave = hits.FindIndex( h => h.Leaving );
				if ( leave >= 0 ) found.Add( hits[leave].T );
			}
		}

		if ( found.Count < 12 ) return 0;
		found.Sort();
		return found[found.Count / 2];
	}

	/// <summary>
	/// How many times bigger the body is around one garment vertex, going by the bones it is
	/// skinned to. What <see cref="FromStockProportions"/> scales positions by, for things that
	/// are displacements rather than positions.
	/// </summary>
	public float ScaleAt( int vertex, int[] boneIndex, float[] boneWeight )
	{
		float sum = 0, weight = 0;
		for ( int j = 0; j < 4; j++ )
		{
			int bone = boneIndex[vertex * 4 + j];
			float w = boneWeight[vertex * 4 + j];
			if ( bone < 0 || w <= 0 ) continue;
			sum += BoneScale[bone] * w;
			weight += w;
		}

		return weight > 1e-6f ? sum / weight : 1f;
	}

	/// <summary>
	/// Takes a garment from the stock bind pose into the body's rest pose, bone by bone, the
	/// way bone merging will. With the garment built in this pose and with <see cref="RestPositions"/>
	/// as its bones, the body's animation moves it exactly as it moves the body.
	/// </summary>
	/// <param name="boneIndex">Four bone slots per garment vertex, as stock bone indices.</param>
	/// <param name="tris">The garment's triangles, with <paramref name="cloth"/> the vertices fitted as cloth: cloth hanging round both legs hangs from the hips the way it hung in the stock pose (<see cref="Hang"/>). Null for none.</param>
	public Vec3[] ToBodyRest( Vec3[] fitted, int[] boneIndex, float[] boneWeight, int[] tris = null, bool[] cloth = null )
	{
		var result = new Vec3[fitted.Length];
		for ( int v = 0; v < fitted.Length; v++ ) result[v] = RestByBones( fitted[v], boneIndex, boneWeight, v, false );
		if ( tris != null && cloth != null ) Hang( fitted, tris, boneIndex, boneWeight, cloth, result );
		return result;
	}

	// One vertex taken back to the rest pose by its bones. Hung, a bone of a leg turns the way the
	// hips do about its hip joint instead of the way it turns itself.
	Vec3 RestByBones( Vec3 p, int[] boneIndex, float[] boneWeight, int v, bool hung )
	{
		var sum = Vec3.Zero;
		float weight = 0;
		for ( int j = 0; j < 4; j++ )
		{
			int bone = boneIndex[v * 4 + j];
			float w = boneWeight[v * 4 + j];
			if ( bone < 0 || w <= 0 ) continue;
			int by = follow[bone];
			if ( hung && hipOf[by] >= 0 )
			{
				int hip = hipOf[by];
				sum += (Quat.Rotate( carry[stockParents[hip]], p - stockPositions[hip] ) + RestPositions[hip]) * w;
			}
			else
				sum += (Quat.Rotate( carry[by], p - stockPositions[by] ) + RestPositions[by]) * w;
			weight += w;
		}
		return weight > 1e-6f ? sum / weight : p;
	}

	const float HangBand = 0.02f * Units.Metre;    // how high a band of the legs is looked at in one go
	const float HangClear = 0.002f * Units.Metre;  // hung cloth keeps this far off the legs, else it goes their way as far as it has to
	const int HangSteps = 6;                       // halvings in finding how far that is
	const int HangSlant = 4;                       // bands of a leg's hem below where it last goes between the legs that are still that leg

	/// <summary>
	/// Cloth that hangs round both legs below the crotch rather than round each leg on its own (a
	/// skirt, a dress, the tails of a coat, an apron) hangs from the hips. A body whose legs are set
	/// wider apart in its rest pose than the stock body's (the humans stand with their feet 8 cm
	/// further apart) has them turned out at the hip by a few degrees, and cloth that goes back with
	/// the thigh under it turned out with it: a straight skirt flared out into an A, its hem 3 cm out
	/// on each side, and a coat's tails stood off the legs. A trouser leg does go with its leg. The
	/// two are told apart by whether the cloth goes between the legs: looking from each leg towards
	/// the other, a trouser leg's inside is in the way, a skirt's isn't. Where the leg turned out
	/// would come through the hanging cloth (a long dress, down at the ankles), the cloth goes the
	/// leg's way as far as it has to, the whole band of it at that height. Only cloth that comes
	/// down from above the crotch hangs: a shoe hung from the hips was thrown 10 cm off the foot.
	/// </summary>
	/// <param name="fitted">The garment in the stock pose.</param>
	/// <param name="rest">The garment taken back to the rest pose by its bones. Changed in place.</param>
	void Hang( Vec3[] fitted, int[] tris, int[] boneIndex, float[] boneWeight, bool[] cloth, Vec3[] rest )
	{
		if ( Trunk == null || bodyRest == null || hips[0] < 0 || hips[1] < 0 || knees[0] < 0 || knees[1] < 0 || ankles[0] < 0 || ankles[1] < 0 ) return;
		float crotch = Trunk.Crotch;
		var clothTris = new List<int>();
		for ( int t = 0; t + 2 < tris.Length; t += 3 )
			if ( cloth[tris[t]] && cloth[tris[t + 1]] && cloth[tris[t + 2]] && (fitted[tris[t]].Z < crotch || fitted[tris[t + 1]].Z < crotch || fitted[tris[t + 2]].Z < crotch) )
			{
				clothTris.Add( tris[t] ); clothTris.Add( tris[t + 1] ); clothTris.Add( tris[t + 2] );
			}
		if ( clothTris.Count == 0 ) return;
		var mesh = new TriMesh( fitted, clothTris.ToArray() );

		// The middle of each leg at a height: down the thigh, then down the shin.
		Vec3 Axis( int side, float z )
		{
			Vec3 hip = stockPositions[hips[side]], knee = stockPositions[knees[side]], ankle = stockPositions[ankles[side]];
			if ( z >= knee.Z ) return hip + (knee - hip) * Math.Clamp( (hip.Z - z) / MathF.Max( hip.Z - knee.Z, 1e-4f ), 0f, 1f );
			return knee + (ankle - knee) * Math.Clamp( (knee.Z - z) / MathF.Max( knee.Z - ankle.Z, 1e-4f ), 0f, 1f );
		}

		// Per side and band down from the crotch, whether cloth goes between the legs there.
		float bottom = MathF.Min( stockPositions[ankles[0]].Z, stockPositions[ankles[1]].Z );
		int bands = Math.Max( 1, (int)MathF.Ceiling( (crotch - bottom) / HangBand ) );
		var between = new bool[2, bands];
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int side = 0; side < 2; side++ )
			for ( int k = 0; k < bands; k++ )
			{
				float z = crotch - (k + 0.5f) * HangBand;
				Vec3 from = Axis( side, z ), to = Axis( 1 - side, z );
				var way = to - from;
				float length = way.Length();
				if ( length < 1e-4f * Units.Metre ) continue;
				mesh.Crossings( from, way / length, 0.5f * length, hits );
				between[side, k] = hits.Count > 0;
			}

		// The crotch of baggy trousers hangs lower than the body's, and there is nothing between
		// the legs above it. Cloth that goes between them anywhere lower down wraps the leg all
		// the way up. And the hem of a leg can be cut on a slant, longer outside than inside.
		for ( int side = 0; side < 2; side++ )
		{
			for ( int k = bands - 2; k >= 0; k-- ) between[side, k] |= between[side, k + 1];
			int last = -1;
			for ( int k = 0; k < bands; k++ ) if ( between[side, k] ) last = k;
			for ( int k = last + 1; k < bands && k <= last + HangSlant; k++ ) if ( last >= 0 ) between[side, k] = true;
		}

		// A leg wrapped anywhere is a trouser leg all the way: right under the crotch a baggy
		// seat hangs too low between the legs for the rays to meet it, and the hem of a pair of
		// shorts below the last ray that met their inside is still the shorts.
		for ( int side = 0; side < 2; side++ )
		{
			bool wrapped = false;
			for ( int k = 0; k < bands; k++ ) wrapped |= between[side, k];
			for ( int k = 0; k < bands; k++ ) between[side, k] = wrapped;
		}

		// Only cloth that comes down from above the crotch hangs from the hips: not a shoe, a sock
		// or anything else that is all on the legs.
		var weld = MeshTools.Weld( fitted, out int welded );
		var piece = GarmentFit.Islands( welded, MeshTools.Edges( tris, weld ) );
		var fromAbove = new bool[welded];
		for ( int v = 0; v < fitted.Length; v++ )
			if ( cloth[v] && fitted[v].Z >= crotch ) fromAbove[piece[weld[v]]] = true;

		// The hanging vertices, each with the leg it is mostly on and its band.
		var hanging = new List<(int Vertex, int Side, int Band)>();
		for ( int v = 0; v < fitted.Length; v++ )
		{
			if ( !cloth[v] || fitted[v].Z >= crotch || !fromAbove[piece[weld[v]]] ) continue;
			int k = Math.Clamp( (int)((crotch - fitted[v].Z) / HangBand), 0, bands - 1 );
			bool wrapped = false;
			int side = -1;
			float most = 0;
			for ( int j = 0; j < 4; j++ )
			{
				int bone = boneIndex[v * 4 + j];
				float w = boneWeight[v * 4 + j];
				if ( bone < 0 || w <= 0 || hipOf[follow[bone]] < 0 ) continue;
				int s = hipOf[follow[bone]] == hips[0] ? 0 : 1;
				wrapped |= between[s, k];
				if ( w > most ) { most = w; side = s; }
			}
			if ( side >= 0 && !wrapped ) hanging.Add( (v, side, k) );
		}
		if ( hanging.Count == 0 ) return;

		// How far each band can hang before the leg comes through it.
		TriMesh body;
		lock ( this ) body = restBody ??= new TriMesh( bodyRest, bodyIndices );
		var hung = new Vec3[hanging.Count];
		var share = new float[2, bands];
		for ( int side = 0; side < 2; side++ )
			for ( int k = 0; k < bands; k++ ) share[side, k] = 1;
		for ( int h = 0; h < hanging.Count; h++ )
		{
			var (v, side, k) = hanging[h];
			hung[h] = RestByBones( fitted[v], boneIndex, boneWeight, v, true );
			if ( body.SignedGap( hung[h], out _ ) >= HangClear ) continue;
			float lo = 0, hi = 1;
			for ( int step = 0; step < HangSteps; step++ )
			{
				float mid = 0.5f * (lo + hi);
				if ( body.SignedGap( rest[v] + (hung[h] - rest[v]) * mid, out _ ) >= HangClear ) lo = mid;
				else hi = mid;
			}
			share[side, k] = MathF.Min( share[side, k], lo );
		}

		// A band hangs no further than the bands next to it, so the cloth turns out smoothly.
		for ( int side = 0; side < 2; side++ )
		{
			var smooth = new float[bands];
			for ( int k = 0; k < bands; k++ )
			{
				float a = share[side, Math.Max( k - 1, 0 )], b = share[side, Math.Min( k + 1, bands - 1 )];
				smooth[k] = MathF.Min( share[side, k], 0.25f * a + 0.5f * share[side, k] + 0.25f * b );
			}
			for ( int k = 0; k < bands; k++ ) share[side, k] = smooth[k];
		}

		for ( int h = 0; h < hanging.Count; h++ )
		{
			var (v, side, k) = hanging[h];
			rest[v] += (hung[h] - rest[v]) * share[side, k];
		}
	}

	/// <summary>
	/// Paint on the skin (<see cref="GarmentFit.Fit"/>), taken back to the body's rest pose
	/// (<see cref="ToBodyRest"/>), goes onto the same spot of the same triangle of the body in
	/// its rest pose, as far off it as it was in the stock pose (grown or shrunk with the
	/// triangle). Taken back bone by bone it is off the skin by however much the bones' moves
	/// differ across a triangle: on a short neck, sized very differently from the stock body's
	/// next to the chest and the collarbones, the edge of a tattoo came back 6 mm off the skin.
	/// Only what lies within a few millimetres of the skin, faded over into the bone-by-bone
	/// place further off (the tips of a brow's hairs). Over a bust it goes onto the skin itself,
	/// not the skin as Drape hangs cloth over it.
	/// </summary>
	/// <param name="stockFit">The garment fitted, in the stock pose and proportions.</param>
	/// <param name="rest">The same garment taken back to the rest pose by its bones. Changed in place, and returned.</param>
	/// <param name="paint">Per vertex, whether it is paint (<see cref="GarmentFit.Fit"/>'s paintOut). Null for none.</param>
	public Vec3[] OntoSkin( Vec3[] stockFit, Vec3[] rest, bool[] paint )
	{
		if ( bodyRest == null || paint == null || Array.IndexOf( paint, true ) < 0 ) return rest;
		TriMesh skin;
		lock ( this ) skin = posedSkin ??= new TriMesh( Positions, bodyIndices ).Skin();
		for ( int v = 0; v < stockFit.Length; v++ )
		{
			if ( !paint[v] ) continue;
			float d = skin.Nearest( stockFit[v], out var q, out int tri );
			if ( tri < 0 || d >= OntoFar ) continue;
			int a = skin.Tris[tri * 3], b = skin.Tris[tri * 3 + 1], c = skin.Tris[tri * 3 + 2];
			var bary = Barycentric( q, Positions[a], Positions[b], Positions[c] );
			var n0 = skin.TriNormals[tri];
			var raw = Vec3.Cross( Positions[b] - Positions[a], Positions[c] - Positions[a] );
			float flip = Vec3.Dot( raw, n0 ) >= 0 ? 1f : -1f;
			var n1 = Vec3.Cross( bodyRest[b] - bodyRest[a], bodyRest[c] - bodyRest[a] ) * flip;
			float l0 = n0.Length(), l1 = n1.Length();
			if ( l0 < 1e-12f || l1 < 1e-12f ) continue;
			float off = Vec3.Dot( stockFit[v] - q, n0 / l0 ) * MathF.Sqrt( l1 / l0 );
			var exact = bodyRest[a] * bary[0] + bodyRest[b] * bary[1] + bodyRest[c] * bary[2] + n1 / l1 * off;
			float w = Math.Clamp( (OntoFar - d) / (OntoFar - OntoNear), 0f, 1f );
			rest[v] = rest[v] * (1 - w) + exact * w;
		}
		return rest;
	}

	const float OntoNear = 0.003f * Units.Metre;   // paint this close to the skin goes back exactly with it
	const float OntoFar = 0.006f * Units.Metre;    // and from this far, by its bones alone

	/// <summary>
	/// Where a point lying on this body's skin in the stock pose and proportions ends up in the
	/// body's rest pose: back the way the skin under it came in, which is what <see cref="SkinUnder"/>,
	/// <see cref="FromStockProportions"/> and <see cref="ToBodyRest"/> do to cloth on the skin.
	/// Also how far it goes there for a step along <paramref name="along"/>.
	/// </summary>
	public Vec3 RestOf( Vec3 p, Vec3 along, out Vec3 rate )
	{
		rate = along;
		skinMesh.Nearest( p, out var q, out int tri );
		if ( tri < 0 ) return p;
		int a = skinMesh.Tris[tri * 3], b = skinMesh.Tris[tri * 3 + 1], c = skinMesh.Tris[tri * 3 + 2];
		var bary = Barycentric( q, skinMesh.Verts[a], skinMesh.Verts[b], skinMesh.Verts[c] );
		var sum = Vec3.Zero;
		var turn = Vec3.Zero;
		float total = 0;
		for ( int k = 0; k < 3; k++ )
		{
			int corner = k == 0 ? a : k == 1 ? b : c;
			for ( int j = 0; j < 4; j++ )
			{
				int bone = skinBones[corner * 4 + j];
				float w = skinWeights[corner * 4 + j] * bary[k];
				if ( bone < 0 || w <= 0 ) continue;
				int by = follow[bone];
				sum += (Quat.Rotate( carry[by], Scaled( p - stockPositions[by], by, false ) ) + RestPositions[by]) * w;
				turn += Quat.Rotate( carry[by], Scaled( along, by, false ) ) * w;
				total += w;
			}
		}
		if ( total <= 1e-6f ) return p;
		rate = turn / total;
		return sum / total;
	}

	/// <summary>The rotation from the stock bind pose to the body's rest pose for a stock bone.</summary>
	public float[] Carry( int stockBone ) => carry[stockBone];

	/// <summary>
	/// A garment bone's rest transform on the body. For a bone the stock skeleton has, the
	/// body's; for one it doesn't (a hat's own jiggle bone), placed under the nearest stock
	/// bone the way the garment itself places it.
	/// </summary>
	public (Vec3 Position, float[] Rotation) GarmentBoneRest( int stockBone, Vec3 position, float[] rotation )
	{
		var c = carry[stockBone];
		return (RestPositions[stockBone] + Quat.Rotate( c, position - stockPositions[stockBone] ), Quat.Multiply( c, rotation ));
	}

	/// <summary>
	/// The rest transforms on the body of the garment's own bones that its physics swings
	/// (<paramref name="own"/>: ones the stock skeleton doesn't have, like a pendant's jiggle
	/// bone), each with the vertices it carries. The engine swings such a bone about its origin,
	/// so the bone has to stay where it was on its vertices: at a pendant's loop. Carried by its stock ancestor (<see cref="GarmentBoneRest"/>) it doesn't,
	/// as the fit moves the vertices with the skin under them and grows them with the body; on
	/// Akira a swag chain's pendant came out 4 cm above its bone and swung about its middle.
	/// Each bone takes the turn and shift that best carry its vertices from the stock pose
	/// (<paramref name="garment"/>'s positions) to <paramref name="rest"/>. A bone with too few
	/// vertices to tell follows its parent, if that is the garment's own too, or the shift of
	/// what it has. Null for the bones that aren't the garment's own.
	/// </summary>
	public (Vec3 Position, float[] Rotation)[] OwnBoneRests( SkinnedGeometry garment, int[] boneMap, bool[] own, Vec3[] rest )
	{
		int bones = garment.BoneNames.Length;
		var weight = new float[bones];
		var from = new Vec3[bones];
		var to = new Vec3[bones];
		var held = new int[bones];
		for ( int k = 0; k < garment.BoneIndex.Length; k++ )
		{
			int b = garment.BoneIndex[k];
			float w = garment.BoneWeight[k];
			if ( b < 0 || b >= bones || !own[b] || w <= 0 ) continue;
			int v = k / 4;
			weight[b] += w;
			from[b] += garment.Positions[v] * w;
			to[b] += rest[v] * w;
			if ( w >= 0.5f ) held[b]++;
		}

		var spread = new float[bones][];
		var size = new float[bones];
		for ( int b = 0; b < bones; b++ )
		{
			if ( weight[b] <= 0 ) continue;
			from[b] /= weight[b];
			to[b] /= weight[b];
			spread[b] = new float[9];
		}
		for ( int k = 0; k < garment.BoneIndex.Length; k++ )
		{
			int b = garment.BoneIndex[k];
			float w = garment.BoneWeight[k];
			if ( b < 0 || b >= bones || spread[b] == null || w <= 0 ) continue;
			int v = k / 4;
			Vec3 p = garment.Positions[v] - from[b], q = rest[v] - to[b];
			var h = spread[b];
			size[b] += w * p.LengthSquared();
			h[0] += w * q.X * p.X; h[1] += w * q.X * p.Y; h[2] += w * q.X * p.Z;
			h[3] += w * q.Y * p.X; h[4] += w * q.Y * p.Y; h[5] += w * q.Y * p.Z;
			h[6] += w * q.Z * p.X; h[7] += w * q.Z * p.Y; h[8] += w * q.Z * p.Z;
		}

		// Parents come first in a model's skeleton, so a parent's rest is there for its children.
		var result = new (Vec3 Position, float[] Rotation)[bones];
		for ( int i = 0; i < bones; i++ )
		{
			if ( !own[i] ) continue;
			var position = garment.BonePositions[i];
			var rotation = garment.BoneRotations[i];
			int s = boneMap[i];
			int parent = garment.BoneParents[i];
			var matrix = held[i] >= OwnBoneHeld ? GarmentFit.Rotation( spread[i] ) : null;
			if ( matrix != null )
			{
				// Grown or shrunk with the body, a pendant still hangs from its loop: the bone
				// keeps its place on the vertices at their new size.
				float grown = 0;
				for ( int k = 0; k < 9; k++ ) grown += matrix[k] * spread[i][k];
				grown = size[i] > 0 ? Math.Clamp( grown / size[i], 0.25f, 4f ) : 1f;
				var turn = FromMatrix( matrix );
				result[i] = (to[i] + Quat.Rotate( turn, position - from[i] ) * grown, Quat.Multiply( turn, rotation ));
			}
			else if ( parent >= 0 && parent < i && own[parent] )
			{
				var turn = Quat.Multiply( result[parent].Rotation, Quat.Inverse( garment.BoneRotations[parent] ) );
				result[i] = (result[parent].Position + Quat.Rotate( turn, position - garment.BonePositions[parent] ), Quat.Multiply( turn, rotation ));
			}
			else if ( s >= 0 )
			{
				result[i] = GarmentBoneRest( s, position, rotation );
				if ( weight[i] > 0 ) result[i].Position += to[i] - (RestPositions[s] + Quat.Rotate( carry[s], from[i] - stockPositions[s] ));
			}
			else
				result[i] = (position + (weight[i] > 0 ? to[i] - from[i] : Vec3.Zero), rotation);
		}
		return result;
	}

	const int OwnBoneHeld = 3;   // vertices mostly on a bone it takes to tell how they turned

	// A rotation matrix (row-major) as a quaternion.
	static float[] FromMatrix( float[] m )
	{
		float trace = m[0] + m[4] + m[8];
		float x, y, z, w;
		if ( trace > 0 )
		{
			float s = MathF.Sqrt( trace + 1f ) * 2f;
			w = 0.25f * s; x = (m[7] - m[5]) / s; y = (m[2] - m[6]) / s; z = (m[3] - m[1]) / s;
		}
		else if ( m[0] > m[4] && m[0] > m[8] )
		{
			float s = MathF.Sqrt( 1f + m[0] - m[4] - m[8] ) * 2f;
			w = (m[7] - m[5]) / s; x = 0.25f * s; y = (m[1] + m[3]) / s; z = (m[2] + m[6]) / s;
		}
		else if ( m[4] > m[8] )
		{
			float s = MathF.Sqrt( 1f + m[4] - m[0] - m[8] ) * 2f;
			w = (m[2] - m[6]) / s; x = (m[1] + m[3]) / s; y = 0.25f * s; z = (m[5] + m[7]) / s;
		}
		else
		{
			float s = MathF.Sqrt( 1f + m[8] - m[0] - m[4] ) * 2f;
			w = (m[3] - m[1]) / s; x = (m[2] + m[6]) / s; y = (m[5] + m[7]) / s; z = 0.25f * s;
		}
		float length = MathF.Sqrt( x * x + y * y + z * z + w * w );
		return new[] { x / length, y / length, z / length, w / length };
	}

	/// <summary>
	/// Grows a garment that was fitted in the stock proportions back to the body's. Each
	/// vertex is scaled about the bones it is skinned to, which is exactly what bone merging
	/// will then carry over to the body's own skeleton.
	/// </summary>
	/// <param name="boneIndex">Four bone slots per garment vertex, as stock bone indices.</param>
	public Vec3[] FromStockProportions( Vec3[] fitted, int[] boneIndex, float[] boneWeight, SkinnedGeometry stock )
	{
		return Grow( fitted, boneIndex, boneWeight, stock );
	}


	/// <summary>
	/// The body's skin weights under each garment vertex: those of the nearest point of the
	/// body, as stock bones. Taken back to the body by these instead of its own weights,
	/// cloth comes back exactly the way the skin under it went in. By its own weights, cloth
	/// over a part the garment and the body weight differently (a chest the body gives to the
	/// upper spine and the garment to the lower, a collar on a neck that sits elsewhere on
	/// this body) comes back a different size or in a different place than the skin.
	/// </summary>
	/// <param name="body">The body in the stock pose and proportions (<see cref="Positions"/>).</param>
	/// <param name="bodyBones">The body's four bone slots per vertex, as stock bone indices.</param>
	/// <param name="ownBones">The garment's own bone slots. A solid piece gets one set of weights for the whole of it, so it stays rigid: the skin's where it sits on the skin (a button on a shirt moves with the cloth round it), its own where it doesn't.</param>
	/// <param name="skip">Stock bones whose weights don't count (the eyes: what an eye bone drives is the eye, not skin anything rests on).</param>
	/// <param name="cloth">The vertices fitted as cloth (<see cref="GarmentFit.Fit"/>), or null. Cloth lying on cloth under it is as far off the skin as the innermost layer, see <see cref="OffSkin"/>.</param>
	public static (int[] Bones, float[] Weights) SkinUnder( Vec3[] fitted, int[] tris, TriMesh body, int[] bodyBones, float[] bodyWeights, int[] ownBones, float[] ownWeights, bool[] solid, bool[] skip, bool[] cloth = null )
	{
		var weld = MeshTools.Weld( fitted, out int n );
		var first = new int[n];
		for ( int i = fitted.Length - 1; i >= 0; i-- ) first[weld[i]] = i;

		TriMesh layers = null;
		if ( cloth != null )
		{
			var clothTris = new List<int>();
			for ( int t = 0; t + 2 < tris.Length; t += 3 )
				if ( cloth[tris[t]] && cloth[tris[t + 1]] && cloth[tris[t + 2]] ) { clothTris.Add( tris[t] ); clothTris.Add( tris[t + 1] ); clothTris.Add( tris[t + 2] ); }
			if ( clothTris.Count > 0 ) layers = new TriMesh( fitted, clothTris.ToArray() );
		}
		var hits = new List<(float T, bool Leaving, int Tri)>();

		// Per welded vertex: the skin's weights near the skin, the garment's own further out,
		// blended by how far off the skin it is. Bones get a column each, for the smoothing.
		var column = new Dictionary<int, int>();
		var cells = new List<(int Vertex, int Column, float Weight)>();
		var fixedRow = new bool[n];
		var blend = new Dictionary<int, float>();
		for ( int r = 0; r < n; r++ )
		{
			int v = first[r];
			blend.Clear();
			float skinShare = 0;
			{
				float d = body.Nearest( fitted[v], out var q, out int tri );
				if ( layers != null && cloth[v] ) d = OffSkin( fitted[v], q, d, layers, hits );
				skinShare = Math.Clamp( (SkinFar - d) / (SkinFar - SkinNear), 0f, 1f );
				fixedRow[r] = d <= SkinNear || (solid != null && solid[v]);   // on the skin: exactly the skin's way back, not evened out
				if ( tri >= 0 && skinShare > 0 )
				{
					int a = body.Tris[tri * 3], b = body.Tris[tri * 3 + 1], c = body.Tris[tri * 3 + 2];
					var bary = Barycentric( q, body.Verts[a], body.Verts[b], body.Verts[c] );
					float total = 0;
					for ( int k = 0; k < 3; k++ )
					{
						int corner = k == 0 ? a : k == 1 ? b : c;
						for ( int j = 0; j < 4; j++ )
						{
							int bone = bodyBones[corner * 4 + j];
							float w = bodyWeights[corner * 4 + j] * bary[k];
							if ( bone < 0 || w <= 0 || skip[bone] ) continue;
							blend[bone] = blend.TryGetValue( bone, out float sum ) ? sum + w : w;
							total += w;
						}
					}
					if ( total > 1e-6f )
						foreach ( int bone in new List<int>( blend.Keys ) ) blend[bone] = blend[bone] / total * skinShare;
					else
						skinShare = 0;
				}
				else
				{
					skinShare = 0;
				}
			}

			float own = 0;
			for ( int j = 0; j < 4; j++ ) if ( ownBones[v * 4 + j] >= 0 ) own += ownWeights[v * 4 + j];
			for ( int j = 0; j < 4; j++ )
			{
				int bone = ownBones[v * 4 + j];
				if ( bone < 0 || own <= 0 ) continue;
				float w = ownWeights[v * 4 + j] / own * (1f - skinShare);
				blend[bone] = blend.TryGetValue( bone, out float sum ) ? sum + w : w;
			}

			foreach ( var (bone, w) in blend )
			{
				if ( !column.TryGetValue( bone, out int col ) ) column[bone] = col = column.Count;
				cells.Add( (r, col, w) );
			}
		}

		int k2 = column.Count;
		var grid = new float[n * k2];
		foreach ( var (r, col, w) in cells ) grid[r * k2 + col] += w;

		// A solid piece is one thing: every vertex of it gets the piece's mean weights.
		var edges = MeshTools.Edges( tris, weld );
		if ( solid != null )
		{
			var piece = new int[n];
			for ( int r = 0; r < n; r++ ) piece[r] = r;
			int Root( int x ) { while ( piece[x] != x ) { piece[x] = piece[piece[x]]; x = piece[x]; } return x; }
			foreach ( var (a, b) in edges )
				if ( solid[first[a]] && solid[first[b]] ) { int ra = Root( a ), rb = Root( b ); if ( ra != rb ) piece[ra] = rb; }
			var sum = new Dictionary<int, float[]>();
			var members = new Dictionary<int, int>();
			for ( int r = 0; r < n; r++ )
			{
				if ( !solid[first[r]] ) continue;
				int root = Root( r );
				if ( !sum.TryGetValue( root, out var total ) ) { sum[root] = total = new float[k2]; members[root] = 0; }
				for ( int c = 0; c < k2; c++ ) total[c] += grid[r * k2 + c];
				members[root]++;
			}
			for ( int r = 0; r < n; r++ )
			{
				if ( !solid[first[r]] ) continue;
				int root = Root( r );
				for ( int c = 0; c < k2; c++ ) grid[r * k2 + c] = sum[root][c] / members[root];
			}
		}

		// Neighbours off the skin that took their weights from different bits of it (the padding
		// of a glove over fingers and over the palm) would pull a garment apart. Even them out
		// over the garment's own edges; cloth on the skin keeps exactly what is under it.
		var next = new float[grid.Length];
		var count = new int[n];
		foreach ( var (a, b) in edges ) { count[a]++; count[b]++; }
		for ( int pass = 0; pass < SmoothPasses; pass++ )
		{
			Array.Clear( next );
			foreach ( var (a, b) in edges )
				for ( int c = 0; c < k2; c++ ) { next[a * k2 + c] += grid[b * k2 + c]; next[b * k2 + c] += grid[a * k2 + c]; }
			for ( int r = 0; r < n; r++ )
			{
				if ( fixedRow[r] || count[r] == 0 ) continue;
				for ( int c = 0; c < k2; c++ ) grid[r * k2 + c] = 0.5f * grid[r * k2 + c] + 0.5f * next[r * k2 + c] / count[r];
			}
		}

		var boneOf = new int[k2];
		foreach ( var (bone, col) in column ) boneOf[col] = bone;
		var bones = new int[fitted.Length * 4];
		var weights = new float[fitted.Length * 4];
		var row = new float[k2];
		for ( int v = 0; v < fitted.Length; v++ )
		{
			// Keep the four that count most, the way skinning would.
			Array.Copy( grid, weld[v] * k2, row, 0, k2 );
			for ( int slot = 0; slot < 4; slot++ )
			{
				int best = -1;
				float most = 0;
				for ( int c = 0; c < k2; c++ ) if ( row[c] > most ) { most = row[c]; best = c; }
				bones[v * 4 + slot] = best >= 0 ? boneOf[best] : -1;
				if ( best < 0 ) continue;
				weights[v * 4 + slot] = most;
				row[best] = 0;
			}
		}

		return (bones, weights);
	}

	const float SkinNear = 0.03f * Units.Metre;   // cloth this close to the skin goes back with it
	const float SkinFar = 0.06f * Units.Metre;    // and this far off, by its own weights
	const int SmoothPasses = 3;
	const float OwnLayer = 0.001f * Units.Metre;  // cloth closer to a vertex than this is the vertex's own surface

	/// <summary>
	/// How far off the skin a bit of cloth is, counted from the innermost of the garment's
	/// layers between it and the skin: a thick garment rests on the skin through its inside. A
	/// Santa jacket's fur trim is fur over cloth over more fur, 11 cm thick. Its inside lay on
	/// the bust and went back with the bust, its outside, more than 6 cm off the skin, went back
	/// by the jacket's own weights, which shrink less than a bust smaller than the stock one.
	/// The trim came back a fifth thicker and stood off the bust like a shelf.
	/// </summary>
	static float OffSkin( Vec3 p, Vec3 skin, float distance, TriMesh layers, List<(float T, bool Leaving, int Tri)> hits )
	{
		if ( distance <= SkinNear ) return distance;
		Vec3 way = (skin - p) / distance;
		layers.Crossings( p + way * OwnLayer, way, distance - OwnLayer, hits );
		float innermost = 0;
		foreach ( var hit in hits ) innermost = MathF.Max( innermost, hit.T + OwnLayer );
		return distance - innermost;
	}

	static float[] Barycentric( Vec3 p, Vec3 a, Vec3 b, Vec3 c )
	{
		Vec3 e0 = b - a, e1 = c - a, e2 = p - a;
		float d00 = Vec3.Dot( e0, e0 ), d01 = Vec3.Dot( e0, e1 ), d11 = Vec3.Dot( e1, e1 ), d20 = Vec3.Dot( e2, e0 ), d21 = Vec3.Dot( e2, e1 );
		float den = d00 * d11 - d01 * d01;
		if ( MathF.Abs( den ) < 1e-20f ) return new[] { 1f, 0f, 0f };
		float v = (d11 * d20 - d01 * d21) / den, w = (d00 * d21 - d01 * d20) / den;
		return new[] { 1f - v - w, v, w };
	}

	Vec3[] Grow( Vec3[] fitted, int[] boneIndex, float[] boneWeight, SkinnedGeometry stock )
	{
		var result = new Vec3[fitted.Length];
		for ( int v = 0; v < fitted.Length; v++ )
		{
			var sum = Vec3.Zero;
			float weight = 0;
			for ( int j = 0; j < 4; j++ )
			{
				int bone = boneIndex[v * 4 + j];
				float w = boneWeight[v * 4 + j];
				if ( bone < 0 || w <= 0 ) continue;
				int by = follow[bone];
				Vec3 joint = stock.BonePositions[by];
				sum += (joint + Scaled( fitted[v] - joint, by, false )) * w;
				weight += w;
			}

			result[v] = weight > 1e-6f ? sum / weight : fitted[v];
		}

		return result;
	}
}

/// <summary>
/// The neck on the stock body and on another one, as a share of its height: 0 at the
/// collarbones, 1 at the bottom of the chin. Cloth over the neck goes no further up this body's
/// neck than it went up the stock body's.
///
/// A body can have a far shorter neck for the size of its head (Aurora's is half the human's,
/// her head as big). Cloth goes back to such a body with the skin it lies on, and the top of a
/// stand-up collar lies on the bottom of the skull: it stays as far up the head as on the stock
/// body, and on a neck half as long that is up to the ears.
///
/// The citizen has no chin and no nape to find: its head goes straight down into its chest in one
/// thick column. Its clothes are cut for the human's neck all the same, their collars as high
/// over its collarbones as the human versions' over his (puffer jacket 136/186 mm in front and
/// behind, the human's 150/201; biker jacket 135/181, 131/186), so its chin and nape are taken
/// where the human male's are for the height of his head joint. Without that, a gorget as high
/// as a puffer's collar went up a neck half as long by its whole height, over Aurora's chin.
/// </summary>
public sealed class Neck
{
	const float Reach = 0.11f * Units.Metre;    // skin further from the neck's axis than this is the shoulders or the face, not the neck
	const float Core = 0.09f * Units.Metre;     // and closer than this, all of it is
	const float Thick = 0.075f * Units.Metre;   // those are for a neck up to this thick from its axis (the human's: 6 cm); a thicker one has them as many times further out (the citizen's: 14 cm)
	const float ChinShare = 0.87f;              // with no chin to find, the chin is this share of the way from the collarbones up to the head joint (the human male's)
	const float NapeShare = 1.23f;              // and the nape this share
	const float SkullShare = 1.45f;             // and the start of the skull behind this share
	const float Borrowed = 0.9f;                // a neck at least this many times as long as such a borrowed one keeps the cloth the skin carries to it (the humans': 0.97, 1.0)
	const float Short = 0.7f;                   // and one this short or shorter is held to the borrowed one's shares in full (Aurora's and Akira's: 0.5)
	const float Above = 0.02f * Units.Metre;    // the neck reaches this far over its top, and fades out over as much again
	const float FaceSide = 0.5f;                // the neck's top is the chin straight ahead, the nape from this far round on (cosine: 60 degrees)
	const float Beside = 0.3f, Behind = 0.7f;    // and where the head begins from 107 to 134 degrees round on, past the ears (minus the cosines)
	const float MaxDrop = 0.05f * Units.Metre;  // cloth is lowered by no more than this down the body's neck in its rest pose
	const float MinClear = 0.003f * Units.Metre; // a collar taken in round the neck stays this far off it
	const float MaxMove = 0.15f * Units.Metre;  // and moved no further than this in the stock pose, where a neck can be twice as long (the citizen's for Akira's)

	readonly Repose pose;
	readonly float axisX, axisY;            // the stock neck's axis, upright through its joint
	readonly float reach, core;             // Reach and Core for the stock neck's thickness
	readonly float weight;                  // how much of the way down to its share cloth is lowered
	readonly float shorter;                 // how much shorter this neck is than the stock one, 0..1 (see Borrowed and Short)
	readonly float collar, chin, nape, skull;    // heights on the stock body; skull NaN when it wasn't found
	readonly float restCollar, restChin, restSkull;  // heights on the body in its rest pose, along its up
	readonly Vec3 up;

	Neck( Repose pose, float axisX, float axisY, float scale, float weight, float shorter, float collar, float chin, float nape, float skull, float restCollar, float restChin, float restSkull, Vec3 up )
	{
		this.weight = weight;
		this.shorter = shorter;
		this.skull = skull;
		this.restSkull = restSkull;
		this.pose = pose;
		this.axisX = axisX;
		this.axisY = axisY;
		reach = Reach * scale;
		core = Core * scale;
		this.collar = collar;
		this.chin = chin;
		this.nape = nape;
		this.restCollar = restCollar;
		this.restChin = restChin;
		this.up = up;
	}

	/// <param name="bodyChin">The bottom of the chin on this body, in the stock pose or with <paramref name="atRest"/> in its rest pose.</param>
	/// <param name="stockChin">How high the bottom of the chin is on the stock body. NaN for a stock body without one, see ChinShare.</param>
	/// <param name="napeHeight">How high the nape is on the stock body. NaN for a stock body without one.</param>
	/// <param name="stockSkull">Where the head begins behind on the stock body (see Repose.SkullStart); NaN when it wasn't found.</param>
	/// <param name="restSkull">The same on this body in its rest pose, measured along its up; NaN when it wasn't found.</param>
	public static Neck Find( Repose pose, SkinnedGeometry stock, TriMesh stockMesh, Vec3 bodyChin, float stockSkull, float restSkull, bool atRest = false, float stockChin = float.NaN, float napeHeight = float.NaN )
	{
		int neck = Array.IndexOf( stock.BoneNames, "neck_0" );
		int left = Array.IndexOf( stock.BoneNames, "clavicle_L" ), right = Array.IndexOf( stock.BoneNames, "clavicle_R" );
		int root = Array.IndexOf( stock.BoneParents, -1 );
		if ( neck < 0 || left < 0 || right < 0 || root < 0 ) return null;

		// Up on the body in its rest pose is wherever the stock body's up went.
		var up = Quat.Rotate( pose.Carry( root ), new Vec3( 0, 0, 1 ) );
		float collar = 0.5f * (stock.BonePositions[left].Z + stock.BonePositions[right].Z);
		bool borrowed = float.IsNaN( stockChin ) || float.IsNaN( napeHeight );
		if ( borrowed )
		{
			int head = Array.IndexOf( stock.BoneNames, "head" );
			if ( head < 0 ) return null;
			float joint = stock.BonePositions[head].Z - collar;
			if ( float.IsNaN( stockChin ) ) stockChin = collar + ChinShare * joint;
			if ( float.IsNaN( napeHeight ) ) napeHeight = collar + NapeShare * joint;
			if ( float.IsNaN( stockSkull ) ) stockSkull = collar + SkullShare * joint;
		}
		float restCollar = Vec3.Dot( 0.5f * (pose.RestPositions[left] + pose.RestPositions[right]), up );
		float restChin = Vec3.Dot( atRest ? bodyChin : pose.RestOf( bodyChin, up, out _ ), up );
		if ( stockChin - collar < 0.02f * Units.Metre || restChin - restCollar < 0.02f * Units.Metre ) return null;

		// A neck borrowed from the human is only as good as the human's: the citizen's clothes
		// come onto the humans as their own versions sit (fitted from it, a T-shirt's back is 137
		// mm over the human male's collarbones, his own 150; on the borrowed neck it would be
		// lowered to 111). Only a far shorter neck is held to it.
		float shorter = Math.Clamp( (Borrowed - (restChin - restCollar) / (stockChin - collar)) / (Borrowed - Short), 0f, 1f );
		float weight = borrowed ? shorter : 1f;
		if ( weight <= 0 ) return null;
		if ( float.IsNaN( stockSkull ) || float.IsNaN( restSkull ) || stockSkull - collar < 0.02f * Units.Metre || restSkull - restCollar < 0.02f * Units.Metre ) stockSkull = restSkull = float.NaN;

		// How thick the stock neck is: from its axis out to its skin halfway up it, where it is
		// thinnest. Sideways a ray can slip out through the seam between a head and a body made
		// apart (the humans': 18 cm out, on the shoulders).
		float axisX = stock.BonePositions[neck].X, axisY = stock.BonePositions[neck].Y;
		var middle = new Vec3( axisX, axisY, collar + 0.5f * (stockChin - collar) );
		var hits = new List<(float T, bool Leaving, int Tri)>();
		float thick = float.MaxValue;
		for ( int k = 0; k < 8; k++ )
		{
			float angle = k * MathF.PI / 4;
			stockMesh.Crossings( middle, new Vec3( MathF.Cos( angle ), MathF.Sin( angle ), 0 ), 0.5f * Units.Metre, hits );
			if ( hits.Count > 0 ) thick = MathF.Min( thick, hits[0].T );
		}
		float scale = thick < float.MaxValue ? MathF.Max( 1f, thick / Thick ) : 1f;
		return new Neck( pose, axisX, axisY, scale, weight, shorter, collar, stockChin, napeHeight, stockSkull, restCollar, restChin, restSkull, up );
	}

	/// <summary>How much a garment point whose closest stock skin is <paramref name="skin"/> is over the neck. 0..1.</summary>
	public float Zone( Vec3 skin ) => Zone( skin, 2 * Above );

	/// <summary>
	/// The same for being taken in round the neck (<see cref="TakeIn"/>): only up to the neck's
	/// top, not over it, and right behind up to where the head begins. Over the nape at the
	/// sides are the ears, and a balaclava taken in to the head there had them come through it.
	/// </summary>
	public float ZoneIn( Vec3 skin ) => Zone( skin, 0, true );

	float Zone( Vec3 skin, float over, bool toSkull = false )
	{
		float dx = skin.X - axisX, dy = skin.Y - axisY;
		float r = MathF.Sqrt( dx * dx + dy * dy );
		float across = Math.Clamp( (reach - r) / (reach - core), 0f, 1f );

		// The neck's top: the chin in front, the nape behind and at the sides (below the ears).
		// Over the chin in front is the face, and cloth on it goes where the face goes.
		float round = Math.Clamp( (1 - dx / MathF.Max( r, 1e-6f )) / (1 - FaceSide), 0f, 1f );
		float neckTop = chin + (nape - chin) * round;
		if ( toSkull && !float.IsNaN( skull ) && skull > nape )
			neckTop += (skull - nape) * Math.Clamp( (-dx / MathF.Max( r, 1e-6f ) - Beside) / (Behind - Beside), 0f, 1f );
		float top = Math.Clamp( (neckTop + over - skin.Z) / Above, 0f, 1f );
		float bottom = Math.Clamp( (skin.Z - collar) / (0.5f * (chin - collar)), 0f, 1f );
		return weight * across * top * bottom;
	}

	// How far round from the front the stock skin is: 0 under the chin, 1 from FaceSide on.
	float Round( Vec3 skin )
	{
		float dx = skin.X - axisX, dy = skin.Y - axisY;
		float r = MathF.Sqrt( dx * dx + dy * dy );
		return Math.Clamp( (1 - dx / MathF.Max( r, 1e-6f )) / (1 - FaceSide), 0f, 1f );
	}

	/// <summary>
	/// How far to move a point in the stock pose and proportions up (never more than 0, so down
	/// or not at all) for it to be no higher up the neck once it is back in the body's rest pose
	/// than <paramref name="stockPoint"/> was up the stock body's, over the stock skin
	/// <paramref name="skin"/>: as a share of the way from the collarbones to the chin, and
	/// behind also of the way to where the head begins.
	///
	/// A big head comes far down the back of a short neck: Aurora's skull begins 9 cm over her
	/// collarbones, the human male's 22 cm over his, while her chin is half as far up as his.
	/// A puffer jacket's collar, 2.5 cm under the human's skull, went as far up her neck's share
	/// to the chin and 2.5 cm up the back of her head.
	/// </summary>
	public float Lower( Vec3 fitted, Vec3 stockPoint, Vec3 skin )
	{
		float target = restCollar + (stockPoint.Z - collar) / (chin - collar) * (restChin - restCollar);
		if ( !float.IsNaN( skull ) )
		{
			float behind = restCollar + (stockPoint.Z - collar) / (skull - collar) * (restSkull - restCollar);
			if ( behind < target ) target += (behind - target) * Round( skin ) * shorter;
		}
		var rise = new Vec3( 0, 0, 1 );
		float drop = 0;
		for ( int pass = 0; pass < 3; pass++ )
		{
			// The way back is linear for the skin under the point; that skin changes as the
			// point moves, so a couple of rounds.
			var rest = pose.RestOf( fitted + rise * drop, rise, out var rate );
			float height = Vec3.Dot( rest, up );
			if ( pass == 0 )
			{
				if ( height <= target ) return 0;
				target = MathF.Max( target, height - MaxDrop );
			}
			float slope = Vec3.Dot( rate, up );
			if ( slope < 0.1f ) break;
			drop += (target - height) / slope;
		}
		return Math.Clamp( drop, -MaxMove, 0f );
	}

	/// <summary>
	/// How far to move a point in the stock pose in towards the neck for it to stand no further
	/// off this body's neck than <paramref name="clearance"/>, the cloth's gap to the stock skin.
	/// Cloth goes with the skin it lies on, and a collar that stands round a thick neck and
	/// meets the bottom of the skull stood round Aurora's thin neck as wide as round the human's,
	/// opening out towards its top. Only on a neck far shorter than the stock one (see Borrowed):
	/// the humans' fill their collars.
	/// </summary>
	public Vec3 TakeIn( Vec3 fitted, float clearance, TriMesh body, List<(float T, bool Leaving, int Tri)> hits )
	{
		if ( shorter <= 0 ) return Vec3.Zero;
		var away = new Vec3( fitted.X - axisX, fitted.Y - axisY, 0 );
		float r = away.Length();
		if ( r < 1e-4f * Units.Metre ) return Vec3.Zero;
		away /= r;
		body.Crossings( new Vec3( axisX, axisY, fitted.Z ), away, r, hits );
		// The neck is what the ray leaves first from the axis; from outside it, it is something else.
		if ( hits.Count == 0 || !hits[0].Leaving ) return Vec3.Zero;
		float want = hits[0].T + MathF.Max( clearance, MinClear );
		return want < r ? away * ((want - r) * shorter) : Vec3.Zero;
	}
}

/// <summary>
/// The trunk of a body from the crotch to the shoulders, in the stock pose and in its rest pose,
/// as a share of its height: 0 at the crotch, 1 at the shoulder joints. Cloth on the lower trunk
/// (a hem, a waistband) is put as far up it in the rest pose as it was in the stock pose.
///
/// Every rig puts its pelvis and spine joints somewhere else up the trunk: the citizen's pelvis
/// joint is 9 cm over its crotch, the humans' 15 cm. Taken into the stock pose by its joints, a
/// human's skin between the crotch and the pelvis joint is squeezed into two thirds of its
/// height, and cloth that goes back with that skin is stretched up the trunk by as much: shirts
/// fitted from the citizen ended at the waist, 5 to 7 cm higher than the humans' own versions
/// of them, and trousers came up to the navel. The crotch and the shoulders are where they are
/// on any body, whatever the joints in between. A body with the stock body's skeleton (a woman
/// for clothes made for a man) comes back as it went, and its clothes with it. Higher up, under
/// the arms, the arms' and collarbones' joints put the skin where it belongs, and nothing is
/// changed there.
/// </summary>
public sealed class Trunk
{
	const float Full = 0.55f;       // cloth is kept at its share of the trunk up to this share
	const float Fade = 0.65f;       // and left as it is from this share up, under the arms
	const float Core = 2.2f;        // skin within this many times the hip joints' offset from the middle is the trunk
	const float Reach = 2.7f;       // and none of it from this far: the arms hanging beside it
	const float MaxShift = 0.08f * Units.Metre;   // cloth is moved no further than this along it
	const float Probe = 0.1f;       // the crotch is the lowest of the trunk found this far either side of the hip joints, as a share of how far apart they are

	readonly Repose pose;
	readonly float axisX, axisY, core, reach;
	readonly float crotch, shoulders;             // heights on the body in the stock pose
	readonly float restCrotch, restShoulders;     // and in its rest pose, along its up
	readonly Vec3 up;

	Trunk( Repose pose, float axisX, float axisY, float core, float reach, float crotch, float shoulders, float restCrotch, float restShoulders, Vec3 up )
	{
		this.pose = pose;
		this.axisX = axisX;
		this.axisY = axisY;
		this.core = core;
		this.reach = reach;
		this.crotch = crotch;
		this.shoulders = shoulders;
		this.restCrotch = restCrotch;
		this.restShoulders = restShoulders;
		this.up = up;
	}

	/// <param name="posedMesh">This body in the stock pose and proportions.</param>
	public static Trunk Find( Repose pose, SkinnedGeometry stock, TriMesh posedMesh )
	{
		int hipL = Array.IndexOf( stock.BoneNames, "leg_upper_L" ), hipR = Array.IndexOf( stock.BoneNames, "leg_upper_R" );
		int kneeL = Array.IndexOf( stock.BoneNames, "leg_lower_L" ), kneeR = Array.IndexOf( stock.BoneNames, "leg_lower_R" );
		int armL = Array.IndexOf( stock.BoneNames, "arm_upper_L" ), armR = Array.IndexOf( stock.BoneNames, "arm_upper_R" );
		int root = Array.IndexOf( stock.BoneParents, -1 );
		if ( hipL < 0 || hipR < 0 || kneeL < 0 || kneeR < 0 || armL < 0 || armR < 0 || root < 0 ) return null;
		if ( !pose.BodyHas[hipL] || !pose.BodyHas[hipR] || !pose.BodyHas[armL] || !pose.BodyHas[armR] ) return null;

		Vec3 hips = 0.5f * (stock.BonePositions[hipL] + stock.BonePositions[hipR]);
		float apart = (stock.BonePositions[hipL] - stock.BonePositions[hipR]).Length();
		float knees = 0.5f * (stock.BonePositions[kneeL].Z + stock.BonePositions[kneeR].Z);
		if ( apart < 0.02f * Units.Metre || knees >= hips.Z ) return null;
		if ( !FindCrotch( posedMesh, hips, apart, knees, out var crotch ) ) return null;

		var up = Quat.Rotate( pose.Carry( root ), new Vec3( 0, 0, 1 ) );
		float shoulders = 0.5f * (stock.BonePositions[armL].Z + stock.BonePositions[armR].Z);
		float restShoulders = Vec3.Dot( 0.5f * (pose.RestPositions[armL] + pose.RestPositions[armR]), up );
		float restCrotch = Vec3.Dot( pose.RestOf( crotch, up, out _ ), up );
		if ( shoulders - crotch.Z < 0.1f * Units.Metre || restShoulders - restCrotch < 0.1f * Units.Metre ) return null;
		float side = 0.5f * apart;
		return new Trunk( pose, hips.X, hips.Y, Core * side, Reach * side, crotch.Z, shoulders, restCrotch, restShoulders, up );
	}

	// The crotch: the lowest point of the trunk between the legs, straight up from the knees'
	// height, about the hip joints. Nothing there below the hip joints, there is no crotch.
	static bool FindCrotch( TriMesh mesh, Vec3 hips, float apart, float knees, out Vec3 crotch )
	{
		crotch = default;
		var hits = new List<(float T, bool Leaving, int Tri)>();
		bool found = false;
		for ( int k = -2; k <= 2; k++ )
		{
			var from = new Vec3( hips.X + k * Probe * apart, hips.Y, knees );
			mesh.Crossings( from, new Vec3( 0, 0, 1 ), hips.Z - knees, hits );
			if ( hits.Count == 0 ) continue;
			var at = from + new Vec3( 0, 0, hits[0].T );
			if ( !found || at.Z < crotch.Z ) crotch = at;
			found = true;
		}
		return found;
	}

	/// <summary>How much cloth whose closest stock skin is <paramref name="skin"/> is on the lower trunk. 0..1.</summary>
	public float Zone( Vec3 skin )
	{
		float dx = skin.X - axisX, dy = skin.Y - axisY;
		float across = Math.Clamp( (reach - MathF.Sqrt( dx * dx + dy * dy )) / (reach - core), 0f, 1f );
		float share = Share( skin );
		if ( share < 0 ) return 0;
		return across * Math.Clamp( (Fade - share) / (Fade - Full), 0f, 1f );
	}

	/// <summary>How high the crotch is in the stock pose.</summary>
	public float Crotch => crotch;

	/// <summary>The share of the trunk a point of the stock pose is at.</summary>
	public float Share( Vec3 stockPoint ) => (stockPoint.Z - crotch) / (shoulders - crotch);

	/// <summary>
	/// How far to move a point in the stock pose and proportions up (or down, below 0) for it to
	/// be at <paramref name="share"/> of this body's trunk once it is back in the body's rest pose.
	/// </summary>
	public float Shift( Vec3 fitted, float share )
	{
		float target = restCrotch + share * (restShoulders - restCrotch);
		var rise = new Vec3( 0, 0, 1 );
		float shift = 0;
		for ( int pass = 0; pass < 3; pass++ )
		{
			// The way back is linear for the skin under the point; that skin changes as the
			// point moves, so a couple of rounds.
			var rest = pose.RestOf( fitted + rise * shift, rise, out var rate );
			float slope = Vec3.Dot( rate, up );
			if ( slope < 0.1f ) break;
			shift += (target - Vec3.Dot( rest, up )) / slope;
		}
		return Math.Clamp( shift, -MaxShift, MaxShift );
	}
}

