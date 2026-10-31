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
//     _listDataCur                         the artifacts in screen order, worn ones left out: tables with iItemUid (an integer, or maybe a string: UidOf)
//     _btDicSelected                       uid -> true / false (a deselected one stays with false: only true counts)
//     m_ListArtifact_ScrollRect / m_ListArtifact_InfinityGrid   the list's scroll, columns (floor((list width - 6) / 90): 7 at the authored 1136x640, 8 at a 1.9:1 window) and row height (116)
using System;
using System.Collections.Generic;
using System.Text;

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

  /// <summary>The equipment panel's filter (Panel_BackpackEquipPanel, READ ONLY): the choice the game holds in m_EquipFilterDataMap (0 status, 1 sets
  /// {LeftSuitList, RightSuitList}, 2 quality {ItemQuality key = true}, 3 level, 4 main stats, 5 sub stats), the sets the pop-up lists (m_LeftSuitIdList /
  /// m_RightSuitIdList), which of the two it shows and whether the panel and the set pop-up are open (the host's widget reads).</summary>
  public sealed class InvFilterRead {
    public bool Known;                                  // the choice was read
    public long[] Suits = new long[0], Qualities = new long[0];   // chosen sets (both lists), chosen quality keys
    public long[] Left = new long[0], Right = new long[0];        // the sets of the pop-up's two lists
    public bool RightShown, ListMode;                   // m_SuitFilterListLeftRight, m_SuitFilterListMode
    public bool Other;                                  // another part is on (status, level, main / sub stats): it may hide the item
    public bool PanelOpen, SuitOpen, PanelKnown;        // the filter panel / the set pop-up shown (widget reads; PanelKnown: they were read)
    public string Key { get { return string.Join(",", Array.ConvertAll(Suits, x => x.ToString())) + "|" + string.Join(",", Array.ConvertAll(Qualities, x => x.ToString())) + "|" + Other + RightShown + PanelOpen + SuitOpen; } }
  }

  public sealed class InventoryScreen {
    /// <summary>The filter of the equipment panel; null = not read (RFX.InvFilterWanted off, or the panel is not there).</summary>
    public InvFilterRead Filter;
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
    public string Diag = "";          // which fields were bound and the counts, for one log line (RFX.ReadArtWorkshopForm)
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
        if (s.Open && Field(f, "m_CachedContentPanels", out panels, out pt) && pt == T_TABLE) {
          ReadArtifactPanel(panels, s);
          bool tried = s.ArtBulk || (ArtListWanted && s.ArtBulkKnown);
          if (tried && !artDiagSaid && (s.List == null || s.Uids.Count == 0)) ArtDiag(f, panels, s);
        }
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
      if (InvFilterWanted && s.Open && s.EquipTab) { try { s.Filter = ReadInvFilter(panel); } catch (Exception e) { s.Filter = null; LL("inventory filter read failed: " + e.Message); } }
      return s;
    }

    /// <summary>«Найти в игре» filters the inventory's list for a long search (the host sets it for the request's run): the filter is read with the inventory.</summary>
    public static volatile bool InvFilterWanted;
    /// <summary>The client size and unit the filter's widget reads are made for (the host sets them; 0 = none: the panels' state is not read).</summary>
    public static double InvFilterW, InvFilterH, InvFilterU;

    /// <summary>The equipment panel's filter state. <paramref name="panel"/> = m_CachedContentPanels[-9998]. Public for the tests (fake tables).</summary>
    public static InvFilterRead ReadInvFilter(ulong panel) {
      var f = new InvFilterRead(); ulong map, t; int mt, tt; ulong sub; int st;
      if (Field(panel, "m_LeftSuitIdList", out t, out tt) && tt == T_TABLE) f.Left = OrderedInts(t);
      if (Field(panel, "m_RightSuitIdList", out t, out tt) && tt == T_TABLE) f.Right = OrderedInts(t);
      ulong v;
      f.RightShown = Field(panel, "m_SuitFilterListLeftRight", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0;
      f.ListMode = Field(panel, "m_SuitFilterListMode", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0;
      if (!Field(panel, "m_EquipFilterDataMap", out map, out mt) || mt != T_TABLE) return f;
      var suits = new List<long>();
      if (IntKey(map, 1, out sub, out st) && st == T_TABLE) {
        if (Field(sub, "LeftSuitList", out t, out tt) && tt == T_TABLE) suits.AddRange(OrderedInts(t));
        if (Field(sub, "RightSuitList", out t, out tt) && tt == T_TABLE) suits.AddRange(OrderedInts(t));
      }
      suits.Sort(); f.Suits = suits.ToArray();
      if (IntKey(map, 2, out sub, out st) && st == T_TABLE) { var q = new HashSet<long>(); ReadTrueKeys(sub, q); var ql = new List<long>(q); ql.Sort(); f.Qualities = ql.ToArray(); }
      foreach (long k in new long[] { 0, 3, 4, 5 }) if (IntKey(map, k, out sub, out st) && st == T_TABLE && TableCount(sub) > 0) f.Other = true;
      f.Known = true;
      // the panel and the set pop-up shown: the widgets' own visibility (src/UiButtons.cs; the host gave the window's size)
      if (InvFilterW > 0 && InvFilterH > 0) {
        var pn = UiKeyRead("panel", InvFilterW, InvFilterH, InvFilterU);
        var sp = UiKeyRead("suitPanel", InvFilterW, InvFilterH, InvFilterU);
        f.PanelKnown = pn != null && pn.Ok;
        f.PanelOpen = f.PanelKnown && pn.Visible;
        f.SuitOpen = f.PanelOpen && sp != null && sp.Ok && sp.Visible;
      }
      return f;
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
            long uid;
            if ((row.TryGetValue("iItemUid", out u) || row.TryGetValue("itemUid", out u)) && UidOf(u, out uid) && uid > 0) byIndex[i] = uid;
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

    // an artifact uid as the script may hold it: an integer, a whole float, or a number written as a string
    static bool UidOf(object u, out long v) {
      v = 0;
      if (u is long) { v = (long)u; return true; }
      if (u is double) { double d = (double)u; if (d == Math.Floor(d) && Math.Abs(d) < 9e15) { v = (long)d; return true; } return false; }
      var str = u as string;
      return str != null && long.TryParse(str, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    static string LuaTypeName(int tt) {
      switch (tt & 0x0F) { case 0: return "nil"; case 1: return "bool"; case 2: return "lud"; case 3: return "num"; case 4: return "str"; case 5: return "table"; case 6: return "fn"; case 7: return "ud"; default: return "t" + (tt & 0x0F); }
    }

    // the string-keyed fields of a script table: key -> {type tag, raw value}; the count of a table-valued entry comes from TableCount
    static List<KeyValuePair<string, int[]>> TableFields(ulong t, out List<ulong> values) {
      var res = new List<KeyValuePair<string, int[]>>(); values = new List<ulong>();
      var h = Read(t, 56); if (h == null || h[8] != 5) return res;
      int lsize = h[11]; if (lsize > 12) return res;
      int nn = 1 << lsize; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return res;
      for (int i = 0; i < nn; i++) {
        int o = i * 32; int ktt = BitConverter.ToInt32(nb, o + 24), vtt = BitConverter.ToInt32(nb, o + 8);
        if ((ktt != T_SSTR && ktt != T_LSTR) || (vtt & 0x0F) == 0) continue;
        string key = ReadLuaString(BitConverter.ToUInt64(nb, o + 16)); if (key == null) continue;
        res.Add(new KeyValuePair<string, int[]>(key, new[] { vtt })); values.Add(BitConverter.ToUInt64(nb, o));
      }
      return res;
    }

    // the number of non-nil entries of a script table (array part + hash part); -1 = unreadable
    static int TableCount(ulong t) {
      var h = Read(t, 56); if (h == null || h[8] != 5) return -1;
      int lsize = h[11]; uint sizearray = BitConverter.ToUInt32(h, 12);
      if (lsize > 20 || sizearray > 1000000) return -1;
      int n = 0;
      if (sizearray > 0) { var ab = Read(BitConverter.ToUInt64(h, 16), (int)sizearray * 16); if (ab != null) for (int i = 0; i < sizearray; i++) if ((BitConverter.ToInt32(ab, i * 16 + 8) & 0x0F) != 0) n++; }
      int nn = 1 << lsize; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32);
      if (nb != null) for (int i = 0; i < nn; i++) if ((BitConverter.ToInt32(nb, i * 32 + 8) & 0x0F) != 0) n++;
      return n;
    }

    // "key=type[count]" for the fields of a table whose names match <paramref name="re"/> (null = all), the matching ones first, at most <paramref name="max"/> chars
    static string DescribeFields(ulong t, System.Text.RegularExpressions.Regex re, int max) {
      List<ulong> vals; var fl = TableFields(t, out vals);
      var hot = new List<string>(); var rest = new List<string>();
      for (int i = 0; i < fl.Count; i++) {
        int tt = fl[i].Value[0]; string d = fl[i].Key + "=" + LuaTypeName(tt);
        if ((tt & 0x0F) == 5) d += "[" + TableCount(vals[i]) + "]";
        if (re == null || re.IsMatch(fl[i].Key)) hot.Add(d); else rest.Add(d);
      }
      hot.Sort(StringComparer.Ordinal); rest.Sort(StringComparer.Ordinal);
      hot.AddRange(re == null ? new List<string>() : rest);
      var sb = new System.Text.StringBuilder();
      foreach (var d in hot) { if (sb.Length + d.Length + 1 > max) { sb.Append(" ..."); break; } if (sb.Length > 0) sb.Append(' '); sb.Append(d); }
      return sb.ToString();
    }

    static bool artDiagSaid;
    static readonly System.Text.RegularExpressions.Regex ArtDiagKeys = new System.Text.RegularExpressions.Regex("list|scroll|grid|sel|batch|uid|sort|filter|count|content|data", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Once per run, when the artifact tab is open but its list cannot be read: the panel's field names with their script types (tables with their entry count),
    /// the form's content / grid fields and the first row of the list, so the next recording shows the real names.</summary>
    static void ArtDiag(ulong form, ulong panels, InventoryScreen s) {
      string line = ArtDiagText(form, panels);
      if (line == null) return;
      artDiagSaid = true;
      LL(line);
    }

    /// <summary>The diagnostics line (see ArtDiag), null when the panel is not there. Public for the tests (fake tables).</summary>
    public static string ArtDiagText(ulong form, ulong panels) {
      ulong panel; int pnt;
      if (!IntKey(panels, ArtifactTabId, out panel, out pnt) || pnt != T_TABLE) return null;
      string line = "art panel fields: " + DescribeFields(panel, ArtDiagKeys, 900);
      line += " | form: " + DescribeFields(form, new System.Text.RegularExpressions.Regex("content|scroll|grid|list", System.Text.RegularExpressions.RegexOptions.IgnoreCase), 300);
      ulong list; int lt;
      if (Field(panel, "_listDataCur", out list, out lt) && lt == T_TABLE) {
        string row1 = "<none>"; ulong r1; int rt;
        if (IntKey(list, 1, out r1, out rt)) row1 = rt == T_TABLE ? DescribeFields(r1, null, 200) : LuaTypeName(rt);
        line += " | _listDataCur[1]: " + row1;
      }
      return line.Length > 1500 ? line.Substring(0, 1500) : line;
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
        int ktt = BitConverter.ToInt32(nb, o + 24);
        if (BitConverter.ToInt32(nb, o + 8) != T_BOOL || (BitConverter.ToUInt64(nb, o) & 0xFFFFFFFF) == 0) continue;
        long k = 0;
        if (ktt == T_INT) k = BitConverter.ToInt64(nb, o + 16);
        else if (ktt == T_FLT) { double dk = BitConverter.Int64BitsToDouble(BitConverter.ToInt64(nb, o + 16)); if (dk == Math.Floor(dk) && Math.Abs(dk) < 9e15) k = (long)dk; }
        else if (ktt == T_SSTR || ktt == T_LSTR) UidOf(ReadLuaString(BitConverter.ToUInt64(nb, o + 16)), out k);   // a uid written as a string
        if (k > 0) into.Add(k);
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
    static string artScrollField; static bool artScrollSaid, artScrollViaGrid;

    /// <summary>The artifact tab's list (the panel's own scroll and grid); null when it cannot be read.</summary>
    public static ListRead ReadArtifactList(ulong panel) {
      int pid = ListPid(); if (pid == 0) return null;
      if (artScrollField != null || artScrollViaGrid) return ReadList(panel, artScrollField, "m_ListArtifact_InfinityGrid", pid);
      foreach (var name in ArtScrollFields) {
        var r = ReadList(panel, name, "m_ListArtifact_InfinityGrid", pid);
        if (r != null) { artScrollField = name; LL("art list: the scroll is bound as " + name + ", the grid as m_ListArtifact_InfinityGrid"); return r; }
      }
      // the scroll bound under another name: any userdata field of the panel with "Scroll" in its name (the grid is the Lua's own)
      List<ulong> vals; var fl = TableFields(panel, out vals);
      foreach (var kv in fl) {
        if ((kv.Value[0] & 0x0F) != 7 || kv.Key.IndexOf("Scroll", StringComparison.OrdinalIgnoreCase) < 0 || Array.IndexOf(ArtScrollFields, kv.Key) >= 0) continue;
        var r = ReadList(panel, kv.Key, "m_ListArtifact_InfinityGrid", pid);
        if (r != null) { artScrollField = kv.Key; LL("art list: the scroll is bound as " + kv.Key + ", the grid as m_ListArtifact_InfinityGrid"); return r; }
      }
      // the panel's script binds only the grid: the scroll component is the one the grid itself holds
      { var r = ReadList(panel, null, "m_ListArtifact_InfinityGrid", pid); if (r != null) { artScrollViaGrid = true; LL("art list: the scroll is the one the grid m_ListArtifact_InfinityGrid holds (no panel field for it: tried " + string.Join(", ", ArtScrollFields) + ")"); return r; } }
      if (!artScrollSaid) { artScrollSaid = true; LL("art list: no scroll read (tried " + string.Join(", ", ArtScrollFields) + " and the grid's own) - the picture is the fallback"); }
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
    /// <summary>A uid field: an integer, a whole float, or a number written as a string.</summary>
    static bool UidField(ulong t, string key, out long val) {
      ulong v; int tt; val = 0;
      if (!Field(t, key, out v, out tt)) return false;
      if (tt == T_INT) { val = (long)v; return true; }
      if (tt == T_FLT) return UidOf(BitConverter.Int64BitsToDouble((long)v), out val);
      if (tt == T_SSTR || tt == T_LSTR) return UidOf(ReadLuaString(v), out val);
      return false;
    }

    static bool IntField(ulong t, string key, out long val) {
      ulong v; int tt; val = 0;
      if (!Field(t, key, out v, out tt)) return false;
      if (tt == T_INT) { val = (long)v; return true; }
      if (tt == T_FLT) { double d = BitConverter.Int64BitsToDouble((long)v); if (d == Math.Floor(d) && Math.Abs(d) < 1e15) { val = (long)d; return true; } }
      return false;
    }

    // the field names the copy list's ScrollRect may be bound under (the Lua only uses the grid's; the first that reads is kept)
    static readonly string[] MatScrollFields = { "m_List_ScrollRect", "m_List_ScrollView", "m_List_Scroll" };
    static string matScrollField; static bool matScrollSaid, matScrollViaGrid;

    /// <summary>The «Продвинуть» panel's copy list (its own scroll and grid); null when it cannot be read.</summary>
    public static ListRead ReadMatList(ulong panel) {
      int pid = ListPid(); if (pid == 0) return null;
      if (matScrollField != null || matScrollViaGrid) return ReadList(panel, matScrollField, "m_List_InfinityGrid", pid);
      foreach (var name in MatScrollFields) {
        var r = ReadList(panel, name, "m_List_InfinityGrid", pid);
        if (r != null) { matScrollField = name; LL("art workshop: the copy list's scroll is bound as " + name); return r; }
      }
      { var r = ReadList(panel, null, "m_List_InfinityGrid", pid); if (r != null) { matScrollViaGrid = true; LL("art workshop: the copy list's scroll is the one its grid holds"); return r; } }
      if (!matScrollSaid) { matScrollSaid = true; LL("art workshop: no scroll read for the copy list (tried " + string.Join(", ", MatScrollFields) + " and the grid's own) - no frames on the copies"); }
      return null;
    }

    /// <summary>The workshop form's fields into <paramref name="s"/> (READ ONLY); <paramref name="f"/> = the form table. Public for the tests (fake tables).</summary>
    public static void ReadArtWorkshopForm(ulong f, ArtWorkshopScreen s) {
      int tt; long n;
      s.Open = InvFormShown(f);
      if (!s.Open) return;
      var dg = new StringBuilder();
      if (IntField(f, "m_iItemType", out n)) s.Tab = (int)Math.Max(0, Math.Min(99, n));
      dg.Append("m_iItemType=").Append(s.Tab);
      ulong ad;
      if (Field(f, "m_ArtifactData", out ad, out tt) && tt == T_TABLE) {
        if (UidField(ad, "iItemUid", out n)) s.Uid = n;
        dg.Append(" m_ArtifactData.iItemUid=").Append(s.Uid > 0 ? "read" : "unreadable");
        if (IntField(ad, "iItemId", out n)) s.ItemId = (int)n;
        if (IntField(ad, "iLevel", out n)) s.Level = (int)Math.Max(0, Math.Min(999, n));
        if (IntField(ad, "iStageLvl", out n)) s.Stage = (int)Math.Max(0, Math.Min(99, n));
      }
      else dg.Append(" m_ArtifactData=missing");
      ulong panel;
      if (s.Tab != 2) { s.Diag = dg.ToString(); return; }
      if (!Field(f, "m_PanelEquipBreak", out panel, out tt) || tt != T_TABLE) {
        List<ulong> fv; var names = new List<string>();
        foreach (var kv in TableFields(f, out fv)) if (kv.Key.IndexOf("Panel", StringComparison.OrdinalIgnoreCase) >= 0) names.Add(kv.Key + ":" + LuaTypeName(kv.Value[0]));
        s.Diag = dg + " m_PanelEquipBreak=" + (tt == 0 ? "missing" : LuaTypeName(tt)) + " form panels [" + string.Join(",", names) + "]";
        return;
      }
      dg.Append(" m_PanelEquipBreak=table");
      ulong list;
      if (Field(panel, "m_vMatData", out list, out tt) && tt == T_TABLE) {
        s.ListPtr = list;
        var d = ParseTable(list, 1, new HashSet<ulong>());
        dg.Append(" m_vMatData=").Append(d == null ? "unparsed" : d.Count + " entries");
        if (d != null) {
          var byIndex = new SortedDictionary<int, long[]>();   // {uid, level, item id}
          foreach (var kv in d) {
            int i; var row = kv.Value as Dictionary<string, object>; object u, lv, iid;
            if (row == null || kv.Key.Length <= 2 || !int.TryParse(kv.Key.Substring(1, kv.Key.Length - 2), out i)) continue;
            long uid = 0; if (row.TryGetValue("iItemUid", out u)) UidOf(u, out uid);
            long level = row.TryGetValue("iLevel", out lv) && lv is long ? Math.Max(0, Math.Min(999, (long)lv)) : -1;
            long itemId = row.TryGetValue("iItemId", out iid) && iid is long ? Math.Max(0, Math.Min((long)int.MaxValue, (long)iid)) : 0;
            byIndex[i] = new[] { uid, level, itemId };
          }
          int expect = 1;
          foreach (var kv in byIndex) { if (kv.Key != expect++) break; s.Mats.Add(kv.Value[0]); s.MatLevels.Add((int)kv.Value[1]); s.MatItemIds.Add((int)kv.Value[2]); }   // a script array: 1..n
          s.PanelKnown = true;
          int stubs = 0; foreach (long mu in s.Mats) if (mu == 0) stubs++;
          dg.Append(" -> ").Append(s.Mats.Count).Append(" in order (").Append(s.Mats.Count - stubs).Append(" copies, ").Append(stubs).Append(" essences)");
        }
      } else {
        List<ulong> pv; var names = new List<string>();
        foreach (var kv in TableFields(panel, out pv)) if ((kv.Value[0] & 0x0F) == 5 && kv.Key.StartsWith("m_v", StringComparison.Ordinal)) names.Add(kv.Key);
        dg.Append(" m_vMatData=").Append(tt == 0 ? "missing" : LuaTypeName(tt)).Append(" panel tables [").Append(string.Join(",", names)).Append("]");
      }
      ulong map;
      if (Field(panel, "m_vMatSelect", out map, out tt) && tt == T_TABLE) {
        var sel = new HashSet<long>(); ReadTrueKeys(map, sel);
        foreach (var k in sel) if (k >= 1 && k <= 100000) s.MatSel.Add((int)k);
        dg.Append(" m_vMatSelect=").Append(s.MatSel.Count).Append(" chosen");
      } else dg.Append(" m_vMatSelect=").Append(tt == 0 ? "missing" : LuaTypeName(tt));
      s.MatList = ReadMatList(panel);
      dg.Append(" list scroll ").Append(s.MatList != null && s.MatList.Has ? "read" : "unreadable");
      s.Diag = dg.ToString();
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
