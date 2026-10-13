// Wardsage app - the TEST build sends its log lines to the site so the developers can read them (release builds never do).
//
// Contract (implemented by the site):
//   POST {site}/api/extractor/test-log   Authorization: Bearer <sync code>, Content-Type: application/json, X-RF-Player
//        body {"v":1,"app":"<version>","lines":["..."]}   1..500 lines, each <= 2000 characters, body <= 256 KB
//        200 ok | 404 (the endpoint is off, e.g. on prod: stop for this run) | 401/403, 429, 5xx, no connection: keep and retry later
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace RealmForge {
  /// <summary>The lines waiting to go: at most 3000 (the oldest are dropped). Thread-safe.</summary>
  public sealed class TestLogQueue {
    public const int MaxLines = 3000;
    public const int MaxLineChars = 2000;

    readonly object gate = new object();
    readonly LinkedList<string> lines = new LinkedList<string>();

    public int Count { get { lock (gate) return lines.Count; } }

    public void Add(string line) {
      if (line == null) return;
      lock (gate) {
        lines.AddLast(line);
        while (lines.Count > MaxLines) lines.RemoveFirst();
      }
    }

    /// <summary>Up to <paramref name="maxLines"/> oldest lines, about <paramref name="maxBytes"/> of UTF-8 (at least one line
    /// is always taken); each is cut to 2000 characters. They are removed from the queue.</summary>
    public List<string> Take(int maxLines, int maxBytes) {
      var res = new List<string>();
      lock (gate) {
        long bytes = 0;
        while (lines.Count > 0 && res.Count < maxLines) {
          string l = lines.First.Value;
          if (l.Length > MaxLineChars) l = l.Substring(0, MaxLineChars);
          long b = Encoding.UTF8.GetByteCount(l) + 4;
          if (res.Count > 0 && bytes + b > maxBytes) break;
          lines.RemoveFirst();
          res.Add(l);
          bytes += b;
        }
      }
      return res;
    }

    /// <summary>Puts lines taken earlier back at the front, in their order (the cap still applies: the oldest go first).</summary>
    public void Return(List<string> back) {
      if (back == null) return;
      lock (gate) {
        for (int i = back.Count - 1; i >= 0; i--) lines.AddFirst(back[i]);
        while (lines.Count > MaxLines) lines.RemoveFirst();
      }
    }
  }

  public static class TestLogClient {
    public enum Result { Ok, Off, Later }

    public static string BuildJson(string app, List<string> lines) {
      var sb = new StringBuilder();
      sb.Append("{\"v\":1,\"app\":").Append(MiniJson.Quote(app ?? "")).Append(",\"lines\":[");
      for (int i = 0; i < lines.Count; i++) {
        if (i > 0) sb.Append(',');
        string l = lines[i] ?? "";
        if (l.Length > TestLogQueue.MaxLineChars) l = l.Substring(0, TestLogQueue.MaxLineChars);
        sb.Append(MiniJson.Quote(l));
      }
      sb.Append("]}");
      return sb.ToString();
    }

    /// <summary>Sends the lines. Never throws. Ok = delivered; Off = the site has no such endpoint (stop); Later = keep the lines.</summary>
    public static Result Send(string site, string code, string app, List<string> lines) {
      try {
        if (lines == null || lines.Count == 0 || string.IsNullOrEmpty(site) || !SyncClient.IsValidCode(code)) return Result.Later;
        SyncClient.EnableTls12();
        var req = (HttpWebRequest)WebRequest.Create(site.TrimEnd('/') + "/api/extractor/test-log");
        req.Method = "POST";
        req.ContentType = "application/json";
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Headers["Authorization"] = "Bearer " + code;
        SyncClient.AddPlayer(req);
        req.Timeout = 10000;
        req.ReadWriteTimeout = 10000;
        req.AllowAutoRedirect = false;
        byte[] body = new UTF8Encoding(false).GetBytes(BuildJson(app, lines));
        req.ContentLength = body.Length;
        using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
        HttpWebResponse resp;
        try { resp = (HttpWebResponse)req.GetResponse(); }
        catch (WebException e) {
          resp = e.Response as HttpWebResponse;
          if (resp == null) return Result.Later;
        }
        using (resp) {
          int code2 = (int)resp.StatusCode;
          if (code2 == 200) return Result.Ok;
          if (code2 == 404) return Result.Off;
          return Result.Later;
        }
      } catch (Exception) { return Result.Later; }
    }
  }
}
