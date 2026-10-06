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
	/// Face landmarks: where each of the citizen's eyes is, and how far this body's eye is from
	/// there once the head has been lined up. Rays from the bones can't tell where a face's
	/// features are, the eye bones can.
	/// </summary>
	public List<(Vec3 At, Vec3 Shift)> Landmarks = new();

	/// <summary>
	/// The body's rest pose, per stock bone: where that bone is on the body and which way it
	/// points, in the body's model space. A stock bone the body doesn't have is placed under
	/// the nearest one it does have, where the stock skeleton puts it.
	/// </summary>
	public Vec3[] RestPositions;
	public float[][] RestRotations;

	/// <summary>Per stock bone, whether the body has a bone of that name.</summary>
	public bool[] BodyHas;

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
			float theirs = (body.BonePositions[i] - body.BonePositions[parent]).Length();
			float ours = (stock.BonePositions[to[i]] - stock.BonePositions[to[parent]]).Length();
			if ( ours < 0.02f * Units.Metre ) continue;   // too short to say anything
			(ratios[parent] ??= new List<float>()).Add( theirs / ours );
		}

		for ( int i = 0; i < bones; i++ )   // parents come first
		{
			int parent = body.BoneParents[i];
			scale[i] = parent >= 0 ? scale[parent] : 1f;
			if ( ratios[i] == null ) continue;
			ratios[i].Sort();
			scale[i] = Math.Clamp( ratios[i][ratios[i].Count / 2], 0.4f, 2.5f );
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
		Array.Fill( result.BoneScale, 1f );
		for ( int i = 0; i < bones; i++ )
		{
			if ( to[i] < 0 ) continue;
			turn[i] = Quat.Inverse( result.carry[to[i]] );
			shrink[i] = 1f / scale[from[i]];
			offset[i] = stock.BonePositions[to[i]] - Quat.Rotate( turn[i], body.BonePositions[from[i]] ) * shrink[i];
			if ( from[i] == i ) result.BoneScale[to[i]] = scale[i];
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

		for ( int i = 0; i < bones; i++ )
		{
			// The eye bone itself lands exactly on the citizen's. What says where the eye
			// really is on this face is its position carried along with the head.
			int parent = body.BoneParents[i];
			if ( !body.BoneNames[i].StartsWith( "eye_" ) || from[i] != i || parent < 0 || turn[parent] == null ) continue;
			Vec3 shift = Quat.Rotate( turn[parent], body.BonePositions[i] ) * shrink[parent] + offset[parent] - stock.BonePositions[to[i]];
			if ( shift.Length() < 0.15f * Units.Metre )
				result.Landmarks.Add( (stock.BonePositions[to[i]], shift) );
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
				sum += (Quat.Rotate( turn[bone], p ) * shrink[bone] + offset[bone]) * w;
				weight += w;
			}

			result.Positions[v] = weight > 1e-6f ? sum / weight : p;
			total += (result.Positions[v] - p).Length();
		}

		result.Moved = result.Positions.Length > 0 ? (float)(total / result.Positions.Length) : 0;
		return result;
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
				sum += (Quat.Rotate( turn[bone], fitted[v] - stockPositions[bone] ) + RestPositions[bone]) * w;
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
				Vec3 joint = stock.BonePositions[bone];
				sum += (joint + (fitted[v] - joint) * BoneScale[bone]) * w;
				weight += w;
			}

			result[v] = weight > 1e-6f ? sum / weight : fitted[v];
		}

		return result;
	}
}
