using System;
using System.Runtime.InteropServices;

namespace CodexNetworking.Serialization
{
    /// <summary>
    /// Default serializer for any unmanaged component: the full form is the struct's bytes, the
    /// delta form is a change mask with one bit per 32-bit word followed by the changed words only.
    /// Needs no knowledge of the fields, so it works for every component without code generation;
    /// it cannot quantize and it does not translate <see cref="CodexECS.Entity"/> fields, so
    /// components that need either get a dedicated serializer.
    /// </summary>
    public sealed class BlitSerializer<T> : INetSerializer<T> where T : unmanaged
    {
        public static readonly BlitSerializer<T> Default = new BlitSerializer<T>();

        /// <summary>Size of T in bytes.</summary>
        public static readonly int Size = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(new T[1])).Length;

        /// <summary>Number of 32-bit words in the delta mask (the last word may be partial).</summary>
        public static readonly int WordCount = (Size + 3) >> 2;

        public void Write(ref BitWriter writer, in T value, INetEntityMap entities)
        {
            T local = value;
            writer.WriteBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref local, 1)));
        }

        public bool Read(ref BitReader reader, out T value, INetEntityMap entities)
        {
            value = default;
            return reader.ReadBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));
        }

        public void WriteDelta(ref BitWriter writer, in T baseline, in T value, INetEntityMap entities)
        {
            T baselineLocal = baseline;
            T valueLocal = value;
            ReadOnlySpan<byte> before = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref baselineLocal, 1));
            ReadOnlySpan<byte> after = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref valueLocal, 1));
            for (int word = 0; word < WordCount; word++)
                writer.WriteBool(Word(before, word) != Word(after, word));
            for (int word = 0; word < WordCount; word++)
            {
                uint current = Word(after, word);
                if (current != Word(before, word))
                    writer.WriteBits(current, WordBits(word));
            }
        }

        public bool ReadDelta(ref BitReader reader, in T baseline, out T value, INetEntityMap entities)
        {
            value = baseline;
            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
            Span<bool> changed = stackalloc bool[WordCount];
            for (int word = 0; word < WordCount; word++)
            {
                if (!reader.ReadBool(out changed[word]))
                    return false;
            }
            for (int word = 0; word < WordCount; word++)
            {
                if (!changed[word])
                    continue;
                if (!reader.ReadBits(WordBits(word), out uint current))
                    return false;
                SetWord(bytes, word, current);
            }
            return true;
        }

        private static int WordBits(int word)
        {
            int remaining = Size - (word << 2);
            return remaining >= 4 ? 32 : remaining * 8;
        }

        private static uint Word(ReadOnlySpan<byte> bytes, int word)
        {
            int offset = word << 2;
            int count = Math.Min(4, bytes.Length - offset);
            uint result = 0;
            for (int i = 0; i < count; i++)
                result |= (uint)bytes[offset + i] << (i * 8);
            return result;
        }

        private static void SetWord(Span<byte> bytes, int word, uint value)
        {
            int offset = word << 2;
            int count = Math.Min(4, bytes.Length - offset);
            for (int i = 0; i < count; i++)
                bytes[offset + i] = (byte)(value >> (i * 8));
        }
    }
}
