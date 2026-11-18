using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// <see cref="IUdpSocket"/> that sends and receives through the operating system's
    /// <c>sendto</c> and <c>recvfrom</c> directly. On .NET the managed <see cref="UdpSocket"/> is
    /// already free of per-datagram allocations; on Unity's Mono it is not (the managed
    /// <c>ReceiveFrom</c> and <c>SendTo</c> build address objects on every call: eight allocations
    /// per datagram sent and received, measured on Unity 6000.3, see docs/PLAN.md 8.4), and a
    /// server exchanging thousands of datagrams a second turns that into steady garbage. This
    /// class keeps a managed socket for creation, binding and disposal only and makes the two hot
    /// calls natively with a buffer and address structs it owns.
    ///
    /// IPv4 only, Windows and Linux (the platforms whose socket address layout this file knows):
    /// check <see cref="IsSupported"/>, or let <see cref="CreateOrFallback"/> choose. Behaves like
    /// <see cref="UdpSocket"/>: non-blocking, oversized datagrams are dropped, errors are not thrown.
    /// </summary>
    public sealed class NativeUdpSocket : IUdpSocket
    {
        // struct sockaddr_in as Windows and Linux lay it out: family, port and address in network order, padding.
        [StructLayout(LayoutKind.Sequential)]
        private struct SockAddrIn
        {
            public ushort Family;
            public ushort Port;
            public uint Address;
            public ulong Zero;
        }

        private static class Windows
        {
            [DllImport("ws2_32.dll", SetLastError = true)]
            internal static extern int recvfrom(IntPtr socket, ref byte buffer, int length, int flags, ref SockAddrIn from, ref int fromLength);

            [DllImport("ws2_32.dll", SetLastError = true)]
            internal static extern int sendto(IntPtr socket, ref byte buffer, int length, int flags, ref SockAddrIn to, int toLength);
        }

        private static class Linux
        {
            [DllImport("libc", SetLastError = true)]
            internal static extern IntPtr recvfrom(int socket, ref byte buffer, IntPtr length, int flags, ref SockAddrIn from, ref int fromLength);

            [DllImport("libc", SetLastError = true)]
            internal static extern IntPtr sendto(int socket, ref byte buffer, IntPtr length, int flags, ref SockAddrIn to, int toLength);
        }

        private const ushort AfInet = 2;
        private const int SockAddrInSize = 16;
        private const int SioUdpConnReset = unchecked((int)0x9800000C);
        private const int WsaWouldBlock = 10035;
        private const int LinuxAgain = 11;
        // Linux: report the real length of a datagram that did not fit, so it can be dropped instead of passed on cut short.
        private const int LinuxMsgTrunc = 0x20;
        private const int MaxSkippedPerReceive = 32;

        private static readonly bool OnWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly bool OnLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        private readonly Socket _socket;
        private readonly IntPtr _handle;
        private readonly byte[] _receiveBuffer;

        /// <summary>True where this implementation can run.</summary>
        public static bool IsSupported => OnWindows || OnLinux;

        /// <summary>
        /// The native socket where it is supported and the address is IPv4, the managed
        /// <see cref="UdpSocket"/> otherwise. The caller owns and disposes the result.
        /// </summary>
        public static IUdpSocket CreateOrFallback(in Address bind, int maxDatagramSize = 2048)
        {
            if (IsSupported && bind.Family == AddressFamily.IPv4)
                return new NativeUdpSocket(bind, maxDatagramSize);
            return new UdpSocket(bind, maxDatagramSize);
        }

        /// <param name="bind">Local IPv4 address; port 0 picks an ephemeral port, 0.0.0.0 binds every interface.</param>
        /// <param name="maxDatagramSize">Largest datagram that can be received; larger ones are dropped.</param>
        public NativeUdpSocket(in Address bind, int maxDatagramSize = 2048)
        {
            if (!IsSupported)
                throw new PlatformNotSupportedException("NativeUdpSocket covers Windows and Linux; use UdpSocket here");
            if (bind.Family != AddressFamily.IPv4)
                throw new ArgumentException("NativeUdpSocket is IPv4 only", nameof(bind));
            _socket = new Socket(System.Net.Sockets.AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Blocking = false;
            _socket.ReceiveBufferSize = 1 << 20;
            _socket.SendBufferSize = 1 << 20;
            if (OnWindows)
            {
                try
                {
                    // Windows reports ICMP "port unreachable" as a reset on later receives; turn that off.
                    _socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
                }
                catch (Exception)
                {
                    // Not supported on this runtime; the receive loop skips failed receives anyway.
                }
            }
            _socket.Bind(UdpSocket.ToEndPoint(bind));
            LocalAddress = UdpSocket.ToAddress((IPEndPoint)_socket.LocalEndPoint);
            _handle = _socket.Handle;
            _receiveBuffer = new byte[maxDatagramSize];
        }

        public Address LocalAddress { get; }

        public bool TryReceive(Span<byte> buffer, out int received, out Address from)
        {
            received = 0;
            from = Address.None;
            for (int attempt = 0; attempt < MaxSkippedPerReceive; attempt++)
            {
                var source = default(SockAddrIn);
                int sourceLength = SockAddrInSize;
                int length;
                int error;
                try
                {
                    if (OnWindows)
                    {
                        length = Windows.recvfrom(_handle, ref _receiveBuffer[0], _receiveBuffer.Length, 0, ref source, ref sourceLength);
                        error = length < 0 ? Marshal.GetLastWin32Error() : 0;
                        if (length < 0 && error == WsaWouldBlock)
                            return false;
                    }
                    else
                    {
                        length = (int)Linux.recvfrom((int)_handle, ref _receiveBuffer[0], (IntPtr)_receiveBuffer.Length, LinuxMsgTrunc, ref source, ref sourceLength);
                        error = length < 0 ? Marshal.GetLastWin32Error() : 0;
                        if (length < 0 && error == LinuxAgain)
                            return false;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
                // Any other failure concerns one datagram (too large, or a reset report for an
                // earlier send): skip it and look at the next. An empty datagram carries nothing.
                if (length <= 0 || length > _receiveBuffer.Length || length > buffer.Length || source.Family != AfInet)
                    continue;
                new ReadOnlySpan<byte>(_receiveBuffer, 0, length).CopyTo(buffer);
                received = length;
                from = new Address(AddressFamily.IPv4, 0, FromNetwork(source.Address), FromNetwork(source.Port));
                return true;
            }
            return false;
        }

        public bool Send(ReadOnlySpan<byte> data, in Address to)
        {
            if (data.Length == 0 || to.Family != AddressFamily.IPv4)
                return false;
            var target = new SockAddrIn { Family = AfInet, Port = ToNetwork(to.Port), Address = ToNetwork((uint)to.Lo) };
            // sendto does not write to the buffer; the reference is only how a span reaches native code.
            ref byte first = ref MemoryMarshal.GetReference(data);
            try
            {
                if (OnWindows)
                    return Windows.sendto(_handle, ref first, data.Length, 0, ref target, SockAddrInSize) == data.Length;
                return (int)Linux.sendto((int)_handle, ref first, (IntPtr)data.Length, 0, ref target, SockAddrInSize) == data.Length;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Dispose() => _socket.Dispose();

        private static uint ToNetwork(uint value) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;

        private static ushort ToNetwork(ushort value) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;

        private static uint FromNetwork(uint value) => ToNetwork(value);

        private static ushort FromNetwork(ushort value) => ToNetwork(value);
    }
}
