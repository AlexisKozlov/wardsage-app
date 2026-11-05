// Wardsage - a small backoff for the app's periodic calls to the site (no threads, no timers of its own): after a failure
// (no answer, 429, 503, any 5xx) the next call waits 5 s, 10 s, 20 s ... up to 5 min, +-20% jitter; a Retry-After the site
// sent wins over that (clamped to 1 s .. 1 h, no jitter); one good answer resets it. Callers ask Blocked() before a call and
// tell Note() the outcome (http 0 = the site was not reached).
using System;

namespace RealmForge {
  public sealed class NetBackoff {
    public const int MinMs = 5000, MaxMs = 5 * 60 * 1000, MaxRetryAfterSec = 3600;
    public const double Jitter = 0.2;
    static readonly Random Rng = new Random();

    int failures;
    DateTime until = DateTime.MinValue;

    // what the last call on this thread saw (set by PlansClient.Send / Layout / Geometry / RemoteConfig, read right after it)
    [ThreadStatic] static int lastHttp;
    [ThreadStatic] static int lastRetryAfter;
    public static void Reset() { lastHttp = 0; lastRetryAfter = -1; }
    public static void Record(int http, string retryAfterHeader) { lastHttp = http; lastRetryAfter = ParseRetryAfter(retryAfterHeader, DateTime.UtcNow); }
    public static int LastHttp { get { return lastHttp; } }
    public static int LastRetryAfterSec { get { return lastRetryAfter; } }

    /// <summary>A reply worth backing off for: no answer at all (0), 429 or a 5xx.</summary>
    public static bool IsFailure(int http) { return http == 0 || http == 429 || http >= 500; }

    /// <summary>Retry-After as seconds (a number, or an HTTP date relative to <paramref name="now"/>); -1 = absent or unreadable.</summary>
    public static int ParseRetryAfter(string h, DateTime now) {
      if (string.IsNullOrEmpty(h)) return -1;
      h = h.Trim();
      int sec;
      if (int.TryParse(h, out sec)) return sec >= 0 ? sec : -1;
      DateTime d;
      if (DateTime.TryParse(h, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out d)) {
        double s = (d - now).TotalSeconds;
        return s <= 0 ? 0 : (int)Math.Ceiling(s);
      }
      return -1;
    }

    /// <summary>The wait after the <paramref name="failureNo"/>-th failure in a row (1 = first): Retry-After (seconds, &gt;= 0) when given,
    /// else 5 s * 2^(n-1) capped at 5 min, scaled by 1 +- 20% with <paramref name="rnd01"/> in [0,1).</summary>
    public static int DelayMs(int failureNo, int retryAfterSec, double rnd01) {
      if (retryAfterSec >= 0) return Math.Max(1, Math.Min(retryAfterSec, MaxRetryAfterSec)) * 1000;
      if (failureNo < 1) failureNo = 1;
      double ms = MinMs;
      for (int i = 1; i < failureNo && ms < MaxMs; i++) ms *= 2;
      if (ms > MaxMs) ms = MaxMs;
      if (rnd01 < 0) rnd01 = 0; if (rnd01 > 1) rnd01 = 1;
      return (int)(ms * (1 - Jitter + 2 * Jitter * rnd01));
    }

    /// <summary>The periodic polls (sell, forum-unread, guild, arena) while the game is not running go out at most this often.</summary>
    public const int IdleMinutes = 5;
    /// <summary>True when a poll made at <paramref name="last"/> is still fresh enough while the game is not running.</summary>
    public static bool IdleThrottled(bool gameRunning, DateTime last, DateTime now) { return !gameRunning && last != DateTime.MinValue && (now - last).TotalMinutes < IdleMinutes; }

    public int Failures { get { return failures; } }
    public bool Blocked(DateTime now) { lock (this) return now < until; }
    public DateTime Until { get { lock (this) return until; } }

    /// <summary>Records one outcome. Returns true when it was a failure (the caller may log it).</summary>
    public bool Note(int http, int retryAfterSec, DateTime now) { return Note(http, retryAfterSec, now, Rng.NextDouble()); }
    public bool Note(int http, int retryAfterSec, DateTime now, double rnd01) {
      lock (this) {
        if (!IsFailure(http)) { failures = 0; until = DateTime.MinValue; return false; }
        failures++;
        until = now.AddMilliseconds(DelayMs(failures, retryAfterSec, rnd01));
        return true;
      }
    }

    /// <summary>Records the outcome of the call just made on this thread (PlansClient.Send and friends set it).</summary>
    public bool NoteLast(DateTime now) { return Note(lastHttp, lastRetryAfter, now); }
  }
}
