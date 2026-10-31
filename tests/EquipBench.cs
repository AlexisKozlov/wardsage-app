// The equip helper's speed, phase by phase (CoreTests --sim N --equip-bench; tests/run-sim.sh --equip-bench [N]): a 6-slot plan from the city,
// with and without the game's login pop-up chain (7 pop-ups, each opening after the last one closed), on the fast PC and the slow laptop.
//   chain: the start to the pilot's first press past the pop-ups (the real SimHost: OfferStep, the click gate's chain wait);
//   hero:  from there to the plan's hero on its gear tab (the real HeroPilot on the sim's grid);
//   slots: the 6 slots, each: the slot opened, the gear filter set (its set, main stat and 3 sub stats: the real FilterPilot), the item picked and
//          «Заменить» pressed (the real AutoPilot), the guide's step moved on at the live poll (app/HostBridge.cs PollLive), the filter pop-up
//          closed before the next slot (app/Overlay.cs EquipClosePopup). The game answers each click after its latency; a press on the item's
//          card before it has opened, or on a pop-up still scaling in, is lost (counted).
// The same file builds against an older core (the pilots' Polling, AutoView.ButtonStill and the hot live poll are looked up by reflection), so
// one bench measures a build before and after.
using System;
using System.Collections.Generic;
using System.Reflection;
using RealmForge;

static partial class CoreTests {
  static readonly string[] BenchChain = { "Form_DailyCheckIn", "Form_ItemReward", "Form_SevenDaySign", "Form_ItemReward", "Form_SevenDaySign", "Form_AnnouncementHall", "Form_ActPopupNewHero" };

  static bool BenchPolling(object pilot, long now) {
    var m = pilot.GetType().GetMethod("Polling", new[] { typeof(long) });
    return m != null && (bool)m.Invoke(pilot, new object[] { now });
  }
  static int BenchLivePoll(bool hot) {
    var m = typeof(LoadPlan).GetMethod("LivePollMs", new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool) });
    return m != null ? (int)m.Invoke(null, new object[] { true, true, true, hot }) : LoadPlan.LivePollMs(true, true, true);
  }
  static bool BenchNew { get { return typeof(AutoView).GetField("ButtonStill") != null; } }   // (this build has the event-confirmed waits)
  static int BenchHotMs() { var f = typeof(LoadPlan).GetField("EquipHotMs"); return f != null ? (int)f.GetValue(null) : 0; }
  static bool BenchHostHot(SimHost h) { var p = typeof(SimHost).GetProperty("EquipHot"); return p != null && (bool)p.GetValue(h, null); }

  static int BenchSlotCount = 6;
  sealed class BenchRun { public long Chain, Hero, Slots, MaxStep; public string WorstStep = ""; public bool Done; public int Lost, Clicks; public string Why = ""; }

  static int EquipBench(GameGeometry geo, int seeds) {
    Console.WriteLine("equip bench: a 6-slot plan from the city, " + seeds + " seeds a cell; mean seconds of the runs at their goal (chain / hero / slots / all)");
    int bad = 0;
    foreach (bool slow in new[] { false, true })
      foreach (bool chain in new[] { false, true }) {
        long c = 0, he = 0, sl = 0; int done = 0, lost = 0, clicks = 0; string why = "";
        for (int i = 0; i < seeds; i++) {
          var r = BenchOne(geo, 880000 + i * 7 + (slow ? 1 : 0), slow, chain);
          if (!r.Done) { if (why == "") why = r.Why; continue; }
          done++; c += r.Chain; he += r.Hero; sl += r.Slots; lost += r.Lost; clicks += r.Clicks;
        }
        if (done < seeds) bad++;
        double n = Math.Max(1, done);
        Console.WriteLine("  " + (slow ? "slow" : "fast") + (chain ? " chain   " : " no chain") + "  done " + done + "/" + seeds
          + "  chain " + (c / n / 1000).ToString("0.0") + " s  hero " + (he / n / 1000).ToString("0.0") + " s  slots " + (sl / n / 1000).ToString("0.0")
          + " s  all " + ((c + he + sl) / n / 1000).ToString("0.0") + " s  clicks " + (clicks / n).ToString("0") + "  lost presses " + lost + (why != "" ? "  (" + why + ")" : ""));
      }
    return bad == 0 ? 0 : 1;
  }

  static BenchRun BenchOne(GameGeometry geo, int seed, bool slow, bool chain) {
    var res = new BenchRun();
    var g = new SimGame(seed, slow, 0); g.Geo = geo; g.SpawnP = 0; g.LiveButtons = true;
    var rr = new Random(seed ^ 0x2B);
    int next = 0;
    if (chain) {
      g.Push(BenchChain[next++]);
      g.Popped = f => { if (next < BenchChain.Length && OfferForms.IsOffer(f)) g.PushOnCity((slow ? 500 : 300) + rr.Next(slow ? 900 : 500), BenchChain[next++]); };
    }
    var r = new Random(seed);
    long plan = 100 + r.Next(30);
    var h = new SimHost(g, PilotFlow.Equip, plan, geo);
    long t = 0, cap = 120000, chainEnd = -1;
    while (t < cap) {
      g.Advance(t);
      if (t >= h.SendingUntil) { h.ReadScreens(t); h.Tick(); }
      if (chainEnd < 0 && h.SentCount(ClickTarget.EquipNav) > 0) chainEnd = t;
      if (g.Bad.Count > 0 || h.Gate.Halted(ClickOwner.Equip)) { res.Why = g.Bad.Count > 0 ? g.Bad[0] : "halt " + h.Gate.HaltReason(ClickOwner.Equip); return res; }
      if (h.PilotState.StartsWith("item_")) break;
      t += h.Gate.AnyPending || BenchHostHot(h) ? 50 : slow ? 150 + r.Next(250) : 100;
    }
    if (t >= cap || chainEnd < 0) { res.Why = "seed " + seed + " hero phase timeout (" + h.PilotState + " " + h.HeroWhy + ")"; return res; }
    res.Chain = chainEnd; res.Hero = t - chainEnd;
    long t0 = t;
    long end = BenchSlots(g, slow, r, ref t, res);
    if (end < 0) return res;
    res.Slots = end - t0; res.Done = true;
    return res;
  }

  // the slots after the hero: a model of the gear list, the filter pop-up and the item's card around the real pilots
  sealed class BenchSlot {
    public bool Open, Panel; public string Side = ""; public long[] Suits = new long[0], Mains = new long[0], Subs = new long[0];
    public long Sel, Owner; public bool Tip; public long TipAt = -1;
  }

  static long BenchSlots(SimGame g, bool slow, Random r, ref long t, BenchRun res) {
    double W = g.W, H = g.H, U = Ui.Unit(W, H);
    var fg = new FilterGeometry(); var hint = SimGame.Hint; var hg = new HeroGeometry();
    var ip = new AutoPilot(SimGame.ItemGeo); var fp = new FilterPilot(fg);
    var due = new List<KeyValuePair<long, Action>>();
    Func<long> lat = () => slow ? 600 + r.Next(900) : 80 + r.Next(170);
    // (the filter's choice and panels show in memory sooner than a screen answers: live 1.6.66.417 on the laptop, sub stat clicks 0.5 s apart took)
    Func<long> memLat = () => slow ? 200 + r.Next(300) : 50 + r.Next(100);
    long[] setOrder = new long[20]; for (int i = 0; i < 20; i++) setOrder[i] = 7000 + i;
    long[] subOrder = new long[18]; for (int i = 0; i < 18; i++) subOrder[i] = 1 + i;
    const long hero = 5;
    long sendingUntil = 0, lastClick = -100000, slotCloseAfter = 0, pollAt = 0;
    string guide = "slot";   // the guide's step as the page has it (moved on at the live poll)
    int slot = 0;
    var s = new BenchSlot();
    int[] slotRect = hint.Slot(0, W, H);
    long tickAt = t;
    // the steps' durations (the pilot's "step ms: slot / pick / selected / confirm"): the slot opened, the item selected, «Заменить» pressed, the item on the hero
    long tSlot0 = t, tPick0 = -1, tSel = -1, tConf = -1; bool slotNoted = false;
    Action<string, long> note = (nm, d) => { if (d > res.MaxStep) { res.MaxStep = d; res.WorstStep = nm + " " + d + " ms (slot " + slot + ")"; } };
    for (long cap = t + 180000; t < cap; ) {
      for (bool any = true; any; ) { any = false; for (int i = 0; i < due.Count; i++) if (due[i].Key <= t) { var a = due[i].Value; due.RemoveAt(i); a(); any = true; break; } }
      long item = 1000 + slot;
      long setId = setOrder[3 + slot % 4], statId = 20 + slot % 3; long[] subIds = { 3, 5 + slot % 4, 11 };
      // (every other slot's item comes into the list's view with its set alone: the guide's verdict is «pick» then)
      bool setOn = s.Panel && s.Suits.Length == 1 && s.Suits[0] == setId;
      bool filterOk = setOn && (slot % 2 == 0 || (s.Mains.Length == 1 && s.Mains[0] == statId && s.Subs.Length == 3));
      // the live poll: the page's guide takes the game's state
      if (t >= pollAt) {
        bool hot = t - lastClick < BenchHotMs() || BenchPolling(ip, t) || BenchPolling(fp, t);
        pollAt = t + BenchLivePoll(hot);
        if (s.Owner == hero) { if (tConf >= 0) note("confirm", t - tConf); tSlot0 = t; tPick0 = tSel = tConf = -1; slotNoted = false; slot++; if (slot >= BenchSlotCount) return t; s = new BenchSlot { Panel = s.Panel, Side = s.Side, Suits = s.Suits, Mains = s.Mains, Subs = s.Subs }; guide = "slot"; continue; }
        guide = !s.Open ? "slot" : !filterOk ? "filter" : s.Sel != item ? "pick" : "replace";
      }
      if (s.Open && !slotNoted) { slotNoted = true; note("slot", t - tSlot0); }
      if (guide == "pick" && tPick0 < 0) tPick0 = t;
      if (s.Sel == item && tSel < 0) { tSel = t; if (tPick0 >= 0) note("pick", t - tPick0); }
      if (t < sendingUntil || t < tickAt) { t += 10; continue; }
      // the overlay's tick: the hot fields at the effect rate while a pilot awaits its click's effect, else the calm read
      bool effect = BenchPolling(ip, t) || BenchPolling(fp, t);
      tickAt = t + LoadPlan.ReadGapMs(false, effect, false, 100000) + (slow ? r.Next(150) : 0);
      AutoAction act = null; string what = "";
      // the filter pop-up left over the slots (the last item's): its × first (app/Overlay.cs: slotCloseAfter 900 ms)
      if (guide == "slot" && s.Panel && t >= slotCloseAfter) {
        slotCloseAfter = t + (BenchNew ? 2000 : 900); ip.Reset();
        act = new AutoAction { Kind = AutoKind.Click, X = fg.X(fg.MainCloseX, W, H), Y = fg.Y(fg.CloseY, W, H) }; what = "close";
      }
      if (act == null && guide != "slot") {
        var fv = new FilterView { NowMs = t, Foreground = true, W = W, H = H, Item = item, Wanted = guide == "filter", SetId = setId, StatId = statId,
          SetIndex = Array.IndexOf(setOrder, setId), StatIndex = (int)statId - 20, SetOrder = setOrder, SubIds = subIds, SubOrder = subOrder, SubAttrs = s.Subs,
          Suits = s.Suits, MainAttrs = s.Mains, PanelOpen = s.Panel, SidePanelOpen = s.Side != "", SetPanelOpen = s.Side == "set" };
        var fa = fp.Step(fv);
        if (fp.State == "failed") { res.Why = "filter failed: " + fp.Note; return -1; }
        if (fa != null) { if (fa.Kind == AutoKind.None) continue; act = fa; what = "filter"; ip.Reset(); }
      }
      if (act == null && !(guide == "slot" && s.Panel)) {
        bool tip = s.Tip;
        bool confirm = guide == "replace";
        var v = new AutoView { NowMs = t, Foreground = true, H = U, Slot = guide == "slot" ? slotRect : null, SlotOpen = guide == "slot" && s.Open,
          Target = guide == "pick" || guide == "replace" ? item : 0, Row = 2, Col = 1, Sel = s.Sel, SelRow = s.Sel == item ? 2 : 0, SelCol = s.Sel == item ? 1 : 0,
          AnchorHas = true, Row1Top = g.Row1Top, Types = SimGame.ItemTypes, AutoConfirm = confirm, Button = confirm && tip ? hint.Replace(W, H) : null,
          Hero = hero, ScreenHero = hero, Owner = s.Owner, TipKnown = true, TipShown = tip, BlueNone = confirm && !tip,
          SideClose = s.Panel && s.Side != "" ? new[] { fg.X(fg.SideCloseX, W, H), fg.Y(fg.CloseY, W, H) } : null };
        var bs = typeof(AutoView).GetField("ButtonStill");   // the card's «Заменить» live and held still: it has stopped opening (300 ms)
        if (bs != null) bs.SetValue(v, tip && t - s.TipAt >= 300 + LiveStill.GapMs);
        var a = ip.Step(v);
        if (ip.State == "failed" || ip.State == "unconfirmed" || ip.State == "slot_failed") { res.Why = "item " + ip.State + ": " + ip.Why; return -1; }
        if (a.Kind == AutoKind.None) { t += 10; continue; }
        act = a; what = confirm && v.Button != null && Math.Abs(a.X - (v.Button[0] + v.Button[2] / 2.0)) < 2 ? "confirm" : guide == "slot" ? "slot" : "item";
      }
      if (act == null) { t += 10; continue; }
      res.Clicks++; lastClick = t; sendingUntil = t + (act.Kind == AutoKind.Drag ? 850 : 300);
      long l = lat(), tc = t; var st = s; double x = act.X, y = act.Y; int sl0 = slot;
      Func<double, double, bool> near = (px, py) => Math.Abs(x - px) < 3 && Math.Abs(y - py) < 3;
      if (what == "close") due.Add(new KeyValuePair<long, Action>(tc + l, () => { st.Panel = false; st.Side = ""; st.Suits = new long[0]; st.Mains = new long[0]; st.Subs = new long[0]; }));
      else if (what == "slot") due.Add(new KeyValuePair<long, Action>(tc + l, () => { st.Open = true; }));
      else if (what == "item") due.Add(new KeyValuePair<long, Action>(tc + l, () => { st.Sel = 1000 + sl0; st.Tip = true; st.TipAt = tc + l; }));
      else if (what == "confirm") {
        if (t - s.TipAt < 300) { res.Lost++; continue; }   // the card still opening: the press is lost
        if (tSel >= 0 && tConf < 0) { tConf = t; note("selected", t - tSel); }
        due.Add(new KeyValuePair<long, Action>(tc + l, () => { st.Owner = hero; st.Sel = 0; st.Tip = false; }));
      } else {   // the filter pop-up's controls
        l = memLat();
        Action eff = null;
        if (near((0.029 + 0.379 / 2) * U, Ui.FromBottom(0.9275, W, H))) eff = () => st.Panel = true;
        else if (near(fg.X(fg.SetArrowX, W, H), fg.Y(fg.SetArrowY, W, H))) eff = () => st.Side = "set";
        else if (near(fg.X(fg.StatArrowX, W, H), fg.Y(fg.StatArrowY, W, H))) eff = () => st.Side = "stat";
        else if (near(fg.X(fg.SubArrowX, W, H), fg.Y(fg.SubArrowY, W, H))) eff = () => st.Side = "sub";
        else if (near(fg.X(fg.SideCloseX, W, H), fg.Y(fg.CloseY, W, H))) eff = () => st.Side = "";
        else if (near(fg.X(fg.ResetX, W, H), fg.Y(fg.ResetY, W, H))) eff = () => { st.Suits = new long[0]; st.Mains = new long[0]; st.Subs = new long[0]; };
        else if (st.Side == "set") { for (int i = 0; i < 20; i++) { var c = fg.SetCell(i, W, H); if (near(c[0], c[1])) { long id = setOrder[i]; eff = () => st.Suits = new[] { id }; } } }
        else if (st.Side == "stat") { for (int i = 0; i < 18; i++) { var c = fg.StatCell(i, W, H); if (near(c[0], c[1])) { long id = 20 + i; eff = () => st.Mains = new[] { id }; } } }
        else if (st.Side == "sub") { for (int i = 0; i < 18; i++) { var c = fg.StatCell(i, W, H); if (near(c[0], c[1])) { long id = subOrder[i]; eff = () => { var l2 = new List<long>(st.Subs); if (!l2.Remove(id)) l2.Add(id); st.Subs = l2.ToArray(); }; } } }
        if (eff == null) { res.Why = "a filter click on nothing @" + (int)x + "," + (int)y; return -1; }
        due.Add(new KeyValuePair<long, Action>(tc + l, eff));
      }
    }
    res.Why = "slots timeout (slot " + slot + ", guide " + guide + ", item " + ip.State + ", filter " + fp.State + ")";
    return -1;
  }
}
