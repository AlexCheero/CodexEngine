using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace CodexECS
{
    /// <summary>One system of a pipeline asset: its full type name and whether it starts active.</summary>
    public sealed class PipelineEntry
    {
        public string Name { get; set; }
        public bool Active { get; set; } = true;
        public bool NonPausable { get; set; }
    }

    /// <summary>
    /// The systems of a pipeline, in tick order per category, as an editor stores them. The file is a JSON object
    /// with one array per category: {"FixedUpdate":[{"Name":"Game.MoveSystem","Active":true,"NonPausable":false}]}.
    /// </summary>
    public sealed class PipelineAsset
    {
        private readonly Dictionary<ESystemCategory, List<PipelineEntry>> _systems =
            new Dictionary<ESystemCategory, List<PipelineEntry>>();

        public PipelineAsset()
        {
            foreach (var category in SystemCatalog.Categories)
                _systems.Add(category, new List<PipelineEntry>());
        }

        public List<PipelineEntry> this[ESystemCategory category] => _systems[category];

        public PipelineAsset Add(ESystemCategory category, Type system, bool active = true, bool nonPausable = false)
        {
            _systems[category].Add(new PipelineEntry { Name = system.FullName, Active = active, NonPausable = nonPausable });
            return this;
        }

        public PipelineAsset Add<TSystem>(ESystemCategory category, bool active = true, bool nonPausable = false)
            where TSystem : EcsSystem => Add(category, typeof(TSystem), active, nonPausable);

        public static PipelineAsset Parse(string json)
        {
            var asset = new PipelineAsset();
            var read = JsonSerializer.Deserialize<Dictionary<string, List<PipelineEntry>>>(json)
                ?? throw new InvalidDataException("The pipeline asset is empty.");
            foreach (var pair in read)
            {
                if (!Enum.TryParse<ESystemCategory>(pair.Key, out var category) || !asset._systems.ContainsKey(category))
                    throw new InvalidDataException($"Unknown pipeline category '{pair.Key}'.");
                if (pair.Value != null)
                    asset._systems[category].AddRange(pair.Value);
            }
            return asset;
        }

        public static PipelineAsset Load(string path) => Parse(File.ReadAllText(path));

        public string ToJson()
        {
            var written = new Dictionary<string, List<PipelineEntry>>();
            foreach (var category in SystemCatalog.Categories)
                written.Add(category.ToString(), _systems[category]);
            return JsonSerializer.Serialize(written, new JsonSerializerOptions { WriteIndented = true });
        }

        public void Save(string path) => File.WriteAllText(path, ToJson());

        /// <summary>
        /// Creates every listed system through its constructor taking one EcsWorld and registers it in order.
        /// The names resolve against SystemCatalog.
        /// </summary>
        public EcsPipeline<ESystemCategory> Build(EcsWorld world)
        {
            var pipeline = new EcsPipeline<ESystemCategory>(world, SystemCatalog.Categories);
            try
            {
                foreach (var category in SystemCatalog.Categories)
                {
                    var entries = _systems[category];
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        if (string.IsNullOrWhiteSpace(entry.Name))
                            throw new InvalidOperationException($"Pipeline category {category} entry {i} has no system name.");
                        if (!SystemCatalog.Types.TryGetValue(entry.Name, out var type))
                            throw new InvalidOperationException(
                                $"Pipeline category {category} entry {i} names '{entry.Name}', which is not an EcsSystem in any loaded assembly.");
                        int index = i;
                        pipeline.Register(category, w => Create(type, w, category, index), entry.Active, entry.NonPausable);
                    }
                }
            }
            catch
            {
                pipeline.Dispose();
                throw;
            }
            return pipeline;
        }

        private static EcsSystem Create(Type type, EcsWorld world, ESystemCategory category, int index)
        {
            try
            {
                return (EcsSystem)Activator.CreateInstance(type, world);
            }
            catch (Exception exception)
            {
                var cause = exception is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException
                    : exception;
                throw new InvalidOperationException(
                    $"Could not construct '{type.FullName}' for category {category} at entry {index}. " +
                    "Systems need a public constructor taking one EcsWorld.", cause);
            }
        }
    }

    /// <summary>
    /// Drives a pipeline built from an asset the way a scene object would: init systems once, a tick per category,
    /// and the enable and disable categories around pausing.
    /// </summary>
    public sealed class PipelineHost : IDisposable
    {
        public EcsPipeline<ESystemCategory> Pipeline { get; }
        public EcsWorld World => Pipeline.World;
        public bool IsPaused => Pipeline.IsPaused;

        public PipelineHost(EcsWorld world, PipelineAsset asset)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));
            Pipeline = asset.Build(world);
        }

        /// <summary>Ticks the Init category, then calls Init on the systems of every category.</summary>
        public void Start()
        {
            Pipeline.Tick(ESystemCategory.Init, true);
            foreach (var category in SystemCatalog.Categories)
                Pipeline.Initialize(category);
        }

        public void Tick(ESystemCategory category, bool force = false) => Pipeline.Tick(category, force);

        public void Pause()
        {
            if (IsPaused)
                return;
            Pipeline.Pause();
            Pipeline.Tick(ESystemCategory.OnDisable, true);
        }

        public void Unpause()
        {
            if (!IsPaused)
                return;
            Pipeline.Unpause();
            Pipeline.Tick(ESystemCategory.OnEnable);
        }

        public void Switch<TSystem>(ESystemCategory category, bool on) where TSystem : EcsSystem =>
            Pipeline.GetRegistration<TSystem>(category).Active = on;

        public void Dispose() => Pipeline.Dispose();
    }
}
