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

		// Per bone: rotation, scale and offset that take a point from the body's rest pose to
		// the stock one.
		var turn = new float[bones][];
		var shrink = new float[bones];
		var offset = new Vec3[bones];
		result.BoneScale = new float[stock.BoneNames.Length];
		Array.Fill( result.BoneScale, 1f );
		for ( int i = 0; i < bones; i++ )
		{
			if ( to[i] < 0 ) continue;
			turn[i] = Quat.Multiply( stock.BoneRotations[to[i]], Quat.Inverse( body.BoneRotations[from[i]] ) );
			shrink[i] = 1f / scale[from[i]];
			offset[i] = stock.BonePositions[to[i]] - Quat.Rotate( turn[i], body.BonePositions[from[i]] ) * shrink[i];
			if ( from[i] == i ) result.BoneScale[to[i]] = scale[i];
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
