// Wardsage app - the TEST build's screen recorder, the parts that need no screen: names, sizes, the frames.jsonl line, the zip parts,
// the upload queue. The capture itself is app/RecCapture.cs.
//
// Contract (implemented by the site, staging only):
//   POST {site}/api/extractor/test-recording?rec=<id>&part=<n>   Authorization: Bearer <sync code>, X-RF-Player (as test-log)
//        body application/zip <= 8 MB (frame_0001.jpg ... and frames.jsonl); rec [A-Za-z0-9-]{8,40}, part 0..119
//        200 ok | 404 (the endpoint is off: stop uploading, keep the files) | 413 (part over the limit: drop that part, log it)
//        | 429 (quota: stop uploading this recording, keep the files) | 401/5xx/no connection: keep the part, retry later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RealmForge {
  /// <summary>One frame the overlay drew, in the game window's client px.</summary>
  public sealed class RecFrameRect { public string Kind; public int X, Y, W, H; }

  /// <summary>One element the overlay showed (a frame, its owl, a hint box, the scroll indicator, a cell mark, a guide highlight) in the game window's
  /// client px; Src = the overlay window it lives in ("glow" / "tip"). Draw paints it (a System.Drawing.Graphics, bitmap origin on the screen,
  /// bitmap size, pulse phase or negative) with the overlay's own drawing code (app/OverlayPaint.cs); null in tests that only read the list.
  /// Layer: drawn into a transparent layer first (it cuts holes: the dimming of a guide highlight). Listed = false: drawn, not named in the sidecar.</summary>
  public sealed class RecOverlayEl {
    public string Key = "", Src = "";
    public int X, Y, W, H, SceneX, SceneY;
    public bool Listed = true, Layer;
    public Action<object, int, int, int, int, float> Draw;
  }

  /// <summary>What the overlay's last tick decided, for the recorder (the overlay builds a new one per tick and publishes it; a published one
  /// is never changed). NaN = no value.</summary>
  public sealed class RecInfo {
    public string Screen = "-", Step = "-";
    public readonly List<RecFrameRect> Frames = new List<RecFrameRect>();
    /// <summary>The overlay's elements at the end of the tick, in drawing order (RecOverlayEl); the recorder draws them onto its frames.</summary>
    public readonly List<RecOverlayEl> Overlay = new List<RecOverlayEl>();
    public double InvRaw = double.NaN, InvRow1 = double.NaN, InvSmooth = double.NaN, FdRaw = double.NaN, FdSmooth = double.NaN;
    public long AtMs;
    // the raw inputs of the tick (src/RecInputs.cs): the overlay clock, the last screen read before the tick ended, the context, the sell hint's inventory, the Foundry list as drawn, the row 1 the list cell was placed from
    public long TickNow, ReadSeq;
    public string Ctx, SInv, SInvList, Fd;
    public double PlaceRow1 = double.NaN;
    public InventoryScreen SellInv;

    /// <summary>Set while a recording runs: the overlay only collects then.</summary>
    public static volatile bool Active;
    static readonly object gate = new object();
    static RecInfo current;

    public static long NowMs() { return Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency; }
    public static void Publish(RecInfo i) { if (i == null) return; i.AtMs = NowMs(); lock (gate) current = i; }
    public static RecInfo Take() { lock (gate) return current; }
    public static void Clear() { lock (gate) current = null; }
  }

  /// <summary>The per-frame sidecar of the overlay elements drawn onto the picture: {"t":ms,"els":[{"key","src","x","y","w","h"}]} (client px, the
  /// frame's own w x h are in frames.jsonl); null when none was drawn.</summary>
  public static class RecOverlayJson {
    public static string Line(long t, IList<RecOverlayEl> els) {
      var sb = new StringBuilder(); int n = 0;
      sb.Append("{\"t\":").Append(t.ToString(CultureInfo.InvariantCulture)).Append(",\"els\":[");
      if (els != null)
        foreach (var e in els) {
          if (e == null || !e.Listed) continue;
          if (n++ > 0) sb.Append(',');
          sb.Append("{\"key\":").Append(MiniJson.Quote(e.Key)).Append(",\"src\":").Append(MiniJson.Quote(e.Src))
            .Append(",\"x\":").Append(e.X.ToString(CultureInfo.InvariantCulture)).Append(",\"y\":").Append(e.Y.ToString(CultureInfo.InvariantCulture))
            .Append(",\"w\":").Append(e.W.ToString(CultureInfo.InvariantCulture)).Append(",\"h\":").Append(e.H.ToString(CultureInfo.InvariantCulture)).Append('}');
        }
      return n == 0 ? null : sb.Append("]}").ToString();
    }
  }

  public static class RecNames {
    public const int MaxPart = 119;
    static readonly Regex IdRx = new Regex("^[A-Za-z0-9-]{8,40}$");

    /// <summary>yyyyMMdd-HHmmss-xxxxxx (6 random hex).</summary>
    public static string NewId(DateTime now, Random rnd) {
      var b = new byte[3]; rnd.NextBytes(b);
      return now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + b[0].ToString("x2") + b[1].ToString("x2") + b[2].ToString("x2");
    }
    public static bool IsValidId(string id) { return id != null && IdRx.IsMatch(id); }
    public static string PartFile(int part) { return "part_" + part.ToString("000", CultureInfo.InvariantCulture) + ".zip"; }
    /// <summary>The overlay sidecar of frame n: the elements drawn onto it (RecOverlayJson).</summary>
    public static string OverlayFile(int n) { return "ov_" + n.ToString("0000", CultureInfo.InvariantCulture) + ".json"; }
    public static string FrameFile(int n) { return "frame_" + n.ToString("0000", CultureInfo.InvariantCulture) + ".jpg"; }
    /// <summary>The part number of a part file name, -1 when it is not one.</summary>
    public static int PartOf(string file) {
      var m = Regex.Match(file ?? "", "^(?:part_)?([0-9]{3})\\.zip$");
      return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
    }
  }

  /// <summary>The recorder's own CPU (its capture and upload threads, each thread's CPU time by GetThreadTimes): the load line splits it from
  /// the rest of the app - a player's app records nothing unless the player presses the button, the lab records every scenario.</summary>
  public static class RecCpu {
    static long ticks100ns;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
    /// <summary>The calling thread's CPU time so far (100 ns units; 0 = not read).</summary>
    public static long ThreadNow() {
      try { long c, e, k, u; return GetThreadTimes(GetCurrentThread(), out c, out e, out k, out u) ? k + u : 0; } catch (Exception) { return 0; }
    }
    /// <summary>Counts the calling thread's CPU since <paramref name="since"/> (from <see cref="ThreadNow"/>) and returns the new mark.</summary>
    public static long Count(long since) { long n = ThreadNow(); if (n > since && since > 0) Add((n - since) / 10000.0); return n; }
    public static void Add(double ms) { if (ms > 0) System.Threading.Interlocked.Add(ref ticks100ns, (long)(ms * 10000)); }
    /// <summary>The recorder's CPU in ms since the app started.</summary>
    public static double TotalMs { get { return System.Threading.Interlocked.Read(ref ticks100ns) / 10000.0; } }
  }

  public static class RecSize {
    public const int MaxWidth = 1024;
    /// <summary>The frame's size so that the width is at most <paramref name="maxW"/> (aspect kept, never enlarged); the scale used.</summary>
    public static double Fit(int w, int h, int maxW, out int nw, out int nh) {
      if (w <= 0 || h <= 0) { nw = Math.Max(1, w); nh = Math.Max(1, h); return 1; }
      double s = w > maxW ? (double)maxW / w : 1.0;
      nw = Math.Max(1, (int)Math.Round(w * s));
      nh = Math.Max(1, (int)Math.Round(h * s));
      return s;
    }
  }

  public static class RecJson {
    static string N(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "null" : Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture); }

    /// <summary>One frames.jsonl line (no line break). <paramref name="info"/> null = nothing known (screen and step "-").</summary>
    public static string Line(long t, int w, int h, double scale, bool bg, RecInfo info, int cx, int cy) { return Line(t, w, h, scale, bg, info, cx, cy, null); }

    /// <param name="inputs">the line's "in" object (src/RecInputs.cs, RecLineEncoder), null = none</param>
    public static string Line(long t, int w, int h, double scale, bool bg, RecInfo info, int cx, int cy, string inputs) {
      var sb = new StringBuilder();
      sb.Append("{\"t\":").Append(t.ToString(CultureInfo.InvariantCulture))
        .Append(",\"w\":").Append(w.ToString(CultureInfo.InvariantCulture)).Append(",\"h\":").Append(h.ToString(CultureInfo.InvariantCulture))
        .Append(",\"scale\":").Append(Math.Round(scale, 4).ToString("0.####", CultureInfo.InvariantCulture));
      if (bg) sb.Append(",\"bg\":true");
      sb.Append(",\"screen\":").Append(MiniJson.Quote(info != null ? info.Screen : "-"));
      sb.Append(",\"step\":").Append(MiniJson.Quote(info != null ? info.Step : "-"));
      sb.Append(",\"frames\":[");
      if (info != null)
        for (int i = 0; i < info.Frames.Count; i++) {
          var f = info.Frames[i];
          if (i > 0) sb.Append(',');
          sb.Append("{\"kind\":").Append(MiniJson.Quote(f.Kind)).Append(",\"x\":").Append(f.X.ToString(CultureInfo.InvariantCulture)).Append(",\"y\":").Append(f.Y.ToString(CultureInfo.InvariantCulture))
            .Append(",\"w\":").Append(f.W.ToString(CultureInfo.InvariantCulture)).Append(",\"h\":").Append(f.H.ToString(CultureInfo.InvariantCulture)).Append('}');
        }
      sb.Append("],\"scroll\":{");
      sb.Append("\"invRaw\":").Append(N(info != null ? info.InvRaw : double.NaN)).Append(",\"invRow1\":").Append(N(info != null ? info.InvRow1 : double.NaN))
        .Append(",\"invSmooth\":").Append(N(info != null ? info.InvSmooth : double.NaN))
        .Append(",\"fdRaw\":").Append(N(info != null ? info.FdRaw : double.NaN)).Append(",\"fdSmooth\":").Append(N(info != null ? info.FdSmooth : double.NaN));
      sb.Append("},\"cursor\":[").Append(cx.ToString(CultureInfo.InvariantCulture)).Append(',').Append(cy.ToString(CultureInfo.InvariantCulture)).Append(']');
      if (inputs != null) sb.Append(",\"in\":").Append(inputs);
      return sb.Append('}').ToString();
    }

    /// <summary>The state event for the interface: {"ev":"rec",...}.</summary>
    public static string State(bool on, int sec, int sent, int total, string why) {
      return "{\"ev\":\"rec\",\"on\":" + (on ? "true" : "false") + ",\"sec\":" + sec.ToString(CultureInfo.InvariantCulture)
        + ",\"sent\":" + sent.ToString(CultureInfo.InvariantCulture) + ",\"total\":" + total.ToString(CultureInfo.InvariantCulture)
        + ",\"why\":" + MiniJson.Quote(why ?? "") + "}";
    }
  }

  /// <summary>One zip part being filled: frames (stored as they are, a jpeg does not shrink) and the frames.jsonl lines. Written to a .tmp
  /// file and renamed when closed, so a part file on disk is always whole.</summary>
  public sealed class RecPartWriter {
    readonly string path, tmp;
    FileStream fs; ZipArchive zip;
    readonly StringBuilder lines = new StringBuilder();
    public int Frames { get; private set; }
    /// <summary>About what the part weighs (the frames plus the lines).</summary>
    public long Bytes { get; private set; }

    public RecPartWriter(string dir, int part) {
      Directory.CreateDirectory(dir);
      path = Path.Combine(dir, RecNames.PartFile(part)); tmp = path + ".tmp";
      fs = new FileStream(tmp, FileMode.Create, FileAccess.Write);
      zip = new ZipArchive(fs, ZipArchiveMode.Create, false);
    }

    public void Add(string name, byte[] jpg, string line) {
      var e = zip.CreateEntry(name, CompressionLevel.NoCompression);
      using (var s = e.Open()) s.Write(jpg, 0, jpg.Length);
      lines.Append(line).Append('\n');
      Frames++; Bytes += jpg.Length + line.Length + 1;
    }

    /// <summary>A small text file of the recording (layout.json, geometry.json), compressed.</summary>
    public void AddText(string name, string text) {
      var e = zip.CreateEntry(name, CompressionLevel.Optimal);
      var b = new UTF8Encoding(false).GetBytes(text ?? "");
      long before = 0; try { before = fs.Position; } catch (Exception) { }
      using (var s = e.Open()) s.Write(b, 0, b.Length);
      long grew = 0; try { grew = fs.Position - before; } catch (Exception) { }
      Bytes += Math.Max(grew, b.Length / 4);
    }

    /// <summary>Finishes the file; the path of the part, null when nothing was added (then no file is left).</summary>
    public string Close() {
      try {
        if (Frames > 0) {
          var e = zip.CreateEntry("frames.jsonl", CompressionLevel.Optimal);
          using (var s = e.Open()) { var b = new UTF8Encoding(false).GetBytes(lines.ToString()); s.Write(b, 0, b.Length); }
        }
        zip.Dispose(); fs.Dispose();
        if (Frames == 0) { File.Delete(tmp); return null; }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        return path;
      } catch (Exception) { try { File.Delete(tmp); } catch (Exception) { } return null; }
    }
  }

  public static class RecClient {
    /// <summary>What the last Send on this thread ended with: the HTTP status (0 = none, no answer) and a short kind ("ok", "http 413", "timeout",
    /// "no connection", "bad input" ...), for the log line.</summary>
    [ThreadStatic] public static int LastStatus;
    [ThreadStatic] public static string LastKind;

    /// <summary>Sends one part. Never throws. Ok = delivered; Off = the site has no such endpoint (stop); Later = keep the part.</summary>
    public static TestLogClient.Result Send(string site, string code, string rec, int part, string file) {
      try {
        LastStatus = 0; LastKind = "";
        if (string.IsNullOrEmpty(site) || !SyncClient.IsValidCode(code) || !RecNames.IsValidId(rec) || part < 0 || part > RecNames.MaxPart) { LastKind = "bad input"; return TestLogClient.Result.Later; }
        byte[] body = File.ReadAllBytes(file);
        SyncClient.EnableTls12();
        var req = (HttpWebRequest)WebRequest.Create(site.TrimEnd('/') + "/api/extractor/test-recording?rec=" + rec + "&part=" + part.ToString(CultureInfo.InvariantCulture));
        req.Method = "POST";
        req.ConnectionGroupName = "rec";   // its own connections: uploads must not block the plan list
        req.ContentType = "application/zip";
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Headers["Authorization"] = "Bearer " + code;
        SyncClient.AddPlayer(req);
        req.Timeout = 60000;
        req.ReadWriteTimeout = 60000;
        req.AllowAutoRedirect = false;
        req.ContentLength = body.Length;
        using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
        HttpWebResponse resp;
        try { resp = (HttpWebResponse)req.GetResponse(); }
        catch (WebException e) {
          resp = e.Response as HttpWebResponse;
          if (resp == null) { LastKind = e.Status == WebExceptionStatus.Timeout ? "timeout" : "no answer (" + e.Status + ")"; return TestLogClient.Result.Later; }
        }
        using (resp) {
          int c = (int)resp.StatusCode;
          LastStatus = c; LastKind = c == 200 ? "ok" : "http " + c;
          if (c == 200) return TestLogClient.Result.Ok;
          if (c == 404) return TestLogClient.Result.Off;
          return TestLogClient.Result.Later;
        }
      } catch (WebException e) { LastKind = e.Status == WebExceptionStatus.Timeout ? "timeout" : "no answer (" + e.Status + ")"; return TestLogClient.Result.Later; }
      catch (Exception e) { LastKind = "error " + e.GetType().Name; return TestLogClient.Result.Later; }
    }
  }

  /// <summary>Sends the closed parts in order from a background thread; a failed one is tried again after 30 s. The thread ends when the
  /// recording is closed and the queue is empty (or the site said 404: the files stay on disk).</summary>
  public sealed class RecUploader {
    sealed class Item { public int Part; public string Path; }
    readonly string rec;
    readonly Func<string> site, code;
    readonly Action<string> log;
    readonly object gate = new object();
    readonly List<Item> queue = new List<Item>();
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    bool closed, started, off, limited;
    int sent, total, dropped;
    Thread th;
    /// <summary>The wait after a failed send (ms); a field so a test can shorten it.</summary>
    public int RetryMs = 30000;
    /// <summary>The send function (tests replace it).</summary>
    public Func<string, string, string, int, string, TestLogClient.Result> Sender = RecClient.Send;

    public RecUploader(string rec, Func<string> site, Func<string> code, Action<string> log) { this.rec = rec; this.site = site; this.code = code; this.log = log ?? (s => { }); }

    public int Sent { get { lock (gate) return sent; } }
    public int Total { get { lock (gate) return total; } }
    public bool Off { get { lock (gate) return off; } }
    /// <summary>Stopped because of the site's quota (429), not because the endpoint is missing.</summary>
    public bool Limited { get { lock (gate) return limited; } }
    /// <summary>Parts the site refused as too large.</summary>
    public int Dropped { get { lock (gate) return dropped; } }
    public bool Idle { get { lock (gate) return queue.Count == 0 && closed; } }

    public void Add(int part, string path) {
      lock (gate) {
        total++;
        if (off) return;
        queue.Add(new Item { Part = part, Path = path });
        if (!started) { started = true; th = new Thread(Run) { IsBackground = true, Name = "rec-upload" }; th.Start(); }
      }
      wake.Set();
    }

    /// <summary>No more parts will come.</summary>
    public void Close() { lock (gate) closed = true; wake.Set(); }

    void Run() {
      long retryAt = 0;
      while (true) {
        Item it = null;
        lock (gate) {
          if (off) return;
          if (queue.Count > 0) it = queue[0]; else if (closed) return;
        }
        long now = RecInfo.NowMs();
        if (it == null || now < retryAt) { wake.WaitOne(it == null ? 1000 : (int)Math.Max(50, Math.Min(1000, retryAt - now))); continue; }
        long size = 0; try { size = new FileInfo(it.Path).Length; } catch (Exception) { }
        RecClient.LastStatus = 0; RecClient.LastKind = "";
        long c0 = RecCpu.ThreadNow();
        var r = Sender(site(), code(), rec, it.Part, it.Path);
        RecCpu.Count(c0);
        int status = RecClient.LastStatus; string kind = RecClient.LastKind;
        if (r == TestLogClient.Result.Later && status == 413) {
          // over the server's limit: it will never be accepted, so do not retry it forever
          lock (gate) { queue.Remove(it); dropped++; }
          log("recording " + rec + ": part " + it.Part + " dropped, the site refused it as too large (http 413, " + size + " bytes)");
        } else if (r == TestLogClient.Result.Later && status == 429) {
          // the quota: more tries only add load; the files stay on disk
          lock (gate) { off = true; limited = true; queue.Clear(); }
          log("recording " + rec + ": the site's upload quota is reached (http 429, part " + it.Part + ", " + size + " bytes), no more parts of this recording are sent, the files stay in " + Path.GetDirectoryName(it.Path));
          return;
        } else if (r == TestLogClient.Result.Ok) {
          lock (gate) { queue.Remove(it); sent++; }
          try { File.Delete(it.Path); } catch (Exception) { }
          log("recording " + rec + ": part " + it.Part + " sent (" + size + ")");
        } else if (r == TestLogClient.Result.Off) {
          lock (gate) { off = true; queue.Clear(); }
          log("recording " + rec + ": the site has no recording endpoint, the files stay in " + Path.GetDirectoryName(it.Path));
          return;
        } else {
          retryAt = RecInfo.NowMs() + RetryMs;
          log("recording " + rec + ": part " + it.Part + " not sent (" + (string.IsNullOrEmpty(kind) ? "no detail" : kind) + ", " + size + " bytes), will retry");
        }
      }
    }
  }
}
