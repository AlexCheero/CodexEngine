"""Helpers for reading Unity project assets outside the editor."""
import pathlib, re, zlib
import numpy as np
from PIL import Image

def srgb_to_linear(c): return c / 12.92 if c <= .04045 else ((c + .055) / 1.055) ** 2.4
def linear_to_srgb(c): return c * 12.92 if c <= .0031308 else 1.055 * c ** (1 / 2.4) - .055

def guid_map(project):
    """guid -> asset path (relative to the project) for every .meta under Assets."""
    project = pathlib.Path(project); out = {}
    for meta in (project / "Assets").rglob("*.meta"):
        m = re.search(r"^guid: ([0-9a-f]{32})\s*$", meta.read_text("utf-8", "ignore"), re.M)
        if m: out[m[1]] = meta.relative_to(project).with_suffix("")
    return out

def open_rgba(path):
    """Opens an image as RGBA. Pillow drops an unlabelled fourth TIFF sample (ExtraSamples=0), which Unity imports as alpha."""
    path = pathlib.Path(path); im = Image.open(path)
    tags = getattr(im, "tag_v2", None)
    if tags and tags.get(277) == 4 and im.mode == "RGB" and tags.get(259) == 8 and tags.get(284, 1) == 1:
        w, h = tags[256], tags[257]
        raw = path.read_bytes(); data = bytearray()
        for offset, count in zip(tags[273], tags[279]): data += zlib.decompress(raw[offset:offset + count])
        a = np.frombuffer(bytes(data), np.uint8)[:w * h * 4].reshape(h, w, 4).copy()
        if tags.get(317, 1) == 2: a = np.cumsum(a, axis=1, dtype=np.uint8)
        return Image.fromarray(a, "RGBA")
    return im.convert("RGBA")
