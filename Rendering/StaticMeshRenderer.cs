using System;
using System.IO;
using Foundry.Content;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Runtime.InteropServices;
namespace CodexEngine.Rendering;

[StructLayout(LayoutKind.Sequential,Pack=4)]
internal struct SurfacePoint : IVertexType
{
    public Vector3 Position,Normal;
    public Vector4 Tangent;
    public Vector2 Uv;
    public Color Color;
    internal static readonly VertexDeclaration Layout=new(
        new VertexElement(0,VertexElementFormat.Vector3,VertexElementUsage.Position,0),
        new VertexElement(12,VertexElementFormat.Vector3,VertexElementUsage.Normal,0),
        new VertexElement(24,VertexElementFormat.Vector4,VertexElementUsage.Tangent,0),
        new VertexElement(40,VertexElementFormat.Vector2,VertexElementUsage.TextureCoordinate,0),
        new VertexElement(48,VertexElementFormat.Color,VertexElementUsage.Color,0));
    VertexDeclaration IVertexType.VertexDeclaration=>Layout;
}

public sealed class StaticMeshRenderer : IDisposable
{
    internal sealed record Span(int Start,int Primitives,Texture2D Map,float Cutoff);
    public sealed class Batch : IDisposable
    {
        internal readonly VertexBuffer Vertices;
        internal readonly IndexBuffer Indices;
        internal readonly Span[] Spans;
        public readonly BoundingSphere Bounds;

        internal Batch(StaticMeshRenderer owner,IEnumerable<(Geometry Geometry,Matrix World)> entries)
        {
            var vertices=new List<SurfacePoint>();
            var min=new Vector3(float.MaxValue);var max=new Vector3(float.MinValue);
            var groups=new Dictionary<(string,float),List<int>>();
            foreach(var (g,world) in entries)
            {
                var normal=Matrix.Transpose(Matrix.Invert(world));
                int first=vertices.Count;
                for(int i=0;i<g.Colors.Length;i++)
                {
                    int j=i*3,k=i*4;
                    Vector3 p=new(g.Positions[j],g.Positions[j+1],g.Positions[j+2]);
                    Vector3 n=new(g.Normals[j],g.Normals[j+1],g.Normals[j+2]);
                    Vector3 t=new(g.Tangents[k],g.Tangents[k+1],g.Tangents[k+2]);
                    p=Vector3.Transform(p,world);min=Vector3.Min(min,p);max=Vector3.Max(max,p);
                    vertices.Add(new SurfacePoint{Position=p,Normal=Vector3.Normalize(Vector3.TransformNormal(n,normal)),
                        Tangent=new(Vector3.Normalize(Vector3.TransformNormal(t,world)),g.Tangents[k+3]),Uv=new(g.Uvs[i*2],g.Uvs[i*2+1]),Color=new Color{PackedValue=g.Colors[i]}});
                }
                foreach(var s in g.Surfaces)
                {
                    var key=(s.Texture,s.Cutoff);
                    if(!groups.TryGetValue(key,out var faces))groups.Add(key,faces=new());
                    for(int i=s.Start;i<s.Start+s.Count;i++)faces.Add(g.Indices[i]+first);
                }
            }
            var indices=new List<int>();var spans=new List<Span>();
            foreach(var (material,faces) in groups)
            {
                spans.Add(new(indices.Count,faces.Count/3,owner.Texture(material.Item1),material.Item2));
                indices.AddRange(faces);
            }
            try
            {
                Vertices = new VertexBuffer(owner.gpu, SurfacePoint.Layout, vertices.Count, BufferUsage.WriteOnly);
                Vertices.SetData(vertices.ToArray());
                Indices = new IndexBuffer(owner.gpu, IndexElementSize.ThirtyTwoBits, indices.Count, BufferUsage.WriteOnly);
                Indices.SetData(indices.ToArray());
            }
            catch { Dispose(); throw; }
            Spans=spans.ToArray();
            Bounds=BoundingSphere.CreateFromBoundingBox(new(min,max));
        }
        public void Dispose(){Vertices?.Dispose();Indices?.Dispose();}
    }

    readonly GraphicsDevice gpu;
    readonly BoundingFrustum frustum = new(Matrix.Identity);
    readonly string textureDirectory;
    readonly Effect shader;
    readonly EffectTechnique surfaceTechnique, frameTechnique;
    readonly Dictionary<string, Texture2D> textures = new();
    readonly EffectParameter world, viewProjection, eye, albedo, cutoff;

    public Matrix ViewProjection { get; private set; }
    public bool InspectFrame { get; set; }

    public StaticMeshRenderer(GraphicsDevice gpu, string effectPath, string textureDirectory,
        string surfaceTechnique, string frameTechnique)
    {
        this.gpu = gpu;
        this.textureDirectory = textureDirectory;
        shader = new Effect(gpu, File.ReadAllBytes(effectPath));
        this.surfaceTechnique = shader.Techniques[surfaceTechnique];
        this.frameTechnique = shader.Techniques[frameTechnique];
        world = shader.Parameters["World"];
        viewProjection = shader.Parameters["ViewProjection"];
        eye = shader.Parameters["EyePosition"];
        albedo = shader.Parameters["BaseMap"];
        cutoff = shader.Parameters["AlphaCutoff"];

        var white = new Texture2D(gpu, 1, 1);
        white.SetData(new[] { Color.White });
        textures.Add("", white);
    }

    /// <summary>Combine meshes by material in world or model space. The caller owns the resulting GPU buffers.</summary>
    public Batch CreateBatch(IEnumerable<(Geometry Geometry, Matrix World)> meshes) => new(this, meshes);

    public Batch LoadModel(string path) => CreateBatch([(Geometry.Read(path), Matrix.Identity)]);

    public Texture2D Texture(string name)
    {
        if(textures.TryGetValue(name,out var existing))return existing;
        using var r=new BinaryReader(File.OpenRead(Path.Combine(textureDirectory,Path.GetFileNameWithoutExtension(name)+".btex")));
        int w=r.ReadInt32(),h=r.ReadInt32(),block=r.ReadInt32(),levels=r.ReadInt32();
        var format=block switch{1=>SurfaceFormat.Dxt1,3=>SurfaceFormat.Dxt3,5=>SurfaceFormat.Dxt5,_=>throw new InvalidDataException("Invalid BC texture "+name)};
        var texture=new Texture2D(gpu,w,h,levels>1,format);
        try{
            for(int i=0;i<levels;i++){byte[] bytes=r.ReadBytes(r.ReadInt32());texture.SetData(i,null,bytes,0,bytes.Length);}
        }catch{texture.Dispose();throw;}
        textures.Add(name,texture);return texture;
    }
    public static Matrix MatrixOf(float[] m)=>new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
    public void Camera(Matrix view,Matrix projection,Vector3 position)
    {
        ViewProjection=view*projection;viewProjection.SetValue(ViewProjection);eye.SetValue(position);
        frustum.Matrix=ViewProjection;
        gpu.DepthStencilState=DepthStencilState.Default;gpu.RasterizerState=RasterizerState.CullNone;gpu.BlendState=BlendState.Opaque;
        gpu.SamplerStates[0]=SamplerState.AnisotropicWrap;
    }
    public void Draw(Batch batch,Matrix transform)
    {
        shader.CurrentTechnique=InspectFrame ? frameTechnique : surfaceTechnique;world.SetValue(transform);
        gpu.SetVertexBuffer(batch.Vertices);gpu.Indices=batch.Indices;
        foreach(var span in batch.Spans){
            albedo.SetValue(span.Map);cutoff.SetValue(span.Cutoff);
            shader.CurrentTechnique.Passes[0].Apply();
            gpu.DrawIndexedPrimitives(PrimitiveType.TriangleList,0,0,batch.Vertices.VertexCount,span.Start,span.Primitives);
        }
    }
    public bool Visible(BoundingSphere bounds) => frustum.Intersects(bounds);

    public void Dispose()
    {
        foreach (var texture in textures.Values) texture.Dispose();
        shader.Dispose();
    }
}
