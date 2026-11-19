struct MeshVertex
{
    float4 position : POSITION0;
    float3 normal : NORMAL0;
    float4 tangent : TANGENT0;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
    float4 indirect : COLOR1;
};
struct SurfaceVertex
{
    float4 clip : POSITION0;
    float3 positionWS : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
    float4 tangentWS : TEXCOORD2;
    float2 uv : TEXCOORD3;
    float3 indirect : TEXCOORD4;
    float4 color : COLOR0;
};
float4x4 World;
float4x4 ViewProjection;
float3 EyePosition;

