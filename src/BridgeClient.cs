// RealmForge extractor - link to the local RealmForge bridge (bridge/, a .NET 8 service on http://127.0.0.1:5055).
//
// Contract (bridge/Host/HostEndpoints.cs); every request carries X-RealmForge-Token, which the bridge writes to
// %LOCALAPPDATA%\RealmForge\bridge-token.txt on its first start (the same Windows user runs both):
//   PUT  /api/host/snapshot               body: account.json as Extractor.Read made it;
//                                         X-Captured-At: when the reading STARTED (ISO 8601 UTC), X-Game-Version
//        200 {version, heroes, items} | 409 the reading started before the last equip done through the bridge
//   GET  /api/host/commands?wait=25       long poll -> {commands:[{id, type:"equip", issuedAt,
//                                         payload:{commandId, heroId, heroName, slots:[{slot:"weapon", itemId}]}}]}
//   POST /api/host/commands/{id}/result   {status:"done"|"cancelled"|"failed", message?} -> 204 | 404 nobody waits
// The bridge is optional: when it is not running every call fails at once (connection refused on loopback) and the
// poller backs off. Nothing here touches the game; the equip itself is the same guide as for a plan from the site.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace RealmForge {
  public enum BridgeStatus { Ok, Stale, NotFound, NoToken, Unreachable, Rejected }

  public sealed class BridgeSlot {
    public int Slot;        // 0 weapon, 1 armor, 2 bracer, 3 amulet, 4 ring (the game's numbering)
    public long ItemUid;
  }

  public sealed class BridgeCommand {
    public string Id;       // the bridge's id for the answer (a GUID)
    public string Type;     // "equip"; anything else is answered "failed"
    public string CommandId, HeroName;
    public long HeroUid;
    public List<BridgeSlot> Slots = new List<BridgeSlot>();
  }

  public sealed class BridgeClient {
    public const string DefaultUrl = "http://127.0.0.1:5055";
    public const string TokenHeader = "X-RealmForge-Token";
    const int MaxReplyBytes = 1024 * 1024;
    static readonly string[] SlotNames = { "weapon", "armor", "bracer", "amulet", "ring" };

    public static string DefaultTokenPath {
      get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RealmForge", "bridge-token.txt"); }
    }

    readonly string baseUrl, tokenPath;
    readonly object gate = new object();
    HttpWebRequest current;   // the request in flight, for Abort()
    bool aborted;

    public BridgeClient(string baseUrl, string tokenPath) {
      this.baseUrl = baseUrl.TrimEnd('/');
      this.tokenPath = tokenPath;
    }

    public BridgeStatus PushSnapshot(string accountJson, string gameVersion, DateTime capturedAtUtc, out string detail) {
      var headers = new Dictionary<string, string>();
      headers["X-Captured-At"] = capturedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
      if (!string.IsNullOrEmpty(gameVersion)) headers["X-Game-Version"] = gameVersion.Length > 64 ? gameVersion.Substring(0, 64) : gameVersion;
      string reply;
      int code = Send("PUT", "/api/host/snapshot", new UTF8Encoding(false).GetBytes(accountJson), headers, 60000, out reply);
      detail = code > 0 ? "HTTP " + code : null;
      return StatusOf(code);
    }

    /// <summary>Long poll: the commands that arrived within <paramref name="waitSeconds"/> (an empty list when none),
    /// or null when the bridge could not be asked (<paramref name="status"/> says why).</summary>
    public List<BridgeCommand> TakeCommands(int waitSeconds, out BridgeStatus status) {
      string reply;
      int code = Send("GET", "/api/host/commands?wait=" + waitSeconds.ToString(CultureInfo.InvariantCulture), null, null,
                      (waitSeconds + 15) * 1000, out reply);
      status = StatusOf(code);
      if (status != BridgeStatus.Ok) return null;
      var list = ParseCommands(reply);
      if (list == null) status = BridgeStatus.Rejected;
      return list;
    }

    public BridgeStatus Report(string id, string status, string message, int timeoutMs) {
      if (!IsCommandId(id)) return BridgeStatus.NotFound;
      string body = "{\"status\":" + MiniJson.Quote(status) + (message != null ? ",\"message\":" + MiniJson.Quote(message) : "") + "}";
      string reply;
      return StatusOf(Send("POST", "/api/host/commands/" + id + "/result", new UTF8Encoding(false).GetBytes(body), null, timeoutMs, out reply));
    }

    /// <summary>Cancels the request in flight and every later one (the program is closing).</summary>
    public void Abort() {
      lock (gate) {
        aborted = true;
        if (current != null) try { current.Abort(); } catch (Exception) { }
      }
    }

    // HTTP code, 0 = the bridge could not be reached (or the client was aborted), -1 = no token file.
    int Send(string method, string path, byte[] body, Dictionary<string, string> headers, int timeoutMs, out string reply) {
      reply = null;
      string token = ReadToken(tokenPath);
      if (token == null) return -1;
      HttpWebRequest req = null;
      try {
        req = (HttpWebRequest)WebRequest.Create(baseUrl + path);
        req.Method = method;
        req.Proxy = null;                 // loopback: never through a system proxy
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Headers[TokenHeader] = token;
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = timeoutMs;
        req.AllowAutoRedirect = false;
        // the poll holds one connection for up to half a minute; snapshots and answers must not queue behind it
        if (req.ServicePoint.ConnectionLimit < 4) req.ServicePoint.ConnectionLimit = 4;
        if (headers != null) foreach (var h in headers) req.Headers[h.Key] = h.Value;
        lock (gate) { if (aborted) return 0; current = req; }
        if (body != null) {
          req.ContentType = "application/json";
          req.ContentLength = body.Length;
          using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
        }
        using (var resp = (HttpWebResponse)req.GetResponse()) { reply = ReadBody(resp); return (int)resp.StatusCode; }
      } catch (WebException e) {
        var resp = e.Response as HttpWebResponse;
        if (resp != null) using (resp) { reply = ReadBody(resp); return (int)resp.StatusCode; }
        return 0;
      } catch (Exception) {
        return 0;
      } finally {
        lock (gate) { if (current == req) current = null; }
      }
    }

    static string ReadBody(HttpWebResponse resp) {
      try {
        using (Stream s = resp.GetResponseStream())
        using (var ms = new MemoryStream()) {
          var buf = new byte[16384]; int n;
          while ((n = s.Read(buf, 0, buf.Length)) > 0 && ms.Length < MaxReplyBytes) ms.Write(buf, 0, n);
          return Encoding.UTF8.GetString(ms.ToArray());
        }
      } catch (Exception) { return null; }
    }

    static string ReadToken(string path) {
      try {
        if (!File.Exists(path)) return null;
        string t = File.ReadAllText(path).Trim();
        if (t.Length < 32 || t.Length > 256) return null;
        foreach (char c in t) if (c < '!' || c > '~') return null;   // header-safe ASCII only
        return t;
      } catch (Exception) { return null; }
    }

    // Maps an HTTP code of the bridge to a status. Pure function (tested without a network).
    public static BridgeStatus StatusOf(int code) {
      if (code == -1) return BridgeStatus.NoToken;
      if (code == 0) return BridgeStatus.Unreachable;
      if (code >= 200 && code < 300) return BridgeStatus.Ok;
      if (code == 409) return BridgeStatus.Stale;
      if (code == 404) return BridgeStatus.NotFound;
      return BridgeStatus.Rejected;   // 401 wrong token, 400/413/415 bad request, 5xx
    }

    // {commands:[...]} -> commands; null when the reply is not that. Pure function (tested without a network).
    // Every command with a usable id is returned, so even an unknown or malformed one gets an answer ("failed").
    public static List<BridgeCommand> ParseCommands(string body) {
      var root = MiniJson.AsObject(MiniJson.TryParse(body));
      object v;
      var list = root != null && root.TryGetValue("commands", out v) ? v as List<object> : null;
      if (list == null) return null;
      var r = new List<BridgeCommand>();
      foreach (var x in list) {
        var o = MiniJson.AsObject(x); if (o == null) continue;
        var c = new BridgeCommand();
        c.Id = MiniJson.GetString(o, "id");
        if (!IsCommandId(c.Id)) continue;
        c.Type = MiniJson.GetString(o, "type") ?? "";
        object pv; var p = o.TryGetValue("payload", out pv) ? MiniJson.AsObject(pv) : null;
        if (c.Type == "equip" && !ReadEquip(p, c)) c.Type = "invalid";
        r.Add(c);
        if (r.Count == 16) break;
      }
      return r;
    }

    static bool ReadEquip(Dictionary<string, object> p, BridgeCommand c) {
      if (p == null) return false;
      c.HeroUid = Num(p, "heroId");
      c.CommandId = Clip(MiniJson.GetString(p, "commandId"), 64);
      c.HeroName = Clip(MiniJson.GetString(p, "heroName"), 80);
      object sv; var slots = p.TryGetValue("slots", out sv) ? sv as List<object> : null;
      if (c.HeroUid <= 0 || slots == null || slots.Count == 0 || slots.Count > 5) return false;
      foreach (var x in slots) {
        var s = MiniJson.AsObject(x); if (s == null) return false;
        int slot = Array.IndexOf(SlotNames, MiniJson.GetString(s, "slot"));
        long uid = Num(s, "itemId");
        if (slot < 0 || uid <= 0) return false;
        foreach (var had in c.Slots) if (had.Slot == slot || had.ItemUid == uid) return false;
        c.Slots.Add(new BridgeSlot { Slot = slot, ItemUid = uid });
      }
      c.Slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
      return true;
    }

    // a GUID as the bridge writes it: 36 characters of hex digits and dashes (it also becomes part of a URL)
    static bool IsCommandId(string id) {
      if (id == null || id.Length != 36) return false;
      foreach (char ch in id) if (!(ch == '-' || (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'))) return false;
      return true;
    }

    static long Num(Dictionary<string, object> d, string key) {
      object v; if (!d.TryGetValue(key, out v) || !(v is double)) return 0;
      double x = (double)v; if (x <= 0 || x > 9e15 || Math.Floor(x) != x) return 0;
      return (long)x;
    }

    static string Clip(string s, int n) { return s == null ? null : s.Length > n ? s.Substring(0, n) : s; }
  }

  /// <summary>
  /// A background thread that long-polls the bridge and hands each command to <c>onCommand</c> (on this thread: the
  /// host marshals to its UI thread). <c>onConnected</c> reports the bridge appearing / going away. While the bridge is
  /// not there it retries after 2 s, doubling up to 30 s. <see cref="Stop"/> ends the thread at once: the pending poll
  /// is aborted, no callback runs after it returns.
  /// </summary>
  public sealed class BridgePoller : IDisposable {
    const int WaitSeconds = 25, RetryMinMs = 2000, RetryMaxMs = 30000;
    readonly BridgeClient client;
    readonly Action<BridgeCommand> onCommand;
    readonly Action<bool> onConnected;
    readonly ManualResetEvent stop = new ManualResetEvent(false);
    readonly Thread thread;
    volatile bool connected, stopped;

    /// <param name="client">Used by this poller only: <see cref="Stop"/> aborts it for good.</param>
    public BridgePoller(BridgeClient client, Action<BridgeCommand> onCommand, Action<bool> onConnected) {
      this.client = client; this.onCommand = onCommand; this.onConnected = onConnected;
      thread = new Thread(Run);
      thread.IsBackground = true;
      thread.Name = "RealmForge bridge poller";
    }

    /// <summary>The last poll reached the bridge.</summary>
    public bool Connected { get { return connected; } }

    public void Start() { thread.Start(); }

    void Run() {
      int retry = RetryMinMs;
      while (!stop.WaitOne(0)) {
        BridgeStatus status;
        // until the bridge has answered once, ask without waiting: a long poll would report it only when it ends
        var commands = client.TakeCommands(connected ? WaitSeconds : 0, out status);
        if (stop.WaitOne(0)) break;
        if (commands == null) {
          SetConnected(false);
          if (stop.WaitOne(retry)) break;
          retry = Math.Min(retry * 2, RetryMaxMs);
          continue;
        }
        retry = RetryMinMs;
        SetConnected(true);
        foreach (var c in commands) {
          if (stop.WaitOne(0)) return;
          try { onCommand(c); } catch (Exception) { }
        }
      }
    }

    void SetConnected(bool now) {
      if (now == connected) return;
      connected = now;
      if (!stopped) try { onConnected(now); } catch (Exception) { }
    }

    /// <summary>Ends the thread: the pending long poll is aborted; waits up to <paramref name="joinMs"/> for it.</summary>
    public void Stop(int joinMs) {
      stopped = true;
      stop.Set();
      client.Abort();
      if (thread.IsAlive) thread.Join(joinMs);
    }

    public void Dispose() { Stop(2000); }
  }
}
