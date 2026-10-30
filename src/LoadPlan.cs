// Wardsage app - how often the app looks at the game, and what that costs (the player's PC must not be loaded by it).
//
// LoadPlan: the periods of the periodic work (the overlay tick, the screen read, the live poll of the equip helper, the code and grade
// watches, the stall rescans), from what is going on: nothing (idle), the game minimized or behind another window, a hint shown and the
// player away from the mouse, the player or a pilot acting, a click awaiting its effect, a list moving under the frames.
// LoadMeter: per activity calls and time, one line a minute in the test build ("load: ..."), with the process CPU per all cores and per one core.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace RealmForge {
  /// <summary>The periods (ms) of the periodic work. Pure: the callers pass what they know.</summary>
  public static class LoadPlan {
    /// <summary>The overlay tick: a click awaits its effect (the expected field is polled until it changes).</summary>
    public const int EffectMs = 50;
    /// <summary>The overlay tick: a list moves under the frames of a list hint (they follow the game's scroll).</summary>
    public const int FollowMs = 33;
    /// <summary>The overlay tick: the player (or a pilot) used the mouse or the keyboard within <see cref="InputRecentMs"/>.</summary>
    public const int ActiveMs = 100;
    /// <summary>The overlay tick: a hint or a pilot is on, nobody touches anything (the hint is redrawn only when it changes).</summary>
    public const int CalmMs = 250;
    /// <summary>The overlay tick: the game is minimized, gone or behind another window (nothing to draw, nothing to click).</summary>
    public const int AwayMs = 2000;
    /// <summary>The overlay tick with no guide at all (only the recorder or nothing): once a second.</summary>
    public const int IdleMs = 1000;
    /// <summary>The overlay tick of the frame diagnostics (Settings → «Диагностика рамки»): as before.</summary>
    public const int DiagMs = 100;
    /// <summary>Input this recent over a list hint: the list may be scrolling (the wheel, a drag and its glide), its frames follow at 33 ms.</summary>
    public const int FollowInputMs = 600;
    /// <summary>Input this recent counts as the player (or a pilot) acting.</summary>
    public const int InputRecentMs = 2000;

    /// <param name="job">a guide or a pilot is on (a plan, «Показать в игре», the storage cleanup)</param>
    /// <param name="diag">the frame diagnostics run</param>
    /// <param name="effect">a pilot's click awaits its effect, or the sell pilot's drag its move</param>
    /// <param name="shown">the game window is there and not minimized</param>
    /// <param name="front">the game (or our compact window over it) is in front</param>
    /// <param name="listHint">a list hint draws frames over a list («Показать в игре», the storage cleanup hint)</param>
    /// <param name="listMoving">the list under the frames moved since the last read</param>
    /// <param name="inputMs">ms since the last mouse / keyboard input of anyone (the pilots' own included); &lt; 0 = not known</param>
    public static int OverlayMs(bool job, bool diag, bool effect, bool shown, bool front, bool listHint, bool listMoving, long inputMs) {
      if (diag) return DiagMs;
      if (!job) return IdleMs;
      if (effect) return EffectMs;
      if (!shown || !front) return AwayMs;
      if (listHint && (listMoving || (inputMs >= 0 && inputMs < FollowInputMs))) return FollowMs;   // (a wheel turn moves the list before a read shows it)
      return InputRecent(inputMs) ? ActiveMs : CalmMs;
    }

    public static bool InputRecent(long inputMs) { return inputMs < 0 || inputMs < InputRecentMs; }

    /// <summary>The least gap between two reads of the game's screens (ReadScreens).</summary>
    /// <param name="away">the game is minimized or gone</param>
    public static int ReadGapMs(bool away, bool effect, bool listMoving, long inputMs) {
      if (away) return AwayMs;
      if (effect || listMoving) return EffectMs - 5;
      return InputRecent(inputMs) ? 120 : CalmMs - 10;
    }

    /// <summary>The quick looks at the gear list between the ticks (30 ms, a screen picture each): only while someone moves the mouse or
    /// scrolls (a list flicked by the player moves far in 100 ms); a still list needs the tick's own look only.</summary>
    public static bool LookFast(long inputMs) { return InputRecent(inputMs); }

    /// <summary>The equip helper's live poll (the page's game state): 400 ms while a guide is on, 1 s with none, 2 s with the game away.</summary>
    public static int LivePollMs(bool job, bool shown, bool front) { return !shown || !front ? AwayMs : job ? 400 : IdleMs; }

    /// <summary>The promo-code watch: 300 ms while the code window is open (or was just open), else once a second; 2 s with the game away.</summary>
    public static int CodeWatchMs(bool armed, bool shown, bool front) { return armed ? 300 : !shown || !front ? AwayMs : IdleMs; }

    /// <summary>The hero grades watch: 800 ms with the game in front, 2 s away.</summary>
    public static int GradeWatchMs(bool shown, bool front) { return !shown || !front ? AwayMs : 800; }

    /// <summary>The wait (s) before the next fresh search of the game's tables for a plan that stands still: 20 s after the first, then
    /// 60, 120 and at most 300 s while the searches find the same tables (a search costs a core for seconds on a slow PC; a hint waiting
    /// for the player searched every 20 s for as long as the player did not act).</summary>
    public static int StallRescanSec(int streak) { return streak <= 0 ? 20 : streak == 1 ? 60 : streak == 2 ? 120 : 300; }

    /// <summary>The record button's tick (test build): 200 ms while recording (its clock), else 500 ms.</summary>
    public static int RecButtonMs(bool recording) { return recording ? 200 : 500; }
  }

  /// <summary>Per activity: calls and time, summed lock-free from any thread; one line a minute (test build).</summary>
  public static class LoadMeter {
    public const int Tick = 0, Screens = 1, Look = 2, Draw = 3, Put = 4, Live = 5, Cdk = 6, Grades = 7, Game = 8, RecBtn = 9,
      RecFrame = 10, Pass = 11, Forms = 12, Scan = 13, UiBtn = 14, Cell = 15, Order = 16, Count = 17;
    static readonly string[] names = { "tick", "screens", "look", "draw", "put", "live", "cdk", "grades", "game", "recbtn",
      "recframe", "pass", "forms", "scan", "uibtn", "cell", "zorder" };
    static readonly long[] calls = new long[Count], ticks = new long[Count];
    static readonly long[] lastCalls = new long[Count], lastTicks = new long[Count];

    public static string Name(int a) { return names[a]; }
    public static long Now() { return Stopwatch.GetTimestamp(); }
    /// <summary>One call of <paramref name="a"/> that began at <paramref name="t0"/> (<see cref="Now"/>).</summary>
    public static void Done(int a, long t0) { Add(a, Stopwatch.GetTimestamp() - t0); }
    public static void Add(int a, long elapsedTicks) {
      if (a < 0 || a >= Count) return;
      Interlocked.Increment(ref calls[a]); Interlocked.Add(ref ticks[a], elapsedTicks);
    }
    /// <summary>A call measured in ms elsewhere (a memory pass).</summary>
    public static void AddMs(int a, long ms) { Add(a, ms * Stopwatch.Frequency / 1000); }
    public static long Calls(int a) { return Interlocked.Read(ref calls[a]); }
    /// <summary>The readers' own measures (src/LiveTables.cs RFX.LoadHook) counted here from now on.</summary>
    public static void Hook() { RFX.LoadHook = (k, t) => Add(k == 0 ? Pass : Forms, t); }

    /// <summary>The process CPU as a share of the whole PC (all cores) and of one core, in %.</summary>
    public static void Pct(double cpuMs, double wallMs, int cores, out double total, out double perCore) {
      perCore = wallMs > 0 ? cpuMs * 100.0 / wallMs : 0;
      total = cores > 0 ? perCore / cores : perCore;
    }

    /// <summary>"load: cpu 1.2% of all 4 cores (4.8% of one core) ..." - the caller's head, then the activities since the last call,
    /// each "name N/min T ms" (calls and time a minute), only the ones that ran.</summary>
    public static string Line(double cpuMs, double wallMs, int cores, string mode, string extra) {
      var ci = CultureInfo.InvariantCulture;
      double total, one; Pct(cpuMs, wallMs, cores, out total, out one);
      var sb = new StringBuilder("load: cpu ");
      sb.Append(total.ToString("0.0", ci)).Append("% of all ").Append(cores).Append(" cores (").Append(one.ToString("0.0", ci)).Append("% of one core) (").Append(mode).Append(')');
      if (!string.IsNullOrEmpty(extra)) sb.Append(", ").Append(extra);
      double perMin = wallMs > 0 ? 60000.0 / wallMs : 0;
      bool first = true;
      for (int a = 0; a < Count; a++) {
        long c = Interlocked.Read(ref calls[a]), t = Interlocked.Read(ref ticks[a]);
        long dc = c - lastCalls[a], dt = t - lastTicks[a];
        lastCalls[a] = c; lastTicks[a] = t;
        if (dc <= 0) continue;
        sb.Append(first ? "; per min: " : ", ").Append(names[a]).Append(' ').Append(Math.Round(dc * perMin).ToString("0", ci)).Append("x ")
          .Append((dt * 1000.0 / Stopwatch.Frequency * perMin).ToString("0", ci)).Append(" ms");
        first = false;
      }
      return sb.ToString();
    }
  }
}
