namespace Foundry.Content;

[Flags]
public enum Parts { Pose=1, Model=2, StaticBox=4, Spawn=8 }
public sealed record PresetSpec(string Name, Parts Components, Parts Group);
