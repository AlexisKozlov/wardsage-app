// Wardsage - the UI graph: every form of the game, its clickable widgets with a known effect (open a form, close, a tab switch, a
// request, a jump), the forms the game opens by itself and the pilots' click paths. src/UiGraphData.cs is generated from the client's
// Lua and UI prefabs by tools/ui-graph.py (each game version: tools/game-update.mjs step ui-graph, after screen-map).
//
// Data lines (UiGraphData.Data):
//   F|form|class|stack|esc|auto            class = the screen map's letter; auto = how the game opens it by itself, comma list:
//                                          activity:<scene> (ActivityFormMgr's pop-up queue), data:<module> (a data / network module),
//                                          result:<form> (that form's event listener: the result of an action there), guide, battle
//   W|path|field|flags|rect|icon|effects   a widget of the form above; flags: s shown in the prefab, t a list template, d dangerous (a
//                                          purchase / claim / sell / consume request), k opens a confirm, g a toggle; rect: 8 values in U
//                                          (OfferForms' format, "" = not measured); effects: o:Form open, c:self close, x:Form close,
//                                          t:what tab switch, r:Request (! = dangerous), j:jump (target not resolved), ';' between
//   P|target|steps                         a pilot target's safe path from the city: "form>widget path>to" a step, ' ' between; '-' none
//
// Pure data (tests/CoreTests.cs, the game sim tests/GameSim.cs); the pilots keep their own hand-written chains - the tests prove the
// graph gives the same ones (UiGraph.PilotChains).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RealmForge {
  public static partial class UiGraph {
    public sealed class Effect { public char Kind; public string Target; public bool Danger; }
    public sealed class Widget {
      public string Form, Path, Field, Icon; public bool Shown, Template, Danger, Confirm, Toggle; public object[] Rect;
      public readonly List<Effect> Effects = new List<Effect>();
      /// <summary>The forms this widget opens (no confirm / dangerous one: see Safe).</summary>
      public IEnumerable<string> Opens() { foreach (var e in Effects) if (e.Kind == 'o') yield return e.Target; }
      /// <summary>A press that changes nothing the player pays for: no dangerous request, no confirm.</summary>
      public bool Safe { get { return !Danger && !Confirm; } }
      public bool Closes { get { foreach (var e in Effects) if (e.Kind == 'c') return true; return false; } }
    }
    public sealed class Form {
      public string Name, Stack, Esc; public char Class; public string[] Auto;
      public readonly List<Widget> Widgets = new List<Widget>();
      public bool IsAuto { get { return Auto.Length > 0; } }
      public bool AutoIn(string scene) { foreach (var a in Auto) if (a == scene) return true; return false; }
    }
    public sealed class Step { public string From, Widget, To; public override string ToString() { return From + " [" + Widget + "] -> " + To; } }

    static Dictionary<string, Form> forms; static Dictionary<string, Step[]> paths; static List<Form> order;
    static void Load() {
      if (forms != null) return;
      var fm = new Dictionary<string, Form>(); var pm = new Dictionary<string, Step[]>(); var ord = new List<Form>(); Form cur = null;
      foreach (var line in Data) {
        var p = line.Split('|');
        if (p[0] == "F") {
          cur = new Form { Name = p[1], Class = p[2].Length > 0 ? p[2][0] : 'U', Stack = p[3], Esc = p[4], Auto = p[5].Length > 0 ? p[5].Split(',') : new string[0] };
          fm[cur.Name] = cur; ord.Add(cur);
        } else if (p[0] == "W" && cur != null) {
          var w = new Widget { Form = cur.Name, Path = p[1], Field = p[2], Shown = p[3].IndexOf('s') >= 0, Template = p[3].IndexOf('t') >= 0, Danger = p[3].IndexOf('d') >= 0,
                               Confirm = p[3].IndexOf('k') >= 0, Toggle = p[3].IndexOf('g') >= 0, Rect = ParseRect(p[4]), Icon = p[5] };
          foreach (var e in p[6].Split(';')) {
            if (e.Length < 2 || e[1] != ':') continue;
            string t = e.Substring(2); bool d = t.EndsWith("!");
            w.Effects.Add(new Effect { Kind = e[0], Target = d ? t.Substring(0, t.Length - 1) : t, Danger = d });
          }
          cur.Widgets.Add(w);
        } else if (p[0] == "P") {
          if (p[2] == "-") { pm[p[1]] = null; continue; }
          var st = new List<Step>();
          foreach (var s in p[2].Split(' ')) { var q = s.Split('>'); if (q.Length == 3) st.Add(new Step { From = q[0], Widget = q[1], To = q[2] }); }
          pm[p[1]] = st.ToArray();
        }
      }
      forms = fm; paths = pm; order = ord;
    }
    static object[] ParseRect(string s) {
      if (string.IsNullOrEmpty(s)) return null;
      var p = s.Split(','); if (p.Length != 8) return null;
      var r = new object[8];
      for (int i = 0; i < 8; i++) r[i] = i % 2 == 0 ? (object)p[i][0] : (object)double.Parse(p[i], CultureInfo.InvariantCulture);
      return r;
    }

    public static IList<Form> All { get { Load(); return order; } }
    public static Form ByName(string name) { Load(); Form f; return name != null && forms.TryGetValue(name, out f) ? f : null; }
    /// <summary>The generated path of a pilot target (null = not reachable by safe clicks / unknown target).</summary>
    public static Step[] PathOf(string target) { Load(); Step[] s; return paths.TryGetValue(target, out s) ? s : null; }
    public static IEnumerable<string> Targets { get { Load(); return paths.Keys; } }

    /// <summary>The pilots' hand-written chains (the city first, then these forms one after the other: app/Overlay.cs, src/*Pilot.cs).
    /// The graph's path to each target must give the same forms (tests/CoreTests.cs); a new game version where it does not fails the tests.</summary>
    public static readonly Dictionary<string, string[]> PilotChains = new Dictionary<string, string[]> {
      { "inventory", new[] { "Form_MainCity", "Form_BackpackIntegration" } },
      { "inventoryBulk", new[] { "Form_MainCity", "Form_BackpackIntegration", "Form_BackpackIntegration#m_BtnBatchSell" } },
      { "hero", new[] { "Form_MainCity", "Form_CharactorMain" } },
      { "foundry", new[] { "Form_MainCity", "Form_BackpackIntegration", "Form_EquipSubliming" } },
      { "foundryAscend", new[] { "Form_MainCity", "Form_BackpackIntegration", "Form_EquipSubliming", "Form_EquipSubliming#m_ToggleBuilding" } },
      { "gem", new[] { "Form_MainCity", "Form_BackpackIntegration", "Form_EquipGemMain" } },
      { "artWorkshop", new[] { "Form_MainCity", "Form_BackpackIntegration", "Form_EquipArtifactIntensify" } },
    };
    /// <summary>The forms a path goes through, the city first.</summary>
    public static string[] Chain(Step[] s) {
      if (s == null) return null;
      var l = new List<string>(); if (s.Length > 0) l.Add(s[0].From);
      foreach (var x in s) l.Add(x.To);
      return l.ToArray();
    }

    /// <summary>Breadth first over the safe opens from <paramref name="from"/> (the generator's paths are cheapest-first over shown widgets;
    /// this one is for any form, e.g. the sim's starts): null = not reachable.</summary>
    public static Step[] Path(string from, string to) {
      Load(); if (from == to) return new Step[0];
      var prev = new Dictionary<string, Step>(); var q = new Queue<string>(); q.Enqueue(from); prev[from] = null;
      while (q.Count > 0) {
        var f = q.Dequeue(); var fo = ByName(f); if (fo == null) continue;
        foreach (var w in fo.Widgets) {
          if (!w.Safe) continue;
          foreach (var t in w.Opens()) {
            if (t.StartsWith("?") || prev.ContainsKey(t)) continue;
            var tf = ByName(t); if (tf == null || tf.Class == 'D' || tf.Class == 'W') continue;
            prev[t] = new Step { From = f, Widget = w.Path, To = t };
            if (t == to) { var l = new List<Step>(); for (var s = prev[t]; s != null; s = prev[s.From]) l.Insert(0, s); return l.ToArray(); }
            q.Enqueue(t);
          }
        }
      }
      return null;
    }
    /// <summary>Every form reachable from the city by safe clicks (with its path's length).</summary>
    public static Dictionary<string, int> Reachable(string from) {
      Load(); var d = new Dictionary<string, int>(); var q = new Queue<string>(); q.Enqueue(from); d[from] = 0;
      while (q.Count > 0) {
        var f = q.Dequeue(); var fo = ByName(f); if (fo == null) continue;
        foreach (var w in fo.Widgets) {
          if (!w.Safe) continue;
          foreach (var t in w.Opens()) {
            if (t.StartsWith("?") || d.ContainsKey(t)) continue;
            var tf = ByName(t); if (tf == null || tf.Class == 'D' || tf.Class == 'W') continue;
            d[t] = d[f] + 1; q.Enqueue(t);
          }
        }
      }
      return d;
    }
    /// <summary>How a form is left (back to the city: each form of the stack in turn): the screen map's way, else a close widget the
    /// graph knows (the screen map calls the form U), else "none".</summary>
    public static string ExitOf(string name) {
      char c = ScreenMap.ClassOf(name);
      switch (c) { case 'F': return "back"; case 'P': return "close"; case 'E': return "escape"; case 'O': return "overlay"; case 'W': return "wait"; case 'D': return "stop"; case 'K': return "known"; }
      var f = ByName(name); if (f == null) return "none";
      foreach (var w in f.Widgets) if (w.Shown && !w.Template && w.Safe && w.Closes && w.Rect != null) return "graph:" + w.Path;
      return "none";
    }
    /// <summary>A widget of <paramref name="form"/> under the point (client px; shown in the prefab, not a list template, measured, not a
    /// full-screen tap catcher): the smallest dangerous one when <paramref name="danger"/>, else the smallest of any kind; null = none.</summary>
    public static Widget WidgetAt(string form, double x, double y, double W, double H, double U, bool danger) {
      var f = ByName(form); if (f == null) return null;
      Widget best = null; double ba = double.MaxValue;
      foreach (var w in f.Widgets) {
        if (!w.Shown || w.Template || w.Rect == null || (danger && !w.Danger)) continue;
        var r = ScreenMap.Place(w.Rect, W, H, U);
        if (r.W < 2 || r.H < 2 || r.W > W * 0.9 && r.H > H * 0.9) continue;
        if (x >= r.X && x <= r.Right && y >= r.Y && y <= r.Bottom && r.W * r.H < ba) { best = w; ba = r.W * r.H; }
      }
      return best;
    }
    /// <summary>Every dangerous widget of <paramref name="form"/> under the point (the filters of <see cref="WidgetAt"/>).</summary>
    public static List<Widget> DangerWidgetsAt(string form, double x, double y, double W, double H, double U) {
      var l = new List<Widget>(); var f = ByName(form); if (f == null) return l;
      foreach (var w in f.Widgets) {
        if (!w.Shown || w.Template || w.Rect == null || !w.Danger) continue;
        var r = ScreenMap.Place(w.Rect, W, H, U);
        if (r.W < 2 || r.H < 2 || r.W > W * 0.9 && r.H > H * 0.9) continue;
        if (x >= r.X && x <= r.Right && y >= r.Y && y <= r.Bottom) l.Add(w);
      }
      return l;
    }
    /// <summary>The dangerous widgets of a form (shown, not a list template): the ones a click must never land on.</summary>
    public static IEnumerable<Widget> DangerWidgets(string form) {
      var f = ByName(form); if (f == null) yield break;
      foreach (var w in f.Widgets) if (w.Danger && w.Shown && !w.Template) yield return w;
    }
    /// <summary>The forms the game opens by itself in a scene ("city": ActivityFormMgr over the city) or after an action (any).</summary>
    public static List<string> AutoForms(string scene) {
      Load(); var l = new List<string>();
      foreach (var f in order) foreach (var a in f.Auto) if (scene == null || a == scene || a.StartsWith(scene)) { l.Add(f.Name); break; }
      return l;
    }
  }
}
