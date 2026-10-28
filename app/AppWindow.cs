// Wardsage.exe - the window: a WebView2 filling a dark-framed form; «Поверх игры» = compact always-on-top mode.
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RealmForge {
  static class Native {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    // Dark title bar (Windows 10 2004+ / 11) and the Windows 11 caption color matching the app background.
    public static void DarkFrame(IntPtr h) {
      try {
        int on = 1;
        if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
        int caption = 0x001B100B;   // COLORREF 0x00BBGGRR of #0b101b
        DwmSetWindowAttribute(h, 35, ref caption, 4);
        int border = 0x005CA4C9;    // #c9a45c
        DwmSetWindowAttribute(h, 34, ref border, 4);
      } catch (Exception) { }
    }
  }

  static class Shell {
    // Opens a web page in the user's normal (non-elevated) browser: through the already running Explorer,
    // because this process runs as administrator (needed to read the game memory).
    public static void OpenUrl(string url) {
      Uri u;
      if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
      try { System.Diagnostics.Process.Start("explorer.exe", "\"" + u.AbsoluteUri + "\""); }
      catch (Exception) { try { System.Diagnostics.Process.Start(u.AbsoluteUri); } catch (Exception) { } }
    }
    public static void OpenFile(string path) {
      try { if (File.Exists(path)) System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); } catch (Exception) { }
    }
    public static void OpenFolder(string path) {
      try {
        if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
        else if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
      } catch (Exception) { }
    }
  }

  static class WebViewCheck {
    public static string RuntimeVersion(string loaderDir) {
      try {
        try { CoreWebView2Environment.SetLoaderDllFolderPath(loaderDir); } catch (Exception) { }
        return CoreWebView2Environment.GetAvailableBrowserVersionString();
      } catch (Exception e) { Log.Write("WebView2 runtime: " + e.Message); return null; }
    }
  }

  sealed class AppWindow : Form {
    const string AppHost = "app.realmforge";
    WebView2 web = new WebView2();
    readonly RecoveryLimiter recoveries = new RecoveryLimiter(5, TimeSpan.FromMinutes(10));
    HostBridge bridge;
    Rectangle normalBounds;
    FormWindowState normalState;
    bool compact;
    // the tray (app/Tray.cs): the close button hides the window there while TrayEnabled (minimizing keeps it on the taskbar);
    // a start with --tray begins hidden
    readonly NotifyIcon tray = new NotifyIcon();
    bool startHidden;
    FormWindowState shownState = FormWindowState.Normal;   // normal or maximized before it went to the tray
    /// <summary>Close to the tray (the player's setting, AppConfig.Tray; set by HostBridge).</summary>
    public bool TrayEnabled = true;
    string trayOpen = "Открыть Wardsage", trayExit = "Выход";
    string trayHint = "Wardsage работает в трее. Выход — правой кнопкой по значку.";
    // a real exit (the tray menu, a fatal error): the close button otherwise only hides the window in the tray
    bool exiting;
    bool hintShown;
    // the startup splash over the WebView until the page says «init» (HideSplash)
    Splash splash;
    readonly System.Windows.Forms.Timer splashGuard = new System.Windows.Forms.Timer();
    // the plain panel in place of a black window when the built-in browser cannot start or render
    readonly System.Windows.Forms.Timer readyGuard = new System.Windows.Forms.Timer();
    readonly System.Diagnostics.Stopwatch navClock = new System.Diagnostics.Stopwatch();
    bool navStarted, navDone, pageReady, fallbackShown;

    public AppWindow(bool hidden = false) {
      startHidden = hidden;
      Text = Channel.WindowTitle;
      BackColor = Color.FromArgb(11, 16, 27);
      AutoScaleMode = AutoScaleMode.Dpi;
      MinimumSize = new Size(880, 600);
      Size = new Size(1100, 720);
      StartPosition = FormStartPosition.CenterScreen;
      RestoreAfterUpdate();
      try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
      PrepareWeb(web);
      Controls.Add(web);
      splash = new Splash();
      Controls.Add(splash);
      splash.BringToFront();
      Load += async (s, e) => await Init();
      SetupTray();
      // hidden start: the window is made (WebView2, timers) but never seen — fully transparent and off the taskbar for
      // the first show, then hidden
      if (startHidden) {
        Opacity = 0;
        ShowInTaskbar = false;
        Shown += (s, e) => { if (startHidden) { startHidden = false; Hide(); Opacity = 1; ShowInTaskbar = true; } };
      }
      ShowSignal.Listen(() => { try { BeginInvoke((Action)ShowFromTray); } catch (Exception) { } });
    }

    /// <summary>The page said «init»: it works. Cancels the not-opened watchdog and removes the splash (any thread).</summary>
    public void PageReady() {
      pageReady = true;
      if (!IsDisposed && !InvokeRequired) readyGuard.Stop();
      HideSplash();
    }

    /// <summary>Removes the startup splash (any thread; safe to call again).</summary>
    public void HideSplash() {
      if (IsDisposed) return;
      if (InvokeRequired) { try { BeginInvoke((Action)HideSplash); } catch (Exception) { } return; }
      splashGuard.Stop();
      var sp = splash;
      if (sp == null) return;
      splash = null;
      try { sp.Hide(); Controls.Remove(sp); sp.Dispose(); } catch (Exception) { }
    }

    /// <summary>The splash again (a recovery restart with a fresh control).</summary>
    public void ShowSplash() {
      if (IsDisposed) return;
      if (splash == null || splash.IsDisposed) { splash = new Splash(); Controls.Add(splash); }
      splash.Show();
      splash.BringToFront();
    }

    void SetupTray() {
      try { tray.Icon = Icon ?? SystemIcons.Application; } catch (Exception) { tray.Icon = SystemIcons.Application; }
      tray.Text = Channel.WindowTitle;
      tray.Visible = true;
      tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
      var menu = new ContextMenuStrip();
      menu.Opening += (s, e) => { menu.Items[0].Text = trayOpen; menu.Items[1].Text = trayExit; };
      menu.Items.Add(trayOpen, null, (s, e) => ShowFromTray());
      menu.Items.Add(trayExit, null, (s, e) => { exiting = true; tray.Visible = false; Close(); });
      tray.ContextMenuStrip = menu;
    }

    /// <summary>The tray menu's words in the app's language.</summary>
    public void SetTrayLang(string lang) {
      bool en = lang == "en";
      trayOpen = en ? "Open Wardsage" : "Открыть Wardsage";
      trayExit = en ? "Exit" : "Выход";
      trayHint = en ? "Wardsage keeps running in the tray. Exit: right-click the icon." : "Wardsage работает в трее. Выход — правой кнопкой по значку.";
    }

    /// <summary>The window back from the tray (or to the front), as it was.</summary>
    public void ShowFromTray() {
      startHidden = false;
      Opacity = 1;
      ShowInTaskbar = true;
      if (!Visible) Show();
      if (WindowState == FormWindowState.Minimized) WindowState = shownState;
      Activate();
    }

    protected override void OnResize(EventArgs e) {
      base.OnResize(e);
      if (WindowState != FormWindowState.Minimized) shownState = WindowState;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkFrame(Handle); }

    // A restart for an update in a quiet moment (HostBridge.TryAutoUpdate): the window comes back where and as it was —
    // without taking the focus from the game. The state goes through a file (the updater starts the new exe itself).
    static string RestartFile { get { return System.IO.Path.Combine(AppConfig.DefaultDir, "restart-window.txt"); } }
    bool quiet;
    protected override bool ShowWithoutActivation { get { return quiet; } }

    public void SaveForRestart() {
      try {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        System.IO.File.WriteAllText(RestartFile, string.Join(",", new[] { b.X, b.Y, b.Width, b.Height, (int)WindowState, TopMost ? 1 : 0, Form.ActiveForm == this ? 1 : 0, Visible && !startHidden ? 1 : 0 }));
      } catch (Exception e) { Log.Write("restart state: " + e.Message); }
    }

    void RestoreAfterUpdate() {
      try {
        var f = RestartFile;
        if (!System.IO.File.Exists(f)) return;
        bool fresh = (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(f)).TotalSeconds < 90;
        var v = System.IO.File.ReadAllText(f).Split(',');
        System.IO.File.Delete(f);
        if (!fresh || v.Length < 7) return;
        var n = Array.ConvertAll(v, int.Parse);
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(n[0], n[1], n[2], n[3]);
        WindowState = (FormWindowState)n[4];
        TopMost = n[5] == 1;
        quiet = n[6] == 0;   // it was not the active window: do not take the focus
        if (n.Length > 7 && n[7] == 0) { startHidden = true; WindowState = FormWindowState.Normal; }   // it was in the tray: back there
      } catch (Exception e) { Log.Write("restart state: " + e.Message); }
    }

    static void PrepareWeb(WebView2 w) {
      w.Dock = DockStyle.Fill;
      w.DefaultBackgroundColor = Color.FromArgb(11, 16, 27);
    }

    // a fresh WebView2 control in place of the current one (a control whose start failed or whose browser died is not reused)
    void ReplaceWeb() {
      var old = web;
      var w = new WebView2();
      PrepareWeb(w);
      Controls.Add(w);
      if (splash != null && !splash.IsDisposed) splash.BringToFront();
      web = w;
      try { Controls.Remove(old); } catch (Exception) { }
      try { old.Dispose(); } catch (Exception) { }
    }

    // The start of the built-in browser. Windows may start the program before it can run it (autostart after a reboot, a
    // wake from sleep): the start then fails with «Operation aborted» (E_ABORT) and the like. Such errors are retried for
    // about 10 minutes (2, 5, 15, 30 s, then every minute) with a fresh control; only then the player is told.
    async System.Threading.Tasks.Task Init(bool fresh = false) {
      Exception last = null;
      for (int attempt = 1; ; attempt++) {
        if (IsDisposed || exiting) return;
        last = null;
        try { await StartWeb(fresh || attempt > 1); return; }
        catch (Exception e) { last = e; }
        bool retry = WebViewRecovery.IsTransient(last) && attempt <= WebViewRecovery.MaxRetries;
        Log.Write("WebView2 start " + attempt + " failed: " + last.GetType().Name + " " + WebViewRecovery.HResultHex(last) + " " + last.Message + (retry ? " - retrying" : " - giving up"));
        if (!retry) break;
        if (splash != null) splash.SetStatus("Жду, пока Windows будет готова…", "Waiting for Windows to be ready…");
        await System.Threading.Tasks.Task.Delay(WebViewRecovery.DelayMs(attempt));
      }
      GiveUp(last);
    }

    // the browser cannot start or render: a plain panel with the reason instead of a black window; the program (sync etc.) keeps running
    void GiveUp(Exception e) {
      ShowFallback(e == null ? "WebView2 start failed" : e.GetType().Name + " " + WebViewRecovery.HResultHex(e) + " " + e.Message);
    }

    void ShowFallback(string reason) {
      if (IsDisposed) return;
      if (InvokeRequired) { try { BeginInvoke((Action)(() => ShowFallback(reason))); } catch (Exception) { } return; }
      if (fallbackShown) return;
      fallbackShown = true;
      Log.Write("WebView2 window not shown: " + reason);
      readyGuard.Stop();
      HideSplash();
      string lang = "ru";
      try { lang = AppConfig.Load().Lang; } catch (Exception) { }
      ReportFallback(reason);
      var back = Color.FromArgb(11, 16, 27);
      var panel = new Panel { Dock = DockStyle.Fill, BackColor = back };
      var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = back, Padding = new Padding(24) };
      int w = 560;
      Func<string, float, Color, Label> label = (text, size, color) => new Label {
        Text = text, AutoSize = true, MaximumSize = new Size(w, 0), ForeColor = color, BackColor = back,
        Font = new Font(SystemFonts.MessageBoxFont.FontFamily, size), Margin = new Padding(0, 0, 0, 14)
      };
      box.Controls.Add(label(WebViewRecovery.FallbackTitle(lang), 12f, Color.FromArgb(232, 226, 210)));
      box.Controls.Add(label(WebViewRecovery.FallbackHint(lang), 10f, Color.FromArgb(180, 176, 165)));
      box.Controls.Add(label(WebViewRecovery.FallbackReason(lang, reason), 8.5f, Color.FromArgb(130, 130, 130)));
      var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, BackColor = back, Margin = new Padding(0) };
      bool en = lang == "en";
      var download = new Button { Text = en ? "Download WebView2 Runtime" : "Скачать WebView2 Runtime", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
      download.Click += (s, e) => {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WebViewRecovery.RuntimeUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open download page: " + ex.Message); }
      };
      var logs = new Button { Text = en ? "Open log folder" : "Открыть папку с журналом", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
      logs.Click += (s, e) => Shell.OpenFolder(File.Exists(Log.Path) ? Log.Path : AppConfig.DefaultDir);
      row.Controls.Add(download);
      row.Controls.Add(logs);
      box.Controls.Add(row);
      panel.Controls.Add(box);
      box.Location = new Point(Scale(24), Scale(24));
      try { web.Visible = false; } catch (Exception) { }
      Controls.Add(panel);
      panel.BringToFront();
    }

    // one error report per run (the bridge may not exist yet when the start failed, so this reads the config itself); same switches as HostBridge
    static void ReportFallback(string reason) {
      System.Threading.Tasks.Task.Factory.StartNew(() => {
        try {
          var cfg = AppConfig.Load();
          if (!cfg.ErrorReports || !SyncClient.IsValidCode(cfg.Code)) return;
          string json = ErrorReports.BuildJson("webview.fallback", reason, Program.Version, null, null, ErrorReports.ReadLogTail(Log.Path),
                                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
          Log.Write("error report webview.fallback: " + ErrorReports.Send(cfg.Site, cfg.Code, json, ErrorReports.TimeoutMs));
        } catch (Exception ex) { Log.Write("error report webview.fallback: " + ex.Message); }
      });
    }

    // the watchdog tick: the page has not said «ready» in time (see WebViewRecovery.ShouldShowFallback)
    void OnReadyGuard(object s, EventArgs e) {
      double secs = navStarted ? navClock.Elapsed.TotalSeconds : -1;
      if (!WebViewRecovery.ShouldShowFallback(false, pageReady, navDone, secs)) return;
      ShowFallback(secs >= WebViewRecovery.HardSeconds && !navDone
        ? "the page did not finish loading in " + WebViewRecovery.HardSeconds + " s"
        : "the page did not signal ready in " + WebViewRecovery.ReadySeconds + " s");
    }

    async System.Threading.Tasks.Task StartWeb(bool fresh) {
      if (fresh) ReplaceWeb();
      pageReady = false;
      // WebView2 needs the control's window; a form that started hidden in the tray has it too, but make sure
      if (!web.IsHandleCreated) { web.CreateControl(); var h = web.Handle; }
      var env = await CoreWebView2Environment.CreateAsync(null, Program.DataDir, null);
      await web.EnsureCoreWebView2Async(env);
      var core = web.CoreWebView2;
      var st = core.Settings;
      bool dev = Environment.GetEnvironmentVariable("REALMFORGE_DEVTOOLS") == "1";
      st.AreDevToolsEnabled = dev;
      st.AreDefaultContextMenusEnabled = dev;
      st.IsZoomControlEnabled = false;
      st.IsStatusBarEnabled = false;
      st.AreBrowserAcceleratorKeysEnabled = dev;
      st.IsGeneralAutofillEnabled = false;
      st.IsPasswordAutosaveEnabled = false;
      core.SetVirtualHostNameToFolderMapping(AppHost, Program.UiDir, CoreWebView2HostResourceAccessKind.Deny);
      // only our own page may load in the window; links go to the user's browser
      core.NavigationStarting += (s, e) => {
        Uri u;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out u) && u.Host == AppHost) { navStarted = true; navDone = false; navClock.Restart(); return; }
        e.Cancel = true;
        Shell.OpenUrl(e.Uri);
      };
      core.NewWindowRequested += (s, e) => { e.Handled = true; Shell.OpenUrl(e.Uri); };
      if (bridge != null) { try { bridge.Dispose(); } catch (Exception) { } bridge = null; }
      var br = new HostBridge(this, core);
      bridge = br;
      core.WebMessageReceived += (s, e) => {
        string json = null;
        try { json = e.WebMessageAsJson; } catch (Exception) { }
        if (json != null) br.OnMessage(json);
      };
      core.NavigationCompleted += (s, e) => { navDone = true; if (!e.IsSuccess) HideSplash(); };
      core.ProcessFailed += (s, e) => OnProcessFailed(core, e);
      // it must never stick: a page that fails or stays silent still gets shown 20 s after navigation (not before: a start that waits for Windows retries for minutes)
      splashGuard.Stop();
      splashGuard.Interval = 20000;
      splashGuard.Tick -= OnSplashGuard;
      splashGuard.Tick += OnSplashGuard;
      splashGuard.Start();
      navStarted = false;
      readyGuard.Stop();
      readyGuard.Interval = 1000;
      readyGuard.Tick -= OnReadyGuard;
      readyGuard.Tick += OnReadyGuard;
      readyGuard.Start();
      core.Navigate("https://" + AppHost + "/index.html");
    }

    void OnSplashGuard(object s, EventArgs e) { HideSplash(); }

    bool recovering;

    // the browser's processes died: a page process is reloaded, the browser process is started anew (the control is replaced)
    void OnProcessFailed(CoreWebView2 from, CoreWebView2ProcessFailedEventArgs e) {
      var kind = e.ProcessFailedKind;
      Log.Write("WebView2 process failed: " + kind + ", exit " + e.ExitCode);
      if (exiting || IsDisposed || web == null || web.CoreWebView2 != from) return;   // or an older, already replaced browser
      bool browser = kind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
      bool render = kind == CoreWebView2ProcessFailedKind.RenderProcessExited || kind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                    || kind == CoreWebView2ProcessFailedKind.FrameRenderProcessExited;
      if (!browser && !render) return;
      if (recovering) return;
      if (!recoveries.TryAcquire(DateTime.UtcNow)) {
        Log.Write("WebView2: too many recoveries, giving up");
        BeginInvoke((Action)(() => ShowFallback("WebView2 process failed: " + kind + ", exit " + e.ExitCode + " (renderer crashed, too many recoveries)")));
        return;
      }
      if (render) {
        try { from.Reload(); } catch (Exception ex) { Log.Write("WebView2 reload: " + ex.Message); }
        return;
      }
      recovering = true;
      BeginInvoke((Action)(async () => {
        try { ShowSplash(); await Init(true); }
        finally { recovering = false; }
      }));
    }

    // «Поверх игры»: a small always-on-top window at the right edge of the screen, and back.
    public void SetCompact(bool on) {
      if (on == compact) return;
      compact = on;
      if (on) {
        normalState = WindowState;
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        normalBounds = Bounds;
        MinimumSize = new Size(340, 420);
        var wa = Screen.FromControl(this).WorkingArea;
        int w = Scale(400), h = Scale(600);
        Bounds = new Rectangle(wa.Right - w - Scale(24), wa.Top + Scale(80), w, h);
        TopMost = true;
      } else {
        TopMost = false;
        MinimumSize = new Size(880, 600);
        Bounds = normalBounds;
        WindowState = normalState;
      }
    }

    int Scale(int px) { using (var g = CreateGraphics()) return (int)Math.Round(px * g.DpiX / 96.0); }

    // the close button (×): with the tray on, the window goes to the tray and the program keeps working (a player may only
    // want it out of the way); a real exit is the tray menu's «Выход» or Windows shutting down
    protected override void OnFormClosing(FormClosingEventArgs e) {
      if (TrayEnabled && !exiting && e.CloseReason == CloseReason.UserClosing) {
        e.Cancel = true;
        Hide();
        if (!hintShown) {
          hintShown = true;
          try { tray.ShowBalloonTip(4000, Channel.WindowTitle, trayHint, ToolTipIcon.Info); } catch (Exception) { }
        }
        return;
      }
      base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
      tray.Visible = false;
      tray.Dispose();
      if (bridge != null) bridge.Dispose();
      base.OnFormClosed(e);
    }
  }
}
