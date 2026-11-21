#ifndef CODEX_LOCAL_LIGHTS
#define CODEX_LOCAL_LIGHTS
// The frame's point and spot lights, found per pixel. CodexEngine.Rendering.ClusteredLights cuts the view into
// clusters (columns, rows and depth slices), lists in each the lights that reach it, and binds the result here.
// A pixel finds its cluster from its world position alone, so any effect that knows where a surface is can light
// it, and pays only for the lights that reach that place.
//
//     LOCAL_LIGHTS_BEGIN(worldPosition, unbakedOnly)
//         sum += light.radiance*saturate(dot(normal,light.toward));
//     LOCAL_LIGHTS_END
//
// runs its body once per light that reaches the point, with `light` a LocalLight. `unbakedOnly` is 1 for a
// surface already lit by a lightmap or light probes, which then skips the lights baked into those, and 0 for
// any other. LocalDiffuse below is the loop most effects want.
// The clusters take one pixel sampler: 4, or the one LIGHT_SAMPLER names before this file is included.
#include "Branching.hlsl"
#include "Lighting.hlsl"
#ifndef LIGHT_SAMPLER
#define LIGHT_SAMPLER s4
#endif
texture LightClusters;
sampler2D ClusterSampler : register(LIGHT_SAMPLER) = sampler_state { Texture=<LightClusters>; MinFilter=Point; MagFilter=Point; MipFilter=None; AddressU=Clamp; AddressV=Clamp; };
float4x4 ClusterView;   // world to the clip space of the view the clusters were built for
float4 ClusterGrid;     // columns, rows, depth slices, slices per doubling of depth
float4 ClusterTexel;    // texel size, the light table's first row, 1/depth at which the slices begin
float ClusterLights;    // lights in the view

struct LocalLight {
    float3 toward;       // unit vector from the surface to the light
    float3 radiance;     // colour after falloff and cone, before the surface's facing and any shadow
    float distance;      // to the light, in world units
    float range;         // where the light ends
    float3 axis;         // a spot's unit axis, away from the light
    float cosOuter;      // cosine of half a spot's outer angle, below -1 for a point light
    float shadowChannel; // channel of a baked shadow mask that holds this light's shadow, negative for none
    float shadowSlot;    // its real-time shadow map (LocalShadows.hlsl), negative for none
    float reach;         // above zero when the light reaches the point
};

// The light table holds four texels a light, sixteen lights a row: position and 1/range², colour and kind, cone
// axis and the cosine of its outer angle, then the cone's sharpness and the shadow slot. The kind is the shadow
// mask channel plus one, plus 8 for a cone and 16 for a shadow map, and above those the size of the source in
// steps of 1/64 times 32: a plain point light is read from two texels.
LocalLight ReadLocalLight(float index, float3 position) {
    LocalLight light;
    light.toward = 0; light.radiance = 0; light.distance = 0; light.shadowChannel = -1; light.shadowSlot = -1;
    light.range = 0; light.axis = 0; light.cosOuter = -2;
    float2 uv = float2((fmod(index,16)*4+.5)*ClusterTexel.x, (ClusterTexel.z+floor(index/16)+.5)*ClusterTexel.y);
    float4 place = tex2Dlod(ClusterSampler,float4(uv,0,0));
    float3 d = place.xyz-position;
    float d2 = max(dot(d,d),1e-4);
    // RangeAttenuation, taken apart: the window that ends the light at its range tells whether it reaches here at
    // all, and the inverse square is held at the source's own size, which the second texel brings.
    float window = d2*place.w;
    float ease = saturate(1-window*window);
    light.reach = ease;
    if (ease > 0) {
        float4 color = tex2Dlod(ClusterSampler,float4(uv.x+ClusterTexel.x,uv.y,0,0));
        float size = floor(color.w/32);
        color.w -= size*32;
        size /= 64;
        light.reach = ease*ease/max(d2,max(size*size,1e-4));
        light.distance = sqrt(d2);
        light.toward = d/light.distance;
        light.range = rsqrt(place.w);
        light.shadowChannel = fmod(color.w,8)-1;
        float spot = 1;
        if (color.w > 7.5) {
            float4 cone = tex2Dlod(ClusterSampler,float4(uv.x+2*ClusterTexel.x,uv.y,0,0));
            float4 more = tex2Dlod(ClusterSampler,float4(uv.x+3*ClusterTexel.x,uv.y,0,0));
            spot = saturate((dot(-light.toward,cone.xyz)-cone.w)*more.x);
            light.shadowSlot = more.y;
            light.axis = cone.xyz;
            light.cosOuter = cone.w;
        }
        light.radiance = color.rgb*(light.reach*spot*spot);
        light.reach *= spot;
    }
    return light;
}

// A cluster lists its lights four to a texel, numbered from one, and ends the list with a zero. The lights outside
// the baked lighting come first and the baked ones after them as negative numbers, so a surface that has baked
// light already stops at the first negative one.
#define LL_NUMBER(id, unbakedOnly) max(id,-(id)*(1-(unbakedOnly)))
#define LOCAL_LIGHTS_BEGIN(position, unbakedOnly) \
    [branch] if (PER_PIXEL(ClusterLights > 0, (position).x)) { \
        float4 ll_clip = mul(float4(position,1),ClusterView); \
        float ll_depth = max(ll_clip.w,1e-4); \
        float2 ll_tile = min(floor(saturate(ll_clip.xy/ll_depth*.5+.5)*ClusterGrid.xy),ClusterGrid.xy-1); \
        float ll_slice = clamp(floor(log2(ll_depth*ClusterTexel.w)*ClusterGrid.w),0,ClusterGrid.z-1); \
        float2 ll_uv = float2((ll_tile.x*4+.5)*ClusterTexel.x,(ll_slice*ClusterGrid.y+ll_tile.y+.5)*ClusterTexel.y); \
        [loop] for (int ll_page = 0; ll_page < 4; ll_page++) { \
            float4 ll_ids = tex2Dlod(ClusterSampler,float4(ll_uv,0,0)); \
            if (LL_NUMBER(ll_ids.x,unbakedOnly) < .5) break; \
            ll_uv.x += ClusterTexel.x; \
            [loop] for (int ll_slot = 0; ll_slot < 4; ll_slot++) { \
                float ll_id = LL_NUMBER(ll_ids[ll_slot],unbakedOnly); \
                if (ll_id < .5) break; \
                LocalLight light = ReadLocalLight(ll_id-1,position); \
                if (light.reach > 0) {
#define LOCAL_LIGHTS_END }}}}

// The diffuse light the local lights put on a surface at `position` facing `normal`, unshadowed.
float3 LocalDiffuse(float3 position, float3 normal) {
    float3 sum = 0;
    LOCAL_LIGHTS_BEGIN(position,0)
        sum += light.radiance*saturate(dot(normal,light.toward));
    LOCAL_LIGHTS_END
    return sum;
}
#endif
