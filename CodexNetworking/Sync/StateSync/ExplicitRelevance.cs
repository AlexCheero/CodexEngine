using System;
using CodexECS;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>
    /// Relevance without positions: the game says per client which ghosts it may see. Each client
    /// has a default (see everything, or see nothing) and a set of exceptions toggled by ghost id,
    /// so a card game hides the opponent's hand by default-visible plus hidden cards, and a
    /// spectator mode shows a selected few by default-hidden plus shown ghosts. Nothing is sent
    /// for a hidden ghost; showing it again sends it in full or as a delta like any re-entry.
    /// Allocation-free once the per-client bitsets have grown to the highest ghost id used.
    /// </summary>
    public sealed class ExplicitRelevance : IRelevance
    {
        private readonly bool[] _defaultVisible;
        private readonly ulong[][] _exceptions;

        public ExplicitRelevance(int maxClients, bool visibleByDefault = true)
        {
            _defaultVisible = new bool[maxClients];
            _exceptions = new ulong[maxClients][];
            for (int i = 0; i < maxClients; i++)
                _defaultVisible[i] = visibleByDefault;
        }

        /// <summary>Sets what the client sees unless an exception says otherwise, and clears its exceptions.</summary>
        public void SetDefault(int client, bool visible)
        {
            _defaultVisible[client] = visible;
            ulong[] bits = _exceptions[client];
            if (bits != null)
                Array.Clear(bits, 0, bits.Length);
        }

        /// <summary>Makes the ghost visible to the client whatever the default.</summary>
        public void Show(int client, uint netId) => SetException(client, netId, !_defaultVisible[client]);

        /// <summary>Hides the ghost from the client whatever the default.</summary>
        public void Hide(int client, uint netId) => SetException(client, netId, _defaultVisible[client]);

        /// <summary>Removes any exception for the ghost: the client's default applies again.</summary>
        public void Reset(int client, uint netId) => SetException(client, netId, false);

        public bool IsVisible(int client, uint netId)
        {
            ulong[] bits = _exceptions[client];
            bool exception = bits != null && (netId >> 6) < (uint)bits.Length && (bits[netId >> 6] & (1UL << (int)(netId & 63))) != 0;
            return _defaultVisible[client] != exception;
        }

        public bool IsRelevant(int client, WorldState state, int slot, uint netId) => IsVisible(client, netId);

        private void SetException(int client, uint netId, bool set)
        {
            ref ulong[] bits = ref _exceptions[client];
            int word = (int)(netId >> 6);
            if (bits == null || word >= bits.Length)
            {
                if (!set)
                    return;
                Array.Resize(ref bits, Math.Max(word + 1, bits == null ? 4 : bits.Length * 2));
            }
            if (set)
                bits[word] |= 1UL << (int)(netId & 63);
            else
                bits[word] &= ~(1UL << (int)(netId & 63));
        }
    }
}
