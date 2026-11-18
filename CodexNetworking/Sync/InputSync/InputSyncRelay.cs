using System;
using CodexECS;
using CodexNetworking.Transport;
using CodexNetworking.Transport.Reliable;

namespace CodexNetworking.Sync.InputSync
{
    /// <summary>Payload layout shared by the relay host and clients: kind, peer, then the session blob.</summary>
    public static class InputSyncPacket
    {
        public const byte Inputs = 1;
        public const byte Hash = 2;
        /// <summary>Host to client: your peer index. Repeated until the client's first packet arrives.</summary>
        public const byte Welcome = 3;
        /// <summary>Host to client: the peer table (join and drop tick of every peer).</summary>
        public const byte Control = 4;
        /// <summary>Host to client: reliable join messages (see <see cref="JoinMessage"/>).</summary>
        public const byte Join = 5;
        /// <summary>Host to client: another peer's inputs written from the host's buffers; the acknowledgements are the host's.</summary>
        public const byte Relayed = 6;
        /// <summary>Client to host: u64 identity; repeated until the Welcome arrives. A returning client gets its old peer index back.</summary>
        public const byte Hello = 7;
        /// <summary>Peer byte of packets sent before a peer index is known.</summary>
        public const byte NoPeer = 0xFF;
        public const int HeaderSize = 2;
    }

    /// <summary>First byte of every message on the join channel.</summary>
    public static class JoinMessage
    {
        /// <summary>mode (u8), start tick (i32), snapshot byte count (i32).</summary>
        public const byte Start = 1;
        /// <summary>The next bytes of the snapshot.</summary>
        public const byte Chunk = 2;
        /// <summary>The snapshot is complete, followed by the peer table; create and start the session.</summary>
        public const byte End = 3;
    }

    /// <summary>
    /// Star topology: the host is peer 0, runs its own session, and forwards every client's input
    /// and hash packets to the other clients. Clients get peer indices 1..PeerCount-1 in connection
    /// order; an index stays reserved after its client leaves.
    ///
    /// The host owns the session lifecycle: it brings a new client up to date (a snapshot of a
    /// confirmed tick or, with <see cref="JoinMode.InputReplay"/>, nothing but the inputs since tick
    /// 0), assigns its join tick once it has caught up, fills input holes a client reports from its
    /// own buffers, drops a client that stays silent for <see cref="InputSyncConfig.PeerTimeout"/>
    /// seconds (its inputs become default from the first unconfirmed tick on) and broadcasts the
    /// peer table whenever it changes.
    ///
    /// Peer indices are handed out when a client's Hello arrives, by identity: a client whose
    /// identity belongs to a dropped peer (the connect token's client id in token mode, otherwise
    /// the identity the client chose) gets that index back and rejoins with a new interval, as long
    /// as the peer has intervals left (<see cref="InputSyncConfig.MaxIntervalsPerPeer"/>); an
    /// index whose client left before ever joining is reused.
    ///
    /// Create it, then call <see cref="Update"/> every frame and <see cref="Pump"/> after the local
    /// session advanced. A relay that is not itself a player calls
    /// <see cref="InputSyncSession{TInput}.SetLocalSpectator"/> on its session.
    /// </summary>
    public sealed class InputSyncRelayHost<TInput> : NetListener, IDisposable where TInput : unmanaged
    {
        private struct ClientState
        {
            public int Peer;
            public bool Welcomed;
            public double LastHeard;
            public ReliableChannel Join;
            public byte[] Snapshot;
            public int SnapshotOffset;
            public bool JoinStarted;
            public bool JoinEnded;
            public int TableVersionSent;
        }

        private const int TableResendPumps = 30;

        private readonly NetHost _host;
        private readonly InputSyncSession<TInput> _session;
        private readonly InputSyncConfig _config;
        private readonly ClientState[] _clients;
        private readonly byte[] _scratch;
        private readonly byte[] _table;
        // Per peer index: who holds it (0 = nobody yet) and whether its connection is live.
        private readonly ulong[] _identity;
        private readonly bool[] _online;
        private int _nextPeer = 1;
        private int _pumps;

        public InputSyncRelayHost(NetConfig netConfig, IUdpSocket socket, IRandomSource random, InputSyncSession<TInput> session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            if (session.LocalPeer != 0)
                throw new ArgumentException("the relay host must be peer 0", nameof(session));
            _config = session.Config;
            if (ReliableChannel.PacketSizeFor(_config.JoinChannel.MaxMessageSize) + InputSyncPacket.HeaderSize > netConfig.MaxPayloadSize)
                throw new ArgumentException("JoinChannel.MaxMessageSize does not fit the transport payload", nameof(session));
            int tableSize = session.PeerCount * (1 + 8 * _config.MaxIntervalsPerPeer);
            if (InputSyncPacket.HeaderSize + tableSize > netConfig.MaxMessageSize)
                throw new ArgumentException("the peer table does not fit a transport message; lower PeerCount or MaxIntervalsPerPeer", nameof(session));
            _host = new NetHost(netConfig, socket, random, this);
            _clients = new ClientState[netConfig.MaxClients];
            for (int i = 0; i < _clients.Length; i++)
            {
                _clients[i].Peer = -1;
                _clients[i].Join = new ReliableChannel(_config.JoinChannel);
            }
            _identity = new ulong[session.PeerCount];
            _online = new bool[session.PeerCount];
            _online[0] = true;
            _scratch = new byte[netConfig.MaxPayloadSize];
            _table = new byte[InputSyncPacket.HeaderSize + tableSize];
        }

        public NetHost Host => _host;
        public InputSyncSession<TInput> Session => _session;

        /// <summary>Peer indices handed out so far, host included.</summary>
        public int ConnectedPeers => _nextPeer;

        /// <summary>Peers whose inputs count right now (joined, not dropped), host included.</summary>
        public int ActivePeers
        {
            get
            {
                int count = 0;
                for (int peer = 0; peer < _session.PeerCount; peer++)
                {
                    if (_session.IsPeerActive(peer))
                        count++;
                }
                return count;
            }
        }

        /// <summary>Clients that came back under a peer index they held before.</summary>
        public int Rejoins { get; private set; }

        /// <summary>The identity a peer index was handed to (0 for the host or an index never used).</summary>
        public ulong IdentityOf(int peer) => _identity[peer];

        /// <summary>Clients dropped by the host because they stayed silent.</summary>
        public int TimedOut { get; private set; }

        /// <summary>Hole-filling input packets sent from the host's buffers.</summary>
        public int GapFills { get; private set; }

        public void Update(double now)
        {
            _host.Update(now);
            for (int client = 0; client < _clients.Length; client++)
            {
                ref ClientState state = ref _clients[client];
                if (state.Peer < 0 || !_host.IsConnected(client) || !state.Welcomed)
                    continue;
                if (now - state.LastHeard > _config.PeerTimeout)
                {
                    TimedOut++;
                    _host.Disconnect(client); // OnDisconnected drops the peer
                }
            }
        }

        /// <summary>Sends the host's own inputs and hashes, join data, hole fills and the peer table.</summary>
        public void Pump()
        {
            _pumps++;
            int size = _session.WriteInputs(new Span<byte>(_scratch, InputSyncPacket.HeaderSize, _scratch.Length - InputSyncPacket.HeaderSize));
            if (size > 0)
                Broadcast(InputSyncPacket.Inputs, 0, size, -1);
            size = _session.WriteHash(new Span<byte>(_scratch, InputSyncPacket.HeaderSize, _scratch.Length - InputSyncPacket.HeaderSize));
            if (size > 0)
                Broadcast(InputSyncPacket.Hash, 0, size, -1);

            bool tableDue = _pumps % TableResendPumps == 0;
            for (int client = 0; client < _clients.Length; client++)
            {
                ref ClientState state = ref _clients[client];
                if (state.Peer < 0 || !_host.IsConnected(client))
                    continue;
                if (!state.JoinStarted)
                    BeginJoin(ref state);
                PumpJoin(client, ref state);
                if (state.Welcomed)
                {
                    if (_pumps % _config.HostResendEveryPumps == 0)
                        FillGaps(client, ref state);
                }
                else
                    SendWelcome(client);
                // A fresh client gets the table every pump until it speaks; afterwards on change and periodically.
                if (!state.Welcomed || tableDue || state.TableVersionSent != _session.PeerTableVersion)
                    SendTable(client, ref state);
            }
        }

        public override void OnConnected(int connection)
        {
            // The peer index waits for the client's Hello, which says who it is.
            ref ClientState state = ref _clients[connection];
            state.Peer = -1;
            state.Welcomed = false;
            state.LastHeard = _host.Now;
            state.Join.Reset();
            state.Snapshot = null;
            state.SnapshotOffset = 0;
            state.JoinStarted = false;
            state.JoinEnded = false;
            state.TableVersionSent = -1;
        }

        public override void OnDisconnected(int connection, DisconnectReason reason)
        {
            ref ClientState state = ref _clients[connection];
            int peer = state.Peer;
            if (peer < 0)
                return;
            state.Welcomed = false;
            state.Peer = -1;
            state.Snapshot = null;
            _online[peer] = false;
            if (_session.IsPeerAbsent(peer))
                _identity[peer] = 0; // never joined: the index is free again
            else
                _session.DropPeer(peer, _session.ConfirmedThrough(peer) + 1); // default inputs from here; may rejoin
        }

        public override void OnPacket(int connection, ushort sequence, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < InputSyncPacket.HeaderSize)
                return;
            ref ClientState state = ref _clients[connection];
            if (payload[0] == InputSyncPacket.Hello)
            {
                state.LastHeard = _host.Now;
                if (state.Peer < 0 && payload.Length >= InputSyncPacket.HeaderSize + 8)
                    AssignPeer(connection, ref state, ReadUInt64(payload, InputSyncPacket.HeaderSize));
                return;
            }
            int peer = state.Peer;
            if (peer < 0 || payload[1] != peer)
                return; // a client may only speak for itself
            state.Welcomed = true;
            state.LastHeard = _host.Now;
            ReadOnlySpan<byte> blob = payload.Slice(InputSyncPacket.HeaderSize);
            switch (payload[0])
            {
                case InputSyncPacket.Inputs:
                    _session.ReceiveInputs(peer, blob);
                    if (!_session.IsPeerActive(peer))
                        TryAssignJoinTick(peer);
                    break;
                case InputSyncPacket.Hash:
                    _session.ReceiveHash(peer, blob);
                    break;
                default:
                    return;
            }
            // Forward verbatim to every other client.
            for (int client = 0; client < _clients.Length; client++)
            {
                if (client != connection && _clients[client].Peer >= 0 && _host.IsConnected(client))
                    _host.TrySend(client, payload, out _);
            }
        }

        public override void OnPacketAcked(int connection, ushort sequence) => _clients[connection].Join.OnPacketAcked(sequence);

        public override void OnPacketLost(int connection, ushort sequence) => _clients[connection].Join.OnPacketLost(sequence);

        // The joiner's inputs start counting once it has caught up to within the input delay plus the
        // rollback window of the host: close enough that the others wait for its first inputs briefly
        // at most. The join tick is never earlier than the host's next input tick, so no peer has
        // simulated it with the host's inputs yet.
        private void TryAssignJoinTick(int peer)
        {
            int reported = _session.RemoteConfirmedThrough(peer, peer);
            if (reported == InputSyncSession<TInput>.Unknown || reported < _session.Tick - _config.InputDelay - _config.MaxRollbackTicks)
                return;
            _session.SetPeerJoinTick(peer, Math.Max(_session.LocalInputTick, reported + 1));
        }

        // Hands the connection a peer index: the one its identity held before when that peer was
        // dropped and may rejoin (token mode uses the token's client id), else a free one.
        private void AssignPeer(int connection, ref ClientState state, ulong identity)
        {
            ulong tokenId = _host.GetClientId(connection);
            if (tokenId != 0)
                identity = tokenId;
            int peer = -1;
            if (identity != 0)
            {
                for (int i = 1; i < _session.PeerCount; i++)
                {
                    if (_identity[i] == identity && !_online[i])
                    {
                        if (_session.CanRejoin(i))
                        {
                            peer = i;
                            Rejoins++;
                        }
                        else if (_session.IsPeerAbsent(i))
                            peer = i; // the same client reconnecting before it ever joined
                        break;
                    }
                }
            }
            if (peer < 0)
            {
                for (int i = 1; i < _session.PeerCount && peer < 0; i++)
                {
                    if (_identity[i] == 0 && !_online[i] && _session.IsPeerAbsent(i))
                        peer = i;
                }
            }
            if (peer < 0)
            {
                _host.Disconnect(connection);
                return;
            }
            _identity[peer] = identity;
            _online[peer] = true;
            _nextPeer = Math.Max(_nextPeer, peer + 1);
            state.Peer = peer;
            _session.ForgetRemoteConfirmed(peer); // a new connection starts from nothing, whatever the old one had
            SendWelcome(connection);
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> buffer, int offset)
        {
            ulong value = 0;
            for (int i = 7; i >= 0; i--)
                value = (value << 8) | buffer[offset + i];
            return value;
        }

        private void BeginJoin(ref ClientState state)
        {
            if (_session.HasPendingRollback)
                return; // the history is about to change; snapshot after it settled
            int tick = 0;
            int bytes = 0;
            if (_config.JoinMode == JoinMode.Snapshot)
            {
                tick = Math.Min(_session.ConfirmedTick + 1, _session.Tick);
                if (!_session.TryGetState(tick, out WorldState snapshot))
                    return; // confirmed too far back for the history; try again once it caught up
                bytes = snapshot.SerializedSize;
                state.Snapshot = new byte[bytes];
                snapshot.Serialize(state.Snapshot);
            }
            state.SnapshotOffset = 0;
            Span<byte> start = stackalloc byte[10];
            start[0] = JoinMessage.Start;
            start[1] = (byte)_config.JoinMode;
            WriteInt32(start, 2, tick);
            WriteInt32(start, 6, bytes);
            state.Join.TrySend(start);
            state.JoinStarted = true;
            state.JoinEnded = false;
        }

        private void PumpJoin(int client, ref ClientState state)
        {
            ReliableChannel channel = state.Join;
            int chunk = _config.JoinChannel.MaxMessageSize - 1;
            while (!state.JoinEnded && channel.CanSend)
            {
                int remaining = state.Snapshot == null ? 0 : state.Snapshot.Length - state.SnapshotOffset;
                if (remaining > 0)
                {
                    int length = Math.Min(chunk, remaining);
                    _scratch[0] = JoinMessage.Chunk;
                    Array.Copy(state.Snapshot, state.SnapshotOffset, _scratch, 1, length);
                    channel.TrySend(new ReadOnlySpan<byte>(_scratch, 0, length + 1));
                    state.SnapshotOffset += length;
                }
                else
                {
                    // The table rides along so the joiner never simulates without knowing who is present.
                    _scratch[0] = JoinMessage.End;
                    int table = _session.WritePeerTable(new Span<byte>(_scratch, 1, _scratch.Length - 1));
                    channel.TrySend(new ReadOnlySpan<byte>(_scratch, 0, 1 + table));
                    state.JoinEnded = true;
                    state.Snapshot = null;
                }
            }
            double now = _host.Now;
            double rtt = _host.GetStats(client).SmoothedRtt;
            for (int i = 0; i < _config.MaxJoinPacketsPerPump; i++)
            {
                if (!channel.TryWritePacket(new Span<byte>(_scratch, InputSyncPacket.HeaderSize, _scratch.Length - InputSyncPacket.HeaderSize), now, rtt, out int written))
                    break;
                _scratch[0] = InputSyncPacket.Join;
                _scratch[1] = (byte)state.Peer;
                if (_host.TrySend(client, new ReadOnlySpan<byte>(_scratch, 0, InputSyncPacket.HeaderSize + written), out ushort sequence))
                    channel.PacketSent(sequence);
                else
                    channel.PacketDropped();
            }
        }

        // Resends, from the host's buffers, inputs old enough that the senders' redundancy and the
        // acknowledgement round trip no longer cover them, which the client still reports missing.
        private void FillGaps(int client, ref ClientState state)
        {
            for (int peer = 0; peer < _session.PeerCount; peer++)
            {
                int reported = _session.RemoteConfirmedThrough(state.Peer, peer);
                if (reported == InputSyncSession<TInput>.Unknown)
                    continue;
                // Inputs of an active peer may still be in flight from the peer itself; a closed interval is final.
                int to = _session.ConfirmedThrough(peer) - (_session.IsPeerActive(peer) ? _config.HostResendDelayTicks : 0);
                if (peer == state.Peer)
                    to = Math.Min(to, _session.LastClosedDrop(peer) - 1); // a rejoiner's own past only; its current inputs are its own
                if (reported >= to)
                    continue;
                int size = _session.WriteInputRange(peer, reported + 1, to,
                    new Span<byte>(_scratch, InputSyncPacket.HeaderSize, _scratch.Length - InputSyncPacket.HeaderSize));
                if (size <= 0)
                    continue;
                _scratch[0] = InputSyncPacket.Relayed;
                _scratch[1] = (byte)peer;
                if (_host.TrySend(client, new ReadOnlySpan<byte>(_scratch, 0, InputSyncPacket.HeaderSize + size), out _))
                    GapFills++;
            }
        }

        private void SendTable(int client, ref ClientState state)
        {
            int size = _session.WritePeerTable(new Span<byte>(_table, InputSyncPacket.HeaderSize, _table.Length - InputSyncPacket.HeaderSize));
            if (size <= 0)
                return;
            _table[0] = InputSyncPacket.Control;
            _table[1] = 0;
            if (_host.TrySend(client, new ReadOnlySpan<byte>(_table, 0, InputSyncPacket.HeaderSize + size), out _))
                state.TableVersionSent = _session.PeerTableVersion;
        }

        private void Broadcast(byte kind, int peer, int blobSize, int exceptClient)
        {
            _scratch[0] = kind;
            _scratch[1] = (byte)peer;
            var payload = new ReadOnlySpan<byte>(_scratch, 0, InputSyncPacket.HeaderSize + blobSize);
            for (int client = 0; client < _clients.Length; client++)
            {
                if (client != exceptClient && _clients[client].Peer >= 0 && _host.IsConnected(client))
                    _host.TrySend(client, payload, out _);
            }
        }

        private void SendWelcome(int client)
        {
            Span<byte> packet = stackalloc byte[InputSyncPacket.HeaderSize];
            packet[0] = InputSyncPacket.Welcome;
            packet[1] = (byte)_clients[client].Peer;
            _host.TrySend(client, packet, out _);
        }

        private static void WriteInt32(Span<byte> buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        internal static int ReadInt32(ReadOnlySpan<byte> buffer, int offset)
        {
            return buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
        }

        public void Dispose() => _host.Dispose();
    }

    /// <summary>
    /// Client side of the star: connects, waits for its peer index and the host's join data, creates
    /// the session through the factory (started from the host's snapshot, or from tick 0 in replay
    /// mode) and then exchanges packets with the host, which forwards them to the other peers. The
    /// factory receives the peer index and must return a session whose world is the game's initial
    /// world; a snapshot join replaces that world's contents.
    /// </summary>
    public sealed class InputSyncClientLink<TInput> : NetListener, IDisposable where TInput : unmanaged
    {
        private readonly NetClient _client;
        private readonly Func<int, InputSyncSession<TInput>> _createSession;
        private readonly byte[] _scratch;
        private readonly ReliableChannel _join;
        private InputSyncSession<TInput> _session;
        private byte[] _snapshot;
        private int _snapshotOffset;
        private int _snapshotSize;
        private JoinMode _joinMode;
        private int _joinTick;
        private int _peer = -1;

        /// <param name="config">The session config; the join channel settings must match the host's.</param>
        public InputSyncClientLink(NetConfig netConfig, IUdpSocket socket, IRandomSource random, InputSyncConfig config, Func<int, InputSyncSession<TInput>> createSession)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
            _client = new NetClient(netConfig, socket, random, this);
            _scratch = new byte[netConfig.MaxPayloadSize];
            _join = new ReliableChannel(config.JoinChannel);
            do
                Identity = random.NextUInt64();
            while (Identity == 0);
        }

        /// <summary>
        /// Who this client is to the host, sent in the Hello: a client that reconnects with the same
        /// identity gets its old peer index back. Random per link by default; a game that wants
        /// rejoins across restarts stores it (in token mode the token's client id is used instead).
        /// Set before <see cref="Connect"/>.
        /// </summary>
        public ulong Identity { get; set; }

        public NetClient Client => _client;

        /// <summary>Null until the host's join data arrived.</summary>
        public InputSyncSession<TInput> Session => _session;

        public int Peer => _peer;

        /// <summary>Snapshot bytes received so far and in total while joining; both 0 in replay mode.</summary>
        public int SnapshotReceived => _snapshotOffset;
        public int SnapshotSize => _snapshotSize;

        /// <summary>True while the local inputs count: the host assigned a join tick and has not dropped this peer.</summary>
        public bool Joined => _session != null && _session.IsPeerActive(_peer);

        public void Connect(in Address host, double now) => _client.Connect(host, now);

        public void Update(double now) => _client.Update(now);

        /// <summary>Sends the Hello until welcomed, then the local inputs and any due hash to the host.</summary>
        public void Pump()
        {
            if (!_client.IsConnected)
                return;
            if (_peer < 0)
            {
                _scratch[0] = InputSyncPacket.Hello;
                _scratch[1] = InputSyncPacket.NoPeer;
                ulong identity = Identity;
                for (int i = 0; i < 8; i++)
                    _scratch[InputSyncPacket.HeaderSize + i] = (byte)(identity >> (i * 8));
                _client.TrySend(new ReadOnlySpan<byte>(_scratch, 0, InputSyncPacket.HeaderSize + 8), out _);
                return;
            }
            if (_session == null)
                return;
            int size = _session.WriteInputs(new Span<byte>(_scratch, InputSyncPacket.HeaderSize, _scratch.Length - InputSyncPacket.HeaderSize));
            if (size > 0)
                Send(InputSyncPacket.Inputs, size);
            size = _session.WriteHash(new Span<byte>(_scratch, InputSyncPacket.HeaderSize, _scratch.Length - InputSyncPacket.HeaderSize));
            if (size > 0)
                Send(InputSyncPacket.Hash, size);
        }

        public override void OnDisconnected(int connection, DisconnectReason reason)
        {
            _snapshot = null;
            _snapshotOffset = 0;
            _join.Reset();
        }

        public override void OnPacket(int connection, ushort sequence, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < InputSyncPacket.HeaderSize)
                return;
            byte kind = payload[0];
            int peer = payload[1];
            ReadOnlySpan<byte> blob = payload.Slice(InputSyncPacket.HeaderSize);
            switch (kind)
            {
                case InputSyncPacket.Welcome:
                    if (_peer < 0)
                    {
                        _peer = peer;
                        DrainJoin(); // join messages may have overtaken the welcome
                    }
                    return;
                case InputSyncPacket.Join:
                    // Always stored: the transport acknowledges the packet whether or not the
                    // session exists yet, so an ignored message would never be resent.
                    ReceiveJoin(blob);
                    return;
                case InputSyncPacket.Control:
                    _session?.ApplyPeerTable(blob);
                    return;
                case InputSyncPacket.Inputs:
                    _session?.ReceiveInputs(peer, blob);
                    return;
                case InputSyncPacket.Relayed:
                    _session?.ReceiveInputs(peer, 0, blob);
                    return;
                case InputSyncPacket.Hash:
                    _session?.ReceiveHash(peer, blob);
                    return;
            }
        }

        private void ReceiveJoin(ReadOnlySpan<byte> blob)
        {
            _join.OnPacket(blob);
            DrainJoin();
        }

        private void DrainJoin()
        {
            while (_peer >= 0 && _session == null && _join.TryPeek(out ReadOnlySpan<byte> message))
            {
                if (message.Length >= 1)
                    HandleJoinMessage(message);
                _join.Dequeue();
            }
        }

        private void HandleJoinMessage(ReadOnlySpan<byte> message)
        {
            switch (message[0])
            {
                case JoinMessage.Start:
                    if (message.Length < 10)
                        return;
                    _joinMode = (JoinMode)message[1];
                    _joinTick = InputSyncRelayHost<TInput>.ReadInt32(message, 2);
                    int bytes = InputSyncRelayHost<TInput>.ReadInt32(message, 6);
                    _snapshot = bytes > 0 ? new byte[bytes] : null;
                    _snapshotSize = Math.Max(bytes, 0);
                    _snapshotOffset = 0;
                    return;
                case JoinMessage.Chunk:
                    int length = message.Length - 1;
                    if (_snapshot == null || _snapshotOffset + length > _snapshot.Length)
                        return;
                    message.Slice(1).CopyTo(new Span<byte>(_snapshot, _snapshotOffset, length));
                    _snapshotOffset += length;
                    return;
                case JoinMessage.End:
                    InputSyncSession<TInput> session = _createSession(_peer);
                    if (_joinMode == JoinMode.Snapshot)
                    {
                        if (_snapshot == null || _snapshotOffset != _snapshot.Length)
                            return;
                        var state = new WorldState();
                        if (!state.Deserialize(_snapshot, session.World))
                            return;
                        session.StartFromHost(state, _joinTick);
                    }
                    else
                        session.StartFromHost(null, 0);
                    session.ApplyPeerTable(message.Slice(1));
                    _snapshot = null;
                    _session = session;
                    return;
            }
        }

        private void Send(byte kind, int blobSize)
        {
            _scratch[0] = kind;
            _scratch[1] = (byte)_peer;
            _client.TrySend(new ReadOnlySpan<byte>(_scratch, 0, InputSyncPacket.HeaderSize + blobSize), out _);
        }

        public void Dispose() => _client.Dispose();
    }
}
