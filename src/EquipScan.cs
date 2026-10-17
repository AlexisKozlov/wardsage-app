// RealmForge extractor - equip helper: reading the game's equipment screen (READ-ONLY).
//
// Same access as the account reader: OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ),
// VirtualQueryEx and ReadProcessMemory. Nothing is ever written to the game and no input is sent to it.
//
// What is read (script tables of the game UI, verified on the live game 2026-09-25):
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
    public bool HeroFromForm;                   // HeroUid comes from the hero screen itself (its displayed hero or last voiced one), not from EquipData
    public long HeroSelUid;                     // EquipData's selected hero (0 = empty), for the log
    public int Part = -1;                       // slot shown in the list, -1 = none (the list is closed: the hero grid shows)
    public int ListCount = -1;                  // items in the list
    public bool HideEquipped, HideEnhanced, HideLocked, FilterActive;
    public Dictionary<long, int> Row = new Dictionary<long, int>();      // plan item uid -> 1-based row (visible list)
    public Dictionary<long, int> Col = new Dictionary<long, int>();      // plan item uid -> 1-based position in the row
    public Dictionary<long, long> Owner = new Dictionary<long, long>();  // plan item uid -> hero uid wearing it (0 = bag)
    public long SelUid;                         // item the player selected in the list (m_CurrentSelectEquipUid), 0 = none
    public int SelRow, SelCol;                  // its 1-based row / position in the row, 0 = not in the list
    public long SelOwner = -1;                  // the hero wearing it (0 = nobody, -1 = unknown)
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
    // the «Артефакт» tab (read only while the host guides an artifact: RFX.SetArtGoal, src/ArtEquip.cs)
    public bool ArtRead;         // the artifact fields below were read
    public bool ArtListOpen;     // m_PanelDatas[10]: the artifact list is open
    public long ArtSel;          // m_PanelDatas[11]: the artifact selected in the list (0 = none)
    public List<long> ArtUids = new List<long>();   // the list in screen order
    public ulong ArtListPtr; public uint ArtRows;    // the list's table and its length
    public RFX.ListRead ArtList; // the list's scroll and grid (null = unreadable)
    public long ArtTargetOwner = -1;   // iHeroId of the goal artifact (-1 = unknown)
    public long ArtWorn = -1;    // the uid of the artifact the hero wears (0 = none, -1 = not read)
  }

  public static partial class RFX {
    const string KGrid = "m_CharactorGrid_InfinityGrid";
    const string KPanel = "m_EquipIdToIndex", KList = "m_EquipListRealData", KFilter = "m_FilterConfig", KHero = "m_CurrentSelectHeroUid", KSel = "m_CurrentSelectEquipUid";

    // One full scan. Must run under Extractor.Gate (RFX keeps its state in static fields).
    public static EquipAddrs FindEquip(ICollection<long> itemUids, Action<string> onLog) {
      OnLog = onLog;
      try {
        LastError = null; LastOpenError = 0;
        var ps = GameInfo.GameProcesses();
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
      CheckForm(a);
      int hsrc; s.HeroUid = HeroShown(a, out hsrc, out s.HeroSelUid);
      s.HeroFromForm = (hsrc == HeroSrcForm && s.HeroSelUid != s.HeroUid) || hsrc == HeroSrcSound;

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
      if (a.Form != 0 && FormActive(a.Form)) {
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
        s.SelOwner = ReadOwner(a, s.SelUid);
      }

      foreach (var uid in itemUids) {
        int c; int r = RowOf(idx, list, uid, out c);
        if (r > 0) { s.Row[uid] = r; if (c > 0) s.Col[uid] = c; }
      }
      return s;
    }

    public const int HeroSrcNone = 0, HeroSrcForm = 1, HeroSrcSelected = 2, HeroSrcSound = 3;

    /// <summary>Picks the hero whose screen is open from the candidates, best first: the form's own displayed hero
    /// (m_SelectHeroData.iHeroId; it follows every swipe and every way in, e.g. a long press on a team screen), then
    /// EquipData's selected hero (can be stale or nil: other forms write it too), then the form's last voiced hero (set
    /// only after the 3D model loads). An «add» slot shown on the form = no hero. Without a live form only EquipData counts.</summary>
    public static long PickHero(bool formAlive, long formHero, bool formAdd, long equipSel, long sound, out int source) {
      source = HeroSrcNone;
      if (formAlive) {
        if (formHero > 0) { source = HeroSrcForm; return formHero; }
        if (formAdd) return 0;
      }
      if (equipSel > 0) { source = HeroSrcSelected; return equipSel; }
      if (formAlive && sound > 0) { source = HeroSrcSound; return sound; }
      return 0;
    }

    /// <summary>The hero whose screen is open (see PickHero); selected = EquipData's value (0 = empty). 0 = unknown / no hero screen.</summary>
    static long HeroShown(EquipAddrs a, out int source, out long selected) {
      ulong v; int tt;
      selected = 0;
      if (a.EquipData != 0 && Field(a.EquipData, KHero, out v, out tt) && tt == T_INT && (long)v > 0) selected = (long)v;
      bool alive = a.Form != 0 && FormAlive(a.Form);
      long formHero = 0, sound = 0; bool add = false;
      if (alive) {
        ulong sd;
        if (Field(a.Form, "m_SelectHeroData", out sd, out tt) && tt == T_TABLE) {
          if (Field(sd, "iHeroId", out v, out tt) && tt == T_INT && (long)v > 0) formHero = (long)v;
          else if (Field(sd, "add", out v, out tt) && tt != T_NIL) add = true;
        }
        if (formHero <= 0 && selected <= 0 && Field(a.Form, "m_LastPlayuSoundId", out v, out tt) && tt == T_INT && (long)v > 0) sound = (long)v;
      }
      bool active = alive && FormActive(a.Form);
      long picked = PickHero(alive, formHero, add, selected, sound, out source);
      if (a.Form != 0 && !active) source = HeroSrcNone;
      return GateHero(a.Form != 0, active, picked);
    }

    // the hero screen is open (EquipData known from before and its hero set): then the scan must find the form too
    /// <summary>The game built the hero screen again (after a battle, say): follow it to the new one. When the window
    /// list's node has moved (the list grew), the form is dropped, so the host scans again.</summary>
    static void CheckForm(EquipAddrs a) {
      // no hero screen known (it was closed - the inventory, the city): look for it again, the player may have opened it
      if (a.Form == 0) {
        ulong f0;
        if (RescanForm(4000, out f0) && f0 != 0) {
          a.Form = f0;
          if (live != null) live.Form = f0;
          RefreshPanel(a);
          L("  hero screen found again");
        }
        return;
      }
      // no node known and the form found by its keys is still shown: it stays
      if (formNode == 0 && FormAlive(a.Form)) return;
      ulong f = LiveForm();
      if (f == a.Form) return;
      // the node is gone, moved, or holds a dead copy (the game rebuilt the screen after a battle): look for the live one
      if (f == 0) {
        if (!RescanForm(4000, out f)) return;   // too soon: the next poll tries again
        if (f == a.Form) return;
      }
      if (f == 0) formNode = 0;
      L(f != 0 ? "  hero screen rebuilt by the game: following it" : "  hero screen lost: scanning again");
      a.Form = f;
      if (live != null) live.Form = f;
      if (f != 0) RefreshPanel(a);
    }

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

    /// <summary>The hero screen as the game's script has it now (read-only). Heroes is the hero grid in display order; it is
    /// read again only when the game builds a new list (GridPtr).</summary>
    public static HeroScreen ReadHeroScreen(EquipAddrs a, HeroScreen prev) {
      var h = new HeroScreen();
      if (a == null || a.EquipData == 0) return h;
      ulong v; int tt;
      int hsrc; long hsel;
      h.Hero = HeroShown(a, out hsrc, out hsel);
      if (a.Form == 0 || !FormActive(a.Form)) return h;   // no form, or hidden (the player left the screen)
      h.FormOk = true;
      if (Field(a.Form, "m_LastType", out v, out tt) && tt == T_INT) h.Tab = (int)(long)v;
      h.SmallCards = Field(a.Form, "m_IsUseSmallCard", out v, out tt) && tt == T_BOOL && v != 0;
      ulong pd; int pdt;
      if (Field(a.Form, "m_PanelDatas", out pd, out pdt) && pdt == T_TABLE && IntKey(pd, 3, out v, out tt) && tt == T_INT) h.Part = (int)(long)v;
      long artGoal = ArtGoalUid;
      if (artGoal > 0) { var lt0 = live; ReadHeroArt(a.Form, lt0 != null ? lt0.ArtOwner : 0, ArtGoalHero, artGoal, h, prev); }
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
        // the class column's choice: its «Все» is a script table with m_ProfessionID -1, a class is the game's config object
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

    /// <summary>An item's sub stats with their values: {stat id, iValue} pairs (EquipData.equips[uid].vViceAttrList; the value
    /// is 0 while the stat is still hidden). Null when unreadable.</summary>
    public static long[][] ReadItemSubs(EquipAddrs a, long uid) {
      if (a == null || a.EquipData == 0) return null;
      return ItemSubs(a.EquipData, uid);
    }

    static long[][] ItemSubs(ulong equipData, long uid) {
      ulong eqs, it, vl; int ett, itt, vt;
      if (equipData == 0 || !Field(equipData, "equips", out eqs, out ett) || ett != T_TABLE) return null;
      if (!IntKey(eqs, uid, out it, out itt) || itt != T_TABLE) return null;
      if (!Field(it, "vViceAttrList", out vl, out vt) || vt != T_TABLE) return null;
      var r = new List<long[]>();
      for (int i = 1; i <= 8; i++) {
        ulong e, v, x; int et, tt, xt;
        if (!IntKey(vl, i, out e, out et) || et != T_TABLE) break;
        if (!Field(e, "iAttrId", out v, out tt) || tt != T_INT) continue;
        long val = Field(e, "iValue", out x, out xt) && xt == T_INT ? (long)x : 0;
        r.Add(new[] { (long)v, val });
      }
      return r.ToArray();
    }

    /// <summary>Is the item on the game's account now: 1 = yes, 0 = no (another account is in the game, or it was sold),
    /// -1 = unknown (the item table is not found yet).</summary>
    public static int ItemOnAccount(long uid) {
      var lt = live; ulong eqs, it; int ett, itt;
      if (lt == null || lt.EquipData == 0 || !Field(lt.EquipData, "equips", out eqs, out ett) || ett != T_TABLE) return -1;
      return IntKey(eqs, uid, out it, out itt) && itt == T_TABLE ? 1 : 0;
    }

    /// <summary>Same as <see cref="ReadItemSubs"/>, from the live tables when the equip scan has not run.</summary>
    public static long[][] ReadLiveItemSubs(long uid) { var lt = live; return lt == null ? null : ItemSubs(lt.EquipData, uid); }

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

    /// <summary>The gear panel's script table as JSON (3 levels, the two big list tables skipped) for the frame diagnostics.</summary>
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

    // ---- how far a list of the game's screen is scrolled: the scroll component of the list (the panel table holds a handle
    // to it, an index into the game's table of the objects its scripts hold) keeps where the list's content was in the last
    // frame (its anchored position, in the game's 1200x675 UI units: y = how far the list is scrolled down)
    static int scrollPid; static ulong scrollGa;

    /// <summary>The content position of the gear list (<paramref name="heroList"/> false) or of the hero list, in UI units;
    /// false when it cannot be read (no layout data for it, another screen, the game changed).</summary>
    public static bool ReadScrollY(EquipAddrs a, bool heroList, out double y) {
      y = 0;
      if (a == null) return false;
      ulong t = heroList ? a.Form : a.Panel;
      if (t == 0) return false;
      ulong rect = ScriptObject(t, heroList ? "m_CharactorGrid_ScrollRect" : "m_EquipList_ScrollRect", "UnityEngine.UI.ScrollRect", a.Pid);
      return rect != 0 && ScrollPos(rect, out y);
    }

    /// <summary>A list of the game's screen as the game itself holds it: how far it is scrolled, its columns and the height
    /// of one row in the scroll's own units. Nothing here comes from the picture.</summary>
    public sealed class ListRead {
      public double ScrollY = double.NaN;   // the content position (units; y = how far the list is scrolled down), NaN = unreadable
      public int Cols;                      // cells per row (the grid's CellPerLine), 0 = unreadable
      public double RowUnits;               // one row in the scroll's units (the grid's LineSize), 0 = unreadable
      public int First = -1, Last = -1;     // the first / last cell (0-based index) the grid has on the screen now (its created cells), -1 = unreadable
      public double FinalRowUnits, FitScale; // (diagnostics) the line size the grid really lays out with, its fit scale
      public bool Has { get { return !double.IsNaN(ScrollY); } }
      public override string ToString() { return Has ? "scroll=" + ScrollY.ToString("0.0") + " cols=" + Cols + " row=" + RowUnits.ToString("0.#") + " final=" + FinalRowUnits.ToString("0.#") + " fit=" + FitScale.ToString("0.###") + " cells=" + First + ".." + Last : "scroll=none"; }
    }

    static bool gridLayoutSaid;

    /// <summary>Reads the scroll and the grid of a list whose owner table (a form or a panel) holds them under
    /// <paramref name="scrollField"/> and <paramref name="gridField"/>; null when the scroll or the grid cannot be read, or the layout data has no grid fields for this game version.</summary>
    public static ListRead ReadList(ulong owner, string scrollField, string gridField, int pid) {
      if (owner == 0) return null;
      // the grid's fields: their offsets come with the layout data of the game version (the "field" section); without them
      // the list is not trusted (null): the caller falls back to the picture
      var ly = Layout.Current;
      int oCols = ly != null ? ly.FieldOffset("InfinityGrid.CellPerLine") : -1, oLine = ly != null ? ly.FieldOffset("InfinityGrid.LineSize") : -1;
      if (oCols < 0 || oLine < 0) {
        if (!gridLayoutSaid) { gridLayoutSaid = true; Log.AppendLine("list grid: the layout data has no grid fields for this game version - the picture is the fallback"); }
        return null;
      }
      var r = new ListRead();
      ulong rect = ScriptObject(owner, scrollField, "UnityEngine.UI.ScrollRect", pid);
      double y;
      if (rect == 0 || !ScrollPos(rect, out y)) return null;
      r.ScrollY = y;
      ulong grid = ScriptObject(owner, gridField, "UI.InfinityGrid", pid);
      if (grid == 0) return null;
      var b = Read(grid + (ulong)oCols, 4);
      if (b != null) { int c = BitConverter.ToInt32(b, 0); if (c >= 1 && c <= 20) r.Cols = c; }
      var l = Read(grid + (ulong)oLine, 4);
      if (l != null) { float ls = BitConverter.ToSingle(l, 0); if (ls > 1 && ls < 2000) r.RowUnits = ls; }
      if (r.Cols < 1 || r.RowUnits <= 0) return null;
      int oFinal = ly.FieldOffset("InfinityGrid.m_finalLineSize"), oFirst = ly.FieldOffset("InfinityGrid.m_crtFirstInstanceIndex"),
          oLast = ly.FieldOffset("InfinityGrid.m_crtLastInstanceIndex"), oFit = ly.FieldOffset("InfinityGrid.m_fitScale");
      if (oFinal >= 0) { var fb = Read(grid + (ulong)oFinal, 4); if (fb != null) { float fl = BitConverter.ToSingle(fb, 0); if (fl > 1 && fl < 2000) r.FinalRowUnits = fl; } }
      if (oFirst >= 0 && oLast >= 0) {
        var fb = Read(grid + (ulong)oFirst, 4); var lb2 = Read(grid + (ulong)oLast, 4);
        if (fb != null && lb2 != null) { int a = BitConverter.ToInt32(fb, 0), z = BitConverter.ToInt32(lb2, 0); if (a >= 0 && z >= a && z < 100000) { r.First = a; r.Last = z; } }
      }
      if (oFit >= 0) { var fb = Read(grid + (ulong)oFit, 4); if (fb != null) { float f = BitConverter.ToSingle(fb, 0); if (f > 0 && f < 100) r.FitScale = f; } }
      return r;
    }

    /// <summary>The inventory's equipment grid (the form's content list); null when it cannot be read.</summary>
    public static ListRead ReadInventoryList(ulong form) { int pid = ListPid(); return pid == 0 ? null : ReadList(form, "m_Content_ScrollRect", "m_Content_InfinityGrid", pid); }

    /// <summary>The foundry's Ascension list (the panel's own list); null when it cannot be read.</summary>
    public static ListRead ReadFoundryList(ulong panel) { int pid = ListPid(); return pid == 0 ? null : ReadList(panel, "m_EquipList_ScrollRect", "m_EquipList_InfinityGrid", pid); }

    // an object the game's scripts hold in a field of a table (the field keeps a handle: an index into the script objects), 0 = none
    static ulong ScriptObject(ulong t, string field, string cls, int pid) {
      var ly = Layout.Current;
      if (ly == null || t == 0) return 0;
      ulong ud; int tt;
      if (!Field(t, field, out ud, out tt) || (tt & 0x0F) != 7 || ud == 0) return 0;
      var ib = Read(ud + 40, 4);   // the handle: an int after the handle object's 40-byte header
      if (ib == null) return 0;
      int idx = BitConverter.ToInt32(ib, 0);
      ulong slots = ScriptObjects(pid);
      if (slots == 0 || idx < 0) return 0;
      var lb = Read(slots + (ulong)ly.Hdr("arrLen"), 8);
      if (lb == null || idx >= BitConverter.ToInt64(lb, 0)) return 0;
      ulong obj = Ptr(slots + (ulong)ly.Hdr("arrData") + (ulong)idx * 16 + 8);   // slot {int next; object obj}
      return IsA(obj, cls) ? obj : 0;
    }

    // a scroll component's content position of the last frame (m_PrevPosition, Vector2)
    static bool ScrollPos(ulong rect, out double y) {
      y = 0;
      var pb = Read(rect + 0xBC, 8);
      if (pb == null) return false;
      float py = BitConverter.ToSingle(pb, 4);
      if (float.IsNaN(py) || float.IsInfinity(py) || Math.Abs(py) > 1e6) return false;
      y = py;
      return true;
    }

    // the slots of the pool of objects the game's scripts hold (its object pool's list), 0 = unknown
    static ulong ScriptObjects(int pid) {
      ulong ga = GameAssemblyOf(pid);
      if (ga == 0) return 0;
      ulong k = ClassAt(ga, "XLua.InternalGlobals");
      if (k == 0) return 0;
      ulong sf = Ptr(k + 0xB8), pool = sf != 0 ? Ptr(sf + 0x10) : 0, tr = pool != 0 ? Ptr(pool + 0x20) : 0;
      if (!IsA(tr, "XLua.ObjectTranslator")) return 0;
      ulong objects = Ptr(tr + 0xC8);
      return objects != 0 ? Ptr(objects + 0x10) : 0;
    }

    static ulong GameAssemblyOf(int pid) {
      if (pid == scrollPid && scrollGa != 0) return scrollGa;
      scrollPid = pid; scrollGa = 0;
      try {
        using (var p = Process.GetProcessById(pid))
          foreach (ProcessModule m in p.Modules)
            if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) { scrollGa = (ulong)(long)m.BaseAddress; break; }
      } catch (Exception) { }
      return scrollGa;
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

    // Value of an integer key of a script table (array part first, then the hash part).
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

    // ---- the hero screen's «Артефакт» tab (READ ONLY; src/ArtEquip.cs holds the steps)
    /// <summary>Form_CharactorMain.m_LastType of the «Артефакт» tab (gear is 3).</summary>
    public const int HeroArtTab = 7;

    /// <summary>The artifact the host guides (0 = none): the hero screen's artifact fields are read only while it is set.</summary>
    public static volatile bool ArtGoalOn;
    static long artGoalUid, artGoalHero;
    public static long ArtGoalUid { get { return System.Threading.Interlocked.Read(ref artGoalUid); } }
    public static long ArtGoalHero { get { return System.Threading.Interlocked.Read(ref artGoalHero); } }
    public static void SetArtGoal(long hero, long uid) {
      System.Threading.Interlocked.Exchange(ref artGoalHero, uid > 0 ? hero : 0);
      System.Threading.Interlocked.Exchange(ref artGoalUid, uid > 0 && hero > 0 ? uid : 0);
      ArtGoalOn = uid > 0 && hero > 0;
    }

    /// <summary>How long a scan of the account's artifacts (who wears what) is reused; tests set 0.</summary>
    public static int ArtScanMs = 800;
    static long artScanHero, artScanGoal, artScanWorn = -1, artScanOwner = -1; static int artScanAt; static bool artScanNeed;

    /// <summary>The hero screen's artifact fields (READ ONLY) into <paramref name="h"/>: <paramref name="form"/> = Form_CharactorMain, <paramref name="arts"/> = the table that owns
    /// m_vArtifacts (ArtifactData). Public for the tests (fake tables).
    ///   form.m_PanelDatas[10]   the artifact list is open (true / the list)       [11]  the uid of the artifact selected in the list
    ///   form.m_Panels.EquipArtifact.m_InfinityGridProxy.m_Data   the list in screen order (1-based tables with iItemUid / iHeroId)
    ///   ArtifactData.m_vArtifacts   every artifact: iItemUid, iHeroId (the hero wearing it, 0 = free)</summary>
    public static void ReadHeroArt(ulong form, ulong arts, long hero, long goal, HeroScreen h, HeroScreen prev) {
      h.ArtRead = false; h.ArtListOpen = false; h.ArtSel = 0; h.ArtTargetOwner = -1; h.ArtWorn = -1;
      ulong pd, v; int tt, pdt;
      bool haveData = Field(form, "m_PanelDatas", out pd, out pdt) && pdt == T_TABLE;
      if (haveData) {
        if (IntKey(pd, 10, out v, out tt)) h.ArtListOpen = tt == T_TABLE || (tt == T_BOOL ? (v & 0xFFFFFFFF) != 0 : tt != T_NIL);
        if (IntKey(pd, 11, out v, out tt)) {
          if (tt == T_INT) h.ArtSel = (long)v;
          else if (tt == T_FLT) { double d = BitConverter.Int64BitsToDouble((long)v); if (d == Math.Floor(d) && d > 0 && d < 9e15) h.ArtSel = (long)d; }
          if (h.ArtSel < 0) h.ArtSel = 0;
        }
      }
      long rowOwner = -1;
      if (h.ArtListOpen) {
        ulong panels, p, proxy, data; int t1, t2, t3, t4;
        if (Field(form, "m_Panels", out panels, out t1) && t1 == T_TABLE && Field(panels, "EquipArtifact", out p, out t2) && t2 == T_TABLE) {
          h.ArtList = ReadHeroArtList(p);
          if (Field(p, "m_InfinityGridProxy", out proxy, out t3) && t3 == T_TABLE && Field(proxy, "m_Data", out data, out t4) && t4 == T_TABLE) {
            h.ArtListPtr = data;
            ReadHeroArtRows(data, goal, h, prev, out rowOwner);
          }
        }
      }
      // who wears the goal artifact now, and what the hero wears: the account's artifact table (a full pass only while the list is closed, throttled)
      long owner = rowOwner, worn = -1;
      // the worn artifact is looked for when the tab is on and the list is closed (which button opens it), and once with the list open; later it is carried over
      bool needWorn = h.Tab == HeroArtTab && (!h.ArtListOpen || prev == null || prev.ArtWorn < 0 || prev.Hero != h.Hero);
      if (!needWorn && prev != null && prev.Hero == h.Hero) worn = prev.ArtWorn;
      if (arts != 0) {
        ulong all; int at;
        if (Field(arts, "m_vArtifacts", out all, out at) && at == T_TABLE) {
          if (owner < 0) { ulong row; int rt; long ow; if (IntKey(all, goal, out row, out rt) && rt == T_TABLE && ArtInt(row, "iHeroId", out ow)) owner = ow; }
          if (needWorn || owner < 0) { long sw; ScanArts(all, hero, goal, needWorn, ref owner, out sw); if (needWorn) worn = sw; }
        }
      }
      h.ArtTargetOwner = owner;
      h.ArtWorn = worn;
      h.ArtRead = haveData && owner >= 0;
    }

    // the hero's worn artifact (the entry with iHeroId == hero) and, when the direct lookup failed, the goal's owner
    static void ScanArts(ulong all, long hero, long goal, bool needWorn, ref long owner, out long worn) {
      worn = -1;
      int now = Environment.TickCount;
      if (ArtScanMs > 0 && artScanHero == hero && artScanGoal == goal && artScanNeed == needWorn && now - artScanAt < ArtScanMs && now >= artScanAt) {
        worn = artScanWorn; if (owner < 0) owner = artScanOwner;
        return;
      }
      long w = needWorn ? 0 : -1, o = owner;
      int n = 0;
      foreach (var t in TableValues(all)) {
        if (++n > 5000) break;
        long hid, uid;
        if (!ArtInt(t, "iHeroId", out hid)) continue;
        if (!ArtInt(t, "iItemUid", out uid)) continue;
        if (needWorn && hid == hero && w == 0 && uid > 0) w = uid;
        if (o < 0 && uid == goal) o = hid;
      }
      worn = w; owner = o;
      artScanHero = hero; artScanGoal = goal; artScanNeed = needWorn; artScanWorn = w; artScanOwner = o; artScanAt = now;
    }

    // the list's rows in screen order (uids). The same table object is reused while the list is unchanged: the previous reading is kept when the row count and
    // the goal's row still hold (a sorted list moves the goal's row)
    static void ReadHeroArtRows(ulong data, long goal, HeroScreen h, HeroScreen prev, out long goalOwner) {
      goalOwner = -1;
      var hd = Read(data, 56);
      uint sizearray = hd != null && hd[8] == 5 ? BitConverter.ToUInt32(hd, 12) : 0;
      if (prev != null && prev.ArtListPtr == data && prev.ArtRows == sizearray && prev.ArtUids.Count > 0) {
        int gi = prev.ArtUids.IndexOf(goal);
        bool ok = true;
        if (gi >= 0) {
          ulong row; int rt; long uid;
          ok = IntKey(data, gi + 1, out row, out rt) && rt == T_TABLE && ArtInt(row, "iItemUid", out uid) && uid == goal;
        }
        if (ok) { h.ArtUids.AddRange(prev.ArtUids); h.ArtRows = prev.ArtRows; if (gi >= 0) goalOwner = GoalRowOwner(data, gi + 1); return; }
      }
      h.ArtRows = sizearray;
      for (int i = 1; i <= 800; i++) {
        ulong row; int rt; long uid;
        if (!IntKey(data, i, out row, out rt) || rt != T_TABLE) break;
        if (!ArtInt(row, "iItemUid", out uid)) uid = 0;
        h.ArtUids.Add(uid);
        if (uid == goal && uid > 0) { long ow; goalOwner = ArtInt(row, "iHeroId", out ow) ? ow : -1; }
      }
    }

    static long GoalRowOwner(ulong data, int index) {
      ulong row; int rt; long ow;
      return IntKey(data, index, out row, out rt) && rt == T_TABLE && ArtInt(row, "iHeroId", out ow) ? ow : -1;
    }

    // the field names the list's scroll may be held under (the first that reads is kept)
    static readonly string[] HeroArtScrollFields = { "m_List_ScrollRect", "m_ListArtifact_ScrollRect", "m_List_ScrollView", "m_List_Scroll", "m_ListArtifact_ScrollView" };
    static readonly string[] HeroArtGridFields = { "m_List_InfinityGrid", "m_ListArtifact_InfinityGrid" };
    static string heroArtScroll, heroArtGrid; static bool heroArtSaid;

    /// <summary>The hero screen's artifact list (the panel's own scroll and grid); null when it cannot be read.</summary>
    public static ListRead ReadHeroArtList(ulong panel) {
      int pid = ListPid(); if (pid == 0) return null;
      if (heroArtScroll != null) return ReadList(panel, heroArtScroll, heroArtGrid, pid);
      foreach (var g in HeroArtGridFields)
        foreach (var name in HeroArtScrollFields) {
          var r = ReadList(panel, name, g, pid);
          if (r != null) { heroArtScroll = name; heroArtGrid = g; L("hero artifact list: the scroll is bound as " + name + ", the grid as " + g); return r; }
        }
      if (!heroArtSaid) { heroArtSaid = true; L("hero artifact list: no scroll field read (tried " + string.Join(", ", HeroArtScrollFields) + ") - no frames on the cells"); }
      return null;
    }

    // an integer field of a script table (an integer, or a float with a whole value)
    static bool ArtInt(ulong t, string key, out long val) {
      ulong v; int tt; val = 0;
      if (!Field(t, key, out v, out tt)) return false;
      if (tt == T_INT) { val = (long)v; return true; }
      if (tt == T_FLT) { double d = BitConverter.Int64BitsToDouble((long)v); if (d == Math.Floor(d) && Math.Abs(d) < 1e15) { val = (long)d; return true; } }
      return false;
    }
  }
}
