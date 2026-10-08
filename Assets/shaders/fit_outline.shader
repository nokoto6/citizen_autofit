// Outline for FitDresser's toon look. A copy of the body or garment is drawn a little blown up
// along its normals with only its back faces showing, which leaves a line round the silhouette
// and along folds. The line is the same width on screen near and far, and takes a dark shade
// of the surface's own colour rather than plain black, as anime lines do.
HEADER
{
	Description = "Outline shell for FitDresser's toon look";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
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

	// Line width in pixels.
	float g_flOutlineWidth < Default( 1.5 ); Range( 0.0, 8.0 ); UiGroup( "Outline,10/10" ); >;

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		float3 vWorld = o.vPositionWs + g_vHighPrecisionLightingOffsetWs.xyz;
		float flDistance = length( vWorld - g_vCameraPositionWs );
		// The size of a pixel at this distance (the FOV is horizontal, in radians).
		float flPixel = 2.0 * tan( g_flCameraFOV * 0.5 ) * flDistance * g_vInvViewportSize.x;
		o.vPositionWs += normalize( o.vNormalWs ) * g_flOutlineWidth * flPixel;
		o.vPositionPs = Position3WsToPs( o.vPositionWs.xyz );
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	RenderState( CullMode, FRONT );

	// How dark the line is against the surface's colour, and a colour mixed into it.
	float g_flOutlineShade < Default( 0.3 ); Range( 0.0, 1.0 ); UiGroup( "Outline,10/11" ); >;
	float3 g_vOutlineColor < UiType( Color ); Default3( 0.10, 0.06, 0.08 ); UiGroup( "Outline,10/12" ); >;
	float g_flOutlineColorMix < Default( 0.5 ); Range( 0.0, 1.0 ); UiGroup( "Outline,10/13" ); >;
	// 1 for cut-out cards (hair, lace), which have no outline: a shell of a card is a box.
	float g_flOutlineSkip < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Outline,10/14" ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		clip( 0.5 - g_flOutlineSkip );
		Material m = Material::From( i );
		float3 vColor = lerp( m.Albedo * g_flOutlineShade, SrgbGammaToLinear( g_vOutlineColor ), g_flOutlineColorMix );
		float4 color = float4( vColor, 1.0 );
		return DoAtmospherics( i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz, i.vPositionSs.xy, color );
	}
}
