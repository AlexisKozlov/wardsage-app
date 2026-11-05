// Tests of the live-rect clicks (package P2) (docs/specs/2026-11-05-work-packages.md): the pilots' widget keys (hero.gridFunnel, gear.*), the new cell kinds (GearSuit / GearStat / GearSub /
// HeroClass / HeroFaction) read from the fake game's memory (FakeLua), the click point (centre, scale settled, shrunk box), the gate's key check. Called from CoreTests.Main.
using System;
using System.Collections.Generic;
using RealmForge;

static partial class CoreTests {
  static readonly string[] LiveKeyNames = { "hero.gridFunnel", "gear.filterBtn", "gear.setArrow", "gear.statArrow", "gear.subArrow", "gear.reset", "gear.hideWorn", "gear.sideClose" };

  // the prefab paths the keys stand on (tools/ui-geometry.py --dump Panel_EquipFilterWidget_Charactor | Panel_Equip_Filter | Form_CharactorMain, game 1.6.46), each below the object the form instantiates
  // it in (the widget's root Panel_EquipFilterWidget_Charactor/, Panel_Equip_Filter/, the form's root)
  static readonly string[] LivePrefabPaths = {
    "MainFrame/MainFrame/SuitContent/FilterBtn/m_BtnSuitOff", "MainFrame/MainFrame/SuitContent/FilterBtn/m_BtnSuitOn",
    "MainFrame/MainFrame/MainAttrContent/FilterBtn/m_BtnMainAttrOff", "MainFrame/MainFrame/MainAttrContent/FilterBtn/m_BtnMainAttrOn",
    "MainFrame/MainFrame/SubAttrContent/FilterBtn/m_BtnSubAttrOff", "MainFrame/MainFrame/SubAttrContent/FilterBtn/m_BtnSubAttrOn",
    "MainFrame/MainFrame/BottomContent/m_BtnReset", "MainFrame/MainFrame/BottomContent/m_HideEquipedToggle",
    "FilterFrame/RectMask/m_FilterContent/m_BtnFilterClose",
    "m_BtnFilter/m_FilterOff", "m_BtnFilter/m_FilterOn",
    "m_Main_Frame/m_Left/m_Left_Go/m_FilterRoot/m_Btn_Filter" };

  static FTab LiveList(FakeState st, params long[] v) {
    var kv = new object[v.Length * 2];
    for (int i = 0; i < v.Length; i++) { kv[2 * i] = (long)(i + 1); kv[2 * i + 1] = v[i]; }
    return st.T(kv);
  }

  /// <summary>The path's objects under a form root (names, one child each), the last one a 100 x 40 widget at <paramref name="pos"/> (units from the centre, y up).</summary>
  sealed class LiveWorld {
    public FakeLua M; public FakeUnity Fu; public FakeState St; public FTab Form, UiT;
    public ulong RootT, RootGo, Csui, CsObj, CsGo, LeafT, LeafGo; public readonly List<ulong> Nodes = new List<ulong>();
    public const double W = 1280, H = 720; public double U = RealmForge.Ui.Unit(W, H), K = RealmForge.Ui.Unit(W, H) / 640;
  }

  static LiveWorld MakeLiveWorld(string path, double[] pos) {
    var w = new LiveWorld(); var m = new FakeLua(2 << 20); w.M = m; RFX.TestMemory = m; RFX.ForgetLive(); RFX.ForgetClasses(); RFX.UiCellReset();
    var fu = new FakeUnity(m); var st = new FakeState(m); w.Fu = fu; w.St = st;
    var full = new[] { 0.0, 0 }; var one = new[] { 1.0, 1 }; var half = new[] { 0.5, 0.5 };
    w.RootT = fu.Rect(0, full, one, full, half, full, 1, out w.RootGo);
    ulong parent = w.RootT; var segs = path.Split('/');
    for (int i = 0; i < segs.Length; i++) {
      ulong go, t = i < segs.Length - 1 ? fu.Rect(parent, full, one, full, half, full, 1, out go) : fu.Rect(parent, half, half, new[] { 100.0, 40 }, half, pos, 1, out go);
      fu.Name(go, segs[i]); fu.Kids(parent, t); w.Nodes.Add(t); parent = t; w.LeafT = t; w.LeafGo = go;
    }
    w.Csui = fu.UiBase(w.RootGo, out w.CsObj, out w.CsGo);
    w.Form = st.T("class", st.T("__cname", "Form_CharactorMain"), "m_LuaLogicActive", true, "m_csui", 0L);
    SetField(st, w.Form, "m_csui", w.Csui, 0x47);
    w.UiT = st.T(1L, w.Form);
    return w;
  }

  static void LiveClickTests() {
    Console.WriteLine("Live-rect clicks (package P2: the pilots' widget keys, the filter lists' cells, the host's click point, the gate):");
    try { LiveKeyRegistryTests(); LiveKeyReadTests(); LiveCellKindTests(); LivePointTests(); LiveGateTests(); }
    finally { RFX.TestMemory = null; RFX.ForgetLive(); RFX.ForgetClasses(); RFX.UiCellReset(); }
  }

  static void LiveKeyRegistryTests() {
    var known = new HashSet<string>(LivePrefabPaths); var bad = new List<string>(); int alts = 0;
    foreach (var key in LiveKeyNames) {
      var ft = FrameTargets.Of(key); var w = FrameTargets.Widget(ft);
      if (ft == null || w == null || ft.Form != "Form_CharactorMain" || w.Trees.Length == 0) { bad.Add(key + " (not a widget key of the hero screen)"); continue; }
      foreach (var tree in w.Trees) {
        alts++; string tail = tree;
        foreach (var root in new[] { "Panel_EquipFilterWidget_Charactor/", "Panel_Equip_Filter/" }) { int at = tree.IndexOf(root, StringComparison.Ordinal); if (at >= 0) { tail = tree.Substring(at + root.Length); break; } }
        if (!known.Contains(tail)) bad.Add(key + " (" + tree + ": not a path of the prefab dump)");
      }
    }
    Check(bad.Count == 0, "keys: the 8 widget keys exist and every alternative's path is one of the prefab dump (" + alts + " paths)" + (bad.Count > 0 ? ": " + string.Join("; ", bad.ToArray()) : ""));
    var inAll = new List<string>(); foreach (var t in FrameTargets.All) if (Array.IndexOf(LiveKeyNames, t.Key) >= 0 || t.Key.StartsWith("cell.Gear", StringComparison.Ordinal) || t.Key.StartsWith("cell.HeroClass", StringComparison.Ordinal) || t.Key.StartsWith("cell.HeroFaction", StringComparison.Ordinal)) inAll.Add(t.Key);
    Check(inAll.Count == 0 && FrameTargets.LiveKeys.Count == 14, "keys: the pilots' keys are kept apart from the hint frames (All has none of them), 14 live keys (" + FrameTargets.LiveKeys.Count + ")");
    var kinds = new[] { UiCellKind.GearSuit, UiCellKind.GearStat, UiCellKind.GearSub, UiCellKind.HeroClass, UiCellKind.HeroFaction }; bool cellsOk = true;
    foreach (var k in kinds) { var ft = FrameTargets.Of(FrameTargets.KeyOf(k)); if (ft == null || ft.Cell != k || ft.Form != "Form_CharactorMain" || FrameTargets.Widget(ft) != null) cellsOk = false; }
    Check(cellsOk, "keys: each new cell kind has its key cell.<Kind> on the hero screen's form (a cell, read by UiCellRead, no widget)");
    // the filter widget sits where the UI graph puts it in the form (the prefab of the widget is a sub-instance of m_PanelFilter)
    var g = UiGraph.ByName("Form_CharactorMain"); bool a = false;
    if (g != null) foreach (var gw in g.Widgets)
      if (gw.Path.StartsWith("m_Main_Frame/m_Right/m_PanelContent/m_EquipPanel/EquipPanel/m_Middle/m_PanelFilter/Panel_EquipFilterWidget_Charactor/", StringComparison.Ordinal)) a = true;
    Check(a, "keys: the gear filter widget's place in the hero equip panel is the UI graph's");
  }

  static void LiveKeyReadTests() {
    double[] pos = { 60, -30 }; int worlds = 0;
    foreach (var key in LiveKeyNames) {
      var ft = FrameTargets.Of(key); var wd = FrameTargets.Widget(ft); string last = "";
      for (int ai = 0; ai < wd.Trees.Length; ai++) {
        var lw = MakeLiveWorld(wd.Trees[ai], pos); worlds++;
        var r = RFX.UiButtonRead(lw.Fu.Env, lw.UiT.A, wd, LiveWorld.W, LiveWorld.H, lw.U);
        double cx = LiveWorld.W / 2 + 60 * lw.K, cy = LiveWorld.H / 2 + 30 * lw.K, x, y; double[] box; string why = "";
        bool read = r.Ok && r.Visible && r.Widget.StartsWith("tree:", StringComparison.Ordinal) && Math.Abs(r.X + r.W / 2 - cx) < 0.05 && Math.Abs(r.Y + r.H / 2 - cy) < 0.05 && Math.Abs(r.W - 100 * lw.K) < 0.05;
        bool pt = RFX.UiKeyPoint(r, LiveWorld.W, LiveWorld.H, r.At, out x, out y, out box, out why) && Math.Abs(x - cx) < 0.05 && Math.Abs(y - cy) < 0.05
          && Math.Abs(box[2] - 0.8 * r.W) < 0.01 && Math.Abs(box[3] - 0.8 * r.H) < 0.01 && Math.Abs(box[0] + box[2] / 2 - cx) < 0.01;
        // hidden: the widget's GameObject off
        lw.M.Mem[lw.LeafGo - FakeLua.Base + 0x57] = 0;
        var rh = RFX.UiButtonRead(lw.Fu.Env, lw.UiT.A, wd, LiveWorld.W, LiveWorld.H, lw.U);
        bool hid = !rh.Visible && !RFX.UiKeyPoint(rh, LiveWorld.W, LiveWorld.H, rh.At, out x, out y, out box, out why) && why == "not visible";
        lw.M.Mem[lw.LeafGo - FakeLua.Base + 0x57] = 1;
        // tween: a panel on the way is scaled; the read is fine, the click waits
        int mid = lw.Nodes.Count / 2; WF(lw.M, lw.Fu.Trs(lw.Nodes[mid]) + 0x20, 0.8, 0.8, 1);
        var rt = RFX.UiButtonRead(lw.Fu.Env, lw.UiT.A, wd, LiveWorld.W, LiveWorld.H, lw.U);
        bool tw = rt.Ok && rt.Visible && Math.Abs(rt.ScaleX - 0.8) < 0.001 && !RFX.UiKeyPoint(rt, LiveWorld.W, LiveWorld.H, rt.At, out x, out y, out box, out why) && why.Contains("tweening");
        WF(lw.M, lw.Fu.Trs(lw.Nodes[mid]) + 0x20, 1, 1, 1);
        if (!(read && pt && hid && tw)) last += " alt" + ai + "[read=" + read + " point=" + pt + " hidden=" + hid + " tween=" + tw + (r.Ok ? "" : " " + r.FailStep + " " + r.FailValue) + "]";
      }
      Check(last == "", "key " + key + ": each of its " + wd.Trees.Length + " tree(s) is read live (centre, size), the point is the centre with the 80 % box, hidden = no point, tweening (scale 0.8) = no point" + last);
    }
    // the arrow's Off / On pair: the shown one is read (the side panel open: m_BtnSuitOn shows, Off is off)
    {
      var wd = FrameTargets.Widget(FrameTargets.Of("gear.setArrow")); var lw = MakeLiveWorld(wd.Trees[0], pos); worlds++;
      var half = new[] { 0.5, 0.5 }; ulong gOn; ulong par = lw.Nodes[lw.Nodes.Count - 2];
      ulong on = lw.Fu.Rect(par, half, half, new[] { 100.0, 40 }, half, new[] { -200.0, 50 }, 1, out gOn); lw.Fu.Name(gOn, "m_BtnSuitOn"); lw.Fu.Kids(par, lw.LeafT, on);
      var r1 = RFX.UiButtonRead(lw.Fu.Env, lw.UiT.A, wd, LiveWorld.W, LiveWorld.H, lw.U);
      lw.M.Mem[lw.LeafGo - FakeLua.Base + 0x56] = 0; lw.M.Mem[lw.LeafGo - FakeLua.Base + 0x57] = 0;
      var r2 = RFX.UiButtonRead(lw.Fu.Env, lw.UiT.A, wd, LiveWorld.W, LiveWorld.H, lw.U);
      Check(r1.Ok && r1.Visible && Math.Abs(r1.X + r1.W / 2 - (LiveWorld.W / 2 + 60 * lw.K)) < 0.05 && r2.Ok && r2.Visible && Math.Abs(r2.X + r2.W / 2 - (LiveWorld.W / 2 - 200 * lw.K)) < 0.05 && r2.Widget.EndsWith("m_BtnSuitOn", StringComparison.Ordinal),
        "key gear.setArrow: the Off button while it is shown, the On button once the panel is open (" + r1.Widget + " -> " + r2.Widget + ")");
    }
    // two shown copies of the form: the instance is not certain, no live click
    {
      var wd = FrameTargets.Widget(FrameTargets.Of("gear.reset")); var lw = MakeLiveWorld(wd.Trees[0], pos); worlds++;
      var full = new[] { 0.0, 0 }; var one = new[] { 1.0, 1 }; var half = new[] { 0.5, 0.5 }; ulong g1, o1, m1;
      ulong root2 = lw.Fu.Rect(0, full, one, full, half, full, 1, out g1); ulong cs2 = lw.Fu.UiBase(g1, out o1, out m1);
      var form2 = lw.St.T("class", lw.St.T("__cname", "Form_CharactorMain"), "m_LuaLogicActive", true, "m_csui", 0L); SetField(lw.St, form2, "m_csui", cs2, 0x47);
      var ui2 = lw.St.T(1L, lw.Form, 2L, form2);
      var r = RFX.UiButtonRead(lw.Fu.Env, ui2.A, wd, LiveWorld.W, LiveWorld.H, lw.U); double x, y; double[] box; string why = "";
      Check(r.Ok && r.Copies == 2 && !RFX.UiKeyPoint(r, LiveWorld.W, LiveWorld.H, r.At, out x, out y, out box, out why) && why.Contains("copies"), "key gear.reset: two shown copies of the form - no live click (" + why + ")");
      // an old read, another window size
      var rr = RFX.UiButtonRead(lw.Fu.Env, lw.UiT.A, wd, LiveWorld.W, LiveWorld.H, lw.U);
      Check(RFX.UiKeyPoint(rr, LiveWorld.W, LiveWorld.H, rr.At + 100, out x, out y, out box, out why) && !RFX.UiKeyPoint(rr, LiveWorld.W, LiveWorld.H, rr.At + 300, out x, out y, out box, out why) && why.StartsWith("old")
        && !RFX.UiKeyPoint(rr, 1000, LiveWorld.H, rr.At, out x, out y, out box, out why) && why == "another window size", "key: a read 100 ms old counts, 300 ms old not; another window size not");
      // the hero grid's funnel is on the form's left, found without the filter panel being built (the sub-instance is made at the first press)
      var gf = FrameTargets.Widget(FrameTargets.Of("hero.gridFunnel")); var lf = MakeLiveWorld(gf.Trees[0], pos); worlds++;
      var rf = RFX.UiButtonRead(lf.Fu.Env, lf.UiT.A, gf, LiveWorld.W, LiveWorld.H, lf.U);
      Check(rf.Ok && rf.Visible && gf.Trees[0].EndsWith("m_FilterRoot/m_Btn_Filter", StringComparison.Ordinal), "key hero.gridFunnel: the grid's funnel m_Btn_Filter is read live");
      // a panel that is not there yet (the filter widget made at the first press): a failed read that names the step, no point
      var lp = MakeLiveWorld("m_Main_Frame/m_Left/m_Left_Go/m_FilterRoot/m_Btn_Filter", pos); worlds++;
      var rm = RFX.UiButtonRead(lp.Fu.Env, lp.UiT.A, FrameTargets.Widget(FrameTargets.Of("gear.sideClose")), LiveWorld.W, LiveWorld.H, lp.U);
      Check(!rm.Ok && rm.FailStep == "tree" && !RFX.UiKeyPoint(rm, LiveWorld.W, LiveWorld.H, rm.At, out x, out y, out box, out why) && why.StartsWith("failed at tree"), "key gear.sideClose: the side panel not built yet - the read fails at 'tree', no point (" + why + ")");
    }
    Console.WriteLine("    live keys: " + worlds + " fake worlds read");
  }

  /// <summary>A grid of <paramref name="n"/> pooled cells (one column; 150 x 40 units, 45 apart) in a 300 x 200 viewport at the screen's centre, content top at the viewport's top; each cell root holds the
  /// <paramref name="chain"/> of names (all full size) down to its leaf (140 x 36 at 5, -2 from the cell's corner). Cell i is bound to list index i.</summary>
  sealed class LiveGrid { public ulong Gud, Obj, Vp, Content; public ulong[] Cell, Leaf; }
  static LiveGrid MakeLiveGrid(FakeUnity fu, FakeLua m, ulong root, string[] chain, int n) {
    var g = new LiveGrid { Cell = new ulong[n], Leaf = new ulong[n] };
    var full = new[] { 0.0, 0 }; var half = new[] { 0.5, 0.5 }; var tl = new[] { 0.0, 1 }; var tc = new[] { 0.5, 1 }; ulong gv, gc;
    g.Vp = fu.Rect(root, half, half, new[] { 300.0, 200 }, half, full, 1, out gv);
    g.Content = fu.Rect(g.Vp, tc, tc, new[] { 300.0, 45.0 * n }, tc, new[] { 0.0, 100 }, 1, out gc);
    var keys = new int[n]; var mgo = new ulong[n]; var cells = new ulong[n];
    for (int i = 0; i < n; i++) {
      ulong go; cells[i] = fu.Rect(g.Content, tl, tl, new[] { 150.0, 40 }, tl, new[] { -150.0, -45.0 * i }, 1, out go); g.Cell[i] = cells[i];
      ulong parent = cells[i];
      for (int c = 0; c < chain.Length; c++) {
        ulong cg; bool leaf = c == chain.Length - 1;
        ulong t = leaf ? fu.Rect(parent, tl, tl, new[] { 140.0, 36 }, tl, new[] { 5.0, -2 }, 1, out cg) : fu.Rect(parent, tl, tl, new[] { 150.0, 40 }, tl, full, 1, out cg);
        fu.Name(cg, chain[c]); fu.Kids(parent, t); parent = t; if (leaf) g.Leaf[i] = t;
      }
      mgo[i] = FakeWrap(m, "UnityEngine", "GameObject", go); keys[i] = i;
    }
    fu.Kids(g.Content, cells);
    g.Gud = FakeManaged(fu, "UI", "InfinityGrid", 0x140, out g.Obj);
    m.W64(g.Obj + 0xC8, FakeWrap(m, "UnityEngine", "RectTransform", g.Vp));
    m.W32(g.Obj + 0xD0, 0); m.W32(g.Obj + 0xD4, n - 1);
    m.W64(g.Obj + 0xD8, FakeCellDict(m, 0x20, keys, mgo));
    return g;
  }

  static void LiveCellKindTests() {
    var m = new FakeLua(8 << 20); RFX.TestMemory = m; RFX.ForgetLive(); RFX.ForgetClasses(); RFX.UiCellReset();
    var fu = new FakeUnity(m); var st = new FakeState(m);
    double W = 1280, H = 720, U = RealmForge.Ui.Unit(W, H), k = U / 640, x, y; double[] box; string why = ""; double[] fr;
    var full = new[] { 0.0, 0 }; var one = new[] { 1.0, 1 }; var half = new[] { 0.5, 0.5 }; ulong g0;
    ulong root = fu.Rect(0, full, one, full, half, full, 1, out g0);
    ulong csObj, csGo; ulong csui = fu.UiBase(g0, out csObj, out csGo);
    // the gear filter widget: three grids, the set's cells hold Content/c_Select (grid mode), the stats' c_Select under the cell
    var suit = MakeLiveGrid(fu, m, root, new[] { "Content", "c_Select" }, 6); var stat = MakeLiveGrid(fu, m, root, new[] { "c_Select" }, 6); var sub = MakeLiveGrid(fu, m, root, new[] { "c_Select" }, 6);
    var cls = MakeLiveGrid(fu, m, root, new[] { "c_Button" }, 6); var fac = MakeLiveGrid(fu, m, root, new[] { "c_Button" }, 6);
    var widget = st.T("m_SuitFilterGridProxy", st.T("m_Data", LiveList(st, 101, 102, 103, 104, 105, 106)), "m_MainAttrFilterGridProxy", st.T("m_Data", LiveList(st, 3, 5, 7, 9, 11, 13)),
      "m_SubAttrFilterGridProxy", st.T("m_Data", LiveList(st, 4, 6, 8, 10, 12, 14)), "m_SuitFilterGrid_InfinityGrid", 0L, "m_MainAttrFilterGrid_InfinityGrid", 0L, "m_SubAttrFilterGrid_InfinityGrid", 0L);
    SetField(st, widget, "m_SuitFilterGrid_InfinityGrid", suit.Gud, 0x47); SetField(st, widget, "m_MainAttrFilterGrid_InfinityGrid", stat.Gud, 0x47); SetField(st, widget, "m_SubAttrFilterGrid_InfinityGrid", sub.Gud, 0x47);
    // the hero list's filter pop-up: classes are config rows (m_ProfessionID, -1 = «Все»), factions flat ids (0 = «Все»)
    var rows = new List<object>(); long[] pid = { -1, 1, 2, 4, 8, 16 };
    for (int i = 0; i < pid.Length; i++) { rows.Add((long)(i + 1)); rows.Add(st.T("m_ProfessionID", pid[i], "m_mProfessionName", "c" + i)); }
    var order = st.T("m_MainInfinityGridProxy", st.T("m_Data", st.T(rows.ToArray())), "m_FactionInfinityGridProxy", st.T("m_Data", LiveList(st, 0, 5, 7, 9, 11, 13)),
      "m_MainList_InfinityGrid", 0L, "m_FactionList_InfinityGrid", 0L);
    SetField(st, order, "m_MainList_InfinityGrid", cls.Gud, 0x47); SetField(st, order, "m_FactionList_InfinityGrid", fac.Gud, 0x47);
    var form = st.T("class", st.T("__cname", "Form_CharactorMain"), "m_LuaLogicActive", true, "m_csui", 0L,
      "m_Panels", st.T("EquipPanel", st.T("m_Panel_EquipFilterWidget_Charactor", widget), "Charactor_FilterOrder", order));
    SetField(st, form, "m_csui", csui, 0x47);
    var ui = st.T(1L, form);
    double vx = W / 2 - 150 * k, top = H / 2 - 100 * k;
    // every kind: the ids bound to the cells, the leaf's rect, the point = its centre, a half-visible cell has none, a cell out of the viewport is hidden
    Action<UiCellKind, long[], LiveGrid, string> OneKind = (kind, ids, grid, what) => {
      var s = RFX.UiCellRead(fu.Env, ui.A, kind, null, W, H, U);
      bool ok = s.Ok && s.Created == 6 && s.Cells.Count == 6 && s.Visible && s.Copies == 1 && s.Form == "Form_CharactorMain";
      var c1 = s.Of(ids[1]);
      bool geo = c1 != null && c1.Index == 1 && Math.Abs(c1.X - (vx + 5 * k)) < 0.05 && Math.Abs(c1.Y - (top + 45 * k + 2 * k)) < 0.05 && Math.Abs(c1.W - 140 * k) < 0.05 && Math.Abs(c1.H - 36 * k) < 0.05 && c1.Active && c1.InView > 0.99 && c1.ScaleDev < 0.001;
      bool map = true; for (int i = 0; i < 6; i++) { var c = s.Of(ids[i]); if (c == null || c.Index != i) map = false; }
      Check(ok && geo && map, what + ": 6 cells made, each bound to its id (" + string.Join(",", Array.ConvertAll(ids, v => v.ToString())) + "), cell 1's leaf rect read (" + (s.Ok ? c1 != null ? c1.X + "," + c1.Y + " " + c1.W + "x" + c1.H : "no cell" : s.FailStep + " " + s.FailValue) + ")");
      int now = s.At;
      bool pt = RFX.UiCellKeyPoint(s, ids[1], W, H, now, out x, out y, out box, out why) && Math.Abs(x - (vx + 5 * k + 70 * k)) < 0.05 && Math.Abs(y - (top + 47 * k + 18 * k)) < 0.05
        && Math.Abs(box[2] - 0.8 * 140 * k) < 0.05 && Math.Abs(box[3] - 0.8 * 36 * k) < 0.05;
      Check(pt, what + ": the point is the leaf's centre, the box the leaf's rect shrunk by 20 % (" + why + ")");
      // cell 4: 18 of 36 units in the 200-unit viewport - shown (framed, clipped) but never clicked; cell 5: out of the viewport, hidden
      Check(RFX.UiCellState(s, ids[4], W, H, now, out fr, out why) == UiCellSee.Shown && !RFX.UiCellKeyPoint(s, ids[4], W, H, now, out x, out y, out box, out why) && why.StartsWith("partly out"), what + ": a half-visible cell has no click point (" + why + ")");
      Check(RFX.UiCellState(s, ids[5], W, H, now, out fr, out why) == UiCellSee.Hidden && s.Of(987654) == null && RFX.UiCellState(s, 987654, W, H, now, out fr, out why) == UiCellSee.Hidden, what + ": a cell below the viewport and an id that is in no cell are hidden");
      // tween: the list's content scaled (a panel sliding / growing in): the cell is read, the click waits
      WF(m, fu.Trs(grid.Content) + 0x20, 0.8, 0.8, 1);
      var st2 = RFX.UiCellRead(fu.Env, ui.A, kind, null, W, H, U);
      var t1 = st2.Of(ids[1]);
      Check(t1 != null && t1.ScaleDev > 0.15 && !RFX.UiCellKeyPoint(st2, ids[1], W, H, st2.At, out x, out y, out box, out why) && why.Contains("tweening"), what + ": the list tweening (scale 0.8) - read, no click point (" + why + ")");
      WF(m, fu.Trs(grid.Content) + 0x20, 1, 1, 1);
      // the form not shown / two copies
      SetField(st, form, "m_LuaLogicActive", 0, 1);
      var sh = RFX.UiCellRead(fu.Env, ui.A, kind, null, W, H, U);
      Check(sh.Ok && !sh.Visible && RFX.UiCellState(sh, ids[1], W, H, sh.At, out fr, out why) == UiCellSee.Unknown && !RFX.UiCellKeyPoint(sh, ids[1], W, H, sh.At, out x, out y, out box, out why), what + ": the form not shown - unknown, no point");
      SetField(st, form, "m_LuaLogicActive", 1, 1);
    };
    OneKind(UiCellKind.GearSuit, new long[] { 101, 102, 103, 104, 105, 106 }, suit, "GearSuit");
    OneKind(UiCellKind.GearStat, new long[] { 3, 5, 7, 9, 11, 13 }, stat, "GearStat");
    OneKind(UiCellKind.GearSub, new long[] { 4, 6, 8, 10, 12, 14 }, sub, "GearSub");
    OneKind(UiCellKind.HeroClass, pid, cls, "HeroClass");
    OneKind(UiCellKind.HeroFaction, new long[] { 0, 5, 7, 9, 11, 13 }, fac, "HeroFaction");
    {   // «Все»: the faction id 0 and the class id -1 are cells of their own (a filter's «Все» is no empty row)
      var sc = RFX.UiCellRead(fu.Env, ui.A, UiCellKind.HeroClass, new long[] { -1 }, W, H, U); var sf = RFX.UiCellRead(fu.Env, ui.A, UiCellKind.HeroFaction, new long[] { 0 }, W, H, U);
      Check(sc.Ok && sc.Cells.Count == 1 && sc.Cells[0].Uid == -1 && sc.Cells[0].Index == 0 && sf.Ok && sf.Cells.Count == 1 && sf.Cells[0].Uid == 0, "«Все»: class -1 and faction 0 are read as cells (a wanted list of one)");
    }
    {   // a class row whose id cannot be read (no m_ProfessionID, or not a table): keyed by its list index, never guessed
      var rows2 = new List<object>();
      for (int i = 0; i < 6; i++) { rows2.Add((long)(i + 1)); rows2.Add(i == 2 ? (object)st.T("m_mProfessionName", "x") : (object)st.T("m_ProfessionID", pid[i])); }
      var proxy2 = st.T("m_Data", st.T(rows2.ToArray()));
      // a new panel table with that proxy
      var order2 = st.T("m_MainInfinityGridProxy", proxy2, "m_FactionInfinityGridProxy", st.T("m_Data", LiveList(st, 0)), "m_MainList_InfinityGrid", 0L, "m_FactionList_InfinityGrid", 0L);
      SetField(st, order2, "m_MainList_InfinityGrid", cls.Gud, 0x47); SetField(st, order2, "m_FactionList_InfinityGrid", fac.Gud, 0x47);
      var form2 = st.T("class", st.T("__cname", "Form_CharactorMain"), "m_LuaLogicActive", true, "m_csui", 0L, "m_Panels", st.T("Charactor_FilterOrder", order2)); SetField(st, form2, "m_csui", csui, 0x47);
      var ui2 = st.T(1L, form2);
      var s = RFX.UiCellRead(fu.Env, ui2.A, UiCellKind.HeroClass, null, W, H, U);
      var c2 = s.Ok ? s.OfIndex(2) : null; var c1 = s.Ok ? s.OfIndex(1) : null;
      Check(s.Ok && c2 != null && RFX.IsIndexUid(c2.Uid) && RFX.IndexOfUid(c2.Uid) == 2 && c2.Uid == RFX.IndexUid(2) && s.Of(RFX.IndexUid(2)) == c2 && c1 != null && c1.Uid == pid[1] && !RFX.IsIndexUid(pid[1]) && !RFX.IsIndexUid(-1) && RFX.IndexOfUid(5) == -1,
        "HeroClass: the row without a readable id is keyed by its index " + (c2 != null ? c2.Uid.ToString() : "-") + ", the others by m_ProfessionID");
      var s2 = RFX.UiCellRead(fu.Env, ui2.A, UiCellKind.HeroClass, new long[] { RFX.IndexUid(2) }, W, H, U);
      Check(s2.Ok && s2.Cells.Count == 1 && s2.Cells[0].Index == 2, "HeroClass: a wanted list of the index id finds that cell");
    }
    {   // the panel not built yet (the pop-up is made at the first press of the funnel): the read fails at the owner, no cell
      var form3 = st.T("class", st.T("__cname", "Form_CharactorMain"), "m_LuaLogicActive", true, "m_csui", 0L, "m_Panels", st.T("EquipPanel", st.T("m_Other", 0L))); SetField(st, form3, "m_csui", csui, 0x47);
      var ui3 = st.T(1L, form3);
      var a = RFX.UiCellRead(fu.Env, ui3.A, UiCellKind.GearSuit, null, W, H, U); var b = RFX.UiCellRead(fu.Env, ui3.A, UiCellKind.HeroClass, null, W, H, U);
      Check(!a.Ok && a.FailStep == "owner" && !b.Ok && b.FailStep == "owner" && RFX.UiCellState(a, 101, W, H, a.At, out fr, out why) == UiCellSee.Unknown, "panels not built: the read fails at 'owner' (unknown, never hidden)");
    }
    {   // two shown copies of the form: the instance is not certain
      var f2 = st.T("class", st.T("__cname", "Form_CharactorMain"), "m_LuaLogicActive", true, "m_csui", 0L); ulong g1, o1, m1;
      ulong r2 = fu.Rect(0, full, one, full, half, full, 1, out g1); ulong cs2 = fu.UiBase(g1, out o1, out m1); SetField(st, f2, "m_csui", cs2, 0x47);
      var ui2 = st.T(1L, form, 2L, f2);
      var s = RFX.UiCellRead(fu.Env, ui2.A, UiCellKind.GearSuit, null, W, H, U);
      Check(s.Copies == 2 && RFX.UiCellState(s, 102, W, H, s.At, out fr, out why) == UiCellSee.Unknown && why.Contains("copies"), "two shown copies of the form: unknown (" + why + ")");
    }
    // the screens the cells are clicked on: the hero screen's form on top, no pop-up, no other form over it
    var hs = new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorMain" };
    bool top1 = true, top2 = true;
    foreach (var kind in new[] { UiCellKind.GearSuit, UiCellKind.GearStat, UiCellKind.GearSub, UiCellKind.HeroClass, UiCellKind.HeroFaction }) {
      if (!RFX.UiCellTopIs(kind, hs)) top1 = false;
      if (RFX.UiCellTopIs(kind, new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorEquipTip", Flow = "Form_CharactorEquipTip" }) || RFX.UiCellTopIs(kind, new ScreenState { Kind = ScreenKind.Inventory, Top = "Form_BackpackIntegration" })
        || RFX.UiCellTopIs(kind, new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorMain", OfferCount = 1 }) || RFX.UiCellTopIs(kind, new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorMain", ModalOpen = true })) top2 = false;
    }
    Check(top1 && top2 && RFX.IsHeroFilterKind(UiCellKind.GearSuit) && !RFX.IsHeroFilterKind(UiCellKind.HeroGear), "top: the filter lists are clicked with the hero screen's form on top, not under a tip / offer / modal");
  }

  static void LivePointTests() {
    var ok = new UiButtonInfo { Form = "F", Widget = "w", X = 100, Y = 100, W = 200, H = 50, Visible = true, Copies = 1, ForW = 1280, ForH = 720, At = 5000 };
    double x, y; double[] box; string why = "";
    Check(RFX.UiKeyPoint(ok, 1280, 720, 5000, out x, out y, out box, out why) && x == 200 && y == 125 && box[0] == 120 && box[1] == 105 && box[2] == 160 && box[3] == 40, "point: the centre of the rect, the box 80 % of it (20 % off, 10 % a side)");
    var ib = RFX.InnerBox(0, 0, 100, 10);
    Check(Math.Abs(ib[0] - 10) < 1e-9 && Math.Abs(ib[1] - 1) < 1e-9 && Math.Abs(ib[2] - 80) < 1e-9 && Math.Abs(ib[3] - 8) < 1e-9, "point: InnerBox keeps 80 % of each side");
    Check(RFX.ScaleSettled(1, 1) && RFX.ScaleSettled(1.015, 0.985) && !RFX.ScaleSettled(1.03, 1) && !RFX.ScaleSettled(1, 0.9) && !RFX.ScaleSettled(0, 0), "point: the scale settled within 2 % of 1; 0.9, 1.03 and 0 are a tween");
    ok.ScaleX = 0.5; Check(!RFX.UiKeyPoint(ok, 1280, 720, 5000, out x, out y, out box, out why) && why.Contains("tweening") && x == 0 && y == 0 && box == null, "point: a tweening widget gives no point and no box (" + why + ")");
    ok.ScaleX = 1; ok.Rotated = true; Check(!RFX.UiKeyPoint(ok, 1280, 720, 5000, out x, out y, out box, out why) && why == "rotated", "point: a rotation on the way - none");
    ok.Rotated = false; ok.W = 3; Check(!RFX.UiKeyPoint(ok, 1280, 720, 5000, out x, out y, out box, out why) && why.StartsWith("size"), "point: a 3 px rect is no button (" + why + ")");
    Check(!RFX.UiKeyPoint(null, 1280, 720, 5000, out x, out y, out box, out why) && why == "no read", "point: no read - none");
    // the log line: the live click and the geometry's fallback with its reason
    string l1 = RFX.KeyClickLine("gear.reset", new[] { 100.0, 100, 200, 50 }, true, 200, 125, null), l2 = RFX.KeyClickLine("gear.reset", null, false, 0, 0, "scale 0.8x0.8 (the widget is tweening)"), l3 = RFX.KeyClickLine("gear.reset", new[] { 1.0, 2, 3, 4 }, false, 0, 0, null);
    Check(l1 == "uibtn key: gear.reset src=live @200,125 rect=[100,100,200,50]" && l2 == "uibtn key: gear.reset src=geo why=scale 0.8x0.8 (the widget is tweening)" && l3 == "uibtn key: gear.reset src=geo why=no read rect=[1,2,3,4]", "log: src=live with the point and rect; src=geo why=... when the live rect was not used: " + l2);
  }

  static void LiveGateTests() {
    var hero = new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorMain" };
    Func<string, bool, double, double[], ClickRequest> Rq = (key, live, px, rect) => new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.HeroBack, AutoOn = true, Front = true, WidgetKey = key, Live = live, X = px, Y = 120, LiveRect = rect, W = 1280, H = 720, U = 720 };
    var rc = new[] { 100.0, 100, 200, 40 };   // the box (10 % off each side): x 120..280, y 104..136
    var g = new ClickGate(); var res = g.Check(Rq("hero.gridFunnel", true, 200, rc), hero, 0);
    Check(res.Allowed, "gate: a live key click at the rect's centre passes (" + res.Reason + " " + res.Detail + ")");
    g = new ClickGate(); res = g.Check(Rq("hero.gridFunnel", true, 105, rc), hero, 0);
    Check(!res.Allowed && res.Reason == "target" && res.Detail.Contains("shrunk by 20 %") && g.Halted(ClickOwner.Locate), "gate: a point on the edge strip (x 105 of 100..300) is refused and stops the run (" + res.Detail + ")");
    g = new ClickGate(); res = g.Check(Rq("hero.gridFunnel", true, 285, rc), hero, 0);
    Check(!res.Allowed && res.Reason == "target", "gate: ... the right edge strip too (x 285 of 100..300)");
    g = new ClickGate(); res = g.Check(Rq("hero.gridFunnel", true, 279, rc), hero, 0);
    Check(res.Allowed, "gate: ... a point just inside the box passes");
    g = new ClickGate(); res = g.Check(Rq("no.such.key", true, 200, rc), hero, 0);
    Check(!res.Allowed && res.Reason == "target" && res.Detail.Contains("no widget key"), "gate: a key the targets do not know is refused");
    g = new ClickGate(); res = g.Check(Rq("cell.GearSuit", true, 200, rc), hero, 0);
    Check(!res.Allowed && res.Reason == "target", "gate: a cell key is no widget key");
    g = new ClickGate(); res = g.Check(Rq("gear.reset", true, 200, null), hero, 0);
    Check(!res.Allowed && res.Reason == "target" && res.Detail.Contains("without the widget's live rect"), "gate: a live click with no rect is refused");
    g = new ClickGate(); res = g.Check(Rq("gear.reset", false, 5, null), hero, 0);
    Check(res.Allowed, "gate: a key click on the geometry's point (no live rect) is the geometry's: the point is not held to a rect");
    g = new ClickGate(); res = g.Check(Rq("", true, 5, null), hero, 0);
    Check(res.Allowed, "gate: no key - nothing changes for the old targets");
    var other = new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_Other" };
    g = new ClickGate(); res = g.Check(Rq("gear.reset", true, 200, rc), other, 0);
    Check(!res.Allowed && res.Verdict == GateVerdict.Wait && res.Reason == "screen" && !g.Halted(ClickOwner.Locate), "gate: the key's form not on top - wait, no stop (" + res.Detail + ")");
    var flow = new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorEquipTip", Flow = "Form_CharactorMain,Form_CharactorEquipTip" };
    g = new ClickGate(); res = g.Check(Rq("gear.reset", true, 200, rc), flow, 0);
    Check(res.Allowed, "gate: the key's form a flow form of the screen - on top enough (" + res.Reason + ")");
  }
}
