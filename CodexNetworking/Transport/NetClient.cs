using System;
using CodexNetworking.Transport.Crypto;

namespace CodexNetworking.Transport
{
    public enum ClientState : byte
    {
        Disconnected = 0,
        SendingConnectRequest = 1,
        SendingChallengeResponse = 2,
        Connected = 3,
    }

    /// <summary>
    /// Client side of the transport. Single-threaded like <see cref="NetHost"/>; the listener's
    /// connection index is always 0. Owns and disposes the socket. In PreSharedKey mode the config
    /// holds the key; in ConnectToken mode <see cref="Connect(in Address, double, ConnectToken)"/>
    /// takes the token the backend issued.
    /// </summary>
    public sealed class NetClient : IDisposable
    {
        private readonly NetConfig _config;
        private readonly IUdpSocket _socket;
        private readonly IRandomSource _random;
        private readonly NetListener _listener;
        private readonly Connection _connection;
        private readonly byte[] _scratch;
        private readonly byte[] _receive;
        private readonly byte[] _clientToServer = new byte[SessionKeys.KeySize];
        private readonly byte[] _serverToClient = new byte[SessionKeys.KeySize];
        private ClientState _state;
        private Address _server;
        private ulong _clientSalt;
        private ulong _serverSalt;
        private ConnectToken _token;
        private double _connectStart;
        private double _lastHandshakeSend;
        private double _now;
        private bool _disposed;

        public NetClient(NetConfig config, IUdpSocket socket, IRandomSource random, NetListener listener)
        {
            config.Validate();
            _config = config;
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            _connection = new Connection(config);
            _scratch = new byte[config.Mtu];
            _receive = new byte[config.Mtu];
        }

        public NetConfig Config => _config;
        public ClientState State => _state;
        public bool IsConnected => _state == ClientState.Connected;
        public Address LocalAddress => _socket.LocalAddress;
        public Address ServerAddress => _server;
        public ConnectionStats Stats => _connection.Acks.Stats;

        /// <summary>Congestion hint; upper layers should send less while it is set.</summary>
        public bool IsCongested => _state == ClientState.Connected && _connection.Congestion.IsCongested;

        /// <summary>Time passed to the last <see cref="Update"/> or <see cref="Connect(in Address, double)"/>; sends are stamped with it.</summary>
        public double Now => _now;

        /// <summary>Handshake packets from the server whose authentication tag did not verify.</summary>
        public int RejectedHandshakes { get; private set; }

        /// <summary>Starts the handshake (None or PreSharedKey mode). Any existing session is dropped first.</summary>
        public void Connect(in Address server, double now)
        {
            if (_config.Encryption == EncryptionMode.ConnectToken)
                throw new InvalidOperationException("ConnectToken mode needs a token: use Connect(server, now, token)");
            Begin(server, now, null);
        }

        /// <summary>Starts the handshake with a connect token (ConnectToken mode).</summary>
        public void Connect(in Address server, double now, ConnectToken token)
        {
            if (_config.Encryption != EncryptionMode.ConnectToken)
                throw new InvalidOperationException("the config is not in ConnectToken mode");
            if (token == null)
                throw new ArgumentNullException(nameof(token));
            Begin(server, now, token);
        }

        private void Begin(in Address server, double now, ConnectToken token)
        {
            if (_disposed)
                return;
            if (_state != ClientState.Disconnected)
                Disconnect();
            _now = now;
            _server = server;
            _token = token;
            _clientSalt = NonZeroSalt();
            _serverSalt = 0;
            _state = ClientState.SendingConnectRequest;
            _connectStart = now;
            SendConnectRequest(now);
        }

        public void Update(double now)
        {
            if (_disposed)
                return;
            _now = now;
            ReceiveAll(now);

            switch (_state)
            {
                case ClientState.SendingConnectRequest:
                case ClientState.SendingChallengeResponse:
                    if (now - _connectStart > _config.ConnectTimeout)
                    {
                        _state = ClientState.Disconnected;
                        _listener.OnDisconnected(0, DisconnectReason.ConnectTimeout);
                        break;
                    }
                    if (now - _lastHandshakeSend >= _config.HandshakeRetryInterval)
                    {
                        if (_state == ClientState.SendingConnectRequest)
                            SendConnectRequest(now);
                        else
                            SendChallengeResponse(now);
                    }
                    break;

                case ClientState.Connected:
                    _connection.Update(now, _listener, 0);
                    if (_state != ClientState.Connected)
                        break;
                    if (now - _connection.LastReceiveTime > _config.ConnectionTimeout)
                    {
                        EndSession(DisconnectReason.Timeout, false, now);
                        break;
                    }
                    if (now - _connection.LastSendTime >= _config.KeepAliveInterval)
                        _connection.SendKeepAlive(_socket, _scratch, _config, now, _listener, 0);
                    break;
            }
        }

        /// <summary>Sends a payload of 1..<see cref="NetConfig.MaxMessageSize"/> bytes while connected.</summary>
        public bool TrySend(ReadOnlySpan<byte> payload, out ushort sequence)
        {
            sequence = 0;
            if (_disposed || _state != ClientState.Connected)
                return false;
            return _connection.TrySendPayload(_socket, _scratch, _config, _now, payload, _listener, 0, out sequence);
        }

        /// <summary>Leaves the session (or abandons a handshake) and reports LocalRequest.</summary>
        public void Disconnect()
        {
            if (_disposed)
                return;
            if (_state == ClientState.Connected)
            {
                EndSession(DisconnectReason.LocalRequest, true, _now);
            }
            else if (_state != ClientState.Disconnected)
            {
                _state = ClientState.Disconnected;
                _listener.OnDisconnected(0, DisconnectReason.LocalRequest);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            Disconnect();
            _disposed = true;
            _socket.Dispose();
        }

        // The secret the session keys derive from: the token's key material or the shared key.
        private ReadOnlySpan<byte> Secret
        {
            get
            {
                switch (_config.Encryption)
                {
                    case EncryptionMode.PreSharedKey:
                        return _config.PreSharedKey;
                    case EncryptionMode.ConnectToken:
                        return _token == null ? ReadOnlySpan<byte>.Empty : _token.KeyMaterial;
                    default:
                        return ReadOnlySpan<byte>.Empty;
                }
            }
        }

        private void ReceiveAll(double now)
        {
            while (_socket.TryReceive(_receive, out int length, out Address from))
            {
                if (_state == ClientState.Disconnected || from != _server)
                    continue;
                var datagram = new Span<byte>(_receive, 0, length);
                if (!Protocol.TryReadPrefix(datagram, _config.ProtocolId, out PacketType type))
                    continue;
                switch (type)
                {
                    case PacketType.Challenge:
                        HandleChallenge(datagram, now);
                        break;
                    case PacketType.Denied:
                        HandleDenied(datagram);
                        break;
                    case PacketType.ConnectRequest:
                    case PacketType.ChallengeResponse:
                        break; // client-to-server only
                    default:
                        HandleSession(datagram, now);
                        break;
                }
            }
        }

        private void HandleChallenge(ReadOnlySpan<byte> datagram, double now)
        {
            if (_state != ClientState.SendingConnectRequest && _state != ClientState.SendingChallengeResponse)
                return;
            if (!Protocol.TryReadChallenge(datagram, out ulong clientSalt, out ulong serverSalt) || clientSalt != _clientSalt)
                return;
            if (_state == ClientState.SendingChallengeResponse && serverSalt != _serverSalt)
                return;
            if (_config.Encrypted)
            {
                // Only a server that knows the secret can seal the challenge: a stranger cannot hold us.
                SessionKeys.Derive(Secret, clientSalt, serverSalt, _clientToServer, _serverToClient);
                if (!Protocol.VerifyHandshake(datagram, Protocol.ChallengeSize, _serverToClient, SessionKeys.PurposeChallenge))
                {
                    RejectedHandshakes++;
                    return;
                }
            }
            _serverSalt = serverSalt;
            _state = ClientState.SendingChallengeResponse;
            SendChallengeResponse(now);
        }

        private void HandleDenied(ReadOnlySpan<byte> datagram)
        {
            if (_state != ClientState.SendingConnectRequest && _state != ClientState.SendingChallengeResponse)
                return;
            if (!Protocol.TryReadDenied(datagram, out ulong clientSalt, out DisconnectReason reason) || clientSalt != _clientSalt)
                return;
            if (_config.Encrypted)
            {
                SessionKeys.Derive(Secret, clientSalt, 0, _clientToServer, _serverToClient);
                if (!Protocol.VerifyHandshake(datagram, Protocol.DeniedSize, _serverToClient, SessionKeys.PurposeDenied))
                {
                    RejectedHandshakes++;
                    return;
                }
            }
            _state = ClientState.Disconnected;
            _listener.OnDisconnected(0, reason == DisconnectReason.ServerFull ? DisconnectReason.ServerFull : DisconnectReason.Denied);
        }

        private void HandleSession(Span<byte> datagram, double now)
        {
            if (_state != ClientState.SendingChallengeResponse && _state != ClientState.Connected)
                return;
            if (!Protocol.TryReadSessionHeader(datagram, out SessionHeader header, out int headerSize))
                return;
            if (header.SessionId != Protocol.DeriveSessionId(_clientSalt, _serverSalt))
                return;

            if (_state == ClientState.SendingChallengeResponse)
            {
                // The server only sends session packets once it accepted us.
                _connection.Begin(_server, _clientSalt, _serverSalt, now, Secret, false);
                _state = ClientState.Connected;
                _listener.OnConnected(0);
                if (_state != ClientState.Connected)
                    return;
            }

            if (!_connection.HandleSessionPacket(header, datagram, headerSize, now, _listener, 0))
                EndSession(DisconnectReason.RemoteRequest, false, now);
        }

        private void EndSession(DisconnectReason reason, bool sendPacket, double now)
        {
            if (_state != ClientState.Connected)
                return;
            if (sendPacket)
                _connection.SendDisconnect(_socket, _scratch, _config, now, _listener, 0);
            _connection.End();
            _state = ClientState.Disconnected;
            _listener.OnDisconnected(0, reason);
        }

        private void SendConnectRequest(double now)
        {
            ReadOnlySpan<byte> token = _token == null ? ReadOnlySpan<byte>.Empty : _token.Bytes;
            int size = Protocol.WriteConnectRequest(_scratch, _config.ProtocolId, _clientSalt, token);
            _socket.Send(new ReadOnlySpan<byte>(_scratch, 0, size), _server);
            _lastHandshakeSend = now;
        }

        private void SendChallengeResponse(double now)
        {
            int size = Protocol.WriteChallenge(_scratch, _config.ProtocolId, PacketType.ChallengeResponse, _clientSalt, _serverSalt);
            if (_config.Encrypted)
            {
                SessionKeys.Derive(Secret, _clientSalt, _serverSalt, _clientToServer, _serverToClient);
                size = Protocol.SealHandshake(_scratch, size, _clientToServer, SessionKeys.PurposeChallengeResponse);
            }
            _socket.Send(new ReadOnlySpan<byte>(_scratch, 0, size), _server);
            _lastHandshakeSend = now;
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
