#!/usr/bin/env python3
"""
Generate the application icon set, and the sidebar mark, from `icon-master.png`.

`icon-master.png` is the CloakBrowser hooded-cloak logo with a HUB wordmark, at
2048x2048. This script only *derives* from it — it deliberately draws nothing,
because an earlier version of this file hand-drew an approximation of the logo
and the approximation drifted from the real brand art. Resizing one master is
the only way the sizes cannot disagree with each other.

Two variants come out of it:

  * with the wordmark, for 64px and up, where three glyphs are still legible;
  * without it, for 48px and below, where "HUB" collapses into a grey smear and
    the cloak silhouette alone reads better.

The wordmark-free variant is not simply the master with the text erased: removing
the text leaves the cloak sitting high with dead space underneath, so the crop is
re-centred on the cloak and tightened. That is what `cloak_only()` does.

It emits every icon the .NET/Avalonia app needs, from the one master, so no two
sizes can disagree about what the logo is:

  * `dotnet/src/CloakHub.App/Assets/app-icon.png` — the window and taskbar icon
    Avalonia loads at runtime;
  * `dotnet/src/CloakHub.App/Assets/cloak-mark.png` — the sidebar mark;
  * `dotnet/src/CloakHub.App/app.ico` — the Win32 icon compiled into the .exe, which
    is what Explorer and the taskbar read before the app is even running;
  * `dotnet/src/CloakHub.Core/Assets/app-icon.png` — the base the per-instance badge
    renderer draws numbers onto.

Run after changing `icon-master.png`:

    python3 build/make-icon.py
"""

import os
import struct

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
MASTER = os.path.join(HERE, "icon-master.png")

#: .NET app project, which owns the runtime window icon and the Win32 .exe icon.
APP_ASSETS = os.path.join(ROOT, "dotnet", "src", "CloakHub.App", "Assets")
APP_DIR = os.path.join(ROOT, "dotnet", "src", "CloakHub.App")

#: Core owns the badge renderer's base icon.
CORE_ASSETS = os.path.join(ROOT, "dotnet", "src", "CloakHub.Core", "Assets")

#: Sizes embedded in the multi-resolution .ico.
#:
#: Windows picks per context — 16px in the title bar, 32 in the taskbar, 256 in
#: the large-icon file view — and scales the nearest match when a size is absent.
#: Shipping only 256 is the common mistake: Explorer downscales it to 16 with no
#: regard for legibility and the result is mud.
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)

#: Flat backdrop of the master art, used to paint out the wordmark.
BG = (25, 26, 28)

# Both boxes below are stored as fractions of the master's size, not pixels, so
# the master can be re-exported at any resolution without silently shifting the
# crops. They were measured off the art rather than guessed.

#: Wordmark bounding box, padded outward so the glyph antialiasing goes with it.
TEXT_BOX = (0.303, 0.762, 0.698, 0.928)

#: Cloak bounding box.
CLOAK_BOX = (0.228, 0.098, 0.771, 0.742)

#: Fraction of the frame the cloak should fill once the wordmark is gone.
CLOAK_FILL = 0.80

#: Below this pixel size the wordmark stops being legible and is dropped.
WORDMARK_MIN = 64


def load_master() -> Image.Image:
    if not os.path.exists(MASTER):
        raise SystemExit(f"missing {MASTER} — the brand art is the input, not generated here")
    return Image.open(MASTER).convert("RGBA")


def cloak_only(master: Image.Image) -> Image.Image:
    """
    The master with the wordmark painted out and the crop re-centred on the cloak.

    Square-cropped rather than letterboxed so the small sizes are not silently
    scaled non-uniformly, and clamped to the canvas so a crop can never read
    past the edge and introduce a transparent band.
    """
    img = master.copy()
    w, h = img.size

    tx0, ty0, tx1, ty1 = (round(f * (w if i % 2 == 0 else h)) for i, f in enumerate(TEXT_BOX))
    img.paste(Image.new("RGBA", (tx1 - tx0, ty1 - ty0), (*BG, 255)), (tx0, ty0))

    x0, y0, x1, y1 = (f * (w if i % 2 == 0 else h) for i, f in enumerate(CLOAK_BOX))
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    side = max(x1 - x0, y1 - y0) / CLOAK_FILL

    side = min(side, w, h)
    left = round(min(max(cx - side / 2, 0), w - side))
    top = round(min(max(cy - side / 2, 0), h - side))

    return img.crop((left, top, left + round(side), top + round(side)))


def write_ico(path: str, master: Image.Image, plain: Image.Image) -> None:
    """
    Write a multi-resolution .ico by hand.

    Pillow's own `save(format="ICO")` is not used because it refuses sizes above
    256 and, more importantly, would force one source image for every entry. The
    point here is the opposite: the small entries must come from the wordmark-free
    art, so each size is encoded separately and assembled into one file.

    Every entry is stored as a PNG rather than a BMP. Vista and later accept PNG
    inside an .ico, it halves the file, and it avoids the legacy format's
    upside-down rows and separate AND-mask, both of which are easy to get subtly
    wrong and produce a fringed icon.
    """
    import io

    entries = []
    for n in ICO_SIZES:
        src = master if n >= WORDMARK_MIN else plain
        buf = io.BytesIO()
        src.resize((n, n), Image.LANCZOS).save(buf, format="PNG", optimize=True)
        entries.append((n, buf.getvalue()))

    # ICONDIR: reserved, type 1 (icon), image count.
    header = struct.pack("<HHH", 0, 1, len(entries))

    # Each ICONDIRENTRY is 16 bytes and they all precede the image data.
    offset = len(header) + 16 * len(entries)
    directory = b""
    for n, data in entries:
        # 256 is stored as 0: the field is one byte, so 256 does not fit. This is
        # the documented convention, not a trick.
        dim = 0 if n >= 256 else n
        directory += struct.pack(
            "<BBBBHHII",
            dim, dim,  # width, height
            0,         # palette size; 0 for truecolour
            0,         # reserved
            1,         # colour planes
            32,        # bits per pixel
            len(data),
            offset,
        )
        offset += len(data)

    with open(path, "wb") as fh:
        fh.write(header)
        fh.write(directory)
        for _, data in entries:
            fh.write(data)


def main() -> None:
    master = load_master()
    plain = cloak_only(master)

    os.makedirs(APP_ASSETS, exist_ok=True)
    os.makedirs(CORE_ASSETS, exist_ok=True)

    # 512 rather than 1024: this is loaded into memory at every launch to draw the
    # window icon, and no platform asks for more than 512 for that. Quadrupling the
    # pixels would cost startup time for a size nothing requests.
    master.resize((512, 512), Image.LANCZOS).save(
        os.path.join(APP_ASSETS, "app-icon.png"))

    # The badge renderer composites instance numbers onto this. Same art as the
    # window icon deliberately: a badged icon that did not match the app's own would
    # be actively confusing on a taskbar.
    master.resize((512, 512), Image.LANCZOS).save(
        os.path.join(CORE_ASSETS, "app-icon.png"))

    plain.resize((128, 128), Image.LANCZOS).save(
        os.path.join(APP_ASSETS, "cloak-mark.png"))

    write_ico(os.path.join(APP_DIR, "app.ico"), master, plain)

    below = [n for n in ICO_SIZES if n < WORDMARK_MIN]

    print("wrote dotnet CloakHub.App/Assets/{app-icon,cloak-mark}.png")
    print("wrote dotnet CloakHub.Core/Assets/app-icon.png (badge base)")
    print(f"wrote dotnet CloakHub.App/app.ico at {', '.join(map(str, ICO_SIZES))}")
    print(f"wordmark dropped below {WORDMARK_MIN}px: {', '.join(map(str, below))}")


if __name__ == "__main__":
    main()
