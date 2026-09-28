// RealmForge app - opt-in error reports to the RealmForge site (Settings → «Отправлять отчёты об ошибках», OFF by default).
//
// Contract (implemented by the site, lib/report/handler.ts):
//   POST {site}/api/report   Authorization: Bearer <sync code>   Content-Type: application/json
//   body {source:"app", kind, message, appVersion, gameVersion, context:{...}, logTail}
//     kind: [a-z0-9][a-z0-9_.:-]{0,63}; message ≤ 2000; versions ≤ 64; context ≤ 16 KB; logTail ≤ 20 480 characters
//   200 {ok:true, id} | 401 invalid_token | 413 | 422 invalid_payload | 429 rate_limited (20 per hour) | 500
//
// What is sent: the kind of failure, a short message, the app and game versions, a few facts (context) and the last
// ~300 lines of the journal (realmforge.log: hero / item ids, the game's screens and the program's steps). The sync code
// is never in the journal; it goes only in the Authorization header, as for every request to the site. Before sending,
// anything that looks like a code or a bearer token is masked and the Windows user folder is replaced with
// %USERPROFILE% (the journal names saved files).
// Limits here: the same kind at most once per 10 minutes, at most 20 reports a day (kept in reports-sent.txt, so a
// crash loop does not flood the site). Nothing is sent while the setting is off.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  public enum ReportStatus { Sent, InvalidToken, RateLimited, Rejected, Unreachable }

  /// <summary>Which reports may go: the same kind at most once per 10 minutes, at most 20 in 24 hours. Thread-safe; the
  /// marks are kept in a file (null path = memory only).</summary>
  public sealed class ErrorReportLimiter {
    public static readonly TimeSpan SameKindGap = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Day = TimeSpan.FromHours(24);
    public const int MaxPerDay = 20;

    readonly string path;
    readonly object gate = new object();
    readonly List<KeyValuePair<DateTime, string>> sent = new List<KeyValuePair<DateTime, string>>();   // UTC, kind

    public ErrorReportLimiter(string path) {
      this.path = path;
      if (path == null) return;
      try {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8)) {
          int sp = line.IndexOf(' ');
          DateTime at;
          if (sp > 0 && DateTime.TryParse(line.Substring(0, sp), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at))
            sent.Add(new KeyValuePair<DateTime, string>(at, line.Substring(sp + 1).Trim()));
        }
      } catch (Exception) { }   // a broken file only loses the marks
    }

    /// <summary>May a report of <paramref name="kind"/> go now? true = yes, and it is counted.</summary>
    public bool TryTake(string kind, DateTime utcNow) {
      lock (gate) {
        sent.RemoveAll(m => m.Key <= utcNow - Day || m.Key > utcNow.AddMinutes(5));   // (a clock set back: old marks go)
        if (sent.Count >= MaxPerDay) return false;
        foreach (var m in sent) if (m.Value == kind && m.Key > utcNow - SameKindGap) return false;
        sent.Add(new KeyValuePair<DateTime, string>(utcNow, kind));
        Save();
        return true;
      }
    }

    public int CountToday(DateTime utcNow) {
      lock (gate) { int n = 0; foreach (var m in sent) if (m.Key > utcNow - Day) n++; return n; }
    }

    void Save() {
      if (path == null) return;
      try {
        var sb = new StringBuilder();
        foreach (var m in sent) sb.Append(m.Key.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(' ').Append(m.Value).Append('\n');
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
      } catch (Exception) { }
    }
  }

  public static class ErrorReports {
    public const int LogLines = 300;
    public const int MaxLogChars = 20000;     // the site takes 20 480
    public const int MaxMessage = 2000;
    public const int MaxContextValue = 1000;
    public static int TimeoutMs = 15000;

    static readonly Regex KindRx = new Regex("^[a-z0-9][a-z0-9_.:-]{0,63}$");
    static readonly Regex CodeRx = new Regex("(?<![0-9A-Za-z_])rf_[0-9A-Za-z]{32}(?![0-9A-Za-z])");
    static readonly Regex BearerRx = new Regex("(?i)(bearer\\s+)[^\\s\"',;]+");

    /// <summary>The kind as the site takes it: lower case, only [a-z0-9_.:-], at most 64; "other" when nothing is left.</summary>
    public static string NormalizeKind(string kind) {
      var sb = new StringBuilder();
      foreach (char ch in (kind ?? "").ToLowerInvariant()) {
        if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == ':' || ch == '-') sb.Append(ch);
        else if (ch == ' ' || ch == '/') sb.Append('_');
        if (sb.Length == 64) break;
      }
      string k = sb.ToString().TrimStart('_', '.', ':', '-');
      return KindRx.IsMatch(k) ? k : "other";
    }

    /// <summary>Masks sync codes and bearer tokens; replaces the Windows user folder with %USERPROFILE%.</summary>
    public static string Scrub(string text, string userProfile) {
      if (string.IsNullOrEmpty(text)) return text ?? "";
      string s = CodeRx.Replace(text, "rf_***");
      s = BearerRx.Replace(s, "$1***");
      if (!string.IsNullOrEmpty(userProfile) && userProfile.Length > 3)
        s = Regex.Replace(s, Regex.Escape(userProfile.TrimEnd('\\', '/')), "%USERPROFILE%", RegexOptions.IgnoreCase);
      return s;
    }

    /// <summary>The last <paramref name="lines"/> lines of a text, then at most <paramref name="maxChars"/> from its end
    /// (cut at a line start when there is one).</summary>
    public static string Tail(string text, int lines, int maxChars) {
      if (string.IsNullOrEmpty(text)) return "";
      string t = text.Replace("\r\n", "\n");
      int end = t.Length; if (end > 0 && t[end - 1] == '\n') end--;
      int pos = end, n = 0;
      while (pos > 0) {
        int nl = t.LastIndexOf('\n', pos - 1);
        n++;
        if (n >= lines) { pos = nl + 1; break; }
        if (nl < 0) { pos = 0; break; }
        pos = nl;
      }
      if (n < lines) pos = 0;
      string r = t.Substring(pos);
      if (r.Length > maxChars) {
        r = r.Substring(r.Length - maxChars);
        int nl = r.IndexOf('\n');
        if (nl >= 0 && nl < r.Length - 1) r = r.Substring(nl + 1);
      }
      return r;
    }

    /// <summary>The journal's last lines (read shared: the program keeps writing it). "" when there is none.</summary>
    public static string ReadLogTail(string path) {
      try {
        if (path == null || !File.Exists(path)) return "";
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
          long start = Math.Max(0, fs.Length - 96 * 1024);   // 300 journal lines are ~30-60 KB
          fs.Seek(start, SeekOrigin.Begin);
          var buf = new byte[fs.Length - start]; int got = 0, k;
          while (got < buf.Length && (k = fs.Read(buf, got, buf.Length - got)) > 0) got += k;
          string text = new UTF8Encoding(false).GetString(buf, 0, got);
          if (start > 0) { int nl = text.IndexOf('\n'); if (nl >= 0) text = text.Substring(nl + 1); }   // a cut first line
          return Tail(text, LogLines, MaxLogChars);
        }
      } catch (Exception) { return ""; }
    }

    static string Cap(string s, int max) {
      if (s == null) return null;
      s = s.Replace("\0", "");
      return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
    }

    /// <summary>The request body. Every text is scrubbed (codes, tokens, the user folder) and capped to the site's limits.</summary>
    public static string BuildJson(string kind, string message, string appVersion, string gameVersion,
                                   IDictionary<string, string> context, string logTail, string userProfile) {
      var sb = new StringBuilder("{\"source\":\"app\"");
      sb.Append(",\"kind\":").Append(MiniJson.Quote(NormalizeKind(kind)));
      string msg = Cap(Scrub(message, userProfile), MaxMessage);
      if (string.IsNullOrEmpty(msg) || msg.Trim().Length == 0) msg = NormalizeKind(kind);
      sb.Append(",\"message\":").Append(MiniJson.Quote(msg));
      if (!string.IsNullOrEmpty(appVersion)) sb.Append(",\"appVersion\":").Append(MiniJson.Quote(Cap(appVersion, 64)));
      if (!string.IsNullOrEmpty(gameVersion)) sb.Append(",\"gameVersion\":").Append(MiniJson.Quote(Cap(gameVersion, 64)));
      sb.Append(",\"context\":{");
      if (context != null) {
        bool first = true; int n = 0;
        foreach (var kv in context) {
          if (kv.Key == null || ++n > 12) continue;
          if (!first) sb.Append(','); first = false;
          sb.Append(MiniJson.Quote(Cap(kv.Key, 40))).Append(':').Append(MiniJson.Quote(Cap(Scrub(kv.Value ?? "", userProfile), MaxContextValue)));
        }
      }
      sb.Append('}');
      string log = Scrub(logTail, userProfile);
      if (!string.IsNullOrEmpty(log)) sb.Append(",\"logTail\":").Append(MiniJson.Quote(Tail(Cap(log, int.MaxValue), LogLines, MaxLogChars)));
      return sb.Append('}').ToString();
    }

    /// <summary>Sends one report. Never throws.</summary>
    public static ReportStatus Send(string site, string code, string json, int timeoutMs) {
      try {
        SyncClient.EnableTls12();
        ServicePointManager.Expect100Continue = false;
        var req = (HttpWebRequest)WebRequest.Create(site + "/api/report");
        req.Method = "POST";
        req.ContentType = "application/json";
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Headers["Authorization"] = "Bearer " + code;
        req.Headers["X-RF-Extractor"] = SyncClient.Version;
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = timeoutMs;
        req.AllowAutoRedirect = false;
        byte[] b = new UTF8Encoding(false).GetBytes(json);
        req.ContentLength = b.Length;
        using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
        using (var resp = (HttpWebResponse)req.GetResponse()) return StatusOf((int)resp.StatusCode);
      } catch (WebException e) {
        var resp = e.Response as HttpWebResponse;
        if (resp != null) using (resp) return StatusOf((int)resp.StatusCode);
        return ReportStatus.Unreachable;
      } catch (Exception) {
        return ReportStatus.Unreachable;
      }
    }

    public static ReportStatus StatusOf(int http) {
      if (http >= 200 && http < 300) return ReportStatus.Sent;
      if (http == 401) return ReportStatus.InvalidToken;
      if (http == 429) return ReportStatus.RateLimited;
      if (http == 0) return ReportStatus.Unreachable;
      return ReportStatus.Rejected;
    }
  }
}
