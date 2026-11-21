using System;
using System.Collections.Generic;
using System.Threading;

namespace CodexECS
{
    public partial class EcsWorld
    {
        private JobDependencies _dependencies;
        private readonly List<EntityCommandBuffer> _commandBuffers = new List<EntityCommandBuffer>(4);
        private int _commandBuffersInUse;
        private object[] _services = Array.Empty<object>();

        /// <summary>The scheduler for this world's jobs, or null when everything runs on the owning thread.</summary>
        public JobScheduler Jobs { get; set; }

        /// <summary>Which unfinished jobs touch which component types.</summary>
        public JobDependencies Dependencies => _dependencies ??= new JobDependencies(this);

        /// <summary>
        /// A command buffer that the next Sync plays back, after the buffers handed out before it.
        /// Take it on the owning thread; jobs may then record into it.
        /// </summary>
        public EntityCommandBuffer CreateCommandBuffer()
        {
            if (_commandBuffersInUse == _commandBuffers.Count)
                _commandBuffers.Add(new EntityCommandBuffer(this, Jobs?.ThreadCount ?? 1));
            return _commandBuffers[_commandBuffersInUse++];
        }

        /// <summary>True when a job is unfinished or a command buffer waits for playback.</summary>
        public bool NeedsSync => _commandBuffersInUse != 0 || Jobs != null && Jobs.Busy;

        /// <summary>
        /// A sync point: finishes every job, then applies the command buffers in the order they were taken.
        /// Structural changes made directly on the owning thread are safe after it.
        /// </summary>
        public void Sync()
        {
            if (Jobs != null)
                Dependencies.CompleteAll();
            if (_commandBuffersInUse == 0)
                return;
            int count = _commandBuffersInUse;
            _commandBuffersInUse = 0;
            Lock();
            try
            {
                for (int i = 0; i < count; i++)
                    _commandBuffers[i].Playback();
            }
            finally
            {
                Unlock();
            }
        }

        private static int _serviceTypes;

        private static class ServiceSlot<T>
        {
            internal static readonly int Index = Interlocked.Increment(ref _serviceTypes) - 1;
        }

        /// <summary>
        /// Shared objects that systems built from a pipeline asset look up by type: a physics world, level
        /// geometry, catalogs. Systems take only the world in their constructor.
        /// </summary>
        public void SetService<T>(T service) where T : class
        {
            int index = ServiceSlot<T>.Index;
            if (index >= _services.Length)
                Array.Resize(ref _services, Math.Max(index + 1, Math.Max(8, _services.Length * 2)));
            _services[index] = service;
        }

        public T Service<T>() where T : class =>
            TryService<T>(out var service) ? service
                : throw new EcsException($"the world has no {typeof(T).Name} service");

        public bool TryService<T>(out T service) where T : class
        {
            int index = ServiceSlot<T>.Index;
            service = index < _services.Length ? (T)_services[index] : null;
            return service != null;
        }
    }
}
