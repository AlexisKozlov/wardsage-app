// The navigator core in the game sim (--nav): SimNavIo gives src/Navigator.cs the sim's reads and sends (SimHost's gated Send is the only glue left),
// the fault injectors of docs/specs/navigator.md 6, and the named acceptance scenarios nav.*. Run: tests/run-sim.sh --nav [seeds] [--graph].
using System;
using System.Collections.Generic;
using System.Text;
using RealmForge;

static partial class CoreTests {
  /// <summary>The injectors (their own Random streams: the old seeds keep their runs).</summary>
  sealed class NavCfg { public double FlickerP, FlagFlickerP, LatePopupP; public int CellLatencyMs; public long LockMs, RebuildMs; public bool OwnCover; }
  static bool SimNav, SimNavCity, SimNavChecks; static NavCfg SimNavCfg = new NavCfg(); static long SimNavPlan;

  sealed partial class SimHost {
    public bool Nav, NavCity; public NavCfg Cfg = new NavCfg(); public bool InNavSend; public int NavSends;
    Navigator nav; readonly ScreenReader reader = new ScreenReader(); SimNavIo nio; List<IAtTargetAction> navActs; Random navR; int flickerLeft;
    readonly List<KeyValuePair<long, long[]>> latHist = new List<KeyValuePair<long, long[]>>();
    // --- the global checks of docs/specs/2026-11-04-core-any-scenario.md 4 (every --nav run): 1 no 8 s without a state change or a reset / halt; 2 no click at one point 3 times with no state change (grid picks and drags excepted);
    // 3 after every reset every action's State is idle; 4 the end is done or a named halt within 120 s; 5 halts only from the allowed set
    // (the navigator's own step stall, Navigator.StepStallMs, counts from the snapshot that stood still after the game's last change: NavSlackMs is that lag, the stretch the harness flags is the stall plus it)
    static readonly long NavStallMs = Environment.GetEnvironmentVariable("NAV_STALL_MS") != null ? long.Parse(Environment.GetEnvironmentVariable("NAV_STALL_MS")) : Navigator.StepStallMs, NavSlackMs = 1200;
    public readonly List<string> NavErrs = new List<string>();
    string chkFp = "", lastClickFp = ""; long chkAt, chkProg, chkBlind; int chkRec, sameN, lastRec; double lastX = -999, lastY = -999; bool stallSaid;
    string NavFp() {
      var g = G;
      return string.Join(">", g.Stack.ToArray()) + "|" + g.Hero.Shown + "/" + g.Hero.Tab + "/" + g.Hero.Part + "/" + g.Hero.Grid + "/" + g.Hero.FilterOpen + "/" + (int)g.Hero.Scroll + "|" + g.GearFilter + g.FilterOwn + g.HeroFilter + g.HideWorn + g.ItemSel + g.Worn + "|" + g.ListGen + "/" + (int)g.Row1Top;
    }
    public void NavCheck(long t) {
      if (nav == null) return;
      string fp = NavFp();
      if (nav.Recoveries != lastRec) {   // check 3 (a reset ran in this tick: no action may keep an old state)
        lastRec = nav.Recoveries;
        if (hp.State != "idle" || fp0State() != "idle" || ip.State != "idle") NavErrs.Add("after a reset an action's state is not idle: hero " + hp.State + ", filter " + fp0State() + ", item " + ip.State + " (t=" + t + ")");
      }
      bool gameWaits = false; foreach (var f in G.Stack) if (ScreenMap.ClassOf(f) == 'W') gameWaits = true;   // (a loading / result screen the game closes by itself is the game's wait, not a stall)
      if (gameWaits || fp != chkFp || nav.ProgressAt != chkProg || nav.BlindMs != chkBlind || nav.Recoveries != chkRec || nav.State == "halted" || nav.State == "done") { chkFp = fp; chkProg = nav.ProgressAt; chkBlind = nav.BlindMs; chkRec = nav.Recoveries; chkAt = t; stallSaid = false; }
      else if (t - chkAt > NavStallMs + NavSlackMs && !stallSaid) { stallSaid = true; NavErrs.Add("no state change, reset or halt for " + (t - chkAt) / 1000 + " s (nav " + nav.Say + ", t=" + t + ")"); }
    }
    string fp0State() { return this.fp.State; }
    void NavSameCheck(Act a) {
      string f = NavFp();
      if (a.Kind != ActKind.Click || (a.Edge ?? "").StartsWith("seek:")) { sameN = 0; lastX = lastY = -999; return; }
      if (Math.Abs(a.X - lastX) <= 2 && Math.Abs(a.Y - lastY) <= 2 && f == lastClickFp) { if (++sameN >= 3) NavErrs.Add("click at " + (int)a.X + "," + (int)a.Y + " " + sameN + " times with no state change (" + a.Edge + ")"); }
      else sameN = 1;
      lastX = a.X; lastY = a.Y; lastClickFp = f;
    }
    public List<string> NavEndChecks(long t, string outcome) {
      var l = new List<string>(NavErrs);
      bool named = outcome == "done" || outcome.StartsWith("navhalt:") || NavRefusal(outcome);
      if (!named) l.Add("the run ends in no done / named halt: " + outcome);
      else if (t > 120000) l.Add("the run took " + t / 1000 + " s of sim time (more than 120 s)");
      if (outcome.StartsWith("navhalt:")) {
        string w = outcome.Substring(8);
        if (!(w == "hero_not_in_grid" || w == "danger" || w == "reset_failed" || w == "phantom_layer" || w.StartsWith("gate_") || w == "unknown_popup" || w == "unknown_screen")) l.Add("a halt outside the allowed set: " + w);
      }
      return l;
    }
    public string NavHalt { get { return nav != null && nav.State == "halted" ? nav.HaltWhy : ""; } }

    /// <summary>The injectors that do not depend on the navigator (late pop-ups, late cells): also on the old code, so the two are compared in the same world.</summary>
    public void SetInjectors(NavCfg cfg) { Cfg = cfg; navR = new Random((int)(plan * 104729 + (G.Slow ? 1 : 0))); }
    public void EnableNav(NavCfg cfg, bool city) {
      Nav = true; NavCity = city; Cfg = cfg;
      navR = new Random((int)(plan * 104729 + (G.Slow ? 1 : 0)));
      nav = new Navigator(Owner); nio = new SimNavIo(this);
    }

    /// <summary>--nav lateCells: the hero screen's fields answer CellLatencyMs late (a list cell read, a hero change).</summary>
    void NavLatency(ScreenInputs inp) {
      if (inp.Hero == null) return;
      latHist.Add(new KeyValuePair<long, long[]>(Now, new[] { inp.Hero.Hero, inp.Hero.Part, inp.Hero.Tab }));
      long want = Now - Cfg.CellLatencyMs; long[] use = null;
      foreach (var kv in latHist) { if (kv.Key <= want) use = kv.Value; else break; }
      while (latHist.Count > 1 && latHist[1].Key <= want) latHist.RemoveAt(0);
      if (use != null) { inp.Hero.Hero = use[0]; inp.Hero.Part = (int)use[1]; inp.Hero.Tab = (int)use[2]; }
    }

    /// <summary>After a send that went out (SimHost.Send): a pop-up the game opens while the click's effect is awaited.</summary>
    void LatePopup() { if (!G.ItemPhase && Cfg.LatePopupP > 0 && navR != null && navR.NextDouble() < Cfg.LatePopupP) { string f = SimPick(navR, 'P'); if (f != null) G.PushLater(100 + navR.Next(500), f); } }
    ScreenState NavUnknownState() { return ScreenState.Classify(new ScreenInputs { Offers = new List<string>() }, Now); }
    ScreenState Flicker(ScreenState scr) {
      if (Cfg.FlickerP <= 0) return scr;
      if (flickerLeft > 0) { flickerLeft--; return NavUnknownState(); }
      if (navR.NextDouble() < Cfg.FlickerP) { flickerLeft = navR.Next(2); return NavUnknownState(); }
      return scr;
    }

    HeroView NavHeroView(long n) {
      var hv = new HeroView {
        NowMs = n, Foreground = true, W = W, H = U, ClientH = H, Plan = plan, Hero = heroScreen.FormOk ? heroScreen.Hero : 0, Tab = heroScreen.Tab, Part = heroScreen.Part,
        SmallCards = heroScreen.SmallCards, Heroes = heroScreen.Heroes, HeroesButton = false, FilterOpen = (G.HeroFilter && G.Full == SimHero) || G.HeroFilterSeen,
        ClassChosen = heroScreen.ClassChosen, FactionList = heroScreen.FactionList, ChosenCamps = heroScreen.ChosenCamps, ClassCount = heroScreen.ClassCount, GridPtr = heroScreen.GridPtr,
        InputOpen = n >= G.LockUntil
      };
      if (G.HeroModel) { var tg = SimGame.SimTags(plan); hv.HeroClass = tg[0]; hv.HeroCamps = new[] { tg[1] }; }
      if (!heroScreen.FormOk) hv.Hero = 0;
      return hv;
    }

    AutoView NavItemView(long n) {
      var scr = Screens.Current;
      G.ItemPhase = true;
      bool tip = G.Stack.Contains(SimTip) && G.Full == SimHero;
      var v = new AutoView {
        NowMs = n, Foreground = true, H = U, Target = SimItem, Row = 2, Col = 1, Sel = G.ItemSel ? SimItem : 0, SelRow = G.ItemSel ? 2 : 0, SelCol = G.ItemSel ? 1 : 0,
        AnchorHas = true, Row1Top = G.Row1Top, Types = SimGame.ItemTypes, AutoConfirm = true, Button = tip ? SimGame.Hint.Replace(W, H) : null,
        Hero = plan, ScreenHero = heroScreen.FormOk ? heroScreen.Hero : 0, Owner = G.Worn ? plan : 0, InputOpen = n >= G.LockUntil, ListSig = G.ListGen * 31L + 7
      };
      v.TipKnown = !formsUnread && scr != null && !scr.Held && scr.Kind == ScreenKind.HeroScreen && scr.OfferRead && formsKind == ScreenKind.HeroScreen;
      v.TipShown = scr != null && ((scr.Flow ?? "") + "," + (scr.ModalWhy ?? "")).Contains(SimTip);
      v.BlueNone = !tip;
      return v;
    }

    readonly FilterPilot fp = new FilterPilot(new FilterGeometry());
    FilterView NavFilterView(long n) {
      bool hidden = G.ItemElsewhere && G.HideWorn;
      return new FilterView { NowMs = n, Foreground = true, W = W, H = H, Item = SimItem, Suits = new long[0], MainAttrs = new long[0], SubAttrs = new long[0], Wanted = hidden,
        PanelOpen = (G.FilterOwn || G.GearFilter) && G.Full == SimHero, HideEquipped = G.HideWorn ^ (Cfg.FlagFlickerP > 0 && navR != null && navR.NextDouble() < Cfg.FlagFlickerP), OnOtherHero = G.ItemElsewhere, ItemListed = !hidden };
    }

    void EnsureNavActions() {
      if (navActs != null) return;
      var hs = new HeroSelectAction(hp, (s, t, n) => NavHeroView(n));
      var gf = new GearFilterAction(fp, (s, t, n) => NavFilterView(n), () => fp.State == "work" || FilterPilot.WantedFor(G.ItemElsewhere && G.HideWorn ? "hiddenEq" : "pick", true));
      var ia = new ItemPickAction(ip, (s, t, n) => NavItemView(n), (v, a) => {
        var b = v.Button; bool onConfirm = b != null && a.X >= b[0] && a.X <= b[0] + b[2] && a.Y >= b[1] && a.Y <= b[1] + b[3];
        return onConfirm ? ClickTarget.EquipConfirm : ClickTarget.EquipNav;
      }, gf);
      ia.OnStep = (v, a) => {
        if (ip.Trace != "" && Environment.GetEnvironmentVariable("SIM_TRACE") != null) Console.WriteLine("  item " + Now + ": " + ip.Trace);
        ip.Trace = "";
        if (ip.State == "done") Done = "done";
      };
      ia.OnSent = a => {
        var b = G.Stack.Contains(SimTip) && G.Full == SimHero ? SimGame.Hint.Replace(W, H) : null;
        if (b != null && a.X >= b[0] && a.X <= b[0] + b[2] && a.Y >= b[1] && a.Y <= b[1] + b[3]) Confirms++;
      };
      gf.OnStep = (fv, fa) => { if (Environment.GetEnvironmentVariable("SIM_TRACE") != null) Console.WriteLine("  filter " + Now + ": " + fp.State + " " + fp.Note + " panel=" + fv.PanelOpen + " hide=" + fv.HideEquipped + " -> " + (fa == null ? "null" : fa.Kind + "@" + (int)fa.X + "," + (int)fa.Y)); };
      navActs = new List<IAtTargetAction> { hs, gf, ia };
    }

    void TickEquipNav() {
      EnsureNavActions(); nio.BeginTick();
      var snap = reader.Update(Flicker(Screens.Current), Now, nio.Picture);
      nav.SetRecipe(NavRecipes.Get("equip"), new Dictionary<string, long> { { "hero", plan }, { "slot", 2 } }, navActs);
      nav.Tick(nio, snap);
      PilotState = nav.State == "halted" ? "nav_halt" : nav.State == "at_target" ? "item_" + ip.State : "nav_" + nav.Say;
    }

    void TickCityNav() {
      if (navActs == null) navActs = new List<IAtTargetAction>();
      nio.BeginTick();
      var snap = reader.Update(Flicker(Screens.Current), Now, nio.Picture);
      nav.SetRecipe(NavRecipes.Get("city"), new Dictionary<string, long>(), navActs);
      nav.Tick(nio, snap);
      PilotState = "nav_" + nav.State;
      if (nav.State == "done") Done = "done";
    }

    /// <summary>--nav ownHintOver: our own hint covers the looks (from OwnCoverFrom on, for ever) - every send waits on it ("own") until the navigator hides our windows (INavIo.HideOwn), which frees them for 20 s.</summary>
    long ownHiddenUntil, ownCoverFrom = -1; public int OwnHides;
    GateResult NavSend(Act a, ScreenState fresh) {
      if (Cfg.OwnCover) {
        if (ownCoverFrom < 0) ownCoverFrom = Now + navR.Next(3000);
        if (Now >= ownCoverFrom && Now >= ownHiddenUntil) { G.Note("send refused: " + a.Target + " - a look is covered by our hint"); return GateResult.Wait("own", "a look is covered by our own hint"); }
      }
      InNavSend = true; lastGate = null; bool ok;
      try {
        if (a.Kind == ActKind.Escape) ok = EscapeSend(a.Target);
        else {
          var aa = new AutoAction { Kind = a.Kind == ActKind.Drag ? AutoKind.Drag : AutoKind.Click, X = a.X, Y = a.Y, DY = a.DY, Cell = a.Cell };
          ok = Send(a.Target, aa, a.Fill == null ? (Action<ClickRequest, ScreenState>)null : (rq, fr) => a.Fill(rq), a.WantCity, a.WantKnown);
        }
      } finally { InNavSend = false; }
      if (!ok) { G.Note("send refused: " + a.Target + " " + (lastGate != null ? lastGate.Verdict + " " + lastGate.Detail : "-")); return lastGate != null && lastGate.Verdict != GateVerdict.Allow ? lastGate : GateResult.Wait("unsent", "not sent"); }
      NavSends++; NavSameCheck(a);
      return GateResult.Allow("sent");
    }

    sealed class SimNavIo : INavIo {
      readonly SimHost h; bool flip;
      public SimNavIo(SimHost h) { this.h = h; }
      public void BeginTick() { flip = h.Cfg.FlagFlickerP > 0 && h.navR.NextDouble() < h.Cfg.FlagFlickerP; }
      public long Now { get { return h.Now; } }
      public bool Front { get { return true; } }
      public bool UserBusy { get { return false; } }
      public double W { get { return h.G.W; } }
      public double H { get { return h.G.H; } }
      public double U { get { return h.G.U; } }
      public bool Slow { get { return h.G.Slow; } }
      public bool InputOpen { get { return h.Now >= h.G.LockUntil; } }
      public GameGeometry Geo { get { return h.geo; } }
      public ClickGate Gate { get { return h.Gate; } }
      public ScreenState Fresh(bool wantCity, bool wantKnown) { return h.Fresh(wantCity, wantKnown); }
      public double[] LiveRect(string form, string field) { return null; }
      public double[] LiveFor(ClickTarget t, string form) { return t == ClickTarget.OfferClose ? h.G.OfferLiveRect(form) : null; }
      public bool Picture(string what) {
        bool v = false;
        switch (what) {
          case "heroesButton": v = h.G.Full == SimCity && h.G.CityClear; break;
          case "heroFilter": v = (h.G.HeroFilter && h.G.Full == SimHero) || h.G.HeroFilterSeen; break;
          case "gearFilterMain": v = ((h.G.GearFilter || h.G.FilterOwn) && h.G.Full == SimHero) || (h.G.PhantomGear && h.G.Full == SimHero && h.G.Hero.Part < 0); break;
          case "gearFilterSide": v = false; break;
          case "tipShown": v = h.G.Stack.Contains(SimTip); break;
        }
        return what == "heroesButton" ? v : v ^ flip;
      }
      public GateResult Send(Act a, ScreenState fresh) { return h.NavSend(a, fresh); }
      public void HideOwn() { h.ownHiddenUntil = h.Now + 20000; h.OwnHides++; h.G.Note("hide own windows"); }
      public void Log(string line) { h.G.Note(line); }
      public void Problem(string kind, string message) { h.G.Note("problem " + kind + ": " + message); }
    }
  }

  // ---------------------------------------------------------------- the acceptance scenarios (nav.*)
  static readonly string[] NavScenarios = {
    "nav.equip.fromAny", "nav.equip.offerChain", "nav.equip.unknownPopup", "nav.equip.tipLeftover", "nav.equip.filterLeftover", "nav.equip.heroFilterOpen",
    "nav.equip.hideWornForeign", "nav.equip.hideWorn", "nav.equip.foreignInvTab", "nav.equip.overFoundry", "nav.equip.bulkSale", "nav.equip.flicker", "nav.equip.lateCells200", "nav.equip.popupDuringEdge", "nav.equip.inputLock", "nav.city.fromAny",
    "nav.equip.heroFactionHides", "nav.equip.heroFilterBlind", "nav.equip.phantomRedX", "nav.equip.resetMidHero", "nav.equip.heroNotOwned", "nav.equip.ownHintOver" };

  static readonly string[] NavStartNames = { "City", "Hero (other hero)", "Hero (gear list)", "Inventory", "Inventory (other tab)", "Inventory (bulk)", "Foundry identify", "Foundry ascend",
    "Foundry recast", "Gem", "Artifact workshop", "Guild", "Arena", "Shop", "Popup over City", "Plan" };

  /// <summary>The start of a scenario: the forms and flags of the game before the run.</summary>
  static void NavStart(string sc, SimGame g, Random r, long plan) {
    long other = 100 + (plan - 100 + 1 + r.Next(28)) % 30;
    switch (sc) {
      case "nav.equip.offerChain": {
        int n = 1 + r.Next(3);
        for (int i = 0; i < n; i++) { string on; int tries = 0; do { on = OfferForms.All[r.Next(OfferForms.All.Length)].Name; } while (!OfferForms.Closable(Geo.Current ?? g.Geo, on) && ++tries < 30); g.Push(on); }
        break;
      }
      case "nav.equip.unknownPopup":
        if (r.Next(2) == 0) g.Push("Form_SimNobodyKnowsThis"); else { SimStartAt(g, "City", 0); g.Push(SimPick(r, 'P')); g.Push("Form_SimNobodyKnowsThis"); }
        break;
      case "nav.equip.tipLeftover":
        g.Hero.Shown = r.Next(3) == 0 ? other : plan; g.Hero.Tab = 3; g.Hero.Part = r.Next(5); g.Push(SimHero); g.Push(SimTip); break;
      case "nav.equip.filterLeftover":
        g.Hero.Shown = r.Next(2) == 0 ? other : plan; g.Hero.Tab = 3; g.Hero.Part = r.Next(5); g.Push(SimHero); g.GearFilter = true; break;
      case "nav.equip.heroFilterOpen":
        g.Hero.Shown = other; g.Hero.Tab = 1 + 2 * r.Next(2); g.Hero.Part = -1; g.Push(SimHero); g.HeroFilter = true; break;
      case "nav.equip.hideWorn": case "nav.equip.hideWornForeign": {   // «Скрыть надетое» on and the plan's item worn by another hero: the filter pilot has to switch it off before the item can be picked
        g.HideWorn = true; g.ItemElsewhere = true;
        if (r.Next(3) != 0) { g.Hero.Shown = r.Next(2) == 0 ? plan : other; g.Hero.Tab = 3; g.Hero.Part = r.Next(3) == 0 ? -1 : 2; g.Push(SimHero); }
        break;
      }
      case "nav.equip.heroFactionHides": case "nav.equip.heroFilterBlind": case "nav.equip.resetMidHero": case "nav.equip.heroNotOwned": {   // the hero grid's filter model (GameSim.cs EnableHeroModel)
        bool notOwned = sc == "nav.equip.heroNotOwned";
        g.EnableHeroModel(plan, !notOwned); g.FilterFadeMs = 300 + r.Next(500);
        g.Hero.Shown = g.Hero.CityHero = other; g.Hero.Tab = 1 + 2 * r.Next(2); g.Hero.Part = -1; g.Push(SimHero);
        var pt = SimGame.SimTags(plan);
        if (sc == "nav.equip.resetMidHero") g.HeroDead = true;
        else if (!notOwned || r.Next(2) == 0) {   // a faction (or class) that is not the plan hero's
          int k = r.Next(3);   // 0: a class, 1: a faction, 2: both
          long fac = 101 + (pt[1] - 101 + 1 + r.Next(5)) % 6, cls = HeroGeometry.ClassOrder[(Array.IndexOf(HeroGeometry.ClassOrder, pt[0]) + 1 + r.Next(5)) % 6];
          g.HeroFilterBy(k != 0 ? fac : 0, k != 1 ? cls : 0);
        }
        if (sc == "nav.equip.heroFilterBlind") g.Hero.Blind = true;
        break;
      }
      case "nav.equip.phantomRedX":
        g.PhantomGear = true; g.Hero.Shown = other; g.Hero.Tab = 1 + r.Next(2); g.Hero.Part = -1; g.Push(SimHero); break;
      case "nav.equip.foreignInvTab": SimStartAt(g, "Inventory (other tab)", 0); break;
      case "nav.equip.overFoundry": SimStartAt(g, r.Next(2) == 0 ? "Foundry ascend" : "Foundry recast", 0); break;
      case "nav.equip.bulkSale": SimStartAt(g, "Inventory (bulk)", 0); break;
      default: {   // from any screen: the named starts, now and then with a leftover and forms over it
        SimStartAt(g, NavStartNames[r.Next(NavStartNames.Length)], other);
        if (g.Full == SimHero && r.Next(3) == 0) { int lk = r.Next(3); if (lk == 0) g.Push(SimTip); else if (lk == 1) g.GearFilter = true; else { g.HeroFilter = true; g.Hero.Part = -1; } }
        // (the realistic mix of --real: a form over the start one run in six, a dangerous one 3 in 100 of those)
        int extra = r.Next(3); extra = extra == 0 && r.Next(2) == 0 ? 1 : 0;
        for (int i = 0; i < extra; i++) { int p = r.Next(100); char c = p < 45 ? 'P' : p < 65 ? 'E' : p < 68 ? 'D' : p < 75 ? 'W' : 'O'; string f = SimPick(r, c); if (f != null) g.Push(f); }
        break;
      }
    }
  }

  /// <summary>A scenario whose only right end is this one (a hero the player does not own: a named stop).</summary>
  static string NavExpect(string sc) { return sc == "nav.equip.heroNotOwned" ? "navhalt:hero_not_in_grid" : null; }

  static NavCfg NavCfgOf(string sc) {
    var c = new NavCfg();
    if (sc == "nav.equip.flicker") { c.FlickerP = 0.1; c.FlagFlickerP = 0.05; }
    else if (sc == "nav.equip.lateCells200") c.CellLatencyMs = 200;
    else if (sc == "nav.equip.popupDuringEdge") c.LatePopupP = 0.15;
    else if (sc == "nav.equip.inputLock") c.LockMs = 350;
    else if (sc == "nav.equip.ownHintOver") c.OwnCover = true;
    else if (sc == "nav.equip.hideWorn") { c.FlagFlickerP = 0.1; c.RebuildMs = 200; }
    return c;
  }

  sealed class NavRes { public string Outcome = ""; public bool Bad, Hang; public string Err = ""; public long Ms; public string Hash = ""; }

  static NavRes NavRunOne(string sc, int seed, bool slow, bool nav, GameGeometry geo, bool verbose) {
    var r = new Random(seed * 7919 + 13);
    double failP = r.Next(3) == 0 ? 0.0 : r.NextDouble() * 0.15;
    var g = new SimGame(seed, slow, failP); g.Geo = geo; g.SpawnP = 0.01;
    long plan = 100 + seed % 30;
    g.LockMs = NavCfgOf(sc).LockMs; g.ListRebuildMs = NavCfgOf(sc).RebuildMs;
    NavStart(sc, g, r, plan);
    SimNav = nav; SimNavCity = sc.StartsWith("nav.city"); SimNavCfg = NavCfgOf(sc); SimNavPlan = plan;
    var oc = new string[1]; var tmp = new Dictionary<string, int>();
    string e;
    SimNavChecks = nav;
    try { e = SimDrive(g, PilotFlow.Equip, slow, failP, r, seed, verbose, tmp, geo, null, false, null, oc); }
    finally { SimNavChecks = false; SimNav = false; SimNavCity = false; SimNavPlan = 0; SimNavCfg = new NavCfg(); }
    string o0 = oc[0] ?? ""; int pi = o0.IndexOf(" ("); if (pi > 0 && !verbose) o0 = o0.Substring(0, pi);
    var res = new NavRes { Outcome = o0 };
    res.Bad = e.Contains("\n    !! ") && !e.Contains("no end within");
    res.Hang = e.Contains("no end within");
    res.Err = e;
    { ulong hsh = 14695981039346656037UL; foreach (char ch in g.Acts.ToString()) { hsh ^= ch; hsh *= 1099511628211UL; } res.Hash = hsh.ToString("x16"); }
    return res;
  }

  /// <summary>Runs that end because a rule of the gate refused (a dangerous form, an Escape on the pilot's own screen, a pop-up with no known cross, a form nobody knows) or the unchanged item pilot gave its own verdict (item_failed / item_unconfirmed: the list work is not the navigator's): the correct stop, named; the sim's
  /// done-rate gate is taken over the other runs (the lab's metric counts them as failures and names them).</summary>
  static bool NavRefusal(string o) {
    return o.StartsWith("navhalt:danger") || o.StartsWith("navhalt:gate_dangerous") || o.StartsWith("navhalt:gate_offer") || o.StartsWith("navhalt:gate_unknown") || o.StartsWith("navhalt:gate_target")
        || o.StartsWith("navhalt:unknown_popup") || o.StartsWith("navhalt:unknown_screen") || o.StartsWith("wait:item_failed") || o.StartsWith("wait:item_unconfirmed") || o.StartsWith("halt:dangerous") || o.StartsWith("halt:offer") || o.StartsWith("halt:target");
  }

  static int NavSimMain(GameGeometry geo, int seeds, int from, bool verbose) {
    int per = Math.Max(20, seeds / (NavScenarios.Length * 2));
    Console.WriteLine("nav sim: " + NavScenarios.Length + " scenarios x fast/slow x " + per + " seeds from " + from + (SimGraph ? " (graph-driven)" : ""));
    string only = Environment.GetEnvironmentVariable("NAV_SC");
    if (only != null) {   // one scenario, one seed: its whole trace (NAV_SC=nav.equip.unknownPopup tests/run-sim.sh --nav --seed 3 [--slow-only])
      var rr = NavRunOne(only, from, Environment.GetEnvironmentVariable("NAV_SLOW") != null, Environment.GetEnvironmentVariable("NAV_OLD") == null, geo, true);
      Console.WriteLine(rr.Outcome + "\n" + rr.Err); return 0;
    }
    int failures = 0; var sw = System.Diagnostics.Stopwatch.StartNew(); int shown = 0;
    foreach (var sc in NavScenarios) {
      foreach (bool slow in new[] { false, true }) {
        int doneNav = 0, doneOld = 0, hang = 0, bad = 0, halts = 0, refNav = 0, refOld = 0, pairN = 0, pairNav = 0, pairOld = 0, expectMiss = 0; var why = new SortedDictionary<string, int>();
        for (int s = from; s < from + per; s++) {
          var a = NavRunOne(sc, s, slow, true, geo, verbose);
          if (Environment.GetEnvironmentVariable("NAV_DIGEST") != null) Console.WriteLine("digest " + sc + (slow ? " slow " : " fast ") + s + " " + a.Hash + " " + a.Outcome);
          var b = sc.StartsWith("nav.city") || NavExpect(sc) != null ? null : NavRunOne(sc, s, slow, false, geo, false);
          if (NavExpect(sc) != null && !a.Outcome.StartsWith(NavExpect(sc)) && !NavRefusal(a.Outcome)) expectMiss++;
          if (a.Outcome.StartsWith("done")) doneNav++;
          else if (a.Outcome.StartsWith("navhalt") || a.Outcome.StartsWith("halt:")) { halts++; int c; why.TryGetValue(a.Outcome, out c); why[a.Outcome] = c + 1; }
          else { int c; why.TryGetValue(a.Outcome, out c); why[a.Outcome] = c + 1; }
          if (b != null && b.Outcome.StartsWith("done")) doneOld++;
          if (Environment.GetEnvironmentVariable("NAV_WORSE") != null && b != null && b.Outcome.StartsWith("done") && !a.Outcome.StartsWith("done")) Console.WriteLine("worse " + sc + (slow ? " slow " : " fast ") + s + " nav=" + a.Outcome + " old=done");
          if (NavRefusal(a.Outcome)) refNav++; if (b != null && NavRefusal(b.Outcome)) refOld++;
          if (b != null && !NavRefusal(a.Outcome) && !NavRefusal(b.Outcome)) { pairN++; if (a.Outcome.StartsWith("done")) pairNav++; if (b.Outcome.StartsWith("done")) pairOld++; }   // (the seeds neither path was stopped by a rule on: the paths compared on the same world)
          if (Environment.GetEnvironmentVariable("NAV_LIST") != null && !a.Outcome.StartsWith("done")) Console.WriteLine("    " + sc + (slow ? " slow" : " fast") + " seed " + s + ": " + a.Outcome);
          if (a.Hang) hang++;
          if (a.Bad) { bad++; if (shown++ < 6) Console.WriteLine("  !! " + sc + (slow ? " slow " : " fast ") + "seed " + s + ": " + a.Err.Replace("\n", "\n  ")); }
        }
        double rate = doneNav * 100.0 / Math.Max(1, per - refNav), need = slow ? 97 : 99;
        bool pass = bad == 0 && hang == 0 && (NavExpect(sc) != null ? expectMiss == 0 : rate >= need && (sc.StartsWith("nav.city") || pairNav >= pairOld));
        if (!pass) failures++;
        var sb = new StringBuilder();
        foreach (var kv in why) sb.Append(" ").Append(kv.Key).Append("=").Append(kv.Value);
        Console.WriteLine((pass ? "  PASS " : "  FAIL ") + sc.PadRight(28) + (slow ? "slow" : "fast") + " done " + doneNav + "/" + per + " (" + rate.ToString("0.0") + " % of the " + (per - refNav) + " not refused by a rule)"
          + (NavExpect(sc) != null ? ", expected " + NavExpect(sc) + " missed " + expectMiss : sc.StartsWith("nav.city") ? "" : ", old code " + doneOld + "/" + per + ", paired (neither refused) " + pairNav + "/" + pairN + " vs old " + pairOld + "/" + pairN) + ", rule breaks " + bad + ", hangs " + hang + (sb.Length > 0 ? " |" + sb : ""));
      }
    }
    Console.WriteLine("nav sim: " + failures + " scenario rows failed, " + sw.ElapsedMilliseconds + " ms");
    return failures == 0 ? 0 : 1;
  }
}
