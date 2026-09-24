// RealmForge extractor - finding the game process and its version (read-only).

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  public static class GameInfo {
    public const string ProcessName = "Watcher of Realms";

    // <game folder>\Watcher of Realms_Data\StreamingAssets\version\windows\realversion.xml
    static readonly string[] VersionFile = { "Watcher of Realms_Data", "StreamingAssets", "version", "windows", "realversion.xml" };

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);

    const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // Id of the running game process, or 0.
    public static int FindGameProcess() {
      Process[] ps = Process.GetProcessesByName(ProcessName);
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
        string path = Path.GetDirectoryName(exePath);
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
