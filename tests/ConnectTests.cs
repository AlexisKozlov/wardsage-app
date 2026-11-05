// Tests of the pilots' hook-up to the memory readers (C2..C6 of the connect step): the pilots name the widget / cell they press (AutoAction.Widget, CellKind, CellId), the hero filter takes the class id,
// the item pick regrids after a click that hit nothing. Called from CoreTests.Main.
using System;
using System.Collections.Generic;
using RealmForge;

static partial class CoreTests {
  static void ConnectTests() {
    Console.WriteLine("Pilots name their widgets (live clicks):");
    const double W = 1920, H = 1009;
    // the gear filter: open the bar, reset, set / stat / sub cells, arrows
    var fp = new FilterPilot(new FilterGeometry());
    var v = new FilterView { NowMs = 1000, Foreground = true, W = W, H = H, Item = 5, Wanted = true, SetId = 721500, StatId = 13, SetIndex = 0, StatIndex = 1, Suits = new long[0], MainAttrs = new long[0], SubAttrs = new long[0], PanelMem = Mem3.No, SideMem = Mem3.No, SetMem = Mem3.No };
    var a = fp.Step(v);
    Check(a != null && a.Kind == AutoKind.Click && a.Widget == FrameTargets.KeyGearFilter, "the filter bar click names gear.filterBtn");
    var fp2 = new FilterPilot(new FilterGeometry());
    var v2 = new FilterView { NowMs = 1000, Foreground = true, W = W, H = H, Item = 5, Wanted = true, SetId = 721500, StatId = 13, SetIndex = 0, StatIndex = 1, Suits = new long[] { 3 }, MainAttrs = new long[0], SubAttrs = new long[0], PanelMem = Mem3.Yes, SideMem = Mem3.No, SetMem = Mem3.No, SetOrder = new long[] { 721500, 1 } };
    a = fp2.Step(v2);
    Check(a != null && a.Widget == FrameTargets.KeyGearReset, "a wrong set chosen: 'Сбросить' names gear.reset");
    var fp3 = new FilterPilot(new FilterGeometry());
    var v3 = new FilterView { NowMs = 1000, Foreground = true, W = W, H = H, Item = 5, Wanted = true, SetId = 721500, StatId = 13, SetIndex = 0, StatIndex = 1, Suits = new long[0], MainAttrs = new long[0], SubAttrs = new long[0], PanelMem = Mem3.Yes, SideMem = Mem3.No, SetMem = Mem3.No, SetOrder = new long[] { 721500, 1 } };
    a = fp3.Step(v3);
    Check(a != null && a.Widget == FrameTargets.KeyGearSetArrow, "the sets panel closed: the arrow names gear.setArrow");
    v3.NowMs = 3000; v3.Resolved = false; v3.SideMem = Mem3.Yes; v3.SetMem = Mem3.Yes;
    a = fp3.Step(v3);
    Check(a != null && a.Kind == AutoKind.Click && a.CellKind == UiCellKind.GearSuit && a.CellId == 721500, "the set's cell is named by its id (GearSuit 721500)");
    // the hero grid's funnel and the class / faction cells
    var hp = new HeroPilot(new HeroGeometry());
    var heroes = new long[40]; for (int i = 0; i < heroes.Length; i++) heroes[i] = 100 + i;
    var hv = new HeroView { NowMs = 1000, Foreground = true, W = W, H = H, ClientH = H, Plan = heroes[30], Hero = heroes[0], Tab = 3, Part = -1, Heroes = heroes, HeroClass = 2, HeroCamps = new long[] { 7 }, FactionList = new long[] { 0, 5, 7, 9 }, ClassCount = 7, FilterMem = Mem3.No };
    a = hp.Step(hv);
    Check(a != null && a.Kind == AutoKind.Click && a.Widget == FrameTargets.KeyGridFunnel, "the hero grid's funnel names hero.gridFunnel");
    hv.NowMs = 5000; hv.FilterMem = Mem3.Yes; hv.FilterOpen = true;
    a = hp.Step(hv);
    Check(a != null && a.CellKind == UiCellKind.HeroFaction && a.CellId == 7, "the faction row is named by its id (HeroFaction 7)");
    hv.NowMs = 9000; hv.ChosenCamps = new long[] { 7 };
    a = hp.Step(hv);
    Check(a != null && a.CellKind == UiCellKind.HeroClass && a.CellId == 2, "the class row is named by its id (HeroClass 2)");

    Console.WriteLine("Hero grid filter: the chosen class by its id:");
    foreach (long chosen in new long[] { 2, 4 }) {
      var hq = new HeroPilot(new HeroGeometry());
      var hw = new HeroView { NowMs = 1000, Foreground = true, W = W, H = H, ClientH = H, Plan = heroes[30], Hero = heroes[0], Tab = 3, Part = -1, Heroes = heroes, HeroClass = 2, HeroCamps = new long[] { 7 }, FactionList = new long[] { 0, 5, 7, 9 }, ClassCount = 7,
        FilterMem = Mem3.Yes, ClassChosen = true, ChosenClass = chosen, ChosenCamps = new long[] { 7 } };
      var qa = hq.Step(hw);   // (the pop-up open, the faction already chosen: the class decides)
      if (chosen == 2) Check(qa != null && qa.Kind == AutoKind.Click && qa.Widget == FrameTargets.KeyGridFunnel && qa.CellKind == UiCellKind.None, "the grid is filtered by the hero's class already (id 2): no class click, the pop-up closes");
      else Check(qa != null && qa.CellKind == UiCellKind.HeroClass && qa.CellId == HeroPilot.ClassAll, "another class chosen (id 4): «Все» of the class column first");
    }
  }
}
