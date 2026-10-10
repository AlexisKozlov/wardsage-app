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
    readonly WebView2 web = new WebView2();
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
      web.Dock = DockStyle.Fill;
      web.DefaultBackgroundColor = Color.FromArgb(11, 16, 27);
      Controls.Add(web);
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

    async System.Threading.Tasks.Task Init() {
      try {
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
          if (Uri.TryCreate(e.Uri, UriKind.Absolute, out u) && u.Host == AppHost) return;
          e.Cancel = true;
          Shell.OpenUrl(e.Uri);
        };
        core.NewWindowRequested += (s, e) => { e.Handled = true; Shell.OpenUrl(e.Uri); };
        bridge = new HostBridge(this, core);
        core.WebMessageReceived += (s, e) => {
          string json = null;
          try { json = e.WebMessageAsJson; } catch (Exception) { }
          if (json != null) bridge.OnMessage(json);
        };
        core.Navigate("https://" + AppHost + "/index.html");
      } catch (Exception e) {
        Program.Fatal(e);
        exiting = true;
        Close();
      }
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
