// Wardsage — the player's guild table (members, their attacks and damage on the guild bosses) read from the game's memory
// (READ-ONLY: only ReadProcessMemory) for the site's «Гильдия» page. Mirrors src/ArenaOpponents.cs.
//
// Where the client keeps it (all resident from the login, no screen is needed):
//   UnionData singleton — m_vMembers = [CmdUnionMemberData {stPlayerIdType {iZoneId, iUid}, iPostId (1 master, 2 vice, 3 elite,
//     4 member), iJoinTime, iTotalHistoryActive, iSevenActive, stRoleSimpleInf {sName, iLevel, iPower, iLogoutTime (0 = online)},
//     mWeekBossData {boss id → {iDamageNum, iPrevDamageNum, iFightNum, iLastFightTime}},
//     mBossData {boss id → {iDamageNum, iPrevDamageNum, iUseItemNum, iLastFightTime}}}],
//     m_UnionInfo {iUnionId, iLevel, stBaseAttr {sUnionName}}. Found by the key m_vApplyUnionIdRecord.
//   UnionWeekBossData — m_mWeekBossInfo[boss id] = {iRefreshTime (end of the current period, epoch s), …}; key m_SavedStageIdsBoss3.
//   UnionBossData (the classic boss) — m_mBossInfo[boss id] = {iRefreshTime, …}; key m_mBossInfo.
//   Two-Heads boss («Павший завет») — the members' mTwoHeadsBossData[boss id] = {ulDamageNum, ulPrevDamageNum, uiFightNum, vFightData = [{ulDamageNum,
//     vFightData = [hero]}]}; the guild boss itself in m_BossInfoById[id] = hp, m_BossRefreshTimeById[id] = period end; key m_BossRefreshTimeById.
//   Weekly activity ranking (only when the dividends screen has loaded it) — m_ActiveValueRankList / …LastWeek = [{stRole {iUid, iZoneId}, iActive}].
// The tables are found together once per game session (two passes over the memory, a few seconds), then read directly.
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
    static ulong guildTab, guildWeekTab, guildClassicTab, guildTwoTab, guildDivTab; static int guildPid, guildLastFind;

    /// <summary>The guild tables found (not read yet): searched when not known, at most every <paramref name="rescanMs"/>.
    /// Call off the interface thread.</summary>
    static bool GuildTables(int rescanMs) {
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) { guildTab = guildWeekTab = guildClassicTab = guildTwoTab = guildDivTab = 0; return false; }
      if (ps[0].Id != guildPid) { guildTab = guildWeekTab = guildClassicTab = guildTwoTab = guildDivTab = 0; guildPid = ps[0].Id; guildLastFind = 0; H = OpenProcess(0x0410, false, ps[0].Id); }
      if (H == IntPtr.Zero) H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return false;
      if (guildTab != 0 && HasTable(guildTab, "m_vMembers") && HasTable(guildTab, "m_UnionInfo")) return true;
      guildTab = guildWeekTab = guildClassicTab = guildTwoTab = guildDivTab = 0;
      if (guildLastFind != 0 && Environment.TickCount - guildLastFind < rescanMs) return false;
      guildLastFind = Environment.TickCount;
      regs = Regions();
      var owners = OwnersOf(FindLuaStringsFast(new[] { KUnion, KWeekBoss, KClassicBoss, KTwoHeads, KDividend }), 1024);
      List<ulong> l; int bestN = -1;
      if (owners.TryGetValue(KUnion, out l))
        foreach (var t in l) {
          ulong m; int mt;
          if (!HasTable(t, "m_UnionInfo") || !Field(t, "m_vMembers", out m, out mt) || mt != T_TABLE) continue;
          int n = EntryCount(m);
          if (n > bestN) { bestN = n; guildTab = t; }
        }
      bestN = -1;
      if (owners.TryGetValue(KWeekBoss, out l))
        foreach (var t in l) {
          ulong m; int mt;
          if (!Field(t, "m_mWeekBossInfo", out m, out mt) || mt != T_TABLE) continue;
          int n = EntryCount(m);
          if (n > bestN) { bestN = n; guildWeekTab = t; }
        }
      bestN = -1;
      if (owners.TryGetValue(KClassicBoss, out l))
        foreach (var t in l) {
          ulong m; int mt;
          if (!Field(t, KClassicBoss, out m, out mt) || mt != T_TABLE) continue;
          int n = EntryCount(m);
          if (n > bestN) { bestN = n; guildClassicTab = t; }
        }
      bestN = -1;
      if (owners.TryGetValue(KTwoHeads, out l))
        foreach (var t in l) {
          if (!HasTable(t, "m_BossInfoById") || !HasTable(t, KTwoHeads)) continue;
          int n = EntryCount(t);
          if (n > bestN) { bestN = n; guildTwoTab = t; }
        }
      bestN = -1;
      if (owners.TryGetValue(KDividend, out l))
        foreach (var t in l) {
          if (!HasTable(t, "m_ActiveValueRankList")) continue;
          int n = EntryCount(t);
          if (n > bestN) { bestN = n; guildDivTab = t; }
        }
      return guildTab != 0;
    }

    static List<Dictionary<string, object>> RankList(ulong t, string key) {
      ulong v; int tt;
      if (t == 0 || !Field(t, key, out v, out tt) || tt != T_TABLE) return null;
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
      if (t == 0 || !Field(t, key, out v, out tt) || tt != T_TABLE) return null;
      return ParseTable(v, depth, new HashSet<ulong>());
    }

    /// <summary>The guild's tables, parsed (ParseTable-shaped) for GuildPayload.Build; false when not found yet.</summary>
    public static bool ReadGuild(int rescanMs, out Dictionary<string, object> union, out List<Dictionary<string, object>> members,
                                 out Dictionary<string, object> week, out Dictionary<string, object> classic, out GuildExtra extra) {
      union = null; members = null; week = null; classic = null; extra = new GuildExtra();
      if (!GuildTables(rescanMs)) return false;
      union = SubTable(guildTab, "m_UnionInfo", 3);
      week = SubTable(guildWeekTab, "m_mWeekBossInfo", 2);
      classic = SubTable(guildClassicTab, KClassicBoss, 2);
      extra.TwoHp = SubTable(guildTwoTab, "m_BossInfoById", 2);
      extra.TwoRefresh = SubTable(guildTwoTab, KTwoHeads, 2);
      extra.RankThis = RankList(guildDivTab, "m_ActiveValueRankList");
      extra.RankLast = RankList(guildDivTab, "m_ActiveValueRankListLastWeek");
      members = new List<Dictionary<string, object>>();
      ulong v; int tt;
      if (Field(guildTab, "m_vMembers", out v, out tt) && tt == T_TABLE)
        foreach (var e in IntEntries(v)) {
          if (e.Tt != T_TABLE || members.Count >= GuildPayload.MaxMembers) continue;
          var md = ParseTable(e.Val, 8, new HashSet<ulong>());
          if (md != null) members.Add(md);
        }
      return union != null;
    }

    /// <summary>The guild payload for the site (null: no guild / not found). <paramref name="selfUid"/> 0 = not known.</summary>
    public static string ReadGuildJson(int rescanMs, long selfUid, long atMs, out bool refreshSoon, out int memberCount) {
      refreshSoon = false; memberCount = 0;
      Dictionary<string, object> union, week, classic; List<Dictionary<string, object>> members; GuildExtra extra;
      if (!ReadGuild(rescanMs, out union, out members, out week, out classic, out extra)) return null;
      return GuildPayload.Build(union, members, week, classic, selfUid, atMs, out refreshSoon, out memberCount, extra);
    }

    /// <summary>Diagnostics (app --dump-guild file): the payload to a file, nothing is sent. Returns a short result line.</summary>
    public static string DumpGuild(string path) {
      try {
        bool soon; int n;
        string json = ReadGuildJson(0, 0, (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds, out soon, out n);
        File.WriteAllText(path, json ?? "null", new UTF8Encoding(false));
        return json == null ? "no guild data found" : "members " + n;
      } catch (Exception e) { try { File.WriteAllText(path, "error: " + e); } catch (Exception) { } return "error " + e.Message; }
    }
  }

  /// <summary>The pure part (tested without the game): script tables as ParseTable gives them → the site's JSON.</summary>
  /// <summary>Optional extra tables for the payload (any may be null).</summary>
  public class GuildExtra {
    public Dictionary<string, object> TwoHp, TwoRefresh;            // Two-Heads boss: {"[id]" -> hp}, {"[id]" -> period end}
    public List<Dictionary<string, object>> RankThis, RankLast;     // weekly activity ranking [{stRole, iActive}]; null = not loaded
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
      if (unionId <= 0 || members == null || members.Count == 0) return null;
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
      if (Has(union, "iWeekActive")) sb.Append(",\"weekActive\":").Append(L(N(union, "iWeekActive")));
      if (Has(union, "iSevenTotalActive")) sb.Append(",\"sevenActive\":").Append(L(N(union, "iSevenTotalActive")));
      if (Has(union, "iCurDayTotalActive")) sb.Append(",\"dayActive\":").Append(L(N(union, "iCurDayTotalActive")));
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
