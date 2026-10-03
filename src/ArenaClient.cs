// RealmForge — the arena's opponents to the RealmForge site (the «Соперники» tab polls them).
//
// Contract (implemented by the site, lib/arenaOpp/handler.ts):
//   POST {site}/api/extractor/arena-opponents      Authorization: Bearer <sync code>
//        body {v:1, stage, week, rule, score, rank, power, team:[uid], refresh:{free, interval, limit, manualAt, pushAt},
//              opponents:[{uid, zone, name, level, score, rank, rankId, power, face, union, challenged, robot, pity, copy,
//                          heroes:[{id, uid, lv, star, sub, aw, pot, pw, sq, lord, skin, sl:{}, attr:{}}],
//                          frames:[[frame, cmd, controller, params…]], stageHeroes:{stage:[hero]}}],
//              fights:[{at, stage, level, myPower, mySum?, myTop?, oppSum?, oppTop?, oppPower?, …}]}   (src/ArenaOpponents.cs)
//        200 {ok:true}   401 invalid_token   422 invalid_payload{details}   429 rate_limited
// One current list per game account: the newest replaces the old one.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public static class ArenaClient {
    public static PlansStatus Send(string site, string code, string json, out string details) {
      int http; string text, err;
      details = null;
      if (!PlansClient.Send("POST", site + "/api/extractor/arena-opponents", code, json, out http, out text, out err)) { details = err; return PlansStatus.Unreachable; }
      return Interpret(http, text, out details);
    }

    /// <summary>Maps the site's reply (pure function, tested without a network).</summary>
    public static PlansStatus Interpret(int http, string body, out string details) {
      details = null;
      var o = MiniJson.AsObject(MiniJson.TryParse(body));
      if (http >= 200 && http < 300) return o != null && MiniJson.GetBool(o, "ok", false) ? PlansStatus.Ok : PlansStatus.Unexpected;
      if (o != null) {
        details = MiniJson.GetString(o, "error");
        object d; var list = o.TryGetValue("details", out d) ? d as List<object> : null;
        if (list != null && list.Count > 0) details = (details ?? "") + ": " + string.Join("; ", list.ConvertAll(x => Convert.ToString(x)).ToArray());
      }
      if (http == 401) return PlansStatus.InvalidToken;
      if (http == 404) return PlansStatus.NotFound;
      if (http >= 500 && http < 600) return PlansStatus.ServerError;
      return PlansStatus.Unexpected;
    }

    /// <summary>The opponents' JSON with the kept arena fights (the monster level's fit) added as "fights".</summary>
    public static string WithFights(string opponentsJson, string fightsJson) {
      if (string.IsNullOrEmpty(opponentsJson) || opponentsJson[opponentsJson.Length - 1] != '}') return opponentsJson;
      if (string.IsNullOrEmpty(fightsJson) || fightsJson[0] != '[') fightsJson = "[]";
      return opponentsJson.Substring(0, opponentsJson.Length - 1) + ",\"fights\":" + fightsJson + "}";
    }
  }
}
