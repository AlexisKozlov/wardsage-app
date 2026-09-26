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
    public bool HideEquipped, OnOtherHero;   // OnOtherHero: the plan takes it from that hero on purpose (the guide skips others)
    public bool PanelOpen, SidePanelOpen, SetPanelOpen;   // from the screen
  }

  public sealed class FilterPilot {
    public const int MaxClicks = 26, AfterClickMs = 550, PartTries = 2, SetTries = 4, MaxSetDrags = 4, MaxSubs = 4;
    readonly FilterGeometry g;
    long item = -1, waitUntil;
    int clicks, setTries, statTries, eqTries, closeTries, openTries, setDrags, subTries;
    bool subSkipped; string side = "";   // the side panel this pilot opened last: "stat" or "sub" (both look alike)
    // the sets list scrolls (20+ sets, 9 rows fit): rows it is scrolled by (as far as known), the row on the screen
    // clicked last (a set clicked there that is not the wanted one tells the real scroll)
    double setScroll; int setClickRow = -1; bool setPending;
    bool setSkipped, statSkipped, eqSkipped, failed;
    /// <summary>idle | work | done | failed</summary>
    public string State = "idle";

    public FilterPilot(FilterGeometry g) { this.g = g; }

    /// <summary>Start over on the next step (the player pressed «Надеть» again).</summary>
    public void Reset() { item = -1; }

    /// <summary>The click to make, <see cref="AutoAction.Nothing"/> to wait, or null: nothing to do with the filter
    /// (the item pilot goes on).</summary>
    public AutoAction Step(FilterView v) {
      if (v.Item != item) {
        item = v.Item; waitUntil = 0; clicks = setTries = statTries = eqTries = closeTries = openTries = setDrags = subTries = 0;
        subSkipped = false; side = "";
        setClickRow = -1; setPending = false;   // setScroll stays: the game keeps the sets list scrolled
        setSkipped = statSkipped = eqSkipped = failed = false; State = "idle";
      }
      if (v.Item <= 0 || v.Suits == null || v.MainAttrs == null) { State = "idle"; return null; }

      bool setOk = v.SetId <= 0 || setSkipped || Only(v.Suits, v.SetId);
      bool statOk = v.StatId <= 0 || statSkipped || Only(v.MainAttrs, v.StatId);
      bool eqOk = !(v.OnOtherHero && v.HideEquipped) || eqSkipped;
      // the item's sub stats (up to 4): the game lists the items with all of them first
      var subs = WantedSubs(v); var chosen = v.SubAttrs ?? new long[0];
      long missing = 0; foreach (var s in subs) if (Array.IndexOf(chosen, s) < 0) { missing = s; break; }
      bool subOk = subs.Count == 0 || subSkipped || missing == 0;
      bool subsClean = true; foreach (var s in chosen) if (!subs.Contains(s)) subsClean = false;
      bool clean = !v.Foreign && OnlyOrNone(v.Suits, v.SetId) && OnlyOrNone(v.MainAttrs, v.StatId) && (subsClean || subSkipped);
      bool satisfied = setOk && statOk && subOk && eqOk && clean;
      // set: leave the pop-up open (its × would drop the filter); the item pilot picks the item from the list beside it
      if (satisfied) { if (State == "work") State = "done"; return null; }

      if (!v.PanelOpen && (satisfied || failed || !v.Wanted)) { if (State == "work") State = "done"; return null; }
      // a panel the player opened himself is his: only a filter this pilot started is continued
      if (State != "work" && !v.Wanted) return null;
      if (State == "failed") return null;
      if (!v.Foreground || v.UserBusy) return AutoAction.Nothing;
      if (v.NowMs < waitUntil) return AutoAction.Nothing;
      State = "work";
      if (clicks >= MaxClicks) failed = true;

      double W = v.W, H = v.H;
      if (!v.PanelOpen) {
        if (++openTries > PartTries) { failed = true; State = "failed"; return null; }
        // the «Фильтр» bar under the list (HintGeometry.Filter): left edge, bottom
        return Click(v, (0.029 + 0.379 / 2) * Ui.Unit(W, H), Ui.FromBottom(0.9275, W, H));
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
      if (!clean) return Click(v, g.X(g.ResetX, W, H), g.Y(g.ResetY, W, H));
      if (!setOk) {
        int count = v.SetOrder != null ? v.SetOrder.Length : 2 * FilterGeometry.SetRows;
        if (v.SetIndex < 0 || v.SetIndex >= count || setTries >= SetTries) { setSkipped = true; return AutoAction.Nothing; }
        if (!v.SetPanelOpen) return Click(v, g.X(g.SetArrowX, W, H), g.Y(g.SetArrowY, W, H));
        int row = v.SetIndex / 2, rows = (count + 1) / 2;
        if (rows <= FilterGeometry.SetRows) setScroll = 0;   // everything fits: the list cannot scroll
        double onScreen = row - setScroll;
        if (onScreen < 0 || onScreen > FilterGeometry.SetRows - 1) {
          // out of view: drag the list so the row comes to the middle (the content follows the pointer)
          if (setDrags >= MaxSetDrags) { setSkipped = true; return AutoAction.Nothing; }
          double want = Math.Max(0, Math.Min(Math.Max(0, rows - FilterGeometry.SetRows), row - FilterGeometry.SetRows / 2));
          double dy = -(want - setScroll) * g.SetPitch * Ui.Unit(W, H);
          double yTop = g.Y(g.SetRow0, W, H), yBottom = g.Y(g.SetRow0 + (FilterGeometry.SetRows - 1) * g.SetPitch, W, H);
          setDrags++; setScroll = want; clicks++; waitUntil = v.NowMs + AfterClickMs;
          return new AutoAction { Kind = AutoKind.Drag, X = g.X(g.SetColX0, W, H), Y = dy < 0 ? yBottom : yTop, DY = dy };
        }
        setTries++;
        setClickRow = (int)Math.Round(onScreen); setPending = true;
        var c = g.SetCell(setClickRow * 2 + v.SetIndex % 2, W, H);
        return Click(v, c[0], c[1]);
      }
      if (!statOk) {
        if (v.StatIndex < 0 || v.StatIndex >= 2 * FilterGeometry.StatRows || statTries >= PartTries) { statSkipped = true; return AutoAction.Nothing; }
        // the stats panel: a side panel without the sets panel's gold buttons
        if (!v.SidePanelOpen || v.SetPanelOpen || side != "stat") { side = "stat"; return Click(v, g.X(g.StatArrowX, W, H), g.Y(g.StatArrowY, W, H)); }
        statTries++;
        var c = g.StatCell(v.StatIndex, W, H);
        return Click(v, c[0], c[1]);
      }
      if (!subOk) {
        int idx = v.SubOrder != null ? Array.IndexOf(v.SubOrder, missing) : -1;
        if (idx < 0 || idx >= 2 * FilterGeometry.StatRows || subTries >= subs.Count + 2) { subSkipped = true; return AutoAction.Nothing; }
        // its panel looks like the main stats' one: which one is open is remembered from the arrow clicked last
        if (!v.SidePanelOpen || v.SetPanelOpen || side != "sub") { side = "sub"; return Click(v, g.X(g.SubArrowX, W, H), g.Y(g.SubArrowY, W, H)); }
        subTries++;
        var c = g.StatCell(idx, W, H);
        return Click(v, c[0], c[1]);
      }
      // «Скрыть надетое» off: the item is on another hero
      if (eqTries >= PartTries) { eqSkipped = true; return AutoAction.Nothing; }
      eqTries++;
      return Click(v, g.X(g.HideEquippedX, W, H), g.Y(g.HideEquippedY, W, H));
    }

    AutoAction Click(FilterView v, double x, double y) {
      clicks++; waitUntil = v.NowMs + AfterClickMs;
      return new AutoAction { Kind = AutoKind.Click, X = x, Y = y };
    }

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
