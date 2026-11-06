// Wardsage - «Надеть в игре» with an artifact: after the five gear items the guide puts the plan's artifact on the hero.
// The way (everything happens on the hero screen, there is no separate artifact form):
//   hero screen, right tab «Артефакт» -> the list (the worn card's icon button, or the big «+» of an empty slot) -> the artifact's cell
//   -> «Заменить» / «Надеть» in the card -> (the artifact is worn by another hero) the pop-up's «Заменить» -> done when the artifact's
//   iHeroId is the hero.
// This file: the reader of those fields (READ ONLY), the step machine, the frames and the auto click pilot. Pure logic, no Windows APIs
// (tests: tests/CoreTests.cs, tests/ScreenReplay.cs); the host (app/Overlay.cs) reads the screens, draws the frames and sends the clicks
// through the click gate (src/ClickGate.cs: the ArtHero* / ArtReplace / ArtTakeOverConfirm targets of the Equip owner).
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum ArtEquipKind {
    Wait,             // nothing to say yet: the screen or the artifact data is not readable, an unknown pop-up is open
    Hero,             // the plan's hero is not the one on the hero screen
    Done,             // the artifact is on the hero (iHeroId == hero)
    ArtTab,           // the hero screen is on another tab: «Артефакт»
    OpenList,         // the list is closed: the worn card's button (HasWorn) or the empty slot's «+»
    PickCell,         // the list is open: the artifact's cell (Pos, 1-based)
    NotInList,        // the list does not hold the artifact (a filter / «Скрыть надетое» hides it)
    PressReplace,     // the artifact's card is open: «Заменить» (HasWorn) / «Надеть»
    ConfirmTakeOver,  // the pop-up «<artifact> в ячейке героя: <имя>» is open: its «Заменить»
    Unfit             // the game will not put it on this hero (the row's bCanEquip is false: another class, or another hero's exclusive one): a hint, never a click
  }

  public sealed class ArtEquipStep {
    public ArtEquipKind Kind;
    public int Pos;              // PickCell: the 1-based place in the list
    public bool HasWorn;         // the hero wears an artifact now (the card says «Заменить», not «Надеть»)
    /// <summary>PickCell: the game's own filter «Скрыть надетое» (off now) would take enough worn rows out ahead of the free artifact to spare a scroll.
    /// NotInList: the filter is on and hides the artifact (another hero wears it): off brings it back.</summary>
    public bool Filter;
    public long Owner = -1;      // iHeroId of the artifact now (-1 = unknown)
    public string Why = "";
    /// <summary>A hint worked out from the screen's geometry alone: the artifact data could not be read (the app never clicks on a guess).</summary>
    public bool Guess;
    public string Key {
      get { return (Guess ? "guess:" : "") + Kind + (Kind == ArtEquipKind.PickCell ? ":" + Pos : "") + (Filter ? ":filter" : "") + (Kind == ArtEquipKind.OpenList || Kind == ArtEquipKind.PressReplace ? (HasWorn ? ":worn" : ":empty") : ""); }
    }
    /// <summary>The step name the page gets (live.art.step).</summary>
    public string Name {
      get {
        switch (Kind) {
          case ArtEquipKind.Hero: return "hero";
          case ArtEquipKind.Done: return "done";
          case ArtEquipKind.ArtTab: return "tab";
          case ArtEquipKind.OpenList: return HasWorn ? "open" : "openAdd";
          case ArtEquipKind.PickCell: return Filter ? "filter" : "pick";
          case ArtEquipKind.NotInList: return "missing";
          case ArtEquipKind.PressReplace: return HasWorn ? "replace" : "equip";
          case ArtEquipKind.ConfirmTakeOver: return "confirm";
          case ArtEquipKind.Unfit: return Why == "exclusive" ? "unfitEx" : "unfit";
          default: return "wait";
        }
      }
    }
  }

  public static class ArtEquipGuide {
    /// <summary>Form_CharactorMain.m_LastType of the «Артефакт» tab (gear is 3).</summary>
    public const int ArtTabType = 7;
    /// <summary>The cells the list shows without a scroll (3 columns, about 3.5 rows of 116 in the viewport): a goal in them needs no filter.</summary>
    public const int FirstView = 9;

    /// <summary>What to do next for <paramref name="goal"/> on <paramref name="hero"/>'s screen. <paramref name="popup"/> = Form_EquipPopUp is open (seen on the picture).</summary>
    public static ArtEquipStep Next(long hero, long goal, HeroScreen h, bool popup) {
      var s = new ArtEquipStep();
      if (h == null || !h.FormOk || h.Hero <= 0) { s.Kind = ArtEquipKind.Hero; s.Why = "screen"; return s; }
      if (h.Hero != hero) { s.Kind = ArtEquipKind.Hero; s.Why = "other hero"; return s; }
      s.Owner = h.ArtTargetOwner;
      if (h.ArtTargetOwner == hero) { s.Kind = ArtEquipKind.Done; return s; }   // already on the hero (also: skipped when it was before the plan)
      if (!h.ArtRead || h.ArtTargetOwner < 0) { s.Kind = ArtEquipKind.Wait; s.Why = "artifact data"; return s; }
      s.HasWorn = h.ArtWorn > 0;
      if (h.Tab != ArtTabType) { s.Kind = ArtEquipKind.ArtTab; return s; }
      if (popup) {
        if (h.ArtListOpen && h.ArtSel == goal) { s.Kind = ArtEquipKind.ConfirmTakeOver; return s; }
        s.Kind = ArtEquipKind.Wait; s.Why = "pop-up"; return s;
      }
      if (!h.ArtListOpen) {
        if (h.ArtWorn == -1) { s.Kind = ArtEquipKind.Wait; s.Why = "worn artifact unknown"; return s; }
        s.Kind = ArtEquipKind.OpenList; return s;
      }
      int i = h.ArtUids.IndexOf(goal);
      // the game's own rule (the row's bCanEquip): another class or another hero's exclusive one - «Заменить» would only show the game's tip; no click, the reason
      if (i >= 0 && h.ArtGoalFit == 0) { s.Kind = ArtEquipKind.Unfit; s.Why = h.ArtGoalExclusive ? "exclusive" : "class"; return s; }
      if (h.ArtSel == goal) { s.Kind = ArtEquipKind.PressReplace; return s; }
      if (i < 0) {
        // the list is filled after the panel slides in: an empty list is waited for, a filled one without the artifact is the player's filter
        s.Kind = h.ArtUids.Count == 0 ? ArtEquipKind.Wait : ArtEquipKind.NotInList; s.Why = h.ArtUids.Count == 0 ? "list loading" : "not in list";
        s.Filter = s.Kind == ArtEquipKind.NotInList && h.ArtHideWorn == 1 && h.ArtTargetOwner > 0;   // «Скрыть надетое» hides it: off
        return s;
      }
      s.Kind = ArtEquipKind.PickCell; s.Pos = i + 1;
      s.Filter = WantsFilter(h, s.Pos);
      return s;
    }

    /// <summary>«Скрыть надетое» before any scroll: the artifact is free, the filter is off, the artifact lies past the first view and the worn rows
    /// ahead of it are at least a row of the grid (the filter then brings it up by a row or more).</summary>
    public static bool WantsFilter(HeroScreen h, int pos) {
      return h != null && h.ArtHideWorn == 0 && h.ArtTargetOwner == 0 && pos > FirstView && h.ArtWornBefore >= 3;
    }
  }

  public static class ArtEquipFallback {
    /// <summary>How long the guide waits for unreadable artifact data before it shows the geometry-based hint.</summary>
    public const int WaitMs = 2000;

    /// <summary>The waits that come from unreadable data (not from the game: a pop-up of another kind, the list still filling).</summary>
    public static bool Waiting(ArtEquipStep s) {
      return s != null && s.Kind == ArtEquipKind.Wait && (s.Why == "artifact data" || s.Why == "worn artifact unknown" || s.Why == "list loading");
    }

    /// <summary>The step to show after <see cref="WaitMs"/> of waiting: what the screen itself says (the tab, the list being open, the card's selection) without the
    /// artifact data. The tab -> the list button -> the list -> «Заменить»; the list's cells cannot be placed then (no frame, the plate only).</summary>
    public static ArtEquipStep Guess(long goal, HeroScreen h, ArtEquipStep waiting) {
      var s = new ArtEquipStep { Guess = true, Owner = waiting != null ? waiting.Owner : -1, Why = "geometry" };
      if (h == null || !h.FormOk) { s.Kind = ArtEquipKind.Wait; s.Why = "screen"; return s; }
      s.HasWorn = h.ArtWorn != 0;   // unknown (-1) -> the worn card's button; the empty slot's «+» only when the hero is known to wear nothing
      if (h.Tab != ArtEquipGuide.ArtTabType) { s.Kind = ArtEquipKind.ArtTab; return s; }
      if (!h.ArtListOpen) { s.Kind = ArtEquipKind.OpenList; return s; }
      if (h.ArtSel == goal && goal > 0) { s.Kind = ArtEquipKind.PressReplace; return s; }
      int i = h.ArtUids.IndexOf(goal);
      if (i >= 0) { s.Kind = ArtEquipKind.PickCell; s.Pos = i + 1; return s; }
      s.Kind = ArtEquipKind.PickCell; s.Pos = 0;   // the list is open: "pick it" without a frame
      return s;
    }
  }

  /// <summary>Where the frames of the artifact steps go (the site's geometry file; where its copy lacks a key the built-in numbers of ArtFrames stand in).</summary>
  public static class ArtEquipFrames {
    public const string KeyTab = "hero.artifactTab", KeyOpenList = "hero.artOpenList", KeyAdd = "hero.artAdd", KeyList = "hero.artList", KeyReplace = "hero.artReplace",
                        KeyPopup = "popup.equipReplace", KeySell = "hero.artSell", KeyQuick = "hero.artQuickEquip", KeyHideWorn = "hero.artHideWorn";
    /// <summary>Buttons of this screen no click may touch: the sell icon of the selected artifact's card and the worn column's «Быстро надеть».</summary>
    public static readonly string[] NeverKeys = { KeySell, KeyQuick };

    public static string KeyOf(ArtEquipKind k, bool hasWorn) {
      switch (k) {
        case ArtEquipKind.ArtTab: return KeyTab;
        case ArtEquipKind.OpenList: return hasWorn ? KeyOpenList : KeyAdd;
        case ArtEquipKind.PressReplace: return KeyReplace;
        case ArtEquipKind.ConfirmTakeOver: return KeyPopup;
        default: return "";
      }
    }

    /// <summary>The frame of the step; null = none (no rectangle, or the cell cannot be placed because the list is unreadable).</summary>
    public static LocateFrame For(ArtEquipStep st, HeroScreen h, GameGeometry g, double W, double H, double U) {
      if (st == null) return null;
      if (st.Kind == ArtEquipKind.PickCell && st.Pos > 0 && !st.Filter)
        return h != null ? ArtFrames.Cell(g, KeyList, h.ArtList, st.Pos - 1, 3, 116, W, H, U) : null;
      string key = st.Filter && (st.Kind == ArtEquipKind.PickCell || st.Kind == ArtEquipKind.NotInList) ? KeyHideWorn : st.Kind == ArtEquipKind.PickCell ? KeyList : KeyOf(st.Kind, st.HasWorn);   // the filter first: its toggle; a cell that cannot be placed (the list's rows are unreadable): the list's area, with the plate
      if (st.Kind == ArtEquipKind.PickCell && st.Pos <= 0 && !st.Guess && !st.Filter) return null;
      if (key == "") return null;
      var r = ArtFrames.Rect(g, key, W, H, U);
      if (r == null) return null;
      var fr = new LocateFrame { Target = key, Raw = new GeoRect { X = r.X, Y = r.Y, W = r.W, H = r.H } };
      fr.Mark = new LocateMark { Kind = "box", X = (int)Math.Round(r.X), Y = (int)Math.Round(r.Y), W = (int)Math.Round(r.W), H = (int)Math.Round(r.H) };
      return fr;
    }

    /// <summary>The click point (client px) lies inside a button this flow never presses.</summary>
    public static bool OnNeverButton(GameGeometry g, double x, double y, double W, double H, double U) {
      foreach (var key in NeverKeys) {
        var rc = ArtFrames.Rect(g, key, W, H, U);
        if (rc != null && x >= rc.X && x <= rc.X + rc.W && y >= rc.Y && y <= rc.Y + rc.H) return true;
      }
      return false;
    }
  }

  /// <summary>Done is final for a goal: the page keeps the plan open until the site resolves it and re-sends the "art" hint, which must not re-arm the same artifact.</summary>
  public sealed class ArtGoalLatch {
    long doneHero, doneUid;
    public bool IsDone { get; private set; }
    /// <summary>The page's live event carries the step while the goal runs and after it is done, whatever hint the page sends then ("done" too).</summary>
    public static bool LiveShown(bool artPhase, bool latched) { return artPhase || latched; }
    public void MarkDone(long hero, long uid) { IsDone = true; doneHero = hero; doneUid = uid; }
    /// <summary>A new started plan: the latch opens.</summary>
    public void NewPlan() { IsDone = false; doneHero = 0; doneUid = 0; }
    /// <summary>The page sends goal (hero, uid) (uid 0 = none): true = ignore it (the done goal again, or the clearing of it); a different goal opens the latch.</summary>
    public bool Ignores(long hero, long uid) {
      if (!IsDone) return false;
      if (uid <= 0 || hero <= 0 || (hero == doneHero && uid == doneUid)) return true;
      NewPlan(); return false;
    }
  }




  /// <summary>«City first» for the artifact steps: the gear steps (the navigator core) leave the hero screen on the gear tab, the artifact steps go on from
  /// THERE (the hero screen is the flow's own screen: ClickGate CityFirst.EquipSet). The old start-from-the-city rule (a run's first moments) never
  /// applies to it - live 1.6.66.577 (120445-6d76a8): after the last item the pilot pressed the back arrow twice, went through the city and the
  /// "Heroes" button again just to open the artifact tab of the same hero.</summary>
  public static class ArtCityStart {
    /// <summary>True = the artifact phase stands on the hero screen: the city-first flag of the run is settled (no back to the city).</summary>
    public static bool Settled(bool artPhase, ScreenKind kind) { return artPhase && kind == ScreenKind.HeroScreen; }
  }
}
