// RealmForge.exe - the window: a WebView2 filling a dark-framed form; «Поверх игры» = compact always-on-top mode.
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

    public AppWindow() {
      Text = "RealmForge";
      BackColor = Color.FromArgb(11, 16, 27);
      AutoScaleMode = AutoScaleMode.Dpi;
      MinimumSize = new Size(880, 600);
      Size = new Size(1100, 720);
      StartPosition = FormStartPosition.CenterScreen;
      try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
      web.Dock = DockStyle.Fill;
      web.DefaultBackgroundColor = Color.FromArgb(11, 16, 27);
      Controls.Add(web);
      Load += async (s, e) => await Init();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkFrame(Handle); }

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

    protected override void OnFormClosed(FormClosedEventArgs e) {
      if (bridge != null) bridge.Dispose();
      base.OnFormClosed(e);
    }
  }
}
