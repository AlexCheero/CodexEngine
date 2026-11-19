# Griefborn actor physics

Branch `codex/griefborn-jolt-actors` extends `2584da9`, the shared baked-indirect-lighting and static-collision tip. The runtime uses the same Jolt system for the level, moving actors, ragdolls, characters and gameplay queries. The game owns the mapping from a native collider to its ECS entity, hit region and lifetime.

| Engine API | Reason for the change |
| --- | --- |
| `CollisionWorld.Raycast`, `RaycastNonAlloc`, `SphereCast`, `OverlapSphereNonAlloc`, `OverlapCapsuleNonAlloc`, `CreateNode` | A simulation worker or a concurrently loaded scene must be able to address its owning world explicitly. Result buffers are reusable. |
| `RaycastExcept` | A moving capsule can test for support against the level and other actors while excluding its own native body. Each query worker owns its exclusion filter. |
| `CreateDynamicBody` with `DynamicBodyMotion`, `RigidBody.LinearVelocity`, `SetGravityFactor` | An upright walking capsule needs translation-only degrees of freedom, authored gravity, friction and solver velocities. Flyers and authored climbs change the gravity factor on the existing body. |
| `CompoundPart`, `CollisionNode.AddCompound`, `CreateDynamicCompound` | A settled pose retains its original oriented limb volumes in one static Jolt body. A budget-retired joint assembly can continue falling and colliding as one rigid compound. Both use the same local shape builder. |
| `CharacterMotor` | Exposes Jolt CharacterVirtual with capsule geometry, slope limits, stair walking, floor adhesion, contacts and a queryable inner body for the player. Character disposal deregisters the inner body before Jolt frees it. |
| `CollisionWorld.Step(dt, subdivisions)` | Large time advances run native substeps and publish managed transforms once at the end. Ordinary game ticks use one step. Dynamic-body removal maintains a dense pose list. |

Create the world with the collision-layer pairs required by the game. Query masks and solver layer pairs have different jobs. For example, a baked corpse can receive a ray on its own layer without blocking the player. Complete worker queries before unregistering or disposing bodies. Dispose characters, game-owned bodies and constraints before their world.

The Griefborn checkout pins the corresponding revision as its Engine submodule. From a built game run `Player/Griefborn.exe --verify` for character traversal, per-body hit regions, growing overlap buffers, native mob steering on the original Arena and continuous ragdoll-to-static collision. The focused `--verify-encounters` runs the encounter and route checks.
