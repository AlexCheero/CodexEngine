"""Packs a camera-relative effect capture (e.g. the gameplay recorder's muzzle-flash mode) into one atlas.

python flipbook_pack.py <capture dir> <out.png> [--tile 256] [--keep 0.05]
The capture holds v<variant>_f<frame>.png crops and flash.json with, per frame, the anchor (muzzle) in screen pixels and
the crop's top-left corner. Every tile is re-centred on its anchor. Frames whose mean brightness stays under --keep times
the brightest frame's (the empty trigger frame, faint leftovers) are dropped. Writes <out.png> (variants in rows, frames in columns) and <out>.json:
{"fps","variants","frames","tile","scale","height","fov"} - draw tile (v,f) centred on the port's anchor, scaled by
port height / capture height, with linear gain `scale`.
"""
import argparse, json, pathlib
from PIL import Image

if __name__ == '__main__':
    p = argparse.ArgumentParser(); p.add_argument('capture'); p.add_argument('out')
    p.add_argument('--tile', type=int, default=256); p.add_argument('--keep', type=float, default=.05)
    a = p.parse_args()
    folder = pathlib.Path(a.capture); index = json.loads((folder / 'flash.json').read_text())
    variants, frames, half = index['variants'], index['frames'], a.tile // 2
    tiles = {}
    for v in range(variants):
        for f in range(frames):
            mx, my, cx, cy = index['muzzle'][v * frames + f]
            with Image.open(folder / f'v{v}_f{f}.png') as im:
                src = im.convert('RGB')
            ax, ay = round(mx - cx), round(my - cy)
            tile = Image.new('RGB', (a.tile, a.tile))
            tile.paste(src.crop((ax - half, ay - half, ax + half, ay + half)), (0, 0))
            tiles[v, f] = tile
    def energy(f): return sum(sum(i * n for i, n in enumerate(tiles[v, f].convert('L').histogram())) for v in range(variants))
    level = [energy(f) for f in range(frames)]
    kept = [f for f in range(frames) if level[f] >= a.keep * max(level)]
    atlas = Image.new('RGB', (a.tile * len(kept), a.tile * variants))
    for (v, f), t in tiles.items():
        if f in kept: atlas.paste(t, (kept.index(f) * a.tile, v * a.tile))
    out = pathlib.Path(a.out); atlas.save(out, optimize=True)
    meta = {k: index[k] for k in ('fps', 'variants', 'scale', 'height', 'fov')} | {'frames': len(kept), 'tile': a.tile}
    out.with_suffix('.json').write_text(json.dumps(meta))
    print(out, atlas.size, meta)
