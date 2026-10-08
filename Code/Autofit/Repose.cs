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

	/// <summary>The front of each eye, on the stock body and on this one (in the stock pose). What glasses go by.</summary>
	public List<(Vec3 Stock, Vec3 Body)> Eyes = new();
	/// <summary>The face down its middle, top to bottom: between the eyes, the tip of the nose, the mouth, the chin. Empty when a face has no mouth to find.</summary>
	public List<(Vec3 Stock, Vec3 Body)> Face = new();

	const float EyeReach = 0.07f * Units.Metre;    // how far around an eye the skin slides with it
	const float BustReach = 0.08f * Units.Metre;   // how far around the front of the chest
	const float FaceReach = 0.03f * Units.Metre;   // how far around the nose, the mouth and the chin
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
			if ( LowerFace( stock, stock.Positions, eyeOurs, out var stockFace ) && LowerFace( body, result.Positions, eyeTheirs, out var bodyFace ) )
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
		}

		// The front of the chest on each side: a bust sits higher or lower on one body than on
		// another, and rays from the spine only move skin towards or away from it. Cloth made to
		// bulge over the citizen's chest would bulge where the chest isn't.
		// Only a chest with a bust has a front to go by. On a flat one the foremost point is
		// anywhere across it and jumps between two bodies of the same shape.
		var fronts = new List<(Vec3 Ours, Vec3 Theirs)>();
		TriMesh reposedMesh = null;
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

		// Last: everything above goes by the skin as it is.
		if ( tips.Count > 0 ) Drape( result.Positions, body, to, stock, tips );

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
		for ( int k = nose + 1; k < slices && gap <= 2; k++ )
		{
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
	public Vec3[] ToBodyRest( Vec3[] fitted, int[] boneIndex, float[] boneWeight )
	{
		var turn = carry;
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
				sum += (Quat.Rotate( turn[by], fitted[v] - stockPositions[by] ) + RestPositions[by]) * w;
				weight += w;
			}

			result[v] = weight > 1e-6f ? sum / weight : fitted[v];
		}

		return result;
	}

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
	public static (int[] Bones, float[] Weights) SkinUnder( Vec3[] fitted, int[] tris, TriMesh body, int[] bodyBones, float[] bodyWeights, int[] ownBones, float[] ownWeights, bool[] solid, bool[] skip )
	{
		var weld = MeshTools.Weld( fitted, out int n );
		var first = new int[n];
		for ( int i = fitted.Length - 1; i >= 0; i-- ) first[weld[i]] = i;

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
