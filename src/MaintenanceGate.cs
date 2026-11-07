// Wardsage - the maintenance window logic of the app (pure functions: the time and the install's seed come from outside, no I/O).
// The site announces works in app-config "maintenance" (src/RemoteConfig.cs Maintenance) and answers 503 with the header
// X-Wardsage-Maintenance: 1 and {"error":"maintenance","maintenance":true,"until":"<ISO UTC>"} while they run.
//   quiet from startsAt - preStopMin until the end of the works plus a per-install spread, so the players do not all come back in one second:
//     ResumeAt(seed) = endsAt + rand(0 .. reopenMin) minutes, the random part is a stable hash of the install's seed;
//   in the "reopening" state only reopenPct percent of the installs (hash(seed) % 100 < pct) wake at their moment, the others wait the full reopenMin.
// The network calls of the app (sync, plans, long poll, update check, news, forum, packs, guild, arena, traces) ask Active() first.
using System;

namespace RealmForge {
  public static class MaintenanceGate {
    /// <summary>The error reports about the network stay silent this long after the works end.</summary>
    public const int AfterSilenceMin = 15;
    /// <summary>A flag from a 503 without a usable end time keeps the app quiet this long at least (then it asks again).</summary>
    public const int FlagDefaultSec = 300, FlagMaxHours = 6;

    /// <summary>FNV-1a over the characters (stable across runs and machines).</summary>
    public static uint Hash(string s) {
      uint h = 0x811C9DC5u;
      if (s != null) foreach (char c in s) { h ^= (uint)c; h *= 0x01000193u; }
      return h;
    }

    /// <summary>The seed of an install from a stable text (the same text gives the same seed).</summary>
    public static int SeedOf(string text) { return (int)(Hash("wardsage|" + text) & 0x7fffffff); }

    static uint Mix(int seed, uint salt) {
      uint h = (uint)seed * 0x9E3779B1u + salt;
      h ^= h >> 15; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
      return h;
    }

    /// <summary>This install is among the <paramref name="pct"/> percent that may come back now (the same answer for the same seed).</summary>
    public static bool InRollout(int seed, int pct) {
      if (pct >= 100) return true;
      if (pct <= 0) return false;
      return Mix(seed, 1u) % 100 < (uint)pct;
    }

    /// <summary>The moment the works end for this install: endsAt plus a stable part of reopenMin minutes by the seed.</summary>
    public static DateTime ResumeAt(DateTime endsAt, int reopenMin, int seed) {
      if (endsAt == DateTime.MinValue) return DateTime.MinValue;
      if (reopenMin < 0) reopenMin = 0;
      double frac = (Mix(seed, 2u) % 10000) / 10000.0;
      return endsAt.AddSeconds(Math.Floor(reopenMin * 60 * frac));
    }

    public static DateTime ResumeAt(Maintenance m, int seed) { return m == null ? DateTime.MinValue : ResumeAt(m.EndsAt, m.ReopenMin, seed); }

    /// <summary>Where the reading stops: startsAt - preStopMin; MinValue when no start is known.</summary>
    public static DateTime StopAt(Maintenance m) { return m == null || !m.HasStart ? DateTime.MinValue : m.StartsAt.AddMinutes(-m.PreStopMin); }

    /// <summary>The moment this install wakes, by the state: in "reopening" an install outside the rollout waits the whole reopenMin.</summary>
    public static DateTime WakeAt(Maintenance m, int seed) {
      if (m == null || !m.HasEnd) return DateTime.MinValue;
      if (m.State == "reopening" && !InRollout(seed, m.ReopenPct)) return m.EndsAt.AddMinutes(m.ReopenMin);
      return ResumeAt(m, seed);
    }

    /// <summary>The app must not call the site now (pure).</summary>
    public static bool Active(Maintenance m, DateTime now, int seed) {
      if (m == null || m.State == "off") return false;
      DateTime wake = WakeAt(m, seed);
      if (m.State == "reopening") return wake != DateTime.MinValue && now < wake;
      if (m.State == "scheduled") {
        if (!m.HasStart || now < StopAt(m)) return false;
        return wake == DateTime.MinValue || now < wake;
      }
      return wake == DateTime.MinValue || now < wake;   // "on"
    }

    /// <summary>For the screen: "off", "soon" (scheduled, the reading has not stopped yet), "stopped" (reading stopped, works not begun), "on" (the works).</summary>
    public static string Phase(Maintenance m, DateTime now, int seed) {
      if (m == null || m.State == "off") return "off";
      if (m.State == "scheduled" && m.HasStart && now < StopAt(m)) return now < m.StartsAt ? "soon" : "off";
      if (!Active(m, now, seed)) return "off";
      if (m.State == "scheduled" && m.HasStart && now < m.StartsAt) return "stopped";
      return "on";
    }

    /// <summary>While the mode is live the app asks app-config (and nothing else) every 120..180 s: the pause never goes above 180 s and never below 90 s.</summary>
    public const int ProbeLoSec = 120, ProbeHiSec = 180, ProbeMinSec = 90, ResumeJitterMaxSec = 20;

    /// <summary>The works mode is live for the screen or the flag: any phase but "off" (soon, stopped, on, reopening), or a 503 flag still in force.</summary>
    public static bool Live(Maintenance m, DateTime now, int seed, DateTime flagUntil) { return Phase(m, now, seed) != "off" || flagUntil > now; }

    /// <summary>The pause before the next app-config probe while the mode is live: 120..180 s by <paramref name="rnd"/> (0..1), at most 180 s, at least 90 s,
    /// and not past the wake time (<paramref name="wake"/>, MinValue = unknown) when that comes sooner than the floor allows.</summary>
    public static int ProbeMs(double rnd, DateTime now, DateTime wake) {
      if (rnd < 0 || double.IsNaN(rnd)) rnd = 0; else if (rnd > 1) rnd = 1;
      double sec = ProbeLoSec + (ProbeHiSec - ProbeLoSec) * rnd;
      if (wake != DateTime.MinValue && wake > now) sec = Math.Min(sec, (wake - now).TotalSeconds + 2);
      sec = Math.Max(ProbeMinSec, Math.Min(ProbeHiSec, sec));
      return (int)(sec * 1000);
    }

    /// <summary>A valid app-config answer without works (state "off" or no field) ends the mode at once: the flag from an earlier 503 is dropped, the planned wake time is not used.</summary>
    public static DateTime FlagAfterAnswer(DateTime flagUntil, Maintenance answered) { return answered == null || answered.State == "off" ? DateTime.MinValue : flagUntil; }

    /// <summary>The light spread (0..20 s) before the other calls resume after an early end.</summary>
    public static int ResumeJitterMs(double rnd) { if (rnd < 0 || double.IsNaN(rnd)) rnd = 0; else if (rnd > 1) rnd = 1; return (int)(rnd * ResumeJitterMaxSec * 1000); }

    /// <summary>Not a maintenance 503 when it has no usable flag; the header value "1" is the flag.</summary>
    public static bool IsHeader(string v) { return v != null && v.Trim() == "1"; }

    /// <summary>A maintenance 503 body: {"maintenance":true} or {"error":"maintenance"}; <paramref name="until"/> is the end time or MinValue.</summary>
    public static bool ParseBody(string body, out DateTime until) {
      until = DateTime.MinValue;
      if (string.IsNullOrEmpty(body) || body.Length > 4096) return false;
      var o = MiniJson.AsObject(MiniJson.TryParse(body));
      if (o == null) return false;
      object v;
      bool flag = o.TryGetValue("maintenance", out v) && v is bool && (bool)v || MiniJson.GetString(o, "error") == "maintenance";
      if (!flag) return false;
      string u = MiniJson.GetString(o, "until"); DateTime t;
      if (u != null && u.Length <= 40 && DateTime.TryParse(u, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out t)) until = t;
      return true;
    }

    /// <summary>Quiet until when a call saw a maintenance flag (header, body or both): the end time when given and in the future, else Retry-After
    /// seconds (-1 = none), else 5 minutes; never more than 6 hours ahead; the spread of reopenMin minutes by the seed is added to a known end.</summary>
    public static DateTime FlagResume(DateTime until, int retryAfterSec, int reopenMin, int seed, DateTime now) {
      DateTime r;
      if (until != DateTime.MinValue && until > now) r = ResumeAt(until, reopenMin, seed);
      else if (retryAfterSec > 0) r = now.AddSeconds(Math.Min(retryAfterSec, NetBackoff.MaxRetryAfterSec));
      else r = now.AddSeconds(FlagDefaultSec);
      DateTime cap = now.AddHours(FlagMaxHours);
      return r > cap ? cap : r;
    }

    /// <summary>The kinds of error report that are only about the network: they stay quiet during the works.</summary>
    public static bool SilencedKind(string kind) { return kind == "sync.net" || kind == "sync.server" || kind == "net"; }

    /// <summary>The report of this (normalized) kind must not go now: it is a network kind and <paramref name="now"/> is before <paramref name="silentUntil"/>
    /// (the end of the works plus 15 minutes, kept by the caller).</summary>
    public static bool ReportSilenced(string kind, DateTime now, DateTime silentUntil) { return SilencedKind(kind) && now < silentUntil; }

    /// <summary>The silent-until time for a window that wakes at <paramref name="wake"/> (MinValue = unknown end: now).</summary>
    public static DateTime SilentUntil(DateTime wake, DateTime now) { return (wake == DateTime.MinValue || wake < now ? now : wake).AddMinutes(AfterSilenceMin); }
  }
}
