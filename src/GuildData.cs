// Wardsage — the player's guild table (members, their attacks and damage on the guild bosses) read from the game's memory
// (READ-ONLY: only ReadProcessMemory) for the site's «Гильдия» page. Mirrors src/ArenaOpponents.cs.
//
// Where the client keeps it (all resident from the login, no screen is needed):
//   UnionData singleton — m_vMembers = [CmdUnionMemberData {stPlayerIdType {iZoneId, iUid}, iPostId (1 master, 2 vice, 3 elite,
//     4 member), iJoinTime, iTotalHistoryActive, iSevenActive, stRoleSimpleInf {sName, iLevel, iPower, iLogoutTime (0 = online)},
//     mWeekBossData {boss id → {iDamageNum, iPrevDamageNum, iFightNum, iLastFightTime}},
//     mBossData {boss id → {iDamageNum, iPrevDamageNum, iUseItemNum, iLastFightTime}}}],
//     m_UnionInfo {iUnionId, iLevel, iMemberNum (the member count the server sent with the guild), stBaseAttr {sUnionName}}.
//     Found by the key m_vApplyUnionIdRecord.
//   UnionWeekBossData — m_mWeekBossInfo[boss id] = {iRefreshTime (end of the current period, epoch s), …}; key m_SavedStageIdsBoss3.
//   UnionBossData (the classic boss) — m_mBossInfo[boss id] = {iRefreshTime, …}; key m_mBossInfo.
//   Two-Heads boss («Павший завет») — the members' mTwoHeadsBossData[boss id] = {ulDamageNum, ulPrevDamageNum, uiFightNum, vFightData = [{ulDamageNum,
//     vFightData = [hero]}]}; the guild boss itself in m_BossInfoById[id] = hp, m_BossRefreshTimeById[id] = period end; key m_BossRefreshTimeById.
//   Weekly activity ranking (only when the dividends screen has loaded it) — m_ActiveValueRankList / …LastWeek = [{stRole {iUid, iZoneId}, iActive}].
// The tables are found together (two passes over the memory, a few seconds), then read directly. Memory can hold more than one
// table with these keys (an older copy that is no longer used), so the guild table is the one whose member list holds the player,
// the largest such one whose list is not short of its iMemberNum; it is looked for again when the player is no longer in it, when
// the list is short, and every 30 minutes.
// Damage values may be integers, floats or numeric strings (the script wraps them in tonumber): all are accepted.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RealmForge {
  public static partial class RFX {
    const string KUnion = "m_vApplyUnionIdRecord", KWeekBoss = "m_SavedStageIdsBoss3", KClassicBoss = "m_mBossInfo",
                 KTwoHeads = "m_BossRefreshTimeById", KDividend = "m_ActiveValueRankListLastWeek";
    static ulong guildTab, guildWeekTab, guildClassicTab, guildTwoTab, guildDivTab; static int guildPid, guildLastFind, guildFoundAt;
    const int GuildRepickMs = 30 * 60 * 1000;

    // Key lookups of the guild (and guild war) code compare the key text itself instead of the shared key-pointer cache of Field:
    // a key's string object is per script state, and memory can still hold tables of an earlier state (an earlier login) whose
    // keys are other objects with the same text. With a pointer taken from such a table, Field missed the live guild table and only
    // the old copies matched (a partial or another account's member list).
    static bool FieldX(ulong t, string key, out ulong val, out int tt) {
      val = 0; tt = 0;
      var h = Read(t, 56); if (h == null || h[8] != 5) return false;
      int lsize = h[11]; if (lsize > 20) return false;
      int nn = 1 << lsize; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return false;
      var kb = Encoding.UTF8.GetBytes(key);
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_SSTR) continue;
        var sb = Read(BitConverter.ToUInt64(nb, o + 16), 24 + kb.Length);
        if (sb == null || sb[8] != 4 || sb[11] != kb.Length) continue;
        bool eq = true; for (int j = 0; j < kb.Length; j++) if (sb[24 + j] != kb[j]) { eq = false; break; }
        if (!eq) continue;
        val = BitConverter.ToUInt64(nb, o); tt = BitConverter.ToInt32(nb, o + 8); return true;
      }
      return false;
    }
    static bool HasTableX(ulong t, string key) { ulong v; int tt; return t != 0 && FieldX(t, key, out v, out tt) && tt == T_TABLE; }

    /// <summary>The number of member records in a guild table's m_vMembers (-1: none) and whether one of them is the player.</summary>
    static int GuildMembers(ulong t, long selfUid, out bool hasSelf) {
      hasSelf = false;
      ulong m; int mt;
      if (t == 0 || !FieldX(t, "m_vMembers", out m, out mt) || mt != T_TABLE) return -1;
      int n = 0;
      foreach (var e in IntEntries(m)) {
        if (e.Tt != T_TABLE) continue;
        n++;
        if (hasSelf || selfUid <= 0) continue;
        ulong id, u; int it, ut; long uid;
        if (FieldX(e.Val, "stPlayerIdType", out id, out it) && it == T_TABLE && FieldX(id, "iUid", out u, out ut) && AsLong(u, ut, out uid) && uid == selfUid)
          hasSelf = true;
      }
      return n;
    }

    /// <summary>The member count a guild table states in m_UnionInfo.iMemberNum; 0 = not known.</summary>
    static long GuildStated(ulong t) {
      ulong ui, v; int ut, vt; long n;
      return t != 0 && FieldX(t, "m_UnionInfo", out ui, out ut) && ut == T_TABLE && FieldX(ui, "iMemberNum", out v, out vt) && AsLong(v, vt, out n) && n > 0 ? n : 0;
    }

    /// <summary>One candidate guild table as the search sees it.</summary>
    class GuildCand { public ulong T; public bool UnionInfo; public int N = -1; public bool Self, Full; public long Stated; }

    static GuildCand ProbeGuild(ulong t, long selfUid) {
      var c = new GuildCand { T = t, UnionInfo = HasTableX(t, "m_UnionInfo") };
      if (!c.UnionInfo) return c;
      c.N = GuildMembers(t, selfUid, out c.Self);
      c.Stated = GuildStated(t);
      c.Full = !GuildPayload.ListShort(c.N, c.Stated);
      return c;
    }

    // usable: has m_UnionInfo and a member list. The one that lists the player wins over any other, then one whose list is not
    // short; among equals the larger list
    static bool GuildUsable(GuildCand c) { return c.UnionInfo && c.N >= 0; }
    static bool GuildBetter(GuildCand c, GuildCand best) {
      if (!GuildUsable(c)) return false;
      if (best == null) return true;
      if (c.Self != best.Self) return c.Self;
      if (c.Full != best.Full) return c.Full;
      return c.N > best.N;
    }

    /// <summary>What the last guild search found (for the log line when no table is chosen).</summary>
    public static string GuildFindNote = "not searched yet";

    /// <summary>Forget the chosen guild table: it is looked for again at the next read (not sooner than the rescan delay).</summary>
    static void ForgetGuildTable() { guildTab = 0; }

    /// <summary>The guild tables found (not read yet): searched when not known, at most every <paramref name="rescanMs"/>.
    /// <paramref name="selfUid"/> (0 = not known): the guild table must list the player. Call off the interface thread.</summary>
    static bool GuildTables(int rescanMs, long selfUid) {
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) { guildTab = guildWeekTab = guildClassicTab = guildTwoTab = guildDivTab = 0; return false; }
      if (ps[0].Id != guildPid) { guildTab = guildWeekTab = guildClassicTab = guildTwoTab = guildDivTab = 0; guildPid = ps[0].Id; guildLastFind = 0; H = OpenProcess(0x0410, false, ps[0].Id); }
      if (H == IntPtr.Zero) H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return false;
      bool self; int cn;
      if (guildTab != 0 && HasTableX(guildTab, "m_UnionInfo") && (cn = GuildMembers(guildTab, selfUid, out self)) >= 0 && (self || selfUid <= 0)
          && !GuildPayload.ListShort(cn, GuildStated(guildTab)) && Environment.TickCount - guildFoundAt < GuildRepickMs) return true;
      guildTab = guildWeekTab = guildClassicTab = guildTwoTab = guildDivTab = 0;
      if (guildLastFind != 0 && Environment.TickCount - guildLastFind < rescanMs) {
        GuildFindNote = "searched " + ((Environment.TickCount - guildLastFind) / 1000) + " s ago, next search in " + ((rescanMs - (Environment.TickCount - guildLastFind)) / 1000) + " s";
        return false;
      }
      guildLastFind = Environment.TickCount;
      regs = Regions();
      var owners = OwnersOf(FindLuaStringsFast(new[] { KUnion, KWeekBoss, KClassicBoss, KTwoHeads, KDividend }), 1024);
      List<ulong> l; int bestN = -1, cands = 0, usable = 0, withSelf = 0; GuildCand best = null;
      if (owners.TryGetValue(KUnion, out l))
        foreach (var t in l) {
          cands++;
          var c = ProbeGuild(t, selfUid);
          if (GuildUsable(c)) { usable++; if (c.Self) withSelf++; }
          if (GuildBetter(c, best)) best = c;
        }
      if (best != null) guildTab = best.T;
      GuildFindNote = "candidates " + cands + ", usable " + usable + ", with the player " + withSelf
                      + (best != null ? ", chosen " + best.N + " members" : "");
      guildFoundAt = Environment.TickCount;
      bestN = -1;
      if (owners.TryGetValue(KWeekBoss, out l))
        foreach (var t in l) {
          ulong m; int mt;
          if (!FieldX(t, "m_mWeekBossInfo", out m, out mt) || mt != T_TABLE) continue;
          int n = EntryCount(m);
          if (n > bestN) { bestN = n; guildWeekTab = t; }
        }
      bestN = -1;
      if (owners.TryGetValue(KClassicBoss, out l))
        foreach (var t in l) {
          ulong m; int mt;
          if (!FieldX(t, KClassicBoss, out m, out mt) || mt != T_TABLE) continue;
          int n = EntryCount(m);
          if (n > bestN) { bestN = n; guildClassicTab = t; }
        }
      bestN = -1;
      if (owners.TryGetValue(KTwoHeads, out l))
        foreach (var t in l) {
          if (!HasTableX(t, "m_BossInfoById") || !HasTableX(t, KTwoHeads)) continue;
          int n = EntryCount(t);
          if (n > bestN) { bestN = n; guildTwoTab = t; }
        }
      bestN = -1;
      if (owners.TryGetValue(KDividend, out l))
        foreach (var t in l) {
          if (!HasTableX(t, "m_ActiveValueRankList")) continue;
          int n = EntryCount(t);
          if (n > bestN) { bestN = n; guildDivTab = t; }
        }
      return guildTab != 0;
    }

    static List<Dictionary<string, object>> RankList(ulong t, string key) {
      ulong v; int tt;
      if (t == 0 || !FieldX(t, key, out v, out tt) || tt != T_TABLE) return null;
      var res = new List<Dictionary<string, object>>();
      foreach (var e in IntEntries(v)) {
        if (e.Tt != T_TABLE || res.Count >= GuildPayload.MaxMembers) continue;
        var d = ParseTable(e.Val, 3, new HashSet<ulong>());
        if (d != null) res.Add(d);
      }
      return res;
    }

    static Dictionary<string, object> SubTable(ulong t, string key, int depth) {
      ulong v; int tt;
      if (t == 0 || !FieldX(t, key, out v, out tt) || tt != T_TABLE) return null;
      return ParseTable(v, depth, new HashSet<ulong>());
    }

    /// <summary>The guild's tables, parsed (ParseTable-shaped) for GuildPayload.Build; false when not found yet.</summary>
    public static bool ReadGuild(int rescanMs, long selfUid, out Dictionary<string, object> union, out List<Dictionary<string, object>> members,
                                 out Dictionary<string, object> week, out Dictionary<string, object> classic, out GuildExtra extra) {
      union = null; members = null; week = null; classic = null; extra = new GuildExtra();
      if (!GuildTables(rescanMs, selfUid)) return false;
      // the members first: nothing read after them (the extras, the guild war) can change them
      members = new List<Dictionary<string, object>>();
      ulong v; int tt;
      if (FieldX(guildTab, "m_vMembers", out v, out tt) && tt == T_TABLE)
        foreach (var e in IntEntries(v)) {
          if (e.Tt != T_TABLE || members.Count >= GuildPayload.MaxMembers) continue;
          var md = ParseTable(e.Val, 8, new HashSet<ulong>());
          if (md != null) members.Add(md);
        }
      union = SubTable(guildTab, "m_UnionInfo", 3);
      week = SubTable(guildWeekTab, "m_mWeekBossInfo", 2);
      classic = SubTable(guildClassicTab, KClassicBoss, 2);
      extra.TwoHp = SubTable(guildTwoTab, "m_BossInfoById", 2);
      extra.TwoRefresh = SubTable(guildTwoTab, KTwoHeads, 2);
      extra.RankThis = RankList(guildDivTab, "m_ActiveValueRankList");
      extra.RankLast = RankList(guildDivTab, "m_ActiveValueRankListLastWeek");
      // the guild war is optional: any failure there leaves it out and nothing else
      try { extra.Gvg = ReadGvg(rescanMs); } catch (Exception) { extra.Gvg = null; }
      return union != null;
    }

    /// <summary>The guild payload for the site (null: no guild / not found / not complete). <paramref name="selfUid"/> 0 = not known.
    /// <paramref name="skip"/> when members were read but the list is not whole: "noSelf" (the player is not in it) or "short"
    /// (fewer than the guild's stated count). The memory is then searched once more right away; if the list is still not whole
    /// nothing is sent and the table is looked for again at the next read.</summary>
    public static string ReadGuildJson(int rescanMs, long selfUid, long atMs, out bool refreshSoon, out int memberCount, out string skip) {
      refreshSoon = false; memberCount = 0; skip = null;
      Dictionary<string, object> union, week, classic; List<Dictionary<string, object>> members; GuildExtra extra;
      for (int attempt = 0; ; attempt++) {
        int foundBefore = guildFoundAt; bool searchedBefore = guildLastFind != 0;
        if (!ReadGuild(attempt == 0 ? rescanMs : 0, selfUid, out union, out members, out week, out classic, out extra)) return null;
        bool fresh = !searchedBefore || guildFoundAt != foundBefore;   // the memory was searched in this read
        skip = GuildPayload.Incomplete(union, members, selfUid);
        if (skip == null) break;
        memberCount = members.Count;
        ForgetGuildTable();
        if (fresh || attempt > 0) return null;
      }
      return GuildPayload.Build(union, members, week, classic, selfUid, atMs, out refreshSoon, out memberCount, extra);
    }

    public static string DumpGuild(string path) { return DumpGuild(path, null); }

    static string Hex(ulong a) { return "\"0x" + a.ToString("x") + "\""; }
    static string JBool(bool b) { return b ? "true" : "false"; }
    static long NumAt(ulong t, string key) { ulong v; int tt; long n; return t != 0 && FieldX(t, key, out v, out tt) && AsLong(v, tt, out n) ? n : 0; }
    static ulong TabAt(ulong t, string key) { ulong v; int tt; return t != 0 && FieldX(t, key, out v, out tt) && tt == T_TABLE ? v : 0; }

    /// <summary>Diagnostics (app --dump-guild file; read-only, nothing is sent): the player id, every candidate guild table the
    /// search finds and which one is chosen, and the payload that would be built. Never throws; returns a short result line.</summary>
    public static string DumpGuild(string path, string appVersion) {
      var sb = new StringBuilder("{\"app\":" + MiniJson.Quote(appVersion ?? SyncClient.Version) + ",\"at\":" + MiniJson.Quote(DateTime.UtcNow.ToString("o")));
      string result = "ok";
      Action<string, Action> part = (name, body) => {
        int mark = sb.Length;
        try { body(); }
        catch (Exception e) { sb.Length = mark; sb.Append(",\"" + name + "Error\":" + MiniJson.Quote(e.GetType().Name + ": " + e.Message)); result = "error in " + name; }
      };
      long self = 0;
      part("process", () => {
        var ps = GameInfo.GameProcesses();
        sb.Append(",\"gameRunning\":" + JBool(ps.Length > 0));
        if (ps.Length == 0) return;
        H = OpenProcess(0x0410, false, ps[0].Id);
        sb.Append(",\"pid\":" + ps[0].Id + ",\"opened\":" + JBool(H != IntPtr.Zero));
        if (H != IntPtr.Zero) regs = Regions();
      });
      if (H == IntPtr.Zero || regs == null) { sb.Append('}'); try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); } catch (Exception) { } return "game not open"; }
      // the player id: the account sync takes PlayerData's m_Uid (the table owning m_mNegSpecialItem with the largest such field)
      part("player", () => {
        sb.Append(",\"player\":{\"syncClientPlayer\":" + SyncClient.Player + ",\"source\":\"PlayerData.m_Uid (owner of m_mNegSpecialItem, largest; this process has not synced)\",\"candidates\":[");
        var owners = OwnersOf(FindLuaStringsFast(new[] { "m_mNegSpecialItem" }), 4096);
        List<ulong> l; int i = 0; ulong largest = 0; int bestN = -1;
        if (owners.TryGetValue("m_mNegSpecialItem", out l))
          foreach (var t in l) {
            ulong v, pv; int tt, pt; long uidCached = 0, uidText = NumAt(t, "m_Uid");
            if (Field(t, "m_Uid", out pv, out pt)) AsLong(pv, pt, out uidCached);
            int n = Field(t, "m_mNegSpecialItem", out v, out tt) && tt == T_TABLE ? EntryCount(v) : -1;
            if (n > bestN) { bestN = n; largest = t; }
            if (i++ > 0) sb.Append(',');
            sb.Append("{\"t\":" + Hex(t) + ",\"uid\":" + uidText + ",\"uidByFieldCache\":" + uidCached + ",\"negSpecialItems\":" + n + ",\"hasCoin\":" + JBool(HasNum(t, "m_Coin")) + "}");
          }
        self = NumAt(largest, "m_Uid");
        sb.Append("],\"chosen\":" + (largest != 0 ? Hex(largest) : "null") + ",\"selfUid\":" + self + "}");
      });
      // every table owning the key m_vApplyUnionIdRecord
      part("guild", () => {
        var tstr = FindLuaStringsFast(new[] { KUnion });
        sb.Append(",\"guild\":{\"keyStrings\":" + tstr.Count + ",\"candidates\":[");
        var owners = OwnersOf(tstr, 1024);
        List<ulong> l; int i = 0; GuildCand best = null;
        string[] keys = { "m_UnionInfo", "m_vMembers", "m_SelfUnionInfo", "m_MasterInf", "m_vApplyUnionIdRecord", "m_vApplyList" };
        if (owners.TryGetValue(KUnion, out l))
          foreach (var t in l) {
            if (i++ > 0) sb.Append(',');
            int mark = sb.Length;
            try {
              var c = ProbeGuild(t, self);
              if (GuildBetter(c, best)) best = c;
              sb.Append("{\"t\":" + Hex(t) + ",\"keys\":[");
              int k = 0;
              foreach (var key in keys) { ulong v; int tt; if (FieldX(t, key, out v, out tt)) { if (k++ > 0) sb.Append(','); sb.Append(MiniJson.Quote(key)); } }
              sb.Append("],\"keysByFieldCache\":[");
              k = 0;
              foreach (var key in keys) { ulong v; int tt; if (Field(t, key, out v, out tt)) { if (k++ > 0) sb.Append(','); sb.Append(MiniJson.Quote(key)); } }
              ulong ui = TabAt(t, "m_UnionInfo"), su = TabAt(t, "m_SelfUnionInfo"), ba = TabAt(ui, "stBaseAttr");
              ulong nm; int nt; string name = ba != 0 && FieldX(ba, "sUnionName", out nm, out nt) && (nt == T_SSTR || nt == T_LSTR) ? ReadLuaString(nm) : null;
              sb.Append("],\"unionId\":" + NumAt(ui, "iUnionId") + ",\"name\":" + MiniJson.Quote(name ?? "") + ",\"memberNum\":" + NumAt(ui, "iMemberNum")
                        + ",\"selfUnionId\":" + NumAt(su, "iUnionId") + ",\"members\":" + c.N + ",\"hasSelf\":" + JBool(c.Self) + ",\"short\":" + JBool(c.UnionInfo && !c.Full) + ",\"firstUids\":[");
              ulong mv = TabAt(t, "m_vMembers"); int u = 0;
              if (mv != 0) foreach (var e in IntEntries(mv)) {
                if (e.Tt != T_TABLE || u >= 3) continue;
                if (u++ > 0) sb.Append(',');
                sb.Append(NumAt(TabAt(e.Val, "stPlayerIdType"), "iUid"));
              }
              sb.Append("]}");
            } catch (Exception e) { sb.Length = mark; sb.Append("{\"t\":" + Hex(t) + ",\"error\":" + MiniJson.Quote(e.Message) + "}"); }
          }
        sb.Append("],\"wouldChoose\":" + (best != null ? Hex(best.T) : "null") + ",\"why\":" + MiniJson.Quote(best == null ? "no usable candidate (needs m_UnionInfo and m_vMembers)"
                  : (best.Self ? "lists the player" : "no candidate lists the player") + ", " + (best.Full ? "not short" : "short of memberNum") + ", " + best.N + " members") + "}");
      });
      // the real read as the app does it (search, choice, payload)
      part("read", () => {
        guildTab = 0; guildLastFind = 0;
        bool soon; int n; string skip;
        string json = ReadGuildJson(0, self, (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds, out soon, out n, out skip);
        sb.Append(",\"read\":{\"chosen\":" + (guildTab != 0 ? Hex(guildTab) : "null") + ",\"note\":" + MiniJson.Quote(GuildFindNote) + ",\"skip\":" + (skip != null ? MiniJson.Quote(skip) : "null")
                  + ",\"members\":" + n + ",\"payloadBytes\":" + (json != null ? json.Length : 0) + "}");
        if (result == "ok") result = json != null ? "members " + n : skip != null ? "not whole (" + skip + "), members " + n : "no guild data found";
      });
      sb.Append('}');
      try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); } catch (Exception e) { return "error writing " + e.Message; }
      return result;
    }
  }

  /// <summary>The pure part (tested without the game): script tables as ParseTable gives them → the site's JSON.</summary>
  /// <summary>Optional extra tables for the payload (any may be null).</summary>
  public class GuildExtra {
    public Dictionary<string, object> TwoHp, TwoRefresh;            // Two-Heads boss: {"[id]" -> hp}, {"[id]" -> period end}
    public List<Dictionary<string, object>> RankThis, RankLast;     // weekly activity ranking [{stRole, iActive}]; null = not loaded
    public GvgInfo Gvg;                                             // the guild war (src/GvgData.cs); null = none
  }

  public static class GuildPayload {
    public const int Version = 1, MaxMembers = 120;

    static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
    static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { return Get(d, k) as Dictionary<string, object>; }

    /// <summary>A script number: integer, float or numeric string (also "1.5e9"); false when it is none.</summary>
    public static bool Try(object v, out long n) {
      n = 0;
      if (v is long) { n = (long)v; return true; }
      if (v is double) { double x = (double)v; if (double.IsNaN(x) || double.IsInfinity(x)) return false; n = (long)Math.Round(x); return true; }
      if (v is bool) { n = (bool)v ? 1 : 0; return true; }
      var s = v as string; if (s == null) return false;
      s = s.Trim();
      if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return true;
      double dv;
      if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out dv) && !double.IsNaN(dv) && !double.IsInfinity(dv)) { n = (long)Math.Round(dv); return true; }
      return false;
    }
    static long N(Dictionary<string, object> d, string k) { long n; return Try(Get(d, k), out n) ? n : 0; }
    static bool Has(Dictionary<string, object> d, string k) { long n; return Try(Get(d, k), out n); }
    static string L(long n) { return n.ToString(CultureInfo.InvariantCulture); }
    static string OrNull(Dictionary<string, object> d, string k, bool positiveOnly) {
      long n; if (!Try(Get(d, k), out n) || (positiveOnly && n <= 0)) return "null";
      return L(n);
    }

    /// <summary>The integer keys "[n]" of a parsed script table in order, with their values.</summary>
    static SortedDictionary<long, object> IntKeyed(object t) {
      var res = new SortedDictionary<long, object>();
      var d = t as Dictionary<string, object>; if (d == null) return res;
      foreach (var kv in d) {
        long i;
        if (kv.Key.Length > 2 && kv.Key[0] == '[' && kv.Key[kv.Key.Length - 1] == ']'
            && long.TryParse(kv.Key.Substring(1, kv.Key.Length - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) res[i] = kv.Value;
      }
      return res;
    }

    static void Bosses(StringBuilder sb, Dictionary<string, object> bosses, long nowSec, ref bool soon) {
      sb.Append('['); bool first = true;
      foreach (var kv in IntKeyed(bosses)) {
        var b = kv.Value as Dictionary<string, object>; if (b == null) continue;
        if (!first) sb.Append(','); first = false;
        sb.Append("{\"id\":").Append(L(kv.Key)).Append(",\"refresh\":").Append(OrNull(b, "iRefreshTime", true));
        if (Has(b, "iCurBossHp")) sb.Append(",\"hp\":").Append(L(N(b, "iCurBossHp")));
        if (Has(b, "iPrevBossHp")) sb.Append(",\"prevHp\":").Append(L(N(b, "iPrevBossHp")));
        if (Get(b, "bKill") is bool) sb.Append(",\"kill\":").Append((bool)Get(b, "bKill") ? "true" : "false");
        sb.Append('}');
        long r; if (Try(Get(b, "iRefreshTime"), out r) && r > nowSec && r - nowSec <= 3600) soon = true;
      }
      sb.Append(']');
    }

    // the heroes of one attack: [{id, lv, star, dmg}] from a vector of {iHeroId, iLevel, iStarLevel, iDamageNum}; null when empty
    static string Heroes(object vec, out long total) {
      total = 0; var sb = new StringBuilder("["); int c = 0;
      foreach (var kv in IntKeyed(vec)) {
        var h = kv.Value as Dictionary<string, object>; if (h == null || N(h, "iHeroId") <= 0) continue;
        if (c++ > 0) sb.Append(',');
        total += N(h, "iDamageNum");
        sb.Append("{\"id\":").Append(L(N(h, "iHeroId"))).Append(",\"lv\":").Append(L(N(h, "iLevel"))).Append(",\"star\":").Append(L(N(h, "iStarLevel")))
          .Append(",\"dmg\":").Append(L(N(h, "iDamageNum"))).Append('}');
      }
      return c == 0 ? null : sb.Append(']').ToString();
    }

    // The most recent attacks of one boss record. Week: vvFightData = [{iDamageNum, vFightHero}], boss 3 also mBoss3FightData = {stage → same};
    // classic: vFightData = the heroes of the last attack (a flat vector). Appends ,"fightsDetail":[…] (last <cap>) when there are any.
    static void Fights(StringBuilder sb, Dictionary<string, object> b, string fightsKey, string stageKey, int cap) {
      var list = new List<string>();
      Action<object, long> add = (f, stage) => {
        var fd = f as Dictionary<string, object>; if (fd == null || !fd.ContainsKey("vFightHero")) return;
        long total; string heroes = Heroes(Get(fd, "vFightHero"), out total);
        if (heroes == null) return;
        long dmg = Has(fd, "iDamageNum") ? N(fd, "iDamageNum") : total;
        list.Add("{" + (stage > 0 ? "\"stage\":" + L(stage) + "," : "") + "\"dmg\":" + L(dmg) + ",\"heroes\":" + heroes + "}");
      };
      var vec = Get(b, fightsKey);
      var flat = IntKeyed(vec);
      bool nested = false;
      foreach (var kv in flat) { var d = kv.Value as Dictionary<string, object>; if (d != null && d.ContainsKey("vFightHero")) { nested = true; break; } }
      if (nested) { foreach (var kv in flat) add(kv.Value, 0); }
      else if (flat.Count > 0) {
        long total; string heroes = Heroes(vec, out total);
        if (heroes != null) list.Add("{\"dmg\":" + L(total) + ",\"heroes\":" + heroes + "}");
      }
      if (stageKey != null) foreach (var kv in IntKeyed(Get(b, stageKey))) add(kv.Value, kv.Key);
      if (list.Count == 0 || cap <= 0) return;
      int from = Math.Max(0, list.Count - cap);
      sb.Append(",\"fightsDetail\":[");
      for (int i = from; i < list.Count; i++) { if (i > from) sb.Append(','); sb.Append(list[i]); }
      sb.Append(']');
    }

    // the member's daily activity summed over the last 7 days (vSevenActive = [{iTime, iActive}]); -1 = no such list
    static long ActiveWeek(Dictionary<string, object> m, long nowSec) {
      var v = Get(m, "vSevenActive"); if (!(v is Dictionary<string, object>)) return -1;
      long sum = 0;
      foreach (var kv in IntKeyed(v)) {
        var e = kv.Value as Dictionary<string, object>; if (e == null) continue;
        long t; if (Try(Get(e, "iTime"), out t) && nowSec - t < 604800 && nowSec - t > -86400) sum += N(e, "iActive");
      }
      return sum;
    }

    // The Two-Heads boss squads of one member record: vFightData = [{ulDamageNum, vFightData = [hero]}]; the last <cap> as fightsDetail
    static void TwoFights(StringBuilder sb, Dictionary<string, object> b, int cap) {
      var list = new List<string>();
      foreach (var kv in IntKeyed(Get(b, "vFightData"))) {
        var sq = kv.Value as Dictionary<string, object>; if (sq == null) continue;
        long total; string heroes = Heroes(Get(sq, "vFightData"), out total);
        if (heroes == null) continue;
        list.Add("{\"dmg\":" + L(Has(sq, "ulDamageNum") ? N(sq, "ulDamageNum") : total) + ",\"heroes\":" + heroes + "}");
      }
      if (list.Count == 0 || cap <= 0) return;
      int from = Math.Max(0, list.Count - cap);
      sb.Append(",\"fightsDetail\":[");
      for (int i = from; i < list.Count; i++) { if (i > from) sb.Append(','); sb.Append(list[i]); }
      sb.Append(']');
    }

    // a member's Two-Heads map {boss id -> {fights, dmg, prevDmg, fightsDetail}}; nothing appended when there is none
    static void TwoHeads(StringBuilder sb, object map, int cap) {
      var any = false; var tmp = new StringBuilder("{");
      foreach (var kv in IntKeyed(map)) {
        var b = kv.Value as Dictionary<string, object>; if (b == null) continue;
        if (any) tmp.Append(','); any = true;
        tmp.Append('"').Append(L(kv.Key)).Append("\":{\"fights\":").Append(L(N(b, "uiFightNum"))).Append(",\"dmg\":").Append(L(N(b, "ulDamageNum")))
          .Append(",\"prevDmg\":").Append(L(N(b, "ulPrevDamageNum")));
        TwoFights(tmp, b, cap);
        tmp.Append('}');
      }
      if (any) sb.Append(",\"twoHeads\":").Append(tmp).Append('}');
    }

    // [{t, a}] of the member's daily activity (all entries, at most 14)
    static void ActiveDays(StringBuilder sb, Dictionary<string, object> m) {
      var v = Get(m, "vSevenActive"); if (!(v is Dictionary<string, object>)) return;
      var tmp = new StringBuilder(); int c = 0;
      foreach (var kv in IntKeyed(v)) {
        var e = kv.Value as Dictionary<string, object>; if (e == null || c >= 14) continue;
        if (c++ > 0) tmp.Append(',');
        tmp.Append("{\"t\":").Append(L(N(e, "iTime"))).Append(",\"a\":").Append(L(N(e, "iActive"))).Append('}');
      }
      if (c > 0) sb.Append(",\"activeDays\":[").Append(tmp).Append(']');
    }

    /// <summary>True when the player (<paramref name="selfUid"/> &gt; 0) is not one of the members read: the list is not the whole
    /// guild (or not the player's guild), so it is not sent. False when the player is not known or there are no members at all.</summary>
    public static bool SelfMissing(IList<Dictionary<string, object>> members, long selfUid) {
      if (selfUid <= 0 || members == null || members.Count == 0) return false;
      foreach (var m in members) { var id = Obj(m, "stPlayerIdType"); if (id != null && N(id, "iUid") == selfUid) return false; }
      return true;
    }

    /// <summary>How many members a list may lack against the guild's stated count before it counts as short: the stated count is
    /// sent with the guild once and is not lowered when someone leaves later in the session.</summary>
    public const int ShortSlack = 2;

    /// <summary>True when <paramref name="read"/> members are fewer than the stated count (0 = not known) by more than ShortSlack.</summary>
    public static bool ListShort(long read, long stated) { return stated > 0 && read + ShortSlack < stated; }

    /// <summary>Why a read member list is not sent (null: it is whole): "noSelf" (the player is not in it), "short" (fewer members
    /// than m_UnionInfo.iMemberNum says). No members at all is not this case (no guild).</summary>
    public static string Incomplete(Dictionary<string, object> union, IList<Dictionary<string, object>> members, long selfUid) {
      if (members == null || members.Count == 0) return null;
      if (SelfMissing(members, selfUid)) return "noSelf";
      if (ListShort(members.Count, N(union, "iMemberNum"))) return "short";
      return null;
    }

    static void Rank(StringBuilder sb, List<Dictionary<string, object>> list) {
      sb.Append('['); int c = 0;
      foreach (var e in list) {
        var r = Obj(e, "stRole"); if (r == null || N(r, "iUid") <= 0 || c >= MaxMembers) continue;
        if (c++ > 0) sb.Append(',');
        sb.Append("{\"uid\":").Append(L(N(r, "iUid"))).Append(",\"zone\":").Append(L(N(r, "iZoneId"))).Append(",\"a\":").Append(L(N(e, "iActive"))).Append('}');
      }
      sb.Append(']');
    }

    // one member's damage map {boss id → {fights, dmg, prevDmg, last}}; fightKey = iFightNum (week) / iUseItemNum (classic)
    static void Damage(StringBuilder sb, object map, string fightKey, string fightsKey, string stageKey, int cap) {
      sb.Append('{'); bool first = true;
      foreach (var kv in IntKeyed(map)) {
        var b = kv.Value as Dictionary<string, object>; if (b == null) continue;
        if (!first) sb.Append(','); first = false;
        sb.Append('"').Append(L(kv.Key)).Append("\":{\"fights\":").Append(L(N(b, fightKey))).Append(",\"dmg\":").Append(L(N(b, "iDamageNum")))
          .Append(",\"prevDmg\":").Append(L(N(b, "iPrevDamageNum"))).Append(",\"last\":").Append(OrNull(b, "iLastFightTime", true));
        Fights(sb, b, fightsKey, stageKey, cap);
        sb.Append('}');
      }
      sb.Append('}');
    }

    /// <summary>The site's payload; null when there is no guild (no id, or no members).</summary>
    public const int MaxBytes = 200 * 1024;
    public static string Build(Dictionary<string, object> union, IList<Dictionary<string, object>> members, Dictionary<string, object> week,
                               Dictionary<string, object> classic, long selfUid, long atMs, out bool refreshSoon, out int memberCount, GuildExtra extra = null) {
      string r = BuildCap(union, members, week, classic, selfUid, atMs, 6, extra, out refreshSoon, out memberCount);
      if (r != null && Encoding.UTF8.GetByteCount(r) > MaxBytes) r = BuildCap(union, members, week, classic, selfUid, atMs, 3, extra, out refreshSoon, out memberCount);
      if (r != null && Encoding.UTF8.GetByteCount(r) > MaxBytes) r = BuildCap(union, members, week, classic, selfUid, atMs, 0, extra, out refreshSoon, out memberCount);
      return r;
    }

    static string BuildCap(Dictionary<string, object> union, IList<Dictionary<string, object>> members, Dictionary<string, object> week,
                           Dictionary<string, object> classic, long selfUid, long atMs, int cap, GuildExtra extra, out bool refreshSoon, out int memberCount) {
      refreshSoon = false; memberCount = 0;
      long unionId = N(union, "iUnionId");
      if (unionId <= 0 || members == null || members.Count == 0 || Incomplete(union, members, selfUid) != null) return null;
      // the player's zone: that of the own member row; else the commonest zone among the members
      long zone = 0; var zones = new Dictionary<long, int>(); int bestZ = 0;
      foreach (var m in members) {
        var id = Obj(m, "stPlayerIdType"); if (id == null) continue;
        long z = N(id, "iZoneId");
        if (selfUid > 0 && N(id, "iUid") == selfUid) { zone = z; bestZ = int.MaxValue; break; }
        int c; zones.TryGetValue(z, out c); zones[z] = ++c;
        if (c > bestZ) { bestZ = c; zone = z; }
      }
      long nowSec = atMs / 1000;
      var sb = new StringBuilder("{\"v\":" + Version + ",\"at\":" + L(atMs) + ",\"zone\":" + L(zone) + ",\"selfUid\":" + L(selfUid));
      var attr = Obj(union, "stBaseAttr");
      string name = Get(attr, "sUnionName") as string ?? "";
      if (name.Length > 40) name = name.Substring(0, 40);
      sb.Append(",\"union\":{\"id\":").Append(L(unionId)).Append(",\"name\":").Append(MiniJson.Quote(name)).Append(",\"level\":").Append(OrNull(union, "iLevel", false));
      if (N(union, "iMemberNum") > 0) sb.Append(",\"memberNum\":").Append(L(N(union, "iMemberNum")));
      if (Has(union, "iWeekActive")) sb.Append(",\"weekActive\":").Append(L(N(union, "iWeekActive")));
      if (Has(union, "iSevenTotalActive")) sb.Append(",\"sevenActive\":").Append(L(N(union, "iSevenTotalActive")));
      if (Has(union, "iCurDayTotalActive")) sb.Append(",\"dayActive\":").Append(L(N(union, "iCurDayTotalActive")));
      if (N(attr, "iBannerId") > 0) sb.Append(",\"bannerId\":").Append(L(N(attr, "iBannerId")));
      if (N(attr, "iBannerBgId") > 0) sb.Append(",\"bannerBgId\":").Append(L(N(attr, "iBannerBgId")));
      sb.Append('}');
      sb.Append(",\"bosses\":{\"week\":"); Bosses(sb, week, nowSec, ref refreshSoon);
      sb.Append(",\"classic\":"); Bosses(sb, classic, nowSec, ref refreshSoon);
      if (extra != null && (extra.TwoHp != null || extra.TwoRefresh != null)) {
        var hps = IntKeyed(extra.TwoHp); var rfs = IntKeyed(extra.TwoRefresh);
        var ids = new SortedDictionary<long, bool>();
        foreach (var k in hps.Keys) ids[k] = true;
        foreach (var k in rfs.Keys) ids[k] = true;
        if (ids.Count > 0) {
          sb.Append(",\"twoHeads\":["); bool f = true;
          foreach (var id in ids.Keys) {
            if (!f) sb.Append(','); f = false;
            object hp, rf; long r;
            hps.TryGetValue(id, out hp); rfs.TryGetValue(id, out rf);
            bool hasRf = Try(rf, out r) && r > 0; long rfv = hasRf ? r : 0;
            sb.Append("{\"id\":").Append(L(id)).Append(",\"refresh\":").Append(hasRf ? L(rfv) : "null");
            if (Try(hp, out r)) sb.Append(",\"hp\":").Append(L(r));
            sb.Append('}');
            if (hasRf && rfv > nowSec && rfv - nowSec <= 3600) refreshSoon = true;
          }
          sb.Append(']');
        }
      }
      sb.Append("},\"members\":[");
      int n = 0;
      foreach (var m in members) {
        var id = Obj(m, "stPlayerIdType"); if (id == null || N(id, "iUid") <= 0 || n >= MaxMembers) continue;
        var role = Obj(m, "stRoleSimpleInf");
        string nm = Get(role, "sName") as string ?? "";
        if (nm.Length > 40) nm = nm.Substring(0, 40);
        if (n++ > 0) sb.Append(',');
        sb.Append("{\"uid\":").Append(L(N(id, "iUid"))).Append(",\"zone\":").Append(L(N(id, "iZoneId"))).Append(",\"name\":").Append(MiniJson.Quote(nm))
          .Append(",\"level\":").Append(OrNull(role, "iLevel", false)).Append(",\"power\":").Append(OrNull(role, "iPower", false))
          .Append(",\"post\":").Append(L(N(m, "iPostId"))).Append(",\"join\":").Append(OrNull(m, "iJoinTime", true))
          .Append(",\"logout\":").Append(OrNull(role, "iLogoutTime", false)).Append(",\"active7\":").Append(OrNull(m, "iSevenActive", false));
        long aw = ActiveWeek(m, nowSec); if (aw >= 0) sb.Append(",\"activeWeek\":").Append(L(aw));
        ActiveDays(sb, m);
        if (Has(m, "iTotalHistoryActive")) sb.Append(",\"activeTotal\":").Append(L(N(m, "iTotalHistoryActive")));
        TwoHeads(sb, Get(m, "mTwoHeadsBossData"), cap);
        sb.Append(",\"week\":"); Damage(sb, Get(m, "mWeekBossData"), "iFightNum", "vvFightData", "mBoss3FightData", cap);
        sb.Append(",\"classic\":"); Damage(sb, Get(m, "mBossData"), "iUseItemNum", "vFightData", null, cap);
        sb.Append('}');
      }
      sb.Append(']');
      if (extra != null && ((extra.RankThis != null && extra.RankThis.Count > 0) || (extra.RankLast != null && extra.RankLast.Count > 0))) {
        sb.Append(",\"activeRank\":{");
        bool f = true;
        if (extra.RankThis != null && extra.RankThis.Count > 0) { sb.Append("\"thisWeek\":"); Rank(sb, extra.RankThis); f = false; }
        if (extra.RankLast != null && extra.RankLast.Count > 0) { if (!f) sb.Append(','); sb.Append("\"lastWeek\":"); Rank(sb, extra.RankLast); }
        sb.Append('}');
      }
      string gvg = extra != null ? GvgPayload.Build(extra.Gvg) : null;
      if (gvg != null) sb.Append(",\"gvg\":").Append(gvg);
      sb.Append('}');
      memberCount = n;
      return n == 0 ? null : sb.ToString();
    }

    /// <summary>A hash of the payload without its "at" (what changed on the guild).</summary>
    public static string Hash(string json) {
      if (json == null) return null;
      int a = json.IndexOf(",\"at\":", StringComparison.Ordinal), z = json.IndexOf(",\"zone\":", StringComparison.Ordinal);
      string core = a >= 0 && z > a ? json.Substring(0, a) + json.Substring(z) : json;
      using (var sha = SHA1.Create()) {
        var h = sha.ComputeHash(Encoding.UTF8.GetBytes(core));
        var sb = new StringBuilder(); foreach (var b in h) sb.Append(b.ToString("x2")); return sb.ToString();
      }
    }

    /// <summary>The send gate: a changed payload, or any boss period ending within the next 60 minutes (so the counts right
    /// before the weekly reset reach the site).</summary>
    public static bool ShouldSend(string hash, string lastHash, bool refreshSoon) { return hash != lastHash || refreshSoon; }

    /// <summary>The cadence: right after a sync, else at most every <paramref name="everyMin"/> minutes.</summary>
    public static bool Due(bool afterSync, DateTime now, DateTime lastTry, int everyMin) { return afterSync || now - lastTry >= TimeSpan.FromMinutes(everyMin); }
  }

  public static class GuildClient {
    public static PlansStatus Send(string site, string code, string json, out string details) {
      int http; string text, err;
      details = null;
      if (!PlansClient.Send("POST", site + "/api/extractor/guild", code, json, out http, out text, out err)) { details = err; return PlansStatus.Unreachable; }
      return ArenaClient.Interpret(http, text, out details);
    }
  }
}
