// Tests of the navigator core (src/Nav*.cs): the snapshot and its debounce, the verified click, the modal recipes, the routes (pinned to the UI graph),
// the planner's first step from a table of start states. Called from the sim section of CoreTests.Main.
using System;
using System.Collections.Generic;
using System.Text;
using RealmForge;

static partial class CoreTests {
  sealed class FakeNavIo : INavIo {
    public long NowMs = 100000; public readonly ClickGate Gt = new ClickGate();
    public readonly List<Act> Sent = new List<Act>(); public GateVerdict Verdict = GateVerdict.Allow;
    public bool HeroesBtn = true; public ScreenState FreshState; public readonly List<string> Lines = new List<string>();
    public long Now { get { return NowMs; } }
    public bool Front { get { return true; } }
    public bool UserBusy { get { return false; } }
    public double W { get { return 1920; } }
    public double H { get { return 1009; } }
    public double U { get { return 1009; } }
    public bool Slow { get { return false; } }
    public bool Open = true;
    public bool InputOpen { get { return Open; } }
    public GameGeometry Geo { get { return null; } }
    public ClickGate Gate { get { return Gt; } }
    public ScreenState Fresh(bool c, bool k) { return FreshState; }
    public double[] LiveRect(string f, string fl) { return null; }
    public double[] LiveFor(ClickTarget t, string f) { return null; }
    public bool GearPic;
    public bool Picture(string w) { return (w == "heroesButton" && HeroesBtn) || (w == "gearFilterMain" && GearPic); }
    public GateResult Send(Act a, ScreenState fresh) {
      if (Verdict == GateVerdict.Allow) { Sent.Add(a); return GateResult.Allow("ok"); }
      return Verdict == GateVerdict.Wait ? GateResult.Wait(WaitReason, "w") : GateResult.Deny("d", "d");
    }
    public int Hid; public string WaitReason = "w";
    public void HideOwn() { Hid++; }
    public string EscKind = ""; public int Keys;
    public string EscapeKind { get { return EscKind; } }
    public bool Hotkey(string name) { Keys++; return true; }
    public void Log(string line) { Lines.Add(line); }
    public void Problem(string kind, string message) { }
  }

  sealed class StubAction : IAtTargetAction {
    public bool Marks; public FakeNavIo Io; long mark; long lastMark;
    public string Name { get { return "stub"; } }
    public ICollection<string> Keep { get { return null; } }
    public bool Wants(Snapshot s, NavTarget t) { return true; }
    public long ProgressAt { get { return mark; } }
    public void Reset() { }
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) {
      if (Marks && now - lastMark >= 1000) { lastMark = now; mark = now; }
      return NavStep.Of(new Act { Owner = t.Owner, Target = ClickTarget.EquipNav, Kind = ActKind.Click, X = 10, Y = 10, Base = "Form_CharactorMain", Fire = true, Why = "stub" }, "act:stub");
    }
  }

  static ScreenState NavState(ScreenKind kind, long hero, int tab, int part, string flow, string[] order, string offer = null, long now = 0) {
    var inp = new ScreenInputs { Offers = new List<string>(), ShownKnown = "", Flow = flow ?? "" };
    if (offer != null) inp.Offers.Add(offer);
    if (order != null) inp.Order = new List<string>(order);
    if (kind == ScreenKind.City) { inp.CityForm = true; inp.CitySeen = true; }
    else if (kind == ScreenKind.HeroScreen) { inp.Hero = new HeroScreen { Hero = hero, FormOk = true, Tab = tab, Part = part }; inp.ShownKnown = "Form_CharactorMain"; }
    else if (kind == ScreenKind.Inventory) { inp.Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }; inp.ShownKnown = "Form_BackpackIntegration"; }
    var s = ScreenState.Classify(inp, now); s.SinceMs = 0; return s;
  }

  static void NavTests() {
    Console.WriteLine("Navigator core (src/Nav*.cs)");
    // routes are the generated, test-pinned chains: a game update that breaks one fails here
    foreach (var g in new[] { NavGoal.HeroScreen, NavGoal.HeroGear, NavGoal.HeroEquip }) {
      var f = NavRoutes.Forms(g);
      Check(f != null && string.Join(">", f) == string.Join(">", UiGraph.PilotChains["hero"]), "nav: route of " + g + " is the pinned chain " + (f == null ? "-" : string.Join(">", f)));
    }
    Check(string.Join(">", NavRoutes.Forms(NavGoal.City)) == "Form_MainCity", "nav: the city route is the city");
    const string H = "Form_CharactorMain", C = "Form_MainCity";
    // the snapshot: roles of the layers
    var tip = NavState(ScreenKind.HeroScreen, 7, 3, 2, "Form_CharactorEquipTip", new[] { "Form_CharactorEquipTip", H, C });
    var sn = ScreenReader.Build(tip, 0, null);
    Check(sn.Kind == ScreenKind.HeroScreen && sn.BaseForm == H && sn.Stack.Count == 1 && sn.Top.Role == LayerRole.Flow && sn.Confidence == NavConfidence.Sure, "nav snapshot: the item tip over the hero screen is a Flow layer (" + sn.LayerKey() + ")");
    var off = NavState(ScreenKind.City, 0, 0, 0, "", new[] { "Form_Recruitment", C }, "Form_Recruitment");
    sn = ScreenReader.Build(off, 0, null);
    Check(sn.Stack.Count == 1 && sn.Top.Role == LayerRole.Offer, "nav snapshot: an offer over the city is an Offer layer");
    var plain = NavState(ScreenKind.HeroScreen, 7, 3, -1, "", new[] { H, C });
    sn = ScreenReader.Build(plain, 0, w => w == "gearFilterMain" || w == "heroFilter");
    Check(sn.Stack.Count == 1 && sn.Has("panel:heroFilter") && !sn.Has("panel:gearFilter"), "nav snapshot: the hero grid's pop-up picture is a layer with the grid showing, the gear filter's cross picture is not (no gear list open) (" + sn.LayerKey() + ")");
    var gearOpen = NavState(ScreenKind.HeroScreen, 7, 3, 2, "", new[] { H, C });
    sn = ScreenReader.Build(gearOpen, 0, w => w == "gearFilterMain" || w == "heroFilter");
    Check(sn.Stack.Count == 1 && sn.Has("panel:gearFilter") && !sn.Has("panel:heroFilter"), "nav snapshot: the gear filter's cross picture is a layer over an open gear list only (" + sn.LayerKey() + ")");
    var tab1 = NavState(ScreenKind.HeroScreen, 7, 1, -1, "", new[] { H, C });
    Check(ScreenReader.Build(tab1, 0, w => w == "gearFilterMain" || w == "gearFilterSide").Stack.Count == 0, "nav snapshot: a red cross seen on hero tab 1 (no gear list) is no layer");
    var noOrder = NavState(ScreenKind.HeroScreen, 7, 3, -1, "", null);
    Check(ScreenReader.Build(noOrder, 0, null).Confidence == NavConfidence.NoOrder, "nav snapshot: no window-table order = NoOrder");
    // the debounce: worse at once, better after StableMs and two reads, a one-read flicker leaves the picture as it was
    var rd = new ScreenReader(); var cityS = NavState(ScreenKind.City, 0, 0, 0, "", new[] { C });
    var popS = NavState(ScreenKind.City, 0, 0, 0, "", new[] { "Form_Recruitment", C }, "Form_Recruitment");
    Snapshot a = null; long tt = 0;
    Action<ScreenState, int> feed = (st, n) => { for (int k = 0; k < n; k++) { tt += 100; a = rd.Update(st, tt); } };
    feed(cityS, 10);
    feed(popS, 1);
    Check(a.Stack.Count == 1 && !a.Stable(tt), "nav debounce: a layer that appears is taken at once, but not acted on yet");
    feed(popS, 5); Check(a.Stack.Count == 1 && a.Stable(tt), "nav debounce: ... and stable after StableMs");
    feed(cityS, 1); Check(a.Stack.Count == 1, "nav debounce: a layer that goes needs more than one read");
    feed(cityS, 8); Check(a.Stack.Count == 0 && a.Stable(tt), "nav debounce: ... two reads StableMs apart take it (" + a.Signature + ")");
    var unk = ScreenState.Classify(new ScreenInputs { Offers = new List<string>() }, 900);
    feed(unk, 1); Check(a.Kind == ScreenKind.Unknown, "nav debounce: the screen unknown is taken at once");
    feed(cityS, 1); feed(cityS, 8);
    Check(a.Kind == ScreenKind.City && a.Stack.Count == 0 && a.Stable(tt), "nav debounce: a one-read flicker leaves the picture as it was after StableMs (" + a.Signature + ")");

    // ActClick with a fake io
    {
      var io = new FakeNavIo(); var rdr = new ScreenReader(); var cs = NavState(ScreenKind.City, 0, 0, 0, "", new[] { C });
      io.FreshState = cs; var snap = rdr.Update(cs, io.NowMs); snap.StableSinceMs = io.NowMs - 1000;
      var ac = new ActClick(); int expectN = 0;
      var act = new Act { Target = ClickTarget.EquipNav, Kind = ActKind.Click, X = 10, Y = 10, Base = C, Forward = true, Edge = "t", Expect = n => { expectN++; return n.Kind == ScreenKind.HeroScreen; } };
      ac.Start(act, io.NowMs); var oc = ac.Tick(io, snap, io.NowMs);
      Check(oc == ActOutcome.Running && io.Sent.Count == 1, "nav act: sent once, the effect awaited");
      ac.Tick(io, snap, io.NowMs + 100); Check(io.Sent.Count == 1, "nav act: never sent twice for one start");
      var hs = NavState(ScreenKind.HeroScreen, 7, 3, -1, "", new[] { H, C });
      var s2 = rdr.Update(hs, io.NowMs + 200); rdr.Update(hs, io.NowMs + 700); s2 = rdr.Update(hs, io.NowMs + 1200);
      oc = ac.Tick(io, s2, io.NowMs + 1200); Check(oc == ActOutcome.Done, "nav act: done when its own test holds on a stable snapshot");
      // wait verdict = not sent, not counted
      io.Verdict = GateVerdict.Wait; io.Sent.Clear();
      ac.Start(act, io.NowMs); oc = ac.Tick(io, snap, io.NowMs); Check(oc == ActOutcome.NotSent, "nav act: a gate wait is not a try");
      // a layer that comes while the effect is awaited
      io.Verdict = GateVerdict.Allow; io.FreshState = cs; var snapC = ScreenReader.Build(cs, io.NowMs, null); snapC.StableSinceMs = io.NowMs - 1000;
      ac = new ActClick(); ac.Start(act, io.NowMs); Check(ac.Tick(io, snapC, io.NowMs) == ActOutcome.Running, "nav act: sent again for a new start");
      var pop = ScreenReader.Build(popS, io.NowMs + 300, null); pop.StableSinceMs = io.NowMs - 1000;
      var dv = ac.Tick(io, pop, io.NowMs + 300);
      Check(dv == ActOutcome.Diverted, "nav act: a new layer during the wait is a divert, not a failure (" + dv + " " + ac.Detail + ")");
      // the wrong screen is never clicked
      io.Sent.Clear(); var inv = NavState(ScreenKind.Inventory, 0, 0, 0, "", new[] { "Form_BackpackIntegration", C });
      var isn = ScreenReader.Build(inv, io.NowMs, null); isn.StableSinceMs = io.NowMs - 1000; io.FreshState = inv;
      ac.Start(act, io.NowMs); Check(ac.Tick(io, isn, io.NowMs) == ActOutcome.WrongScreen && io.Sent.Count == 0, "nav act: a click meant for the city is not sent on the inventory");
    }

    // the modal recipes
    {
      var io = new FakeNavIo(); var m = new NavMemory();
      Func<ScreenState, Func<string, bool>, Snapshot> mk = (st, pic) => { var q = ScreenReader.Build(st, io.NowMs, pic); q.StableSinceMs = io.NowMs - 5000; return q; };
      var s1 = mk(NavState(ScreenKind.HeroScreen, 7, 3, 2, "", new[] { H, C }), w => w == "gearFilterMain");
      var st1 = ModalHandler.Next(s1, null, m, io, ClickOwner.Equip);
      Check(st1.Kind == StepKind.Act && st1.Act.Target == ClickTarget.EquipClosePopup && st1.Act.Closing == "panel:gearFilter", "nav modal: the gear filter's pop-up is closed by its red cross (EquipClosePopup)");
      Check(ModalHandler.Next(s1, new[] { "panel:gearFilter" }, new NavMemory(), io, ClickOwner.Equip).Kind == StepKind.None, "nav modal: a kept layer is left alone");
      var s2 = mk(NavState(ScreenKind.HeroScreen, 7, 3, -1, "", new[] { H, C }), w => w == "heroFilter");
      st1 = ModalHandler.Next(s2, null, new NavMemory(), io, ClickOwner.Equip);
      Check(st1.Kind == StepKind.Act && st1.Act.Target == ClickTarget.EquipFilter && st1.Act.Closing == "panel:heroFilter", "nav modal: the hero grid's filter is closed by the funnel (EquipFilter), once");
      var m3 = new NavMemory(); var s3 = mk(NavState(ScreenKind.HeroScreen, 7, 3, 2, "Form_CharactorEquipTip", new[] { "Form_CharactorEquipTip", H, C }), null);
      m3.SeenAt["Form_CharactorEquipTip"] = io.NowMs - 100;
      Check(ModalHandler.Next(s3, null, m3, io, ClickOwner.Equip).Kind == StepKind.Wait, "nav modal: a tip just seen is waited for (it closes by itself after «Заменить»)");
      m3.SeenAt["Form_CharactorEquipTip"] = io.NowMs - 5000; st1 = ModalHandler.Next(s3, null, m3, io, ClickOwner.Equip);
      Check(st1.Kind == StepKind.Act && st1.Act.Target == ClickTarget.EquipTipEscape && st1.Act.Kind == ActKind.Escape, "nav modal: a tip left over is closed by one Escape (EquipTipEscape)");
      Check(ModalHandler.Next(s3, NavKeep.ItemPick, new NavMemory(), io, ClickOwner.Equip).Kind == StepKind.None, "nav modal: the item pick keeps the tip");
      // a dangerous dialog is never an act
      string dang = null; foreach (var e in ScreenMap.All) if (e.Class == 'D') { dang = e.Name; break; }
      var s4 = mk(NavState(ScreenKind.HeroScreen, 7, 3, -1, "", new[] { dang, H, C }), null);
      var m4 = ModalHandler.Next(s4, null, new NavMemory(), io, ClickOwner.Equip);
      Check(m4.Kind != StepKind.Act, "nav modal: a dangerous dialog (" + dang + ") is never clicked (" + m4.Kind + ")");
      // Escape on the city is never an act
      string ec = null; foreach (var e in ScreenMap.All) if (e.Class == 'E' && e.Name != "Form_CharactorEquipTip") { ec = e.Name; break; }
      var s5 = mk(NavState(ScreenKind.City, 0, 0, 0, "", new[] { ec, C }), null);
      var m5 = ModalHandler.Next(s5, null, new NavMemory(), io, ClickOwner.Equip);
      Check(m5.Kind != StepKind.Act || m5.Act.Kind != ActKind.Escape, "nav modal: no Escape on the city for " + ec);
    }

    // the planner's first step from a table of start states
    {
      var io = new FakeNavIo(); var m = new NavMemory();
      Func<ScreenState, Func<string, bool>, Snapshot> mk = (st, pic) => { var q = ScreenReader.Build(st, io.NowMs, pic); q.StableSinceMs = io.NowMs - 5000; return q; };
      var eq = new NavTarget { Goal = NavGoal.HeroEquip, Hero = 7, Slot = 2 };
      Func<Snapshot, NavTarget, string> edge = (s, t) => { io.NowMs += 5000; var p = NavPlanner.Plan(s, t, null, new NavMemory(), io); return p.Kind + ":" + p.Edge; };
      Eq("Act:graph:hero", edge(mk(NavState(ScreenKind.City, 0, 0, 0, "", new[] { C }), null), eq), "nav plan: from the city the first step is «Герои»");
      Eq("Act:pop:Form_BackpackIntegration", edge(mk(NavState(ScreenKind.Inventory, 0, 0, 0, "", new[] { "Form_BackpackIntegration", C }), null), eq), "nav plan: from the inventory the first step is the back arrow");
      Eq("Act:facet:listClosed", edge(mk(NavState(ScreenKind.HeroScreen, 9, 3, 1, "", new[] { H, C }), null), eq), "nav plan: another hero with a gear list open: the list's back arrow");
      Eq("AtTarget:seek:hero", edge(mk(NavState(ScreenKind.HeroScreen, 9, 3, -1, "", new[] { H, C }), null), eq), "nav plan: another hero, no list: the hero pilot looks for the hero");
      Eq("Act:facet:gearTab", edge(mk(NavState(ScreenKind.HeroScreen, 7, 1, -1, "", new[] { H, C }), null), eq), "nav plan: the right hero on another tab: the gear tab");
      Eq("Act:facet:slot", edge(mk(NavState(ScreenKind.HeroScreen, 7, 3, 0, "", new[] { H, C }), null), eq), "nav plan: another slot open: the slot's icon");
      Eq("AtTarget:at_target", edge(mk(NavState(ScreenKind.HeroScreen, 7, 3, 2, "", new[] { H, C }), null), eq), "nav plan: everything holds: at the target");
      Eq("Act:modal:panel:gearFilter", edge(mk(NavState(ScreenKind.HeroScreen, 7, 3, 0, "", new[] { H, C }), w => w == "gearFilterMain"), eq), "nav plan: a gear filter left from the last item is closed before the next slot");
      var offS = mk(NavState(ScreenKind.City, 0, 0, 0, "", new[] { "Form_Recruitment", C }, "Form_Recruitment"), null);
      Check(edge(offS, eq).StartsWith("Wait") || edge(offS, eq).StartsWith("Act:modal"), "nav plan: an offer over the city is dealt with first (" + edge(offS, eq) + ")");
      var unstable = ScreenReader.Build(NavState(ScreenKind.City, 0, 0, 0, "", new[] { C }), io.NowMs, null);
      { var pw = NavPlanner.Plan(unstable, eq, null, new NavMemory(), io); Eq("Wait", pw.Kind.ToString(), "nav plan: a picture that has not stood still is waited for"); }
      var held = NavState(ScreenKind.City, 0, 0, 0, "", new[] { C }); held.Held = true;
      var hsn = ScreenReader.Build(held, io.NowMs, null); hsn.StableSinceMs = io.NowMs - 5000;
      Eq("Wait:", edge(hsn, eq), "nav plan: a held screen is never acted on");
      var cityT = new NavTarget { Goal = NavGoal.City };
      Eq("Act:pop:Form_CharactorMain", edge(mk(NavState(ScreenKind.HeroScreen, 7, 3, -1, "", new[] { H, C }), null), cityT), "nav plan: to the city from the hero screen: the back arrow");
      Eq("AtTarget:at_target", edge(mk(NavState(ScreenKind.City, 0, 0, 0, "", new[] { C }), null), cityT), "nav plan: in the city already");
    }
    // the predicted Escape from the stacks (the order seen in the owner's log of 1.6.47: the top is the END of the list; Flow keeps Form_LeftTip, back=Ignore)
    {
      Func<string, string, bool, bool, StackForm> F = (n, b, d, act) => new StackForm { Name = n, Back = b, HasDelegate = d, Active = act };
      var st = new Dictionary<string, IList<StackForm>>();
      st["Main"] = new List<StackForm> { F("Form_MainCity", "Handle", true, false), F("Form_CharactorMain", "Handle", true, true), F("Form_CharactorEquipTip", "Handle", true, true) };
      st["Flow"] = new List<StackForm> { F("Form_LeftTip", "Ignore", true, true) };
      var pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "close" && pr.Form == "Form_CharactorEquipTip", "nav escape: the tip on top of the hero screen is what Escape closes (" + pr + ")");
      st["Main"].RemoveAt(2); pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "close" && pr.Form == "Form_CharactorMain", "nav escape: then the hero screen (" + pr + ")");
      st["Main"] = new List<StackForm> { F("Form_MainCity", "Handle", true, true) }; pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "exit-dialog", "nav escape: on the bare city Escape opens the quit dialog - never sent (" + pr + ")");
      st["Main"] = new List<StackForm> { F("Form_MainCity", "Handle", true, true), F("Form_BattleEnd", "Block", false, true) }; pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "block" && pr.Form == "Form_BattleEnd", "nav escape: a Block form stops the search (" + pr + ")");
      st["Main"] = new List<StackForm> { F("Form_MainCity", "Handle", true, true), F("Form_X", "Handle", false, true) }; pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "swallowed", "nav escape: Handle with no back delegate swallows the key (" + pr + ")");
      st["Main"] = new List<StackForm> { F("Form_MainCity", "Handle", true, true), F("Form_Y", "default", false, true) }; pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "exit-dialog", "nav escape: a default form with no delegate passes the key down (" + pr + ")");
      st["Popup"] = new List<StackForm> { F("Form_Pop", "Handle", true, true) }; pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "close" && pr.Form == "Form_Pop" && pr.Stack == "Popup", "nav escape: a Popup-stack form is searched before Main (" + pr + ")");
    }
    // build 502's log: Flow holds Form_LeftTip (Ignore, a Lua override), Main = the bare city, then the quit dialog appears in Top (no delegate, no name)
    {
      Func<string, string, bool, bool, bool, StackForm> G2 = (n, b, d, act, lua) => new StackForm { Name = n, Back = b, HasDelegate = d, Active = act, LuaBehav = lua };
      var st = new Dictionary<string, IList<StackForm>>();
      st["Main"] = new List<StackForm> { G2("Form_MainCity", "Handle", true, true, false) };
      st["Flow"] = new List<StackForm> { G2("Form_LeftTip", "Ignore", true, true, true) };
      var pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "exit-dialog", "nav escape: LeftTip in Flow is Ignore, Escape on the bare city opens the quit dialog (" + pr + ")");
      st["Flow"] = new List<StackForm> { G2("id:-538304093", "default", true, true, true) }; pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "unknown", "nav escape: an unnamed Flow form with a Lua behaviour is not guessed (" + pr + ")");
      st["Flow"] = new List<StackForm> { G2("Form_Block1", "Block", false, true, true) }; pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "exit-dialog", "nav escape: a Block form in Flow ends only Flow's walk, Main is still searched (" + pr + ")");
      st["Main"] = new List<StackForm> { G2("Form_MainCity", "Handle", true, true, false), G2("Form_CharactorMain", "Handle", true, true, false) }; pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "close" && pr.Form == "Form_CharactorMain", "nav escape: ... and the hero screen is what Escape closes (" + pr + ")");
      st["Top"] = new List<StackForm> { G2("id:212616562", "default", false, true, false) }; pr = EscapePredictor.Predict(st);
      Check(pr.Kind == "exit-dialog-up", "nav escape: a nameless Top form with no back delegate is the quit dialog (" + pr + ")");
    }
    // the canvas scale of build 502: 1920x1009 client, scaler 1.5766 for a 1136x640 reference = fitted by height
    Eq("height", RFX.NavFit(1.5766, 1136, 640, 1920, 1009), "nav geometry: the canvas scale fits by height (1009/640)");
    Eq("none", RFX.NavFit(1.2, 1136, 640, 1920, 1009), "nav geometry: a scale that is neither is told");
    Check(Math.Abs(Ui.Unit(1920, 1009) - 1009) < 0.001, "nav geometry: Ui.Unit equals the client height on 1920x1009");
    // the hero pilot starts no action while the game's input is locked, and loses no try
    {
      var gm = new HeroGeometry(); var game = new FakeHeroGame(gm, 1920, 1009) { Heroes = new long[40], Shown = 1 };
      for (int i = 0; i < 40; i++) game.Heroes[i] = 500 + i;
      var pl = new HeroPilot(gm); int lockedActs = 0; string last = "";
      for (long t = 0; t < 60000; t += 100) {
        bool open = (t / 700) % 2 == 0;
        var v = new HeroView { NowMs = t, Foreground = true, W = 1920, H = 1009, Plan = 520, Hero = game.Shown, Tab = game.Tab, Part = game.Part, Heroes = game.Heroes, ClientH = 1009, GridPtr = game.Grid, InputOpen = open };
        var act = pl.Step(v); last = pl.State;
        if (act == null) break;
        if (act.Kind != AutoKind.None) { if (!open) lockedActs++; if (act.Kind == AutoKind.Click) game.Click(act.X, act.Y); else game.Drag(act.DY); }
      }
      Check(lockedActs == 0 && last == "done" && game.Shown == 520, "nav pilot: no action into a locked input, the hero is found all the same (" + lockedActs + " locked acts, " + last + ")");
    }
    // the input gate: nothing is sent into a locked input
    {
      var io = new FakeNavIo(); io.Open = false; var rdr = new ScreenReader(); var cs = NavState(ScreenKind.City, 0, 0, 0, "", new[] { "Form_MainCity" }); io.FreshState = cs;
      var sn2 = ScreenReader.Build(cs, io.NowMs, null); sn2.StableSinceMs = io.NowMs - 1000;
      var ac = new ActClick(); ac.Start(new Act { Target = ClickTarget.EquipNav, Kind = ActKind.Click, X = 10, Y = 10, Base = "Form_MainCity" }, io.NowMs);
      Check(ac.Tick(io, sn2, io.NowMs) == ActOutcome.NotSent && io.Sent.Count == 0, "nav act: not sent while the game's input is locked");
      io.Open = true; ac.Start(new Act { Target = ClickTarget.EquipNav, Kind = ActKind.Click, X = 10, Y = 10, Base = "Form_MainCity" }, io.NowMs);
      Check(ac.Tick(io, sn2, io.NowMs) == ActOutcome.Running && io.Sent.Count == 1, "nav act: sent as soon as the input is open");
    }
    // the recipes (tools/recipes/*.json compiled into NavRecipesData.cs): the steps, the conditions, the compiled waypoints
    {
      var rq = NavRecipes.Get("equip");
      Check(rq != null && rq.Steps.Count == 7 && rq.Steps[0].Id == "heroes" && rq.Steps[2].IsSeek && rq.Steps[5].IsAction && !rq.Steps[5].IsSeek && rq.Params.Length == 2, "nav recipe: equip has its seven steps (" + (rq != null ? rq.Steps.Count.ToString() : "-") + ")");
      var hsn = ScreenReader.Build(NavState(ScreenKind.HeroScreen, 7, 3, 2, "", new[] { "Form_CharactorMain", "Form_MainCity" }), 0, null);
      var args = new Dictionary<string, long> { { "hero", 9 }, { "slot", 2 } };
      Check(NavFacets.Eval("Hero!=$hero && HeroSlot>=0", hsn, args, false) && !NavFacets.Eval("Hero==$hero", hsn, args, false) && NavFacets.Eval("HeroTab==gear", hsn, args, false) && NavFacets.Eval("$slot>=0", hsn, args, false), "nav recipe: conditions read the facets and the parameters");
      var unread = ScreenReader.Build(NavState(ScreenKind.HeroScreen, 7, -1, -1, "", new[] { "Form_CharactorMain", "Form_MainCity" }), 0, null);
      Check(NavFacets.Eval("HeroTab==gear", unread, args, true) && !NavFacets.Eval("HeroTab==gear", unread, args, false), "nav recipe: an unread tab passes the hold test but not the click's own check");
      var wps = NavRecipes.Compile(rq, args, "", ClickOwner.Equip);
      Check(wps.Count == 5, "nav recipe: the navigation steps become waypoints (" + wps.Count + ")");
      Check(NavRecipes.Compile(rq, args, "gearTab", ClickOwner.Equip).Count == 4 && NavRecipes.Compile(rq, args, "heroes", ClickOwner.Equip).Count == 1, "nav recipe: a goal ends at its step");
      Check(NavRecipes.Get("city").Steps.Count == 1 && NavRecipes.Get("nothing") == null, "nav recipe: the city recipe, an unknown id is null");
    }
    // the game is raised over a browser or our own window only, once per run; never over a launcher or a system dialog
    Check(FrontPolicy.IsBrowser("chrome") && FrontPolicy.IsBrowser("msedge") && FrontPolicy.IsBrowser("Firefox") && !FrontPolicy.IsBrowser("moontonlauncher") && !FrontPolicy.IsBrowser("PlariumPlay") && !FrontPolicy.IsBrowser("Telegram") && !FrontPolicy.IsBrowser(""), "front: browsers are told from launchers and chats");
    Check(FrontPolicy.ShouldRaiseOverBrowser(true, true, false, false, false, 5000, 10, 0, true, false), "front: a waiting pilot raises the game over an idle browser once");
    Check(!FrontPolicy.ShouldRaiseOverBrowser(true, true, false, false, false, 5000, 10, 0, true, true), "front: ... not a second time in the run");
    Check(!FrontPolicy.ShouldRaiseOverBrowser(true, true, false, false, false, 5000, 10, 0, false, false), "front: ... never over another program (the launcher)");
    Check(!FrontPolicy.ShouldRaiseOverBrowser(true, true, false, false, false, 100, 10, 0, true, false), "front: ... never while the player is active");
    // D5: a pilot's fire click is "sent", not progress; only its own ProgressAt (or a new screen state) keeps the step clock going
    {
      Func<bool, string> run = marks => {
        var io = new FakeNavIo(); var rdr = new ScreenReader(); var nv = new Navigator(ClickOwner.Equip);
        var st = NavState(ScreenKind.HeroScreen, 7, 3, 2, "", new[] { "Form_CharactorMain", "Form_MainCity" }); io.FreshState = st;
        var stub = new StubAction { Marks = marks, Io = io };
        nv.SetTarget(new NavTarget { Goal = NavGoal.HeroGear, Hero = 7 }, new List<IAtTargetAction> { stub });
        for (int i = 0; i < 400 && nv.State != "halted" && nv.Recoveries == 0; i++) { io.NowMs += 100; nv.Tick(io, rdr.Update(st, io.NowMs)); }
        return nv.State + "/" + nv.Recoveries + "/" + io.Sent.Count;
      };
      string noMarks = run(false), withMarks = run(true);
      Check(noMarks.StartsWith("work/1/") || noMarks.StartsWith("at_target/1/"), "nav stall: a pilot that only sends clicks, with no progress mark, is reset after " + Navigator.StepStallMs + " ms (" + noMarks + ")");
      Check(withMarks.EndsWith("/0/") == false && withMarks.Split('/')[1] == "0", "nav stall: the same pilot with a progress mark every second is never reset in 40 s (" + withMarks + ")");
    }
    // D6: a gate wait that lasts is escalated - our own windows hidden after 3 s when they are the cause, a reset by 8 s whatever the cause (a reset hides them too)
    {
      Func<string, string> run = reason => {
        var io = new FakeNavIo { Verdict = GateVerdict.Wait, WaitReason = reason }; var rdr = new ScreenReader(); var nv = new Navigator(ClickOwner.Equip);
        var st = NavState(ScreenKind.HeroScreen, 7, 3, 2, "", new[] { "Form_CharactorMain", "Form_MainCity" }); io.FreshState = st;
        nv.SetTarget(new NavTarget { Goal = NavGoal.HeroGear, Hero = 7 }, new List<IAtTargetAction> { new StubAction { Io = io } });
        long t0 = io.NowMs; int hidAt3 = -1;
        for (int i = 0; i < 200 && nv.Recoveries == 0; i++) { io.NowMs += 100; nv.Tick(io, rdr.Update(st, io.NowMs)); if (io.NowMs - t0 >= 3600 && hidAt3 < 0) hidAt3 = io.Hid; }
        return hidAt3 + "/" + nv.Recoveries + "/" + (io.NowMs - t0) + "/" + io.Hid;
      };
      string own = run("own"), other = run("screen");
      Check(own.StartsWith("1/1/") && long.Parse(own.Split('/')[2]) <= 9500, "nav gate wait: a click waiting on our own hint hides our windows after 3 s and the step is reset by 9 s (" + own + ")");
      Check(other.StartsWith("0/1/") && long.Parse(other.Split('/')[2]) <= 9500, "nav gate wait: any other cause is a reset by 9 s, our windows are not hidden before it (" + other + ")");
    }
    // D7: the way back to the city is a ladder; a back arrow that does nothing ends in the named stop reset_failed:<form> within 40 s, not in a wait
    {
      var io = new FakeNavIo(); var rdr = new ScreenReader(); var nv = new Navigator(ClickOwner.Equip);
      var inv = NavState(ScreenKind.Inventory, 0, 0, 0, "", new[] { "Form_BackpackIntegration", "Form_MainCity" }); io.FreshState = inv;
      nv.SetTarget(new NavTarget { Goal = NavGoal.City }, new List<IAtTargetAction>());
      long t0 = io.NowMs;
      for (int i = 0; i < 600 && nv.State != "halted"; i++) { io.NowMs += 100; nv.Tick(io, rdr.Update(inv, io.NowMs)); }
      Check(nv.State == "halted" && nv.HaltWhy.StartsWith("reset_failed:") && io.NowMs - t0 <= 40000, "nav ladder: a back arrow that does nothing ends in reset_failed:<form> (" + nv.State + " " + nv.HaltWhy + " after " + (io.NowMs - t0) + " ms)");
    }
    // the navigator's watchdog: a run always ends
    {
      var io = new FakeNavIo(); var rdr = new ScreenReader(); var nv = new Navigator(ClickOwner.Equip);
      var wait = NavState(ScreenKind.City, 0, 0, 0, "", new[] { C }); wait.Held = true;
      nv.SetTarget(new NavTarget { Goal = NavGoal.HeroGear, Hero = 7 }, new List<IAtTargetAction>());
      for (int i = 0; i < 2500 && nv.State != "halted"; i++) { io.NowMs += 100; nv.Tick(io, rdr.Update(wait, io.NowMs)); }
      Check(nv.State == "halted" && nv.HaltWhy.StartsWith("recipe_stuck:stall"), "nav stall: a screen that never reads is " + Navigator.MaxResets + " resets (the clocks stand still for the grace of the unreadable screen), then a named stop (" + nv.State + " " + nv.HaltWhy + ")");
      // verify-or-reset: a click whose effect never shows is a reset (back to the city, then the step again), MaxResets times, then recipe_stuck:<step>:<outcome>
      var io2 = new FakeNavIo(); var rd2 = new ScreenReader(); var nv2 = new Navigator(ClickOwner.Equip);
      var cityS2 = NavState(ScreenKind.City, 0, 0, 0, "", new[] { "Form_MainCity" }); io2.FreshState = cityS2;
      nv2.SetTarget(new NavTarget { Goal = NavGoal.HeroGear, Hero = 7 }, new List<IAtTargetAction>());
      for (int i = 0; i < 1500 && nv2.State != "halted"; i++) { io2.NowMs += 100; nv2.Tick(io2, rd2.Update(cityS2, io2.NowMs)); }
      Check(nv2.State == "halted" && nv2.HaltWhy.StartsWith("recipe_stuck:graph:hero:NoEffect") && io2.Sent.Count == Navigator.MaxResets + 1, "nav reset: a step with no effect is retried after a reset " + Navigator.MaxResets + " times, then a named stop (" + nv2.HaltWhy + ", " + io2.Sent.Count + " clicks)");
    }
  }
}
