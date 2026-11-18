using Foundry.Content;
using Godot;
using System.Text.Json;

namespace Foundry.Authoring;

[Tool, GlobalClass]
public partial class EntityAuthoring : Node3D
{
	[Export] public string SourceName { get; set; } = "";
	[Export] public bool AllocateTransformTogether { get; set; } = true;

	public GameplaySpec DescribeGameplay()
	{
		var defaults = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		var group = new List<string>();
		foreach (var child in GetChildren().OfType<EntityState>())
		{
			if (!defaults.TryAdd(child.TypeKey, child.BakeDefaults()))
				throw new InvalidDataException($"{Name}: duplicate {child.TypeKey} component.");
			if (child.AllocateTogether) group.Add(child.TypeKey);
		}
		return new(defaults, group.ToArray());
	}
}
