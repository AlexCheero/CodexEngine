#include "ColorSpace.hlsl"
// Probes store sRGB-encoded linear irradiance, like the surface bake.
texture3D ProbeAtlas;
sampler3D Probes : register(s2) = sampler_state {
    Texture=<ProbeAtlas>; MinFilter=Linear; MagFilter=Linear; MipFilter=None;
    AddressU=Clamp; AddressV=Clamp; AddressW=Clamp;
};
float4 ProbeOriginStep;
float3 ProbeSize;
float3 ProbeIrradiance(float3 world, float3 n) {
    float3 cell=clamp((world-ProbeOriginStep.xyz)*ProbeOriginStep.w,0,ProbeSize-1);
    float3 uvw=(cell+.5)/ProbeSize;
    uvw.y/=6;
    float3 lobe=(float3(0,2,4)+float3(n.x<0,n.y<0,n.z<0))/6;
    float3 w=n*n;
    return w.x*Linear(tex3D(Probes,uvw+float3(0,lobe.x,0)).rgb)
         + w.y*Linear(tex3D(Probes,uvw+float3(0,lobe.y,0)).rgb)
         + w.z*Linear(tex3D(Probes,uvw+float3(0,lobe.z,0)).rgb);
}
