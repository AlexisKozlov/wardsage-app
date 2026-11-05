// Tests of src/ListMem.cs (package P5a) (docs/specs/2026-11-05-work-packages.md): the scroll math (720-grid, V2 heights 100/44/60), the coast model on the recorded series
// (lab e7754bd3, inventory list), the stand predicate, and the readers on a stand-in memory. Called from CoreTests.Main.
using System;
using RealmForge;

static partial class CoreTests {
  static void LmPutF(FakeLua m, ulong a, int off, double v) { Array.Copy(BitConverter.GetBytes((float)v), 0, m.Mem, (int)(a - FakeLua.Base) + off, 4); }
  static void LmPutI(FakeLua m, ulong a, int off, int v) { Array.Copy(BitConverter.GetBytes(v), 0, m.Mem, (int)(a - FakeLua.Base) + off, 4); }
  static void LmPutP(FakeLua m, ulong a, int off, ulong v) { Array.Copy(BitConverter.GetBytes(v), 0, m.Mem, (int)(a - FakeLua.Base) + off, 8); }

  // a ScrollRect: pos, velocity, dragging, the content / view bounds (centre y, height)
  static ulong LmScroll(FakeLua m, double posY, double velY, bool drag, double contentH, double contentCy, double viewH, double viewCy) {
    ulong a = m.Alloc(0x200);
    LmPutI(m, a, 0x24, 1); m.Mem[a - FakeLua.Base + 0x2C] = 1; LmPutF(m, a, 0x30, 0.135); LmPutF(m, a, 0x34, 1);
    LmPutF(m, a, 0x80 + 4, contentCy); LmPutF(m, a, 0x80 + 0xC + 4, contentH / 2);
    LmPutF(m, a, 0x98 + 4, viewCy); LmPutF(m, a, 0x98 + 0xC + 4, viewH / 2);
    LmPutF(m, a, 0xB0 + 4, velY); m.Mem[a - FakeLua.Base + 0xB8] = (byte)(drag ? 1 : 0);
    LmPutF(m, a, 0xBC + 4, posY);
    return a;
  }

  static void ListMemTests() {
    Console.WriteLine("Lists and scrolling from memory (src/ListMem.cs):");
    // ---- the 720-hero grid: 7 columns, row 100, pads 10 / 10, viewport 425
    const double line = 100, pad = 10, view = 425;
    double content = ScrollMath.ContentHeight(720, 7, pad, pad, line);
    Eq(10320.0, content, "720 cells in 7 columns: 103 rows + pads = content height");
    Eq(20.0, ScrollMath.PadTotal(content, 720, 7, line), "pad total read back from the content height");
    Check(ScrollMath.Drag(0, ScrollMath.RowTop(0, pad, line), line, view, content, 8, 1, 1, 20, 0).Needed == false, "cell 0 at the top: already whole in the viewport, no drag");
    var d700 = ScrollMath.Drag(0, ScrollMath.RowTop(ScrollMath.RowOf(700, 7), pad, line), line, view, content, 8, 1, 1, 20, 0);
    Check(d700.Needed && Math.Abs(d700.Target - 9847.5) < 0.01, "cell 700: row 100 goes to the middle (target " + d700.Target + ")");
    Check(Math.Abs(d700.DyPx - (-9847.5 - 8)) < 0.01, "cell 700: the drag goes up by the way plus the threshold (" + d700.DyPx + ")");
    var d719 = ScrollMath.Drag(0, ScrollMath.RowTop(ScrollMath.RowOf(719, 7), pad, line), line, view, content, 8, 1, 1, 20, 0);
    Check(Math.Abs(d719.Target - (content - view)) < 0.01, "the last cell: the target stops at the end of the content");
    var back = ScrollMath.Drag(5000, ScrollMath.RowTop(1, pad, line), line, view, content, 8, 1, 1, 20, 0);
    Check(back.Needed && back.Target == 0 && back.DyPx > 0, "a row near the top from far down: target 0, the drag goes down (positive dy)");
    var cap = ScrollMath.Drag(0, ScrollMath.RowTop(100, pad, line), line, view, content, 8, 1, 1, 20, 300);
    Check(Math.Abs(cap.DyPx - (-308)) < 0.01, "a drag longer than the strip is cut to the span (+ threshold): the next drag does the rest");
    var thr4 = ScrollMath.Drag(0, ScrollMath.RowTop(100, pad, line), line, view, content, 4, 1, 1, 20, 0);
    var thr8 = ScrollMath.Drag(0, ScrollMath.RowTop(100, pad, line), line, view, content, 8, 1, 1, 20, 0);
    Check(Math.Abs((thr4.DyPx - thr8.DyPx) - 4) < 0.01, "the threshold is a parameter (4 px vs 8 px differ by 4), not a constant");
    var g2 = ScrollMath.Drag(0, ScrollMath.RowTop(100, pad, line), line, view, content, 8, 1, 0.5, 20, 0);
    Check(Math.Abs(g2.DyPx) > Math.Abs(thr8.DyPx) * 1.9, "a measured gain of 0.5 doubles the drag");
    Check(double.IsNaN(ScrollMath.Gain(2, 3)) && Math.Abs(ScrollMath.Gain(450, 500) - 0.9) < 1e-9 && double.IsNaN(ScrollMath.Gain(5000, 500)), "gain: too small / out of range = NaN");
    Check(ScrollMath.RowInView(60, 10, 100, 425) > 0.49 && ScrollMath.RowInView(60, 10, 100, 425) < 0.51, "a row half above the viewport is half in view");

    // ---- V2: rows of 100 (item), 44 (title), 60 (empty)
    double c2;
    var hs = new double[] { 44, 100, 100, 100, 44, 100, 60, 100, 100, 44, 100, 100 };
    var tops = ScrollMath.RowTops(hs, 0, 0, out c2);
    Check(tops[0] == 0 && tops[1] == 44 && tops[4] == 344 && tops[6] == 488 && tops[11] == 892 && Math.Abs(c2 - 992) < 0.01, "V2: row tops are sums of the heights (44,100,100,100,44,100,60,...), content " + c2);
    var p2 = ScrollMath.Drag(0, tops[10], hs[10], 300, c2, 8, 1, 1, 20, 0);
    Check(Math.Abs(p2.Target - (c2 - 300)) < 0.01 && p2.DyPx < 0, "V2: a row near the end: the target is clamped to contentH - viewH");
    Check(ScrollMath.Drag(100, tops[2], hs[2], 300, c2, 8, 1, 1, 20, 0).Needed == false, "V2: a row inside the viewport needs no drag");

    // ---- the coast, on the recorded releases (position, velocity while not dragged, the position the list came to rest at)
    double[][] rec = {
      new[] { 1342.2, 11.4, 1347.2 }, new[] { 1344.4, 6.9, 1347.2 }, new[] { 1345.7, 4.3, 1347.2 }, new[] { 1346.5, 2.7, 1347.2 },
      new[] { 1790.9, 12.4, 1796.2 }, new[] { 1793.1, 7.9, 1796.2 }, new[] { 1794.5, 4.9, 1796.2 }, new[] { 1795.5, 2.8, 1796.2 },
      new[] { 2240.1, 9.9, 2244.4 }, new[] { 2242.0, 5.7, 2244.4 }, new[] { 2243.1, 3.5, 2244.4 },
      new[] { 2688.6, 9.1, 2692.5 }, new[] { 3136.6, 10.7, 3141.2 } };
    double worst = 0;
    foreach (var r in rec) worst = Math.Max(worst, Math.Abs(r[0] + ScrollMath.Coast(r[1], 0.135) - r[2]));
    Check(worst < 0.7, "coast model: the rest position is within 0.7 unit of the recorded one on " + rec.Length + " samples (worst " + worst.ToString("0.00") + ")");
    Eq(0.0, ScrollMath.Coast(0.8, 0.135), "a velocity under 1 coasts nowhere");
    Check(ScrollMath.Coast(-11.4, 0.135) < -4.5 && ScrollMath.Coast(-11.4, double.NaN) < -4.5, "a negative velocity coasts back; an unread decel uses the default");
    Check(double.IsNaN(ScrollMath.FinalPos(100, 900, true, 0.135, 5000, 400)), "while dragged the end is unknown");
    Eq(4600.0, ScrollMath.FinalPos(4598, 50, false, 0.135, 5000, 400), "the end is clamped to the content end");
    Check(ScrollMath.VelocityAfter(11.4, 0.135, 0.25) > 6.5 && ScrollMath.VelocityAfter(11.4, 0.135, 0.25) < 7.5 && ScrollMath.VelocityAfter(11.4, 0.135, 2) == 0, "velocity decays by 0.135 per second and is zeroed under 1 (11.4 -> 6.9 in 0.25 s as recorded)");
    double secs = ScrollMath.SecondsToStop(11.4, 0.135);
    Check(secs > 1.0 && secs < 1.5, "a release at 11 units/s stops in about 1.2 s (" + secs.ToString("0.00") + ")");

    // ---- «stands»
    var stand = new ScrollStand();
    var st = new ScrollState { Valid = true, PosY = 1342.2, VelY = 11.4 };
    Check(!stand.Feed(st), "coasting: not standing");
    st.PosY = 1347.2; st.VelY = 0;
    Check(!stand.Feed(st), "first quiet read: not yet");
    Check(stand.Feed(st), "second quiet read at the same position: stands");
    st.Dragging = true; Check(!stand.Feed(st), "dragged again: not standing");
    st.Dragging = false; st.PosY = 1400; Check(!stand.Feed(st) && !stand.Feed(new ScrollState { Valid = true, PosY = 1405, VelY = 0 }), "quiet but the position moved: not standing");
    Check(!stand.Feed(null), "no read: not standing");

    // ---- the readers on a stand-in memory
    var m = new FakeLua(1 << 20); RFX.TestMemory = m; RFX.ForgetDragThreshold();
    try {
      // the inventory list as recorded: pos 898.5, content centre y -2464.5, view centre -212.5
      ulong inv = LmScroll(m, 898.5, 1.0, false, 6726, -2464.5, 425, -212.5);
      var s = RFX.ReadScroll(inv);
      Check(s.Valid && Math.Abs(s.PosY - 898.5) < 0.01 && Math.Abs(s.VelY - 1) < 0.01 && !s.Dragging && s.Movement == 1 && s.Inertia, "scroll: position, velocity, dragging, movement, inertia");
      Check(Math.Abs(s.ContentH - 6726) < 0.5 && Math.Abs(s.ViewH - 425) < 0.5 && Math.Abs(s.ContentHFromCentres - 6726) < 0.5 && Math.Abs(s.Decel - 0.135) < 1e-4, "scroll: content / viewport heights, and the cross-check from the bounds centres");
      // the hero grid as recorded: pos 55, content centre -14432.5, view centre -241.9
      ulong hero = LmScroll(m, 55, 0, false, 28975, -14432.5, 483.8, -241.9);
      var hs0 = RFX.ReadScroll(hero);
      Check(hs0.Valid && Math.Abs(hs0.ContentHFromCentres - 28975) < 0.5 && Math.Abs(hs0.MaxPos - (28975 - 483.8)) < 0.5, "scroll: the hero grid's recorded bounds");
      ulong bad = LmScroll(m, double.NaN, 0, false, 100, 0, 50, 0);
      Check(!RFX.ReadScroll(bad).Valid && !RFX.ReadScroll(0).Valid && !RFX.ReadScroll(FakeLua.Base + (1 << 20) - 8).Valid, "scroll: NaN, null and unreadable are not valid");
      ulong flat = LmScroll(m, 10, 0, false, 100, 0, 0, 0);
      Check(!RFX.ReadScroll(flat).Valid, "scroll: a viewport without height is not valid");

      // an InfinityGrid V1: 720 cells, 7 columns, line 100, cells 14..41
      ulong g1 = m.Alloc(0x200);
      LmPutI(m, g1, 0x30, 7); LmPutF(m, g1, 0x78, 100); LmPutF(m, g1, 0x7C, 100); LmPutI(m, g1, 0x90, 720);
      LmPutP(m, g1, 0xB8, 0x1234); LmPutI(m, g1, 0xD0, 14); LmPutI(m, g1, 0xD4, 41);
      var gs = RFX.ReadGridMem(g1, false);
      Check(gs.Valid && !gs.V2 && gs.Total == 720 && gs.Cols == 7 && gs.RowUnits == 100 && gs.First == 14 && gs.Last == 41 && gs.ScrollRect == 0x1234, "grid V1: total, columns, row, first / last, the scroll object");
      Check(gs.Live(14) && gs.Live(41) && !gs.Live(13) && !gs.Live(42), "grid: only First..Last are live cells");
      LmPutI(m, g1, 0x30, 0); Check(!RFX.ReadGridMem(g1, false).Valid, "grid V1: 0 columns is not valid");
      LmPutI(m, g1, 0x30, 7); LmPutI(m, g1, 0xD4, 900); Check(!RFX.ReadGridMem(g1, false).Valid, "grid V1: the last cell beyond the total is not valid");
      LmPutI(m, g1, 0xD4, 41);

      // the whole list: PlanTo uses the memory threshold when it was read
      var lr = new ListMemRead { Scroll = RFX.ReadScroll(LmScroll(m, 0, 0, false, content, -content / 2, view, -view / 2)), Grid = RFX.ReadGridMem(g1, false), ThresholdPx = 6 };
      var plan = lr.PlanTo(700, pad, 1, 1, 20, 0, 8);
      Check(lr.Has && plan != null && Math.Abs(plan.DyPx - (-9847.5 - 6)) < 0.01, "list plan: the drag carries the game's threshold (6), not the fallback (8): " + plan);
      lr.ThresholdPx = -1; plan = lr.PlanTo(700, pad, 1, 1, 20, 0, 8);
      Check(plan != null && Math.Abs(plan.DyPx - (-9847.5 - 8)) < 0.01, "list plan: threshold unread = the pilot's fallback");

      // an InfinityGridV2 with a height cache (Dictionary<int, Vector2>, entries of 0x14 bytes)
      ulong g2m = m.Alloc(0x200);
      LmPutI(m, g2m, 0xBC, 4); LmPutI(m, g2m, 0xF8, 0); LmPutI(m, g2m, 0xFC, 3); LmPutF(m, g2m, 0xA8, 100); LmPutP(m, g2m, 0xE0, 0x4321);
      ulong dict = m.Alloc(0x40), ents = m.Alloc(0x20 + 5 * 0x14);
      double[] hh = { 44, 100, 100, 60 };
      for (int i = 0; i < 4; i++) { ulong e = ents + 0x20 + (ulong)(i * 0x14); LmPutI(m, e, 0, i); LmPutI(m, e, 4, -1); LmPutI(m, e, 8, i); LmPutF(m, e, 0xC, 500); LmPutF(m, e, 0x10, hh[i]); }
      LmPutP(m, dict, 0x18, ents); LmPutI(m, dict, 0x20, 4); LmPutP(m, g2m, 0xA0, dict);
      var gv2 = RFX.ReadGridMem(g2m, true);
      Check(gv2.Valid && gv2.V2 && gv2.Total == 4 && gv2.Cols == 1 && gv2.DefaultHeight == 100 && gv2.ScrollRect == 0x4321 && gv2.First == 0 && gv2.Last == 3, "grid V2: total at 0xBC, cells 0xF8/0xFC, default height, scroll object");
      Check(gv2.HeightY != null && gv2.HeightY[0] == 44 && gv2.HeightY[3] == 60, "grid V2: the height cache is read by index");
      double cc; Check(RFX.RowTopsV2(gv2, 0, 0, out cc) == null, "grid V2: heights are not offered until the cache is compared with a live list");
      LmPutI(m, ents + 0x20 + 0x14, 0, -1);   // one entry freed: the cache is incomplete
      Check(RFX.ReadGridMem(g2m, true).HeightY == null, "grid V2: an incomplete cache gives no heights");

      // the drag threshold: EventSystem.m_DragThreshold through the static list
      ulong es = m.Alloc(0x80); LmPutI(m, es, 0x34, 8);
      ulong arr = m.Alloc(0x40); LmPutP(m, arr, 0x20, es);
      ulong lst = m.Alloc(0x40); LmPutP(m, lst, 0x10, arr); LmPutI(m, lst, 0x18, 1);
      Eq(8, RFX.DragThresholdFromList(lst), "drag threshold from memory: EventSystem[0].m_DragThreshold");
      LmPutI(m, lst, 0x18, 0); Eq(-1, RFX.DragThresholdFromList(lst), "drag threshold: an empty EventSystem list = unread");
      LmPutI(m, lst, 0x18, 1); LmPutI(m, es, 0x34, 5000); Eq(-1, RFX.DragThresholdFromList(lst), "drag threshold: an implausible value = unread");
      Eq(-1, RFX.DragThresholdOf(0), "drag threshold: no object = unread");
    } finally { RFX.TestMemory = null; RFX.ForgetDragThreshold(); }
  }
}
