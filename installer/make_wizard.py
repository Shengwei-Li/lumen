"""
Builds the installer's wizard images (100% and 200%) from the app icon grid.

    python installer/make_wizard.py
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(here, "..", "Assets"))
from make_icon import grid  # noqa: E402

PAPER = (241, 241, 239)
INK = (17, 17, 17)
MUTED = (122, 122, 118)
RULE = (214, 214, 210)
FONTS = r"C:\Windows\Fonts"


def icon(cell):
    img = Image.new("RGBA", (16, 16))
    img.putdata([p for row in grid() for p in row])
    return img.resize((16 * cell, 16 * cell), Image.NEAREST)


def large(s):
    """Left panel of the welcome and finish pages: 164 x 314 at 100%."""
    img = Image.new("RGB", (164 * s, 314 * s), PAPER)
    d = ImageDraw.Draw(img)
    ic = icon(4 * s)
    img.paste(ic, (24 * s, 40 * s), ic)
    title = ImageFont.truetype(os.path.join(FONTS, "SegUIVar.ttf"), 26 * s)
    try:
        title.set_variation_by_name("Semibold")
    except Exception:
        pass
    d.text((22 * s, 140 * s), "Lumen", font=title, fill=INK, anchor="ls")
    mono = ImageFont.truetype(os.path.join(FONTS, "consola.ttf"), 9 * s)
    d.line([(24 * s, 270 * s), (140 * s, 270 * s)], fill=RULE, width=s)
    d.text((24 * s, 288 * s), "v1.0", font=mono, fill=MUTED, anchor="ls")
    return img


def small(s):
    """Top-right mark on the inner pages: 55 x 55 at 100%."""
    img = Image.new("RGB", (55 * s, 55 * s), (255, 255, 255))
    ic = icon(3 * s)
    off = (55 * s - ic.width) // 2
    img.paste(ic, (off, off), ic)
    return img


def main():
    for s in (1, 2):
        large(s).save(os.path.join(here, f"wizard-{100 * s}.bmp"))
        small(s).save(os.path.join(here, f"wizard-small-{100 * s}.bmp"))


if __name__ == "__main__":
    main()
