// RealmForge — the arena's current opponents, read from the game's memory while the arena screen is open (READ-ONLY),
// for the site's «Соперники» tab (a verdict per opponent). Only ReadProcessMemory: nothing is written, nothing is sent to
// the game's server, the game is not hooked.
//
// Where the client keeps them: the PVPData singleton (mtPVPData()):
//   m_Opponents = the list the arena screen shows (Push_BasicPvp_OpponentData → _HandlerMessage_Push_BasicPvp_OpponentData:
//     each a clone of BasicPvpPlayerData — stRole {iZoneId, iUid, iRoleId}, sName, iLevel, iScore, iRank, iRankId, bRobot,
//     bPityRobot, bCopyRealPlayer, iChallengedTimes, iUnionId, iFaceId, vHeroData [BasicPvpHeroData: iHeroId, iBaseId, iLevel,
//     iPotentialLevel, mSkillLevel, iStarLevel, iSublimLevel, vSkills, mAttr {attr id → raw value}, iPower, iSquadId,
//     iLordPosition, iAwakeningFlag, iSkinId], iPower (= Σ heroes' iPower, set by the client), vFrameInfos (the defence
//     record: CmdFrameIdxData {iIdx frame, iCmd, vParams, iUid}), mStageHeroBasicData {stage → heroes}). The whole list
//     comes with the push the server sends when the arena screen opens (Form_PVPMain:OnActive → OpponentDataReq_CS) and
//     after every refresh (free every m_iFreeRefreshCountInterval s, or paid, or after all are beaten): the client has
//     every opponent's defence heroes with their battle stats (mAttr) from the start, not only when one is opened.
//     The scores are refreshed by GetOpponentScore_SC (iScore) on a timer.
//   m_iPVPLastStageID (the day's defence stage), m_iWeekIndex, m_iScore / m_iRank (the player's), m_iPower and m_vHeroData
//   (the player's attack team for the stage), m_iFreeRefreshOpponentNum (1 = a free refresh is ready),
//   m_iFreeRefreshCountInterval, m_iRefreshEnemyCountLimit, m_LastManualRefreshTime, m_iLastPushOpponentTime (server s),
//   m_stFightRole (the opponent of the fight started last: {iUid, iZoneId, iRoleId}).
// The table is found once per game session by a key only it has (m_iFreeRefreshOpponentMaxNum, set in PVPData:ctor) —
// two passes over the memory, a few seconds — then read directly (a few KB).
//
// The arena screen (Form_PVPMain, UIDefines.ID_FORM_PVPMAIN; the id from the layout data) is open while its node in UIInstance holds a
// table whose m_LuaLogicActive is true (UIBase:OnActive sets it, OnPreInactive clears it).
//
// An arena fight's monster level: BattleData.<iStageLevel> 0x24 (GameSimulation._BattleData 0x48; the server's
// CmdStartChallengeInfo.iStageLevel), read when the fight starts and kept with the fight (ArenaFightJson).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RealmForge {
  public static partial class RFX {
    const string KPvp = "m_iFreeRefreshOpponentMaxNum";
    static ulong pvpData; static int pvpPid, pvpLastFind;

    /// <summary>The arena screen is open now: true / false; null = not known (UIInstance not found yet). Cheap.</summary>
    public static bool? PvpScreenOpen() {
      ulong t = UiKnown ? uiTable : 0; if (t == 0) return null;
      long pvpId = FormId("ID_FORM_PVPMAIN"); if (pvpId == 0) return null;
      var h = Read(t, 56); if (h == null) return null;
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return null;
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
        if (!SameId(BitConverter.ToInt64(nb, o + 16), pvpId)) continue;
        ulong f = BitConverter.ToUInt64(nb, o), v; int tt;
        return Field(f, "m_LuaLogicActive", out v, out tt) && tt == T_BOOL && v != 0;
      }
      return false;   // never opened in this session
    }

    /// <summary>The PVPData table (0 = not found); searched (two passes, a few seconds) when not known, at most every
    /// <paramref name="rescanMs"/>. Call off the interface thread.</summary>
    public static ulong PvpData(int rescanMs) {
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) { pvpData = 0; return 0; }
      if (ps[0].Id != pvpPid) { pvpData = 0; pvpPid = ps[0].Id; pvpLastFind = 0; H = OpenProcess(0x0410, false, ps[0].Id); }
      if (H == IntPtr.Zero) H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return 0;
      if (pvpData != 0 && HasTable(pvpData, "m_Opponents") && HasNum(pvpData, KPvp) && InCurrentState(pvpData)) return pvpData;
      pvpData = 0;
      if (pvpLastFind != 0 && Environment.TickCount - pvpLastFind < rescanMs) return 0;
      pvpLastFind = Environment.TickCount;
      regs = Regions();
      var owners = OwnersOf(FindLuaStringsFast(new[] { KPvp }), 1024);
      List<ulong> l; ulong best = 0; int bestN = -1;
      if (owners.TryGetValue(KPvp, out l))
        foreach (var t in InState(l, CurrentState())) {   // a copy of an earlier login: never taken while the running one is known
          ulong m; int mt;
          if (!HasNum(t, KPvp) || !Field(t, "m_Opponents", out m, out mt) || mt != T_TABLE) continue;
          int n = EntryCount(m) * 10 + (HasNum(t, "m_iScore") ? 1 : 0);   // a lingering old copy: fewer opponents, no score
          if (n > bestN) { bestN = n; best = t; }
        }
      pvpData = best;
      return best;
    }

    /// <summary>PVPData's plain fields and its opponents (parsed script tables), or false when PVPData is not known.</summary>
    static bool ReadPvp(ulong pvp, out Dictionary<string, object> plain, out List<Dictionary<string, object>> opps) {
      plain = null; opps = null;
      if (pvp == 0) return false;
      var d = ParseTable(pvp, 0, new HashSet<ulong>());
      if (d == null) return false;
      plain = new Dictionary<string, object>();
      foreach (var kv in d) if (kv.Value is long || kv.Value is double || kv.Value is bool) plain[kv.Key] = kv.Value;
      ulong v; int tt;
      if (Field(pvp, "m_vHeroData", out v, out tt) && tt == T_TABLE) plain["m_vHeroData"] = ParseTable(v, 2, new HashSet<ulong>());
      if (Field(pvp, "m_stFightRole", out v, out tt) && tt == T_TABLE) plain["m_stFightRole"] = ParseTable(v, 1, new HashSet<ulong>());
      opps = new List<Dictionary<string, object>>();
      if (Field(pvp, "m_Opponents", out v, out tt) && tt == T_TABLE)
        foreach (var e in IntEntries(v)) {
          if (e.Tt != T_TABLE || opps.Count >= 12) continue;
          var od = ParseTable(e.Val, 5, new HashSet<ulong>());
          if (od != null) opps.Add(od);
        }
      return true;
    }

    /// <summary>The arena's opponents as the JSON the site takes (ArenaOpp.Json), with its signature (what changed:
    /// the list, the scores, the refresh); null when PVPData is not found. With the player's saved arena teams
    /// ("teams": stage → hero uids in order, HeroData.m_TeamData for the arena's stages).</summary>
    public static string ReadArenaOpponents(int rescanMs, out string sig) {
      sig = null;
      Dictionary<string, object> plain; List<Dictionary<string, object>> opps;
      if (!ReadPvp(PvpData(rescanMs), out plain, out opps)) return null;
      string teams = ArenaTeams(rescanMs);
      sig = ArenaOpp.Signature(plain, opps) + "|" + teams;
      string json = ArenaOpp.Json(plain, opps);
      return teams.Length > 2 && json.EndsWith("}", StringComparison.Ordinal) ? json.Substring(0, json.Length - 1) + ",\"teams\":" + teams + "}" : json;
    }

    // HeroData (the player's teams: m_TeamData[team id] = [{iHeroId, iSquadId, …}], the arena's team ids = its stage ids,
    // HeroData GetTeamHeros, PVPData GetAttackStageIDByRuleId): found once by its m_TeamData key
    static ulong heroData; static int heroLastFind;
    const string KTeams = "m_TeamData";

    /// <summary>The player's saved teams for the arena's stages (6001000–6002999) as JSON {stage: [hero uid…]}; "{}" when
    /// none / not found.</summary>
    static string ArenaTeams(int rescanMs) {
      if (heroData == 0 || !HasTable(heroData, KTeams) || !InCurrentState(heroData)) {
        heroData = 0;
        if (heroLastFind != 0 && Environment.TickCount - heroLastFind < rescanMs) return "{}";
        heroLastFind = Environment.TickCount;
        if (regs == null) regs = Regions();
        var owners = OwnersOf(FindLuaStringsFast(new[] { KTeams }), 1024);
        List<ulong> l; int bestN = -1;
        if (owners.TryGetValue(KTeams, out l))
          foreach (var t in InState(l, CurrentState())) {
            ulong m; int mt;
            if (!Field(t, KTeams, out m, out mt) || mt != T_TABLE) continue;
            int n = 0; foreach (var e in IntEntries(m)) if (e.Tt == T_TABLE) n++;
            if (n > bestN) { bestN = n; heroData = t; }
          }
        if (heroData == 0) return "{}";
      }
      ulong td; int tt;
      if (!Field(heroData, KTeams, out td, out tt) || tt != T_TABLE) return "{}";
      var sb = new StringBuilder("{"); int k = 0;
      foreach (var e in IntEntries(td)) {
        if (e.Key < 6001000 || e.Key > 6002999 || e.Tt != T_TABLE) continue;
        var uids = new List<string>();
        foreach (var h in IntEntries(e.Val)) {
          if (h.Tt != T_TABLE) continue;
          ulong v; int ht;
          if (Field(h.Val, "iHeroId", out v, out ht) && (ht == T_INT || ht == T_FLT)) {
            long u = ht == T_INT ? (long)v : (long)BitConverter.Int64BitsToDouble((long)v);
            if (u > 0 && uids.Count < 10) uids.Add(u.ToString(CultureInfo.InvariantCulture));
          }
        }
        if (uids.Count == 0) continue;
        if (k++ > 0) sb.Append(',');
        sb.Append('"').Append(e.Key.ToString(CultureInfo.InvariantCulture)).Append("\":[").Append(string.Join(",", uids.ToArray())).Append(']');
      }
      return sb.Append('}').ToString();
    }

    /// <summary>An arena fight starting: the monsters' level (BattleData.iStageLevel), the stage, the opponent of the
    /// fight (PVPData.m_stFightRole, found in the list) and the player's team power. JSON, or null.</summary>
    public static string ArenaFightJson(ulong sim) {
      ulong data = sim != 0 ? Ptr(sim + 0x48) : 0;
      var b = data != 0 ? Read(data + 0x18, 0x10) : null;
      if (b == null) return null;
      int stage = BitConverter.ToInt32(b, 0), chapterType = BitConverter.ToInt32(b, 8), level = BitConverter.ToInt32(b, 0xC);
      Dictionary<string, object> plain = null; List<Dictionary<string, object>> opps = null;
      try { ReadPvp(PvpData(60000), out plain, out opps); } catch (Exception) { }
      return ArenaOpp.FightJson(stage, level, chapterType, plain, opps);
    }
  }

  /// <summary>The pure part (tested without the game): script tables as ParseTable gives them → the site's JSON.</summary>
  public static class ArenaOpp {
    public const int Version = 1;
    const int MaxFrames = 400, MaxHeroes = 8, MaxStages = 6;

    static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
    static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { return Get(d, k) as Dictionary<string, object>; }
    public static long Num(object v) {
      if (v is long) return (long)v;
      if (v is double) { double x = (double)v; return double.IsNaN(x) || double.IsInfinity(x) ? 0 : (long)Math.Round(x); }
      if (v is bool) return (bool)v ? 1 : 0;
      long n; var s = v as string;
      return s != null && long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
    }
    static long N(Dictionary<string, object> d, string k) { return Num(Get(d, k)); }
    static bool Has(Dictionary<string, object> d, string k) { var v = Get(d, k); return v is long || v is double; }

    /// <summary>A script array as ParseTable gives it ({"[1]": a, "[2]": b, …}): the values in index order.</summary>
    public static List<object> Items(object t) {
      var res = new List<object>();
      var d = t as Dictionary<string, object>; if (d == null) return res;
      var keyed = new SortedDictionary<long, object>();
      foreach (var kv in d) {
        long i;
        if (kv.Key.Length > 2 && kv.Key[0] == '[' && kv.Key[kv.Key.Length - 1] == ']'
            && long.TryParse(kv.Key.Substring(1, kv.Key.Length - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) keyed[i] = kv.Value;
      }
      res.AddRange(keyed.Values);
      return res;
    }

    /// <summary>A script map {[k] = number}: "k" → number.</summary>
    static void NumMap(StringBuilder sb, object t) {
      sb.Append('{'); bool first = true;
      var d = t as Dictionary<string, object>;
      if (d != null) foreach (var kv in d) {
        if (kv.Key.Length < 3 || kv.Key[0] != '[' || !(kv.Value is long || kv.Value is double)) continue;
        if (!first) sb.Append(','); first = false;
        sb.Append('"').Append(kv.Key.Substring(1, kv.Key.Length - 2)).Append("\":").Append(Num(kv.Value).ToString(CultureInfo.InvariantCulture));
      }
      sb.Append('}');
    }

    static void Field(StringBuilder sb, string name, long v) { sb.Append(",\"").Append(name).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }

    /// <summary>One defence hero (BasicPvpHeroData).</summary>
    static void Hero(StringBuilder sb, Dictionary<string, object> h) {
      sb.Append("{\"id\":").Append(N(h, "iBaseId").ToString(CultureInfo.InvariantCulture));
      Field(sb, "uid", N(h, "iHeroId")); Field(sb, "lv", N(h, "iLevel")); Field(sb, "star", N(h, "iStarLevel"));
      Field(sb, "sub", N(h, "iSublimLevel")); Field(sb, "aw", N(h, "iAwakeningFlag")); Field(sb, "pot", N(h, "iPotentialLevel"));
      // RealPower: the client keeps the server's power there once GetFightPvpOpponentData has replaced iPower
      Field(sb, "pw", Has(h, "RealPower") ? N(h, "RealPower") : N(h, "iPower"));
      Field(sb, "sq", N(h, "iSquadId")); Field(sb, "lord", N(h, "iLordPosition")); Field(sb, "skin", N(h, "iSkinId"));
      sb.Append(",\"sl\":"); NumMap(sb, Get(h, "mSkillLevel"));
      sb.Append(",\"attr\":"); NumMap(sb, Get(h, "mAttr"));
      // vSkills [CmdIDNum {iId, iNum}]: the hero's battle skills as the server builds the fight — the sets' and the
      // artifact's skills with the hero's own (the site runs them; mAttr carries only their stats)
      sb.Append(",\"sk\":["); int k = 0;
      foreach (var x in Items(Get(h, "vSkills"))) {
        var s = x as Dictionary<string, object>; if (s == null || N(s, "iId") <= 0 || k >= 64) continue;
        if (k++ > 0) sb.Append(',');
        sb.Append('[').Append(N(s, "iId").ToString(CultureInfo.InvariantCulture)).Append(',').Append(N(s, "iNum").ToString(CultureInfo.InvariantCulture)).Append(']');
      }
      sb.Append(']');
      sb.Append('}');
    }

    static void Heroes(StringBuilder sb, object list) {
      sb.Append('['); int n = 0;
      foreach (var x in Items(list)) {
        var h = x as Dictionary<string, object>; if (h == null || N(h, "iBaseId") <= 0 || n >= MaxHeroes) continue;
        if (n++ > 0) sb.Append(','); Hero(sb, h);
      }
      sb.Append(']');
    }

    /// <summary>The defence record: [frame, cmd, controller, params…] (SYNC_PUT_TOWER 1000 [unit, x, y, face], …).</summary>
    static void Frames(StringBuilder sb, object list) {
      sb.Append('['); int n = 0;
      foreach (var x in Items(list)) {
        var f = x as Dictionary<string, object>; if (f == null || n >= MaxFrames) continue;
        if (n++ > 0) sb.Append(',');
        sb.Append('[').Append(N(f, "iIdx").ToString(CultureInfo.InvariantCulture)).Append(',').Append(N(f, "iCmd").ToString(CultureInfo.InvariantCulture))
          .Append(',').Append(N(f, "iUid").ToString(CultureInfo.InvariantCulture));
        foreach (var p in Items(Get(f, "vParams"))) sb.Append(',').Append(Num(p).ToString(CultureInfo.InvariantCulture));
        sb.Append(']');
      }
      sb.Append(']');
    }

    static void Opponent(StringBuilder sb, Dictionary<string, object> o) {
      var role = Obj(o, "stRole");
      sb.Append("{\"uid\":").Append(N(role, "iUid").ToString(CultureInfo.InvariantCulture));
      Field(sb, "zone", N(role, "iZoneId"));
      sb.Append(",\"name\":").Append(MiniJson.Quote(Clip(Get(o, "sName") as string, 40) ?? ""));
      Field(sb, "level", N(o, "iLevel")); Field(sb, "score", N(o, "iScore")); Field(sb, "rank", N(o, "iRank"));
      Field(sb, "rankId", N(o, "iRankId")); Field(sb, "power", N(o, "iPower")); Field(sb, "face", N(o, "iFaceId"));
      Field(sb, "union", N(o, "iUnionId")); Field(sb, "challenged", N(o, "iChallengedTimes"));
      sb.Append(",\"robot\":").Append(N(o, "bRobot") != 0 ? "true" : "false");
      sb.Append(",\"pity\":").Append(N(o, "bPityRobot") != 0 ? "true" : "false");
      sb.Append(",\"copy\":").Append(N(o, "bCopyRealPlayer") != 0 ? "true" : "false");
      sb.Append(",\"heroes\":"); Heroes(sb, Get(o, "vHeroData"));
      sb.Append(",\"frames\":"); Frames(sb, Get(o, "vFrameInfos"));
      sb.Append(",\"stageHeroes\":{");
      var sh = Obj(o, "mStageHeroBasicData"); int n = 0;
      if (sh != null) foreach (var kv in sh) {
        if (kv.Key.Length < 3 || kv.Key[0] != '[' || n >= MaxStages) continue;
        if (n++ > 0) sb.Append(',');
        sb.Append('"').Append(kv.Key.Substring(1, kv.Key.Length - 2)).Append("\":"); Heroes(sb, kv.Value);
      }
      sb.Append("}}");
    }

    static string Clip(string s, int n) { return s == null ? null : s.Length <= n ? s : s.Substring(0, n); }

    /// <summary>The arena's state and opponents as the site's JSON (POST /api/extractor/arena-opponents).</summary>
    public static string Json(Dictionary<string, object> pvp, IList<Dictionary<string, object>> opps) {
      var sb = new StringBuilder("{\"v\":" + Version);
      Field(sb, "stage", N(pvp, "m_iPVPLastStageID")); Field(sb, "week", N(pvp, "m_iWeekIndex")); Field(sb, "rule", N(pvp, "m_PvpRuleId"));
      Field(sb, "score", N(pvp, "m_iScore")); Field(sb, "rank", N(pvp, "m_iRank")); Field(sb, "power", N(pvp, "m_iPower"));
      sb.Append(",\"team\":[");
      int n = 0; foreach (var x in Items(Get(pvp, "m_vHeroData"))) { var h = x as Dictionary<string, object>; long u = N(h, "iHeroId"); if (u <= 0) continue; if (n++ > 0) sb.Append(','); sb.Append(u.ToString(CultureInfo.InvariantCulture)); }
      sb.Append("],\"refresh\":{\"free\":").Append(N(pvp, "m_iFreeRefreshOpponentNum").ToString(CultureInfo.InvariantCulture));
      Field(sb, "interval", N(pvp, "m_iFreeRefreshCountInterval")); Field(sb, "limit", N(pvp, "m_iRefreshEnemyCountLimit"));
      Field(sb, "manualAt", N(pvp, "m_LastManualRefreshTime")); Field(sb, "pushAt", N(pvp, "m_iLastPushOpponentTime"));
      sb.Append("},\"opponents\":[");
      n = 0;
      if (opps != null) foreach (var o in opps) { if (o == null || Obj(o, "stRole") == null) continue; if (n++ > 0) sb.Append(','); Opponent(sb, o); }
      sb.Append("]}");
      return sb.ToString();
    }

    /// <summary>What the site must hear about: the opponents (who, their score and power, their heroes' powers), the
    /// stage and the last push — not the clock.</summary>
    public static string Signature(Dictionary<string, object> pvp, IList<Dictionary<string, object>> opps) {
      var sb = new StringBuilder();
      sb.Append(N(pvp, "m_iPVPLastStageID")).Append('|').Append(N(pvp, "m_iLastPushOpponentTime")).Append('|').Append(N(pvp, "m_iFreeRefreshOpponentNum")).Append('|').Append(N(pvp, "m_iScore"));
      if (opps != null) foreach (var o in opps) {
        sb.Append(';').Append(N(Obj(o, "stRole"), "iUid")).Append(':').Append(N(o, "iScore")).Append(':').Append(N(o, "iPower")).Append(':').Append(N(o, "iChallengedTimes"));
        foreach (var x in Items(Get(o, "vHeroData"))) { var h = x as Dictionary<string, object>; sb.Append(',').Append(N(h, "iBaseId")).Append('/').Append(N(h, "iPower")); }
      }
      return sb.ToString();
    }

    /// <summary>An arena fight's start: the stage, the monsters' level, the opponent (PVPData.m_stFightRole in the list)
    /// with its whole record ("oppRaw", as in the list) and the player's attack team power. JSON.</summary>
    public static string FightJson(int stage, int level, int chapterType, Dictionary<string, object> pvp, IList<Dictionary<string, object>> opps) {
      var sb = new StringBuilder("{\"stage\":" + stage + ",\"level\":" + level + ",\"chapterType\":" + chapterType);
      if (pvp != null) {
        Field(sb, "myPower", N(pvp, "m_iPower")); Field(sb, "myScore", N(pvp, "m_iScore"));
        var role = Get(pvp, "m_stFightRole") as Dictionary<string, object>;
        long uid = N(role, "iUid");
        if (uid != 0 && opps != null)
          foreach (var o in opps) {
            if (N(Obj(o, "stRole"), "iUid") != uid) continue;
            long sum = 0, top = 0; int n = 0;
            foreach (var x in Items(Get(o, "vHeroData"))) { var h = x as Dictionary<string, object>; long p = Has(h, "RealPower") ? N(h, "RealPower") : N(h, "iPower"); sum += p; if (p > top) top = p; n++; }
            sb.Append(",\"opp\":{\"uid\":").Append(uid.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"name\":").Append(MiniJson.Quote(Clip(Get(o, "sName") as string, 40) ?? ""));
            Field(sb, "power", N(o, "iPower")); Field(sb, "heroes", sum); Field(sb, "top", top); Field(sb, "n", n);
            Field(sb, "score", N(o, "iScore")); Field(sb, "rankId", N(o, "iRankId"));
            sb.Append(",\"robot\":").Append(N(o, "bRobot") != 0 ? "true" : "false").Append('}');
            // the whole opponent as the list sends it (its heroes' stats, defence record): the site replays its forecast of this
            // fight against the real outcome (realmforge-web scripts/arena-verdicts.ts)
            sb.Append(",\"oppRaw\":"); Opponent(sb, o);
            break;
          }
      }
      sb.Append('}');
      return sb.ToString();
    }

    /// <summary>A kept arena fight (battles\*.json) as one record of the monster level's fit: when, the stage, the level,
    /// and both sides' battle power from the statistic lines ("c" 1 = the player, 2 = the opponent; app 1.6.24+) or from
    /// the start's reading (arenaInfo). Null when the file is no arena fight with a known level.</summary>
    public static string FightRecord(string battleJson) { return FightRecord(battleJson, false); }

    /// <summary>With <paramref name="withStats"/>: also the player's heroes' battle stats as they entered the fight (the
    /// timeline's heroes[…].base, app 1.6.30+) as "mine": [{uid, unit, base}] — the site runs the player's side on them.</summary>
    public static string FightRecord(string battleJson, bool withStats) {
      var o = MiniJson.AsObject(MiniJson.TryParse(battleJson));
      var info = o != null ? MiniJson.AsObject(Get(o, "arenaInfo")) : null;
      if (info == null) return null;
      long level = Num(Get(info, "level")), stage = Num(Get(info, "stage"));
      if (level <= 0 || stage / 1000 != 6001) return null;
      long[] sum = new long[3], top = new long[3]; int[] cnt = new int[3];
      var lines = Get(o, "lines") as List<object>;
      if (lines != null) foreach (var x in lines) {
        var l = MiniJson.AsObject(x); if (l == null || !(Get(l, "c") is double)) continue;
        int c = (int)Num(Get(l, "c")); if (c < 1 || c > 2) continue;
        long p = Num(Get(l, "iPower")); if (p <= 0) continue;
        sum[c] += p; cnt[c]++; if (p > top[c]) top[c] = p;
      }
      var opp = MiniJson.AsObject(Get(info, "opp"));
      var sb = new StringBuilder("{\"at\":" + MiniJson.Quote(MiniJson.GetString(o, "at") ?? "") + ",\"stage\":" + stage + ",\"level\":" + level);
      Field(sb, "myPower", Num(Get(info, "myPower")));
      if (cnt[1] > 0) { Field(sb, "mySum", sum[1]); Field(sb, "myTop", top[1]); Field(sb, "myN", cnt[1]); }
      if (cnt[2] > 0) { Field(sb, "oppSum", sum[2]); Field(sb, "oppTop", top[2]); Field(sb, "oppN", cnt[2]); }
      if (opp != null) { Field(sb, "oppPower", Num(Get(opp, "power"))); Field(sb, "oppHeroes", Num(Get(opp, "heroes"))); Field(sb, "oppTopList", Num(Get(opp, "top"))); }
      if (withStats) {
        var tl = MiniJson.AsObject(Get(o, "timeline"));
        var hs = tl != null ? MiniJson.AsObject(Get(tl, "heroes")) : null;
        var mine = new StringBuilder(); int nm = 0;
        if (hs != null) foreach (var kv in hs) {
          var h = MiniJson.AsObject(kv.Value); if (h == null) continue;
          if (Get(h, "c") is double && Num(Get(h, "c")) != 1) continue;
          var b = MiniJson.AsObject(Get(h, "base")); if (b == null || nm >= 12) continue;
          mine.Append(nm++ > 0 ? "," : "").Append("{\"uid\":").Append(Num(Get(h, "uid")).ToString(CultureInfo.InvariantCulture)).Append(",\"unit\":").Append(Num(Get(h, "unit")).ToString(CultureInfo.InvariantCulture)).Append(",\"base\":{");
          int nb = 0;
          foreach (var bv in b) { if (!(bv.Value is double) || nb >= 80 || bv.Key.StartsWith("-1") && bv.Key.Length > 2 || bv.Key.StartsWith("-2") && bv.Key.Length > 2) continue; mine.Append(nb++ > 0 ? "," : "").Append(MiniJson.Quote(bv.Key)).Append(':').Append(((double)bv.Value).ToString("R", CultureInfo.InvariantCulture)); }
          mine.Append('}');
          // where and when the game placed it ([frame, x, y, face] — the auto battle's placings repeat fight to fight)
          var pl = Get(h, "placed") as List<object>;
          if (pl != null) {
            mine.Append(",\"placed\":["); int np = 0;
            foreach (var p in pl) {
              var a = p as List<object>; if (a == null || a.Count < 4 || np >= 8) continue;
              mine.Append(np++ > 0 ? "," : "").Append('[');
              for (int i = 0; i < 4; i++) mine.Append(i > 0 ? "," : "").Append(Num(a[i]).ToString(CultureInfo.InvariantCulture));
              mine.Append(']');
            }
            mine.Append(']');
          }
          mine.Append('}');
        }
        if (nm > 0) sb.Append(",\"mine\":[").Append(mine).Append(']');
        // the player's own switches and placings as recorded ([frame, cmd, params…], controller 1): the auto battle replays them
        var opsList = tl != null ? Get(tl, "ops") as List<object> : null;
        if (opsList != null) {
          var myOps = new StringBuilder(); int nOps = 0;
          foreach (var x in opsList) {
            var a = x as List<object>; if (a == null || a.Count < 3 || nOps >= 200) continue;
            if (!(a[0] is double) || !(a[1] is double) || !(a[2] is double) || Num(a[2]) != 1) continue;
            long cmd = Num(a[1]);
            if (cmd != 1000 && cmd != 1001 && cmd != 2000 && cmd != 2001 && cmd != 2003 && cmd != 2011 && cmd != 2015) continue;
            myOps.Append(nOps++ > 0 ? "," : "").Append('[').Append(Num(a[0]).ToString(CultureInfo.InvariantCulture)).Append(',').Append(cmd.ToString(CultureInfo.InvariantCulture));
            for (int i = 3; i < a.Count; i++) myOps.Append(',').Append(Num(a[i]).ToString(CultureInfo.InvariantCulture));
            myOps.Append(']');
          }
          if (nOps > 0) sb.Append(",\"myOps\":[").Append(myOps).Append(']');
        }
        // the opponent's heroes' battle skills ([skill, level], their sets' and artifact's with their own: the opponents'
        // list never shows them) and the opponent's uid — a rematch counts them (the site: lib/arenaOpp/recSkills.ts)
        var raw = MiniJson.AsObject(Get(info, "oppRaw"));
        if (raw != null) Field(sb, "oppUid", Num(Get(raw, "uid")));
        var theirs = new StringBuilder(); int nt = 0;
        if (hs != null) foreach (var kv in hs) {
          var h = MiniJson.AsObject(kv.Value); if (h == null || !(Get(h, "c") is double) || Num(Get(h, "c")) != 2 || nt >= 12) continue;
          var b = MiniJson.AsObject(Get(h, "base")); if (b == null) continue;
          var sk = new StringBuilder(); int ns = 0;
          for (int i = 0; i < 40; i++) {
            object id, lv; if (!b.TryGetValue((-100 - i).ToString(CultureInfo.InvariantCulture), out id) || !(id is double)) break;
            b.TryGetValue((-200 - i).ToString(CultureInfo.InvariantCulture), out lv);
            sk.Append(ns++ > 0 ? "," : "").Append('[').Append(((long)(double)id).ToString(CultureInfo.InvariantCulture)).Append(',').Append((lv is double ? (long)(double)lv : 1).ToString(CultureInfo.InvariantCulture)).Append(']');
          }
          if (ns == 0) continue;
          theirs.Append(nt++ > 0 ? "," : "").Append("{\"unit\":").Append(Num(Get(h, "unit")).ToString(CultureInfo.InvariantCulture)).Append(",\"sk\":[").Append(sk).Append("]}");
        }
        if (nt > 0) sb.Append(",\"theirs\":[").Append(theirs).Append(']');
      }
      sb.Append('}');
      return sb.ToString();
    }

    /// <summary>The kept arena fights with a known level (the newest 30 files of <paramref name="dir"/>) as a JSON array.</summary>
    public static string FightRecords(string dir) {
      var sb = new StringBuilder("["); int n = 0;
      try {
        if (Directory.Exists(dir)) {
          var files = Directory.GetFiles(dir, "*.json"); Array.Sort(files);
          int k = 0;
          foreach (var f in files) {
            string r = null;
            // the battle stats only for the newest 5 files (the request stays small)
            bool stats = files.Length - k++ <= 5;
            try { var s = File.ReadAllText(f, Encoding.UTF8); if (s.IndexOf("\"arenaInfo\"", StringComparison.Ordinal) >= 0) r = FightRecord(s, stats); } catch (Exception) { }
            if (r == null) continue;
            if (n++ > 0) sb.Append(','); sb.Append(r);
          }
        }
      } catch (Exception) { }
      return sb.Append(']').ToString();
    }
  }
}
