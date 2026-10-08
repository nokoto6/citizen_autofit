using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static Sandbox.Clothing;

namespace Sandbox;

/// <summary>
/// Dresses a citizen whose body has been edited.
///
/// Stock clothing is modelled on the stock citizen, so it clips through a body with a different
/// shape. This component dresses the body like <see cref="Dresser"/> does, but every garment is
/// refitted to the body first (see <see cref="ClothingFitter"/>). It works for any model on the
/// citizen skeleton and any citizen clothing, including items downloaded at runtime.
///
/// The fitting is done on worker threads, so nothing stalls. A garment that has been worn on
/// this body before is on right away. A new one shows up a moment later in its roughest LOD,
/// which is quick to fit, and sharpens as the more detailed LODs come in.
///
/// The stock ClothingContainer.Apply can't be used here: it treats any model that isn't
/// citizen.vmdl as a human and throws away clothing that has no human variant.
/// </summary>
[Title( "Fit Dresser" ), Category( "Game" ), Icon( "checkroom" )]
public sealed class FitDresser : Component, Component.ExecuteInEditor
{
	/// <summary>The body to dress.</summary>
	[Property] public SkinnedModelRenderer BodyTarget { get; set; }

	/// <summary>Wear what the local player's avatar wears instead of the list below.</summary>
	[Property] public bool UseLocalAvatar { get; set; }

	[Property, HideIf( nameof( UseLocalAvatar ), true )]
	public List<ClothingContainer.ClothingEntry> Clothing { get; set; } = [];

	/// <summary>Refit garments to the body when they are put on. Off: everything is worn as is, for comparison.</summary>
	[Property] public bool FitAtRuntime { get; set; } = true;

	public enum ClothingVersion
	{
		/// <summary>Whichever stock skeleton the body's is closest to.</summary>
		Auto,
		Citizen,
		HumanMale,
		HumanFemale,
	}

	/// <summary>
	/// Which version of each garment to wear. Auto goes by the skeleton, which can tell a
	/// citizen from a human but is a coin toss between the two humans. Set it by hand if it
	/// guesses wrong.
	/// </summary>
	[Property] public ClothingVersion Version { get; set; } = ClothingVersion.Auto;

	/// <summary>
	/// How tall the character is, same as the stock Dresser's height: 0 is the shortest, 1 the
	/// tallest, 0.5 leaves it alone. The body's animation graph does the scaling (the citizen's
	/// has it), and the clothing follows the bones. With Use Local Avatar the avatar's own
	/// height is used.
	/// </summary>
	[Property, Range( 0, 1 ), HideIf( nameof( UseLocalAvatar ), true ), Change( nameof( OnHeightChanged ) )]
	public float Height { get; set; } = 0.5f;

	/// <summary>
	/// Skin tone, same as the stock Dresser's tint: 0 to 1 across the skin shader's range of
	/// tones, set on the body and on any skin a garment carries. With Use Local Avatar the
	/// avatar's own tone is used.
	/// </summary>
	[Property, Range( 0, 1 ), HideIf( nameof( UseLocalAvatar ), true ), Change( nameof( OnSkinChanged ) )]
	public float Tint { get; set; } = 0.5f;

	/// <summary>Skin age, same as the stock Dresser's: how worn the skin shader draws the skin.</summary>
	[Property, Range( 0, 1 ), HideIf( nameof( UseLocalAvatar ), true ), Change( nameof( OnSkinChanged ) )]
	public float Age { get; set; } = 0.5f;

	/// <summary>
	/// Colour the eyes with Eye Color. Goes to the renderer as the eye_tint attribute, which a
	/// body's iris material has to read (Aurora's does); the stock bodies get their eye colour
	/// from an eyes material carried by clothing instead, and ignore this.
	/// </summary>
	[Property, HideIf( nameof( UseLocalAvatar ), true ), Change( nameof( OnTintEyesChanged ) )]
	public bool TintEyes { get; set; }

	/// <summary>The iris colour, with Tint Eyes on. The default is Aurora's own brown.</summary>
	[Property, ShowIf( nameof( TintEyes ), true ), Change( nameof( OnEyeColorChanged ) )]
	public Color EyeColor { get; set; } = new Color( 0.34f, 0.21f, 0.11f );

	/// <summary>
	/// Anime look for the body and everything it wears: every material is swapped for a copy on
	/// shaders/fit_toon.shader with the same textures (flat bands of light, coloured shade, a
	/// rim of light, the original's gloss as a hard highlight and a reflection), and skin toned
	/// by Tint gets an anime skin tone. See-through materials (glass) keep their own look.
	/// </summary>
	[Property, Change( nameof( OnToonChanged ) )]
	public bool Toon { get; set; }

	/// <summary>
	/// Some clothing hides a part of the body and draws its own copy of that skin instead, as
	/// part of the garment. The copy has the stock body's shape and doesn't suit another body.
	/// With this on, such clothing is worn without its skin and the body part it meant to hide
	/// stays visible.
	/// </summary>
	[Property] public bool RemoveSkinFromClothing { get; set; }

	[Property] public bool ApplyOnStart { get; set; } = true;

	/// <summary>How many garments of the last Apply were fitted on the spot.</summary>
	public int FittedCount { get; private set; }

	/// <summary>How many garments of the last Apply were worn as they are.</summary>
	public int StockCount { get; private set; }

	const string ClothingTag = "clothing";

	protected override void OnStart()
	{
		if ( ApplyOnStart )
			Apply();
	}

	// Goes up every time the outfit is put on or taken off. An Apply that comes back from
	// waiting and finds it changed has been overtaken and stops.
	int outfit;

	[Button( "Clear Clothing" )]
	public void Clear()
	{
		outfit++;
		if ( !BodyTarget.IsValid() )
			return;

		foreach ( var child in BodyTarget.GameObject.Children.ToArray() )
		{
			if ( !child.Tags.Has( ClothingTag ) ) continue;
			foreach ( var renderer in child.Components.GetAll<SkinnedModelRenderer>() )
				ClothingFitter.Retire( renderer.Model );
			child.Destroy();
		}

		BodyTarget.BodyGroups = BodyTarget.Model?.Parts.DefaultMask ?? 0;
		BodyTarget.SetMaterialOverride( null, "skin" );
		BodyTarget.SetMaterialOverride( null, "eyes" );
	}

	/// <summary>
	/// Throws away everything fitted so far and dresses again. Use it after the body model has
	/// been re-exported.
	/// </summary>
	[Button( "Refit Clothing" )]
	public void Refit()
	{
		ClothingFitter.Clear();
		Apply();
	}

	/// <summary>
	/// Refits and re-dresses every Fit Dresser in the scene. Same as pressing Refit Clothing
	/// on each of them.
	/// </summary>
	[ConCmd( "fitdresser_refit" )]
	public static void RefitAll()
	{
		ClothingFitter.Clear();
		Live.RemoveWhere( x => !x.IsValid() );
		foreach ( var dresser in Live.ToArray() )
			dresser.Apply();
		Log.Info( $"fitdresser_refit: {Live.Count} dressers" );
	}

	/// <summary>
	/// Prints, for each dresser's body, what its toon copies were given: shader, colour texture
	/// and features of each material slot. Changes nothing.
	/// </summary>
	[ConCmd( "fitdresser_toon_info" )]
	public static void ToonInfo()
	{
		Live.RemoveWhere( x => !x.IsValid() );
		foreach ( var dresser in Live )
		{
			var body = dresser.BodyTarget;
			if ( !body.IsValid() || body.Model is null ) continue;
			Log.Info( $"fitdresser_toon_info: '{dresser.GameObject.Name}' toon {dresser.Toon}, {body.Materials.Count} slots" );
			for ( int i = 0; i < body.Materials.Count; i++ )
			{
				var original = body.Materials.GetOriginal( i );
				var copy = body.Materials.GetOverride( i );
				string Describe( Material m ) => m is null ? "none" : $"{m.Name} on {m.ShaderName}, colour {m.GetTexture( "g_tColor" )?.ResourceName ?? "missing"} {m.GetTexture( "g_tColor" )?.Width}x{m.GetTexture( "g_tColor" )?.Height}, rma {m.GetTexture( "g_tRma" )?.Width}, alpha test {m.GetFeature( "F_ALPHA_TEST" )}, morph {m.GetFeature( "F_MORPH_SUPPORTED" )}";
				Log.Info( $"  slot {i}: {Describe( original )} -> {Describe( copy )}" );
			}
		}
	}

	// Every dresser that is currently alive, in the editor scene too. A console command has no
	// way to reach the scene open in the editor, so they sign in here themselves.
	static readonly HashSet<FitDresser> Live = new();

	// Garments still fading in, and when each started.
	readonly Dictionary<SkinnedModelRenderer, float> fading = new();
	const float FadeTime = 0.3f;

	protected override void OnUpdate()
	{
		Live.Add( this );
		Stretch();
	}

	protected override void OnDestroy()
	{
		Live.Remove( this );
	}

	protected override void OnDisabled()
	{
		ScaleWhole( 1f );
		Unstretch();
	}

	// How far clothing can stick out past the body: hair, a hat, a sword on the back.
	const float ClothingReach = 16f;

	protected override void OnPreRender()
	{
		foreach ( var (renderer, started) in fading.ToArray() )
		{
			float t = (Time.Now - started) / FadeTime;
			if ( !renderer.IsValid() || t >= 1f )
			{
				if ( renderer.IsValid() ) renderer.Tint = renderer.Tint.WithAlpha( 1 );
				fading.Remove( renderer );
				continue;
			}
			renderer.Tint = renderer.Tint.WithAlpha( t );
		}

		if ( !BodyTarget.IsValid() ) return;

		// A model built at runtime has no per-bone extents, so the engine works out the bounds
		// of a bone-merged garment from its bone positions alone. That box is far too small
		// (for hair, a sliver through the neck) and the garment gets culled while it is still
		// on screen. The body's bounds are right, and the clothing is on the body.
		var bounds = BodyTarget.Bounds.Grow( ClothingReach );
		foreach ( var child in BodyTarget.GameObject.Children )
		{
			if ( !child.Tags.Has( ClothingTag ) ) continue;
			var sceneModel = child.GetComponent<SkinnedModelRenderer>()?.SceneModel;
			if ( sceneModel.IsValid() ) sceneModel.Bounds = bounds;
		}
	}

	// What a random outfit is made of: for each group, the odds of wearing something from it
	// and the categories to pick from. Same table as the stock Dresser's Randomize, which sits
	// in an internal engine class and can't be called from here.
	static readonly (float Chance, ClothingCategory[] Categories)[] RandomGroups =
	{
		(1.0f, [ClothingCategory.Hat, ClothingCategory.HatCap, ClothingCategory.Hair, ClothingCategory.HairShort, ClothingCategory.HairLong, ClothingCategory.HairMedium]),
		(1.0f, [ClothingCategory.Trousers, ClothingCategory.Shorts, ClothingCategory.Underwear, ClothingCategory.Skirt]),
		(1.0f, [ClothingCategory.Shirt, ClothingCategory.TShirt, ClothingCategory.Tops]),
		(1.0f, [ClothingCategory.Shoes, ClothingCategory.Boots]),
		(0.5f, [ClothingCategory.Jacket, ClothingCategory.Vest, ClothingCategory.Coat, ClothingCategory.Cardigan]),
		(0.3f, [ClothingCategory.FacialHairBeard, ClothingCategory.FacialHairGoatee, ClothingCategory.FacialHairSideburns]),
		(0.5f, [ClothingCategory.GlassesEye, ClothingCategory.GlassesSun]),
		(0.2f, [ClothingCategory.Gloves]),
	};

	/// <summary>
	/// Make a random outfit and put it on.
	/// </summary>
	[Button, HideIf( nameof( UseLocalAvatar ), true )]
	public void Randomize()
	{
		// Not Game.Random: this is pressed in the editor, where the game isn't ticking.
		var random = new Random();
		var all = ResourceLibrary.GetAll<Clothing>().ToList();

		Clothing.Clear();
		foreach ( var (chance, categories) in RandomGroups )
		{
			if ( random.Float() > chance ) continue;

			var category = random.FromArray( categories );
			var options = all.Where( x => x.Category == category ).ToList();
			if ( options.Count == 0 ) continue;

			var item = random.FromList( options );
			if ( !item.IsValid() ) continue;

			Clothing.Add( new ClothingContainer.ClothingEntry( item ) { Tint = random.Float() } );
		}

		Apply();
	}

	[Button( "Apply Clothing" )]
	public void Apply()
	{
		_ = ApplyAndReport();
	}

	async Task ApplyAndReport()
	{
		try
		{
			await ApplyAsync();
		}
		catch ( Exception e )
		{
			// Nobody waits for this task, so an exception would vanish without a trace.
			Log.Warning( $"FitDresser on '{GameObject?.Name}': {e.GetType().Name}: {e.Message}" );
		}
	}

	async Task ApplyAsync()
	{
		if ( !BodyTarget.IsValid() )
			return;

		Clear();
		// Fresh toon copies: ones made before a shader recompile have lost their textures.
		toonOf.Clear();
		int mine = outfit;
		bool Overtaken() => mine != outfit || !this.IsValid() || !BodyTarget.IsValid();

		var container = BuildContainer();
		ownProportions = ClothingFitter.OwnProportions( BodyTarget.Model );
		ApplyHeight( UseLocalAvatar ? container.Height : Height );
		skinTint = UseLocalAvatar ? container.Tint : Tint;
		skinAge = UseLocalAvatar ? container.Age : Age;
		ApplySkin();
		var worn = container.Clothing
			.Where( x => x.Clothing is not null )
			.ToList();

		var skin = FirstMaterial( worn.Select( x => x.Clothing.SkinMaterial ) );
		var eyes = FirstMaterial( worn.Select( x => x.Clothing.EyesMaterial ) );
		BodyTarget.SetMaterialOverride( skin, "skin" );
		BodyTarget.SetMaterialOverride( eyes, "eyes" );

		// Clothing comes in a citizen version and usually one for each human. Use the version
		// made for the skeleton closest to this body's.
		var bodyKind = Version switch
		{
			ClothingVersion.Citizen => ClothingFitter.BodyKind.Citizen,
			ClothingVersion.HumanMale => ClothingFitter.BodyKind.HumanMale,
			ClothingVersion.HumanFemale => ClothingFitter.BodyKind.HumanFemale,
			_ => FitAtRuntime ? await ClothingFitter.KindOfAsync( BodyTarget.Model ) : ClothingFitter.BodyKind.Citizen,
		};
		if ( Overtaken() ) return;

		// Which model of each garment to wear.
		var models = new List<(ClothingContainer.ClothingEntry Entry, string StockPath, ClothingFitter.BodyKind MadeFor)>();
		foreach ( var entry in worn )
		{
			var item = entry.Clothing;

			// Skins are a material on the body, not a model.
			if ( !string.IsNullOrEmpty( item.SkinMaterial ) )
				continue;

			var madeFor = bodyKind;
			string stockPath = null;
			if ( madeFor == ClothingFitter.BodyKind.HumanFemale )
				stockPath = item.HumanAltFemaleModel;
			if ( string.IsNullOrEmpty( stockPath ) && madeFor != ClothingFitter.BodyKind.Citizen )
			{
				stockPath = item.HumanAltModel;
				madeFor = ClothingFitter.BodyKind.HumanMale;
			}
			if ( string.IsNullOrEmpty( stockPath ) )
			{
				// No human version of this one. The citizen version will have to do.
				stockPath = item.GetModel( worn.Select( x => x.Clothing ).Where( x => x != item ) );
				madeFor = ClothingFitter.BodyKind.Citizen;
			}
			if ( string.IsNullOrEmpty( stockPath ) )
				continue;

			models.Add( (entry, stockPath, madeFor) );
		}

		// Hair goes under the hat, if there is one: fitted so that it doesn't come through it.
		string hat = models.FirstOrDefault( x => HairGoesUnder( x.Entry.Clothing ) ).StockPath;

		// Ask for everything up front so the garments are fitted side by side.
		var wearing = new List<(Clothing Item, string StockPath, Task<bool> Fitted)>();
		foreach ( var (entry, stockPath, madeFor) in models )
			wearing.Add( (entry.Clothing, stockPath, WearAsync( entry, stockPath, madeFor, skin, eyes, Overtaken, IsHair( entry.Clothing ) ? hat : null )) );

		int fittedCount = 0, stockCount = 0;
		var skinless = new HashSet<Clothing>();
		foreach ( var (item, stockPath, garment) in wearing )
		{
			bool wasFitted = await garment;
			if ( Overtaken() ) return;

			if ( wasFitted ) fittedCount++;
			else stockCount++;

			if ( wasFitted && RemoveSkinFromClothing && ClothingFitter.HasSkin( stockPath ) )
				skinless.Add( item );
		}

		// Hide the body parts the clothing covers, same rules as the stock dresser. Last, so
		// there are no holes in the body while the clothes are still on their way.
		// A garment that lost its copy of the skin no longer covers for the part it hides.
		var hiding = worn.Select( x => x.Clothing ).Where( x => !skinless.Contains( x ) );

		foreach ( var (name, value) in container.GetBodyGroups( hiding, BodyTarget.Model ) )
			BodyTarget.SetBodyGroup( name, value );
		ApplyToon();

		FittedCount = fittedCount;
		StockCount = stockCount;
		Log.Info( $"FitDresser on '{GameObject.Name}' ({bodyKind} clothing): {FittedCount} fitted, {StockCount} as is" );
	}

	void OnHeightChanged( float before, float after )
	{
		if ( !UseLocalAvatar )
			ApplyHeight( after );
	}

	// The tone and age the body and every garment on it are drawn with right now.
	float skinTint = 0.5f, skinAge = 0.5f;

	void OnSkinChanged( float before, float after )
	{
		if ( UseLocalAvatar ) return;
		skinTint = Tint;
		skinAge = Age;
		ApplySkin();
	}

	// Same attributes as the stock Dresser: the skin shader reads them off each renderer.
	void ApplySkin()
	{
		if ( !BodyTarget.IsValid() ) return;
		SetSkin( BodyTarget );
		foreach ( var renderer in BodyTarget.GameObject.Children.Where( x => x.Tags.Has( ClothingTag ) ).SelectMany( x => x.Components.GetAll<SkinnedModelRenderer>() ) )
			SetSkin( renderer );
	}

	void OnToonChanged( bool before, bool after )
	{
		// Copies made by older code survive a hotload; switching the look makes them anew.
		toonOf.Clear();
		ApplyToon();
	}

	// A change callback has to take the property's own type, or it is never called.
	void OnTintEyesChanged( bool before, bool after ) => OnSkinChanged( 0, 0 );
	void OnEyeColorChanged( Color before, Color after ) => OnSkinChanged( 0, 0 );

	void SetSkin( SkinnedModelRenderer renderer )
	{
		renderer.Attributes.Set( "skin_tint", skinTint );
		renderer.Attributes.Set( "skin_age", skinAge );
		// The picker shows the colour as it looks on screen; the shader works in linear light.
		bool tinted = TintEyes && !UseLocalAvatar;
		renderer.Attributes.Set( "eye_tinted", tinted ? 1f : 0f );
		renderer.Attributes.Set( "eye_tint", new Vector3( ToLinear( EyeColor.r ), ToLinear( EyeColor.g ), ToLinear( EyeColor.b ) ) );
	}

	static float ToLinear( float c ) => c <= 0.04045f ? c / 12.92f : MathF.Pow( (c + 0.055f) / 1.055f, 2.4f );

	const string ToonShader = "shaders/fit_toon.shader";

	// The toon copy of each original material, made once per outfit. Null where a material
	// keeps its own look.
	readonly Dictionary<Material, Material> toonOf = new();
	static int toonCopies;

	void ApplyToon()
	{
		if ( !BodyTarget.IsValid() ) return;
		// An earlier version drew an outline as a hidden child of the body; take it away.
		foreach ( var leftover in BodyTarget.GameObject.Children.Where( x => x.Name == "Toon Outline" ).ToArray() )
			leftover.Destroy();
		ToonRenderer( BodyTarget );
		foreach ( var renderer in BodyTarget.GameObject.Children.Where( x => x.Tags.Has( ClothingTag ) ).SelectMany( x => x.Components.GetAll<SkinnedModelRenderer>() ) )
			ToonRenderer( renderer );
	}

	// Per material slot, so each keeps its own textures. A garment gets this again every time a
	// more detailed model is swapped in: its slots may be in another order.
	void ToonRenderer( SkinnedModelRenderer renderer )
	{
		if ( !renderer.IsValid() || renderer.Model is null ) return;
		for ( int i = 0; i < renderer.Materials.Count; i++ )
			renderer.Materials.SetOverride( i, Toon ? ToonCopy( renderer.Materials.GetOriginal( i ) ) : null );
	}

	static Texture Valid( Texture texture ) => texture is not null && texture.IsValid() ? texture : null;

	// A copy of a material on the toon shader, with its textures and the features the look
	// depends on. Null for a material that keeps its own look: see-through ones (glass, a lens),
	// the painted iris on its own shader, and any without a colour texture to copy.
	Material ToonCopy( Material original )
	{
		if ( original is null ) return null;
		if ( toonOf.TryGetValue( original, out var made ) ) return made;

		string shader = original.ShaderName ?? "";
		var color = Valid( original.GetTexture( "g_tColor" ) );
		if ( original.GetFeature( "F_TRANSLUCENT" ) > 0 || shader.Contains( "glass" ) || shader.Contains( "aurora_iris" ) || color is null )
		{
			toonOf[original] = null;
			return null;
		}

		// Each copy its own name: two made under one name (two dressers on the same body model)
		// came out without their textures.
		made = Material.Create( $"{original.ResourceName}_toon_{++toonCopies}", ToonShader );
		// Features first: changing one reloads the material's combos.
		bool cutOut = original.GetFeature( "F_ALPHA_TEST" ) > 0;
		made.SetFeature( "F_MORPH_SUPPORTED", 1 );
		if ( original.GetFeature( "F_RENDER_BACKFACES" ) > 0 ) made.SetFeature( "F_RENDER_BACKFACES", 1 );
		if ( cutOut ) made.SetFeature( "F_ALPHA_TEST", 1 );

		// Every texture the shader reads has to be given: one left unset on a material made at
		// runtime is the engine's checkerboard.
		made.Set( "g_tColor", color );
		made.Set( "g_tNormal", Valid( original.GetTexture( "g_tNormal" ) ) ?? Texture.Load( "materials/default/default_normal.tga" ) );
		made.Set( "g_tRma", Valid( original.GetTexture( "g_tRma" ) ) ?? Texture.White );
		if ( cutOut ) made.Set( "g_flAlphaTestReference", original.GetVector4( "g_flAlphaTestReference" ).x );

		// Skin the stock shader tones by skin_tint (the citizen's tint mask).
		if ( shader.Contains( "skin" ) && original.GetFeature( "F_TINT_MASK" ) > 0 ) made.Set( "g_flToonSkin", 1f );

		// complex.shader packs roughness with the normal, which is what gives a jacket its sheen,
		// and has metalness as a texture or a number.
		bool complex = shader.Contains( "complex" );
		made.Set( "g_flToonComplex", complex ? 1f : 0f );
		var metal = complex && original.GetFeature( "F_METALNESS_TEXTURE" ) > 0 ? Valid( original.GetTexture( "g_tMetalness" ) ) : null;
		made.Set( "g_tToonMetal", metal ?? Texture.White );
		made.Set( "g_flToonMetal", metal is not null ? 1f : complex ? original.GetVector4( "g_flMetalness" ).x : 0f );

		toonOf[original] = made;
		return made;
	}

	// Same parameter and range as the stock Dresser. The stock graph makes a body taller or
	// shorter with additive sequences that lengthen its bones and raise its pelvis. A body with
	// proportions of its own ignores the bone moves, so only the pelvis would rise and it would
	// float. Such a body gets the same lengthening from Stretch instead.
	void ApplyHeight( float height )
	{
		if ( !BodyTarget.IsValid() ) return;
		float scale = height.Remap( 0, 1, 0.8f, 1.2f, true );
		BodyTarget.Set( "scale_height", ownProportions ? 1f : scale );
		stretch = ownProportions ? scale : 1f;
		// Height used to scale the whole body; a scene saved with that on gets it taken off.
		ScaleWhole( 1f );
	}

	// Whether the body is built with proportions of its own (see ClothingFitter.OwnProportions).
	bool ownProportions;

	/// <summary>
	/// The scale an older Height put on the body's object. Kept only so a scene saved with it
	/// gets its body back to the size it was.
	/// </summary>
	[Property, Hide] public float AppliedScale { get; set; } = 1f;

	void ScaleWhole( float scale )
	{
		if ( !BodyTarget.IsValid() || AppliedScale == scale ) return;
		var body = BodyTarget.GameObject;
		body.LocalScale = body.LocalScale * (scale / AppliedScale);
		AppliedScale = scale;
	}

	// How much longer each part of the skeleton gets when the stock graph doubles the height,
	// measured off its Scale_Twice_delta (the citizen's and the human's are the same clip).
	// Legs and arms by the joint, the torso by its spine. The neck, collarbones, hips, feet and
	// fingers keep their size, so the head, shoulders and hands stay as they are and a taller
	// body is longer rather than bigger.
	const float LegsTwice = 1.58f, ArmsTwice = 1.40f, TorsoTwice = 1.95f;

	// Moving the joints apart only stretches the skin where it bends between two bones; a thigh
	// skinned to its one bone would keep its length and only the knee would get longer. So the
	// bones of the lengthened parts are scaled too: along the bone (their X axis) by as much as
	// the part grows, across it by this share of that, or a tall body would come out thin and
	// a short one stocky.
	const float ThickShare = 0.5f;

	// Height for a body with proportions of its own: 1 is as built.
	float stretch = 1f;
	bool stretched;

	// Per bone of the model the tables were made for: what its offset from its parent grows by
	// at twice the height, what the bone itself is scaled by along its length then, and whether
	// it hangs off the pelvis (and is lifted with it to keep the feet on the ground).
	Model stretchModel;
	float[] stretchTwice, lengthTwice;
	bool[] underPelvis;
	int[] feet;
	Transform[] animated, stretchedPose;

	// The animation is taken as it came out of the graph and laid out again with longer bones,
	// as bone overrides. Overrides are applied by the next animation update, so the body shows
	// the pose of the frame before, the same delay ragdoll bones have. Bone-merged clothing
	// takes the bones as overridden and stretches along.
	void Stretch()
	{
		var body = BodyTarget;
		if ( !body.IsValid() || !body.SceneModel.IsValid() || body.Model is null ) return;
		if ( stretch == 1f )
		{
			Unstretch();
			return;
		}

		var bones = body.Model.Bones.AllBones;
		// After a hotload the tables can be missing ones added since they were made.
		if ( stretchModel != body.Model || underPelvis?.Length != bones.Count || lengthTwice?.Length != bones.Count || feet is null )
			MakeStretch( body.Model );

		var so = body.SceneModel;
		var model = so.Transform;
		for ( int i = 0; i < bones.Count; i++ )
		{
			// Nothing to lay out before the first animation update.
			if ( !body.TryGetBoneTransformAnimation( bones[i], out var world ) || world.Rotation == default ) return;
			animated[i] = model.ToLocal( world );
			var parent = bones[i].Parent;
			if ( parent is null )
			{
				stretchedPose[i] = animated[i];
				continue;
			}

			var local = animated[parent.Index].ToLocal( animated[i] );
			var offset = local.Position * (1f + (stretch - 1f) * (stretchTwice[i] - 1f));
			stretchedPose[i] = stretchedPose[parent.Index].ToWorld( new Transform( offset, local.Rotation ) );
		}

		// Longer legs put the feet lower; the pelvis goes up until the lower foot is as high as
		// the animation had it, standing, crouching or in the air.
		float wasLow = float.MaxValue, nowLow = float.MaxValue;
		foreach ( int f in feet )
		{
			wasLow = MathF.Min( wasLow, animated[f].Position.z );
			nowLow = MathF.Min( nowLow, stretchedPose[f].Position.z );
		}
		float lift = feet.Length > 0 ? wasLow - nowLow : 0f;

		for ( int i = 0; i < bones.Count; i++ )
		{
			var pose = stretchedPose[i];
			if ( underPelvis[i] ) pose.Position += Vector3.Up * lift;
			float along = 1f + (stretch - 1f) * (lengthTwice[i] - 1f);
			pose.Scale = new Vector3( along, 1f + (along - 1f) * ThickShare, 1f + (along - 1f) * ThickShare );
			so.SetBoneOverride( i, pose );
		}
		stretched = true;
	}

	void Unstretch()
	{
		if ( !stretched ) return;
		stretched = false;
		if ( BodyTarget.IsValid() && BodyTarget.SceneModel.IsValid() )
			BodyTarget.SceneModel.ClearBoneOverrides();
	}

	void MakeStretch( Model model )
	{
		var bones = model.Bones.AllBones;
		stretchModel = model;
		stretchTwice = new float[bones.Count];
		lengthTwice = new float[bones.Count];
		underPelvis = new bool[bones.Count];
		animated = new Transform[bones.Count];
		stretchedPose = new Transform[bones.Count];
		var feetList = new List<int>();
		for ( int i = 0; i < bones.Count; i++ )
		{
			var bone = bones[i];
			string name = bone.Name;
			string parent = bone.Parent?.Name ?? "";
			float twice = 1f;
			if ( name.StartsWith( "spine_" ) ) twice = TorsoTwice;
			else if ( parent.StartsWith( "leg_upper_" ) || parent.StartsWith( "leg_lower_" ) ) twice = LegsTwice;
			else if ( parent.StartsWith( "arm_upper_" ) || parent.StartsWith( "arm_lower_" ) ) twice = ArmsTwice;
			stretchTwice[i] = twice;

			// The bones the lengthened parts are skinned to: the thigh, shin, upper arm and
			// forearm with their twist bones, and the spine below the chest (the chest bone holds
			// the neck and shoulders, which keep their size).
			float length = 1f;
			if ( name == "spine_0" || name == "spine_1" ) length = TorsoTwice;
			else if ( name.StartsWith( "leg_upper_" ) || name.StartsWith( "leg_lower_" ) ) length = LegsTwice;
			else if ( name.StartsWith( "arm_upper_" ) || name.StartsWith( "arm_lower_" ) ) length = ArmsTwice;
			lengthTwice[i] = length;

			underPelvis[i] = name == "pelvis" || (bone.Parent is not null && underPelvis[bone.Parent.Index]);
			if ( name.StartsWith( "ankle_" ) ) feetList.Add( i );
		}
		feet = feetList.ToArray();
	}

	// Puts one garment on. It appears as soon as its roughest LOD has been fitted and gets
	// swapped for a more detailed model each time another LOD is ready. True if it was fitted.
	async Task<bool> WearAsync( ClothingContainer.ClothingEntry entry, string stockPath, ClothingFitter.BodyKind madeFor, Material skin, Material eyes, Func<bool> overtaken, string under = null )
	{
		var item = entry.Clothing;
		SkinnedModelRenderer renderer = null;

		void Show( Model model )
		{
			if ( overtaken() || !model.IsValid() || model.IsError )
				return;

			if ( renderer.IsValid() )
			{
				if ( renderer.Model == model ) return;
				ClothingFitter.Retire( renderer.Model );
				renderer.Model = model;
				ToonRenderer( renderer );
				return;
			}

			// New objects go into whichever scene is current, which isn't ours after a wait or
			// when this was called from a console command.
			using var sceneScope = BodyTarget.Scene.Push();

			var go = new GameObject( false, $"Clothing - {item.ResourceName}" );
			go.Flags |= GameObjectFlags.NotSaved;
			go.Parent = BodyTarget.GameObject;
			go.Tags.Add( ClothingTag );

			renderer = go.Components.Create<SkinnedModelRenderer>();
			renderer.Model = model;
			renderer.BoneMergeTarget = BodyTarget;
			renderer.SetMaterialOverride( skin, "skin" );
			renderer.SetMaterialOverride( eyes, "eyes" );
			SetSkin( renderer );

			if ( !string.IsNullOrEmpty( item.MaterialGroup ) )
				renderer.MaterialGroup = item.MaterialGroup;

			if ( item.AllowTintSelect )
				renderer.Tint = item.TintSelection.Evaluate( entry.Tint?.Clamp( 0, 1 ) ?? item.TintDefault );

			// Something appearing out of nothing, a moment after the character did, is
			// better eased in than popped in. Swapping in a more detailed model later isn't.
			renderer.Tint = renderer.Tint.WithAlpha( 0 );
			fading[renderer] = Time.Now;

			go.Enabled = true;
			ToonRenderer( renderer );
		}

		// Only a garment that carries a copy of the skin has anything to lose. Clearing the
		// skin lifts cloth that sat level with it, which is wrong for a boot's sole under a
		// foot that stays hidden anyway.
		bool withoutSkin = RemoveSkinFromClothing && ClothingFitter.HasSkin( stockPath );
		Model fitted = FitAtRuntime ? await ClothingFitter.FitAsync( BodyTarget.Model, stockPath, madeFor, Show, withoutSkin, under ) : null;
		if ( overtaken() )
			return false;

		Show( fitted.IsValid() ? fitted : Model.Load( stockPath ) );
		if ( !fitted.IsValid() )
			return false;

		// Jiggle bones and anything else that moves a garment on its own are run by the
		// garment's animation graph. A model built at runtime can't have one, so the fitted
		// model borrows the original's. Same skeleton, so the graph doesn't know the difference.
		var original = await Model.LoadAsync( stockPath );
		if ( !overtaken() && renderer.IsValid() && original?.AnimGraph is { } graph )
			renderer.AnimationGraph = graph;

		return true;
	}

	ClothingContainer BuildContainer()
	{
		if ( UseLocalAvatar )
			return ClothingContainer.CreateFromLocalUser();

		// Going through Add() drops items that can't be worn together, like the stock dresser.
		// Except hair with a hat: the engine keeps those apart because hair comes through a
		// hat, and here the hair is fitted under it instead. Not under a mask over the whole
		// head: hair would only show in its eye holes.
		var container = new ClothingContainer();
		foreach ( var entry in Clothing )
		{
			if ( entry?.Clothing is null || IsHair( entry.Clothing ) ) continue;
			container.Add( entry );
		}

		foreach ( var entry in Clothing )
		{
			if ( entry?.Clothing is null || !IsHair( entry.Clothing ) ) continue;
			if ( container.Clothing.All( x => HairGoesUnder( x.Clothing ) || x.Clothing.CanBeWornWith( entry.Clothing ) ) )
				container.Clothing.Add( entry );
		}

		return container;
	}

	// A hat on top of the head, which hair can be fitted under. Not a mask or a helmet that
	// also covers the face, or a costume head in place of the head.
	static bool HairGoesUnder( Clothing item )
	{
		if ( !item.IsValid() || !item.Category.ToString().StartsWith( "Hat" ) ) return false;
		var slots = item.SlotsUnder | item.SlotsOver;
		return slots.HasFlag( Sandbox.Clothing.Slots.HeadTop ) && !slots.HasFlag( Sandbox.Clothing.Slots.HeadBottom ) && !item.HideBody.HasFlag( Sandbox.Clothing.BodyGroups.Head );
	}
	static bool IsHair( Clothing item ) => item.IsValid() && item.Category.ToString().StartsWith( "Hair" );

	static Material FirstMaterial( IEnumerable<string> paths )
	{
		var path = paths.FirstOrDefault( x => !string.IsNullOrWhiteSpace( x ) );
		return path is null ? null : Material.Load( path );
	}
}
