using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace CodexECS
{
    /// <summary>When a pipeline host ticks a system. A system may belong to several categories.</summary>
    [Flags]
    public enum ESystemCategory
    {
        Init            = 1 << 0,
        Update          = 1 << 1,
        LateUpdate      = 1 << 2,
        FixedUpdate     = 1 << 3,
        LateFixedUpdate = 1 << 4,
        OnEnable        = 1 << 5,
        OnDisable       = 1 << 6,
        /// <summary>Created with the pipeline and never ticked: it subscribes to world reactions itself.</summary>
        Reactive        = 1 << 7,
    }

    /// <summary>The categories an editor offers a system under.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class SystemAttribute : Attribute
    {
        public readonly ESystemCategory Categories;
        public SystemAttribute(ESystemCategory categories) => Categories = categories;
    }

    /// <summary>
    /// Every concrete EcsSystem in the loaded assemblies, by full type name: the names a pipeline asset stores.
    /// </summary>
    public static class SystemCatalog
    {
        public static readonly ESystemCategory[] Categories = (ESystemCategory[])Enum.GetValues(typeof(ESystemCategory));

        private static Dictionary<string, Type> _types;

        public static IReadOnlyDictionary<string, Type> Types => _types ??= Scan();

        /// <summary>Looks again after more assemblies have loaded.</summary>
        public static void Refresh() => _types = Scan();

        private static Dictionary<string, Type> Scan()
        {
            var found = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                foreach (var type in types)
                    if (!type.IsAbstract && !type.IsGenericTypeDefinition && typeof(EcsSystem).IsAssignableFrom(type))
                        found[type.FullName] = type;
            }
            return found;
        }

        /// <summary>The categories the system's attribute names, or none.</summary>
        public static ESystemCategory CategoriesOf(Type system) =>
            system.GetCustomAttribute<SystemAttribute>()?.Categories ?? 0;

        /// <summary>
        /// Writes the catalog for an editor that cannot load the game's assembly: name, assembly and categories
        /// of each system that carries a System attribute, sorted by name. Systems without the attribute still
        /// resolve by name; they are just not offered.
        /// </summary>
        public static string ExportJson()
        {
            var entries = Types.Values.Where(t => CategoriesOf(t) != 0).OrderBy(t => t.FullName, StringComparer.Ordinal).Select(type => new
            {
                Name = type.FullName,
                Assembly = type.Assembly.GetName().Name,
                Categories = Categories.Where(c => (CategoriesOf(type) & c) != 0).Select(c => c.ToString()).ToArray()
            });
            return JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
