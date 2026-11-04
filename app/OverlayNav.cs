// Wardsage.exe - the navigator core's host side (src/Nav*.cs, docs/specs/navigator.md): OverlayNavIo gives the navigator the reads and the sends of the
// overlay (every click still goes through EquipSend / SendInside / EscapeSend / TipEscapeSend and so through the click gate), TickAutoNav is the auto-equip
// gear phase with the navigator in place of the old preamble. Behind the navCore switch (off by default).
using System;
using System.Collections.Generic;

namespace RealmForge {
  sealed partial class OverlayController {
    /// <summary>The navigator core is on (the site's "navCore" switch, or the test build's config); set by the host bridge.</summary>
    public bool NavCoreEnabled;
    bool NavCoreOn { get { return NavCoreEnabled; } }

    readonly ScreenReader navReader = new ScreenReader();
    readonly Navigator navEquip = new Navigator(ClickOwner.Equip), navLab = new Navigator(ClickOwner.Equip);
    OverlayNavIo navIo;
    List<IAtTargetAction> navActions;
    GateResult navLastGate;
    FilterView navFview; bool navFviewMade;

    // ---- TEST build: the navigator's probe (src/NavProbe.cs, docs/specs/game-nav-research.md): the game's own UI state read-only, logged as «navprobe» once a second
    // and at once when a line changes, with our own clicks / keys and the player's Esc / C / B presses, so the owner's hand-run can be compared with what the game said
    readonly System.Windows.Forms.Timer npTimer = new System.Windows.Forms.Timer(); long npFullAt, npHotAt; readonly Dictionary<string, string> npLast = new Dictionary<string, string>(); bool npEsc, npC, npB, npLeft;
    void NavProbeStart() {
      npTimer.Interval = 250;
      npTimer.Tick += (s, e) => { try { NavProbeTick(); } catch (Exception ex) { Log.Write("navprobe: " + ex.Message); } };
      npTimer.Start();
      Log.Write("navprobe: on (test build; read-only; one line per state a second and at every change)");
    }
    internal void NavProbeOwn(string what) { Log.Write("navprobe own " + what); }
    void NavProbeTick() {
      long now = NowMs();
      // the player's keys (edges only)
      bool esc = (W32.GetAsyncKeyState(0x1B) & 0x8000) != 0, c = (W32.GetAsyncKeyState(0x43) & 0x8000) != 0, b = (W32.GetAsyncKeyState(0x42) & 0x8000) != 0, lb = (W32.GetAsyncKeyState(0x01) & 0x8000) != 0;
      if (esc && !npEsc) Log.Write("navprobe player key Escape" + (sending ? " (while our own input is on its way)" : ""));
      if (c && !npC) Log.Write("navprobe player key C"); if (b && !npB) Log.Write("navprobe player key B"); if (lb && !npLeft) Log.Write("navprobe player mouse left");
      npEsc = esc; npC = c; npB = b; npLeft = lb;
      { IntPtr gw = GameWindow(); W32.RECT gcr; if (gw != IntPtr.Zero && W32.GetClientRect(gw, out gcr)) { RFX.NavProbeClientW = gcr.R; RFX.NavProbeClientH = gcr.B; W32.POINT cur; var org = new W32.POINT(0, 0); W32.ClientToScreen(gw, ref org); if (W32.GetCursorPos(out cur)) { RFX.NavProbeCursorX = cur.X - org.X; RFX.NavProbeCursorY = cur.Y - org.Y; } } }
      var lines = RFX.NavProbeLines();
      if (lines == null) return;
      bool full = now - npFullAt >= 1000; if (full) npFullAt = now;
      if (now - npHotAt >= 30000) { npHotAt = now; var hk = RFX.NavProbeHotkeys(); if (hk != null) foreach (var h in hk) Log.Write("navprobe " + h); }
      foreach (var ln in lines) {
        string key = ln.Length > 14 ? ln.Substring(0, 14) : ln; string old;
        bool same = npLast.TryGetValue(key, out old) && old == ln;
        if (full || !same) Log.Write("navprobe " + (full && same ? "" : same ? "" : "CHANGE ") + ln);
        npLast[key] = ln;
      }
    }

    /// <summary>The navigator asks for our own windows to stop covering the looks (INavIo.HideOwn): the hint windows are hidden at the end of every tick for 20 s (the looks of the next tick see the bare game).</summary>
    long navOwnHideUntil;
    internal void NavHideOwn(long now) { navOwnHideUntil = now + 20000; NavOwnHideTick(); }
    void NavOwnHideTick() {
      bool away = NowMs() < navOwnHideUntil;
      bool was = GlowWindow.OwnAway; GlowWindow.OwnAway = away;
      if (!away || was) return;   // (hidden once; the hint windows draw nothing while away, so they do not blink)
      foreach (var w in GlowWindow.Live()) if (w.IsHandleCreated && !w.IsDisposed && w.Visible && !w.KeepCaptureState) w.Hide();
    }

    void NavRestart() { navEquip.Restart(); navLab.Restart(); navReader.Reset(); }

    sealed class OverlayNavIo : INavIo {
      readonly OverlayController c;
      public W32.POINT O; public W32.RECT Cr; public bool FrontNow, Busy; public long NowMs;
      public OverlayNavIo(OverlayController c) { this.c = c; }
      public long Now { get { return NowMs; } }
      public bool Front { get { return FrontNow; } }
      public bool UserBusy { get { return Busy; } }
      public double W { get { return Cr.R; } }
      public double H { get { return Cr.B; } }
      public double U { get { return Ui.Unit(Cr.R, Cr.B); } }
      public bool Slow { get { return false; } }
      public bool InputOpen { get { return RFX.NavInputOpen() ?? true; } }
      public GameGeometry Geo { get { return RealmForge.Geo.Current; } }
      public ClickGate Gate { get { return c.gate; } }
      public ScreenState Fresh(bool wantCity, bool wantKnown) { return c.FreshScreen(wantCity, wantKnown); }
      public double[] LiveRect(string form, string field) { return RFX.UiFieldReadNow(form, field, W, H, U); }
      public double[] LiveFor(ClickTarget t, string form) {
        RFX.UiButtonWant(t, form, W, H, U);
        var r = RFX.UiButtonFresh(t, form, W, H, Environment.TickCount, RFX.UiFreshMs);
        return r != null ? new[] { r.X, r.Y, r.W, r.H } : null;
      }
      public bool Picture(string what) {
        if (!FrontNow) return false;
        switch (what) {
          case "heroesButton": return c.HeroesButtonSeen(O, W, H);
          case "heroFilter": return c.HeroFilterSeen(O, W, H);
          case "gearFilterMain": return c.Mark(O, c.fg.X(c.fg.MainCloseX, W, H), c.fg.Y(c.fg.CloseY, W, H), U, ButtonCheck.IsCloseRed);
          case "gearFilterSide": return c.Mark(O, c.fg.X(c.fg.SideCloseX, W, H), c.fg.Y(c.fg.CloseY, W, H), U, ButtonCheck.IsCloseRed);
          case "tipShown": return c.EquipTipShown();
        }
        return false;
      }
      public void HideOwn() { c.NavHideOwn(NowMs); }
      public string EscapeKind { get { return RFX.NavEscapeKind(); } }
      public bool Hotkey(string name) { return false; }   // (not wired: a key press needs a gate target and the live check, docs/specs/2026-11-04-core-any-scenario.md 5.4)
      public void Log(string line) { RealmForge.Log.Write(line); }
      public void Problem(string kind, string message) { c.RaiseProblem(kind, message, new Dictionary<string, string>()); }
      public GateResult Send(Act a, ScreenState fresh) {
        c.navLastGate = null; bool ok;
        if (a.Kind == ActKind.Escape) {
          ok = a.Target == ClickTarget.EquipTipEscape ? c.TipEscapeSend(Cr, FrontNow) : c.EscapeSend(a.Owner, Cr, FrontNow);
        } else {
          int sx = O.X + (int)Math.Round(a.X), sy = O.Y + (int)Math.Round(a.Y);
          if (sx < O.X || sy < O.Y || sx >= O.X + Cr.R || sy >= O.Y + Cr.B) return GateResult.Wait("outside", "the point lies outside the game window");
          var aa = new AutoAction { Kind = a.Kind == ActKind.Drag ? AutoKind.Drag : AutoKind.Click, X = a.X, Y = a.Y, DY = a.DY, Cell = a.Cell };
          bool equipT = a.Target == ClickTarget.EquipNav || a.Target == ClickTarget.EquipFilter || a.Target == ClickTarget.EquipClosePopup || a.Target == ClickTarget.EquipConfirm;
          if (a.Owner == ClickOwner.Equip && equipT) ok = c.EquipSend(a.Target, aa, sx, sy, O, FrontNow);
          else ok = c.SendInside(aa, O, Cr, a.Owner, a.Target, FrontNow, a.Fill == null ? (Action<ClickRequest, ScreenState>)null : (rq, fr) => a.Fill(rq));
        }
        c.AnnounceHalts();
        if (ok) return GateResult.Allow("sent");
        var g = c.navLastGate;
        if (g != null && g.Verdict != GateVerdict.Allow) return g;
        // (the click waited for a look our own hint covers: said so, so that the navigator can hide our windows - Navigator.GateWait)
        string cov = unknownLooks.Blocking(OverlayController.NowMs());
        return cov != null ? GateResult.Wait("own", "a look is covered by our own hint (" + cov + ")") : GateResult.Wait("unsent", "not sent");
      }
    }

    OverlayNavIo NavIo(W32.POINT o, W32.RECT cr, bool front, bool busy, long now) {
      if (navIo == null) navIo = new OverlayNavIo(this);
      navIo.O = o; navIo.Cr = cr; navIo.FrontNow = front; navIo.Busy = busy; navIo.NowMs = now;
      return navIo;
    }

    HeroView NavHeroView(AutoView v, W32.POINT o, W32.RECT cr, long now) {
      if (screens.Hero != null) heroScreen = screens.Hero;
      var hv = new HeroView {
        NowMs = now, Foreground = v.Foreground, UserBusy = v.UserBusy, UserClicked = v.UserClicked, W = cr.R, H = Ui.Unit(cr.R, cr.B), ClientH = cr.B,
        Plan = runHero, Hero = heroScreen.FormOk ? heroScreen.Hero : 0, Tab = heroScreen.Tab, Part = heroScreen.Part, SmallCards = heroScreen.SmallCards,
        Heroes = heroScreen.Heroes, HeroesButton = false,
        FilterOpen = v.Foreground && HeroFilterSeen(o, cr.R, cr.B), ClassChosen = heroScreen.ClassChosen, FactionList = heroScreen.FactionList,
        ChosenCamps = heroScreen.ChosenCamps, ClassCount = heroScreen.ClassCount, GridPtr = heroScreen.GridPtr, InputOpen = RFX.NavInputOpen() ?? true
      };
      long[] tags = HeroTags(runHero);
      if (tags != null) { hv.HeroClass = tags[0]; hv.HeroCamps = new long[tags.Length - 1]; Array.Copy(tags, 1, hv.HeroCamps, 0, tags.Length - 1); }
      return hv;
    }

    FilterView NavFilterView(W32.POINT o, W32.RECT cr, AutoView v, long now) {
      if (!navFviewMade) {
        navFviewMade = true; navFview = FilterViewNow(o, cr, now, v.Foreground, v.UserBusy);
        if (navFview.PanelOpen && navFview.SidePanelOpen) filterTouchAt = now;
        LogFilter(navFview); lastFilterView = navFview;
      }
      return navFview;
    }

    void NavBuildActions(AutoView v, W32.POINT o, W32.RECT cr, bool confirm) {
      // (the views are made from the tick's own v / o / cr, kept in fields the closures read)
      navTickV = v; navTickO = o; navTickCr = cr; navTickConfirm = confirm;
      if (navActions != null) return;
      var hs = new HeroSelectAction(hpilot, (s, t, n) => NavHeroView(navTickV, navTickO, navTickCr, n));
      hs.OnStep = (hv, ha) => {
        if (recB != null) { try { recB.Hp = RecView.Line(hv, ha, hpilot.State); } catch (Exception) { } }
        lastHeroView = hv; LogHero(hv);
      };
      var gf = new GearFilterAction(fpilot, (s, t, n) => NavFilterView(navTickO, navTickCr, navTickV, n), () => fpilot.State == "work" || (filterItem != 0 && FilterPilot.WantedFor(filterKind, state == "on")));   // (the guide's verdict asks for the filter: far down, hiddenEq, hiddenFilter, notIn - not only the "filter" hint; «Скрыть надетое» with a foreign item has no hint)
      gf.OnStep = (fv, fa) => {
        if (fpilot.State == "failed") {
          if (!filterFailSaid) {
            filterFailSaid = true; AutoDetailFor("filter_failed");
            var fc = new Dictionary<string, string>(AutoDetail); fc["why"] = fpilot.Note;
            RaiseProblem("pilot.filter_failed", "filter failed: the game's gear filter could not be set for item " + fv.Item + " (kind " + filterKind + ")" + (fpilot.Note != "" ? " - " + fpilot.Note : ""), fc);
          }
        } else filterFailSaid = false;
      };
      var ip = new ItemPickAction(pilot, (s, t, n) => {
        var vv = navTickV; var fview = NavFilterView(navTickO, navTickCr, vv, n);
        if (fview.PanelOpen && fview.SidePanelOpen) vv.SideClose = new[] { fg.X(fg.SideCloseX, navTickCr.R, navTickCr.B), fg.Y(fg.CloseY, navTickCr.R, navTickCr.B) };
        vv.FilterAt = filterTouchAt; vv.ListSig = typesPtr != 0 ? (long)typesPtr * 31 + (types != null ? types.Length : 0) : 0;
        LiveGearView(vv, navTickCr);
        vv.Slot = null; vv.InputOpen = RFX.NavInputOpen() ?? true;
        return vv;
      }, (vv, a) => {
        bool onConfirm = confirmBtn != null && a.X >= confirmBtn[0] && a.X <= confirmBtn[0] + confirmBtn[2] && a.Y >= confirmBtn[1] && a.Y <= confirmBtn[1] + confirmBtn[3];
        return navTickConfirm && onConfirm ? ClickTarget.EquipConfirm : ClickTarget.EquipNav;
      }, gf);
      ip.OnStep = (vv, a) => {
        if (recB != null) { try { recB.Ap = RecView.Line(vv, a, pilot.State); } catch (Exception) { } }
        if (pilot.Trace != "") { Log.Write("auto: " + pilot.Trace); lastPilotTrace = pilot.Trace; pilot.Trace = ""; }
      };
      ip.OnSent = a => { if (cellLivePt != null) pilot.LivePoint(cellLivePt[0], cellLivePt[1]); equipClickAt = NowMs(); };
      navActions = new List<IAtTargetAction> { hs, gf, ip };
    }
    AutoView navTickV; W32.POINT navTickO; W32.RECT navTickCr; bool navTickConfirm;

    /// <summary>The gear phase of a started plan with the navigator: the target is the plan's hero (and the slot the guide asks for), the navigator brings the game
    /// there from any screen, closes what is in the way, and runs the pilots as actions at the target.</summary>
    void TickAutoNav(W32.POINT o, W32.RECT cr, AutoView v, long now, bool confirm) {
      var io = NavIo(o, cr, v.Foreground, v.UserBusy, now);
      navFviewMade = false; navFview = null;
      NavBuildActions(v, o, cr, confirm);
      var scr = screens.Current;
      var snap = navReader.Update(scr, now, io.Picture);
      int slot = hintKind == "slot" ? hintSlot
               : scr != null && scr.Kind == ScreenKind.HeroScreen && scr.Hero == runHero && scr.HeroTab == HeroGeometry.GearTab && scr.HeroSlot >= 0 ? scr.HeroSlot : -1;
      navEquip.SetRecipe(NavRecipes.Get("equip"), new Dictionary<string, long> { { "hero", runHero }, { "slot", slot } }, navActions);
      navEquip.Tick(io, snap);
      string say;
      if (navEquip.State == "halted") say = navEquip.HaltWhy.StartsWith("gate_") ? navEquip.HaltWhy : "nav_" + navEquip.HaltWhy;
      else if (navEquip.Say == "paused" || navEquip.Say == "background") say = navEquip.Say;
      else if (navEquip.State == "at_target") say = pilot.State;
      else say = "work";
      AutoSay(say);
    }

    void TickLabPrepNav(LabPrep p, W32.POINT o, W32.RECT cr, bool front, long now) {
      var io = NavIo(o, cr, front, false, now);
      var snap = navReader.Update(screens.Current, now, io.Picture);
      navLab.SetTarget(new NavTarget { Goal = NavGoal.City, Owner = ClickOwner.Equip }, new List<IAtTargetAction>());
      navLab.Tick(io, snap);
      if (navLab.State == "done") { navLab.Clear(); LabPrepEnd(true, "the city (navigator)"); }
      else if (navLab.State == "halted") { string w = navLab.HaltWhy; navLab.Clear(); LabPrepEnd(false, "the navigator stopped: " + w); }
    }
  }
}
