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
// inventory's or the hero gear screen's), the Foundry's «Вознесение» tab, the slot tab of the Ascension list and the item's cell
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
    FoundryButton,     // «Литейная» (the inventory's or the hero gear screen's)
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

    readonly HeroGeometry hg = new HeroGeometry();
    readonly SellPilot list = new SellPilot(SellGeometry.Inventory(1920, 1009, 8));
    double geoW, geoH; int geoCols;
    string latched = "";            // abort reason, "" = running
    long waitUntil, pauseUntil, workSince;
    LocateTodo kind; int kindClicks, actions, clicks, drags;
    double dragFrom = double.NaN; int dragStill;   // the foundry list's scroll before the last drag, drags in a row that moved nothing
    /// <summary>What the last step did and why (the app's log).</summary>
    public string Trace = "";
    /// <summary>The item the list pilot is after (set by the host with the request).</summary>
    public long ItemUid;

    /// <summary>A new request (or the player stopped it): start over.</summary>
    public void Reset() {
      latched = ""; waitUntil = pauseUntil = workSince = 0; kind = LocateTodo.Nothing; kindClicks = actions = clicks = drags = 0;
      list.Reset(); geoW = geoH = 0; geoCols = 0; Trace = ""; dragFrom = double.NaN; dragStill = 0;
    }

    /// <summary>The overlay's cancel button: no more auto clicks for this request (the hints go on).</summary>
    public void Cancel() { if (latched == "") latched = "cancelled"; }

    /// <summary>The click gate stopped the pilot (src/ClickGate.cs): no more auto clicks for this request, the hints go on.</summary>
    public void Halt(string why) { if (latched == "") latched = why; }

    public bool Stopped { get { return latched != ""; } }
    /// <summary>Why it stopped ("" while it runs): bulk, lost, stuck, screen, cancelled.</summary>
    public string Reason { get { return latched; } }

    public LocateMove Step(LocateView v) {
      var m = new LocateMove();
      if (latched != "") { m.State = "stopped"; m.Why = latched; return m; }
      string why;
      var todo = LocateAuto.Decide(v, out why);
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
      actions++; waitUntil = v.NowMs + waitMs;
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
        if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
        if (++clicks > MaxClicks) return Abort(m, "lost");
        return Click(m, v, f.ClickX, f.ClickY, AfterTabMs, "cell");
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

    LocateMove Abort(LocateMove m, string why) {
      latched = why; m.State = "stopped"; m.Why = why; m.Action = AutoAction.Nothing;
      Say("stop: " + why);
      return m;
    }

    void Say(string t) { Trace = Trace == "" ? t : Trace + "; " + t; }
  }
}
