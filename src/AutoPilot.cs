// RealmForge — the app finds the item to put on by itself: opens the gear slot, scrolls the list, clicks the item.
// «Заменить» (equip) is pressed only with Settings → «Автоматически подтверждать замену» (off by default), once per item,
// and only when the game's memory says the right item is selected on the plan's hero screen and that hero does not wear
// it yet (on a worn item the same place of the card is «Снять»). Otherwise the player presses it.
//
// Pure decision logic, no Windows APIs (tests: tests/CoreTests.cs). The host (app/Overlay.cs) tells it every tick
// what is on the screen and performs the action it returns (a click or a drag at a client-area point).
//
// Scrolling: by dragging the list, as a player does. The mouse wheel is useless here: the game's list is a Unity
// ScrollRect with m_ScrollSensitivity 1 (Panel_Equip_Filter.unity3d), about 1 px per notch. A drag moves the content
// with the pointer (gain ~1, measured anyway); the pointer rests before the release so the list does not coast
// (inertia). After every drag the pilot clicks an item in the middle of the visible list: the game then reports which
// item that is, which re-anchors the list position exactly (ScrollAnchor.FromClick). Clicking an item only selects it.
using System;

namespace RealmForge {
  public enum AutoKind { None, Click, Drag }

  public sealed class AutoAction {
    public AutoKind Kind;
    public double X, Y;       // client px: where the cursor goes (Drag: where the button goes down)
    public double DY;         // Drag: pointer move in client px; < 0 = up (the content follows: later rows come into view)
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
    // auto-confirm: the setting is on and the guide shows the right item selected; the rest is read this tick
    public bool AutoConfirm;
    public int[] Button;           // the confirm button seen on the screen now, {x, y, w, h} client px: «Заменить» (two
                                   // cards) or «Надеть» (one card: the slot is empty); null = none seen (yet)
    public long Hero;              // the plan's hero (0 = unknown)
    public long ScreenHero;        // hero whose gear screen the game shows
    public long Owner = -1;        // hero wearing Target now: 0 = in the bag, -1 = unknown
  }

  public sealed class AutoPilot {
    public const int SlotTries = 3, ClickTries = 5, DragTries = 60, CalibTries = 4;
    public const int AfterSlotMs = 900, AfterClickMs = 500, AfterDragMs = 800, UserPauseMs = 4000;
    public const int ConfirmSettleMs = 600, AfterConfirmMs = 3000;   // item card opening; the server's answer to «Заменить»
    public const double MinDragPx = 24;   // shorter moves would count as a click in the game

    readonly ListGeometry g;
    string goal = "";
    long waitUntil, pauseUntil;
    int clicks, drags, calibs, stuck, calibMiss;
    bool needCalib, measure, calibPending;
    long selAtCalib;
    double beforeDrag, lastDrag;
    long selSince; bool confirmed;
    /// <summary>List move per px of pointer drag (0 = not measured yet, 1 assumed); kept for the next items.</summary>
    public double Gain;
    /// <summary>idle | work | done | paused | background | failed | slot_failed (the slot does not open) | unconfirmed
    /// («Заменить» pressed, the item did not go on)</summary>
    public string State = "idle";

    public AutoPilot(ListGeometry g) { this.g = g; }

    /// <summary>Start over on the next step (the list was changed under it: the filter).</summary>
    public void Reset() { goal = "\u0001"; }

    public AutoAction Step(AutoView v) {
      string key = v.Slot != null ? "slot:" + v.Slot[0] + ":" + v.Slot[1] : v.Target > 0 && v.Row > 0 ? "item:" + v.Target : "";
      if (key != goal) {
        goal = key; waitUntil = 0; clicks = drags = calibs = stuck = calibMiss = 0; needCalib = measure = calibPending = false;
        selSince = 0; confirmed = false; State = "idle";
      }
      if (key == "") { State = "idle"; return AutoAction.Nothing; }
      bool selected = v.Slot == null && v.Sel == v.Target;
      if (!selected) selSince = 0; else if (selSince == 0) selSince = v.NowMs;
      // selected: the player presses «Заменить», or it is pressed below and the item is on the hero now
      if (selected && (!v.AutoConfirm || (v.Hero > 0 && v.Owner == v.Hero))) { State = "done"; return AutoAction.Nothing; }
      if (State == "failed" || State == "unconfirmed" || State == "slot_failed") return AutoAction.Nothing;
      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;       // the player acts himself: step back for a while
      if (!v.Foreground) { State = "background"; return AutoAction.Nothing; }
      if (v.UserBusy || v.NowMs < pauseUntil) { State = "paused"; return AutoAction.Nothing; }
      State = "work";
      if (v.NowMs < waitUntil) return AutoAction.Nothing;
      if (selected) return ConfirmStep(v);
      return v.Slot != null ? SlotStep(v) : ItemStep(v);
    }

    // «Заменить» once: the game shows the plan's hero and the item is not on that hero yet (else the button there is
    // «Снять»). When the item has not gone on after the server's time, stop - the player looks at the game.
    AutoAction ConfirmStep(AutoView v) {
      if (confirmed) { State = "unconfirmed"; return AutoAction.Nothing; }
      if (v.Hero <= 0 || v.ScreenHero != v.Hero || v.Owner < 0) return AutoAction.Nothing;   // not sure yet: wait
      if (v.Button == null || v.NowMs - selSince < ConfirmSettleMs) return AutoAction.Nothing;  // the item card is opening
      confirmed = true; waitUntil = v.NowMs + AfterConfirmMs;
      return Click(v.Button[0] + v.Button[2] / 2.0, v.Button[1] + v.Button[3] / 2.0);
    }

    AutoAction SlotStep(AutoView v) {
      // the slot does not open: the game keeps bracer / amulet / ring closed until a story stage (EquipData:IsSlotUnLock)
      if (clicks >= SlotTries) { State = "slot_failed"; return AutoAction.Nothing; }
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
        if (calibs >= CalibTries + 3 * DragTries) return Fail();
        calibs++; needCalib = false; calibPending = true; selAtCalib = v.Sel; waitUntil = v.NowMs + AfterClickMs;
        // where the list should be now: moved with the drag since the last anchor, else as last known, else at the top
        // (a short filtered list is; its middle can be a title or «nothing found» - no item to click there)
        double est = measure && v.AnchorHas ? beforeDrag + lastDrag * GainOr1 : v.AnchorHas ? v.Row1Top : ScrollAnchor.TopRow1(g, H);
        double shift = (calibMiss % 3 == 1 ? 1 : calibMiss % 3 == 2 ? -1 : 0) * pitch;
        return Click(col1, double.IsNaN(est) || v.Types == null ? mid + shift * 0.3 : NearestCellCentre(est, v.Types, H, mid + shift));
      }
      // after a drag and the re-anchoring click: how far did the list follow the pointer?
      if (measure) {
        measure = false;
        double moved = v.Row1Top - beforeDrag;                       // same sign as the drag when the list followed it
        if (Math.Abs(moved) > 3 && Math.Sign(moved) == Math.Sign(lastDrag)) { Gain = Math.Max(0.2, Math.Min(3, moved / lastDrag)); stuck = 0; }
        else if (++stuck >= 2) return Fail();                         // the list does not move (its end, or no drag)
      }

      double top = v.Row1Top + g.RowOffset(v.Types, v.Row) * H, cy = top + cell / 2, cx = g.ColLeft(v.Col) * H + cell / 2;
      if (cy >= viewTop + cell * 0.45 && cy <= viewBottom - cell * 0.45) {
        if (clicks >= ClickTries) return Fail();
        clicks++; waitUntil = v.NowMs + AfterClickMs;
        return Click(cx, cy);
      }
      if (drags >= DragTries) return Fail();
      double dist = cy - mid;                                          // > 0: the item is below: drag the list up
      double span = viewBottom - viewTop - cell;                       // the longest drag that stays over the list
      double dy = Math.Max(-span, Math.Min(span, -dist / GainOr1));
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      double y0 = dy < 0 ? viewBottom - cell / 2 : viewTop + cell / 2;   // start at the far end so the whole move fits
      drags++; beforeDrag = v.Row1Top; lastDrag = dy; needCalib = true; measure = true;
      waitUntil = v.NowMs + AfterDragMs;
      return new AutoAction { Kind = AutoKind.Drag, X = (g.StripLeft + g.StripRight) / 2 * H, Y = y0, DY = dy };
    }

    double GainOr1 { get { return Gain > 0.2 ? Gain : 1; } }

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
