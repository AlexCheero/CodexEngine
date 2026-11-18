// The ambient channel holds the part of scene colour that local geometry can occlude.
// Its alpha is axial view depth in world units (perspective clip W).
struct LitSurface { float4 scene : COLOR0; float4 ambientDepth : COLOR1; };
LitSurface WriteSurface(float4 scene, float3 ambientRadiance, float3 positionWS) {
    LitSurface result;
    result.scene = scene;
    result.ambientDepth = float4(ambientRadiance,mul(float4(positionWS,1),ViewProjection).w);
    return result;
}
