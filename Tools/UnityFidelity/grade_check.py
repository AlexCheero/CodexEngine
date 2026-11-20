"""Checks the UrpPostProcessing grading port (without bloom) offline.

python grade_check.py <stacks.json> <graded-dir> <nopost-dir> name [name ...]
Applies the grade from <stacks.json> (name -> UrpVolumeStack) to <nopost-dir>/<name>.nopost.png and compares with
Unity's own graded <graded-dir>/<name>.png. Bloom needs HDR input, so bright scenes match best.
"""
import json, sys, pathlib
import numpy as np
from PIL import Image
stacks = {}
def dec(c): return np.where(c <= .04045, c / 12.92, ((c + .055) / 1.055) ** 2.4)
def enc(c): c = np.maximum(c, 0); return np.where(c <= .0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - .055)
def logc(x): return np.where(x > .011361, .244161 * np.log10(np.maximum(5.555556 * x + .047996, 1e-9)) + .386036, 5.301883 * x + .092819)
def unlogc(x): return np.where(x > 5.301883 * .011361 + .092819, (10 ** ((x - .386036) / .244161) - .047996) / 5.555556, (x - .092819) / 5.301883)
def curve(x): return (x * (.2 * x + .24 * .29) + .272 * .02) / (x * (.2 * x + .29) + .272 * .3) - .02 / .3
def neutral(x): w = 1 / curve(np.array(5.3)); return curve(x * w) * w
def luma(c): return c @ np.array([.2126729, .7151522, .0721750])
def prep(v, scale):
    s = np.array(v[:3]) * scale; w = v[3] * scale; return s - luma(s), w
def grade(c, s, apply_vignette=True):
    h, w_ = c.shape[:2]
    if apply_vignette and s["vignette"][0] > 0:
        y, x = np.mgrid[0:h, 0:w_]; uv = np.stack([(x + .5) / w_, (y + .5) / h], -1)
        d = np.abs(uv - np.array(s["vignette"][3:5])) * s["vignette"][0] * 3
        f = np.clip(1 - (d ** 2).sum(-1), 0, 1) ** (s["vignette"][1] * 5)
        c = c * f[..., None]
    c = c * 2 ** s["postExposure"]
    c = unlogc((logc(c) - .4135884) * (s["contrast"] / 100 + 1) + .4135884)
    c = np.maximum(c, 0)
    l = luma(c); lim = s["smhLimits"]
    def smooth(a, b, x): t = np.clip((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t)
    sh = 1 - smooth(lim[0], lim[1], l); hi = smooth(lim[2], lim[3], l)
    def tone(v): off = v[3] * (1 if v[3] < 0 else 4); return np.maximum(dec(np.array(v[:3])) + off, 0)
    c = c * (tone(s["shadows"]) * sh[..., None] + tone(s["midtones"]) * (1 - sh - hi)[..., None] + tone(s["highlights"]) * hi[..., None])
    ls, lw = prep(s["lift"], .2); lift = ls + lw
    gs, gw = prep(s["gain"], .8); gain = gs + gw + 1
    ms, mw = prep(s["gamma"], .8); inv = 1 / np.maximum(ms + mw + 1, 1e-3)
    c = c * gain + lift
    c = np.maximum(np.sign(c) * np.abs(c) ** inv, 0)
    l = luma(c)[..., None]; c = l + (s["saturation"] / 100 + 1) * (c - l)
    c = np.maximum(c, 0)
    return neutral(c) if s["tonemap"] == 1 else np.clip(c, 0, 1)
if __name__=="__main__":
    stacks.update(json.loads(pathlib.Path(sys.argv[1]).read_text()))
    graded_dir, raw_dir = pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3])
    for level in sys.argv[4:]:
        raw = np.asarray(Image.open(raw_dir / f"{level}.nopost.png").convert("RGB"), np.float64) / 255
        ref = np.asarray(Image.open(graded_dir / f"{level}.png").convert("RGB"), np.float64) / 255
        out = enc(grade(dec(raw), stacks[level]))
        print(f"{level}: ported {out.mean((0,1)).round(3)} unity {ref.mean((0,1)).round(3)} absdiff {np.abs(out-ref).mean():.3f} (ungraded {np.abs(raw-ref).mean():.3f})")
