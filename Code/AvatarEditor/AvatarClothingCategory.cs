using System;
using System.Collections.Generic;
using Sandbox;

namespace FitAvatar;

/// <summary>
/// Groups the clothing categories into a nice groups. The menu keeps these as .grp resources;
/// here they are a table in code, as a game only sees a resource type of its own after the
/// editor restarts, and the clothing grid came up empty until then. The icons are the
/// menu's own, as svg files next to the scene.
/// </summary>
public sealed class AvatarClothingCategory
{
	public string Name { get; }
	public Category[] Categories { get; }

	public AvatarClothingCategory( string name, Category[] categories )
	{
		Name = name;
		Categories = categories;
	}

	public sealed class Category
	{
		public string Name { get; }
		public string Title { get; }
		public bool ShowAll { get; }
		public SubCategory[] SubCategories { get; }
		readonly string icon;

		public Category( string name, string title, string icon, bool showAll, SubCategory[] subCategories )
		{
			Name = name;
			Title = title;
			this.icon = icon;
			ShowAll = showAll;
			SubCategories = subCategories;
		}

		public Texture Icon => LoadIcon( icon );
	}

	public sealed class SubCategory
	{
		public string Name { get; }
		public string Title { get; }
		public Clothing.ClothingCategory[] Categories { get; }
		readonly string icon;

		public SubCategory( string name, string title, string icon, Clothing.ClothingCategory[] categories )
		{
			Name = name;
			Title = title;
			this.icon = icon;
			Categories = categories;
		}

		public Texture Icon => icon is null ? null : LoadIcon( icon );
	}

	// Drawn white, at the size the menu's icons were made at; the loader keeps one per path.
	static Texture LoadIcon( string path ) => Texture.Load( $"{path}?w=256&h=256", false );

	/// <summary>The groups by name: face, clothing, accessories.</summary>
	public static AvatarClothingCategory Get( string name ) => All.GetValueOrDefault( name ?? "" );

	static readonly Dictionary<string, AvatarClothingCategory> All = new()
	{
		["face"] = new( "Face",
		[
			new( "hair", "Hair", "scenes/avatar/icons/face_hair.svg", true,
			[
				new( "short", "Short", "scenes/avatar/icons/face_hair_short.svg", [Clothing.ClothingCategory.Hair, Clothing.ClothingCategory.HairShort] ),
				new( "medium", "Medium", "scenes/avatar/icons/face_hair_medium.svg", [Clothing.ClothingCategory.HairMedium] ),
				new( "long", "Long", "scenes/avatar/icons/face_hair_long.svg", [Clothing.ClothingCategory.HairLong] ),
				new( "updo", "Updo", "scenes/avatar/icons/face_hair_updo.svg", [Clothing.ClothingCategory.HairUpdo] ),
				new( "special", "Special", "scenes/avatar/icons/face_hair_special.svg", [Clothing.ClothingCategory.HairSpecial] ),
			] ),
			new( "eyes", "Eyes", "scenes/avatar/icons/face_eyes.svg", true,
			[
				new( "color", "Color", "scenes/avatar/icons/face_eyes_color.svg", [Clothing.ClothingCategory.Eyes] ),
				new( "brows", "Eyebrows", "scenes/avatar/icons/face_eyes_brows.svg", [Clothing.ClothingCategory.Eyebrows] ),
				new( "lashes", "Lashes", "scenes/avatar/icons/face_eyes_lashes.svg", [Clothing.ClothingCategory.Eyelashes] ),
			] ),
			new( "makeup", "Makeup", "scenes/avatar/icons/face_makeup.svg", false,
			[
				new( "lips", "Lips", "scenes/avatar/icons/face_makeup_lips.svg", [Clothing.ClothingCategory.MakeupLips] ),
				new( "eyeshadow", "Eye Shadow", "scenes/avatar/icons/face_makeup_eyeshadow.svg", [Clothing.ClothingCategory.MakeupEyeshadow] ),
				new( "eyeliner", "Eyeliner", "scenes/avatar/icons/face_makeup_eyeliner.svg", [Clothing.ClothingCategory.MakeupEyeliner] ),
				new( "highlighter", "Highlighter", "scenes/avatar/icons/face_makeup_highlighter.svg", [Clothing.ClothingCategory.MakeupHighlighter] ),
				new( "blush", "Blush", "scenes/avatar/icons/face_makeup_blush.svg", [Clothing.ClothingCategory.MakeupBlush] ),
				new( "special", "Special", "scenes/avatar/icons/face_makeup_special.svg", [Clothing.ClothingCategory.MakeupSpecial] ),
			] ),
			new( "complexion", "Complexion", "scenes/avatar/icons/face_complexion.svg", false,
			[
				new( "freckles", "Freckles", "scenes/avatar/icons/face_complexion_freckles.svg", [Clothing.ClothingCategory.ComplexionFreckles] ),
				new( "acne", "Acne", "scenes/avatar/icons/face_complexion_acne.svg", [Clothing.ClothingCategory.ComplexionAcne] ),
				new( "scars", "Scars", "scenes/avatar/icons/face_complexion_scars.svg", [Clothing.ClothingCategory.ComplexionScars] ),
			] ),
			new( "facialhair", "Facial Hair", "scenes/avatar/icons/face_facialhair.svg", false,
			[
				new( "mustache", "Mustache", "scenes/avatar/icons/face_facialhair_mustache.svg", [Clothing.ClothingCategory.FacialHairMustache] ),
				new( "beard", "Beard", "scenes/avatar/icons/face_facialhair_beard.svg", [Clothing.ClothingCategory.FacialHairBeard, Clothing.ClothingCategory.Facial] ),
				new( "stubble", "Stubble", "scenes/avatar/icons/face_facialhair_stubble.svg", [Clothing.ClothingCategory.FacialHairStubble] ),
				new( "sideburns", "Sideburns", "scenes/avatar/icons/face_facialhair_sideburns.svg", [Clothing.ClothingCategory.FacialHairSideburns] ),
				new( "goatee", "Goatee", "scenes/avatar/icons/face_facialhair_goatee.svg", [Clothing.ClothingCategory.FacialHairGoatee] ),
			] ),
		] ),
		["clothing"] = new( "Clothing",
		[
			new( "fullbody", "Full Body", "scenes/avatar/icons/clothing_fullbody.svg", true,
			[
				new( "dress", "Dress", "scenes/avatar/icons/clothing_fullbody_dress.svg", [Clothing.ClothingCategory.Dress, Clothing.ClothingCategory.Fullbody] ),
				new( "suit", "Suit", "scenes/avatar/icons/clothing_fullbody_suit.svg", [Clothing.ClothingCategory.Suit] ),
				new( "uniform", "Uniform", "scenes/avatar/icons/clothing_fullbody_uniform.svg", [Clothing.ClothingCategory.Uniform] ),
				new( "costume", "Costume", "scenes/avatar/icons/clothing_fullbody_costume.svg", [Clothing.ClothingCategory.Costume] ),
			] ),
			new( "tops", "Tops", "scenes/avatar/icons/clothing_tops.svg", true,
			[
				new( "tshirt", "T-Shirt", "scenes/avatar/icons/clothing_tops_tshirt.svg", [Clothing.ClothingCategory.TShirt, Clothing.ClothingCategory.Tops, Clothing.ClothingCategory.None] ),
				new( "vest", "Vest", "scenes/avatar/icons/clothing_tops_vest.svg", [Clothing.ClothingCategory.Vest] ),
				new( "hoodie", "Hoodie", "scenes/avatar/icons/clothing_tops_hoodie.svg", [Clothing.ClothingCategory.Hoodie] ),
				new( "sweatshirt", "Sweatshirt", "scenes/avatar/icons/clothing_tops_sweatshirt.svg", [Clothing.ClothingCategory.Sweatshirt] ),
				new( "shirt", "Shirt", "scenes/avatar/icons/clothing_tops_shirt.svg", [Clothing.ClothingCategory.Shirt] ),
				new( "knitwear", "Knitwear", "scenes/avatar/icons/clothing_tops_knitwear.svg", [Clothing.ClothingCategory.Knitwear] ),
			] ),
			new( "bottoms", "Bottoms", "scenes/avatar/icons/clothing_bottoms.svg", false,
			[
				new( "shorts", "Shorts", "scenes/avatar/icons/clothing_bottoms_shorts.svg", [Clothing.ClothingCategory.Shorts] ),
				new( "trousers", "Trousers", "scenes/avatar/icons/clothing_bottoms_trousers.svg", [Clothing.ClothingCategory.Trousers, Clothing.ClothingCategory.Bottoms] ),
				new( "jeans", "Jeans", "scenes/avatar/icons/clothing_bottoms_jeans.svg", [Clothing.ClothingCategory.Jeans] ),
				new( "skirt", "Skirt", "scenes/avatar/icons/clothing_bottoms_skirt.svg", [Clothing.ClothingCategory.Skirt] ),
			] ),
			new( "shoes", "Shoes", "scenes/avatar/icons/clothing_shoes.svg", false,
			[
				new( "shoes", "Shoes", "scenes/avatar/icons/clothing_shoes_shoes.svg", [Clothing.ClothingCategory.Footwear, Clothing.ClothingCategory.Shoes, Clothing.ClothingCategory.Heels, Clothing.ClothingCategory.Sandals, Clothing.ClothingCategory.Slippers, Clothing.ClothingCategory.Boots] ),
				new( "heels", "Heels", "scenes/avatar/icons/clothing_shoes_heels.svg", [Clothing.ClothingCategory.Heels] ),
				new( "trainers", "Trainers", "scenes/avatar/icons/clothing_shoes_trainers.svg", [Clothing.ClothingCategory.Trainers] ),
				new( "boots", "Boots", "scenes/avatar/icons/clothing_shoes_boots.svg", [Clothing.ClothingCategory.Boots] ),
				new( "slippers", "Slippers", "scenes/avatar/icons/clothing_shoes_slippers.svg", [Clothing.ClothingCategory.Slippers] ),
			] ),
			new( "outwear", "Coats", "scenes/avatar/icons/clothing_outwear.svg", false,
			[
				new( "jacket", "Jacket", "scenes/avatar/icons/clothing_outwear_jacket.svg", [Clothing.ClothingCategory.Jacket] ),
				new( "cardigan", "Cardigan", "scenes/avatar/icons/clothing_outwear_cardigan.svg", [Clothing.ClothingCategory.Cardigan] ),
				new( "coat", "Coat", "scenes/avatar/icons/clothing_outwear_coat.svg", [Clothing.ClothingCategory.Coat] ),
				new( "gilet", "Gilet", "scenes/avatar/icons/clothing_outwear_gilet.svg", [Clothing.ClothingCategory.Gilet] ),
			] ),
			new( "underwear", "Underwear", "scenes/avatar/icons/clothing_underwear.svg", false,
			[
				new( "underwear", "Underwear", "scenes/avatar/icons/clothing_underwear_underwear.svg", [Clothing.ClothingCategory.Underwear, Clothing.ClothingCategory.Underpants] ),
				new( "socks", "Socks", "scenes/avatar/icons/clothing_underwear_socks.svg", [Clothing.ClothingCategory.Socks] ),
				new( "bra", "Bra", "scenes/avatar/icons/clothing_underwear_bra.svg", [Clothing.ClothingCategory.Bra] ),
			] ),
		] ),
		["accessories"] = new( "accessories",
		[
			new( "hat", "Hats", "scenes/avatar/icons/accessories_hat.svg", true,
			[
				new( "cap", "Cap", "scenes/avatar/icons/accessories_hat_cap.svg", [Clothing.ClothingCategory.Hat, Clothing.ClothingCategory.Headwear] ),
				new( "beanie", "Beanie", "scenes/avatar/icons/accessories_hat_beanie.svg", [Clothing.ClothingCategory.HatBeanie] ),
				new( "formal", "Formal", "scenes/avatar/icons/accessories_hat_formal.svg", [Clothing.ClothingCategory.HatFormal] ),
				new( "costume", "Costume", "scenes/avatar/icons/accessories_hat_costume.svg", [Clothing.ClothingCategory.HatCostume] ),
				new( "uniform", "Uniform", "scenes/avatar/icons/accessories_hat_uniform.svg", [Clothing.ClothingCategory.HatUniform] ),
				new( "special", "Special", "scenes/avatar/icons/accessories_hat_special.svg", [Clothing.ClothingCategory.HatSpecial] ),
			] ),
			new( "glasses", "Glasses", "scenes/avatar/icons/accessories_glasses.svg", true,
			[
				new( "eye", "Eye", "scenes/avatar/icons/accessories_glasses_eye.svg", [Clothing.ClothingCategory.GlassesEye, Clothing.ClothingCategory.Eyewear] ),
				new( "sun", "Sun", "scenes/avatar/icons/accessories_glasses_sun.svg", [Clothing.ClothingCategory.GlassesSun] ),
				new( "special", "Special", "scenes/avatar/icons/accessories_glasses_special.svg", [Clothing.ClothingCategory.GlassesSpecial] ),
			] ),
			new( "neck", "Neck", "scenes/avatar/icons/accessories_neck.svg", false,
			[
				new( "chain", "Chain", "scenes/avatar/icons/accessories_neck_chain.svg", [Clothing.ClothingCategory.NecklaceChain] ),
				new( "pendant", "Trousers", "scenes/avatar/icons/accessories_neck_pendant.svg", [Clothing.ClothingCategory.NecklacePendant] ),
				new( "special", "Special", "scenes/avatar/icons/accessories_neck_special.svg", [Clothing.ClothingCategory.NecklaceSpecial] ),
			] ),
			new( "wrist", "Wrist", "scenes/avatar/icons/accessories_wrist.svg", false,
			[
				new( "watch", "Watch", "scenes/avatar/icons/accessories_wrist_watch.svg", [Clothing.ClothingCategory.WristWatch, Clothing.ClothingCategory.Wristwear] ),
				new( "jewel", "Jewel", "scenes/avatar/icons/accessories_wrist_jewel.svg", [Clothing.ClothingCategory.WristJewel] ),
				new( "band", "Band", "scenes/avatar/icons/accessories_wrist_band.svg", [Clothing.ClothingCategory.WristBand] ),
				new( "special", "Special", "scenes/avatar/icons/accessories_wrist_special.svg", [Clothing.ClothingCategory.WristSpecial] ),
			] ),
			new( "ear", "Ear", "scenes/avatar/icons/accessories_ear.svg", false,
			[
				new( "dangle", "Dangle", "scenes/avatar/icons/accessories_ear_dangle.svg", [Clothing.ClothingCategory.EarringDangle] ),
				new( "stud", "Stud", "scenes/avatar/icons/accessories_ear_stud.svg", [Clothing.ClothingCategory.EarringStud] ),
				new( "special", "Special", "scenes/avatar/icons/accessories_ear_special.svg", [Clothing.ClothingCategory.EarringSpecial] ),
			] ),
			new( "piercing", "Piercings", "scenes/avatar/icons/accessories_piercing.svg", false,
			[
				new( "piercing", "Piercing", "scenes/avatar/icons/accessories_piercing_piercing.svg", [Clothing.ClothingCategory.Piercing] ),
				new( "nose", "Nose", "scenes/avatar/icons/accessories_piercing_nose.svg", [Clothing.ClothingCategory.PierceNose] ),
				new( "eyebrow", "Eyebrow", "scenes/avatar/icons/accessories_piercing_eyebrow.svg", [Clothing.ClothingCategory.PierceEyebrow] ),
				new( "special", "Special", "scenes/avatar/icons/accessories_piercing_special.svg", [Clothing.ClothingCategory.PierceSpecial] ),
			] ),
			new( "head", "Head", "scenes/avatar/icons/accessories_head.svg", false,
			[
				new( "jewel", "Jewel", "scenes/avatar/icons/accessories_head_jewel.svg", [Clothing.ClothingCategory.HeadJewel] ),
				new( "band", "Band", "scenes/avatar/icons/accessories_head_band.svg", [Clothing.ClothingCategory.HeadBand] ),
				new( "tech", "Tech", "scenes/avatar/icons/accessories_head_tech.svg", [Clothing.ClothingCategory.HeadTech] ),
				new( "special", "Special", "scenes/avatar/icons/accessories_head_special.svg", [Clothing.ClothingCategory.HeadSpecial] ),
			] ),
			new( "hands", "Hands", "scenes/avatar/icons/accessories_hands.svg", true,
			[
				new( "gloves", "Gloves", "scenes/avatar/icons/accessories_hands_gloves.svg", [Clothing.ClothingCategory.Gloves] ),
				new( "rings", "Rings", "scenes/avatar/icons/accessories_hands_rings.svg", [Clothing.ClothingCategory.Ring] ),
			] ),
		] ),
	};
}
