// rebuild-tag: iris-v3
// The iris of a VRoid body. VRoid paints the iris as a flat picture, with the colour baked in,
// so a dresser's eye colour can't reach it. Here the picture is kept as a grey pattern and the
// colour comes in as the eye_tint attribute (FitDresser.EyeColor), the way skin_tint colours
// the skin. A little glow keeps the eye readable in the shade, as the painted look wants.
HEADER
{
	Description = "VRoid iris coloured by the eye_tint attribute";
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

	// The body's own iris colour, linear, and the dresser's one. The dresser's is used while
	// eye_tinted is 1, so turning Tint Eyes off brings the body's colour back.
	float3 g_vIrisColor < UiType( Color ); Default3( 0.094, 0.036, 0.012 ); UiGroup( "Eye,10/20" ); >;
	float3 g_vEyeTint < Attribute( "eye_tint" ); Default3( 0.094, 0.036, 0.012 ); >;
	float g_flEyeTinted < Attribute( "eye_tinted" ); Default( 0.0 ); >;
	float g_flGlow < Default( 0.06 ); Range( 0.0, 1.0 ); UiGroup( "Eye,10/10" ); >;

	// The grey pattern is scaled so its mean over the iris is 0.5 in the texture, which is this
	// once read as linear. Dividing by it, the tint is the mean colour of the iris.
	static const float GreyMean = 0.214;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::From( i );
		m.Albedo = saturate( m.Albedo / GreyMean * lerp( g_vIrisColor, g_vEyeTint, g_flEyeTinted ) );
		m.Emission = m.Albedo * g_flGlow;
		return ShadingModelStandard::Shade( i, m );
	}
}
