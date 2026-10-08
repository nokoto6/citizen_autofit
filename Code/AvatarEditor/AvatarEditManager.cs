using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox;
using static Sandbox.ClothingContainer;

namespace FitAvatar;

/// <summary>
/// s&amp;box's avatar editor (game/addons/menu/Code/AvatarEditor, MIT, Facepunch), brought into a
/// game: the same screens and scene, with FitDresser doing the dressing. The editor picks one of
/// four bodies (the citizen, the two humans and Aurora, whose clothing is refitted to her), and
/// has the toon look as a switch. What the menu saves to the player's avatar is kept in a cookie
/// here, as a game can't change the avatar.
/// </summary>
public sealed partial class AvatarEditManager : Component
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

	const string SaveCookie = "fitavatar.outfit";

	string lastSaved;
	public ClothingContainer Container { get; set; } = new();
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
			Container.PrefersHuman = IsHuman;
			ShowBody();
		}
	}

	public bool IsHuman => body is BodyKind.HumanMale or BodyKind.HumanFemale;

	/// <summary>The citizen and Aurora are toned and aged by the skin shader; the humans wear a skin instead.</summary>
	public bool CitizenActive => !IsHuman;

	public bool Toon
	{
		get => toon;
		set { toon = value; if ( Shown is { } d ) d.Toon = value; }
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

	List<Clothing> allClothing = new();

	protected override void OnAwake()
	{
		// Every garment there is: the stock ones and whatever cloud clothing the scene has mounted.
		allClothing = ResourceLibrary.GetAll<Clothing>().Where( x => x.IsValid() ).OrderBy( x => x.Title ).ToList();

		var saved = Game.Cookies.GetString( SaveCookie, null );
		Container = string.IsNullOrEmpty( saved ) ? ClothingContainer.CreateFromLocalUser() ?? new() : ClothingContainer.CreateFromJson( saved );
		lastSaved = Container.Serialize();
		AvatarBackgroundRig.RestoreSaved();
	}

	protected override void OnStart()
	{
		body = StartBody;
		toon = StartToon;
		Container.PrefersHuman = IsHuman;
		foreach ( var (kind, title, model, version) in Bodies )
		{
			// At the scene's middle, where the backdrops are built round the body.
			var go = new GameObject( false, title );
			go.Flags |= GameObjectFlags.NotSaved;
			go.WorldPosition = Vector3.Zero;
			go.WorldRotation = Rotation.Identity;
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
		ApplyChangesToModel();
	}

	public IEnumerable<Clothing> GetAllClothing() => allClothing;

	protected override void OnUpdate()
	{
		if ( Shown?.BodyTarget is not { } renderer || Scene.Camera is null ) return;
		UpdateEyes( renderer );
		UpdateCamera( renderer );
	}

	public bool IsSelected( Clothing clothing ) => Container.Has( clothing );

	public bool IsPurchased( Clothing item ) => true;

	public string DisplayName
	{
		get => Container.DisplayName;
		set => Container.DisplayName = value;
	}

	public float Height
	{
		get => Container.Height;
		set { Container.Height = value; if ( Shown is { } d ) d.Height = value; }
	}

	public float Age
	{
		get => Container.Age;
		set { Container.Age = value; if ( Shown is { } d ) d.Age = value; }
	}

	public float Tint
	{
		get => Container.Tint;
		set { Container.Tint = value; if ( Shown is { } d ) d.Tint = value; }
	}

	/// <summary>
	/// A workshop item, put on. The menu only previews one; refitting one to Aurora takes a few
	/// seconds, so here a click puts it on and a second click takes it off.
	/// </summary>
	public async Task<Clothing> WearPackageAsync( Package package )
	{
		var clothing = await Cloud.Load<Clothing>( package.FullIdent );
		if ( !clothing.IsValid() ) return null;
		if ( !allClothing.Contains( clothing ) ) allClothing.Add( clothing );
		OnClothingToggle( clothing );
		return clothing;
	}

	// Hovering an item doesn't try it on: on Aurora every garment is refitted, which takes
	// seconds, and a preview on every icon the mouse crosses would queue up dozens of fits.
	public void OnClothingHover( Clothing clothing )
	{
	}

	public void SetTint( Clothing clothing, float f )
	{
		ClothingEntry e = Container.FindEntry( clothing );
		if ( e is null ) return;

		e.Tint = f;
		// Only the colour changes: recoloured in place, not dressed anew.
		Shown?.SetClothingTint( clothing, f );
	}

	public float GetTint( Clothing clothing )
	{
		ClothingEntry e = Container.FindEntry( clothing );
		if ( e is not null && e.Tint.HasValue )
			return e.Tint.Value;

		return clothing.TintDefault;
	}

	public void OnClothingToggle( Clothing clothing )
	{
		if ( Container.Has( clothing ) ) Container.Clothing.RemoveAll( x => x.Clothing == clothing );
		else Wear( clothing );
		ApplyChangesToModel();
	}

	/// <summary>
	/// Puts a garment on and takes off what it can't be worn with, as the container's Add does,
	/// except that a hat and hair stay together: their slots overlap, but FitDresser lays the
	/// hair under the hat.
	/// </summary>
	ClothingEntry Wear( Clothing clothing )
	{
		Container.Clothing.RemoveAll( x => x.Clothing is { } worn && !worn.CanBeWornWith( clothing ) && !HatAndHair( worn, clothing ) );
		var entry = new ClothingEntry( clothing );
		Container.Clothing.Add( entry );
		return entry;
	}

	static bool IsHat( Clothing c ) => c.Category.ToString().StartsWith( "Hat" ) || c.Category == Clothing.ClothingCategory.Headwear;
	static bool IsHair( Clothing c ) => c.Category.ToString().StartsWith( "Hair" );
	static bool HatAndHair( Clothing a, Clothing b ) => (IsHat( a ) && IsHair( b )) || (IsHat( b ) && IsHair( a ));

	/// <summary>Puts the outfit and the look on the body shown.</summary>
	public void ApplyChangesToModel()
	{
		if ( Shown is not { } d ) return;
		d.Clothing = Container.Clothing.Where( x => x.Clothing.IsValid() ).Select( x => new ClothingEntry( x.Clothing ) { Tint = x.Tint } ).ToList();
		d.Height = Container.Height;
		d.Age = Container.Age;
		d.Tint = Container.Tint;
		d.TintEyes = tintEyes;
		d.EyeColor = eyeColor;
		d.Toon = toon;
		d.Apply();
	}

	/// <summary>Changes whenever anything worn or its colour changes, for the panels to redraw.</summary>
	public int OutfitHash
	{
		get
		{
			var hash = new HashCode();
			foreach ( var x in Container.Clothing ) { hash.Add( x.Clothing ); hash.Add( x.Tint ); }
			return hash.ToHashCode();
		}
	}

	public bool HasUnsavedChanges => lastSaved != Container.Serialize();

	public void SaveChanges()
	{
		lastSaved = Container.Serialize();
		Game.Cookies.SetString( SaveCookie, lastSaved );
	}

	public void RevertChanges()
	{
		Container.Deserialize( lastSaved );
		ApplyChangesToModel();
	}

	public void Clear()
	{
		Container.Clothing.Clear();
		ApplyChangesToModel();
	}

	public void Randomize()
	{
		if ( Shown is not { } d ) return;
		d.Randomize();
		Container.Clothing.Clear();
		foreach ( var entry in d.Clothing )
			if ( entry.Clothing.IsValid() ) Wear( entry.Clothing ).Tint = entry.Tint;
	}
}

public struct ColorSwatch
{
	public float Value;
	public Color Color;
}
