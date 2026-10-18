// Wardsage - «Показать в игре»: where the frame (and the auto click) of a step goes, from the game's screen geometry
// (src/Geometry.cs) and, for lists, the game's own reading of the list (RFX.ReadList: scroll, row size, columns).
// Pure placement math, no Windows APIs (tests: tests/CoreTests.cs); the host (app/Overlay.cs) draws the frames and
// src/LocatePilot.cs clicks the targets that are safe to click. Without geometry (the site has no file for the game's
// version, or the app is offline with no saved copy) every function gives null and the steps stay text hints.
using System;

namespace RealmForge {
  /// <summary>The Foundry's right tab strip: the game hides tabs by what the account has unlocked, and the tabs left pack from
  /// the top, so the k-th visible tab sits k tab heights below the first position.</summary>
  public static class FoundryPlace {
    public static readonly string[] Tabs = { "forge", "identify", "ascend", "recast", "reforge" };
    /// <summary>One tab's height in units of U (the geometry's foundry.tab.* are 0.125 high, stacked without a gap).</summary>
    public const double TabPitch = 0.125;
    /// <summary>The list's first row sits at the list top minus the grid's top padding (-10 / -15 in the data: the grid starts a
    /// little below the viewport's top edge). Not verified on the live game: flip to +1 if the frames sit about a margin too low.</summary>
    public const double PadSign = -1;

    /// <summary>What is assumed to be shown when the host cannot read it: all five tabs (Ascension is third whichever of
    /// the others are hidden, as long as Forge and Identify are shown; only Recast / Reforge hide, and a hidden one frees its slot).</summary>
    public static bool[] AllTabs() { return new[] { true, true, true, true, true }; }

    /// <summary>The place of tab <paramref name="name"/> among the shown ones (0-based), -1 = hidden or unknown.</summary>
    public static int TabIndex(bool[] visible, string name) {
      int t = Array.IndexOf(Tabs, name);
      if (t < 0) return -1;
      if (visible == null || visible.Length != Tabs.Length) visible = AllTabs();
      if (!visible[t]) return -1;
      int k = 0;
      for (int i = 0; i < t; i++) if (visible[i]) k++;
      return k;
    }

    /// <summary>A tab's rectangle on the screen; null = no geometry, or the tab is hidden.</summary>
    public static GeoRect Tab(GameGeometry g, string name, bool[] visible, double W, double H, double U) {
      if (g == null) return null;
      int k = TabIndex(visible, name);
      var first = g.Rect("foundry.tab.forge", W, H, U);
      if (k < 0 || first == null) return null;
      first.Y += k * TabPitch * U;
      return first;
    }

    /// <summary>Where the list item at the 0-based <paramref name="index"/> is: the cell on the list's viewport from the geometry
    /// (<paramref name="prefix"/> "ascend" or "recast"), placed from the game's scroll reading <paramref name="l"/>. Null = it cannot be
    /// placed (no geometry or no reading).</summary>
    public static LocateFrame Cell(GameGeometry g, string prefix, RFX.ListRead l, int index, double W, double H, double U, double dx = 0) {
      if (g == null || l == null || !l.Has || index < 0) return null;
      var view = g.Rect(prefix + ".list", W, H, U);
      var cell = g.Rect(prefix + ".cell", W, H, U);
      var grid = g.Grid(prefix + ".list");
      if (view == null || cell == null || grid == null || view.W <= 0 || view.H <= 0) return null;
      view.X += dx;
      int cols = l.Cols >= 1 ? l.Cols : grid.Cols;
      double line = l.RowUnits > 0 ? l.RowUnits : grid.LineSize;
      double k = U / GameGeometry.CanvasH, pitch = line * k;
      int row = index / cols, col = index % cols;
      double rowTop = view.Y + PadSign * grid.PadTop * k + row * pitch - l.ScrollY * k;
      double slotW = view.W / cols;
      double cx = view.X + col * slotW + (slotW - cell.W) / 2, cy = rowTop + (pitch - cell.H) / 2;
      double mid = cy + cell.H / 2, viewMid = view.Y + view.H / 2;
      var f = new LocateFrame { Target = "cell", ScrollPx = viewMid - mid };
      var m = new LocateMark { X = (int)Math.Round(cx), W = (int)Math.Round(cell.W) };
      if (mid < view.Y) { m.Kind = "up"; m.Y = (int)Math.Round(view.Y); m.Rows = Math.Max(1, (int)Math.Ceiling((view.Y - rowTop) / pitch)); }
      else if (mid > view.Bottom) { m.Kind = "down"; m.Y = (int)Math.Round(view.Bottom); m.Rows = Math.Max(1, (int)Math.Ceiling((rowTop + pitch - view.Bottom) / pitch)); }
      else {
        double t = Math.Max(cy, view.Y), b = Math.Min(cy + cell.H, view.Bottom);   // clipped to the viewport
        m.Kind = "box"; m.Y = (int)Math.Round(t); m.H = (int)Math.Round(b - t);
        f.Clickable = (b - t) >= cell.H * 0.9;   // a cell cut by the viewport edge is scrolled in first
        f.ClickX = cx + cell.W / 2; f.ClickY = (t + b) / 2;
      }
      f.Mark = m;
      return f;
    }
  }

  /// <summary>A frame for a step: what it points at, where, and whether the auto mode may click it.</summary>
  public sealed class LocateFrame {
    public string Target = "";            // geometry key, "cell" for a list item
    public LocateMark Mark = new LocateMark();   // box | up | down in client px (up / down: x, the edge and the cell's width)
    public bool Clickable;                // the auto mode may click it now (the rest are hints)
    public double ClickX, ClickY;         // client px
    public GeoRect Raw;                   // the geometry's own rectangle (client px, before the legacy art inset), for the outline of the new highlight; null for a list cell
    public double ScrollPx;               // list cell: how far the content has to move to put the cell in the middle (+ = down)
    public double ViewX, ViewY, ViewW, ViewH;   // list cell: the viewport (client px), where a scrolling drag goes
  }

  public static class LocateFrames {
    /// <summary>The visible part of a button's picture inside its image rectangle, as insets {left, top, right, bottom} in canvas units
    /// (640 = one U): the picture has an empty margin (mostly below), the frame hugs the visible part.</summary>
    static readonly System.Collections.Generic.Dictionary<string, double[]> ArtInset = new System.Collections.Generic.Dictionary<string, double[]> {
      { "ascend.button", new[] { 10.0, 3.7, 11.0, 12.9 } }
    };

    /// <summary>The same step cut to its slot part only (what the auto mode may click while the item is in the list but off screen).</summary>
    public static LocateStep SlotOnly(LocateStep st) {
      return new LocateStep { Kind = LocateKind.AscendFilter, Missing = new[] { "slot" } };
    }

    /// <summary>How far the filter pop-up sits from the authored place: the game puts its left edge at the filter button's right edge.
    /// NaN = no geometry for it.</summary>
    public static double PopupShift(GameGeometry g, double W, double H, double U) {
      var btn = g.Rect("ascend.filter", W, H, U); var root = g.Rect("filter.root", W, H, U);
      return btn == null || root == null ? double.NaN : btn.Right - root.X;
    }

    static LocateFrame PopupFrame(LocateStep st, RecastScreen rs, GameGeometry g, double W, double H, double U) {
      double dx = PopupShift(g, W, H, U);
      if (double.IsNaN(dx)) return null;
      string key = "";
      switch (st.FilterGo) {
        case "reset": key = "filter.reset"; break;
        case "suitRow": key = "filter.suitOpen"; break;
        case "mainRow": key = "filter.mainOpen"; break;
        case "subRow": key = "filter.subOpen"; break;
        case "suitTab": key = "filter.suit.tab." + st.SuitTab; break;
        case "suitCell": {
          if (rs == null || st.SuitIndex < 0) return null;
          var f = FoundryPlace.Cell(g, "filter.suit", rs.FilterSuitGrid, st.SuitIndex, W, H, U, dx);
          var vw = g.Rect("filter.suit.list", W, H, U);
          if (f != null) f.Clickable = false;   // the filter is the player's
          if (f != null && vw != null) { f.ViewX = vw.X + dx; f.ViewY = vw.Y; f.ViewW = vw.W; f.ViewH = vw.H; }
          return f;
        }
        default: return null;
      }
      var r = g.Rect(key, W, H, U);
      if (r == null) return null;
      r.X += dx;
      return Box(key, r, false);
    }

    static LocateFrame Box(string key, GeoRect r, bool click) {
      var fr = new LocateFrame { Target = key, Clickable = click, ClickX = r.CX, ClickY = r.CY };
      fr.Raw = new GeoRect { X = r.X, Y = r.Y, W = r.W, H = r.H };
      fr.Mark = new LocateMark { Kind = "box", X = (int)Math.Round(r.X), Y = (int)Math.Round(r.Y), W = (int)Math.Round(r.W), H = (int)Math.Round(r.H) };
      return fr;
    }

    /// <summary>The inputs a list cell is placed from, one log line (what the next live test compares with the screen).</summary>
    public static string PlacementInputs(LocateStep st, RecastScreen rs, GameGeometry g, double W, double H, double U) {
      if (st == null || rs == null || g == null) return "placement: no data";
      var l = rs.AscendList;
      var view = g.Rect("ascend.list", W, H, U); var cell = g.Rect("ascend.cell", W, H, U); var grid = g.Grid("ascend.list");
      return "placement: index=" + (st.Pos - 1) + " listLen=" + rs.AscendEquips.Length + " cols=" + (l != null ? l.Cols : 0) + " scrollY=" + (l != null && l.Has ? l.ScrollY.ToString("0.#") : "none")
        + " lineSize=" + (l != null ? l.RowUnits.ToString("0.#") : "0") + " (geometry " + (grid != null ? grid.LineSize.ToString("0.#") + "/" + grid.Cols + "/" + grid.PadTop.ToString("0.#") : "none") + ")"
        + " view=" + (view != null ? (int)view.X + "," + (int)view.Y + "," + (int)view.W + "," + (int)view.H : "none")
        + " cell=" + (cell != null ? (int)cell.W + "x" + (int)cell.H : "none") + " window=" + (int)W + "x" + (int)H;
    }

    /// <summary>The slot toggle the step wants, or -1.</summary>
    static int SlotOf(LocateStep st, PlanItem item) {
      return st.Missing != null && Array.IndexOf(st.Missing, "slot") >= 0 && item != null && item.Slot >= 0 && item.Slot <= 4 ? item.Slot : -1;
    }

    /// <summary>Whether a frame (not a click) may be drawn for this kind of step at all.</summary>
    public static bool Framed(LocateKind k) {
      switch (k) {
        case LocateKind.OpenFoundry: case LocateKind.OpenAscendTab: case LocateKind.OpenRecastTab:
        case LocateKind.AscendFilter: case LocateKind.RecastFilter: case LocateKind.AscendPick: case LocateKind.RecastPick:
        case LocateKind.AscendReady: case LocateKind.Done: case LocateKind.Selected: case LocateKind.GemFunction: case LocateKind.ExitBulk:
          return true;
        default: return false;
      }
    }

    /// <summary>The frame of the step; null = none (no geometry, no such rectangle, not placeable). Clickable only for what the auto
    /// mode may press: «Литейная», the «Вознесение» tab, the slot tab, the item's cell of the Ascension list and the item card's gem button
    /// («Улучшение самоцветами», recast path). The final buttons («Вознести», «Преобразовать», «Улучшить») and the filter are never clickable.</summary>
    public static LocateFrame For(LocateStep st, PlanItem item, RecastScreen rs, bool[] tabs, GameGeometry g, double W, double H, double U) {
      if (st == null || g == null || !Framed(st.Kind)) return null;
      GeoRect r = null; string key = "";
      bool click = false;
      switch (st.Kind) {
        case LocateKind.OpenFoundry:
          key = st.OnGear ? "hero.workshop" : "inventory.workshop"; click = true; break;
        case LocateKind.OpenAscendTab:
          r = FoundryPlace.Tab(g, "ascend", tabs, W, H, U); key = "foundry.tab.ascend"; click = true; break;
        case LocateKind.OpenRecastTab:
          r = FoundryPlace.Tab(g, "recast", tabs, W, H, U); key = "foundry.tab.recast"; break;
        case LocateKind.AscendFilter: case LocateKind.RecastFilter: {
          string p = st.Kind == LocateKind.AscendFilter ? "ascend" : "recast";
          int slot = SlotOf(st, item);
          if (slot >= 0) { key = p + ".slot." + slot; click = p == "ascend"; }
          else if (st.Kind == LocateKind.AscendFilter && st.FilterGo != "") { var pf = PopupFrame(st, rs, g, W, H, U); if (pf != null) return pf; key = p + ".filter"; }
          else key = p + ".filter";   // the filter itself is the player's
          break;
        }
        case LocateKind.AscendPick: case LocateKind.RecastPick: {
          if (rs == null) return null;
          bool asc = st.Kind == LocateKind.AscendPick;
          var l = asc ? rs.AscendList : rs.RecastList;
          var f = st.Pos > 0 ? FoundryPlace.Cell(g, asc ? "ascend" : "recast", l, st.Pos - 1, W, H, U) : null;
          if (f == null) f = ByScrollWord(st, g, asc ? "ascend" : "recast", W, H, U);
          if (f == null) return null;
          var vw = g.Rect((asc ? "ascend" : "recast") + ".list", W, H, U);
          if (vw != null) { f.ViewX = vw.X; f.ViewY = vw.Y; f.ViewW = vw.W; f.ViewH = vw.H; }
          f.Clickable = f.Clickable && asc;
          return f;
        }
        case LocateKind.AscendReady: key = "ascend.button"; break;
        case LocateKind.ExitBulk: key = "inventory.bulkBack"; break;   // pointed at only: the app never clicks in the bulk-sale mode
        case LocateKind.Done: key = st.OnGem ? "gem.button" : "recast.button"; break;
        case LocateKind.GemFunction: return GemRow(g, rs, st.Func != 0 ? st.Func : GemRows.Recast, W, H, U);
        case LocateKind.Selected: key = st.Worn ? "inventory.cardLeft" : "inventory.cardSolo"; click = true; break;   // a worn item: Gems | Remove; otherwise the gem button is alone, centred (the recast path clicks it; the pilot checks the purpose)
      }
      if (r == null && key != "" && key != "foundry.tab.ascend" && key != "foundry.tab.recast") r = g.Rect(key, W, H, U);
      if (r == null) return null;
      double[] ins; var raw = r;
      if (ArtInset.TryGetValue(key, out ins)) {
        double k = U / GameGeometry.CanvasH;
        r = new GeoRect { X = r.X + ins[0] * k, Y = r.Y + ins[1] * k, W = r.W - (ins[0] + ins[2]) * k, H = r.H - (ins[1] + ins[3]) * k };
      }
      var res = Box(key, r, click);
      res.Raw = raw;
      return res;
    }

    /// <summary>The gem screen's list row of a recast function (a hint; clickable only when asked: the recast path selects the row, never the
    /// «Улучшить» button). The game's own reading of the list places it; when that is unreadable the list is taken at the top (it opens there).
    /// Null = no geometry, or the function is not in the list.</summary>
    public static LocateFrame GemRow(GameGeometry g, RecastScreen rs, int func, double W, double H, double U, bool click = false) {
      int row = GemRows.Index(func);
      var grid = g != null ? g.Grid("gem.list") : null;
      if (row < 0 || grid == null) return null;
      var l = rs != null ? rs.GemFuncList : null;
      if (l == null || !l.Has) l = new RFX.ListRead { ScrollY = 0, Cols = 1, RowUnits = grid.LineSize };
      var f = FoundryPlace.Cell(g, "gem", l, row, W, H, U);
      if (f != null && !click) f.Clickable = false;   // Cell: clickable only when the row is fully in the list's viewport
      return f;
    }

    /// <summary>The item's mark on the inventory list's scroll bar: {x, track top, track bottom, dot y, bracket top, bracket bottom} in client px
    /// (the same strip the plan's hints draw), from the list's first row (<paramref name="row1"/>, client y of its top) and the list's own
    /// numbers: <paramref name="viewTop"/> and <paramref name="topPad"/> as the grid lays out, <paramref name="pitch"/> one row, all px.
    /// Null = no geometry for the bar, or the row is not in the list.</summary>
    public static double[] ListMarker(GameGeometry g, double row1, double viewTop, double topPad, double pitch, int rows, int row, double W, double H, double U) {
      var bar = g != null ? g.Rect("inventory.scrollbar", W, H, U) : null;
      var view = g != null ? g.Rect("inventory.listView", W, H, U) : null;
      if (bar == null || bar.H <= 0 || rows < 1 || row < 1 || row > rows || pitch <= 0) return null;
      double total = Math.Max(rows * pitch + 2 * topPad, 1), viewLen = view != null && view.H > 0 ? view.H : bar.H;
      double scroll = viewTop - (row1 - topPad), itemY = topPad + (row - 0.5) * pitch;   // from the content's top
      Func<double, double> at = v => bar.Y + bar.H * Math.Max(0, Math.Min(1, v / total));
      double b0 = at(scroll), b1 = at(scroll + viewLen);
      if (b1 - b0 < 6) b1 = Math.Min(bar.Bottom, b0 + 6);
      return new[] { bar.CX, bar.Y, bar.Bottom, at(itemY), b0, b1 };
    }

    // the exact scroll is unreadable but the game's own cell range says the item is above / below: only the pill, at the viewport edge
    static LocateFrame ByScrollWord(LocateStep st, GameGeometry g, string prefix, double W, double H, double U) {
      if (st.Scroll != "above" && st.Scroll != "below") return null;
      var view = g.Rect(prefix + ".list", W, H, U); var cell = g.Rect(prefix + ".cell", W, H, U);
      if (view == null || cell == null) return null;
      bool up = st.Scroll == "above";
      var m = new LocateMark { Kind = up ? "up" : "down", X = (int)Math.Round(view.X + (view.W - cell.W) / 2), W = (int)Math.Round(cell.W),
                               Y = (int)Math.Round(up ? view.Y : view.Bottom), Rows = Math.Max(1, st.ScrollRows) };
      return new LocateFrame { Target = "cell", Mark = m };
    }

    /// <summary>The inventory list cell of a row/column for a grid whose row 1 is placed: the mark (box / up / down) and, for up / down, the item's mark on the
    /// list's scroll bar (null for a box, or when there is no geometry for the bar). Client px; one place for the overlay and the recording replay.</summary>
    public static LocateMark ListScene(GameGeometry g, LocateGrid grid, SellGeometry lg, int uidCount, int cols, int row, int col, double W, double H, double U, out double[] marker) {
      var mk = grid.Place(lg, row, col, W, H, U);
      marker = mk.Kind != "box" ? ListMarker(g, grid.Row1, lg.ViewTopPx(U), lg.TopPad * U, lg.PitchY * U, (uidCount + cols - 1) / cols, row, W, H, U) : null;
      return mk;
    }

    public static LocateMark ListScene(GameGeometry g, double row1, SellGeometry lg, int uidCount, int cols, int row, int col, double W, double H, double U, out double[] marker) {
      var grid = new LocateGrid(); grid.FromScroll(row1, 0);
      return ListScene(g, grid, lg, uidCount, cols, row, col, W, H, U, out marker);
    }

    /// <summary>The frame around the city bar's «Инвентарь» button {x, y, w, h} in client px: its centre is <paramref name="invDX"/> U from the right and
    /// <paramref name="btnDY"/> U from the bottom, its size from the geometry.</summary>
    public static int[] CityButton(GameGeometry g, double W, double H, double U, double invDX, double btnDY) {
      double cx = W - invDX * U, cy = H - btnDY * U;
      var bs = CityButtonSize(g, U); double bw = bs[0], bh = bs[1];
      return new[] { (int)(cx - bw / 2), (int)(cy - bh / 2), (int)bw, (int)bh };
    }

    /// <summary>The city bar's button size in px (the frame around «Инвентарь» there): the geometry's hit area, else the old estimate.</summary>
    public static double[] CityButtonSize(GameGeometry g, double U) {
      var s = g != null ? g.Dims("city.barButton") : null;
      return s != null && s[0] > 0 && s[1] > 0 ? new[] { s[0] * U, s[1] * U } : new[] { 0.11 * U, 0.13 * U };
    }
  }
  /// <summary>«Продвинуть» an artifact (purpose artPromote): the frames of its steps - the inventory's «Артефакт» toggle, the artifact's cell in the tab's
  /// list, «Мастерская», the workshop's «Продвинуть» tab, the copy in its list, the «Продвинуть» button. Frames and pills only: nothing here is ever
  /// clickable (the app does not click for this request). The site's geometry file gives the rectangles; where its copy lacks a key the research numbers
  /// (work/artifact-screens.md, prefab measurements in U) stand in. Pure placement math (tests/CoreTests.cs).</summary>
  public static class ArtFrames {
    // key -> {x anchor, x0, x1, y anchor top, y0, y1 (anchors as the geometry file's: L C R / T M B)}; the bottom anchor is the 7th element
    static readonly System.Collections.Generic.Dictionary<string, object[]> Research = new System.Collections.Generic.Dictionary<string, object[]> {
      { "inventory.leftTab.artifact", new object[] { 'L', 0.0023, 'L', 0.1711, 'T', 0.4022, 'T', 0.5272 } },
      { "inventory.artList",          new object[] { 'L', 0.195, 'R', -0.5231, 'T', 0.1922, 'B', -0.14 } },
      { "inventory.artWorkshop",      new object[] { 'R', -0.2562, 'R', -0.0344, 'B', -0.1030, 'B', -0.0202 } },
      { "artifact.tab.promote",       new object[] { 'R', -0.1631, 'R', 0.0025, 'T', 0.125, 'T', 0.25 } },
      { "artifact.tab.enhance",       new object[] { 'R', -0.1631, 'R', 0.0025, 'T', 0.0, 'T', 0.125 } },
      { "artifact.matList",           new object[] { 'L', 0.0289, 'L', 0.4117, 'T', 0.1141, 'B', -0.0876 } },
      { "artifact.promoteBtn",        new object[] { 'C', -0.3109, 'C', 0.05, 'M', 0.2812, 'M', 0.4 } },
      // the hero screen's artifact steps of «Надеть в игре» (src/ArtEquip.cs): the prefab rectangles in the shown state of the sliding panels (tools/ui-geometry.py ART_SHOW;
      // the file adds the shape and the inset of the visible art). The right panel's x is 442 units (0.6906 U) left of its authored (hidden) place.
      { "hero.artifactTab",           new object[] { 'R', -0.1656, 'R', 0.0, 'T', 0.7469, 'T', 0.8719 } },
      { "hero.artOpenList",           new object[] { 'R', -0.2953, 'R', -0.2281, 'T', 0.1969, 'T', 0.2625 } },   // the card's swap icon (BtnDecompose, a circle); its art matches a live 1920x1009 recording
      { "hero.artAdd",                new object[] { 'R', -0.5034, 'R', -0.3809, 'M', -0.3993, 'M', -0.2366 } },
      { "hero.artList",               new object[] { 'L', 0.0289, 'L', 0.4117, 'T', 0.1609, 'B', -0.1906 } },
      { "hero.artReplace",            new object[] { 'L', 0.7031, 'L', 0.925, 'B', -0.1413, 'B', -0.0584 } },
      { "hero.artSell",               new object[] { 'L', 0.8969, 'L', 0.9672, 'T', 0.2591, 'T', 0.3294 } },
      { "hero.artQuickEquip",         new object[] { 'R', -0.6687, 'R', -0.4469, 'B', -0.1008, 'B', -0.018 } },
      { "popup.equipReplace",         new object[] { 'C', 0.0031, 'C', 0.3641, 'M', 0.0563, 'M', 0.175 } },
    };

    static double X(char a, double off, double W, double U) { return (a == 'L' ? 0 : a == 'C' ? W / 2 : W) + off * U; }
    static double Y(char a, double off, double H, double U) { return (a == 'T' ? 0 : a == 'M' ? H / 2 : H) + off * U; }

    /// <summary>The rectangle of a key: the geometry file's, else the research numbers; null = neither.</summary>
    public static GeoRect Rect(GameGeometry g, string key, double W, double H, double U) {
      var r = g != null ? g.Rect(key, W, H, U) : null;
      if (r != null && r.W > 2 && r.H > 2) return r;
      object[] f;
      if (!Research.TryGetValue(key, out f)) return null;
      double x0 = X((char)f[0], (double)f[1], W, U), x1 = X((char)f[2], (double)f[3], W, U), y0 = Y((char)f[4], (double)f[5], H, U), y1 = Y((char)f[6], (double)f[7], H, U);
      return new GeoRect { X = Math.Min(x0, x1), Y = Math.Min(y0, y1), W = Math.Abs(x1 - x0), H = Math.Abs(y1 - y0) };
    }

    /// <summary>Whether a frame is drawn for this kind of step.</summary>
    public static bool Framed(LocateKind k) {
      switch (k) {
        case LocateKind.ArtOpenTab: case LocateKind.ArtInList: case LocateKind.ArtSelected: case LocateKind.ArtOpenPromote:
        case LocateKind.ArtMatPick: case LocateKind.ArtPromote: case LocateKind.ArtLevelFirst: case LocateKind.ArtMatGuess:
          return true;
        default: return false;
      }
    }

    /// <summary>The frame of the step; null = none (no rectangle, or the cell cannot be placed because the list is unreadable).</summary>
    public static LocateFrame For(LocateStep st, InventoryScreen inv, ArtWorkshopScreen ws, GameGeometry g, double W, double H, double U) {
      if (st == null || !Framed(st.Kind)) return null;
      string key = "";
      switch (st.Kind) {
        case LocateKind.ArtOpenTab: key = "inventory.leftTab.artifact"; break;
        case LocateKind.ArtSelected: key = "inventory.artWorkshop"; break;
        case LocateKind.ArtOpenPromote: key = "artifact.tab.promote"; break;
        case LocateKind.ArtLevelFirst: key = ws != null && ws.Tab == 1 ? "" : "artifact.tab.enhance"; break;   // on the «Улучшить» tab itself: the plate only
        case LocateKind.ArtPromote: key = "artifact.promoteBtn"; break;
        case LocateKind.ArtMatGuess: key = "artifact.matList"; break;   // the whole list area: the cells cannot be placed without the game's scroll
        case LocateKind.ArtInList:
          return st.Pos > 0 && inv != null ? Cell(g, "inventory.artList", inv.List, st.Pos - 1, LocateGuide.ArtInvCols, 116, W, H, U) : null;
        case LocateKind.ArtMatPick:
          return st.Pos > 0 && ws != null ? Cell(g, "artifact.matList", ws.MatList, st.Pos - 1, LocateGuide.ArtMatCols, 114, W, H, U) : null;
      }
      if (key == "") return null;
      var r = Rect(g, key, W, H, U);
      if (r == null) return null;
      var fr = new LocateFrame { Target = key, Raw = new GeoRect { X = r.X, Y = r.Y, W = r.W, H = r.H } };
      fr.Mark = new LocateMark { Kind = "box", X = (int)Math.Round(r.X), Y = (int)Math.Round(r.Y), W = (int)Math.Round(r.W), H = (int)Math.Round(r.H) };
      return fr;
    }

    /// <summary>The card (80 x 110 canvas units, centred in its cell) at the 0-based list <paramref name="index"/> of the list in <paramref name="rectKey"/>,
    /// placed from the game's own scroll reading <paramref name="l"/> (exact, nothing from the picture): a box, or an "up" / "down" pill at the list's edge.
    /// Null = the list cannot be read or has no rectangle.</summary>
    public static LocateFrame Cell(GameGeometry g, string rectKey, RFX.ListRead l, int index, int fallbackCols, double fallbackLine, double W, double H, double U) {
      if (l == null || !l.Has || index < 0) return null;
      var view = Rect(g, rectKey, W, H, U);
      if (view == null || view.W <= 0 || view.H <= 0) return null;
      var grid = g != null ? g.Grid(rectKey) : null;
      int cols = l.Cols >= 1 ? l.Cols : grid != null ? grid.Cols : fallbackCols;
      double line = l.RowUnits > 0 ? l.RowUnits : grid != null && grid.LineSize > 0 ? grid.LineSize : fallbackLine;
      double padTop = grid != null ? grid.PadTop : 0;
      double k = U / GameGeometry.CanvasH, pitch = line * k, cardW = 80 * k, cardH = 110 * k;
      int row = index / cols, col = index % cols;
      double rowTop = view.Y + FoundryPlace.PadSign * padTop * k + row * pitch - l.ScrollY * k;
      double slotW = view.W / cols;
      double cx = view.X + col * slotW + (slotW - cardW) / 2, cy = rowTop + (pitch - cardH) / 2;
      double mid = cy + cardH / 2;
      var f = new LocateFrame { Target = "cell", ScrollPx = view.Y + view.H / 2 - mid };
      var m = new LocateMark { X = (int)Math.Round(cx), W = (int)Math.Round(cardW) };
      if (mid < view.Y) { m.Kind = "up"; m.Y = (int)Math.Round(view.Y); m.Rows = Math.Max(1, (int)Math.Ceiling((view.Y - rowTop) / pitch)); }
      else if (mid > view.Bottom) { m.Kind = "down"; m.Y = (int)Math.Round(view.Bottom); m.Rows = Math.Max(1, (int)Math.Ceiling((rowTop + pitch - view.Bottom) / pitch)); }
      else {
        double t = Math.Max(cy, view.Y), b = Math.Min(cy + cardH, view.Bottom);   // clipped to the viewport
        m.Kind = "box"; m.Y = (int)Math.Round(t); m.H = (int)Math.Round(b - t);
      }
      f.Mark = m; f.ViewX = view.X; f.ViewY = view.Y; f.ViewW = view.W; f.ViewH = view.H;
      return f;
    }
  }
}
