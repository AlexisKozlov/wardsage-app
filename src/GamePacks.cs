// Wardsage — the addresses of the game's downloaded update packs, from the game's own log, to the site (app/PacksWatch.cs
// ticks it). The game server tells the client these names after login, so the site cannot know them otherwise.
//
// Contract (implemented by the site, lib/gamePacks/handler.ts):
//   POST {site}/api/extractor/game-packs      Authorization: Bearer <sync code>
//        body {v:1, urls:[address]}   (at most 100; only the addresses of the game's update packs)
//        200 {ok:true, stored:n}   401 invalid_token   422 invalid_payload{details}
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  public static class GamePacks {
    const int TailBytes = 4 * 1024 * 1024;
    const int MaxUrls = 100;
    // the same check as the site's
    static readonly Regex UrlRe = new Regex(@"^https?://[a-z0-9-]+\.ztdheros\.com/(pc_res_version5/out|pc_res_hotfix5)/[0-9]+\.[0-9]+/zg_[A-Za-z0-9_.]+\.tgrp$", RegexOptions.CultureInvariant);
    static readonly Regex FindRe = new Regex(@"url = (\S+?)(?:,|\s|$)", RegexOptions.CultureInvariant);

    public static bool IsValid(string url) { return url != null && url.Length <= 300 && UrlRe.IsMatch(url); }

    /// <summary>The distinct valid pack addresses of a log text, in the order first seen.</summary>
    public static List<string> ParseLog(string text) {
      var res = new List<string>(); var seen = new HashSet<string>();
      if (string.IsNullOrEmpty(text)) return res;
      foreach (Match m in FindRe.Matches(text)) {
        string u = m.Groups[1].Value;
        if (IsValid(u) && seen.Add(u)) res.Add(u);
      }
      return res;
    }

    /// <summary>Where the app writes its notes (set by the host; the core has no log).</summary>
    public static Action<string> Note;
    static string foundDir;   // the log folder found by the search (another build's company / product names)

    static string LowRoot() { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low"; }

    /// <summary>The game's log folder: the usual one when it has a Player.log, else a folder found by <see cref="FindLogDir"/>, else the usual one.</summary>
    public static string LogDir() {
      string usual = Path.Combine(LowRoot(), "Watcher of Realms", "Watcher of Realms");
      if (File.Exists(Path.Combine(usual, "Player.log"))) return usual;
      string f = foundDir;
      if (f != null && File.Exists(Path.Combine(f, "Player.log"))) return f;
      f = FindLogDir(LowRoot(), GameInfo.KnownExe);
      if (f != null) { foundDir = f; var n = Note; if (n != null) n("game log folder: " + f); return f; }
      return usual;
    }

    /// <summary>Searches <paramref name="root"/>\*\*\Player.log (two levels, at most 200 and 100 folders) for a log that mentions
    /// the game (its name or the folder of <paramref name="exe"/>); null when none.</summary>
    public static string FindLogDir(string root, string exe) {
      try {
        if (!Directory.Exists(root)) return null;
        string exeDir = null;
        try { exeDir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe); } catch (Exception) { }
        int a = 0;
        foreach (string company in Directory.GetDirectories(root)) {
          if (++a > 200) break;
          int b = 0;
          string[] products;
          try { products = Directory.GetDirectories(company); } catch (Exception) { continue; }
          foreach (string product in products) {
            if (++b > 100) break;
            string log = Path.Combine(product, "Player.log");
            if (!File.Exists(log)) continue;
            string head = Head(log);
            if (head == null) continue;
            if (head.IndexOf("Watcher of Realms", StringComparison.OrdinalIgnoreCase) >= 0
                || (exeDir != null && head.IndexOf(exeDir, StringComparison.OrdinalIgnoreCase) >= 0)
                || (exeDir != null && head.IndexOf(exeDir.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) >= 0)) return product;
          }
        }
      } catch (Exception) { }
      return null;
    }

    /// <summary>The first 64 KB of a file the game may keep open; null when it cannot be read.</summary>
    static string Head(string path) {
      try {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
          var buf = new byte[(int)Math.Min(fs.Length, 65536)]; int got = 0;
          while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
          return Encoding.UTF8.GetString(buf, 0, got);
        }
      } catch (Exception) { return null; }
    }

    /// <summary>The last 4 MB of a file the game may keep open; null when it cannot be read.</summary>
    static string Tail(string path) {
      try {
        if (!File.Exists(path)) return null;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
          long len = fs.Length, from = Math.Max(0, len - TailBytes);
          fs.Seek(from, SeekOrigin.Begin);
          var buf = new byte[len - from]; int got = 0;
          while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
          return Encoding.UTF8.GetString(buf, 0, got);
        }
      } catch { return null; }
    }

    /// <summary>The pack addresses of Player-prev.log and Player.log (the previous run first); empty on any error.</summary>
    public static List<string> ReadLogs() {
      var res = new List<string>();
      try {
        string dir = LogDir();
        var all = new HashSet<string>();
        foreach (string name in new[] { "Player-prev.log", "Player.log" }) {
          foreach (string u in ParseLog(Tail(Path.Combine(dir, name)))) if (all.Add(u)) res.Add(u);
        }
        if (res.Count > MaxUrls) res.RemoveRange(0, res.Count - MaxUrls);   // keep the newest
      } catch { res.Clear(); }
      return res;
    }

    public static string Json(List<string> urls) {
      var sb = new StringBuilder("{\"v\":1,\"urls\":[");
      for (int i = 0; i < urls.Count; i++) { if (i > 0) sb.Append(','); sb.Append(MiniJson.Quote(urls[i])); }
      return sb.Append("]}").ToString();
    }

    public static PlansStatus Send(string site, string code, string json, out string details) {
      int http; string text, err;
      details = null;
      if (!PlansClient.Send("POST", site + "/api/extractor/game-packs", code, json, out http, out text, out err)) { details = err; return PlansStatus.Unreachable; }
      return ArenaClient.Interpret(http, text, out details);
    }
  }
}
