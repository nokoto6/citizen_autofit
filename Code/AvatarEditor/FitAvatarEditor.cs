using System;
using System.Collections.Generic;
using System.Linq;

namespace Sandbox;

/// <summary>
/// A character editor like s&amp;box's own avatar editor (game/addons/menu/Code/AvatarEditor,
/// MIT, Facepunch), on FitDresser: pick a body (the citizen, the two humans or Aurora), put
/// clothing on it from a grid of icons, set height, age, skin tone and eye colour, and switch
/// the toon look on and off. The bodies are made here when the scene starts; only the chosen
/// one is shown, and it wears the outfit as it stands.
/// </summary>
public sealed class FitAvatarEditor : Component
{
	public enum BodyKind
	{
		Citizen,
		HumanMale,
		HumanFemale,
		Aurora,
	}

	static readonly (BodyKind Kind, string Title, string Model, FitDresser.ClothingVersion Version)[] Bodies =
	[
		(BodyKind.Citizen, "Citizen", "models/citizen/citizen.vmdl", FitDresser.ClothingVersion.Citizen),
		(BodyKind.HumanMale, "Human", "models/citizen_human/citizen_human_male.vmdl", FitDresser.ClothingVersion.HumanMale),
		(BodyKind.HumanFemale, "Human F", "models/citizen_human/citizen_human_female.vmdl", FitDresser.ClothingVersion.HumanFemale),
		(BodyKind.Aurora, "Aurora", "models/aurora/aurora.vmdl", FitDresser.ClothingVersion.Auto),
	];

	public static IEnumerable<(BodyKind Kind, string Title)> BodyChoices => Bodies.Select( x => (x.Kind, x.Title) );

	/// <summary>The body shown when the scene starts.</summary>
	[Property] public BodyKind StartBody { get; set; } = BodyKind.Aurora;

	/// <summary>Start with the toon look on.</summary>
	[Property] public bool StartToon { get; set; } = true;

	public ClothingContainer Outfit { get; private set; } = new();

	readonly Dictionary<BodyKind, FitDresser> dressers = new();
	BodyKind body;
	bool toon, tintEyes;
	Color eyeColor = new( 0.34f, 0.21f, 0.11f );

	public BodyKind Body
	{
		get => body;
		set
		{
			if ( body == value ) return;
			body = value;
			ShowBody();
		}
	}

	public bool IsHuman => body is BodyKind.HumanMale or BodyKind.HumanFemale;

	/// <summary>The citizen and Aurora are toned and aged by the skin shader; the humans wear a skin instead.</summary>
	public bool HasSkinTone => !IsHuman;

	public bool Toon
	{
		get => toon;
		set { toon = value; if ( Shown is { } d ) d.Toon = value; }
	}

	public float Height
	{
		get => Outfit.Height;
		set { Outfit.Height = value; if ( Shown is { } d ) d.Height = value; }
	}

	public float Age
	{
		get => Outfit.Age;
		set { Outfit.Age = value; if ( Shown is { } d ) d.Age = value; }
	}

	public float Tint
	{
		get => Outfit.Tint;
		set { Outfit.Tint = value; if ( Shown is { } d ) d.Tint = value; }
	}

	public bool TintEyes
	{
		get => tintEyes;
		set { tintEyes = value; if ( Shown is { } d ) d.TintEyes = value; }
	}

	public Color EyeColor
	{
		get => eyeColor;
		set { eyeColor = value; tintEyes = true; if ( Shown is { } d ) { d.EyeColor = value; d.TintEyes = true; } }
	}

	public FitDresser Shown => dressers.GetValueOrDefault( body );
	public SkinnedModelRenderer ActiveRenderer => Shown?.BodyTarget;

	List<Clothing> allClothing;

	/// <summary>Every garment there is: the stock ones and whatever cloud clothing the scene has mounted.</summary>
	public IReadOnlyList<Clothing> AllClothing => allClothing ??= ResourceLibrary.GetAll<Clothing>()
		.Where( x => x.IsValid() )
		.OrderBy( x => x.Title )
		.ToList();

	protected override void OnStart()
	{
		body = StartBody;
		toon = StartToon;
		// Start from what the player's own avatar wears, as the avatar editor does.
		Outfit = ClothingContainer.CreateFromLocalUser() ?? new ClothingContainer();
		foreach ( var (kind, title, model, version) in Bodies )
		{
			var go = new GameObject( GameObject, false, title );
			go.Flags |= GameObjectFlags.NotSaved;
			var renderer = go.AddComponent<SkinnedModelRenderer>();
			renderer.Model = Model.Load( model );
			renderer.UseAnimGraph = true;
			var dresser = go.AddComponent<FitDresser>();
			dresser.BodyTarget = renderer;
			dresser.ApplyOnStart = false;
			dresser.Version = version;
			// The stock bodies wear their clothing as made; only Aurora needs it refitted.
			dresser.FitAtRuntime = kind == BodyKind.Aurora;
			dresser.RemoveSkinFromClothing = kind == BodyKind.Aurora;
			dressers[kind] = dresser;
		}
		ShowBody();
	}

	void ShowBody()
	{
		foreach ( var (kind, dresser) in dressers )
			dresser.GameObject.Enabled = kind == body;
		Apply();
	}

	/// <summary>Puts the outfit and the look on the body shown.</summary>
	public void Apply()
	{
		if ( Shown is not { } d ) return;
		d.Clothing = Outfit.Clothing.Select( x => new ClothingContainer.ClothingEntry( x.Clothing ) { Tint = x.Tint } ).ToList();
		d.Height = Outfit.Height;
		d.Age = Outfit.Age;
		d.Tint = Outfit.Tint;
		d.TintEyes = tintEyes;
		d.EyeColor = eyeColor;
		d.Toon = toon;
		d.Apply();
	}

	public bool IsSelected( Clothing clothing ) => Outfit.Has( clothing );

	public void Toggle( Clothing clothing )
	{
		Outfit.Toggle( clothing );
		Apply();
	}

	public float GetTint( Clothing clothing ) => Outfit.FindEntry( clothing )?.Tint ?? clothing.TintDefault;

	public void SetTint( Clothing clothing, float tint )
	{
		if ( Outfit.FindEntry( clothing ) is not { } entry ) return;
		entry.Tint = tint;
		Apply();
	}

	public void Clear()
	{
		Outfit.Clothing.Clear();
		Apply();
	}

	public void Randomize()
	{
		if ( Shown is not { } d ) return;
		d.Randomize();
		Outfit.Clothing.Clear();
		foreach ( var entry in d.Clothing )
			if ( entry.Clothing.IsValid() ) Outfit.Add( entry.Clothing ).Tint = entry.Tint;
	}

	protected override void OnUpdate()
	{
		if ( ActiveRenderer is not { } renderer || Scene.Camera is null ) return;
		UpdateEyes( renderer );
		UpdateCamera();
	}

	// The camera and the eyes, as the avatar editor has them: left drag turns round the body,
	// right drag slides the view, the wheel zooms in on the point under the mouse; the body
	// looks at the mouse.
	float center = 35f, distance = 200f, zoomVelocity, sideways;
	Angles angle = new( 0, 180, 0 );
	Vector2 lastMouse;

	void UpdateEyes( SkinnedModelRenderer renderer )
	{
		var head = renderer.GetAttachment( "eyes" ) ?? renderer.WorldTransform;
		var ray = Scene.Camera.ScreenPixelToRay( Mouse.Position );
		var eyes = head.Position;
		var target = ray.Project( (eyes + renderer.WorldRotation.Forward * 20f).Distance( ray.Position ) );
		if ( distance < 100 ) target = eyes + new Vector3( 1000, -500, 0 );
		renderer.SetLookDirection( "aim_eyes", target - eyes, 1 );
		renderer.SetLookDirection( "aim_body", target - eyes, 0.5f );
		renderer.SetLookDirection( "aim_head", target - eyes, 0.8f );
	}

	void UpdateCamera()
	{
		var camera = Scene.Camera;
		bool turning = false, moved = false;
		if ( Input.Down( "attack1" ) )
		{
			angle.yaw += Mouse.Delta.x * 0.4f;
			angle.pitch -= Mouse.Delta.y * 0.2f;
			turning = true;
		}
		angle.pitch = angle.pitch.Clamp( -70, 70 );
		angle.roll = 0;

		var mouse = Mouse.Position;
		var before = camera.ScreenPixelToRay( lastMouse );
		lastMouse = mouse;
		if ( Input.Down( "attack2" ) )
		{
			moved = Mouse.Delta != 0f;
			sideways = (sideways + Mouse.Delta.x).Clamp( -1000, 1000 );
		}
		if ( Input.MouseWheel.y != 0 )
		{
			zoomVelocity += Input.MouseWheel.y * -30f;
			moved = true;
		}
		distance = (distance + zoomVelocity * Time.Delta).Clamp( 0, 200 );
		zoomVelocity = zoomVelocity.LerpTo( 0, Time.Delta * 3f );

		var side = sideways * angle.ToRotation().Left * 0.05f;
		camera.WorldRotation = angle;
		camera.FieldOfView = float.Lerp( 6, 50, distance.Remap( 0, 150 ) );
		camera.WorldPosition = Vector3.Up * center + angle.Forward * -distance.Clamp( 50, 200 ) + side;

		if ( moved && !turning )
		{
			var after = camera.ScreenPixelToRay( mouse );
			var plane = new Plane( 0, angle.Forward * -1 );
			if ( plane.Trace( before ) - plane.Trace( after ) is { } delta )
				center = (center + delta.z).Clamp( 0, 70 );
			camera.WorldPosition = Vector3.Up * center + angle.Forward * -distance.Clamp( 50, 200 ) + side;
		}
	}
}
