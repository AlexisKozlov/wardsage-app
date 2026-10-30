#!/usr/bin/env python
"""The screen map: every form (Form_*) of the game client and how a pilot leaves it, from the client's own files only.

  python tools/screen-map.py                    -> src/ScreenMapData.cs (C# 5 static table, compiled into the app)
  python tools/screen-map.py --json <file>      -> the same table as JSON as well (for tools / a game model)
  python tools/screen-map.py --check            -> nothing written; exit 1 when src/ScreenMapData.cs differs
  options: --lua <decompiled Lua root (work/lua/src)>  --game <WOR dir>  --out <file>

Sources (the pipeline's dumps, nothing from a running game):
  - the decompiled client Lua, UI/**/Form_*.lua: RegisterLuaUI(name, class, stack) gives the form and its UI stack (StackFlow = the
    always-on flow layer), Form:IsFullScreen() the full-screen flag, Form:GetBackspaceBehav() what the game's back key (Escape) does
    on it (CS.UI.EBackspaceBehav: Handle = the form takes it - UIBase:OnActive binds it to OnDefaultBackClicked: the back arrow's
    logic, else the close handler, which hides the form; Block = swallowed; Ignore / Follow = passed on to the form below),
    UIHelper.NewButtonBack* the standard back arrow, AddBackPressListener a form's own back-key handler, and the calls that buy,
    spend, draw or start a fight (the dangerous ones);
  - the UI prefabs (StreamingAssets/UI/<prefab>.unity3d), measured like the site's geometry (~/wardsage/tools/ui-geometry.py):
    the back arrow (Btn_Back of the Button_Back* sub prefab), the close button (×) and the other buttons (never clicked).

Classes (one letter in the table):
  F  full screen with a back arrow (rect; "standard" when it holds HeroGeometry's back point top left)
  P  pop-up / modal with a close button × (rect)
  E  no button to press, but the game's back key closes it (GetBackspaceBehav Handle with the default handler): one Escape
  O  non-blocking overlay (the flow layer without a button): ignored
  K  a screen the app reads itself (the city, the hero screen, the inventory, the Foundry ...: OfferForms.KnownScreens)
  D  dangerous: no × / back arrow and it buys, spends, draws, starts a fight or quits (or a confirm dialog): the pilot stops
  W  battle / loading / result / tutorial: wait, never click
  P  also a tap-to-close form (UIHelper.NewClick2Close, a full-screen Click2Close): the tap strip at the bottom centre (TAP_RECT)
  U  none of these (no exit known): the pilot waits and stops after ClickGate.UnknownStallMs, as before

Rects: {x0 anchor, x0, x1 anchor, x1, y0 anchor, y0 (top), y1 anchor, y1 (bottom)} in U (the canvas height), anchors L C R / T M B
(OfferForms' format). Never-click buttons: every other button of the prefab whose name says buy / go / claim / confirm / fight ...;
their rects are kept when they lie near the exit, the generator drops an exit that lies on one.
"""
import sys, os, re, json, glob, argparse, importlib.util

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.dirname(HERE)
RF = os.environ.get('RF_ROOT', os.path.expanduser('~/wardsage'))
OUT_CS = os.path.join(APP, 'src', 'ScreenMapData.cs')

STD_BACK = (0.0515, 0.0367)        # HeroGeometry.BackX / BackY (src/HeroPilot.cs): from the top left, in U
TAP_RECT = ['C', -0.1, 'C', 0.1, 'B', -0.07, 'B', -0.02]   # the tap of a Click2Close form: the strip of «нажмите, чтобы закрыть» (CloseTextBg at B -0.045)
NEAR = 0.35                         # a never-click button this near (U) to the exit point keeps its rect in the table

# forms of the C# side (no Lua): the exit question of the city, the tips toast (a known screen), the battle test form
CS_FORMS = {'Form_SystemTips': ('D', 'quit: the game\'s exit question (Form_MainCity\'s back key)'),
            'Form_Battle': ('W', 'the fight (C# form)'), 'Form_BattleTest': ('W', 'the fight (C# form)')}
# the screens the app reads itself (OfferForms.KnownScreens): class K, their own pilots' fast paths decide there
KNOWN = ('Form_MainCity', 'Form_CharactorMain', 'Form_BackpackIntegration', 'Form_EquipSubliming', 'Form_EquipGemMain',
         'Form_EquipArtifactIntensify', 'Form_Battle', 'Form_CommonTips', 'Form_EquipPopUp', 'Form_EquipFilterWidget')
# OfferForms.IgnoredOverlays: always-on toasts, ignored whatever their prefab holds
IGNORED = ('Form_LeftTip', 'Form_Marquee', 'Form_MiniSystemTip', 'Form_PowerChangeTips', 'Form_IntegralRewardTip')

WAIT_RX = re.compile(r'^Form_(Battle(?!field|Pass|Report|Record|Formation|Team|Help|Rule|Tip|Prepare|Ready|Array|Lineup|Detail|Buff|Info|Hero|Statistic)\w*|\w*BattleEnd\w*|'
                     r'\w*Loading\w*|Guide\w*|\w*Cutscene\w*|Subtitle|Dialog|VideoContainer|\w*Waiting\w*|Loading\w*|\w*BattleTimeLine\w*|'
                     r'\w*BattleResult\w*|\w*BattleWin\w*|\w*BattleLose\w*|\w*BattleFail\w*|\w*BattleSettle\w*|Login\w*|Splash\w*|Update\w*)$')
CONFIRM_RX = re.compile(r'(Confirm|SecondTip|BuyTip|CostTip|PurchaseTip|PayTip|ExitTip|QuitTip|SpendTip)', re.I)
DANGER_CODE = re.compile(r'\b(CashPurchase\w*|DirectBuy|_DirectBuy|SendMessage_Shop_Buy|UseAndBuy\w*|RequestPurchase|SendLottery|RequestLottery|\w*LotteryDraw|'
                         r'RequestExchange|_TryToBuy\w*|_PurchaseGamePass\w*|StartBattle|_StartBattle|RequestStartBattle|TryToStartBattle|_SendStartBattle|'
                         r'ExitStarkApp|PurchaseManager)\s*\(')
NEVER_RX = re.compile(r'(Buy|Purchase|Pay|Recharge|Charge|Shop|Store|Mall|Gift|Pack|Goto|GoTo|Jump|Get|Claim|Receive|Reward|Confirm|Sure|Ok$|OK|Yes|'
                      r'Battle|Fight|Challenge|Start|Begin|Attack|Sweep|Raid|Summon|Draw|Lottery|Recruit|Upgrade|LevelUp|Levelup|Exchange|Sell|Use|'
                      r'Spend|Exit|Quit|Logout|Delete|Reset|Refresh|Unlock|Join|Apply|Create|Donate|Send|Accept|Plus|Add|Go$|Free|Open|Enter|Equip|Select|Choose)', re.I)
# the result forms a player's action opens (recast / ascension / promotion / gem results: Form_*Success*): never an Escape on them (they lie
# over a pilot's own screen, where the gate never sends one) - a tap-to-close one (Click2Close / Click2Coroutine) is closed by its tap strip,
# one that holds the player's choice (keep the old / take the new stats) waits: the choice is the player's
SUCCESS_RX = re.compile(r'^Form_\w*Success\d*$')
CHOICE_CODE = re.compile(r'(\w*SendMessage_\w*Choose\w*|GetRecastChooseEquip|_OnSuccess2Select)\s*\(')
CLOSE_RX = re.compile(r'^(m_?)?(Btn_?Close\w*|Button_?Close\w*|Close_?Btn\w*|Close_?Button\w*|btn_?close\w*|Close)$', re.I)


def load_uig():
    p = os.path.join(RF, 'tools', 'ui-geometry.py')
    spec = importlib.util.spec_from_file_location('uig', p)
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


# ---- Lua ---------------------------------------------------------------------------------------------------------
REG_RX = re.compile(r'^\s*RegisterLuaUI\(\s*("([^"]+)"|[A-Za-z_][A-Za-z0-9_]*)\s*,\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*(Stack\w+)', re.M)


def lua_forms(root):
    forms = {}
    for p in sorted(glob.glob(os.path.join(root, 'UI', '**', '*.lua'), recursive=True)):
        t = open(p, encoding='utf8', errors='replace').read()
        for m in REG_RX.finditer(t):
            name = m.group(2)
            if name is None:
                v = re.search(r'\b%s\s*=\s*"([^"]+)"' % re.escape(m.group(1)), t)
                if not v:
                    continue
                name = v.group(1)
            if not name.startswith('Form_') or name in forms:
                continue
            cls = m.group(3)
            forms[name] = {'cls': cls, 'stack': m.group(4), 'text': t, 'file': os.path.relpath(p, root).replace(os.sep, '/')}
    return forms


def fn_body(t, cls, fn):
    m = re.search(r'^function %s[:.]%s\(\)\s*\n(.*?)^end' % (re.escape(cls), fn), t, re.M | re.S)
    return m.group(1) if m else None


def lua_facts(f):
    t, cls = f['text'], f['cls']
    loc = re.search(r'^local fullscreen\s*=\s*(true|false)', t, re.M)
    fs = None
    b = fn_body(t, cls, 'IsFullScreen')
    if b is not None:
        r = b.strip()
        if r == 'return true': fs = True
        elif r == 'return false': fs = False
        elif r == 'return fullscreen' and loc: fs = loc.group(1) == 'true'
    elif loc:
        fs = loc.group(1) == 'true'
    esc = 'default'
    b = fn_body(t, cls, 'GetBackspaceBehav')
    if b is not None:
        r = re.findall(r'EBackspaceBehav\.(\w+)', b)
        esc = r[0] if len(set(r)) == 1 and re.match(r'^\s*return EBackspaceBehav\.\w+\s*$', b) else ('cond:' + '/'.join(sorted(set(r))) if r else 'cond')
    own = re.findall(r'AddBackPressListener\((.*)\)', t)
    own_ok = all(re.search(r'"(On\w*(Back|Close|Outside|Exit)\w*)"', a) for a in own) if own else True
    return {'fs': fs, 'esc': esc, 'backBtn': 'NewButtonBack' in t, 'tap': 'NewClick2Close' in t, 'ownBack': bool(own), 'ownBackOk': own_ok,
            'tapAny': 'NewClick2Close' in t or 'NewClick2Coroutine' in t, 'choice': bool(CHOICE_CODE.search(t)),
            'danger': sorted(set(m.group(1) for m in DANGER_CODE.finditer(t)))}


# ---- prefabs -------------------------------------------------------------------------------------------------------
def rect_of(m):
    return [m['x0'][0], m['x0'][1], m['x1'][0], m['x1'][1], m['y0'][0], m['y0'][1], m['y1'][0], m['y1'][1]]


def anchored(r):
    return '?' not in (r[0], r[2], r[4], r[6])


def place(r, W=16 / 9.0, H=1.0):
    """The rect in U on a 16:9 client (U = H = 1)."""
    def X(a, o): return (0 if a == 'L' else W / 2 if a == 'C' else W) + o
    def Y(a, o): return (0 if a == 'T' else H / 2 if a == 'M' else H) + o
    x0, x1, y0, y1 = X(r[0], r[1]), X(r[2], r[3]), Y(r[4], r[5]), Y(r[6], r[7])
    return min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)


def onscreen(r):
    """The rect's centre lies on the client at 16:9 and at 5:4 (U = the width-fitted height there)."""
    for W, H in ((16 / 9.0, 1.0), (16 / 9.0, 16 / 9.0 / 1.25)):
        x0, y0, x1, y1 = place(r, W, H)
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        if not (0.01 < cx < W - 0.01 and 0.01 < cy < H - 0.01):
            return False
    return True


def inside(pt, r, m=0.0):
    x0, y0, x1, y1 = place(r)
    return x0 + m <= pt[0] <= x1 - m and y0 + m <= pt[1] <= y1 - m


def shown(n):
    while n is not None:
        if not n.active:
            return False
        n = n.parent
    return True


def prefab_facts(g, game, name):
    try:
        root = g.build(game, name)
    except Exception as e:
        return {'err': str(e)[:60]}
    if root is None:
        return None
    btns = []
    for n in g.walk(root):
        if any(cn == 'Button' for cn, _ in n.comps) and n.parent is not None and shown(n):
            btns.append(n)   # (a button hidden in the prefab is shown by the form's code at times: not an exit to rely on)
    want = {i: n for i, n in enumerate(btns)}
    try:
        meas = g.measure(root, want) if want else {}
    except Exception as e:
        return {'err': 'measure: ' + str(e)[:60]}
    out = []
    for i, n in want.items():
        r = rect_of(meas[i])
        out.append({'name': n.name, 'path': n.path, 'rect': [r[0], round(r[1], 4), r[2], round(r[3], 4), r[4], round(r[5], 4), r[6], round(r[7], 4)],
                    'w': meas[i]['w'], 'h': meas[i]['h']})
    return {'buttons': out}


def classify(name, lf, pf):
    """(class, back/close rect or None ('std' = standard back), why, never names, never rects)"""
    if name in KNOWN:
        return 'K', None, 'a screen the app reads (its pilots decide there)', [], []
    if name in CS_FORMS:
        c, why = CS_FORMS[name]
        return c, None, why, [], []
    if name in IGNORED:
        return 'O', None, 'always-on overlay (OfferForms.IgnoredOverlays)', [], []
    btns = (pf or {}).get('buttons', [])
    never = [b for b in btns if NEVER_RX.search(b['name']) and not CLOSE_RX.match(b['name']) and b['name'] != 'Btn_Back']
    nnames = sorted(set(b['name'] for b in never))
    if WAIT_RX.match(name):
        return 'W', None, 'battle / loading / result / tutorial', nnames, []
    if lf['stack'] == 'StackFlow':
        if not btns:
            return 'O', None, 'flow layer, no button', [], []
        return 'W', None, 'flow layer with buttons (blocks)', nnames, []
    fs = lf['fs'] if lf['fs'] is not None else lf['stack'] == 'StackBase'
    esc_ok = lf['esc'] == 'Handle' and (not lf['ownBack'] or lf['ownBackOk'])

    def near(pt):
        res = []
        for b in never:
            x0, y0, x1, y1 = place(b['rect'])
            if x0 - NEAR <= pt[0] <= x1 + NEAR and y0 - NEAR <= pt[1] <= y1 + NEAR:
                res.append(b['rect'])
        return sorted(res)

    def clear(pt):
        return not any(inside(pt, b['rect']) for b in never)

    backs = [b for b in btns if b['name'] == 'Btn_Back' and anchored(b['rect']) and b['w'] > 0.02 and b['h'] > 0.02]
    if fs and backs:
        b = sorted(backs, key=lambda b: (place(b['rect'])[1], place(b['rect'])[0]))[0]
        r = b['rect']
        if inside(STD_BACK, r, 0.005) and r[0] == 'L' and r[2] == 'L' and r[4] == 'T' and r[6] == 'T' and clear(STD_BACK):
            return 'F', 'std', 'back arrow (standard, top left)', nnames, near(STD_BACK)
        x0, y0, x1, y1 = place(r)
        pt = ((x0 + x1) / 2, (y0 + y1) / 2)
        if clear(pt):
            return 'F', r, 'back arrow (' + b['path'].split('/', 1)[-1] + ')', nnames, near(pt)
    closes = [b for b in btns if CLOSE_RX.match(b['name']) and anchored(b['rect']) and onscreen(b['rect']) and 0.02 < b['w'] < 0.4 and 0.02 < b['h'] < 0.4]
    if closes:
        b = sorted(closes, key=lambda b: (b['path'].count('/'), b['w'] * b['h'], b['path']))[0]
        r = b['rect']
        x0, y0, x1, y1 = place(r)
        pt = ((x0 + x1) / 2, (y0 + y1) / 2)
        if clear(pt) and not fs:
            return 'P', r, 'close button (' + b['path'].split('/', 1)[-1] + ')', nnames, near(pt)
        if clear(pt) and fs and not backs:
            return 'P', r, 'full screen, close button (' + b['path'].split('/', 1)[-1] + ')', nnames, near(pt)
    if SUCCESS_RX.match(name) and not lf['danger']:
        if lf.get('choice'):
            return 'W', None, "a result with the player's choice (old / new): wait, never clicked", nnames, []
        taps = [b for b in btns if b['name'].startswith('Click2Close') and b['w'] > 1.5 and b['h'] > 0.9]
        if taps and lf.get('tapAny'):
            r = TAP_RECT
            x0, y0, x1, y1 = place(r)
            pt = ((x0 + x1) / 2, (y0 + y1) / 2)
            others = [b for b in btns if not b['name'].startswith('Click2Close')]
            if clear(pt) and not any(inside((x, y), b['rect']) for b in others for x in (x0, pt[0], x1) for y in (y0, pt[1], y1)):
                return 'P', r, 'a result, tap to close (the strip of its text at the bottom; never Escape)', nnames, near(pt)
    if lf['danger'] or (not fs and CONFIRM_RX.search(name)):
        return 'D', None, 'no ×: ' + (','.join(lf['danger'][:3]) if lf['danger'] else 'a confirm dialog'), nnames, []
    if esc_ok:
        return 'E', None, 'the back key closes it (GetBackspaceBehav Handle)', nnames, []
    # tap to close (UIHelper.NewClick2Close: a full-screen Click2Close button, «нажмите, чтобы закрыть» at the bottom centre - the reward
    # and notice forms): the tap goes to that text's strip, never onto a buy / claim / item button
    taps = [b for b in btns if b['name'] == 'Click2Close' and b['w'] > 1.5 and b['h'] > 0.9]
    if taps and lf.get('tap') and 'Chat' not in name:   # (the chat forms: their input lies at the bottom)
        r = TAP_RECT
        x0, y0, x1, y1 = place(r)
        pt = ((x0 + x1) / 2, (y0 + y1) / 2)
        others = [b for b in btns if b['name'] != 'Click2Close']   # no other button of the prefab under the strip (only the text is there)
        if clear(pt) and not any(inside((x, y), b['rect']) for b in others for x in (x0, pt[0], x1) for y in (y0, pt[1], y1)):
            return 'P', r, 'tap to close (Click2Close, the strip of its text at the bottom)', nnames, near(pt)
    return 'U', None, 'no exit known (backspace ' + lf['esc'] + (', own back-key handler' if lf['ownBack'] else '') + (', no prefab' if pf is None else '') + ')', nnames, []


# ---- output ----------------------------------------------------------------------------------------------------------
def cs_rect(r):
    if r is None: return 'null'
    if r == 'std': return 'Std'
    return "R('%s', %s, '%s', %s, '%s', %s, '%s', %s)" % (r[0], num(r[1]), r[2], num(r[3]), r[4], num(r[5]), r[6], num(r[7]))


def num(v):
    s = ('%.4f' % v).rstrip('0').rstrip('.')
    return '0.0' if s in ('-0', '0', '') else (s if '.' in s else s + '.0')


def cs_str(s):
    return '"' + s.replace('\\', '\\\\').replace('"', '\\"') + '"'


def emit_cs(rows, ver, src):
    L = ['// GENERATED by tools/screen-map.py from the game client (game %s; %s) - do not edit by hand, run the generator.' % (ver, src),
         '// The screen map: every form of the game and how a pilot leaves it (class letters and rect format: tools/screen-map.py; logic: src/ScreenMap.cs).',
         'namespace RealmForge {',
         '  public static partial class ScreenMap {',
         '    public const string GameVersion = %s;' % cs_str(ver or ''),
         '    static readonly Entry[] Data = {']
    for r in rows:
        nv = ', '.join(cs_rect(x) for x in r['nearRects'])
        L.append('      new Entry(%s, \'%s\', %s, %s, %s, %s%s),' % (cs_str(r['name']), r['class'], cs_str(r['esc']), cs_rect(r['rect']), cs_str(','.join(r['never'])),
                                                                    cs_str(r['why']), (', ' + nv) if nv else ''))
    L += ['    };', '  }', '}', '']
    return '\n'.join(L)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--lua', default=os.path.join(RF, 'work', 'lua', 'src'))
    ap.add_argument('--game', default=os.environ.get('WOR_ROOT', os.path.join(RF, 'game', 'WOR')))
    ap.add_argument('--out', default=OUT_CS)
    ap.add_argument('--json')
    ap.add_argument('--check', action='store_true')
    ap.add_argument('--quiet', action='store_true')
    a = ap.parse_args()
    g = load_uig()
    forms = lua_forms(a.lua)
    if len(forms) < 500:
        sys.exit('only %d forms in %s: not the decompiled client Lua?' % (len(forms), a.lua))
    rows = []
    names = sorted(set(forms) | set(CS_FORMS) | set(KNOWN))
    for i, name in enumerate(names):
        lf = lua_facts(forms[name]) if name in forms else {'fs': None, 'esc': 'cs', 'backBtn': False, 'ownBack': False, 'ownBackOk': True, 'danger': [], 'stack': 'StackMain'}
        if name in forms: lf['stack'] = forms[name]['stack']
        pf = prefab_facts(g, a.game, name) if name in forms else None
        c, rect, why, never, nearr = classify(name, lf, pf)
        rows.append({'name': name, 'class': c, 'esc': lf['esc'], 'rect': rect, 'never': never, 'why': why, 'nearRects': nearr,
                     'stack': lf['stack'], 'fullscreen': lf['fs'], 'file': forms[name]['file'] if name in forms else None})
        if not a.quiet and i % 200 == 0:
            print('  %d / %d forms' % (i, len(names)), file=sys.stderr)
    ver = g.game_version(a.game)
    txt = emit_cs(rows, ver, 'Lua UI/**/Form_*.lua + StreamingAssets/UI prefabs')
    cnt = {}
    for r in rows: cnt[r['class']] = cnt.get(r['class'], 0) + 1
    summary = ' '.join('%s=%d' % kv for kv in sorted(cnt.items()))
    if a.check:
        old = open(a.out, encoding='utf8').read() if os.path.exists(a.out) else ''
        print('screen map: %d forms (%s)%s' % (len(rows), summary, '' if old == txt else ' - DIFFERS from ' + a.out))
        sys.exit(0 if old == txt else 1)
    with open(a.out, 'w', encoding='utf8', newline='\n') as f:
        f.write(txt)
    if a.json:
        with open(a.json, 'w', encoding='utf8', newline='\n') as f:
            json.dump({'game': ver, 'classes': 'F back arrow, P close ×, E Escape, O overlay, D dangerous, W wait, U unknown',
                       'rect': '[x0 anchor, x0, x1 anchor, x1, y0 anchor, y0 (top), y1 anchor, y1 (bottom)] in U, anchors L C R / T M B; "std" = HeroGeometry back point',
                       'forms': rows}, f, indent=1, ensure_ascii=False)
    print('screen map: %d forms (%s) -> %s' % (len(rows), summary, a.out))


if __name__ == '__main__':
    main()
