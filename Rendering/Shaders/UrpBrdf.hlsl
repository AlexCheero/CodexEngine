#ifndef CODEX_URP_BRDF
#define CODEX_URP_BRDF
// URP Lit BRDF (metallic workflow) from BRDF.hlsl: data setup, direct specular term and environment BRDF.
// All colours are linear.
struct UrpBrdf {
    float3 diffuse, specular;
    float perceptualRoughness, roughness2, normalization, grazing;
};
UrpBrdf UrpBrdfMetallic(float3 albedo, float metallic, float smoothness) {
    UrpBrdf b;
    float oneMinusReflectivity = .96*(1-metallic);
    b.diffuse = albedo*oneMinusReflectivity;
    b.specular = lerp(float3(.04,.04,.04),albedo,metallic);
    b.perceptualRoughness = 1-smoothness;
    float roughness = max(b.perceptualRoughness*b.perceptualRoughness,.0078125);
    b.roughness2 = max(roughness*roughness,6.103515625e-5);
    b.normalization = roughness*4+2;
    b.grazing = saturate(smoothness+1-oneMinusReflectivity);
    return b;
}
// DirectBRDFSpecular: multiply by b.specular and the light's radiance (colour * attenuation * N·L).
float UrpSpecularTerm(UrpBrdf b, float3 n, float3 l, float3 v) {
    float3 h = normalize(l+v);
    float nh = saturate(dot(n,h)), lh = saturate(dot(l,h));
    float d = nh*nh*(b.roughness2-1)+1.00001;
    return b.roughness2/((d*d)*max(.1,lh*lh)*b.normalization);
}
// EnvironmentBRDFSpecular: the factor applied to the reflected radiance.
float3 UrpEnvironmentSpecular(UrpBrdf b, float nv) {
    float fresnel = pow(1-saturate(nv),4);
    return lerp(b.specular,float3(b.grazing,b.grazing,b.grazing),fresnel)/(b.roughness2+1);
}
// PerceptualRoughnessToMipmapLevel with UNITY_SPECCUBE_LOD_STEPS = 6, relative to the reflection cubemap's top mip.
float UrpReflectionMip(UrpBrdf b) { return b.perceptualRoughness*(1.7-.7*b.perceptualRoughness)*6; }
#endif
