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
        internal readonly Texture2D Map;
        internal readonly Vector4 Tint;
        internal readonly float Cutoff;
        readonly int[] boneIndices;
        readonly Matrix[] inverseBindPoses;
        public BoundingBox Bounds { get; }
        public ReadOnlySpan<int> Bones => boneIndices;
        public ReadOnlySpan<Matrix> Bind => inverseBindPoses;

        internal Part(GraphicsDevice gpu, PartData data, Texture2D map)
        {
            boneIndices = data.Bones;
            inverseBindPoses = data.Bind;
            Map = map;
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
        internal Vector4 Tint;
        internal float Cutoff;
        internal BoundingBox Bounds;
    }

    public static PartData[] ReadParts(BinaryReader reader, Func<string, long, StaticMeshRenderer.TextureData> readTexture)
    {
        var parts = new PartData[reader.ReadInt32()];
        for (int i = 0; i < parts.Length; i++)
        {
            var data = parts[i] = new PartData();
            data.Texture = readTexture(reader.ReadString(), reader.ReadInt64());
            data.Tint = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
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
                parts[i] = new Part(gpu, data[i], texture(data[i].Texture));
        }
        catch { foreach (var part in parts) part?.Dispose(); throw; }
        return parts;
    }

    public void Draw(ReadOnlySpan<Part> parts, ReadOnlySpan<Matrix> bones, Matrix world, Matrix viewProjection,
        Vector3 eye, bool inspectFrame, EffectTechnique technique = null)
    {
        shader.CurrentTechnique = technique ?? (inspectFrame ? frameTechnique : surfaceTechnique);
        worldParameter.SetValue(world);
        viewProjectionParameter.SetValue(viewProjection);
        eyeParameter.SetValue(eye);
        foreach (var part in parts)
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
            tintParameter.SetValue(part.Tint);
            cutoffParameter.SetValue(part.Cutoff);
            gpu.SetVertexBuffer(part.Vertices);
            gpu.Indices = part.Indices;
            shader.CurrentTechnique.Passes[0].Apply();
            gpu.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, part.Vertices.VertexCount, 0, part.Indices.IndexCount/3);
        }
    }

    public void Dispose() => shader.Dispose();
}
