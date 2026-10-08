// Anime look for a body dressed by FitDresser (Toon). The dresser makes a copy of every material
// of the body and its clothing on this shader, with the same colour and normal textures, so
// everything keeps its own colours and only the light changes: the sun and the lamps light a
// surface in two flat bands with a short soft edge between them, the shaded band takes a cool
// shade colour instead of going grey, the sky light is flattened, and a thin rim of light runs
// round the silhouette. Shadows cast on the surface fall into the shaded band.
// Skin that the stock shader tones by the dresser's tint (the citizen's tint mask) gets an anime
// skin tone from the same skin_tint value instead.
HEADER
{
	Description = "Toon look for FitDresser bodies and clothing";
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_ALPHA_TEST, 0..1, "Rendering" );
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		return FinalizeVertex( ProcessVertex( i ) );
	}
}

PS
{
	// Before the includes: common/pixel.hlsl turns alpha-to-coverage on for alpha-tested
	// materials only if it already knows they are.
	StaticCombo( S_ALPHA_TEST, F_ALPHA_TEST, Sys( ALL ) );

	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	// Where the lit band ends (in the cosine of the light's angle) and how soft its edge is.
	float g_flToonEdge < Default( 0.05 ); Range( -1.0, 1.0 ); UiGroup( "Toon,10/10" ); >;
	float g_flToonSoft < Default( 0.08 ); Range( 0.001, 0.5 ); UiGroup( "Toon,10/11" ); >;
	// The shaded band: the direct light it keeps, and the colour it is multiplied by.
	float g_flToonShadeLight < Default( 0.45 ); Range( 0.0, 1.0 ); UiGroup( "Toon,10/12" ); >;
	float3 g_vToonShade < UiType( Color ); Default3( 0.80, 0.74, 0.88 ); UiGroup( "Toon,10/13" ); >;
	// Sky light: how much of it, and how flat (1 = the same from every side).
	float g_flToonAmbient < Default( 1.0 ); Range( 0.0, 2.0 ); UiGroup( "Toon,10/14" ); >;
	float g_flToonAmbientFlat < Default( 0.6 ); Range( 0.0, 1.0 ); UiGroup( "Toon,10/15" ); >;
	// Rim light round the silhouette, on the lit side.
	float g_flToonRim < Default( 0.25 ); Range( 0.0, 2.0 ); UiGroup( "Toon,10/16" ); >;
	float g_flToonRimWidth < Default( 0.35 ); Range( 0.01, 1.0 ); UiGroup( "Toon,10/17" ); >;
	// 1 on skin that the stock shader tones by skin_tint: the tone comes from the two colours
	// below instead, light skin at tint 0 and dark at tint 1, over the texture's own shading.
	float g_flToonSkin < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Toon,10/20" ); >;
	float3 g_vToonSkinLight < UiType( Color ); Default3( 1.0, 0.86, 0.78 ); UiGroup( "Toon,10/21" ); >;
	float3 g_vToonSkinDark < UiType( Color ); Default3( 0.36, 0.22, 0.15 ); UiGroup( "Toon,10/22" ); >;
	float g_flSkinTint < Attribute( "skin_tint" ); Default( 0.5 ); >;

	// Gloss. A copy of a complex.shader material keeps its normal texture as complex packs it:
	// a hemi-octahedral normal in RG and the roughness in B. With that, the surface gets a hard
	// highlight from the sun and a reflection of its surroundings, both as glossy as the
	// original; metal colours them with its own colour.
	float g_flToonComplex < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Toon,10/30" ); >;
	float g_flToonGloss < Default( 1.0 ); Range( 0.0, 2.0 ); UiGroup( "Toon,10/31" ); >;
	float g_flToonHighlightEdge < Default( 0.5 ); Range( 0.05, 0.95 ); UiGroup( "Toon,10/32" ); >;
	float g_flToonMetal < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Toon,10/33" ); >;
	CreateInputTexture2D( TextureToonMetal, Linear, 8, "", "_metal", "Toon,10/34", Default( 1.0 ) );
	Texture2D g_tToonMetal < Channel( R, Box( TextureToonMetal ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;

	// Ordered dither threshold, 0..1, from a 4x4 Bayer matrix.
	float Bayer( float2 vPixel )
	{
		uint2 p = uint2( vPixel ) & 3;
		const float m[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
		return ( m[p.y * 4 + p.x] + 0.5 ) / 16.0;
	}

	float Band( float cosine )
	{
		return smoothstep( g_flToonEdge - g_flToonSoft, g_flToonEdge + g_flToonSoft, cosine );
	}

	float Luma( float3 c ) { return dot( c, float3( 0.2126, 0.7152, 0.0722 ) ); }

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::From( i );
		m.WorldPositionWithOffset = i.vPositionWithOffsetWs;
		m.WorldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		m.ScreenPosition = i.vPositionSs;

		#if ( S_ALPHA_TEST )
			// Cut out by the alpha, but the half-transparent part (sheer stockings, the thin end of
			// a lock) is dithered by how transparent it is instead of cut at the threshold: cut,
			// it vanished up close, where the sharp mip has it below the threshold everywhere.
			// Hair, all but fully opaque or fully clear, keeps a clean edge.
			float flCut = g_flAlphaTestReference;
			float flDither = Bayer( m.ScreenPosition.xy );
			clip( m.Opacity - lerp( flCut * 0.15, flCut, flDither ) );
			// What is left is solid; alpha-to-coverage would cut the half-transparent part again.
			m.Opacity = 1.0;
		#endif

		#if ( S_MODE_DEPTH )
			return float4( 0, 0, 0, m.Opacity );
		#else
		if ( DepthNormals::WantsDepthNormals() )
			return DepthNormals::Output( m.Normal, m.Roughness, m.Opacity );

		float3 vAlbedo = m.Albedo;
		[branch]
		if ( g_flToonSkin > 0.0 )
		{
			// The texture keeps its light and dark (blush, lips) relative to its brightest skin.
			float3 vTone = SrgbGammaToLinear( lerp( g_vToonSkinLight, g_vToonSkinDark, g_flSkinTint ) );
			vAlbedo = lerp( vAlbedo, vTone * saturate( vAlbedo / max( Luma( vAlbedo ), 0.05 ) * 0.9 ), g_flToonSkin );
		}

		// The bands follow the smooth vertex normal: a normal map's fine detail (pores, weave)
		// would fray the edge of the light into specks.
		float3 N = normalize( i.vNormalWs );
		float3 P = m.WorldPosition;
		float3 V = normalize( g_vCameraPositionWs - P );

		// The detailed normal and roughness, where the original had them (see g_flToonComplex).
		float3 vDetail = N;
		float flRough = 1.0;
		[branch]
		if ( g_flToonComplex > 0.0 )
		{
			float4 vTexel = g_tNormal.Sample( TextureFiltering, i.vTextureCoords.xy );
			flRough = vTexel.b;
			vDetail = Vec3TsToWsNormalized( DecodeHemiOctahedronNormal( vTexel.rg ), N, i.vTangentUWs, i.vTangentVWs );
		}
		float flMetal = g_flToonMetal * g_tToonMetal.Sample( TextureFiltering, i.vTextureCoords.xy ).r;
		float flGloss = saturate( 1.0 - flRough );

		// The sun. Its shadow is asked outside any branch: it reads screen derivatives.
		float flSunShadow = g_DirectionalLightCascadeCount > 0 ? DirectionalLightShadow::GetVisibility( P, m.ScreenPosition ) : 1.0;
		float3 vLit = 0.0, vShaded = 0.0;
		float flSunBand = 0.0;
		if ( g_DirectionalLightEnabled )
		{
			float flCos = dot( N, -g_DirectionalLightDirection.xyz );
			// The cast shadow as soft as the shadow map gives it, everywhere: let in only where the
			// light falls squarely, it left a lit streak in the head's shadow on the neck.
			flSunBand = Band( flCos ) * flSunShadow;
			vLit += g_DirectionalLightColor.rgb * flSunBand;
			vShaded += g_DirectionalLightColor.rgb * ( 1.0 - flSunBand );
		}

		// Lamps.
		uint nLights = Light::Count( m.ScreenPosition );
		for ( uint li = 0; li < nLights; li++ )
		{
			Light light = Light::From( P, m.ScreenPosition, li, m.LightmapUV );
			float3 vColor = light.Color * light.Attenuation;
			float flCos = dot( N, light.Direction );
			float flBand = Band( flCos ) * light.Visibility;
			vLit += vColor * flBand;
			vShaded += vColor * ( 1.0 - flBand );
		}

		// Sky light, mostly the same from every side so it doesn't model the form.
		float3 vAmbient = lerp( AmbientLight::From( P, m.ScreenPosition, N ), AmbientLight::From( P, m.ScreenPosition, float3( 0, 0, 1 ) ), g_flToonAmbientFlat ) * g_flToonAmbient;

		float3 vLight = vLit + vShaded * g_flToonShadeLight + vAmbient;
		// The shaded band is coloured, not just darker.
		float flLitShare = saturate( Luma( vLit ) / max( Luma( vLit + vShaded ), 1e-4 ) );
		float3 vShade = lerp( g_vToonShade, float3( 1, 1, 1 ), flLitShare );

		// A hard-edged highlight from the sun, and the surroundings reflected, as glossy as the
		// original surface. Metal shows its colour in both and has no diffuse colour of its own.
		float3 vSpecColor = lerp( float3( 0.04, 0.04, 0.04 ), vAlbedo, flMetal );
		float3 vGlint = 0.0;
		if ( g_DirectionalLightEnabled )
		{
			float3 H = normalize( -g_DirectionalLightDirection.xyz + V );
			float flSpec = pow( saturate( dot( vDetail, H ) ), exp2( 2.0 + flGloss * 9.0 ) );
			float flEdge = g_flToonHighlightEdge;
			vGlint = g_DirectionalLightColor.rgb * lerp( float3( 1, 1, 1 ), vAlbedo, flMetal ) * smoothstep( flEdge - 0.05, flEdge + 0.05, flSpec ) * flSunBand * flGloss;
		}
		float flFacing = saturate( dot( vDetail, V ) );
		float3 vFresnel = vSpecColor + ( 1.0 - vSpecColor ) * pow( 1.0 - flFacing, 5.0 );
		float3 vReflect = EnvMap::From( P, m.ScreenPosition, reflect( -V, vDetail ), flRough.xx ) * vFresnel * flGloss * flGloss;
		vAlbedo *= 1.0 - flMetal;

		float flRim = smoothstep( 1.0 - g_flToonRimWidth, 1.0 - g_flToonRimWidth * 0.5, 1.0 - saturate( dot( N, V ) ) );
		float3 vRim = vAlbedo * ( vLit + vAmbient ) * flRim * g_flToonRim;

		// No ambient occlusion: a copied material has the occlusion texture only when its original
		// was on the same inputs, and toon shading reads better without it anyway.
		float4 color = float4( vAlbedo * vLight * vShade + vRim + ( vGlint + vReflect ) * g_flToonGloss + m.Emission, m.Opacity );

		if ( g_bWireframeMode )
			return g_vWireframeColor;

		return DoAtmospherics( P, m.ScreenPosition.xy, color );
		#endif
	}
}
