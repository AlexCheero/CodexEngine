# CodexEngine

CodexEngine is the reusable C# runtime and Godot authoring layer used by FPS Deathmatch. A game checks out this repository as its `Engine` submodule. Open `CodexEngine.sln` to work on runtime code independently, or open the consuming game's Godot C# solution to debug engine and game code together.

| Directory | Responsibility |
| --- | --- |
| `CodexECS` | Entity storage, filters, systems and the pipeline from the Unity project's ECS |
| `CodexNetworking` | Transport, serialization, snapshots and ECS synchronization |
| `Runtime` | Pure entity presets, math, Jolt simulation primitives and the shared scene and mesh formats |
| `Rendering` | FNA static and skinned mesh rendering, GPU assets, shader sources and FXC compilation |
| `Godot` | Editor-authored presets and components, content baking and integration with the consuming Godot project |
| `ThirdParty` | FNA framework sources and native libraries, plus BRUTAL notices and attribution |
| `Tools` | Installation and configuration of the pinned Godot .NET and .NET SDK toolchain |

The runtime consumes the Ahwoo BRUTAL Jolt package pinned in `Runtime/CodexEngine.Runtime.csproj`. The ECS and networking directories contain the C# versions ported from the Unity game. Their upstream repositories are [Codex ECS](https://github.com/AlexCheero/ECS) and [CodexNetworking](https://github.com/AlexCheero/CodexNetworking).

On Windows x64, build the engine with `dotnet build CodexEngine.sln -c Release` using .NET 10 and a Windows SDK with x64 FXC. The game’s `Edit.cmd` sets up the bundled tools, exposes the engine add-on directly to Godot and opens the editor. The asset files and gameplay components in a game determine what gets rendered or simulated.
