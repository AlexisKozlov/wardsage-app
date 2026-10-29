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
    EquipConfirm,     // equip helper: «Заменить» (only with the player's auto confirm)
    OfferClose,       // every owner: the × of the game's offer / notice pop-up on top of the city (src/OfferForms.cs; never its buy / go / claim buttons)
    UnknownBack,      // every owner: the game's back arrow top left (HeroBack's spot) on a full-screen screen nobody knows, no pop-up on it (at most MaxUnknownBacks a run)
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
    ArtTakeOverConfirm // the pop-up «<artifact> в ячейке героя: <имя>» (the artifact is worn by another hero): its «Заменить» (the player's auto confirm too)
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
    /// <summary>A pilot that sees the same unrecognised screen (no offer pop-up on it, not held, not a fight's result) this long stops with
    /// "unknown screen &lt;form&gt;" instead of waiting for ever (UnknownStall).</summary>
    public const int UnknownStallMs = 20000;
    /// <summary>Calls of UnknownStall further apart than this (the game was in the background, the player was busy) start the count again.</summary>
    public const int StallGapMs = 2000;
    /// <summary>The unknown-screen back press (UnknownBack): nearly every full-screen form of the game has its back arrow at the hero screen's
    /// spot top left. On a screen nobody knows (no pop-up or modal detected, no known screen's form shown, not the city, not a fight) that has
    /// lasted UnknownBackSettleMs, the pilot presses it - at most MaxUnknownBacks times a run, each press has to change the screen (else
    /// "nochange"), and none once the screen is a known one.</summary>
    public const int MaxUnknownBacks = 3, UnknownBackSettleMs = 3000, UnknownBackRetryMs = 1000;
    /// <summary>How far (in units of the client height) the press may lie from the back arrow's centre.</summary>
    public const double UnknownBackTol = 0.03;
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

    sealed class Watch { public Func<ScreenState, bool> Done; public long Deadline; public string What; public string Offer; public ClickTarget Target; public long ArmedAt; public int Search; }   // Search: 0 none, 1 extended while a search runs, 2 its end awaited

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
    readonly Dictionary<ClickOwner, int> unknownBacks = new Dictionary<ClickOwner, int>();    // UnknownBack presses in this run
    readonly Dictionary<ClickOwner, long> backTried = new Dictionary<ClickOwner, long>();     // the last UnknownBackDue yes
    readonly Dictionary<ClickOwner, ClickTarget> lastTarget = new Dictionary<ClickOwner, ClickTarget>();   // the owner's last click

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

    /// <summary>A new request (or the player started it again): the owner may click again.</summary>
    public void Resume(ClickOwner o) {
      halted.Remove(o); watches.Remove(o); unknownSince.Remove(o); noted.Remove(o); clickedAt.Remove(o); offerRun.Remove(o); haltDetail.Remove(o); foundryWait.Remove(o);
      stall.Remove(o); stallBrief.Remove(o); searchWaited.Remove(o);
      unknownBacks.Remove(o); backTried.Remove(o); lastTarget.Remove(o);
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
      if (!s.OfferRead) return "the window table is not read";
      if (Exempt(s.TopForm)) return "a fight's result";
      if (s.ShownKnown == null) return "the shown forms are not known";
      if (s.ShownKnown != "") return "a known screen's form is shown (" + s.ShownKnown + ")";
      return "";
    }

    /// <summary>The host may try the back press now: the owner runs (not halted, no click on its way, no form search), the screen
    /// (the tick's) has been the same unrecognised one for UnknownBackSettleMs, presses are left, and no try was made within UnknownBackRetryMs.
    /// The fresh state is judged again by Check (which also needs the shown known forms).</summary>
    public bool UnknownBackDue(ClickOwner o, ScreenState s, long now) {
      if (halted.ContainsKey(o) || watches.ContainsKey(o) || SearchWait) return false;
      if ((o == ClickOwner.Sell && NoSellClicks) || (o == ClickOwner.Locate && NoLocateClicks)) return false;
      if (!Unrecognised(s) || s.ModalOpen || !s.OfferRead || Exempt(s.TopForm)) return false;
      if (now - s.SinceMs < UnknownBackSettleMs) return false;
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
      if (searchWaited.Remove(o)) { Halt(o, "unknown", "unknown screen " + name + ": the form search ended without finding the screen"); return true; }
      long[] t; string b;
      if (!stall.TryGetValue(o, out t) || now - t[1] > StallGapMs || !stallBrief.TryGetValue(o, out b) || b != s.Brief) { t = new[] { now, now }; stall[o] = t; stallBrief[o] = s.Brief; }
      t[1] = now;
      if (now - t[0] < UnknownStallMs) return false;
      Halt(o, "unknown", "unknown screen " + name + " for " + ((now - t[0]) / 1000) + " s");
      return true;
    }

    static bool Exempt(string top) {
      if (string.IsNullOrEmpty(top)) return false;
      foreach (var n in top.Split(',')) if (Array.IndexOf(StallExempt, n.Trim()) >= 0) return true;
      return false;
    }

    /// <summary>The pilot stops (also used by the host for its own reasons, e.g. the player's cancel).</summary>
    public GateResult Halt(ClickOwner o, string reason, string detail) {
      if (!halted.ContainsKey(o)) { halted[o] = reason; haltDetail[o] = detail ?? ""; watches.Remove(o); Note(o, "halt:" + reason, "STOP (" + reason + ") " + detail + " - hints only from now on"); }
      return GateResult.Deny(reason, detail);
    }

    /// <summary>(e) A click was sent: what has to happen next (null = nothing is checked, e.g. a drag).</summary>
    public void Arm(ClickOwner o, ClickTarget t, Func<ScreenState, bool> done, long now) { Arm(o, t, done, now, null); }

    /// <summary>The same; <paramref name="offer"/> = the pop-up an OfferClose closes (its effect is logged as "offer: closed &lt;Form&gt;").</summary>
    public void Arm(ClickOwner o, ClickTarget t, Func<ScreenState, bool> done, long now, string offer) {
      clickedAt[o] = now; lastTarget[o] = t;
      if (t == ClickTarget.OfferClose) offerRun[o] = OfferRun(o) + 1; else offerRun.Remove(o);
      if (t == ClickTarget.UnknownBack) unknownBacks[o] = UnknownBacks(o) + 1;
      if (done == null) { watches.Remove(o); return; }
      int ms = t == ClickTarget.CityInventory || t == ClickTarget.FoundryButton || t == ClickTarget.ArtWorkshop || t == ClickTarget.ArtReplace || t == ClickTarget.ArtTakeOverConfirm ? SlowEffectMs : t == ClickTarget.InvCardGem || t == ClickTarget.GemFuncRow || t == ClickTarget.ArtMatCell ? GemEffectMs : t == ClickTarget.ArtBulkCell ? ArtTickMs : EffectMs;
      watches[o] = new Watch { Done = done, Deadline = now + ms, What = t == ClickTarget.OfferClose && offer != null ? "OfferClose(" + offer + ")" : t.ToString(), Offer = t == ClickTarget.OfferClose ? offer ?? "" : null, Target = t, ArmedAt = now };
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
      }
      return null;
    }

    /// <summary>(e) Looks at the screen now: a click that showed its effect is done; one that did not within its time stops the owner.
    /// Called every tick for the owners with a click on the way.</summary>
    public void Observe(ScreenState s, long now) {
      if (s == null || watches.Count == 0) return;
      foreach (var o in new List<ClickOwner>(watches.Keys)) {
        var w = watches[o];
        if (w.Done(s)) {
          watches.Remove(o); Note(o, "effect:" + w.What + ":" + now, w.What + " took effect");
          if (w.Offer != null) { var sk = Sink; if (sk != null) sk("offer: closed " + w.Offer); }
        }
        else if (now > w.Deadline && SearchWait && Unrecognised(s)) { w.Deadline = now + 1000; searchWaited.Add(o); }   // the form is being searched for: its effect may still show
        else if (!StillAwaited(o, w, now)) Halt(o, "nochange", w.What + " changed nothing within the time (screen " + s.Brief + ")");
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
      if (halted.TryGetValue(o, out h)) return GateResult.Deny("halted", h);
      Observe(s, now);
      if (halted.TryGetValue(o, out h)) return GateResult.Deny("halted", h);
      if (watches.ContainsKey(o)) return GateResult.Wait("pending", "the previous click has not shown its effect yet");
      // an offer pop-up on top: the pilot closes it first (OfferClose), nothing else is decided under it
      if (s != null && s.OfferCount > 0) return GateResult.Wait("offer", s.ModalWhy);
      bool modeUnknown = s != null && s.Kind == ScreenKind.Inventory && s.InvEquipTab && !s.InvBulkKnown;
      if (s == null || s.Kind == ScreenKind.Unknown || modeUnknown) {
        string d = modeUnknown ? "the inventory's mode is not readable" : "the screen is not recognised" + (s != null && s.TopForm != "" ? " (top form " + s.TopForm + " not in the list)" : "");
        if (!modeUnknown && SearchWait && Unrecognised(s)) { searchWaited.Add(o); stall.Remove(o); return GateResult.Wait("search", d + " (a form search runs)"); }
        long at;
        // (the back press leaves one unknown screen for another one at times: that is its effect, judged by its watch, not a stop)
        ClickTarget lt;
        if (clickedAt.TryGetValue(o, out at) && now - at <= OwnClickWindowMs && !searchWaited.Contains(o) && !(lastTarget.TryGetValue(o, out lt) && lt == ClickTarget.UnknownBack)) return Halt(o, "unknown", d + " after the pilot's own click");
        if (!modeUnknown && UnknownStall(o, s, now)) return GateResult.Deny("unknown", HaltDetail(o));
        return GateResult.Wait("unknown", d);
      }
      stall.Remove(o); searchWaited.Remove(o);
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
      if (r.Target == ClickTarget.OfferClose) return CheckOffer(r, f, now, tag);
      if (r.Target == ClickTarget.UnknownBack) return CheckUnknownBack(r, f, tag);
      // any other click waits while an offer pop-up is on top (it would land on the pop-up)
      if (f != null && f.OfferCount > 0) return GateResult.Wait("offer", tag + ": " + f.ModalWhy + " is open");
      // the artifact cleanup never clicks, whatever the auto click setting says; nothing of the artifact tab is on any whitelist either
      // (with the site's artAuto switch and the auto click on - ArtSellAuto / ArtLocateAuto - the artifact flows click their Art* targets and nothing else)
      bool artT = IsArtTarget(r.Target);
      if (r.Owner == ClickOwner.Sell && (NoSellClicks || (artT && !ArtSellAuto) || (ArtSellAuto && !artT) || (f != null && f.Kind == ScreenKind.Inventory && f.InvArtTab && r.Target != ClickTarget.InvEquipTab && !artT))) return Halt(r.Owner, "artifact", tag + ": " + (NoSellClicks || !ArtSellAuto ? "the artifact cleanup is hints only" : "not a target of the artifact cleanup"));
      if (r.Owner == ClickOwner.Locate && (NoLocateClicks || (artT && !ArtLocateAuto) || (ArtLocateAuto && !artT))) return Halt(r.Owner, "artifact", tag + ": " + (NoLocateClicks || !ArtLocateAuto ? "the artifact guide is hints only" : "not a target of the artifact guide"));
      bool heroArt = IsHeroArtTarget(r.Target);
      if (artT && r.Owner == ClickOwner.Equip && !heroArt) return Halt(r.Owner, "target", tag + " is not a target Equip may click");
      if (heroArt && r.Owner == ClickOwner.Equip && !ArtEquipAuto) return Halt(r.Owner, "artifact", tag + ": the artifact steps are hints only");
      var rule = RuleOf(r.Owner, r.Target);
      if (rule == null) return Halt(r.Owner, "target", tag + " is not a target " + r.Owner + " may click");
      if (f == null || f.Kind == ScreenKind.Unknown) return UnknownWait(r, f, now, tag + ": the screen is not recognised" + (f != null && f.TopForm != "" ? " (top form " + f.TopForm + " not in the list)" : ""));
      if (Array.IndexOf(rule.Kinds, f.Kind) < 0) return Halt(r.Owner, "screen", tag + " needs " + string.Join("/", Names(rule.Kinds)) + ", the game shows " + f.Brief);
      if (f.ModalOpen && !rule.AllowModal) return Halt(r.Owner, "modal", tag + ": " + f.ModalWhy + " is open (" + f.Brief + ")");
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
        if (r.FoundryUnread) {   // waited for while a search can find it (the host asks for one), then a stop
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
      var res = GateResult.Allow(tag + " on " + f.Brief + (r.CellUid > 0 ? " cell " + r.CellUid : ""));
      Note(r.Owner, "allow:" + r.Target + ":" + f.Brief, "allow " + res.Detail);
      return res;
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
      if (f.OfferCount > 1) return Halt(o, "modal", tag + ": " + f.OfferCount + " offer pop-ups are shown at once (" + form + " ...): which one is on top is not known");
      if (r.Form != form) return Halt(o, "cell", tag + ": the pop-up shown is " + form + ", not " + (r.Form ?? "-"));
      if (!OfferForms.IsOffer(form)) return Halt(o, "offer", tag + ": " + form + " is not on the offer list");
      if (f.Kind != ScreenKind.City && f.Kind != ScreenKind.Unknown) return Halt(o, "screen", tag + ": the pop-up is over " + f.Brief + ", not the city");
      if (OfferForms.CloseRect(r.Geo, form, r.W, r.H, r.U) == null) return Halt(o, "offer", tag + ": the close button of " + form + " is not known - not clicked");
      if (!OfferForms.OnClose(r.Geo, form, r.X, r.Y, r.W, r.H, r.U)) return Halt(o, "target", tag + ": not on the close button of " + form);
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
      if (!OnBackArrow(r.X, r.Y, r.U)) return Halt(o, "target", tag + ": not on the back arrow");
      var res = GateResult.Allow(tag + " on " + f.Brief + " (press " + (n + 1) + " of " + MaxUnknownBacks + ")");
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
