using System;
using System.Linq;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// The stock Dresser's body deforms (neck, waist, chest, head, nose, chin): the volumes of
/// models/citizen/citizen_deforms.prefab, set by the same sliders over the same ranges. They
/// change the body and, through Include Bone Merged, everything it wears with it.
///
/// The stock Dresser has them on the citizen only. Here any body gets them: on another body
/// the volumes are carried onto it (see ClothingFitter.PlaceDeformsAsync), and the sliders
/// move it from its own shape: the stock defaults are the citizen's look (a slimmer neck, a
/// fuller chest), which another body already has a look of its own instead of.
/// </summary>
public sealed partial class FitDresser
{
	const string DeformsPrefab = "models/citizen/citizen_deforms.prefab";
	const string DeformsName = "citizen_deforms";
	const string CitizenModel = "models/citizen/citizen.vmdl";

	// The stock Dresser's ranges: each volume's weight at 0 and at 1 on its slider, and the
	// slider's default.
	static readonly (string Name, float Min, float Max, float Default)[] DeformRanges =
	[
		("deform_neck", 0.5f, -0.2f, 0f),
		("deform_waist", -0.6f, 0.6f, 0.5f),
		("deform_chest", 0f, 0.8f, 0.5f),
		("deform_head", -0.4f, 0.4f, 0.5f),
		("deform_nose", 0f, 0.8f, 0f),
		("deform_chin", 0f, 1f, 0f),
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
	// The body the volumes were laid out for, and whether that is done (another body's
	// placement is worked out first, and the volumes stay off until then).
	Model deformsFor;
	bool deformsPlaced;
	int deformJob;

	static bool IsCitizen( Model model ) => string.Equals( model?.ResourcePath, CitizenModel, StringComparison.OrdinalIgnoreCase );

	/// <summary>Sets the deform volumes' weights from the sliders, making and placing the volumes first if the body doesn't have them yet.</summary>
	public void UpdateDeforms()
	{
		if ( !BodyTarget.IsValid() || BodyTarget.Model is null ) return;

		var body = BodyTarget.Model;
		if ( deformRoot.IsValid() && deformsFor != body )
		{
			// Another body: the volumes were laid out for the one before.
			deformRoot.Destroy();
			deformRoot = null;
		}

		if ( !deformRoot.IsValid() )
		{
			deformsPlaced = false;
			deformsFor = body;
			_ = MakeDeforms( ++deformJob );
			return;
		}

		if ( !deformsPlaced ) return;

		bool citizen = IsCitizen( body );
		foreach ( var deformer in deformRoot.Components.GetAll<ModelDeformer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			string name = deformer.GameObject.Name;
			int i = Array.FindIndex( DeformRanges, x => x.Name == name );
			if ( i < 0 ) continue;
			var (_, min, max, start) = DeformRanges[i];
			float weight = DeformValue( name ).Remap( 0, 1, min, max, false );
			// Another body is as built at the defaults.
			if ( !citizen ) weight -= start.Remap( 0, 1, min, max, false );
			deformer.Weight = weight;
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

		var body = BodyTarget.Model;
		if ( !IsCitizen( body ) && !await PlaceDeforms( root, body ) )
			return;
		if ( job != deformJob || !root.IsValid() ) return;

		deformsPlaced = true;
		UpdateDeforms();
	}

	// Moves each volume of the citizen's onto this body: its centre, which way it faces and how
	// far it reaches along each of its axes.
	async Task<bool> PlaceDeforms( GameObject root, Model body )
	{
		var deformers = root.Components.GetAll<ModelDeformer>( FindMode.EverythingInSelfAndDescendants ).ToArray();
		var volumes = deformers.Select( d =>
		{
			var t = d.GameObject.LocalTransform;
			var sphere = d.SceneVolume.Sphere;
			var r = sphere.Radius;
			return (d.GameObject.Name, t.PointToWorld( sphere.Center ),
				t.Rotation * new Vector3( t.Scale.x * r, 0, 0 ),
				t.Rotation * new Vector3( 0, t.Scale.y * r, 0 ),
				t.Rotation * new Vector3( 0, 0, t.Scale.z * r ));
		} ).ToArray();

		var placed = await ClothingFitter.PlaceDeformsAsync( body, volumes );
		if ( placed is null || !root.IsValid() ) return false;

		for ( int i = 0; i < deformers.Length; i++ )
		{
			var go = deformers[i].GameObject;
			if ( placed[i] is not { } p )
			{
				// Nowhere to put it on this body: it does nothing rather than the wrong thing.
				deformers[i].Enabled = false;
				continue;
			}

			float r = deformers[i].SceneVolume.Sphere.Radius;
			var forward = p.X.Normal;
			var up = (p.Z - forward * p.Z.Dot( forward )).Normal;
			var rotation = Rotation.LookAt( forward, up );
			var scale = new Vector3( p.X.Length, p.Y.Length, p.Z.Length ) / MathF.Max( r, 1e-3f );
			// The sphere's own centre is in its object's space, which is now turned and scaled.
			var centre = deformers[i].SceneVolume.Sphere.Center;
			go.LocalTransform = new Transform( p.Centre - rotation * (centre * scale), rotation, scale );
		}
		return true;
	}

	void ReleaseDeforms()
	{
		deformJob++;
		if ( deformRoot.IsValid() ) deformRoot.Destroy();
		deformRoot = null;
		deformsFor = null;
		deformsPlaced = false;
	}
}
