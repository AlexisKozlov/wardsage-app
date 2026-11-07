// Wardsage - the site's switches for the app: GET {site}/api/extractor/app-config (public, no code).
//   200 {"readEnabled":true,"features":{"guild":true,"gvg":true,"battle":true,"equip":true,"locate":true,"guideStyle":false},"at":"<ISO>"}
// Asked at start and every 10 minutes (the test build every 2). The last good reply is kept in app-config.json in the app's
// data folder and used while the site cannot be reached; with no copy at all everything is on, except the features that start OFF
// (DefaultOff: "guideStyle", the dimming-and-hand highlight of buttons, until the owner switches them on). A missing or odd field is on,
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

  /// <summary>The site's maintenance mode (app-config "maintenance"; always present, {state:"off"} when there is none). Unknown fields are ignored.
  ///   {"state":"off|scheduled|on|reopening","startsAt":"<ISO UTC>","endsAt":"<ISO UTC>","mode":"readonly|full","ru":"..","en":"..","link":"<url>" or {"href":".."},
  ///    "preStopMin":15,"reopenMin":20,"reopenPct":100}
  /// The window logic (when the app is quiet, when it wakes) is in src/MaintenanceGate.cs.</summary>
  public sealed class Maintenance {
    public string State = "off", Mode = "readonly", Ru = "", En = "", Link;
    public DateTime StartsAt = DateTime.MinValue, EndsAt = DateTime.MinValue;   // UTC; MinValue = not given
    public int PreStopMin = 15, ReopenMin = 20, ReopenPct = 100;
    const int MaxText = 600;

    public bool HasStart { get { return StartsAt != DateTime.MinValue; } }
    public bool HasEnd { get { return EndsAt != DateTime.MinValue; } }

    static DateTime Time(Dictionary<string, object> o, string k) {
      object v; DateTime t;
      if (o.TryGetValue(k, out v) && v is string && ((string)v).Length <= 40
          && DateTime.TryParse((string)v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out t)) return t;
      return DateTime.MinValue;
    }

    static int Num(Dictionary<string, object> o, string k, int def, int min, int max) {
      object v;
      if (o.TryGetValue(k, out v) && v is double) { double d = (double)v; if (d >= min && d <= max) return (int)d; }
      return def;
    }

    static string Str(Dictionary<string, object> o, string k, int max) {
      object v; if (o.TryGetValue(k, out v) && v is string && ((string)v).Length <= max) return (string)v;
      return "";
    }

    /// <summary>From the reply's "maintenance" value; an absent, odd or unknown-state value is off.</summary>
    public static Maintenance From(object v) {
      var m = new Maintenance();
      var o = MiniJson.AsObject(v);
      if (o == null) return m;
      string st = Str(o, "state", 12);
      if (st != "scheduled" && st != "on" && st != "reopening") return m;
      m.State = st;
      m.StartsAt = Time(o, "startsAt"); m.EndsAt = Time(o, "endsAt");
      m.Mode = Str(o, "mode", 10) == "full" ? "full" : "readonly";
      m.Ru = Str(o, "ru", MaxText); m.En = Str(o, "en", MaxText);
      object lv;
      if (o.TryGetValue("link", out lv)) {
        var lo = MiniJson.AsObject(lv);
        string href = lv is string ? (string)lv : lo != null ? Str(lo, "href", 300) : "";
        if (href.Length > 0 && href.Length <= 300) m.Link = href;
      }
      m.PreStopMin = Num(o, "preStopMin", 15, 0, 240);
      m.ReopenMin = Num(o, "reopenMin", 20, 0, 240);
      m.ReopenPct = Num(o, "reopenPct", 100, 0, 100);
      return m;
    }

    public bool SameAs(Maintenance o) { return o != null && ToJson() == o.ToJson(); }

    static string Iso(DateTime t) { return t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture); }

    public string ToJson() {
      var sb = new StringBuilder("{\"state\":").Append(MiniJson.Quote(State));
      if (State == "off") return sb.Append('}').ToString();
      if (HasStart) sb.Append(",\"startsAt\":").Append(MiniJson.Quote(Iso(StartsAt)));
      if (HasEnd) sb.Append(",\"endsAt\":").Append(MiniJson.Quote(Iso(EndsAt)));
      sb.Append(",\"mode\":").Append(MiniJson.Quote(Mode));
      if (Ru.Length > 0) sb.Append(",\"ru\":").Append(MiniJson.Quote(Ru));
      if (En.Length > 0) sb.Append(",\"en\":").Append(MiniJson.Quote(En));
      if (Link != null) sb.Append(",\"link\":").Append(MiniJson.Quote(Link));
      sb.Append(",\"preStopMin\":").Append(PreStopMin).Append(",\"reopenMin\":").Append(ReopenMin).Append(",\"reopenPct\":").Append(ReopenPct);
      return sb.Append('}').ToString();
    }
  }

  public sealed class RemoteSwitches {
    public static readonly string[] Features = { "guild", "gvg", "battle", "equip", "locate", "guideStyle" };
    /// <summary>Features that are off until the site says true (a missing or odd value, no reply and no saved copy all mean off).</summary>
    public static readonly string[] DefaultOff = { "guideStyle" };
    public static bool StartsOff(string feature) { return Array.IndexOf(DefaultOff, feature) >= 0; }
    const int MaxBytes = 16 * 1024;

    public bool ReadEnabled = true;
    /// <summary>The automatic switch to the spare host (src/SiteRoute.cs): on unless the site says features.siteFallback === false. Not in <see cref="Features"/> (it is no feature of the UI).</summary>
    public bool SiteFallback = true;
    public readonly Dictionary<string, bool> Feature = new Dictionary<string, bool>();
    public string At;
    /// <summary>The site's announcement, kept in the saved copy so it stays after a restart and while the site is down; null when the site sent none.</summary>
    public SiteNotice Notice;
    /// <summary>The maintenance mode; never null (state "off" when the site says nothing). Not in <see cref="Features"/>.</summary>
    public Maintenance Maint = new Maintenance();

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
      if (o == null || o.ReadEnabled != ReadEnabled || o.SiteFallback != SiteFallback) return false;
      if ((o.Notice == null) != (Notice == null) || (Notice != null && !Notice.SameAs(o.Notice))) return false;
      if (!Maint.SameAs(o.Maint)) return false;
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
      if (fo != null && fo.TryGetValue("siteFallback", out v) && v is bool && !(bool)v) r.SiteFallback = false;
      if (fo != null) foreach (var f in Features) {
        bool has = fo.TryGetValue(f, out v);
        if (StartsOff(f)) r.Feature[f] = has && v is bool && (bool)v;
        else if (has && v is bool && !(bool)v) r.Feature[f] = false;
      }
      if (o.TryGetValue("announcement", out v)) r.Notice = SiteNotice.From(v);
      if (o.TryGetValue("maintenance", out v)) r.Maint = Maintenance.From(v);
      if (o.TryGetValue("at", out v) && v is string && ((string)v).Length <= 40) r.At = (string)v;
      return r;
    }

    public string ToJson() {
      var sb = new StringBuilder("{\"readEnabled\":").Append(ReadEnabled ? "true" : "false").Append(",\"features\":{");
      for (int i = 0; i < Features.Length; i++) {
        if (i > 0) sb.Append(',');
        sb.Append('"').Append(Features[i]).Append("\":").Append(Feature[Features[i]] ? "true" : "false");
      }
      if (!SiteFallback) sb.Append(",\"siteFallback\":false");
      sb.Append('}');
      if (Notice != null) sb.Append(",\"announcement\":").Append(Notice.ToJson());
      sb.Append(",\"maintenance\":").Append(Maint.ToJson());
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

    /// <summary>The last Fetch on this thread got no HTTP answer at all (no connection, timeout, cut connection, TLS). A 5xx is an answer: `reached` is false for it, this is not. For src/SiteRoute.cs.</summary>
    [ThreadStatic] public static bool LastNoAnswer;

    /// <summary>The site's switches, or null (no connection, not a 2xx, not JSON).</summary>
    public static RemoteSwitches Fetch(string site) { bool reached; return Fetch(site, out reached); }

    /// <summary>As above; `reached` is false only when the site gave no answer or a 5xx (a 4xx means it is up).</summary>
    public static RemoteSwitches Fetch(string site, out bool reached) {
      reached = false; LastNoAnswer = false;
      try {
        SyncClient.EnableTls12();
        var req = (HttpWebRequest)WebRequest.Create(site.TrimEnd('/') + "/api/extractor/app-config");
        req.Method = "GET";
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Timeout = 8000;
        req.ReadWriteTimeout = 8000;
        req.AllowAutoRedirect = false;
        NetBackoff.Reset();
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream()) {
          NetBackoff.Record((int)resp.StatusCode, resp.Headers["Retry-After"]);
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
        if (resp != null) { reached = (int)resp.StatusCode < 500; NetBackoff.Record((int)resp.StatusCode, resp.Headers["Retry-After"]); resp.Close(); }
        else LastNoAnswer = true;
        return null;
      } catch (Exception) { LastNoAnswer = true; return null; }
    }
  }
}
