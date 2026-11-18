float4 FrameFragment(SurfaceVertex v) : COLOR0
{
    float3 n=normalize(v.normalWS);
    float3 dx=ddx(v.positionWS),dy=ddy(v.positionWS);
    float2 u=ddx(v.uv),w=ddy(v.uv);
    float3 reconstructed=dx*w.y-dy*u.y;
    float3 t=v.tangentWS.w==0 ? reconstructed : v.tangentWS.xyz;
    t*=rsqrt(max(0.0000001,dot(t,t)));
    float3 b=cross(n,t)*(v.tangentWS.w==0 ? sign(u.x*w.y-u.y*w.x) : v.tangentWS.w);
    float grid=step(0.5,frac((floor(v.uv.x*8)+floor(v.uv.y*8))*0.5));
    return float4(0.25 + 0.55*abs(n) + 0.12*abs(b)*grid,1);
}
