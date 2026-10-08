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
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	StaticCombo( S_ALPHA_TEST, F_ALPHA_TEST, Sys( ALL ) );
	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	// Where the lit band ends (in the cosine of the light's angle) and how soft its edge is.
	float g_flToonEdge < Default( 0.05 ); Range( -1.0, 1.0 ); UiGroup( "Toon,10/10" ); >;
	float g_flToonSoft < Default( 0.04 ); Range( 0.001, 0.5 ); UiGroup( "Toon,10/11" ); >;
	// The shaded band: the direct light it keeps, and the colour it is multiplied by.
	float g_flToonShadeLight < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Toon,10/12" ); >;
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

		AdjustAlphaToCoverage( m );

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

		float3 N = m.Normal;
		float3 P = m.WorldPosition;
		float3 V = normalize( g_vCameraPositionWs - P );

		// The sun. Its shadow is asked outside any branch: it reads screen derivatives.
		float flSunShadow = g_DirectionalLightCascadeCount > 0 ? DirectionalLightShadow::GetVisibility( P, m.ScreenPosition ) : 1.0;
		float3 vLit = 0.0, vShaded = 0.0;
		float flSunBand = 0.0;
		if ( g_DirectionalLightEnabled )
		{
			flSunBand = Band( dot( N, -g_DirectionalLightDirection.xyz ) ) * smoothstep( 0.35, 0.65, flSunShadow );
			vLit += g_DirectionalLightColor.rgb * flSunBand;
			vShaded += g_DirectionalLightColor.rgb * ( 1.0 - flSunBand );
		}

		// Lamps.
		uint nLights = Light::Count( m.ScreenPosition );
		for ( uint li = 0; li < nLights; li++ )
		{
			Light light = Light::From( P, m.ScreenPosition, li, m.LightmapUV );
			float3 vColor = light.Color * light.Attenuation;
			float flBand = Band( dot( N, light.Direction ) ) * smoothstep( 0.35, 0.65, light.Visibility );
			vLit += vColor * flBand;
			vShaded += vColor * ( 1.0 - flBand );
		}

		// Sky light, mostly the same from every side so it doesn't model the form.
		float3 vAmbient = lerp( AmbientLight::From( P, m.ScreenPosition, N ), AmbientLight::From( P, m.ScreenPosition, float3( 0, 0, 1 ) ), g_flToonAmbientFlat ) * g_flToonAmbient;

		float3 vLight = vLit + vShaded * g_flToonShadeLight + vAmbient;
		// The shaded band is coloured, not just darker.
		float flLitShare = saturate( Luma( vLit ) / max( Luma( vLit + vShaded ), 1e-4 ) );
		float3 vShade = lerp( g_vToonShade, float3( 1, 1, 1 ), flLitShare );

		float flRim = smoothstep( 1.0 - g_flToonRimWidth, 1.0 - g_flToonRimWidth * 0.5, 1.0 - saturate( dot( N, V ) ) );
		float3 vRim = vAlbedo * ( vLit + vAmbient ) * flRim * g_flToonRim;

		// No ambient occlusion: a copied material has the occlusion texture only when its original
		// was on the same inputs, and toon shading reads better without it anyway.
		float4 color = float4( vAlbedo * vLight * vShade + vRim + m.Emission, m.Opacity );

		if ( g_bWireframeMode )
			return g_vWireframeColor;

		return DoAtmospherics( P, m.ScreenPosition.xy, color );
		#endif
	}
}
