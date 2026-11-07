// Wardsage - a small backoff for the app's periodic calls to the site (no threads, no timers of its own): after a failure
// (no answer, 429, 409, any 5xx) the next call waits 5 s, 10 s, 20 s ... up to 5 min, +-40% jitter; a Retry-After the site
// sent wins over that (clamped to 2 s .. 20 s while the site is only busy, 1 s .. 1 h in maintenance); one good answer resets it. Callers ask Blocked() before a call and
// tell Note() the outcome (http 0 = the site was not reached). While the site reopens after maintenance (Reopening, set by the app) a Retry-After
// gets a spread of 0..+50% so that the players do not all come back in the same second; for ordinary errors nothing changes.
using System;

namespace RealmForge {
  public sealed class NetBackoff {
    public const int MinMs = 5000, MaxMs = 5 * 60 * 1000, MaxRetryAfterSec = 3600;
    /// <summary>Outside maintenance a Retry-After is clamped to MinRetryAfterSec .. BusyRetryAfterSec (a busy site is asked again soon, never in an hour); the maintenance gate keeps its own logic.</summary>
    public const int MinRetryAfterSec = 2, BusyRetryAfterSec = 20;
    public const double Jitter = 0.4;
    static readonly Random Rng = new Random();

    /// <summary>The site is reopening after maintenance: a Retry-After is spread (set by the app from the maintenance state; any thread).</summary>
    public static volatile bool Reopening;
    public const double ReopenSpread = 0.5;

    int failures;
    DateTime until = DateTime.MinValue;

    // what the last call on this thread saw (set by PlansClient.Send / Layout / Geometry / RemoteConfig, read right after it)
    [ThreadStatic] static int lastHttp;
    [ThreadStatic] static int lastRetryAfter;
    [ThreadStatic] static bool lastMaint;
    public static void Reset() { lastHttp = 0; lastRetryAfter = -1; lastMaint = false; }
    public static void Record(int http, string retryAfterHeader) { lastHttp = http; lastRetryAfter = ParseRetryAfter(retryAfterHeader, DateTime.UtcNow); }
    /// <summary>As Record, plus the X-Wardsage-Maintenance header value of the reply ("1" = the site is in maintenance).</summary>
    public static void Record(int http, string retryAfterHeader, string maintenanceHeader) {
      Record(http, retryAfterHeader);
      lastMaint = maintenanceHeader != null && maintenanceHeader.Trim() == "1";
    }
    /// <summary>The last reply on this thread carried X-Wardsage-Maintenance: 1.</summary>
    public static bool LastMaintenance { get { return lastMaint; } }
    public static int LastHttp { get { return lastHttp; } }
    public static int LastRetryAfterSec { get { return lastRetryAfter; } }

    /// <summary>A reply worth backing off for: no answer at all (0), 429, a 5xx, or 409 (a refusal: asking again at once changes nothing). The one exception is
    /// /api/sync's 409 account_forgotten: that is SyncClient's own answer (SyncStatus.Forgotten, SyncGate), it never goes through this class.</summary>
    public static bool IsFailure(int http) { return http == 0 || http == 429 || http == 409 || http >= 500; }

    /// <summary>Retry-After as seconds (a number, or an HTTP date relative to <paramref name="now"/>); -1 = absent or unreadable.</summary>
    public static int ParseRetryAfter(string h, DateTime now) {
      if (string.IsNullOrEmpty(h)) return -1;
      h = h.Trim();
      int sec;
      if (int.TryParse(h, out sec)) return sec >= 0 ? sec : -1;
      DateTime d;
      if (DateTime.TryParse(h, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out d)) {
        double s = (d - now).TotalSeconds;
        return s <= 0 ? 0 : (int)Math.Min(Math.Ceiling(s), int.MaxValue / 2);
      }
      return -1;
    }

    /// <summary>The wait after the <paramref name="failureNo"/>-th failure in a row (1 = first): Retry-After (seconds, &gt;= 0) when given,
    /// else 5 s * 2^(n-1) capped at 5 min, scaled by 1 +- 20% with <paramref name="rnd01"/> in [0,1).</summary>
    public static int DelayMs(int failureNo, int retryAfterSec, double rnd01) { return DelayMs(failureNo, retryAfterSec, rnd01, Reopening); }

    /// <summary>As above; <paramref name="spread"/> (the site is reopening) adds 0..+50% to a Retry-After (never less than the site asked).</summary>
    public static int DelayMs(int failureNo, int retryAfterSec, double rnd01, bool spread) { return DelayMs(failureNo, retryAfterSec, rnd01, spread, true); }

    /// <summary>As above; <paramref name="maintenance"/> false (the site is only busy): a Retry-After is clamped to 2 s .. 20 s and spread 0..+30%, still at most 20 s.</summary>
    public static int DelayMs(int failureNo, int retryAfterSec, double rnd01, bool spread, bool maintenance) {
      if (retryAfterSec >= 0 && !maintenance) {
        if (rnd01 < 0) rnd01 = 0; if (rnd01 > 1) rnd01 = 1;
        double b = Math.Max(MinRetryAfterSec, Math.Min(retryAfterSec, BusyRetryAfterSec)) * 1000.0 * (1 + 0.3 * rnd01);
        return (int)Math.Min(b, BusyRetryAfterSec * 1000.0);
      }
      if (retryAfterSec >= 0) {
        double ra = Math.Max(1, Math.Min(retryAfterSec, MaxRetryAfterSec)) * 1000.0;
        if (spread) { if (rnd01 < 0) rnd01 = 0; if (rnd01 > 1) rnd01 = 1; ra *= 1 + ReopenSpread * rnd01; }
        return (int)ra;
      }
      if (failureNo < 1) failureNo = 1;
      double ms = MinMs;
      for (int i = 1; i < failureNo && ms < MaxMs; i++) ms *= 2;
      if (ms > MaxMs) ms = MaxMs;
      if (rnd01 < 0) rnd01 = 0; if (rnd01 > 1) rnd01 = 1;
      return (int)(ms * (1 - Jitter + 2 * Jitter * rnd01));
    }

    /// <summary>The periodic polls (sell, forum-unread, guild, arena) while the game is not running go out at most this often.</summary>
    public const int IdleMinutes = 5;

    /// <summary>The pause before a repeated request that no NetBackoff runs (the long poll, the repeated «done» report): <paramref name="baseMs"/> scaled
    /// by 1 +- 40% with <paramref name="rnd01"/>, never under 1.5 s and never over <paramref name="maxMs"/>.</summary>
    public static int Spread(int baseMs, int maxMs, double rnd01) {
      if (rnd01 < 0) rnd01 = 0; if (rnd01 > 1) rnd01 = 1;
      int ms = (int)(baseMs * (1 - Jitter + 2 * Jitter * rnd01));
      return Math.Max(1500, Math.Min(ms, maxMs));
    }
    public static int Spread(int baseMs, int maxMs) { lock (Rng) return Spread(baseMs, maxMs, Rng.NextDouble()); }
    /// <summary>True when a poll made at <paramref name="last"/> is still fresh enough while the game is not running.</summary>
    public static bool IdleThrottled(bool gameRunning, DateTime last, DateTime now) { return !gameRunning && last != DateTime.MinValue && (now - last).TotalMinutes < IdleMinutes; }

    public int Failures { get { return failures; } }
    public bool Blocked(DateTime now) { lock (this) return now < until; }
    public DateTime Until { get { lock (this) return until; } }

    /// <summary>Records one outcome. Returns true when it was a failure (the caller may log it).</summary>
    public bool Note(int http, int retryAfterSec, DateTime now) { return Note(http, retryAfterSec, now, Rng.NextDouble()); }
    public bool Note(int http, int retryAfterSec, DateTime now, double rnd01) { return Note(http, retryAfterSec, now, rnd01, true); }
    /// <param name="maintenance">the reply was a maintenance one (a long Retry-After is honoured); false = the site is only busy.</param>
    public bool Note(int http, int retryAfterSec, DateTime now, double rnd01, bool maintenance) {
      lock (this) {
        if (!IsFailure(http)) { failures = 0; until = DateTime.MinValue; return false; }
        failures++;
        until = now.AddMilliseconds(DelayMs(failures, retryAfterSec, rnd01, Reopening, maintenance));
        return true;
      }
    }

    /// <summary>Records the outcome of the call just made on this thread (PlansClient.Send and friends set it).</summary>
    public bool NoteLast(DateTime now) { return Note(lastHttp, lastRetryAfter, now, Rng.NextDouble(), lastMaint); }
  }
}
