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
  }

  public sealed class ClickGate {
    /// <summary>How long an unreadable screen is waited for before the pilot stops.</summary>
    public const int UnknownGraceMs = ScreenTracker.HoldMs;
    /// <summary>How long a click has to show its effect (a screen that opens may take a moment).</summary>
    public const int EffectMs = 3500, SlowEffectMs = 6000, GemEffectMs = 2000;
    /// <summary>An artifact tick (or its taking off) has to show in the game's choice within this time.</summary>
    public const int ArtTickMs = 1000;
    /// <summary>An unrecognised screen stops the pilot only this long after the pilot's OWN last click (the screen it caused); later it is waited for.</summary>
    public const int OwnClickWindowMs = 2500;
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
            case ClickTarget.FoundryButton: return new Rule { Kinds = K(ScreenKind.Inventory), InvNormalEquip = true };
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

    sealed class Watch { public Func<ScreenState, bool> Done; public long Deadline; public string What; }

    readonly Dictionary<ClickOwner, string> halted = new Dictionary<ClickOwner, string>();
    readonly Dictionary<ClickOwner, Watch> watches = new Dictionary<ClickOwner, Watch>();
    readonly Dictionary<ClickOwner, long> unknownSince = new Dictionary<ClickOwner, long>();   // Check's own timer: Preflight never touches it
    readonly Dictionary<ClickOwner, string> noted = new Dictionary<ClickOwner, string>();
    readonly Dictionary<ClickOwner, long> clickedAt = new Dictionary<ClickOwner, long>();   // when the owner sent its last click in this request

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
      halted.Remove(o); watches.Remove(o); unknownSince.Remove(o); noted.Remove(o); clickedAt.Remove(o);
    }

    /// <summary>The pilot stops (also used by the host for its own reasons, e.g. the player's cancel).</summary>
    public GateResult Halt(ClickOwner o, string reason, string detail) {
      if (!halted.ContainsKey(o)) { halted[o] = reason; watches.Remove(o); Note(o, "halt:" + reason, "STOP (" + reason + ") " + detail + " - hints only from now on"); }
      return GateResult.Deny(reason, detail);
    }

    /// <summary>(e) A click was sent: what has to happen next (null = nothing is checked, e.g. a drag).</summary>
    public void Arm(ClickOwner o, ClickTarget t, Func<ScreenState, bool> done, long now) {
      clickedAt[o] = now;
      if (done == null) { watches.Remove(o); return; }
      int ms = t == ClickTarget.CityInventory || t == ClickTarget.FoundryButton || t == ClickTarget.ArtWorkshop || t == ClickTarget.ArtReplace || t == ClickTarget.ArtTakeOverConfirm ? SlowEffectMs : t == ClickTarget.InvCardGem || t == ClickTarget.GemFuncRow || t == ClickTarget.ArtMatCell ? GemEffectMs : t == ClickTarget.ArtBulkCell ? ArtTickMs : EffectMs;
      watches[o] = new Watch { Done = done, Deadline = now + ms, What = t.ToString() };
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
        if (w.Done(s)) { watches.Remove(o); Note(o, "effect:" + w.What + ":" + now, w.What + " took effect"); }
        else if (now > w.Deadline) Halt(o, "nochange", w.What + " changed nothing within the time (screen " + s.Brief + ")");
      }
    }

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
      bool modeUnknown = s != null && s.Kind == ScreenKind.Inventory && s.InvEquipTab && !s.InvBulkKnown;
      if (s == null || s.Kind == ScreenKind.Unknown || modeUnknown) {
        string d = modeUnknown ? "the inventory's mode is not readable" : "the screen is not recognised";
        long at;
        if (clickedAt.TryGetValue(o, out at) && now - at <= OwnClickWindowMs) return Halt(o, "unknown", d + " after the pilot's own click");
        return GateResult.Wait("unknown", d);
      }
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
      if (f == null || f.Kind == ScreenKind.Unknown) return UnknownWait(r, f, now, tag + ": the screen is not recognised");
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

    GateResult UnknownWait(ClickRequest r, ScreenState f, long now, string detail) {
      long since;
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
