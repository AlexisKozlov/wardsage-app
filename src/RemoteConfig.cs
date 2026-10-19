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
  /// <summary>The site's announcement (site_settings 'announcement'): plain text in both languages, shown as a banner in the app.</summary>
  public sealed class SiteNotice {
    public string Id = "", Ru = "", En = "", Tone = "info", Until, LinkHref, LinkRu, LinkEn;
    const int MaxText = 600;

    static string Str(Dictionary<string, object> o, string k, int max) {
      object v; if (o.TryGetValue(k, out v) && v is string && ((string)v).Length <= max) return (string)v;
      return null;
    }

    /// <summary>From the reply's "announcement" value; null for null, a non-object or no text at all.</summary>
    public static SiteNotice From(object v) {
      var o = MiniJson.AsObject(v);
      if (o == null) return null;
      var n = new SiteNotice { Ru = Str(o, "ru", MaxText) ?? "", En = Str(o, "en", MaxText) ?? "", Id = Str(o, "id", 80) ?? "" };
      if (n.Ru.Trim().Length == 0 && n.En.Trim().Length == 0) return null;
      if (Str(o, "tone", 10) == "warn") n.Tone = "warn";
      n.Until = Str(o, "until", 40);
      var l = o.TryGetValue("link", out v) ? MiniJson.AsObject(v) : null;
      if (l != null) {
        n.LinkHref = Str(l, "href", 300); n.LinkRu = Str(l, "ru", 80); n.LinkEn = Str(l, "en", 80);
        if (string.IsNullOrEmpty(n.LinkHref)) n.LinkHref = n.LinkRu = n.LinkEn = null;
      }
      return n;
    }

    /// <summary>Not past its end time (an unreadable time counts as no end).</summary>
    public bool Live(DateTime nowUtc) {
      DateTime u;
      if (string.IsNullOrEmpty(Until)) return true;
      if (!DateTime.TryParse(Until, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out u)) return true;
      return u > nowUtc;
    }

    public bool SameAs(SiteNotice o) { return o != null && ToJson() == o.ToJson(); }

    public string ToJson() {
      var sb = new StringBuilder("{\"id\":").Append(MiniJson.Quote(Id)).Append(",\"ru\":").Append(MiniJson.Quote(Ru))
        .Append(",\"en\":").Append(MiniJson.Quote(En)).Append(",\"tone\":").Append(MiniJson.Quote(Tone));
      if (Until != null) sb.Append(",\"until\":").Append(MiniJson.Quote(Until));
      if (LinkHref != null) sb.Append(",\"link\":{\"href\":").Append(MiniJson.Quote(LinkHref)).Append(",\"ru\":").Append(MiniJson.Quote(LinkRu ?? ""))
        .Append(",\"en\":").Append(MiniJson.Quote(LinkEn ?? "")).Append('}');
      return sb.Append('}').ToString();
    }
  }

  public sealed class RemoteSwitches {
    public static readonly string[] Features = { "guild", "gvg", "battle", "autoclick", "equip", "locate", "locateAuto", "guideStyle", "artAuto" };
    /// <summary>Features that are off until the site says true (a missing or odd value, no reply and no saved copy all mean off).</summary>
    public static readonly string[] DefaultOff = { "locateAuto", "guideStyle", "artAuto" };
    public static bool StartsOff(string feature) { return Array.IndexOf(DefaultOff, feature) >= 0; }
    const int MaxBytes = 16 * 1024;

    public bool ReadEnabled = true;
    public readonly Dictionary<string, bool> Feature = new Dictionary<string, bool>();
    public string At;
    /// <summary>The site's announcement, kept in the saved copy so it stays after a restart and while the site is down; null when the site sent none.</summary>
    public SiteNotice Notice;

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
      if ((o.Notice == null) != (Notice == null) || (Notice != null && !Notice.SameAs(o.Notice))) return false;
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
      if (o.TryGetValue("announcement", out v)) r.Notice = SiteNotice.From(v);
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
      if (Notice != null) sb.Append(",\"announcement\":").Append(Notice.ToJson());
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
    public static RemoteSwitches Fetch(string site) { bool reached; return Fetch(site, out reached); }

    /// <summary>As above; `reached` is false only when the site gave no answer or a 5xx (a 4xx means it is up).</summary>
    public static RemoteSwitches Fetch(string site, out bool reached) {
      reached = false;
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
          reached = (int)resp.StatusCode < 500;
          var buf = new byte[4096]; int n;
          while ((n = s.Read(buf, 0, buf.Length)) > 0) {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBytes) return null;
          }
          return Interpret((int)resp.StatusCode, Encoding.UTF8.GetString(ms.ToArray()));
        }
      } catch (WebException e) {
        var resp = e.Response as HttpWebResponse;
        if (resp != null) { reached = (int)resp.StatusCode < 500; resp.Close(); }
        return null;
      } catch (Exception) { return null; }
    }
  }
}
