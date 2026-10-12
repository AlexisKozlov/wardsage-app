// Wardsage — the guild war (GvG) table of the player's guild, read from the game's memory (READ-ONLY: only ReadProcessMemory)
// and added to the guild upload as the optional "gvg" block. Same way as src/GuildData.cs.
//
// Where the client keeps it (resident once the player is in a guild):
//   a singleton table with m_GVGBriefData, m_GVGSoldierFightTime, m_GVGUnionInfo and m_GVGGlobalData (found by the key m_GVGBriefData;
//   the other war mode has m_GVGUnionInfo too but not the brief data).
//   m_GVGUnionInfo {iIssueId, iScore, iDivision, iFightingRound, iUnionId, iLastRank, uiMatchGrade,
//     vGarrisons = [{stRoleId {iZoneId, iUid}, iId, iSubId}] (the guild's posts; uid 0 = empty, zone 4294967295 = a robot),
//     m_vGarrisons = {[iId]: {Side, status, vSub = {[iSubId]: {Side, status, Role}}}}  (Role is set when the post is taken),
//       Role = {stRoleId {iZoneId, iUid}, sName, iLevel, iAttackedNum, stAttackerStatistic / stDefenderStatistic
//       {iVictoryNum, iDefeatNum, iTotalVictoryNum, iTotalDefeatNum}, iAttackedOpponentHp, iTotalContribution, uiGVGPos (>0 = commander)},
//     vGarrisonRole = the same Role records as a plain list (empty in current clients),
//     vRoundInfo = [{iOpponentUnionId, iWinState (3 = won, 1 = lost, 0 or missing = not decided yet), iStartScore (the guild score at the round start),
//       iDefeatNum, uiFakeGuildId (!= 0 = the opponent was a bot guild)}]}
//   m_GVGGlobalData {iIssueId, iPeriodType, iNextChangeTime (field iNextChangeStateTime), iFightingRound}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RealmForge {
  /// <summary>The guild war tables as ParseTable gives them (any may be null) plus the season limits (null = not known).</summary>
  public class GvgInfo {
    public Dictionary<string, object> Union, Global;
    public long? MaxKeys, CmdExtra;
  }

  public static partial class RFX {
    const string KGvg = "m_GVGBriefData";
    static ulong gvgTab; static int gvgPid, gvgLastFind;

    static bool GvgTable(int rescanMs) {
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) { gvgTab = 0; return false; }
      if (ps[0].Id != gvgPid) { gvgTab = 0; gvgPid = ps[0].Id; gvgLastFind = 0; H = OpenProcess(0x0410, false, ps[0].Id); }
      if (H == IntPtr.Zero) H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return false;
      if (gvgTab != 0 && HasTableX(gvgTab, "m_GVGUnionInfo")) return true;
      gvgTab = 0;
      if (gvgLastFind != 0 && Environment.TickCount - gvgLastFind < rescanMs) return false;
      gvgLastFind = Environment.TickCount;
      regs = Regions();
      var owners = OwnersOf(FindLuaStringsFast(new[] { KGvg }), 1024);
      List<ulong> l; int bestN = -1;
      if (owners.TryGetValue(KGvg, out l))
        foreach (var t in l) {
          ulong m; int mt;
          if (!FieldX(t, KGvg, out m, out mt) || mt != T_TABLE || !HasTableX(t, "m_GVGUnionInfo")) continue;
          int n = EntryCount(t);
          if (n > bestN) { bestN = n; gvgTab = t; }
        }
      return gvgTab != 0;
    }

    /// <summary>The guild war tables; null when not found (no guild, or no war data yet).</summary>
    public static GvgInfo ReadGvg(int rescanMs) {
      if (!GvgTable(rescanMs)) return null;
      var g = new GvgInfo();
      g.Union = SubTable(gvgTab, "m_GVGUnionInfo", 6);
      g.Global = SubTable(gvgTab, "m_GVGGlobalData", 2);
      return g.Union == null ? null : g;
    }

    static void Raw(StringBuilder sb, object v) {
      var d = v as Dictionary<string, object>;
      if (d != null) {
        sb.Append('{'); bool f = true;
        foreach (var kv in d) { if (!f) sb.Append(','); f = false; sb.Append(MiniJson.Quote(kv.Key)).Append(':'); Raw(sb, kv.Value); }
        sb.Append('}');
      } else if (v == null) sb.Append("null");
      else if (v is bool) sb.Append((bool)v ? "true" : "false");
      else if (v is string) sb.Append(MiniJson.Quote((string)v));
      else sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
    }

    /// <summary>Diagnostics (app --dump-gvg file): the raw values found, nothing is sent. Returns a short result line.</summary>
    public static string DumpGvg(string path) {
      try {
        var g = ReadGvg(0);
        if (g == null) { File.WriteAllText(path, "null", new UTF8Encoding(false)); return "no guild war data found"; }
        var sb = new StringBuilder("{\"union\":");
        Raw(sb, g.Union);
        sb.Append(",\"global\":");
        Raw(sb, g.Global);
        sb.Append(",\"payload\":");
        string p = GvgPayload.Build(g);
        sb.Append(p ?? "null").Append('}');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return p == null ? "no guild war data" : "ok";
      } catch (Exception e) { try { File.WriteAllText(path, "error: " + e); } catch (Exception) { } return "error " + e.Message; }
    }
  }

  /// <summary>The pure part (tested without the game): GvgInfo → the "gvg" JSON object of the guild upload.</summary>
  public static class GvgPayload {
    public const int MaxMembers = 200, MaxRounds = 50;

    static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
    static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { return Get(d, k) as Dictionary<string, object>; }
    static long N(Dictionary<string, object> d, string k) { long n; return GuildPayload.Try(Get(d, k), out n) ? n : 0; }
    static string L(long n) { return n.ToString(CultureInfo.InvariantCulture); }
    static string NumOrNull(Dictionary<string, object> d, string k, bool positiveOnly) {
      long n; if (!GuildPayload.Try(Get(d, k), out n) || (positiveOnly && n <= 0)) return "null";
      return L(n);
    }
    // the site wants a number 0..4 here (0 = not known)
    static string Phase(Dictionary<string, object> gl) { long n; return GuildPayload.Try(Get(gl, "iPeriodType"), out n) && n >= 0 && n <= 4 ? L(n) : "0"; }
    static SortedDictionary<long, object> Items(object t) {
      var res = new SortedDictionary<long, object>();
      var d = t as Dictionary<string, object>; if (d == null) return res;
      foreach (var kv in d) {
        long i;
        if (kv.Key.Length > 2 && kv.Key[0] == '[' && kv.Key[kv.Key.Length - 1] == ']'
            && long.TryParse(kv.Key.Substring(1, kv.Key.Length - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) res[i] = kv.Value;
      }
      return res;
    }

    static bool Real(Dictionary<string, object> id, out long zone, out long uid) {
      zone = N(id, "iZoneId"); uid = N(id, "iUid");
      return uid > 0 && zone >= 0 && zone < 2000000000L;
    }

    /// <summary>The guild's own players with their war records: the Role of every taken post of its side (deduped by zone and uid),
    /// plus the plain vGarrisonRole list of older clients. Robots and empty posts are skipped.</summary>
    static List<Dictionary<string, object>> Roles(Dictionary<string, object> u) {
      var res = new List<Dictionary<string, object>>(); var seen = new HashSet<string>();
      Action<Dictionary<string, object>> add = r => {
        long z, id; var rid = Obj(r, "stRoleId");
        if (rid == null || !Real(rid, out z, out id) || !seen.Add(L(z) + ":" + L(id))) return;
        res.Add(r);
      };
      foreach (var kv in Items(Get(u, "vGarrisonRole"))) { var r = kv.Value as Dictionary<string, object>; if (r != null) add(r); }
      // the posts listed for the guild itself tell which players are its own; without that list the lowest side seen is taken
      var own = new HashSet<string>();
      foreach (var kv in Items(Get(u, "vGarrisons"))) {
        var gr = kv.Value as Dictionary<string, object>; long z, id;
        if (gr != null && Obj(gr, "stRoleId") != null && Real(Obj(gr, "stRoleId"), out z, out id)) own.Add(L(z) + ":" + L(id));
      }
      var subs = new List<KeyValuePair<long, Dictionary<string, object>>>(); long minSide = long.MaxValue;
      var mg = Get(u, "m_vGarrisons") as Dictionary<string, object>;
      if (mg != null)
        foreach (var gk in Items(mg)) {
          var g = gk.Value as Dictionary<string, object>; if (g == null) continue;
          foreach (var sk in Items(Get(g, "vSub"))) {
            var s = sk.Value as Dictionary<string, object>; var r = s == null ? null : Obj(s, "Role"); if (r == null) continue;
            long side = Get(s, "Side") == null ? Get(g, "Side") == null ? 0 : N(g, "Side") : N(s, "Side");
            if (side < minSide) minSide = side;
            subs.Add(new KeyValuePair<long, Dictionary<string, object>>(side, r));
          }
        }
      foreach (var p in subs) {
        var rid = Obj(p.Value, "stRoleId"); long z, id;
        if (rid == null || !Real(rid, out z, out id)) continue;
        if (own.Count > 0 ? !own.Contains(L(z) + ":" + L(id)) : p.Key != minSide) continue;
        add(p.Value);
      }
      return res;
    }

    /// <summary>The "gvg" object; null only when there is no war table at all. Members are optional: without them the season, rounds and score are still sent.</summary>
    public static string Build(GvgInfo g) {
      if (g == null || g.Union == null) return null;
      var u = g.Union;
      var sb = new StringBuilder();
      int n = 0;
      foreach (var m in Roles(u)) {
        if (n >= MaxMembers) break;
        var id = Obj(m, "stRoleId");
        string nm = Get(m, "sName") as string ?? "";
        if (nm.Length > 40) nm = nm.Substring(0, 40);
        var a = Obj(m, "stAttackerStatistic"); var d = Obj(m, "stDefenderStatistic");
        if (n++ > 0) sb.Append(',');
        sb.Append("{\"zone\":").Append(L(N(id, "iZoneId"))).Append(",\"uid\":").Append(L(N(id, "iUid"))).Append(",\"name\":").Append(MiniJson.Quote(nm))
          .Append(",\"attacks\":").Append(L(N(m, "iAttackedNum"))).Append(",\"cmd\":").Append(N(m, "uiGVGPos") > 0 ? "true" : "false")
          .Append(",\"atkW\":").Append(L(N(a, "iVictoryNum"))).Append(",\"atkL\":").Append(L(N(a, "iDefeatNum")))
          .Append(",\"atkTotW\":").Append(L(N(a, "iTotalVictoryNum"))).Append(",\"atkTotL\":").Append(L(N(a, "iTotalDefeatNum")))
          .Append(",\"defW\":").Append(L(N(d, "iVictoryNum"))).Append(",\"defL\":").Append(L(N(d, "iDefeatNum")))
          .Append(",\"defTotW\":").Append(L(N(d, "iTotalVictoryNum"))).Append(",\"defTotL\":").Append(L(N(d, "iTotalDefeatNum")))
          .Append(",\"contrib\":").Append(L(N(m, "iTotalContribution"))).Append(",\"oppHp\":").Append(L(N(m, "iAttackedOpponentHp"))).Append('}');
      }
      var rs = new StringBuilder(); int rc = 0;
      foreach (var kv in Items(Get(u, "vRoundInfo"))) {
        var r = kv.Value as Dictionary<string, object>; if (r == null) continue;
        if (rc >= MaxRounds) break;
        long ws; string win = GuildPayload.Try(Get(r, "iWinState"), out ws) && (ws == 3 || ws == 1) ? (ws == 3 ? "1" : "0") : "null";
        bool fake = N(r, "uiFakeGuildId") != 0;
        if (rc++ > 0) rs.Append(',');
        rs.Append("{\"round\":").Append(L(rc)).Append(",\"opp\":").Append(fake ? "null" : NumOrNull(r, "iOpponentUnionId", true))
          .Append(",\"win\":").Append(win).Append(",\"defeatNum\":").Append(L(N(r, "iDefeatNum")))
          .Append(",\"startScore\":").Append(NumOrNull(r, "iStartScore", false)).Append(fake ? ",\"bot\":true" : "").Append('}');
      }
      var gl = g.Global;
      long issue = N(u, "iIssueId"); if (issue <= 0) issue = N(gl, "iIssueId");
      long round = N(u, "iFightingRound"); if (round <= 0) round = N(gl, "iFightingRound");
      var o = new StringBuilder("{\"v\":1,\"issue\":").Append(L(issue)).Append(",\"round\":").Append(L(round))
        .Append(",\"phase\":").Append(Phase(gl)).Append(",\"nextChange\":").Append(NumOrNull(gl, "iNextChangeStateTime", true))
        .Append(",\"score\":").Append(L(N(u, "iScore"))).Append(",\"division\":").Append(L(N(u, "iDivision")))
        .Append(",\"rank\":").Append(NumOrNull(u, "iLastRank", true))
        .Append(",\"maxKeys\":").Append(g.MaxKeys.HasValue ? L(g.MaxKeys.Value) : "null")
        .Append(",\"cmdExtra\":").Append(g.CmdExtra.HasValue ? L(g.CmdExtra.Value) : "null")
        .Append(",\"rounds\":[").Append(rs).Append("],\"members\":[").Append(sb).Append("]}");
      return o.ToString();
    }
  }
}
