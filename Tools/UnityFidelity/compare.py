"""Side-by-side Unity/native frame comparison with image statistics.

python compare.py <unity-dir> <native-dir> <out-dir> [--suffix .nopost] [--hud-fraction 0.78] name [name ...]
Reads <dir>/<name><suffix>.png, writes <out>/<name><suffix>.jpg (Unity top, native bottom, 3x abs-diff right)
and prints luminance percentiles, saturation and a 3x3 native/Unity luminance grid.
"""
import argparse, pathlib
import numpy as np
from PIL import Image

def lum(a): return a @ np.array([.2126, .7152, .0722])
def stats(a):
    l = lum(a)
    return dict(mean=a.reshape(-1, 3).mean(0), l=l.mean(), p5=np.percentile(l, 5), p50=np.percentile(l, 50),
                p95=np.percentile(l, 95), contrast=l.std(), sat=(a.max(-1) - a.min(-1)).mean())

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("unity"); ap.add_argument("native"); ap.add_argument("out"); ap.add_argument("names", nargs="+")
    ap.add_argument("--suffix", default=""); ap.add_argument("--unity-suffix")
    ap.add_argument("--rows", type=float, default=1.0, help="fraction of rows to measure (exclude a HUD strip)")
    ap.add_argument("--full-rows", nargs="*", default=[], help="names measured on all rows")
    a = ap.parse_args()
    out = pathlib.Path(a.out); out.mkdir(parents=True, exist_ok=True)
    usuf = a.suffix if a.unity_suffix is None else a.unity_suffix
    for name in a.names:
        u, n = pathlib.Path(a.unity) / f"{name}{usuf}.png", pathlib.Path(a.native) / f"{name}{a.suffix}.png"
        if not (u.exists() and n.exists()): print(name, "missing"); continue
        ui = Image.open(u).convert("RGB"); ni = Image.open(n).convert("RGB").resize(ui.size)
        x, y = np.asarray(ui, np.float32) / 255, np.asarray(ni, np.float32) / 255
        rows = slice(None) if name in a.full_rows else slice(0, int(x.shape[0] * a.rows))
        sx, sy = stats(x[rows]), stats(y[rows])
        h, w = x[rows].shape[:2]
        grid = [[lum(y[rows][i*h//3:(i+1)*h//3, j*w//3:(j+1)*w//3]).mean() / max(1e-4, lum(x[rows][i*h//3:(i+1)*h//3, j*w//3:(j+1)*w//3]).mean())
                 for j in range(3)] for i in range(3)]
        print(f"{name}: absdiff {np.abs(x - y).mean():.3f}")
        for k in ("l", "p5", "p50", "p95", "contrast", "sat"):
            print(f"   {k:8s} unity {sx[k]:.3f} native {sy[k]:.3f}")
        print(f"   rgb      unity {np.round(sx['mean'],3)} native {np.round(sy['mean'],3)}")
        print("   grid native/unity luminance:", " | ".join(" ".join(f"{v:.2f}" for v in r) for r in grid))
        d = np.clip(np.abs(x - y) * 3, 0, 1); W, H = ui.size
        sheet = Image.new("RGB", (W * 2, H * 2), (20, 20, 20))
        sheet.paste(ui, (0, 0)); sheet.paste(ni, (0, H)); sheet.paste(Image.fromarray((d * 255).astype(np.uint8)), (W, H // 2))
        sheet.resize((W, H)).save(out / f"{name}{a.suffix}.jpg", quality=88)

if __name__ == "__main__": main()
