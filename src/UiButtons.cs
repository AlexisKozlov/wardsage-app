// Wardsage — a button's place on the screen read from the running game by its name (read-only, test build: shadow log only).
//
// The pilots click points the site's geometry computes from the prefabs (tools/ui-geometry.py). This reads where the game
// itself has put the button now and logs both (uibtn: ...), so the two can be compared on live runs before anything clicks by it.
//
// The chain (no scan; the Lua root of src/LuaRoot.cs gives XLua.LuaEnv and UIInstance):
//   UIInstance -> the form (class name) -> its Lua fields down the path (UIBase:Init binds the prefab's m_* objects into the
//   form table, UILuaHelper.BindViewObjects) -> a full userdata (tt 0x47; Lua 5.3 Udata: len 0x18, data 0x28 = xLua's int
//   object index) -> LuaEnv.translator 0x20 -> ObjectTranslator.objects 0xC8 (XLua.ObjectPool) -> list 0x10 (Slot[]:
//   il2cpp array, length 0x18, data 0x20, Slot {int next; object obj 0x8}, 16 bytes) -> the managed UnityEngine.Object
//   -> m_CachedPtr 0x10 -> the native object.
// Native (UnityPlayer.dll of Unity 2019.4.40f1, read from its icalls' code): Component.m_GameObject 0x30; GameObject
// components 0x30 (16-byte pairs, the component at 0x8; the Transform first), count 0x40, activeSelf 0x56, activeInHierarchy
// cached 0x57 (0xFF = not computed yet); Transform hierarchy 0x38, index 0x40, parent 0x90; TransformHierarchy local TRS
// array 0x18 (48 bytes each: position 0x0, rotation 0x10, scale 0x20); RectTransform anchorMin 0xC8, anchorMax 0xD0,
// anchoredPosition 0xD8, sizeDelta 0xE0, pivot 0xE8.
// The rect: the widget's transforms up to the form's root (the transform of the form's C# UIBase, m_csui), each one's local
// position / scale from the hierarchy and its rect from its anchors and sizeDelta, the root's rect from the canvas
// (reference 640 units high, U pixels = 640 units: Ui.Unit). Rotation is assumed none (flagged when not).
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
    public bool Ok { get { return FailStep == null; } }
    internal UiButtonInfo Fail(string step, string value) { FailStep = step; FailValue = value; return this; }
  }

  /// <summary>A button by name: the form's class and the Lua field paths that lead to it (the first that is there is taken).</summary>
  public sealed class UiWidget {
    public readonly string Form; public readonly string[] Paths;
    public UiWidget(string form, params string[] paths) { Form = form; Paths = paths; }
  }

  public static partial class RFX {
    const int T_UDATA = 0x47;
    // XLua / il2cpp
    const int LuaEnvTranslator = 0x20, TranslatorObjects = 0xC8, PoolList = 0x10, ArrLen = 0x18, ArrData = 0x20, CachedPtr = 0x10;
    // UnityPlayer 2019.4.40f1
    const int CompGo = 0x30, GoComps = 0x30, GoCompCount = 0x40, GoActiveSelf = 0x56, GoActiveCached = 0x57;
    const int TrHier = 0x38, TrIndex = 0x40, TrParent = 0x90, HierLocal = 0x18, TrsSize = 48, RtFields = 0xC8;
    public const double CanvasRefH = 640;

    /// <summary>The close buttons of the offer pop-ups (src/OfferForms.cs) and the back arrow of a full screen.</summary>
    static readonly string[] OfferClosePaths = { "m_BtnClose", "m_ButtonClose", "m_CloseBtn", "m_Button_Close", "m_BtnCloseShow", "Button_Close", "m_ButtonBack.m_btnBack" };

    /// <summary>The button a pilot's click target is, by name (null = not one the shadow reads). <paramref name="offerForm"/> = the
    /// pop-up an OfferClose closes.</summary>
    public static UiWidget UiWidgetOf(ClickTarget t, string offerForm) {
      switch (t) {
        case ClickTarget.HeroBack: return new UiWidget("Form_CharactorMain", "m_ButtonBack.m_btnBack");
        case ClickTarget.CityInventory: return new UiWidget("Form_MainCity", "m_Button_Backpack");
        case ClickTarget.FoundryButton: return new UiWidget("Form_BackpackIntegration", "m_PanelContentCur.m_BtnWorkShop");
        case ClickTarget.AscendTab: return new UiWidget("Form_EquipSubliming", "m_ToggleSubliming");
        case ClickTarget.EquipConfirm: return new UiWidget("Form_CharactorEquipTip", "m_SelectEquipView.m_RightBtn.m_gameObject", "m_OneEquipView.m_RightBtn.m_gameObject");
        case ClickTarget.OfferClose: return string.IsNullOrEmpty(offerForm) ? null : new UiWidget(offerForm, OfferClosePaths);
      }
      return null;
    }

    static int I32(byte[] b, int o) { return BitConverter.ToInt32(b, o); }
    static float F32(byte[] b, int o) { return BitConverter.ToSingle(b, o); }

    /// <summary>The native object of a Lua field holding a C# object (0 = none; the step in <paramref name="r"/>), its managed class in <paramref name="cls"/>.</summary>
    static ulong NativeOf(ulong env, ulong ud, string what, UiButtonInfo r, out string cls) {
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
      ulong nat = Ptr(obj + CachedPtr);
      if (nat == 0) { r.Fail("m_CachedPtr " + what, cls + " destroyed"); return 0; }
      return nat;
    }

    /// <summary>The GameObject and Transform of a native object (a GameObject or a Component); false = the step in <paramref name="r"/>.</summary>
    static bool GoAndTransform(ulong nat, string cls, string what, UiButtonInfo r, out ulong go, out ulong tr) {
      go = cls == "UnityEngine.GameObject" ? nat : Ptr(nat + CompGo); tr = 0;
      var gb = go != 0 ? Read(go + GoComps, 0x18) : null;
      if (gb == null) { r.Fail("gameObject " + what, HexA(go)); return false; }
      ulong comps = BitConverter.ToUInt64(gb, 0); long cnt = BitConverter.ToInt64(gb, 0x10);
      if (comps == 0 || cnt < 1 || cnt > 256) { r.Fail("components " + what, cnt.ToString()); return false; }
      tr = Ptr(comps + 8);
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

    /// <summary>The form of class <paramref name="cls"/> in the window table: the shown one whose C# side is alive first, then any alive one,
    /// then a shown one, then any (0 = none). The game builds a form again (the city after the guild): a destroyed copy is never preferred.</summary>
    static ulong FormOf(ulong env, ulong ui, string cls, out bool shown) {
      shown = false;
      var h = Read(ui, 56); if (h == null || h[8] != 5 || h[11] > 12) return 0;
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return 0;
      ulong best = 0; int bestRank = -1;
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (I32(nb, o + 24) != T_INT || I32(nb, o + 8) != T_TABLE) continue;
        ulong f = BitConverter.ToUInt64(nb, o);
        string why;
        if (FormClassName(f, out why) != cls) continue;
        bool sh = Shown(f), alive = CsuiAlive(env, f);
        int rank = (alive ? 2 : 0) + (sh ? 1 : 0);
        if (rank > bestRank) { best = f; bestRank = rank; shown = sh; }
        if (rank == 3) break;
      }
      return best;
    }

    /// <summary>Is the form's C# UIBase (m_csui) still a live Unity object (its m_CachedPtr set)?</summary>
    static bool CsuiAlive(ulong env, ulong f) {
      ulong csv; int ctt;
      if (env == 0 || !Field(f, "m_csui", out csv, out ctt) || ctt != T_UDATA) return false;
      var probe = new UiButtonInfo(); string cls;
      return NativeOf(env, csv, "m_csui", probe, out cls) != 0;
    }

    /// <summary>The live rect of a button by name, from <paramref name="env"/> (XLua.LuaEnv) and <paramref name="ui"/> (UIInstance),
    /// for a game window of client size W x H px and UI unit U px.</summary>
    public static UiButtonInfo UiButtonRead(ulong env, ulong ui, UiWidget w, double W, double H, double U) {
      var sw = Stopwatch.StartNew();
      var r = new UiButtonInfo { Form = w.Form, Widget = w.Paths.Length > 0 ? w.Paths[0] : "" };
      try { UiButtonReadInto(env, ui, w, W, H, U, r); }
      catch (Exception e) { r.Fail("exception", e.Message); }
      finally { r.Ms = sw.ElapsedMilliseconds; }
      return r;
    }

    static void UiButtonReadInto(ulong env, ulong ui, UiWidget w, double W, double H, double U, UiButtonInfo r) {
      if (env == 0 || ui == 0) { r.Fail("lua root", "not reached"); return; }
      if (U <= 0) { r.Fail("window", "no size"); return; }
      bool shown; ulong f = FormOf(env, ui, w.Form, out shown);
      if (f == 0) { r.Fail("form", "not in UIInstance"); return; }
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
      ulong rnat = NativeOf(env, csv, "m_csui", r, out rcls); if (rnat == 0) return;
      ulong go, tr, rgo, rtr;
      if (!GoAndTransform(nat, cls, "widget", r, out go, out tr)) return;
      if (!GoAndTransform(rnat, rcls, "m_csui", r, out rgo, out rtr)) return;
      // the transforms from the widget up to the form's root
      var chain = new List<ulong>(); ulong t = tr;
      while (t != rtr) {
        if (t == 0 || chain.Count >= 48) { r.Fail("parents", t == 0 ? "not under the form's root" : "too deep"); return; }
        chain.Add(t); t = Ptr(t + TrParent);
      }
      chain.Add(rtr); chain.Reverse();   // root first
      int n = chain.Count;
      var lp = new double[n, 2]; var ls = new double[n, 2]; var rt = new double[n, 10];
      for (int i = 0; i < n; i++) {
        var hb = Read(chain[i] + TrHier, 12); if (hb == null) { r.Fail("hierarchy", HexA(chain[i])); return; }
        ulong hier = BitConverter.ToUInt64(hb, 0); int ix = I32(hb, 8);
        ulong arr = hier != 0 ? Ptr(hier + HierLocal) : 0;
        var trs = arr != 0 && ix >= 0 && ix < 100000 ? Read(arr + (ulong)ix * TrsSize, TrsSize) : null;
        if (trs == null) { r.Fail("local transform", HexA(hier) + "[" + ix + "]"); return; }
        lp[i, 0] = F32(trs, 0); lp[i, 1] = F32(trs, 4);
        ls[i, 0] = F32(trs, 0x20); ls[i, 1] = F32(trs, 0x24);
        double qx = F32(trs, 0x10), qy = F32(trs, 0x14), qz = F32(trs, 0x18), qw = F32(trs, 0x1C);
        if (Math.Abs(qx) > 1e-3 || Math.Abs(qy) > 1e-3 || Math.Abs(qz) > 1e-3 || Math.Abs(Math.Abs(qw) - 1) > 1e-3) r.Rotated = true;
        var fb = Read(chain[i] + RtFields, 40); if (fb == null) { r.Fail("rect fields", HexA(chain[i])); return; }
        for (int k = 0; k < 10; k++) rt[i, k] = F32(fb, 4 * k);
        // anchors and pivot are fractions; a wild value = not a RectTransform (or another layout)
        for (int k = 0; k < 10; k++)
          if (double.IsNaN(rt[i, k]) || double.IsInfinity(rt[i, k]) || ((k < 4 || k >= 8) && (rt[i, k] < -0.01 || rt[i, k] > 1.01)) || Math.Abs(rt[i, k]) > 1e5) {
            r.Fail("rect fields", "#" + i + " value " + k + " = " + rt[i, k].ToString("R", CultureInfo.InvariantCulture)); return;
          }
      }
      // the root's rect in the canvas (canvas centre = origin, y up), then down the chain
      double k2 = U / CanvasRefH, cw = W / k2, ch = H / k2;
      double pw = cw, ph = ch, sx = 1, sy = 1, px = 0, py = 0;
      for (int i = 0; i < n; i++) {
        double amx = rt[i, 0], amy = rt[i, 1], aMx = rt[i, 2], aMy = rt[i, 3], sdx = rt[i, 6], sdy = rt[i, 7];
        double w2 = pw * (aMx - amx) + sdx, h2 = ph * (aMy - amy) + sdy;
        if (i == 0) {   // the root: its pivot from its anchors in the canvas (its parent is assumed to fill the canvas)
          px = -cw / 2 + cw * (amx + (aMx - amx) * rt[i, 8]) + rt[i, 4];
          py = -ch / 2 + ch * (amy + (aMy - amy) * rt[i, 9]) + rt[i, 5];
        } else { px += sx * lp[i, 0]; py += sy * lp[i, 1]; }
        sx *= ls[i, 0]; sy *= ls[i, 1];
        pw = w2; ph = h2;
      }
      double cx = px + sx * (0.5 - rt[n - 1, 8]) * pw, cy = py + sy * (0.5 - rt[n - 1, 9]) * ph;
      r.W = Math.Abs(sx * pw) * k2; r.H = Math.Abs(sy * ph) * k2;
      r.X = W / 2 + cx * k2 - r.W / 2; r.Y = H / 2 - cy * k2 - r.H / 2;
      // shown: the form is shown and the object is active in the hierarchy (the cached flag; not computed yet: every activeSelf up to the root)
      bool active;
      var ab = Read(go + GoActiveSelf, 2); if (ab == null) { r.Fail("active", HexA(go)); return; }
      if (ab[1] == 0 || ab[1] == 1) active = ab[1] == 1;
      else {
        active = true;
        foreach (var c in chain) { ulong g = Ptr(c + CompGo); var sb = g != 0 ? Read(g + GoActiveSelf, 1) : null; if (sb == null || sb[0] == 0) { active = false; break; } }
      }
      r.Visible = shown && active && r.W >= 1 && r.H >= 1;
    }

    static string N0(double v) { return Math.Round(v).ToString(CultureInfo.InvariantCulture); }

    /// <summary>The log line: "uibtn: Form.widget live=[x,y,w,h] layout=[x,y] diff=Npx visible=T in M ms" or "uibtn: Form.widget failed at step (value)".</summary>
    public static string UiButtonLine(UiButtonInfo r, double lx, double ly) {
      string who = r.Form + "." + r.Widget;
      if (!r.Ok) return "uibtn: " + who + " failed at " + r.FailStep + " (" + r.FailValue + ") in " + r.Ms + " ms";
      double cx = r.X + r.W / 2, cy = r.Y + r.H / 2, d = Math.Sqrt((cx - lx) * (cx - lx) + (cy - ly) * (cy - ly));
      bool inside = lx >= r.X && lx < r.X + r.W && ly >= r.Y && ly < r.Y + r.H;
      return "uibtn: " + who + " live=[" + N0(r.X) + "," + N0(r.Y) + "," + N0(r.W) + "," + N0(r.H) + "] layout=[" + N0(lx) + "," + N0(ly) + "] diff=" + N0(d) + "px"
        + (inside ? "" : " outside") + " visible=" + (r.Visible ? "T" : "F") + (r.Rotated ? " rotated" : "") + " " + r.ClassName + " in " + r.Ms + " ms";
    }

    // ---- the shadow run (test build): before each pilot click on one of these buttons and once a minute for the last one; changes nothing
    static int ubBusy, ubAt, ubLastAt;
    static ClickTarget ubT; static string ubOffer; static double ubX, ubY, ubW, ubH, ubU;

    /// <summary>XLua.LuaEnv and UIInstance walked from the script root on every read (never a cached copy: the game builds its forms and
    /// tables again, an old UIInstance lingers in memory with destroyed forms).</summary>
    static bool UiRoots(out ulong env, out ulong ui, out string why) {
      why = null;
      var lr = LuaRootWalk(RootGa());
      if (lr.Env == 0 || lr.Ui == 0) { why = lr.FailStep + " (" + lr.FailValue + ")"; env = ui = 0; return false; }
      env = lr.Env; ui = lr.Ui; return true;
    }

    /// <summary>Test build: the live rect of the button a pilot is about to click (<paramref name="x"/>, <paramref name="y"/> = its click
    /// point, client px), read in the background and logged next to that point.</summary>
    public static void UiButtonShadow(ClickTarget t, string offerForm, double x, double y, double W, double H, double U, string why) {
      if (!Channel.IsTest || UiWidgetOf(t, offerForm) == null) return;
      ubT = t; ubOffer = offerForm; ubX = x; ubY = y; ubW = W; ubH = H; ubU = U;
      ubLastAt = Environment.TickCount; if (ubLastAt == 0) ubLastAt = 1;
      UiButtonShadowRun(why);
    }

    /// <summary>Test build: once a minute, the last button a pilot clicked (within 10 minutes) read again.</summary>
    public static void UiButtonShadowTick() {
      if (!Channel.IsTest || ubLastAt == 0) return;
      int now = Environment.TickCount;
      if (unchecked(now - ubLastAt) > 600000) { ubLastAt = 0; return; }
      if (ubAt != 0 && unchecked(now - ubAt) < 60000) return;
      UiButtonShadowRun("minute");
    }

    static void UiButtonShadowRun(string why) {
      if (Interlocked.CompareExchange(ref ubBusy, 1, 0) != 0) return;
      ubAt = Environment.TickCount; if (ubAt == 0) ubAt = 1;
      var t = ubT; var w = UiWidgetOf(ubT, ubOffer); double x = ubX, y = ubY, W = ubW, H = ubH, U = ubU;
      try {
        Task.Factory.StartNew(() => {
          try {
            if (TestMemory == null && (Layout.Current == null || GameInfo.GameProcesses().Length == 0)) return;
            LL(UiButtonLineNow(t, w, x, y, W, H, U) + " [" + why + "]");
          } catch (Exception e) { LL("uibtn: failed at exception (" + e.Message + ")"); }
          finally { Interlocked.Exchange(ref ubBusy, 0); }
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
      } catch (Exception) { Interlocked.Exchange(ref ubBusy, 0); }
    }

    static string UiButtonLineNow(ClickTarget t, UiWidget w, double x, double y, double W, double H, double U) {
      var sw = Stopwatch.StartNew();
      ulong env, ui; string why;
      if (!UiRoots(out env, out ui, out why)) return "uibtn: " + w.Form + "." + w.Paths[0] + " failed at lua root (" + why + ") in " + sw.ElapsedMilliseconds + " ms";
      var r = UiButtonRead(env, ui, w, W, H, U);
      r.Ms = sw.ElapsedMilliseconds;
      UiButtonKeep(t, r, W, H, Environment.TickCount);
      return UiButtonLine(r, x, y);
    }

    // the last good live rect (a shown, unrotated button), for the hints' plates to keep clear of (never for a click)
    sealed class LiveRect { public ClickTarget T; public double W, H, X, Y, RW, RH; public int At; }
    static LiveRect ubLive;
    /// <summary>How long a live rect is taken as the button's place for the plates.</summary>
    public const int UiLiveMs = 90000;

    public static void UiButtonKeep(ClickTarget t, UiButtonInfo r, double W, double H, int at) {
      if (r == null || !r.Ok || !r.Visible || r.Rotated || r.W <= 0 || r.H <= 0) return;
      ubLive = new LiveRect { T = t, W = W, H = H, X = r.X, Y = r.Y, RW = r.W, RH = r.H, At = at };
    }

    /// <summary>The button's live rect {x, y, w, h} (client px) read lately for a window of this size (uibtn), or null: the layout's then.</summary>
    public static double[] UiButtonLive(ClickTarget t, double W, double H, int now) {
      var l = ubLive;
      if (l == null || l.T != t || Math.Abs(l.W - W) > 0.5 || Math.Abs(l.H - H) > 0.5 || unchecked(now - l.At) > UiLiveMs || unchecked(now - l.At) < 0) return null;
      return new[] { l.X, l.Y, l.RW, l.RH };
    }

    /// <summary>For the tests: one read now with the roots given (no task), and its log line.</summary>
    public static UiButtonInfo UiButtonForTest(ulong env, ulong ui, UiWidget w, double x, double y, double W, double H, double U, out string line) {
      var r = UiButtonRead(env, ui, w, W, H, U);
      line = UiButtonLine(r, x, y);
      return r;
    }
  }
}
