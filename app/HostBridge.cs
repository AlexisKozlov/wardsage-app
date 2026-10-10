// RealmForge.exe - messages between the page (ui/app.js) and the program.
//
// page -> host  {cmd:"init"|"setLang"|"setCode"|"clearCode"|"setSaveCopy"|"setErrorReports"|"setSite"|"paste"|"sync"|"open"|"openLog"|
//                    "openFolder"|"plans.load"|"equip.scan"|"equip.watch"|"equip.finish"|"highlight"|"compact", ...}
// host -> page  {ev:"state"|"game"|"paste"|"sync"|"plans"|"equip.scan"|"equip.live"|"overlay"|"bridge.equip"|"bridge.drop", ...}
// The local bridge (bridge/, optional): every reading of the game also goes to it, and its equip commands become
// plans on the page (id "bridge:<command id>") that the same guide walks through; equip.finish answers the bridge.
// The page is trusted content from our own folder, but every argument is still validated here: codes by format,
// URLs by scheme and host, item uids as numbers. Nothing here writes to the game.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    DateTime rescanAfter = DateTime.MinValue;
    EquipAddrs addrs;
    List<long> watch = new List<long>();
    string lastLive;
    string lastSavedPath;
    readonly OverlayController overlay;
    // automatic sync (Settings → «Автосинхронизация»): the site learns about gear changes without a button press
    readonly System.Windows.Forms.Timer autoTimer = new System.Windows.Forms.Timer();
    DateTime lastSyncStart = DateTime.MinValue, autoDue = DateTime.MaxValue;
    Dictionary<long, long> lastOwners;
    DateTime gameSeenAt = DateTime.MinValue;   // when the game process was first seen (this run / since it last started)
    bool serverRetried;                        // the one quick retry after a 5xx is used up until a sync succeeds
    const int AutoEveryMin = 5, SyncGapSec = 31, FirstSyncDelaySec = 60, NotReadyRetrySec = 45, ServerRetrySec = 60;   // the site takes one sync per code per 30 s
    // automatic updates (app/Updater.cs): first check shortly after start, then every 6 hours
    readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
    string updateReady;   // JSON of the downloaded update for the page, or null
    // local bridge (src/BridgeClient.cs): the poller has its own client (aborted on exit); this one sends snapshots and answers
    const string BridgePlanPrefix = "bridge:";
    readonly BridgeClient bridgeApi = new BridgeClient(BridgeClient.DefaultUrl, BridgeClient.DefaultTokenPath);
    readonly BridgePoller bridgePoller;
    readonly Dictionary<string, BridgeCommand> bridgeOpen = new Dictionary<string, BridgeCommand>();   // UI thread only

    public HostBridge(AppWindow win, CoreWebView2 core) {
      this.win = win; this.core = core;
      cfg = AppConfig.Load();
      SyncClient.App = Program.Version;
      TakeInstallLang();
      if (string.IsNullOrEmpty(cfg.Site)) cfg.Site = SyncClient.DefaultSite;
      arenaWatch = new ArenaWatch(() => cfg.Site, () => cfg.Code);
      gameTimer.Interval = 2000; gameTimer.Tick += (s, e) => { CheckGame(false); WatchBattleEnd(); WatchStall(); arenaWatch.Tick(gameRunning); TraceStartTick(); TryAutoUpdate(); }; gameTimer.Start();
      liveTimer.Interval = 400; liveTimer.Tick += (s, e) => PollLive();
      overlay = new OverlayController(st => Post("{\"ev\":\"overlay\",\"state\":" + S(st) + "}"),
                                      st => { Post("{\"ev\":\"auto\",\"state\":" + S(st) + "}"); OnAutoState(st); },
                                      (p, st) => Post("{\"ev\":\"sell.state\",\"state\":" + S(st) + ",\"selected\":" + p.Selected + ",\"missing\":" + p.Missing + ",\"extra\":" + p.Extra + "}"));
      overlay.AutoEnabled = cfg.AutoClick;
      overlay.AutoConfirm = cfg.AutoConfirm;
      overlay.CancelText = CancelText(cfg.Lang);
      coach.Lang = cfg.Lang;
      // «Отменить надевание» over the game: the interface drops the running build (as its own «Отменить»)
      overlay.CancelPressed += () => { Log.Write("cancel pressed over the game"); Post("{\"ev\":\"cancelRun\"}"); };
      overlay.Problem += (kind, message) => Report(kind, message, null);
      Program.FatalReport = ReportFatal;
      autoTimer.Interval = 1000; autoTimer.Tick += (s, e) => AutoSyncTick(); autoTimer.Start();
      updateTimer.Interval = 20000; updateTimer.Tick += (s, e) => { updateTimer.Interval = 5 * 60 * 1000; if (updateReady == null) CheckUpdate(); }; updateTimer.Start();
      bridgePoller = new BridgePoller(new BridgeClient(BridgeClient.DefaultUrl, BridgeClient.DefaultTokenPath),
                                      c => OnUi(() => OnBridgeCommand(c)), up => OnUi(() => OnBridgeConnected(up)));
      bridgePoller.Start();
      Log.Write(Channel.ProductName + " " + Program.Version + " started");
    }

    static string CancelText(string lang) { return lang == "en" ? "Cancel equipping" : "Отменить надевание"; }

    public void Dispose() {
      Program.FatalReport = null;
      bridgePoller.Dispose();   // the pending long poll is aborted: the thread ends at once
      CloseBridgeCommands("cancelled", "Wardsage was closed.", true);
      gameTimer.Dispose(); liveTimer.Dispose(); autoTimer.Dispose(); updateTimer.Dispose(); overlay.Dispose();
    }

    // «Обновлять автоматически» (on by default): a downloaded update is put in place by restarting in a quiet moment — no
    // sync, scan or fight capture running, no fight going on, the equip / sell pilot idle, no open site command, the
    // window untouched for 2 minutes
    DateTime lastUi = DateTime.UtcNow;
    bool restarting;
    void TryAutoUpdate() {
      if (updateReady == null || restarting || !cfg.AutoUpdate) return;
      if (syncing || scanning || capturing || bridgeOpen.Count > 0 || overlay.Busy) return;
      var rec = recorder;
      if (rec != null && rec.Running) return;
      if ((DateTime.UtcNow - lastUi).TotalMinutes < 2) return;
      restarting = true;
      Log.Write("update: restarting by itself in a quiet moment");
      win.SaveForRestart();
      Program.RestartForUpdate();
    }

    void CheckUpdate() {
      string site = cfg.Site;
      Task.Factory.StartNew(() => {
        var u = Updater.CheckAndDownload(site);
        if (u == null) return;
        updateReady = "{\"ev\":\"update\",\"version\":" + S(u.Version) + ",\"notesRu\":" + S(u.NotesRu) + ",\"notesEn\":" + S(u.NotesEn) + "}";   // the page shows the one of its language
        Post(updateReady);
      });
    }

    // ------------------------------------------------------------------ plumbing

    void Post(string json) {
      if (win.IsDisposed) return;
      if (win.InvokeRequired) { try { win.BeginInvoke((Action)(() => Post(json))); } catch (Exception) { } return; }
      try { core.PostWebMessageAsJson(json); } catch (Exception) { }
    }

    void OnUi(Action a) {
      if (win.IsDisposed) return;
      try { win.BeginInvoke(a); } catch (Exception) { }   // the window is closing
    }

    static string S(string v) { return v == null ? "null" : MiniJson.Quote(v); }
    static string B(bool v) { return v ? "true" : "false"; }
    static string N(long v) { return v.ToString(CultureInfo.InvariantCulture); }

    public void OnMessage(string json) {
      var m = MiniJson.AsObject(MiniJson.TryParse(json));
      if (m == null) return;
      string cmd = MiniJson.GetString(m, "cmd");
      if (cmd != "init" && cmd != "log") lastUi = DateTime.UtcNow;
      try {
        switch (cmd) {
          case "init": SendState(null); CheckGame(true); if (updateReady != null) Post(updateReady); break;
          case "setLang": cfg.Lang = MiniJson.GetString(m, "lang") == "en" ? "en" : "ru"; overlay.CancelText = CancelText(cfg.Lang); coach.Lang = cfg.Lang; Save(); break;
          case "setCode": {
            string code = SyncClient.ExtractCode(MiniJson.GetString(m, "code") ?? "");
            if (SyncClient.IsValidCode(code)) { cfg.Code = code; Save(); SendState("codeSaved"); }
            break;
          }
          case "clearCode": cfg.Code = ""; Save(); SendState(null); break;
          case "setSaveCopy": cfg.SaveCopy = MiniJson.GetBool(m, "on", false); Save(); break;
          case "setAutoSync": cfg.AutoSync = MiniJson.GetBool(m, "on", true); Save(); if (cfg.AutoSync) RequestAutoSync(0); break;
          case "setAutoClick": cfg.AutoClick = MiniJson.GetBool(m, "on", true); overlay.AutoEnabled = cfg.AutoClick; Save(); break;
          case "setAutoConfirm": cfg.AutoConfirm = MiniJson.GetBool(m, "on", false); overlay.AutoConfirm = cfg.AutoConfirm; Save(); break;
          case "setAutoUpdate": cfg.AutoUpdate = MiniJson.GetBool(m, "on", true); Save(); break;
          case "setTray": cfg.Tray = MiniJson.GetBool(m, "on", true); win.TrayEnabled = cfg.Tray; Save(); break;
          case "setAutostart": Autostart.Set(MiniJson.GetBool(m, "on", false)); SendState(null); break;
          case "setErrorReports": cfg.ErrorReports = MiniJson.GetBool(m, "on", false); Save(); Log.Write("error reports " + (cfg.ErrorReports ? "on" : "off")); break;
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
          case "sync": StartSync(MiniJson.GetBool(m, "saveOnly", false), false); break;
          case "open": OpenAllowed(MiniJson.GetString(m, "url")); break;
          case "openLog": Shell.OpenFile(Log.Path); break;
          case "openFolder": Shell.OpenFolder(lastSavedPath ?? Extractor.OutputDir); break;
          case "plans.load": LoadPlans(MiniJson.GetString(m, "lang")); break;
          case "sell.load": LoadSell(MiniJson.GetString(m, "lang")); break;
          case "sell.run": {
            // storage cleanup: select the list's items on the game's sell screen (from the gear list of the hero the game
            // showed last, else of any hero); the game comes forward as for «Надеть»
            var uids = Uids(m);
            var parts = new List<int>(); object pv;
            var pl = m.TryGetValue("slots", out pv) ? pv as List<object> : null;
            if (pl != null) foreach (var x in pl) if (x is double && (double)x >= 0 && (double)x <= 4 && !parts.Contains((int)(double)x)) parts.Add((int)(double)x);
            // upgrade levels, same order as the uids: below +16 the game opens «Быстрое улучшение» over the list
            var levels = new Dictionary<long, int>(); object lv;
            var ll = m.TryGetValue("levels", out lv) ? lv as List<object> : null;
            if (ll != null) for (int i = 0; i < ll.Count && i < uids.Count; i++) if (ll[i] is double) levels[uids[i]] = (int)Math.Max(0, Math.Min(99, (double)ll[i]));
            long hero = addrs != null ? RFX.ReadHero(addrs) : 0; if (hero <= 0) hero = RFX.AnyHeroUid();
            overlay.SetSell(uids, parts, levels.Count == uids.Count ? levels : null, hero);
            Log.Write("sell: run " + uids.Count + " items, hero " + hero);
            if (!cfg.AutoClick) Log.Write("sell: waiting - auto click is off");
            if (addrs == null && !scanning && cfg.AutoClick) StartScan(watch, true);   // the pilot needs the game's screens found
            break;
          }
          case "sell.stop": overlay.SetSell(null, null, null, 0); break;
          case "sell.finish": FinishSell(MiniJson.GetString(m, "id"), MiniJson.GetBool(m, "done", false)); break;
          case "equip.scan": if (scanning) watch = Uids(m); else StartScan(Uids(m)); break;   // (the scan ahead is running: its result serves these items too)
          case "equip.watch": Watch(Uids(m)); break;
          case "equip.run": {
            // the plan the player started («Надеть»): its hero is brought up on the hero screen
            object v; double h = m.TryGetValue("hero", out v) && v is double ? (double)v : 0;
            overlay.SetRun(h > 0 && h < 9e15 ? (long)h : 0);
            if (MiniJson.GetBool(m, "restart", false)) overlay.RestartPilots();
            break;
          }
          case "log": Log.Write("ui: " + (MiniJson.GetString(m, "text") ?? "")); break;
          case "equip.rescan":
            // the page's plan stands still: the game's tables are read again (at most every 20 s)
            if (addrs != null && !scanning && DateTime.UtcNow > rescanAfter) {
              rescanAfter = DateTime.UtcNow.AddSeconds(20); Log.Write("equip scan again: the plan stands still");
              StartScan(watch, false, true); overlay.RestartPilots();
            }
            break;
          case "equip.finish": FinishPlan(MiniJson.GetString(m, "id"), MiniJson.GetBool(m, "done", false), MiniJson.GetBool(m, "taken", false)); break;
          case "highlight": {
            object v; double u = m.TryGetValue("uid", out v) && v is double ? (double)v : 0;
            overlay.SetTarget(u > 0 && u < 9e15 && watch.Contains((long)u) ? (long)u : 0);
            // hint on the game's own buttons: filter / «Заменить» / next slot, with up to two short lines
            string hint = MiniJson.GetString(m, "hint") ?? "";
            if (hint != "filter" && hint != "replace" && hint != "slot") hint = "";
            double slot = m.TryGetValue("slot", out v) && v is double ? (double)v : -1;
            var lines = new List<string>();
            foreach (var k in new[] { "line1", "line2" }) {
              string l = MiniJson.GetString(m, k);
              if (!string.IsNullOrEmpty(l)) lines.Add(l.Length > 80 ? l.Substring(0, 80) : l);
            }
            double hero = m.TryGetValue("hero", out v) && v is double ? (double)v : 0;
            double item = m.TryGetValue("item", out v) && v is double ? (double)v : 0;
            string kind = MiniJson.GetString(m, "kind") ?? "";
            overlay.SetFilterGoal(item > 0 && item < 9e15 && watch.Contains((long)item) ? (long)item : 0, kind.Length <= 20 ? kind : "");
            overlay.SetHint(hint, slot >= 0 && slot <= 4 ? (int)slot : -1, lines.ToArray(), hero > 0 && hero < 9e15 ? (long)hero : 0);
            break;
          }
          case "diag": overlay.StartDiag(5); break;
          case "battle.capture": CaptureNow(-1, 0, 0); break;
          case "compact": win.SetCompact(MiniJson.GetBool(m, "on", false)); break;
          case "update.restart": Program.RestartForUpdate(); break;
        }
      } catch (Exception e) {
        Log.Write("message " + cmd + ": " + e);
        Report("unhandled", "message " + cmd + ": " + e.GetType().Name + ": " + e.Message, Ctx("exception", e.ToString()));
      }
    }

    void Save() { try { cfg.Save(); } catch (Exception e) { Log.Write("config save: " + e.Message); } }

    // ------------------------------------------------------------------ error reports
    // Settings → «Отправлять отчёты об ошибках» (OFF by default; src/ErrorReport.cs): failures of the pilots, the sync,
    // the live poll, the fight recording and unhandled exceptions go to the site with the journal's last 300 lines, so
    // the owner learns about them without the player describing them. Nothing is sent while the setting is off.
    readonly ErrorReportLimiter reportLimit = new ErrorReportLimiter(Path.Combine(AppConfig.DefaultDir, "reports-sent.txt"));

    static Dictionary<string, string> Ctx(string key, string value) { return new Dictionary<string, string> { { key, value } }; }

    /// <summary>On, a code to sign it with, and not the same kind in 10 minutes / not over 20 a day.</summary>
    bool ReportAllowed(string kind) {
      return cfg.ErrorReports && SyncClient.IsValidCode(cfg.Code) && reportLimit.TryTake(ErrorReports.NormalizeKind(kind), DateTime.UtcNow);
    }

    string ReportJson(string kind, string message, Dictionary<string, string> ctx) {
      return ErrorReports.BuildJson(kind, message, Program.Version, gameVersion, ctx, ErrorReports.ReadLogTail(Log.Path),
                                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Sends a report in the background (any thread).</summary>
    void Report(string kind, string message, Dictionary<string, string> ctx) {
      if (!ReportAllowed(kind)) return;
      string site = cfg.Site, code = cfg.Code;
      Task.Factory.StartNew(() => {
        var st = ErrorReports.Send(site, code, ReportJson(kind, message, ctx), ErrorReports.TimeoutMs);
        Log.Write("error report " + ErrorReports.NormalizeKind(kind) + ": " + st);
      });
    }

    /// <summary>An unhandled exception: sent right away with a short wait (the program may be about to end).</summary>
    void ReportFatal(Exception e) {
      if (!ReportAllowed("unhandled")) return;
      var st = ErrorReports.Send(cfg.Site, cfg.Code, ReportJson("unhandled", e.GetType().Name + ": " + e.Message, Ctx("exception", e.ToString())), 5000);
      Log.Write("error report unhandled: " + st);
    }

    /// <summary>The equip pilot's state (Overlay AutoSay, on every change): its failures are reported.</summary>
    void OnAutoState(string st) {
      if (st == "failed" || st == "slot_failed" || st == "unconfirmed" || st == "hero_failed")
        Report("pilot." + st, "auto: " + st, Ctx("state", st));
    }

    static List<long> Uids(Dictionary<string, object> m) {
      var r = new List<long>(); object v;
      var list = m.TryGetValue("uids", out v) ? v as List<object> : null;
      if (list != null) foreach (var x in list) if (x is double && (double)x > 0 && (double)x < 9e15 && r.Count < SellClient.MaxItems) r.Add((long)(double)x);
      return r;
    }

    // Only the site, the project page on GitHub and Microsoft's WebView2 page are ever opened.
    void OpenAllowed(string url) {
      Uri u, site;
      if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) return;
      bool ok = u.Host == "github.com" || u.Host == "go.microsoft.com" || u.Host == "discord.gg" || u.Host == "discord.com";
      if (Uri.TryCreate(cfg.Site, UriKind.Absolute, out site) && u.Host == site.Host) ok = true;
      if (ok) Shell.OpenUrl(u.AbsoluteUri);
    }

    void SendState(string flag) {
      var sb = new StringBuilder("{\"ev\":\"state\"");
      sb.Append(",\"lang\":").Append(S(cfg.Lang == "en" ? "en" : "ru"));
      sb.Append(",\"version\":").Append(S(Program.Version));
      sb.Append(",\"channel\":").Append(S(Channel.IsTest ? "test" : "main"));
      sb.Append(",\"site\":").Append(S(cfg.Site));
      sb.Append(",\"defaultSite\":").Append(S(SyncClient.DefaultSite));
      bool has = SyncClient.IsValidCode(cfg.Code);
      sb.Append(",\"hasCode\":").Append(B(has));
      sb.Append(",\"codePrefix\":").Append(S(has ? cfg.Code.Substring(0, 8) : ""));
      sb.Append(",\"saveCopy\":").Append(B(cfg.SaveCopy));
      sb.Append(",\"autoSync\":").Append(B(cfg.AutoSync)).Append(",\"autoClick\":").Append(B(cfg.AutoClick))
        .Append(",\"autoConfirm\":").Append(B(cfg.AutoConfirm)).Append(",\"errorReports\":").Append(B(cfg.ErrorReports)).Append(",\"autoUpdate\":").Append(B(cfg.AutoUpdate))
        .Append(",\"tray\":").Append(B(cfg.Tray)).Append(",\"autostart\":").Append(B(Autostart.IsOn()));
      win.TrayEnabled = cfg.Tray;
      win.SetTrayLang(cfg.Lang);
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

    // The language picked in the installer (installer/RealmForge.iss leaves install-lang.txt next to the exe): taken once.
    void TakeInstallLang() {
      try {
        string f = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "install-lang.txt");
        if (!System.IO.File.Exists(f)) return;
        string l = System.IO.File.ReadAllText(f).Trim();
        System.IO.File.Delete(f);
        if (l == "ru" || l == "en") { cfg.Lang = l; cfg.Save(); }
      } catch (Exception) { }
    }

    // ------------------------------------------------------------------ game status

    void CheckGame(bool force) {
      int pid = 0;
      try { pid = GameInfo.FindGameProcess(); } catch (Exception) { }
      if (pid != gamePid) {
        gamePid = pid;
        gameVersion = pid != 0 ? GameInfo.TryReadVersion(GameInfo.TryGetExePath(pid)) : null;
        Log.Write(pid != 0 ? "game found: #" + pid + " " + (GameInfo.TryGetExePath(pid) ?? "?") + ", version " + (gameVersion ?? "?") : "game closed");
      }
      bool running = pid != 0;
      if (!force && gameSent && running == gameRunning) return;
      gameSent = true; gameRunning = running;
      Post("{\"ev\":\"game\",\"running\":" + B(running) + ",\"version\":" + S(gameVersion) + "}");
      if (!running && addrs != null) { addrs = null; overlay.SetAddrs(null); liveTimer.Stop(); lastLive = null; }
      if (!running) CloseBridgeCommands("failed", "The game was closed.", false);
      // the game's screens are found in memory at once (tens of seconds), not when the first «Надеть» comes: then the
      // equip starts right away
      if (running && addrs == null && !scanning && cfg.AutoClick) StartScan(watch, true);
    }

    // ------------------------------------------------------------------ boss fights

    // A boss fight's result screen came up: its statistics are read once (src/BattleCapture.cs, read-only), kept and
    // sent with the next sync. UIInstance is looked for (off this thread) at most every 60 s until found. The game keeps
    // closed result screens, so a new fight is a result screen whose mark (table, instance, frames) changed; the first
    // look after the start is only the baseline.
    bool capturing, uiFinding, uiLogged;
    Dictionary<int, string> endMarks;
    // the boss coach over the game (app/BattleCoach.cs): a new running simulation = a fight starts
    readonly BattleCoach coach = new BattleCoach();
    // the arena's opponents to the site while the arena screen is open (app/ArenaWatch.cs); an arena fight's monster level
    // and opponent, read at its start (src/ArenaOpponents.cs) and kept with the fight as "arenaInfo"
    ArenaWatch arenaWatch;
    volatile string arenaInfo; volatile int arenaInfoStage;
    FightRecorder arenaTaken;
    void WatchBattleEnd() {
      if (!gameRunning || capturing) return;
      WatchBattleStart();
      if (!RFX.UiKnown) {
        if (!uiFinding) {
          uiFinding = true;
          Task.Factory.StartNew(() => {
            try { RFX.FindUi(60000); if (RFX.UiKnown && !uiLogged) { uiLogged = true; Log.Write("battle watch: game windows found at " + RFX.UiAddress.ToString("X")); } }
            catch (Exception e) { Log.Write("battle watch: " + e.Message); }
            finally { uiFinding = false; }
          });
        }
        return;
      }
      List<KeyValuePair<int, ulong>> forms; List<string> marks;
      try { forms = RFX.BattleEndForms(out marks); } catch (Exception) { return; }
      bool first = endMarks == null;
      if (first) { endMarks = new Dictionary<int, string>(); Log.Write("battle watch: baseline " + string.Join(" ", marks.ToArray())); }
      int kind = -1; ulong form = 0;
      for (int i = 0; i < forms.Count; i++) {
        string was; int k = forms[i].Key;
        bool changed = !endMarks.TryGetValue(k, out was) || was != marks[i];
        endMarks[k] = marks[i];
        if (changed && !first) { kind = k; form = forms[i].Value; }
      }
      if (kind < 0) {
        // an arena fight: its result screen is reused and has no fight frame, so its mark never changes — the recorder saw
        // the fight end (the simulation's state 2): a few seconds later (the screen's numbers filled) it is captured
        var rec = recorder;
        if (rec == null || rec == arenaTaken || rec.Running || rec.Stage / 1000 != 6001 && rec.Stage / 1000 != 6002) return;
        double after = (DateTime.UtcNow - rec.EndedUtc).TotalSeconds;
        if (after < 4 || after > 300) return;
        int pi = forms.FindIndex(x => x.Key == 3 || x.Key == 4);
        kind = pi >= 0 ? forms[pi].Key : -1; form = pi >= 0 ? forms[pi].Value : 0;
        Log.Write("battle end (arena, by the recorder): stage " + rec.Stage + " " + string.Join(" ", marks.ToArray()));
        arenaTaken = rec;   // once per fight, also when no result screen is held (kind −1 keeps the recorder)
        CaptureNow(kind, form, 0);
        return;
      }
      Log.Write("battle end: " + kind + " " + string.Join(" ", marks.ToArray()));
      CaptureNow(kind, form, 1500);
    }

    // a new fight: its stage; the recorder follows it, and a known boss -> the coach follows its clock
    // the stage of the fight that started last: the recorded fight names it (the site knows the boss from it)
    volatile int lastStage; DateTime lastStageAt;

    // The battle view's simulation (BattleManager.instance.m_simulation, four pointer reads): a fight starts when it is
    // running and is another object than the last one seen, or its clock went back (the game reused it).
    ulong simSeen; uint simFrames; bool simLogged;
    void WatchBattleStart() {
      ulong sim; uint fr = 0; int st = 0;
      try { sim = RFX.CurrentSim(); if (sim == 0 || !RFX.SimClock(sim, out fr, out st)) sim = 0; } catch (Exception) { return; }
      if (sim == 0) { st = 0; fr = 0; }
      // once: the battle view found (or still not, when the game's windows already are)
      if (!simLogged && (sim != 0 || RFX.UiKnown)) { simLogged = true; Log.Write("battle watch: battle view " + (sim != 0 ? sim.ToString("X") + " state " + st + " frames " + fr : "not found yet")); }
      if (sim == 0) return;
      bool fresh = st == 1 && (sim != simSeen || fr + 15 < simFrames);
      bool wasSeen = simSeen != 0;
      simSeen = sim; simFrames = fr;
      if (!fresh) return;
      int stage = RFX.SimStage(sim);
      Log.Write("battle start: stage " + stage + " at " + RFX.FrameSeconds(fr).ToString("0") + " s" + (coach.Knows(stage) ? " (coach)" : "") + (wasSeen ? "" : " (first look)"));
      lastStage = stage; lastStageAt = DateTime.UtcNow;
      StartRecorder(sim, stage);
      if (stage / 1000 == 6001) {
        ShootField(stage);
        arenaInfo = null; arenaInfoStage = stage;
        Task.Factory.StartNew(() => { try { var ai = RFX.ArenaFightJson(sim); arenaInfo = ai; Log.Write("arena fight: " + (ai == null ? "not read" : ai.Length > 400 ? ai.Substring(0, 400) + "…" : ai)); } catch (Exception ex) { Log.Write("arena fight: " + ex.Message); } });
      }
      if (coach.Running) coach.Stop();
      // the player's plan for this boss (the site's «Отправить план в игру»): fresh from the site when the copy is over a
      // minute old, then the coach (off the UI thread: the site may take a moment)
      string site = cfg.Site, code = cfg.Code;
      Task.Factory.StartNew(() => {
        FightPlan plan = null;
        try {
          if (SyncClient.IsValidCode(code) && (DateTime.UtcNow - fightPlansAt).TotalSeconds > 60) LoadFightPlans(site, code);
          var heroes = new HashSet<long>();
          foreach (var k in RFX.BattleHeroes(sim).Keys) heroes.Add(k);
          plan = FightPlanClient.For(fightPlans, stage, heroes);
          if (plan != null) Log.Write("battle start: plan for " + plan.Boss + ", " + plan.Steps.Count + " heroes (" + heroes.Count + " in the fight)");
        } catch (Exception e) { Log.Write("fight plans: " + e.Message); }
        win.BeginInvoke((Action)(() => { if (simSeen == sim && (plan != null || coach.Knows(stage))) coach.Start(sim, stage, plan); }));
      });
    }

    /// <summary>An arena fight started: 3 s later, with the game in front, a picture of the game window (the field in
    /// the game's own camera) is kept in battles/shots/ — the site draws its arena plan over it. A screen capture of the
    /// game's window only (what the player sees); nothing is read from or written to the game. The last 30 are kept.</summary>
    void ShootField(int stage) {
      var t = new System.Windows.Forms.Timer { Interval = 3000 };
      t.Tick += (s, e) => {
        t.Stop(); t.Dispose();
        try {
          var ps = GameInfo.GameProcesses();
          IntPtr hwnd = ps.Length > 0 ? ps[0].MainWindowHandle : IntPtr.Zero;
          int fp = 0, gp = 0;
          if (hwnd != IntPtr.Zero) { W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp); W32.GetWindowThreadProcessId(hwnd, out gp); }
          W32.RECT cr;
          if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || fp != gp || !W32.GetClientRect(hwnd, out cr) || cr.R < 400 || cr.B < 300) { Log.Write("arena shot: the game is not in front"); return; }
          var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
          string dir = System.IO.Path.Combine(RFX.BattlesDir, "shots");
          System.IO.Directory.CreateDirectory(dir);
          string path = System.IO.Path.Combine(dir, stage + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
          using (var bmp = new System.Drawing.Bitmap(cr.R, cr.B))
          using (var g = System.Drawing.Graphics.FromImage(bmp)) {
            g.CopyFromScreen(o.X, o.Y, 0, 0, new System.Drawing.Size(cr.R, cr.B));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
          }
          var files = System.IO.Directory.GetFiles(dir, "*.png"); Array.Sort(files);
          for (int i = 0; i < files.Length - 30; i++) try { System.IO.File.Delete(files[i]); } catch (Exception) { }
          Log.Write("arena shot: " + path + " (" + cr.R + "x" + cr.B + ")");
        } catch (Exception ex) { Log.Write("arena shot: " + ex.Message); }
      };
      t.Start();
    }

    // the account's fight plans (src/FightPlanClient.cs), fetched at a fight's start (at most once a minute)
    volatile List<FightPlan> fightPlans = new List<FightPlan>();
    DateTime fightPlansAt = DateTime.MinValue;

    void LoadFightPlans(string site, string code) {
      var r = FightPlanClient.Get(site, code);
      if (r.Status == PlansStatus.Ok) { fightPlans = r.Plans; fightPlansAt = DateTime.UtcNow; Log.Write("fight plans: " + r.Plans.Count); }
      else if (r.Status == PlansStatus.InvalidToken) { fightPlans = new List<FightPlan>(); fightPlansAt = DateTime.UtcNow; }
      else Log.Write("fight plans: " + r.Status + (r.Details != null ? " " + r.Details : ""));
    }

    // the arena fights' detailed record to the site (src/TraceUpload.cs): after each kept fight, and once per start as soon as the
    // game account is known (unsent fights of the last 3 days); with the account sync's consent (AutoSync) and a sync code
    readonly TraceSentList traceSent = new TraceSentList(TraceUpload.SentPath);
    bool traceStartDone;

    void UploadTraces() {
      if (!cfg.AutoSync || !SyncClient.IsValidCode(cfg.Code)) return;
      string site = cfg.Site, code = cfg.Code;
      Task.Factory.StartNew(() => { try { TraceUpload.RunOnce(RFX.BattlesDir, traceSent, site, code, Log.Write); } catch (Exception e) { Log.Write("трасса боя: ошибка " + e.Message); } });
    }

    void TraceStartTick() {
      if (traceStartDone || !gameRunning || SyncClient.Player <= 0) return;
      traceStartDone = true;
      UploadTraces();
    }

    // the timeline of the fight going on (src/BattleTimeline.cs, read-only): every fight, not only the coach's bosses;
    // attached to the fight's record when its result screen comes
    volatile FightRecorder recorder;

    void StartRecorder(ulong sim, int stage) {
      var old = recorder; if (old != null) old.Stop();
      var rec = new FightRecorder(sim, stage) { Log = Log.Write, OnSample = (f, hs) => coach.Feed(hs) };
      recorder = rec;
      rec.Start();
    }

    /// <summary>The timeline of the last fight (null: none followed, or it ended over 10 minutes ago). At a result
    /// screen it waits for the recorder to see the end and gives the fight up (a later capture must not get it again).</summary>
    string TakeTimeline(bool ended) {
      var rec = recorder;
      if (rec == null || (DateTime.UtcNow - rec.StartedUtc).TotalMinutes > 45) return null;
      if (!rec.Running && (DateTime.UtcNow - rec.EndedUtc).TotalMinutes > 10) return null;   // an older fight's
      try {
        string tl = rec.Json(ended ? 4000 : 0);
        if (ended) { rec.Stop(); recorder = null; }
        return tl;
      } catch (Exception e) { Log.Write("fight recorder: " + e.Message); return null; }
    }

    // the last account reading (the sync's) and the kept fight waiting for the next one (src/BattleCapture.cs SaveBattleAccount)
    volatile string lastAccount, battleForAccount; DateTime lastAccountAt;
    void SaveBattleAccount(string battle, string account) {
      try { RFX.SaveBattleAccount(battle, account); Log.Write("battle account kept: " + System.IO.Path.GetFileName(battle) + " (" + account.Length + " bytes)"); }
      catch (Exception e) { Log.Write("battle account: " + e.Message); }
    }

    /// <summary>Reads the last fight's statistics (after <paramref name="waitMs"/>), keeps it and syncs; also the
    /// «Записать бой» button (kind -1: no result screen known).</summary>
    void CaptureNow(int kind, ulong form, int waitMs) {
      if (capturing) return;
      capturing = true;
      Task.Factory.StartNew(() => {
        try {
          if (waitMs > 0) System.Threading.Thread.Sleep(waitMs);   // the screen fills its numbers first
          string tl = TakeTimeline(kind >= 0);
          string json = RFX.CaptureBattle(kind, form, tl);
          if (json == null) {
            Log.Write("battle end: no statistics found");
            // (the «Записать бой» button outside a fight finds nothing either: only a result screen the watch saw counts)
            // reported for the guild boss screens only (0, 1): their fights feed the calibration; the arena (3, 4) and the
            // other stages (2) are not used yet
            if (kind == 0 || kind == 1) Report("battle.no_stats", "battle end: no statistics found (result screen " + kind + ")", Ctx("screen", kind.ToString(CultureInfo.InvariantCulture)));
            win.BeginInvoke((Action)(() => Post("{\"ev\":\"battle\",\"ok\":false}")));
            return;
          }
          int st = lastStage;
          if (st > 0 && (DateTime.UtcNow - lastStageAt).TotalMinutes < 20 && json.StartsWith("{", StringComparison.Ordinal))
            json = "{\"stage\":" + st + "," + json.Substring(1);
          { var ai = arenaInfo; if (ai != null && st == arenaInfoStage && json.StartsWith("{", StringComparison.Ordinal)) { json = "{\"arenaInfo\":" + ai + "," + json.Substring(1); arenaInfo = null; } }
          string path = RFX.SaveBattle(json);
          if (path == null) { Log.Write("battle end: a replay of a kept fight, not kept"); win.BeginInvoke((Action)(() => Post("{\"ev\":\"battle\",\"ok\":false}"))); return; }
          Log.Write("battle kept: " + path + " (" + json.Length + " bytes" + (tl != null ? ", timeline " + tl.Length : "") + ")");
          // the account for checking the simulation against this fight: the last reading now (gear cannot change during a
          // fight), replaced by the reading of the sync that follows
          var acc = lastAccount;
          if (acc != null && (DateTime.UtcNow - lastAccountAt).TotalMinutes < 30) SaveBattleAccount(path, acc);
          battleForAccount = path;
          UploadTraces();
          win.BeginInvoke((Action)(() => { Post("{\"ev\":\"battle\",\"ok\":true}"); RequestAutoSync(3); }));
        } catch (Exception e) {
          Log.Write("battle end: " + e.Message);
          Report("battle.capture_failed", "battle end: " + e.GetType().Name + ": " + e.Message, Ctx("exception", e.ToString()));
        }
        finally { capturing = false; }
      });
    }

    // ------------------------------------------------------------------ sync

    [ThreadStatic] static bool autoRun;   // the sync running on this thread was started by AutoSyncTick
    void SyncEv(string stage, string extra) { Post("{\"ev\":\"sync\",\"stage\":\"" + stage + "\"" + (autoRun ? ",\"auto\":true" : "") + (extra ?? "") + "}"); }

    /// <summary>Sync by itself in <paramref name="delaySec"/> s (or as soon as the site allows): after gear changes in the
    /// game, a finished plan, or turning the setting on. Several requests close together make one sync.</summary>
    void RequestAutoSync(int delaySec) {
      var due = DateTime.UtcNow.AddSeconds(delaySec);
      if (due < autoDue) autoDue = due;
    }

    void AutoSyncTick() {
      if (!gameRunning) gameSeenAt = DateTime.MinValue;
      if (!cfg.AutoSync || !gameRunning || !(SyncClient.IsValidCode(cfg.Code) || bridgePoller.Connected)) return;
      var now = DateTime.UtcNow;
      if (gameSeenAt == DateTime.MinValue) gameSeenAt = now;
      if (now - gameSeenAt < TimeSpan.FromSeconds(FirstSyncDelaySec)) return;   // the game is still loading / at the login screen
      if (now - lastSyncStart > TimeSpan.FromMinutes(AutoEveryMin)) RequestAutoSync(0);   // changes made without a plan
      if (syncing || now < autoDue || now - lastSyncStart < TimeSpan.FromSeconds(SyncGapSec)) return;
      autoDue = DateTime.MaxValue;
      StartSync(false, true);
    }

    /// <summary>Who wears the watched items changed (the player put something on): tell the site soon.</summary>
    void NoteOwners(Dictionary<long, long> owners) {
      if (lastOwners != null) {
        bool changed = owners.Count != lastOwners.Count;
        if (!changed) foreach (var kv in owners) { long o; if (!lastOwners.TryGetValue(kv.Key, out o) || o != kv.Value) { changed = true; break; } }
        if (changed) RequestAutoSync(8);
      }
      lastOwners = new Dictionary<long, long>(owners);
    }

    void StartSync(bool saveOnly, bool auto) {
      if (syncing) return;
      bool upload = !saveOnly && SyncClient.IsValidCode(cfg.Code), toBridge = bridgePoller.Connected;
      if (auto && !upload && !toBridge) return;
      string code = cfg.Code, site = cfg.Site; bool copy = !auto && (cfg.SaveCopy || !upload);
      syncing = true; lastSyncStart = DateTime.UtcNow;
      var t = new Thread(() => {
        autoRun = auto;
        try { RunSync(upload, copy, site, code, toBridge); }
        catch (Exception e) { Log.Write("sync: " + e); SyncError("read", e.GetType().Name + ": " + e.Message, 0); }
        finally { syncing = false; }
      });
      t.IsBackground = true;
      t.Start();
    }

    void SyncError(string kind, string detail, int retryAfter) {
      SyncEv("error", ",\"error\":{\"kind\":\"" + kind + "\",\"detail\":" + S(detail) + ",\"retryAfter\":" + N(retryAfter) + "}");
      // reported: reading and sending failures. Not: the site's rate limit, the game not running (the player's state) and
      // a rejected code (the report would be rejected the same way)
      if (kind != "rate" && kind != "not_running" && kind != "token")
        Report("sync." + kind, "sync: " + kind + (string.IsNullOrEmpty(detail) ? "" : ": " + detail), Ctx("auto", autoRun ? "true" : "false"));
    }

    void RunSync(bool upload, bool copy, string site, string code, bool toBridge) {
      bool isAuto = autoRun;
      SyncEv("find", null);
      string version;
      int pid = Extractor.FindGame(out version);
      if (pid == 0) {
        if (!isAuto) { try { Log.Write("sync: the game is not found; similar processes: " + GameInfo.DescribeCandidates()); } catch (Exception) { } }
        SyncError("not_running", null, 0); return;
      }
      var started = DateTime.UtcNow;
      SyncEv("read", ",\"seconds\":0");
      var ticker = new System.Threading.Timer(_ => SyncEv("read", ",\"seconds\":" + N((long)(DateTime.UtcNow - started).TotalSeconds)), null, 1000, 1000);
      ExtractResult ex;
      try { ex = Extractor.Read(version, null); }
      finally { ticker.Dispose(); }
      Log.Write("read: " + ex.Error + " " + (ex.Detail ?? "") + "\n" + RFX.Log);
      switch (SyncOutcome.Classify(isAuto, ex.Error)) {
        case SyncKind.Ok: break;
        case SyncKind.NotRunning: SyncError("not_running", null, 0); return;
        case SyncKind.Access: SyncError("access", null, 0); return;
        case SyncKind.NotReady:   // the game has not loaded the account yet: no error card, no report, look again soon
          SyncEv("waiting", null);
          win.BeginInvoke((Action)(() => RequestAutoSync(NotReadyRetrySec)));
          return;
        default: SyncError("read", ex.Detail, 0); return;
      }
      int seconds = (int)(DateTime.UtcNow - started).TotalSeconds;
      lastAccount = ex.Json; lastAccountAt = DateTime.UtcNow;
      { var bp = battleForAccount; if (bp != null) { battleForAccount = null; SaveBattleAccount(bp, ex.Json); } }
      {   // the heroes of the account in the game now: plans for heroes not on it (another account) are set aside
        var uids = Uids(ex.Json, "heroes", "iHeroId");
        var itemUids = Uids(ex.Json, "equipment", "iItemUid");
        if (uids.Count > 0) {
          string hj = "{\"ev\":\"accountHeroes\",\"uids\":[" + string.Join(",", uids) + "],\"items\":[" + string.Join(",", itemUids) + "]}";
          win.BeginInvoke((Action)(() => Post(hj)));
        }
      }
      string saved = null;
      if (copy) { try { saved = Extractor.SaveCopy(ex.Json, RFX.Log.ToString()); lastSavedPath = saved; } catch (Exception e) { Log.Write("save: " + e.Message); } }
      if (toBridge) {
        string detail;
        var b = bridgeApi.PushSnapshot(ex.Json, version, started, out detail);
        Log.Write("bridge snapshot: " + b + (detail != null ? " " + detail : ""));
      }
      if (isAuto && !upload) return;   // a reading only for the bridge: nothing changed on the site, nothing to show
      int heroes = ex.Heroes, items = ex.Items, arts = ex.Artifacts;
      string viewUrl = null;
      if (upload) {
        SyncEv("send", null);
        var r = SyncClient.Send(site, code, ex.Json);
        Log.Write("send: " + r.Status + " " + r.HttpCode + " " + (r.Details ?? ""));
        switch (r.Status) {
          case SyncStatus.Ok: break;
          case SyncStatus.InvalidToken: SyncError("token", null, 0); return;
          case SyncStatus.RateLimited: {
            int after = r.RetryAfterSeconds > 0 ? r.RetryAfterSeconds : 30;
            if (autoRun) win.BeginInvoke((Action)(() => RequestAutoSync(after + 1)));
            SyncError("rate", null, after); return;
          }
          case SyncStatus.InvalidPayload: case SyncStatus.TooLarge: SyncError("payload", r.Details ?? r.Status.ToString(), 0); return;
          case SyncStatus.ServerError: case SyncStatus.Unexpected: case SyncStatus.Redirect:
            if (r.HttpCode >= 500 && !serverRetried) { serverRetried = true; win.BeginInvoke((Action)(() => RequestAutoSync(ServerRetrySec))); }   // one quick retry
            SyncError("server", "HTTP " + r.HttpCode, 0); return;
          default: SyncError("net", r.Details, 0); return;
        }
        serverRetried = false;
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
        autoRun = isAuto;   // this part runs on the UI thread
        SyncEv("done", ",\"result\":{\"seconds\":" + N(seconds) + ",\"heroes\":" + N(heroes) + ",\"items\":" + N(items) + ",\"artifacts\":" + N(arts)
          + ",\"viewUrl\":" + S(viewUrl) + ",\"saved\":" + S(upload ? null : saved) + "},\"last\":" + LastJson());
      }));
    }

    // Base ids of the three strongest heroes of the dump (by the game's own power value).
    // the uids of a list of the account read (heroes / equipment)
    static List<long> Uids(string json, string list, string key) {
      var res = new List<long>();
      try {
        var root = MiniJson.AsObject(MiniJson.Parse(json)); object hv;
        var heroes = root != null && root.TryGetValue(list, out hv) ? hv as List<object> : null;
        if (heroes != null) foreach (var h in heroes) { var d = MiniJson.AsObject(h); object u; if (d != null && d.TryGetValue(key, out u) && u is double) res.Add((long)(double)u); }
      } catch (Exception) { }
      return res;
    }

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
        // set and main stat of every plan item: the auto-pilot sets the game's gear filter with them
        if (r.Status == PlansStatus.Ok) {
          var info = new List<long[]>();
          foreach (var p in r.Plans) foreach (var it in p.Items)
            info.Add(new[] { it.Uid, it.SetId, it.Main.Count > 0 && it.Main[0].Stat > 0 ? it.Main[0].Stat : 0 });
          win.BeginInvoke((Action)(() => { foreach (var x in info) overlay.SetItemInfo(x[0], x[1], x[2]); }));
        }
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
              .Append(",\"subs\":").Append(LinesJson(it.Subs, "rolls")).Append(",\"setBonus\":").Append(LinesJson(it.SetBonus, "pieces"))
              .Append(",\"main\":").Append(LinesJson(it.Main, "rolls")).Append(",\"quality\":").Append(N(it.Quality)).Append(",\"qualityName\":").Append(S(it.QualityName));
            if (it.CurKnown && it.Cur == null) sb.Append(",\"cur\":null");
            else if (it.CurKnown) sb.Append(",\"cur\":{\"uid\":").Append(N(it.Cur.Uid)).Append(",\"name\":").Append(S(it.Cur.Name)).Append(",\"level\":").Append(N(it.Cur.Level))
                   .Append(",\"stars\":").Append(N(it.Cur.Stars)).Append(",\"icon\":").Append(S(it.Cur.Icon))
                   .Append(",\"mainStat\":").Append(S(it.Cur.MainStat)).Append(",\"subs\":").Append(LinesJson(it.Cur.Subs, "rolls"))
                   .Append(",\"main\":").Append(LinesJson(it.Cur.Main, "rolls")).Append(",\"quality\":").Append(N(it.Cur.Quality))
                   .Append(",\"qualityName\":").Append(S(it.Cur.QualityName)).Append('}');
            sb.Append('}');
          }
          sb.Append("]}");
        }
        Post(sb.Append("]}").ToString());
      });
    }

    static string LinesJson(List<SubStat> l, string numKey) {
      var sb = new StringBuilder("[");
      for (int i = 0; i < l.Count; i++) {
        if (i > 0) sb.Append(',');
        sb.Append("{\"text\":").Append(S(l[i].Text)).Append(",\"").Append(numKey).Append("\":").Append(N(l[i].Rolls));
        if (l[i].Stat >= 0) sb.Append(",\"stat\":").Append(N(l[i].Stat));
        if (l[i].Name != null) sb.Append(",\"name\":").Append(S(l[i].Name)).Append(",\"value\":").Append(S(l[i].Value ?? ""));
        if (l[i].Now != null) sb.Append(",\"now\":").Append(S(l[i].Now));
        if (l[i].Bar >= 0) sb.Append(",\"bar\":").Append(l[i].Bar.ToString("0.###", CultureInfo.InvariantCulture));
        sb.Append('}');
      }
      return sb.Append(']').ToString();
    }

    /// <param name="fresh">the live tables searched again too (not the ones found earlier this game session)</param>
    void StartScan(List<long> uids, bool ahead = false, bool fresh = false) {
      if (scanning || (uids.Count == 0 && !ahead)) return;
      scanning = true; watch = uids; liveTimer.Stop(); lastLive = null;
      Task.Factory.StartNew(() => {
        EquipAddrs a = null;
        try {
          lock (Extractor.Gate) {
            if (fresh) RFX.ForgetLive();
            a = RFX.FindEquip(uids, fresh ? (Action<string>)(s => Log.Write("  " + s)) : null);
          }
        }
        catch (Exception e) { Log.Write("equip scan: " + e); }
        win.BeginInvoke((Action)(() => {
          scanning = false; addrs = a; overlay.SetAddrs(a);
          Log.Write("equip scan: " + (a == null ? "failed " + RFX.LastError : "panel=" + (a.Panel != 0) + " form=" + (a.Form != 0) + " items=" + a.Items.Count));
          Post("{\"ev\":\"equip.scan\",\"status\":" + S(a == null ? "fail" : "ok") + ",\"panel\":" + B(a != null && (a.Panel != 0 || a.Form != 0)) + "}");
          if (a != null) { liveTimer.Start(); PollLive(); }
        }));
      });
    }

    void Watch(List<long> uids) {
      if (addrs == null) { if (scanning) watch = uids; else StartScan(uids); return; }
      // new plan items are found through EquipData.equips on the next poll; a full scan only without it
      if (addrs.EquipData == 0) foreach (var u in uids) if (!addrs.Items.ContainsKey(u)) { StartScan(uids); return; }
      watch = uids;
    }

    // A started plan standing still (the same step 10 s, no pilot clicking): the game's tables are searched afresh -
    // live 27.09 an item put on was not seen until the app was restarted
    string stallKey, pollError, ownersLogged; DateTime stallAt;
    void WatchStall() {
      string k = overlay.StallKey();
      if (k == null || k != stallKey) { stallKey = k; stallAt = DateTime.UtcNow; return; }
      if ((DateTime.UtcNow - stallAt).TotalSeconds < 10 || addrs == null || scanning || DateTime.UtcNow <= rescanAfter) return;
      stallAt = DateTime.UtcNow; rescanAfter = DateTime.UtcNow.AddSeconds(20);
      Log.Write("equip scan again: the plan stands still (" + k + ")");
      StartScan(watch, false, true); overlay.RestartPilots();
    }

    void PollLive() {
      if (addrs == null || scanning) return;
      EquipLive s;
      try { s = RFX.Poll(addrs, watch); }
      catch (Exception e) {
        // (was swallowed: a poll failing every time froze the page's guide on one step)
        string m = e.GetType().Name + ": " + e.Message;
        if (m != pollError) { pollError = m; Log.Write("live poll failed: " + e); Report("live_poll_failed", "live poll failed: " + m, Ctx("exception", e.ToString())); }
        return;
      }
      pollError = null;
      // the hero screen is open, but the scan ran before the game built it (the player was in the city): scan again, at
      // most once a minute - without the form the hero cannot be brought up and the gear list is not known to be open
      if (s.GameRunning && s.HeroUid > 0 && addrs.Form == 0 && DateTime.UtcNow > rescanAfter) {
        rescanAfter = DateTime.UtcNow.AddSeconds(60); Log.Write("equip scan again: the hero screen is open now"); StartScan(watch); return;
      }
      var sb = new StringBuilder("{\"ev\":\"equip.live\",\"live\":{");
      sb.Append("\"gameRunning\":").Append(B(s.GameRunning)).Append(",\"heroUid\":").Append(N(s.HeroUid)).Append(",\"panelOk\":").Append(B(s.PanelOk))
        .Append(",\"form\":").Append(B(s.FormOk)).Append(",\"tab\":").Append(N(s.Tab))
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
      NoteOwners(s.Owner);
      {   // the journal: who wears the plan items (on every change), the hero and the open slot
        var ob = new StringBuilder("live: hero " + s.HeroUid + " part " + s.Part + " sel " + s.SelUid + " owners");
        foreach (var kv in s.Owner) ob.Append(' ').Append(kv.Key).Append('>').Append(kv.Value);
        string ol = ob.ToString();
        if (ol != ownersLogged) { ownersLogged = ol; Log.Write(ol); }
      }
      string json = sb.ToString();
      if (json == lastLive) return;   // nothing changed on the game screen
      lastLive = json;
      Post(json);
      if (!s.GameRunning) { liveTimer.Stop(); addrs = null; overlay.SetAddrs(null); }
    }

    void FinishPlan(string id, bool done, bool taken) {
      if (id != null && id.StartsWith(BridgePlanPrefix, StringComparison.Ordinal)) { FinishBridgeCommand(id.Substring(BridgePlanPrefix.Length), done, taken); return; }
      string site = cfg.Site, code = cfg.Code;
      if (string.IsNullOrEmpty(id) || !SyncClient.IsValidCode(code)) return;
      Task.Factory.StartNew(() => {
        var r = PlansClient.Finish(site, code, id, done);
        Log.Write("plan " + id + " " + (done ? "done" : "cancelled") + ": " + r.Status);
        if (done) win.BeginInvoke((Action)(() => RequestAutoSync(1)));
      });
    }

    // ------------------------------------------------------------------ storage cleanup

    void LoadSell(string lang) {
      string site = cfg.Site, code = cfg.Code;
      if (!SyncClient.IsValidCode(code)) { Post("{\"ev\":\"sell\",\"status\":\"invalid_token\"}"); return; }
      Task.Factory.StartNew(() => {
        var r = SellClient.Get(site, code, lang == "en" ? "en" : "ru");
        if (r.Status != PlansStatus.Ok) {
          Post("{\"ev\":\"sell\",\"status\":" + S(r.Status == PlansStatus.InvalidToken ? "invalid_token" : "error") + ",\"detail\":" + S(r.Details ?? ("HTTP " + r.HttpCode)) + "}");
          return;
        }
        var sb = new StringBuilder("{\"ev\":\"sell\",\"status\":\"ok\",\"list\":");
        if (r.List == null) sb.Append("null");
        else {
          sb.Append("{\"id\":").Append(S(r.List.Id)).Append(",\"createdAt\":").Append(S(r.List.CreatedAt)).Append(",\"items\":[");
          for (int i = 0; i < r.List.Items.Count; i++) {
            var it = r.List.Items[i]; if (i > 0) sb.Append(',');
            sb.Append("{\"uid\":").Append(N(it.Uid)).Append(",\"slot\":").Append(N(it.Slot)).Append(",\"name\":").Append(S(it.Name))
              .Append(",\"setName\":").Append(S(it.SetName)).Append(",\"level\":").Append(N(it.Level)).Append(",\"stars\":").Append(N(it.Stars)).Append('}');
          }
          sb.Append("]}");
        }
        Post(sb.Append('}').ToString());
      });
    }

    void FinishSell(string id, bool done) {
      string site = cfg.Site, code = cfg.Code;
      if (string.IsNullOrEmpty(id) || !SyncClient.IsValidCode(code)) return;
      Task.Factory.StartNew(() => {
        var r = SellClient.Finish(site, code, id, done);
        Log.Write("sell list " + id + " " + (done ? "done" : "cancelled") + ": " + r.Status);
        if (done) win.BeginInvoke((Action)(() => RequestAutoSync(1)));
      });
    }

    // ------------------------------------------------------------------ local bridge

    void OnBridgeConnected(bool up) {
      Log.Write("bridge " + (up ? "connected" : "gone"));
      if (up && gameRunning) RequestAutoSync(0);   // the bridge needs a snapshot before it can accept equip commands
    }

    // An equip command from the bridge becomes a plan on the page; the guide, the frame and the auto click work as for
    // a plan from the site. The page answers with equip.finish (FinishPlan) when the items are on the hero or the player
    // removes the plan.
    void OnBridgeCommand(BridgeCommand c) {
      if (c.Type != "equip") { ReportBridge(c.Id, "failed", "Wardsage " + Program.Version + " cannot run '" + c.Type + "' commands."); return; }
      if (!gameRunning) { ReportBridge(c.Id, "failed", "The game is not running."); return; }
      if (bridgeOpen.ContainsKey(c.Id)) return;
      bridgeOpen[c.Id] = c;
      foreach (var s in c.Slots) overlay.SetItemInfo(s.ItemUid, s.SetId, s.MainStatId);   // for the game's filter
      Log.Write("bridge equip " + c.Id + " (" + (c.CommandId ?? "") + "): hero " + N(c.HeroUid) + ", " + c.Slots.Count + " item(s)");

      var sb = new StringBuilder("{\"ev\":\"bridge.equip\",\"plan\":{\"id\":").Append(S(BridgePlanPrefix + c.Id))
        .Append(",\"heroUid\":").Append(N(c.HeroUid)).Append(",\"heroName\":").Append(S(c.HeroName ?? "#" + N(c.HeroUid)))
        .Append(",\"items\":[");
      for (int i = 0; i < c.Slots.Count; i++) {
        if (i > 0) sb.Append(',');
        sb.Append("{\"slot\":").Append(N(c.Slots[i].Slot)).Append(",\"uid\":").Append(N(c.Slots[i].ItemUid)).Append('}');
      }
      Post(sb.Append("]}}").ToString());
    }

    void FinishBridgeCommand(string id, bool done, bool taken) {
      if (!bridgeOpen.Remove(id)) return;
      if (taken) ReportBridge(id, "failed", "An item is on another hero now; Wardsage does not take it off. Rebuild the plan.");
      else ReportBridge(id, done ? "done" : "cancelled", done ? null : "Removed in Wardsage.");
      if (done) RequestAutoSync(1);   // a fresh reading confirms the move to the bridge and the site
    }

    /// <summary>Answers every open command (game closed, program closing) and takes their plans off the page.
    /// <paramref name="wait"/>: answer on this thread, briefly - the program is about to exit.</summary>
    void CloseBridgeCommands(string status, string message, bool wait) {
      if (bridgeOpen.Count == 0) return;
      var ids = new List<string>(bridgeOpen.Keys);
      bridgeOpen.Clear();
      foreach (var id in ids) {
        if (wait) { try { bridgeApi.Report(id, status, message, 1500); } catch (Exception) { } }
        else ReportBridge(id, status, message);
      }
      if (!wait) {
        var sb = new StringBuilder("{\"ev\":\"bridge.drop\",\"ids\":[");
        for (int i = 0; i < ids.Count; i++) { if (i > 0) sb.Append(','); sb.Append(S(BridgePlanPrefix + ids[i])); }
        Post(sb.Append("]}").ToString());
      }
    }

    void ReportBridge(string id, string status, string message) {
      Task.Factory.StartNew(() => {
        var r = bridgeApi.Report(id, status, message, 10000);
        Log.Write("bridge result " + id + " " + status + ": " + r);
      });
    }
  }
}
