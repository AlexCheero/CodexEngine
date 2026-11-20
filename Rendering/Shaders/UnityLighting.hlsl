#ifndef CODEX_UNITY_LIGHTING
#define CODEX_UNITY_LIGHTING
// Reference formulas from Unity's Universal Render Pipeline and built-in shaders, for content
// converted from Unity scenes. Inputs are linear unless a name says otherwise.

// Exact sRGB transfer functions, as used by Unity for textures, colours and the final blit.
float3 SrgbToLinear(float3 c) { c = max(c,0); return c <= .04045 ? c/12.92 : pow((c+.055)/1.055,2.4); }
float3 LinearToSrgb(float3 c) { c = max(c,0); return c <= .0031308 ? c*12.92 : 1.055*pow(c,1/2.4)-.055; }

// URP punctual light attenuation: inverse square with the smooth window (1-(d²/r²)²)².
// invRangeSquared is 1/range².
float UrpDistanceAttenuation(float distanceSquared, float invRangeSquared) {
    float window = distanceSquared*invRangeSquared;
    float smooth = saturate(1-window*window);
    return smooth*smooth/max(distanceSquared,.0001);
}

// Unity scene fog amount (0 = clear) from eye depth. squared selects ExponentialSquared over Exponential.
float UnityFogAmount(float eyeDepth, float density, float squared) {
    float d = density*eyeDepth;
    return saturate(1-exp(-lerp(d,d*d,squared)));
}

// An ambient probe evaluated on the six axes (+X,-X,+Y,-Y,+Z,-Z), blended by the squared normal.
float3 AmbientCubeAt(float3 n, float3 px, float3 nx, float3 py, float3 ny, float3 pz, float3 nz) {
    float3 q = n*n;
    return q.x*(n.x>0?px:nx) + q.y*(n.y>0?py:ny) + q.z*(n.z>0?pz:nz);
}

// Texture coordinate inside a horizontal-cross cubemap (4x3 faces: -X,+Z,+X,-Z in the middle row).
// rotation holds (cos, sin) of Unity's skybox _Rotation; Unity rotates the sky geometry, so a
// direction d samples the cube at R(-angle)·d.
float2 CrossCubeUV(float3 d, float2 rotation) {
    d.xz = float2(dot(d.xz,rotation), d.z*rotation.x-d.x*rotation.y);
    float3 a = abs(d); float2 xy, tile;
    if (a.x >= max(a.z,a.y)) { xy = float2(-sign(d.x)*d.z,d.y)/a.x; tile = float2(d.x>0?2:0,1); }
    else if (a.y >= a.z) { xy = float2(d.x,-sign(d.y)*d.z)/a.y; tile = float2(1,d.y>0?0:2); }
    else { xy = float2(sign(d.z)*d.x,d.y)/a.z; tile = float2(d.z>0?1:3,1); }
    return (tile+clamp(.5+xy*float2(.5,-.5),.001,.999))/float2(4,3);
}

// DecodeDirectionalLightmap (CombinedDirectional lightmaps): direction is the linear _comp_dir texel.
float3 DecodeDirectionalLightmap(float3 color, float4 direction, float3 normal) {
    float halfLambert = dot(normal, direction.xyz-.5)+.5;
    return color*halfLambert/max(1e-4,direction.w);
}

// Shader Graph Voronoi node (Unity_Voronoi_float): distance to the nearest animated cell centre.
float2 ShaderGraphVoronoiVector(float2 uv, float offset) {
    uv = frac(sin(mul(uv,float2x2(15.27,47.63,99.41,89.98)))*46839.32);
    return float2(sin(uv.y*offset)*.5+.5, cos(uv.x*offset)*.5+.5);
}
float ShaderGraphVoronoi(float2 uv, float angleOffset, float cellDensity) {
    float2 g = floor(uv*cellDensity), f = frac(uv*cellDensity); float best = 8;
    for (int y=-1; y<=1; y++) for (int x=-1; x<=1; x++) {
        float2 lattice = float2(x,y);
        best = min(best, distance(lattice+ShaderGraphVoronoiVector(lattice+g,angleOffset), f));
    }
    return best;
}
#endif
