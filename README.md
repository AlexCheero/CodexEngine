# CodexEngine

CodexEngine is the reusable C# runtime and Godot authoring layer used by Griefborn and CyberFPS. A game checks out this repository as its `Engine` submodule. Open `CodexEngine.sln` to work on runtime code independently, or open the consuming game's Godot C# solution to debug engine and game code together.

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

`DirectionalShadowMap` takes a direction toward the sun, a texture resolution, a coverage radius and a light-space depth on either side of the centre, in world units. Call `Position` before collecting casters. Use its `Intersects` test independently of camera visibility so off-camera objects can cast into the view. Call `Begin` and render from `ViewProjection` using the stock `StaticShadow` and `AnimatedShadow` techniques or your own depth-writing material. Switch to the scene target and call `BindReceiver(material, true)` before drawing lit surfaces. Custom effects can include `Rendering/Shaders/DirectionalShadows.hlsl` and multiply direct sunlight by `SunVisibility(worldPosition, worldNormal)`. The include reserves pixel sampler 1.

On Windows x64, build the engine with `dotnet build CodexEngine.sln -c Release` using .NET 10 and a Windows SDK with x64 FXC. The game’s `Edit.cmd` sets up the bundled tools, exposes the engine add-on directly to Godot and opens the editor. The asset files and gameplay components in a game determine what gets rendered or simulated.

`ScreenSpaceAmbientOcclusion` loads `AmbientOcclusion.fxb`. After drawing the sky, call `BeginOpaque(scene)` with a colour and depth target created with `PreserveContents`. Stock lit materials write ambient RGB and axial view depth to the second colour attachment. Custom materials can use `WriteSurface` in `Rendering/Shaders/AmbientSurface.hlsl` for the same layout. After opaque geometry, call `Apply(projection, radius, strength)` with the active perspective projection and a radius in world units. It restores the scene target for subsequent transparent draws.

`IndirectLightBake.Write` bakes visible skylight and one diffuse bounce from textured static scene geometry. Supply the same sun direction and colours as the player, and the horizontal bounds where moving objects need probes. The resulting `.brgi` contains a world-space colour mesh and six directional irradiance values per probe. Load it with `BakedSceneLight.Read`. Use `StaticMeshRenderer.CreateBatch(light)` to draw the refined surface with vertex irradiance in `COLOR1`, and keep the original scene mesh for shadow casters. For moving objects, create `IrradianceProbes`, call `Bind(material)` and sample `ProbeIrradiance(worldPosition, unitNormal)` from `Rendering/Shaders/IrradianceProbes.hlsl`. It reserves pixel sampler 2.


## Codex Editor

Run `Edit.cmd` on Windows to open the standalone Godot .NET authoring project. The launcher installs the pinned upstream Godot .NET distribution and builds the editor scripts. The Codex editor customization lives under `Godot/addons/codex_engine` and includes the Entity Presets dock, the component inspector, and the content baking tools.

Projects that embed the workbench enable `res://addons/codex_engine/plugin.cfg` in their Godot project and store their entity presets in `res://Entities`. A project can set `codex/editor/entity_palette` to its own C# script derived from `EntityWorkbenchPalette` to expose game-specific components.
