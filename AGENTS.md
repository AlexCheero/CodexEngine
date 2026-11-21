# Components

- Pass components by `ref` or `in`. Read them from the world with `ref` or `ref readonly`, as in `ref var pose = ref world.Get<Pose>(id);`.
- Copy a component only when it is a reference type, or when the copy is the point, such as a snapshot handed to a worker.
- Keep components unmanaged where it makes sense: numbers, `Entity` handles, catalog indices and resource handles instead of object references.
