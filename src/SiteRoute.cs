// Wardsage - the automatic switch wardsage.com -> ru.wardsage.com (and back). Used by app/*.cs only (not by the PowerShell extractor).
//
// The hosts are fixed here, never taken from a server reply: {https://wardsage.com, https://ru.wardsage.com}. The automatic choice works only when the
// site of the settings (cfg.Site, never overwritten) is one of the two (that one is the preferred host, the other is the spare); any other address
// (localhost, test.wardsage.com, a custom one) switches it off.
// "A failure" is only "no answer": http 0, timeout, a cut connection, TLS. Any HTTP answer (401, 404, 409, 422, 429, 5xx) means the server is up: it is not
// counted and never switches. The caller reports it: NoteNoAnswer / NoteAnswer (a report for a host that is not the active one is ignored).
//
//   - the preferred host gave no answer twice in 10 minutes (a sliding window, successes do not reset it) and the spare answers a probe
//     (GET {spare}/api/extractor/app-config without a token, 8 s, one at a time, at most once in 5 minutes)  ->  the spare is active for 3 days;
//   - after the 3 days: a trial return to the preferred host; a single failure in the first 10 minutes of the trial (and the spare answers)
//     -> the spare again for 3 days; a working host stays;
//   - insurance: the spare (active) gives no answer twice in 10 minutes -> back to the preferred host at once when it answers the same probe
//     (when it does not, the spare stays: it is the one that works better).
// The state is kept in site-route.json {active, until, strikes, at} (atomic write). No network and no clock of its own: the time and the probe come in.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace RealmForge {
  public sealed class SiteRoute {
    public const string Main = "https://wardsage.com";
    public const string Ru = "https://ru.wardsage.com";
    public const int WindowMinutes = 10, TtlDays = 3, ProbeGapMinutes = 5, TrialMinutes = 10, Strikes = 2, ProbeTimeoutMs = 8000;
    public static readonly string[] DefaultGroup = { Main, Ru };

    readonly object gate = new object();
    readonly string[] group;
    readonly Func<DateTime> now;
    readonly Func<string, bool> probe;
    readonly string path;
    readonly Action<string> log;
    /// <summary>How the probe is run (default: a task). A test passes a synchronous runner.</summary>
    public Action<Action> Run = a => System.Threading.Tasks.Task.Factory.StartNew(a);

    string pref, alt;                 // the preferred host (from cfg.Site) and the spare; null while the automatic choice is off
    string active;                    // pref or alt
    DateTime? until;                  // active == alt: the end of the 3 days; active == pref: the end of the trial (null: no trial)
    int strikes;                      // how many times in a row the spare was chosen
    DateTime? at;                     // the last change
    readonly List<DateTime> fails = new List<DateTime>();
    DateTime lastProbe = DateTime.MinValue;
    bool probing;
    volatile int pending;             // probes started and not finished (WaitIdle)

    /// <param name="group">the two hosts (null: <see cref="DefaultGroup"/>)</param>
    /// <param name="now">the UTC time</param>
    /// <param name="probe">does this host answer (any HTTP answer)? (null: <see cref="ProbeHttp"/>)</param>
    /// <param name="path">site-route.json (null: nothing is saved)</param>
    public SiteRoute(string[] group, Func<DateTime> now, Func<string, bool> probe, string path, Action<string> log) {
      this.group = group ?? DefaultGroup; this.now = now ?? (() => DateTime.UtcNow); this.probe = probe ?? ProbeHttp;
      this.path = path; this.log = log ?? (s => { });
    }

    public static string StatePath { get { return Path.Combine(AppConfig.DefaultDir, "site-route.json"); } }

    // --- what the callers ask ---

    /// <summary>The host to use now for the settings' site <paramref name="cfgSite"/>: cfgSite itself, or the spare while it is the active one.
    /// <paramref name="enabled"/> false (RemoteSwitches.SiteFallback) or a site outside the group: cfgSite.</summary>
    public string Resolve(string cfgSite, bool enabled) {
      lock (gate) {
        if (!Select(cfgSite, enabled)) return cfgSite;
        Expire();
        return active == alt ? alt : cfgSite;
      }
    }

    /// <summary>The host the settings' site <paramref name="cfgSite"/> is switched to right now (null: not switched).</summary>
    public bool Switched(string cfgSite, bool enabled) {
      lock (gate) { return Select(cfgSite, enabled) && active == alt; }
    }

    /// <summary>The page may be opened from the app: a host of the group (the Open-URL filter).</summary>
    public bool IsGroupHost(string host) {
      if (host == null) return false;
      foreach (var g in group) { Uri u; if (Uri.TryCreate(g, UriKind.Absolute, out u) && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase)) return true; }
      return false;
    }

    /// <summary>Forget the state (the user changed the site address).</summary>
    public void Reset() {
      lock (gate) {
        pref = alt = null; active = null; until = null; strikes = 0; at = null; fails.Clear();
        if (path != null) try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
      }
    }

    /// <summary>A request to <paramref name="usedSite"/> got no answer (http 0, timeout, cut connection, TLS). Ignored for a host that is not the active one.</summary>
    public void NoteNoAnswer(string usedSite) {
      string target; string from;
      lock (gate) {
        if (pref == null || usedSite == null) return;
        Expire();
        from = active;
        if (!SameHost(usedSite, from)) return;
        DateTime t = now();
        fails.Add(t); Prune(t);
        bool trial = from == pref && until.HasValue;
        int need = trial ? 1 : Strikes;
        if (fails.Count < need || probing) return;
        if (t - lastProbe < TimeSpan.FromMinutes(ProbeGapMinutes)) return;
        probing = true; lastProbe = t; pending++;
        target = from == pref ? alt : pref;
      }
      string tgt = target, frm = from;
      Run(() => {
        bool ok; try { ok = probe(tgt); } catch (Exception) { ok = false; }
        Finish(frm, tgt, ok);
      });
    }

    /// <summary>A request to <paramref name="usedSite"/> got an HTTP answer (any status). The window of failures is not reset by it.</summary>
    public void NoteAnswer(string usedSite) { }

    /// <summary>Waits for the running probe (for the tests).</summary>
    public bool WaitIdle(int ms) {
      DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
      while (pending > 0 && DateTime.UtcNow < end) System.Threading.Thread.Sleep(5);
      return pending == 0;
    }

    // --- inside ---

    void Finish(string from, string target, bool ok) {
      lock (gate) {
        try {
          probing = false;
          if (pref == null || active != from) return;
          DateTime t = now();
          if (!ok) { log("site route: " + target + " does not answer either, staying on " + from); return; }
          if (from == pref) {
            active = alt; until = t.AddDays(TtlDays); strikes++; at = t; fails.Clear(); Save();
            log("site route: " + pref + " gives no answer, switched to " + alt + " for " + TtlDays + " days");
          } else {
            active = pref; until = null; strikes = 0; at = t; fails.Clear(); Save();
            log("site route: " + alt + " gives no answer, back to " + pref);
          }
        } finally { pending--; }
      }
    }

    static bool SameHost(string a, string b) {
      if (a == null || b == null) return false;
      return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    void Prune(DateTime t) {
      DateTime from = t.AddMinutes(-WindowMinutes);
      fails.RemoveAll(x => x < from);
    }

    string Norm(string site) {
      string err; string n = SyncClient.NormalizeSite(site, out err);
      return n;
    }

    /// <summary>The group host the settings' site stands for (the normalized one), or null.</summary>
    string InGroup(string site) {
      string n = Norm(site);
      if (n == null) return null;
      foreach (var g in group) if (SameHost(g, n)) return g;
      return null;
    }

    // (re)reads the state when the preferred host changed; false while the automatic choice is off for this site
    bool Select(string cfgSite, bool enabled) {
      string p = enabled ? InGroup(cfgSite) : null;
      if (p == null) return false;
      if (p != pref) {
        pref = p; alt = SameHost(group[0], p) ? group[1] : group[0];
        active = pref; until = null; strikes = 0; at = null; fails.Clear();
        Load();
      }
      return true;
    }

    // the trial return after the 3 days, the end of a trial
    void Expire() {
      DateTime t = now();
      if (active == alt && until.HasValue && until.Value <= t) {
        active = pref; until = t.AddMinutes(TrialMinutes); at = t; fails.Clear(); Save();
        log("site route: the " + TtlDays + " days are over, trying " + pref + " again");
      } else if (active == pref && until.HasValue && until.Value <= t) {
        until = null; strikes = 0; Save();
      }
    }

    static string Iso(DateTime? t) { return t.HasValue ? t.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) : null; }
    static DateTime? ParseIso(string s) {
      DateTime d;
      if (!string.IsNullOrEmpty(s) && s.Length <= 40 && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out d)) return d;
      return null;
    }

    void Load() {
      if (path == null) return;
      try {
        if (!File.Exists(path)) return;
        var o = MiniJson.AsObject(MiniJson.TryParse(File.ReadAllText(path, Encoding.UTF8)));
        if (o == null) return;
        string a = MiniJson.GetString(o, "active");
        string h = null; foreach (var g in new[] { pref, alt }) if (SameHost(g, a)) h = g;   // a host that is not of the group is not taken
        if (h == null) return;
        active = h; until = ParseIso(MiniJson.GetString(o, "until")); at = ParseIso(MiniJson.GetString(o, "at"));
        int s = MiniJson.GetCount(o, "strikes"); strikes = s < 0 ? 0 : s;
        if (active == alt && !until.HasValue) until = now().AddDays(TtlDays);   // a state without an end: the full time from now
      } catch (Exception) { }
    }

    void Save() {
      if (path == null) return;
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var sb = new StringBuilder("{\"active\":").Append(MiniJson.Quote(active ?? ""));
        if (until.HasValue) sb.Append(",\"until\":").Append(MiniJson.Quote(Iso(until)));
        sb.Append(",\"strikes\":").Append(strikes);
        if (at.HasValue) sb.Append(",\"at\":").Append(MiniJson.Quote(Iso(at)));
        sb.Append('}');
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
      } catch (Exception) { }
    }

    /// <summary>The real probe: GET {host}/api/extractor/app-config, no token, 8 s. True for any HTTP answer. Does not touch NetBackoff.</summary>
    public static bool ProbeHttp(string host) {
      try {
        SyncClient.EnableTls12();
        var req = (HttpWebRequest)WebRequest.Create(host.TrimEnd('/') + "/api/extractor/app-config");
        req.Method = "GET"; req.Accept = "application/json"; req.UserAgent = SyncClient.UserAgent;
        req.Timeout = ProbeTimeoutMs; req.ReadWriteTimeout = ProbeTimeoutMs; req.AllowAutoRedirect = false;
        using (var resp = (HttpWebResponse)req.GetResponse()) return (int)resp.StatusCode > 0;
      } catch (WebException e) {
        var resp = e.Response as HttpWebResponse;
        if (resp != null) { int c = (int)resp.StatusCode; resp.Close(); return c > 0; }
        return false;
      } catch (Exception) { return false; }
    }
  }
}
