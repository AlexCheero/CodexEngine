#ifndef CODEX_BRDF
#define CODEX_BRDF
// A metallic and smoothness BRDF with a GGX highlight cheap enough for forward shading: data setup, the
// direct specular term and the environment term.
// All colours are linear.
struct Brdf {
    float3 diffuse, specular;
    float perceptualRoughness, roughness2, normalization, grazing;
};
Brdf BrdfMetallic(float3 albedo, float metallic, float smoothness) {
    Brdf b;
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
// The direct highlight: multiply by b.specular and the light's radiance (colour * attenuation * N·L).
float SpecularTerm(Brdf b, float3 n, float3 l, float3 v) {
    float3 h = normalize(l+v);
    float nh = saturate(dot(n,h)), lh = saturate(dot(l,h));
    float d = nh*nh*(b.roughness2-1)+1.00001;
    return b.roughness2/((d*d)*max(.1,lh*lh)*b.normalization);
}
// The factor applied to radiance reflected from the surroundings.
float3 EnvironmentSpecular(Brdf b, float nv) {
    float fresnel = pow(1-saturate(nv),4);
    return lerp(b.specular,float3(b.grazing,b.grazing,b.grazing),fresnel)/(b.roughness2+1);
}
// The reflection cubemap level a roughness reads, of six below the top one (EnvironmentCube convolves them to match).
float ReflectionMip(Brdf b) { return b.perceptualRoughness*(1.7-.7*b.perceptualRoughness)*6; }
#endif
