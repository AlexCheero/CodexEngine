using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>Draws a four-weight mesh using the caller's model-space bone transforms.</summary>
public sealed class SkinnedMeshRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Vertex : IVertexType
    {
        public Vector3 Position, Normal;
        public Vector2 Uv;
        public uint Joints;
        public Vector4 Weights;
        public static readonly VertexDeclaration Layout = new(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(32, VertexElementFormat.Byte4, VertexElementUsage.BlendIndices, 0),
            new VertexElement(36, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0));
        public VertexDeclaration VertexDeclaration => Layout;
    }

    public sealed class Part : IDisposable
    {
        internal readonly VertexBuffer Vertices;
        internal readonly IndexBuffer Indices;
        internal readonly Texture2D Map, Normal, Mask, Glow;
        internal readonly Vector4 Tint;
        internal readonly Vector4 Factors;
        internal readonly Vector3 Emission;
        internal readonly float Cutoff;
        internal readonly bool Blend;
        readonly int[] boneIndices;
        readonly Matrix[] inverseBindPoses;
        public BoundingBox Bounds { get; }
        public ReadOnlySpan<int> Bones => boneIndices;
        public ReadOnlySpan<Matrix> Bind => inverseBindPoses;

        internal Part(GraphicsDevice gpu, PartData data, Func<StaticMeshRenderer.TextureData, Texture2D> texture)
        {
            boneIndices = data.Bones;
            inverseBindPoses = data.Bind;
            Map = texture(data.Texture);
            Normal = texture(data.Normal);
            Mask = texture(data.Mask);
            Glow = texture(data.Glow);
            Factors = data.Material.Factors;
            Emission = data.Material.Emission;
            Blend = data.Material.Blend;
            Tint = data.Tint;
            Cutoff = data.Cutoff;
            Bounds = data.Bounds;
            try
            {
                Vertices = new VertexBuffer(gpu, Vertex.Layout, data.Vertices.Length, BufferUsage.WriteOnly);
                Vertices.SetData(data.Vertices);
                Indices = new IndexBuffer(gpu, IndexElementSize.ThirtyTwoBits, data.Indices.Length, BufferUsage.WriteOnly);
                Indices.SetData(data.Indices);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Vertices?.Dispose(); Indices?.Dispose(); }
    }

    readonly GraphicsDevice gpu;
    readonly Effect shader;
    readonly EffectTechnique surfaceTechnique, frameTechnique;
    readonly EffectParameter paletteParameter, worldParameter, viewProjectionParameter, mapParameter, tintParameter, cutoffParameter, eyeParameter;
    // Absent from effects that shade the base texture alone.
    readonly EffectParameter normalParameter, maskParameter, glowParameter, factorsParameter, emissionParameter;
    readonly BlendState blended = new()
    {
        // Glass leaves the eye distance held in the target's alpha to the surface behind it.
        ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.InverseSourceAlpha,
        AlphaSourceBlend = Blend.Zero, AlphaDestinationBlend = Blend.One
    };
    readonly Vector4[] palette = new Vector4[64 * 3];
    public Effect Material => shader;

    public SkinnedMeshRenderer(GraphicsDevice gpu, string effectPath, string surfaceTechnique, string frameTechnique)
    {
        this.gpu = gpu;
        shader = new Effect(gpu, File.ReadAllBytes(effectPath));
        this.surfaceTechnique = shader.Techniques[surfaceTechnique];
        this.frameTechnique = shader.Techniques[frameTechnique];
        paletteParameter = shader.Parameters["Palette"];
        worldParameter = shader.Parameters["World"];
        viewProjectionParameter = shader.Parameters["ViewProjection"];
        mapParameter = shader.Parameters["BaseMap"];
        tintParameter = shader.Parameters["Tint"];
        cutoffParameter = shader.Parameters["AlphaCutoff"];
        eyeParameter = shader.Parameters["EyePosition"];
        normalParameter = shader.Parameters["NormalMap"];
        maskParameter = shader.Parameters["MaskMap"];
        glowParameter = shader.Parameters["GlowMap"];
        factorsParameter = shader.Parameters["Material"];
        emissionParameter = shader.Parameters["Emission"];
    }

    /// <summary>Converts the file's three left-handed affine rows into an FNA row-vector transform.</summary>
    public static Matrix ReadMatrix(BinaryReader reader)
    {
        Span<float> m = stackalloc float[12];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(m));
        return new Matrix(m[0],m[4],-m[8],0, m[1],m[5],-m[9],0, -m[2],-m[6],m[10],0, m[3],m[7],-m[11],1);
    }

    public sealed class PartData
    {
        internal Vertex[] Vertices;
        internal int[] Indices, Bones;
        internal Matrix[] Bind;
        internal StaticMeshRenderer.TextureData Texture;
        internal StaticMeshRenderer.TextureData Normal = StaticMeshRenderer.TextureData.BuiltIn(StaticMeshRenderer.FlatNormal);
        internal StaticMeshRenderer.TextureData Mask = StaticMeshRenderer.TextureData.BuiltIn(StaticMeshRenderer.PlainMask);
        internal StaticMeshRenderer.TextureData Glow = StaticMeshRenderer.TextureData.BuiltIn(StaticMeshRenderer.NoGlow);
        internal SurfaceMaterial Material = SurfaceMaterial.Plain;
        internal Vector4 Tint;
        internal float Cutoff;
        internal BoundingBox Bounds;
    }

    /// <summary>
    /// Decodes parts on any thread. With <paramref name="surfaces"/>, each part also takes the authored
    /// material that shares its base texture and tint, and reads that material's maps.
    /// <paramref name="model"/> names the asset as kind-id for materials that look alike.
    /// </summary>
    public static PartData[] ReadParts(BinaryReader reader, Func<string, long, StaticMeshRenderer.TextureData> readTexture,
        StaticMeshRenderer surfaces = null, string model = null)
    {
        var parts = new PartData[reader.ReadInt32()];
        for (int i = 0; i < parts.Length; i++)
        {
            var data = parts[i] = new PartData();
            data.Texture = readTexture(reader.ReadString(), reader.ReadInt64());
            data.Tint = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (surfaces != null)
            {
                data.Material = surfaces.Materials.Find(data.Texture.Name, data.Tint, model);
                data.Normal = surfaces.ReadMap(data.Material.Normal, StaticMeshRenderer.FlatNormal);
                data.Mask = surfaces.ReadMap(data.Material.Mask, StaticMeshRenderer.PlainMask);
                data.Glow = surfaces.ReadMap(data.Material.Glow, StaticMeshRenderer.NoGlow);
            }
            data.Cutoff = reader.ReadSingle();
            data.Bones = new int[reader.ReadInt32()];
            data.Bind = new Matrix[data.Bones.Length];
            for (int b = 0; b < data.Bones.Length; b++)
            {
                data.Bones[b] = reader.ReadInt32();
                data.Bind[b] = ReadMatrix(reader);
            }
            data.Vertices = new Vertex[reader.ReadInt32()];
            data.Indices = new int[reader.ReadInt32()];
            reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(data.Vertices.AsSpan()));
            reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(data.Indices.AsSpan()));
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var vertex in data.Vertices)
            {
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }
            data.Bounds = new BoundingBox(min, max);
        }
        return parts;
    }

    public Part[] UploadParts(PartData[] data, Func<StaticMeshRenderer.TextureData, Texture2D> texture)
    {
        var parts = new Part[data.Length];
        try
        {
            for (int i = 0; i < parts.Length; i++)
                parts[i] = new Part(gpu, data[i], texture);
        }
        catch { foreach (var part in parts) part?.Dispose(); throw; }
        return parts;
    }

    /// <summary>
    /// Draws every part with <paramref name="technique"/>. With <paramref name="opaqueOnly"/> the blended
    /// parts are left for <see cref="DrawBlended"/>, which must wait until the opaque scene is finished.
    /// </summary>
    public void Draw(ReadOnlySpan<Part> parts, ReadOnlySpan<Matrix> bones, Matrix world, Matrix viewProjection,
        Vector3 eye, bool inspectFrame, EffectTechnique technique = null, bool opaqueOnly = false)
    {
        Begin(technique ?? (inspectFrame ? frameTechnique : surfaceTechnique), world, viewProjection, eye);
        foreach (var part in parts)
            if (!opaqueOnly || !part.Blend) DrawPart(part, bones);
    }

    /// <summary>Draws the blended parts by their alpha, without writing depth, using a technique that returns premultiplied colour.</summary>
    public void DrawBlended(ReadOnlySpan<Part> parts, ReadOnlySpan<Matrix> bones, Matrix world, Matrix viewProjection,
        Vector3 eye, EffectTechnique technique)
    {
        bool any = false;
        foreach (var part in parts) any |= part.Blend;
        if (!any) return;
        var blend = gpu.BlendState;
        var depth = gpu.DepthStencilState;
        gpu.BlendState = blended;
        gpu.DepthStencilState = DepthStencilState.DepthRead;
        Begin(technique, world, viewProjection, eye);
        foreach (var part in parts)
            if (part.Blend) DrawPart(part, bones);
        gpu.BlendState = blend;
        gpu.DepthStencilState = depth;
    }

    void Begin(EffectTechnique technique, Matrix world, Matrix viewProjection, Vector3 eye)
    {
        shader.CurrentTechnique = technique;
        worldParameter.SetValue(world);
        viewProjectionParameter.SetValue(viewProjection);
        eyeParameter.SetValue(eye);
        gpu.SamplerStates[0] = gpu.SamplerStates[3] = gpu.SamplerStates[4] = gpu.SamplerStates[5] = StaticMeshRenderer.SurfaceSampler;
    }

    void DrawPart(Part part, ReadOnlySpan<Matrix> bones)
    {
        for (int k = 0; k < part.Bones.Length; k++)
        {
            var m = part.Bind[k] * bones[part.Bones[k]];
            palette[3*k] = new Vector4(m.M11, m.M21, m.M31, m.M41);
            palette[3*k+1] = new Vector4(m.M12, m.M22, m.M32, m.M42);
            palette[3*k+2] = new Vector4(m.M13, m.M23, m.M33, m.M43);
        }
        paletteParameter.SetValue(palette);
        mapParameter.SetValue(part.Map);
        normalParameter?.SetValue(part.Normal);
        maskParameter?.SetValue(part.Mask);
        glowParameter?.SetValue(part.Glow);
        factorsParameter?.SetValue(part.Factors);
        emissionParameter?.SetValue(part.Emission);
        tintParameter.SetValue(part.Tint);
        cutoffParameter.SetValue(part.Cutoff);
        gpu.SetVertexBuffer(part.Vertices);
        gpu.Indices = part.Indices;
        shader.CurrentTechnique.Passes[0].Apply();
        gpu.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, part.Vertices.VertexCount, 0, part.Indices.IndexCount/3);
    }

    public void Dispose() { shader.Dispose(); blended.Dispose(); }
}
