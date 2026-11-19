using System;
using System.IO;
using Foundry.Content;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
namespace CodexEngine.Rendering;

[StructLayout(LayoutKind.Sequential,Pack=4)]
internal struct SurfacePoint : IVertexType
{
    public Vector3 Position,Normal;
    public Vector4 Tangent;
    public Vector2 Uv;
    public Color Color,Indirect;
    internal static readonly VertexDeclaration Layout=new(
        new VertexElement(0,VertexElementFormat.Vector3,VertexElementUsage.Position,0),
        new VertexElement(12,VertexElementFormat.Vector3,VertexElementUsage.Normal,0),
        new VertexElement(24,VertexElementFormat.Vector4,VertexElementUsage.Tangent,0),
        new VertexElement(40,VertexElementFormat.Vector2,VertexElementUsage.TextureCoordinate,0),
        new VertexElement(48,VertexElementFormat.Color,VertexElementUsage.Color,0),
        new VertexElement(52,VertexElementFormat.Color,VertexElementUsage.Color,1));
    VertexDeclaration IVertexType.VertexDeclaration=>Layout;
}

public sealed class StaticMeshRenderer : IDisposable
{
    internal sealed record Span(int Start,int Primitives,Texture2D Map,float Cutoff,MapSlot Normal,MapSlot Mask,
        MapSlot Glow,Vector4 Factors,Vector3 Emission,bool Blend);
    /// <summary>
    /// One material map, shared by every surface that uses it. Until a worker has read the file and
    /// <see cref="StreamMaps"/> has uploaded it, surfaces draw its built-in stand-in, so a model or fighter
    /// seen for the first time never waits on its maps.
    /// </summary>
    public sealed class MapSlot
    {
        internal readonly string Name;
        internal Texture2D Current;
        internal bool Ready;
        internal MapSlot(string name,Texture2D current,bool ready){Name=name;Current=current;Ready=ready;}
    }
    /// <summary>Collects triangles by base texture, cutoff and authored material. Opaque spans come first.</summary>
    sealed class Grouping(StaticMeshRenderer owner,string model)
    {
        readonly Dictionary<(string Texture,float Cutoff,SurfaceMaterial Material),List<int>> groups=new();
        readonly Dictionary<(string,uint),SurfaceMaterial> known=new();
        public void Add(DrawSurface surface,int[] indices,uint[] colors,int firstVertex)
        {
            // A surface merged by the baker can hold several tints of one texture, each its own material.
            List<int> faces=null;uint current=0;
            for(int i=surface.Start;i<surface.Start+surface.Count;i+=3)
            {
                uint color=colors[indices[i]];
                if(faces==null || color!=current)
                {
                    current=color;
                    if(!known.TryGetValue((surface.Texture,color),out var material))
                        known.Add((surface.Texture,color),material=owner.materials.Find(surface.Texture,new Color{PackedValue=color}.ToVector4(),model));
                    var key=(surface.Texture,surface.Cutoff,material);
                    if(!groups.TryGetValue(key,out faces))groups.Add(key,faces=new());
                }
                faces.Add(indices[i]+firstVertex);faces.Add(indices[i+1]+firstVertex);faces.Add(indices[i+2]+firstVertex);
            }
        }
        public (int[] Indices,Span[] Spans) Build()
        {
            var indices=new List<int>();var spans=new List<Span>();
            foreach(bool blend in new[]{false,true})
                foreach(var (key,faces) in groups)
                {
                    var m=key.Material;
                    if(m.Blend!=blend)continue;
                    spans.Add(new(indices.Count,faces.Count/3,owner.Texture(key.Texture),key.Cutoff,
                        owner.Map(m.Normal,FlatNormal),owner.Map(m.Mask,PlainMask),owner.Map(m.Glow,NoGlow),m.Factors,m.Emission,m.Blend));
                    indices.AddRange(faces);
                }
            return (indices.ToArray(),spans.ToArray());
        }
    }
    public sealed class Batch : IDisposable
    {
        internal readonly VertexBuffer Vertices;
        internal readonly IndexBuffer Indices;
        internal readonly Span[] Spans;
        public readonly BoundingSphere Bounds;
        /// <summary>Has surfaces for <see cref="DrawBlended"/>.</summary>
        public bool HasBlended => Spans.Length>0 && Spans[^1].Blend;

        internal Batch(StaticMeshRenderer owner,IEnumerable<(Geometry Geometry,Matrix World)> entries,string model)
        {
            var vertices=new List<SurfacePoint>();
            var min=new Vector3(float.MaxValue);var max=new Vector3(float.MinValue);
            var groups=new Grouping(owner,model);
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
                        Tangent=new(Vector3.Normalize(Vector3.TransformNormal(t,world)),g.Tangents[k+3]),Uv=new(g.Uvs[i*2],g.Uvs[i*2+1]),
                        Color=new Color{PackedValue=g.Colors[i]}});
                }
                foreach(var s in g.Surfaces)groups.Add(s,g.Indices,g.Colors,first);
            }
            var (indices,spans)=groups.Build();
            (Vertices,Indices)=Upload(owner.gpu,vertices.ToArray(),indices);
            Spans=spans;
            Bounds=BoundingSphere.CreateFromBoundingBox(new(min,max));
        }
        internal Batch(StaticMeshRenderer owner,BakedSceneLight light)
        {
            // The baker has already transformed the vertices and merged them by base texture.
            var mesh=light.Mesh;
            var vertices=new SurfacePoint[mesh.Colors.Length];
            var min=new Vector3(float.MaxValue);
            var max=new Vector3(float.MinValue);
            for(int i=0;i<vertices.Length;i++)
            {
                int j=3*i,k=4*i;
                ref var v=ref vertices[i];
                v.Position=new(mesh.Positions[j],mesh.Positions[j+1],mesh.Positions[j+2]);
                v.Normal=new(mesh.Normals[j],mesh.Normals[j+1],mesh.Normals[j+2]);
                v.Tangent=new(mesh.Tangents[k],mesh.Tangents[k+1],mesh.Tangents[k+2],mesh.Tangents[k+3]);
                v.Uv=new(mesh.Uvs[2*i],mesh.Uvs[2*i+1]);
                v.Color.PackedValue=mesh.Colors[i];
                v.Indirect.PackedValue=light.SurfaceLight[i];
                min=Vector3.Min(min,v.Position); max=Vector3.Max(max,v.Position);
            }
            Bounds=BoundingSphere.CreateFromBoundingBox(new(min,max));
            var groups=new Grouping(owner,null);
            foreach(var s in mesh.Surfaces)groups.Add(s,mesh.Indices,mesh.Colors,0);
            var (indices,spans)=groups.Build();
            Spans=spans;
            (Vertices,Indices)=Upload(owner.gpu,vertices,indices);
        }
        static (VertexBuffer,IndexBuffer) Upload(GraphicsDevice gpu,SurfacePoint[] vertices,int[] indices)
        {
            var vb=new VertexBuffer(gpu,SurfacePoint.Layout,vertices.Length,BufferUsage.WriteOnly);
            IndexBuffer ib=null;
            try
            {
                vb.SetData(vertices);
                ib=new IndexBuffer(gpu,IndexElementSize.ThirtyTwoBits,indices.Length,BufferUsage.WriteOnly);
                ib.SetData(indices);
                return (vb,ib);
            }
            catch { vb.Dispose(); ib?.Dispose(); throw; }
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
    // Absent from effects that shade the base texture alone.
    readonly EffectParameter normalMap, maskMap, glowMap, factors, emission;
    readonly SurfaceMaterials materials;
    // Map streaming. The game thread owns the slots; one worker reads the requested files in order.
    readonly Dictionary<string, MapSlot> maps = new();
    readonly List<MapSlot> unready = new();
    readonly ConcurrentQueue<MapSlot> requested = new();
    readonly ConcurrentQueue<(MapSlot Slot, TextureData Data, ExceptionDispatchInfo Error)> arrived = new();
    int reading;
    readonly ConcurrentDictionary<string, Lazy<TextureData>> shared = new();
    readonly BlendState blended = new()
    {
        // Glass leaves the eye distance held in the target's alpha to the surface behind it.
        ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.InverseSourceAlpha,
        AlphaSourceBlend = Blend.Zero, AlphaDestinationBlend = Blend.One
    };

    /// <summary>Built-in maps for a material without its own: no relief, a dielectric mask, no glow.</summary>
    public const string FlatNormal = "#normal", PlainMask = "#mask", NoGlow = "#black";

    /// <summary>
    /// Surface maps stay sharp at grazing angles. Surface effects leave the filter to this state:
    /// FNA reapplies a filter named in an effect on every pass and cannot express anisotropy there.
    /// </summary>
    public static readonly SamplerState SurfaceSampler = new()
    {
        Name = "CodexEngine.SurfaceSampler", Filter = TextureFilter.Anisotropic, MaxAnisotropy = 16,
        AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap
    };

    public Matrix ViewProjection { get; private set; }
    public Effect Material => shader;
    public bool InspectFrame { get; set; }

    public StaticMeshRenderer(GraphicsDevice gpu, string effectPath, string textureDirectory,
        string surfaceTechnique, string frameTechnique, SurfaceMaterials materials = null)
    {
        this.gpu = gpu;
        this.textureDirectory = textureDirectory;
        this.materials = materials ?? new SurfaceMaterials();
        shader = new Effect(gpu, File.ReadAllBytes(effectPath));
        this.surfaceTechnique = shader.Techniques[surfaceTechnique];
        this.frameTechnique = shader.Techniques[frameTechnique];
        world = shader.Parameters["World"];
        viewProjection = shader.Parameters["ViewProjection"];
        eye = shader.Parameters["EyePosition"];
        albedo = shader.Parameters["BaseMap"];
        cutoff = shader.Parameters["AlphaCutoff"];
        normalMap = shader.Parameters["NormalMap"];
        maskMap = shader.Parameters["MaskMap"];
        glowMap = shader.Parameters["GlowMap"];
        factors = shader.Parameters["Material"];
        emission = shader.Parameters["Emission"];

        // The plain mask is full metalness and smoothness for the material's scales, no occlusion and 4% reflectance.
        foreach (var (name, color) in new[] { ("", Color.White), (FlatNormal, new Color(128, 128, 255)),
            (PlainMask, new Color(255, 255, 10, 255)), (NoGlow, Color.Black) })
        {
            var texture = new Texture2D(gpu, 1, 1);
            texture.SetData(new[] { color });
            textures.Add(name, texture);
        }
    }

    public SurfaceMaterials Materials => materials;

    /// <summary>Combine meshes by material in world or model space. The caller owns the resulting GPU buffers.</summary>
    public Batch CreateBatch(IEnumerable<(Geometry Geometry, Matrix World)> meshes) => new(this, meshes, null);
    public Batch CreateBatch(BakedSceneLight light) => new(this,light);

    /// <summary>The file name, kind-id, tells this model's materials from another's that look alike.</summary>
    public Batch LoadModel(string path) => new(this, [(Geometry.Read(path), Matrix.Identity)], Path.GetFileNameWithoutExtension(path));

    public Texture2D Texture(string name)
    {
        if(textures.TryGetValue(name,out var existing))return existing;
        return Texture(ReadTexture(name));
    }

    public sealed class TextureData
    {
        internal string Name;
        internal int Width, Height;
        internal SurfaceFormat Format;
        internal byte[][] Mips;
        internal static readonly TextureData White = new() { Name = "" };
        internal static TextureData BuiltIn(string name) => new() { Name = name };
    }

    // This stage touches only the baked file and can run on an asset worker.
    public TextureData ReadTexture(string name)
    {
        if (name.Length == 0) return TextureData.White;
        if (name[0] == '#') return TextureData.BuiltIn(name);
        using var r = new BinaryReader(File.OpenRead(Path.Combine(textureDirectory, Path.GetFileNameWithoutExtension(name) + ".btex")));
        var data = new TextureData { Name = name, Width = r.ReadInt32(), Height = r.ReadInt32() };
        data.Format = r.ReadInt32() switch { 1 => SurfaceFormat.Dxt1, 3 => SurfaceFormat.Dxt3, 5 => SurfaceFormat.Dxt5, 7 => SurfaceFormat.Bc7EXT,
            _ => throw new InvalidDataException("Invalid BC texture " + name) };
        data.Mips = new byte[r.ReadInt32()][];
        for (int i = 0; i < data.Mips.Length; i++)
        {
            data.Mips[i] = new byte[r.ReadInt32()];
            r.BaseStream.ReadExactly(data.Mips[i]);
        }
        return data;
    }

    /// <summary>
    /// The slot for a material map, on the game thread. A map the bake has not produced keeps its built-in
    /// stand-in; any other is read in the background and arrives through <see cref="StreamMaps"/>.
    /// </summary>
    public MapSlot Map(string name, string standIn)
    {
        if (maps.TryGetValue(name, out var slot)) return slot;
        if (textures.TryGetValue(name, out var existing)) slot = new(name, existing, true);
        else if (name.Length == 0 || name[0] == '#' ||
            !File.Exists(Path.Combine(textureDirectory, Path.GetFileNameWithoutExtension(name) + ".btex")))
            slot = new(name, textures[standIn], true);
        else
        {
            slot = new(name, textures[standIn], false);
            unready.Add(slot);
            requested.Enqueue(slot);
            if (Interlocked.CompareExchange(ref reading, 1, 0) == 0) Task.Run(ReadRequested);
        }
        maps.Add(name, slot);
        return slot;
    }

    void ReadRequested()
    {
        do
        {
            while (requested.TryDequeue(out var slot))
            {
                try { arrived.Enqueue((slot, ReadTexture(slot.Name), null)); }
                catch (Exception e) { arrived.Enqueue((slot, null, ExceptionDispatchInfo.Capture(e))); }
            }
            Volatile.Write(ref reading, 0);
            // Picks up a request queued after the last dequeue but before the flag cleared.
        } while (!requested.IsEmpty && Interlocked.CompareExchange(ref reading, 1, 0) == 0);
    }

    /// <summary>
    /// Uploads maps the worker has read, on the game thread once a frame. Stops once the frame's uploads
    /// reach <paramref name="byteBudget"/>, so one frame takes about one large map at most.
    /// </summary>
    public void StreamMaps(long byteBudget = 4 << 20)
    {
        long spent = 0;
        while (spent < byteBudget && arrived.TryDequeue(out var item))
        {
            if (item.Slot.Ready) continue;
            item.Error?.Throw();
            spent += Resolve(item.Slot, item.Data);
        }
        if (unready.Count > 64) unready.RemoveAll(s => s.Ready);
    }

    /// <summary>Reads and uploads every map still on its stand-in, for loading screens.</summary>
    public void LoadMapsNow()
    {
        foreach (var slot in unready)
            if (!slot.Ready) Resolve(slot, ReadTexture(slot.Name));
        unready.Clear();
    }

    long Resolve(MapSlot slot, TextureData data)
    {
        long bytes = 0;
        if (!textures.ContainsKey(data.Name)) foreach (var mip in data.Mips) bytes += mip.Length;
        slot.Current = Texture(data);
        slot.Ready = true;
        return bytes;
    }

    /// <summary>
    /// <see cref="ReadTexture"/> once per name, shared by every asset worker. The bytes are let go
    /// once <see cref="Texture(TextureData)"/> has uploaded them.
    /// </summary>
    public TextureData ReadShared(string name)
    {
        if (name.Length == 0 || name[0] == '#') return ReadTexture(name);
        return shared.GetOrAdd(name, n => new Lazy<TextureData>(() => ReadTexture(n))).Value;
    }

    /// <summary>Whether a texture is already on the GPU, so <see cref="Texture(TextureData)"/> costs nothing.</summary>
    public bool Uploaded(string name) => textures.ContainsKey(name);

    public Texture2D Texture(TextureData data)
    {
        if (textures.TryGetValue(data.Name, out var existing)) return existing;
        var texture=new Texture2D(gpu,data.Width,data.Height,data.Mips.Length>1,data.Format);
        try{
            for(int i=0;i<data.Mips.Length;i++){var bytes=data.Mips[i];texture.SetData(i,null,bytes,0,bytes.Length);}
        }catch{texture.Dispose();throw;}
        textures.Add(data.Name,texture);
        // Later lookups find the texture by name. Shared reads would otherwise keep the bytes for good.
        data.Mips = null;
        return texture;
    }
    public static Matrix MatrixOf(float[] m)=>new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
    public void Camera(Matrix view,Matrix projection,Vector3 position)
    {
        ViewProjection=view*projection;viewProjection.SetValue(ViewProjection);eye.SetValue(position);
        frustum.Matrix=ViewProjection;
        gpu.DepthStencilState=DepthStencilState.Default;gpu.RasterizerState=RasterizerState.CullNone;gpu.BlendState=BlendState.Opaque;
        gpu.SamplerStates[0]=SurfaceSampler;
    }
    /// <summary>Draws the opaque surfaces. A shadow pass passes <paramref name="everySurface"/> so glass casts too.</summary>
    public void Draw(Batch batch,Matrix transform,EffectTechnique technique = null,bool everySurface = false)
    {
        shader.CurrentTechnique=technique ?? (InspectFrame ? frameTechnique : surfaceTechnique);world.SetValue(transform);
        DrawSpans(batch,false,everySurface);
    }
    /// <summary>Draws the blended surfaces over the finished opaque scene with a technique that returns premultiplied colour.</summary>
    public void DrawBlended(Batch batch,Matrix transform,EffectTechnique technique)
    {
        if(!batch.HasBlended)return;
        var blend=gpu.BlendState;var depth=gpu.DepthStencilState;
        gpu.BlendState=blended;gpu.DepthStencilState=DepthStencilState.DepthRead;
        shader.CurrentTechnique=technique;world.SetValue(transform);
        DrawSpans(batch,true,false);
        gpu.BlendState=blend;gpu.DepthStencilState=depth;
    }
    void DrawSpans(Batch batch,bool blend,bool every)
    {
        gpu.SamplerStates[0]=SurfaceSampler;gpu.SamplerStates[3]=SurfaceSampler;gpu.SamplerStates[4]=SurfaceSampler;gpu.SamplerStates[5]=SurfaceSampler;
        gpu.SetVertexBuffer(batch.Vertices);gpu.Indices=batch.Indices;
        foreach(var span in batch.Spans){
            if(!every && span.Blend!=blend)continue;
            albedo.SetValue(span.Map);cutoff.SetValue(span.Cutoff);
            normalMap?.SetValue(span.Normal.Current);maskMap?.SetValue(span.Mask.Current);glowMap?.SetValue(span.Glow.Current);
            factors?.SetValue(span.Factors);emission?.SetValue(span.Emission);
            shader.CurrentTechnique.Passes[0].Apply();
            gpu.DrawIndexedPrimitives(PrimitiveType.TriangleList,0,0,batch.Vertices.VertexCount,span.Start,span.Primitives);
        }
    }
    public bool Visible(BoundingSphere bounds) => frustum.Intersects(bounds);

    public void Dispose()
    {
        foreach (var texture in textures.Values) texture.Dispose();
        shader.Dispose();
        blended.Dispose();
    }
}
