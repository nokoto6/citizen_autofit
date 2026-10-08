using System;
using System.Collections.Generic;
using System.Linq;

namespace Sandbox;

/// <summary>
/// How the avatar editor sorts clothing: three tabs (face, clothing, accessories), each a list
/// of sections, each split into kinds. The same grouping as s&amp;box's own editor (its .clthgrp
/// files), kept here as code with icon font names instead of drawn icons.
/// </summary>
public static class FitClothingGroups
{
	public sealed record Kind( string Title, Clothing.ClothingCategory[] Categories );

	public sealed record Section( string Title, string Icon, Kind[] Kinds )
	{
		public IEnumerable<Clothing.ClothingCategory> Categories => Kinds.SelectMany( x => x.Categories );
	}

	public sealed record Tab( string Title, Section[] Sections );

	static Kind K( string title, params string[] categories ) =>
		new( title, categories.Select( x => Enum.TryParse<Clothing.ClothingCategory>( x, out var c ) ? (Clothing.ClothingCategory?)c : null ).Where( x => x.HasValue ).Select( x => x.Value ).ToArray() );

	public static readonly Tab[] Tabs =
	[
		new( "Face",
		[
			new( "Hair", "face_retouching_natural", [K( "Short", "Hair", "HairShort" ), K( "Medium", "HairMedium" ), K( "Long", "HairLong" ), K( "Updo", "HairUpdo" ), K( "Special", "HairSpecial" )] ),
			new( "Eyes", "visibility", [K( "Color", "Eyes" ), K( "Eyebrows", "Eyebrows" ), K( "Lashes", "Eyelashes" )] ),
			new( "Makeup", "brush", [K( "Lips", "MakeupLips" ), K( "Eye Shadow", "MakeupEyeshadow" ), K( "Eyeliner", "MakeupEyeliner" ), K( "Highlighter", "MakeupHighlighter" ), K( "Blush", "MakeupBlush" ), K( "Special", "MakeupSpecial" )] ),
			new( "Complexion", "face", [K( "Freckles", "ComplexionFreckles" ), K( "Acne", "ComplexionAcne" ), K( "Scars", "ComplexionScars" )] ),
			new( "Facial Hair", "face_6", [K( "Mustache", "FacialHairMustache" ), K( "Beard", "FacialHairBeard", "Facial" ), K( "Stubble", "FacialHairStubble" ), K( "Sideburns", "FacialHairSideburns" ), K( "Goatee", "FacialHairGoatee" )] ),
		] ),
		new( "Clothing",
		[
			new( "Full Body", "accessibility", [K( "Dress", "Dress", "Fullbody" ), K( "Suit", "Suit" ), K( "Uniform", "Uniform" ), K( "Costume", "Costume" )] ),
			new( "Tops", "checkroom", [K( "T-Shirt", "TShirt", "Tops", "None" ), K( "Vest", "Vest" ), K( "Hoodie", "Hoodie" ), K( "Sweatshirt", "Sweatshirt" ), K( "Shirt", "Shirt" ), K( "Knitwear", "Knitwear" )] ),
			new( "Bottoms", "airline_seat_legroom_normal", [K( "Shorts", "Shorts" ), K( "Trousers", "Trousers", "Bottoms" ), K( "Jeans", "Jeans" ), K( "Skirt", "Skirt" )] ),
			new( "Shoes", "ice_skating", [K( "Shoes", "Footwear", "Shoes", "Sandals" ), K( "Heels", "Heels" ), K( "Trainers", "Trainers" ), K( "Boots", "Boots" ), K( "Slippers", "Slippers" )] ),
			new( "Coats", "dry_cleaning", [K( "Jacket", "Jacket" ), K( "Cardigan", "Cardigan" ), K( "Coat", "Coat" ), K( "Gilet", "Gilet" )] ),
			new( "Underwear", "local_laundry_service", [K( "Underwear", "Underwear", "Underpants" ), K( "Socks", "Socks" ), K( "Bra", "Bra" )] ),
		] ),
		new( "Accessories",
		[
			new( "Hats", "school", [K( "Cap", "Hat", "Headwear" ), K( "Beanie", "HatBeanie" ), K( "Formal", "HatFormal" ), K( "Costume", "HatCostume" ), K( "Uniform", "HatUniform" ), K( "Special", "HatSpecial" )] ),
			new( "Glasses", "eyeglasses", [K( "Eye", "GlassesEye", "Eyewear" ), K( "Sun", "GlassesSun" ), K( "Special", "GlassesSpecial" )] ),
			new( "Neck", "diamond", [K( "Chain", "NecklaceChain" ), K( "Pendant", "NecklacePendant" ), K( "Special", "NecklaceSpecial" )] ),
			new( "Wrist", "watch", [K( "Watch", "WristWatch", "Wristwear" ), K( "Jewel", "WristJewel" ), K( "Band", "WristBand" ), K( "Special", "WristSpecial" )] ),
			new( "Ear", "hearing", [K( "Dangle", "EarringDangle" ), K( "Stud", "EarringStud" ), K( "Special", "EarringSpecial" )] ),
			new( "Piercings", "radio_button_checked", [K( "Piercing", "Piercing" ), K( "Nose", "PierceNose" ), K( "Eyebrow", "PierceEyebrow" ), K( "Special", "PierceSpecial" )] ),
			new( "Head", "headset", [K( "Jewel", "HeadJewel" ), K( "Band", "HeadBand" ), K( "Tech", "HeadTech" ), K( "Special", "HeadSpecial" )] ),
			new( "Hands", "back_hand", [K( "Gloves", "Gloves" ), K( "Rings", "Ring" )] ),
		] ),
	];
}
