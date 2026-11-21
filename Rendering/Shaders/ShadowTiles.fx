// Empties one tile of a shadow atlas: every texel as far as a depth can be, and the depth buffer with it.
// CodexEngine.Rendering.LocalShadowAtlas draws it into the tile's viewport before the tile's casters.
struct TileVertex { float4 position : POSITION0; };
TileVertex Cover(float4 position : POSITION0) { TileVertex v; v.position = float4(position.xy,1,1); return v; }
float4 Empty(TileVertex v) : COLOR0 { return 3.0e38; }
technique EmptyTile { pass P0 { VertexShader = compile vs_3_0 Cover(); PixelShader = compile ps_3_0 Empty(); } }
