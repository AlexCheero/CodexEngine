#ifndef CODEX_LIT_SURFACE
#define CODEX_LIT_SURFACE
// The lit surface every game on the engine shares, in two steps so a game can add its own light between them:
// GatherLight collects what reaches the surface (lightmap or probes, sun with shadows, the level's local lights)
// and ShadeSurface turns it into radiance (Lambert or URP Lit, sky reflections) under the level's fog.
// The effect supplies the vertex stage and the surface's colour and normal; C# binds the rest:
//   DirectionalShadowMap.BindReceiver   sun direction and shadows       (pixel sampler 1)
//   LightGrid.Bind                      local lights                    (samplers 4 and 5)
//   IrradianceProbes.Bind + UseProbes   light probes                    (sampler 15, or PROBE_SAMPLER)
//   Atmosphere.Bind                     fog beyond the flat one
//   EnvironmentCube                     what glossy surfaces reflect, which may change per draw (sampler 13)
//   Irradiance, Shadowmask, LightDirection per draw, with HasBake and HasLightDirection (samplers 2, 3 and 14)
// Samplers 0, 6 to 12 are the effect's own.
#include "Branching.hlsl"
#include "DirectionalShadows.hlsl"
#include "UnityLighting.hlsl"
#include "UrpBrdf.hlsl"
#include "Atmosphere.hlsl"
// The lightmap holds the probes' default sampler.
#ifndef PROBE_SAMPLER
#define PROBE_SAMPLER s15
#endif
#include "IrradianceProbes.hlsl"

float3 Eye, SunColor, Ambient, Fog;
float UseProbes;
float SquaredFog, Density;
float SunShadowStrength=1;
// Unity's ambient probe evaluated on the six axes (+X,-X,+Y,-Y,+Z,-Z). Flat ambient levels leave UseAmbientCube at 0.
float3 AmbientCube[6];
float UseAmbientCube;
// The draw's lightmap (RGBM), its shadow mask and, where the bake has one, its dominant light direction.
texture Irradiance, Shadowmask, LightDirection;
sampler2D LightmapSampler : register(s2)=sampler_state { Texture=<Irradiance>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
sampler2D MaskSampler : register(s3)=sampler_state { Texture=<Shadowmask>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
sampler2D LightDirectionSampler : register(s14)=sampler_state { Texture=<LightDirection>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
float HasBake, SunBaked, HasLightDirection;
float4 SunMask;
// Local lights in a grid over the ground plane (CodexEngine.Rendering.LightGrid).
texture LightGrid, Lights;
sampler2D Grid : register(s4)=sampler_state {Texture=<LightGrid>; MinFilter=Point; MagFilter=Point; MipFilter=None; AddressU=Clamp; AddressV=Clamp;};
sampler2D LightData : register(s5)=sampler_state {Texture=<Lights>; MinFilter=Point; MagFilter=Point; MipFilter=None; AddressU=Clamp; AddressV=Clamp;};
float4 GridInfo;
float3 LightInfo;
// What the draw's surfaces reflect: the sky (Unity's default reflection source), or for a draw inside a reflection
// probe's box the scene captured around that probe. Linear radiance, each smaller level convolved for a rougher
// surface (CodexEngine.Rendering.EnvironmentCube).
texture EnvironmentCube;
samplerCUBE Surroundings:register(s13)=sampler_state{Texture=<EnvironmentCube>;MinFilter=Linear;MagFilter=Linear;MipFilter=Linear;AddressU=Clamp;AddressV=Clamp;AddressW=Clamp;};
float ReflectionIntensity=1;
// Per-surface URP Lit inputs: smoothness, metallic, environment reflections, specular highlights. Zero keeps Lambert.
float4 SurfaceBrdf;

struct SurfaceLight {
    float3 light;      // irradiance: everything that reaches the surface
    float3 ambient;    // the part of it nearby geometry can occlude
    float3 specular;   // highlights, to be multiplied by the surface's specular colour
    float3 albedo;     // linear
    float3 view;       // toward the eye
    UrpBrdf brdf;
};
struct ShadedSurface {
    float3 colour;     // linear radiance under the fog
    float3 ambient;    // the occludable share of it, for ScreenSpaceAmbientOcclusion's target
};

// `fromEye` is the surface position relative to the eye and `colour` its display-encoded base colour.
// `added` is light the caller brings itself, and `occlusion` scales the ambient part (1 leaves it whole).
// Call this before any branch of the pixel shader: it samples with screen-space derivatives, and when a branch
// came first the sun's shadow lookup shadowed flat ground against itself.
SurfaceLight GatherLight(float3 fromEye,float3 n,float3 colour,float2 lightUV,float shadowReceiver,float3 added,float occlusion) {
    float4 lm=tex2D(LightmapSampler,lightUV), mask=tex2D(MaskSampler,lightUV), lightDirection=tex2D(LightDirectionSampler,lightUV);
    // The sun lookup takes screen-space derivatives, so it runs before the first branch.
    float sunLit=SunVisibility(fromEye+Eye,n);
    float3 baked=lm.rgb*lm.a*8;
    baked=lerp(baked,DecodeDirectionalLightmap(baked,lightDirection,n),HasLightDirection);
    float3 unbaked=UseAmbientCube>0?AmbientCubeAt(n,AmbientCube[0],AmbientCube[1],AmbientCube[2],AmbientCube[3],AmbientCube[4],AmbientCube[5]):Ambient;
    // Where the level has baked light probes, what no lightmap covers takes its ambient light from them, as in Unity.
    [branch] if(PER_PIXEL(UseProbes>HasBake,fromEye.x))unbaked=ProbeIrradiance(fromEye+Eye,n);
    SurfaceLight s;
    s.light=lerp(unbaked,baked,HasBake);
    s.ambient=s.light;
    s.view=-normalize(fromEye);
    s.albedo=SrgbToLinear(colour);
    s.brdf=UrpBrdfMetallic(s.albedo,SurfaceBrdf.y,SurfaceBrdf.x);
    s.specular=0;
    float3 sunRadiance=SunColor*saturate(dot(n,SunDirection))*(1-HasBake*SunBaked)
          *min(1-HasBake*dot(1-mask,SunMask),lerp(1,sunLit,shadowReceiver*SunShadowStrength));
    s.light+=sunRadiance;
    if(SurfaceBrdf.w>0)s.specular+=sunRadiance*UrpSpecularTerm(s.brdf,n,SunDirection,s.view);
    float3 ws=fromEye+Eye;
    s.light+=added;
    float2 cell=floor((ws.xz-GridInfo.xy)*LightInfo.x);
    float2 tileUV=(cell*float2(2,1)+.5)*GridInfo.zw;
    if(LightInfo.z>0 && all(tileUV>0) && all(tileUV<float2(1,.5))) {
        // Baked lights already reach lightmapped surfaces through the lightmap and probe-lit ones through the probes.
        tileUV.y+=max(HasBake,UseProbes)*.5;
        [loop] for(int page=0;page<2;page++) {
            float4 indices=tex2Dlod(Grid,float4(tileUV,0,0))*255;
            [loop] for(int slot=0;slot<4;slot++) {
                float index=indices[slot];if(index<.5) break;
                float row=(index+.5)*LightInfo.y;
                float4 l=tex2Dlod(LightData,float4(.125,row,0,0));
                float3 toLight=l.xyz-ws;
                float d2=max(.0001,dot(toLight,toLight));
                float attenuation=UrpDistanceAttenuation(d2,l.w);
                if(attenuation>0) {
                    float4 color=tex2Dlod(LightData,float4(.375,row,0,0));
                    float4 spot=tex2Dlod(LightData,float4(.625,row,0,0));
                    float scale=tex2Dlod(LightData,float4(.875,row,0,0)).x;
                    float3 dir=toLight*rsqrt(d2);
                    float cone=saturate((dot(-dir,spot.xyz)-spot.w)*scale);
                    float shadowed=color.w<0?1:mask[(int)max(color.w,0)];
                    float3 radiance=color.rgb*saturate(dot(n,dir))*cone*cone*attenuation*lerp(1,shadowed,HasBake);
                    s.light+=radiance;
                    if(SurfaceBrdf.w>0)s.specular+=radiance*UrpSpecularTerm(s.brdf,n,dir,s.view);
                }
            }
            tileUV.x+=GridInfo.z;
        }
    }
    s.light-=s.ambient*(1-occlusion);
    s.ambient*=occlusion;
    return s;
}

// `viewDepth` is the eye depth along the view axis (clip w), which is what URP's fog reads.
ShadedSurface ShadeSurface(SurfaceLight s,float3 fromEye,float3 n,float viewDepth,float occlusion) {
    // URP evaluates fog from eye depth and blends it in linear color.
    float fog = UnityFogAmount(viewDepth*FogHeightScale(Eye.y,fromEye.y),Density,SquaredFog);
    float3 linearLit=s.albedo*max(s.light,0);
    if(SurfaceBrdf.z+SurfaceBrdf.w>0){
        linearLit=s.brdf.diffuse*max(s.light,0)+s.brdf.specular*s.specular;
        if(SurfaceBrdf.z>0){
            float3 r=reflect(-s.view,n);
            float3 sky=texCUBElod(Surroundings,float4(r,UrpReflectionMip(s.brdf))).rgb;
            linearLit+=sky*ReflectionIntensity*occlusion*UrpEnvironmentSpecular(s.brdf,dot(n,s.view));
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
