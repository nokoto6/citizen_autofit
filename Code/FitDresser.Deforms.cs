using System;
using System.Linq;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// The stock Dresser's body deforms (neck, waist, chest, head, nose, chin): the volumes of
/// models/citizen/citizen_deforms.prefab, set by the same sliders over the same ranges. They
/// change the body and, through Include Bone Merged, everything it wears with it. Like the
/// stock Dresser's, on the citizen only: the volumes are shaped for its body.
/// </summary>
public sealed partial class FitDresser
{
	const string DeformsPrefab = "models/citizen/citizen_deforms.prefab";
	const string DeformsName = "citizen_deforms";
	const string CitizenModel = "models/citizen/citizen.vmdl";

	// The stock Dresser's ranges: each volume's weight at 0 and at 1 on its slider.
	static readonly (string Name, float Min, float Max)[] DeformRanges =
	[
		("deform_neck", 0.5f, -0.2f),
		("deform_waist", -0.6f, 0.6f),
		("deform_chest", 0f, 0.8f),
		("deform_head", -0.4f, 0.4f),
		("deform_nose", 0f, 0.8f),
		("deform_chin", 0f, 1f),
	];

	[Property, Range( 0, 1 ), Group( "Deforms" ), Change( nameof( OnDeformChanged ) )] public float NeckSize { get; set; } = 0f;
	[Property, Range( 0, 1 ), Group( "Deforms" ), Change( nameof( OnDeformChanged ) )] public float WaistSize { get; set; } = 0.5f;
	[Property, Range( 0, 1 ), Group( "Deforms" ), Change( nameof( OnDeformChanged ) )] public float ChestSize { get; set; } = 0.5f;
	[Property, Range( 0, 1 ), Group( "Deforms" ), Change( nameof( OnDeformChanged ) )] public float HeadShape { get; set; } = 0.5f;
	[Property, Range( 0, 1 ), Group( "Deforms" ), Change( nameof( OnDeformChanged ) )] public float NoseSize { get; set; } = 0f;
	[Property, Range( 0, 1 ), Group( "Deforms" ), Change( nameof( OnDeformChanged ) )] public float ChinSize { get; set; } = 0f;

	void OnDeformChanged( float before, float after ) => UpdateDeforms();

	float DeformValue( string name ) => name switch
	{
		"deform_neck" => NeckSize,
		"deform_waist" => WaistSize,
		"deform_chest" => ChestSize,
		"deform_head" => HeadShape,
		"deform_nose" => NoseSize,
		"deform_chin" => ChinSize,
		_ => 0f,
	};

	GameObject deformRoot;
	int deformJob;

	static bool IsCitizen( Model model ) => string.Equals( model?.ResourcePath, CitizenModel, StringComparison.OrdinalIgnoreCase );

	/// <summary>Sets the deform volumes' weights from the sliders, making the volumes first if the body doesn't have them yet.</summary>
	public void UpdateDeforms()
	{
		if ( !BodyTarget.IsValid() ) return;
		if ( !IsCitizen( BodyTarget.Model ) )
		{
			ReleaseDeforms();
			return;
		}

		if ( !deformRoot.IsValid() )
		{
			_ = MakeDeforms( ++deformJob );
			return;
		}

		foreach ( var deformer in deformRoot.Components.GetAll<ModelDeformer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			string name = deformer.GameObject.Name;
			int i = Array.FindIndex( DeformRanges, x => x.Name == name );
			if ( i < 0 ) continue;
			var (_, min, max) = DeformRanges[i];
			deformer.Weight = DeformValue( name ).Remap( 0, 1, min, max, false );
		}
		deformRoot.Enabled = true;
	}

	async Task MakeDeforms( int job )
	{
		var prefab = ResourceLibrary.Get<PrefabFile>( DeformsPrefab ) ?? await ResourceLibrary.LoadAsync<PrefabFile>( DeformsPrefab );
		if ( job != deformJob || !this.IsValid() || !BodyTarget.IsValid() ) return;
		if ( prefab is null )
		{
			Log.Warning( $"FitDresser: {DeformsPrefab} isn't there, so the body has no deforms" );
			return;
		}

		using var sceneScope = BodyTarget.Scene.Push();
		// One left from before a hotload, which forgets which object was ours.
		foreach ( var old in BodyTarget.GameObject.Children.Where( x => x.Name == DeformsName ).ToArray() )
			old.Destroy();
		var root = GameObject.Clone( prefab, new CloneConfig { Parent = BodyTarget.GameObject, Transform = global::Transform.Zero, StartEnabled = false } );
		root.Name = DeformsName;
		root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		root.NetworkMode = NetworkMode.Never;
		deformRoot = root;

		UpdateDeforms();
	}

	void ReleaseDeforms()
	{
		deformJob++;
		if ( deformRoot.IsValid() ) deformRoot.Destroy();
		deformRoot = null;
		// And one a hotload left without an owner.
		if ( BodyTarget.IsValid() )
			foreach ( var old in BodyTarget.GameObject.Children.Where( x => x.Name == DeformsName ).ToArray() )
				old.Destroy();
	}
}
