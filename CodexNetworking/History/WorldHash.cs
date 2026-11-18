using System;
using System.Runtime.InteropServices;
using CodexECS;

namespace CodexNetworking.History
{
    /// <summary>
    /// Hash of a captured simulation state: slot bookkeeping plus every registered component in the
    /// registry's stable order, payload bytes only for slots that own the component. Two peers whose
    /// hashes differ at the same tick have desynchronized.
    /// </summary>
    public static class WorldHash
    {
        public static ulong Compute(WorldState state, SimulationRegistry registry)
        {
            if (!state.IsCaptured)
                throw new InvalidOperationException("state was never captured");
            StateHasher hasher = StateHasher.Create();
            hasher.Add(state.PageCount);
            hasher.Add(state.EntitiesCount);
            hasher.Add(MemoryMarshal.AsBytes(state.AliveWords));
            hasher.Add(MemoryMarshal.AsBytes(state.Versions));

            int count = registry.Count;
            for (int i = 0; i < count; i++)
            {
                int componentId = registry.ComponentIdAt(i);
                ReadOnlySpan<ulong> words = state.MembershipWords(componentId);
                hasher.Add(MemoryMarshal.AsBytes(words));
                SimulationRegistry.PayloadHasher hashPayload = registry.HasherAt(i);
                if (hashPayload == null)
                    continue;
                for (int page = 0; page < words.Length; page++)
                {
                    ulong bits = words[page];
                    if (bits != 0)
                        hashPayload(state, componentId, page, bits, ref hasher);
                }
            }
            return hasher.Hash;
        }
    }
}
