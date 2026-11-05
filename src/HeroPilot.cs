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
    // the hero grid's bottom bar (funnel, sort, direction): the game's m_BtnFilter, whole; measured by the live read at 1366x705 (lab 548: 3,637,275x40). The ONE layout of hero.filter (every hint path)
    public double GridBarX0 = 0.0043, GridBarW = 0.39, GridBarY0 = 0.9035, GridBarH = 0.0567;
    public GeoRect GridBarRect(double W, double H) {
      double U = Ui.Unit(W, H);
      return new GeoRect { X = Math.Round(GridBarX0 * U), Y = Math.Round(Ui.FromBottom(GridBarY0, W, H)), W = Math.Round(GridBarW * U), H = Math.Round(GridBarH * U) };
    }
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
    /// <summary>The pop-up is open. The host fills it from the pixels (HeroFilterSeen); the pilot replaces it by the memory's verdict when there is one (FilterMem).</summary>
    public bool FilterOpen, ClassChosen;
    /// <summary>The class the grid is filtered by, its id (HeroScreen.ChosenClass: the class config's m_ProfessionID, or its row in the class column); 0 = none or chosen and not known (then only ClassChosen is known). ClassSrc: where it came from.</summary>
    public long ChosenClass; public string ClassSrc = "";
    /// <summary>The pop-up as the game's script holds it (HeroScreen.FilterMem): Yes / No decide, Unknown (panel not built, flag nil) leaves the pixels' FilterOpen.</summary>
    public Mem3 FilterMem = Mem3.Unknown;
    /// <summary>FilterMem is Unknown only because the panel prefab loads / m_Panels is nil (HeroScreen.FilterPending, at most 1.5 s): the funnel is not pressed meanwhile.</summary>
    public bool FilterPending;
    /// <summary>What the host puts into FilterMem: the screen read's verdict, Unknown without one.</summary>
    public static Mem3 MemOf(HeroScreen s) { return s == null ? Mem3.Unknown : s.FilterMem; }
    /// <summary>Set by the pilot once FilterOpen has been settled against FilterMem for this view.</summary>
    public bool FilterResolved;
    public long[] FactionList, ChosenCamps;
    public int ClassCount;
    public ulong GridPtr;
    public long HeroClass;
    public long[] HeroCamps;
    /// <summary>The navigator owns the way to the hero grid (src/NavEquip.cs): the pilot does not click «Герои», the gear tab or the gear list's back arrow; it only finds the hero in the grid. Default off: the old path unchanged.</summary>
    public bool NavOwned;
    /// <summary>The game's input is open (RFX.NavInputOpen: no UILocker slot, no transition): the pilot starts no action while it is locked, instead of a fixed pause. Default true.</summary>
    public bool InputOpen = true;
  }

  public sealed class HeroPilot {
    // MaxClicks / MaxDrags count the tries WITHOUT progress (every click that brings the plan's hero closer starts them anew, a long
    // grid takes dozens of drags); MaxTotal is the hard stop
    /// <summary>NavOwned: this many grid clicks in a row that brought up no other hero (the grid takes no click) are a can't, not 24 slow tries: the navigator resets.</summary>
    public const int MaxNavMisses = 8;
    public const int MaxClicks = 24, MaxDrags = 30, MaxTotal = 240, MaxBack = 3, MaxTab = 3, MaxCity = 2;
    // a failed hero is tried again after a pause (something may have been over the grid meanwhile), this many times
    public const int RetryMs = 6000, MaxRetries = 2;
    public const int FarRows = 5;   // the hero further down than this: filter the grid first
    public const int FilterClickMs = 1000;   // after a class / faction click: the game applies it before the pop-up closes
    /// <summary>After a funnel press (it is a toggle): the pop-up has to show within this (the game's answer 0.6-1.5 s on a slow laptop plus the pop-up's own fade); not seen = no second press, the pilot gives up (notfound) and the navigator resets.</summary>
    public const int FunnelSeenMs = 2600;
    public const int AfterClickMs = 500, AfterDragMs = 700, AfterBackMs = 900, UserPauseMs = 4000;   // each click is checked in memory
    public const double MinDragPx = 24;
    // a game slower than AfterClickMs (a slow laptop: 0.6-1.5 s): a hero brought up after the click's wait ran out is a late answer, the waits grow to
    // the latest answer seen plus this margin (never above MaxLateMs); a fast PC never answers that late, its waits stay as they are
    public const int LateMarginMs = 400, MaxLateMs = 4000;
    readonly HeroGeometry g;
    long plan = -1, waitUntil, pauseUntil, failedAt;
    int retries;
    // the latest answer seen (0 = none later than AfterClickMs), the last grid click's time, the hero seen on the last tick
    long lateMs, pickAt; long seenHero;
    int clicks, drags, backs, tabs, misses, city, total, bestDist = int.MaxValue;
    // the grid filter: 0 not started, 1 filtering, 2 done (or given up); clearing a filter that hides the hero
    long funnelAt; bool funnelPressed;
    /// <summary>The filter pop-up did not show after a funnel press: it cannot be read on this screen. Kept across resets (it is about the game's pixels); the optional filtering of a far hero is skipped then.</summary>
    bool filterBlind;
    /// <summary>The funnel was pressed (a toggle) and the pop-up was not seen: it may be open over the screen, so the navigator must not trust an Escape to reach the city. Cleared by a reset of the pilot or when the pop-up is seen.</summary>
    public bool FunnelUnseen;
    int filterPhase, funnelTries, factionTries, clearTries, classResets, doneCloses; bool classClicked, factionDone, classDone, forceClear;
    ulong lastGrid;
    double dragScale = 1; int lastDragSign;   // (the game's drag gain learnt by overshoots; kept across plans, it is the game's)
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

    /// <summary>Counts what really moved the search on (a hero other than the one shown brought up by a grid pick, a pick closer to the plan's hero, a drag of the grid, a filter press): the navigator's stall clock
    /// reads it (HeroSelectAction.ProgressAt) - a pick that changes nothing is not progress, however often it is sent. Kept over Reset (a counter only).</summary>
    public int Progress;

    /// <summary>Start over on the next step (the player pressed «Надеть» again).</summary>
    /// <summary>(What the pilot learnt about the game stays: the drag gain and the latest answer time.)</summary>
    public void Reset() { plan = -1; Restart(); retries = 0; unsent = null; unsentRun = 0; }

    /// <summary>Memory first: the pop-up's state is the game's flag (FilterMem Yes / No); the pixels (FilterOpen as the host saw them) only when memory does not know.
    /// A disagreement is trusted to memory and told once per episode that lasts over 1.5 s (NavNotes: «disagree: hero filter mem=.. pix=.. for=..»).</summary>
    void ResolveFilter(HeroView v) {
      if (v.FilterResolved) return;
      v.FilterResolved = true;
      if (v.FilterMem == Mem3.Unknown) { disagree.Reset(); return; }
      bool mem = v.FilterMem == Mem3.Yes;
      if (v.Foreground) {   // (not in front: the pixels were not looked at)
        var line = disagree.Update("hero filter", mem ? "yes" : "no", v.FilterOpen ? "yes" : "no", v.NowMs);
        if (line != null) NavNotes.Add(line + " (memory trusted)");
      } else disagree.Reset();
      v.FilterOpen = mem;
    }
    readonly DisagreeTracker disagree = new DisagreeTracker();
    /// <summary>The funnel is pressed again after this many presses at most when memory says closed throughout (a press that did not reach the game); with the pop-up unreadable never.</summary>
    public const int MaxMemFunnels = 2;

    /// <summary>The grid is filtered by a faction or a class (what the memory shows).</summary>
    public static bool FilterActive(HeroView v) { return (v.ChosenCamps != null && v.ChosenCamps.Length > 0) || v.ClassChosen; }

    /// <summary>The click or drag to make, <see cref="AutoAction.Nothing"/> to wait, or null: the hero screen shows the
    /// plan's hero with the gear tab (or there is nothing to do), the gear pilots go on.</summary>
    public AutoAction Step(HeroView v) {
      ResolveFilter(v);
      if (unsent != null) unsentRun = 0;   // the last action went out
      var before = Save();
      fade = false;
      var a = StepCore(v);
      unsent = a != null && a.Kind != AutoKind.None ? before : null;
      // a click's wait ends once its effect shows (EffectWait); a pop-up's close keeps its fade, a drag its wait
      // (a grid pick: only another hero shown is its effect - a slow game's late answer to it is judged by Learn, never a grid redraw)
      if (a != null && a.Kind == AutoKind.Click && !fade) { pickKey = pending; effect.Arm(Key(v), v.NowMs); } else if (a != null && a.Kind != AutoKind.None) effect.Drop();
      return a;
    }
    readonly EffectWait effect = new EffectWait(); bool fade, pickKey;
    /// <summary>The pilot waits for its last click's effect (the host reads at its effect rate meanwhile).</summary>
    /// <summary>(Not for a grid pick: a slow game answers it after its wait, and the late answer is learnt only from reads at the usual rate.)</summary>
    public bool Polling(long now) { return effect.Armed && !pickKey && now < waitUntil; }
    // what a click of the hero pilot changes: the hero shown, the tab, the gear list, the grid filter (pop-up, factions, class, the grid)
    string Key(HeroView v) {
      if (pickKey) return v.Hero.ToString();
      return v.Hero + "|" + v.Tab + "|" + v.Part + "|" + v.FilterOpen + "|" + (v.ChosenCamps != null ? string.Join(",", Array.ConvertAll(v.ChosenCamps, x => x.ToString())) : "-") + "|" + v.ClassChosen + "|" + v.ChosenClass + "|" + v.GridPtr;
    }

    /// <summary>The action of the last Step did not go out (the gate waited, a read failed before the click): it does not count as a try, and its
    /// answer is not awaited - the pilot is as before it (an unsent «Герои» click took the city's two tries, then "noscreen").</summary>
    /// <summary>The click gate retries a failed step (ClickGate.RetryDue: a pop-up took the «Герои» press ...): the city's tries start over.</summary>
    public void StepAgain() { city = 0; waitUntil = 0; pending = false; }
    public void NotSent() { if (unsent != null && ++unsentRun <= MaxUnsent) Load(unsent); unsent = null; }
    /// <summary>Refused actions in a row taken back; beyond this they count again (a gate that never lets one out ends in the pilot's own stop).</summary>
    public const int MaxUnsent = 60;
    int unsentRun;

    sealed class Saved { public long WaitUntil, HeroBefore, PickAt, FunnelAt; public bool FunnelPressed; public int Clicks, Drags, Backs, Tabs, Misses, City, Total, BestDist, FilterPhase, FunnelTries, FactionTries, ClearTries, ClassResets, DoneCloses;
                         public bool Has, Pending, ClassClicked, FactionDone, ClassDone; public double Lo, Hi, ClickY; }
    Saved unsent;
    Saved Save() {
      return new Saved { FunnelAt = funnelAt, FunnelPressed = funnelPressed, WaitUntil = waitUntil, HeroBefore = heroBefore, PickAt = pickAt, Clicks = clicks, Drags = drags, Backs = backs, Tabs = tabs, Misses = misses, City = city, Total = total,
                         BestDist = bestDist, FilterPhase = filterPhase, FunnelTries = funnelTries, FactionTries = factionTries, ClearTries = clearTries, ClassResets = classResets,
                         DoneCloses = doneCloses, Has = has, Pending = pending, ClassClicked = classClicked, FactionDone = factionDone, ClassDone = classDone, Lo = lo, Hi = hi, ClickY = clickY };
    }
    void Load(Saved x) {
      effect.Drop(); funnelAt = x.FunnelAt; funnelPressed = x.FunnelPressed;
      waitUntil = x.WaitUntil; heroBefore = x.HeroBefore; pickAt = x.PickAt; clicks = x.Clicks; drags = x.Drags; backs = x.Backs; tabs = x.Tabs; misses = x.Misses; city = x.City; total = x.Total;
      bestDist = x.BestDist; filterPhase = x.FilterPhase; funnelTries = x.FunnelTries; factionTries = x.FactionTries; clearTries = x.ClearTries; classResets = x.ClassResets;
      doneCloses = x.DoneCloses; has = x.Has; pending = x.Pending; classClicked = x.ClassClicked; factionDone = x.FactionDone; classDone = x.ClassDone; lo = x.Lo; hi = x.Hi; clickY = x.ClickY;
    }

    AutoAction StepCore(HeroView v) {
      if (v.Plan != plan) { plan = v.Plan; Restart(); retries = 0; }
      if (State == "failed" && failedAt == 0) failedAt = v.NowMs;
      if (State == "failed" && retries < MaxRetries && v.NowMs - failedAt >= RetryMs) { retries++; Restart(); }
      if (v.Plan <= 0) { State = "idle"; return null; }
      // a click that brought up nobody new is judged when its wait has run out (a slow PC shows the new hero later than the next tick)
      // a hero that comes later than the click's wait: the game is slow. What the clicks since taught is not to be trusted (an answer taken for
      // the wrong click), the rows are found again with waits as long as the game's answers
      if (v.Hero != seenHero && v.Hero != 0 && seenHero != 0 && pickAt > 0 && v.NowMs - pickAt > AfterClickMs && v.NowMs - pickAt > lateMs) {
        long dt = v.NowMs - pickAt;
        if (dt <= MaxLateMs) { lateMs = dt; has = false; pending = false; misses = 0; waitUntil = Math.Max(waitUntil, v.NowMs + Late(AfterClickMs)); }
      }
      seenHero = v.Hero;
      if (v.NowMs < waitUntil && effect.Seen(Key(v), v.NowMs)) waitUntil = v.NowMs;   // the last click's effect seen, held still
      if (pending && (v.Hero != heroBefore || v.NowMs >= waitUntil)) Learn(v);
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
          doneCloses++; FilterDirty = false; State = "work"; waitUntil = v.NowMs + Late(AfterClickMs + 700); fade = true;   // it fades out
          return Funnel(v, v.H);
        }
        if (doneCloses > 0 && v.NowMs < waitUntil) return AutoAction.Nothing;   // (its fade: no slot click through it)
        if (State != "idle") State = "done"; return null;
      }
      if (v.NavOwned && heroOk) { if (State != "idle") State = "done"; return null; }   // (the gear tab is the navigator's)
      if (State == "failed" || State == "notfound" || State == "small") return AutoAction.Nothing;
      // the hero screen is closed: from the main city its «Герои» button opens it (only when the button is seen with
      // nothing over it - a pop-up over the city dims it); elsewhere the player is asked
      if (v.Hero != 0) city = 0;   // the hero screen was reached: the next time in the city starts afresh
      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;
      if (!v.Foreground) { State = "background"; return AutoAction.Nothing; }   // (the «Герои» button is seen only with the game in front)
      if (v.NavOwned && (v.Hero == 0 || v.Part >= 0)) { State = v.Hero == 0 ? "noscreen" : "work"; return AutoAction.Nothing; }   // («Герои» and the list's back arrow are the navigator's)
      if (v.Hero == 0 && (!v.HeroesButton || city >= MaxCity)) { State = "noscreen"; return AutoAction.Nothing; }
      if (v.UserBusy || v.NowMs < pauseUntil) { State = "paused"; return AutoAction.Nothing; }
      State = "work";
      if (v.NowMs < waitUntil) return AutoAction.Nothing;
      if (!v.InputOpen) return AutoAction.Nothing;   // the game's input is locked: nothing is sent into it, no try is spent
      double H = v.H;
      if (v.Hero == 0) {
        city++; waitUntil = v.NowMs + Late(3000);
        double chh = v.ClientH > 0 ? v.ClientH : H;
        return Click(v.W - g.HeroesBtnDX * H, chh - g.HeroesBtnDY * H);
      }

      if (heroOk) {                                   // the right hero on another tab: open «Снаряжение»
        if (++tabs > MaxTab) return Fail("the gear tab does not open after " + MaxTab + " clicks (tab " + v.Tab + ")");
        waitUntil = v.NowMs + Late(AfterBackMs);
        return Click(v.W - g.GearTabDX * H, g.GearTabY * H);
      }
      if (v.Part >= 0) {                              // the gear list covers the grid: back to the grid
        if (++backs > MaxBack) return Fail("the gear list does not close after " + MaxBack + " back clicks (part " + v.Part + ")");
        has = false; waitUntil = v.NowMs + Late(AfterBackMs);
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
      if (v.FilterOpen) { FilterDirty = false; waitUntil = v.NowMs + Late(AfterClickMs + 700); fade = true; return Funnel(v, H); }   // (the pop-up covers the grid; it fades out)

      double ch = v.ClientH > 0 ? v.ClientH : H;
      double pitch = pitchF * H, card = cardF * H, top = g.ViewTop * H, bottom = ch - (1 - g.ViewBottom) * H, mid = (top + bottom) / 2;
      if (!has) {                                     // nothing known: a card near the middle tells where the rows are
        if (v.NavOwned && misses >= MaxNavMisses) return Fail(misses + " grid clicks in a row brought up no other hero");
        if (clicks >= MaxClicks || total >= MaxTotal) return Fail("no click near the grid's middle brings up another hero (" + clicks + " clicks, " + misses + " misses)");
        double y = mid + (misses % 3 == 1 ? 0.5 : misses % 3 == 2 ? -0.5 : 0) * pitch;
        // not the shown hero's column: a click on its own card changes nothing (live 27.09: Элизия in the middle column
        // at the middle of the grid took all 24 clicks)
        int sj = Array.IndexOf(v.Heroes, v.Hero), col = sj >= 0 && sj % HeroGeometry.Columns == 1 ? 0 : 1;
        return Pick(v, g.ColX(col) * H, y);
      }
      double row1 = (lo + hi) / 2, cy = row1 + (t / HeroGeometry.Columns) * pitch + card / 2;
      if (cy >= top + card * 0.3 && cy <= bottom - card * 0.3) {
        if (v.NavOwned && misses >= MaxNavMisses) return Fail(misses + " grid clicks in a row brought up no other hero");
        if (clicks >= MaxClicks || total >= MaxTotal) return Fail("the plan's hero at place " + t + " is not brought up by " + clicks + " clicks");
        return Pick(v, g.ColX(t % HeroGeometry.Columns) * H, cy);
      }
      // out of view: drag the grid so the row comes to the middle (the content follows the pointer)
      if (drags >= MaxDrags || total >= MaxTotal) return Fail("the plan's hero at place " + t + " is not reached after " + drags + " drags");
      // the pointer may leave the grid while dragging (the game keeps following it): up to near the window's edge
      double maxUp = bottom - card * 0.3 - 0.03 * H, maxDown = ch - 0.03 * H - (top + card * 0.3);
      double dy = Math.Max(-maxUp, Math.Min(maxDown, mid - cy));
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      // the game's grid follows the pointer by its own gain (live 2026-10-18: about twice as far): a drag that overshoots turns the next one back, each turn
      // shortens the drags (x0.6) until they land - without it a hero 30 rows down in a grid with a gain far from 1 swings back and forth until the tries run out
      int sg = dy < 0 ? -1 : 1;
      if (lastDragSign != 0 && sg != lastDragSign) dragScale = Math.Max(0.15, dragScale * 0.6);
      lastDragSign = sg;
      if (dragScale < 1) { dy *= dragScale; if (Math.Abs(dy) < MinDragPx) dy = sg * MinDragPx; }
      double y0 = dy < 0 ? bottom - card * 0.3 : top + card * 0.3;
      drags++; total++; Progress++; waitUntil = v.NowMs + Late(AfterDragMs);
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
      if (v.FilterOpen) { funnelPressed = false; FunnelUnseen = false; }   // (the pop-up was seen: the next press is a new one)
      int t = Array.IndexOf(v.Heroes, v.Plan);
      bool active = (v.ChosenCamps != null && v.ChosenCamps.Length > 0) || v.ClassChosen;
      // (also a filtered grid whose rows the pilot cannot find: three clicks in the middle brought up no other hero - the player's filter
      // left on over the grid, live 1.6.66.452: 24 clicks, no hero. «Все» in both columns, the unfiltered grid starts at its top.)
      if (misses >= 3 && active) forceClear = true;
      if ((t < 0 || forceClear) && active) {              // hidden by a filter, or its rows not found: clear it
        if (classClicked && !classDone) { classDone = true; }      // our class click hid it: «Все» below undoes it
        if (clearTries >= 3) return null;
        if (!v.FilterOpen) return OpenFilter(v, H);
        clearTries++; waitUntil = v.NowMs + Late(AfterClickMs);
        return v.ClassChosen ? Cell(g.ClassX, 0, H, UiCellKind.HeroClass, ClassAll) : Cell(g.FactionX, 0, H, UiCellKind.HeroFaction, FactionAll);
      }
      if (t < 0 || filterPhase == 2) return null;
      bool known = v.HeroClass > 0 || (v.HeroCamps != null && v.HeroCamps.Length > 0);
      if (filterPhase == 0 && (!known || (filterBlind && v.FilterMem == Mem3.Unknown) || !FarFromView(v, t))) return null;   // (blind only on pixels: a memory flag reads the pop-up)
      filterPhase = 1;
      if (!v.FilterOpen) {
        // the pop-up was never seen after the funnel clicks: a grid click now could land inside it (a wrong class filter),
        // so stop here - a misdetection costs a stop, never a click
        return OpenFilter(v, H);
      }
      if (!factionDone) {
        int row = FactionRow(v);
        if (row < 1 || factionTries >= 2) factionDone = true;
        else if (Array.IndexOf(v.ChosenCamps ?? new long[0], v.FactionList[row]) >= 0) factionDone = true;
        else { factionTries++; waitUntil = v.NowMs + Late(FilterClickMs); return Cell(g.FactionX, row, H, UiCellKind.HeroFaction, v.FactionList[row]); }
      }
      if (!classDone) {
        int row = ClassRow(v);
        if (row < 1 || classClicked) classDone = true;
        // the grid is filtered by this very class already (its id read from the game): no click at all, never «Все» and the same class again
        else if (v.ClassChosen && v.ChosenClass > 0 && v.ChosenClass == v.HeroClass) classDone = true;
        // a class chosen already (the player's): «Все» first - a class clicked then would be added to it, not replace it
        else if (v.ClassChosen) { if (++classResets > 2) classDone = true; else { waitUntil = v.NowMs + Late(AfterClickMs); return Cell(g.ClassX, 0, H, UiCellKind.HeroClass, ClassAll); } }
        else { classClicked = true; waitUntil = v.NowMs + Late(FilterClickMs); return Cell(g.ClassX, row, H, UiCellKind.HeroClass, HeroGeometry.ClassOrder[row - 1]); }
      }
      filterPhase = 2; FilterDirty = false; waitUntil = v.NowMs + Late(AfterClickMs + 700); fade = true;   // it fades out: no grid click through it meanwhile
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

    /// <summary>The funnel is a toggle: one press, then the pop-up has to show (pixels) within FunnelSeenMs. Never a second press on a pop-up not seen (it would shut the one the first press opened);
    /// not seen = the pilot cannot go on (notfound, the navigator resets: the game clears the filter when the hero screen is opened again).</summary>
    AutoAction OpenFilter(HeroView v, double H) {
      if (!funnelPressed && v.FilterPending && v.FilterMem == Mem3.Unknown) return AutoAction.Nothing;   // (the screen is still being built: a press now could be lost or toggle the half-built panel)
      if (funnelPressed) {
        if (v.NowMs - funnelAt < Late(FunnelSeenMs)) return AutoAction.Nothing;
        funnelPressed = false;
        if (v.FilterMem != Mem3.Unknown) {
          // the game says closed (it was not open on the pixels either way): a toggle press that did not reach it. The state is known, so a second press is no blind toggle - at most MaxMemFunnels in all
          if (funnelTries >= MaxMemFunnels) { State = "notfound"; Why = "filter pop-up still closed in memory after " + funnelTries + " funnel presses"; return AutoAction.Nothing; }
        } else {
          filterBlind = true; FunnelUnseen = true; State = "notfound"; Why = "filter pop-up not seen after the funnel press";
          return AutoAction.Nothing;
        }
      }
      funnelPressed = true; funnelAt = v.NowMs; funnelTries++; waitUntil = v.NowMs + Late(AfterClickMs);
      return Funnel(v, H);
    }

    AutoAction Funnel(HeroView v, double H) {
      Progress++;
      double ch = v.ClientH > 0 ? v.ClientH : H;
      var a = Click(g.FunnelX * H, ch - g.FunnelDY * H); a.Widget = FrameTargets.KeyGridFunnel; return a;
    }

    /// <summary>The pilot clicked in the grid's filter pop-up and has not closed it itself: if the plan goes away now (cancelled, another scenario) the
    /// pop-up stays open over the hero screen with the pilot's faction ticked (lab 4512f342: the next hint had no frame to show). Cleanup closes it.</summary>
    public bool FilterDirty;
    int cleanTries; long cleanAt;
    /// <summary>With no plan to run: leave the hero grid's filter as found or cleared («Все» in both columns) and the pop-up closed. Null = nothing to do now.</summary>
    public AutoAction Cleanup(HeroView v) {
      ResolveFilter(v);
      if (!FilterDirty) { cleanTries = 0; return null; }
      if (!v.Foreground || v.UserBusy || v.NowMs - cleanAt < 700) return null;
      double H = v.H;
      if (!v.FilterOpen || cleanTries >= 4) { FilterDirty = false; cleanTries = 0; return null; }
      cleanAt = v.NowMs; cleanTries++;
      if (v.ChosenCamps != null && v.ChosenCamps.Length > 0 && cleanTries <= 2) return CellAt(Click(g.FactionX * H, (g.FilterRow0) * H), UiCellKind.HeroFaction, FactionAll);
      if (v.ClassChosen && cleanTries <= 3) return CellAt(Click(g.ClassX * H, (g.FilterRow0) * H), UiCellKind.HeroClass, ClassAll);
      FilterDirty = false; cleanTries = 0;
      return Funnel(v, H);
    }

    /// <summary>«Все» of the class column (the game's id -1) and of the faction column (0): the ids the live cell reads carry (UiCellKind.HeroClass / HeroFaction).</summary>
    public const long ClassAll = -1, FactionAll = 0;
    static AutoAction CellAt(AutoAction a, UiCellKind kind, long id) { a.CellKind = kind; a.CellId = id; return a; }
    AutoAction Cell(double x, int row, double H, UiCellKind kind, long id) { FilterDirty = true; Progress++; return CellAt(Click(x * H, (g.FilterRow0 + row * g.FilterPitch) * H), kind, id); }

    // a click brought up hero i (or nobody new): the top of i's row is in [y - card, y]
    void Learn(HeroView v) {
      pending = false;
      // a gap, the hero already shown, or still loading: twice in a row means the rows are not where they were thought
      // (the hero already shown: its own card or a gap between cards - which one is not known, so nothing is learnt)
      if (v.Heroes == null || v.Hero == heroBefore) { if (++misses >= 2) has = false; return; }
      int i = Array.IndexOf(v.Heroes, v.Hero); if (i < 0) return;
      misses = 0; Progress++;
      // closer to the plan's hero than any click before: the way is being made, the tries start anew
      int pi = Array.IndexOf(v.Heroes, v.Plan);
      if (pi >= 0) {
        int dist = Math.Abs(i / HeroGeometry.Columns - pi / HeroGeometry.Columns);
        if (dist < bestDist) { bestDist = dist; clicks = 0; drags = 0; Progress++; }
      }
      double pitch = pitchF * v.H, card = cardF * v.H, off = (i / HeroGeometry.Columns) * pitch;
      double nlo = clickY - card - off, nhi = clickY - off;
      if (has && Math.Max(lo, nlo) <= Math.Min(hi, nhi)) { lo = Math.Max(lo, nlo); hi = Math.Min(hi, nhi); }
      else { lo = nlo; hi = nhi; has = true; }
    }

    AutoAction Pick(HeroView v, double x, double y) {
      clicks++; total++; pending = true; clickY = y; heroBefore = v.Hero; pickAt = v.NowMs; waitUntil = v.NowMs + Late(AfterClickMs);
      return Click(x, y);
    }

    void Restart() {
      effect.Drop(); FunnelUnseen = false;
      waitUntil = pauseUntil = failedAt = pickAt = 0; funnelAt = 0; funnelPressed = false; seenHero = 0; clicks = drags = backs = tabs = misses = city = total = 0; bestDist = int.MaxValue; has = pending = false; State = "idle";
      lastDragSign = 0;
      Why = ""; filterPhase = funnelTries = factionTries = clearTries = classResets = doneCloses = 0; classClicked = factionDone = classDone = forceClear = false; lastGrid = 0;
    }

    /// <summary>A wait after an action: as given, or on a game seen answering late, at least its latest answer and a margin.</summary>
    long Late(long ms) { return lateMs > 0 ? Math.Max(ms, Math.Min(MaxLateMs, lateMs + LateMarginMs)) : ms; }

    AutoAction Fail(string why) { State = "failed"; Why = why; return AutoAction.Nothing; }

    /// <summary>What the pilot saw and spent, for the error report: the plan's and the shown hero's places in the grid, the tab,
    /// the gear list, the grid filter pop-up, the tries and where it thinks row 1 is.</summary>
    public string Detail(HeroView v) {
      if (v == null) return "-";
      int pi = v.Heroes != null ? Array.IndexOf(v.Heroes, v.Plan) : -2, si = v.Heroes != null ? Array.IndexOf(v.Heroes, v.Hero) : -2;
      return "state " + State + " plan@" + pi + " shown@" + si + " grid " + (v.Heroes != null ? v.Heroes.Length : -1) + " tab " + v.Tab + " part " + v.Part
        + " filterOpen " + v.FilterOpen + " mem " + v.FilterMem + " small " + v.SmallCards + " clicks " + clicks + " drags " + drags + " backs " + backs + " tabs " + tabs
        + " misses " + misses + " total " + total + " filterPhase " + filterPhase + " funnel " + funnelTries + " doneCloses " + doneCloses
        + " row1 " + (has ? (int)lo + ".." + (int)hi : "unknown") + " retries " + retries + " late " + lateMs;
    }

    static AutoAction Click(double x, double y) { return new AutoAction { Kind = AutoKind.Click, X = x, Y = y }; }
  }
}

namespace RealmForge {
  /// <summary>Lines the pure pilots want in the test log (the probe's lines carry them out: RFX.NavProbeLines drains this). At most 20 wait.</summary>
  /// <summary>Memory against pixels, told only when it lasts: a pair that differs is a disagreement only after HoldMs (1.5 s) without a break (the picture lags the game's flag by the window's fade, 0.3-0.8 s, and
  /// the probe samples once a second: a lone pair is no conflict). One line per episode: «disagree: &lt;name&gt; mem=.. pix=.. for=&lt;ms&gt;». Memory and picture are "yes" / "no"; anything else (unknown, n/a) is no comparison and ends the episode.</summary>
  public sealed class DisagreeTracker {
    public const long HoldMs = 1500;
    string key; long since; bool said;
    public void Reset() { key = null; said = false; }
    /// <summary>The line to log now, or null.</summary>
    public string Update(string name, string mem, string pix, long nowMs) {
      bool ok = (mem == "yes" || mem == "no") && (pix == "yes" || pix == "no");
      return Compare(name, ok ? mem : null, ok ? pix : null, nowMs);
    }
    /// <summary>True once the current disagreement has lasted HoldMs (the line was given).</summary>
    public bool Held { get { return said; } }
    /// <summary>The same for any two words (null = not known: no comparison).</summary>
    public string Compare(string name, string mem, string pix, long nowMs) {
      if (mem == null || pix == null || mem == pix) { Reset(); return null; }
      string k = mem + "/" + pix;
      if (k != key || nowMs < since) { key = k; since = nowMs; said = false; }
      if (said || nowMs - since < HoldMs) return null;
      said = true;
      return "disagree: " + name + " mem=" + mem + " pix=" + pix + " for=" + (nowMs - since);
    }
  }

  /// <summary>The item card's button kind (E4, plan A6): «Заменить» (two cards: m_CurrentEquipInfo set) or «Надеть» (one card). Memory leads; the picture's single blue button only checks it. A picture that contradicts
  /// memory for over 1.5 s (the fields were not cleared between items: a stale m_CurrentEquipInfo) takes over again, told once as «disagree: tip card ..». Kinds: 1 = replace, 2 = equip, 0 = not known / no single button.</summary>
  public sealed class TipKindTracker {
    readonly DisagreeTracker dis = new DisagreeTracker();
    static string Name(int k) { return k == 1 ? "replace" : k == 2 ? "equip" : null; }
    /// <summary>The kind to use (1 / 2) or 0 = memory does not decide, the pictures do (rep / eq as found). <paramref name="line"/>: a log line, or null.</summary>
    public int Decide(int memKind, int pixKind, long nowMs, out string line) {
      line = dis.Compare("tip card", Name(memKind), Name(pixKind), nowMs);
      if (memKind == 0) return 0;
      return dis.Held ? 0 : memKind;
    }
    public void Reset() { dis.Reset(); }
  }

  public static class NavNotes {
    static readonly System.Collections.Generic.Queue<string> q = new System.Collections.Generic.Queue<string>();
    public static void Add(string s) { lock (q) { q.Enqueue(s); while (q.Count > 20) q.Dequeue(); } }
    public static System.Collections.Generic.List<string> Take() { lock (q) { var r = new System.Collections.Generic.List<string>(q); q.Clear(); return r; } }
  }

  /// <summary>The hero grid's filter pop-up on a grabbed frame: a dark strip left of the class check boxes, the Faction|Filter
  /// divider, and a second mark that grid lines cannot fake: a blue selected cell of the class / faction columns or,
  /// on older layouts, the Class|Faction divider. Pure: the pixels come from <c>grab(x, y, w, h)</c> in client px.</summary>
  public static class HeroFilterLook {
    public static bool IsBlueRow(int c) {
      int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
      return b >= r + 25 && b >= g + 10 && b >= 70 && r < 140;
    }

    static bool IsStripDark(int c) {
      int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
      return (r + g + b < 170 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 45) || IsBlueRow(c);
    }

    static void StripBox(double U, out int x0, out int y0, out int w, out int h) {
      x0 = (int)(0.030 * U); int x1 = (int)(0.058 * U); y0 = (int)(0.30 * U); int y1 = (int)(0.87 * U); w = x1 - x0; h = y1 - y0;
    }

    public static bool Seen(Func<int, int, int, int, int[]> grab, double U) {
      int x0, y0, w, h; StripBox(U, out x0, out y0, out w, out h);
      int[] px = grab(x0, y0, w, h);
      if (px == null) return false;
      int dark = 0;
      foreach (int c in px) if (IsStripDark(c)) dark++;
      if (dark < 0.85 * px.Length) return false;
      return Marks(grab, U);
    }

    /// <summary>The same on a grab that may hold masked pixels (our own hint): the dark strip is judged on its known pixels (a few masked rows
    /// cannot make it «not known» when the rest already says no / yes; a strip that is no ends the look as a definite no); the marks keep the
    /// withhold rule of <see cref="OwnMask.Read"/> (a grab with a masked pixel is not used).</summary>
    /// <summary>The latest look's verdict and when (Environment.TickCount; 0 = none): the navigator's probe prints it next to the game's flag (mem vs pix in one recording).</summary>
    public static Seen3 LastLook = Seen3.Unknown; public static int LastLookAt;
    /// <summary>Is the grid's filter pop-up open: the game's flag when it answers (Yes / No), the pixel look only when memory is Unknown (E3, docs/specs/2026-11-04-memory-everywhere-plan.md A1).</summary>
    public static bool Open(Mem3 mem, Seen3 look) { return mem != Mem3.Unknown ? mem == Mem3.Yes : look == Seen3.Yes; }
    /// <summary>Whether the host still looks at the picture while memory answers: only to keep the probe's pix= (and the E2 comparison) fresh, once a second; never for a decision.</summary>
    public static bool LogLookDue(Mem3 mem, int lastLookAt, int nowTick) { return mem != Mem3.Unknown && (lastLookAt == 0 || unchecked(nowTick - lastLookAt) >= 1000); }
    public static Seen3 Look(Func<int, int, int, int, int[]> grab, double U) {
      Seen3 r = LookRaw(grab, U);
      LastLook = r; LastLookAt = Environment.TickCount | 1;
      return r;
    }

    static Seen3 LookRaw(Func<int, int, int, int, int[]> grab, double U) {
      int x0, y0, w, h; StripBox(U, out x0, out y0, out w, out h);
      int[] px = grab(x0, y0, w, h);
      if (px == null) return Seen3.No;
      Seen3 strip = OwnMask.ShareAtLeast(px, IsStripDark, 0.85);
      if (strip == Seen3.No) return Seen3.No;
      Seen3 marks = OwnMask.Read(g => Marks(g, U), grab);
      if (marks == Seen3.No) return Seen3.No;
      return strip == Seen3.Yes ? marks : Seen3.Unknown;
    }

    static bool Marks(Func<int, int, int, int, int[]> grab, double U) {
      // a dark strip is not enough: a filtered grid of not-owned heroes (dark empty cards) looks the same, and a click on the
      // funnel then OPENS the pop-up again. Its own marks, 2 of 3 (the divider moved between layouts: at 503x283 on game 1.6.47 the
      // Фракция | Фильтр line is at 0.7705·U and nothing at 0.4836·U; 1920×1009 and 1.6.46 at 526×296 have it at 0.4836·U):
      // (a) the blue «Все» / chosen cell of the class column, (b) the same in the faction column, (c) a thin light divider at
      // 0.2557, 0.4836 or 0.7705·U
      int marks = 0;
      if (BlueCellSeen(grab, U, 0)) marks++;
      if (BlueCellSeen(grab, U, 1)) marks++;
      if (marks < 2) {
        int lines = 0;   // (two different lines are the pop-up even without a blue cell)
        foreach (double fx in new[] { 0.2557, 0.4836, 0.7705 }) if (DividerSeen(grab, (int)(fx * U), (int)(0.16 * U), (int)(0.87 * U))) lines++;
        marks += lines >= 2 ? 2 : lines;
      }
      return marks >= 2;
    }

    /// <summary>Any column.</summary>
    public static bool BlueCellSeen(Func<int, int, int, int, int[]> grab, double U) { return BlueCellSeen(grab, U, 0) || BlueCellSeen(grab, U, 1); }

    // a selected cell: a box of the cell's blue between its check box and its text, in any row of one column (0 class, 1 faction)
    // («Все» when nothing is chosen, the chosen class / faction otherwise). Calibrated on the 1.6.46 recording at 526×296: the
    // pop-up open 0.87 and more, the closed grid 0.51 at most
    public static bool BlueCellSeen(Func<int, int, int, int, int[]> grab, double U, int column) {
      double[] col = column == 0 ? new[] { 0.12, 0.17 } : new[] { 0.35, 0.385 };
      for (int r = 0; r < 7; r++) {
        int x0 = (int)(col[0] * U), x1 = (int)(col[1] * U), yc = (int)((0.2379 + r * 0.1004) * U), hh = (int)(0.03 * U);
        int[] px = grab(x0, yc - hh, x1 - x0, 2 * hh);
        if (px == null || px.Length == 0) continue;
        int n = 0; foreach (int c in px) if (IsBlueRow(c)) n++;
        if (n >= 0.7 * px.Length) return true;
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
