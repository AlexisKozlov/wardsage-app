// Wardsage - the update download that survives a stalling connection (app/Updater.cs uses it; the pure parts live here so the tests can run them).
//
// Seen in the site's logs: a part of the clients get the first 66-197 KB of the exe and then the stream stands still (the server finished in 0.5 s;
// the client dies after its read timeout). So the file is fetched in small Range requests (ChunkBytes each), every request on a NEW connection,
// the read timeout is short (IdleReadMs), a stalled connection is dropped and the next request starts at the same offset. The bytes already
// received stay in a .part file (kept between attempts and between runs); a server that ignores Range (answers 200) is read whole.
// After the file is complete the caller checks size, SHA-256 and the RSA signature as before (Updater.Verify), then moves it into place.
// Only five failed attempts in a row without a single new byte stop the round (Stalled); the caller waits UpdateRetry's backoff then.
using System;
using System.Globalization;
using System.IO;
using System.Net;

namespace RealmForge {
  /// <summary>Which hosts the update file may come from.</summary>
  public static class UpdateHosts {
    /// <summary>Hosts besides the site's own that may serve the exe (https only). Add a mirror's host here.</summary>
    public static readonly string[] Mirrors = { "wardsage.com" };
    /// <summary>The file's address is the feed's own host and scheme, or one of <see cref="Mirrors"/> over https.</summary>
    public static bool Allowed(Uri site, Uri file) {
      if (site == null || file == null) return false;
      if (file.Host == site.Host && file.Scheme == site.Scheme) return true;
      if (file.Scheme != Uri.UriSchemeHttps) return false;
      foreach (var h in Mirrors) if (string.Equals(file.Host, h, StringComparison.OrdinalIgnoreCase)) return true;
      return false;
    }
  }

  /// <summary>The wait between rounds of the update check after a failed download: 1, 5, 30, 30, 30, 30 minutes, +-20 %, never above 30 minutes;
  /// six failed rounds in a row end the retrying (the next check comes with the long timer: <see cref="GiveUpWaitMs"/>). A success resets it.</summary>
  public static class UpdateRetry {
    public const int MaxRounds = 6;
    public const double Jitter = 0.2;
    public const int CapMs = 30 * 60 * 1000;
    public const int GiveUpWaitMs = 6 * 60 * 60 * 1000;
    static readonly int[] Minutes = { 1, 5, 30, 30, 30, 30 };
    /// <summary>The wait after the <paramref name="failureNo"/>-th failed round in a row (1 = first); <paramref name="rnd01"/> in [0,1) picks the jitter.</summary>
    public static int DelayMs(int failureNo, double rnd01) {
      int i = failureNo < 1 ? 0 : failureNo > Minutes.Length ? Minutes.Length - 1 : failureNo - 1;
      if (rnd01 < 0) rnd01 = 0; if (rnd01 > 1) rnd01 = 1;
      double ms = Minutes[i] * 60000.0 * (1 - Jitter + 2 * Jitter * rnd01);
      return (int)Math.Min(ms, CapMs);
    }
    public static bool GiveUp(int failures) { return failures >= MaxRounds; }
  }

  /// <summary>One answer to a ranged request. Status 206: bytes <see cref="RangeStart"/>.. of a file of <see cref="Total"/> bytes; 200: the whole file from 0.</summary>
  public sealed class RangeReply : IDisposable {
    public int Status;
    public long RangeStart;
    public long Total = -1;
    public Stream Body;
    public void Dispose() { if (Body != null) Body.Dispose(); }
  }

  /// <summary>Opens a NEW connection for the bytes [offset, offset + length) of the file (length only asks; a server may answer with more or the whole file).</summary>
  public delegate RangeReply RangeOpener(long offset, long length);

  public sealed class DownloadResult {
    public bool Complete, Mismatch;
    public long Have;
    public int Opens, Stalls;
    public string Why = "";
  }

  public static class ChunkedDownload {
    public const int ChunkBytes = 256 * 1024, MaxNoProgress = 5, IdleReadMs = 9000;

    /// <summary>Fills <paramref name="partPath"/> up to <paramref name="size"/> bytes (continuing what is in it). Stops with Why = "stalled" after
    /// <paramref name="maxNoProgress"/> attempts in a row that brought no byte beyond the best point so far; Mismatch = the server's file is not
    /// <paramref name="size"/> long (another build: the caller drops the part). <paramref name="pause"/> is called between attempts without progress (may be null).</summary>
    public static DownloadResult Run(RangeOpener open, string partPath, long size, int chunk, int maxNoProgress, Action<int> pause, Action<string> log) {
      var res = new DownloadResult();
      long have = 0;
      try { if (File.Exists(partPath)) have = new FileInfo(partPath).Length; } catch (IOException) { have = 0; }
      if (have > size) { try { File.Delete(partPath); } catch (IOException) { } have = 0; }
      long best = have; int noProgress = 0; bool wholeOnly = false;
      using (var fs = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) {
        fs.SetLength(have); fs.Seek(have, SeekOrigin.Begin);
        var buf = new byte[16384];
        while (have < size) {
          if (noProgress >= maxNoProgress) { res.Why = "stalled"; break; }
          long want = Math.Min(chunk, size - have);
          try {
            using (var r = open(have, wholeOnly ? size - have : want)) {
              res.Opens++;
              if (r == null || r.Body == null) throw new IOException("no reply");
              long limit;
              if (r.Status == 206) {
                if (r.Total >= 0 && r.Total != size) { res.Mismatch = true; res.Why = "size changed on the server"; break; }
                if (r.RangeStart != have) throw new IOException("the server answered another range");
                limit = size - have;   // (a longer answer than asked is taken as far as it comes)
              } else if (r.Status == 200) {
                if (r.Total >= 0 && r.Total != size) { res.Mismatch = true; res.Why = "size changed on the server"; break; }
                wholeOnly = true;   // no Range support: the whole file from its start, whatever was kept
                if (have != 0) { fs.SetLength(0); fs.Seek(0, SeekOrigin.Begin); have = 0; }
                limit = size;
              } else throw new IOException("http " + r.Status);
              int n;
              while (limit > 0 && (n = r.Body.Read(buf, 0, (int)Math.Min(buf.Length, limit))) > 0) {
                fs.Write(buf, 0, n); have += n; limit -= n;
                if (r.Status == 206 && have - (r.RangeStart) >= want && !wholeOnly) break;   // this chunk is in; the next one on a new connection
              }
              if (have < size && r.Status == 200 && limit > 0) throw new IOException("the body ended early");
            }
          } catch (Exception e) {
            res.Stalls++;
            if (log != null) log("update: connection dropped at " + have + " of " + size + " (" + e.Message + ")");
          }
          fs.Flush();
          if (have > best) { best = have; noProgress = 0; }
          else { noProgress++; if (pause != null) pause(noProgress); }
        }
        fs.Flush();
      }
      res.Have = have; res.Complete = have == size && !res.Mismatch;
      if (!res.Complete && res.Why == "") res.Why = "incomplete";
      return res;
    }
  }

  /// <summary>The real HTTP opener of the update download (HttpWebRequest); the tests run it against a local listener.</summary>
  public static class RangeHttp {
    /// <summary>The player's PC cannot tell the update from a normal page: a stalled connection is dropped after ChunkedDownload.IdleReadMs, not 60 s.</summary>
    public static RangeReply Open(string url, long offset, long length) {
      const SecurityProtocolType Tls12 = (SecurityProtocolType)3072;
      try { ServicePointManager.SecurityProtocol |= Tls12; } catch (NotSupportedException) { }
      var req = (HttpWebRequest)WebRequest.Create(url);
      req.Method = "GET"; req.UserAgent = SyncClient.UserAgent; req.Timeout = 15000; req.ReadWriteTimeout = ChunkedDownload.IdleReadMs;
      req.AllowAutoRedirect = false; req.KeepAlive = false;   // (a new connection for every request)
      req.Headers["Cache-Control"] = "no-cache";
      req.AddRange(offset, offset + length - 1);   // Range is restricted: Headers["Range"] throws ArgumentException on .NET Framework
      HttpWebResponse resp;
      try { resp = (HttpWebResponse)req.GetResponse(); }
      catch (WebException we) {
        var er = we.Response as HttpWebResponse;
        if (er == null) throw;
        int code = (int)er.StatusCode; er.Close();
        return new RangeReply { Status = code, Body = new MemoryStream(new byte[0]) };
      }
      var r = new RangeReply { Status = (int)resp.StatusCode, Body = resp.GetResponseStream() };
      if (r.Status == 206) {
        // Content-Range: bytes 0-262143/6810624
        string cr = resp.Headers["Content-Range"] ?? "";
        int dash = cr.IndexOf('-'), slash = cr.IndexOf('/'); long a, tot;
        if (cr.StartsWith("bytes ", StringComparison.Ordinal) && dash > 6 && slash > dash
            && long.TryParse(cr.Substring(6, dash - 6), NumberStyles.None, CultureInfo.InvariantCulture, out a)
            && long.TryParse(cr.Substring(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out tot)) { r.RangeStart = a; r.Total = tot; }
        else r.RangeStart = -1;
      } else if (r.Status == 200) r.Total = resp.ContentLength;
      return new RangeReply { Status = r.Status, RangeStart = r.RangeStart, Total = r.Total, Body = new ResponseBody(resp, r.Body) };
    }

    /// <summary>Closes the response together with its stream.</summary>
    sealed class ResponseBody : Stream {
      readonly HttpWebResponse resp; readonly Stream s;
      public ResponseBody(HttpWebResponse r, Stream st) { resp = r; s = st; }
      public override int Read(byte[] b, int o, int c) { return s.Read(b, o, c); }
      public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
      public override void Flush() { }
      public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
      public override void SetLength(long v) { throw new NotSupportedException(); }
      public override bool CanRead { get { return true; } }
      public override bool CanSeek { get { return false; } }
      public override bool CanWrite { get { return false; } }
      public override long Length { get { throw new NotSupportedException(); } }
      public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
      protected override void Dispose(bool d) { if (d) { try { s.Dispose(); } catch (Exception) { } try { resp.Close(); } catch (Exception) { } } base.Dispose(d); }
    }
  }
}
