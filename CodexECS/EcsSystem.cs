
namespace CodexECS
{
    //TODO: add check that Includes and Excludes doesn't intersects
    public abstract class EcsSystem
    {
        protected static int Id<T>() => ComponentMeta<T>.Id;

        public virtual void Init(EcsWorld world) { }
        public virtual void Tick(EcsWorld world) { }

        // Jobs. A system that declares nothing is exclusive: the pipeline finishes every job and plays back
        // every command buffer before it ticks, and it may touch anything.
        // A system that declares what it reads and writes ticks while jobs on other data are still running, and
        // may leave its own jobs running. In exchange it must
        //  - schedule with Dependency and store the handle of what it scheduled back into Dependency;
        //  - call CompleteDependency before touching the declared components on the owning thread;
        //  - record structural changes in a command buffer instead of making them.
        internal ComponentAccess Access;

        /// <summary>Starts or extends this system's declaration. Call from the constructor.</summary>
        protected ComponentAccess Declare() => Access ??= new ComponentAccess();

        /// <summary>
        /// Before Tick: the unfinished jobs of earlier systems that conflict with this system's declaration.
        /// After Tick: the jobs this system left running, which later systems that conflict will wait for.
        /// </summary>
        protected internal JobHandle Dependency { get; set; }

        /// <summary>Finishes the jobs this system must wait for and clears Dependency.</summary>
        protected void CompleteDependency()
        {
            Dependency.Complete();
            Dependency = default;
        }
    }
}
