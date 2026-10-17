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
}
