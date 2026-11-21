#include "Branching.hlsl"
float3 SunDirection = float3(-.5102,.8045,-.3042);
float4x4 ShadowBasis;      // world to light space: a rotation whose +Z points at the sun
float4 ShadowCascades[4];  // light-space centre (z at the far end of the depth range) and 1/half-size, smallest first
float4 ShadowMetrics;      // 1/tile resolution, 1/depth range, cascade count, enabled
float4 ShadowFilter;       // comparisons along one axis (2, 3 or 4), usable extent of a tile, blend band
texture ShadowTexture;
sampler2D ShadowSampler : register(s1) = sampler_state {
    Texture=<ShadowTexture>; MinFilter=Point; MagFilter=Point; MipFilter=None; AddressU=Clamp; AddressV=Clamp;
};

// One comparison against the receiver plane at that texel's centre. A single reference depth
// makes a flat, sloping receiver shadow itself.
float ShadowTap(float2 texel, float2 at, float tile, float depth, float2 depthStep) {
    float2 uv = float2((texel.x*ShadowMetrics.x+tile)/ShadowMetrics.z, texel.y*ShadowMetrics.x);
    return step(depth+dot(texel-at,depthStep), tex2Dlod(ShadowSampler,float4(uv,0,0)).r);
}

// Visibility from one cascade. `slope` is the receiver plane's light-space depth per unit of light-space x and y.
float CascadeVisibility(float4 cascade, float tile, float3 light, float3 normal, float facing, float2 slope) {
    float size = 2*ShadowMetrics.x/cascade.w;                      // world width of one texel
    // Step off the surface along its normal, more as it turns from the sun, and toward the sun (+Z here).
    float3 d = light+size*(1.35*(1-facing)*normal+float3(0,0,.20))-cascade.xyz;
    float depth = -d.z*ShadowMetrics.y;
    float2 at = (d.xy*cascade.w*float2(.5,-.5)+.5)/ShadowMetrics.x; // position in the tile, in texels
    float2 depthStep = float2(-slope.x,slope.y)*size*ShadowMetrics.y;
    float lit = 0;
    // The outer comparisons carry bilinear weights, which keeps the penumbra continuous as the
    // receiver moves across the texel grid: a box of three, two or one texel.
    [branch] if (PER_PIXEL(ShadowFilter.x > 3.5, depth)) {
        float2 base = floor(at-.5), f = at-.5-base;
        float4 across = float4(1-f.x,1,1,f.x), along = float4(1-f.y,1,1,f.y);
        [unroll] for (int j=0; j<4; j++)
            [unroll] for (int i=0; i<4; i++)
                lit += across[i]*along[j]*ShadowTap(base+float2(i-.5,j-.5),at,tile,depth,depthStep);
        lit /= 9;
    } else [branch] if (PER_PIXEL(ShadowFilter.x > 2.5, depth)) {
        float2 base = floor(at), f = at-base;
        float3 across = float3(1-f.x,1,f.x), along = float3(1-f.y,1,f.y);
        [unroll] for (int j=0; j<3; j++)
            [unroll] for (int i=0; i<3; i++)
                lit += across[i]*along[j]*ShadowTap(base+float2(i-.5,j-.5),at,tile,depth,depthStep);
        lit /= 4;
    } else {
        float2 base = floor(at-.5), f = at-.5-base;
        lit = lerp(lerp(ShadowTap(base+float2(.5,.5),at,tile,depth,depthStep),ShadowTap(base+float2(1.5,.5),at,tile,depth,depthStep),f.x),
                   lerp(ShadowTap(base+float2(.5,1.5),at,tile,depth,depthStep),ShadowTap(base+float2(1.5,1.5),at,tile,depth,depthStep),f.x),f.y);
    }
    return depth<=0 || depth>=1 ? 1 : lit;
}

float SunVisibility(float3 position, float3 normal) {
    float3 light = mul(float4(position,1),ShadowBasis).xyz;
    // The receiver plane, from how light-space position changes across the screen. It is the same in
    // every cascade, so a pixel quad that straddles two of them still yields a clean slope.
    float3 dx = ddx(light), dy = ddy(light);
    float determinant = dx.x*dy.y-dx.y*dy.x;
    float2 slope = float2(dy.y*dx.z-dx.y*dy.z, dx.x*dy.z-dy.x*dx.z);
    slope *= sign(determinant)/max(abs(determinant),1e-14);
    float facing = saturate(dot(normal,SunDirection));
    float3 lightNormal = mul(normal,(float3x3)ShadowBasis);
    // The smallest cascade that holds the receiver, and the next one out for the blend.
    float count = ShadowMetrics.z, tile = 0, edge = 2;
    float4 cascade = 0, wider = 0;
    [unroll] for (int i=3; i>=0; i--) {
        float4 c = ShadowCascades[i];
        float2 n = abs((light.xy-c.xy)*c.w);
        float e = max(n.x,n.y);
        if (i < count && (e < ShadowFilter.y || i > count-1.5)) { wider = cascade; cascade = c; tile = i; edge = e; }
    }
    float outward = saturate((edge-ShadowFilter.y+ShadowFilter.z)/ShadowFilter.z);
    bool widest = tile > count-1.5;
    float lit = 1;
    [branch] if (ShadowMetrics.w > 0 && !(widest && outward >= 1)) {
        lit = CascadeVisibility(cascade,tile,light,lightNormal,facing,slope);
        // Toward a cascade's edge the receiver takes on the next one, and past the widest, full light.
        float beyond = 1;
        [branch] if (outward > 0 && !widest) beyond = CascadeVisibility(wider,tile+1,light,lightNormal,facing,slope);
        lit = lerp(lit,beyond,outward);
    }
    return lit;
}
