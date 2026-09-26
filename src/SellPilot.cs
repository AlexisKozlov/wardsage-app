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
  }

  public sealed class SellPilot {
    public const int AfterClickMs = 250, AfterDragMs = 500, AfterFilterMs = 1100, UserPauseMs = 4000, MaxClicks = 4000, MaxDrags = 400, MaxMiss = 8, MaxFilterClicks = 12, AfterCloseMs = 300, MaxLevel = 16;
    public const double MinDragPx = 24;

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

    public SellPilot(SellGeometry g) { this.g = g; }

    /// <summary>A new list from the site, or the run was stopped: start over.</summary>
    public void Reset() { list = 0; exact = phased = false; waitUntil = pauseUntil = 0; checkClick = false; undo = 0; clicks = drags = misses = filterClicks = endDir = endSeen = 0; resetDone = popup = false; slips.Clear(); skip.Clear(); State = "idle"; }

    public AutoAction Step(SellView v) {
      var s = v.Screen;
      if (s == null || !s.Open || s.Types == null || v.Wanted == null) { State = "idle"; return AutoAction.Nothing; }
      Count(v);
      // a new list (the screen was opened, the filter changed, a sale went through): it is at the top again
      // stopped: nothing more, but a stray item it selected is still taken back (below)
      if (State == "failed" && undo == 0 && !checkClick) return AutoAction.Nothing;
      if (s.ListPtr != list) { list = s.ListPtr; row1 = g.ViewTopPx(v.U) + g.TopPad * v.U; exact = true; checkClick = false; undo = 0; measure = false; }
      // after a drag: the stat bars on the screen give the rows' exact phase (the drag's error is well under half a row)
      if (!exact && !phased && !double.IsNaN(v.Phase)) Snap(v);
      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;
      bool failed = State == "failed";
      if (!v.Foreground) { if (!failed) State = "background"; return AutoAction.Nothing; }
      if (v.UserBusy || v.NowMs < pauseUntil) { if (!failed) State = "paused"; checkClick = false; return AutoAction.Nothing; }
      if (v.NowMs < waitUntil) { if (!failed) State = "work"; return AutoAction.Nothing; }

      if (checkClick) { checkClick = false; AfterClick(v); }
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
        double cx = g.CellCentreX(p[1], v.W, v.U);
        var pc = ClosePopupFor(v, cx, cy); if (pc != null) return pc;
        if (clicks >= MaxClicks) return Fail();
        clicks++;
        meant = next; meantCol = p[1]; curBefore = s.Current; selBefore = new HashSet<long>(s.Selected); clickY = cy; clickX = cx; checkClick = true; waitUntil = v.NowMs + AfterClickMs;
        popup = Enhanceable(v, next);
        Say("click " + next + " r" + p[0] + "c" + p[1] + " y" + (int)cy + (exact ? " exact" : phased ? " phased" : " est"));
        return new AutoAction { Kind = AutoKind.Click, X = cx, Y = cy };
      }
      if (drags >= MaxDrags) return Fail();
      double span = viewBottom - viewTop - cell;
      double want = -(cy - mid), gd = GainFor(want);
      double dy = Math.Max(-span, Math.Min(span, want / gd));
      if (Math.Sign(dy) != endDir) { endDir = 0; endSeen = 0; }
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      double y0 = dy < 0 ? viewBottom - cell / 2 : viewTop + cell / 2;
      Say("drag " + (int)dy + " for " + next + " r" + p[0] + " y" + (int)cy);
      drags++; beforeDrag = row1; lastDrag = dy; measure = true;
      row1 += dy * GainFor(dy); exact = phased = false;
      waitUntil = v.NowMs + AfterDragMs;
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
        resetDone = true; filterClicks++; waitUntil = v.NowMs + AfterFilterMs;
        var r = g.Reset(v.W, v.H, v.U); return new AutoAction { Kind = AutoKind.Click, X = r[0], Y = r[1] };
      }
      if (v.Parts == null || v.Parts.Count == 0) return null;
      var want = new HashSet<int>(); foreach (var p in v.Parts) if (p >= 0 && p <= 4) want.Add(p);
      if (want.Count == 5) want.Clear();                       // every part: no part filter (the game shows all then)
      for (int p = 0; p <= 4; p++) {
        if (want.Contains(p) == s.Parts.Contains(p)) continue;
        filterClicks++; waitUntil = v.NowMs + AfterFilterMs;
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
        // nothing clicked (a gap, a title): the estimate is off by part of a row
        exact = false; popup = true;   // perhaps «Быстрое улучшение» took the click: closed before the next one
        if (++misses >= MaxMiss) { State = "failed"; Say("missed " + misses + " times"); }
        else row1 += (misses % 2 == 1 ? 1 : -1) * g.PitchY * v.U * 0.35 * misses;   // +0.35, −0.7, +1.05 … of a row around the estimate
      }
    }

    // «Быстрое улучшение» opens by itself after a click on an item that can still be upgraded, over the lower middle of
    // the list (its «Улучшить», under the list, spends resources: never clicked). A click at (x, y) under it closes it
    // first on the empty spot right of the list (that also closes the item card; the game's m_bShow stays set, so the
    // pilot keeps its own idea of whether it is shown). Elsewhere the click goes straight on: the panel follows the item.
    AutoAction ClosePopupFor(SellView v, double x, double y) {
      if (!popup || !g.InPopup(x, y, v.W, v.U)) return null;
      popup = false; waitUntil = v.NowMs + AfterCloseMs; Say("close");
      var c = g.Close(v.W, v.H, v.U); return new AutoAction { Kind = AutoKind.Click, X = c[0], Y = c[1] };
    }

    bool Enhanceable(SellView v, long uid) { int lv; return v.Levels == null || !v.Levels.TryGetValue(uid, out lv) || lv < MaxLevel; }

    AutoAction Undo(SellView v) {
      if (!v.Screen.Selected.Contains(undo)) { undo = 0; return AutoAction.Nothing; }
      if (++undoTries > 3) { undo = 0; return Fail(); }
      meant = undo; curBefore = v.Screen.Current; selBefore = new HashSet<long>(v.Screen.Selected); clickY = undoY; checkClick = false; popup = Enhanceable(v, undo);
      waitUntil = v.NowMs + AfterClickMs;
      return new AutoAction { Kind = AutoKind.Click, X = undoX, Y = undoY };
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
