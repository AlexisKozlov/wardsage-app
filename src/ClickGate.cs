// Wardsage - the ONE gate every auto click goes through (the equip helper, the storage cleanup, «Показать в игре»).
//
// The app never writes into the game and never injects; its only action is a mouse click / drag at a point of the game's window,
// and only when the player turned auto click on. A click is sent only when ALL of this holds:
//   (a) auto click is on (the player's setting and the site's switches) and the game is in front,
//   (b) the target is in the whitelist of the screen the game shows NOW (Rule below),
//   (c) the screen was read fresh right before the click and still is the expected one, with no pop-up / pending choice open,
//   (d) a list cell is the cell of the item meant (its place from the game's list order and its exact scroll),
//   (e) the previous click had its expected effect (a screen, tab or selection change) within the timeout.
// Anything else is no click. A failure of a step the game's screen explains (nothing changed, an unknown screen, a pop-up or another
// screen in the way, a cell not where it was: RetryReasons) is a RETRY, not a stop: nothing is clicked for RetrySettleMs, the run's
// back / Escape / pop-up counts start over (the pilot reads the screen again, closes the pop-ups, goes to the city first if it has to) and
// the step is made again. The pilot stops for good (until a new request, says why: «Автонажатие остановлено: <причина>», hints only)
// on the MaxStepFailures-th failure of the same step in a row, after MaxRetriesRun retries a run, on a dangerous form (buy / quit), the
// player's own input or cancel, and on every other reason (a click that is not allowed, a hint run, the window off the screen ...).
// A pop-up that came while a click's effect was awaited is a retry that does not count against the step. After a pop-up was closed
// nothing else is clicked for OfferChainMs (the game opens the next pop-up of its login chain then).
//
// What is NEVER a target: «Вознести», «Преобразовать», «Улучшить», «Продать», «Продвинуть», the item card's button and every confirmation
// dialog of the game (except «Заменить» of the equip helper - the gear card's and, for a plan with an artifact, the artifact card's and its "worn by another hero" pop-up's -
// and only with the player's own auto confirm setting).
//
// The two artifact flows (the storage cleanup of the «Артефакт» tab, «Показать в игре» for a promotion) click only with the site's "artAuto"
// switch AND the player's auto click (ArtSellAuto / ArtLocateAuto below); with it off they are hints only, as before. Their targets are the Art*
// ones: the «Артефакт» tab, «Массовая продажа», the cells to tick / the artifact's cell / the copy's cell, «Мастерская», the «Продвинуть» tab.
// Never «Продать», «Все» (select all), the lock icons, «Продвинуть» / «Улучшить» (NeverKeys).
//
// The game's offer / notice pop-ups over the city (src/OfferForms.cs: the game's own pop-up queue) are closed by every pilot before its
// step (OfferClose): only when the one pop-up shown is on that list, the city (or nothing known) is under it, the click point is on its ×
// (the prefab's close button) and on none of its other buttons («Купить», «Перейти», «Получить» ... are never targets), at most
// MaxOfferCloses in a row; the click's effect is that pop-up gone. A pop-up with no known × is a stop with its name.
//
// Pure decision logic, no Windows APIs (tests: tests/ScreenReplay.cs); the host (app/Overlay.cs) reads the screen and sends.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum ClickOwner { Locate, Sell, Equip }

  /// <summary>How a pilot leaves the form on top, by the screen map (ClickGate.MapExit).</summary>
  public enum MapMove { None, Wait, Stop, Back, Close, Escape }
  public sealed class MapDecision {
    public MapMove Move; public string Form = ""; public char Class = 'U'; public GeoRect Point; public string Why = "";
    public override string ToString() { return Move + (Form != "" ? " " + Form + ":" + ScreenMap.ClassName(Class) : "") + (Why != "" ? " (" + Why + ")" : ""); }
  }

  public enum ClickTarget {
    None,
    HeroBack,         // the hero screen's back arrow
    CityInventory,    // the city's «Инвентарь»
    InvEquipTab,      // the inventory's left menu «Снаряжение»
    InvBulkEnter,     // «Массовая продажа» (the storage cleanup only)
    InvListCell,      // an item's cell in the inventory list, normal mode (selects it; «Показать в игре»)
    InvBulkCell,      // an item's cell in the inventory list, bulk-sale mode (toggles its choice; the storage cleanup only)
    InvListDrag,      // a drag that scrolls the inventory list
    FoundryButton,    // «Литейная» of the inventory's equipment tab (never on the hero screen)
    AscendTab,        // the Foundry's «Вознесение» tab
    SlotTab,          // the Ascension list's slot tab
    AscendCell,       // an item's cell in the Ascension list (selects it)
    AscendDrag,       // a drag that scrolls the Ascension list
    InvCardGem,       // the selected item's card, its «Улучшение самоцветами» button (normal mode; opens the gem screen; «Показать в игре» recast)
    GemFuncRow,       // the gem screen's function list: the row of the wanted recast function (selects it only; «Показать в игре» recast)
    EquipNav,         // equip helper: the city's «Герои», a hero's card, the gear tab, the back arrow, a slot, a gear list item, a list drag
    EquipFilter,      // equip helper: the controls of the game's gear / hero filter pop-up
    EquipClosePopup,  // equip helper: the red × of a pop-up left over the list
    EquipTipEscape,   // equip helper: ONE Escape key press that closes the item's tip / compare (its own flow form) when the next step is a slot it covers (live 1.6.66.354)
    EquipConfirm,     // equip helper: «Заменить» (only with the player's auto confirm)
    OfferClose,       // every owner: the × of the game's offer / notice pop-up on top of the city (src/OfferForms.cs; never its buy / go / claim buttons)
    UnknownBack,      // every owner: the game's back arrow top left (HeroBack's spot) on a full-screen screen nobody knows, no pop-up on it (at most MaxUnknownBacks a run)
    CityBack,         // every owner: the same back arrow on a KNOWN screen that is not the pilot's way (the Foundry, the inventory, the gem screen ...), on the way to the city (at most MaxCityBacks a run)
    EscapeBack,       // every owner: ONE Escape key press after a back press that changed nothing (never on the city, in a fight or with the exit dialog; at most MaxEscapes a run); or on a form the screen map closes with Escape
    MapClose,         // every owner: the × of the pop-up on top as the screen map gives it (src/ScreenMap.cs, class P; never its other buttons; at most MaxMapMoves a run)
    // the artifact flows (only with the site's "artAuto" switch; never «Продать», «Все», a lock, «Продвинуть», «Улучшить»)
    ArtTab,           // the inventory's left menu «Артефакт»
    ArtBulkEnter,     // the artifact tab's «Массовая продажа» (the storage cleanup)
    ArtBulkCell,      // an artifact's cell in the artifact tab's bulk-sale mode (ticks it, or takes a wrong tick off)
    ArtListDrag,      // a drag that scrolls the artifact list
    ArtCell,          // an artifact's cell in the artifact tab's normal mode (opens its card; «Показать в игре» promotion)
    ArtWorkshop,      // the artifact card's «Мастерская» (opens the workshop form)
    ArtPromoteTab,    // the workshop's right tab «Продвинуть»
    ArtMatCell,       // the workshop's «Продвинуть» panel: the planned copy / essence in the left list (chooses it)
    ArtMatDrag,       // a drag that scrolls that list
    // «Надеть в игре» with an artifact (the Equip owner, only with the site's "artAuto" switch AND the player's auto click; src/ArtEquip.cs). Never the sell icon of the card, «Быстро надеть», «Снять», «Улучшить».
    ArtHeroTab,       // the hero screen's right tab «Артефакт»
    ArtOpenList,      // the worn artifact's list button / the empty slot's «+» (opens the artifact list)
    ArtHeroCell,      // the plan's artifact in the hero screen's artifact list (selects it, opens its card)
    ArtHeroDrag,      // a drag that scrolls that list
    ArtReplace,       // the selected artifact card's «Заменить» / «Надеть» (the player's auto confirm too)
    ArtTakeOverConfirm, // the pop-up «<artifact> в ячейке героя: <имя>» (the artifact is worn by another hero): its «Заменить» (the player's auto confirm too)
    ArtHideWorn       // the artifact list's own filter «Скрыть надетое» (on before a long scroll to a free artifact; off when it hides the artifact). Untick = off
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
    /// <summary>InvListDrag / a list cell click: the list's exact scroll cannot be read (the layout data of this game version has no list fields), so no cell could be told later: the run stops before it scrolls.</summary>
    public bool NoScroll;
    /// <summary>The click point (client px), for the log.</summary>
    public double X, Y;
    /// <summary>InvCardGem / GemFuncRow: the item the screen has to hold selected is one of CellOk. GemFuncRow: the function the click selects (7 / 8).</summary>
    public int Func;
    /// <summary>ArtBulkCell: the click takes a tick off (the cell is a ticked one that is not wanted); else it ticks.</summary>
    public bool Untick;
    /// <summary>ArtMatCell: the 1-based entry of the workshop's m_vMatData under the click (0 = none), and the essence item ids the pilot may choose (CellOk holds the copies' uids).</summary>
    public int MatIndex;
    public ICollection<int> ItemOk;
    /// <summary>The hero artifact targets: the plan's hero and the artifact (iItemUid) the steps are about.</summary>
    public long Hero, ArtUid;
    /// <summary>FoundryButton: the Foundry's form cannot be read now although the game built it before (or the pilot opened it): the Foundry may be open
    /// over the inventory unseen, and the inventory's «Литейная» lies inside its «Вознести» - the click waits for a read of the Foundry (set by the host).</summary>
    public bool FoundryUnread;
    /// <summary>OfferClose: the pop-up the click closes, the game window's client size and unit, and the site's geometry (null = the prefab numbers).</summary>
    public string Form;
    public double W, H, U;
    public GameGeometry Geo;
    /// <summary>The point is the centre of the button's live rect (read by name on the form on top, fresh: src/UiButtons.cs), not the geometry's.</summary>
    public bool Live;
    /// <summary>Every form nobody knows the window table shows now, the ones taken for a part of the screen too (set by the host; null = not
    /// given). The table has no order: any of them may lie on top, so no click lands on a never-click button of one (needs W, H, U).</summary>
    public List<string> Shown;
  }

  /// <summary>Every pilot's flow (owner rule 2026-10-30, city first). The equip helper with the filter's «hide worn» is Equip; the guides
  /// without auto click (EquipGuide, the hint runs) click nothing and have no flow here.</summary>
  public enum PilotFlow { Equip, ArtEquip, Sell, ArtSell, LocateAscend, LocateRecast, LocateArtPromote }

  /// <summary>City first, for EVERY pilot: on a known screen from which the pilot cannot go on with its own flow, it first goes back to the city
  /// (the hosts' shared BackStep: MapExit / CityBack / one EscapeBack, the gate's limits; never an Escape on the city or in a fight), then takes
  /// its normal path. The screens a flow goes on from (its continuation set) are declared here; a flow's sub-state on one of them (a Locate
  /// step that is not that screen's) comes in as <c>subAway</c> (LocateAuto.NeedsCityBack / ArtLocateAuto.NeedsCityBack). An unknown screen
  /// is the unknown-screen back's (UnknownBackStep), the city is every flow's start.</summary>
  public static class CityFirst {
    public static readonly PilotFlow[] All = { PilotFlow.Equip, PilotFlow.ArtEquip, PilotFlow.Sell, PilotFlow.ArtSell, PilotFlow.LocateAscend, PilotFlow.LocateRecast, PilotFlow.LocateArtPromote };
    static readonly ScreenKind[] EquipSet = { ScreenKind.City, ScreenKind.HeroScreen };   // the hero pilot walks the hero screen to the plan's hero and tab
    static readonly ScreenKind[] SellSet = { ScreenKind.City, ScreenKind.HeroScreen, ScreenKind.Inventory };   // the inventory on any tab / mode: the pilot switches
    static readonly ScreenKind[] AscendSet = { ScreenKind.City, ScreenKind.HeroScreen, ScreenKind.Inventory, ScreenKind.Foundry };   // any Foundry tab: «Вознесение»
    static readonly ScreenKind[] RecastSet = { ScreenKind.City, ScreenKind.HeroScreen, ScreenKind.Inventory, ScreenKind.Gem };   // the recast path is the inventory's card -> the gem screen
    static readonly ScreenKind[] ArtPromoteSet = { ScreenKind.City, ScreenKind.HeroScreen, ScreenKind.Inventory, ScreenKind.ArtWorkshop };

    /// <summary>The screens the flow goes on from.</summary>
    public static ScreenKind[] Continuations(PilotFlow f) {
      switch (f) {
        case PilotFlow.Equip: case PilotFlow.ArtEquip: return EquipSet;
        case PilotFlow.Sell: case PilotFlow.ArtSell: return SellSet;
        case PilotFlow.LocateAscend: return AscendSet;
        case PilotFlow.LocateRecast: return RecastSet;
        default: return ArtPromoteSet;
      }
    }

    /// <summary>The screen the flow's last step is on (its work screen): never an Escape there during its run (ClickGate CheckEscape, with the
    /// owners' ScreenMap.PilotScreen).</summary>
    public static ScreenKind WorkScreen(PilotFlow f) {
      switch (f) {
        case PilotFlow.Equip: case PilotFlow.ArtEquip: return ScreenKind.HeroScreen;
        case PilotFlow.Sell: case PilotFlow.ArtSell: return ScreenKind.Inventory;
        case PilotFlow.LocateAscend: return ScreenKind.Foundry;
        case PilotFlow.LocateRecast: return ScreenKind.Gem;
        default: return ScreenKind.ArtWorkshop;
      }
    }

    /// <summary>The Locate flow of a request's purpose (ascend | artPromote | anything else: the recast).</summary>
    public static PilotFlow OfLocate(string purpose) {
      return purpose == "ascend" ? PilotFlow.LocateAscend : purpose == "artPromote" ? PilotFlow.LocateArtPromote : PilotFlow.LocateRecast;
    }

    /// <summary>The flow goes on from this screen (null / unknown: not this check's, true). A recast's finished states on the Foundry's own
    /// recast tab (the item selected there, a result's choice waiting: the player's) are a continuation too.</summary>
    public static bool Continues(PilotFlow f, ScreenState s, LocateKind step = LocateKind.None) {
      if (s == null || s.Kind == ScreenKind.Unknown) return true;
      if (Array.IndexOf(Continuations(f), s.Kind) >= 0) return true;
      return f == PilotFlow.LocateRecast && s.Kind == ScreenKind.Foundry && (step == LocateKind.Done || step == LocateKind.ChoosePending);
    }

    /// <summary>Back to the city first: a known screen other than the city that the flow does not go on from, or one it does but its sub-state
    /// there is not the flow's (<paramref name="subAway"/>). A Locate request with nothing to drive (no item, another account) never goes back.</summary>
    public static bool NeedsCity(PilotFlow f, ScreenState s, LocateKind step = LocateKind.None, bool subAway = false) {
      if (s == null || s.Kind == ScreenKind.Unknown || s.Kind == ScreenKind.City) return false;
      bool locate = f == PilotFlow.LocateAscend || f == PilotFlow.LocateRecast || f == PilotFlow.LocateArtPromote;
      if (locate && (step == LocateKind.NoItem || step == LocateKind.OtherAccount)) return false;
      return !Continues(f, s, step) || subAway;
    }
  }

  /// <summary>The city's bottom bar as the pilots click it: the place of «Инвентарь» (U from the right; an event button moves it), the city
  /// form it was found on and whether the bar is settled. When the game builds the city again (another table for its form in the window
  /// table: after the guild, an event screen) the place is dropped and searched again on the new city, and «Инвентарь» counts as seen only
  /// once the picture stayed at one place for SettleMs (the new bar comes in with its animation). Pure (tests/CoreTests.cs).</summary>
  public sealed class CityBar {
    public const double DefaultDX = 0.5222 + 0.9316;   // «Инвентарь»: 0.9316 left of «Герои» in the city's bottom bar
    public const int SettleMs = 600;
    public double InvDX = DefaultDX;
    public long ScanAt;           // the last search along the bar
    public bool MustSearch;       // the next look searches the whole bar (a new city / the last press changed nothing)
    public ulong Instance;        // the city form the place belongs to (0 = not known yet)
    long seenFrom = -1;           // since when the picture is seen at InvDX after a drop (-1 = no drop pending: seen = ready)
    double seenDX = double.NaN;
    /// <summary>The city form the game holds now (0 = unknown: nothing changes). True when it is another one than the place was found on:
    /// the place is dropped.</summary>
    public bool Note(ulong inst) {
      if (inst == 0 || inst == Instance) return false;
      bool had = Instance != 0; Instance = inst;
      if (had) Drop();
      return had;
    }
    /// <summary>Forget the place: the next look searches the bar, the city clicks wait for a settled picture.</summary>
    public void Drop() { InvDX = DefaultDX; ScanAt = 0; MustSearch = true; seenFrom = 0; seenDX = double.NaN; }
    /// <summary>What the picture look gave (<paramref name="seen"/> at InvDX): may a city click go to the bar now?</summary>
    public bool Look(bool seen, long now) {
      if (seenFrom < 0) return seen;
      if (!seen || MustSearch) { seenFrom = 0; seenDX = double.NaN; return false; }
      if (seenFrom == 0 || Math.Abs(seenDX - InvDX) > 0.05) { seenFrom = now; seenDX = InvDX; return false; }
      if (now - seenFrom < SettleMs) return false;
      seenFrom = -1; return true;
    }
    /// <summary>A drop waits for the settled picture.</summary>
    public bool Settling { get { return seenFrom >= 0; } }
  }

  /// <summary>A pilot button's live rect (uibtn) held still: two reads of the game's own rect at least GapMs apart give the same rect (within
  /// TolPx) - the button has stopped moving (a bar coming in with its animation moves it). The city's «Инвентарь» is pressed on that at once,
  /// without the bar's picture search (live 1.6.66.391: 18 s from the city to the press). Pure (tests/CoreTests.cs).</summary>
  public sealed class LiveStill {
    public const int GapMs = 100; public const double TolPx = 1.0;
    double[] first; long firstAt;
    /// <summary>The last Note had a fresh rect: while it moves the press waits for it (the bar's picture is no fallback then: a press into a
    /// sliding bar is lost); without one the bar's picture decides.</summary>
    public bool Fresh;
    /// <summary>The fresh live rect (x, y, w, h; null = none fresh) and the time of its read: true once it has been the same for GapMs.</summary>
    public bool Note(double[] r, long readAt) {
      Fresh = r != null && r.Length >= 4;
      if (!Fresh) { first = null; return false; }
      if (first == null || !Same(first, r)) { first = (double[])r.Clone(); firstAt = readAt; return false; }
      return readAt - firstAt >= GapMs;
    }
    public void Reset() { first = null; Fresh = false; }
    static bool Same(double[] a, double[] b) { for (int i = 0; i < 4; i++) if (Math.Abs(a[i] - b[i]) > TolPx) return false; return true; }
  }

  public sealed class ClickGate {
    /// <summary>How long an unreadable screen is waited for before the pilot stops.</summary>
    public const int UnknownGraceMs = ScreenTracker.HoldMs;
    /// <summary>How long a click has to show its effect (a screen that opens may take a moment).</summary>
    public const int EffectMs = 3500, SlowEffectMs = 6000, GemEffectMs = 2000;
    /// <summary>A slow PC (one form search takes ~17 s): a CityInventory / FoundryButton click whose form was not found yet shows its effect only
    /// after the search ends. While FormSearchPending says such a search runs, the deadline moves on (SearchGraceMs past the search's end, so its
    /// result is read once), up to SearchCapMs after the click. Only the search running at the deadline counts: a later one does not extend again.</summary>
    public const int SearchCapMs = 30000, SearchGraceMs = 1500;
    /// <summary>The host: a form search is running and the form this target opens is not found yet (null = never).</summary>
    public Func<ClickTarget, bool> FormSearchPending;
    /// <summary>The host: the form this target opens HAS been found. After a search ran for the click, a found form keeps the effect awaited until the
    /// screen classifier reports the screen (up to SearchCapMs), not just SearchGraceMs past the search's end (null = never found).</summary>
    public Func<ClickTarget, bool> FormFound;
    /// <summary>An artifact tick (or its taking off) has to show in the game's choice within this time.</summary>
    public const int ArtTickMs = 1000;
    /// <summary>An unrecognised screen stops the pilot only this long after the pilot's OWN last click (the screen it caused); later it is waited for.</summary>
    public const int OwnClickWindowMs = 2500;
    /// <summary>At most this many offer pop-ups are closed in a row (no other click of the pilot between): more is a loop, a stop.</summary>
    public const int MaxOfferCloses = 8;
    /// <summary>The retry policy (the header): the same step failing this many times in a row stops the owner; at most MaxRetriesRun retries a run;
    /// nothing is clicked for RetrySettleMs after a retry, nor for OfferChainMs after a pop-up was closed (the next one of the chain comes then).</summary>
    public const int MaxStepFailures = 3, MaxRetriesRun = 9, RetrySettleMs = 1000, OfferChainMs = 1000;
    /// <summary>The stop reasons a retry replaces (the rest are stops at once: dangerous, exitdialog, target, artifact, scroll, bulk, covered, offscreen ...).</summary>
    public static readonly string[] RetryReasons = { "nochange", "unknown", "modal", "screen", "cell", "offer", "offers", "popup" };
    /// <summary>Failures are retried (the policy above); false = every failure stops the owner at once (the old rule, for tests of single checks).</summary>
    public bool Resilient = DefaultResilient;
    /// <summary>The value new gates start with (the unit tests of single checks set false; the app and the sim keep true).</summary>
    public static bool DefaultResilient = true;
    /// <summary>The owner's last retry, for the window: "reason/n" while it is recent (RetryShowMs), else "".</summary>
    public string RetryShown(ClickOwner o, long now) { long at; string w; return !halted.ContainsKey(o) && retryAt.TryGetValue(o, out at) && now - at <= RetryShowMs && retryWhy.TryGetValue(o, out w) ? w : ""; }
    public const int RetryShowMs = 4000;
    /// <summary>Retries made this run (the tests and the sim's report).</summary>
    public int Retries(ClickOwner o) { int n; return retries.TryGetValue(o, out n) ? n : 0; }
    /// <summary>A retry was made, or a pop-up closed (it may have taken the pilot's last press): the host drops what it keeps of the screen (the
    /// city bar's place ...) and the pilot's tries of its step start over before the step again; taken by the host.</summary>
    public readonly HashSet<ClickOwner> RetryDue = new HashSet<ClickOwner>();
    /// <summary>A pilot that sees the same unrecognised screen (no offer pop-up on it, not held, not a fight's result) this long stops with
    /// "unknown screen &lt;form&gt;" instead of waiting for ever (UnknownStall).</summary>
    public const int UnknownStallMs = 20000;
    /// <summary>Calls of UnknownStall further apart than this (the game was in the background, the player was busy) start the count again.</summary>
    public const int StallGapMs = 2000;
    /// <summary>The unknown-screen back press (UnknownBack): nearly every full-screen form of the game has its back arrow at the hero screen's
    /// spot top left. On a screen nobody knows (no pop-up or modal detected, no known screen's form shown, not the city, not a fight) that has
    /// lasted UnknownBackSettleMs, the pilot presses it - at most MaxUnknownBacks times a run, each press has to change the screen (else
    /// "nochange"), and none once the screen is a known one. A screen whose top form is named (the window table read it) needs only
    /// UnknownBackNamedSettleMs (live 1.6.66.391: 3 s more after the name came, for a screen the map already knew to have a back arrow).</summary>
    public const int MaxUnknownBacks = 3, UnknownBackSettleMs = 3000, UnknownBackNamedSettleMs = 500, UnknownBackRetryMs = 1000;
    /// <summary>The stillness an unknown screen needs before its back press: short when its top form is named, else UnknownBackSettleMs.</summary>
    public static int UnknownBackSettle(ScreenState s) {
      if (s == null || string.IsNullOrEmpty(s.TopForm)) return UnknownBackSettleMs;
      foreach (var n in s.TopForm.Split(',')) if (n == "" || n.StartsWith("id:")) return UnknownBackSettleMs;
      return UnknownBackNamedSettleMs;
    }
    /// <summary>How far (in units of the client height) the press may lie from the back arrow's centre.</summary>
    public const double UnknownBackTol = 0.03;
    /// <summary>Back to the city (CityBack): a pilot on a known screen it cannot start from (the host says so: CityBackDue's caller) presses the
    /// back arrow top left - once the screen has lasted CityBackSettleMs, at most MaxCityBacks times a run, each press has to change the screen;
    /// none once the city is on top. A back press (CityBack or UnknownBack) that changes nothing is followed by ONE Escape key press (EscapeBack,
    /// most screens close with it) instead of the stop, at most MaxEscapes a run; never on the city (it asks to quit the game), in a fight, with
    /// a pop-up or the exit dialog (ExitDialogForms) shown. An Escape that changes nothing, or the exit dialog after it, stops the owner.</summary>
    public const int MaxCityBacks = 4, MaxEscapes = 2, CityBackSettleMs = 1000;
    /// <summary>The screen map's exits (MapExit): a form must have been on top MapSettleMs before its × or Escape (its opening animation), at most
    /// MaxMapMoves of them a run (then "nochange": a loop).</summary>
    public const int MapSettleMs = 1000, MaxMapMoves = 6;
    /// <summary>The game's exit question (Form_MainCity's back-press listener: CS.Form_SystemTips «выйти из игры?»): never an Escape while it shows, a stop when it appears.</summary>
    public static readonly string[] ExitDialogForms = { "Form_SystemTips" };
    /// <summary>Shown forms the unknown-screen stop leaves alone: a fight's result screens (the player is in a fight, not lost).</summary>
    public static readonly string[] StallExempt = { "Form_UnionBossBattleEnd", "Form_UnionWeekBossBattleEnd", "Form_BattleEnd", "Form_PvpBattleEnd", "Form_SparringPvpBattleEnd", "Form_ExpeditionBattleEnd" };
    /// <summary>A form the readers knew is being searched for again (the game moved its node, the search takes up to ~20 s on a slow PC): an
    /// unrecognised screen is waited for meanwhile - no stop for it, no click effect runs out, the unknown-screen count does not run. Set by the host.</summary>
    public volatile bool SearchWait;
    /// <summary>The log (the host's journal); null = none. A line only when an owner's verdict changes.</summary>
    public Action<string> Sink;
    /// <summary>The storage cleanup of ARTIFACTS is a hint run: the Sell owner never clicks while it runs (set by the host with the run).</summary>
    public volatile bool NoSellClicks;
    /// <summary>«Показать в игре» for an ARTIFACT (promotion) is hints only: the Locate owner never clicks while it runs (set by the host with the request).</summary>
    public volatile bool NoLocateClicks;
    /// <summary>The artifact cleanup may click (the Art* targets of the Sell owner): the site's "artAuto" switch and the player's auto click are both on. Set by the host.</summary>
    public volatile bool ArtSellAuto;
    /// <summary>The artifact promotion guide may click (the Art* targets of the Locate owner): the same condition. Set by the host.</summary>
    public volatile bool ArtLocateAuto;
    /// <summary>The artifact steps of «Надеть в игре» may click (the ArtHero* / ArtReplace / ArtTakeOverConfirm targets of the Equip owner): the site's "artAuto" switch and the player's auto click are both on. Set by the host.</summary>
    public volatile bool ArtEquipAuto;

    /// <summary>Geometry keys of the buttons the artifact flows never click (the workshop's «Продвинуть» and «Улучшить» buttons); no click point may lie inside them
    /// (ArtAuto.OnNeverButton, checked by the host before every artifact click) and no Art* target is placed by them.</summary>
    public static readonly string[] NeverKeys = { "artifact.promoteBtn", "artifact.upgradeBtn", "artifact.enhanceBtn" };
    /// <summary>The geometry key an Art* target is placed by ("" = by the list, not by a key).</summary>
    public static string KeyOf(ClickTarget t) {
      switch (t) {
        case ClickTarget.ArtTab: return "inventory.leftTab.artifact";
        case ClickTarget.ArtBulkEnter: return "inventory.artBulkSell";
        case ClickTarget.ArtWorkshop: return "inventory.artWorkshop";
        case ClickTarget.ArtPromoteTab: return "artifact.tab.promote";
        default: return "";
      }
    }
    public static bool IsArtTarget(ClickTarget t) { return t >= ClickTarget.ArtTab; }
    /// <summary>The way to the inventory the artifact guide and the artifact cleanup may also click (the hero screen's back arrow, the city's «Инвентарь»): they start from any screen.</summary>
    public static bool ArtLocateWay(ClickTarget t) { return t == ClickTarget.HeroBack || t == ClickTarget.CityInventory; }
    /// <summary>The targets of the hero screen's artifact steps (the Equip owner's).</summary>
    public static bool IsHeroArtTarget(ClickTarget t) { return t >= ClickTarget.ArtHeroTab; }
    /// <summary>The target acts on the artifact workshop, the screen that has the «Продвинуть» / «Улучшить» buttons (NeverKeys): no point of it may lie inside them.</summary>
    public static bool IsWorkshopTarget(ClickTarget t) { return t == ClickTarget.ArtPromoteTab || t == ClickTarget.ArtMatCell || t == ClickTarget.ArtMatDrag; }

    /// <summary>ScreenState.ModalWhy of Form_EquipPopUp (the "worn by another hero" question), seen on the picture.</summary>
    public const string ModalEquipPopup = "equipPopup";

    sealed class Rule {
      public ScreenKind[] Kinds;
      public bool InvNormalEquip;   // on the Inventory: the equipment tab in the normal mode, the mode known
      public bool InvBulkEquip;     // on the Inventory: the equipment tab in bulk-sale mode, the mode known
      public bool InvOtherTab;      // on the Inventory: NOT the equipment tab
      public int FoundryTab = -2;   // -2 any, else the tab it must be on
      public int FoundryNotTab = -2;// -2 none, else the tab it must not be on
      public int HeroTab = -1;      // on the hero screen: the tab it must be on (3 = gear), -1 any
      public bool SelItem;          // the item selected on the screen (inventory card / gem screen) must be one of the items meant
      public bool GemFuncWanted;    // gem screen: the click selects the wanted function, which must not be the selected one yet
      public bool NeedScroll;       // a list drag or a list cell click: the exact scroll must be readable (a drag only makes sense when a cell click can follow)
      public bool Cell;             // a list cell: the item under the click must be one of the items meant
      public bool AllowModal;       // the pop-up itself is the target (or the equip helper, which works with the game's filter pop-ups)
      public int InvArt;            // on the Inventory, the artifact flows: 1 = artifact tab, normal mode  2 = artifact tab, bulk sale  3 = NOT the artifact tab (and no equipment bulk sale)
      public bool SelArt;           // the artifact whose card is open must be one of the items meant
      public int WsTab = -1;        // on the workshop: the tab it must be on (2 = «Продвинуть»), -1 any
      public int WsNotTab = -1;     // ... the tab it must not be on, -1 none
      public bool WsUid;            // the workshop must hold one of the items meant
      public bool MatCell;          // the workshop's list entry under the click must be a planned one and not chosen yet
      public bool ArtTick;          // artifact bulk sale: the cell under the click is a wanted one that is not ticked (tick), or a ticked one that is not wanted (Untick)
      public bool FoundryClosed;    // the fresh read must show the Foundry NOT open (FoundryButton: its point lies inside the Foundry's «Вознести»)
      public int HeroArt;           // hero screen artifact steps: 1 not on the artifact tab  2 tab, list closed  3 tab, list open  4 + the plan's artifact selected (its card)  5 + the "worn by another hero" pop-up
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
            case ClickTarget.InvListCell: return new Rule { Kinds = K(ScreenKind.Inventory), InvNormalEquip = true, Cell = true, NeedScroll = true };
            case ClickTarget.InvListDrag: return new Rule { Kinds = K(ScreenKind.Inventory), InvNormalEquip = true };
            // the inventory's only: the hero screen covers the city and the inventory (live 2026-10-29: its own «Литейная» is gone with a gear list open)
            case ClickTarget.FoundryButton: return new Rule { Kinds = K(ScreenKind.Inventory), InvNormalEquip = true, FoundryClosed = true };
            case ClickTarget.AscendTab: return new Rule { Kinds = K(ScreenKind.Foundry), FoundryNotTab = 2 };
            case ClickTarget.SlotTab: return new Rule { Kinds = K(ScreenKind.Foundry), FoundryTab = 2 };
            case ClickTarget.AscendCell: return new Rule { Kinds = K(ScreenKind.Foundry), FoundryTab = 2, Cell = true };
            case ClickTarget.AscendDrag: return new Rule { Kinds = K(ScreenKind.Foundry), FoundryTab = 2 };
            // the recast path; the gem screen's «Улучшить», the Foundry's «Преобразовать» and «Вознести» are in NO whitelist
            case ClickTarget.InvCardGem: return new Rule { Kinds = K(ScreenKind.Inventory), InvNormalEquip = true, SelItem = true };
            case ClickTarget.GemFuncRow: return new Rule { Kinds = K(ScreenKind.Gem), SelItem = true, GemFuncWanted = true };
            // the artifact promotion; the workshop's «Продвинуть» / «Улучшить» buttons are in NO whitelist
            case ClickTarget.ArtTab: return new Rule { Kinds = K(ScreenKind.Inventory), InvArt = 3 };
            case ClickTarget.ArtCell: return new Rule { Kinds = K(ScreenKind.Inventory), InvArt = 1, Cell = true, NeedScroll = true };
            case ClickTarget.ArtListDrag: return new Rule { Kinds = K(ScreenKind.Inventory), InvArt = 1 };
            case ClickTarget.ArtWorkshop: return new Rule { Kinds = K(ScreenKind.Inventory), InvArt = 1, SelArt = true };
            case ClickTarget.ArtPromoteTab: return new Rule { Kinds = K(ScreenKind.ArtWorkshop), WsNotTab = 2, WsUid = true };
            case ClickTarget.ArtMatCell: return new Rule { Kinds = K(ScreenKind.ArtWorkshop), WsTab = 2, WsUid = true, MatCell = true };
            case ClickTarget.ArtMatDrag: return new Rule { Kinds = K(ScreenKind.ArtWorkshop), WsTab = 2, WsUid = true };
          }
          return null;
        case ClickOwner.Sell:
          switch (t) {
            case ClickTarget.HeroBack: return new Rule { Kinds = K(ScreenKind.HeroScreen) };
            case ClickTarget.CityInventory: return new Rule { Kinds = K(ScreenKind.City) };
            case ClickTarget.InvEquipTab: return new Rule { Kinds = K(ScreenKind.Inventory), InvOtherTab = true };
            case ClickTarget.InvBulkEnter: return new Rule { Kinds = K(ScreenKind.Inventory), InvNormalEquip = true };
            case ClickTarget.InvBulkCell: return new Rule { Kinds = K(ScreenKind.Inventory), InvBulkEquip = true, Cell = true, NeedScroll = true };
            case ClickTarget.InvListDrag: return new Rule { Kinds = K(ScreenKind.Inventory), InvBulkEquip = true, NeedScroll = true };
            // the artifact cleanup; «Продать», «Все» and the lock icons are in NO whitelist
            case ClickTarget.ArtTab: return new Rule { Kinds = K(ScreenKind.Inventory), InvArt = 3 };
            case ClickTarget.ArtBulkEnter: return new Rule { Kinds = K(ScreenKind.Inventory), InvArt = 1 };
            case ClickTarget.ArtBulkCell: return new Rule { Kinds = K(ScreenKind.Inventory), InvArt = 2, ArtTick = true };
            case ClickTarget.ArtListDrag: return new Rule { Kinds = K(ScreenKind.Inventory), InvArt = 2 };
          }
          return null;
        case ClickOwner.Equip:
          switch (t) {
            case ClickTarget.EquipNav: return new Rule { Kinds = K(ScreenKind.City, ScreenKind.HeroScreen), AllowModal = true };
            case ClickTarget.EquipFilter: case ClickTarget.EquipClosePopup: case ClickTarget.EquipConfirm:
              return new Rule { Kinds = K(ScreenKind.HeroScreen), AllowModal = true };
            // the artifact steps after the gear; `Cell` = the artifact under the click is the plan's
            case ClickTarget.ArtHeroTab: return new Rule { Kinds = K(ScreenKind.HeroScreen), HeroArt = 1 };
            case ClickTarget.ArtOpenList: return new Rule { Kinds = K(ScreenKind.HeroScreen), HeroArt = 2 };
            case ClickTarget.ArtHeroCell: return new Rule { Kinds = K(ScreenKind.HeroScreen), HeroArt = 3, Cell = true };
            case ClickTarget.ArtHeroDrag: return new Rule { Kinds = K(ScreenKind.HeroScreen), HeroArt = 3 };
            case ClickTarget.ArtReplace: return new Rule { Kinds = K(ScreenKind.HeroScreen), HeroArt = 4 };
            case ClickTarget.ArtTakeOverConfirm: return new Rule { Kinds = K(ScreenKind.HeroScreen), HeroArt = 5, AllowModal = true };
            case ClickTarget.ArtHideWorn: return new Rule { Kinds = K(ScreenKind.HeroScreen), HeroArt = 3 };
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
        string cond = r.InvNormalEquip ? " normal-equip" : r.InvBulkEquip ? " bulk-sale" : r.InvOtherTab ? " other-tab" : r.InvArt == 1 ? " art-normal" : r.InvArt == 2 ? " art-bulk-sale" : r.InvArt == 3 ? " other-tab" : r.HeroArt != 0 ? " hero-art" + r.HeroArt : "";
        parts.Add(t + "@" + string.Join("/", ks.ToArray()) + cond);
      }
      return string.Join(", ", parts.ToArray());
    }

    sealed class Watch { public Func<ScreenState, bool> Done; public long Deadline; public string What; public string Offer; public ClickTarget Target; public long ArmedAt; public int Search; public bool ModalBefore; }   // Search: 0 none, 1 extended while a search runs, 2 its end awaited

    readonly Dictionary<ClickOwner, string> halted = new Dictionary<ClickOwner, string>();
    readonly Dictionary<ClickOwner, Watch> watches = new Dictionary<ClickOwner, Watch>();
    readonly Dictionary<ClickOwner, long> unknownSince = new Dictionary<ClickOwner, long>();   // Check's own timer: Preflight never touches it
    readonly Dictionary<ClickOwner, string> noted = new Dictionary<ClickOwner, string>();
    readonly Dictionary<ClickOwner, long> clickedAt = new Dictionary<ClickOwner, long>();   // when the owner sent its last click in this request
    readonly Dictionary<ClickOwner, int> offerRun = new Dictionary<ClickOwner, int>();      // offer pop-ups closed in a row
    readonly Dictionary<ClickOwner, string> haltDetail = new Dictionary<ClickOwner, string>();
    readonly Dictionary<ClickOwner, long[]> stall = new Dictionary<ClickOwner, long[]>();       // UnknownStall: since, last call
    readonly Dictionary<ClickOwner, string> stallBrief = new Dictionary<ClickOwner, string>();
    readonly HashSet<ClickOwner> searchWaited = new HashSet<ClickOwner>();                      // an unknown screen was waited for during a form search
    readonly Dictionary<ClickOwner, long> foundryWait = new Dictionary<ClickOwner, long>();   // FoundryButton: since when the Foundry's form is unreadable
    readonly Dictionary<ClickOwner, long> ownUnreadWait = new Dictionary<ClickOwner, long>();   // EscapeBack: since when the pilot's own screen is shown but unread
    readonly Dictionary<ClickOwner, int> unknownBacks = new Dictionary<ClickOwner, int>();    // UnknownBack presses in this run
    readonly Dictionary<ClickOwner, long> backTried = new Dictionary<ClickOwner, long>();     // the last UnknownBackDue yes
    readonly Dictionary<ClickOwner, ClickTarget> lastTarget = new Dictionary<ClickOwner, ClickTarget>();   // the owner's last click
    readonly Dictionary<ClickOwner, int> cityBacks = new Dictionary<ClickOwner, int>();       // CityBack presses in this run
    readonly Dictionary<ClickOwner, int> escapes = new Dictionary<ClickOwner, int>();         // EscapeBack presses in this run
    /// <summary>The flow each owner runs now (the hosts set it every tick: CityFirst.OfLocate of the request's purpose ...); its work screen gets no Escape.</summary>
    public readonly Dictionary<ClickOwner, PilotFlow> Flows = new Dictionary<ClickOwner, PilotFlow>();
    readonly HashSet<ClickOwner> barRetried = new HashSet<ClickOwner>();                     // «Инвентарь» opened its bar neighbour: pressed again once
    readonly HashSet<ClickOwner> escapeDue = new HashSet<ClickOwner>();                       // a back press changed nothing: one Escape next
    readonly Dictionary<ClickOwner, int> tipEscapes = new Dictionary<ClickOwner, int>();      // EquipTipEscape presses in this run
    /// <summary>The item tip / compare forms the equip helper closes with EquipTipEscape before a slot step (its own flow forms: ScreenMap.FlowForms).</summary>
    public static readonly string[] TipForms = { "Form_CharactorEquipTip", "Form_EquipedCompared" };
    public const int MaxTipEscapes = 2;
    readonly HashSet<ClickOwner> cellRetried = new HashSet<ClickOwner>();                     // an Ascension cell click changed nothing: placed again once
    /// <summary>An Ascension cell click changed nothing and is placed again (the owner's pilot reads the list again: CellAgain); once a run.</summary>
    public readonly HashSet<ClickOwner> CellAgainDue = new HashSet<ClickOwner>();
    readonly HashSet<ClickOwner> cityInvRetried = new HashSet<ClickOwner>();                  // «Инвентарь» changed nothing on the city: the bar read again, pressed again once
    /// <summary>A city «Инвентарь» click changed nothing (the city still shown): the host drops the bar's place and looks for it again
    /// (CityBar.Drop) before the one more press; once a run.</summary>
    public readonly HashSet<ClickOwner> CityAgainDue = new HashSet<ClickOwner>();
    readonly Dictionary<ClickOwner, long> cityTried = new Dictionary<ClickOwner, long>();     // the last CityBackDue / EscapeDue yes
    readonly Dictionary<ClickOwner, int> mapMoves = new Dictionary<ClickOwner, int>();        // MapClose clicks and map Escapes in this run
    readonly Dictionary<ClickOwner, long> mapTried = new Dictionary<ClickOwner, long>();      // the last MapDue yes
    // the retry policy: the step that failed last and how often in a row, retries this run, no click until, the last retry (the window)
    readonly Dictionary<ClickOwner, ClickTarget> failStep = new Dictionary<ClickOwner, ClickTarget>();
    readonly Dictionary<ClickOwner, int> failCount = new Dictionary<ClickOwner, int>(), retries = new Dictionary<ClickOwner, int>();
    readonly Dictionary<ClickOwner, long> retryUntil = new Dictionary<ClickOwner, long>(), retryAt = new Dictionary<ClickOwner, long>(), chainUntil = new Dictionary<ClickOwner, long>();
    readonly Dictionary<ClickOwner, string> retryWhy = new Dictionary<ClickOwner, string>();
    readonly Dictionary<ClickOwner, ClickTarget> stepNow = new Dictionary<ClickOwner, ClickTarget>();   // the step being judged (Check's target, a watch's)
    readonly Dictionary<ClickOwner, ClickTarget> pilotStep = new Dictionary<ClickOwner, ClickTarget>(); // the pilot's own step (the last target that is not a way out)
    long lastNow;
    readonly HashSet<ClickOwner> modalChecked = new HashSet<ClickOwner>();   // the screen Check allowed the owner's click on had a pop-up / modal open

    void Note(ClickOwner o, string key, string text) {
      string was; if (noted.TryGetValue(o, out was) && was == key) return;
      noted[o] = key;
      var s = Sink; if (s != null) s("click gate " + o + ": " + text);
    }

    /// <summary>Why the owner's pilot stopped ("" = it runs).</summary>
    public string HaltReason(ClickOwner o) { string r; return halted.TryGetValue(o, out r) ? r : ""; }
    /// <summary>The stop's detail (what was clicked / seen), "" = it runs.</summary>
    public string HaltDetail(ClickOwner o) { string r; return haltDetail.TryGetValue(o, out r) ? r : ""; }
    /// <summary>Offer pop-ups this owner closed in a row.</summary>
    public int OfferRun(ClickOwner o) { int n; return offerRun.TryGetValue(o, out n) ? n : 0; }
    public bool Halted(ClickOwner o) { return halted.ContainsKey(o); }
    /// <summary>A click is still waiting to show its effect.</summary>
    public bool Pending(ClickOwner o) { return watches.ContainsKey(o); }
    /// <summary>Some owner's click is on its way (its effect awaited): the screen is read often then (event confirmation).</summary>
    public bool AnyPending { get { return watches.Count > 0; } }
    readonly Dictionary<ClickOwner, long> effectAt = new Dictionary<ClickOwner, long>();
    /// <summary>When the owner's last click was seen to take effect (0 = never): the pilot's wait after it can end then.</summary>
    public long EffectAt(ClickOwner o) { long t; return effectAt.TryGetValue(o, out t) ? t : 0; }

    /// <summary>A new request (or the player started it again): the owner may click again.</summary>
    public void Resume(ClickOwner o) {
      halted.Remove(o); watches.Remove(o); unknownSince.Remove(o); noted.Remove(o); clickedAt.Remove(o); offerRun.Remove(o); haltDetail.Remove(o); foundryWait.Remove(o); ownUnreadWait.Remove(o);
      stall.Remove(o); stallBrief.Remove(o); searchWaited.Remove(o);
      unknownBacks.Remove(o); backTried.Remove(o); lastTarget.Remove(o);
      cityBacks.Remove(o); escapes.Remove(o); escapeDue.Remove(o); cityTried.Remove(o); cellRetried.Remove(o); CellAgainDue.Remove(o); tipEscapes.Remove(o); barRetried.Remove(o); cityInvRetried.Remove(o); CityAgainDue.Remove(o);
      mapMoves.Remove(o); mapTried.Remove(o); coverSince.Remove(o);
      failStep.Remove(o); failCount.Remove(o); retries.Remove(o); retryUntil.Remove(o); retryAt.Remove(o); chainUntil.Remove(o); retryWhy.Remove(o); stepNow.Remove(o); pilotStep.Remove(o); RetryDue.Remove(o);
    }

    /// <summary>The screen map's verdict for the form on top (ScreenState.Top): on an unknown screen, or for a pop-up over a known screen or the
    /// city ("form:" modal). Dangerous: Stop; a fight / loading / result: Wait; a pop-up with its ×: Close (the point; never on one of its other
    /// buttons); a form the game's back key closes: Escape (never on the city: Stop there); a full screen with its back arrow on an unknown
    /// screen: Back (the UnknownBack press, at the map's point); else None (the older rules decide). An offer pop-up and a held screen: None.</summary>
    public static MapDecision MapExit(ScreenState s, double W, double H, double U) {
      var d = new MapDecision();
      if (s == null || s.Held || s.OfferCount > 0) return d;
      if (s.Kind == ScreenKind.Unknown && !string.IsNullOrEmpty(s.Flow)) return d;   // a pilot's flow form shown: the screen under it is only not read
      bool over = s.ModalOpen && s.ModalWhy != null && s.ModalWhy.StartsWith("form:");
      if (s.Kind == ScreenKind.Unknown ? s.ModalOpen : !over) return d;
      d.Form = s.Top ?? ""; d.Class = s.TopClass;
      if (d.Form == "") return d;
      var e = ScreenMap.ByName(d.Form);
      string why = e != null ? e.Why : "";
      switch (d.Class) {
        case 'D': d.Move = MapMove.Stop; d.Why = "dangerous: " + why; break;
        case 'W': d.Move = MapMove.Wait; d.Why = why; break;
        case 'P': {
          var pt = ScreenMap.ExitPoint(d.Form, W, H, U);
          if (pt != null && !ScreenMap.OnNever(d.Form, pt.X, pt.Y, W, H, U)) { d.Move = MapMove.Close; d.Point = pt; d.Why = why; }
          break;
        }
        case 'E':
          if (s.Kind == ScreenKind.City) { d.Move = MapMove.Stop; d.Why = "only the back key closes it, and Escape is never sent on the city"; }
          else { d.Move = MapMove.Escape; d.Why = why; }
          break;
        case 'F':
          if (s.Kind == ScreenKind.Unknown) { d.Move = MapMove.Back; d.Point = ScreenMap.ExitPoint(d.Form, W, H, U); d.Why = why; }
          break;
      }
      return d;
    }

    /// <summary>The forms nobody knows shown with the screen: on an unknown screen its TopForm list, over a known screen or the city the pop-ups of
    /// its "form:" modal.</summary>
    static List<string> ShownForms(ScreenState s) {
      var l = new List<string>();
      string list = s.Kind == ScreenKind.Unknown ? s.TopForm : s.ModalWhy != null && s.ModalWhy.StartsWith("form:") ? s.ModalWhy.Substring(5) : "";
      if (!string.IsNullOrEmpty(list)) foreach (var n in list.Split(',')) if (n.Trim() != "") l.Add(n.Trim());
      return l;
    }

    /// <summary>The map's × or Escape may be tried now: the owner runs (not halted, no click on its way, no form search, not a hint run), the form
    /// has been on top MapSettleMs, no try within UnknownBackRetryMs; MaxMapMoves made already: the owner stops ("nochange").</summary>
    public bool MapDue(ClickOwner o, ScreenState s, long now) {
      if (s == null || halted.ContainsKey(o) || watches.ContainsKey(o) || SearchWait || HintOnly(o)) return false;
      if (now - s.SinceMs < MapSettleMs) return false;
      long t; if (mapTried.TryGetValue(o, out t) && now - t < UnknownBackRetryMs) return false;
      int n = MapMoves(o);
      if (n >= MaxMapMoves) { Halt(o, "nochange", "the screen map's exits: " + n + " made already, the game shows " + s.Brief + " (" + s.Top + ")"); return false; }
      mapTried[o] = now;
      return true;
    }
    public int MapMoves(ClickOwner o) { int n; return mapMoves.TryGetValue(o, out n) ? n : 0; }
    /// <summary>The map says the form on top is dangerous (or has no exit allowed here): the owner stops, nothing of it is pressed.</summary>
    public GateResult MapStop(ClickOwner o, MapDecision d, ScreenState s) {
      return Halt(o, "dangerous", "the game shows " + d.Form + " (" + d.Why + ") on " + (s != null ? s.Brief : "?") + " - nothing of it is pressed");
    }
    /// <summary>The map closes the form on top with the back key: one Escape is due (EscapeSend; judged by CheckEscape on a fresh read).</summary>
    public void MapEscape(ClickOwner o) { escapeDue.Add(o); mapMoves[o] = MapMoves(o) + 1; }
    /// <summary>The fresh screen's pop-up over a known screen is one the map closes with Escape (never on the city).</summary>
    static bool MapEscapeOk(ScreenState s) {
      return s != null && s.Kind != ScreenKind.City && s.TopClass == 'E' && s.ModalOpen && s.ModalWhy != null && s.ModalWhy.StartsWith("form:") && s.OfferCount == 0;
    }

    /// <summary>The back-to-the-city presses (CityBack) and the Escape presses (EscapeBack) made in this run.</summary>
    public int CityBacks(ClickOwner o) { int n; return cityBacks.TryGetValue(o, out n) ? n : 0; }
    public int Escapes(ClickOwner o) { int n; return escapes.TryGetValue(o, out n) ? n : 0; }
    /// <summary>A back press changed nothing: the owner's next step is one Escape (EscapeDue).</summary>
    public bool EscapePending(ClickOwner o) { return escapeDue.Contains(o); }

    /// <summary>The back press changed nothing but the screen is now one the pilot can go on from: no Escape any more.</summary>
    public void DropEscape(ClickOwner o) { escapeDue.Remove(o); }

    /// <summary>The gear pilots' city-first check (CityFirst.NeedsCity of the owner's gear flow): Equip goes on in the city or on the hero
    /// screen; Sell in the inventory, the city or the hero screen (its own HeroBack). Locate asks CityFirst with its purpose's flow.</summary>
    public static bool AwayFromStart(ClickOwner o, ScreenState s) {
      if (o == ClickOwner.Equip) return CityFirst.NeedsCity(PilotFlow.Equip, s);
      if (o == ClickOwner.Sell) return CityFirst.NeedsCity(PilotFlow.Sell, s);
      return false;
    }

    bool HintOnly(ClickOwner o) { return (o == ClickOwner.Sell && NoSellClicks) || (o == ClickOwner.Locate && NoLocateClicks); }

    static bool HasForm(string list, string[] names) {
      if (string.IsNullOrEmpty(list)) return false;
      foreach (var n in list.Split(',', ':')) if (Array.IndexOf(names, n.Trim()) >= 0) return true;
      return false;
    }
    static readonly string[] BattleForms = { "Form_Battle" };
    static readonly string[] FoundryForms = { "Form_EquipSubliming" };
    /// <summary>The game's exit question is shown (as the top unknown form, a pop-up's name or a new form over a screen).</summary>
    public static bool ExitDialogShown(ScreenState s) {
      return s != null && (HasForm(s.TopForm, ExitDialogForms) || HasForm(s.ModalWhy, ExitDialogForms) || HasForm(s.Offer, ExitDialogForms));
    }

    /// <summary>The screen allows the back-to-the-city press (CityBack): "" = yes, else why not. Only a known screen that is not the city, not
    /// held, no pop-up on it, no fight's form shown.</summary>
    public static string CityBackBlock(ScreenState s) {
      if (s == null) return "no screen";
      if (s.Kind == ScreenKind.City) return "the city is on top";
      if (s.Kind == ScreenKind.Unknown) return "the screen is not recognised";
      if (s.Held) return "the screen is held";
      if (s.OfferCount > 0) return "an offer pop-up is open";
      if (s.ModalOpen && !FullScreensOver(s)) return "a pop-up is open (" + s.ModalWhy + ")";
      if (!string.IsNullOrEmpty(s.Flow)) return FlowBlock + s.Flow;
      if (HasForm(s.ShownKnown, BattleForms)) return "a fight's form is shown";
      if (ExitDialogShown(s)) return "the exit dialog is shown";
      return "";
    }

    /// <summary>The "pop-up" over the known screen is full screens only (the map's F: each left by the standard back arrow top left) - the window
    /// table has no order, so one may lie UNDER the screen: the back arrow is the same press either way (it closes whichever is on top).</summary>
    static bool FullScreensOver(ScreenState s) {
      if (s.OfferCount > 0 || s.ModalWhy == null || !s.ModalWhy.StartsWith("form:")) return false;
      var l = ShownForms(s); if (l.Count == 0) return false;
      foreach (var n in l) { var e = ScreenMap.ByName(n); if (e == null || e.Class != 'F' || !e.StdBack) return false; }
      return true;
    }

    /// <summary>The screen allows the one Escape press (EscapeBack): "" = yes. NEVER on the city (Escape there asks to quit the game), never in a
    /// fight (its form shown, or the shown forms not read), never with a pop-up, an offer or the exit dialog shown; on an unknown screen only
    /// where the unknown-screen back press is allowed.</summary>
    public static string EscapeBlock(ScreenState s) {
      if (s == null) return "no screen";
      if (s.Kind == ScreenKind.City) return "the city is on top (Escape asks to quit the game there)";
      if (s.Held) return "the screen is held";
      if (s.OfferCount > 0) return "an offer pop-up is open";
      if (s.ModalOpen && !MapEscapeOk(s)) return "a pop-up is open (" + s.ModalWhy + ")";
      if (!string.IsNullOrEmpty(s.Flow)) return FlowBlock + s.Flow;
      if (ExitDialogShown(s)) return "the exit dialog is shown";
      if (Exempt(s.TopForm)) return "a fight's result";
      if (s.ShownKnown == null) return "the shown forms are not known";
      if (HasForm(s.ShownKnown, BattleForms)) return "a fight's form is shown";
      if (s.Kind == ScreenKind.Unknown) { string b = UnknownBackBlock(s); if (b != "" && !(s.TopClass == 'E' && b.StartsWith(MapBlock))) return b; }
      return "";
    }

    /// <summary>The host's pilot cannot go on from this screen (it needs the city, or its next target is not on this screen): may it press the
    /// back arrow now? The owner runs (not halted, no click on its way, no form search, not a hint run, no Escape due), the screen (the tick's)
    /// is a known one other than the city with no pop-up, it has lasted CityBackSettleMs, no try within UnknownBackRetryMs. MaxCityBacks
    /// presses made already: the owner stops ("nochange": the city does not come).</summary>
    public bool CityBackDue(ClickOwner o, ScreenState s, long now) {
      if (halted.ContainsKey(o) || watches.ContainsKey(o) || SearchWait || HintOnly(o) || escapeDue.Contains(o)) return false;
      if (CityBackBlock(s) != "") return false;
      if (now - s.SinceMs < CityBackSettleMs) return false;
      long t; if (cityTried.TryGetValue(o, out t) && now - t < UnknownBackRetryMs) return false;
      int n = CityBacks(o);
      if (n >= MaxCityBacks) { Halt(o, "nochange", "back to the city: the back arrow was pressed " + n + " times, the game shows " + s.Brief); return false; }
      cityTried[o] = now;
      return true;
    }

    /// <summary>The one Escape after a back press that changed nothing may be sent now (the fresh state is judged again by Check). The city on
    /// top meanwhile: nothing is due any more.</summary>
    public bool EscapeDue(ClickOwner o, ScreenState s, long now) {
      if (!escapeDue.Contains(o)) return false;
      if (s != null && s.Kind == ScreenKind.City) { escapeDue.Remove(o); return false; }
      if (halted.ContainsKey(o) || watches.ContainsKey(o) || SearchWait || HintOnly(o)) return false;
      // (a tick's state has no shown forms read: the fresh one Check judges has them)
      if (s == null || s.Held || (s.ModalOpen && !MapEscapeOk(s)) || !string.IsNullOrEmpty(s.Flow) || s.OfferCount > 0 || ExitDialogShown(s) || Exempt(s.TopForm) || HasForm(s.ShownKnown, BattleForms)) return false;
      long t; if (cityTried.TryGetValue(o, out t) && now - t < UnknownBackRetryMs) return false;
      cityTried[o] = now;
      return true;
    }

    /// <summary>The unknown-screen back presses made in this run.</summary>
    public int UnknownBacks(ClickOwner o) { int n; return unknownBacks.TryGetValue(o, out n) ? n : 0; }

    /// <summary>The screen allows the unknown-screen back press (a fresh or a tick's state): "" = yes, else why not. Never on a known screen,
    /// the city, a held screen, with an offer / modal / pop-up, with a known screen's form shown or not read (the battle among them), on a fight's result.</summary>
    public static string UnknownBackBlock(ScreenState s) {
      if (s == null) return "no screen";
      if (s.Kind != ScreenKind.Unknown) return "the screen is " + s.Kind;
      if (s.Held) return "the screen is held";
      if (s.OfferCount > 0) return "an offer pop-up is open";
      if (s.ModalOpen) return "a pop-up is open (" + s.ModalWhy + ")";
      if (!string.IsNullOrEmpty(s.Flow)) return FlowBlock + s.Flow;
      if (!s.OfferRead) return "the window table is not read";
      if (Exempt(s.TopForm)) return "a fight's result";
      if (s.ShownKnown == null) return "the shown forms are not known";
      if (s.ShownKnown != "" && !AllBelow(s.Order, s.Top, s.ShownKnown)) return "a known screen's form is shown (" + s.ShownKnown + ")";
      if (s.Top != "" && "DWPE".IndexOf(s.TopClass) >= 0) return MapBlock + s.Top + " is " + ScreenMap.ClassName(s.TopClass);
      return "";
    }
    /// <summary>The window table's order puts every form of the comma list <paramref name="list"/> under <paramref name="top"/> (false = no order,
    /// or not so): a known screen under the full screen on top is not on top.</summary>
    public static bool AllBelow(List<string> order, string top, string list) {
      if (order == null || string.IsNullOrEmpty(top)) return false;
      int ti = order.IndexOf(top); if (ti < 0) return false;
      foreach (var n in list.Split(',')) { string x = n.Trim(); if (x != "" && order.IndexOf(x) <= ti) return false; }
      return true;
    }
    const string MapBlock = "the screen map: ";
    /// <summary>A pilot's own flow form is shown (ScreenMap.FlowForms): no back press or Escape acts on it.</summary>
    const string FlowBlock = "a pilot's own flow form is shown: ";

    /// <summary>The host may try the back press now: the owner runs (not halted, no click on its way, no form search), the screen
    /// (the tick's) has been the same unrecognised one for UnknownBackSettleMs, presses are left, and no try was made within UnknownBackRetryMs.
    /// The fresh state is judged again by Check (which also needs the shown known forms).</summary>
    public bool UnknownBackDue(ClickOwner o, ScreenState s, long now) {
      if (halted.ContainsKey(o) || watches.ContainsKey(o) || SearchWait) return false;
      if ((o == ClickOwner.Sell && NoSellClicks) || (o == ClickOwner.Locate && NoLocateClicks)) return false;
      if (!Unrecognised(s) || s.ModalOpen || !string.IsNullOrEmpty(s.Flow) || !s.OfferRead || Exempt(s.TopForm) || (s.Top != "" && "DWPE".IndexOf(s.TopClass) >= 0)) return false;
      if (now - s.SinceMs < UnknownBackSettle(s)) return false;
      if (UnknownBacks(o) >= MaxUnknownBacks) return false;
      long t; if (backTried.TryGetValue(o, out t) && now - t < UnknownBackRetryMs) return false;
      backTried[o] = now;
      return true;
    }

    /// <summary>The point is the back arrow's (HeroGeometry.BackX / BackY, client px from the top left in units of <paramref name="U"/>).</summary>
    public static bool OnBackArrow(double x, double y, double U) {
      var g = new HeroGeometry();
      return U > 0 && Math.Abs(x - g.BackX * U) <= UnknownBackTol * U && Math.Abs(y - g.BackY * U) <= UnknownBackTol * U;
    }

    /// <summary>The screen is unrecognised for the unknown-screen rules: Unknown, no offer pop-up on it (the pilots close those), not held from a known one.</summary>
    static bool Unrecognised(ScreenState s) { return s != null && s.Kind == ScreenKind.Unknown && s.OfferCount == 0 && !s.Held; }

    /// <summary>A pilot run on an unrecognised screen: true = the owner stops now, "unknown screen &lt;form&gt;" (the same unrecognised screen for
    /// UnknownStallMs, calls at most StallGapMs apart). A known screen, an offer on top, a held screen, a fight's result form or a running
    /// form search (SearchWait) never count. When a form search that was waited for ends with the screen still unrecognised, the owner stops at once.</summary>
    public bool UnknownStall(ClickOwner o, ScreenState s, long now) {
      if (halted.ContainsKey(o)) return false;
      if (!Unrecognised(s) || Exempt(s.TopForm)) { stall.Remove(o); searchWaited.Remove(o); return false; }
      if (SearchWait) { searchWaited.Add(o); stall.Remove(o); return false; }
      string name = s.TopForm != "" ? s.TopForm : "?";
      if (searchWaited.Remove(o)) { Halt(o, "unknown", "unknown screen " + name + ": the form search ended without finding the screen"); return halted.ContainsKey(o); }
      long[] t; string b;
      if (!stall.TryGetValue(o, out t) || now - t[1] > StallGapMs || !stallBrief.TryGetValue(o, out b) || b != s.Brief) { t = new[] { now, now }; stall[o] = t; stallBrief[o] = s.Brief; }
      t[1] = now;
      if (now - t[0] < UnknownStallMs) return false;
      Halt(o, "unknown", "unknown screen " + name + " for " + ((now - t[0]) / 1000) + " s");
      return halted.ContainsKey(o);
    }

    static bool Exempt(string top) {
      if (string.IsNullOrEmpty(top)) return false;
      foreach (var n in top.Split(',')) if (Array.IndexOf(StallExempt, n.Trim()) >= 0 || ScreenMap.ClassOf(n.Trim()) == 'W') return true;
      return false;
    }

    /// <summary>The pilot stops (also used by the host for its own reasons, e.g. the player's cancel).</summary>
    public GateResult Halt(ClickOwner o, string reason, string detail) {
      if (halted.ContainsKey(o)) return GateResult.Deny(reason, detail);
      string why;
      if (Retry(o, reason, detail, out why)) return GateResult.Wait("retry", detail);
      halted[o] = reason; haltDetail[o] = detail ?? ""; watches.Remove(o); Note(o, "halt:" + reason, "STOP (" + reason + ") " + detail + why + " - hints only from now on");
      return GateResult.Deny(reason, detail);
    }

    /// <summary>The re-check right before the press (the host's click thread, after the cursor moved): a pop-up or form that came on top since
    /// the gate's fresh read (an offer not in <paramref name="offersBefore"/>, a form ScreenMap.Newcomers names), the pop-up an OfferClose
    /// meant gone already, or the window table unread: the reason (no press: the click is a retry, ClickGate "popup"); null = press.</summary>
    public static string LateTop(ClickTarget t, string form, ICollection<string> offersBefore, ICollection<string> offersNow, ICollection<string> formsBefore, ICollection<string> formsNow) {
      if (offersNow == null) return "the window table could not be read right before the press";
      foreach (var n in offersNow) if (offersBefore == null || !offersBefore.Contains(n)) return "the pop-up " + n + " came on top right before the press";
      if (t == ClickTarget.OfferClose && !offersNow.Contains(form ?? "")) return (form ?? "the pop-up") + " is gone already";
      bool way = t == ClickTarget.UnknownBack || t == ClickTarget.CityBack || t == ClickTarget.EscapeBack || t == ClickTarget.MapClose;
      string nc = way ? "" : ScreenMap.Newcomers(formsBefore, formsNow);
      return nc != "" ? "the form " + nc + " came on top right before the press" : null;
    }

    static bool IsWayOut(ClickTarget t) { return t == ClickTarget.OfferClose || t == ClickTarget.MapClose || t == ClickTarget.UnknownBack || t == ClickTarget.CityBack || t == ClickTarget.EscapeBack || t == ClickTarget.EquipTipEscape; }

    /// <summary>The pilot's step a failure counts for: the target judged / awaited now, or the last one; a way out (a ×, a back press, an
    /// Escape) counts for the pilot's own step it was made for (the last other target), so a loop of ways out and a step ends in the stop too.</summary>
    ClickTarget StepOf(ClickOwner o) {
      ClickTarget t;
      if (!stepNow.TryGetValue(o, out t) && !lastTarget.TryGetValue(o, out t)) t = ClickTarget.None;
      ClickTarget p; if ((t == ClickTarget.None || IsWayOut(t)) && pilotStep.TryGetValue(o, out p)) return p;
      return t;
    }

    /// <summary>The retry policy (the header): true = the failure is retried (nothing is clicked for RetrySettleMs, the run's counts start over,
    /// the step is made again); false = the owner stops (<paramref name="why"/>: the count that ended the retries, "" = not a reason to retry).</summary>
    bool Retry(ClickOwner o, string reason, string detail, out string why) {
      why = "";
      if (!Resilient || Array.IndexOf(RetryReasons, reason) < 0) return false;
      var step = StepOf(o); ClickTarget fs; int was = 0;
      bool same = failStep.TryGetValue(o, out fs) && fs == step && failCount.TryGetValue(o, out was);
      if (!same) was = 0;
      int n = reason == "popup" ? Math.Max(1, was) : was + 1, total = Retries(o) + 1;   // (a pop-up that came in the click's time is not the step's failure)
      if (n >= MaxStepFailures) { why = " (failure " + n + " of " + step + " in a row)"; return false; }
      if (total > MaxRetriesRun) { why = " (" + MaxRetriesRun + " retries this run already)"; return false; }
      failStep[o] = step; failCount[o] = reason == "popup" ? was : n; retries[o] = total;
      watches.Remove(o); escapeDue.Remove(o); offerRun.Remove(o); unknownBacks.Remove(o); cityBacks.Remove(o); escapes.Remove(o); mapMoves.Remove(o); tipEscapes.Remove(o);
      stall.Remove(o);   // (the one-time retries of the gate's own - city-again, bar-again, cell-again - stay used) stallBrief.Remove(o); searchWaited.Remove(o); unknownSince.Remove(o);
      foundryWait.Remove(o); ownUnreadWait.Remove(o); backTried.Remove(o); cityTried.Remove(o); mapTried.Remove(o); clickedAt.Remove(o); noted.Remove(o);
      retryUntil[o] = lastNow + RetrySettleMs; retryAt[o] = lastNow; retryWhy[o] = reason + "/" + Math.Max(1, n); RetryDue.Add(o);
      Note(o, "retry:" + total, "RETRY " + Math.Max(1, n) + "/" + MaxStepFailures + " of " + step + " (" + reason + ") " + detail + " - the screen read again, pop-ups closed, city first, the step again (retry " + total + " of " + MaxRetriesRun + " this run)");
      return true;
    }

    /// <summary>(e) A click was sent: what has to happen next (null = nothing is checked, e.g. a drag).</summary>
    public void Arm(ClickOwner o, ClickTarget t, Func<ScreenState, bool> done, long now) { Arm(o, t, done, now, null); }

    /// <summary>The same; <paramref name="offer"/> = the pop-up an OfferClose closes (its effect is logged as "offer: closed &lt;Form&gt;").</summary>
    public void Arm(ClickOwner o, ClickTarget t, Func<ScreenState, bool> done, long now, string offer) {
      clickedAt[o] = now; lastTarget[o] = t; if (!IsWayOut(t)) pilotStep[o] = t;
      if (t == ClickTarget.OfferClose) offerRun[o] = OfferRun(o) + 1; else offerRun.Remove(o);
      if (t == ClickTarget.UnknownBack) unknownBacks[o] = UnknownBacks(o) + 1;
      if (t == ClickTarget.CityBack) cityBacks[o] = CityBacks(o) + 1;
      if (t == ClickTarget.EscapeBack) { escapes[o] = Escapes(o) + 1; escapeDue.Remove(o); }
      if (t == ClickTarget.EquipTipEscape) { int n; tipEscapes.TryGetValue(o, out n); tipEscapes[o] = n + 1; }
      if (t == ClickTarget.MapClose) mapMoves[o] = MapMoves(o) + 1;
      if (done == null) { watches.Remove(o); return; }
      int ms = t == ClickTarget.CityInventory || t == ClickTarget.FoundryButton || t == ClickTarget.ArtWorkshop || t == ClickTarget.ArtReplace || t == ClickTarget.ArtTakeOverConfirm ? SlowEffectMs : t == ClickTarget.InvCardGem || t == ClickTarget.GemFuncRow || t == ClickTarget.ArtMatCell ? GemEffectMs : t == ClickTarget.ArtBulkCell ? ArtTickMs : EffectMs;
      watches[o] = new Watch { Done = done, Deadline = now + ms, What = t == ClickTarget.OfferClose && offer != null ? "OfferClose(" + offer + ")" : t.ToString(), Offer = t == ClickTarget.OfferClose ? offer ?? "" : null, Target = t, ArmedAt = now, ModalBefore = modalChecked.Contains(o) };
    }

    /// <summary>What a click is expected to change, judged on the state before it (null = nothing to check).</summary>
    public static Func<ScreenState, bool> Expect(ClickRequest r, ScreenState before) {
      long uid = r.CellUid; string selKey = before != null ? before.InvSelKey : ""; string fkey = before != null ? before.FoundryKey : "";
      switch (r.Target) {
        case ClickTarget.HeroBack: {   // the back arrow closes an open gear list first (the hero grid shows again), the hero screen then
          int part = before != null && before.Kind == ScreenKind.HeroScreen ? before.HeroSlot : -1;
          return s => s.Kind != ScreenKind.HeroScreen || (part >= 0 && s.HeroSlot < 0);
        }
        case ClickTarget.CityInventory: return s => s.Kind == ScreenKind.Inventory;
        case ClickTarget.EquipTipEscape: return s => !s.Held && (s.Kind != ScreenKind.HeroScreen || !OnlyTip(s));
        // back to the city / the Escape: the screen is another one (another kind, a hero screen's gear list closed, a pop-up or another unknown form on top)
        case ClickTarget.CityBack: case ClickTarget.EscapeBack: {
          ScreenKind k = before != null ? before.Kind : ScreenKind.Unknown; int slot = before != null ? before.HeroSlot : -1;
          string top = before != null ? before.TopForm : ""; bool modal = before != null && before.ModalOpen; string why = before != null ? before.ModalWhy : "";
          bool bulk = before != null && before.InvBulk, abulk = before != null && before.InvArtBulk;   // (in a sale mode the arrow may leave the mode first)
          return s => s.Kind != k || (k == ScreenKind.HeroScreen && s.HeroSlot != slot) || (k == ScreenKind.Unknown && s.TopForm != top) || s.OfferCount > 0 || (s.ModalOpen && !modal)
                      || (k == ScreenKind.Inventory && (s.InvBulk != bulk || s.InvArtBulk != abulk))
                      || (modal && (!s.ModalOpen || s.ModalWhy != why));   // the pop-up the map's Escape closed
        }
        // the map's pop-up is no longer on top (a read that names no form on an unknown screen is not an effect)
        case ClickTarget.MapClose: { string form = r.Form ?? ""; return s => s.Top != form && (s.Kind != ScreenKind.Unknown || s.TopForm != ""); }
        // the screen is another one: a known one, another unknown form on top, a pop-up (an unknown screen with no name read stays "the same": nochange)
        case ClickTarget.UnknownBack: {
          string top = before != null ? before.TopForm : "";
          return s => s.Kind != ScreenKind.Unknown || s.TopForm != top || s.OfferCount > 0 || s.ModalOpen;
        }
        // the pop-up is no longer shown (read from the window table, so a failed read is no effect)
        case ClickTarget.OfferClose: { string form = r.Form ?? ""; return s => s.OfferRead && s.Offer != form; }
        case ClickTarget.InvEquipTab: return s => s.Kind == ScreenKind.Inventory && s.InvEquipTab;
        case ClickTarget.InvBulkEnter: return s => s.Kind == ScreenKind.Inventory && s.InvBulk;
        case ClickTarget.InvListCell: return s => s.Kind == ScreenKind.Inventory && s.InvSelected == uid;
        case ClickTarget.InvBulkCell: return s => s.Kind == ScreenKind.Inventory && s.InvSelKey != selKey;
        case ClickTarget.FoundryButton: return s => s.Kind == ScreenKind.Foundry;
        case ClickTarget.AscendTab: return s => s.Kind == ScreenKind.Foundry && s.FoundryTab == 2;
        case ClickTarget.SlotTab: return s => s.Kind == ScreenKind.Foundry && s.FoundryKey != fkey;
        case ClickTarget.AscendCell: return s => s.Kind == ScreenKind.Foundry && s.FoundrySel == uid;
        case ClickTarget.InvCardGem: return s => s.Kind == ScreenKind.Gem;
        case ClickTarget.GemFuncRow: { int fn = r.Func; return s => s.Kind == ScreenKind.Gem && s.GemFunc == fn; }
        case ClickTarget.ArtTab: return s => s.Kind == ScreenKind.Inventory && s.InvArtTab;
        case ClickTarget.ArtBulkEnter: return s => s.Kind == ScreenKind.Inventory && s.InvArtTab && s.InvArtBulk;
        // the game's choice (_btDicSelected) holds the artifact now (or no longer holds it, for a tick taken off)
        case ClickTarget.ArtBulkCell: { bool want = !r.Untick; return s => s.Kind == ScreenKind.Inventory && s.Inv != null && s.Inv.Selected.Contains(uid) == want; }
        case ClickTarget.ArtCell: return s => s.Kind == ScreenKind.Inventory && s.InvArtSelected == uid;
        case ClickTarget.ArtWorkshop: return s => s.Kind == ScreenKind.ArtWorkshop;
        case ClickTarget.ArtPromoteTab: return s => s.Kind == ScreenKind.ArtWorkshop && s.ArtWsTab == 2;
        case ClickTarget.ArtMatCell: { int mi = r.MatIndex; return s => s.Kind == ScreenKind.ArtWorkshop && s.ArtWs != null && s.ArtWs.MatSel.Contains(mi); }
        // the hero screen's artifact steps: the tab, the open list, the selected artifact, «Заменить» (the pop-up, or the artifact on the hero), the pop-up's «Заменить» (on the hero)
        case ClickTarget.ArtHeroTab: return s => s.Kind == ScreenKind.HeroScreen && s.HeroTab == RFX.HeroArtTab;
        case ClickTarget.ArtOpenList: return s => s.Kind == ScreenKind.HeroScreen && s.HeroScr != null && s.HeroScr.ArtListOpen;
        case ClickTarget.ArtHeroCell: { long au = r.ArtUid; return s => s.Kind == ScreenKind.HeroScreen && s.HeroScr != null && s.HeroScr.ArtSel == au; }
        case ClickTarget.ArtReplace: { long hh = r.Hero; return s => s.Kind == ScreenKind.HeroScreen && s.HeroScr != null && (s.HeroScr.ArtTargetOwner == hh || (s.ModalOpen && s.ModalWhy == ModalEquipPopup)); }
        case ClickTarget.ArtTakeOverConfirm: { long hh = r.Hero; return s => s.Kind == ScreenKind.HeroScreen && s.HeroScr != null && s.HeroScr.ArtTargetOwner == hh; }
        case ClickTarget.ArtHideWorn: { int want = r.Untick ? 0 : 1; return s => s.Kind == ScreenKind.HeroScreen && s.HeroScr != null && s.HeroScr.ArtHideWorn == want; }
      }
      return null;
    }

    /// <summary>(e) Looks at the screen now: a click that showed its effect is done; one that did not within its time stops the owner.
    /// Called every tick for the owners with a click on the way.</summary>
    public void Observe(ScreenState s, long now) {
      if (now > lastNow) lastNow = now;
      if (s == null || watches.Count == 0) return;
      foreach (var o in new List<ClickOwner>(watches.Keys)) {
        var w = watches[o];
        stepNow[o] = w.Target;
        if (w.Done(s)) {
          watches.Remove(o); Note(o, "effect:" + w.What + ":" + now, w.What + " took effect");
          ReadTiming.Confirm(now - w.ArmedAt); effectAt[o] = now;
          // a step of the pilot went on: the failures in a row are over (not for the ways out - a ×, a back press, an Escape: their loop is counted on)
          if (!IsWayOut(w.Target)) { failStep.Remove(o); failCount.Remove(o); }
          if (w.Offer != null) { chainUntil[o] = now + OfferChainMs; RetryDue.Add(o); var sk = Sink; if (sk != null) sk("offer: closed " + w.Offer); Note(o, "chain:" + now, "the next pop-up of the chain is waited for " + OfferChainMs + " ms before any other click"); }
        }
        else if (now > w.Deadline && SearchWait && Unrecognised(s)) { w.Deadline = now + 1000; searchWaited.Add(o); }   // the form is being searched for: its effect may still show
        else if (!StillAwaited(o, w, now)) {
          // a back press that changed nothing: one Escape next (not on the city, at most MaxEscapes a run), else the stop
          if ((w.Target == ClickTarget.CityBack || w.Target == ClickTarget.UnknownBack) && Escapes(o) < MaxEscapes && s.Kind != ScreenKind.City && !ExitDialogShown(s)) {
            watches.Remove(o); escapeDue.Add(o);
            Note(o, "escape-due:" + w.ArmedAt, w.What + " changed nothing within the time (screen " + s.Brief + ") - one Escape next");
          }
          // an Ascension cell placed from a list still moving (a slow PC): the list is read again and the cell placed again, once a run
          else if (w.Target == ClickTarget.AscendCell && s.Kind == ScreenKind.Foundry && cellRetried.Add(o)) {
            watches.Remove(o); CellAgainDue.Add(o);
            Note(o, "cell-again:" + w.ArmedAt, w.What + " changed nothing within the time (screen " + s.Brief + ") - the list is read again, the cell placed again once");
          }
          // «Инвентарь» took its neighbour in the city's bar («Сборка»: «План сборки», an offer list): city first again - the list is closed
          // with its × / back arrow (OfferStep) and «Инвентарь» pressed again, once a run
          else if (w.Target == ClickTarget.CityInventory && s.OfferCount > 0 && barRetried.Add(o)) {
            watches.Remove(o);
            Note(o, "bar-again:" + w.ArmedAt, w.What + " opened " + s.Offer + " instead (the bar's neighbour) - closed, back to the city, «Инвентарь» again once");
          }
          // «Инвентарь» changed nothing and the city is still shown (the city was built again after a guild / event screen and its bar was not
          // there yet, live 1.6.66.385): the bar is read again and «Инвентарь» pressed once more, once a run
          else if (w.Target == ClickTarget.CityInventory && s.Kind == ScreenKind.City && cityInvRetried.Add(o)) {
            watches.Remove(o); CityAgainDue.Add(o);
            Note(o, "city-again:" + w.ArmedAt, w.What + " changed nothing within the time (screen " + s.Brief + ") - the bar is read again, «Инвентарь» pressed again once");
          }
          // a pop-up came while the effect was awaited (the game's login chain): not the step's failure - closed, the step again
          else if (w.Target != ClickTarget.OfferClose && (s.OfferCount > 0 || (s.ModalOpen && s.ModalWhy != null && s.ModalWhy.StartsWith("form:"))) && !w.ModalBefore) Halt(o, "popup", w.What + " changed nothing: a pop-up came in its time (screen " + s.Brief + ")");
          else Halt(o, "nochange", w.What + " changed nothing within the time (screen " + s.Brief + ")");
        }
        stepNow.Remove(o);
      }
      // the game's exit question after the pilot's Escape: stop (nothing of it is ever pressed)
      foreach (var o in new List<ClickOwner>(lastTarget.Keys)) {
        long at;
        if (lastTarget[o] == ClickTarget.EscapeBack && !halted.ContainsKey(o) && ExitDialogShown(s) && clickedAt.TryGetValue(o, out at) && now - at <= 10000)
          Halt(o, "exitdialog", "the game's exit question is shown after the Escape (" + s.Brief + ") - nothing of it is pressed, close it with its «Отмена»");
      }
    }

    /// <summary>Is the click's effect still awaited at <paramref name="now"/>? Its deadline; for CityInventory / FoundryButton also the search for its
    /// form that runs when the deadline passes (a slow PC): then the deadline follows the search, and its end plus SearchGraceMs, up to SearchCapMs.</summary>
    bool StillAwaited(ClickOwner o, Watch w, long now) {
      if (w.Target != ClickTarget.CityInventory && w.Target != ClickTarget.FoundryButton) return now <= w.Deadline;
      long cap = w.ArmedAt + SearchCapMs;
      if (w.Search == 1) {   // the search runs: the deadline follows it; once it ended, its result is read for SearchGraceMs
        if (now >= cap) return false;
        if (!SearchBusy(w.Target)) w.Search = 2;
        w.Deadline = Math.Min(now + SearchGraceMs, cap);
        if (w.Search == 2 && FormIsFound(w.Target)) w.Deadline = cap;
        return true;
      }
      if (w.Search == 2 && FormIsFound(w.Target)) { w.Deadline = cap; return now < cap; }   // found: the screen is read soon, wait for it up to the cap
      if (now <= w.Deadline) return true;
      if (w.Search == 0 && now < cap && SearchBusy(w.Target)) {
        Note(o, "search:" + w.What + ":" + w.ArmedAt, w.What + ": no effect yet, the form search still runs - waiting for it (up to " + SearchCapMs + " ms after the click)");
        w.Search = 1; w.Deadline = Math.Min(now + SearchGraceMs, cap);
        return true;
      }
      return false;
    }
    bool FormIsFound(ClickTarget t) { var p = FormFound; return p != null && p(t); }
    bool SearchBusy(ClickTarget t) { var p = FormSearchPending; return p != null && p(t); }

    /// <summary>Before the pilot decides anything this tick: may it go on? Deny = it is stopped; Wait = not now (a click is still on its way,
    /// or the screen is not recognised yet). An unrecognised screen right after the pilot's OWN click (within OwnClickWindowMs; the tracker has
    /// held the last screen for its 1.5 s already) stops the pilot; at any other time (the player clicked something, the game is switching
    /// forms) it is only waited for: no click while it lasts, the pilot goes on when the screen is known again.</summary>
    public GateResult Preflight(ClickOwner o, ScreenState s, long now) {
      string h;
      if (now > lastNow) lastNow = now;
      if (halted.TryGetValue(o, out h)) return GateResult.Deny("halted", h);
      Observe(s, now);
      if (halted.TryGetValue(o, out h)) return GateResult.Deny("halted", h);
      if (watches.ContainsKey(o)) return GateResult.Wait("pending", "the previous click has not shown its effect yet");
      // an offer pop-up on top: the pilot closes it first (OfferClose), nothing else is decided under it
      if (s != null && s.OfferCount > 0) return GateResult.Wait("offer", s.ModalWhy);
      long until;
      if (retryUntil.TryGetValue(o, out until) && now < until) return GateResult.Wait("retry", "a retry: the screen is read again before the step (" + (until - now) + " ms)");
      if (chainUntil.TryGetValue(o, out until) && now < until) return GateResult.Wait("chain", "a pop-up was closed: the next one of the chain is waited for (" + (until - now) + " ms)");
      bool modeUnknown = s != null && s.Kind == ScreenKind.Inventory && s.InvEquipTab && !s.InvBulkKnown;
      if (s == null || s.Kind == ScreenKind.Unknown || modeUnknown) {
        string d = modeUnknown ? "the inventory's mode is not readable" : "the screen is not recognised" + (s != null && s.TopForm != "" ? " (top form " + s.TopForm + " not in the list)" : "");
        if (!modeUnknown && SearchWait && Unrecognised(s)) { searchWaited.Add(o); stall.Remove(o); return GateResult.Wait("search", d + " (a form search runs)"); }
        long at;
        // (the back press leaves one unknown screen for another one at times: that is its effect, judged by its watch, not a stop)
        ClickTarget lt;
        if (clickedAt.TryGetValue(o, out at) && now - at <= OwnClickWindowMs && !searchWaited.Contains(o) && !(lastTarget.TryGetValue(o, out lt) && (lt == ClickTarget.UnknownBack || lt == ClickTarget.CityBack || lt == ClickTarget.EscapeBack || lt == ClickTarget.MapClose))) return Halt(o, "unknown", d + " after the pilot's own click");
        if (!modeUnknown && UnknownStall(o, s, now)) return GateResult.Deny("unknown", HaltDetail(o));
        return GateResult.Wait("unknown", d);
      }
      stall.Remove(o); searchWaited.Remove(o);
      return GateResult.Allow(s.Brief);
    }

    /// <summary>The verdict for a click on a FRESH state <paramref name="f"/> (never a held one).</summary>
    public GateResult Check(ClickRequest r, ScreenState f, long now) {
      string h;
      if (now > lastNow) lastNow = now;
      if (halted.TryGetValue(r.Owner, out h)) return GateResult.Deny("halted", h);
      if (!r.AutoOn) return GateResult.Deny("off", "auto click is off");
      if (!r.Front) return GateResult.Wait("background", "the game is not in front");
      Observe(f, now);
      if (halted.TryGetValue(r.Owner, out h)) return GateResult.Deny("halted", h);
      if (watches.ContainsKey(r.Owner)) return GateResult.Wait("pending", "the previous click has not shown its effect yet");
      if (f != null && (f.OfferCount > 0 || f.ModalOpen)) modalChecked.Add(r.Owner); else modalChecked.Remove(r.Owner);
      if (!IsWayOut(r.Target)) pilotStep[r.Owner] = r.Target;
      try { stepNow[r.Owner] = r.Target; return CheckStep(r, f, now); } finally { stepNow.Remove(r.Owner); }
    }

    GateResult CheckStep(ClickRequest r, ScreenState f, long now) {
      string tag = r.Target + " @" + (int)r.X + "," + (int)r.Y;
      long until;
      if (retryUntil.TryGetValue(r.Owner, out until) && now < until) return GateResult.Wait("retry", tag + ": a retry - the screen is read again first");
      // after a pop-up was closed the next one of the game's chain is waited for (only another pop-up's × may be pressed meanwhile)
      if (r.Target != ClickTarget.OfferClose && chainUntil.TryGetValue(r.Owner, out until) && now < until) return GateResult.Wait("chain", tag + ": a pop-up was closed " + (OfferChainMs - (until - now)) + " ms ago - the next one of the chain is waited for");
      var cover = CheckCover(r, f, now, tag); if (cover != null) return cover;
      if (r.Target == ClickTarget.OfferClose) return CheckOffer(r, f, now, tag);
      if (r.Target == ClickTarget.UnknownBack) return CheckUnknownBack(r, f, tag);
      if (r.Target == ClickTarget.CityBack) return CheckCityBack(r, f, tag);
      if (r.Target == ClickTarget.EscapeBack) return CheckEscape(r, f, tag, now);
      if (r.Target == ClickTarget.EquipTipEscape) return CheckTipEscape(r, f);
      if (r.Target == ClickTarget.MapClose) return CheckMapClose(r, f, now, tag);
      // any other click waits while an offer pop-up is on top (it would land on the pop-up)
      if (f != null && f.OfferCount > 0) return GateResult.Wait("offer", tag + ": " + f.ModalWhy + " is open");
      // the artifact cleanup never clicks, whatever the auto click setting says; nothing of the artifact tab is on any whitelist either
      // (with the site's artAuto switch and the auto click on - ArtSellAuto / ArtLocateAuto - the artifact flows click their Art* targets and nothing else)
      bool artT = IsArtTarget(r.Target);
      if (r.Owner == ClickOwner.Sell && (NoSellClicks || (artT && !ArtSellAuto) || (ArtSellAuto && !artT && !ArtLocateWay(r.Target)) || (f != null && f.Kind == ScreenKind.Inventory && f.InvArtTab && r.Target != ClickTarget.InvEquipTab && !artT))) return Halt(r.Owner, "artifact", tag + ": " + (NoSellClicks || !ArtSellAuto ? "the artifact cleanup is hints only" : "not a target of the artifact cleanup"));
      if (r.Owner == ClickOwner.Locate && (NoLocateClicks || (artT && !ArtLocateAuto) || (ArtLocateAuto && !artT && !ArtLocateWay(r.Target)))) return Halt(r.Owner, "artifact", tag + ": " + (NoLocateClicks || !ArtLocateAuto ? "the artifact guide is hints only" : "not a target of the artifact guide"));
      bool heroArt = IsHeroArtTarget(r.Target);
      if (artT && r.Owner == ClickOwner.Equip && !heroArt) return Halt(r.Owner, "target", tag + " is not a target Equip may click");
      if (heroArt && r.Owner == ClickOwner.Equip && !ArtEquipAuto) return Halt(r.Owner, "artifact", tag + ": the artifact steps are hints only");
      var rule = RuleOf(r.Owner, r.Target);
      if (rule == null) return Halt(r.Owner, "target", tag + " is not a target " + r.Owner + " may click");
      if (f == null || f.Kind == ScreenKind.Unknown) return UnknownWait(r, f, now, tag + ": the screen is not recognised" + (f != null && f.TopForm != "" ? " (top form " + f.TopForm + " not in the list)" : ""));
      // the window table shows the screen but its reader gave no reading of it: its details (tab, mode, selection) are not known - waited for as
      // an unrecognised screen (the forms are searched again), never judged from details it does not have
      if (f.Partial) return UnknownWait(r, f, now, tag + ": " + f.Brief + " - the window table shows it, its reader has no reading yet");
      if (Array.IndexOf(rule.Kinds, f.Kind) < 0) return Halt(r.Owner, "screen", tag + " needs " + string.Join("/", Names(rule.Kinds)) + ", the game shows " + f.Brief);
      if (f.ModalOpen && !rule.AllowModal) return Halt(r.Owner, "modal", tag + ": " + f.ModalWhy + " is open (" + f.Brief + ")");
      // a form shown with the screen since before the run (taken for a part of it: nobody can tell over or under) may lie on top
      if (r.Shown != null && r.U > 0)
        foreach (var n in r.Shown) if (ScreenMap.OnNever(n, r.X, r.Y, r.W, r.H, r.U)) return Halt(r.Owner, "target", tag + ": lies on a never-click button of " + n + ", shown with " + f.Brief);
      if (rule.HeroArt != 0) {
        var hs = f.HeroScr;
        if (hs == null || r.Hero <= 0 || r.ArtUid <= 0) return Halt(r.Owner, "target", tag + ": no hero / artifact named");
        if (f.Hero != r.Hero) return Halt(r.Owner, "screen", tag + ": the hero screen shows hero " + f.Hero + ", not " + r.Hero);
        if (!hs.ArtRead) return UnknownWait(r, f, now, tag + ": the artifact fields of the hero screen are not readable");
        if (hs.ArtTargetOwner == r.Hero) return Halt(r.Owner, "screen", tag + ": the artifact " + r.ArtUid + " is on the hero already");
        if (rule.HeroArt == 1) { if (hs.Tab == RFX.HeroArtTab) return Halt(r.Owner, "screen", tag + ": already on the artifact tab"); }
        else {
          if (hs.Tab != RFX.HeroArtTab) return Halt(r.Owner, "screen", tag + ": the hero screen is on tab " + hs.Tab + ", not the artifact tab");
          if (rule.HeroArt == 2 && hs.ArtListOpen) return Halt(r.Owner, "screen", tag + ": the artifact list is open already");
          if (rule.HeroArt >= 3 && !hs.ArtListOpen) return Halt(r.Owner, "screen", tag + ": the artifact list is not open");
          if (rule.HeroArt == 3 && hs.ArtSel == r.ArtUid) return GateResult.Wait("screen", tag + ": the artifact is selected already");
          if (rule.HeroArt >= 4 && hs.ArtSel != r.ArtUid) return Halt(r.Owner, "cell", tag + ": the card open is artifact " + hs.ArtSel + ", not " + r.ArtUid);
          if (rule.HeroArt == 5) {
            if (!f.ModalOpen || f.ModalWhy != ModalEquipPopup) return Halt(r.Owner, "modal", tag + ": the pop-up of the artifact is not open (" + f.Brief + ")");
            if (hs.ArtTargetOwner <= 0) return Halt(r.Owner, "cell", tag + ": the artifact is worn by nobody - no pop-up expected");
          } else if (f.ModalOpen) return Halt(r.Owner, "modal", tag + ": " + f.ModalWhy + " is open (" + f.Brief + ")");
        }
      }

      if (f.Kind == ScreenKind.Inventory) {
        bool needMode = rule.InvNormalEquip || rule.InvBulkEquip;
        if (needMode && !f.InvBulkKnown) return UnknownWait(r, f, now, tag + ": the inventory's mode is not readable");
        if (rule.InvNormalEquip) {
          if (f.InvBulk) return Halt(r.Owner, "bulk", tag + ": the inventory is in bulk-sale mode");
          if (!f.InvEquipTab) return Halt(r.Owner, "screen", tag + ": not on the equipment tab");
        }
        if (rule.InvBulkEquip) {
          if (!f.InvEquipTab || !f.InvBulk) return Halt(r.Owner, "screen", tag + ": not in bulk-sale mode on the equipment tab");
        }
        if (rule.InvOtherTab && f.InvEquipTab) return Halt(r.Owner, "screen", tag + ": already on the equipment tab");
      }
      if (f.Kind == ScreenKind.Inventory && rule.InvArt != 0) {
        if (rule.InvArt == 3) {
          if (f.InvArtTab) return Halt(r.Owner, "screen", tag + ": already on the artifact tab");
          if (f.InvEquipTab) {
            if (!f.InvBulkKnown) return UnknownWait(r, f, now, tag + ": the inventory's mode is not readable");
            if (f.InvBulk) return Halt(r.Owner, "bulk", tag + ": the equipment tab is in bulk-sale mode (leaving it is the player's)");
          }
        } else {
          if (!f.InvArtTab) return Halt(r.Owner, "screen", tag + ": not on the artifact tab");
          if (!f.InvArtBulkKnown) return UnknownWait(r, f, now, tag + ": the artifact tab's mode is not readable");
          if (rule.InvArt == 1 && f.InvArtMode != -1) return Halt(r.Owner, "bulk", tag + ": the artifact tab is not in its normal mode (mode " + f.InvArtMode + ")");
          if (rule.InvArt == 2 && !f.InvArtBulk) return Halt(r.Owner, "screen", tag + ": the artifact tab is not in bulk-sale mode");
        }
      }
      if (f.Kind == ScreenKind.ArtWorkshop) {
        if (rule.WsTab != -1 && f.ArtWsTab != rule.WsTab) return Halt(r.Owner, "screen", tag + ": the workshop is on tab " + f.ArtWsTab);
        if (rule.WsNotTab != -1 && f.ArtWsTab == rule.WsNotTab) return Halt(r.Owner, "screen", tag + ": the workshop is already on tab " + f.ArtWsTab);
      }
      if (rule.SelArt) {
        long sa = f.InvArtSelected;
        if (sa <= 0 || r.CellOk == null || !r.CellOk.Contains(sa)) return Halt(r.Owner, "cell", tag + ": the artifact card open is " + sa + ", not one of the items meant");
      }
      if (rule.WsUid) {
        long wu = f.ArtWsUid;
        if (wu <= 0 || r.CellOk == null || !r.CellOk.Contains(wu)) return Halt(r.Owner, "cell", tag + ": the workshop holds artifact " + wu + ", not one of the items meant");
      }
      if (rule.ArtTick) {
        // the cell under the click (the list order and the exact scroll) is the artifact meant: a wanted one that is not ticked, or a ticked one that is not wanted
        if (r.CellUid <= 0) return Halt(r.Owner, "cell", tag + ": the artifact under the click could not be worked out (the list's exact scroll is needed)");
        bool ticked = f.Inv != null && f.Inv.Selected.Contains(r.CellUid), wanted = r.CellOk != null && r.CellOk.Contains(r.CellUid);
        if (r.Untick ? !ticked || wanted : !wanted || ticked) return Halt(r.Owner, "cell", tag + ": the cell holds artifact " + r.CellUid + " (" + (ticked ? "ticked" : "not ticked") + (wanted ? ", wanted" : ", not wanted") + "): not a " + (r.Untick ? "wrong tick" : "tick to make"));
      }
      if (rule.MatCell) {
        var ws = f.ArtWs; int mi = r.MatIndex;
        if (ws == null || !ws.PanelKnown || mi < 1 || mi > ws.Mats.Count) return Halt(r.Owner, "cell", tag + ": no entry of the copy list under the click");
        if (ws.MatSel.Contains(mi)) return Halt(r.Owner, "cell", tag + ": entry " + mi + " is chosen already (a click would take it off)");
        long mu = ws.Mats[mi - 1];
        if (mu > 0 && mu == f.ArtWsUid) return Halt(r.Owner, "cell", tag + ": entry " + mi + " is the open artifact itself");
        if (mu > 0) { if (r.CellUid != mu || r.CellOk == null || !r.CellOk.Contains(mu)) return Halt(r.Owner, "cell", tag + ": entry " + mi + " holds copy " + mu + ", not a planned one"); }
        else {
          int mid = mi - 1 < ws.MatItemIds.Count ? ws.MatItemIds[mi - 1] : 0;
          if (mid <= 0 || r.ItemOk == null || !r.ItemOk.Contains(mid)) return Halt(r.Owner, "cell", tag + ": entry " + mi + " holds essence item " + mid + ", not the planned one");
        }
      }
      if (rule.FoundryClosed) {
        if (f.Rs != null && f.Rs.SublimingOpen) return Halt(r.Owner, "screen", tag + ": the Foundry is open (" + f.Brief + ")");
        // the window table shows the Foundry's form while its memory read does not have it yet (the slow laptop finds it seconds after it opens,
        // the pilot started with it open): it lies over the inventory, «Литейная» would hit its «Вознести» - the same wait
        if (r.FoundryUnread || HasForm(f.ShownKnown, FoundryForms)) {   // waited for while a search can find it (the host asks for one), then a stop
          long since;
          if (!foundryWait.TryGetValue(r.Owner, out since)) { since = now; foundryWait[r.Owner] = now; }
          string d = tag + ": the Foundry's form cannot be read - it may be open over the inventory (its «Вознести» lies there)";
          if (now - since > SearchCapMs) return Halt(r.Owner, "unknown", d + " for " + (now - since) + " ms");
          Note(r.Owner, "wait-foundry:" + since, "wait " + d);
          return GateResult.Wait("unknown", d);
        }
        foundryWait.Remove(r.Owner);
      }
      if (f.Kind == ScreenKind.HeroScreen && rule.HeroTab >= 0 && f.HeroTab != rule.HeroTab) return Halt(r.Owner, "screen", tag + ": the hero screen is on tab " + f.HeroTab);
      if (f.Kind == ScreenKind.Foundry) {
        if (rule.FoundryTab != -2 && f.FoundryTab != rule.FoundryTab) return Halt(r.Owner, "screen", tag + ": the Foundry is on " + f.FoundryTabName);
        if (rule.FoundryNotTab != -2 && f.FoundryTab == rule.FoundryNotTab) return Halt(r.Owner, "screen", tag + ": the Foundry is already on " + f.FoundryTabName);
      }
      if (rule.SelItem) {
        long sel = f.Kind == ScreenKind.Gem ? f.GemSel : f.InvSelected;
        if (sel <= 0 || r.CellOk == null || !r.CellOk.Contains(sel)) return Halt(r.Owner, "cell", tag + ": the screen holds item " + sel + ", not one of the items meant");
      }
      if (rule.GemFuncWanted) {
        if (r.Func != GemRows.Recast && r.Func != GemRows.AdvancedRecast) return Halt(r.Owner, "target", tag + ": " + r.Func + " is not a recast function");
        if (f.GemFunc == r.Func) return GateResult.Wait("screen", tag + ": the function is already selected");
      }
      if (rule.NeedScroll && r.NoScroll) return Halt(r.Owner, "scroll", tag + ": the list's exact scroll cannot be read for this game version, so no cell could be clicked safely (nothing was scrolled)");
      if (rule.Cell) {
        if (r.CellUid <= 0) return r.NoScroll ? Halt(r.Owner, "scroll", tag + ": the item under the click could not be worked out - the list's exact scroll cannot be read")
                                                : Halt(r.Owner, "cell", tag + ": the item under the click could not be worked out (the list's exact scroll is needed)");
        if (r.CellOk == null || !r.CellOk.Contains(r.CellUid)) return Halt(r.Owner, "cell", tag + ": the cell under the click holds item " + r.CellUid + ", not one of the items meant");
      }
      unknownSince.Remove(r.Owner);
      { ClickTarget fs; if (failStep.TryGetValue(r.Owner, out fs) && fs != r.Target) { failStep.Remove(r.Owner); failCount.Remove(r.Owner); } }   // (the pilot is on another step: the failures in a row are over)
      var res = GateResult.Allow(tag + " on " + f.Brief + (r.CellUid > 0 ? " cell " + r.CellUid : ""));
      Note(r.Owner, "allow:" + r.Target + ":" + f.Brief, "allow " + res.Detail);
      return res;
    }

    /// <summary>The live rect (client px: x, y, w, h) of a form's widget by its Lua field, for a window of W x H px and unit U (src/UiButtons.cs
    /// UiFieldReadNow); null = not read. The cover check takes it besides the UI graph's rect of a dangerous widget.</summary>
    public Func<string, string, double, double, double, double[]> DangerLive;
    /// <summary>The host reads the window table's order (ScreenInputs.Order: app/Overlay.cs, the game sim): a fresh state without it is a top not
    /// known - a pop-up / screen shown besides the target's form, or shown forms not read at all, mean no click. Off: the older hosts' rules
    /// (the forms shown since before the run count as the screen's part); the dangerous checks below run either way.</summary>
    public bool OrderRead;

    /// <summary>The form a click is meant for: the pop-up of an OfferClose / MapClose, the known screen's own form, else the form on top.</summary>
    static string TargetForm(ClickRequest r, ScreenState f) {
      if (r.Target == ClickTarget.OfferClose || r.Target == ClickTarget.MapClose) return r.Form ?? "";
      if (f.Kind != ScreenKind.Unknown) return ScreenState.OwnForm(f.Kind);
      return f.Top ?? "";
    }
    /// <summary>Every shown form the fresh state names when the window table's order is not known: the forms nobody knows (the request's), the
    /// unknown screen's / the pop-up's forms, the known screens shown, the offer and the flow forms.</summary>
    static List<string> ShownUnordered(ClickRequest r, ScreenState f) {
      var l = new List<string>();
      Action<string> add = list => { if (!string.IsNullOrEmpty(list)) foreach (var n in list.Split(',')) { string x = n.Trim(); if (x != "" && !l.Contains(x)) l.Add(x); } };
      if (r.Shown != null) foreach (var n in r.Shown) add(n);
      add(f.TopForm); add(f.ModalWhy != null && f.ModalWhy.StartsWith("form:") ? f.ModalWhy.Substring(5) : ""); add(f.ShownKnown); add(f.Offer);
      return l;
    }
    /// <summary>A dangerous widget of <paramref name="form"/> under the click point: the UI graph's rect, or (<paramref name="live"/>) its live rect.</summary>
    /// (<paramref name="own"/>: the click's own form - its close button, a request the game makes as the form closes, is left out: the × lists decide it.)
    UiGraph.Widget DangerAt(string form, ClickRequest r, bool live, bool own = false) {
      if (r.W <= 0 || r.H <= 0 || r.U <= 0) return null;
      var w = UiGraph.WidgetAt(form, r.X, r.Y, r.W, r.H, r.U, true); if (w != null && !(own && w.Closes)) return w;
      var lv = live ? DangerLive : null; if (lv == null) return null;
      foreach (var d in UiGraph.DangerWidgets(form)) {
        if (string.IsNullOrEmpty(d.Field)) continue;
        var rc = lv(form, d.Field, r.W, r.H, r.U);
        if (rc != null && r.X >= rc[0] && r.X <= rc[0] + rc[2] && r.Y >= rc[1] && r.Y <= rc[1] + rc[3]) return d;
      }
      return null;
    }

    /// <summary>Before ANY click with a point: every shown form is read, not only the one meant. With the window table's order (ScreenState.Order)
    /// the forms above the target's form are known: a dangerous dialog among them stops the owner, a dangerous widget of one of them under the
    /// point stops it, any other pop-up / screen there (or a form whose widget takes the point) is waited for - the click would land on it.
    /// Without the order every shown form other than the target's own, its flow forms, the overlays and the known screens under it may be on
    /// top: a dangerous one stops, a dangerous widget of one under the point stops, a pop-up / screen among them makes the top ambiguous - no
    /// click. Last, a dangerous widget of the target's own form under the point stops (the UI graph's rects). Null = nothing against it.</summary>
    GateResult CheckCover(ClickRequest r, ScreenState f, long now, string tag) {
      if (f == null || r.Target == ClickTarget.EscapeBack || r.Target == ClickTarget.EquipTipEscape || r.Target == ClickTarget.None) return null;
      var o = r.Owner;
      string tf = TargetForm(r, f); if (tf == "") return null;   // (an unknown screen with no form named: the target's own rules wait)
      var mine = new HashSet<string> { tf };
      foreach (var n in (f.Flow ?? "").Split(',')) if (n.Trim() != "") mine.Add(n.Trim());
      List<string> above = null; bool sure = false;
      if (f.Order != null) {
        var l = new List<string>();
        foreach (var n in f.Order) { if (mine.Contains(n)) { sure = true; break; } l.Add(n); }
        if (sure) above = l;
      }
      if (!sure) {
        if (OrderRead && r.Shown == null) return CoverWait(o, "order", tag + ": neither the window table's order nor its other shown forms are read - which form is on top is not known", now);
        above = ShownUnordered(r, f);
      }
      string where = sure ? "over " + tf : "shown with " + tf + " (the window table's order not known)";
      var cand = new List<string>();
      foreach (var n in above) {
        if (mine.Contains(n) || ScreenMap.IsFlow(f.Kind, n) || ScreenMap.ClassOf(n) == 'O') continue;
        var k = ScreenState.KindOfForm(n);
        if (!sure && k != ScreenKind.Unknown && (n == tf || ScreenState.Rank(k) <= ScreenState.Rank(f.Kind))) continue;   // (a known screen under it)
        cand.Add(n);
      }
      // first the dangerous ones: a dangerous dialog shown, a dangerous widget of a shown form under the point (the UI graph's rect, its live rect)
      foreach (var n in cand) if (ScreenMap.ClassOf(n) == 'D') return Halt(o, "dangerous", tag + ": the dangerous " + n + " is " + where + " - nothing is pressed");
      foreach (var n in cand) {
        if (ScreenState.KindOfForm(n) != ScreenKind.Unknown) continue;   // (an app screen: its reader reads it, the rules below wait for it - the Foundry's «Вознести» under FoundryButton: FoundryClosed)
        var dw = DangerAt(n, r, true);
        if (dw != null) return Halt(o, "target", tag + ": lies on a dangerous widget of " + n + " (" + dw.Path + "), " + where);
      }
      // then any form that would take the click (with the order), or that may lie on top (without it)
      foreach (var n in cand) {
        char c = ScreenMap.ClassOf(n); var k = ScreenState.KindOfForm(n);
        if (sure) {
          if (c != 'U' || k != ScreenKind.Unknown || OfferForms.IsOffer(n) || UiGraph.WidgetAt(n, r.X, r.Y, r.W, r.H, r.U, false) != null)
            return CoverWait(o, "covered", tag + ": " + n + " lies " + where, now);
        } else if (OrderRead && ("FPEW".IndexOf(c) >= 0 || k != ScreenKind.Unknown || OfferForms.IsOffer(n)))
          return CoverWait(o, "order", tag + ": " + n + " is " + where + " - which one is on top is not known", now);
      }
      coverSince.Remove(o);
      var own = DangerAt(tf, r, false, true);
      if (own != null) return Halt(o, "target", tag + ": lies on a dangerous widget of " + tf + " (" + own.Path + ")");
      return null;
    }
    /// <summary>How long a click may wait for a top form that stays covered / not known before the owner stops (a hint instead of a click).</summary>
    public const int CoverWaitMs = 15000;
    readonly Dictionary<ClickOwner, long> coverSince = new Dictionary<ClickOwner, long>(), coverLast = new Dictionary<ClickOwner, long>();
    GateResult CoverWait(ClickOwner o, string reason, string detail, long now) {
      long at, last;   // (a wait more than 3 s after the last one starts the time again: the pilot did other things meanwhile)
      if (!coverSince.TryGetValue(o, out at) || !coverLast.TryGetValue(o, out last) || now - last > 3000) { coverSince[o] = now; at = now; }
      coverLast[o] = now;
      if (now - at >= CoverWaitMs) { coverSince.Remove(o); return Halt(o, reason, detail + " (" + (now - at) / 1000 + " s)"); }
      return GateResult.Wait(reason, detail);
    }

    /// <summary>OfferClose: the one pop-up shown is the one meant, on the list, over the city (or nothing known); the point is on its × and on
    /// none of its other buttons; not more than MaxOfferCloses in a row.</summary>
    GateResult CheckOffer(ClickRequest r, ScreenState f, long now, string tag) {
      var o = r.Owner;
      // a hint run never clicks (the artifact cleanup / guide without the site's switch): no click, and that is not a stop
      if ((o == ClickOwner.Sell && NoSellClicks) || (o == ClickOwner.Locate && NoLocateClicks)) return GateResult.Deny("off", tag + ": a hint run clicks nothing");
      if (f == null) return UnknownWait(r, f, now, tag + ": the screen is not read");
      if (!f.OfferRead || f.OfferCount == 0) return GateResult.Wait("screen", tag + ": no offer pop-up is shown (" + f.Brief + ")");   // gone already (the player closed it): nothing to do
      string form = f.Offer;
      if (f.OfferCount > 1 && !f.OfferTopKnown) return Halt(o, "modal", tag + ": " + f.OfferCount + " offer pop-ups are shown at once (" + form + " ...): which one is on top is not known");
      if (r.Form != form) return Halt(o, "cell", tag + ": the pop-up shown is " + form + ", not " + (r.Form ?? "-"));
      if (!OfferForms.IsOffer(form)) return Halt(o, "offer", tag + ": " + form + " is not on the offer list");
      if (f.Kind != ScreenKind.City && f.Kind != ScreenKind.Unknown) return Halt(o, "screen", tag + ": the pop-up is over " + f.Brief + ", not the city");
      if (OfferForms.CloseRect(r.Geo, form, r.W, r.H, r.U) == null) return Halt(o, "offer", tag + ": the close button of " + form + " is not known - not clicked");
      if (!r.Live && !OfferForms.OnClose(r.Geo, form, r.X, r.Y, r.W, r.H, r.U)) return Halt(o, "target", tag + ": not on the close button of " + form);   // (a live point is the × itself)
      if (OfferForms.OnNeverButton(form, r.X, r.Y, r.W, r.H, r.U)) return Halt(o, "target", tag + ": lies on another button of " + form + " (buy / go / claim are never pressed)");
      int n = OfferRun(o);
      if (n >= MaxOfferCloses) return Halt(o, "offers", tag + ": " + n + " offer pop-ups closed in a row, " + form + " again - stopped (a loop?)");
      unknownSince.Remove(o);
      var res = GateResult.Allow(tag + " on " + f.Brief);
      Note(o, "allow:" + r.Target + ":" + f.Brief, "allow " + res.Detail);
      return res;
    }

    /// <summary>UnknownBack: the fresh screen allows it (UnknownBackBlock), presses are left, the point is on the back arrow. A screen that does not
    /// allow it is waited for (the unknown-screen stop still runs); MaxUnknownBacks reached stops the owner.</summary>
    GateResult CheckUnknownBack(ClickRequest r, ScreenState f, string tag) {
      var o = r.Owner;
      if ((o == ClickOwner.Sell && NoSellClicks) || (o == ClickOwner.Locate && NoLocateClicks)) return GateResult.Deny("off", tag + ": a hint run clicks nothing");
      string block = UnknownBackBlock(f);
      if (block != "") { Note(o, "wait-back:" + block, "wait " + tag + ": " + block); return GateResult.Wait("screen", tag + ": " + block); }
      int n = UnknownBacks(o);
      if (n >= MaxUnknownBacks) return Halt(o, "unknown", "unknown screen " + (f.TopForm != "" ? f.TopForm : "?") + ": the back arrow was pressed " + n + " times already");
      if (!OnBackArrow(r.X, r.Y, r.U) && !(f.TopClass == 'F' && r.W > 0 && ScreenMap.OnExit(f.Top, r.X, r.Y, r.W, r.H, r.U))) return Halt(o, "target", tag + ": not on the back arrow");
      var res = GateResult.Allow(tag + " on " + f.Brief + " (press " + (n + 1) + " of " + MaxUnknownBacks + ")");
      Note(o, "allow:" + r.Target + ":" + f.Brief + ":" + n, "allow " + res.Detail);
      return res;
    }

    /// <summary>CityBack: the fresh screen allows it (CityBackBlock), presses are left, the point is on the back arrow. A screen that does not allow
    /// it is waited for; MaxCityBacks reached stops the owner.</summary>
    GateResult CheckCityBack(ClickRequest r, ScreenState f, string tag) {
      var o = r.Owner;
      if (HintOnly(o)) return GateResult.Deny("off", tag + ": a hint run clicks nothing");
      string block = CityBackBlock(f);
      if (block != "") { Note(o, "wait-city:" + block, "wait " + tag + ": " + block); return GateResult.Wait("screen", tag + ": " + block); }
      int n = CityBacks(o);
      if (n >= MaxCityBacks) return Halt(o, "nochange", "back to the city: the back arrow was pressed " + n + " times, the game shows " + f.Brief);
      if (!OnBackArrow(r.X, r.Y, r.U)) return Halt(o, "target", tag + ": not on the back arrow");
      var res = GateResult.Allow(tag + " on " + f.Brief + " (back to the city, press " + (n + 1) + " of " + MaxCityBacks + ")");
      Note(o, "allow:" + r.Target + ":" + f.Brief + ":" + n, "allow " + res.Detail);
      return res;
    }

    /// <summary>The pilot's own screen (ScreenMap.PilotScreen) the window table shows while the fresh read is a screen it lies over (the Foundry
    /// over the inventory) or no screen while its form search runs: its form's name, "" = none.</summary>
    string OwnScreenUnread(ClickOwner o, ScreenState f) {
      if (f == null || f.Held) return "";
      foreach (var k in new[] { ScreenKind.Foundry, ScreenKind.Inventory, ScreenKind.HeroScreen }) {
        if (f.Kind == k || !ScreenMap.PilotScreen(o, k)) continue;
        string form = ScreenState.OwnForm(k);
        if (!HasForm(f.ShownKnown, new[] { form })) continue;
        if (k == ScreenKind.Foundry && f.Kind == ScreenKind.Inventory) return form;
        if (f.Kind == ScreenKind.Unknown && k != ScreenKind.HeroScreen && SearchBusy(k == ScreenKind.Foundry ? ClickTarget.FoundryButton : ClickTarget.CityInventory)) return form;
      }
      return "";
    }

    /// <summary>EscapeBack: only after a back press that changed nothing, on a fresh screen EscapeBlock allows (never the city, a fight, a pop-up,
    /// the exit dialog), at most MaxEscapes a run.</summary>
    GateResult CheckEscape(ClickRequest r, ScreenState f, string tag, long now) {
      var o = r.Owner;
      if (HintOnly(o)) return GateResult.Deny("off", tag + ": a hint run clicks nothing");
      tag = "EscapeBack (key)";
      // never on the screen the pilot works on (the hero screen of the equip helper ...): a pop-up there is the pilot's flow or a stop
      PilotFlow fl;
      if (f != null && !f.Held && (ScreenMap.PilotScreen(o, f.Kind) || (Flows.TryGetValue(o, out fl) && f.Kind == CityFirst.WorkScreen(fl)))) { escapeDue.Remove(o); return Halt(o, "dangerous", tag + ": never on the pilot's own screen " + f.Brief + " - nothing is pressed"); }
      // the window table shows the pilot's own screen while the memory read does not have it yet (the slow laptop finds the Foundry / the
      // inventory seconds after it opens): the pop-up may lie over that screen - no Escape until it is read (then the rule above stops)
      string own = OwnScreenUnread(o, f);
      if (own != "") {
        long since; if (!ownUnreadWait.TryGetValue(o, out since)) { since = now; ownUnreadWait[o] = since; }
        string d = tag + ": the pilot's own screen " + own + " is shown but not read yet (" + f.Brief + ") - the pop-up may lie over it";
        if (now - since > SearchCapMs) { escapeDue.Remove(o); return Halt(o, "unknown", d + " for " + (now - since) + " ms"); }
        Note(o, "wait-own:" + since, "wait " + d); return GateResult.Wait("unknown", d);
      }
      ownUnreadWait.Remove(o);
      if (!escapeDue.Contains(o)) return Halt(o, "target", tag + ": no back press before it changed nothing");
      string block = EscapeBlock(f);
      if (block != "") {
        if (f != null && f.Kind == ScreenKind.City) escapeDue.Remove(o);   // the city came: nothing to close
        Note(o, "wait-esc:" + block, "wait " + tag + ": " + block); return GateResult.Wait("screen", tag + ": " + block);
      }
      int n = Escapes(o);
      if (n >= MaxEscapes) return Halt(o, "nochange", tag + ": Escape was pressed " + n + " times already, the game shows " + f.Brief);
      var res = GateResult.Allow(tag + " on " + f.Brief + " (Escape " + (n + 1) + " of " + MaxEscapes + ")");
      Note(o, "allow:" + r.Target + ":" + f.Brief + ":" + n, "allow " + res.Detail);
      return res;
    }

    /// <summary>The shown flow forms (and the pop-up read, if any) are only the item's tip / compare.</summary>
    public static bool OnlyTip(ScreenState s) {
      if (s == null || string.IsNullOrEmpty(s.Flow)) return false;
      foreach (var n in s.Flow.Split(',')) if (n != "" && Array.IndexOf(TipForms, n) < 0) return false;
      return !s.ModalOpen || Array.IndexOf(TipForms, s.ModalWhy ?? "") >= 0;
    }

    /// <summary>EquipTipEscape: the next step needs the hero's slots and the item's tip (a flow form of the equip helper) lies over them. The
    /// FlowForms rule keeps Escape off a flow form only while it is the step's target: here it is in the way. Only the equip helper, only on its
    /// hero screen read fresh, nothing but the tip / compare shown over it (no pop-up, offer, exit dialog), at most MaxTipEscapes a run.</summary>
    GateResult CheckTipEscape(ClickRequest r, ScreenState f) {
      var o = r.Owner; string tag = "EquipTipEscape (key)";
      if (o != ClickOwner.Equip) return Halt(o, "target", tag + " is not a target " + o + " may press");
      if (f == null || f.Held || f.Kind != ScreenKind.HeroScreen) return GateResult.Wait("screen", tag + ": needs the hero screen read fresh (" + (f != null ? f.Brief : "no screen") + ")");
      if (f.OfferCount > 0 || ExitDialogShown(f) || f.ShownKnown == null || !OnlyTip(f)) return GateResult.Wait("screen", tag + ": not only the item's tip is shown (" + f.Brief + ", flow " + f.Flow + ")");
      int n; tipEscapes.TryGetValue(o, out n);
      if (n >= MaxTipEscapes) return Halt(o, "nochange", tag + ": Escape was pressed " + n + " times for the item's tip, it is still shown over the slots");
      var res = GateResult.Allow(tag + " on " + f.Brief + " (" + (n + 1) + " of " + MaxTipEscapes + ")");
      Note(o, "allow:tipesc:" + n, "allow " + res.Detail);
      return res;
    }

    /// <summary>MapClose: the fresh screen's top form is still the pop-up the map closes with its × (MapExit Close), the point is on its × and on
    /// none of its other buttons, MaxMapMoves not reached.</summary>
    GateResult CheckMapClose(ClickRequest r, ScreenState f, long now, string tag) {
      var o = r.Owner;
      if (HintOnly(o)) return GateResult.Deny("off", tag + ": a hint run clicks nothing");
      if (f == null) return UnknownWait(r, f, now, tag + ": the screen is not read");
      var d = MapExit(f, r.W, r.H, r.U);
      string form = r.Form ?? "";
      if (d.Move != MapMove.Close || d.Form != form) {
        if (d.Form != form) return GateResult.Wait("screen", tag + ": " + form + " is no longer on top (" + f.Brief + ", " + d + ")");
        return Halt(o, "target", tag + ": the screen map has no × to press on " + form + " (" + d + ")");
      }
      if (!ScreenMap.OnExit(form, r.X, r.Y, r.W, r.H, r.U)) return Halt(o, "target", tag + ": not on the close button of " + form);
      if (ScreenMap.OnNever(form, r.X, r.Y, r.W, r.H, r.U)) return Halt(o, "target", tag + ": lies on another button of " + form + " (never pressed)");
      // without the window table's order another pop-up shown with it may lie on top of this one, so the point must be off its never-click buttons too
      foreach (var other in ShownForms(f))
        if (other != form && ScreenMap.OnNever(other, r.X, r.Y, r.W, r.H, r.U)) return Halt(o, "target", tag + ": lies on a never-click button of " + other + ", shown with " + form);
      int n = MapMoves(o);
      if (n >= MaxMapMoves) return Halt(o, "nochange", tag + ": " + n + " of the screen map's exits made already, " + form + " again (a loop?)");
      var res = GateResult.Allow(tag + " on " + f.Brief + " (" + form + ")");
      Note(o, "allow:" + r.Target + ":" + f.Brief + ":" + n, "allow " + res.Detail);
      return res;
    }

    GateResult UnknownWait(ClickRequest r, ScreenState f, long now, string detail) {
      long since;
      if (SearchWait && Unrecognised(f)) { unknownSince.Remove(r.Owner); searchWaited.Add(r.Owner); return GateResult.Wait("search", detail + " (a form search runs)"); }
      if (!unknownSince.TryGetValue(r.Owner, out since)) { since = now; unknownSince[r.Owner] = now; }
      if (now - since > UnknownGraceMs) return Halt(r.Owner, "unknown", detail + " for " + (now - since) + " ms");
      Note(r.Owner, "wait-unknown:" + r.Target + ":" + (f == null ? "none" : f.Brief), "wait (unknown) " + r.Target + " on " + (f == null ? "no screen" : f.Brief));
      return GateResult.Wait("unknown", detail);
    }

    static string[] Names(ScreenKind[] ks) { var a = new string[ks.Length]; for (int i = 0; i < ks.Length; i++) a[i] = ks[i].ToString(); return a; }

    // ---- (d) which item is under a click point

    /// <summary>The 0-based index of the card under client point (x, y) in a list the game scrolls (the artifact tab's list, the workshop's copy list), placed as
    /// ArtFrames.Cell places a card from the list's exact scroll; -1 = no card there / the list cannot be placed. The caller looks the item up in the list's order.</summary>
    public static int ListCardIndexAt(GameGeometry g, string rectKey, RFX.ListRead l, int fallbackCols, double fallbackLine, double x, double y, double W, double H, double U) {
      if (l == null || !l.Has) return -1;
      var view = ArtFrames.Rect(g, rectKey, W, H, U);
      if (view == null || view.W <= 0 || view.H <= 0) return -1;
      var grid = g != null ? g.Grid(rectKey) : null;
      int cols = l.Cols >= 1 ? l.Cols : grid != null ? grid.Cols : fallbackCols;
      double line = l.RowUnits > 0 ? l.RowUnits : grid != null && grid.LineSize > 0 ? grid.LineSize : fallbackLine;
      double padTop = grid != null ? grid.PadTop : 0;
      double k = U / GameGeometry.CanvasH, pitch = line * k, cardW = 80 * k, cardH = 110 * k;
      if (cols < 1 || pitch <= 0) return -1;
      if (x < view.X || x > view.X + view.W || y < view.Y || y > view.Y + view.H) return -1;   // outside the list's view
      double top0 = view.Y + FoundryPlace.PadSign * padTop * k - l.ScrollY * k;
      double dy = y - top0;
      if (dy < 0) return -1;
      int row = (int)Math.Floor(dy / pitch);
      double slotW = view.W / cols;
      int col = (int)Math.Floor((x - view.X) / slotW);
      if (col < 0 || col >= cols) return -1;
      double cx = view.X + col * slotW + (slotW - cardW) / 2, cy = top0 + row * pitch + (pitch - cardH) / 2;
      if (x < cx || x > cx + cardW || y < cy || y > cy + cardH) return -1;
      return row * cols + col;
    }

    /// <summary>The click point (client px) lies inside a button the artifact flows never press (NeverKeys: «Продвинуть», «Улучшить»), by the site's geometry or the research numbers.</summary>
    public static bool OnNeverButton(GameGeometry g, double x, double y, double W, double H, double U) {
      foreach (var key in NeverKeys) {
        var rc = ArtFrames.Rect(g, key, W, H, U);
        if (rc != null && x >= rc.X && x <= rc.X + rc.W && y >= rc.Y && y <= rc.Y + rc.H) return true;
      }
      return false;
    }

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
