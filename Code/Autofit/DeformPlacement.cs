using System;

namespace Sandbox.Autofit;

/// <summary>
/// The stock citizen's deform volumes (models/citizen/citizen_deforms.prefab: the avatar
/// editor's neck, waist, chest, head, nose and chin sliders), carried onto another body.
///
/// A volume is a centre and three axes, on the citizen in its model space. Each goes with one
/// bone, the way a garment vertex would, so it lands on the same part of the other body at
/// that body's size there. The nose and the chin also go with the face's own landmarks (a
/// head bone tells where the head is, not where on it the nose sticks out), and the neck with
/// the chin above it.
/// </summary>
public static class DeformPlacement
{
	/// <summary>The bone each volume goes with, and the <see cref="Repose.NoseAndChin"/> landmark that moves it (-1 for none).</summary>
	public static (string Bone, int Face) Anchor( string volume ) => volume switch
	{
		"deform_neck" => ("neck_0", 1),
		"deform_waist" => ("spine_0", -1),
		"deform_chest" => ("spine_2", -1),
		"deform_head" => ("head", -1),
		"deform_nose" => ("head", 0),
		"deform_chin" => ("head", 1),
		_ => ("head", -1),
	};

	/// <summary>A volume's centre and axes on the body, from the same on the citizen. False if the body has nothing to place it by.</summary>
	public static bool Place( Repose pose, SkinnedGeometry stock, string volume, ref Vec3 centre, ref Vec3 x, ref Vec3 y, ref Vec3 z )
	{
		var (boneName, face) = Anchor( volume );
		int bone = Array.IndexOf( stock.BoneNames, boneName );
		if ( bone < 0 || !pose.BodyHas[bone] ) return false;

		var shift = Vec3.Zero;
		bool neck = volume == "deform_neck";
		if ( face >= 0 && face < pose.NoseAndChin.Count && !neck )
			shift = pose.NoseAndChin[face].Body - pose.NoseAndChin[face].Stock;
		Vec3 from = centre + shift;
		Vec3 at = pose.StockToBodyRest( from, bone, stock );
		x = pose.StockToBodyRest( from + x, bone, stock ) - at;
		y = pose.StockToBodyRest( from + y, bone, stock ) - at;
		z = pose.StockToBodyRest( from + z, bone, stock ) - at;
		// The neck is as far up from its joint to the chin as it is on the citizen: a longer
		// neck under a smaller head has its middle elsewhere than the joint alone says.
		if ( neck && face < pose.NoseAndChin.Count )
		{
			var (ours, theirs) = pose.NoseAndChin[face];
			Vec3 joint = stock.BonePositions[bone];
			float along = Math.Clamp( (centre.Z - joint.Z) / MathF.Max( ours.Z - joint.Z, 1e-3f ), 0f, 1f );
			int head = Array.IndexOf( stock.BoneNames, "head" );
			Vec3 jointOnBody = pose.StockToBodyRest( joint, bone, stock );
			Vec3 chinOnBody = head >= 0 ? pose.StockToBodyRest( theirs, head, stock ) : at;
			at = new Vec3( at.X, at.Y, jointOnBody.Z + (chinOnBody.Z - jointOnBody.Z) * along );
		}
		centre = at;
		return true;
	}
}
