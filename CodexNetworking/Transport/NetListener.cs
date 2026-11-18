using System;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// Receives transport events synchronously from inside <c>Update</c> of a host or client, on the
    /// calling thread, with no queueing or allocation. Payload spans are only valid during the call.
    /// On a host <paramref name="connection"/> is the client index; on a client it is always 0.
    /// Acked and Lost are reported exactly once per payload sequence returned by Send; KeepAlive and
    /// handshake traffic never reaches the listener.
    /// </summary>
    public abstract class NetListener
    {
        public virtual void OnConnected(int connection) { }

        public virtual void OnDisconnected(int connection, DisconnectReason reason) { }

        public virtual void OnPacket(int connection, ushort sequence, ReadOnlySpan<byte> payload) { }

        public virtual void OnPacketAcked(int connection, ushort sequence) { }

        /// <summary>The packet was not acked within the reorder window or the loss timeout. It may still arrive.</summary>
        public virtual void OnPacketLost(int connection, ushort sequence) { }
    }
}
