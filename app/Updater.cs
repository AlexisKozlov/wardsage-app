// RealmForge.exe — automatic updates.
//
// The site publishes /downloads/latest.json: {version, url, size, sha256, sig, notes:{ru,en}}. The app checks it at
// start and every few hours, downloads a newer RealmForge.exe in the background into %LOCALAPPDATA%\RealmForge\update\
// and puts it in place at the next start (or when the player presses «Перезапустить»): the running exe is renamed to
// *.old (Windows allows that), the new one is copied to its name and started.
//
// Only a file signed with the project's update key is ever installed: RSA-3072, PKCS#1 v1.5 over SHA-256 of the whole
// exe (tools/release-app.mjs signs; the private key never leaves the release PC). Size and SHA-256 are checked too.
// A hacked site or CDN therefore cannot push a different program to the players.
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
    // public half of the update key (base64url), from tools/update-keygen.mjs
    const string KeyN = "6MiNF84wJ_zDGNfzUv5x11fzdDrgLEBJsZUvjuhpFYv90wVNCUbKTU7FkmGcFFz-n0SUx6n1dkVwkc98JB8qzA_2SLTBtX_WqdqFBOzm4dQubRzxkPjOWVP0i2OYZYou2G4txHDNcOi66yZXfVw1-HhtjB7osxEAKR62O2gVBx3_GKUQs_Pq08PPDx-jBvaBqt4Yrr5UoMPypU7OZKQzVv_NQ3aOV9s_8_bn5TWygaez5hgAGipdciZZ91FTOStk_yLF0RanB2CFbGQIhZckGI0HefEy7aw_LlsOkWcFOYG4QRDMgWfW1kNGrqOCETfBwXPM0Krb3jT4mXTO4pL2kbC_MYXo2vwcyi8BPZZfOtNu4DCJ0D0GXpTy4zYk8cvBhTHoY9-_xKMfoI3eMLW5WOUFzBrzNiz-oLfPsGsntbZmo1FgJDUyKI21FIc0wVCjVCZ-GkstjizfzxIeMoQMFEh3tSHn7bPmeE2L5zmDHuGIm2JgTRzfmy4DcPZQV0Zj";
    const string KeyE = "AQAB";
    const long MaxSize = 64L << 20;

    static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RealmForge", "update"); } }
    static string StagedExe(string v) { return Path.Combine(Dir, "RealmForge-" + v + ".exe"); }
    static string StagedMeta(string v) { return Path.Combine(Dir, "RealmForge-" + v + ".json"); }

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

    static byte[] B64Url(string s) {
      s = s.Replace('-', '+').Replace('_', '/');
      while (s.Length % 4 != 0) s += "=";
      return Convert.FromBase64String(s);
    }

    /// <summary>Is <paramref name="file"/> exactly the build the manifest describes, signed with the update key?</summary>
    public static bool Verify(byte[] file, UpdateInfo u) {
      if (file == null || u == null || file.LongLength != u.Size || string.IsNullOrEmpty(u.Sig)) return false;
      using (var sha = SHA256.Create()) {
        string hex = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        if (hex != (u.Sha256 ?? "").ToLowerInvariant()) return false;
      }
      try {
        using (var rsa = new RSACng()) {
          rsa.ImportParameters(new RSAParameters { Modulus = B64Url(KeyN), Exponent = B64Url(KeyE) });
          return rsa.VerifyData(file, Convert.FromBase64String(u.Sig), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
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
    public static UpdateInfo CheckAndDownload(string site) {
      try {
        Uri baseUri; if (!Uri.TryCreate(site, UriKind.Absolute, out baseUri)) return null;
        var u = FromJson(Encoding.UTF8.GetString(Fetch(site.TrimEnd('/') + "/downloads/latest.json", 64 * 1024)));
        if (u == null || !Newer(u.Version, Program.Version)) return null;
        if (Staged(u.Version) != null) return u;
        // the build comes from the same site only
        Uri file; if (!Uri.TryCreate(baseUri, u.Url, out file) || file.Host != baseUri.Host || file.Scheme != baseUri.Scheme) return null;
        var bytes = Fetch(file.AbsoluteUri, MaxSize);
        if (!Verify(bytes, u)) { Log.Write("update " + u.Version + ": signature / hash check FAILED, not installed"); return null; }
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(StagedExe(u.Version), bytes);
        File.WriteAllText(StagedMeta(u.Version), ToJson(u), new UTF8Encoding(false));
        Log.Write("update " + u.Version + ": downloaded and verified");
        return u;
      } catch (Exception e) { Log.Write("update check: " + e.Message); return null; }
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
        foreach (var f in Directory.GetFiles(Dir, "RealmForge-*.json")) {
          string v = Path.GetFileNameWithoutExtension(f).Substring("RealmForge-".Length);
          if (Newer(v, Program.Version) && (best == null || Newer(v, best.Version))) { var u = Staged(v); if (u != null) best = u; }
        }
        return best;
      } catch (Exception) { return null; }
    }

    /// <summary>At start, before anything else: puts a waiting build in place of this exe and starts it. True = this
    /// process must exit now. Also removes the leftovers of the previous update.</summary>
    public static bool ApplyAtStart(Action beforeStart) {
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
        foreach (var f in Directory.GetFiles(Dir, "RealmForge-*.*")) {
          string v = Path.GetFileNameWithoutExtension(f).Substring("RealmForge-".Length);
          if (!Newer(v, Program.Version)) try { File.Delete(f); } catch (Exception) { }
        }
      } catch (Exception) { }
    }
  }
}
