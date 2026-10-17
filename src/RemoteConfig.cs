// Wardsage - the site's switches for the app: GET {site}/api/extractor/app-config (public, no code).
//   200 {"readEnabled":true,"features":{"guild":true,"gvg":true,"battle":true,"autoclick":true,"equip":true,"locate":true,"locateAuto":false,"guideStyle":false,"artAuto":false},"at":"<ISO>"}
// Asked at start and every 10 minutes (the test build every 2). The last good reply is kept in app-config.json in the app's
// data folder and used while the site cannot be reached; with no copy at all everything is on, except the features that start OFF
// (DefaultOff: "locateAuto", the auto clicks of «Показать в игре», "artAuto", the auto clicks of the two artifact flows (storage cleanup, promotion), and "guideStyle", the dimming-and-hand highlight of buttons, until the owner switches them on). A missing or odd field is on,
// and for a feature that starts off it is off (only a plain true switches it on).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace RealmForge {
  public sealed class RemoteSwitches {
    public static readonly string[] Features = { "guild", "gvg", "battle", "autoclick", "equip", "locate", "locateAuto", "guideStyle", "artAuto" };
    /// <summary>Features that are off until the site says true (a missing or odd value, no reply and no saved copy all mean off).</summary>
    public static readonly string[] DefaultOff = { "locateAuto", "guideStyle", "artAuto" };
    public static bool StartsOff(string feature) { return Array.IndexOf(DefaultOff, feature) >= 0; }
    const int MaxBytes = 16 * 1024;

    public bool ReadEnabled = true;
    public readonly Dictionary<string, bool> Feature = new Dictionary<string, bool>();
    public string At;

    public RemoteSwitches() { foreach (var f in Features) Feature[f] = !StartsOff(f); }

    /// <summary>Everything on except the features that start off (no reply and no saved copy).</summary>
    public static RemoteSwitches Permissive() { return new RemoteSwitches(); }

    /// <summary>The feature may run: reading is on and the feature's switch is on. An unknown name is never allowed.</summary>
    public bool Allows(string feature) {
      bool on;
      return ReadEnabled && feature != null && Feature.TryGetValue(feature, out on) && on;
    }

    /// <summary>The features switched off (empty while reading is off as a whole: the notice covers them).</summary>
    public List<string> Off() {
      var r = new List<string>();
      if (!ReadEnabled) return r;
      foreach (var f in Features) if (!Feature[f] && !StartsOff(f)) r.Add(f);   // a feature that starts off is not "switched off"
      return r;
    }

    /// <summary>Same switches (the time of the reply aside).</summary>
    public bool SameAs(RemoteSwitches o) {
      if (o == null || o.ReadEnabled != ReadEnabled) return false;
      foreach (var f in Features) if (o.Feature[f] != Feature[f]) return false;
      return true;
    }

    /// <summary>The switches from the site's reply or the saved copy; null when it is not a JSON object.</summary>
    public static RemoteSwitches Parse(string json) {
      if (string.IsNullOrEmpty(json) || json.Length > MaxBytes) return null;
      var o = MiniJson.AsObject(MiniJson.TryParse(json));
      if (o == null) return null;
      var r = new RemoteSwitches();
      object v;
      if (o.TryGetValue("readEnabled", out v) && v is bool && !(bool)v) r.ReadEnabled = false;
      var fo = o.TryGetValue("features", out v) ? MiniJson.AsObject(v) : null;
      if (fo != null) foreach (var f in Features) {
        bool has = fo.TryGetValue(f, out v);
        if (StartsOff(f)) r.Feature[f] = has && v is bool && (bool)v;
        else if (has && v is bool && !(bool)v) r.Feature[f] = false;
      }
      if (o.TryGetValue("at", out v) && v is string && ((string)v).Length <= 40) r.At = (string)v;
      return r;
    }

    public string ToJson() {
      var sb = new StringBuilder("{\"readEnabled\":").Append(ReadEnabled ? "true" : "false").Append(",\"features\":{");
      for (int i = 0; i < Features.Length; i++) {
        if (i > 0) sb.Append(',');
        sb.Append('"').Append(Features[i]).Append("\":").Append(Feature[Features[i]] ? "true" : "false");
      }
      sb.Append('}');
      if (At != null) sb.Append(",\"at\":").Append(MiniJson.Quote(At));
      return sb.Append('}').ToString();
    }

    /// <summary>The reply when it is a 2xx with a JSON object; else null (the caller keeps what it has).</summary>
    public static RemoteSwitches Interpret(int code, string body) {
      if (code < 200 || code >= 300) return null;
      return Parse(body);
    }

    /// <summary>Minutes between two asks.</summary>
    public static int EveryMinutes(bool testBuild) { return testBuild ? 2 : 10; }

    public static string CachePath { get { return Path.Combine(AppConfig.DefaultDir, "app-config.json"); } }

    /// <summary>The saved copy, or null (none, unreadable).</summary>
    public static RemoteSwitches LoadCache(string path) {
      try { if (File.Exists(path)) return Parse(File.ReadAllText(path, Encoding.UTF8)); } catch (Exception) { }
      return null;
    }

    public void SaveCache(string path) {
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
      } catch (Exception) { }
    }

    /// <summary>The site's switches, or null (no connection, not a 2xx, not JSON).</summary>
    public static RemoteSwitches Fetch(string site) {
      try {
        SyncClient.EnableTls12();
        var req = (HttpWebRequest)WebRequest.Create(site.TrimEnd('/') + "/api/extractor/app-config");
        req.Method = "GET";
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Timeout = 8000;
        req.ReadWriteTimeout = 8000;
        req.AllowAutoRedirect = false;
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream()) {
          var buf = new byte[4096]; int n;
          while ((n = s.Read(buf, 0, buf.Length)) > 0) {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBytes) return null;
          }
          return Interpret((int)resp.StatusCode, Encoding.UTF8.GetString(ms.ToArray()));
        }
      } catch (Exception) { return null; }
    }
  }
}
