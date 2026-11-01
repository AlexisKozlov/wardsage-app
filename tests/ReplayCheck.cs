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
    public long T, Tn; public string Screen = "-", Step = "-", Mode = ""; public bool Front;
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
    } else if (type == "frame-on-card") {
      r.Ok = false; r.Note = "no data: where the artifact list's cards really are is only in the picture (fixtures keep none) and the recorder keeps no read of the cards' rects";
    } else { r.Ok = false; r.Note = "unknown check type '" + type + "'"; }
  }
}
