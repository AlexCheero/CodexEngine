using System;
using System.Runtime.CompilerServices;

namespace CodexECS
{
    public sealed partial class EcsFilter
    {
        /// <summary>
        /// A run of pages handed to one thread: foreach yields the matching entity ids in slot order.
        /// It takes no world lock and uses no pooled state, so batches of one filter can be read from several
        /// threads at once, provided nothing changes the world's structure until they finish.
        /// </summary>
        public readonly struct Batch
        {
            private readonly EcsFilter _filter;
            public readonly int FirstPage, EndPage;
            /// <summary>Position of the batch in the job, in slot order: a stable key for ordered results.</summary>
            public readonly int Index;

            internal Batch(EcsFilter filter, int firstPage, int endPage, int index)
            {
                _filter = filter;
                FirstPage = firstPage;
                EndPage = endPage;
                Index = index;
            }

            public EcsWorld World => _filter.World;

            public Enumerator GetEnumerator() => new Enumerator(_filter, FirstPage, EndPage);

            /// <summary>Number of matching entities in the batch.</summary>
            public int Count()
            {
                int count = 0;
                for (int page = FirstPage; page < EndPage; page++)
                    count += Bits.PopCount(_filter.PageWord(page));
                return count;
            }

            public struct Enumerator
            {
                private readonly EcsFilter _filter;
                private readonly int _end;
                private int _page;
                private ulong _word;

                internal Enumerator(EcsFilter filter, int firstPage, int endPage)
                {
                    _filter = filter;
                    _page = firstPage - 1;
                    _end = endPage;
                    _word = 0;
                    Current = 0;
                }

                public int Current { get; private set; }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool MoveNext()
                {
                    while (_word == 0)
                    {
                        if (++_page >= _end)
                            return false;
                        _word = _filter.PageWord(_page);
                    }
                    Current = (_page << Layout.PageShift) | Bits.TrailingZeros(_word);
                    _word &= _word - 1;
                    return true;
                }
            }
        }

        // The page's slots that own every include and no exclude.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong PageWord(int page)
        {
            var includes = Includes;
            ulong word = includes[0].Words[page];
            for (int i = 1; i < includes.Length && word != 0; i++)
                word &= includes[i].Words[page];
            if (word == 0)
                return 0;
            var excludes = Excludes;
            for (int i = 0; i < excludes.Length; i++)
                word &= ~excludes[i].Words[page];
            return word;
        }

        /// <summary>Pages that may hold matches; batches of a parallel job cover [0, PageCount).</summary>
        public int PageCount => World.PageCount;

        /// <summary>One batch covering the whole filter, for running a batch body on the calling thread.</summary>
        public Batch All => new Batch(this, 0, World.PageCount, 0);

        /// <summary>
        /// Schedules body over the filter in batches of pagesPerBatch pages (64 slots each) on the world's
        /// scheduler. The body may read and write components of the entities it is given. It must not create or
        /// delete entities or add or remove components: record those in an EntityCommandBuffer.
        /// </summary>
        public JobHandle ScheduleParallel(Action<Batch> body, int pagesPerBatch = 8, JobHandle dependsOn = default)
        {
            var jobs = World.Jobs ?? throw new InvalidOperationException("The world has no job scheduler.");
            return jobs.ScheduleEntities(this, body, pagesPerBatch, dependsOn);
        }

        /// <summary>Number of batches ScheduleParallel makes for this page size at the world's current size.</summary>
        public int BatchCount(int pagesPerBatch = 8) => (World.PageCount + pagesPerBatch - 1) / pagesPerBatch;
    }
}
