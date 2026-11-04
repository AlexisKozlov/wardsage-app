// Wardsage - the navigator's targets, routes and planner (docs/specs/navigator.md 3.4, 3.5).
//
// NavPlanner.Plan is pure: from a snapshot to the next step - the modal layers first (NavModal.cs), then the pop toward the route, then the first
// waypoint of the canonical route that does not hold. Stage 1: City, HeroScreen, HeroGear, HeroEquip.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum NavGoal { City, HeroScreen, HeroGear, HeroEquip, Inventory, InventoryBulk, Foundry, Gem, ArtWorkshop }

  public sealed class NavTarget {
    public NavGoal Goal;
    public long Hero;
    public int Slot = -1;
    public int Tab = -1;
    public ClickOwner Owner = ClickOwner.Equip;
    /// <summary>The recipe this target runs (src/NavRecipe.cs; "" = the one of the goal) and the step it ends at ("" = all navigation steps).</summary>
    public string RecipeId = "", UpTo = "";
    /// <summary>Resets this run has made so far (the navigator sets it before every action step): a pilot that cannot finish asks for a reset first and halts when a reset did not help.</summary>
    public int Resets;
    public string Key() { return Goal + "|" + Hero + "|" + Slot + "|" + Tab + "|" + RecipeId + "|" + UpTo; }
  }

  public enum StepKind { None, Wait, Act, AtTarget, Halt, Reset }

  public sealed class NavStep {
    public StepKind Kind;
    public Act Act;
    public string Edge = "";
    public ICollection<string> Keep;
    public string Why = "";
    public static readonly NavStep Nothing = new NavStep { Kind = StepKind.None };
    public static NavStep Wait(string why) { return new NavStep { Kind = StepKind.Wait, Why = why }; }
    public static NavStep Halt(string why) { return new NavStep { Kind = StepKind.Halt, Why = why }; }
    /// <summary>The step cannot go on (an in-place action failed its own check): the run goes back to the city and re-enters (Navigator.Reset).</summary>
    public static NavStep Reset(string why) { return new NavStep { Kind = StepKind.Reset, Why = why }; }
    public static NavStep Of(Act a, string edge) { a.Edge = a.Edge != "" ? a.Edge : edge; return new NavStep { Kind = StepKind.Act, Act = a, Edge = edge, Why = a.Why }; }
  }

  /// <summary>What a pilot does at (or on the way to) the target. Step: null = nothing to do now (the next action may act); an Act, a Wait or a Halt = this tick is taken.</summary>
  public interface IAtTargetAction {
    string Name { get; }
    /// <summary>The layers this action needs open.</summary>
    ICollection<string> Keep { get; }
    /// <summary>This action has work for the target now (the gear filter only while the guide asks for it).</summary>
    bool Wants(Snapshot s, NavTarget t);
    /// <summary>When this action last made progress that is not a state change of the screen (a grid drag that moved the search on, an accepted pick, a list scroll): a pilot's click is "sent", never progress by itself
    /// (docs/specs/2026-11-04-core-any-scenario.md D5). 0 = never. The navigator's stall clock reads it.</summary>
    long ProgressAt { get; }
    NavStep Step(Snapshot s, NavTarget t, INavIo io, long now);
    void Reset();
  }

  public sealed class Waypoint {
    public string Key = "";
    public string Form = "";
    public Func<Snapshot, NavTarget, bool> Holds;
    public Func<Snapshot, NavTarget, INavIo, Act> Edge;
    public string Seek = "";
  }

  public static class NavRoutes {
    /// <summary>The graph's path key of a goal (UiGraph.PathOf), null = the city itself.</summary>
    public static string GraphKey(NavGoal g) {
      switch (g) {
        case NavGoal.HeroScreen: case NavGoal.HeroGear: case NavGoal.HeroEquip: return "hero";
        case NavGoal.Inventory: return "inventory";
        case NavGoal.InventoryBulk: return "inventoryBulk";
        case NavGoal.Foundry: return "foundry";
        case NavGoal.Gem: return "gem";
        case NavGoal.ArtWorkshop: return "artWorkshop";
      }
      return null;
    }
    /// <summary>The forms of the goal's canonical route, the city first (the generated chain; the test pins it to UiGraph.PathOf).</summary>
    public static string[] Forms(NavGoal g) {
      string k = GraphKey(g);
      if (k == null) return new[] { "Form_MainCity" };
      return UiGraph.Chain(UiGraph.PathOf(k));
    }

    /// <summary>The target's navigation as waypoints: the steps of its recipe (data: tools/recipes/*.json), up to the step the goal ends at.</summary>
    public static List<Waypoint> Of(NavTarget t) {
      string id = t.RecipeId != "" ? t.RecipeId : t.Goal == NavGoal.City ? "city" : t.Goal == NavGoal.HeroScreen || t.Goal == NavGoal.HeroGear || t.Goal == NavGoal.HeroEquip ? "equip" : "";
      var r = id != "" ? NavRecipes.Get(id) : null;
      if (r == null) return new List<Waypoint>();
      string upTo = t.UpTo != "" ? t.UpTo : t.Goal == NavGoal.HeroScreen ? "heroes" : t.Goal == NavGoal.HeroGear ? "gearTab" : "";
      var args = new Dictionary<string, long> { { "hero", t.Hero }, { "slot", t.Slot } };
      return NavRecipes.Compile(r, args, upTo, t.Owner);
    }
  }

  public static class NavPlanner {
    static readonly HeroGeometry Hg = new HeroGeometry();

    public static NavStep Plan(Snapshot s, NavTarget t, ICollection<string> atTargetKeep, NavMemory m, INavIo io) { return Plan(s, t, atTargetKeep, m, io, null); }

    /// <summary>Pure (but for the gate's due checks, which note the try): the next step from this snapshot to the target.</summary>
    public static NavStep Plan(Snapshot s, NavTarget t, ICollection<string> atTargetKeep, NavMemory m, INavIo io, ICollection<string> seekKeep) {
      long now = io.Now; var st = s.State;
      if (s.Confidence < NavConfidence.NoOrder) return NavStep.Wait("screen read: " + s.Confidence);
      if (!s.Stable(now)) return NavStep.Wait("stabilising");
      var wps = NavRoutes.Of(t);
      int first = -1;
      for (int i = 0; i < wps.Count; i++) if (!wps[i].Holds(s, t)) { first = i; break; }
      ICollection<string> keep = m.WithPhantoms(first < 0 ? atTargetKeep : wps[first].Seek != "" ? seekKeep : null, now);
      var md = ModalHandler.Next(s, keep, m, io, t.Owner);
      if (md.Kind != StepKind.None) return md;
      if (s.Kind == ScreenKind.Unknown) return UnknownScreen(s, t, m, io, now);
      bool onRoute = s.Kind == ScreenKind.City || (t.Goal != NavGoal.City && s.Kind == ScreenKind.HeroScreen);
      if (!onRoute) return Pop(s, t, io, now);
      if (first < 0) return new NavStep { Kind = StepKind.AtTarget, Edge = "at_target", Keep = atTargetKeep };
      var w = wps[first];
      if (w.Seek != "") return new NavStep { Kind = StepKind.AtTarget, Edge = "seek:" + w.Seek, Keep = seekKeep, Why = w.Key };
      var act = w.Edge != null ? w.Edge(s, t, io) : null;
      if (act == null) return NavStep.Wait("edge " + w.Key + " not ready");
      return NavStep.Of(act, w.Key);
    }

    static List<string> FormsOf(Snapshot s) { var l = new List<string>(); foreach (var x in s.Stack) l.Add(x.Form); return l; }

    internal static NavStep EscapeStep(ClickOwner owner, Snapshot s, ClickTarget target, string edge) {
      string lk = s.LayerKey(); var kind = s.Kind;
      return NavStep.Of(new Act { Owner = owner, Target = target, Kind = ActKind.Escape, Why = "escape", Edge = edge, WantKnown = true, Keep = s.Kind == ScreenKind.Unknown ? FormsOf(s) : null,
        Expect = n => n.LayerKey() != lk || n.Kind != kind }, edge);
    }

    static NavStep Pop(Snapshot s, NavTarget t, INavIo io, long now) {
      var gate = io.Gate; var st = s.State;
      if (gate.EscapeDue(t.Owner, st, now)) return EscapeStep(t.Owner, s, ClickTarget.EscapeBack, "pop:escape");
      if (!gate.CityBackDue(t.Owner, st, now)) return gate.Halted(t.Owner) ? NavStep.Halt("gate_" + gate.HaltReason(t.Owner)) : NavStep.Wait("back to the city not due (" + ClickGate.CityBackBlock(st) + ")");
      string bf = s.BaseForm; var kind = s.Kind; double U = io.U;
      return NavStep.Of(new Act { Owner = t.Owner, Target = ClickTarget.CityBack, Kind = ActKind.Click, X = Hg.BackX * U, Y = Hg.BackY * U, Base = bf, WantKnown = true, Why = "back to the city",
        Expect = n => n.Kind != kind || n.BaseForm != bf, Fill = rq => { rq.U = U; } }, "pop:" + bf);
    }

    static NavStep UnknownScreen(Snapshot s, NavTarget t, NavMemory m, INavIo io, long now) {
      var gate = io.Gate; var st = s.State;
      if (gate.UnknownStall(t.Owner, st, now)) return NavStep.Halt("unknown_screen:" + (st.TopForm ?? ""));
      if (gate.EscapeDue(t.Owner, st, now)) return EscapeStep(t.Owner, s, ClickTarget.EscapeBack, "pop:escape");
      if (!gate.UnknownBackDue(t.Owner, st, now)) return NavStep.Wait("unknown screen " + st.TopForm);   // (the gate's UnknownStall and the watchdog end it by name)

      double W = io.W, H = io.H, U = io.U;
      var md = ClickGate.MapExit(st, W, H, U);
      double bx = md.Move == MapMove.Back && md.Point != null ? md.Point.X : Hg.BackX * U, by = md.Move == MapMove.Back && md.Point != null ? md.Point.Y : Hg.BackY * U;
      string sig = s.Signature;
      return NavStep.Of(new Act { Owner = t.Owner, Target = ClickTarget.UnknownBack, Kind = ActKind.Click, X = bx, Y = by, WantKnown = true, Why = "unknown screen: back", Keep = FormsOf(s),
        Expect = n => n.Signature != sig, Fill = rq => { rq.U = U; rq.W = W; rq.H = H; } }, "pop:unknown");
    }
  }
}
