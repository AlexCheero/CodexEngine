// Temporal anti-aliasing (CodexEngine.Rendering.TemporalAntialiasing). Every frame is drawn a fraction of a pixel
// aside; this pass blends it into the frames before it, each pixel fetched from where its surface was a frame ago.
// The scene holds linear HDR radiance, and in alpha whatever the game keeps there, which is blended alike.
texture Scene, History, Surfaces;
sampler2D Current = sampler_state { Texture=<Scene>; MinFilter=Point; MagFilter=Point; MipFilter=None; AddressU=Clamp; AddressV=Clamp; };
sampler2D Before = sampler_state { Texture=<History>; MinFilter=Linear; MagFilter=Linear; MipFilter=None; AddressU=Clamp; AddressV=Clamp; };
sampler2D Depth = sampler_state { Texture=<Surfaces>; MinFilter=Point; MagFilter=Point; MipFilter=None; AddressU=Clamp; AddressV=Clamp; };
float4 SourceSize;     // texel size, dimensions
float4x4 Reproject;    // (ndc·depth, depth, 1) of this frame to the last frame's clip space
float4 Settings;       // share of the new frame, tolerance in deviations, 1/alpha of screen-fixed surfaces or 0,
                       // how many times brighter than its history a pixel must be to start replacing it

struct ScreenVertex { float4 Position:POSITION0; float2 UV:TEXCOORD0; };
ScreenVertex Project(float4 position:POSITION0, float2 uv:TEXCOORD0) { ScreenVertex v; v.Position=position; v.UV=uv; return v; }

// Frames are blended as the radiance they hold. Weighing bright pixels down first would steady a glint, and dim
// everything that glows as soon as it moves: the bloom that follows this pass needs the light kept whole.
float4 Now(float2 uv) { return tex2Dlod(Current,float4(uv,0,0)); }

// History through a sharp filter (Catmull-Rom, five bilinear fetches): a plain bilinear fetch softens the image a
// little more every frame it is fetched from between texels.
float4 Past(float2 uv) {
    float2 position = uv*SourceSize.zw;
    float2 centre = floor(position-.5)+.5, f = position-centre, f2 = f*f, f3 = f2*f;
    float2 w0 = -.5*f3+f2-.5*f, w1 = 1.5*f3-2.5*f2+1, w2 = -1.5*f3+2*f2+.5*f, w3 = .5*f3-.5*f2;
    float2 w12 = w1+w2;
    float2 t0 = (centre-1)*SourceSize.xy, t12 = (centre+w2/w12)*SourceSize.xy, t3 = (centre+2)*SourceSize.xy;
    float4 sum = tex2Dlod(Before,float4(t12.x,t0.y,0,0))*(w12.x*w0.y)
        + tex2Dlod(Before,float4(t0.x,t12.y,0,0))*(w0.x*w12.y)
        + tex2Dlod(Before,float4(t12.x,t12.y,0,0))*(w12.x*w12.y)
        + tex2Dlod(Before,float4(t3.x,t12.y,0,0))*(w3.x*w12.y)
        + tex2Dlod(Before,float4(t12.x,t3.y,0,0))*(w12.x*w3.y);
    float weight = w12.x*w0.y+w0.x*w12.y+w12.x*w12.y+w3.x*w12.y+w12.x*w3.y;
    return float4(max(sum.rgb/weight,0),sum.a/weight);
}

float4 Blend(ScreenVertex v):COLOR0 {
    float2 uv = v.UV, t = SourceSize.xy;
    float4 centre = Now(uv);
    // What the colours around the pixel allow its history to be: their mean, give or take their spread.
    float4 sum = centre, squares = centre*centre, c;
    c = Now(uv+float2(-t.x,-t.y)); sum += c; squares += c*c;
    c = Now(uv+float2(0,-t.y)); sum += c; squares += c*c;
    c = Now(uv+float2(t.x,-t.y)); sum += c; squares += c*c;
    c = Now(uv+float2(-t.x,0)); sum += c; squares += c*c;
    c = Now(uv+float2(t.x,0)); sum += c; squares += c*c;
    c = Now(uv+float2(-t.x,t.y)); sum += c; squares += c*c;
    c = Now(uv+float2(0,t.y)); sum += c; squares += c*c;
    c = Now(uv+float2(t.x,t.y)); sum += c; squares += c*c;
    float4 mean = sum/9, spread = sqrt(max(squares/9-mean*mean,0))*Settings.y;
    // The nearest surface among the pixel and its four neighbours: an edge then moves with what is in front.
    float depth = tex2Dlod(Depth,float4(uv,0,0)).a;
    depth = min(depth,tex2Dlod(Depth,float4(uv+float2(-t.x,0),0,0)).a);
    depth = min(depth,tex2Dlod(Depth,float4(uv+float2(t.x,0),0,0)).a);
    depth = min(depth,tex2Dlod(Depth,float4(uv+float2(0,-t.y),0,0)).a);
    depth = min(depth,tex2Dlod(Depth,float4(uv+float2(0,t.y),0,0)).a);
    float4 before = mul(float4((uv*float2(2,-2)+float2(-1,1))*depth,depth,1),Reproject);
    float2 old = before.xy/max(before.w,1e-4)*float2(.5,-.5)+.5;
    // A surface fixed to the screen was at the same pixel.
    old = lerp(old,uv,step(.5,centre.a*Settings.z));
    float weight = Settings.x;
    if (before.w <= 0 || any(old < 0) || any(old > 1)) weight = 1;
    float4 history = clamp(Past(old),mean-spread,mean+spread);
    // Something that moves on its own has no motion to follow here, and where it glows its history holds what was
    // behind it a frame ago: blended in, a moving glint or a glowing rim would lose most of its light. A pixel
    // several times brighter than its history takes the new frame instead. Still surfaces stay well below that,
    // as their history already holds their own average.
    float now = max(centre.r,max(centre.g,centre.b)), then = max(history.r,max(history.g,history.b));
    weight = lerp(weight,1,saturate((now/max(then,1e-3)-Settings.w)/(2*Settings.w)));
    return lerp(history,centre,weight);
}
technique Temporal { pass P0 { VertexShader=compile vs_3_0 Project(); PixelShader=compile ps_3_0 Blend(); } }
