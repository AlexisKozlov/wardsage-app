// RealmForge — the game's inventory («Инвентарь», Form_BackpackIntegration) in bulk sale mode, read from memory
// (READ-ONLY). The storage cleanup selects the site's items there by clicking them, as the player does (src/SellPilot.cs);
// «Продать» is pressed by the player.
//   form.m_LuaLogicActive                  true while the form is shown
//   form.m_CurrentMainTabId2               -9998 = «Снаряжение» (BackpackMainTabType.Equip)
//   form.m_CachedContentPanels[-9998]      the equipment panel (Panel_BackpackEquipPanel):
//     m_CurrentBottomButtonState           2 = «Массовая продажа» (BulkSales)
//     m_FilterAndSortEquipUidList          the items in list order, one per cell, rows of the grid's column count
//     m_StateDataMap                       selected item uid -> its place in m_StateDataList (the keys are the choice)
// In bulk sale a click on an item only toggles it (no card, no pop-up); equipped items are not listed, locked ones do not
// toggle. A filter or tab change clears the choice.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public sealed class SellScreen {
    public bool Open;                       // the list is shown and ready to select in (for the inventory: bulk sale)
    public ulong ListPtr;                   // the list table (a new one when the list is built again: filter, sale)
    public int[] Types;                     // row kinds, 1-based (Types[0] unused); 1 = items
    public Dictionary<long, int[]> Pos = new Dictionary<long, int[]>();   // uid -> {row, col}
    public HashSet<long> Selected = new HashSet<long>();
    public long Current;                    // the item clicked last, 0 = none (not needed: a click shows in Selected)
    public HashSet<int> Parts = new HashSet<int>();   // the screen's part filter, empty = all
    public bool Popup;                      // a pop-up over the list (none in the inventory's bulk sale)
  }

  public sealed class InventoryScreen {
    public bool Open;          // the inventory is shown
    public bool EquipTab;      // on «Снаряжение»
    public bool Bulk;          // in «Массовая продажа»
    public ulong ListPtr;
    public List<long> Uids = new List<long>();          // list order
    public HashSet<long> Selected = new HashSet<long>();

    /// <summary>The list as the sell pilot sees it: rows of <paramref name="cols"/> items.</summary>
    public SellScreen AsSell(int cols) {
      var s = new SellScreen { Open = Open && EquipTab && Bulk, ListPtr = ListPtr };
      int rows = cols > 0 ? (Uids.Count + cols - 1) / cols : 0;
      s.Types = new int[rows + 1];
      for (int r = 1; r <= rows; r++) s.Types[r] = 1;
      for (int i = 0; i < Uids.Count && cols > 0; i++) s.Pos[Uids[i]] = new[] { i / cols + 1, i % cols + 1 };
      foreach (var u in Selected) s.Selected.Add(u);
      return s;
    }
  }

  public static partial class RFX {
    const long EquipTabId = -9998;   // BackpackMainTabType.Equip
    const int BulkSales = 2;         // Panel_BackpackEquipPanel.EquipBottomButtonState.BulkSales

    /// <summary>The inventory now; null when the game has none (never opened) or it cannot be read.</summary>
    public static InventoryScreen ReadInventory(int rescanMs) {
      ulong f = LiveInvForm(rescanMs);
      if (f == 0) return null;
      var s = new InventoryScreen();
      ulong v; int tt;
      s.Open = Field(f, "m_LuaLogicActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0;   // a Lua boolean fills 4 bytes
      s.EquipTab = Field(f, "m_CurrentMainTabId2", out v, out tt) && tt == T_INT && (long)v == EquipTabId;
      ulong panels, panel; int pt, pnt;
      if (!Field(f, "m_CachedContentPanels", out panels, out pt) || pt != T_TABLE || !IntKey(panels, EquipTabId, out panel, out pnt) || pnt != T_TABLE) return s;
      s.Bulk = Field(panel, "m_CurrentBottomButtonState", out v, out tt) && tt == T_INT && (long)v == BulkSales;
      ulong list; int lt;
      if (Field(panel, "m_FilterAndSortEquipUidList", out list, out lt) && lt == T_TABLE) {
        s.ListPtr = list;
        var d = ParseTable(list, 0, new HashSet<ulong>());
        if (d != null) {
          var byIndex = new SortedDictionary<int, long>();
          foreach (var kv in d) { int i; if (kv.Value is long && kv.Key.Length > 2 && int.TryParse(kv.Key.Substring(1, kv.Key.Length - 2), out i)) byIndex[i] = (long)kv.Value; }
          int expect = 1;
          foreach (var kv in byIndex) { if (kv.Key != expect++) break; s.Uids.Add(kv.Value); }   // a Lua array: 1..n
        }
      }
      ulong map; int mt;
      if (Field(panel, "m_StateDataMap", out map, out mt) && mt == T_TABLE) {
        var d = ParseTable(map, 0, new HashSet<ulong>());
        if (d != null) foreach (var k in d.Keys) { long u; if (k.Length > 2 && long.TryParse(k.Substring(1, k.Length - 2), out u) && u > 0) s.Selected.Add(u); }
      }
      return s;
    }
  }
}
