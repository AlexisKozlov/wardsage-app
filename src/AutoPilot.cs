// RealmForge — the app finds the item to put on by itself: opens the gear slot, scrolls the list, clicks the item.
// «Заменить» (equip) is never pressed: the player decides.
//
// Pure decision logic, no Windows APIs (tests: tests/CoreTests.cs). The host (app/Overlay.cs) tells it every tick
// what is on the screen and performs the action it returns (a click or wheel notches at a client-area point).
//
// Scrolling: how far one wheel notch moves the list is not known in advance (the game's ScrollRect), so after every
// wheel batch the pilot clicks an item in the middle of the visible list: the game then reports which item that is,
// which re-anchors the list position exactly (ScrollAnchor.FromClick), and the move per notch is measured from it.
// The next batch then goes straight to the target. Clicking an item only selects it in the game's list.
using System;

namespace RealmForge {
  public enum AutoKind { None, Click, Wheel }

  public sealed class AutoAction {
    public AutoKind Kind;
    public double X, Y;       // client px: where the cursor goes
    public int Notches;       // Wheel: > 0 = scroll down (later rows come into view), < 0 = up
    public static readonly AutoAction Nothing = new AutoAction();
  }

  /// <summary>What the host sees this tick.</summary>
  public sealed class AutoView {
    public long NowMs;
    public bool Foreground;        // the game window itself is in front
    public bool UserBusy;          // the player is moving the mouse or holds a button
    public bool UserClicked;       // the player clicked since the last tick (not the pilot)
    public double H;               // client height
    public int[] Slot;             // gear slot to open, {x, y, w, h} client px; null = none
    public long Target;            // item to select; 0 = none
    public int Row, Col;           // its place in the list shown now (0 = not in it)
    public long Sel;               // item selected in the game
    public bool AnchorHas;         // the list position on the screen is known
    public double Row1Top;         // client px of row 1's top
    public int[] Types;            // list row kinds (ListGeometry)
  }

  public sealed class AutoPilot {
    public const int SlotTries = 3, ClickTries = 5, WheelTries = 12, CalibTries = 4;
    public const int AfterSlotMs = 1300, AfterClickMs = 800, AfterWheelMs = 900, UserPauseMs = 4000;
    public const int MaxNotches = 40, FirstNotches = 3;

    readonly ListGeometry g;
    string goal = "";
    long waitUntil, pauseUntil;
    int clicks, wheels, calibs, stuck, calibMiss;
    bool needCalib, measure, calibPending;
    long selAtCalib;
    double beforeWheel; int lastNotches;
    /// <summary>List move per wheel notch in client px (0 = not measured yet); kept for the next items.</summary>
    public double PxPerNotch;
    /// <summary>idle | work | done | paused | background | failed</summary>
    public string State = "idle";

    public AutoPilot(ListGeometry g) { this.g = g; }

    public AutoAction Step(AutoView v) {
      string key = v.Slot != null ? "slot:" + v.Slot[0] + ":" + v.Slot[1] : v.Target > 0 && v.Row > 0 ? "item:" + v.Target : "";
      if (key != goal) { goal = key; waitUntil = 0; clicks = wheels = calibs = stuck = calibMiss = 0; needCalib = measure = calibPending = false; State = "idle"; }
      if (key == "") { State = "idle"; return AutoAction.Nothing; }
      if (v.Slot == null && v.Sel == v.Target) { State = "done"; return AutoAction.Nothing; }
      if (State == "failed") return AutoAction.Nothing;
      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;       // the player acts himself: step back for a while
      if (!v.Foreground) { State = "background"; return AutoAction.Nothing; }
      if (v.UserBusy || v.NowMs < pauseUntil) { State = "paused"; return AutoAction.Nothing; }
      State = "work";
      if (v.NowMs < waitUntil) return AutoAction.Nothing;
      return v.Slot != null ? SlotStep(v) : ItemStep(v);
    }

    AutoAction SlotStep(AutoView v) {
      if (clicks >= SlotTries) return Fail();
      clicks++; waitUntil = v.NowMs + AfterSlotMs;
      return Click(v.Slot[0] + v.Slot[2] / 2.0, v.Slot[1] + v.Slot[3] / 2.0);
    }

    AutoAction ItemStep(AutoView v) {
      double H = v.H, cell = g.CellW * H, pitch = g.PitchY * H;
      double viewTop = g.ViewTop * H, viewBottom = g.ViewBottom * H, mid = (viewTop + viewBottom) / 2;
      double col1 = g.ColLeft(1) * H + cell / 2;

      // the re-anchoring click selected nothing new (a gap, a title row, the same item): once more, a row further
      if (calibPending) {
        calibPending = false;
        if (v.Sel == selAtCalib && calibMiss < 3) { calibMiss++; needCalib = true; }
        else calibMiss = 0;
      }
      // no idea where the rows are: click an item in the middle, the game says which one it is
      if (!v.AnchorHas || needCalib) {
        if (calibs >= CalibTries + 3 * WheelTries) return Fail();
        calibs++; needCalib = false; calibPending = true; selAtCalib = v.Sel; waitUntil = v.NowMs + AfterClickMs;
        // where the list should be now: moved by the measured step since the wheel, else as last known
        double est = measure && PxPerNotch > 1 ? beforeWheel - lastNotches * PxPerNotch : v.AnchorHas ? v.Row1Top : double.NaN;
        double shift = (calibMiss % 3 == 1 ? 1 : calibMiss % 3 == 2 ? -1 : 0) * pitch;
        return Click(col1, double.IsNaN(est) || v.Types == null ? mid + shift * 0.3 : NearestCellCentre(est, v.Types, H, mid + shift));
      }
      // after a wheel batch and the re-anchoring click: how far did one notch move the list?
      if (measure) {
        measure = false;
        double moved = beforeWheel - v.Row1Top;                      // > 0 when the list went down
        if (Math.Abs(moved) > 3 && Math.Sign(moved) == Math.Sign(lastNotches)) { PxPerNotch = Math.Abs(moved / lastNotches); stuck = 0; }
        else if (++stuck >= 2) return Fail();                         // the list does not move (its end, or no wheel)
      }

      double top = v.Row1Top + g.RowOffset(v.Types, v.Row) * H, cy = top + cell / 2, cx = g.ColLeft(v.Col) * H + cell / 2;
      if (cy >= viewTop + cell * 0.45 && cy <= viewBottom - cell * 0.45) {
        if (clicks >= ClickTries) return Fail();
        clicks++; waitUntil = v.NowMs + AfterClickMs;
        return Click(cx, cy);
      }
      if (wheels >= WheelTries) return Fail();
      double dist = cy - mid;                                          // > 0: the item is below
      int n = PxPerNotch > 1 ? (int)Math.Round(dist / PxPerNotch) : Math.Sign(dist) * FirstNotches;
      if (n == 0) n = Math.Sign(dist);
      n = Math.Max(-MaxNotches, Math.Min(MaxNotches, n));
      wheels++; beforeWheel = v.Row1Top; lastNotches = n; needCalib = true; measure = true;
      waitUntil = v.NowMs + AfterWheelMs;
      return new AutoAction { Kind = AutoKind.Wheel, X = (g.StripLeft + g.StripRight) / 2 * H, Y = mid, Notches = n };
    }

    /// <summary>Centre (client px) of the item cell whose centre is closest to <paramref name="y"/>, with row 1 at <paramref name="row1"/>.</summary>
    double NearestCellCentre(double row1, int[] types, double H, double y) {
      double best = y, bd = double.MaxValue, half = g.CellW * H / 2;
      for (int r = 1; r < types.Length; r++) {
        if (types[r] != 1 && types[r] != 0) continue;
        double c = row1 + g.RowOffset(types, r) * H + half, d = Math.Abs(c - y);
        if (d < bd) { bd = d; best = c; }
      }
      return best;
    }

    AutoAction Fail() { State = "failed"; return AutoAction.Nothing; }

    static AutoAction Click(double x, double y) { return new AutoAction { Kind = AutoKind.Click, X = x, Y = y }; }
  }
}
