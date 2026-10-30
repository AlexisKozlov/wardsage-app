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
    public long Cell;         // Click: the item whose list cell it picks (0 = none): the host clicks the cell's live rect when it is fresh
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
    public int SelRow, SelCol;     // the selected item's place in the list shown now (0 = not in it)
    public bool AnchorHas;         // the list position on the screen is known
    public double Row1Top;         // client px of row 1's top
    public int[] Types;            // list row kinds (ListGeometry)
    public double[] SideClose;     // client px of the red x of the filter's side panel when it is open (it swallows clicks on the list); null = closed
    // auto-confirm: the setting is on and the guide shows the right item selected; the rest is read this tick
    public bool AutoConfirm;
    public int[] Button;           // the confirm button seen on the screen now, {x, y, w, h} client px: «Заменить» (two
                                   // cards) or «Надеть» (one card: the slot is empty); null = none seen (yet)
    public long Hero;              // the plan's hero (0 = unknown)
    public long ScreenHero;        // hero whose gear screen the game shows
    public long Owner = -1;        // hero wearing Target now: 0 = in the bag, -1 = unknown
    public bool SlotOpen;          // the screen read shows the gear list of Slot's part open
    public bool PanelNone;         // no gear list panel exists yet (it is created when a slot is first opened): click the slot
    public bool PanelOk = true;
    /// <summary>The game's window table was read for this hero screen (the shown forms are known) and whether the item's tip
    /// (Form_CharactorEquipTip, the card with «Надеть» / «Заменить») is among them.</summary>
    public bool TipKnown, TipShown;
    /// <summary>Neither blue button search saw one (no «Заменить», no «Надеть»; not a tie of two): the card is not on the screen.</summary>
    public bool BlueNone;    // the game's panel table is readable (false while the addresses are scanned again)
  }

  public sealed class AutoPilot {
    public const int SlotTries = 3, ClickTries = 5, DragTries = 60, CalibTries = 4, WrongTries = 2, AfterConfirmClicks = 2, SlotWaits = 40;
    public const int AfterSlotMs = 900, AfterClickMs = 500, AfterDragMs = 800, UserPauseMs = 4000;
    public const int SlotGraceMs = 2500;   // the last slot click gets this long to show its effect before the slot counts as closed
    public const int ConfirmSettleMs = 600, AfterConfirmMs = 3000;
    /// <summary>The item selected with its tip known closed this long: the item is clicked again (at most TipReopens times an item).</summary>
    public const int TipReopenMs = 2500, TipReopens = 2;   // item card opening; the server's answer to «Заменить»
    public const double MinDragPx = 24;   // shorter moves would count as a click in the game

    readonly ListGeometry g;
    string goal = "";
    long waitUntil, pauseUntil;
    int clicks, drags, calibs, stuck, calibMiss;
    bool needCalib, measure, calibPending, endTaken;
    long selAtCalib;
    double beforeDrag, lastDrag;
    long selSince; bool confirmed; int confirmRetries, tipReopens;
    int lastAct; bool lastRetry;   // the last action: 1 = «Заменить», 2 = the item clicked again for its tip (NotSent undoes it)
    int slotWaits, afterClicks, wrongs; long lastSlotClick;
    // the last item click: where it went, and whether its result is still to be looked at; the row 1 top it implies
    // when it selected another item (NaN = none)
    bool itemPending; double clickX, clickY, row1Fix = double.NaN, row1FixBase;
    // what the clicks on the list taught (live 1.6.66.354: the filter shrank the list, the scroll read stayed stale, every
    // re-anchoring click was undone by the next read - 184 clicks between two rows): row 1 is this far from the host's anchor
    // (NaN = trust it); the list clicks that changed nothing for this item; the last list click point and how often in a row
    double row1Off = double.NaN, calY; int noEffect, samePt, typesLen = -1; long selBefore; double lastPtX = double.NaN, lastPtY = double.NaN; bool lastNoEffect;
    /// <summary>List clicks with no effect for one item before the pilot stops; the same point at most this often in a row.</summary>
    public const int NoEffectTries = 3, SamePointMax = 2;
    /// <summary>What the last step did (the app's log), empty when nothing new.</summary>
    public string Trace = "";
    /// <summary>Why the pilot gave up (failed / slot_failed / unconfirmed), for the error report.</summary>
    public string Why = "";
    /// <summary>Clicks and drags spent on the current goal.</summary>
    public int Clicks { get { return clicks; } }
    public int Drags { get { return drags; } }
    /// <summary>How many times «Заменить» is pressed again for one item when the first press left it off the hero.</summary>
    public const int ConfirmRetries = 1;
    /// <summary>List move per px of pointer drag (0 = not measured yet, 1 assumed); kept for the next items.</summary>
    public double Gain;
    /// <summary>idle | work | done | paused | background | failed | slot_failed (the slot does not open) | unconfirmed
    /// («Заменить» pressed, the item did not go on)</summary>
    public string State = "idle";

    public AutoPilot(ListGeometry g) { this.g = g; }

    /// <summary>Start over on the next step (the list was changed under it: the filter).</summary>
    public void Reset() { goal = "\u0001"; }

    /// <summary>The host did not send the last action (the click gate's wait, the shown forms unread): a «Заменить» press or a click for the
    /// item's tip is not counted (live 1.6.66.348 sim: the press that met the tip's first read was taken for made, the retry met a failed read - unconfirmed).</summary>
    public void NotSent() {
      if (lastAct == 1) { confirmed = lastRetry; if (lastRetry) confirmRetries--; waitUntil = 0; }
      else if (lastAct == 2) { tipReopens--; waitUntil = 0; calibPending = false; }
      // a list click not sent: its result is not waited for, its point was not clicked
      if (listSent) { listSent = false; itemPending = calibPending = false; if (samePt > 0) samePt--; if (samePt == 0) lastPtX = lastPtY = double.NaN; waitUntil = 0; }
      lastAct = 0;
    }

    public AutoAction Step(AutoView v) {
      lastAct = 0; listSent = false;
      string key = v.Slot != null ? "slot:" + v.Slot[0] + ":" + v.Slot[1] : v.Target > 0 && v.Row > 0 ? "item:" + v.Target : "";
      if (key != goal) {
        goal = key; waitUntil = 0; clicks = drags = calibs = stuck = calibMiss = 0; needCalib = measure = calibPending = endTaken = false;
        selSince = 0; confirmed = false; confirmRetries = 0; tipReopens = 0; lastRetry = false; State = "idle"; Why = "";
        slotWaits = afterClicks = wrongs = 0; lastSlotClick = 0; itemPending = false; row1Fix = double.NaN;
        row1Off = double.NaN; noEffect = samePt = 0; typesLen = -1; lastPtX = lastPtY = double.NaN; lastNoEffect = false;
      }
      if (key == "") { State = "idle"; return AutoAction.Nothing; }
      curSel = v.Sel; nowMs = v.NowMs;
      bool selected = v.Slot == null && v.Sel == v.Target;
      if (!selected) selSince = 0; else if (selSince == 0) selSince = v.NowMs;
      if (selected) itemPending = false;
      // selected: the player presses «Заменить», or it is pressed below and the item is on the hero now
      if (selected && (!v.AutoConfirm || (v.Hero > 0 && v.Owner == v.Hero))) { State = "done"; return AutoAction.Nothing; }
      if (State == "failed" || State == "unconfirmed" || State == "slot_failed") return AutoAction.Nothing;
      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;       // the player acts himself: step back for a while
      if (!v.Foreground) { State = "background"; return AutoAction.Nothing; }
      if (v.UserBusy || v.NowMs < pauseUntil) { State = "paused"; return AutoAction.Nothing; }
      State = "work";
      if (v.NowMs < waitUntil) return AutoAction.Nothing;
      // «Заменить» was pressed and the selection went away: the item is on the hero, or the press did nothing (a dialog of
      // the game, a missed press) - item clicks would only select it again and fail, the player looks at the game
      if (confirmed && !selected && v.Slot == null) {
        if (v.Hero > 0 && v.Owner == v.Hero) { State = "done"; return AutoAction.Nothing; }
        if (v.Sel <= 0) { State = "unconfirmed"; Why = "no selection after the confirm press, owner " + v.Owner + ", hero " + v.Hero; Trace = "unconfirmed: " + Why; return AutoAction.Nothing; }
      }
      if (selected) return ConfirmStep(v);
      return v.Slot != null ? SlotStep(v) : ItemStep(v);
    }

    // «Заменить» once: the game shows the plan's hero and the item is not on that hero yet (else the button there is
    // «Снять»). When the item has not gone on after the server's time, stop - the player looks at the game.
    AutoAction ConfirmStep(AutoView v) {
      if (confirmed) {
        // the first press left the item off the hero and it is selected again (the card was not settled, or the press missed): once more, never more
        if (confirmRetries >= ConfirmRetries) { State = "unconfirmed"; return AutoAction.Nothing; }
        confirmRetries++; confirmed = false; lastRetry = true; Trace = "confirm again for item " + v.Target + " (owner " + v.Owner + ", hero " + v.Hero + ")";
      }
      if (v.Hero <= 0 || v.ScreenHero != v.Hero || v.Owner < 0) return AutoAction.Nothing;   // not sure yet: wait
      // the item is selected but its tip is known closed: the game keeps the selection after the tip went (an Escape, the app
      // started on it - live 1.6.66.348: «no single blue button», no step for 2 min). The item is clicked again: it opens the
      // tip, or clears the selection and the next item click selects it with its tip
      // (only for that known reason: no blue button at all and the window table read without the tip form)
      if (v.Button == null && v.BlueNone && v.TipKnown && !v.TipShown && v.NowMs - selSince >= TipReopenMs) {
        if (tipReopens >= TipReopens) { State = "unconfirmed"; Why = "the item's tip does not open (" + tipReopens + " clicks for it)"; Trace = "unconfirmed: " + Why; return AutoAction.Nothing; }
        tipReopens++; selSince = v.NowMs; lastAct = 2;
        var pt = tipReopens == 1 ? ItemPoint(v) : null;
        if (pt != null) {
          waitUntil = v.NowMs + AfterClickMs;
          Trace = "the tip of item " + v.Target + " is not open (no blue button, no tip form shown): the item clicked again at " + (int)pt[0] + "," + (int)pt[1];
          return ListClick(pt[0], pt[1]);
        }
        // the click on the item changed nothing (live 1.6.66.349: two clicks at 65,179, the selection and the closed tip stayed - the rows
        // are not where the anchor puts them): an item in the list's middle is clicked to anchor the rows again (it selects another
        // item; the plan's item is then clicked as any, which opens its tip)
        needCalib = true;
        var a = ItemStep(v);
        Trace = "the tip of item " + v.Target + " is still not open: the rows are anchored again" + (a.Kind == AutoKind.Click ? " at " + (int)a.X + "," + (int)a.Y : "");
        return a;
      }
      if (v.Button == null || v.NowMs - selSince < ConfirmSettleMs) return AutoAction.Nothing;  // the item card is opening
      confirmed = true; waitUntil = v.NowMs + AfterConfirmMs; lastAct = 1;
      Trace = "confirm press for item " + v.Target + " at " + (int)(v.Button[0] + v.Button[2] / 2.0) + "," + (int)(v.Button[1] + v.Button[3] / 2.0) + " button " + v.Button[2] + "x" + v.Button[3] + " owner " + v.Owner + " hero " + v.Hero + " card settled " + (v.NowMs - selSince) + " ms";
      return Click(v.Button[0] + v.Button[2] / 2.0, v.Button[1] + v.Button[3] / 2.0);
    }

    AutoAction SlotStep(AutoView v) {
      // the slot does not open: the game keeps bracer / amulet / ring closed until a story stage (EquipData:IsSlotUnLock)
      // (the screen read shows it open, or the panel table is being read again: not the game's fault, no try is spent)
      if (v.SlotOpen || (!v.PanelOk && !v.PanelNone)) {
        if (++slotWaits > SlotWaits) { State = "slot_failed"; Why = "the slot is shown open for " + slotWaits + " ticks, memory disagrees (panel ok " + v.PanelOk + ")"; Trace = "slot failed: " + Why; }
        waitUntil = v.NowMs + AfterSlotMs; return AutoAction.Nothing;
      }
      if (clicks >= SlotTries) {
        if (v.NowMs - lastSlotClick < SlotGraceMs) return AutoAction.Nothing;   // the last click may still be opening it
        State = "slot_failed"; Why = "the slot does not open after " + clicks + " clicks"; Trace = "slot failed: " + Why; return AutoAction.Nothing;
      }
      clicks++; lastSlotClick = v.NowMs; waitUntil = v.NowMs + AfterSlotMs;
      return Click(v.Slot[0] + v.Slot[2] / 2.0, v.Slot[1] + v.Slot[3] / 2.0);
    }

    /// <summary>The centre of the target item's cell (its place in the list shown now), null = not known or not in the view.</summary>
    double[] ItemPoint(AutoView v) {
      if (!v.AnchorHas || v.Types == null || v.Row <= 0 || v.Col <= 0) return null;
      double H = v.H, cell = g.CellW * H;
      double cy = v.Row1Top + Off + g.RowOffset(v.Types, v.Row) * H + cell / 2, cx = g.ColLeft(v.Col) * H + cell / 2;
      if (cy < g.ViewTop * H + cell * 0.45 || cy > g.ViewBottom * H - cell * 0.45) return null;
      return new[] { cx, cy };
    }

    AutoAction ItemStep(AutoView v) {
      double H = v.H, cell = g.CellW * H, pitch = g.PitchY * H;
      double viewTop = g.ViewTop * H, viewBottom = g.ViewBottom * H, mid = (viewTop + viewBottom) / 2;
      double col1 = g.ColLeft(1) * H + cell / 2;

      // the filter's side panel is open: clicks on the list would go into it - close it first (not counted as a try)
      if (v.SideClose != null) { waitUntil = v.NowMs + AfterClickMs; return Click(v.SideClose[0], v.SideClose[1]); }
      // the list changed its length (a filter): what was learnt of its rows is stale - one more read before any click
      int tl = v.Types != null ? v.Types.Length : 0;
      if (tl != typesLen) {
        bool first = typesLen < 0; typesLen = tl;
        if (!first) { row1Off = row1Fix = double.NaN; itemPending = calibPending = false; lastPtX = lastPtY = double.NaN; samePt = 0; waitUntil = v.NowMs + AfterClickMs; Trace = "the list changed (" + tl + " rows): read again"; return AutoAction.Nothing; }
      }
      // the last list click: did it change anything? (the selection stayed: a gap, a stale place) - the rows are anchored again, and
      // after NoEffectTries such clicks for one item the pilot stops
      if (itemPending || calibPending) {
        // (a slow PC answers up to 1.5 s after the click: nothing changed yet is not judged before JudgeMs)
        if (v.Sel == selBefore && v.NowMs - clickAt < JudgeMs) return AutoAction.Nothing;
        lastNoEffect = v.Sel == selBefore;
        if (lastNoEffect) {
          if (++noEffect >= NoEffectTries) { itemPending = calibPending = false; return Fail(noEffect + " clicks on the list changed nothing (sel " + v.Sel + ", last at " + (int)lastPtX + "," + (int)lastPtY + ")"); }
          needCalib = true;
        }
        // a click selected an item whose row is known: the click point lies in that row - the rows are where that says
        else if (v.Sel > 0 && v.SelRow > 0 && v.Types != null && v.SelRow < v.Types.Length) Learn(v, itemPending ? clickY : calY);
      }

      // the item click selected another item: the list is not where the pilot thinks it is. Its place in the list is known
      // (SelRow): row 1 is where that click puts it; never the same point again, and after two such hits stop
      if (itemPending) {
        itemPending = false;
        if (v.Sel > 0 && v.Sel != v.Target) {
          if (++wrongs >= WrongTries) return Fail("wrong item " + v.Sel + " at row " + v.SelRow + "/" + v.SelCol);
          Trace = "wrong item " + v.Sel + " at row " + v.SelRow + "/" + v.SelCol + " (clicked for row " + v.Row + "/" + v.Col + ")";
          if (!(v.SelRow > 0 && v.Types != null && v.SelRow < v.Types.Length)) needCalib = true;
        }
      }
      // the re-anchoring click selected nothing new (a gap, a title row, the same item): once more, a row further
      if (calibPending) {
        calibPending = false;
        // (also a selection that went away: the click hit the selected item again, which clears it - nothing new to anchor by)
        if ((v.Sel == selAtCalib || v.Sel <= 0) && calibMiss < 3) { calibMiss++; needCalib = true; }
        else calibMiss = 0;
      }
      // no idea where the rows are: click an item in the middle, the game says which one it is
      if (!v.AnchorHas || needCalib) {
        if (calibs >= CalibTries + 3 * DragTries) return Fail("no anchor after " + calibs + " calibration clicks");
        row1Fix = double.NaN;
        calibs++; needCalib = false; calibPending = true; selAtCalib = v.Sel; waitUntil = v.NowMs + AfterClickMs;
        // where the list should be now: moved with the drag since the last anchor, else as last known, else at the top
        // (a short filtered list is; its middle can be a title or «nothing found» - no item to click there)
        // (the list stops at its ends: a drag longer than the way left moved it only that far - see Clamp)
        double est = measure && v.AnchorHas ? Clamp(beforeDrag + lastDrag * GainOr1, v.Types, H) : v.AnchorHas ? v.Row1Top + Off : ScrollAnchor.TopRow1(g, H);
        double shift = (calibMiss % 3 == 1 ? 1 : calibMiss % 3 == 2 ? -1 : 0) * pitch;
        double cyC = double.IsNaN(est) || v.Types == null ? mid + shift * 0.3 : NearestCellCentre(est, v.Types, H, mid + shift);
        // (after a wrong hit: not the point that selected the other item - a row further)
        if (wrongs > 0 && v.Types != null && !double.IsNaN(est) && Math.Abs(col1 - clickX) < 2 && Math.Abs(cyC - clickY) < 2) cyC = NearestCellCentre(est, v.Types, H, cyC + pitch);
        // (the last click changed nothing: its point is off the cells - half a row from it, not where the rows are thought to be)
        if (lastNoEffect && !double.IsNaN(lastPtY)) {
          double up = lastPtY - pitch / 2, dn = lastPtY + pitch / 2, lo = viewTop + cell * 0.45, hi = viewBottom - cell * 0.45;
          cyC = noEffect % 2 == 1 ? (dn <= hi ? dn : up) : (up >= lo ? up : dn);
        }
        lastNoEffect = false; calY = cyC;
        return ListClick(col1, cyC);
      }
      // after a drag and the re-anchoring click: how far did the list follow the pointer?
      if (measure) {
        measure = false;
        double moved = v.Row1Top - beforeDrag;                       // same sign as the drag when the list followed it
        if (Math.Abs(moved) > 3 && Math.Sign(moved) == Math.Sign(lastDrag)) { Gain = Math.Max(0.2, Math.Min(3, moved / lastDrag)); stuck = 0; }
        else if (++stuck >= 2) {
          // the list stays put on drags toward one end: it is AT that end, whatever the position read says (live 1.6.66: the
          // scroll read after the filter shrank the list put row 1 at -223 / -166 px, the item at row 2 "above" the view, while
          // the short list sat at its top). Once per item the rows are taken from that end, then it gives up as before.
          if (endTaken || v.Types == null || v.Types.Length < 2) return Fail("the list does not move (row 1 top " + (int)v.Row1Top + ", " + drags + " drags " + (lastDrag > 0 ? "down" : "up") + ")");
          endTaken = true; stuck = 0;
          double top0 = ScrollAnchor.TopRow1(g, H);
          row1Fix = lastDrag > 0 ? top0 : Clamp(double.NegativeInfinity, v.Types, H); row1FixBase = v.Row1Top;
          Trace = "the list does not move " + (lastDrag > 0 ? "down" : "up") + " (row 1 top " + (int)v.Row1Top + "): taken at its " + (lastDrag > 0 ? "top" : "end") + ", row 1 at " + (int)row1Fix;
        }
      }

      // (the host moved the anchor itself since the wrong hit: its row 1 is the one to trust)
      if (!double.IsNaN(row1Fix) && Math.Abs(v.Row1Top - row1FixBase) > 1) row1Fix = double.NaN;
      double row1 = double.IsNaN(row1Fix) ? v.Row1Top + Off : row1Fix;
      double top = row1 + g.RowOffset(v.Types, v.Row) * H, cy = top + cell / 2, cx = g.ColLeft(v.Col) * H + cell / 2;
      if (cy >= viewTop + cell * 0.45 && cy <= viewBottom - cell * 0.45) {
        // the same point selected another item before: look for the rows again instead of clicking it
        if (wrongs > 0 && Math.Abs(cx - clickX) < 2 && Math.Abs(cy - clickY) < 2) { row1Fix = double.NaN; needCalib = true; return AutoAction.Nothing; }
        // after «Заменить» the item clicks have a budget of their own (the confirm is not repeated by clicking)
        if (confirmed) { if (afterClicks >= AfterConfirmClicks) { State = "unconfirmed"; Why = "the item is not selected again after the confirm press (sel " + v.Sel + ", owner " + v.Owner + ")"; Trace = "unconfirmed: " + Why; return AutoAction.Nothing; } afterClicks++; }
        else if (clicks >= ClickTries) return Fail("item " + v.Target + " not selected after " + clicks + " clicks (sel " + v.Sel + ")");
        clicks++; waitUntil = v.NowMs + AfterClickMs;
        itemPending = true; clickX = cx; clickY = cy;
        var ic = ListClick(cx, cy); if (ic.Kind == AutoKind.Click) ic.Cell = v.Target;
        return ic;
      }
      if (drags >= DragTries) return Fail("the item is not reached after " + drags + " drags");
      double dist = cy - mid;                                          // > 0: the item is below: drag the list up
      double span = viewBottom - viewTop - cell;                       // the longest drag that stays over the list
      // only as far as the list can go: a short list (a few rows under the sub stats filter) ends before the item reaches the middle;
      // a longer drag would move it to its end only, and the position after it would be guessed a screen too far (live 2026-10-18)
      double move = Clamp(v.Row1Top - dist, v.Types, H) - v.Row1Top;
      double dy = Math.Max(-span, Math.Min(span, (Math.Abs(move) >= 1 ? move : -dist) / GainOr1));
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      double y0 = dy < 0 ? viewBottom - cell / 2 : viewTop + cell / 2;   // start at the far end so the whole move fits
      drags++; beforeDrag = v.Row1Top; lastDrag = dy; needCalib = true; measure = true; row1Fix = double.NaN;
      waitUntil = v.NowMs + AfterDragMs;
      return new AutoAction { Kind = AutoKind.Drag, X = (g.StripLeft + g.StripRight) / 2 * H, Y = y0, DY = dy };
    }

    double Off { get { return double.IsNaN(row1Off) ? 0 : row1Off; } }

    /// <summary>The click at <paramref name="y"/> selected the item of row SelRow: when the rows as thought put that row's cell elsewhere,
    /// row 1 is taken from the click (the point at the cell's middle).</summary>
    void Learn(AutoView v, double y) {
      double H = v.H, cell = g.CellW * H, t = v.Row1Top + Off + g.RowOffset(v.Types, v.SelRow) * H;
      if (y >= t - 1 && y <= t + cell + 1) return;
      row1Off = y - cell / 2 - g.RowOffset(v.Types, v.SelRow) * H - v.Row1Top; row1Fix = double.NaN;
      Trace = (Trace != "" ? Trace + "; " : "") + "rows taken from the click on row " + v.SelRow + ": row 1 at " + (int)(v.Row1Top + row1Off) + " (anchor " + (int)v.Row1Top + ")";
    }

    /// <summary>A click on the list (an item or the re-anchoring one): never the same point more than SamePointMax times in a row.</summary>
    /// <summary>The host clicked the item's cell where the game has put it (its live rect), not at the pilot's point: what follows is learned from there.</summary>
    public void LivePoint(double x, double y) { if (!itemPending) return; clickX = x; clickY = y; lastPtX = x; lastPtY = y; }

    AutoAction ListClick(double x, double y) {
      if (Math.Abs(x - lastPtX) < 2 && Math.Abs(y - lastPtY) < 2) { if (++samePt > SamePointMax) { itemPending = calibPending = false; return Fail("the same point " + (int)x + "," + (int)y + " would be clicked a " + samePt + "rd time in a row"); } }
      else samePt = 1;
      lastPtX = x; lastPtY = y; selBefore = curSel; clickAt = nowMs; listSent = true;
      return Click(x, y);
    }
    long curSel, nowMs, clickAt; bool listSent;
    /// <summary>How long a list click gets to change the selection before it counts as one that changed nothing.</summary>
    public const int JudgeMs = 1800;

    double GainOr1 { get { return Gain > 0.2 ? Gain : 1; } }

    /// <summary>Row 1's top kept inside the places the list can be: from its top (scroll 0) to where the last row ends at the bottom of the view.</summary>
    double Clamp(double row1, int[] types, double H) {
      double top = ScrollAnchor.TopRow1(g, H);
      if (types == null || types.Length < 2) return Math.Min(top, row1);
      double end = Math.Min(top, g.ViewBottom * H - ListGeometry.TopPad * H - g.RowOffset(types, types.Length) * H);
      return Math.Max(end, Math.Min(top, row1));
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

    AutoAction Fail(string why) { State = "failed"; Why = why; Trace = "failed: " + why; return AutoAction.Nothing; }

    static AutoAction Click(double x, double y) { return new AutoAction { Kind = AutoKind.Click, X = x, Y = y }; }
  }
}
