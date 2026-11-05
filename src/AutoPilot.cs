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
    public string Widget;     // Click: the widget it presses (a src/FrameTargets.cs key, e.g. "hero.gridFunnel", "gear.reset"): the host clicks its live rect when it is settled; X, Y (the geometry) stay the fallback
    public UiCellKind CellKind; public long CellId;   // Click: the list cell it presses (a filter's set / stat / sub stat, a hero class / faction, by its id): the same, by the cell's live rect
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
    /// <summary>The navigator owns the way to the slot (src/NavEquip.cs): the pilot never opens a slot, it works on the open one. Default off: the old path unchanged.</summary>
    public bool NavOwned;
    /// <summary>The game's input is open (RFX.NavInputOpen): the pilot starts no click or drag while it is locked. Default true.</summary>
    public bool InputOpen = true;
    public bool PanelNone;         // no gear list panel exists yet (it is created when a slot is first opened): click the slot
    public bool PanelOk = true;
    /// <summary>The game's window table was read for this hero screen (the shown forms are known) and whether the item's tip
    /// (Form_CharactorEquipTip, the card with «Надеть» / «Заменить») is among them.</summary>
    public bool TipKnown, TipShown;
    /// <summary>Neither blue button search saw one (no «Заменить», no «Надеть»; not a tie of two): the card is not on the screen.</summary>
    public bool BlueNone;    // the game's panel table is readable (false while the addresses are scanned again)
    /// <summary>Button is the live rect of the tip's «Заменить» / «Надеть», held still (LiveStill): the card has stopped opening.</summary>
    public bool ButtonStill;
    /// <summary>The game's own cells of the gear list are readable now (a fresh live read of the grid: its form shown, its viewport known): item clicks go to a
    /// live cell only, never to a point from the row math. LiveTarget: the cell of Target is among them, wholly in view, the list on top, at LiveX, LiveY (client px).</summary>
    public bool LiveGrid, LiveTarget; public double LiveX, LiveY;
    /// <summary>Which list the rows belong to now (the host: its pointer and length); a change = the list was rebuilt (a filter, a toggle): clicks wait for two equal reads.</summary>
    public long ListSig;
    /// <summary>The game's own list position (the host's live read of the cells it made, memory only): the first and last made cell (0-based list index, -1 = not read), the target's cell centre y when it is made
    /// (client px, NaN = no cell of it) and the viewport's top and bottom. With these the pilot scrolls by what the game says, never by row math.</summary>
    public int LiveFirst = -1, LiveLast = -1;
    public double LiveCellY = double.NaN, LiveViewTop = double.NaN, LiveViewBottom = double.NaN;
    /// <summary>The game's content position of the list (ScrollRect content.anchoredPosition.y, the list's own units: an item row is 100, a title 44, an empty row 60), NaN = not read.
    /// With it and the viewport the pilot computes the drag to a row from memory (ScrollTo), never from the made cells' guess.</summary>
    public double LiveScroll = double.NaN;
    /// <summary>The gear list as the game's ScrollRect and grid hold it (RFX.ReadListMem, package P5): ListKnown = read; ListMoving = dragged, scrolling or still coasting (|speed| above the standing limit); the content and view heights in
    /// the list's own units (the bounds: the longest position is content - view); the game's drag threshold in px (EventSystem.m_DragThreshold), 0 = not read (the pilot keeps its constant).</summary>
    public bool ListKnown, ListMoving; public double ListContentH, ListViewH, ListThrPx;
    /// <summary>The game's own pointer (RFX.ReadPointer): what is under the cursor now, read while a click on a live cell awaits its judgement (WantRay). RayNone = the raycast hit nothing: the click went into the void.</summary>
    public bool RayKnown, RayNone;
    /// <summary>How many regrids the host has done (a full re-read of the gear grid, new cell and scroll-rect addresses): the pilot's RegridSeq it has caught up with.</summary>
    public int RegridDone;
    /// <summary>Another cell than the target's, wholly in view (LiveNbUid, its centre): the way to re-select when the selected item's card is not open (the game's click on a selected item does nothing). The host reads it only while WantNeighbour.</summary>
    public bool LiveNb; public double LiveNbX, LiveNbY; public long LiveNbUid;
    /// <summary>The item whose card stands (Form_CharactorEquipTip.m_SelectEquipInfo.iItemUid), 0 = none / unread.</summary>
    public long TipUid;
    /// <summary>When the filter was last touched (a pilot click on it, its panel closing; host clock, 0 = never): the list is rebuilding after it.</summary>
    public long FilterAt;
  }

  /// <summary>A pilot's wait after its click ends early once the click's effect shows in the fields the pilot reads every tick (the hot
  /// fields: the hero shown, the open slot, the selection, the filter's choice and panels ...): a key of them other than the one at the
  /// click, the same on two reads at least StableMs apart and at least MinMs after the click. A click whose effect is never seen (a drag,
  /// a read that stays stale) keeps its fixed wait. Polling: while armed the host reads at its effect rate (50 ms, src/LoadPlan.cs).</summary>
  public sealed class EffectWait {
    public const int StableMs = 50, MinMs = 150;
    string before, last; long clickAt, lastAt; bool armed;
    public void Arm(string key, long now) { before = key; last = null; clickAt = now; armed = true; }
    public void Drop() { armed = false; last = null; }
    public bool Armed { get { return armed; } }
    /// <summary>True once: the effect has shown and held still (the wait may end now).</summary>
    public bool Seen(string key, long now) {
      if (!armed) return false;
      if (key == before) { last = null; return false; }
      if (key != last) { last = key; lastAt = now; return false; }
      if (now - lastAt < StableMs || now - clickAt < MinMs) return false;
      armed = false; return true;
    }
  }
  /// <summary>The «replace» step's lines name the button the game shows: the page says «Заменить» for every selected item, an empty slot has «Надеть».</summary>
  public static class ConfirmText {
    public static string[] ForButton(string[] lines, bool equip) {
      if (!equip || lines == null) return lines;
      var l = new string[lines.Length];
      for (int i = 0; i < l.Length; i++) l[i] = (lines[i] ?? "").Replace("«Заменить»", "«Надеть»").Replace("“Replace”", "“Equip”");
      return l;
    }
  }
  public sealed class AutoPilot {
    public const int SlotTries = 3, ClickTries = 5, DragTries = 60, CalibTries = 4, WrongTries = 2, AfterConfirmClicks = 2, SlotWaits = 40;
    public const int AfterSlotMs = 900, AfterClickMs = 500, AfterDragMs = 800, UserPauseMs = 4000;
    public const int SlotGraceMs = 2500;   // the last slot click gets this long to show its effect before the slot counts as closed
    public const int ConfirmSettleMs = 600, AfterConfirmMs = 3000;
    /// <summary>The selection this long with the button's live rect held still: the card is open, «Заменить» is pressed (else ConfirmSettleMs).</summary>
    public const int ConfirmLiveMs = 150;
    /// <summary>The item selected with its tip known closed this long: the item is clicked again (at most TipReopens times an item).</summary>
    public const int TipReopenMs = 2500, TipReopens = 2;   // item card opening; the server's answer to «Заменить»
    public const double MinDragPx = 24;   // shorter moves would count as a click in the game
    /// <summary>The pixels a drag loses at its start (EventSystem.pixelDragThreshold: the content follows the pointer from where the drag began, not from the press); a computed drag is that much longer.</summary>
    public const double DragThresholdPx = 8;
    /// <summary>The list position must stand still this long (two equal reads) before the next drag is computed: a drag coasts (inertia) after the release.</summary>
    public const int ScrollSettleMs = 250;
    /// <summary>Content units of one item row (the game's template height), a title row and an empty row are 44 / 60 of it.</summary>
    public const double RowUnits = 100;

    readonly ListGeometry g;
    /// <summary>The atom executor (src/NavAct.cs): every list click and drag of the live path goes through it; its lines are the host's «atom: ...» log lines.</summary>
    public readonly AtomRunner Atoms = new AtomRunner();
    /// <summary>The pilot needs a neighbour cell of the target (a selected item whose card is closed is re-selected through another cell): the host reads the whole list's cells while this is on.</summary>
    public bool WantNeighbour { get { return wantNb; } }
    bool wantNb, curLive, dragPending; string dragPos = ""; double dragDy, dragCell0 = double.NaN, dragScroll0 = double.NaN, scrollSeen = double.NaN; long scrollSeenAt; long dragAt, swapWaitSince, liveNoPosSince; int noMove, swaps, nudges; bool altStrip, sameRetried, retryOk;
    public const int MaxSwaps = 3, SwapWaitMs = 3000, LivePosWaitMs = 15000;
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
    // the list rebuilt by a filter / toggle (live 1.6.66.442 «Скрыть надетое»: the re-anchoring click on the old rows picked a random item, the
    // item itself was clicked a second later): no click until the filter has rested ListSettleMs, the list's sign and the target's live cell are
    // the same on two reads; with live cells the item is clicked only at its live cell, a calibration click on the row math never
    long listSig = long.MinValue, listSigAt, liveSeenAt; double liveSeenX = double.NaN, liveSeenY;
    public const int ListSettleMs = 450;
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
    public void Reset() { goal = "\u0001"; State = "idle"; }

    /// <summary>The host did not send the last action (the click gate's wait, the shown forms unread): a «Заменить» press or a click for the
    /// item's tip is not counted (live 1.6.66.348 sim: the press that met the tip's first read was taken for made, the retry met a failed read - unconfirmed).</summary>
    public void NotSent() {
      if (lastAct == 1) { confirmed = lastRetry; if (lastRetry) confirmRetries--; waitUntil = 0; }
      else if (lastAct == 2) { tipReopens--; waitUntil = 0; calibPending = false; }
      // a list click not sent: its result is not waited for, its point was not clicked
      // (the click budget too: a click the gate did not let out - the last Escape's effect still awaited, live 578 122804: five refused ticks in one second
      // were five "clicks", "not selected after 5 clicks", a failed step and a 7 s watchdog - is no try)
      if (listSent) { if (counted > 0 && clicks > 0) clicks--; if (counted == 2 && afterClicks > 0) afterClicks--; counted = 0; }
      if (listSent) { listSent = false; itemPending = calibPending = false; if (samePt > 0) samePt--; if (samePt == 0) lastPtX = lastPtY = double.NaN; waitUntil = 0; }
      lastAct = 0; effect.Drop(); Atoms.Abort();
    }

    public AutoAction Step(AutoView v) {
      if (v.NavOwned) v.Slot = null;   // the navigator opened the slot (NavPlan's facet:slot): the item steps only
      lastAct = 0; listSent = false; counted = 0;
      string key = v.Slot != null ? "slot:" + v.Slot[0] + ":" + v.Slot[1] : v.Target > 0 && (v.Row > 0 || (v.AutoConfirm && v.Sel == v.Target)) ? "item:" + v.Target : "";
      if (key != goal) {
        // the step before ended: a slot step's duration is noted for the item step's line
        if (goal.StartsWith("slot:") && goalAt > 0) slotMs = v.NowMs - goalAt;
        else if (goal.StartsWith("item:")) slotMs = -1;
        goalAt = v.NowMs; pickAt = selAt = confirmAt = 0; stepSaid = false;
        goal = key; waitUntil = 0; effect.Drop(); clicks = drags = calibs = stuck = calibMiss = 0; needCalib = measure = calibPending = endTaken = false;
        selSince = 0; confirmed = false; confirmRetries = 0; tipReopens = 0; lastRetry = false; State = "idle"; Why = "";
        slotWaits = afterClicks = wrongs = 0; lastSlotClick = 0; itemPending = false; row1Fix = double.NaN;
        row1Off = double.NaN; noEffect = samePt = 0; typesLen = -1; lastPtX = lastPtY = double.NaN; lastNoEffect = false;
        listSig = long.MinValue; liveSeenX = double.NaN;
        wantNb = dragPending = altStrip = sameRetried = retryOk = false; noMove = swaps = nudges = 0; swapWaitSince = liveNoPosSince = 0; Atoms.Clear();
        regrids = 0; regridPending = false;
      }
      if (key == "") { State = "idle"; return AutoAction.Nothing; }
      curSel = v.Sel; nowMs = v.NowMs; curKey = Key(v); curTip = v.TipKnown && v.TipShown; curLive = v.LiveGrid;
      Atoms.Poll(GameFacts.Of(v), v.NowMs);
      bool selected = v.Slot == null && v.Sel == v.Target;
      if (!selected) selSince = 0; else if (selSince == 0) { selSince = v.NowMs; if (selAt == 0) selAt = v.NowMs; }
      if (selected) itemPending = false;
      // selected: the player presses «Заменить», or it is pressed below and the item is on the hero now
      if (selected && (!v.AutoConfirm || (v.Hero > 0 && v.Owner == v.Hero))) { State = "done"; StepSay(v.NowMs); return AutoAction.Nothing; }
      if (State == "failed" || State == "unconfirmed" || State == "slot_failed") return AutoAction.Nothing;
      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;       // the player acts himself: step back for a while
      if (!v.Foreground) { State = "background"; return AutoAction.Nothing; }
      if (v.UserBusy || v.NowMs < pauseUntil) { State = "paused"; return AutoAction.Nothing; }
      State = "work";
      // «Заменить» was pressed and the item is on the plan's hero: done at once (not after the press's whole wait)
      if (confirmed && !selected && v.Slot == null && v.Hero > 0 && v.Owner == v.Hero) { State = "done"; StepSay(v.NowMs); return AutoAction.Nothing; }
      // the last click's effect (the slot open, the selection, the tip) seen and held still: its fixed wait ends now
      if (v.NowMs < waitUntil && effect.Seen(Key(v), v.NowMs)) waitUntil = v.NowMs;
      if (v.NowMs < waitUntil) return AutoAction.Nothing;
      // «Заменить» was pressed and the selection went away: the item is on the hero, or the press did nothing (a dialog of
      // the game, a missed press) - item clicks would only select it again and fail, the player looks at the game
      if (confirmed && !selected && v.Slot == null) {
        if (v.Hero > 0 && v.Owner == v.Hero) { State = "done"; StepSay(v.NowMs); return AutoAction.Nothing; }
        if (v.Sel <= 0) { State = "unconfirmed"; Why = "no selection after the confirm press, owner " + v.Owner + ", hero " + v.Hero; Trace = "unconfirmed: " + Why; return AutoAction.Nothing; }
      }
      if (selected) return ConfirmStep(v);
      if (!v.InputOpen) return AutoAction.Nothing;
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
        if (v.LiveGrid) {
          // the game's click on the item that is selected already does nothing (_OnEquipClicked opens the card only when the selection changes: live 464e21): another cell is clicked, then the item again
          var sw = SwapStep(v, "the selected item's card is closed");
          if (sw.Kind == AutoKind.Click) { tipReopens++; selSince = v.NowMs; lastAct = 2; Trace = "the tip of item " + v.Target + " is not open and a click on it opens nothing: another cell is clicked first"; }
          return sw;
        }
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
      if (v.Button == null || v.NowMs - selSince < (v.ButtonStill ? ConfirmLiveMs : ConfirmSettleMs)) return AutoAction.Nothing;  // the item card is opening
      confirmed = true; confirmAt = v.NowMs; waitUntil = v.NowMs + AfterConfirmMs; lastAct = 1; effect.Drop();   // (its answer: the owner, checked above; never ended early by the selection)
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
      // the last click may still be opening it (a slow laptop: 0.6-1.5 s): not clicked again before SlotGraceMs
      if (clicks > 0 && v.NowMs - lastSlotClick < SlotGraceMs) return AutoAction.Nothing;
      if (clicks >= SlotTries) {
        State = "slot_failed"; Why = "the slot does not open after " + clicks + " clicks"; Trace = "slot failed: " + Why; return AutoAction.Nothing;
      }
      clicks++; lastSlotClick = v.NowMs; waitUntil = v.NowMs + AfterSlotMs; effect.Arm(Key(v), v.NowMs);
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
      if (v.SideClose != null) { waitUntil = v.NowMs + AfterClickMs; effect.Arm(Key(v), v.NowMs); return Click(v.SideClose[0], v.SideClose[1]); }
      // the list was rebuilt (its sign changed) or the filter was just touched: what was read of the rows is stale - no click until the list
      // holds still (the sign the same on two reads, the filter at rest ListSettleMs)
      if (v.ListSig != listSig) {
        bool firstSig = listSig == long.MinValue; listSig = v.ListSig; listSigAt = v.NowMs;
        if (!firstSig) { row1Off = row1Fix = double.NaN; itemPending = calibPending = needCalib = false; lastPtX = lastPtY = double.NaN; samePt = 0; liveSeenX = double.NaN; waitUntil = v.NowMs + EffectWait.StableMs; effect.Drop(); Trace = "the list was rebuilt: read again"; return AutoAction.Nothing; }
      }
      if (v.FilterAt > 0 && v.NowMs - v.FilterAt < ListSettleMs) { liveSeenX = double.NaN; waitUntil = Math.Max(waitUntil, v.FilterAt + ListSettleMs); return AutoAction.Nothing; }
      // the list changed its length (a filter): what was learnt of its rows is stale - one more read before any click
      int tl = v.Types != null ? v.Types.Length : 0;
      if (tl != typesLen) {
        bool first = typesLen < 0; typesLen = tl;
        if (!first) { row1Off = row1Fix = double.NaN; itemPending = calibPending = false; lastPtX = lastPtY = double.NaN; samePt = 0; waitUntil = v.NowMs + EffectWait.StableMs; effect.Drop(); Trace = "the list changed (" + tl + " rows): read again"; return AutoAction.Nothing; }
      }
      // the last list click: did it change anything? (the selection stayed: a gap, a stale place) - the rows are anchored again, and
      // after NoEffectTries such clicks for one item the pilot stops
      if (itemPending || calibPending) {
        // a click made while another item's tip stood was taken by the tip (its tap catcher closes it, the selection stays: lab 1.6.66.427, the
        // re-anchoring click selected row 4 and opened its tip, the click on the item only closed it): that is no click that changed nothing,
        // the item is clicked again now that the tip is gone
        if (tipAtClick && v.TipKnown && !v.TipShown && v.Sel == selBefore) {
          itemPending = calibPending = false; tipAtClick = false; lastPtX = lastPtY = clickX = clickY = double.NaN; samePt = 0;
          waitUntil = v.NowMs + EffectWait.StableMs; effect.Drop();
          Trace = "the click was taken by the tip of item " + v.Sel + " (it closed): the click again";
          return AutoAction.Nothing;
        }
        // (a slow PC answers up to 1.5 s after the click: nothing changed yet is not judged before JudgeMs)
        if (v.Sel == selBefore && v.NowMs - clickAt < JudgeMs) return AutoAction.Nothing;
        lastNoEffect = v.Sel == selBefore;
        if (lastNoEffect) {
          // the click on the live cell hit nothing at all (the game's pointer: raycast 0x0): the cell's rect is not where the game has the cell (a pooled / rebuilt grid read through old addresses): the grid is read again whole
          // and the click goes to the UPDATED rect, never to the same one (lab 559: 21 clicks at live[67,148], raycast 0x0, the same cellY every time)
          if (v.LiveGrid && itemPending && ((v.RayKnown && v.RayNone) || (regrids == 0 && noEffect == 0 && v.Sel <= 0 && !v.TipShown)) && regrids < MaxRegrids && !double.IsNaN(clickX)) {
            softRegrid = !(v.RayKnown && v.RayNone);
            regrids++; RegridSeq++; regridPending = true; missX = clickX; missY = clickY; regridAt = v.NowMs; itemPending = calibPending = false;
            Atoms.Note("item.click pre=ok target=live[" + (int)clickX + "," + (int)clickY + "] src=live eff=sel=" + v.Target + " " + (v.NowMs - clickAt) + "ms -> NoEffect diag=" + GameFacts.Of(v) + " | atom: " + (v.RayKnown && v.RayNone ? "raycast=0x0" : "first no effect, nothing selected, no card") + " -> regrid #" + regrids + " (the cells and the scroll rect are read again, the next click goes to the updated rect)");
            retryOk = true; liveSeenX = double.NaN; lastPtX = lastPtY = double.NaN; samePt = 0; waitUntil = v.NowMs + EffectWait.StableMs; effect.Drop();
            return AutoAction.Nothing;
          }
          if (++noEffect >= NoEffectTries) { itemPending = calibPending = false; return Fail(noEffect + " clicks on the list changed nothing (sel " + v.Sel + ", last at " + (int)lastPtX + "," + (int)lastPtY + ")"); }
          if (!v.LiveGrid) needCalib = true;
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
          if (!v.LiveGrid && !(v.SelRow > 0 && v.Types != null && v.SelRow < v.Types.Length)) needCalib = true;
          if (v.LiveGrid) { liveSeenX = double.NaN; waitUntil = v.NowMs + EffectWait.StableMs; effect.Drop(); return AutoAction.Nothing; }   // the live cell held another item: the list moved - read again
        }
      }
      // the re-anchoring click selected nothing new (a gap, a title row, the same item): once more, a row further
      if (calibPending) {
        calibPending = false;
        // (also a selection that went away: the click hit the selected item again, which clears it - nothing new to anchor by)
        if ((v.Sel == selAtCalib || v.Sel <= 0) && calibMiss < 3) { calibMiss++; needCalib = true; }
        else calibMiss = 0;
      }
      // the game's own cells are readable: the item is clicked at its live cell (the same point on two reads), nothing else on the list is clicked: no calibration click, no click on a point from the row math,
      // however long the live cell takes to show (live 464e21: twelve of them picked other items, each opened a card and the game scrolled the list back to it). The list is brought to the item by what the game says of its position.
      if (v.LiveGrid) {
        if (v.LiveTarget) {
          liveNoPosSince = 0; dragPending = false; noMove = 0;
          if (Atoms.Active && Atoms.Name == "list.drag") return AutoAction.Nothing;   // (a drag of ours is still moving the list: the cell is clicked where it stands after it)
          // (the host reads the cells about every 200 ms: a tick between two reads may see none - a gap keeps what was seen, a moved cell starts over)
          bool same = !double.IsNaN(liveSeenX) && Math.Abs(v.LiveX - liveSeenX) < 1.5 && Math.Abs(v.LiveY - liveSeenY) < 1.5;
          if (!same) { liveSeenAt = v.NowMs; liveSeenX = v.LiveX; liveSeenY = v.LiveY; }
          if (v.NowMs - liveSeenAt < EffectWait.StableMs || v.NowMs - listSigAt < EffectWait.StableMs) return AutoAction.Nothing;   // the same place on reads 50 ms apart first
          if (regridPending) {
            // the host's full re-read first (RegridDone), at most RegridWaitMs; then the rect against the one that hit nothing
            if (v.RegridDone < RegridSeq && v.NowMs - regridAt < RegridWaitMs) return AutoAction.Nothing;
            regridPending = false;
            bool moved = Math.Abs(v.LiveX - missX) >= 1.5 || Math.Abs(v.LiveY - missY) >= 1.5;
            if (moved) Atoms.Note("regrid #" + regrids + ": the cell's rect moved [" + (int)missX + "," + (int)missY + "] -> [" + (int)v.LiveX + "," + (int)v.LiveY + "]" + (v.RegridDone < RegridSeq ? " (the host did not answer)" : "") + ": the click goes to the updated rect");
            else if (softRegrid) Atoms.Note("regrid #" + regrids + ": the rect is the same [" + (int)v.LiveX + "," + (int)v.LiveY + "] (no pointer evidence of a layer over the list): the click again at once");
            else {
              BlockerSeq++; BlockerNote = "raycast=0x0 at live[" + (int)missX + "," + (int)missY + "], the rect is the same after the regrid";
              Atoms.Note("regrid #" + regrids + ": the rect is the same [" + (int)v.LiveX + "," + (int)v.LiveY + "] and the pointer hit nothing -> a layer over the list: the host looks for it (modal / panel / locker), the list is moved by a row meanwhile");
              if (nudges < 2 && v.LiveFirst >= 0 && !double.IsNaN(v.LiveViewTop) && !double.IsNaN(v.LiveViewBottom) && v.InputOpen) return Nudge(v, GameFacts.Of(v));
            }
          }
          needCalib = calibPending = false; wrongs = 0;
          long tg = v.Target;
          if (!Atoms.Begin("item.click", "u" + tg, "live[" + (int)v.LiveX + "," + (int)v.LiveY + "] src=live", "sel=" + tg, ff => ff.Sel == tg, JudgeMs, GameFacts.Of(v), v.NowMs, !retryOk)) return SwapStep(v, "the click on the live cell changed nothing and the facts are the same");
          retryOk = false;
          if (confirmed) { if (afterClicks >= AfterConfirmClicks) { State = "unconfirmed"; Why = "the item is not selected again after the confirm press (sel " + v.Sel + ", owner " + v.Owner + ")"; Trace = "unconfirmed: " + Why; return AutoAction.Nothing; } afterClicks++; counted = 2; }
          else if (clicks >= ClickTries) return Fail("item " + v.Target + " not selected after " + clicks + " clicks (sel " + v.Sel + ")");
          if (counted == 0) counted = 1;
          clicks++; waitUntil = v.NowMs + AfterClickMs; wantNb = false;
          itemPending = true; clickX = v.LiveX; clickY = v.LiveY;
          // (the point is the live cell's: a second click on it is a new try, not the same stale point)
          lastPtX = lastPtY = double.NaN; samePt = 0;
          var lc = ListClick(v.LiveX, v.LiveY); if (lc.Kind == AutoKind.Click) lc.Cell = v.Target;
          return lc;
        }
        return LiveScroll(v);
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
        if (confirmed) { if (afterClicks >= AfterConfirmClicks) { State = "unconfirmed"; Why = "the item is not selected again after the confirm press (sel " + v.Sel + ", owner " + v.Owner + ")"; Trace = "unconfirmed: " + Why; return AutoAction.Nothing; } afterClicks++; counted = 2; }
        else if (clicks >= ClickTries) return Fail("item " + v.Target + " not selected after " + clicks + " clicks (sel " + v.Sel + ")");
        if (counted == 0) counted = 1;
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
      waitUntil = v.NowMs + AfterDragMs; effect.Drop();
      return new AutoAction { Kind = AutoKind.Drag, X = (g.StripLeft + g.StripRight) / 2 * H, Y = y0, DY = dy };
    }

    /// <summary>The target's cell is not in view (or not whole): the list is moved by what the game says of its position (the cells it made: first / last, the target's cell y, the viewport). ONE drag at a time, its effect
    /// the live position changing; a drag that moved nothing is diagnosed (another place of the strip once, then a named failure), never repeated blind. No click is made here.</summary>
    AutoAction LiveScroll(AutoView v) {
      double H = v.H, cell = g.CellW * H, pitch = g.PitchY * H;
      var f = GameFacts.Of(v);
      if (!double.IsNaN(v.LiveScroll) && v.Types != null && v.Row > 0 && !double.IsNaN(v.LiveViewTop) && !double.IsNaN(v.LiveViewBottom)) return MemScroll(v, f);
      dragScroll0 = double.NaN;
      if (dragPending && Atoms.Active && Atoms.Name == "list.drag") return AutoAction.Nothing;
      if (dragPending) {
        dragPending = false; string now = f.PosKey();
        if (now == dragPos) {
          noMove++;
          Trace = "the list did not move after the drag (live position " + now + ", " + noMove + " in a row)" + (noMove < 2 ? ": another place of the strip" : "");
          if (noMove >= 2) return Fail("the list does not move: its live position " + now + " is the same after " + noMove + " drags (" + f + ")");
          altStrip = !altStrip;
        } else {
          noMove = 0;
          // the gain of the drag from where the target's cell was and is (both made); else the last one stands
          if (!double.IsNaN(dragCell0) && !double.IsNaN(v.LiveCellY) && Math.Abs(dragDy) >= MinDragPx) {
            double gain = (v.LiveCellY - dragCell0) / dragDy;
            if (gain > 0.3 && gain < 2.5) Gain = gain;
          }
        }
      }
      if (!v.InputOpen) { Atoms.PreFails("list.drag", "input locked", f); return AutoAction.Nothing; }
      if (v.Row <= 0) { Atoms.PreFails("list.drag", "the target's row is not known", f); return AutoAction.Nothing; }
      if (v.LiveFirst < 0) {
        // the cells are read but not the list's place: nothing to scroll by, nothing to click - waited for, then a named stop
        if (liveNoPosSince == 0) liveNoPosSince = v.NowMs;
        Atoms.PreFails("list.drag", "no live list position", f);
        if (v.NowMs - liveNoPosSince > LivePosWaitMs) return Fail("the game's list position is not readable for " + (v.NowMs - liveNoPosSince) / 1000 + " s (no live cell of item " + v.Target + ")");
        return AutoAction.Nothing;
      }
      liveNoPosSince = 0;
      double vt = !double.IsNaN(v.LiveViewTop) ? v.LiveViewTop : g.ViewTop * H, vb = !double.IsNaN(v.LiveViewBottom) ? v.LiveViewBottom : g.ViewBottom * H, mid = (vt + vb) / 2;
      int idx = v.Row - 1; double dist;
      if (!double.IsNaN(v.LiveCellY)) dist = v.LiveCellY - mid;                                 // the cell is made: its own place
      else dist = (idx - (v.LiveFirst + v.LiveLast) / 2.0) * pitch;                              // not made: rows from the middle of the made ones
      if (drags >= DragTries) return Fail("the item is not reached after " + drags + " drags");
      double span = vb - vt - cell;
      double dy = Math.Max(-span, Math.Min(span, -dist / GainOr1));
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      double x0 = altStrip ? (g.StripLeft + (g.StripRight - g.StripLeft) * 0.2) * H : (g.StripLeft + g.StripRight) / 2 * H, y0 = dy < 0 ? vb - cell / 2 : vt + cell / 2;
      string pos0 = f.PosKey();
      if (!Atoms.Begin("list.drag", "to" + v.Target + (dy < 0 ? "up" : "down") + (altStrip ? "b" : "a"), "viewport[" + (int)vt + ".." + (int)vb + "] dy=" + (int)dy + " src=live", "list position changed", ff => ff.PosKey() != pos0, 1500, f, v.NowMs))
        return Fail("the drag " + (dy < 0 ? "up" : "down") + " moved nothing before and the facts are the same (" + f + ")");
      drags++; dragPending = true; dragPos = pos0; dragDy = dy; dragCell0 = v.LiveCellY; dragAt = v.NowMs;
      waitUntil = v.NowMs + AfterDragMs; effect.Arm(curKey, v.NowMs);
      return new AutoAction { Kind = AutoKind.Drag, X = x0, Y = y0, DY = dy };
    }

    /// <summary>ScrollTo from the game's memory (docs/specs/2026-11-04-game-atoms.md A3): the list's content position, the viewport and the row heights (item 100, title 44, empty 60) say the position that puts the target's row
    /// in the middle: y* = clamp(rowTop - viewH/2, 0, contentH - viewH). One drag of (y* - y) px (+ the drag threshold the game swallows), the pointer rests before the release; the position then read again once it stands still (a coast
    /// after the release is waited for), and the miss (a coast, a gain off) is one more computed drag - never a guess. A drag that moved nothing is diagnosed (another place of the strip once, then a named failure).</summary>
    AutoAction MemScroll(AutoView v, GameFacts f) {
      double H = v.H, cell = g.CellW * H, pitch = g.PitchY * H, k = pitch / RowUnits;
      // the position stands still on two reads (a drag coasts after the release)
      if (double.IsNaN(scrollSeen) || Math.Abs(scrollSeen - v.LiveScroll) > 0.25) { scrollSeen = v.LiveScroll; scrollSeenAt = v.NowMs; }
      if (v.NowMs - scrollSeenAt < ScrollSettleMs) return AutoAction.Nothing;
      // the game's own word that the list stands (not dragged, not scrolling, no coast left): a coasting list is waited for, at most MovingCapMs (a speed that never reads zero is not a reason to stand for ever)
      if (v.ListKnown && v.ListMoving) {
        if (movingSince == 0) movingSince = v.NowMs;
        if (v.NowMs - movingSince < MovingCapMs) { Atoms.PreFails("list.drag", "the list is moving (memory: dragged / scrolling / coasting)", f); return AutoAction.Nothing; }
      } else movingSince = 0;
      double thr = v.ListThrPx > 0 ? v.ListThrPx : DragThresholdPx;   // (the game's own drag threshold from memory, else the measured constant)
      // the drag is judged only when its atom has ended (the effect seen, or its 1.5 s out): the host's drag takes a second or more on a loaded PC, the pointer is still moving at the 0.8 s the wait ends
      // (lab 559: «position changed 1328 ms -> superseded», the second drag started under the first, two reads of the old position = «the list does not move» after 4 s)
      if (dragPending && Atoms.Active && Atoms.Name == "list.drag") return AutoAction.Nothing;
      if (dragPending) {
        dragPending = false;
        double moved = -(v.LiveScroll - dragScroll0) * k, cmd = dragDy - Math.Sign(dragDy) * thr;   // (px the content went in the pointer's direction; the drag that counted)
        if (Math.Abs(v.LiveScroll - dragScroll0) < 0.5) {
          noMove++;
          Trace = "the list did not move after the drag (content " + v.LiveScroll.ToString("0.#") + ", " + noMove + " in a row)" + (noMove < 2 ? ": another place of the strip" : "");
          if (noMove >= 2) return Fail("the list does not move: its content position " + v.LiveScroll.ToString("0.#") + " is the same after " + noMove + " drags (" + f + ")");
          altStrip = !altStrip;
        } else {
          noMove = 0;
          if (Math.Abs(cmd) >= MinDragPx / 2) { double gain = moved / cmd; if (gain > 0.3 && gain < 2.5) Gain = gain; }
        }
      }
      if (!v.InputOpen) { Atoms.PreFails("list.drag", "input locked", f); return AutoAction.Nothing; }
      double vt = v.LiveViewTop, vb = v.LiveViewBottom, viewU = (vb - vt) / k;
      double padU = ListGeometry.TopPad / g.PitchY * RowUnits, unit = RowUnits / g.PitchY;
      double rowTop = padU + g.RowOffset(v.Types, v.Row) * unit, contentU = padU + g.RowOffset(v.Types, v.Types.Length) * unit;
      if (v.ListKnown && v.ListContentH > 0 && v.ListViewH > 0) { contentU = v.ListContentH; viewU = v.ListViewH; }   // (the bounds from the game: its content and view heights, not the row math's)
      double want = Math.Max(0, Math.Min(Math.Max(0, contentU - viewU), rowTop + RowUnits / 2 - viewU / 2));
      double delta = want - v.LiveScroll;   // > 0: later rows have to come up (the content goes up with the pointer)
      if (drags >= DragTries) return Fail("the item is not reached after " + drags + " drags");
      double span = vb - vt - cell / 2 - thr;   // (the drag may start on a cell: it drags the list)
      double dy = -delta * k / GainOr1;
      dy = Math.Max(-span, Math.Min(span, dy));
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      dy += Math.Sign(dy) * thr;
      double x0 = altStrip ? (g.StripLeft + (g.StripRight - g.StripLeft) * 0.2) * H : (g.StripLeft + g.StripRight) / 2 * H, y0 = dy < 0 ? vb - cell / 4 : vt + cell / 4;
      string pos0 = v.LiveScroll.ToString("0.#");
      string posKey0 = f.PosKey();
      if (!Atoms.Begin("list.drag", "to" + v.Target + (dy < 0 ? "up" : "down") + (altStrip ? "b" : "a"), "viewport[" + (int)vt + ".." + (int)vb + "] dy=" + (int)dy + " src=mem content " + pos0 + "->" + want.ToString("0.#"), "list position changed", ff => ff.PosKey() != posKey0, 1500, f, v.NowMs))
        return Fail("the drag " + (dy < 0 ? "up" : "down") + " moved nothing before and the facts are the same (" + f + ")");
      drags++; dragPending = true; dragScroll0 = v.LiveScroll; dragDy = dy; dragAt = v.NowMs;
      waitUntil = v.NowMs + AfterDragMs; effect.Arm(curKey, v.NowMs);
      return new AutoAction { Kind = AutoKind.Drag, X = x0, Y = y0, DY = dy };
    }

    /// <summary>Another cell is clicked so that the next click on the target's cell changes the selection (a click on the selected item opens no card; a click that changed nothing on the same facts is not repeated).
    /// The neighbour is the host's live cell; none yet = it is asked for (WantNeighbour) and waited for, SwapWaitMs at most.</summary>
    AutoAction SwapStep(AutoView v, string why) {
      var f = GameFacts.Of(v);
      // another item is selected and the click on the target's own live cell did not take it (lab 559: sel 54835, target 55070, 26 refused clicks, 6 s idle, a reset every 40 s): a click on a NEIGHBOUR
      // would only select a third item. The list is moved by a row at once (the cell then stands elsewhere: a new diagnosis, the click on it is allowed), never a neighbour clicked, never 3 s waited for one.
      if (v.Sel > 0 && v.Sel != v.Target && v.LiveTarget) {
        wantNb = false;
        if (nudges < 2 && v.LiveFirst >= 0 && !double.IsNaN(v.LiveViewTop) && !double.IsNaN(v.LiveViewBottom) && v.InputOpen) {
          Trace = "the click on the live cell of item " + v.Target + " selected nothing (sel " + v.Sel + "): the list is moved by a row, no other cell is clicked (" + f + ")";
          return Nudge(v, f);
        }
        if (!sameRetried) {
          sameRetried = true; retryOk = true; swapWaitSince = 0; liveSeenAt = v.NowMs; listSigAt = v.NowMs;
          Trace = "the list moved, the click on the live cell of item " + v.Target + " is repeated once when its rect stands still (" + f + ")";
          return AutoAction.Nothing;
        }
        return Fail("item " + v.Target + " is not selected by its live cell (sel " + v.Sel + ", list moved by a row " + nudges + " times, the cell clicked again " + (sameRetried ? "yes" : "no") + "; " + f + ")");
      }
      wantNb = true;
      if (!v.LiveNb) {
        if (swapWaitSince == 0) swapWaitSince = v.NowMs;
        Atoms.PreFails("item.swap", "no neighbour cell in view yet", f);
        if (v.NowMs - swapWaitSince > SwapWaitMs) {
          // no neighbour cell in view (sel 0, no card after the click on the live cell: the first click missed, the cell was not interactive yet, the list was animating): ONE more click on the same live cell once its rect
          // stands still again (the list's position read from memory has not changed since: no new diagnosis, so the repeat is allowed once on purpose), then the list is moved by one row and the new live cell clicked;
          // a failure only after both, with the memory's diagnosis
          if (v.LiveTarget && !sameRetried) {
            sameRetried = true; retryOk = true; swapWaitSince = 0; liveSeenAt = v.NowMs; listSigAt = v.NowMs;
            Trace = "no neighbour cell in view: the live cell is clicked once more when its rect stands still (" + f + ")";
            return AutoAction.Nothing;
          }
          if (v.LiveTarget && nudges < 1 && v.LiveFirst >= 0 && !double.IsNaN(v.LiveViewTop) && !double.IsNaN(v.LiveViewBottom) && v.InputOpen) return Nudge(v, f);
          return Fail("no other cell of the list is in view to re-select by (" + why + "; " + f + "; the same cell was clicked again " + (sameRetried ? "yes" : "no") + ", list moved by a row " + (nudges > 0 ? "yes" : "no") + ")");
        }
        return AutoAction.Nothing;
      }
      swapWaitSince = 0;
      if (++swaps > MaxSwaps) return Fail("the item " + v.Target + " could not be re-selected through another cell in " + MaxSwaps + " tries (" + why + "; " + f + ")");
      long nb = v.LiveNbUid;
      Atoms.Begin("item.swap", "nb" + nb, "live[" + (int)v.LiveNbX + "," + (int)v.LiveNbY + "] src=live (" + why + ")", "sel=" + nb, ff => ff.Sel == nb, JudgeMs, f, v.NowMs, false);
      wantNb = false; itemPending = calibPending = false; lastPtX = lastPtY = double.NaN; samePt = 0; waitUntil = v.NowMs + AfterClickMs;
      var lc = ListClick(v.LiveNbX, v.LiveNbY); if (lc.Kind == AutoKind.Click) lc.Cell = nb;
      return lc;
    }

    /// <summary>One drag of one row toward the middle of the list (the click on the target's live cell changed nothing): its effect the list position changing; the list then stands elsewhere and the click on the cell is a new try.</summary>
    AutoAction Nudge(AutoView v, GameFacts f) {
      nudges++; swapWaitSince = 0; dragPending = false;
      double H = v.H, cell = g.CellW * H, pitch = g.PitchY * H, vt = v.LiveViewTop, vb = v.LiveViewBottom;
      double dy = Math.Max(MinDragPx, Math.Min(pitch, vb - vt - cell)) * ((nudges % 2 == 1) == (v.LiveCellY > (vt + vb) / 2) ? -1 : 1);   // one row toward the middle (the second nudge the other way: the first may have hit the end)
      string posKey0 = f.PosKey();
      Atoms.Begin("list.drag", "nudge" + v.Target + nudges, "viewport[" + (int)vt + ".." + (int)vb + "] dy=" + (int)dy + " src=live (one row, the click on the live cell changed nothing)", "list position changed", ff => ff.PosKey() != posKey0, 1500, f, v.NowMs, false);
      double x0 = (g.StripLeft + g.StripRight) / 2 * H, y0 = dy < 0 ? vb - cell / 2 : vt + cell / 2;
      drags++; dragPending = true; dragPos = posKey0; dragDy = dy; dragCell0 = v.LiveCellY; dragAt = v.NowMs;
      waitUntil = v.NowMs + AfterDragMs; effect.Arm(curKey, v.NowMs);
      return new AutoAction { Kind = AutoKind.Drag, X = x0, Y = y0, DY = dy };
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
      if (pickAt == 0) pickAt = nowMs;
      if (!curLive) Atoms.Geo("item.click", x, y);   // (only where the game's cells are not read at all: the old way, told in the log)
      lastPtX = x; lastPtY = y; selBefore = curSel; tipAtClick = curTip; clickAt = nowMs; listSent = true; effect.Arm(curKey, nowMs);
      return Click(x, y);
    }
    readonly EffectWait effect = new EffectWait(); string curKey = "";
    /// <summary>The pilot waits for its last click's effect (the host reads at its effect rate meanwhile).</summary>
    /// <summary>(An item click whose selection is not seen yet is polled at the same rate until the judging time: the selected uid is a hot field.)</summary>
    public bool Polling(long now) { return (effect.Armed && now < waitUntil) || (itemPending && curSel == selBefore && now - clickAt < JudgeMs); }
    // the durations of the step (the host's log: "step ms: slot=… pick=… selected=… confirm=…"; - = not part of this step)
    long movingSince; public const int MovingCapMs = 3000;
    // a click on the target's live cell that hit nothing (the game's raycast 0x0): the grid is read again (new cell and scroll-rect addresses) and the click goes to the updated rect; the same rect after it = a layer over the list
    int regrids; bool regridPending; double missX, missY; long regridAt;
    public const int MaxRegrids = 3, RegridWaitMs = 2500;
    /// <summary>The pilot asks the host for a full re-read of the gear grid (counts up); the host answers with AutoView.RegridDone.</summary>
    public int RegridSeq { get; private set; }
    /// <summary>The pilot found the click's rect unchanged after a regrid with the pointer hitting nothing: counts up, BlockerNote says what; the host looks for the layer over the list and closes it.</summary>
    public int BlockerSeq { get; private set; }
    public string BlockerNote = "";
    /// <summary>The host reads the game's pointer now: a click on a live cell awaits its judgement.</summary>
    public bool WantRay { get { return itemPending; } }
    long goalAt, pickAt, selAt, confirmAt, slotMs = -1; bool stepSaid;
    void StepSay(long now) {
      if (stepSaid || goalAt == 0) return;
      stepSaid = true;
      Func<long, string> ms = d => d < 0 ? "-" : d.ToString();
      long pick = selAt > 0 ? selAt - goalAt : -1, selected = selAt > 0 && confirmAt > 0 ? confirmAt - selAt : -1, confirm = confirmAt > 0 ? now - confirmAt : -1;
      StepLine = "step ms: slot=" + ms(slotMs) + " pick=" + ms(pick) + " selected=" + ms(selected) + " confirm=" + ms(confirm);
      Trace = Trace != "" ? Trace + "; " + StepLine : StepLine;
    }
    /// <summary>The last finished step's durations (see above); "" = none yet.</summary>
    public string StepLine = "";
    // what a click of this pilot changes: the selection, the slot open, the tip, the list (its length)
    static string Key(AutoView v) { return v.Sel + "|" + v.SlotOpen + "|" + v.TipShown + "|" + (v.Types != null ? v.Types.Length : -1) + "|" + (v.LiveGrid ? v.LiveFirst + ":" + v.LiveLast + ":" + (double.IsNaN(v.LiveCellY) ? "-" : ((int)Math.Round(v.LiveCellY / 3)).ToString()) : ""); }
    long curSel, nowMs, clickAt; bool listSent, curTip, tipAtClick;
    bool softRegrid;   // the regrid was asked for by a first click with nothing selected and no card, not by the pointer's raycast 0x0: the same rect after it is clicked again, no layer is looked for
    int counted;   // what the last list click was counted in: 1 clicks, 2 clicks and afterClicks (NotSent takes it back)
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
