texture Surface, Mask;
sampler2D Samples : register(s0) = sampler_state {
    Texture=<Surface>; MinFilter=Point; MagFilter=Point; MipFilter=None; AddressU=Clamp; AddressV=Clamp;
};
sampler2D MaskSamples : register(s1) = sampler_state {
    Texture=<Mask>; MinFilter=Point; MagFilter=Point; MipFilter=None; AddressU=Clamp; AddressV=Clamp;
};
float2 MaskTexel;
float4 Camera; // inverse dimensions, inverse perspective scales
float2 Settings; // world-space radius, ambient strength

struct Quad { float4 position : POSITION0; float2 uv : TEXCOORD0; };
Quad Project(Quad v) { return v; }
float3 Position(float2 uv, float z) {
    return float3((uv*2-1)*Camera.zw*float2(1,-1),-1)*z;
}
float3 ReadPosition(float2 uv) {
    return Position(uv,tex2Dlod(Samples,float4(uv,0,0)).a);
}
float3 Tangent(float3 center, float3 low, float3 high) {
    // Use the continuous side at silhouettes and a symmetric chord on flat surfaces.
    float3 a = center-low, b = high-center;
    float da = dot(a,a), db = dot(b,b);
    if (da > 4*db) return b;
    if (db > 4*da) return a;
    return a+b;
}
float Obstruction(float2 uv, float2 pixels, float3 p, float3 n) {
    float2 tap = uv+round(pixels)*Camera.xy;
    float3 neighbor = ReadPosition(tap);
    float3 d = neighbor-p;
    float d2 = dot(d,d);
    float valid = all(tap>=0) && all(tap<=1) && neighbor.z<0;
    float elevation = saturate((dot(n,d)-max(.012,-p.z*.001))*rsqrt(max(d2,1e-6))-.04);
    return valid*elevation*saturate(1-d2/(Settings.x*Settings.x));
}
float2 SurfaceCenter(float2 uv) { return (floor(uv/Camera.xy+.001)+.5)*Camera.xy; }
float4 EstimateOcclusion(Quad v) : COLOR0 {
    float2 uv = SurfaceCenter(v.uv);
    float4 surface = tex2D(Samples,uv);
    if (surface.a<=0) return 0;
    float3 p = Position(uv,surface.a);
    float2 dx = float2(Camera.x,0), dy = float2(0,Camera.y);
    float3 x = Tangent(p,ReadPosition(uv-dx),ReadPosition(uv+dx));
    float3 y = Tangent(p,ReadPosition(uv-dy),ReadPosition(uv+dy));
    float3 n = cross(y,x);
    n *= rsqrt(max(dot(n,n),1e-18));
    if (dot(n,p)>0) n=-n;
    float radiusPixels = min(72,Settings.x/(2*Camera.y*Camera.w*surface.a));
    float total = 0;
    // Interleave a fixed set of rotations and radial offsets across adjacent half-resolution pixels.
    float2 pixel = floor(v.uv/MaskTexel);
    float phase = frac(dot(pixel,float2(.7548777,.5698403)));
    float angle = phase*.785398;
    float2 dir = float2(cos(angle),sin(angle));
    [loop] for (int i=0; i<8; i++) {
        float2 ray = dir*radiusPixels;
        float offset = frac(phase+i*.618034)*.25;
        float horizon = max(Obstruction(uv,ray*(.07+offset),p,n),Obstruction(uv,ray*(.36+offset),p,n));
        total += max(horizon,Obstruction(uv,ray*(.69+offset),p,n));
        dir = .70710678*float2(dir.x-dir.y,dir.x+dir.y);
    }
    // Stereographic representation of a view-facing normal, used for surface-aware reconstruction.
    float2 encoded = n.xy/max(.001,1+n.z);
    return float4(saturate(total*(Settings.y/8)),encoded,surface.a);
}

float4 CompositeAmbient(Quad v) : COLOR0 {
    float4 surface = tex2D(Samples,v.uv);
    if (surface.a<=0) return 0;
    float3 receiver = Position(v.uv,surface.a);
    float2 maskPixel = v.uv/MaskTexel-.5;
    float2 home = floor(maskPixel+.5);
    float sum = 0, weights = 0;
    [unroll] for (int y=-1; y<=1; y++) {
        [unroll] for (int x=-1; x<=1; x++) {
            float2 ij = home+float2(x,y);
            float2 uv = clamp((ij+.5)*MaskTexel,.5*MaskTexel,1-.5*MaskTexel);
            float4 sample = tex2Dlod(MaskSamples,float4(uv,0,0));
            float2 enc = sample.yz;
            float e2 = dot(enc,enc);
            float3 n = float3(2*enc,1-e2)/(1+e2);
            float3 delta = receiver-Position(SurfaceCenter(uv),sample.a);
            float spacing = max(.025, surface.a*Camera.y*Camera.w*4);
            float onPlane = saturate(1-abs(dot(delta,n))/spacing);
            float2 tent = saturate(1-abs(ij-maskPixel)/1.6);
            float w = tent.x*tent.y*onPlane*step(.0001,sample.a)*saturate(1-abs(delta.z)/Settings.x);
            sum += w*sample.x;
            weights += w;
        }
    }
    return float4(surface.rgb*sum/max(weights,1e-5),0);
}
technique Estimate {
    pass P0 { VertexShader=compile vs_3_0 Project(); PixelShader=compile ps_3_0 EstimateOcclusion(); }
}
technique Composite {
    pass P0 { VertexShader=compile vs_3_0 Project(); PixelShader=compile ps_3_0 CompositeAmbient(); }
}
