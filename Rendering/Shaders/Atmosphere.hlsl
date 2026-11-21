#ifndef CODEX_ATMOSPHERE
#define CODEX_ATMOSPHERE
// Fog that thins with height, takes part of its colour from the sky behind it and brightens toward the sun.
// CodexEngine.Rendering.Atmosphere binds the settings. With every one at zero the fog is the caller's flat one.
#include "UnityLighting.hlsl"
float4 FogShape;  // height falloff per unit, height at which the density is the authored one, share of sky colour, sun scatter
float4 FogSun;    // scatter radiance (linear), lobe exponent
// The sky's colour along +X,-X,+Y,-Y,+Z,-Z with no detail left, linear (EnvironmentCube.Axes). The fog keeps its
// own copy of the sky: a draw's reflection cube may hold a reflection probe's surroundings instead, and two
// surfaces side by side must still fade into the same fog.
float3 FogSky[6];

// Scales the eye distance of a point `rise` above the eye, so the thinner air higher up counts for less.
// It is the mean density along the ray, relative to the authored one: e^(-k(eye-base)) · (1-e^(-k·rise))/(k·rise).
float FogHeightScale(float eyeHeight, float rise) {
    float t = FogShape.x*rise;
    float along = abs(t) > .001 ? (1-exp(-t))/t : 1-.5*t;
    return FogShape.x > 0 ? min(exp(-FogShape.x*(eyeHeight-FogShape.y))*along,4) : 1;
}

// The fog's colour along the unit vector `direction`: the flat colour moved toward the sky's colour that way, plus
// a glow around the sun.
float3 FogRadiance(float3 flat, float3 direction, float3 sunDirection) {
    float3 sky = AmbientCubeAt(direction,FogSky[0],FogSky[1],FogSky[2],FogSky[3],FogSky[4],FogSky[5]);
    return lerp(flat,sky,FogShape.z)+FogSun.rgb*(FogShape.w*pow(saturate(dot(direction,sunDirection)),FogSun.w));
}
#endif
