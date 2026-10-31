// RealmForge — where the parts of a hint over the game go (client pixels of the game window). Pure placement, no
// drawing (app/Overlay.cs draws; tests: tests/CoreTests.cs).
//
// A hint is the game's gold frame around what to press, a step plate beside it (kept inside the window: right of the
// target, else left, else above, else below) with a small pointer toward it, and for an item out of view a gold scroll
// strip along the list with a dot where the item is in the whole list and a pill «Вещь здесь ↓ N рядов» at the dot.
using System;

namespace RealmForge {
  public static class HintLayout {
    public const int Gap = 14, Margin = 6;

    /// <summary>A plate with no target on screen: top centre of the window, 0.12 U from the top (below the title bar area), kept inside. Returns {x, y}.</summary>
    public static int[] TopPlate(int w, int h, int W, int H, double U) {
      return new[] { Clamp(W / 2 - w / 2, Margin, W - w - Margin), Clamp((int)(0.12 * U), Margin, H - h - Margin) };
    }

    /// <summary>The plate (<paramref name="w"/> × <paramref name="h"/>) beside <paramref name="target"/> {x, y, w, h}:
    /// right of it, else left, else above, else below (a wide button: above first), always inside the W × H window. <paramref name="side"/> = where the
    /// plate is: 'r', 'l', 't' (above), 'b' (below). Returns {x, y}.</summary>
    public static int[] Plate(int[] target, int w, int h, int W, int H, out char side) {
      int tx = target[0], ty = target[1], tw = target[2], th = target[3];
      int cy = Clamp(ty + th / 2 - h / 2, Margin, H - h - Margin);
      // a wide, low target (a button row such as «Заменить» / «Снять»): the plate goes above it, not over its neighbours
      if (tw > th * 1.6 && ty - Gap - h >= Margin) { side = 't'; return new[] { Clamp(tx + tw / 2 - w / 2, Margin, W - w - Margin), ty - Gap - h }; }
      if (tx + tw + Gap + w <= W - Margin) { side = 'r'; return new[] { tx + tw + Gap, cy }; }
      if (tx - Gap - w >= Margin) { side = 'l'; return new[] { tx - Gap - w, cy }; }
      int cx = Clamp(tx + tw / 2 - w / 2, Margin, W - w - Margin);
      if (ty - Gap - h >= Margin) { side = 't'; return new[] { cx, ty - Gap - h }; }
      side = 'b';
      return new[] { cx, Clamp(ty + th + Gap, Margin, H - h - Margin) };
    }

    /// <summary>Like <see cref="Plate"/>, but the plate never covers its own <paramref name="target"/> nor any rect of
    /// <paramref name="avoid"/> (the step's other click targets, {x, y, w, h}, same px; null = none): beside the target (right, left,
    /// above, below; a wide button: above, below first), slid along that side past the rects it would cover, always inside the
    /// W × H window. Null = no such place at this size (the caller shrinks the plate; <see cref="Place"/> takes the least bad).</summary>
    public static int[] TryPlace(int[] target, int[][] avoid, int w, int h, int W, int H, out char side) {
      foreach (var c in Candidates(target, avoid, w, h, W, H))
        if (c[0] >= Margin && c[1] >= Margin && c[0] + w <= W - Margin && c[1] + h <= H - Margin && Cost(c[0], c[1], w, h, target, avoid) == 0) { side = (char)c[2]; return new[] { c[0], c[1] }; }
      side = 'b';
      return null;
    }

    /// <summary><see cref="TryPlace"/>, else the place inside the window that covers the least (the target counts ten times a neighbour).</summary>
    public static int[] Place(int[] target, int[][] avoid, int w, int h, int W, int H, out char side) {
      var p = TryPlace(target, avoid, w, h, W, H, out side);
      if (p != null) return p;
      long best = long.MaxValue; p = null; side = 'b';
      foreach (var c in Candidates(target, avoid, w, h, W, H)) {
        int x = Clamp(c[0], Margin, W - w - Margin), y = Clamp(c[1], Margin, H - h - Margin);
        long k = Cost(x, y, w, h, target, avoid);
        if (k < best) { best = k; p = new[] { x, y }; side = (char)c[2]; }
      }
      return p;
    }

    /// <summary>A plate with no target (top centre, <see cref="TopPlate"/>) moved off the step's click targets when it would cover one:
    /// the top corners, then the bottom centre and corners; none free = the top centre.</summary>
    public static int[] TopPlateFree(int w, int h, int W, int H, double U, int[][] avoid) {
      var top = TopPlate(w, h, W, H, U);
      if (avoid == null || avoid.Length == 0) return top;
      int l = Margin, r = Math.Max(Margin, W - w - Margin), cx = top[0], t = top[1], b = Math.Max(Margin, H - h - Margin);
      foreach (var c in new[] { new[] { cx, t }, new[] { l, t }, new[] { r, t }, new[] { cx, b }, new[] { l, b }, new[] { r, b } })
        if (Cost(c[0], c[1], w, h, null, avoid) == 0) return c;
      return top;
    }

    // the places tried, in order: {x, y, side}
    static System.Collections.Generic.List<int[]> Candidates(int[] t, int[][] avoid, int w, int h, int W, int H) {
      int tx = t[0], ty = t[1], tw = t[2], th = t[3];
      var res = new System.Collections.Generic.List<int[]>();
      var ys = new System.Collections.Generic.List<int> { Clamp(ty + th / 2 - h / 2, Margin, H - h - Margin), ty, ty + th - h, Margin, H - h - Margin };
      var xs = new System.Collections.Generic.List<int> { Clamp(tx + tw / 2 - w / 2, Margin, W - w - Margin), tx, tx + tw - w, Margin, W - w - Margin };
      if (avoid != null) foreach (var a in avoid) if (a != null) { ys.Add(a[1] - Gap - h); ys.Add(a[1] + a[3] + Gap); xs.Add(a[0] - Gap - w); xs.Add(a[0] + a[2] + Gap); }
      // a rect of avoid that holds the target whole is the target's row (the city bar): only above or below it, never beside along the row
      bool row = false;
      if (avoid != null) foreach (var a in avoid) if (a != null && a[0] <= tx && a[1] <= ty && a[0] + a[2] >= tx + tw && a[1] + a[3] >= ty + th) row = true;
      string order = row ? "tb" : tw > th * 1.6 ? "tbrl" : "rltb";
      foreach (char s in order) {
        if (s == 'r' || s == 'l') { int x = s == 'r' ? tx + tw + Gap : tx - Gap - w; foreach (int y in ys) res.Add(new[] { x, y, s }); }
        else { int y = s == 't' ? ty - Gap - h : ty + th + Gap; foreach (int x in xs) res.Add(new[] { x, y, s }); }
      }
      return res;
    }

    // how much a plate at (x, y) covers: the target (kept a gap away) ten times, the other targets once; 0 = free
    static long Cost(int x, int y, int w, int h, int[] target, int[][] avoid) {
      long k = 0;
      if (target != null) k += 10 * Overlap(x, y, w, h, target[0] - Gap / 2, target[1] - Gap / 2, target[2] + Gap, target[3] + Gap);
      if (avoid != null) foreach (var a in avoid) if (a != null) k += Overlap(x, y, w, h, a[0] - 4, a[1] - 4, a[2] + 8, a[3] + 8);
      return k;
    }

    static long Overlap(int x, int y, int w, int h, int ax, int ay, int aw, int ah) {
      long ox = Math.Min(x + w, ax + aw) - Math.Max(x, ax), oy = Math.Min(y + h, ay + ah) - Math.Max(y, ay);
      return ox > 0 && oy > 0 ? ox * oy : 0;
    }

    /// <summary>The pointer of a plate at {x, y} (w × h) on side <paramref name="side"/> of the target: three points
    /// {x0, y0, x1, y1, tipX, tipY}, the base on the plate's edge facing the target, the tip toward its middle.</summary>
    public static double[] Pointer(int[] plate, int w, int h, char side, int[] target, double size) {
      double tcx = target[0] + target[2] / 2.0, tcy = target[1] + target[3] / 2.0;
      if (side == 'r' || side == 'l') {
        double ex = side == 'r' ? plate[0] : plate[0] + w, y = Math.Max(plate[1] + size + 4, Math.Min(plate[1] + h - size - 4, tcy));
        double tip = side == 'r' ? ex - size : ex + size;
        return new[] { ex, y - size, ex, y + size, tip, y };
      }
      double ey = side == 'b' ? plate[1] : plate[1] + h, x = Math.Max(plate[0] + size + 4, Math.Min(plate[0] + w - size - 4, tcx));
      double tipY = side == 'b' ? ey - size : ey + size;
      return new[] { x - size, ey, x + size, ey, x, tipY };
    }

    /// <summary>The scroll strip of a list: the visible part runs from <paramref name="viewTop"/> to
    /// <paramref name="viewBottom"/> (client px); the whole list is <paramref name="contentH"/> px, scrolled by
    /// <paramref name="scroll"/> px; the item's top is <paramref name="itemY"/> px into the list. Out: the dot's y and the
    /// bracket of the visible part [b0, b1] on the strip (the strip spans the visible part of the list).</summary>
    public static void Strip(double viewTop, double viewBottom, double contentH, double scroll, double itemY, out double dotY, out double b0, out double b1) {
      double len = viewBottom - viewTop, total = Math.Max(contentH, len);
      dotY = viewTop + len * Math.Max(0, Math.Min(1, itemY / total));
      b0 = viewTop + len * Math.Max(0, Math.Min(1, scroll / total));
      b1 = viewTop + len * Math.Max(0, Math.Min(1, (scroll + len) / total));
      if (b1 - b0 < 6) b1 = Math.Min(viewBottom, b0 + 6);
    }

    /// <summary>The plural form of a count: 0 = one (1, 21 ряд), 1 = few (2–4 ряда), 2 = many (5 рядов); English: 0 = 1, 2 = other.</summary>
    public static int Plural(int n, bool ru) {
      n = Math.Abs(n);
      if (!ru) return n == 1 ? 0 : 2;
      int a = n % 100, b = n % 10;
      return a > 10 && a < 20 ? 2 : b == 1 ? 0 : b >= 2 && b <= 4 ? 1 : 2;
    }

    static int Clamp(int v, int lo, int hi) { return hi < lo ? lo : Math.Max(lo, Math.Min(hi, v)); }
  }
  /// <summary>Keeps a hint steady over the game: a new step (or no hint) is taken over only when it has been asked for
  /// <see cref="DebounceMs"/> in a row (the first hint at once), so reads that flip for a moment do not hide, show or move it;
  /// within one step a tick without a target keeps the last good one (<see cref="Stale"/>: drawn as a plate only, no frame)
  /// for up to <see cref="KeepMs"/>. Pure: the caller passes the clock.</summary>
  public sealed class HintHold {
    public const int DebounceMs = 350, KeepMs = 15000;
    /// <summary>The step shown (null = no hint).</summary>
    public string Key { get; private set; }
    /// <summary>The target the plate is placed at (screen px {x, y, w, h}); null = no target (the plate at the top).</summary>
    public int[] Target { get; private set; }
    /// <summary>What came with <see cref="Target"/> (the caller's highlight shape).</summary>
    public object Tag { get; private set; }
    /// <summary>The target is the last good one, not this tick's.</summary>
    public bool Stale { get; private set; }
    string cand; long candSince, goodAt; bool any, candSet;

    public void Reset() { Key = null; Target = null; Tag = null; Stale = false; cand = null; any = candSet = false; }

    /// <summary>This tick: the step that wants a hint (null = none) and its target now (null = none this tick). True = a hint is shown.</summary>
    public bool Update(string key, int[] target, object tag, long now) {
      if (key != Key) {
        if (any) {
          if (!candSet || key != cand) { cand = key; candSet = true; candSince = now; }
          if (now - candSince < DebounceMs) return Key != null;   // not yet: the old hint stays as it was
        }
        any = true; candSet = false; Key = key; Target = target; Tag = tag; Stale = false; goodAt = now;
        return Key != null;
      }
      candSet = false;
      if (target != null) { Target = target; Tag = tag; Stale = false; goodAt = now; }
      else if (Target != null && now - goodAt < KeepMs) Stale = true;
      else { Target = null; Tag = null; Stale = false; }
      return Key != null;
    }
  }

  /// <summary>The step plate's size against the game client (U: the client's UI unit, its height on 16:9): fonts and padding
  /// in U with a readable minimum, at most <see cref="MaxWidthFrac"/> of the client wide (longer texts are cut with «…»); a small
  /// client (U below <see cref="CompactBelowU"/>) gets one short line, the step number and the action (the app's window has the
  /// whole text). Pure (tests/CoreTests.cs).</summary>
  public static class HintSize {
    public const double MaxWidthFrac = 0.4, CompactBelowU = 420;
    public const double MinCaps = 8, MinMain = 10, MinSub = 9, MinPad = 5;
    /// <summary>The font size in px: 0 = the caps line, 1 = the action (a pill's is smaller), 2 = the next step.</summary>
    public static double FontPx(int which, bool pill, double U) {
      return which == 0 ? Math.Max(MinCaps, 0.0155 * U) : which == 1 ? Math.Max(MinMain, (pill ? 0.019 : 0.026) * U) : Math.Max(MinSub, 0.018 * U);
    }
    public static double MinPx(int which) { return which == 0 ? MinCaps : which == 1 ? MinMain : MinSub; }
    public static double Pad(double U) { return Math.Max(MinPad, 0.012 * U); }
    public static int MaxWidth(double W) { return (int)Math.Max(100, MaxWidthFrac * W); }
    public static bool Compact(double U) { return U < CompactBelowU; }
    /// <summary>"N/M" from a caps line that ends with its step numbers («… · шаг 1 из 6», «… · step 1 of 6»); "" when it has none.</summary>
    public static string StepShort(string caps) {
      var m = System.Text.RegularExpressions.Regex.Match(caps ?? "", @"(\d+)\D{1,8}?(\d+)\s*$");
      return m.Success ? m.Groups[1].Value + "/" + m.Groups[2].Value : "";
    }
    /// <summary>The compact plate's one line: the step number and the action.</summary>
    public static string CompactLine(string caps, string main) {
      string n = StepShort(caps); main = main ?? "";
      return n == "" ? main : main == "" ? n : n + " · " + main;
    }
    /// <summary><paramref name="s"/> cut to at most <paramref name="maxW"/> px wide (by <paramref name="measure"/>) with «…» at the end.</summary>
    public static string Ellipsize(string s, double maxW, Func<string, double> measure) {
      if (string.IsNullOrEmpty(s) || measure(s) <= maxW) return s ?? "";
      int lo = 0, hi = s.Length - 1;
      while (lo < hi) { int mid = (lo + hi + 1) / 2; if (measure(s.Substring(0, mid).TrimEnd() + "…") <= maxW) lo = mid; else hi = mid - 1; }
      return lo == 0 ? "…" : s.Substring(0, lo).TrimEnd() + "…";
    }
    /// <summary>Two target / avoid rect lists the same within <paramref name="tol"/> px each (a live rect that jitters by a pixel is the same place).</summary>
    public static bool SameRects(int[][] a, int[][] b, int tol) {
      int na = a == null ? 0 : a.Length, nb = b == null ? 0 : b.Length;
      if (na != nb) return false;
      for (int i = 0; i < na; i++) if (!SameRect(a[i], b[i], tol)) return false;
      return true;
    }
    public static bool SameRect(int[] a, int[] b, int tol) {
      if (a == null || b == null) return a == b;
      if (a.Length != b.Length) return false;
      for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > tol) return false;
      return true;
    }
  }

  /// <summary>One plate layout per step: while the step (its id), the client size, the target and the step's other targets stay
  /// (within <see cref="Tol"/> px), the plate keeps its scale and place; only its text is redrawn. Counts the layouts of the step
  /// (1 = it never moved). Pure (tests/CoreTests.cs).</summary>
  public sealed class PlateHold {
    public const int Tol = 3;
    public string Step; public int[] Target; public int[][] Avoid; public float Scale = 1f; public int[] Pos; public char Side = 'r';
    /// <summary>The layouts made for the current step; <see cref="LastStep"/> / <see cref="LastCount"/>: the step before and its count.</summary>
    public int Count; public string LastStep; public int LastCount;
    /// <summary>True = keep the held layout (Scale, Pos, Side).</summary>
    public bool Keep(string step, int[] target, int[][] avoid) {
      return Pos != null && step == Step && HintSize.SameRect(target, Target, Tol) && HintSize.SameRects(avoid, Avoid, Tol);
    }
    /// <summary>A new layout made: held for the step; true = a new step began (LastStep / LastCount say how the one before went).</summary>
    public bool Made(string step, int[] target, int[][] avoid, float scale, int[] pos, char side) {
      bool fresh = step != Step;
      if (fresh) { LastStep = Step; LastCount = Count; Count = 0; }
      Step = step; Target = target; Avoid = avoid; Scale = scale; Pos = pos; Side = side; Count++;
      return fresh;
    }
  }

  /// <summary>A list's scroll held still: the list's scroll value (its own units; NaN = unreadable) noted each tick, still once it has not moved by
  /// more than <see cref="Tol"/> for <see cref="StillMs"/> (the first value seen counts as still; unreadable = still). The find-the-item step frames
  /// the item only then, so a drag does not move the plate and the frame each tick. Pure (tests/CoreTests.cs).</summary>
  public sealed class ScrollStill {
    public const int StillMs = 350;
    public const double Tol = 0.5;
    double y = double.NaN; long at;
    public bool Note(double scroll, long now) {
      if (double.IsNaN(scroll)) return true;
      if (double.IsNaN(y)) { y = scroll; at = now - StillMs; }
      else if (Math.Abs(scroll - y) > Tol) { y = scroll; at = now; }
      return now - at >= StillMs;
    }
    public void Reset() { y = double.NaN; at = 0; }
  }
}
