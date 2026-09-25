// RealmForge extractor - equip helper: reading the game's equipment screen (READ-ONLY).
//
// Same access as the account reader: OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ),
// VirtualQueryEx and ReadProcessMemory. Nothing is ever written to the game and no input is sent to it.
//
// What is read (Lua tables of the game UI, verified on the live game 2026-09-25):
//   * the equipment list panel (the table that owns m_EquipIdToIndex): m_EquipIdToIndex (item uid -> row of
//     m_EquipListRealData), m_EquipListRealData (rows {Type=1, Item={uid, uid, uid}}), m_FilterConfig (Part = shown slot,
//     IsHideEquiped, IsHideEnhanced, Suits, MainAttrs, ...);
//   * EquipData.m_CurrentSelectHeroUid — the hero whose gear screen is open;
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
    public Dictionary<long, ulong> Items = new Dictionary<long, ulong>();   // plan item uid -> item table
  }

  public sealed class EquipLive {
    public bool GameRunning = true;
    public bool PanelOk;                        // the panel table is still readable
    public long HeroUid;                        // 0 = unknown
    public int Part = -1;                       // slot shown in the list, -1 = none (hero screen without a chosen slot)
    public int ListCount = -1;                  // items in the list
    public bool HideEquipped, HideEnhanced, HideLocked, FilterActive;
    public Dictionary<long, int> Row = new Dictionary<long, int>();      // plan item uid -> 1-based row (visible list)
    public Dictionary<long, int> Col = new Dictionary<long, int>();      // plan item uid -> 1-based position in the row
    public Dictionary<long, long> Owner = new Dictionary<long, long>();  // plan item uid -> hero uid wearing it (0 = bag)
    public long SelUid;                         // item the player selected in the list (m_CurrentSelectEquipUid), 0 = none
    public int SelRow, SelCol;                  // its 1-based row / position in the row, 0 = not in the list
    public ulong ListPtr;                       // m_EquipListRealData table: a new one on every list rebuild
  }

  public static partial class RFX {
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
        regs = Regions();
        var tstr = FindLuaStrings(new[] { KPanel, KHero, "iStarLvl", "iItemUid" });

        foreach (var t in TablesWithKeys(tstr, KPanel)) { ulong v; int tt; if (Field(t, KList, out v, out tt) && tt == T_TABLE) { a.Panel = t; break; } }
        foreach (var t in TablesWithKeys(tstr, KHero)) { ulong v; int tt; if (Field(t, KHero, out v, out tt)) { a.EquipData = t; break; } }

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
        L("  panel " + (a.Panel != 0) + ", hero " + (a.EquipData != 0) + ", items " + a.Items.Count);
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

      if (a.Panel == 0) return s;
      ulong idx, list, filter; int t1, t2, t3;
      if (!Field(a.Panel, KPanel, out idx, out t1) || t1 != T_TABLE || !Field(a.Panel, KList, out list, out t2) || t2 != T_TABLE) return s;
      s.PanelOk = true;

      if (Field(a.Panel, KFilter, out filter, out t3) && t3 == T_TABLE) {
        var f = ParseTable(filter, 1, new HashSet<ulong>());
        if (f != null) {
          var part = f.ContainsKey("Part") ? f["Part"] as Dictionary<string, object> : null;
          if (part != null && part.Count == 1) foreach (var kv in part) if (kv.Value is long) s.Part = (int)(long)kv.Value;
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

    /// <summary>Selected item uid only (one field read, for the fast overlay timer); 0 = none or unreadable.</summary>
    public static long ReadSel(EquipAddrs a) {
      if (a == null || a.Panel == 0) return 0;
      ulong v; int tt;
      return Field(a.Panel, KSel, out v, out tt) && tt == T_INT && (long)v > 0 ? (long)v : 0;
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
