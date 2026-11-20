"""Compares each URP grading step of the port with Unity's own (postkeep-<component> experiments).

python component_check.py <stacks.json> <experiment-dir> name [name ...]
Needs <dir>/<name>.nopost.png and <dir>/<name>.postkeep-<component>.png for each component below.
"""
import json, pathlib, sys
import numpy as np
from PIL import Image
import grade_check as g

NEUTRAL = dict(tonemap=0, postExposure=0, contrast=0, saturation=0, lift=[1, 1, 1, 0], gamma=[1, 1, 1, 0], gain=[1, 1, 1, 0],
               shadows=[1, 1, 1, 0], midtones=[1, 1, 1, 0], highlights=[1, 1, 1, 0], vignette=[0, .45, 0, .5, .5])
KEEP = dict(exposure=["postExposure"], contrast=["contrast"], saturation=["saturation"], lgg=["lift", "gamma", "gain"],
            smh=["shadows", "midtones", "highlights"], tonemap=["tonemap"], vignette=["vignette"])

def load(path): return np.asarray(Image.open(path).convert("RGB"), np.float64) / 255

if __name__ == "__main__":
    stacks = json.loads(pathlib.Path(sys.argv[1]).read_text()); folder = pathlib.Path(sys.argv[2])
    for name in sys.argv[3:]:
        raw = load(folder / f"{name}.nopost.png")
        for component, fields in KEEP.items():
            path = folder / f"{name}.postkeep-{component}.png"
            if not path.exists(): continue
            stack = dict(stacks[name]); stack.update({k: v for k, v in NEUTRAL.items() if k not in fields})
            ported = g.enc(g.grade(g.dec(raw), stack)); unity = load(path)
            print(f"{name:9s} {component:10s} raw {raw.mean():.3f} unity {unity.mean():.3f} ported {ported.mean():.3f} diff {np.abs(ported-unity).mean():.4f}")
