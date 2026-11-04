// Regression replay of an owner's recording (tests/replays/<name>/, made by tools/rec-to-test.mjs from a recording folder).
//
//   dotnet exec ... RecReplay.dll --check <tests/replays/name>
//
// The fixture's frames.jsonl is replayed by RecReplay's loop on the REAL code (ScreenTracker, LocateGuide, SellHint, and the pilots: SellPilot from the
// recorded sell screen, AutoPilot / HeroPilot from the views the recorder kept in the "ap" / "hp" slots); every tick's result is kept (ReplayCheck.Tick)
// and expect.json is judged on them:
//   {"name":..,"source":"<recording id>","status":"pass"|"pending","pending":"why","checks":[{"id":..,"text":"what must happen","type":..,..}]}
// status "pass": every check must hold. status "pending": the bug is not fixed yet (or the recording lacks the data): at least one check must fail,
// and when all hold the run fails with "flip it to pass" - the fixture then guards the fix for good.
// Check types (the numbers are ms of the overlay clock):
//   replay-agrees    {max}                         recorded vs replayed screen / step / rectangles differ in at most max frames (guide and hint modes)
//   click-within     {from:"liveTarget"|"target", ms}   the item pilot clicks the plan's item (its Cell) within ms from the first tick the item can be clicked
//   no-click-empty   {}                             every item-pilot click while the live grid is read names a cell (Cell != 0): never a click on an empty cell
//   max-idle         {pilot:"item"|"hero", ms}      the pilot, while it has work (target / plan, game in front, player idle, not done or failed), is never
//                                                   without a click or a drag for longer than ms
//   hero-cantdo-reset {ms}                         (build 522, nothing moved for 20 s) the hero pilot replayed NavOwned on the recorded hero views, with the plan hero hidden by a faction filter: it reports "cannot finish"
//                                                   (notfound / failed / small - HeroSelectAction turns it into a navigator reset) within ms of the first view that shows the filter hiding the plan hero
//   hero-pick-after-reset {ms}                     then the pilot is reset (as the navigator does) and fed the later views: once the unfiltered grid shows the plan hero it clicks / drags toward it within ms
//                                                   (a click names the column of the hero's place), never sits in a terminal state
//   no-phantom-gear-layer {}                       on every recorded hero view whose gear list is not open (tab != 3 or no slot) the navigator's snapshot has no gear filter layer even if the red-cross picture says so
//   nav-own-wait-escalates {ms}                    (build 521: the click waited for ever on a look our own hint covered) the navigator on the recorded hero screen, every click answered "covered by our hint", hides our windows
//                                                   within 4 s and resets the step by ms at the latest
//   frame-on-card    {}                             (artifact list: the frame on the named card) needs the cards' real place: always "no data" for now
//   sell-done-sticky {}                             once the storage cleanup is done, the pilot stays done while the selection stays complete
//                                                   (mouse moves, clicks and the game behind must not take the final step away: the frame on «Продать»)
// A check that needs a pilot's slot the recording does not have fails with "no data: ...": the recording came from a build without it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RealmForge;

static class ReplayCheck {
  /// <summary>NAV_REPLAY=1: the recorded pilot views are run with NavOwned on (the navigator owns the way to the slot and the hero screen).</summary>
  static readonly bool NavOwnedRun = Environment.GetEnvironmentVariable("NAV_REPLAY") == "1";
  public sealed class Tick {
    public long T, Tn; public string Screen = "-", ScreenRec = "-", Step = "-", Mode = ""; public bool Front;
    public bool Busy;                                   // the player moved the mouse in the last 700 ms (as the overlay's UserBusy)
    public AutoView Item; public AutoAction ItemRec, ItemRep; public string ItemRecState, ItemRepState;
    public HeroView Hero; public AutoAction HeroRec, HeroRep; public string HeroRecState, HeroRepState;
    public string SellState, SellControl;               // the replayed sell pilot, and one fed without the player's presence (the truth)
  }

  // ---- the pilots, fed from the recorded lines in order (one instance for the whole run, as the app has)
  public sealed class Pilots {
    public readonly AutoPilot Item = new AutoPilot(new ListGeometry());
    public readonly HeroPilot Hero = new HeroPilot(new HeroGeometry());
    SellPilot sell, control;
    double curX = double.NaN, curY = double.NaN; long moveAt = long.MinValue;

    public void Run(Tick k, Dictionary<string, object> inn, RecCtx ctx, Dictionary<string, object> tickSlots, Dictionary<string, object> j, int w, int h, double U, double row1) {
      var cur = j.ContainsKey("cursor") ? j["cursor"] as List<object> : null;
      if (cur != null && cur.Count >= 2 && cur[0] is double && cur[1] is double) {
        double cx = (double)cur[0], cy = (double)cur[1];
        if (double.IsNaN(curX) || Math.Abs(cx - curX) > 0.5 || Math.Abs(cy - curY) > 0.5) { if (!double.IsNaN(curX)) moveAt = k.Tn; curX = cx; curY = cy; }
      }
      k.Busy = k.Tn - moveAt < 700;
      object ap, hp;
      if (inn.TryGetValue("ap", out ap)) {
        var d = RecIn.O(ap); var v = new AutoView(); RecView.Fill(v, d != null && d.ContainsKey("v") ? d["v"] : null);
        if (NavOwnedRun) v.NavOwned = true;
        k.Item = v; k.ItemRec = RecView.Action(ap); k.ItemRecState = d != null ? RecIn.Str(d, "s") : null;
        k.ItemRep = Item.Step(v); k.ItemRepState = Item.State;
        if (k.ItemRep.Kind != AutoKind.None) Item.LivePoint(k.ItemRep.X, k.ItemRep.Y);   // (the app tells the pilot where a click really went: the live cell's point, here the one it asked)
      }
      if (inn.TryGetValue("hp", out hp)) {
        var d = RecIn.O(hp); var v = new HeroView(); RecView.Fill(v, d != null && d.ContainsKey("v") ? d["v"] : null);
        if (NavOwnedRun) v.NavOwned = true;
        k.Hero = v; k.HeroRec = RecView.Action(hp); k.HeroRecState = d != null ? RecIn.Str(d, "s") : null;
        k.HeroRep = Hero.Step(v) ?? AutoAction.Nothing; k.HeroRepState = Hero.State;
      }
      if (ctx != null && ctx.Mode == "sell") {
        var inv = RecIn.ParseInv(tickSlots.ContainsKey("sinv") ? tickSlots["sinv"] : null, tickSlots.ContainsKey("sinvList") ? tickSlots["sinvList"] : null);
        if (inv != null && ctx.Want.Length > 0) {
          int cols = inv.List != null && inv.List.Cols > 0 ? inv.List.Cols : ctx.ShCols;
          var want = new HashSet<long>(ctx.Want);
          var scr = ctx.Art ? inv.AsSellArt(cols) : inv.AsSell(cols);
          // the truth: a pilot just made, with the player away, says whether the selection is complete (everything wanted ticked, nothing else)
          var g2 = SellGeometry.Inventory(w, U, cols); control = new SellPilot(g2);
          control.Step(new SellView { NowMs = k.Tn, Foreground = true, UserBusy = false, UserClicked = false, W = w, H = h, U = U, Screen = scr, Wanted = want, Row1 = row1 });
          // the pilot of the run: its own clicks have no effect in a replay, so it is made again whenever the selection is not complete (a new run); while
          // complete it sees the player as recorded (mouse moving, the game behind)
          if (control.State != "done") sell = new SellPilot(g2);
          else {
            if (sell == null) sell = new SellPilot(g2);
            sell.Step(new SellView { NowMs = k.Tn, Foreground = ctx.Front, UserBusy = k.Busy, UserClicked = false, W = w, H = h, U = U, Screen = scr, Wanted = want, Row1 = row1 });
          }
          if (sell == null) sell = new SellPilot(g2);
          k.SellState = sell.State; k.SellControl = control.State;
        }
      }
    }
  }

  // ---- the judgement
  sealed class Result { public string Id, Text; public bool Ok; public string Note; }

  static Dictionary<string, object> O(object o) { return o as Dictionary<string, object>; }
  static double Num(Dictionary<string, object> d, string k, double def) { object v; return d != null && d.TryGetValue(k, out v) && v is double ? (double)v : def; }
  static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as string : null; }

  static bool Works(AutoAction a) { return a != null && (a.Kind == AutoKind.Click || a.Kind == AutoKind.Drag); }

  /// <summary>Judges expect.json on the ticks; returns the process exit code (0 = as expected).</summary>
  public static int Judge(string expectPath, List<Tick> ticks, int mismatches, TextWriter o) {
    var ex = O(MiniJson.TryParse(File.ReadAllText(expectPath, new UTF8Encoding(false))));
    if (ex == null) { o.WriteLine("expect.json does not parse: " + expectPath); return 1; }
    string name = Str(ex, "name") ?? Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(expectPath)));
    bool pending = Str(ex, "status") == "pending";
    var results = new List<Result>();
    var checks = ex.ContainsKey("checks") ? ex["checks"] as List<object> : null;
    if (checks == null || checks.Count == 0) { o.WriteLine(name + ": no checks"); return 1; }
    foreach (var co in checks) {
      var c = O(co); if (c == null) continue;
      var r = new Result { Id = Str(c, "id") ?? "?", Text = Str(c, "text") ?? "" };
      try { Eval(c, ticks, mismatches, r); } catch (Exception e) { r.Ok = false; r.Note = "check error: " + e.Message; }
      results.Add(r);
    }
    int bad = 0;
    foreach (var r in results) { if (!r.Ok) bad++; o.WriteLine("  " + (r.Ok ? "ok   " : r.Note != null && r.Note.StartsWith("no data") ? "NODATA" : "FAIL ") + " " + r.Id + (r.Text != "" ? " - " + r.Text : "") + (r.Note != null && !r.Ok ? "  [" + r.Note + "]" : "")); }
    if (pending) {
      string why = Str(ex, "pending") ?? "";
      if (bad == 0) { o.WriteLine(name + ": PENDING fixture now passes every check - set \"status\":\"pass\" in expect.json (" + why + ")"); return 1; }
      o.WriteLine(name + ": pending as expected (" + bad + " of " + results.Count + " checks fail: " + why + ")");
      return 0;
    }
    if (bad > 0) { o.WriteLine(name + ": REGRESSION - " + bad + " of " + results.Count + " checks fail"); return 1; }
    o.WriteLine(name + ": ok (" + results.Count + " checks)");
    return 0;
  }

  static ScreenState HeroState(HeroView v, long now) {
    var inp = new ScreenInputs { Offers = new List<string>(), ShownKnown = "Form_CharactorMain", Flow = "", Order = new List<string> { "Form_CharactorMain", "Form_MainCity" },
      Hero = new HeroScreen { Hero = v.Hero, FormOk = true, Tab = v.Tab, Part = v.Part } };
    var s = ScreenState.Classify(inp, now); s.SinceMs = 0; return s;
  }

  static bool CantDo(HeroPilot p) { return p.State == "notfound" || p.State == "failed" || p.State == "small"; }

  /// <summary>The hero pilot on the recorded hero views, NavOwned, reset as the navigator does when it cannot finish.</summary>
  static void HeroReplay(Dictionary<string, object> c, List<Tick> ticks, Result r, bool afterReset) {
    long ms = (long)Num(c, "ms", 5000);
    var p = new HeroPilot(new HeroGeometry());
    long hiddenAt = -1, resetAt = -1, pickAt = -1, readyAt = -1; int n = 0; string note = "";
    foreach (var k in ticks) {
      if (k.Hero == null) continue; n++;
      var v = k.Hero; v.NavOwned = true;
      int idx = v.Heroes != null ? Array.IndexOf(v.Heroes, v.Plan) : -1;
      bool hidden = idx < 0 && HeroPilot.FilterActive(v);
      if (hiddenAt < 0 && hidden && v.Hero != 0) hiddenAt = k.Tn;
      if (resetAt >= 0 && readyAt < 0 && idx >= 0 && !HeroPilot.FilterActive(v) && v.Part < 0 && v.Hero != 0) readyAt = k.Tn;
      if (resetAt >= 0 && readyAt < 0) continue;   // (the trip to the city: the navigator does not step the pilot on it)
      var a = p.Step(v);
      if (resetAt < 0) {
        if (hiddenAt >= 0 && CantDo(p)) { resetAt = k.Tn; p.Reset(); }
        continue;
      }
      if (readyAt >= 0 && pickAt < 0 && a != null && (a.Kind == AutoKind.Click || a.Kind == AutoKind.Drag)) {
        pickAt = k.Tn;
        if (a.Kind == AutoKind.Click) {
          double H = v.H, want = new HeroGeometry().ColX(idx % HeroGeometry.Columns) * H;
          if (Math.Abs(a.X - want) > 2) note = "the click is at x " + (int)a.X + ", the hero's column is at " + (int)want;
        }
      }
      if (readyAt >= 0 && pickAt < 0 && CantDo(p)) note = "the pilot is in " + p.State + " (" + p.Why + ") on the unfiltered grid at " + k.Tn + ", hero " + v.Hero + " tab " + v.Tab + " part " + v.Part + " idx " + idx;
    }
    if (n == 0) { r.Ok = false; r.Note = "no data: the recording has no hero pilot views (hp)"; return; }
    if (!afterReset) {
      r.Ok = hiddenAt >= 0 && resetAt >= 0 && resetAt - hiddenAt <= ms;
      if (!r.Ok) r.Note = hiddenAt < 0 ? "the plan hero was never hidden by a filter in the views" : resetAt < 0 ? "the pilot never reported that it cannot finish (" + (ticks[ticks.Count - 1].Tn - hiddenAt) + " ms)" : "it took " + (resetAt - hiddenAt) + " ms, limit " + ms;
    } else {
      r.Ok = resetAt >= 0 && readyAt >= 0 && pickAt >= 0 && pickAt - readyAt <= ms && note == "";
      if (!r.Ok) r.Note = resetAt < 0 ? "no reset to follow" : readyAt < 0 ? "the unfiltered grid with the plan hero never came" : note != "" ? note : pickAt < 0 ? "no pick after the grid showed the hero (" + (ticks[ticks.Count - 1].Tn - readyAt) + " ms)" : "the pick came " + (pickAt - readyAt) + " ms after, limit " + ms;
    }
  }

  /// <summary>The navigator's io in the 0d0bf6 check: every click waits on "a look covered by our own hint" until the windows are hidden (which is never enough for the check: they come back).</summary>
  sealed class OwnWaitIo : INavIo {
    public long NowMs = 100000; public readonly ClickGate Gt = new ClickGate(); public ScreenState FreshState; public int Hid;
    public long Now { get { return NowMs; } }
    public bool Front { get { return true; } }
    public bool UserBusy { get { return false; } }
    public double W { get { return 503; } }
    public double H { get { return 283; } }
    public double U { get { return 283; } }
    public bool Slow { get { return false; } }
    public bool InputOpen { get { return true; } }
    public GameGeometry Geo { get { return null; } }
    public ClickGate Gate { get { return Gt; } }
    public ScreenState Fresh(bool c, bool k) { return FreshState; }
    public double[] LiveRect(string f, string fl) { return null; }
    public double[] LiveFor(ClickTarget t, string f) { return null; }
    public bool Picture(string w) { return false; }
    public GateResult Send(Act a, ScreenState fresh) { return GateResult.Wait("own", "a look is covered by our own hint"); }
    public void HideOwn() { Hid++; }
    public string EscapeKind { get { return ""; } }
    public bool Hotkey(string name) { return false; }
    public void Log(string line) { }
    public void Problem(string kind, string message) { }
  }
  sealed class OwnWaitAction : IAtTargetAction {
    public string Name { get { return "stub"; } }
    public ICollection<string> Keep { get { return null; } }
    public bool Wants(Snapshot s, NavTarget t) { return true; }
    public long ProgressAt { get { return 0; } }
    public void Reset() { }
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) {
      return NavStep.Of(new Act { Owner = t.Owner, Target = ClickTarget.EquipNav, Kind = ActKind.Click, X = 10, Y = 10, Base = "Form_CharactorMain", Fire = true, Why = "stub" }, "act:stub");
    }
  }

  static void Eval(Dictionary<string, object> c, List<Tick> ticks, int mismatches, Result r) {
    string type = Str(c, "type");
    r.Ok = true;
    if (type == "replay-agrees") {
      int max = (int)Num(c, "max", 0);
      r.Ok = mismatches <= max; if (!r.Ok) r.Note = mismatches + " recorded-vs-replayed differences, at most " + max + " allowed";
    } else if (type == "click-within") {
      long ms = (long)Num(c, "ms", 3000); bool live = (Str(c, "from") ?? "liveTarget") == "liveTarget";
      long start = -1, end = -1; int n = 0;
      foreach (var k in ticks) {
        if (k.Item == null) continue; n++;
        var v = k.Item;
        if (start < 0 && v.Target != 0 && v.Sel != v.Target && (!live || (v.LiveGrid && v.LiveTarget)) && v.Foreground) start = k.Tn;
        if (start >= 0 && end < 0 && k.ItemRep.Kind == AutoKind.Click && k.ItemRep.Cell == v.Target) end = k.Tn;   // (the replayed click only: the recorded selection came from the recorded run's clicks)
      }
      if (n == 0) { r.Ok = false; r.Note = "no data: the recording has no item pilot views (ap)"; return; }
      if (start < 0) { r.Ok = false; r.Note = "the item was never clickable in the recording (" + (live ? "no live cell of the target" : "no target") + ")"; return; }
      r.Ok = end >= 0 && end - start <= ms;
      if (!r.Ok) r.Note = end < 0 ? "never clicked in " + (ticks[ticks.Count - 1].Tn - start) + " ms" : "clicked after " + (end - start) + " ms, limit " + ms;
    } else if (type == "no-click-empty") {
      int n = 0, clicks = 0, bad = 0; long first = -1;
      foreach (var k in ticks) {
        if (k.Item == null) continue; n++;
        if (k.ItemRep.Kind == AutoKind.Click && k.Item.LiveGrid) { clicks++; if (k.ItemRep.Cell == 0) { bad++; if (first < 0) first = k.Tn; } }
      }
      if (n == 0) { r.Ok = false; r.Note = "no data: the recording has no item pilot views (ap)"; return; }
      r.Ok = bad == 0; if (!r.Ok) r.Note = bad + " of " + clicks + " clicks on the live grid name no cell (first at " + first + ")";
    } else if (type == "max-idle") {
      string who = Str(c, "pilot") ?? "item"; long ms = (long)Num(c, "ms", 8000);
      long idleFrom = -1, worst = 0, worstAt = -1; int n = 0;
      foreach (var k in ticks) {
        bool has = who == "item" ? k.Item != null : k.Hero != null;
        if (!has) continue; n++;
        bool work, did; string st;
        if (who == "item") { var v = k.Item; st = k.ItemRepState; work = v.Target != 0 && v.Sel != v.Target && v.Foreground && !v.UserBusy && st != "done" && st != "failed"; did = Works(k.ItemRep); }
        else { var v = k.Hero; st = k.HeroRepState; work = v.Plan != 0 && v.Hero != v.Plan && v.Foreground && !v.UserBusy && st != "done" && st != "failed"; did = Works(k.HeroRep); }
        if (!work || did) { idleFrom = -1; continue; }
        if (idleFrom < 0) idleFrom = k.Tn;
        if (k.Tn - idleFrom > worst) { worst = k.Tn - idleFrom; worstAt = idleFrom; }
      }
      if (n == 0) { r.Ok = false; r.Note = "no data: the recording has no " + who + " pilot views (" + (who == "item" ? "ap" : "hp") + ")"; return; }
      r.Ok = worst <= ms; if (!r.Ok) r.Note = "idle " + worst + " ms from " + worstAt + " (limit " + ms + ")";
    } else if (type == "sell-done-sticky") {
      int n = 0, drops = 0; bool done = false; long firstDrop = -1; string what = "";
      foreach (var k in ticks) {
        if (k.SellState == null) continue; n++;
        if (k.SellControl != "done") { done = false; continue; }   // (the selection is not complete: the final step is rightly gone)
        if (k.SellState == "done") done = true;
        else if (done) { drops++; if (firstDrop < 0) { firstDrop = k.Tn; what = k.SellState; } }
      }
      if (n == 0) { r.Ok = false; r.Note = "no data: the recording has no sell screen with a list of wanted items"; return; }
      if (Environment.GetEnvironmentVariable("REPLAY_DEBUG") != null) { var sb = new StringBuilder(); foreach (var k in ticks) if (k.SellState != null) sb.Append(k.Tn + ":" + k.SellState + "/" + k.SellControl + (k.Busy ? "b" : "") + " "); Console.Error.WriteLine(sb); }
      r.Ok = drops == 0; if (!r.Ok) r.Note = "the finished run left done " + drops + " times (first at " + firstDrop + ": " + what + ")";
    } else if (type == "hero-cantdo-reset" || type == "hero-pick-after-reset") {
      HeroReplay(c, ticks, r, type == "hero-pick-after-reset");
    } else if (type == "no-phantom-gear-layer") {
      int n = 0, bad = 0; long first = -1;
      foreach (var k in ticks) {
        if (k.Hero == null || k.Hero.Hero == 0) continue;
        var v = k.Hero; if (v.Tab == HeroGeometry.GearTab && v.Part >= 0) continue; n++;
        var snap = ScreenReader.Build(HeroState(v, k.Tn), k.Tn, w => w == "gearFilterMain" || w == "gearFilterSide");
        foreach (var l in snap.Stack) if (l.Form == "panel:gearFilter" || l.Form == "panel:gearSide") { bad++; if (first < 0) first = k.Tn; }
      }
      if (n == 0) { r.Ok = false; r.Note = "no data: the recording has no hero views off the gear list (hp)"; return; }
      r.Ok = bad == 0; if (!r.Ok) r.Note = bad + " of " + n + " views build a gear filter layer from the picture alone (first at " + first + ")";
    } else if (type == "nav-own-wait-escalates") {
      long ms = (long)Num(c, "ms", 9000);
      Tick at = null; foreach (var k in ticks) if (k.ScreenRec.StartsWith("HeroScreen hero ") && k.ScreenRec.Contains(" tab 3 slot ") && !k.ScreenRec.Contains("flow(")) at = k;
      if (at == null) { r.Ok = false; r.Note = "no data: the recording has no hero screen on the gear tab with a slot"; return; }
      var parts = at.ScreenRec.Split(' '); long hero = long.Parse(parts[2], CultureInfo.InvariantCulture); int slot = int.Parse(parts[parts.Length - 1], CultureInfo.InvariantCulture);
      var io = new OwnWaitIo(); var rdr = new ScreenReader(); var nv = new Navigator(ClickOwner.Equip);
      var st = HeroState(new HeroView { Hero = hero, Tab = HeroGeometry.GearTab, Part = slot }, 0);
      io.FreshState = st;
      nv.SetTarget(new NavTarget { Goal = NavGoal.HeroGear, Hero = hero }, new List<IAtTargetAction> { new OwnWaitAction() });
      long t0 = io.NowMs, hidAt = -1, resetAt = -1;
      for (int i = 0; i < 400 && resetAt < 0; i++) {
        io.NowMs += 100; nv.Tick(io, rdr.Update(st, io.NowMs));
        if (io.Hid > 0 && hidAt < 0) hidAt = io.NowMs - t0;
        if (nv.Recoveries > 0) resetAt = io.NowMs - t0;
      }
      r.Ok = hidAt >= 0 && hidAt <= 4000 && resetAt >= 0 && resetAt <= ms;
      if (!r.Ok) r.Note = "windows hidden at " + hidAt + " ms (limit 4000), step reset at " + resetAt + " ms (limit " + ms + ")";
    } else if (type == "frame-on-card") {
      r.Ok = false; r.Note = "no data: where the artifact list's cards really are is only in the picture (fixtures keep none) and the recorder keeps no read of the cards' rects";
    } else { r.Ok = false; r.Note = "unknown check type '" + type + "'"; }
  }
}
