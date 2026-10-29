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
  /// <summary>Which blue confirm button to press when both searches see one («Заменить» at its left-anchored place and «Надеть»
  /// at the centre). On a 3440×1440 (21:9) client both were seen on every replace for one player (27-29.10, 18 reports
  /// pilot.confirm_no_button "replace True, equip True"), while every press that worked there was the centred one: the hint then
  /// fell back to the usual «Заменить» place, higher than the real button. The kind whose single press put the item on the hero
  /// is kept per client size and decides the tie; with nothing learned yet a tie still presses nothing.</summary>
  public sealed class ConfirmChoice {
    readonly System.Collections.Generic.Dictionary<string, bool> learned = new System.Collections.Generic.Dictionary<string, bool>();   // size -> true = «Надеть» place
    long pendingItem; bool pendingEquip; string pendingSize;

    public static string SizeKey(double W, double H) { return (int)W + "x" + (int)H; }

    /// <summary>The button to use, or null. A single one is noted as pending for the item; a tie uses what worked at this size.</summary>
    public int[] Pick(int[] rep, int[] eq, long item, double W, double H) {
      string size = SizeKey(W, H);
      if ((rep != null) != (eq != null)) {
        if (item > 0) { pendingItem = item; pendingEquip = eq != null; pendingSize = size; }
        return rep ?? eq;
      }
      bool useEquip;
      if (rep == null || !learned.TryGetValue(size, out useEquip)) return null;
      return useEquip ? eq : rep;
    }

    /// <summary>The pending item is now worn by the hero: its button kind is the one that works at that size.</summary>
    public void Worn(long item) {
      if (item <= 0 || item != pendingItem || pendingSize == null) return;
      learned[pendingSize] = pendingEquip;
      pendingItem = 0;
    }

    public long PendingItem { get { return pendingItem; } }
    /// <summary>The learned kind at a size: "equip", "replace" or "" (nothing yet).</summary>
    public string Learned(double W, double H) { bool e; return learned.TryGetValue(SizeKey(W, H), out e) ? (e ? "equip" : "replace") : ""; }
  }

  /// <summary>The game window's geometry self-check (journal line and pilot report field "geom"): the client origin by
  /// ClientToScreen and by the window rect (frame borders taken as equal left/right/bottom, the caption on top), the client
  /// size, the window's DPI and the Wine version (none on Windows). Under Wine / CrossOver the two origins or the DPI can
  /// differ from Windows; a hint drawn off the game's button is then visible in the line.</summary>
  public static class GameGeomCheck {
    /// <summary>The client origin from the window rect {L, T, R, B} and the client size: the side border = half the width
    /// difference, the client's bottom = the window's bottom minus that border.</summary>
    public static void OriginFromWindow(int l, int t, int r, int b, int cw, int ch, out int x, out int y) {
      int border = System.Math.Max(0, ((r - l) - cw) / 2);
      x = l + border;
      y = System.Math.Max(t, b - border - ch);
    }

    public static string Line(int ox, int oy, int l, int t, int r, int b, int cw, int ch, int dpi, string wine) {
      int x2, y2; OriginFromWindow(l, t, r, b, cw, ch, out x2, out y2);
      return "client " + cw + "x" + ch + " at " + ox + "," + oy + " (window " + l + "," + t + " " + (r - l) + "x" + (b - t)
        + " -> " + x2 + "," + y2 + (x2 != ox || y2 != oy ? " DIFFERS " + (x2 - ox) + "," + (y2 - oy) : " same") + ")"
        + " dpi " + dpi + " wine " + (string.IsNullOrEmpty(wine) ? "no" : wine);
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
