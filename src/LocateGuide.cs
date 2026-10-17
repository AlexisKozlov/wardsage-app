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
    AscendLoading,    // the Ascension list is not readable yet / just changed (a slot tab was pressed): wait, no filter advice
    AscendReady,      // the Ascension screen has the item selected: the player presses the Ascend button
    ExitBulk          // the inventory is in «Массовая продажа»: leave it first (the app clicks nothing there)
  }

  public sealed class LocateStep {
    public LocateKind Kind;
    public int Pos, Row, Col, Count;                // 1-based place in the list (0 = unknown), the list's length
    public string[] Missing = new string[0];        // foundry filter parts not set yet: slot, suit, main, subs
    public bool OnGear;                             // the foundry's button is on the hero's gear screen (not the inventory)
    public bool OnHero;                             // the item's hero is open: the slot can be pressed there
    public int Func;                                // recast on the gem screen: the function the item needs (7 = Recast, 8 = Advanced Recast; 0 = unknown)
    public bool WrongFunc;                          // ... and the screen has the other recast function selected
    public bool Worn;                               // the item is worn: its card in the inventory has both buttons (otherwise the gem button is alone)
    public bool OnGem;                              // recast: the item is on the gem screen (Done there = press «Улучшить», not the foundry's button)
    public string Scroll = "";                      // where the item is in the foundry's list by the game's own reading: visible | above | below ("" = unknown)
    public int ScrollRows;                          // rows to scroll for above / below
    public string FilterGo = "";                    // the filter pop-up is open: what to press in it: reset | suitRow | suitTab | suitCell | mainRow | subRow ("" = nothing / closed)
    public int SuitTab = -1, SuitIndex = -1;        // suitTab / suitCell: the set panel's tab (0 = 2 pieces, 1 = 3 pieces) and the set's place in the grid (0-based)
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

    /// <summary>How long the overlay keeps the last step while the screen reads Unknown (the form is being searched for).</summary>
    public const int HoldUnknownMs = 15000;

    /// <summary>Keep the previous step (no recompute, no frames) while the screen is Unknown for less than HoldUnknownMs; unknownSinceMs &lt; 0 = not Unknown.</summary>
    public static bool HoldOnUnknown(LocateStep prevStep, ScreenKind screenKind, long unknownSinceMs, long now) {
      return prevStep != null && prevStep.Kind != LocateKind.None && screenKind == ScreenKind.Unknown && unknownSinceMs >= 0 && now - unknownSinceMs < HoldUnknownMs;
    }

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
        if (rs != null && rs.OnAscend && rs.AscendSel == uid && Array.IndexOf(rs.AscendEquips, uid) >= 0) { st.Kind = LocateKind.AscendReady; return st; }
        if (onAccount == 0) { st.Kind = LocateKind.OtherAccount; return st; }
        if (rs != null && rs.OnAscend) {
          st.Count = rs.AscendEquips.Length;
          var ap = Position(rs.AscendEquips, uid, ColsOf(rs.AscendList));
          if (ap != null) { st.Pos = ap[0]; st.Row = ap[1]; st.Col = ap[2]; Where(st, rs.AscendList, ap[0] - 1); }
          st.Missing = MissingFilter(req.Item, rs.AscendParts, rs.AscendSuits, rs.AscendMainAttrs, rs.AscendSubAttrs);
          // the item is in the shown list (whatever its length): pick it; only an item the list does not hold needs the filter
          bool extra = ExtraSubFilter(req.Item, rs.AscendSubAttrs);
          // a long list and the item off the screen: the slot tab, then the set, narrow it before any scrolling (main / sub stats are not asked here)
          if (ap != null && LongOffScreen(st, rs.AscendList)) {
            var pre = new List<string>(); foreach (var m in st.Missing) if (m == "slot" || m == "suit") pre.Add(m);
            if (pre.Count > 0) { st.Missing = pre.ToArray(); st.Kind = LocateKind.AscendFilter; PopupStep(st, req.Item, rs, false); return st; }
          }
          if (ap != null) st.Kind = LocateKind.AscendPick;
          else if (rs.AscendList == null || !rs.AscendList.Has) st.Kind = LocateKind.AscendLoading;   // not read yet: nothing is known about the list
          else if (st.Missing.Length > 0 || extra) { st.Kind = LocateKind.AscendFilter; PopupStep(st, req.Item, rs, extra); }
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
      // the gem screen: the item is there and the function the item needs is selected = ready (the player presses «Улучшить» himself)
      int want = req.Purpose == "ascend" ? 0 : GemRows.FuncOfQuality(req.Item.Quality);
      bool gemOk = rs != null && rs.OnGemRecast && rs.GemSel == uid && (want == 0 || rs.GemFunc == want);
      if (rs != null && ((rs.OnRecast && rs.Sel == uid) || gemOk)) { st.Kind = LocateKind.Done; st.Func = want; st.OnGem = gemOk && !(rs.OnRecast && rs.Sel == uid); return st; }
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
      // the gem screen holds the item but not the function it needs (none chosen, or the other recast one)
      if (rs != null && rs.GemOpen && rs.GemSel == uid && !gemOk) { st.Kind = LocateKind.GemFunction; st.Func = want; st.WrongFunc = rs.OnGemRecast && want != 0 && rs.GemFunc != want; return st; }
      if (rs != null && rs.OnGemRecast) {
        st.Count = rs.GemList.Length;
        var p = Position(rs.GemList, uid, 1);
        if (p != null) { st.Pos = p[0]; st.Row = p[1]; st.Col = p[2]; st.Kind = LocateKind.GemPick; }
        else st.Kind = rs.GemList.Length == 0 ? LocateKind.GemPick : LocateKind.RecastNotInList;
        return st;
      }
      // the recast path stays through the inventory: item selected -> card button «Улучшение самоцветами» -> the gem screen's
      // «Преобразование» -> the item -> «Улучшить». The Foundry is only for Ascension. A worn item: its hero's slot (OnHero)
      return NextInInventory(req, inv, hero, cols, st);
    }

    /// <summary>The item is known to be off the screen (above / below the game's cells) in a list longer than two screens of cells.</summary>
    public static bool LongOffScreen(LocateStep st, RFX.ListRead l) {
      if (l == null || l.First < 0 || l.Last < l.First || (st.Scroll != "above" && st.Scroll != "below")) return false;
      return st.Count > 2 * (l.Last - l.First + 1);
    }

    /// <summary>The filter keeps a sub stat the item does not have (the list holds only items with all the chosen sub stats).</summary>
    public static bool ExtraSubFilter(PlanItem it, long[] subAttrs) {
      if (it == null || subAttrs == null) return false;
      foreach (var a in subAttrs) {
        bool has = false;
        foreach (var s in it.Subs) if (s.Stat == a) has = true;
        if (!has) return true;
      }
      return false;
    }

    /// <summary>With the filter pop-up open: what to press in it (reset | suitRow | suitTab | suitCell | mainRow | subRow). The slot
    /// switches are outside the pop-up and stay first (nothing here when the slot is still missing).</summary>
    public static void PopupStep(LocateStep st, PlanItem it, RecastScreen rs, bool extra) {
      st.FilterGo = ""; st.SuitTab = -1; st.SuitIndex = -1;
      if (rs == null || !rs.AscendFilterOpen || it == null || Array.IndexOf(st.Missing, "slot") >= 0) return;
      if (extra) { st.FilterGo = "reset"; return; }
      if (Array.IndexOf(st.Missing, "suit") >= 0) {
        if (rs.FilterPanel != 1) { st.FilterGo = "suitRow"; return; }
        int tab = Array.IndexOf(rs.FilterLeftSuits, (long)it.SetId) >= 0 ? 0 : Array.IndexOf(rs.FilterRightSuits, (long)it.SetId) >= 0 ? 1 : -1;
        if (tab < 0) return;   // the pop-up has no such set for the shown slots
        int idx = Array.IndexOf(rs.FilterShowSuits, (long)it.SetId);
        if (idx < 0) { st.FilterGo = "suitTab"; st.SuitTab = tab; return; }   // the other tab is shown
        st.FilterGo = "suitCell"; st.SuitTab = tab; st.SuitIndex = idx;
        return;
      }
      if (Array.IndexOf(st.Missing, "main") >= 0) { st.FilterGo = "mainRow"; return; }
      if (Array.IndexOf(st.Missing, "subs") >= 0) st.FilterGo = "subRow";
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
        if (inv.EquipTab && inv.Bulk) { st.Kind = LocateKind.ExitBulk; return st; }   // a click on a cell here would tick it for sale
        if (!inv.EquipTab) { st.Kind = LocateKind.OpenEquipTab; return st; }
        if (toFoundry) { st.Kind = LocateKind.OpenFoundry; return st; }
        if (inv.Current == uid) { st.Kind = LocateKind.Selected; st.Worn = req.HeroUid > 0; return st; }
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
    public bool WantPhase(long nowMs) {
      // never while the list moves (the picture lags the scroll value); a learned offset is checked once a second at most
      if (!off.StillFor(nowMs, off.Known ? ScrollOffset.RelearnStillMs : ScrollOffset.LearnStillMs)) return false;
      if (!off.Known ? nowMs - phaseAt >= 200 : nowMs - phaseAt >= 1000) { phaseAt = nowMs; return true; }
      return false;
    }

    /// <summary>Client y of row 1's top (can be far above the window); NaN = the scroll is not readable. <paramref name="phaseY"/>:
    /// a row top seen on the screen (NaN = none this look). <paramref name="nowMs"/>: the clock (ms) - then the picture offset is
    /// only learned while the list stands still; -1 = no clock.</summary>
    public double Row1(SellGeometry g, RFX.ListRead l, double phaseY, double U, long nowMs = -1) {
      if (l == null || !l.Has) return double.NaN;
      double units = l.RowUnits > 0 ? l.RowUnits : FallbackRowUnits, pitch = g.PitchY * U;
      double raw = g.ViewTopPx(U) + g.TopPad * U - l.ScrollY * pitch / units;
      return off.Place(raw, phaseY, pitch, U, nowMs);
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

  /// <summary>The gem screen's function list («Улучшение самоцветами»): the rows follow the game's config order (Recast, Advanced Recast,
  /// Refine, Advanced Refine, then the gem enhancements); a function the account has not unlocked is not in the list. Pure helpers.</summary>
  public static class GemRows {
    public const int Recast = 7, AdvancedRecast = 8;
    /// <summary>The functions in list order (config order).</summary>
    public static readonly int[] Order = { 7, 8, 5, 6, 1, 11, 2, 3, 4 };

    /// <summary>The recast function an item of this quality needs: mythic (6) and its variant (10) = Recast; ancient mythic (8) and the
    /// higher / variant ones (11, 12, 13) = Advanced Recast; 0 = unknown quality (either will do).</summary>
    public static int FuncOfQuality(int quality) {
      switch (quality) {
        case 6: case 10: return Recast;
        case 8: case 11: case 12: case 13: return AdvancedRecast;
        default: return 0;
      }
    }

    /// <summary>The 0-based row of a function in the list; <paramref name="absent"/> = functions known to be missing (not unlocked);
    /// -1 when the function is not in the list.</summary>
    public static int Index(int func, int[] absent = null) {
      int k = 0;
      foreach (int f in Order) {
        if (absent != null && Array.IndexOf(absent, f) >= 0) continue;
        if (f == func) return k;
        k++;
      }
      return -1;
    }
  }

  public sealed class LocateMark {
    public string Kind = "box";   // box | up | down
    public int X, Y, W, H, Rows;
  }
}
