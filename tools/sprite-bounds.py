#!/usr/bin/env python3
"""The art of every UI sprite the forms of the UI graph draw: where its visible shape lies inside the sprite's rect.

Many buttons draw their visible shape inside a larger sprite with transparent padding and decorations (the city bar's
«Инвентарь»: a round plate with a pointed tip and side lines in a 108 x 88 sprite), so the RectTransform's rect is not
the shape a frame should hug. This reads, per sprite (by name), from its alpha:
  - the sprite's rect (px), pixels per unit and 9-slice border (px: left, bottom, right, top);
  - the visible box (alpha >= VIS_ALPHA, the main shape: its largest piece and the pieces at least a quarter of it), fractions of the
    rect (l, t, r, b; y down);
  - the main shape: c = circle (a circle fits its edge), r = rounded rect (corner radius, fraction of the box height), x = rect,
    p = irregular (the box and the contour only);
  - the circle fitted to the edge at VIS_ALPHA (the visible edge, a soft rim included) and at CORE_ALPHA (the solid body):
    centre x / y as fractions of the rect width / height, diameter as a fraction of its height;
  - the contour: the outer boundary of the main piece (holes filled, dilated 1 px), simplified to at most MAX_PTS points,
    normalised to the rect (0..1, 3 digits).
The app maps it through the live rect of the Image that draws the sprite (src/SpriteArt.cs) and draws the contour as the frame.

  python3 tools/sprite-bounds.py --game <WOR> --graph work/ui-graph/ui-graph.json [--out src/SpriteArtData.cs] [--json f] [--only name,..]
Needs UnityPy and numpy ($PYTHON of gamecdn/env.sh) and ~/wardsage/tools/ui-geometry.py (the prefab and atlas readers).
"""
import sys, os, re, gc, json, argparse, importlib.util, collections, time
sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.dirname(HERE)
RF = os.environ.get('RF_ROOT', os.path.expanduser('~/wardsage'))
OUT_CS = os.path.join(APP, 'src', 'SpriteArtData.cs')

VIS_ALPHA = 0.2     # what the eye sees as the shape's edge (a soft silver rim included)
CORE_ALPHA = 0.9    # the solid body
MAX_SIDE = 160      # the alpha is measured at most this many px on its long side (fractions do not need more)
MAX_PTS = 64        # contour points at most
THIN = 0.025       # strokes thinner than this part of the short side (x2) are decorations
KEEP_PIECE = 0.25   # pieces of the visible mask at least this part of the largest belong to the shape (else decorations: side lines, dots)

import numpy as np


def load_mod(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


# ---- masks (numpy only) ------------------------------------------------------------------------------------------
def shift_or(B, k=1):
    """B dilated by a (2k+1) square."""
    P = np.pad(B, k)
    out = np.zeros_like(B)
    h, w = B.shape
    for dy in range(2 * k + 1):
        for dx in range(2 * k + 1):
            out |= P[dy:dy + h, dx:dx + w]
    return out


def erode(B):
    P = np.pad(B, 1)
    out = np.ones_like(B)
    h, w = B.shape
    for dy in range(3):
        for dx in range(3):
            out &= P[dy:dy + h, dx:dx + w]
    return out


def pieces(B):
    """Labels of the 4-connected pieces of B (PIL's flood fill, in C): (label array, sizes list; label i+1 has sizes[i])."""
    from PIL import Image, ImageDraw
    h, w = B.shape
    im = Image.fromarray(np.where(B, 255, 0).astype(np.uint8)).copy()
    lab = np.zeros((h, w), dtype=np.int32)
    sizes = []
    left = B.copy()
    while True:
        ys, xs = np.nonzero(left)
        if not len(xs) or len(sizes) >= 250:
            break
        ImageDraw.floodfill(im, (int(xs[0]), int(ys[0])), 128, thresh=0)
        a = np.asarray(im) == 128
        sizes.append(int(a.sum())); lab[a] = len(sizes); left &= ~a
        ImageDraw.floodfill(im, (int(xs[0]), int(ys[0])), 1, thresh=0)
    return lab, sizes


def fill_holes(B):
    """B with its holes filled (what is not reached from the border through ~B)."""
    from PIL import Image, ImageDraw
    im = Image.fromarray(np.pad(np.where(B, 255, 0).astype(np.uint8), 1)).copy()
    ImageDraw.floodfill(im, (0, 0), 128, thresh=0)
    return (np.asarray(im) != 128)[1:-1, 1:-1]


def trace(B):
    """The outer boundary of the single piece B (Moore neighbour tracing): pixel (x, y) list, clockwise on screen."""
    ys, xs = np.nonzero(B)
    if not len(xs):
        return []
    i = np.lexsort((xs, ys))[0]
    start = (int(xs[i]), int(ys[i]))
    h, w = B.shape
    nb = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)]   # E, SE, S, SW, W, NW, N, NE (y down)
    def on(x, y): return 0 <= x < w and 0 <= y < h and B[y, x]
    pts = [start]; cur = start; d = 6   # came from the north (the start is the topmost, leftmost pixel)
    for _ in range(4 * (w + h) * 4 + 16):
        found = False
        for k in range(8):
            dd = (d + 6 + k) % 8   # start looking left of where we came from
            nx, ny = cur[0] + nb[dd][0], cur[1] + nb[dd][1]
            if on(nx, ny):
                cur = (nx, ny); d = dd; found = True
                break
        if not found or cur == start:
            break
        pts.append(cur)
    return pts


def dp(pts, eps):
    """Douglas-Peucker of an open polyline."""
    if len(pts) < 3:
        return pts
    a, b = np.array(pts[0], float), np.array(pts[-1], float)
    P = np.array(pts, float)
    ab = b - a; L = np.hypot(*ab)
    d = np.abs(ab[0] * (P[:, 1] - a[1]) - ab[1] * (P[:, 0] - a[0])) / L if L > 1e-9 else np.hypot(*(P - a).T)
    i = int(np.argmax(d))
    if d[i] <= eps:
        return [pts[0], pts[-1]]
    return dp(pts[:i + 1], eps)[:-1] + dp(pts[i:], eps)


def simplify(pts, max_pts):
    if len(pts) <= 3:
        return pts
    P = np.array(pts, float)
    far = int(np.argmax(np.hypot(P[:, 0] - P[0, 0], P[:, 1] - P[0, 1])))
    eps = 0.6
    while True:
        s = dp(pts[:far + 1], eps)[:-1] + dp(pts[far:] + [pts[0]], eps)[:-1]
        if len(s) <= max_pts:
            return s
        eps *= 1.4


def fit_circle(B):
    """A circle fitted to the edge of B, outliers (a tip, a notch) dropped: (cx, cy, r, inlier part) in px (pixel centres + 0.5)."""
    edge = B & ~erode(B)
    ys, xs = np.nonzero(edge)
    if len(xs) < 8:
        return None
    xs = xs + 0.5; ys = ys + 0.5
    keep = np.ones(len(xs), bool)
    cx = cy = r = 0.0
    for _ in range(10):
        x, y = xs[keep], ys[keep]
        if len(x) < 8:
            return None
        M = np.c_[2 * x, 2 * y, np.ones_like(x)]
        sol = np.linalg.lstsq(M, x * x + y * y, rcond=None)[0]
        cx, cy = sol[0], sol[1]; r = float(np.sqrt(max(0.0, sol[2] + cx * cx + cy * cy)))
        res = np.abs(np.hypot(xs - cx, ys - cy) - r)
        nk = res < max(1.0, 0.04 * r)
        if (nk == keep).all():
            break
        keep = nk
    return float(cx), float(cy), r, float(keep.mean())


def main_mask(A, thr):
    """The visible mask at thr without thin decorations (an opening of THIN of the short side: side lines, hairlines): the largest piece and
    the pieces at least KEEP_PIECE of it (after a 1 px dilate), holes filled. -> (union mask, main piece mask) or (None, None)."""
    B = A >= thr
    if not B.any():
        return None, None
    k = max(1, int(round(THIN * min(A.shape))))
    O = B
    for _ in range(k): O = erode(O)
    O = shift_or(O, k) & B
    if O.sum() < 0.3 * B.sum():   # all of it thin (a line, a ring of hairline): no opening
        O = B
    D = shift_or(O, 1)
    lab, sizes = pieces(D)
    big = int(np.argmax(sizes)) + 1
    keep = [i + 1 for i, s in enumerate(sizes) if s >= KEEP_PIECE * sizes[big - 1]]
    U = np.isin(lab, keep) & O
    M = fill_holes(lab == big)
    return U, M


def measure(alpha):
    """alpha: float array (h x w px of the sprite's rect, y down) -> the record (fractions)."""
    H0, W0 = alpha.shape
    k = min(1.0, MAX_SIDE / max(H0, W0))
    A = alpha
    if k < 1.0:
        from PIL import Image
        A = np.asarray(Image.fromarray((alpha * 255).astype(np.uint8)).resize((max(1, int(round(W0 * k))), max(1, int(round(H0 * k)))), Image.BILINEAR), float) / 255
    h, w = A.shape
    U, M = main_mask(A, VIS_ALPHA)
    if U is None:
        return None
    ys, xs = np.nonzero(U)
    box = [xs.min() / w, ys.min() / h, (xs.max() + 1) / w, (ys.max() + 1) / h]
    rec = {'box': [round(float(v), 4) for v in box]}
    # the shape of the main piece (without the 1 px dilate: the fit is on the visible edge)
    Mv = M & (A >= VIS_ALPHA) | (fill_holes(M & (A >= VIS_ALPHA)) & M)
    myx = np.nonzero(Mv)
    bw, bh = myx[1].max() - myx[1].min() + 1, myx[0].max() - myx[0].min() + 1
    fc = fit_circle(Mv)
    _, Mc = main_mask(A, CORE_ALPHA)
    cc = fit_circle(Mc & (A >= CORE_ALPHA)) if Mc is not None else None
    if fc and fc[3] >= 0.6:
        rec['circle'] = [round(fc[0] / w, 4), round(fc[1] / h, 4), round(2 * fc[2] / h, 4)]
    if cc and cc[3] >= 0.6:
        rec['core'] = [round(cc[0] / w, 4), round(cc[1] / h, 4), round(2 * cc[2] / h, 4)]
    fill = Mv.sum() / float(bw * bh)
    if fc and fc[3] >= 0.75 and 2 * fc[2] >= 0.8 * max(bw, bh):   # (a round plate with a small tip is a circle)
        rec['k'] = 'c'
    elif fill >= 0.97:
        rec['k'] = 'x'
    else:
        crop = Mv[myx[0].min():myx[0].max() + 1, myx[1].min():myx[1].max() + 1].astype(float)
        s = CLASSIFY(crop, 0.5) if crop.shape[0] >= 6 and crop.shape[1] >= 6 else {'k': 'p'}
        if s['k'] == 'round':
            rec['k'] = 'r'; rec['r'] = s['r']
        elif s['k'] == 'rect' and fill >= 0.9:
            rec['k'] = 'x'
        else:
            rec['k'] = 'p'
    # the contour of the main piece (dilated 1 px: the outline sits on the visible edge, not inside it)
    pts = trace(M)
    if len(pts) >= 8:   # the pixel steps smoothed (a circular mean of 5), then simplified
        P = np.array(pts, float); n = len(P)
        P = sum(np.roll(P, s, axis=0) for s in (-2, -1, 0, 1, 2)) / 5.0
        pts = [tuple(p) for p in P]
    if len(pts) >= 3:
        s = simplify(pts, MAX_PTS)
        poly = []
        for x, y in s:
            poly.append(min(1000, max(0, int(round((x + 0.5) / w * 1000)))))
            poly.append(min(1000, max(0, int(round((y + 0.5) / h * 1000)))))
        rec['poly'] = poly
    return rec


CLASSIFY = None
QUIET = False


def sprite_alpha(spr):
    """The sprite's alpha over its whole rect (a tight-packed sprite's image may be smaller: placed at its texture rect offset)."""
    img = spr['img']
    a = np.asarray(img.getchannel('A'), float) / 255
    rw, rh = int(round(spr['rect'][0])), int(round(spr['rect'][1]))
    if a.shape == (rh, rw) or rw <= 0 or rh <= 0:
        return a
    full = np.zeros((rh, rw))
    ox, oy = spr.get('off', (0, 0))
    y0 = rh - int(round(oy)) - a.shape[0]   # the offset is from the bottom left
    x0 = int(round(ox))
    y0 = max(0, min(rh - a.shape[0], y0)); x0 = max(0, min(rw - a.shape[1], x0))
    full[y0:y0 + a.shape[0], x0:x0 + a.shape[1]] = a[:rh - y0, :rw - x0]
    return full


# ---- the sprites of the forms -----------------------------------------------------------------------------------------
def sprite_refs(g, game, graph, only=None, forms=None):
    """{(cab, path id): set of 'Form/path' users} of every enabled Image with a sprite in the forms' prefabs and their mounted panels."""
    sp = g.Sprites(game)
    refs = collections.defaultdict(set)
    prefabs = []
    for name, f in sorted(graph['forms'].items()):
        if not f.get('prefab') or (forms and name not in forms):
            continue
        prefabs.append(name)
        for m in f.get('mounts', []):
            if m.get('prefab') and m['prefab'] != '(nested)':
                prefabs.append(m['prefab'])
    seen = set()
    for p in prefabs:
        if p in seen:
            continue
        seen.add(p)
        try:
            root = g.build(game, p)
        except Exception:
            root = None
        if root is None:
            continue
        for n in g.walk(root):
            t = g.comp(n, 'Image')
            if t is None or not t['m_Sprite']['m_PathID'] or not t['m_Sprite']['m_FileID']:
                continue
            try:
                ref = (sp.cab_of(n.bundle, t['m_Sprite']['m_FileID']), t['m_Sprite']['m_PathID'])
            except Exception:
                continue
            if only and n.name not in only and not any(o in n.path for o in only):
                continue
            refs[ref].add(n.path)
        g.Bundle.cache.clear()   # (every prefab once: the bundle cache would hold them all, several GB)
        if len(seen) % 50 == 0:
            gc.collect()
            if not QUIET: print('  %d / %d prefabs, %d sprites' % (len(seen), len(prefabs), len(refs)), file=sys.stderr)
    return sp, refs


LUA_SET = re.compile(r'SetAtlasSprite\(\s*"(Atlas_\w+)"\s*,\s*(?:string\.format\(\s*)?"([\w%]+)"(\s*\.\.)?')
LUA_IMG = re.compile(r'SetImage\([^,()]*(?:\([^()]*\))?[^,()]*,\s*"(Atlas_\w+)/([\w%]+)"(\s*\.\.)?')


def lua_refs(lua, sp):
    """The sprites the client's Lua puts on Images by name (SetAtlasSprite("Atlas_X", "name"), UIUtil.SetImage(img, "Atlas_X/name");
    a name built at run time - "prefix" .. n, string.format("prefix%02d", n) - takes every sprite of the atlas starting with the prefix):
    {(cab, path id): {'lua:<file>'}} (the item cells' quality frames are set so: common_Image_Depot_110N)."""
    want = collections.defaultdict(lambda: collections.defaultdict(set))   # atlas -> name or prefix* -> files
    for root, _, files in os.walk(lua):
        for fn in files:
            if not fn.endswith('.lua'): continue
            txt = open(os.path.join(root, fn), encoding='utf8', errors='replace').read()
            for rx in (LUA_SET, LUA_IMG):
                for m in rx.finditer(txt):
                    nm = m.group(2)
                    if '%' in nm: nm = nm.split('%')[0] + '*'
                    elif m.group(3): nm += '*'
                    if nm != '*': want[m.group(1)][nm].add(fn[:-4])
    import UnityPy
    out = collections.defaultdict(set)
    for atlas, names in sorted(want.items()):
        p = os.path.join(sp.dir, atlas + '.unity3d')
        if not os.path.exists(p): continue
        env = UnityPy.load(p)
        pre = [(n[:-1], f) for n, f in names.items() if n.endswith('*')]
        for o in env.objects:
            if o.type.name != 'Sprite': continue
            try:
                nm = o.peek_name() if hasattr(o, 'peek_name') else o.read().m_Name
            except Exception:
                continue
            users = set(names.get(nm, ()))
            for q, f in pre:
                if nm.startswith(q): users |= f
            if users:
                out[(o.assets_file.name.lower(), o.path_id)] |= {'lua:' + u for u in users}
                sp.index[o.assets_file.name.lower()] = atlas + '.unity3d'
        del env
        gc.collect()
    return out


def measure_all(sp, wanted):
    """Reads the wanted sprites one atlas bundle at a time and measures each at once (the images are not kept: several GB for all):
    {(cab, path id): (sprite info without its image, record or None)}."""
    import UnityPy
    sp.scan({c for c, _ in wanted})
    by = collections.defaultdict(list)
    for c, pid in wanted:
        by[sp.index.get(c)].append((c, pid))
    out = {}
    done = 0
    for fname, items in sorted(by.items(), key=lambda kv: kv[0] or ''):
        if fname is None:
            continue
        env = UnityPy.load(os.path.join(sp.dir, fname))
        objs = {(o.assets_file.name.lower(), o.path_id): o for o in env.objects}
        for k in items:
            o = objs.get(k)
            if o is None:
                continue
            try:
                x = o.read()
                spr = {'name': x.m_Name, 'img': x.image.convert('RGBA'), 'border': (x.m_Border.x, x.m_Border.y, x.m_Border.z, x.m_Border.w),
                       'ppu': x.m_PixelsToUnits, 'rect': (x.m_Rect.width, x.m_Rect.height)}
                try:
                    off = x.m_RD.textureRectOffset; spr['off'] = (off.x, off.y)
                except Exception:
                    pass
                rec = measure(sprite_alpha(spr))
            except Exception as e:
                if not QUIET: print('  skipped %s: %s' % (k, str(e)[:80]), file=sys.stderr)
                continue
            del spr['img']
            out[k] = (spr, rec)
        done += len(items)
        del env, objs
        gc.collect()
        if not QUIET: print('  %d / %d sprites measured (%s)' % (done, len(wanted), fname), file=sys.stderr)
    return out


def esc(s):
    return s.replace('\\', '\\\\').replace('"', '\\"')


def emit_cs(rows, ver):
    out = ['// GENERATED by tools/sprite-bounds.py from the game client (game %s; StreamingAssets/UI atlases of the UI graph\'s forms) - do not edit by hand, run the generator.' % ver,
           '// Each UI sprite\'s visible art inside its rect (line format: src/SpriteArt.cs).',
           'namespace RealmForge {',
           '  public static partial class SpriteArt {',
           '    public const string GameVersion = "%s";' % (ver or ''),
           '    static readonly string[] Data = {']
    for r in rows:
        out.append('      "%s",' % esc(r))
    out += ['    };', '  }', '}', '']
    return '\n'.join(out)


def fmt(v):
    s = ('%.4f' % v).rstrip('0').rstrip('.')
    return '0' if s in ('-0', '') else s


def row(name, spr, rec):
    """name|w|h|ppu|bl,bb,br,bt|l,t,r,b|k|r|cx,cy,d|core cx,cy,d|poly (x,y pairs 0..1000)"""
    b = spr['border']
    return '|'.join([name, fmt(spr['rect'][0]), fmt(spr['rect'][1]), fmt(spr['ppu']), ','.join(fmt(v) for v in b),
                     ','.join(fmt(v) for v in rec['box']), rec['k'], fmt(rec.get('r', 0)),
                     ','.join(fmt(v) for v in rec.get('circle', [])), ','.join(fmt(v) for v in rec.get('core', [])),
                     ','.join(str(v) for v in (rec.get('poly') or []) if rec['k'] != 'x')])   # (a rect's contour is its box)


def main():
    global CLASSIFY, QUIET
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=os.environ.get('WOR_ROOT', os.path.join(RF, 'game', 'WOR')))
    ap.add_argument('--graph', default=os.path.join(RF, 'work', 'ui-graph', 'ui-graph.json'))
    ap.add_argument('--out', default=OUT_CS)
    ap.add_argument('--json')
    ap.add_argument('--only', help='a comma list of node names / path parts (a quick look; nothing written)')
    ap.add_argument('--forms', help='a comma list of forms (a quick look; nothing written)')
    ap.add_argument('--lua', default=os.path.join(RF, 'work', 'lua', 'src'), help='the client\'s decompiled Lua (the sprites it sets by name)')
    ap.add_argument('--refs', help='a cache of the sprite list (written, or read when it exists)')
    ap.add_argument('--quiet', action='store_true')
    a = ap.parse_args()
    QUIET = a.quiet
    g = load_mod(os.path.join(RF, 'tools', 'ui-geometry.py'), 'uig')
    CLASSIFY = g.classify_shape
    graph = json.load(open(a.graph, encoding='utf8'))
    only = a.only.split(',') if a.only else None
    forms = a.forms.split(',') if a.forms else None
    only = only or (['/'] if forms else None)
    if a.refs and os.path.exists(a.refs) and not only:   # (a debugging shortcut: the sprite list of an earlier run)
        sp = g.Sprites(a.game)
        refs = {(c, int(p)): set(u) for c, p, u in json.load(open(a.refs))}
    else:
        sp, refs = sprite_refs(g, a.game, graph, only, forms)
        if a.refs: json.dump([[c, str(p), sorted(u)] for (c, p), u in refs.items()], open(a.refs, 'w'))
    if not refs:
        sys.exit('no sprites found in the forms of ' + a.graph)
    nprefab = len(refs)
    if not only and os.path.isdir(a.lua):
        for k, u in lua_refs(a.lua, sp).items():
            refs.setdefault(k, set()).update(u)
    measured = measure_all(sp, set(refs))
    by_name = {}
    clash = 0
    for ref, (spr, rec) in measured.items():
        nm = spr['name']
        if nm in by_name:
            if by_name[nm][1]['rect'] != spr['rect']:
                clash += 1
            if len(refs[ref]) <= len(refs[by_name[nm][0]]):
                continue
        by_name[nm] = (ref, spr, rec)
    rows, js, empty = [], {}, 0
    for nm in sorted(by_name):
        ref, spr, rec = by_name[nm]
        if rec is None:
            empty += 1
            continue
        rows.append(row(nm, spr, rec))
        js[nm] = dict(rec, rect=spr['rect'], ppu=spr['ppu'], border=spr['border'], users=sorted(refs[ref])[:5])
    ver = g.game_version(a.game)
    summary = 'sprite art: %d sprites of %d Image refs + %d set by the Lua (%d with the same name and another size kept once, %d fully transparent left out)' % (len(rows), nprefab, len(refs) - nprefab, clash, empty)
    if only:
        json.dump(js, sys.stdout, indent=1, ensure_ascii=False)
        print('\n' + summary, file=sys.stderr)
        return
    with open(a.out, 'w', encoding='utf8', newline='\n') as fo:
        fo.write(emit_cs(rows, ver))
    if a.json:
        os.makedirs(os.path.dirname(os.path.abspath(a.json)), exist_ok=True)
        with open(a.json, 'w', encoding='utf8', newline='\n') as fo:
            json.dump({'game': ver, 'sprites': js}, fo, indent=0, ensure_ascii=False)
    print(summary)


if __name__ == '__main__':
    main()
