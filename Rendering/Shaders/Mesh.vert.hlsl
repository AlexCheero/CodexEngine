SurfaceVertex ProjectMesh(MeshVertex input)
{
    SurfaceVertex output;
    float4 world = mul(input.position, World);
    output.clip = mul(world, ViewProjection);
    output.positionWS = world.xyz;
    output.normalWS = normalize(mul(input.normal, (float3x3)World));
    output.tangentWS = float4(normalize(mul(input.tangent.xyz,(float3x3)World)),input.tangent.w);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}
SurfaceVertex StaticVertex(MeshVertex input) { return ProjectMesh(input); }

