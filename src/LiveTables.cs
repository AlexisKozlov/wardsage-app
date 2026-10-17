// RealmForge extractor - the game's live script tables, found once per game session (READ-ONLY).
//
// The account and the equip helper both need a few tables of the game's script state: EquipData (items: equips, the
// hero on the gear screen), HeroData (heroes: m_CharactorDatas), the tables holding the artifacts and the faction
// rewards, and the hero screen (Form_CharactorMain, its gear list panel). They are created at login and live as long as
// the game runs (their contents change, the tables do not move), so the slow part - finding them in ~2 GB of memory -
// is done once: afterwards a sync reads them directly (a second or two instead of a minute and more).
//
// The search itself: short script strings are interned and 8-byte aligned (TString: tt at +8, length at +11, text at +24),
// so the key names are found by checking aligned positions only; then one pass finds the table nodes keyed by any of
// them and one more the tables owning those nodes. The passes run on all processor cores.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RealmForge {
  public sealed class LiveTables {
    public int Pid;
    public ulong EquipData, HeroData, CampOwner, ArtOwner, BeastOwner, ActOwner, Form, Panel;
    public ulong ItemData, PlayerData;   // the bag (ItemData) and the currencies (PlayerData): "resources"
    public ulong UnionWeekBoss;          // UnionWeekBossData: the gear saved with the Zerbus teams (m_SavedTeamEquipsInfo)
    public ulong GameData;               // GameData: its frame counter (m_Knock) moves while the game runs
    public ulong State;                  // the script state (login) the tables belong to: the address of its instance id key; 0 = not known
    public bool Ambiguous;               // memory holds the tables of several logins and the running one could not be told
    public string Note;                  // how the state was chosen (for the log)
  }

  public static partial class RFX {
    static LiveTables live;
    const string KCamp = "m_CampHeroPerfectReward", KArts = "m_vArtifacts", KHeroes = "m_CharactorDatas", KBeasts = "m_AllBeastInfo", KEquipData = "suitId2EquipIdExt";
    // summoning: the open pools (ActivityData, activities of type 54) and the player's counters (PlayerData = the beasts' owner)
    const string KAct = "m_ActData54";
    static readonly string[] SummonPlayerKeys = { "m_SoftMustFivesNum", "m_SoftId2LotteryNum", "m_MustFiveNum", "m_mLotteryNum", "m_iTotalLotteryNum", "m_AwakePity", "m_LotteryRecord" };

    /// <summary>The live tables of the running game: the ones found before when they are still there, else searched for
    /// (again). <paramref name="needForm"/>: also the hero screen (created when the player first opens it).</summary>
    /// <summary>The live tables are searched again on the next use (the helper's plan stood still: an item put on was not
    /// seen until the app was restarted, live 27.09).</summary>
    public static void ForgetLive() { live = null; keyText.Clear(); }

    // ------------------------------------------------------------------ the tables for the on-screen hints, without an account read
    // The screen readers (lists, forms) need the process handle H, the memory regions (regs) and the live tables (their pid
    // reaches the script-object pool); the first account read sets them up about a minute after the app starts. A hint asked
    // for earlier found them missing for the whole session. This does just that discovery (no account read, no upload).
    static int guideTryAt, guideBusy, readerPid, guidePid; static string guideWhy;

    /// <summary>The retry gate: true when no try was made yet (lastTick 0) or at least <paramref name="gapMs"/> passed since it.</summary>
    public static bool GuideTryDue(int lastTick, int nowTick, int gapMs) { return lastTick == 0 || unchecked(nowTick - lastTick) >= gapMs; }

    /// <summary>The handle, the regions and the live tables are all there (nothing to find for the hints).</summary>
    static bool LiveReady() { var lt = live; return H != IntPtr.Zero && regs != null && lt != null && (lt.EquipData != 0 || lt.HeroData != 0); }

    /// <summary>Non-blocking: when the readers' basis is missing, ONE background job finds it (skipped while an account read or
    /// another scan holds Extractor.Gate; tried again at most every 3 s). Logs the result.</summary>
    public static void EnsureLiveForGuides() {
      if (LiveReady()) return;
      int now = Environment.TickCount; if (now == 0) now = 1;
      if (!GuideTryDue(guideTryAt, now, 3000)) return;
      if (Interlocked.CompareExchange(ref guideBusy, 1, 0) != 0) return;
      guideTryAt = now;
      Task.Factory.StartNew(() => {
        try { GuideDiscovery(); }
        catch (Exception e) { L("live: not found (" + e.GetType().Name + ": " + e.Message + ")"); }
        finally { guideBusy = 0; }
      });
    }

    static void GuideDiscovery() {
      if (!Monitor.TryEnter(Extractor.Gate)) return;   // an account read / scan runs: it sets the same things up
      try {
        if (LiveReady()) return;
        var sw = Stopwatch.StartNew();
        string why = null;
        int pid = GameInfo.FindGameProcess();
        if (pid == 0) why = "the game is not running";
        else if (Layout.Current == null) why = "no layout data yet";
        else {
          if (H == IntPtr.Zero || guidePid != pid) { H = OpenProcess(0x0410, false, pid); guidePid = pid; }
          if (H == IntPtr.Zero) why = "cannot open the game process";
          else {
            readerPid = pid;
            var lt = EnsureLive(pid, false);
            if (lt.EquipData == 0 && lt.HeroData == 0) why = "the account tables are not in memory yet";
          }
        }
        string said = why == null ? "ok" : why;
        if (why == null) L("live: found for the hints in " + sw.ElapsedMilliseconds + " ms");
        else if (said != guideWhy) L("live: not found (" + why + ")");
        guideWhy = said;
      } finally { Monitor.Exit(Extractor.Gate); }
    }

    /// <summary>The game pid the list readers use: the live tables' one, else the one the readers' handle was opened for.</summary>
    static int ListPid() { var lt = live; return lt != null ? lt.Pid : readerPid; }

    internal static LiveTables EnsureLive(int pid, bool needForm) {
      var lt = live; LastFindFresh = false; readerPid = pid;
      if (lt != null && lt.Pid == pid && HasTable(lt.EquipData, "equips") && HasTable(lt.HeroData, KHeroes)
          && (!needForm || Layout.Current == null || (HasTable(lt.Form, "m_InfinityGridProxy") && LiveForm() == lt.Form))) {
        string why = StateGone(lt);
        if (why == null) return lt;
        ForgetKeys(why);
      }
      var sw = Stopwatch.StartNew();
      var before = lt != null && lt.Pid == pid ? lt.State : 0;
      lt = FindLive(pid);
      L("Live tables found in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s: equip " + (lt.EquipData != 0) + ", heroes " + (lt.HeroData != 0)
        + ", artifacts " + (lt.ArtOwner != 0) + ", rewards " + (lt.CampOwner != 0) + ", hero screen " + (lt.Form != 0)
        + ", bag " + (lt.ItemData != 0) + ", player " + (lt.PlayerData != 0));
      L("Script state: " + lt.Note + (before != 0 && lt.State != 0 && lt.State != before ? "; a new login since the last search" : ""));
      if (before != 0 && lt.State != before) subValues = null;   // the server copies of another login: collected again
      live = lt; LastFindFresh = true;
      return lt;
    }

    /// <summary>The last EnsureLive searched the tables (they were not the ones found before).</summary>
    [ThreadStatic] static bool LastFindFresh;

    // ------------------------------------------------------------------ the script state (login) the tables belong to
    //
    // The game builds its whole script state again at a re-login or an account switch, and memory can hold the tables of the
    // earlier state for a long time. Every object the game's classes make carries the key ____instanceId, and a key's string
    // object is one per state: the address of that key string tells which state a table belongs to. GameData (the owner of
    // m_bInitElva) adds the time of each frame to m_Knock, so the state whose GameData's m_Knock moves is the one the game
    // runs. All the tables of a reading come from that one state.
    const string KGameData = "m_bInitElva", KInst = "____instanceId", KKnock = "m_Knock";
    const int KnockWaitMs = 1000, KnockStepMs = 20;

    /// <summary>The script state a table belongs to (the address of its ____instanceId key string), 0 = not an instance.</summary>
    static ulong StateKey(ulong t) { ulong v, k; int tt; return t != 0 && FieldKey(t, KInst, out v, out tt, out k) ? k : 0; }

    static string KnockSample(ulong gd) { ulong v; int tt; return Field(gd, KKnock, out v, out tt) && (tt == T_FLT || tt == T_INT) ? tt + ":" + v : null; }

    /// <summary>Of the GameData tables, the ones whose frame counter moves within <paramref name="maxMs"/> (once one has moved,
    /// the others get as long again).</summary>
    static HashSet<ulong> Moving(IList<ulong> gds, int maxMs) {
      var first = new Dictionary<ulong, string>(); var moved = new HashSet<ulong>();
      foreach (var g in gds) { var s0 = KnockSample(g); if (s0 != null) first[g] = s0; }
      var sw = Stopwatch.StartNew(); long movedAt = -1;
      while (moved.Count < first.Count && sw.ElapsedMilliseconds < maxMs) {
        Thread.Sleep(KnockStepMs);
        foreach (var kv in first) if (!moved.Contains(kv.Key)) { var s1 = KnockSample(kv.Key); if (s1 != null && s1 != kv.Value) moved.Add(kv.Key); }
        if (moved.Count > 0 && movedAt < 0) movedAt = sw.ElapsedMilliseconds;
        if (movedAt >= 0 && sw.ElapsedMilliseconds >= 2 * movedAt + KnockStepMs) break;
      }
      return moved;
    }

    /// <summary>Why the live tables found before may no longer be the game's (null: they are).</summary>
    static string StateGone(LiveTables lt) {
      if (lt.Ambiguous) return "the login of the tables found before was not known";
      if (lt.State == 0) return null;   // the game's objects carry no state mark: checked as before (tables still there)
      foreach (var t in new[] { lt.GameData, lt.EquipData, lt.HeroData, lt.PlayerData })
        if (t != 0 && StateKey(t) != lt.State) return "a live table no longer belongs to the login it was found in";
      if (lt.GameData != 0 && Moving(new[] { lt.GameData }, KnockWaitMs).Count == 0) return "the game showed no frames: another login may have replaced the tables";
      return null;
    }

    /// <summary>The tables of the state (state 0: all); tables without a state mark are kept after them.</summary>
    static List<ulong> InState(List<ulong> l, ulong state) {
      if (state == 0 || l == null) return l;
      var r = new List<ulong>(); var unmarked = new List<ulong>();
      foreach (var t in l) { ulong st = StateKey(t); if (st == state) r.Add(t); else if (st == 0) unmarked.Add(t); }
      r.AddRange(unmarked);
      return r;
    }

    /// <summary>Chooses the script state of the reading (sets State, GameData, Ambiguous, Note) and keeps only its tables in
    /// <paramref name="owners"/>.</summary>
    static void ChooseState(Dictionary<string, List<ulong>> owners, LiveTables lt) {
      var byState = new Dictionary<ulong, ulong>();   // state -> its GameData
      List<ulong> gds;
      if (owners.TryGetValue(KGameData, out gds))
        foreach (var g in gds) { if (!HasNum(g, KKnock)) continue; ulong st = StateKey(g); if (st != 0 && !byState.ContainsKey(st)) byState[st] = g; }
      if (byState.Count > 0) {
        var moving = Moving(new List<ulong>(byState.Values), KnockWaitMs);
        var runs = new List<ulong>(); foreach (var kv in byState) if (moving.Contains(kv.Value)) runs.Add(kv.Key);
        if (byState.Count > 1 && runs.Count == 1) { lt.State = runs[0]; lt.Note = "the running one of " + byState.Count; }
        else if (byState.Count == 1) { foreach (var k in byState.Keys) lt.State = k; lt.Note = "the only one (" + (runs.Count == 1 ? "running" : "no frames seen") + ")"; }
        else { lt.Ambiguous = true; lt.Note = byState.Count + " logins in memory, " + (runs.Count == 0 ? "none seen running (the game showed no frames)" : runs.Count + " seen running"); }
        if (lt.State != 0) lt.GameData = byState[lt.State];
      } else {
        // no frame counter (another game version): several states of the hero table cannot be told apart
        var heroStates = new HashSet<ulong>(); List<ulong> hl;
        if (owners.TryGetValue(KHeroes, out hl)) foreach (var t in hl) { ulong st = StateKey(t); if (st != 0) heroStates.Add(st); }
        if (heroStates.Count > 1) { lt.Ambiguous = true; lt.Note = heroStates.Count + " logins in memory, no frame counter found"; }
        else lt.Note = "no frame counter found; " + (heroStates.Count == 1 ? "one login in memory" : "no login marks");
      }
      // without a running state known: the state of the largest hero table, so that the reading at least never mixes logins
      if (lt.State == 0) {
        ulong hd = Largest(owners, KHeroes); lt.State = StateKey(hd);
        ulong g; if (lt.State != 0 && byState.TryGetValue(lt.State, out g)) lt.GameData = g;
      }
      if (lt.State == 0) return;
      foreach (var key in new List<string>(owners.Keys)) owners[key] = InState(owners[key], lt.State);
    }

    /// <summary>A table of the reading that belongs to another script state than the reading (null: none).</summary>
    static string MixedState(LiveTables lt) {
      if (lt.State == 0) return null;
      string[] names = { "equipment", "heroes", "player", "bag", "rewards", "artifacts", "beasts", "activities", "zerbus" };
      ulong[] ts = { lt.EquipData, lt.HeroData, lt.PlayerData, lt.ItemData, lt.CampOwner, lt.ArtOwner, lt.BeastOwner, lt.ActOwner, lt.UnionWeekBoss };
      for (int i = 0; i < ts.Length; i++) { ulong st = StateKey(ts[i]); if (st != 0 && st != lt.State) return "the " + names[i] + " table belongs to another login"; }
      return null;
    }

    static int stateCheckedAt; static ulong stateChecked; static bool stateCheckedLive;

    /// <summary>The script state the game runs now: the live tables' one while its frame counter moves (checked at most every
    /// 5 s); 0 = not known (then the readers choose as before).</summary>
    internal static ulong CurrentState() {
      var lt = live; if (lt == null || lt.State == 0 || lt.GameData == 0 || lt.Ambiguous) return 0;
      if (stateCheckedAt != 0 && Environment.TickCount - stateCheckedAt < 5000 && stateChecked == lt.State) return stateCheckedLive ? lt.State : 0;
      stateCheckedLive = StateKey(lt.GameData) == lt.State && Moving(new[] { lt.GameData }, KnockWaitMs).Count > 0;
      stateChecked = lt.State; stateCheckedAt = Environment.TickCount;
      return stateCheckedLive ? lt.State : 0;
    }

    /// <summary>A table found earlier still belongs to the state the game runs (or no state is known).</summary>
    static bool InCurrentState(ulong t) { ulong st = CurrentState(); if (st == 0) return true; ulong k = StateKey(t); return k == 0 || k == st; }

    static long lastUid; static int lastUidPid;
    /// <summary>Why the last reading gave no account (with LastError "not_ready").</summary>
    public static string LastDetail;

    static long PlayerUid(LiveTables lt) {
      ulong pv; int pt; long uid;
      return lt.PlayerData != 0 && Field(lt.PlayerData, "m_Uid", out pv, out pt) && AsLong(pv, pt, out uid) && uid > 0 ? uid : 0;
    }

    /// <summary>account.json from the live tables of the game process <paramref name="pid"/>. Null when they are not all
    /// found (the caller does the full scan) or, with LastError "not_ready", when it is not known which login they belong to.</summary>
    public static string ReadLiveAccount(int pid) { return ReadLiveAccount(pid, Stopwatch.StartNew()); }

    static string ReadLiveAccount(int pid, Stopwatch sw) {
      LastDetail = null;
      var lt = EnsureLive(pid, false); bool fresh = LastFindFresh;
      if (lt.EquipData == 0 || lt.HeroData == 0) return null;
      long uid = PlayerUid(lt);
      // another player than at the last reading of this game session, from tables found earlier: they are looked for again
      if (!fresh && uid > 0 && lastUidPid == pid && lastUid > 0 && uid != lastUid) {
        L("Player " + uid + " is not the player of the last reading (" + lastUid + "): the live tables are looked for again");
        ForgetLive(); ForgetKeys("the player changed");
        lt = EnsureLive(pid, false); fresh = true;
        if (lt.EquipData == 0 || lt.HeroData == 0) return null;
        uid = PlayerUid(lt);
      }
      string mixed = lt.Ambiguous ? lt.Note : MixedState(lt);
      if (mixed != null && !fresh) {
        ForgetLive(); ForgetKeys(mixed);
        lt = EnsureLive(pid, false);
        if (lt.EquipData == 0 || lt.HeroData == 0) return null;
        uid = PlayerUid(lt);
        mixed = lt.Ambiguous ? lt.Note : MixedState(lt);
      }
      if (mixed != null) {
        LastError = "not_ready"; LastDetail = "the game's tables of more than one login are in memory (" + mixed + ")";
        L("Not sent: " + LastDetail);
        return null;
      }
      var json = AccountFromLive(lt, sw, uid);
      if (json != null && uid > 0) {
        if (lastUidPid == pid && lastUid != uid) L("Player of this reading: " + uid + (lastUid > 0 ? " (was " + lastUid + ")" : ""));
        lastUid = uid; lastUidPid = pid;
      }
      return json;
    }

    // ------------------------------------------------------------------ the live hero screen

    // The game keeps its open windows in UIStatic's UIInstance (form id -> form). After a scene change (a battle) it
    // builds the hero screen again, and the old table stays in memory for a while - with the same fields and even the
    // same ____instanceId - so a table found by its keys can be the dead copy. The node of UIInstance keyed by the hero
    // screen's id always holds the live one: found once, then read on every poll (8 bytes). The form ids come with the
    // layout data from the server (src/Layout.cs); without it no form is found this way (0).
    /// <summary>A form id (UIDefines) from the layout data; 0 when there is none.</summary>
    internal static long FormId(string name) { var ly = Layout.Current; return ly != null ? ly.Form(name) : 0; }
    static long FormCharactorMainId { get { return FormId("ID_FORM_CHARACTORMAIN"); } }
    internal static long FormBackpackId { get { return FormId("ID_FORM_BACKPACKINTEGRATION"); } }   // «Инвентарь»
    internal static long FormRecastId { get { return FormId("ID_FORM_EQUIPSUBLIMING"); } }          // the foundry (Литейная): its «Преобразовать» tab
    internal static long FormGemId { get { return FormId("ID_FORM_EQUIPGEMMAIN"); } }               // the gem screen: reached from the item card, also holds the recast functions
    internal static long FormMainCityId { get { var ly = Layout.Current; return ly != null ? ly.FormOpt("ID_FORM_MAINCITY") : 0; } }   // the city (main screen); older layout data lacks it: 0
    static ulong formNode, invNode, subNode, gemNode, cityNode;

    // the value of a UIInstance node keyed by the form id, when it is still that node and holds a table with the key
    static ulong NodeValue(ulong n, long id, string key) {
      var b = n != 0 && id != 0 ? Read(n, 32) : null; if (b == null) return 0;
      if (BitConverter.ToInt64(b, 16) != id || BitConverter.ToInt32(b, 24) != T_INT || BitConverter.ToInt32(b, 8) != T_TABLE) return 0;
      ulong f = BitConverter.ToUInt64(b, 0);
      return HasTable(f, key) ? f : 0;
    }
    static ulong NodeForm(ulong n) { ulong f = NodeValue(n, FormCharactorMainId, "m_InfinityGridProxy"); return f != 0 && FormAlive(f) ? f : 0; }

    /// <summary>A hero screen the game still shows: its grid component is set. When the game closes a screen it clears the
    /// component fields (nil) and the table lingers - and so can an old node array of UIInstance (after a rehash) that
    /// still points at it. Reading such a dead copy, the pilots saw the old grid order and no gear list (27.09: clicks
    /// on the wrong heroes, «slot does not open»).</summary>
    internal static bool FormAlive(ulong f) { ulong v; int tt; return f != 0 && Field(f, KGrid, out v, out tt) && tt != T_NIL; }

    /// <summary>The form is on screen right now: UIBase:OnActive sets m_LuaLogicActive = true, OnPreInactive clears it (nil)
    /// (UI/Common/UIBase.lua:350 / :418). A hidden form stays in memory with all its fields (m_SelectHeroData too), so
    /// FormAlive alone does not mean «shown».</summary>
    internal static bool FormActive(ulong f) { ulong v; int tt; return FormAlive(f) && Field(f, "m_LuaLogicActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0; }

    /// <summary>The hero of the hero screen, or 0 when the form is known but hidden (the player left the screen): stale
    /// fields of a hidden form and EquipData's old selection must not count as «the hero screen is open».</summary>
    public static long GateHero(bool formKnown, bool formActive, long hero) { return formKnown && !formActive ? 0 : hero; }

    /// <summary>The live hero screen searched again (a full pass, about a second), at most every <paramref name="ms"/>;
    /// false when it is too soon (then <paramref name="form"/> is not known).</summary>
    internal static bool RescanForm(int ms, out ulong form) {
      form = 0;
      if (regs == null || Layout.Current == null) return false;
      if (lastNodeScan != 0 && Environment.TickCount - lastNodeScan < ms) return false;
      lastNodeScan = Environment.TickCount;
      form = FindFormNode();
      return true;
    }
    static ulong NodeInv(ulong n) { return NodeValue(n, FormBackpackId, "m_CachedContentPanels"); }

    // the table of a UIInstance node keyed by the form id, when it is still that node
    static ulong NodeTable(ulong n, long id) {
      var b = n != 0 && id != 0 ? Read(n, 32) : null; if (b == null) return 0;
      if (BitConverter.ToInt64(b, 16) != id || BitConverter.ToInt32(b, 24) != T_INT || BitConverter.ToInt32(b, 8) != T_TABLE) return 0;
      return BitConverter.ToUInt64(b, 0);
    }

    /// <summary>Searches UIInstance's nodes of the hero screen, the inventory and the two recast screens (one pass; a
    /// form the player has not opened yet has none); returns the live hero screen (0 = none).</summary>
    static ulong FindFormNode() {
      lock (formScanLock) { return FindFormNodeLocked(); }
    }
    static readonly object formScanLock = new object();

    // the new values are built into locals and published at the end: a reader never sees a 0 from the middle of a scan
    static ulong FindFormNodeLocked() {
      ulong nForm = 0, nInv = 0, nSub = 0, nGem = 0, nCity = 0;
      if (Layout.Current == null) { formNode = invNode = subNode = gemNode = cityNode = 0; return 0; }   // no form ids: the hero screen is found by its keys
      int t0 = Environment.TickCount;
      long idMain = FormCharactorMainId, idInv = FormBackpackId, idRecast = FormRecastId, idGem = FormGemId, idCity = FormMainCityId;
      var hits = new List<ulong>();
      ScanParallel((b0, buf, len) => {
        for (int i = 0; i + 32 <= len; i += 8) {
          long k = BitConverter.ToInt64(buf, i + 16);
          if (k != idMain && k != idInv && k != idRecast && k != idGem && (idCity == 0 || k != idCity)) continue;
          if (BitConverter.ToInt32(buf, i + 24) != T_INT || BitConverter.ToInt32(buf, i + 8) != T_TABLE) continue;
          lock (hits) hits.Add(b0 + (ulong)i);
        }
      });
      ulong form = 0;
      var invHits = new List<ulong>(); var subHits = new List<ulong>(); var gemHits = new List<ulong>(); var cityHits = new List<ulong>();
      foreach (var n in hits) {
        ulong f;
        if (nForm == 0 && (f = NodeForm(n)) != 0) { nForm = n; form = f; }
        else if (NodeInv(n) != 0) invHits.Add(n);
        else if (NodeTable(n, FormRecastId) != 0) subHits.Add(n);
        else if (NodeTable(n, FormGemId) != 0) gemHits.Add(n);
        else if (idCity != 0 && NodeTable(n, idCity) != 0) cityHits.Add(n);
      }
      // several copies of one form can linger (an old node array after a rehash, the table of an earlier open): the one the game shows wins
      Func<ulong, bool> invShown = n => InvFormShown(NodeInv(n));
      Func<ulong, bool> subShown = n => InvFormShown(NodeTable(n, FormRecastId));
      Func<ulong, bool> gemShown = n => InvFormShown(NodeTable(n, FormGemId));
      Func<ulong, bool> cityShown = n => InvFormShown(NodeTable(n, FormMainCityId));
      nCity = PickNode(cityHits, cityShown);
      nInv = PickNode(invHits, invShown); nSub = PickNode(subHits, subShown); nGem = PickNode(gemHits, gemShown);
      formNode = nForm; invNode = nInv; subNode = nSub; gemNode = nGem; cityNode = nCity;
      int ki = CountWhere(invHits, invShown), ks = CountWhere(subHits, subShown), kg = CountWhere(gemHits, gemShown);
      string line = "inventory " + invHits.Count + " (shown " + ki + "), foundry " + subHits.Count + " (shown " + ks + "), gem " + gemHits.Count + " (shown " + kg + "), city " + cityHits.Count + " (shown " + CountWhere(cityHits, cityShown) + ")";
      if (line != formsScanSaid) { formsScanSaid = line; Log.AppendLine("forms: scan " + (Environment.TickCount - t0) + " ms, " + line); }
      return form;
    }
    static string formsScanSaid = "";

    static int CountWhere(IList<ulong> l, Func<ulong, bool> p) { int n = 0; foreach (var x in l) if (p(x)) n++; return n; }

    /// <summary>Of the nodes found for one form id the one whose table is shown; none shown = the first; none = 0.</summary>
    public static ulong PickNode(IList<ulong> hits, Func<ulong, bool> shown) {
      if (hits == null || hits.Count == 0) return 0;
      foreach (var n in hits) if (shown(n)) return n;
      return hits[0];
    }

    // ---- the background search: one at a time, off the UI thread; the readers use the nodes it publishes on later ticks
    static int formSearchBusy, lastFormSearchAt; static string formSearchSaid = "";
    static int lostBits; static bool invOk, subOk, gemOk, cityOk, heroOk;
    static readonly string[] lostNames = { "inventory", "foundry", "gem", "city", "hero" };

    /// <summary>A form node that read fine on the last call reads 0 now (the game moved it): remembered for the overlay (TakeFormsLost).</summary>
    static void Watch(ref bool ok, ulong value, ulong node, int bit) {
      if (value != 0) ok = true;
      else if (ok && node != 0) { ok = false; { int o; do o = lostBits; while (Interlocked.CompareExchange(ref lostBits, o | (1 << bit), o) != o); } }
    }
    /// <summary>The forms that were readable and are not now, since the last call ("inventory, foundry"); null = none.</summary>
    public static string TakeFormsLost() {
      int b = Interlocked.Exchange(ref lostBits, 0); if (b == 0) return null;
      var l = new List<string>(); for (int i = 0; i < lostNames.Length; i++) if ((b & (1 << i)) != 0) l.Add(lostNames[i]);
      return string.Join(", ", l.ToArray());
    }
    /// <summary>The inventory's node is not known (the storage start asks for a search while the form is not built yet).</summary>
    public static bool InvFormMissing() { return NodeInv(invNode) == 0; }

    /// <summary>Starts a search of the forms on a worker thread (never two at once, at least <paramref name="minGapMs"/> after the last start); false = not started.
    /// Does not block.</summary>
    public static bool RequestFormSearch(string reason, int minGapMs) {
      if (regs == null || Layout.Current == null) return false;
      int now = Environment.TickCount;
      if (lastFormSearchAt != 0 && now - lastFormSearchAt < minGapMs) return false;
      if (Interlocked.CompareExchange(ref formSearchBusy, 1, 0) != 0) return false;
      lastFormSearchAt = now == 0 ? 1 : now;
      if (reason != formSearchSaid) { formSearchSaid = reason; Log.AppendLine("forms: search (" + reason + ")"); }
      ThreadPool.QueueUserWorkItem(_ => {
        try { FindFormNode(); } catch (Exception e) { SayOnce("the forms search failed", e); }
        finally { Interlocked.Exchange(ref formSearchBusy, 0); }
      });
      return true;
    }
    static bool FormSearchRunning { get { return Volatile.Read(ref formSearchBusy) != 0; } }

    static int lastFormsRescan;
    /// <summary>The forms searched again (a full pass, about a second) when at least <paramref name="minGapMs"/> passed since the last
    /// call of this one (its own clock); the known live hero screen is kept if the pass does not find it again.</summary>
    public static bool RescanFormsIfStale(int minGapMs) {
      if (regs == null || Layout.Current == null) return false;
      if (lastFormsRescan != 0 && Environment.TickCount - lastFormsRescan < minGapMs) return false;
      lastFormsRescan = Environment.TickCount; if (lastFormsRescan == 0) lastFormsRescan = 1;
      ulong oldForm = formNode;
      ulong f = FindFormNode();
      if (f == 0 && oldForm != 0 && NodeForm(oldForm) != 0) formNode = oldForm;
      return true;
    }

    /// <summary>The hero screen the game holds now (0 = unknown: the node moved or was never found).</summary>
    internal static ulong LiveForm() { ulong f = NodeForm(formNode); Watch(ref heroOk, f, formNode, 4); return f; }

    /// <summary>The inventory (Form_BackpackIntegration) the game holds now; searched again (a full pass, about a second)
    /// when its node is not known or has moved, at most every <paramref name="rescanMs"/> (&lt; 0: not now). 0 = the
    /// player never opened it.</summary>
    internal static ulong LiveInvForm(int rescanMs) {
      ulong f = NodeInv(invNode);
      Watch(ref invOk, f, invNode, 0);
      if (f != 0 || rescanMs < 0 || FormSearchRunning) return f;
      if (Environment.TickCount - lastNodeScan < rescanMs && lastNodeScan != 0) return 0;
      lastNodeScan = Environment.TickCount;
      if (regs == null) return 0;
      FindFormNode();
      return NodeInv(invNode);
    }
    static int lastNodeScan;

    /// <summary>The foundry's form (<paramref name="gem"/> false) or the gem screen (true) the game holds now; searched
    /// again like the inventory. 0 = never opened.</summary>
    internal static ulong LiveRecastForm(bool gem, int rescanMs) {
      ulong f = NodeTable(gem ? gemNode : subNode, gem ? FormGemId : FormRecastId);
      if (gem) Watch(ref gemOk, f, gemNode, 2); else Watch(ref subOk, f, subNode, 1);
      if (f != 0 || rescanMs < 0 || FormSearchRunning) return f;
      if (Environment.TickCount - lastNodeScan < rescanMs && lastNodeScan != 0) return 0;
      lastNodeScan = Environment.TickCount;
      if (regs == null) return 0;
      FindFormNode();
      return NodeTable(gem ? gemNode : subNode, gem ? FormGemId : FormRecastId);
    }

    /// <summary>The city form (the main screen) the game holds now; 0 = id unknown or its node never found. No rescan of its own
    /// (the scans come from the inventory / foundry / gem paths and the overlay's stale-forms pass).</summary>
    internal static ulong LiveCityForm() { ulong f = NodeTable(cityNode, FormMainCityId); Watch(ref cityOk, f, cityNode, 3); return f; }

    /// <summary>Is the city on screen: true / false from its form's active flag; null = the form id is unknown or its node was never found.</summary>
    public static bool? CityShown() {
      if (FormMainCityId == 0) return null;
      ulong f = LiveCityForm();
      return f == 0 ? (bool?)null : InvFormShown(f);
    }

    /// <summary>Some hero of the account (the storage cleanup opens the sell screen from a hero's gear list), 0 = none.</summary>
    public static long AnyHeroUid() {
      var lt = live; ulong heroes; int t;
      if (lt == null || lt.HeroData == 0 || !Field(lt.HeroData, KHeroes, out heroes, out t) || t != T_TABLE) return 0;
      foreach (var v in TableValues(heroes)) { ulong u; int ut; if (Field(v, "iHeroId", out u, out ut) && ut == T_INT && (long)u > 0) return (long)u; }
      return 0;
    }

    static bool HasTable(ulong t, string key) { ulong v; int tt; return t != 0 && Field(t, key, out v, out tt) && tt == T_TABLE; }

    static LiveTables FindLive(int pid) {
      regs = Regions();
      string[] anchors = { KEquipData, KHero, KHeroes, KCamp, KArts, KBeasts, KAct, KGrid, KPanel, KItems, KPlayer, KZerbus, KGameData };
      var tstr = FindLuaStringsFast(anchors);
      var found = new List<string>(); foreach (var kv in tstr) found.Add(kv.Value + "@" + kv.Key.ToString("X"));
      L("Strings found: " + string.Join(", ", found.ToArray()));
      var owners = OwnersOf(tstr);
      var lt = new LiveTables { Pid = pid };
      ChooseState(owners, lt);   // only the tables of the login the game runs
      List<ulong> l;
      // EquipData: the owner of suitId2EquipIdExt (set in EquipData:ctor) with the item map. m_CurrentSelectHeroUid and
      // m_EquipFilterConfigs are set only once the gear screen was opened (and the first is nil again outside it: the key
      // is gone after a GC), so they are only the fallback
      if (owners.TryGetValue(KEquipData, out l)) {
        int n = 0; foreach (var t in l) if (HasTable(t, "equips")) { if (lt.EquipData == 0) lt.EquipData = t; n++; }
        if (n > 1) L("  EquipData: " + n + " candidates, the first taken");
      }
      if (lt.EquipData == 0 && owners.TryGetValue(KHero, out l)) {
        foreach (var t in l) if (HasTable(t, "equips") && HasTable(t, "m_EquipFilterConfigs")) { lt.EquipData = t; break; }
        if (lt.EquipData == 0) foreach (var t in l) if (HasTable(t, "equips")) { lt.EquipData = t; break; }
      }
      lt.HeroData = Largest(owners, KHeroes);
      lt.CampOwner = Largest(owners, KCamp);
      lt.ArtOwner = Largest(owners, KArts);
      lt.BeastOwner = Largest(owners, KBeasts);
      lt.ActOwner = Largest(owners, KAct);
      // resources: ItemData (the bag) and PlayerData (currencies; also the owner of m_AllBeastInfo)
      lt.ItemData = BestItemData(owners);
      lt.PlayerData = Largest(owners, KPlayer);
      lt.UnionWeekBoss = Largest(owners, KZerbus);
      if (lt.PlayerData == 0 || !HasNum(lt.PlayerData, "m_Coin")) lt.PlayerData = lt.BeastOwner;
      // the hero screen: the one the game's window list holds (a dead copy of an older one can own the keys too)
      lt.Form = FindFormNode();
      if (lt.Form == 0 && owners.TryGetValue(KGrid, out l)) foreach (var t in l) if (HasTable(t, "m_InfinityGridProxy")) { lt.Form = t; break; }
      if (owners.TryGetValue(KPanel, out l)) foreach (var t in l) if (HasTable(t, KList) && HasTable(t, "m_Form")) { lt.Panel = t; break; }
      return lt;
    }

    // the owner of the key whose value (a table) has the most entries
    static ulong Largest(Dictionary<string, List<ulong>> owners, string key) {
      List<ulong> l; if (!owners.TryGetValue(key, out l)) return 0;
      ulong best = 0; int bestN = -1;
      foreach (var t in l) {
        ulong v; int tt; if (!Field(t, key, out v, out tt) || tt != T_TABLE) continue;
        int n = EntryCount(v);
        if (n > bestN) { bestN = n; best = t; }
      }
      return best;
    }

    static int EntryCount(ulong t) {
      var h = Read(t, 56); if (h == null || h[8] != 5) return 0;
      int lsize = h[11]; uint sizearray = BitConverter.ToUInt32(h, 12);
      if (lsize > 20 || sizearray > 1000000) return 0;
      int n = 0;
      if (sizearray > 0) { var ab = Read(BitConverter.ToUInt64(h, 16), (int)sizearray * 16); if (ab != null) for (int i = 0; i < sizearray; i++) if (BitConverter.ToInt32(ab, i * 16 + 8) != T_NIL) n++; }
      int nn = 1 << lsize; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32);
      if (nb != null) for (int i = 0; i < nn; i++) if (BitConverter.ToInt32(nb, i * 32 + 8) != T_NIL && BitConverter.ToInt32(nb, i * 32 + 24) != T_NIL) n++;
      return n;
    }

    // ------------------------------------------------------------------ parallel passes

    /// <summary>Calls <paramref name="f"/> for every 16 MB chunk of the game's writable memory, on all cores. The callback
    /// must be thread-safe.</summary>
    static void ScanParallel(ChunkFn f) {
      const int CH = 16 * 1024 * 1024;
      var chunks = new List<KeyValuePair<ulong, int>>();
      foreach (var r in regs) for (ulong off = 0; off < r.Size; off += CH) chunks.Add(new KeyValuePair<ulong, int>(r.Base + off, (int)Math.Min((ulong)CH, r.Size - off)));
      Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
        () => new byte[CH],
        (c, state, buf) => {
          int rd;
          if (Rpm(c.Key, buf, c.Value, out rd) && rd > 0) f(c.Key, buf, rd);
          return buf;
        },
        buf => { });
    }

    /// <summary>Short script strings with these texts (TString address -> text): aligned positions only.</summary>
    static Dictionary<ulong, string> FindLuaStringsFast(string[] names) {
      var res = new Dictionary<ulong, string>();
      var byLen = new Dictionary<int, List<KeyValuePair<byte[], string>>>();
      foreach (var s in names) {
        var p = Encoding.ASCII.GetBytes(s);
        List<KeyValuePair<byte[], string>> l; if (!byLen.TryGetValue(p.Length, out l)) byLen[p.Length] = l = new List<KeyValuePair<byte[], string>>();
        l.Add(new KeyValuePair<byte[], string>(p, s));
      }
      ScanParallel((b0, buf, len) => {
        for (int i = 24; i + 48 < len; i += 8) {
          if (buf[i - 16] != 4) continue;                      // tt: short string
          List<KeyValuePair<byte[], string>> l;
          if (!byLen.TryGetValue(buf[i - 13], out l)) continue; // length
          foreach (var kv in l) {
            var p = kv.Key; if (buf[i] != p[0] || buf[i + p.Length] != 0) continue;
            bool ok = true; for (int j = 1; j < p.Length; j++) if (buf[i + j] != p[j]) { ok = false; break; }
            if (ok) lock (res) res[b0 + (ulong)i - 24] = kv.Value;
          }
        }
      });
      return res;
    }

    /// <summary>For every text: the tables that have a field with that name (two passes for all of them).</summary>
    static Dictionary<string, List<ulong>> OwnersOf(Dictionary<ulong, string> tstr) { return OwnersOf(tstr, 4096); }

    static Dictionary<string, List<ulong>> OwnersOf(Dictionary<ulong, string> tstr, int window) {
      var anchorTs = new HashSet<ulong>(tstr.Keys);
      var nodes = new List<KeyValuePair<ulong, string>>();
      ScanParallel((b0, buf, len) => {
        for (int i = 16; i + 16 <= len; i += 8) {
          if (buf[i + 8] != T_SSTR || buf[i + 9] != 0) continue;   // node key tt (the int is 0x44)
          ulong v = BitConverter.ToUInt64(buf, i);
          if (!anchorTs.Contains(v) || BitConverter.ToInt32(buf, i + 8) != T_SSTR) continue;
          lock (nodes) nodes.Add(new KeyValuePair<ulong, string>(b0 + (ulong)i - 16, tstr[v]));
        }
      });
      // the node array can start up to 4096 nodes before the key's node (big manager tables have hundreds of fields);
      // one table can own several of the keys (HeroData: the heroes and the faction rewards)
      var starts = new Dictionary<ulong, List<KeyValuePair<ulong, string>>>();
      foreach (var n in nodes) for (int i = 0; i < window; i++) {
        ulong s = n.Key - (ulong)(32 * i);
        List<KeyValuePair<ulong, string>> sl; if (!starts.TryGetValue(s, out sl)) starts[s] = sl = new List<KeyValuePair<ulong, string>>();
        sl.Add(n);
      }
      L("  nodes: " + nodes.Count);
      var res = new Dictionary<string, List<ulong>>();
      ScanParallel((b0, buf, len) => {
        for (int i = 0; i + 56 <= len; i += 8) {
          if (buf[i + 8] != 5) continue;
          ulong np = BitConverter.ToUInt64(buf, i + 24);
          List<KeyValuePair<ulong, string>> hits; if (!starts.TryGetValue(np, out hits)) continue;
          int lsize = buf[i + 11]; if (lsize > 12) continue;
          foreach (var hit in hits) {
            if (hit.Key >= np + (ulong)(32 << lsize)) continue;
            lock (res) { List<ulong> l; if (!res.TryGetValue(hit.Value, out l)) res[hit.Value] = l = new List<ulong>(); if (!l.Contains(b0 + (ulong)i)) l.Add(b0 + (ulong)i); }
          }
        }
      });
      foreach (var kv in res) L("  tables with '" + kv.Key + "': " + kv.Value.Count);
      return res;
    }

    // ------------------------------------------------------------------ values of the sub stats not revealed yet

    // The live item tables have 0 for the sub stats an item has not revealed yet (they open at +4/+8/+12/+16), and so has
    // the server's copy of an item sent after a change; the copies sent at login have the real values, which the site
    // shows dimmed («с заточки +N»). They are collected once per game session: uid -> attribute pool id -> value.
    static Dictionary<long, Dictionary<long, long>> subValues;
    static int subValuesPid;

    static void CollectSubValues(int pid) {
      if (subValues != null && subValuesPid == pid) return;
      var sw = Stopwatch.StartNew();
      var res = new Dictionary<long, Dictionary<long, long>>();
      var tstr = FindLuaStringsFast(new[] { "iStarLvl" });
      var owners = OwnersOf(tstr, 64);
      List<ulong> tables;
      if (owners.TryGetValue("iStarLvl", out tables))
        foreach (var t in tables) {
          ulong v; int tt;
          if (Field(t, "vConfig", out v, out tt)) continue;   // a live table: 0 for hidden values
          var d = ParseTable(t, 3, new HashSet<ulong>());
          long uid = d != null ? LNum(d, "iItemUid") : 0; if (uid <= 0) continue;
          object o; var list = d.TryGetValue("vViceAttrList", out o) ? o as Dictionary<string, object> : null; if (list == null) continue;
          foreach (var kv in list) {
            var a = kv.Value as Dictionary<string, object>; if (a == null) continue;
            long pool = LNum(a, "iAttrId"), val = LNum(a, "iValue"); if (pool <= 0 || val == 0) continue;
            Dictionary<long, long> m; if (!res.TryGetValue(uid, out m)) res[uid] = m = new Dictionary<long, long>();
            m[pool] = val;
          }
        }
      subValues = res; subValuesPid = pid;
      L("Sub stat values of " + res.Count + " items in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
    }

    /// <summary>Diagnostics: every table owning the hero screen's grid key, with the types of its fields.</summary>
    public static string DumpForms() {
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) return null;
      H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return null;
      regs = Regions();
      var owners = OwnersOf(FindLuaStringsFast(new[] { KGrid }));
      var sb = new StringBuilder(); List<ulong> l;
      if (owners.TryGetValue(KGrid, out l))
        foreach (var t in l) {
          sb.Append(t.ToString("X")).Append(':');
          var d = ParseTable(t, 0, new HashSet<ulong>());
          if (d != null) foreach (var kv in d) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value is string ? (string)kv.Value : kv.Value == null ? "nil" : kv.Value.ToString());
          sb.Append('\n');
        }
      sb.Append("live (UIInstance): ").Append(FindFormNode().ToString("X")).Append(" node ").Append(formNode.ToString("X")).Append('\n');
      return sb.ToString();
    }

    // ------------------------------------------------------------------ battle study (diagnostics)

    /// <summary>Every script table holding a server battle hero (MTTDProto.CmdHeroFight: iHeroId, iBaseId, mAttr{attr id ->
    /// value}, ...) as JSON lines - still in memory after a battle until the game's garbage collector takes it. Read-only, for comparing the
    /// server's battle stats with the stats the site computes for the hero panel.</summary>
    /// <summary>Battle study: the battle statistics of heroes still in memory (CSharpBattle.Battle.DamageStatisticsData:
    /// iBaseID 0x10, iHeroID 0x14, iPower 0x18, fDamageAmount 0x28 (all enemies), fDamageAmountToBoss 0x30,
    /// fTreatmentAmount 0x38, fAcceptDamageAmount 0x3C, iStarLevel 0x40, iSublimLevel 0x44), and the battle-end screen's
    /// frame count (Form_BattleEnd.m_FightFramIdx; one logic frame = 270/4096 s). JSON lines, read-only. Null without
    /// layout data (src/Layout.cs).</summary>
    public static string DumpBattleStats() {
      var ly = Layout.Current; if (ly == null) return null;
      int ds = ly.Span("DamageStatisticsData"), gs = ly.Span("GameSimulation"), ko = ly.Hdr("klass");
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) return null;
      H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return null;
      regs = Regions();
      var sb = new StringBuilder(); var seen = new HashSet<string>();
      var hits = new List<ulong>();
      ScanParallel((b0, buf, len) => {
        for (int i = 0; i + ds <= len; i += 8) {
          int bid = BitConverter.ToInt32(buf, i + 0x10);
          if (bid < 1000 || bid > 9999) continue;
          uint uid = BitConverter.ToUInt32(buf, i + 0x14);
          if (uid < (uint)bid * 100000u || uid >= (uint)bid * 100000u + 100) continue;
          int star = BitConverter.ToInt32(buf, i + 0x40), sub = BitConverter.ToInt32(buf, i + 0x44);
          long dmg = BitConverter.ToInt64(buf, i + 0x28);
          if (star < 1 || star > 8 || sub < 0 || sub > 12 || dmg < 0 || dmg > 1000000000000L) continue;
          if (BitConverter.ToUInt64(buf, i + ko) == 0) continue;
          lock (hits) hits.Add(b0 + (ulong)i);
        }
      });
      foreach (var o in hits) {
        var b = Read(o, ds); if (b == null) continue;
        string line = "{\"at\":\"" + o.ToString("X") + "\",\"klass\":\"" + BitConverter.ToUInt64(b, ko).ToString("X") + "\",\"iBaseID\":" + BitConverter.ToInt32(b, 0x10)
          + ",\"iHeroID\":" + BitConverter.ToUInt32(b, 0x14) + ",\"iPower\":" + BitConverter.ToUInt32(b, 0x18)
          + ",\"damage\":" + BitConverter.ToInt64(b, 0x28) + ",\"toBoss\":" + BitConverter.ToInt64(b, 0x30)
          + ",\"heal\":" + BitConverter.ToInt32(b, 0x38) + ",\"taken\":" + BitConverter.ToInt32(b, 0x3C)
          + ",\"star\":" + BitConverter.ToInt32(b, 0x40) + ",\"sublim\":" + BitConverter.ToInt32(b, 0x44)
          + ",\"overflow\":" + BitConverter.ToInt64(b, 0x58) + "}";
        if (seen.Add(line.Substring(line.IndexOf("\"iBaseID\"")))) sb.Append(line).Append('\n');
      }
      // the simulations still in memory: CSharpBattle.Battle.GameSimulation and its kinds (their classes: layout data
      // from the server): <CurrentFrameIdx> 0xC4, m_state 0xA4
      ulong ga = 0;
      try { foreach (ProcessModule m in ps[0].Modules) if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) ga = (ulong)(long)m.BaseAddress; } catch (Exception) { }
      if (ga != 0) {
        ForgetClasses();
        var klass = SimKlasses(ga);
        var sims = new List<ulong>();
        ScanParallel((b0, buf, len) => {
          for (int i = 0; i + gs <= len; i += 8) if (klass.ContainsKey(BitConverter.ToUInt64(buf, i + ko))) lock (sims) sims.Add(b0 + (ulong)i);
        });
        foreach (var o in sims) {
          var b = Read(o, gs); if (b == null) continue;
          uint frames = BitConverter.ToUInt32(b, 0xC4); int state = SimState(ly, BitConverter.ToInt32(b, 0xA4));
          if (state == SimOther || frames == 0 || frames > 200000) continue;   // running / ended; a class pointer elsewhere is no object
          sb.Append("{\"sim\":\"" + klass[BitConverter.ToUInt64(b, ko)] + "\",\"at\":\"" + o.ToString("X") + "\",\"state\":" + BitConverter.ToInt32(b, 0xA4)
            + ",\"frames\":" + frames + ",\"seconds\":" + (frames * 270 / 4096.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "}\n");
        }
      }
      // the battle-end screen: its frame count
      var owners = OwnersOf(FindLuaStringsFast(new[] { "m_FightFramIdx" }), 1024);
      List<ulong> l;
      if (owners.TryGetValue("m_FightFramIdx", out l))
        foreach (var t in l) { ulong v; int tt; if (Field(t, "m_FightFramIdx", out v, out tt) && tt == T_INT) sb.Append("{\"form\":\"" + t.ToString("X") + "\",\"frames\":" + (long)v + ",\"seconds\":" + ((long)v * 270 / 4096.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "}\n"); }
      return sb.ToString();
    }

    public static string DumpHeroFights() {
      var ly = Layout.Current; if (ly == null) return null;
      int hs = ly.Span("CmdHeroFight"), ko = ly.Hdr("klass"), ad = ly.Hdr("arrData"), kv = ly.Size("SdpPair");
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) return null;
      H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return null;
      regs = Regions();
      var tstr = FindLuaStringsFast(new[] { "mAttr" });
      var owners = OwnersOf(tstr, 256);
      var sb = new StringBuilder(); var seen = new HashSet<string>();
      List<ulong> l;
      if (owners.TryGetValue("mAttr", out l))
        foreach (var t in l) {
          var d = ParseTable(t, 2, new HashSet<ulong>());
          if (d == null || !d.ContainsKey("iBaseId")) continue;
          var one = new StringBuilder(); J(one, d);
          if (seen.Add(one.ToString())) sb.Append(one).Append('\n');
        }
      // the battle's own heroes come in a C# message (CmdStartChallengeInfo), not through the scripts: MTTDProto.CmdHeroFight
      // objects - iHeroId 0x10, iBaseId 0x14, iLevel 0x18, iStarLevel 0x30, iSublimLevel 0x34, mAttr 0x48 (XDictionary:
      // VersionedList<KeyValuePair<SdpUInt, SdpUInt>> - _items 0x10, _size 0x18), iPower 0x68
      var hits = new List<ulong>();
      ScanParallel((b0, buf, len) => {
        for (int i = 0; i + hs <= len; i += 8) {
          uint bid = BitConverter.ToUInt32(buf, i + 0x14);
          if (bid < 1000 || bid > 9999) continue;
          uint uid = BitConverter.ToUInt32(buf, i + 0x10);
          if (uid < bid * 100000u || uid >= bid * 100000u + 100) continue;
          uint lv = BitConverter.ToUInt32(buf, i + 0x18), star = BitConverter.ToUInt32(buf, i + 0x30), pw = BitConverter.ToUInt32(buf, i + 0x68);
          if (lv < 1 || lv > 100 || star < 1 || star > 8 || pw == 0 || pw > 10000000) continue;
          if (BitConverter.ToUInt64(buf, i + ko) == 0 || BitConverter.ToUInt64(buf, i + 0x48) == 0) continue;
          lock (hits) hits.Add(b0 + (ulong)i);
        }
      });
      foreach (var o in hits) {
        var b = Read(o, hs); if (b == null) continue;
        var lst = Read(BitConverter.ToUInt64(b, 0x48), 0x20); if (lst == null) continue;
        int n = BitConverter.ToInt32(lst, 0x18); if (n <= 0 || n > 200) continue;
        var arr = Read(BitConverter.ToUInt64(lst, 0x10) + (ulong)ad, n * kv); if (arr == null) continue;
        var one = new StringBuilder();
        one.Append("{\"src\":\"cs\",\"iHeroId\":").Append(BitConverter.ToUInt32(b, 0x10)).Append(",\"iBaseId\":").Append(BitConverter.ToUInt32(b, 0x14))
           .Append(",\"iLevel\":").Append(BitConverter.ToUInt32(b, 0x18)).Append(",\"iStarLevel\":").Append(BitConverter.ToUInt32(b, 0x30))
           .Append(",\"iSublimLevel\":").Append(BitConverter.ToUInt32(b, 0x34)).Append(",\"iSquadId\":").Append(BitConverter.ToUInt32(b, 0x5C))
           .Append(",\"iLordPosition\":").Append(BitConverter.ToUInt32(b, 0x60)).Append(",\"iAwakeningFlag\":").Append(BitConverter.ToUInt32(b, 0x64))
           .Append(",\"iPower\":").Append(BitConverter.ToUInt32(b, 0x68)).Append(",\"mAttr\":{");
        for (int k = 0; k < n; k++) {
          if (k > 0) one.Append(',');
          one.Append("\"[").Append(BitConverter.ToUInt32(arr, k * kv)).Append("]\":").Append(BitConverter.ToInt32(arr, k * kv + 4));
        }
        one.Append("}}");
        if (seen.Add(one.ToString())) sb.Append(one).Append('\n');
      }
      L("  C# hero fights: " + hits.Count);
      return sb.ToString();
    }

    // ------------------------------------------------------------------ the account from the live tables

    /// <summary>account.json from the live tables; null when they are not all there (the caller then does the full scan).</summary>
    static string AccountFromLive(LiveTables lt, Stopwatch sw, long puid) {
      ulong equips, heroes; int t1, t2;
      if (!Field(lt.EquipData, "equips", out equips, out t1) || t1 != T_TABLE) return null;
      if (!Field(lt.HeroData, KHeroes, out heroes, out t2) || t2 != T_TABLE) return null;
      var sb = new StringBuilder(); sb.Append("{\n\"equipment\":[\n");
      try { CollectSubValues(lt.Pid); } catch (Exception e) { L("Sub stat values: " + e.Message); }
      int ne = 0; var seen = new HashSet<long>();
      string[] cfg = { "vConfig", "vItemConfig", "vSuitConfig" };
      foreach (var v in TableValues(equips)) {
        var skip = new HashSet<ulong>();
        foreach (var k in cfg) { ulong fv; int ft; if (Field(v, k, out fv, out ft) && ft == T_TABLE) skip.Add(fv); }
        var d = ParseTable(v, 3, skip);
        var raw = ServerForm(d);
        if (raw == null || !seen.Add((long)raw["iItemUid"])) continue;
        if (ne++ > 0) sb.Append(",\n"); J(sb, raw);
      }
      L("  equipment items (owned): " + ne);
      sb.Append("\n],\n\"heroes\":[\n");
      int nh = 0, nShort = 0; var seenH = new HashSet<long>();
      foreach (var v in TableValues(heroes)) {
        var d = ParseTable(v, 2, new HashSet<ulong>());
        object u; if (d == null || !d.TryGetValue("iHeroId", out u) || !(u is long) || (long)u <= 0 || !d.ContainsKey("iBaseId")) continue;
        if (!seenH.Add((long)u)) continue;
        // a hero never levelled or equipped is kept by the game in a short form (only its ids, the rest are the game's
        // defaults): sent as it is, the site fills level 1, the stars of the hero's quality and its power
        if (!d.ContainsKey("vEquipSlot")) nShort++;
        if (nh++ > 0) sb.Append(",\n"); J(sb, d);
      }
      L("  heroes (owned): " + nh + (nShort > 0 ? " (" + nShort + " in the short form)" : ""));
      Dictionary<string, object> camp = null, arts = null;
      ulong cv, av; int ct, at;
      if (lt.CampOwner != 0 && Field(lt.CampOwner, KCamp, out cv, out ct) && ct == T_TABLE) camp = ParseTable(cv, 1, new HashSet<ulong>());
      if (lt.ArtOwner != 0 && Field(lt.ArtOwner, KArts, out av, out at) && at == T_TABLE) arts = ParseTable(av, 5, new HashSet<ulong>());
      L("  reward events: " + (camp == null ? 0 : camp.Count) + ", artifacts: " + (arts == null ? 0 : arts.Count));
      sb.Append("\n],\n\"campRewards\":"); J(sb, camp);
      sb.Append(",\n\"artifacts\":"); J(sb, arts);
      // Deity Beasts: account-wide hero attributes (PlayerData.m_AllBeastInfo: beast id -> vAttrList {iAttrId, iLevel})
      Dictionary<string, object> beasts = null; ulong bv; int bt;
      if (lt.BeastOwner != 0 && Field(lt.BeastOwner, KBeasts, out bv, out bt) && bt == T_TABLE) beasts = ParseTable(bv, 3, new HashSet<ulong>());
      sb.Append(",\n\"beasts\":"); J(sb, beasts);
      // Summoning: PlayerData counters (pulls left to the guarantee by softId, per pool)
      // and ActivityData.m_ActData54.pool = the pools open now, with softId, the rate text (mDetailCfg) and times
      var summon = new Dictionary<string, object>();
      if (lt.BeastOwner != 0) foreach (var k in SummonPlayerKeys) {
        ulong sv; int st;
        if (!Field(lt.BeastOwner, k, out sv, out st)) continue;
        if (st == T_TABLE) summon[k] = ParseTable(sv, k == "m_LotteryRecord" ? 4 : 3, new HashSet<ulong>());
        else if (st == T_INT) summon[k] = (long)sv;
      }
      ulong a54, apool; int a54t, apt;
      if (lt.ActOwner != 0 && Field(lt.ActOwner, KAct, out a54, out a54t) && a54t == T_TABLE && Field(a54, "pool", out apool, out apt) && apt == T_TABLE)
        summon["pools"] = ParseTable(apool, 4, new HashSet<ulong>());
      L("  summon: " + summon.Count + " parts" + (summon.ContainsKey("pools") ? "" : " (no pools)"));
      sb.Append(",\n\"summon\":"); J(sb, summon);
      // resources: item id -> count, from the bag and the player's currencies
      Dictionary<string, object> resMeta;
      var resMap = ReadResources(lt, out resMeta);
      L("  resources: " + (resMap == null ? "not found" : resMap.Count + " ids"));
      sb.Append(",\n\"resources\":"); J(sb, resMap);
      sb.Append(",\n\"resourcesMeta\":"); J(sb, resMeta);
      // Zerbus: each team's saved gear (stage -> [{uiHeroId, vEquipUid, ulArtifactUid, ...}]) and power (UnionWeekBossData
      // SetSavedTeamEquipsBoss3 / _Handler_Union_SaveEquipPlan_SC); present once the game got the guild's boss data
      if (lt.UnionWeekBoss != 0) {
        ulong zv, zp; int zt, zpt;
        var zer = new Dictionary<string, object>();
        if (Field(lt.UnionWeekBoss, KZerbus, out zv, out zt) && zt == T_TABLE) zer["teams"] = ParseTable(zv, 4, new HashSet<ulong>());
        if (Field(lt.UnionWeekBoss, "m_SavedTeamEquipPlanPower", out zp, out zpt) && zpt == T_TABLE) zer["power"] = ParseTable(zp, 1, new HashSet<ulong>());
        L("  zerbus saved gear: " + (zer.ContainsKey("teams") ? "read" : "none"));
        sb.Append(",\n\"zerbusSaved\":"); J(sb, zer);
      }
      // boss fights captured at their result screens (src/BattleCapture.cs): the site compares them with its simulation
      sb.Append(",\n\"battles\":").Append(BattlesJson());
      sb.Append("\n,\"meta\":{\"extractor\":\"" + ExtractorVersion + "\"");
      if (GameVersion != null) { sb.Append(",\"gameVersion\":"); J(sb, GameVersion); }
      // the game account (PlayerData m_Uid / m_Name): one site account may get two game accounts
      // (puid: read once by the caller, from the same script state as the heroes and items above)
      if (lt.PlayerData != 0) {
        ulong pv; int pt; long lvl;
        if (puid > 0) { sb.Append(",\"player\":" + puid); SyncClient.Player = puid; }
        if (Field(lt.PlayerData, "m_Name", out pv, out pt) && (pt == T_SSTR || pt == T_LSTR)) { sb.Append(",\"playerName\":"); J(sb, ReadLuaString(pv)); }
        if (Field(lt.PlayerData, "m_Level", out pv, out pt) && AsLong(pv, pt, out lvl)) sb.Append(",\"playerLevel\":" + lvl);
      }
      sb.Append(",\"seconds\":" + (int)sw.Elapsed.TotalSeconds + ",\"equipment\":" + ne + ",\"heroes\":" + nh + ",\"live\":true}\n}\n");
      L("Done in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
      return sb.ToString();
    }

    /// <summary>An item of EquipData.equips (the game's reworked form, EquipData:_CreateEquip) back in the server's form,
    /// the one the site reads: iLevel for iIntensifyLvl, the attribute pool ids (iId) as iAttrId.</summary>
    internal static Dictionary<string, object> ServerForm(Dictionary<string, object> d) {
      if (d == null) return null;
      object u; if (!d.TryGetValue("iItemUid", out u) || !(u is long) || (long)u <= 0) return null;
      var r = new Dictionary<string, object>();
      r["iItemUid"] = u;
      foreach (var k in new[] { "iItemId", "iStarLvl", "iHeroId", "bLocked", "iExtraMasterAttrValue", "iIntensifyExp", "iVaryEffectId", "iExclusiveEffectId" }) {
        object v; if (d.TryGetValue(k, out v) && v != null) r[k] = v;
      }
      object lv; r["iLevel"] = d.TryGetValue("iIntensifyLvl", out lv) ? lv : 0L;
      if (!r.ContainsKey("iHeroId")) r["iHeroId"] = 0L;
      r["vMasterAttrList"] = PoolAttrs(d, "vMasterAttrList");
      r["vViceAttrList"] = PoolAttrs(d, "vViceAttrList");
      // hidden sub stats: the value the server sent at login
      Dictionary<long, long> known;
      var sv = subValues;
      if (sv != null && sv.TryGetValue((long)u, out known))
        foreach (var e in ((Dictionary<string, object>)r["vViceAttrList"]).Values) {
          var a = e as Dictionary<string, object>; long pool = LNum(a, "iAttrId"), kvv;
          if (pool > 0 && LNum(a, "iValue") == 0 && known.TryGetValue(pool, out kvv)) a["iValue"] = kvv;
        }
      object mc; if (d.TryGetValue("vViceAttrMilepostCnt", out mc) && mc is Dictionary<string, object>) r["vViceAttrMilepostCnt"] = mc;
      return r;
    }

    static Dictionary<string, object> PoolAttrs(Dictionary<string, object> d, string key) {
      var r = new Dictionary<string, object>();
      object o; var list = d.TryGetValue(key, out o) ? o as Dictionary<string, object> : null;
      if (list == null) return r;
      foreach (var kv in list) {
        var a = kv.Value as Dictionary<string, object>; if (a == null) continue;
        object id, val;
        if (!a.TryGetValue("iId", out id)) continue;
        var e = new Dictionary<string, object>(); e["iAttrId"] = id; if (a.TryGetValue("iValue", out val)) e["iValue"] = val;
        r[kv.Key] = e;
      }
      return r;
    }

    // ------------------------------------------------------------------ resources (bag + currencies)

    // ItemData:ctor sets m_HeroBaseId2ItemId and PlayerData:_RoleInit m_mNegSpecialItem - both keys exist in no other
    // script table of the game. The bag is ItemData.m_Items[itemType][itemId] = {m_Config, m_Count}
    // (updated in place by Push_SetItem), time-limited items ItemData.m_TimeItems[itemId][uid] = {m_Count, m_EndTime, ..}.
    const string KItems = "m_HeroBaseId2ItemId", KPlayer = "m_mNegSpecialItem";
    // UnionWeekBossData (the guild's weekly bosses): the third (Zerbus, two teams) fights with the gear saved with each team
    const string KZerbus = "m_SavedTeamEquipsInfo";

    // PlayerData scalar fields -> item id (CurrencyType; ItemData:GetItemNumById)
    static readonly KeyValuePair<string, long>[] PlayerCurrencies = {
      new KeyValuePair<string, long>("m_Coin", 1),                    // Gold
      new KeyValuePair<string, long>("m_Exp", 2),                     // commander EXP
      new KeyValuePair<string, long>("m_Energy", 3),                  // stamina
      new KeyValuePair<string, long>("m_Diamond", 4),                 // diamonds (can be < 0: a debt)
      new KeyValuePair<string, long>("m_ExpItem", 5),                 // Hero EXP Potion (hero level-ups)
      new KeyValuePair<string, long>("m_TowerClimbTalentPoint", 16),  // TalentPoint
      new KeyValuePair<string, long>("m_FriendHeartNum", 39),         // FriendHeart
    };

    /// <summary>The ItemData instance: of the owners of m_HeroBaseId2ItemId the one whose m_Items holds the most items
    /// (after a relogin an old instance can linger until the GC takes it).</summary>
    static ulong BestItemData(Dictionary<string, List<ulong>> owners) {
      List<ulong> l; if (!owners.TryGetValue(KItems, out l)) return 0;
      ulong best = 0; int bestN = -1;
      foreach (var t in l) {
        ulong m; int mt; if (!Field(t, "m_Items", out m, out mt) || mt != T_TABLE) continue;
        int n = 0; foreach (var e in IntEntries(m)) if (e.Tt == T_TABLE) n += EntryCount(e.Val);
        if (n > bestN) { bestN = n; best = t; }
      }
      if (l.Count > 1) L("  ItemData: " + l.Count + " candidates, " + bestN + " items in the chosen one");
      return best;
    }

    static bool HasNum(ulong t, string key) { ulong v; int tt; return t != 0 && Field(t, key, out v, out tt) && (tt == T_INT || tt == T_FLT); }

    struct IntEntry { public long Key; public ulong Val; public int Tt; }

    /// <summary>The integer-keyed entries of a script table: the array part (key = index + 1) and the hash nodes with an
    /// integer key.</summary>
    static List<IntEntry> IntEntries(ulong t) {
      var r = new List<IntEntry>();
      var h = Read(t, 56); if (h == null || h[8] != 5) return r;
      int lsize = h[11]; uint sizearray = BitConverter.ToUInt32(h, 12);
      if (lsize > 20 || sizearray > 1000000) return r;
      if (sizearray > 0) {
        var ab = Read(BitConverter.ToUInt64(h, 16), (int)sizearray * 16);
        if (ab != null) for (int i = 0; i < sizearray; i++) {
          int tt = BitConverter.ToInt32(ab, i * 16 + 8); if (tt == T_NIL) continue;
          r.Add(new IntEntry { Key = i + 1, Val = BitConverter.ToUInt64(ab, i * 16), Tt = tt });
        }
      }
      int nn = 1 << lsize; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32);
      if (nb != null) for (int i = 0; i < nn; i++) {
        int o = i * 32; int tt = BitConverter.ToInt32(nb, o + 8);
        if (tt == T_NIL || BitConverter.ToInt32(nb, o + 24) != T_INT) continue;
        r.Add(new IntEntry { Key = BitConverter.ToInt64(nb, o + 16), Val = BitConverter.ToUInt64(nb, o), Tt = tt });
      }
      return r;
    }

    /// <summary>A script number as long: integer, float or a numeric string (m_mNegSpecialItem holds int64 values the game
    /// reads with tonumber()).</summary>
    static bool AsLong(ulong v, int tt, out long n) {
      n = 0;
      if (tt == T_INT) { n = (long)v; return true; }
      if (tt == T_FLT) { double d = BitConverter.Int64BitsToDouble((long)v); if (double.IsNaN(d) || double.IsInfinity(d)) return false; n = (long)Math.Round(d); return true; }
      if (tt == T_SSTR || tt == T_LSTR) {
        var s = ReadLuaString(v);
        return s != null && long.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n);
      }
      return false;
    }

    /// <summary>The player's resources: item id -> count. The bag (ItemData.m_Items, plus m_TimeItems summed), then the
    /// currencies of PlayerData, which win as in ItemData:GetItemNumById (scalars, m_SpecialItem, m_mNegSpecialItem).
    /// Null when neither table is known; <paramref name="meta"/> says what was found.</summary>
    internal static Dictionary<string, object> ReadResources(LiveTables lt, out Dictionary<string, object> meta) {
      meta = new Dictionary<string, object>();
      var counts = new SortedDictionary<long, long>();
      bool bag = false, player = false; int types = 0;

      ulong m; int mt;
      if (lt.ItemData != 0 && Field(lt.ItemData, "m_Items", out m, out mt) && mt == T_TABLE) {
        bag = true;
        foreach (var byType in IntEntries(m)) {                       // itemType -> {itemId -> item}
          if (byType.Tt != T_TABLE) continue;
          types++;
          foreach (var it in IntEntries(byType.Val)) {                // itemId -> {m_Config, m_Count}
            if (it.Tt != T_TABLE || it.Key <= 0) continue;
            ulong c; int ct; long n;
            if (!Field(it.Val, "m_Count", out c, out ct) || !AsLong(c, ct, out n) || n == 0) continue;
            long old; counts.TryGetValue(it.Key, out old); counts[it.Key] = old + n;
          }
        }
        ulong ti; int tit;
        if (Field(lt.ItemData, "m_TimeItems", out ti, out tit) && tit == T_TABLE)
          foreach (var byId in IntEntries(ti)) {                      // itemId -> {uid -> {m_Count, m_EndTime}}
            if (byId.Tt != T_TABLE || byId.Key <= 0) continue;
            foreach (var one in TableValues(byId.Val)) {
              ulong c; int ct; long n;
              if (!Field(one, "m_Count", out c, out ct) || !AsLong(c, ct, out n) || n == 0) continue;
              long old; counts.TryGetValue(byId.Key, out old); counts[byId.Key] = old + n;
            }
          }
      }

      if (lt.PlayerData != 0) {
        foreach (var kv in PlayerCurrencies) {
          ulong v; int tt; long n;
          if (Field(lt.PlayerData, kv.Key, out v, out tt) && AsLong(v, tt, out n)) { counts[kv.Value] = n; player = true; }
        }
        // m_SpecialItem: tokens, coins, Mythril 18, awakening tokens 25/26...; m_mNegSpecialItem: the summoning crystals
        // (8/21/22 free, 29/30/31 paid, 37), can be negative - it wins, as in PlayerData:GetNegSpecialItem
        foreach (var key in new[] { "m_SpecialItem", "m_mNegSpecialItem" }) {
          ulong sm; int st;
          if (!Field(lt.PlayerData, key, out sm, out st) || st != T_TABLE) continue;
          player = true;
          foreach (var e in IntEntries(sm)) { long n; if (e.Key > 0 && AsLong(e.Val, e.Tt, out n)) counts[e.Key] = n; }
        }
      }

      meta["bag"] = bag; meta["player"] = player; meta["types"] = (long)types;
      if (!bag && !player) return null;
      var res = new Dictionary<string, object>();
      foreach (var kv in counts) res[kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = kv.Value;
      return res;
    }
  }
}
