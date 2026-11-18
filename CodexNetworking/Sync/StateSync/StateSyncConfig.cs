using System;
using CodexNetworking.Transport;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>Tuning of a server-authoritative session. Server and clients share the tick rate; the rest is per side.</summary>
    public sealed class StateSyncConfig
    {
        /// <summary>Server: ticks between snapshots to each client (1 = every tick).</summary>
        public int SnapshotInterval = 1;

        /// <summary>Server: snapshots kept for delta baselines and lag compensation. Power of two.</summary>
        public int ServerHistoryCapacity = 64;

        /// <summary>Client: predicted ticks kept for reconciliation. Power of two, more than the expected lead.</summary>
        public int ClientHistoryCapacity = 64;

        /// <summary>Client: recent input ticks repeated in every input packet.</summary>
        public int InputRedundancy = 8;

        /// <summary>Client: ticks the local simulation should run ahead of the server so inputs arrive just in time.</summary>
        public int TargetInputLead = 2;

        /// <summary>Client: ticks to predict ahead right after the first snapshot.</summary>
        public int InitialLead = 3;

        /// <summary>Input buffers (both sides). Power of two.</summary>
        public int InputBufferCapacity = 256;

        /// <summary>Server: fraction of the packet budget used for a client while the transport reports it congested.</summary>
        public double CongestedBudgetFraction = 0.5;

        /// <summary>Client: how many snapshot ticks behind the newest applied one the presentation renders.</summary>
        public int InterpolationDelayTicks = 2;

        /// <summary>Server: reliable event packets sent per tick per client at most.</summary>
        public int MaxEventPacketsPerTick = 4;

        /// <summary>
        /// Client: an event for a ghost that is not here yet creates an empty ghost for the next
        /// snapshot to fill (default), so an event that beats its spawn is not lost. Turn it off
        /// together with <see cref="SnapshotWriter.DespawnIrrelevant"/>: there an unknown ghost is
        /// usually one that was just hidden, the snapshot never comes, and the event is dropped instead.
        /// </summary>
        public bool EventsCreateGhosts = true;

        /// <summary>Reliable channel carrying event components (both sides must agree on the window).</summary>
        public Transport.Reliable.ReliableChannelConfig EventChannel = new Transport.Reliable.ReliableChannelConfig { Window = 64, MaxMessageSize = 256 };

        public void Validate()
        {
            if (SnapshotInterval < 1)
                throw new ArgumentOutOfRangeException(nameof(SnapshotInterval));
            if (!IsPowerOfTwo(ServerHistoryCapacity) || !IsPowerOfTwo(ClientHistoryCapacity) || !IsPowerOfTwo(InputBufferCapacity))
                throw new ArgumentOutOfRangeException("capacities must be powers of two");
            if (InputRedundancy < 1 || InputRedundancy > 64)
                throw new ArgumentOutOfRangeException(nameof(InputRedundancy));
            if (TargetInputLead < 0 || InitialLead < 0 || InitialLead >= ClientHistoryCapacity)
                throw new ArgumentOutOfRangeException(nameof(TargetInputLead));
            if (CongestedBudgetFraction <= 0 || CongestedBudgetFraction > 1)
                throw new ArgumentOutOfRangeException(nameof(CongestedBudgetFraction));
            if (InterpolationDelayTicks < 0 || MaxEventPacketsPerTick < 1)
                throw new ArgumentOutOfRangeException(nameof(InterpolationDelayTicks));
            EventChannel.Validate();
        }

        private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    }
}
