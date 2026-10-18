using System;

namespace RealmForge {
  /// <summary>Where the rows of the artifact list are on the screen, from the picture alone: the fallback when the game's own scroll of the list cannot be
  /// read (the exact reading in SellScan.ReadArtifactList is the first choice and is always used when it works). The same method as the hero list's: the cards'
  /// frame's side lines give the rows' phase (CardPhase), a list that was just built is at its top (anchor), and the list's movement since is followed by
  /// matching the strip's picture (ScrollFollow). Pure: the host hands over a screen grab of the list's strip. Tests: tests/ArtPicReplay.cs.</summary>
  public sealed class ArtGridPicture {
    readonly ScrollAnchor anchor = new ScrollAnchor();
    readonly ScrollFollow follow = new ScrollFollow();
    ulong ptr; long freshMs; bool freshTop;
    /// <summary>How the last look went (the app's log): the phase, the anchor, why it is not sure.</summary>
    public string Note = "";
    public bool HasPhase; public double Phase, Conf;   // client px

    const int SigSegs = 4;

    /// <summary>The strip the picture is taken from: the list's viewport over its columns (client px).</summary>
    public static bool Strip(SellGeometry g, double W, double H, double U, out int x, out int y, out int w, out int h) {
      x = (int)Math.Round(g.X(g.X0, W, U)); y = (int)Math.Round(g.ViewTopPx(U));
      w = (int)Math.Round(g.X(g.X0 + (g.Columns - 1) * g.PitchX + g.CellW, W, U)) - x; h = (int)Math.Round(g.ViewBottomPx(H, U)) - y;
      return x >= 0 && w > 20 && x + w <= W && h > 100;
    }

    /// <summary>The list as the exact reading would give it, from row 1's place (the same ListRead the frames of a card are placed from: LocateFrames.ArtFrames.Cell).</summary>
    public static RFX.ListRead ToListRead(SellGeometry g, double U, double row1) {
      double k = U / GameGeometry.CanvasH;
      return new RFX.ListRead { ScrollY = (g.ViewTopPx(U) + g.TopPad * U - row1) / k, Cols = g.Columns, RowUnits = g.PitchY * GameGeometry.CanvasH };
    }

    public void Reset() { anchor.Has = false; follow.Reset(); ptr = 0; freshTop = false; Note = ""; HasPhase = false; }

    /// <summary>A pixel of a card rather than of the list's background: the background between the rows is dark and bluish-grey (blue not below red), a card is red / gold
    /// or has a bright frame and art. Measured on the live list (1920 x 1009): the gap between two rows is 0 on this test along the whole row, a card line is 0.2 - 1.0.</summary>
    static bool IsCard(int p) { int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255; int mx = Math.Max(r, Math.Max(g, b)); return r >= b + 12 || mx >= 110 || mx - Math.Min(r, Math.Min(g, b)) >= 40; }

    /// <summary>The rows' phase from the cards' top edges: every card starts with a bright crest line right after the dark gap between the rows (a gap of 6 canvas units),
    /// the one feature all the cards share (their art, and the notch under the crest, differ). prof = share of card pixels on every line; the score of a phase p is the
    /// mean over the rows of (the profile in the 4 lines from p) minus (the gap above it); the middle of the best plateau wins. conf = that score (0.8 and more on a live list).</summary>
    public static bool EdgePhase(double[] prof, double pitch, double gap, out double phase, out double conf) {
      phase = 0; conf = 0;
      int h = prof.Length, steps = (int)Math.Ceiling(pitch), g0 = (int)Math.Round(gap), a = 4;
      if (pitch < 20 || h < pitch * 1.5 || g0 < 3) return false;
      var sc = new double[steps]; double best = double.MinValue;
      for (int p = 0; p < steps; p++) {
        double s = 0; int n = 0;
        for (double y0 = p; y0 + a < h; y0 += pitch) {
          int y = (int)Math.Round(y0);
          if (y - g0 - 1 < 0 || y + a >= h) continue;
          double A = 0, B = 0;
          for (int i = 1; i <= a; i++) A += prof[y + i];
          for (int i = 2; i <= g0 - 1; i++) B += prof[y - i];
          s += A / a - B / Math.Max(1, g0 - 2); n++;
        }
        sc[p] = n > 0 ? s / n : double.MinValue;
        if (sc[p] > best) best = sc[p];
      }
      if (best < 0.4) { conf = best; return false; }
      // the middle of the plateau (phases within 5% of the best), circular
      int first = -1, len = 0, bestFirst = 0, bestLen = 0;
      for (int k = 0; k < 2 * steps; k++) {
        if (sc[k % steps] >= best - 0.05) { if (first < 0) first = k; len++; if (len > bestLen) { bestLen = len; bestFirst = first; } } else { first = -1; len = 0; }
      }
      phase = (bestFirst + (bestLen - 1) / 2.0) % steps; conf = best;
      return true;
    }

    /// <summary>One look. <paramref name="px"/>: the strip (w x h ARGB, row by row) whose top left is at client (<paramref name="stripX"/>, <paramref name="stripY"/>) -
    /// the list's viewport over its columns. <paramref name="rowsN"/>: rows of the list; <paramref name="listPtr"/>: the list's identity (a new one is at its
    /// top). <paramref name="clickRow"/> / <paramref name="cursorY"/>: the player just ticked / chose the card of this 0-based row with the pointer at that client y (-1 = no
    /// click): that tells where the list is when the picture has lost it (the card is under the pointer, the rows' phase fixes the rest).
    /// Returns true with row 1's top in client px when the rows are placed.</summary>
    public bool Update(int[] px, int w, int h, int stripX, int stripY, SellGeometry g, double W, double U, int rowsN, ulong listPtr, long now, out double row1, int clickRow = -1, double cursorY = double.NaN) {
      row1 = double.NaN;
      if (px == null || w < 20 || h < 40 || g == null) { Note = "no picture"; return false; }
      double pitch = g.PitchY * U, card = g.CellHeight * U, cardW = g.CellW * U;
      double top0 = g.ViewTopPx(U) + g.TopPad * U, vb = stripY + h;
      int cols = g.Columns;
      var xs = new int[cols * 2];
      var prof = new double[h];
      for (int c = 0; c < cols; c++) {
        double left = g.X(g.X0 + c * g.PitchX, W, U) - stripX;
        xs[2 * c] = (int)(left + cardW * 0.12); xs[2 * c + 1] = (int)(left + cardW * 0.88);
      }
      for (int y = 0; y < h; y++) {
        int n = 0, k = 0;
        for (int c = 0; c < cols; c++)
          for (int x = Math.Max(0, xs[2 * c]); x < Math.Min(w, xs[2 * c + 1]); x += 2) { n++; if (IsCard(px[y * w + x])) k++; }
        prof[y] = n > 0 ? k / (double)n : 0;
      }
      double ph, conf;
      HasPhase = EdgePhase(prof, pitch, (g.PitchY - g.CellHeight) * U, out ph, out conf);
      Phase = stripY + ph; Conf = conf;
      float[] sig = ListTracker.LineSig(px, w, h, xs, SigSegs);
      int n4 = cols * SigSegs;
      if (listPtr != ptr) { anchor.Has = false; follow.Reset(); freshMs = now; freshTop = true; ptr = listPtr; }
      // at the top: a fresh list (a tight phase match only) or one that fits on the screen
      if (!anchor.Has && HasPhase) {
        double d = ((Phase - top0) % pitch + pitch) % pitch; if (d > pitch / 2) d -= pitch;
        bool fits = top0 + rowsN * pitch <= vb + (pitch - card);
        if ((fits || (freshTop && now - freshMs < 1500)) && Math.Abs(d) <= Math.Max(3.5, 0.06 * pitch)) {
          anchor.Row1Top = top0 + d; anchor.Has = true; follow.Reset(); follow.Anchored(sig, now);
        }
      }
      bool sure = anchor.Has && follow.Follow(anchor, sig, n4, pitch, now);
      // the player's click: the card is under the pointer (its top within a card height above it); the nearest row top with the measured phase
      if (!sure && clickRow >= 0 && HasPhase && !double.IsNaN(cursorY) && cursorY >= stripY && cursorY <= vb) {
        anchor.Row1Top = ListTracker.Snap(cursorY - card / 2 - clickRow * pitch, Phase, pitch); anchor.Has = true; follow.Reset(); follow.Anchored(sig, now);
        sure = true; follow.Note = "click";
      }
      if (sure && HasPhase) anchor.Row1Top = ListTracker.Snap(anchor.Row1Top - stripY, ph, pitch) + stripY;
      if (sure) {
        double end = Math.Min(top0, vb - rowsN * pitch), slack = 0.5 * h;
        if (anchor.Row1Top > top0 + slack || anchor.Row1Top < end - slack) { anchor.Has = false; sure = false; }
      }
      if (sure) follow.Settled(anchor, sig, top0, pitch);
      Note = "pixels phase=" + (HasPhase ? Phase.ToString("0") : "none") + "/" + conf.ToString("0.00") + " row1=" + (anchor.Has ? anchor.Row1Top.ToString("0") : "-") + " sure=" + sure + " follow=" + follow.Note;
      if (!sure) return false;
      row1 = anchor.Row1Top;
      return true;
    }
  }
}
