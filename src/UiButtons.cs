// Wardsage — a button's place on the screen read from the running game by its name (read-only).
//
// The pilots click points the site's geometry computes from the prefabs (tools/ui-geometry.py). This reads where the game
// itself has put the button now: the clicks and hint frames of the pilot buttons take it when it is fresh, visible and on the
// form on top (else the geometry's point), and the test build logs both (uibtn: ...) with a step-by-step dump of a failing read.
//
// The chain (no scan; the Lua root of src/LuaRoot.cs gives XLua.LuaEnv and UIInstance, walked again on every read):
//   UIInstance -> the form (class name; the shown copy whose C# side is alive) -> its Lua fields down the path (UIBase:Init binds the
//   prefab's m_* objects into the form table, UILuaHelper.BindViewObjects) -> a full userdata (tt 0x47; Lua 5.3 Udata: len 0x18,
//   data 0x28 = xLua's int object index) -> LuaEnv.translator 0x20 -> ObjectTranslator.objects 0xC8 (XLua.ObjectPool) -> list 0x10
//   (Slot[]: il2cpp array, length 0x18, data 0x20, Slot {int next; object obj 0x8}, 16 bytes) -> the managed UnityEngine.Object
//   -> m_CachedPtr 0x10 -> the native object.
// The form's root: its m_csui is a Form_LuaBase, a UIBase - a plain C# class, not a Unity object (its slot 0x10 is m_param, empty:
// live 2026-10-30 every form read "destroyed" there). UIBase.Destroyed 0xF8 off -> m_uiGameObject 0xB8 (managed GameObject) -> m_CachedPtr.
// Native (UnityPlayer.dll of Unity 2019.4.40f1, read from its icalls' code): Component.m_GameObject 0x30; GameObject
// components 0x30 (16-byte pairs, the component at 0x8; the Transform first), count 0x40, activeSelf 0x56, activeInHierarchy
// cached 0x57 (0xFF = not computed yet); Transform hierarchy 0x38, index 0x40, parent 0x90; TransformHierarchy local TRS
// array 0x18 (48 bytes each: position 0x0, rotation 0x10, scale 0x20); RectTransform anchorMin 0xC8, anchorMax 0xD0,
// anchoredPosition 0xD8, sizeDelta 0xE0, pivot 0xE8.
// The rect: the widget's transforms up to the form's root and on up to the root canvas (the topmost RectTransform with the client's
// shape: a screen-space canvas is the client size over its scale factor, so client px per canvas unit = W / its width, at any window
// size and scaler mode), each one's local position / scale from the hierarchy and its rect from its anchors and sizeDelta. No such
// canvas: the form's root fills a canvas 640 units high = U px (Ui.Unit). Rotation is assumed none (flagged when not).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace RealmForge {
  /// <summary>Where a button is on the screen now (client px), or the step the read stopped at (<see cref="FailStep"/>).</summary>
  public sealed class UiButtonInfo {
    public string Form = "", Widget = "", FailStep, FailValue;
    public double X, Y, W, H;       // the rect, client px (top left, size)
    public bool Visible, Rotated;   // shown form + active object; a transform on the way with a rotation
    public string ClassName = "";   // the managed class of the widget's object
    public long Ms;
    /// <summary>The form's table the rect was read on, how many shown copies of the form with a live C# side the window table has
    /// (1 = the instance is certain), and the client size the read was made for.</summary>
    public ulong FormTable; public int Copies; public double ForW, ForH;
    /// <summary>Where the px-per-canvas-unit scale came from: "canvas" (the root canvas's size against the client) or "unit" (Ui.Unit).</summary>
    public string Scale = "";
    /// <summary>Environment.TickCount when the read started (its age is counted from here).</summary>
    public int At;
    readonly System.Text.StringBuilder trace = new System.Text.StringBuilder();
    /// <summary>Each step's addresses and values, for the diagnostic dump (uibtn diag: ...).</summary>
    public string Trace { get { return trace.ToString(); } }
    internal void Tr(string k, string v) { if (trace.Length < 1800) trace.Append(k).Append('=').Append(v).Append(' '); }
    /// <summary>The button's icon (its picture child, the label left out: Icon widgets only), client px; IW 0 = none found (the button's rect frames it).</summary>
    public double IX, IY, IW, IH; public string IconName = "";
    public bool Ok { get { return FailStep == null; } }
    internal UiButtonInfo Fail(string step, string value) { FailStep = step; FailValue = value; return this; }
  }

  /// <summary>A button by name: the form's class and the Lua field paths that lead to it (the first that is there is taken).</summary>
  public sealed class UiWidget {
    public readonly string Form; public readonly string[] Paths;
    /// <summary>A bar button with a picture and a label under it: its frame hugs the picture (the icon child), not the whole button.</summary>
    public bool Icon;
    public UiWidget(string form, params string[] paths) { Form = form; Paths = paths; }
  }

  /// <summary>The icon of a bar button among its children (depth 1-2): active, not a label / red dot / effect by its name, centred on the button,
  /// near square; a name with icon / img / image the first, then the biggest. And its frame: the icon's rect outset by FramePad px with corners
  /// rounded to its size, kept inside the client. Pure (tests/CoreTests.cs).</summary>
  public static class UiIcon {
    public sealed class Cand { public string Name = ""; public double X, Y, W, H; public bool Active; public int Depth; }
    public const double FramePad = 2;
    static readonly string[] NotIcon = { "text", "txt", "label", "title", "name", "red", "dot", "num", "count", "tips", "effect", "fx", "lock", "new", "mask", "glow", "light", "arrow" };
    static readonly string[] IconWords = { "icon", "img", "image", "pic", "sprite" };
    /// <summary>The index of the icon in <paramref name="c"/> for the button's rect (bx, by, bw, bh); -1 = none (the button's rect frames it).</summary>
    public static int Pick(IList<Cand> c, double bx, double by, double bw, double bh) {
      int best = -1; double bs = double.NegativeInfinity;
      if (c == null || bw <= 0 || bh <= 0) return -1;
      for (int i = 0; i < c.Count; i++) {
        var k = c[i]; string n = (k.Name ?? "").ToLowerInvariant();
        if (!k.Active || k.W < 4 || k.H < 4) continue;
        bool bad = false; foreach (var t in NotIcon) if (n.Contains(t)) { bad = true; break; }
        if (bad) continue;
        double cx = k.X + k.W / 2, cy = k.Y + k.H / 2, a = k.W / k.H;
        if (cx < bx - bw * 0.1 || cx > bx + bw * 1.1 || cy < by - bh * 0.1 || cy > by + bh * 1.1) continue;
        if (a < 0.5 || a > 2) continue;
        if (k.W * k.H > bw * bh * 0.95 && k.W >= bw * 0.97 && k.H >= bh * 0.97) continue;   // the button's backdrop, not its picture
        double sc = k.W * k.H / (bw * bh) - 0.1 * k.Depth;
        foreach (var t in IconWords) if (n.Contains(t)) { sc += 2; break; }
        if (sc > bs) { bs = sc; best = i; }
      }
      return best;
    }
    /// <summary>The frame round the icon (x, y, w, h, corner radius), client px, within W x H.</summary>
    public static double[] Frame(double x, double y, double w, double h, double W, double H) {
      double l = Math.Max(0, x - FramePad), t = Math.Max(0, y - FramePad), r = Math.Min(W, x + w + FramePad), b = Math.Min(H, y + h + FramePad);
      return new[] { l, t, r - l, b - t, Math.Round(Math.Min(r - l, b - t) * 0.18) };
    }
  }

  public static partial class RFX {
    const int T_UDATA = 0x47;
    // XLua / il2cpp
    const int LuaEnvTranslator = 0x20, TranslatorObjects = 0xC8, PoolList = 0x10, ArrLen = 0x18, ArrData = 0x20, CachedPtr = 0x10;
    // UnityPlayer 2019.4.40f1
    const int CompGo = 0x30, GoComps = 0x30, GoCompCount = 0x40, GoActiveSelf = 0x56, GoActiveCached = 0x57;
    const int TrHier = 0x38, TrIndex = 0x40, TrParent = 0x90, HierLocal = 0x18, TrsSize = 48, RtFields = 0xC8;
    const int TrChildren = 0x70, TrChildCount = 0x80, GoName = 0x60;   // (Transform.m_Children: data, size; GameObject.m_Name: char*)
    // UIBase (the game's C# form class, a plain object; dump of 1.6.46.841.1): m_uiGameObject 0xB8, <Destroyed>k__BackingField 0xF8
    const int UiBaseGameObject = 0xB8, UiBaseDestroyed = 0xF8;
    public const double CanvasRefH = 640;

    /// <summary>The close buttons of the offer pop-ups (src/OfferForms.cs) and the back arrow of a full screen.</summary>
    static readonly string[] OfferClosePaths = { "m_BtnClose", "m_ButtonClose", "m_CloseBtn", "m_Button_Close", "m_BtnCloseShow", "Button_Close", "m_ButtonBack.m_btnBack" };

    /// <summary>The button a pilot's click target is, by name (null = not one the shadow reads). <paramref name="offerForm"/> = the
    /// pop-up an OfferClose closes.</summary>
    public static UiWidget UiWidgetOf(ClickTarget t, string offerForm) {
      switch (t) {
        case ClickTarget.HeroBack: return new UiWidget("Form_CharactorMain", "m_ButtonBack.m_btnBack");
        case ClickTarget.CityInventory: return new UiWidget("Form_MainCity", "m_Button_Backpack") { Icon = true };
        case ClickTarget.FoundryButton: return new UiWidget("Form_BackpackIntegration", "m_PanelContentCur.m_BtnWorkShop") { Icon = true };
        case ClickTarget.AscendTab: return new UiWidget("Form_EquipSubliming", "m_ToggleSubliming");
        case ClickTarget.EquipConfirm: return new UiWidget("Form_CharactorEquipTip", "m_SelectEquipView.m_RightBtn.m_gameObject", "m_OneEquipView.m_RightBtn.m_gameObject");
        case ClickTarget.OfferClose: return string.IsNullOrEmpty(offerForm) ? null : new UiWidget(offerForm, OfferClosePaths);
      }
      return null;
    }

    static int I32(byte[] b, int o) { return BitConverter.ToInt32(b, o); }
    static float F32(byte[] b, int o) { return BitConverter.ToSingle(b, o); }
    static string F2(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

    /// <summary>The managed object behind a Lua userdata (xLua's object pool slot; 0 = none, the step in <paramref name="r"/>), its class in <paramref name="cls"/>.</summary>
    static ulong ManagedOf(ulong env, ulong ud, string what, UiButtonInfo r, out string cls) {
      cls = null;
      var ub = Read(ud, 0x2C); if (ub == null || ub[8] != 7) { r.Fail("userdata " + what, "unreadable " + HexA(ud)); return 0; }
      if (BitConverter.ToUInt64(ub, 0x18) != 4) { r.Fail("userdata " + what, "len " + BitConverter.ToUInt64(ub, 0x18)); return 0; }
      int idx = I32(ub, 0x28);
      ulong tr = Ptr(env + LuaEnvTranslator);
      if (tr == 0 || ClassName(Ptr(tr)) != "XLua.ObjectTranslator") { r.Fail("translator", HexA(tr)); return 0; }
      ulong pool = Ptr(tr + TranslatorObjects), list = pool != 0 ? Ptr(pool + PoolList) : 0;
      if (list == 0) { r.Fail("objects", HexA(pool)); return 0; }
      ulong n = Ptr(list + ArrLen);
      if (idx < 0 || (ulong)idx >= n) { r.Fail("slot " + what, idx + " of " + n); return 0; }
      ulong obj = Ptr(list + ArrData + (ulong)idx * 16 + 8);
      if (obj == 0) { r.Fail("slot " + what, idx + " empty"); return 0; }
      cls = ClassName(Ptr(obj)) ?? "";
      r.Tr(what, "ud " + HexA(ud) + " slot " + idx + "/" + n + " obj " + HexA(obj) + " " + cls);
      return obj;
    }

    /// <summary>The native object of a Lua field holding a Unity object (its m_CachedPtr; 0 = none, the step in <paramref name="r"/>).</summary>
    static ulong NativeOf(ulong env, ulong ud, string what, UiButtonInfo r, out string cls) {
      ulong obj = ManagedOf(env, ud, what, r, out cls); if (obj == 0) return 0;
      ulong nat = Ptr(obj + CachedPtr);
      r.Tr(what + ".native", HexA(nat));
      if (nat == 0) { r.Fail("m_CachedPtr " + what, cls + " destroyed"); return 0; }
      return nat;
    }

    /// <summary>A form's C# side (m_csui) is a UIBase: a plain C# class (Form_LuaBase in the game), not a Unity object - its m_CachedPtr
    /// slot is its first field, empty on every live form (live 2026-10-30: "m_csui (Form_LuaBase destroyed)" on all of them).</summary>
    static bool IsUiBaseClass(string cls) { return cls == "UIBase" || cls.EndsWith("LuaBase", StringComparison.Ordinal) || cls.StartsWith("Form_", StringComparison.Ordinal); }

    /// <summary>The native root GameObject of a form from its m_csui (0 = none, the step in <paramref name="r"/>): a UIBase -> its Destroyed
    /// flag 0xF8 must be off -> m_uiGameObject 0xB8 (a managed GameObject) -> m_CachedPtr. Another class (a Unity object): its own m_CachedPtr.</summary>
    static ulong FormRootOf(ulong env, ulong csv, UiButtonInfo r, out string cls) {
      ulong obj = ManagedOf(env, csv, "m_csui", r, out cls); if (obj == 0) return 0;
      ulong mgo = Ptr(obj + UiBaseGameObject);
      string gcls = mgo != 0 ? ClassName(Ptr(mgo)) : null;
      bool uiBase = IsUiBaseClass(cls);
      if (uiBase || gcls == "UnityEngine.GameObject") {
        var db = Read(obj + UiBaseDestroyed, 1);
        r.Tr("m_csui.Destroyed", db == null ? "unreadable" : db[0].ToString());
        if (db == null) { r.Fail("m_csui", cls + " unreadable at " + HexA(obj)); return 0; }
        if (db[0] != 0) { r.Fail("m_csui", cls + " Destroyed"); return 0; }
        r.Tr("m_uiGameObject", HexA(mgo) + " " + (gcls ?? "?"));
        if (gcls != "UnityEngine.GameObject") { r.Fail("m_uiGameObject", mgo == 0 ? "null" : "class " + (gcls ?? "unreadable")); return 0; }
        ulong nat = Ptr(mgo + CachedPtr);
        r.Tr("m_uiGameObject.native", HexA(nat));
        if (nat == 0) { r.Fail("m_uiGameObject", "destroyed"); return 0; }
        cls = "UnityEngine.GameObject";
        return nat;
      }
      ulong n2 = Ptr(obj + CachedPtr);
      r.Tr("m_csui.native", HexA(n2));
      if (n2 == 0) { r.Fail("m_CachedPtr m_csui", cls + " destroyed"); return 0; }
      return n2;
    }

    /// <summary>The GameObject and Transform of a native object (a GameObject or a Component); false = the step in <paramref name="r"/>.</summary>
    static bool GoAndTransform(ulong nat, string cls, string what, UiButtonInfo r, out ulong go, out ulong tr) {
      go = cls == "UnityEngine.GameObject" ? nat : Ptr(nat + CompGo); tr = 0;
      var gb = go != 0 ? Read(go + GoComps, 0x18) : null;
      if (gb == null) { r.Fail("gameObject " + what, HexA(go)); return false; }
      ulong comps = BitConverter.ToUInt64(gb, 0); long cnt = BitConverter.ToInt64(gb, 0x10);
      if (comps == 0 || cnt < 1 || cnt > 256) { r.Fail("components " + what, cnt.ToString()); return false; }
      tr = Ptr(comps + 8);
      r.Tr(what + ".go", HexA(go) + " tr " + HexA(tr));
      if (tr == 0 || Ptr(tr + CompGo) != go) { r.Fail("transform " + what, HexA(tr)); return false; }
      return true;
    }

    /// <summary>A Lua path "a.b.c" from table <paramref name="t"/>: the last field's value and type (false = a link missing).</summary>
    static bool LuaPath(ulong t, string path, out ulong v, out int tt, out string miss) {
      v = 0; tt = 0; miss = null;
      var parts = path.Split('.');
      for (int i = 0; i < parts.Length; i++) {
        if (!Field(t, parts[i], out v, out tt) || tt == T_NIL) { miss = parts[i] + " missing"; return false; }
        if (i < parts.Length - 1) { if (tt != T_TABLE) { miss = parts[i] + " tt " + tt; return false; } t = v; }
      }
      return true;
    }

    /// <summary>The form of class <paramref name="cls"/> in the window table, read now: the shown one whose C# side is alive (not Destroyed, its
    /// root GameObject there) first, then any alive one, then a shown one, then any (0 = none). The game builds a form again (the city after the
    /// guild): a destroyed copy is never preferred. <paramref name="copies"/> = the shown live copies (more than 1: the instance is not certain).</summary>
    static ulong FormOf(ulong env, ulong ui, string cls, UiButtonInfo r, out bool shown, out int copies) {
      shown = false; copies = 0;
      var h = Read(ui, 56); if (h == null || h[8] != 5 || h[11] > 12) return 0;
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return 0;
      ulong best = 0; int bestRank = -1; var seen = new System.Text.StringBuilder();
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (I32(nb, o + 24) != T_INT || I32(nb, o + 8) != T_TABLE) continue;
        ulong f = BitConverter.ToUInt64(nb, o);
        string why;
        if (FormClassName(f, out why) != cls) continue;
        bool sh = Shown(f), alive = CsuiAlive(env, f);
        if (sh && alive) copies++;
        int rank = (alive ? 2 : 0) + (sh ? 1 : 0);
        if (seen.Length < 200) seen.Append(HexA(f)).Append(sh ? "+shown" : "").Append(alive ? "+alive" : "+dead").Append(',');
        if (rank > bestRank) { best = f; bestRank = rank; shown = sh; }
      }
      r.Tr("forms", seen.Length > 0 ? seen.ToString() : "none");
      return best;
    }

    /// <summary>Is the form's C# side (m_csui) alive: not Destroyed and its root GameObject a live Unity object?</summary>
    static bool CsuiAlive(ulong env, ulong f) {
      ulong csv; int ctt;
      if (env == 0 || !Field(f, "m_csui", out csv, out ctt) || ctt != T_UDATA) return false;
      var probe = new UiButtonInfo(); string cls;
      return FormRootOf(env, csv, probe, out cls) != 0;
    }

    /// <summary>The live rect of a button by name, from <paramref name="env"/> (XLua.LuaEnv) and <paramref name="ui"/> (UIInstance),
    /// for a game window of client size W x H px and UI unit U px (the fallback scale when the root canvas cannot be told).</summary>
    public static UiButtonInfo UiButtonRead(ulong env, ulong ui, UiWidget w, double W, double H, double U) {
      var sw = Stopwatch.StartNew();
      var r = new UiButtonInfo { Form = w.Form, Widget = w.Paths.Length > 0 ? w.Paths[0] : "", ForW = W, ForH = H, At = Environment.TickCount };
      try { UiButtonReadInto(env, ui, w, W, H, U, r); }
      catch (Exception e) { r.Fail("exception", e.Message); }
      finally { r.Ms = sw.ElapsedMilliseconds; }
      return r;
    }

    /// <summary>The rect fields of a RectTransform {anchorMin, anchorMax, anchoredPosition, sizeDelta, pivot} (null = unreadable or not one:
    /// anchors and pivot are fractions, the rest finite).</summary>
    static double[] RectFields(ulong t, out string bad) {
      bad = null;
      var fb = Read(t + RtFields, 40); if (fb == null) { bad = "unreadable"; return null; }
      var v = new double[10];
      for (int k = 0; k < 10; k++) {
        v[k] = F32(fb, 4 * k);
        if (double.IsNaN(v[k]) || double.IsInfinity(v[k]) || ((k < 4 || k >= 8) && (v[k] < -0.01 || v[k] > 1.01)) || Math.Abs(v[k]) > 1e5) {
          bad = "value " + k + " = " + v[k].ToString("R", CultureInfo.InvariantCulture); return null;
        }
      }
      return v;
    }

    /// <summary>The root canvas above the form's root <paramref name="rtr"/>: the topmost RectTransform whose size has the client's shape
    /// (a screen-space canvas is sized to the screen over its scale factor, whatever the CanvasScaler's mode). Fills <paramref name="above"/> with the
    /// transforms from it down to (not with) <paramref name="rtr"/>; false = none such (the Ui.Unit scale is the fallback).</summary>
    static bool CanvasAbove(ulong rtr, double W, double H, UiButtonInfo r, List<ulong> above) {
      above.Clear();
      var ups = new List<ulong>(); ulong t = Ptr(rtr + TrParent);
      while (t != 0 && ups.Count < 24) { ups.Add(t); t = Ptr(t + TrParent); }
      double want = W / H;
      for (int i = ups.Count - 1; i >= 0; i--) {   // topmost first
        string bad; var v = RectFields(ups[i], out bad);
        if (v == null) continue;
        double sx = v[6], sy = v[7];
        if (sx < 1 || sy < 1) continue;
        if (Math.Abs(sx / sy - want) / want > 0.03) { r.Tr("canvas?", "#" + i + " " + F2(sx) + "x" + F2(sy) + " shape"); continue; }
        string bad2 = null;
        for (int j = i - 1; j >= 0 && bad2 == null; j--) if (RectFields(ups[j], out bad2) == null) bad2 = "#" + j + " " + bad2;
        if (bad2 != null) { r.Tr("canvas", HexA(ups[i]) + " but no RectTransform below it: " + bad2); return false; }
        for (int j = i; j >= 0; j--) above.Add(ups[j]);
        r.Tr("canvas", HexA(ups[i]) + " " + F2(sx) + "x" + F2(sy) + " (" + (ups.Count - i) + " up of " + ups.Count + ")");
        return true;
      }
      r.Tr("canvas", "none of " + ups.Count + " up");
      return false;
    }

    /// <summary>The rect (client px) of the last transform of <paramref name="chain"/> (the canvas or the form's root first). <paramref name="main"/>:
    /// the button's own read (its scale and rotation noted in <paramref name="r"/>).</summary>
    static bool ChainRect(List<ulong> chain, bool canvas, double W, double H, double U, UiButtonInfo r, bool main, out double x, out double y, out double w, out double h) {
      int n = chain.Count; x = y = w = h = 0;
      var lp = new double[n, 2]; var ls = new double[n, 2]; var rt = new double[n][];
      for (int i = 0; i < n; i++) {
        var hb = Read(chain[i] + TrHier, 12); if (hb == null) { r.Fail("hierarchy", HexA(chain[i])); return false; }
        ulong hier = BitConverter.ToUInt64(hb, 0); int ix = I32(hb, 8);
        ulong arr = hier != 0 ? Ptr(hier + HierLocal) : 0;
        var trs = arr != 0 && ix >= 0 && ix < 100000 ? Read(arr + (ulong)ix * TrsSize, TrsSize) : null;
        if (trs == null) { r.Fail("local transform", HexA(hier) + "[" + ix + "]"); return false; }
        lp[i, 0] = F32(trs, 0); lp[i, 1] = F32(trs, 4);
        ls[i, 0] = F32(trs, 0x20); ls[i, 1] = F32(trs, 0x24);
        double qx = F32(trs, 0x10), qy = F32(trs, 0x14), qz = F32(trs, 0x18), qw = F32(trs, 0x1C);
        if (i > 0 || !canvas) if (Math.Abs(qx) > 1e-3 || Math.Abs(qy) > 1e-3 || Math.Abs(qz) > 1e-3 || Math.Abs(Math.Abs(qw) - 1) > 1e-3) { if (main) r.Rotated = true; }
        string bad; rt[i] = RectFields(chain[i], out bad);
        if (rt[i] == null) { r.Fail("rect fields", "#" + i + " " + bad); return false; }
      }
      double k2, cw, ch, pw, ph, sx = 1, sy = 1, px, py; int i0;
      if (canvas) {   // the canvas: its centre the origin, y up; children placed from its pivot
        cw = rt[0][6]; ch = rt[0][7]; k2 = W / cw;
        pw = cw; ph = ch; px = (rt[0][8] - 0.5) * cw; py = (rt[0][9] - 0.5) * ch; i0 = 1;
        if (main) r.Scale = "canvas";
        if (main) r.Tr("scale", "canvas " + F2(k2) + " px/unit (" + F2(H / ch) + " by height) vs unit " + F2(U / CanvasRefH));
      } else {        // the form's root in a canvas of 640 units high = U px (its parent assumed to fill the canvas)
        k2 = U / CanvasRefH; cw = W / k2; ch = H / k2; pw = cw; ph = ch; px = py = 0; i0 = 0;
        if (main) r.Scale = "unit";
        if (main) r.Tr("scale", "unit " + F2(k2) + " px/unit");
      }
      for (int i = i0; i < n; i++) {
        var v = rt[i];
        double amx = v[0], amy = v[1], aMx = v[2], aMy = v[3], sdx = v[6], sdy = v[7];
        double w2 = pw * (aMx - amx) + sdx, h2 = ph * (aMy - amy) + sdy;
        if (i == 0) {   // (unit mode) the root: its pivot from its anchors in the canvas
          px = -cw / 2 + cw * (amx + (aMx - amx) * v[8]) + v[4];
          py = -ch / 2 + ch * (amy + (aMy - amy) * v[9]) + v[5];
        } else { px += sx * lp[i, 0]; py += sy * lp[i, 1]; }
        sx *= ls[i, 0]; sy *= ls[i, 1];
        pw = w2; ph = h2;
      }
      double cx = px + sx * (0.5 - rt[n - 1][8]) * pw, cy = py + sy * (0.5 - rt[n - 1][9]) * ph;
      w = Math.Abs(sx * pw) * k2; h = Math.Abs(sy * ph) * k2;
      x = W / 2 + cx * k2 - w / 2; y = H / 2 - cy * k2 - h / 2;
      return true;
    }

    /// <summary>The icon child of the button (UiIcon.Pick over its children, depth 1-2, at most 40 looked at) into r.IX.. (none: IW stays 0).</summary>
    static void IconInto(List<ulong> chain, bool canvas, double W, double H, double U, UiButtonInfo r) {
      var cands = new List<UiIcon.Cand>();
      var todo = new List<KeyValuePair<List<ulong>, int>> { new KeyValuePair<List<ulong>, int>(chain, 0) };
      for (int q = 0; q < todo.Count && cands.Count < 40; q++) {
        var at = todo[q].Key; int depth = todo[q].Value; if (depth >= 2) continue;
        ulong t = at[at.Count - 1];
        var cb = Read(t + TrChildren, 0x18); if (cb == null) continue;
        ulong arr = BitConverter.ToUInt64(cb, 0); long cnt = BitConverter.ToInt64(cb, 0x10);
        if (arr == 0 || cnt <= 0 || cnt > 32) continue;
        for (int i = 0; i < cnt && cands.Count < 40; i++) {
          ulong c = Ptr(arr + (ulong)(8 * i)); if (c == 0 || Ptr(c + TrParent) != t) continue;
          ulong go = Ptr(c + CompGo); if (go == 0) continue;
          var ab = Read(go + GoActiveSelf, 1);
          var path = new List<ulong>(at); path.Add(c);
          var tmp = new UiButtonInfo(); double x, y, w, h;
          if (!ChainRect(path, canvas, W, H, U, tmp, false, out x, out y, out w, out h)) continue;
          cands.Add(new UiIcon.Cand { Name = CStr(Ptr(go + GoName)) ?? "", X = x, Y = y, W = w, H = h, Active = ab != null && ab[0] == 1, Depth = depth + 1 });
          todo.Add(new KeyValuePair<List<ulong>, int>(path, depth + 1));
        }
      }
      int k = UiIcon.Pick(cands, r.X, r.Y, r.W, r.H);
      r.Tr("icon", k < 0 ? "none of " + cands.Count : cands[k].Name + " " + N0(cands[k].X) + "," + N0(cands[k].Y) + "," + N0(cands[k].W) + "," + N0(cands[k].H));
      if (k < 0) return;
      r.IX = cands[k].X; r.IY = cands[k].Y; r.IW = cands[k].W; r.IH = cands[k].H; r.IconName = cands[k].Name;
    }

    static void UiButtonReadInto(ulong env, ulong ui, UiWidget w, double W, double H, double U, UiButtonInfo r) {
      if (env == 0 || ui == 0) { r.Fail("lua root", "not reached"); return; }
      if (W < 50 || H < 50) { r.Fail("window", "no client size " + F2(W) + "x" + F2(H)); return; }
      r.Tr("client", F2(W) + "x" + F2(H) + " U " + F2(U));
      bool shown; int copies; ulong f = FormOf(env, ui, w.Form, r, out shown, out copies);
      if (f == 0) { r.Fail("form", "not in UIInstance"); return; }
      r.FormTable = f; r.Copies = copies;
      // the widget's field: the first path that is there
      ulong ud = 0; int tt = 0; string miss = null;
      foreach (var p in w.Paths) {
        ulong v; string m;
        if (LuaPath(f, p, out v, out tt, out m)) { if (tt == T_UDATA) { ud = v; r.Widget = p; break; } m = p + " tt " + tt; }
        if (miss == null) miss = m;
      }
      if (ud == 0) { r.Fail("field", miss); return; }
      ulong csv; int ctt;
      if (!Field(f, "m_csui", out csv, out ctt) || ctt != T_UDATA) { r.Fail("m_csui", "tt " + ctt); return; }
      string cls, rcls;
      ulong nat = NativeOf(env, ud, "widget", r, out cls); if (nat == 0) return;
      r.ClassName = cls;
      ulong rnat = FormRootOf(env, csv, r, out rcls); if (rnat == 0) return;
      ulong go, tr, rgo, rtr;
      if (!GoAndTransform(nat, cls, "widget", r, out go, out tr)) return;
      if (!GoAndTransform(rnat, rcls, "root", r, out rgo, out rtr)) return;
      // the transforms from the widget up to the form's root
      var chain = new List<ulong>(); ulong t = tr;
      while (t != rtr) {
        if (t == 0 || chain.Count >= 48) { r.Fail("parents", t == 0 ? "not under the form's root" : "too deep"); return; }
        chain.Add(t); t = Ptr(t + TrParent);
      }
      chain.Add(rtr); chain.Reverse();   // root first
      int nForm = chain.Count;
      // the scale: the root canvas's size against the client (any window size, any scaler mode), else Ui.Unit (640 canvas units = U px)
      var above = new List<ulong>();
      bool canvas = CanvasAbove(rtr, W, H, r, above);
      if (!canvas && U <= 0) { r.Fail("window", "no canvas and no unit"); return; }
      if (canvas) chain.InsertRange(0, above);
      int n = chain.Count;
      double rx, ry, rw, rh;
      if (!ChainRect(chain, canvas, W, H, U, r, true, out rx, out ry, out rw, out rh)) return;
      r.X = rx; r.Y = ry; r.W = rw; r.H = rh;
      r.Tr("rect", N0(r.X) + "," + N0(r.Y) + "," + N0(r.W) + "," + N0(r.H) + " chain " + n + " (form " + nForm + ")");
      // shown: the form is shown and the object is active in the hierarchy (the cached flag; not computed yet: every activeSelf up to the root)
      bool active;
      var ab = Read(go + GoActiveSelf, 2); if (ab == null) { r.Fail("active", HexA(go)); return; }
      if (ab[1] == 0 || ab[1] == 1) active = ab[1] == 1;
      else {
        active = true;
        for (int i = n - 1; i >= n - nForm; i--) { ulong g = Ptr(chain[i] + CompGo); var sb = g != 0 ? Read(g + GoActiveSelf, 1) : null; if (sb == null || sb[0] == 0) { active = false; break; } }
      }
      r.Tr("shown", (shown ? "form" : "form-hidden") + (active ? " active" : " inactive") + " copies " + copies);
      r.Visible = shown && active && r.W >= 1 && r.H >= 1;
      if (w.Icon && r.Visible) IconInto(chain, canvas, W, H, U, r);
    }

    static string N0(double v) { return Math.Round(v).ToString(CultureInfo.InvariantCulture); }

    /// <summary>The log line: "uibtn: Form.widget live=[x,y,w,h] layout=[x,y] diff=Npx visible=T in M ms" or "uibtn: Form.widget failed at step (value)".</summary>
    public static string UiButtonLine(UiButtonInfo r, double lx, double ly) {
      string who = r.Form + "." + r.Widget;
      if (!r.Ok) return "uibtn: " + who + " failed at " + r.FailStep + " (" + r.FailValue + ") in " + r.Ms + " ms";
      double cx = r.X + r.W / 2, cy = r.Y + r.H / 2, d = Math.Sqrt((cx - lx) * (cx - lx) + (cy - ly) * (cy - ly));
      bool inside = lx >= r.X && lx < r.X + r.W && ly >= r.Y && ly < r.Y + r.H;
      return "uibtn: " + who + " live=[" + N0(r.X) + "," + N0(r.Y) + "," + N0(r.W) + "," + N0(r.H) + "] layout=[" + N0(lx) + "," + N0(ly) + "] diff=" + N0(d) + "px"
        + (inside ? "" : " outside") + " visible=" + (r.Visible ? "T" : "F") + (r.Rotated ? " rotated" : "") + (r.Copies > 1 ? " copies=" + r.Copies : "")
        + (r.Scale != "" ? " scale=" + r.Scale : "") + (r.IW > 0 ? " icon=" + r.IconName + "[" + N0(r.IX) + "," + N0(r.IY) + "," + N0(r.IW) + "," + N0(r.IH) + "]" : "") + " " + r.ClassName + " in " + r.Ms + " ms";
    }

    // ---- the reads: the clicks and hint frames of the pilot buttons use the live rect when fresh; a minute re-read of the last click (test build log)
    static int ubAt, ubLastAt;
    static ClickTarget ubT; static string ubOffer; static double ubX, ubY, ubW, ubH, ubU;

    /// <summary>XLua.LuaEnv and UIInstance walked from the script root on every read (never a cached copy: the game builds its forms and
    /// tables again, an old UIInstance lingers in memory with destroyed forms).</summary>
    static bool UiRoots(out ulong env, out ulong ui, out string why) {
      why = null;
      var lr = LuaRootWalk(RootGa());
      if (lr.Env == 0 || lr.Ui == 0) { why = lr.FailStep + " (" + lr.FailValue + ")"; env = ui = 0; return false; }
      env = lr.Env; ui = lr.Ui; return true;
    }

    /// <summary>No game to read (and no stand-in memory of the tests).</summary>
    static bool UiNoGame() { return TestMemory == null && (Layout.Current == null || GameInfo.GameProcesses().Length == 0); }

    /// <summary>One read now of the pilot button <paramref name="t"/> (from the script root), kept for the clicks and frames; a failed read is
    /// dumped step by step once a minute (test build). Null = not a button read by name, or no game.</summary>
    public static UiButtonInfo UiButtonReadNow(ClickTarget t, string offerForm, double W, double H, double U) {
      var w = UiWidgetOf(t, offerForm); if (w == null || UiNoGame()) return null;
      var sw = Stopwatch.StartNew(); int at = Environment.TickCount;
      ulong env, ui; string why; UiButtonInfo r;
      if (!UiRoots(out env, out ui, out why)) { r = new UiButtonInfo { Form = w.Form, Widget = w.Paths[0], ForW = W, ForH = H, At = at }; r.Fail("lua root", why); }
      else r = UiButtonRead(env, ui, w, W, H, U);
      r.Ms = sw.ElapsedMilliseconds;
      at = Environment.TickCount; r.At = at;   // (its age from the end of the read: the rect fields are read last)
      UiButtonKeep(t, r, W, H, at);
      UiButtonDiag(r, at);
      return r;
    }

    /// <summary>Remembers the button a pilot clicks (the minute re-read logs it again).</summary>
    public static void UiButtonClicked(ClickTarget t, string offerForm, double x, double y, double W, double H, double U) {
      if (UiWidgetOf(t, offerForm) == null) return;
      ubT = t; ubOffer = offerForm; ubX = x; ubY = y; ubW = W; ubH = H; ubU = U;
      ubLastAt = Environment.TickCount; if (ubLastAt == 0) ubLastAt = 1;
      ubAt = ubLastAt;
    }

    static int ubBusy;
    /// <summary>Test build: once a minute, the last button a pilot clicked (within 10 minutes) read again and logged.</summary>
    public static void UiButtonShadowTick() {
      if (!Channel.IsTest || ubLastAt == 0) return;
      int now = Environment.TickCount;
      if (unchecked(now - ubLastAt) > 600000) { ubLastAt = 0; return; }
      if (ubAt != 0 && unchecked(now - ubAt) < 60000) return;
      if (Interlocked.CompareExchange(ref ubBusy, 1, 0) != 0) return;
      ubAt = now; if (ubAt == 0) ubAt = 1;
      var t = ubT; string offer = ubOffer; double x = ubX, y = ubY, W = ubW, H = ubH, U = ubU;
      try {
        Task.Factory.StartNew(() => {
          try { var r = UiButtonReadNow(t, offer, W, H, U); if (r != null) LL(UiButtonLine(r, x, y) + " [minute]"); }
          catch (Exception e) { LL("uibtn: failed at exception (" + e.Message + ")"); }
          finally { Interlocked.Exchange(ref ubBusy, 0); }
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
      } catch (Exception) { Interlocked.Exchange(ref ubBusy, 0); }
    }

    // ---- the diagnostic dump (test build): one failing read a minute, every step's addresses and values
    static int ubDiagAt;
    public const int UiDiagMs = 60000;
    /// <summary>The dump line of a read that failed or is not visible ("uibtn diag: ..."), null = none due (one a minute, test build).</summary>
    public static string UiButtonDiagLine(UiButtonInfo r, int now) {
      if (r == null || (r.Ok && r.Visible)) return null;
      if (ubDiagAt != 0 && unchecked(now - ubDiagAt) < UiDiagMs && unchecked(now - ubDiagAt) >= 0) return null;
      ubDiagAt = now == 0 ? 1 : now;
      return "uibtn diag: " + r.Form + "." + r.Widget + " " + (r.Ok ? "not visible" : "failed at " + r.FailStep + " (" + r.FailValue + ")") + ": " + r.Trace.TrimEnd();
    }
    static void UiButtonDiag(UiButtonInfo r, int now) { if (!Channel.IsTest) return; var s = UiButtonDiagLine(r, now); if (s != null) LL(s); }
    /// <summary>For the tests: the dump's minute starts over.</summary>
    public static void UiDiagReset() { ubDiagAt = 0; }

    // ---- the kept reads: the last read of each pilot button (a good one, or the failure that replaced it)
    sealed class LiveRect { public ClickTarget T; public UiButtonInfo R; public double W, H; public int At; }
    static readonly Dictionary<ClickTarget, LiveRect> ubLive = new Dictionary<ClickTarget, LiveRect>();
    /// <summary>How long a live rect is taken as the button's place for the plates to keep clear of.</summary>
    public const int UiLiveMs = 90000;
    /// <summary>How old a live rect may be to be clicked or framed (else the layout's place).</summary>
    public const int UiFreshMs = 200;

    public static void UiButtonKeep(ClickTarget t, UiButtonInfo r, double W, double H, int at) {
      if (r == null) return;
      bool good = r.Ok && r.Visible && !r.Rotated && r.W > 0 && r.H > 0;
      lock (ubLive) {
        if (good) ubLive[t] = new LiveRect { T = t, R = r, W = W, H = H, At = at };
        else ubLive.Remove(t);   // a failed or hidden read: the button's place is not known now
      }
    }

    static LiveRect LiveOf(ClickTarget t) { lock (ubLive) { LiveRect l; return ubLive.TryGetValue(t, out l) ? l : null; } }

    /// <summary>The button's live rect {x, y, w, h} (client px) read lately for a window of this size (uibtn), or null: the layout's then.</summary>
    public static double[] UiButtonLive(ClickTarget t, double W, double H, int now) {
      var l = LiveOf(t);
      if (l == null || Math.Abs(l.W - W) > 0.5 || Math.Abs(l.H - H) > 0.5 || unchecked(now - l.At) > UiLiveMs || unchecked(now - l.At) < 0) return null;
      return new[] { l.R.X, l.R.Y, l.R.W, l.R.H };
    }

    /// <summary>The kept read of the button if it is fresh (at most <paramref name="maxMs"/> old), for this window size and (OfferClose) this pop-up; else null.</summary>
    public static UiButtonInfo UiButtonFresh(ClickTarget t, string offerForm, double W, double H, int now, int maxMs) {
      var l = LiveOf(t); var w = UiWidgetOf(t, offerForm);
      if (l == null || w == null || l.R.Form != w.Form || Math.Abs(l.W - W) > 0.5 || Math.Abs(l.H - H) > 0.5) return null;
      int age = unchecked(now - l.At);
      return age >= 0 && age <= maxMs ? l.R : null;
    }

    // ---- the background reads while a frame or a click of a pilot button is near (at most one at a time, each button every 100 ms)
    static int uwBusy; static readonly Dictionary<ClickTarget, int> uwTried = new Dictionary<ClickTarget, int>();
    public const int UiWantMs = 100;

    /// <summary>A pilot button's frame is shown or its click is near: it is read again in the background when its kept read is 100 ms old.</summary>
    public static void UiButtonWant(ClickTarget t, string offerForm, double W, double H, double U) {
      if (UiWidgetOf(t, offerForm) == null) return;
      int now = Environment.TickCount;
      lock (uwTried) { int at; if (uwTried.TryGetValue(t, out at) && unchecked(now - at) >= 0 && unchecked(now - at) < UiWantMs) return; uwTried[t] = now; }
      if (Interlocked.CompareExchange(ref uwBusy, 1, 0) != 0) return;
      try {
        Task.Factory.StartNew(() => {
          try { UiButtonReadNow(t, offerForm, W, H, U); }
          catch (Exception e) { LL("uibtn: failed at exception (" + e.Message + ")"); }
          finally { Interlocked.Exchange(ref uwBusy, 0); }
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
      } catch (Exception) { Interlocked.Exchange(ref uwBusy, 0); }
    }

    /// <summary>Is the form the button is read on the one on top of the game now (the screen state's top by name; «Заменить» is the pilot's own
    /// flow form over the hero screen)? A covering screen or pop-up = no live click there.</summary>
    public static bool UiTopIs(ClickTarget t, string offerForm, ScreenState s) {
      var w = UiWidgetOf(t, offerForm);
      if (w == null || s == null || s.Held) return false;
      if (t == ClickTarget.EquipConfirm) return s.Kind == ScreenKind.HeroScreen && s.OfferCount == 0 && Array.IndexOf((s.Flow ?? "").Split(','), w.Form) >= 0;
      if (t == ClickTarget.OfferClose) return s.OfferCount > 0 && s.Offer == w.Form;
      return s.Top == w.Form && s.OfferCount == 0 && !s.ModalOpen;
    }

    /// <summary>The click point of a pilot button from its live read: the rect's centre when the read is ok, fresh (at most UiFreshMs old at
    /// <paramref name="now"/>), visible, unrotated, the form's one shown copy and the centre inside the client; else false and why (the layout's point then).</summary>
    public static bool UiLivePoint(UiButtonInfo r, double W, double H, int now, out double x, out double y, out string why) {
      x = y = 0; why = null;
      if (r == null) { why = "no read"; return false; }
      if (!r.Ok) { why = "failed at " + r.FailStep; return false; }
      int age = unchecked(now - r.At);
      if (age < 0 || age > UiFreshMs) { why = "old (" + age + " ms)"; return false; }
      if (Math.Abs(r.ForW - W) > 0.5 || Math.Abs(r.ForH - H) > 0.5) { why = "another window size"; return false; }
      if (!r.Visible) { why = "not visible"; return false; }
      if (r.Rotated) { why = "rotated"; return false; }
      if (r.Copies != 1) { why = r.Copies + " shown copies of the form"; return false; }
      double cx = r.X + r.W / 2, cy = r.Y + r.H / 2;
      if (cx < 1 || cy < 1 || cx > W - 1 || cy > H - 1) { why = "centre outside the client"; return false; }
      if (r.W > W * 0.9 || r.H > H * 0.9) { why = "larger than the window"; return false; }
      x = cx; y = cy; return true;
    }

    /// <summary>For the tests: one read now with the roots given (no task), and its log line.</summary>
    public static UiButtonInfo UiButtonForTest(ulong env, ulong ui, UiWidget w, double x, double y, double W, double H, double U, out string line) {
      var r = UiButtonRead(env, ui, w, W, H, U);
      line = UiButtonLine(r, x, y);
      return r;
    }
  }
}
