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
    """Opens an image as RGBA. Pillow drops an unlabelled fourth TIFF sample (ExtraSamples=0), which Unity imports as alpha;
    relabelling it as unassociated alpha lets Pillow's own decoders (any compression) keep it."""
    path = pathlib.Path(path); im = Image.open(path)
    tags = getattr(im, "tag_v2", None)
    if tags and tags.get(277) == 4 and im.mode == "RGB":
        im.tag_v2[338] = (2,); im._setup()
    return im.convert("RGBA")
