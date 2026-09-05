"""Builds the LocalAi mark: assets/localai.ico, docs/images/localai-mark.png, the lockup.

Run by hand, not by the build and not by CI:

    python scripts/build-mark.py

It exists because two of the seven icon frames are set pixel by pixel and cannot be recovered
from the master geometry. Reduce the master to 16 and the seal is a smudge; reduce it to 24 and
the ring that makes a seal a seal goes soft. Those two frames live below as literal ASCII, which
is the only form in which the next person can see what was decided - inside a .ico they exist
only as bytes, and the first rebuild that re-reduces all seven loses them without a word.

The master geometry is assets/localai-mark.svg. If the two disagree, the SVG is the record and
this file is the defect.
"""

import pathlib
import struct
import sys
from io import BytesIO

from PIL import Image, ImageDraw, ImageFont

INK = "#2D7FD3"
SUPERSAMPLE = 4

# The mark on a 256 canvas, origin top-left, y down. The L is variant 9 unchanged; the seal is
# three pointy-top hexagons on centre (187,68) with circumradii 52 / 38 / 24, its right flat
# flush with the L's right edge at x=232 and its apex level with the L's top at y=16.
L_BLOCK = [(24, 16), (104, 16), (104, 160), (232, 160), (232, 240), (24, 240)]
SEAL_OUTER = [(187, 16), (232, 42), (232, 94), (187, 120), (142, 94), (142, 42)]
SEAL_RING = [(187, 30), (220, 49), (220, 87), (187, 106), (154, 87), (154, 49)]
SEAL_CORE = [(187, 44), (208, 56), (208, 80), (187, 92), (166, 80), (166, 56)]

# The 48 frame is drawn from the master shifted half a pixel left. Without it the L's three
# vertical edges land on 4.5, 19.5 and 43.5, and every vertical edge in the mark becomes a soft
# two-column blend.
HALF_PIXEL_AT_48 = -256 / 48 / 2

# Hand-set. Read them as pictures - that is the point of keeping them this way.
GRID_16 = """
................
.#####....##....
.#####...####...
.#####..######..
.#####..######..
.#####..######..
.#####...####...
.#####....##....
.#####..........
.#####..........
.#############..
.#############..
.#############..
.#############..
.#############..
................
"""

GRID_24 = """
........................
........................
..########......###.....
..########.....#...#....
..########....#.###.#...
..########...#.#####.#..
..########...#.#####.#..
..########...#.#####.#..
..########...#.#####.#..
..########....#.###.#...
..########.....#...#....
..########......###.....
..########..............
..########..............
..########..............
..####################..
..####################..
..####################..
..####################..
..####################..
..####################..
..####################..
..####################..
........................
"""


def from_grid(art):
    """One frame exactly as drawn: '#' is ink, anything else is transparent."""
    rows = [row for row in art.strip("\n").split("\n")]
    size = len(rows)
    if any(len(row) != size for row in rows):
        sys.exit("a hand-set grid is not square")
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ink = tuple(int(INK[i:i + 2], 16) for i in (1, 3, 5)) + (255,)
    pixels = image.load()
    for y, row in enumerate(rows):
        for x, cell in enumerate(row):
            if cell == "#":
                pixels[x, y] = ink
    return image


def master(dx=0.0):
    """The whole mark at 4x, optionally shifted, for reduction to one frame."""
    image = Image.new("RGBA", (256 * SUPERSAMPLE, 256 * SUPERSAMPLE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def polygon(points, fill):
        draw.polygon([((x + dx) * SUPERSAMPLE, y * SUPERSAMPLE) for x, y in points], fill=fill)

    polygon(L_BLOCK, INK)
    polygon(SEAL_OUTER, INK)
    # Knocked out rather than white: tile-less, a white ring is invisible on a light taskbar,
    # and a hole is the same shape on every surface because it is the surface.
    polygon(SEAL_RING, (0, 0, 0, 0))
    polygon(SEAL_CORE, INK)
    return image


def reduced(size, dx=0.0):
    return master(dx).resize((size, size), Image.LANCZOS)


def frames():
    """The seven frames, each built the way its size needs."""
    return {
        16: from_grid(GRID_16),
        24: from_grid(GRID_24),
        32: reduced(32),
        48: reduced(48, HALF_PIXEL_AT_48),
        64: reduced(64),
        128: reduced(128),
        256: reduced(256),
    }


def bmp_payload(image):
    """A 32-bit BGRA DIB with the doubled height and the AND mask an ICO frame carries."""
    width, height = image.size
    pixels = image.load()
    rows = []
    for y in range(height - 1, -1, -1):          # DIBs are stored bottom-up
        row = bytearray()
        for x in range(width):
            r, g, b, a = pixels[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    colour = b"".join(rows)

    # Ignored for a 32-bit icon, but its bytes are still part of the frame, and a missing mask
    # is how a hand-written ICO renders as garbage in Explorer.
    mask_stride = ((width + 31) // 32) * 4
    mask = b"\x00" * (mask_stride * height)

    header = struct.pack(
        "<IiiHHIIiiII",
        40, width, height * 2, 1, 32, 0, len(colour) + len(mask), 0, 0, 0, 0)
    return header + colour + mask


def write_ico(path, built):
    """The container by hand: DIB up to 64, PNG for 128 and 256.

    Not Pillow's own writer. `IcoImagePlugin._save` reuses a supplied frame only when its size
    matches exactly and otherwise thumbnails the base image without a word, which would throw
    away both hand-set frames; and its `bitmap_format` is all-or-nothing, so the DIB/PNG split
    this file already ships could not be reproduced through it.
    """
    entries, payloads = [], []
    for size in sorted(built):
        image = built[size]
        if size >= 128:
            buffer = BytesIO()
            image.save(buffer, format="PNG")
            payload = buffer.getvalue()
        else:
            payload = bmp_payload(image)
        entries.append((size, len(payload)))
        payloads.append(payload)

    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = len(header) + 16 * len(entries)
    directory = b""
    for size, length in entries:
        directory += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,   # 0 means 256
            0 if size >= 256 else size,
            0,                            # palette entries: none, this is true colour
            0,                            # reserved
            1,                            # colour planes
            32,                           # bits per pixel
            length,
            offset)
        offset += length
    path.write_bytes(header + directory + b"".join(payloads))


# The lockup: the mark, then the product's name. One artwork for both languages - the name is
# Latin in the Russian documentation too, so only the alt text beside it ever changes.
LOCKUP = (1008, 288)
LOCKUP_MARK_ORIGIN = (48, 32)      # where the mark's ink box starts
LOCKUP_FONT = r"C:\Windows\Fonts\seguisb.ttf"
LOCKUP_FONT_SIZE = 200
# 80 units from the mark's ink to the text's ink: twice the moat inside the mark, because a
# lockup's join has to be visibly wider than the space within the mark or the seal reads as part
# of the word spacing. The baseline centres the text's ink on the mark's ink centre.
LOCKUP_BASELINE = (319, 217)


def lockup(scale=2):
    image = Image.new("RGBA", (LOCKUP[0] * scale, LOCKUP[1] * scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    dx, dy = LOCKUP_MARK_ORIGIN[0] - 24, LOCKUP_MARK_ORIGIN[1] - 16

    def polygon(points, fill):
        draw.polygon([((x + dx) * scale, (y + dy) * scale) for x, y in points], fill=fill)

    polygon(L_BLOCK, INK)
    polygon(SEAL_OUTER, INK)
    polygon(SEAL_RING, (0, 0, 0, 0))
    polygon(SEAL_CORE, INK)

    font = ImageFont.truetype(LOCKUP_FONT, LOCKUP_FONT_SIZE * scale)
    draw.text(
        (LOCKUP_BASELINE[0] * scale, LOCKUP_BASELINE[1] * scale),
        "LocalAi",
        font=font,
        fill=INK,
        anchor="ls")
    return image.resize(LOCKUP, Image.LANCZOS)


def main():
    root = pathlib.Path(__file__).resolve().parent.parent
    built = frames()

    write_ico(root / "assets" / "localai.ico", built)
    built[128].save(root / "docs" / "images" / "localai-mark.png", format="PNG")
    lockup().save(root / "assets" / "localai-lockup.png", format="PNG")

    print("frames: " + ", ".join(str(size) for size in sorted(built)))
    for size in (16, 24):
        ink = built[size].getchannel("A").point(
            lambda a: 255 if a > 127 else 0).histogram()[255]
        print("hand-set %d: %d ink pixels" % (size, ink))
    print("ico: %d bytes" % (root / "assets" / "localai.ico").stat().st_size)
    print("png: %d bytes" % (root / "docs" / "images" / "localai-mark.png").stat().st_size)
    print("lockup: %d bytes" % (root / "assets" / "localai-lockup.png").stat().st_size)


if __name__ == "__main__":
    main()
