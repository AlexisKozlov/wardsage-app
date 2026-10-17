// Replay tests for the screen state and the click gate (src/GameScreen.cs, src/ClickGate.cs).
//
// tests/fixtures/screens/*.json are recorded sequences of what the game readers gave (inventory, Foundry / gem screen, hero screen,
// the city's bottom bar) with the expected screen, guide step, pilot decision and gate verdicts per frame. They cover the live cases
// that went wrong (2026-10-17: auto click on in «Массовая продажа», the Foundry screen not recognised for a moment). _geometry.json is
// the site's screen geometry for the game version the fixtures were recorded on.
//
//   dotnet exec ... ScreenReplay.dll [fixtures dir]      exit code 0 = everything passed
//
//   artifact promotion ("request": {"purpose":"artPromote","uid","need","cap","level","via","copies":[...],"artAuto":false}): inputs "artWs": {"open","tab","uid","level","stage","mats","matLevels","matItemIds","matSel","list"},
//   inv "artMode" / "artSel" (normal mode of the artifact tab); expect "artStep" (the guide's step key), "artFrame" (a frame for it exists), "artTodo" (what the promotion pilot decides: Hint | ArtTab | Cell | Workshop | PromoteTab | MatCell);
//   the gate lets the Locate owner click only the Art* targets and only with "artAuto": true (the site's artAuto switch with the auto click on); "sell": {"artAuto": true} is the same for the cleanup.
//   click cell kinds: "artbulk" (a card of the artifact tab's bulk-sale list; "untick": true takes a tick off), "artlist" (a card of the normal list), "mat" (an entry of the workshop's copy list; "itemOk": [...] = essence item ids)
//   equip plan with an artifact ("request": {"purpose":"artEquip","hero","uid","artAuto","autoConfirm"}): hero.art = {"read","open","sel","uids","owner","worn","list"}, frame "popup": true = Form_EquipPopUp seen;
//   expect "artEqStep" (ArtEquipStep.Key), "artEqTodo" (what the pilot decides, "btn": false = «Заменить» not seen as the blue button), "artEqFrame"; click cell kind "heroart" (a card of the hero screen's artifact list).
//   "artAuto": true = the site's switch with the player's auto click (the gate lets the Equip owner click the hero-screen artifact targets).
// Fixture: {"name", "request": {"purpose","uid","slot","hero"}, "cols", "frames": [{"t", "inv", "rs", "hero", "city", "expect", "clicks"}]}
//   inv/rs/hero null or missing = that read failed. expect: screen, held, bulk, artBulk, brief (a substring of the screen's brief), sellStep (the storage
//   cleanup's step key; needs "sell"), tab, modal, step, todo, why, halt (owner:reason), frame (a frame for the step exists). Optional "sell":
//   {"uids": [...], "artifact": true} = a storage cleanup list (an artifact list also switches the gate's artifact no-click rule on). clicks: {"owner","target","auto","front","cellIndex","cellKind","ok","expect":{"verdict","reason"}}
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
    var s = new InventoryScreen { Open = Bool(d, "open", true), EquipTab = Bool(d, "equip", true), Bulk = Bool(d, "bulk", false), BulkKnown = Bool(d, "known", true), Current = (long)Num(d, "current", 0),
      ArtTab = Bool(d, "art", false), ArtBulk = Bool(d, "artBulk", false), ArtBulkKnown = Bool(d, "artKnown", false), ArtMode = (int)Num(d, "artMode", 0), ArtSel = (int)Num(d, "artSel", 0), ArtSelKnown = Get(d, "artSel") != null };
    s.Uids.AddRange(Longs(Get(d, "uids"))); foreach (var u in Longs(Get(d, "selected"))) s.Selected.Add(u);
    s.List = ListOf(O(Get(d, "list")));
    s.ListPtr = 1;
    return s;
  }

  static ArtWorkshopScreen ArtWsOf(Dictionary<string, object> d) {
    if (d == null) return null;
    var s = new ArtWorkshopScreen { Open = Bool(d, "open", true), Tab = (int)Num(d, "tab", 0), Uid = (long)Num(d, "uid", 0), Level = (int)Num(d, "level", -1), Stage = (int)Num(d, "stage", -1) };
    var mats = Get(d, "mats");
    if (mats != null) { s.PanelKnown = true; s.Mats.AddRange(Longs(mats)); foreach (var x in Ints(Get(d, "matLevels"))) s.MatLevels.Add(x); foreach (var x in Ints(Get(d, "matSel"))) s.MatSel.Add(x); foreach (var x in Ints(Get(d, "matItemIds"))) s.MatItemIds.Add(x); }
    s.MatList = ListOf(O(Get(d, "list")));
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
    var h = new HeroScreen { Hero = (long)Num(d, "hero", 0), FormOk = Bool(d, "form", true), Tab = (int)Num(d, "tab", -1), Part = (int)Num(d, "part", -1), FilterOpen = Bool(d, "filter", false) };
    var a = O(Get(d, "art"));
    if (a != null) {
      h.ArtRead = Bool(a, "read", true); h.ArtListOpen = Bool(a, "open", false); h.ArtSel = (long)Num(a, "sel", 0); h.ArtUids.AddRange(Longs(Get(a, "uids")));
      h.ArtTargetOwner = (long)Num(a, "owner", -1); h.ArtWorn = (long)Num(a, "worn", -1); h.ArtList = ListOf(O(Get(a, "list"))); h.ArtListPtr = 1;
    }
    return h;
  }

  static ScreenInputs InputsOf(Dictionary<string, object> f) {
    return new ScreenInputs { Inv = InvOf(O(Get(f, "inv"))), Rs = RsOf(O(Get(f, "rs"))), Hero = HeroOf(O(Get(f, "hero"))), ArtWs = ArtWsOf(O(Get(f, "artWs"))), CitySeen = Bool(f, "city", false), CityForm = Get(f, "cityForm") is bool ? (bool?)Bool(f, "cityForm", false) : null,
      ModalExtra = Bool(f, "popup", false), ModalExtraWhy = Bool(f, "popup", false) ? ClickGate.ModalEquipPopup : "" };
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
    ArtPromoteInfo artReq = null; long eqHero = 0, eqUid = 0; bool eqAutoConfirm = false;
    if (rq != null && Str(rq, "purpose") == "artPromote") {
      artReq = new ArtPromoteInfo { Uid = (long)Num(rq, "uid", 0), Need = (int)Num(rq, "need", 1), Cap = (int)Num(rq, "cap", 0), Level = (int)Num(rq, "level", 0), Via = Str(rq, "via") ?? "none" };
      artReq.Copies.AddRange(Longs(Get(rq, "copies")));
      req = new LocateRequest { Id = "r", Purpose = "artPromote", Art = artReq };
      bool artAutoOn = Bool(rq, "artAuto", false);
      gate.NoLocateClicks = !artAutoOn; gate.ArtLocateAuto = artAutoOn;
    } else if (rq != null && Str(rq, "purpose") == "artEquip") {
      eqHero = (long)Num(rq, "hero", 0); eqUid = (long)Num(rq, "uid", 0); eqAutoConfirm = Bool(rq, "autoConfirm", false);
      gate.ArtEquipAuto = Bool(rq, "artAuto", false);
    } else if (rq != null) {
      item = new PlanItem { Uid = (long)Num(rq, "uid", 0), Slot = (int)Num(rq, "slot", 0), Quality = (int)Num(rq, "quality", 0) };
      req = new LocateRequest { Id = "r", Purpose = Str(rq, "purpose") ?? "recast", Item = item, HeroUid = (long)Num(rq, "hero", 0) };
    }
    int cols = (int)Num(fx, "cols", 8);
    var sellSpec = O(Get(fx, "sell")); var sellWanted = new HashSet<long>(); bool sellArt = false;
    if (sellSpec != null) { foreach (var u in Longs(Get(sellSpec, "uids"))) sellWanted.Add(u); sellArt = Bool(sellSpec, "artifact", false); bool sellAuto = Bool(sellSpec, "artAuto", false); gate.NoSellClicks = sellArt && !sellAuto; gate.ArtSellAuto = sellArt && sellAuto; }
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
        if (Get(ex, "artBulk") is bool) Check((st.Kind == ScreenKind.Inventory && st.InvArtBulk) == (bool)Get(ex, "artBulk"), at + ": artBulk=" + st.InvArtBulk);
        if ((e = Str(ex, "brief")) != null) Check(st.Brief.Contains(e), at + ": brief has '" + e + "' (got " + st.Brief + ")");
        if ((e = Str(ex, "sellStep")) != null) Check(SellHint.Classify(tracker.Inv, sellWanted, sellArt).Key == e, at + ": storage cleanup step " + e + " (got " + SellHint.Classify(tracker.Inv, sellWanted, sellArt).Key + ")");
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
      if (artReq != null) {
        step = LocateGuide.NextArt(req, tracker.Inv, tracker.ArtWs);
        string e;
        if (ex != null && (e = Str(ex, "artStep")) != null) Check(step.Key == e, at + ": art step " + e + (step.Key == e ? "" : " (got " + step.Key + ")"));
        if (ex != null && (e = Str(ex, "artTodo")) != null) {
          var av = new ArtView { NowMs = t, Foreground = true, W = W, H = H, U = U, Step = step, Inv = tracker.Inv, Ws = tracker.ArtWs, Art = artReq, Geometry = geo };
          string awhy; var atodo = ArtLocateAuto.Decide(av, out awhy);
          Check(atodo.ToString() == e, at + ": art pilot decides " + e + (atodo.ToString() == e ? "" : " (got " + atodo + " " + awhy + ")"));
          // whatever it decides, the action it would make is never on «Продвинуть» / «Улучшить»
          var am = new ArtPilot().Step(av);
          if (am.Action.Kind != AutoKind.None && (am.Todo == ArtTodo.PromoteTab || am.Todo == ArtTodo.MatCell)) Check(!ClickGate.OnNeverButton(geo, am.Action.X, am.Action.Y, W, H, U), at + ": the pilot's action is not on «Продвинуть» / «Улучшить»");
        }
        if (ex != null && Get(ex, "artFrame") is bool) {
          var fr = ArtFrames.For(step, tracker.Inv, tracker.ArtWs, geo, W, H, U);
          Check((fr != null) == (bool)Get(ex, "artFrame"), at + ": art frame " + ((bool)Get(ex, "artFrame") ? "exists" : "is absent"));
        }
      } else if (eqUid > 0) {
        var es = ArtEquipGuide.Next(eqHero, eqUid, tracker.Hero, st.ModalOpen && st.ModalWhy == ClickGate.ModalEquipPopup);
        string e;
        if (ex != null && (e = Str(ex, "artEqStep")) != null) Check(es.Key == e, at + ": art equip step " + e + (es.Key == e ? "" : " (got " + es.Key + ")"));
        if (ex != null && (e = Str(ex, "artEqTodo")) != null) {
          var av = new ArtEquipView { NowMs = t, Foreground = true, W = W, H = H, U = U, Step = es, Hero = tracker.Hero, Geometry = geo, AutoConfirm = eqAutoConfirm, ReplaceBtnSeen = Bool(ex, "btn", true) };
          string awhy; var atodo = ArtEquipPilot.Decide(av, out awhy);
          Check(atodo.ToString() == e, at + ": art equip pilot decides " + e + (atodo.ToString() == e ? "" : " (got " + atodo + " " + awhy + ")"));
          var am = new ArtEquipPilot().Step(av);
          if (am.Action.Kind != AutoKind.None) Check(!ArtEquipFrames.OnNeverButton(geo, am.Action.X, am.Action.Y, W, H, U), at + ": the pilot's action is not on the sell icon / «Быстро надеть»");
        }
        if (ex != null && Get(ex, "artEqFrame") is bool) Check((ArtEquipFrames.For(es, tracker.Hero, geo, W, H, U) != null) == (bool)Get(ex, "artEqFrame"), at + ": art equip frame " + ((bool)Get(ex, "artEqFrame") ? "exists" : "is absent"));
      } else if (req != null) {
        step = LocateGuide.Next(req, tracker.Inv, tracker.Rs, tracker.Hero, cols, 1);
        var view = new LocateView { NowMs = t, Foreground = true, W = W, H = H, U = U, Step = step, Inv = tracker.Inv, Cols = cols,
          HeroScreenOpen = st.Kind == ScreenKind.HeroScreen, OtherFormOpen = st.Kind == ScreenKind.Foundry || st.Kind == ScreenKind.Gem, CityButton = st.Kind == ScreenKind.City,
          Geometry = geo, Rs = tracker.Rs, Item = item, Purpose = req.Purpose };
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
        if (okl.Length == 0 && eqUid > 0) ok.Add(eqUid);
        cr.Hero = eqHero; cr.ArtUid = eqUid;
        cr.CellOk = ok; cr.Func = (int)Num(c, "func", 0); cr.Untick = Bool(c, "untick", false);
        var itemOk = new HashSet<int>(); foreach (var x in Ints(Get(c, "itemOk"))) itemOk.Add(x); cr.ItemOk = itemOk;
        if (Get(c, "cellIndex") is double) {
          int idx = (int)(double)Get(c, "cellIndex");
          string kind = Str(c, "cellKind") ?? "inv";
          if (kind == "inv" && fresh.Inv != null) {
            var g = SellGeometry.Inventory(W, U, cols);
            double row1 = new InvScroll().Row1(g, fresh.Inv.List, double.NaN, U);
            cr.X = g.CellCentreX(idx % cols + 1, W, U); cr.Y = row1 + (idx / cols) * g.PitchY * U + g.CellHeight * U / 2;
            cr.CellUid = ClickGate.InvCellUid(fresh.Inv, g, row1, cols, cr.X, cr.Y, W, U);
          } else if (kind == "artbulk" && fresh.Inv != null) {
            var g = SellGeometry.Artifacts(W, H, U, 7, null);
            double row1 = new InvScroll().Row1(g, fresh.Inv.List, double.NaN, U);
            cr.X = g.CellCentreX(idx % 7 + 1, W, U); cr.Y = row1 + (idx / 7) * g.PitchY * U + g.CellHeight * U / 2;
            cr.CellUid = ClickGate.InvCellUid(fresh.Inv, g, row1, 7, cr.X, cr.Y, W, U);
          } else if (kind == "artlist" && fresh.Inv != null) {
            var fr = ArtFrames.Cell(geo, "inventory.artList", fresh.Inv.List, idx, 7, 116, W, H, U);
            cr.X = fr.Mark.X + fr.Mark.W / 2.0; cr.Y = fr.Mark.Y + fr.Mark.H / 2.0;
            int ci = ClickGate.ListCardIndexAt(geo, "inventory.artList", fresh.Inv.List, 7, 116, cr.X, cr.Y, W, H, U);
            cr.CellUid = ci >= 0 && ci < fresh.Inv.Uids.Count ? fresh.Inv.Uids[ci] : 0;
          } else if (kind == "mat" && fresh.ArtWs != null) {
            var fr = ArtFrames.Cell(geo, "artifact.matList", fresh.ArtWs.MatList, idx, 3, 114, W, H, U);
            cr.X = fr.Mark.X + fr.Mark.W / 2.0; cr.Y = fr.Mark.Y + fr.Mark.H / 2.0;
            int ci = ClickGate.ListCardIndexAt(geo, "artifact.matList", fresh.ArtWs.MatList, 3, 114, cr.X, cr.Y, W, H, U);
            cr.MatIndex = ci >= 0 && ci < fresh.ArtWs.Mats.Count ? ci + 1 : 0;
            cr.CellUid = cr.MatIndex > 0 ? fresh.ArtWs.Mats[ci] : 0;
          } else if (kind == "heroart" && fresh.HeroScr != null) {
            var fr = ArtFrames.Cell(geo, "hero.artList", fresh.HeroScr.ArtList, idx, 3, 116, W, H, U);
            cr.X = fr.Mark.X + fr.Mark.W / 2.0; cr.Y = fr.Mark.Y + fr.Mark.H / 2.0;
            int ci = ClickGate.ListCardIndexAt(geo, "hero.artList", fresh.HeroScr.ArtList, 3, 116, cr.X, cr.Y, W, H, U);
            cr.CellUid = ci >= 0 && ci < fresh.HeroScr.ArtUids.Count ? fresh.HeroScr.ArtUids[ci] : 0;
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
    // never a target: the game's final buttons and confirmations (the equip helper's «Заменить» and the artifact steps' pop-up «Заменить» are the only confirms, opt-in)
    foreach (string nm in Enum.GetNames(typeof(ClickTarget))) {
      bool bad = nm.Contains("Ascend") && nm.Contains("Button") || nm.Contains("Recast") || nm.Contains("Enhance") || nm.Contains("Sell") && !nm.Contains("Bulk") || nm.Contains("Confirm") && nm != "EquipConfirm" && nm != "ArtTakeOverConfirm" || nm.Contains("Upgrade");
      Check(!bad, "no target named " + nm + " is a final button");
    }
    foreach (ClickOwner o in Enum.GetValues(typeof(ClickOwner))) {
      string wl = ClickGate.WhitelistText(o);
      Check(wl != "", o + " whitelist: " + wl);
    }
    // the recast path: the card's gem button and the gem list's row are on the list; the final buttons are in NO whitelist (not even as a name)
    string lwl = ClickGate.WhitelistText(ClickOwner.Locate);
    Check(lwl.Contains("InvCardGem@Inventory") && lwl.Contains("GemFuncRow@Gem"), "locate may click the card's gem button (Inventory) and the gem list's row (Gem)");
    foreach (ClickOwner oo in Enum.GetValues(typeof(ClickOwner))) {
      string w2 = ClickGate.WhitelistText(oo);
      Check(!w2.Contains("GemButton") && !w2.Contains("RecastButton") && !w2.Contains("AscendButton") && !w2.Contains("Upgrade") && !w2.Contains("Improve"), oo + ": no gem / recast / ascend button on the whitelist");
    }
    Check(!ClickGate.WhitelistText(ClickOwner.Sell).Contains("Gem") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("Gem"), "the storage cleanup and the equip helper never click the gem button or the gem list");
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

    Console.WriteLine("recast clicks: the item must be the request's");
    {
      var g = new ClickGate();
      var inv = new InventoryScreen { Open = true, EquipTab = true, Bulk = false, BulkKnown = true, Current = 1003 };
      var invOther = new InventoryScreen { Open = true, EquipTab = true, Bulk = false, BulkKnown = true, Current = 1004 };
      var invBulk = new InventoryScreen { Open = true, EquipTab = true, Bulk = true, BulkKnown = true, Current = 1003 };
      var ok1 = new HashSet<long> { 1003 };
      Func<ClickTarget, HashSet<long>, int, ClickRequest> R = (tg, ok, fn) => new ClickRequest { Owner = ClickOwner.Locate, Target = tg, AutoOn = true, Front = true, CellOk = ok, Func = fn };
      Check(new ClickGate().Check(R(ClickTarget.InvCardGem, ok1, 0), ScreenState.Classify(new ScreenInputs { Inv = inv }, 0), 0).Allowed, "card gem button: the request's item is selected -> allowed");
      var r2 = new ClickGate().Check(R(ClickTarget.InvCardGem, ok1, 0), ScreenState.Classify(new ScreenInputs { Inv = invOther }, 0), 0);
      Check(r2.Verdict == GateVerdict.Deny && r2.Reason == "cell", "card gem button: another item selected -> stop");
      r2 = new ClickGate().Check(R(ClickTarget.InvCardGem, ok1, 0), ScreenState.Classify(new ScreenInputs { Inv = invBulk }, 0), 0);
      Check(r2.Verdict == GateVerdict.Deny && r2.Reason == "bulk", "card gem button: bulk-sale mode -> stop");
      var gem = new RecastScreen { GemOpen = true, GemFunc = -1, GemSel = 1003 };
      var gemOther = new RecastScreen { GemOpen = true, GemFunc = -1, GemSel = 1004 };
      var gemNone = new RecastScreen { GemOpen = true, GemFunc = -1, GemSel = 0 };
      Check(new ClickGate().Check(R(ClickTarget.GemFuncRow, ok1, 7), ScreenState.Classify(new ScreenInputs { Rs = gem }, 0), 0).Allowed, "gem row: the request's item is on the gem screen -> allowed");
      r2 = new ClickGate().Check(R(ClickTarget.GemFuncRow, ok1, 7), ScreenState.Classify(new ScreenInputs { Rs = gemOther }, 0), 0);
      Check(r2.Verdict == GateVerdict.Deny && r2.Reason == "cell", "gem row: another item on the gem screen -> stop");
      r2 = new ClickGate().Check(R(ClickTarget.GemFuncRow, ok1, 7), ScreenState.Classify(new ScreenInputs { Rs = gemNone }, 0), 0);
      Check(r2.Verdict == GateVerdict.Deny && r2.Reason == "cell", "gem row: no item on the gem screen -> stop");
      foreach (int bad in new[] { 0, 5, 6, 11 }) {
        r2 = new ClickGate().Check(R(ClickTarget.GemFuncRow, ok1, bad), ScreenState.Classify(new ScreenInputs { Rs = gem }, 0), 0);
        Check(r2.Verdict == GateVerdict.Deny && r2.Reason == "target", "gem row: function " + bad + " is not a recast function -> stop");
      }
      r2 = new ClickGate().Check(R(ClickTarget.GemFuncRow, ok1, 7), ScreenState.Classify(new ScreenInputs { Inv = inv }, 0), 0);
      Check(r2.Verdict == GateVerdict.Deny && r2.Reason == "screen", "gem row: on the inventory -> stop");
      // the expected effect: the gem screen within 2 s, the wanted function selected within 2 s
      var g2 = new ClickGate(); var rq = R(ClickTarget.InvCardGem, ok1, 0); var before = ScreenState.Classify(new ScreenInputs { Inv = inv }, 0);
      g2.Arm(ClickOwner.Locate, ClickTarget.InvCardGem, ClickGate.Expect(rq, before), 0);
      g2.Observe(before, ClickGate.GemEffectMs + 10);
      Check(g2.HaltReason(ClickOwner.Locate) == "nochange", "card gem button: the gem screen does not open within 2 s -> stop");
      g2 = new ClickGate(); g2.Arm(ClickOwner.Locate, ClickTarget.InvCardGem, ClickGate.Expect(rq, before), 0);
      g2.Observe(ScreenState.Classify(new ScreenInputs { Rs = gem }, 900), 900);
      Check(!g2.Halted(ClickOwner.Locate) && !g2.Pending(ClickOwner.Locate), "card gem button: the gem screen opened -> done");
      var rr = R(ClickTarget.GemFuncRow, ok1, 7); var gemAfter = new RecastScreen { GemOpen = true, GemFunc = 8, GemSel = 1003 };
      g2 = new ClickGate(); g2.Arm(ClickOwner.Locate, ClickTarget.GemFuncRow, ClickGate.Expect(rr, ScreenState.Classify(new ScreenInputs { Rs = gem }, 0)), 0);
      g2.Observe(ScreenState.Classify(new ScreenInputs { Rs = gemAfter }, 500), 500);
      Check(g2.Pending(ClickOwner.Locate), "gem row: another function selected -> not the effect yet");
      g2.Observe(ScreenState.Classify(new ScreenInputs { Rs = new RecastScreen { GemOpen = true, GemFunc = 7, GemSel = 1003 } }, 900), 900);
      Check(!g2.Pending(ClickOwner.Locate) && !g2.Halted(ClickOwner.Locate), "gem row: the wanted function selected -> done");
    }

    Console.WriteLine("an unrecognised screen halts only the pilot's own click");
    {
      var g = new ClickGate();
      var unk = ScreenState.Classify(new ScreenInputs(), 0);
      var inv = ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true } }, 0);
      Check(g.Preflight(ClickOwner.Locate, unk, 0).Verdict == GateVerdict.Wait, "no click yet: an unknown screen is waited for");
      var rq = new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.InvEquipTab, AutoOn = true, Front = true };
      g.Arm(ClickOwner.Locate, ClickTarget.InvEquipTab, s => s.Kind == ScreenKind.Inventory, 1000);
      // 1) the click's effect is seen, then the screen goes unknown at once (the click's own doing, within the window) -> stop
      g.Observe(inv, 1200);
      Check(g.Preflight(ClickOwner.Locate, unk, 1300).Verdict == GateVerdict.Deny && g.HaltReason(ClickOwner.Locate) == "unknown", "unknown right after the pilot's own click -> stop");
      // 2) the same later (the player clicked something, the game switches forms) -> wait, no stop, and it goes on when the screen is known
      g = new ClickGate();
      g.Arm(ClickOwner.Locate, ClickTarget.InvEquipTab, s => s.Kind == ScreenKind.Inventory, 1000);
      g.Observe(inv, 1200);
      var pre = g.Preflight(ClickOwner.Locate, unk, 1000 + ClickGate.OwnClickWindowMs + 1);
      Check(pre.Verdict == GateVerdict.Wait && pre.Reason == "unknown" && !g.Halted(ClickOwner.Locate), "unknown long after the pilot's click -> only waited for");
      pre = g.Preflight(ClickOwner.Locate, unk, 60000);
      Check(pre.Verdict == GateVerdict.Wait && !g.Halted(ClickOwner.Locate), "... however long it lasts");
      Check(g.Preflight(ClickOwner.Locate, inv, 61000).Allowed, "... and the pilot goes on when the screen is known again");
      // a click is still never made on an unknown screen
      Check(g.Check(rq, unk, 61500).Verdict == GateVerdict.Wait && !g.Halted(ClickOwner.Locate), "no click while the screen is unknown (the gate waits)");
      Check(g.Check(rq, unk, 61500 + ClickGate.UnknownGraceMs + 10).Verdict == GateVerdict.Deny, "... and an unknown screen at the moment of a click for too long still stops it");
      // a mode that cannot be read behaves the same way
      var modeUnk = ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = false } }, 0);
      g = new ClickGate(); g.Arm(ClickOwner.Locate, ClickTarget.InvEquipTab, null, 0);
      Check(g.Preflight(ClickOwner.Locate, modeUnk, 20000).Verdict == GateVerdict.Wait && !g.Halted(ClickOwner.Locate), "the inventory's mode unreadable long after a click -> waited for");
      g = new ClickGate(); g.Arm(ClickOwner.Locate, ClickTarget.InvEquipTab, null, 20000);
      Check(g.Preflight(ClickOwner.Locate, modeUnk, 20500).Verdict == GateVerdict.Deny, "... right after a click -> stop");
    }

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
    Check(!sw.Allows("artAuto") && !RemoteSwitches.Parse("{}").Allows("artAuto") && !RemoteSwitches.Parse("{\"features\":{\"artAuto\":1}}").Allows("artAuto") && RemoteSwitches.Parse("{\"features\":{\"artAuto\":true}}").Allows("artAuto"), "artAuto (the artifact flows' clicks) is off by default and on only with a plain true");
    Check(!RemoteSwitches.Parse("{\"features\":{\"artAuto\":true}}").Allows("locateAuto"), "artAuto does not switch locateAuto on");

    Console.WriteLine("the artifact buttons that are never pressed");
    foreach (string key in ClickGate.NeverKeys) {
      foreach (ClickTarget tg in Enum.GetValues(typeof(ClickTarget))) Check(ClickGate.KeyOf(tg) != key, "no target is placed by the never-pressed button " + key + " (" + tg + ")");
      foreach (ClickOwner o in Enum.GetValues(typeof(ClickOwner))) Check(!ClickGate.WhitelistText(o).Contains(key) && !ClickGate.WhitelistText(o).Contains("PromoteBtn"), o + " whitelist: " + key + " is in no whitelist");
    }
    Check(!ClickGate.WhitelistText(ClickOwner.Locate).Contains("ArtBulk") && !ClickGate.WhitelistText(ClickOwner.Sell).Contains("ArtCell") && !ClickGate.WhitelistText(ClickOwner.Sell).Contains("ArtMat") && !ClickGate.WhitelistText(ClickOwner.Sell).Contains("ArtWorkshop") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtTab") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtBulk") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtCell") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtMat") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtWorkshop") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtPromote"),
      "each owner has only its own artifact targets (the promotion no bulk sale, the cleanup no normal-list cell / workshop, the equip helper only the hero screen's)");
    Check(ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtTakeOverConfirm") && !ClickGate.WhitelistText(ClickOwner.Locate).Contains("ArtTakeOverConfirm") && !ClickGate.WhitelistText(ClickOwner.Sell).Contains("ArtReplace"), "the pop-up's «Заменить» of an artifact is the equip helper's alone");
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
