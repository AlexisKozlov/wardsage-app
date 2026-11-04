using System;
using System.Collections.Generic;

namespace RealmForge {
  /// <summary>A yes / no that can also be «not known» (a look whose pixels our own hint covers).</summary>
  public enum Seen3 { No = 0, Yes = 1, Unknown = 2 }

  /// <summary>The one mask of the screen looks. Where one of our own windows is shown to screen captures (the player's
  /// «show hints in recordings» setting, the recorder, Windows older than 10 2004) a grab of the game holds our drawing; those
  /// pixels are set to <see cref="ListTracker.Masked"/>, and every reader treats them as «not known», never as dark and never as a
  /// match. Pure: the screen rectangles come from the caller.</summary>
  public static class OwnMask {
    /// <summary>More of a grab than this covered = the look is not known.</summary>
    public const double MaxShare = 0.2;

    /// <summary>Sets the pixels of a grab at (x, y) w × h that lie in one of the parts ({x, y, w, h}, screen px) to Masked.</summary>
    public static void Apply(int[] px, int x, int y, int w, int h, IEnumerable<int[]> parts) {
      if (px == null || parts == null || w <= 0 || h <= 0 || px.Length < w * h) return;
      foreach (var r in parts) {
        if (r == null || r.Length < 4) continue;
        int ax = Math.Max(x, r[0]), ay = Math.Max(y, r[1]), bx = Math.Min(x + w, r[0] + r[2]), by = Math.Min(y + h, r[1] + r[3]);
        for (int yy = ay; yy < by; yy++) for (int xx = ax; xx < bx; xx++) px[(yy - y) * w + (xx - x)] = ListTracker.Masked;
      }
    }

    /// <summary>How many pixels are masked.</summary>
    public static int Count(int[] px) {
      int n = 0;
      if (px != null) foreach (int p in px) if (p == ListTracker.Masked) n++;
      return n;
    }

    /// <summary>More than <paramref name="maxShare"/> of the grab is masked (a null grab is not a look at all: covered).</summary>
    public static bool Covered(int[] px, double maxShare = MaxShare) {
      if (px == null || px.Length == 0) return true;
      return Count(px) > maxShare * px.Length;
    }

    /// <summary>Runs a yes / no reader that takes grabs (<c>grab(x, y, w, h)</c>) over a grab function that may hold masked pixels. A grab with a
    /// masked pixel is withheld from the reader (it gets null, as a failed grab); a yes the reader still reaches stays yes (the game's own pixels
    /// said it); a no that was reached with withheld grabs is not known. So the result is never the opposite of the unmasked game's.</summary>
    public static Seen3 Read(Func<Func<int, int, int, int, int[]>, bool> reader, Func<int, int, int, int, int[]> grab) {
      bool withheld = false;
      Func<int, int, int, int, int[]> g = (x, y, w, h) => {
        int[] px = grab(x, y, w, h);
        if (px != null && Count(px) > 0) { withheld = true; return null; }
        return px;
      };
      bool yes = reader(g);
      return yes ? Seen3.Yes : withheld ? Seen3.Unknown : Seen3.No;
    }

    /// <summary>Whether at least <paramref name="min"/> of the grab matches <paramref name="pred"/>, with masked pixels unknown: yes when the known
    /// matches alone reach min * N; no when the matches plus every masked pixel still fall short; else not known. A null grab is no.</summary>
    public static Seen3 ShareAtLeast(int[] px, Func<int, bool> pred, double min) {
      if (px == null || px.Length == 0) return Seen3.No;
      int m = 0, k = 0;
      foreach (int p in px) { if (p == ListTracker.Masked) k++; else if (pred(p)) m++; }
      double need = min * px.Length;
      if (m >= need) return Seen3.Yes;
      return m + k < need ? Seen3.No : Seen3.Unknown;
    }

    /// <summary>Both must be yes for yes; one no is no; else not known.</summary>
    public static Seen3 And(Seen3 a, Seen3 b) {
      if (a == Seen3.No || b == Seen3.No) return Seen3.No;
      return a == Seen3.Yes && b == Seen3.Yes ? Seen3.Yes : Seen3.Unknown;
    }

    /// <summary>One yes is yes; else not known if either is; else no.</summary>
    public static Seen3 Or(Seen3 a, Seen3 b) {
      if (a == Seen3.Yes || b == Seen3.Yes) return Seen3.Yes;
      return a == Seen3.Unknown || b == Seen3.Unknown ? Seen3.Unknown : Seen3.No;
    }
  }

  /// <summary>Which look answers count as «not known» for the click gate (docs/specs/2026-11-04-memory-first-state.md): a state the game's own flag answers (Yes / No) is KNOWN whatever its picture says - a picture our hint covers
  /// never makes a click wait ('own'); the picture only matters while the flag is Unknown.</summary>
  public static class LookPolicy {
    public static bool Blocks(Mem3 mem, Seen3 look) { return mem == Mem3.Unknown && look == Seen3.Unknown; }
  }

  /// <summary>The looks that came back «not known» (covered by our own hint): while one is, the pilot does not click; one that stays
  /// unknown for <see cref="TimeoutMs"/> is a problem to report (never a guess). A look not repeated for <see cref="StaleMs"/> no longer holds
  /// the pilot back (the flow moved on).</summary>
  public sealed class UnknownLooks {
    public const long TimeoutMs = 8000, StaleMs = 1500;
    readonly Dictionary<string, long> since = new Dictionary<string, long>(), last = new Dictionary<string, long>();
    readonly HashSet<string> reported = new HashSet<string>();

    /// <summary>A look of this kind gave a known (false) or an unknown (true) answer at <paramref name="now"/> (ms).</summary>
    public void Note(string kind, bool unknown, long now) {
      if (!unknown) { since.Remove(kind); last.Remove(kind); reported.Remove(kind); return; }
      long l;
      if (!last.TryGetValue(kind, out l) || now - l > StaleMs || now < l) { since[kind] = now; reported.Remove(kind); }
      last[kind] = now;
    }

    /// <summary>Two clocks read a few ms apart (the tick start, the fresh read): a note up to SkewMs ahead of <paramref name="now"/> is still fresh.</summary>
    public const long SkewMs = 250;
    static bool Fresh(long now, long last) { return now - last <= StaleMs && last - now <= SkewMs; }

    /// <summary>The kind of a look that is unknown now (and fresh), or null: no click this tick.</summary>
    public string Blocking(long now) {
      foreach (var kv in last) if (Fresh(now, kv.Value)) return kv.Key;
      return null;
    }

    /// <summary>The kind unknown for the whole timeout, once; null otherwise.</summary>
    public string Overdue(long now) {
      foreach (var kv in last) {
        if (!Fresh(now, kv.Value) || reported.Contains(kv.Key)) continue;
        long s; if (since.TryGetValue(kv.Key, out s) && now - s >= TimeoutMs) { reported.Add(kv.Key); return kv.Key; }
      }
      return null;
    }

    public void Clear() { since.Clear(); last.Clear(); reported.Clear(); }
  }
}
