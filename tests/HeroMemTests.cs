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
    {   // the panel is built by the first press (before it: never built = closed, E1), exact after: the real order of the game
      var gm = mk(); gm.Blind = true; gm.MemMode = 2; var hp = new HeroPilot(g);
      string s = go(gm, hp);
      Check(s == "done" && gm.Shown == far && gm.Funnels == 2, "flag No (never built) until the first press, then exact; pixels blind: done with two presses (" + s + ", " + gm.Funnels + ")");
    }
    {   // the prefab loads 800 ms after the press (slot false = Unknown, pending): no second press meanwhile, the pilot goes on once it answers
      var gm = mk(); gm.MemMode = 3; var hp = new HeroPilot(g);
      string s = go(gm, hp);
      Check(s == "done" && gm.Shown == far && gm.Funnels == 2 && !gm.FilterOpen, "panel prefab loading: one press to open, done (" + s + ", " + gm.Funnels + ")");
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
    GearMemTests();
    MemGapTests();
  }

  // ---------------------------------------------------------------- the gear filter from the game's memory (docs/specs/2026-11-04-memory-first-state.md P2-P4, build 538 live log)
  static ScreenState GearState(int tab, int part, Mem3 open, Mem3 side, Mem3 heroFilter) {
    var hs = new HeroScreen { Hero = 7, FormOk = true, Tab = tab, Part = part, FilterMem = heroFilter, Gear = new GearMem { Open = open, Side = side, SetPanel = side == Mem3.Unknown ? Mem3.Unknown : Mem3.No } };
    var st = ScreenState.Classify(new ScreenInputs { Offers = new List<string>(), ShownKnown = "Form_CharactorMain", Hero = hs, Order = new List<string> { "Form_CharactorMain", "Form_MainCity" } }, 0);
    st.SinceMs = 0; return st;
  }

  static void GearMemTests() {
    var g = new FilterGeometry(); double W = 1920, H = 1009;
    Func<List<string>, string, int> count = (l, part) => { int n = 0; foreach (var x in l) if (x.IndexOf(part, StringComparison.Ordinal) >= 0) n++; return n; };
    {   // the owner's build 538: the game's flag turns on 300 ms after the bar press and the picture shows it 1 s later: ONE press with the flag, the old pixel-only path presses the toggle again and shuts it
      Func<bool, int> run = useMem => {
        var game = new FakeFilterGame(g, W, H) { SetList = new long[] { 721500, 721600 }, StatList = new long[] { 1, 13 } };
        var p = new FilterPilot(g); int bar = 0; var pend = new List<KeyValuePair<long, double[]>>(); var hist = new List<KeyValuePair<long, bool>>();
        double by = Ui.FromBottom(0.9275, W, H);
        for (long t = 0; t < 6000; t += 100) {
          hist.Add(new KeyValuePair<long, bool>(t, game.Open));
          foreach (var q in pend.ToArray()) if (q.Key <= t) { game.Click(q.Value[0], q.Value[1]); pend.Remove(q); }
          bool pix = false; foreach (var h in hist) if (h.Key <= t - 1000) pix = h.Value;
          var v = new FilterView { NowMs = t, Foreground = true, W = W, H = H, Item = 5, Wanted = true, SetId = 721500, StatId = 13, SetIndex = 0, StatIndex = 1, Suits = game.Suits.ToArray(), MainAttrs = game.Mains.ToArray(), HideEquipped = game.HideEq, SetOrder = game.SetList,
            SubOrder = new long[0], SubAttrs = new long[0], PanelOpen = pix, PanelMem = useMem ? (game.Open ? Mem3.Yes : Mem3.No) : Mem3.Unknown, SideMem = useMem ? (game.Open && game.Side ? Mem3.Yes : Mem3.No) : Mem3.Unknown, SetMem = useMem ? (game.Open && game.Side && game.SetSide ? Mem3.Yes : Mem3.No) : Mem3.Unknown };
          var a = p.Step(v);
          if (a != null && a.Kind == AutoKind.Click) { if (Math.Abs(a.Y - by) < 3) bar++; pend.Add(new KeyValuePair<long, double[]>(t + 300, new[] { a.X, a.Y })); }
        }
        return bar;
      };
      NavNotes.Take();
      int withMem = run(true), pixOnly = run(false);
      Check(withMem == 1, "gear filter: the flag leads the picture by 1 s: ONE press of the filter bar (" + withMem + ")");
      Check(pixOnly >= 2, "gear filter (old pixel-only path, the proof the case bites): the bar is pressed again before the picture shows it (" + pixOnly + " presses)");
      NavNotes.Take();
    }
    {   // the pilot puts the flag over the pictures, one note per disagreement episode; Unknown leaves the pictures; the sets panel is m_ButtonStates[1]
      NavNotes.Take(); var p = new FilterPilot(g);
      for (int i = 0; i < 4; i++) { var v = new FilterView { NowMs = i * 100, Foreground = true, Item = 0, PanelOpen = false, PanelMem = Mem3.Yes }; p.Step(v); if (i == 0) Check(v.PanelOpen, "gear filter: flag yes over a picture of no -> open"); }
      var notes = NavNotes.Take();
      Check(count(notes, "filter open: mem=yes pix=no") == 1, "gear filter: ONE 'filter open: mem=yes pix=no' line for the episode (" + string.Join(" | ", notes.ToArray()) + ")");
      var vu = new FilterView { NowMs = 1000, Foreground = true, Item = 0, PanelOpen = true, SidePanelOpen = true, SetPanelOpen = true }; p.Step(vu);
      Check(vu.PanelOpen && vu.SidePanelOpen && vu.SetPanelOpen, "gear filter: flags Unknown leave the pictures alone");
      var vs = new FilterView { NowMs = 1100, Foreground = true, Item = 0, PanelOpen = false, PanelMem = Mem3.Yes, SideMem = Mem3.Yes, SetMem = Mem3.No, SidePanelOpen = false, SetPanelOpen = true }; p.Step(vs);
      Check(vs.PanelOpen && vs.SidePanelOpen && !vs.SetPanelOpen, "gear filter: the side panel is open but not the sets (m_ButtonStates[1] = 0): stats, not sets");
      var vt = new FilterView { NowMs = 1200, Foreground = true, Item = 0, PanelMem = Mem3.Yes, SideMem = Mem3.Yes, SetMem = Mem3.Yes }; p.Step(vt);
      Check(vt.SetPanelOpen, "gear filter: m_ButtonStates[1] = 1: the sets panel");
      NavNotes.Take();
    }
    {   // the snapshot is memory first: the flag answers (the picture is not even asked), the picture decides only when the flag is Unknown; hero tab 1 can have no gear layer
      int asked = 0; Func<string, bool> yes = w => { asked++; return true; }, no = w => { asked++; return false; };
      var sn = ScreenReader.Build(GearState(3, 2, Mem3.No, Mem3.No, Mem3.Unknown), 0, yes);
      Check(!sn.Has("panel:gearFilter") && !sn.Has("panel:gearSide") && sn.MemClosed.Contains("panel:gearFilter"), "snapshot: flag no + a red picture: no gear layer, closed in memory (" + sn.LayerKey() + ")");
      sn = ScreenReader.Build(GearState(3, 2, Mem3.Yes, Mem3.No, Mem3.Unknown), 0, no);
      Check(sn.Has("panel:gearFilter") && !sn.Has("panel:gearSide"), "snapshot: flag yes + no picture: the gear layer is there (" + sn.LayerKey() + ")");
      asked = 0; ScreenReader.Build(GearState(3, 2, Mem3.Yes, Mem3.No, Mem3.No), 0, yes);
      Check(asked == 0, "snapshot: every flag answers: no picture is asked (" + asked + ")");
      sn = ScreenReader.Build(GearState(3, 2, Mem3.Unknown, Mem3.Unknown, Mem3.Unknown), 0, w => w == "gearFilterMain");
      Check(sn.Has("panel:gearFilter"), "snapshot: flag Unknown + picture yes: the layer (the fallback)");
      sn = ScreenReader.Build(GearState(1, -1, Mem3.Yes, Mem3.Yes, Mem3.Unknown), 0, yes);
      Check(!sn.Has("panel:gearFilter") && !sn.Has("panel:gearSide"), "snapshot: hero tab 1 (no gear list): no gear layer, whatever the flag or the picture says (" + sn.LayerKey() + ")");
      sn = ScreenReader.Build(GearState(1, -1, Mem3.Unknown, Mem3.Unknown, Mem3.No), 0, yes);
      Check(!sn.Has("panel:heroFilter") && sn.MemClosed.Contains("panel:heroFilter"), "snapshot: hero grid flag no + a picture of yes: no hero filter layer");
      sn = ScreenReader.Build(GearState(1, -1, Mem3.Unknown, Mem3.Unknown, Mem3.Yes), 0, no);
      Check(sn.Has("panel:heroFilter"), "snapshot: hero grid flag yes + a picture of no: the layer");
    }
    {   // the click gate: a state the flag answers is KNOWN whatever its covered picture says; the picture counts only with the flag Unknown
      Check(!LookPolicy.Blocks(Mem3.No, Seen3.Unknown) && !LookPolicy.Blocks(Mem3.Yes, Seen3.Unknown) && LookPolicy.Blocks(Mem3.Unknown, Seen3.Unknown) && !LookPolicy.Blocks(Mem3.Unknown, Seen3.Yes) && !LookPolicy.Blocks(Mem3.Unknown, Seen3.No), "look policy: only an Unknown look of an Unknown flag blocks a click");
      var ul = new UnknownLooks();
      for (long t = 0; t < 20000; t += 200) { ul.Note("hero filter", LookPolicy.Blocks(Mem3.No, Seen3.Unknown), t); ul.Note("mark@x", LookPolicy.Blocks(Mem3.Yes, Seen3.Unknown), t); }
      Check(ul.Blocking(19900) == null && ul.Overdue(19900) == null, "look policy: a picture covered for 20 s under an exact flag never blocks nor reports");
      var ub = new UnknownLooks();
      for (long t = 0; t < 3000; t += 200) ub.Note("hero filter", LookPolicy.Blocks(Mem3.Unknown, Seen3.Unknown), t);
      Check(ub.Blocking(2900) == "hero filter", "look policy: with the flag Unknown the covered picture still blocks (the old protection)");
    }
    {   // a pop-up close whose panel the flag already says is closed is Done, not NoEffect (the debounced snapshot still carries the old layer)
      var io = new FakeNavIo { GearPic = true }; var rd = new ScreenReader();
      Func<Mem3, ScreenState> st = m => GearState(3, 2, m, Mem3.Unknown, Mem3.Unknown);
      long t0 = io.NowMs; Snapshot sn = null;
      for (int k = 0; k < 8; k++) sn = rd.Update(st(Mem3.Unknown), t0 + k * 100, w => w == "gearFilterMain");
      Check(sn.Has("panel:gearFilter"), "close: the picture-only layer is in the stack");
      io.FreshState = st(Mem3.Unknown);
      var act = new Act { Target = ClickTarget.EquipClosePopup, Kind = ActKind.Click, X = 10, Y = 10, Closing = "panel:gearFilter", Edge = "modal:panel:gearFilter", Expect = n => !n.Has("panel:gearFilter") };
      var ac = new ActClick(); ac.Start(act, t0 + 800); ac.Tick(io, sn, t0 + 800);
      for (long t = t0 + 900; t < t0 + 4200; t += 100) { sn = rd.Update(st(Mem3.Unknown), t, w => w == "gearFilterMain"); ac.Tick(io, sn, t); }
      var o1 = ac.Tick(io, sn, t0 + 4400);
      Check(o1 == ActOutcome.NoEffect, "close: with the flag Unknown and the picture stuck on: NoEffect after the budget (" + o1 + ")");
      var ac2 = new ActClick(); ac2.Start(act, t0 + 800); ac2.Tick(io, sn, t0 + 800);   // (the same close, the flag says closed in the last read before the budget ends)
      rd = new ScreenReader(); for (int k = 0; k < 8; k++) sn = rd.Update(st(Mem3.Unknown), t0 + k * 100, w => w == "gearFilterMain");
      var sm = rd.Update(st(Mem3.No), t0 + 4400, w => w == "gearFilterMain");
      Check(sm.MemClosed.Contains("panel:gearFilter") && sm.Has("panel:gearFilter"), "close: the debounced snapshot still has the layer but knows the flag says closed (" + sm.LayerKey() + ")");
      var o2 = ac2.Tick(io, sm, t0 + 4400);
      Check(o2 == ActOutcome.Done && ac2.Detail.IndexOf("memory", StringComparison.Ordinal) >= 0, "close: NoEffect with the flag already closed counts Done, no reset (" + o2 + " " + ac2.Detail + ")");
    }
    {   // the effect budget runs on while the game is busy (live 538: graph:hero NoEffect 3531 ms, the screen came half a second later)
      var io = new FakeNavIo(); const string C = "Form_MainCity", HF = "Form_CharactorMain";
      var cs = NavState(ScreenKind.City, 0, 0, 0, "", new[] { C }); io.FreshState = cs;
      Func<Act> mk = () => new Act { Target = ClickTarget.EquipNav, Kind = ActKind.Click, X = 10, Y = 10, Base = C, Edge = "graph:hero", Expect = n => n.Kind == ScreenKind.HeroScreen };
      var snap = ScreenReader.Build(cs, io.NowMs, null); snap.StableSinceMs = io.NowMs - 100000;
      var ac = new ActClick(); ac.Start(mk(), io.NowMs); ac.Tick(io, snap, io.NowMs);
      io.Open = true; var late = ac.Tick(io, snap, io.NowMs + ClickGate.EffectMs + 50);
      Check(late == ActOutcome.NoEffect, "act budget: a quiet game gives NoEffect at the default budget (" + late + ")");
      ac = new ActClick(); ac.Start(mk(), io.NowMs); ac.Tick(io, snap, io.NowMs);
      io.Open = false; var o3 = ac.Tick(io, snap, io.NowMs + ClickGate.EffectMs + 50);
      Check(o3 == ActOutcome.Running, "act budget: input locked (the game is loading) at the budget: still running (" + o3 + ")");
      io.Open = true; var o4 = ac.Tick(io, snap, io.NowMs + ClickGate.EffectMs + 50 + ActClick.BusyGraceMs + 50);
      Check(o4 == ActOutcome.NoEffect, "act budget: ... NoEffect once the game has been quiet for the grace (" + o4 + ")");
      ac = new ActClick(); ac.Start(mk(), io.NowMs); ac.Tick(io, snap, io.NowMs);
      io.Open = false; var o5 = ac.Tick(io, snap, io.NowMs + ActClick.BusyCapMs + 100);
      Check(o5 == ActOutcome.NoEffect, "act budget: a game busy for ever is cut at the cap (" + o5 + ")");
      io.Open = true;
    }
  }

  // ---------------------------------------------------------------- E1: Unknown -> No for lazily built panels (docs/specs/2026-11-04-memory-everywhere-plan.md §5)
  static void MemGapTests() {
    string why; MemGap gap;
    Func<MemSlot, MemSlot, MemSlot, bool, bool, Mem3> hero = (panels, panel, active, on, standing) => RFX.DecideHeroFilter(new RFX.HeroFilterFacts { Panels = panels, Panel = panel, Active = active, ActiveOn = on, Standing = standing }, out why, out gap);
    Check(hero(MemSlot.Table, MemSlot.Nil, MemSlot.Nil, false, true) == Mem3.No, "hero filter: form stands, m_Panels a table, panel absent = never built = No");
    RFX.DecideHeroFilter(new RFX.HeroFilterFacts { Panels = MemSlot.Table, Panel = MemSlot.Nil }, out why, out gap);
    Check(gap == MemGap.NeverBuilt && why.IndexOf("never built", StringComparison.Ordinal) >= 0, "hero filter: the reason says never built (" + why + ")");
    Check(hero(MemSlot.Table, MemSlot.Table, MemSlot.Nil, false, true) == Mem3.No, "hero filter: panel built but m_IsActive nil (PanelBase.lua:10) = No");
    Check(hero(MemSlot.Table, MemSlot.Nil, MemSlot.Nil, false, false) == Mem3.Unknown, "hero filter: input locked (a transition): panel absent stays Unknown");
    RFX.DecideHeroFilter(new RFX.HeroFilterFacts { Panels = MemSlot.Table, Panel = MemSlot.Nil, Standing = false }, out why, out gap);
    Check(gap == MemGap.Loading, "hero filter: input locked gap = Loading (bounded)");
    Check(hero(MemSlot.Table, MemSlot.Bool, MemSlot.Nil, false, true) == Mem3.Unknown, "hero filter: prefab loading (slot false) = Unknown");
    RFX.DecideHeroFilter(new RFX.HeroFilterFacts { Panels = MemSlot.Table, Panel = MemSlot.Bool }, out why, out gap);
    Check(gap == MemGap.Loading, "hero filter: prefab loading gap = Loading");
    Check(hero(MemSlot.Nil, MemSlot.Nil, MemSlot.Nil, false, true) == Mem3.Unknown, "hero filter: m_Panels nil = Unknown (the screen is being built)");
    RFX.DecideHeroFilter(new RFX.HeroFilterFacts { Panels = MemSlot.Nil }, out why, out gap);
    Check(gap == MemGap.TableNil, "hero filter: m_Panels nil gap = TableNil");
    Check(hero(MemSlot.Table, MemSlot.Table, MemSlot.Bool, true, true) == Mem3.Yes && hero(MemSlot.Table, MemSlot.Table, MemSlot.Bool, false, true) == Mem3.No, "hero filter: the flag itself is exact");

    Func<GearFacts0, Mem3> gear = f => RFX.DecideGear(f.F).Open;
    Func<MemSlot, MemSlot, MemSlot, int, bool, GearFacts0> gf = (ep, w, show, tab, standing) => new GearFacts0 { F = new RFX.GearFacts { Panels = MemSlot.Table, EquipPanel = ep, Widget = w, Show = show, Tab = tab, Standing = standing } };
    Check(gear(gf(MemSlot.Nil, MemSlot.Nil, MemSlot.Nil, 3, true)) == Mem3.No, "gear filter: tab 3, EquipPanel absent = No");
    Check(gear(gf(MemSlot.Table, MemSlot.Nil, MemSlot.Nil, 3, true)) == Mem3.No, "gear filter: filter widget absent (built by the first bar press) = No");
    Check(gear(gf(MemSlot.Table, MemSlot.Table, MemSlot.Nil, 3, true)) == Mem3.No, "gear filter: m_bShow nil = No");
    Check(gear(gf(MemSlot.Table, MemSlot.Table, MemSlot.Bool, 1, true)) == Mem3.No, "gear filter: tab 1 = No whatever the widget holds");
    var gt = RFX.DecideGear(new RFX.GearFacts { Panels = MemSlot.Table, EquipPanel = MemSlot.Table, Widget = MemSlot.Table, Show = MemSlot.Bool, ShowOn = true, Tab = 1 });
    Check(gt.Open == Mem3.No && gt.Side == Mem3.No && gt.SetPanel == Mem3.No, "gear filter: tab 1 closes side and sets too");
    Check(gear(gf(MemSlot.Nil, MemSlot.Nil, MemSlot.Nil, 3, false)) == Mem3.Unknown, "gear filter: input locked = Unknown");
    var gl = RFX.DecideGear(new RFX.GearFacts { Panels = MemSlot.Table, EquipPanel = MemSlot.Table, Widget = MemSlot.Bool, Tab = 3 });
    Check(gl.Open == Mem3.Unknown && gl.Gap == MemGap.Loading, "gear filter: widget slot false (prefab loading) = Unknown, Loading");
    var gn = RFX.DecideGear(new RFX.GearFacts { Panels = MemSlot.Nil, Tab = 3 });
    Check(gn.Open == Mem3.Unknown && gn.Gap == MemGap.TableNil, "gear filter: m_Panels nil = Unknown, TableNil");
    var gy = RFX.DecideGear(new RFX.GearFacts { Panels = MemSlot.Table, EquipPanel = MemSlot.Table, Widget = MemSlot.Table, Show = MemSlot.Bool, ShowOn = true, Tab = 3, States = new long[] { 1, 0, 0 } });
    Check(gy.Open == Mem3.Yes && gy.Side == Mem3.Yes && gy.SetPanel == Mem3.Yes, "gear filter: open with the sets panel (states 1,0,0)");

    {   // the latch: Unknown stays pending for 1.5 s from the first sight, then the fallback decides; a different answer starts it over
      var l = new MemGapLatch();
      Check(l.Pending(MemGap.Loading, 1000) && l.Pending(MemGap.Loading, 2499) && !l.Pending(MemGap.Loading, 2500) && !l.Pending(MemGap.Loading, 9000), "latch: pending 1.5 s, then not (TableNil / Loading)");
      Check(!l.Pending(MemGap.None, 9100) && l.Pending(MemGap.TableNil, 9200) && !l.Pending(MemGap.NeverBuilt, 9300), "latch: an answer resets it, NeverBuilt is never pending");
    }
    {   // the pilots hold still while pending, then act on the fallback
      var hg = new HeroGeometry(); var hp = new HeroPilot(hg);
      var tags = new Dictionary<long, long[]>(); long far = 900100;
      var v = new HeroView { NowMs = 0, Foreground = true, W = 1920, H = 1009, ClientH = 1009, Plan = far, Hero = 1, Heroes = new long[] { 1, 2, 3 }, FilterMem = Mem3.Unknown, FilterPending = true, HeroClass = 3, HeroCamps = new long[] { 101 }, FactionList = FakeHeroGame.FactionList, ClassCount = 7 };
      var a = hp.Step(v);
      Check(a == null || a.Kind != AutoKind.Click || Math.Abs(a.X - hg.FunnelX * 1009) > 20, "hero pilot: panel loading (pending): the funnel is not pressed");
      var fg = new FilterGeometry(); var fp = new FilterPilot(fg);
      var fv = new FilterView { NowMs = 0, Foreground = true, W = 1920, H = 1009, Item = 5, Wanted = true, SetId = 721500, StatId = 13, SetIndex = 0, StatIndex = 1, Suits = new long[0], MainAttrs = new long[0], SubOrder = new long[0], SubAttrs = new long[0], PanelMem = Mem3.Unknown, MemPending = true };
      var fa = fp.Step(fv);
      Check(fa == null || fa.Kind != AutoKind.Click, "gear filter pilot: panel loading (pending): the filter bar is not pressed");
      var fv2 = new FilterView { NowMs = 2000, Foreground = true, W = 1920, H = 1009, Item = 5, Wanted = true, SetId = 721500, StatId = 13, SetIndex = 0, StatIndex = 1, Suits = new long[0], MainAttrs = new long[0], SubOrder = new long[0], SubAttrs = new long[0], PanelMem = Mem3.Unknown, MemPending = false };
      var fa2 = fp.Step(fv2);
      Check(fa2 != null && fa2.Kind == AutoKind.Click, "gear filter pilot: pending over, memory still Unknown: the pictures decide, the bar is pressed");
    }
  }
  sealed class GearFacts0 { public RFX.GearFacts F; }
}
