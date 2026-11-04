// Wardsage - the navigator executor (docs/specs/navigator.md 3.6): INavIo (what the host gives), NavMemory, and the Navigator that ticks the planner,
// the verified click, the recovery ladder and the watchdog. A run ends done or halted with a named reason; it never waits for ever.
//
// Pure logic, no Windows APIs.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public interface INavIo {
    long Now { get; }
    bool Front { get; }
    bool UserBusy { get; }
    double W { get; }
    double H { get; }
    double U { get; }
    bool Slow { get; }
    /// <summary>The game's input is open: no UILocker slot, no stack transition or animation (RFX.NavInputOpen). Clicks and Escape wait for it instead of fixed pauses; true when it cannot be read.</summary>
    bool InputOpen { get; }
    GameGeometry Geo { get; }
    ScreenState Fresh(bool wantCity, bool wantKnown);
    double[] LiveRect(string form, string field);
    /// <summary>The live rect of a pilot button of a form (the offer pop-up's ×); null = none fresh.</summary>
    double[] LiveFor(ClickTarget t, string form);
    /// <summary>"heroesButton" | "heroFilter" | "gearFilterMain" | "gearFilterSide" | "tipShown".</summary>
    bool Picture(string what);
    /// <summary>Builds the request, gate.Check, Arm, send. Allow = sent; Wait = not sent; Deny (maybe a halt).</summary>
    GateResult Send(Act a, ScreenState fresh);
    ClickGate Gate { get; }
    /// <summary>Our own hint / mask windows stop covering the game's looks for a few seconds (hidden, not just excluded from captures): a gate wait that our own UI causes, or a reset, never waits on them
    /// (docs/specs/2026-11-04-core-any-scenario.md D6). The host redraws them afterwards.</summary>
    void HideOwn();
    /// <summary>What the game's Escape would do now, from the UI stacks (EscapePredictor.Kind: "close" | "block" | "swallowed" | "exit-dialog" | "none" ...); "" = not known (the probe is off). The recovery ladder sends an Escape of its own only on "close".</summary>
    string EscapeKind { get; }
    /// <summary>The game's own hotkey of a screen ("heroes" = 'C', ID_FORM_CHARACTORMAIN) as a fallback for a click that does nothing; false = not sent. Behind the config flag NavHotkey (off), the host decides.</summary>
    bool Hotkey(string name);
    void Log(string line);
    void Problem(string kind, string message);
  }

  public sealed class NavMemory {
    public readonly Dictionary<string, int> Rung = new Dictionary<string, int>();
    public readonly Dictionary<string, int> Resets = new Dictionary<string, int>();
    public int TotalResets, Sweeps;
    public readonly Dictionary<string, long> NextAt = new Dictionary<string, long>();
    public readonly Dictionary<string, long> SeenAt = new Dictionary<string, long>();
    public readonly Dictionary<string, long> WaitSince = new Dictionary<string, long>();
    public readonly LiveStill OfferLive = new LiveStill(); public string OfferLiveForm = "";
    public long NoOrderSince = -1;
    /// <summary>A layer closed and back (docs/specs/2026-11-04-core-any-scenario.md D4): when each closed layer's close was counted done, the strikes of the ones that came back within ReturnWindowMs, and the pixel
    /// layers taken for phantoms (ignored until the time).</summary>
    public readonly Dictionary<string, long> ClosedAt = new Dictionary<string, long>(), PhantomUntil = new Dictionary<string, long>();
    public readonly Dictionary<string, int> Strikes = new Dictionary<string, int>();
    public int Phantoms;
    public const int ReturnWindowMs = 3000, PhantomMs = 10000, MaxPhantoms = 3;
    /// <summary>The keep set with the phantom layers added: they are tolerated, never closed again.</summary>
    public ICollection<string> WithPhantoms(ICollection<string> keep, long now) {
      HashSet<string> add = null;
      foreach (var kv in PhantomUntil) if (kv.Value > now) { if (add == null) add = new HashSet<string>(); add.Add(kv.Key); }
      if (add == null) return keep;
      if (keep != null) foreach (var k in keep) add.Add(k);
      return add;
    }
    /// <summary>One try per key and gap: true = go ahead.</summary>
    public bool Due(string key, long now, int gapMs) {
      long t; if (NextAt.TryGetValue(key, out t) && now < t) return false;
      NextAt[key] = now + gapMs; return true;
    }
    public bool Blocked(string key, long now) { long t; return NextAt.TryGetValue(key, out t) && now < t; }
    public void Hold(string key, long until) { NextAt[key] = until; }
    public long Seen(string form, long now) { long t; if (!SeenAt.TryGetValue(form, out t)) { SeenAt[form] = now; t = now; } return t; }
    readonly Dictionary<string, long> lastIn = new Dictionary<string, long>();
    /// <summary>Forget a layer that has been gone for a few seconds (a flicker of the read does not start its clock over).</summary>
    public void Prune(Snapshot s, long now) {
      foreach (var l in s.Stack) lastIn[l.Form] = now;
      var gone = new List<string>();
      foreach (var k in SeenAt.Keys) { long t; if (!s.Has(k) && (!lastIn.TryGetValue(k, out t) || now - t > 3000)) gone.Add(k); }
      foreach (var k in gone) { SeenAt.Remove(k); WaitSince.Remove(k); Rung.Remove(k); lastIn.Remove(k); }
    }
  }

  public sealed class Navigator {
    /// <summary>Seconds, not minutes (docs/specs/2026-11-04-core-any-scenario.md D5): a step that shows no change of the screen and no progress mark of its action for StepStallMs is reset; NoProgressMs is the watchdog behind it
    /// (new states only: a flicker between two known ones is not progress); a doomed run ends in a named halt within about 60-90 s.</summary>
    public const int MaxResets = 2, MaxResetsTotal = 5, MaxSweeps = 14, StepStallMs = 8000, RungMs = 2000, MaxRungFails = 4, LadderCapMs = 12000, HideOwnMs = 3000, GateWaitResetMs = 8000, BlindGraceMs = 12000, NoProgressMs = 10000, RunCapMs = 120000, WaitCapMs = 90000, OrderUnreadMs = 15000;
    /// <summary>idle | work | at_target | done | halted</summary>
    public string State = "idle";
    public string HaltWhy = "";
    /// <summary>What the navigator is doing or waiting for now (for the status line and the log).</summary>
    public string Say = "";
    public int Clicks, Recoveries;
    /// <summary>The navigator may press the game's heroes hotkey when the click on the city's «Герои» does nothing (the config flag NavHotkey; off by default).</summary>
    public bool HotkeyFallback;
    /// <summary>The run is on its way back to the city: the recovery ladder is its clock (RungMs a rung, LadderCapMs).</summary>
    public bool OnLadder { get { return popCity; } }
    /// <summary>How long (within BlindGraceMs) the screen could not be read (held / partial reads, a picture that does not stand still) since the run last moved: the stall clocks stand still meanwhile - nothing can be verified, nothing is the step's fault. Past the grace they run again.</summary>
    public long BlindMs { get { return blindMs; } }
    /// <summary>The latest progress mark of the pilots (IAtTargetAction.ProgressAt): what the sim's stall check counts besides the game's own changes.</summary>
    public long ProgressAt { get { long m = 0; foreach (var a in actions) if (a.ProgressAt > m) m = a.ProgressAt; return m; } }
    public readonly ClickOwner Owner;
    public NavTarget Target { get { return target; } }

    NavTarget target; IList<IAtTargetAction> actions = new List<IAtTargetAction>();
    NavMemory mem = new NavMemory(); readonly ActClick act = new ActClick();
    long lastDt, popSince, startedAt = -1, lastTickAt = -1, pausedMs, lastProgress, stepProgress, blindMs, gateSince = -1; bool ownHidden, resetTick, hideKeep; long hideAt; string ladKey = ""; int ladDepth; long ladSince; int ladFails; Snapshot lastSnap; bool hotkeyTried; string lastSig = "", logged = "", curEdge = "";
    bool lastHeld, popCity, needResume, tipSeen, unknownWatch; string unknownWhy = "";
    readonly List<string> recent = new List<string>();
    public Navigator(ClickOwner owner) { Owner = owner; }

    /// <summary>Run a recipe (src/NavRecipe.cs) with its parameters: the hero and the slot of the equip recipe. The pilots (actions) are the ones its action steps name.</summary>
    public void SetRecipe(Recipe r, IDictionary<string, long> args, IList<IAtTargetAction> acts) {
      long hero = 0, slot = -1; args.TryGetValue("hero", out hero); if (!args.TryGetValue("slot", out slot)) slot = -1;
      SetTarget(new NavTarget { Goal = r.Id == "city" ? NavGoal.City : NavGoal.HeroEquip, Hero = hero, Slot = (int)slot, Owner = Owner, RecipeId = r.Id }, acts);
    }

    public void SetTarget(NavTarget t, IList<IAtTargetAction> acts) {
      actions = acts ?? new List<IAtTargetAction>();
      if (target != null && t.Key() == target.Key()) { target = t; return; }
      target = t; mem = new NavMemory(); act.Start(null, 0); popCity = false; needResume = true; hotkeyTried = false; hideKeep = false; ladKey = ""; ladFails = 0;
      State = "work"; HaltWhy = ""; Say = ""; startedAt = -1; pausedMs = 0; Clicks = 0; Recoveries = 0; curEdge = ""; logged = "";
    }
    /// <summary>A new run of the player (the pilots restart): start over even for the same target.</summary>
    public void Restart() { target = null; State = "idle"; HaltWhy = ""; }
    public void Clear() { target = null; State = "idle"; HaltWhy = ""; Say = ""; }

    void Halt(INavIo io, string why) {
      if (State == "halted") return;
      State = "halted"; HaltWhy = why; Say = "halt " + why;
      io.Log("nav: halt " + why);
      if (!why.StartsWith("gate_") && !io.Gate.Halted(Owner)) io.Problem("pilot.nav_stuck", "navigator stopped: " + why);
    }

    public void Tick(INavIo io, Snapshot s) {
      ClickGate.NavLateParts = true;
      try { TickCore(io, s); } finally { ClickGate.NavLateParts = false; }
    }

    void TickCore(INavIo io, Snapshot s) {
      long now = io.Now; resetTick = false;
      long dt = lastTickAt >= 0 ? Math.Min(now - lastTickAt, 5000) : 0; lastTickAt = now; lastDt = dt;
      if (target == null || State == "idle" || State == "halted" || State == "done") return;
      if (startedAt < 0) { startedAt = now; lastProgress = now; stepProgress = now; }
      if (needResume) { needResume = false; if (!io.Gate.Halted(Owner)) io.Gate.Resume(Owner); }
      if (io.Gate.Halted(Owner)) { Halt(io, "gate_" + io.Gate.HaltReason(Owner)); return; }
      if (io.UserBusy || !io.Front) {   // a pause is not a failure and does not count toward the no-progress clock
        Say = io.UserBusy ? "paused" : "background"; pausedMs += dt; lastProgress += dt; stepProgress += dt; return;
      }
      if (s == null) return;
      if ((s.Confidence < NavConfidence.NoOrder || !s.Stable(now)) && blindMs < BlindGraceMs) { blindMs += dt; lastProgress += dt; stepProgress += dt; pausedMs += dt; }   // (see BlindMs; the run cap counts working time)
      if (s.HasTip()) tipSeen = true; else if (tipSeen) { tipSeen = false; io.Gate.ResetTipEscapes(Owner); }
      mem.Prune(s, now); lastSnap = s;
      LadderWatch(io, s, now, dt);
      if (State == "halted") return;
      foreach (var l in s.Stack) if (l.Role == LayerRole.Wait) { lastProgress = stepProgress = now; break; }   // (a loading / result screen the game closes by itself is the game's wait: its own cap, WaitCapMs, ends it)
      WatchReturns(io, s, now);
      if (State == "halted") return;
      foreach (var a in actions) if (a.ProgressAt > stepProgress) { stepProgress = a.ProgressAt; if (stepProgress > lastProgress) lastProgress = stepProgress; blindMs = 0; }   // (a pilot's own mark: a click that was only sent is not one)
      if (s.StateKey != lastSig) {   // a new state is progress; a state seen a moment ago (a flicker between two) is not
        lastSig = s.StateKey; gateSince = -1; ownHidden = false;
        if (!recent.Contains(lastSig)) {
          lastProgress = now;
          if (s.Confidence != NavConfidence.Held && !lastHeld) { stepProgress = now; blindMs = 0; }   // (a held read - the memory reads failing - that comes back with details is no change of the game)
        }
        recent.Add(lastSig); if (recent.Count > 8) recent.RemoveAt(0);
      }
      lastHeld = s.Confidence == NavConfidence.Held;
      // the window table's order: 15 s without it is a stop with a reason
      if (s.Confidence == NavConfidence.NoOrder) { if (mem.NoOrderSince < 0) mem.NoOrderSince = now; if (now - mem.NoOrderSince > OrderUnreadMs) { Halt(io, "order_unread"); return; } }
      else mem.NoOrderSince = -1;
      if (act.Active) {
        var oc = act.Tick(io, s, now);
        if (oc == ActOutcome.Running) return;
        Handle(io, oc, now);
        if (State == "halted" || resetTick) return;   // (a reset is a clean slate: nothing else runs in this tick)
      }
      if (hideKeep && now - hideAt > 10000) { hideAt = now; io.HideOwn(); }   // (once our windows had to be hidden they stay hidden for the run)
      if (State == "work" && now - startedAt - pausedMs > RunCapMs) { Halt(io, "run_cap"); return; }
      if (popCity && !(s.Kind == ScreenKind.City && s.Stack.Count == 0) && now - popSince > 2 * LadderCapMs) { LadderHalt(io, s, "the way back to the city takes " + (now - popSince) / 1000 + " s"); return; }
      // (on the way back to the city the ladder is the clock: RungMs a rung, LadderCapMs without a change)
      if (!popCity && State == "work" && now - lastProgress > NoProgressMs) {
        Reset(io, "watchdog", "no_progress:" + (Say == "" ? "?" : Say), now); lastProgress = now;
        return;   // (a reset is a clean slate: nothing else runs in this tick)
      } else if (!popCity && (State == "work" || State == "at_target") && now - stepProgress > StepStallMs) {
        Reset(io, "stall:" + (stepKey == "" ? "?" : stepKey), "stall:" + (Say == "" ? "?" : Say), now);
        return;
      }
      var tgt = target;
      if (popCity) {
        if (s.Kind == ScreenKind.City && s.Stack.Count == 0 && s.Stable(now)) popCity = false;
        else tgt = new NavTarget { Goal = NavGoal.City, Owner = Owner };
      }
      ICollection<string> atKeep = null, seekKeep = null;
      foreach (var a in actions) {
        if (a.Name == "hero") seekKeep = a.Keep;
        else if (atKeep == null && a.Wants(s, tgt)) atKeep = a.Keep;
      }
      var step = NavPlanner.Plan(s, tgt, atKeep, mem, io, seekKeep);
      Note(io, step);
      if (step.Kind != StepKind.AtTarget && State == "at_target") State = "work";
      switch (step.Kind) {
        case StepKind.Wait:
          Say = step.Why;
          if (popCity && step.Why.StartsWith("back to the city not due")) GateWait(io, step.Why, now);
          return;
        case StepKind.None: return;
        case StepKind.Halt: Halt(io, step.Why); return;
        case StepKind.Reset: Reset(io, step.Edge != "" ? step.Edge : "step", step.Why, now); return;
        case StepKind.Act:
          StartAct(io, s, step.Act, now); return;
        case StepKind.AtTarget:
          if (tgt.Goal == NavGoal.City && popCity) { popCity = false; lastProgress = stepProgress = now; blindMs = 0; return; }   // (the way back to the city is done: the real target goes on)
          if (tgt.Goal == NavGoal.City) {
            State = "done"; Say = "done"; io.Log("nav: done City " + (now - startedAt) + "ms clicks=" + Clicks + " recoveries=" + Recoveries); return;
          }
          RunActions(io, s, tgt, step, now); return;
      }
    }

    string stepKey = "";
    /// <summary>The recovery ladder of the trip back to the city (docs/specs/2026-11-04-core-any-scenario.md D7; the rungs are the modal sweep, the Escape the stacks predict to close, the back arrow / map exit, in the order the planner
    /// and the gate give them): while the run is on its way back to the city (popCity) every rung gets RungMs to change the stack depth or the base form; a rung that does not is a failure, MaxRungFails of them - or
    /// LadderCapMs without a change - end the run with the named stop reset_failed:&lt;top form&gt;. Not a failed step and not another reset: the reset is the ladder.</summary>
    void LadderWatch(INavIo io, Snapshot s, long now, long dt) {
      if (!popCity) { ladKey = ""; ladFails = 0; return; }
      foreach (var l in s.Stack) if (l.Role == LayerRole.Wait) { ladSince = now; ladFails = 0; popSince += dt; return; }   // (a result / loading screen the game closes by itself: its own wait, WaitCapMs)
      // (progress = fewer layers, or another base form that is a known screen; a read that flickers between known and unknown is not)
      string key = s.BaseForm + "|" + s.Stack.Count;
      if (ladKey == "") { ladKey = key; ladSince = now; ladFails = 0; ladDepth = s.Stack.Count; }
      else if (key != ladKey) {
        bool better = s.Stack.Count < ladDepth || (s.Kind != ScreenKind.Unknown && s.BaseForm != "" && !ladKey.StartsWith(s.BaseForm + "|"));
        ladKey = key; if (s.Stack.Count < ladDepth) ladDepth = s.Stack.Count;
        if (better) { ladSince = now; ladFails = 0; }
        return;
      }
      if (s.Confidence < NavConfidence.NoOrder || !s.Stable(now)) { ladSince += dt; popSince += dt; return; }   // (an unreadable screen is not the ladder's failure)
      if (now - ladSince > LadderCapMs) LadderHalt(io, s, "no change in " + LadderCapMs / 1000 + " s");
    }

    void LadderFail(INavIo io, string edge, string how, long now) {
      ladFails++;
      io.Log("nav: ladder rung " + edge + " failed (" + how + ": no change in " + RungMs / 1000 + " s) " + ladFails + "/" + MaxRungFails);
      if (ladFails >= MaxRungFails && lastSnap != null) LadderHalt(io, lastSnap, ladFails + " rungs");
    }

    void LadderHalt(INavIo io, Snapshot s, string why) {
      string top = s.Stack.Count > 0 ? s.Stack[0].Form : s.BaseForm;
      io.Log("nav: the way back to the city is closed (" + why + ")");
      Halt(io, (s.Kind == ScreenKind.Unknown ? "unknown_screen:" : "reset_failed:") + (top == "" ? "?" : top));   // (a screen nobody knows keeps its old name)
    }

    /// <summary>A gate wait (a click not sent, the way back to the city not due) on the same step: after HideOwnMs our own windows are hidden if they are the cause (the host says so with "own"), after
    /// GateWaitResetMs it is a failed step whatever the cause (a reset, which hides them anyway). A wait is never a failure by itself, but it never lasts for ever.</summary>
    void GateWait(INavIo io, string why, long now) {
      if (gateSince < 0) gateSince = now;
      // (the host could not send because it could not read - the forms or a look are unread, no verdict of the gate: the same blind time as an unreadable screen, within its grace)
      if (why == "not sent" && blindMs < BlindGraceMs) { blindMs += lastDt; lastProgress += lastDt; stepProgress += lastDt; gateSince += lastDt; pausedMs += lastDt; }
      long d = now - gateSince;
      if (d > HideOwnMs && !ownHidden && why != null && why.StartsWith("own")) { ownHidden = true; io.Log("nav: gate wait " + d + " ms (" + why + ") - our own windows hidden"); hideKeep = true; hideAt = now; io.HideOwn(); }
      if (d > GateWaitResetMs) {
        string wh = why ?? "?"; gateSince = -1;
        Reset(io, "gate:" + (stepKey == "" ? "?" : stepKey), "gate_wait:" + wh, now);
      }
    }

    void Note(INavIo io, NavStep step) {
      string k = step.Kind + "|" + step.Edge;
      stepKey = step.Edge != "" ? step.Edge : step.Kind.ToString();
      Say = step.Edge != "" ? step.Edge : step.Why;
      if (k == logged || step.Kind == StepKind.Wait) return;
      logged = k;
      io.Log("nav: plan " + (target != null ? target.Goal.ToString() : "?") + " step=" + step.Edge + " why=" + step.Why);
    }

    void RunActions(INavIo io, Snapshot s, NavTarget tgt, NavStep step, long now) {
      bool seek = step.Edge.StartsWith("seek:");
      State = seek ? "work" : "at_target";
      tgt.Resets = mem.TotalResets;
      foreach (var a in actions) {
        if (seek ? a.Name != step.Edge.Substring(5) : (a.Name == "hero" || !a.Wants(s, tgt))) continue;
        var r = a.Step(s, tgt, io, now);
        if (r == null) continue;
        if (r.Kind == StepKind.Halt) { Halt(io, r.Why); return; }
        if (r.Kind == StepKind.Reset) { Reset(io, "act:" + a.Name, r.Why, now); return; }
        if (r.Kind == StepKind.Act) { StartAct(io, s, r.Act, now); return; }
        Say = r.Why; return;
      }
      if (!seek) Say = "at target";
    }

    void StartAct(INavIo io, Snapshot s, Act a, long now) {
      curEdge = a.Edge;
      if (popCity && a.TimeoutMs == 0 && (a.Edge.StartsWith("pop:") || a.Edge.StartsWith("modal:"))) a.TimeoutMs = RungMs;   // (each rung of the way back gets RungMs to show a change)
      if (mem.PhantomUntil.Count > 0) a.Keep = mem.WithPhantoms(a.Keep, now);
      act.Start(a, now);
      var oc = act.Tick(io, s, now);
      if (oc != ActOutcome.Running) Handle(io, oc, now);
    }

    /// <summary>Verify-or-reset (docs/specs/navigator.md 6b.1): a step either shows its effect or the run goes back to the city (the modal sweep and the exits are the same
    /// pop ladder as ever) and re-enters from the first step that does not hold. A step that fails MaxResets times, or MaxResetsTotal resets in a run: a named stop.</summary>
    void Reset(INavIo io, string edge, string why, long now) {
      // a pop-up came over the step (Diverted) or the screen is not the step's (WrongScreen): the modal sweep that opens every plan clears it and the step runs again from
      // where the game is - no trip to the city. Counted apart (MaxSweeps a run), not as a failed step.
      if (why == "Diverted" || why == "WrongScreen") {
        if (++mem.Sweeps > MaxSweeps) { Halt(io, "recipe_stuck:" + edge + ":" + why + " x" + mem.Sweeps); return; }
        io.Log("nav: sweep " + edge + " (" + why + ") #" + mem.Sweeps);
        return;
      }
      int n; mem.Resets.TryGetValue(edge, out n); n++; mem.Resets[edge] = n; mem.TotalResets++; Recoveries++;
      bool clock = edge == "watchdog" || edge.StartsWith("stall:");
      if (clock && why.StartsWith("no_progress:unknown screen")) { unknownWatch = true; unknownWhy = why.Substring("no_progress:unknown screen".Length).Trim(); }
      else if (clock && why.StartsWith("stall:unknown screen")) { unknownWatch = true; unknownWhy = why.Substring("stall:unknown screen".Length).Trim(); }
      if (n > MaxResets || mem.TotalResets > MaxResetsTotal) {
        // (a screen nobody knows that the gate will not let us leave: named by its form, not as a stuck step)
        if (clock && unknownWatch) Halt(io, "unknown_screen:" + unknownWhy);
        else Halt(io, "recipe_stuck:" + edge + ":" + why);
        return;
      }
      if (!popCity) popSince = now;
      resetTick = true; popCity = true; lastProgress = stepProgress = now; blindMs = 0; gateSince = -1; ownHidden = true; recent.Clear();
      io.HideOwn();   // (during a reset our own windows never block a look)
      ClearRunState();   // a reset is a clean slate: only the run's counters (resets, sweeps) and what the pilots learnt about the game (drag gain, late answers) stay
      io.Log("nav: reset " + edge + " (" + why + ") #" + n + "/" + mem.TotalResets);
    }

    /// <summary>A layer whose close was counted done and that is back within ReturnWindowMs: twice in a row it is not closing. A pixel-only layer is a phantom (ignored for PhantomMs, logged; MaxPhantoms
    /// in a run is a named stop); a real form that keeps coming back is a failed step (reset).</summary>
    void WatchReturns(INavIo io, Snapshot s, long now) {
      if (mem.ClosedAt.Count == 0 || !s.Stable(now)) return;
      string back = null;
      foreach (var l in s.Stack) { long at; if (mem.ClosedAt.TryGetValue(l.Form, out at) && now - at <= NavMemory.ReturnWindowMs && !(mem.PhantomUntil.ContainsKey(l.Form) && mem.PhantomUntil[l.Form] > now)) { back = l.Form; break; } }
      if (back == null) {
        var old = new List<string>(); foreach (var kv in mem.ClosedAt) if (now - kv.Value > NavMemory.ReturnWindowMs) old.Add(kv.Key);
        foreach (var k in old) { mem.ClosedAt.Remove(k); mem.Strikes.Remove(k); }
        return;
      }
      mem.ClosedAt.Remove(back);
      int n; mem.Strikes.TryGetValue(back, out n); mem.Strikes[back] = ++n;
      if (n < 2) return;
      mem.Strikes.Remove(back);
      if (back.StartsWith("panel:")) {
        mem.PhantomUntil[back] = now + NavMemory.PhantomMs;
        io.Log("nav: " + back + " came back after " + n + " closes - a phantom (told by pixels alone), ignored for " + NavMemory.PhantomMs / 1000 + " s #" + (mem.Phantoms + 1));
        if (++mem.Phantoms >= NavMemory.MaxPhantoms) Halt(io, "phantom_layer:" + back);
        return;
      }
      Reset(io, "layer:" + back, "LayerBack", now);
    }

    /// <summary>The click in flight, every action's pilot state (a "notfound" / "failed" is not carried into the next try), the timers of the click helpers and the ladder's rungs.</summary>
    void ClearRunState() {
      act.Start(null, 0); curEdge = "";
      foreach (var a in actions) a.Reset();
      mem.NextAt.Clear(); mem.Rung.Clear(); mem.WaitSince.Clear();
    }

    void Handle(INavIo io, ActOutcome oc, long now) {
      var a = act.Cur; string edge = a != null ? a.Edge : "";
      if (oc != ActOutcome.NotSent) io.Log("nav: act " + (a != null ? a.Target.ToString() : "?") + " " + (edge != "" ? edge : (a != null ? a.Why : "")) + " -> " + oc + " " + (now - act.SentAt) + "ms" + (act.Detail != "" ? " (" + act.Detail + ")" : ""));
      switch (oc) {
        case ActOutcome.Done:
          gateSince = -1; ownHidden = false;
          // (a click that was only sent by a pilot - Fire - is not progress: the pilot's own mark says so, IAtTargetAction.ProgressAt; a verified click is)
          if (act.SentAt > 0 || (a != null && a.Fire)) { Clicks++; if (a == null || !a.Fire) { lastProgress = stepProgress = now; blindMs = 0; } }
          if (a != null && a.Closing != "") mem.ClosedAt[a.Closing] = now;
          if (edge != "") mem.Resets.Remove(edge);
          return;
        case ActOutcome.Halted: Halt(io, "gate_" + io.Gate.HaltReason(Owner)); return;
        case ActOutcome.NotSent: GateWait(io, act.Detail, now); return;   // nothing was clicked: the same step runs again on the next tick (a gate wait that lasts is escalated: GateWait)
        case ActOutcome.NoEffect: case ActOutcome.Timeout:
          if (popCity && edge != "" && (edge.StartsWith("pop:") || edge.StartsWith("modal:"))) { LadderFail(io, edge, oc.ToString(), now); return; }
          // the city's «Герои» did nothing: the game's own hotkey once, if the flag allows (the next plan shows whether it worked)
          if (HotkeyFallback && !hotkeyTried && edge == "graph:hero" && io.Hotkey("heroes")) { hotkeyTried = true; io.Log("nav: " + edge + " did nothing - the heroes hotkey pressed"); return; }
          // a click that went out with the item's tip over the screen was probably taken by the tip (it closes on any click beside it): the step runs again, no trip to the city
          if (act.TipAtSend) { Reset(io, edge != "" ? edge : (a != null ? a.Why : "step"), "WrongScreen", now); return; }
          Reset(io, edge != "" ? edge : (a != null ? a.Why : "step"), oc.ToString(), now); return;
        default: Reset(io, edge != "" ? edge : (a != null ? a.Why : "step"), oc.ToString(), now); return;
      }
    }
  }
}
