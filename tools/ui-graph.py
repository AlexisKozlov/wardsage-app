#!/usr/bin/env python
"""The UI graph: every form of the game client, its clickable widgets, what each one does and the safe click paths between the
forms, from the client's own files only (the decompiled Lua and the UI prefabs; nothing from a running game).

  python tools/ui-graph.py                        -> src/UiGraphData.cs (C# 5 static table, src/UiGraph.cs reads it) + the report
  python tools/ui-graph.py --json <file>          -> the whole graph as JSON as well (every widget, its rect, handler and effects)
  python tools/ui-graph.py --check                -> nothing written; exit 1 when src/UiGraphData.cs differs
  options: --lua <decompiled Lua root (work/lua/src)>  --game <WOR dir>  --out <file>  --report <file>  --screen-map <ScreenMapData.cs>

Sources:
  - the forms: RegisterLuaUI (tools/screen-map.py's reader) and their class letter / Escape behaviour from src/ScreenMapData.cs (the
    screen map is generated first: one classification, the graph adds the widgets and the edges);
  - the widgets: the form's prefab (StreamingAssets/UI, tools/ui-geometry.py), with the panels its Lua builds in code mounted where the
    code puts them (Panel_X.new(self, self.m_Holder): Panel_X's prefab under m_Holder); every Button / Toggle / ActiveToggle, its
    path, rect (anchors and offsets in U, ui-geometry's format), shown in the prefab or not, in a list template or not, and its picture
    child (the icon plate a frame hugs);
  - the handlers: the bindings in the class's code (AddListener / NewCommonButton / BindCallback / RegisterButtonCallback with
    handlerEx(self, "Fn") on the widget's field, the bind callbacks of a list's template) and the generated name binding of the client
    (field m_Btn_Foo -> On<BtnFoo>Clicked, case-insensitive); the back arrow (NewButtonBack -> OnButtonBackClicked) and the tap to
    close (Click2Close);
  - the effects: the handler's code and one level of the helpers it calls on self (and on its form, for a panel): ShowUI(ID_FORM_X)
    opens X (a confirm form or a dangerous one makes the edge dangerous), CloseUI / Hide / CloseForm closes, the network requests
    (SendMessage_* / Request* / Send*: dangerous unless a read: Get / Info / Rank / List / View ...), screen-map's dangerous calls, a
    tab switch (a toggle, or a helper named *Tab / *Toggle / *Page / *Switch), a jump (the game's jump system: target not resolved);
  - the automatic forms: ActivityFormMgr's pop-up queue (its scene: the city, a dungeon, the lottery), forms the data / network
    modules open (outside UI/), and the forms a form opens from its event listeners (the results after its actions).

Paths: breadth first over the safe edges (an open that is neither a confirm nor dangerous) from Form_MainCity to every form; the pilots'
targets with their steps; the way back from a form is its exit (the screen map's class: back arrow, ×, Escape, tap).
"""
import sys, os, re, json, glob, argparse, importlib.util, collections
sys.dont_write_bytecode = True   # (screen-map.py is loaded as a module: no __pycache__ in tools)

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.dirname(HERE)
RF = os.environ.get('RF_ROOT', os.path.expanduser('~/wardsage'))
OUT_CS = os.path.join(APP, 'src', 'UiGraphData.cs')
SMAP_CS = os.path.join(APP, 'src', 'ScreenMapData.cs')
CITY = 'Form_MainCity'

CLICKABLE = ('Button', 'Toggle', 'ActiveToggle')
SAFE_REQ = re.compile(r'(Cancel|Get|Info|Rank|List|View|Init|Recommend|Search|Query|Detail|Report|Remind|SetFuncOpen|Preview|Check|Record|Log|Read|Load|Lock)', re.I)
TAB_RX = re.compile(r'(Tab|Toggle|Page|Switch|SubPanel|ChangePanel|Category|Mode)', re.I)
CONFIRM_FORM = re.compile(r'(Confirm|CommonPopup|SecondTip|BuyTip|CostTip|PurchaseTip|PayTip|ExitTip|QuitTip|SpendTip|BuyPopup|UnbindAlert)', re.I)
JUMP_RX = re.compile(r'\b(QuickJump\w*|JumpTo\w*|mtJump\w*|JumpManager|GotoFunc\w*|GoToFunc\w*|DoJump\w*|JumpSystem\w*|OpenFunction\w*)\s*[:.(]')
OPEN_RX = re.compile(r'\b(ShowUI|CheckAndShowUI|OpenUI|ShowUIEx|PushUI)\(\s*UIDefines\.(ID_FORM_\w+)')
CLOSEID_RX = re.compile(r'\bCloseUI\(\s*UIDefines\.(ID_FORM_\w+)')
CLOSESELF_RX = re.compile(r'\bself(\.m_\w*[Ff]orm\w*|\.m_[Pp]arent\w*|\.m_Owner\w*)?:(CloseForm|Hide|CloseSelf|OnBtnCloseClicked|OnButtonCloseClicked|OnButtonBackClicked|OnBackClk|Close)\(|'
                          r'CloseUI\(\s*self(\.m_\w+)?:GetID\(\)|\w+\.super\.OnButtonBackClicked\(')
REQ_RX = re.compile(r'\b(SendMessage_\w+|Request\w+|Req[A-Z]\w*|Send[A-Z]\w*)\s*\(')
CALL_RX = re.compile(r'\bself(\.m_\w*[Ff]orm\w*|\.m_[Pp]arent\w*)?:(\w+)\(')
HANDLER_RX = re.compile(r'handlerEx?\(\s*self(?:\.m_\w+)?\s*,\s*(?:"(\w+)"|self(?:\.m_\w+)?\.(\w+))\s*(?:,\s*([^)]*))?\)')
BIND_LINE = re.compile(r'(AddListener|NewCommonButton|NewButton\w*|BindCallback|AddClickListener|RegisterButtonCallback|SetClick\w*|AddClick\w*|SetCallback|SetOnClick\w*)\s*\(')
BINDCB_RX = re.compile(r'(NewItemsByLayout|RegisterBindCallback|NewItems\w*|RegisterItemCallback|SetBindFunc\w*)\s*\(([^\n]*)')
EVENT_RX = re.compile(r'(?:EventCenter|self):AddListener\(\s*([\w.]+)\s*,\s*handlerEx?\(\s*self\s*,\s*"(\w+)"')
STD_BASES = {'UIBase', 'PanelBase', 'PanelBase2', 'ViewBase'}
FORMLIKE = re.compile(r'^(Form_|Panel_|Widget_)')


def load_mod(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


# ---- the screen map (the class of every form, as the app has it) ----------------------------------------------------
ENTRY_RX = re.compile(r'new Entry\("([^"]+)", \'(\w)\', "([^"]*)", (null|Std|R\([^)]*\)), "([^"]*)", "((?:[^"\\]|\\.)*)"')


def read_smap(path):
    out = {}
    ver = ''
    t = open(path, encoding='utf8').read()
    v = re.search(r'GameVersion = "([^"]*)"', t)
    if v: ver = v.group(1)
    for m in ENTRY_RX.finditer(t):
        out[m.group(1)] = {'class': m.group(2), 'esc': m.group(3), 'exit': m.group(4), 'never': m.group(5), 'why': m.group(6)}
    return out, ver


# ---- Lua --------------------------------------------------------------------------------------------------------------
FN_RX = re.compile(r'^function ([A-Za-z_]\w*)[:.]([A-Za-z_]\w*)\(([^)]*)\)\s*$', re.M)
CLASS_RX = re.compile(r'\bclass\(\s*"(\w+)"\s*(?:,\s*(?:require\("([^"]+)"\)|(\w+)))?')
REQUIRE_RX = re.compile(r'^local (\w+) = require\("([^"]+)"\)', re.M)


class LuaFile:
    def __init__(self, root, rel):
        self.rel = rel
        self.text = open(os.path.join(root, rel), encoding='utf8', errors='replace').read()
        self.requires = {m.group(1): m.group(2) for m in REQUIRE_RX.finditer(self.text)}
        c = CLASS_RX.search(self.text)
        self.cls = c.group(1) if c else None
        base = None
        if c:
            base = c.group(2) or (self.requires.get(c.group(3)) if c.group(3) else None) or c.group(3)
        self.base = base
        self.fns = {}
        lines = self.text.split('\n')
        starts = [(m.start(), m) for m in FN_RX.finditer(self.text)]
        for pos, m in starts:
            ln = self.text.count('\n', 0, pos)
            body = []
            for l in lines[ln + 1:]:
                if l.startswith('end') and l.strip() == 'end':
                    break
                body.append(l)
            if self.cls is None or m.group(1) == self.cls:
                self.fns[m.group(2)] = (m.group(3), '\n'.join(body))
        self.mixins = re.findall(r'^RawInterface\(\s*\w+\s*,\s*require\("([^"]+)"\)\s*\)', self.text, re.M)
        self.prefab = None
        p = re.search(r'Instantiate\w*\(\s*"(\w+)"\s*,\s*ResourceType\.UI', self.text) or re.search(r'Load\w*Prefab\w*\(\s*"(\w+)"', self.text)
        if p: self.prefab = p.group(1)


class Lua:
    def __init__(self, root):
        self.root = root
        self.files = {}
        for p in glob.glob(os.path.join(root, 'UI', '**', '*.lua'), recursive=True):
            rel = os.path.relpath(p, root).replace(os.sep, '/')
            self.files[rel[:-4]] = LuaFile(root, rel)
        self.by_cls = {}
        for k, f in self.files.items():
            if f.cls and f.cls not in self.by_cls:
                self.by_cls[f.cls] = f

    def file(self, req):
        return self.files.get(req) if req else None

    def chain(self, f):
        """The class and its bases (the form's own first), the standard bases left out."""
        out, seen = [], set()
        while f is not None and f.rel not in seen:
            seen.add(f.rel)
            if f.cls in STD_BASES: break
            out.append(f)
            b = f.base
            f = self.file(b) if b and '/' in b else (self.by_cls.get(b) if b else None)
        return out

    def methods(self, f):
        m = {}
        for c in reversed(self.chain(f)):
            for mx in c.mixins:   # RawInterface(Class, require(...)): the mixin's methods are the class's
                mf = self.file(mx)
                if mf: m.update(mf.fns)
            m.update(c.fns)
        return m

    def texts(self, f):
        """The class's code with its mixins (and its bases'): where the panels are built."""
        out = []
        for c in self.chain(f):
            out.append(c)
            out.extend(x for x in (self.file(mx) for mx in c.mixins) if x)
        return out


# ---- prefabs ------------------------------------------------------------------------------------------------------------
def shown(n, upto=None):
    while n is not None and n is not upto:
        if not n.active: return False
        n = n.parent
    return True


def has(n, cname):
    return any(cn == cname for cn, _ in n.comps)


def find_node(root, name, walk):
    for n in walk(root):
        if n.name == name: return n
    return None


def icon_of(n):
    """The widget's picture child (the icon plate a frame hugs): the first Image within two levels named icon / img / image."""
    q = [(k, 1) for k in n.kids]
    while q:
        k, d = q.pop(0)
        if has(k, 'Image') and re.search(r'icon|img|image|pic', k.name, re.I) and not re.search(r'Effect|Select|Glow|Light|Red|Point|Mask|Got|Lock|New|Bg$', k.name, re.I):
            return k.name if d == 1 else k.parent.name + '/' + k.name
        if d < 2: q.extend((kk, d + 1) for kk in k.kids)
    return None


# ---- the graph ------------------------------------------------------------------------------------------------------------
class Graph:
    def __init__(self, lua, g, game, smap):
        self.lua, self.g, self.game, self.smap = lua, g, game, smap
        self.id2form = {}
        self.forms = {}

    def id_map(self, forms):
        for name, f in forms.items():
            lf = self.lua.files.get(f['file'][:-4])
            if lf and 'GetID' in lf.fns:
                m = re.search(r'UIDefines\.(ID_FORM_\w+)', lf.fns['GetID'][1])
                if m: self.id2form.setdefault(m.group(1), name)
        for name in forms:
            self.id2form.setdefault('ID_FORM_' + name[5:].upper(), name)

    def form_of_id(self, i):
        return self.id2form.get(i)

    # -- effects of a handler (its body and one level of helpers on self / the panel's form) --
    def effects(self, cls_file, fn, args, form_file, depth=0, seen=None):
        seen = seen if seen is not None else set()
        ms = self.lua.methods(cls_file)
        if fn not in ms or (cls_file.rel, fn) in seen:
            return None
        seen.add((cls_file.rel, fn))
        body = ms[fn][1]
        eff = []
        for m in OPEN_RX.finditer(body):
            t = self.form_of_id(m.group(2))
            eff.append(('open', t or '?' + m.group(2)))
        for m in CLOSEID_RX.finditer(body):
            t = self.form_of_id(m.group(1))
            eff.append(('closeForm', t or '?' + m.group(1)))
        if CLOSESELF_RX.search(body):
            eff.append(('close', 'self'))
        for m in REQ_RX.finditer(body):
            r = m.group(1)
            if r.startswith('Send') and not r.startswith('SendMessage') and re.match(r'Send(Event|Notify|Broadcast|Msg)\b', r): continue
            eff.append(('req', r + ('' if SAFE_REQ.search(r[11:] if r.startswith('SendMessage') else r) else '!')))
        for m in self.danger.finditer(body):
            eff.append(('req', m.group(1) + '!'))
        if JUMP_RX.search(body):
            eff.append(('jump', JUMP_RX.search(body).group(1)))
        if depth == 0:
            for m in CALL_RX.finditer(body):
                callee = m.group(2)
                if callee == fn: continue
                if m.group(1):   # the panel's form
                    tgt = form_file
                else:
                    tgt = cls_file
                if tgt is None: continue
                if TAB_RX.search(callee) and not re.search(r'Refresh|Red', callee):
                    eff.append(('tab', callee))
                sub = self.effects(tgt, callee, '', form_file, depth + 1, seen)
                if sub: eff.extend(sub)
        return eff

    def build_form(self, name, f):
        lf = self.lua.files.get(f['file'][:-4])
        root = self.g.build(self.game, name)
        out = {'name': name, 'file': f['file'], 'stack': f['stack'], 'widgets': [], 'mounts': [], 'listeners': [], 'prefab': root is not None}
        sm = self.smap.get(name, {})
        out['class'] = sm.get('class', '?'); out['esc'] = sm.get('esc', ''); out['exit'] = sm.get('exit', 'null')
        if lf is None or root is None:
            return out
        owners = {id(root): lf}
        # the panels the code builds into the form (Panel_X.new(self, self.m_Holder ...)), two levels
        def mount_panels(holder_root, cf, depth):
            found = []
            for src in self.lua.texts(cf):
                for var, req in src.requires.items():
                    for m in re.finditer(r'\b%s\.new\(\s*self\s*,\s*self\.(m_\w+)' % re.escape(var), src.text):
                        found.append((m.group(1), req, None))
                # UIBase:_CreateOrRefreshPanel(name, resPath, self.m_Holder.transform, type, function(go) ... require("X").new(self, go ...
                for m in re.finditer(r'_CreateOrRefreshPanel\(\s*"\w+"\s*,\s*"([\w/]+)"\s*,\s*self\.(m_\w+)[^\n]*\n?[^\n]*?require\("([^"]+)"\)\.new', src.text):
                    found.append((m.group(2), m.group(3), m.group(1).split('/')[-1]))
            for holder, req, prefab in found:
                pf = self.lua.file(req)
                if pf is None or pf.cls is None: continue
                at = find_node(holder_root, holder, self.g.walk)
                if at is None: continue
                prefab = prefab or pf.prefab or pf.cls
                if not self.g.find_bundle(self.game, prefab):
                    # the panel's objects are in the prefab already (a nested sub prefab under the holder): the holder is its root
                    if at.kids and id(at) not in owners:
                        owners[id(at)] = pf
                        out['mounts'].append({'at': holder, 'panel': pf.cls, 'prefab': '(nested)'})
                    continue
                if any(k.name == prefab for k in at.kids): continue
                try:
                    sub = self.g.build(self.game, prefab, at, 1)
                except Exception:
                    sub = None
                if sub is None: continue
                at.kids.append(sub)
                owners[id(sub)] = pf
                out['mounts'].append({'at': holder, 'panel': pf.cls, 'prefab': prefab})
                if depth < 3: mount_panels(sub, pf, depth + 1)
        mount_panels(root, lf, 1)
        nodes = list(self.g.walk(root))
        for n in nodes:   # a sub prefab with its own binder named like a Lua class (a panel the prefab nests): that class owns its fields
            if id(n) not in owners and has(n, 'LuaBinder') and n.name in self.lua.by_cls and self.lua.by_cls[n.name].fns:
                owners[id(n)] = self.lua.by_cls[n.name]
        btns = [n for n in nodes if any(cn in CLICKABLE for cn, _ in n.comps) and n.parent is not None]
        try:
            meas = self.g.measure(root, {i: n for i, n in enumerate(btns)}) if btns else {}
        except Exception as e:
            meas = {}
            out['measureErr'] = str(e)[:80]
        # the bindings of every class on the form (the form and its panels)
        binds = {}
        for oid, cf in owners.items():
            binds[oid] = self.bindings(cf)
        for i, n in enumerate(btns):
            owner_node = n.parent
            sub_widget = False
            while owner_node is not None and id(owner_node) not in owners:
                if has(owner_node, 'LuaBinder'): sub_widget = True   # a sub prefab with its own binder: its holder field is the owner's
                owner_node = owner_node.parent
            cf = owners.get(id(owner_node)) if owner_node is not None else lf
            tpl = False
            k = n
            while k is not None and k is not owner_node:
                if has(k, 'TemplateCache'): tpl = True
                k = k.parent
            field = n.name
            holder = None
            k = n.parent
            while k is not None and k is not owner_node:
                if k.name.startswith('m_') and holder is None and (has(k, 'SubPrefabHolder') or sub_widget or tpl): holder = k.name
                k = k.parent
            kind = 'toggle' if (has(n, 'Toggle') or has(n, 'ActiveToggle')) else 'button'
            r = meas.get(i)
            rect = None
            if r is not None:
                rect = [r['x0'][0], round(r['x0'][1], 4), r['x1'][0], round(r['x1'][1], 4), r['y0'][0], round(r['y0'][1], 4), r['y1'][0], round(r['y1'][1], 4)]
            w = {'path': n.path.split('/', 1)[1] if '/' in n.path else n.path, 'field': field, 'kind': kind, 'shown': shown(n), 'template': tpl,
                 'owner': cf.cls, 'holder': holder, 'rect': rect, 'w': r['w'] if r else 0, 'h': r['h'] if r else 0, 'icon': icon_of(n)}
            hd, how = self.handler(n, field, holder, cf, binds.get(id(owner_node), {}), lf, tpl)
            w['handler'], w['how'] = hd, how
            if hd:
                hfile = cf
                fn, args = hd
                e = [('close', 'self')] if fn == '@close' else [('jump', 'resource bar +'), ('req', 'ResourceBarBuy!')] if fn == '@buy' else [] if fn == '@tip' else self.effects(hfile, fn, args, lf)
                if fn == 'OnButtonBackClicked' and e is not None and not e:
                    e = [('close', 'self')]
                if fn in ('OnButtonBackClicked', 'OnBtnCloseClicked', 'OnButtonCloseClicked') and e is None:
                    e = [('close', 'self')]   # UIBase's own: Hide
                if e is None: w['handler'] = None; w['how'] = 'missing:' + fn
                else:
                    if kind == 'toggle' and not any(x[0] in ('open', 'tab') for x in e): e = [('tab', fn + ('(' + args + ')' if args else ''))] + e
                    w['effects'] = dedupe(e)
            self.judge(w)
            out['widgets'].append(w)
        # event listeners: the forms opened from them are the results of the form's actions
        for cf in owners.values():
            ms = self.lua.methods(cf)
            for body in [b for _, b in ms.values()]:
                for m in EVENT_RX.finditer(body):
                    e = self.effects(cf, m.group(2), '', lf)
                    opens = sorted(set(t for k2, t in (e or []) if k2 == 'open'))
                    if opens: out['listeners'].append({'event': m.group(1), 'fn': m.group(2), 'opens': opens})
        return out

    def bindings(self, cf):
        """field -> (fn, args) from the class's binding lines; template field -> [(fn, args)] from its bind callbacks."""
        ms = self.lua.methods(cf)
        out, tpl = {}, {}
        for fname, (_, body) in ms.items():
            for line in body.split('\n'):
                if not BIND_LINE.search(line): continue
                h = HANDLER_RX.search(line)
                if not h: continue
                fn = h.group(1) or h.group(2)
                args = (h.group(3) or '').strip()
                fields = re.findall(r'self\.(m_\w+)', line[:h.start()])
                rbc = re.search(r'RegisterButtonCallback\(\s*"(\w+)"', line)
                for fl in fields:
                    key = fl + ('/' + rbc.group(1) if rbc else '')
                    out.setdefault(key, (fn, args))
                if not fields:
                    tpl.setdefault(fname, []).append((fn, args))   # a local (a list item's toggle): its bind callback's
            for m in BINDCB_RX.finditer(body):
                fl = re.findall(r'self\.(m_\w+)', body[max(0, m.start() - 80):m.start()] + m.group(2))
                h = HANDLER_RX.search(m.group(2))
                if fl and h:
                    out.setdefault('#bind:' + (h.group(1) or h.group(2)), fl)
        return {'fields': out, 'local': tpl}

    def handler(self, n, field, holder, cf, b, lf, tpl):
        ms = self.lua.methods(cf)
        low = {k.lower(): k for k in ms}
        fields = b.get('fields', {}) if b else {}
        def key(nm): return nm[2:] if nm.startswith('m_') else nm
        # the standard back arrow / the tap to close
        if field == 'Btn_Back' or (holder in ('m_Back', 'm_ButtonBack', 'm_BtnBack') and n.parent is not None and n.parent.name.startswith('Button_Back')):
            mm = self.lua.methods(lf)
            return ('OnButtonBackClicked', ''), 'back'
        if field.startswith('Click2Close') or field == 'm_Click2Close':
            return ('@close', ''), 'tap'
        # the resource bar (UIHelper.NewResourceBar, its own widget class): a tip on the item, the shop / a purchase on its «+»
        k = n.parent
        while k is not None and not k.name.startswith('MainCity_ResourceBar'): k = k.parent
        if k is not None:
            return (('@buy', '') if 'Plus' in field else ('@tip', '')), 'resbar'
        # the help / rule button of a title (CommonPopTitle, Button_Back's Button_Help): the rule text, nothing else
        if re.match(r'^(Button_Help|m_Rule|m_HelpBtn|m_BtnHelp|m_BtnRule)$', field) or (holder in ('m_Rule', 'm_HelpBtn') and field.startswith('Button_Help')):
            return ('@tip', ''), 'help'
        # explicit binding on the field (or field_Component)
        for fl, v in fields.items():
            if fl.startswith('#bind:'): continue
            base = fl.split('/')[0]
            if '/' in fl:
                if fl.split('/')[1] == field and holder and (holder == base or base.startswith(holder + '_') or base.split('_')[0:2] == holder.split('_')[0:2]):
                    return v, 'grid'
                continue
            if base == field or (base.startswith(field + '_') and re.match(r'^_(Button|Toggle|ActiveToggle|Btn|Go|Obj|Widget|Trans\w*|Image|Rect\w*)$', base[len(field):])):
                return v, 'bind'
        # a list template's bind callback
        if tpl and b:
            for fl, v in fields.items():
                if not fl.startswith('#bind:'): continue
                cb = fl[6:]
                if (holder in v or field in v) and cb in b.get('local', {}):
                    return b['local'][cb][0], 'template'
                if (holder in v or field in v) and cb in ms:
                    loc = [x for x in HANDLER_RX.finditer(ms[cb][1])]
                    if loc and re.search(r'AddListener|onClick|onValueChanged', ms[cb][1]):
                        h = loc[-1]
                        return (h.group(1) or h.group(2), (h.group(3) or '').strip()), 'template'
        # the generated name binding: m_Btn_Foo -> OnBtnFooClicked
        for nm in (field, holder) if holder else (field,):
            k = 'on' + key(nm).replace('_', '').lower() + 'clicked'
            if k in low: return (low[k], ''), 'name'
            k2 = 'on' + key(nm).lower() + 'clicked'
            if k2 in low: return (low[k2], ''), 'name'
        # a close button (×) with the standard close handler
        if re.match(r'^(m_)?(Btn_?Close|Button_?Close|Close)\w*$', field, re.I) or (holder and re.match(r'^m_(Btn_?)?Close', holder)):
            for k in ('onbtncloseclicked', 'onbuttoncloseclicked', 'oncloseclicked'):
                if k in low: return (low[k], ''), 'name'
            return ('OnBtnCloseClicked', ''), 'close'
        # a sub widget bound by the form on its holder field
        if holder and holder in fields:
            return fields[holder], 'form'
        return None, None

    def judge(self, w):
        e = w.get('effects')
        if e is None:
            w['kind2'] = 'unresolved'
            return
        danger = any(k == 'req' and t.endswith('!') for k, t in e)
        conf = False
        for k, t in e:
            if k == 'open' and t and not t.startswith('?'):
                c = self.smap.get(t, {}).get('class')
                if c == 'D' or CONFIRM_FORM.search(t): conf = True
        w['danger'] = danger
        w['confirm'] = conf
        kinds = [k for k, _ in e]
        if 'open' in kinds: w['kind2'] = 'open'
        elif 'close' in kinds or 'closeForm' in kinds: w['kind2'] = 'close'
        elif 'tab' in kinds: w['kind2'] = 'tab'
        elif 'req' in kinds: w['kind2'] = 'request'
        elif 'jump' in kinds: w['kind2'] = 'jump'
        else: w['kind2'] = 'local'


def dedupe(e):
    out, seen = [], set()
    for x in e:
        if x not in seen:
            seen.add(x); out.append(x)
    return out


# ---- automatic forms ---------------------------------------------------------------------------------------------------
def auto_forms(root, graph, forms):
    auto = collections.defaultdict(set)
    p = os.path.join(root, 'Activity', 'ActivityFormMgr.lua')
    if os.path.exists(p):
        t = open(p, encoding='utf8', errors='replace').read()
        scenes = {'TryPopup_InDungeon': 'dungeon', 'TryPopup_InSuperDungeon': 'superDungeon', 'TryPopup_InLottery': 'lottery'}
        spans = []
        for m in FN_RX.finditer(t):
            spans.append((m.start(), m.group(2)))
        for m in re.finditer(r'UIDefines\.(ID_FORM_\w+)', t):
            fn = ''
            for s, nm in spans:
                if s <= m.start(): fn = nm
            if 'IsUIOnStackTop' in t[max(0, m.start() - 40):m.start()]: continue
            f = graph.form_of_id(m.group(1))
            if f and f != CITY: auto[f].add('activity:' + scenes.get(fn, 'city'))
    for p in glob.glob(os.path.join(root, '**', '*.lua'), recursive=True):
        rel = os.path.relpath(p, root).replace(os.sep, '/')
        if rel.startswith('UI/') or rel.startswith('Activity/ActivityFormMgr'): continue
        t = open(p, encoding='utf8', errors='replace').read()
        if 'ShowUI' not in t: continue
        src = rel.split('/')[0]
        tag = 'guide' if 'Guide' in src else 'battle' if re.search(r'Battle|JobFlow|SLG', src) else 'data:' + os.path.splitext(os.path.basename(rel))[0]
        for m in OPEN_RX.finditer(t):
            f = graph.form_of_id(m.group(2))
            if f and f != CITY: auto[f].add(tag)
    return auto


# ---- paths --------------------------------------------------------------------------------------------------------------
def safe_edge(w):
    return w.get('effects') and not w.get('danger') and not w.get('confirm')


def paths_from(graph_forms, start=CITY, smap=None):
    """Breadth first over the safe opens; prefers shown widgets over hidden ones / list templates (a step costs 1, a hidden one 1.5)."""
    import heapq
    best = {start: (0, None)}
    q = [(0, start)]
    while q:
        d, f = heapq.heappop(q)
        if d > best[f][0]: continue
        for w in graph_forms.get(f, {}).get('widgets', []):
            if not safe_edge(w): continue
            for k, t in w['effects']:
                if k != 'open' or not t or t.startswith('?') or t == f: continue
                c = (smap or {}).get(t, {}).get('class')
                if c in ('D', 'W'): continue
                cost = d + (1.0 if w['shown'] and not w['template'] else 1.5)
                if t not in best or cost < best[t][0] - 1e-9:
                    best[t] = (cost, (f, w))
                    heapq.heappush(q, (cost, t))
    return best


def chain_path(forms, chain):
    """The pilot's chain as graph steps (each a shown-or-hidden safe widget of the form that opens the next one); None = not in the graph."""
    if not chain: return None
    out = []
    for step in chain:
        a, b = step.split('>')
        ws = [w for w in forms.get(a, {}).get('widgets', []) if safe_edge(w) and ('open', b) in [tuple(x) for x in w['effects']]]
        if not ws: return None
        ws.sort(key=lambda w: (not w['shown'], w['template'], len(w['path'])))
        out.append((a, ws[0], b))
    return out


def path_to(best, t):
    if t not in best: return None
    out = []
    while best[t][1] is not None:
        f, w = best[t][1]
        out.append((f, w, t))
        t = f
    return list(reversed(out))


# The pilots' targets: (key, form, a widget field on it to press last or None, what the pilot does there)
PILOT_TARGETS = [
    ('inventory', 'Form_BackpackIntegration', None, 'Sell / ArtSell / Locate: the inventory (city «Инвентарь»)'),
    ('inventoryBulk', 'Form_BackpackIntegration', 'm_BtnBatchSell', 'Sell: «Массовая продажа» on the equipment tab'),
    ('artWorkshop', 'Form_EquipArtifactIntensify', None, 'LocateArtPromote / ArtSell: the artifact workshop'),
    ('foundry', 'Form_EquipSubliming', None, 'LocateAscend: the Foundry'),
    ('foundryAscend', 'Form_EquipSubliming', 'm_ToggleBuilding', 'LocateAscend: the Foundry tab «Вознесение»'),
    ('foundryRecast', 'Form_EquipSubliming', 'm_ToggleRecast', 'the Foundry tab «Перековка»'),
    ('foundryIdentify', 'Form_EquipSubliming', 'm_ToggleIdentify', 'the Foundry tab «Идентификация»'),
    ('foundryReforge', 'Form_EquipSubliming', 'm_ToggleReforge', 'the Foundry tab «Перековка свойств»'),
    ('foundrySubliming', 'Form_EquipSubliming', 'm_ToggleSubliming', 'the Foundry tab «Возвышение»'),
    ('hero', 'Form_CharactorMain', None, 'Equip / ArtEquip: the hero screen'),
    ('gem', 'Form_EquipGemMain', None, 'LocateRecast: the gem screen'),
    ('equipPlan', 'Form_EquipmentPlan', None, '«План сборки» (a start screen of the sim)'),
]
# the pilots' hand-written chains (src/*Pilot.cs, app/Overlay.cs: the city first, then these presses): the graph's path must be the same
PILOT_CHAINS = {
    'inventory': ['Form_MainCity>Form_BackpackIntegration'],
    'hero': ['Form_MainCity>Form_CharactorMain'],
    'foundry': ['Form_MainCity>Form_BackpackIntegration', 'Form_BackpackIntegration>Form_EquipSubliming'],
    'gem': ['Form_MainCity>Form_BackpackIntegration', 'Form_BackpackIntegration>Form_EquipGemMain'],
    'artWorkshop': ['Form_MainCity>Form_BackpackIntegration', 'Form_BackpackIntegration>Form_EquipArtifactIntensify'],
}


# ---- output -------------------------------------------------------------------------------------------------------------
def esc(s):
    return (s or '').replace('\\', '\\\\').replace('"', '\\"').replace('|', '/')


def num(v):
    s = ('%.4f' % v).rstrip('0').rstrip('.')
    return '0' if s in ('-0', '') else s


def rect_s(r):
    if not r: return ''
    return ','.join([r[0], num(r[1]), r[2], num(r[3]), r[4], num(r[5]), r[6], num(r[7])])


def eff_s(e):
    m = {'open': 'o', 'close': 'c', 'closeForm': 'x', 'tab': 't', 'req': 'r', 'jump': 'j'}
    return ';'.join(m[k] + ':' + (t or '') for k, t in e)


def emit_cs(forms, auto, paths, ver):
    L = ['// GENERATED by tools/ui-graph.py from the game client (game %s; Lua UI/** + StreamingAssets/UI prefabs) - do not edit by hand, run the generator.' % ver,
         '// The UI graph: every form, its widgets with a known effect, the automatic forms and the pilots\' paths (line format: src/UiGraph.cs).',
         'namespace RealmForge {',
         '  public static partial class UiGraph {',
         '    public const string GameVersion = "%s";' % esc(ver),
         '    static readonly string[] Data = {']
    used = set((f, w['path']) for _, steps in paths for f, w, _ in (steps or []))
    for name in sorted(forms):
        f = forms[name]
        L.append('      "F|%s|%s|%s|%s|%s",' % (name, f['class'], f['stack'], esc(f['esc']), ','.join(sorted(auto.get(name, ())))))
        for w in f['widgets']:
            e = w.get('effects')
            if e is None or (w['kind2'] == 'local' and (name, w['path']) not in used): continue   # (a local press on a path: a mode switch, kept)
            flags = ('s' if w['shown'] else '') + ('t' if w['template'] else '') + ('d' if w.get('danger') else '') + ('k' if w.get('confirm') else '') + ('g' if w['kind'] == 'toggle' else '')
            L.append('      "W|%s|%s|%s|%s|%s|%s",' % (esc(w['path']), esc(w['field']), flags, rect_s(w['rect']), esc(w.get('icon') or ''), esc(eff_s(e))))
    for k, steps in paths:
        L.append('      "P|%s|%s",' % (k, ' '.join('%s>%s>%s' % (f, esc(w['path']), t) for f, w, t in steps) if steps is not None else '-'))
    L += ['    };', '  }', '}', '']
    return '\n'.join(L)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--lua', default=os.path.join(RF, 'work', 'lua', 'src'))
    ap.add_argument('--game', default=os.environ.get('WOR_ROOT', os.path.join(RF, 'game', 'WOR')))
    ap.add_argument('--out', default=OUT_CS)
    ap.add_argument('--screen-map', default=SMAP_CS)
    ap.add_argument('--json')
    ap.add_argument('--report')
    ap.add_argument('--check', action='store_true')
    ap.add_argument('--quiet', action='store_true')
    ap.add_argument('--only', help='a comma list of forms (a quick look; nothing written)')
    a = ap.parse_args()
    g = load_mod(os.path.join(RF, 'tools', 'ui-geometry.py'), 'uig')
    sm = load_mod(os.path.join(HERE, 'screen-map.py'), 'smap')
    forms = sm.lua_forms(a.lua)
    if len(forms) < 500:
        sys.exit('only %d forms in %s: not the decompiled client Lua?' % (len(forms), a.lua))
    smap, smver = read_smap(a.screen_map)
    lua = Lua(a.lua)
    gr = Graph(lua, g, a.game, smap)
    gr.danger = sm.DANGER_CODE
    gr.id_map(forms)
    names = sorted(forms)
    if a.only: names = [n for n in a.only.split(',') if n in forms]
    out = {}
    for i, name in enumerate(names):
        try:
            out[name] = gr.build_form(name, forms[name])
        except Exception as e:
            out[name] = {'name': name, 'file': forms[name]['file'], 'stack': forms[name]['stack'], 'widgets': [], 'mounts': [], 'listeners': [], 'err': str(e)[:100],
                         'class': smap.get(name, {}).get('class', '?'), 'esc': smap.get(name, {}).get('esc', ''), 'exit': 'null', 'prefab': False}
        if not a.quiet and i % 100 == 0:
            print('  %d / %d forms' % (i, len(names)), file=sys.stderr)
    if a.only:
        json.dump(out, sys.stdout, indent=1, ensure_ascii=False, default=list)
        return
    for name, e in smap.items():   # the C# forms of the screen map (no Lua)
        if name not in out:
            out[name] = {'name': name, 'file': None, 'stack': 'cs', 'widgets': [], 'mounts': [], 'listeners': [], 'class': e['class'], 'esc': e['esc'], 'exit': e['exit'], 'prefab': False}
    auto = auto_forms(a.lua, gr, out)
    for name, f in out.items():
        for l in f['listeners']:
            for t in l['opens']:
                if t in out and t != name: auto[t].add('result:' + name)
    best = paths_from(out, CITY, smap)
    paths = []
    for key, form, widget, why in PILOT_TARGETS:
        p = path_to(best, form)
        ch = chain_path(out, PILOT_CHAINS.get(key if key in PILOT_CHAINS else {'foundryAscend': 'foundry', 'foundryRecast': 'foundry', 'foundryIdentify': 'foundry',
                                                                              'foundryReforge': 'foundry', 'foundrySubliming': 'foundry', 'inventoryBulk': 'inventory'}.get(key, key)))
        if ch is not None and ch[-1][2] == form and (p is None or len(ch) <= len(p)):
            p = ch   # the pilot's own chain is a safe path of the graph and no longer than the cheapest one: it is kept (the same steps)
        if p is not None and widget:
            ws = [w for w in out.get(form, {}).get('widgets', []) if w['field'] == widget or w.get('holder') == widget]
            if ws: p = p + [(form, ws[0], form + '#' + widget)]
            else: p = None
        paths.append((key, p))
    ver = g.game_version(a.game)
    txt = emit_cs(out, auto, paths, ver)
    rep = report(out, auto, best, paths, smap)
    if a.check:
        old = open(a.out, encoding='utf8').read() if os.path.exists(a.out) else ''
        print(rep.split('\n')[0] + ('' if old == txt else ' - DIFFERS from ' + a.out))
        sys.exit(0 if old == txt else 1)
    with open(a.out, 'w', encoding='utf8', newline='\n') as fo:
        fo.write(txt)
    if a.json:
        os.makedirs(os.path.dirname(os.path.abspath(a.json)), exist_ok=True)
        with open(a.json, 'w', encoding='utf8', newline='\n') as fo:
            json.dump({'game': ver, 'forms': out, 'auto': {k: sorted(v) for k, v in auto.items()},
                       'paths': {k: [[f, w['path'], t] for f, w, t in p] if p is not None else None for k, p in paths},
                       'reach': {k: round(v[0], 1) for k, v in best.items()}}, fo, indent=1, ensure_ascii=False, default=list)
    if a.report:
        os.makedirs(os.path.dirname(os.path.abspath(a.report)), exist_ok=True)
        with open(a.report, 'w', encoding='utf8', newline='\n') as fo:
            fo.write(rep)
    print(rep if not a.quiet else rep.split('\n')[0])


def report(out, auto, best, paths, smap):
    nf = len(out)
    ws = [w for f in out.values() for w in f['widgets']]
    res = [w for w in ws if w.get('effects') is not None]
    kinds = collections.Counter(w.get('kind2') for w in ws)
    danger = [w for w in ws if w.get('danger')]
    conf = [w for w in ws if w.get('confirm')]
    shownw = [w for w in ws if w['shown']]
    shownres = [w for w in shownw if w.get('effects') is not None]
    opens = [(f['name'], t) for f in out.values() for w in f['widgets'] for k, t in (w.get('effects') or []) if k == 'open']
    unres_open = [t for _, t in opens if t.startswith('?')]
    L = ['ui graph: %d forms, %d clickable widgets (%d shown in the prefab), %d with a resolved handler (%d%%; shown %d%%), %d dangerous, %d confirm, %d forms reachable from the city by safe clicks'
         % (nf, len(ws), len(shownw), len(res), 100 * len(res) // max(1, len(ws)), 100 * len(shownres) // max(1, len(shownw)), len(danger), len(conf), len(best))]
    L.append('widgets by effect: ' + ', '.join('%s %d' % kv for kv in sorted(kinds.items(), key=lambda x: -x[1])))
    L.append('transitions: %d opens (%d to an id with no form), %d closes; forms with a prefab %d, without %d'
             % (len(opens), len(unres_open), sum(1 for w in ws for k, _ in (w.get('effects') or []) if k in ('close', 'closeForm')),
                sum(1 for f in out.values() if f.get('prefab')), sum(1 for f in out.values() if not f.get('prefab'))))
    miss = collections.Counter(w['how'].split(':')[0] for w in ws if w.get('how') and w['how'].startswith('missing'))
    L.append('unresolved: %d widgets with no handler found, %d whose handler name is not in the class (%s)' % (kinds.get('unresolved', 0) - sum(miss.values()), sum(miss.values()), ','.join(sorted(set(w['how'][8:] for w in ws if (w.get('how') or '').startswith('missing')))[:8])))
    ac = collections.Counter(s.split(':')[0] for v in auto.values() for s in v)
    L.append('automatic forms: %d (%s)' % (len(auto), ', '.join('%s %d' % kv for kv in sorted(ac.items()))))
    succ = sorted(n for n in out if re.match(r'^Form_\w*Success\d*$', n))
    def openers(s):
        o = set(f['name'] for f in out.values() for w in f['widgets'] for k, t in (w.get('effects') or []) if k == 'open' and t == s)
        o |= set(x.split(':', 1)[1] for x in auto.get(s, ()) if x.startswith('result:'))
        return ','.join(sorted(o)[:4]) or '?'
    L.append('success / result forms: %d, opened by: %s' % (len(succ), '; '.join('%s <- %s' % (s, openers(s)) for s in succ)))
    cls = collections.Counter(f['class'] for f in out.values())
    noexit = sorted(n for n, f in out.items() if f['class'] == 'U' and n in best)
    L.append('screen-map classes: ' + ' '.join('%s=%d' % kv for kv in sorted(cls.items())) + '; reachable U forms (no exit known to the map): %d' % len(noexit))
    gclose = []
    for n in noexit:
        cw = [w for w in out[n]['widgets'] if w['shown'] and not w['template'] and any(k == 'close' for k, _ in (w.get('effects') or [])) and not w.get('danger') and w['rect']]
        if cw: gclose.append(n + ':' + cw[0]['path'])
    L.append('  of them the graph knows a close widget for: %d (%s)' % (len(gclose), ', '.join(gclose[:10])))
    L.append('paths from the city (safe clicks) to the pilots\' targets:')
    for key, p in paths:
        want = PILOT_CHAINS.get(key)
        got = ['%s>%s' % (f, t) for f, w, t in (p or []) if '#' not in t]
        same = '' if want is None else (' = the pilot\'s chain' if got == want else ' != the pilot\'s chain ' + ' '.join(want))
        L.append('  %-22s %s%s' % (key, ' | '.join('%s [%s] -> %s' % (f, w['path'], t) for f, w, t in p) if p is not None else 'NOT REACHABLE', same))
    unreach = sorted(n for n in out if n not in best and out[n]['class'] not in ('W', 'O', 'D'))
    L.append('forms not reachable by safe clicks from the city (W / O / D left out): %d' % len(unreach))
    return '\n'.join(L) + '\n'


if __name__ == '__main__':
    main()
