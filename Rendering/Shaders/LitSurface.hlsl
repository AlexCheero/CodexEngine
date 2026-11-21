#ifndef CODEX_LIT_SURFACE
#define CODEX_LIT_SURFACE
// The lit surface every game on the engine shares, in two steps so a game can add its own light between them:
// GatherLight collects what reaches the surface (lightmap or probes, sun with shadows, the level's local lights)
// and ShadeSurface turns it into radiance (Lambert or the BRDF of Brdf.hlsl, sky reflections) under the level's fog.
// The effect supplies the vertex stage and the surface's colour and normal; C# binds the rest:
//   DirectionalShadowMap.BindReceiver   sun direction and shadows       (pixel sampler 1)
//   ClusteredLights.Bind                local lights                    (sampler 4, LocalLights.hlsl)
//   LocalShadowAtlas.BindReceiver       their shadows, with LOCAL_SHADOWS (sampler 5, LocalShadows.hlsl)
//   IrradianceProbes.Bind + UseProbes   light probes                    (sampler 15, or PROBE_SAMPLER)
//   Atmosphere.Bind                     fog beyond the flat one
//   EnvironmentCube                     what glossy surfaces reflect, which may change per draw (sampler 13)
//   Irradiance, Shadowmask, LightDirection per draw, with HasBake and HasLightDirection (samplers 2, 3 and 14)
// Samplers 0, 6 to 12 are the effect's own, and 5 too without LOCAL_SHADOWS.
#include "Branching.hlsl"
#include "DirectionalShadows.hlsl"
#include "Lighting.hlsl"
#include "Brdf.hlsl"
#include "Atmosphere.hlsl"
#include "LocalLights.hlsl"
// An effect that defines LOCAL_SHADOWS before this file also takes the shadows of LocalShadowAtlas (sampler 5).
#ifdef LOCAL_SHADOWS
#include "LocalShadows.hlsl"
#endif
// The lightmap holds the probes' default sampler.
#ifndef PROBE_SAMPLER
#define PROBE_SAMPLER s15
#endif
#include "IrradianceProbes.hlsl"

float3 Eye, SunColor, Ambient, Fog;
float UseProbes;
float SquaredFog, Density;
float SunShadowStrength=1;
// The level's ambient probe evaluated on the six axes (+X,-X,+Y,-Y,+Z,-Z). Flat ambient levels leave UseAmbientCube at 0.
float3 AmbientCube[6];
float UseAmbientCube;
// The draw's lightmap (RGBM), its shadow mask and, where the bake has one, its dominant light direction.
texture Irradiance, Shadowmask, LightDirection;
sampler2D LightmapSampler : register(s2)=sampler_state { Texture=<Irradiance>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
sampler2D MaskSampler : register(s3)=sampler_state { Texture=<Shadowmask>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
sampler2D LightDirectionSampler : register(s14)=sampler_state { Texture=<LightDirection>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
float HasBake, SunBaked, HasLightDirection;
float4 SunMask;
// What the draw's surfaces reflect: the sky, or for a draw inside a reflection
// probe's box the scene captured around that probe. Linear radiance, each smaller level convolved for a rougher
// surface (CodexEngine.Rendering.EnvironmentCube).
texture EnvironmentCube;
samplerCUBE Surroundings:register(s13)=sampler_state{Texture=<EnvironmentCube>;MinFilter=Linear;MagFilter=Linear;MipFilter=Linear;AddressU=Clamp;AddressV=Clamp;AddressW=Clamp;};
float ReflectionIntensity=1;
// Per-surface BRDF inputs: smoothness, metallic, environment reflections, specular highlights. Zero keeps Lambert.
float4 SurfaceBrdf;

struct SurfaceLight {
    float3 light;      // irradiance: everything that reaches the surface
    float3 ambient;    // the part of it nearby geometry can occlude
    float3 specular;   // highlights, to be multiplied by the surface's specular colour
    float3 albedo;     // linear
    float3 view;       // toward the eye
    Brdf brdf;
};
struct ShadedSurface {
    float3 colour;     // linear radiance under the fog
    float3 ambient;    // the occludable share of it, for ScreenSpaceAmbientOcclusion's target
};

// `fromEye` is the surface position relative to the eye and `colour` its display-encoded base colour.
// `added` is light the caller brings itself, and `occlusion` scales the ambient part (1 leaves it whole).
// `metallic` is the surface's metalness at this texel, for an effect that reads it from a map.
// Call this before any branch of the pixel shader: it samples with screen-space derivatives, and when a branch
// came first the sun's shadow lookup shadowed flat ground against itself.
SurfaceLight GatherLight(float3 fromEye,float3 n,float3 colour,float2 lightUV,float shadowReceiver,float3 added,float occlusion,float metallic) {
    float4 lm=tex2D(LightmapSampler,lightUV), mask=tex2D(MaskSampler,lightUV), lightDirection=tex2D(LightDirectionSampler,lightUV);
    // The sun lookup takes screen-space derivatives, so it runs before the first branch.
    float sunLit=SunVisibility(fromEye+Eye,n);
    float3 baked=lm.rgb*lm.a*8;
    baked=lerp(baked,DecodeDirectionalLightmap(baked,lightDirection,n),HasLightDirection);
    float3 unbaked=UseAmbientCube>0?AmbientCubeAt(n,AmbientCube[0],AmbientCube[1],AmbientCube[2],AmbientCube[3],AmbientCube[4],AmbientCube[5]):Ambient;
    // Where the level has baked light probes, what no lightmap covers takes its ambient light from them.
    [branch] if(PER_PIXEL(UseProbes>HasBake,fromEye.x))unbaked=ProbeIrradiance(fromEye+Eye,n);
    SurfaceLight s;
    s.light=lerp(unbaked,baked,HasBake);
    s.ambient=s.light;
    s.view=-normalize(fromEye);
    s.albedo=SrgbToLinear(colour);
    s.brdf=BrdfMetallic(s.albedo,metallic,SurfaceBrdf.x);
    s.specular=0;
    float3 sunRadiance=SunColor*saturate(dot(n,SunDirection))*(1-HasBake*SunBaked)
          *min(1-HasBake*dot(1-mask,SunMask),lerp(1,sunLit,shadowReceiver*SunShadowStrength));
    s.light+=sunRadiance;
    if(SurfaceBrdf.w>0)s.specular+=sunRadiance*SpecularTerm(s.brdf,n,SunDirection,s.view);
    float3 ws=fromEye+Eye;
    s.light+=added;
    // Baked lights already reach lightmapped surfaces through the lightmap and probe-lit ones through the probes.
    LOCAL_LIGHTS_BEGIN(ws,max(HasBake,UseProbes))
        float shadowed=light.shadowChannel<0?1:mask[(int)max(light.shadowChannel,0)];
        float visible=lerp(1,shadowed,HasBake);
#ifdef LOCAL_SHADOWS
        // Like the sun: a baked mask and a real-time map each hide what the other cannot see.
        visible=min(visible,lerp(1,LocalShadow(light,n),shadowReceiver));
#endif
        float3 radiance=light.radiance*saturate(dot(n,light.toward))*visible;
        s.light+=radiance;
        if(SurfaceBrdf.w>0)s.specular+=radiance*SpecularTerm(s.brdf,n,light.toward,s.view);
    LOCAL_LIGHTS_END
    s.light-=s.ambient*(1-occlusion);
    s.ambient*=occlusion;
    return s;
}
// The same with the surface's own metalness (SurfaceBrdf.y) on every texel.
SurfaceLight GatherLight(float3 fromEye,float3 n,float3 colour,float2 lightUV,float shadowReceiver,float3 added,float occlusion) {
    return GatherLight(fromEye,n,colour,lightUV,shadowReceiver,added,occlusion,SurfaceBrdf.y);
}

// `viewDepth` is the eye depth along the view axis (clip w), which is what the fog reads.
ShadedSurface ShadeSurface(SurfaceLight s,float3 fromEye,float3 n,float viewDepth,float occlusion) {
    // Fog is evaluated from eye depth and blended in linear colour.
    float fog = FogAmount(viewDepth*FogHeightScale(Eye.y,fromEye.y),Density,SquaredFog);
    float3 linearLit=s.albedo*max(s.light,0);
    if(SurfaceBrdf.z+SurfaceBrdf.w>0){
        linearLit=s.brdf.diffuse*max(s.light,0)+s.brdf.specular*s.specular;
        if(SurfaceBrdf.z>0){
            float3 r=reflect(-s.view,n);
            float3 sky=texCUBElod(Surroundings,float4(r,ReflectionMip(s.brdf))).rgb;
            linearLit+=sky*ReflectionIntensity*occlusion*EnvironmentSpecular(s.brdf,dot(n,s.view));
        }
    }
    float3 fogColour=SrgbToLinear(Fog);
    [branch] if(PER_PIXEL(FogShape.z+FogShape.w>0,viewDepth))fogColour=FogRadiance(fogColour,normalize(fromEye),SunDirection);
    ShadedSurface o;
    o.colour=lerp(linearLit,fogColour,saturate(fog));
    o.ambient=linearLit*lerp(saturate(s.ambient/max(s.light,.0001)),1,.25)*(1-fog);
    return o;
}
#endif
