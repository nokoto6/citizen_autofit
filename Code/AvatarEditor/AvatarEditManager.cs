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
	const string DyeCookie = "fitavatar.dyes";

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
		ReadDyes( Game.Cookies.GetString( DyeCookie, "" ) );
		lastSavedDyes = WriteDyes();
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

	/// <summary>The human skins made for the body shown: the male ones on the male, the female ones on the female.</summary>
	public IEnumerable<Clothing> HumanSkins()
	{
		string model = Bodies.FirstOrDefault( x => x.Kind == body ).Model ?? "";
		return allClothing.Where( x => x.HasHumanSkin && string.Equals( (x.HumanSkinModel ?? "").TrimStart( '/' ), model, StringComparison.OrdinalIgnoreCase ) );
	}

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

	// The stock editor's eye sliders: a place on the stock eye palette and how the irises are
	// turned. The citizen's eyes read them.
	public float IrisColor
	{
		get => Container.EyeColor;
		set { Container.EyeColor = value; if ( Shown is { } d ) d.IrisColor = Container.EyeColor; }
	}

	public float IrisAlign
	{
		get => Container.EyeAlign;
		set { Container.EyeAlign = value; if ( Shown is { } d ) d.IrisAlign = Container.EyeAlign; }
	}

	// The stock editor's body deforms, on the citizen (see FitDresser.Deforms).
	public float NeckSize { get => Container.NeckSize; set { Container.NeckSize = value; ApplyDeforms(); } }
	public float WaistSize { get => Container.WaistSize; set { Container.WaistSize = value; ApplyDeforms(); } }
	public float ChestSize { get => Container.ChestSize; set { Container.ChestSize = value; ApplyDeforms(); } }
	public float HeadShape { get => Container.HeadShape; set { Container.HeadShape = value; ApplyDeforms(); } }
	public float NoseSize { get => Container.NoseSize; set { Container.NoseSize = value; ApplyDeforms(); } }
	public float ChinSize { get => Container.ChinSize; set { Container.ChinSize = value; ApplyDeforms(); } }

	void ApplyDeforms()
	{
		if ( Shown is not { } d ) return;
		d.NeckSize = Container.NeckSize;
		d.WaistSize = Container.WaistSize;
		d.ChestSize = Container.ChestSize;
		d.HeadShape = Container.HeadShape;
		d.NoseSize = Container.NoseSize;
		d.ChinSize = Container.ChinSize;
		d.UpdateDeforms();
	}

	/// <summary>
	/// Hair dyes, as in Protect The House's barber: a colour laid over the hair's shade. Natural
	/// is the hair's own blonde to black range.
	/// </summary>
	public static readonly (string Name, Color Color)[] DyePalette =
	[
		("Snow", Color.FromBytes( 0xF2, 0xF2, 0xF0 )),
		("Ash", Color.FromBytes( 0x9A, 0xA0, 0xA8 )),
		("Ruby", Color.FromBytes( 0xE2, 0x3C, 0x4E )),
		("Ember", Color.FromBytes( 0xF0, 0x66, 0x2C )),
		("Gold", Color.FromBytes( 0xF2, 0xC1, 0x4E )),
		("Lime", Color.FromBytes( 0x8F, 0xD1, 0x4F )),
		("Emerald", Color.FromBytes( 0x2F, 0xBF, 0x6E )),
		("Teal", Color.FromBytes( 0x27, 0xC4, 0xC0 )),
		("Azure", Color.FromBytes( 0x3A, 0xA0, 0xF0 )),
		("Sapphire", Color.FromBytes( 0x3A, 0x5B, 0xF0 )),
		("Violet", Color.FromBytes( 0x8A, 0x5B, 0xF0 )),
		("Orchid", Color.FromBytes( 0xC8, 0x5B, 0xF0 )),
		("Rose", Color.FromBytes( 0xF0, 0x5B, 0xB0 )),
	];

	// The dye on each dyed garment, by its place in DyePalette.
	readonly Dictionary<Clothing, int> dyes = new();
	string lastSavedDyes = "";

	/// <summary>Hair of any kind, which can be dyed.</summary>
	public static bool IsDyeable( Clothing c ) => c is not null && c.AllowTintSelect && (c.Category.ToString().StartsWith( "Hair" ) || c.Category.ToString().StartsWith( "FacialHair" ) || c.Category == Clothing.ClothingCategory.Eyebrows);

	/// <summary>The dye on a garment, -1 for its natural colour.</summary>
	public int GetDye( Clothing clothing ) => dyes.TryGetValue( clothing, out var i ) ? i : -1;

	public void SetDye( Clothing clothing, int dye )
	{
		if ( clothing is null ) return;
		if ( dye < 0 || dye >= DyePalette.Length ) dyes.Remove( clothing );
		else dyes[clothing] = dye;
		Shown?.SetClothingDye( clothing, dye >= 0 && dye < DyePalette.Length ? DyePalette[dye].Color : null );
	}

	/// <summary>The colour a garment is drawn in at a tint, dyed or not: what the colour strip shows.</summary>
	public Color ColorAt( Clothing clothing, float tint ) => GetDye( clothing ) is int dye && dye >= 0
		? FitDresser.Dyed( DyePalette[dye].Color, tint )
		: clothing.TintSelection.Evaluate( tint );

	string WriteDyes() => string.Join( ";", dyes.Where( x => x.Key.IsValid() ).OrderBy( x => x.Key.ResourcePath ).Select( x => $"{x.Key.ResourcePath}={DyePalette[x.Value].Name}" ) );

	void ReadDyes( string saved )
	{
		dyes.Clear();
		foreach ( var pair in (saved ?? "").Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var parts = pair.Split( '=' );
			if ( parts.Length != 2 ) continue;
			int dye = Array.FindIndex( DyePalette, x => x.Name == parts[1] );
			var clothing = ResourceLibrary.Get<Clothing>( parts[0] );
			if ( dye >= 0 && clothing.IsValid() ) dyes[clothing] = dye;
		}
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
		Container.Clothing.RemoveAll( x => x.Clothing is { } worn && (!worn.CanBeWornWith( clothing ) && !HatAndHair( worn, clothing ) || worn.HasHumanSkin && clothing.HasHumanSkin) );
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
		d.IrisColor = Container.EyeColor;
		d.IrisAlign = Container.EyeAlign;
		d.Toon = toon;
		d.Dyes.Clear();
		foreach ( var (clothing, dye) in dyes ) d.Dyes[clothing] = DyePalette[dye].Color;
		d.NeckSize = Container.NeckSize;
		d.WaistSize = Container.WaistSize;
		d.ChestSize = Container.ChestSize;
		d.HeadShape = Container.HeadShape;
		d.NoseSize = Container.NoseSize;
		d.ChinSize = Container.ChinSize;
		d.Apply();
	}

	/// <summary>Changes whenever anything worn or its colour changes, for the panels to redraw.</summary>
	public int OutfitHash
	{
		get
		{
			var hash = new HashCode();
			foreach ( var x in Container.Clothing ) { hash.Add( x.Clothing ); hash.Add( x.Tint ); hash.Add( GetDye( x.Clothing ) ); }
			return hash.ToHashCode();
		}
	}

	public bool HasUnsavedChanges => lastSaved != Container.Serialize() || lastSavedDyes != WriteDyes();

	public void SaveChanges()
	{
		lastSaved = Container.Serialize();
		lastSavedDyes = WriteDyes();
		Game.Cookies.SetString( SaveCookie, lastSaved );
		Game.Cookies.SetString( DyeCookie, lastSavedDyes );
	}

	public void RevertChanges()
	{
		Container.Deserialize( lastSaved );
		ReadDyes( lastSavedDyes );
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
