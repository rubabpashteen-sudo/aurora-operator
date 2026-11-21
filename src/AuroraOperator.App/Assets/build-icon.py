#!/usr/bin/env python3
"""Generate Aurora Operator's application icon.

Produces Assets/aurora.ico (256/64/32/16) and Assets/aurora.png (256x256):
a professional aurora "A" mark - two ribbon strokes forming an "A" with a
crossbar, over aurora bands on a dark charcoal background (#14161A).

Uses Pillow when available; otherwise falls back to a pure-stdlib PNG
writer (zlib + struct) that draws the same mark pixel by pixel, and a
minimal ICO writer around the 256px PNG.
"""

import math
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
SIZE = 256
BG = (20, 22, 26, 255)
TEAL = (94, 234, 212)
VIOLET = (167, 139, 250)
GREEN = (52, 211, 153)


def _lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def _dist_to_segment(px, py, x1, y1, x2, y2):
    dx, dy = x2 - x1, y2 - y1
    length_sq = dx * dx + dy * dy
    if length_sq == 0:
        return math.hypot(px - x1, py - y1)
    t = max(0.0, min(1.0, ((px - x1) * dx + (py - y1) * dy) / length_sq))
    return math.hypot(px - (x1 + t * dx), py - (y1 + t * dy))


def pixel(x, y, size):
    """Colour of one pixel of the mark, scaled to `size`."""
    s = size / 256.0
    # Dark background with a subtle vertical lift.
    base = _lerp((16, 18, 22), (28, 31, 40), y / max(size - 1, 1))
    r, g, b = base
    # Aurora bands: soft sine ribbons across the upper half.
    for i, (colour, phase, amp) in enumerate(
        [(TEAL, 0.0, 26), (VIOLET, 1.9, 20), (GREEN, 3.6, 16)]
    ):
        band_y = size * (0.20 + 0.07 * i) + amp * s * math.sin(
            x / size * 6.0 + phase
        )
        d = abs(y - band_y)
        glow = max(0.0, 1.0 - d / (14 * s))
        strength = glow * glow * 0.45
        r = int(r + (colour[0] - r) * strength)
        g = int(g + (colour[1] - g) * strength)
        b = int(b + (colour[2] - b) * strength)
    # The "A": left leg (teal), right leg (violet), crossbar (green).
    if _dist_to_segment(x, y, 78 * s, 202 * s, 128 * s, 54 * s) <= 11 * s:
        r, g, b = TEAL
    elif _dist_to_segment(x, y, 178 * s, 202 * s, 128 * s, 54 * s) <= 11 * s:
        r, g, b = VIOLET
    elif 140 * s <= y <= 153 * s and 102 * s <= x <= 154 * s:
        r, g, b = GREEN
    # Rounded corners: transparent outside the radius.
    radius = 44 * s
    cx = cy = size / 2.0
    corner = radius - min(x, size - 1 - x, y, size - 1 - y)
    alpha = 255
    if x < radius and y < radius:
        alpha = 255 if math.hypot(x - radius, y - radius) <= radius else 0
    elif x >= size - radius and y < radius:
        alpha = 255 if math.hypot(x - (size - radius), y - radius) <= radius else 0
    elif x < radius and y >= size - radius:
        alpha = 255 if math.hypot(x - radius, y - (size - radius)) <= radius else 0
    elif x >= size - radius and y >= size - radius:
        alpha = (
            255
            if math.hypot(x - (size - radius), y - (size - radius)) <= radius
            else 0
        )
    return (r, g, b, alpha)


def _png_bytes(size):
    """Encode the mark as PNG bytes using only the standard library."""
    raw = bytearray()
    for y in range(size):
        raw.append(0)  # filter: none
        for x in range(size):
            raw.extend(pixel(x, y, size))

    def chunk(tag, data):
        out = struct.pack(">I", len(data)) + tag + data
        out += struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        return out

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def _write_ico(path, png_by_size):
    """Minimal ICO writer embedding PNGs (Vista+ style, works for 256-16)."""
    count = len(png_by_size)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries = bytearray()
    data = bytearray()
    for size, blob in png_by_size:
        byte_size = 0 if size >= 256 else size
        entries += struct.pack(
            "<BBBBHHII", byte_size, byte_size, 0, 0, 1, 32, len(blob), offset
        )
        data += blob
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(header + bytes(entries) + bytes(data))


def build_with_pillow():
    from PIL import Image, ImageDraw

    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    # Rounded dark background.
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=44, fill=255)
    bg = Image.new("RGBA", (SIZE, SIZE), BG)
    img.paste(bg, (0, 0), mask)
    # Aurora bands on an overlay, then composited.
    overlay = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    od = ImageDraw.Draw(overlay)
    for i, (colour, phase, amp) in enumerate(
        [(TEAL, 0.0, 26), (VIOLET, 1.9, 20), (GREEN, 3.6, 16)]
    ):
        points = [
            (x, 0.20 * SIZE + 0.07 * i * SIZE + amp * math.sin(x / SIZE * 6.0 + phase))
            for x in range(0, SIZE + 1, 4)
        ]
        od.line(points, fill=colour + (110,), width=9)
    img = Image.alpha_composite(img, overlay)
    d = ImageDraw.Draw(img)
    # The "A" mark.
    d.line([(78, 202), (128, 54)], fill=TEAL + (255,), width=22)
    d.line([(178, 202), (128, 54)], fill=VIOLET + (255,), width=22)
    d.line([(102, 146), (154, 146)], fill=GREEN + (255,), width=13)
    d.line([(78, 202), (128, 54)], fill=TEAL + (255,), width=22, joint="curve")
    png_path = os.path.join(HERE, "aurora.png")
    ico_path = os.path.join(HERE, "aurora.ico")
    img.save(png_path, "PNG")
    img.save(ico_path, "ICO", sizes=[(256, 256), (64, 64), (32, 32), (16, 16)])
    return png_path, ico_path


def build_fallback():
    png_path = os.path.join(HERE, "aurora.png")
    ico_path = os.path.join(HERE, "aurora.ico")
    png_256 = _png_bytes(256)
    with open(png_path, "wb") as f:
        f.write(png_256)
    _write_ico(
        ico_path,
        [(256, png_256), (64, _png_bytes(64)), (32, _png_bytes(32)), (16, _png_bytes(16))],
    )
    return png_path, ico_path


def main():
    try:
        png_path, ico_path = build_with_pillow()
        print("Built with Pillow: %s, %s" % (png_path, ico_path))
    except ImportError:
        png_path, ico_path = build_fallback()
        print("Pillow not available; built with stdlib fallback: %s, %s" % (png_path, ico_path))
    for path in (png_path, ico_path):
        print("%s: %d bytes" % (path, os.path.getsize(path)))


if __name__ == "__main__":
    sys.exit(main())
