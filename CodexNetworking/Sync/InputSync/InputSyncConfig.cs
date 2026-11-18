using System;

namespace CodexNetworking.Sync.InputSync
{
    /// <summary>
    /// Tuning of an input-synchronized session. All peers must use the same PeerCount, InputDelay
    /// and HashInterval. Fighting games run InputDelay 2..3 with MaxRollbackTicks 7..8; an RTS with
    /// thousands of units runs InputDelay 8..15 with MaxRollbackTicks 0 and stalls instead.
    /// </summary>
    public sealed class InputSyncConfig
    {
        /// <summary>Players in the session, including the local one.</summary>
        public int PeerCount = 2;

        /// <summary>Ticks between submitting a local input and the tick it applies to.</summary>
        public int InputDelay = 2;

        /// <summary>
        /// How far the simulation may run ahead of the oldest unconfirmed remote input by predicting it.
        /// 0 means classic lockstep: wait for every input before simulating the tick.
        /// </summary>
        public int MaxRollbackTicks = 8;

        /// <summary>Recent input ticks repeated in every input packet, so loss costs nothing.</summary>
        public int RedundantInputs = 8;

        /// <summary>State hashes are exchanged for every tick that is a multiple of this.</summary>
        public int HashInterval = 30;

        /// <summary>Snapshots kept; power of two, at least MaxRollbackTicks + HashInterval + a margin.</summary>
        public int HistoryCapacity = 128;

        /// <summary>Per-peer input ring; power of two, larger than the history.</summary>
        public int InputBufferCapacity = 512;

        /// <summary>How a peer that connects after the session started gets up to speed.</summary>
        public JoinMode JoinMode = JoinMode.Snapshot;

        /// <summary>Host: seconds without any packet from a joined peer before it is dropped to default inputs.</summary>
        public double PeerTimeout = 3.0;

        /// <summary>Host: every how many pumps it resends inputs a client reports missing.</summary>
        public int HostResendEveryPumps = 3;

        /// <summary>
        /// Host: only inputs at least this many ticks older than its confirmed-through are resent to a
        /// client that reports them missing; newer ones are still in flight or covered by the senders'
        /// redundancy. Should exceed RedundantInputs plus a round trip in ticks.
        /// </summary>
        public int HostResendDelayTicks = 20;

        /// <summary>Host: reliable join packets sent to one client per pump; bounds the snapshot transfer rate.</summary>
        public int MaxJoinPacketsPerPump = 8;

        /// <summary>Host: keep every confirmed input since tick 0 (needed for InputReplay joins and recordings).</summary>
        public bool KeepInputLog;

        /// <summary>
        /// How many times a peer may join (first join plus rejoins after drops). Each interval costs 8
        /// bytes per peer in the peer table; a client beyond the limit gets a fresh index instead.
        /// </summary>
        public int MaxIntervalsPerPeer = 4;

        /// <summary>Pacing: being this many ticks ahead of or behind the other peers (on average) is left alone.</summary>
        public double PaceDeadBandTicks = 1.0;

        /// <summary>Pacing: rate change per tick of advantage beyond the dead band (0.02 = 2% per tick).</summary>
        public double PaceGain = 0.02;

        /// <summary>Pacing: the recommended rate stays within 1 ± this.</summary>
        public double MaxPaceAdjust = 0.1;

        /// <summary>Reliable channel carrying join data (snapshot chunks, input backfill).</summary>
        public Transport.Reliable.ReliableChannelConfig JoinChannel = new Transport.Reliable.ReliableChannelConfig { Window = 128, MaxMessageSize = 1024 };

        public void Validate()
        {
            if (PeerCount < 1 || PeerCount > 64)
                throw new ArgumentOutOfRangeException(nameof(PeerCount));
            if (InputDelay < 0 || InputDelay > 60)
                throw new ArgumentOutOfRangeException(nameof(InputDelay));
            if (MaxRollbackTicks < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxRollbackTicks));
            if (RedundantInputs < 1 || RedundantInputs > 64)
                throw new ArgumentOutOfRangeException(nameof(RedundantInputs));
            if (HashInterval < 1)
                throw new ArgumentOutOfRangeException(nameof(HashInterval));
            if (!IsPowerOfTwo(HistoryCapacity) || HistoryCapacity < MaxRollbackTicks + HashInterval + 2)
                throw new ArgumentOutOfRangeException(nameof(HistoryCapacity), "power of two covering rollback and hash intervals");
            if (!IsPowerOfTwo(InputBufferCapacity) || InputBufferCapacity <= HistoryCapacity)
                throw new ArgumentOutOfRangeException(nameof(InputBufferCapacity), "power of two larger than HistoryCapacity");
            if (PeerTimeout <= 0)
                throw new ArgumentOutOfRangeException(nameof(PeerTimeout));
            if (HostResendEveryPumps < 1 || MaxJoinPacketsPerPump < 1 || HostResendDelayTicks < 0)
                throw new ArgumentOutOfRangeException(nameof(HostResendEveryPumps));
            if (PaceDeadBandTicks < 0 || PaceGain < 0 || MaxPaceAdjust < 0 || MaxPaceAdjust > 0.5)
                throw new ArgumentOutOfRangeException(nameof(PaceGain));
            if (MaxIntervalsPerPeer < 1 || MaxIntervalsPerPeer > 16)
                throw new ArgumentOutOfRangeException(nameof(MaxIntervalsPerPeer));
            if (JoinMode == JoinMode.InputReplay && !KeepInputLog)
                throw new ArgumentException("InputReplay joins need KeepInputLog");
            JoinChannel.Validate();
        }

        private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    }

    public enum JoinMode : byte
    {
        /// <summary>The joiner receives the host's world state at the join tick plus the inputs since.</summary>
        Snapshot = 0,
        /// <summary>The joiner starts from the initial world and replays every input since tick 0.</summary>
        InputReplay = 1,
    }
}
