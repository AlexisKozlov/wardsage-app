// The hero grid's filter pop-up decided by the game's memory flag (Charactor_FilterOrder.m_IsActive) with the pixels only as the fallback (docs/specs/2026-11-04-memory-first-state.md, steps 1-2).
using System;
using System.Collections.Generic;
using RealmForge;

static partial class CoreTests {
  static void HeroMemTests() {
    var g = new HeroGeometry(); double W = 1920, H = 1009;
    var heroes = new long[130]; for (int i = 0; i < heroes.Length; i++) heroes[i] = (200100 + i) * 100000L;
    var tagsAll = new Dictionary<long, long[]>(); var rt = new Random(5);
    foreach (var u in heroes) tagsAll[u] = new long[] { HeroGeometry.ClassOrder[rt.Next(6)], FakeHeroGame.FactionList[1 + rt.Next(10)] };
    Func<FakeHeroGame> mk = () => { var f = new FakeHeroGame(g, W, H) { All = heroes, Tags = tagsAll, Shown = heroes[2] }; f.Refilter(); return f; };
    long far = heroes[125]; HeroView lv;
    Func<FakeHeroGame, HeroPilot, string> go = (game, p) => RunHero(p, game, far, 3000, out lv, tagsAll[far]);
    Func<List<string>, string, int> count = (l, part) => { int n = 0; foreach (var x in l) if (x.IndexOf(part, StringComparison.Ordinal) >= 0) n++; return n; };

    {   // the flag is exact, the pixels never see the pop-up: no "pop-up not seen" stop, one press to open and one to close
      NavNotes.Take(); var gm = mk(); gm.Blind = true; gm.MemMode = 1; var hp = new HeroPilot(g);
      string s = go(gm, hp); var notes = NavNotes.Take();
      Check(s == "done" && gm.Shown == far && gm.Funnels == 2 && !gm.FilterOpen && !hp.FunnelUnseen, "memory exact, pixels blind: the pilot filters and closes the pop-up (" + s + ", " + gm.Funnels + " funnel presses, " + hp.Why + ")");
      Check(count(notes, "filter open: mem=yes pix=no") == 1, "memory yes / pixels no: ONE disagreement line for the whole episode (" + notes.Count + " notes: " + string.Join(" | ", notes.ToArray()) + ")");
    }
    {   // the panel is built by the first press (nil before: Unknown, the pixels decide), exact after: the real order of the game
      var gm = mk(); gm.Blind = true; gm.MemMode = 2; var hp = new HeroPilot(g);
      string s = go(gm, hp);
      Check(s == "done" && gm.Shown == far && gm.Funnels == 2, "flag unknown until the first press, then exact; pixels blind: done with two presses (" + s + ", " + gm.Funnels + ")");
    }
    {   // no memory at all (Unknown always) and blind pixels: the old protection, one press and a stop
      var gm = mk(); gm.Blind = true; gm.MemMode = 0; var hp = new HeroPilot(g);
      string s = go(gm, hp);
      Check(s == "notfound" && gm.Funnels == 1 && hp.FunnelUnseen, "memory Unknown + pixels No: one press, then notfound (the blind-toggle stop is kept; " + s + ", " + gm.Funnels + " presses)");
    }
    {   // the game's flag turns on 1.2 s after the press: the pilot waits, no second press
      var gm = mk(); gm.Blind = true; gm.MemMode = 1; gm.MemLagMs = 1200; var hp = new HeroPilot(g);
      string s = go(gm, hp);
      Check(s == "done" && gm.Funnels == 2, "flag answers 1.2 s late: still one press to open (" + s + ", " + gm.Funnels + ")");
    }
    {   // the pop-up fades out 700 ms after its close while the flag is already off: memory is trusted (closed), one note, the grid is not clicked through it
      NavNotes.Take(); var gm = mk(); gm.MemMode = 1; gm.FadeMs = 700; var hp = new HeroPilot(g);
      string s = go(gm, hp); var notes = NavNotes.Take();
      Check(s == "done" && gm.Shown == far && gm.Funnels == 2 && !gm.FilterOpen, "delayed fade: done with two presses (" + s + ", " + gm.Funnels + ")");
      Check(count(notes, "filter open: mem=no pix=yes") == 1, "fade: one disagreement line (mem=no pix=yes) (" + string.Join(" | ", notes.ToArray()) + ")");
    }
    {   // memory and pixels agree: no note
      NavNotes.Take(); var gm = mk(); gm.MemMode = 1; var hp = new HeroPilot(g);
      string s = go(gm, hp); var notes = NavNotes.Take();
      Check(s == "done" && notes.Count == 0, "memory and pixels agree: no disagreement line (" + notes.Count + ")");
    }
    {   // the funnel press was lost (game busy): memory says closed all along, so a second press is no blind toggle; bounded
      var gm = mk(); gm.MemMode = 1; gm.DropFunnel = 1; var hp = new HeroPilot(g);
      string s = go(gm, hp);
      Check(s == "done" && gm.Funnels == 3, "a lost funnel press with the flag closed: pressed again, then done (" + s + ", " + gm.Funnels + ")");
      var gd = mk(); gd.MemMode = 1; gd.DropFunnel = 5; var hd = new HeroPilot(g);
      s = go(gd, hd);
      Check(s == "notfound" && gd.Funnels == HeroPilot.MaxMemFunnels && hd.Why.IndexOf("closed in memory", StringComparison.Ordinal) >= 0 && !hd.FunnelUnseen, "presses that never open it: stop after " + HeroPilot.MaxMemFunnels + " (" + s + ", " + gd.Funnels + ", " + hd.Why + ")");
    }
    {   // Unknown leaves the pixels alone (an open pop-up on the pixels stays open), a memory No overrides stale pixels, a memory Yes overrides blind ones
      var p = new HeroPilot(g);
      var v1 = new HeroView { NowMs = 0, Foreground = true, FilterOpen = true, FilterMem = Mem3.Unknown }; p.Step(v1);
      var v2 = new HeroView { NowMs = 0, Foreground = true, FilterOpen = true, FilterMem = Mem3.No }; p.Step(v2);
      var v3 = new HeroView { NowMs = 0, Foreground = true, FilterOpen = false, FilterMem = Mem3.Yes }; p.Step(v3);
      Check(v1.FilterOpen && !v2.FilterOpen && v3.FilterOpen, "tri-state: Unknown keeps the pixels, No / Yes win over them (" + v1.FilterOpen + " " + v2.FilterOpen + " " + v3.FilterOpen + ")");
      NavNotes.Take();
    }
    {   // the host's mapping HeroScreen -> HeroView.FilterMem (HeroView.MemOf): the screen's verdict goes through, no screen = Unknown
      var hs = new HeroScreen { FilterMem = Mem3.Yes };
      Check(HeroView.MemOf(hs) == Mem3.Yes && HeroView.MemOf(new HeroScreen { FilterMem = Mem3.No }) == Mem3.No && HeroView.MemOf(new HeroScreen()) == Mem3.Unknown && HeroView.MemOf(null) == Mem3.Unknown, "host mapping: HeroView carries HeroScreen.FilterMem, null screen / default = Unknown");
    }
    {   // cleanup of a pop-up the pilot left open: memory says open while the pixels do not see it, it is still closed
      var all = new long[60]; var tags = new Dictionary<long, long[]>();
      for (int i = 0; i < all.Length; i++) { all[i] = (200100 + i) * 100000L; tags[all[i]] = new long[] { HeroGeometry.ClassOrder[i % 6], FakeHeroGame.FactionList[1 + i % 10] }; }
      var gc = new FakeHeroGame(g, W, H) { All = all, Tags = tags, MemMode = 1, Blind = true }; gc.Refilter();
      var hp = new HeroPilot(g); gc.FilterOpen = true; gc.Built = true; hp.FilterDirty = true;
      for (long t = 0; t < 20000 && hp.FilterDirty; t += 100) {
        gc.Now = t;
        var v = new HeroView { NowMs = t, Foreground = true, W = W, H = H, ClientH = H, FilterOpen = false, FilterMem = gc.MemAt(t), ChosenCamps = gc.Camps.ToArray(), ClassChosen = gc.Class > 0 };
        var a = hp.Cleanup(v); if (a != null && a.Kind == AutoKind.Click) gc.Click(a.X, a.Y);
      }
      Check(!gc.FilterOpen && !hp.FilterDirty, "cleanup: the flag says open (pixels blind): the pop-up is closed (open " + gc.FilterOpen + ")");
      NavNotes.Take();
    }
  }
}
