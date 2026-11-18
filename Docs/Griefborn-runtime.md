# Runtime changes for Griefborn

Branch `codex/griefborn-static-collision` starts at `aef3dab`, the engine revision that includes the pooled FAudio one-shot bank. The branch changes two runtime APIs.

## Static mesh collision

`CollisionNode.AddMesh` accepts node-local indexed triangle spans, constructs a Jolt mesh and returns the owning collider. The existing JSON loader calls the same method. `CollisionWorld.OptimizeBroadPhase` completes a bulk insert. Griefborn already has BRM geometry in memory for rendering. Passing its transformed triangles directly to Jolt avoids serializing a second copy of every level to physics JSON. Static geometry is grouped into spatial cells. Rotating tutorial gate leaves have their own Jolt nodes.

## Synchronous parallel work

`RangeWorkers` partitions an index range into disjoint slices, with the caller processing the first slice. The owner supplies a cached delegate and a minimum amount of work per lane. Persistent threads and wait handles are created once. Returning from `Run` joins all slices, including on exceptions, so the owner can safely reduce results and change ECS structure. Create workers on the owning simulation thread after constructing its collision world. Dispose them before disposing that world. The worker threads inherit the construction-time execution context.

Griefborn uses this for enemy movement planning and large pellet queries. The simulation alone creates and destroys entities and applies health changes in order.

## Verification

Build `CodexEngine.sln` with the .NET 10 SDK and the Windows SDK. In the Griefborn checkout, `Build.ps1` publishes the Windows player and runs `Griefborn.exe --verify`. Its checks exercise uneven range partitioning, reuse after an exception, concurrent collision queries, and character grounding in the three playable levels. The combat and encounter suites exercise both systems through the same entry points used by the player.
