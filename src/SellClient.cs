// RealmForge extractor - storage cleanup («Очистить склад») from the RealmForge site.
//
// Contract (implemented by the site, lib/sell/handler.ts):
//   GET  {site}/api/extractor/sell?lang=ru|en      Authorization: Bearer <sync code>
//        200 {ok:true, list:{id, createdAt, items:[{uid, slot, name, setName, level, stars}]} | null}
//        401 invalid_token
//   POST {site}/api/extractor/sell/{id}            body {"status":"done"|"cancelled"}
//        200 {ok:true} | 401 | 404 not_found
// The site only stores the list; the app selects the items on the game's sell screen, the player presses «Продать».
using System;
using System.Collections.Generic;

namespace RealmForge {
  public sealed class SellItem {
    public long Uid; public int Slot; public string Name, SetName; public int Level, Stars;
  }

  public sealed class SellList {
    public string Id, CreatedAt;
    public List<SellItem> Items = new List<SellItem>();
  }

  public sealed class SellResult {
    public PlansStatus Status; public int HttpCode; public string Details;
    public SellList List;   // null: no pending list
  }

  public static class SellClient {
    public const int MaxItems = 1000;

    public static SellResult Get(string site, string code, string lang) {
      int http; string text, err;
      if (!PlansClient.Send("GET", site + "/api/extractor/sell?lang=" + (lang == "en" ? "en" : "ru"), code, null, out http, out text, out err))
        return new SellResult { Status = PlansStatus.Unreachable, Details = err };
      return Interpret(http, text);
    }

    public static SellResult Finish(string site, string code, string id, bool done) {
      if (id == null || id.Length > 64) return new SellResult { Status = PlansStatus.NotFound };
      int http; string text, err;
      if (!PlansClient.Send("POST", site + "/api/extractor/sell/" + Uri.EscapeDataString(id), code, done ? "{\"status\":\"done\"}" : "{\"status\":\"cancelled\"}", out http, out text, out err))
        return new SellResult { Status = PlansStatus.Unreachable, Details = err };
      return Interpret(http, text);
    }

    // Maps an HTTP reply to a SellResult. Pure function (tested without a network).
    public static SellResult Interpret(int code, string body) {
      var r = new SellResult { HttpCode = code };
      var obj = MiniJson.AsObject(MiniJson.TryParse(body));
      if (code >= 200 && code < 300) {
        if (obj == null || !MiniJson.GetBool(obj, "ok", false)) { r.Status = PlansStatus.Unexpected; return r; }
        r.Status = PlansStatus.Ok;
        object v; var l = obj.TryGetValue("list", out v) ? MiniJson.AsObject(v) : null;
        if (l != null) {
          string id = MiniJson.GetString(l, "id");
          if (!string.IsNullOrEmpty(id) && id.Length <= 64) {
            var list = new SellList { Id = id, CreatedAt = Clip(MiniJson.GetString(l, "createdAt"), 40) };
            object iv; var items = l.TryGetValue("items", out iv) ? iv as List<object> : null;
            if (items != null) foreach (var x in items) {
              var o = MiniJson.AsObject(x); if (o == null || list.Items.Count >= MaxItems) continue;
              long uid = Num(o, "uid"); if (uid <= 0) continue;
              list.Items.Add(new SellItem {
                Uid = uid, Slot = (int)Math.Min(9, Num(o, "slot")), Name = Clip(MiniJson.GetString(o, "name"), 100),
                SetName = Clip(MiniJson.GetString(o, "setName"), 100), Level = (int)Math.Min(99, Num(o, "level")), Stars = (int)Math.Min(20, Num(o, "stars"))
              });
            }
            if (list.Items.Count > 0) r.List = list;
          }
        }
        return r;
      }
      if (code == 401) r.Status = PlansStatus.InvalidToken;
      else if (code == 404) r.Status = PlansStatus.NotFound;
      else if (code >= 500 && code < 600) r.Status = PlansStatus.ServerError;
      else r.Status = PlansStatus.Unexpected;
      return r;
    }

    static long Num(Dictionary<string, object> d, string key) {
      object v; if (d == null || !d.TryGetValue(key, out v) || !(v is double)) return 0;
      double x = (double)v; if (x < 0 || x > 9e15 || Math.Floor(x) != x) return 0;
      return (long)x;
    }

    static string Clip(string s, int n) { return s == null ? null : s.Length <= n ? s : s.Substring(0, n); }
  }
}
