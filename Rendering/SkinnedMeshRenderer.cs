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

        internal Part(GraphicsDevice gpu, Vertex[] vertices, int[] indices, int[] bones, Matrix[] bind,
            Texture2D map, Vector4 tint, float cutoff)
        {
            boneIndices = bones;
            inverseBindPoses = bind;
            Map = map;
            Tint = tint;
            Cutoff = cutoff;
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var vertex in vertices)
            {
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }
            Bounds = new BoundingBox(min, max);
            try
            {
                Vertices = new VertexBuffer(gpu, Vertex.Layout, vertices.Length, BufferUsage.WriteOnly);
                Vertices.SetData(vertices);
                Indices = new IndexBuffer(gpu, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
                Indices.SetData(indices);
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

    /// <summary>Reads the packed GPU mesh section. Each part owns its GPU buffers and borrows its texture.</summary>
    public Part[] ReadParts(BinaryReader reader, Func<string, long, Texture2D> resolveTexture)
    {
        var parts = new Part[reader.ReadInt32()];
        try
        {
            for (int i = 0; i < parts.Length; i++)
            {
                Texture2D map = resolveTexture(reader.ReadString(), reader.ReadInt64());
                var tint = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                float cutoff = reader.ReadSingle();
                int numBones = reader.ReadInt32();
                var bones = new int[numBones];
                var bind = new Matrix[numBones];
                for (int b = 0; b < numBones; b++) { bones[b] = reader.ReadInt32(); bind[b] = ReadMatrix(reader); }
                var vertices = new Vertex[reader.ReadInt32()];
                var indices = new int[reader.ReadInt32()];
                reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(vertices.AsSpan()));
                reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(indices.AsSpan()));
                parts[i] = new Part(gpu, vertices, indices, bones, bind, map, tint, cutoff);
            }
        }
        catch { foreach (var part in parts) part?.Dispose(); throw; }
        return parts;
    }

    public void Draw(ReadOnlySpan<Part> parts, ReadOnlySpan<Matrix> bones, Matrix world, Matrix viewProjection,
        Vector3 eye, bool inspectFrame)
    {
        shader.CurrentTechnique = inspectFrame ? frameTechnique : surfaceTechnique;
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
