// Wardsage.exe — automatic updates.
//
// The site publishes /downloads/latest.json: {version, url, size, sha256, sig, notes:{ru,en}}. The app checks it at
// start and every few hours, downloads a newer RealmForge.exe in the background into %LOCALAPPDATA%\Wardsage\update\
// and puts it in place at the next start (or when the player presses «Перезапустить»): the running exe is renamed to
// *.old (Windows allows that), the new one is copied to its name and started.
//
// Only a file signed with the project's update key is ever installed: RSA-3072, PKCS#1 v1.5 over SHA-256 of the whole
// exe (tools/release-app.mjs signs; the private key never leaves the release PC). Size and SHA-256 are checked too.
// A hacked site or CDN therefore cannot push a different program to the players.
//
// The download itself: Range requests of 256 KB on a new connection each, a 9 s idle read timeout, a .part file that is kept between attempts and
// runs, the file written atomically (the complete .part is verified, then renamed) - src/UpdateDownload.cs. Redirects are not followed.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace RealmForge {
  sealed class UpdateInfo {
    public string Version, Url, Sha256, Sig, NotesRu, NotesEn;
    public long Size;
  }

  static class Updater {
    // public keys: src/UpdateKeys.cs (the test build also trusts the key of the automatic test builds)
    const long MaxSize = 64L << 20;

    static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Channel.FolderName, "update"); } }
    static string StagedExe(string v) { return Path.Combine(Dir, Channel.StagedPrefix + v + ".exe"); }
    /// <summary>The unfinished download: the version and the start of the hash in its name, so another build never continues it.</summary>
    static string PartPath(UpdateInfo u) { string h = (u.Sha256 ?? "").ToLowerInvariant(); return Path.Combine(Dir, Channel.StagedPrefix + u.Version + "-" + (h.Length >= 8 ? h.Substring(0, 8) : "x") + ".part"); }
    /// <summary>The version a staged file's name starts with (1.6.68 from "rf-1.6.68-ab12cd34.part"), "" when none.</summary>
    static string VersionOfFile(string path) {
      string n = Path.GetFileName(path); if (!n.StartsWith(Channel.StagedPrefix, StringComparison.Ordinal)) return "";
      n = n.Substring(Channel.StagedPrefix.Length); int i = 0;
      while (i < n.Length && (char.IsDigit(n[i]) || n[i] == '.')) i++;
      return n.Substring(0, i).TrimEnd('.');
    }
    static string StagedMeta(string v) { return Path.Combine(Dir, Channel.StagedPrefix + v + ".json"); }

    /// <summary>Is <paramref name="candidate"/> a later version than <paramref name="current"/> (1.3.0 style; missing
    /// parts = 0; anything unparsable is never newer)?</summary>
    public static bool Newer(string candidate, string current) {
      int[] a = Parse(candidate), b = Parse(current);
      if (a == null || b == null) return false;
      for (int i = 0; i < 4; i++) if (a[i] != b[i]) return a[i] > b[i];
      return false;
    }

    static int[] Parse(string v) {
      if (string.IsNullOrEmpty(v) || v.Length > 20) return null;
      var r = new int[4]; var p = v.Split('.');
      if (p.Length > 4) return null;
      for (int i = 0; i < p.Length; i++) if (!int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out r[i])) return null;
      return r;
    }

    /// <summary>Is <paramref name="file"/> exactly the build the manifest describes, signed with the update key?</summary>
    public static bool Verify(byte[] file, UpdateInfo u) {
      if (file == null || u == null || file.LongLength != u.Size || string.IsNullOrEmpty(u.Sig)) return false;
      using (var sha = SHA256.Create()) {
        string hex = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        if (hex != (u.Sha256 ?? "").ToLowerInvariant()) return false;
      }
      try {
        return UpdateKeys.VerifyAny(file, Convert.FromBase64String(u.Sig), UpdateKeys.Accepted(Channel.IsTest));
      } catch (Exception e) { Log.Write("update: verify " + e.Message); return false; }
    }

    public static UpdateInfo FromJson(string json) {
      var d = MiniJson.AsObject(MiniJson.TryParse(json));
      if (d == null) return null;
      var u = new UpdateInfo {
        Version = MiniJson.GetString(d, "version"), Url = MiniJson.GetString(d, "url"),
        Sha256 = MiniJson.GetString(d, "sha256"), Sig = MiniJson.GetString(d, "sig")
      };
      object v; if (d.TryGetValue("size", out v) && v is double) u.Size = (long)(double)v;
      var notes = d.TryGetValue("notes", out v) ? MiniJson.AsObject(v) : null;
      if (notes != null) { u.NotesRu = MiniJson.GetString(notes, "ru"); u.NotesEn = MiniJson.GetString(notes, "en"); }
      return Parse(u.Version) == null || string.IsNullOrEmpty(u.Url) || u.Size <= 0 || u.Size > MaxSize ? null : u;
    }

    static string ToJson(UpdateInfo u) {
      return "{\"version\":" + MiniJson.Quote(u.Version) + ",\"url\":" + MiniJson.Quote(u.Url) + ",\"size\":" + u.Size.ToString(CultureInfo.InvariantCulture)
        + ",\"sha256\":" + MiniJson.Quote(u.Sha256 ?? "") + ",\"sig\":" + MiniJson.Quote(u.Sig ?? "")
        + ",\"notes\":{\"ru\":" + MiniJson.Quote(u.NotesRu ?? "") + ",\"en\":" + MiniJson.Quote(u.NotesEn ?? "") + "}}";
    }

    /// <summary>The player's PC cannot tell the update from a normal page: a stalled connection is dropped after IdleReadMs, not 60 s.</summary>
    static RangeReply OpenRange(string url, long offset, long length) {
      const SecurityProtocolType Tls12 = (SecurityProtocolType)3072;
      try { ServicePointManager.SecurityProtocol |= Tls12; } catch (NotSupportedException) { }
      var req = (HttpWebRequest)WebRequest.Create(url);
      req.Method = "GET"; req.UserAgent = SyncClient.UserAgent; req.Timeout = 15000; req.ReadWriteTimeout = ChunkedDownload.IdleReadMs;
      req.AllowAutoRedirect = false; req.KeepAlive = false;   // (a new connection for every request)
      req.Headers["Cache-Control"] = "no-cache";
      req.Headers["Range"] = "bytes=" + offset.ToString(CultureInfo.InvariantCulture) + "-" + (offset + length - 1).ToString(CultureInfo.InvariantCulture);
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

    static byte[] Fetch(string url, long max) {
      const SecurityProtocolType Tls12 = (SecurityProtocolType)3072;
      try { ServicePointManager.SecurityProtocol |= Tls12; } catch (NotSupportedException) { }
      var req = (HttpWebRequest)WebRequest.Create(url);
      req.Method = "GET"; req.UserAgent = SyncClient.UserAgent; req.Timeout = 30000; req.ReadWriteTimeout = 60000;
      req.AllowAutoRedirect = false;
      req.Headers["Cache-Control"] = "no-cache";
      using (var resp = (HttpWebResponse)req.GetResponse())
      using (var s = resp.GetResponseStream())
      using (var ms = new MemoryStream()) {
        var buf = new byte[65536]; int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > max) throw new IOException("too large"); }
        return ms.ToArray();
      }
    }

    /// <summary>Checks the site; a newer signed build is downloaded and kept for the next start. Returns it, or null
    /// (no update, or it could not be fetched / verified — logged).</summary>
    /// <summary>The last check failed on the network (the feed or the file could not be fetched): try again soon.</summary>
    public static bool LastFailed;

    public static UpdateInfo CheckAndDownload(string site) {
      LastFailed = false;
      try {
        Uri baseUri; if (!Uri.TryCreate(site, UriKind.Absolute, out baseUri)) return null;
        var u = FromJson(Encoding.UTF8.GetString(Fetch(site.TrimEnd('/') + Channel.FeedPath, 64 * 1024)));
        if (u == null || !Newer(u.Version, Program.Version)) return null;
        if (Staged(u.Version) != null) return u;
        // the build comes from the same site (host and scheme), or from a listed mirror host (UpdateHosts.Mirrors)
        Uri file; if (!Uri.TryCreate(baseUri, u.Url, out file) || !UpdateHosts.Allowed(baseUri, file)) return null;
        Directory.CreateDirectory(Dir);
        string part = PartPath(u), url = file.AbsoluteUri;
        var dl = ChunkedDownload.Run((off, len) => OpenRange(url, off, len), part, u.Size, ChunkedDownload.ChunkBytes, ChunkedDownload.MaxNoProgress,
          n => System.Threading.Thread.Sleep(Math.Min(3000, 500 * n)), Log.Write);
        if (dl.Mismatch) { try { File.Delete(part); } catch (Exception) { } Log.Write("update " + u.Version + ": the file on the server is not the one the feed describes"); LastFailed = true; return null; }
        if (!dl.Complete) { Log.Write("update " + u.Version + ": download " + dl.Why + " at " + dl.Have + " of " + u.Size + " (kept for the next try)"); LastFailed = true; return null; }
        var bytes = File.ReadAllBytes(part);
        if (!Verify(bytes, u)) { try { File.Delete(part); } catch (Exception) { } Log.Write("update " + u.Version + ": signature / hash check FAILED, not installed"); return null; }
        // atomic: the verified .part becomes the staged exe by one rename, the meta file by temp + rename
        string exe = StagedExe(u.Version), meta = StagedMeta(u.Version), tmp = meta + ".tmp";
        if (File.Exists(exe)) File.Delete(exe);
        File.Move(part, exe);
        File.WriteAllText(tmp, ToJson(u), new UTF8Encoding(false));
        if (File.Exists(meta)) File.Delete(meta);
        File.Move(tmp, meta);
        Log.Write("update " + u.Version + ": downloaded and verified");
        return u;
      } catch (Exception e) { Log.Write("update check: " + e.Message); LastFailed = true; return null; }
    }

    /// <summary>A downloaded, verified build of that version waiting to be put in place, or null.</summary>
    static UpdateInfo Staged(string version) {
      try {
        if (!File.Exists(StagedExe(version)) || !File.Exists(StagedMeta(version))) return null;
        var u = FromJson(File.ReadAllText(StagedMeta(version), Encoding.UTF8));
        return u != null && u.Version == version && Verify(File.ReadAllBytes(StagedExe(version)), u) ? u : null;
      } catch (Exception) { return null; }
    }

    /// <summary>The newest waiting build newer than this one, or null.</summary>
    public static UpdateInfo Waiting() {
      try {
        if (!Directory.Exists(Dir)) return null;
        UpdateInfo best = null;
        foreach (var f in Directory.GetFiles(Dir, Channel.StagedPrefix + "*.json")) {
          string v = Path.GetFileNameWithoutExtension(f).Substring(Channel.StagedPrefix.Length);
          if (Newer(v, Program.Version) && (best == null || Newer(v, best.Version))) { var u = Staged(v); if (u != null) best = u; }
        }
        return best;
      } catch (Exception) { return null; }
    }

    /// <summary>At start, before anything else: puts a waiting build in place of this exe and starts it. True = this
    /// process must exit now. Also removes the leftovers of the previous update.</summary>
    public static bool ApplyAtStart(Action beforeStart) {
      if (!Channel.UpdatesEnabled(Channel.IsStreamer)) return false;   // the streamer build never installs a staged update
      string exe = Application.ExecutablePath;
      try { if (File.Exists(exe + ".old")) File.Delete(exe + ".old"); } catch (Exception) { }
      var u = Waiting();
      if (u == null) { CleanStaged(); return false; }
      try {
        File.Move(exe, exe + ".old");
        try { File.Copy(StagedExe(u.Version), exe); }
        catch (Exception) { File.Move(exe + ".old", exe); throw; }
        Log.Write("update: " + Program.Version + " -> " + u.Version);
        beforeStart();   // the new process must be able to take the single-instance lock
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        return true;
      } catch (Exception e) { Log.Write("update apply: " + e.Message); return false; }
    }

    static void CleanStaged() {
      try {
        if (!Directory.Exists(Dir)) return;
        foreach (var f in Directory.GetFiles(Dir, Channel.StagedPrefix + "*.*")) {
          string v = VersionOfFile(f);
          if (!Newer(v, Program.Version)) try { File.Delete(f); } catch (Exception) { }
        }
      } catch (Exception) { }
    }
  }
}
