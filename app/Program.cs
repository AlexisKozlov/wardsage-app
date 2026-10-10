// Wardsage.exe - entry point.
//
// A single executable: the interface (ui/*) and the WebView2 SDK (Microsoft.Web.WebView2.*.dll, WebView2Loader.dll)
// are embedded as resources and unpacked on start to %LOCALAPPDATA%\RealmForge\app\<build>\. The page is rendered by
// the WebView2 Runtime that ships with Windows 10/11 (Microsoft Edge). The account reader is the same read-only code
// as before (src/*.cs): OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ) + ReadProcessMemory.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace RealmForge {
  static class Program {
    public const string Version = "1.6.55";
    internal static string AppDir;          // unpacked resources of this build
    internal static string UiDir;
    internal static string DataDir;         // WebView2 user data (cache, local storage)
    static Mutex single;
    static bool startInTray;   // started by Windows (autostart, --tray): straight to the tray

    [STAThread]
    static int Main(string[] args) {
      { int di = Array.IndexOf(args, "--dump-guild"); if (di >= 0 && di + 1 < args.Length) { RFX.DumpGuild(args[di + 1]); return 0; } }   // diagnostics: the guild payload to a file, nothing sent
      bool created;
      single = new Mutex(true, Channel.MutexName, out created);
      // already running (maybe hidden in the tray): ask it to show its window
      if (!created) { if (!ShowSignal.Send()) BringOtherToFront(); return 0; }
      startInTray = Array.IndexOf(args, Autostart.TrayArg) >= 0;
      // a downloaded, signed update waits: put it in place and start it (app/Updater.cs)
      if (Updater.ApplyAtStart(ReleaseSingle)) return 0;
      // an install updated in place still runs as RealmForge.exe: becomes Wardsage.exe (once), the old file is removed later
      if (!Channel.IsTest && ExeRename.Run(args, ReleaseSingle)) return 0;

      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
      Application.ThreadException += (s, e) => Fatal(e.Exception);
      AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal(e.ExceptionObject as Exception);

      try { Unpack(); }
      catch (Exception e) { Fatal(e); return 1; }
      AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
        string name = new AssemblyName(e.Name).Name + ".dll";
        string path = Path.Combine(AppDir, name);
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
      };
      return Run();
    }

    // Separate method: the WebView2 types are touched only after AssemblyResolve is installed.
    static int Run() {
      string runtime = WebViewCheck.RuntimeVersion(AppDir);
      if (runtime == null) {
        var r = MessageBox.Show(
          "Для работы Wardsage нужен компонент Microsoft Edge WebView2 Runtime (он есть в Windows 11 и в обновлённой Windows 10).\n\n" +
          "Открыть страницу загрузки Microsoft?\n\n" +
          "Wardsage needs the Microsoft Edge WebView2 Runtime. Open the Microsoft download page?",
          Channel.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r == DialogResult.Yes) Shell.OpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        return 2;
      }
      Autostart.Refresh();
      if (!Channel.IsTest) ThreadPool.QueueUserWorkItem(delegate { ShortcutMigration.Run(); });
      using (var w = new AppWindow(startInTray)) Application.Run(w);
      return 0;
    }

    // Unpacks embedded resources into a folder named after their hash (a new build never reuses old files).
    static void Unpack() {
      var asm = Assembly.GetExecutingAssembly();
      var names = new List<string>(asm.GetManifestResourceNames());
      names.Sort(StringComparer.Ordinal);
      string hash;
      using (var sha = SHA256.Create()) {
        foreach (var n in names) using (var s = asm.GetManifestResourceStream(n)) {
          var b = ReadAll(s);
          sha.TransformBlock(b, 0, b.Length, null, 0);
        }
        sha.TransformFinalBlock(new byte[0], 0, 0);
        hash = BitConverter.ToString(sha.Hash, 0, 6).Replace("-", "").ToLowerInvariant();
      }
      string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Channel.FolderName);
      AppDir = Path.Combine(root, "app", Version + "-" + hash);
      UiDir = Path.Combine(AppDir, "ui");
      DataDir = Path.Combine(root, "WebView2");
      string done = Path.Combine(AppDir, ".complete");
      if (File.Exists(done)) return;
      Directory.CreateDirectory(UiDir);
      foreach (var n in names) {
        // resource names: "bin/<file>" -> AppDir, "ui/<path>" -> UiDir
        string rel = n.Replace('/', Path.DirectorySeparatorChar);
        string target = n.StartsWith("bin/", StringComparison.Ordinal) ? Path.Combine(AppDir, rel.Substring(4)) : Path.Combine(AppDir, rel);
        if (!target.StartsWith(AppDir, StringComparison.OrdinalIgnoreCase)) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        using (var s = asm.GetManifestResourceStream(n)) File.WriteAllBytes(target, ReadAll(s));
      }
      File.WriteAllText(done, Version);
      // older builds are left for the next start to clean (their files may still be in use right now)
      try {
        foreach (var d in Directory.GetDirectories(Path.Combine(root, "app")))
          if (!string.Equals(d, AppDir, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(d, true); } catch (Exception) { }
      } catch (Exception) { }
    }

    static void ReleaseSingle() { try { single.ReleaseMutex(); single.Dispose(); } catch (Exception) { } }

    /// <summary>«Перезапустить» after an update was downloaded: a fresh start of this exe installs it.</summary>
    internal static void RestartForUpdate() {
      ReleaseSingle();
      try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false }); }
      catch (Exception e) { Log.Write("restart: " + e.Message); return; }
      Application.Exit();
    }

    static byte[] ReadAll(Stream s) { using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }

    static void BringOtherToFront() {
      try {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
          if (p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero) { Native.ShowWindow(p.MainWindowHandle, 9); Native.SetForegroundWindow(p.MainWindowHandle); }
      } catch (Exception) { }
    }

    /// <summary>Sends an unhandled exception to the site when the player turned the error reports on (set by HostBridge).</summary>
    internal static Action<Exception> FatalReport;

    internal static void Fatal(Exception e) {
      string msg = e == null ? "unknown error" : e.GetType().Name + ": " + e.Message;
      try { Log.Write("FATAL " + (e == null ? msg : e.ToString())); } catch (Exception) { }
      var fr = FatalReport;
      if (fr != null && e != null) try { fr(e); } catch (Exception) { }
      MessageBox.Show(Channel.ProductName + ": " + msg, Channel.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }

  /// <summary>An in-place updated install never reruns the installer and keeps its «RealmForge» shortcuts: the ones that
  /// point at this exe are replaced by «Wardsage» ones (same target, arguments, folder, icon). Once, best effort, in the
  /// background; late COM binding through reflection (no extra references).</summary>
  static class ShortcutMigration {
    public static void Run() {
      try {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        object shell = Activator.CreateInstance(shellType);
        string exe = Application.ExecutablePath;
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory }) {
          try {
            string dir = Environment.GetFolderPath(folder);
            string old = System.IO.Path.Combine(dir, "RealmForge.lnk"), now = System.IO.Path.Combine(dir, "Wardsage.lnk");
            if (!File.Exists(old) || File.Exists(now)) continue;
            object src = Call(shell, "CreateShortcut", old);
            string target = Get(src, "TargetPath") as string;
            if (target == null || !string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) continue;
            object dst = Call(shell, "CreateShortcut", now);
            foreach (var p in new[] { "TargetPath", "Arguments", "WorkingDirectory", "IconLocation", "Description", "WindowStyle" }) {
              object v = Get(src, p);
              if (v != null) Set(dst, p, v);
            }
            Call(dst, "Save");
            if (File.Exists(now)) File.Delete(old);
            Log.Write("shortcut renamed: " + now);
          } catch (Exception e) { Log.Write("shortcut: " + e.Message); }
        }
      } catch (Exception e) { Log.Write("shortcut: " + e.Message); }
    }

    /// <summary>The exe was renamed: «RealmForge.lnk» / «Wardsage.lnk» in Programs and on the Desktop that point at the old
    /// exe are recreated as «Wardsage.lnk» pointing at the new one (same arguments and folder, icon from the new exe).</summary>
    public static void Retarget(string oldExe, string newExe) {
      Type shellType = Type.GetTypeFromProgID("WScript.Shell");
      if (shellType == null) return;
      object shell = Activator.CreateInstance(shellType);
      foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory }) {
        try {
          string dir = Environment.GetFolderPath(folder);
          string old = System.IO.Path.Combine(dir, "RealmForge.lnk"), now = System.IO.Path.Combine(dir, "Wardsage.lnk");
          foreach (var lnk in new[] { old, now }) {
            if (!File.Exists(lnk)) continue;
            object src = Call(shell, "CreateShortcut", lnk);
            string target = Get(src, "TargetPath") as string;
            if (target == null || !string.Equals(target, oldExe, StringComparison.OrdinalIgnoreCase)) continue;
            object args = Get(src, "Arguments"), wd = Get(src, "WorkingDirectory"), desc = Get(src, "Description"), ws = Get(src, "WindowStyle");
            object dst = Call(shell, "CreateShortcut", now);
            Set(dst, "TargetPath", newExe);
            if (args != null) Set(dst, "Arguments", args);
            if (wd != null) Set(dst, "WorkingDirectory", wd);
            if (desc != null) Set(dst, "Description", desc);
            if (ws != null) Set(dst, "WindowStyle", ws);
            Set(dst, "IconLocation", newExe + ",0");
            Call(dst, "Save");
            if (File.Exists(now) && !string.Equals(lnk, now, StringComparison.OrdinalIgnoreCase)) File.Delete(lnk);
            Log.Write("shortcut retargeted: " + now);
            break;
          }
        } catch (Exception e) { Log.Write("shortcut: " + e.Message); }
      }
    }

    static object Call(object o, string name, params object[] args) { return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }
    static object Get(object o, string name) { return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null); }
    static void Set(object o, string name, object v) { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new object[] { v }); }
  }

  /// <summary>One-time in-place rename of an install made under the old product name: RealmForge.exe is copied to
  /// Wardsage.exe, the shortcuts, the autostart entry and the «Apps» entry of Windows follow, then Wardsage.exe starts
  /// and this process exits. The install folder keeps its name (the player chose it; the uninstaller lives there).
  /// Best effort: any failure leaves the install as it was.</summary>
  static class ExeRename {
    const string OldExe = "RealmForge.exe", NewExe = "Wardsage.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B7E4C5A-3F21-4D8E-9B61-5A2C7E0D4F13}_is1";

    /// <summary>True = this process must exit now (the renamed copy was started).</summary>
    public static bool Run(string[] args, Action beforeStart) {
      string exe = Application.ExecutablePath;
      try {
        if (!string.Equals(Path.GetFileName(exe), OldExe, StringComparison.OrdinalIgnoreCase)) { CleanOld(Path.GetDirectoryName(exe)); return false; }
        string dir = Path.GetDirectoryName(exe), fresh = Path.Combine(dir, NewExe);
        File.Copy(exe, fresh, true);
        try { ShortcutMigration.Retarget(exe, fresh); } catch (Exception e) { Log.Write("rename shortcuts: " + e.Message); }
        try { Autostart.Retarget(exe, fresh); } catch (Exception e) { Log.Write("rename autostart: " + e.Message); }
        try { UpdateUninstallEntry(dir); } catch (Exception e) { Log.Write("rename apps entry: " + e.Message); }
        Log.Write("renamed in place: " + fresh);
        var cmd = Environment.GetCommandLineArgs();
        var parts = new List<string>();
        for (int i = 1; i < cmd.Length; i++) parts.Add("\"" + cmd[i].Replace("\"", "\\\"") + "\"");
        beforeStart();   // the new process must be able to take the single-instance lock
        Process.Start(new ProcessStartInfo(fresh) { UseShellExecute = false, WorkingDirectory = dir, Arguments = string.Join(" ", parts.ToArray()) });
        return true;
      } catch (Exception e) { Log.Write("rename: " + e.Message); return false; }
    }

    static void UpdateUninstallEntry(string dir) {
      using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey, true)) {
        if (k == null) {
          using (var m = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey, false)) if (m != null) Log.Write("rename: the Apps entry is machine-wide, left as is");
          return;
        }
        k.SetValue("DisplayName", "Wardsage");
        k.SetValue("DisplayIcon", Path.Combine(dir, NewExe));
        k.SetValue("Publisher", "Wardsage (fan project)");
        foreach (var n in new[] { "URLInfoAbout", "HelpLink" }) if (k.GetValue(n) != null) k.SetValue(n, "https://wardsage.com");
      }
    }

    /// <summary>Started as Wardsage.exe: the old file next to it goes away (it may still be closing: retried in the background).</summary>
    static void CleanOld(string dir) {
      string old = Path.Combine(dir, OldExe);
      if (!File.Exists(old) && !File.Exists(old + ".old")) return;
      ThreadPool.QueueUserWorkItem(delegate {
        try {
          if (File.Exists(old + ".old")) try { File.Delete(old + ".old"); } catch (Exception) { }
          for (int i = 0; i < 10 && File.Exists(old); i++) {
            try { File.Delete(old); } catch (Exception) { Thread.Sleep(1000); }
          }
          if (File.Exists(old)) { try { File.Move(old, old + ".old"); } catch (Exception) { } }
        } catch (Exception e) { Log.Write("rename cleanup: " + e.Message); }
      });
    }
  }

  static class Log {
    public static string Path { get { return System.IO.Path.Combine(AppConfig.DefaultDir, "realmforge.log"); } }
    static readonly object gate = new object();
    public static void Write(string line) {
      lock (gate) {
        try {
          Directory.CreateDirectory(AppConfig.DefaultDir);
          var fi = new FileInfo(Path);
          if (fi.Exists && fi.Length > 2 * 1024 * 1024) fi.Delete();
          File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
        } catch (Exception) { }
      }
    }
  }
}
