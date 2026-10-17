// Wardsage - the ONE gate every auto click goes through (the equip helper, the storage cleanup, «Показать в игре»).
//
// The app never writes into the game and never injects; its only action is a mouse click / drag at a point of the game's window,
// and only when the player turned auto click on. A click is sent only when ALL of this holds:
//   (a) auto click is on (the player's setting and the site's switches) and the game is in front,
//   (b) the target is in the whitelist of the screen the game shows NOW (Rule below),
//   (c) the screen was read fresh right before the click and still is the expected one, with no pop-up / pending choice open,
//   (d) a list cell is the cell of the item meant (its place from the game's list order and its exact scroll),
//   (e) the previous click had its expected effect (a screen, tab or selection change) within the timeout.
// Anything else: the pilot of that owner stops until a new request, says why («Автонажатие остановлено: <причина>») and leaves
// only the hints. An unreadable screen is first waited for (reads fail for a moment now and then), then it is a stop.
//
// What is NEVER a target: «Вознести», «Преобразовать», «Улучшить», «Продать», the item card's button and every confirmation
// dialog of the game (except «Заменить» of the equip helper, and only with the player's own auto confirm setting).
//
// Pure decision logic, no Windows APIs (tests: tests/ScreenReplay.cs); the host (app/Overlay.cs) reads the screen and sends.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum ClickOwner { Locate, Sell, Equip }

  public enum ClickTarget {
    None,
    HeroBack,         // the hero screen's back arrow
    CityInventory,    // the city's «Инвентарь»
    InvEquipTab,      // the inventory's left menu «Снаряжение»
    InvBulkEnter,     // «Массовая продажа» (the storage cleanup only)
    InvListCell,      // an item's cell in the inventory list, normal mode (selects it; «Показать в игре»)
    InvBulkCell,      // an item's cell in the inventory list, bulk-sale mode (toggles its choice; the storage cleanup only)
    InvListDrag,      // a drag that scrolls the inventory list
    FoundryButton,    // «Литейная» (the inventory's or the hero gear screen's)
    AscendTab,        // the Foundry's «Вознесение» tab
    SlotTab,          // the Ascension list's slot tab
    AscendCell,       // an item's cell in the Ascension list (selects it)
    AscendDrag,       // a drag that scrolls the Ascension list
    EquipNav,         // equip helper: the city's «Герои», a hero's card, the gear tab, the back arrow, a slot, a gear list item, a list drag
    EquipFilter,      // equip helper: the controls of the game's gear / hero filter pop-up
    EquipClosePopup,  // equip helper: the red × of a pop-up left over the list
    EquipConfirm      // equip helper: «Заменить» (only with the player's auto confirm)
  }

  public enum GateVerdict { Allow, Wait, Deny }

  public sealed class GateResult {
    public GateVerdict Verdict;
    /// <summary>ok | off | background | pending | unknown | screen | modal | bulk | cell | target | nochange | halted</summary>
    public string Reason = "";
    public string Detail = "";
    public bool Allowed { get { return Verdict == GateVerdict.Allow; } }
    public static GateResult Allow(string d) { return new GateResult { Verdict = GateVerdict.Allow, Reason = "ok", Detail = d }; }
    public static GateResult Wait(string r, string d) { return new GateResult { Verdict = GateVerdict.Wait, Reason = r, Detail = d }; }
    public static GateResult Deny(string r, string d) { return new GateResult { Verdict = GateVerdict.Deny, Reason = r, Detail = d }; }
  }

  /// <summary>One click (or drag) the pilot wants to make.</summary>
  public sealed class ClickRequest {
    public ClickOwner Owner;
    public ClickTarget Target;
    /// <summary>(a) auto click is on for this owner, and the game is in front.</summary>
    public bool AutoOn, Front;
    /// <summary>(d) for a cell target: the item under the click point (0 = could not be worked out) and the items the pilot may click.</summary>
    public long CellUid;
    public ICollection<long> CellOk;
    /// <summary>The click point (client px), for the log.</summary>
    public double X, Y;
  }

  public sealed class ClickGate {
    /// <summary>How long an unreadable screen is waited for before the pilot stops.</summary>
    public const int UnknownGraceMs = ScreenTracker.HoldMs;
    /// <summary>How long a click has to show its effect (a screen that opens may take a moment).</summary>
    public const int EffectMs = 3500, SlowEffectMs = 6000;
    /// <summary>The log (the host's journal); null = none. A line only when an owner's verdict changes.</summary>
    public Action<string> Sink;

    sealed class Rule {
      public ScreenKind[] Kinds;
      public bool InvNormalEquip;   // on the Inventory: the equipment tab in the normal mode, the mode known
      public bool InvBulkEquip;     // on the Inventory: the equipment tab in bulk-sale mode, the mode known
      public bool InvOtherTab;      // on the Inventory: NOT the equipment tab
      public int FoundryTab = -2;   // -2 any, else the tab it must be on
      public int FoundryNotTab = -2;// -2 none, else the tab it must not be on
      public int HeroTab = -1;      // on the hero screen: the tab it must be on (3 = gear), -1 any
      public bool Cell;             // a list cell: the item under the click must be one of the items meant
      public bool AllowModal;       // the pop-up itself is the target (or the equip helper, which works with the game's filter pop-ups)
    }

    static ScreenKind[] K(params ScreenKind[] k) { return k; }

    /// <summary>The whitelist. Null = this owner never clicks that target.</summary>
    static Rule RuleOf(ClickOwner o, ClickTarget t) {
      switch (o) {
        case ClickOwner.Locate:
          switch (t) {
            case ClickTarget.HeroBack: return new Rule { Kinds = K(ScreenKind.HeroScreen) };
            case ClickTarget.CityInventory: return new Rule { Kinds = K(ScreenKind.City) };
            case ClickTarget.InvEquipTab: return new Rule { Kinds = K(ScreenKind.Inventory), InvOtherTab = true };
            case ClickTarget.InvListCell: return new Rule { Kinds = K(ScreenKind.Inventory), InvNormalEquip = true, Cell = true };
            case ClickTarget.InvListDrag: return new Rule { Kinds = K(ScreenKind.Inventory), InvNormalEquip = true };
            case ClickTarget.FoundryButton: return new Rule { Kinds = K(ScreenKind.Inventory, ScreenKind.HeroScreen), InvNormalEquip = true, HeroTab = 3 };
            case ClickTarget.AscendTab: return new Rule { Kinds = K(ScreenKind.Foundry), FoundryNotTab = 2 };
            case ClickTarget.SlotTab: return new Rule { Kinds = K(ScreenKind.Foundry), FoundryTab = 2 };
            case ClickTarget.AscendCell: return new Rule { Kinds = K(ScreenKind.Foundry), FoundryTab = 2, Cell = true };
            case ClickTarget.AscendDrag: return new Rule { Kinds = K(ScreenKind.Foundry), FoundryTab = 2 };
          }
          return null;
        case ClickOwner.Sell:
          switch (t) {
            case ClickTarget.HeroBack: return new Rule { Kinds = K(ScreenKind.HeroScreen) };
            case ClickTarget.CityInventory: return new Rule { Kinds = K(ScreenKind.City) };
            case ClickTarget.InvEquipTab: return new Rule { Kinds = K(ScreenKind.Inventory), InvOtherTab = true };
            case ClickTarget.InvBulkEnter: return new Rule { Kinds = K(ScreenKind.Inventory), InvNormalEquip = true };
            case ClickTarget.InvBulkCell: return new Rule { Kinds = K(ScreenKind.Inventory), InvBulkEquip = true, Cell = true };
            case ClickTarget.InvListDrag: return new Rule { Kinds = K(ScreenKind.Inventory), InvBulkEquip = true };
          }
          return null;
        case ClickOwner.Equip:
          switch (t) {
            case ClickTarget.EquipNav: return new Rule { Kinds = K(ScreenKind.City, ScreenKind.HeroScreen), AllowModal = true };
            case ClickTarget.EquipFilter: case ClickTarget.EquipClosePopup: case ClickTarget.EquipConfirm:
              return new Rule { Kinds = K(ScreenKind.HeroScreen), AllowModal = true };
          }
          return null;
      }
      return null;
    }

    /// <summary>The whitelist as text, for the log and the report: target names per owner and screen.</summary>
    public static string WhitelistText(ClickOwner o) {
      var parts = new List<string>();
      foreach (ClickTarget t in Enum.GetValues(typeof(ClickTarget))) {
        var r = RuleOf(o, t);
        if (r == null) continue;
        var ks = new List<string>(); foreach (var k in r.Kinds) ks.Add(k.ToString());
        string cond = r.InvNormalEquip ? " normal-equip" : r.InvBulkEquip ? " bulk-sale" : r.InvOtherTab ? " other-tab" : "";
        parts.Add(t + "@" + string.Join("/", ks.ToArray()) + cond);
      }
      return string.Join(", ", parts.ToArray());
    }

    sealed class Watch { public Func<ScreenState, bool> Done; public long Deadline; public string What; }

    readonly Dictionary<ClickOwner, string> halted = new Dictionary<ClickOwner, string>();
    readonly Dictionary<ClickOwner, Watch> watches = new Dictionary<ClickOwner, Watch>();
    readonly Dictionary<ClickOwner, long> unknownSince = new Dictionary<ClickOwner, long>();
    readonly Dictionary<ClickOwner, string> noted = new Dictionary<ClickOwner, string>();
    readonly HashSet<ClickOwner> clicked = new HashSet<ClickOwner>();   // the owner has sent a click in this request

    void Note(ClickOwner o, string key, string text) {
      string was; if (noted.TryGetValue(o, out was) && was == key) return;
      noted[o] = key;
      var s = Sink; if (s != null) s("click gate " + o + ": " + text);
    }

    /// <summary>Why the owner's pilot stopped ("" = it runs).</summary>
    public string HaltReason(ClickOwner o) { string r; return halted.TryGetValue(o, out r) ? r : ""; }
    public bool Halted(ClickOwner o) { return halted.ContainsKey(o); }
    /// <summary>A click is still waiting to show its effect.</summary>
    public bool Pending(ClickOwner o) { return watches.ContainsKey(o); }

    /// <summary>A new request (or the player started it again): the owner may click again.</summary>
    public void Resume(ClickOwner o) {
      halted.Remove(o); watches.Remove(o); unknownSince.Remove(o); noted.Remove(o); clicked.Remove(o);
    }

    /// <summary>The pilot stops (also used by the host for its own reasons, e.g. the player's cancel).</summary>
    public GateResult Halt(ClickOwner o, string reason, string detail) {
      if (!halted.ContainsKey(o)) { halted[o] = reason; watches.Remove(o); Note(o, "halt:" + reason, "STOP (" + reason + ") " + detail + " - hints only from now on"); }
      return GateResult.Deny(reason, detail);
    }

    /// <summary>(e) A click was sent: what has to happen next (null = nothing is checked, e.g. a drag).</summary>
    public void Arm(ClickOwner o, ClickTarget t, Func<ScreenState, bool> done, long now) {
      clicked.Add(o);
      if (done == null) { watches.Remove(o); return; }
      watches[o] = new Watch { Done = done, Deadline = now + (t == ClickTarget.CityInventory || t == ClickTarget.FoundryButton ? SlowEffectMs : EffectMs), What = t.ToString() };
    }

    /// <summary>What a click is expected to change, judged on the state before it (null = nothing to check).</summary>
    public static Func<ScreenState, bool> Expect(ClickRequest r, ScreenState before) {
      long uid = r.CellUid; string selKey = before != null ? before.InvSelKey : ""; string fkey = before != null ? before.FoundryKey : "";
      switch (r.Target) {
        case ClickTarget.HeroBack: return s => s.Kind != ScreenKind.HeroScreen;
        case ClickTarget.CityInventory: return s => s.Kind == ScreenKind.Inventory;
        case ClickTarget.InvEquipTab: return s => s.Kind == ScreenKind.Inventory && s.InvEquipTab;
        case ClickTarget.InvBulkEnter: return s => s.Kind == ScreenKind.Inventory && s.InvBulk;
        case ClickTarget.InvListCell: return s => s.Kind == ScreenKind.Inventory && s.InvSelected == uid;
        case ClickTarget.InvBulkCell: return s => s.Kind == ScreenKind.Inventory && s.InvSelKey != selKey;
        case ClickTarget.FoundryButton: return s => s.Kind == ScreenKind.Foundry;
        case ClickTarget.AscendTab: return s => s.Kind == ScreenKind.Foundry && s.FoundryTab == 2;
        case ClickTarget.SlotTab: return s => s.Kind == ScreenKind.Foundry && s.FoundryKey != fkey;
        case ClickTarget.AscendCell: return s => s.Kind == ScreenKind.Foundry && s.FoundrySel == uid;
      }
      return null;
    }

    /// <summary>(e) Looks at the screen now: a click that showed its effect is done; one that did not within its time stops the owner.
    /// Called every tick for the owners with a click on the way.</summary>
    public void Observe(ScreenState s, long now) {
      if (s == null || watches.Count == 0) return;
      foreach (var o in new List<ClickOwner>(watches.Keys)) {
        var w = watches[o];
        if (w.Done(s)) { watches.Remove(o); Note(o, "effect:" + w.What + ":" + now, w.What + " took effect"); }
        else if (now > w.Deadline) Halt(o, "nochange", w.What + " changed nothing within the time (screen " + s.Brief + ")");
      }
    }

    /// <summary>Before the pilot decides anything this tick: may it go on? Deny = it is stopped; Wait = not now (a click is still on its way,
    /// or the screen is not recognised yet - before the first click that only means the player is somewhere else, and it is waited for;
    /// after a click an unrecognised screen stops the pilot: the tracker has held the last screen for its 1.5 s already).</summary>
    public GateResult Preflight(ClickOwner o, ScreenState s, long now) {
      string h;
      if (halted.TryGetValue(o, out h)) return GateResult.Deny("halted", h);
      Observe(s, now);
      if (halted.TryGetValue(o, out h)) return GateResult.Deny("halted", h);
      if (watches.ContainsKey(o)) return GateResult.Wait("pending", "the previous click has not shown its effect yet");
      bool modeUnknown = s != null && s.Kind == ScreenKind.Inventory && s.InvEquipTab && !s.InvBulkKnown;
      if (s == null || s.Kind == ScreenKind.Unknown || modeUnknown) {
        string d = modeUnknown ? "the inventory's mode is not readable" : "the screen is not recognised";
        if (clicked.Contains(o)) return Halt(o, "unknown", d + " after a click");
        return GateResult.Wait("unknown", d);
      }
      unknownSince.Remove(o);
      return GateResult.Allow(s.Brief);
    }

    /// <summary>The verdict for a click on a FRESH state <paramref name="f"/> (never a held one).</summary>
    public GateResult Check(ClickRequest r, ScreenState f, long now) {
      string h;
      if (halted.TryGetValue(r.Owner, out h)) return GateResult.Deny("halted", h);
      if (!r.AutoOn) return GateResult.Deny("off", "auto click is off");
      if (!r.Front) return GateResult.Wait("background", "the game is not in front");
      Observe(f, now);
      if (halted.TryGetValue(r.Owner, out h)) return GateResult.Deny("halted", h);
      if (watches.ContainsKey(r.Owner)) return GateResult.Wait("pending", "the previous click has not shown its effect yet");

      string tag = r.Target + " @" + (int)r.X + "," + (int)r.Y;
      var rule = RuleOf(r.Owner, r.Target);
      if (rule == null) return Halt(r.Owner, "target", tag + " is not a target " + r.Owner + " may click");
      if (f == null || f.Kind == ScreenKind.Unknown) return UnknownWait(r, now, tag + ": the screen is not recognised");
      if (Array.IndexOf(rule.Kinds, f.Kind) < 0) return Halt(r.Owner, "screen", tag + " needs " + string.Join("/", Names(rule.Kinds)) + ", the game shows " + f.Brief);
      if (f.ModalOpen && !rule.AllowModal) return Halt(r.Owner, "modal", tag + ": " + f.ModalWhy + " is open (" + f.Brief + ")");

      if (f.Kind == ScreenKind.Inventory) {
        bool needMode = rule.InvNormalEquip || rule.InvBulkEquip;
        if (needMode && !f.InvBulkKnown) return UnknownWait(r, now, tag + ": the inventory's mode is not readable");
        if (rule.InvNormalEquip) {
          if (f.InvBulk) return Halt(r.Owner, "bulk", tag + ": the inventory is in bulk-sale mode");
          if (!f.InvEquipTab) return Halt(r.Owner, "screen", tag + ": not on the equipment tab");
        }
        if (rule.InvBulkEquip) {
          if (!f.InvEquipTab || !f.InvBulk) return Halt(r.Owner, "screen", tag + ": not in bulk-sale mode on the equipment tab");
        }
        if (rule.InvOtherTab && f.InvEquipTab) return Halt(r.Owner, "screen", tag + ": already on the equipment tab");
      }
      if (f.Kind == ScreenKind.HeroScreen && rule.HeroTab >= 0 && f.HeroTab != rule.HeroTab) return Halt(r.Owner, "screen", tag + ": the hero screen is on tab " + f.HeroTab);
      if (f.Kind == ScreenKind.Foundry) {
        if (rule.FoundryTab != -2 && f.FoundryTab != rule.FoundryTab) return Halt(r.Owner, "screen", tag + ": the Foundry is on " + f.FoundryTabName);
        if (rule.FoundryNotTab != -2 && f.FoundryTab == rule.FoundryNotTab) return Halt(r.Owner, "screen", tag + ": the Foundry is already on " + f.FoundryTabName);
      }
      if (rule.Cell) {
        if (r.CellUid <= 0) return Halt(r.Owner, "cell", tag + ": the item under the click could not be worked out (the list's exact scroll is needed)");
        if (r.CellOk == null || !r.CellOk.Contains(r.CellUid)) return Halt(r.Owner, "cell", tag + ": the cell under the click holds item " + r.CellUid + ", not one of the items meant");
      }
      unknownSince.Remove(r.Owner);
      var res = GateResult.Allow(tag + " on " + f.Brief + (r.CellUid > 0 ? " cell " + r.CellUid : ""));
      Note(r.Owner, "allow:" + r.Target + ":" + f.Brief, "allow " + res.Detail);
      return res;
    }

    GateResult UnknownWait(ClickRequest r, long now, string detail) {
      long since;
      if (!unknownSince.TryGetValue(r.Owner, out since)) { since = now; unknownSince[r.Owner] = now; }
      if (now - since > UnknownGraceMs) return Halt(r.Owner, "unknown", detail + " for " + (now - since) + " ms");
      return GateResult.Wait("unknown", detail);
    }

    static string[] Names(ScreenKind[] ks) { var a = new string[ks.Length]; for (int i = 0; i < ks.Length; i++) a[i] = ks[i].ToString(); return a; }

    // ---- (d) which item is under a click point

    /// <summary>The item in the inventory list's cell at client point (x, y): from the list order (rows of <paramref name="cols"/>) and row 1's
    /// exact place (<paramref name="row1"/>, client y of its top; NaN = unknown). 0 = no cell there, or it cannot be worked out.</summary>
    public static long InvCellUid(InventoryScreen inv, SellGeometry g, double row1, int cols, double x, double y, double W, double U) {
      if (inv == null || g == null || double.IsNaN(row1) || cols < 1) return 0;
      double pitchX = g.PitchX * U, pitchY = g.PitchY * U;
      if (pitchX <= 0 || pitchY <= 0) return 0;
      double dx = x - g.X(g.X0, W, U), dy = y - row1;
      if (dx < 0 || dy < 0) return 0;
      int col = (int)Math.Floor(dx / pitchX), row = (int)Math.Floor(dy / pitchY);
      if (col >= cols || dx - col * pitchX > g.CellW * U || dy - row * pitchY > g.CellHeight * U) return 0;
      int idx = row * cols + col;
      return idx >= 0 && idx < inv.Uids.Count ? inv.Uids[idx] : 0;
    }

    /// <summary>The item in the Foundry's Ascension list cell at client point (x, y): the list's order and the game's own scroll.</summary>
    public static long AscendCellUid(GameGeometry geo, RecastScreen rs, double x, double y, double W, double H, double U) {
      if (geo == null || rs == null || rs.AscendList == null || !rs.AscendList.Has) return 0;
      var l = rs.AscendList;
      var view = geo.Rect("ascend.list", W, H, U); var cell = geo.Rect("ascend.cell", W, H, U); var grid = geo.Grid("ascend.list");
      if (view == null || cell == null || grid == null || view.W <= 0) return 0;
      int cols = l.Cols >= 1 ? l.Cols : grid.Cols;
      double line = l.RowUnits > 0 ? l.RowUnits : grid.LineSize, k = U / GameGeometry.CanvasH, pitch = line * k;
      if (cols < 1 || pitch <= 0) return 0;
      double top0 = view.Y + FoundryPlace.PadSign * grid.PadTop * k - l.ScrollY * k;
      double dy = y - top0, dx = x - view.X;
      if (dy < 0 || dx < 0) return 0;
      int row = (int)Math.Floor(dy / pitch);
      double slotW = view.W / cols;
      int col = (int)Math.Floor(dx / slotW);
      if (col >= cols) return 0;
      double cx = view.X + col * slotW + (slotW - cell.W) / 2, cy = top0 + row * pitch + (pitch - cell.H) / 2;
      if (x < cx || x > cx + cell.W || y < cy || y > cy + cell.H) return 0;
      int idx = row * cols + col;
      return idx >= 0 && idx < rs.AscendEquips.Length ? rs.AscendEquips[idx] : 0;
    }
  }
}
