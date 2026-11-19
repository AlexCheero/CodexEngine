# Upright Jolt cylinders for crowds

The engine branch is `codex/griefborn-crowd-contacts`. It extends `codex/griefborn-jolt-actors`. The configured engine remote uses `main` and its tip is an ancestor. The FPS project pins `2584da9`, which is also an ancestor and supplies the shared sun shadow, SSAO and baked-lighting features.

`CollisionShape.Cylinder(height, radius)` creates a vertically aligned Jolt cylinder. Height is the full distance between the end faces. The convex radius is limited to 5 cm or a quarter of the radius or half-height, whichever is smaller. The existing body factory owns the temporary native shape while constructing the body.

Griefborn uses the cylinder for grounded living mobs. Its flat faces support an actor that lands on another actor. Vertical sides keep horizontal contact impulses horizontal when a heavier mob drives into a smaller one. Rotation remains locked through the existing motion settings. Jolt mass and velocity correction limited by the mob's traction determine how it pushes through a crowd. The game owns steering, ram priority, rendering and gameplay rules. Flying mobs and the player use capsules.

The game regression `VerifyMobileContacts` covers a stack, a rammer pushing lighter bodies on the ground, wall contacts and partial support at a ledge. Run the Griefborn player's `--verify-encounters` to exercise the change.
