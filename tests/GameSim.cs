// A deterministic model of the game's UI for the pilots (built into CoreTests; C# 5 like the rest of it):
//
//   CoreTests --sim <seeds> [--from <n>] [--slow-only] [--real] [--graph] [--seed <n>] [--verbose] [--list <outcome>] [--digest]   (SIM_TRACE=1: every click with the fresh state;
//   --graph: the game's forms, transitions and pop-ups from the UI graph (src/UiGraphData.cs): the starts walk a safe path from the city to any
//   reachable form, the pop-ups are the forms the game opens by itself (ActivityFormMgr's queue over the city, the data modules' results), a
//   click on a form's widget does what the client's code does (opens / closes a form; a click on a dangerous one breaks the run);
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
  const string SimGem = "Form_EquipGemMain", SimWs = "Form_EquipArtifactIntensify";   // the gem screen, the artifact workshop (over the inventory)
  const long SimItem = 777;
  // the equip flow's item tip: the game opens it when the pilot selects an item of the gear list (a flow form of the hero screen, never closed)
  const string SimTip = "Form_CharactorEquipTip";

  sealed class SimGame {
    public readonly List<string> Stack = new List<string> { SimCity };
    public readonly Random R, R2; public readonly bool Slow; public readonly double FailP;   // (R2: the later additions, so the old seeds keep their runs)
    public readonly double W = 1920, H = 1009, U;
    // the city built again when a guild screen over it closes (live 1.6.66.385: Form_UnionBoss > Form_UnionAdd > the city, a new Form_MainCity):
    // its bar comes in with an animation, a press within BarInMs is lost. Off unless a test sets it (the seeds keep their runs)
    public bool CityRebuild; public ulong CityInst = 1; public long CityBuiltAt = -100000; public int LostPresses; public const long BarInMs = 1500;
    public readonly FakeHeroGame Hero; readonly HeroGeometry hg = new HeroGeometry();
    public bool InvEquipTab, InvBulk, InvArtTab; public int FoundryTab;
    // the artifact tab's list (7 columns, a row 116 units; empty unless a test fills it), its selected card (1-based, 0 none) and the workshop's artifact;
    // ArtWorn: the selected artifact is worn - the card hides «Продать» and «Мастерская» moves left off the layout's place (live 1.6.66.402)
    public readonly List<long> InvArts = new List<long>(); public int InvArtSel; public long WsUid = 555; public bool ArtWorn;
    public const int ArtCols = 7, ArtRowUnits = 116;
    /// <summary>The card's «Мастерская» where the game has put it (its live rect: x, y, w, h); null = the inventory's artifact card is not on top.</summary>
    public double[] WorkshopLive() {
      if (TopForm() != SimInv || !InvArtTab || InvArtSel <= 0) return null;
      var r = ArtFrames.Rect(Geo, "inventory.artWorkshop", W, H, U); if (r == null) return null;
      return new[] { r.X - (ArtWorn ? 0.8 * r.W : 0), r.Y, r.W, r.H };
    }
    public bool FoundryBuilt;   // the Foundry's form was built once: its reader gives "not open" after it closes (app RFX.ReadRecastScreenSafe)
    // the inventory's equipment list (8 columns; «Массовая продажа» ticks its cells) and the Foundry's Ascension list (5 columns, the slot tabs
    // narrow it): their items, the ticked / selected ones, the scroll (the game's units: a row is 118 / 108)
    public readonly List<long> InvItems = new List<long>(); public readonly HashSet<long> InvSel = new HashSet<long>(); public double InvScrollY; public ulong InvPtr = 11;
    public readonly List<long> AscAll = new List<long>(); public readonly Dictionary<long, int> AscSlot = new Dictionary<long, int>();
    public readonly HashSet<int> AscParts = new HashSet<int>(); public long AscSel; public double AscScrollY;
    public const int InvCols = 8, InvRowUnits = 118, AscCols = 5, AscRowUnits = 108;
    public readonly SellGeometry InvGeo; public GameGeometry Geo;
    // the inventory list follows a drag by its own gain, up and down apart (live about 1.0 and 0.84): the pilot's drags land off by that much
    public double InvGainUp = 1, InvGainDown = 1;
    public readonly HashSet<string> StartForms = new HashSet<string>();   // the forms there at the start (over or under a screen: nobody can tell)
    public readonly List<string> Bad = new List<string>(), Log = new List<string>();
    public readonly StringBuilder Acts = new StringBuilder();   // every click / drag / Escape with its time (--digest)
    public int CityEscapes;
    public double SpawnP = 0.04;   // a pop-up the game opens by itself, a click (--real: 0.01)
    public string OwnScreen = "";   // the full screen the pilot works on: never an Escape there during its run
    // the plan's item in the hero's gear list (row 2, column 1: AutoPilot's ListGeometry) and its state: selected (the game keeps the selection
    // when its tip closes: the laptop 1.6.66.348), worn; the gear list takes item clicks only once the pilot is at the item (ItemPhase)
    public static readonly HintGeometry Hint = new HintGeometry(); public static readonly ListGeometry ItemGeo = new ListGeometry(); public static readonly int[] ItemTypes = { 0, 1, 1, 1, 1 };
    public bool ItemPhase, ItemSel, Worn;
    public double Row1Top { get { return (ItemGeo.ViewTop + ListGeometry.TopPad) * U; } }
    public double ItemX { get { return (ItemGeo.ColLeft(1) + ItemGeo.CellW / 2) * U; } }
    public double ItemY { get { return Row1Top + (ItemGeo.RowOffset(ItemTypes, 2) + ItemGeo.CellW / 2) * U; } }
    readonly List<KeyValuePair<long, Action>> due = new List<KeyValuePair<long, Action>>();
    // the slow laptop: the reader finds the inventory / Foundry form some seconds after it first opens; a lost form is searched for 17 s
    readonly Dictionary<string, long> findAt = new Dictionary<string, long>();
    public long LostFrom = -1, LostTo = -1;
    // a known screen's own part the game builds within the first seconds (a form the screen map does not know): never a pop-up on top
    readonly Dictionary<string, string> latePart = new Dictionary<string, string>(); readonly Dictionary<string, long> latePartAt = new Dictionary<string, long>();
    readonly Dictionary<string, int> order = new Dictionary<string, int>(); readonly Dictionary<string, long> openAt = new Dictionary<string, long>();
    // the window table is read every 2 s (and fresh when the screen changes): a form shown longer than this before a click had to be seen
    const long SeenWithinMs = 2600;
    public long Now;
    // the window table's shown screens (ScreenInputs.Screens): read every tick, it fails far less often than the readers (its own stream: the
    // game's stays the seeds'); SubFailP: the readers' extra failures while the table reads (live 1.6.66.397: "read failed for inventory!"
    // with the inventory shown in the table); NoTable: the app without the window table (the readers alone decide the kind)
    public double SubFailP; public bool NoTable; public int KindFlips; ScreenKind lastKind = ScreenKind.Unknown;
    readonly Random tableR, subR, orderR;
    /// <summary>The window table's order (ScreenInputs.Order): not certain in OrderFailP of the reads; NoOrder: never read (the app before it).</summary>
    public double OrderFailP = 0.05; public bool NoOrder;
    /// <summary>Every shown form, the top one first (the stack's order; a part a known screen builds late lies right over it).</summary>
    public List<string> Order() {
      var l = new List<string>(); string part; long at;
      for (int i = Stack.Count - 1; i >= 0; i--) {
        if (i == FullIndex && latePart.TryGetValue(Stack[i], out part) && latePartAt.TryGetValue(Stack[i], out at) && Now >= at) l.Add(part);
        l.Add(Stack[i]);
      }
      return l;
    }
    /// <summary>The tracker's screen this tick against the game's top screen: a known screen the app called Unknown (or another one) is a flip.</summary>
    public void NoteKind(ScreenKind k) {
      var want = ScreenState.KindOfForm(Full);
      if (want != ScreenKind.Unknown && k != want && lastKind == want && TopForm() == Full) KindFlips++;
      lastKind = k;
    }
    public SimGame(int seed, bool slow, double failP, double w = 1920, double h = 1009) {
      W = w; H = h;
      R = new Random(seed); R2 = new Random(seed * 31 + 7); fieldR = new Random(seed ^ 0x5F17); tableR = new Random(seed ^ 0x7AB1); subR = new Random(seed ^ 0x3C55); orderR = new Random(seed ^ 0x6D21); Slow = slow; FailP = failP; U = Ui.Unit(W, H);
      Hero = new FakeHeroGame(hg, W, U);
      var all = new List<long>(); for (int i = 0; i < 30; i++) all.Add(100 + i);
      Hero.All = all.ToArray(); Hero.Heroes = all.ToArray(); Hero.CityHero = 100; Hero.Tab = R.Next(2) == 0 ? 3 : 1;
      InvGeo = SellGeometry.Inventory(W, U, InvCols);
      { var gr = new Random(seed ^ 0x2B7D); InvGainUp = 0.9 + 0.2 * gr.NextDouble(); InvGainDown = 0.78 + 0.17 * gr.NextDouble(); }   // (its own stream: the game's stays the seeds')
      int ni = 20 + R.Next(60); for (int i = 0; i < ni; i++) InvItems.Add(9000 + i);
      int na = 3 + R.Next(40); for (int i = 0; i < na; i++) { long u = 600 + i; AscAll.Add(u); AscSlot[u] = R.Next(5); }
      // (a third: a long list - the slot filter leaves rows to scroll, live 1.6.66.354)
      if (R2.Next(3) == 0) { int nb = 100 + R2.Next(150); for (int i = 0; i < nb; i++) { long u = 20000 + i; AscAll.Add(u); AscSlot[u] = R2.Next(5); } }
      AscAll.Insert(R.Next(AscAll.Count + 1), SimItem); AscSlot[SimItem] = 3;
    }
    public List<long> AscShown() { var l = new List<long>(); foreach (var u in AscAll) if (AscParts.Count == 0 || AscParts.Contains(AscSlot[u])) l.Add(u); return l; }
    double InvPitch { get { return InvGeo.PitchY * U; } }
    double InvMaxScroll { get { int rows = (InvItems.Count + InvCols - 1) / InvCols; double px = rows * InvPitch + InvGeo.TopPad * U - (InvGeo.ViewBottomPx(H, U) - InvGeo.ViewTopPx(U)); return Math.Max(0, px) * InvRowUnits / InvPitch; } }
    double AscK { get { return U / GameGeometry.CanvasH; } }
    double AscMaxScroll { get { var v = Geo.Rect("ascend.list", W, H, U); int rows = (AscShown().Count + AscCols - 1) / AscCols; return Math.Max(0, rows * AscRowUnits - v.H / AscK); } }
    /// <summary>The Foundry's state as the game holds it (also the truth the game's own clicks are judged on).</summary>
    public RecastScreen Rs() {
      var parts = new List<int>(AscParts); parts.Sort();
      return new RecastScreen { SublimingOpen = true, CurType = FoundryTab, AscendShown = FoundryTab == 2, AscendEquips = FoundryTab == 2 ? AscShown().ToArray() : new long[0],
                                AscendSel = FoundryTab == 2 ? AscSel : 0, AscendParts = parts.ToArray(),
                                AscendList = FoundryTab == 2 ? new RFX.ListRead { ScrollY = AscScrollY, Cols = AscCols, RowUnits = AscRowUnits } : null };
    }
    public InventoryScreen Inv() {
      var inv = new InventoryScreen { Open = true, EquipTab = InvEquipTab && !InvArtTab, Bulk = InvBulk, BulkKnown = true, ListPtr = InvPtr, ArtTab = InvArtTab, ArtBulkKnown = true, ArtMode = -1 };
      if (InvEquipTab) { inv.Uids.AddRange(InvItems); foreach (var u in InvSel) inv.Selected.Add(u); inv.List = new RFX.ListRead { ScrollY = InvScrollY, Cols = InvCols, RowUnits = InvRowUnits }; }
      else if (InvArtTab && InvArts.Count > 0) { inv.Uids.AddRange(InvArts); inv.ArtSel = InvArtSel; inv.ArtSelKnown = true; inv.List = new RFX.ListRead { ScrollY = 0, Cols = ArtCols, RowUnits = ArtRowUnits }; }
      return inv;
    }
    double InvRow1 { get { return InvGeo.ViewTopPx(U) + InvGeo.TopPad * U - InvScrollY * InvPitch / InvRowUnits; } }
    void Say(string t) { Log.Add(Now + " " + t); if (Log.Count > 60) Log.RemoveAt(0); }
    public void Note(string t) { Say(t); }
    public long Lat() { return Slow ? 600 + R.Next(900) : 80 + R.Next(170); }   // the game's answer (the fast PC: within the click's own send time)
    // the reader's costs (the slow laptop): DISCOVERY = finding a form the game just built - a region scan (--scan, the reader before the
    // window table was its source: 3-8 s) or one read of the window table (a tick's check, ~1 ms); a FIELD read = a known field of a known
    // node (the header and one node: tens of µs each, ~25 of them a tick). A fast PC finds every form at once either way
    public bool Scan;   // --scan: the old reader
    public long DiscoveryN, DiscoveryTotalMs, FieldReads, FieldUs;
    long DiscoveryMs() { return Scan ? 3000 + R.Next(5000) : 1 + R.Next(2); }
    /// <summary>The µs of one tick's field reads (counted, not waited: they end within the tick).</summary>
    public void FieldTick() { FieldReads++; FieldUs += Slow ? 400 + fieldR.Next(1200) : 60 + fieldR.Next(140); }
    readonly Random fieldR;   // (its own: the game's stream stays the one the seeds were made with)
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
    // the city «Инвентарь»'s live rect (uibtn; LiveButtons on): read while the city is on top; it moves while a new bar comes in (BarInMs)
    public bool LiveButtons;
    public double[] CityLiveRect() {
      if (!CityClear) return null;
      double slide = CityRebuild && Now - CityBuiltAt < BarInMs ? (BarInMs - (Now - CityBuiltAt)) * 0.05 : 0;
      return new[] { W - 1.45 * U, H - 0.17 * U + slide, 0.11 * U, 0.11 * U };
    }
    public void Push(string f) {
      Stack.Add(f); Say("open " + f); openAt[f] = Now;
      if (ScreenMap.ClassOf(f) == 'W') { long ms = 2000 + R.Next(8000); Later(ms, () => Remove(f)); }   // a loading / result screen goes by itself
      if (f == SimFoundry) FoundryBuilt = true;
      if (Slow && (f == SimInv || f == SimFoundry) && !findAt.ContainsKey(f)) { long d = DiscoveryMs(); findAt[f] = Now + d; DiscoveryN++; DiscoveryTotalMs += d; }
      if (!Slow && !findAt.ContainsKey(f)) findAt[f] = Now;
      if ((f == SimHero || f == SimInv || f == SimFoundry) && R.Next(3) == 0) { latePart[f] = f + "_SimPart"; latePartAt[f] = Now + 200 + R.Next(2500); }
      else if (f == SimHero || f == SimInv || f == SimFoundry) latePart.Remove(f);
    }
    void Pop(string f) { int i = Stack.LastIndexOf(f); if (i > 0) { Stack.RemoveAt(i); Say("close " + f); Rebuilt(f); var pp = Popped; if (pp != null) pp(f); } }
    /// <summary>A form was closed by a click (the login chain scenario opens the next pop-up then).</summary>
    public Action<string> Popped;
    /// <summary>Called before a click is judged (a pop-up the game opens in that very moment takes the click).</summary>
    public Action<ClickTarget> BeforeClick;
    /// <summary>A form opened after <paramref name="ms"/> (the scenarios' own pop-ups).</summary>
    public void PushLater(long ms, string f) { Later(ms, () => Push(f)); }
    /// <summary>The game's login pop-up queue: a pop-up opens over the city only (Form_MainCity's queue); elsewhere it waits for the city.</summary>
    public void PushOnCity(long ms, string f) { Later(ms, () => { if (Full == SimCity) Push(f); else PushOnCity(200, f); }); }   // (an overlay above it stays)
    void Rebuilt(string f) { if (CityRebuild && f.StartsWith("Form_Union") && Top == SimCity) { CityInst++; CityBuiltAt = Now; Say("city built again (" + CityInst + ")"); } }
    void Remove(string f) { int i = Stack.LastIndexOf(f); if (i > 0) { Stack.RemoveAt(i); Say("gone " + f); } }
    string TopForm() { for (int i = Stack.Count - 1; i >= 0; i--) if (ScreenMap.ClassOf(Stack[i]) != 'O') return Stack[i]; return SimCity; }
    /// <summary>Now and then a pop-up the game opens by itself (a reward, a notice, a loading).</summary>
    void MaybeSpawn() {
      if (R.NextDouble() >= SpawnP) return;
      if (SimGraph) {   // the forms the game opens by itself (the UI graph): ActivityFormMgr's queue over the city, a data module's / an action's result anywhere
        string gf = SimGraphAuto(R, Full == SimCity);
        if (gf != null) Later(500 + R.Next(2500), () => { if (!ScreenMap.IsFlow(FullKind, gf) && (Full == SimCity || !UiGraph.ByName(gf).AutoIn("activity:city"))) { SimGraphSeen.Add(gf); Push(gf); } });
        return;
      }
      string f = SimPick(R, R.Next(3) == 0 ? 'W' : R.Next(2) == 0 ? 'P' : 'E');
      // (never a pilot's flow form of the screen it would open over: the game opens those only from that screen's own buttons - the app takes them
      // for a sub-state of the screen, ScreenMap.FlowForms)
      if (f != null && (ScreenMap.ClassOf(f) != 'E' || !CityClear)) Later(500 + R.Next(2500), () => { if (!ScreenMap.IsFlow(FullKind, f)) Push(f); });
    }
    ScreenKind FullKind { get { string f = Full; return f == SimHero ? ScreenKind.HeroScreen : f == SimInv ? ScreenKind.Inventory : f == SimFoundry ? ScreenKind.Foundry : f == SimGem ? ScreenKind.Gem : f == SimWs ? ScreenKind.ArtWorkshop : ScreenKind.Unknown; } }

    public bool Searching(string f) { long t; return findAt.TryGetValue(f, out t) && Now < t && Stack.Contains(f); }
    bool Found(string f) { long t; return (!findAt.TryGetValue(f, out t) || Now >= t) && !(Now >= LostFrom && Now < LostTo); }
    bool Fail() { return R.NextDouble() < FailP; }

    /// <summary>What the app's readers give this tick (memory: the known forms; the window table: the offers).</summary>
    public ScreenInputs Read() {
      var i = new ScreenInputs { Offers = Fail() ? null : new List<string>(), ShownKnown = "" };
      if (i.Offers != null) foreach (var f in Stack) if (OfferForms.IsOffer(f)) i.Offers.Add(f);
      string full = Full;
      i.CityForm = full == SimCity;
      bool heroOn = full == SimHero;
      // (a failed read of the hero screen is no reading, never "no hero screen")
      if (Stack.Contains(SimHero)) { if (!Fail()) i.Hero = new HeroScreen { Hero = Hero.Shown, FormOk = heroOn, Tab = Hero.Tab, Part = Hero.Part, Heroes = Hero.Heroes, GridPtr = Hero.Grid }; }
      else if (!Fail()) i.Hero = new HeroScreen { Hero = 0, FormOk = false };
      if ((full == SimInv || full == SimFoundry) && Found(SimInv) && !Fail())
        i.Inv = Inv();
      if (full == SimFoundry && Found(SimFoundry) && !Fail())
        i.Rs = Rs();
      // the gem screen and the workshop (opened from the inventory: its reader still gives it under them)
      if ((full == SimGem || full == SimWs) && Stack.Contains(SimInv) && Found(SimInv) && !Fail()) i.Inv = Inv();
      if (full == SimGem && !Fail()) i.Rs = new RecastScreen { GemOpen = true, GemFunc = 0 };
      else if (full != SimFoundry && FoundryBuilt && i.Rs == null && !Fail()) i.Rs = new RecastScreen { SublimingOpen = false, CurType = -1 };
      if (full == SimWs && !Fail()) i.ArtWs = new ArtWorkshopScreen { Open = true, Tab = 1, Uid = WsUid };   // (555: another artifact's workshop)
      if (SubFailP > 0) { if (subR.NextDouble() < SubFailP) i.Inv = null; if (subR.NextDouble() < SubFailP) i.Rs = null; if (subR.NextDouble() < SubFailP) i.Hero = null; if (subR.NextDouble() < SubFailP) i.ArtWs = null; }
      if (!NoTable && !Scan && tableR.NextDouble() >= FailP / 4) {   // (--scan: the reader before the window table)
        i.Screens = new List<string>();
        if (ScreenState.KindOfForm(full) != ScreenKind.Unknown) i.Screens.Add(full);
        if ((full == SimGem || full == SimWs || full == SimFoundry) && Stack.Contains(SimInv)) i.Screens.Add(SimInv);   // (the inventory under them)
        if (!NoOrder && orderR.NextDouble() >= OrderFailP) i.Order = Order();   // (the forms' places: now and then not certain - a form's place unread, two at one place)
      }
      return i;
    }
    /// <summary>The shown forms nobody knows (the window table, in no order); null = the read failed.</summary>
    public List<string> Unknown() {
      if (Fail()) return null;
      var l = new List<string>();
      foreach (var f in Stack) if (Array.IndexOf(OfferForms.KnownScreens, f) < 0 && !OfferForms.IsOffer(f)) l.Add(f);
      string part; long at;   // a form the known screen on top builds a moment after it opens (no pop-up: a part of the screen)
      if (latePart.TryGetValue(Full, out part) && latePartAt.TryGetValue(Full, out at) && Now >= at) l.Add(part);
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

    static bool In(GeoRect r, double x, double y) { return r != null && x >= r.X && x <= r.Right && y >= r.Y && y <= r.Bottom; }
    bool Near(double x, double y, double px, double py, double tol) { return Math.Abs(x - px) <= tol && Math.Abs(y - py) <= tol; }
    bool OnBack(double x, double y) { return Near(x, y, hg.BackX * U, hg.BackY * U, 15); }

    /// <summary>A click: judged on what is on top now, its effect shows after the game's latency.</summary>
    public void Click(double x, double y, ClickTarget tg) {
      var bc = BeforeClick; if (bc != null) bc(tg);
      string top = TopForm(); char c = ScreenMap.ClassOf(top);
      if (c == 'D') { Bad.Add("CLICK ON A DANGEROUS FORM " + top + " @" + (int)x + "," + (int)y + " (" + tg + ")"); return; }
      if (ScreenMap.OnNever(top, x, y, W, H, U)) { Bad.Add("CLICK ON A NEVER-CLICK BUTTON of " + top + " @" + (int)x + "," + (int)y + " (" + tg + ")"); return; }
      Say("click " + tg + " @" + (int)x + "," + (int)y + " on " + top); Acts.Append(Now + " c" + (int)x + "," + (int)y + ";");
      // the equip flow's item tip (a flow form of the hero screen, the pilots go on under it): a click beside it closes it and is taken by it
      if (top == SimTip && Full == SimHero && tg != ClickTarget.EquipConfirm) { Later(Lat(), () => { if (TopForm() == SimTip) Pop(SimTip); }); return; }
      // a click meant for a screen that lands on a pop-up / screen opened over it since the start, long enough ago for the app to have seen it
      long oa; if (ScreenTarget(tg) && c != 'K' && !StartForms.Contains(top) && "PEF".IndexOf(c) >= 0 && openAt.TryGetValue(top, out oa) && Now - oa > SeenWithinMs)
        Bad.Add("A " + tg + " CLICK LANDED ON " + top + " (" + c + ") opened over the screen after the start @" + (int)x + "," + (int)y);
      if (OfferForms.IsOffer(top)) {
        if (OfferForms.OnNeverButton(top, x, y, W, H, U)) { Bad.Add("CLICK ON A PLAN BUTTON of " + top + " @" + (int)x + "," + (int)y + " (" + tg + ")"); return; }
        long l0 = Lat(); if (OfferForms.OnClose(Geo, top, x, y, W, H, U)) Later(l0, () => Pop(top));
        return;
      }
      long lat = Lat(); MaybeSpawn();
      if (c == 'F' || c == 'P' || c == 'U') {
        if (c != 'U' && ScreenMap.OnExit(top, x, y, W, H, U)) Later(lat, () => Pop(top));
        else if (c == 'U' && OnBack(x, y)) Later(lat, () => Pop(top));
        else if (SimGraph) GraphPress(top, x, y, lat, tg);
        return;
      }
      if (SimGraph && c != 'K' && c != 'W') { GraphPress(top, x, y, lat, tg); if (Bad.Count > 0) return; }
      // the item's tip: «Заменить» puts the item on (the tip closes)
      if (top == SimTip && Full == SimHero && tg == ClickTarget.EquipConfirm) {
        if (In(new GeoRect { X = SimGame.Hint.Replace(W, H)[0], Y = SimGame.Hint.Replace(W, H)[1], W = SimGame.Hint.Replace(W, H)[2], H = SimGame.Hint.Replace(W, H)[3] }, x, y)) Later(lat, () => { if (TopForm() == SimTip) { Pop(SimTip); Worn = true; ItemSel = false; } });
        return;
      }
      if (c != 'K') return;   // E: only the back key closes it; W: nothing takes clicks
      if (top == SimCity) {
        if (tg == ClickTarget.CityInventory && Now - CityBuiltAt < BarInMs) { LostPresses++; Say("the bar is still coming in: the press is lost"); return; }
        if (tg == ClickTarget.CityInventory && Near(x, y, W - (0.5222 + 0.9316) * U, H - hg.HeroesBtnDY * U, Math.Min(20, 0.055 * U))) {
          // now and then the bar has moved and the click takes «Сборка» next to «Инвентарь»: «План сборки» opens (a full screen of the offer list)
          if (R.Next(25) == 0) { Later(lat, () => { if (OfferForms.IsOffer(TopForm())) { Say("the press was taken by " + TopForm()); return; } Push("Form_EquipmentPlan"); }); return; }
          Later(lat, () => {
            if (OfferForms.IsOffer(TopForm())) { Say("the press was taken by " + TopForm() + " (it came in the press's time)"); return; }   // (live 1.6.66.393)
            InvEquipTab = R.Next(2) == 0; InvBulk = false; InvArtTab = false; InvSel.Clear(); InvScrollY = 0; Push(SimInv);
          }); return;
        }
        if (Near(x, y, W - hg.HeroesBtnDX * U, H - hg.HeroesBtnDY * U, 20)) Later(lat, () => { if (TopForm() != SimCity) return; Hero.Part = -1; Hero.Shown = Hero.CityHero; Push(SimHero); });
        return;
      }
      // the item cell: selects it and opens its tip; on the selected item with no tip, the game opens the tip or clears the selection
      if (top == SimHero && ItemPhase && tg == ClickTarget.EquipNav && Near(x, y, ItemX, ItemY, 6)) {
        Later(lat, () => {
          if (TopForm() != SimHero) return;
          if (!ItemSel || R.Next(2) == 0) { ItemSel = true; Push(SimTip); } else ItemSel = false;
        });
        return;
      }
      if (top == SimHero) {
        // the gear tab's neighbour «План сборки» (a click a little off it opens the build plan screen)
        if (Near(x, y, W - hg.GearTabDX * U, hg.GearTabY * U, 20) && R.Next(25) == 0) { Later(lat, () => Push("Form_EquipmentPlan")); return; }
        Later(lat, () => {
          if (TopForm() != SimHero) return;
          Hero.City = false; Hero.Blocked = false; Hero.Click(x, y);
          if (Hero.Shown == 0) Pop(SimHero);
        });
        return;
      }
      if (top == SimInv) {
        if (OnBack(x, y)) { Later(lat, () => Pop(SimInv)); return; }
        if (Near(x, y, 0.085 * U, 0.315 * U, 20)) { Later(lat, () => { InvEquipTab = true; InvBulk = false; InvArtTab = false; }); return; }
        var atr = ArtFrames.Rect(Geo, "inventory.leftTab.artifact", W, H, U);
        if (InvArtTab && InvArts.Count > 0 && tg == ClickTarget.ArtCell) {
          int ci = ClickGate.ListCardIndexAt(Geo, "inventory.artList", Inv().List, ArtCols, ArtRowUnits, x, y, W, H, U);
          if (ci >= 0 && ci < InvArts.Count) Later(lat, () => { if (TopForm() == SimInv && InvArtTab) InvArtSel = ci + 1; });
          return;
        }
        if (InvArtTab && tg == ClickTarget.ArtWorkshop) {   // only a press on the button where it is now opens the workshop
          var wl = WorkshopLive();
          if (wl != null && x >= wl[0] && x <= wl[0] + wl[2] && y >= wl[1] && y <= wl[1] + wl[3]) { long wu = InvArts[InvArtSel - 1]; Later(lat, () => { if (TopForm() == SimInv) { WsUid = wu; Push(SimWs); } }); }
          return;
        }
        if (tg == ClickTarget.ArtTab && In(atr, x, y)) { if (InvBulk) { Bad.Add("ArtTab CLICK IN THE EQUIPMENT BULK SALE"); return; } Later(lat, () => { InvArtTab = true; InvEquipTab = false; }); return; }
        var bs = Geo.Rect("inventory.bulkSell", W, H, U); var bb = Geo.Rect("inventory.bulkBack", W, H, U);
        if (InvEquipTab && InvBulk) {
          // «Массовая продажа»: the same button is «Продать» now (never pressed), «Назад» leaves the mode, a cell ticks / unticks its item
          if (In(bs, x, y)) { Bad.Add("CLICK ON «Продать» (bulk sale) @" + (int)x + "," + (int)y + " (" + tg + ")"); return; }
          if (In(bb, x, y)) { Later(lat, () => { InvBulk = false; InvSel.Clear(); }); return; }
          long u = ClickGate.InvCellUid(Inv(), InvGeo, InvRow1, InvCols, x, y, W, U);
          if (u != 0) Later(lat, () => { if (TopForm() == SimInv && InvBulk) { if (!InvSel.Remove(u)) InvSel.Add(u); } });
          return;
        }
        if (InvEquipTab && Near(x, y, 0.5867 * U, H - 0.0723 * U, 20)) { Later(lat, () => { InvBulk = true; InvSel.Clear(); }); return; }
        if (tg == ClickTarget.FoundryButton) { Later(lat, () => { FoundryTab = R.Next(2) == 0 ? 0 : 3; Push(SimFoundry); }); return; }
        return;
      }
      if (top == SimGem || top == SimWs) { if (OnBack(x, y)) Later(lat, () => Pop(top)); return; }
      if (top == SimFoundry) {
        if (OnBack(x, y)) { Later(lat, () => Pop(SimFoundry)); return; }
        if (tg == ClickTarget.AscendTab) { Later(lat, () => { FoundryTab = 2; AscSel = 0; AscScrollY = 0; }); return; }
        if (FoundryTab == 2) {
          var ab = Geo.Rect("ascend.button", W, H, U); if (In(ab, x, y)) { Bad.Add("CLICK ON «Вознести» @" + (int)x + "," + (int)y + " (" + tg + ")"); return; }
          var st = Geo.Rect("ascend.slot.3", W, H, U);
          if (In(st, x, y)) { Later(lat, () => { if (!AscParts.Remove(3)) AscParts.Add(3); AscScrollY = 0; }); return; }
          // (the game takes the press when it gets it: a list still coasting has moved under the point by then)
          Later(lat, () => {
            if (TopForm() != SimFoundry || FoundryTab != 2) return;
            long u = ClickGate.AscendCellUid(Geo, Rs(), x, y, W, H, U);
            if (u != 0 && AscShown().Contains(u)) AscSel = u;
          });
        }
      }
    }

    /// <summary>--graph: a press on a form the app does not read: the widget under it (shown in the prefab, not a list template; the
    /// smallest one) does what the client's code does - opens its form, closes its own; a dangerous one (a purchase / claim / sell / consume
    /// request, a confirm) breaks the run.</summary>
    void GraphPress(string top, double x, double y, long lat, ClickTarget tg) {
      var w = SimGraphWidgetAt(top, x, y, W, H, U); if (w == null) return;
      GraphPresses++; SimGraphPressN++; if (w.Danger) SimGraphDangerN++;
      // (a request that buys / claims / sells / consumes breaks the run; a confirm opens - the pilot stops on it, nothing is spent yet)
      if (w.Danger) { Bad.Add("CLICK ON A DANGEROUS WIDGET of " + top + ": " + w.Path + " @" + (int)x + "," + (int)y + " (" + tg + ")"); return; }
      if (w.Confirm) SimGraphConfirmN++;
      Say("graph: " + top + " [" + w.Path + "]");
      foreach (var t in w.Opens()) {
        var tf = UiGraph.ByName(t); if (tf == null || Array.IndexOf(OfferForms.KnownScreens, t) >= 0) continue;
        string to = t; Later(lat, () => { if (TopForm() == top) { SimGraphSeen.Add(to); Push(to); } }); return;
      }
      if (w.Closes) Later(lat, () => Pop(top));
    }
    public int GraphPresses;

    /// <summary>A drag: the hero grid follows it; on anything else it is a press on its start point.</summary>
    public void Drag(double x, double y, double dy, ClickTarget tg) {
      string top = TopForm();
      if (top == SimInv && InvBulk || top == SimFoundry && FoundryTab == 2) {   // a list follows the pointer
        Say("drag " + tg + " @" + (int)x + "," + (int)y + " by " + (int)dy + " on " + top); Acts.Append(Now + " d" + (int)x + "," + (int)y + "," + (int)dy + ";");
        bool inv = top == SimInv;
        long lat0 = Lat();
        // the slow laptop's Ascension list coasts: most of the move first, the rest 0.4-0.9 s later (live 1.6.66.354: the cell placed from
        // the first read, the click changed nothing)
        double coast = !inv && Slow ? 0.25 + 0.2 * R2.NextDouble() : 0; long coastMs = 400 + R2.Next(500);
        Later(lat0, () => {
          if (TopForm() != top) return;
          if (inv) InvScrollY = Math.Max(0, Math.Min(InvMaxScroll, InvScrollY - dy * (dy < 0 ? InvGainUp : InvGainDown) * InvRowUnits / InvPitch));
          else AscScrollY = Math.Max(0, Math.Min(AscMaxScroll, AscScrollY - dy * (1 - coast) / AscK));
        });
        if (coast > 0) Later(lat0 + coastMs, () => { if (TopForm() == top) AscScrollY = Math.Max(0, Math.Min(AscMaxScroll, AscScrollY - dy * coast / AscK)); });
        return;
      }
      if (top != SimHero) { Click(x, y, tg); return; }
      Say("drag " + tg + " @" + (int)x + "," + (int)y + " by " + (int)dy); Acts.Append(Now + " d" + (int)x + "," + (int)y + "," + (int)dy + ";");
      Later(Lat(), () => { if (TopForm() == SimHero) Hero.Drag(dy); });
    }

    static bool ScreenTarget(ClickTarget t) {
      switch (t) {
        case ClickTarget.EquipNav: case ClickTarget.InvBulkCell: case ClickTarget.InvListDrag: case ClickTarget.InvEquipTab: case ClickTarget.InvBulkEnter:
        case ClickTarget.FoundryButton: case ClickTarget.AscendTab: case ClickTarget.SlotTab: case ClickTarget.AscendCell: case ClickTarget.AscendDrag: case ClickTarget.ArtTab: return true;
      }
      return false;
    }

    /// <summary>The Escape key: closes an E / P / F form (and the app's screens); on the city the game asks to quit.</summary>
    public void Escape() {
      string top = TopForm(); char c = ScreenMap.ClassOf(top);
      Say("ESCAPE on " + top); Acts.Append(Now + " e;");
      if (c == 'D') { Bad.Add("ESCAPE ON A DANGEROUS FORM " + top); return; }
      if (Stack.Contains(SimTip) && Full == SimHero) Bad.Add("ESCAPE WITH THE EQUIP FLOW'S TIP SHOWN (" + top + ")");
      else if (OwnScreen != "" && Full == OwnScreen) Bad.Add("ESCAPE ON THE PILOT'S OWN SCREEN " + OwnScreen + " (" + top + ")");
      long lat = Lat();
      if (top == SimCity) { CityEscapes++; Bad.Add("ESCAPE ON THE CITY (the game asks to quit)"); Later(lat, () => Push("Form_SystemTips")); return; }
      if (c == 'W') return;
      if (top == SimHero) { Later(lat, () => { if (Hero.Part >= 0) Hero.Part = -1; else { Hero.Shown = 0; Pop(SimHero); } }); return; }
      if (top == SimInv && InvBulk) { Later(lat, () => { InvBulk = false; }); return; }
      Later(lat, () => Pop(top));
    }
  }

  /// <summary>--order-fail P: the share of window table reads whose z-order is not certain (-1 = the game's default).</summary>
  static double SimOrderFailP = -1;
  /// <summary>--graph: the game sim's forms, transitions and pop-ups come from the UI graph (src/UiGraphData.cs).</summary>
  static bool SimGraph;
  /// <summary>The forms a --graph run showed (its starts, the pop-ups, the forms its presses opened): the coverage of the report.</summary>
  static readonly HashSet<string> SimGraphSeen = new HashSet<string>();
  static List<string> simAutoCity, simAutoAny; static List<string> simReach; static long SimGraphPressN, SimGraphDangerN, SimGraphConfirmN;
  static bool SimGraphUsable(string f) {
    return Array.IndexOf(OfferForms.KnownScreens, f) < 0 && !OfferForms.IsOffer(f) && Array.IndexOf(OfferForms.IgnoredOverlays, f) < 0 && ScreenMap.ByName(f) != null;
  }
  static void SimGraphInit() {
    if (simReach != null) return;
    simAutoCity = new List<string>(); simAutoAny = new List<string>();
    foreach (var f in UiGraph.All) {
      if (!f.IsAuto) continue;
      // (the city's queue: its offers too - the sim's offer pop-ups, closed by their own ×)
      if (f.AutoIn("activity:city") && (SimGraphUsable(f.Name) || OfferForms.IsOffer(f.Name))) simAutoCity.Add(f.Name);
      if (!SimGraphUsable(f.Name)) continue;
      foreach (var a in f.Auto) if (a.StartsWith("data:") || a.StartsWith("result:")) { simAutoAny.Add(f.Name); break; }
    }
    simReach = new List<string>();
    foreach (var kv in UiGraph.Reachable(SimCity)) if (kv.Key != SimCity && SimGraphUsable(kv.Key)) simReach.Add(kv.Key);
    simReach.Sort(StringComparer.Ordinal);
  }
  /// <summary>A form the game opens by itself: over the city its pop-up queue's or a data module's, elsewhere a data module's / an action's result.</summary>
  static string SimGraphAuto(Random r, bool city) {
    SimGraphInit();
    var l = city && r.Next(2) == 0 ? simAutoCity : simAutoAny;
    return l.Count == 0 ? null : l[r.Next(l.Count)];
  }
  /// <summary>The widget of the form under the point (shown in the prefab, not a list template; the smallest one).</summary>
  static UiGraph.Widget SimGraphWidgetAt(string form, double x, double y, double W, double H, double U) {
    var f = UiGraph.ByName(form); if (f == null) return null;
    UiGraph.Widget best = null; double ba = double.MaxValue;
    foreach (var w in f.Widgets) {
      if (!w.Shown || w.Template || w.Rect == null) continue;
      var r = ScreenMap.Place(w.Rect, W, H, U);
      if (r.W < 2 || r.H < 2 || r.W > W * 0.9 && r.H > H * 0.9) continue;   // (a full-screen tap catcher is the screen map's exit, not a widget)
      if (x >= r.X && x <= r.Right && y >= r.Y && y <= r.Bottom && r.W * r.H < ba) { best = w; ba = r.W * r.H; }
    }
    return best;
  }
  /// <summary>A --graph start: the forms of a safe path from the city to a reachable form, pushed one after the other.</summary>
  static void SimGraphStart(SimGame g, Random r) {
    SimGraphInit(); if (simReach.Count == 0) return;
    string goal = simReach[r.Next(simReach.Count)];
    var p = UiGraph.Path(SimCity, goal); if (p == null) return;
    foreach (var s in p) {
      if (s.To == SimHero) { g.Hero.Shown = 100 + r.Next(30); g.Push(SimHero); }
      else if (s.To == SimInv) { g.InvEquipTab = r.Next(2) == 0; g.Push(SimInv); }
      else if (s.To == SimFoundry) { g.FoundryTab = new[] { 0, 2, 3 }[r.Next(3)]; g.Push(SimFoundry); }
      else if (Array.IndexOf(OfferForms.KnownScreens, s.To) >= 0 || OfferForms.IsOffer(s.To)) return;   // (the app's own screens / the offers: the hand-made starts have them)
      else { SimGraphSeen.Add(s.To); g.Push(s.To); }
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
    // the city bar's place as app/Overlay.cs keeps it (InventoryButtonSeen): dropped when the city is built again or «Инвентарь» changed nothing
    readonly CityBar bar = new CityBar();
    bool CityButtonSeen() {
      if (G.LiveButtons && cityLive.Note(G.CityLiveRect(), Now)) { if (CityReadyAt < 0) CityReadyAt = Now; return true; }   // (app/Overlay.cs CityLiveStill: the live rect held still, no bar search)
      if (G.LiveButtons && cityLive.Fresh) return false;   // (it still moves)
      bar.Note(G.CityInst); bar.MustSearch = false; return bar.Look(G.Full == SimCity && G.CityClear, Now);   // (the search along the bar finds it at once here)
    }
    readonly LiveStill cityLive = new LiveStill(); public long CityReadyAt = -1, UnknownFromAt = -1;
    public string PilotState = "";
    public string HeroWhy { get { return hp.Why; } }
    public long SendingUntil;   // app/Overlay.cs Send: the cursor moves and the click take ~300 ms on their own thread; no tick reads the screens meanwhile
    readonly SellPilot spilot = new SellPilot(new SellGeometry()); readonly InvScroll sellScroll = new InvScroll(); public readonly HashSet<long> Wanted = new HashSet<long>();
    public string SellWhy { get { return spilot.FailWhy; } }
    public bool SellPolling { get { return Owner == ClickOwner.Sell && spilot.Polling; } }   // (app/Overlay.cs: the 50 ms read while the sell pilot awaits a move)
    public readonly PilotFlow Flow;
    public static ClickOwner OwnerOf(PilotFlow f) {
      return f == PilotFlow.Equip || f == PilotFlow.ArtEquip ? ClickOwner.Equip : f == PilotFlow.Sell || f == PilotFlow.ArtSell ? ClickOwner.Sell : ClickOwner.Locate;
    }
    public SimHost(SimGame g, PilotFlow flow, long plan, GameGeometry geo) {
      if (SimGateLog) Gate.Sink = x => g.Note("gate: " + x);
      G = g; Flow = flow; Owner = OwnerOf(flow); this.plan = plan; this.geo = geo;
      spilot.Geometry = g.InvGeo;
      int nw = 1 + g.R.Next(8); while (Wanted.Count < nw) Wanted.Add(g.InvItems[g.R.Next(g.InvItems.Count)]);
      string purpose = flow == PilotFlow.LocateRecast ? "recast" : flow == PilotFlow.LocateArtPromote ? "artPromote" : "ascend";
      req = new LocateRequest { Id = "sim", Purpose = purpose, Item = new PlanItem { Uid = SimItem, Slot = 3, Level = 16, Stars = 5 } };
      if (flow == PilotFlow.LocateRecast) { req.Item.Quality = 8; g.InvItems.Insert(g.R2.Next(g.InvItems.Count + 1), SimItem); }   // (the recast's item is in the inventory)
      if (flow == PilotFlow.LocateArtPromote) req.Art = new ArtPromoteInfo { Uid = 4242, Id = 1, Level = 10, Cap = 10, Stage = 1 };
      Gate.Flows[Owner] = flow;
      Gate.ArtSellAuto = flow == PilotFlow.ArtSell; Gate.ArtLocateAuto = flow == PilotFlow.LocateArtPromote; Gate.ArtEquipAuto = flow == PilotFlow.ArtEquip;
      Gate.FormSearchPending = t => t == ClickTarget.CityInventory ? G.Searching(SimInv) : t == ClickTarget.FoundryButton && G.Searching(SimFoundry);
      Gate.FormFound = t => t == ClickTarget.CityInventory ? !G.Searching(SimInv) : t == ClickTarget.FoundryButton && !G.Searching(SimFoundry);
    }
    double W { get { return G.W; } } double H { get { return G.H; } } double U { get { return G.U; } }

    readonly OfferForms.Order offerOrder = new OfferForms.Order();
    // ReadOffersInto (app/Overlay.cs): the forms nobody knows, read every 2 s; over a known screen the ones that came later are a pop-up
    const int TopSettleMs = 3000, TopFlickerMs = 2000, KnownFormsMs = 60000, FreshFormsMs = 0, SettleReadMs = 500, UnknownReadMs = 250;   // (FreshFormsMs: the forms read again before every click - cheap with the window table; the pilots click faster now)
    List<string> knownForms, firstForms; long knownFormsAt, firstFormsAt;
    long topBaseAt, topLastAt, unknownFormsAt; ScreenKind topLastKind = ScreenKind.Unknown; ScreenKind topBaseKind = ScreenKind.Unknown; HashSet<string> topBase = new HashSet<string>();
    string topNewNow = "", topUnknownNow = "", topCityNow = ""; ScreenKind topNewKind = ScreenKind.Unknown; List<string> unknownForms;
    string topFlowNow = ""; ScreenKind topFlowKind = ScreenKind.Unknown; bool formsUnread; ScreenKind formsKind = ScreenKind.Unknown;
    bool FormsUnread(ScreenState fresh) { return formsUnread || fresh == null || !fresh.OfferRead || (fresh.Kind != ScreenKind.City && fresh.Kind != ScreenKind.Unknown && fresh.Kind != formsKind)
                                                 || fresh.ModalOpen && (Screens.Current == null || !Screens.Current.ModalOpen || Screens.Current.Held); }   // (app/Overlay.cs TickBehind)
    void ReadOffersInto(ScreenInputs inp, long now) {
      formsUnread = inp.Offers == null;
      var cls = ScreenState.Classify(inp, now).Kind;
      topNewNow = ""; topUnknownNow = ""; topCityNow = ""; topFlowNow = "";
      if (cls == ScreenKind.Unknown && formsKind != ScreenKind.Unknown) { unknownFormsAt = 0; UnknownFromAt = now; }   // just became unknown: its forms read at once
      formsKind = cls;
      if (inp.Offers == null || inp.Offers.Count == 0) {
        // the forms of the last read on the city or a known screen (before this tick's): one not in it came with the screen. A short unknown
        // stretch between (the slow laptop: the inventory's form is found seconds after it opened) is skipped: a pop-up that came then is new too
        // (before any: the first read of the run, the forms there when it began - a pop-up that came after it is new as well)
        var prevForms = knownForms != null && now - knownFormsAt <= KnownFormsMs ? knownForms : firstForms != null && now - firstFormsAt <= KnownFormsMs ? firstForms : unknownForms;
        // (every 2 s; within a known screen's settle time every SettleReadMs: a part it builds late must be read before that time ends)
        bool settling = topBaseKind != ScreenKind.Unknown && topBaseKind != ScreenKind.City && now - topBaseAt < TopSettleMs;
        if (now - unknownFormsAt >= (settling ? SettleReadMs : cls == ScreenKind.Unknown ? UnknownReadMs : 2000) || unknownFormsAt == 0) { unknownFormsAt = now; unknownForms = G.Unknown(); if (unknownForms == null) { formsUnread = true; unknownFormsAt = 0; }  else if (firstForms == null) { firstForms = unknownForms; firstFormsAt = now; } }
        var cur = unknownForms ?? new List<string>();
        ScreenKind flowOn = cls == ScreenKind.Unknown ? topLastKind : cls;
        var flow = cur.FindAll(n => ScreenMap.IsFlow(flowOn, n));
        if (flow.Count > 0) { cur = cur.FindAll(n => !ScreenMap.IsFlow(flowOn, n)); inp.Flow = topFlowNow = string.Join(",", flow.ToArray()); topFlowKind = cls; }
        if (cls == ScreenKind.Unknown) {
          if (cur.Count > 0) inp.TopUnknown = topUnknownNow = string.Join(",", cur.ToArray());
        } else if (cls == ScreenKind.City) {
          var cov = cur.FindAll(n => "PEDW".IndexOf(ScreenMap.ClassOf(n)) >= 0);
          if (unknownForms != null) { knownForms = unknownForms; knownFormsAt = now; }
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
          if (unknownForms != null) { knownForms = cur.FindAll(n => topBase.Contains(n)); knownFormsAt = now; }   // (a pop-up on top stays one on the next screen)
          if (fresh.Count > 0) { inp.TopNew = topNewNow = string.Join(",", fresh.ToArray()); topNewKind = cls; }
        }
        if (cls == ScreenKind.Unknown || cls == ScreenKind.City) topBaseKind = cls;
      } else { unknownForms = null; unknownFormsAt = 0; }   // (app/Overlay.cs: the forms read again the moment the offer list goes)
    }
    public void ReadScreens(long now) {
      Now = now;
      G.FieldTick();
      var inp = G.Read(); inp.Offers = offerOrder.Note(inp.Offers); inp.OfferTopKnown = offerOrder.TopKnown;   // (app/Overlay.cs: the newest offer on top)
      if (!ScreenState.WantArtWs(Flow == PilotFlow.LocateArtPromote, inp.Screens)) inp.ArtWs = null;   // (app/Overlay.cs WantArtWs)
      string ik = ListSettle.KeyOf(inp.Inv), rk = ListSettle.KeyOf(inp.Rs);   // (app/Overlay.cs ReadScreens)
      InvSettle.Update(ik, now); RsSettle.Update(rk, now);
      ListMoving = (ik != null && !InvSettle.Settled) || (rk != null && rk != "" && !RsSettle.Settled);
      ReadOffersInto(inp, now);
      inp.CitySeen = inp.CityForm == true && G.CityClear;
      var st = Screens.Update(inp, now);
      G.NoteKind(st.Kind);
      bool sw = now >= G.LostFrom && now < G.LostTo;
      Gate.SearchWait = sw;
      if (Screens.Hero != null) heroScreen = Screens.Hero;
      Gate.Observe(st, now);
      if (Gate.CityAgainDue.Count > 0) { Gate.CityAgainDue.Clear(); bar.Drop(); }   // (app/Overlay.cs ReadScreens)
      if (Gate.RetryDue.Count > 0) { if (Gate.RetryDue.Contains(ClickOwner.Locate)) { lp.StepAgain(); ap.StepAgain(); } if (Gate.RetryDue.Contains(ClickOwner.Equip)) hp.StepAgain(); Gate.RetryDue.Clear(); bar.Drop(); }   // (app/Overlay.cs ReadScreens: the retry)
      long le = Gate.EffectAt(ClickOwner.Locate); if (le != locEffectSeen) { locEffectSeen = le; lp.Confirmed(now); }   // (app/Overlay.cs ReadScreens)
    }
    long locEffectSeen;
    public readonly ListSettle InvSettle = new ListSettle(), RsSettle = new ListSettle(); public bool ListMoving;
    // FreshScreen (app/Overlay.cs): fresh memory reads, the forms read by the tick
    ScreenState Fresh(bool wantCity, bool wantKnownForms) {
      var inp = G.Read(); inp.Offers = offerOrder.Note(inp.Offers); inp.OfferTopKnown = offerOrder.TopKnown;   // (app/Overlay.cs: the newest offer on top)
      if (!ScreenState.WantArtWs(Flow == PilotFlow.LocateArtPromote, inp.Screens)) inp.ArtWs = null;   // (app/Overlay.cs WantArtWs)
      if (wantKnownForms) inp.ShownKnown = G.ShownKnown();
      var st = ScreenState.Classify(inp, Now);
      if (topFlowNow != "" && st.Kind == topFlowKind) { inp.Flow = topFlowNow; st = ScreenState.Classify(inp, Now); }
      if (topNewNow != "" && st.Kind == topNewKind) { inp.TopNew = topNewNow; st = ScreenState.Classify(inp, Now); }
      else if (topUnknownNow != "" && st.Kind == ScreenKind.Unknown) { inp.TopUnknown = topUnknownNow; st = ScreenState.Classify(inp, Now); }
      else if (topCityNow != "" && st.Kind == ScreenKind.City) { inp.TopCity = topCityNow; st = ScreenState.Classify(inp, Now); }
      // a pop-up or screen that came since the tick's read of the forms (every 2 s) and before this click: it lies on top. No click now (the
      // forms count as unread); the next tick takes the new read and closes it first as any pop-up (a gate check now would stop the run on it)
      if (unknownForms != null && Now - unknownFormsAt >= FreshFormsMs && (inp.Offers == null || inp.Offers.Count == 0)) {
        var fl = G.Unknown();
        if (fl != null && ScreenMap.Newcomers(unknownForms, fl) != "") { unknownForms = fl; unknownFormsAt = Now; formsUnread = true; }
      }
      if (wantCity && ScreenState.NeedsCityPicture(st.Kind, inp.CityForm)) {
        bool seen = G.Full == SimCity && G.CityClear;
        if (inp.CityForm == true && !seen) inp.CityForm = false;
        inp.CitySeen = seen; st = ScreenState.Classify(inp, Now);
      }
      return st;
    }
    void Count(ClickTarget t) { int n; Sent.TryGetValue(t, out n); Sent[t] = n + 1; }
    public int SentCount(ClickTarget t) { int n; return Sent.TryGetValue(t, out n) ? n : 0; }
    // GatedSend / SendInside: the gate's yes on a fresh read, then the click and its watch
    bool Send(ClickTarget t, AutoAction a, Action<ClickRequest, ScreenState> fill, bool wantCity, bool wantKnown) {
      if (a.X < 0 || a.Y < 0 || a.X >= W || a.Y >= H) return false;
      var fresh = Fresh(wantCity, wantKnown);
      if (FormsUnread(fresh)) { if (Environment.GetEnvironmentVariable("SIM_TRACE") != null) Console.WriteLine("  UNREAD " + t + " fresh=" + (fresh != null ? fresh.Brief : "-") + " unread=" + formsUnread); return false; }
      var rq = new ClickRequest { Owner = Owner, Target = t, AutoOn = true, Front = true, X = a.X, Y = a.Y };
      if (fill != null) fill(rq, fresh);
      if (rq.U <= 0) { rq.W = W; rq.H = H; rq.U = U; }
      rq.Shown = unknownForms;   // (app/Overlay.cs SendInside)
      // app/Overlay.cs LiveClick: a pilot button read by name (RFX.UiWidgetOf) is pressed at the centre of its live rect
      var lr = t == ClickTarget.ArtWorkshop && G.LiveButtons && RFX.UiWidgetOf(t, rq.Form) != null ? G.WorkshopLive() : null;
      if (lr != null && a.Kind == AutoKind.Click) { a = new AutoAction { Kind = AutoKind.Click, X = lr[0] + lr[2] / 2, Y = lr[1] + lr[3] / 2 }; rq.X = a.X; rq.Y = a.Y; rq.Live = true; }
      Gate.OrderRead = !G.NoOrder && !G.NoTable && !G.Scan;   // (app/Overlay.cs: the fresh read has the window table's z-order; the older readers had none)
      var res = Gate.Check(rq, fresh, Now);
      if (!res.Allowed) { if (Environment.GetEnvironmentVariable("SIM_TRACE") != null) Console.WriteLine("  DENY " + t + " fresh=" + fresh.Brief + " tick=" + (Screens.Current != null ? Screens.Current.Brief : "-") + " " + res.Detail); return false; }
      Gate.Arm(Owner, t, ClickGate.Expect(rq, fresh), Now, rq.Form);
      Count(t);
      if (Environment.GetEnvironmentVariable("SIM_TRACE") != null) Console.WriteLine("  send " + t + " fresh=" + fresh.Brief + " top=" + fresh.Top + " tick=" + (Screens.Current != null ? Screens.Current.Brief : "-") + " unread=" + formsUnread);
      if (a.Kind == AutoKind.Click) G.Click(a.X, a.Y, t); else if (a.Kind == AutoKind.Drag) G.Drag(a.X, a.Y, a.DY, t);
      SendingUntil = Now + (a.Kind == AutoKind.Drag ? 850 : 300);   // (a drag: press, 16 moves of 20 ms, 350 ms at rest, release - app/Overlay.cs Send)
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
    // OfferStep (app/Overlay.cs): an offer-list form on top («План сборки» too) is left by its × / back arrow, never its other buttons
    bool OfferStep(ScreenState scr) {
      if (scr == null || scr.OfferCount == 0) return false;
      if (Gate.Halted(Owner) || Gate.Pending(Owner)) return true;
      if (!OfferForms.Settled(scr, Now)) return true;
      string form = scr.Offer; var pt = OfferForms.ClosePoint(geo, form, W, H, U);
      if (pt == null) { Gate.Halt(Owner, "offer", "the offer pop-up " + form + " has no known close button - not clicked"); return true; }
      Send(ClickTarget.OfferClose, new AutoAction { Kind = AutoKind.Click, X = pt.X, Y = pt.Y }, (rq, fr) => { rq.Form = form; rq.W = W; rq.H = H; rq.U = U; rq.Geo = geo; }, false, false);
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
      if (Environment.GetEnvironmentVariable("SIM_TRACE") != null) Console.WriteLine("  backstep " + Now + " " + (scr != null ? scr.Brief + " block=" + ClickGate.CityBackBlock(scr) : "-"));
      if (!Gate.CityBackDue(Owner, scr, Now)) return !Gate.Halted(Owner) && ClickGate.CityBackBlock(scr) == "";   // (city first: the pilot never goes on from a screen not its flow's)
      Send(ClickTarget.CityBack, new AutoAction { Kind = AutoKind.Click, X = hg2.BackX * U, Y = hg2.BackY * U }, (rq, fr) => { rq.U = U; }, false, true);
      return true;
    }

    public void Tick() {
      if (Done != "") return;
      if (Owner == ClickOwner.Equip) TickEquip();
      else if (Flow == PilotFlow.ArtSell) TickArtSell();
      else if (Owner == ClickOwner.Sell) TickSell();
      else if (Flow == PilotFlow.LocateArtPromote) TickArtPromote();
      else TickLocate();
    }
    // TickAuto's hero part (app/Overlay.cs): the plan's hero on the hero screen, the gear tab
    void TickEquip() {
      var scr = Screens.Current;
      if (OfferStep(scr)) return;
      if (UnknownBackStep(scr)) return;
      if (BackStep(scr, CityFirst.NeedsCity(Flow, scr))) return;   // city first (the shared table)
      if (Gate.UnknownStall(ClickOwner.Equip, scr, Now)) return;
      var hv = new HeroView {
        NowMs = Now, Foreground = true, W = W, H = U, ClientH = H, Plan = plan, Hero = heroScreen.FormOk ? heroScreen.Hero : 0, Tab = heroScreen.Tab, Part = heroScreen.Part,
        SmallCards = heroScreen.SmallCards, Heroes = heroScreen.Heroes, HeroesButton = heroScreen.Hero == 0 && G.Full == SimCity && G.CityClear,
        FilterOpen = false, ClassChosen = heroScreen.ClassChosen, FactionList = heroScreen.FactionList, ChosenCamps = heroScreen.ChosenCamps, ClassCount = heroScreen.ClassCount, GridPtr = heroScreen.GridPtr
      };
      if (!heroScreen.FormOk) hv.Hero = 0;
      var ha = hp.Step(hv); PilotState = "hero_" + hp.State;
      // (idle: the plan's hero was there already) then the item, the hero pilot still watching the hero every tick (app/Overlay.cs TickAuto);
      // the artifact equip's goal here: the plan's hero on its gear tab (the artifact steps after it: ArtEquipPilot's tests)
      if (ha == null) { if (hp.State == "done" || hp.State == "idle") { if (Flow == PilotFlow.ArtEquip) { if (heroScreen.FormOk && heroScreen.Hero == plan) Done = "done"; return; } ItemStep(scr); } return; }
      if (ha.Kind == AutoKind.None) return;
      if (!Send(ClickTarget.EquipNav, ha, null, true, false)) hp.NotSent();   // (app/Overlay.cs: EquipSend's no)
    }
    // the item on the plan's hero through the real AutoPilot: the game opens its tip (Form_CharactorEquipTip, a flow form of the hero screen: never
    // escaped - the laptop 1.6.66.347 escaped it twice and stopped), «Заменить» on it is EquipConfirm. A start with the item selected and its tip
    // closed (the laptop 1.6.66.348: «no single blue button», no step for 2 min): the item is clicked again
    readonly AutoPilot ip = new AutoPilot(SimGame.ItemGeo);
    void ItemStep(ScreenState scr) {
      G.ItemPhase = true;
      bool tip = G.Stack.Contains(SimTip) && G.Full == SimHero;
      var v = new AutoView {
        NowMs = Now, Foreground = true, H = U, Target = SimItem, Row = 2, Col = 1, Sel = G.ItemSel ? SimItem : 0, SelRow = G.ItemSel ? 2 : 0, SelCol = G.ItemSel ? 1 : 0,
        AnchorHas = true, Row1Top = G.Row1Top, Types = SimGame.ItemTypes, AutoConfirm = true, Button = tip ? SimGame.Hint.Replace(W, H) : null,
        Hero = plan, ScreenHero = heroScreen.FormOk ? heroScreen.Hero : 0, Owner = G.Worn ? plan : 0
      };
      // (app/Overlay.cs TickAuto: the tip among the shown forms)
      v.TipKnown = !formsUnread && scr != null && !scr.Held && scr.Kind == ScreenKind.HeroScreen && scr.OfferRead && formsKind == ScreenKind.HeroScreen;
      v.TipShown = scr != null && ((scr.Flow ?? "") + "," + (scr.ModalWhy ?? "")).Contains(SimTip);
      v.BlueNone = !tip;
      var a = ip.Step(v);
      if (ip.Trace != "" && Environment.GetEnvironmentVariable("SIM_TRACE") != null) Console.WriteLine("  item " + Now + ": " + ip.Trace);
      ip.Trace = "";
      PilotState = "item_" + ip.State;
      if (ip.State == "done") { Done = "done"; return; }
      if (a.Kind == AutoKind.None) return;
      var b = v.Button; bool onConfirm = b != null && a.X >= b[0] && a.X <= b[0] + b[2] && a.Y >= b[1] && a.Y <= b[1] + b[3];
      if (onConfirm) Confirms++;
      if (!Send(onConfirm ? ClickTarget.EquipConfirm : ClickTarget.EquipNav, a, null, false, false)) { ip.NotSent(); if (onConfirm) Confirms--; }
    }
    public int Confirms;
    // TickSell (app/Overlay.cs) up to «Массовая продажа» (the marking itself: RunSell's tests)
    void TickSell() {
      var scr = Screens.Current;
      if (Gate.Halted(ClickOwner.Sell)) return;
      var pre = Gate.Preflight(ClickOwner.Sell, scr, Now);
      if (pre.Verdict == GateVerdict.Deny) return;
      if (pre.Reason == "offer" && OfferStep(scr)) { PilotState = "open"; return; }
      if (pre.Reason == "unknown" && UnknownBackStep(scr)) return;
      if (pre.Verdict == GateVerdict.Wait) { PilotState = pre.Reason == "unknown" ? "no_city" : PilotState; return; }
      if (MapStep(scr)) { PilotState = "open"; return; }   // a pop-up over the inventory first
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
        // the marking: the sell pilot on the game's list order and its exact scroll; every cell click must hit a wanted (or a ticked) item
        double srow1 = sellScroll.Row1(spilot.Geometry, inv.List, double.NaN, U);
        if (!InvSettle.Settled) return;
        var a = spilot.Step(new SellView { NowMs = Now, Foreground = true, W = W, H = H, U = U, Screen = inv.AsSell(SimGame.InvCols), Wanted = Wanted, Phase = double.NaN, Row1 = srow1, NeedExact = true });
        if (spilot.State == "failed" && spilot.FailWhy == "scroll") Gate.Halt(ClickOwner.Sell, "scroll", "the inventory's exact scroll stayed unreadable");
        PilotState = "sell_" + spilot.State;
        if (a.Kind == AutoKind.None) { if (spilot.State == "done") Done = "done"; return; }
        var sg = spilot.Geometry;
        bool sent = Send(a.Kind == AutoKind.Drag ? ClickTarget.InvListDrag : ClickTarget.InvBulkCell, a, (rq, fr) => {
          rq.CellUid = ClickGate.InvCellUid(fr.Inv, sg, sellScroll.Row1(sg, fr.Inv != null ? fr.Inv.List : null, double.NaN, U, Now), SimGame.InvCols, rq.X, rq.Y, W, U);
          var ok = new HashSet<long>(Wanted); if (fr.Inv != null) foreach (var u in fr.Inv.Selected) ok.Add(u);
          rq.CellOk = ok; rq.NoScroll = fr.Inv == null || fr.Inv.List == null || !fr.Inv.List.Has;
          if (Environment.GetEnvironmentVariable("SIM_TRACE") != null) Console.WriteLine("  cell x=" + (int)rq.X + " y=" + (int)rq.Y + " uid=" + rq.CellUid + " row1=" + sellScroll.Row1(sg, fr.Inv != null ? fr.Inv.List : null, double.NaN, U, Now) + " srow1=" + srow1 + " scroll=" + (fr.Inv != null && fr.Inv.List != null ? fr.Inv.List.ScrollY : -1) + " n=" + (fr.Inv != null ? fr.Inv.Uids.Count : -1) + " " + spilot.Trace);
        }, false, false);
        if (sent) spilot.Sent(); else spilot.NotSent();   // (app/Overlay.cs TickSell)
        return;
      }
      if (BackStep(scr, CityFirst.NeedsCity(PilotFlow.Sell, scr))) { PilotState = "open"; return; }
      if (scr != null && scr.Kind == ScreenKind.HeroScreen) {
        PilotState = "open";
        if (Now - sellBtnAt < 2000) return;
        sellBtnAt = Now;
        Send(ClickTarget.HeroBack, new AutoAction { Kind = AutoKind.Click, X = hg2.BackX * U, Y = hg2.BackY * U }, null, false, false);
        return;
      }
      if (scr != null && scr.Kind == ScreenKind.City && CityButtonSeen()) {
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
      if (pre.Reason == "offer" && OfferStep(scr)) return;
      if (pre.Reason == "unknown" && UnknownBackStep(scr)) return;
      if (pre.Verdict == GateVerdict.Wait) { PilotState = pre.Reason == "pending" ? "work" : "hint:no_city"; return; }
      bool heroOpen = scr != null && scr.Kind == ScreenKind.HeroScreen;
      bool formOpen = scr != null && (scr.Kind == ScreenKind.Foundry || scr.Kind == ScreenKind.Gem);
      var dstep = LocateGuide.Next(req, inv, locateRs, heroScreen, 8, 1);
      if (dstep.Kind == LocateKind.AscendReady) { Done = "done"; return; }   // the item selected in the Ascension list: «Вознести» is the player's
      // the recast: its item in the inventory's list (the card, the gem screen after it: LocatePilot's tests)
      if (Flow == PilotFlow.LocateRecast && dstep.Kind == LocateKind.InList && scr != null && scr.Kind == ScreenKind.Inventory) { Done = "done"; return; }
      var view = new LocateView { NowMs = Now, Foreground = true, W = W, H = H, U = U, Step = dstep, Inv = inv, Cols = 8, HeroScreenOpen = heroOpen, OtherFormOpen = formOpen,
                                  Geometry = geo, Rs = locateRs, Item = req.Item, Purpose = req.Purpose };
      if (LocateAuto.NeedsCityButton(dstep, heroOpen, formOpen)) view.CityButton = CityButtonSeen();
      bool needBack = CityFirst.NeedsCity(Flow, scr, dstep.Kind, LocateAuto.NeedsCityBack(view));   // city first (app/Overlay.cs TickLocateDrive)
      if (BackStep(scr, needBack)) return;
      if (Gate.CellAgainDue.Remove(ClickOwner.Locate)) lp.CellAgain();   // (ClickGate.Observe: a cell click without effect, placed again once)
      if ((dstep.Kind == LocateKind.InList && !InvSettle.Settled) || (dstep.Kind == LocateKind.AscendPick && !RsSettle.Settled)) return;
      var mv = lp.Step(view);
      PilotState = mv.State + (mv.Why != "" ? ":" + mv.Why : "") + "@" + dstep.Kind;
      if (mv.Action.Kind == AutoKind.None) return;
      ClickTarget tg = ClickTarget.None; Action<ClickRequest, ScreenState> fill = null;
      switch (mv.Todo) {
        case LocateTodo.BackToCity: tg = ClickTarget.HeroBack; break;
        case LocateTodo.InventoryButton: tg = ClickTarget.CityInventory; break;
        case LocateTodo.EquipTab: tg = ClickTarget.InvEquipTab; break;
        case LocateTodo.FoundryButton: tg = ClickTarget.FoundryButton; break;
        case LocateTodo.AscendTab: tg = ClickTarget.AscendTab; break;
        case LocateTodo.SlotTab: tg = ClickTarget.SlotTab; break;
        case LocateTodo.AscendCell:
          tg = mv.Action.Kind == AutoKind.Drag ? ClickTarget.AscendDrag : ClickTarget.AscendCell;
          fill = (rq, fr) => { rq.CellUid = ClickGate.AscendCellUid(geo, fr.Rs, rq.X, rq.Y, W, H, U); rq.CellOk = new[] { SimItem }; };
          break;
      }
      if (Send(tg, mv.Action, fill, tg == ClickTarget.CityInventory, tg == ClickTarget.UnknownBack || tg == ClickTarget.CityBack || tg == ClickTarget.FoundryButton)) lp.Sent(); else lp.NotSent();
    }
    // TickArtSellAuto (app/Overlay.cs) up to the artifact tab: city first, the hero screen's back arrow, «Инвентарь», «Артефакт»
    long artSellAt = -100000;
    void TickArtSell() {
      var scr = Screens.Current;
      if (Gate.Halted(ClickOwner.Sell)) return;
      var pre = Gate.Preflight(ClickOwner.Sell, scr, Now);
      if (pre.Reason == "offer" && OfferStep(scr)) { PilotState = "open"; return; }
      if (pre.Reason == "unknown" && UnknownBackStep(scr)) return;
      if (pre.Verdict != GateVerdict.Allow) { PilotState = pre.Reason == "unknown" ? "no_city" : PilotState; return; }
      bool eqBulk = scr != null && scr.Kind == ScreenKind.Inventory && scr.InvEquipTab && scr.InvBulkKnown && scr.InvBulk;   // (app/Overlay.cs TickArtSellAuto)
      bool need = CityFirst.NeedsCity(PilotFlow.ArtSell, scr, LocateKind.None, eqBulk);
      if (BackStep(scr, need)) { PilotState = "open"; return; }
      if (need) { PilotState = "no_city"; return; }   // the back press is blocked (a pop-up over the screen): the hint says so
      PilotState = "open";
      if (scr != null && scr.Kind == ScreenKind.HeroScreen) {
        if (Now - artSellAt < 2000) return;
        artSellAt = Now; Send(ClickTarget.HeroBack, new AutoAction { Kind = AutoKind.Click, X = hg2.BackX * U, Y = hg2.BackY * U }, null, false, false); return;
      }
      if (scr != null && scr.Kind == ScreenKind.City && CityButtonSeen()) {
        if (Now - artSellAt < 2500) return;
        artSellAt = Now; Send(ClickTarget.CityInventory, new AutoAction { Kind = AutoKind.Click, X = W - (0.5222 + 0.9316) * U, Y = H - hg2.HeroesBtnDY * U }, null, true, false); return;
      }
      var inv = Screens.Inv;
      if (scr == null || scr.Kind != ScreenKind.Inventory || inv == null) { PilotState = "no_city"; return; }
      if (inv.ArtTab) { Done = "done"; return; }   // the artifact tab: the marking after it is RunSell's / the art sell tests'
      if (Now - artSellAt < 2000) return;
      artSellAt = Now;
      var tr = ArtFrames.Rect(geo, "inventory.leftTab.artifact", W, H, U);
      if (tr != null) Send(ClickTarget.ArtTab, new AutoAction { Kind = AutoKind.Click, X = tr.CX, Y = tr.CY }, null, false, false);
    }
    // TickArtDrive (app/Overlay.cs) up to the artifact tab: the real ArtPilot on LocateGuide.NextArt, city first
    readonly ArtPilot ap = new ArtPilot();
    /// <summary>The artifact promotion runs on to its workshop (the cell, «Мастерская»); off: done at the artifact tab.</summary>
    public bool ArtToWorkshop;
    void TickArtPromote() {
      if (ap.Stopped) { PilotState = "stopped:" + ap.Reason; return; }
      var scr = Screens.Current; var inv = Screens.Inv;
      var pre = Gate.Preflight(ClickOwner.Locate, scr, Now);
      if (pre.Reason == "offer" && OfferStep(scr)) return;
      if (pre.Reason == "unknown" && UnknownBackStep(scr)) return;
      if (pre.Verdict != GateVerdict.Allow) { PilotState = pre.Reason == "pending" ? "work" : "hint:no_city"; return; }
      var ws = scr != null ? scr.ArtWs : null;
      var step = LocateGuide.NextArt(req, scr != null && scr.Kind == ScreenKind.Inventory ? inv : null, ws);
      if (!ArtToWorkshop && scr != null && scr.Kind == ScreenKind.Inventory && inv != null && inv.ArtTab) { Done = "done"; return; }   // the artifact tab: its cell, the workshop after it (ArtPilot's tests; ArtToWorkshop: here)
      if (ArtToWorkshop && ws != null && ws.Uid == req.Art.Uid) { Done = "done"; return; }
      bool heroOpen = scr != null && scr.Kind == ScreenKind.HeroScreen;
      bool formOpen = scr != null && (scr.Kind == ScreenKind.Foundry || scr.Kind == ScreenKind.Gem);
      var view = new ArtView { NowMs = Now, Foreground = true, W = W, H = H, U = U, Step = step, Inv = inv, Ws = ws, Art = req.Art, Geometry = geo, HeroScreenOpen = heroOpen, OtherFormOpen = formOpen };
      if (ArtLocateAuto.NeedsCityButton(step, heroOpen, formOpen)) view.CityButton = CityButtonSeen();
      if (BackStep(scr, CityFirst.NeedsCity(PilotFlow.LocateArtPromote, scr, step.Kind, ArtLocateAuto.NeedsCityBack(view)))) return;
      var mv = ap.Step(view);
      PilotState = (mv.State == "hint" ? "hint:" : mv.State + ":") + mv.Why + "@" + step.Kind;
      if (mv.Action.Kind == AutoKind.None) return;
      ClickTarget tg; Action<ClickRequest, ScreenState> fill = null;
      switch (mv.Todo) {
        case ArtTodo.HeroBack: tg = ClickTarget.HeroBack; break;
        case ArtTodo.InventoryButton: tg = ClickTarget.CityInventory; break;
        case ArtTodo.ArtTab: tg = ClickTarget.ArtTab; break;
        case ArtTodo.Workshop: tg = ClickTarget.ArtWorkshop; fill = (rq, fr) => { rq.CellOk = new[] { req.Art.Uid }; }; break;
        case ArtTodo.Cell:   // (app/Overlay.cs SendArt)
          tg = mv.Action.Kind == AutoKind.Drag ? ClickTarget.ArtListDrag : ClickTarget.ArtCell;
          fill = (rq, fr) => {
            int i = fr.Inv != null ? ClickGate.ListCardIndexAt(geo, "inventory.artList", fr.Inv.List, LocateGuide.ArtInvCols, 116, rq.X, rq.Y, W, H, U) : -1;
            rq.CellUid = i >= 0 && fr.Inv != null && i < fr.Inv.Uids.Count ? fr.Inv.Uids[i] : 0;
            rq.CellOk = new[] { req.Art.Uid };
            rq.NoScroll = fr.Inv == null || fr.Inv.List == null || !fr.Inv.List.Has;
          };
          break;
        default: return;
      }
      Send(tg, mv.Action, fill, tg == ClickTarget.CityInventory, false);
    }
  }

  static readonly string[] SimNamed = { "Form_Union", "Form_PVPMain", "Form_Shop" };

  /// <summary>One seed: the start, the task, the run. Returns "" when the run kept every rule, else what went wrong.</summary>
  sealed class SimStat { public long Runs, Done, DoneMs, ConfirmN, ConfirmMs, ConfirmMax, DiscN, DiscMs, FieldN, FieldUs; }

  /// <summary>--gate-log (with --seed): the gate's journal lines in the seed's trace.</summary>
  static bool SimGateLog;
  static string SimRun(int seed, bool slowOnly, bool verbose, Dictionary<string, int> outcomes, GameGeometry geo, string list = null, bool real = false, bool scan = false, Dictionary<string, SimStat> stats = null) {
    ReadTiming.Take();   // (the click gate counts its click-to-effect times there: this run's own)
    var r = new Random(seed * 7919 + 13);
    bool slow = slowOnly || r.Next(2) == 0;
    double failP = r.Next(3) == 0 ? 0.0 : r.NextDouble() * 0.15;
    var g = new SimGame(seed, slow, failP); g.Geo = geo; g.Scan = scan; if (real) g.SpawnP = 0.01; if (SimOrderFailP >= 0) g.OrderFailP = SimOrderFailP;
    var owner = new[] { ClickOwner.Equip, ClickOwner.Sell, ClickOwner.Locate }[r.Next(3)];
    // the start: a screen and the forms over it
    int k = r.Next(10);
    if (k == 2) { g.Hero.Shown = 100 + r.Next(30); g.Push(SimHero); if (r.Next(3) == 0) g.Hero.Part = r.Next(5); }
    else if (k == 3 || k == 4) { g.InvEquipTab = r.Next(2) == 0; g.InvBulk = g.InvEquipTab && r.Next(3) == 0; g.Push(SimInv); if (k == 4) { g.FoundryTab = new[] { 0, 2, 3 }[r.Next(3)]; g.Push(SimFoundry); } }
    else if (k >= 5 && SimGraph) SimGraphStart(g, r);   // (--graph: a real stack, the forms of a safe path from the city)
    else if (k >= 5) { int n = 1 + r.Next(2); for (int i = 0; i < n; i++) g.Push(r.Next(2) == 0 ? SimNamed[r.Next(SimNamed.Length)] : SimPick(r, 'F')); }
    // (the stress mix: 0-2 forms over the start, a tenth of them dangerous; --real: a form over the start one run in six, a dangerous one 3 in 100 of those)
    int extra = r.Next(3); if (real) extra = extra == 0 && r.Next(2) == 0 ? 1 : 0;
    for (int i = 0; i < extra; i++) {
      int p = r.Next(100); if (real && p >= 65 && p < 75) p = r.Next(10) < 3 ? p : 50;
      char c = p < 45 ? 'P' : p < 65 ? 'E' : p < 75 ? 'D' : p < 82 ? 'W' : 'O';
      string f = SimGraph && (c == 'P' || c == 'E' || c == 'D') && r.Next(2) == 0 ? SimGraphAuto(r, g.Full == SimCity) : SimPick(r, c); if (f != null) { if (SimGraph) SimGraphSeen.Add(f); g.Push(f); }
    }
    // a known form moved: the old reader searched for it with a pass (17 s on the laptop); the window table gives it on the next tick's check
    if (slow && r.Next(8) == 0) { g.LostFrom = 2000 + r.Next(20000); g.LostTo = g.LostFrom + (scan ? 17000 : 150 + r.Next(250)); }
    // every pilot's flow (CityFirst): the artifact equip / cleanup, the recast and the artifact promotion besides the gear ones, and the
    // screens of the other flows as a start (the gem screen, the workshop, «План сборки») - their own stream, the old seeds keep their runs
    var r3 = new Random(seed * 131 + 5);
    if (SimOldFlows) return SimDrive(g, owner == ClickOwner.Equip ? PilotFlow.Equip : owner == ClickOwner.Sell ? PilotFlow.Sell : PilotFlow.LocateAscend, slow, failP, r, seed, verbose, outcomes, geo, list, scan, stats);
    PilotFlow flow = owner == ClickOwner.Equip ? (r3.Next(3) == 0 ? PilotFlow.ArtEquip : PilotFlow.Equip) : owner == ClickOwner.Sell ? (r3.Next(3) == 0 ? PilotFlow.ArtSell : PilotFlow.Sell)
                   : new[] { PilotFlow.LocateAscend, PilotFlow.LocateAscend, PilotFlow.LocateRecast, PilotFlow.LocateArtPromote }[r3.Next(4)];
    int k3 = r3.Next(10);
    if (k3 == 0) SimStartAt(g, "Gem", 0); else if (k3 == 1) SimStartAt(g, "Artifact workshop", 0); else if (k3 == 2) SimStartAt(g, "Plan", 0);
    return SimDrive(g, flow, slow, failP, r, seed, verbose, outcomes, geo, list, scan, stats);
  }

  /// <summary>The regression seeds below were found with the gear flows and the Ascension only: they keep those (the soak and the matrix run every flow).</summary>
  static bool SimOldFlows;
  /// <summary>A pop-up the screen map closes (its own exit), the same one every run.</summary>
  static string SimPopup() {
    foreach (var e in ScreenMap.All) if (e.Class == 'P' && Array.IndexOf(OfferForms.KnownScreens, e.Name) < 0 && !OfferForms.IsOffer(e.Name) && e.Name.Contains("Reward")) return e.Name;
    return SimPick(new Random(1), 'P');
  }
  /// <summary>A start screen by name (the matrix's and the soak's): pushed over what the game shows.</summary>
  static void SimStartAt(SimGame g, string start, long other) {
    switch (start) {
      case "City": break;
      case "Hero (other hero)": g.Hero.Shown = other; g.Hero.Part = -1; g.Push(SimHero); break;
      case "Hero (gear list)": g.Hero.Shown = other; g.Hero.Tab = 3; g.Hero.Part = 2; g.Push(SimHero); break;
      case "Inventory": g.InvEquipTab = true; g.InvBulk = false; g.Push(SimInv); break;
      case "Inventory (other tab)": g.InvEquipTab = false; g.InvBulk = false; g.Push(SimInv); break;
      case "Inventory (bulk)": g.InvEquipTab = true; g.InvBulk = true; g.Push(SimInv); break;
      case "Foundry identify": case "Foundry ascend": case "Foundry recast":
        g.InvEquipTab = true; g.Push(SimInv); g.FoundryTab = start == "Foundry identify" ? 1 : start == "Foundry ascend" ? 2 : 3; g.Push(SimFoundry); break;
      case "Gem": g.InvEquipTab = true; g.Push(SimInv); g.Push(SimGem); break;
      case "Artifact workshop": g.InvEquipTab = false; g.InvArtTab = true; g.Push(SimInv); g.Push(SimWs); break;
      case "Guild": g.Push("Form_Union"); break;
      case "Arena": g.Push("Form_PVPMain"); break;
      case "Shop": g.Push("Form_Shop"); break;
      case "Popup over City": g.Push(SimPopup()); break;
      case "Plan": g.Push("Form_EquipmentPlan"); break;
      default: throw new ArgumentException(start);
    }
  }

  /// <summary>The run itself (SimRun's and the matrix's): the host of the flow, the ticks, the end and its rules.</summary>
  static string SimDrive(SimGame g, PilotFlow flow, bool slow, double failP, Random r, int seed, bool verbose, Dictionary<string, int> outcomes, GameGeometry geo, string list, bool scan, Dictionary<string, SimStat> stats, string[] outcomeOut = null) {
    var owner = SimHost.OwnerOf(flow);
    string start = string.Join(" > ", g.Stack.ToArray());
    foreach (var f in g.Stack) g.StartForms.Add(f);
    long plan = 100 + r.Next(30);
    var h = new SimHost(g, flow, plan, geo);
    // (Locate's own screen is where its last step is: the Foundry, the gem screen, the workshop - the inventory is its way, as for the Ascension)
    g.OwnScreen = owner == ClickOwner.Equip ? SimHero : owner == ClickOwner.Sell ? SimInv : flow == PilotFlow.LocateRecast ? SimGem : flow == PilotFlow.LocateArtPromote ? SimWs : SimFoundry;
    g.ItemSel = owner == ClickOwner.Equip && r.Next(2) == 0;   // the item left selected with its tip closed
    long cap = slow ? 300000 : 150000, t = 0, lastChange = 0; string lastSay = "";
    while (t < cap) {
      g.Advance(t);
      if (t >= h.SendingUntil) { h.ReadScreens(t); h.Tick(); }
      if (g.Bad.Count > 0 || h.Done != "" || h.Gate.Halted(owner) || h.PilotState == "item_unconfirmed" || h.PilotState == "item_failed") break;   // (the item pilot's own ends)
      string say = h.PilotState + "|" + string.Join(">", g.Stack.ToArray());
      if (say != lastSay) { lastSay = say; lastChange = t; }
      // a pilot that tells the player why it waits (the hero screen not reachable, a hint) and nothing changes for 30 s: a stop with a reason
      if (t - lastChange > 30000 && (h.PilotState.StartsWith("hero_noscreen") || h.PilotState.StartsWith("hero_failed") || h.PilotState.StartsWith("hero_notfound") || h.PilotState.StartsWith("hint") || h.PilotState.StartsWith("stopped") || h.PilotState == "no_city" || h.PilotState == "sell_failed")) break;
      // (app/Overlay.cs ReadScreens: every 120 ms, a tick of the slow laptop's timer 150-400 ms; 50 ms while a click awaits its effect)
      t += !scan && (h.Gate.AnyPending || h.ListMoving || h.SellPolling) ? 50 : slow ? 150 + r.Next(250) : 100;
    }
    string fname = flow == PilotFlow.Equip ? "Equip" : flow == PilotFlow.Sell ? "Sell" : flow == PilotFlow.LocateAscend ? "Locate" : flow.ToString();   // (the old flows keep their names)
    if (stats != null) {
      string mk = fname + (slow ? " slow" : " fast");
      SimStat ss; if (!stats.TryGetValue(mk, out ss)) stats[mk] = ss = new SimStat();
      ss.Runs++; if (h.Done != "") { ss.Done++; ss.DoneMs += t; }
      ss.ConfirmN += ReadTiming.ConfirmN; ss.ConfirmMs += ReadTiming.ConfirmMs; if (ReadTiming.ConfirmMax > ss.ConfirmMax) ss.ConfirmMax = ReadTiming.ConfirmMax;
      ss.DiscN += g.DiscoveryN; ss.DiscMs += g.DiscoveryTotalMs; ss.FieldN += g.FieldReads; ss.FieldUs += g.FieldUs;
    }
    string outcome = h.Done != "" ? "done" : h.Gate.Halted(owner) ? "halt:" + h.Gate.HaltReason(owner) : t >= cap ? "TIMEOUT" : "wait:" + h.PilotState.Split('@')[0];
    string key = fname + (slow ? " slow " : " fast ") + outcome;
    if (outcomeOut != null) outcomeOut[0] = outcome + (h.Gate.Halted(owner) ? " (" + h.Gate.HaltDetail(owner) + ")" : "");
    int cnt; outcomes.TryGetValue(key, out cnt); outcomes[key] = cnt + 1;
    if (list == "--digest") {   // one line a seed: its outcome and a hash of every press with its time (two builds compared seed by seed)
      ulong hsh = 14695981039346656037UL; foreach (char ch in g.Acts.ToString()) { hsh ^= ch; hsh *= 1099511628211UL; }
      Console.WriteLine(seed + " " + key + " " + hsh.ToString("x16"));
    } else if (list != null && key.Contains(list)) Console.WriteLine("seed " + seed + ": " + key + (h.PilotState.StartsWith("hero_") ? " " + h.HeroWhy : h.PilotState.StartsWith("sell_") ? " " + h.SellWhy : "") + " end [" + string.Join(" > ", g.Stack.ToArray()) + "]");
    if (h.Done != "" && flow == PilotFlow.Sell) {   // the marking: the wanted items ticked, nothing else (after the last clicks' answers)
      g.Advance(t + 3000);
      var xtra = new List<string>(); foreach (var u in g.InvSel) if (!h.Wanted.Contains(u)) xtra.Add(u.ToString());
      var miss = new List<string>(); foreach (var u in h.Wanted) if (!g.InvSel.Contains(u)) miss.Add(u.ToString());
      if (xtra.Count > 0 || miss.Count > 0) g.Bad.Add("SELL MARKS WRONG: extra [" + string.Join(",", xtra.ToArray()) + "] missing [" + string.Join(",", miss.ToArray()) + "]");
    }
    // (not when a pop-up is shown at the stop: the game opened it after the click and before its answer, which it took - the click was right)
    if (h.Gate.HaltReason(owner) == "nochange" && h.Gate.HaltDetail(owner).StartsWith("AscendCell") && h.Gate.HaltDetail(owner).Contains("(screen Foundry") && !h.Gate.HaltDetail(owner).Contains("modal(")) g.Bad.Add("ASCENSION CELL CLICK WITHOUT EFFECT: " + h.Gate.HaltDetail(owner));
    if (h.Done != "" && flow == PilotFlow.Equip && !g.Worn) g.Bad.Add("EQUIP DONE WITHOUT THE ITEM ON (" + h.Confirms + " EquipConfirm presses)");
    var errs = new List<string>(g.Bad);
    if (outcome == "TIMEOUT") errs.Add("no end within " + cap / 1000 + " s (silent waiting): " + h.PilotState);
    int n0, tries = 1 + h.Gate.Retries(owner);   // (each retry of the policy starts the run's counts over: the caps hold for each try)
    if (h.Gate.Retries(owner) > ClickGate.MaxRetriesRun) errs.Add(h.Gate.Retries(owner) + " retries");
    if (h.Sent.TryGetValue(ClickTarget.UnknownBack, out n0) && n0 > ClickGate.MaxUnknownBacks * tries) errs.Add(n0 + " unknown-screen back presses");
    if (h.Sent.TryGetValue(ClickTarget.CityBack, out n0) && n0 > ClickGate.MaxCityBacks * tries) errs.Add(n0 + " back-to-the-city presses");
    if (h.Sent.TryGetValue(ClickTarget.MapClose, out n0) && n0 > ClickGate.MaxMapMoves * tries) errs.Add(n0 + " × presses");
    if (h.EscapesSent > (ClickGate.MaxEscapes + ClickGate.MaxMapMoves) * tries) errs.Add(h.EscapesSent + " Escapes");
    if (errs.Count == 0 && !verbose) return "";
    var sb = new StringBuilder();
    sb.Append("seed " + seed + " " + fname + (slow ? " slow" : " fast") + " fail " + failP.ToString("0.00") + " start [" + start + "] -> " + outcome
              + (h.Gate.Halted(owner) ? " (" + h.Gate.HaltDetail(owner) + ")" : "") + " end [" + string.Join(" > ", g.Stack.ToArray()) + "]");
    foreach (var e in errs) sb.Append("\n    !! " + e);
    if (errs.Count > 0 || verbose) foreach (var l in g.Log) sb.Append("\n      " + l);
    return sb.ToString();
  }

  // live 1.6.66.354 (the laptop, equip from the city): the filter shrank the gear list and the item moved up; the scroll read stayed stale
  // and the host put the anchor back from it after every click, so the pilot's re-anchoring clicks taught nothing - 184 clicks for a
  // minute at one spot between the two bottom rows. The game here answers a click 1 s later (the slow laptop); the host's Row1Top is
  // the stale one every tick. shift: how far (in rows) the stale anchor puts the rows below the real ones.
  static string StaleRowsRun(double shift, bool barHit, bool startSel, out int clicks, out int maxSame, out string why) {
    var geo = new ListGeometry(); double H = 1009, pitch = geo.PitchY * H, cell = geo.CellW * H;
    var types = new[] { 0, 1, 1, 1, 1, 1, 1 };
    double real = ScrollAnchor.TopRow1(geo, H), stale = real + shift * pitch;
    long target = 9021; long sel = startSel ? target : 9051; clicks = 0; maxSame = 0; int same = 0; double lx = double.NaN, ly = double.NaN;
    Func<double, double, long> at = (x, y) => {
      int col = geo.ColumnAt(x / H); if (col == 0) return 0;
      for (int r = 1; r < types.Length; r++) { double t = real + geo.RowOffset(types, r) * H; if (y >= t && y <= t + (barHit ? geo.BarBottom * H : cell)) return 9000 + r * 10 + col; }
      return 0;
    };
    var p = new AutoPilot(geo); var due = new List<KeyValuePair<long, long>>(); int confirms = 0;
    var button = new[] { 800, 600, 136, 37 };
    for (long t = 0; t < 120000; t += 100) {
      foreach (var e in due.ToArray()) if (e.Key <= t) { sel = e.Value == sel ? 0 : e.Value; due.Remove(e); }
      int selRow = sel > 0 ? (int)((sel - 9000) / 10) : 0;
      var a = p.Step(new AutoView { NowMs = t, Foreground = true, H = H, Target = target, Row = 2, Col = 1, Sel = sel, SelRow = selRow, SelCol = sel > 0 ? 1 : 0,
                                    AnchorHas = true, Row1Top = stale, Types = types, AutoConfirm = startSel, Button = startSel && sel == target ? button : null, Hero = 5, ScreenHero = 5, Owner = 0 });
      if (p.State == "failed" || p.State == "done") break;
      if (a.Kind != AutoKind.Click) continue;
      if (a.X >= button[0] && a.X <= button[0] + button[2] && a.Y >= button[1] && a.Y <= button[1] + button[3]) { confirms++; break; }
      clicks++;
      if (Math.Abs(a.X - lx) < 2 && Math.Abs(a.Y - ly) < 2) same++; else same = 1;
      lx = a.X; ly = a.Y; maxSame = Math.Max(maxSame, same);
      long hit = at(a.X, a.Y); if (hit != 0) due.Add(new KeyValuePair<long, long>(t + 1000, hit));
    }
    why = p.Why;
    return confirms > 0 ? "confirm" : sel == target ? "selected" : p.State;
  }

  // live 1.6.66.354 (the owner's photo): the weapon's compare tip stayed open, the next step was the bracer slot - the frame sat on the
  // compare card where the slot is behind it, the pilot clicked there. The tip is the equip helper's flow form: Escape is kept off it only
  // while it is the step's target; before a slot step ONE Escape closes it (EquipTipEscape), at most 2 a run, nothing else shown over it.
  static void TipOverSlots() {
    Func<string, bool, ScreenState> hero = (flow, modal) => {
      var s = ScreenState.Classify(new ScreenInputs { Hero = new HeroScreen { Hero = 204200000, FormOk = true, Tab = 3, Part = 0 }, Offers = new List<string>(), ShownKnown = "Form_CharactorMain", Flow = flow }, 0);
      s.SinceMs = 0; return s;
    };
    var tip = hero("Form_CharactorEquipTip", false); var cmp = hero("Form_CharactorEquipTip,Form_EquipedCompared", false);
    var g = new ClickGate();
    Func<ScreenState, ClickGate, GateResult> esc = (st, gg) => gg.Check(new ClickRequest { Owner = ClickOwner.Equip, Target = ClickTarget.EquipTipEscape, AutoOn = true, Front = true }, st, 1000);
    Check(ClickGate.OnlyTip(tip) && ClickGate.OnlyTip(cmp) && !ClickGate.OnlyTip(hero("", false)) && !ClickGate.OnlyTip(hero("Form_EquipFilterWidget", false)), "sim: the item's tip / compare over the slots is told apart from the other flow forms");
    var r1 = esc(cmp, g);
    Check(r1.Allowed, "sim: tip open, a slot step: one Escape closes the compare tip (" + r1.Detail + ")");
    g.Arm(ClickOwner.Equip, ClickTarget.EquipTipEscape, ClickGate.Expect(new ClickRequest { Owner = ClickOwner.Equip, Target = ClickTarget.EquipTipEscape }, cmp), 1000);
    g.Observe(hero("", false), 1500);
    Check(!g.Pending(ClickOwner.Equip) && !g.Halted(ClickOwner.Equip), "sim: ... the tip gone is the Escape's effect");
    var g2 = new ClickGate();
    Check(!esc(hero("Form_EquipFilterWidget", false), g2).Allowed && !g2.Halted(ClickOwner.Equip), "sim: no tip Escape while the filter pop-up (a step's own target) is shown");
    Check(!g2.Check(new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.EquipTipEscape, AutoOn = true, Front = true }, tip, 1000).Allowed, "sim: the tip Escape is the equip helper's only");
    var g3 = new ClickGate(); int sent = 0;
    for (int i = 0; i < 4 && !g3.Halted(ClickOwner.Equip); i++) {
      if (esc(tip, g3).Allowed) { sent++; g3.Arm(ClickOwner.Equip, ClickTarget.EquipTipEscape, null, 1000 + i); }
    }
    Check(sent == ClickGate.MaxTipEscapes && g3.Halted(ClickOwner.Equip), "sim: a tip that stays: " + sent + " Escapes, then a stop with the reason (" + g3.HaltDetail(ClickOwner.Equip) + ")");
    // the general Escape stays off the pilot's own screen
    var g4 = new ClickGate();
    Check(!g4.Check(new ClickRequest { Owner = ClickOwner.Equip, Target = ClickTarget.EscapeBack, AutoOn = true, Front = true }, tip, 1000).Allowed, "sim: the back Escape never on the hero screen with the tip");
  }

  static void StaleRowsAfterFilter() {
    int c, m; string why;
    foreach (bool bar in new[] { false, true }) {
      // the click for row 2 selects the item of row 4: the rows are taken from that click, the next one selects the item
      string r = StaleRowsRun(2, bar, false, out c, out m, out why);
      Check(r == "selected" && c <= 4 && m <= 2, "sim: stale rows after the filter (a click selects row 4): the item is selected (" + r + ", " + c + " clicks, same point " + m + " in a row, " + why + ")");
      // the click lands between two rows (nothing changes): never more than 2 clicks at one point, the item or a clear stop within a few clicks
      r = StaleRowsRun(2.5, bar, false, out c, out m, out why);
      Check((r == "selected" || (r == "failed" && why != "")) && c <= 8 && m <= 2, "sim: stale rows after the filter (a click between two rows): no loop (" + r + ", " + c + " clicks, same point " + m + " in a row, " + why + ")");
    }
    // the item is selected already when the pilot starts: no list click, straight to «Заменить»
    string s0 = StaleRowsRun(2, false, true, out c, out m, out why);
    Check(s0 == "confirm" && c == 0, "sim: the item selected already: the confirm step at once (" + s0 + ", " + c + " list clicks)");
  }

  // what the game sim found (each a fix in src/ or app/Overlay.cs), kept as plain tests
  static void SimRegressionTests() {
    UiGraphTests();
    ZOrderTests();
    {   // city first (owner rule 2026-10-30): every pilot x every start screen reaches its goal or stops with its reason
      var mgeo = GameGeometry.Parse(GeoJson(), GeoGame);
      var mf = SimMatrix(mgeo, true);
      SimOldFlows = true;
      Check(mf.Count == 0, "sim: city-first matrix, " + CityFirst.All.Length + " flows x " + SimMatrixStarts.Length + " starts: every run at its goal or a stop with its reason" + (mf.Count > 0 ? " - " + mf.Count + " failed: " + mf[0] : ""));
    }
    SimCityRebuilt(GameGeometry.Parse(GeoJson(), GeoGame));
    SimLoginChain(GameGeometry.Parse(GeoJson(), GeoGame));
    SimTableKind(GameGeometry.Parse(GeoJson(), GeoGame));
    SimUnknownToInventory(GameGeometry.Parse(GeoJson(), GeoGame));
    SimArtWorkshop(GameGeometry.Parse(GeoJson(), GeoGame));
    SimLeftOnWorkshop(GameGeometry.Parse(GeoJson(), GeoGame));
    SimResultForms();
    Console.WriteLine("Game sim: its findings");
    StaleRowsAfterFilter();
    TipOverSlots();
    // (9) a list is settled after two equal reads; a moving one (a glide after a drag, a slot filter) is not, an unreadable one neither
    {
      var ls = new ListSettle(); var inv = new InventoryScreen { Open = true, EquipTab = true, Bulk = true, List = new RFX.ListRead { ScrollY = 0 } }; inv.Uids.AddRange(new long[] { 1, 2, 3 });
      ls.Update(ListSettle.KeyOf(inv), 0); Check(!ls.Settled, "settle: one read is not settled");
      ls.Update(ListSettle.KeyOf(inv), 50); Check(ls.Settled, "settle: two equal reads are");
      inv.List.ScrollY = 40; ls.Update(ListSettle.KeyOf(inv), 100); Check(!ls.Settled, "settle: the scroll moved: not settled");
      inv.List.ScrollY = 55; ls.Update(ListSettle.KeyOf(inv), 150); Check(!ls.Settled, "settle: still gliding");
      long n0 = ReadTiming.SettleN, m0 = ReadTiming.SettleMs;
      ls.Update(ListSettle.KeyOf(inv), 200); Check(ls.Settled, "settle: stopped");
      Check(ReadTiming.SettleN == n0 + 1 && ReadTiming.SettleMs - m0 == 100, "settle: the time from the first move to settled is counted (" + (ReadTiming.SettleMs - m0) + " ms)");
      ls.Update(null, 250); Check(!ls.Settled, "settle: an unreadable read is not settled");
      var rs = new RecastScreen { CurType = 2, AscendEquips = new long[] { 5, 6 }, AscendParts = new[] { 3 }, AscendList = new RFX.ListRead { ScrollY = 0 } };
      string k1 = ListSettle.KeyOf(rs); rs.AscendParts = new int[0];
      Check(k1 != ListSettle.KeyOf(rs) && ListSettle.KeyOf(new RecastScreen { CurType = 0 }) == "", "settle: a slot filter changes the Ascension list's key; another tab has none");
    }
    // (10) a form shown with the screen from the start (taken for its part) may lie on top: no list click on one of its never-click buttons
    {
      const double W = 1920, H = 1009; double U = Ui.Unit(W, H); string pf = null; double px = 0, py = 0;
      foreach (var e in ScreenMap.All) {
        if (pf != null || !ScreenMap.MayBeBase(e.Name)) continue;
        for (double x = 100; x < W && pf == null; x += 37) for (double y = 150; y < H - 150 && pf == null; y += 29) if (ScreenMap.OnNever(e.Name, x, y, W, H, U)) { pf = e.Name; px = x; py = y; }
      }
      Check(pf != null, "sim: a form that may be a screen's part has a never-click button (" + pf + ")");
      if (pf != null) {
        var g = new ClickGate();
        var s = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), ShownKnown = "", Inv = new InventoryScreen { Open = true, EquipTab = true, Bulk = true, BulkKnown = true } }, 0);
        var rq = new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.InvBulkCell, AutoOn = true, Front = true, X = px, Y = py, W = W, H = H, U = U, Shown = new List<string> { pf }, CellUid = 1, CellOk = new long[] { 1 } };
        var res = g.Check(rq, s, 10000);
        Check(!res.Allowed && g.HaltReason(ClickOwner.Sell) == "target", "sim: a list click on a never-click button of " + pf + " shown with the inventory is refused (" + res.Detail + ")");
      }
    }
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
    // (8) the forms a fresh read has that the last one had not: the map's screens / pop-ups / waits; a late part of a screen only when the map does not
    // call it one of those; the Foundry's form in the window table keeps «Литейная» waiting though its memory read is not there yet
    Eq("Form_BuyPopup,Form_TogglePopUp", ScreenMap.Newcomers(new List<string> { "Form_LeftTip" }, new List<string> { "Form_LeftTip", "Form_BuyPopup", "Form_TogglePopUp", "Form_X_SimPart" }), "sim: newcomers of a fresh forms read");
    Check(ScreenMap.MayBeLatePart("Form_X_SimPart") && !ScreenMap.MayBeLatePart("Form_TogglePopUp") && !ScreenMap.MayBeLatePart("Form_DeityBeastBuffTips") && !ScreenMap.MayBeLatePart("Form_BuyPopup"),
          "sim: a late part of a screen is no pop-up / Escape form / dangerous dialog of the map");
    {
      var inp = new ScreenInputs { CityForm = false, Offers = new List<string>(), ShownKnown = "Form_BackpackIntegration,Form_EquipSubliming",
                                   Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true } };
      var fs = ScreenState.Classify(inp, 10000); var gg = new ClickGate();
      var r0 = gg.Check(new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.FoundryButton, AutoOn = true, Front = true, X = 1330, Y = 938 }, fs, 10000);
      Check(!r0.Allowed && !gg.Halted(ClickOwner.Locate), "sim: «Литейная» waits while the window table shows the Foundry unread (" + r0.Detail + ")");
      inp.ShownKnown = "Form_BackpackIntegration"; fs = ScreenState.Classify(inp, 10000);
      Check(gg.Check(new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.FoundryButton, AutoOn = true, Front = true, X = 1330, Y = 938 }, fs, 10000).Allowed, "sim: «Литейная» with the Foundry not shown");
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
      // (7) the model's build plan screen, sell marking, Foundry slot tab / Ascension pick and screens' late parts: a pop-up that came while the slow
      // laptop searched for the inventory / Foundry (228, 6727, 15263, 2032, 359), after the run's first read (21515: no click under it), a moment
      // before a click (22466: a drag on a never-click button), «Литейная» with the Foundry open but not read yet (2229: on «Вознести»), the sell
      // pilot's guess beside an exact scroll (9) and its click on the old scroll after a late drag (20), drags into a pop-up for ever (2640)
      // (found with the region-scan reader, whose late finds they need: its model --scan; the rules are kept with the window-table reader as well)
      foreach (bool scan in new[] { true, false })
        foreach (int seed in new[] { 9, 20, 228, 359, 2032, 2229, 2640, 6727, 15263, 21515, 22466 }) {
          string e = SimRun(seed, false, false, oc, geo, null, false, scan);
          Check(e == "", "sim: seed " + seed + (scan ? " (scan reader)" : "") + " keeps every rule" + (e != "" ? ": " + e.Split('\n')[0] + " " + (e.Split('\n').Length > 1 ? e.Split('\n')[1].Trim() : "") : ""));
        }
      // (2032 and 228 keep their rules but no longer end done: the pilots click sooner now (the effect confirmed, the list settled) and the
      // pop-up the game opens after a click lands before its answer - the safe stop "nochange" is right there; 9 the same since the named
      // unknown screen's back is pressed after 500 ms: a game pop-up lands on its first sell cell's click)
      foreach (int seed in new[] { 20, 2229, 6727, 15263, 22466 }) {
        var o1 = new Dictionary<string, int>(); SimRun(seed, false, false, o1, geo, null, false, true);
        string got = string.Join(",", new List<string>(o1.Keys).ToArray());
        Check(got.EndsWith(" done"), "sim: seed " + seed + " reaches its end (" + got + ")");
      }
      // (found with the region-scan reader: its model. 161 with the window-table reader: the share pop-up the game opens over the hero screen
      // comes earlier in the run and has no exit but Escape - never pressed on the pilot's own screen, the safe stop "dangerous")
      foreach (int seed in new[] { 19, 62, 82, 161, 250 }) {
        var o1 = new Dictionary<string, int>(); SimRun(seed, false, false, o1, geo, null, false, true);
        string got = string.Join(",", new List<string>(o1.Keys).ToArray());
        Check(got.EndsWith(" done"), "sim: seed " + seed + " brings up the plan's hero (" + got + ")");
      }
      // (8) the 50 000-seed soak: a pop-up that came under an offer list (25906: the forms were not read again when the list went - a click under
      // it), Escape while the slow laptop's Foundry was shown but not read yet (19461, 30660, 46982, 55636 slow: Escape on the pilot's own screen);
      // the rest were a pilot's flow form the model opened by itself over its screen (the game opens those only from that screen's own buttons)
      foreach (int seed in new[] { 11812, 12485, 14365, 19310, 25906, 26380, 56891 }) {
        string e = SimRun(seed, false, false, oc, geo);
        Check(e == "", "sim: seed " + seed + " keeps every rule" + (e != "" ? ": " + e.Split('\n')[0] + " " + (e.Split('\n').Length > 1 ? e.Split('\n')[1].Trim() : "") : ""));
      }
      // (9) the slow laptop's Ascension list coasts after a drag (live 1.6.66.354: the cell placed from a read of a list still moving, the
      // click changed nothing - «nochange»): the cell waits for two equal reads, a click without effect is placed again once
      {
        var o9 = new Dictionary<string, int>(); int asc = 0;
        for (int seed = 0; seed < 300; seed++) if (SimRun(seed, true, false, o9, geo).Contains("ASCENSION CELL")) asc++;
        int ld; o9.TryGetValue("Locate slow done", out ld);
        Check(asc == 0 && ld > 0, "sim: slow Ascension scroll, 300 slow seeds: no Locate stop on a cell click without effect (" + asc + " stops, " + ld + " done)");
      }
      foreach (int seed in new[] { 11753, 19461, 30660, 30839, 46982, 51899, 55636, 59452 }) {
        string e = SimRun(seed, true, false, oc, geo);
        Check(e == "", "sim: slow seed " + seed + " keeps every rule" + (e != "" ? ": " + e.Split('\n')[0] + " " + (e.Split('\n').Length > 1 ? e.Split('\n')[1].Trim() : "") : ""));
      }
    }
    SimOldFlows = false;
  }

  /// <summary>The city built again after the guild (live 1.6.66.385, Form_UnionBoss > Form_UnionAdd > the city: the bar's press 1 s after the city
  /// came was lost, the run stopped "nochange"), in a full-size and a tiny game window (526x296): every city flow reaches its goal - the bar is
  /// searched again on the new city and has to settle, a lost press is read again and pressed once more.</summary>
  static void SimCityRebuilt(GameGeometry geo) {
    var flows = new[] { PilotFlow.Sell, PilotFlow.ArtSell, PilotFlow.LocateAscend, PilotFlow.LocateRecast, PilotFlow.LocateArtPromote };
    foreach (var size in new[] { new[] { 1920.0, 1009.0 }, new[] { 526.0, 296.0 } }) {
      int runs = 0, done = 0, lost = 0; string fail = "";
      foreach (var flow in flows) for (int i = 0; i < 4; i++) {
        int seed = 770000 + (int)flow * 31 + i; bool slow = i % 2 == 1;
        ReadTiming.Take();
        var g = new SimGame(seed, slow, 0, size[0], size[1]); g.Geo = geo; g.SpawnP = 0; g.CityRebuild = true;
        SimStartAt(g, "Guild", 0);
        var oc = new Dictionary<string, int>(); var outc = new string[1];
        string e = SimDrive(g, flow, slow, 0, new Random(seed), seed, false, oc, geo, null, false, null, outc);
        runs++; lost += g.LostPresses;
        if (e != "" && fail == "") fail = flow + (slow ? " slow: " : " fast: ") + e.Split('\n')[0];
        else if (outc[0] == "done") done++;
        else if (fail == "") fail = flow + (slow ? " slow: " : " fast: ") + outc[0];
      }
      string sz = (int)size[0] + "x" + (int)size[1];
      Check(fail == "" && done == runs, "sim: the city built again after the guild, " + sz + ": " + done + "/" + runs + " runs at their goal" + (fail != "" ? " - " + fail : ""));
      Check(lost > 0, "sim: the city built again, " + sz + ": a press lost while the bar came in was pressed again (" + lost + " lost)");
    }
  }

  /// <summary>Live 1.6.66.397 on a small window (526x296): the readers failed now and then while the window table knew the forms, the screen went
  /// City > Unknown > Inventory and the hint jumped. Every flow, a full-size and the small window, the readers failing a third of their reads:
  /// with the window table the screen's kind never leaves the game's top screen, and every run reaches its goal (the old way, the readers alone, is printed for the comparison).</summary>
  static void SimTableKind(GameGeometry geo) {
    var flows = new[] { PilotFlow.Sell, PilotFlow.ArtSell, PilotFlow.LocateAscend, PilotFlow.LocateRecast, PilotFlow.LocateArtPromote, PilotFlow.Equip };
    foreach (var size in new[] { new[] { 1920.0, 1009.0 }, new[] { 526.0, 296.0 } }) {
      string sz = (int)size[0] + "x" + (int)size[1]; int oldDone = 0;
      foreach (bool table in new[] { false, true }) {
        int runs = 0, done = 0, flips = 0, screenHalts = 0; string broke = "", other = "";
        foreach (var flow in flows) for (int i = 0; i < 4; i++) {
          int seed = 397000 + (int)flow * 37 + i; bool slow = i % 2 == 1;
          ReadTiming.Take();
          var g = new SimGame(seed, slow, 0.05, size[0], size[1]); g.Geo = geo; g.SpawnP = 0; g.SubFailP = 0.3; g.NoTable = !table;
          var oc = new Dictionary<string, int>(); var outc = new string[1];
          string e = SimDrive(g, flow, slow, 0.05, new Random(seed), seed, false, oc, geo, null, false, null, outc);
          runs++; flips += g.KindFlips;
          string tag = flow + (slow ? " slow: " : " fast: ");
          if (e != "") { if (broke == "") broke = tag + e.Split('\n')[0]; }
          else if (outc[0] == "done") done++;
          else {
            if (outc[0] == "halt:screen" || outc[0] == "halt:unknown") screenHalts++;
            if (other == "") other = tag + outc[0];
          }
        }
        if (!table) { oldDone = done; Console.WriteLine("  (the old way, " + sz + ", readers only: " + done + "/" + runs + " runs at their goal, " + flips + " kind flips" + (other != "" ? "; " + other : "") + ")"); continue; }
        Check(broke == "", "sim: failing sub-reads, " + sz + ", window table: every rule kept" + (broke != "" ? " - " + broke : ""));
        Check(flips == 0, "sim: failing sub-reads, " + sz + ", window table: the screen's kind stays the game's top screen (" + flips + " flips)");
        Check(screenHalts == 0 && done >= oldDone && done >= runs - 3, "sim: failing sub-reads, " + sz + ", window table: " + done + "/" + runs + " runs at their goal (the old way " + oldDone + "), no stop on a wrong or unknown screen" + (other != "" ? " (" + other + ")" : ""));
      }
    }
  }

  /// <summary>Live 1.6.66.393: «Показать в игре» right after the login, the game's pop-up chain over the city - Form_DailyCheckIn (its × not
  /// where the field name says: m_BtnClose missing), Form_ItemReward (tap to close, it was no offer then), Form_AnnouncementHall coming while
  /// «Инвентарь» was on its way (the run stopped "nochange"), Form_ActPopupNewHero after it. Every city flow, fast and slow, a full-size and
  /// a small window: the chain is closed pop-up by pop-up (the next one waited for), a pop-up that takes the pilot's click is a retry, and the
  /// run reaches its goal - no stop, no click on a buy / claim button, the gate's caps kept.</summary>
  static void SimLoginChain(GameGeometry geo) {
    bool was = ClickGate.DefaultResilient; ClickGate.DefaultResilient = true;
    try {
      var flows = new[] { PilotFlow.Sell, PilotFlow.ArtSell, PilotFlow.LocateAscend, PilotFlow.LocateRecast, PilotFlow.LocateArtPromote, PilotFlow.Equip };
      foreach (var size in new[] { new[] { 1920.0, 1009.0 }, new[] { 1280.0, 720.0 } }) {
        int runs = 0, done = 0, retried = 0, taken = 0; string fail = "";
        foreach (var flow in flows) for (int i = 0; i < 6; i++) {
          int seed = 393000 + (int)flow * 41 + i; bool slow = i % 2 == 1;
          ReadTiming.Take();
          var g = new SimGame(seed, slow, 0, size[0], size[1]); g.Geo = geo; g.SpawnP = 0; g.LiveButtons = i % 3 != 2;
          g.Push("Form_DailyCheckIn");
          bool newHero = false, annClosed = false, heroClosed = false, rewardClosed = false, ann = false; var rr = new Random(seed);
          bool annInPress = i % 2 == 0;   // AnnouncementHall: in the time of the pilot's first press after the reward (live), or a few seconds on
          g.Popped = f => {
            if (f == "Form_DailyCheckIn") g.PushOnCity(200 + rr.Next(400), "Form_ItemReward");
            else if (f == "Form_ItemReward") { rewardClosed = true; if (!annInPress) { ann = true; g.PushOnCity(1100 + rr.Next(3500), "Form_AnnouncementHall"); } }
            else if (f == "Form_AnnouncementHall") annClosed = true;
            else if (f == "Form_ActPopupNewHero") heroClosed = true;
          };
          // the pop-ups that come while the pilot works: AnnouncementHall within its press's time (the press is lost, live 1.6.66.393),
          // ActPopupNewHero in the very moment of the next press on the city (it takes that press)
          g.BeforeClick = tg => {
            if (tg == ClickTarget.OfferClose || g.Full != SimCity) return;
            if (rewardClosed && !ann) { ann = true; g.PushLater(rr.Next(60), "Form_AnnouncementHall"); }
            else if (!newHero && annClosed && g.Top == SimCity) { newHero = true; taken++; g.Push("Form_ActPopupNewHero"); }
          };
          var oc = new Dictionary<string, int>(); var outc = new string[1];
          bool mv = Environment.GetEnvironmentVariable("SIM_CHAIN_V") == seed.ToString();   // (one run's trace)
          string e = SimDrive(g, flow, slow, 0, new Random(seed), seed, mv, oc, geo, null, false, null, outc);
          if (mv) { Console.WriteLine(e); e = e.Contains("\n    !! ") ? e : ""; }
          runs++;
          if (e != "" && fail == "") fail = flow + (slow ? " slow: " : " fast: ") + e.Split('\n')[0] + (e.Split('\n').Length > 1 ? " " + e.Split('\n')[1].Trim() : "");
          else if (outc[0] == "done") done++;
          else if (fail == "") fail = flow + (slow ? " slow " : " fast ") + "seed " + seed + ": " + outc[0];
          if (newHero && heroClosed) retried++;
        }
        string sz = (int)size[0] + "x" + (int)size[1];
        Check(fail == "" && done == runs, "sim: the login pop-up chain (DailyCheckIn > ItemReward > AnnouncementHall > ActPopupNewHero), " + sz + ": " + done + "/" + runs + " runs at their goal" + (fail != "" ? " - " + fail : ""));
        Check(taken > 0 && retried == taken, "sim: the login chain, " + sz + ": the pop-up that took a press was closed and the step made again (" + retried + "/" + taken + ")");
      }
    } finally { ClickGate.DefaultResilient = was; }
  }

  /// <summary>Live 1.6.66.391: «Показать в игре» from another full screen took 9 s to name its top form, 3 s more to press its back arrow, 18 s
  /// from the city to «Инвентарь». Now (the fast PC, the live rects read): the top form named within a tick, its back pressed after 500 ms, the
  /// city's «Инвентарь» pressed on its live rect held still - the inventory open within about 1.5 s of the start; with the city built again
  /// (its bar sliding in) the press waits for the rect to stop and is never lost.</summary>
  /// <summary>The game's result form after the player's own press (live 1.6.66.401: Form_EquipRecastSuccess over the gem screen got an Escape,
  /// the gate stopped the run, the job was never done): the job is done by the form, a choice form waits, a tap-to-close one is closed by its tap.</summary>
  static void SimResultForms() {
    const double W = 1920, H = 1009; double U = Ui.Unit(W, H);
    // recast on the gem screen: «Улучшить» pressed by the player, the result with the old / new choice comes
    var gem = new RecastScreen { GemOpen = true, GemFunc = 7, GemSel = 777 };
    var sg = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopNew = "Form_EquipRecastSuccess", Rs = gem }, 5000);
    var dg = ClickGate.MapExit(sg, W, H, U);
    Check(sg.Kind == ScreenKind.Gem && sg.Top == "Form_EquipRecastSuccess" && dg.Move == MapMove.Wait, "sim: the recast result over the gem screen waits (the choice is the player's), no Escape (" + sg.Brief + ", " + dg + ")");
    var fin = new LocateFinish(); var subs = new[] { new long[] { 5, 10 } };
    fin.Update(false, 777, LocateKind.Done, gem, subs, 4000);
    Check(fin.Update(false, 777, LocateKind.Done, gem, subs, 5000, ScreenMap.SuccessShown(sg, "foundry")) == "recast", "sim: the recast result form marks the job done");
    // the Ascension: the result is closed by its tap through the gate, on the pilot's own screen (the Foundry), never an Escape there
    var asc = new RecastScreen { SublimingOpen = true, CurType = 2, AscendShown = true, AscendEquips = new long[] { 777 }, AscendSel = 777 };
    var sa = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopNew = "Form_EquipSublimedSuccess", Rs = asc }, 5000);
    var da = ClickGate.MapExit(sa, W, H, U);
    Check(da.Move == MapMove.Close && da.Point != null && da.Point.Y > H * 0.9, "sim: the ascension result is closed by its tap strip at the bottom (" + sa.Brief + ", " + da + ")");
    var g = new ClickGate(); g.Flows[ClickOwner.Locate] = PilotFlow.LocateAscend;
    var rq = new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.MapClose, AutoOn = true, Front = true, X = da.Point.X, Y = da.Point.Y, W = W, H = H, U = U, Form = "Form_EquipSublimedSuccess" };
    var res = g.Check(rq, sa, 7000);
    Check(res.Allowed, "sim: the gate allows the result's tap on the Foundry (" + res.Detail + ")");
    var fa = new LocateFinish();
    fa.Update(true, 777, LocateKind.AscendReady, asc, null, 4000);
    Check(fa.Update(true, 777, LocateKind.AscendReady, asc, null, 5000, ScreenMap.SuccessShown(sa, "ascend")) == "ascend", "sim: the ascension result form marks the job done");
  }

  /// <summary>Lab 1.6.66.402 (locate_art): a worn artifact's card hides «Продать», «Мастерская» moves left; pressed at the layout's place it changed
  /// nothing three times. The artifact promotion from the inventory's artifact tab: its cell, «Мастерская» pressed on its live rect, the workshop of
  /// that artifact - worn or not, fast and slow; without the live rect the worn card's button is missed (the lab's stop).</summary>
  /// <summary>Lab 1.6.66.403: «Показать в игре» for an artifact left the game on the workshop, the storage run after it read the screen as
  /// "ArtWorkshop details?" (the workshop was read only while an artifact request was guided: held, no back press) and said "no_city" for good.
  /// Every other flow started on the workshop, fast and slow: back to the city and on to its goal.</summary>
  static void SimLeftOnWorkshop(GameGeometry geo) {
    int runs = 0, done = 0; string fail = "";
    foreach (var flow in CityFirst.All) {
      if (flow == PilotFlow.LocateArtPromote) continue;
      for (int i = 0; i < 4; i++) {
        int seed = 403000 + (int)flow * 53 + i; bool slow = i % 2 == 1;
        ReadTiming.Take();
        var g = new SimGame(seed, slow, 0); g.Geo = geo; g.SpawnP = 0;
        SimStartAt(g, "Artifact workshop", 100 + (seed % 30));
        var oc = new Dictionary<string, int>(); var outc = new string[1];
        string e = SimDrive(g, flow, slow, 0, new Random(seed), seed, false, oc, geo, null, false, null, outc);
        runs++;
        if (e != "") { if (fail == "") fail = flow + (slow ? " slow: " : " fast: ") + e.Split('\n')[0]; }
        else if (outc[0] == "done") done++;
        else if (fail == "") fail = flow + (slow ? " slow: " : " fast: ") + outc[0];
      }
    }
    Check(fail == "" && done == runs, "sim: a run started on the artifact workshop (no artifact request): " + done + "/" + runs + " back to the city and at their goal" + (fail != "" ? " - " + fail : ""));
  }

  static void SimArtWorkshop(GameGeometry geo) {
    foreach (bool worn in new[] { false, true }) foreach (bool live in new[] { true, false }) {
      int ok = 0, runs = 0; string fail = ""; long worst = 0;
      for (int i = 0; i < 6; i++) {
        int seed = 402000 + i * 31 + (worn ? 7 : 0); bool slow = i % 2 == 1;
        var g = new SimGame(seed, slow, 0); g.Geo = geo; g.SpawnP = 0; g.LiveButtons = live; g.ArtWorn = worn;
        g.InvEquipTab = false; g.InvArtTab = true; g.InvArts.AddRange(new long[] { 7001, 7002, 4242, 7003 }); g.Push(SimInv);
        foreach (var f in g.Stack) g.StartForms.Add(f);
        var h = new SimHost(g, PilotFlow.LocateArtPromote, 100, geo) { ArtToWorkshop = true }; g.OwnScreen = SimInv;
        long t = 0; runs++;
        while (t < 30000 && h.Done == "") {
          g.Advance(t);
          if (t >= h.SendingUntil) { h.ReadScreens(t); h.Tick(); }
          if (g.Bad.Count > 0 || h.Gate.Halted(ClickOwner.Locate)) break;
          t += h.Gate.AnyPending ? 50 : 100;
        }
        if (h.Done == "done") { ok++; worst = Math.Max(worst, t); }
        else if (fail == "") fail = g.Bad.Count > 0 ? g.Bad[0] : h.Gate.Halted(ClickOwner.Locate) ? h.Gate.HaltReason(ClickOwner.Locate) : "no workshop within 30 s (" + h.PilotState + ")";
      }
      string what = "sim: artifact promotion" + (worn ? " of a worn artifact" : "") + (live ? " with" : " without") + " the live «Мастерская»: ";
      if (live || !worn) Check(ok == runs, what + "its workshop in " + ok + "/" + runs + " runs, worst " + worst + " ms" + (fail != "" ? " - " + fail : ""));
      else Check(ok == 0 && fail.Contains("nochange"), what + "the layout's place misses it (the lab's stop): " + ok + "/" + runs + (fail != "" ? " - " + fail : ""));
    }
  }

  static void SimUnknownToInventory(GameGeometry geo) {
    foreach (var start in new[] { "Guild", "Arena", "Shop" }) foreach (bool rebuild in new[] { false, true }) {
      if (rebuild && start != "Guild") continue;
      long worst = 0, named = -1; string fail = ""; int lost = 0;
      for (int i = 0; i < 6 && fail == ""; i++) {
        int seed = 391000 + i * 17 + start.Length;
        var g = new SimGame(seed, false, 0); g.Geo = geo; g.SpawnP = 0; g.LiveButtons = true; g.CityRebuild = rebuild;
        SimStartAt(g, start, 0);
        foreach (var f in g.Stack) g.StartForms.Add(f);
        var h = new SimHost(g, PilotFlow.LocateAscend, 100, geo); g.OwnScreen = SimFoundry;
        long t = 0, at = -1;
        while (t < 20000) {
          g.Advance(t);
          if (t >= h.SendingUntil) { h.ReadScreens(t); h.Tick(); }
          if (g.Full == SimInv) { at = t; break; }
          if (g.Bad.Count > 0 || h.Gate.Halted(ClickOwner.Locate)) break;
          t += h.Gate.AnyPending ? 50 : 100;
        }
        lost += g.LostPresses;
        if (g.Bad.Count > 0) fail = g.Bad[0];
        else if (at < 0) fail = "no inventory within 20 s (" + (h.Gate.Halted(ClickOwner.Locate) ? h.Gate.HaltReason(ClickOwner.Locate) : h.PilotState) + ")";
        worst = Math.Max(worst, at); if (h.CityReadyAt >= 0 && named < 0) named = h.CityReadyAt;
      }
      long cap = rebuild ? 1500 + SimGame.BarInMs : 1500;
      Check(fail == "" && worst <= cap && (named >= 0 || !rebuild) && lost == 0, "sim: " + start + (rebuild ? " (the city built again)" : "") + " -> city -> inventory on the fast PC within "
        + cap + " ms: worst " + worst + " ms" + (named < 0 && rebuild ? ", the live rect never used" : "") + (lost > 0 ? ", " + lost + " presses lost" : "") + (fail != "" ? " - " + fail : ""));
    }
  }

  /// <summary>City first, every pilot from every start (owner rule 2026-10-30): each flow x each start screen, fast and slow, no pop-ups of the
  /// game's own (the start's only). A cell is "ok" when every run reached the flow's goal; a stop with its reason is listed; a broken rule or a
  /// run without an end (silent waiting) fails. Returns the failed cells.</summary>
  public static readonly string[] SimMatrixStarts = { "City", "Hero (other hero)", "Hero (gear list)", "Inventory", "Inventory (other tab)", "Inventory (bulk)",
    "Foundry identify", "Foundry ascend", "Foundry recast", "Gem", "Artifact workshop", "Guild", "Arena", "Shop", "Popup over City", "Plan" };
  static List<string> SimMatrix(GameGeometry geo, bool print, int runs = 4) {
    var fails = new List<string>(); var sb = new StringBuilder();
    sb.Append("city-first matrix (" + runs + " runs a cell; ok = every run at its goal)\n");
    foreach (var flow in CityFirst.All) {
      sb.Append("  " + flow.ToString().PadRight(17));
      var cells = new List<string>();
      foreach (var start in SimMatrixStarts) {
        int done = 0; var why = new SortedDictionary<string, int>(); string fail = "";
        for (int i = 0; i < runs; i++) {
          int seed = 900000 + Array.IndexOf(SimMatrixStarts, start) * 97 + (int)flow * 7919 + i;
          bool slow = i % 2 == 1;
          ReadTiming.Take();
          var g = new SimGame(seed, slow, 0); g.Geo = geo; g.SpawnP = 0;   // (read failures and the game's own pop-ups: the soak's)
          SimStartAt(g, start, 100 + (seed % 30));
          var oc = new Dictionary<string, int>(); var outc = new string[1];
          bool mv = Environment.GetEnvironmentVariable("SIM_MATRIX_V") == flow + "/" + start;   // one cell's runs with their traces
          string e = SimDrive(g, flow, slow, 0, new Random(seed), seed, mv, oc, geo, null, false, null, outc);
          if (mv) { Console.WriteLine(e); e = e.Contains("\n    !! ") ? e : ""; }
          if (e != "") { fail = e.Split('\n')[0] + (e.Split('\n').Length > 1 ? " " + e.Split('\n')[1].Trim() : ""); break; }
          if (outc[0] == "done") done++;
          else { string w = outc[0].Split(' ')[0]; int n; why.TryGetValue(w, out n); why[w] = n + 1; }
        }
        string cell = fail != "" ? "FAIL" : done == runs ? "ok" : string.Join("+", new List<string>(why.Keys).ToArray());
        if (fail != "") fails.Add(flow + " from " + start + ": " + fail);
        cells.Add(start + "=" + cell);
      }
      sb.Append(string.Join(" | ", cells.ToArray())).Append("\n");
    }
    if (print) Console.Write(sb.ToString());
    return fails;
  }

  /// <summary>The storage cleanup's marking on a long list (--sell-bench): the game opens on «Массовая продажа» with <paramref name="items"/> items,
  /// <paramref name="n"/> of them wanted (spread at random); the run ends done or stopped. "" when it kept every rule; <paramref name="ms"/> = its time,
  /// <paramref name="markMs"/> = from the first cell click to the end.</summary>
  static string SimSellRun(int seed, bool slow, int n, int items, GameGeometry geo, double spawnP, out long ms, out long markMs, out int clicks, out int drags) {
    ReadTiming.Take();
    var g = new SimGame(seed, slow, 0); g.Geo = geo; g.SpawnP = spawnP;
    g.InvItems.Clear(); for (int i = 0; i < items; i++) g.InvItems.Add(9000 + i);
    g.InvEquipTab = true; g.InvBulk = true; g.Push(SimInv);
    foreach (var f in g.Stack) g.StartForms.Add(f);
    var h = new SimHost(g, PilotFlow.Sell, 100, geo);
    h.Wanted.Clear(); var wr = new Random(seed * 13 + 5); while (h.Wanted.Count < n) h.Wanted.Add(g.InvItems[wr.Next(items)]);
    g.OwnScreen = SimInv;
    var r = new Random(seed * 7919 + 13);
    long cap = 600000, t = 0, first = -1;
    while (t < cap) {
      g.Advance(t);
      if (t >= h.SendingUntil) { h.ReadScreens(t); h.Tick(); }
      if (first < 0 && h.SentCount(ClickTarget.InvBulkCell) > 0) first = t;
      if (g.Bad.Count > 0 || h.Done != "" || h.Gate.Halted(ClickOwner.Sell) || h.PilotState == "sell_failed") break;
      t += h.Gate.AnyPending || h.ListMoving || h.SellPolling ? 50 : slow ? 150 + r.Next(250) : 100;
    }
    ms = t; markMs = first < 0 ? 0 : t - first; clicks = h.SentCount(ClickTarget.InvBulkCell); drags = h.SentCount(ClickTarget.InvListDrag);
    var errs = new List<string>(g.Bad);
    if (h.Done != "") {
      g.Advance(t + 3000);
      var xtra = new List<string>(); foreach (var u in g.InvSel) if (!h.Wanted.Contains(u)) xtra.Add(u.ToString());
      var miss = new List<string>(); foreach (var u in h.Wanted) if (!g.InvSel.Contains(u)) miss.Add(u.ToString());
      if (xtra.Count > 0 || miss.Count > 0) errs.Add("SELL MARKS WRONG: extra [" + string.Join(",", xtra.ToArray()) + "] missing [" + string.Join(",", miss.ToArray()) + "]");
    } else {
      var xtra = new List<string>(); foreach (var u in g.InvSel) if (!h.Wanted.Contains(u)) xtra.Add(u.ToString());
      if (xtra.Count > 0) errs.Add("SELL STOPPED WITH WRONG MARKS: extra [" + string.Join(",", xtra.ToArray()) + "]");
    }
    string outcome = h.Done != "" ? "done" : h.Gate.Halted(ClickOwner.Sell) ? "halt:" + h.Gate.HaltReason(ClickOwner.Sell) : t >= cap ? "TIMEOUT" : "wait:" + h.PilotState;
    if (errs.Count == 0) return outcome == "done" ? "" : "~" + outcome;
    var sb = new StringBuilder("seed " + seed + (slow ? " slow" : " fast") + " n " + n + " -> " + outcome);
    foreach (var e in errs) sb.Append("\n    !! " + e);
    foreach (var l in g.Log) sb.Append("\n      " + l);
    return sb.ToString();
  }

  /// <summary>--sell-bench [runs] [--spawn]: seconds a marked item and the total for 8 / 30 / 100 wanted items, both machines (--spawn: the game's own pop-ups as in the soak).</summary>
  static int SellBench(GameGeometry geo, int runs, int from, bool spawn) {
    int broke = 0;
    Console.WriteLine("sell bench: " + runs + " runs each, 320 items in the list, " + (spawn ? "the game's pop-ups on" : "no pop-ups"));
    foreach (bool slow in new[] { false, true })
      foreach (int n in new[] { 8, 30, 100 }) {
        long tot = 0, mark = 0; int done = 0, cl = 0, dr = 0; var ends = new SortedDictionary<string, int>();
        for (int k = 0; k < runs; k++) {
          long ms, mk; int c, d;
          string e = SimSellRun(from + k, slow, n, 320, geo, spawn ? 0.04 : 0, out ms, out mk, out c, out d);
          if (e.Contains("\n    !! ")) { broke++; if (broke <= 5) Console.WriteLine(e); }
          string end = e == "" ? "done" : e.StartsWith("~") ? e.Substring(1) : "BROKE";
          int x; ends.TryGetValue(end, out x); ends[end] = x + 1;
          if (e == "") { done++; tot += ms; mark += mk; cl += c; dr += d; }
        }
        var es = new List<string>(); foreach (var kv in ends) es.Add(kv.Key + " " + kv.Value);
        Console.WriteLine("  " + (slow ? "slow" : "fast") + " " + n.ToString().PadLeft(3) + " items: " + (done > 0 ? (tot / done / 1000.0).ToString("0.0") : "-") + " s total, "
          + (done > 0 ? (mark / done / (double)n / 1000.0).ToString("0.00") : "-") + " s an item (marking), " + (done > 0 ? (cl / (double)done).ToString("0.0") : "-") + " clicks, "
          + (done > 0 ? (dr / (double)done).ToString("0.0") : "-") + " drags  [" + string.Join(", ", es.ToArray()) + "]");
      }
    Console.WriteLine("sell bench: " + broke + " broke a rule");
    return broke == 0 ? 0 : 1;
  }

  /// <summary>The hero screen's artifact list for --art-bench: <paramref name="items"/> artifacts, <paramref name="free"/> of them free (the goal among them,
  /// near the end, the rest worn by other heroes), the list open on the «Артефакт» tab. The pilot runs on its own (no gate) against a model of the
  /// game: a click answers after the machine's latency, a drag's scroll glides to its place, «Скрыть надетое» rebuilds the list with the free ones
  /// from the top. The run ends when the goal's card is selected (ArtSel). <paramref name="old"/> = origin/main's way: no filter, 1 s after each drag.</summary>
  static string ArtListRun(int seed, bool slow, bool old, int items, int free, out long ms, out int clicks, out int drags) {
    var r = new Random(seed * 31 + (slow ? 7 : 3));
    double W = 1136, H = 640, U = Ui.Unit(W, H), k = U / GameGeometry.CanvasH;
    const long hero = 231, goal = 77777;
    int lat = slow ? 180 + r.Next(120) : 50 + r.Next(50), glide = slow ? 450 + r.Next(250) : 220 + r.Next(120);
    double gain = 0.85 + r.NextDouble() * 0.25;
    // the list in the game's order: the goal near the end (a low rarity), the other free ones spread among the worn ones
    var all = new List<long>(); var worn = new HashSet<long>();
    int goalAt = items - 1 - r.Next(Math.Max(1, items / 8));
    var freeAt = new HashSet<int> { goalAt }; while (freeAt.Count < free) freeAt.Add(r.Next(items));
    for (int i = 0; i < items; i++) { long u = i == goalAt ? goal : 50000 + i; all.Add(u); if (!freeAt.Contains(i)) worn.Add(u); }
    var view = ArtFrames.Rect(null, ArtEquipFrames.KeyList, W, H, U);
    double viewUnits = view.H / k;
    int hide = 0; double scroll = 0, from = 0, to = 0; long glideAt = -1; long sel = 0;
    var due = new List<KeyValuePair<long, Action>>();
    var pilot = new ArtEquipPilot { NoFilter = old, FixedDragWait = old };
    long t = 0, cap = 120000; clicks = drags = 0;
    Func<List<long>> shown = () => { if (hide == 0) return all; var l = new List<long>(); foreach (var u in all) if (!worn.Contains(u)) l.Add(u); return l; };
    while (t < cap && sel != goal) {
      // the game: due answers, the scroll's glide
      for (int i = 0; i < due.Count; i++) if (due[i].Key <= t) { due[i].Value(); due.RemoveAt(i); i--; }
      if (glideAt >= 0) { double f = Math.Min(1, (t - glideAt) / (double)glide); f = 1 - (1 - f) * (1 - f); scroll = from + (to - from) * f; if (f >= 1) glideAt = -1; }
      var list = shown();
      double maxScroll = Math.Max(0, Math.Ceiling(list.Count / 3.0) * 116 - viewUnits);
      var hs = new HeroScreen { Hero = hero, FormOk = true, Tab = 7, ArtRead = true, ArtListOpen = true, ArtSel = sel, ArtTargetOwner = 0, ArtWorn = 9001, ArtHideWorn = hide, ArtGoalFit = 1,
                                ArtList = new RFX.ListRead { ScrollY = Math.Round(scroll, 1), Cols = 3, RowUnits = 116 } };
      hs.ArtUids.AddRange(list);
      int gi = list.IndexOf(goal), wa = 0; for (int i = 0; i < gi; i++) if (worn.Contains(list[i])) wa++;
      hs.ArtWornBefore = wa;
      var st = ArtEquipGuide.Next(hero, goal, hs, false);
      var v = new ArtEquipView { NowMs = t, Foreground = true, W = W, H = H, U = U, Step = st, Hero = hs };
      // the live cell read (the grid's cells): the card wholly in the viewport
      if (st.Kind == ArtEquipKind.PickCell && gi >= 0) {
        var cf = ArtFrames.Cell(null, ArtEquipFrames.KeyList, hs.ArtList, gi, 3, 116, W, H, U);
        if (cf != null && cf.Mark.Kind == "box" && cf.Mark.H >= 110 * k * 0.98) v.CellLive = new double[] { cf.Mark.X, cf.Mark.Y, cf.Mark.W, cf.Mark.H };
      }
      var mv = pilot.Step(v);
      if (pilot.Stopped) break;
      var a = mv.Action;
      if (a.Kind == AutoKind.Drag) {
        drags++;
        double s1 = Math.Max(0, Math.Min(maxScroll, scroll - a.DY / k * gain));
        long send = 120;   // the drag's own time (press, move, release)
        due.Add(new KeyValuePair<long, Action>(t + send + lat, () => { from = scroll; to = s1; glideAt = t; }));
      } else if (a.Kind == AutoKind.Click) {
        clicks++;
        double cx = a.X, cy = a.Y;
        if (mv.Todo == ArtEquipTodo.Filter) due.Add(new KeyValuePair<long, Action>(t + lat, () => { hide = 1 - hide; scroll = 0; glideAt = -1; }));
        else if (mv.Todo == ArtEquipTodo.Cell) due.Add(new KeyValuePair<long, Action>(t + lat, () => {
          var l2 = shown(); int g2 = l2.IndexOf(goal);
          var cf = g2 >= 0 ? ArtFrames.Cell(null, ArtEquipFrames.KeyList, new RFX.ListRead { ScrollY = scroll, Cols = 3, RowUnits = 116 }, g2, 3, 116, W, H, U) : null;
          if (cf != null && cf.Mark.Kind == "box" && cx >= cf.Mark.X && cx <= cf.Mark.X + cf.Mark.W && cy >= cf.Mark.Y && cy <= cf.Mark.Y + cf.Mark.H) sel = goal;
        }));
      }
      t += 50;
    }
    ms = t;
    return sel == goal ? "" : pilot.Stopped ? "stop:" + pilot.Reason : "TIMEOUT";
  }

  /// <summary>--art-bench [runs]: seconds from the open artifact list to the goal's card selected, 200 artifacts (the filter leaves 10), both machines, origin/main's way vs now.</summary>
  static int ArtBench(int runs, int from) {
    int bad = 0;
    Console.WriteLine("artifact bench: " + runs + " runs each, 200 artifacts in the list, 10 free (the goal one of them, near the end)");
    foreach (bool slow in new[] { false, true })
      foreach (bool old in new[] { true, false }) {
        long tot = 0, worst = 0; int done = 0, cl = 0, dr = 0; var ends = new SortedDictionary<string, int>();
        for (int i = 0; i < runs; i++) {
          long ms; int c, d; string e = ArtListRun(from + i, slow, old, 200, 10, out ms, out c, out d);
          string end = e == "" ? "done" : e; int x; ends.TryGetValue(end, out x); ends[end] = x + 1;
          if (e == "") { done++; tot += ms; cl += c; dr += d; worst = Math.Max(worst, ms); } else if (!old) bad++;
        }
        var es = new List<string>(); foreach (var kv in ends) es.Add(kv.Key + " " + kv.Value);
        Console.WriteLine("  " + (slow ? "slow" : "fast") + " " + (old ? "before (scroll, 1 s waits)" : "after (filter, live cell, still wait)") + ": "
          + (done > 0 ? (tot / done / 1000.0).ToString("0.00") : "-") + " s average, " + (worst / 1000.0).ToString("0.00") + " s worst, "
          + (done > 0 ? (cl / (double)done).ToString("0.0") : "-") + " clicks, " + (done > 0 ? (dr / (double)done).ToString("0.0") : "-") + " drags  [" + string.Join(", ", es.ToArray()) + "]");
      }
    Console.WriteLine("artifact bench: " + bad + " runs of the new way did not end on the goal");
    return bad == 0 ? 0 : 1;
  }

  /// <summary>--graph: the UI graph's numbers and how much of it the run covered.</summary>
  static void SimGraphReport() {
    SimGraphInit();
    int w = 0, d = 0, k = 0, open = 0, close = 0, req = 0, auto = 0, unknownT = 0;
    foreach (var f in UiGraph.All) {
      if (f.IsAuto) auto++;
      foreach (var x in f.Widgets) {
        w++; if (x.Danger) d++; if (x.Confirm) k++;
        foreach (var e in x.Effects) { if (e.Kind == 'o') { open++; if (e.Target.StartsWith("?")) unknownT++; } else if (e.Kind == 'c' || e.Kind == 'x') close++; else if (e.Kind == 'r') req++; }
      }
    }
    int seenReach = 0; foreach (var f in simReach) if (SimGraphSeen.Contains(f)) seenReach++;
    Console.WriteLine("ui graph (game " + UiGraph.GameVersion + "): " + UiGraph.All.Count + " forms, " + w + " widgets with an effect (" + d + " dangerous, " + k + " open a confirm), "
      + open + " opens (" + unknownT + " to an id with no form), " + close + " closes, " + req + " requests; " + auto + " forms the game opens by itself ("
      + simAutoCity.Count + " over the city, " + simAutoAny.Count + " results / data)");
    Console.WriteLine("  reachable from the city by safe clicks: " + simReach.Count + " forms (the app's own screens left out); shown in this run: " + seenReach
      + " of them, " + SimGraphSeen.Count + " forms in all; presses on a graph widget " + SimGraphPressN + " (" + SimGraphDangerN + " on a dangerous one, " + SimGraphConfirmN + " opened a confirm)");
    foreach (var t in UiGraph.Targets) {
      var p = UiGraph.PathOf(t);
      Console.WriteLine("  path " + t.PadRight(18) + (p == null ? "NOT REACHABLE" : string.Join(" | ", Array.ConvertAll(p, s => s.ToString()))));
    }
  }

  /// <summary>The centre of a form's dangerous widget whose path contains <paramref name="part"/> (client px), or null.</summary>
  static double[] DangerPoint(string form, string part, double W, double H, double U) {
    foreach (var w in UiGraph.DangerWidgets(form)) {
      if (w.Rect == null || w.Path.IndexOf(part, StringComparison.Ordinal) < 0) continue;
      var r = ScreenMap.Place(w.Rect, W, H, U); return new[] { r.CX, r.CY };
    }
    return null;
  }
  static GateResult ZGate(ClickTarget t, ClickOwner o, double[] pt, string form, List<string> shown, ScreenState s, bool orderRead, double W, double H, double U) {
    var g = new ClickGate { OrderRead = orderRead };
    return g.Check(new ClickRequest { Owner = o, Target = t, AutoOn = true, Front = true, X = pt[0], Y = pt[1], W = W, H = H, U = U, Form = form, Shown = shown }, s, 10000);
  }
  /// <summary>The window table's z-order (ScreenInputs.Order) and the click gate's read of every shown form: the three kinds of rule breaks the
  /// graph-driven sim found (game sim --graph, f36711b: 16 in 20 000 seeds) and the seeds that showed them.</summary>
  static void ZOrderTests() {
    double W = 1920, H = 1009, U = Ui.Unit(W, H);
    // the order itself: stack priority, then sorting order; a tie or an unread place (overlays aside) = not certain
    var z = ScreenState.ZOrder(new[] { new ScreenState.FormZ { Name = "Form_MainCity", Prio = 0, Sorting = 10, Readable = true }, new ScreenState.FormZ { Name = "Form_HeroAppraise", Prio = 2, Sorting = 5, Readable = true },
                                       new ScreenState.FormZ { Name = "Form_CharactorMain", Prio = 1, Sorting = 30, Readable = true }, new ScreenState.FormZ { Name = "Form_BranchDungeon", Prio = 2, Sorting = 9, Readable = true } });
    Check(z != null && string.Join(">", z.ToArray()) == "Form_BranchDungeon>Form_HeroAppraise>Form_CharactorMain>Form_MainCity", "z-order: by the stack's priority, then the sorting order, the top first (" + (z == null ? "null" : string.Join(">", z.ToArray())) + ")");
    Check(ScreenState.ZOrder(new[] { new ScreenState.FormZ { Name = "Form_HeroAppraise", Prio = 2, Sorting = 5, Readable = true }, new ScreenState.FormZ { Name = "Form_BranchDungeon", Prio = 2, Sorting = 5, Readable = true } }) == null, "z-order: two forms at one place - not certain");
    Check(ScreenState.ZOrder(new[] { new ScreenState.FormZ { Name = "Form_HeroAppraise", Prio = 2, Sorting = 5, Readable = true }, new ScreenState.FormZ { Name = "Form_BranchDungeon" } }) == null, "z-order: a form whose place is not read - not certain");

    // (1) an offer's × with a dangerous purchase dialog over it (seed 11920: Form_PurGiftDetail over Form_UnionWeekBossPopUp)
    {
      string offer = "Form_UnionWeekBossPopUp", buy = "Form_PurGiftDetail";
      var order = new List<string> { buy, offer, "Form_SuperStrikeTips", "Form_MainCity" };
      var s = ScreenState.Classify(new ScreenInputs { CityForm = true, CitySeen = true, Offers = new List<string> { offer }, Order = order }, 1000);
      Check(s.OfferCount == 0 && s.Top == buy && s.TopClass == 'D', "z-order: an offer under a dangerous dialog is not the top (" + s.Brief + ", top " + s.Top + ")");
      var pt = OfferForms.ClosePoint(null, offer, W, H, U); var p = new[] { pt.X, pt.Y };
      var v = ZGate(ClickTarget.OfferClose, ClickOwner.Locate, p, offer, null, s, true, W, H, U);
      Check(v.Verdict == GateVerdict.Deny && v.Reason == "dangerous", "z-order: the offer's × with the dialog over it - a stop, nothing pressed (" + v.Reason + ": " + v.Detail + ")");
      var su = ScreenState.Classify(new ScreenInputs { CityForm = true, CitySeen = true, Offers = new List<string> { offer } }, 1000);
      v = ZGate(ClickTarget.OfferClose, ClickOwner.Locate, p, offer, null, su, true, W, H, U);
      Check(v.Verdict == GateVerdict.Wait && v.Reason == "order", "z-order: the order and the other shown forms not read - no click (" + v.Reason + ")");
      v = ZGate(ClickTarget.OfferClose, ClickOwner.Locate, p, offer, new List<string> { "Form_SuperStrikeTips", buy }, su, true, W, H, U);
      Check(v.Verdict == GateVerdict.Deny && v.Reason == "dangerous", "z-order: no order, the dialog among the shown forms - a stop (" + v.Reason + ")");
      v = ZGate(ClickTarget.OfferClose, ClickOwner.Locate, p, offer, new List<string> { buy }, su, false, W, H, U);
      Check(v.Verdict == GateVerdict.Deny && v.Reason == "dangerous", "z-order: a host with no order at all still stops on the dialog shown (" + v.Reason + ")");
      // an offer under a screen opened later is not on top either (its × would land on that screen)
      var si = ScreenState.Classify(new ScreenInputs { CityForm = false, Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, Screens = new List<string> { SimInv }, Offers = new List<string> { offer }, Order = new List<string> { SimInv, offer, "Form_MainCity" } }, 1000);
      Check(si.OfferCount == 0 && si.Kind == ScreenKind.Inventory && !si.ModalOpen, "z-order: an offer under the inventory is not a pop-up on top (" + si.Brief + ")");
    }
    // (2) a pop-up's × under a full screen opened later: lands on its resource bar's «+» (seed 10704: Form_BranchDungeon over Form_HeroAppraise)
    {
      var plus = DangerPoint("Form_BranchDungeon", "m_ResourceBarItemPlus", W, H, U);
      Check(plus != null, "z-order: Form_BranchDungeon's resource bar «+» is a dangerous widget of the UI graph");
      var order = new List<string> { "Form_BranchDungeon", "Form_HeroAppraise", SimHero, "Form_MainCity" };
      var s = ScreenState.Classify(new ScreenInputs { CityForm = false, Hero = new HeroScreen { Hero = 100, FormOk = true, Tab = 1, Part = -1 }, Screens = new List<string> { SimHero }, Offers = new List<string>(), ShownKnown = "", TopNew = "Form_HeroAppraise,Form_BranchDungeon", Order = order }, 1000);
      Check(s.Top == "Form_BranchDungeon", "z-order: of the pop-ups over the hero screen the one on top is the later full screen (" + s.Top + ", " + s.Brief + ")");
      var d = ClickGate.MapExit(s, W, H, U);
      Check(d.Move != MapMove.Close || d.Form != "Form_HeroAppraise", "z-order: the map closes no pop-up under the full screen (" + d + ")");
      if (plus != null) {
        var v = ZGate(ClickTarget.MapClose, ClickOwner.Equip, plus, "Form_HeroAppraise", new List<string> { "Form_HeroAppraise", "Form_BranchDungeon" }, s, true, W, H, U);
        Check(!v.Allowed, "z-order: the pop-up's × under the full screen is never pressed (" + v.Reason + ": " + v.Detail + ")");
        var su = ScreenState.Classify(new ScreenInputs { CityForm = false, Hero = new HeroScreen { Hero = 100, FormOk = true, Tab = 1, Part = -1 }, Screens = new List<string> { SimHero }, Offers = new List<string>(), ShownKnown = "", TopNew = "Form_HeroAppraise,Form_BranchDungeon" }, 1000);
        v = ZGate(ClickTarget.MapClose, ClickOwner.Equip, plus, "Form_HeroAppraise", new List<string> { "Form_HeroAppraise", "Form_BranchDungeon" }, su, true, W, H, U);
        Check(v.Verdict == GateVerdict.Deny && v.Reason == "target", "z-order: no order - the point on a shown form's dangerous «+» is a stop (" + v.Reason + ": " + v.Detail + ")");
        v = ZGate(ClickTarget.MapClose, ClickOwner.Equip, plus, "Form_HeroAppraise", new List<string> { "Form_HeroAppraise", "Form_BranchDungeon" }, su, false, W, H, U);
        Check(v.Verdict == GateVerdict.Deny && v.Reason == "target", "z-order: a host with no order at all still stops on the «+» (" + v.Reason + ")");
      }
    }
    // (3) a pilot click on the inventory with a pop-up over it from the start (seed 14292: Form_ChooseChanelToSharePopUp's share button)
    {
      string pop = "Form_ChooseChanelToSharePopUp";
      var share = DangerPoint(pop, "m_GuideBtnShard", W, H, U);
      Check(share != null, "z-order: " + pop + "'s share button is a dangerous widget of the UI graph");
      var inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true };
      var order = new List<string> { "Form_IntegralRewardTip", pop, "Form_TournamentBonusPopUp", SimInv, "Form_MainCity" };
      var s = ScreenState.Classify(new ScreenInputs { CityForm = false, Inv = inv, Screens = new List<string> { SimInv }, Offers = new List<string>(), Order = order }, 1000);
      Check(s.Kind == ScreenKind.Inventory && s.ModalOpen && s.ModalWhy.Contains(pop), "z-order: a pop-up over the inventory since the start is a pop-up on top, not a part of it (" + s.Brief + ")");
      if (share != null) {
        var v = ZGate(ClickTarget.FoundryButton, ClickOwner.Locate, share, null, new List<string> { "Form_TournamentBonusPopUp", pop, "Form_IntegralRewardTip" }, s, true, W, H, U);
        Check(!v.Allowed, "z-order: FoundryButton is never pressed under it (" + v.Reason + ": " + v.Detail + ")");
        var su = ScreenState.Classify(new ScreenInputs { CityForm = false, Inv = inv, Screens = new List<string> { SimInv }, Offers = new List<string>() }, 1000);
        v = ZGate(ClickTarget.FoundryButton, ClickOwner.Locate, share, null, new List<string> { "Form_TournamentBonusPopUp", pop, "Form_IntegralRewardTip" }, su, true, W, H, U);
        Check(v.Verdict == GateVerdict.Deny && v.Reason == "target", "z-order: no order - the point on the shown pop-up's dangerous button is a stop (" + v.Reason + ": " + v.Detail + ")");
        // with the order, the inventory on top: the same point is not refused for the pop-up (it lies under the inventory)
        var top = ScreenState.Classify(new ScreenInputs { CityForm = false, Inv = inv, Screens = new List<string> { SimInv }, Offers = new List<string>(), Order = new List<string> { SimInv, pop, "Form_MainCity" } }, 1000);
        v = ZGate(ClickTarget.FoundryButton, ClickOwner.Locate, share, null, new List<string> { pop }, top, true, W, H, U);
        Check(v.Reason != "target" && v.Reason != "covered" && v.Reason != "order" && !top.ModalOpen, "z-order: the pop-up under the inventory takes no part (" + v.Reason + ": " + v.Detail + ")");
      }
    }
    // a full screen over the known screens: its back arrow is pressed (the known screens under it are not on top)
    Check(ClickGate.AllBelow(new List<string> { "Form_PhysicalPrize", SimInv, "Form_MainCity" }, "Form_PhysicalPrize", SimInv) && !ClickGate.AllBelow(null, "Form_PhysicalPrize", SimInv),
          "z-order: a known screen under the full screen on top is below it (no order: not known)");
    // the seeds of the graph-driven sim's rule breaks (f36711b), with the order read and with it never certain
    var geo = GameGeometry.Parse(GeoJson(), GeoGame);
    bool was = SimGraph; double wasP = SimOrderFailP; SimGraph = true;
    foreach (double fp in new[] { -1.0, 1.0 })
      foreach (int seed in new[] { 10704, 11920, 14292, 14415, 15290, 15849, 16157, 16488, 16870, 22850, 23375, 26615, 27080, 27328, 27527, 27543 }) {
        SimOrderFailP = fp;
        string e = SimRun(seed, false, false, new Dictionary<string, int>(), geo);
        Check(e == "", "sim --graph: seed " + seed + (fp > 0 ? " (the order never certain)" : "") + " keeps every rule" + (e != "" ? ": " + e.Split('\n')[0] + " " + (e.Split('\n').Length > 1 ? e.Split('\n')[1].Trim() : "") : ""));
      }
    SimGraph = was; SimOrderFailP = wasP;
  }

  /// <summary>The UI graph against the pilots: loaded, every pilot target reachable by safe clicks, the generated path the pilot's own chain.</summary>
  static void UiGraphTests() {
    Check(UiGraph.All.Count >= ScreenMap.Count - 5 && UiGraph.ByName(SimCity) != null, "ui graph: " + UiGraph.All.Count + " forms loaded (the screen map has " + ScreenMap.Count + ")");
    foreach (var kv in UiGraph.PilotChains) {
      var got = UiGraph.Chain(UiGraph.PathOf(kv.Key));
      Check(got != null && string.Join(">", got) == string.Join(">", kv.Value), "ui graph: the path to " + kv.Key + " is the pilot's chain (" + (got == null ? "none" : string.Join(" > ", got)) + ")");
    }
    // every widget the generated paths press is safe and shown; the city's bar buttons the pilots press open what the pilots expect
    foreach (var t in UiGraph.Targets) {
      var p = UiGraph.PathOf(t); if (p == null) continue;
      foreach (var s in p) {
        UiGraph.Widget w = null; foreach (var x in UiGraph.ByName(s.From).Widgets) if (x.Path == s.Widget) w = x;
        Check(w != null && w.Safe, "ui graph: " + t + ": the step " + s + " presses a safe widget");
      }
    }
    var bp = UiGraph.Path(SimCity, SimInv);
    Check(bp != null && bp.Length == 1 && bp[0].Widget.EndsWith("m_Button_Backpack"), "ui graph: the city's «Инвентарь» (m_Button_Backpack) opens the inventory (" + (bp == null ? "none" : bp[0].ToString()) + ")");
    Check(UiGraph.AutoForms("activity:city").Count > 5, "ui graph: the city's pop-up queue (ActivityFormMgr) has its forms (" + UiGraph.AutoForms("activity:city").Count + ")");
  }

  static int SimMain(string[] args) {
    Layout.Use(TestLayout());
    int seeds = 500, from = 0; bool slowOnly = false, verbose = false, real = false, scan = false, timing = false, matrix = false; string list = null;
    for (int i = 1; i < args.Length; i++) {
      if (args[i] == "--slow-only") slowOnly = true;
      else if (args[i] == "--verbose") verbose = true;
      else if (args[i] == "--real") real = true;
      else if (args[i] == "--graph") SimGraph = true;
      else if (args[i] == "--order-fail" && i + 1 < args.Length) SimOrderFailP = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
      else if (args[i] == "--seed" && i + 1 < args.Length) { from = int.Parse(args[++i]); seeds = 1; }
      else if (args[i] == "--from" && i + 1 < args.Length) from = int.Parse(args[++i]);
      else if (args[i] == "--list" && i + 1 < args.Length) list = args[++i];
      else if (args[i] == "--digest") list = "--digest";
      else if (args[i] == "--scan") scan = true;
      else if (args[i] == "--timing") timing = true;
      else if (args[i] == "--sell-bench" || args[i] == "--spawn" || args[i] == "--art-bench") { }
      else if (args[i] == "--matrix") matrix = true;
      else if (args[i] == "--gate-log") SimGateLog = true;
      else { int n; if (int.TryParse(args[i], out n)) seeds = n; }
    }
    var geo = GameGeometry.Parse(GeoJson(), GeoGame);
    if (Array.IndexOf(args, "--sell-bench") >= 0) return SellBench(geo, seeds, from, Array.IndexOf(args, "--spawn") >= 0);
    if (Array.IndexOf(args, "--art-bench") >= 0) return ArtBench(seeds == 500 ? 200 : seeds, from);
    if (matrix) {   // the city-first matrix alone (tests/run-sim.sh --matrix [runs a cell])
      var mf = SimMatrix(geo, true, seeds > 0 && seeds != 500 ? seeds : 4);
      foreach (var f in mf) Console.WriteLine("  !! " + f);
      Console.WriteLine("city-first matrix: " + mf.Count + " cells failed");
      return mf.Count == 0 ? 0 : 1;
    }
    var outcomes = new SortedDictionary<string, int>(); var tmp = new Dictionary<string, int>(); var stats = new Dictionary<string, SimStat>();
    int bad = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int s = from; s < from + seeds; s++) {
      string e = SimRun(s, slowOnly, verbose, tmp, geo, list, real, scan, stats);
      bool broke = e.Contains("\n    !! ");
      if (broke) bad++;
      if (e != "" && (verbose || bad <= 5)) Console.WriteLine(e);
    }
    foreach (var kv in tmp) outcomes[kv.Key] = kv.Value;
    Console.WriteLine("game sim: " + seeds + " seeds from " + from + " in " + sw.ElapsedMilliseconds + " ms, " + bad + " broke a rule" + (SimGraph ? " (graph-driven)" : ""));
    if (SimGraph) SimGraphReport();
    foreach (var kv in outcomes) Console.WriteLine("  " + kv.Value.ToString().PadLeft(5) + "  " + kv.Key);
    if (timing) {   // the reader's costs and the pilots' times (--scan: the reader before the window table was the forms' source)
      Console.WriteLine("timing (" + (scan ? "region-scan reader" : "window-table reader") + "): done = mean time of the runs that ended done; confirm = click to its effect seen");
      var keys = new List<string>(stats.Keys); keys.Sort();
      foreach (var k in keys) {
        var x = stats[k];
        Console.WriteLine("  " + k.PadRight(12) + " done " + x.Done + "/" + x.Runs + " in " + (x.Done > 0 ? x.DoneMs / x.Done : 0) + " ms, confirm " + (x.ConfirmN > 0 ? x.ConfirmMs / x.ConfirmN : 0)
          + " ms (max " + x.ConfirmMax + ", " + x.ConfirmN + "), discovery " + (x.DiscN > 0 ? x.DiscMs / x.DiscN : 0) + " ms (" + x.DiscN + "), field " + (x.FieldN > 0 ? x.FieldUs / x.FieldN : 0) + " µs a tick");
      }
    }
    return bad == 0 ? 0 : 1;
  }
}
