// RealmForge — storage cleanup: in the game's inventory, «Массовая продажа» (src/SellScan.cs), the app selects the
// items the player picked on the site. «Продать» is ALWAYS pressed by the player; the app never sells.
//
// A click on an item of this list toggles its selection, so the pilot never clicks to find out where the rows are (as
// the gear list pilot does): the list opens scrolled to the top (row positions known), after a drag the stat bars on the
// screen give the rows' exact phase, and after every click the item whose selection changed says where the pointer was.
// A click that hit a neighbour instead of the wanted item re-anchors the list exactly; if that neighbour is not wanted,
// the same place is clicked once more at once, which takes it off the choice again; a click in another column stops the
// pilot (the columns are not where it thinks). Items the player selected himself are left alone (reported as extra).
// SellGeometry's defaults are the gear list's sell screen (Form_EquipSell, measured first; its part filter and
// «Быстрое улучшение» pop-up are handled too) - the app uses SellGeometry.Inventory.
//
// Pure decision logic, no Windows APIs (tests: tests/CoreTests.cs); the host (app/Overlay.cs) performs the actions.
using System;
using System.Collections.Generic;

namespace RealmForge {
  /// <summary>Layout of the sell screen's list in UI units (Ui.Unit), measured on the live game 2026-09-26 (1920×1009 and
  /// 1600×1000): the list is centred horizontally, its top follows the window's top edge and the bottom of its view the
  /// bottom edge (a taller window shows more rows).</summary>
  public sealed class SellGeometry {
    public double X0 = -0.1764, PitchX = 0.1269, CellW = 0.1130;   // column 1's cell left edge from the centre, step, size
    public double PitchY = 0.1705;                                // item row step
    public double TitleRow = 0.5407, EmptyRow = 0.60;            // other row kinds relative to an item row (the «Все» row)
    public double ViewTop = 0.1030, ViewBottom = 0.8226;         // visible part: top from the top edge, bottom from the bottom
    public double TopPad = 0.0;                                   // view top → row 1's top at scroll 0
    public double BarTop = 0.1229, BarBottom = 0.1487;            // stat bar under the cells, from the row top
    public double SellBtnX = 0.3588, SellBtnY = 0.1229;           // «Продать» next to «1119/2300» on the gear list (top-left)
    // the left panel is centred in the window (x from the centre, y by Ui.FromMiddle): «Выбрать часть» icons (weapon,
    // armour, bracer, amulet, ring = the game's parts 0..4, the site's slots) and «Сбросить»
    public double PartX0 = -0.7968, PartPitch = 0.1115, PartY = 0.1863;
    public double ResetX = -0.7532, ResetY = 0.8484;
    // an empty spot right of the list (left of the tabs), centred: a click there closes the item card and «Быстрое
    // улучшение» and changes nothing else (live 2026-09-26: the choice stayed)
    public double CloseX = 0.674, CloseY = 0.4955;
    // where «Быстрое улучшение» sits over the list (x from the centre, y from the top, UI units; measured 1920×1009 with a
    // margin): only a click there needs it closed first
    public double PopupX0 = -0.21, PopupX1 = 0.34, PopupY0 = 0.48, PopupY1 = 0.92;
    public bool InPopup(double x, double y, double W, double U) { if (PopupX0 >= PopupX1) return false; double fx = (x - W / 2) / U, fy = y / U; return fx > PopupX0 && fx < PopupX1 && fy > PopupY0 && fy < PopupY1; }
    public int Columns = 6;
    public double CellH = double.NaN;                             // cell height when it differs from the width
    public double CellHeight { get { return double.IsNaN(CellH) ? CellW : CellH; } }

    /// <summary>The inventory's equipment list (Form_BackpackIntegration), measured on the live game 2026-09-26 at
    /// 1920×1009, 1600×1000 and 1300×1000: the rows follow the UI unit from the top (first row's bar at 0.335, step
    /// 0.1846); the columns fill the width between the left menu (0.205 from the left edge) and the item panel on the
    /// right (0.522 from the right edge), <paramref name="cols"/> of them (the game counts them from the width: 8 there).
    /// No part filter (a filter change clears the choice) and no pop-up.</summary>
    public static SellGeometry Inventory(double W, double U, int cols) {
      double pitchX = (W - 0.727 * U) / cols / U;
      return new SellGeometry {
        X0 = 0.205 - W / (2 * U), PitchX = pitchX, CellW = pitchX * 0.86, CellH = 0.127, Columns = cols,
        PitchY = 0.1846, TitleRow = 1, ViewTop = 0.190, TopPad = 0.012, ViewBottom = 0.83,
        BarTop = 0.133, BarBottom = 0.165,
        PartX0 = double.NaN, ResetX = double.NaN, PopupX0 = 1, PopupX1 = 0,
      };
    }

    /// <summary>The artifact tab's list (Panel_ArtifactPanel, «Массовая продажа»): cells of the grid are <paramref name="cols"/> per row across the list's width
    /// (left edge 0.195 U from the left, right edge 0.5231 U from the right, top 0.1922 U, bottom 0.14 U above the window's bottom), a row is 116 of the canvas's
    /// 640 units; the card in a cell is 80 x 110 units, centred. No part filter, no pop-up. <paramref name="list"/> = the list's rectangle from the site's
    /// geometry file (inventory.artList, client px) or null (then the numbers above). Measured on a live recording (test app 1.6.62, 1366x705 window): 8 columns at that width (the game takes floor((width - 6) / 90) of canvas units), a row 120 px = 116 units.</summary>
    public static SellGeometry Artifacts(double W, double H, double U, int cols, GeoRect list = null) {
      if (cols < 1) cols = 1;
      double left = 0.195, right = W / U - 0.5231, top = 0.1922, viewBottom = 0.86;   // ViewBottom: from the top edge's side, bottom edge = H - (1 - ViewBottom) U
      if (list != null && list.W > 10 && list.H > 10) { left = list.X / U; right = list.Right / U; top = list.Y / U; viewBottom = 1 - (H - list.Bottom) / U; }
      double pitchX = (right - left) / cols, cardW = 80.0 / 640, cardH = 110.0 / 640, row = 116.0 / 640;
      return new SellGeometry {
        X0 = left - W / (2 * U) + (pitchX - cardW) / 2, PitchX = pitchX, CellW = cardW, CellH = cardH, Columns = cols,
        PitchY = row, TitleRow = 1, ViewTop = top, TopPad = (row - cardH) / 2, ViewBottom = viewBottom,
        BarTop = 0, BarBottom = cardH,
        PartX0 = double.NaN, ResetX = double.NaN, PopupX0 = 1, PopupX1 = 0,
      };
    }

    /// <summary>Columns of the artifact list as the game counts them (Panel_ArtifactPanel: floor((list width in canvas units - 6) / 90)); <paramref name="fallback"/> when the rectangle is unknown.</summary>
    public static int ArtCols(GeoRect list, double U, int fallback) {
      if (list == null || list.W <= 10 || U <= 0) return fallback;
      int c = (int)System.Math.Floor((list.W / U * 640 - 6) / 90);
      return c < 1 || c > 20 ? fallback : c;
    }

    public double RowHeight(int type) { return type == 2 ? PitchY * TitleRow : type == 3 ? PitchY * EmptyRow : PitchY; }
    public double RowOffset(int[] types, int row) {
      double y = 0;
      for (int i = 1; i < row; i++) y += RowHeight(types != null && i < types.Length ? types[i] : 1);
      return y;
    }
    public double X(double fx, double W, double U) { return W / 2 + fx * U; }
    public double CellCentreX(int col, double W, double U) { return X(X0 + (col - 1) * PitchX + CellW / 2, W, U); }
    public double StripCentreX(double W, double U) { return X(X0 + (Columns * PitchX) / 2, W, U); }
    public double ViewTopPx(double U) { return ViewTop * U; }
    public double ViewBottomPx(double H, double U) { return H - (1 - ViewBottom) * U; }
    public double[] Part(int part, double W, double H, double U) { return new[] { W / 2 + (PartX0 + part * PartPitch) * U, Ui.FromMiddle(PartY, W, H) }; }
    public double[] Reset(double W, double H, double U) { return new[] { W / 2 + ResetX * U, Ui.FromMiddle(ResetY, W, H) }; }
    public double[] Close(double W, double H, double U) { return new[] { W / 2 + CloseX * U, Ui.FromMiddle(CloseY, W, H) }; }
  }

  public sealed class SellView {
    public long NowMs;
    public bool Foreground, UserBusy, UserClicked;
    public double W, H, U;              // client size, UI unit (Ui.Unit)
    public SellScreen Screen;           // open sell screen
    public ICollection<long> Wanted;    // the site's list
    public ICollection<int> Parts;      // gear parts of the list's items (the site's slots 0..4); null = unknown
    public IDictionary<long, int> Levels;   // the items' upgrade levels (below +16 «Быстрое улучшение» opens); null = unknown
    public double Phase = double.NaN;   // row-top phase of the item rows on the screen now (client px mod the row step), NaN = not seen
    public double Row1 = double.NaN;    // client y of row 1's top from the game's own scroll reading (InvScroll), NaN = unreadable
    public bool NeedExact;              // a cell click needs Row1 (the gate refuses one without the exact scroll): without it the pilot waits, then stops as "scroll"
  }

  public sealed class SellPilot {
    public const int AfterClickMs = 250, AfterDragMs = 500, AfterFilterMs = 1100, UserPauseMs = 4000, MaxClicks = 4000, MaxDrags = 400, MaxMiss = 8, MaxFilterClicks = 12, AfterCloseMs = 300, MaxLevel = 16, ScrollWaitMs = 1500;
    public const double MinDragPx = 24;
    /// <summary>An up drag ends no higher than this share of the window's height from its top; a drag brings its target this share of the view in.</summary>
    public const double UpEndTop = 0.03, AimIn = 0.25;
    /// <summary>A click's answer or a drag's move is awaited: the host reads the game every 50 ms meanwhile.</summary>
    public bool Polling { get { return checkClick || dragWait; } }
    // a game slower than AfterClickMs (a slow laptop): a choice that changes after the click was judged is a late answer, the waits grow to the latest
    // answer seen plus this margin (never above MaxLateMs); a fast PC's never change
    public const int LateMarginMs = 400, MaxLateMs = 4000;

    SellGeometry g;
    /// <summary>The list's layout; a new one (the window was resized) starts the anchor over.</summary>
    public SellGeometry Geometry { get { return g; } set { if (value != g) { g = value; list = 0; } } }
    ulong list;                  // the rows table the anchor belongs to
    double row1;                 // client px of row 1's top
    bool exact;                  // row1 is known exactly (list opened at the top, or a click said where it is)
    long waitUntil, pauseUntil;
    // the last click: what was meant, where, what the game said before it
    int meantCol;
    long meant, curBefore; HashSet<long> selBefore = new HashSet<long>(); double clickY, clickX; bool checkClick;
    long undo; double undoY, undoX; int undoTries;
    int clicks, drags, misses, filterClicks; bool resetDone, phased, popup;
    int endDir, endSeen;                          // the list did not move on drags this way (±1), how many times running: its end at 2
    readonly Dictionary<long, int> slips = new Dictionary<long, int>();   // wanted items taken off by a stray click
    readonly HashSet<long> skip = new HashSet<long>();                  // wanted items out of reach (counted missing)

    double beforeDrag, lastDrag; bool measure;
    /// <summary>List move per px of drag (1 assumed until measured).</summary>
    public double Gain;
    /// <summary>The same, for drags up (the list's later rows) and down: the game's list follows them differently
    /// (live: about 1.0 and 0.84); kept from the rows' phase after every drag.</summary>
    public double GainUp, GainDown;
    /// <summary>idle | work | paused | background | done | failed</summary>
    public string State = "idle";
    /// <summary>Wanted items selected / wanted items not in the list (sold already, or hidden by the screen's filter) /
    /// selected items that are not wanted (the player's own choice, or a slip the pilot could not take back).</summary>
    public int Selected, Missing, Extra;
    /// <summary>What the last step did and why (the app's log).</summary>
    public string Trace = "";
    /// <summary>Why the run failed when it is not the pilot's own count: "scroll" = a cell click needed the game's exact scroll and it stayed unreadable.</summary>
    public string FailWhy = "";
    long noScrollSince = -1;
    long clickAt, lateMs; HashSet<long> seenSel;
    // a drag on the game's exact scroll: the scroll before it and when, awaited until it moves (or DragLandMs: the list's end)
    bool dragWait; double dragFrom; long dragAt; int stillDrags;   // stillDrags: drags in a row the scroll did not follow
    public const int MaxStillDrags = 4;
    public const int DragLandMs = 2000;   // the last cell click's time, the latest answer seen, the choice on the last tick

    public SellPilot(SellGeometry g) { this.g = g; }

    // the last action the host was given, to take back when it could not send it (the gate refused it: a pop-up came, the forms unread, a
    // failed read): a refused cell click is no miss and a refused drag no drag the list ignored (at most MaxUnsent in a row; then they count)
    int lastAct, unsentRun; double lastRow1; bool lastExact, lastPhased, lastPopup;
    public const int MaxUnsent = 60;
    /// <summary>The last step's action was not sent: taken back (a cell click, a drag, an undo click, a pop-up close).</summary>
    public void NotSent() {
      int k = lastAct; lastAct = 0;
      if (k == 0 || ++unsentRun > MaxUnsent) return;
      if (k == 1) { checkClick = false; clicks--; popup = lastPopup; waitUntil = 0; }
      else if (k == 2) { dragWait = false; drags--; measure = false; row1 = lastRow1; exact = lastExact; phased = lastPhased; waitUntil = 0; }
      else if (k == 3) { undoTries--; popup = lastPopup; waitUntil = 0; }
      else if (k == 4) { popup = true; waitUntil = 0; }
      Say("not sent");
    }
    /// <summary>The last step's action went out: the refusals in a row start over.</summary>
    public void Sent() { if (lastAct != 0) unsentRun = 0; lastAct = 0; }

    /// <summary>A new list from the site, or the run was stopped: start over.</summary>
    public void Reset() { list = 0; exact = phased = false; waitUntil = pauseUntil = 0; checkClick = dragWait = false; stillDrags = 0; undo = 0; clicks = drags = misses = filterClicks = endDir = endSeen = 0; resetDone = popup = false; slips.Clear(); skip.Clear(); State = "idle"; FailWhy = ""; noScrollSince = -1; }

    public AutoAction Step(SellView v) {
      lastAct = 0;
      var s = v.Screen;
      if (s == null || !s.Open || s.Types == null || v.Wanted == null) { State = "idle"; noScrollSince = -1; return AutoAction.Nothing; }
      Count(v);
      // a new list (the screen was opened, the filter changed, a sale went through): it is at the top again
      // stopped: nothing more, but a stray item it selected is still taken back (below)
      if (State == "failed" && undo == 0 && !checkClick) return AutoAction.Nothing;
      if (s.ListPtr != list) { noScrollSince = -1; list = s.ListPtr; row1 = g.ViewTopPx(v.U) + g.TopPad * v.U; exact = true; checkClick = false; undo = 0; measure = false; }
      // after a drag: the stat bars on the screen give the rows' exact phase (the drag's error is well under half a row)
      // the game says where the list is scrolled: that is exact, whatever a drag did (the picture is the last resort)
      if (!double.IsNaN(v.Row1)) { row1 = v.Row1; exact = true; phased = true; noScrollSince = -1; }
      // the choice changed with no click awaited, after the last one was judged: the game answers late
      if (!checkClick && seenSel != null && clickAt > 0 && !seenSel.SetEquals(s.Selected)) {
        long dt = v.NowMs - clickAt;
        if (dt > AfterClickMs && dt <= MaxLateMs && dt > lateMs) { lateMs = dt; Say("late answer " + dt + " ms"); }
      }
      seenSel = new HashSet<long>(s.Selected);
      // the drag's answer: the game's scroll moves (late on a slow laptop: a click or drag on the old scroll would miss the list's new place)
      if (dragWait && !double.IsNaN(v.Row1)) {
        long dt = v.NowMs - dragAt;
        if (Math.Abs(v.Row1 - dragFrom) > 1) { dragWait = false; stillDrags = 0; if (dt > AfterDragMs && dt <= MaxLateMs && dt > lateMs) { lateMs = dt; Say("late drag " + dt + " ms"); } }
        else if (dt < Math.Max(DragLandMs, Late(AfterDragMs))) { State = "work"; return AutoAction.Nothing; }
        else {
          // the list did not move (its end, or something over it takes the drags): again and again - a stop, not drags for ever
          dragWait = false;
          if (++stillDrags >= MaxStillDrags) { State = "failed"; FailWhy = "drag"; Say("the list did not move on " + stillDrags + " drags"); return AutoAction.Nothing; }
        }
      }
      if (!exact && !phased && !double.IsNaN(v.Phase)) Snap(v);
      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;
      // the unreadable-scroll clock runs only while the pilot works: a pause, the background or a screen change starts it over
      if (!v.Foreground || v.UserBusy || v.NowMs < pauseUntil) noScrollSince = -1;
      else if (double.IsNaN(v.Row1) && noScrollSince < 0) noScrollSince = v.NowMs;
      bool failed = State == "failed";
      if (!v.Foreground) { if (!failed && State != "done") State = "background"; return AutoAction.Nothing; }   // (a finished run stays finished: the final frame on «Продать» is drawn from it)
      if (v.UserBusy || v.NowMs < pauseUntil) { if (!failed && State != "done") State = "paused"; checkClick = false;   // mouse moves must not take the final step away (live 1.6.66.446: the «Продать» frame went out while the mouse moved)
         return AutoAction.Nothing; }
      // a click's answer is the choice changing (the game's own selection, read every 50 ms while it is awaited): judged at once, the next item
      // follows at once; the fixed wait is only for a click that changed nothing (yet)
      bool answered = checkClick && !s.Selected.SetEquals(selBefore);
      if (v.NowMs < waitUntil && !answered) { if (!failed) State = "work"; return AutoAction.Nothing; }

      if (checkClick) { checkClick = false; AfterClick(v); seenSel = new HashSet<long>(s.Selected); }
      if (undo != 0) { var uc = ClosePopupFor(v, undoX, undoY); return uc ?? Undo(v); }
      if (State == "failed") return AutoAction.Nothing;

      // before anything is selected (a filter change clears the choice): the screen's filter shows only the parts the
      // list has items of - a shorter list, less scrolling; «Сбросить» first when the player's filter hides some of them
      if (Selected == 0 && Extra == 0 && filterClicks < MaxFilterClicks) {
        var f = FilterStep(v);
        if (f != null) return f;
      }

      long next = NextTarget(v);
      if (next == 0) { State = "done"; return AutoAction.Nothing; }
      State = "work";
      int[] p = s.Pos[next];
      double pitch = g.PitchY * v.U, cell = g.CellHeight * v.U;
      double viewTop = g.ViewTopPx(v.U), viewBottom = g.ViewBottomPx(v.H, v.U), mid = (viewTop + viewBottom) / 2;
      double cy = row1 + g.RowOffset(s.Types, p[0]) * v.U + cell / 2;
      // visible (with a margin while the position is only estimated after a drag): click it
      double margin = exact || phased ? cell * 0.45 : pitch;
      // the list is at its end that way: the item is as far in view as it gets - click it where it is (inside the view)
      bool atEnd = endDir != 0 && endSeen >= 2 && Math.Sign(-(cy - mid)) == endDir && exact;
      if (atEnd && (cy < viewTop + cell * 0.3 || cy > viewBottom - cell * 0.3)) { skip.Add(next); return AutoAction.Nothing; }
      if ((cy >= viewTop + margin && cy <= viewBottom - margin) || atEnd) {
        // the gate clicks a cell only with the game's exact scroll: re-read for a while, then stop (never click on the picture's estimate)
        if (v.NeedExact && double.IsNaN(v.Row1)) {
          if (v.NowMs - noScrollSince < ScrollWaitMs) { waitUntil = v.NowMs + 250; return AutoAction.Nothing; }
          State = "failed"; FailWhy = "scroll"; Say("scroll: the game's list scroll stayed unreadable for " + (v.NowMs - noScrollSince) + " ms - no cell click on an estimate");
          return AutoAction.Nothing;
        }
        double cx = g.CellCentreX(p[1], v.W, v.U);
        var pc = ClosePopupFor(v, cx, cy); if (pc != null) return pc;
        if (clicks >= MaxClicks) return Fail();
        clicks++;
        meant = next; meantCol = p[1]; curBefore = s.Current; selBefore = new HashSet<long>(s.Selected); clickY = cy; clickX = cx; checkClick = true; clickAt = v.NowMs; waitUntil = v.NowMs + Late(AfterClickMs);
        lastPopup = popup; popup = Enhanceable(v, next);
        Say("click " + next + " r" + p[0] + "c" + p[1] + " y" + (int)cy + (exact ? " exact" : phased ? " phased" : " est"));
        lastAct = 1;
        return new AutoAction { Kind = AutoKind.Click, X = cx, Y = cy, Cell = next };
      }
      if (drags >= MaxDrags) return Fail();
      // the drag starts inside the view. Up (the list's later rows) its end may leave the list towards the top edge (the game keeps following
      // the pointer; never below the list: «Массовая продажа» / «Продать» lie under it), down it ends inside the view. The target is brought a
      // quarter of the view in from the edge it comes in at: the rows after it in the sweep come into view with it, and a drag off by the
      // gain's error still leaves it inside the view
      double span = viewBottom - viewTop - cell, spanUp = Math.Max(span, (viewBottom - cell / 2) - UpEndTop * v.H);
      double aim = cy > mid ? viewTop + (viewBottom - viewTop) * AimIn : viewBottom - (viewBottom - viewTop) * AimIn;
      double want = aim - cy, gd = GainFor(want);
      double dy = Math.Max(-spanUp, Math.Min(span, want / gd));
      if (Math.Sign(dy) != endDir) { endDir = 0; endSeen = 0; }
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      double y0 = dy < 0 ? viewBottom - cell / 2 : viewTop + cell / 2;
      Say("drag " + (int)dy + " for " + next + " r" + p[0] + " y" + (int)cy);
      drags++; beforeDrag = row1; lastDrag = dy; measure = true; lastAct = 2; lastRow1 = row1; lastExact = exact; lastPhased = phased;
      row1 += dy * GainFor(dy); exact = phased = false;
      // with the game's own scroll no fixed wait: the scroll is awaited until it moves (then the host's two equal reads 50 ms apart: still)
      if (!double.IsNaN(v.Row1)) { dragWait = true; dragFrom = v.Row1; dragAt = v.NowMs; waitUntil = v.NowMs; }
      else waitUntil = v.NowMs + Late(AfterDragMs);
      return new AutoAction { Kind = AutoKind.Drag, X = g.StripCentreX(v.W, v.U), Y = y0, DY = dy };
    }

    // the rows' phase seen on the screen: the item row nearest the middle moves to the nearest position with that phase
    void Snap(SellView v) {
      var s = v.Screen; double pitch = g.PitchY * v.U, mid = (g.ViewTopPx(v.U) + g.ViewBottomPx(v.H, v.U)) / 2;
      int best = 0; double bd = double.MaxValue;
      for (int r = 1; r < s.Types.Length; r++) {
        if (s.Types[r] != 1) continue;
        double d = Math.Abs(row1 + g.RowOffset(s.Types, r) * v.U + pitch / 2 - mid);
        if (d < bd) { bd = d; best = r; }
      }
      if (best == 0) return;
      double pred = row1 + g.RowOffset(s.Types, best) * v.U;
      double moved = ListTracker.Snap(pred, v.Phase, pitch) - pred;
      row1 += moved; Say("snap " + (int)moved);
      // the drag's real move (the correction is under half a row): the gain for that direction
      if (measure && Math.Abs(lastDrag) > pitch * 0.5) SetGain((row1 - beforeDrag) / lastDrag);
      phased = true;
    }

    AutoAction FilterStep(SellView v) {
      var s = v.Screen;
      if (double.IsNaN(g.PartX0)) return null;   // no filter on this screen (the inventory: a filter change clears the choice)
      if (!resetDone && Missing > 0) {
        resetDone = true; filterClicks++; waitUntil = v.NowMs + Late(AfterFilterMs);
        var r = g.Reset(v.W, v.H, v.U); return new AutoAction { Kind = AutoKind.Click, X = r[0], Y = r[1] };
      }
      if (v.Parts == null || v.Parts.Count == 0) return null;
      var want = new HashSet<int>(); foreach (var p in v.Parts) if (p >= 0 && p <= 4) want.Add(p);
      if (want.Count == 5) want.Clear();                       // every part: no part filter (the game shows all then)
      for (int p = 0; p <= 4; p++) {
        if (want.Contains(p) == s.Parts.Contains(p)) continue;
        filterClicks++; waitUntil = v.NowMs + Late(AfterFilterMs);
        var c = g.Part(p, v.W, v.H, v.U); return new AutoAction { Kind = AutoKind.Click, X = c[0], Y = c[1] };
      }
      return null;
    }

    // what the click did, from the item the game shows as clicked
    void AfterClick(SellView v) {
      var s = v.Screen;
      // the item whose selection changed is the one under the pointer (the item card, if the screen has one, too)
      long hit = 0; int changed = 0;
      foreach (var u in s.Selected) if (!selBefore.Contains(u)) { hit = u; changed++; }
      foreach (var u in selBefore) if (!s.Selected.Contains(u)) { hit = u; changed++; }
      if (changed != 1) hit = s.Current != curBefore || s.Current == meant ? s.Current : 0;
      if (hit == 0 && s.Selected.Contains(meant)) hit = meant;
      int[] p;
      Say("hit " + hit + " meant " + meant + (s.Selected.Contains(meant) ? " (selected)" : ""));
      if (hit != 0 && s.Pos.TryGetValue(hit, out p)) {
        // the row that was really under the pointer: the list position is known exactly now
        double cell = g.CellHeight * v.U;
        double newRow1 = clickY - cell / 2 - g.RowOffset(s.Types, p[0]) * v.U;
        if (measure && Math.Abs(lastDrag) > 1) {
          double moved = newRow1 - beforeDrag;
          // (only a click on the item meant proves where the list is; a stray hit may be a whole row off the estimate)
          if (Math.Sign(moved) == Math.Sign(lastDrag) && Math.Abs(moved) > Math.Abs(lastDrag) * 0.15) { SetGain(moved / lastDrag); endDir = 0; endSeen = 0; }
          else if (hit == meant) { if (endDir == Math.Sign(lastDrag)) endSeen++; else { endDir = Math.Sign(lastDrag); endSeen = 1; } }   // hardly moved: the list's end
        }
        measure = false;
        row1 = newRow1; exact = true; misses = 0;
        // another column: the columns are not where they were thought to be - take a stray item off, then stop
        if (p[1] != meantCol) { State = "failed"; Say("another column: " + p[1] + " for " + meantCol); }
        // a wanted item taken off by a stray click comes back next; again and again means the pilot is lost
        if (hit != meant && v.Wanted.Contains(hit) && !s.Selected.Contains(hit)) { int n; slips.TryGetValue(hit, out n); slips[hit] = ++n; if (n > 3) { State = "failed"; Say("slipped 4 times: " + hit); } }
        // a neighbour that is not wanted went on the choice: take it off again (same place)
        if (hit != meant && !v.Wanted.Contains(hit) && s.Selected.Contains(hit)) { undo = hit; undoY = clickY; undoX = clickX; undoTries = 0; }   // at the very place it was hit
      } else {
        // nothing clicked (a gap, a title): the estimate is off by part of a row. With the game's own scroll the place is exact: the click
        // was not taken or its answer is late (a slow laptop) - the same place again, never a guess beside it (it went off the cells)
        popup = true;   // perhaps «Быстрое улучшение» took the click: closed before the next one
        if (++misses >= MaxMiss) { State = "failed"; Say("missed " + misses + " times"); }
        else if (double.IsNaN(v.Row1)) { exact = false; row1 += (misses % 2 == 1 ? 1 : -1) * g.PitchY * v.U * 0.35 * misses; }   // +0.35, −0.7, +1.05 … of a row around the estimate
      }
    }

    /// <summary>A wait after an action: as given, or on a game seen answering late, at least its latest answer and a margin.</summary>
    long Late(long ms) { return lateMs > 0 ? Math.Max(ms, Math.Min(MaxLateMs, lateMs + LateMarginMs)) : ms; }

    // «Быстрое улучшение» opens by itself after a click on an item that can still be upgraded, over the lower middle of
    // the list (its «Улучшить», under the list, spends resources: never clicked). A click at (x, y) under it closes it
    // first on the empty spot right of the list (that also closes the item card; the game's m_bShow stays set, so the
    // pilot keeps its own idea of whether it is shown). Elsewhere the click goes straight on: the panel follows the item.
    AutoAction ClosePopupFor(SellView v, double x, double y) {
      if (!popup || !g.InPopup(x, y, v.W, v.U)) return null;
      popup = false; waitUntil = v.NowMs + Late(AfterCloseMs); Say("close"); lastAct = 4;
      var c = g.Close(v.W, v.H, v.U); return new AutoAction { Kind = AutoKind.Click, X = c[0], Y = c[1] };
    }

    bool Enhanceable(SellView v, long uid) { int lv; return v.Levels == null || !v.Levels.TryGetValue(uid, out lv) || lv < MaxLevel; }

    /// <summary>The host clicked the cell where the game has put it (its live rect), not at the pilot's point: the hit is judged from there.</summary>
    public void LivePoint(double x, double y) { if (lastAct == 1) { clickX = x; clickY = y; } else if (lastAct == 3) { undoX = x; undoY = y; clickY = y; } }

    AutoAction Undo(SellView v) {
      if (!v.Screen.Selected.Contains(undo)) { undo = 0; return AutoAction.Nothing; }
      if (++undoTries > 3) { undo = 0; return Fail(); }
      meant = undo; curBefore = v.Screen.Current; selBefore = new HashSet<long>(v.Screen.Selected); clickY = undoY; checkClick = false; lastPopup = popup; popup = Enhanceable(v, undo);
      clickAt = v.NowMs; waitUntil = v.NowMs + Late(AfterClickMs); lastAct = 3;
      return new AutoAction { Kind = AutoKind.Click, X = undoX, Y = undoY, Cell = undo };
    }

    // the wanted item to select next: one sweep through the list - the top one in view, else the next one below the view,
    // else the nearest above (items left behind: a stray click took them off, the list was scrolled by the player)
    long NextTarget(SellView v) {
      var s = v.Screen;
      double cell = g.CellHeight * v.U, top = g.ViewTopPx(v.U) + cell * 0.45, bottom = g.ViewBottomPx(v.H, v.U) - cell * 0.45;
      long inView = 0, below = 0, above = 0; double yIn = double.MaxValue, yBelow = double.MaxValue, yAbove = double.MinValue;
      foreach (var u in v.Wanted) {
        int[] p; if (s.Selected.Contains(u) || skip.Contains(u) || !s.Pos.TryGetValue(u, out p)) continue;
        double cy = row1 + g.RowOffset(s.Types, p[0]) * v.U + cell / 2 + p[1] * 0.01;
        if (cy >= top && cy <= bottom) { if (cy < yIn) { yIn = cy; inView = u; } }
        else if (cy > bottom) { if (cy < yBelow) { yBelow = cy; below = u; } }
        else if (cy > yAbove) { yAbove = cy; above = u; }
      }
      return inView != 0 ? inView : below != 0 ? below : above;
    }

    void Count(SellView v) {
      int sel = 0, miss = 0, extra = 0;
      foreach (var u in v.Wanted) { if (v.Screen.Selected.Contains(u)) sel++; else if (!v.Screen.Pos.ContainsKey(u) || skip.Contains(u)) miss++; }
      var want = v.Wanted as HashSet<long> ?? new HashSet<long>(v.Wanted);
      foreach (var u in v.Screen.Selected) if (!want.Contains(u)) extra++;
      Selected = sel; Missing = miss; Extra = extra;
    }

    double GainOr1 { get { return Gain > 0.2 ? Gain : 1; } }
    double GainFor(double dy) { double g2 = dy > 0 ? GainDown : GainUp; return g2 > 0.2 ? g2 : GainOr1; }
    void SetGain(double k) {
      if (k < 0.4 || k > 2) return;                      // not a plausible move (the list's end, a stray)
      if (lastDrag > 0) GainDown = GainDown > 0.2 ? (GainDown + k) / 2 : k; else GainUp = GainUp > 0.2 ? (GainUp + k) / 2 : k;
      Gain = k;
    }
    AutoAction Fail() { State = "failed"; Say("fail"); return AutoAction.Nothing; }
    void Say(string t) { Trace = Trace == "" ? t : Trace + "; " + t; }
  }
}
