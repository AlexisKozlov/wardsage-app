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
    /// <summary>The Image that draws the framed picture (the icon when IW &gt; 0, else the widget itself): its sprite's name ("" = not read,
    /// <see cref="ArtWhy"/> says why) and how it is drawn (src/SpriteArt.cs).</summary>
    public string Sprite = "", ArtWhy = ""; public SpriteArt.Draw Art;
    /// <summary>The last ChainRect's pivot (y up), client px per local unit and flips of its last transform (the Image's drawing).</summary>
    internal double CPivX = 0.5, CPivY = 0.5, CPx; internal bool CFlipX, CFlipY;
    public bool Ok { get { return FailStep == null; } }
    internal UiButtonInfo Fail(string step, string value) { FailStep = step; FailValue = value; return this; }
  }

  /// <summary>A button by name: the form's class and the Lua field paths that lead to it (the first that is there is taken).</summary>
  public sealed class UiWidget {
    public readonly string Form; public readonly string[] Paths;
    /// <summary>A bar button with a picture and a label under it: its frame hugs the picture (the icon child), not the whole button.</summary>
    public bool Icon;
    /// <summary>The picture child by its exact name (the city bar's Image_Icon: wider than the hit area, so the shape rules of
    /// <see cref="UiIcon.Pick"/> take it for a backdrop); null = picked by <see cref="UiIcon.Pick"/>.</summary>
    public string IconChild;
    /// <summary>No field of <see cref="Paths"/> there: the close button is looked for live among the form's objects (UiCloseFind), cached per form.</summary>
    public bool Discover;
    /// <summary>Several instances of the button (a compare view's two tips): every path is read and the best one is taken (<see cref="RFX.UiPickScore"/>:
    /// shown, inside the client, in its lower half, the right-hand one) and kept while it stays shown - hovering never re-picks.</summary>
    public bool Pick;
    /// <summary>No field of <see cref="Paths"/> there: the object by its place under the form's root (src/FrameTargets.cs), alternatives tried in
    /// turn: names between '/', "(Clone)" ignored, "#n" = the n-th active child; a name not under its parent is looked for deeper (a panel the
    /// form makes in code). The first found active wins.</summary>
    public string[] Trees;
    public UiWidget(string form, params string[] paths) { Form = form; Paths = paths; }
  }

  /// <summary>The icon of a bar button among its children (depth 1-2): active, not a label / red dot / effect by its name, centred on the button,
  /// near square; a name with icon / img / image the first, then the biggest. And its frame: the icon's rect outset by FramePad px with corners
  /// rounded to its size, kept inside the client. Pure (tests/CoreTests.cs).</summary>
  public static class UiIcon {
    public sealed class Cand { public string Name = ""; public double X, Y, W, H; public bool Active; public int Depth; internal ulong Go; internal UiButtonInfo Rect; }
    public const double FramePad = 2;
    static readonly string[] NotIcon = { "text", "txt", "label", "title", "name", "red", "dot", "num", "count", "tips", "effect", "fx", "lock", "new", "mask", "glow", "light", "arrow", "hitarea", "hit_area", "raycast" };   // (BtnHitArea: the touch area, not the picture)
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
    /// <summary>A button with no icon (a wide one: «Заменить», a tab): its visual child - the biggest active picture among its children that is
    /// not a label / dot / effect by its name, its centre on the button, at least a third of the button's area - else -1 (the button's rect).
    /// The hit area of such a button is often larger than what is drawn. Pure (tests/CoreTests.cs).</summary>
    public static int PickVisual(IList<Cand> c, double bx, double by, double bw, double bh) {
      int best = -1; double ba = 0;
      if (c == null || bw <= 0 || bh <= 0) return -1;
      for (int i = 0; i < c.Count; i++) {
        var k = c[i]; string n = (k.Name ?? "").ToLowerInvariant();
        if (!k.Active || k.W < 4 || k.H < 4) continue;
        bool bad = false; foreach (var t in NotIcon) if (n.Contains(t)) { bad = true; break; }
        if (bad) continue;
        double cx = k.X + k.W / 2, cy = k.Y + k.H / 2;
        if (cx < bx || cx > bx + bw || cy < by || cy > by + bh) continue;
        if (k.W * k.H < bw * bh / 3 || k.W > bw * 1.05 || k.H > bh * 1.05) continue;
        if (k.W * k.H > ba) { ba = k.W * k.H; best = i; }
      }
      return best;
    }
    /// <summary>The test build's line for a frame drawn round a pilot button: "frame: Form.widget rect=[x,y,w,h] source=live|layout".</summary>
    public static string FrameLine(string who, double[] f, bool live) {
      return "frame: " + who + " rect=[" + Math.Round(f[0]) + "," + Math.Round(f[1]) + "," + Math.Round(f[2]) + "," + Math.Round(f[3]) + "] source=" + (live ? "live" : "layout");
    }
    /// <summary>The px per canvas unit of a screen-space root canvas <paramref name="cw"/> x <paramref name="ch"/> units in a W x H client: the
    /// canvas is sized to the client over its scale factor, so width and height give the same scale (the small 526x296 client of 1.6.66.397:
    /// a canvas of about 1138x640 units, 0.46 px a unit both ways - the 37 px between the live rect and the layout's place came from the layout's bar offset,
    /// not from the scale). <paramref name="byH"/> = the scale by the height (the check).</summary>
    public static double CanvasScale(double W, double H, double cw, double ch, out double byH) {
      byH = ch > 0 ? H / ch : 0;
      return cw > 0 ? W / cw : 0;
    }
    /// <summary>The city bar button's dark round plate is no child of its own: it is drawn by the button's Image_Icon sprite (the plate with
    /// a pointed tip below and side lines, 108 x 88 canvas units for «Инвентарь»; its other children are the hit area, the label and the
    /// red dot). The plate's visible edge (the sprite's alpha 0.2, its soft silver rim included) fitted with a circle: centre (0.495, 0.447)
    /// of the rect, diameter 0.8175 of its height (72 units: 33 px on the 526x296 client, live 1.6.66.400 about 34).</summary>
    public const double RimCX = 0.495, RimCY = 0.447, RimD = 0.8175;
    /// <summary>The index of the child named <paramref name="name"/> (active, not tiny); -1 = none or no name.</summary>
    public static int Named(IList<Cand> c, string name) {
      if (c == null || string.IsNullOrEmpty(name)) return -1;
      for (int i = 0; i < c.Count; i++) if (c[i].Active && c[i].W >= 4 && c[i].H >= 4 && c[i].Name == name) return i;
      return -1;
    }
    /// <summary>The plate circle {cx, cy, d} of a city bar button from its Image_Icon's rect.</summary>
    public static double[] BarPlate(double x, double y, double w, double h) { return new[] { x + RimCX * w, y + RimCY * h, RimD * h }; }
    /// <summary>The frame round a city bar button's rim from its Image_Icon's live rect (x, y, w, h): the square round the rim circle,
    /// FramePad px out (x, y, w, h, radius = half its side), within W x H. The tip, the side lines and the label are left out.</summary>
    public static double[] BarRimFrame(double x, double y, double w, double h, double W, double H) {
      var pl = BarPlate(x, y, w, h); double cx = pl[0], cy = pl[1], r = pl[2] / 2 + FramePad;
      double l = Math.Max(0, cx - r), t = Math.Max(0, cy - r), rr = Math.Min(W, cx + r), b = Math.Min(H, cy + r);
      return new[] { l, t, rr - l, b - t, Math.Round(Math.Min(rr - l, b - t) / 2) };
    }
    /// <summary>The frame round the icon (x, y, w, h, corner radius), client px, within W x H.</summary>
    public static double[] Frame(double x, double y, double w, double h, double W, double H) {
      double l = Math.Max(0, x - FramePad), t = Math.Max(0, y - FramePad), r = Math.Min(W, x + w + FramePad), b = Math.Min(H, y + h + FramePad);
      return new[] { l, t, r - l, b - t, Math.Round(Math.Min(r - l, b - t) * 0.18) };
    }
  }

  /// <summary>One frame source per pilot button: once its live rect was read, the frame stays on the last live rect (and follows each new one) while
  /// the reads miss for up to <see cref="HoldMs"/>; only then the layout's place. A read that says the button is gone (hidden, another form on
  /// top) drops it at once. Never live and layout tick by tick (live 1.6.66.397: the city's «Инвентарь» frame jumped 36 px each tick). Pure.</summary>
  /// <summary>A frame's drawn rect follows its live rect only on a real move: while every value stays within <see cref="TolPx"/> of the one drawn,
  /// the drawn one is kept (the first live rect is drawn as it is). Pure (tests/CoreTests.cs).</summary>
  public sealed class FrameSteady {
    public const double TolPx = 3;
    double[] drawn;
    public double[] Pick(double[] live) {
      if (live == null) { drawn = null; return null; }
      if (drawn != null && drawn.Length == live.Length) {
        bool same = true; for (int i = 0; i < live.Length && i < 4; i++) if (Math.Abs(live[i] - drawn[i]) > TolPx) same = false;
        if (same) return drawn;
      }
      drawn = (double[])live.Clone(); return drawn;
    }
  }

  public sealed class FrameStick {
    public const int HoldMs = 60000;   // (a held frame goes only when its target is gone / covered / another step; never for the age of a read)
    double[] last; long at; double lw, lh;
    /// <summary>The live frame now: <paramref name="live"/> when read this tick, else the last one within HoldMs (same window size); null = the
    /// layout's. <paramref name="gone"/>: the read says the button is not there (no frame held).</summary>
    public double[] Pick(double[] live, bool gone, double W, double H, long now) {
      if (gone) { last = null; return null; }
      if (live != null) { last = live; at = now; lw = W; lh = H; return live; }
      if (last != null && Math.Abs(lw - W) <= 0.5 && Math.Abs(lh - H) <= 0.5 && now - at >= 0 && now - at <= HoldMs) return last;
      last = null; return null;
    }
    /// <summary>The frame shown is a held one (no live read this tick).</summary>
    public bool Has { get { return last != null; } }
    public void Reset() { last = null; }
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
        // (Icon: every button is framed on its picture - its icon child, or for a wide button its visual child - never the whole hit area with its label)
        case ClickTarget.HeroBack: return new UiWidget("Form_CharactorMain", "m_ButtonBack.m_btnBack") { Icon = true };
        case ClickTarget.CityInventory: return new UiWidget("Form_MainCity", "m_Button_Backpack") { Icon = true, IconChild = "Image_Icon" };
        case ClickTarget.FoundryButton: return new UiWidget("Form_BackpackIntegration", "m_PanelContentCur.m_BtnWorkShop") { Icon = true };
        case ClickTarget.AscendTab: return new UiWidget("Form_EquipSubliming", "m_ToggleSubliming") { Icon = true };
        case ClickTarget.ArtWorkshop: return new UiWidget("Form_BackpackIntegration", "m_PanelContentCur.m_BtnIntensify") { Icon = true };   // (the artifact card's «Мастерская»: it moves left when «Продать» is hidden on a worn artifact)
        case ClickTarget.ArtHideWorn: return new UiWidget("Form_CharactorMain", "m_Panels.EquipArtifact.m_HideEquiped");   // (the artifact list's «Скрыть надетое»: the whole toggle takes the click)
        // the selected artifact's «Заменить» / «Надеть» (the compare tip's, else the hero's card's): framed on its visible plate Button_Normal_2 where the
        // game has put it (the geometry's rect is the plate's from the left edge at one aspect: on a wider client it sat left of the button)
        case ClickTarget.ArtReplace: return new UiWidget("Form_CharactorMain", "m_Panels.EquipArtifactInfoTip.m_BtnEquip", "m_Panels.EquipArtifactInfo.m_BtnEquip") { Icon = true, IconChild = "Button_Normal_2" };
        case ClickTarget.EquipConfirm: return new UiWidget("Form_CharactorEquipTip", "m_SelectEquipView.m_RightBtn.m_gameObject", "m_OneEquipView.m_RightBtn.m_gameObject") { Icon = true, Pick = true };
        case ClickTarget.OfferClose: return string.IsNullOrEmpty(offerForm) ? null : new UiWidget(offerForm, OfferClosePaths) { Icon = true, Discover = true };
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
    /// <summary>A read of the window table that finds no form of the class (the table read while the game rehashes it, a name read that failed)
    /// is tried once more at once; still none: the last good result of the class stands in for up to FormKeepMs when its table still is a form of
    /// that class (live 1.6.66.397: "not in UIInstance, forms=none" now and then while the city was shown).</summary>
    public const int FormKeepMs = 500;
    sealed class FormHit { public ulong F; public int Copies, At; }
    static readonly Dictionary<string, FormHit> formLast = new Dictionary<string, FormHit>();
    static ulong FormOf(ulong env, ulong ui, string cls, UiButtonInfo r, out bool shown, out int copies) {
      ulong f = FormOfOnce(env, ui, cls, r, out shown, out copies);
      if (f == 0) { r.Tr("forms", "none - read again"); f = FormOfOnce(env, ui, cls, r, out shown, out copies); }
      int now = Environment.TickCount;
      lock (formLast) {
        FormHit h;
        if (f != 0) { formLast[cls] = new FormHit { F = f, Copies = copies, At = now }; return f; }
        if (formLast.TryGetValue(cls, out h) && unchecked(now - h.At) >= 0 && unchecked(now - h.At) <= FormKeepMs) {
          string why;
          if (FormClassName(h.F, out why) == cls) { shown = Shown(h.F); copies = h.Copies; r.Tr("forms", "kept " + HexA(h.F) + " (" + unchecked(now - h.At) + " ms old)"); return h.F; }
        }
      }
      return 0;
    }
    static ulong FormOfOnce(ulong env, ulong ui, string cls, UiButtonInfo r, out bool shown, out int copies) {
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
      if (w.Pick && w.Paths.Length > 1) return UiPickRead(env, ui, w, W, H, U);
      var sw = Stopwatch.StartNew();
      var r = new UiButtonInfo { Form = w.Form, Widget = w.Paths.Length > 0 ? w.Paths[0] : "", ForW = W, ForH = H, At = Environment.TickCount };
      try { UiButtonReadInto(env, ui, w, W, H, U, r); }
      catch (Exception e) { r.Fail("exception", e.Message); }
      finally { r.Ms = sw.ElapsedMilliseconds; }
      return r;
    }

    static readonly Dictionary<string, string> pickKept = new Dictionary<string, string>();   // widget -> the path taken while it stays shown

    /// <summary>How good a read of one of several instances is: 0 = none (failed, hidden, outside the client), else shown 4 + the centre in the lower
    /// half 2 + the centre right of the client's middle 1 (the candidate's card is the right one of the compare view). Pure (tests/CoreTests.cs).</summary>
    public static int UiPickScore(UiButtonInfo r, double W, double H) {
      if (r == null || !r.Ok || !r.Visible || r.W < 4 || r.H < 4) return 0;
      double cx = r.X + r.W / 2, cy = r.Y + r.H / 2;
      if (cx < 0 || cx > W || cy < 0 || cy > H) return 0;
      return 4 + (cy > H / 2 ? 2 : 0) + (cx > W / 2 ? 1 : 0);
    }

    /// <summary>The instance of <paramref name="w"/> to frame: the one kept from the last read while it is still shown and inside the client (no re-pick
    /// when the cursor hovers a button), else the best of all paths; none shown: the first path's read (it says why).</summary>
    static UiButtonInfo UiPickRead(ulong env, ulong ui, UiWidget w, double W, double H, double U) {
      string key = w.Form + "|" + w.Paths[0], kept; lock (pickKept) pickKept.TryGetValue(key, out kept);
      UiButtonInfo best = null, first = null; int bs = 0;
      foreach (var p in w.Paths) {
        var r = UiButtonRead(env, ui, new UiWidget(w.Form, p) { Icon = w.Icon, IconChild = w.IconChild, Discover = w.Discover, Trees = w.Trees }, W, H, U);
        if (first == null) first = r;
        int sc = UiPickScore(r, W, H);
        if (sc > 0 && p == kept) { best = r; break; }   // the kept one stays while it is shown
        if (sc > bs) { bs = sc; best = r; }
      }
      lock (pickKept) { if (best != null) pickKept[key] = best.Widget; else pickKept.Remove(key); }
      return best ?? first;
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
        cw = rt[0][6]; ch = rt[0][7]; double byH; k2 = UiIcon.CanvasScale(W, H, cw, ch, out byH);
        pw = cw; ph = ch; px = (rt[0][8] - 0.5) * cw; py = (rt[0][9] - 0.5) * ch; i0 = 1;
        if (main) r.Scale = "canvas";
        if (main) r.Tr("scale", "canvas " + F2(k2) + " px/unit (" + F2(byH) + " by height) vs unit " + F2(U / CanvasRefH));
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
      r.CPivX = rt[n - 1][8]; r.CPivY = rt[n - 1][9]; r.CPx = Math.Abs(sx) * k2; r.CFlipX = sx < 0; r.CFlipY = sy < 0;
      return true;
    }

    /// <summary>The icon child of the button (UiIcon.Pick over its children, depth 1-2, at most 40 looked at) into r.IX.. (none: IW stays 0).</summary>
    static void IconInto(List<ulong> chain, bool canvas, double W, double H, double U, UiButtonInfo r, string named = null) {
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
          cands.Add(new UiIcon.Cand { Name = CStr(Ptr(go + GoName)) ?? "", X = x, Y = y, W = w, H = h, Active = ab != null && ab[0] == 1, Depth = depth + 1, Go = go, Rect = tmp });
          todo.Add(new KeyValuePair<List<ulong>, int>(path, depth + 1));
        }
      }
      int k = UiIcon.Named(cands, named);
      if (k < 0) k = UiIcon.Pick(cands, r.X, r.Y, r.W, r.H);
      if (k < 0) k = UiIcon.PickVisual(cands, r.X, r.Y, r.W, r.H);   // (no icon: the drawn part of a wide button)
      r.Tr("icon", k < 0 ? "none of " + cands.Count : cands[k].Name + " " + N0(cands[k].X) + "," + N0(cands[k].Y) + "," + N0(cands[k].W) + "," + N0(cands[k].H));
      if (k < 0) return;
      r.IX = cands[k].X; r.IY = cands[k].Y; r.IW = cands[k].W; r.IH = cands[k].H; r.IconName = cands[k].Name;
      ArtInto(cands[k].Go, cands[k].Rect, r);
    }

    // ---- the Image of a picture: its sprite's name and how it draws it (src/SpriteArt.cs maps the sprite's art through the rect)
    // native component -> its managed object (Unity 2019.4 Object: the cached scripting object 0x28), checked by class name (UnityEngine.UI.Image
    // or the game's subclass of it); Image (dump 1.6.46.841.1): m_Sprite 0xD0, m_OverrideSprite 0xD8 (drawn when set), m_Type 0xE0,
    // m_PreserveAspect 0xE4, m_PixelsPerUnitMultiplier 0x108; Sprite (managed) -> m_CachedPtr -> the native Sprite, NamedObject m_Name 0x30 (char*).
    const int NativeManaged = 0x28, ImgSprite = 0xD0, ImgType = 0xE0, ImgPreserve = 0xE4, ImgPpum = 0x108, NamedName = 0x30;

    /// <summary>The sprite name and drawing of the Image on GameObject <paramref name="go"/> (native) into r.Sprite / r.Art; <paramref name="rect"/>:
    /// the ChainRect info of that object's rect (pivot, px per unit, flips). Nothing found: r.ArtWhy says why (the frame stays the rect's).</summary>
    static void ArtInto(ulong go, UiButtonInfo rect, UiButtonInfo r) {
      r.Sprite = ""; r.Art = null; r.ArtWhy = "";
      if (go == 0) { r.ArtWhy = "no object"; return; }
      var cb = Read(go + GoComps, 0x18); if (cb == null) { r.ArtWhy = "components"; return; }
      ulong arr = BitConverter.ToUInt64(cb, 0); long cnt = BitConverter.ToInt64(cb, 0x10);
      if (arr == 0 || cnt <= 0 || cnt > 24) { r.ArtWhy = "components " + cnt; return; }
      for (int i = 1; i < cnt; i++) {   // (0 is the Transform)
        ulong c = Ptr(arr + (ulong)(16 * i + 8)); if (c == 0) continue;
        ulong m = Ptr(c + NativeManaged); if (m == 0 || Ptr(m + CachedPtr) != c) continue;   // (the managed side points back at the native one)
        string cls = ClassName(Ptr(m)) ?? "";
        if (!cls.EndsWith("Image", StringComparison.Ordinal) || cls.EndsWith("RawImage", StringComparison.Ordinal)) continue;
        var b = Read(m + ImgSprite, ImgPpum + 4 - ImgSprite); if (b == null) { r.ArtWhy = "image fields"; return; }
        ulong sp = BitConverter.ToUInt64(b, 8); if (sp == 0) sp = BitConverter.ToUInt64(b, 0);
        if (sp == 0) { r.ArtWhy = "no sprite"; return; }
        ulong sn = Ptr(sp + CachedPtr); string name = sn != 0 ? CStr(Ptr(sn + NamedName)) : null;
        if (string.IsNullOrEmpty(name) || name.Length > 80) { r.ArtWhy = "sprite name"; return; }
        foreach (char ch in name) if (ch < 32 || ch > 126) { r.ArtWhy = "sprite name"; return; }
        int type = I32(b, ImgType - ImgSprite); float ppum = F32(b, ImgPpum - ImgSprite);
        r.Sprite = name;
        r.Art = new SpriteArt.Draw { Type = type >= 0 && type <= 3 ? type : 0, Preserve = b[ImgPreserve - ImgSprite] != 0, Ppum = ppum > 0.01 && ppum < 100 ? ppum : 1,
          PivX = rect != null ? rect.CPivX : 0.5, PivY = rect != null ? rect.CPivY : 0.5, Px = rect != null ? rect.CPx : 0, FlipX = rect != null && rect.CFlipX, FlipY = rect != null && rect.CFlipY };
        return;
      }
      r.ArtWhy = "no Image";
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
      if (ud == 0 && !w.Discover && w.Trees == null) { r.Fail("field", miss); return; }
      ulong csv; int ctt;
      if (!Field(f, "m_csui", out csv, out ctt) || ctt != T_UDATA) { r.Fail("m_csui", "tt " + ctt); return; }
      string cls = "", rcls;
      ulong nat = 0;
      if (ud != 0) { nat = NativeOf(env, ud, "widget", r, out cls); if (nat == 0) return; r.ClassName = cls; }
      ulong rnat = FormRootOf(env, csv, r, out rcls); if (rnat == 0) return;
      ulong go = 0, tr = 0, rgo, rtr;
      if (ud != 0 && !GoAndTransform(nat, cls, "widget", r, out go, out tr)) return;
      if (!GoAndTransform(rnat, rcls, "root", r, out rgo, out rtr)) return;
      if (ud == 0 && w.Trees != null) {   // a frame target by its place in the form's tree
        string found;
        if (!TreeFind(rtr, w.Trees, r, out tr, out go, out found)) return;
        r.Widget = "tree:" + found; r.ClassName = "UnityEngine.GameObject";
      } else if (ud == 0) {   // the field is not there (live 1.6.66.393: Form_DailyCheckIn.m_BtnClose missing): the close button looked for live
        string found;
        if (!UiCloseFind(w.Form, rtr, W, H, U, r, out tr, out go, out found)) { if (r.Ok) r.Fail("field", miss + "; no close button found live"); return; }
        r.Widget = "live:" + found; r.ClassName = "UnityEngine.GameObject";
      }
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
      if (w.Icon && r.Visible) IconInto(chain, canvas, W, H, U, r, w.IconChild);
      if (r.Visible && r.IW <= 0) {   // the widget's own Image draws it (no icon child): its rect's drawing is the main one
        var me = new UiButtonInfo { CPivX = r.CPivX, CPivY = r.CPivY, CPx = r.CPx, CFlipX = r.CFlipX, CFlipY = r.CFlipY };
        ArtInto(go, me, r);
      }
    }

    // ---- a frame target by its place in the form's tree (src/FrameTargets.cs)
    static string NodeName(ulong t) {
      ulong go = Ptr(t + CompGo); string n = go != 0 ? CStr(Ptr(go + GoName)) : null;
      if (n == null) return null;
      return n.EndsWith("(Clone)", StringComparison.Ordinal) ? n.Substring(0, n.Length - 7) : n;
    }
    static bool NodeActive(ulong t) { ulong go = Ptr(t + CompGo); var b = go != 0 ? Read(go + GoActiveSelf, 1) : null; return b != null && b[0] == 1; }
    static List<ulong> Kids(ulong t) {
      var res = new List<ulong>();
      var cb = Read(t + TrChildren, 0x18); if (cb == null) return res;
      ulong arr = BitConverter.ToUInt64(cb, 0); long cnt = BitConverter.ToInt64(cb, 0x10);
      if (arr == 0 || cnt <= 0 || cnt > 256) return res;
      for (int i = 0; i < cnt; i++) { ulong c = Ptr(arr + (ulong)(8 * i)); if (c != 0 && Ptr(c + TrParent) == t) res.Add(c); }
      return res;
    }
    /// <summary>The child of <paramref name="t"/> named <paramref name="seg"/> (an active one first), or "#n": its n-th active child; 0 = none.</summary>
    static ulong ChildNamed(ulong t, string seg) {
      var kids = Kids(t);
      if (seg.StartsWith("#", StringComparison.Ordinal)) {
        int n; if (!int.TryParse(seg.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return 0;
        foreach (var c in kids) if (NodeActive(c) && n-- == 0) return c;
        return 0;
      }
      ulong hidden = 0;
      foreach (var c in kids) if (NodeName(c) == seg) { if (NodeActive(c)) return c; if (hidden == 0) hidden = c; }
      return hidden;
    }
    /// <summary>A node named <paramref name="seg"/> below <paramref name="t"/> (breadth first, 6 levels, 800 nodes; an active one first).</summary>
    static ulong DescendantNamed(ulong t, string seg) {
      if (seg.StartsWith("#", StringComparison.Ordinal)) return 0;
      var q = new List<KeyValuePair<ulong, int>> { new KeyValuePair<ulong, int>(t, 0) }; ulong hidden = 0;
      for (int i = 0; i < q.Count && q.Count < 800; i++) {
        if (q[i].Value >= 6) continue;
        foreach (var c in Kids(q[i].Key)) {
          if (NodeName(c) == seg) { if (NodeActive(c)) return c; if (hidden == 0) hidden = c; }
          q.Add(new KeyValuePair<ulong, int>(c, q[i].Value + 1));
        }
      }
      return hidden;
    }
    static bool ActiveUp(ulong t, ulong root) {
      for (int i = 0; i < 64 && t != 0; i++) { if (!NodeActive(t)) return false; if (t == root) return true; t = Ptr(t + TrParent); }
      return false;
    }
    /// <summary>The first of <paramref name="trees"/> found under the form's root <paramref name="rtr"/> and shown (else the first found at all: the read
    /// says not visible); false = none found (the step in <paramref name="r"/>).</summary>
    static bool TreeFind(ulong rtr, string[] trees, UiButtonInfo r, out ulong tr, out ulong go, out string found) {
      tr = go = 0; found = null; string why = null; ulong ftr = 0; string ff = null;
      foreach (var p in trees) {
        ulong t = rtr; bool ok = true;
        foreach (var seg in p.Split('/')) {
          if (seg.Length == 0) continue;
          ulong c = ChildNamed(t, seg); if (c == 0) c = DescendantNamed(t, seg);
          if (c == 0) { ok = false; if (why == null) why = p + " at " + seg; break; }
          t = c;
        }
        if (!ok || Ptr(t + CompGo) == 0) continue;
        if (ftr == 0) { ftr = t; ff = p; }
        if (ActiveUp(t, rtr)) { tr = t; go = Ptr(t + CompGo); found = p; return true; }
      }
      if (ftr != 0) { tr = ftr; go = Ptr(ftr + CompGo); found = ff; return true; }
      r.Fail("tree", why ?? "no path"); return false;
    }

    // ---- the kept reads of the frame targets by key (src/FrameTargets.cs; the pilot buttons keep theirs by ClickTarget above)
    sealed class KeyedRect { public UiButtonInfo R; public double W, H; public int At; }
    static readonly Dictionary<string, KeyedRect> tgLive = new Dictionary<string, KeyedRect>();
    static readonly Dictionary<string, int> tgTried = new Dictionary<string, int>();
    static int tgBusy;

    /// <summary>One read now of the frame target <paramref name="key"/> (widget <paramref name="w"/>), kept for its frame. Null = no game.</summary>
    public static UiButtonInfo UiTargetReadNow(string key, UiWidget w, double W, double H, double U) {
      if (w == null || UiNoGame()) return null;
      int at = Environment.TickCount; long m0 = LoadMeter.Now();
      ulong env, ui; string why; UiButtonInfo r;
      if (!UiRoots(out env, out ui, out why)) { r = new UiButtonInfo { Form = w.Form, Widget = key, ForW = W, ForH = H, At = at }; r.Fail("lua root", why); }
      else r = UiButtonRead(env, ui, w, W, H, U);
      r.At = Environment.TickCount;
      lock (tgLive) tgLive[key] = new KeyedRect { R = r, W = W, H = H, At = r.At };
      LoadMeter.Done(LoadMeter.UiBtn, m0);
      return r;
    }

    /// <summary>The kept read of frame target <paramref name="key"/> when at most <paramref name="maxMs"/> old and for this window size (a failed or hidden
    /// read too: UiLivePoint says why); else null.</summary>
    public static UiButtonInfo UiTargetFresh(string key, double W, double H, int now, int maxMs) {
      KeyedRect l; lock (tgLive) if (!tgLive.TryGetValue(key, out l)) return null;
      if (Math.Abs(l.W - W) > 0.5 || Math.Abs(l.H - H) > 0.5) return null;
      int age = unchecked(now - l.At);
      return age >= 0 && age <= maxMs ? l.R : null;
    }

    /// <summary>A frame target is shown: read again in the background when its kept read is UiWantMs old (one read at a time).</summary>
    public static void UiTargetWant(string key, UiWidget w, double W, double H, double U) {
      if (w == null) return;
      int now = Environment.TickCount;
      lock (tgTried) { int at; if (tgTried.TryGetValue(key, out at) && unchecked(now - at) >= 0 && unchecked(now - at) < UiWantMs) return; tgTried[key] = now; }
      if (Interlocked.CompareExchange(ref tgBusy, 1, 0) != 0) return;
      try {
        Task.Factory.StartNew(() => {
          try { UiTargetReadNow(key, w, W, H, U); }
          catch (Exception e) { LL("uibtn: failed at exception (" + e.Message + ")"); }
          finally { Interlocked.Exchange(ref tgBusy, 0); }
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
      } catch (Exception) { Interlocked.Exchange(ref tgBusy, 0); }
    }

    /// <summary>Is <paramref name="form"/> on top now (named on top, or one of the flow forms over its screen), with no pop-up over it?</summary>
    public static bool UiFormTopIs(string form, ScreenState s) {
      if (string.IsNullOrEmpty(form) || s == null || s.Held || s.OfferCount > 0) return false;
      if (s.Top == form) return true;
      return !s.ModalOpen && Array.IndexOf((s.Flow ?? "").Split(','), form) >= 0;
    }

    static string N0(double v) { return Math.Round(v).ToString(CultureInfo.InvariantCulture); }

    // ---- the close button looked for live: a form whose × is not under the field names (OfferClosePaths)
    /// <summary>The object names a close button has in the game's prefabs (tools/screen-map.py CLOSE_RX, and the exit buttons).</summary>
    public static readonly System.Text.RegularExpressions.Regex CloseName = new System.Text.RegularExpressions.Regex(
      @"^(m_?)?(Btn_?Close\w*|Button_?Close\w*|Close_?Btn\w*|Close_?Button\w*|btn_?close\w*|Btn_?Exit\w*|Exit_?Btn\w*|Button_?Exit\w*|Close)$",
      System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly Dictionary<string, string> closeFound = new Dictionary<string, string>();   // form class -> the object path found (names from the root)
    public const int CloseFindNodes = 600, CloseFindDepth = 10;

    /// <summary>Of the close-button candidates (client px) the one to press: a size of a button (8 px up to 30 % of the height), its centre in
    /// the client's top right part (right of 45 % of the width, above 55 % of the height) and on none of the form's never-click buttons; the
    /// top-right-most of those. -1 = none. Pure (tests/CoreTests.cs).</summary>
    public static int UiClosePick(string form, IList<UiIcon.Cand> c, double W, double H, double U) {
      int best = -1; double bs = double.NegativeInfinity;
      for (int i = 0; c != null && i < c.Count; i++) {
        var k = c[i]; if (!k.Active || !CloseName.IsMatch(k.Name ?? "")) continue;
        if (k.W < 8 || k.H < 8 || k.W > H * 0.3 || k.H > H * 0.3) continue;
        double cx = k.X + k.W / 2, cy = k.Y + k.H / 2;
        if (cx < W * 0.45 || cx > W - 1 || cy > H * 0.55 || cy < 1) continue;
        if (OfferForms.OnNeverButton(form, cx, cy, W, H, U)) continue;
        double sc = cx / W - cy / H;
        if (sc > bs) { bs = sc; best = i; }
      }
      return best;
    }

    /// <summary>The form's close button among its objects (active ones only, breadth first from the form's root, at most CloseFindNodes objects
    /// CloseFindDepth deep): the path cached for the form is tried first, else the objects are walked and UiClosePick chooses.</summary>
    static bool UiCloseFind(string form, ulong rtr, double W, double H, double U, UiButtonInfo r, out ulong tr, out ulong go, out string found) {
      tr = 0; go = 0; found = null;
      var above = new List<ulong>(); var probe = new UiButtonInfo();
      bool canvas = CanvasAbove(rtr, W, H, probe, above);
      if (!canvas && U <= 0) return false;
      string cached; lock (closeFound) closeFound.TryGetValue(form, out cached);
      if (cached != null) {
        ulong t = rtr, g = 0; bool ok = true;
        foreach (var nm in cached.Split('/')) { ulong c = ChildNamed(t, nm, out g); if (c == 0) { ok = false; break; } t = c; }
        if (ok) { tr = t; go = g; found = cached; r.Tr("close", "cached " + cached); return true; }
        lock (closeFound) closeFound.Remove(form);
      }
      var cands = new List<UiIcon.Cand>(); var paths = new List<string>(); var trs = new List<ulong>(); var gos = new List<ulong>();
      var todo = new List<KeyValuePair<List<ulong>, string>> { new KeyValuePair<List<ulong>, string>(new List<ulong> { rtr }, "") };
      int seen = 0;
      for (int q = 0; q < todo.Count && seen < CloseFindNodes; q++) {
        var at = todo[q].Key; string pre = todo[q].Value;
        if (at.Count > CloseFindDepth) continue;
        ulong t = at[at.Count - 1];
        var cb = Read(t + TrChildren, 0x18); if (cb == null) continue;
        ulong arr = BitConverter.ToUInt64(cb, 0); long cnt = BitConverter.ToInt64(cb, 0x10);
        if (arr == 0 || cnt <= 0 || cnt > 64) continue;
        for (int i = 0; i < cnt && seen < CloseFindNodes; i++) {
          ulong c = Ptr(arr + (ulong)(8 * i)); if (c == 0 || Ptr(c + TrParent) != t) continue;
          ulong g = Ptr(c + CompGo); if (g == 0) continue;
          var ab = Read(g + GoActiveSelf, 1); if (ab == null || ab[0] != 1) continue;   // (a hidden object and all under it: not shown)
          seen++;
          string nm = CStr(Ptr(g + GoName)) ?? ""; string path = pre == "" ? nm : pre + "/" + nm;
          var p2 = new List<ulong>(at); p2.Add(c);
          if (CloseName.IsMatch(nm)) {
            var chain = new List<ulong>(p2); if (canvas) chain.InsertRange(0, above);
            var tmp = new UiButtonInfo(); double x, y, w2, h2;
            if (ChainRect(chain, canvas, W, H, U, tmp, false, out x, out y, out w2, out h2)) {
              cands.Add(new UiIcon.Cand { Name = nm, X = x, Y = y, W = w2, H = h2, Active = true, Depth = p2.Count - 1 }); paths.Add(path); trs.Add(c); gos.Add(g);
            }
          }
          if (nm.IndexOf('/') < 0) todo.Add(new KeyValuePair<List<ulong>, string>(p2, path));
        }
      }
      int k = UiClosePick(form, cands, W, H, U);
      var sb = new System.Text.StringBuilder();
      for (int i = 0; i < cands.Count && sb.Length < 300; i++) sb.Append(paths[i]).Append('[').Append(N0(cands[i].X)).Append(',').Append(N0(cands[i].Y)).Append(',').Append(N0(cands[i].W)).Append(',').Append(N0(cands[i].H)).Append("] ");
      r.Tr("close", (k < 0 ? "none" : "found " + paths[k]) + " of " + cands.Count + " named (" + seen + " objects): " + sb);
      if (k < 0) return false;
      tr = trs[k]; go = gos[k]; found = paths[k];
      lock (closeFound) closeFound[form] = found;
      LL("uibtn: " + form + " close button found live: " + found + " [" + N0(cands[k].X) + "," + N0(cands[k].Y) + "," + N0(cands[k].W) + "," + N0(cands[k].H) + "] (cached for the form)");
      return true;
    }

    /// <summary>The active child of transform <paramref name="t"/> named <paramref name="name"/> (0 = none), its GameObject in <paramref name="go"/>.</summary>
    static ulong ChildNamed(ulong t, string name, out ulong go) {
      go = 0;
      var cb = Read(t + TrChildren, 0x18); if (cb == null) return 0;
      ulong arr = BitConverter.ToUInt64(cb, 0); long cnt = BitConverter.ToInt64(cb, 0x10);
      if (arr == 0 || cnt <= 0 || cnt > 64) return 0;
      for (int i = 0; i < cnt; i++) {
        ulong c = Ptr(arr + (ulong)(8 * i)); if (c == 0 || Ptr(c + TrParent) != t) continue;
        ulong g = Ptr(c + CompGo); if (g == 0) continue;
        var ab = Read(g + GoActiveSelf, 1); if (ab == null || ab[0] != 1) continue;
        if ((CStr(Ptr(g + GoName)) ?? "") == name) { go = g; return c; }
      }
      return 0;
    }

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
    static bool UiNoGame() { return TestMemory == null && (Layout.Current == null || GameInfo.GamePidFresh(1000) == 0); }

    /// <summary>The live rect (client px: x, y, w, h) of a form's widget by its Lua field, read now (the click gate's cover check: a dangerous
    /// widget of a form shown over the click's form); null = not read, or not visible. No game: null.</summary>
    public static double[] UiFieldReadNow(string form, string field, double W, double H, double U) {
      if (string.IsNullOrEmpty(form) || string.IsNullOrEmpty(field) || UiNoGame()) return null;
      ulong env, ui; string why;
      if (!UiRoots(out env, out ui, out why)) return null;
      var r = UiButtonRead(env, ui, new UiWidget(form, field), W, H, U);
      return r.FailStep == null && r.Visible && r.W > 0 && r.H > 0 ? new[] { r.X, r.Y, r.W, r.H } : null;
    }

    /// <summary>One read now of the pilot button <paramref name="t"/> (from the script root), kept for the clicks and frames; a failed read is
    /// dumped step by step once a minute (test build). Null = not a button read by name, or no game.</summary>
    public static UiButtonInfo UiButtonReadNow(ClickTarget t, string offerForm, double W, double H, double U) {
      var w = UiWidgetOf(t, offerForm); if (w == null || UiNoGame()) return null;
      var sw = Stopwatch.StartNew(); int at = Environment.TickCount; long m0 = LoadMeter.Now();
      ulong env, ui; string why; UiButtonInfo r;
      if (!UiRoots(out env, out ui, out why)) { r = new UiButtonInfo { Form = w.Form, Widget = w.Paths[0], ForW = W, ForH = H, At = at }; r.Fail("lua root", why); }
      else r = UiButtonRead(env, ui, w, W, H, U);
      r.Ms = sw.ElapsedMilliseconds;
      at = Environment.TickCount; r.At = at;   // (its age from the end of the read: the rect fields are read last)
      UiButtonKeep(t, r, W, H, at);
      LoadMeter.Done(LoadMeter.UiBtn, m0);
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
    /// <summary>How old a live read may be to place a FRAME (never a click): the screen read of a calm hint runs every 250 ms (src/LoadPlan.cs), so
    /// a read is up to that old at the next tick; a frame is not hidden for it (owner, 1.6.66.435: the frame went away when he stopped touching).</summary>
    public const int UiFrameFreshMs = 900;

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
      // (the tip is the equip pilot's flow form above the hero screen: named on top, or shown as the hero screen's flow form, it IS the top form
      // of the target)
      if (t == ClickTarget.EquipConfirm) return s.OfferCount == 0 && (s.Top == w.Form || (s.Kind == ScreenKind.HeroScreen && Array.IndexOf((s.Flow ?? "").Split(','), w.Form) >= 0));
      if (t == ClickTarget.OfferClose) return s.OfferCount > 0 && s.Offer == w.Form;
      return s.Top == w.Form && s.OfferCount == 0 && !s.ModalOpen;
    }

    /// <summary>The click point of a pilot button from its live read: the rect's centre when the read is ok, fresh (at most UiFreshMs old at
    /// <paramref name="now"/>), visible, unrotated, the form's one shown copy and the centre inside the client; else false and why (the layout's point then).</summary>
    public static bool UiLivePoint(UiButtonInfo r, double W, double H, int now, out double x, out double y, out string why, int maxAge = UiFreshMs) {
      x = y = 0; why = null;
      if (r == null) { why = "no read"; return false; }
      if (!r.Ok) { why = "failed at " + r.FailStep; return false; }
      int age = unchecked(now - r.At);
      if (age < 0 || age > maxAge) { why = "old (" + age + " ms)"; return false; }
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
  // ---- item cells of the game's lists, read live (src/UiButtons.cs: the same chain as the buttons)
  // The lists are UI.InfinityGrid / UI.InfinityGridV2 components: pooled cells under the ScrollRect's content, the grid's m_crtItems
  // (Dictionary<int, ItemInstance>: the list index a cell is bound to -> {TemplateIndex 0x0, ItemGo 0x8 [V2: Index 0x10]}) holds the
  // cells it has made for the rows in view. The Lua bind of each list maps the index to the item (idx 0-based):
  //   Form_BackpackIntegration.m_Content_InfinityGrid      m_CachedContentPanels[-9998].m_FilterAndSortEquipUidList[idx + 1] (cell child c_Item)
  //   Form_EquipSubliming.m_PanelEquipSubliming / m_PanelEquipRecast .m_EquipList_InfinityGrid   .m_Equips[idx + 1] (cell child c_EquipItem)
  //   Form_CharactorMain.m_Panels.EquipFilter.m_EquipList_InfinityGrid (V2)   .m_EquipListRealData[idx + 1].Item[col] (cell child c_Equip<col>)
  // The item's picture inside: c_Frame (the quality frame round the icon), else c_Icon, else the cell child, else the cell.
  // InfinityGrid (dump 1.6.46.841.1): m_crtFirstInstanceIndex 0xD0, m_crtLastInstanceIndex 0xD4, m_crtItems 0xD8, viewport 0xC8;
  // InfinityGridV2: 0xF8, 0xFC, 0x100, viewport 0xF0. .NET Dictionary: entries 0x18, count 0x20; Entry {hashCode, next, key 0x8, value 0x10}.
  //   Form_CharactorMain (the hero list)  the form's one *_InfinityGrid   .m_InfinityGridProxy.m_Data[idx + 1].iHeroId (the card: the cell itself)
  public enum UiCellKind { None, Inventory, Ascend, Recast, HeroGear, HeroArt, HeroList }

  /// <summary>One item cell the grid has made now: the item, its list index (0-based) and row column (HeroGear, 1-based), its picture's rect (client px).</summary>
  public sealed class UiCell {
    public long Uid; public int Index, Col; public double X, Y, W, H; public string Icon = ""; public bool Active;
    /// <summary>The picture's Image: its sprite ("" = not read) and drawing (src/SpriteArt.cs); Fit = the frame UiCellState made from it (null = the rect's).</summary>
    public string Sprite = ""; public SpriteArt.Draw Art; public SpriteArt.Fit Fit;
    /// <summary>How much of the picture lies inside the list's viewport (0..1).</summary>
    public double InView;
    /// <summary>The whole cell (client px): the cell root with its picture, the icon and the stat plate under it; the frame is drawn on it.</summary>
    public double BX, BY, BW, BH;
  }

  /// <summary>The cells of a list read at once (the wanted items only), the viewport, or the step the read stopped at.</summary>
  public sealed class UiCellSet {
    public UiCellKind Kind; public string Form = "", FailStep, FailValue, Scale = "";
    public readonly List<UiCell> Cells = new List<UiCell>();
    public double VX, VY, VW, VH;   // the viewport (client px)
    public bool Visible, Rotated;   // the form shown; a rotation on the way
    public int Copies, Created, First = -1, Last = -1; public double ForW, ForH; public int At; public long Ms;
    public readonly System.Text.StringBuilder Trace = new System.Text.StringBuilder();
    public bool Ok { get { return FailStep == null; } }
    internal UiCellSet Fail(string step, string value) { FailStep = step; FailValue = value; return this; }
    public UiCell Of(long uid) { foreach (var c in Cells) if (c.Uid == uid) return c; return null; }
  }

  /// <summary>What a live cell read says about an item: not known (the layout's estimate stands), shown (its frame), hidden (no cell in view, or covered).</summary>
  public enum UiCellSee { Unknown, Shown, Hidden }

  public static partial class RFX {
    sealed class CellSpec { public string Form, Owner, Grid, Uids, Child, UidKey = "iItemUid"; public bool V2, Rows, Tables; }
    static CellSpec SpecOf(UiCellKind k) {
      switch (k) {
        case UiCellKind.Inventory: return new CellSpec { Form = "Form_BackpackIntegration", Owner = "", Grid = "m_Content_InfinityGrid", Uids = "m_CachedContentPanels.[-9998].m_FilterAndSortEquipUidList", Child = "c_Item" };
        case UiCellKind.Ascend: return new CellSpec { Form = "Form_EquipSubliming", Owner = "m_PanelEquipSubliming", Grid = "m_EquipList_InfinityGrid", Uids = "m_PanelEquipSubliming.m_Equips", Child = "c_EquipItem" };
        case UiCellKind.Recast: return new CellSpec { Form = "Form_EquipSubliming", Owner = "m_PanelEquipRecast", Grid = "m_EquipList_InfinityGrid", Uids = "m_PanelEquipRecast.m_Equips", Child = "c_EquipItem" };
        case UiCellKind.HeroGear: return new CellSpec { Form = "Form_CharactorMain", Owner = "m_Panels.EquipFilter", Grid = "m_EquipList_InfinityGrid", Uids = "m_Panels.EquipFilter.m_EquipListRealData", Child = "c_Equip", V2 = true, Rows = true };
        // the hero screen's artifact list (Panel_Equip_Artifact): the grid proxy's m_Data rows are the artifact tables (iItemUid), the cell's card is c_CardBtn
        case UiCellKind.HeroArt: return new CellSpec { Form = "Form_CharactorMain", Owner = "m_Panels.EquipArtifact", Grid = "m_List_InfinityGrid", Uids = "m_Panels.EquipArtifact.m_InfinityGridProxy.m_Data", Child = "c_CardBtn", Tables = true };
        // the hero list: the grid proxy's m_Data rows are the hero tables (iHeroId, the order EquipScan.ReadHeroScreen reads); the grid is the form's
        // one field named *_InfinityGrid (Grid "*": found by its name, none or more than one = no read - the card is then not framed)
        case UiCellKind.HeroList: return new CellSpec { Form = "Form_CharactorMain", Owner = "", Grid = "*", Uids = "m_InfinityGridProxy.m_Data", Child = "", Tables = true, UidKey = "iHeroId" };
      }
      return null;
    }
    /// <summary>The list a cell click target picks in (None = not an item pick).</summary>
    public static UiCellKind UiCellKindOf(ClickTarget t) {
      switch (t) {
        case ClickTarget.InvListCell: case ClickTarget.InvBulkCell: return UiCellKind.Inventory;
        case ClickTarget.AscendCell: return UiCellKind.Ascend;
        case ClickTarget.EquipNav: return UiCellKind.HeroGear;
        case ClickTarget.ArtHeroCell: return UiCellKind.HeroArt;
      }
      return UiCellKind.None;
    }

    /// <summary>A Lua path "a.[-9998].b" from table <paramref name="t"/> (a [n] link is an integer key): the last value and type.</summary>
    static bool LuaPathX(ulong t, string path, out ulong v, out int tt) {
      v = t; tt = T_TABLE;
      if (string.IsNullOrEmpty(path)) return true;
      var parts = path.Split('.');
      for (int i = 0; i < parts.Length; i++) {
        if (tt != T_TABLE) return false;
        string p = parts[i]; long n;
        bool ok = p.StartsWith("[", StringComparison.Ordinal) && long.TryParse(p.Trim('[', ']'), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? IntKey(v, n, out v, out tt) : Field(v, p, out v, out tt);
        if (!ok || tt == T_NIL) return false;
      }
      return true;
    }

    /// <summary>The item a cell of list index <paramref name="idx"/> (0-based) shows: flat lists by the index, rows of HeroGear by row and column.</summary>
    static long CellUidOf(ulong list, int idx, int col, bool rows, bool tables = false, string uidKey = "iItemUid") {
      ulong v; int tt;
      if (!IntKey(list, idx + 1, out v, out tt)) return 0;
      if (tables) { long u; return tt == T_TABLE && ArtInt(v, uidKey, out u) ? u : 0; }
      if (!rows) return tt == T_INT ? (long)v : 0;
      if (tt != T_TABLE) return 0;
      ulong items; int itt;
      if (!Field(v, "Item", out items, out itt) || itt != T_TABLE) return 0;
      return IntKey(items, col, out v, out tt) && tt == T_INT ? (long)v : 0;
    }

    /// <summary>A child transform of <paramref name="t"/> named <paramref name="name"/> (breadth first, depth up to <paramref name="depth"/>, at most 80 looked at), with the path to it.</summary>
    static ulong ChildNamed(ulong t, string name, int depth, List<ulong> path) {
      var q = new List<KeyValuePair<ulong, int>> { new KeyValuePair<ulong, int>(t, 0) }; var from = new Dictionary<ulong, ulong>();
      for (int i = 0; i < q.Count && q.Count < 80; i++) {
        ulong at = q[i].Key; int d = q[i].Value; if (d >= depth) continue;
        var cb = Read(at + TrChildren, 0x18); if (cb == null) continue;
        ulong arr = BitConverter.ToUInt64(cb, 0); long cnt = BitConverter.ToInt64(cb, 0x10);
        if (arr == 0 || cnt <= 0 || cnt > 48) continue;
        for (int j = 0; j < cnt; j++) {
          ulong c = Ptr(arr + (ulong)(8 * j)); if (c == 0 || Ptr(c + TrParent) != at || from.ContainsKey(c)) continue;
          from[c] = at;
          ulong go = Ptr(c + CompGo);
          if (go != 0 && CStr(Ptr(go + GoName)) == name) {
            var back = new List<ulong>(); for (ulong x = c; x != t; x = from[x]) back.Add(x);
            back.Reverse(); path.AddRange(back); return c;
          }
          q.Add(new KeyValuePair<ulong, int>(c, d + 1));
        }
      }
      return 0;
    }

    /// <summary>Is the GameObject of transform <paramref name="t"/> active in the hierarchy (the cached flag; not computed yet: its own flag)?</summary>
    static bool TrActive(ulong t) {
      ulong go = Ptr(t + CompGo); var ab = go != 0 ? Read(go + GoActiveSelf, 2) : null;
      if (ab == null) return false;
      return ab[1] == 0 || ab[1] == 1 ? ab[1] == 1 : ab[0] == 1;
    }

    /// <summary>The rect (client px) of transform <paramref name="t"/> under the form's root <paramref name="rtr"/> (chain up to it, the canvas above prefixed).</summary>
    static bool RectUnder(ulong t, ulong rtr, List<ulong> above, bool canvas, double W, double H, double U, UiButtonInfo bi, bool main, out double x, out double y, out double w, out double h) {
      x = y = w = h = 0;
      var chain = new List<ulong>();
      for (ulong a = t; a != rtr; a = Ptr(a + TrParent)) { if (a == 0 || chain.Count >= 64) { bi.Fail("parents", a == 0 ? "not under the form's root" : "too deep"); return false; } chain.Add(a); }
      chain.Add(rtr); chain.Reverse();
      if (canvas) chain.InsertRange(0, above);
      return ChainRect(chain, canvas, W, H, U, bi, main, out x, out y, out w, out h);
    }

    /// <summary>The cells of list <paramref name="k"/> showing the items of <paramref name="want"/> (null = every cell made), read now.</summary>
    public static UiCellSet UiCellRead(ulong env, ulong ui, UiCellKind k, ICollection<long> want, double W, double H, double U) {
      var sw = Stopwatch.StartNew(); long m0 = LoadMeter.Now();
      var r = new UiCellSet { Kind = k, ForW = W, ForH = H, At = Environment.TickCount };
      try { UiCellReadInto(env, ui, k, want, W, H, U, r); }
      catch (Exception e) { r.Fail("exception", e.Message); }
      finally { r.Ms = sw.ElapsedMilliseconds; LoadMeter.Done(LoadMeter.Cell, m0); }
      return r;
    }

    static void UiCellReadInto(ulong env, ulong ui, UiCellKind k, ICollection<long> want, double W, double H, double U, UiCellSet r) {
      var sp = SpecOf(k); if (sp == null) { r.Fail("list", k.ToString()); return; }
      r.Form = sp.Form;
      if (env == 0 || ui == 0) { r.Fail("lua root", "not reached"); return; }
      if (W < 50 || H < 50) { r.Fail("window", "no client size"); return; }
      var bi = new UiButtonInfo();
      bool shown; int copies; ulong f = FormOf(env, ui, sp.Form, bi, out shown, out copies);
      if (f == 0) { r.Fail("form", "not in UIInstance"); return; }
      r.Copies = copies; r.Visible = shown;
      ulong owner, list, gud; int ott, ltt, gtt;
      if (!LuaPathX(f, sp.Owner, out owner, out ott) || ott != T_TABLE) { r.Fail("owner", sp.Owner); return; }
      if (!LuaPathX(f, sp.Uids, out list, out ltt) || ltt != T_TABLE) { r.Fail("uids", sp.Uids); return; }
      if (sp.Grid == "*") {   // found by its name: exactly one *_InfinityGrid on the owner, else no read (never a guess)
        var gs = FieldsEnding(owner, "_InfinityGrid", T_UDATA);
        if (gs.Count != 1) { r.Fail("grid", "*_InfinityGrid x" + gs.Count); return; }
        gud = gs[0].Value; gtt = T_UDATA; r.Trace.Append("grid ").Append(gs[0].Key).Append(' ');
      } else if (!Field(owner, sp.Grid, out gud, out gtt) || gtt != T_UDATA) { r.Fail("grid", sp.Grid + " tt " + gtt); return; }
      string gcls; ulong grid = ManagedOf(env, gud, "grid", bi, out gcls);
      if (grid == 0) { r.Fail(bi.FailStep, bi.FailValue); r.Trace.Append(bi.Trace); return; }
      if (gcls.IndexOf("InfinityGrid", StringComparison.Ordinal) < 0) { r.Fail("grid", "class " + gcls); return; }
      bool v2 = sp.V2 || (sp.Grid == "*" && gcls.EndsWith("V2", StringComparison.Ordinal));
      int oFirst = v2 ? 0xF8 : 0xD0;
      if (!v2 && Layout.Current != null) { int o = Layout.Current.FieldOffset("InfinityGrid.m_crtFirstInstanceIndex"); if (o > 0) oFirst = o; }
      var fl = Read(grid + (ulong)oFirst, 8); if (fl != null) { r.First = I32(fl, 0); r.Last = I32(fl, 4); }
      ulong dict = Ptr(grid + (ulong)(oFirst + 8)), vpm = Ptr(grid + (ulong)(oFirst - 8));   // (m_crtItems after the indices, the viewport before them)
      // the form's root and the canvas above it (the scale)
      ulong csv; int ctt; string rcls;
      if (!Field(f, "m_csui", out csv, out ctt) || ctt != T_UDATA) { r.Fail("m_csui", "tt " + ctt); return; }
      ulong rnat = FormRootOf(env, csv, bi, out rcls); if (rnat == 0) { r.Fail(bi.FailStep, bi.FailValue); r.Trace.Append(bi.Trace); return; }
      ulong rgo, rtr; if (!GoAndTransform(rnat, rcls, "root", bi, out rgo, out rtr)) { r.Fail(bi.FailStep, bi.FailValue); r.Trace.Append(bi.Trace); return; }
      var above = new List<ulong>(); bool canvas = CanvasAbove(rtr, W, H, bi, above);
      if (!canvas && U <= 0) { r.Fail("window", "no canvas and no unit"); return; }
      r.Scale = canvas ? "canvas" : "unit";
      // the viewport
      if (vpm == 0 || ClassName(Ptr(vpm)) != "UnityEngine.RectTransform") { r.Fail("viewport", HexA(vpm)); return; }
      ulong vnat = Ptr(vpm + CachedPtr); if (vnat == 0) { r.Fail("viewport", "destroyed"); return; }
      double x, y, w, h;
      if (!RectUnder(vnat, rtr, above, canvas, W, H, U, bi, true, out x, out y, out w, out h)) { r.Fail("viewport " + bi.FailStep, bi.FailValue); return; }
      r.VX = x; r.VY = y; r.VW = w; r.VH = h; r.Rotated = bi.Rotated;
      // the cells made now
      var db = dict != 0 ? Read(dict + 0x18, 12) : null; if (db == null) { r.Fail("m_crtItems", HexA(dict)); return; }
      ulong ents = BitConverter.ToUInt64(db, 0); int cnt = I32(db, 8);
      if (cnt < 0 || cnt > 512 || (cnt > 0 && ents == 0)) { r.Fail("m_crtItems", "count " + cnt); return; }
      int stride = v2 ? 0x28 : 0x20;
      var eb = cnt > 0 ? Read(ents + ArrData, cnt * stride) : new byte[0]; if (eb == null) { r.Fail("m_crtItems", "entries unreadable"); return; }
      for (int i = 0; i < cnt; i++) {
        int o = i * stride; if (I32(eb, o) < 0) continue;   // a free entry
        int idx = I32(eb, o + 8); ulong mgo = BitConverter.ToUInt64(eb, o + 0x18);
        if (idx < 0 || mgo == 0) continue;
        r.Created++;
        if (r.First >= 0 && r.Last >= r.First && (idx < r.First || idx > r.Last)) { r.Fail("m_crtItems", "index " + idx + " outside " + r.First + ".." + r.Last); return; }
        ulong gnat = Ptr(mgo + CachedPtr); if (gnat == 0) continue;
        ulong cgo, ctr; if (!GoAndTransform(gnat, "UnityEngine.GameObject", "cell", new UiButtonInfo(), out cgo, out ctr)) continue;
        for (int col = sp.Rows ? 1 : 0; col <= (sp.Rows ? 6 : 0); col++) {
          long uid = CellUidOf(list, idx, col, sp.Rows, sp.Tables, sp.UidKey);
          if (uid <= 0) { if (sp.Rows) break; continue; }
          if (want != null && !want.Contains(uid)) continue;
          var path = new List<ulong>();
          ulong item = sp.Child == "" ? 0 : ChildNamed(ctr, sp.Rows ? sp.Child + col : sp.Child, 2, path);
          if (item == 0 && sp.Rows) continue;
          ulong at = item != 0 ? item : ctr, pic = sp.Tables ? at : ChildNamed(at, "c_Frame", 4, new List<ulong>());   // (an artifact card is framed whole: the card is the picture)
          string icon = sp.Tables ? (item != 0 ? sp.Child : "cell") : "c_Frame";
          if (pic == 0) { pic = ChildNamed(at, "c_Icon", 4, new List<ulong>()); icon = "c_Icon"; }
          if (pic == 0) { pic = at; icon = item != 0 ? (sp.Rows ? sp.Child + col : sp.Child) : "cell"; }
          var cb = new UiButtonInfo();
          if (!RectUnder(pic, rtr, above, canvas, W, H, U, cb, true, out x, out y, out w, out h)) continue;
          if (cb.Rotated) r.Rotated = true;
          var c = new UiCell { Uid = uid, Index = idx, Col = col, X = x, Y = y, W = w, H = h, Icon = icon, Active = TrActive(ctr) && TrActive(pic) };
          // the whole cell: the cell root (its item child for a row) with the picture - the icon with its stat plate, one frame for all of it
          c.BX = x; c.BY = y; c.BW = w; c.BH = h;
          double ax, ay, aw, ah;
          foreach (ulong root in new[] { at, ctr })   // (the item child, then the cell: a plate that is the cell's own child is in the cell's rect)
            if (root != pic && RectUnder(root, rtr, above, canvas, W, H, U, new UiButtonInfo(), true, out ax, out ay, out aw, out ah) && aw >= 4 && ah >= 4) CellBox(c, ax, ay, aw, ah, sp.Rows && root == at);
          if (sp.Rows) {   // the game's own yellow selection frame (c_EquipSelected, shown on a selected item) is the cell's true rect; never above the picture
            double sx, sy, sw, sh; ulong selT = ChildNamed(at, "c_EquipSelected", 3, new List<ulong>());
            if (!(selT != 0 && RectUnder(selT, rtr, above, canvas, W, H, U, new UiButtonInfo(), true, out sx, out sy, out sw, out sh) && CellSelected(c, sx, sy, sw, sh))) CellClampTop(c);
          }
          if (icon == "c_Frame") { var ai = new UiButtonInfo(); ArtInto(Ptr(pic + CompGo), cb, ai); c.Sprite = ai.Sprite; c.Art = ai.Art; }   // (the quality frame's sprite: its visible part)
          c.InView = InViewOf(c.X, c.Y, c.W, c.H, r.VX, r.VY, r.VW, r.VH);
          r.Cells.Add(c);
        }
      }
      if (r.Trace.Length < 600) r.Trace.Append("cells ").Append(r.Created).Append(" (").Append(r.First).Append("..").Append(r.Last).Append(") kept ").Append(r.Cells.Count);
    }

    /// <summary>The part (0..1) of the rect x, y, w, h inside the viewport vx, vy, vw, vh.</summary>
    public static double InViewOf(double x, double y, double w, double h, double vx, double vy, double vw, double vh) {
      if (w <= 0 || h <= 0) return 0;
      double ix = Math.Max(0, Math.Min(x + w, vx + vw) - Math.Max(x, vx)), iy = Math.Max(0, Math.Min(y + h, vy + vh) - Math.Max(y, vy));
      return ix * iy / (w * h);
    }

    /// <summary>The whole cell <paramref name="c"/>: its picture's rect joined with the cell root's (x, y, w, h); a root wider than 1.6 pictures or
    /// taller than 2.2 (the row's strip, a list's content) is not the cell and is left out.</summary>
    public static void CellBox(UiCell c, double x, double y, double w, double h, bool item = false) {
      if (w > c.W * 1.6 + 2 || h > c.H * (item ? 3.0 : 2.2) + 2) return;   // (item: a list row's own item child, the cell itself: its stat plate may be tall)
      double l = Math.Min(c.X, x), t = Math.Min(c.Y, y), r = Math.Max(c.X + c.W, x + w), b = Math.Max(c.Y + c.H, y + h);
      c.BX = l; c.BY = t; c.BW = r - l; c.BH = b - t;
    }

    /// <summary>The game's selection frame of the cell (x, y, w, h) is the whole cell when it is about the picture's size (0.8..1.6 wide, 0.8..2.2 tall) and holds the
    /// picture's middle; false = it is not usable.</summary>
    public static bool CellSelected(UiCell c, double x, double y, double w, double h) {
      if (w < c.W * 0.8 || w > c.W * 1.6 + 2 || h < c.H * 0.8 || h > c.H * 2.2 + 2) return false;
      double mx = c.X + c.W / 2, my = c.Y + c.H / 2;
      if (mx < x || mx > x + w || my < y || my > y + h) return false;
      c.BX = x; c.BY = y; c.BW = w; c.BH = h;
      return true;
    }

    /// <summary>The cell's box never reaches above its picture (a root rect that stands higher covers the plate of the item above).</summary>
    public static void CellClampTop(UiCell c) {
      if (c.BY >= c.Y) return;
      c.BH -= c.Y - c.BY; c.BY = c.Y;
      if (c.BH < c.H) c.BH = c.H;
    }

    /// <summary>What the read <paramref name="s"/> says of item <paramref name="uid"/> at <paramref name="now"/>: Shown with its frame (the picture
    /// 2 px out, rounded, clipped to the viewport: x, y, w, h, radius), Hidden (no cell of it made, or not half in view, or switched off), or Unknown (no
    /// fresh good read for this window: the layout's estimate). The form on top is the caller's (<see cref="UiCellTopIs"/>).</summary>
    public static UiCellSee UiCellState(UiCellSet s, long uid, double W, double H, int now, out double[] frame, out string why, int maxAge = UiFreshMs) {
      frame = null; why = null;
      if (s == null) { why = "no read"; return UiCellSee.Unknown; }
      if (!s.Ok) { why = "failed at " + s.FailStep; return UiCellSee.Unknown; }
      int age = unchecked(now - s.At);
      if (age < 0 || age > maxAge) { why = "old (" + age + " ms)"; return UiCellSee.Unknown; }
      if (Math.Abs(s.ForW - W) > 0.5 || Math.Abs(s.ForH - H) > 0.5) { why = "another window size"; return UiCellSee.Unknown; }
      if (!s.Visible) { why = "form not shown"; return UiCellSee.Unknown; }
      if (s.Rotated) { why = "rotated"; return UiCellSee.Unknown; }
      if (s.Copies != 1) { why = s.Copies + " shown copies of the form"; return UiCellSee.Unknown; }
      if (s.VW < 4 || s.VH < 4) { why = "no viewport"; return UiCellSee.Unknown; }
      var c = s.Of(uid);
      if (c == null) { why = "no cell made for it (out of view)"; return UiCellSee.Hidden; }
      if (!c.Active) { why = "cell switched off"; return UiCellSee.Hidden; }
      if (c.InView < 0.5) { why = "out of the viewport (" + Math.Round(c.InView * 100) + "% in)"; return UiCellSee.Hidden; }
      if (c.W > W * 0.5 || c.H > H * 0.5 || c.W < 4 || c.H < 4) { why = "size " + N0(c.W) + "x" + N0(c.H); return UiCellSee.Unknown; }
      // one frame on the whole cell (the icon with its stat plate, the card), a rounded rect: never the quality frame's contour alone
      c.Fit = null;
      bool box = c.BW >= 4 && c.BH >= 4;
      var fr = box ? UiIcon.Frame(c.BX, c.BY, c.BW, c.BH, W, H) : UiIcon.Frame(c.X, c.Y, c.W, c.H, W, H);
      double l = Math.Max(fr[0], s.VX - UiIcon.FramePad), t = Math.Max(fr[1], s.VY - UiIcon.FramePad), rr = Math.Min(fr[0] + fr[2], s.VX + s.VW + UiIcon.FramePad), b = Math.Min(fr[1] + fr[3], s.VY + s.VH + UiIcon.FramePad);
      frame = new[] { l, t, rr - l, b - t, fr[4] };
      return UiCellSee.Shown;
    }

    /// <summary>The click point of item <paramref name="uid"/> from the read: its picture's centre when Shown, nearly wholly in view (95%) and inside the client; else false and why.</summary>
    public static bool UiCellPoint(UiCellSet s, long uid, double W, double H, int now, out double x, out double y, out string why) {
      x = y = 0; double[] fr;
      var st = UiCellState(s, uid, W, H, now, out fr, out why);
      if (st != UiCellSee.Shown) { if (why == null) why = st.ToString(); return false; }
      var c = s.Of(uid);
      if (c.InView < 0.95) { why = "partly out of the viewport (" + Math.Round(c.InView * 100) + "% in)"; return false; }
      double cx = c.X + c.W / 2, cy = c.Y + c.H / 2;
      if (cx < 1 || cy < 1 || cx > W - 1 || cy > H - 1) { why = "centre outside the client"; return false; }
      x = cx; y = cy; return true;
    }

    /// <summary>Is the list's form on top of the game now (no pop-up, no modal over it)? Covered = no live frame, no live click.</summary>
    public static bool UiCellTopIs(UiCellKind k, ScreenState s) {
      var sp = SpecOf(k);
      if (sp == null || s == null || s.Held || s.OfferCount > 0 || s.ModalOpen) return false;
      if (k == UiCellKind.HeroGear) return s.Kind == ScreenKind.HeroScreen;   // (the item's tip is the equip helper's own flow form over it)
      if (k == UiCellKind.HeroArt) return s.Kind == ScreenKind.HeroScreen && s.Top == sp.Form;   // (no pop-up: Form_EquipPopUp or the sort form cover the list)
      return s.Top == sp.Form;
    }

    /// <summary>The log line: "uibtn cell: Inventory 123 live=[x,y,w,h] calc=[x,y,w,h] diff=Npx ..." (calc: the layout's rect or point; null = none).</summary>
    public static string UiCellLine(UiCellSet s, long uid, double[] calc) {
      string who = "uibtn cell: " + (s != null ? s.Kind.ToString() : "?") + " " + uid;
      if (s == null) return who + " no read";
      if (!s.Ok) return who + " failed at " + s.FailStep + " (" + s.FailValue + ") in " + s.Ms + " ms";
      var c = s.Of(uid);
      string cs = calc == null ? "none" : calc.Length >= 4 ? N0(calc[0]) + "," + N0(calc[1]) + "," + N0(calc[2]) + "," + N0(calc[3]) : calc.Length >= 2 ? N0(calc[0]) + "," + N0(calc[1]) : "none";
      if (c == null) return who + " live=none (cells " + s.Created + ", " + s.First + ".." + s.Last + ") calc=[" + cs + "] visible=" + (s.Visible ? "T" : "F") + " in " + s.Ms + " ms";
      string d = "";
      if (calc != null && calc.Length >= 2) {
        double lx = c.X + c.W / 2, ly = c.Y + c.H / 2, kx = calc.Length >= 4 ? calc[0] + calc[2] / 2 : calc[0], ky = calc.Length >= 4 ? calc[1] + calc[3] / 2 : calc[1];
        d = " diff=" + N0(Math.Sqrt((lx - kx) * (lx - kx) + (ly - ky) * (ly - ky))) + "px";
      }
      return who + " live=[" + N0(c.X) + "," + N0(c.Y) + "," + N0(c.W) + "," + N0(c.H) + "] calc=[" + cs + "]" + d + " icon=" + c.Icon + " idx=" + c.Index + (c.Col > 0 ? " col=" + c.Col : "")
        + " inview=" + Math.Round(c.InView * 100) + "%" + (c.Active ? "" : " inactive") + " visible=" + (s.Visible ? "T" : "F") + (s.Copies != 1 ? " copies=" + s.Copies : "") + " scale=" + s.Scale + " in " + s.Ms + " ms";
    }

    // ---- the shadow log: the first minute of each pilot run, a line when the item or its place changed (at most one a second)
    sealed class ShadowAt { public int Start, Last; public string Sig = ""; }
    static readonly Dictionary<string, ShadowAt> cellShadow = new Dictionary<string, ShadowAt>();
    public const int UiCellShadowMs = 60000;
    /// <summary>The shadow line due for pilot run <paramref name="run"/> (a key new per run), or null (past its first minute, nothing changed, or within a second).</summary>
    public static string UiCellShadowLine(string run, UiCellSet s, long uid, double[] calc, int now) {
      if (string.IsNullOrEmpty(run) || s == null) return null;
      ShadowAt a;
      lock (cellShadow) {
        if (!cellShadow.TryGetValue(run, out a)) { if (cellShadow.Count > 64) cellShadow.Clear(); a = new ShadowAt { Start = now, Last = unchecked(now - 100000) }; cellShadow[run] = a; }
        if (unchecked(now - a.Start) > UiCellShadowMs || unchecked(now - a.Last) < 1000) return null;
        var c = s.Ok ? s.Of(uid) : null;
        string sig = uid + "|" + (c == null ? (s.Ok ? "none" : s.FailStep) : N0(c.X / 4) + "," + N0(c.Y / 4)) + "|" + (calc != null && calc.Length >= 2 ? N0(calc[0] / 4) + "," + N0(calc[1] / 4) : "");
        if (sig == a.Sig) return null;
        a.Sig = sig; a.Last = now;
      }
      return UiCellLine(s, uid, calc) + " [shadow]";
    }

    // ---- the kept reads (one per list) and the background reads while a cell is framed or a pick is near (one at a time, every tick)
    sealed class KeptCells { public UiCellSet S; public HashSet<long> Want; }
    static readonly Dictionary<UiCellKind, KeptCells> cellLive = new Dictionary<UiCellKind, KeptCells>();
    static int ucBusy;

    /// <summary>One read now of list <paramref name="k"/> for the items <paramref name="want"/>, kept for the frames and clicks. Null = no game.</summary>
    public static UiCellSet UiCellReadNow(UiCellKind k, ICollection<long> want, double W, double H, double U) {
      if (SpecOf(k) == null || UiNoGame()) return null;
      var sw = Stopwatch.StartNew();
      ulong env, ui; string why; UiCellSet r;
      if (!UiRoots(out env, out ui, out why)) { r = new UiCellSet { Kind = k, ForW = W, ForH = H }; r.Fail("lua root", why); }
      else r = UiCellRead(env, ui, k, want, W, H, U);
      r.Ms = sw.ElapsedMilliseconds; r.At = Environment.TickCount;
      UiCellKeep(k, r, want);
      return r;
    }

    public static void UiCellKeep(UiCellKind k, UiCellSet r, ICollection<long> want) {
      if (r == null) return;
      lock (cellLive) cellLive[k] = new KeptCells { S = r, Want = want != null ? new HashSet<long>(want) : null };
    }

    /// <summary>The kept read of list <paramref name="k"/> if it covers item <paramref name="uid"/>, is for this window size and at most <paramref name="maxMs"/> old; else null.</summary>
    public static UiCellSet UiCellFresh(UiCellKind k, long uid, double W, double H, int now, int maxMs) {
      KeptCells kc; lock (cellLive) { if (!cellLive.TryGetValue(k, out kc)) return null; }
      var s = kc.S; if (kc.Want != null && !kc.Want.Contains(uid)) return null;
      if (Math.Abs(s.ForW - W) > 0.5 || Math.Abs(s.ForH - H) > 0.5) return null;
      int age = unchecked(now - s.At);
      return age >= 0 && age <= maxMs ? s : null;
    }

    /// <summary>A cell of list <paramref name="k"/> is framed or picked soon: it is read again in the background (every tick while it is, one read at a time).</summary>
    public static void UiCellWant(UiCellKind k, ICollection<long> want, double W, double H, double U) {
      if (SpecOf(k) == null) return;
      if (Interlocked.CompareExchange(ref ucBusy, 1, 0) != 0) return;
      var w = want != null ? new List<long>(want) : null;
      try {
        Task.Factory.StartNew(() => {
          try { UiCellReadNow(k, w, W, H, U); }
          catch (Exception e) { LL("uibtn cell: failed at exception (" + e.Message + ")"); }
          finally { Interlocked.Exchange(ref ucBusy, 0); }
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
      } catch (Exception) { Interlocked.Exchange(ref ucBusy, 0); }
    }

    /// <summary>For the tests: the kept reads and the shadow minute start over.</summary>
    public static void UiCellReset() { lock (cellLive) cellLive.Clear(); lock (cellShadow) cellShadow.Clear(); }
  }
}
