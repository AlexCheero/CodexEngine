using System;
using System.Buffers.Binary;

namespace CodexNetworking.Transport.Crypto
{
    /// <summary>
    /// What a backend hands a client so it can connect to a host in <see cref="EncryptionMode.ConnectToken"/>
    /// mode: the sealed token for the host (only the host can read it) plus, in the clear for the
    /// client alone, the key material the session keys derive from. Created by
    /// <see cref="ConnectTokenFactory.Create"/> on the backend, which shares the token key with the
    /// host; transported to the client over the backend's own secure channel (https or similar).
    /// </summary>
    public sealed class ConnectToken
    {
        public const int NonceSize = ChaCha20Poly1305.NonceSize;
        public const int SecretSize = 8 + 8 + SessionKeys.KeySize;
        public const int Size = NonceSize + SecretSize + ChaCha20Poly1305.TagSize;

        /// <summary>The sealed token, sent verbatim in the connect request.</summary>
        public readonly byte[] Bytes = new byte[Size];

        /// <summary>The per-client secret the session keys derive from; the client keeps it, the host reads it from the token.</summary>
        public readonly byte[] KeyMaterial = new byte[SessionKeys.KeySize];

        /// <summary>Opaque id the backend chose for the client; the host reports it through <see cref="NetHost.GetClientId"/>.</summary>
        public ulong ClientId;

        /// <summary>Unix seconds after which the host rejects the token.</summary>
        public long ExpiresAt;
    }

    /// <summary>Issues and opens connect tokens. The token key is the backend's secret shared with the host.</summary>
    public static class ConnectTokenFactory
    {
        /// <summary>Backend side: seals (expiry, client id, fresh key material) under the token key, bound to the protocol id.</summary>
        public static ConnectToken Create(ReadOnlySpan<byte> tokenKey, uint protocolId, ulong clientId, long expiresAt, IRandomSource random)
        {
            if (tokenKey.Length != SessionKeys.KeySize)
                throw new ArgumentException("token key must be 32 bytes", nameof(tokenKey));
            var token = new ConnectToken { ClientId = clientId, ExpiresAt = expiresAt };
            SessionKeys.FillRandom(random, token.KeyMaterial);
            Span<byte> bytes = token.Bytes;
            SessionKeys.FillRandom(random, bytes.Slice(0, ConnectToken.NonceSize));
            Span<byte> secret = bytes.Slice(ConnectToken.NonceSize, ConnectToken.SecretSize);
            BinaryPrimitives.WriteInt64LittleEndian(secret, expiresAt);
            BinaryPrimitives.WriteUInt64LittleEndian(secret.Slice(8), clientId);
            token.KeyMaterial.CopyTo(secret.Slice(16));
            Span<byte> aad = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(aad, protocolId);
            ChaCha20Poly1305.Seal(tokenKey, bytes.Slice(0, ConnectToken.NonceSize), aad, secret, bytes.Slice(ConnectToken.NonceSize + ConnectToken.SecretSize));
            return token;
        }

        /// <summary>
        /// Host side: verifies and reads a token without allocating. The caller checks the expiry.
        /// <paramref name="scratch"/> receives the decrypted secret and must hold <see cref="ConnectToken.SecretSize"/> bytes.
        /// </summary>
        public static bool TryOpen(ReadOnlySpan<byte> tokenKey, uint protocolId, ReadOnlySpan<byte> token, Span<byte> scratch,
            out long expiresAt, out ulong clientId, Span<byte> keyMaterial)
        {
            expiresAt = 0;
            clientId = 0;
            if (token.Length < ConnectToken.Size || scratch.Length < ConnectToken.SecretSize || keyMaterial.Length < SessionKeys.KeySize)
                return false;
            Span<byte> secret = scratch.Slice(0, ConnectToken.SecretSize);
            token.Slice(ConnectToken.NonceSize, ConnectToken.SecretSize).CopyTo(secret);
            Span<byte> aad = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(aad, protocolId);
            if (!ChaCha20Poly1305.Open(tokenKey, token.Slice(0, ConnectToken.NonceSize), aad, secret, token.Slice(ConnectToken.NonceSize + ConnectToken.SecretSize, ChaCha20Poly1305.TagSize)))
                return false;
            expiresAt = BinaryPrimitives.ReadInt64LittleEndian(secret);
            clientId = BinaryPrimitives.ReadUInt64LittleEndian(secret.Slice(8));
            secret.Slice(16, SessionKeys.KeySize).CopyTo(keyMaterial);
            secret.Clear();
            return true;
        }
    }

    /// <summary>
    /// Host side: remembers the tokens accepted recently (by nonce) so a captured token cannot be
    /// used twice while it is still valid. Fixed size; the oldest entry is replaced.
    /// </summary>
    internal sealed class TokenReplayMemory
    {
        private readonly ulong[] _low;
        private readonly uint[] _high;
        private readonly long[] _expiry;
        private int _next;

        public TokenReplayMemory(int capacity)
        {
            _low = new ulong[capacity];
            _high = new uint[capacity];
            _expiry = new long[capacity];
        }

        /// <summary>True when the token was already accepted and has not expired; otherwise records it.</summary>
        public bool SeenOrRemember(ReadOnlySpan<byte> token, long expiresAt, long now)
        {
            ulong low = BinaryPrimitives.ReadUInt64LittleEndian(token);
            uint high = BinaryPrimitives.ReadUInt32LittleEndian(token.Slice(8));
            for (int i = 0; i < _low.Length; i++)
            {
                if (_expiry[i] > now && _low[i] == low && _high[i] == high)
                    return true;
            }
            _low[_next] = low;
            _high[_next] = high;
            _expiry[_next] = expiresAt;
            _next = (_next + 1) % _low.Length;
            return false;
        }
    }
}
