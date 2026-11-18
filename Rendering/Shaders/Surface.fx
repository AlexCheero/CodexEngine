#include "SurfaceInterface.hlsl"
#include "Mesh.vert.hlsl"
#include "Yard.frag.hlsl"
#include "Inspection.frag.hlsl"

#define PAIR(name,vertex,fragment) technique name { pass P0 { VertexShader = compile vs_3_0 vertex(); PixelShader = compile ps_3_0 fragment(); } }
PAIR(StaticYard, StaticVertex, YardFragment)
PAIR(StaticFrame, StaticVertex, FrameFragment)
ShadowVertex StaticCaster(MeshVertex v) { return ProjectCaster(StaticVertex(v)); }
PAIR(StaticShadow, StaticCaster, ShadowDepth)

