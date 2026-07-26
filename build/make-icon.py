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

Run after changing `icon-master.png`:

    python3 build/make-icon.py
"""

import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
MASTER = os.path.join(HERE, "icon-master.png")

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

SIZES = (16, 32, 48, 64, 128, 256, 512)


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


def main() -> None:
    master = load_master()
    plain = cloak_only(master)

    # electron-builder reads build/icon.png for the .icns/.ico it derives, and
    # build/icons/*.png for Linux.
    master.resize((1024, 1024), Image.LANCZOS).save(os.path.join(HERE, "icon.png"))

    icons_dir = os.path.join(HERE, "icons")
    os.makedirs(icons_dir, exist_ok=True)
    for n in SIZES:
        src = master if n >= WORDMARK_MIN else plain
        src.resize((n, n), Image.LANCZOS).save(os.path.join(icons_dir, f"{n}x{n}.png"))

    # The sidebar mark renders at 26px, far below WORDMARK_MIN, so it uses the
    # cloak-only art. Emitted at 4x for high-DPI displays and committed as a
    # renderer asset, so the UI and the packaged icon come from one source.
    assets = os.path.join(os.path.dirname(HERE), "src", "renderer", "assets")
    os.makedirs(assets, exist_ok=True)
    plain.resize((128, 128), Image.LANCZOS).save(os.path.join(assets, "cloak-mark.png"))

    below = [n for n in SIZES if n < WORDMARK_MIN]
    print(f"wrote icon.png (1024) and icons/ at {', '.join(map(str, SIZES))}")
    print(f"wordmark dropped below {WORDMARK_MIN}px: {', '.join(map(str, below))}")
    print("wrote src/renderer/assets/cloak-mark.png (128, cloak only)")


if __name__ == "__main__":
    main()
