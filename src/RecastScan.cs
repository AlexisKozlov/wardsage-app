// RealmForge — the game's recast screens, read from memory (READ-ONLY): the foundry's «Преобразовать» tab and the gem
// screen's recast functions. The «Найти в игре» helper (src/LocateGuide.cs) tells the player where an item is; nothing is
// written to the game and nothing is clicked.
//   foundry form (UIDefines.ID_FORM_EQUIPSUBLIMING):
//     m_LuaLogicActive            true while the form is shown (m_PanelEquipSubliming = the «Вознесение» tab: the same list and filter
//                                 fields as below, plus m_EquipList_ScrollRect / m_EquipList_InfinityGrid for the scroll: RFX.ReadFoundryList)
//     m_curType                   3 = the «Преобразовать» tab
//     m_PanelEquipRecast          the tab's panel:
//       m_IsShow / m_bActive      the panel is on the screen
//       m_Equips                  the uids of the list, one per cell, in screen order
//       m_EquipId                 the item selected now
//       m_SelectParts             the slot switches on the left
//       m_FilterConfig            Suits, MainAttrs, SubAttrs / vSubAttrs (the set, main and sub stat ids chosen)
//   gem screen (UIDefines.ID_FORM_EQUIPGEMMAIN):
//     m_CurrentEquipSelectedUid   the item selected now
//     m_CurrentEquipGemSelectedUid the function selected (7, 8 = recast)
//     m_Panel_EquipFilter.m_AllEquips the item pop-up's list
// Which of the two screens a player gets depends on the account; the field names are checked on the live game.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public sealed class RecastScreen {
    public bool SublimingOpen;                 // the foundry form is shown
    public int CurType = -1;                   // its tab (3 = «Преобразовать»), -1 = unknown
    public bool PanelShown;                    // the recast panel is on the screen
    public RFX.ListRead RecastList;            // how the game scrolls its list (null = unreadable)
    public long[] Equips = new long[0];        // its list in screen order
    public long Sel;                           // item selected there, 0 = none
    public int[] SelectParts = new int[0];     // slot switches on (0..4), empty = all
    public long[] Suits = new long[0], MainAttrs = new long[0], SubAttrs = new long[0];   // its filter
    public bool GemOpen;                       // the gem screen is shown
    public int GemFunc = -1;                   // its selected function (7, 8 = recast)
    public long GemSel;                        // item selected there, 0 = none
    public long[] GemList = new long[0];       // the item pop-up's list
    public RFX.ListRead GemFuncList;           // how the gem screen scrolls its function list (null = unreadable)
    public bool AscendShown;                   // the foundry's «Вознесение» panel (tab 2) is on the screen
    public long[] AscendEquips = new long[0];  // its list in screen order
    public long AscendSel;                     // item selected there, 0 = none
    public int[] AscendParts = new int[0];     // its slot switches on (0..4), empty = all
    public long[] AscendSuits = new long[0], AscendMainAttrs = new long[0], AscendSubAttrs = new long[0];   // its filter
    public RFX.ListRead AscendList;            // how the game scrolls the list (null = unreadable)
    // the Ascension list's filter pop-up (Form_EquipFilterWidget, held by the panel as m_FilterForm while m_FilterWidgetVisible)
    public bool AscendFilterOpen;              // the pop-up is on the screen
    public int FilterPanel;                    // its opened sub panel: 0 none, 1 set, 2 main stat, 3 sub stats (m_ButtonStates)
    public long[] FilterShowSuits = new long[0], FilterLeftSuits = new long[0], FilterRightSuits = new long[0];   // the set grid's list now / the 2-piece / the 3-piece sets
    public RFX.ListRead FilterSuitGrid;        // how the set grid scrolls (null = unreadable)
    public bool ChoosePending;                 // a recast result waits for the choice (blocks selecting another item); not read yet

    public bool OnRecast { get { return SublimingOpen && PanelShown && CurType == 3; } }
    public bool OnAscend { get { return SublimingOpen && AscendShown && CurType == 2; } }
    public bool OnGemRecast { get { return GemOpen && (GemFunc == 7 || GemFunc == 8); } }
  }

  public static partial class RFX {
    /// <summary>The recast screens now; null when the game has neither (never opened) or they cannot be read. A form the
    /// game never built is searched for (a full pass, about a second) at most every <paramref name="rescanMs"/> (&lt; 0: not now).</summary>
    static readonly HashSet<string> saidOnce = new HashSet<string>();
    /// <summary>Logs a failed optional read once per kind and exception type: type, message, top stack frame.</summary>
    public static void SayOnce(string what, Exception e) {
      string top = ""; if (e.StackTrace != null) { var ls = e.StackTrace.Split('\n'); top = ls.Length > 0 ? ls[0].Trim() : ""; }
      string key = what + "|" + e.GetType().Name;
      lock (saidOnce) { if (!saidOnce.Add(key)) return; }
      Log.AppendLine("locate: " + what + ": " + e.GetType().Name + ": " + e.Message + " at " + top);
    }

    /// <summary>Only the scroll of the Foundry's Ascension (<paramref name="ascend"/>) or Recast list, cheap enough to read every overlay
    /// frame (the full screen read is done a few times a second). Null = unreadable.</summary>
    public static ListRead ReadFoundryListLive(bool ascend) {
      try {
        ulong sf = LiveRecastForm(false, -1);
        if (sf == 0) return null;
        ulong panel; int pt;
        if (!Field(sf, ascend ? "m_PanelEquipSubliming" : "m_PanelEquipRecast", out panel, out pt) || pt != T_TABLE) return null;
        return ReadFoundryList(panel);
      } catch (Exception e) { SayOnce("the live list read failed", e); return null; }
    }

    /// <summary>ReadRecastScreen that never throws: a failure is logged once and reads as null (the screen is unknown).</summary>
    public static RecastScreen ReadRecastScreenSafe(int rescanMs) {
      try { return ReadRecastScreen(rescanMs); } catch (Exception e) { SayOnce("the foundry screen read failed", e); return null; }
    }

    public static RecastScreen ReadRecastScreen(int rescanMs) {
      ulong sf = LiveRecastForm(false, rescanMs), gf = LiveRecastForm(true, rescanMs);
      if (sf == 0 && gf == 0) return null;
      var s = new RecastScreen();
      ulong v; int tt;
      if (sf != 0) {
        s.SublimingOpen = Field(sf, "m_LuaLogicActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0;   // a boolean fills 4 bytes
        if (Field(sf, "m_curType", out v, out tt) && tt == T_INT) s.CurType = (int)(long)v;
        ulong panel; int pt;
        if (Field(sf, "m_PanelEquipRecast", out panel, out pt) && pt == T_TABLE) {
          s.PanelShown = (Field(panel, "m_IsShow", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0)
                      || (Field(panel, "m_bActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0);
          ulong t;
          if (s.PanelShown) { try { s.RecastList = ReadFoundryList(panel); } catch (Exception e) { s.RecastList = null; SayOnce("the Foundry list read failed", e); } }
          if (Field(panel, "m_Equips", out t, out tt) && tt == T_TABLE) s.Equips = OrderedInts(t);
          if (Field(panel, "m_EquipId", out v, out tt) && tt == T_INT && (long)v > 0) s.Sel = (long)v;
          if (Field(panel, "m_SelectParts", out t, out tt) && tt == T_TABLE) {
            var parts = new List<int>(); foreach (var x in IntSet(t)) if (x >= 0 && x <= 4) parts.Add((int)x);
            s.SelectParts = parts.ToArray();
          }
          ulong fc;
          if (Field(panel, "m_FilterConfig", out fc, out tt) && tt == T_TABLE) {
            if (Field(fc, "Suits", out t, out tt) && tt == T_TABLE) s.Suits = IntSet(t);
            if (Field(fc, "MainAttrs", out t, out tt) && tt == T_TABLE) s.MainAttrs = IntSet(t);
            if (Field(fc, "vSubAttrs", out t, out tt) && tt == T_TABLE) s.SubAttrs = IntSet(t);
            else if (Field(fc, "SubAttrs", out t, out tt) && tt == T_TABLE) s.SubAttrs = IntSet(t);
          }
        }
      }
      if (sf != 0) {
        // the «Вознесение» tab's panel has the same list fields as the recast panel (read-only, no filter is read)
        ulong ap, t; int apt;
        if (Field(sf, "m_PanelEquipSubliming", out ap, out apt) && apt == T_TABLE) {
          s.AscendShown = (Field(ap, "m_IsShow", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0)
                       || (Field(ap, "m_bActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0);
          if (Field(ap, "m_Equips", out t, out tt) && tt == T_TABLE) s.AscendEquips = OrderedInts(t);
          if (Field(ap, "m_EquipId", out v, out tt) && tt == T_INT && (long)v > 0) s.AscendSel = (long)v;
          if (Field(ap, "m_SelectParts", out t, out tt) && tt == T_TABLE) {
            var parts = new List<int>(); foreach (var x in IntSet(t)) if (x >= 0 && x <= 4) parts.Add((int)x);
            s.AscendParts = parts.ToArray();
          }
          ulong fc;
          if (Field(ap, "m_FilterConfig", out fc, out tt) && tt == T_TABLE) {
            if (Field(fc, "Suits", out t, out tt) && tt == T_TABLE) s.AscendSuits = IntSet(t);
            if (Field(fc, "MainAttrs", out t, out tt) && tt == T_TABLE) s.AscendMainAttrs = IntSet(t);
            if (Field(fc, "vSubAttrs", out t, out tt) && tt == T_TABLE) s.AscendSubAttrs = IntSet(t);
            else if (Field(fc, "SubAttrs", out t, out tt) && tt == T_TABLE) s.AscendSubAttrs = IntSet(t);
          }
          if (s.AscendShown) { try { s.AscendList = ReadFoundryList(ap); } catch (Exception e) { s.AscendList = null; SayOnce("the Ascension list read failed", e); } }
          // optional: the pop-up's fields are absent while it is closed / on other tabs; whatever happens here never costs the foundry's state
          try { ReadFilterPopup(ap, s); } catch (Exception e) { s.AscendFilterOpen = false; s.FilterPanel = 0; SayOnce("the filter pop-up read failed", e); }
        }
      }
      if (gf != 0) {
        s.GemOpen = Field(gf, "m_LuaLogicActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0;
        if (Field(gf, "m_CurrentEquipGemSelectedUid", out v, out tt) && tt == T_INT) s.GemFunc = (int)(long)v;
        if (Field(gf, "m_CurrentEquipSelectedUid", out v, out tt) && tt == T_INT && (long)v > 0) s.GemSel = (long)v;
        ulong pop, t; int pt;
        if (Field(gf, "m_Panel_EquipFilter", out pop, out pt) && pt == T_TABLE && Field(pop, "m_AllEquips", out t, out tt) && tt == T_TABLE) s.GemList = OrderedInts(t);
        // optional: the function list's scroll (unreadable = null); never makes the gem screen or the foundry unreadable
        if (s.GemOpen && live != null) { try { s.GemFuncList = ReadList(gf, "m_GemInfinityGrid_ScrollRect", "m_GemInfinityGrid_InfinityGrid", live.Pid); } catch (Exception e) { s.GemFuncList = null; SayOnce("the gem function list read failed", e); } }
      }
      return s;
    }

    // the filter pop-up of the Ascension panel: open flag, the opened sub panel, the set lists and the set grid's scroll
    static void ReadFilterPopup(ulong ap, RecastScreen s) {
      ulong v, form, pw, t; int tt;
      if (!Field(ap, "m_FilterWidgetVisible", out v, out tt) || tt != T_BOOL || (v & 0xFFFFFFFF) == 0) return;
      if (!Field(ap, "m_FilterForm", out form, out tt) || tt != T_TABLE) return;
      if (!Field(form, "m_PanelFilterWidget", out pw, out tt) || tt != T_TABLE) return;
      s.AscendFilterOpen = true;
      if (Field(pw, "m_ButtonStates", out t, out tt) && tt == T_TABLE) {
        var st = OrderedInts(t);
        for (int i = 0; i < st.Length; i++) if (st[i] == 1) { s.FilterPanel = i + 1; break; }
      }
      if (Field(pw, "m_ShowSuitIds", out t, out tt) && tt == T_TABLE) s.FilterShowSuits = OrderedInts(t);
      if (Field(pw, "m_LeftSuitList", out t, out tt) && tt == T_TABLE) s.FilterLeftSuits = OrderedInts(t);
      if (Field(pw, "m_RightSuitList", out t, out tt) && tt == T_TABLE) s.FilterRightSuits = OrderedInts(t);
      if (s.FilterPanel == 1 && live != null) s.FilterSuitGrid = ReadList(pw, "m_SuitFilterGrid_ScrollRect", "m_SuitFilterGrid_InfinityGrid", live.Pid);
    }

    // an array of numbers (keys 1..n) in order
    static long[] OrderedInts(ulong t) {
      var d = ParseTable(t, 0, new HashSet<ulong>());
      var r = new List<long>();
      if (d == null) return r.ToArray();
      var byIndex = new SortedDictionary<int, long>();
      foreach (var kv in d) { int i; if (kv.Value is long && kv.Key.Length > 2 && int.TryParse(kv.Key.Substring(1, kv.Key.Length - 2), out i)) byIndex[i] = (long)kv.Value; }
      int expect = 1;
      foreach (var kv in byIndex) { if (kv.Key != expect++) break; r.Add(kv.Value); }
      return r.ToArray();
    }

    // the numbers a table holds: its values when it is a list, its keys when it is a set ({[id] = true})
    static long[] IntSet(ulong t) {
      var d = ParseTable(t, 0, new HashSet<ulong>());
      var r = new List<long>();
      if (d == null) return r.ToArray();
      foreach (var kv in d) {
        long k;
        if (kv.Value is long) r.Add((long)kv.Value);
        else if (kv.Value is bool && (bool)kv.Value && kv.Key.Length > 2 && long.TryParse(kv.Key.Substring(1, kv.Key.Length - 2), out k)) r.Add(k);
      }
      r.Sort();
      return r.ToArray();
    }
  }
}
