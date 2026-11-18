using System;
using CodexNetworking.Transport.Crypto;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// Server side of the transport: accepts clients through a salt challenge handshake and runs one
    /// session per client slot. Single-threaded: call <see cref="Update"/> from one thread at a fixed
    /// rate; <see cref="TrySend"/> and <see cref="Disconnect"/> may be called from listener callbacks.
    /// Owns and disposes the socket. In encrypted modes the handshake proves the client holds the
    /// pre-shared key or a valid connect token before a slot is spent, and every session packet is
    /// sealed under keys derived per session.
    /// </summary>
    public sealed class NetHost : IDisposable
    {
        private struct Pending
        {
            public bool Used;
            public Address Address;
            public ulong ClientSalt;
            public ulong ServerSalt;
            public double Time;
            public ulong ClientId;
            public byte[] Secret; // token mode: the client's key material; allocated once per slot
        }

        private readonly NetConfig _config;
        private readonly IUdpSocket _socket;
        private readonly IRandomSource _random;
        private readonly NetListener _listener;
        private readonly Connection[] _clients;
        private readonly ulong[] _clientIds;
        private readonly AddressMap _clientByAddress;
        private readonly Pending[] _pending;
        private readonly TokenReplayMemory _replay;
        private readonly byte[] _scratch;
        private readonly byte[] _receive;
        private readonly byte[] _clientToServer = new byte[SessionKeys.KeySize];
        private readonly byte[] _serverToClient = new byte[SessionKeys.KeySize];
        private readonly byte[] _tokenScratch = new byte[ConnectToken.SecretSize];
        private readonly byte[] _tokenMaterial = new byte[SessionKeys.KeySize];
        private int _connectedCount;
        private double _now;
        private bool _disposed;

        public NetHost(NetConfig config, IUdpSocket socket, IRandomSource random, NetListener listener)
        {
            config.Validate();
            if (config.Encryption == EncryptionMode.ConnectToken && config.TokenKey == null)
                throw new ArgumentException("ConnectToken mode needs the TokenKey on the host", nameof(config));
            _config = config;
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            _clients = new Connection[config.MaxClients];
            for (int i = 0; i < _clients.Length; i++)
                _clients[i] = new Connection(config);
            _clientIds = new ulong[config.MaxClients];
            _clientByAddress = new AddressMap(config.MaxClients);
            _pending = new Pending[config.MaxPendingConnections];
            if (config.Encryption == EncryptionMode.ConnectToken)
            {
                for (int i = 0; i < _pending.Length; i++)
                    _pending[i].Secret = new byte[SessionKeys.KeySize];
                _replay = new TokenReplayMemory(config.TokenReplayMemory);
            }
            _scratch = new byte[config.Mtu];
            _receive = new byte[config.Mtu];
        }

        public NetConfig Config => _config;
        public Address LocalAddress => _socket.LocalAddress;
        public int MaxClients => _clients.Length;
        public int ConnectedCount => _connectedCount;

        /// <summary>Time passed to the last <see cref="Update"/>; sends are stamped with it.</summary>
        public double Now => _now;

        /// <summary>Connect requests refused because their token did not verify, had expired or was replayed.</summary>
        public int RejectedTokens { get; private set; }

        /// <summary>Handshake packets whose authentication tag did not verify.</summary>
        public int RejectedHandshakes { get; private set; }

        public bool IsConnected(int client) =>
            (uint)client < (uint)_clients.Length && _clients[client].State == ConnectionState.Connected;

        public Address GetAddress(int client) => IsConnected(client) ? _clients[client].Remote : Address.None;

        public ConnectionStats GetStats(int client) => IsConnected(client) ? _clients[client].Acks.Stats : default;

        /// <summary>ConnectToken mode: the id the backend put in the client's token; 0 otherwise.</summary>
        public ulong GetClientId(int client) => IsConnected(client) ? _clientIds[client] : 0;

        /// <summary>Congestion hint for the client; upper layers should send less while it is set.</summary>
        public bool IsCongested(int client) => IsConnected(client) && _clients[client].Congestion.IsCongested;

        /// <summary>Receives everything pending, then runs timeouts, loss detection and keep-alives.</summary>
        public void Update(double now)
        {
            if (_disposed)
                return;
            _now = now;
            ReceiveAll(now);

            for (int i = 0; i < _clients.Length; i++)
            {
                Connection client = _clients[i];
                if (client.State != ConnectionState.Connected)
                    continue;
                client.Update(now, _listener, i);
                if (client.State != ConnectionState.Connected)
                    continue;
                if (now - client.LastReceiveTime > _config.ConnectionTimeout)
                {
                    EndClient(i, DisconnectReason.Timeout, false, now);
                    continue;
                }
                if (now - client.LastSendTime >= _config.KeepAliveInterval)
                    client.SendKeepAlive(_socket, _scratch, _config, now, _listener, i);
            }

            for (int i = 0; i < _pending.Length; i++)
            {
                if (_pending[i].Used && now - _pending[i].Time > _config.PendingConnectionTimeout)
                    _pending[i].Used = false;
            }
        }

        /// <summary>
        /// Sends a payload of 1..<see cref="NetConfig.MaxMessageSize"/> bytes. The returned sequence is
        /// later reported to the listener as acked or lost.
        /// </summary>
        public bool TrySend(int client, ReadOnlySpan<byte> payload, out ushort sequence)
        {
            sequence = 0;
            if (_disposed || !IsConnected(client))
                return false;
            return _clients[client].TrySendPayload(_socket, _scratch, _config, _now, payload, _listener, client, out sequence);
        }

        public void Disconnect(int client)
        {
            if (!_disposed && IsConnected(client))
                EndClient(client, DisconnectReason.LocalRequest, true, _now);
        }

        public void DisconnectAll()
        {
            for (int i = 0; i < _clients.Length; i++)
                Disconnect(i);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            DisconnectAll();
            _disposed = true;
            _socket.Dispose();
        }

        private void ReceiveAll(double now)
        {
            while (_socket.TryReceive(_receive, out int length, out Address from))
            {
                var datagram = new Span<byte>(_receive, 0, length);
                if (!Protocol.TryReadPrefix(datagram, _config.ProtocolId, out PacketType type))
                    continue;
                switch (type)
                {
                    case PacketType.ConnectRequest:
                        HandleConnectRequest(datagram, from, now);
                        break;
                    case PacketType.ChallengeResponse:
                        HandleChallengeResponse(datagram, from, now);
                        break;
                    case PacketType.Challenge:
                    case PacketType.Denied:
                        break; // server-to-client only
                    default:
                        HandleSession(datagram, from, now);
                        break;
                }
            }
        }

        private void HandleConnectRequest(ReadOnlySpan<byte> datagram, in Address from, double now)
        {
            bool tokenMode = _config.Encryption == EncryptionMode.ConnectToken;
            if (!Protocol.TryReadConnectRequest(datagram, tokenMode, out ulong clientSalt, out ReadOnlySpan<byte> token))
                return;
            // A connected address keeps its session; a restarted client gets in once the old one times out.
            if (FindClient(from) >= 0)
                return;

            int pending = FindPending(from);
            if (pending >= 0 && _pending[pending].ClientSalt == clientSalt)
            {
                SendChallenge(pending);
                return;
            }

            ulong clientId = 0;
            if (tokenMode)
            {
                // Only a token the backend sealed, still valid and never seen before, earns a challenge.
                if (!ConnectTokenFactory.TryOpen(_config.TokenKey, _config.ProtocolId, token, _tokenScratch, out long expiresAt, out clientId, _tokenMaterial))
                {
                    RejectedTokens++;
                    return;
                }
                long unixNow = _config.UnixTime();
                if (expiresAt <= unixNow || _replay.SeenOrRemember(token, expiresAt, unixNow))
                {
                    RejectedTokens++;
                    return;
                }
            }
            if (_connectedCount >= _clients.Length)
            {
                SendDenied(from, clientSalt, DisconnectReason.ServerFull, tokenMode ? _tokenMaterial : _config.PreSharedKey);
                return;
            }
            if (pending < 0)
                pending = AcquirePending(now);
            ref Pending slot = ref _pending[pending];
            slot.Used = true;
            slot.Address = from;
            slot.ClientSalt = clientSalt;
            slot.ServerSalt = NonZeroSalt();
            slot.Time = now;
            slot.ClientId = clientId;
            if (tokenMode)
                _tokenMaterial.CopyTo(slot.Secret, 0);
            SendChallenge(pending);
        }

        private void HandleChallengeResponse(ReadOnlySpan<byte> datagram, in Address from, double now)
        {
            if (!Protocol.TryReadChallenge(datagram, out ulong clientSalt, out ulong serverSalt))
                return;

            int pending = FindPending(from);
            if (pending < 0 || _pending[pending].ClientSalt != clientSalt || _pending[pending].ServerSalt != serverSalt)
            {
                // Already promoted and our first KeepAlive was lost: answer again so the client completes.
                int existing = FindClient(from);
                if (existing >= 0 && _clients[existing].ClientSalt == clientSalt && _clients[existing].ServerSalt == serverSalt)
                {
                    if (_config.Encrypted && !Protocol.VerifyHandshake(datagram, Protocol.ChallengeSize, _clients[existing].ReceiveKey, SessionKeys.PurposeChallengeResponse))
                    {
                        RejectedHandshakes++;
                        return;
                    }
                    _clients[existing].SendKeepAlive(_socket, _scratch, _config, now, _listener, existing);
                }
                return;
            }

            ReadOnlySpan<byte> secret = SecretOf(pending);
            if (_config.Encrypted)
            {
                SessionKeys.Derive(secret, clientSalt, serverSalt, _clientToServer, _serverToClient);
                if (!Protocol.VerifyHandshake(datagram, Protocol.ChallengeSize, _clientToServer, SessionKeys.PurposeChallengeResponse))
                {
                    RejectedHandshakes++;
                    return;
                }
            }

            _pending[pending].Used = false;
            int slot = FindFreeClient();
            if (slot < 0)
            {
                SendDenied(from, clientSalt, DisconnectReason.ServerFull, secret);
                return;
            }
            Connection client = _clients[slot];
            client.Begin(from, clientSalt, serverSalt, now, secret, true);
            _clientIds[slot] = _pending[pending].ClientId;
            _clientByAddress.Set(from, slot);
            _connectedCount++;
            client.SendKeepAlive(_socket, _scratch, _config, now, _listener, slot);
            _listener.OnConnected(slot);
        }

        private void HandleSession(Span<byte> datagram, in Address from, double now)
        {
            int index = FindClient(from);
            if (index < 0)
                return;
            if (!Protocol.TryReadSessionHeader(datagram, out SessionHeader header, out int headerSize))
                return;
            Connection client = _clients[index];
            if (header.SessionId != client.SessionId)
                return;
            if (!client.HandleSessionPacket(header, datagram, headerSize, now, _listener, index))
                EndClient(index, DisconnectReason.RemoteRequest, false, now);
        }

        private void EndClient(int index, DisconnectReason reason, bool sendPacket, double now)
        {
            Connection client = _clients[index];
            if (client.State != ConnectionState.Connected)
                return;
            if (sendPacket)
                client.SendDisconnect(_socket, _scratch, _config, now, _listener, index);
            _clientByAddress.Remove(client.Remote);
            client.End();
            _clientIds[index] = 0;
            _connectedCount--;
            _listener.OnDisconnected(index, reason);
        }

        // The secret a pending client's session keys derive from: its token material or the shared key.
        private ReadOnlySpan<byte> SecretOf(int pending)
        {
            switch (_config.Encryption)
            {
                case EncryptionMode.PreSharedKey:
                    return _config.PreSharedKey;
                case EncryptionMode.ConnectToken:
                    return _pending[pending].Secret;
                default:
                    return ReadOnlySpan<byte>.Empty;
            }
        }

        private void SendChallenge(int pending)
        {
            ref Pending slot = ref _pending[pending];
            int size = Protocol.WriteChallenge(_scratch, _config.ProtocolId, PacketType.Challenge, slot.ClientSalt, slot.ServerSalt);
            if (_config.Encrypted)
            {
                SessionKeys.Derive(SecretOf(pending), slot.ClientSalt, slot.ServerSalt, _clientToServer, _serverToClient);
                size = Protocol.SealHandshake(_scratch, size, _serverToClient, SessionKeys.PurposeChallenge);
            }
            _socket.Send(new ReadOnlySpan<byte>(_scratch, 0, size), slot.Address);
        }

        private void SendDenied(in Address to, ulong clientSalt, DisconnectReason reason, ReadOnlySpan<byte> secret)
        {
            int size = Protocol.WriteDenied(_scratch, _config.ProtocolId, clientSalt, reason);
            if (_config.Encrypted)
            {
                // No server salt exists yet: the denial is keyed from the client salt alone.
                SessionKeys.Derive(secret, clientSalt, 0, _clientToServer, _serverToClient);
                size = Protocol.SealHandshake(_scratch, size, _serverToClient, SessionKeys.PurposeDenied);
            }
            _socket.Send(new ReadOnlySpan<byte>(_scratch, 0, size), to);
        }

        private int FindClient(in Address address)
        {
            return _clientByAddress.TryGet(address, out int slot) && _clients[slot].State == ConnectionState.Connected ? slot : -1;
        }

        private int FindFreeClient()
        {
            for (int i = 0; i < _clients.Length; i++)
            {
                if (_clients[i].State == ConnectionState.Disconnected)
                    return i;
            }
            return -1;
        }

        private int FindPending(in Address address)
        {
            for (int i = 0; i < _pending.Length; i++)
            {
                if (_pending[i].Used && _pending[i].Address == address)
                    return i;
            }
            return -1;
        }

        // A free pending slot, or the oldest one when all are taken.
        private int AcquirePending(double now)
        {
            int oldest = 0;
            for (int i = 0; i < _pending.Length; i++)
            {
                if (!_pending[i].Used)
                    return i;
                if (_pending[i].Time < _pending[oldest].Time)
                    oldest = i;
            }
            return oldest;
        }

        private ulong NonZeroSalt()
        {
            ulong salt;
            do
                salt = _random.NextUInt64();
            while (salt == 0);
            return salt;
        }
    }
}
