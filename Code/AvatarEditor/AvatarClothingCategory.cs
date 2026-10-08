using System;
using System.Linq;
using Sandbox;

namespace FitAvatar;

/// <summary>
/// Groups the clothing categories into a nice groups
/// </summary>
[AssetType( Name = "Fit Clothing Category", Extension = "fitgrp", Category = "Citizen" )]
public class AvatarClothingCategory : GameResource
{
	[Property] public string Name { get; set; }

	[Property] public Category[] Categories { get; set; }


	public class Category
	{
		[KeyProperty]
		public string Name { get; set; }
		public string Title { get; set; }
		public Texture Icon { get; set; }
		public bool ShowAll { get; set; } = true;
		public SubCategory[] SubCategories { get; set; }
	}

	public class SubCategory
	{
		[KeyProperty]
		public string Name { get; set; } = "Name";
		public string Title { get; set; } = "Title";
		public Texture Icon { get; set; }
		public Clothing.ClothingCategory[] Categories { get; set; }
	}
}

public static class AvatarClothingCategoryInfo
{
	/// <summary>Which clothing category files the resource library knows. Read only.</summary>
	[ConCmd( "fitavatar_categories" )]
	public static void Print()
	{
		var all = ResourceLibrary.GetAll<AvatarClothingCategory>().ToList();
		Log.Info( $"fitavatar_categories: {all.Count} loaded: {string.Join( ", ", all.Select( x => x.ResourcePath ) )}" );
		foreach ( var path in new[] { "scenes/avatar/face.fitgrp", "/scenes/avatar/face.fitgrp" } )
			Log.Info( $"  Get({path}) = {ResourceLibrary.Get<AvatarClothingCategory>( path )?.ResourcePath ?? "null"}" );
	}
}
