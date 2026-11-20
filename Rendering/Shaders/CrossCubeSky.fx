// Horizontal-cross cubemap sky. The gain is a pixel-shader constant so HDR values above 1 survive.
float4x4 WorldViewProjection;
float3 Gain;
texture Cross;
sampler2D CrossSampler = sampler_state { Texture=<Cross>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
struct SkyVertex { float4 position:POSITION0; float2 uv:TEXCOORD0; };
SkyVertex Project(float4 position:POSITION0, float2 uv:TEXCOORD0) { SkyVertex v; v.position=mul(position,WorldViewProjection); v.uv=uv; return v; }
float4 Sky(SkyVertex v):COLOR0 { return float4(tex2D(CrossSampler,v.uv).rgb*Gain,1); }
technique CrossCubeSky { pass P0 { VertexShader=compile vs_3_0 Project(); PixelShader=compile ps_3_0 Sky(); } }
