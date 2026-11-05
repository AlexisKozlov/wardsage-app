// Tests of the hero filter's class / faction reads (package P3) (docs/specs/2026-11-05-work-packages.md): the choice as ids from m_PanelDatas[6]
// (the class config's m_ProfessionID, the factions), the fallback by the class column's row, and «Скрыть надетое» of the gear list.
using System;
using System.Collections.Generic;
using RealmForge;

static partial class CoreTests {
  // the node of an integer key of a fake table: set its value to a userdata handle (type 7 = a script handle with its int at +40)
  static void FakeSetHandle(FakeLua m, FTab t, long key, ulong ud) {
    ulong np = m.R64(t.A + 24); int nn = 1 << m.Mem[t.A - FakeLua.Base + 11];
    for (int i = 0; i < nn; i++) {
      ulong nd = np + (ulong)(32 * i);
      if (m.R64(nd + 16) == (ulong)key && BitConverter.ToInt32(m.Mem, (int)(nd + 24 - FakeLua.Base)) == 0x13) { m.W64(nd, ud); m.W32(nd + 8, 0x47); return; }
    }
    throw new Exception("no key " + key);
  }

  /// <summary>A hero screen form: m_PanelDatas[6] = {[1] = the chosen classes, [3] = the chosen factions} and the filter panel's class column (handles of the
  /// class objects, row 1 = «Все»). classes: a long = an entry that is a table with that m_ProfessionID (-1 = «Все»), a string "cfg:N" = a class object whose id the game's config holds
  /// (handle N, read through HeroClassIdHook), "col:N" = a class object whose id is not readable (the column row decides).</summary>
  static ulong FakeHeroChoiceForm(FakeLua m, FakeState st, object[] classes, long[] camps, int columnRows) {
    var kv = new List<object>();
    var cls = new List<object>(); var handles = new Dictionary<long, ulong>();
    Func<long, ulong> Ud = n => { ulong u; if (!handles.TryGetValue(n, out u)) { u = m.Alloc(64); m.W32(u + 40, (int)n); handles[n] = u; } return u; };
    for (int i = 0; i < classes.Length; i++) { cls.Add((long)(i + 1)); cls.Add(classes[i] is long ? (object)st.T("m_ProfessionID", (long)classes[i]) : (object)0L); }
    var clsT = st.T(cls.ToArray());
    for (int i = 0; i < classes.Length; i++) if (!(classes[i] is long)) FakeSetHandle(m, clsT, i + 1, Ud(long.Parse(((string)classes[i]).Substring(4))));
    var cm = new List<object>(); for (int i = 0; i < camps.Length; i++) { cm.Add((long)(i + 1)); cm.Add(camps[i]); }
    var data = st.T(1L, clsT, 3L, st.T(cm.ToArray()));
    var col = new List<object>();
    for (int r = 1; r <= columnRows; r++) { col.Add((long)r); col.Add(0L); }
    var colT = st.T(col.ToArray());
    for (int r = 1; r <= columnRows; r++) FakeSetHandle(m, colT, r, Ud(1000 + r));   // (the column's own objects: handles 1001...)
    // a chosen "col:N" entry shares its handle with the column's row N - 1000 when N > 1000
    var proxy = st.T("m_Data", colT);
    var panel = st.T("m_MainInfinityGridProxy", proxy);
    var panels = st.T("Charactor_FilterOrder", panel);
    return st.T("m_PanelDatas", st.T(6L, data), "m_Panels", panels).A;
  }

  static void HeroChoiceTests() {
    var prevMem = RFX.TestMemory; var prevHook = RFX.HeroClassIdHook;
    try {
      var m = new FakeLua(1 << 18); RFX.TestMemory = m; var st = new FakeState(m);
      // handle N -> class id: the config objects of the classes (the hook stands for the game's object read)
      RFX.HeroClassIdHook = ud => { int h = BitConverter.ToInt32(m.Mem, (int)(ud + 40 - FakeLua.Base)); return h == 4 ? 4 : h == 16 ? 16 : h == 256 ? 256 : long.MinValue; };
      Func<ulong, RFX.HeroChoice> Rd = f => RFX.ReadHeroChoiceIds(f);

      // «Все»: a table with -1, no factions
      var c = Rd(FakeHeroChoiceForm(m, st, new object[] { -1L }, new long[0], 6));
      Check(c.Read && !c.ClassChosen && c.ClassId == 0 && c.ClassSrc == "all" && c.Camps.Length == 0, "hero choice: «Все» = no class, no factions");
      // a class (a config object): its id from the object
      c = Rd(FakeHeroChoiceForm(m, st, new object[] { "cfg:4" }, new long[0], 6));
      Check(c.ClassChosen && c.ClassId == 4 && c.ClassSrc == "cfg", "hero choice: a class chosen, id " + c.ClassId + " from the config object (" + c.ClassSrc + ")");
      // class + faction
      c = Rd(FakeHeroChoiceForm(m, st, new object[] { "cfg:16" }, new long[] { 103, 107 }, 6));
      Check(c.ClassChosen && c.ClassId == 16 && c.Camps.Length == 2 && c.Camps[0] == 103 && c.Camps[1] == 107, "hero choice: class 16 + factions 103, 107");
      // the faction alone
      c = Rd(FakeHeroChoiceForm(m, st, new object[] { -1L }, new long[] { 105 }, 6));
      Check(!c.ClassChosen && c.ClassId == 0 && c.Camps.Length == 1 && c.Camps[0] == 105, "hero choice: faction 105 only");
      // Tactician (256) is not in the column (it lists «Все» + 5 classes): the id is read from the object all the same
      c = Rd(FakeHeroChoiceForm(m, st, new object[] { "cfg:256" }, new long[0], 6));
      Check(c.ClassChosen && c.ClassId == 256 && c.ClassSrc == "cfg", "hero choice: Tactician read by its object while the column hides it");
      // the id of the object not readable: the column's row decides (handle 1003 = the 3rd row = «Все», class 1, class 2 -> 0-based row 2 -> ClassOrder[1])
      c = Rd(FakeHeroChoiceForm(m, st, new object[] { "col:1003" }, new long[0], 6));
      Check(c.ClassChosen && c.ClassId == HeroGeometry.ClassOrder[1] && c.ClassSrc == "mem(2)", "hero choice fallback: the 3rd row of the column = class " + c.ClassId + " src " + c.ClassSrc);
      // the object is not in the column and not readable: chosen, id unknown (never guessed)
      c = Rd(FakeHeroChoiceForm(m, st, new object[] { "col:77" }, new long[0], 6));
      Check(c.ClassChosen && c.ClassId == 0 && c.ClassSrc == "unknown", "hero choice: unreadable id and no row = chosen, unknown (" + c.ClassSrc + ")");
      // the form without the choice table
      c = Rd(st.T("m_PanelDatas", st.T()).A);
      Check(!c.Read && !c.ClassChosen && c.ClassSrc == "unread", "hero choice: no m_PanelDatas[6] = unreadable");
      long[] camps; bool cc; Check(RFX.ReadHeroChoice(FakeHeroChoiceForm(m, st, new object[] { "cfg:4" }, new long[] { 101 }, 6), out camps, out cc) && cc && camps.Length == 1, "hero choice: the old bool read still answers");

      // the pure decision
      Func<long[], int[], RFX.HeroChoice> D = (ids, rows) => RFX.DecideHeroChoice(new RFX.HeroChoiceFacts { Read = true, EntryIds = ids, EntryRows = rows });
      Check(D(new long[] { -1 }, new int[] { 0 }).ClassChosen == false, "decide: «Все» only");
      Check(D(new long[] { long.MinValue }, new int[] { 6 }).ClassId == HeroGeometry.ClassOrder[5], "decide: the last row = the last class of the order");
      Check(D(new long[] { long.MinValue }, new int[] { HeroGeometry.ClassOrder.Length + 1 }).ClassSrc == "unknown", "decide: a row beyond the order is unknown");
      Check(D(new long[] { long.MinValue }, new int[] { 0 }).ClassSrc == "unknown", "decide: row 0 («Все») with an unreadable id is unknown, not «Все»");
      Check(D(new long[] { 8 }, new int[] { 2 }).ClassId == 8, "decide: the object's id wins over the row");
      Check(!RFX.DecideHeroChoice(null).Read, "decide: nothing read");
    } finally { RFX.TestMemory = prevMem; RFX.HeroClassIdHook = prevHook; }

    // «Скрыть надетое» of the gear list
    try {
      var m = new FakeLua(1 << 16); RFX.TestMemory = m; var st = new FakeState(m);
      Func<bool, bool, EquipAddrs> A = (unused, flagWorn) => {
        var eq = st.T("m_EquipFilterConfigs", st.T("IsEquipedInCharactor", flagWorn, "IsEnhanceInCharactor", false, "IsOnlyExclusiveInCharactor", false));
        return new EquipAddrs { EquipData = eq.A };
      };
      var a1 = A(false, true); Check(RFX.ReadHideWorn(a1) == 1, "hide worn: the saved flag on = 1");
      var a2 = A(false, false); Check(RFX.ReadHideWorn(a2) == 0, "hide worn: the saved flag off = 0");
      var panel = st.T("m_FilterConfig", st.T("IsHideEquiped", true, "Suits", st.T(), "MainAttrs", st.T(), "vSubAttrs", st.T(), "Part", st.T()));
      var a3 = new EquipAddrs { Panel = panel.A, EquipData = A(false, false).EquipData };
      Check(RFX.ReadHideWorn(a3) == 1, "hide worn: the list panel's own filter first (on over the saved off)");
      Check(RFX.ReadHideWorn(null) == -1 && RFX.ReadHideWorn(new EquipAddrs()) == -1, "hide worn: nothing to read = -1");
    } finally { RFX.TestMemory = prevMem; }
  }
}
