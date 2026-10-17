// RealmForge — «Найти в игре»: what to tell the player next, and where the frame goes in the inventory's list.
// Pure decision logic, no Windows APIs and no clicks (tests: tests/CoreTests.cs); the host (app/Overlay.cs, app/HostBridge.cs)
// reads the game's screens and draws the hints. Without auto click the player does every click himself; with it
// src/LocatePilot.cs does the navigation up to the last button.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum LocateKind {
    None,             // no request
    NoItem,           // the item is not in the site's last snapshot
    OtherAccount,     // the game holds another account: the item is not there
    OpenInventory,    // open the city's inventory (or the hero's gear screen when the item is worn)
    OpenEquipTab,     // the inventory is open on another tab
    InList,           // the item is in the inventory's list: frame / arrow
    HiddenFilter,     // the item is on the account but not in the list: a filter hides it
    Selected,         // the item is selected in the inventory: press the card's button
    OpenRecastTab,    // the foundry is open on another tab
    RecastFilter,     // the foundry's list is long: set the slot / filter first
    RecastPick,       // the item is in the foundry's list: pick it
    RecastNotInList,  // the filter is set but the item is not in the list
    ChoosePending,    // a recast result waits for the choice
    GemPick,          // the gem screen: pick the item in its pop-up
    Done,             // the recast screen has the item selected
    OpenFoundry,      // «Ascension»: the «Foundry» button of the inventory's equipment tab / of the hero's gear screen (the foundry starts without the item)
    GemFunction,      // the gem screen holds the item: its recast function is still to choose
    AscendFilter,     // the Ascension list: set the slot / filter first
    OpenAscendTab,    // «Ascension»: the foundry is open on another tab
    AscendPick,       // the item is in the foundry's Ascension list: pick it
    AscendNotInList,  // the Ascension list does not hold the item (not ready for Ascension, or a filter hides it)
    AscendReady       // the Ascension screen has the item selected: the player presses the Ascend button
  }

  public sealed class LocateStep {
    public LocateKind Kind;
    public int Pos, Row, Col, Count;                // 1-based place in the list (0 = unknown), the list's length
    public string[] Missing = new string[0];        // foundry filter parts not set yet: slot, suit, main, subs
    public bool OnGear;                             // the foundry's button is on the hero's gear screen (not the inventory)
    public bool OnHero;                             // the item's hero is open: the slot can be pressed there
    public bool OnGem;                              // recast: the item is on the gem screen (Done there = press «Улучшить», not the foundry's button)
    public string Scroll = "";                      // where the item is in the foundry's list by the game's own reading: visible | above | below ("" = unknown)
    public int ScrollRows;                          // rows to scroll for above / below
    public bool NeedClick;                          // the inventory's list position is not known yet: the player is asked to click a cell
    public string Auto = "", AutoWhy = "";          // auto click (src/LocatePilot.cs): "" = off, else work | paused | background | hint | stopped, and why
    /// <summary>The page's key of the step (ui/locate.js).</summary>
    public string Key { get { return Kind.ToString().Substring(0, 1).ToLowerInvariant() + Kind.ToString().Substring(1); } }
  }

  /// <summary>The item's sub stats on the site against the game's now (types only: the game shows a value only once the stat
  /// is open).</summary>
  public sealed class SubCheck {
    public bool[] Match = new bool[0];   // per site sub stat (in the card's order): the game has that stat on the item
    public bool Changed;                 // the game's stat types differ from the site's: the item changed after the snapshot
  }

  public static class LocateGuide {
    /// <summary>Foundry lists up to this long are picked from directly; longer ones are filtered first.</summary>
    public const int ShortList = 8;

    /// <param name="onAccount">1 = the item is on the game's account, 0 = it is not, -1 = unknown (RFX.ItemOnAccount)</param>
    /// <param name="cols">columns of the inventory's grid</param>
    public static LocateStep Next(LocateRequest req, InventoryScreen inv, RecastScreen rs, HeroScreen hero, int cols, int onAccount) {
      var st = new LocateStep();
      if (req == null) { st.Kind = LocateKind.None; return st; }
      if (req.Item == null) { st.Kind = LocateKind.NoItem; return st; }
      long uid = req.Item.Uid;
      bool asc = req.Purpose == "ascend";
      st.OnHero = hero != null && req.HeroUid > 0 && hero.Hero == req.HeroUid && hero.Tab >= 0;
      if (asc) {
        if (rs != null && rs.OnAscend && rs.AscendSel == uid) { st.Kind = LocateKind.AscendReady; return st; }
        if (onAccount == 0) { st.Kind = LocateKind.OtherAccount; return st; }
        if (rs != null && rs.OnAscend) {
          st.Count = rs.AscendEquips.Length;
          var ap = Position(rs.AscendEquips, uid, ColsOf(rs.AscendList));
          if (ap != null) { st.Pos = ap[0]; st.Row = ap[1]; st.Col = ap[2]; Where(st, rs.AscendList, ap[0] - 1); }
          st.Missing = MissingFilter(req.Item, rs.AscendParts, rs.AscendSuits, rs.AscendMainAttrs, rs.AscendSubAttrs);
          if (st.Missing.Length > 0 && (ap == null || rs.AscendEquips.Length > ShortList)) st.Kind = LocateKind.AscendFilter;
          else if (ap != null) st.Kind = LocateKind.AscendPick;
          else st.Kind = LocateKind.AscendNotInList;
          return st;
        }
        if (rs != null && rs.SublimingOpen) { st.Kind = LocateKind.OpenAscendTab; return st; }
        // not in the foundry yet. The inventory's selection does not carry over (the foundry starts empty), so the item is
        // never looked for in the inventory: only the way to the foundry; the item is found in the foundry's own list.
        // The foundry's button is on the hero's gear screen and in the inventory (neither chooses an item, and a worn item's card
        // has no Ascension button): the screen the player is on is used. Worn or not does not matter: the foundry's list holds worn items too.
        bool invOpen = inv != null && inv.Open;
        if (!invOpen && hero != null && hero.Hero > 0 && hero.Tab == 3) { st.Kind = LocateKind.OpenFoundry; st.OnGear = true; return st; }
        return NextInInventory(req, inv, hero, cols, st, true);
      }
      if (rs != null && ((rs.OnRecast && rs.Sel == uid) || (rs.OnGemRecast && rs.GemSel == uid))) { st.Kind = LocateKind.Done; st.OnGem = rs.OnGemRecast && rs.GemSel == uid && !(rs.OnRecast && rs.Sel == uid); return st; }
      if (onAccount == 0) { st.Kind = LocateKind.OtherAccount; return st; }

      if (rs != null && rs.SublimingOpen && rs.CurType >= 0 && rs.CurType != 3) { st.Kind = LocateKind.OpenRecastTab; return st; }
      if (rs != null && rs.OnRecast) {
        st.Count = rs.Equips.Length;
        var p = Position(rs.Equips, uid, ColsOf(rs.RecastList));
        if (p != null) { st.Pos = p[0]; st.Row = p[1]; st.Col = p[2]; Where(st, rs.RecastList, p[0] - 1); }
        if (rs.ChoosePending) { st.Kind = LocateKind.ChoosePending; return st; }
        st.Missing = MissingFilter(req.Item, rs.SelectParts, rs.Suits, rs.MainAttrs, rs.SubAttrs);
        if (st.Missing.Length > 0 && (p == null || rs.Equips.Length > ShortList)) st.Kind = LocateKind.RecastFilter;
        else if (p != null) st.Kind = LocateKind.RecastPick;
        else st.Kind = LocateKind.RecastNotInList;
        return st;
      }
      if (rs != null && rs.OnGemRecast) {
        st.Count = rs.GemList.Length;
        var p = Position(rs.GemList, uid, 1);
        if (p != null) { st.Pos = p[0]; st.Row = p[1]; st.Col = p[2]; st.Kind = LocateKind.GemPick; }
        else st.Kind = rs.GemList.Length == 0 ? LocateKind.GemPick : LocateKind.RecastNotInList;
        return st;
      }
      if (rs != null && rs.GemOpen && rs.GemSel == uid) { st.Kind = LocateKind.GemFunction; return st; }
      // the recast path stays through the inventory: item selected -> card button «Улучшение самоцветами» -> the gem screen's
      // «Преобразование» -> the item -> «Улучшить». The Foundry is only for Ascension. A worn item: its hero's slot (OnHero)
      return NextInInventory(req, inv, hero, cols, st);
    }

    /// <summary>Columns of a list as the game says (1 = unknown: then the list is laid out one cell per row).</summary>
    public static int ColsOf(RFX.ListRead l) { return l != null && l.Cols >= 1 ? l.Cols : 1; }

    /// <summary>Where the item at the 0-based <paramref name="index"/> is against the cells the game's grid has on the screen
    /// now (its first / last created cell): sets Scroll to visible, or above / below with the rows to scroll.</summary>
    public static void Where(LocateStep st, RFX.ListRead l, int index) {
      st.Scroll = ""; st.ScrollRows = 0;
      if (l == null || l.First < 0 || l.Last < l.First || l.Cols < 1 || index < 0) return;
      int row = index / l.Cols, first = l.First / l.Cols, last = l.Last / l.Cols;
      if (row < first) { st.Scroll = "above"; st.ScrollRows = first - row; }
      else if (row > last) { st.Scroll = "below"; st.ScrollRows = row - last; }
      else st.Scroll = "visible";
    }

    // the part both purposes share: find the item in the inventory (or on the hero's gear screen); toFoundry: only the way to the foundry
    static LocateStep NextInInventory(LocateRequest req, InventoryScreen inv, HeroScreen hero, int cols, LocateStep st, bool toFoundry = false) {
      long uid = req.Item.Uid;
      if (inv != null && inv.Open) {
        if (!inv.EquipTab) { st.Kind = LocateKind.OpenEquipTab; return st; }
        if (toFoundry) { st.Kind = LocateKind.OpenFoundry; return st; }
        if (inv.Current == uid) { st.Kind = LocateKind.Selected; return st; }
        st.Count = inv.Uids.Count;
        var p = Position(inv.Uids.ToArray(), uid, cols);
        if (p == null) { st.Kind = LocateKind.HiddenFilter; return st; }
        st.Kind = LocateKind.InList; st.Pos = p[0]; st.Row = p[1]; st.Col = p[2];
        return st;
      }
      st.Kind = LocateKind.OpenInventory;
      return st;
    }

    /// <summary>The place of <paramref name="uid"/> in a list laid out in rows of <paramref name="cols"/>: {index, row, col},
    /// all 1-based; null when it is not in the list.</summary>
    public static int[] Position(long[] list, long uid, int cols) {
      if (list == null || uid <= 0) return null;
      if (cols < 1) cols = 1;
      for (int i = 0; i < list.Length; i++) if (list[i] == uid) return new[] { i + 1, i / cols + 1, i % cols + 1 };
      return null;
    }

    /// <summary>The parts of the foundry's filter that are not set for this item yet, in the order the player sets them:
    /// "slot", "suit", "main", "subs". The foundry's lists hold only items with all the chosen sub stats.</summary>
    public static string[] MissingFilter(PlanItem it, RecastScreen rs) {
      return rs == null ? new string[0] : MissingFilter(it, rs.SelectParts, rs.Suits, rs.MainAttrs, rs.SubAttrs);
    }

    public static string[] MissingFilter(PlanItem it, int[] parts, long[] suits, long[] mains, long[] subAttrs) {
      var r = new List<string>();
      if (it == null) return r.ToArray();
      if (Array.IndexOf(parts, it.Slot) < 0) r.Add("slot");
      if (it.SetId > 0 && Array.IndexOf(suits, it.SetId) < 0) r.Add("suit");
      long main = it.Main.Count > 0 ? it.Main[0].Stat : -1;
      if (main > 0 && Array.IndexOf(mains, main) < 0) r.Add("main");
      bool subs = false;
      foreach (var s in it.Subs) if (s.Stat >= 0 && Array.IndexOf(subAttrs, (long)s.Stat) < 0) subs = true;
      if (subs) r.Add("subs");
      return r.ToArray();
    }

    /// <summary>The item's sub stat types read from the game ({id, value} pairs) against the site's card.</summary>
    public static SubCheck CheckSubs(PlanItem it, long[][] live) {
      var c = new SubCheck();
      if (it == null || live == null) return c;
      var ids = new List<long>(); foreach (var p in live) if (p != null && p.Length > 0) ids.Add(p[0]);
      c.Match = new bool[it.Subs.Count];
      var site = new List<long>();
      for (int i = 0; i < it.Subs.Count; i++) {
        int s = it.Subs[i].Stat;
        c.Match[i] = s >= 0 && ids.Contains(s);
        if (s >= 0) site.Add(s);
      }
      if (site.Count > 0) foreach (var id in ids) if (!site.Contains(id)) c.Changed = true;
      foreach (var id in site) if (!ids.Contains(id)) c.Changed = true;
      return c;
    }
  }

  /// <summary>Where the inventory's rows are on the screen, from the stat bars under the cells (see ScrollAnchor for the hero
  /// screen's list). Rows are all item rows of one step. Client pixels.</summary>
  public sealed class LocateGrid {
    public bool Has;
    public double Row1;      // client y of row 1's top (can be far above the window)
    public ulong ListPtr;    // the list the anchor belongs to

    public bool Exact;       // Row1 comes from the game's own scroll reading (InvScroll), not from the picture

    public void Reset() { Has = false; ListPtr = 0; Exact = false; }

    /// <summary>Row 1's place from the game's scroll reading (<see cref="InvScroll"/>).</summary>
    public void FromScroll(double row1, ulong ptr) { Row1 = row1; Has = true; ListPtr = ptr; Exact = true; }

    /// <summary>A list just built opens at the top: anchor there when the bars on the screen agree (<paramref name="phase"/>:
    /// a row top mod the row step, client px).</summary>
    public bool FromTop(SellGeometry g, double phase, double U, ulong ptr) {
      double pitch = g.PitchY * U, top = g.ViewTopPx(U) + g.TopPad * U;
      double d = (phase - top) % pitch; if (d < 0) d += pitch; if (d > pitch / 2) d -= pitch;
      if (Math.Abs(d) > pitch * 0.12) return false;
      Row1 = top + d; Has = true; ListPtr = ptr;
      return true;
    }

    /// <summary>The player clicked the item of row <paramref name="row"/> with the pointer at <paramref name="cursorY"/>.</summary>
    public void FromClick(SellGeometry g, int row, double cursorY, double U, ulong ptr) {
      Row1 = cursorY - g.BarBottom * U / 2 - (row - 1) * g.PitchY * U; Has = true; ListPtr = ptr;
    }

    /// <summary>Follows the scrolling: the bars' phase snaps the estimate of an item row that is visible.</summary>
    public void Track(SellGeometry g, int refRow, double phase, double U) {
      if (!Has || refRow < 1) return;
      double pitch = g.PitchY * U, pred = Row1 + (refRow - 1) * pitch;
      Row1 += ListTracker.Snap(pred, phase, pitch) - pred;
    }

    public double RowTop(SellGeometry g, int row, double U) { return Row1 + (row - 1) * g.PitchY * U; }

    /// <summary>A row near the middle of the visible list (to take the phase from).</summary>
    public int VisibleRow(SellGeometry g, int rows, double W, double H, double U) {
      double mid = (g.ViewTopPx(U) + g.ViewBottomPx(H, U)) / 2;
      int best = 0; double bd = double.MaxValue;
      for (int r = 1; r <= rows; r++) { double d = Math.Abs(RowTop(g, r, U) + g.PitchY * U / 2 - mid); if (d < bd) { bd = d; best = r; } }
      return best;
    }

    /// <summary>The mark for the item at (row, col): "box" around the cell, or "up" / "down" with the rows to scroll.
    /// Rect in client px (for "up" / "down": x, the list's edge, and the cell's width).</summary>
    public LocateMark Place(SellGeometry g, int row, int col, double W, double H, double U) {
      double top = RowTop(g, row, U), left = g.X(g.X0 + (col - 1) * g.PitchX, W, U);
      double cell = g.CellW * U, full = g.BarBottom * U, pitch = g.PitchY * U;
      double mid = top + g.CellHeight * U / 2, vt = g.ViewTopPx(U), vb = g.ViewBottomPx(H, U);
      var m = new LocateMark { X = (int)left, W = (int)cell };
      if (mid < vt) { m.Kind = "up"; m.Y = (int)vt; m.Rows = (int)Math.Ceiling((vt - top) / pitch); }
      else if (mid > vb) { m.Kind = "down"; m.Y = (int)vb; m.Rows = (int)Math.Ceiling((top + full - vb) / pitch); }
      else { m.Kind = "box"; m.Y = (int)top; m.H = (int)full; }
      return m;
    }
  }

  /// <summary>Where row 1 of the inventory's grid is on the screen, from the game's own reading of the list's scroll (how far
  /// the content is scrolled, in the grid's row units) - one helper for «Найти в игре» and the storage cleanup, hints and auto.
  /// The picture only fixes a constant once (the offset between the game's units and the window, see ScrollOffset).</summary>
  public sealed class InvScroll {
    /// <summary>A row in the scroll's units when the grid's LineSize could not be read (the form lays rows out by 118).</summary>
    public const double FallbackRowUnits = 118;
    readonly ScrollOffset off = new ScrollOffset();
    long phaseAt;

    /// <summary>The picture is wanted (to learn the constant): not learned yet, or now and then to check it.</summary>
    public bool WantPhase(long nowMs) { if (!off.Known || nowMs - phaseAt >= 1000) { phaseAt = nowMs; return true; } return false; }

    /// <summary>Client y of row 1's top (can be far above the window); NaN = the scroll is not readable. <paramref name="phaseY"/>:
    /// a row top seen on the screen (NaN = none this look).</summary>
    public double Row1(SellGeometry g, RFX.ListRead l, double phaseY, double U) {
      if (l == null || !l.Has) return double.NaN;
      double units = l.RowUnits > 0 ? l.RowUnits : FallbackRowUnits, pitch = g.PitchY * U;
      double raw = g.ViewTopPx(U) + g.TopPad * U - l.ScrollY * pitch / units;
      return off.Place(raw, phaseY, pitch, U);
    }
  }

  /// <summary>Which feature owns the overlay when both an equip plan and a locate request are active: the one started most
  /// recently wins, the other waits (the app window shows both).</summary>
  public static class OverlayPriority {
    public const string None = "none", Plan = "plan", Locate = "locate";
    /// <param name="planSince">when the plan became active (ms, the same clock as locateSince)</param>
    public static string Winner(bool planActive, long planSince, bool locateActive, long locateSince) {
      if (planActive && locateActive) return locateSince >= planSince ? Locate : Plan;
      if (locateActive) return Locate;
      if (planActive) return Plan;
      return None;
    }
  }

  public sealed class LocateMark {
    public string Kind = "box";   // box | up | down
    public int X, Y, W, H, Rows;
  }
}
