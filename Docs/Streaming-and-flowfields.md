# Level streaming, prepared shapes and chunked flowfields

Added for GTA-Like's open city; nothing in them is specific to it.

## WorldStreamer (`Runtime/Streaming/WorldStreamer.cs`, namespace `CodexEngine.Streaming`)

A world is cut into square cells of one or more detail levels (`StreamingLevel`: cell size, load radius, unload radius,
optional inner radius). A game supplies an `ICellSource<T>`: `Exists` (from a manifest), `Load` (on the streamer's worker
threads: file reads, decoding, mesh building, `PreparedShape`s; no device and no simulation), `Activate` (on the calling
thread: GPU uploads, adding bodies; returns a cost), `Deactivate` and `Discard` (a cell that loaded after it stopped being
wanted). Call `Update(focus, velocity)` or `Update(ReadOnlySpan<StreamingFocus>)` once a frame or tick: it queues missing
cells nearest first (counting from the focus and from where its velocity takes it in `Lookahead` seconds; finer levels
first at equal distance), activates finished cells within `ActivationBudget`, deactivates cells past their unload radius
and cancels loads no longer wanted. `Active` lists the active cells, `IsReady(position)` tells whether the finest cell under a
point is in (hold a body still until it is), `Preload` blocks for a loading screen, `Stats` counts what is loading.

Hierarchical levels of detail: with a coarse level whose cells are whole multiples (up to 8) of the next finer level's,
`ActiveChildren(coarseKey)` returns a bit per fine child that is active. Draw the coarse cell only for the clear bits and
the two levels never overlap, while the coarse content keeps covering a fine cell until it has loaded.

## PreparedShape (`Runtime/Physics/PreparedShape.cs`)

`PreparedShape.Mesh(positions, indices)` and `PreparedShape.Compound(parts)` build Jolt shapes on any thread (degenerate
triangles left out). `node.Add(prepared)` adds the static body on the simulation thread; dispose the prepared shape after.

## Chunked flowfields (`Runtime/Navigation/ChunkedFlowFields.cs`)

`ChunkedFlowGrid` is a walk grid of `FlowChunk`s (a cost per cell, 0 blocked, and any number of baked direction fields as
one byte per cell) that come and go with the streamed world; lookups are lock-free. `Sample(field, position)` blends the
four nearest cells' directions. `FlowIntegrator.Integrate` is a multi-seed eight-neighbour Dijkstra (diagonals never cut a
blocked corner, an optional per-cell penalty) and `Directions` turns integrated costs into steps downhill or uphill.

`DynamicFlowField.StartFlee(danger, radius)` snapshots the loaded chunks and works on a background task. Pass one
integrates the walking cost from the danger (dearer near it) chunk by chunk, nearest the danger first, each chunk seeded
from its finished neighbours' borders, and publishes "away" directions per chunk as soon as each is done. Pass two takes,
in each of eight directions around the danger, the reachable cell farthest from it as a refuge, integrates toward the
nearest refuge and replaces the chunks' directions. `Sample`, `Ready`, `ChunksDone`, `FirstChunkMilliseconds` and
`TotalMilliseconds` report progress; on GTA-Like's downtown (17 chunks of 80 x 80 cells) the first chunk is published in
about 10 ms and all of them in about 140 ms.

## RigidBody additions (`Runtime/Physics/Dynamics.cs`)

`AngularVelocity`, `PointVelocity(point)`, `AddAngularImpulse`, `Teleport(position, rotation, linear, angular)` and
`SetContinuous` (linear-cast motion quality), for raycast-wheel vehicles.
