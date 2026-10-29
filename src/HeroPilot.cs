// RealmForge — the auto-pilot brings up the plan's hero on the game's hero screen before the gear: the gear list is closed
// with the back arrow (it covers the hero grid), the hero is found in the grid and clicked, and the right-hand tab is
// switched to «Снаряжение». Only for a plan the player started («Надеть»): a player looking through heroes is left alone.
//
// Pure decision logic, no Windows APIs (tests: tests/CoreTests.cs). The host (app/Overlay.cs) tells it every tick what
// the game's memory says (RFX.ReadHeroScreen: the hero shown, the grid order, the open tab and gear list) and performs
// the click or drag returned.
//
// Where the grid is scrolled is not in memory, so every click teaches it: a click that brings up hero i puts the top of
// i's row between the cursor and one card height above it. The pilot keeps the interval of row 1's top that all clicks
// so far agree with, clicks where the plan's hero should be, and drags the grid (as a player does) when it is out of view.
// A wrong click only shows another hero.
using System;

namespace RealmForge {
  /// <summary>The hero screen, measured on the live game (1920×1009 client): x from the left edge (the grid) or the right
  /// edge (the tabs), y from the top, all in fractions of the client height.</summary>
  public sealed class HeroGeometry {
    public double Col1X = 0.0966, PitchX = 0.1279;     // centre of the grid's first column, column step
    public double CardH = 0.1625, PitchY = 0.1856;     // card height, row step (big cards)
    public double SmallCardH = 0.097, SmallPitchY = 0.1259;   // small cards (the same columns; LineSize 85 instead of 125)
    public double Row1Top = 0.1645;                    // row 1's top with the grid at the top
    public double ViewTop = 0.158, ViewBottom = 0.910; // visible part of the grid
    public double BackX = 0.0515, BackY = 0.0367;      // back arrow, top left (closes the gear list)
    public double GearTabDX = 0.0862, GearTabY = 0.334; // «Снаряжение» tab, x from the right edge
    // «Герои» of the main city's bottom bar (the bar is anchored to the bottom right; it wraps on narrow windows):
    // x from the right edge, y from the bottom (1600×1000 and 1920×1009 measured)
    public double HeroesBtnDX = 0.5222, HeroesBtnDY = 0.1389;
    // the grid's filter: its button under the grid (left edge, from the bottom) and the pop-up's class / faction columns
    // (left / top), rows from «Все» down, 7 visible without scrolling (1920×1009 measured)
    public double FunnelX = 0.0634, FunnelDY = 0.0505;
    public double ClassX = 0.0942, FactionX = 0.321, FilterRow0 = 0.2379, FilterPitch = 0.1004;
    public const int FilterRows = 7;
    /// <summary>Class ids of the class column after «Все», in its order (by id; Tactician only when the game shows it).</summary>
    public static readonly long[] ClassOrder = { 1, 2, 4, 8, 16, 256 };
    public const int Columns = 3;
    public const int GearTab = 3;                      // CharactorMainPanelType.Equip

    public double ColX(int col) { return (Col1X + col * PitchX); }   // col 0-based
  }

  /// <summary>What the host sees this tick.</summary>
  public sealed class HeroView {
    public long NowMs;
    public bool Foreground, UserBusy, UserClicked;
    public double W, H;            // H: the UI unit (Ui.Unit), the scale of every size and distance from the top / left
    public double ClientH;         // the client height (the grid's bottom is anchored there); 0 = H
    public long Plan;              // the plan's hero; 0 = no started plan (the pilot does nothing)
    public long Hero;              // hero the game shows; 0 = the hero screen is closed
    public int Tab = -1, Part = -1;
    public bool SmallCards;
    public long[] Heroes;          // the grid in display order
    public bool HeroesButton;      // the city's «Герои» button is on the screen, nothing over it (checked on the pixels)
    // the grid's filter (HeroScreen) and the plan hero's class and factions (app/res/hero_tags.json; 0 / empty = unknown)
    public bool FilterOpen, ClassChosen;
    public long[] FactionList, ChosenCamps;
    public int ClassCount;
    public ulong GridPtr;
    public long HeroClass;
    public long[] HeroCamps;
  }

  public sealed class HeroPilot {
    // MaxClicks / MaxDrags count the tries WITHOUT progress (every click that brings the plan's hero closer starts them anew, a long
    // grid takes dozens of drags); MaxTotal is the hard stop
    public const int MaxClicks = 24, MaxDrags = 30, MaxTotal = 240, MaxBack = 3, MaxTab = 3, MaxCity = 2;
    // a failed hero is tried again after a pause (something may have been over the grid meanwhile), this many times
    public const int RetryMs = 6000, MaxRetries = 2;
    public const int FarRows = 5;   // the hero further down than this: filter the grid first
    public const int FilterClickMs = 1000;   // after a class / faction click: the game applies it before the pop-up closes
    public const int AfterClickMs = 500, AfterDragMs = 700, AfterBackMs = 900, UserPauseMs = 4000;   // each click is checked in memory
    public const double MinDragPx = 24;
    readonly HeroGeometry g;
    long plan = -1, waitUntil, pauseUntil, failedAt;
    int retries;
    int clicks, drags, backs, tabs, misses, city, total, bestDist = int.MaxValue;
    // the grid filter: 0 not started, 1 filtering, 2 done (or given up); clearing a filter that hides the hero
    int filterPhase, funnelTries, factionTries, clearTries, classResets, doneCloses; bool classClicked, factionDone, classDone;
    ulong lastGrid;
    // row 1's top (client px) lies in [lo, hi]; has = known
    bool has; double lo, hi;
    double pitchF = 0.1856, cardF = 0.1625;   // the grid's layout now (big or small cards)
    // the last click, checked on the next tick: where, and who was shown before it
    bool pending; double clickY; long heroBefore;
    /// <summary>idle | work | done | paused | background | noscreen (the hero screen is closed) | notfound (the hero is
    /// not in the grid: its filter) | failed</summary>
    public string State = "idle";
    /// <summary>Why the pilot stopped, when it is a stop worth a journal line (the host appends it to the hero line).</summary>
    public string Why = "";

    public HeroPilot(HeroGeometry g) { this.g = g; }

    /// <summary>Start over on the next step (the player pressed «Надеть» again).</summary>
    public void Reset() { plan = -1; }

    /// <summary>The click or drag to make, <see cref="AutoAction.Nothing"/> to wait, or null: the hero screen shows the
    /// plan's hero with the gear tab (or there is nothing to do), the gear pilots go on.</summary>
    public AutoAction Step(HeroView v) {
      if (v.Plan != plan) { plan = v.Plan; Restart(); retries = 0; }
      if (State == "failed" && failedAt == 0) failedAt = v.NowMs;
      if (State == "failed" && retries < MaxRetries && v.NowMs - failedAt >= RetryMs) { retries++; Restart(); }
      if (v.Plan <= 0) { State = "idle"; return null; }
      if (pending) Learn(v);
      bool heroOk = v.Hero == v.Plan, tabOk = v.Tab == HeroGeometry.GearTab || v.Tab < 0;
      // (the grid filter set for the hero stays: back to «Все» the game would select another hero - live test 2026-09-26;
      // a filter hiding the next plan's hero is cleared then, FilterStep)
      if (heroOk && tabOk) {
        // the grid's filter pop-up still open (its «Все» / class click brought up the plan's hero, the game selects the first
        // one of the new grid): it lies over the hero's gear slots and takes their clicks - closed by the funnel first (live
        // 1.6.66: done with filter=open, then "the slot does not open after 3 clicks"). One click: a second would open it again.
        if (v.FilterOpen && v.Part < 0 && doneCloses < 1) {
          if (!v.Foreground || v.UserBusy) return AutoAction.Nothing;
          if (v.NowMs < waitUntil) return AutoAction.Nothing;
          doneCloses++; State = "work"; waitUntil = v.NowMs + AfterClickMs + 700;   // it fades out
          return Funnel(v, v.H);
        }
        if (doneCloses > 0 && v.NowMs < waitUntil) return AutoAction.Nothing;   // (its fade: no slot click through it)
        if (State != "idle") State = "done"; return null;
      }
      if (State == "failed" || State == "notfound" || State == "small") return AutoAction.Nothing;
      // the hero screen is closed: from the main city its «Герои» button opens it (only when the button is seen with
      // nothing over it - a pop-up over the city dims it); elsewhere the player is asked
      if (v.Hero != 0) city = 0;   // the hero screen was reached: the next time in the city starts afresh
      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;
      if (!v.Foreground) { State = "background"; return AutoAction.Nothing; }   // (the «Герои» button is seen only with the game in front)
      if (v.Hero == 0 && (!v.HeroesButton || city >= MaxCity)) { State = "noscreen"; return AutoAction.Nothing; }
      if (v.UserBusy || v.NowMs < pauseUntil) { State = "paused"; return AutoAction.Nothing; }
      State = "work";
      if (v.NowMs < waitUntil) return AutoAction.Nothing;
      double H = v.H;
      if (v.Hero == 0) {
        city++; waitUntil = v.NowMs + 3000;
        double chh = v.ClientH > 0 ? v.ClientH : H;
        return Click(v.W - g.HeroesBtnDX * H, chh - g.HeroesBtnDY * H);
      }

      if (heroOk) {                                   // the right hero on another tab: open «Снаряжение»
        if (++tabs > MaxTab) return Fail("the gear tab does not open after " + MaxTab + " clicks (tab " + v.Tab + ")");
        waitUntil = v.NowMs + AfterBackMs;
        return Click(v.W - g.GearTabDX * H, g.GearTabY * H);
      }
      if (v.Part >= 0) {                              // the gear list covers the grid: back to the grid
        if (++backs > MaxBack) return Fail("the gear list does not close after " + MaxBack + " back clicks (part " + v.Part + ")");
        has = false; waitUntil = v.NowMs + AfterBackMs;
        return Click(g.BackX * H, g.BackY * H);
      }
      pitchF = v.SmallCards ? g.SmallPitchY : g.PitchY; cardF = v.SmallCards ? g.SmallCardH : g.CardH;
      if (v.GridPtr != lastGrid) {   // sorted / filtered again: the game shows the new grid from its top
        bool first = lastGrid == 0; lastGrid = v.GridPtr;
        if (!first) { lo = hi = g.Row1Top * H; lo -= 4; hi += 4; has = true; misses = 0; }
      }
      // a grid that fits on the screen cannot scroll: its rows are where they are at the top
      if (!has && v.Heroes != null) {
        double ch0 = v.ClientH > 0 ? v.ClientH : H;
        int rowsN = (v.Heroes.Length + HeroGeometry.Columns - 1) / HeroGeometry.Columns;
        if (g.Row1Top * H + rowsN * pitchF * H <= ch0 - (1 - g.ViewBottom) * H) { lo = hi = g.Row1Top * H; lo -= 4; hi += 4; has = true; }
      }
      var fa = FilterStep(v, H);
      if (fa != null) return fa;
      if (v.Heroes == null || v.Heroes.Length == 0) return AutoAction.Nothing;
      int t = Array.IndexOf(v.Heroes, v.Plan);
      if (t < 0) { State = "notfound"; return AutoAction.Nothing; }
      if (v.FilterOpen) { waitUntil = v.NowMs + AfterClickMs + 700; return Funnel(v, H); }   // (the pop-up covers the grid; it fades out)

      double ch = v.ClientH > 0 ? v.ClientH : H;
      double pitch = pitchF * H, card = cardF * H, top = g.ViewTop * H, bottom = ch - (1 - g.ViewBottom) * H, mid = (top + bottom) / 2;
      if (!has) {                                     // nothing known: a card near the middle tells where the rows are
        if (clicks >= MaxClicks || total >= MaxTotal) return Fail("no click near the grid's middle brings up another hero (" + clicks + " clicks, " + misses + " misses)");
        double y = mid + (misses % 3 == 1 ? 0.5 : misses % 3 == 2 ? -0.5 : 0) * pitch;
        // not the shown hero's column: a click on its own card changes nothing (live 27.09: Элизия in the middle column
        // at the middle of the grid took all 24 clicks)
        int sj = Array.IndexOf(v.Heroes, v.Hero), col = sj >= 0 && sj % HeroGeometry.Columns == 1 ? 0 : 1;
        return Pick(v, g.ColX(col) * H, y);
      }
      double row1 = (lo + hi) / 2, cy = row1 + (t / HeroGeometry.Columns) * pitch + card / 2;
      if (cy >= top + card * 0.3 && cy <= bottom - card * 0.3) {
        if (clicks >= MaxClicks || total >= MaxTotal) return Fail("the plan's hero at place " + t + " is not brought up by " + clicks + " clicks");
        return Pick(v, g.ColX(t % HeroGeometry.Columns) * H, cy);
      }
      // out of view: drag the grid so the row comes to the middle (the content follows the pointer)
      if (drags >= MaxDrags || total >= MaxTotal) return Fail("the plan's hero at place " + t + " is not reached after " + drags + " drags");
      // the pointer may leave the grid while dragging (the game keeps following it): up to near the window's edge
      double maxUp = bottom - card * 0.3 - 0.03 * H, maxDown = ch - 0.03 * H - (top + card * 0.3);
      double dy = Math.Max(-maxUp, Math.Min(maxDown, mid - cy));
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      double y0 = dy < 0 ? bottom - card * 0.3 : top + card * 0.3;
      drags++; total++; waitUntil = v.NowMs + AfterDragMs;
      // the grid moved by about dy; the ends of the grid stop it, so the next click checks
      lo += dy; hi += dy; double slack = Math.Abs(dy) * 0.08 + 6; lo -= slack; hi += slack;
      if (hi - lo > 2.5 * card) has = false;
      return new AutoAction { Kind = AutoKind.Drag, X = g.ColX(1) * H, Y = y0, DY = dy };
    }

    // The game's grid filter (the funnel under the grid): the plan hero far down -> its faction and class, so it is in
    // the first rows; the hero hidden by a filter (the player's, an earlier plan's) -> «Все» in both columns. Every click
    // is checked in memory: the chosen factions are readable, the class is not (config objects) - a wrong class shows as
    // the hero gone from the grid, then «Все» undoes it. Null: nothing to do with the filter now.
    AutoAction FilterStep(HeroView v, double H) {
      if (v.Heroes == null) return null;
      int t = Array.IndexOf(v.Heroes, v.Plan);
      bool active = (v.ChosenCamps != null && v.ChosenCamps.Length > 0) || v.ClassChosen;
      if (t < 0 && active) {                                       // hidden by a filter: clear it
        if (classClicked && !classDone) { classDone = true; }      // our class click hid it: «Все» below undoes it
        if (clearTries >= 3) return null;
        if (!v.FilterOpen) { if (++funnelTries > 4) return null; waitUntil = v.NowMs + AfterClickMs; return Funnel(v, H); }
        clearTries++; waitUntil = v.NowMs + AfterClickMs;
        return v.ClassChosen ? Cell(g.ClassX, 0, H) : Cell(g.FactionX, 0, H);
      }
      if (t < 0 || filterPhase == 2) return null;
      bool known = v.HeroClass > 0 || (v.HeroCamps != null && v.HeroCamps.Length > 0);
      if (filterPhase == 0 && (!known || !FarFromView(v, t))) return null;
      filterPhase = 1;
      if (!v.FilterOpen) {
        // the pop-up was never seen after the funnel clicks: a grid click now could land inside it (a wrong class filter),
        // so stop here - a misdetection costs a stop, never a click
        if (++funnelTries > 3) { filterPhase = 2; return Fail("gear filter pop-up not recognised after " + (funnelTries - 1) + " funnel clicks - stopping, no grid click"); }
        waitUntil = v.NowMs + AfterClickMs; return Funnel(v, H);
      }
      if (!factionDone) {
        int row = FactionRow(v);
        if (row < 1 || factionTries >= 2) factionDone = true;
        else if (Array.IndexOf(v.ChosenCamps ?? new long[0], v.FactionList[row]) >= 0) factionDone = true;
        else { factionTries++; waitUntil = v.NowMs + FilterClickMs; return Cell(g.FactionX, row, H); }
      }
      if (!classDone) {
        int row = ClassRow(v);
        if (row < 1 || classClicked) classDone = true;
        // a class chosen already (the player's): «Все» first - a class clicked then would be added to it, not replace it
        else if (v.ClassChosen) { if (++classResets > 2) classDone = true; else { waitUntil = v.NowMs + AfterClickMs; return Cell(g.ClassX, 0, H); } }
        else { classClicked = true; waitUntil = v.NowMs + FilterClickMs; return Cell(g.ClassX, row, H); }
      }
      filterPhase = 2; waitUntil = v.NowMs + AfterClickMs + 700;   // it fades out: no grid click through it meanwhile
      return Funnel(v, H);                                         // close the pop-up: the grid is filtered
    }

    // The hero is far from where the grid is scrolled now. The game keeps the grid's scroll when the gear list closes, so the
    // hero shown (the last plan's) says where the view is: hero 5 of 720 is far from a grid left at place 233, not near
    // (live 1.6.62: dozens of drags did not get there). Without the shown hero in the grid the view is taken at the top.
    static bool FarFromView(HeroView v, int t) {
      int j = v.Hero > 0 ? Array.IndexOf(v.Heroes, v.Hero) : -1;
      int from = j >= 0 ? j / HeroGeometry.Columns : 0;
      return Math.Abs(t / HeroGeometry.Columns - from) >= FarRows;
    }

    int FactionRow(HeroView v) {
      if (v.FactionList == null || v.HeroCamps == null) return -1;
      foreach (var c in v.HeroCamps) { int i = Array.IndexOf(v.FactionList, c); if (i >= 1 && i < HeroGeometry.FilterRows) return i; }
      return -1;
    }

    int ClassRow(HeroView v) {
      if (v.HeroClass <= 0 || v.ClassCount < 2) return -1;
      int i = Array.IndexOf(HeroGeometry.ClassOrder, v.HeroClass);
      if (i < 0 || i + 1 >= v.ClassCount || i + 1 >= HeroGeometry.FilterRows) return -1;   // not in the column (Tactician hidden)
      return i + 1;
    }

    AutoAction Funnel(HeroView v, double H) {
      double ch = v.ClientH > 0 ? v.ClientH : H;
      return Click(g.FunnelX * H, ch - g.FunnelDY * H);
    }

    AutoAction Cell(double x, int row, double H) { return Click(x * H, (g.FilterRow0 + row * g.FilterPitch) * H); }

    // a click brought up hero i (or nobody new): the top of i's row is in [y - card, y]
    void Learn(HeroView v) {
      pending = false;
      // a gap, the hero already shown, or still loading: twice in a row means the rows are not where they were thought
      // (the hero already shown: its own card or a gap between cards - which one is not known, so nothing is learnt)
      if (v.Heroes == null || v.Hero == heroBefore) { if (++misses >= 2) has = false; return; }
      int i = Array.IndexOf(v.Heroes, v.Hero); if (i < 0) return;
      misses = 0;
      // closer to the plan's hero than any click before: the way is being made, the tries start anew
      int pi = Array.IndexOf(v.Heroes, v.Plan);
      if (pi >= 0) {
        int dist = Math.Abs(i / HeroGeometry.Columns - pi / HeroGeometry.Columns);
        if (dist < bestDist) { bestDist = dist; clicks = 0; drags = 0; }
      }
      double pitch = pitchF * v.H, card = cardF * v.H, off = (i / HeroGeometry.Columns) * pitch;
      double nlo = clickY - card - off, nhi = clickY - off;
      if (has && Math.Max(lo, nlo) <= Math.Min(hi, nhi)) { lo = Math.Max(lo, nlo); hi = Math.Min(hi, nhi); }
      else { lo = nlo; hi = nhi; has = true; }
    }

    AutoAction Pick(HeroView v, double x, double y) {
      clicks++; total++; pending = true; clickY = y; heroBefore = v.Hero; waitUntil = v.NowMs + AfterClickMs;
      return Click(x, y);
    }

    void Restart() {
      waitUntil = pauseUntil = failedAt = 0; clicks = drags = backs = tabs = misses = city = total = 0; bestDist = int.MaxValue; has = pending = false; State = "idle";
      Why = ""; filterPhase = funnelTries = factionTries = clearTries = classResets = doneCloses = 0; classClicked = factionDone = classDone = false; lastGrid = 0;
    }

    AutoAction Fail(string why) { State = "failed"; Why = why; return AutoAction.Nothing; }

    /// <summary>What the pilot saw and spent, for the error report: the plan's and the shown hero's places in the grid, the tab,
    /// the gear list, the grid filter pop-up, the tries and where it thinks row 1 is.</summary>
    public string Detail(HeroView v) {
      if (v == null) return "-";
      int pi = v.Heroes != null ? Array.IndexOf(v.Heroes, v.Plan) : -2, si = v.Heroes != null ? Array.IndexOf(v.Heroes, v.Hero) : -2;
      return "state " + State + " plan@" + pi + " shown@" + si + " grid " + (v.Heroes != null ? v.Heroes.Length : -1) + " tab " + v.Tab + " part " + v.Part
        + " filterOpen " + v.FilterOpen + " small " + v.SmallCards + " clicks " + clicks + " drags " + drags + " backs " + backs + " tabs " + tabs
        + " misses " + misses + " total " + total + " filterPhase " + filterPhase + " funnel " + funnelTries + " doneCloses " + doneCloses
        + " row1 " + (has ? (int)lo + ".." + (int)hi : "unknown") + " retries " + retries;
    }

    static AutoAction Click(double x, double y) { return new AutoAction { Kind = AutoKind.Click, X = x, Y = y }; }
  }
}

namespace RealmForge {
  /// <summary>The hero grid's filter pop-up on a grabbed frame: a dark strip left of the class check boxes, the Faction|Filter
  /// divider, and a second mark that grid lines cannot fake: a blue selected cell of the class / faction columns or,
  /// on older layouts, the Class|Faction divider. Pure: the pixels come from <c>grab(x, y, w, h)</c> in client px.</summary>
  public static class HeroFilterLook {
    public static bool IsBlueRow(int c) {
      int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
      return b >= r + 25 && b >= g + 10 && b >= 70 && r < 140;
    }

    public static bool Seen(Func<int, int, int, int, int[]> grab, double U) {
      int x0 = (int)(0.030 * U), x1 = (int)(0.058 * U), y0 = (int)(0.30 * U), y1 = (int)(0.87 * U);
      int[] px = grab(x0, y0, x1 - x0, y1 - y0);
      if (px == null) return false;
      int dark = 0;
      foreach (int c in px) {
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        if ((r + g + b < 170 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 45) || IsBlueRow(c)) dark++;
      }
      if (dark < 0.85 * px.Length) return false;
      // a dark strip is not enough: a filtered grid of not-owned heroes (dark empty cards) looks the same, and a click on the
      // funnel then OPENS the pop-up again. Its own marks (measured at 1920×1009 and on 1.6.46 at 526×296): the thin light line
      // between Фракция | Фильтр (x 0.4836·U), and the blue «Все» cell of the class column at row 0 (y 0.2379·U) or the
      // Класс | Фракция line (x 0.2557·U; absent on 1.6.46)
      if (!DividerSeen(grab, (int)(0.4836 * U), (int)(0.16 * U), (int)(0.87 * U))) return false;
      return BlueCellSeen(grab, U) || DividerSeen(grab, (int)(0.2557 * U), (int)(0.16 * U), (int)(0.87 * U));
    }

    // a selected cell: a box of the cell's blue between its check box and its text, in any row of the class or faction column
    // («Все» when nothing is chosen, the chosen class / faction otherwise). Calibrated on the 1.6.46 recording at 526×296: the
    // pop-up open 0.87 and more, the closed grid 0.51 at most
    public static bool BlueCellSeen(Func<int, int, int, int, int[]> grab, double U) {
      foreach (double[] col in new[] { new[] { 0.12, 0.17 }, new[] { 0.35, 0.385 } }) {
        for (int r = 0; r < 7; r++) {
          int x0 = (int)(col[0] * U), x1 = (int)(col[1] * U), yc = (int)((0.2379 + r * 0.1004) * U), hh = (int)(0.03 * U);
          int[] px = grab(x0, yc - hh, x1 - x0, 2 * hh);
          if (px == null || px.Length == 0) continue;
          int n = 0; foreach (int c in px) if (IsBlueRow(c)) n++;
          if (n >= 0.7 * px.Length) return true;
        }
      }
      return false;
    }

    // a thin vertical line brighter than the pop-up around it, near x (a few pixels of play for rounding)
    public static bool DividerSeen(Func<int, int, int, int, int[]> grab, int x, int y0, int y1) {
      const int half = 12;
      int w = 2 * half + 1, h = y1 - y0;
      int[] px = h > 0 ? grab(x - half, y0, w, h) : null;
      if (px == null) return false;
      var mean = new double[w];
      for (int c = 0; c < w; c++) {
        long sum = 0; int n = 0;
        for (int r = 0; r < h; r += 3) { int v = px[r * w + c]; sum += ((v >> 16) & 255) + ((v >> 8) & 255) + (v & 255); n++; }
        mean[c] = n > 0 ? sum / (3.0 * n) : 0;
      }
      for (int c = 4; c < w - 4; c++) {
        double side = (mean[c - 4] + mean[c + 4]) / 2;
        if (mean[c] >= side + 30 && mean[c] >= mean[c - 1] && mean[c] >= mean[c + 1]) return true;
      }
      return false;
    }
  }
}
