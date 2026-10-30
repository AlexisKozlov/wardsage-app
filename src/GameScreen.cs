// Wardsage - which screen of the game is open, decided in ONE place.
//
// Every feature that needs to know where the player is (the equip helper, «Показать в игре», the storage cleanup, and every
// auto click) takes it from the ScreenState of the overlay's tick, not from a check of its own. The state is made of the
// existing readers (the hero screen, the inventory incl. its bulk-sale mode, the Foundry and the gem screen, the city's form, else its bottom bar),
// only the decision is central:
//
//   Screen = City | Inventory(tab, bulk, selected item) | HeroScreen(hero, tab, slot) | Foundry(tab, pop-up) | Gem(function) | ArtWorkshop(tab, artifact) | Unknown
//   plus ModalOpen (a pop-up or a pending choice the app can detect; "offer:<Form>" = one of the game's offer / notice pop-ups, src/OfferForms.cs) and the times.
//
// A short read failure must not flip the screen: the readers give null now and then (the form is rebuilt, a table is mid-write).
// ScreenTracker keeps the last good reading of each source and the last confirmed screen for up to 1.5 s while the reads fail
// (said once in the log), then the screen is Unknown. The fresh state used by the click gate (src/ClickGate.cs) is NEVER held:
// ScreenState.Classify on a fresh read decides alone.
//
// Pure decision logic, no Windows APIs (tests: tests/ScreenReplay.cs with tests/fixtures/screens/*.json); the host
// (app/Overlay.cs) does the reads.
using System;
using System.Collections.Generic;
using System.Text;

namespace RealmForge {
  public enum ScreenKind { Unknown, City, Inventory, HeroScreen, Foundry, Gem, ArtWorkshop }

  /// <summary>What the readers gave this tick. A null reader = the read failed (or the form was never built).</summary>
  public sealed class ScreenInputs {
    public InventoryScreen Inv;
    public RecastScreen Rs;
    public HeroScreen Hero;
    /// <summary>The artifact workshop («Мастерская»); null = not read (only read while an artifact request is guided).</summary>
    public ArtWorkshopScreen ArtWs;
    /// <summary>The city's bottom bar button was seen (a picture; looked for only when no form is open).</summary>
    public bool CitySeen;
    /// <summary>The city's form is shown (read from memory like the other forms); null = unknown (no form id / node not found): the picture decides then.</summary>
    public bool? CityForm;
    /// <summary>A pop-up found by the picture (the host fills it for a fresh read only), with its name.</summary>
    public bool ModalExtra;
    public string ModalExtraWhy = "";
    /// <summary>The offer / notice pop-ups (src/OfferForms.cs) shown now, by form name (src/OfferScan.cs); null = the game's window table was not read.</summary>
    public List<string> Offers;
    /// <summary>Several offers shown: the first of Offers is the one on top (it came last, alone: OfferForms.Order).</summary>
    public bool OfferTopKnown;
    /// <summary>A shown form that is neither a screen the app knows nor an offer (for the log and the stop's reason; "" = none / not read).</summary>
    public string TopUnknown = "";
    /// <summary>Over a known screen other than the city: shown forms nobody knows that were not there when this screen came (an item tip, a reward,
    /// a «Недостаточно…» notice ...): a pop-up on top, so the screen is modal ("form:&lt;names&gt;"); "" = none / not read.</summary>
    public string TopNew = "";
    /// <summary>The shown forms that are screens the app knows, the battle among them (src/OfferScan.cs ReadShownKnownForms): "" = none, null = not read.
    /// Read for the unknown-screen back press only (ClickGate UnknownBack); it never changes the screen's kind.</summary>
    public string ShownKnown;
    /// <summary>On the city: the shown forms the screen map (src/ScreenMap.cs) calls a pop-up, a dangerous dialog or a wait (they cover the city's
    /// buttons): the screen is modal ("form:&lt;names&gt;"); "" = none / not read.</summary>
    public string TopCity = "";
    /// <summary>The shown forms a pilot's own flow opens on this screen (ScreenMap.FlowForms: the item tip the item select opens, a list's filter,
    /// a sale / ascension confirm): the screen's sub-state, never a pop-up to close; "" = none / not read.</summary>
    public string Flow = "";
  }

  public sealed class ScreenState {
    /// <summary>Is the picture of the city's bottom bar needed to judge the screen? Yes for City, and for Unknown unless the city form reads open.</summary>
    public static bool NeedsCityPicture(ScreenKind kind, bool? cityForm) {
      return kind == ScreenKind.City || (kind == ScreenKind.Unknown && cityForm != true);
    }

    public ScreenKind Kind = ScreenKind.Unknown;
    // Inventory
    public bool InvEquipTab, InvBulk, InvBulkKnown;
    public bool InvArtTab, InvArtBulk, InvArtBulkKnown;   // on «Артефакт» (then InvEquipTab is false): its «Массовая продажа» and whether the mode was read
    public long InvSelected;          // the item whose card is open (normal mode), 0 = none
    public int InvArtMode;            // the artifact tab's mode (-1 normal, 1 arrange, 2 bulk sale, 0 = unread)
    public long InvArtSelected;       // the artifact whose card is open (normal mode; read only while an artifact request is guided), 0 = none / unread
    public string InvSelKey = "";     // the bulk-sale choice (count:sum of uids), to see a click change it
    // HeroScreen
    public long Hero;
    public int HeroTab = -1, HeroSlot = -1;
    // Foundry (FoundryTab: 0 forge, 1 identify, 2 ascend, 3 recast, 4 reforge, -1 unknown)
    public int FoundryTab = -1;
    public bool FoundryPopup;         // the Ascension list's filter pop-up
    public long FoundrySel;           // the item selected in the shown list
    public string FoundryKey = "";    // the slot switches and the list (a slot tab press changes it)
    // Gem
    public int GemFunc = -1;
    public long GemSel;
    // ArtWorkshop (ArtWsTab: 1 enhance, 2 promote, 5 refine, 0 unread)
    public int ArtWsTab;
    public long ArtWsUid;
    public string ArtWsKey = "";      // the chosen copies (a click on the list changes it)
    // any pop-up / pending choice the app can detect
    public bool ModalOpen;
    public string ModalWhy = "";
    // the offer / notice pop-ups over the city (src/OfferForms.cs): the first one shown, how many, whether the window table was read at all
    public string Offer = "";
    public int OfferCount;
    public bool OfferRead;
    /// <summary>One offer shown, or of several the one on top is known (Offer is it).</summary>
    public bool OfferTopKnown;
    /// <summary>On an Unknown screen: the shown form that is not known (its name, for the log), "" = none.</summary>
    public string TopForm = "";
    /// <summary>The form on top by name, for every screen: the offer pop-up, else the screen map's top of the forms over the screen (a pop-up
    /// over a known screen or the city, the forms of an unknown screen: ScreenMap.Top), else the known screen's own form; "" = not known.</summary>
    public string Top = "";
    /// <summary>The screen map's class of Top (ScreenMap.ClassOf: F P E O D W K U).</summary>
    public char TopClass = 'U';
    /// <summary>The shown known screens' forms (ScreenInputs.ShownKnown): "" = none, null = not read.</summary>
    public string ShownKnown;
    /// <summary>The pilot's own flow forms shown on the screen (ScreenInputs.Flow; the screen stays the base one): "" = none.</summary>
    public string Flow = "";
    // times (ms, the overlay's clock): this reading, the last time the screen was really read (older when Held), since when the screen is this one
    public long ReadAtMs, ConfirmedAtMs, SinceMs;
    /// <summary>The reads failed this moment and the last confirmed screen is shown (at most ScreenTracker.HoldMs).</summary>
    public bool Held;
    // the readings the state was made of (for the host and the guide)
    public InventoryScreen Inv;
    public RecastScreen Rs;
    public HeroScreen HeroScr;
    public ArtWorkshopScreen ArtWs;

    public static readonly string[] FoundryTabNames = { "forge", "identify", "ascend", "recast", "reforge" };
    public string FoundryTabName { get { return FoundryTab >= 0 && FoundryTab < FoundryTabNames.Length ? FoundryTabNames[FoundryTab] : "unknown"; } }
    public bool OnAscend { get { return Kind == ScreenKind.Foundry && FoundryTab == 2; } }
    /// <summary>The inventory's equipment list in the normal mode (the mode known).</summary>
    public bool InvNormalEquip { get { return Kind == ScreenKind.Inventory && InvEquipTab && InvBulkKnown && !InvBulk; } }

    /// <summary>A short description for the log, without the volatile parts (selection, times).</summary>
    public string Brief {
      get {
        var sb = new StringBuilder(Kind.ToString());
        switch (Kind) {
          case ScreenKind.Inventory:
            if (InvArtTab) sb.Append(" artifact").Append(!InvArtBulkKnown ? " bulk?" : InvArtBulk ? " ART-BULK-SALE" : " normal");
            else sb.Append(InvEquipTab ? " equip" : " other-tab").Append(!InvBulkKnown ? " bulk?" : InvBulk ? " BULK-SALE" : " normal");
            break;
          case ScreenKind.HeroScreen: sb.Append(" hero ").Append(Hero).Append(" tab ").Append(HeroTab).Append(" slot ").Append(HeroSlot); break;
          case ScreenKind.Foundry: sb.Append(' ').Append(FoundryTabName).Append(FoundryPopup ? " popup" : ""); break;
          case ScreenKind.Gem: sb.Append(" func ").Append(GemFunc); break;
          case ScreenKind.ArtWorkshop: sb.Append(" tab ").Append(ArtWsTab); break;
        }
        if (ModalOpen) sb.Append(" modal(").Append(ModalWhy).Append(')');
        if (Flow != "") sb.Append(" flow(").Append(Flow).Append(')');
        if (Kind == ScreenKind.Unknown && TopForm != "") sb.Append(" top(").Append(TopForm).Append(')');
        if (Kind == ScreenKind.Unknown && Top != "") sb.Append(" map(").Append(Top).Append(':').Append(ScreenMap.ClassName(TopClass)).Append(')');
        return sb.ToString();
      }
    }

    /// <summary>Everything that can change a decision (the brief plus the selections).</summary>
    public string Key { get { return Brief + "|" + InvSelected + "|" + InvSelKey + "|" + FoundrySel + "|" + FoundryKey + "|" + GemSel + "|" + InvArtSelected + "|" + ArtWsUid + "|" + ArtWsKey; } }

    /// <summary>The screen from one set of readings, no memory of earlier ones (the click gate uses this on a fresh read).
    /// Order: the Foundry and the gem screen open from the inventory / the hero's gear screen and sit above them.</summary>
    /// <summary>The form of a screen the app reads ("" = Unknown).</summary>
    public static string OwnForm(ScreenKind k) {
      switch (k) {
        case ScreenKind.City: return "Form_MainCity";
        case ScreenKind.Inventory: return "Form_BackpackIntegration";
        case ScreenKind.HeroScreen: return "Form_CharactorMain";
        case ScreenKind.Foundry: return "Form_EquipSubliming";
        case ScreenKind.Gem: return "Form_EquipGemMain";
        case ScreenKind.ArtWorkshop: return "Form_EquipArtifactIntensify";
      }
      return "";
    }

    public static ScreenState Classify(ScreenInputs i, long now) {
      var s = new ScreenState { ReadAtMs = now, ConfirmedAtMs = now, SinceMs = now, Inv = i.Inv, Rs = i.Rs, HeroScr = i.Hero, ArtWs = i.ArtWs };
      var rs = i.Rs; var inv = i.Inv; var h = i.Hero; var aw = i.ArtWs;
      bool heroUp = h != null && h.FormOk && h.Hero > 0;   // Hero alone stays set while the form is hidden behind another screen (FormOk = the form is on)
      if (aw != null && aw.Open) {
        // a full-screen form that opens from the inventory's artifact card or the hero's artifact tab: it sits above them
        s.Kind = ScreenKind.ArtWorkshop; s.ArtWsTab = aw.Tab; s.ArtWsUid = aw.Uid;
        s.ArtWsKey = aw.Stage + "/" + string.Join(",", new List<int>(aw.MatSel).ConvertAll(x => x.ToString()).ToArray());
      } else if (rs != null && rs.SublimingOpen) {
        s.Kind = ScreenKind.Foundry;
        s.FoundryTab = rs.CurType;
        s.FoundryPopup = rs.AscendFilterOpen;
        s.FoundrySel = rs.CurType == 2 ? rs.AscendSel : rs.CurType == 3 ? rs.Sel : 0;
        s.FoundryKey = rs.CurType == 2 ? string.Join(",", Array.ConvertAll(rs.AscendParts, x => x.ToString())) + "/" + rs.AscendEquips.Length
                     : rs.CurType == 3 ? string.Join(",", Array.ConvertAll(rs.SelectParts, x => x.ToString())) + "/" + rs.Equips.Length : "";
        if (rs.AscendFilterOpen) { s.ModalOpen = true; s.ModalWhy = "filter"; }
        else if (rs.ChoosePending) { s.ModalOpen = true; s.ModalWhy = "choice"; }
      } else if (rs != null && rs.GemOpen) {
        s.Kind = ScreenKind.Gem; s.GemFunc = rs.GemFunc; s.GemSel = rs.GemSel;
      } else if (inv != null && inv.Open) {
        s.Kind = ScreenKind.Inventory;
        s.InvEquipTab = inv.EquipTab; s.InvBulk = inv.Bulk; s.InvBulkKnown = inv.BulkKnown;
        s.InvArtTab = inv.ArtTab; s.InvArtBulk = inv.ArtBulk; s.InvArtBulkKnown = inv.ArtBulkKnown; s.InvArtMode = inv.ArtMode;
        s.InvArtSelected = inv.ArtSel >= 1 && inv.ArtSel <= inv.Uids.Count ? inv.Uids[inv.ArtSel - 1] : 0;
        s.InvSelected = inv.Current;
        long sum = 0; foreach (var u in inv.Selected) sum = unchecked(sum + u);
        s.InvSelKey = inv.Selected.Count + ":" + sum;
      } else if (heroUp) {
        s.Kind = ScreenKind.HeroScreen; s.Hero = h.Hero; s.HeroTab = h.Tab; s.HeroSlot = h.Part;
        if (h.FilterOpen) { s.ModalOpen = true; s.ModalWhy = "heroFilter"; }
      } else if (i.CityForm == true || i.CitySeen) {
        s.Kind = ScreenKind.City;
      }
      // an offer pop-up sits above every screen (the game opens it on top of the city): it is THE modal
      s.OfferRead = i.Offers != null;
      if (i.Offers != null && i.Offers.Count > 0) {
        s.OfferCount = i.Offers.Count; s.Offer = i.Offers[0]; s.OfferTopKnown = i.Offers.Count == 1 || i.OfferTopKnown;
        s.ModalOpen = true; s.ModalWhy = "offer:" + s.Offer + (s.OfferCount > 1 ? "+" + (s.OfferCount - 1) : "");
      }
      if (!s.ModalOpen && i.ModalExtra) { s.ModalOpen = true; s.ModalWhy = i.ModalExtraWhy; }
      if (!s.ModalOpen && s.Kind != ScreenKind.Unknown && s.Kind != ScreenKind.City && !string.IsNullOrEmpty(i.TopNew)) { s.ModalOpen = true; s.ModalWhy = "form:" + i.TopNew; }
      if (!s.ModalOpen && s.Kind == ScreenKind.City && !string.IsNullOrEmpty(i.TopCity)) { s.ModalOpen = true; s.ModalWhy = "form:" + i.TopCity; }
      if (s.Kind == ScreenKind.Unknown) s.TopForm = i.TopUnknown ?? "";
      s.ShownKnown = i.ShownKnown;
      if (s.Kind != ScreenKind.City) s.Flow = i.Flow ?? "";
      // the form on top by name
      if (s.OfferCount > 0) s.Top = s.Offer;
      else if (s.Kind == ScreenKind.Unknown) s.Top = ScreenMap.Top(s.TopForm);
      else if (s.ModalOpen && s.ModalWhy.StartsWith("form:")) s.Top = ScreenMap.Top(s.ModalWhy.Substring(5));
      if (s.Top == "") s.Top = OwnForm(s.Kind);
      s.TopClass = ScreenMap.ClassOf(s.Top);
      return s;
    }
  }

  /// <summary>Makes the per-tick ScreenState: classifies the readings and holds the last good ones while the reads fail.</summary>
  public sealed class ScreenTracker {
    /// <summary>How long the last good reading / the last confirmed screen stands in for a failing read.</summary>
    public const int HoldMs = 1500;

    /// <summary>The log (the host's journal); null = none.</summary>
    public Action<string> Sink;

    InventoryScreen inv; RecastScreen rs; HeroScreen hero; ArtWorkshopScreen artWs;
    long invAt, rsAt, heroAt, artWsAt;
    ScreenState last;
    string briefSaid = "", holdSaid = "", failSaid = "";

    /// <summary>The state of the last Update (null before the first).</summary>
    public ScreenState Current { get { return last; } }
    /// <summary>The held readings (the last good ones within HoldMs): what the guide and the pilots are given.</summary>
    public InventoryScreen Inv { get { return inv; } }
    public RecastScreen Rs { get { return rs; } }
    public HeroScreen Hero { get { return hero; } }
    public ArtWorkshopScreen ArtWs { get { return artWs; } }

    void Say(string t) { var s = Sink; if (s != null) s(t); }

    static T Hold<T>(T raw, ref T held, ref long at, long now, string name, List<string> failing, ref long oldest) where T : class {
      if (raw != null) { held = raw; at = now; return held; }
      if (held != null && now - at <= HoldMs) { failing.Add(name); if (at < oldest) oldest = at; return held; }
      held = null; failing.Add(name + "!");   // gone for good
      return null;
    }

    public ScreenState Update(ScreenInputs raw, long now) {
      var failing = new List<string>();
      var use = new ScreenInputs { CitySeen = raw.CitySeen, CityForm = raw.CityForm, ModalExtra = raw.ModalExtra, ModalExtraWhy = raw.ModalExtraWhy, Offers = raw.Offers, OfferTopKnown = raw.OfferTopKnown, TopUnknown = raw.TopUnknown, TopNew = raw.TopNew, ShownKnown = raw.ShownKnown, TopCity = raw.TopCity, Flow = raw.Flow };
      long oldest = now;   // the oldest reading that stands in for a failed one
      use.Inv = Hold(raw.Inv, ref inv, ref invAt, now, "inventory", failing, ref oldest);
      use.Rs = Hold(raw.Rs, ref rs, ref rsAt, now, "foundry", failing, ref oldest);
      use.Hero = Hold(raw.Hero, ref hero, ref heroAt, now, "hero", failing, ref oldest);
      // the workshop is read only while an artifact request is guided and is null while its form was never found: only a reading that
      // existed is held (a short failure), a missing one is not a failure
      if (raw.ArtWs != null) { artWs = raw.ArtWs; artWsAt = now; }
      else if (artWs != null && now - artWsAt > HoldMs) artWs = null;
      else if (artWs != null) { failing.Add("artWorkshop"); if (artWsAt < oldest) oldest = artWsAt; }
      use.ArtWs = artWs;
      var cand = ScreenState.Classify(use, now);
      if (oldest < now) { cand.Held = true; cand.ConfirmedAtMs = oldest; }   // made from readings that stand in for failed ones
      // the whole screen unknown for a moment while it was a known one: keep it (a flicker is not a screen change)
      // (not while an offer pop-up is read on top: that is a known state, the pilots close it)
      if (cand.Kind == ScreenKind.Unknown && cand.OfferCount == 0 && last != null && last.Kind != ScreenKind.Unknown && now - last.ConfirmedAtMs <= HoldMs) {
        var held = ShallowCopy(last);
        held.Held = true; held.ReadAtMs = now;
        if (holdSaid != last.Brief) { holdSaid = last.Brief; Say("screen: reads failing - keeping " + last.Brief + " for up to " + HoldMs + " ms"); }
        last = held;
        return held;
      }
      string fsig = string.Join(",", failing.ToArray());
      if (fsig != failSaid) { failSaid = fsig; if (fsig != "") Say("screen: read failed for " + fsig + " (the last good reading stands in for " + HoldMs + " ms)"); }
      if (holdSaid != "") {
        if (cand.Kind != ScreenKind.Unknown) Say("screen: reads back (" + cand.Brief + ")");
        else Say("screen: unknown after " + HoldMs + " ms of failing reads");
        holdSaid = "";
      }
      if (last != null && last.Brief == cand.Brief) cand.SinceMs = last.SinceMs;
      if (cand.Brief != briefSaid) { briefSaid = cand.Brief; Say("screen: " + cand.Brief); }
      last = cand;
      return cand;
    }

    static ScreenState ShallowCopy(ScreenState s) {
      var c = new ScreenState();
      c.Kind = s.Kind; c.InvEquipTab = s.InvEquipTab; c.InvBulk = s.InvBulk; c.InvBulkKnown = s.InvBulkKnown; c.InvArtTab = s.InvArtTab; c.InvArtBulk = s.InvArtBulk; c.InvArtBulkKnown = s.InvArtBulkKnown; c.InvArtMode = s.InvArtMode; c.InvArtSelected = s.InvArtSelected; c.ArtWsTab = s.ArtWsTab; c.ArtWsUid = s.ArtWsUid; c.ArtWsKey = s.ArtWsKey; c.InvSelected = s.InvSelected; c.InvSelKey = s.InvSelKey;
      c.Hero = s.Hero; c.HeroTab = s.HeroTab; c.HeroSlot = s.HeroSlot;
      c.FoundryTab = s.FoundryTab; c.FoundryPopup = s.FoundryPopup; c.FoundrySel = s.FoundrySel; c.FoundryKey = s.FoundryKey;
      c.GemFunc = s.GemFunc; c.GemSel = s.GemSel; c.ModalOpen = s.ModalOpen; c.ModalWhy = s.ModalWhy;
      c.Offer = s.Offer; c.OfferCount = s.OfferCount; c.OfferRead = s.OfferRead; c.OfferTopKnown = s.OfferTopKnown; c.TopForm = s.TopForm; c.Top = s.Top; c.TopClass = s.TopClass; c.Flow = s.Flow;
      c.ReadAtMs = s.ReadAtMs; c.ConfirmedAtMs = s.ConfirmedAtMs; c.SinceMs = s.SinceMs; c.Held = s.Held;
      c.Inv = s.Inv; c.Rs = s.Rs; c.HeroScr = s.HeroScr; c.ArtWs = s.ArtWs;
      return c;
    }
  }
  /// <summary>When the overlay asks for a background search of the game's forms (a full pass, ~1 s, off the UI thread). Pure.</summary>
  /// <summary>A list the pilots click into, settled: two reads in a row gave the same scroll, length, ends and filter (a list that still moves
  /// after a drag, a filter or a tab press - the game's scroll glides on - is not clicked from an older read). The pilots' list steps run only
  /// on a settled list; the time from its first move to settled is measured (ReadTiming).</summary>
  public sealed class ListSettle {
    string last; long movedAt = -1;
    /// <summary>The last two reads were equal (and readable).</summary>
    public bool Settled { get; private set; }
    /// <summary>One read of the list (null = not readable: not settled).</summary>
    public void Update(string key, long now) {
      bool same = key != null && key == last;
      if (!same && last != null && key != null && movedAt < 0) movedAt = now;
      if (same && movedAt >= 0) { ReadTiming.Settle(now - movedAt); movedAt = -1; }
      Settled = same; last = key;
    }
    public void Reset() { last = null; movedAt = -1; Settled = false; }
    static readonly System.Globalization.CultureInfo Ci = System.Globalization.CultureInfo.InvariantCulture;
    /// <summary>The inventory's list as the pilots click it (null = not read).</summary>
    public static string KeyOf(InventoryScreen inv) {
      if (inv == null || !inv.Open) return null;
      int n = inv.Uids.Count;
      return (inv.EquipTab ? "e" : "o") + (inv.Bulk ? "b" : "n") + "|" + (inv.List != null && inv.List.Has ? inv.List.ScrollY.ToString("0.0", Ci) : "-") + "|" + n
        + "|" + (n > 0 ? inv.Uids[0] : 0) + "|" + (n > 0 ? inv.Uids[n - 1] : 0) + "|" + inv.ListPtr;
    }
    /// <summary>The Foundry's Ascension list as the pilots click it (null = not read; "" = not on that tab).</summary>
    public static string KeyOf(RecastScreen rs) {
      if (rs == null) return null;
      if (rs.CurType != 2) return "";
      var e = rs.AscendEquips ?? new long[0];
      return (rs.AscendList != null && rs.AscendList.Has ? rs.AscendList.ScrollY.ToString("0.0", Ci) : "-") + "|" + e.Length + "|" + (e.Length > 0 ? e[0] : 0) + "|" + (e.Length > 0 ? e[e.Length - 1] : 0)
        + "|" + string.Join(",", Array.ConvertAll(rs.AscendParts ?? new int[0], x => x.ToString(Ci)));
    }
  }

  public static class FormSearchPolicy {
    /// <summary>After the pilot's own click that opens a screen: how long a search is wanted (until the screen is read).</summary>
    public const int OwnOpenWindowMs = 6000;
    /// <summary>... while the form that click opens has not been found yet (a slow PC: one search takes ~17 s): searches go on up to the click gate's cap.</summary>
    public const int SlowOpenWindowMs = ClickGate.SearchCapMs;
    /// <summary>The least gap between two searches while the pilot's click is awaited (keep >= 1000 until the window table is the main source).</summary>
    public const int OwnOpenGapMs = 1000;
    public const int UnknownGapMs = 2000;

    /// <summary>The reason of the search wanted now (and its least gap), null = none. <paramref name="msSinceOwnOpenClick"/> &lt; 0: no
    /// open click of the pilot is awaited. A held screen (made from earlier readings while the reads fail) counts as Unknown.</summary>
    public static string Want(ScreenKind kind, bool held, long msSinceOwnOpenClick, out int gapMs) { return Want(kind, held, msSinceOwnOpenClick, false, out gapMs); }
    /// <summary>The same; <paramref name="formMissing"/> = the form the pilot's click opens is not found yet (the window is SlowOpenWindowMs then).</summary>
    public static string Want(ScreenKind kind, bool held, long msSinceOwnOpenClick, bool formMissing, out int gapMs) {
      if (msSinceOwnOpenClick >= 0 && msSinceOwnOpenClick < (formMissing ? SlowOpenWindowMs : OwnOpenWindowMs)) { gapMs = OwnOpenGapMs; return "opened by the pilot's click"; }
      if (kind == ScreenKind.Unknown || held) { gapMs = UnknownGapMs; return "screen unknown"; }
      gapMs = 0; return null;
    }
  }
}
