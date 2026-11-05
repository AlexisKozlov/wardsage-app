// Tests of the sim's memory model tests/SimMemory.cs (package P6a) (docs/specs/2026-11-05-work-packages.md): the real readers (ReadHeroScreen, ReadHeroChoiceIds, ReadTipMemIn,
// ReadEquipPopupIn, UiCellRead, UiButtonRead) read from the fake memory exactly what the model wrote.
using System;
using System.Collections.Generic;
using RealmForge;

static partial class CoreTests {
  static string SimJoin(long[] a) { return a == null ? "null" : string.Join(",", Array.ConvertAll(a, x => x.ToString())); }

  /// <summary>The hero screen the reader found against the model: null = the same, else the first difference.</summary>
  static string SimHeroDiff(SimMemory w, HeroScreen s) {
    var h = w.Hero;
    bool shown = h.Form == SimForm.Shown;
    if (h.Form == SimForm.Absent) return s.FormOk || s.Hero != w.EquipHero ? "no form: FormOk " + s.FormOk + " hero " + s.Hero : null;
    if (s.Hero != (shown ? h.Hero : 0)) return "hero " + s.Hero + " want " + (shown ? h.Hero : 0);
    if (s.FormOk != shown) return "FormOk " + s.FormOk;
    if (!shown) return null;
    if (s.Tab != h.Tab) return "tab " + s.Tab;
    if (s.SmallCards != h.Small) return "small " + s.SmallCards;
    if (s.Part != h.Part) return "part " + s.Part;
    if (s.Heroes == null || SimJoin(s.Heroes) != SimJoin(h.Grid)) return "heroes " + SimJoin(s.Heroes) + " want " + SimJoin(h.Grid);
    if (s.FilterMem != SimMemory.ExpectFilter(h)) return "filter " + s.FilterMem + " want " + SimMemory.ExpectFilter(h) + " (" + s.FilterGap + ")";
    if (s.Gear.Open != SimMemory.ExpectGear(h)) return "gear " + s.Gear.Open + " want " + SimMemory.ExpectGear(h) + " (" + s.Gear.Why + ")";
    if (s.Gear.Side != SimMemory.ExpectSide(h)) return "gear side " + s.Gear.Side + " want " + SimMemory.ExpectSide(h);
    if (s.Gear.SetPanel != SimMemory.ExpectSetPanel(h)) return "gear sets " + s.Gear.SetPanel + " want " + SimMemory.ExpectSetPanel(h);
    if (s.ClassChosen != (h.ClassId > 0)) return "class chosen " + s.ClassChosen;
    if (s.ChosenClass != h.ClassId) return "class id " + s.ChosenClass + " want " + h.ClassId;
    if (SimJoin(s.ChosenCamps) != SimJoin(h.Camps)) return "camps " + SimJoin(s.ChosenCamps) + " want " + SimJoin(h.Camps);
    bool panel = !h.PanelsNil && (h.Filter == SimSlot.Closed || h.Filter == SimSlot.Open);
    if (panel && h.FactionList != null && SimJoin(s.FactionList) != SimJoin(h.FactionList)) return "factions " + SimJoin(s.FactionList);
    if (panel && s.ClassCount != h.ClassCount) return "class count " + s.ClassCount + " want " + h.ClassCount;
    return null;
  }

  static string SimTipDiff(SimMemory w, TipState s) {
    var t = w.Tip;
    bool shown = t.Form == SimForm.Shown;
    if (s.Shown != shown) return "shown " + s.Shown + " (" + s.Why + ")";
    if (!shown) return s.Kind != TipKind.Unknown || s.SelectUid != 0 || s.Clickable ? "a card not shown still reads facts" : null;
    if (s.Kind != SimMemory.ExpectKind(t)) return "kind " + s.Kind + " want " + SimMemory.ExpectKind(t);
    if (s.View != SimMemory.ExpectView(t)) return "view " + s.View + " want " + SimMemory.ExpectView(t);
    long uid = t.Select != 0 ? t.Select : t.One;
    if (s.SelectUid != uid) return "uid " + s.SelectUid + " want " + uid;
    bool click = SimMemory.ExpectView(t) != TipView.None && t.ButtonActive;
    if (s.Clickable != click) return "clickable " + s.Clickable + " want " + click + " (" + s.Why + ")";
    return null;
  }

  static void SimMemoryTests() {
    Console.WriteLine("The sim's fake game memory read by the real readers (tests/SimMemory.cs):");
    var rnd = new Random(61);
    long[] classes = HeroGeometry.ClassOrder;
    Func<int, long[]> heroIds = n => { var a = new long[n]; for (int i = 0; i < n; i++) a[i] = (200100 + i) * 100000L + 1; return a; };

    // ---- the hero screen: every state of the filter pop-up and of the gear pop-up, both at once
    using (var w = new SimMemory(1280, 720)) {
      var h = w.Hero; h.Hero = 203400001; h.Grid = heroIds(40); h.FactionList = new long[] { 0, 101, 102, 103 };
      int bad = 0, n = 0; string first = null; HeroScreen prev = null;
      foreach (SimSlot f in new[] { SimSlot.Absent, SimSlot.Loading, SimSlot.Closed, SimSlot.Open })
        foreach (SimSlot g in new[] { SimSlot.Absent, SimSlot.Loading, SimSlot.Closed, SimSlot.Open })
          foreach (int tab in new[] { 3, 1 })
            foreach (bool panelsNil in new[] { false, true }) {
              h.Filter = f; h.Gear = g; h.Tab = tab; h.PanelsNil = panelsNil; h.SideStates = new long[] { g == SimSlot.Open ? 1 : 0, 0, 0 };
              w.Sync(); prev = w.ReadHeroScreen(prev); n++;
              string d = SimHeroDiff(w, prev);
              if (d != null) { bad++; if (first == null) first = "filter " + f + " gear " + g + " tab " + tab + " nil " + panelsNil + ": " + d; }
            }
      Check(bad == 0, "hero screen: " + n + " states of the two pop-ups (filter x gear x tab x m_Panels nil) read as the model has them" + (first != null ? " (" + bad + " differ, first " + first + ")" : ""));
      var s = w.ReadHeroScreen(null);
      Check(s.FormOk && s.Hero == 203400001, "hero screen: the plain read finds the form and the hero");
    }

    // ---- the grid, the chosen class and factions, the side panels, the hidden form: a random walk through the model on one world (stale caches would show here)
    using (var w = new SimMemory(1366, 705)) {
      var h = w.Hero; int bad = 0; string first = null; HeroScreen prev = null;
      long[] campIds = { 101, 102, 103, 104, 105, 107, 109 };
      for (int it = 0; it < 400; it++) {
        h.Form = rnd.Next(10) == 0 ? SimForm.Hidden : SimForm.Shown;
        h.Hero = rnd.Next(5) == 0 ? 0 : (200100 + rnd.Next(60)) * 100000L + 1;
        h.Tab = rnd.Next(4); h.Part = rnd.Next(3) == 0 ? rnd.Next(1, 7) : -1; h.Small = rnd.Next(2) == 0;
        if (rnd.Next(3) == 0 || it == 0) { h.Grid = heroIds(rnd.Next(1, 130)); if (rnd.Next(2) == 0) { var sh = heroIds(h.Grid.Length); for (int i = sh.Length - 1; i > 0; i--) { int j = rnd.Next(i + 1); long tmp = sh[i]; sh[i] = sh[j]; sh[j] = tmp; } h.Grid = sh; } }
        h.PanelsNil = rnd.Next(8) == 0;
        h.Filter = (SimSlot)rnd.Next(4); h.Gear = (SimSlot)rnd.Next(4);
        h.ClassId = rnd.Next(3) == 0 ? 0 : classes[rnd.Next(classes.Length)];
        int nc = rnd.Next(3); h.Camps = new long[nc]; for (int i = 0; i < nc; i++) h.Camps[i] = campIds[rnd.Next(campIds.Length)];
        h.FactionList = rnd.Next(4) == 0 ? null : new long[] { 0, 101, 102, 103, 104, 105, 107, 109, 110, 111, 112 };
        h.ClassCount = rnd.Next(2) == 0 ? 6 : 5;
        h.SideStates = new long[] { rnd.Next(2), rnd.Next(2), rnd.Next(2) };
        w.Sync(); var s = w.ReadHeroScreen(prev); prev = s;
        string d = SimHeroDiff(w, s);
        if (d != null) { bad++; if (first == null) first = "iteration " + it + ": " + d; }
      }
      Check(bad == 0, "hero screen: 400 random model states on one world (grid re-sorted, class / factions, side panels, form hidden) read as written" + (first != null ? " (" + bad + " differ, first " + first + ")" : ""));
      // the grid read is kept while the game's list table stays and read again when the game builds a new one (same length, other order)
      h.Form = SimForm.Shown; h.Tab = 3; h.Filter = SimSlot.Closed; h.Gear = SimSlot.Closed; h.PanelsNil = false; h.Hero = 203400001; h.Grid = heroIds(30); w.Sync();
      var a = w.ReadHeroScreen(null); w.Sync(); var b = w.ReadHeroScreen(a);
      Check(b.GridPtr == a.GridPtr && SimJoin(b.Heroes) == SimJoin(h.Grid), "hero grid: the same table, the same heroes (kept)");
      var rev = (long[])h.Grid.Clone(); Array.Reverse(rev); h.Grid = rev; w.Sync(); var c = w.ReadHeroScreen(b);
      Check(c.GridPtr != b.GridPtr && SimJoin(c.Heroes) == SimJoin(rev), "hero grid: a new list table (the game sorted it again) is read again");
      // the choice by itself
      h.ClassId = 8; h.Camps = new long[] { 103, 107 }; w.Sync(); var ch = w.ReadHeroChoice();
      Check(ch.Read && ch.ClassChosen && ch.ClassId == 8 && ch.ClassSrc == "cfg" && SimJoin(ch.Camps) == "103,107", "hero choice: class 8 and factions 103, 107 as written (" + ch + ")");
      h.ClassId = 0; h.Camps = new long[0]; w.Sync(); ch = w.ReadHeroChoice();
      Check(ch.Read && !ch.ClassChosen && ch.ClassSrc == "all" && ch.Camps.Length == 0, "hero choice: «Все» and no factions (" + ch + ")");
    }

    // ---- the item card: every combination of the three info fields, the form's state, the button's activity; the button's rect
    using (var w = new SimMemory(1280, 720)) {
      var t = w.Tip; int bad = 0, n = 0; string first = null;
      foreach (SimForm form in new[] { SimForm.Absent, SimForm.Hidden, SimForm.Shown })
        foreach (long sel in new long[] { 0, 7001 })
          foreach (bool cur in new[] { false, true })
            foreach (long one in new long[] { 0, 7002 })
              foreach (bool act in new[] { true, false }) {
                t.Form = form; t.Select = sel; t.Current = cur; t.One = one; t.ButtonActive = act;
                w.Sync(); n++;
                string d = SimTipDiff(w, w.ReadTip());
                if (d != null) { bad++; if (first == null) first = form + " sel " + sel + " cur " + cur + " one " + one + " active " + act + ": " + d; }
              }
      Check(bad == 0, "item card: " + n + " combinations (form x select x current x one x button active) read as the model has them" + (first != null ? " (" + bad + " differ, first " + first + ")" : ""));
      // the rect of the button: the model's px
      int badRect = 0; string firstRect = null;
      for (int i = 0; i < 40; i++) {
        t.Form = SimForm.Shown; t.Select = 7001; t.Current = i % 2 == 0; t.One = 0; t.ButtonActive = true;
        t.BX = rnd.Next(0, 900); t.BY = rnd.Next(0, 600); t.BW = rnd.Next(40, 250); t.BH = rnd.Next(30, 100);
        w.Sync(); var b = w.ReadTipButton(TipView.Select);
        if (!b.Ok || !b.Visible || Math.Abs(b.X - t.BX) > 0.1 || Math.Abs(b.Y - t.BY) > 0.1 || Math.Abs(b.W - t.BW) > 0.1 || Math.Abs(b.H - t.BH) > 0.1) { badRect++; if (firstRect == null) firstRect = "model " + t.BX + "," + t.BY + " " + t.BW + "x" + t.BH + " read " + b.X + "," + b.Y + " " + b.W + "x" + b.H + " (" + b.FailStep + " " + b.FailValue + ")"; }
      }
      Check(badRect == 0, "item card button: 40 random rects read back in client px as placed" + (firstRect != null ? " (" + badRect + " differ, first " + firstRect + ")" : ""));
      // the pop-up «the artifact is on a hero already»
      string why; bool bp = true; string pw = "";
      foreach (SimForm pf in new[] { SimForm.Absent, SimForm.Hidden, SimForm.Shown }) {
        t.Form = SimForm.Shown; t.Select = 7001; t.Popup = pf; w.Sync();
        bool open = w.ReadPopup(out why);
        if (open != (pf == SimForm.Shown) || (pf == SimForm.Absent && why != "form absent") || (pf == SimForm.Hidden && why != "form hidden")) { bp = false; pw += pf + "=" + open + "/" + why + " "; }
      }
      Check(bp, "equip pop-up: absent / hidden / shown read as the model has them (" + pw + ")");
      // the item card stays readable with the pop-up over it
      t.Popup = SimForm.Shown; w.Sync();
      Check(SimTipDiff(w, w.ReadTip()) == null, "item card under the equip pop-up: unchanged");
    }

    // ---- the inventory's cells: the scroll, the pool, the rects
    using (var w = new SimMemory(1280, 720)) {
      var v = w.Inv; v.Form = SimForm.Shown;
      var uids = new long[60]; for (int i = 0; i < uids.Length; i++) uids[i] = 9000 + i * 3;
      v.Uids = uids; int bad = 0, n = 0; string first = null;
      foreach (double scroll in new double[] { 0, 55, 110, 215, 330, 440, 600 }) {
        v.Scroll = scroll; w.Sync(); var s = w.ReadCells(null); n++;
        var want = new List<int>(); for (int i = 0; i < uids.Length; i++) if (v.Made(i)) want.Add(i);
        string d = null;
        if (!s.Ok) d = "read failed: " + s.FailStep + " " + s.FailValue;
        else if (s.Cells.Count != want.Count) d = s.Cells.Count + " cells, the model made " + want.Count;
        else if (Math.Abs(s.VX - v.VX) > 0.1 || Math.Abs(s.VY - v.VY) > 0.1 || Math.Abs(s.VW - v.VW) > 0.1 || Math.Abs(s.VH - v.VH) > 0.1) d = "viewport " + s.VX + "," + s.VY + " " + s.VW + "x" + s.VH;
        else foreach (int i in want) {
          var c = s.Of(uids[i]); var fr = v.Frame(i);
          if (c == null) { d = "no cell for index " + i; break; }
          if (c.Index != i || Math.Abs(c.X - fr[0]) > 0.1 || Math.Abs(c.Y - fr[1]) > 0.1 || Math.Abs(c.W - fr[2]) > 0.1 || Math.Abs(c.H - fr[3]) > 0.1) { d = "cell " + i + " read " + c.Index + " " + c.X + "," + c.Y + " " + c.W + " model " + fr[0] + "," + fr[1] + " " + fr[2]; break; }
          double inView = RFX.InViewOf(fr[0], fr[1], fr[2], fr[3], v.VX, v.VY, v.VW, v.VH);
          if (Math.Abs(c.InView - inView) > 0.01) { d = "cell " + i + " in view " + c.InView + " model " + inView; break; }
        }
        if (d != null) { bad++; if (first == null) first = "scroll " + scroll + ": " + d; }
      }
      Check(bad == 0, "inventory cells: " + n + " scroll positions, the pooled cells' items, indices and picture rects as the model lays them out" + (first != null ? " (" + bad + " differ, first " + first + ")" : ""));
      // an item with no cell made is not found; the wanted set narrows the read
      v.Scroll = 0; w.Sync(); var s0 = w.ReadCells(new long[] { uids[1], uids[59] });
      Check(s0.Ok && s0.Of(uids[1]) != null && s0.Of(uids[59]) == null && s0.Cells.Count == 1, "inventory cells: the wanted item on screen is read, one far below has no cell");
      // the form shown or hidden
      v.Form = SimForm.Hidden; w.Sync(); var sh = w.ReadCells(null);
      Check(sh.Ok && !sh.Visible, "inventory cells: the form not shown reads as not visible");
      v.Form = SimForm.Absent; w.Sync(); var sa = w.ReadCells(null);
      Check(!sa.Ok && sa.FailStep == "form", "inventory cells: the form gone from the window table: no read (" + sa.FailStep + ")");
    }

    // ---- all forms at once, the window table holding three forms: each reader finds its own
    using (var w = new SimMemory(1920, 1009)) {
      var h = w.Hero; h.Hero = 203400001; h.Grid = heroIds(12); h.Filter = SimSlot.Open; h.ClassId = 4;
      var t = w.Tip; t.Form = SimForm.Shown; t.Select = 7001; t.Current = true; t.Popup = SimForm.Shown;
      var v = w.Inv; v.Form = SimForm.Shown; v.Uids = new long[] { 11, 12, 13, 14, 15, 16, 17 };
      w.Sync();
      var s = w.ReadHeroScreen(null); var tip = w.ReadTip(); string why; var cells = w.ReadCells(null);
      Check(SimHeroDiff(w, s) == null && SimTipDiff(w, tip) == null && w.ReadPopup(out why) && cells.Ok && cells.Cells.Count == 7, "all four forms in the window table at 1920x1009: each reader reads its own (" + SimHeroDiff(w, s) + " " + SimTipDiff(w, tip) + ")");
    }
    Check(RFX.TestMemory == null, "sim memory: the stand-in memory is taken out of RFX again");
  }
}
