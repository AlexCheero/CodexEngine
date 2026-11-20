// Horizontal-cross cubemap sky. The gain is a pixel-shader constant so HDR values above 1 survive.
float4x4 WorldViewProjection;
float3 Gain;
float LinearOutput;   // 1: write linear radiance for a linear scene target
texture Cross;
sampler2D CrossSampler = sampler_state { Texture=<Cross>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
struct SkyVertex { float4 position:POSITION0; float2 uv:TEXCOORD0; };
SkyVertex Project(float4 position:POSITION0, float2 uv:TEXCOORD0) { SkyVertex v; v.position=mul(position,WorldViewProjection); v.uv=uv; return v; }
float3 Decode(float3 c) { c=max(c,0); return c<=.04045?c/12.92:pow((c+.055)/1.055,2.4); }
float4 Sky(SkyVertex v):COLOR0 { float3 c=tex2D(CrossSampler,v.uv).rgb*Gain; return float4(lerp(c,Decode(c),LinearOutput),1); }
technique CrossCubeSky { pass P0 { VertexShader=compile vs_3_0 Project(); PixelShader=compile ps_3_0 Sky(); } }
