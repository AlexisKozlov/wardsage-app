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
// What it does NOT click yet - these stay hints (the text for each step exists): the Foundry button in the inventory, the item
// card's «Вознесение» / «Преобразование» button, the Foundry's tabs and its list. Their places on the screen are not measured,
// and the card's left button changes its meaning with the item (Enhance / Ascend / Gems), so a guessed click could press the
// wrong thing. When a place is measured, add its step to LocateAuto.Decide.
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
  }

  public sealed class LocateMove {
    public AutoAction Action = AutoAction.Nothing;
    /// <summary>work | paused | background | hint | stopped</summary>
    public string State = "hint";
    /// <summary>Why: for "hint" the step's reason (foundry, form, no_city, hidden), for "stopped" the abort reason (bulk, lost, stuck, screen, cancelled).</summary>
    public string Why = "";
  }

  public static class LocateAuto {
    /// <summary>The auto step needs the city's «Инвентарь» button looked for (a screen grab): only then.</summary>
    public static bool NeedsCityButton(LocateStep st, bool heroScreenOpen, bool otherFormOpen) {
      return st != null && st.Kind == LocateKind.OpenInventory && !heroScreenOpen && !otherFormOpen;
    }

    /// <summary>What to do at this step. <paramref name="why"/> explains a Hint or Stop.</summary>
    public static LocateTodo Decide(LocateView v, out string why) {
      why = "";
      var st = v.Step;
      if (st == null || st.Kind == LocateKind.None) return LocateTodo.Nothing;
      switch (st.Kind) {
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
        default:
          // Selected (the Foundry button / the card's button), the Foundry's tabs and lists, the gem screen, the end:
          // the places are not measured, or it is the player's last press
          why = "foundry"; return LocateTodo.Hint;
      }
    }
  }

  public sealed class LocatePilot {
    public const int UserPauseMs = 4000, AfterBackMs = 1800, AfterButtonMs = 2500, AfterTabMs = 1500, SettleMs = 700;
    public const int MaxStepClicks = 4, MaxActions = 90, MaxClicks = 14, MaxDrags = 45, MaxWorkMs = 5 * 60 * 1000;

    readonly HeroGeometry hg = new HeroGeometry();
    readonly SellPilot list = new SellPilot(SellGeometry.Inventory(1920, 1009, 8));
    double geoW, geoH; int geoCols;
    string latched = "";            // abort reason, "" = running
    long waitUntil, pauseUntil, workSince;
    LocateTodo kind; int kindClicks, actions, clicks, drags;
    /// <summary>What the last step did and why (the app's log).</summary>
    public string Trace = "";
    /// <summary>The item the list pilot is after (set by the host with the request).</summary>
    public long ItemUid;

    /// <summary>A new request (or the player stopped it): start over.</summary>
    public void Reset() {
      latched = ""; waitUntil = pauseUntil = workSince = 0; kind = LocateTodo.Nothing; kindClicks = actions = clicks = drags = 0;
      list.Reset(); geoW = geoH = 0; geoCols = 0; Trace = "";
    }

    /// <summary>The overlay's cancel button: no more auto clicks for this request (the hints go on).</summary>
    public void Cancel() { if (latched == "") latched = "cancelled"; }

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
      m.State = "work";
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

    LocateMove Abort(LocateMove m, string why) {
      latched = why; m.State = "stopped"; m.Why = why; m.Action = AutoAction.Nothing;
      Say("stop: " + why);
      return m;
    }

    void Say(string t) { Trace = Trace == "" ? t : Trace + "; " + t; }
  }
}
