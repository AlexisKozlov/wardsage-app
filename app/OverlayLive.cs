// Wardsage.exe - the overlay's live-rect clicks (package P2) (docs/specs/2026-11-05-work-packages.md): a pilot's click by the widget it presses (ClickRequest.WidgetKey, src/FrameTargets.cs) or by the
// cell of a list (UiCellKind) lands on the centre of the widget's live rect - never on a fraction of the window. The point is the centre of the rect shrunk by 20 % (the gate holds it inside:
// src/ClickGate.cs CheckWidgetKey) and only a settled rect counts (scale 1 on its chain, not while a pop-up or a panel tweens in); otherwise the pilot's own point stays and the log says
// "src=geo why=...". The decisions are RFX.UiKeyPoint / RFX.UiCellKeyPoint (src/UiButtons.cs, unit-tested in tests/LiveClickTests.cs); this part only reads, sets the request and logs.
using System;

namespace RealmForge {
  sealed partial class OverlayController {
    /// <summary>The click of <paramref name="req"/> by its widget key: the live rect read fresh (at most UiFreshMs old, else one read now), the form on top of the fresh screen. A live point is set
    /// into the request (X, Y, Live, LiveRect) and the screen point (<paramref name="sx"/>, <paramref name="sy"/>); else nothing changes (the pilot's point). One log line per click.</summary>
    // the same "uibtn key" line is a lookup the pilot repeats every tick while the navigator holds its click back (gate / stable screen): one line per second, not nine (applog_008 10:29:02: 56 lines, 40 real clicks)
    string lastKeyLine = ""; int lastKeyLineAt; int keyLineSkipped;
    void LogKeyLine(string line) {
      int now = Environment.TickCount;
      if (line == lastKeyLine && unchecked(now - lastKeyLineAt) < 1000) { keyLineSkipped++; return; }
      if (keyLineSkipped > 0) { Log.Write("uibtn key: " + keyLineSkipped + " same lookups left out"); keyLineSkipped = 0; }
      lastKeyLine = line; lastKeyLineAt = now; Log.Write(line);
    }

    bool LiveClickKey(ClickRequest req, ScreenState fresh, W32.POINT o, ref int sx, ref int sy) {
      string key = req.WidgetKey;
      if (string.IsNullOrEmpty(key) || req.W <= 0 || req.H <= 0) return false;
      var ft = FrameTargets.Of(key); var w = FrameTargets.Widget(ft);
      if (w == null) { LogKeyLine(RFX.KeyClickLine(key, null, false, 0, 0, "no widget for the key")); return false; }
      int now = Environment.TickCount;
      var r = RFX.UiTargetFresh(key, req.W, req.H, now, RFX.UiFreshMs) ?? RFX.UiTargetReadNow(key, w, req.W, req.H, req.U);
      double x, y; double[] box; string why;
      bool live = RFX.UiKeyPoint(r, req.W, req.H, now, out x, out y, out box, out why);
      if (live && !RFX.UiFormTopIs(ft.Form, fresh)) { live = false; why = "the form on top is " + (fresh != null ? fresh.Top + " (" + fresh.Brief + ")" : "not read"); }
      double[] rect = r != null && r.Ok ? new[] { r.X, r.Y, r.W, r.H } : null;
      if (live) {
        req.X = x; req.Y = y; req.Live = true; req.LiveRect = rect;
        sx = o.X + (int)Math.Round(x); sy = o.Y + (int)Math.Round(y);
      } else if (why == null) why = r != null && !r.Ok ? "failed at " + r.FailStep : "no read";
      LogKeyLine(RFX.KeyClickLine(key, rect, live, x, y, why));
      return live;
    }

    /// <summary>The click on cell <paramref name="id"/> of list <paramref name="kind"/> (a set / stat / class / faction id, 0 and negative ones too, or <see cref="RFX.IndexUid"/> of a row without a
    /// readable id): the cell's live rect, as for a widget. Sets X, Y, Live, LiveRect, the screen point; one log line.</summary>
    bool LiveClickCell(ClickRequest req, ScreenState fresh, UiCellKind kind, long id, W32.POINT o, ref int sx, ref int sy) {
      if (kind == UiCellKind.None || req.W <= 0 || req.H <= 0) return false;
      string key = FrameTargets.KeyOf(kind) + "#" + id;
      int now = Environment.TickCount;
      var s = RFX.UiCellFresh(kind, id, req.W, req.H, now, RFX.UiFreshMs) ?? RFX.UiCellReadNow(kind, new[] { id }, req.W, req.H, req.U);
      double x, y; double[] box; string why;
      bool live = RFX.UiCellKeyPoint(s, id, req.W, req.H, now, out x, out y, out box, out why);
      if (live && !RFX.UiCellTopIs(kind, fresh)) { live = false; why = "the form on top is " + (fresh != null ? fresh.Top + " (" + fresh.Brief + ")" : "not read"); }
      var c = s != null ? s.Of(id) : null;
      double[] rect = c != null ? new[] { c.X, c.Y, c.W, c.H } : null;
      if (live) {
        req.X = x; req.Y = y; req.Live = true; req.LiveRect = rect;
        sx = o.X + (int)Math.Round(x); sy = o.Y + (int)Math.Round(y);
      } else if (why == null) why = s != null && !s.Ok ? "failed at " + s.FailStep : "no read";
      LogKeyLine(RFX.KeyClickLine(key, rect, live, x, y, why));
      return live;
    }

    /// <summary>A pilot's click by what it names (AutoAction.Widget = a key, AutoAction.CellKind + CellId = a list cell): the live rect's centre instead of the geometry's point when the read is settled
    /// (LiveClickKey / LiveClickCell; the log says "src=live" or "src=geo why="). Nothing to do for a click that names neither or already has a live point.</summary>
    void LiveWidgetClick(ClickRequest req, AutoAction a, ScreenState fresh, W32.POINT o, ref int sx, ref int sy) {
      if (a == null || a.Kind != AutoKind.Click || req.Live) return;
      if (a.CellKind != UiCellKind.None) LiveClickCell(req, fresh, a.CellKind, a.CellId, o, ref sx, ref sy);
      else if (!string.IsNullOrEmpty(a.Widget)) { req.WidgetKey = a.Widget; LiveClickKey(req, fresh, o, ref sx, ref sy); }
    }

    // ---- the lists as the game's ScrollRect / grid hold them (package P5, src/ListMem.cs): position, speed, bounds, the drag threshold; read at most every 100 ms and only while a pilot asks
    const int ListMemReuseMs = 100;
    long listMemGearAt, listMemHeroAt; ListMemRead listMemGear, listMemHero;

    ListMemRead GearListMemNow() {
      long now = NowMs();
      if (listMemGearAt != 0 && now - listMemGearAt < ListMemReuseMs && now >= listMemGearAt) return listMemGear;
      listMemGearAt = now; listMemGear = null;
      try { if (addrs != null && addrs.Panel != 0) listMemGear = RFX.ReadGearListMem(addrs.Panel); } catch (Exception e) { Log.Write("list mem: gear list read failed - " + e.Message); }
      ListMemSay("gear list", listMemGear);
      return listMemGear;
    }
    ListMemRead HeroListMemNow() {
      long now = NowMs();
      if (listMemHeroAt != 0 && now - listMemHeroAt < ListMemReuseMs && now >= listMemHeroAt) return listMemHero;
      listMemHeroAt = now; listMemHero = null;
      try { if (addrs != null && addrs.Form != 0) listMemHero = RFX.ReadHeroGridMem(addrs.Form); } catch (Exception e) { Log.Write("list mem: hero grid read failed - " + e.Message); }
      ListMemSay("hero grid", listMemHero);
      return listMemHero;
    }
    /// <summary>One line when the answer of a list read changes between read and not read (never every tick).</summary>
    void ListMemSay(string what, ListMemRead r) {
      string key = what + (r != null && r.Has ? " read" : " none: " + (r != null ? r.Why : "no read"));
      string was; if (listMemSayDic.TryGetValue(what, out was) && was == key) return;
      listMemSayDic[what] = key;
      Log.Write("list mem: " + what + (r != null && r.Has ? " " + r + " src=mem" : " not read (" + (r != null ? r.Why : "no addresses") + ") - the pilot keeps its own numbers"));
    }
    readonly System.Collections.Generic.Dictionary<string, string> listMemSayDic = new System.Collections.Generic.Dictionary<string, string>();

    /// <summary>The item pilot's view of the gear list from the game's own scroll (ListKnown, ListMoving, bounds, threshold).</summary>
    void FillGearListMem(AutoView v) {
      var r = GearListMemNow();
      if (r == null || !r.Has) return;
      v.ListKnown = true; v.ListMoving = r.Moving; v.ListContentH = r.Scroll.ContentH; v.ListViewH = r.Scroll.ViewH; v.ListThrPx = r.ThresholdPx > 0 ? r.ThresholdPx : 0;
    }
    /// <summary>The hero pilot's view of the hero grid from the game's own scroll.</summary>
    void FillHeroListMem(HeroView hv) {
      var r = HeroListMemNow();
      if (r == null || !r.Has) return;
      hv.ListKnown = true; hv.ListMoving = r.Moving; hv.ListPos = r.Scroll.PosY; hv.ListMax = r.Scroll.MaxPos; hv.ListThrPx = r.ThresholdPx > 0 ? r.ThresholdPx : 0;
    }
    long listMemSideAt; ListMemRead listMemSide; string listMemSideWhich = "";
    /// <summary>The filter pilot's view of the side panel's list: moving or standing (the sets list while the sets panel is open, else the stat / sub stat list the pilot opened last).</summary>
    void FillSideListMem(FilterView fv, string sideName) {
      bool side = fv.SideMem != Mem3.Unknown ? fv.SideMem == Mem3.Yes : fv.SidePanelOpen, sets = fv.SetMem != Mem3.Unknown ? fv.SetMem == Mem3.Yes && side : fv.SetPanelOpen;
      if (!side || addrs == null || addrs.Form == 0) return;
      string which = sets ? "suit" : sideName == "sub" ? "sub" : "stat"; long now = NowMs();
      if (which != listMemSideWhich || now - listMemSideAt >= ListMemReuseMs || now < listMemSideAt) {
        listMemSideWhich = which; listMemSideAt = now; listMemSide = null;
        try { listMemSide = RFX.ReadGearSideListMem(addrs.Form, which); } catch (Exception e) { Log.Write("list mem: side list read failed - " + e.Message); }
        ListMemSay("gear " + which + " list", listMemSide);
      }
      if (listMemSide != null && listMemSide.Has) fv.SideListMoving = listMemSide.Moving;
    }

    // ---- a click on the target's live cell that hit nothing (the game's raycast 0x0): the pilot asks for a regrid (AutoPilot.RegridSeq) and, when the rect stayed the same after it, a look for the layer over the list
    int regridDone, blockerDone;

    /// <summary>The pilot's view gets the game's pointer while a click on a live cell awaits its judgement.</summary>
    void FillRay(AutoView v) {
      v.RegridDone = regridDone;
      if (!pilot.WantRay) return;
      var p = RFX.ReadPointer();
      if (!p.Ok) { if (rayWhySaid != p.Why) { rayWhySaid = p.Why; Log.Write("pointer: not read (" + p.Why + ") - a click that hits nothing cannot be told"); } return; }
      v.RayKnown = true; v.RayNone = p.HitNothing;
    }
    string rayWhySaid = "";

    /// <summary>The regrid the pilot asked for: the readers' caches dropped, the whole gear grid read now (every cell made, new addresses), told in the log. The next tick's view is built on it.</summary>
    void RegridIfAsked(double W, double H, double U) {
      if (pilot.RegridSeq == regridDone) return;
      regridDone = pilot.RegridSeq;
      var sw = System.Diagnostics.Stopwatch.StartNew();
      RFX.RegridForget(); listMemGearAt = 0; listMemHeroAt = 0;
      var s = RFX.UiCellReadNow(UiCellKind.HeroGear, null, W, H, U);   // (no want list: the whole grid)
      var c = s != null ? s.Of(target) : null;
      Log.Write("atom: regrid #" + regridDone + " src=mem: caches dropped, the whole gear grid read again in " + sw.ElapsedMilliseconds + " ms - " + (s == null ? "no game" : !s.Ok ? "failed at " + s.FailStep + " (" + s.FailValue + ")" : "cells " + s.Created + " (" + s.First + ".." + s.Last + ") kept " + s.Cells.Count + ", item " + target + (c != null ? " at [" + (int)c.X + "," + (int)c.Y + " " + (int)c.W + "x" + (int)c.H + "]" + (c.Active ? "" : " switched off") : " has no cell")));
    }

    /// <summary>The rect stayed the same after the regrid and the pointer still hits nothing: what lies over the list, from the memory's facts, once per ask; the forms are searched again (the addresses may be old).</summary>
    void BlockerIfAsked() {
      if (pilot.BlockerSeq == blockerDone) return;
      blockerDone = pilot.BlockerSeq;
      var sc = screens.Current; var gm = GearMemNow(); bool? open = RFX.NavInputOpen();
      Log.Write("blocker look: " + pilot.BlockerNote + " | screen " + (sc != null ? sc.Brief + " top=" + sc.Top + " modal=" + (sc.ModalOpen ? sc.ModalWhy : "no") + " offers=" + sc.OfferCount + " flow=" + sc.Flow : "not read")
        + " | input " + (open == null ? "unread" : open.Value ? "open" : "LOCKED") + " | gear filter open=" + gm.Open + " side=" + gm.Side + " sets=" + gm.SetPanel + " pending=" + gm.Pending + " | " + RFX.ReadPointer());
      RFX.RequestFormSearch("regrid: raycast 0x0 and the cell's rect unchanged", 1500);
    }
  }
}
