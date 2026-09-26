// RealmForge — where an item of the game's gear list is on the screen (for the highlight over the game).
//
// Pure logic, no Windows APIs (tests: tests/CoreTests.cs). The screen is only LOOKED at: the host takes a
// screenshot of the list area and passes its pixels here. Nothing is sent to the game.
//
// The list scroll position is not in the game's Lua data, so it is found like this:
//   * anchor: when the player clicks an item, the game stores its uid (and we know its row from the Lua list);
//     the mouse cursor is on that item at that moment, which gives its row on the screen;
//   * tracking: every item cell has a stat bar under it (slate-blue band with the main stat); the bars repeat with
//     the row pitch, so their vertical phase on a screenshot shows how far the list has moved since the anchor.
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

    /// <summary>Moves y to the nearest position with the given phase (both in the same pixels); the move is in (-pitch/2, pitch/2].</summary>
    public static double Snap(double y, double phase, double pitch) {
      double d = (phase - y) % pitch;
      if (d > pitch / 2) d -= pitch;
      if (d <= -pitch / 2) d += pitch;
      return y + d;
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
      if (Math.Abs(d) > pitch * 0.12) return false;
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
