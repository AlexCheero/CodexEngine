using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CodexECS;
using CodexNetworking.History;
using CodexNetworking.Serialization;

namespace CodexNetworking.Sync.InputSync
{
    /// <summary>One simulation tick driven by every peer's input for that tick, indexed by peer.</summary>
    public interface IInputStep<TInput> where TInput : unmanaged
    {
        void Step(EcsWorld world, int tick, ReadOnlySpan<TInput> inputs, bool resimulating);
    }

    /// <summary>
    /// Deterministic input-synchronized simulation for one peer: lockstep with input delay, optional
    /// prediction with rollback, redundant input packets with acknowledgement-driven resend, and
    /// periodic state hashes. Transport agnostic: the owner moves the byte blobs produced by
    /// <see cref="WriteInputs"/> and <see cref="WriteHash"/> to the other peers and feeds theirs into
    /// <see cref="ReceiveInputs(int, ReadOnlySpan{byte})"/> and <see cref="ReceiveHash"/>. Every peer
    /// runs the same code with the same config.
    ///
    /// Peers need not all be present from tick 0. A remote peer is absent until its join tick is
    /// known; its inputs are the default value before the join tick and from its drop tick on, and
    /// every peer applies the same join and drop ticks (decided by the host and carried in the peer
    /// table), which keeps the simulation identical everywhere. The local peer's own join tick is
    /// 0 for a session started with <see cref="Start"/> and otherwise comes from the host too.
    /// </summary>
    public sealed class InputSyncSession<TInput> where TInput : unmanaged
    {
        private sealed class StepAdapter : ISimulationStep
        {
            public InputSyncSession<TInput> Owner;

            public void Step(EcsWorld world, int tick, bool resimulating)
            {
                Owner.FillFrame(tick);
                Owner._step.Step(world, tick, Owner._frame, resimulating);
            }
        }

        private struct PendingHash
        {
            public int Tick;
            public ulong Hash;
            public bool Used;
        }

        private const int PendingHashes = 16;
        private const int MaxInputsPerPacket = 127;

        /// <summary>Join tick of a peer that has not joined; its inputs are default for every tick.</summary>
        public const int Absent = int.MaxValue;

        /// <summary>Drop tick of a peer that was not dropped.</summary>
        public const int NotDropped = int.MaxValue;

        /// <summary>Value of <see cref="RemoteConfirmedThrough"/> before the peer's first packet.</summary>
        public const int Unknown = int.MinValue;

        private readonly InputSyncConfig _config;
        private readonly IInputStep<TInput> _step;
        private readonly SimulationLoop _loop;
        private readonly InputBuffer<TInput>[] _inputs;
        private readonly int[] _confirmedThrough;
        // [peer][of]: what each remote peer reported as its confirmed-through of every peer's inputs.
        private readonly int[][] _remoteConfirmed;
        // Current interval of each peer: real inputs apply to ticks in [join, drop).
        private readonly int[] _joinTick;
        private readonly int[] _droppedFrom;
        // Earlier, closed intervals (a peer that was dropped and rejoined): [join, drop) pairs.
        private readonly int[][] _pastJoin;
        private readonly int[][] _pastDrop;
        private readonly int[] _pastCount;
        private readonly PendingHash[][] _remoteHashes;
        private bool _tableKnown;
        private int _tableTick;
        private readonly TInput[] _frame;
        private readonly List<TInput>[] _log;
        private readonly int _localPeer;
        private int _rollbackTo = -1;
        private int _nextHashTick;
        private int _hashSendTick = -1;
        private bool _hashReady;
        private int _startTick;
        private int _peerTableVersion;

        public InputSyncSession(EcsWorld world, IInputStep<TInput> step, SimulationRegistry registry, InputSyncConfig config, int localPeer)
        {
            config.Validate();
            if (localPeer < 0 || localPeer >= config.PeerCount)
                throw new ArgumentOutOfRangeException(nameof(localPeer));
            _config = config;
            _step = step ?? throw new ArgumentNullException(nameof(step));
            _localPeer = localPeer;
            _loop = new SimulationLoop(world, new StepAdapter { Owner = this }, registry, config.HistoryCapacity);
            _inputs = new InputBuffer<TInput>[config.PeerCount];
            _confirmedThrough = new int[config.PeerCount];
            _remoteConfirmed = new int[config.PeerCount][];
            _joinTick = new int[config.PeerCount];
            _droppedFrom = new int[config.PeerCount];
            _pastJoin = new int[config.PeerCount][];
            _pastDrop = new int[config.PeerCount][];
            _pastCount = new int[config.PeerCount];
            _remoteHashes = new PendingHash[config.PeerCount][];
            _log = config.KeepInputLog ? new List<TInput>[config.PeerCount] : null;
            for (int i = 0; i < config.PeerCount; i++)
            {
                _inputs[i] = new InputBuffer<TInput>(config.InputBufferCapacity);
                _remoteConfirmed[i] = new int[config.PeerCount];
                _pastJoin[i] = new int[config.MaxIntervalsPerPeer - 1];
                _pastDrop[i] = new int[config.MaxIntervalsPerPeer - 1];
                _remoteHashes[i] = new PendingHash[PendingHashes];
                if (_log != null)
                    _log[i] = new List<TInput>();
            }
            _frame = new TInput[config.PeerCount];
            ResetPeers(0);
        }

        public InputSyncConfig Config => _config;
        public int LocalPeer => _localPeer;
        public int PeerCount => _config.PeerCount;
        public SimulationLoop Loop => _loop;
        public EcsWorld World => _loop.World;

        /// <summary>Ticks simulated so far; also the next tick to simulate.</summary>
        public int Tick => _loop.Tick;

        /// <summary>Tick the session started at (0, or the tick of the snapshot it joined from).</summary>
        public int StartTick => _startTick;

        /// <summary>The tick a local input submitted now applies to.</summary>
        public int LocalInputTick => _loop.Tick + _config.InputDelay;

        public int RollbackCount => _loop.RollbackCount;
        public long ResimulatedTicks => _loop.ResimulatedTicks;

        /// <summary>A confirmed input contradicted a simulated tick; the next <see cref="TryAdvance"/> rolls back first.</summary>
        public bool HasPendingRollback => _rollbackTo >= 0;

        /// <summary>Calls to <see cref="TryAdvance"/> that could not simulate because inputs were missing.</summary>
        public int Stalls { get; private set; }

        /// <summary>Bumped whenever a join or drop tick changes; lets the host know when to resend the peer table.</summary>
        public int PeerTableVersion => _peerTableVersion;

        /// <summary>
        /// Rate for the game's <see cref="TickClock"/> so the peers stay level: below 1 when this
        /// peer runs ahead of the others, above 1 when it lags, 1 inside the dead band. Measured
        /// the GGPO way: our lead over a peer (our newest input tick minus the newest input we hold
        /// from it) against the lead that peer last reported over us, which cancels the transit
        /// delay both see. Updated whenever a remote input packet is received.
        /// </summary>
        public double RecommendedRateScale { get; private set; } = 1.0;

        /// <summary>Average ticks this peer is ahead of the active remote peers; negative when behind.</summary>
        public double FrameAdvantage { get; private set; }

        /// <summary>
        /// Highest tick for which every peer's input is confirmed or defaulted. Peers that are absent,
        /// or dropped with nothing left unconfirmed, do not hold it back.
        /// </summary>
        public int ConfirmedTick
        {
            get
            {
                int min = int.MaxValue;
                for (int i = 0; i < _confirmedThrough.Length; i++)
                    min = Math.Min(min, OldestUnresolved(i));
                return min == int.MaxValue ? int.MaxValue - 1 : min - 1;
            }
        }

        /// <summary>Highest tick with a contiguous run of real confirmed inputs from the peer (join-tick aware).</summary>
        public int ConfirmedThrough(int peer) => _confirmedThrough[peer];

        /// <summary>What <paramref name="peer"/> last reported as its confirmed-through of <paramref name="of"/>'s inputs, or <see cref="Unknown"/>.</summary>
        public int RemoteConfirmedThrough(int peer, int of) => _remoteConfirmed[peer][of];

        /// <summary>Forgets what a peer reported (host side, when a new connection takes over its index).</summary>
        public void ForgetRemoteConfirmed(int peer)
        {
            for (int of = 0; of < _inputs.Length; of++)
                _remoteConfirmed[peer][of] = Unknown;
        }

        /// <summary>
        /// Newest host tick whose joins this peer knows about (from the peer table). Hashes are only
        /// sent and compared for ticks up to it: a join the host decides later always lies beyond
        /// it, so a tick inside is never re-simulated for a peer that turned out to be present.
        /// </summary>
        public int PeerTableTick => _tableTick;

        /// <summary>Join tick of the peer's current (latest) interval; <see cref="Absent"/> when it never joined.</summary>
        public int JoinTickOf(int peer) => _joinTick[peer];

        /// <summary>Drop tick of the peer's current interval; <see cref="NotDropped"/> while it plays.</summary>
        public int DroppedFromOf(int peer) => _droppedFrom[peer];

        /// <summary>The peer's current interval is closed: it left (or was a spectator from the start).</summary>
        public bool IsPeerDropped(int peer) => _droppedFrom[peer] != NotDropped;

        /// <summary>The peer never joined; its inputs are default everywhere.</summary>
        public bool IsPeerAbsent(int peer) => _joinTick[peer] == Absent;

        /// <summary>The peer has joined and has not been dropped since: its inputs count right now.</summary>
        public bool IsPeerActive(int peer) => _joinTick[peer] != Absent && _droppedFrom[peer] == NotDropped;

        /// <summary>Intervals the peer has had: earlier closed ones plus the current one.</summary>
        public int IntervalCount(int peer) => _pastCount[peer] + (_joinTick[peer] != Absent || _droppedFrom[peer] != NotDropped ? 1 : 0);

        /// <summary>Drop tick of the peer's newest closed earlier interval, or -1 when it never rejoined.</summary>
        public int LastPastDrop(int peer) => _pastCount[peer] == 0 ? -1 : _pastDrop[peer][_pastCount[peer] - 1];

        /// <summary>End of the peer's newest closed interval (the current one when dropped), or -1 when none closed.</summary>
        public int LastClosedDrop(int peer) => IsPeerDropped(peer) && !IsPeerAbsent(peer) ? _droppedFrom[peer] : LastPastDrop(peer);

        /// <summary>Whether the peer may still join again (the table has room for another interval).</summary>
        public bool CanRejoin(int peer) => IsPeerDropped(peer) && !IsPeerAbsent(peer) && _pastCount[peer] < _pastJoin[peer].Length;

        /// <summary>True once the host's peer table has been applied; a joiner does not simulate before that.</summary>
        public bool PeerTableKnown => _tableKnown;

        /// <summary>Raised once per detected mismatch with (tick, peer). Nothing recovers automatically.</summary>
        public event Action<int, int> Desynced;

        /// <summary>Raised when a rollback target fell out of the history; the session is no longer trustworthy.</summary>
        public event Action<int> RollbackFailed;

        /// <summary>
        /// Starts at tick 0 from the current world: snapshots it and fills the first InputDelay ticks
        /// of every peer with default inputs so the first ticks can run before any packet arrives.
        /// The local peer joins at tick 0. Remote peers are absent until their join tick is set,
        /// unless <paramref name="allPeersPresent"/>, which has every peer join at tick 0 (for
        /// sessions without a host, where the peers agree to start together).
        /// </summary>
        public void Start(bool allPeersPresent = false)
        {
            ResetPeers(0);
            _loop.Start(0);
            TInput none = default;
            for (int peer = 0; peer < _inputs.Length; peer++)
            {
                for (int t = 0; t < _config.InputDelay; t++)
                {
                    _inputs[peer].Set(t, none, true);
                    Log(peer, t, none);
                }
                _confirmedThrough[peer] = _config.InputDelay - 1;
                if (allPeersPresent)
                    _joinTick[peer] = 0;
            }
            _joinTick[_localPeer] = 0;
            _tableKnown = true;
            _tableTick = int.MaxValue; // the host (or a host-less mesh) decides joins itself
            StartSchedules(0);
        }

        /// <summary>
        /// Joins a running session: restores the host's <paramref name="snapshot"/> of <paramref name="tick"/>
        /// into the world and continues from there, or, with a null snapshot, replays from the initial
        /// world at tick 0. Join and drop ticks, the local one included, arrive through
        /// <see cref="ApplyPeerTable"/>; every peer's inputs since the tick arrive through
        /// <see cref="ReceiveInputs(int, int, ReadOnlySpan{byte})"/>. Until the local join tick is
        /// known the local inputs are default.
        /// </summary>
        public void StartFromHost(WorldState snapshot, int tick)
        {
            if (snapshot == null)
                tick = 0;
            ResetPeers(tick);
            if (snapshot != null)
                _loop.World.RestoreState(snapshot);
            _loop.Start(tick);
            TInput none = default;
            for (int t = tick; t < tick + _config.InputDelay; t++)
                _inputs[_localPeer].Set(t, none, true);
            _confirmedThrough[_localPeer] = tick + _config.InputDelay - 1;
            StartSchedules(tick);
        }

        private void ResetPeers(int tick)
        {
            _startTick = tick;
            _tableKnown = false;
            _tableTick = -1;
            for (int peer = 0; peer < _inputs.Length; peer++)
            {
                _inputs[peer].Clear();
                _confirmedThrough[peer] = tick - 1;
                _joinTick[peer] = Absent;
                _droppedFrom[peer] = NotDropped;
                _pastCount[peer] = 0;
                for (int of = 0; of < _inputs.Length; of++)
                    _remoteConfirmed[peer][of] = Unknown;
                for (int i = 0; i < PendingHashes; i++)
                    _remoteHashes[peer][i].Used = false;
                if (_log != null)
                    _log[peer].Clear();
            }
            _peerTableVersion++;
        }

        private void StartSchedules(int tick)
        {
            _nextHashTick = (tick / _config.HashInterval + 1) * _config.HashInterval;
            _hashSendTick = tick;
            _hashReady = false;
            _rollbackTo = -1;
        }

        /// <summary>A snapshot from the history; the state of <paramref name="tick"/> before it was simulated.</summary>
        public bool TryGetState(int tick, out WorldState state) => _loop.History.TryGet(tick, out state);

        /// <summary>
        /// Declares from which tick a peer's real inputs apply: the first join of an absent peer, or
        /// a rejoin of a dropped one (the tick must not precede its drop). Before a join and between
        /// a drop and a rejoin the inputs are default. The host decides the ticks and every peer
        /// applies the same values; an interval never changes once set. Setting the local peer's own
        /// join tick is how a joiner learns when its inputs start counting. False when nothing
        /// changed (already joined, or no room for another interval).
        /// </summary>
        public bool SetPeerJoinTick(int peer, int joinTick)
        {
            if (joinTick == Absent || _joinTick[peer] == joinTick)
                return false;
            bool rejoin = false;
            if (_joinTick[peer] != Absent)
            {
                // A new interval needs the current one closed and room to archive it.
                if (!IsPeerDropped(peer) || joinTick < _droppedFrom[peer] || _pastCount[peer] >= _pastJoin[peer].Length)
                    return false;
                _pastJoin[peer][_pastCount[peer]] = _joinTick[peer];
                _pastDrop[peer][_pastCount[peer]] = _droppedFrom[peer];
                _pastCount[peer]++;
                rejoin = true;
            }
            else if (IsPeerDropped(peer))
                return false; // left before ever joining: stays default
            _joinTick[peer] = joinTick;
            _droppedFrom[peer] = NotDropped;
            _peerTableVersion++;
            if (peer == _localPeer)
            {
                // Nobody will acknowledge our inputs before our join tick: never resend them. The
                // inputs submitted so far were stored as defaults, so nothing simulated changes.
                for (int q = 0; q < _inputs.Length; q++)
                {
                    if (_remoteConfirmed[q][_localPeer] != Unknown && _remoteConfirmed[q][_localPeer] < joinTick - 1)
                        _remoteConfirmed[q][_localPeer] = joinTick - 1;
                }
                return true;
            }
            // Real inputs are contiguous from the first join; the defaulted gap before a rejoin is
            // walked by AdvanceConfirmed once the earlier interval is complete.
            if (!rejoin && _confirmedThrough[peer] < joinTick - 1)
                _confirmedThrough[peer] = joinTick - 1;
            AdvanceConfirmed(peer);
            // Ticks already simulated with defaults for this peer must be redone with its inputs.
            ScheduleRollback(joinTick);
            return true;
        }

        /// <summary>
        /// Drops a peer: its inputs are the default value from <paramref name="fromTick"/> on, whatever
        /// arrives later, until a rejoin. The host decides the tick and every peer applies the same
        /// one. Ticks already simulated with other inputs are rolled back.
        /// </summary>
        public void DropPeer(int peer, int fromTick)
        {
            if (_droppedFrom[peer] <= fromTick)
                return;
            bool wasAbsent = IsPeerAbsent(peer);
            if (!wasAbsent && fromTick < _joinTick[peer])
                fromTick = _joinTick[peer];
            _droppedFrom[peer] = fromTick;
            _peerTableVersion++;
            if (!wasAbsent)
                ScheduleRollback(fromTick); // an absent peer was default already: nothing changes
            UpdateHashSchedule();
        }

        /// <summary>
        /// Writes every peer's intervals (host side): a count, then (join, drop) pairs, earlier ones
        /// first and the current one last. Returns the byte count, 0 when the buffer is too small.
        /// </summary>
        public int WritePeerTable(Span<byte> buffer)
        {
            var writer = new BitWriter(buffer);
            // Stamp: every join decided after this table lies beyond this tick.
            writer.WriteInt32(_tableTick == int.MaxValue ? _loop.Tick : _tableTick);
            for (int peer = 0; peer < _inputs.Length; peer++)
            {
                int count = IntervalCount(peer);
                writer.WriteByte((byte)count);
                for (int i = 0; i < _pastCount[peer]; i++)
                {
                    writer.WriteInt32(_pastJoin[peer][i]);
                    writer.WriteInt32(_pastDrop[peer][i]);
                }
                if (count > _pastCount[peer])
                {
                    writer.WriteInt32(_joinTick[peer]);
                    writer.WriteInt32(_droppedFrom[peer]);
                }
            }
            return writer.Overflowed ? 0 : writer.Flush();
        }

        /// <summary>Applies a peer table written by the host; intervals already known are skipped.</summary>
        public bool ApplyPeerTable(ReadOnlySpan<byte> payload)
        {
            var reader = new BitReader(payload);
            if (!reader.ReadInt32(out int stamp))
                return false;
            for (int peer = 0; peer < _inputs.Length; peer++)
            {
                if (!reader.ReadByte(out byte count))
                    return false;
                for (int i = 0; i < count; i++)
                {
                    if (!reader.ReadInt32(out int joinTick) || !reader.ReadInt32(out int droppedFrom))
                        return false;
                    int known = IntervalCount(peer);
                    if (i < known - 1)
                        continue; // an earlier interval we already hold
                    if (i == known - 1)
                    {
                        // Our current interval: it may have closed since.
                        if (droppedFrom != NotDropped)
                            DropPeer(peer, droppedFrom);
                        continue;
                    }
                    if (joinTick != Absent)
                        SetPeerJoinTick(peer, joinTick);
                    if (droppedFrom != NotDropped)
                        DropPeer(peer, droppedFrom);
                }
            }
            _tableKnown = true;
            if (_tableTick != int.MaxValue && stamp > _tableTick)
            {
                _tableTick = stamp;
                UpdateHashSchedule();
                CheckPendingHashes();
            }
            return true;
        }

        /// <summary>
        /// Records the local input for <see cref="LocalInputTick"/>. Call once per tick before advancing.
        /// While the local join tick is unknown the input is replaced by the default value, so the
        /// ticks the host may still assign to the join carry what everyone simulates for them.
        /// </summary>
        public void SubmitLocalInput(in TInput input)
        {
            int tick = LocalInputTick;
            // A rejoining peer replaying its own past: those ticks come from the host's buffers.
            if (InClosedInterval(_localPeer, tick))
                return;
            TInput stored = IsPeerActive(_localPeer) ? input : default;
            if (_inputs[_localPeer].Set(tick, stored, true))
                Log(_localPeer, tick, stored);
            AdvanceConfirmed(_localPeer);
        }

        /// <summary>
        /// Applies a pending rollback and simulates one tick if every input it needs is confirmed,
        /// defaulted, or may be predicted within the rollback window. Returns false (a stall) otherwise.
        /// </summary>
        public bool TryAdvance()
        {
            if (!_tableKnown)
            {
                Stalls++; // a joiner must know who is present before it simulates anything
                return false;
            }
            if (_rollbackTo >= 0)
            {
                int target = _rollbackTo;
                _rollbackTo = -1;
                if (!_loop.Rollback(target))
                    RollbackFailed?.Invoke(target);
            }

            int tick = _loop.Tick;
            if (!_inputs[_localPeer].IsConfirmed(tick))
            {
                Stalls++;
                return false;
            }
            for (int peer = 0; peer < _inputs.Length; peer++)
            {
                if (peer == _localPeer)
                    continue;
                // Every unresolved tick must stay inside the rollback window, or it could never be
                // corrected; with a window of 0 this is plain lockstep.
                int oldestUnresolved = OldestUnresolved(peer);
                if (oldestUnresolved <= tick && tick - oldestUnresolved >= _config.MaxRollbackTicks)
                {
                    Stalls++;
                    return false;
                }
            }

            _loop.Advance();
            if (_loop.Tick >= _nextHashTick)
                _nextHashTick += _config.HashInterval;
            CheckPendingHashes();
            return true;
        }

        /// <summary>
        /// Writes local inputs for the other peers: at least the newest RedundantInputs ticks, and
        /// everything back to the oldest tick some peer has not acknowledged yet, so a hole left by
        /// lost packets is always refilled. Also carries this peer's acknowledgements of everyone
        /// else's inputs. Returns the byte count, 0 when there is nothing confirmed yet.
        /// </summary>
        public int WriteInputs(Span<byte> buffer)
        {
            InputBuffer<TInput> local = _inputs[_localPeer];
            if (!local.HasConfirmed)
                return 0;
            int newest = local.LatestConfirmedTick;
            int from = newest - _config.RedundantInputs + 1;
            for (int peer = 0; peer < _inputs.Length; peer++)
            {
                int acked = _remoteConfirmed[peer][_localPeer];
                if (peer != _localPeer && acked != Unknown && IsPeerActive(peer))
                    from = Math.Min(from, acked + 1);
            }
            from = Math.Max(from, Math.Max(_startTick, newest - local.Capacity + 1));
            return WriteRange(_localPeer, from, newest, buffer);
        }

        /// <summary>
        /// Writes a range of any peer's confirmed inputs in the input packet format (host side, for
        /// filling holes of a client or backfilling a joiner). The range is clipped to what is
        /// available, oldest first, at most 127 ticks. Returns 0 when nothing in it is available.
        /// </summary>
        public int WriteInputRange(int peer, int fromTick, int toTick, Span<byte> buffer)
        {
            if (peer < 0 || peer >= _inputs.Length)
                return 0;
            return WriteRange(peer, Math.Max(fromTick, 0), toTick, buffer);
        }

        private int WriteRange(int peer, int from, int to, Span<byte> buffer)
        {
            // Oldest first when the gap is too wide for one packet: holes close before new ticks ship.
            to = Math.Min(to, from + MaxInputsPerPacket - 1);
            while (from <= to && !HasInput(peer, from))
                from++;
            int count = 0;
            while (from + count <= to && HasInput(peer, from + count))
                count++;
            if (count <= 0)
                return 0;
            to = from + count - 1;

            var writer = new BitWriter(buffer);
            writer.WriteInt32(to);
            writer.WriteBits((uint)count, 7);
            INetSerializer<TInput> serializer = NetSerializer<TInput>.Instance;
            for (int t = from; t <= to; t++)
            {
                GetInput(peer, t, out TInput input);
                serializer.Write(ref writer, input, IdentityEntityMap.Instance);
            }
            for (int p = 0; p < _confirmedThrough.Length; p++)
                writer.WriteInt32(_confirmedThrough[p]);
            return writer.Overflowed ? 0 : writer.Flush();
        }

        /// <summary>Applies an input packet written by <paramref name="peer"/> itself.</summary>
        public bool ReceiveInputs(int peer, ReadOnlySpan<byte> payload) => ReceiveInputs(peer, peer, payload);

        /// <summary>
        /// Applies an input packet carrying <paramref name="peer"/>'s inputs, written by that peer or
        /// relayed from the host's buffers (<paramref name="sender"/> = host); the acknowledgements in
        /// it are credited to the sender. Schedules a rollback when a confirmed input contradicts a
        /// prediction already simulated. Inputs for the local peer are accepted only for its earlier
        /// intervals (a rejoining peer replaying what it did before it dropped).
        /// </summary>
        public bool ReceiveInputs(int peer, int sender, ReadOnlySpan<byte> payload)
        {
            if (peer < 0 || peer >= _inputs.Length || sender < 0 || sender >= _inputs.Length || sender == _localPeer)
                return false;
            var reader = new BitReader(payload);
            if (!reader.ReadInt32(out int newest) || !reader.ReadBits(7, out uint rawCount))
                return false;
            int count = (int)rawCount;
            if (count < 1 || count > MaxInputsPerPacket || newest < 0)
                return false;
            INetSerializer<TInput> serializer = NetSerializer<TInput>.Instance;
            InputBuffer<TInput> buffer = _inputs[peer];
            for (int t = newest - count + 1; t <= newest; t++)
            {
                if (!serializer.Read(ref reader, out TInput input, IdentityEntityMap.Instance))
                    return false;
                if (t < 0 || t < _startTick || Defaulted(peer, t) || buffer.IsConfirmed(t))
                    continue;
                if (peer == _localPeer && !InClosedInterval(peer, t))
                    continue; // our current inputs are ours alone
                bool simulated = t < _loop.Tick;
                bool differs = true;
                if (simulated && buffer.TryGet(t, out TInput used, out _))
                    differs = !SameBytes(used, input);
                if (buffer.Set(t, input, true))
                    Log(peer, t, input);
                if (simulated && differs)
                    ScheduleRollback(t);
            }
            int[] remote = _remoteConfirmed[sender];
            for (int i = 0; i < _confirmedThrough.Length; i++)
            {
                if (!reader.ReadInt32(out int acked))
                    return false;
                if (acked > remote[i])
                    remote[i] = acked;
            }
            AdvanceConfirmed(peer);
            CheckPendingHashes();
            UpdatePacing();
            return true;
        }

        /// <summary>
        /// Turns the local peer into a spectator or dedicated relay: its inputs are default from
        /// tick 0, nobody waits for them, and the peer table tells everyone. Call after Start().
        /// </summary>
        public void SetLocalSpectator() => DropPeer(_localPeer, 0);

        private void UpdatePacing()
        {
            int count = 0;
            long total = 0;
            int ourNewest = LocalInputTick - 1;
            for (int q = 0; q < _inputs.Length; q++)
            {
                if (q == _localPeer || !IsPeerActive(q))
                    continue;
                int theirNewest = _remoteConfirmed[q][q];
                int theirViewOfUs = _remoteConfirmed[q][_localPeer];
                if (theirNewest == Unknown || theirViewOfUs == Unknown)
                    continue;
                int ourLead = ourNewest - _confirmedThrough[q];
                int theirLead = theirNewest - theirViewOfUs;
                total += ourLead - theirLead;
                count++;
            }
            if (count == 0)
            {
                FrameAdvantage = 0;
                RecommendedRateScale = 1.0;
                return;
            }
            // Each side's lead carries the same one-way delay; the difference is twice the true gap.
            double advantage = total / (2.0 * count);
            FrameAdvantage = advantage;
            if (Math.Abs(advantage) <= _config.PaceDeadBandTicks)
            {
                RecommendedRateScale = 1.0;
                return;
            }
            double adjust = advantage * _config.PaceGain;
            adjust = Math.Max(-_config.MaxPaceAdjust, Math.Min(_config.MaxPaceAdjust, adjust));
            RecommendedRateScale = 1.0 - adjust;
        }

        /// <summary>
        /// Writes the next state hash due for exchange, once every input that produced it is confirmed.
        /// Returns 0 when none is ready.
        /// </summary>
        public int WriteHash(Span<byte> buffer)
        {
            // A pending rollback means the history is about to change: hash after it is applied.
            if (!_hashReady || _rollbackTo >= 0)
                return 0;
            if (!_loop.TryHash(_hashSendTick, out ulong hash))
            {
                _hashReady = false;
                return 0;
            }
            var writer = new BitWriter(buffer);
            writer.WriteInt32(_hashSendTick);
            writer.WriteUInt64(hash);
            _hashReady = false;
            return writer.Overflowed ? 0 : writer.Flush();
        }

        /// <summary>Compares a remote hash with the local one for that tick, now or once the tick is reached.</summary>
        public bool ReceiveHash(int peer, ReadOnlySpan<byte> payload)
        {
            if (peer < 0 || peer >= _inputs.Length || peer == _localPeer)
                return false;
            var reader = new BitReader(payload);
            if (!reader.ReadInt32(out int tick) || !reader.ReadUInt64(out ulong hash))
                return false;
            if (tick <= _startTick)
                return true; // produced before this peer's history begins
            if (_rollbackTo < 0 && tick <= _loop.Tick && tick <= _tableTick && _loop.TryHash(tick, out ulong local) && ConfirmedTick >= tick - 1)
            {
                if (local != hash)
                    Desynced?.Invoke(tick, peer);
                return true;
            }
            PendingHash[] pending = _remoteHashes[peer];
            int slot = tick % PendingHashes;
            pending[slot].Tick = tick;
            pending[slot].Hash = hash;
            pending[slot].Used = true;
            return true;
        }

        /// <summary>Input used for a peer at a tick (confirmed or predicted); for presentation and debugging.</summary>
        public bool TryGetInput(int peer, int tick, out TInput input, out bool confirmed) => _inputs[peer].TryGet(tick, out input, out confirmed);

        // ---- internals ------------------------------------------------------------------------------

        // Outside every interval of the peer: the default input applies.
        private bool Defaulted(int peer, int tick) => (tick < _joinTick[peer] || tick >= _droppedFrom[peer]) && !InPastInterval(peer, tick);

        // Inside an interval that has ended: an earlier one, or the current one of a dropped peer.
        private bool InClosedInterval(int peer, int tick)
        {
            if (IsPeerDropped(peer) && tick >= _joinTick[peer] && tick < _droppedFrom[peer])
                return true;
            return InPastInterval(peer, tick);
        }

        private bool InPastInterval(int peer, int tick)
        {
            int[] joins = _pastJoin[peer];
            int[] drops = _pastDrop[peer];
            for (int i = 0; i < _pastCount[peer]; i++)
            {
                if (tick >= joins[i] && tick < drops[i])
                    return true;
            }
            return false;
        }

        // First tick of the peer that is neither confirmed nor defaulted; int.MaxValue when there is none.
        private int OldestUnresolved(int peer)
        {
            if (IsPeerAbsent(peer))
                return int.MaxValue;
            int next = _confirmedThrough[peer] + 1;
            if (next < _joinTick[peer] && Defaulted(peer, next))
                next = _joinTick[peer]; // the gap before a join or rejoin is default; a missing earlier input is not
            return next >= _droppedFrom[peer] ? int.MaxValue : next;
        }

        private bool HasInput(int peer, int tick)
        {
            if (_log != null && tick < _log[peer].Count)
                return true;
            return _inputs[peer].IsConfirmed(tick);
        }

        private void GetInput(int peer, int tick, out TInput input)
        {
            if (_inputs[peer].TryGet(tick, out input, out bool confirmed) && confirmed)
                return;
            if (_log != null && tick < _log[peer].Count)
            {
                input = _log[peer][tick];
                return;
            }
            input = default;
        }

        private void Log(int peer, int tick, in TInput input)
        {
            if (_log == null)
                return;
            List<TInput> log = _log[peer];
            while (log.Count < tick)
                log.Add(default);
            if (tick == log.Count)
                log.Add(input);
            else
                log[tick] = input;
        }

        private void ScheduleRollback(int tick)
        {
            tick = Math.Max(tick, _startTick); // nothing before the start tick can be resimulated
            if (tick >= _loop.Tick)
                return; // not simulated yet: nothing to redo
            if (_rollbackTo < 0 || tick < _rollbackTo)
                _rollbackTo = tick;
        }

        private void FillFrame(int tick)
        {
            for (int peer = 0; peer < _inputs.Length; peer++)
            {
                if (Defaulted(peer, tick))
                {
                    _frame[peer] = default;
                    continue;
                }
                if (!_inputs[peer].Resolve(tick, out TInput input))
                    _inputs[peer].Set(tick, input, false); // remember the prediction so a later confirmation can compare
                _frame[peer] = input;
            }
        }

        private void AdvanceConfirmed(int peer)
        {
            InputBuffer<TInput> buffer = _inputs[peer];
            int t = _confirmedThrough[peer] + 1;
            // Real inputs count as confirmed; so does the defaulted gap between a drop and a rejoin,
            // which no packet will ever fill (bounded by the current join tick).
            bool joined = !IsPeerAbsent(peer); // an absent peer is default everywhere: nothing to walk
            while (buffer.IsConfirmed(t) || (joined && t < _joinTick[peer] && Defaulted(peer, t)))
                t++;
            _confirmedThrough[peer] = t - 1;
            UpdateHashSchedule();
        }

        private void UpdateHashSchedule()
        {
            // The hash for tick H depends on inputs of ticks < H: send it once all of them are confirmed.
            int due = _nextHashTick - _config.HashInterval;
            if (due > _hashSendTick && due <= _loop.Tick && due <= _tableTick && ConfirmedTick >= due - 1 && due > _startTick)
            {
                _hashSendTick = due;
                _hashReady = true;
            }
        }

        private void CheckPendingHashes()
        {
            UpdateHashSchedule();
            if (_rollbackTo >= 0)
                return; // compare once the rollback has rewritten the history
            int confirmed = ConfirmedTick;
            for (int peer = 0; peer < _remoteHashes.Length; peer++)
            {
                PendingHash[] pending = _remoteHashes[peer];
                for (int i = 0; i < pending.Length; i++)
                {
                    if (!pending[i].Used || pending[i].Tick > _loop.Tick || pending[i].Tick > _tableTick || confirmed < pending[i].Tick - 1)
                        continue;
                    pending[i].Used = false;
                    if (_loop.TryHash(pending[i].Tick, out ulong local) && local != pending[i].Hash)
                        Desynced?.Invoke(pending[i].Tick, peer);
                }
            }
        }

        private static bool SameBytes(in TInput a, in TInput b)
        {
            TInput x = a;
            TInput y = b;
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref x, 1))
                .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref y, 1)));
        }
    }
}
