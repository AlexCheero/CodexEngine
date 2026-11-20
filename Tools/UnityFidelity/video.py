"""Encodes recorded frames (frame_NNNNN.png) as Motion-JPEG AVI, alone or side by side, without ffmpeg.

python video.py <out.avi> [--fps 30] [--height 720] <dir> [label=<name>] [<dir> [label=<name>] ...]
Several folders are placed side by side (scaled to --height each), trimmed to the shortest recording.
"""
import argparse, io, pathlib, struct
from PIL import Image, ImageDraw, ImageFont

def chunk(tag, data):
    return tag + struct.pack('<I', len(data)) + data + (b'\0' if len(data) % 2 else b'')

def lst(kind, body):
    return b'LIST' + struct.pack('<I', len(body) + 4) + kind + body

def write_avi(path, frames, width, height, fps):
    """frames: iterable of JPEG bytes. Writes an AVI 1.0 file with an idx1 index (fine below 1 GB)."""
    movi, index, offset, count, biggest = io.BytesIO(), io.BytesIO(), 4, 0, 0
    for jpeg in frames:
        data = chunk(b'00dc', jpeg)
        index.write(b'00dc' + struct.pack('<III', 0x10, offset, len(jpeg)))
        movi.write(data); offset += len(data); count += 1; biggest = max(biggest, len(jpeg))
    avih = struct.pack('<IIIIIIIIII4I', round(1e6 / fps), biggest * fps, 0, 0x10, count, 0, 1, biggest, width, height, 0, 0, 0, 0)
    strh = struct.pack('<4s4sIHHIIIIIIIIhhhh', b'vids', b'MJPG', 0, 0, 0, 0, 1, fps, 0, count, biggest, 0xFFFFFFFF, 0, 0, 0, width, height)
    strf = struct.pack('<IiiHH4sIiiII', 40, width, height, 1, 24, b'MJPG', width * height * 3, 0, 0, 0, 0)
    hdrl = lst(b'hdrl', chunk(b'avih', avih) + lst(b'strl', chunk(b'strh', strh) + chunk(b'strf', strf)))
    body = b'AVI ' + hdrl + lst(b'movi', movi.getvalue()) + chunk(b'idx1', index.getvalue())
    pathlib.Path(path).write_bytes(b'RIFF' + struct.pack('<I', len(body)) + body)
    return count

def font(size):
    for name in ('arialbd.ttf', 'DejaVuSans-Bold.ttf'):
        try: return ImageFont.truetype(name, size)
        except OSError: pass
    return ImageFont.load_default()

def compose(folders, labels, height, quality):
    lists = [sorted(pathlib.Path(f).glob('frame_*.png')) for f in folders]
    count = min(len(l) for l in lists)
    face = font(max(14, height // 24))
    for i in range(count):
        tiles = []
        for files, label in zip(lists, labels):
            with Image.open(files[i]) as im:
                tile = im.convert('RGB')
            if tile.height != height: tile = tile.resize((round(tile.width * height / tile.height), height), Image.LANCZOS)
            if label:
                d = ImageDraw.Draw(tile); x, y = height // 40, height // 40
                d.text((x, y), label, font=face, fill=(255, 255, 255), stroke_width=max(1, height // 240), stroke_fill=(0, 0, 0))
            tiles.append(tile)
        frame = Image.new('RGB', (sum(t.width for t in tiles), height))
        x = 0
        for t in tiles: frame.paste(t, (x, 0)); x += t.width
        out = io.BytesIO(); frame.save(out, 'JPEG', quality=quality, subsampling=0); yield frame.size, out.getvalue()

if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('out'); p.add_argument('--fps', type=int, default=30); p.add_argument('--height', type=int, default=720)
    p.add_argument('--quality', type=int, default=90)
    p.add_argument('inputs', nargs='+', help='frame folders; a following "label=<text>" names the previous folder')
    a = p.parse_args()
    folders, labels = [], []
    for item in a.inputs:
        if item.startswith('label='): labels[-1] = item[6:]
        else: folders.append(item); labels.append('')
    stream = compose(folders, labels, a.height, a.quality)
    first = next(stream)
    size = first[0]
    def frames():
        yield first[1]
        for s, data in stream: yield data
    n = write_avi(a.out, frames(), size[0], size[1], a.fps)
    print(f'{a.out}: {n} frames, {size[0]}x{size[1]} at {a.fps} fps, {pathlib.Path(a.out).stat().st_size / 2**20:.1f} MiB')
