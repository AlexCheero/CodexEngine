using System;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// Minimal unreliable datagram socket. Everything above this interface is pure logic, so the same
    /// host and client code runs on a real UDP socket, the in-process simulator, or a platform socket
    /// (console, relay service, WebRTC data channel) supplied by the application.
    /// Implementations are non-blocking and single-threaded: the owner calls them from one thread.
    /// </summary>
    public interface IUdpSocket : IDisposable
    {
        Address LocalAddress { get; }

        /// <summary>
        /// Copies the next pending datagram into <paramref name="buffer"/>. Returns false when none is
        /// pending. Datagrams larger than the buffer are dropped.
        /// </summary>
        bool TryReceive(Span<byte> buffer, out int received, out Address from);

        /// <summary>Sends one datagram. Returns false when the socket refused it synchronously.</summary>
        bool Send(ReadOnlySpan<byte> data, in Address to);
    }
}
