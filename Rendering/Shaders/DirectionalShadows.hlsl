float3 SunDirection = float3(-.5102,.8045,-.3042);
float3 ShadowCenter;
float4x4 ShadowViewProjection;
float4 ShadowMetrics; // inverse resolution, world texel size, radius, enabled
texture ShadowTexture;
sampler2D ShadowSampler : register(s1) = sampler_state {
    Texture=<ShadowTexture>; MinFilter=Point; MagFilter=Point; MipFilter=None; AddressU=Clamp; AddressV=Clamp;
};

float SunVisibility(float3 position, float3 normal) {
    if (ShadowMetrics.w == 0) return 1;
    float facing = saturate(dot(normal, SunDirection));
    float3 offset = ShadowMetrics.y * (1.35*(1-facing)*normal + .20*SunDirection);
    float3 light = mul(float4(position+offset,1), ShadowViewProjection).xyz;
    float2 uv = light.xy*float2(.5,-.5)+.5;
    // Compare each tap against the receiver plane at that tap's texel centre.
    // A single reference depth makes a flat, sloping receiver shadow itself.
    float2 dx = ddx(uv), dy = ddy(uv);
    float2 dz = float2(ddx(light.z),ddy(light.z));
    float determinant = dx.x*dy.y-dx.y*dy.x;
    float2 slope = float2(dy.y*dz.x-dx.y*dz.y, dx.x*dz.y-dy.x*dz.x);
    slope *= sign(determinant)/max(abs(determinant),1e-14);
    float fade = saturate((1-distance(position,ShadowCenter)/ShadowMetrics.z)*10);
    if (fade == 0 || any(abs(light.xy)>=1) || light.z<=0 || light.z>=1) return 1;
    // Sixteen comparisons under a three-texel box. Bilinear weights on its outer taps keep the
    // penumbra continuous as the receiver moves across the texel grid.
    float2 grid = uv/ShadowMetrics.x-.5;
    float2 center = (floor(grid)+.5)*ShadowMetrics.x;
    float2 f = frac(grid);
    float reference = light.z + dot(center-uv,slope);
    float2 depthStep = slope*ShadowMetrics.x;
    float4 across = float4(1-f.x,1,1,f.x), along = float4(1-f.y,1,1,f.y);
    float lit = 0;
    [unroll] for (int j=0; j<4; j++) {
        [unroll] for (int i=0; i<4; i++) {
            float2 tap = float2(i-1,j-1);
            float occluder = tex2Dlod(ShadowSampler,float4(center+tap*ShadowMetrics.x,0,0)).r;
            lit += across[i]*along[j]*step(reference+dot(tap,depthStep),occluder);
        }
    }
    return lerp(1,lit/9,fade);
}
