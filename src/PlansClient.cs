// RealmForge extractor - equip plans («Надеть в игре») from the RealmForge site.
//
// Contract (implemented by the site, lib/plans/handler.ts):
//   GET  {site}/api/extractor/plans?lang=ru|en        Authorization: Bearer <sync code>
//        200 {ok:true, plans:[{id, heroUid, heroName, createdAt,
//              items:[{slot, uid, slotName, name, setName, level, stars, mainStat, fromHeroUid, fromHeroName, icon,
//                     cur:{uid, name, level, stars, icon}|null}]}]}
//        401 invalid_token
//   POST {site}/api/extractor/plans/{id}                body {"status":"done"|"cancelled"}
//        200 {ok:true} | 401 | 404 not_found
// The site only stores builds; the extractor reads the game (read-only) and shows hints. Nothing is sent to the game.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace RealmForge {
  public sealed class PlanItem {
    public int Slot;
    public long Uid;
    public string SlotName, Name, SetName, MainStat, FromHeroName;
    public int Level, Stars;
    public long FromHeroUid;
    /// <summary>Game icon sprite name (Item_123456) or "" — only [A-Za-z0-9_], it becomes part of a site URL.</summary>
    public string Icon = "";
    /// <summary>What the hero wears in that slot now (null: empty slot or an old plan).</summary>
    public CurItem Cur;
  }

  public sealed class CurItem {
    public long Uid;
    public string Name, Icon = "";
    public int Level, Stars;
  }

  public sealed class Plan {
    public string Id;
    public long HeroUid;
    public string HeroName;
    public string CreatedAt;
    public List<PlanItem> Items = new List<PlanItem>();
  }

  public enum PlansStatus { Ok, InvalidToken, NotFound, ServerError, Unreachable, Unexpected }

  public sealed class PlansResult {
    public PlansStatus Status;
    public int HttpCode;
    public List<Plan> Plans = new List<Plan>();
    public string Details;
  }

  public static class PlansClient {
    const int MaxReplyBytes = 1024 * 1024;

    public static PlansResult List(string site, string code, string lang) {
      return Request("GET", site + "/api/extractor/plans?lang=" + (lang == "en" ? "en" : "ru"), code, null, true);
    }

    public static PlansResult Finish(string site, string code, string planId, bool done) {
      if (planId == null || planId.Length > 64) { var r = new PlansResult(); r.Status = PlansStatus.NotFound; return r; }
      string body = done ? "{\"status\":\"done\"}" : "{\"status\":\"cancelled\"}";
      return Request("POST", site + "/api/extractor/plans/" + Uri.EscapeDataString(planId), code, body, false);
    }

    static PlansResult Request(string method, string url, string code, string body, bool parsePlans) {
      try {
        SyncClient.EnableTls12();
        ServicePointManager.Expect100Continue = false;
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Headers["Authorization"] = "Bearer " + code;
        req.Headers["X-RF-Extractor"] = SyncClient.Version;
        req.Timeout = 30000;
        req.ReadWriteTimeout = 30000;
        req.AllowAutoRedirect = false;
        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        if (body != null) {
          byte[] b = new UTF8Encoding(false).GetBytes(body);
          req.ContentType = "application/json";
          req.ContentLength = b.Length;
          using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
        }
        using (var resp = (HttpWebResponse)req.GetResponse()) return FromResponse(resp, parsePlans);
      } catch (WebException e) {
        var resp = e.Response as HttpWebResponse;
        if (resp != null) using (resp) return FromResponse(resp, parsePlans);
        var r = new PlansResult(); r.Status = PlansStatus.Unreachable; r.Details = e.Message; return r;
      } catch (Exception e) {
        var r = new PlansResult(); r.Status = PlansStatus.Unreachable; r.Details = e.Message; return r;
      }
    }

    static PlansResult FromResponse(HttpWebResponse resp, bool parsePlans) {
      string text = null;
      try {
        using (Stream s = resp.GetResponseStream())
        using (var ms = new MemoryStream()) {
          var buf = new byte[16384]; int n;
          while ((n = s.Read(buf, 0, buf.Length)) > 0 && ms.Length < MaxReplyBytes) ms.Write(buf, 0, n);
          text = Encoding.UTF8.GetString(ms.ToArray());
        }
      } catch (Exception) { }
      return Interpret((int)resp.StatusCode, text, parsePlans);
    }

    // Maps an HTTP reply to a PlansResult. Pure function (tested without a network).
    public static PlansResult Interpret(int code, string body, bool parsePlans) {
      var r = new PlansResult(); r.HttpCode = code;
      var obj = MiniJson.AsObject(MiniJson.TryParse(body));
      if (code >= 200 && code < 300) {
        if (obj == null || !MiniJson.GetBool(obj, "ok", false)) { r.Status = PlansStatus.Unexpected; return r; }
        r.Status = PlansStatus.Ok;
        if (parsePlans) {
          object v; var list = obj.TryGetValue("plans", out v) ? v as List<object> : null;
          if (list != null) foreach (var p in list) { var plan = ParsePlan(MiniJson.AsObject(p)); if (plan != null) r.Plans.Add(plan); }
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

    static string IconName(string s) {
      if (string.IsNullOrEmpty(s) || s.Length > 64) return "";
      foreach (char ch in s) if (!(ch == '_' || (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))) return "";
      return s;
    }

    static Plan ParsePlan(Dictionary<string, object> o) {
      if (o == null) return null;
      var p = new Plan();
      p.Id = MiniJson.GetString(o, "id");
      p.HeroUid = Num(o, "heroUid");
      p.HeroName = MiniJson.GetString(o, "heroName") ?? ("#" + p.HeroUid);
      p.CreatedAt = MiniJson.GetString(o, "createdAt");
      if (string.IsNullOrEmpty(p.Id) || p.HeroUid <= 0) return null;
      object v; var items = o.TryGetValue("items", out v) ? v as List<object> : null;
      if (items != null) foreach (var x in items) {
        var d = MiniJson.AsObject(x); if (d == null) continue;
        var it = new PlanItem();
        it.Slot = (int)Num(d, "slot");
        it.Uid = Num(d, "uid");
        if (it.Uid <= 0 || it.Slot < 0 || it.Slot > 4) continue;
        it.SlotName = MiniJson.GetString(d, "slotName") ?? ("#" + it.Slot);
        it.Name = MiniJson.GetString(d, "name") ?? ("#" + it.Uid);
        it.SetName = MiniJson.GetString(d, "setName");
        it.MainStat = MiniJson.GetString(d, "mainStat") ?? "";
        it.Level = (int)Num(d, "level");
        it.Stars = (int)Num(d, "stars");
        it.FromHeroUid = Num(d, "fromHeroUid");
        it.FromHeroName = MiniJson.GetString(d, "fromHeroName");
        it.Icon = IconName(MiniJson.GetString(d, "icon"));
        object cv; var c = d.TryGetValue("cur", out cv) ? MiniJson.AsObject(cv) : null;
        if (c != null && Num(c, "uid") > 0)
          it.Cur = new CurItem { Uid = Num(c, "uid"), Name = MiniJson.GetString(c, "name") ?? "", Icon = IconName(MiniJson.GetString(c, "icon")),
                                 Level = (int)Num(c, "level"), Stars = (int)Num(c, "stars") };
        p.Items.Add(it);
      }
      p.Items.Sort((a, b) => a.Slot.CompareTo(b.Slot));
      return p.Items.Count > 0 ? p : null;
    }
  }
}
