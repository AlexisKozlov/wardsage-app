// RealmForge.exe — where the game's own buttons are on the hero's gear screen (for the hints over the game).
//
// Fractions of the game client height, measured on the live game (1456×764 client): the gear screen scales with the
// window height; the filter and «Заменить» are anchored to the left edge, the five slots to the window centre.
namespace RealmForge {
  public sealed class HintGeometry {
    public double FilterX0 = 0.029, FilterX1 = 0.408, FilterY0 = 0.899, FilterY1 = 0.956;      // «Фильтр» bar under the list
    public double ReplaceX0 = 0.668, ReplaceX1 = 0.866, ReplaceY0 = 0.806, ReplaceY1 = 0.86;   // «Заменить» of the item card
    public double SlotSize = 0.1007;                   // slot square
    public double SlotLeftDx = -0.356, SlotRightDx = 0.264;   // slot centre from the window centre (slots 0–1 left, 2–4 right)
    public double[] SlotY = { 0.497, 0.63, 0.357, 0.494, 0.631 };   // slot top

    /// <summary>Slot 0–4 as {x, y, w, h} in client pixels; null for another slot.</summary>
    public int[] Slot(int slot, double W, double H) {
      if (slot < 0 || slot > 4) return null;
      double s = SlotSize * H, cx = W / 2 + (slot < 2 ? SlotLeftDx : SlotRightDx) * H;
      return new[] { (int)(cx - s / 2), (int)(SlotY[slot] * H), (int)s, (int)s };
    }

    public int[] Filter(double H) { return new[] { (int)(FilterX0 * H), (int)(FilterY0 * H), (int)((FilterX1 - FilterX0) * H), (int)((FilterY1 - FilterY0) * H) }; }

    public int[] Replace(double H) { return new[] { (int)(ReplaceX0 * H), (int)(ReplaceY0 * H), (int)((ReplaceX1 - ReplaceX0) * H), (int)((ReplaceY1 - ReplaceY0) * H) }; }
  }
}
