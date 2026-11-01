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
    void Log(string line);
    void Problem(string kind, string message);
  }

  public sealed class NavMemory {
    public readonly Dictionary<string, int> Rung = new Dictionary<string, int>();
    public readonly Dictionary<string, int> EdgeFails = new Dictionary<string, int>();
    public readonly Dictionary<string, long> NextAt = new Dictionary<string, long>();
    public readonly Dictionary<string, long> SeenAt = new Dictionary<string, long>();
    public readonly Dictionary<string, long> WaitSince = new Dictionary<string, long>();
    public readonly LiveStill OfferLive = new LiveStill(); public string OfferLiveForm = "";
    public int Replans;
    public long NoOrderSince = -1;
    /// <summary>One try per key and gap: true = go ahead.</summary>
    public bool Due(string key, long now, int gapMs) {
      long t; if (NextAt.TryGetValue(key, out t) && now < t) return false;
      NextAt[key] = now + gapMs; return true;
    }
    public bool Blocked(string key, long now) { long t; return NextAt.TryGetValue(key, out t) && now < t; }
    public void Hold(string key, long until) { NextAt[key] = until; }
    public long Seen(string form, long now) { long t; if (!SeenAt.TryGetValue(form, out t)) { SeenAt[form] = now; t = now; } return t; }
    public void Prune(Snapshot s) {
      var gone = new List<string>();
      foreach (var k in SeenAt.Keys) if (!s.Has(k)) gone.Add(k);
      foreach (var k in gone) { SeenAt.Remove(k); WaitSince.Remove(k); Rung.Remove(k); }
    }
  }

  public sealed class Navigator {
    public const int MaxEdgeFails = 3, MaxReplans = 3, NoProgressMs = 20000, RunCapMs = 180000, WaitCapMs = 90000, OrderUnreadMs = 15000;
    /// <summary>idle | work | at_target | done | halted</summary>
    public string State = "idle";
    public string HaltWhy = "";
    /// <summary>What the navigator is doing or waiting for now (for the status line and the log).</summary>
    public string Say = "";
    public int Clicks, Recoveries;
    public readonly ClickOwner Owner;
    public NavTarget Target { get { return target; } }

    NavTarget target; IList<IAtTargetAction> actions = new List<IAtTargetAction>();
    NavMemory mem = new NavMemory(); readonly ActClick act = new ActClick();
    long startedAt = -1, lastTickAt = -1, pausedMs, lastProgress; string lastSig = "", logged = "", curEdge = "";
    bool popCity, needResume, tipSeen;
    public Navigator(ClickOwner owner) { Owner = owner; }

    public void SetTarget(NavTarget t, IList<IAtTargetAction> acts) {
      actions = acts ?? new List<IAtTargetAction>();
      if (target != null && t.Key() == target.Key()) { target = t; return; }
      target = t; mem = new NavMemory(); act.Start(null, 0); popCity = false; needResume = true;
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
      long now = io.Now;
      long dt = lastTickAt >= 0 ? Math.Min(now - lastTickAt, 5000) : 0; lastTickAt = now;
      if (target == null || State == "idle" || State == "halted" || State == "done") return;
      if (startedAt < 0) { startedAt = now; lastProgress = now; }
      if (needResume) { needResume = false; if (!io.Gate.Halted(Owner)) io.Gate.Resume(Owner); }
      if (io.Gate.Halted(Owner)) { Halt(io, "gate_" + io.Gate.HaltReason(Owner)); return; }
      if (io.UserBusy || !io.Front) {   // a pause is not a failure and does not count toward the no-progress clock
        Say = io.UserBusy ? "paused" : "background"; pausedMs += dt; lastProgress += dt; return;
      }
      if (s == null) return;
      if (s.HasTip()) tipSeen = true; else if (tipSeen) { tipSeen = false; io.Gate.ResetTipEscapes(Owner); }
      mem.Prune(s);
      if (s.Signature != lastSig) { lastSig = s.Signature; lastProgress = now; }
      // the window table's order: 15 s without it is a stop with a reason
      if (s.Confidence == NavConfidence.NoOrder) { if (mem.NoOrderSince < 0) mem.NoOrderSince = now; if (now - mem.NoOrderSince > OrderUnreadMs) { Halt(io, "order_unread"); return; } }
      else mem.NoOrderSince = -1;
      if (act.Active) {
        var oc = act.Tick(io, s, now);
        if (oc == ActOutcome.Running) return;
        Handle(io, oc, now);
        if (State == "halted") return;
      }
      if (State == "work" && now - startedAt - pausedMs > RunCapMs) { Halt(io, "run_cap"); return; }
      if (State == "work" && now - lastProgress > NoProgressMs) {
        if (mem.Replans == 0 && !popCity) { mem.Replans++; popCity = true; Recoveries++; lastProgress = now; io.Log("nav: recover R2 no_progress:" + Say); }
        else { Halt(io, "no_progress:" + (Say == "" ? "?" : Say)); return; }
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
      switch (step.Kind) {
        case StepKind.Wait: Say = step.Why; return;
        case StepKind.None: return;
        case StepKind.Halt: Halt(io, step.Why); return;
        case StepKind.Act:
          StartAct(io, s, step.Act, now); return;
        case StepKind.AtTarget:
          if (tgt.Goal == NavGoal.City) {
            State = "done"; Say = "done"; io.Log("nav: done City " + (now - startedAt) + "ms clicks=" + Clicks + " recoveries=" + Recoveries); return;
          }
          RunActions(io, s, tgt, step, now); return;
      }
    }

    void Note(INavIo io, NavStep step) {
      string k = step.Kind + "|" + step.Edge;
      Say = step.Edge != "" ? step.Edge : step.Why;
      if (k == logged || step.Kind == StepKind.Wait) return;
      logged = k;
      io.Log("nav: plan " + (target != null ? target.Goal.ToString() : "?") + " step=" + step.Edge + " why=" + step.Why);
    }

    void RunActions(INavIo io, Snapshot s, NavTarget tgt, NavStep step, long now) {
      bool seek = step.Edge.StartsWith("seek:");
      State = seek ? "work" : "at_target";
      foreach (var a in actions) {
        if (seek ? a.Name != step.Edge.Substring(5) : (a.Name == "hero" || !a.Wants(s, tgt))) continue;
        var r = a.Step(s, tgt, io, now);
        if (r == null) continue;
        if (r.Kind == StepKind.Halt) { Halt(io, r.Why); return; }
        if (r.Kind == StepKind.Act) { StartAct(io, s, r.Act, now); return; }
        Say = r.Why; return;
      }
      if (!seek) Say = "at target";
    }

    void StartAct(INavIo io, Snapshot s, Act a, long now) {
      if (a.Edge != "" && mem.Blocked("retry:" + a.Edge, now)) { Say = "retry wait " + a.Edge; return; }
      curEdge = a.Edge;
      act.Start(a, now);
      var oc = act.Tick(io, s, now);
      if (oc != ActOutcome.Running) Handle(io, oc, now);
    }

    void Handle(INavIo io, ActOutcome oc, long now) {
      var a = act.Cur; string edge = a != null ? a.Edge : "";
      if (oc != ActOutcome.NotSent) io.Log("nav: act " + (a != null ? a.Target.ToString() : "?") + " " + (edge != "" ? edge : (a != null ? a.Why : "")) + " -> " + oc + " " + (now - act.SentAt) + "ms" + (act.Detail != "" ? " (" + act.Detail + ")" : ""));
      switch (oc) {
        case ActOutcome.Done:
          if (act.SentAt > 0 || (a != null && a.Fire)) { Clicks++; lastProgress = now; }
          if (edge != "") mem.EdgeFails.Remove(edge);
          return;
        case ActOutcome.Halted: Halt(io, "gate_" + io.Gate.HaltReason(Owner)); return;
        case ActOutcome.NoEffect: case ActOutcome.Timeout: case ActOutcome.Denied:
          if (edge == "") return;
          int n; mem.EdgeFails.TryGetValue(edge, out n); n++; mem.EdgeFails[edge] = n;
          mem.Hold("retry:" + edge, now + ClickGate.RetrySettleMs);
          if (n < MaxEdgeFails) { Recoveries++; io.Log("nav: recover R0 " + edge + " try " + n); return; }
          mem.EdgeFails.Remove(edge);
          if (++mem.Replans > MaxReplans) { Halt(io, "nav_stuck:" + edge + ":" + oc); return; }
          Recoveries++; popCity = true; io.Log("nav: recover R2 " + edge);
          return;
        default: return;   // Diverted / WrongScreen / NotSent: nothing is counted; the next plan starts from the new snapshot
      }
    }
  }
}
