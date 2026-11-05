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
    // the program window and the pilot's click: the window is put aside only for a click it would catch (it is the window at the point), never for one it lies under
    Check(FrontPolicy.ClickNeedsOwnAside(true, false, 100, 100, 600, 500, 300, 300, 300, true), "window aside: a click on our window at the point: yes");
    Check(!FrontPolicy.ClickNeedsOwnAside(true, false, 100, 100, 600, 500, 300, 300, 300, false), "window aside: the rect covers the point but the game is over it: no flip (was the flash after every click)");
    Check(!FrontPolicy.ClickNeedsOwnAside(true, false, 100, 100, 600, 500, 900, 300, 900, true), "window aside: the point is outside our rect: no");
    Check(!FrontPolicy.ClickNeedsOwnAside(true, true, 100, 100, 600, 500, 300, 300, 300, true) && !FrontPolicy.ClickNeedsOwnAside(false, false, 100, 100, 600, 500, 300, 300, 300, true), "window aside: minimized / hidden window: no");
    Check(FrontPolicy.ClickNeedsOwnAside(true, false, 100, 100, 600, 500, 300, 50, 300, true) && !FrontPolicy.ClickNeedsOwnAside(true, false, 100, 100, 600, 500, 300, 50, 80, true), "window aside: a drag that reaches the window counts, one that stays above it does not");
    var zl = new ZFlipLog();
    string l1 = zl.Line(1000, "own window"), l2 = zl.Line(1300, "own window"), l3 = zl.Line(1700, "own window"), l4 = zl.Line(2100, "own window");
    Check(l1 != null && l1.StartsWith("win: z-order flip reason=own window") && l2 == null && l3 == null && l4 != null && l4.Contains("+2 more"), "window flip log: once a second, the rest counted: " + l4);
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
    BuildPlannerTests();
    BuildSimTests();
    AtomTests();
    ConfirmServerTests();
    Lab559Tests();
  }

  // ---------------------------------------------------------------- the build planner (docs/specs/2026-11-04-shortest-paths.md 2): modes, order, flags, norms
  static BuildItem BI(int slot, long uid, long set, int row, bool worn = false) { return new BuildItem { Slot = slot, Uid = uid, SetId = set, Row = row, Worn = worn }; }
  static string Order(BuildPlan p) { var l = new List<string>(); foreach (var st in p.Steps) l.Add(st.Item.Slot + ":" + st.Mode); return string.Join(" ", l.ToArray()); }

  static void BuildPlannerTests() {
    Console.WriteLine("Build planner");
    int overhead = 1000 + 3000 + 700;   // city -> heroes, the hero, the gear tab
    // all five in the first screenful: no filter, no drag, within the 18 s of the plan (with the artifact)
    var all = new List<BuildItem> { BI(1, 11, 5, 1), BI(2, 12, 5, 2), BI(3, 13, 6, 3), BI(4, 14, 6, 4), BI(5, 15, 7, 2), new BuildItem { Slot = 9, Uid = 16, Artifact = true } };
    var p = BuildPlanner.Plan(all, false, false, false);
    Check(p.FilterOpens == 0 && !p.Steps.Exists(x => x.Mode == SlotMode.Filter || x.Mode == SlotMode.Scroll) && p.Steps[p.Steps.Count - 1].Item.Artifact && p.EstimateMs + overhead <= 20000,
      "planner: everything in view is direct, no filter, the artifact last, " + (p.EstimateMs + overhead) + " ms (" + Order(p) + ")");
    // the worn slot costs nothing and goes first
    p = BuildPlanner.Plan(new List<BuildItem> { BI(1, 11, 5, 1, true), BI(2, 12, 5, 3) }, false, false, false);
    Check(p.Steps[0].Mode == SlotMode.Skip && p.Steps[0].EstimateMs == 0 && p.EstimateMs == BuildPlanner.ItemMs, "planner: an item already on the hero is skipped (" + Order(p) + ")");
    // an item a screen or two away: computed drags, no filter (1-3 drags cost less than the filter's opening); one far of no set: drags as well
    p = BuildPlanner.Plan(new List<BuildItem> { BI(1, 11, 5, 9), BI(2, 12, 0, 40) }, false, false, false);
    Check(p.Steps[0].Mode == SlotMode.Scroll && p.Steps[1].Mode == SlotMode.Scroll && p.FilterOpens == 0, "planner: a nearby row and a far item of no set are scrolled, never filtered (" + Order(p) + ")");
    // a far lone item of a set: the filter (more than 3 s of drags)
    p = BuildPlanner.Plan(new List<BuildItem> { BI(2, 12, 7, 40) }, false, false, false);
    Check(p.Steps[0].Mode == SlotMode.Filter && p.Steps[0].SuitId == 7 && p.FilterOpens == 1, "planner: a far item of a set takes the set filter (" + Order(p) + ")");
    // the spec's case: a group of 3, a group of 2, one in view, the artifact: no-filter slots first, then the 3, then the 2, the artifact last, the pop-up opened once and the part changed inside it (the owner's screenshot: its own «Выбрать часть» row)
    var two = new List<BuildItem> { BI(1, 21, 8, 30), BI(2, 22, 9, 28), BI(3, 23, 8, 45), BI(4, 24, 9, 60), BI(5, 25, 8, 35), BI(6, 26, 4, 2), new BuildItem { Slot = 9, Uid = 27, Artifact = true } };
    p = BuildPlanner.Plan(two, false, false, false);
    string ord = Order(p);
    Check(ord == "6:Direct 1:Filter 3:Filter 5:Filter 2:Filter 4:Filter 9:Direct" && p.FilterOpens == 2 && p.Steps[1].Group == 1 && p.Steps[4].Group == 2 && p.Steps[1].EstimateMs == BuildPlanner.ItemMs + BuildPlanner.FilterOpenMs && p.Steps[2].EstimateMs == BuildPlanner.ItemMs + BuildPlanner.PartChangeMs
        && p.Steps[4].EstimateMs == BuildPlanner.ItemMs + BuildPlanner.SetChangeMs + BuildPlanner.PartChangeMs && p.EstimateMs + overhead >= 20000 && p.EstimateMs + overhead <= 30000,
      "planner: the free slot, the group of 3, the group of 2, the artifact last, one opening (2 s), a part change 0.6 s per further item, a set change 1.8 s, " + (p.EstimateMs + overhead) + " ms (" + ord + ")");
    // the rows around the limit: 13 by drags (3 drags = 2.7 s), 14 by the set filter; unknown row = in view
    Check(BuildPlanner.DirectMaxRow == 13 && !BuildPlanner.FilterBeatsScroll(BuildPlanner.DirectMaxRow) && BuildPlanner.FilterBeatsScroll(BuildPlanner.DirectMaxRow + 1) && BuildPlanner.FilterOpenMs == 2000 && BuildPlanner.PartChangeMs == 600 && BuildPlanner.FilterFullMs == 6000,
      "planner: the filter is taken from the 14th row (the set-only filter 2 s, a part change 0.6 s, the full filter 6 s)");
    p = BuildPlanner.Plan(new List<BuildItem> { BI(1, 31, 5, 13) }, false, false, false); var p14 = BuildPlanner.Plan(new List<BuildItem> { BI(2, 32, 5, 14), BI(3, 33, 0, 14) }, false, false, false);
    Check(Order(p) == "1:Scroll" && Order(p14) == "3:Scroll 2:Filter", "planner: row 13 of a set is scrolled, row 14 takes the filter, row 14 of no set is scrolled (" + Order(p) + " | " + Order(p14) + ")");
    p = BuildPlanner.Plan(new List<BuildItem> { BI(1, 31, 5, 30), BI(2, 32, 5, 33), BI(3, 33, 0, 14) }, false, false, false);
    Check(Order(p) == "3:Scroll 1:Filter 2:Filter", "planner: two far items of a set take the group's filter, the far item of no set is scrolled (" + Order(p) + ")");
    // F6b (owner's screenshot): the gear filter pop-up has its own part row, so while it is open on the gear list the 'slot' step holds (the hero screen's slot icons are covered) and the filter action is the way to the target's slot
    {
      var tg = new NavTarget { Goal = NavGoal.HeroEquip, Hero = 7, Slot = 4 };
      Func<Mem3, Snapshot> snap = open => { var st = GearState(3, 2, open, Mem3.No, Mem3.No); return new ScreenReader().Update(st, 1000); };
      Func<Snapshot, bool> slotHolds = sn => { foreach (var w in NavRoutes.Of(tg)) if (w.Key == "facet:slot") return w.Holds(sn, tg); return false; };
      var sOpen = snap(Mem3.Yes); var sShut = snap(Mem3.No);
      Check(NavFacets.Read("GearFilter", sOpen) == 1 && NavFacets.Read("GearFilter", sShut) == 0 && NavFacets.Read("HeroSlot", sOpen) == 2, "F6b: the GearFilter facet is the game's flag on a shown gear list");
      Check(slotHolds(sOpen) && !slotHolds(sShut), "F6b: the slot step holds while the pop-up is open (another slot wanted), and presses the hero slot icon when it is shut");
      var fa = new GearFilterAction(new FilterPilot(new FilterGeometry()), (s0, t0, n) => new FilterView { NowMs = n }, () => false);
      Check(fa.Wants(sOpen, tg) && !fa.Wants(sShut, tg), "F6b: an open pop-up that is not this target's yet: the filter action wants the tick (its part row), a shut one with no verdict does not");
    }
    // the player's flags that hide an item are cleared only when an item needs it
    var hid = new List<BuildItem> { BI(1, 11, 5, 0), new BuildItem { Slot = 2, Uid = 12, HiddenByWorn = true }, new BuildItem { Slot = 3, Uid = 13, HiddenByEnhanced = true } };
    Check(BuildPlanner.Plan(hid, true, false, false).Flags.ClearWorn && !BuildPlanner.Plan(hid, true, false, false).Flags.ClearEnhanced && BuildPlanner.Plan(hid, true, true, true).Flags.ToString() == "worn enhanced " && !BuildPlanner.Plan(hid, false, false, false).Flags.Any,
      "planner: the hide flags are cleared only where an item of the build is hidden by one that is on");
    // S3: the flag is cleared once only where an item needs it, never put back; the applog line says what was done
    {
      var k = new BuildFlagKeeper(); var need = new BuildFlags { ClearWorn = true };
      Check(k.ClearDue(1, 0) == "", "flags: nothing before the build begins");
      k.Begin(1, 0, need);
      Check(k.ClearDue(1, 0) == "worn", "flags: «Скрыть надетое» on and needed: cleared first");
      k.Cleared("worn");
      Check(k.ClearDue(0, 0) == "", "flags: cleared once, not again");
      var k2 = new BuildFlagKeeper(); k2.Begin(0, 0, need);
      Check(k2.ClearDue(0, 0) == "", "flags: a flag that was off is never switched");
      var k3 = new BuildFlagKeeper(); k3.Begin(1, 1, new BuildFlags());
      Check(k3.ClearDue(1, 1) == "", "flags: no item needs a flag cleared: they are not touched");
      Check(BuildFlagKeeper.Line(1, true) == "flags: hideWorn before=on action=cleared" && BuildFlagKeeper.Line(1, false) == "flags: hideWorn before=on action=kept" && BuildFlagKeeper.Line(-1, false) == "flags: hideWorn before=unread action=kept", "flags: the applog line");
    }
    // the norms are the recipe's budgets
    Check(BuildPlanner.NormMs("build", "pick", 0) == BuildPlanner.ItemLimitMs && BuildPlanner.NormMs("build", "filter", 0) == BuildPlanner.FilterLimitMs && BuildPlanner.NormMs("build", "hero", 0) == 8000 && BuildPlanner.NormMs("equip", "pick", 0) == BuildPlanner.ItemLimitMs,
      "planner: the norms (item 6 s, filter 4 s, hero 8 s) are the build recipe's budgets, no 60 s");
    // an item pick that works past its norm is reset with a diagnosis, not repeated
    {
      var io = new FakeNavIo(); var pl = new AutoPilot(new ListGeometry());
      var ia = new ItemPickAction(pl, (s, t, n) => new AutoView { NowMs = n, Foreground = true, H = 705, Target = 5, Row = 3, Col = 1, Sel = 0, AnchorHas = true, Types = new[] { 0, 1, 1, 1 }, Row1Top = 100, Hero = 5, ScreenHero = 5, Owner = 0, InputOpen = false },
        (v, a) => ClickTarget.EquipNav);
      var t0 = new NavTarget { Goal = NavGoal.HeroEquip, Hero = 5, Slot = 2 }; var rd = new ScreenReader(); var st = NavState(ScreenKind.HeroScreen, 5, 3, 2, "", new[] { "Form_CharactorMain", "Form_MainCity" }); io.FreshState = st;
      string reset = ""; long first = io.NowMs;
      for (int i = 0; i < 200 && reset == ""; i++) { io.NowMs += 200; var sn = rd.Update(st, io.NowMs); var ns = ia.Step(sn, t0, io, io.NowMs); if (ns != null && ns.Kind == StepKind.Reset) reset = ns.Why; }
      Check(reset.StartsWith("item_limit ") && io.NowMs - first >= 6000 && io.NowMs - first <= 9000, "planner: an item that takes past its 6 s norm is a reset with its diagnosis (" + reset + ", after " + (io.NowMs - first) + " ms)");
    }
    // lab 552 (312cc9: the item filter open): the filter's own clicks and waits ran the pick's 6 s norm out -> item_limit -> the city and back for the same slot, three times. The filter has its own budget
    {
      var io = new FakeNavIo(); var pl = new AutoPilot(new ListGeometry());
      var fpl = new FilterPilot(new FilterGeometry());
      var gfa = new GearFilterAction(fpl, (s0, t1, n) => new FilterView { NowMs = n, Foreground = true, W = 1366, H = 705, Item = 5, Wanted = true, SetId = 721500, SetIndex = 3, Suits = new long[0], MainAttrs = new long[0], PanelMem = Mem3.Unknown, MemPending = true }, null);
      var ia = new ItemPickAction(pl, (s0, t1, n) => new AutoView { NowMs = n, Foreground = true, H = 705, Target = 5, Row = 3, Col = 1, Sel = 0, AnchorHas = true, Types = new[] { 0, 1, 1, 1 }, Row1Top = 100, Hero = 5, ScreenHero = 5, Owner = 0, InputOpen = false },
        (v, a) => ClickTarget.EquipNav, gfa);
      var t0 = new NavTarget { Goal = NavGoal.HeroEquip, Hero = 5, Slot = 2 }; var rd = new ScreenReader(); var st = NavState(ScreenKind.HeroScreen, 5, 3, 2, "", new[] { "Form_CharactorMain", "Form_MainCity" }); io.FreshState = st;
      string reset = ""; long first = io.NowMs;
      for (int i = 0; i < 200 && reset == ""; i++) { io.NowMs += 200; var sn = rd.Update(st, io.NowMs); var ns = ia.Step(sn, t0, io, io.NowMs); if (ns != null && ns.Kind == StepKind.Reset) reset = ns.Why; }
      Check(reset.StartsWith("filter_limit ") && io.NowMs - first >= ItemPickAction.FilterAllowMs && io.NowMs - first <= ItemPickAction.FilterAllowMs + 3000, "planner: the filter's time is its own budget (not the pick's 6 s): a reset only past " + ItemPickAction.FilterAllowMs + " ms (" + reset + ", after " + (io.NowMs - first) + " ms)");
    }
  }

  // ---------------------------------------------------------------- S4: the build on the planner in the sim (the pilot runs every item on the game's list model, the filter and the artifact are costed by the planner's own norms)
  sealed class BuildRunResult { public long Ms; public int Drags, Blind, Refused, Resets, Filters; public string Fail = ""; public BuildPlan Plan; public string Line = ""; }

  /// <summary>The whole build in sim time: <paramref name="overheadMs"/> (city -> heroes, the hero, the gear tab, and the way back to the city when the run starts elsewhere), then the plan's steps in the planner's order. Items in view or scrolled run on the real
  /// AutoPilot over the list model (AtomListSim: threshold, inertia 12 %); a filtered item is the list the filter leaves (its row 1-3) after the planner's cost of opening / changing the set; the artifact is its norm. A flag to clear costs a toggle at the start and one at the end.</summary>
  static BuildRunResult BuildRun(List<BuildItem> items, long overheadMs, bool flagWorn) {
    var r = new BuildRunResult(); var plan = BuildPlanner.Plan(items, flagWorn, false, false); r.Plan = plan; r.Ms = overheadMs;
    if (plan.Flags.Any) r.Ms += 2 * BuildPlanner.SetChangeMs;
    var inGroup = new Dictionary<int, int>();
    foreach (var st in plan.Steps) {
      if (st.Mode == SlotMode.Skip) continue;
      if (st.Item.Artifact) { r.Ms += BuildPlanner.ArtifactMs; continue; }
      int row = st.Item.Row > 0 ? st.Item.Row : 1;
      if (st.Mode == SlotMode.Filter) {
        int n; inGroup.TryGetValue(st.Group, out n); inGroup[st.Group] = n + 1;
        row = 1 + n % 3; r.Ms += st.EstimateMs - BuildPlanner.ItemMs; r.Filters += n == 0 ? 1 : 0;
      }
      var sim = new AtomListSim { Gain = 0.9, TargetRow = row, Inertia = 0.12 };
      AutoPilot p; long doneAt; int clicks; string res = AtomListRun(sim, true, out p, out doneAt, out clicks);
      if (res != "done") { r.Fail = "slot " + st.Item.Slot + ": " + res; return r; }
      r.Ms += doneAt; r.Drags += sim.Drags; r.Blind += sim.Blind; r.Refused += p.Atoms.Refused;
      if (doneAt > BuildPlanner.ItemLimitMs + sim.Drags * ItemPickAction.DragAllowMs) r.Resets++;
    }
    r.Line = plan.ToString() + " | " + r.Ms + " ms, " + r.Drags + " drags, " + r.Filters + " filter openings";
    return r;
  }

  static void BuildSimTests() {
    Console.WriteLine("Build in the sim (S4)");
    int city = 1000 + 3000 + 700, fromElsewhere = city + 3500;
    Func<int, long, long, int, bool, BuildItem> it = (slot, uid, set, row, art) => new BuildItem { Slot = slot, Uid = uid, SetId = set, Row = row, Artifact = art };
    // 1. every item in view
    var s1 = new List<BuildItem> { it(1, 11, 5, 1, false), it(2, 12, 5, 2, false), it(3, 13, 6, 3, false), it(4, 14, 6, 4, false), it(5, 15, 7, 2, false), it(9, 16, 0, 0, true) };
    var r1 = BuildRun(s1, city, false);
    Check(r1.Fail == "" && r1.Ms <= 20000 && r1.Blind == 0 && r1.Refused == 0 && r1.Resets == 0 && r1.Drags == 0 && r1.Filters == 0, "build sim 1: everything in view: " + r1.Line + " (budget 20 s, blind " + r1.Blind + ", repeats " + r1.Refused + ", resets " + r1.Resets + ") " + r1.Fail);
    // 2. a group of 3 and of 2 far down, one free, the artifact
    var s2 = new List<BuildItem> { it(1, 21, 8, 30, false), it(2, 22, 9, 28, false), it(3, 23, 8, 45, false), it(4, 24, 9, 60, false), it(5, 25, 8, 35, false), it(6, 26, 4, 2, false), it(9, 27, 0, 0, true) };
    var r2 = BuildRun(s2, city, false);
    Check(r2.Fail == "" && r2.Ms <= 30000 && r2.Blind == 0 && r2.Refused == 0 && r2.Resets == 0 && r2.Filters == 2 && r2.Plan.FilterOpens == 2, "build sim 2: two set groups: " + r2.Line + " (budget 30 s, blind " + r2.Blind + ", repeats " + r2.Refused + ", resets " + r2.Resets + ") " + r2.Fail);
    // 3. one item far with no set (no filter can bring it): computed drags, within the norm plus its drags
    var s3 = new List<BuildItem> { it(1, 31, 5, 2, false), it(2, 32, 0, 40, false), it(3, 33, 6, 3, false), it(4, 34, 6, 4, false), it(5, 35, 7, 1, false), it(9, 36, 0, 0, true) };
    var r3 = BuildRun(s3, city, false);
    Check(r3.Fail == "" && r3.Ms <= 30000 && r3.Blind == 0 && r3.Refused == 0 && r3.Resets == 0 && r3.Drags <= BuildPlanner.DragsFor(40) + 2, "build sim 3: an item far of no set: " + r3.Line + " (budget 30 s, blind " + r3.Blind + ", repeats " + r3.Refused + ", resets " + r3.Resets + ") " + r3.Fail);
    // 4. an item hidden by «Скрыть надетое» (worn by another hero): the flag is cleared once for the build and not put back
    var s4 = new List<BuildItem> { it(1, 41, 5, 1, false), new BuildItem { Slot = 2, Uid = 42, SetId = 5, Row = 0, HiddenByWorn = true }, it(3, 43, 6, 3, false), it(9, 44, 0, 0, true) };
    var r4 = BuildRun(s4, city, true);
    var kp = new BuildFlagKeeper(); kp.Begin(1, 0, r4.Plan.Flags); string c1 = kp.ClearDue(1, 0); kp.Cleared(c1);
    Check(r4.Fail == "" && r4.Plan.Flags.ClearWorn && c1 == "worn" && r4.Ms <= 30000 && r4.Blind == 0 && r4.Refused == 0, "build sim 4: a hidden item: " + r4.Line + " (flags " + r4.Plan.Flags + "cleared once, never put back) " + r4.Fail);
    // 5. two heroes of one class (the same sets): each build is planned and run by itself, the second one does not lean on the first one's filter
    var h1 = BuildRun(s2, city, false); var h2 = BuildRun(s2, 3000 + 700, false);
    Check(h1.Fail == "" && h2.Fail == "" && h1.Plan.FilterOpens == 2 && h2.Plan.FilterOpens == 2 && h1.Ms <= 30000 && h2.Ms <= 30000 && h1.Blind + h2.Blind == 0, "build sim 5: two heroes of one class: " + h1.Ms + " ms and " + h2.Ms + " ms, " + (h1.Plan.FilterOpens + h2.Plan.FilterOpens) + " filter openings in all");
    // 6. the run starts away from the city (a pop-up, another screen): the way back costs more, the budget holds
    var r6 = BuildRun(s2, fromElsewhere, false); var r6b = BuildRun(s1, fromElsewhere, false);
    Check(r6.Fail == "" && r6b.Fail == "" && r6.Ms <= 30000 && r6b.Ms <= 30000 && r6.Blind + r6b.Blind == 0, "build sim 6: the start not from the city: " + r6.Ms + " ms (two groups), " + r6b.Ms + " ms (all in view)");
    Console.WriteLine("  budget table (sim ms, with the way to the gear tab): all in view " + r1.Ms + " | two set groups " + r2.Ms + " | far item " + r3.Ms + " | hidden item " + r4.Ms + " | two heroes " + h1.Ms + "+" + h2.Ms + " | not from the city " + r6.Ms + "/" + r6b.Ms + " | the norms: item " + BuildPlanner.ItemLimitMs + ", filter " + BuildPlanner.FilterLimitMs + ", build 30000");
  }

  // ---------------------------------------------------------------- the atoms (docs/specs/2026-11-04-game-atoms.md A1 / A2): the replay of the recording 464e21 (tests/fixtures/rec-464e21-atoms.txt)

  /// <summary>The game's gear list as the recording shows it (client 1366x705): rows of one cell pitch, the cells the game made (first..last, with a buffer of rows beyond the viewport), a drag that moves the content by Gain of the pointer's move,
  /// a click on a cell that selects its item (the card opens, the list is located to it), a click on the item that is selected already that does nothing (_OnEquipClicked), a click on the card's button that puts the item on the hero.</summary>
  sealed class AtomListSim {
    public readonly ListGeometry G = new ListGeometry(); public double H = 705, Pitch, Cell, VT, VB, Scroll, Gain = 0.9, Thr = 8, Inertia = 0; public bool Mem = true;
    public int Rows = 60, TargetRow = 38; public long Target = 19993, Sel, TipUid; public bool Tip, Worn;
    public int Drags, Blind, OnSelected, OnTarget, OnNb, Confirms; public long FirstClickAt = -1, WornAt = -1; public string FirstClick = "";
    readonly List<KeyValuePair<long, Action>> due = new List<KeyValuePair<long, Action>>();
    public AtomListSim() { Pitch = G.PitchY * H; Cell = G.CellW * H; VT = G.ViewTop * H; VB = G.ViewBottom * H; }
    public long UidOf(int row) { return row == TargetRow ? Target : 1000 + row; }
    public int RowOf(long uid) { return uid == Target ? TargetRow : (int)(uid - 1000); }
    public double CellY(int row) { return VT + Cell / 2 + (row - 1) * Pitch - Scroll; }
    public bool Whole(int row) { double y = CellY(row); return y - Cell / 2 >= VT && y + Cell / 2 <= VB; }
    public int First { get { return Math.Max(0, (int)Math.Floor(Scroll / Pitch) - 1); } }
    public int Last { get { return Math.Min(Rows - 1, (int)Math.Floor((Scroll + VB - VT) / Pitch) + 2); } }
    public bool Made(int row) { return row - 1 >= First && row - 1 <= Last; }
    public void At(long t, Action a) { due.Add(new KeyValuePair<long, Action>(t, a)); }
    public void Advance(long t) { foreach (var e in due.ToArray()) if (e.Key <= t) { due.Remove(e); e.Value(); } }
    /// <summary>How far the list can scroll (px): all rows and the top pad, less the view.</summary>
    public double MaxScroll { get { return Math.Max(0, Rows * Pitch + ListGeometry.TopPad * H - (VB - VT)); } }
    public void Locate(int row) { Scroll = Math.Max(0, Math.Min(MaxScroll, (row - 3) * Pitch)); }
    void Select(long uid) {
      if (uid == Sel) { OnSelected++; return; }   // the game: nothing happens
      Sel = uid; Tip = true; TipUid = uid; Locate(RowOf(uid));
    }
    public AutoView View(long t, bool nb) {
      var types = new int[Rows + 1]; for (int i = 1; i <= Rows; i++) types[i] = 1;
      double real = G.ViewTop * H;   // (the row math is stale on purpose: it puts row 38 in the middle of the view, as the recording's shadow did - visible=T)
      var v = new AutoView { NowMs = t, Foreground = true, H = H, Target = Target, Row = TargetRow, Col = 1, Sel = Sel, SelRow = Sel > 0 ? RowOf(Sel) : 0, SelCol = Sel > 0 ? 1 : 0, AnchorHas = true,
        Row1Top = (VT + VB) / 2 - (TargetRow - 1) * Pitch - Cell / 2 + 0 * real, Types = types, AutoConfirm = true, Hero = 5, ScreenHero = 5, Owner = Worn ? 5 : 0, TipKnown = true, TipShown = Tip, TipUid = Tip ? TipUid : 0,
        BlueNone = !Tip, Button = Tip && Sel == Target ? new[] { 500, 520, 150, 52 } : null, ButtonStill = true, ListSig = 7, InputOpen = true,
        LiveGrid = true, LiveFirst = First, LiveLast = Last, LiveViewTop = VT, LiveViewBottom = VB };
      if (Mem) v.LiveScroll = Scroll / Pitch * 100;
      if (Made(TargetRow)) v.LiveCellY = CellY(TargetRow);
      if (Made(TargetRow) && Whole(TargetRow)) { v.LiveTarget = true; v.LiveX = G.ColLeft(1) * H + Cell / 2; v.LiveY = CellY(TargetRow); }
      if (nb) for (int r = First + 1; r <= Last + 1; r++) if (r != TargetRow && Whole(r)) { v.LiveNb = true; v.LiveNbUid = UidOf(r); v.LiveNbX = G.ColLeft(1) * H + Cell / 2; v.LiveNbY = CellY(r); break; }
      return v;
    }
    /// <summary>What the pilot did reaches the game (client px; Cell = the item whose live cell the host clicks).</summary>
    public void Do(AutoAction a, long t) {
      if (a.Kind == AutoKind.Drag) {   // the game swallows the drag threshold at the start; the content follows the pointer by Gain; the release coasts by Inertia of the move, 450 ms later
        Drags++; double eff = a.DY - Math.Sign(a.DY) * Thr;
        At(t + 250, () => { Scroll = Math.Max(0, Math.Min(MaxScroll, Scroll - eff * Gain)); });
        if (Inertia > 0) At(t + 700, () => { Scroll = Math.Max(0, Math.Min(MaxScroll, Scroll - eff * Gain * Inertia)); });
        return;
      }
      if (a.Kind != AutoKind.Click) return;
      if (FirstClickAt < 0) { FirstClickAt = t; FirstClick = a.Cell > 0 ? "cell " + a.Cell : "point " + (int)a.X + "," + (int)a.Y; }
      if (a.X >= 500 && a.X <= 650 && a.Y >= 520 && a.Y <= 572 && Tip && Sel == Target) { Confirms++; At(t + 400, () => { Worn = true; WornAt = t + 400; Sel = 0; Tip = false; TipUid = 0; }); return; }
      if (a.Cell > 0) {   // the host clicks the cell's live rect when it is whole in view
        int row = RowOf(a.Cell);
        if (Made(row) && Whole(row)) { if (a.Cell == Target) OnTarget++; else OnNb++; long u = a.Cell; At(t + 400, () => Select(u)); return; }
        Blind++; return;   // (refused by the host: no click on a layout's point)
      }
      Blind++;   // a click on a point of the pilot's own math
      double yy = a.Y; int hit = (int)Math.Floor((yy - VT + Scroll) / Pitch) + 1; if (hit >= 1 && hit <= Rows) { long u = UidOf(hit); At(t + 400, () => Select(u)); }
    }
  }

  static string AtomListRun(AtomListSim sim, bool wantNb, out AutoPilot p, out long doneAt, out int clicks) {
    p = new AutoPilot(new ListGeometry()); doneAt = -1; clicks = 0;
    for (long t = 0; t < 40000; t += 100) {
      sim.Advance(t);
      var v = sim.View(t, wantNb && p.WantNeighbour);
      var a = p.Step(v);
      if (p.State == "done" && sim.Worn) { doneAt = t; return "done"; }
      if (p.State == "failed" || p.State == "unconfirmed") return p.State + ": " + p.Why;
      if (a.Kind == AutoKind.Click) clicks++;
      sim.Do(a, t);
    }
    return "timeout (state " + p.State + ")";
  }

  // live 582 (130417): the item's server took 3.0 s to put it on the hero; the pilot's fixed wait ended in the same tick and called the step unconfirmed
  static void ConfirmServerTests() {
    Console.WriteLine("Confirm: the server's time (live 582, item 17087)");
    var pl = new AutoPilot(new ListGeometry());
    Func<long, long, AutoView> view = (n, owner) => new AutoView { NowMs = n, Foreground = true, H = 705, Target = 17087, Row = 3, Col = 1, Sel = 17087, SelRow = 3, SelCol = 1, AnchorHas = true, Types = new[] { 0, 1, 1, 1 }, Row1Top = 100, Hero = 229000000, ScreenHero = 229000000, Owner = owner, AutoConfirm = true, Button = new[] { 186, 226, 62, 23 }, ButtonStill = true, TipKnown = true, TipShown = true, InputOpen = true };
    long t = 1000; AutoAction a = null;
    for (int i = 0; i < 20 && !(a != null && a.Kind == AutoKind.Click); i++) { t += 100; a = pl.Step(view(t, 0)); }
    Check(a != null && a.Kind == AutoKind.Click, "confirm: the press goes out");
    long press = t;
    var gone = new AutoView { NowMs = 0, Foreground = true, H = 705, Target = 17087, Row = 3, Col = 1, Sel = 0, AnchorHas = true, Types = new[] { 0, 1, 1, 1 }, Row1Top = 100, Hero = 229000000, ScreenHero = 229000000, Owner = 0, AutoConfirm = true, InputOpen = true };
    for (t = press + 100; t <= press + 3100; t += 100) { gone.NowMs = t; pl.Step(gone); }
    Check(pl.State != "unconfirmed", "confirm: selection gone, owner not yet there at 3.1 s: still waiting for the server, not unconfirmed (" + pl.State + " " + pl.Why + ")");
    gone.NowMs = press + 3200; gone.Owner = 229000000; pl.Step(gone);
    Check(pl.State == "done", "confirm: the owner seen in memory ends it at once (" + pl.State + ")");
    var p2 = new AutoPilot(new ListGeometry()); a = null; t = 1000;
    for (int i = 0; i < 20 && !(a != null && a.Kind == AutoKind.Click); i++) { t += 100; a = p2.Step(view(t, 0)); }
    press = t; gone.Owner = 0;
    for (t = press + 100; t <= press + AutoPilot.ConfirmServerCapMs + 200; t += 100) { gone.NowMs = t; p2.Step(gone); }
    Check(p2.State == "unconfirmed", "confirm: no answer of the server for the cap: unconfirmed with its reason (" + p2.State + ")");
  }

  static void AtomTests() {
    Console.WriteLine("Atoms (the recording 464e21: items 19993)");
    var lines = System.IO.File.ReadAllLines("tests/fixtures/rec-464e21-atoms.txt");
    // ---- the recording itself: what window A and window B show
    var rx = new System.Text.RegularExpressions.Regex(@"^2026-11-04 (\d\d:\d\d:\d\d)  (.*)$");
    int blindA = 0; var calcY = new List<int>(); int row19993 = 0, cells0 = -1, last0 = -1; string bAt = null, bEsc = null;
    foreach (var l in lines) {
      var m = rx.Match(l); if (!m.Success) continue; string tm = m.Groups[1].Value, txt = m.Groups[2].Value;
      if (string.CompareOrdinal(tm, "22:29:54") >= 0 && string.CompareOrdinal(tm, "22:30:16") <= 0 && txt.StartsWith("nav: act EquipNav act:item -> Done")) blindA++;
      var cm = System.Text.RegularExpressions.Regex.Match(txt, @"^uibtn cell: HeroGear 19993 live=none \(cells (\d+), (\d+)\.\.(\d+)\) calc=\[28,(\d+),");
      if (cm.Success && string.CompareOrdinal(tm, "22:29:54") >= 0 && string.CompareOrdinal(tm, "22:30:16") <= 0) { int y = int.Parse(cm.Groups[4].Value); if (calcY.Count == 0 || calcY[calcY.Count - 1] != y) calcY.Add(y); }
      var rm = System.Text.RegularExpressions.Regex.Match(txt, @"^auto: (?:work|idle) item 19993 row (\d+)/1"); if (rm.Success && row19993 == 0) row19993 = int.Parse(rm.Groups[1].Value);
      var c0 = System.Text.RegularExpressions.Regex.Match(txt, @"^uibtn cell: HeroGear 19993 live=none \(cells (\d+), 0\.\.(\d+)\)"); if (c0.Success && cells0 < 0) { cells0 = int.Parse(c0.Groups[1].Value); last0 = int.Parse(c0.Groups[2].Value); }
      if (txt.StartsWith("navprobe CHANGE tip card: select=19993") && bAt == null && string.CompareOrdinal(tm, "22:30:24") >= 0) bAt = tm;
      if (txt.StartsWith("nav: modal Form_CharactorEquipTip how=TipEscape") && bAt != null && bEsc == null && string.CompareOrdinal(tm, bAt) >= 0) bEsc = tm;
    }
    Check(blindA >= 10 && row19993 == 38 && cells0 == 8 && last0 == 7 && calcY.Count >= 5, "atoms recording: window A is " + blindA + " item clicks/drags on the fixed points, the item at row " + row19993 + " with " + cells0 + " cells made (0.." + last0 + "), the row math's y " + string.Join(",", calcY.ToArray()));
    Check(bAt != null && bEsc != null && string.CompareOrdinal(bEsc, bAt) <= 0 || (bAt != null && bEsc != null && TimeSpan.Parse(bEsc).TotalSeconds - TimeSpan.Parse(bAt).TotalSeconds <= 2), "atoms recording: window B is the card of item 19993 opened at " + bAt + " and closed by our Escape at " + bEsc);
    // the gain of one recorded drag (a 430 px pointer move: 577 -> 147) from the row math's y after each: the model's list follows the pointer that much
    double step = 0; int n = 0; for (int i = 1; i + 1 < calcY.Count && n < 3; i++) { if (calcY[i] > calcY[i + 1] && calcY[i] - calcY[i + 1] > 300) { step += calcY[i] - calcY[i + 1]; n++; } }
    double gain = n > 0 ? Math.Min(1.0, step / n / 430.0) : 0.9;

    // ---- A1: window A on the new pilot: no click on any point of its own, one drag per screenful, the item selected at its live cell, the card, «Заменить»
    {
      var sim = new AtomListSim { Gain = gain, TargetRow = row19993 };
      AutoPilot p; long doneAt; int clicks; string r = AtomListRun(sim, true, out p, out doneAt, out clicks);
      Check(r == "done" && sim.Blind == 0 && sim.OnSelected == 0 && sim.Confirms == 1 && sim.Drags >= 4 && sim.Drags <= 16 && doneAt <= 30000 && p.Atoms.Refused == 0,
        "atoms A1 (recording window A): item 19993 at row " + row19993 + " is picked and put on in " + doneAt + " ms with " + sim.Drags + " drags, " + clicks + " clicks, " + sim.Blind + " blind clicks, " + sim.OnSelected + " on the selected item, " + sim.Confirms + " confirm (" + r + ", gain " + Math.Round(gain, 2) + ")");
    }
    // ---- A3: ScrollTo from the game's memory (content position, viewport, row heights): the drags are the computed ones - screenfuls the item is away, one when it is within a screen - the game swallows the drag threshold and the
    // release coasts 12% further, the pilot waits for the list to stand still, no blind click, no drag more than one over the arithmetic
    foreach (int trow in new[] { 8, 20, 38 }) {
      var sim = new AtomListSim { Gain = gain, TargetRow = trow, Inertia = 0.12 };
      AutoPilot p; long doneAt; int clicks; string r = AtomListRun(sim, true, out p, out doneAt, out clicks);
      double span = sim.VB - sim.VT - sim.Cell / 2 - AutoPilot.DragThresholdPx, need = Math.Max(0, Math.Min(sim.MaxScroll, (trow - 1) * sim.Pitch + sim.Cell / 2 - (sim.VB - sim.VT) / 2));
      int bound = (int)Math.Ceiling(need / (span * gain)) + 1;
      Check(r == "done" && sim.Blind == 0 && sim.OnSelected == 0 && sim.Drags <= bound && (trow > 8 || sim.Drags <= 1) && p.Atoms.Refused == 0,
        "atoms A3: item at row " + trow + " by the content position: " + sim.Drags + " drags (at most " + bound + "), " + sim.Blind + " blind clicks, done in " + doneAt + " ms (" + r + ")");
    }
    // the list position unread (no content position): the made cells' way as before, the same result
    {
      var sim = new AtomListSim { Gain = gain, TargetRow = 20, Mem = false };
      AutoPilot p; long doneAt; int clicks; string r = AtomListRun(sim, true, out p, out doneAt, out clicks);
      Check(r == "done" && sim.Blind == 0 && sim.Drags <= 16, "atoms A3: without the content position the made cells still bring the item (" + sim.Drags + " drags, " + r + ")");
    }
    // ---- A1: window B: the item is selected, its card is closed (our Escape, the player): a click on it does nothing - another cell is clicked first, then the item
    {
      var sim = new AtomListSim { Gain = gain, TargetRow = 2, Sel = 19993, Tip = false };
      sim.Scroll = 0;
      AutoPilot p; long doneAt; int clicks; string r = AtomListRun(sim, true, out p, out doneAt, out clicks);
      Check(r == "done" && sim.OnSelected == 0 && sim.OnNb >= 1 && sim.OnTarget == 1 && sim.Confirms == 1 && doneAt <= 15000,
        "atoms A1 (recording window B): the selected item's card is closed: another cell " + sim.OnNb + "x, then the item " + sim.OnTarget + "x, no click on the selected item (" + sim.OnSelected + "), «Заменить» " + sim.Confirms + "x in " + doneAt + " ms (" + r + ")");
    }
    // ---- A1: the item at hand (its live cell in view): one click, the card, «Заменить» - no drag, no other click
    {
      var sim = new AtomListSim { Gain = gain, TargetRow = 2 };
      AutoPilot p; long doneAt; int clicks; string r = AtomListRun(sim, true, out p, out doneAt, out clicks);
      Check(r == "done" && sim.Blind == 0 && sim.Drags == 0 && sim.OnTarget == 1 && sim.OnNb == 0 && doneAt <= 8000, "atoms A1: the item at hand is one click on its live cell (" + sim.Drags + " drags, " + sim.OnTarget + " clicks on it, " + sim.OnNb + " on others, " + doneAt + " ms, " + r + ")");
    }
    // ---- A2: the executor - a repeat without a new diagnosis is refused, a new diagnosis allows it; the log line
    {
      var ar = new AtomRunner(); var f0 = new GameFacts { Sel = 5, ListFirst = 3, ListLast = 11, CellY = 900 };
      bool b1 = ar.Begin("list.drag", "to7up", "viewport[100..600] dy=-400 src=live", "list position changed", ff => ff.PosKey() != f0.PosKey(), 1500, f0, 0);
      Check(b1 && ar.Active && ar.Poll(f0, 1000) == AtomResult.Running && ar.Poll(f0, 1600) == AtomResult.NoEffect && !ar.Active, "atoms A2: an atom runs until its field changes or its time is out (NoEffect)");
      Check(!ar.Begin("list.drag", "to7up", "viewport[100..600] dy=-400 src=live", "list position changed", ff => ff.PosKey() != f0.PosKey(), 1500, f0, 1700) && ar.Refused == 1, "atoms A2: the same atom on the same facts is refused (no repeat without a new diagnosis)");
      var f1 = new GameFacts { Sel = 5, ListFirst = 3, ListLast = 11, CellY = 900, TipUid = 5, TipShown = true };
      Check(ar.Begin("list.drag", "to7up", "viewport[100..600] dy=-400 src=live", "list position changed", ff => ff.PosKey() != f0.PosKey(), 1500, f1, 1800), "atoms A2: a changed fact (a new diagnosis) allows it again");
      var f2 = new GameFacts { Sel = 5, ListFirst = 5, ListLast = 13, CellY = 700 };
      string line = ""; Check(ar.Poll(f2, 2000) == AtomResult.Done && (line = ar.Take()).Contains("list.drag pre=ok target=viewport[100..600] dy=-400 src=live eff=list position changed") && line.Contains("-> Done") && line.Contains("-> NoEffect") && line.Contains("REFUSED"), "atoms A2: the log line names the atom, its target, the awaited field, the time, the result and the facts (" + line + ")");
    }
    // ---- B (navigator): the card of the item the step works on is kept, whatever action holds the keep list; a card of another item is closed as before
    {
      Func<long, int> escapes = tipUid => {
        var io = new FakeNavIo(); var rdr = new ScreenReader(); var nv = new Navigator(ClickOwner.Equip);
        var st = NavState(ScreenKind.HeroScreen, 7, 3, 2, "Form_CharactorEquipTip", new[] { "Form_CharactorEquipTip", "Form_CharactorMain", "Form_MainCity" }); io.FreshState = st;
        nv.SetTarget(new NavTarget { Goal = NavGoal.HeroEquip, Hero = 7, Slot = 2, RecipeId = "equip", Owner = ClickOwner.Equip }, new List<IAtTargetAction> { new KeepOnlyFilterAction() });
        for (int i = 0; i < 80; i++) { io.NowMs += 100; var sn = rdr.Update(st, io.NowMs); sn.WantUid = 19993; sn.TipUid = tipUid; nv.Tick(io, sn); }
        int e = 0; foreach (var a in io.Sent) if (a.Target == ClickTarget.EquipTipEscape) e++;
        if (tipUid == 19993) { bool said = false; foreach (var l in io.Lines) if (l.StartsWith("atom: the card of item 19993")) said = true; if (!said) e += 100; }
        return e;
      };
      Check(escapes(19993) == 0, "atoms B (recording window B): the card of the item the step works on is not closed by our Escape, though the filter action (stale kind) holds a keep list without it (" + escapes(19993) + ")");
      Check(escapes(555) >= 1, "atoms B: the card of another item (a leftover) is still closed by one Escape (" + escapes(555) + ")");
    }
    // ---- «Заменить»: our own hint over its button is moved away at the first refusal, not after 3 s (the recording: eight presses refused in 3.3 s)
    {
      var io = new FakeNavIo { Verdict = GateVerdict.Wait, WaitReason = "own" }; var rdr = new ScreenReader(); var nv = new Navigator(ClickOwner.Equip);
      var st = NavState(ScreenKind.HeroScreen, 7, 3, 2, "", new[] { "Form_CharactorMain", "Form_MainCity" }); io.FreshState = st;
      nv.SetTarget(new NavTarget { Goal = NavGoal.HeroEquip, Hero = 7, Slot = 2, RecipeId = "equip", Owner = ClickOwner.Equip }, new List<IAtTargetAction> { new ConfirmAction() });
      long t0 = io.NowMs; int hidAt = -1;
      for (int i = 0; i < 30 && hidAt < 0; i++) { io.NowMs += 100; nv.Tick(io, rdr.Update(st, io.NowMs)); if (io.Hid > 0) hidAt = (int)(io.NowMs - t0); }
      Check(hidAt >= 0 && hidAt <= 800, "atoms A1: «Заменить» refused for our own window: the windows are hidden at the first refusal (" + hidAt + " ms; was 3000)");
    }
  }

  /// <summary>The gear filter action as the host has it just after the filter was set: the guide's kind still wants it (its keep list has no card), it has nothing to click.</summary>
  sealed class KeepOnlyFilterAction : IAtTargetAction {
    public string Name { get { return "filter"; } }
    public ICollection<string> Keep { get { return NavKeep.GearFilter; } }
    public bool Wants(Snapshot s, NavTarget t) { return true; }
    public long ProgressAt { get { return 0; } }
    public void Reset() { }
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) { return null; }
  }
  sealed class ConfirmAction : IAtTargetAction {
    public string Name { get { return "item"; } }
    public ICollection<string> Keep { get { return NavKeep.ItemPickNoFilter; } }
    public bool Wants(Snapshot s, NavTarget t) { return true; }
    public long ProgressAt { get { return 0; } }
    public void Reset() { }
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) {
      return NavStep.Of(new Act { Owner = t.Owner, Target = ClickTarget.EquipConfirm, Kind = ActKind.Click, X = 541, Y = 592, Base = "Form_CharactorMain", Fire = true, Why = "confirm" }, "act:item");
    }
  }
  // ---------------------------------------------------------------- lab 559 (recording 409391, windows w1..w3): the list clicks and drags of the item pick
  static void Lab559Tests() {
    Console.WriteLine("Lab 559 (item pick: foreign cells, progress budget, slow drag)");
    var geo = new ListGeometry(); double H = 705, cell = geo.CellW * H; double lx = geo.ColLeft(1) * H + cell / 2;
    // R1: item 55070 is made at its live cell, item 54835 stays selected whatever is clicked (the list takes no click: raycast 0x0). A neighbour cell (uid 77) is in view. No click on any other cell than
    // the target's, the list is moved (a drag) at once after the first refused click - not 3 s later, not after a reset
    {
      var pg = new AutoPilot(geo); int foreign = 0, targetClicks = 0, drags = 0; long firstDragAt = -1, firstClickAt = -1;
      for (long t = 0; t < 40000 && pg.State != "failed"; t += 100) {
        var v = new AutoView { NowMs = t, Foreground = true, H = H, Target = 55070, Row = 2, Col = 1, Sel = 54835, SelRow = 0, SelCol = 0, AnchorHas = true, Row1Top = 120, Types = new[] { 0, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, TipKnown = true, TipShown = false,
          Hero = 5, ScreenHero = 5, Owner = 0, InputOpen = true, LiveGrid = true, LiveTarget = true, LiveX = lx, LiveY = 148, LiveFirst = 0, LiveLast = 8, LiveCellY = 148, LiveScroll = 54, LiveViewTop = 107, LiveViewBottom = 615,
          LiveNb = pg.WantNeighbour, LiveNbX = lx, LiveNbY = 300, LiveNbUid = 77, ListSig = 1 };
        var a = pg.Step(v);
        if (a != null && a.Kind == AutoKind.Click) { if (a.Cell == 77) foreign++; else targetClicks++; if (firstClickAt < 0) firstClickAt = t; }
        else if (a != null && a.Kind == AutoKind.Drag) { drags++; if (firstDragAt < 0) firstDragAt = t; }
      }
      Check(foreign == 0 && targetClicks >= 1 && drags >= 1 && firstDragAt - firstClickAt <= 3000 && pg.Atoms.Refused <= 6,
        "lab 559 R1: sel 54835 stays, the cell of 55070 is made: " + foreign + " clicks on foreign cells, " + targetClicks + " on the target, first drag " + (firstDragAt - firstClickAt) + " ms after the first click, " + drags + " drags, refused " + pg.Atoms.Refused + " (" + pg.State + ")");
    }
    // R2: the norm runs from the last progress. The content position changes every 3 s for 15 s (a legal scroll): no item_limit; then it stands still: the reset comes about 6 s after the last change
    {
      var io = new FakeNavIo(); var pl = new AutoPilot(new ListGeometry());
      long t00 = io.NowMs;
      var ia = new ItemPickAction(pl, (s0, t1, n) => new AutoView { NowMs = n, Foreground = true, H = 705, Target = 5, Row = 3, Col = 1, Sel = 0, AnchorHas = true, Types = new[] { 0, 1, 1, 1 }, Row1Top = 100, Hero = 5, ScreenHero = 5, Owner = 0, InputOpen = false,
          LiveGrid = true, LiveFirst = 0, LiveLast = 8, LiveScroll = 50 + 100 * Math.Min(5, (n - t00) / 3000) }, (v, a) => ClickTarget.EquipNav);
      var t0 = new NavTarget { Goal = NavGoal.HeroEquip, Hero = 5, Slot = 2 }; var rd = new ScreenReader(); var st = NavState(ScreenKind.HeroScreen, 5, 3, 2, "", new[] { "Form_CharactorMain", "Form_MainCity" }); io.FreshState = st;
      string reset = ""; long first = io.NowMs;
      for (int i = 0; i < 300 && reset == ""; i++) { io.NowMs += 200; var sn = rd.Update(st, io.NowMs); var ns = ia.Step(sn, t0, io, io.NowMs); if (ns != null && ns.Kind == StepKind.Reset) reset = ns.Why; }
      long ms = io.NowMs - first;
      Check(reset.StartsWith("item_limit ") && ms >= 20000 && ms <= 24500, "lab 559 R2: the content position moved every 3 s for 15 s: no reset from the step's start (6 s), one 6 s after the last progress (" + reset + ", after " + ms + " ms)");
    }
    // R3: a drag the host needs 1.4 s for (a loaded PC): the position is read after the drag atom ended, no second drag under the first, no «the list does not move» while it is moving
    {
      var pg = new AutoPilot(geo); var types = new int[41]; for (int i = 1; i <= 40; i++) types[i] = 1;
      int drags = 0; long d1 = -1, d2 = -1; double scroll = 54; long moveAt = -1;
      for (long t = 0; t < 12000 && pg.State != "failed" && d2 < 0; t += 100) {
        if (moveAt >= 0 && t >= moveAt) { scroll = 900; }
        var v = new AutoView { NowMs = t, Foreground = true, H = H, Target = 55070, Row = 21, Col = 3, Sel = 54835, AnchorHas = true, Row1Top = 120, Types = types, TipKnown = true, TipShown = false, Hero = 5, ScreenHero = 5, Owner = 0, InputOpen = true,
          LiveGrid = true, LiveTarget = false, LiveFirst = scroll > 54 ? 8 : 0, LiveLast = scroll > 54 ? 16 : 7, LiveScroll = scroll, LiveViewTop = 107, LiveViewBottom = 615, ListSig = 1 };
        var a = pg.Step(v);
        if (a != null && a.Kind == AutoKind.Drag) { drags++; if (d1 < 0) { d1 = t; moveAt = t + 1400; } else if (d2 < 0) d2 = t; }
      }
      Check(d1 >= 0 && pg.State != "failed" && (d2 < 0 || d2 - d1 >= 1400), "lab 559 R3: the host's drag takes 1.4 s: the second drag starts " + (d2 < 0 ? "never" : (d2 - d1) + " ms") + " after the first, state " + pg.State + " (" + pg.Why + ")");
    }
  }
}
