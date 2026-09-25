// RealmForge.exe - messages between the page (ui/app.js) and the program.
//
// page -> host  {cmd:"init"|"setLang"|"setCode"|"clearCode"|"setSaveCopy"|"setSite"|"paste"|"sync"|"open"|"openLog"|
//                    "openFolder"|"plans.load"|"equip.scan"|"equip.watch"|"equip.finish"|"highlight"|"compact", ...}
// host -> page  {ev:"state"|"game"|"paste"|"sync"|"plans"|"equip.scan"|"equip.live"|"overlay", ...}
// The page is trusted content from our own folder, but every argument is still validated here: codes by format,
// URLs by scheme and host, item uids as numbers. Nothing here writes to the game.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace RealmForge {
  sealed class HostBridge : IDisposable {
    readonly AppWindow win;
    readonly CoreWebView2 core;
    readonly AppConfig cfg;
    readonly System.Windows.Forms.Timer gameTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer liveTimer = new System.Windows.Forms.Timer();
    int gamePid; string gameVersion; bool gameSent, gameRunning;
    volatile bool syncing, scanning;
    EquipAddrs addrs;
    List<long> watch = new List<long>();
    string lastLive;
    string lastSavedPath;
    readonly OverlayController overlay;

    public HostBridge(AppWindow win, CoreWebView2 core) {
      this.win = win; this.core = core;
      cfg = AppConfig.Load();
      if (string.IsNullOrEmpty(cfg.Site)) cfg.Site = SyncClient.DefaultSite;
      gameTimer.Interval = 2000; gameTimer.Tick += (s, e) => CheckGame(false); gameTimer.Start();
      liveTimer.Interval = 400; liveTimer.Tick += (s, e) => PollLive();
      overlay = new OverlayController(st => Post("{\"ev\":\"overlay\",\"state\":" + S(st) + "}"));
      Log.Write("RealmForge " + Program.Version + " started");
    }

    public void Dispose() { gameTimer.Dispose(); liveTimer.Dispose(); overlay.Dispose(); }

    // ------------------------------------------------------------------ plumbing

    void Post(string json) {
      if (win.IsDisposed) return;
      if (win.InvokeRequired) { try { win.BeginInvoke((Action)(() => Post(json))); } catch (Exception) { } return; }
      try { core.PostWebMessageAsJson(json); } catch (Exception) { }
    }

    static string S(string v) { return v == null ? "null" : MiniJson.Quote(v); }
    static string B(bool v) { return v ? "true" : "false"; }
    static string N(long v) { return v.ToString(CultureInfo.InvariantCulture); }

    public void OnMessage(string json) {
      var m = MiniJson.AsObject(MiniJson.TryParse(json));
      if (m == null) return;
      string cmd = MiniJson.GetString(m, "cmd");
      try {
        switch (cmd) {
          case "init": SendState(null); CheckGame(true); break;
          case "setLang": cfg.Lang = MiniJson.GetString(m, "lang") == "en" ? "en" : "ru"; Save(); break;
          case "setCode": {
            string code = SyncClient.ExtractCode(MiniJson.GetString(m, "code") ?? "");
            if (SyncClient.IsValidCode(code)) { cfg.Code = code; Save(); SendState("codeSaved"); }
            break;
          }
          case "clearCode": cfg.Code = ""; Save(); SendState(null); break;
          case "setSaveCopy": cfg.SaveCopy = MiniJson.GetBool(m, "on", false); Save(); break;
          case "setSite": {
            string err; string site = SyncClient.NormalizeSite(MiniJson.GetString(m, "site"), out err);
            if (site != null) { cfg.Site = site; Save(); SendState("siteSaved"); }
            break;
          }
          case "paste": {
            string text = "";
            try { if (Clipboard.ContainsText()) text = SyncClient.ExtractCode(Clipboard.GetText()); } catch (Exception) { }
            if (text.Length > 200) text = text.Substring(0, 200);
            Post("{\"ev\":\"paste\",\"text\":" + S(text) + "}");
            break;
          }
          case "sync": StartSync(MiniJson.GetBool(m, "saveOnly", false)); break;
          case "open": OpenAllowed(MiniJson.GetString(m, "url")); break;
          case "openLog": Shell.OpenFile(Log.Path); break;
          case "openFolder": Shell.OpenFolder(lastSavedPath ?? Extractor.OutputDir); break;
          case "plans.load": LoadPlans(MiniJson.GetString(m, "lang")); break;
          case "equip.scan": StartScan(Uids(m)); break;
          case "equip.watch": Watch(Uids(m)); break;
          case "equip.finish": FinishPlan(MiniJson.GetString(m, "id"), MiniJson.GetBool(m, "done", false)); break;
          case "highlight": {
            object v; double u = m.TryGetValue("uid", out v) && v is double ? (double)v : 0;
            overlay.SetTarget(u > 0 && u < 9e15 && watch.Contains((long)u) ? (long)u : 0);
            break;
          }
          case "compact": win.SetCompact(MiniJson.GetBool(m, "on", false)); break;
        }
      } catch (Exception e) { Log.Write("message " + cmd + ": " + e); }
    }

    void Save() { try { cfg.Save(); } catch (Exception e) { Log.Write("config save: " + e.Message); } }

    static List<long> Uids(Dictionary<string, object> m) {
      var r = new List<long>(); object v;
      var list = m.TryGetValue("uids", out v) ? v as List<object> : null;
      if (list != null) foreach (var x in list) if (x is double && (double)x > 0 && (double)x < 9e15 && r.Count < 500) r.Add((long)(double)x);
      return r;
    }

    // Only the site, the project page on GitHub and Microsoft's WebView2 page are ever opened.
    void OpenAllowed(string url) {
      Uri u, site;
      if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) return;
      bool ok = u.Host == "github.com" || u.Host == "go.microsoft.com";
      if (Uri.TryCreate(cfg.Site, UriKind.Absolute, out site) && u.Host == site.Host) ok = true;
      if (ok) Shell.OpenUrl(u.AbsoluteUri);
    }

    void SendState(string flag) {
      var sb = new StringBuilder("{\"ev\":\"state\"");
      sb.Append(",\"lang\":").Append(S(cfg.Lang == "en" ? "en" : "ru"));
      sb.Append(",\"version\":").Append(S(Program.Version));
      sb.Append(",\"site\":").Append(S(cfg.Site));
      sb.Append(",\"defaultSite\":").Append(S(SyncClient.DefaultSite));
      bool has = SyncClient.IsValidCode(cfg.Code);
      sb.Append(",\"hasCode\":").Append(B(has));
      sb.Append(",\"codePrefix\":").Append(S(has ? cfg.Code.Substring(0, 8) : ""));
      sb.Append(",\"saveCopy\":").Append(B(cfg.SaveCopy));
      sb.Append(",\"last\":").Append(LastJson());
      if (flag != null) sb.Append(",\"").Append(flag).Append("\":true");
      sb.Append('}');
      Post(sb.ToString());
    }

    string LastJson() {
      if (string.IsNullOrEmpty(cfg.LastAt)) return "null";
      var sb = new StringBuilder("{\"at\":").Append(S(cfg.LastAt));
      sb.Append(",\"heroes\":").Append(N(cfg.LastHeroes)).Append(",\"items\":").Append(N(cfg.LastItems)).Append(",\"artifacts\":").Append(N(cfg.LastArtifacts));
      sb.Append(",\"top\":[");
      for (int i = 0; i < cfg.LastTop.Count; i++) { if (i > 0) sb.Append(','); sb.Append(N(cfg.LastTop[i])); }
      return sb.Append("]}").ToString();
    }

    // ------------------------------------------------------------------ game status

    void CheckGame(bool force) {
      int pid = 0;
      try { pid = GameInfo.FindGameProcess(); } catch (Exception) { }
      if (pid != gamePid) {
        gamePid = pid;
        gameVersion = pid != 0 ? GameInfo.TryReadVersion(GameInfo.TryGetExePath(pid)) : null;
      }
      bool running = pid != 0;
      if (!force && gameSent && running == gameRunning) return;
      gameSent = true; gameRunning = running;
      Post("{\"ev\":\"game\",\"running\":" + B(running) + ",\"version\":" + S(gameVersion) + "}");
      if (!running && addrs != null) { addrs = null; overlay.SetAddrs(null); liveTimer.Stop(); lastLive = null; }
    }

    // ------------------------------------------------------------------ sync

    void SyncEv(string stage, string extra) { Post("{\"ev\":\"sync\",\"stage\":\"" + stage + "\"" + (extra ?? "") + "}"); }

    void StartSync(bool saveOnly) {
      if (syncing) return;
      bool upload = !saveOnly && SyncClient.IsValidCode(cfg.Code);
      string code = cfg.Code, site = cfg.Site; bool copy = cfg.SaveCopy || !upload;
      syncing = true;
      var t = new Thread(() => {
        try { RunSync(upload, copy, site, code); }
        catch (Exception e) { Log.Write("sync: " + e); SyncError("read", e.GetType().Name + ": " + e.Message, 0); }
        finally { syncing = false; }
      });
      t.IsBackground = true;
      t.Start();
    }

    void SyncError(string kind, string detail, int retryAfter) {
      SyncEv("error", ",\"error\":{\"kind\":\"" + kind + "\",\"detail\":" + S(detail) + ",\"retryAfter\":" + N(retryAfter) + "}");
    }

    void RunSync(bool upload, bool copy, string site, string code) {
      SyncEv("find", null);
      string version;
      int pid = Extractor.FindGame(out version);
      if (pid == 0) { SyncError("not_running", null, 0); return; }
      var started = DateTime.UtcNow;
      SyncEv("read", ",\"seconds\":0");
      var ticker = new System.Threading.Timer(_ => SyncEv("read", ",\"seconds\":" + N((long)(DateTime.UtcNow - started).TotalSeconds)), null, 1000, 1000);
      ExtractResult ex;
      try { ex = Extractor.Read(version, null); }
      finally { ticker.Dispose(); }
      Log.Write("read: " + ex.Error + " " + (ex.Detail ?? "") + "\n" + RFX.Log);
      switch (ex.Error) {
        case ExtractError.None: break;
        case ExtractError.GameNotRunning: SyncError("not_running", null, 0); return;
        case ExtractError.AccessDenied: SyncError("access", null, 0); return;
        default: SyncError("read", ex.Detail, 0); return;
      }
      int seconds = (int)(DateTime.UtcNow - started).TotalSeconds;
      string saved = null;
      if (copy) { try { saved = Extractor.SaveCopy(ex.Json, RFX.Log.ToString()); lastSavedPath = saved; } catch (Exception e) { Log.Write("save: " + e.Message); } }
      int heroes = ex.Heroes, items = ex.Items, arts = ex.Artifacts;
      string viewUrl = null;
      if (upload) {
        SyncEv("send", null);
        var r = SyncClient.Send(site, code, ex.Json);
        Log.Write("send: " + r.Status + " " + r.HttpCode + " " + (r.Details ?? ""));
        switch (r.Status) {
          case SyncStatus.Ok: break;
          case SyncStatus.InvalidToken: SyncError("token", null, 0); return;
          case SyncStatus.RateLimited: SyncError("rate", null, r.RetryAfterSeconds > 0 ? r.RetryAfterSeconds : 30); return;
          case SyncStatus.InvalidPayload: case SyncStatus.TooLarge: SyncError("payload", r.Details ?? r.Status.ToString(), 0); return;
          case SyncStatus.ServerError: case SyncStatus.Unexpected: case SyncStatus.Redirect: SyncError("server", "HTTP " + r.HttpCode, 0); return;
          default: SyncError("net", r.Details, 0); return;
        }
        if (r.Heroes >= 0) heroes = r.Heroes;
        if (r.Items >= 0) items = r.Items;
        if (r.Artifacts >= 0) arts = r.Artifacts;
        viewUrl = r.ViewUrl ?? site + "/app/heroes";
      }
      var top = TopHeroes(ex.Json);
      win.BeginInvoke((Action)(() => {
        cfg.LastAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        cfg.LastHeroes = heroes; cfg.LastItems = items; cfg.LastArtifacts = arts;
        if (top.Count > 0) cfg.LastTop = top;
        Save();
        SyncEv("done", ",\"result\":{\"seconds\":" + N(seconds) + ",\"heroes\":" + N(heroes) + ",\"items\":" + N(items) + ",\"artifacts\":" + N(arts)
          + ",\"viewUrl\":" + S(viewUrl) + ",\"saved\":" + S(upload ? null : saved) + "},\"last\":" + LastJson());
      }));
    }

    // Base ids of the three strongest heroes of the dump (by the game's own power value).
    static List<int> TopHeroes(string json) {
      var res = new List<int>();
      try {
        var root = MiniJson.AsObject(MiniJson.Parse(json)); object hv;
        var heroes = root != null && root.TryGetValue("heroes", out hv) ? hv as List<object> : null;
        if (heroes == null) return res;
        var list = new List<KeyValuePair<double, int>>();
        foreach (var h in heroes) {
          var d = MiniJson.AsObject(h); if (d == null) continue;
          object p, b;
          if (d.TryGetValue("iPower", out p) && d.TryGetValue("iBaseId", out b) && p is double && b is double)
            list.Add(new KeyValuePair<double, int>((double)p, (int)(double)b));
        }
        list.Sort((a, c) => c.Key.CompareTo(a.Key));
        foreach (var kv in list) { if (!res.Contains(kv.Value)) res.Add(kv.Value); if (res.Count == 3) break; }
      } catch (Exception) { }
      return res;
    }

    // ------------------------------------------------------------------ equip helper

    void LoadPlans(string lang) {
      string site = cfg.Site, code = cfg.Code;
      if (!SyncClient.IsValidCode(code)) { Post("{\"ev\":\"plans\",\"status\":\"invalid_token\"}"); return; }
      Task.Factory.StartNew(() => {
        var r = PlansClient.List(site, code, lang == "en" ? "en" : "ru");
        if (r.Status != PlansStatus.Ok) {
          Post("{\"ev\":\"plans\",\"status\":" + S(r.Status == PlansStatus.InvalidToken ? "invalid_token" : "error") + ",\"detail\":" + S(r.Details ?? ("HTTP " + r.HttpCode)) + "}");
          return;
        }
        var sb = new StringBuilder("{\"ev\":\"plans\",\"status\":\"ok\",\"plans\":[");
        for (int i = 0; i < r.Plans.Count; i++) {
          var p = r.Plans[i]; if (i > 0) sb.Append(',');
          sb.Append("{\"id\":").Append(S(p.Id)).Append(",\"heroUid\":").Append(N(p.HeroUid)).Append(",\"heroName\":").Append(S(p.HeroName))
            .Append(",\"createdAt\":").Append(S(p.CreatedAt)).Append(",\"items\":[");
          for (int j = 0; j < p.Items.Count; j++) {
            var it = p.Items[j]; if (j > 0) sb.Append(',');
            sb.Append("{\"slot\":").Append(N(it.Slot)).Append(",\"uid\":").Append(N(it.Uid)).Append(",\"slotName\":").Append(S(it.SlotName))
              .Append(",\"name\":").Append(S(it.Name)).Append(",\"setName\":").Append(S(it.SetName)).Append(",\"level\":").Append(N(it.Level))
              .Append(",\"stars\":").Append(N(it.Stars)).Append(",\"mainStat\":").Append(S(it.MainStat)).Append(",\"fromHeroUid\":").Append(N(it.FromHeroUid))
              .Append(",\"fromHeroName\":").Append(S(it.FromHeroName)).Append(",\"icon\":").Append(S(it.Icon)).Append(",\"setIcon\":").Append(S(it.SetIcon))
              .Append(",\"subs\":").Append(LinesJson(it.Subs, "rolls")).Append(",\"setBonus\":").Append(LinesJson(it.SetBonus, "pieces"));
            if (it.CurKnown && it.Cur == null) sb.Append(",\"cur\":null");
            else if (it.CurKnown) sb.Append(",\"cur\":{\"uid\":").Append(N(it.Cur.Uid)).Append(",\"name\":").Append(S(it.Cur.Name)).Append(",\"level\":").Append(N(it.Cur.Level))
                   .Append(",\"stars\":").Append(N(it.Cur.Stars)).Append(",\"icon\":").Append(S(it.Cur.Icon))
                   .Append(",\"mainStat\":").Append(S(it.Cur.MainStat)).Append(",\"subs\":").Append(LinesJson(it.Cur.Subs, "rolls")).Append('}');
            sb.Append('}');
          }
          sb.Append("]}");
        }
        Post(sb.Append("]}").ToString());
      });
    }

    static string LinesJson(List<SubStat> l, string numKey) {
      var sb = new StringBuilder("[");
      for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append("{\"text\":").Append(S(l[i].Text)).Append(",\"").Append(numKey).Append("\":").Append(N(l[i].Rolls)).Append('}'); }
      return sb.Append(']').ToString();
    }

    void StartScan(List<long> uids) {
      if (scanning || uids.Count == 0) return;
      scanning = true; watch = uids; liveTimer.Stop(); lastLive = null;
      Task.Factory.StartNew(() => {
        EquipAddrs a = null;
        try { lock (Extractor.Gate) a = RFX.FindEquip(uids, null); }
        catch (Exception e) { Log.Write("equip scan: " + e); }
        win.BeginInvoke((Action)(() => {
          scanning = false; addrs = a; overlay.SetAddrs(a);
          Log.Write("equip scan: " + (a == null ? "failed " + RFX.LastError : "panel=" + (a.Panel != 0) + " items=" + a.Items.Count));
          Post("{\"ev\":\"equip.scan\",\"status\":" + S(a == null ? "fail" : "ok") + ",\"panel\":" + B(a != null && a.Panel != 0) + "}");
          if (a != null) { liveTimer.Start(); PollLive(); }
        }));
      });
    }

    void Watch(List<long> uids) {
      if (addrs == null) { StartScan(uids); return; }
      // new plan items are found through EquipData.equips on the next poll; a full scan only without it
      if (addrs.EquipData == 0) foreach (var u in uids) if (!addrs.Items.ContainsKey(u)) { StartScan(uids); return; }
      watch = uids;
    }

    void PollLive() {
      if (addrs == null || scanning) return;
      EquipLive s;
      try { s = RFX.Poll(addrs, watch); } catch (Exception) { return; }
      var sb = new StringBuilder("{\"ev\":\"equip.live\",\"live\":{");
      sb.Append("\"gameRunning\":").Append(B(s.GameRunning)).Append(",\"heroUid\":").Append(N(s.HeroUid)).Append(",\"panelOk\":").Append(B(s.PanelOk))
        .Append(",\"part\":").Append(N(s.Part)).Append(",\"hideEquipped\":").Append(B(s.HideEquipped)).Append(",\"hideEnhanced\":").Append(B(s.HideEnhanced))
        .Append(",\"filterActive\":").Append(B(s.FilterActive)).Append(",\"selUid\":").Append(N(s.SelUid)).Append(",\"selRow\":").Append(N(s.SelRow))
        .Append(",\"selCol\":").Append(N(s.SelCol)).Append(",\"rows\":{");
      bool first = true;
      foreach (var kv in s.Row) {
        int col; s.Col.TryGetValue(kv.Key, out col);
        if (!first) sb.Append(','); first = false;
        sb.Append('"').Append(N(kv.Key)).Append("\":[").Append(N(kv.Value)).Append(',').Append(N(col)).Append(']');
      }
      sb.Append("},\"owner\":{"); first = true;
      foreach (var kv in s.Owner) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(N(kv.Key)).Append("\":").Append(N(kv.Value)); }
      sb.Append("}}}");
      string json = sb.ToString();
      if (json == lastLive) return;   // nothing changed on the game screen
      lastLive = json;
      Post(json);
      if (!s.GameRunning) { liveTimer.Stop(); addrs = null; overlay.SetAddrs(null); }
    }

    void FinishPlan(string id, bool done) {
      string site = cfg.Site, code = cfg.Code;
      if (string.IsNullOrEmpty(id) || !SyncClient.IsValidCode(code)) return;
      Task.Factory.StartNew(() => {
        var r = PlansClient.Finish(site, code, id, done);
        Log.Write("plan " + id + " " + (done ? "done" : "cancelled") + ": " + r.Status);
      });
    }
  }
}
