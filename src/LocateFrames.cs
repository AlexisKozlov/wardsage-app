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
    public static LocateFrame Cell(GameGeometry g, string prefix, RFX.ListRead l, int index, double W, double H, double U) {
      if (g == null || l == null || !l.Has || index < 0) return null;
      var view = g.Rect(prefix + ".list", W, H, U);
      var cell = g.Rect(prefix + ".cell", W, H, U);
      var grid = g.Grid(prefix + ".list");
      if (view == null || cell == null || grid == null || view.W <= 0 || view.H <= 0) return null;
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
    public double ScrollPx;               // list cell: how far the content has to move to put the cell in the middle (+ = down)
    public double ViewX, ViewY, ViewW, ViewH;   // list cell: the viewport (client px), where a scrolling drag goes
  }

  public static class LocateFrames {
    /// <summary>The slot toggle the step wants, or -1.</summary>
    static int SlotOf(LocateStep st, PlanItem item) {
      return st.Missing != null && Array.IndexOf(st.Missing, "slot") >= 0 && item != null && item.Slot >= 0 && item.Slot <= 4 ? item.Slot : -1;
    }

    /// <summary>Whether a frame (not a click) may be drawn for this kind of step at all.</summary>
    public static bool Framed(LocateKind k) {
      switch (k) {
        case LocateKind.OpenFoundry: case LocateKind.OpenAscendTab: case LocateKind.OpenRecastTab:
        case LocateKind.AscendFilter: case LocateKind.RecastFilter: case LocateKind.AscendPick: case LocateKind.RecastPick:
        case LocateKind.AscendReady: case LocateKind.Done: case LocateKind.Selected:
          return true;
        default: return false;
      }
    }

    /// <summary>The frame of the step; null = none (no geometry, no such rectangle, not placeable). Clickable only for what the auto
    /// mode may press: «Литейная», the «Вознесение» tab, the slot tab and the item's cell of the Ascension list. The final
    /// buttons («Вознести», «Преобразовать», the card's button) and the filter are never clickable.</summary>
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
          if (slot >= 0) { key = p + ".slot." + slot; click = p == "ascend"; } else key = p + ".filter";   // the filter itself is the player's
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
        case LocateKind.Done: if (st.OnGem) return null; key = "recast.button"; break;
        case LocateKind.Selected: key = "inventory.cardLeft"; break;
      }
      if (r == null && key != "" && key != "foundry.tab.ascend" && key != "foundry.tab.recast") r = g.Rect(key, W, H, U);
      if (r == null) return null;
      var fr = new LocateFrame { Target = key, Clickable = click, ClickX = r.CX, ClickY = r.CY };
      fr.Mark = new LocateMark { Kind = "box", X = (int)Math.Round(r.X), Y = (int)Math.Round(r.Y), W = (int)Math.Round(r.W), H = (int)Math.Round(r.H) };
      return fr;
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

    /// <summary>The city bar's button size in px (the frame around «Инвентарь» there): the geometry's hit area, else the old estimate.</summary>
    public static double[] CityButtonSize(GameGeometry g, double U) {
      var s = g != null ? g.Dims("city.barButton") : null;
      return s != null && s[0] > 0 && s[1] > 0 ? new[] { s[0] * U, s[1] * U } : new[] { 0.11 * U, 0.13 * U };
    }
  }
}
