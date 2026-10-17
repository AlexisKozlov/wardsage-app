// Wardsage — storage cleanup without auto click: what to tell the player next and which cells of the inventory's
// «Массовая продажа» grid get a frame. Pure decision logic, no Windows APIs and no clicks (tests: tests/CoreTests.cs);
// the host (app/Overlay.cs, app/HostBridge.cs) reads the inventory (src/SellScan.cs), places the rows on the screen
// (LocateGrid, as «Найти в игре» does) and draws. The player does every click; the selection is only read.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum SellHintKind {
    OpenInventory,   // the inventory is not open
    OpenEquipTab,    // the inventory is on another tab
    PressBulk,       // «Массовая продажа» is not on yet
    NeedPosition,    // in the grid, but the rows' place on the screen is not known yet (the list was scrolled)
    Select,          // items of the list are still to select (and/or a stray one is selected)
    AllSelected,     // every item of the list that is in the grid is selected: press «Продать»
    NoneHere         // none of the list's items is in the grid (sold, worn or hidden by the inventory's filter)
  }

  /// <summary>A frame on a cell: "todo" (list item, not selected), "done" (list item, selected), "extra" (selected, not in
  /// the list). Client px.</summary>
  public sealed class SellFrame {
    public long Uid;
    public string Style = "todo";
    public int X, Y, W, H;
  }

  public sealed class SellHintStep {
    public SellHintKind Kind;
    /// <summary>List items selected / list items in the grid / list items not in the grid / selected items not on the list.</summary>
    public int Selected, Total, Missing, Extra;
    /// <summary>List items still to select that are out of view below / above.</summary>
    public int Below, Above;
    public List<SellFrame> Frames = new List<SellFrame>();
    /// <summary>The page's key of the step (ui/sellhint.js).</summary>
    public string Key { get { string k = Kind.ToString(); return k.Substring(0, 1).ToLowerInvariant() + k.Substring(1); } }
    /// <summary>What the screen's selection says, without the scroll: a changed value is shown after it was read twice.</summary>
    public string StateKey { get { return Key + "|" + Selected + "|" + Total + "|" + Missing + "|" + Extra; } }
    /// <summary>Everything the page and the plates show.</summary>
    public string FullKey { get { return StateKey + "|" + Below + "|" + Above; } }
  }

  /// <summary>The one rule for everything the overlay draws over the game (equip helper, locate, storage cleanup, plates):
  /// only while the game window exists, is visible, is not minimized and is in front (or our own always-on-top window over it).
  /// Pure, so the host applies it centrally and the tests pin it down.</summary>
  public static class OverlayGate {
    /// <param name="hasWindow">the game window is known</param>
    /// <param name="visible">it is shown (IsWindowVisible)</param>
    /// <param name="minimized">it is minimized (IsIconic)</param>
    /// <param name="inFront">the foreground window is the game's, or our always-on-top window over the game</param>
    /// <param name="screenActive">the game screen the drawing belongs to is the active one (true when it has no own screen)</param>
    public static bool CanDraw(bool hasWindow, bool visible, bool minimized, bool inFront, bool screenActive) {
      return hasWindow && visible && !minimized && inFront && screenActive;
    }

    /// <summary>Is the foreground window "the game in front"? <paramref name="foregroundIsGame"/>: it belongs to the game's
    /// process; <paramref name="foregroundIsOwn"/> / <paramref name="ownTopmost"/>: it is a window of ours and an always-on-top one
    /// (the compact always-on-top window; the normal app window never counts, it is not a part of the game's picture).</summary>
    public static bool InFront(bool foregroundIsGame, bool foregroundIsOwn, bool ownTopmost) {
      return foregroundIsGame || (foregroundIsOwn && ownTopmost);
    }

    /// <summary>Clips a rectangle (x, y, w, h) to the game's client rect (x, y, w, h, same px); false = nothing of it is inside.</summary>
    public static bool Clip(int[] r, int cx, int cy, int cw, int ch, out int[] res) {
      int x0 = Math.Max(r[0], cx), y0 = Math.Max(r[1], cy), x1 = Math.Min(r[0] + r[2], cx + cw), y1 = Math.Min(r[1] + r[3], cy + ch);
      res = x1 > x0 && y1 > y0 ? new[] { x0, y0, x1 - x0, y1 - y0 } : null;
      return res != null;
    }
  }

  public static class SellHint {
    /// <summary>The step with frames placed from the game's own scroll: <paramref name="row1"/> = client y of row 1's top from
    /// InvScroll.Row1 (the exact scroll of the list, read every tick), NaN = the exact read is not available - then there are
    /// NO frames at all (the step keeps its counts: the text plate "marked X of N" is all that is shown), never guessed ones.</summary>
    public static SellHintStep BuildExact(InventoryScreen inv, ICollection<long> wanted, int cols, double row1, SellGeometry g, double W, double H, double U) {
      var st = Classify(inv, wanted);
      if (st.Kind != SellHintKind.Select || double.IsNaN(row1) || g == null) return st;
      var grid = new LocateGrid();
      grid.FromScroll(row1, inv.ListPtr);
      return Build(inv, wanted, cols, grid, g, W, H, U);
    }

    /// <summary>The step from what the inventory shows, without the rows' place on the screen (so never NeedPosition).</summary>
    public static SellHintStep Classify(InventoryScreen inv, ICollection<long> wanted) {
      var st = new SellHintStep();
      if (inv == null || !inv.Open) { st.Kind = SellHintKind.OpenInventory; return st; }
      if (!inv.EquipTab) { st.Kind = SellHintKind.OpenEquipTab; return st; }
      if (!inv.Bulk) { st.Kind = SellHintKind.PressBulk; return st; }
      var inList = new HashSet<long>(inv.Uids);
      var want = wanted as HashSet<long> ?? new HashSet<long>(wanted ?? new long[0]);
      foreach (var u in want) {
        if (inList.Contains(u)) { st.Total++; if (inv.Selected.Contains(u)) st.Selected++; }
        else st.Missing++;
      }
      foreach (var u in inv.Selected) if (!want.Contains(u)) st.Extra++;
      if (st.Total == 0 && st.Extra == 0) st.Kind = SellHintKind.NoneHere;
      else if (st.Total > 0 && st.Selected == st.Total && st.Extra == 0) st.Kind = SellHintKind.AllSelected;
      else st.Kind = SellHintKind.Select;
      return st;
    }

    /// <summary>The step with the frames of the cells in view. <paramref name="grid"/>: where the rows are on the screen
    /// (null or without a place = unknown: "NeedPosition", no frames).</summary>
    public static SellHintStep Build(InventoryScreen inv, ICollection<long> wanted, int cols, LocateGrid grid, SellGeometry g, double W, double H, double U) {
      var st = Classify(inv, wanted);
      if (st.Kind != SellHintKind.Select) return st;
      if (grid == null || !grid.Has || g == null) { st.Kind = SellHintKind.NeedPosition; return st; }
      if (cols < 1) cols = 1;
      var want = wanted as HashSet<long> ?? new HashSet<long>(wanted ?? new long[0]);
      var index = new Dictionary<long, int>();
      for (int i = 0; i < inv.Uids.Count; i++) index[inv.Uids[i]] = i;
      // the list's items in the grid, then the selected ones that are not on the list
      var uids = new List<long>();
      foreach (var u in want) if (index.ContainsKey(u)) uids.Add(u);
      foreach (var u in inv.Selected) if (!want.Contains(u) && index.ContainsKey(u)) uids.Add(u);
      foreach (var u in uids) {
        int i = index[u];
        bool wantedItem = want.Contains(u), selected = inv.Selected.Contains(u);
        var mark = grid.Place(g, i / cols + 1, i % cols + 1, W, H, U);
        string style = !wantedItem ? "extra" : selected ? "done" : "todo";
        if (mark.Kind == "box") st.Frames.Add(new SellFrame { Uid = u, Style = style, X = mark.X, Y = mark.Y, W = mark.W, H = mark.H });
        else if (style == "todo") { if (mark.Kind == "down") st.Below++; else st.Above++; }
      }
      st.Frames.Sort(delegate (SellFrame a, SellFrame b) { int c = a.Y.CompareTo(b.Y); return c != 0 ? c : a.X.CompareTo(b.X); });
      return st;
    }

    /// <summary>Keeps the frames that did not really move: a frame within <paramref name="tol"/> px of the same cell's frame
    /// drawn before (same style) stays where it was, so the picture does not shiver while the rows are tracked.</summary>
    public static List<SellFrame> Stabilize(List<SellFrame> prev, List<SellFrame> cur, int tol) {
      if (prev == null || prev.Count == 0) return cur;
      var byUid = new Dictionary<long, SellFrame>();
      foreach (var p in prev) byUid[p.Uid] = p;
      foreach (var f in cur) {
        SellFrame p;
        if (byUid.TryGetValue(f.Uid, out p) && p.Style == f.Style && Math.Abs(p.X - f.X) <= tol && Math.Abs(p.Y - f.Y) <= tol && Math.Abs(p.W - f.W) <= tol && Math.Abs(p.H - f.H) <= tol) {
          f.X = p.X; f.Y = p.Y; f.W = p.W; f.H = p.H;
        }
      }
      return cur;
    }

    /// <summary>The cell of the list item at (row, col) is found by position: used for the tests and for the host's click
    /// anchoring - the 1-based {row, col} of <paramref name="uid"/> in the grid, null when it is not in it.</summary>
    public static int[] Cell(InventoryScreen inv, long uid, int cols) {
      var p = inv == null ? null : LocateGuide.Position(inv.Uids.ToArray(), uid, cols);
      return p == null ? null : new[] { p[1], p[2] };
    }

    /// <summary>The one uid whose selection differs between two reads (0 when none or more than one changed).</summary>
    public static long OneChange(ICollection<long> before, ICollection<long> after) {
      long one = 0; int n = 0;
      var b = before as HashSet<long> ?? new HashSet<long>(before ?? new long[0]);
      var a = after as HashSet<long> ?? new HashSet<long>(after ?? new long[0]);
      foreach (var u in a) if (!b.Contains(u)) { one = u; n++; }
      foreach (var u in b) if (!a.Contains(u)) { one = u; n++; }
      return n == 1 ? one : 0;
    }
  }
}
