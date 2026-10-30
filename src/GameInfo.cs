// RealmForge extractor - finding the game process and its version (read-only).

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  public static class GameInfo {
    public const string ProcessName = "Watcher of Realms";
    const string WindowTitle = "Watcher of Realms";   // other regions' builds: «Watcher of Realms - US», ...
    const string WindowTitleKo = "워처 오브 렐름";      // the Korean title

    /// <summary>The exe of the running game as last found (for the log folder search and the build fingerprint); null = none yet.</summary>
    public static volatile string KnownExe;

    /// <summary>A window title of the game: the name anywhere in it (a region or store may put it after a prefix), or the Korean name.</summary>
    public static bool TitleIsGame(string title) {
      if (string.IsNullOrEmpty(title)) return false;
      return title.IndexOf(WindowTitle, StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf(WindowTitleKo, StringComparison.Ordinal) >= 0;
    }

    /// <summary>The hex SHA-256 of the GameAssembly.dll next to the exe (null when there is none or it cannot be read).</summary>
    public static string TryHashAssembly(string exePath) {
      try {
        if (string.IsNullOrEmpty(exePath)) return null;
        string p = Path.Combine(Path.GetDirectoryName(exePath), "GameAssembly.dll");
        if (!File.Exists(p)) return null;
        using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sha = SHA256.Create()) {
          var h = sha.ComputeHash(fs);
          var sb = new StringBuilder(64);
          foreach (byte b in h) sb.Append(b.ToString("x2"));
          return sb.ToString();
        }
      } catch (Exception) { return null; }
    }

    // <game folder>\<exe name>_Data\StreamingAssets\version\windows\realversion.xml
    static readonly string[] VersionFile = { "StreamingAssets", "version", "windows", "realversion.xml" };

    // A build with another exe name, found by its window once; then looked up by that name like the usual one.
    static string otherName;
    static DateTime nextScan = DateTime.MinValue;
    static readonly object ScanGate = new object();

    // The running game's processes (the caller disposes them), empty when it is not running. The usual build is found
    // by its process name; a build named otherwise (another region or store) by a window titled «Watcher of Realms…»
    // whose exe is a Unity game (GameAssembly.dll or <exe>_Data next to it), not its launcher.
    public static Process[] GameProcesses() {
      Process[] ps = Process.GetProcessesByName(ProcessName);
      if (ps.Length > 0) return ps;
      string other = otherName;
      if (other != null) { ps = Process.GetProcessesByName(other); if (ps.Length > 0) return ps; }
      lock (ScanGate) {
        if (DateTime.UtcNow < nextScan) return ps;
        nextScan = DateTime.UtcNow.AddSeconds(3);   // the window scan is heavier: not on every poll
        Process found = null;
        foreach (var p in Process.GetProcesses()) {
          if (found == null && LooksLikeGame(p)) { found = p; continue; }
          p.Dispose();
        }
        if (found == null) return ps;
        otherName = found.ProcessName;
        return new[] { found };
      }
    }

    static bool LooksLikeGame(Process p) {
      try {
        if (p.ProcessName.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        string title = p.MainWindowTitle;
        if (!TitleIsGame(title)) return false;
        string exe = TryGetExePath(p.Id);
        if (string.IsNullOrEmpty(exe)) return false;
        string dir = Path.GetDirectoryName(exe);
        return File.Exists(Path.Combine(dir, "GameAssembly.dll")) || Directory.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + "_Data"));
      } catch (Exception) { return false; }
    }

    // For the log when the game is not found: processes and windows that mention the game.
    public static string DescribeCandidates() {
      var sb = new StringBuilder();
      foreach (var p in Process.GetProcesses()) {
        try {
          string name = p.ProcessName, title = "";
          try { title = p.MainWindowTitle ?? ""; } catch (Exception) { }
          if (name.IndexOf("watcher", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("realms", StringComparison.OrdinalIgnoreCase) < 0
              && !TitleIsGame(title)) continue;
          if (sb.Length > 0) sb.Append("; ");
          sb.Append(name).Append(" #").Append(p.Id);
          if (title.Length > 0) sb.Append(" «").Append(title).Append("»");
          string exe = TryGetExePath(p.Id);
          if (exe != null) sb.Append(" ").Append(exe);
        } catch (Exception) { }
        finally { p.Dispose(); }
      }
      return sb.Length > 0 ? sb.ToString() : "none";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);

    const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // Id of the running game process, or 0.
    static int freshPid, freshAt; static readonly object freshGate = new object();
    /// <summary>The game's process id as found at most <paramref name="maxAgeMs"/> ago (0 = not running): the hot paths' "is there a game"
    /// without listing every process of the PC on each call (a list of them all costs ms and garbage on a slow PC).</summary>
    public static int GamePidFresh(int maxAgeMs) {
      int now = Environment.TickCount;
      lock (freshGate) { if (freshAt != 0 && unchecked(now - freshAt) >= 0 && unchecked(now - freshAt) < maxAgeMs) return freshPid; }
      int pid = FindGameProcess();
      lock (freshGate) { freshPid = pid; freshAt = now == 0 ? 1 : now; }
      return pid;
    }

    public static int FindGameProcess() {
      Process[] ps = GameProcesses();
      try { return ps.Length > 0 ? ps[0].Id : 0; }
      finally { foreach (var p in ps) p.Dispose(); }
    }

    // Full path of the game exe. QueryFullProcessImageName needs only "limited information"
    // access, which usually works even for a game that runs as administrator. Returns null on failure.
    public static string TryGetExePath(int pid) {
      try {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero) {
          try {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref size)) return sb.ToString(0, size);
          } finally { CloseHandle(h); }
        }
      } catch (Exception) { /* not on Windows, or the API is missing: fall through */ }
      try {
        using (var p = Process.GetProcessById(pid)) return p.MainModule.FileName;
      } catch (Exception) { return null; }
    }

    // Reads the game version from realversion.xml next to the exe. Returns null when unknown.
    public static string TryReadVersion(string exePath) {
      try {
        if (string.IsNullOrEmpty(exePath)) return null;
        string path = Path.Combine(Path.GetDirectoryName(exePath), Path.GetFileNameWithoutExtension(exePath) + "_Data");
        foreach (var part in VersionFile) path = Path.Combine(path, part);
        if (!File.Exists(path)) return null;
        var fi = new FileInfo(path);
        if (fi.Length > 64 * 1024) return null;
        return ParseVersionXml(File.ReadAllText(path, Encoding.UTF8));
      } catch (Exception) { return null; }
    }

    // The format of realversion.xml is not documented, so this is deliberately forgiving:
    //   1. an element or attribute whose name contains "version" (e.g. <version>1.2.3</version>, Version="1.2.3");
    //   2. otherwise the first text node that looks like a version number (1.2 / 1.2.3.4...);
    //   3. otherwise a short file without any tags is taken as the version itself.
    public static string ParseVersionXml(string xml) {
      if (xml == null) return null;
      string s = xml.Trim().TrimStart('﻿');
      s = Regex.Replace(s, @"<\?.*?\?>|<!--.*?-->", "", RegexOptions.Singleline);  // <?xml version="1.0"?> is not ours
      Match m = Regex.Match(s, @"<([A-Za-z_][\w.:-]*version[\w.:-]*)\b[^>]*>\s*([^<]+?)\s*</\1\s*>", RegexOptions.IgnoreCase);
      if (m.Success && Clean(m.Groups[2].Value) != null) return Clean(m.Groups[2].Value);
      m = Regex.Match(s, @"\b[\w.:-]*version[\w.:-]*\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
      if (m.Success) {
        string v = Clean(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        if (v != null) return v;
      }
      m = Regex.Match(s, @">\s*(\d+(?:\.\d+){1,4}[\w.+-]*)\s*<");
      if (m.Success) return Clean(m.Groups[1].Value);
      if (s.IndexOf('<') < 0) return Clean(s);
      return null;
    }

    static string Clean(string v) {
      if (v == null) return null;
      v = v.Trim();
      if (v.Length == 0 || v.Length > 64) return null;
      foreach (char c in v) if (c < 32 || c == '<' || c == '>') return null;
      return v;
    }
  }
}
