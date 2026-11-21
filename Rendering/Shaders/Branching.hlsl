#ifndef CODEX_BRANCHING
#define CODEX_BRANCHING
// A [branch] whose condition reads only uniforms compiles to a static branch on a bool register, and through
// FNA's effect translation on SDL_GPU that branch is never taken: the code inside silently does not run.
// PER_PIXEL ANDs in a term that varies per pixel, which keeps the branch a dynamic one. `varying` is any
// interpolated or sampled number. Use it for every [branch] that depends on settings alone.
#define PER_PIXEL(condition, varying) ((condition) && (varying) > -1e30)
#endif
