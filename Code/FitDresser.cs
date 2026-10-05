using System;
using System.Collections.Generic;
using System.Linq;
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

	[Button( "Clear Clothing" )]
	public void Clear()
	{
		if ( !BodyTarget.IsValid() )
			return;

		foreach ( var child in BodyTarget.GameObject.Children.ToArray() )
		{
			if ( child.Tags.Has( ClothingTag ) )
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

	// Every dresser that is currently alive, in the editor scene too. A console command has no
	// way to reach the scene open in the editor, so they sign in here themselves.
	static readonly HashSet<FitDresser> Live = new();

	protected override void OnUpdate()
	{
		Live.Add( this );
	}

	protected override void OnDestroy()
	{
		Live.Remove( this );
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
		if ( !BodyTarget.IsValid() )
			return;

		// New objects go into whichever scene is current, which isn't ours when this is called
		// from a console command or from another scene's code.
		using var sceneScope = BodyTarget.Scene.Push();

		Clear();
		FittedCount = 0;
		StockCount = 0;

		var container = BuildContainer();
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
			_ => FitAtRuntime ? ClothingFitter.KindOf( BodyTarget.Model ) : ClothingFitter.BodyKind.Citizen,
		};

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

			var model = LoadFitted( stockPath, madeFor );
			if ( !model.IsValid() || model.IsError )
				continue;

			var go = new GameObject( false, $"Clothing - {item.ResourceName}" );
			go.Flags |= GameObjectFlags.NotSaved;
			go.Parent = BodyTarget.GameObject;
			go.Tags.Add( ClothingTag );

			var renderer = go.Components.Create<SkinnedModelRenderer>();
			renderer.Model = model;
			renderer.BoneMergeTarget = BodyTarget;
			renderer.SetMaterialOverride( skin, "skin" );
			renderer.SetMaterialOverride( eyes, "eyes" );

			if ( !string.IsNullOrEmpty( item.MaterialGroup ) )
				renderer.MaterialGroup = item.MaterialGroup;

			if ( item.AllowTintSelect )
				renderer.Tint = item.TintSelection.Evaluate( entry.Tint?.Clamp( 0, 1 ) ?? item.TintDefault );

			go.Enabled = true;
		}

		// Hide the body parts the clothing covers, same rules as the stock dresser.
		foreach ( var (name, value) in container.GetBodyGroups( worn.Select( x => x.Clothing ), BodyTarget.Model ) )
			BodyTarget.SetBodyGroup( name, value );

		Log.Info( $"FitDresser on '{GameObject.Name}' ({bodyKind} clothing): {FittedCount} fitted, {StockCount} as is" );
	}

	ClothingContainer BuildContainer()
	{
		if ( UseLocalAvatar )
			return ClothingContainer.CreateFromLocalUser();

		// Going through Add() drops items that can't be worn together, like the stock dresser.
		var container = new ClothingContainer();
		foreach ( var entry in Clothing )
		{
			if ( entry?.Clothing is not null )
				container.Add( entry );
		}

		return container;
	}

	Model LoadFitted( string stockPath, ClothingFitter.BodyKind madeFor )
	{
		if ( FitAtRuntime )
		{
			var fitted = ClothingFitter.Fit( BodyTarget.Model, stockPath, madeFor );
			if ( fitted.IsValid() )
			{
				FittedCount++;
				return fitted;
			}
		}

		StockCount++;
		return Model.Load( stockPath );
	}

	static Material FirstMaterial( IEnumerable<string> paths )
	{
		var path = paths.FirstOrDefault( x => !string.IsNullOrWhiteSpace( x ) );
		return path is null ? null : Material.Load( path );
	}
}
