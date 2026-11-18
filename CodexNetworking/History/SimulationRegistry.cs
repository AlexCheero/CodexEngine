using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using CodexECS;

namespace CodexNetworking.History
{
    /// <summary>
    /// The set of components that make up the simulation state, in an order that is identical on
    /// every peer. <see cref="ComponentMeta{T}.Id"/> depends on which type is touched first in a
    /// process, so it differs between machines; this registry sorts by full type name once it is
    /// <see cref="Seal"/>ed and exposes that stable order. Build it completely at startup, seal it,
    /// then hand it to <see cref="StateHistory"/> and <see cref="WorldHash"/>.
    /// Only unmanaged components can be registered; that is enforced by the generic constraint.
    /// </summary>
    public sealed class SimulationRegistry
    {
        /// <summary>Hashes the payload of the slots set in <paramref name="bits"/> on one page.</summary>
        public delegate void PayloadHasher(WorldState state, int componentId, int page, ulong bits, ref StateHasher hasher);

        private struct Entry
        {
            public int ComponentId;
            public string Name;
            public bool IsTag;
            public PayloadHasher HashPayload;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private int[] _componentIds = Array.Empty<int>();
        private bool _sealed;

        public bool IsSealed => _sealed;

        public int Count => _entries.Count;

        /// <summary>Component ids in stable (type name) order. Only valid after <see cref="Seal"/>.</summary>
        public ReadOnlySpan<int> ComponentIds
        {
            get
            {
                if (!_sealed)
                    throw new InvalidOperationException("SimulationRegistry must be sealed before use");
                return new ReadOnlySpan<int>(_componentIds, 0, _entries.Count);
            }
        }

        public void Register<T>() where T : unmanaged, IComponent
        {
            if (_sealed)
                throw new InvalidOperationException("SimulationRegistry is sealed; register every component before sealing");
            int id = ComponentMeta<T>.Id;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].ComponentId == id)
                    return;
            }
            _entries.Add(new Entry
            {
                ComponentId = id,
                Name = typeof(T).FullName,
                IsTag = ComponentMeta<T>.IsTag,
                HashPayload = ComponentMeta<T>.IsTag ? null : HashPayload<T>,
            });
        }

        /// <summary>
        /// Registers every struct in the assemblies carrying <see cref="SimulationStateAttribute"/>.
        /// Uses reflection and generic instantiation at runtime, which IL2CPP may strip or reject:
        /// use it in the editor and on desktop, and call <see cref="Register{T}"/> explicitly (or from
        /// generated code) for AOT players. A marked struct that holds references throws.
        /// </summary>
        public void RegisterFromAttributes(params Assembly[] assemblies)
        {
            MethodInfo register = typeof(SimulationRegistry).GetMethod(nameof(Register), BindingFlags.Public | BindingFlags.Instance);
            foreach (Assembly assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }
                foreach (Type type in types)
                {
                    if (type == null || !type.IsValueType || type.IsEnum || type.IsGenericTypeDefinition)
                        continue;
                    if (type.GetCustomAttribute<SimulationStateAttribute>() == null)
                        continue;
                    if (!typeof(IComponent).IsAssignableFrom(type))
                        throw new InvalidOperationException($"{type.FullName} is marked [SimulationState] but does not implement IComponent");
                    try
                    {
                        register.MakeGenericMethod(type).Invoke(this, null);
                    }
                    catch (ArgumentException e)
                    {
                        throw new InvalidOperationException($"{type.FullName} is marked [SimulationState] but holds references", e);
                    }
                    catch (TargetInvocationException e) when (e.InnerException != null)
                    {
                        throw e.InnerException;
                    }
                }
            }
        }

        /// <summary>Freezes the set and sorts it by type name. Sealing twice is harmless.</summary>
        public void Seal()
        {
            if (_sealed)
                return;
            _entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            _componentIds = new int[_entries.Count];
            for (int i = 0; i < _entries.Count; i++)
                _componentIds[i] = _entries[i].ComponentId;
            _sealed = true;
        }

        /// <summary>Position of a component in the stable order, or -1.</summary>
        public int IndexOf(int componentId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].ComponentId == componentId)
                    return i;
            }
            return -1;
        }

        public bool Contains(int componentId) => IndexOf(componentId) >= 0;

        public bool IsTag(int index) => _entries[index].IsTag;

        public string NameAt(int index) => _entries[index].Name;

        public int ComponentIdAt(int index) => _entries[index].ComponentId;

        /// <summary>Null for tags.</summary>
        public PayloadHasher HasherAt(int index) => _entries[index].HashPayload;

        private static void HashPayload<T>(WorldState state, int componentId, int page, ulong bits, ref StateHasher hasher) where T : unmanaged
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(state.Payload<T>(componentId, page));
            int size = bytes.Length >> 6; // 64 slots per page
            while (bits != 0)
            {
                int slot = TrailingZeros(bits);
                bits &= bits - 1;
                hasher.Add(bytes.Slice(slot * size, size));
            }
        }

        private static int TrailingZeros(ulong value)
        {
            int count = 0;
            while ((value & 1) == 0)
            {
                value >>= 1;
                count++;
            }
            return count;
        }
    }
}
