// RealmForge.exe — where the game's own buttons are on the hero's gear screen (for the hints over the game).
//
// Fractions of the game client height, measured on the live game (1456×764 client): the gear screen scales with the
// window height; the filter and «Заменить» are anchored to the left edge, the five slots to the window centre.
namespace RealmForge {
  public sealed class HintGeometry {
    public double FilterX0 = 0.029, FilterX1 = 0.408, FilterY0 = 0.899, FilterY1 = 0.956;      // «Фильтр» bar under the list
    public double ReplaceX0 = 0.668, ReplaceX1 = 0.866, ReplaceY0 = 0.806, ReplaceY1 = 0.86;   // «Заменить» of the item card
    // «Надеть» of the single item card (the hero wears nothing in that slot): the card is centred in the window
    // (measured on a 1920×1009 client)
    public double EquipDx0 = 0.015, EquipDx1 = 0.206, EquipY0 = 0.8355, EquipY1 = 0.89;
    public double SlotSize = 0.1007;                   // slot square
    public double SlotLeftDx = -0.356, SlotRightDx = 0.264;   // slot centre from the window centre (slots 0–1 left, 2–4 right)
    public double[] SlotY = { 0.497, 0.63, 0.357, 0.494, 0.631 };   // slot top

    /// <summary>Slot 0–4 as {x, y, w, h} in client pixels; null for another slot.</summary>
    // (sizes in the UI unit Ui.Unit: the window height, or less on windows narrower than 16:9)
    public int[] Slot(int slot, double W, double H) {
      if (slot < 0 || slot > 4) return null;
      double U = Ui.Unit(W, H), s = SlotSize * U, cx = W / 2 + (slot < 2 ? SlotLeftDx : SlotRightDx) * U;   // window centre
      return new[] { (int)(cx - s / 2), (int)Ui.FromMiddle(SlotY[slot], W, H), (int)s, (int)s };
    }

    public int[] Filter(double W, double H) {   // left edge, bottom
      double U = Ui.Unit(W, H);
      return new[] { (int)(FilterX0 * U), (int)Ui.FromBottom(FilterY0, W, H), (int)((FilterX1 - FilterX0) * U), (int)((FilterY1 - FilterY0) * U) };
    }

    public int[] Equip(double W, double H) {   // the single card is centred in the window (x and y; 1600×1000 checked)
      double U = Ui.Unit(W, H);
      return new[] { (int)(W / 2 + EquipDx0 * U), (int)Ui.FromMiddle(EquipY0, W, H), (int)((EquipDx1 - EquipDx0) * U), (int)((EquipY1 - EquipY0) * U) };
    }

    public int[] Replace(double W, double H) {   // left edge, top
      double U = Ui.Unit(W, H);
      return new[] { (int)(ReplaceX0 * U), (int)(ReplaceY0 * U), (int)((ReplaceX1 - ReplaceX0) * U), (int)((ReplaceY1 - ReplaceY0) * U) };
    }
  }
  /// <summary>Our own clickable buttons over the game: the TEST build's record button on the game's left edge at mid-height
  /// (d = 0.034 U, flush with the edge, so it ends left of the gear list's first column at 0.0393 U) and the cancel plate. The
  /// first place (top left) lay on the game's back arrow (0.0515 U, 0.0367 U): a pilot click there pressed our button instead of
  /// the game's (live 2026-10-29, Locate's HeroBack started a recording and stopped on «nochange»). The left edge at mid-height
  /// is clear of the back arrow, the filter / funnel (bottom), the lists, the cards, the slots and the centred panels; a pilot
  /// click that does fall on one of the buttons still lets it pass the mouse through (app/Overlay.cs Send).</summary>
  public static class OwnButtons {
    public static int RecDiameter(double U) { return System.Math.Max(16, (int)(0.034 * U)); }
    /// <summary>The idle record button in client px {x, y, w, h} (its bitmap: d + 3 px of shadow), centred on the left edge at mid-height.</summary>
    public static int[] RecBox(double W, double H) { int d = RecDiameter(Ui.Unit(W, H)); return new[] { 0, (int)(H / 2 - (d + 3) / 2.0), d + 3, d + 3 }; }
    /// <summary>A click (or a drag from y to y2) at x touches one of these parts {x, y, w, h}.</summary>
    public static bool Covers(System.Collections.Generic.IList<int[]> parts, int x, int y, int y2) {
      if (parts == null) return false;
      int top = System.Math.Min(y, y2), bottom = System.Math.Max(y, y2);
      foreach (var p in parts)
        if (p != null && p.Length >= 4 && x >= p[0] && x < p[0] + p[2] && bottom >= p[1] && top < p[1] + p[3]) return true;
      return false;
    }
    /// <summary>Both ends of a click / drag (x, y..y2) lie inside the rect {x, y, w, h} (the desktop: a game window larger than the
    /// screen has client points off it, and the cursor would be clamped to the screen's edge, onto something else).</summary>
    public static bool AllInside(int[] r, int x, int y, int y2) {
      if (r == null || r.Length < 4) return false;
      return x >= r[0] && x < r[0] + r[2] && y >= r[1] && y < r[1] + r[3] && y2 >= r[1] && y2 < r[1] + r[3];
    }
  }
}
