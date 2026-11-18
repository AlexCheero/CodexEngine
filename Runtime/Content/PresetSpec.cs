using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foundry.Content;

[Flags]
public enum Parts { Pose=1, Model=2, StaticBox=4, Spawn=8 }
public sealed record GameplaySpec(
    [property: JsonPropertyName("components")] Dictionary<string, JsonElement> Components,
    [property: JsonPropertyName("group")] string[] Group);
public sealed record PresetSpec(string Name, Parts Components, Parts Group, GameplaySpec Gameplay);
