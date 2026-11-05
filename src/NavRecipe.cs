// Wardsage - the navigator's tasks as data (docs/specs/navigator.md 6b.2): a recipe is an ordered list of steps (tools/recipes/<id>.json, compiled into src/NavRecipesData.cs by
// tools/recipes.mjs, which also validates it against the UI graph and the code registries below). The interpreter is the navigator: the navigation steps become the planner's
// waypoints (hold test, one click with its own success test, or the hero pilot's seek), the terminal action steps are the pilots that run at the target.
// Closed vocabulary, no expression evaluator: a condition is facet-op-value joined by &&.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RealmForge {
  public sealed class RecipeStep {
    public string Id = "", Key = "", From = "", When = "", Widget = "", Target = "", Fallback = "", Need = "", Why = "", Action = "";
    public string ExpectForm = "", ExpectFacet = "", ExpectAction = "";
    public string[] Keep = new string[0];
    public int TimeoutMs, BudgetMs;
    public bool IsAction { get { return Action != ""; } }
    /// <summary>An action step whose success is a facet (the hero pilot's seek): it is a navigation step. The other actions run at the target.</summary>
    public bool IsSeek { get { return Action != "" && ExpectFacet != ""; } }
  }

  public sealed class Recipe {
    public string Id = "", Game = "", Owner = ""; public int V;
    public string[] Params = new string[0];
    public readonly List<RecipeStep> Steps = new List<RecipeStep>();
  }

  /// <summary>The facets a recipe can test, read from the stable snapshot (the registry the validator checks names against).</summary>
  public static class NavFacets {
    public static readonly string[] Names = { "Hero", "HeroTab", "HeroSlot", "GearFilter" };
    public static long Read(string facet, Snapshot s) {
      switch (facet) {
        case "Hero": return s.State.Hero;
        case "HeroTab": return s.State.HeroTab;
        case "HeroSlot": return s.State.HeroSlot;
        // the gear filter pop-up is open on a shown gear list (the game's flag, m_bShow): the part row inside it changes the slot, the hero screen's slot icons are covered
        case "GearFilter": return s.State.GearOpenMem == Mem3.Yes && s.State.HeroSlot >= 0 ? 1 : 0;
      }
      throw new ArgumentException("facet " + facet);
    }
    public static readonly string[] Constants = { "gear" };
    static long Const(string n) { return n == "gear" ? HeroGeometry.GearTab : 0; }

    static long Val(string tok, Snapshot s, IDictionary<string, long> args) {
      tok = tok.Trim(); long n;
      if (tok.StartsWith("$", StringComparison.Ordinal)) { long v; return args.TryGetValue(tok.Substring(1), out v) ? v : 0; }
      if (long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
      if (Array.IndexOf(Constants, tok) >= 0) return Const(tok);
      return s != null ? Read(tok, s) : 0;
    }

    static readonly string[] Ops = { "==", "!=", ">=", "<=", "<", ">" };
    /// <summary>"A op B && C op D" on the snapshot s and the run's parameters. lenient: an unread hero tab (&lt; 0) passes "HeroTab==gear" (the hold test; the click's own test is strict).</summary>
    public static bool Eval(string expr, Snapshot s, IDictionary<string, long> args, bool lenient) {
      if (string.IsNullOrEmpty(expr)) return true;
      foreach (var part in expr.Split(new[] { "&&" }, StringSplitOptions.None)) {
        string p = part.Trim(); string op = null; int at = -1;
        foreach (var o in Ops) { at = p.IndexOf(o, StringComparison.Ordinal); if (at > 0) { op = o; break; } }
        if (op == null) throw new ArgumentException("condition " + p);
        string l = p.Substring(0, at).Trim(), r = p.Substring(at + op.Length).Trim();
        long a = Val(l, s, args), b = Val(r, s, args); bool ok;
        switch (op) {
          case "==": ok = a == b || (lenient && l == "HeroTab" && r == "gear" && a < 0); break;
          case "!=": ok = a != b; break;
          case ">=": ok = a >= b; break;
          case "<=": ok = a <= b; break;
          case "<": ok = a < b; break;
          default: ok = a > b; break;
        }
        if (!ok) return false;
      }
      return true;
    }
  }

  public static class NavRecipes {
    static readonly Dictionary<string, Recipe> cache = new Dictionary<string, Recipe>();
    static readonly HeroGeometry Hg = new HeroGeometry();
    static readonly HintGeometry Slots = new HintGeometry();

    // the names the code knows: the validator checks every recipe against them (CoreTests dumps them with RECIPE_REGISTRY=1)
    public static readonly string[] ActionIds = { "hero.select", "gear.filter", "gear.pick" };
    public static readonly string[] GeoKeys = { "geo:hero.heroesBtn", "geo:hero.gearTab", "geo:hero.slot($slot)", "std:back" };
    /// <summary>What a step needs before its click: "mem:cityHeroes" = the city's «Герои» button is usable (the game's memory first, OverlayCity.HeroesButtonNow; the host answers the picture only while memory is Unknown);
    /// "pic:heroesButton" = the picture alone (older recipes).</summary>
    public static readonly string[] Pictures = { "pic:heroesButton", "mem:cityHeroes" };
    /// <summary>The navigator's action name of a recipe action id (IAtTargetAction.Name).</summary>
    public static string ActionName(string id) {
      switch (id) { case "hero.select": return "hero"; case "gear.filter": return "filter"; case "gear.pick": return "item"; }
      return id;
    }

    public static IEnumerable<string> RegistryLines() {
      foreach (var n in Enum.GetNames(typeof(ClickTarget))) yield return "target:" + n;
      foreach (var n in NavFacets.Names) yield return "facet:" + n;
      foreach (var n in NavFacets.Constants) yield return "const:" + n;
      foreach (var n in ActionIds) yield return "action:" + n;
      foreach (var n in GeoKeys) yield return "geo:" + n;
      foreach (var n in Pictures) yield return "pic:" + n;
    }

    public static Recipe Get(string id) {
      lock (cache) {
        Recipe r; if (cache.TryGetValue(id, out r)) return r;
        foreach (var json in NavRecipesData.Data) {
          var o = MiniJson.AsObject(MiniJson.TryParse(json)); if (o == null) continue;
          if (MiniJson.GetString(o, "id") != id) continue;
          r = Parse(o); cache[id] = r; return r;
        }
      }
      return null;
    }

    static string[] Strs(object v) {
      var l = v as List<object>; if (l == null) return new string[0];
      var r = new string[l.Count]; for (int i = 0; i < r.Length; i++) r[i] = l[i] as string ?? "";
      return r;
    }
    static int Int(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is double ? (int)(double)v : 0; }
    static string Str(Dictionary<string, object> d, string k) { return MiniJson.GetString(d, k) ?? ""; }

    static Recipe Parse(Dictionary<string, object> o) {
      var r = new Recipe { Id = Str(o, "id"), Game = Str(o, "game"), Owner = Str(o, "owner"), V = Int(o, "v") };
      object v; if (o.TryGetValue("params", out v)) r.Params = Strs(v);
      var steps = o.TryGetValue("steps", out v) ? v as List<object> : null;
      if (steps != null) foreach (var so in steps) {
        var d = MiniJson.AsObject(so); if (d == null) continue;
        var s = new RecipeStep { Id = Str(d, "id"), Key = Str(d, "key"), From = Str(d, "from"), When = Str(d, "when"), Widget = Str(d, "widget"), Target = Str(d, "target"), Fallback = Str(d, "fallback"),
          Need = Str(d, "need"), Why = Str(d, "why"), Action = Str(d, "action"), TimeoutMs = Int(d, "timeoutMs"), BudgetMs = Int(d, "budgetMs") };
        if (d.TryGetValue("keep", out v)) s.Keep = Strs(v);
        var ex = d.TryGetValue("expect", out v) ? MiniJson.AsObject(v) : null;
        if (ex != null) { s.ExpectForm = Str(ex, "form"); s.ExpectFacet = Str(ex, "facet"); s.ExpectAction = Str(ex, "action"); }
        if (s.Key == "") s.Key = "step:" + s.Id;
        r.Steps.Add(s);
      }
      return r;
    }

    /// <summary>The click point of a geo key in client px (null = not known).</summary>
    public static double[] GeoPoint(string key, INavIo io, IDictionary<string, long> args) {
      double U = io.U;
      switch (key) {
        case "std:back": return new[] { Hg.BackX * U, Hg.BackY * U };
        case "geo:hero.heroesBtn": return new[] { io.W - Hg.HeroesBtnDX * U, io.H - Hg.HeroesBtnDY * U };
        case "geo:hero.gearTab": return new[] { io.W - Hg.GearTabDX * U, Hg.GearTabY * U };
        case "geo:hero.slot($slot)": {
          long sl; if (!args.TryGetValue("slot", out sl)) return null;
          var r = Slots.Slot((int)sl, io.W, io.H); return r == null ? null : new[] { r[0] + r[2] / 2.0, r[1] + r[3] / 2.0 };
        }
      }
      return null;
    }

    static bool HasPicture(string need, INavIo io) {
      if (need == "") return true;
      if (need == "pic:heroesButton" || need == "mem:cityHeroes") return io.Picture("heroesButton");   // (the host's answer: memory first for the mem: key)
      return false;
    }

    /// <summary>The navigation steps of a recipe as the planner's waypoints, up to and including the step <paramref name="upTo"/> ("" = all of them): a step holds when its
    /// condition is false or its expectation holds; its edge is one click (target, point from the geo key) verified by the same expectation.</summary>
    public static List<Waypoint> Compile(Recipe r, IDictionary<string, long> args, string upTo, ClickOwner owner) {
      var l = new List<Waypoint>();
      foreach (var st in r.Steps) {
        if (st.IsAction && !st.IsSeek) break;   // the terminal actions: they run at the target
        var step = st;
        var w = new Waypoint { Key = step.Key, Form = step.From };
        w.Holds = (s, t) => !NavFacets.Eval(step.When, s, args, true) || Expect(step, s, args, true);
        if (step.IsSeek) w.Seek = ActionName(step.Action);
        else w.Edge = (s, t, io) => {
          if (s.Kind != ScreenState.KindOfForm(step.From) || !HasPicture(step.Need, io)) return null;
          var p = GeoPoint(step.Fallback != "" ? step.Fallback : step.Widget, io, args); if (p == null) return null;
          ClickTarget tg = (ClickTarget)Enum.Parse(typeof(ClickTarget), step.Target);
          var act = new Act { Owner = t.Owner, Target = tg, Kind = ActKind.Click, X = p[0], Y = p[1], Base = step.From, Expect = n => Expect(step, n, args, false), Why = step.Why != "" ? step.Why : step.Id,
            Edge = step.Key, Forward = true, WantCity = true, Keep = ClickGate.TipForms };
          if (step.TimeoutMs > 0) act.TimeoutMs = step.TimeoutMs;
          return act;
        };
        l.Add(w);
        if (upTo != "" && step.Id == upTo) break;
      }
      return l;
    }

    static bool Expect(RecipeStep step, Snapshot s, IDictionary<string, long> args, bool lenient) {
      if (step.ExpectForm != "" && s.Kind != ScreenState.KindOfForm(step.ExpectForm)) return false;
      return NavFacets.Eval(step.ExpectFacet, s, args, lenient);
    }
  }
}
