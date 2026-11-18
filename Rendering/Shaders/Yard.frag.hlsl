texture BaseMap;
sampler2D AlbedoSampler = sampler_state { Texture=<BaseMap>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Wrap; AddressV=Wrap; };
float AlphaCutoff;
#include "DirectionalShadows.hlsl"
#include "ShadowCaster.hlsl"
float4 YardFragment(SurfaceVertex v) : COLOR0
{
    float3 n = normalize(v.normalWS);
    float key = saturate(dot(n,SunDirection)) * SunVisibility(v.positionWS,n);
    float sky = n.y*0.5+0.5;
    float3 light = float3(0.38,0.44,0.46) + sky*0.25 + key*float3(0.44,0.39,0.32);
    float4 surface = tex2D(AlbedoSampler,v.uv)*v.color;
    clip(surface.a-AlphaCutoff);
    float3 baseColor = surface.rgb * light;
    float fog = saturate((length(EyePosition-v.positionWS)-90.0)/540.0);
    return float4(lerp(baseColor,float3(0.67,0.73,0.71),fog),1);
}
