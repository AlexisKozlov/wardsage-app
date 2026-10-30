// Wardsage - «Показать в игре» with auto click ON: the app does the navigation itself, as the equip helper and the storage
// cleanup do, up to the point where the player presses the last button. The final button («Вознести» / «Преобразовать») and
// the game's confirmation dialogs are NEVER pressed.
//
// What it clicks (every target is one the app already knows from the other auto modes; nothing is guessed):
//   - the hero screen's back arrow and the city's «Инвентарь» button (as the storage cleanup opens the inventory),
//   - the inventory's left menu «Снаряжение» (the same spot the storage cleanup uses),
//   - the item's cell in the inventory's list, scrolled to by dragging the list (SellPilot with the inventory's geometry,
//     run on a normal-mode screen: a click only selects the item here, nothing is sold). The list's position is the game's own
//     scroll reading (InvScroll); the picture is only the fallback. Not for Ascension: the Foundry starts without the item.
// With the screen geometry from the site (src/Geometry.cs, placed by src/LocateFrames.cs) it also clicks «Литейная» (the
// inventory's; from the hero gear screen it goes back to the city first), the Foundry's «Вознесение» tab, the slot tab of the Ascension list and the item's cell
// there (scrolling the list by dragging, the exact place from the game's own scroll reading). Without the geometry these stay hints.
// Recast: with the item selected in the Inventory it clicks the card's gem button («Улучшение самоцветами»; the rect is the frame's), and on the
// gem screen the list row of the wanted recast function; then it stops - «Улучшить» is the player's.
// What it NEVER clicks: «Вознести», «Преобразовать», «Улучшить» (the card's other buttons too: only the gem button is clicked, and only on the
// recast path), any confirmation, and the filter (it is only pointed at, the player sets it).
//
// Safety: it stops (State "stopped", and says why) on anything unexpected - the bulk-sale mode (a click there would toggle a
// sale), a list it cannot get to the item in, a step that does not move on; it steps back while the player uses the mouse
// and while the game is not in front; the overlay's cancel button stops it for good.
//
// Pure decision logic, no Windows APIs (tests: tests/CoreTests.cs); the host (app/Overlay.cs) reads the screens and
// performs the action returned.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum LocateTodo {
    Nothing,           // no request, or nothing the app could do
    Hint,              // the player's step (also a step whose click targets are not known)
    BackToCity,        // the hero screen is open: its back arrow
    InventoryButton,   // the city: «Инвентарь»
    EquipTab,          // the inventory is on another tab: «Снаряжение»
    PickItem,          // the item is in the inventory's list: scroll to it and click it
    FoundryButton,     // «Литейная» of the inventory (the hero gear screen's one is only pointed at: the hero screen is closed first)
    AscendTab,         // the Foundry is open on another tab: «Вознесение»
    SlotTab,           // the Ascension list: the item's slot tab (the filter itself is never touched)
    CardGem,           // recast: the selected item's card, the gem button («Улучшение самоцветами»)
    GemFunc,           // recast: the gem screen's list row of the wanted function («Преобразование» / «Продв. преобразование»)
    AscendCell,        // the item is in the Ascension list: scroll to it and click its cell (selecting only)
    Stop               // cannot go on alone
  }

  /// <summary>What the host sees this tick.</summary>
  public sealed class LocateView {
    public long NowMs;
    public bool Foreground, UserBusy, UserClicked;
    public double W, H, U;            // client size, UI unit (Ui.Unit)
    public LocateStep Step;           // LocateGuide.Next for this tick
    public InventoryScreen Inv;       // the inventory, read now; null = none
    public int Cols = 8;              // columns of the inventory's grid
    public bool HeroScreenOpen;       // the hero screen (its gear list too) is shown
    public bool OtherFormOpen;        // the Foundry or the gem screen is shown
    public bool CityButton;           // «Инвентарь» is seen in the city's bottom bar (looked for only when asked: see NeedsCityButton)
    public double InvDX = 0.5222 + 0.9316;   // its place from the right edge, UI units
    public double Row1 = double.NaN;  // row 1's top from the game's own scroll reading (InvScroll), NaN = unreadable
    public double Phase = double.NaN; // row-top phase of the inventory's rows on the screen now (client px mod the row step)
    public GameGeometry Geometry;          // the screens' geometry for the game version, null = none (the Foundry steps stay hints)
    public RecastScreen Rs;           // the Foundry, read now
    public PlanItem Item;             // the item looked for
    public string Purpose = "";       // the request's purpose: recast | ascend
    public bool[] FoundryTabs;        // which of the Foundry's tabs are shown (null = all assumed)
  }

  public sealed class LocateMove {
    public AutoAction Action = AutoAction.Nothing;
    /// <summary>work | paused | background | hint | stopped</summary>
    public string State = "hint";
    /// <summary>Why: for "hint" the step's reason (foundry, form, no_city, hidden), for "stopped" the abort reason (bulk, lost, stuck, screen, cancelled).</summary>
    public string Why = "";
    /// <summary>What the action is for (the host maps it to the click gate's target; Nothing when there is no action).</summary>
    public LocateTodo Todo = LocateTodo.Nothing;
  }

  public static class LocateAuto {
    /// <summary>The auto step needs the city's «Инвентарь» button looked for (a screen grab): only then.</summary>
    public static bool NeedsCityButton(LocateStep st, bool heroScreenOpen, bool otherFormOpen) {
      return st != null && st.Kind == LocateKind.OpenInventory && !heroScreenOpen && !otherFormOpen;
    }

    static LocateTodo Hint(out string why) { why = "foundry"; return LocateTodo.Hint; }

    /// <summary>The Locate sub-state for CityFirst.NeedsCity: the step is the inventory's (or the city's) but the Foundry / the gem screen is on
    /// top (a hint "form"): back towards the city first (ClickTarget.CityBack; the host presses it until the step's screen shows). The recast's
    /// own gem steps never count.</summary>
    public static bool NeedsCityBack(LocateView v) {
      if (v == null || v.Step == null) return false;
      var k = v.Step.Kind;
      // (a recast with the Foundry open - left on the Ascension tab by an earlier request, lab 1.6.66.377 - is the shared table's: the Foundry is
      // not in the recast's continuation set, CityFirst in src/ClickGate.cs)
      // the inventory in «Массовая продажа» (a cell click there would tick a sale): the pilot never clicks in it, so city first as well
      if (k == LocateKind.ExitBulk) return true;
      if (k != LocateKind.OpenInventory && k != LocateKind.OpenEquipTab && k != LocateKind.InList && k != LocateKind.OpenFoundry) return false;
      string why; return Decide(v, out why) == LocateTodo.Hint && why == "form";
    }

    /// <summary>What to do at this step. <paramref name="why"/> explains a Hint or Stop.</summary>
    public static LocateTodo Decide(LocateView v, out string why) {
      why = "";
      var st = v.Step;
      if (st == null || st.Kind == LocateKind.None) return LocateTodo.Nothing;
      switch (st.Kind) {
        case LocateKind.ExitBulk:
          why = "bulk"; return LocateTodo.Hint;   // «Массовая продажа»: the player leaves it, the app clicks nothing there
        case LocateKind.OpenInventory:
          if (v.OtherFormOpen) { why = "form"; return LocateTodo.Hint; }
          if (v.HeroScreenOpen) return LocateTodo.BackToCity;
          if (v.CityButton) return LocateTodo.InventoryButton;
          why = "no_city"; return LocateTodo.Hint;   // not the city (a fight, a pop-up ...): the player brings it up
        case LocateKind.OpenEquipTab:
          if (v.OtherFormOpen) { why = "form"; return LocateTodo.Hint; }
          return LocateTodo.EquipTab;
        case LocateKind.InList:
          if (v.OtherFormOpen) { why = "form"; return LocateTodo.Hint; }
          if (v.Inv == null || !v.Inv.Open || !v.Inv.EquipTab) { why = "screen"; return LocateTodo.Stop; }
          if (v.Inv.Bulk) { why = "bulk"; return LocateTodo.Stop; }   // a click would toggle a sale choice
          if (v.Cols < 1) { why = "screen"; return LocateTodo.Stop; }
          return LocateTodo.PickItem;
        case LocateKind.HiddenFilter:
          why = "hidden"; return LocateTodo.Hint;
        case LocateKind.OpenFoundry: {
          if (v.OtherFormOpen) { why = "form"; return LocateTodo.Hint; }   // the gem screen is on top: the button is not there
          var f = LocateFrames.For(st, v.Item, v.Rs, v.FoundryTabs, v.Geometry, v.W, v.H, v.U);
          if (f == null || !f.Clickable) { why = "foundry"; return LocateTodo.Hint; }
          // the hero screen covers the city: its back arrow first (an open gear list closes with it too), then the city's «Инвентарь»
          // and the inventory's «Литейная». The gear screen's own «Литейная» is never clicked (with a slot's gear list open it is not
          // there: live 2026-10-29); it stays framed for the player.
          if (v.HeroScreenOpen) return LocateTodo.BackToCity;
          if (st.OnGear ? !v.HeroScreenOpen : (v.Inv == null || !v.Inv.Open || !v.Inv.EquipTab)) { why = "screen"; return LocateTodo.Stop; }
          return LocateTodo.FoundryButton;
        }
        case LocateKind.OpenAscendTab:
        case LocateKind.AscendFilter:
        case LocateKind.AscendPick: {
          var f = LocateFrames.For(st, v.Item, v.Rs, v.FoundryTabs, v.Geometry, v.W, v.H, v.U);
          if (v.Rs == null || !v.Rs.SublimingOpen || f == null) { why = "foundry"; return LocateTodo.Hint; }
          if (st.Kind == LocateKind.OpenAscendTab) return f.Clickable ? LocateTodo.AscendTab : Hint(out why);
          if (st.Kind == LocateKind.AscendFilter) return f.Clickable ? LocateTodo.SlotTab : Hint(out why);   // not the slot: the filter is the player's
          // the item is in the list but not on the screen and the list is not narrowed to its slot: the slot tab first (it is safe
          // and shortens the list), then the scroll and the cell
          if (st.Scroll != "visible" && Array.IndexOf(st.Missing, "slot") >= 0) {
            var sf = LocateFrames.For(LocateFrames.SlotOnly(st), v.Item, v.Rs, v.FoundryTabs, v.Geometry, v.W, v.H, v.U);
            if (sf != null && sf.Clickable) return LocateTodo.SlotTab;
          }
          if (v.Rs.AscendList == null || !v.Rs.AscendList.Has) { why = "foundry"; return LocateTodo.Hint; }
          return LocateTodo.AscendCell;
        }
        case LocateKind.Selected: {
          // recast only (Ascension never gets here: the Foundry starts without the item); the card's gem button, never its other button
          if (v.Purpose == "ascend") { why = "foundry"; return LocateTodo.Hint; }
          if (v.OtherFormOpen) { why = "form"; return LocateTodo.Hint; }
          if (v.Inv == null || !v.Inv.Open || !v.Inv.EquipTab || v.Inv.Bulk || v.Item == null || v.Inv.Current != v.Item.Uid) { why = "screen"; return LocateTodo.Hint; }
          var f = LocateFrames.For(st, v.Item, v.Rs, v.FoundryTabs, v.Geometry, v.W, v.H, v.U);
          if (f == null || !f.Clickable) { why = "foundry"; return LocateTodo.Hint; }
          return LocateTodo.CardGem;
        }
        case LocateKind.GemFunction: {
          // the gem screen holds the item and the wanted recast function is not selected: its list row (the function is known from the item's quality)
          if (v.Purpose == "ascend" || st.Func == 0 || v.Rs == null || !v.Rs.GemOpen || v.Item == null || v.Rs.GemSel != v.Item.Uid || v.Rs.GemFunc == st.Func) { why = "foundry"; return LocateTodo.Hint; }
          var f = LocateFrames.GemRow(v.Geometry, v.Rs, st.Func, v.W, v.H, v.U, true);
          if (f == null || !f.Clickable) { why = "foundry"; return LocateTodo.Hint; }
          return LocateTodo.GemFunc;
        }
        default:
          // the Foundry's tabs and lists, the gem screen's item pop-up, the end: the places are not measured, or it is the player's last press
          why = "foundry"; return LocateTodo.Hint;
      }
    }
  }

  public sealed class LocatePilot {
    public const int UserPauseMs = 4000, AfterBackMs = 1800, AfterButtonMs = 2500, AfterTabMs = 1500, AfterGemMs = 1500, SettleMs = 700;
    public const int MaxStepClicks = 4, MaxActions = 90, MaxClicks = 14, MaxDrags = 45, MaxWorkMs = 5 * 60 * 1000;
    public const double MinDragPx = 24;
    /// <summary>Steps the pilot can only wait at: the Ascension list not readable (AscendLoading) is waited for up to LoadingCapMs; the item not in the
    /// Ascension list (AscendNotInList) or hidden by the inventory's filter (HiddenFilter) is a stop once it has held for SettledMs (a list that is being
    /// rebuilt after a tab press says "not in the list" for a moment). Stop reasons: loading | notinlist | hidden.</summary>
    public const int LoadingCapMs = 20000, SettledMs = 3000;

    readonly HeroGeometry hg = new HeroGeometry();
    readonly SellPilot list = new SellPilot(SellGeometry.Inventory(1920, 1009, 8));
    double geoW, geoH; int geoCols;
    string latched = "";            // abort reason, "" = running
    long waitUntil, pauseUntil, workSince;
    LocateTodo kind; int kindClicks, actions, clicks, drags;
    double dragFrom = double.NaN; int dragStill;   // the foundry list's scroll before the last drag, drags in a row that moved nothing
    // the cell click waits for two equal scroll reads (a slow PC: the list still moves after the drag - live 1.6.66.354, the cell
    // placed from a stale read, the click changed nothing); the last cell click's point and how often in a row
    double cellSy = double.NaN, cellX = double.NaN, cellY = double.NaN; int cellSame;
    public const int CellReadMs = 1000, CellSameMax = 2;
    LocateKind waitKind = LocateKind.None; long waitSince, waitSeen;   // a step the pilot can only wait at (see LoadingCapMs), since when, last seen
    /// <summary>What the last step did and why (the app's log).</summary>
    public string Trace = "";
    /// <summary>The item the list pilot is after (set by the host with the request).</summary>
    public long ItemUid;

    /// <summary>A new request (or the player stopped it): start over.</summary>
    public void Reset() {
      latched = ""; waitUntil = pauseUntil = workSince = 0; kind = LocateTodo.Nothing; kindClicks = actions = clicks = drags = 0;
      list.Reset(); geoW = geoH = 0; geoCols = 0; Trace = ""; dragFrom = double.NaN; dragStill = 0; waitKind = LocateKind.None; waitSince = waitSeen = 0;
      cellSy = cellX = cellY = double.NaN; cellSame = 0;
      unsentOk = false; unsentRun = 0; clickedAt = 0; confirmWait = false;
    }

    /// <summary>The click gate found the last cell click without effect and lets it be placed again once (ClickGate.Observe): the
    /// scroll is read twice again before the next cell click.</summary>
    public void CellAgain() { cellSy = double.NaN; waitUntil = 0; Say("the cell click changed nothing: the list is read again"); }

    /// <summary>The overlay's cancel button: no more auto clicks for this request (the hints go on).</summary>
    public void Cancel() { if (latched == "") latched = "cancelled"; }

    /// <summary>The click gate stopped the pilot (src/ClickGate.cs): no more auto clicks for this request, the hints go on.</summary>
    public void Halt(string why) { if (latched == "") latched = why; }
    /// <summary>The click gate retries a failed step (ClickGate.RetryDue): the step's clicks start over, its wait ends (the run's caps stay).</summary>
    public void StepAgain() { kindClicks = 0; waitUntil = 0; }

    public bool Stopped { get { return latched != ""; } }
    /// <summary>Why it stopped ("" while it runs): bulk, lost, stuck, screen, cancelled, loading, notinlist, hidden.</summary>
    public string Reason { get { return latched; } }

    public LocateMove Step(LocateView v) {
      var m = new LocateMove();
      if (latched != "") { m.State = "stopped"; m.Why = latched; return m; }
      string why;
      var todo = LocateAuto.Decide(v, out why);
      var sk = v.Step != null ? v.Step.Kind : LocateKind.None;
      string waitWhy = sk == LocateKind.AscendLoading ? "loading" : sk == LocateKind.AscendNotInList ? "notinlist" : sk == LocateKind.HiddenFilter ? "hidden" : "";
      if (waitWhy == "" || todo != LocateTodo.Hint) waitKind = LocateKind.None;
      else {
        if (waitKind != sk || v.NowMs - waitSeen > 1500) { waitKind = sk; waitSince = v.NowMs; }   // a gap (the pilot was not asked: the gate waited) starts over
        waitSeen = v.NowMs;
        if (v.NowMs - waitSince >= (sk == LocateKind.AscendLoading ? LoadingCapMs : SettledMs)) return Abort(m, waitWhy);
      }
      if (todo == LocateTodo.Nothing || todo == LocateTodo.Hint) { m.State = "hint"; m.Why = why; kind = LocateTodo.Nothing; kindClicks = 0; return m; }
      if (todo == LocateTodo.Stop) return Abort(m, why);

      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;
      if (!v.Foreground) { m.State = "background"; workSince = 0; return m; }
      if (v.UserBusy || v.NowMs < pauseUntil) { m.State = "paused"; workSince = 0; return m; }
      m.State = "work"; m.Todo = todo;
      if (workSince == 0) workSince = v.NowMs;
      if (v.NowMs - workSince > MaxWorkMs || actions >= MaxActions) return Abort(m, "stuck");
      if (v.NowMs < waitUntil) return m;

      if (todo != kind) { kind = todo; kindClicks = 0; }
      switch (todo) {
        case LocateTodo.BackToCity:
          if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
          return Click(m, v, hg.BackX * v.U, hg.BackY * v.U, AfterBackMs, "back");
        case LocateTodo.InventoryButton:
          if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
          return Click(m, v, v.W - v.InvDX * v.U, v.H - hg.HeroesBtnDY * v.U, AfterButtonMs, "inventory");
        case LocateTodo.EquipTab:
          if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
          return Click(m, v, 0.085 * v.U, 0.315 * v.U, AfterTabMs, "tab");   // the left menu's «Снаряжение» (the storage cleanup's spot)
        case LocateTodo.FoundryButton: case LocateTodo.AscendTab: case LocateTodo.SlotTab: {
          if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
          var f = LocateFrames.For(todo == LocateTodo.SlotTab ? LocateFrames.SlotOnly(v.Step) : v.Step, v.Item, v.Rs, v.FoundryTabs, v.Geometry, v.W, v.H, v.U);
          if (f == null || !f.Clickable) return Abort(m, "screen");
          return Click(m, v, f.ClickX, f.ClickY, todo == LocateTodo.FoundryButton ? AfterButtonMs : AfterTabMs, todo == LocateTodo.FoundryButton ? "foundry" : todo == LocateTodo.AscendTab ? "ascend tab" : "slot tab");
        }
        case LocateTodo.CardGem: case LocateTodo.GemFunc: {
          if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
          var f = todo == LocateTodo.CardGem ? LocateFrames.For(v.Step, v.Item, v.Rs, v.FoundryTabs, v.Geometry, v.W, v.H, v.U)
                                             : LocateFrames.GemRow(v.Geometry, v.Rs, v.Step.Func, v.W, v.H, v.U, true);
          if (f == null || !f.Clickable) return Abort(m, "screen");
          return Click(m, v, f.ClickX, f.ClickY, AfterGemMs, todo == LocateTodo.CardGem ? "gem button" : "gem function");
        }
        case LocateTodo.AscendCell:
          return AscendCell(m, v);
        default:
          return PickItem(m, v);
      }
    }

    LocateMove Click(LocateMove m, LocateView v, double x, double y, int waitMs, string what) {
      unsentOk = what != "cell"; unsentWait = waitUntil; unsentKind = kindClicks - 1;
      actions++; waitUntil = v.NowMs + waitMs; confirmWait = what != "cell"; clickedAt = v.NowMs;   // (a list cell: the list pilot's own waits)
      Say(what + " " + (int)x + "," + (int)y);
      m.Action = new AutoAction { Kind = AutoKind.Click, X = x, Y = y };
      return m;
    }

    // the item's cell: the storage cleanup's list pilot with this one item "wanted". On the normal screen a click selects the
    // item (the game then reports it as the current one), which is also how the pilot learns where the rows are.
    LocateMove PickItem(LocateMove m, LocateView v) {
      if (geoW != v.W || geoH != v.H || geoCols != v.Cols) { geoW = v.W; geoH = v.H; geoCols = v.Cols; list.Geometry = SellGeometry.Inventory(v.W, v.U, v.Cols); }
      var scr = v.Inv.AsSell(v.Cols);
      scr.Open = true; scr.Current = v.Inv.Current;
      var wanted = new HashSet<long> { ItemUid };
      var a = list.Step(new SellView { NowMs = v.NowMs, Foreground = v.Foreground, UserBusy = v.UserBusy, UserClicked = false, W = v.W, H = v.H, U = v.U,
                                       Screen = scr, Wanted = wanted, Phase = v.Phase, Row1 = v.Row1 });
      if (list.Trace != "") { Say(list.Trace); list.Trace = ""; }
      if (list.State == "failed" || list.State == "done") return Abort(m, "lost");   // "done" without the item selected: it is out of reach
      if (a.Kind == AutoKind.None) return m;
      actions++;
      if (a.Kind == AutoKind.Click && ++clicks > MaxClicks) return Abort(m, "lost");
      if (a.Kind == AutoKind.Drag && ++drags > MaxDrags) return Abort(m, "lost");
      waitUntil = v.NowMs + (a.Kind == AutoKind.Drag ? SettleMs + 300 : SettleMs);   // the game answers before the next look
      m.Action = a;
      return m;
    }

    // the item's cell in the Foundry's Ascension list: out of the viewport (or cut by its edge) -> drag the list toward it, the game's own
    // scroll reading says how far it still is; in view -> one click on the cell (it only selects the item; the pilot never presses
    // «Вознести»: that step is a hint). A list that does not move on drags (its end) stops the pilot.
    LocateMove AscendCell(LocateMove m, LocateView v) {
      var f = LocateFrames.For(v.Step, v.Item, v.Rs, v.FoundryTabs, v.Geometry, v.W, v.H, v.U);
      if (f == null || v.Rs == null || v.Rs.AscendList == null || f.ViewH <= 0) return Abort(m, "screen");
      double sy = v.Rs.AscendList.ScrollY;
      if (!double.IsNaN(dragFrom)) {
        if (Math.Abs(sy - dragFrom) < 0.5) { if (++dragStill >= 2) return Abort(m, "lost"); } else dragStill = 0;
        dragFrom = double.NaN;
      }
      if (f.Clickable) {
        // the list's place from two equal reads: a read that differs from the last one waits for the next
        if (double.IsNaN(cellSy) || Math.Abs(sy - cellSy) >= 0.5) { cellSy = sy; waitUntil = v.NowMs + CellReadMs; return m; }
        bool same = Math.Abs(f.ClickX - cellX) < 2 && Math.Abs(f.ClickY - cellY) < 2;
        if (same && cellSame >= CellSameMax) return Abort(m, "lost");
        cellSame = same ? cellSame + 1 : 1; cellX = f.ClickX; cellY = f.ClickY; cellSy = double.NaN;
        if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
        if (++clicks > MaxClicks) return Abort(m, "lost");
        return Click(m, v, f.ClickX, f.ClickY, AfterTabMs, "cell");
      }
      double dy = Math.Max(-0.8 * f.ViewH, Math.Min(0.8 * f.ViewH, f.ScrollPx));   // content follows the pointer: + = later rows move down
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      if (++drags > MaxDrags) return Abort(m, "lost");
      actions++; dragFrom = sy; cellSy = double.NaN;
      waitUntil = v.NowMs + SettleMs + 300;
      Say("scroll " + (int)dy);
      m.Action = new AutoAction { Kind = AutoKind.Drag, X = f.ViewX + f.ViewW / 2, Y = f.ViewY + f.ViewH / 2 - dy / 2, DY = dy };
      return m;
    }

    // a button / tab click the host could not send (the gate refused it: a pop-up or wait form came, the forms unread): taken back, so a wait
    // form over the screen does not use up the step's MaxStepClicks (at most MaxUnsent in a row; then they count again)
    bool unsentOk, confirmWait; long unsentWait, clickedAt; int unsentKind, unsentRun;
    public const int MaxUnsent = 60, SettleAfterEffectMs = 150;
    /// <summary>The last step's click was not sent: a button / tab click is taken back (list cells and drags are the list pilot's own).</summary>
    public void NotSent() {
      if (!unsentOk || ++unsentRun > MaxUnsent) { unsentOk = false; return; }
      actions--; kindClicks = Math.Max(0, unsentKind); waitUntil = unsentWait; unsentOk = false;
    }
    /// <summary>The click gate saw the effect of the last button / tab click (event confirmation): the fixed wait after it ends a short settle later.</summary>
    public void Confirmed(long now) {
      if (!confirmWait || now < clickedAt) return;
      if (waitUntil > now + SettleAfterEffectMs) waitUntil = now + SettleAfterEffectMs;
      confirmWait = false;
    }
    /// <summary>A click went out (the host sent it): the refusals in a row start over.</summary>
    public void Sent() { unsentRun = 0; unsentOk = false; }

    LocateMove Abort(LocateMove m, string why) {
      latched = why; m.State = "stopped"; m.Why = why; m.Action = AutoAction.Nothing;
      Say("stop: " + why);
      return m;
    }

    void Say(string t) { Trace = Trace == "" ? t : Trace + "; " + t; }
  }
}

namespace RealmForge {
  public enum ArtTodo {
    Nothing,      // no request
    Hint,         // the player's step (also a step the app never clicks: «Улучшить» first, the final «Продвинуть», a wrong copy chosen ...)
    ArtTab,       // the inventory's left menu «Артефакт»
    Cell,         // the artifact's cell in the normal list (selects it): the click or the drag that brings it into view
    Workshop,     // the open card's «Мастерская»
    PromoteTab,   // the workshop's tab «Продвинуть»
    MatCell,      // the planned copy / essence in the workshop's left list: the click or the drag that brings it into view
    HeroBack,     // not in the inventory yet, the hero screen is open: its back arrow
    InventoryButton   // not in the inventory yet, the city: «Инвентарь»
  }

  /// <summary>What the host sees this tick of an artifact promotion («Показать в игре», purpose artPromote).</summary>
  public sealed class ArtView {
    public long NowMs;
    public bool Foreground, UserBusy, UserClicked;
    public double W, H, U;
    public LocateStep Step;             // LocateGuide.NextArt for this tick
    public InventoryScreen Inv;         // the inventory as read now (null = not shown)
    public ArtWorkshopScreen Ws;        // the workshop as read now (null = not shown)
    public ArtPromoteInfo Art;
    public GameGeometry Geometry;       // the site's screen geometry (null: the research numbers of ArtFrames)
    public bool HeroScreenOpen;         // the hero screen is shown
    public bool OtherFormOpen;          // the Foundry or the gem screen is shown (the host presses the back arrow: NeedsCityBack)
    public bool CityButton;             // «Инвентарь» is seen in the city's bottom bar (looked for only when asked: NeedsCityButton)
    public double InvDX = 0.5222 + 0.9316;   // its place from the right edge, UI units
  }

  public sealed class ArtMove {
    public AutoAction Action = AutoAction.Nothing;
    /// <summary>work | paused | background | hint | stopped</summary>
    public string State = "hint";
    public string Why = "";
    public ArtTodo Todo = ArtTodo.Nothing;
    /// <summary>MatCell: the 1-based entry of the workshop's list the click is for.</summary>
    public int MatIndex;
    /// <summary>The action is a drag that scrolls a list (the click gate target is the list's drag).</summary>
    public bool Drag { get { return Action.Kind == AutoKind.Drag; } }
  }

  /// <summary>«Показать в игре» for an ARTIFACT promotion with the site's "artAuto" switch and the player's auto click on: the app opens the «Артефакт» tab, selects the
  /// artifact's cell (scrolling the list), opens «Мастерская», the «Продвинуть» tab and chooses the planned copy / essence. It NEVER presses «Продвинуть» or «Улучшить»
  /// (the last step is a hint; ClickGate.NeverKeys), the lock icons, or anything else. Pure decision logic (tests: tests/CoreTests.cs, tests/ScreenReplay.cs).</summary>
  public static class ArtLocateAuto {
    /// <summary>Is the entry (1-based) of the workshop's list one the plan meant: a planned copy, or an essence stub when the plan spends an essence.</summary>
    public static bool Planned(ArtPromoteInfo art, ArtWorkshopScreen ws, int index) {
      if (art == null || ws == null || index < 1 || index > ws.Mats.Count) return false;
      long u = ws.Mats[index - 1];
      if (u != 0) return art.Copies.Contains(u);
      return art.Via == "essence" && index - 1 < ws.MatItemIds.Count && ws.MatItemIds[index - 1] > 0;
    }

    /// <summary>The artifact promotion's sub-state for CityFirst.NeedsCity: on the way to the inventory with the Foundry / the gem screen on top,
    /// or the workshop open on ANOTHER artifact (an earlier request left it): back with its arrow first (ClickTarget.CityBack, the host's BackStep).</summary>
    public static bool NeedsCityBack(ArtView v) {
      if (v == null || v.Step == null) return false;
      if (v.Step.Kind == LocateKind.ArtWrongArtifact && v.Ws != null && v.Ws.Open) return true;
      // a sale / organise mode of the inventory (the equipment's or the artifact tab's): never clicked in, city first
      if (v.Step.Kind == LocateKind.ArtExitMode) return true;
      if (v.Step.Kind == LocateKind.ArtOpenTab && v.Inv != null && v.Inv.EquipTab && v.Inv.BulkKnown && v.Inv.Bulk) return true;
      return v.Step.Kind == LocateKind.OpenInventory && v.OtherFormOpen;
    }
    /// <summary>The city's «Инвентарь» has to be looked for (a screen grab): only on the way to the inventory with no screen over the city.</summary>
    public static bool NeedsCityButton(LocateStep st, bool heroScreenOpen, bool otherFormOpen) {
      return st != null && st.Kind == LocateKind.OpenInventory && !heroScreenOpen && !otherFormOpen;
    }

    public static ArtTodo Decide(ArtView v, out string why) {
      why = "";
      var st = v.Step;
      if (st == null || st.Kind == LocateKind.None) return ArtTodo.Nothing;
      var inv = v.Inv; var ws = v.Ws;
      switch (st.Kind) {
        case LocateKind.OpenInventory:
          // any screen is a start (an earlier request may have left the Foundry or the gem screen open: lab 1.6.66.377 waited here)
          if (v.OtherFormOpen) { why = "form"; return ArtTodo.Hint; }   // the host's back arrow (NeedsCityBack)
          if (v.HeroScreenOpen) return ArtTodo.HeroBack;
          if (v.CityButton) return ArtTodo.InventoryButton;
          why = "no_city"; return ArtTodo.Hint;
        case LocateKind.ArtOpenTab:
          if (inv == null || !inv.Open || inv.ArtTab) { why = "screen"; return ArtTodo.Hint; }
          if (inv.EquipTab && !inv.BulkKnown) { why = "wait"; return ArtTodo.Hint; }
          if (inv.EquipTab && inv.Bulk) { why = "bulk"; return ArtTodo.Hint; }   // leaving the equipment sale is the player's
          return ArtTodo.ArtTab;
        case LocateKind.ArtInList: {
          if (inv == null || !inv.Open || !inv.ArtTab || !inv.ArtBulkKnown || inv.ArtBulk || inv.ArtMode != -1) { why = "screen"; return ArtTodo.Hint; }
          var f = ArtFrames.For(st, inv, null, v.Geometry, v.W, v.H, v.U);
          if (f == null) { why = "list"; return ArtTodo.Hint; }
          return ArtTodo.Cell;
        }
        case LocateKind.ArtSelected:
          if (inv == null || !inv.Open || !inv.ArtTab || !inv.ArtBulkKnown || inv.ArtBulk || inv.ArtMode != -1 || !inv.ArtSelKnown || inv.ArtSel != st.Pos) { why = "screen"; return ArtTodo.Hint; }
          return ArtTodo.Workshop;
        case LocateKind.ArtOpenPromote:
          if (ws == null || !ws.Open || v.Art == null || ws.Uid != v.Art.Uid || ws.Tab == 2) { why = "screen"; return ArtTodo.Hint; }
          return ArtTodo.PromoteTab;
        case LocateKind.ArtMatPick: {
          if (ws == null || !ws.Open || ws.Tab != 2 || !ws.PanelKnown || v.Art == null || ws.Uid != v.Art.Uid) { why = "screen"; return ArtTodo.Hint; }
          if (st.MatWrong) { why = "wrong"; return ArtTodo.Hint; }                    // a wrong copy chosen: the player takes it off
          if (!Planned(v.Art, ws, st.Pos)) { why = "plan"; return ArtTodo.Hint; }     // not a copy / essence the plan meant: the player picks
          var f = ArtFrames.For(st, null, ws, v.Geometry, v.W, v.H, v.U);
          if (f == null) { why = "list"; return ArtTodo.Hint; }
          return ArtTodo.MatCell;
        }
        default:
          why = "player"; return ArtTodo.Hint;   // «Улучшить» first, the final «Продвинуть», nothing to choose ...
      }
    }
  }

  public sealed class ArtPilot {
    public const int UserPauseMs = 4000, AfterTabMs = 1500, AfterOpenMs = 2500, SettleMs = 700;
    public const int MaxStepClicks = 4, MaxActions = 90, MaxClicks = 14, MaxDrags = 45, MaxWorkMs = 5 * 60 * 1000;
    public const double MinDragPx = 24;

    readonly HeroGeometry hg = new HeroGeometry();
    string latched = "";
    long waitUntil, pauseUntil, workSince;
    ArtTodo kind; int kindClicks, actions, clicks, drags, lastMat;
    double dragFrom = double.NaN; int dragStill;
    /// <summary>What the last step did and why (the app's log).</summary>
    public string Trace = "";

    public void Reset() { latched = ""; waitUntil = pauseUntil = workSince = 0; kind = ArtTodo.Nothing; kindClicks = actions = clicks = drags = lastMat = 0; dragFrom = double.NaN; dragStill = 0; Trace = ""; }
    /// <summary>The click gate retries a failed step (ClickGate.RetryDue): the step's clicks start over, its wait ends (the run's caps stay).</summary>
    public void StepAgain() { kindClicks = 0; waitUntil = 0; }
    public void Cancel() { if (latched == "") latched = "cancelled"; }
    public void Halt(string why) { if (latched == "") latched = why; }
    public bool Stopped { get { return latched != ""; } }
    public string Reason { get { return latched; } }

    static AutoAction Center(GeoRect r) { return new AutoAction { Kind = AutoKind.Click, X = r.X + r.W / 2, Y = r.Y + r.H / 2 }; }

    public ArtMove Step(ArtView v) {
      var m = new ArtMove();
      if (latched != "") { m.State = "stopped"; m.Why = latched; return m; }
      string why;
      var todo = ArtLocateAuto.Decide(v, out why);
      if (todo == ArtTodo.Nothing || todo == ArtTodo.Hint) { m.State = "hint"; m.Why = why; kind = ArtTodo.Nothing; kindClicks = 0; return m; }
      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;
      if (!v.Foreground) { m.State = "background"; workSince = 0; return m; }
      if (v.UserBusy || v.NowMs < pauseUntil) { m.State = "paused"; workSince = 0; return m; }
      m.State = "work"; m.Todo = todo;
      if (workSince == 0) workSince = v.NowMs;
      if (v.NowMs - workSince > MaxWorkMs || actions >= MaxActions) return Abort(m, "stuck");
      if (v.NowMs < waitUntil) return m;
      if (todo != kind) { kind = todo; kindClicks = 0; }
      switch (todo) {
        case ArtTodo.HeroBack:
          if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
          return Click(m, v, new AutoAction { Kind = AutoKind.Click, X = hg.BackX * v.U, Y = hg.BackY * v.U }, AfterTabMs, "back");
        case ArtTodo.InventoryButton:
          if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
          return Click(m, v, new AutoAction { Kind = AutoKind.Click, X = v.W - v.InvDX * v.U, Y = v.H - hg.HeroesBtnDY * v.U }, AfterOpenMs, "inventory");
        case ArtTodo.ArtTab: case ArtTodo.Workshop: case ArtTodo.PromoteTab: {
          if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
          string key = todo == ArtTodo.ArtTab ? "inventory.leftTab.artifact" : todo == ArtTodo.Workshop ? "inventory.artWorkshop" : "artifact.tab.promote";
          var r = ArtFrames.Rect(v.Geometry, key, v.W, v.H, v.U);
          if (r == null) return Abort(m, "screen");
          return Click(m, v, Center(r), todo == ArtTodo.Workshop ? AfterOpenMs : AfterTabMs, key);
        }
        default:
          return Cell(m, v, todo == ArtTodo.MatCell);
      }
    }

    // a card of a list the game scrolls: out of view (or cut by the edge) -> drag the list toward it, the game's own scroll reading says how far it still is;
    // in view -> one click on its centre (it only selects / chooses). A list that does not move on drags (its end) stops the pilot.
    ArtMove Cell(ArtMove m, ArtView v, bool mat) {
      var f = mat ? ArtFrames.For(v.Step, null, v.Ws, v.Geometry, v.W, v.H, v.U) : ArtFrames.For(v.Step, v.Inv, null, v.Geometry, v.W, v.H, v.U);
      var l = mat ? (v.Ws != null ? v.Ws.MatList : null) : (v.Inv != null ? v.Inv.List : null);
      if (f == null || l == null || !l.Has || f.ViewH <= 0) return Abort(m, "screen");
      double sy = l.ScrollY;
      if (!double.IsNaN(dragFrom)) {
        if (Math.Abs(sy - dragFrom) < 0.5) { if (++dragStill >= 2) return Abort(m, "lost"); } else dragStill = 0;
        dragFrom = double.NaN;
      }
      double cardH = 110 * v.U / GameGeometry.CanvasH;
      if (f.Mark.Kind == "box" && f.Mark.H >= 0.6 * cardH) {
        if (mat && v.Step.Pos != lastMat) { lastMat = v.Step.Pos; kindClicks = 0; }
        if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
        if (++clicks > MaxClicks) return Abort(m, "lost");
        m.MatIndex = mat ? v.Step.Pos : 0;
        return Click(m, v, new AutoAction { Kind = AutoKind.Click, X = f.Mark.X + f.Mark.W / 2.0, Y = f.Mark.Y + f.Mark.H / 2.0 }, mat ? AfterTabMs : AfterOpenMs, mat ? "copy" : "artifact");
      }
      double dy = Math.Max(-0.8 * f.ViewH, Math.Min(0.8 * f.ViewH, f.ScrollPx));   // content follows the pointer: + = later rows move down
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      if (++drags > MaxDrags) return Abort(m, "lost");
      actions++; dragFrom = sy;
      waitUntil = v.NowMs + SettleMs + 300;
      Say("scroll " + (int)dy);
      m.Action = new AutoAction { Kind = AutoKind.Drag, X = f.ViewX + f.ViewW / 2, Y = f.ViewY + f.ViewH / 2 - dy / 2, DY = dy };
      return m;
    }

    ArtMove Click(ArtMove m, ArtView v, AutoAction a, int waitMs, string what) {
      actions++; waitUntil = v.NowMs + waitMs;
      Say(what + " " + (int)a.X + "," + (int)a.Y);
      m.Action = a;
      return m;
    }

    ArtMove Abort(ArtMove m, string why) {
      latched = why; m.State = "stopped"; m.Why = why; m.Action = AutoAction.Nothing;
      Say("stop: " + why);
      return m;
    }

    void Say(string t) { Trace = Trace == "" ? t : Trace + "; " + t; }
  }
}
