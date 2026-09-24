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
//   * item tables (iItemUid, iHeroId) of the plan items — who wears each item now.
// FindEquip() scans the memory once (tens of seconds); Poll() then re-reads the found tables several times a second.
using System;
using System.Collections.Generic;
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
  }

  public static partial class RFX {
    const string KPanel = "m_EquipIdToIndex", KList = "m_EquipListRealData", KFilter = "m_FilterConfig", KHero = "m_CurrentSelectHeroUid";

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

      foreach (var uid in itemUids) {
        ulong it; if (!a.Items.TryGetValue(uid, out it)) continue;
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

      foreach (var uid in itemUids) {
        ulong row; int rtt;
        if (!IntKey(idx, uid, out row, out rtt) || rtt != T_INT) continue;
        long r = (long)row; if (r < 1 || r > 100000) continue;
        s.Row[uid] = (int)r;
        // position inside the row: m_EquipListRealData[r].Item = {uid, uid, uid}
        ulong rowT; int rowTt;
        if (!IntKey(list, r, out rowT, out rowTt) || rowTt != T_TABLE) continue;
        ulong items; int itt;
        if (!Field(rowT, "Item", out items, out itt) || itt != T_TABLE) continue;
        for (int c = 1; c <= 6; c++) {
          ulong u; int utt;
          if (!IntKey(items, c, out u, out utt)) break;
          if (utt == T_INT && (long)u == uid) { s.Col[uid] = c; break; }
        }
      }
      return s;
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
