#include "ColorSpace.hlsl"
// Probes store sRGB-encoded linear irradiance, like the surface bake, as a share of ProbeScale.
// The atlas takes pixel sampler 2 unless the effect names another before including this file.
#ifndef PROBE_SAMPLER
#define PROBE_SAMPLER s2
#endif
texture3D ProbeAtlas;
sampler3D Probes : register(PROBE_SAMPLER) = sampler_state {
    Texture=<ProbeAtlas>; MinFilter=Linear; MagFilter=Linear; MipFilter=None;
    AddressU=Clamp; AddressV=Clamp; AddressW=Clamp;
};
float4 ProbeOriginStep;
float3 ProbeSize;
float ProbeScale = 1;
// Explicit level, so a material may sample probes inside a branch.
float3 ProbeLobe(float3 uvw) { return Linear(tex3Dlod(Probes,float4(uvw,0)).rgb); }
float3 ProbeIrradiance(float3 world, float3 n) {
    float3 cell=clamp((world-ProbeOriginStep.xyz)*ProbeOriginStep.w,0,ProbeSize-1);
    float3 uvw=(cell+.5)/ProbeSize;
    uvw.y/=6;
    float3 lobe=(float3(0,2,4)+float3(n.x<0,n.y<0,n.z<0))/6;
    float3 w=n*n;
    return (w.x*ProbeLobe(uvw+float3(0,lobe.x,0))
          + w.y*ProbeLobe(uvw+float3(0,lobe.y,0))
          + w.z*ProbeLobe(uvw+float3(0,lobe.z,0)))*ProbeScale;
}
