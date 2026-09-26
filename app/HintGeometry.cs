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
}
