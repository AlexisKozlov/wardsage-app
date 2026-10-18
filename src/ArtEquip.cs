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
    ConfirmTakeOver   // the pop-up «<artifact> в ячейке героя: <имя>» is open: its «Заменить»
  }

  public sealed class ArtEquipStep {
    public ArtEquipKind Kind;
    public int Pos;              // PickCell: the 1-based place in the list
    public bool HasWorn;         // the hero wears an artifact now (the card says «Заменить», not «Надеть»)
    public long Owner = -1;      // iHeroId of the artifact now (-1 = unknown)
    public string Why = "";
    /// <summary>A hint worked out from the screen's geometry alone: the artifact data could not be read (the app never clicks on a guess).</summary>
    public bool Guess;
    public string Key {
      get { return (Guess ? "guess:" : "") + Kind + (Kind == ArtEquipKind.PickCell ? ":" + Pos : "") + (Kind == ArtEquipKind.OpenList || Kind == ArtEquipKind.PressReplace ? (HasWorn ? ":worn" : ":empty") : ""); }
    }
    /// <summary>The step name the page gets (live.art.step).</summary>
    public string Name {
      get {
        switch (Kind) {
          case ArtEquipKind.Hero: return "hero";
          case ArtEquipKind.Done: return "done";
          case ArtEquipKind.ArtTab: return "tab";
          case ArtEquipKind.OpenList: return HasWorn ? "open" : "openAdd";
          case ArtEquipKind.PickCell: return "pick";
          case ArtEquipKind.NotInList: return "missing";
          case ArtEquipKind.PressReplace: return HasWorn ? "replace" : "equip";
          case ArtEquipKind.ConfirmTakeOver: return "confirm";
          default: return "wait";
        }
      }
    }
  }

  public static class ArtEquipGuide {
    /// <summary>Form_CharactorMain.m_LastType of the «Артефакт» tab (gear is 3).</summary>
    public const int ArtTabType = 7;

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
      if (h.ArtSel == goal) { s.Kind = ArtEquipKind.PressReplace; return s; }
      int i = h.ArtUids.IndexOf(goal);
      if (i < 0) {
        // the list is filled after the panel slides in: an empty list is waited for, a filled one without the artifact is the player's filter
        s.Kind = h.ArtUids.Count == 0 ? ArtEquipKind.Wait : ArtEquipKind.NotInList; s.Why = h.ArtUids.Count == 0 ? "list loading" : "not in list";
        return s;
      }
      s.Kind = ArtEquipKind.PickCell; s.Pos = i + 1;
      return s;
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
                        KeyPopup = "popup.equipReplace", KeySell = "hero.artSell", KeyQuick = "hero.artQuickEquip";
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
      if (st.Kind == ArtEquipKind.PickCell && st.Pos > 0)
        return h != null ? ArtFrames.Cell(g, KeyList, h.ArtList, st.Pos - 1, 3, 116, W, H, U) : null;
      string key = st.Kind == ArtEquipKind.PickCell ? KeyList : KeyOf(st.Kind, st.HasWorn);   // a cell that cannot be placed (the list's rows are unreadable): the list's area, with the plate
      if (st.Kind == ArtEquipKind.PickCell && st.Pos <= 0 && !st.Guess) return null;
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

  public enum ArtEquipTodo {
    Nothing,      // no goal
    Hint,         // the player's step (or one the app does not click)
    Tab,          // the hero screen's «Артефакт» tab
    OpenList,     // the worn card's button / the empty slot's «+»
    Cell,         // the artifact's cell: the click or the drag that brings it into view
    Replace,      // the card's «Заменить» / «Надеть»
    Confirm       // the pop-up's «Заменить»
  }

  /// <summary>What the host sees this tick of the artifact steps.</summary>
  public sealed class ArtEquipView {
    public long NowMs;
    public bool Foreground, UserBusy, UserClicked;
    public double W, H, U;
    public ArtEquipStep Step;
    public HeroScreen Hero;
    public GameGeometry Geometry;
    /// <summary>The player's auto confirm setting: «Заменить» (the card's and the pop-up's) is pressed by the app only with it.</summary>
    public bool AutoConfirm;
    /// <summary>The card's button is seen on the picture as the game's blue button (so the click cannot land on a neighbour).</summary>
    public bool ReplaceBtnSeen;
  }

  public sealed class ArtEquipMove {
    public AutoAction Action = AutoAction.Nothing;
    /// <summary>work | paused | background | hint | stopped</summary>
    public string State = "hint";
    public string Why = "";
    public ArtEquipTodo Todo = ArtEquipTodo.Nothing;
    public bool Drag { get { return Action.Kind == AutoKind.Drag; } }
  }

  /// <summary>The auto click for the artifact steps: opens the tab and the list, scrolls to the artifact and selects its cell; with the player's auto confirm also
  /// presses «Заменить» of the card and of the pop-up. Never the sell icon, «Быстро надеть», «Снять», «Улучшить». Pure decision logic.</summary>
  public sealed class ArtEquipPilot {
    public const int UserPauseMs = 4000, AfterTabMs = 1500, AfterOpenMs = 2200, AfterConfirmMs = 2500, SettleMs = 700;
    public const int MaxStepClicks = 4, MaxActions = 60, MaxClicks = 12, MaxDrags = 40, MaxWorkMs = 4 * 60 * 1000;
    public const double MinDragPx = 24;

    string latched = "";
    long waitUntil, pauseUntil, workSince;
    ArtEquipTodo kind; int kindClicks, actions, clicks, drags;
    double dragFrom = double.NaN; int dragStill;
    public string Trace = "";

    public void Reset() { latched = ""; waitUntil = pauseUntil = workSince = 0; kind = ArtEquipTodo.Nothing; kindClicks = actions = clicks = drags = 0; dragFrom = double.NaN; dragStill = 0; Trace = ""; }
    public void Halt(string why) { if (latched == "") latched = why; }
    public bool Stopped { get { return latched != ""; } }
    public string Reason { get { return latched; } }

    /// <summary>What the pilot would do for the step (no timing).</summary>
    public static ArtEquipTodo Decide(ArtEquipView v, out string why) {
      why = "";
      var st = v.Step;
      if (st == null) return ArtEquipTodo.Nothing;
      if (st.Guess) { why = "guess"; return ArtEquipTodo.Hint; }
      var h = v.Hero;
      if (h == null || !h.FormOk) { why = "screen"; return ArtEquipTodo.Hint; }
      switch (st.Kind) {
        case ArtEquipKind.ArtTab: return ArtEquipTodo.Tab;
        case ArtEquipKind.OpenList: return ArtEquipTodo.OpenList;
        case ArtEquipKind.PickCell:
          if (h.ArtList == null || !h.ArtList.Has) { why = "list"; return ArtEquipTodo.Hint; }
          return ArtEquipTodo.Cell;
        case ArtEquipKind.PressReplace:
          if (!v.AutoConfirm) { why = "player"; return ArtEquipTodo.Hint; }
          if (!v.ReplaceBtnSeen) { why = "button"; return ArtEquipTodo.Hint; }
          return ArtEquipTodo.Replace;
        case ArtEquipKind.ConfirmTakeOver:
          if (!v.AutoConfirm) { why = "player"; return ArtEquipTodo.Hint; }
          return ArtEquipTodo.Confirm;
        case ArtEquipKind.NotInList: why = "list"; return ArtEquipTodo.Hint;
        default: why = "screen"; return ArtEquipTodo.Hint;
      }
    }

    static AutoAction Center(GeoRect r) { return new AutoAction { Kind = AutoKind.Click, X = r.X + r.W / 2, Y = r.Y + r.H / 2 }; }

    public ArtEquipMove Step(ArtEquipView v) {
      var m = new ArtEquipMove();
      if (latched != "") { m.State = "stopped"; m.Why = latched; return m; }
      string why;
      var todo = Decide(v, out why);
      if (todo == ArtEquipTodo.Nothing || todo == ArtEquipTodo.Hint) { m.State = "hint"; m.Why = why; kind = ArtEquipTodo.Nothing; kindClicks = 0; return m; }
      if (v.UserClicked) pauseUntil = v.NowMs + UserPauseMs;
      if (!v.Foreground) { m.State = "background"; workSince = 0; return m; }
      if (v.UserBusy || v.NowMs < pauseUntil) { m.State = "paused"; workSince = 0; return m; }
      m.State = "work"; m.Todo = todo;
      if (workSince == 0) workSince = v.NowMs;
      if (v.NowMs - workSince > MaxWorkMs || actions >= MaxActions) return Abort(m, "stuck");
      if (v.NowMs < waitUntil) return m;
      if (todo != kind) { kind = todo; kindClicks = 0; }
      if (todo == ArtEquipTodo.Cell) return Cell(m, v);
      if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
      var step = v.Step;
      string key = ArtEquipFrames.KeyOf(step.Kind, step.HasWorn);
      var r = ArtFrames.Rect(v.Geometry, key, v.W, v.H, v.U);
      if (r == null) return Abort(m, "screen");
      return Click(m, v, Center(r), todo == ArtEquipTodo.Tab ? AfterTabMs : todo == ArtEquipTodo.OpenList ? AfterOpenMs : AfterConfirmMs, key);
    }

    // the artifact's card in the list the game scrolls: out of view (or cut by the edge) -> drag the list toward it; in view -> one click on its centre (it only selects)
    ArtEquipMove Cell(ArtEquipMove m, ArtEquipView v) {
      var f = ArtEquipFrames.For(v.Step, v.Hero, v.Geometry, v.W, v.H, v.U);
      var l = v.Hero != null ? v.Hero.ArtList : null;
      if (f == null || l == null || !l.Has || f.ViewH <= 0) return Abort(m, "screen");
      double sy = l.ScrollY;
      if (!double.IsNaN(dragFrom)) {
        if (Math.Abs(sy - dragFrom) < 0.5) { if (++dragStill >= 2) return Abort(m, "lost"); } else dragStill = 0;
        dragFrom = double.NaN;
      }
      double cardH = 110 * v.U / GameGeometry.CanvasH;
      if (f.Mark.Kind == "box" && f.Mark.H >= 0.6 * cardH) {
        if (++kindClicks > MaxStepClicks) return Abort(m, "stuck");
        if (++clicks > MaxClicks) return Abort(m, "lost");
        return Click(m, v, new AutoAction { Kind = AutoKind.Click, X = f.Mark.X + f.Mark.W / 2.0, Y = f.Mark.Y + f.Mark.H / 2.0 }, AfterOpenMs, "artifact");
      }
      double dy = Math.Max(-0.8 * f.ViewH, Math.Min(0.8 * f.ViewH, f.ScrollPx));   // content follows the pointer: + = later rows move down
      if (Math.Abs(dy) < MinDragPx) dy = dy < 0 ? -MinDragPx : MinDragPx;
      if (++drags > MaxDrags) return Abort(m, "lost");
      actions++; dragFrom = sy;
      waitUntil = v.NowMs + SettleMs + 300;
      Say("scroll " + (int)dy);
      m.Action = new AutoAction { Kind = AutoKind.Drag, X = f.ViewX + f.ViewW / 2, Y = f.ViewY + f.ViewH / 2 - dy / 2, DY = dy };
      return m;
    }

    ArtEquipMove Click(ArtEquipMove m, ArtEquipView v, AutoAction a, int waitMs, string what) {
      actions++; waitUntil = v.NowMs + waitMs;
      Say(what + " " + (int)a.X + "," + (int)a.Y);
      m.Action = a;
      return m;
    }

    ArtEquipMove Abort(ArtEquipMove m, string why) {
      latched = why; m.State = "stopped"; m.Why = why; m.Action = AutoAction.Nothing;
      Say("stop: " + why);
      return m;
    }

    void Say(string t) { Trace = Trace == "" ? t : Trace + "; " + t; }
  }
}
