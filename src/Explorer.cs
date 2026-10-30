// Wardsage - TEST build: the explorer. Started by the lab runner (tools/lab/WardsageLab.ps1, scenario explore) through a command file, no
// clicks needed: from the city it walks the game's UI over the UI graph's SAFE transitions only (src/UiGraph.cs) and checks each form it
// reaches against the graph and the screen map, then goes back to the city.
//
//   explore.cmd  one line:  "<seq> start <budget> <rot> <run id>"   or   "<seq> stop"      (%LOCALAPPDATA%\WardsageTest\lab\, as rec.cmd)
//   explore.ack  {"seq":N,"state":"running|stopped|idle|stale","run":".."}
//   explore.json the run's result (ExploreRun.Json): forms visited, the mismatches, the time; the runner uploads it (lab_put_file)
//
// What it presses (each press through ClickGate, owner Explore: ExploreOpen / ExploreTab / ExploreClose / ExploreEscape, every safety check):
//   - a widget of the form on top that the graph says opens ONE form (shown in the prefab, not a list template, measured, no request, no
//     confirm, not dangerous, no word of a fight / purchase / reward ... in its path, not a never-click button of the screen map) and only to a
//     form that is left by its back arrow, its ×, Escape or a close widget the graph knows (never a D / W / K form);
//   - a tab of the form (the graph's tab switch handler, same rules), at most MaxTabs a form;
//   - the way back as the screen map gives it (back arrow F, × P, Escape E, the graph's close widget U), the offers by their ×.
// It stops for good on a dangerous (D) form, the gate's stop, a form on top it cannot leave, the player's own input (the host) or the time cap.
// At each target form: the live top form and the shown forms' order, the live rects of the graph's shown widgets (visible? where?), whether
// its way back works; against the graph: "explore: <form> ok" or "explore: <form> mismatch <what>".
//
// The plan (ExplorePlan): a breadth-first tree of those safe opens from the city, its forms in depth-first order; a run takes <budget> forms
// from place budget * rot of that order on (the next run the next ones), and moves between them over the tree (back up to the common form,
// then down). Pure logic, no Windows APIs (tests: the game sim, tests/GameSim.cs ExplorerSim).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  public sealed class ExploreCmd {
    public long Seq; public string Op = "", Run = ""; public int Budget, Rot;
    public const int MaxBudget = 200;
    static readonly Regex LineRx = new Regex("^([0-9]{1,15}) (?:(start) ([0-9]{1,3}) ([0-9]{1,6}) ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})|(stop))$");
    /// <summary>The command of the file's first line; null = not exactly one of the two forms (or a budget out of 1..MaxBudget).</summary>
    public static ExploreCmd Parse(string text) {
      if (text == null || text.Length > 200) return null;
      int nl = text.IndexOfAny(new[] { '\r', '\n' }); string line = nl >= 0 ? text.Substring(0, nl) : text;
      var m = LineRx.Match(line); if (!m.Success) return null;
      long seq; if (!long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out seq)) return null;
      if (m.Groups[6].Success) return new ExploreCmd { Seq = seq, Op = "stop" };
      int b = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), r = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
      if (b < 1 || b > MaxBudget) return null;
      return new ExploreCmd { Seq = seq, Op = "start", Budget = b, Rot = r, Run = m.Groups[5].Value };
    }
    public static string Ack(long seq, string state, string run) {
      return "{\"seq\":" + seq.ToString(CultureInfo.InvariantCulture) + ",\"state\":" + MiniJson.Quote(state ?? "") + ",\"run\":" + MiniJson.Quote(run ?? "") + "}";
    }
  }

  /// <summary>Which graph edges the explorer may press, the tree over them and a run's forms.</summary>
  public static class ExplorePlan {
    public const string City = "Form_MainCity";
    // a word of a widget's path that is never pressed by the explorer, whatever the graph says its press does (a fight, a purchase, a reward,
    // a confirm, a change of the account, the heroes or the gear ...)
    static readonly HashSet<string> NeverWords = new HashSet<string>(StringComparer.Ordinal) {
      "fight", "battle", "challenge", "start", "sweep", "auto", "quick", "skip",
      "buy", "purchase", "pay", "recharge", "charge", "sell", "use", "claim", "receive", "get", "draw", "recruit", "free",
      "once", "ten", "donate", "exchange", "confirm", "sure", "ok", "yes", "upgrade", "levelup", "intensify", "ascend", "sublim",
      "subliming", "reset", "refresh", "delete", "logout", "quit", "exit", "account", "bind", "send", "share", "lock", "unlock", "join", "apply",
      "invite", "kick", "leave", "dismiss", "transfer", "unequip", "wear", "replace", "remove", "craft", "make", "forge", "cook", "build", "train",
      "learn", "speed", "accelerate", "finish", "complete", "collect", "accept", "submit", "dispatch", "rename", "input", "report", "like", "vote",
      "decompose", "dismantle", "salvage", "fuse", "merge", "evolve", "awake", "awaken", "promote", "rebirth", "retreat", "surrender", "giveup",
      "abandon", "cancel", "agree", "refuse", "sign", "checkin", "pray"
    };
    static readonly Regex WordRx = new Regex("[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+");
    /// <summary>The words of a path ("m_BtnGetReward/Button_Special_2" -> m, btn, get, reward, button, special, 2), lower case.</summary>
    public static List<string> Words(string path) {
      var l = new List<string>(); if (string.IsNullOrEmpty(path)) return l;
      foreach (Match m in WordRx.Matches(path)) l.Add(m.Value.ToLowerInvariant());
      return l;
    }
    /// <summary>The widget's own part of its path: from its nearest m_ segment on (the panels above it name a place, not what it does).</summary>
    public static string OwnPart(string path) {
      var p = (path ?? "").Split('/');
      for (int i = p.Length - 1; i >= 0; i--) if (p[i].StartsWith("m_")) return string.Join("/", p, i, p.Length - i);
      return p.Length >= 2 ? p[p.Length - 2] + "/" + p[p.Length - 1] : path ?? "";
    }
    public static string NeverWord(string path) { foreach (var w in Words(path)) if (NeverWords.Contains(w)) return w; return null; }

    const double PW = 1920, PH = 1009;
    static bool Measured(UiGraph.Widget w) {
      if (w.Rect == null) return false;
      double U = Ui.Unit(PW, PH); var r = ScreenMap.Place(w.Rect, PW, PH, U);
      return !(r.W > PW * 0.9 && r.H > PH * 0.9);
    }
    /// <summary>A tap catcher over the whole window (a tip closed by a tap anywhere).</summary>
    public static bool FullScreen(UiGraph.Widget w) {
      if (w.Rect == null) return false;
      double U = Ui.Unit(PW, PH); var r = ScreenMap.Place(w.Rect, PW, PH, U);
      return r.W > PW * 0.9 && r.H > PH * 0.9;
    }
    /// <summary>The prefab gives the widget no size (a layout group places it: the city's bottom bar ...) or no place inside the window (a parent
    /// moves it): it is pressed only on its live rect.</summary>
    public static bool LayoutOnly(UiGraph.Widget w) { return NeedsLive(w, PW, PH, Ui.Unit(PW, PH)); }
    public static bool NeedsLive(UiGraph.Widget w, double W, double H, double U) {
      if (w.Rect == null) return true;
      for (int i = 0; i < 8; i += 2) if ("LCRTMB".IndexOf((char)w.Rect[i]) < 0) return true;   // (an anchor the generator could not resolve)
      var r = ScreenMap.Place(w.Rect, W, H, U);
      return r.W < 4 || r.H < 4 || r.CX < 0 || r.CX > W || r.CY < 0 || r.CY > H;
    }
    static bool OnNeverList(string form, UiGraph.Widget w) {
      var e = ScreenMap.ByName(form); if (e == null) return false;
      foreach (var n in e.Never) if (n == w.Field || w.Path.EndsWith("/" + n) || w.Path.Contains("/" + n + "/") || w.Path.StartsWith(n + "/") || w.Path == n) return true;
      return false;
    }
    /// <summary>The widget's basic safety (one hidden in the prefab is pressed only where the live read finds it): not a list template, measured, no request / confirm / danger, no never-word, not
    /// a never-click button of the screen map. Why it is not (null = it is).</summary>
    public static string Unsafe(string form, UiGraph.Widget w) {
      if (w == null) return "not in the graph";
      if (w.Template) return "a list template";
      if (!w.Safe) return w.Danger ? "dangerous" : "opens a confirm";
      if (!Measured(w)) return "not measured";
      foreach (var e in w.Effects) { if (e.Kind == 'r') return "makes a request"; if (e.Danger) return "a dangerous effect"; if (e.Kind == 'j') return "a jump"; }
      string nw = NeverWord(OwnPart(w.Path)); if (nw != null) return "the word '" + nw + "'";
      if (OnNeverList(form, w)) return "a never-click button of the screen map";
      return null;
    }
    /// <summary>A form the explorer may go to: in the screen map and the graph, left by its back arrow / × / Escape / the graph's close widget;
    /// never a dangerous, wait, overlay or app screen (K) form.</summary>
    public static bool FormOk(string name) {
      if (string.IsNullOrEmpty(name) || name == City || UiGraph.ByName(name) == null || ScreenMap.ByName(name) == null) return false;
      if (Array.IndexOf(OfferForms.KnownScreens, name) >= 0 && Array.IndexOf(PassScreens, name) < 0 || OfferForms.IsIgnoredOverlay(name)) return false;
      char c = ScreenMap.ClassOf(name);
      if (c == 'K') return Array.IndexOf(PassScreens, name) >= 0 && CloseWidget(name) != null;
      if (c == 'F' || c == 'P') return ScreenMap.ByName(name).Rect != null;
      if (c == 'E') return true;
      if (c == 'U') return CloseWidget(name) != null;
      return false;
    }
    /// <summary>The app's own screens the explorer goes through (the heroes, the inventory: their back arrow is the graph's close widget).</summary>
    public static readonly string[] PassScreens = { "Form_CharactorMain", "Form_BackpackIntegration", "Form_EquipSubliming", "Form_EquipGemMain", "Form_EquipArtifactIntensify" };
    /// <summary>The form on top by the window table's order: overlays left out, and a form neither the screen map nor the UI graph knows (a part a
    /// screen builds late); "" = none, null = no order.</summary>
    public static string TopOf(List<string> order) {
      if (order == null) return null;
      foreach (var n in order) if (ScreenMap.ClassOf(n) != 'O' && !OfferForms.IsIgnoredOverlay(n) && (ScreenMap.ByName(n) != null || UiGraph.ByName(n) != null)) return n;
      return "";
    }
    /// <summary>The graph's close widget of a form (its press only closes the form; safe, shown, measured); null = none.</summary>
    public static UiGraph.Widget CloseWidget(string form) {
      var f = UiGraph.ByName(form); if (f == null) return null;
      UiGraph.Widget catcher = null;
      foreach (var w in f.Widgets) {
        if (w.Effects.Count == 0) continue;
        bool only = true, self = false; foreach (var e in w.Effects) { if (e.Kind == 'c') self = true; else if (e.Kind != 'x') only = false; }   // (x: the tips it closes with it)
        if (!only || !self) continue;
        if (Unsafe(form, w) == null) return w;
        if (catcher == null && FullScreen(w) && w.Shown && !w.Template && w.Safe) catcher = w;
      }
      return catcher;   // (else a tap catcher over the whole window: pressed where no other widget of the form is)
    }
    /// <summary>The form a widget opens when the explorer may press it for that (exactly one effect: open a form it may go to); null = no.</summary>
    public static string OpenOf(string form, UiGraph.Widget w) {
      if (Unsafe(form, w) != null || w.Toggle) return null;
      string t = null;   // (one open; a tab handler beside it is the form's own UI; a close of the form itself: the new one takes its place - the explorer finds the form under it when it goes back)
      foreach (var e in w.Effects) { if (e.Kind == 'o') { if (t != null) return null; t = e.Target; } else if (e.Kind == 'c') { } else if (e.Kind != 't' || e.Target.IndexOf("lock", StringComparison.OrdinalIgnoreCase) >= 0) return null; }
      if (t == null || t.StartsWith("?") || t == form) return null;
      return FormOk(t) ? t : null;
    }
    static readonly Regex TabRx = new Regex("tab|page|toggle|mode|switch|select", RegexOptions.IgnoreCase);
    /// <summary>A tab of the form the explorer may press (only tab switches by a tab handler; same safety).</summary>
    public static bool TabOk(string form, UiGraph.Widget w) {
      if (Unsafe(form, w) != null || w.Effects.Count == 0) return false;
      foreach (var e in w.Effects) if (e.Kind != 't' || !TabRx.IsMatch(e.Target) || e.Target.IndexOf("lock", StringComparison.OrdinalIgnoreCase) >= 0) return false;
      return true;
    }

    public sealed class Edge { public string From, To; public UiGraph.Widget Widget; public override string ToString() { return From + " [" + Widget.Path + "] -> " + To; } }
    static Dictionary<string, Edge> parent; static List<string> preorder; static Dictionary<string, List<Edge>> alts;
    /// <summary>Every widget of the tree parent that opens the form (the tree's own first: shown and measured ones before the others).</summary>
    public static List<Edge> Edges(string to) { Build(); List<Edge> l; return alts.TryGetValue(to, out l) ? l : new List<Edge>(); }
    /// <summary>The first of <see cref="Edges"/> whose widget is not in <paramref name="dead"/> ("from>path"); null = none left.</summary>
    public static Edge EdgeTo(string to, ICollection<string> dead) {
      foreach (var e in Edges(to)) if (dead == null || !dead.Contains(e.From + ">" + e.Widget.Path)) return e;
      return null;
    }
    /// <summary>The breadth-first tree of the safe opens from the city: each form's edge from its parent.</summary>
    public static Dictionary<string, Edge> Tree() { Build(); return parent; }
    /// <summary>Every form of the tree in depth-first order (children in the graph's widget order), the city left out.</summary>
    public static List<string> Order() { Build(); return preorder; }
    static void Build() {
      if (parent != null) return;
      var par = new Dictionary<string, Edge>(); var kids = new Dictionary<string, List<string>>(); var q = new Queue<string>(); q.Enqueue(City);
      var seen = new HashSet<string> { City };
      var al = new Dictionary<string, List<Edge>>();
      while (q.Count > 0) {
        string f = q.Dequeue(); var fo = UiGraph.ByName(f); if (fo == null) continue;
        var ws = new List<UiGraph.Widget>(); foreach (var w in fo.Widgets) if (w.Shown && !LayoutOnly(w)) ws.Add(w);
        foreach (var w in fo.Widgets) if (!(w.Shown && !LayoutOnly(w))) ws.Add(w);   // (shown and measured first: pressed where the graph has them)
        var mine = new List<string>();
        foreach (var w in ws) {
          string t = OpenOf(f, w); if (t == null) continue;
          if (!seen.Contains(t)) { seen.Add(t); mine.Add(t); par[t] = new Edge { From = f, To = t, Widget = w }; al[t] = new List<Edge>(); }
          if (mine.Contains(t)) al[t].Add(new Edge { From = f, To = t, Widget = w });
        }
        var kl = new List<string>();   // (the children in the graph's widget order)
        foreach (var w in fo.Widgets) { string t = OpenOf(f, w); if (t != null && mine.Contains(t) && !kl.Contains(t)) kl.Add(t); }
        if (kl.Count > 0) kids[f] = kl;
        foreach (var t in kl) q.Enqueue(t);
      }
      alts = al;
      var pre = new List<string>(); var st = new Stack<string>(); st.Push(City);
      while (st.Count > 0) {
        string f = st.Pop(); if (f != City) pre.Add(f);
        List<string> k; if (kids.TryGetValue(f, out k)) for (int i = k.Count - 1; i >= 0; i--) st.Push(k[i]);
      }
      parent = par; preorder = pre;
    }
    /// <summary>The forms from the city to <paramref name="form"/> (the city first); null = not in the tree.</summary>
    public static List<string> Chain(string form) {
      Build(); var l = new List<string>(); string f = form;
      while (f != City) { Edge e; if (!parent.TryGetValue(f, out e)) return null; l.Insert(0, f); f = e.From; }
      l.Insert(0, City); return l;
    }
    /// <summary>A run's forms: the whole order from place budget * rot on (wrapping), so a run that skips some takes the next ones.</summary>
    public static List<string> Run(int budget, int rot) {
      Build(); var l = new List<string>(); int n = preorder.Count; if (n == 0) return l;
      int s = (int)(((long)Math.Max(1, budget) * Math.Max(0, rot)) % n);
      for (int i = 0; i < n; i++) l.Add(preorder[(s + i) % n]);
      return l;
    }
  }

  /// <summary>What the host read this tick for the explorer.</summary>
  public sealed class ExploreView {
    public long Now; public bool Front = true;
    /// <summary>The window table's z-order, the top first; null = not certain this tick.</summary>
    public List<string> Order;
    public double W, H, U;
    /// <summary>The live rect (client px: x, y, w, h) of a form's widget by its Lua field; null = not read / not visible.</summary>
    public Func<string, string, double[]> Live;
    /// <summary>An offer pop-up's × (src/OfferForms.cs ClosePoint with the layout); null = none known.</summary>
    public Func<string, GeoRect> OfferClose;
  }
  public enum ExploreKind { None, Click, Escape, Done }
  public sealed class ExploreAct {
    public ExploreKind Kind; public ClickTarget Target; public string Form = "", Widget = "", To = ""; public double X, Y; public double[] LiveRect;
    public override string ToString() { return Kind + " " + Target + " " + Form + (Widget != "" ? " [" + Widget + "]" : "") + (To != "" ? " -> " + To : "") + (Kind == ExploreKind.Click ? " @" + (int)X + "," + (int)Y : ""); }
  }
  public sealed class ExploreMismatch {
    public string Form = "", Kind = "", What = "", Widget = "";
    public string Json() { return "{\"form\":" + MiniJson.Quote(Form) + ",\"kind\":" + MiniJson.Quote(Kind) + ",\"what\":" + MiniJson.Quote(What) + ",\"widget\":" + MiniJson.Quote(Widget) + "}"; }
  }

  /// <summary>One run of the explorer: Tick gives the next press, the host sends it through the gate and says Sent / Denied.</summary>
  public sealed class Explorer {
    public const int OpenMs = 6000, CloseMs = 4500, SettleMs = 900, TabMs = 1500, WaitFormMs = 30000, OrderMissMs = 15000, GateWaitMs = 15000;
    public const int MaxTabs = 3, MaxWidgets = 30, MaxPopups = 25;
    public const long CapMs = 30 * 60 * 1000;
    public readonly int Budget, Rot; public readonly string RunId;
    public string State = "running", Why = "";
    public bool Over { get { return State != "running"; } }
    public int Visited, OkForms, Popups, Presses;
    public readonly List<ExploreMismatch> Mismatches = new List<ExploreMismatch>();
    public readonly List<string> Skips = new List<string>();
    public readonly List<string> Lines = new List<string>();   // the log lines since the host took them
    readonly StringBuilder forms = new StringBuilder();
    readonly List<string> targets; int idx;
    readonly List<string> stack = new List<string> { ExplorePlan.City };
    readonly HashSet<string> dead = new HashSet<string>(), done = new HashSet<string>();
    enum Phase { Idle, Open, Close, Tab, Settle }
    Phase ph = Phase.Idle; ExploreAct cur; long sentAt, startAt = -1, orderMissAt = -1, waitAt = -1, gateWaitAt = -1, topSince; string lastTop = "";
    string closeHow = ""; bool closeFallback;
    // the target form being looked at: its mismatches, its tabs still to press, its result line
    string insForm = ""; List<ExploreMismatch> insMis; Queue<UiGraph.Widget> tabs; string insOrder = "", insClose = "";

    public Explorer(int budget, int rot, string run) {
      Budget = Math.Max(1, Math.Min(ExploreCmd.MaxBudget, budget)); Rot = Math.Max(0, rot); RunId = run ?? "";
      targets = ExplorePlan.Run(Budget, Rot);
      Say("explore: start, budget " + Budget + ", rotation " + Rot + ", " + ExplorePlan.Order().Count + " forms reachable by safe presses (graph of game " + UiGraph.GameVersion + ")");
    }
    void Say(string s) { Lines.Add(s); }
    public void Stop(string why) {
      if (Over) return;
      FinishForm();
      State = "stopped"; Why = why ?? ""; Say("explore: STOP " + Why);
    }
    void Finish() { if (Over) return; FinishForm(); State = "done"; Say("explore: done, " + Visited + " forms, " + OkForms + " ok, " + Mismatches.Count + " mismatches, " + Popups + " pop-ups closed"); }
    public long Ms(long now) { return startAt < 0 ? 0 : now - startAt; }

    static string TopOf(List<string> order) { return ExplorePlan.TopOf(order); }
    void Mis(string form, string kind, string what, string widget) {
      var m = new ExploreMismatch { Form = form, Kind = kind, What = what, Widget = widget ?? "" };
      Mismatches.Add(m); if (form == insForm && insMis != null) insMis.Add(m); else Say("explore: " + form + " mismatch " + kind + ": " + what);
    }

    /// <summary>The next press (None = wait this tick; Done = the run is over). Nothing changes until the host says Sent / Denied.</summary>
    public ExploreAct Tick(ExploreView v) {
      if (Over) return new ExploreAct { Kind = ExploreKind.Done };
      if (startAt < 0) startAt = v.Now;
      if (v.Now - startAt > CapMs) { Stop("the time cap of " + CapMs / 60000 + " min"); return new ExploreAct { Kind = ExploreKind.Done }; }
      string top = TopOf(v.Order);
      if (top == null || top == "") {
        if (orderMissAt < 0) orderMissAt = v.Now;
        if (v.Now - orderMissAt > OrderMissMs) Stop("the window table's order not read for " + OrderMissMs / 1000 + " s");
        return None;
      }
      orderMissAt = -1;
      if (top != lastTop) { lastTop = top; topSince = v.Now; }
      char c = ScreenMap.ClassOf(top);
      if (c == 'D') { Stop("a dangerous form is on top: " + top); return new ExploreAct { Kind = ExploreKind.Done }; }
      if (c == 'W') {
        if (waitAt < 0) waitAt = v.Now;
        if (v.Now - waitAt > WaitFormMs) Stop(top + " (wait) stays for " + WaitFormMs / 1000 + " s");
        return None;
      }
      waitAt = -1;
      switch (ph) {
        case Phase.Open:
          if (top == cur.To) { stack.Add(top); Arrived(top, v); return None; }
          if (top == cur.Form) {
            if (v.Now - sentAt < OpenMs) return None;
            Mis(cur.To, "reach", "not opened by " + cur.Form + " [" + cur.Widget + "] in " + OpenMs / 1000 + " s", cur.Widget);
            dead.Add(cur.Form + ">" + cur.Widget); Skip(cur.To); ph = Phase.Idle; return None;
          }
          if (v.Now - topSince < 400) return None;   // (the form on top just changed: one more read)
          if (stack.Contains(top)) { Mis(cur.To, "reach", "the press on " + cur.Form + " [" + cur.Widget + "] closed it (" + top + " on top)", cur.Widget); Trim(top); }
          else { Mis(cur.To, "reach", cur.Form + " [" + cur.Widget + "] opened " + top + " instead", cur.Widget); stack.Add(top); }
          dead.Add(cur.Form + ">" + cur.Widget); Skip(cur.To); ph = Phase.Idle; return None;
        case Phase.Close:
          if (v.Order.IndexOf(cur.Form) < 0) {
            if (cur.Form == insForm) insClose = closeFallback ? "escape" : "ok";
            int i = stack.LastIndexOf(cur.Form); if (i > 0) stack.RemoveRange(i, stack.Count - i);
            if (cur.Form == insForm) FinishForm();
            ph = Phase.Idle; return None;
          }
          if (v.Now - sentAt < CloseMs) return None;
          if (top != cur.Form) { ph = Phase.Idle; return None; }   // (a pop-up came over it: closed first, then this one again)
          Mis(cur.Form, "close", (closeFallback ? "Escape" : closeHow) + " did not close it in " + CloseMs / 1000 + " s", cur.Widget);
          if (cur.Form == insForm) insClose = "failed";
          if (!closeFallback && EscapeOk(cur.Form)) { closeFallback = true; return new ExploreAct { Kind = ExploreKind.Escape, Target = ClickTarget.ExploreEscape, Form = cur.Form }; }
          Stop("cannot leave " + cur.Form); return new ExploreAct { Kind = ExploreKind.Done };
        case Phase.Tab:
          if (v.Now - sentAt < TabMs) return None;
          if (top != cur.Form && !stack.Contains(top)) { Mis(cur.Form, "tab", "the tab [" + cur.Widget + "] opened " + top, cur.Widget); stack.Add(top); }
          else if (top != cur.Form) { Mis(cur.Form, "tab", "the tab [" + cur.Widget + "] closed the form", cur.Widget); Trim(top); FinishForm(); }
          ph = Phase.Idle; return None;
        case Phase.Settle:
          if (top != stack[stack.Count - 1]) { ph = Phase.Idle; break; }
          if (v.Now - topSince < SettleMs) return None;
          Inspect(top, v); ph = Phase.Idle; return None;
      }
      return Decide(top, v);
    }
    static readonly ExploreAct None = new ExploreAct { Kind = ExploreKind.None };

    void Arrived(string top, ExploreView v) {
      ph = Phase.Settle; topSince = v.Now;
    }
    void Trim(string top) { int i = stack.LastIndexOf(top); if (i >= 0 && i < stack.Count - 1) stack.RemoveRange(i + 1, stack.Count - i - 1); }
    void Skip(string form) { if (idx < targets.Count && targets[idx] == form) { done.Add(form); idx++; } }

    /// <summary>The target form on top and settled: the window table's order, the graph's shown widgets live, its tabs to press.</summary>
    void Inspect(string form, ExploreView v) {
      if (idx >= targets.Count || targets[idx] != form || done.Contains(form)) return;
      done.Add(form); idx++; Visited++;
      insForm = form; insMis = new List<ExploreMismatch>(); insClose = "";
      var ord = new List<string>(); foreach (var n in v.Order) { ord.Add(n); if (ord.Count >= 8) break; }
      insOrder = string.Join(" > ", ord.ToArray());
      var f = UiGraph.ByName(form); int n0 = 0;
      tabs = new Queue<UiGraph.Widget>();
      if (f != null && v.Live != null) {
        foreach (var w in f.Widgets) {
          if (!w.Shown || w.Template || w.Rect == null) continue;
          if (ExplorePlan.TabOk(form, w) && tabs.Count < MaxTabs) tabs.Enqueue(w);
          string fld = LiveField(w); if (fld == null) continue;
          if (++n0 > MaxWidgets) break;
          var g = ScreenMap.Place(w.Rect, v.W, v.H, v.U);
          if (g.W > v.W * 0.9 && g.H > v.H * 0.9) continue;   // (a full-screen tap catcher)
          var lr = v.Live(form, fld);
          if (lr == null) { Mis(form, "widget", "[" + w.Path + "] not visible live", w.Path); continue; }
          if (ExplorePlan.NeedsLive(w, v.W, v.H, v.U)) continue;   // (no size / place in the prefab: only whether it is there)
          double tol = Math.Max(10, 0.03 * v.U);
          if (fld == w.Field) {
            double dx = (lr[0] + lr[2] / 2) - g.CX, dy = (lr[1] + lr[3] / 2) - g.CY;
            if (Math.Abs(dx) > tol || Math.Abs(dy) > tol || Math.Abs(lr[2] - g.W) > Math.Max(tol, 0.3 * g.W) || Math.Abs(lr[3] - g.H) > Math.Max(tol, 0.3 * g.H))
              Mis(form, "rect", "[" + w.Path + "] live " + R(lr[0]) + "," + R(lr[1]) + " " + R(lr[2]) + "x" + R(lr[3]) + ", graph " + R(g.X) + "," + R(g.Y) + " " + R(g.W) + "x" + R(g.H), w.Path);
          } else if (g.CX < lr[0] - tol || g.CX > lr[0] + lr[2] + tol || g.CY < lr[1] - tol || g.CY > lr[1] + lr[3] + tol)
            Mis(form, "rect", "[" + w.Path + "] outside its field " + fld + " live " + R(lr[0]) + "," + R(lr[1]) + " " + R(lr[2]) + "x" + R(lr[3]), w.Path);
        }
      }
      if (f == null || v.Live == null) foreach (var w in f != null ? f.Widgets : new List<UiGraph.Widget>()) if (ExplorePlan.TabOk(form, w) && tabs.Count < MaxTabs) tabs.Enqueue(w);
    }
    static string R(double d) { return ((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture); }
    /// <summary>The Lua field a widget is read live by: its own when it is an m_ one, else the nearest m_ part of its path; null = none.</summary>
    public static string LiveField(UiGraph.Widget w) {
      if (w.Field != null && w.Field.StartsWith("m_")) return w.Field;
      var p = w.Path.Split('/');
      for (int i = p.Length - 1; i >= 0; i--) if (p[i].StartsWith("m_")) return p[i];
      return null;
    }
    /// <summary>The graph's widget under the point that takes a press (UiGraph.WidgetAt, the smallest), a tap catcher behind the form's content
    /// (only closes it, over half the window) left out: it lies under the buttons.</summary>
    public static UiGraph.Widget WidgetAt(string form, double x, double y, double W, double H, double U) {
      var f = UiGraph.ByName(form); if (f == null) return null;
      UiGraph.Widget best = null; double ba = double.MaxValue;
      foreach (var w in f.Widgets) {
        if (!w.Shown || w.Template || w.Rect == null) continue;
        var r = ScreenMap.Place(w.Rect, W, H, U);
        if (r.W < 2 || r.H < 2 || r.W > W * 0.9 && r.H > H * 0.9) continue;
        if (r.W * r.H > 0.5 * W * H && Catcher(w)) continue;
        if (x >= r.X && x <= r.Right && y >= r.Y && y <= r.Bottom && r.W * r.H < ba) { best = w; ba = r.W * r.H; }
      }
      return best;
    }
    static bool Catcher(UiGraph.Widget w) { if (w.Effects.Count == 0) return false; foreach (var e in w.Effects) if (e.Kind != 'c') return false; return true; }
    /// <summary>Two widgets whose press opens the same one form (a button inside a button that does the same).</summary>
    public static bool Same(string form, UiGraph.Widget a, UiGraph.Widget b) {
      if (a == null || b == null) return false;
      string x = ExplorePlan.OpenOf(form, a); return x != null && x == ExplorePlan.OpenOf(form, b);
    }
    /// <summary>The widget is the only one of the form read by that m_ field (a button inside its m_ parent: the parent's live rect is its own).</summary>
    public static bool OnlyUnder(string form, string field, UiGraph.Widget w) {
      var f = UiGraph.ByName(form); if (f == null) return false;
      foreach (var x in f.Widgets) if (x != w && (x.Field == field || LiveField(x) == field)) return false;
      return true;
    }
    /// <summary>Two widgets of the form have this field name (its live read names neither for sure).</summary>
    public static bool SharedField(string form, string field) {
      var f = UiGraph.ByName(form); if (f == null) return false; int n = 0;
      foreach (var w in f.Widgets) if (w.Field == field && ++n > 1) return true;
      return false;
    }
    /// <summary>The target form's result: its log line and its part of the JSON (once, when it is left or the run ends).</summary>
    void FinishForm() {
      if (insForm == "") return;
      string form = insForm; var mis = insMis ?? new List<ExploreMismatch>();
      if (insClose == "") insClose = "not left";
      bool ok = mis.Count == 0 && insClose == "ok";
      if (ok) OkForms++;
      var what = new List<string>(); foreach (var m in mis) what.Add(m.Kind + ": " + m.What);
      if (insClose != "ok" && insClose != "failed") what.Add("close: " + insClose);
      Say("explore: " + form + (ok ? " ok" : " mismatch " + string.Join("; ", what.ToArray())) + " (order " + insOrder + ")");
      if (forms.Length > 0) forms.Append(',');
      forms.Append("{\"form\":" + MiniJson.Quote(form) + ",\"ok\":" + (ok ? "true" : "false") + ",\"close\":" + MiniJson.Quote(insClose) + ",\"order\":" + MiniJson.Quote(insOrder) + ",\"mismatches\":" + mis.Count + "}");
      insForm = ""; insMis = null; tabs = null;
    }

    static bool EscapeOk(string form) {
      char c = ScreenMap.ClassOf(form); var e = ScreenMap.ByName(form);
      return form != ExplorePlan.City && (c == 'E' || (c == 'F' || c == 'P' || c == 'U') && e != null && e.Esc == "Handle");
    }

    /// <summary>The press that leaves the form on top: the offer's ×, the back arrow / × of the screen map, Escape, the graph's close widget.</summary>
    ExploreAct CloseAct(string top, ExploreView v) {
      char c = ScreenMap.ClassOf(top);
      if (top == ExplorePlan.City || c == 'D' || c == 'W') return null;
      if (c == 'K') {
        if (Array.IndexOf(ExplorePlan.PassScreens, top) < 0) return null;
        var kw = ExplorePlan.CloseWidget(top); var ka = kw != null ? PointOn(top, kw, v) : null;
        if (ka != null) ka.Target = ClickTarget.ExploreClose;
        return ka;
      }
      if (OfferForms.IsOffer(top) && v.OfferClose != null) {
        var p = v.OfferClose(top);
        if (p != null) return new ExploreAct { Kind = ExploreKind.Click, Target = ClickTarget.ExploreClose, Form = top, Widget = "offer ×", X = p.X, Y = p.Y };
      }
      if (c == 'F' || c == 'P') {
        var p = ScreenMap.ExitPoint(top, v.W, v.H, v.U);
        if (p != null) return new ExploreAct { Kind = ExploreKind.Click, Target = ClickTarget.ExploreClose, Form = top, Widget = c == 'F' ? "back arrow" : "×", X = p.X, Y = p.Y };
      }
      if (c == 'E') return new ExploreAct { Kind = ExploreKind.Escape, Target = ClickTarget.ExploreEscape, Form = top, Widget = "Escape" };
      var w = ExplorePlan.CloseWidget(top);
      if (w != null) { var a = PointOn(top, w, v); if (a != null) { a.Target = ClickTarget.ExploreClose; return a; } }
      return EscapeOk(top) ? new ExploreAct { Kind = ExploreKind.Escape, Target = ClickTarget.ExploreEscape, Form = top, Widget = "Escape" } : null;
    }
    /// <summary>A press on a graph widget of the form: the centre of its live rect (or the graph's), at a point where the graph's smallest
    /// widget is this one (else a quarter's centre); null = no such point.</summary>
    static ExploreAct PointOn(string form, UiGraph.Widget w, ExploreView v) { string why; return PointOn(form, w, v, out why); }
    static ExploreAct PointOn(string form, UiGraph.Widget w, ExploreView v, out string why) {
      why = "";
      if (ExplorePlan.FullScreen(w)) {   // a tap catcher: a point of its own, near an edge, where no other widget of the form lies
        double[][] es = { new[] { 0.5, 0.04 }, new[] { 0.04, 0.5 }, new[] { 0.96, 0.5 }, new[] { 0.5, 0.96 }, new[] { 0.06, 0.94 }, new[] { 0.94, 0.94 } };
        foreach (var p in es) {
          double x = p[0] * v.W, y = p[1] * v.H;
          if (UiGraph.WidgetAt(form, x, y, v.W, v.H, v.U, false) == null && !ScreenMap.OnNever(form, x, y, v.W, v.H, v.U))
            return new ExploreAct { Kind = ExploreKind.Click, Form = form, Widget = w.Path, X = x, Y = y };
        }
        why = "no free point for the tap catcher"; return null;
      }
      string fld = LiveField(w);
      bool own = fld != null && (fld == w.Field || OnlyUnder(form, fld, w)) && !SharedField(form, fld);   // (its own field, or the m_ parent it alone fills)
      double[] lr = own && v.Live != null ? v.Live(form, fld) : null;
      if (lr == null && (ExplorePlan.NeedsLive(w, v.W, v.H, v.U) || !w.Shown || own && v.Live != null)) { why = own ? "not visible live" : "no live field (" + (fld ?? "none") + ")"; return null; }   // (its own field read live and not there: not pressed)   // (placed by a layout group: only where the game has it now)
      var g = lr != null ? new GeoRect { X = lr[0], Y = lr[1], W = lr[2], H = lr[3] } : ScreenMap.Place(w.Rect, v.W, v.H, v.U);
      if (g.W < 2 || g.H < 2) { why = "no size"; return null; }
      double[][] pts = { new[] { 0.5, 0.5 }, new[] { 0.3, 0.3 }, new[] { 0.7, 0.3 }, new[] { 0.3, 0.7 }, new[] { 0.7, 0.7 } };
      foreach (var p in pts) {
        double x = g.X + p[0] * g.W, y = g.Y + p[1] * g.H;
        if (x < 1 || y < 1 || x > v.W - 2 || y > v.H - 2) continue;
        var at = WidgetAt(form, x, y, v.W, v.H, v.U);
        if (at != null && at != w && !Same(form, at, w) && v.Live != null && !string.IsNullOrEmpty(at.Field) && !SharedField(form, at.Field)) {
          var ar = v.Live(form, at.Field);   // (the smaller widget there is not shown now: the press is this one's - the gate checks it the same way)
          if (ar == null || !(x >= ar[0] && x <= ar[0] + ar[2] && y >= ar[1] && y <= ar[1] + ar[3])) at = w;
        }
        if (at == w || at == null && lr != null || Same(form, at, w)) return new ExploreAct { Kind = ExploreKind.Click, Form = form, Widget = w.Path, X = x, Y = y, LiveRect = lr };
        why = at == null ? "off the graph's widget" : at.Path + " lies over it";
      }
      if (why == "") why = "off the window";
      return null;
    }

    /// <summary>No way back found this tick: a live read may have missed it - read again for NoWayMs, then stop.</summary>
    ExploreAct NoWay(string why, long now) {
      if (noWayAt < 0) noWayAt = now;
      if (now - noWayAt < NoWayMs) return None;
      Stop(why); return new ExploreAct { Kind = ExploreKind.Done };
    }
    long noWayAt = -1; readonly Dictionary<string, int> liveMiss = new Dictionary<string, int>();
    public const int NoWayMs = 3000;

    ExploreAct Decide(string top, ExploreView v) {
      string exp = stack[stack.Count - 1];
      if (top != exp) {
        if (stack.Contains(top)) { if (stack.IndexOf(insForm) > stack.IndexOf(top) || insForm != "" && !stack.Contains(insForm)) { insClose = "closed by the game"; FinishForm(); } Trim(top); return Decide(top, v); }
        // a form nobody asked for on top (the game's own pop-up, or what a press opened instead): closed first
        var ca = CloseAct(top, v);
        if (ca == null) return NoWay(top + " is on top and the explorer has no way to close it", v.Now);
        noWayAt = -1;
        if (!stack.Contains(top) && Popups >= MaxPopups) { Stop("more than " + MaxPopups + " pop-ups"); return new ExploreAct { Kind = ExploreKind.Done }; }
        return ca;
      }
      // the target form's tabs first
      if (insForm == top && tabs != null && tabs.Count > 0) {
        var w = tabs.Peek(); var a = PointOn(top, w, v);
        if (a == null) { tabs.Dequeue(); Skips.Add(top + " [" + w.Path + "]: no clear point"); return None; }
        a.Target = ClickTarget.ExploreTab; return a;
      }
      // the next target
      while (idx < targets.Count && Visited < Budget) {
        string t = targets[idx];
        var ch = ExplorePlan.Chain(t);
        bool bad = ch == null || done.Contains(t);
        if (!bad) for (int i = 0; i + 1 < ch.Count; i++) if (ExplorePlan.EdgeTo(ch[i + 1], dead) == null) { bad = true; break; }
        if (bad) { idx++; continue; }
        // the stack is a start of the chain: the next form down; else up first
        bool prefix = stack.Count <= ch.Count;
        if (prefix) for (int i = 0; i < stack.Count; i++) if (stack[i] != ch[i]) { prefix = false; break; }
        if (prefix && stack.Count < ch.Count) {
          var e = ExplorePlan.EdgeTo(ch[stack.Count], dead);
          string pw; var a = PointOn(top, e.Widget, v, out pw);
          if (a == null && pw == "not visible live") {   // (a live read can miss once: read again on the next ticks)
            string key = top + ">" + e.Widget.Path; int n; liveMiss.TryGetValue(key, out n); liveMiss[key] = ++n;
            if (n < 3) return None;
          }
          if (a == null) { Skips.Add(top + " [" + e.Widget.Path + "]: " + pw); Say("explore: skip " + top + " [" + e.Widget.Path + "] -> " + e.To + ": " + pw); dead.Add(top + ">" + e.Widget.Path); continue; }
          a.Target = ClickTarget.ExploreOpen; a.To = e.To; return a;
        }
        break;
      }
      bool more = idx < targets.Count && Visited < Budget;
      if (!more && stack.Count == 1) { Finish(); return new ExploreAct { Kind = ExploreKind.Done }; }
      var cl = CloseAct(top, v);
      if (cl == null) return NoWay(top + ": no way back found (" + (ExplorePlan.CloseWidget(top) != null ? "its close widget not visible live" : "none known") + ")", v.Now);
      noWayAt = -1;
      return cl;
    }

    /// <summary>The host sent the press (the gate allowed it).</summary>
    public void Sent(ExploreAct a, long now) {
      if (a == null || a.Kind == ExploreKind.None || a.Kind == ExploreKind.Done) return;
      Presses++; cur = a; sentAt = now; gateWaitAt = -1;
      switch (a.Target) {
        case ClickTarget.ExploreOpen: ph = Phase.Open; break;
        case ClickTarget.ExploreTab: ph = Phase.Tab; if (tabs != null && tabs.Count > 0) tabs.Dequeue(); break;
        default:
          if (ph != Phase.Close) closeFallback = false;
          if (stack[stack.Count - 1] != a.Form) Popups++;
          closeHow = a.Widget; ph = Phase.Close;
          if (a.Kind == ExploreKind.Escape && closeFallback) Say("explore: " + a.Form + ": Escape as the fallback");
          break;
      }
    }
    /// <summary>The gate said no: a stop ends the run; a wait is waited for (GateWaitMs, then a skip); any other no skips the press.</summary>
    public void Denied(ExploreAct a, GateResult r, bool halted, long now) {
      if (a == null) return;
      if (halted) { Stop("the click gate stopped: " + r.Reason + " - " + r.Detail); return; }
      if (r.Verdict == GateVerdict.Wait) {
        if (gateWaitAt < 0) gateWaitAt = now;
        if (now - gateWaitAt < GateWaitMs) return;
      }
      gateWaitAt = -1;
      Skips.Add(a.Form + " [" + a.Widget + "]: " + r.Reason + " " + r.Detail);
      Say("explore: skip " + a + " (" + r.Reason + ": " + r.Detail + ")");
      if (a.Target == ClickTarget.ExploreOpen) { dead.Add(a.Form + ">" + a.Widget); if (idx < targets.Count && targets[idx] == a.To) idx++; }
      else if (a.Target == ClickTarget.ExploreTab) { if (tabs != null && tabs.Count > 0) tabs.Dequeue(); }
      else if (!closeFallback && EscapeOk(a.Form) && a.Kind != ExploreKind.Escape) { closeFallback = true; }
      else Stop("cannot leave " + a.Form + " (" + r.Reason + ")");
    }

    /// <summary>The run's result (explore.json).</summary>
    public string Json(long now, string app, string game) {
      var sb = new StringBuilder();
      sb.Append("{\"v\":1,\"run\":").Append(MiniJson.Quote(RunId)).Append(",\"app\":").Append(MiniJson.Quote(app ?? "")).Append(",\"game\":").Append(MiniJson.Quote(game ?? ""))
        .Append(",\"graph\":").Append(MiniJson.Quote(UiGraph.GameVersion)).Append(",\"budget\":").Append(Budget).Append(",\"rot\":").Append(Rot)
        .Append(",\"state\":").Append(MiniJson.Quote(State)).Append(",\"why\":").Append(MiniJson.Quote(Why))
        .Append(",\"secs\":").Append(Ms(now) / 1000).Append(",\"visited\":").Append(Visited).Append(",\"ok\":").Append(OkForms)
        .Append(",\"popups\":").Append(Popups).Append(",\"presses\":").Append(Presses).Append(",\"reachable\":").Append(ExplorePlan.Order().Count)
        .Append(",\"forms\":[").Append(forms).Append("],\"mismatches\":[");
      for (int i = 0; i < Mismatches.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Mismatches[i].Json()); }
      sb.Append("],\"skips\":[");
      for (int i = 0; i < Skips.Count && i < 200; i++) { if (i > 0) sb.Append(','); sb.Append(MiniJson.Quote(Skips[i])); }
      sb.Append("]}");
      return sb.ToString();
    }
  }
}
