// Wardsage - the navigator's verified-click primitive (docs/specs/navigator.md 3.2).
//
// An Act is one click, drag or Escape with its own success test. ActClick sends it once (through INavIo.Send, i.e. the click gate), then watches the
// stable snapshots until the test holds (Done), a new layer comes (Diverted), the gate halts, or the time runs out. It never sends twice for one Start.
//
// Pure logic, no Windows APIs.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum ActKind { Click, Drag, Escape }
  public enum ActOutcome { Running, Done, NoEffect, Diverted, WrongScreen, Denied, Halted, Timeout, NotSent }

  public sealed class Act {
    public ClickOwner Owner = ClickOwner.Equip;
    public ClickTarget Target;
    public ActKind Kind;
    /// <summary>Live rect source (RFX.UiFieldReadNow(Form, Field)); "" = the point only.</summary>
    public string Form = "", Field = "";
    /// <summary>Client px; DY: a drag's move.</summary>
    public double X, Y, DY;
    public long Cell;
    /// <summary>The pilot's live-click names (AutoAction.Widget / CellKind / CellId): carried to the host, which presses the live rect.</summary>
    public string Widget; public UiCellKind CellKind; public long CellId;
    /// <summary>The edge's own success test on stable snapshots; null = none (with Fire: done once sent).</summary>
    public Func<Snapshot, bool> Expect;
    public int TimeoutMs;
    /// <summary>A click whose effect is a plain local open (default budget): with the game's memory showing no reaction at all (the same state, input open, never busy, nothing stirred) this long after the send it is NoEffect now,
    /// not after the whole budget (live 582: «graph:hero NoEffect 3516 ms» of a click that was lost). 0 = the budget only.</summary>
    public int QuickMs;
    public string Why = "";
    public Action<ClickRequest> Fill;
    /// <summary>The base form the snapshot must have ("" = any).</summary>
    public string Base = "";
    /// <summary>Layers that may stay above the base while this act runs.</summary>
    public ICollection<string> Keep;
    /// <summary>The layer this act closes: it must be in the stack.</summary>
    public string Closing = "";
    /// <summary>A pilot's action: done once sent (the pilot watches its effect itself); not sent = OnNotSent, nothing counted.</summary>
    public bool Fire;
    /// <summary>A forward edge: needs the window table's order (Sure); exits and closes work without it.</summary>
    public bool Forward;
    /// <summary>The fresh read wants the city's picture / the shown known forms (the host's FreshScreen flags).</summary>
    public bool WantCity, WantKnown;
    public Action OnSent, OnNotSent;
    /// <summary>The edge key for the recovery ladder ("" = none).</summary>
    public string Edge = "";
  }

  public sealed class ActClick {
    public Act Cur { get; private set; }
    public ActOutcome Outcome { get; private set; }
    public string Detail = "";
    public long SentAt { get; private set; }
    /// <summary>The click went out with the item's tip over the screen (passed through): the tip's tap catcher may have taken it - no effect is then no failure of the step.</summary>
    public bool TipAtSend { get; private set; }
    /// <summary>The default effect budget is stretched while the game is busy (BusyGraceMs after the last busy read), at most BusyCapMs after the send.</summary>
    public const int BusyGraceMs = 1500, BusyCapMs = 9000;
    long busyAt; string keyAtSend = "";
    bool sent; long startAt; string layersAtSend = ""; HashSet<string> formsAtSend = new HashSet<string>();

    public bool Active { get { return Cur != null && Outcome == ActOutcome.Running; } }

    public void Start(Act a, long now) { Cur = a; Outcome = ActOutcome.Running; Detail = ""; sent = false; startAt = now; SentAt = 0; }

    /// <summary>The run was paused (the player has the mouse / the game is behind) for <paramref name="dt"/> ms: the click's own clocks stand still.</summary>
    public void Pause(long dt) { startAt += dt; if (SentAt > 0) SentAt += dt; }

    ActOutcome End(ActOutcome o, string why) {
      Outcome = o; Detail = why;
      if (!sent && o != ActOutcome.Done && Cur != null && Cur.OnNotSent != null) Cur.OnNotSent();   // (a pilot's action that did not go out is taken back)
      return o;
    }

    static bool HasBlocking(Snapshot s, ICollection<string> keep) {
      foreach (var l in s.Stack) if (keep == null || !keep.Contains(l.Form)) return true;
      return false;
    }

    public ActOutcome Tick(INavIo io, Snapshot s, long now) {
      if (Cur == null || Outcome != ActOutcome.Running) return Outcome;
      var a = Cur;
      if (io.Gate.Halted(a.Owner)) return End(ActOutcome.Halted, io.Gate.HaltReason(a.Owner));
      if (!sent) return TickBeforeSend(io, s, now);
      // sent: the effect is awaited
      if (s.Confidence >= NavConfidence.NoOrder && !s.State.Held) {
        // a pop-up close whose panel the game's memory says is closed is done at once, whatever the pictures (or the debounce of the snapshot) still show
        if (a.Closing != "" && s.MemClosed.Contains(a.Closing)) return End(ActOutcome.Done, "closed in memory");
        if (a.Expect != null && s.Stable(now) && a.Expect(s)) return End(ActOutcome.Done, "effect");
        // a close whose layer is gone is done even if the next pop-up of a chain is already there
        if (a.Closing != "" && a.Expect != null && s.Stable(now) && a.Expect(s)) return End(ActOutcome.Done, "closed");
        if (s.Stable(now)) foreach (var l in s.Stack) {   // (a layer that is there for one read - a picture flicker - is no divert)
          if (formsAtSend.Contains(l.Form)) continue;
          if (a.Keep != null && a.Keep.Contains(l.Form)) continue;
          return End(ActOutcome.Diverted, "new layer " + l.Form);
        }
      }
      int to = a.TimeoutMs > 0 ? a.TimeoutMs : (io.Slow ? ClickGate.SlowEffectMs : ClickGate.EffectMs);
      // the game is still answering (its input locked by a transition / a load, or the screen read not standing still): the default budget runs from the last moment it moved, up to BusyCapMs from the send
      // (live 538: «graph:hero NoEffect 3531 ms» reset a cold open of the hero grid that showed its screen half a second later)
      bool busy = !io.InputOpen || !s.Stable(now);
      if (busy) busyAt = now;
      // (memory first: the effect is judged on facts, a click the game did not react to at all is not waited for by the clock: the same state key, the input open, no busy read since the send)
      if (a.QuickMs > 0 && a.TimeoutMs == 0 && a.Expect != null && busyAt == 0 && s.Confidence >= NavConfidence.NoOrder && !s.State.Held && s.Stable(now) && now - SentAt >= a.QuickMs && s.StateKey == keyAtSend) {
        io.Gate.CancelWatch(a.Owner);
        return End(ActOutcome.NoEffect, (now - SentAt) + " ms (quick: memory shows no reaction)");
      }
      if (a.TimeoutMs == 0 && now - SentAt > to && now - SentAt <= BusyCapMs && now - busyAt <= BusyGraceMs && a.Expect != null) return ActOutcome.Running;
      if (a.Expect == null) { if (!io.Gate.Pending(a.Owner)) return End(ActOutcome.Done, "no watch"); }
      if (now - SentAt > to) {
        if (a.Expect == null) return End(ActOutcome.Done, "late");

        return End(io.Gate.Pending(a.Owner) ? ActOutcome.Timeout : ActOutcome.NoEffect, (now - SentAt) + " ms");
      }
      return ActOutcome.Running;
    }

    ActOutcome TickBeforeSend(INavIo io, Snapshot s, long now) {
      var a = Cur;
      if (s.Confidence < NavConfidence.NoOrder || !s.Stable(now)) return NotSent(a, "snapshot not stable");
      if (!io.InputOpen) return NotSent(a, "the game's input is locked");
      if (a.Forward && s.Confidence < NavConfidence.Sure) return NotSent(a, "no order for a forward edge");
      if (a.Base != "" && s.BaseForm != a.Base) return End(ActOutcome.WrongScreen, "base " + s.BaseForm + " != " + a.Base);
      if (a.Closing != "") { if (!s.Has(a.Closing)) return End(ActOutcome.WrongScreen, a.Closing + " is gone"); }
      else if (HasBlocking(s, a.Keep)) return End(ActOutcome.WrongScreen, "a layer is over the base: " + s.Top);
      var fresh = io.Fresh(a.WantCity, a.WantKnown);
      if (fresh == null) return NotSent(a, "no fresh read");
      var fs = ScreenReader.Build(fresh, now, io.Picture);
      if (fs.BaseForm != s.BaseForm || (fresh.Order != null && fs.LayerKey() != s.LayerKey()) || (fresh.Order == null && (fs.Stack.Count == 0) != (s.Stack.Count == 0)))
        return NotSent(a, "the fresh read differs: " + fs.LayerKey() + " vs " + s.LayerKey());
      double x = a.X, y = a.Y;
      if (a.Kind == ActKind.Click && a.Form != "" && a.Field != "") {
        var lr = io.LiveRect(a.Form, a.Field);
        if (lr != null && lr.Length >= 4 && lr[2] > 0 && lr[3] > 0) { x = lr[0] + lr[2] / 2; y = lr[1] + lr[3] / 2; }
      }
      a.X = x; a.Y = y;
      var res = io.Send(a, fresh);
      if (res == null || res.Verdict == GateVerdict.Wait) return NotSent(a, res != null ? res.Reason : "not sent");
      if (res.Verdict == GateVerdict.Deny) {
        if (io.Gate.Halted(a.Owner)) return End(ActOutcome.Halted, io.Gate.HaltReason(a.Owner));
        return End(ActOutcome.Denied, res.Reason + ": " + res.Detail);
      }
      sent = true; SentAt = now; busyAt = 0; layersAtSend = s.LayerKey(); keyAtSend = s.StateKey;
      formsAtSend = new HashSet<string>(); foreach (var l in s.Stack) formsAtSend.Add(l.Form);
      TipAtSend = s.HasTip() && a.Closing == "";
      if (a.OnSent != null) a.OnSent();
      if (a.Fire) return End(ActOutcome.Done, "sent");
      return ActOutcome.Running;
    }

    ActOutcome NotSent(Act a, string why) { return End(ActOutcome.NotSent, why); }
  }
}

namespace RealmForge {
  /// <summary>ONE snapshot of the game's fields an atom reads (docs/specs/2026-11-04-game-atoms.md 2-3), taken from the pilot's view of the tick: the selection, the card, the list's live position, the target's live cell, a neighbour cell.
  /// Preconditions, targets and effects of every atom are judged on it and on nothing else (no second read half a tick later).</summary>
  public sealed class GameFacts {
    public long Sel, TipUid, Want, Hero, ScreenHero, Owner, ListSig;
    public bool TipShown, TipKnown, InputOpen = true, LiveGrid, LiveTarget, LiveNb, Foreground = true;
    public int ListFirst = -1, ListLast = -1;
    public double CellY = double.NaN, Scroll = double.NaN, TargetX, TargetY, NbX, NbY, ViewTop = double.NaN, ViewBottom = double.NaN;
    public long NbUid;
    /// <summary>What the game's own pointer says is under the cursor (RayKnown: read; RayNone: its raycast hit nothing, 0x0): after a click on a live cell, nothing under it means the click went into the void.</summary>
    public bool RayKnown, RayNone;
    public static GameFacts Of(AutoView v) {
      return new GameFacts { Sel = v.Sel, TipUid = v.TipUid, Want = v.Target, Hero = v.Hero, ScreenHero = v.ScreenHero, Owner = v.Owner, ListSig = v.ListSig, TipShown = v.TipShown, TipKnown = v.TipKnown,
        InputOpen = v.InputOpen, LiveGrid = v.LiveGrid, LiveTarget = v.LiveTarget, LiveNb = v.LiveNb, Foreground = v.Foreground, ListFirst = v.LiveFirst, ListLast = v.LiveLast, CellY = v.LiveCellY, Scroll = v.LiveScroll,
        TargetX = v.LiveX, TargetY = v.LiveY, NbX = v.LiveNbX, NbY = v.LiveNbY, NbUid = v.LiveNbUid, ViewTop = v.LiveViewTop, ViewBottom = v.LiveViewBottom, RayKnown = v.RayKnown, RayNone = v.RayNone };
    }
    /// <summary>Where the list stands (its first and last made cell, the target's cell row on the screen): a change = the list moved.</summary>
    public string PosKey() { return ListFirst + ":" + ListLast + ":" + (double.IsNaN(CellY) ? "-" : ((int)Math.Round(CellY / 3)).ToString()) + (double.IsNaN(Scroll) ? "" : ":" + (int)Math.Round(Scroll)); }
    /// <summary>What the facts say now, as a key: an atom that failed is not done again while this is the same (the diagnosis did not change).</summary>
    public string DiagKey() { return Sel + "|" + TipUid + "|" + TipShown + "|" + PosKey() + "|" + ListSig + "|" + LiveTarget + (RayKnown ? (RayNone ? "|raycast0" : "|raycast1") : ""); }
    public override string ToString() {
      return "sel=" + Sel + " tip=" + (TipShown ? TipUid.ToString() : "-") + " list=" + ListFirst + ".." + ListLast + " cellY=" + (double.IsNaN(CellY) ? "-" : ((int)CellY).ToString()) + " target=" + (LiveTarget ? "live" : "none") + " owner=" + Owner + (RayKnown && RayNone ? " raycast=0x0" : "");
    }
  }

  public enum AtomResult { Running, Done, NoEffect }

  /// <summary>The atom executor of the pilots (docs/specs/2026-11-04-game-atoms.md 3): precondition -> a target from the game's live rect -> ONE click or drag -> the awaited field with a timeout -> a diagnosis (the line «atom ...» of the log)
  /// and a change of method. The one rule that makes it an executor and not a counter: an atom that ended without its effect is never done again while the facts are the same (a new diagnosis is the only licence to repeat).</summary>
  public sealed class AtomRunner {
    public readonly List<string> Lines = new List<string>();
    string name = "", target = "", eff = ""; long startAt; int timeoutMs; Func<GameFacts, bool> effect; string key = "";
    readonly Dictionary<string, string> failedAt = new Dictionary<string, string>();
    public bool Active { get { return effect != null; } }
    public string Name { get { return name; } }
    /// <summary>Atoms refused for a repeat without a new diagnosis (the tests' and the sim's global check: it stays 0 on a good run).</summary>
    public int Refused;

    void Say(string line) { Lines.Add(line); if (Lines.Count > 60) Lines.RemoveAt(0); }

    /// <summary>The atoms done since the last call, as one text for the host's log line (without the "atom: " tag); "" = none.</summary>
    public string Take() { if (Lines.Count == 0) return ""; var s = string.Join(" | ", Lines.ToArray()); Lines.Clear(); return s; }

    /// <summary>Starts the atom <paramref name="name"/> on <paramref name="targetKey"/> (what it is aimed at: an item, a direction) with its live target text <paramref name="desc"/> (never a point from the screen's fractions), the
    /// effect to await (<paramref name="effName"/>: the field it changes) within <paramref name="timeoutMs"/>. False = refused: it failed before on these very facts (a repeat with no new diagnosis); the caller changes the method.</summary>
    public bool Begin(string name, string targetKey, string desc, string effName, Func<GameFacts, bool> effect, int timeoutMs, GameFacts f, long now, bool guard = true) {
      string k = name + "@" + targetKey, dk = f.DiagKey(), was;
      if (guard && failedAt.TryGetValue(k, out was) && was == dk) {
        Refused++; Say(name + " pre=ok target=" + desc + " REFUSED: it ended without " + effName + " on these same facts (" + f + "), no new diagnosis - another method");
        return false;
      }
      if (this.effect != null) Finish(f, now, "superseded", false);
      this.name = name; this.target = desc; this.eff = effName; this.effect = effect; this.timeoutMs = timeoutMs; startAt = now; key = k;
      return true;
    }

    /// <summary>Called with the tick's facts: the effect seen = Done (a line), the time out = NoEffect (a line with the diagnosis, the repeat guard armed on these facts).</summary>
    public AtomResult Poll(GameFacts f, long now) {
      if (effect == null) return AtomResult.Running;
      if (effect(f)) { Finish(f, now, "Done", true); return AtomResult.Done; }
      if (now - startAt >= timeoutMs) { Finish(f, now, "NoEffect", false); return AtomResult.NoEffect; }
      return AtomResult.Running;
    }

    void Finish(GameFacts f, long now, string how, bool ok) {
      Say(name + " pre=ok target=" + target + " eff=" + eff + " " + (now - startAt) + "ms -> " + how + " diag=" + f);
      if (ok) failedAt.Remove(key); else if (how == "NoEffect") failedAt[key] = f.DiagKey();
      effect = null;
    }

    /// <summary>A diagnosis line of the pilot's own (a regrid, a blocker): into the log with the atoms' lines.</summary>
    public void Note(string line) { Say(line); }

    /// <summary>A precondition that does not hold, as a line of the log (nothing is done; the caller waits for the field or changes the method).</summary>
    public void PreFails(string name, string why, GameFacts f) { string l = name + " pre=" + why + " (" + f + ")"; if (Lines.Count == 0 || Lines[Lines.Count - 1] != l) Say(l); }

    /// <summary>The action of the last Begin did not go out (the gate waited): it is not awaited and counts for nothing.</summary>
    public void Abort() { effect = null; }

    /// <summary>A click from the layout's / row math's point (no live rect of its target - the old way, only where the game's cells are not read at all): never silent.</summary>
    public void Geo(string name, double x, double y) { Say(name + " pre=ok target=geo[" + (int)x + "," + (int)y + "] src=geo (no live rect: the game's cells are not read)"); }

    /// <summary>A new run: nothing is awaited, the guard is cleared.</summary>
    public void Clear() { effect = null; failedAt.Clear(); }
  }
}
