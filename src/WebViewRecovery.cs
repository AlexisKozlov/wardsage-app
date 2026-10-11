// Wardsage - pure logic for restarting the built-in browser (WebView2): which errors are worth a retry, how long to wait,
// how often a crash recovery may run, and the words for the player. The window code (app/AppWindow.cs) uses it.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RealmForge {
  public static class WebViewRecovery {
    public const int E_ABORT = unchecked((int)0x80004004);

    /// <summary>Waits before retry 1, 2, 3, 4 (seconds); after that every 60 s.</summary>
    static readonly int[] Steps = { 2, 5, 15, 30 };
    /// <summary>Retries after the first failed try: 4 short waits + 9 minutes = about 10 minutes in total.</summary>
    public const int MaxRetries = 13;

    /// <summary>Milliseconds to wait before retry number <paramref name="retry"/> (1 = the first retry).</summary>
    public static int DelayMs(int retry) {
      if (retry < 1) retry = 1;
      return (retry <= Steps.Length ? Steps[retry - 1] : 60) * 1000;
    }

    static Exception Inner(Exception e) {
      var ag = e as AggregateException;
      while (ag != null && ag.InnerExceptions.Count == 1) { e = ag.InnerExceptions[0]; ag = e as AggregateException; }
      return e;
    }

    /// <summary>A start of the browser that may work a little later (Windows still waking up, the runtime busy).
    /// A missing runtime is permanent.</summary>
    public static bool IsTransient(Exception e) {
      e = Inner(e);
      if (e == null) return false;
      if (e.GetType().Name == "WebView2RuntimeNotFoundException") return false;
      return e is COMException || e is InvalidOperationException;
    }

    /// <summary>A WebView2 call that was aborted (E_ABORT) outside the start: the browser is closing or restarting. Harmless.</summary>
    public static bool IsAbort(Exception e) {
      e = Inner(e);
      var ce = e as COMException;
      return ce != null && ce.HResult == E_ABORT;
    }

    public static string HResultHex(Exception e) {
      e = Inner(e);
      return e == null ? "" : "0x" + unchecked((uint)e.HResult).ToString("X8");
    }

    /// <summary>The text of the error box (Russian or English), with the technical code at the end.</summary>
    public static string FriendlyMessage(string lang, Exception e) {
      string tech = e == null ? "" : "\n\n(" + Inner(e).GetType().Name + " " + HResultHex(e) + ")";
      if (lang == "en")
        return "Could not open the program window (the browser built into Windows did not start). Restart Wardsage; if it keeps happening, restart the computer." + tech;
      return "Не удалось открыть окно программы (встроенный браузер Windows не запустился). Перезапусти Wardsage; если повторяется — перезагрузи компьютер." + tech;
    }
  }

  /// <summary>At most <c>max</c> recoveries per <c>window</c>: a browser that keeps crashing is not restarted forever.</summary>
  public sealed class RecoveryLimiter {
    readonly int max;
    readonly TimeSpan window;
    readonly List<DateTime> times = new List<DateTime>();

    public RecoveryLimiter(int max, TimeSpan window) { this.max = max; this.window = window; }

    /// <summary>True = a recovery may run now (and is counted).</summary>
    public bool TryAcquire(DateTime now) {
      times.RemoveAll(t => now - t >= window);
      if (times.Count >= max) return false;
      times.Add(now);
      return true;
    }
  }
}
