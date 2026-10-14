// RealmForge — where an item of the game's gear list is on the screen (for the highlight over the game).
//
// Pure logic, no Windows APIs (tests: tests/CoreTests.cs). The screen is only LOOKED at: the host takes a
// screenshot of the list area and passes its pixels here. Nothing is sent to the game.
//
// The list scroll position is not in the game's script data, so it is found like this:
//   * anchor: when the player clicks an item, the game stores its uid (and we know its row from the script list);
//     the mouse cursor is on that item at that moment, which gives its row on the screen;
//   * tracking: every item cell has a stat bar under it (slate-blue band with the main stat); the bars repeat with
//     the row pitch, so their vertical phase on a screenshot shows how far the list has moved since the anchor;
//   * whole rows: the phase repeats every row, so the strip picture of the last tick is compared with the new one
//     (ScrollFollow) to tell how many rows the list went.
using System;
using System.Collections.Generic;

namespace RealmForge {
  /// <summary>The game's UI scale (CanvasScalerRoot.HandleScaleWithScreenSize): the 16:9 reference layout (1200×675) is
  /// fitted to the window height, or to its width when the window is narrower than 16:9 (16:10, 4:3). Unit(W, H) is the
  /// height the reference layout gets on the screen, in client px: every size and every distance from the edge an element
  /// is anchored to scales with it (measured on the live game 2026-09-26: 1920×1009, 1920×1057, 1600×1000). Elements
  /// anchored to the top / left edge: f·U; to the bottom: H − (1 − f)·U; to the centre: H/2 + (f − 0.5)·U.</summary>
  public static class Ui {
    public const double RefAspect = 675.0 / 1200.0;
    public static double Unit(double W, double H) { return Math.Min(H, W * RefAspect); }
    public static double FromBottom(double f, double W, double H) { return H - (1 - f) * Unit(W, H); }
    public static double FromMiddle(double f, double W, double H) { return H / 2 + (f - 0.5) * Unit(W, H); }
  }

  /// <summary>Gear list layout of the equipment screen, in fractions of the game client height (measured on the
  /// live game, 1456×764 client). The game UI scales with the window height and is anchored to the left edge.</summary>
  public sealed class ListGeometry {
    public double X0 = 0.0393;          // left edge of the first column's cell
    public double PitchX = 0.1257;      // column step
    public double CellW = 0.1126;       // cell (icon square) width = height
    public double PitchY = 0.1571;      // row step of item rows (template height 100)
    public double BarTop = 0.1139, BarBottom = 0.1453;   // stat bar under the cell, from the row top
    public double ViewTop = 0.1518, ViewBottom = 0.8743; // visible part of the list
    public const double TitleRow = 0.44, EmptyRow = 0.60;
    public const double TopPad = 0.0135;   // gap between the list's top edge and row 1 at scroll 0 (measured on the game screen)  // other row kinds, relative to an item row (44 / 60 vs 100)
    public const int Columns = 3;

    /// <summary>Item rows the visible part of the list shows (about 4.6).</summary>
    public double VisibleRows { get { return (ViewBottom - ViewTop) / PitchY; } }

    public double RowHeight(int type) { return type == 2 ? PitchY * TitleRow : type == 3 ? PitchY * EmptyRow : PitchY; }

    /// <summary>Offset of the top of row <paramref name="row"/> from the top of row 1 (types[i] = kind of row i, 1-based).</summary>
    public double RowOffset(int[] types, int row) {
      double y = 0;
      for (int i = 1; i < row; i++) y += RowHeight(types != null && i < types.Length ? types[i] : 1);
      return y;
    }

    public double ColLeft(int col) { return X0 + (col - 1) * PitchX; }
    public double StripLeft { get { return X0; } }
    public double StripRight { get { return X0 + (Columns - 1) * PitchX + CellW; } }

    /// <summary>Column under a horizontal position (fractions of height), 0 = between/outside cells.</summary>
    public int ColumnAt(double x) {
      for (int c = 1; c <= Columns; c++) { double l = ColLeft(c); if (x >= l - 0.01 && x <= l + CellW + 0.01) return c; }
      return 0;
    }
  }

  /// <summary>The game's blue action button («Заменить», «Надеть»). Measured on the live game (2026-09-26, 1920×1009 and
  /// 61 diagnostics screenshots): in the middle of the button 0.35 («Заменить») / 0.43 («Надеть») of the pixels are its
  /// blue fill (white text and the gradient are the rest), 0.26 while the item card is still fading in, 0.00 anywhere
  /// else on the gear and hero screens (gold «Улучшить», grey «Макс. уровень», the dark background).</summary>
  public static class ButtonCheck {
    public const double MinShare = 0.2;   // 0.28 on a 1600×1000 window (smaller button, the text a bigger part of it)

    public static bool IsBlueFill(int argb) {
      int r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
      return b >= 85 && b >= r + 30 && b >= g + 18;
    }

    public static double BlueShare(int[] px) { return Share(px, IsBlueFill); }

    /// <summary>The red round × of the filter panels: about 0.6 of a small square on it, 0 when the panel is closed.</summary>
    public static bool IsCloseRed(int argb) {
      int r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
      return r >= 150 && r >= g + 70 && r >= b + 70;
    }

    /// <summary>The gold round buttons of the sets panel: about 0.43, 0 on the stats panel.</summary>
    public static bool IsGold(int argb) {
      int r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
      return r >= 150 && g >= 100 && r >= b + 60 && g >= b + 30;
    }

    public const double MinMarkShare = 0.25;

    public static double Share(int[] px, Func<int, bool> test) {
      if (px == null || px.Length == 0) return 0;
      int n = 0;
      foreach (int p in px) if (test(p)) n++;
      return n / (double)px.Length;
    }
  }

  public static class ListTracker {
    /// <summary>Is this pixel part of a stat bar (desaturated slate blue)? Rarity backgrounds are saturated
    /// (red, purple, blue, gold) or grey, the bar is in between.</summary>
    public static bool IsBar(int argb) {
      int r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
      if (b < 70 || b > 185) return false;
      if (b < r + 18 || b < g + 8) return false;
      int mn = Math.Min(r, Math.Min(g, b));
      double sat = (b - mn) / (double)b;
      return sat >= 0.18 && sat <= 0.52;
    }

    /// <summary>Share of stat-bar pixels on every line of a screenshot strip (pixels row by row, width w),
    /// counting only the columns' cells (xs: [from, to) pairs in strip pixels).</summary>
    public static double[] BarProfile(int[] px, int w, int h, int[] xs) {
      var prof = new double[h];
      for (int y = 0; y < h; y++) {
        int n = 0, all = 0, row = y * w;
        for (int k = 0; k + 1 < xs.Length; k += 2)
          for (int x = Math.Max(0, xs[k]); x < Math.Min(w, xs[k + 1]); x++) { all++; if (IsBar(px[row + x])) n++; }
        prof[y] = all > 0 ? n / (double)all : 0;
      }
      return prof;
    }

    /// <summary>Vertical phase of the rows: the row-top position modulo the pitch (in strip pixels) that best lines up
    /// the bars, and how clearly (share of bar pixels inside the bar bands minus outside; about 0.3+ on a real list).</summary>
    public static bool FindPhase(double[] prof, double pitch, double barTop, double barBottom, out double phase, out double conf) {
      phase = 0; conf = 0;
      int h = prof.Length;
      if (pitch < 8 || h < pitch * 1.5) return false;
      int steps = (int)Math.Ceiling(pitch);
      double best = double.MinValue; int bestP = 0;
      var scores = new double[steps];
      for (int p = 0; p < steps; p++) {
        double inS = 0, outS = 0; int inN = 0, outN = 0;
        for (int y = 0; y < h; y++) {
          double t = (y - p) % pitch; if (t < 0) t += pitch;
          if (t >= barTop + 1 && t < barBottom - 1) { inS += prof[y]; inN++; }
          else if (t < barTop - 2 || t >= barBottom + 2) { outS += prof[y]; outN++; }
        }
        double s = (inN > 0 ? inS / inN : 0) - (outN > 0 ? outS / outN : 0);
        scores[p] = s;
        if (s > best) { best = s; bestP = p; }
      }
      // refine to a fraction of a pixel with the neighbours
      double l = scores[(bestP - 1 + steps) % steps], r = scores[(bestP + 1) % steps], d = l - 2 * best + r;
      double off = d < 0 ? 0.5 * (l - r) / d : 0;
      phase = bestP + Math.Max(-0.5, Math.Min(0.5, off));
      conf = best;
      return best >= 0.15;
    }

    /// <summary>Vertical phase of a grid of cards with dark gaps between the rows (the hero list): the card top modulo the
    /// pitch (strip lines) where the cards are brightest against the gaps; prof = mean brightness of every line over the
    /// cards. conf: card minus gap brightness (40–50 on the live hero list at 1920×1009, about −5 at other phases).</summary>
    public static bool CardPhase(double[] prof, double pitch, double card, out double phase, out double conf) {
      phase = 0; conf = 0;
      int h = prof.Length;
      if (pitch < 8 || card >= pitch || h < pitch * 1.5) return false;
      int steps = (int)Math.Ceiling(pitch); double best = double.MinValue; int bp = 0;
      for (int p = 0; p < steps; p++) {
        double a = 0, b = 0; int na = 0, nb = 0;
        for (int y = 0; y < h; y++) {
          double t = (y - p) % pitch; if (t < 0) t += pitch;
          if (t >= 3 && t < card - 3) { a += prof[y]; na++; }
          else if (t >= card + 2 && t < pitch - 2) { b += prof[y]; nb++; }
        }
        double s = (na > 0 ? a / na : 0) - (nb > 0 ? b / nb : 0);
        if (s > best) { best = s; bp = p; }
      }
      phase = bp; conf = best;
      return best >= 15;
    }

    /// <summary>How many columns a grid of cells with stat bars fills the strip with (pixels row by row, width w): the
    /// bars' horizontal profile repeats with the column step, so the count whose step correlates best wins (of nearly
    /// equal ones the larger: half the count correlates at twice the step too). 0 = no bars seen. Checked on the
    /// inventory at 1920×1009, 1600×1000 and 1300×1000 (8 columns, bars broken by the icons and numbers in them).</summary>
    public static int GridColumns(int[] px, int w, int h, int min = 4, int max = 16) {
      var col = new double[w]; int lines = 0;
      for (int y = 0; y < h; y++) {
        int row = y * w, n = 0;
        for (int x = 0; x < w; x++) if (IsBar(px[row + x])) n++;
        if (n <= 0.2 * w) continue;                  // only lines through the bars
        lines++;
        for (int x = 0; x < w; x++) if (IsBar(px[row + x])) col[x]++;
      }
      if (lines < 3) return 0;
      double m = 0; foreach (var v in col) m += v; m /= w;
      for (int x = 0; x < w; x++) col[x] -= m;
      var score = new double[max + 1]; double best = double.MinValue;
      for (int cols = min; cols <= max; cols++) {
        double lag = w / (double)cols, s = 0; int n = 0;
        for (int x = 0; x + lag < w - 1; x++) {
          double xl = x + lag; int i = (int)xl; double f = xl - i;
          s += col[x] * (col[i] * (1 - f) + col[i + 1] * f); n++;
        }
        score[cols] = n > 0 ? s / n : double.MinValue;
        if (score[cols] > best) best = score[cols];
      }
      if (best <= 0) return 0;
      for (int cols = max; cols >= min; cols--) if (score[cols] >= best * 0.9) return cols;
      return 0;
    }

    /// <summary>Moves y to the nearest position with the given phase (both in the same pixels); the move is in (-pitch/2, pitch/2].</summary>
    public static double Snap(double y, double phase, double pitch) {
      double d = (phase - y) % pitch;
      if (d > pitch / 2) d -= pitch;
      if (d <= -pitch / 2) d += pitch;
      return y + d;
    }

    /// <summary>A picture of the list strip to follow its movement with: for every line, the mean brightness of
    /// <paramref name="segs"/> equal parts of every cell column (xs: [from, to) pairs in strip pixels), smoothed over 5 lines
    /// (a list at a fraction of a pixel then still matches). Lines × (columns · segs).</summary>
    public static float[] LineSig(int[] px, int w, int h, int[] xs, int segs) {
      int cols = xs.Length / 2, n = cols * segs;
      var raw = new float[h * n];
      for (int y = 0; y < h; y++) {
        int k = 0, row = y * w;
        for (int c = 0; c < cols; c++) {
          int a = Math.Max(0, xs[2 * c]), b = Math.Min(w, xs[2 * c + 1]);
          for (int s = 0; s < segs; s++, k++) {
            int x0 = a + (b - a) * s / segs, x1 = a + (b - a) * (s + 1) / segs;
            double sum = 0;
            for (int x = x0; x < x1; x++) { int p = px[row + x]; sum += ((p >> 16) & 255) + ((p >> 8) & 255) + (p & 255); }
            raw[y * n + k] = x1 > x0 ? (float)(sum / (3.0 * (x1 - x0))) : 0f;
          }
        }
      }
      const int R = 2;
      var sig = new float[h * n];
      for (int y = 0; y < h; y++) {
        int y0 = Math.Max(0, y - R), y1 = Math.Min(h - 1, y + R);
        for (int k = 0; k < n; k++) {
          double sum = 0;
          for (int yy = y0; yy <= y1; yy++) sum += raw[yy * n + k];
          sig[y * n + k] = (float)(sum / (y1 - y0 + 1));
        }
      }
      return sig;
    }

    // what counts as the same picture (mean brightness difference of the smoothed pictures, 0..255). Measured on live
    // screenshots of a 600×340 window (the hardest case: small cells, sharp edges): a still list 0–1.6 apart, the right shift
    // of a moved list 2–2.6 (a fraction of a pixel off), a shift a row off 8.4+ even on a list of alike amulets
    public const double StillDiff = 1.0, MatchDiff = 6.0, MinOverlap = 0.4;

    /// <summary>How far the list moved between two strip pictures (LineSig, <paramref name="n"/> values per line), in px to
    /// half a pixel, positive = the content went down. The shift with the smallest mean difference over the overlap (at least
    /// MinOverlap of the strip). False when that is not clear: nothing fits (the list moved further than the strip, something
    /// covers it, another screen) or a shift half a row or more away fits nearly as well (rows of alike items).</summary>
    public static bool Register(float[] a, float[] b, int n, double pitch, out double shift, out double diff, out double second) {
      var m = Match(a, b, n, pitch);
      shift = m.Shift; diff = m.Diff; second = m.Second;
      return m.Ok;
    }

    /// <summary>The shifts that fit two strip pictures (see Register): <see cref="ShiftMatch.Shifts"/> are the ones nearly as
    /// good as the best (more than one: rows of alike items, which one is right only the speed of the list can tell), best
    /// first; Ok = exactly one, clearly.</summary>
    public static ShiftMatch Match(float[] a, float[] b, int n, double pitch) {
      var m = new ShiftMatch();
      if (a == null || b == null || n <= 0 || a.Length != b.Length || a.Length % n != 0) return m;
      int h = a.Length / n;
      if (h < 8) return m;
      // a still list (most ticks): the same picture
      double d0 = SigDiff(a, b, n, h, 0);
      if (d0 <= StillDiff) { m.Ok = m.Still = true; m.Diff = d0; m.Shifts = new[] { 0.0 }; m.Diffs = new[] { d0 }; return m; }
      // every shift on pictures F times shorter (a 1000 px high window: 6 lines in one), then the good ones on the full
      // pictures, to half a pixel
      int F = Math.Max(1, h / 120);
      float[] ca = F > 1 ? Shrink(a, n, h, F) : a, cb = F > 1 ? Shrink(b, n, h, F) : b;
      int ch = ca.Length / n, maxS = (int)(ch * (1 - MinOverlap));
      var d = new double[2 * maxS + 1];
      double cbv = double.MaxValue;
      for (int s = -maxS; s <= maxS; s++) { double v = SigDiff(ca, cb, n, ch, s); d[s + maxS] = v; if (v < cbv) cbv = v; }
      // local minima nearly as good as the best (the coarse pictures blur a little: some slack), half a row apart at least
      var cand = new System.Collections.Generic.List<int>();
      for (int s = -maxS; s <= maxS; s++) {
        double v = d[s + maxS];
        if (v > cbv * 1.5 + 3) continue;
        if (s > -maxS && d[s + maxS - 1] < v) continue;
        if (s < maxS && d[s + maxS + 1] <= v) continue;
        cand.Add(s);
      }
      var shifts = new System.Collections.Generic.List<double>(); var diffs = new System.Collections.Generic.List<double>();
      foreach (int cs in cand) {
        int t; double v = Around(a, b, n, h, cs * F, F, out t), sh = t;
        double l = HalfDiff(a, b, n, h, t - 1), r = HalfDiff(a, b, n, h, t);
        if (l < v) { v = l; sh = t - 0.5; }
        if (r < v) { v = r; sh = t + 0.5; }
        bool near = false;
        for (int i = 0; i < shifts.Count; i++)
          if (Math.Abs(shifts[i] - sh) < pitch / 2) { near = true; if (v < diffs[i]) { shifts[i] = sh; diffs[i] = v; } }
        if (!near) { shifts.Add(sh); diffs.Add(v); }
      }
      if (shifts.Count == 0) return m;
      // best first
      var order = new int[shifts.Count]; for (int i = 0; i < order.Length; i++) order[i] = i;
      Array.Sort(order, (x, y) => diffs[x].CompareTo(diffs[y]));
      double bv = diffs[order[0]];
      // the best other shift half a row or more away, from every shift (not only the minima): how clear the best one is
      double best = shifts[order[0]], sv = double.MaxValue, cov = double.MaxValue; int cOther = int.MinValue;
      for (int s = -maxS; s <= maxS; s++) if (Math.Abs(s * F - best) >= pitch / 2 + F && d[s + maxS] < cov) { cov = d[s + maxS]; cOther = s; }
      if (cOther != int.MinValue) {
        int t; sv = Around(a, b, n, h, cOther * F, F, out t);
        sv = Math.Min(sv, Math.Min(HalfDiff(a, b, n, h, t - 1), HalfDiff(a, b, n, h, t)));
      }
      for (int i = 1; i < order.Length; i++) sv = Math.Min(sv, diffs[order[i]]);
      var gs = new System.Collections.Generic.List<double>(); var gd = new System.Collections.Generic.List<double>();
      foreach (int i in order) if (diffs[i] <= MatchDiff && diffs[i] < bv * 1.5 + 2) { gs.Add(shifts[i]); gd.Add(diffs[i]); }
      m.Shifts = gs.ToArray(); m.Diffs = gd.ToArray();
      m.Shift = best; m.Diff = bv; m.Second = sv;
      m.Ok = bv <= MatchDiff && sv >= bv * 1.5 + 2;
      return m;
    }

    /// <summary>Is there an item cell in a column at each row slot of the strip (pixels row by row, width w)? Slot k's top is
    /// at phase + k·pitch (strip px); a cell is coloured (its rarity background, the item), the list's background behind
    /// the cells is the dark game scene. 1 = a cell, 0 = none, -1 = too little of the slot on the screen or not clear
    /// (measured on live screenshots: 0.26–0.57 of a cell's pixels are coloured, 0.00 of an empty slot).</summary>
    public static int[] SlotCells(int[] px, int w, int h, int x0, int x1, double phase, double pitch, double cell, out double[] tops) {
      var st = new System.Collections.Generic.List<int>(); var tp = new System.Collections.Generic.List<double>();
      x0 = Math.Max(0, x0); x1 = Math.Min(w, x1);
      for (int k = -2; phase + k * pitch < h; k++) {
        double y = phase + k * pitch, a = Math.Max(0, y + cell * 0.05), b = Math.Min(h, y + cell * 0.95);
        if (b - a < cell * 0.25 || x1 <= x0) continue;
        int n = 0, c = 0;
        for (int yy = (int)Math.Ceiling(a); yy < b; yy++)
          for (int x = x0; x < x1; x++) {
            int p = px[yy * w + x], r = (p >> 16) & 255, g = (p >> 8) & 255, bl = p & 255;
            int mx = Math.Max(r, Math.Max(g, bl)), mn = Math.Min(r, Math.Min(g, bl));
            n++; if (mx - mn >= 50 && mx >= 110) c++;
          }
        double share = n > 0 ? c / (double)n : 0;
        st.Add(share >= 0.15 ? 1 : share <= 0.03 ? 0 : -1); tp.Add(y);
      }
      tops = tp.ToArray();
      return st.ToArray();
    }

    /// <summary>An end of the list on the screen (SlotCells of the first column, which every row fills): +1 = the bottom end
    /// (cells, then empty slots: <paramref name="top"/> = the last row's top), -1 = the top end (empty slots above the
    /// first row: the game lets the list be pulled past its ends for a moment; <paramref name="top"/> = row 1's top),
    /// 0 = no end seen or not clear.</summary>
    public static int ListEdge(int[] cells, double[] tops, out double top) {
      top = 0;
      if (cells == null || cells.Length < 2) return 0;
      foreach (int c in cells) if (c < 0) return 0;
      int changes = 0, at = -1;
      for (int i = 1; i < cells.Length; i++) if (cells[i] != cells[i - 1]) { changes++; at = i; }
      if (changes != 1) return 0;
      if (cells[at - 1] == 1) { top = tops[at - 1]; return 1; }
      top = tops[at]; return -1;
    }

    /// <summary>The game's «selected» glow around a cell: a gold glow in the gaps left and right of the cell (lx: the cell's
    /// left edge, rx: its right edge, strip px; pixels row by row, width w). The glow's lines [y0, y1], false when there is
    /// not exactly one clear run of it at least <paramref name="minLen"/> long (or reaching the strip's edge).</summary>
    public static bool GlowRun(int[] px, int w, int h, int lx, int rx, double minLen, out int y0, out int y1) {
      y0 = y1 = -1;
      var xs = new[] { lx - 6, lx - 5, lx - 4, lx - 3, rx + 2, rx + 3, rx + 4, rx + 5 };
      int runs = 0, start = -1, gap = 0, last = -1;
      for (int y = 0; y <= h; y++) {
        bool on = false;
        if (y < h) {
          int n = 0, c = 0;
          foreach (int x in xs) {
            if (x < 0 || x >= w) continue;
            int p = px[y * w + x], r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
            n++; if (r >= 90 && r - b >= 35 && g >= 0.55 * r) c++;
          }
          on = n > 0 && c >= 0.4 * n;
        }
        if (on) { if (start < 0) start = y; last = y; gap = 0; continue; }
        if (start >= 0 && (++gap > 6 || y == h)) {
          bool edge = start == 0 || last >= h - 1;
          if (last - start + 1 >= minLen || (edge && last - start + 1 >= minLen * 0.3)) { runs++; y0 = start; y1 = last; }
          start = -1; gap = 0;
        }
      }
      return runs == 1;
    }

    // the picture with every F lines averaged into one
    static float[] Shrink(float[] a, int n, int h, int F) {
      int ch = h / F;
      var c = new float[ch * n];
      for (int y = 0; y < ch; y++)
        for (int k = 0; k < n; k++) {
          double sum = 0;
          for (int i = 0; i < F; i++) sum += a[(y * F + i) * n + k];
          c[y * n + k] = (float)(sum / F);
        }
      return c;
    }

    // the best whole-pixel shift within F of s on the full pictures
    static double Around(float[] a, float[] b, int n, int h, int s, int F, out int best) {
      best = s; double bv = double.MaxValue;
      for (int t = s - F; t <= s + F; t++) { double v = SigDiff(a, b, n, h, t); if (v < bv) { bv = v; best = t; } }
      return bv;
    }

    // mean |a[y] - b[y + s]| over the lines both pictures have
    static double SigDiff(float[] a, float[] b, int n, int h, int s) {
      int y0 = Math.Max(0, -s), y1 = Math.Min(h, h - s);
      if (y1 - y0 <= 0) return double.MaxValue;
      double sum = 0;
      for (int y = y0; y < y1; y++) {
        int ia = y * n, ib = (y + s) * n;
        for (int k = 0; k < n; k++) { double v = a[ia + k] - b[ib + k]; sum += v < 0 ? -v : v; }
      }
      return sum / ((y1 - y0) * (double)n);
    }

    // the same for the shift s + 0.5 (b between its lines y + s and y + s + 1)
    static double HalfDiff(float[] a, float[] b, int n, int h, int s) {
      int y0 = Math.Max(0, -s), y1 = Math.Min(h, h - s - 1);
      if (y1 - y0 <= 0) return double.MaxValue;
      double sum = 0;
      for (int y = y0; y < y1; y++) {
        int ia = y * n, ib = (y + s) * n;
        for (int k = 0; k < n; k++) { double v = a[ia + k] - 0.5 * (b[ib + k] + b[ib + n + k]); sum += v < 0 ? -v : v; }
      }
      return sum / ((y1 - y0) * (double)n);
    }
  }

  /// <summary>What two strip pictures say about the list's move (ListTracker.Match).</summary>
  public sealed class ShiftMatch {
    public bool Ok, Still;
    public double Shift, Diff, Second = double.MaxValue;
    public double[] Shifts = new double[0], Diffs = new double[0];   // the shifts that fit nearly as well as the best, best first
  }

  /// <summary>Keeps the anchor on the list while it scrolls. The stat bars' phase alone repeats every row, so a scroll of more
  /// than half a row between two looks was taken for a small one the other way (live 2026-10-14, 600×340 window). The strip
  /// picture of the last look the anchor was sure of is compared with the new one: the shift that fits says how far the list
  /// went, the bar phase then puts it on the pixel. Lists of alike items repeat: a picture can fit at several shifts, and a
  /// list flicked further than the strip in one look fits somewhere wrong (live 2026-10-14, 1920×1009: 25 rows off after a
  /// flick through rows of the same chest pieces). So the list's speed is kept: of several fitting shifts the one the speed
  /// predicts is taken, a shift far from it while the list is fast is not trusted, and once unsure the old picture is not
  /// matched again after a moment (the list has gone an unknown way): the anchor waits for something that says where the
  /// rows are (a click, the selected item's glow, an end of the list, the list back at its top).</summary>
  public sealed class ScrollFollow {
    public float[] Last;          // the strip at the last look the anchor was sure of, and when that was
    public long LastMs;
    public double Speed;          // px per ms at the last sure look (content down = positive)
    public float[] Top;           // the strip with the list at the very top, and row 1's top then
    public double TopRow1;
    public bool Unsure;
    public long LostMs;
    public string Note = "";      // how this look went (diagnostics): first / still / track / top / lost / gone
    public double Shift, Diff, Second, Pred;
    /// <summary>How long after the anchor became unsure the last sure picture may still be matched (the list moved less
    /// than a strip meanwhile only for so long).</summary>
    public const long RetryMs = 250;

    public void Reset() { Last = null; Top = null; Unsure = false; Note = ""; Shift = 0; Diff = Second = Pred = 0; Speed = 0; LastMs = 0; }

    /// <summary>The anchor was just set (list top, a click, other evidence) on this picture (null = take the next one).</summary>
    public void Anchored(float[] sig, long nowMs) { Last = sig; LastMs = nowMs; Unsure = false; Speed = 0; }

    /// <summary>Moves the anchor by how far the list went since the last sure picture. True = the anchor is sure now.</summary>
    public bool Follow(ScrollAnchor a, float[] sig, int n, double pitch, long nowMs) {
      if (!a.Has) { Note = "none"; return false; }
      if (Last == null || sig == null || Last.Length != sig.Length) {
        if (Last != null && sig != null) { Unsure = true; LostMs = nowMs; Note = "gone"; return false; }   // the window was resized
        if (sig != null) { Last = sig; LastMs = nowMs; }
        Note = "first"; return !Unsure;
      }
      if (Unsure && nowMs - LostMs > RetryMs) { Note = "gone"; return TopMatch(a, sig, n, pitch, nowMs); }
      double dt = Math.Max(1, nowMs - LastMs);
      Pred = Speed * dt;
      var m = ListTracker.Match(Last, sig, n, pitch);
      Shift = m.Shift; Diff = m.Diff; Second = m.Second;
      double s;
      if (Choose(m, Pred, dt, pitch, sig.Length / n, out s)) {
        a.Row1Top += s; Speed = s / dt; Last = sig; LastMs = nowMs; Unsure = false; Shift = s;
        Note = m.Still ? "still" : "track";
        return true;
      }
      if (TopMatch(a, sig, n, pitch, nowMs)) return true;
      if (!Unsure) { Unsure = true; LostMs = nowMs; }
      Note = "lost";
      return false;
    }

    // which fitting shift is the move: the only one, unless the list is fast and it is far from where the speed says the
    // list is; of several (alike rows) the one the speed predicts, when exactly one is near it and the look came soon
    // enough after the last one for the speed to tell (a list cannot change its speed by a row in 70 ms)
    static bool Choose(ShiftMatch m, double pred, double dt, double pitch, int h, out double s) {
      s = 0;
      if (m.Still) return true;
      var c = m.Shifts;
      if (c == null || c.Length == 0) return false;
      double tol = Math.Max(0.5 * pitch, 0.35 * Math.Abs(pred));
      if (c.Length == 1 && m.Ok) {
        s = c[0];
        bool fast = Math.Abs(pred) >= pitch / 2 || Math.Abs(s) >= 0.4 * h;
        return !fast || Math.Abs(s - pred) <= tol;
      }
      if (dt > 70) return false;
      int near = 0, far = 0;
      foreach (double x in c) { double e = Math.Abs(x - pred); if (e <= 0.3 * pitch) { near++; s = x; } else if (e >= 0.7 * pitch) far++; }
      return near == 1 && near + far == c.Length;
    }

    // the list back at its top: that picture is known (only within half a row of it - further down, rows of alike items
    // elsewhere in a long list could pass for the top ones)
    bool TopMatch(ScrollAnchor a, float[] sig, int n, double pitch, long nowMs) {
      if (Top == null || Top.Length != sig.Length) return false;
      var m = ListTracker.Match(Top, sig, n, pitch);
      if (!m.Ok || Math.Abs(m.Shift) > pitch / 2) return false;
      a.Row1Top = TopRow1 + m.Shift; Last = sig; LastMs = nowMs; Speed = 0; Unsure = false; Note = "top";
      Shift = m.Shift; Diff = m.Diff; Second = m.Second;
      return true;
    }

    /// <summary>After the phase snap: a sure anchor with the list at the top (row 1 within a few px of
    /// <paramref name="topRow1"/>) keeps this picture as the list-top one.</summary>
    public void Settled(ScrollAnchor a, float[] sig, double topRow1, double pitch) {
      if (Unsure || !a.Has || sig == null) return;
      if (Math.Abs(a.Row1Top - topRow1) <= Math.Max(3.5, pitch * 0.06)) { Top = sig; TopRow1 = a.Row1Top; }
    }
  }

  /// <summary>Where row 1 of the list is on the screen now (client pixels; can be far above the window).</summary>
  public sealed class ScrollAnchor {
    public bool Has;
    public double Row1Top;       // client px
    public ulong ListPtr;        // the list the anchor belongs to

    /// <summary>The player clicked the item in row <paramref name="selRow"/>: its row top is somewhere in
    /// [cursorY - rowSpan, cursorY]; take the middle, the bar phase then snaps it to the real row.</summary>
    public void FromClick(ListGeometry g, int[] types, int selRow, double cursorY, double H, ulong listPtr) {
      double selTop = cursorY - g.BarBottom * H / 2;
      Row1Top = selTop - g.RowOffset(types, selRow) * H;
      Has = true; ListPtr = listPtr;
    }

    /// <summary>Row 1's top when the list is scrolled to the very top (the game opens the list there), client px.</summary>
    public static double TopRow1(ListGeometry g, double H) { return (g.ViewTop + ListGeometry.TopPad) * H; }

    /// <summary>The list was just (re)built: if the stat bars on the screen sit where they would with the list at the top,
    /// anchor there without waiting for a click. <paramref name="phaseY"/> is a row top found on the screen (client px).
    /// False when the phase does not match (the list is scrolled): then the first click anchors as before.</summary>
    public bool FromTop(ListGeometry g, double phaseY, double H, ulong listPtr) { return FromTop(g, null, phaseY, H, listPtr); }

    /// <summary>As above, for a list whose first rows may be section titles (the sub stats filter groups the items: «Полное
    /// соотв.», «Частичное», «Несовпадение»): the bars belong to the first item row, below them.</summary>
    public bool FromTop(ListGeometry g, int[] types, double phaseY, double H, ulong listPtr) {
      double pitch = g.PitchY * H, top = TopRow1(g, H);
      int first = 1;
      if (types != null) { first = 0; for (int r = 1; r < types.Length; r++) if (types[r] == 1) { first = r; break; } if (first == 0) return false; }
      double itemTop = top + g.RowOffset(types, first) * H;
      double d = (phaseY - itemTop) % pitch; if (d < 0) d += pitch; if (d > pitch / 2) d -= pitch;
      // (tight: the game does not always open a list at its top - it may keep the scroll of the last slot's list - and a
      // looser match took a list scrolled by some rows and 18 px for the top, live 2026-10-14)
      if (Math.Abs(d) > Math.Max(3.5, pitch * 0.06)) return false;
      Row1Top = top + d; Has = true; ListPtr = listPtr;
      return true;
    }

    /// <summary>Follows the scrolling: <paramref name="phaseY"/> is a row-top position found on the screen (client px),
    /// <paramref name="refRow"/> an item row that is visible (so it has the same phase). False if the move is too big to trust.</summary>
    public bool Track(ListGeometry g, int[] types, int refRow, double phaseY, double H) {
      if (!Has) return false;
      double pitch = g.PitchY * H;
      double pred = Row1Top + g.RowOffset(types, refRow) * H;
      double snapped = ListTracker.Snap(pred, phaseY, pitch);
      Row1Top += snapped - pred;
      return true;
    }

    public double RowTop(ListGeometry g, int[] types, int row, double H) { return Row1Top + g.RowOffset(types, row) * H; }

    /// <summary>An item row near the middle of the visible list (to take the phase from), 0 = none.</summary>
    public int VisibleRow(ListGeometry g, int[] types, double H) {
      double mid = (g.ViewTop + g.ViewBottom) / 2 * H;
      int best = 0; double bd = double.MaxValue;
      for (int r = 1; r < types.Length; r++) {
        if (types[r] != 1 && types[r] != 0) continue;
        double d = Math.Abs(RowTop(g, types, r, H) + g.PitchY * H / 2 - mid);
        if (d < bd) { bd = d; best = r; }
      }
      return best;
    }
  }
}
