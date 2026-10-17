// Wardsage - long poll for new work from the site (a plan, a «Найти в игре» request, a sell list): the site answers GET /api/extractor/plans/wait
// after up to 25 s, earlier when something changed, so the app reloads the plans within a second or two instead of waiting for its timer.
//   GET {site}/api/extractor/plans/wait?since=<ISO 8601 UTC>   Authorization: Bearer <sync code>
//   200 {"changed":true|false,"at":"<server ISO>"} | 401 bad code | 429 too many waits | 404 not deployed (old site)
using System;
using System.Globalization;
using System.Net;
using System.Threading;

namespace RealmForge {
  public enum WaitKind { Ok, Unauthorized, TooMany, NotDeployed, Error }

  public sealed class WaitReply {
    public WaitKind Kind;
    public bool Changed;
    public string At = "";
  }

  public static class PlansWait {
    public const int TimeoutMs = 35000, BackoffFirstMs = 2000, BackoffMaxMs = 60000;

    /// <summary>One reply of the site by HTTP code and body (pure).</summary>
    public static WaitReply Interpret(int http, string body) {
      var r = new WaitReply();
      if (http == 404) { r.Kind = WaitKind.NotDeployed; return r; }
      if (http == 401) { r.Kind = WaitKind.Unauthorized; return r; }
      if (http == 429) { r.Kind = WaitKind.TooMany; return r; }
      if (http < 200 || http >= 300) { r.Kind = WaitKind.Error; return r; }
      var o = MiniJson.AsObject(MiniJson.TryParse(body));
      if (o == null) { r.Kind = WaitKind.Error; return r; }
      r.Kind = WaitKind.Ok; r.Changed = MiniJson.GetBool(o, "changed", false);
      string at = MiniJson.GetString(o, "at") ?? "";
      DateTime t;
      r.At = at.Length <= 40 && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t) ? at : "";
      return r;
    }

    /// <summary>The wait after the n-th failure in a row (1-based): 2 s, 4 s, 8 s ... up to 60 s.</summary>
    public static int Backoff(int failures) {
      if (failures < 1) return 0;
      long ms = BackoffFirstMs;
      for (int i = 1; i < failures && ms < BackoffMaxMs; i++) ms *= 2;
      return (int)Math.Min(ms, BackoffMaxMs);
    }

    /// <summary>The `since` to send: the server's last `at`, else the local UTC time minus 60 s.</summary>
    public static string Since(string lastAt, DateTime nowUtc) {
      return string.IsNullOrEmpty(lastAt) ? nowUtc.AddSeconds(-60).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : lastAt;
    }

    /// <summary>One long request; false when the site was not reached.</summary>
    public static bool Request(string site, string code, string since, Action<HttpWebRequest> started, out int http, out string text) {
      http = 0; text = null;
      try {
        SyncClient.EnableTls12();
        ServicePointManager.Expect100Continue = false;
        var req = (HttpWebRequest)WebRequest.Create(site + "/api/extractor/plans/wait?since=" + Uri.EscapeDataString(since));
        req.Method = "GET"; req.Accept = "application/json"; req.UserAgent = SyncClient.UserAgent;
        req.Headers["Authorization"] = "Bearer " + code; req.Headers["X-RF-Extractor"] = SyncClient.Version;
        SyncClient.AddPlayer(req);
        req.Timeout = TimeoutMs; req.ReadWriteTimeout = TimeoutMs; req.AllowAutoRedirect = false;
        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        if (started != null) started(req);
        using (var resp = (HttpWebResponse)req.GetResponse()) { http = (int)resp.StatusCode; text = Read(resp); }
        return true;
      } catch (WebException e) {
        var resp = e.Response as HttpWebResponse;
        if (resp != null) using (resp) { http = (int)resp.StatusCode; text = Read(resp); return true; }
        return false;
      } catch (Exception) { return false; }
    }

    static string Read(HttpWebResponse resp) {
      try { using (var s = new System.IO.StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8)) { var buf = new char[4096]; int n = s.Read(buf, 0, buf.Length); return n > 0 ? new string(buf, 0, n) : ""; } }
      catch (Exception) { return null; }
    }
  }

  /// <summary>The background loop: its own thread, stops at once (the pending request is aborted) and restarts for a new site / code.</summary>
  public sealed class PlansWaiter {
    readonly Func<string> site, code;
    readonly Action changed;        // new work on the site (any thread)
    readonly Action<bool> state;    // the long poll works / does not (any thread)
    readonly Action<string> log;
    Thread th; volatile int gen; volatile bool on; volatile HttpWebRequest pending;
    public bool Dead { get; private set; }   // the site has no such endpoint: not for this run

    public PlansWaiter(Func<string> site, Func<string> code, Action changed, Action<bool> state, Action<string> log) {
      this.site = site; this.code = code; this.changed = changed; this.state = state; this.log = log;
    }

    public bool Running { get { return on; } }

    public void Start() {
      if (on || Dead) return;
      on = true; int my = ++gen;
      th = new Thread(() => Run(my)) { IsBackground = true, Name = "plans-wait" };
      th.Start();
    }

    /// <summary>Ends the loop now (the pending long request is aborted); a later Start begins a new one.</summary>
    public void Stop() {
      on = false; gen++;
      var p = pending; if (p != null) try { p.Abort(); } catch (Exception) { }
      state(false);
    }

    void Run(int my) {
      string since = "", s0 = site(), c0 = code();
      int fails = 0; bool ok = false;
      while (my == gen) {
        int http; string text;
        bool reached = PlansWait.Request(s0, c0, PlansWait.Since(since, DateTime.UtcNow), r0 => { pending = r0; if (my != gen) try { r0.Abort(); } catch (Exception) { } }, out http, out text);
        if (my != gen) return;
        var r = reached ? PlansWait.Interpret(http, text) : new WaitReply { Kind = WaitKind.Error };
        if (r.Kind == WaitKind.Ok) {
          fails = 0; if (!ok) { ok = true; state(true); }
          if (r.At != "") since = r.At;
          if (r.Changed) { try { changed(); } catch (Exception) { } }
          continue;
        }
        if (ok) { ok = false; state(false); }
        if (r.Kind == WaitKind.NotDeployed) { Dead = true; on = false; log("plans wait: the site has no long poll, the timer polling stays"); return; }
        fails++;
        int wait = PlansWait.Backoff(fails);
        if (fails == 1 || fails % 10 == 0) log("plans wait: " + r.Kind + (reached ? " (HTTP " + http + ")" : " (no connection)") + ", retry in " + wait / 1000 + " s");
        for (int t = 0; t < wait && my == gen; t += 100) Thread.Sleep(100);
      }
    }
  }
}
