"""Builds src/RdpManager/app.ico from design/app-icon.svg.

The renderer only understands the Vonal icon vocabulary: <rect> (optionally rounded,
filled and/or stroked) and <path> made of absolute M/H/V commands with square caps.
Anything else is rejected rather than drawn wrong. Shapes are drawn aliased at a
supersampled size and box-filtered down, so every output pixel gets its exact coverage.

Usage: python design/build-icon.py [source.svg] [target.ico]    (needs Pillow and numpy)
"""
import io
import math
import re
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SIZES = [256, 128, 64, 48, 32, 24, 16]
CANVAS = 1024          # viewBox the source is drawn on
GRID = 32              # every edge, radius and half stroke in the family is a multiple of this
MIN_SUPERSAMPLED = 4096
INHERITED = ("fill", "stroke", "stroke-width", "stroke-linecap")


def color(value):
    if not re.fullmatch(r"#[0-9a-fA-F]{6}", value):
        raise ValueError(f"unsupported colour {value!r}")
    return tuple(int(value[i:i + 2], 16) for i in (1, 3, 5)) + (255,)


def paint_ops(svg_path):
    """Returns (rgba, outer, hole) in paint order; outer and hole are (x0, y0, x1, y1, radius)."""
    ops = []

    def walk(node, style):
        style = {**style, **{k: node.get(k) for k in INHERITED if node.get(k) is not None}}
        tag = node.tag.rsplit("}", 1)[-1]
        if tag in ("svg", "g"):
            for child in node:
                walk(child, style)
            return
        fill, stroke = style.get("fill", "#000000"), style.get("stroke", "none")
        half = float(style.get("stroke-width", "1")) / 2
        if tag == "rect":
            x, y, w, h = (float(node.get(k, "0")) for k in ("x", "y", "width", "height"))
            r = float(node.get("rx", "0"))
            if node.get("ry") not in (None, node.get("rx")):
                raise ValueError("elliptical corners are not part of the vocabulary")
            if fill != "none":
                ops.append((color(fill), (x, y, x + w, y + h, r), None))
            if stroke != "none":
                ops.append((color(stroke),
                            (x - half, y - half, x + w + half, y + h + half, r + half),
                            (x + half, y + half, x + w - half, y + h - half, max(r - half, 0))))
        elif tag == "path":
            if stroke == "none" or style.get("stroke-linecap") != "square":
                raise ValueError("paths must be stroked lines with square caps")
            tokens = re.findall(r"[A-Za-z]|-?\d+(?:\.\d+)?", node.get("d", ""))
            px = py = None
            while tokens:
                cmd = tokens.pop(0)
                if cmd == "M":
                    px, py = float(tokens.pop(0)), float(tokens.pop(0))
                    continue
                if cmd not in ("H", "V") or px is None:
                    raise ValueError(f"unsupported path command {cmd!r}")
                nx, ny = (float(tokens.pop(0)), py) if cmd == "H" else (px, float(tokens.pop(0)))
                ops.append((color(stroke),
                            (min(px, nx) - half, min(py, ny) - half,
                             max(px, nx) + half, max(py, ny) + half, 0), None))
                px, py = nx, ny
        elif tag not in ("title", "desc"):
            raise ValueError(f"<{tag}> is not part of the vocabulary")

    walk(ET.parse(svg_path).getroot(), {})
    return ops


def render(ops, size):
    # Pick a supersampled size on which every GRID-unit edge falls on a whole pixel.
    factor = math.ceil(MIN_SUPERSAMPLED / size)
    while (size * factor * GRID) % CANVAS:
        factor += 1
    big = size * factor
    scale = big / CANVAS

    def box(shape):
        x0, y0, x1, y1, r = (round(v * scale) for v in shape)
        return [x0, y0, x1 - 1, y1 - 1], r

    # Transparent pixels stay (0, 0, 0, 0) and painted ones are opaque, so the canvas is
    # already premultiplied and a plain mean gives correct edge colours.
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    for rgba, outer, hole in ops:
        mask = Image.new("L", (big, big), 0)
        draw = ImageDraw.Draw(mask)
        xy, r = box(outer)
        draw.rounded_rectangle(xy, radius=r, fill=255)
        if hole:
            xy, r = box(hole)
            draw.rounded_rectangle(xy, radius=r, fill=0)
        canvas.paste(rgba, mask=mask)

    pixels = np.asarray(canvas).reshape(size, factor, size, factor, 4)
    mean = pixels.sum(axis=(1, 3), dtype=np.uint32) / (factor * factor)
    alpha = mean[..., 3:]
    rgb = np.divide(mean[..., :3] * 255, alpha, out=np.zeros_like(mean[..., :3]), where=alpha > 0)
    return Image.fromarray(np.concatenate([rgb, alpha], axis=2).round().clip(0, 255).astype(np.uint8))


def write_ico(images, path):
    blobs = []
    for image in images:
        buffer = io.BytesIO()
        image.save(buffer, "PNG", optimize=True)
        blobs.append(buffer.getvalue())
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries = b""
    for image, blob in zip(images, blobs):
        side = image.width % 256  # 0 means 256 in the ICO directory
        entries += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    path.write_bytes(header + entries + b"".join(blobs))


def main():
    source = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "design" / "app-icon.svg"
    target = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "src" / "RdpManager" / "app.ico"
    ops = paint_ops(source)
    write_ico([render(ops, size) for size in SIZES], target)
    print(f"{target}: {', '.join(map(str, SIZES))} px")


if __name__ == "__main__":
    main()
