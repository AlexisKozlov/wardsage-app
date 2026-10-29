// RealmForge extractor - equip plans («Надеть в игре») from the RealmForge site.
//
// Contract (implemented by the site, lib/plans/handler.ts):
//   GET  {site}/api/extractor/plans?lang=ru|en&artifacts=1        Authorization: Bearer <sync code>
//        "artifacts=1" tells the site this app can guide an artifact promotion request (purpose "artPromote"); an older app never sends it and never gets one.
//        "art=1" tells the site this app can also put an artifact on the hero: a plan may then carry  artifact:{uid, id[, name]}  (the artifact to wear after the 5 gear items).
//        200 {ok:true, plans:[{id, heroUid, heroName, createdAt,
//              items:[{slot, uid, slotName, name, setName, setId?, level, stars, mainStat, fromHeroUid, fromHeroName, icon,
//                     cur:{uid, name, level, stars, icon}|null}]}]}
//        401 invalid_token
//   POST {site}/api/extractor/plans/{id}                body {"status":"done"|"cancelled"}
//        200 {ok:true} | 401 | 404 not_found
// The same reply may carry  locate: {id, purpose:"recast"|"ascend", createdAt, item:{...a plan item, heroUid, heroName}|null}|null  -
// a «Найти в игре» request from the site (src/LocateGuide.cs); older sites omit it.
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
    public long SetId;          // for the game's gear filter; 0 = not sent (older plans / site)
    /// <summary>Game icon sprite name (Item_123456) or "" — only [A-Za-z0-9_], it becomes part of a site URL.</summary>
    public string Icon = "";
    /// <summary>Set icon sprite name (icon_suit_…) or "" — same rules as Icon.</summary>
    public string SetIcon = "";
    /// <summary>What the hero wears in that slot now (null: empty slot, or unknown when CurKnown is false).</summary>
    public CurItem Cur;
    public bool CurKnown;
    public List<SubStat> Subs = new List<SubStat>();
    public List<SubStat> SetBonus = new List<SubStat>();   // Rolls = pieces needed
    /// <summary>Game-style card (absent for older plans): main stats with the stat id, the item quality and its name.</summary>
    public List<SubStat> Main = new List<SubStat>();
    public int Quality;
    public string QualityName;
  }

  public sealed class CurItem {
    public long Uid;
    public string Name, Icon = "", MainStat = "";
    public int Level, Stars;
    public List<SubStat> Subs = new List<SubStat>();
    public List<SubStat> Main = new List<SubStat>();
    public int Quality;
    public string QualityName;
  }

  /// <summary>Substat line of the item card («АТК +35») and its upgrade count; also used for set bonuses (Rolls = pieces)
  /// and main stats. For the game-style card: stat id (icon; -1 unknown), name and value apart, the game's bar 0..1 (-1 none).</summary>
  public sealed class SubStat {
    public string Text;
    public int Rolls;
    public int Stat = -1;
    public string Name, Value, Now;   // Now: a main stat's value now when the card shows it at +16
    public double Bar = -1;
  }

  /// <summary>The artifact a plan also puts on the hero (site: "artifact":{uid, id}); the app guides it after the gear (src/ArtEquip.cs).</summary>
  public sealed class PlanArtifact {
    public long Uid;
    public int Id;
    public string Name = "";   // optional (the site may not send one)
  }

  public sealed class Plan {
    public string Id;
    public long HeroUid;
    public string HeroName;
    public string CreatedAt;
    public List<PlanItem> Items = new List<PlanItem>();
    public PlanArtifact Artifact;   // null: the plan has no artifact
  }

  /// <summary>The artifact of a "artPromote" request: what the app guides the player to (Inventory → «Артефакт» → the artifact → «Мастерская» → «Продвинуть» → the copy).</summary>
  public sealed class ArtPromoteInfo {
    public long Uid;                 // the artifact (iItemUid)
    public int Id;                   // its type (iItemId)
    public string Name = "";
    public int Level, Cap, Stage;    // level now, the level cap of the stage, the stage when the site looked
    public int Need = 1;             // copies the promotion takes
    public string Via = "none";      // what the plan spends: copy | essence | none
    public long Copy;                // the planned spare copy (uid), 0 = none
    public List<long> Copies = new List<long>();   // the copies to pick, in order (the planned one first)
  }

  /// <summary>A «Найти в игре» request: the item to show the player in the game (Item null: not in the site's last snapshot).
  /// Purpose "artPromote": Art is the artifact (Art null: it is not in the snapshot), Item is always null.</summary>
  public sealed class LocateRequest {
    public string Id, Purpose, CreatedAt;
    public PlanItem Item;
    public ArtPromoteInfo Art;
    public long HeroUid;
    public string HeroName;
  }

  public enum PlansStatus { Ok, InvalidToken, NotFound, ServerError, Unreachable, Unexpected }

  public sealed class PlansResult {
    public PlansStatus Status;
    public int HttpCode;
    public List<Plan> Plans = new List<Plan>();
    public LocateRequest Locate;   // null: no pending request
    public string Details;
  }

  public static class PlansClient {
    /// <summary>A «cancelled» report that did not get through (site unreachable or busy) is worth repeating; any other answer is final.</summary>
    public static bool RetryableCancel(PlansStatus s) { return s == PlansStatus.Unreachable || s == PlansStatus.ServerError; }
    public const int CancelRetryMs = 10000, CancelRetryTotalMs = 5 * 60 * 1000;

    const int MaxReplyBytes = 1024 * 1024;

    public static PlansResult List(string site, string code, string lang) {
      return Request("GET", site + "/api/extractor/plans?lang=" + (lang == "en" ? "en" : "ru") + "&artifacts=1&art=1", code, null, true);
    }

    public static PlansResult Finish(string site, string code, string planId, bool done) {
      if (planId == null || planId.Length > 64) { var r = new PlansResult(); r.Status = PlansStatus.NotFound; return r; }
      string body = done ? "{\"status\":\"done\"}" : "{\"status\":\"cancelled\"}";
      return Request("POST", site + "/api/extractor/plans/" + Uri.EscapeDataString(planId), code, body, false);
    }

    static PlansResult Request(string method, string url, string code, string body, bool parsePlans) {
      int http; string text, err;
      if (!Send(method, url, code, body, true, out http, out text, out err)) { var r = new PlansResult(); r.Status = PlansStatus.Unreachable; r.Details = err; return r; }
      return Interpret(http, text, parsePlans);
    }

    /// <summary>One request with the sync code; false when the site was not reached (err says why).</summary>
    internal static bool Send(string method, string url, string code, string body, out int http, out string text, out string err) {
      return Send(method, url, code, body, false, out http, out text, out err);
    }

    /// <summary>The same; <paramref name="forPlans"/>: a plan / sell-list / locate request, named for SyncClient.PlanPlayer
    /// (the account last read on this install while the game's one is not read yet). Data read from the game never is.</summary>
    internal static bool Send(string method, string url, string code, string body, bool forPlans, out int http, out string text, out string err) {
      http = 0; text = null; err = null;
      try {
        SyncClient.EnableTls12();
        ServicePointManager.Expect100Continue = false;
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Headers["Authorization"] = "Bearer " + code;
        req.Headers["X-RF-Extractor"] = SyncClient.Version;
        if (forPlans) SyncClient.AddPlanPlayer(req); else SyncClient.AddPlayer(req);
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
        using (var resp = (HttpWebResponse)req.GetResponse()) { http = (int)resp.StatusCode; text = ReadBody(resp); }
        return true;
      } catch (WebException e) {
        var resp = e.Response as HttpWebResponse;
        if (resp != null) using (resp) { http = (int)resp.StatusCode; text = ReadBody(resp); return true; }
        err = e.Message; return false;
      } catch (Exception e) {
        err = e.Message; return false;
      }
    }

    static string ReadBody(HttpWebResponse resp) {
      try {
        using (Stream s = resp.GetResponseStream())
        using (var ms = new MemoryStream()) {
          var buf = new byte[16384]; int n;
          while ((n = s.Read(buf, 0, buf.Length)) > 0 && ms.Length < MaxReplyBytes) ms.Write(buf, 0, n);
          return Encoding.UTF8.GetString(ms.ToArray());
        }
      } catch (Exception) { return null; }
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
          object lv; if (obj.TryGetValue("locate", out lv)) r.Locate = ParseLocate(MiniJson.AsObject(lv));
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

    // [{text, rolls}] lists of the item card (at most 12 lines of 200 characters)
    static List<SubStat> Lines(Dictionary<string, object> d, string key, string textKey, string numKey) {
      var r = new List<SubStat>(); object v;
      var list = d != null && d.TryGetValue(key, out v) ? v as List<object> : null;
      if (list == null) return r;
      foreach (var x in list) {
        var o = MiniJson.AsObject(x); if (o == null) continue;
        string t = MiniJson.GetString(o, textKey); if (string.IsNullOrEmpty(t)) continue;
        var s = new SubStat { Text = Clip(t, 200), Rolls = numKey == null ? 0 : (int)Math.Min(99, Num(o, numKey)) };
        object sv;
        if (o.TryGetValue("stat", out sv) && sv is double && (double)sv >= 0 && (double)sv < 100000) s.Stat = (int)(double)sv;
        s.Name = Clip(MiniJson.GetString(o, "name"), 80);
        s.Value = Clip(MiniJson.GetString(o, "value"), 40);
        s.Now = Clip(MiniJson.GetString(o, "now"), 40);
        if (o.TryGetValue("bar", out sv) && sv is double && (double)sv >= 0 && (double)sv <= 1) s.Bar = (double)sv;
        r.Add(s);
        if (r.Count == 12) break;
      }
      return r;
    }

    static string Clip(string s, int n) { return s == null ? null : s.Length > n ? s.Substring(0, n) : s; }

    static void Card(Dictionary<string, object> o, out List<SubStat> main, out int quality, out string qualityName) {
      main = Lines(o, "main", "name", null);
      quality = (int)Math.Max(0, Math.Min(99, Num(o, "quality")));
      qualityName = Clip(MiniJson.GetString(o, "qualityName"), 60);
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
        var it = ParseItem(MiniJson.AsObject(x));
        if (it != null) p.Items.Add(it);
      }
      p.Items.Sort((a, b) => a.Slot.CompareTo(b.Slot));
      object av;
      if (o.TryGetValue("artifact", out av)) {
        var ao = MiniJson.AsObject(av);
        if (ao != null && Num(ao, "uid") > 0) p.Artifact = new PlanArtifact { Uid = Num(ao, "uid"), Id = (int)Math.Min(int.MaxValue, Num(ao, "id")), Name = Clip(MiniJson.GetString(ao, "name"), 100) ?? "" };
      }
      return p.Items.Count > 0 ? p : null;
    }

    static LocateRequest ParseLocate(Dictionary<string, object> o) {
      if (o == null) return null;
      string id = MiniJson.GetString(o, "id");
      if (string.IsNullOrEmpty(id) || id.Length > 64) return null;
      string purpose = MiniJson.GetString(o, "purpose") ?? "recast";
      if (purpose != "recast" && purpose != "ascend" && purpose != "artPromote") return null;
      var r = new LocateRequest { Id = id, Purpose = purpose, CreatedAt = Clip(MiniJson.GetString(o, "createdAt"), 40) };
      object iv;
      if (purpose == "artPromote") { r.Art = ParseArt(o.TryGetValue("art", out iv) ? MiniJson.AsObject(iv) : null); return r; }
      var d = o.TryGetValue("item", out iv) ? MiniJson.AsObject(iv) : null;
      r.Item = ParseItem(d);
      if (r.Item != null) {
        r.HeroUid = Num(d, "heroUid");
        r.HeroName = Clip(MiniJson.GetString(d, "heroName"), 100);
      }
      return r;
    }

    static ArtPromoteInfo ParseArt(Dictionary<string, object> d) {
      if (d == null) return null;
      var a = new ArtPromoteInfo { Uid = Num(d, "uid") };
      if (a.Uid <= 0) return null;
      a.Id = (int)Num(d, "id"); a.Name = Clip(MiniJson.GetString(d, "name"), 100) ?? "";
      a.Level = (int)Math.Max(0, Math.Min(99, Num(d, "lv"))); a.Cap = (int)Math.Max(0, Math.Min(99, Num(d, "cap"))); a.Stage = (int)Math.Max(0, Math.Min(9, Num(d, "st")));
      a.Need = (int)Math.Max(1, Math.Min(20, Num(d, "need")));
      string via = MiniJson.GetString(d, "via"); a.Via = via == "copy" || via == "essence" ? via : "none";
      a.Copy = Num(d, "copy");
      object cv; var cl = d.TryGetValue("copies", out cv) ? cv as List<object> : null;
      if (cl != null) foreach (var x in cl) if (x is double && (double)x > 0 && a.Copies.Count < 20) a.Copies.Add((long)(double)x);
      return a;
    }

    static PlanItem ParseItem(Dictionary<string, object> d) {
      {
        if (d == null) return null;
        var it = new PlanItem();
        it.Slot = (int)Num(d, "slot");
        it.Uid = Num(d, "uid");
        if (it.Uid <= 0 || it.Slot < 0 || it.Slot > 4) return null;
        it.SlotName = MiniJson.GetString(d, "slotName") ?? ("#" + it.Slot);
        it.Name = MiniJson.GetString(d, "name") ?? ("#" + it.Uid);
        it.SetName = MiniJson.GetString(d, "setName");
        it.MainStat = MiniJson.GetString(d, "mainStat") ?? "";
        it.Level = (int)Num(d, "level");
        it.Stars = (int)Num(d, "stars");
        it.FromHeroUid = Num(d, "fromHeroUid");
        it.SetId = Num(d, "setId");
        it.FromHeroName = MiniJson.GetString(d, "fromHeroName");
        it.Icon = IconName(MiniJson.GetString(d, "icon"));
        it.SetIcon = IconName(MiniJson.GetString(d, "setIcon"));
        object cv; it.CurKnown = d.TryGetValue("cur", out cv);
        var c = it.CurKnown ? MiniJson.AsObject(cv) : null;
        if (c != null && Num(c, "uid") > 0)
          it.Cur = new CurItem { Uid = Num(c, "uid"), Name = MiniJson.GetString(c, "name") ?? "", Icon = IconName(MiniJson.GetString(c, "icon")),
                                 Level = (int)Num(c, "level"), Stars = (int)Num(c, "stars"), MainStat = MiniJson.GetString(c, "mainStat") ?? "",
                                 Subs = Lines(c, "subs", "text", "rolls") };
        if (it.Cur != null) Card(c, out it.Cur.Main, out it.Cur.Quality, out it.Cur.QualityName);
        it.Subs = Lines(d, "subs", "text", "rolls");
        Card(d, out it.Main, out it.Quality, out it.QualityName);
        it.SetBonus = Lines(d, "setBonus", "text", "pieces");
        return it;
      }
    }
  }
}
