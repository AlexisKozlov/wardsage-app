// RealmForge.exe - messages between the page (ui/app.js) and the program.
//
// page -> host  {cmd:"init"|"setLang"|"setCode"|"clearCode"|"setSaveCopy"|"setErrorReports"|"setSite"|"paste"|"sync"|"open"|"openLog"|
//                    "openFolder"|"plans.load"|"news.fetch"|"forum.unread"|"equip.scan"|"equip.watch"|"equip.finish"|"highlight"|"compact", ...}
// host -> page  {ev:"state"|"game"|"paste"|"sync"|"plans"|"equip.scan"|"equip.live"|"overlay", ...}
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
    bool serverRetried;                        // the one quick retry after a 5xx or a dropped connection is used up until a sync succeeds
    const int AutoEveryMin = 5, SyncGapSec = 31, FirstSyncDelaySec = 60, NotReadyRetrySec = 45, ServerRetrySec = 60;   // the site takes one sync per code per 30 s
    // automatic updates (app/Updater.cs): first check shortly after start, then every 6 hours
    readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
    string updateReady;   // JSON of the downloaded update for the page, or null
    string updateReadyVersion;   // its version
    // the site's switches (src/RemoteConfig.cs): asked at start and every 10 min (test build 2); the saved copy until then
    RemoteSwitches remote = RemoteSwitches.LoadCache(RemoteSwitches.CachePath) ?? RemoteSwitches.Permissive();
    readonly System.Windows.Forms.Timer remoteTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer testLogTimer = new System.Windows.Forms.Timer();   // TEST build only: log lines to the site
    bool testLogSending;
    PlansWaiter plansWait; string plansWaitKey = "";   // the long poll for new work from the site (src/PlansWait.cs)
    ScreenRecorder testRec;   // TEST build only: the screen recorder (app/RecCapture.cs), made at the first «Записать»

    public HostBridge(AppWindow win, CoreWebView2 core) {
      this.win = win; this.core = core;
      cfg = AppConfig.Load();
      SyncClient.App = Program.Version;
      TakeInstallLang();
      if (string.IsNullOrEmpty(cfg.Site)) cfg.Site = SyncClient.DefaultSite;
      arenaWatch = new ArenaWatch(() => cfg.Site, () => cfg.Code);
      guildWatch = new GuildWatch(() => cfg.Site, () => cfg.Code, st => Post("{\"ev\":\"guild\",\"state\":" + S(st) + "}"));
      packsWatch = new PacksWatch(() => cfg.Site, () => cfg.Code);
      codeWatch = new CodeWatch(() => cfg.SendCodes, () => cfg.Site, () => cfg.Code);
      gradeWatch = new GradeWatch(() => cfg.Code);
      gameTimer.Interval = 2000; gameTimer.Tick += (s, e) => {
        CheckGame(false);
        SyncPlansWait();
        if (On("battle")) WatchBattleEnd();
        WatchStall();
        arenaWatch.Tick(gameRunning && On("battle")); guildWatch.Tick(gameRunning && On("guild")); packsWatch.Tick(gameRunning); codeWatch.Tick(gameRunning); gradeWatch.Tick(gameRunning);
        TraceStartTick(); TryAutoUpdate(); LayoutTick();
      }; gameTimer.Start();
      liveTimer.Interval = 400; liveTimer.Tick += (s, e) => PollLive();
      overlay = new OverlayController((st, far) => Post("{\"ev\":\"overlay\",\"state\":" + S(st) + ",\"far\":" + (far ? "true" : "false") + "}"),
                                      st => { Post("{\"ev\":\"auto\",\"state\":" + S(st) + "}"); OnAutoState(st); },
                                      (p, st) => Post("{\"ev\":\"sell.state\",\"state\":" + S(st) + ",\"selected\":" + p.Selected + ",\"missing\":" + p.Missing + ",\"extra\":" + p.Extra + "}"));
      ApplyAutoClick(); ApplyGuideStyle(); overlay.GuideSite = cfg.Site;
      RFX.SkipGvg = !On("gvg");
      overlay.AutoConfirm = cfg.AutoConfirm;
      overlay.RecButton = Channel.IsTest && cfg.RecButton;
      overlay.RecPressed += () => { Log.Write("record button pressed over the game"); ToggleRec(); };
      overlay.CancelText = CancelText(cfg.Lang); overlay.LocateCancelText = LocateCancelText(cfg.Lang); overlay.SellCancelText = SellCancelText(cfg.Lang);
      coach.Lang = cfg.Lang;
      coach.PlaceX = cfg.CoachX; coach.PlaceY = cfg.CoachY;
      coach.PlaceChanged = (x, y) => { cfg.CoachX = x; cfg.CoachY = y; Save(); };
      // «Отменить надевание» over the game: the interface drops the running build (as its own «Отменить»)
      overlay.CancelPressed += () => { Log.Write("cancel pressed over the game"); Post("{\"ev\":\"cancelRun\"}"); };
      overlay.LocateChanged += (step, subs) => PostLocateState(step, subs);
      overlay.LocateFinished += (id, what) => {
        FinishLocate(id, true, true);   // reported done; the overlay shows its plate a few seconds and ends the hints itself
        if (cfg.AutoSync) RequestAutoSync(1);   // the same sync as the automatic one (its own gap rule), without the 5 min wait: the site drops the item
        Post("{\"ev\":\"locate.done\",\"id\":" + S(id) + ",\"what\":" + S(what) + "}");
      };
      overlay.SellHintChanged += st => PostSellHint(st);
      // «Отменить чистку склада» over the game: the run ends at once here (clicks and hints), the page drops the list and tells the site in the background
      overlay.SellCancelPressed += () => { overlay.SetSell(null, null, null, 0); Post("{\"ev\":\"sell.cancelRun\"}"); };
      overlay.Problem += (kind, message) => Report(kind, message, null);
      Program.FatalReport = ReportFatal;
      autoTimer.Interval = 1000; autoTimer.Tick += (s, e) => AutoSyncTick(); autoTimer.Start();
      updateTimer.Interval = 20000; updateTimer.Tick += (s, e) => { updateTimer.Interval = (Channel.IsTest ? 2 : 5) * 60 * 1000; if (updateReady == null || Channel.IsTest) CheckUpdate(); }; updateTimer.Start();
      remoteTimer.Interval = 3000; remoteTimer.Tick += (s, e) => { remoteTimer.Interval = RemoteSwitches.EveryMinutes(Channel.IsTest) * 60 * 1000; FetchRemote(); }; remoteTimer.Start();
      if (Channel.IsTest) { testLogTimer.Interval = 30000; testLogTimer.Tick += (s, e) => { testLogTimer.Interval = 120000; SendTestLog(); }; testLogTimer.Start(); }
      Log.Write(Channel.ProductName + " " + Program.Version + " started");
      Log.Write("switches (saved copy or defaults): " + remote.ToJson());
    }

    // ------------------------------------------------------------------ the site's switches

    /// <summary>The app's own clicks: the player's setting and the site's "autoclick" switch.</summary>
    bool AutoClickOn() { return cfg.AutoClick && On("autoclick"); }
    /// <summary>«Показать в игре» clicks by itself only with its own switch on (off by default until the owner enables it), on top of the player's setting.</summary>
    bool LocateAutoOn() { return AutoClickOn() && On("locateAuto"); }
    /// <summary>The artifact flows (storage cleanup, promotion) click by themselves only with the site's "artAuto" switch on (off by default), on top of the player's setting.</summary>
    bool ArtAutoOn() { return AutoClickOn() && On("artAuto"); }
    /// <summary>The new button highlight: only the site's "guideStyle" switch.</summary>
    void ApplyGuideStyle() { overlay.GuideStyle = remote.Allows("guideStyle"); }
    void ApplyAutoClick() { overlay.AutoEnabled = AutoClickOn(); overlay.LocateAutoEnabled = LocateAutoOn(); overlay.ArtAutoEnabled = ArtAutoOn(); }

    // the features that read the game's screens and fights need the layout data for the running game version
    // (src/Layout.cs); the account sync, the guild and the update packs do not
    static readonly HashSet<string> NeedsLayout = new HashSet<string> { "battle", "equip", "locate", "autoclick", "locateAuto", "artAuto" };
    /// <summary>A feature may run: the site's switch is on and, when it needs it, the layout data is there.</summary>
    bool On(string feature) { return remote.Allows(feature) && (!NeedsLayout.Contains(feature) || Layout.Current != null); }

    void FetchRemote() {
      string site = cfg.Site;
      Task.Factory.StartNew(() => {
        bool reached;
        var r = RemoteSwitches.Fetch(site, out reached);
        if (r == null) {   // no usable reply: the switches held (and the saved announcement) stay; ask again soon; the page says "no connection" only when the site was not reached
          OnUi(() => { remoteTimer.Interval = 20000; Post("{\"ev\":\"site\",\"ok\":" + B(reached) + "}"); });
          return;
        }
        r.SaveCache(RemoteSwitches.CachePath);
        OnUi(() => {
          remoteTimer.Interval = RemoteSwitches.EveryMinutes(Channel.IsTest) * 60 * 1000;
          Post("{\"ev\":\"site\",\"ok\":true}");
          bool changed = !r.SameAs(remote);
          remote = r;
          if (changed) { Log.Write("switches changed: " + r.ToJson()); ApplyRemote(); }
          else PostRemote();   // the announcement may have run out
        });
      });
    }

    /// <summary>Puts the switches in force at once: what is off stops now, not at its next start.</summary>
    void ApplyRemote() {
      ApplyAutoClick(); ApplyGuideStyle();
      RFX.SkipGvg = !On("gvg");
      if (!On("locate")) overlay.SetLocate(null);
      if (!On("equip")) { overlay.SetRun(0); overlay.SetTarget(0); overlay.SetHint("", -1, new string[0], 0); }
      if (!On("battle")) {
        if (coach.Running) coach.Stop();
        var rec = recorder; if (rec != null) { rec.Stop(); recorder = null; }
      }
      if (!remote.ReadEnabled || Layout.Current == null) overlay.SetSell(null, null, null, 0);
      if (!remote.ReadEnabled) {
        liveTimer.Stop(); addrs = null; overlay.SetAddrs(null); lastLive = null;
      }
      PostRemote();
    }

    void PostRemote() {
      var sb = new StringBuilder("{\"ev\":\"remote\",\"readEnabled\":").Append(B(remote.ReadEnabled)).Append(",\"off\":[");
      var off = remote.Off();
      for (int i = 0; i < off.Count; i++) { if (i > 0) sb.Append(','); sb.Append(S(off[i])); }
      sb.Append("],\"announcement\":");
      var nt = remote.Notice;
      sb.Append(nt != null && nt.Live(DateTime.UtcNow) ? nt.ToJson() : "null");
      Post(sb.Append('}').ToString());
    }

    // ------------------------------------------------------------------ layout data for the running game version

    string layoutFor, layoutAsm;                                 // the game version the data in force (or awaited) is for
    DateTime layoutRetryAt = DateTime.MaxValue;       // the next ask while there is none
    bool layoutFetching, layoutNoneLogged, layoutDenied;   // layoutDenied: no valid sync code, the site gives no data

    /// <summary>The game (re)started with version <paramref name="v"/>: its saved copy now, the site asked at once (a saved
    /// copy is refreshed once per game start).</summary>
    void SelectLayout(string v) {
      var had = Layout.Current;
      Layout.Forget();
      var cached = GameLayout.IsVersion(v) ? Layout.LoadCache(Layout.CacheDir, v) : null;
      Layout.Use(cached);
      layoutFor = v; layoutAsm = Layout.LocalAsm; layoutNoneLogged = false;
      bool otherBuild = cached == null && Layout.CacheIsOtherBuild(Layout.CacheDir, v);
      Log.Write("layout data for game version " + (v ?? "?") + ": " + (cached != null ? "saved copy" : otherBuild ? "layout data is for a different build of the game (saved copy not usable)" : "none yet"));
      if (cached != null && cached.RvaDropped) LogOtherBuild(v, cached);
      if (had != cached) ApplyRemote();
      PostLayout();
      FetchLayout(v);
      SelectGeometry(v);
    }

    void LayoutTick() {
      if (gameRunning && Layout.Current == null && !layoutFetching && DateTime.UtcNow >= layoutRetryAt) FetchLayout(layoutFor);
      if (gameRunning && Geo.Current == null && !geoFetching && DateTime.UtcNow >= geoRetryAt) FetchGeometry(layoutFor);
    }

    // ------------------------------------------------------------------ screen geometry for the running game version (src/Geometry.cs)
    // Frames and clicks in the Foundry come from it; none (no file on the site, offline without a saved copy) = text hints only.

    DateTime geoRetryAt = DateTime.MaxValue;
    bool geoFetching, geoNoneLogged;

    void SelectGeometry(string v) {
      var cached = GameLayout.IsVersion(v) ? Geo.LoadCache(Geo.CacheDir, v) : null;
      Geo.Use(cached);
      geoNoneLogged = false;
      Log.Write("screen geometry for game version " + (v ?? "?") + ": " + (cached != null ? "saved copy" : "none yet"));
      FetchGeometry(v);
    }

    void FetchGeometry(string v) {
      if (!GameLayout.IsVersion(v) || geoFetching || !SyncClient.IsValidCode(cfg.Code)) return;
      geoFetching = true;
      geoRetryAt = DateTime.UtcNow.AddMinutes(Layout.RetryMinutes(Channel.IsTest));
      string site = cfg.Site, code = cfg.Code;
      Task.Factory.StartNew(() => {
        GameGeometry g = null; string json = null; var r = Layout.Reply.NoReply;
        try { r = Geo.Fetch(site, code, v, out g, out json); } catch (Exception) { r = Layout.Reply.NoReply; }
        if (r == Layout.Reply.Got) Geo.SaveCache(Geo.CacheDir, v, json);
        OnUi(() => {
          geoFetching = false;
          if (v != layoutFor) return;   // another game version meanwhile
          if (r == Layout.Reply.Got) { Geo.Use(g); Log.Write("screen geometry for game version " + v + ": from the site"); }
          else if (Geo.Current == null && !geoNoneLogged) {
            geoNoneLogged = true;
            Log.Write("screen geometry for game version " + v + ": " + (r == Layout.Reply.None ? "not on the site yet" : r == Layout.Reply.Denied ? "no valid sync code" : "the site did not answer") + "; the Foundry steps stay text hints");
          }
        });
      });
    }

    void FetchLayout(string v) {
      if (!GameLayout.IsVersion(v) || layoutFetching) return;
      if (!SyncClient.IsValidCode(cfg.Code)) { if (!layoutDenied) { layoutDenied = true; PostLayout(); } return; }
      layoutFetching = true;
      layoutRetryAt = DateTime.UtcNow.AddMinutes(Layout.RetryMinutes(Channel.IsTest));
      string site = cfg.Site, code = cfg.Code;
      Task.Factory.StartNew(() => {
        GameLayout l = null; string json = null; var r = Layout.Reply.NoReply;
        try { r = Layout.Fetch(site, code, v, out l, out json); } catch (Exception) { r = Layout.Reply.NoReply; }
        if (r == Layout.Reply.Got) Layout.SaveCache(Layout.CacheDir, v, json);
        OnUi(() => {
          layoutFetching = false;
          if (v != layoutFor) return;   // another game version meanwhile
          bool denied = r == Layout.Reply.Denied;
          if (denied != layoutDenied) { layoutDenied = denied; PostLayout(); }
          if (r == Layout.Reply.Got) {
            bool was = Layout.Current != null;
            Layout.Use(l);
            Log.Write("layout data for game version " + v + ": from the site");
            if (l != null && l.RvaDropped) LogOtherBuild(v, l);
            ApplyRemote();
            PostLayout();
            // the game's screens found in memory now (as at the game's start)
            if (!was && gameRunning && addrs == null && !scanning && AutoClickOn()) StartScan(watch, true);
          } else if (Layout.Current == null && !layoutNoneLogged) {
            layoutNoneLogged = true;
            if (r == Layout.Reply.OtherBuild) Log.Write("layout data for game version " + v + ": the build fingerprint in the layout data is malformed; the data is not used");
            else Log.Write("layout data for game version " + v + ": " + (r == Layout.Reply.None ? "not on the site yet" : r == Layout.Reply.Denied ? "no valid sync code" : "the site did not answer") + "; asked again every " + Layout.RetryMinutes(Channel.IsTest) + " min");
          }
        });
      });
    }

    static string Short(string asm) { return asm == null ? "?" : asm.Length > 12 ? asm.Substring(0, 12) : asm; }
    static void LogOtherBuild(string v, GameLayout l) {
      Log.Write("layout data for game version " + v + ": for another build (asm " + Short(l.Asm) + ", local " + Short(Layout.LocalAsm) + "): rva ignored, classes found by scan");
    }

    /// <summary>The page's note: the running game has no layout data (yet), some features wait for it.</summary>
    void PostLayout() {
      Post("{\"ev\":\"layout\",\"missing\":" + B(gameRunning && Layout.Current == null) + ",\"noCode\":" + B(layoutDenied) + ",\"game\":" + S(gameVersion) + "}");
    }

    static string CancelText(string lang) { return lang == "en" ? "Cancel equipping" : "Отменить надевание"; }
    static string LocateCancelText(string lang) { return lang == "en" ? "Stop auto click" : "Остановить автонажатие"; }
    static string SellCancelText(string lang) { return lang == "en" ? "Cancel storage cleanup" : "Отменить чистку склада"; }

    public void Dispose() {
      Program.FatalReport = null;
      gameTimer.Dispose(); liveTimer.Dispose(); autoTimer.Dispose(); updateTimer.Dispose(); remoteTimer.Dispose(); testLogTimer.Dispose(); if (testRec != null) testRec.Stop(); if (plansWait != null) plansWait.Stop(); overlay.Dispose();
    }

    // «Обновлять автоматически» (on by default): a downloaded update is put in place by restarting in a quiet moment — no
    // sync, scan or fight capture running, no fight going on, the equip / sell pilot idle, no open site command, the
    // window untouched for 2 minutes
    DateTime lastUi = DateTime.UtcNow;
    bool restarting;
    void TryAutoUpdate() {
      if (updateReady == null || restarting || !cfg.AutoUpdate) return;
      if (syncing || scanning || capturing || overlay.Busy) return;
      var rec = recorder;
      if (rec != null && rec.Running) return;
      if ((DateTime.UtcNow - lastUi).TotalMinutes < 2) return;
      restarting = true;
      Log.Write("update: restarting by itself in a quiet moment");
      win.SaveForRestart();
      Program.RestartForUpdate();
    }

    // TEST build: send the waiting log lines (never logs about itself: that would feed the queue)
    void SendTestLog() {
      if (testLogSending || !SyncClient.IsValidCode(cfg.Code)) return;
      var lines = Log.TestQueue.Take(500, 200 * 1024);
      if (lines.Count == 0) return;
      testLogSending = true;
      string site = cfg.Site, code = cfg.Code;
      Task.Factory.StartNew(() => {
        var r = TestLogClient.Result.Later;
        try { r = TestLogClient.Send(site, code, Program.Version, lines); } catch (Exception) { }
        if (r != TestLogClient.Result.Ok) Log.TestQueue.Return(lines);
        testLogSending = false;
        if (r == TestLogClient.Result.Off) {
          try { win.BeginInvoke((Action)(() => testLogTimer.Stop())); } catch (Exception) { }
        }
      });
    }

    void CheckUpdate() {
      string site = cfg.Site;
      Task.Factory.StartNew(() => {
        var u = Updater.CheckAndDownload(site);
        // a dropped connection: look again in 20 s instead of waiting for the next round
        if (Updater.LastFailed) { OnUi(() => { updateTimer.Stop(); updateTimer.Interval = 20000; updateTimer.Start(); }); return; }
        if (u == null) return;
        if (updateReady != null && !Updater.Newer(u.Version, updateReadyVersion)) return;   // test builds keep looking: only a newer one replaces the downloaded one
        updateReadyVersion = u.Version;
        updateReady = "{\"ev\":\"update\",\"version\":" + S(u.Version) + ",\"notesRu\":" + S(u.NotesRu) + ",\"notesEn\":" + S(u.NotesEn) + "}";   // the page shows the one of its language
        Post(updateReady);
      });
    }

    // ------------------------------------------------------------------ plumbing

    // TEST build: the screen recorder starts / stops from Settings (rec.start / rec.stop) or from the button over the game (same code path)
    void StartRec() {
      if (!Channel.IsTest) return;
      if (testRec == null) testRec = new ScreenRecorder(() => cfg.Site, () => cfg.Code, s => Log.Write(s), ev => Post(ev), on => OnUi(() => overlay.SetRecording(on)));
      testRec.Start();
    }
    void ToggleRec() { if (testRec != null && testRec.On) testRec.Stop(); else StartRec(); }

    // the long poll runs while the game runs and a valid code is set; a new site / code restarts it; the page learns whether it works
    // (then its own polling slows down). A site without the endpoint (404) is left alone for this run
    void SyncPlansWait() {
      bool want = gameRunning && SyncClient.IsValidCode(cfg.Code);
      string key = want ? cfg.Site + "|" + cfg.Code : "";
      if (plansWait == null) plansWait = new PlansWaiter(() => cfg.Site, () => cfg.Code, () => Post("{\"ev\":\"plans.changed\"}"), (ok, reach) => Post("{\"ev\":\"plansWait\",\"ok\":" + B(ok) + ",\"reach\":" + B(reach) + "}"), s => Log.Write(s));
      if (key != plansWaitKey) {
        bool dead = plansWait.Dead;
        plansWait.Stop();
        plansWaitKey = key;
        if (dead) plansWait = new PlansWaiter(() => cfg.Site, () => cfg.Code, () => Post("{\"ev\":\"plans.changed\"}"), (ok, reach) => Post("{\"ev\":\"plansWait\",\"ok\":" + B(ok) + ",\"reach\":" + B(reach) + "}"), s => Log.Write(s));
      }
      if (want) plansWait.Start();
    }

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
          case "init": win.PageReady(); SendState(null); CheckGame(true); PostRemote(); PostLayout(); if (updateReady != null) Post(updateReady); break;
          case "acceptTerms": cfg.TermsAccepted = AppConfig.TermsVersion; Save(); Log.Write("terms accepted: " + AppConfig.TermsVersion); break;
          case "setLang": cfg.Lang = MiniJson.GetString(m, "lang") == "en" ? "en" : "ru"; overlay.CancelText = CancelText(cfg.Lang); overlay.LocateCancelText = LocateCancelText(cfg.Lang); overlay.SellCancelText = SellCancelText(cfg.Lang); coach.Lang = cfg.Lang; Save(); break;
          case "setCode": {
            string code = SyncClient.ExtractCode(MiniJson.GetString(m, "code") ?? "");
            if (SyncClient.IsValidCode(code)) {
              cfg.Code = code; Save(); SendState("codeSaved");
              if (gameRunning && Layout.Current == null) FetchLayout(layoutFor);   // the layout data is given with a code only
              if (gameRunning && Geo.Current == null) FetchGeometry(layoutFor);
            }
            break;
          }
          case "clearCode": cfg.Code = ""; Save(); SendState(null); break;
          case "setSaveCopy": cfg.SaveCopy = MiniJson.GetBool(m, "on", false); Save(); break;
          case "setAutoSync": cfg.AutoSync = MiniJson.GetBool(m, "on", true); Save(); if (cfg.AutoSync) RequestAutoSync(0); break;
          case "setAutoClick": cfg.AutoClick = MiniJson.GetBool(m, "on", true); ApplyAutoClick(); if (AutoClickOn() && overlay.HasJob && addrs == null && !scanning) StartScan(watch, true); Save(); break;   // (switched on mid-job: the pilots need the game found)
          case "setAutoConfirm": cfg.AutoConfirm = MiniJson.GetBool(m, "on", false); overlay.AutoConfirm = cfg.AutoConfirm; Save(); break;
          case "setAutoUpdate": cfg.AutoUpdate = MiniJson.GetBool(m, "on", true); Save(); break;
          case "setSendCodes": cfg.SendCodes = MiniJson.GetBool(m, "on", true); Save(); break;
          case "setBossHints": cfg.BossHints = MiniJson.GetBool(m, "on", true); if (!cfg.BossHints && coach.Running) coach.Stop(); Save(); break;
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
          case "news.fetch": LoadNews(MiniJson.GetString(m, "lang")); break;
          case "forum.unread": LoadForum(); break;
          case "locate.show": {
            // «Найти в игре»: the overlay shows hints for the request the last plans reply carried (the page names it by id)
            if (!On("locate")) break;
            var lr = lastLocate; string lid = MiniJson.GetString(m, "id");
            if (lr != null && (lr.Item != null || lr.Art != null) && lr.Id == lid) {
              RFX.EnsureLiveForGuides();
              overlay.SetLocate(lr); Log.Write("locate: show " + (lr.Item != null ? "item " + lr.Item.Uid : "artifact " + lr.Art.Uid + (ArtAutoOn() ? " (promotion, auto click for artifacts on)" : " (promotion, hints only)")) + (lr.Item == null ? "" : LocateAutoOn() ? " (auto click on)" : AutoClickOn() ? " (auto click is on, but the site's locateAuto switch is off: hints only)" : ""));
              if (addrs == null && !scanning && AutoClickOn()) StartScan(watch, true);   // the auto steps need the game's screens found
            }
            break;
          }
          case "locate.tip": {
            var lines = new List<string>(); object lv2;
            var ll2 = m.TryGetValue("lines", out lv2) ? lv2 as List<object> : null;
            if (ll2 != null) foreach (var x in ll2) if (x is string && lines.Count < 4) lines.Add(((string)x).Length > 120 ? ((string)x).Substring(0, 120) : (string)x);
            string lcaps = MiniJson.GetString(m, "caps") ?? ""; if (lcaps.Length > 120) lcaps = lcaps.Substring(0, 120);
            // the pills' texts (item above / below, row forms): short strings, the page's language
            object ltx; var ltd = m.TryGetValue("texts", out ltx) ? ltx as Dictionary<string, object> : null;
            Dictionary<string, string> ltexts = null;
            if (ltd != null) { ltexts = new Dictionary<string, string>(); foreach (var kv in ltd) { var sv = kv.Value as string; if (sv != null && ltexts.Count < 12) ltexts[kv.Key] = Cut(sv); } }
            overlay.SetLocateTip(lines.ToArray(), lcaps, ltexts);
            break;
          }
          case "locate.stop": overlay.SetLocate(null); break;
          case "locate.finish": FinishLocate(MiniJson.GetString(m, "id"), MiniJson.GetBool(m, "done", false), MiniJson.GetBool(m, "keep", false)); break;
          case "sell.load": LoadSell(MiniJson.GetString(m, "lang")); break;
          case "sell.run": case "sell.hint": {
            // storage cleanup: select the list's items on the game's sell screen (from the gear list of the hero the game
            // showed last, else of any hero); the game comes forward as for «Надеть». "sell.hint": the same list, but only
            // frames over the game (auto click off): nothing is clicked, nothing comes forward
            if (!remote.ReadEnabled) { Log.Write("sell: game reading is off by the site's switch"); break; }
            if (Layout.Current == null) { Log.Write("sell: no layout data for game version " + (gameVersion ?? "?")); PostLayout(); break; }
            var uids = Uids(m);
            var parts = new List<int>(); object pv;
            var pl = m.TryGetValue("slots", out pv) ? pv as List<object> : null;
            if (pl != null) foreach (var x in pl) if (x is double && (double)x >= 0 && (double)x <= 4 && !parts.Contains((int)(double)x)) parts.Add((int)(double)x);
            // upgrade levels, same order as the uids: below +16 the game opens «Быстрое улучшение» over the list
            var levels = new Dictionary<long, int>(); object lv;
            var ll = m.TryGetValue("levels", out lv) ? lv as List<object> : null;
            if (ll != null) for (int i = 0; i < ll.Count && i < uids.Count; i++) if (ll[i] is double) levels[uids[i]] = (int)Math.Max(0, Math.Min(99, (double)ll[i]));
            long hero = addrs != null ? RFX.ReadHero(addrs) : 0; if (hero <= 0) hero = RFX.AnyHeroUid();
            bool hintOnly = cmd == "sell.hint";
            // an artifact list is always a hint run (frames on the artifact tab's «Массовая продажа»); the clicks come on top of it only with the player's auto click AND the site's "artAuto" switch (Overlay.TickArtSellAuto)
            bool artifact = MiniJson.GetString(m, "kind") == "artifact";
            if (artifact) hintOnly = true;
            RFX.EnsureLiveForGuides();
            overlay.SetSell(uids, parts, levels.Count == uids.Count ? levels : null, hero, hintOnly, artifact);
            Log.Write((artifact ? "art sell: " : "sell: ") + (hintOnly ? "hint" : "run") + " " + uids.Count + " items, hero " + hero + (artifact ? (ArtAutoOn() ? " (auto click for artifacts on)" : AutoClickOn() ? " (auto click is on, but the site's artAuto switch is off: hints only)" : "") : ""));
            if (hintOnly) break;
            if (!AutoClickOn()) Log.Write("sell: waiting - auto click is off" + (cfg.AutoClick ? " (the site's switch)" : ""));
            if (addrs == null && !scanning && AutoClickOn()) StartScan(watch, true);   // the pilot needs the game's screens found
            break;
          }
          case "sell.tip": {
            // the page's texts for the hint's plates: the step's lines and the pills for list items out of view
            var tl = new List<string>(); object tv;
            var tll = m.TryGetValue("lines", out tv) ? tv as List<object> : null;
            if (tll != null) foreach (var x in tll) if (x is string && tl.Count < 4) tl.Add(((string)x).Length > 120 ? ((string)x).Substring(0, 120) : (string)x);
            string pu = MiniJson.GetString(m, "pillUp") ?? "", pd = MiniJson.GetString(m, "pillDown") ?? "";
            overlay.SetSellTip(tl.ToArray(), pu.Length > 80 ? pu.Substring(0, 80) : pu, pd.Length > 80 ? pd.Substring(0, 80) : pd);
            break;
          }
          case "sell.stop": overlay.SetSell(null, null, null, 0); break;
          case "sell.finish": FinishSell(MiniJson.GetString(m, "id"), MiniJson.GetBool(m, "done", false)); break;
          case "equip.scan": case "equip.watch": case "equip.run": case "equip.rescan": case "highlight":
            if (!On("equip")) break;   // the site's switch: the equip helper is off
            OnEquip(cmd, m); break;
          case "log": Log.Write("ui: " + (MiniJson.GetString(m, "text") ?? "")); break;
          case "equip.finish": FinishPlan(MiniJson.GetString(m, "id"), MiniJson.GetBool(m, "done", false), MiniJson.GetBool(m, "taken", false)); break;
          case "diag": overlay.StartDiag(5); break;
          case "rec.start": StartRec(); break;
          case "rec.stop": if (testRec != null) testRec.Stop(); break;
          case "setRecButton": cfg.RecButton = MiniJson.GetBool(m, "on", true); overlay.RecButton = Channel.IsTest && cfg.RecButton; Save(); break;
          case "battle.capture": if (On("battle")) CaptureNow(-1, 0, 0); break;
          case "compact": win.SetCompact(MiniJson.GetBool(m, "on", false)); break;
          case "update.restart": Program.RestartForUpdate(); break;
        }
      } catch (Exception e) {
        Log.Write("message " + cmd + ": " + e);
        Report("unhandled", "message " + cmd + ": " + e.GetType().Name + ": " + e.Message, Ctx("exception", e.ToString()));
      }
    }

    // the equip helper's messages (only while the site's "equip" switch is on)
    void OnEquip(string cmd, Dictionary<string, object> m) {
        switch (cmd) {
          case "equip.scan": if (scanning) watch = Uids(m); else StartScan(Uids(m)); break;   // (the scan ahead is running: its result serves these items too)
          case "equip.watch": Watch(Uids(m), MiniJson.GetBool(m, "fresh", false)); break;
          case "equip.run": {
            // the plan the player started («Надеть»): its hero is brought up on the hero screen
            object v; double h = m.TryGetValue("hero", out v) && v is double ? (double)v : 0;
            overlay.SetRun(h > 0 && h < 9e15 ? (long)h : 0);
            if (MiniJson.GetBool(m, "restart", false)) overlay.RestartPilots();
            break;
          }
          case "equip.rescan":
            // the page's plan stands still: the game's tables are read again (at most every 20 s)
            if (addrs != null && !scanning && DateTime.UtcNow > rescanAfter) {
              rescanAfter = DateTime.UtcNow.AddSeconds(20); Log.Write("equip scan again: the plan stands still");
              StartScan(watch, false, true); overlay.RestartPilots();
            }
            break;
          case "highlight": {
            object v; double u = m.TryGetValue("uid", out v) && v is double ? (double)v : 0;
            overlay.SetTarget(u > 0 && u < 9e15 && watch.Contains((long)u) ? (long)u : 0);
            // hint on the game's own buttons: filter / «Заменить» / next slot, with up to two short lines
            string hint = MiniJson.GetString(m, "hint") ?? "";
            if (hint != "filter" && hint != "replace" && hint != "slot" && hint != "hero" && hint != "geartab" && hint != "done" && hint != "art") hint = "";
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
            // the tip at the game filter's × while it covers the list (the page's text in the interface language)
            string closeTip = MiniJson.GetString(m, "closeTip") ?? "";
            overlay.CloseFilterText = closeTip.Length > 80 ? closeTip.Substring(0, 80) : closeTip;
            // the filter guide's texts (the item's set and main stat) and the hero hint's (pick, find, up, down, «Герои», back)
            overlay.FilterSetText = Cut(MiniJson.GetString(m, "setTip"));
            overlay.FilterStatText = Cut(MiniJson.GetString(m, "statTip"));
            var heroTips = new List<string>();
            object ht; var htl = m.TryGetValue("heroTips", out ht) ? ht as List<object> : null;
            if (htl != null) foreach (var x in htl) { if (heroTips.Count >= 6) break; heroTips.Add(Cut(x as string)); }
            overlay.HeroTips = heroTips.ToArray();
            // the plates' texts (step caps, what to press, the strip's pill, ...) and a wrong item put on
            object tx; var txd = m.TryGetValue("texts", out tx) ? tx as Dictionary<string, object> : null;
            var texts = new Dictionary<string, string>();
            if (txd != null) foreach (var kv in txd) { var sv = kv.Value as string; if (sv != null && texts.Count < 40) texts[kv.Key] = Cut(sv); }
            overlay.Texts = texts;
            overlay.WrongItem = MiniJson.GetBool(m, "wrong", false);
            // the plan's artifact (hint "art": the gear is on, the artifact is next): the hero and the artifact's uid
            double artUid = m.TryGetValue("artUid", out v) && v is double ? (double)v : 0;
            bool artOk = hint == "art" && artUid > 0 && artUid < 9e15 && hero > 0 && hero < 9e15;
            overlay.SetArtGoal(artOk ? (long)hero : 0, artOk ? (long)artUid : 0);
            overlay.SetHint(hint, slot >= 0 && slot <= 4 ? (int)slot : -1, lines.ToArray(), hero > 0 && hero < 9e15 ? (long)hero : 0);
            break;
          }
        }
    }

    static string Cut(string s) { s = s ?? ""; return s.Length > 80 ? s.Substring(0, 80) : s; }

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
      if (st == "failed" || st == "slot_failed" || st == "unconfirmed" || st == "hero_failed") {
        var ctx = Ctx("state", st);
        string why = "";
        foreach (var kv in overlay.AutoDetail) { ctx[kv.Key] = kv.Value; if (kv.Key == "why") why = kv.Value; }
        Report("pilot." + st, "auto: " + st + (why != "" ? " - " + why : ""), ctx);
      }
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
      bool ok = u.Host == "github.com" || u.Host == "go.microsoft.com" || u.Host == "discord.gg" || u.Host == "discord.com" || u.Host == "wardsage.com" || u.Host == "www.wardsage.com";
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
      sb.Append(",\"termsOk\":").Append(B(cfg.TermsAccepted == AppConfig.TermsVersion));
      sb.Append(",\"hasCode\":").Append(B(has));
      sb.Append(",\"codePrefix\":").Append(S(has ? cfg.Code.Substring(0, 8) : ""));
      sb.Append(",\"saveCopy\":").Append(B(cfg.SaveCopy));
      sb.Append(",\"autoSync\":").Append(B(cfg.AutoSync)).Append(",\"autoClick\":").Append(B(cfg.AutoClick))
        .Append(",\"autoConfirm\":").Append(B(cfg.AutoConfirm)).Append(",\"errorReports\":").Append(B(cfg.ErrorReports)).Append(",\"autoUpdate\":").Append(B(cfg.AutoUpdate))
        .Append(",\"tray\":").Append(B(cfg.Tray)).Append(",\"bossHints\":").Append(B(cfg.BossHints)).Append(",\"sendCodes\":").Append(B(cfg.SendCodes)).Append(",\"recButton\":").Append(B(cfg.RecButton)).Append(",\"guideStyle\":").Append(S(cfg.GuideStyle)).Append(",\"autostart\":").Append(B(Autostart.IsOn()));
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
      GamePacks.Note = s => Log.Write(s);
      int pid = 0;
      try { pid = GameInfo.FindGameProcess(); } catch (Exception) { }
      if (pid != gamePid) {
        gamePid = pid;
        gameVersion = pid != 0 ? GameInfo.TryReadVersion(GameInfo.TryGetExePath(pid)) : null;
        string exe = pid != 0 ? GameInfo.TryGetExePath(pid) : null;
        GameInfo.KnownExe = exe ?? GameInfo.KnownExe;
        Layout.LocalAsm = pid != 0 ? GameInfo.TryHashAssembly(exe) : null;
        if (pid != 0) Log.Write("game build fingerprint (GameAssembly.dll sha256): " + (Layout.LocalAsm ?? "unknown"));
        Log.Write(pid != 0 ? "game found: #" + pid + " " + (GameInfo.TryGetExePath(pid) ?? "?") + ", version " + (gameVersion ?? "?") : "game closed");
        if (pid != 0 && (gameVersion != layoutFor || Layout.LocalAsm != layoutAsm)) SelectLayout(gameVersion);
      }
      bool running = pid != 0;
      if (!force && gameSent && running == gameRunning) return;
      gameSent = true; gameRunning = running;
      Post("{\"ev\":\"game\",\"running\":" + B(running) + ",\"version\":" + S(gameVersion) + "}");
      PostLayout();
      if (!running && addrs != null) { addrs = null; overlay.SetAddrs(null); liveTimer.Stop(); lastLive = null; }
      // the game's screens are found in memory at once (tens of seconds), not when the first «Надеть» comes: then the
      // equip starts right away
      if (running && addrs == null && !scanning && AutoClickOn()) StartScan(watch, true);
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
    GuildWatch guildWatch;   // the guild table to the site (app/GuildWatch.cs)
    GradeWatch gradeWatch;   // the hero rating screen: the rows shown go to the site with the next sync (app/GradeWatch.cs)
    CodeWatch codeWatch;     // the code-redeem window: the game's answer to a code goes to the site if the setting is on (app/CodeWatch.cs)
    PacksWatch packsWatch;   // the downloaded game update packs' addresses to the site (app/PacksWatch.cs)
    volatile string arenaInfo; volatile int arenaInfoStage;
    FightRecorder arenaTaken;
    void WatchBattleEnd() {
      if (!gameRunning) return;
      WatchBattleStart();   // also while a result is being captured: the next fight may already be starting
      if (capturing) return;
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
        // an arena or Malric fight: its result screen is reused or not seen and has no fight frame, so its mark never changes — the recorder saw
        // the fight end (the simulation's state 2): a few seconds later (the screen's numbers filled) it is captured
        var rec = recorder;
        if (rec == null || rec == arenaTaken || rec.Running || !TraceUpload.EndedByRecorder(rec.Stage)) return;
        double after = (DateTime.UtcNow - rec.EndedUtc).TotalSeconds;
        if (after < 4 || after > 300) return;
        int pi = forms.FindIndex(x => x.Key == 3 || x.Key == 4 || x.Key == 5);
        kind = pi >= 0 ? forms[pi].Key : -1; form = pi >= 0 ? forms[pi].Value : 0;
        Log.Write("battle end (arena or Malric, by the recorder): stage " + rec.Stage + " " + string.Join(" ", marks.ToArray()));
        arenaTaken = rec;   // once per fight, also when no result screen is held (kind −1 keeps the recorder)
        CaptureNow(kind, form, 0);
        return;
      }
      Log.Write("battle end: " + kind + " " + string.Join(" ", marks.ToArray()));
      CaptureNow(kind, form, 1500);
    }

    // a new fight: its stage; the recorder follows it, and a known boss -> the coach follows its clock
    // the stage of the fight that started last: the recorded fight names it (the site knows the boss from it)
    volatile int lastStage; DateTime lastStageAt; ulong lastStageSim;

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
      bool fresh = st == RFX.SimRunning && (sim != simSeen || fr + 15 < simFrames);
      bool wasSeen = simSeen != 0;
      simSeen = sim; simFrames = fr;
      if (!fresh) return;
      int stage = RFX.SimStage(sim);
      Log.Write("battle start: stage " + stage + " at " + RFX.FrameSeconds(fr).ToString("0") + " s" + (coach.Knows(stage) ? " (coach)" : "") + (wasSeen ? "" : " (first look)"));
      lastStage = stage; lastStageAt = DateTime.UtcNow; lastStageSim = sim;
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
        win.BeginInvoke((Action)(() => { if (simSeen == sim && cfg.BossHints && (plan != null || coach.Knows(stage))) coach.Start(sim, stage, plan); }));
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
      if (!cfg.AutoSync || !SyncClient.IsValidCode(cfg.Code) || !On("battle")) return;
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
          // the stage: the result screen's own, else the fight's start, else the battle view's simulation (read-only)
          int formSt = json.StartsWith("{", StringComparison.Ordinal) ? RFX.StageFromEnd(json) : 0;
          int simSt = 0; bool simNew = false;
          if (formSt <= 0) {
            try { ulong cs = RFX.CurrentSim(); if (cs != 0) { simSt = RFX.SimStage(cs); simNew = cs != lastStageSim; } } catch (Exception) { }
          }
          int st = RFX.PickStage(formSt, simSt, simNew, lastStage, (DateTime.UtcNow - lastStageAt).TotalMinutes);
          if (st > 0 && st != lastStage) Log.Write("battle end: stage " + st + " from " + (formSt > 0 ? "the result screen" : "the battle simulation") + (lastStage > 0 ? " (the start gave " + lastStage + ")" : ""));
          if (st <= 0) Log.Write("battle end: stage unknown (screen " + kind + ")");
          if (st > 0 && json.StartsWith("{", StringComparison.Ordinal))
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
      if (!cfg.AutoSync || !gameRunning || !remote.ReadEnabled || !(SyncClient.IsValidCode(cfg.Code))) return;
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
      if (!remote.ReadEnabled) { if (!auto) SyncError("paused", null, 0); return; }   // the site's switch: no game reading
      bool upload = !saveOnly && SyncClient.IsValidCode(cfg.Code);
      if (auto && !upload) return;
      string code = cfg.Code, site = cfg.Site; bool copy = !auto && (cfg.SaveCopy || !upload);
      syncing = true; lastSyncStart = DateTime.UtcNow;
      var t = new Thread(() => {
        autoRun = auto;
        try { RunSync(upload, copy, site, code); }
        catch (Exception e) { Log.Write("sync: " + e); SyncError("read", e.GetType().Name + ": " + e.Message, 0); }
        finally { syncing = false; }
      });
      t.IsBackground = true;
      t.Start();
    }

    void SyncError(string kind, string detail, int retryAfter, bool report = true) {
      SyncEv("error", ",\"error\":{\"kind\":\"" + kind + "\",\"detail\":" + S(detail) + ",\"retryAfter\":" + N(retryAfter) + "}");
      // reported: reading and sending failures. Not: the site's rate limit, the game not running (the player's state) and
      // a rejected code (the report would be rejected the same way)
      if (report && kind != "rate" && kind != "not_running" && kind != "token" && kind != "paused")
        Report("sync." + kind, "sync: " + kind + (string.IsNullOrEmpty(detail) ? "" : ": " + detail), Ctx("auto", autoRun ? "true" : "false"));
    }

    void RunSync(bool upload, bool copy, string site, string code) {
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
        default:   // a manual sync before the game has loaded the account (no heroes and no items): a calm "still loading" card, no report
          if (ex.Error == ExtractError.NoAccountData) { SyncError("loading", null, 0, false); return; }
          SyncError("read", ex.Detail, 0); return;
      }
      int seconds = (int)(DateTime.UtcNow - started).TotalSeconds;
      lastAccount = ex.Json; lastAccountAt = DateTime.UtcNow;
      guildWatch.AfterSync();
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
      int heroes = ex.Heroes, items = ex.Items, arts = ex.Artifacts;
      string viewUrl = null;
      if (upload) {
        SyncEv("send", null);
        var r = SyncClient.Send(site, code, ex.Json);
        Log.Write("send: " + r.Status + " " + r.HttpCode + " " + (r.Details ?? "") + (r.Retried ? " (sent again on a fresh connection after the first was closed)" : ""));
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
            if (r.HttpCode >= 500 && !serverRetried) {
              // one quick retry; a 5xx is mostly the site restarting (a deploy), so it is reported only when the retry fails too
              serverRetried = true;
              win.BeginInvoke((Action)(() => RequestAutoSync(ServerRetrySec)));
              SyncError("server", "HTTP " + r.HttpCode, 0, report: false); return;
            }
            SyncError("server", "HTTP " + r.HttpCode, 0); return;
          default:
            if (!serverRetried) {   // the connection dropped: one quick retry, reported only when the retry fails too
              serverRetried = true;
              win.BeginInvoke((Action)(() => RequestAutoSync(ServerRetrySec)));
              SyncError("net", r.Details, 0, report: false); return;
            }
            SyncError("net", r.Details, 0); return;
        }
        serverRetried = false;
        gradeWatch.Flush(site, code);
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

    // the unified «What's new» feed of the site; the page falls back to its built-in list when the reply is {"ok":false}
    void LoadNews(string lang) {
      string site = cfg.Site;
      Task.Factory.StartNew(() => {
        var feed = NewsClient.Fetch(site, lang);
        Post(feed == null ? "{\"ev\":\"news\",\"ok\":false}" : "{\"ev\":\"news\",\"ok\":true,\"feed\":" + feed + "}");
      });
    }

    // new replies in the player's community topics; the page never sees the sync code
    void LoadForum() {
      string site = cfg.Site, code = cfg.Code;
      Task.Factory.StartNew(() => { Post("{\"ev\":\"forum\",\"r\":" + NewsClient.Unread(site, code) + "}"); });
    }

    volatile string plansLogged;
    void LoadPlans(string lang) {
      string site = cfg.Site, code = cfg.Code;
      if (!SyncClient.IsValidCode(code)) { Post("{\"ev\":\"plans\",\"status\":\"invalid_token\"}"); return; }
      Task.Factory.StartNew(() => {
       try {
        var r = PlansClient.List(site, code, lang == "en" ? "en" : "ru");
        // set and main stat of every plan item: the auto-pilot sets the game's gear filter with them
        if (r.Status == PlansStatus.Ok) {
          var info = new List<long[]>();
          foreach (var p in r.Plans) foreach (var it in p.Items)
            info.Add(new[] { it.Uid, it.SetId, it.Main.Count > 0 && it.Main[0].Stat > 0 ? it.Main[0].Stat : 0 });
          win.BeginInvoke((Action)(() => { foreach (var x in info) overlay.SetItemInfo(x[0], x[1], x[2]); }));
          // the log names the plans the app holds (once per change: the list is asked for every few seconds)
          var pk = new StringBuilder();
          foreach (var p in r.Plans) pk.Append(pk.Length > 0 ? ", " : "").Append(p.HeroName).Append(" (").Append(p.HeroUid).Append(") ").Append(p.Items.Count).Append(" items");
          string plansNow = r.Plans.Count + (r.Plans.Count > 0 ? ": " + pk : "");
          if (plansNow != plansLogged) { plansLogged = plansNow; Log.Write("equip plans " + plansNow); }
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
            ItemJson(sb, it, true);
          }
          sb.Append(']');
          if (p.Artifact != null) sb.Append(",\"artifact\":{\"uid\":").Append(N(p.Artifact.Uid)).Append(",\"id\":").Append(N(p.Artifact.Id)).Append(",\"name\":").Append(S(p.Artifact.Name ?? "")).Append('}');
          sb.Append('}');
        }
        Post(sb.Append("]}").ToString());
        // a «Найти в игре» request from the site rides on the same reply
        var lq = r.Locate; lastLocate = lq;
        var lb = new StringBuilder("{\"ev\":\"locate\",\"req\":");
        if (lq == null) lb.Append("null");
        else {
          lb.Append("{\"id\":").Append(S(lq.Id)).Append(",\"purpose\":").Append(S(lq.Purpose)).Append(",\"createdAt\":").Append(S(lq.CreatedAt))
            .Append(",\"heroUid\":").Append(N(lq.HeroUid)).Append(",\"heroName\":").Append(S(lq.HeroName)).Append(",\"item\":");
          if (lq.Item == null) lb.Append("null"); else ItemJson(lb, lq.Item, false);
          if (lq.Purpose == "artPromote") {
            lb.Append(",\"art\":");
            var aq = lq.Art;
            if (aq == null) lb.Append("null");
            else lb.Append("{\"uid\":").Append(N(aq.Uid)).Append(",\"id\":").Append(N(aq.Id)).Append(",\"name\":").Append(S(aq.Name)).Append(",\"lv\":").Append(N(aq.Level))
              .Append(",\"cap\":").Append(N(aq.Cap)).Append(",\"st\":").Append(N(aq.Stage)).Append(",\"need\":").Append(N(aq.Need)).Append(",\"via\":").Append(S(aq.Via)).Append('}');
          }
          lb.Append('}');
        }
        Post(lb.Append('}').ToString());
       } catch (Exception e) {
        // the page waits for this event («Загружаю сборки…»): a failure must still answer it
        Log.Write("plans load failed: " + e.GetType().Name + ": " + e.Message);
        Post("{\"ev\":\"plans\",\"status\":\"error\",\"detail\":" + S(e.Message) + "}");
       }
      });
    }

    volatile LocateRequest lastLocate;

    // one plan item as the page's card shows it
    static void ItemJson(StringBuilder sb, PlanItem it, bool withCur) {
      sb.Append("{\"slot\":").Append(N(it.Slot)).Append(",\"uid\":").Append(N(it.Uid)).Append(",\"slotName\":").Append(S(it.SlotName))
        .Append(",\"name\":").Append(S(it.Name)).Append(",\"setName\":").Append(S(it.SetName)).Append(",\"level\":").Append(N(it.Level))
        .Append(",\"stars\":").Append(N(it.Stars)).Append(",\"mainStat\":").Append(S(it.MainStat)).Append(",\"fromHeroUid\":").Append(N(it.FromHeroUid))
        .Append(",\"fromHeroName\":").Append(S(it.FromHeroName)).Append(",\"icon\":").Append(S(it.Icon)).Append(",\"setIcon\":").Append(S(it.SetIcon))
        .Append(",\"setId\":").Append(N(it.SetId))
        .Append(",\"subs\":").Append(LinesJson(it.Subs, "rolls")).Append(",\"setBonus\":").Append(LinesJson(it.SetBonus, "pieces"))
        .Append(",\"main\":").Append(LinesJson(it.Main, "rolls")).Append(",\"quality\":").Append(N(it.Quality)).Append(",\"qualityName\":").Append(S(it.QualityName));
      if (withCur) {
        if (it.CurKnown && it.Cur == null) sb.Append(",\"cur\":null");
        else if (it.CurKnown) sb.Append(",\"cur\":{\"uid\":").Append(N(it.Cur.Uid)).Append(",\"name\":").Append(S(it.Cur.Name)).Append(",\"level\":").Append(N(it.Cur.Level))
               .Append(",\"stars\":").Append(N(it.Cur.Stars)).Append(",\"icon\":").Append(S(it.Cur.Icon))
               .Append(",\"mainStat\":").Append(S(it.Cur.MainStat)).Append(",\"subs\":").Append(LinesJson(it.Cur.Subs, "rolls"))
               .Append(",\"main\":").Append(LinesJson(it.Cur.Main, "rolls")).Append(",\"quality\":").Append(N(it.Cur.Quality))
               .Append(",\"qualityName\":").Append(S(it.Cur.QualityName)).Append('}');
      }
      sb.Append('}');
    }

    // «Найти в игре»: the overlay's step for the page (it composes the texts)
    void PostLocateState(LocateStep st, SubCheck subs) {
      var sb = new StringBuilder("{\"ev\":\"locate.state\",\"step\":").Append(S(st.Key)).Append(",\"pos\":").Append(N(st.Pos)).Append(",\"row\":").Append(N(st.Row))
        .Append(",\"col\":").Append(N(st.Col)).Append(",\"count\":").Append(N(st.Count)).Append(",\"onHero\":").Append(B(st.OnHero)).Append(",\"onGear\":").Append(B(st.OnGear)).Append(",\"onGem\":").Append(B(st.OnGem)).Append(",\"func\":").Append(N(st.Func)).Append(",\"wrongFunc\":").Append(B(st.WrongFunc)).Append(",\"worn\":").Append(B(st.Worn)).Append(",\"scroll\":").Append(S(st.Scroll)).Append(",\"scrollRows\":").Append(N(st.ScrollRows))
        .Append(",\"filterGo\":").Append(S(st.FilterGo)).Append(",\"suitTab\":").Append(N(st.SuitTab))
        .Append(",\"artCap\":").Append(N(st.ArtCap)).Append(",\"artLevel\":").Append(N(st.ArtLevel)).Append(",\"matNeed\":").Append(N(st.MatNeed)).Append(",\"matChosen\":").Append(N(st.MatChosen))
        .Append(",\"matKind\":").Append(S(st.MatKind)).Append(",\"matWrong\":").Append(B(st.MatWrong))
        .Append(",\"needClick\":").Append(B(st.NeedClick)).Append(",\"auto\":").Append(S(st.Auto)).Append(",\"autoWhy\":").Append(S(st.AutoWhy)).Append(",\"missing\":[");
      for (int i = 0; i < st.Missing.Length; i++) { if (i > 0) sb.Append(','); sb.Append(S(st.Missing[i])); }
      sb.Append("],\"subs\":");
      if (subs == null) sb.Append("null");
      else { sb.Append('['); for (int i = 0; i < subs.Match.Length; i++) { if (i > 0) sb.Append(','); sb.Append(B(subs.Match[i])); } sb.Append(']'); }
      sb.Append(",\"changed\":").Append(B(subs != null && subs.Changed)).Append('}');
      Post(sb.ToString());
    }

    // storage cleanup without auto click: the hint's step for the page (it composes the texts)
    void PostSellHint(SellHintStep st) {
      Post("{\"ev\":\"sell.hint\",\"step\":" + S(st.Key) + ",\"selected\":" + N(st.Selected) + ",\"total\":" + N(st.Total) + ",\"missing\":" + N(st.Missing)
        + ",\"extra\":" + N(st.Extra) + ",\"below\":" + N(st.Below) + ",\"above\":" + N(st.Above) + "}");
    }

    // keep: the request is reported but its hints stay on the screen a little longer (the page stops them: locate.stop)
    void FinishLocate(string id, bool done, bool keep = false) {
      string site = cfg.Site, code = cfg.Code;
      if (!keep) overlay.SetLocate(null);
      if (string.IsNullOrEmpty(id) || !SyncClient.IsValidCode(code)) return;
      Task.Factory.StartNew(() => FinishBackground("locate", id, done, () => LocateClient.Finish(site, code, id, done).Status));
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
      if (scanning || (uids.Count == 0 && !ahead) || !remote.ReadEnabled) return;
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

    void Watch(List<long> uids, bool fresh = false) {
      // a new or re-sent plan: the game's tables are read again (the earlier scan may hold stale ones), at most every 20 s
      if (fresh && addrs != null && !scanning && remote.ReadEnabled && DateTime.UtcNow > rescanAfter) {
        rescanAfter = DateTime.UtcNow.AddSeconds(20); Log.Write("equip scan again: a new plan was received");
        StartScan(uids, false, true); return;
      }
      RFX.EnsureLiveForGuides();
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

    // The plan stands on «open the hero» while the game's hero screen is open (another hero, or none read): after 10 s the
    // game's tables are read again, with or without the auto-click (live 1.6.61: the hero was not seen until a restart)
    DateTime heroStepSince = DateTime.MinValue; long heroFromFormLogged;
    bool HeroStepStuck(EquipLive s) {
      long want = overlay.HeroStepHero();
      if (want <= 0 || !s.GameRunning || !s.FormOk || s.HeroUid == want) { heroStepSince = DateTime.MinValue; return false; }
      var now = DateTime.UtcNow;
      if (heroStepSince == DateTime.MinValue) { heroStepSince = now; return false; }
      if ((now - heroStepSince).TotalSeconds < 10 || now <= rescanAfter) return false;
      heroStepSince = now; rescanAfter = now.AddSeconds(20);
      Log.Write("equip scan again: hero step stands still (game hero " + s.HeroUid + ", plan hero " + want + ")");
      return true;
    }

    void PollLive() {
      if (addrs == null || scanning || !remote.ReadEnabled) return;
      EquipLive s;
      try { s = RFX.Poll(addrs, watch); }
      catch (Exception e) {
        // (was swallowed: a poll failing every time froze the page's guide on one step)
        string m = e.GetType().Name + ": " + e.Message;
        if (m != pollError) { pollError = m; Log.Write("live poll failed: " + e); Report("live_poll_failed", "live poll failed: " + m, Ctx("exception", e.ToString())); }
        return;
      }
      pollError = null;
      overlay.PanelOk = s.PanelOk;
      // the hero screen is open, but the scan ran before the game built it (the player was in the city): scan again, at
      // most once a minute - without the form the hero cannot be brought up and the gear list is not known to be open
      if (s.GameRunning && s.HeroUid > 0 && addrs.Form == 0 && DateTime.UtcNow > rescanAfter) {
        rescanAfter = DateTime.UtcNow.AddSeconds(60); Log.Write("equip scan again: the hero screen is open now"); StartScan(watch); return;
      }
      if (HeroStepStuck(s)) { StartScan(watch, false, true); return; }
      if (s.HeroFromForm) { if (heroFromFormLogged != s.HeroUid) { heroFromFormLogged = s.HeroUid; Log.Write("live: hero " + s.HeroUid + " from the hero screen (selected hero: " + (s.HeroSelUid > 0 ? s.HeroSelUid.ToString() : "none") + ")"); } }
      else heroFromFormLogged = 0;
      bool inBulk; string scrName = overlay.ScreenName(out inBulk);   // the overlay's screen of this tick (src/GameScreen.cs)
      var sb = new StringBuilder("{\"ev\":\"equip.live\",\"live\":{");
      sb.Append("\"gameRunning\":").Append(B(s.GameRunning)).Append(",\"heroUid\":").Append(N(s.HeroUid)).Append(",\"panelOk\":").Append(B(s.PanelOk))
        .Append(",\"form\":").Append(B(s.FormOk)).Append(",\"tab\":").Append(N(s.Tab))
        .Append(",\"part\":").Append(N(s.Part)).Append(",\"hideEquipped\":").Append(B(s.HideEquipped)).Append(",\"hideEnhanced\":").Append(B(s.HideEnhanced))
        .Append(",\"filterActive\":").Append(B(s.FilterActive)).Append(",\"selUid\":").Append(N(s.SelUid)).Append(",\"selRow\":").Append(N(s.SelRow))
        .Append(",\"selCol\":").Append(N(s.SelCol)).Append(",\"selOwner\":").Append(N(s.SelOwner))
        .Append(",\"screen\":").Append(S(scrName)).Append(",\"bulk\":").Append(B(inBulk)).Append(",\"rows\":{");
      bool first = true;
      foreach (var kv in s.Row) {
        int col; s.Col.TryGetValue(kv.Key, out col);
        if (!first) sb.Append(','); first = false;
        sb.Append('"').Append(N(kv.Key)).Append("\":[").Append(N(kv.Value)).Append(',').Append(N(col)).Append(']');
      }
      sb.Append("},\"owner\":{"); first = true;
      foreach (var kv in s.Owner) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(N(kv.Key)).Append("\":").Append(N(kv.Value)); }
      sb.Append('}');
      string artLive = overlay.ArtLive();   // the artifact step of a plan with an artifact (src/ArtEquip.cs)
      if (artLive != null) sb.Append(",\"art\":").Append(artLive);
      sb.Append("}}");
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
      string site = cfg.Site, code = cfg.Code;
      if (string.IsNullOrEmpty(id) || !SyncClient.IsValidCode(code)) return;
      Task.Factory.StartNew(() => {
        var st = FinishBackground("plan", id, done, () => PlansClient.Finish(site, code, id, done).Status);
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
          sb.Append("{\"id\":").Append(S(r.List.Id)).Append(",\"kind\":").Append(S(r.List.Kind)).Append(",\"createdAt\":").Append(S(r.List.CreatedAt)).Append(",\"items\":[");
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
        var st = FinishBackground("sell list", id, done, () => SellClient.Finish(site, code, id, done).Status);
        if (done) win.BeginInvoke((Action)(() => RequestAutoSync(1)));
      });
    }

    // Reports a finished / cancelled request to the site from a pool thread; never blocks the page or the next job. A «cancelled» that
    // did not get through (site busy or unreachable) is repeated every 10 s for up to 5 min (a newer report of the same request replaces it).
    readonly Dictionary<string, int> finishGen = new Dictionary<string, int>();
    PlansStatus FinishBackground(string kind, string id, bool done, Func<PlansStatus> send) {
      string key = kind + " " + id; int gen;
      lock (finishGen) { finishGen.TryGetValue(key, out gen); finishGen[key] = ++gen; }
      var sw = Stopwatch.StartNew(); int n = 0; PlansStatus st;
      while (true) {
        st = send(); n++;
        Log.Write(key + " " + (done ? "done" : "cancelled") + ": " + st + (n > 1 ? " (try " + n + ")" : ""));
        if (done || !PlansClient.RetryableCancel(st) || sw.ElapsedMilliseconds + PlansClient.CancelRetryMs > PlansClient.CancelRetryTotalMs) break;
        Thread.Sleep(PlansClient.CancelRetryMs);
        int cur; lock (finishGen) { if (!finishGen.TryGetValue(key, out cur) || cur != gen) return st; }   // superseded by a newer report
      }
      lock (finishGen) { int cur; if (finishGen.TryGetValue(key, out cur) && cur == gen) finishGen.Remove(key); }
      return st;
    }
  }
}
