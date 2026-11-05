// RealmForge — the auto-pilot sets the game's gear filter to the item's set and main stat, so the item is in the first
// rows of the list instead of 100+ rows down (and «Скрыть надетое» off when the item is on another hero).
//
// Pure decision logic, no Windows APIs (tests: tests/CoreTests.cs). The host (app/Overlay.cs) tells it every tick what
// the game shows - the filter as the game's memory has it (Panel_Equip_Filter.m_FilterConfig), where the set and the stat
// are in the game's own lists (EquipData: SuitListWithPart sorted by m_SuitSort, MainAttrList sorted by stat id), and
// which panels are open (their red × and the sets panel's gold buttons on the screen) - and performs the click returned.
// Every step is checked in memory before the next one: a wrong click only toggles a filter option, the pilot then
// presses «Сбросить» and goes on without that part of the filter. The game applies the filter at once (no «OK») to the
// list on the left, but only while the pop-up is open: its × drops the filter. So the pop-up stays open once the filter
// is set, and the item is picked from the filtered list next to it (checked on the live game 2026-09-26).
using System;

namespace RealmForge {
  /// <summary>The gear filter pop-up, measured on the live game (1920×1009 client): x from the window centre, y from the
  /// top, both in fractions of the client height.</summary>
  public sealed class FilterGeometry {
    public double MainCloseX = 0.3181, CloseY = 0.0902;      // red × of the filter panel
    public double SideCloseX = 0.8543;                        // red × of the side panel (sets / main stats)
    public double SetButtonsX = 0.7215, SetButtonsY = 0.1269; // the sets panel's gold grid/list buttons (only on that panel)
    public double SetArrowX = 0.2339, SetArrowY = 0.3538;     // «Выбрать комплект» ›
    public double StatArrowX = 0.2339, StatArrowY = 0.6194;   // «Выбрать основные хар-ки» ›
    public double ResetX = -0.3865, ResetY = 0.8285;          // «Сбросить» of the filter panel
    public double HideEquippedX = -0.3915, HideEquippedY = 0.9118;   // «Скрыть надетое»
    public double SetColX0 = 0.4876, SetColX1 = 0.7235, SetRow0 = 0.2497, SetPitch = 0.0656;   // sets: 2 columns
    public double StatColX0 = 0.4817, StatColX1 = 0.7185, StatRow0 = 0.2359, StatPitch = 0.0689; // main stats: 2 columns
    public double SubArrowX = 0.2339, SubArrowY = 0.7493;    // «Выбрать второстепенные хар-ки» › (its panel: the main stats' layout)
    // rows fully visible without scrolling the side panel: 10 (live 2026-09-26: all 19 weapon sets and all 20 bracer /
    // amulet / ring sets show at once, the 10th row fully)
    public const int SetRows = 10, StatRows = 9;

    // the pop-up is anchored to the window centre (x and y), sizes scale with the UI unit (Ui.Unit)
    public double X(double fx, double W, double H) { return W / 2 + fx * Ui.Unit(W, H); }
    public double Y(double fy, double W, double H) { return Ui.FromMiddle(fy, W, H); }
    public double[] SetCell(int index, double W, double H) {
      return new[] { X(index % 2 == 0 ? SetColX0 : SetColX1, W, H), Y(SetRow0 + index / 2 * SetPitch, W, H) };
    }
    public double[] StatCell(int index, double W, double H) {
      return new[] { X(index % 2 == 0 ? StatColX0 : StatColX1, W, H), Y(StatRow0 + index / 2 * StatPitch, W, H) };
    }
  }

  /// <summary>What the host sees this tick for the filter.</summary>
  public sealed class FilterView {
    public long NowMs;
    public bool Foreground, UserBusy;
    public double W, H;
    public long Item;               // the item to find (0 = none)
    public bool Wanted;             // the guide says the item is far down or not in the list: filtering helps
    public long SetId, StatId;      // the item's set and main stat (0 = unknown: not filtered by it)
    public int SetIndex = -1, StatIndex = -1;   // their places in the game's lists (-1 = unknown)
    public long[] SetOrder;         // the sets list in the game's order (a wrongly clicked set tells where the list is)
    public long[] SubIds;           // the item's sub stats (null / empty = not filtered by them)
    public long[] SubOrder;         // the sub stats list of the filter, in its order
    public long[] SubAttrs;         // sub stats chosen in the filter now (null = none)
    public long[] Suits, MainAttrs; // the filter now; null = unreadable
    public bool Foreign;            // other filters are on (level, sub stats): they could hide the item
    public bool ItemListed;         // the guide sees the item in the game's list (its verdict is not «hidden by worn»): the game applied the untick even if the read is stale
    public bool HideEquipped, OnOtherHero;   // OnOtherHero: the plan takes it from that hero on purpose (the guide skips others)
    public bool PanelOpen, SidePanelOpen, SetPanelOpen;   // from the screen (the pictures; the pilot replaces them with the game's flags where those are known: FilterPilot.Resolve)
    /// <summary>The same three from the game's memory (EquipScan.ReadGearMem): Yes / No are exact, Unknown = not readable (the pictures stand). The pop-up, its side panel, the sets panel (m_ButtonStates[1] = Suit, the set-vs-stats test).</summary>
    public Mem3 PanelMem = Mem3.Unknown, SideMem = Mem3.Unknown, SetMem = Mem3.Unknown;
    /// <summary>PanelMem is Unknown only because the panel prefab loads / m_Panels is nil (GearMem.Pending, at most 1.5 s): the filter bar is not pressed meanwhile.</summary>
    public bool MemPending;
    /// <summary>The side panel's list (sets / main stats / sub stats) is dragged or coasting, from the game's own ScrollRect (RFX.ReadGearSideListMem): no cell is clicked on it meanwhile (a coast moves the cell away). False = standing or not read.</summary>
    public bool SideListMoving;
    public bool Resolved;
  }

  public sealed class FilterPilot {
    /// <summary>After the click that opens the pop-up the pilot waits this long for the game's flag to turn on before it presses the (toggle) bar again.</summary>
    public const int MemOpenWaitMs = 1500;
    public const int MaxClicks = 26, AfterClickMs = 550, PartTries = 2, EqConfirmMs = 3500, SetTries = 4, MaxSetDrags = 4, MaxSubs = 4, MaxPasses = 3, MaxReopens = 3;
    readonly FilterGeometry g;
    long item = -1, waitUntil, openWaitUntil;
    readonly DisagreeTracker disagree = new DisagreeTracker();
    long eqClickAt, lastNowMs;   // lastNowMs: the clock of the toggle's wait stands still while the player has the mouse / the game is behind (live 572 df57b5: «auto: paused» ate the wait)
    int clicks, setTries, statTries, eqTries, closeTries, openTries, setDrags, subTries, sideCloseTries;
    // the filter was complete once for this item (wasSet); the game may drop it afterwards (the hero screen opened again):
    // the next pass starts its tries over, a few passes at most
    int passes; bool wasSet;
    // the panel closed before anything was applied (a screen read failed, the game hiccuped): it is opened again, a few
    // times per item, before the pilot gives up
    int reopens;
    // the sub stat clicked last (a toggle) with the chosen ones before it: the next click waits for the game's read to show it, and a click that dropped another sub stat ends the sub stat part (never re-clicked)
    long subLastId, subLastAt; long[] subBefore;
    public const int SubSettleMs = 1500;
    bool subSkipped; string side = "";   // the side panel this pilot opened last: "stat" or "sub" (both look alike)
    // the sets list scrolls (20+ sets, 9 rows fit): rows it is scrolled by (as far as known), the row on the screen
    // clicked last (a set clicked there that is not the wanted one tells the real scroll)
    double setScroll; int setClickRow = -1; bool setPending;
    bool setSkipped, statSkipped, failed;
    /// <summary>idle | work | done | failed</summary>
    public string State = "idle";
    /// <summary>Why the pilot gave up or what it is doing besides the filter (for the journal).</summary>
    public string Note = "";
    /// <summary>The pilot gave up because the game dropped its filter again and again: the item is picked without the filter (no reset of the step, no trip to the city: live 1.6.66.572 item 33643).</summary>
    public bool DroppedGiveUp;   // (also the toggle of «Скрыть надетое» that did not read back)

    public FilterPilot(FilterGeometry g) { this.g = g; }

    /// <summary>The guide's verdict (ui/guide.js kind) asks for the filter: the item is far down, hidden or not in the list (a
    /// «Скрыть улучшенное» verdict does not: the filter cannot show it).</summary>
    public static bool WantedFor(string kind, bool onScreen) {
      return kind == "filter" || kind == "hiddenEq" || kind == "hiddenFilter" || kind == "notIn" || ((kind == "pick" || kind == "rel") && !onScreen);
    }

    /// <summary>Start over on the next step (the player pressed «Надеть» again).</summary>
    public void Reset() { item = -1; State = "idle"; Note = ""; }

    /// <summary>The click to make, <see cref="AutoAction.Nothing"/> to wait, or null: nothing to do with the filter
    /// (the item pilot goes on).</summary>
    // the panel read flickers while it opens (lab b3da8af5: open, closed, open, closed within a second): the «Фильтр» bar is a toggle, a click on a read
    // of «closed» right after a read of «open» shut the panel the first click had opened - three times, then «the panel does not stay open»
    long panelSeenAt = long.MinValue; public const int PanelGraceMs = 1500;
    public AutoAction Step(FilterView v) {
      Resolve(v);
      var before = SaveState();
      var a = StepCore(v);
      unsent = a != null && a.Kind != AutoKind.None ? before : null;
      return a;
    }

    /// <summary>Memory first: the pop-up, its side panel and the sets panel are the game's flags where those answer; the pictures (what the host put in PanelOpen ...) only where the flag is Unknown. A disagreement of the
    /// pop-up is told once per episode that lasts over 1.5 s (NavNotes: «disagree: gear filter mem=.. pix=.. for=..»), memory trusted.</summary>
    void Resolve(FilterView v) {
      if (v.Resolved) return;
      v.Resolved = true;
      if (v.PanelMem != Mem3.Unknown) {
        bool mem = v.PanelMem == Mem3.Yes;
        if (v.Foreground) {
          var line = disagree.Update("gear filter", mem ? "yes" : "no", v.PanelOpen ? "yes" : "no", v.NowMs);
          if (line != null) NavNotes.Add(line + " (memory trusted)");
        } else disagree.Reset();
        v.PanelOpen = mem;
      } else disagree.Reset();
      if (v.SideMem != Mem3.Unknown) v.SidePanelOpen = v.SideMem == Mem3.Yes;
      if (v.SetMem != Mem3.Unknown) v.SetPanelOpen = v.SetMem == Mem3.Yes && v.SidePanelOpen;
    }

    // The action of the last Step did not go out (the navigator waits for a stable screen, the gate said wait): the pilot is as before it, the click is not counted and not awaited.
    // (The old host never calls this: its behaviour is unchanged.)
    sealed class Saved { public long WaitUntil, OpenWaitUntil, EqClickAt, SubLastId, SubLastAt; public long[] SubBefore; public int Clicks, SetTries, StatTries, EqTries, CloseTries, OpenTries, SetDrags, SubTries, SideCloseTries, Passes, Reopens, SetClickRow; public double SetScroll; public bool SetPending, WasSet, SubSkipped, SetSkipped, StatSkipped, Failed; public string State, Side, Note; public long Item; }
    Saved unsent;
    Saved SaveState() {
      return new Saved { SubLastId = subLastId, SubLastAt = subLastAt, SubBefore = subBefore, WaitUntil = waitUntil, OpenWaitUntil = openWaitUntil, EqClickAt = eqClickAt, Clicks = clicks, SetTries = setTries, StatTries = statTries, EqTries = eqTries, CloseTries = closeTries, OpenTries = openTries, SetDrags = setDrags, SubTries = subTries,
        SideCloseTries = sideCloseTries, Passes = passes, Reopens = reopens, SetClickRow = setClickRow, SetScroll = setScroll, SetPending = setPending, WasSet = wasSet, SubSkipped = subSkipped, SetSkipped = setSkipped, StatSkipped = statSkipped, Failed = failed, State = State, Side = side, Note = Note, Item = item };
    }
    public void NotSent() {
      var x = unsent; unsent = null; if (x == null) return;
      effect.Drop();
      subLastId = x.SubLastId; subLastAt = x.SubLastAt; subBefore = x.SubBefore;
      waitUntil = x.WaitUntil; openWaitUntil = x.OpenWaitUntil; eqClickAt = x.EqClickAt; clicks = x.Clicks; setTries = x.SetTries; statTries = x.StatTries; eqTries = x.EqTries; closeTries = x.CloseTries; openTries = x.OpenTries; setDrags = x.SetDrags;
      subTries = x.SubTries; sideCloseTries = x.SideCloseTries; passes = x.Passes; reopens = x.Reopens; setClickRow = x.SetClickRow; setScroll = x.SetScroll; setPending = x.SetPending; wasSet = x.WasSet;
      subSkipped = x.SubSkipped; setSkipped = x.SetSkipped; statSkipped = x.StatSkipped; failed = x.Failed; State = x.State; side = x.Side; Note = x.Note;
      item = x.Item;   // (the item too: a click that did not go out on the first tick of a NEW item put the old item's finished state back under the new item, and the next ticks counted its "drops" - live 1.6.66.572 item 33643: «dropped 3 times» -> reset -> the city and back)
    }

    AutoAction StepCore(FilterView v) {
      if (v.PanelOpen && v.PanelMem == Mem3.Unknown) panelSeenAt = v.NowMs;
      if (v.Item != item) {
        item = v.Item; waitUntil = 0; passes = 0; reopens = 0; failed = false; DroppedGiveUp = false; State = "idle"; effect.Drop();
        NewPass();
      }
      if (v.Item <= 0 || v.Suits == null || v.MainAttrs == null) { State = "idle"; effect.Drop(); return null; }
      if (eqTries > 0 && lastNowMs > 0 && (v.UserBusy || !v.Foreground)) eqClickAt += Math.Max(0, v.NowMs - lastNowMs);
      lastNowMs = v.NowMs;
      // the last click's effect in the filter's memory / panels, held still for two reads: its fixed wait ends now
      if (v.NowMs < waitUntil && effect.Seen(Key(v), v.NowMs)) waitUntil = v.NowMs;

      bool setOk = v.SetId <= 0 || setSkipped || Only(v.Suits, v.SetId);
      bool statOk = v.StatId <= 0 || statSkipped || Only(v.MainAttrs, v.StatId);
      // the read of «Скрыть надетое» is stale while the panel is open: one click per pass, never again for an unconfirmed read
      if (eqTries > 0 && v.OnOtherHero && v.HideEquipped && !v.ItemListed && v.NowMs - eqClickAt > EqConfirmMs) {
        failed = true; State = "failed"; Note = "«Скрыть надетое» did not switch off"; DroppedGiveUp = true; return null;   // (quiet: the toggle may still take effect, the pick goes on - no reset, no trip to the city)
      }
      // the guide sees the item in the list: the game shows it whatever the read says, so a click would only switch «Скрыть
      // надетое» ON and hide it (live 1.6.66: the item listed for 5 s under the set filter with the read "on", the one click
      // hid it, then "did not switch off")
      bool eqOk = !(v.OnOtherHero && v.HideEquipped) || v.ItemListed || (eqTries > 0 && v.NowMs - eqClickAt > EqConfirmMs);
      // the item's sub stats (up to 4): the game lists the items with all of them first
      var subs = WantedSubs(v); var chosen = v.SubAttrs ?? new long[0];
      long missing = 0; foreach (var s in subs) if (Array.IndexOf(chosen, s) < 0) { missing = s; break; }
      bool subOk = subs.Count == 0 || subSkipped || missing == 0;
      bool subsClean = true; foreach (var s in chosen) if (!subs.Contains(s)) subsClean = false;
      bool clean = !v.Foreign && OnlyOrNone(v.Suits, v.SetId) && OnlyOrNone(v.MainAttrs, v.StatId) && (subsClean || subSkipped);
      // the guide sees the item in the list's view now (its verdict no longer asks for the filter: live 1.6.66.417, the set alone brought the
      // weapon up, and the main stat and four sub stats were still clicked, 4 s): what is set is enough, the rest is not clicked
      bool enough = !v.Wanted && State == "work" && v.PanelOpen && eqOk && !failed;
      bool satisfied = (setOk && statOk && subOk && eqOk && clean) || enough;
      // set: leave the pop-up open (its × would drop the filter); the item pilot picks the item from the list beside it
      // (the side panel of sets / stats stays open after the last pick and swallows the item list's clicks: closed first)
      if (satisfied) {
        if (v.PanelOpen) wasSet = true;   // (set = seen on in the game's pop-up; a closed pop-up that needed nothing was never set, so nothing can be «dropped»)
        if (v.PanelOpen && v.SidePanelOpen && (State == "work" || State == "done")) {
          if (!v.Foreground || v.UserBusy) return AutoAction.Nothing;
          if (v.NowMs < waitUntil) return AutoAction.Nothing;
          if (sideCloseTries >= PartTries) {
            failed = true; State = "failed"; Note = "the side panel stays open after " + sideCloseTries + " closing clicks";
            return null;
          }
          sideCloseTries++; State = "work"; Note = "closing the side panel (" + sideCloseTries + ")";
          return Click(v, g.X(g.SideCloseX, v.W, v.H), g.Y(g.CloseY, v.W, v.H), FrameTargets.KeyGearSideClose);
        }
        if (State == "work") State = "done";
        return null;
      }

      if (!v.PanelOpen && (satisfied || failed || !v.Wanted)) { if (State == "work") State = "done"; return null; }
      // a panel the player opened himself is his: only a filter this pilot started is continued
      if (State != "work" && !v.Wanted) return null;
      if (State == "failed") return null;
      if (!v.Foreground || v.UserBusy) return AutoAction.Nothing;
      if (v.NowMs < waitUntil) return AutoAction.Nothing;
      // the filter was set for this item and is gone now (the game dropped it): set it again with fresh tries
      // (the tries left from the first pass made the second give up halfway - live 10.10)
      if (State != "work" && wasSet) {
        if (++passes >= MaxPasses) { failed = true; State = "failed"; Note = "the filter was dropped " + passes + " times"; DroppedGiveUp = true; return null; }
        NewPass();
      }
      State = "work";
      if (clicks >= MaxClicks) failed = true;

      double W = v.W, H = v.H;
      if (!v.PanelOpen) {
        if (v.PanelMem == Mem3.Unknown && v.MemPending && !failed) return AutoAction.Nothing;   // (the screen is still being built)
        if (v.PanelMem == Mem3.Unknown && panelSeenAt != long.MinValue && v.NowMs - panelSeenAt < PanelGraceMs && !failed) return AutoAction.Nothing;   // (it was just seen open: the read flickers, no toggling click; a memory flag does not flicker)
        // the bar is a toggle: pressed once, the game's flag is awaited (it leads the pictures) before another press
        if (v.PanelMem != Mem3.Unknown && v.NowMs < openWaitUntil && !failed) return AutoAction.Nothing;
        if (++openTries > PartTries) {
          if (reopens >= MaxReopens) { failed = true; State = "failed"; Note = "the filter panel does not stay open (" + reopens + " retries)"; return null; }
          reopens++; NewPass(); openTries = 1; Note = "the panel closed without a filter: opening it again (" + reopens + ")";
        }
        // the «Фильтр» bar under the list (HintGeometry.Filter): left edge, bottom
        if (v.PanelMem != Mem3.Unknown) openWaitUntil = v.NowMs + MemOpenWaitMs;
        return Click(v, (0.029 + 0.379 / 2) * Ui.Unit(W, H), Ui.FromBottom(0.9275, W, H), FrameTargets.KeyGearFilter);
      }
      // the panel is open: close it only when this pilot gave up (the list then shows everything again)
      if (failed) {
        if (++closeTries > PartTries) { failed = true; State = "failed"; return null; }
        return Click(v, g.X(g.MainCloseX, W, H), g.Y(g.CloseY, W, H));
      }
      // the last set click chose another set: that set's row was where the click went, so the list is scrolled by the
      // difference (then «Сбросить» clears it below and the wanted one is clicked where it really is)
      if (setPending) {
        setPending = false;
        if (v.Suits.Length == 1 && v.Suits[0] != v.SetId && v.SetOrder != null && setClickRow >= 0) {
          int j = Array.IndexOf(v.SetOrder, v.Suits[0]);
          if (j >= 0) setScroll = j / 2 - setClickRow;
        }
      }
      // (the game keeps the sets list where it was when the panel is closed and opened again, also after «Сбросить»:
      // the scroll learned from a wrong click stays - live test 2026-09-26)
      if (!clean) return Click(v, g.X(g.ResetX, W, H), g.Y(g.ResetY, W, H), FrameTargets.KeyGearReset);
      if (!setOk) {
        int count = v.SetOrder != null ? v.SetOrder.Length : 2 * FilterGeometry.SetRows;
        if (v.SetIndex < 0 || v.SetIndex >= count || setTries >= SetTries) { setSkipped = true; return AutoAction.Nothing; }
        if (!v.SetPanelOpen) return Click(v, g.X(g.SetArrowX, W, H), g.Y(g.SetArrowY, W, H), FrameTargets.KeyGearSetArrow);
        if (SideMoving(v)) return AutoAction.Nothing;
        int row = v.SetIndex / 2, rows = (count + 1) / 2;
        if (rows <= FilterGeometry.SetRows) setScroll = 0;   // everything fits: the list cannot scroll
        double onScreen = row - setScroll;
        if (onScreen < 0 || onScreen > FilterGeometry.SetRows - 1) {
          // out of view: drag the list so the row comes to the middle (the content follows the pointer)
          if (setDrags >= MaxSetDrags) { setSkipped = true; return AutoAction.Nothing; }
          double want = Math.Max(0, Math.Min(Math.Max(0, rows - FilterGeometry.SetRows), row - FilterGeometry.SetRows / 2));
          double dy = -(want - setScroll) * g.SetPitch * Ui.Unit(W, H);
          double yTop = g.Y(g.SetRow0, W, H), yBottom = g.Y(g.SetRow0 + (FilterGeometry.SetRows - 1) * g.SetPitch, W, H);
          setDrags++; setScroll = want; clicks++; waitUntil = v.NowMs + AfterClickMs; effect.Drop();   // (a drag's effect is not read: its wait stays)
          return new AutoAction { Kind = AutoKind.Drag, X = g.X(g.SetColX0, W, H), Y = dy < 0 ? yBottom : yTop, DY = dy };
        }
        setTries++;
        setClickRow = (int)Math.Round(onScreen); setPending = true;
        var c = g.SetCell(setClickRow * 2 + v.SetIndex % 2, W, H);
        return Click(v, c[0], c[1], null, UiCellKind.GearSuit, v.SetId);
      }
      if (!statOk) {
        if (v.StatIndex < 0 || v.StatIndex >= 2 * FilterGeometry.StatRows || statTries >= PartTries) { statSkipped = true; return AutoAction.Nothing; }
        // the stats panel: a side panel without the sets panel's gold buttons
        if (!v.SidePanelOpen || v.SetPanelOpen || side != "stat") { side = "stat"; return Click(v, g.X(g.StatArrowX, W, H), g.Y(g.StatArrowY, W, H), FrameTargets.KeyGearStatArrow); }
        if (SideMoving(v)) return AutoAction.Nothing;
        statTries++;
        var c = g.StatCell(v.StatIndex, W, H);
        return Click(v, c[0], c[1], null, UiCellKind.GearStat, v.StatId);
      }
      if (!subOk && subLastId != 0) {
        // the last sub stat click is checked in the filter's memory before the next one: a stat of the filter that is gone was toggled off by it (the list is not where it was assumed): the sub stats stop here, the set alone stays
        bool lost = false; foreach (var b in subBefore ?? new long[0]) if (Array.IndexOf(chosen, b) < 0) lost = true;
        bool took = Array.IndexOf(chosen, subLastId) >= 0;
        if (lost) { subSkipped = true; subLastId = 0; Note = "a sub stat click toggled another one off: the sub stats stop"; return AutoAction.Nothing; }
        if (!took && v.NowMs - subLastAt < SubSettleMs) return AutoAction.Nothing;   // (the read may still be the old one: a second click would toggle it off)
        subLastId = 0;
      }
      if (!subOk) {
        int idx = v.SubOrder != null ? Array.IndexOf(v.SubOrder, missing) : -1;
        if (idx < 0 || idx >= 2 * FilterGeometry.StatRows || subTries >= subs.Count + 2) { subSkipped = true; return AutoAction.Nothing; }
        // its panel looks like the main stats' one: which one is open is remembered from the arrow clicked last
        if (!v.SidePanelOpen || v.SetPanelOpen || side != "sub") { side = "sub"; return Click(v, g.X(g.SubArrowX, W, H), g.Y(g.SubArrowY, W, H), FrameTargets.KeyGearSubArrow); }
        if (SideMoving(v)) return AutoAction.Nothing;
        subTries++; subLastId = missing; subLastAt = v.NowMs; subBefore = (long[])chosen.Clone();
        var c = g.StatCell(idx, W, H);
        return Click(v, c[0], c[1], null, UiCellKind.GearSub, missing);
      }
      // «Скрыть надетое» off: the item is on another hero. A side panel still open (the sub stats' one stays after the last
      // pick) swallows the click: closed first, and the filter is not done while the item stays hidden (live 1.6.64)
      if (v.SidePanelOpen) {
        if (sideCloseTries >= PartTries) { failed = true; State = "failed"; Note = "the side panel stays open after " + sideCloseTries + " closing clicks (hide worn)"; return null; }
        sideCloseTries++; Note = "closing the side panel (" + sideCloseTries + ")";
        return Click(v, g.X(g.SideCloseX, W, H), g.Y(g.CloseY, W, H), FrameTargets.KeyGearSideClose);
      }
      if (eqTries > 0) return AutoAction.Nothing;   // clicked once: waits for the game, never toggles back
      eqTries++; eqClickAt = v.NowMs;
      return Click(v, g.X(g.HideEquippedX, W, H), g.Y(g.HideEquippedY, W, H), FrameTargets.KeyGearHideWorn);
    }

    /// <summary>Which side panel this pilot opened last ("stat" or "sub"; "" = none / the sets panel): the host reads that panel's list.</summary>
    public string Side { get { return side; } }
    /// <summary>The side list moves: waited for, at most MovingCapMs (a speed that never reads zero does not hold the pilot for ever).</summary>
    public const int MovingCapMs = 3000;
    long sideMovingSince;
    bool SideMoving(FilterView v) {
      if (!v.SideListMoving) { sideMovingSince = 0; return false; }
      if (sideMovingSince == 0) sideMovingSince = v.NowMs;
      return v.NowMs - sideMovingSince < MovingCapMs;
    }

    // the tries of one pass (setScroll stays: the game keeps the sets list scrolled)
    void NewPass() {
      clicks = setTries = statTries = eqTries = closeTries = openTries = setDrags = subTries = sideCloseTries = 0;
      Note = ""; wasSet = false; openWaitUntil = 0;
      subSkipped = false; side = ""; subLastId = 0; subBefore = null;
      setClickRow = -1; setPending = false;
      setSkipped = statSkipped = false;
    }

    AutoAction Click(FilterView v, double x, double y, string widget = null, UiCellKind cell = UiCellKind.None, long cellId = 0) {
      clicks++; waitUntil = v.NowMs + AfterClickMs; effect.Arm(Key(v), v.NowMs);
      return new AutoAction { Kind = AutoKind.Click, X = x, Y = y, Widget = widget, CellKind = cell, CellId = cellId };   // (Widget / cell: the host presses its live rect, the point is the fallback)
    }

    /// <summary>The pilot waits for its last click's effect (the host reads at its effect rate meanwhile).</summary>
    public bool Polling(long now) { return effect.Armed && now < waitUntil; }
    readonly EffectWait effect = new EffectWait();
    // what a filter click changes: the chosen set / stats / sub stats, the panels open, «Скрыть надетое»
    static string Key(FilterView v) {
      return J(v.Suits) + "|" + J(v.MainAttrs) + "|" + J(v.SubAttrs) + "|" + v.PanelOpen + v.SidePanelOpen + v.SetPanelOpen + v.HideEquipped;
    }
    static string J(long[] a) { return a == null ? "-" : string.Join(",", Array.ConvertAll(a, x => x.ToString())); }

    static bool Only(long[] set, long id) { return set.Length == 1 && set[0] == id; }

    // the item's sub stats the filter offers, at most 4 (the game's limit)
    static System.Collections.Generic.List<long> WantedSubs(FilterView v) {
      var r = new System.Collections.Generic.List<long>();
      if (v.SubIds == null || v.SubOrder == null) return r;
      foreach (var s in v.SubIds) if (Array.IndexOf(v.SubOrder, s) >= 0 && !r.Contains(s) && r.Count < MaxSubs) r.Add(s);
      return r;
    }
    static bool OnlyOrNone(long[] set, long id) { return set.Length == 0 || (id > 0 && Only(set, id)); }
  }
}
