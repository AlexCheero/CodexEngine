struct SkinInput
{
    float4 position : POSITION0;
    float3 normal : NORMAL0;
    float2 uv : TEXCOORD0;
    float4 indices : BLENDINDICES0;
    float4 weights : BLENDWEIGHT0;
};
#include "SurfaceInterface.hlsl"
#include "Character.vert.hlsl"
#include "Yard.frag.hlsl"
#include "Inspection.frag.hlsl"
technique AnimatedCharacter
{
    pass P0 { VertexShader = compile vs_3_0 CharacterVertex(); PixelShader = compile ps_3_0 YardFragment(); }
}
technique AnimatedFrame
{
    pass P0 { VertexShader = compile vs_3_0 CharacterVertex(); PixelShader = compile ps_3_0 FrameFragment(); }
}
