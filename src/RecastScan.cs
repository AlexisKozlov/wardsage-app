// RealmForge — the game's recast screens, read from memory (READ-ONLY): the foundry's «Преобразовать» tab and the gem
// screen's recast functions. The «Найти в игре» helper (src/LocateGuide.cs) tells the player where an item is; nothing is
// written to the game and nothing is clicked.
//   foundry form (id 0):
//     m_LuaLogicActive            true while the form is shown
//     m_curType                   3 = the «Преобразовать» tab
//     m_PanelEquipRecast          the tab's panel:
//       m_IsShow / m_bActive      the panel is on the screen
//       m_Equips                  the uids of the list, one per cell, in screen order
//       m_EquipId                 the item selected now
//       m_SelectParts             the slot switches on the left
//       m_FilterConfig            Suits, MainAttrs, SubAttrs / vSubAttrs (the set, main and sub stat ids chosen)
//   gem screen (id 0):
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
    public long[] Equips = new long[0];        // its list in screen order
    public long Sel;                           // item selected there, 0 = none
    public int[] SelectParts = new int[0];     // slot switches on (0..4), empty = all
    public long[] Suits = new long[0], MainAttrs = new long[0], SubAttrs = new long[0];   // its filter
    public bool GemOpen;                       // the gem screen is shown
    public int GemFunc = -1;                   // its selected function (7, 8 = recast)
    public long GemSel;                        // item selected there, 0 = none
    public long[] GemList = new long[0];       // the item pop-up's list
    public bool ChoosePending;                 // a recast result waits for the choice (blocks selecting another item); not read yet

    public bool OnRecast { get { return SublimingOpen && PanelShown && CurType == 3; } }
    public bool OnGemRecast { get { return GemOpen && (GemFunc == 7 || GemFunc == 8); } }
  }

  public static partial class RFX {
    /// <summary>The recast screens now; null when the game has neither (never opened) or they cannot be read. A form the
    /// game never built is searched for (a full pass, about a second) at most every <paramref name="rescanMs"/> (&lt; 0: not now).</summary>
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
      if (gf != 0) {
        s.GemOpen = Field(gf, "m_LuaLogicActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0;
        if (Field(gf, "m_CurrentEquipGemSelectedUid", out v, out tt) && tt == T_INT) s.GemFunc = (int)(long)v;
        if (Field(gf, "m_CurrentEquipSelectedUid", out v, out tt) && tt == T_INT && (long)v > 0) s.GemSel = (long)v;
        ulong pop, t; int pt;
        if (Field(gf, "m_Panel_EquipFilter", out pop, out pt) && pt == T_TABLE && Field(pop, "m_AllEquips", out t, out tt) && tt == T_TABLE) s.GemList = OrderedInts(t);
      }
      return s;
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
