// Wardsage — the guild war (GvG) table of the player's guild, read from the game's memory (READ-ONLY: only ReadProcessMemory)
// and added to the guild upload as the optional "gvg" block. Same way as src/GuildData.cs.
//
// Where the client keeps it (resident once the player is in a guild):
//   a singleton table with m_GVGBriefData, m_GVGSoldierFightTime, m_GVGUnionInfo and m_GVGGlobalData (found by the key m_GVGBriefData;
//   the other war mode has m_GVGUnionInfo too but not the brief data).
//   m_GVGUnionInfo {iIssueId, iScore, iDivision, iFightingRound, iUnionId, iLastRank, uiMatchGrade,
//     vGarrisonRole = [{stRoleId {iZoneId, iUid}, sName, iLevel, iAttackedNum, stAttackerStatistic / stDefenderStatistic
//       {iVictoryNum, iDefeatNum, iTotalVictoryNum, iTotalDefeatNum}, iAttackedOpponentHp, iTotalContribution, uiGVGPos (>0 = commander)}],
//     vRoundInfo = [{iOpponentUnionId, iWinState, iDefeatNum, uiFakeGuildId}]}
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
      if (gvgTab != 0 && HasTable(gvgTab, "m_GVGUnionInfo")) return true;
      gvgTab = 0;
      if (gvgLastFind != 0 && Environment.TickCount - gvgLastFind < rescanMs) return false;
      gvgLastFind = Environment.TickCount;
      regs = Regions();
      var owners = OwnersOf(FindLuaStringsFast(new[] { KGvg }), 1024);
      List<ulong> l; int bestN = -1;
      if (owners.TryGetValue(KGvg, out l))
        foreach (var t in l) {
          ulong m; int mt;
          if (!Field(t, KGvg, out m, out mt) || mt != T_TABLE || !HasTable(t, "m_GVGUnionInfo")) continue;
          int n = EntryCount(t);
          if (n > bestN) { bestN = n; gvgTab = t; }
        }
      return gvgTab != 0;
    }

    /// <summary>The guild war tables; null when not found (no guild, or no war data yet).</summary>
    public static GvgInfo ReadGvg(int rescanMs) {
      if (!GvgTable(rescanMs)) return null;
      var g = new GvgInfo();
      g.Union = SubTable(gvgTab, "m_GVGUnionInfo", 5);
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
        return p == null ? "no members" : "ok";
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

    /// <summary>The "gvg" object; null when there is no war table or no members in it.</summary>
    public static string Build(GvgInfo g) {
      if (g == null || g.Union == null) return null;
      var u = g.Union;
      var sb = new StringBuilder();
      int n = 0;
      foreach (var kv in Items(Get(u, "vGarrisonRole"))) {
        var m = kv.Value as Dictionary<string, object>; if (m == null || n >= MaxMembers) continue;
        var id = Obj(m, "stRoleId");
        if (id == null || N(id, "iUid") <= 0) continue;
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
      if (n == 0) return null;
      var rs = new StringBuilder(); int rc = 0;
      foreach (var kv in Items(Get(u, "vRoundInfo"))) {
        var r = kv.Value as Dictionary<string, object>; if (r == null) continue;
        if (rc >= MaxRounds) break;
        long ws; string win = GuildPayload.Try(Get(r, "iWinState"), out ws) && (ws == 0 || ws == 1) ? L(ws) : "null";
        bool fake = N(r, "uiFakeGuildId") > 0;
        if (rc++ > 0) rs.Append(',');
        rs.Append("{\"round\":").Append(L(rc)).Append(",\"opp\":").Append(fake ? "null" : NumOrNull(r, "iOpponentUnionId", true))
          .Append(",\"win\":").Append(win).Append(",\"defeatNum\":").Append(L(N(r, "iDefeatNum"))).Append('}');
      }
      var gl = g.Global;
      long issue = N(u, "iIssueId"); if (issue <= 0) issue = N(gl, "iIssueId");
      long round = N(u, "iFightingRound"); if (round <= 0) round = N(gl, "iFightingRound");
      var o = new StringBuilder("{\"v\":1,\"issue\":").Append(L(issue)).Append(",\"round\":").Append(L(round))
        .Append(",\"phase\":").Append(NumOrNull(gl, "iPeriodType", false)).Append(",\"nextChange\":").Append(NumOrNull(gl, "iNextChangeStateTime", true))
        .Append(",\"score\":").Append(L(N(u, "iScore"))).Append(",\"division\":").Append(L(N(u, "iDivision")))
        .Append(",\"rank\":").Append(NumOrNull(u, "iLastRank", true))
        .Append(",\"maxKeys\":").Append(g.MaxKeys.HasValue ? L(g.MaxKeys.Value) : "null")
        .Append(",\"cmdExtra\":").Append(g.CmdExtra.HasValue ? L(g.CmdExtra.Value) : "null")
        .Append(",\"rounds\":[").Append(rs).Append("],\"members\":[").Append(sb).Append("]}");
      return o.ToString();
    }
  }
}
