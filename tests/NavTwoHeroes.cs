// Two heroes of one class and faction in a row (a player's report: the auto-equip stops after the first hero, the second is picked by hand).
using System;
using System.Collections.Generic;
using RealmForge;

static partial class CoreTests {
  /// <summary>One pilot, one game: the first hero far down in a 720-hero grid, the second of the same class and faction right after him.
  /// navOwned: the HeroPilot as the navigator runs it (it does not click «Герои», the gear tab or the list's back arrow: the test plays those).</summary>
  static string TwoHeroesRun(int seed, bool navOwned, bool startFar, out string why) {
    var g = new HeroGeometry(); double W = 1920, H = 1009;
    var big = new long[720]; for (int i = 0; i < big.Length; i++) big[i] = (200100 + i) * 100000L;
    var tags = new Dictionary<long, long[]>(); var rb = new Random(seed);
    // (heroes with one or two factions, as the game's: Arach [class 1, 106], Numera [class 1, 106, 107])
    foreach (var u in big) { long cl = HeroGeometry.ClassOrder[rb.Next(6)], f1 = FakeHeroGame.FactionList[1 + rb.Next(10)]; tags[u] = rb.Next(2) == 0 ? new long[] { cl, f1 } : new long[] { cl, f1, FakeHeroGame.FactionList[1 + rb.Next(10)] }; }
    long h1 = big[400 + rb.Next(200)], h2 = 0;
    foreach (var u in big) if (u != h1 && Array.IndexOf(big, u) > Array.IndexOf(big, h1) + 1 && tags[u][0] == tags[h1][0] && tags[u][1] == tags[h1][1]) { h2 = u; break; }
    if (h2 == 0) foreach (var u in big) if (u != h1 && tags[u][0] == tags[h1][0] && tags[u][1] == tags[h1][1]) { h2 = u; break; }
    var game = new FakeHeroGame(g, W, H) { All = big, Tags = tags, Gain = 1, SelectFirst = true }; game.Refilter();
    game.Shown = startFar ? big[233] : big[0]; if (startFar) game.Scroll = Math.Min(game.MaxScroll, (233 / 3) * g.PitchY * H - g.ViewTop * H * 2);
    var p = new HeroPilot(g); HeroView lv; why = "";
    foreach (var plan in new[] { h1, h2 }) {
      string st = RunHeroNav(p, game, plan, 3000, out lv, tags[plan], navOwned);
      if (st != "done" || game.Shown != plan) { why = "plan " + (plan == h1 ? "1" : "2") + " " + st + " shown " + game.Shown + " class " + game.Class + " camps " + game.Camps.Count + " filterOpen " + game.FilterOpen + " (" + p.Why + ")"; return st; }
      game.Part = 2;   // the item pilot opened a slot's list; the next plan starts from there
    }
    return "done";
  }

  static string RunHeroNav(HeroPilot p, FakeHeroGame game, long plan, int maxSteps, out HeroView last, long[] tags, bool navOwned) {
    last = null;
    for (long t = 0; t < maxSteps * 100L; t += 100) {
      if (navOwned && game.Part >= 0 && game.Shown != plan) { game.Click(g0.BackX * 1009, g0.BackY * 1009); continue; }   // facet:listClosed (the navigator's)
      var v = new HeroView { NowMs = t + planClock, Foreground = true, W = 1920, H = 1009, Plan = plan, Hero = game.Shown, Tab = game.Tab,
        Part = game.Part, Heroes = game.Heroes, HeroesButton = game.City, ClientH = 1009, SmallCards = game.Small, NavOwned = navOwned,
        FilterOpen = game.FilterOpen && !game.Blind, ChosenCamps = game.Camps.ToArray(), ClassChosen = game.Class > 0, GridPtr = game.Grid,
        FactionList = game.Tags != null ? FakeHeroGame.FactionList : null, ClassCount = 7 };
      if (tags != null) { v.HeroClass = tags[0]; v.HeroCamps = new long[tags.Length - 1]; Array.Copy(tags, 1, v.HeroCamps, 0, tags.Length - 1); }
      last = v;
      var a = p.Step(v);
      if (a == null) { planClock += t + 100; return p.State; }
      if (a.Kind == AutoKind.Click) game.Click(a.X, a.Y);
      else if (a.Kind == AutoKind.Drag) game.Drag(a.DY);
      if (p.State == "failed" || p.State == "notfound" || p.State == "noscreen" || p.State == "small") { planClock += t + 100; return p.State; }
    }
    planClock += maxSteps * 100L;
    return "timeout:" + p.State;
  }
  static long planClock; static readonly HeroGeometry g0 = new HeroGeometry();

  static void TwoHeroesTests() {
    Console.WriteLine("Two heroes of one class and faction in a row");
    foreach (bool nav in new[] { false, true })
      foreach (bool far in new[] { false, true }) {
        int ok = 0, bad = 0; string first = "";
        for (int s = 1; s <= 30; s++) { planClock = 0; string why; string r = TwoHeroesRun(s, nav, far, out why); if (r == "done") ok++; else { bad++; if (first == "") first = "seed " + s + ": " + why; } }
        Check(bad == 0, "two heroes of one class and faction, the first far in the grid" + (far ? ", the view left far from him" : "") + (nav ? ", NavOwned" : ", old path") + ": " + ok + "/30 " + first);
      }
  }
}
