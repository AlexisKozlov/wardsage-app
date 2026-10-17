// Replay tests for the screen state and the click gate (src/GameScreen.cs, src/ClickGate.cs).
//
// tests/fixtures/screens/*.json are recorded sequences of what the game readers gave (inventory, Foundry / gem screen, hero screen,
// the city's bottom bar) with the expected screen, guide step, pilot decision and gate verdicts per frame. They cover the live cases
// that went wrong (2026-10-17: auto click on in «Массовая продажа», the Foundry screen not recognised for a moment). _geometry.json is
// the site's screen geometry for the game version the fixtures were recorded on.
//
//   dotnet exec ... ScreenReplay.dll [fixtures dir]      exit code 0 = everything passed
//
// Fixture: {"name", "request": {"purpose","uid","slot","hero"}, "cols", "frames": [{"t", "inv", "rs", "hero", "city", "expect", "clicks"}]}
//   inv/rs/hero null or missing = that read failed. expect: screen, held, bulk, tab, modal, step, todo, why, halt (owner:reason),
//   frame (a frame for the step exists). clicks: {"owner","target","auto","front","cellIndex","cellKind","ok","expect":{"verdict","reason"}}
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RealmForge;

static class ScreenReplay {
  static int passed, failed;
  const double W = 1920, H = 1009;

  static void Check(bool ok, string what) {
    if (ok) { passed++; Console.WriteLine("  ok   " + what); } else { failed++; Console.WriteLine("  FAIL " + what); }
  }

  static Dictionary<string, object> O(object o) { return MiniJson.AsObject(o); }
  static List<object> L(object o) { return o as List<object>; }
  static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
  static double Num(Dictionary<string, object> d, string k, double def) { var v = Get(d, k); return v is double ? (double)v : def; }
  static bool Bool(Dictionary<string, object> d, string k, bool def) { var v = Get(d, k); return v is bool ? (bool)v : def; }
  static string Str(Dictionary<string, object> d, string k) { var v = Get(d, k); return v as string; }
  static long[] Longs(object o) { var l = L(o); var r = new List<long>(); if (l != null) foreach (var x in l) if (x is double) r.Add((long)(double)x); return r.ToArray(); }
  static int[] Ints(object o) { var l = L(o); var r = new List<int>(); if (l != null) foreach (var x in l) if (x is double) r.Add((int)(double)x); return r.ToArray(); }

  static RFX.ListRead ListOf(Dictionary<string, object> d) {
    if (d == null) return null;
    return new RFX.ListRead { ScrollY = Num(d, "scrollY", 0), Cols = (int)Num(d, "cols", 0), RowUnits = Num(d, "rowUnits", 0), First = (int)Num(d, "first", -1), Last = (int)Num(d, "last", -1) };
  }

  static InventoryScreen InvOf(Dictionary<string, object> d) {
    if (d == null) return null;
    var s = new InventoryScreen { Open = Bool(d, "open", true), EquipTab = Bool(d, "equip", true), Bulk = Bool(d, "bulk", false), BulkKnown = Bool(d, "known", true), Current = (long)Num(d, "current", 0) };
    s.Uids.AddRange(Longs(Get(d, "uids"))); foreach (var u in Longs(Get(d, "selected"))) s.Selected.Add(u);
    s.List = ListOf(O(Get(d, "list")));
    s.ListPtr = 1;
    return s;
  }

  static RecastScreen RsOf(Dictionary<string, object> d) {
    if (d == null) return null;
    var s = new RecastScreen { SublimingOpen = Bool(d, "foundry", false), CurType = (int)Num(d, "tab", -1), AscendShown = Bool(d, "ascendShown", false), PanelShown = Bool(d, "recastShown", false),
      AscendSel = (long)Num(d, "ascendSel", 0), AscendFilterOpen = Bool(d, "popup", false), ChoosePending = Bool(d, "choose", false), GemOpen = Bool(d, "gem", false), GemFunc = (int)Num(d, "gemFunc", -1), GemSel = (long)Num(d, "gemSel", 0) };
    s.AscendEquips = Longs(Get(d, "ascendEquips")); s.AscendParts = Ints(Get(d, "ascendParts")); s.GemList = Longs(Get(d, "gemList"));
    s.AscendList = ListOf(O(Get(d, "ascendList"))); s.GemFuncList = ListOf(O(Get(d, "gemFuncList")));
    return s;
  }

  static HeroScreen HeroOf(Dictionary<string, object> d) {
    if (d == null) return null;
    return new HeroScreen { Hero = (long)Num(d, "hero", 0), FormOk = Bool(d, "form", true), Tab = (int)Num(d, "tab", -1), Part = (int)Num(d, "part", -1), FilterOpen = Bool(d, "filter", false) };
  }

  static ScreenInputs InputsOf(Dictionary<string, object> f) {
    return new ScreenInputs { Inv = InvOf(O(Get(f, "inv"))), Rs = RsOf(O(Get(f, "rs"))), Hero = HeroOf(O(Get(f, "hero"))), CitySeen = Bool(f, "city", false), CityForm = Get(f, "cityForm") is bool ? (bool?)Bool(f, "cityForm", false) : null };
  }

  static string Verdict(GateResult r) { return r.Verdict + (r.Reason != "" && r.Reason != "ok" ? ":" + r.Reason : ""); }

  static void Run(string file, GameGeometry geo) {
    var fx = O(MiniJson.TryParse(File.ReadAllText(file, Encoding.UTF8)));
    string name = Str(fx, "name") ?? Path.GetFileName(file);
    Console.WriteLine(name);
    var logs = new List<string>();
    var tracker = new ScreenTracker { Sink = delegate (string s) { logs.Add(s); } };
    var gate = new ClickGate { Sink = delegate (string s) { logs.Add(s); } };
    LocateRequest req = null; PlanItem item = null;
    var rq = O(Get(fx, "request"));
    if (rq != null) {
      item = new PlanItem { Uid = (long)Num(rq, "uid", 0), Slot = (int)Num(rq, "slot", 0), Quality = (int)Num(rq, "quality", 0) };
      req = new LocateRequest { Id = "r", Purpose = Str(rq, "purpose") ?? "recast", Item = item, HeroUid = (long)Num(rq, "hero", 0) };
    }
    int cols = (int)Num(fx, "cols", 8);
    double U = Ui.Unit(W, H);
    var inv0 = new InvScroll();
    int n = 0;
    foreach (var fo in L(Get(fx, "frames"))) {
      var f = O(fo); n++;
      long t = (long)Num(f, "t", 0);
      string at = "frame " + n + " (t=" + t + ")";
      var raw = InputsOf(f);
      var st = tracker.Update(raw, t);
      gate.Observe(st, t);
      var ex = O(Get(f, "expect"));
      if (ex != null) {
        string e;
        if ((e = Str(ex, "screen")) != null) Check(st.Kind.ToString() == e, at + ": screen " + e + (st.Kind.ToString() == e ? "" : " (got " + st.Brief + ")"));
        if (Get(ex, "held") is bool) Check(st.Held == (bool)Get(ex, "held"), at + ": held=" + st.Held);
        if (Get(ex, "bulk") is bool) Check((st.Kind == ScreenKind.Inventory && st.InvBulk) == (bool)Get(ex, "bulk"), at + ": bulk=" + st.InvBulk);
        if ((e = Str(ex, "tab")) != null) Check(st.FoundryTabName == e, at + ": foundry tab " + e + " (got " + st.FoundryTabName + ")");
        if (Get(ex, "modal") is bool) Check(st.ModalOpen == (bool)Get(ex, "modal"), at + ": modal=" + st.ModalOpen);
        if (Str(ex, "halt") == "") { foreach (ClickOwner ow0 in Enum.GetValues(typeof(ClickOwner))) Check(!gate.Halted(ow0), at + ": " + ow0 + " not halted"); }
        else if ((e = Str(ex, "halt")) != null) {
          var parts = e.Split(':'); ClickOwner ow = (ClickOwner)Enum.Parse(typeof(ClickOwner), parts[0]);
          Check(gate.HaltReason(ow) == parts[1], at + ": " + parts[0] + " halted '" + parts[1] + "' (got '" + gate.HaltReason(ow) + "')");
        }
        if (Get(ex, "logged") is string) { string needle = (string)Get(ex, "logged"); bool has = logs.Exists(l => l.Contains(needle)); Check(has, at + ": logged '" + needle + "'"); }
      }
      LocateStep step = null;
      if (req != null) {
        step = LocateGuide.Next(req, tracker.Inv, tracker.Rs, tracker.Hero, cols, 1);
        var view = new LocateView { NowMs = t, Foreground = true, W = W, H = H, U = U, Step = step, Inv = tracker.Inv, Cols = cols,
          HeroScreenOpen = st.Kind == ScreenKind.HeroScreen, OtherFormOpen = st.Kind == ScreenKind.Foundry || st.Kind == ScreenKind.Gem, CityButton = st.Kind == ScreenKind.City,
          Geometry = geo, Rs = tracker.Rs, Item = item };
        if (ex != null) {
          string e;
          if ((e = Str(ex, "step")) != null) Check(step.Kind.ToString() == e, at + ": step " + e + (step.Kind.ToString() == e ? "" : " (got " + step.Kind + ")"));
          string why;
          var todo = LocateAuto.Decide(view, out why);
          if ((e = Str(ex, "todo")) != null) Check(todo.ToString() == e, at + ": pilot decides " + e + (todo.ToString() == e ? "" : " (got " + todo + ")"));
          if ((e = Str(ex, "why")) != null) Check(why == e, at + ": why '" + e + "' (got '" + why + "')");
          if (Get(ex, "frame") is bool) {
            var fr = LocateFrames.For(step, item, tracker.Rs, null, geo, W, H, U);
            Check((fr != null) == (bool)Get(ex, "frame"), at + ": frame for the step " + ((bool)Get(ex, "frame") ? "exists" : "is absent"));
          }
        }
      }
      var clicks = L(Get(f, "clicks"));
      if (clicks == null) continue;
      foreach (var co in clicks) {
        var c = O(co);
        var owner = (ClickOwner)Enum.Parse(typeof(ClickOwner), Str(c, "owner") ?? "Locate");
        var target = (ClickTarget)Enum.Parse(typeof(ClickTarget), Str(c, "target"));
        var fresh = ScreenState.Classify(raw, t);   // the gate reads fresh: no holding
        var cr = new ClickRequest { Owner = owner, Target = target, AutoOn = Bool(c, "auto", true), Front = Bool(c, "front", true) };
        var ok = new HashSet<long>(); var okl = Longs(Get(c, "ok")); foreach (var u in okl) ok.Add(u);
        if (okl.Length == 0 && item != null) ok.Add(item.Uid);
        cr.CellOk = ok;
        if (Get(c, "cellIndex") is double) {
          int idx = (int)(double)Get(c, "cellIndex");
          string kind = Str(c, "cellKind") ?? "inv";
          if (kind == "inv" && fresh.Inv != null) {
            var g = SellGeometry.Inventory(W, U, cols);
            double row1 = new InvScroll().Row1(g, fresh.Inv.List, double.NaN, U);
            cr.X = g.CellCentreX(idx % cols + 1, W, U); cr.Y = row1 + (idx / cols) * g.PitchY * U + g.CellHeight * U / 2;
            cr.CellUid = ClickGate.InvCellUid(fresh.Inv, g, row1, cols, cr.X, cr.Y, W, U);
          } else if (kind == "ascend" && fresh.Rs != null) {
            var fr = FoundryPlace.Cell(geo, "ascend", fresh.Rs.AscendList, idx, W, H, U);
            cr.X = fr.Mark.X + fr.Mark.W / 2.0; cr.Y = fr.Mark.Y + fr.Mark.H / 2.0;
            cr.CellUid = ClickGate.AscendCellUid(geo, fresh.Rs, cr.X, cr.Y, W, H, U);
          }
        }
        var res = gate.Check(cr, fresh, t);
        var cex = O(Get(c, "expect"));
        string want = cex != null ? Str(cex, "verdict") + (Str(cex, "reason") != null ? ":" + Str(cex, "reason") : "") : "Allow";
        string got = Verdict(res);
        Check(got == want, at + ": " + owner + " " + target + (cr.CellUid > 0 ? " on item " + cr.CellUid : "") + " -> " + want + (got == want ? "" : " (got " + got + ": " + res.Detail + ")"));
        if (res.Allowed) gate.Arm(owner, target, ClickGate.Expect(cr, fresh), t);
      }
    }
    // a line is logged for every verdict change, never one per frame
    Check(logs.Count < 60, "the log stays short (" + logs.Count + " lines)");
  }

  // ---- checks that need no fixture
  static void Static() {
    Console.WriteLine("whitelist");
    // never a target: the game's final buttons and confirmations (the equip helper's «Заменить» is the one confirm, opt-in)
    foreach (string nm in Enum.GetNames(typeof(ClickTarget))) {
      bool bad = nm.Contains("Ascend") && nm.Contains("Button") || nm.Contains("Recast") || nm.Contains("Enhance") || nm.Contains("Sell") && !nm.Contains("Bulk") || nm.Contains("Confirm") && nm != "EquipConfirm" || nm.Contains("Upgrade");
      Check(!bad, "no target named " + nm + " is a final button");
    }
    foreach (ClickOwner o in Enum.GetValues(typeof(ClickOwner))) {
      string wl = ClickGate.WhitelistText(o);
      Check(wl != "", o + " whitelist: " + wl);
    }
    Check(!ClickGate.WhitelistText(ClickOwner.Locate).Contains("BulkCell") && !ClickGate.WhitelistText(ClickOwner.Locate).Contains("BulkEnter"), "locate never clicks in the bulk-sale mode or on its button");
    Check(!ClickGate.WhitelistText(ClickOwner.Sell).Contains("InvListCell") && !ClickGate.WhitelistText(ClickOwner.Sell).Contains("Ascend") && !ClickGate.WhitelistText(ClickOwner.Sell).Contains("Foundry"), "the storage cleanup clicks no normal-mode cell and nothing of the Foundry");
    Check(!ClickGate.WhitelistText(ClickOwner.Equip).Contains("Inv") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("Foundry"), "the equip helper clicks only the city and the hero screen");

    Console.WriteLine("no screen, no click");
    var gate = new ClickGate();
    var req = new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.InvEquipTab, AutoOn = true, Front = true };
    var unknown = ScreenState.Classify(new ScreenInputs(), 0);
    Check(unknown.Kind == ScreenKind.Unknown, "nothing read: Unknown");
    Check(gate.Check(req, unknown, 0).Verdict == GateVerdict.Wait, "an unknown screen is waited for ...");
    Check(gate.Check(req, unknown, ClickGate.UnknownGraceMs + 10).Verdict == GateVerdict.Deny && gate.HaltReason(ClickOwner.Locate) == "unknown", "... and stops the pilot after the grace");
    gate.Resume(ClickOwner.Locate);
    Check(gate.Check(req, unknown, 5000).Verdict == GateVerdict.Wait, "a new request starts again");
    Check(gate.Check(new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.None, AutoOn = true, Front = true }, unknown, 6000).Verdict == GateVerdict.Deny, "a click with no target is never allowed");
    var gate2 = new ClickGate();
    Check(gate2.Check(new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.HeroBack, AutoOn = false, Front = true }, unknown, 0).Reason == "off", "auto click off: no click");
    Check(!gate2.Halted(ClickOwner.Locate), "... and that is not a stop (the hints go on, the switch may come back)");

    Console.WriteLine("remote switch");
    var sw = RemoteSwitches.Permissive();
    Check(!sw.Allows("locateAuto") && sw.Allows("locate") && sw.Allows("autoclick"), "locateAuto is off by default, the other switches are on");
    Check(!RemoteSwitches.Parse("{}").Allows("locateAuto"), "a reply without it: off");
    Check(!RemoteSwitches.Parse("{\"features\":{\"locateAuto\":\"yes\"}}").Allows("locateAuto"), "an odd value: off");
    Check(RemoteSwitches.Parse("{\"features\":{\"locateAuto\":true}}").Allows("locateAuto"), "only true switches it on");
    Check(RemoteSwitches.Parse("{\"features\":{\"locateAuto\":true}}").Allows("sellAuto") == false, "an unknown switch is never allowed");
    Check(RemoteSwitches.Parse("{\"readEnabled\":true}").Off().Count == 0, "a feature that starts off is not listed as 'switched off'");
    var back = RemoteSwitches.Parse(RemoteSwitches.Parse("{\"features\":{\"locateAuto\":true}}").ToJson());
    Check(back.Allows("locateAuto"), "the saved copy keeps it");
  }

  static int Main(string[] args) {
    string dir = args.Length > 0 ? args[0] : "tests/fixtures/screens";
    var geo = GameGeometry.Parse(File.ReadAllText(Path.Combine(dir, "_geometry.json"), Encoding.UTF8), "1.6.39.829.1");
    if (geo == null) { Console.WriteLine("FAIL the geometry fixture does not load"); return 1; }
    var files = Directory.GetFiles(dir, "*.json"); Array.Sort(files);
    foreach (var f in files) { if (Path.GetFileName(f).StartsWith("_")) continue; Run(f, geo); }
    Static();
    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed");
    return failed == 0 ? 0 : 1;
  }
}
