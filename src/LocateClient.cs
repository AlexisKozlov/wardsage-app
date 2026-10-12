// RealmForge extractor - closing a «Найти в игре» request on the site.
//
// Contract (implemented by the site, lib/locate/handler.ts):
//   POST {site}/api/extractor/locate/{id}          body {"status":"done"|"cancelled"}
//        200 {ok:true} | 401 | 404 not_found | 422
// The request itself arrives with the plans (PlansClient: GET /api/extractor/plans, field "locate").
using System;

namespace RealmForge {
  public static class LocateClient {
    public static PlansResult Finish(string site, string code, string id, bool done) {
      if (string.IsNullOrEmpty(id) || id.Length > 64) return new PlansResult { Status = PlansStatus.NotFound };
      int http; string text, err;
      if (!PlansClient.Send("POST", site + "/api/extractor/locate/" + Uri.EscapeDataString(id), code, done ? "{\"status\":\"done\"}" : "{\"status\":\"cancelled\"}", out http, out text, out err))
        return new PlansResult { Status = PlansStatus.Unreachable, Details = err };
      return PlansClient.Interpret(http, text, false);
    }
  }
}
