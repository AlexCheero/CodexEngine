using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// Writes back-buffer frames as PNG files without stalling the game loop on encoding: the read-back happens on the
/// calling thread, compression on worker threads (at most <c>pending</c> frames in flight). Dispose waits for all writes.
/// </summary>
public sealed class FrameRecorder : IDisposable
{
    private readonly SemaphoreSlim slots;
    private readonly List<Task> writes = [];
    private readonly Queue<byte[]> buffers = new();
    public FrameRecorder(int pending = 8) => slots = new SemaphoreSlim(pending, pending);

    public void Capture(GraphicsDevice device, string path)
    {
        int width = device.PresentationParameters.BackBufferWidth, height = device.PresentationParameters.BackBufferHeight;
        slots.Wait();
        byte[] pixels;
        lock (buffers) pixels = buffers.Count > 0 && buffers.Peek().Length == width * height * 4 ? buffers.Dequeue() : new byte[width * height * 4];
        device.GetBackBufferData(pixels);
        writes.RemoveAll(t => t.IsCompleted);
        writes.Add(Task.Run(() =>
        {
            try { File.WriteAllBytes(path, EncodePng(pixels, width, height)); }
            finally { lock (buffers) buffers.Enqueue(pixels); slots.Release(); }
        }));
    }

    /// <summary>RGBA8 pixels to an opaque RGB PNG (alpha dropped, Sub filter, fast deflate).</summary>
    public static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        var raw = new byte[(width * 3 + 1) * height];
        for (int y = 0, o = 0; y < height; y++)
        {
            raw[o++] = 1; // Sub: each byte minus the same channel of the previous pixel
            int row = y * width * 4;
            for (int x = 0; x < width; x++, o += 3)
                for (int c = 0; c < 3; c++)
                    raw[o + c] = (byte)(rgba[row + x * 4 + c] - (x > 0 ? rgba[row + x * 4 - 4 + c] : 0));
        }
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        Chunk(png, "IHDR", header);
        using (var data = new MemoryStream())
        {
            using (var z = new ZLibStream(data, CompressionLevel.Fastest, true)) z.Write(raw);
            Chunk(png, "IDAT", data.ToArray());
        }
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length); s.Write(word);
        var body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
        data.CopyTo(body, 4); s.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc(body)); s.Write(word);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
    private static uint Crc(byte[] bytes)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in bytes) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    public void Dispose()
    {
        Task.WaitAll([.. writes]);
        slots.Dispose();
    }
}
