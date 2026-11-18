using System;
using System.Buffers.Binary;

namespace CodexNetworking.Transport
{
    public enum PacketType : byte
    {
        None = 0,
        // Handshake packets: prefix + salts, no sequence numbers.
        ConnectRequest = 1,
        Challenge = 2,
        ChallengeResponse = 3,
        Denied = 4,
        // Session packets: prefix + session id + sequence + acks.
        KeepAlive = 5,
        Payload = 6,
        Fragment = 7,
        Disconnect = 8,
    }

    public enum DisconnectReason : byte
    {
        None = 0,
        /// <summary>The local side called Disconnect.</summary>
        LocalRequest = 1,
        /// <summary>The remote side sent a Disconnect packet.</summary>
        RemoteRequest = 2,
        /// <summary>No packet arrived for <see cref="NetConfig.ConnectionTimeout"/> seconds.</summary>
        Timeout = 3,
        /// <summary>The handshake did not complete within <see cref="NetConfig.ConnectTimeout"/> seconds.</summary>
        ConnectTimeout = 4,
        /// <summary>The server has no free client slot.</summary>
        ServerFull = 5,
        /// <summary>The server refused the connection for another reason.</summary>
        Denied = 6,
    }

    /// <summary>Decoded header of a session packet (KeepAlive, Payload, Fragment, Disconnect).</summary>
    public struct SessionHeader
    {
        public PacketType Type;
        public uint SessionId;
        public ushort Sequence;
        public ushort Ack;
        /// <summary>Bit i set means packet (Ack - i) was received; bit 0 is Ack itself.</summary>
        public ulong AckBits;
        public byte FragmentIndex;
        public byte FragmentCount;
    }

    /// <summary>
    /// Wire format, little-endian. See docs/TRANSPORT_PROTOCOL.md. Every packet starts with the 32-bit
    /// protocol id and a type byte; packets with another protocol id are dropped before any parsing.
    /// </summary>
    public static class Protocol
    {
        public const int PrefixSize = 5;
        public const int SessionHeaderSize = PrefixSize + 4 + 2 + 2 + 8;
        public const int FragmentHeaderSize = SessionHeaderSize + 2;
        /// <summary>Connect requests are zero-padded to this size so a request is never smaller than the reply.</summary>
        public const int ConnectRequestSize = 64;
        /// <summary>Connect requests that carry a connect token (prefix, salt, 76-byte token, padding).</summary>
        public const int ConnectRequestWithTokenSize = 128;
        public const int ConnectTokenOffset = PrefixSize + 8;
        public const int ChallengeSize = PrefixSize + 16;
        public const int DeniedSize = PrefixSize + 8 + 1;
        /// <summary>Authentication tag appended to handshake packets in encrypted modes.</summary>
        public const int HandshakeTagSize = Crypto.SessionKeys.TagSize;
        /// <summary>Hard cap from the 64-bit fragment mask; <see cref="NetConfig.MaxFragments"/> may be lower.</summary>
        public const int MaxFragmentCount = 64;

        public static bool TryReadPrefix(ReadOnlySpan<byte> datagram, uint protocolId, out PacketType type)
        {
            type = PacketType.None;
            if (datagram.Length < PrefixSize)
                return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(datagram) != protocolId)
                return false;
            byte raw = datagram[4];
            if (raw < (byte)PacketType.ConnectRequest || raw > (byte)PacketType.Disconnect)
                return false;
            type = (PacketType)raw;
            return true;
        }

        private static void WritePrefix(Span<byte> destination, uint protocolId, PacketType type)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, protocolId);
            destination[4] = (byte)type;
        }

        /// <summary>Writes a connect request; with a token (76 bytes) the datagram is the larger size.</summary>
        public static int WriteConnectRequest(Span<byte> destination, uint protocolId, ulong clientSalt, ReadOnlySpan<byte> token)
        {
            int size = token.Length > 0 ? ConnectRequestWithTokenSize : ConnectRequestSize;
            destination.Slice(0, size).Clear();
            WritePrefix(destination, protocolId, PacketType.ConnectRequest);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(PrefixSize), clientSalt);
            token.CopyTo(destination.Slice(ConnectTokenOffset));
            return size;
        }

        /// <summary>Reads a connect request of the size the host expects; the token slice is empty without a token.</summary>
        public static bool TryReadConnectRequest(ReadOnlySpan<byte> datagram, bool withToken, out ulong clientSalt, out ReadOnlySpan<byte> token)
        {
            clientSalt = 0;
            token = ReadOnlySpan<byte>.Empty;
            if (datagram.Length < (withToken ? ConnectRequestWithTokenSize : ConnectRequestSize))
                return false;
            clientSalt = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(PrefixSize));
            if (withToken)
                token = datagram.Slice(ConnectTokenOffset, Crypto.ConnectToken.Size);
            return true;
        }

        /// <summary>
        /// Appends the tag that authenticates a handshake packet of <paramref name="length"/> bytes
        /// under the given key and purpose; returns the new length.
        /// </summary>
        public static int SealHandshake(Span<byte> packet, int length, ReadOnlySpan<byte> key, byte purpose)
        {
            return length + Crypto.SessionKeys.Seal(key, purpose, 0, 0, packet.Slice(0, length), packet.Slice(length), 0);
        }

        /// <summary>Verifies the tag of a handshake packet whose unsealed form is <paramref name="length"/> bytes.</summary>
        public static bool VerifyHandshake(ReadOnlySpan<byte> datagram, int length, ReadOnlySpan<byte> key, byte purpose)
        {
            if (datagram.Length < length + HandshakeTagSize)
                return false;
            Span<byte> tag = stackalloc byte[HandshakeTagSize];
            datagram.Slice(length, HandshakeTagSize).CopyTo(tag);
            return Crypto.SessionKeys.Open(key, purpose, 0, 0, datagram.Slice(0, length), tag);
        }

        /// <summary>Writes a Challenge or ChallengeResponse packet.</summary>
        public static int WriteChallenge(Span<byte> destination, uint protocolId, PacketType type, ulong clientSalt, ulong serverSalt)
        {
            WritePrefix(destination, protocolId, type);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(PrefixSize), clientSalt);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(PrefixSize + 8), serverSalt);
            return ChallengeSize;
        }

        public static bool TryReadChallenge(ReadOnlySpan<byte> datagram, out ulong clientSalt, out ulong serverSalt)
        {
            clientSalt = 0;
            serverSalt = 0;
            if (datagram.Length < ChallengeSize)
                return false;
            clientSalt = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(PrefixSize));
            serverSalt = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(PrefixSize + 8));
            return true;
        }

        public static int WriteDenied(Span<byte> destination, uint protocolId, ulong clientSalt, DisconnectReason reason)
        {
            WritePrefix(destination, protocolId, PacketType.Denied);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(PrefixSize), clientSalt);
            destination[PrefixSize + 8] = (byte)reason;
            return DeniedSize;
        }

        public static bool TryReadDenied(ReadOnlySpan<byte> datagram, out ulong clientSalt, out DisconnectReason reason)
        {
            clientSalt = 0;
            reason = DisconnectReason.None;
            if (datagram.Length < DeniedSize)
                return false;
            clientSalt = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(PrefixSize));
            reason = (DisconnectReason)datagram[PrefixSize + 8];
            return true;
        }

        /// <summary>Writes a session header and returns its size (fragment headers are two bytes longer).</summary>
        public static int WriteSessionHeader(Span<byte> destination, uint protocolId, in SessionHeader header)
        {
            WritePrefix(destination, protocolId, header.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(5), header.SessionId);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(9), header.Sequence);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(11), header.Ack);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(13), header.AckBits);
            if (header.Type != PacketType.Fragment)
                return SessionHeaderSize;
            destination[21] = header.FragmentIndex;
            destination[22] = header.FragmentCount;
            return FragmentHeaderSize;
        }

        /// <summary>Reads a session header. The prefix must already have been validated.</summary>
        public static bool TryReadSessionHeader(ReadOnlySpan<byte> datagram, out SessionHeader header, out int headerSize)
        {
            header = default;
            headerSize = 0;
            if (datagram.Length < SessionHeaderSize)
                return false;
            header.Type = (PacketType)datagram[4];
            header.SessionId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(5));
            header.Sequence = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(9));
            header.Ack = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(11));
            header.AckBits = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(13));
            if (header.Type != PacketType.Fragment)
            {
                headerSize = SessionHeaderSize;
                return true;
            }
            if (datagram.Length < FragmentHeaderSize)
                return false;
            header.FragmentIndex = datagram[21];
            header.FragmentCount = datagram[22];
            headerSize = FragmentHeaderSize;
            return true;
        }

        /// <summary>32-bit session id mixed from both salts; carried by every session packet.</summary>
        public static uint DeriveSessionId(ulong clientSalt, ulong serverSalt)
        {
            ulong x = clientSalt ^ serverSalt;
            x ^= x >> 33;
            x *= 0xFF51AFD7ED558CCDUL;
            x ^= x >> 33;
            x *= 0xC4CEB9FE1A85EC53UL;
            x ^= x >> 33;
            return (uint)x;
        }
    }
}
