// URP camera post-processing: bloom, chromatic aberration, vignette, HDR colour grading and tonemapping.
// The scene texture holds sRGB-encoded HDR radiance; passes decode it and the final pass re-encodes.
#include "UnityLighting.hlsl"
texture Scene, LowTexture, GlowTexture;
sampler2D Source = sampler_state { Texture=<Scene>; MinFilter=Linear; MagFilter=Linear; MipFilter=None; AddressU=Clamp; AddressV=Clamp; };
sampler2D LowSampler = sampler_state { Texture=<LowTexture>; MinFilter=Linear; MagFilter=Linear; MipFilter=None; AddressU=Clamp; AddressV=Clamp; };
sampler2D GlowSampler = sampler_state { Texture=<GlowTexture>; MinFilter=Linear; MagFilter=Linear; MipFilter=None; AddressU=Clamp; AddressV=Clamp; };
float4 SourceSize, LowSize;                // xy texel size, zw dimensions
float4 BloomParams;                        // threshold, knee, clamp, scatter
float4 BloomComposite;                     // tint rgb * intensity, high quality filtering
float4 GradeParams;                        // post exposure (linear), contrast, saturation, tonemap mode
float3 GradeLift, GradeGamma, GradeGain, GradeShadows, GradeMidtones, GradeHighlights, GradeFilter, GradeBalance;
float4 GradeLimits;
float4 VignetteParams;                     // center xy, intensity, smoothness
float4 VignetteColor;                      // rgb, roundness
float ChromaAmount;

// Explicit LOD everywhere: implicit-gradient samples inside dynamic branches read zero after translation.
float3 Fetch(sampler2D s, float2 uv) { return tex2Dlod(s,float4(uv,0,0)).rgb; }
struct ScreenVertex { float4 Position:POSITION0; float2 UV:TEXCOORD0; };
ScreenVertex Project(float4 position:POSITION0, float2 uv:TEXCOORD0) { ScreenVertex v; v.Position=position; v.UV=uv; return v; }
float3 SceneLinear(float2 uv) { return SrgbToLinear(Fetch(Source,uv)); }

float4 BloomPrefilter(ScreenVertex v):COLOR0 {
    float t = SourceSize.x; float2 uv = v.UV;
    float3 A=SceneLinear(uv+t*float2(-1,-1)), B=SceneLinear(uv+t*float2(0,-1)), C=SceneLinear(uv+t*float2(1,-1));
    float3 D=SceneLinear(uv+t*float2(-.5,-.5)), E=SceneLinear(uv+t*float2(.5,-.5));
    float3 F=SceneLinear(uv+t*float2(-1,0)), G=SceneLinear(uv), H=SceneLinear(uv+t*float2(1,0));
    float3 I=SceneLinear(uv+t*float2(-.5,.5)), J=SceneLinear(uv+t*float2(.5,.5));
    float3 K=SceneLinear(uv+t*float2(-1,1)), L=SceneLinear(uv+t*float2(0,1)), M=SceneLinear(uv+t*float2(1,1));
    float3 c = BloomComposite.w>0 ? (D+E+I+J)*.125+((A+B+G+F)+(B+C+H+G)+(F+G+L+K)+(G+H+M+L))*.03125 : G;
    c = min(BloomParams.z,c);
    float brightness = max(c.r,max(c.g,c.b));
    float soft = clamp(brightness-BloomParams.x+BloomParams.y,0,2*BloomParams.y);
    soft = soft*soft/(4*BloomParams.y+1e-4);
    c *= max(brightness-BloomParams.x,soft)/max(brightness,1e-4);
    return float4(max(c,0),1);
}
float4 BloomBlurH(ScreenVertex v):COLOR0 {
    float t = SourceSize.x*2;
    float3 c = Fetch(Source,v.UV+float2(t*-4,0))*.01621622+Fetch(Source,v.UV+float2(t*-3,0))*.05405405
        +Fetch(Source,v.UV+float2(t*-2,0))*.12162162+Fetch(Source,v.UV+float2(t*-1,0))*.19459459
        +Fetch(Source,v.UV)*.22702703
        +Fetch(Source,v.UV+float2(t,0))*.19459459+Fetch(Source,v.UV+float2(t*2,0))*.12162162
        +Fetch(Source,v.UV+float2(t*3,0))*.05405405+Fetch(Source,v.UV+float2(t*4,0))*.01621622;
    return float4(c,1);
}
float4 BloomBlurV(ScreenVertex v):COLOR0 {
    float t = SourceSize.y;
    float3 c = Fetch(Source,v.UV-float2(0,t*3.23076923))*.07027027+Fetch(Source,v.UV-float2(0,t*1.38461538))*.31621622
        +Fetch(Source,v.UV)*.22702703
        +Fetch(Source,v.UV+float2(0,t*1.38461538))*.31621622+Fetch(Source,v.UV+float2(0,t*3.23076923))*.07027027;
    return float4(c,1);
}
float3 Bicubic(sampler2D s, float2 uv, float4 size) {
    float2 st = uv*size.zw-.5; float2 i = floor(st); float2 f = st-i;
    float2 f2 = f*f, f3 = f2*f;
    float2 w0=(1-3*f+3*f2-f3)/6, w1=(4-6*f2+3*f3)/6, w2=(1+3*f+3*f2-3*f3)/6, w3=f3/6;
    float2 g0 = w0+w1, g1 = w2+w3;
    float2 h0 = (i-.5+w1/g0)*size.xy, h1 = (i+1.5+w3/g1)*size.xy;
    return g0.y*(g0.x*Fetch(s,float2(h0.x,h0.y))+g1.x*Fetch(s,float2(h1.x,h0.y)))
          +g1.y*(g0.x*Fetch(s,float2(h0.x,h1.y))+g1.x*Fetch(s,float2(h1.x,h1.y)));
}
float4 BloomUpsample(ScreenVertex v):COLOR0 {
    float3 high = Fetch(Source,v.UV);
    float3 low = BloomComposite.w>0 ? Bicubic(LowSampler,v.UV,LowSize) : Fetch(LowSampler,v.UV);
    return float4(lerp(high,low,BloomParams.w),1);
}

static const float3x3 LinToLMS = {3.90405e-1,5.49941e-1,8.92632e-3, 7.08416e-2,9.63172e-1,1.35775e-3, 2.31082e-2,1.28021e-1,9.36245e-1};
static const float3x3 LMSToLin = {2.85847e+0,-1.62879e+0,-2.48910e-2, -2.10182e-1,1.15820e+0,3.24281e-4, -4.18120e-2,-1.18169e-1,1.06867e+0};
float3 ToLogC(float3 x) { return x>.011361 ? .244161*log10(5.555556*x+.047996)+.386036 : 5.301883*x+.092819; }
float3 FromLogC(float3 x) { return x>5.301883*.011361+.092819 ? (pow(10,(x-.386036)/.244161)-.047996)/5.555556 : (x-.092819)/5.301883; }
float3 NeutralCurve(float3 x) { return (x*(.2*x+.24*.29)+.272*.02)/(x*(.2*x+.29)+.272*.3)-.02/.3; }
float3 Neutral(float3 x) { float white = 1/NeutralCurve(5.3).x; return NeutralCurve(x*white)*white; }
// Narkowicz fit of the ACES reference rendering transform.
float3 Aces(float3 x) { x *= .6; return saturate(x*(2.51*x+.03)/(x*(2.43*x+.59)+.14)); }
float Luma(float3 c) { return dot(c,float3(.2126729,.7151522,.0721750)); }
// LutBuilderHdr.ColorGrade followed by the tonemapper.
float3 GradeColor(float3 c) {
    c = mul(LMSToLin,mul(LinToLMS,c)*GradeBalance);
    c = FromLogC((ToLogC(c)-.4135884)*GradeParams.y+.4135884);
    c = max(c*GradeFilter,0);
    float l = Luma(c);
    float sh = 1-smoothstep(GradeLimits.x,GradeLimits.y,l), hi = smoothstep(GradeLimits.z,GradeLimits.w,l);
    c = c*GradeShadows*sh+c*GradeMidtones*(1-sh-hi)+c*GradeHighlights*hi;
    c = c*GradeGain+GradeLift;
    c = max(sign(c)*pow(abs(c),GradeGamma),0);
    l = Luma(c); c = l+GradeParams.z*(c-l);
    c = max(c,0);
    return GradeParams.w==1 ? Neutral(c) : GradeParams.w==2 ? Aces(c) : saturate(c);
}
// UberPost: chromatic aberration, bloom, vignette, then grading at post exposure.
float4 Uber(ScreenVertex v):COLOR0 {
    float2 uv = v.UV; float3 c;
    if (ChromaAmount>0) {
        float2 coords = 2*uv-1; float2 delta = (-coords*dot(coords,coords)*ChromaAmount)/3;
        c = float3(SceneLinear(uv).r,SceneLinear(uv+delta).g,SceneLinear(uv+2*delta).b);
    } else c = SceneLinear(uv);
    c += (BloomComposite.w>0?Bicubic(GlowSampler,uv,LowSize):Fetch(GlowSampler,uv))*BloomComposite.rgb;
    float2 d = abs(uv-VignetteParams.xy)*VignetteParams.z; d.x *= VignetteColor.w;
    c *= lerp(VignetteColor.rgb,1,pow(saturate(1-dot(d,d)),VignetteParams.w));
    return float4(LinearToSrgb(GradeColor(c*GradeParams.x)),1);
}
#define PASS(NAME,PS) technique NAME { pass P0 { VertexShader=compile vs_3_0 Project(); PixelShader=compile ps_3_0 PS(); } }
PASS(Prefilter,BloomPrefilter) PASS(BlurH,BloomBlurH) PASS(BlurV,BloomBlurV) PASS(Upsample,BloomUpsample) PASS(Composite,Uber)
