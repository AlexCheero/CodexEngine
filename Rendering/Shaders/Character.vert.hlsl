float4 Palette[192];
float4 Tint;

float3 TransformBone(float4 value, int bone)
{
    int row = 3 * bone;
    return float3(dot(value, Palette[row]), dot(value, Palette[row+1]), dot(value, Palette[row+2]));
}

SurfaceVertex CharacterVertex(SkinInput v)
{
    int4 i = int4(v.indices);
    float4 n = float4(v.normal, 0);
    float3 position = TransformBone(v.position, i.x) * v.weights.x + TransformBone(v.position, i.y) * v.weights.y +
        TransformBone(v.position, i.z) * v.weights.z + TransformBone(v.position, i.w) * v.weights.w;
    float3 normal = TransformBone(n, i.x) * v.weights.x + TransformBone(n, i.y) * v.weights.y +
        TransformBone(n, i.z) * v.weights.z + TransformBone(n, i.w) * v.weights.w;
    SurfaceVertex output;
    float4 pos=mul(float4(position,1),World);
    output.clip = mul(pos, ViewProjection);
    output.positionWS=pos.xyz;
    output.normalWS = normalize(mul(normal, (float3x3)World));
    output.tangentWS=0;
    output.color=Tint;
    output.indirect=0;
    output.uv = v.uv;
    return output;
}
