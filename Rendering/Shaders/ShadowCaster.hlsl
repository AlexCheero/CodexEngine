// Rigid and skinned vertex stages share the colour pass's deformation, UV and alpha cutoff.
struct ShadowVertex {
    float4 clip : POSITION0;
    float4 surface : TEXCOORD0;
};
ShadowVertex ProjectCaster(SurfaceVertex v) {
    ShadowVertex o;
    o.clip = v.clip;
    o.surface = float4(v.uv, v.color.a, v.clip.z);
    return o;
}
float4 ShadowDepth(ShadowVertex v) : COLOR0 {
    clip(tex2D(AlbedoSampler,v.surface.xy).a*v.surface.z-AlphaCutoff);
    return v.surface.w;
}
