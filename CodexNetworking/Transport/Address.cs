using System;
using System.Buffers.Binary;

namespace CodexNetworking.Transport
{
    public enum AddressFamily : byte
    {
        None = 0,
        IPv4 = 1,
        IPv6 = 2,
    }

    /// <summary>
    /// Engine- and socket-API-agnostic endpoint address. The 16 address bytes are stored big-endian
    /// in two 64-bit words; IPv4 uses the low 32 bits of <see cref="Lo"/>. Value type, no allocations.
    /// </summary>
    public readonly struct Address : IEquatable<Address>
    {
        public readonly ulong Hi;
        public readonly ulong Lo;
        public readonly ushort Port;
        public readonly AddressFamily Family;

        public static readonly Address None = default;

        public Address(AddressFamily family, ulong hi, ulong lo, ushort port)
        {
            Family = family;
            Hi = hi;
            Lo = lo;
            Port = port;
        }

        public bool IsNone => Family == AddressFamily.None;

        public static Address IPv4(byte a, byte b, byte c, byte d, ushort port)
        {
            ulong lo = ((ulong)a << 24) | ((ulong)b << 16) | ((ulong)c << 8) | d;
            return new Address(AddressFamily.IPv4, 0, lo, port);
        }

        /// <summary>IPv4 from 4 big-endian bytes, or IPv6 from 16 big-endian bytes.</summary>
        public static Address FromBytes(ReadOnlySpan<byte> bytes, ushort port)
        {
            if (bytes.Length == 4)
                return new Address(AddressFamily.IPv4, 0, BinaryPrimitives.ReadUInt32BigEndian(bytes), port);
            if (bytes.Length == 16)
                return new Address(AddressFamily.IPv6,
                    BinaryPrimitives.ReadUInt64BigEndian(bytes),
                    BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8)), port);
            throw new ArgumentException("address must be 4 or 16 bytes", nameof(bytes));
        }

        public static Address Loopback(ushort port) => IPv4(127, 0, 0, 1, port);
        public static Address Any(ushort port) => IPv4(0, 0, 0, 0, port);

        public Address WithPort(ushort port) => new Address(Family, Hi, Lo, port);

        /// <summary>Number of address bytes for this family: 4, 16 or 0.</summary>
        public int ByteLength => Family == AddressFamily.IPv4 ? 4 : Family == AddressFamily.IPv6 ? 16 : 0;

        /// <summary>Writes the address bytes big-endian and returns the count written.</summary>
        public int WriteBytes(Span<byte> destination)
        {
            switch (Family)
            {
                case AddressFamily.IPv4:
                    BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)Lo);
                    return 4;
                case AddressFamily.IPv6:
                    BinaryPrimitives.WriteUInt64BigEndian(destination, Hi);
                    BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8), Lo);
                    return 16;
                default:
                    return 0;
            }
        }

        /// <summary>Parses dotted IPv4 only. IPv6 text parsing is left to the socket implementation.</summary>
        public static bool TryParseIPv4(string text, ushort port, out Address address)
        {
            address = None;
            if (string.IsNullOrEmpty(text))
                return false;
            uint packed = 0;
            int part = 0;
            int value = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '0' && c <= '9')
                {
                    value = value < 0 ? c - '0' : value * 10 + (c - '0');
                    if (value > 255)
                        return false;
                }
                else if (c == '.')
                {
                    if (value < 0 || part == 3)
                        return false;
                    packed = (packed << 8) | (uint)value;
                    value = -1;
                    part++;
                }
                else
                {
                    return false;
                }
            }
            if (part != 3 || value < 0)
                return false;
            packed = (packed << 8) | (uint)value;
            address = new Address(AddressFamily.IPv4, 0, packed, port);
            return true;
        }

        public bool Equals(Address other) =>
            Lo == other.Lo && Hi == other.Hi && Port == other.Port && Family == other.Family;

        public override bool Equals(object obj) => obj is Address other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Hi, Lo, Port, (int)Family);

        public static bool operator ==(Address a, Address b) => a.Equals(b);
        public static bool operator !=(Address a, Address b) => !a.Equals(b);

        /// <summary>Debug formatting; allocates.</summary>
        public override string ToString()
        {
            switch (Family)
            {
                case AddressFamily.IPv4:
                    return $"{(Lo >> 24) & 0xFF}.{(Lo >> 16) & 0xFF}.{(Lo >> 8) & 0xFF}.{Lo & 0xFF}:{Port}";
                case AddressFamily.IPv6:
                    return $"[{Hi >> 48:x}:{(Hi >> 32) & 0xFFFF:x}:{(Hi >> 16) & 0xFFFF:x}:{Hi & 0xFFFF:x}:{Lo >> 48:x}:{(Lo >> 32) & 0xFFFF:x}:{(Lo >> 16) & 0xFFFF:x}:{Lo & 0xFFFF:x}]:{Port}";
                default:
                    return "none";
            }
        }
    }
}
