using System;
using System.Net;
using System.Net.Sockets;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// <see cref="IUdpSocket"/> on System.Net.Sockets. Non-blocking, IPv4 or dual-mode IPv6.
    /// Steady-state allocations: none on .NET Core; on Mono the managed ReceiveFrom allocates one
    /// endpoint per datagram, which a native socket implementation can remove later (see docs/PLAN.md).
    /// </summary>
    public sealed class UdpSocket : IUdpSocket
    {
        private struct EndPointCacheEntry
        {
            public Address Address;
            public IPEndPoint EndPoint;
        }

        private const int SioUdpConnReset = unchecked((int)0x9800000C);

        private readonly Socket _socket;
        private readonly byte[] _receiveBuffer;
        private readonly byte[] _sendBuffer;
        private readonly EndPointCacheEntry[] _endPoints = new EndPointCacheEntry[256];
        private readonly bool _ipv6;
        private EndPoint _receiveEndPoint;
        private int _endPointCount;
        private int _endPointNext;

        /// <param name="bind">Local address; port 0 picks an ephemeral port, 0.0.0.0 binds every interface.</param>
        /// <param name="maxDatagramSize">Largest datagram that can be sent or received.</param>
        public UdpSocket(in Address bind, int maxDatagramSize = 2048)
        {
            if (bind.IsNone)
                throw new ArgumentException("bind address required", nameof(bind));
            _ipv6 = bind.Family == AddressFamily.IPv6;
            _socket = new Socket(_ipv6 ? System.Net.Sockets.AddressFamily.InterNetworkV6 : System.Net.Sockets.AddressFamily.InterNetwork,
                SocketType.Dgram, ProtocolType.Udp);
            if (_ipv6)
                _socket.DualMode = true;
            _socket.Blocking = false;
            _socket.ReceiveBufferSize = 1 << 20;
            _socket.SendBufferSize = 1 << 20;
            DisableConnectionReset();
            _socket.Bind(ToEndPoint(bind));
            LocalAddress = ToAddress((IPEndPoint)_socket.LocalEndPoint);
            _receiveBuffer = new byte[maxDatagramSize];
            _sendBuffer = new byte[maxDatagramSize];
            _receiveEndPoint = new IPEndPoint(_ipv6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        }

        public Address LocalAddress { get; }

        public bool TryReceive(Span<byte> buffer, out int received, out Address from)
        {
            received = 0;
            from = Address.None;
            while (true)
            {
                int length;
                try
                {
                    if (_socket.Available == 0)
                        return false;
                    length = _socket.ReceiveFrom(_receiveBuffer, 0, _receiveBuffer.Length, SocketFlags.None, ref _receiveEndPoint);
                }
                catch (SocketException e)
                {
                    // ConnectionReset: ICMP port unreachable for an earlier send. MessageSize: oversized datagram.
                    if (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.MessageSize)
                        continue;
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
                if (length <= 0)
                    return false;
                if (length > buffer.Length)
                    continue;
                new ReadOnlySpan<byte>(_receiveBuffer, 0, length).CopyTo(buffer);
                received = length;
                from = ToAddress((IPEndPoint)_receiveEndPoint);
                return true;
            }
        }

        public bool Send(ReadOnlySpan<byte> data, in Address to)
        {
            if (data.Length == 0 || data.Length > _sendBuffer.Length || to.IsNone)
                return false;
            IPEndPoint endPoint = GetEndPoint(to);
            data.CopyTo(_sendBuffer);
            try
            {
                return _socket.SendTo(_sendBuffer, 0, data.Length, SocketFlags.None, endPoint) == data.Length;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Dispose() => _socket.Dispose();

        /// <summary>Resolves a host name or literal to an address, preferring IPv4. Allocates; call once at connect time.</summary>
        public static bool TryResolve(string host, ushort port, out Address address)
        {
            address = Address.None;
            if (Address.TryParseIPv4(host, port, out address))
                return true;
            try
            {
                IPAddress[] candidates = Dns.GetHostAddresses(host);
                IPAddress best = null;
                for (int i = 0; i < candidates.Length; i++)
                {
                    IPAddress candidate = candidates[i];
                    if (candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        best = candidate;
                        break;
                    }
                    if (best == null && candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        best = candidate;
                }
                if (best == null)
                    return false;
                address = ToAddress(new IPEndPoint(best, port));
                return !address.IsNone;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static Address ToAddress(IPEndPoint endPoint)
        {
            IPAddress ip = endPoint.Address;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            Span<byte> bytes = stackalloc byte[16];
            if (!ip.TryWriteBytes(bytes, out int written) || (written != 4 && written != 16))
                return Address.None;
            return Address.FromBytes(bytes.Slice(0, written), (ushort)endPoint.Port);
        }

        public static IPEndPoint ToEndPoint(in Address address) => new IPEndPoint(ToIPAddress(address), address.Port);

        private static IPAddress ToIPAddress(in Address address)
        {
            Span<byte> bytes = stackalloc byte[16];
            int length = address.WriteBytes(bytes);
            return new IPAddress(bytes.Slice(0, length));
        }

        private IPEndPoint GetEndPoint(in Address address)
        {
            for (int i = 0; i < _endPointCount; i++)
            {
                if (_endPoints[i].Address == address)
                    return _endPoints[i].EndPoint;
            }
            IPAddress ip = ToIPAddress(address);
            if (_ipv6 && address.Family == AddressFamily.IPv4)
                ip = ip.MapToIPv6();
            var endPoint = new IPEndPoint(ip, address.Port);
            int slot;
            if (_endPointCount < _endPoints.Length)
            {
                slot = _endPointCount++;
            }
            else
            {
                slot = _endPointNext;
                _endPointNext = (_endPointNext + 1) % _endPoints.Length;
            }
            _endPoints[slot].Address = address;
            _endPoints[slot].EndPoint = endPoint;
            return endPoint;
        }

        // Windows reports ICMP "port unreachable" as ConnectionReset on later receives; turn that off.
        private void DisableConnectionReset()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return;
            try
            {
                _socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception)
            {
                // Not supported on this runtime; the receive loop tolerates ConnectionReset anyway.
            }
        }
    }
}
