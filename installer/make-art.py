"""Installer art in the game's style (run once; the PNGs are committed): installer/art/*.png.

  python installer/make-art.py <realmforge-web checkout> <Cinzel Bold .ttf>

back.png   the wizard pages' background: the game's town art, darkened so the white text stays readable
side.png   the welcome / finish pages' picture: the game's battle art with a gold frame and REALMFORGE
small.png  the top-right emblem: gold crest with R
Sizes are for 200 % DPI; Inno Setup scales them down.
"""
import os
import sys
from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageFont

WEB, FONT = sys.argv[1], sys.argv[2]
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'art')
os.makedirs(OUT, exist_ok=True)
GOLD, GOLD_HI, INK = (232, 207, 142), (255, 241, 200), (11, 14, 20)


def art(name):
    return Image.open(os.path.join(WEB, 'public', 'art', 'ui', name)).convert('RGB')


def cover(im, w, h, cx=0.5):
    """Crop to the aspect of w×h around the horizontal centre cx, then resize."""
    iw, ih = im.size
    if iw / ih > w / h:
        nw = int(ih * w / h)
        x = int(min(max(0, cx * iw - nw / 2), iw - nw))
        im = im.crop((x, 0, x + nw, ih))
    else:
        nh = int(iw * h / w)
        im = im.crop((0, (ih - nh) // 2, iw, (ih - nh) // 2 + nh))
    return im.resize((w, h), Image.LANCZOS)


def vignette(im, strength):
    w, h = im.size
    m = Image.new('L', (w, h), 0)
    d = ImageDraw.Draw(m)
    for i in range(60):
        a = int(255 * (i / 60) ** 1.6 * strength)
        d.rectangle((i * w // 120, i * h // 120, w - i * w // 120, h - i * h // 120), outline=255 - a)
    m = m.filter(ImageFilter.GaussianBlur(w // 30))
    return Image.composite(im, Image.new('RGB', (w, h), INK), m)


# background of the wizard pages (window about 700×540 at 100 %)
back = cover(art('BG_Chapter8.webp'), 1400, 1080, 0.42)
back = ImageEnhance.Brightness(back).enhance(0.32)
back = ImageEnhance.Color(back).enhance(0.75)
back = Image.blend(back, Image.new('RGB', back.size, INK), 0.35)
back.save(os.path.join(OUT, 'back.png'), optimize=True)

# side picture of the welcome / finish pages (modern style: 240×459 at 100 %)
W, H = 480, 918
side = cover(art('BG_MoChao.webp'), W, H, 0.36)
side = ImageEnhance.Contrast(side).enhance(1.08)
grad = Image.new('L', (1, H))
for y in range(H):
    grad.putpixel((0, y), int(255 * max(0, (y - H * 0.55) / (H * 0.45)) ** 1.3))
side = Image.composite(Image.new('RGB', (W, H), INK), side, grad.resize((W, H)))
d = ImageDraw.Draw(side)
d.rectangle((6, 6, W - 7, H - 7), outline=(40, 30, 12), width=10)
d.rectangle((10, 10, W - 11, H - 11), outline=GOLD, width=4)
d.rectangle((18, 18, W - 19, H - 19), outline=(120, 96, 50), width=2)
f = ImageFont.truetype(FONT, 58)
t = 'REALMFORGE'
tw = d.textlength(t, font=f)
for dx, dy in ((0, 3), (2, 2), (-2, 2)):
    d.text(((W - tw) / 2 + dx, H - 190 + dy), t, font=f, fill=(0, 0, 0))
d.text(((W - tw) / 2, H - 190), t, font=f, fill=GOLD_HI)
f2 = ImageFont.truetype(FONT, 26)
s = 'WATCHER OF REALMS'
d.text(((W - d.textlength(s, font=f2)) / 2, H - 112), s, font=f2, fill=GOLD)
d.line((W * 0.2, H - 128, W * 0.8, H - 128), fill=(150, 120, 60), width=2)
side.save(os.path.join(OUT, 'side.png'), optimize=True)

# the emblem (58×58 at 100 %)
S = 116
em = Image.new('RGBA', (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(em)
pts = [(S / 2, 4), (S - 8, S * 0.28), (S - 8, S * 0.62), (S / 2, S - 4), (8, S * 0.62), (8, S * 0.28)]
d.polygon(pts, fill=(30, 22, 10), outline=GOLD)
d.line(pts + [pts[0]], fill=GOLD, width=5)
inner = [(x * 0.8 + S * 0.1, y * 0.8 + S * 0.1) for x, y in pts]
d.line(inner + [inner[0]], fill=(150, 120, 60), width=2)
f3 = ImageFont.truetype(FONT, 64)
bb = d.textbbox((0, 0), 'R', font=f3)
d.text(((S - (bb[2] - bb[0])) / 2 - bb[0], (S - (bb[3] - bb[1])) / 2 - bb[1]), 'R', font=f3, fill=GOLD_HI)
em.save(os.path.join(OUT, 'small.png'), optimize=True)
print('installer art written to', OUT)
