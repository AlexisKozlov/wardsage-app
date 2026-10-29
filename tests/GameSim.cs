// A deterministic model of the game's UI for the pilots (built into CoreTests; C# 5 like the rest of it):
//
//   CoreTests --sim <seeds> [--from <n>] [--slow-only] [--seed <n>] [--verbose] [--list <outcome>] [--digest]   (SIM_TRACE=1: every click with the fresh state;
//   --digest: a line a seed with its outcome and a hash of its presses; --list: each seed whose "owner machine outcome" contains the text, with the hero pilot's why)
//
// The game: a stack of forms (the city at the bottom; the hero screen, the inventory and the Foundry the app reads; any form of the screen
// map on top: back arrow F, close button P, Escape E, dangerous D, wait W, overlay O). A click / Escape acts as the map says (the back arrow
// closes a full screen, the × a pop-up, Escape an E / P / F form); a D form's buttons and the map's never-click buttons are never to be
// pressed: any click on them fails the run. The host: a port of app/Overlay.cs's glue (ReadOffersInto, MapStep, BackStep, UnknownBackStep,
// EscapeSend, GatedSend and the Equip / Sell / Locate ticks) around the real ScreenTracker, ScreenState, ClickGate, HeroPilot and LocatePilot;
// the hero grid is FakeHeroGame's. Two machines: a fast PC (the game answers in ~300 ms) and a slow laptop (the game answers in 0.6-1.5 s, an
// inventory / Foundry form is found 3-8 s after the click that opens it, a lost form's search pass takes 17 s, the window table read fails
// now and then, the timer jitters).
//
// Each seed: a random start (screen, forms over it, machine) and a task (an equip plan's hero, «Найти в игре» for an ascendable item, the
// storage cleanup's way to «Массовая продажа»), run to its end. Asserted: the run ends (done, or stopped with a reason) within the time cap,
// never a click on a D form or a never-click button, never Escape on the city or a D form, the back / Escape / × presses within the gate's caps.
using System;
using System.Collections.Generic;
using System.Text;
using RealmForge;

static partial class CoreTests {
  const string SimCity = "Form_MainCity", SimHero = "Form_CharactorMain", SimInv = "Form_BackpackIntegration", SimFoundry = "Form_EquipSubliming";
  const long SimItem = 777;

  sealed class SimGame {
    public readonly List<string> Stack = new List<string> { SimCity };
    public readonly Random R; public readonly bool Slow; public readonly double FailP;
    public readonly double W = 1920, H = 1009, U;
    public readonly FakeHeroGame Hero; readonly HeroGeometry hg = new HeroGeometry();
    public bool InvEquipTab, InvBulk; public int FoundryTab;
    public readonly List<string> Bad = new List<string>(), Log = new List<string>();
    public readonly StringBuilder Acts = new StringBuilder();   // every click / drag / Escape with its time (--digest)
    public int CityEscapes;
    readonly List<KeyValuePair<long, Action>> due = new List<KeyValuePair<long, Action>>();
    // the slow laptop: the reader finds the inventory / Foundry form some seconds after it first opens; a lost form is searched for 17 s
    readonly Dictionary<string, long> findAt = new Dictionary<string, long>();
    public long LostFrom = -1, LostTo = -1;
    readonly Dictionary<string, int> order = new Dictionary<string, int>();
    public long Now;
    public SimGame(int seed, bool slow, double failP) {
      R = new Random(seed); Slow = slow; FailP = failP; U = Ui.Unit(W, H);
      Hero = new FakeHeroGame(hg, W, U);
      var all = new List<long>(); for (int i = 0; i < 30; i++) all.Add(100 + i);
      Hero.All = all.ToArray(); Hero.Heroes = all.ToArray(); Hero.CityHero = 100; Hero.Tab = R.Next(2) == 0 ? 3 : 1;
    }
    void Say(string t) { Log.Add(Now + " " + t); if (Log.Count > 60) Log.RemoveAt(0); }
    public long Lat() { return Slow ? 600 + R.Next(900) : 80 + R.Next(170); }   // the game's answer (the fast PC: within the click's own send time)
    void Later(long ms, Action a) { due.Add(new KeyValuePair<long, Action>(Now + ms, a)); }
    public void Advance(long now) {
      Now = now;
      for (bool any = true; any; ) {
        any = false;
        for (int i = 0; i < due.Count; i++) if (due[i].Key <= now) { var a = due[i].Value; due.RemoveAt(i); a(); any = true; break; }
      }
    }
    // the stack's full screen (the last F / K / U form) and what lies above it
    public int FullIndex { get { int i = Stack.Count - 1; while (i > 0 && "FKU".IndexOf(ScreenMap.ClassOf(Stack[i])) < 0) i--; return i; } }
    public string Full { get { return Stack[FullIndex]; } }
    /// <summary>The form on top for the player (overlays left out).</summary>
    public string Top { get { for (int i = Stack.Count - 1; i >= 0; i--) if (ScreenMap.ClassOf(Stack[i]) != 'O') return Stack[i]; return SimCity; } }
    public bool CityClear { get { return Top == SimCity; } }
    public void Push(string f) {
      Stack.Add(f); Say("open " + f);
      if (ScreenMap.ClassOf(f) == 'W') { long ms = 2000 + R.Next(8000); Later(ms, () => Remove(f)); }   // a loading / result screen goes by itself
      if (Slow && (f == SimInv || f == SimFoundry) && !findAt.ContainsKey(f)) findAt[f] = Now + 3000 + R.Next(5000);
      if (!Slow && !findAt.ContainsKey(f)) findAt[f] = Now;
    }
    void Pop(string f) { int i = Stack.LastIndexOf(f); if (i > 0) { Stack.RemoveAt(i); Say("close " + f); } }   // (an overlay above it stays)
    void Remove(string f) { int i = Stack.LastIndexOf(f); if (i > 0) { Stack.RemoveAt(i); Say("gone " + f); } }
    string TopForm() { for (int i = Stack.Count - 1; i >= 0; i--) if (ScreenMap.ClassOf(Stack[i]) != 'O') return Stack[i]; return SimCity; }
    /// <summary>Now and then a pop-up the game opens by itself (a reward, a notice, a loading).</summary>
    void MaybeSpawn() {
      if (R.NextDouble() >= 0.04) return;
      string f = SimPick(R, R.Next(3) == 0 ? 'W' : R.Next(2) == 0 ? 'P' : 'E');
      if (f != null && (ScreenMap.ClassOf(f) != 'E' || !CityClear)) Later(500 + R.Next(2500), () => Push(f));
    }

    public bool Searching(string f) { long t; return findAt.TryGetValue(f, out t) && Now < t && Stack.Contains(f); }
    bool Found(string f) { long t; return (!findAt.TryGetValue(f, out t) || Now >= t) && !(Now >= LostFrom && Now < LostTo); }
    bool Fail() { return R.NextDouble() < FailP; }

    /// <summary>What the app's readers give this tick (memory: the known forms; the window table: the offers).</summary>
    public ScreenInputs Read() {
      var i = new ScreenInputs { Offers = Fail() ? null : new List<string>(), ShownKnown = "" };
      string full = Full;
      i.CityForm = full == SimCity;
      bool heroOn = full == SimHero;
      if (Stack.Contains(SimHero) && !Fail()) i.Hero = new HeroScreen { Hero = Hero.Shown, FormOk = heroOn, Tab = Hero.Tab, Part = Hero.Part, Heroes = Hero.Heroes, GridPtr = Hero.Grid };
      else if (!Fail()) i.Hero = new HeroScreen { Hero = 0, FormOk = false };
      if ((full == SimInv || full == SimFoundry) && Found(SimInv) && !Fail())
        i.Inv = new InventoryScreen { Open = true, EquipTab = InvEquipTab, Bulk = InvBulk, BulkKnown = true };
      if (full == SimFoundry && Found(SimFoundry) && !Fail())
        i.Rs = new RecastScreen { SublimingOpen = true, CurType = FoundryTab, AscendShown = FoundryTab == 2, AscendEquips = FoundryTab == 2 ? new long[] { 501, SimItem, 502 } : new long[0] };
      return i;
    }
    /// <summary>The shown forms nobody knows (the window table, in no order); null = the read failed.</summary>
    public List<string> Unknown() {
      if (Fail()) return null;
      var l = new List<string>();
      foreach (var f in Stack) if (Array.IndexOf(OfferForms.KnownScreens, f) < 0) l.Add(f);
      // in no order the app can rely on, but the same while the forms stay (a hash table's order): a key per form for the whole run
      foreach (var f in l) if (!order.ContainsKey(f)) order[f] = R.Next();
      l.Sort((a, b) => order[a].CompareTo(order[b]));
      return l;
    }
    public string ShownKnown() {
      var l = new List<string>();
      foreach (var f in Stack) if (f != SimCity && Array.IndexOf(OfferForms.KnownScreens, f) >= 0) l.Add(f);
      return string.Join(",", l.ToArray());
    }

    bool Near(double x, double y, double px, double py, double tol) { return Math.Abs(x - px) <= tol && Math.Abs(y - py) <= tol; }
    bool OnBack(double x, double y) { return Near(x, y, hg.BackX * U, hg.BackY * U, 15); }

    /// <summary>A click: judged on what is on top now, its effect shows after the game's latency.</summary>
    public void Click(double x, double y, ClickTarget tg) {
      string top = TopForm(); char c = ScreenMap.ClassOf(top);
      if (c == 'D') { Bad.Add("CLICK ON A DANGEROUS FORM " + top + " @" + (int)x + "," + (int)y + " (" + tg + ")"); return; }
      if (ScreenMap.OnNever(top, x, y, W, H, U)) { Bad.Add("CLICK ON A NEVER-CLICK BUTTON of " + top + " @" + (int)x + "," + (int)y + " (" + tg + ")"); return; }
      Say("click " + tg + " @" + (int)x + "," + (int)y + " on " + top); Acts.Append(Now + " c" + (int)x + "," + (int)y + ";");
      long lat = Lat(); MaybeSpawn();
      if (c == 'F' || c == 'P' || c == 'U') {
        if (c != 'U' && ScreenMap.OnExit(top, x, y, W, H, U)) Later(lat, () => Pop(top));
        else if (c == 'U' && OnBack(x, y)) Later(lat, () => Pop(top));
        return;
      }
      if (c != 'K') return;   // E: only the back key closes it; W: nothing takes clicks
      if (top == SimCity) {
        if (tg == ClickTarget.CityInventory && Near(x, y, W - (0.5222 + 0.9316) * U, H - hg.HeroesBtnDY * U, 20)) { Later(lat, () => { InvEquipTab = R.Next(2) == 0; InvBulk = false; Push(SimInv); }); return; }
        if (Near(x, y, W - hg.HeroesBtnDX * U, H - hg.HeroesBtnDY * U, 20)) Later(lat, () => { if (TopForm() != SimCity) return; Hero.Part = -1; Hero.Shown = Hero.CityHero; Push(SimHero); });
        return;
      }
      if (top == SimHero) {
        Later(lat, () => {
          if (TopForm() != SimHero) return;
          Hero.City = false; Hero.Blocked = false; Hero.Click(x, y);
          if (Hero.Shown == 0) Pop(SimHero);
        });
        return;
      }
      if (top == SimInv) {
        if (OnBack(x, y)) { Later(lat, () => Pop(SimInv)); return; }
        if (Near(x, y, 0.085 * U, 0.315 * U, 20)) { Later(lat, () => { InvEquipTab = true; InvBulk = false; }); return; }
        if (InvEquipTab && Near(x, y, 0.5867 * U, H - 0.0723 * U, 20)) { Later(lat, () => { InvBulk = true; }); return; }
        if (tg == ClickTarget.FoundryButton) { Later(lat, () => { FoundryTab = R.Next(2) == 0 ? 0 : 3; Push(SimFoundry); }); return; }
        return;
      }
      if (top == SimFoundry) {
        if (OnBack(x, y)) { Later(lat, () => Pop(SimFoundry)); return; }
        if (tg == ClickTarget.AscendTab) Later(lat, () => { FoundryTab = 2; });
      }
    }

    /// <summary>A drag: the hero grid follows it; on anything else it is a press on its start point.</summary>
    public void Drag(double x, double y, double dy, ClickTarget tg) {
      if (TopForm() != SimHero) { Click(x, y, tg); return; }
      Say("drag " + tg + " @" + (int)x + "," + (int)y + " by " + (int)dy); Acts.Append(Now + " d" + (int)x + "," + (int)y + "," + (int)dy + ";");
      Later(Lat(), () => { if (TopForm() == SimHero) Hero.Drag(dy); });
    }

    /// <summary>The Escape key: closes an E / P / F form (and the app's screens); on the city the game asks to quit.</summary>
    public void Escape() {
      string top = TopForm(); char c = ScreenMap.ClassOf(top);
      Say("ESCAPE on " + top); Acts.Append(Now + " e;");
      if (c == 'D') { Bad.Add("ESCAPE ON A DANGEROUS FORM " + top); return; }
      long lat = Lat();
      if (top == SimCity) { CityEscapes++; Bad.Add("ESCAPE ON THE CITY (the game asks to quit)"); Later(lat, () => Push("Form_SystemTips")); return; }
      if (c == 'W') return;
      if (top == SimHero) { Later(lat, () => { if (Hero.Part >= 0) Hero.Part = -1; else { Hero.Shown = 0; Pop(SimHero); } }); return; }
      if (top == SimInv && InvBulk) { Later(lat, () => { InvBulk = false; }); return; }
      Later(lat, () => Pop(top));
    }
  }

  static readonly Dictionary<char, List<string>> simPool = new Dictionary<char, List<string>>();
  static string SimPick(Random r, char cls) {
    if (simPool.Count == 0) {
      foreach (var e in ScreenMap.All) {
        if (Array.IndexOf(OfferForms.KnownScreens, e.Name) >= 0 || OfferForms.IsOffer(e.Name) || Array.IndexOf(OfferForms.IgnoredOverlays, e.Name) >= 0 && e.Class != 'O') continue;
        List<string> l; if (!simPool.TryGetValue(e.Class, out l)) simPool[e.Class] = l = new List<string>();
        l.Add(e.Name);
      }
    }
    List<string> p; if (!simPool.TryGetValue(cls, out p) || p.Count == 0) return null;
    return p[r.Next(p.Count)];
  }

  // ---- the host: app/Overlay.cs's glue around the real tracker, gate and pilots ----
  sealed class SimHost {
    public readonly SimGame G; public readonly ClickGate Gate = new ClickGate(); public readonly ScreenTracker Screens = new ScreenTracker();
    public readonly ClickOwner Owner; public long Now;
    public readonly Dictionary<ClickTarget, int> Sent = new Dictionary<ClickTarget, int>(); public int EscapesSent;
    public string Done = "";
    readonly HeroPilot hp = new HeroPilot(new HeroGeometry()); readonly LocatePilot lp = new LocatePilot(); readonly HeroGeometry hg2 = new HeroGeometry();
    HeroScreen heroScreen = new HeroScreen(); RecastScreen locateRs; readonly long plan; readonly LocateRequest req;
    readonly GameGeometry geo;
    long sellBtnAt = -100000;
    public string PilotState = "";
    public string HeroWhy { get { return hp.Why; } }
    public long SendingUntil;   // app/Overlay.cs Send: the cursor moves and the click take ~300 ms on their own thread; no tick reads the screens meanwhile
    public SimHost(SimGame g, ClickOwner o, long plan, GameGeometry geo) {
      G = g; Owner = o; this.plan = plan; this.geo = geo;
      req = new LocateRequest { Id = "sim", Purpose = "ascend", Item = new PlanItem { Uid = SimItem, Slot = 3, Level = 16, Stars = 5 } };
      Gate.FormSearchPending = t => t == ClickTarget.CityInventory ? G.Searching(SimInv) : t == ClickTarget.FoundryButton && G.Searching(SimFoundry);
      Gate.FormFound = t => t == ClickTarget.CityInventory ? !G.Searching(SimInv) : t == ClickTarget.FoundryButton && !G.Searching(SimFoundry);
    }
    double W { get { return G.W; } } double H { get { return G.H; } } double U { get { return G.U; } }

    // ReadOffersInto (app/Overlay.cs): the forms nobody knows, read every 2 s; over a known screen the ones that came later are a pop-up
    const int TopSettleMs = 3000, TopFlickerMs = 2000;
    long topBaseAt, topLastAt, unknownFormsAt; ScreenKind topLastKind = ScreenKind.Unknown; ScreenKind topBaseKind = ScreenKind.Unknown; HashSet<string> topBase = new HashSet<string>();
    string topNewNow = "", topUnknownNow = "", topCityNow = ""; ScreenKind topNewKind = ScreenKind.Unknown; List<string> unknownForms; bool formsUnread; ScreenKind formsKind = ScreenKind.Unknown;
    bool FormsUnread(ScreenState fresh) { return formsUnread || fresh == null || !fresh.OfferRead || (fresh.Kind != ScreenKind.City && fresh.Kind != ScreenKind.Unknown && fresh.Kind != formsKind); }
    void ReadOffersInto(ScreenInputs inp, long now) {
      formsUnread = inp.Offers == null;
      var cls = ScreenState.Classify(inp, now).Kind;
      topNewNow = ""; topUnknownNow = ""; topCityNow = "";
      formsKind = cls;
      if (inp.Offers == null || inp.Offers.Count == 0) {
        var prevForms = unknownForms;   // the forms of the last read (before this tick's): one not in it came with the screen
        if (now - unknownFormsAt >= 2000 || unknownFormsAt == 0) { unknownFormsAt = now; unknownForms = G.Unknown(); if (unknownForms == null) { formsUnread = true; unknownFormsAt = 0; } }
        var cur = unknownForms ?? new List<string>();
        if (cls == ScreenKind.Unknown) {
          if (cur.Count > 0) inp.TopUnknown = topUnknownNow = string.Join(",", cur.ToArray());
        } else if (cls == ScreenKind.City) {
          var cov = cur.FindAll(n => "PEDW".IndexOf(ScreenMap.ClassOf(n)) >= 0);
          if (cov.Count > 0) inp.TopCity = topCityNow = string.Join(",", cov.ToArray());
        } else if (unknownForms != null) {
          // (a read that failed for a tick or two flips the screen to unknown and back: the same screen within TopFlickerMs keeps its base, else a
          // pop-up shown meanwhile would be taken for the screen's own form - the slow laptop's grid clicks went into one)
          if (cls != topBaseKind && cls == topLastKind && now - topLastAt <= TopFlickerMs) topBaseKind = cls;
          if (cls != topBaseKind) { topBaseKind = cls; topBaseAt = now; topBase = new HashSet<string>(); unknownForms = G.Unknown(); unknownFormsAt = now; cur = unknownForms ?? new List<string>(); if (unknownForms == null) { formsUnread = true; unknownFormsAt = 0; } }
          topLastKind = cls; topLastAt = now;
          // (one shown already is the screen's own, a pop-up may lie under the full screen - the window table has no order; a pop-up or screen that came
          // with it or later lies on top, a part the game builds later does not)
          if (now - topBaseAt < TopSettleMs) foreach (var n in cur) if (now == topBaseAt && (prevForms == null || prevForms.Contains(n)) ? ScreenMap.MayBeBase(n) : ScreenMap.MayBeLatePart(n)) topBase.Add(n);
          var fresh = cur.FindAll(n => !topBase.Contains(n));
          if (fresh.Count > 0) { inp.TopNew = topNewNow = string.Join(",", fresh.ToArray()); topNewKind = cls; }
        }
        if (cls == ScreenKind.Unknown || cls == ScreenKind.City) topBaseKind = cls;
      } else unknownForms = null;
    }
    public void ReadScreens(long now) {
      Now = now;
      var inp = G.Read();
      ReadOffersInto(inp, now);
      inp.CitySeen = inp.CityForm == true && G.CityClear;
      var st = Screens.Update(inp, now);
      bool sw = now >= G.LostFrom && now < G.LostTo;
      Gate.SearchWait = sw;
      if (Screens.Hero != null) heroScreen = Screens.Hero;
      Gate.Observe(st, now);
    }
    // FreshScreen (app/Overlay.cs): fresh memory reads, the forms read by the tick
    ScreenState Fresh(bool wantCity, bool wantKnownForms) {
      var inp = G.Read();
      if (wantKnownForms) inp.ShownKnown = G.ShownKnown();
      var st = ScreenState.Classify(inp, Now);
      if (topNewNow != "" && st.Kind == topNewKind) { inp.TopNew = topNewNow; st = ScreenState.Classify(inp, Now); }
      else if (topUnknownNow != "" && st.Kind == ScreenKind.Unknown) { inp.TopUnknown = topUnknownNow; st = ScreenState.Classify(inp, Now); }
      else if (topCityNow != "" && st.Kind == ScreenKind.City) { inp.TopCity = topCityNow; st = ScreenState.Classify(inp, Now); }
      if (wantCity && ScreenState.NeedsCityPicture(st.Kind, inp.CityForm)) {
        bool seen = G.Full == SimCity && G.CityClear;
        if (inp.CityForm == true && !seen) inp.CityForm = false;
        inp.CitySeen = seen; st = ScreenState.Classify(inp, Now);
      }
      return st;
    }
    void Count(ClickTarget t) { int n; Sent.TryGetValue(t, out n); Sent[t] = n + 1; }
    // GatedSend / SendInside: the gate's yes on a fresh read, then the click and its watch
    bool Send(ClickTarget t, AutoAction a, Action<ClickRequest, ScreenState> fill, bool wantCity, bool wantKnown) {
      if (a.X < 0 || a.Y < 0 || a.X >= W || a.Y >= H) return false;
      var fresh = Fresh(wantCity, wantKnown);
      if (FormsUnread(fresh)) return false;
      var rq = new ClickRequest { Owner = Owner, Target = t, AutoOn = true, Front = true, X = a.X, Y = a.Y };
      if (fill != null) fill(rq, fresh);
      var res = Gate.Check(rq, fresh, Now);
      if (!res.Allowed) return false;
      Gate.Arm(Owner, t, ClickGate.Expect(rq, fresh), Now, rq.Form);
      Count(t);
      if (Environment.GetEnvironmentVariable("SIM_TRACE") != null) Console.WriteLine("  send " + t + " fresh=" + fresh.Brief + " top=" + fresh.Top + " tick=" + (Screens.Current != null ? Screens.Current.Brief : "-") + " unread=" + formsUnread);
      if (a.Kind == AutoKind.Click) G.Click(a.X, a.Y, t); else if (a.Kind == AutoKind.Drag) G.Drag(a.X, a.Y, a.DY, t);
      SendingUntil = Now + 300;
      return true;
    }
    bool EscapeSend() {
      var fresh = Fresh(false, true);
      if (FormsUnread(fresh)) return false;
      var rq = new ClickRequest { Owner = Owner, Target = ClickTarget.EscapeBack, AutoOn = true, Front = true, U = U };
      if (!Gate.Check(rq, fresh, Now).Allowed) return false;
      Gate.Arm(Owner, ClickTarget.EscapeBack, ClickGate.Expect(rq, fresh), Now);
      EscapesSent++; G.Escape(); SendingUntil = Now + 90;
      return true;
    }
    bool MapStep(ScreenState scr) {
      if (scr == null) return false;
      var d = ClickGate.MapExit(scr, W, H, U);
      if (d.Move == MapMove.None || d.Move == MapMove.Back) return false;
      if (d.Move == MapMove.Wait || Gate.Halted(Owner)) return true;
      if (Gate.EscapeDue(Owner, scr, Now)) { EscapeSend(); return true; }
      if (!Gate.MapDue(Owner, scr, Now)) return true;
      if (d.Move == MapMove.Stop) { Gate.MapStop(Owner, d, scr); return true; }
      if (d.Move == MapMove.Escape) { Gate.MapEscape(Owner); EscapeSend(); return true; }
      string form = d.Form;
      Send(ClickTarget.MapClose, new AutoAction { Kind = AutoKind.Click, X = d.Point.X, Y = d.Point.Y }, (rq, fr) => { rq.Form = form; rq.W = W; rq.H = H; rq.U = U; }, false, false);
      return true;
    }
    bool UnknownBackStep(ScreenState scr) {
      if (MapStep(scr)) return true;
      if (scr != null && scr.Kind == ScreenKind.Unknown && Gate.EscapeDue(Owner, scr, Now)) { EscapeSend(); return true; }
      if (!Gate.UnknownBackDue(Owner, scr, Now)) return false;
      var md = ClickGate.MapExit(scr, W, H, U);
      double bx = md.Move == MapMove.Back && md.Point != null ? md.Point.X : hg2.BackX * U, by = md.Move == MapMove.Back && md.Point != null ? md.Point.Y : hg2.BackY * U;
      Send(ClickTarget.UnknownBack, new AutoAction { Kind = AutoKind.Click, X = bx, Y = by }, (rq, fr) => { rq.U = U; rq.W = W; rq.H = H; }, false, true);
      return true;
    }
    bool BackStep(ScreenState scr, bool need) {
      if (MapStep(scr)) return true;
      if (!need) { if (scr != null && scr.Kind != ScreenKind.Unknown) Gate.DropEscape(Owner); return false; }
      if (Gate.EscapeDue(Owner, scr, Now)) { EscapeSend(); return true; }
      if (!Gate.CityBackDue(Owner, scr, Now)) return false;
      Send(ClickTarget.CityBack, new AutoAction { Kind = AutoKind.Click, X = hg2.BackX * U, Y = hg2.BackY * U }, (rq, fr) => { rq.U = U; }, false, true);
      return true;
    }

    public void Tick() {
      if (Done != "") return;
      if (Owner == ClickOwner.Equip) TickEquip(); else if (Owner == ClickOwner.Sell) TickSell(); else TickLocate();
    }
    // TickAuto's hero part (app/Overlay.cs): the plan's hero on the hero screen, the gear tab
    void TickEquip() {
      var scr = Screens.Current;
      if (UnknownBackStep(scr)) return;
      if (BackStep(scr, ClickGate.AwayFromStart(ClickOwner.Equip, scr))) return;
      if (Gate.UnknownStall(ClickOwner.Equip, scr, Now)) return;
      var hv = new HeroView {
        NowMs = Now, Foreground = true, W = W, H = U, ClientH = H, Plan = plan, Hero = heroScreen.FormOk ? heroScreen.Hero : 0, Tab = heroScreen.Tab, Part = heroScreen.Part,
        SmallCards = heroScreen.SmallCards, Heroes = heroScreen.Heroes, HeroesButton = heroScreen.Hero == 0 && G.Full == SimCity && G.CityClear,
        FilterOpen = false, ClassChosen = heroScreen.ClassChosen, FactionList = heroScreen.FactionList, ChosenCamps = heroScreen.ChosenCamps, ClassCount = heroScreen.ClassCount, GridPtr = heroScreen.GridPtr
      };
      if (!heroScreen.FormOk) hv.Hero = 0;
      var ha = hp.Step(hv); PilotState = "hero_" + hp.State;
      if (ha == null) { if (hp.State == "done" || hp.State == "idle") Done = "done"; return; }   // idle: the plan's hero was there already
      if (ha.Kind == AutoKind.None) return;
      Send(ClickTarget.EquipNav, ha, null, true, false);
    }
    // TickSell (app/Overlay.cs) up to «Массовая продажа» (the marking itself: RunSell's tests)
    void TickSell() {
      var scr = Screens.Current;
      if (Gate.Halted(ClickOwner.Sell)) return;
      var pre = Gate.Preflight(ClickOwner.Sell, scr, Now);
      if (pre.Verdict == GateVerdict.Deny) return;
      if (pre.Reason == "unknown" && UnknownBackStep(scr)) return;
      if (pre.Verdict == GateVerdict.Wait) { PilotState = pre.Reason == "unknown" ? "no_city" : PilotState; return; }
      var inv = Screens.Inv;
      if (scr != null && scr.Kind == ScreenKind.Inventory && inv != null) {
        if (!inv.EquipTab || !inv.Bulk) {
          PilotState = "open";
          if (Now - sellBtnAt < 2000) return;
          sellBtnAt = Now;
          var a0 = !inv.EquipTab ? new AutoAction { Kind = AutoKind.Click, X = 0.085 * U, Y = 0.315 * U } : new AutoAction { Kind = AutoKind.Click, X = 0.5867 * U, Y = H - 0.0723 * U };
          Send(!inv.EquipTab ? ClickTarget.InvEquipTab : ClickTarget.InvBulkEnter, a0, null, false, false);
          return;
        }
        Done = "done"; return;
      }
      if (BackStep(scr, ClickGate.AwayFromStart(ClickOwner.Sell, scr))) { PilotState = "open"; return; }
      if (scr != null && scr.Kind == ScreenKind.HeroScreen) {
        PilotState = "open";
        if (Now - sellBtnAt < 2000) return;
        sellBtnAt = Now;
        Send(ClickTarget.HeroBack, new AutoAction { Kind = AutoKind.Click, X = hg2.BackX * U, Y = hg2.BackY * U }, null, false, false);
        return;
      }
      if (scr != null && scr.Kind == ScreenKind.City && G.Full == SimCity && G.CityClear) {
        PilotState = "open";
        if (Now - sellBtnAt < 2500) return;
        sellBtnAt = Now;
        Send(ClickTarget.CityInventory, new AutoAction { Kind = AutoKind.Click, X = W - (0.5222 + 0.9316) * U, Y = H - hg2.HeroesBtnDY * U }, null, true, false);
        return;
      }
      PilotState = "no_city";
    }
    // TickLocate (app/Overlay.cs) for an ascendable item: the city → the inventory → «Литейная» → «Вознесение»
    void TickLocate() {
      if (lp.Stopped) { PilotState = "stopped:" + lp.Reason; return; }
      var scr = Screens.Current; var inv = Screens.Inv;
      if (Screens.Rs != null) locateRs = Screens.Rs;
      var pre = Gate.Preflight(ClickOwner.Locate, scr, Now);
      if (lp.Stopped) return;
      if (pre.Reason == "unknown" && UnknownBackStep(scr)) return;
      if (pre.Verdict == GateVerdict.Wait) { PilotState = pre.Reason == "pending" ? "work" : "hint:no_city"; return; }
      bool heroOpen = scr != null && scr.Kind == ScreenKind.HeroScreen;
      bool formOpen = scr != null && (scr.Kind == ScreenKind.Foundry || scr.Kind == ScreenKind.Gem);
      var dstep = LocateGuide.Next(req, inv, locateRs, heroScreen, 8, 1);
      if (dstep.Kind == LocateKind.AscendPick || dstep.Kind == LocateKind.AscendLoading || dstep.Kind == LocateKind.AscendReady) { Done = "done"; return; }
      var view = new LocateView { NowMs = Now, Foreground = true, W = W, H = H, U = U, Step = dstep, Inv = inv, Cols = 8, HeroScreenOpen = heroOpen, OtherFormOpen = formOpen,
                                  Geometry = geo, Rs = locateRs, Item = req.Item, Purpose = req.Purpose };
      if (LocateAuto.NeedsCityButton(dstep, heroOpen, formOpen)) view.CityButton = G.Full == SimCity && G.CityClear;
      bool needBack = LocateAuto.NeedsCityBack(view);
      if (BackStep(scr, needBack)) return;
      var mv = lp.Step(view);
      PilotState = mv.State + (mv.Why != "" ? ":" + mv.Why : "") + "@" + dstep.Kind;
      if (mv.Action.Kind == AutoKind.None) return;
      ClickTarget tg = ClickTarget.None;
      switch (mv.Todo) {
        case LocateTodo.BackToCity: tg = ClickTarget.HeroBack; break;
        case LocateTodo.InventoryButton: tg = ClickTarget.CityInventory; break;
        case LocateTodo.EquipTab: tg = ClickTarget.InvEquipTab; break;
        case LocateTodo.FoundryButton: tg = ClickTarget.FoundryButton; break;
        case LocateTodo.AscendTab: tg = ClickTarget.AscendTab; break;
        case LocateTodo.SlotTab: tg = ClickTarget.SlotTab; break;
      }
      Send(tg, mv.Action, null, tg == ClickTarget.CityInventory, tg == ClickTarget.UnknownBack || tg == ClickTarget.CityBack);
    }
  }

  static readonly string[] SimNamed = { "Form_Union", "Form_PVPMain", "Form_Shop" };

  /// <summary>One seed: the start, the task, the run. Returns "" when the run kept every rule, else what went wrong.</summary>
  static string SimRun(int seed, bool slowOnly, bool verbose, Dictionary<string, int> outcomes, GameGeometry geo, string list = null) {
    var r = new Random(seed * 7919 + 13);
    bool slow = slowOnly || r.Next(2) == 0;
    double failP = r.Next(3) == 0 ? 0.0 : r.NextDouble() * 0.15;
    var g = new SimGame(seed, slow, failP);
    var owner = new[] { ClickOwner.Equip, ClickOwner.Sell, ClickOwner.Locate }[r.Next(3)];
    // the start: a screen and the forms over it
    int k = r.Next(10);
    if (k == 2) { g.Hero.Shown = 100 + r.Next(30); g.Push(SimHero); if (r.Next(3) == 0) g.Hero.Part = r.Next(5); }
    else if (k == 3 || k == 4) { g.InvEquipTab = r.Next(2) == 0; g.InvBulk = g.InvEquipTab && r.Next(3) == 0; g.Push(SimInv); if (k == 4) { g.FoundryTab = new[] { 0, 2, 3 }[r.Next(3)]; g.Push(SimFoundry); } }
    else if (k >= 5) { int n = 1 + r.Next(2); for (int i = 0; i < n; i++) g.Push(r.Next(2) == 0 ? SimNamed[r.Next(SimNamed.Length)] : SimPick(r, 'F')); }
    int extra = r.Next(3);
    for (int i = 0; i < extra; i++) {
      int p = r.Next(100); char c = p < 45 ? 'P' : p < 65 ? 'E' : p < 75 ? 'D' : p < 82 ? 'W' : 'O';
      string f = SimPick(r, c); if (f != null) g.Push(f);
    }
    if (slow && r.Next(8) == 0) { g.LostFrom = 2000 + r.Next(20000); g.LostTo = g.LostFrom + 17000; }
    string start = string.Join(" > ", g.Stack.ToArray());
    long plan = 100 + r.Next(30);
    var h = new SimHost(g, owner, plan, geo);
    long cap = slow ? 300000 : 150000, t = 0, lastChange = 0; string lastSay = "";
    while (t < cap) {
      g.Advance(t);
      if (t >= h.SendingUntil) { h.ReadScreens(t); h.Tick(); }
      if (g.Bad.Count > 0 || h.Done != "" || h.Gate.Halted(owner)) break;
      string say = h.PilotState + "|" + string.Join(">", g.Stack.ToArray());
      if (say != lastSay) { lastSay = say; lastChange = t; }
      // a pilot that tells the player why it waits (the hero screen not reachable, a hint) and nothing changes for 30 s: a stop with a reason
      if (t - lastChange > 30000 && (h.PilotState.StartsWith("hero_noscreen") || h.PilotState.StartsWith("hero_failed") || h.PilotState.StartsWith("hero_notfound") || h.PilotState.StartsWith("hint") || h.PilotState.StartsWith("stopped") || h.PilotState == "no_city")) break;
      t += slow ? 150 + r.Next(250) : 100;
    }
    string outcome = h.Done != "" ? "done" : h.Gate.Halted(owner) ? "halt:" + h.Gate.HaltReason(owner) : t >= cap ? "TIMEOUT" : "wait:" + h.PilotState.Split('@')[0];
    string key = owner + (slow ? " slow " : " fast ") + outcome;
    int cnt; outcomes.TryGetValue(key, out cnt); outcomes[key] = cnt + 1;
    if (list == "--digest") {   // one line a seed: its outcome and a hash of every press with its time (two builds compared seed by seed)
      ulong hsh = 14695981039346656037UL; foreach (char ch in g.Acts.ToString()) { hsh ^= ch; hsh *= 1099511628211UL; }
      Console.WriteLine(seed + " " + key + " " + hsh.ToString("x16"));
    } else if (list != null && key.Contains(list)) Console.WriteLine("seed " + seed + ": " + key + (h.PilotState.StartsWith("hero_") ? " " + h.HeroWhy : "") + " end [" + string.Join(" > ", g.Stack.ToArray()) + "]");
    var errs = new List<string>(g.Bad);
    if (outcome == "TIMEOUT") errs.Add("no end within " + cap / 1000 + " s (silent waiting): " + h.PilotState);
    int n0;
    if (h.Sent.TryGetValue(ClickTarget.UnknownBack, out n0) && n0 > ClickGate.MaxUnknownBacks) errs.Add(n0 + " unknown-screen back presses");
    if (h.Sent.TryGetValue(ClickTarget.CityBack, out n0) && n0 > ClickGate.MaxCityBacks) errs.Add(n0 + " back-to-the-city presses");
    if (h.Sent.TryGetValue(ClickTarget.MapClose, out n0) && n0 > ClickGate.MaxMapMoves) errs.Add(n0 + " × presses");
    if (h.EscapesSent > ClickGate.MaxEscapes + ClickGate.MaxMapMoves) errs.Add(h.EscapesSent + " Escapes");
    if (errs.Count == 0 && !verbose) return "";
    var sb = new StringBuilder();
    sb.Append("seed " + seed + " " + owner + (slow ? " slow" : " fast") + " fail " + failP.ToString("0.00") + " start [" + start + "] -> " + outcome
              + (h.Gate.Halted(owner) ? " (" + h.Gate.HaltDetail(owner) + ")" : "") + " end [" + string.Join(" > ", g.Stack.ToArray()) + "]");
    foreach (var e in errs) sb.Append("\n    !! " + e);
    if (errs.Count > 0 || verbose) foreach (var l in g.Log) sb.Append("\n      " + l);
    return sb.ToString();
  }

  // what the game sim found (each a fix in src/ or app/Overlay.cs), kept as plain tests
  static void SimRegressionTests() {
    Console.WriteLine("Game sim: its findings");
    // (1) a dangerous dialog or a wait shown when a known screen comes is never taken for a part of that screen (the pilot clicked under a buy dialog)
    Check(!ScreenMap.MayBeBase("Form_BuyPopup") && !ScreenMap.MayBeBase("Form_BattleLoading") && ScreenMap.MayBeBase("Form_AccountInfo") && ScreenMap.MayBeBase("Form_NotInTheGame"),
          "sim: a dangerous dialog / a wait is never a known screen's own form, a pop-up may be");
    // (2) the × of one pop-up must not lie on a never-click button of another pop-up shown with it (the window table has no order)
    {
      const double W = 1920, H = 1009; double U = Ui.Unit(W, H); string a = null, b = null;
      foreach (var ea in ScreenMap.All) {
        if (ea.Class != 'P' || a != null) continue;
        var pt = ScreenMap.ExitPoint(ea.Name, W, H, U); if (pt == null || ScreenMap.OnNever(ea.Name, pt.X, pt.Y, W, H, U)) continue;
        foreach (var eb in ScreenMap.All) if (eb.Class == 'P' && eb.Name != ea.Name && ScreenMap.OnNever(eb.Name, pt.X, pt.Y, W, H, U)) { a = ea.Name; b = eb.Name; break; }
      }
      Check(a != null, "sim: two pop-ups whose × and never-click button overlap exist (" + a + " / " + b + ")");
      if (a != null) {
        var pt = ScreenMap.ExitPoint(a, W, H, U);
        foreach (var list in new[] { a + "," + b, b + "," + a }) {
          var s = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = list, ShownKnown = "" }, 0);
          var d = ClickGate.MapExit(s, W, H, U);
          if (d.Move != MapMove.Close) continue;
          var g = new ClickGate(); var p2 = ScreenMap.ExitPoint(d.Form, W, H, U);
          var res = g.Check(new ClickRequest { Owner = ClickOwner.Equip, Target = ClickTarget.MapClose, AutoOn = true, Front = true, X = p2.X, Y = p2.Y, W = W, H = H, U = U, Form = d.Form }, s, 10000);
          string other = d.Form == a ? b : a;
          Check(!res.Allowed == ScreenMap.OnNever(other, p2.X, p2.Y, W, H, U), "sim: the × of " + d.Form + " shown with " + other + ": " + (res.Allowed ? "allowed" : res.Detail));
        }
      }
    }
    // (3) the hero grid: a click whose new hero shows a few ticks late is still learnt (a slow PC), the plan's hero is reached
    {
      var g = new HeroGeometry(); var game = new FakeHeroGame(g, 1920, 1009);
      var all = new List<long>(); for (int i = 0; i < 30; i++) all.Add(100 + i);
      game.All = all.ToArray(); game.Heroes = all.ToArray(); game.Shown = 100;
      var p = new HeroPilot(g); var q = new List<KeyValuePair<long, double[]>>(); string st = "timeout";
      for (long t = 0; t < 120000; t += 100) {
        foreach (var e in q.ToArray()) if (e.Key <= t) { if (e.Value[2] == 0) game.Click(e.Value[0], e.Value[1]); else game.Drag(e.Value[2]); q.Remove(e); }
        var a = p.Step(new HeroView { NowMs = t, Foreground = true, W = 1920, H = 1009, ClientH = 1009, Plan = 127, Hero = game.Shown, Tab = 3, Part = -1, Heroes = game.Heroes, GridPtr = game.Grid });
        if (a == null) { st = p.State; break; }
        if (p.State == "failed") { st = "failed: " + p.Why; break; }
        if (a.Kind == AutoKind.Click) q.Add(new KeyValuePair<long, double[]>(t + 300, new[] { a.X, a.Y, 0 }));
        else if (a.Kind == AutoKind.Drag) q.Add(new KeyValuePair<long, double[]>(t + 300, new[] { a.X, a.Y, a.DY }));
      }
      Eq("done", st, "sim: the hero grid with the game answering 300 ms after each click");
    }
    // (5) the hero grid on a slow laptop: the game answers 0.6-1.5 s after each click (later than the click's wait): every answer was taken for the
    // next click's, the rows never agreed (seeds 250, 161: «no click brings up another hero» / «not brought up by 24 clicks»); the waits now grow
    foreach (int lat in new[] { 900, 1500 }) {
      var g = new HeroGeometry(); var game = new FakeHeroGame(g, 1920, 1009);
      var all = new List<long>(); for (int i = 0; i < 30; i++) all.Add(100 + i);
      game.All = all.ToArray(); game.Heroes = all.ToArray(); game.Shown = 100;
      var p = new HeroPilot(g); var q = new List<KeyValuePair<long, double[]>>(); string st = "timeout"; int acts = 0;
      for (long t = 0; t < 120000; t += 100) {
        foreach (var e in q.ToArray()) if (e.Key <= t) { if (e.Value[2] == 0) game.Click(e.Value[0], e.Value[1]); else game.Drag(e.Value[2]); q.Remove(e); }
        var a = p.Step(new HeroView { NowMs = t, Foreground = true, W = 1920, H = 1009, ClientH = 1009, Plan = 127, Hero = game.Shown, Tab = 3, Part = -1, Heroes = game.Heroes, GridPtr = game.Grid });
        if (a == null) { st = p.State; break; }
        if (p.State == "failed") { st = "failed: " + p.Why; break; }
        if (a.Kind != AutoKind.None) acts++;
        if (a.Kind == AutoKind.Click) q.Add(new KeyValuePair<long, double[]>(t + lat, new[] { a.X, a.Y, 0 }));
        else if (a.Kind == AutoKind.Drag) q.Add(new KeyValuePair<long, double[]>(t + lat, new[] { a.X, a.Y, a.DY }));
      }
      Eq("done", st, "sim: the hero grid with the game answering " + lat + " ms after each click");
      Check(acts <= 20, "sim: the slow hero grid in " + acts + " clicks and drags (at most 20)");
    }
    // (4) the seeds that found them: a dangerous dialog over a known screen at the start (10 ... 337), a failed window table read at the click
    // (612, 619, 1227, 1259, 1899), the × of one pop-up on a never-click button of another shown with it (27054, 31961); 3810: a plan already on
    {
      var geo = GameGeometry.Parse(GeoJson(), GeoGame); var oc = new Dictionary<string, int>();
      foreach (int seed in new[] { 10, 194, 200, 330, 337, 612, 619, 1227, 1259, 1899, 3810, 27054, 31961 }) {
        string e = SimRun(seed, false, false, oc, geo);
        Check(e == "", "sim: seed " + seed + " keeps every rule" + (e != "" ? ": " + e.Split('\n')[0] + " " + (e.Split('\n').Length > 1 ? e.Split('\n')[1].Trim() : "") : ""));
      }
      // (6) seeds that must reach the plan's hero: a pop-up that came with the hero screen (62) or while a failed read made it unknown for a
      // tick (82, 19) was taken for the screen's own form and took the grid clicks; a slow laptop's late answers (250, 161)
      foreach (int seed in new[] { 19, 62, 82, 161, 250 }) {
        var o1 = new Dictionary<string, int>(); SimRun(seed, false, false, o1, geo);
        string got = string.Join(",", new List<string>(o1.Keys).ToArray());
        Check(got.EndsWith(" done"), "sim: seed " + seed + " brings up the plan's hero (" + got + ")");
      }
    }
  }

  static int SimMain(string[] args) {
    Layout.Use(TestLayout());
    int seeds = 500, from = 0; bool slowOnly = false, verbose = false; string list = null;
    for (int i = 1; i < args.Length; i++) {
      if (args[i] == "--slow-only") slowOnly = true;
      else if (args[i] == "--verbose") verbose = true;
      else if (args[i] == "--seed" && i + 1 < args.Length) { from = int.Parse(args[++i]); seeds = 1; }
      else if (args[i] == "--from" && i + 1 < args.Length) from = int.Parse(args[++i]);
      else if (args[i] == "--list" && i + 1 < args.Length) list = args[++i];
      else if (args[i] == "--digest") list = "--digest";
      else { int n; if (int.TryParse(args[i], out n)) seeds = n; }
    }
    var geo = GameGeometry.Parse(GeoJson(), GeoGame);
    var outcomes = new SortedDictionary<string, int>(); var tmp = new Dictionary<string, int>();
    int bad = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int s = from; s < from + seeds; s++) {
      string e = SimRun(s, slowOnly, verbose, tmp, geo, list);
      bool broke = e.Contains("\n    !! ");
      if (broke) bad++;
      if (e != "" && (verbose || bad <= 5)) Console.WriteLine(e);
    }
    foreach (var kv in tmp) outcomes[kv.Key] = kv.Value;
    Console.WriteLine("game sim: " + seeds + " seeds from " + from + " in " + sw.ElapsedMilliseconds + " ms, " + bad + " broke a rule");
    foreach (var kv in outcomes) Console.WriteLine("  " + kv.Value.ToString().PadLeft(5) + "  " + kv.Key);
    return bad == 0 ? 0 : 1;
  }
}
