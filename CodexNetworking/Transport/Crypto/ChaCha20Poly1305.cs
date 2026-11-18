using System;
using System.Buffers.Binary;

namespace CodexNetworking.Transport.Crypto
{
    /// <summary>
    /// AEAD_CHACHA20_POLY1305 (RFC 8439 section 2.8): the plaintext is encrypted in place and a
    /// 16-byte tag over the associated data and the ciphertext is appended by the caller's layout.
    /// </summary>
    public static class ChaCha20Poly1305
    {
        public const int KeySize = ChaCha20.KeySize;
        public const int NonceSize = ChaCha20.NonceSize;
        public const int TagSize = Poly1305.TagSize;

        /// <summary>Encrypts <paramref name="data"/> in place and writes the tag.</summary>
        public static void Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> data, Span<byte> tag)
        {
            ChaCha20.Xor(key, nonce, 1, data);
            Tag(key, nonce, aad, data, tag);
        }

        /// <summary>Verifies the tag over the ciphertext; on success decrypts <paramref name="data"/> in place.</summary>
        public static bool Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> data, ReadOnlySpan<byte> tag)
        {
            Span<byte> expected = stackalloc byte[TagSize];
            Tag(key, nonce, aad, data, expected);
            if (!Poly1305.TagsEqual(expected, tag))
                return false;
            ChaCha20.Xor(key, nonce, 1, data);
            return true;
        }

        private static void Tag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ciphertext, Span<byte> tag)
        {
            Span<byte> block = stackalloc byte[ChaCha20.BlockSize];
            ChaCha20.Block(key, nonce, 0, block);
            Span<byte> scratch = stackalloc byte[16];
            var mac = new Poly1305(block.Slice(0, Poly1305.KeySize), scratch);
            mac.Update(aad);
            mac.PadToBlock();
            mac.Update(ciphertext);
            mac.PadToBlock();
            Span<byte> lengths = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(lengths, (ulong)aad.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(lengths.Slice(8), (ulong)ciphertext.Length);
            mac.Update(lengths);
            mac.Finish(tag);
            block.Clear();
        }
    }
}
