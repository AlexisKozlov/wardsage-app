// Tests of src/CityMem.cs (package P1) (docs/specs/2026-11-05-work-packages.md): the city's buttons and the city on top from the game's memory (a stand-in memory: FakeLua / FakeUnity of CoreTests).
using System;
using System.Collections.Generic;
using RealmForge;

static partial class CoreTests {
  /// <summary>Puts a second component on the GameObject <paramref name="go"/> (after its Transform), as the game's native component list has it.</summary>
  static ulong CityAddComp(FakeUnity fu, ulong go, ulong comp) {
    var m = fu.M; ulong comps = m.Alloc(32); ulong tr = m.R64(m.R64(go + 0x30) + 8);
    m.W64(comps + 8, tr); m.W64(comps + 24, comp); m.W64(go + 0x30, comps); m.W64(go + 0x40, 2);
    return comp;
  }
  /// <summary>A managed Selectable (a Button) over the native component <paramref name="comp"/>: m_CachedPtr 0x10, m_Interactable 0xD0, m_GroupsAllowInteraction 0xE0.</summary>
  static ulong CitySelectable(FakeLua m, ulong comp, bool interactable, bool groups) {
    ulong o = FakeWrap(m, "UnityEngine.UI", "Button", comp); ulong big = m.Alloc(0x100);
    m.W64(big, m.R64(o)); m.W64(big + 0x10, comp); m.Mem[big - FakeLua.Base + 0xD0] = (byte)(interactable ? 1 : 0); m.Mem[big - FakeLua.Base + 0xE0] = (byte)(groups ? 1 : 0);
    return big;
  }
  /// <summary>The game's s_Selectables array (data at 0x20) of the managed objects.</summary>
  static ulong CitySelList(FakeLua m, params ulong[] objs) {
    ulong a = m.Alloc(0x20 + 8 * Math.Max(objs.Length, 1));
    for (int i = 0; i < objs.Length; i++) m.W64(a + 0x20 + (ulong)(8 * i), objs[i]);
    return a;
  }

  static void CityMemTests() {
    Console.WriteLine("The city from memory (src/CityMem.cs):");
    // ---- the city on top: the window table's order
    const string City = "Form_MainCity", Hero = "Form_CharactorMain", Offer = "Form_ActPopupImage", Overlay = "Form_IntegralRewardTip";
    Check(CityMem.OnTopOf(new List<string> { City }) == Mem3.Yes, "city on top: the city alone in the order");
    Check(CityMem.OnTopOf(new List<string> { Offer, City }) == Mem3.No, "city covered: an offer pop-up above it");
    Check(CityMem.OnTopOf(new List<string> { Hero, City }) == Mem3.No, "city covered: another screen above it");
    Check(CityMem.OnTopOf(new List<string> { Overlay, City }) == Mem3.Yes, "city on top: an always-on overlay above it does not cover it");
    Check(CityMem.OnTopOf(new List<string> { Hero }) == Mem3.No && CityMem.OnTopOf(new List<string>()) == Mem3.No, "city not shown at all: No");
    Check(CityMem.OnTopOf(null) == Mem3.Unknown, "no window order: Unknown (the picture decides)");
    Check(CityMem.CityScreen(null, null) == Mem3.Unknown && CityMem.CityScreen(new List<string> { City }, new List<string> { City }) == Mem3.Yes
      && CityMem.CityScreen(new List<string> { City, Hero }, new List<string> { Hero, City }) == Mem3.No && CityMem.CityScreen(new List<string>(), new List<string>()) == Mem3.No
      && CityMem.CityScreen(new List<string> { City }, null) == Mem3.Yes && CityMem.CityScreen(new List<string> { City, Hero }, null) == Mem3.Unknown, "the city is the screen: from the table's forms and order, Unknown without the table");
    // ---- the screen's kind: the bar's picture counts only while the window table is not read
    {
      var tableRead = new ScreenInputs { CitySeen = true, Screens = new List<string>() };
      var noTable = new ScreenInputs { CitySeen = true, Screens = null };
      Check(ScreenState.Classify(tableRead, 1000).Kind == ScreenKind.Unknown && ScreenState.Classify(noTable, 1000).Kind == ScreenKind.City, "screen: the city's picture counts only while the window table is not read (table read, no known form: Unknown)");
      Check(ScreenState.Classify(new ScreenInputs { CitySeen = true, Screens = new List<string> { City } }, 1000).Kind == ScreenKind.City && ScreenState.Classify(new ScreenInputs { CityForm = true, Screens = new List<string>() }, 1000).Kind == ScreenKind.City, "screen: the city's form in the table, or its form flag, is the city");
      Check(!ScreenState.NeedsCityPicture(ScreenKind.City, null, true) && !ScreenState.NeedsCityPicture(ScreenKind.Unknown, null, true) && ScreenState.NeedsCityPicture(ScreenKind.Unknown, null, false) && ScreenState.NeedsCityPicture(ScreenKind.City, null, false), "the bar's picture is wanted only while the window table is not read");
    }
    // ---- Clickable: the rule
    Check(CityMem.Combine(Mem3.Yes, Mem3.Yes, Mem3.Yes, true, true, true) == Mem3.Yes && CityMem.Combine(Mem3.Yes, Mem3.Unknown, Mem3.Yes, true, true, true) == Mem3.Yes, "clickable: shown, on top, input open, Selectable on (or not found): Yes");
    Check(CityMem.Combine(Mem3.No, Mem3.Yes, Mem3.Yes, true, true, false) == Mem3.No && CityMem.Combine(Mem3.Yes, Mem3.No, Mem3.Yes, true, true, true) == Mem3.No && CityMem.Combine(Mem3.Yes, Mem3.Yes, Mem3.No, true, true, true) == Mem3.No
      && CityMem.Combine(Mem3.Yes, Mem3.Yes, Mem3.Yes, false, true, true) == Mem3.No && CityMem.Combine(Mem3.Yes, Mem3.Yes, Mem3.Yes, true, false, true) == Mem3.No, "clickable: hidden / not interactable / covered / input locked / outside the client: each No");
    Check(CityMem.Combine(Mem3.Yes, Mem3.Yes, Mem3.Unknown, true, true, true) == Mem3.Unknown && CityMem.Combine(Mem3.Yes, Mem3.Yes, Mem3.Yes, null, true, true) == Mem3.Unknown && CityMem.Combine(Mem3.Unknown, Mem3.Yes, Mem3.Yes, true, true, false) == Mem3.Unknown, "clickable: no order / input not read / the read failed: Unknown, never closed");
    // ---- the buttons read from the stand-in memory
    var m = new FakeLua(4 << 20); RFX.TestMemory = m; RFX.ForgetLive(); RFX.ForgetClasses(); RFX.ClickableReset();
    try {
      var fu = new FakeUnity(m); var st = new FakeState(m);
      double W = 1280, H = 720, U = Ui.Unit(W, H), k = U / 640, cw = W / k, ch = H / k;
      Func<double, double, bool> Near = (x, y) => Math.Abs(x - y) < 0.6;
      var full = new[] { 0.0, 0 }; var one = new[] { 1.0, 1 }; var half = new[] { 0.5, 0.5 };
      ulong g0, gA, gBar, gH, gI;
      ulong root = fu.Rect(0, full, one, full, half, full, 1, out g0);
      ulong a = fu.Rect(root, full, one, full, half, full, 1, out gA);
      // (the stand-in canvas has no size of its own: positions count from the middle of the client, y up - as the other button tests)
      ulong bar = fu.Rect(a, half, half, new[] { 400.0, 80 }, half, new[] { 0.0, 0 }, 1, out gBar);   // the bottom bar, a 400 x 80 parent that slides with its buttons
      ulong ht = fu.Rect(bar, half, half, new[] { 60.0, 50 }, half, new[] { cw / 2 - 40, 40 - ch / 2 }, 1, out gH);   // «Герои»: centre 40 units from the right and the bottom edge
      ulong it = fu.Rect(bar, half, half, new[] { 60.0, 50 }, half, new[] { cw / 2 - 120, 40 - ch / 2 }, 1, out gI);   // «Инвентарь»: 80 further left
      int iH, iI; ulong csObj, csGo;
      ulong csui = fu.UiBase(g0, out csObj, out csGo);
      ulong heroesUd = fu.Obj("UnityEngine", "GameObject", gH, out iH);   // «Герои»: a GameObject field (the Button sits on it)
      ulong invComp = CityAddComp(fu, gI, fu.Comp(it));                  // «Инвентарь»: a Button field (the component is on the object)
      ulong invUd = fu.Obj("UnityEngine.UI", "Button", invComp, out iI);
      ulong heroesComp = CityAddComp(fu, gH, m.Alloc(0x40)); m.W64(heroesComp + 0x30, gH);
      ulong selH = CitySelectable(m, heroesComp, true, true), selI = CitySelectable(m, invComp, true, true);
      ulong list = CitySelList(m, selH, selI);
      var city = st.T("class", st.T("__cname", "Form_MainCity"), "m_LuaLogicActive", true, "m_csui", 0L, "m_Button_Heros", 0L, "m_Button_Backpack", 0L);
      SetField(st, city, "m_csui", csui, 0x47); SetField(st, city, "m_Button_Heros", heroesUd, 0x47); SetField(st, city, "m_Button_Backpack", invUd, 0x47);
      var ui = st.T(1L, city);
      var onTop = new List<string> { City };
      Func<List<string>, bool?, CityState> Read = (ord, input) => { RFX.ClickableReset(); return RFX.CityReadAt(fu.Env, ui.A, list, 2, true, ord, input, W, H, U); };
      Action<string, CityButton, Mem3, Mem3> Is = (what, b, shown, click) => Check(b.Shown == shown && b.Clickable == click, what + ": shown " + b.Shown + " clickable " + b.Clickable + " (" + b.Why + ")");

      var s = Read(onTop, true);
      Is("city on top, input open: «Герои»", s.Heroes, Mem3.Yes, Mem3.Yes); Is("  «Инвентарь»", s.Inventory, Mem3.Yes, Mem3.Yes);
      Check(s.OnTop == Mem3.Yes && s.Heroes.Selectable == Mem3.Yes && s.Inventory.Selectable == Mem3.Yes, "  the city is on top and both Selectables are on");
      Check(s.Heroes.HasRect && Near(s.Heroes.X + s.Heroes.W / 2, W - 40 * k) && Near(s.Heroes.Y + s.Heroes.H / 2, H - 40 * k) && Near(s.Heroes.W, 60 * k) && Near(s.Heroes.H, 50 * k), "  «Герои»'s live rect: centre 40 units from the corner, 60 x 50 (" + s.Heroes.X + "," + s.Heroes.Y + " " + s.Heroes.W + "x" + s.Heroes.H + ")");
      Check(s.Inventory.HasRect && Near(s.Inventory.X + s.Inventory.W / 2, W - 120 * k) && s.Inventory.X + s.Inventory.W < s.Heroes.X, "  «Инвентарь»'s rect is left of «Герои»'s");
      // the buttons' own click helper (the pilots' clickability): the GameObject alone
      Check(RFX.Clickable(0) == Mem3.Unknown, "RFX.Clickable(go): no object - Unknown");
      string why; RFX.ClickableReset();
      Check(RFX.SelectableOf(gH, list, 2, out why) == Mem3.Yes && RFX.SelectableOf(gI, list, 2, out why) == Mem3.Yes && RFX.SelectableOf(gA, list, 2, out why) == Mem3.Unknown, "RFX.SelectableOf(go): a Selectable on the object, on a component of it, none on another object: Yes / Yes / Unknown (" + why + ")");

      // an offer over the city with the buttons still active: not clickable
      s = Read(new List<string> { Offer, City }, true);
      Is("offer over the city: «Герои»", s.Heroes, Mem3.Yes, Mem3.No); Is("  «Инвентарь»", s.Inventory, Mem3.Yes, Mem3.No);
      Check(s.OnTop == Mem3.No && s.Heroes.Why == "city covered", "  the city is not on top: " + s.Heroes.Why);
      s = Read(new List<string> { Hero, City }, true); Check(s.Heroes.Clickable == Mem3.No, "another screen over the city: not clickable");
      // the input gate
      s = Read(onTop, false); Is("input locked (a transition): «Герои»", s.Heroes, Mem3.Yes, Mem3.No); Check(s.Heroes.Why == "input locked", "  " + s.Heroes.Why);
      s = Read(onTop, null); Is("input not read: «Герои»", s.Heroes, Mem3.Yes, Mem3.Unknown);
      s = Read(null, true); Is("no window order: «Герои»", s.Heroes, Mem3.Yes, Mem3.Unknown); Check(s.OnTop == Mem3.Unknown, "  the city's place is Unknown");
      // the Selectable's flags
      m.Mem[selH - FakeLua.Base + 0xD0] = 0;
      s = Read(onTop, true); Is("m_Interactable off: «Герои»", s.Heroes, Mem3.Yes, Mem3.No); Is("  «Инвентарь» (its own Selectable is on)", s.Inventory, Mem3.Yes, Mem3.Yes); Check(s.Heroes.Why == "not interactable", "  " + s.Heroes.Why);
      m.Mem[selH - FakeLua.Base + 0xD0] = 1; m.Mem[selH - FakeLua.Base + 0xE0] = 0;
      s = Read(onTop, true); Is("m_GroupsAllowInteraction off (a CanvasGroup above): «Герои»", s.Heroes, Mem3.Yes, Mem3.No);
      m.Mem[selH - FakeLua.Base + 0xE0] = 1;
      // a Selectable that is not in the game's list (not enabled / none there): Unknown, never "off"; the rest still decides
      list = CitySelList(m, selI);
      s = Read(onTop, true); Check(s.Heroes.Selectable == Mem3.Unknown && s.Heroes.Clickable == Mem3.Yes && s.Inventory.Clickable == Mem3.Yes, "no Selectable found for «Герои»: Selectable Unknown, still clickable by shown + on top + input (" + s.Heroes.Why + ")");
      list = CitySelList(m, selH, selI);
      // the cache: a found Selectable is followed on the next read (flag flips are seen at once), a destroyed one is searched again
      s = Read(onTop, true); RFX.CityReadAt(fu.Env, ui.A, list, 2, true, onTop, true, W, H, U);
      m.Mem[selH - FakeLua.Base + 0xD0] = 0; s = RFX.CityReadAt(fu.Env, ui.A, list, 2, true, onTop, true, W, H, U);
      Check(s.Heroes.Clickable == Mem3.No, "the Selectable found is followed on the next read without a new search");
      m.Mem[selH - FakeLua.Base + 0xD0] = 1;
      // hidden: the object inactive in the hierarchy
      m.Mem[gH - FakeLua.Base + 0x57] = 0;
      s = Read(onTop, true); Is("button inactive (cached flag 0): «Герои»", s.Heroes, Mem3.No, Mem3.No); Check(!s.Heroes.HasRect, "  no rect");
      m.Mem[gH - FakeLua.Base + 0x57] = 1;
      // the form not shown / not built
      SetField(st, city, "m_LuaLogicActive", 0, 1);
      s = Read(onTop, true); Is("city form not shown: «Герои»", s.Heroes, Mem3.No, Mem3.No);
      SetField(st, city, "m_LuaLogicActive", 1, 1);
      var ui2 = st.T(1L, st.T("class", st.T("__cname", "Form_Other"), "m_LuaLogicActive", true));
      System.Threading.Thread.Sleep(RFX.FormKeepMs + 100);   // (a form read a moment ago stands in for a failed read for FormKeepMs)
      RFX.ClickableReset(); s = RFX.CityReadAt(fu.Env, ui2.A, list, 2, true, onTop, true, W, H, U);
      Is("no city form in the table, the order says it is on top: «Герои»", s.Heroes, Mem3.Unknown, Mem3.Unknown); Check(s.Heroes.Why.StartsWith("form"), "  " + s.Heroes.Why);
      RFX.ClickableReset(); s = RFX.CityReadAt(fu.Env, ui2.A, list, 2, true, new List<string> { Offer }, true, W, H, U);
      Check(s.Heroes.Clickable == Mem3.No, "no city form and the city is not in the order: not clickable");

      // the bar in its CityRebuild animation: it slides in from the right, input locked; only the settled bar is clickable
      WF(m, fu.Trs(bar), 700, 0);
      s = Read(onTop, false); Check(s.Heroes.Clickable == Mem3.No && s.Heroes.Shown == Mem3.Yes, "bar sliding in (rect moved right, input locked): not clickable (" + s.Heroes.Why + ")");
      s = Read(onTop, true); Check(s.Heroes.Clickable == Mem3.No && s.Heroes.Why == "outside the client", "bar still off the screen with the input open: the rect's centre is outside the client, not clickable (" + s.Heroes.Why + ")");
      WF(m, fu.Trs(bar), 0, 0);
      s = Read(onTop, true); Is("bar settled: «Герои»", s.Heroes, Mem3.Yes, Mem3.Yes);
      // a narrow window: the same bar on a 526 x 296 client keeps its place and scale
      {
        double W2 = 526, H2 = 296, U2 = Ui.Unit(W2, H2), k2 = U2 / 640;
        double cw2 = W2 / k2, ch2 = H2 / k2;
        WF(m, fu.Trs(ht), cw2 / 2 - 40, 40 - ch2 / 2); WF(m, fu.Trs(it), cw2 / 2 - 120, 40 - ch2 / 2);
        RFX.ClickableReset(); s = RFX.CityReadAt(fu.Env, ui.A, list, 2, true, onTop, true, W2, H2, U2);
        Check(s.Heroes.Clickable == Mem3.Yes && Near(s.Heroes.X + s.Heroes.W / 2, W2 - 40 * k2) && Near(s.Heroes.Y + s.Heroes.H / 2, H2 - 40 * k2) && Near(s.Heroes.W, 60 * k2),
          "narrow client 526x296: «Герои» clickable at its place (" + s.Heroes.X + "," + s.Heroes.Y + " " + s.Heroes.W + "x" + s.Heroes.H + ", scale " + k2 + ")");
        WF(m, fu.Trs(ht), -W2 / k2 / 2 - 100, 0);   // pushed beyond the left edge
        RFX.ClickableReset(); s = RFX.CityReadAt(fu.Env, ui.A, list, 2, true, onTop, true, W2, H2, U2);
        Check(s.Heroes.Clickable == Mem3.No && s.Heroes.Shown == Mem3.Yes, "narrow client: a button pushed off the window is shown but not clickable (" + s.Heroes.Why + ")");
        WF(m, fu.Trs(ht), cw / 2 - 40, 40 - ch / 2); WF(m, fu.Trs(it), cw / 2 - 120, 40 - ch / 2);
      }
      // no Selectable list at all (the class not found yet): the rest still answers
      RFX.ClickableReset(); s = RFX.CityReadAt(fu.Env, ui.A, 0, 0, false, onTop, true, W, H, U);
      Check(s.Heroes.Selectable == Mem3.Unknown && s.Heroes.Clickable == Mem3.Yes, "no Selectable list: Selectable Unknown, still clickable");
      Check(s.Heroes.Line().StartsWith("yes/yes") && s.Heroes.Line().Contains(" rect="), "the log text: " + s.Heroes.Line());
    } finally { RFX.TestMemory = null; RFX.ForgetLive(); RFX.ForgetClasses(); RFX.ClickableReset(); }
  }
}
