// Wardsage - which screen of the game is open, decided in ONE place.
//
// Every feature that needs to know where the player is (the equip helper, «Показать в игре», the storage cleanup, and every
// auto click) takes it from the ScreenState of the overlay's tick, not from a check of its own. The state is made of the
// existing readers (the hero screen, the inventory incl. its bulk-sale mode, the Foundry and the gem screen, the city's form, else its bottom bar),
// only the decision is central:
//
//   Screen = City | Inventory(tab, bulk, selected item) | HeroScreen(hero, tab, slot) | Foundry(tab, pop-up) | Gem(function) | ArtWorkshop(tab, artifact) | Unknown
//   plus ModalOpen (a pop-up or a pending choice the app can detect) and the times.
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
        return sb.ToString();
      }
    }

    /// <summary>Everything that can change a decision (the brief plus the selections).</summary>
    public string Key { get { return Brief + "|" + InvSelected + "|" + InvSelKey + "|" + FoundrySel + "|" + FoundryKey + "|" + GemSel + "|" + InvArtSelected + "|" + ArtWsUid + "|" + ArtWsKey; } }

    /// <summary>The screen from one set of readings, no memory of earlier ones (the click gate uses this on a fresh read).
    /// Order: the Foundry and the gem screen open from the inventory / the hero's gear screen and sit above them.</summary>
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
      if (!s.ModalOpen && i.ModalExtra) { s.ModalOpen = true; s.ModalWhy = i.ModalExtraWhy; }
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
      var use = new ScreenInputs { CitySeen = raw.CitySeen, CityForm = raw.CityForm, ModalExtra = raw.ModalExtra, ModalExtraWhy = raw.ModalExtraWhy };
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
      if (cand.Kind == ScreenKind.Unknown && last != null && last.Kind != ScreenKind.Unknown && now - last.ConfirmedAtMs <= HoldMs) {
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
      c.ReadAtMs = s.ReadAtMs; c.ConfirmedAtMs = s.ConfirmedAtMs; c.SinceMs = s.SinceMs; c.Held = s.Held;
      c.Inv = s.Inv; c.Rs = s.Rs; c.HeroScr = s.HeroScr; c.ArtWs = s.ArtWs;
      return c;
    }
  }
}
