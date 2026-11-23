# /// script
# requires-python = ">=3.11"
# dependencies = ["pillow>=11"]
# ///
"""
Renders the Fluorite logo into every MSIX visual asset, the in-app logos, a multi-size AppIcon.ico and the
Logo.svg master, plus the website's link previews (PNG, with SVG masters).

The logo is a block of banded green fluorite drawn as Minecraft-style pixel art. The colour bands (emerald,
deep green, mint, sea green and thin near-clear layers) are tilted planes through the block, so the stripes
wrap continuously around its edges like the layers of a cut stone. The block is seen like an inventory item
(true isometric, top 100%, left 90%, right 75% brightness). Every size is drawn pixel for pixel at its own
size, never resampled: 16 texels per face from 96 px up, 8 from 40 px, 4 below that.

    uv run tools/generate-assets.py            # writes to src/Fluorite.App/Assets and website/src/assets
    uv run tools/generate-assets.py <dir>      # writes everything to <dir>, to preview a change

The link previews need Windows' Segoe UI Variable and Microsoft YaHei fonts.
"""
import hashlib
from html import escape
import io
import math
import os
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "src/Fluorite.App/Assets"
SITE_OUT = OUT if len(sys.argv) > 1 else ROOT / "website/src/assets"

# ── Bands ─────────────────────────────────────────────────────────────────────────────
# Band colours: (dark, mid, light) texel tones.
COLOURS = {
    "deep":  ("#1F7F62", "#258E6E", "#2E9E7B"),
    "green": ("#33A67F", "#3DB78C", "#55C79C"),
    "sea":   ("#2A9A8E", "#33AC9F", "#4ABDB0"),
    "mint":  ("#72D3AE", "#86DDBB", "#A0E8CB"),
    "clear": ("#BFEFD9", "#CFF5E3", "#DFFAED"),
}
# Band order and thickness, from the block's front top corner inwards.
BANDS = [("green", .30), ("deep", .12), ("mint", .18), ("sea", .28), ("clear", .07),
         ("green", .26), ("deep", .10), ("sea", .20)]
# Band plane: position = TILT . (a, b, depth below the top), plus a gentle wave.
TILT = (0.55, 0.35, 0.75)
WAVE = 0.035
SHADE = {"top": 1.0, "left": 0.90, "right": 0.75}

_EDGES = []
_pos = 0.0
for _name, _width in BANDS:
    _EDGES.append((_pos, _pos + _width, _name))
    _pos += _width
_TOTAL = _pos


def _rgb(c):
    return tuple(int(c[i:i + 2], 16) for i in (1, 3, 5))


def _rnd(*key):
    """Deterministic value in [0, 1), so every run draws the same texels."""
    return int.from_bytes(hashlib.md5(repr(key).encode()).digest()[:4], "little") / 2**32


def texel_colour(face, texels, i, j, a, b, h):
    """Colour of texel (i, j) of a face, at block coordinates a, b (0..1 along the left / right faces from the
    front edge) and h (0 bottom .. 1 top)."""
    key = (face, texels, i, j)
    s = TILT[0] * a + TILT[1] * b + TILT[2] * (1 - h)
    s += WAVE * math.sin(9 * a + 5 * b + 7 * h) + (_rnd("j", key) - .5) * 0.05
    s %= _TOTAL
    k = next(k for k, (lo, hi, _) in enumerate(_EDGES) if lo <= s < hi)
    name = _EDGES[k][2]
    if name == "clear" and texels <= 4:  # a thin clear layer would show as lone pale pixels when this small
        name = _EDGES[k - 1][2]
    r = _rnd("t", key)
    return _rgb(COLOURS[name][0 if r < 0.22 else 2 if r > 0.82 else 1])


# ── The block ─────────────────────────────────────────────────────────────────────────
def faces(w):
    """True-isometric block filling a w x w square (the hexagon is w tall).
    Per visible face: name, origin, u axis, v axis, (u, v) -> (a, b, h)."""
    e = w / 2
    hw = e * math.cos(math.pi / 6)
    cx = w / 2
    T, R, C, L = (cx, 0), (cx + hw, e / 2), (cx, e), (cx - hw, e / 2)
    return [
        ("top", L, (T[0] - L[0], T[1] - L[1]), (C[0] - L[0], C[1] - L[1]), lambda u, v: (1 - v, u, 1.0)),
        ("left", L, (C[0] - L[0], C[1] - L[1]), (0, e), lambda u, v: (1 - u, 0.0, 1 - v)),
        ("right", C, (R[0] - C[0], R[1] - C[1]), (0, e), lambda u, v: (0.0, u, 1 - v)),
    ]


def texels_for(px):
    return 16 if px >= 96 else 8 if px >= 40 else 4


def shaded(face, texels, i, j, to3d):
    a, b, h = to3d((i + .5) / texels, (j + .5) / texels)
    r, g, bl = texel_colour(face, texels, i, j, a, b, h)
    f = SHADE[face]
    return round(r * f), round(g * f), round(bl * f)


def render(px):
    """The logo at px x px. Texels are looked up per pixel (nearest), so the pixel art stays sharp at every
    size; from 64 px up the silhouette is supersampled for smooth outer edges."""
    ss = 4 if px >= 64 else 1
    w = px * ss
    n = texels_for(px)
    img = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    out = img.load()
    fs = faces(w)
    cache = {}
    for y in range(w):
        for x in range(w):
            p = (x + .5, y + .5)
            for name, o, a, b, to3d in fs:
                det = a[0] * b[1] - a[1] * b[0]
                u = ((p[0] - o[0]) * b[1] - (p[1] - o[1]) * b[0]) / det
                v = (a[0] * (p[1] - o[1]) - a[1] * (p[0] - o[0])) / det
                if 0 <= u < 1 and 0 <= v < 1:
                    i, j = int(u * n), int(v * n)
                    if (name, i, j) not in cache:
                        cache[name, i, j] = shaded(name, n, i, j, to3d)
                    out[x, y] = cache[name, i, j] + (255,)
                    break
    return img.reduce(ss) if ss > 1 else img


def logo_polygons(w):
    """The logo filling a w x w square as SVG polygons, one parallelogram per texel, 16 texels per face."""
    n = 16
    polys = []
    for name, o, a, b, to3d in faces(w):
        for j in range(n):
            for i in range(n):
                def at(u, v):
                    return f"{o[0] + a[0] * u + b[0] * v:.2f},{o[1] + a[1] * u + b[1] * v:.2f}"
                r, g, bl = shaded(name, n, i, j, to3d)
                pts = " ".join(at(u / n, v / n) for u, v in ((i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)))
                polys.append(f'<polygon points="{pts}" fill="#{r:02X}{g:02X}{bl:02X}"/>')
    return polys


def logo_svg():
    """Vector master of the logo."""
    w = 256
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {w}" width="{w}" height="{w}" '
            f'shape-rendering="crispEdges">\n<title>Fluorite Launcher</title>\n' + "\n".join(logo_polygons(w)) + "\n</svg>\n")


# ── Output ────────────────────────────────────────────────────────────────────────────
def canvas(w, h, fill):
    """Logo centred on a transparent w x h canvas, `fill` of the shorter side."""
    px = round(min(w, h) * fill)
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    img.alpha_composite(render(px), ((w - px) // 2, (h - px) // 2))
    return img


def save(img, name):
    img.save(OUT / name, optimize=True)


# ── Link previews ─────────────────────────────────────────────────────────────────────
# Text is set in the website's fonts: Segoe UI Variable, and Microsoft YaHei for Chinese.
FONTS = Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
INK, INK2, ACCENT = (26, 26, 26), (96, 96, 96), (31, 127, 98)
CARD_TEXT = {
    "en": ("A Minecraft: Java Edition launcher for Windows", ["Windows 10 & 11", "x64 & ARM64", "GPL-3.0"]),
    "zh-hans": ("适用于 Windows 的 Minecraft: Java 版启动器", ["Windows 10 & 11", "x64 和 ARM64", "GPL 3.0 开源"]),
}


CARD_SIZE, CARD_BG = (1200, 630), (243, 243, 243)
CARD_LOGO = (96, 155, 320)  # x, y, size


def card_lines(lang):
    """The link preview's text: x, y (top of the line), text, the text's language, size, weight, colour."""
    tagline, facts = CARD_TEXT[lang]
    en = lang == "en"
    return [
        (480, 196, "Fluorite Launcher", "en", 72, 600, INK),
        (480, 290, tagline, lang, 29 if en else 27, 400, INK2),
        (482, 340, "  ·  ".join(facts), lang, 24 if en else 23, 400, INK2),
        (482, 540, "fluorite.l5z12.dev", "en", 24, 600, ACCENT),
    ]


def opsz(size):
    """Segoe UI Variable's optical size: Display for headings, Text for the rest."""
    return 36 if size > 40 else 20 if size >= 20 else 10.5


def card_font(lang, size, weight):
    """Segoe UI Variable at a weight; Chinese lines use Microsoft YaHei, drawn from its regular file."""
    if lang != "en":
        return ImageFont.truetype(str(FONTS / "msyh.ttc"), size)
    font = ImageFont.truetype(str(FONTS / "SegUIVar.ttf"), size)
    font.set_variation_by_axes([weight, opsz(size)])
    return font


def hex_colour(rgb):
    return "#%02X%02X%02X" % rgb


def social_card(lang):
    """Link preview: the logo on the left, name, tagline and facts beside it, the site's address."""
    img = Image.new("RGBA", CARD_SIZE, CARD_BG + (255,))
    x, y, px = CARD_LOGO
    img.alpha_composite(render(px), (x, y))
    d = ImageDraw.Draw(img)
    for x, y, text, text_lang, size, weight, colour in card_lines(lang):
        d.text((x, y), text, font=card_font(text_lang, size, weight), fill=colour)
    return img


def social_card_svg(lang):
    """The same card as an SVG master. Its text stays text, in the website's fonts, so it can be edited."""
    w, h = CARD_SIZE
    x, y, px = CARD_LOGO
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" '
           f'lang="{"en" if lang == "en" else "zh-Hans"}">',
           "<title>Fluorite Launcher</title>",
           f'<rect width="{w}" height="{h}" fill="{hex_colour(CARD_BG)}"/>',
           f'<g transform="translate({x} {y})" shape-rendering="crispEdges">', *logo_polygons(px), "</g>"]
    for x, y, text, text_lang, size, weight, colour in card_lines(lang):
        if text_lang == "en":
            family = f"'Segoe UI Variable {'Display' if size > 40 else 'Text'}', 'Segoe UI', sans-serif"
            # Browsers see the named Display and Text families with fixed weights (400, 600, ...), so the card sticks to those.
            style = f"white-space:pre;font-variation-settings:'opsz' {opsz(size)}"
        else:
            family, weight, style = "'Microsoft YaHei', sans-serif", 400, "white-space:pre"
        # Pillow placed the line by its top (the font's ascender); SVG places it by its baseline.
        baseline = y + card_font(text_lang, size, weight).getmetrics()[0]
        out.append(f'<text x="{x}" y="{baseline}" font-family="{family}" font-size="{size}" font-weight="{weight}" '
                   f'fill="{hex_colour(colour)}" style="{style}" xml:space="preserve">{escape(text)}</text>')
    return "\n".join(out) + "\n</svg>\n"


SCALES = (100, 125, 150, 200, 400)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob("Logo*.png"):
        old.unlink()

    # Taskbar / Start / title bar: unplated target sizes, drawn edge to edge.
    for t in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
        icon = render(t)
        save(icon, f"Square44x44Logo.targetsize-{t}_altform-unplated.png")
        save(icon, f"Square44x44Logo.targetsize-{t}.png")

    for scale in SCALES:
        f = scale / 100
        save(canvas(round(44 * f), round(44 * f), 0.92), f"Square44x44Logo.scale-{scale}.png")
        save(canvas(round(150 * f), round(150 * f), 0.66), f"Square150x150Logo.scale-{scale}.png")
        save(canvas(round(310 * f), round(150 * f), 0.66), f"Wide310x150Logo.scale-{scale}.png")
        save(canvas(round(620 * f), round(300 * f), 0.5), f"SplashScreen.scale-{scale}.png")
        # In-app logos, one file per display scale at the exact pixel size (MRT picks the right one):
        # title bar 18 DIP, Settings 20 DIP, welcome dialog 48 DIP.
        for name, dip in (("LogoSmall", 18), ("LogoIcon", 20), ("LogoMedium", 48)):
            save(render(round(dip * f)), f"{name}.scale-{scale}.png")

    save(canvas(48, 48, 0.96), "LockScreenLogo.scale-200.png")
    save(canvas(50, 50, 0.96), "StoreLogo.png")

    # Multi-size ICO with PNG entries, each drawn at its own size.
    sizes = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    blobs = []
    for s in sizes:
        buf = io.BytesIO()
        render(s).save(buf, "PNG", optimize=True)
        blobs.append(buf.getvalue())
    ico = bytearray(struct.pack("<HHH", 0, 1, len(sizes)))
    offset = 6 + 16 * len(sizes)
    for s, blob in zip(sizes, blobs):
        ico += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    for blob in blobs:
        ico += blob
    (OUT / "AppIcon.ico").write_bytes(bytes(ico))

    (OUT / "Logo.svg").write_text(logo_svg(), encoding="utf-8")

    SITE_OUT.mkdir(parents=True, exist_ok=True)
    for lang in CARD_TEXT:
        social_card(lang).convert("RGB").save(SITE_OUT / f"social-{lang}.png", optimize=True)
        (SITE_OUT / f"social-{lang}.svg").write_bytes(social_card_svg(lang).encode("utf-8"))
    print(f"Assets written to {OUT}, link previews to {SITE_OUT}")


if __name__ == "__main__":
    main()
