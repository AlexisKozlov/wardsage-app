// RealmForge extractor - equip helper: reading the game's equipment screen (READ-ONLY).
//
// Same access as the account reader: OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ),
// VirtualQueryEx and ReadProcessMemory. Nothing is ever written to the game and no input is sent to it.
//
// What is read (Lua tables of the game UI, verified on the live game 2026-09-25):
//   * the equipment list panel (the table that owns m_EquipIdToIndex): m_EquipIdToIndex (item uid -> row of
//     m_EquipListRealData), m_EquipListRealData (rows {Type=1, Item={uid, uid, uid}}), m_FilterConfig (Part = shown slot,
//     IsHideEquiped, IsHideEnhanced, Suits, MainAttrs, ...);
//   * EquipData.m_CurrentSelectHeroUid — the hero whose gear screen is open (nil once the hero screen is closed);
//   * the hero screen (Form_CharactorMain, the table that owns m_CharactorGrid_InfinityGrid): its hero grid in display
//     order (m_InfinityGridProxy.m_Data[i].iHeroId), m_PanelDatas[3] = the gear slot whose list replaces the grid, and
//     m_Panels.EquipFilter = the list panel above (created when a slot is first opened, so it is looked up there on
//     every poll instead of scanning the memory again);
//   * item tables (iItemUid, iHeroId) of the plan items — who wears each item now, looked up in EquipData.equips
//     (uid -> item table) on every poll, because the game swaps in a new table when an item changes.
// FindEquip() scans the memory once (tens of seconds); Poll() then re-reads the found tables several times a second.
using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RealmForge {
  public sealed class EquipAddrs {
    public int Pid;
    public ulong Panel;                         // 0 = not found (the gear screen has not been opened yet)
    public ulong EquipData;                     // 0 = not found
    public ulong Form;                          // hero screen (Form_CharactorMain), 0 = not found
    public Dictionary<long, ulong> Items = new Dictionary<long, ulong>();   // plan item uid -> item table
  }

  public sealed class EquipLive {
    public bool GameRunning = true;
    public bool PanelOk;                        // the panel table is still readable
    public bool FormOk;                         // the hero screen's form is known (see Part)
    public int Tab = -1;                        // the hero screen's right-hand tab (3 = gear), -1 = unknown
    public long HeroUid;                        // 0 = unknown
    public int Part = -1;                       // slot shown in the list, -1 = none (the list is closed: the hero grid shows)
    public int ListCount = -1;                  // items in the list
    public bool HideEquipped, HideEnhanced, HideLocked, FilterActive;
    public Dictionary<long, int> Row = new Dictionary<long, int>();      // plan item uid -> 1-based row (visible list)
    public Dictionary<long, int> Col = new Dictionary<long, int>();      // plan item uid -> 1-based position in the row
    public Dictionary<long, long> Owner = new Dictionary<long, long>();  // plan item uid -> hero uid wearing it (0 = bag)
    public long SelUid;                         // item the player selected in the list (m_CurrentSelectEquipUid), 0 = none
    public int SelRow, SelCol;                  // its 1-based row / position in the row, 0 = not in the list
    public ulong ListPtr;                       // m_EquipListRealData table: a new one on every list rebuild
  }

  /// <summary>The hero screen (Form_CharactorMain) now.</summary>
  public sealed class HeroScreen {
    public long Hero;            // EquipData.m_CurrentSelectHeroUid: hero shown; 0 = the hero screen is closed
    public bool FormOk;          // the form table was read
    public int Tab = -1;         // m_LastType: the right-hand tab (3 = gear), -1 = unknown
    public int Part = -1;        // m_PanelDatas[3]: the slot whose gear list replaces the hero grid, -1 = the grid shows
    public bool SmallCards;      // the grid shows small cards (another layout)
    public long[] Heroes;        // the grid in display order (hero uids), null = unreadable
    public ulong GridPtr;        // m_InfinityGridProxy.m_Data: a new table when the grid is sorted / filtered again
    // the grid's filter (Panel_CharactorFilterOrder, m_PanelDatas[6] = Hero_Filter)
    public bool FilterOpen;      // the filter pop-up is on the screen (PanelBase.m_IsActive)
    public long[] FactionList;   // its faction column, top down (0 = «Все"), null until it was opened once
    public int ClassCount;       // entries of its class column («Все» included), 0 = unknown
    public long[] ChosenCamps;   // factions the grid is filtered by now (empty = all)
    public bool ClassChosen;     // the grid is filtered by a class now (which one is not readable: game config objects)
  }

  public static partial class RFX {
    const string KGrid = "m_CharactorGrid_InfinityGrid";
    const string KPanel = "m_EquipIdToIndex", KList = "m_EquipListRealData", KFilter = "m_FilterConfig", KHero = "m_CurrentSelectHeroUid", KSel = "m_CurrentSelectEquipUid";

    // One full scan. Must run under Extractor.Gate (RFX keeps its state in static fields).
    public static EquipAddrs FindEquip(ICollection<long> itemUids, Action<string> onLog) {
      OnLog = onLog;
      try {
        LastError = null; LastOpenError = 0;
        var ps = Process.GetProcessesByName("Watcher of Realms");
        if (ps.Length == 0) { LastError = "not_running"; return null; }
        H = OpenProcess(0x0410, false, ps[0].Id);
        if (H == IntPtr.Zero) { LastOpenError = Marshal.GetLastWin32Error(); LastError = "open_failed"; return null; }
        var a = new EquipAddrs(); a.Pid = ps[0].Id;
        // the live tables found once per game session (shared with the account read): no scan when they are known
        var lt = EnsureLive(a.Pid, HeroScreenOpen());
        if (lt.EquipData != 0) {
          a.EquipData = lt.EquipData; a.Form = lt.Form; a.Panel = lt.Panel;
          RefreshPanel(a);
          L("  panel " + (a.Panel != 0) + ", hero " + (a.EquipData != 0) + ", form " + (a.Form != 0) + " (live tables)");
          return a;
        }
        regs = Regions();
        var tstr = FindLuaStrings(new[] { KPanel, KHero, KGrid, "iStarLvl", "iItemUid" });

        foreach (var t in TablesWithKeys(tstr, KGrid)) { ulong v; int tt; if (Field(t, "m_InfinityGridProxy", out v, out tt) && tt == T_TABLE) { a.Form = t; break; } }
        foreach (var t in TablesWithKeys(tstr, KPanel)) { ulong v; int tt; if (Field(t, KList, out v, out tt) && tt == T_TABLE && Field(t, "m_Form", out v, out tt)) { a.Panel = t; break; } }
        // several UI tables also own m_CurrentSelectHeroUid (the quick put-on, switching and plan panels): EquipData is
        // the one with the item map and the filter's lists
        foreach (var t in TablesWithKeys(tstr, KHero)) {
          ulong v; int tt;
          if (Field(t, "equips", out v, out tt) && tt == T_TABLE && Field(t, "m_EquipFilterConfigs", out v, out tt)) { a.EquipData = t; break; }
        }
        if (a.EquipData == 0) foreach (var t in TablesWithKeys(tstr, KHero)) { ulong v; int tt; if (Field(t, "equips", out v, out tt) && tt == T_TABLE) { a.EquipData = t; break; } }
        RefreshPanel(a);

        // item tables of the plan items: the copies held by the storage container (as in Run)
        var want = new HashSet<long>(itemUids);
        var itemT = new Dictionary<ulong, long>();
        foreach (var t in TablesWithKeys(tstr, "iStarLvl")) {
          ulong v; int tt;
          if (!Field(t, "iItemUid", out v, out tt) || tt != T_INT || !want.Contains((long)v)) continue;
          ulong c; int ctt; if (Field(t, "vConfig", out c, out ctt)) continue;
          itemT[t] = (long)v;
        }
        L("  plan item tables: " + itemT.Count);
        if (itemT.Count > 0) {
          var bag = Containers(new HashSet<ulong>(itemT.Keys), 1);
          ulong bagT = Best(bag, "plan items");
          if (bagT != 0) foreach (var t in bag[bagT]) if (!a.Items.ContainsKey(itemT[t])) a.Items[itemT[t]] = t;
          foreach (var kv in itemT) if (!a.Items.ContainsKey(kv.Value)) a.Items[kv.Value] = kv.Key;   // fallback
        }
        L("  panel " + (a.Panel != 0) + ", hero " + (a.EquipData != 0) + ", form " + (a.Form != 0) + ", items " + a.Items.Count);
        return a;
      } finally { OnLog = null; }
    }

    // Re-reads the found tables. Cheap (a few hundred bytes); safe to call from a timer thread.
    public static EquipLive Poll(EquipAddrs a, ICollection<long> itemUids) {
      var s = new EquipLive();
      try { if (Process.GetProcessById(a.Pid).HasExited) { s.GameRunning = false; return s; } }
      catch (ArgumentException) { s.GameRunning = false; return s; }

      ulong v; int tt;
      if (a.EquipData != 0 && Field(a.EquipData, KHero, out v, out tt) && tt == T_INT) s.HeroUid = (long)v;

      // Who wears each plan item. The game REPLACES an item's table when it changes (EquipData.equips[uid] = new table,
      // e.g. right after the player puts it on), so the current table is looked up in EquipData.equips every time;
      // the tables found by the scan are only a fallback.
      ulong eqs = 0; int ett;
      bool haveEq = a.EquipData != 0 && Field(a.EquipData, "equips", out eqs, out ett) && ett == T_TABLE;
      foreach (var uid in itemUids) {
        ulong it; int itt;
        if (haveEq && IntKey(eqs, uid, out it, out itt) && itt == T_TABLE
            && Field(it, "iHeroId", out v, out tt) && tt == T_INT) {
          s.Owner[uid] = (long)v; a.Items[uid] = it;
          continue;
        }
        if (!a.Items.TryGetValue(uid, out it)) continue;
        if (Field(it, "iItemUid", out v, out tt) && tt == T_INT && (long)v == uid && Field(it, "iHeroId", out v, out tt) && tt == T_INT)
          s.Owner[uid] = (long)v;
      }

      RefreshPanel(a);
      // the hero screen: which slot's gear list is open (m_PanelDatas[3]; nil = the hero grid shows). The list panel keeps
      // its last slot in m_FilterConfig after it is closed, so with the form known only this says the list is on screen.
      int listPart = -1;
      if (a.Form != 0) {
        s.FormOk = true;
        ulong pd; int pdt;
        if (Field(a.Form, "m_PanelDatas", out pd, out pdt) && pdt == T_TABLE && IntKey(pd, 3, out v, out tt) && tt == T_INT) listPart = (int)(long)v;
        if (Field(a.Form, "m_LastType", out v, out tt) && tt == T_INT) s.Tab = (int)(long)v;
      }
      if (a.Panel == 0) return s;
      ulong idx, list, filter; int t1, t2, t3;
      if (!Field(a.Panel, KPanel, out idx, out t1) || t1 != T_TABLE || !Field(a.Panel, KList, out list, out t2) || t2 != T_TABLE) return s;
      s.PanelOk = true;

      if (Field(a.Panel, KFilter, out filter, out t3) && t3 == T_TABLE) {
        var f = ParseTable(filter, 1, new HashSet<ulong>());
        if (f != null) {
          var part = f.ContainsKey("Part") ? f["Part"] as Dictionary<string, object> : null;
          if (part != null && part.Count == 1) foreach (var kv in part) if (kv.Value is long) s.Part = (int)(long)kv.Value;
          if (a.Form != 0 && s.Part != listPart) s.Part = -1;   // the list is closed (or still switching slots)
          s.HideEquipped = f.ContainsKey("IsHideEquiped") && f["IsHideEquiped"] is bool && (bool)f["IsHideEquiped"];
          s.HideEnhanced = f.ContainsKey("IsHideEnhanced") && f["IsHideEnhanced"] is bool && (bool)f["IsHideEnhanced"];
          s.HideLocked = f.ContainsKey("IsHideLocked") && f["IsHideLocked"] is bool && (bool)f["IsHideLocked"];
          foreach (var k in new[] { "Suits", "MainAttrs", "SubAttrs", "Level", "Quality" }) {
            var d = f.ContainsKey(k) ? f[k] as Dictionary<string, object> : null;
            if (d != null && d.Count > 0) s.FilterActive = true;
          }
        }
      }
      if (Field(a.Panel, "m_EquipCount", out v, out tt) && tt == T_INT) s.ListCount = (int)(long)v;
      s.ListPtr = list;
      if (Field(a.Panel, KSel, out v, out tt) && tt == T_INT && (long)v > 0) {
        s.SelUid = (long)v; int c;
        s.SelRow = RowOf(idx, list, s.SelUid, out c); s.SelCol = c;
      }

      foreach (var uid in itemUids) {
        int c; int r = RowOf(idx, list, uid, out c);
        if (r > 0) { s.Row[uid] = r; if (c > 0) s.Col[uid] = c; }
      }
      return s;
    }

    // the hero screen is open (EquipData known from before and its hero set): then the scan must find the form too
    static bool HeroScreenOpen() {
      var lt = live; ulong v; int tt;
      return lt != null && lt.EquipData != 0 && Field(lt.EquipData, KHero, out v, out tt) && tt == T_INT && (long)v > 0;
    }

    /// <summary>The gear list panel of the hero screen (m_Panels.EquipFilter): the game creates it when a slot is first
    /// opened, and a new one when the hero screen is built again. Keeps the one found by the scan when there is no form.</summary>
    public static void RefreshPanel(EquipAddrs a) {
      if (a == null || a.Form == 0) return;
      ulong panels, p, v; int t1, t2, t3;
      if (!Field(a.Form, "m_Panels", out panels, out t1) || t1 != T_TABLE) return;
      if (!Field(panels, "EquipFilter", out p, out t2) || t2 != T_TABLE || p == a.Panel) return;
      if (Field(p, KPanel, out v, out t3) && t3 == T_TABLE) a.Panel = p;
    }

    /// <summary>The hero screen as the game's Lua has it now (read-only). Heroes is the hero grid in display order; it is
    /// read again only when the game builds a new list (GridPtr).</summary>
    public static HeroScreen ReadHeroScreen(EquipAddrs a, HeroScreen prev) {
      var h = new HeroScreen();
      if (a == null || a.EquipData == 0) return h;
      ulong v; int tt;
      if (Field(a.EquipData, KHero, out v, out tt) && tt == T_INT) h.Hero = (long)v;
      if (a.Form == 0) return h;
      h.FormOk = true;
      if (Field(a.Form, "m_LastType", out v, out tt) && tt == T_INT) h.Tab = (int)(long)v;
      h.SmallCards = Field(a.Form, "m_IsUseSmallCard", out v, out tt) && tt == T_BOOL && v != 0;
      ulong pd; int pdt;
      if (Field(a.Form, "m_PanelDatas", out pd, out pdt) && pdt == T_TABLE && IntKey(pd, 3, out v, out tt) && tt == T_INT) h.Part = (int)(long)v;
      ulong proxy, data; int t1, t2;
      if (!Field(a.Form, "m_InfinityGridProxy", out proxy, out t1) || t1 != T_TABLE || !Field(proxy, "m_Data", out data, out t2) || t2 != T_TABLE) return h;
      h.GridPtr = data;
      ReadHeroFilter(a, h);
      if (prev != null && prev.GridPtr == data && prev.Heroes != null) { h.Heroes = prev.Heroes; return h; }
      var ids = new List<long>();
      for (int i = 1; i <= 2000; i++) {
        ulong hd; int ht;
        if (!IntKey(data, i, out hd, out ht) || ht != T_TABLE) break;
        ids.Add(Field(hd, "iHeroId", out v, out tt) && tt == T_INT ? (long)v : 0);
      }
      h.Heroes = ids.ToArray();
      return h;
    }

    static void ReadHeroFilter(EquipAddrs a, HeroScreen h) {
      ulong v; int tt;
      h.ChosenCamps = new long[0];
      ulong pd; int pdt;
      if (Field(a.Form, "m_PanelDatas", out pd, out pdt) && pdt == T_TABLE && IntKey(pd, 6, out v, out tt) && tt == T_TABLE) {
        ulong camps, cls; int ct, lt;
        if (IntKey(v, 3, out camps, out ct) && ct == T_TABLE) { var c = IntArray(camps); var l = new List<long>(); foreach (var x in c) if (x > 0) l.Add(x); h.ChosenCamps = l.ToArray(); }
        // the class column's choice: its «Все» is a Lua table with m_ProfessionID -1, a class is the game's config object
        if (IntKey(v, 1, out cls, out lt) && lt == T_TABLE)
          for (int i = 1; i <= 8; i++) {
            ulong e, pid; int et, pt;
            if (!IntKey(cls, i, out e, out et)) break;
            if (et == T_TABLE && Field(e, "m_ProfessionID", out pid, out pt) && pt == T_INT && (long)pid == -1) continue;
            h.ClassChosen = true; break;
          }
      }
      ulong panels, p; int t1, t2;
      if (!Field(a.Form, "m_Panels", out panels, out t1) || t1 != T_TABLE || !Field(panels, "Charactor_FilterOrder", out p, out t2) || t2 != T_TABLE) return;
      // (whether the pop-up is on the screen is seen on the pixels: the game shows and hides it on the C# side)
      ulong fp, fd, mp, md; int f1, f2, m1, m2;
      if (Field(p, "m_FactionInfinityGridProxy", out fp, out f1) && f1 == T_TABLE && Field(fp, "m_Data", out fd, out f2) && f2 == T_TABLE) h.FactionList = IntArray(fd);
      if (Field(p, "m_MainInfinityGridProxy", out mp, out m1) && m1 == T_TABLE && Field(mp, "m_Data", out md, out m2) && m2 == T_TABLE) h.ClassCount = EntryCount(md);
    }

    /// <summary>Selected item uid only (one field read, for the fast overlay timer); 0 = none or unreadable.</summary>
    public static long ReadSel(EquipAddrs a) {
      if (a == null || a.Panel == 0) return 0;
      ulong v; int tt;
      return Field(a.Panel, KSel, out v, out tt) && tt == T_INT && (long)v > 0 ? (long)v : 0;
    }

    /// <summary>The gear filter as the list panel holds it (m_FilterConfig, applied at once by the game): the chosen set ids
    /// and main stat ids, the slot shown, whether other filters are on (level, sub stats), «Скрыть надетое».</summary>
    public static bool ReadFilter(EquipAddrs a, out long[] suits, out long[] mainAttrs, out long[] subAttrs, out int part, out bool foreign, out bool hideEquipped) {
      suits = mainAttrs = subAttrs = null; part = -1; foreign = hideEquipped = false;
      if (a == null || a.Panel == 0) return false;
      ulong filter; int tt;
      if (!Field(a.Panel, KFilter, out filter, out tt) || tt != T_TABLE) return false;
      var f = ParseTable(filter, 1, new HashSet<ulong>());
      if (f == null) return false;
      suits = KeyIds(f, "Suits"); mainAttrs = KeyIds(f, "MainAttrs");
      subAttrs = KeyIds(f, "vSubAttrs");   // {[stat id] = priority}; SubAttrs is the same as a list
      var parts = KeyIds(f, "Part"); if (parts.Length == 1) part = (int)parts[0];
      foreach (var k in new[] { "Level" }) {
        var d = f.ContainsKey(k) ? f[k] as Dictionary<string, object> : null;
        if (d != null && d.Count > 0) foreign = true;
      }
      hideEquipped = f.ContainsKey("IsHideEquiped") && f["IsHideEquiped"] is bool && (bool)f["IsHideEquiped"];
      return true;
    }

    // {[721600] = 1, ...}: the ids are the keys
    static long[] KeyIds(Dictionary<string, object> f, string key) {
      var d = f.ContainsKey(key) ? f[key] as Dictionary<string, object> : null;
      var r = new List<long>();
      if (d != null) foreach (var k in d.Keys) { long id; if (k.Length > 2 && long.TryParse(k.Substring(1, k.Length - 2), out id)) r.Add(id); }
      return r.ToArray();
    }

    /// <summary>The sets in the order the game's filter lists them for a gear slot: EquipData.m_EquipFilterConfigs.SuitListWithPart,
    /// the weapon's list for slots 0–1 and the bracer's for 2–4 (Panel_EquipFilterWidget_Common:_CalculateSuitIdsBySelectPart),
    /// sorted by m_SuitSort. Null until the game has built it.</summary>
    public static long[] ReadSuitOrder(EquipAddrs a, int slot) {
      if (a == null || a.EquipData == 0 || slot < 0 || slot > 4) return null;
      ulong cfg, byPart, list; int t1, t2, t3;
      if (!Field(a.EquipData, "m_EquipFilterConfigs", out cfg, out t1) || t1 != T_TABLE) return null;
      if (!Field(cfg, "SuitListWithPart", out byPart, out t2) || t2 != T_TABLE) return null;
      if (!IntKey(byPart, slot <= 1 ? 0 : 2, out list, out t3) || t3 != T_TABLE) return null;
      var r = IntArray(list);
      return r.Length > 0 ? r : null;
    }

    /// <summary>The main stats the filter offers for a gear slot, in its order (EquipData.m_GlobalAttrWithSuit.MainAttrList,
    /// sorted by stat id as the filter widget does). Null until the game has built it.</summary>
    public static long[] ReadMainAttrOrder(EquipAddrs a, int slot) {
      if (a == null || a.EquipData == 0 || slot < 0 || slot > 4) return null;
      ulong glob, byPart, list; int t1, t2, t3;
      if (!Field(a.EquipData, "m_GlobalAttrWithSuit", out glob, out t1) || t1 != T_TABLE) return null;
      if (!Field(glob, "MainAttrList", out byPart, out t2) || t2 != T_TABLE) return null;
      if (!IntKey(byPart, slot, out list, out t3) || t3 != T_TABLE) return null;
      var r = IntArray(list);
      Array.Sort(r);
      return r.Length > 0 ? r : null;
    }

    /// <summary>The sub stats the filter offers for a gear slot, in its order (EquipData.m_GlobalAttrWithSuit.SubAttrList,
    /// sorted by stat id as the filter widget does). Null until the game has built it.</summary>
    public static long[] ReadSubAttrOrder(EquipAddrs a, int slot) {
      if (a == null || a.EquipData == 0 || slot < 0 || slot > 4) return null;
      ulong glob, byPart, list; int t1, t2, t3;
      if (!Field(a.EquipData, "m_GlobalAttrWithSuit", out glob, out t1) || t1 != T_TABLE) return null;
      if (!Field(glob, "SubAttrList", out byPart, out t2) || t2 != T_TABLE) return null;
      if (!IntKey(byPart, slot, out list, out t3) || t3 != T_TABLE) return null;
      var r = IntArray(list);
      Array.Sort(r);
      return r.Length > 0 ? r : null;
    }

    /// <summary>An item's sub stats as the game's filter matches them: EquipData.equips[uid].vViceAttrList[i].iAttrId
    /// (stat type ids, the ones still hidden included). Null when unreadable.</summary>
    public static long[] ReadSubIds(EquipAddrs a, long uid) {
      if (a == null || a.EquipData == 0) return null;
      ulong eqs, it, vl; int ett, itt, vt;
      if (!Field(a.EquipData, "equips", out eqs, out ett) || ett != T_TABLE) return null;
      if (!IntKey(eqs, uid, out it, out itt) || itt != T_TABLE) return null;
      if (!Field(it, "vViceAttrList", out vl, out vt) || vt != T_TABLE) return null;
      var r = new List<long>();
      for (int i = 1; i <= 8; i++) {
        ulong e, v; int et, tt;
        if (!IntKey(vl, i, out e, out et) || et != T_TABLE) break;
        if (Field(e, "iAttrId", out v, out tt) && tt == T_INT && !r.Contains((long)v)) r.Add((long)v);
      }
      return r.ToArray();
    }

    static long[] IntArray(ulong t) {
      var r = new List<long>();
      for (int i = 1; i <= 500; i++) { ulong v; int tt; if (!IntKey(t, i, out v, out tt) || tt != T_INT) break; r.Add((long)v); }
      return r.ToArray();
    }

    /// <summary>Hero whose gear screen is open (EquipData.m_CurrentSelectHeroUid); 0 = none or unreadable.</summary>
    public static long ReadHero(EquipAddrs a) {
      if (a == null || a.EquipData == 0) return 0;
      ulong v; int tt;
      return Field(a.EquipData, KHero, out v, out tt) && tt == T_INT ? (long)v : 0;
    }

    /// <summary>Hero wearing the item now (EquipData.equips[uid].iHeroId, the current table): 0 = in the bag, -1 = unknown.</summary>
    public static long ReadOwner(EquipAddrs a, long uid) {
      if (a == null || a.EquipData == 0) return -1;
      ulong eqs, it, v; int ett, itt, tt;
      if (!Field(a.EquipData, "equips", out eqs, out ett) || ett != T_TABLE) return -1;
      if (!IntKey(eqs, uid, out it, out itt) || itt != T_TABLE) return -1;
      return Field(it, "iHeroId", out v, out tt) && tt == T_INT ? (long)v : -1;
    }

    /// <summary>The gear panel's Lua table as JSON (3 levels, the two big list tables skipped) for the frame diagnostics.</summary>
    public static string DumpPanel(EquipAddrs a) {
      if (a == null || a.Panel == 0) return null;
      var d = ParseTable(a.Panel, 3, new HashSet<ulong>());
      if (d == null) return null;
      foreach (var k in new[] { "m_EquipListRealData", "m_EquipIdToIndex" }) if (d.ContainsKey(k)) d[k] = "<skipped>";
      var sb = new StringBuilder(); J(sb, d);
      return sb.ToString();
    }

    /// <summary>Row / position of one item in the current list (0 = not in it) and the list table (0 = unreadable).</summary>
    public static int RowOfUid(EquipAddrs a, long uid, out int col, out ulong listPtr) {
      col = 0; listPtr = 0;
      if (a == null || a.Panel == 0) return 0;
      ulong idx, list; int t1, t2;
      if (!Field(a.Panel, KPanel, out idx, out t1) || t1 != T_TABLE || !Field(a.Panel, KList, out list, out t2) || t2 != T_TABLE) return 0;
      listPtr = list;
      return RowOf(idx, list, uid, out col);
    }

    // Row of an item uid in the list (1-based, 0 = not in it) and its position in the row (0 = unknown).
    static int RowOf(ulong idx, ulong list, long uid, out int col) {
      col = 0;
      ulong row; int rtt;
      if (!IntKey(idx, uid, out row, out rtt) || rtt != T_INT) return 0;
      long r = (long)row; if (r < 1 || r > 100000) return 0;
      // position inside the row: m_EquipListRealData[r].Item = {uid, uid, uid}
      ulong rowT; int rowTt, itt; ulong items;
      if (!IntKey(list, r, out rowT, out rowTt) || rowTt != T_TABLE) return (int)r;
      if (!Field(rowT, "Item", out items, out itt) || itt != T_TABLE) return (int)r;
      for (int c = 1; c <= 6; c++) {
        ulong u; int utt;
        if (!IntKey(items, c, out u, out utt)) break;
        if (utt == T_INT && (long)u == uid) { col = c; break; }
      }
      return (int)r;
    }

    /// <summary>Row kinds of the list, rows 1..count (index 0 unused): 1 items, 2 section title, 3 empty-section row, 0 unknown.
    /// The game gives them different heights (100 / 44 / 60), needed to turn a row number into a screen position.</summary>
    public static int[] RowTypes(ulong list, int count) {
      var r = new int[count + 1];
      for (int i = 1; i <= count; i++) {
        ulong rowT, v; int tt, vtt;
        if (!IntKey(list, i, out rowT, out tt) || tt != T_TABLE) break;
        if (Field(rowT, "Type", out v, out vtt) && vtt == T_INT) r[i] = (int)(long)v;
      }
      return r;
    }

    // Value of an integer key of a Lua table (array part first, then the hash part).
    static bool IntKey(ulong t, long key, out ulong val, out int tt) {
      val = 0; tt = 0;
      var h = Read(t, 56); if (h == null || h[8] != 5) return false;
      int lsize = h[11]; uint sizearray = BitConverter.ToUInt32(h, 12);
      if (lsize > 20 || sizearray > 1000000) return false;
      if (key >= 1 && key <= sizearray) {
        var b = Read(BitConverter.ToUInt64(h, 16) + (ulong)(key - 1) * 16, 16); if (b == null) return false;
        val = BitConverter.ToUInt64(b, 0); tt = BitConverter.ToInt32(b, 8);
        return tt != T_NIL;
      }
      int nn = 1 << lsize; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return false;
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt64(nb, o + 16) != key) continue;
        val = BitConverter.ToUInt64(nb, o); tt = BitConverter.ToInt32(nb, o + 8);
        return tt != T_NIL;
      }
      return false;
    }
  }
}
