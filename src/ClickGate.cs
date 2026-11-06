// Wardsage - what the hint frames need to know about the game's screens: the widgets a frame can point at (ClickTarget names them), the "city first"
// rule (which screens each guide flow goes on from), the one hint-mode fallback (back to the city), the city bar's place, live-rect stillness.
// The program does not press anything in the game: there is no click gate any more, only these read-only rules. Pure logic, no Windows APIs.
using System;
using System.Collections.Generic;

namespace RealmForge {
  /// <summary>Whose guide a hint belongs to (the screen map and the hint fallback key their rules by it).</summary>
  public enum ClickOwner { Locate, Sell, Equip, LocateFilter }

  /// <summary>The widgets the hint frames point at (names only: nothing is pressed by the program).</summary>
  public enum ClickTarget {
    None,
    HeroBack,         // the hero screen's back arrow
    CityInventory,    // the city's «Инвентарь»
    InvEquipTab,      // the inventory's left menu «Снаряжение»
    InvBulkEnter,     // «Массовая продажа» (the storage cleanup only)
    InvListCell,      // an item's cell in the inventory list, normal mode (selects it; «Показать в игре»)
    InvBulkCell,      // an item's cell in the inventory list, bulk-sale mode (toggles its choice; the storage cleanup only)
    InvListDrag,      // a drag that scrolls the inventory list
    FoundryButton,    // «Литейная» of the inventory's equipment tab
    HeroFoundry,      // «Литейная» of the hero screen's gear tab (the round button bottom right; only while the game's memory shows it, nothing over it)
    AscendTab,        // the Foundry's «Вознесение» tab
    SlotTab,          // the Ascension list's slot tab
    AscendCell,       // an item's cell in the Ascension list (selects it)
    AscendDrag,       // a drag that scrolls the Ascension list
    InvCardGem,       // the selected item's card, its «Улучшение самоцветами» button (normal mode; opens the gem screen; «Показать в игре» recast)
    GemFuncRow,       // the gem screen's function list: the row of the wanted recast function (selects it only; «Показать в игре» recast)
    InvMaterialsTab,  // the inventory's left menu «Материалы» (owner Sell only; the test build's lab setup, src/LabRec.cs LabPrep: puts the game on that tab, nothing else)
    // the inventory's filter for a long search (owner LocateFilter only; src/LocatePilot.cs InvFilterPilot): a button / toggle of the equipment panel's filter (the key in ClickRequest.Form),
    // a set of its pop-up, a drag that scrolls the pop-up's list, a click outside that closes the pop-up / the panel (the filter stays). Never «Продать» or any button of the card
    InvFilterBtn, InvFilterCell, InvFilterDrag, InvFilterClose,
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
    ArtHideWorn,      // the artifact list's own filter «Скрыть надетое» (on before a long scroll to a free artifact; off when it hides the artifact). Untick = off
    // a tab of it, its way back (the screen map's back arrow / ×, an offer's ×, the graph's close widget), one Escape on a form the map closes so
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

  /// <summary>How the hint leaves a screen no flow can continue from: its back arrow, a pop-up's close button, or the Escape key (text only).</summary>
  public enum HintLeave { None, Back, Close, Esc }

  /// <summary>What an open gear filter pop-up means for the frame's hint when the plan has no use of it (live 2026-11-06 1.6.68.600: the pop-up
  /// stood in the middle, the item was in the list on the left, the slots switch in the pop-up - «close the filter» was wrong). Pure (tests/CoreTests.cs).</summary>
  public enum GearFilterMove {
    /// <summary>The player works with a slot: the pop-up's own «Выбрать часть» row switches it - frame that icon.</summary>
    SlotInFilter,
    /// <summary>The pop-up covers the item's cell: the tip points at its red ×.</summary>
    Close,
    /// <summary>The item is beside the pop-up: the frame goes on in the list, the pop-up is left alone.</summary>
    ListBeside,
    /// <summary>The pop-up still holds a set / main stat / sub stat of the previous item (it hides this one): the tip lights what to pick.</summary>
    FixFilter
  }

  public static class GearFilterHint {
    /// <param name="filterFits">the pop-up's choice suits the step's item (or the pop-up has no choice): see <see cref="Fits"/></param>
    public static GearFilterMove Decide(string hintKind, int hintSlot, int slotIcons, bool coveredByFilter, bool filterFits = true) {
      if (hintKind == "slot" && hintSlot >= 0 && hintSlot < slotIcons) return filterFits ? GearFilterMove.SlotInFilter : GearFilterMove.FixFilter;
      return coveredByFilter ? GearFilterMove.Close : GearFilterMove.ListBeside;
    }

    /// <summary>The filter's choice lets the item through: no set chosen or the item's set among them (unknown set = no verdict), the same for the main
    /// stat (statId 0 = the slot has one main stat or it is unknown), and every chosen sub stat is one of the item's (itemSubs null = unknown).</summary>
    public static bool Fits(long[] suits, long[] mains, long[] subs, long setId, long statId, long[] itemSubs) {
      if (suits != null && suits.Length > 0 && setId > 0 && Array.IndexOf(suits, setId) < 0) return false;
      if (mains != null && mains.Length > 0 && statId > 0 && Array.IndexOf(mains, statId) < 0) return false;
      if (subs != null && subs.Length > 0 && itemSubs != null) foreach (var x in subs) if (Array.IndexOf(itemSubs, x) < 0) return false;
      return true;
    }
  }

  /// <summary>The fallback hint's plan: what to show (Kind) and the frame's rect in client px (Back / Close; null = the standard back spot).</summary>
  public sealed class HintFallbackPlan {
    public HintLeave Kind; public GeoRect Rect; public string Why = "";
    /// <summary>The frame target's key when the rect is that target's layout (the host reads its live rect through FrameFor, so the frame is reported to the audit): "" = none.</summary>
    public string Key = "";
    /// <summary>What the plate says: "" = «Вернитесь в город», "closeFilter" = the gear filter pop-up is open (the game's flag): «Закройте фильтр» on its red × (a way back to the city is not the first thing).</summary>
    public string Text = "";
  }

  /// <summary>The ONE hint-mode fallback for every flow (equip, find item / locate, artifact, storage cleanup; owner rule 2026-11-01): when the app cannot
  /// recognise the current screen (Unknown), or it is a known screen the flow does not go on from (CityFirst), or a pop-up nobody knows is over it, the
  /// hint is «Вернитесь в город» with a frame on the way out: the pop-up's close button when one is open (it covers the screen), else the screen's back
  /// arrow (a known screen: its own, read live by the host; an unknown one: the screen map's, or one read live), else the text alone «Нажмите Esc» (no
  /// frame). Never a step label for a step the screen cannot do. Pure (tests/CoreTests.cs); the host draws it (app/Overlay.cs TickHintFallback). Auto
  /// mode is not this: its pilots go back by themselves (UnknownBackStep / BackStep).</summary>
  public static class HintFallback {
    /// <summary>The screen has to stay as it is this long before the fallback shows (the form is being searched for, a screen is coming in).</summary>
    public const int SettleMs = 700;

    /// <param name="heroFilterPic">the host sees the hero grid's filter pop-up on the picture (the game's own flag may not be read: the pilot uses the picture too)</param>
    /// <param name="backLive">the screen's back arrow was read live by the host (only needed on an unknown screen the map has no exit for)</param>
    public static HintFallbackPlan Decide(PilotFlow f, ScreenState s, LocateKind step, bool backLive, double W, double H, double U, bool heroFilterPic = false) {
      var p = new HintFallbackPlan();
      if (s == null || s.Held || s.Kind == ScreenKind.City) return p;
      bool unk = s.Kind == ScreenKind.Unknown;
      if (unk && !string.IsNullOrEmpty(s.Flow)) return p;   // a pilot's flow form is shown: the screen under it is only not read
      bool locate = f == PilotFlow.LocateAscend || f == PilotFlow.LocateRecast || f == PilotFlow.LocateArtPromote;
      if (locate && (step == LocateKind.NoItem || step == LocateKind.OtherAccount)) return p;   // nothing to drive: it never goes back
      // an open pop-up always wins over the flow's step: the hero grid's filter pop-up (its own screen, the flows go on from it) is closed with the funnel
      if (s.Kind == ScreenKind.HeroScreen && ((s.ModalOpen && s.ModalWhy == "heroFilter") || heroFilterPic)) { p.Kind = HintLeave.Close; p.Rect = new HeroGeometry().GridBarRect(W, H); p.Key = "hero.filter"; p.Why = "the hero grid's filter pop-up: its filter bar (the funnel)"; return p; }
      var md = ClickGate.MapExit(s, W, H, U);
      if (md.Move == MapMove.Wait || md.Move == MapMove.Stop) return p;   // a fight / loading / a dangerous form: not the hint's to judge
      bool form = s.ModalWhy != null && s.ModalWhy.StartsWith("form:") && s.OfferCount == 0;
      bool away = !unk && !CityFirst.Continues(f, s, step);
      bool stray = !unk && form && md.Move == MapMove.None;   // a pop-up over the screen that the map does not know
      if (!unk && !away && !stray) return p;
      // the gear filter pop-up (the game's flag m_bShow = yes) covers the hero screen and the flow cannot go on from it: the hint says to close it, with its red × framed (live 10:5x scenario 10: «Вернитесь в город» over an open filter)
      if (s.Kind == ScreenKind.HeroScreen && s.GearOpenMem == Mem3.Yes && f != PilotFlow.Equip && f != PilotFlow.ArtEquip) {
        var fg = new FilterGeometry(); double d = 0.06 * U, cx = fg.X(fg.MainCloseX, W, H), cy = fg.Y(fg.CloseY, W, H);
        p.Kind = HintLeave.Close; p.Rect = new GeoRect { X = cx - d / 2, Y = cy - d / 2, W = d, H = d }; p.Text = "closeFilter"; p.Why = "the gear filter pop-up is open (memory): its red ×"; return p;
      }
      bool pop = s.ModalOpen || s.OfferCount > 0;
      if (md.Move == MapMove.Close && md.Point != null) { var r = ScreenMap.ExitRect(md.Form, W, H, U); if (r != null) { p.Kind = HintLeave.Close; p.Rect = r; p.Why = "pop-up " + md.Form; return p; } }
      if (md.Move == MapMove.Escape) { p.Kind = HintLeave.Esc; p.Why = "pop-up " + md.Form + " closes with the back key"; return p; }
      if (md.Move == MapMove.Back && md.Point != null) { var r = ScreenMap.ExitRect(md.Form, W, H, U); if (r != null) { p.Kind = HintLeave.Back; p.Rect = r; p.Why = "back arrow of " + md.Form; return p; } }
      if (pop) { p.Kind = HintLeave.Esc; p.Why = "pop-up with no known close button"; return p; }
      if (!unk || backLive) { p.Kind = HintLeave.Back; p.Rect = BackRect(U); p.Why = unk ? "back arrow read live" : "back arrow of " + s.Kind; return p; }
      p.Kind = HintLeave.Esc; p.Why = "unknown screen, no back arrow read"; return p;
    }

    /// <summary>The standard back arrow's frame (HeroBack's spot, the same one the pilots press), client px.</summary>
    public static GeoRect BackRect(double U) {
      var g = new HeroGeometry(); double t = ClickGate.UnknownBackTol * U;
      return new GeoRect { X = g.BackX * U - t, Y = g.BackY * U - t, W = 2 * t, H = 2 * t };
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


  /// <summary>How a pilot leaves the form on top, by the screen map (ClickGate.MapExit).</summary>
  public enum MapMove { None, Wait, Stop, Back, Close, Escape }
  public sealed class MapDecision {
    public MapMove Move; public string Form = ""; public char Class = 'U'; public GeoRect Point; public string Why = "";
    public override string ToString() { return Move + (Form != "" ? " " + Form + ":" + ScreenMap.ClassName(Class) : "") + (Why != "" ? " (" + Why + ")" : ""); }
  }

  public static class ClickGate {
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
    /// <summary>A form search may take this long on a slow PC; the screen read waits for it.</summary>
    public const int SearchCapMs = 30000;
    /// <summary>How far (in units of the client height) a point may lie from the back arrow's centre to count as it.</summary>
    public const double UnknownBackTol = 0.03;
    /// <summary>ScreenState.ModalWhy of Form_EquipPopUp (the "worn by another hero" question), seen on the picture.</summary>
    public const string ModalEquipPopup = "equipPopup";
    /// <summary>The item card / compare forms of the hero screen.</summary>
    public static readonly string[] TipForms = { "Form_CharactorEquipTip", "Form_EquipedCompared" };
    /// <summary>The shown flow forms (and the pop-up read, if any) are only the item's tip / compare.</summary>
    public static bool OnlyTip(ScreenState s) {
      if (s == null || string.IsNullOrEmpty(s.Flow)) return false;
      foreach (var n in s.Flow.Split(',')) if (n != "" && Array.IndexOf(TipForms, n) < 0) return false;
      return !s.ModalOpen || Array.IndexOf(TipForms, s.ModalWhy ?? "") >= 0;
    }
  }
}
