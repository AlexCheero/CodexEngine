// URP camera post-processing: bloom, chromatic aberration, vignette, HDR colour grading and tonemapping.
// The scene texture holds sRGB-encoded HDR radiance; passes decode it and the final pass re-encodes.
#include "UnityLighting.hlsl"
#include "Branching.hlsl"
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
float SceneIsLinear;                       // 1: the scene texture holds linear radiance instead of sRGB-encoded
float2 Output;                             // x: dither amplitude in display units, y: 1 keeps luma in alpha for Smooth
texture ShaftTexture;
sampler2D ShaftSampler = sampler_state { Texture=<ShaftTexture>; MinFilter=Linear; MagFilter=Linear; MipFilter=None; AddressU=Clamp; AddressV=Clamp; };
float4 ShaftSource;                        // the sun on screen (uv), share of the way to it one blur pass covers, width/height
float4 ShaftLight;                         // tint times strength, then 1 when shafts are drawn
float ShaftThreshold;                      // sky radiance below this feeds no shaft

// Explicit LOD everywhere: implicit-gradient samples inside dynamic branches read zero after translation.
float3 Fetch(sampler2D s, float2 uv) { return tex2Dlod(s,float4(uv,0,0)).rgb; }
struct ScreenVertex { float4 Position:POSITION0; float2 UV:TEXCOORD0; };
ScreenVertex Project(float4 position:POSITION0, float2 uv:TEXCOORD0) { ScreenVertex v; v.Position=position; v.UV=uv; return v; }
float3 SceneLinear(float2 uv) { float3 c = Fetch(Source,uv); return lerp(SrgbToLinear(c),c,SceneIsLinear); }

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

// Light shafts, first pass: keep the sky around the sun, at a quarter of the size. The scene's alpha is 1 where
// nothing was drawn over the sky, and each of the four samples averages four texels.
float4 ShaftSky(ScreenVertex v):COLOR0 {
    float3 sum = 0;
    [unroll] for (int j=0; j<2; j++)
        [unroll] for (int i=0; i<2; i++) {
            float4 c = tex2Dlod(Source,float4(v.UV+SourceSize.xy*(float2(i,j)*2-1),0,0));
            float3 radiance = min(lerp(SrgbToLinear(c.rgb),c.rgb,SceneIsLinear),4);
            // Only what outshines the threshold streaks, so a dim sky adds no haze of its own.
            float bright = max(radiance.r,max(radiance.g,radiance.b));
            sum += radiance*(saturate((bright-ShaftThreshold)/max(bright,1e-4))*smoothstep(.985,.999,c.a));
        }
    float2 away = (v.UV-ShaftSource.xy)*float2(ShaftSource.w,1);
    return float4(sum*.25*saturate(1-length(away)/.9),1);
}
// Run twice: smear that light away from the sun. The second run takes steps an eighth as long, which fills
// the gaps between the first one's samples.
float4 ShaftSmear(ScreenVertex v):COLOR0 {
    float2 stride = (ShaftSource.xy-v.UV)*ShaftSource.z/8;
    float3 sum = 0; float weight = 1, total = 0;
    [unroll] for (int i=0; i<8; i++) { sum += Fetch(Source,v.UV+stride*i)*weight; total += weight; weight *= .86; }
    return float4(sum/total,1);
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
// Interleaved gradient noise: even across a neighbourhood, so it reads as grain rather than a pattern.
float Noise(float2 pixel) { return frac(52.9829189*frac(dot(pixel,float2(.06711056,.00583715)))); }
// UberPost: chromatic aberration, bloom, vignette, then grading at post exposure.
float4 Uber(ScreenVertex v):COLOR0 {
    float2 uv = v.UV; float3 c;
    if (ChromaAmount>0) {
        float2 coords = 2*uv-1; float2 delta = (-coords*dot(coords,coords)*ChromaAmount)/3;
        c = float3(SceneLinear(uv).r,SceneLinear(uv+delta).g,SceneLinear(uv+2*delta).b);
    } else c = SceneLinear(uv);
    c += (BloomComposite.w>0?Bicubic(GlowSampler,uv,LowSize):Fetch(GlowSampler,uv))*BloomComposite.rgb;
    [branch] if (PER_PIXEL(ShaftLight.w>0,uv.x)) c += Fetch(ShaftSampler,uv)*ShaftLight.rgb;
    float2 d = abs(uv-VignetteParams.xy)*VignetteParams.z; d.x *= VignetteColor.w;
    c *= lerp(VignetteColor.rgb,1,pow(saturate(1-dot(d,d)),VignetteParams.w));
    float3 encoded = LinearToSrgb(GradeColor(c*GradeParams.x));
    // Triangular noise of one display step hides the bands an 8-bit target cuts into smooth gradients.
    float2 pixel = uv*SourceSize.zw;
    encoded += (Noise(pixel)+Noise(pixel+float2(47.0,17.0))-1)*Output.x;
    return float4(encoded,lerp(1,dot(saturate(encoded),float3(.299,.587,.114)),Output.y));
}
// Edge smoothing over the display-encoded frame, whose alpha holds luma (FXAA 3.11 quality search).
// Most pixels leave after five samples; an edge pixel walks along the edge to find where it ends and
// blends toward the neighbour across it by its position on that span.
float4 Encoded(float2 uv) { return tex2Dlod(Source,float4(uv,0,0)); }
float4 SmoothEdges(ScreenVertex v):COLOR0 {
    float2 uv = v.UV, t = SourceSize.xy;
    float4 m = Encoded(uv);
    float lN = Encoded(uv+float2(0,-t.y)).a, lS = Encoded(uv+float2(0,t.y)).a;
    float lW = Encoded(uv+float2(-t.x,0)).a, lE = Encoded(uv+float2(t.x,0)).a;
    float hi = max(max(lN,lS),max(lW,max(lE,m.a))), lo = min(min(lN,lS),min(lW,min(lE,m.a)));
    float range = hi-lo;
    if (range < max(.04,hi*.125)) return float4(m.rgb,1);
    float lNW = Encoded(uv+float2(-t.x,-t.y)).a, lNE = Encoded(uv+float2(t.x,-t.y)).a;
    float lSW = Encoded(uv+float2(-t.x,t.y)).a, lSE = Encoded(uv+float2(t.x,t.y)).a;
    float horizontal = abs(lN+lS-2*m.a)*2+abs(lNE+lSE-2*lE)+abs(lNW+lSW-2*lW);
    float vertical = abs(lW+lE-2*m.a)*2+abs(lNW+lNE-2*lN)+abs(lSW+lSE-2*lS);
    bool flat = horizontal >= vertical;               // the edge runs along x
    float l1 = flat?lN:lW, l2 = flat?lS:lE;
    bool first = abs(l1-m.a) >= abs(l2-m.a);          // the steeper side is the one before this pixel
    float gradient = .25*max(abs(l1-m.a),abs(l2-m.a));
    float across = (flat?t.y:t.x)*(first?-1:1);
    float edgeLuma = .5*((first?l1:l2)+m.a);
    float2 along = flat?float2(t.x,0):float2(0,t.y);
    float2 start = uv+(flat?float2(0,across*.5):float2(across*.5,0));
    float2 back = start-along, ahead = start+along;
    float endBack = Encoded(back).a-edgeLuma, endAhead = Encoded(ahead).a-edgeLuma;
    bool doneBack = abs(endBack) >= gradient, doneAhead = abs(endAhead) >= gradient;
    static const float Stride[5] = {1.5,2,2,4,8};
    [unroll] for (int i=0; i<5; i++) {
        if (!doneBack) { back -= along*Stride[i]; endBack = Encoded(back).a-edgeLuma; doneBack = abs(endBack) >= gradient; }
        if (!doneAhead) { ahead += along*Stride[i]; endAhead = Encoded(ahead).a-edgeLuma; doneAhead = abs(endAhead) >= gradient; }
    }
    float toBack = flat?uv.x-back.x:uv.y-back.y, toAhead = flat?ahead.x-uv.x:ahead.y-uv.y;
    bool below = m.a < edgeLuma;
    bool nearEndBelow = (toBack<toAhead?endBack:endAhead) < 0;
    float shift = nearEndBelow != below ? .5-min(toBack,toAhead)/(toBack+toAhead) : 0;
    // A lone bright or dark pixel has no span to measure; it blends by its contrast with the neighbourhood.
    float lone = saturate(abs((2*(lN+lS+lW+lE)+lNW+lNE+lSW+lSE)/12-m.a)/range);
    lone = lone*lone*(3-2*lone);
    shift = max(shift,lone*lone*.5);
    return float4(Encoded(uv+(flat?float2(0,shift*across):float2(shift*across,0))).rgb,1);
}
#define PASS(NAME,PS) technique NAME { pass P0 { VertexShader=compile vs_3_0 Project(); PixelShader=compile ps_3_0 PS(); } }
PASS(Prefilter,BloomPrefilter) PASS(BlurH,BloomBlurH) PASS(BlurV,BloomBlurV) PASS(Upsample,BloomUpsample) PASS(Composite,Uber)
PASS(Smooth,SmoothEdges) PASS(ShaftMask,ShaftSky) PASS(ShaftBlur,ShaftSmear)
