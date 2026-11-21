// Unlit textured, vertex-coloured sprites authored for a display-encoded target, drawn into a linear HDR target:
// the product texture*colour is decoded from sRGB so blending happens on linear values.
#include "Lighting.hlsl"
float4x4 WorldViewProjection;
// Linear multiplier after decoding: HDR sprites stored as sRGB(linear/scale) are drawn with Gain=scale.
float Gain;
texture Sprite;
sampler2D SpriteSampler = sampler_state { Texture=<Sprite>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
struct SpriteVertex { float4 position:POSITION0; float4 color:COLOR0; float2 uv:TEXCOORD0; };
SpriteVertex Project(float4 position:POSITION0, float4 color:COLOR0, float2 uv:TEXCOORD0) {
    SpriteVertex v; v.position=mul(position,WorldViewProjection); v.color=color; v.uv=uv; return v;
}
float4 Shade(SpriteVertex v):COLOR0 { float4 c=tex2D(SpriteSampler,v.uv)*v.color; return float4(SrgbToLinear(c.rgb)*Gain,c.a); }
technique SrgbSprite { pass P0 { VertexShader=compile vs_3_0 Project(); PixelShader=compile ps_3_0 Shade(); } }
