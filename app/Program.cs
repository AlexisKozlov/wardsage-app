// RealmForge.exe - entry point.
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
    public const string Version = "1.2.2";
    internal static string AppDir;          // unpacked resources of this build
    internal static string UiDir;
    internal static string DataDir;         // WebView2 user data (cache, local storage)
    static Mutex single;

    [STAThread]
    static int Main(string[] args) {
      bool created;
      single = new Mutex(true, "RealmForge.Desktop.SingleInstance", out created);
      if (!created) { BringOtherToFront(); return 0; }

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
          "Для работы RealmForge нужен компонент Microsoft Edge WebView2 Runtime (он есть в Windows 11 и в обновлённой Windows 10).\n\n" +
          "Открыть страницу загрузки Microsoft?\n\n" +
          "RealmForge needs the Microsoft Edge WebView2 Runtime. Open the Microsoft download page?",
          "RealmForge", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r == DialogResult.Yes) Shell.OpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        return 2;
      }
      using (var w = new AppWindow()) Application.Run(w);
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
      string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RealmForge");
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

    static byte[] ReadAll(Stream s) { using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }

    static void BringOtherToFront() {
      try {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
          if (p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero) { Native.ShowWindow(p.MainWindowHandle, 9); Native.SetForegroundWindow(p.MainWindowHandle); }
      } catch (Exception) { }
    }

    internal static void Fatal(Exception e) {
      string msg = e == null ? "unknown error" : e.GetType().Name + ": " + e.Message;
      try { Log.Write("FATAL " + (e == null ? msg : e.ToString())); } catch (Exception) { }
      MessageBox.Show("RealmForge: " + msg, "RealmForge", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
