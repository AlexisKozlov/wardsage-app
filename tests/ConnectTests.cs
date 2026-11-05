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
  
    Console.WriteLine("Confirm button: the kind from memory, one search:");
    Check(LookPolicy.FirstIsReplace(1) && !LookPolicy.SecondSearch(1, true), "memory: replace - its spot alone when found");
    Check(!LookPolicy.FirstIsReplace(2) && !LookPolicy.SecondSearch(2, true), "memory: equip - its spot alone when found");
    Check(LookPolicy.SecondSearch(1, false) && LookPolicy.SecondSearch(2, false), "the named spot found nothing: the other kind is looked at too (memory may be stale)");
    Check(LookPolicy.FirstIsReplace(0) && LookPolicy.SecondSearch(0, true), "memory unknown: both spots, as before");
 
    ListMemHookTests();
    Lab559RegridTests();
  }

  // ---------------------------------------------------------------- C5: the lists' own scroll (standing, bounds, the drag threshold) in the pilots
  static void ListMemHookTests() {
    Console.WriteLine("Pilots use the lists' memory (standing, threshold, bounds):");
    var geo = new ListGeometry(); double H = 705;
    var types = new int[41]; for (int i = 1; i <= 40; i++) types[i] = 1;
    Func<double, bool, double, AutoView> mk = (thr, moving, now) => new AutoView { NowMs = (long)now, Foreground = true, H = H, Target = 55070, Row = 5, Col = 3, Sel = 54835, AnchorHas = true, Row1Top = 120, Types = types, TipKnown = true, TipShown = false, Hero = 5, ScreenHero = 5, Owner = 0,
      InputOpen = true, LiveGrid = true, LiveTarget = false, LiveFirst = 0, LiveLast = 7, LiveScroll = 54, LiveViewTop = 107, LiveViewBottom = 615, ListSig = 1, ListKnown = true, ListMoving = moving, ListThrPx = thr, ListContentH = 4400, ListViewH = 508 / (geo.PitchY * H / AutoPilot.RowUnits) };
    // the drag is longer by the game's own threshold than by the constant
    double dyOf(double thr) {
      var pg = new AutoPilot(geo);
      for (long t = 0; t < 3000; t += 100) { var a = pg.Step(mk(thr, false, t)); if (a != null && a.Kind == AutoKind.Drag) return a.DY; }
      return double.NaN;
    }
    double d8 = dyOf(0), d30 = dyOf(30);
    Check(!double.IsNaN(d8) && !double.IsNaN(d30) && Math.Abs(Math.Abs(d30) - Math.Abs(d8) - 22) < 0.01, "the drag carries the game's threshold (30 px) instead of the constant 8: " + d8.ToString("0.#") + " -> " + d30.ToString("0.#"));
    // a moving list: no drag for the cap, then the pilot goes on
    {
      var pg = new AutoPilot(geo); long firstDrag = -1;
      for (long t = 0; t < 8000 && firstDrag < 0; t += 100) { var a = pg.Step(mk(0, true, t)); if (a != null && a.Kind == AutoKind.Drag) firstDrag = t; }
      Check(firstDrag >= AutoPilot.MovingCapMs - 200 && firstDrag <= AutoPilot.MovingCapMs + 1200, "a list the game says moves is not dragged for " + AutoPilot.MovingCapMs + " ms (first drag at " + firstDrag + ")");
    }
    {
      var pg = new AutoPilot(geo); long firstDrag = -1;
      for (long t = 0; t < 3000 && firstDrag < 0; t += 100) { var a = pg.Step(mk(0, t < 1000, t)); if (a != null && a.Kind == AutoKind.Drag) firstDrag = t; }
      Check(firstDrag >= 1000 && firstDrag <= 2000, "it stops: the drag follows within a second of the stop (at " + firstDrag + ")");
    }
    // the hero grid: moving = no click, the drag minimum from the threshold
    {
      var hp = new HeroPilot(new HeroGeometry()); var heroes = new long[40]; for (int i = 0; i < heroes.Length; i++) heroes[i] = 100 + i;
      Func<long, bool, HeroView> hvOf = (t, moving) => new HeroView { NowMs = t, Foreground = true, W = 1920, H = 1009, ClientH = 1009, Plan = heroes[30], Hero = heroes[0], Tab = 3, Part = -1, Heroes = heroes, FilterMem = Mem3.No, ListKnown = true, ListMoving = moving, ListThrPx = 20 };
      var a = hp.Step(hvOf(1000, true));
      Check(a == AutoAction.Nothing || (a != null && a.Kind == AutoKind.None), "the hero grid moves: no click, no drag");
      a = hp.Step(hvOf(5000, true));
      Check(a != null && a.Kind != AutoKind.None, "a grid that never reads zero speed holds the pilot for " + HeroPilot.MovingCapMs + " ms only");
      Check(HeroPilot.MinDrag(new HeroView { ListThrPx = 20 }) == 48 && HeroPilot.MinDrag(new HeroView()) == HeroPilot.MinDragPx, "the shortest drag is twice the game's threshold and a margin (else 24)");
    }
    // the filter's side list moves: no cell is clicked
    {
      var fp = new FilterPilot(new FilterGeometry());
      var v = new FilterView { NowMs = 1000, Foreground = true, W = 1920, H = 1009, Item = 5, Wanted = true, SetId = 721500, StatId = 13, SetIndex = 0, StatIndex = 1, Suits = new long[0], MainAttrs = new long[0], SubAttrs = new long[0], PanelMem = Mem3.Yes, SideMem = Mem3.Yes, SetMem = Mem3.Yes, SetOrder = new long[] { 721500, 1 }, SideListMoving = true };
      var a = fp.Step(v);
      Check(a == AutoAction.Nothing, "the sets list moves: no set cell is clicked");
      v.NowMs = 2000; v.Resolved = false; v.SideListMoving = false; a = fp.Step(v);
      Check(a != null && a.Kind == AutoKind.Click && a.CellKind == UiCellKind.GearSuit, "it stands: the set's cell is clicked");
    }
  }

  // ---------------------------------------------------------------- C6: lab 559 - a click on the target's live cell hits nothing (raycast 0x0), the cell's rect is stale
  static void Lab559RegridTests() {
    Console.WriteLine("Lab 559 (a click that hit nothing: regrid, the click on the updated rect):");
    var geo = new ListGeometry(); double H = 705, cell = geo.CellW * H, lx = geo.ColLeft(1) * H + cell / 2;
    foreach (bool rectMoves in new[] { true, false }) {
      var pg = new AutoPilot(geo); double rectY = 148, staleY = 148; int regridHost = 0; var clicks = new List<double>(); int drags = 0, hostRegrids = 0; long sel = 54835; double lastClickY = double.NaN; var log = new List<string>();
      for (long t = 0; t < 30000 && pg.State != "failed" && pg.State != "done"; t += 100) {
        if (pg.RegridSeq != regridHost) { regridHost = pg.RegridSeq; hostRegrids++; if (rectMoves) rectY = 262; }   // the host reads the grid whole: the cell stands elsewhere (or not)
        var v = new AutoView { NowMs = t, Foreground = true, H = H, Target = 55070, Row = 2, Col = 1, Sel = sel, SelRow = 0, SelCol = 0, AnchorHas = true, Row1Top = 120, Types = new[] { 0, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, TipKnown = true, TipShown = false,
          Hero = 5, ScreenHero = 5, Owner = 0, InputOpen = true, LiveGrid = true, LiveTarget = true, LiveX = lx, LiveY = rectY, LiveFirst = 0, LiveLast = 8, LiveCellY = rectY, LiveScroll = 54, LiveViewTop = 107, LiveViewBottom = 615,
          LiveNb = pg.WantNeighbour, LiveNbX = lx, LiveNbY = 300, LiveNbUid = 77, ListSig = 1, RegridDone = hostRegrids };
        if (pg.WantRay && !double.IsNaN(lastClickY)) { v.RayKnown = true; v.RayNone = Math.Abs(lastClickY - staleY) < 2; }   // (the click at the stale rect hit nothing)
        var a = pg.Step(v);
        if (a != null && a.Kind == AutoKind.Click) { clicks.Add(a.Y); lastClickY = a.Y; if (a.Cell == 55070 && Math.Abs(a.Y - 262) < 2) sel = 55070; }
        else if (a != null && a.Kind == AutoKind.Drag) { drags++; if (!rectMoves) rectY = 190; }   // (the list moved by a row: the cell stands elsewhere)
        string at = pg.Atoms.Take(); if (at != "") log.Add(at);
      }
      string all = string.Join(" ## ", log.ToArray());
      if (rectMoves) {
        Check(clicks.Count >= 2 && Math.Abs(clicks[0] - 148) < 2 && Math.Abs(clicks[1] - 262) < 2 && pg.State == "done", "lab 559 regrid: first click at the stale rect (y 148), raycast 0x0, the grid read again, the second at the updated one (y " + (clicks.Count > 1 ? clicks[1].ToString("0") : "-") + "), state " + pg.State + " (" + clicks.Count + " clicks)");
        Check(all.Contains("raycast=0x0 -> regrid #1") && all.Contains("the cell's rect moved [67,148]") && hostRegrids == 1, "lab 559 regrid: the log says 'raycast=0x0 -> regrid' and the rect move (" + hostRegrids + " host regrids)");
      } else {
        Check(pg.BlockerSeq >= 1 && all.Contains("a layer over the list") && drags >= 1 && clicks.Count >= 1 && !(clicks.Count >= 2 && Math.Abs(clicks[1] - clicks[0]) < 2 && drags == 0), "lab 559 regrid: the rect stays the same after the regrid: a layer over the list is named (blocker seq " + pg.BlockerSeq + "), the list is moved (" + drags + " drags), not the same rect clicked again at once");
      }
    }
    // the pointer's bytes: nothing under it / the locker
    var pb = new byte[0x130]; BitConverter.GetBytes(67f).CopyTo(pb, 0xE8); BitConverter.GetBytes(557f).CopyTo(pb, 0xEC);
    var p0 = RFX.PointerOf(pb, 0x1234);
    Check(p0.Ok && p0.HitNothing && !p0.HitsLocker && Math.Abs(p0.X - 67) < 0.01, "the game's pointer with raycast 0x0 = nothing under the click");
    BitConverter.GetBytes((ulong)0x1234).CopyTo(pb, 0x48);
    var p1 = RFX.PointerOf(pb, 0x1234);
    Check(p1.Ok && !p1.HitNothing && p1.HitsLocker, "a raycast on the UILocker's image is the locker, not a hit on a cell");
    Check(!RFX.PointerOf(new byte[10], 0).Ok, "a short buffer is no pointer");
  }
}
