# Unity fidelity tools

Compare a native port against the Unity project it came from, and convert Unity look data for CodexEngine.Rendering.

## Reference renders

1. The native game writes a frame and its camera pose for each scene: `<name>.png` and `<name>.png.camera.json` with
   `{"eye":[x,y,z],"forward":[x,y,z],"up":[x,y,z],"fov":verticalDegrees,"width":w,"height":h}` in Unity world axes.
2. A config lists the scenes:
   `{"quality":3,"scenes":[{"name":"Level","scene":"Assets/Level.unity","additive":["Assets/Shared.unity"],"cameraPrefab":"Assets/Camera.prefab"}]}`.
   The scene's enabled camera is used. `cameraPrefab` is instantiated when the scene has no camera or no global volume
   (gameplay cameras spawned at runtime), or always with `"forceCameraPrefab":true`.
3. `capture_unity.ps1 -Source <project> -Config <json> -Poses <dir> -Out <dir> [-Experiment <name>]` renders the poses
   headless in batch mode (no window) with the URP renderer. It installs `UnityReference.cs.txt` into
   `Assets/Editor/CodexFidelity` for the run and deletes it afterwards. Run `check_unity_script.sh <project>` after editing
   the script; it compiles against the project's editor and URP assemblies in seconds.

Per scene it writes:

| File | Contents |
| --- | --- |
| `.png` | URP frame at the requested quality level |
| `.post.json` | blended volume stack (load as `UrpVolumeStack`) |
| `.ambient.json` | ambient probe on ±X, ±Y, ±Z (`AmbientCubeAt` in `UnityLighting.hlsl`) |
| `.lights.txt` | lights, shadow strength, ambient and reflection settings, terrains, reflection probes |
| `.surfaces.json` | per mesh/material-key URP Lit smoothness, metallic (gloss maps averaged), reflection flags |
| `.lightmaps.txt` | lightmap formats and mean decoded values |
| `.probe.txt` | renderer and material under a 3×4 screen grid |

Experiments isolate one contribution: `nopost`, `nofog`, `skyonly`, `noreflect`, `noparticles`, `particlecolor`
(all particles red), `particlenoinstancing`, `particlesblack`/`particleswhite` (particles alone over black/white,
which solves particle colour and coverage), `hdr` (float scene colour without post at -3 EV), `postidentity`,
`postbloomonly` and `postkeep-<exposure|contrast|saturation|lgg|smh|tonemap|vignette>` (an override volume keeps one
grading step). `-Experiment a,b,c` renders several experiments in one editor session, which saves the ~1.5 min startup per run.

Edit-mode renders don't run game scripts. Materials whose inputs scripts set at runtime can look different from the game.

## Comparison

`compare.py <unity-dir> <native-dir> <out-dir> names…` prints luminance percentiles, saturation and a 3×3 ratio grid
and writes side-by-side sheets. `grade_check.py` applies the `UrpPostProcessing` grade to a Unity `nopost` frame to
validate the grading port offline, and `component_check.py` compares each grading step with the `postkeep-*`
experiments. `graph_dump.py` prints a Shader Graph's nodes and edges for porting.

## Conversions

- `sky.py`: Skybox/Cubemap multiplier (`_Tint`, `[Gamma] _Exposure`, `unity_ColorSpaceDouble`) and the cross encoding for
  `CrossCubeSkybox`.
- `unity_assets.py`: guid map, sRGB helpers and an RGBA loader that keeps the unlabelled fourth TIFF sample Unity imports as alpha.

## Runtime counterparts

- `UrpPostProcessing` + `Shaders/UrpPost.fx`: bloom pyramid, chromatic aberration, vignette, HDR grading, Neutral/ACES tonemapping.
- `Shaders/UrpBrdf.hlsl`: URP Lit metallic BRDF (direct specular term, environment BRDF, reflection mip).
- `Shaders/UnityLighting.hlsl`: exact sRGB, URP light attenuation, Unity fog, ambient cube, cross-cube lookup, Shader Graph Voronoi.
- `CrossCubeSkybox` + `Shaders/CrossCubeSky.fx`: cross cubemap sky with Unity's rotation convention and an HDR gain
  (a pixel-shader constant; FNA's BasicEffect clamps its colour to 1).

Formulas follow the URP/core-RP sources of the project's Unity version (Library/PackageCache), e.g.
`ColorUtils.PrepareLiftGammaGain`. Under FNA's Vulkan path, implicit-gradient `tex2D` inside dynamic branches reads
zero, so post shaders sample with `tex2Dlod`.
