#ifndef CODEX_LOCAL_SHADOWS
#define CODEX_LOCAL_SHADOWS
// Real-time shadows of local lights. CodexEngine.Rendering.LocalShadowAtlas keeps a row of square tiles for each
// light it has chosen: six around a point light, one along a spot's axis. LocalShadow returns how much of a light
// from LocalLights.hlsl reaches a surface, and 1 for a light that holds no row.
// The atlas takes one pixel sampler: 5, or the one LOCAL_SHADOW_SAMPLER names before this file is included.
#include "LocalLights.hlsl"
#ifndef LOCAL_SHADOW_SAMPLER
#define LOCAL_SHADOW_SAMPLER s5
#endif
texture LocalShadowAtlas;
sampler2D LocalShadowSampler : register(LOCAL_SHADOW_SAMPLER) = sampler_state { Texture=<LocalShadowAtlas>; MinFilter=Point; MagFilter=Point; MipFilter=None; AddressU=Clamp; AddressV=Clamp; };
float4 LocalShadowGrid;   // tile size in the atlas (u, v), texels along a tile, how much wider than its angle a tile sees
float4 LocalShadowDepth;  // longest shadow range, near plane as a share of the far one, strength

float LocalShadowTap(float2 texel, float2 origin, float reference) {
    float2 uv = (origin+clamp(texel,.5,LocalShadowGrid.z-.5)/LocalShadowGrid.z)*LocalShadowGrid.xy;
    return step(reference, tex2Dlod(LocalShadowSampler,float4(uv,0,0)).r);
}

// `normal` is the surface's unit normal. The lookup takes no screen-space derivatives, so it may sit in a branch.
float LocalShadow(LocalLight light, float3 normal) {
    float lit = 1;
    [branch] if (light.shadowSlot > -.5 && LocalShadowDepth.z > 0) {
        float far = min(light.range, LocalShadowDepth.x), near = far*LocalShadowDepth.y;
        // From the light to the surface, stepped off the surface by a texel or two: more as it turns from the light.
        float3 away = -light.toward*light.distance;
        float facing = saturate(dot(normal,light.toward));
        float texel = 2*light.distance*LocalShadowGrid.w/LocalShadowGrid.z;
        away += normal*(texel*(1+2*(1-facing)));
        float3 a = abs(away);
        float face = 0, along; float2 st;
        if (light.cosOuter > -1) {
            // A spot's one tile looks down its axis and spans its outer angle.
            float3 forward = light.axis;
            float3 right = normalize(cross(abs(forward.y) < .99 ? float3(0,1,0) : float3(1,0,0), forward));
            along = dot(away,forward);
            st = float2(dot(away,right),dot(away,cross(forward,right)))*light.cosOuter/sqrt(max(1-light.cosOuter*light.cosOuter,1e-6));
        } else if (a.x >= a.y && a.x >= a.z) {
            along = a.x; face = away.x > 0 ? 0 : 1; st = float2(away.x > 0 ? -away.z : away.z, away.y);
        } else if (a.y >= a.z) {
            along = a.y; face = away.y > 0 ? 2 : 3; st = float2(away.x, away.y > 0 ? -away.z : away.z);
        } else {
            along = a.z; face = away.z > 0 ? 4 : 5; st = float2(away.z > 0 ? away.x : -away.x, away.y);
        }
        along = max(along,1e-4);
        // The caster pass wrote clip z, which runs from 0 at the near plane to the far distance at the far one.
        float reference = (along-near-texel)*far/(far-near);
        float2 at = (st/(along*LocalShadowGrid.w)*float2(.5,-.5)+.5)*LocalShadowGrid.z;
        float2 base = floor(at-.5), f = at-.5-base, origin = float2(face,light.shadowSlot);
        lit = lerp(lerp(LocalShadowTap(base+float2(.5,.5),origin,reference),LocalShadowTap(base+float2(1.5,.5),origin,reference),f.x),
                   lerp(LocalShadowTap(base+float2(.5,1.5),origin,reference),LocalShadowTap(base+float2(1.5,1.5),origin,reference),f.x),f.y);
        // Shadows end before the range does, and a surface past the tile's edge or its near plane stays lit.
        lit = lerp(lit,1,saturate((along/far-.85)/.15));
        if (along < near || max(abs(st.x),abs(st.y)) > along*LocalShadowGrid.w) lit = 1;
        lit = lerp(1,lit,LocalShadowDepth.z);
    }
    return lit;
}
#endif
