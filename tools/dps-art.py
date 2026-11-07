#!/usr/bin/env python3
"""Builds the DPS meter's picture atlases and their index from the site's art (webp, read by PIL).

  python3 tools/dps-art.py --web <folder with public/art and data/> [--res app/res] [--cs src/DpsArtIndex.cs]

The web folder is a checkout (or `git archive`) of the web repo's origin/main with: public/art/{ui,skills,aspects,heads},
data/hero-pages/*.json, data/aspects.json, data/hero-head-crops.json.
Writes app/res/dps_ui.png (UI pieces at their own size, 32 bpp), app/res/dps_heads.jpg (hero portraits 64x64, 24 columns),
app/res/dps_skills.jpg (skill and aspect icons 64x64, 24 columns (JPEG q90, 4:2:0)) and src/DpsArtIndex.cs (name|x|y|w|h, hero -> rarity, class, faction).
Run it on the test server (not on the production machine): nice -n 19 python3 tools/dps-art.py --web /srv/work/artsrc
"""
import argparse, glob, json, os, re, sys
from PIL import Image

UI = ("common_description_tips common_bg_popup_titel common_btn_hortab0 common_btn_hortab2 common_btn_normal_s common_btn_special_s "
      "common_bg_barframe_01 bar_bg bar_red bar_yellow bar_blue bar_purple title_bg plate5 frame1 frame2 frame3 frame4 frame5 "
      "prof_Acrher prof_Fighter prof_Mage prof_Paladin prof_TacticMaster prof_Treatment back art-sep").split()
CELL, COLS, WIN = 64, 24, 104          # icon side in the atlas, columns of a grid, side of the head window in the 122x185 portrait
BG = (24, 28, 38)                       # what a transparent cut-out is laid on before JPEG (no alpha there)
BG_ASPECT = (176, 160, 120)             # the aspect icons are dark outline dragons with alpha: they lie on a light gold plate (on BG they were invisible)
PROF = {1: "Fighter", 2: "Paladin", 4: "Acrher", 8: "Mage", 16: "Treatment", 256: "TacticMaster"}   # lib/art.ts of the site

ap = argparse.ArgumentParser()
ap.add_argument("--web", required=True); ap.add_argument("--res", default="app/res"); ap.add_argument("--cs", default="src/DpsArtIndex.cs")
a = ap.parse_args()
art = os.path.join(a.web, "public/art")
jload = lambda p: json.load(open(os.path.join(a.web, p), encoding="utf-8"))
missing = []


def opened(path):
    if not os.path.exists(path):
        missing.append(os.path.relpath(path, a.web)); return None
    return Image.open(path).convert("RGBA")


def flat(im, side, back=BG):
    bg = Image.new("RGBA", im.size, back + (255,)); bg.alpha_composite(im)
    return bg.convert("RGB").resize((side, side), Image.LANCZOS)


# 1) the UI pieces: a shelf pack, widest atlas row 1024, 2 px gaps
pieces = [(n, opened(os.path.join(art, "ui", n + ".webp"))) for n in UI]
pieces = sorted([p for p in pieces if p[1] is not None], key=lambda p: (-p[1].height, p[0]))
x = y = shelf = 0; place = []
for n, im in pieces:
    if x + im.width > 1024: x, y, shelf = 0, y + shelf + 2, 0
    place.append((n, x, y, im)); x += im.width + 2; shelf = max(shelf, im.height)
H = 1
while H < y + shelf: H *= 2
ui = Image.new("RGBA", (1024, H), (0, 0, 0, 0))
for n, px, py, im in place: ui.paste(im, (px, py))
ui.save(os.path.join(a.res, "dps_ui.png"), optimize=True)
ui_rows = ["%s|%d|%d|%d|%d" % (n, px, py, im.width, im.height) for n, px, py, im in sorted(place)]

# 2) hero portraits: the head window of the cut-out (data/hero-head-crops.json), 48x48
crops = jload("data/hero-head-crops.json")
heroes = {}
for p in glob.glob(os.path.join(a.web, "data/hero-pages/2*.json")):
    h = json.load(open(p, encoding="utf-8"))
    if h.get("id"): heroes[h["id"]] = h
ids = sorted(i for i in heroes if os.path.exists(os.path.join(art, "heads", "HeroHead_%d.webp" % i)))
rows = (len(ids) + COLS - 1) // COLS
hd = Image.new("RGB", (COLS * CELL, max(1, rows) * CELL), BG)
head_rows = []; hero_rows = []
for k, i in enumerate(ids):
    im = opened(os.path.join(art, "heads", "HeroHead_%d.webp" % i))
    cx, cy = crops.get(str(i), [(im.width - WIN) // 2, 0])
    cx = max(0, min(im.width - WIN, cx)); cy = max(0, min(im.height - WIN, cy))
    sq = im.crop((cx, cy, cx + WIN, cy + WIN))
    gx, gy = (k % COLS) * CELL, (k // COLS) * CELL
    hd.paste(flat(sq, CELL), (gx, gy))
    head_rows.append("%d|%d|%d|%d|%d" % (i, gx, gy, CELL, CELL))
    h = heroes[i]; fac = (h.get("fac") or [{}])[0].get("id", 0)
    hero_rows.append("%d|%d|%s|%d" % (i, h.get("r", 0), PROF.get(h.get("cls"), ""), fac))
hd.save(os.path.join(a.res, "dps_heads.jpg"), quality=90, subsampling=2, optimize=True)   # 4:2:0

# 3) skill icons (the ones the names table knows) and aspect icons
icons = set()
for h in heroes.values():
    for s in h.get("skills", []):
        if s.get("icon"): icons.add(s["icon"])
names = [("%s" % n, os.path.join(art, "skills", n + ".webp")) for n in sorted(icons)]
for k, asp in sorted(jload("data/aspects.json").items(), key=lambda kv: int(kv[0])):
    if asp.get("icon"): names.append(("aspect:" + k, os.path.join(art, "aspects", k + ".webp")))
ims = [(n, opened(p)) for n, p in names]
BACK = lambda n: BG_ASPECT if n.startswith("aspect:") else BG
ims = [(n, im) for n, im in ims if im is not None]
rows = (len(ims) + COLS - 1) // COLS
sk = Image.new("RGB", (COLS * CELL, max(1, rows) * CELL), BG)
skill_rows = []
for k, (n, im) in enumerate(ims):
    gx, gy = (k % COLS) * CELL, (k // COLS) * CELL
    sk.paste(flat(im, CELL, BACK(n)), (gx, gy)); skill_rows.append("%s|%d|%d|%d|%d" % (n, gx, gy, CELL, CELL))
sk.save(os.path.join(a.res, "dps_skills.jpg"), quality=90, subsampling=2, optimize=True)   # 4:2:0


def arr(name, comment, rs):
    return "    // %s\n    static readonly string[] %s = {\n%s\n    };\n" % (comment, name, ",\n".join('    "%s"' % r for r in rs))


cs = """// GENERATED by tools/dps-art.py from the site's art (do not edit): where each picture sits in the DPS meter's atlases
// (app/res/dps_ui.png, dps_heads.jpg, dps_skills.jpg), and each hero's rarity, class and faction.
// %d UI pieces, %d heroes, %d skill and aspect icons.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RealmForge {
  public static class DpsArtIndex {
%s%s%s%s
    public static readonly Dictionary<string, int[]> Ui = Rects(UiRows), Heads = Rects(HeadRows), Skills = Rects(SkillRows);
    static readonly Dictionary<int, string[]> HeroInfo = Info();

    // name -> [x, y, w, h]
    static Dictionary<string, int[]> Rects(string[] rows) {
      var d = new Dictionary<string, int[]>();
      foreach (var r in rows) {
        var p = r.Split('|'); int x, y, w, h;
        if (p.Length == 5 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) && int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out y) &&
            int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out w) && int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) d[p[0]] = new[] { x, y, w, h };
      }
      return d;
    }

    static Dictionary<int, string[]> Info() {
      var d = new Dictionary<int, string[]>();
      foreach (var r in HeroRows) {
        var p = r.Split('|'); int id;
        if (p.Length == 4 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) d[id] = p;
      }
      return d;
    }

    /// <summary>The hero's portrait in dps_heads.jpg, or null.</summary>
    public static int[] Head(int hero) { int[] r; return Heads.TryGetValue(hero.ToString(CultureInfo.InvariantCulture), out r) ? r : null; }
    /// <summary>The skill's icon in dps_skills.jpg (by DpsNames.SkillIcon), or null.</summary>
    public static int[] Skill(int skillId) {
      string n = DpsNames.SkillIcon(skillId); int[] r;
      return n.Length > 0 && Skills.TryGetValue(n, out r) ? r : null;
    }
    /// <summary>A UI piece of dps_ui.png, or null.</summary>
    public static int[] Piece(string name) { int[] r; return Ui.TryGetValue(name, out r) ? r : null; }
    /// <summary>The hero's rarity (1..5), 0 when unknown.</summary>
    public static int Rarity(int hero) { string[] p; int v; return HeroInfo.TryGetValue(hero, out p) && int.TryParse(p[1], out v) ? v : 0; }
    /// <summary>The hero's class icon name in the UI atlas (prof_Mage ...), "" when unknown.</summary>
    public static string ClassPiece(int hero) { string[] p; return HeroInfo.TryGetValue(hero, out p) && p[2].Length > 0 ? "prof_" + p[2] : ""; }
    /// <summary>The hero's faction id (101 ...), 0 when unknown.</summary>
    public static int Faction(int hero) { string[] p; int v; return HeroInfo.TryGetValue(hero, out p) && int.TryParse(p[3], out v) ? v : 0; }
  }
}
""" % (len(ui_rows), len(hero_rows), len(skill_rows),
       arr("UiRows", "name|x|y|w|h in dps_ui.png", ui_rows), arr("HeadRows", "hero id|x|y|w|h in dps_heads.jpg", head_rows),
       arr("SkillRows", "icon name (or aspect:key)|x|y|w|h in dps_skills.jpg", skill_rows), arr("HeroRows", "hero id|rarity|class piece (prof_*)|faction id", hero_rows))
open(a.cs, "w", encoding="utf-8").write(cs)
for f in ("dps_ui.png", "dps_heads.jpg", "dps_skills.jpg"):
    print(f, os.path.getsize(os.path.join(a.res, f)), "bytes", Image.open(os.path.join(a.res, f)).size)
print("ui", len(ui_rows), "heroes", len(hero_rows), "skill icons", len(skill_rows), "missing art:", sorted(set(missing)))
