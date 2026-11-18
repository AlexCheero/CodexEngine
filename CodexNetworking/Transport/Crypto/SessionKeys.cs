using System;
using System.Buffers.Binary;

namespace CodexNetworking.Transport.Crypto
{
    public enum EncryptionMode : byte
    {
        /// <summary>Plain packets; the protocol as documented without tags.</summary>
        None = 0,
        /// <summary>Both ends hold the same 32-byte key (<see cref="NetConfig.PreSharedKey"/>); every session derives its own keys from it.</summary>
        PreSharedKey = 1,
        /// <summary>
        /// The host holds a key shared with a backend (<see cref="NetConfig.TokenKey"/>); clients
        /// present a <see cref="ConnectToken"/> the backend issued, which carries the per-client
        /// secret the session keys derive from.
        /// </summary>
        ConnectToken = 2,
    }

    /// <summary>
    /// Key schedule and nonce layout. Session keys come from the master secret (the pre-shared key
    /// or a token's key material) and both handshake salts: one ChaCha20 keystream block keyed with
    /// the secret, nonce = clientSalt ‖ low half of serverSalt, counter = high half of serverSalt,
    /// gives the client-to-server key (first 32 bytes) and the server-to-client key (next 32).
    /// Nonces never repeat under a key: a purpose byte, the fragment index, and the 64-bit packet
    /// counter whose low 16 bits are the sequence on the wire.
    /// </summary>
    public static class SessionKeys
    {
        public const int KeySize = ChaCha20.KeySize;
        public const int TagSize = ChaCha20Poly1305.TagSize;
        public const int NonceSize = ChaCha20.NonceSize;

        public const byte PurposeChallenge = 0;
        public const byte PurposeChallengeResponse = 1;
        public const byte PurposeDenied = 2;
        public const byte PurposeSession = 3;

        public static void Derive(ReadOnlySpan<byte> secret, ulong clientSalt, ulong serverSalt, Span<byte> clientToServer, Span<byte> serverToClient)
        {
            Span<byte> nonce = stackalloc byte[NonceSize];
            BinaryPrimitives.WriteUInt64LittleEndian(nonce, clientSalt);
            BinaryPrimitives.WriteUInt32LittleEndian(nonce.Slice(8), (uint)serverSalt);
            Span<byte> block = stackalloc byte[ChaCha20.BlockSize];
            ChaCha20.Block(secret, nonce, (uint)(serverSalt >> 32), block);
            block.Slice(0, KeySize).CopyTo(clientToServer);
            block.Slice(KeySize, KeySize).CopyTo(serverToClient);
            block.Clear();
        }

        public static void WriteNonce(Span<byte> nonce, byte purpose, byte fragmentIndex, ulong counter)
        {
            nonce[0] = purpose;
            nonce[1] = fragmentIndex;
            nonce[2] = 0;
            nonce[3] = 0;
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4), counter);
        }

        /// <summary>
        /// The 64-bit packet counter a 16-bit wire sequence most likely stands for, given the highest
        /// counter seen so far: the candidate nearest to it. A wrong guess only fails the tag.
        /// </summary>
        public static ulong ExtendSequence(ushort sequence, ulong highestSeen, bool anySeen)
        {
            if (!anySeen)
                return sequence;
            ulong candidate = (highestSeen & ~0xFFFFUL) | sequence;
            if (candidate > highestSeen + 0x8000)
                candidate -= 0x10000;
            else if (candidate + 0x8000 < highestSeen && candidate + 0x10000 >= highestSeen - 0x8000)
                candidate += 0x10000;
            return candidate;
        }

        /// <summary>Seals <paramref name="data"/> in place with the tag appended right after it; returns the sealed length.</summary>
        public static int Seal(ReadOnlySpan<byte> key, byte purpose, byte fragmentIndex, ulong counter, ReadOnlySpan<byte> aad, Span<byte> buffer, int dataLength)
        {
            Span<byte> nonce = stackalloc byte[NonceSize];
            WriteNonce(nonce, purpose, fragmentIndex, counter);
            ChaCha20Poly1305.Seal(key, nonce, aad, buffer.Slice(0, dataLength), buffer.Slice(dataLength, TagSize));
            return dataLength + TagSize;
        }

        /// <summary>Opens a sealed buffer (data followed by the tag) in place; false when the tag does not verify.</summary>
        public static bool Open(ReadOnlySpan<byte> key, byte purpose, byte fragmentIndex, ulong counter, ReadOnlySpan<byte> aad, Span<byte> sealedData)
        {
            if (sealedData.Length < TagSize)
                return false;
            Span<byte> nonce = stackalloc byte[NonceSize];
            WriteNonce(nonce, purpose, fragmentIndex, counter);
            int dataLength = sealedData.Length - TagSize;
            return ChaCha20Poly1305.Open(key, nonce, aad, sealedData.Slice(0, dataLength), sealedData.Slice(dataLength, TagSize));
        }

        public static void FillRandom(IRandomSource random, Span<byte> destination)
        {
            int offset = 0;
            while (offset < destination.Length)
            {
                ulong value = random.NextUInt64();
                int take = Math.Min(8, destination.Length - offset);
                for (int i = 0; i < take; i++)
                    destination[offset + i] = (byte)(value >> (i * 8));
                offset += take;
            }
        }
    }
}
