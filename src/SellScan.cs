// RealmForge — the game's inventory («Инвентарь», Form_BackpackIntegration) in bulk sale mode, read from memory
// (READ-ONLY). The storage cleanup selects the site's items there by clicking them, as the player does (src/SellPilot.cs);
// «Продать» is pressed by the player.
//   form.m_LuaLogicActive                  true while the form is shown
//   form.m_CurrentMainTabId2               -9998 = «Снаряжение» (BackpackMainTabType.Equip)
//   form.m_CachedContentPanels[-9998]      the equipment panel (Panel_BackpackEquipPanel):
//     m_CurrentBottomButtonState           2 = «Массовая продажа» (BulkSales)
//     m_FilterAndSortEquipUidList          the items in list order, one per cell, rows of the grid's column count
//     m_CurrentSelectEquipUid              the item selected now (its card is open at the right); 0 = none
//   form.m_Content_ScrollRect / m_Content_InfinityGrid   the grid's scroll position (the content's position in the last frame),
//                                          its CellPerLine (columns) and LineSize (a row, in the scroll's units): RFX.ReadInventoryList
//     m_StateDataMap                       selected item uid -> its place in m_StateDataList (the keys are the choice)
// In bulk sale a click on an item only toggles it (no card, no pop-up); equipped items are not listed, locked ones do not
// toggle. A filter or tab change clears the choice.
// The artifact tab («Артефакт», the third main tab of the same form) is read the same way, never written:
//   form.m_CurrentMainTabId2               -9997 = BackpackMainTabType.Artifacts
//   form.m_CachedContentPanels[-9997]      the artifact panel (Panel_ArtifactPanel):
//     _isActive                            the panel is shown
//     _btFlagBatch                         2 = «Массовая продажа» (-1 normal, 1 «Масс. организовать»)
//     _listDataCur                         the artifacts in screen order, worn ones left out: tables with iItemUid
//     _btDicSelected                       uid -> true / false (a deselected one stays with false: only true counts)
//     m_ListArtifact_ScrollRect / m_ListArtifact_InfinityGrid   the list's scroll, columns (7 at the authored width) and row height (116)
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
    public bool ArtTab;        // on «Артефакт» (then EquipTab is false and Bulk / BulkKnown are not used: the Art* fields tell the mode)
    public bool ArtBulk;       // the artifact tab is in «Массовая продажа» (its panel is shown)
    public bool ArtBulkKnown;  // the artifact panel's mode was read
    public int ArtMode;        // the artifact panel's _btFlagBatch: -1 normal, 1 «Масс. организовать», 2 «Массовая продажа»; 0 = not read
    public int ArtSel;         // normal mode (read only for «Показать в игре»): the 1-based place in Uids of the card whose info is open (_selectedIndexMain), 0 = none / unreadable
    public bool ArtSelKnown;   // ... and the field was read
    public bool BulkKnown;     // the equipment panel's mode was read (false: a short read failure - the mode is NOT known, never assume "normal")
    public ulong ListPtr;
    public List<long> Uids = new List<long>();          // list order
    public HashSet<long> Selected = new HashSet<long>();
    public long Current;       // the item selected in the normal mode (the one whose card is open), 0 = none
    /// <summary>How the game scrolls the grid: its exact position, columns and row height (RFX.ReadInventoryList); null = unreadable
    /// (then the picture is the last resort).</summary>
    public RFX.ListRead List;

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

    /// <summary>The artifact tab's «Массовая продажа» list as the sell pilot sees it: rows of <paramref name="cols"/> cards (the same pilot as the equipment list).</summary>
    public SellScreen AsSellArt(int cols) {
      var s = AsSell(cols);
      s.Open = Open && ArtTab && ArtBulk;
      return s;
    }
  }

  /// <summary>The artifact workshop (Form_EquipArtifactIntensify): which artifact is open, on which tab and, on «Продвинуть», the copies to pick from.
  /// Read only (RFX.ReadArtWorkshop).</summary>
  public sealed class ArtWorkshopScreen {
    public bool Open;                 // the form is shown
    public int Tab;                   // m_iItemType: 1 «Улучшить», 2 «Продвинуть», 5 «Доработка», 0 = not read
    public long Uid;                  // the open artifact (m_ArtifactData.iItemUid), 0 = not read
    public int ItemId, Level = -1, Stage = -1;   // its type, level and promotion stage now (-1 = not read)
    public bool PanelKnown;           // the «Продвинуть» panel's candidate list was read
    public List<long> Mats = new List<long>();    // m_vMatData in screen order: a copy's uid, 0 = an essence stub
    public List<int> MatLevels = new List<int>(); // per entry: the copy's iLevel (-1 = not read / an essence)
    public List<int> MatItemIds = new List<int>();   // per entry: its iItemId (an essence stub's item; 0 = not read)
    public HashSet<int> MatSel = new HashSet<int>();   // 1-based indices of m_vMatData that are chosen
    public RFX.ListRead MatList;      // the copy list's scroll and grid (null = unreadable)
    public ulong ListPtr;
  }

  public static partial class RFX {
    const long EquipTabId = -9998;   // BackpackMainTabType.Equip
    const int BulkSales = 2;         // Panel_BackpackEquipPanel.EquipBottomButtonState.BulkSales
    const long ArtifactTabId = -9997;   // BackpackMainTabType.Artifacts
    const int ArtBatchSell = 2;      // Panel_ArtifactPanel.BatchFlagSell

    /// <summary>The cheap per-tick look for the overlay: is the inventory form on screen now (m_LuaLogicActive) and, if so, its list's exact
    /// scroll (null = unreadable). Reads two fields, no list parsing, so it can run every tick.</summary>
    public static ListRead ReadInventoryLive(out bool active) {
      active = false;
      ulong f = LiveInvForm(-1);
      if (!InvFormShown(f)) return null;
      active = true;
      return ReadInventoryList(f);
    }

    /// <summary>The inventory form is on screen: m_LuaLogicActive (a Lua boolean fills 4 bytes). NOT FormActive: that also wants the hero
    /// screen's grid field, which the inventory form never has (live 0.61.193: the hint stayed at «open the inventory»).</summary>
    public static bool InvFormShown(ulong f) { ulong v; int tt; return f != 0 && Field(f, "m_LuaLogicActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0; }

    /// <summary>The inventory now; null when the game has none (never opened) or it cannot be read.</summary>
    public static InventoryScreen ReadInventory(int rescanMs) {
      ulong f = LiveInvForm(rescanMs);
      if (f == 0) return null;
      var s = new InventoryScreen();
      ulong v; int tt;
      s.Open = Field(f, "m_LuaLogicActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0;   // a Lua boolean fills 4 bytes
      s.EquipTab = Field(f, "m_CurrentMainTabId2", out v, out tt) && tt == T_INT && (long)v == EquipTabId;
      s.ArtTab = Field(f, "m_CurrentMainTabId2", out v, out tt) && tt == T_INT && (long)v == ArtifactTabId;
      ulong panels, panel; int pt, pnt;
      if (s.ArtTab) {
        if (s.Open && Field(f, "m_CachedContentPanels", out panels, out pt) && pt == T_TABLE) ReadArtifactPanel(panels, s);
        return s;
      }
      if (!Field(f, "m_CachedContentPanels", out panels, out pt) || pt != T_TABLE || !IntKey(panels, EquipTabId, out panel, out pnt) || pnt != T_TABLE) return s;
      if (s.Open && s.EquipTab) s.List = ReadInventoryList(f);
      s.BulkKnown = Field(panel, "m_CurrentBottomButtonState", out v, out tt) && tt == T_INT;
      s.Bulk = s.BulkKnown && (long)v == BulkSales;
      if (Field(panel, KSel, out v, out tt) && tt == T_INT && (long)v > 0) s.Current = (long)v;
      ulong list; int lt;
      if (Field(panel, "m_FilterAndSortEquipUidList", out list, out lt) && lt == T_TABLE) {
        s.ListPtr = list;
        var d = ParseTable(list, 0, new HashSet<ulong>());
        if (d != null) {
          var byIndex = new SortedDictionary<int, long>();
          foreach (var kv in d) { int i; if (kv.Value is long && kv.Key.Length > 2 && int.TryParse(kv.Key.Substring(1, kv.Key.Length - 2), out i)) byIndex[i] = (long)kv.Value; }
          int expect = 1;
          foreach (var kv in byIndex) { if (kv.Key != expect++) break; s.Uids.Add(kv.Value); }   // a script array: 1..n
        }
      }
      ulong map; int mt;
      if (Field(panel, "m_StateDataMap", out map, out mt) && mt == T_TABLE) {
        var d = ParseTable(map, 0, new HashSet<ulong>());
        if (d != null) foreach (var k in d.Keys) { long u; if (k.Length > 2 && long.TryParse(k.Substring(1, k.Length - 2), out u) && u > 0) s.Selected.Add(u); }
      }
      return s;
    }

    /// <summary>The artifact panel's mode and, in «Массовая продажа», its list and choice (READ ONLY). <paramref name="panels"/> = the form's
    /// m_CachedContentPanels. Public for the tests (fake tables).</summary>
    public static void ReadArtifactPanel(ulong panels, InventoryScreen s) {
      ulong panel, v; int pnt, tt;
      if (!IntKey(panels, ArtifactTabId, out panel, out pnt) || pnt != T_TABLE) return;
      bool active = Field(panel, "_isActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0;   // a Lua boolean fills 4 bytes
      bool flagOk = Field(panel, "_btFlagBatch", out v, out tt) && tt == T_INT;
      s.ArtBulkKnown = active && flagOk;
      s.ArtBulk = s.ArtBulkKnown && (long)v == ArtBatchSell;
      if (s.ArtBulkKnown) s.ArtMode = (int)Math.Max(-9, Math.Min(9, (long)v));
      // outside the bulk sale the list and the open card are read only while «Показать в игре» guides an artifact (ArtListWanted)
      if (!s.ArtBulk && !(ArtListWanted && s.ArtBulkKnown)) return;
      s.List = ReadArtifactList(panel);
      ulong list; int lt;
      if (Field(panel, "_listDataCur", out list, out lt) && lt == T_TABLE) {
        s.ListPtr = list;
        var d = ParseTable(list, 1, new HashSet<ulong>());   // the artifacts: tables of data, their own tables are not followed
        if (d != null) {
          var byIndex = new SortedDictionary<int, long>();
          foreach (var kv in d) {
            int i; var row = kv.Value as Dictionary<string, object>; object u;
            if (row == null || kv.Key.Length <= 2 || !int.TryParse(kv.Key.Substring(1, kv.Key.Length - 2), out i)) continue;
            if (row.TryGetValue("iItemUid", out u) && u is long) byIndex[i] = (long)u;
          }
          int expect = 1;
          foreach (var kv in byIndex) { if (kv.Key != expect++) break; s.Uids.Add(kv.Value); }   // a script array: 1..n
        }
      }
      if (!s.ArtBulk) {
        s.ArtSelKnown = Field(panel, "_selectedIndexMain", out v, out tt) && tt == T_INT;
        if (s.ArtSelKnown && (long)v >= 1 && (long)v <= s.Uids.Count) s.ArtSel = (int)(long)v;
        return;
      }
      ulong map; int mt;
      if (Field(panel, "_btDicSelected", out map, out mt) && mt == T_TABLE) ReadTrueKeys(map, s.Selected);
    }

    /// <summary>«Показать в игре» guides an artifact: the artifact tab's list and open card are read in its normal mode too (off otherwise).</summary>
    public static volatile bool ArtListWanted;

    // the integer keys of a script table whose value is true (a deselected entry stays with false)
    static void ReadTrueKeys(ulong t, HashSet<long> into) {
      var h = Read(t, 56); if (h == null || h[8] != 5) return;
      int lsize = h[11]; uint sizearray = BitConverter.ToUInt32(h, 12);
      if (lsize > 20 || sizearray > 1000000) return;
      if (sizearray > 0) {
        var ab = Read(BitConverter.ToUInt64(h, 16), (int)sizearray * 16);
        if (ab != null) for (int i = 0; i < sizearray; i++) if (BitConverter.ToInt32(ab, i * 16 + 8) == T_BOOL && (BitConverter.ToUInt64(ab, i * 16) & 0xFFFFFFFF) != 0) into.Add(i + 1);
      }
      int nn = 1 << lsize; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return;
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_BOOL || (BitConverter.ToUInt64(nb, o) & 0xFFFFFFFF) == 0) continue;
        long k = BitConverter.ToInt64(nb, o + 16); if (k > 0) into.Add(k);
      }
    }

    // ---- the artifact workshop (Form_EquipArtifactIntensify, «Мастерская»): READ ONLY, for «Показать в игре» -> «Продвинуть»
    //   form.m_LuaLogicActive            shown
    //   form.m_iItemType                 the right tab: 1 «Улучшить», 2 «Продвинуть», 5 «Доработка»
    //   form.m_ArtifactData              the open artifact: iItemUid, iItemId, iLevel, iStageLvl (read again from the game's data after each success)
    //   form.m_PanelEquipBreak           the «Продвинуть» panel (a table):
    //     m_vMatData                     the candidates in screen order: free copies (iItemUid) first, then artifact-essence stubs ({iItemId}, no uid)
    //     m_vMatSelect                   chosen: index in m_vMatData -> true
    //     m_List_ScrollRect / m_List_InfinityGrid   the copy list's scroll and grid (3 columns, row 114)
    // Not verified on the live game.

    // the field names the artifact list's ScrollRect may be bound under (the Lua only uses the grid's; the first that reads is kept)
    static readonly string[] ArtScrollFields = { "m_ListArtifact_ScrollRect", "m_ListArtifact_ScrollView", "m_ListArtifact_Scroll" };
    static string artScrollField; static bool artScrollSaid;

    /// <summary>The artifact tab's list (the panel's own scroll and grid); null when it cannot be read.</summary>
    public static ListRead ReadArtifactList(ulong panel) {
      int pid = ListPid(); if (pid == 0) return null;
      if (artScrollField != null) return ReadList(panel, artScrollField, "m_ListArtifact_InfinityGrid", pid);
      foreach (var name in ArtScrollFields) {
        var r = ReadList(panel, name, "m_ListArtifact_InfinityGrid", pid);
        if (r != null) { artScrollField = name; L("art list: the scroll is bound as " + name); return r; }
      }
      if (!artScrollSaid) { artScrollSaid = true; L("art list: no scroll field read (tried " + string.Join(", ", ArtScrollFields) + ") - no frames on the cells"); }
      return null;
    }

    /// <summary>The cheap per-tick look for the artifact tab (as ReadInventoryLive): is the inventory form on screen, and the artifact list's exact scroll
    /// (null = unreadable or another tab).</summary>
    public static ListRead ReadArtifactLive(out bool active) {
      active = false;
      ulong f = LiveInvForm(-1);
      if (!InvFormShown(f)) return null;
      active = true;
      ulong v; int tt, pt, pnt; ulong panels, panel;
      if (!Field(f, "m_CurrentMainTabId2", out v, out tt) || tt != T_INT || (long)v != ArtifactTabId) return null;
      if (!Field(f, "m_CachedContentPanels", out panels, out pt) || pt != T_TABLE || !IntKey(panels, ArtifactTabId, out panel, out pnt) || pnt != T_TABLE) return null;
      return ReadArtifactList(panel);
    }

    /// <summary>An integer field of a script table (an integer, or a float with a whole value).</summary>
    static bool IntField(ulong t, string key, out long val) {
      ulong v; int tt; val = 0;
      if (!Field(t, key, out v, out tt)) return false;
      if (tt == T_INT) { val = (long)v; return true; }
      if (tt == T_FLT) { double d = BitConverter.Int64BitsToDouble((long)v); if (d == Math.Floor(d) && Math.Abs(d) < 1e15) { val = (long)d; return true; } }
      return false;
    }

    // the field names the copy list's ScrollRect may be bound under (the Lua only uses the grid's; the first that reads is kept)
    static readonly string[] MatScrollFields = { "m_List_ScrollRect", "m_List_ScrollView", "m_List_Scroll" };
    static string matScrollField; static bool matScrollSaid;

    /// <summary>The «Продвинуть» panel's copy list (its own scroll and grid); null when it cannot be read.</summary>
    public static ListRead ReadMatList(ulong panel) {
      int pid = ListPid(); if (pid == 0) return null;
      if (matScrollField != null) return ReadList(panel, matScrollField, "m_List_InfinityGrid", pid);
      foreach (var name in MatScrollFields) {
        var r = ReadList(panel, name, "m_List_InfinityGrid", pid);
        if (r != null) { matScrollField = name; L("art workshop: the copy list's scroll is bound as " + name); return r; }
      }
      if (!matScrollSaid) { matScrollSaid = true; L("art workshop: no scroll field read for the copy list (tried " + string.Join(", ", MatScrollFields) + ") - no frames on the copies"); }
      return null;
    }

    /// <summary>The workshop form's fields into <paramref name="s"/> (READ ONLY); <paramref name="f"/> = the form table. Public for the tests (fake tables).</summary>
    public static void ReadArtWorkshopForm(ulong f, ArtWorkshopScreen s) {
      int tt; long n;
      s.Open = InvFormShown(f);
      if (!s.Open) return;
      if (IntField(f, "m_iItemType", out n)) s.Tab = (int)Math.Max(0, Math.Min(99, n));
      ulong ad;
      if (Field(f, "m_ArtifactData", out ad, out tt) && tt == T_TABLE) {
        if (IntField(ad, "iItemUid", out n)) s.Uid = n;
        if (IntField(ad, "iItemId", out n)) s.ItemId = (int)n;
        if (IntField(ad, "iLevel", out n)) s.Level = (int)Math.Max(0, Math.Min(999, n));
        if (IntField(ad, "iStageLvl", out n)) s.Stage = (int)Math.Max(0, Math.Min(99, n));
      }
      ulong panel;
      if (s.Tab != 2 || !Field(f, "m_PanelEquipBreak", out panel, out tt) || tt != T_TABLE) return;
      ulong list;
      if (Field(panel, "m_vMatData", out list, out tt) && tt == T_TABLE) {
        s.ListPtr = list;
        var d = ParseTable(list, 1, new HashSet<ulong>());
        if (d != null) {
          var byIndex = new SortedDictionary<int, long[]>();   // {uid, level, item id}
          foreach (var kv in d) {
            int i; var row = kv.Value as Dictionary<string, object>; object u, lv, iid;
            if (row == null || kv.Key.Length <= 2 || !int.TryParse(kv.Key.Substring(1, kv.Key.Length - 2), out i)) continue;
            long uid = row.TryGetValue("iItemUid", out u) && u is long ? (long)u : 0;
            long level = row.TryGetValue("iLevel", out lv) && lv is long ? Math.Max(0, Math.Min(999, (long)lv)) : -1;
            long itemId = row.TryGetValue("iItemId", out iid) && iid is long ? Math.Max(0, Math.Min((long)int.MaxValue, (long)iid)) : 0;
            byIndex[i] = new[] { uid, level, itemId };
          }
          int expect = 1;
          foreach (var kv in byIndex) { if (kv.Key != expect++) break; s.Mats.Add(kv.Value[0]); s.MatLevels.Add((int)kv.Value[1]); s.MatItemIds.Add((int)kv.Value[2]); }   // a script array: 1..n
          s.PanelKnown = true;
        }
      }
      ulong map;
      if (Field(panel, "m_vMatSelect", out map, out tt) && tt == T_TABLE) {
        var sel = new HashSet<long>(); ReadTrueKeys(map, sel);
        foreach (var k in sel) if (k >= 1 && k <= 100000) s.MatSel.Add((int)k);
      }
      s.MatList = ReadMatList(panel);
    }

    /// <summary>The cheap per-tick look at the workshop's copy list: is the form on screen, and the list's exact scroll (null = unreadable).</summary>
    public static ListRead ReadArtMatLive(out bool active) {
      active = false;
      ulong f = LiveArtForm();
      if (!InvFormShown(f)) return null;
      active = true;
      ulong panel; int tt;
      if (!Field(f, "m_PanelEquipBreak", out panel, out tt) || tt != T_TABLE) return null;
      return ReadMatList(panel);
    }

    /// <summary>The artifact workshop now; null when the game has none (never opened, or the layout data has no form id for it) or it cannot be read.</summary>
    public static ArtWorkshopScreen ReadArtWorkshop() {
      ulong f = LiveArtForm();
      if (f == 0) return null;
      var s = new ArtWorkshopScreen();
      ReadArtWorkshopForm(f, s);
      return s;
    }
  }
}
