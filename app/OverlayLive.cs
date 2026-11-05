// Wardsage.exe - the overlay's live-rect clicks (package P2) (docs/specs/2026-11-05-work-packages.md): a pilot's click by the widget it presses (ClickRequest.WidgetKey, src/FrameTargets.cs) or by the
// cell of a list (UiCellKind) lands on the centre of the widget's live rect - never on a fraction of the window. The point is the centre of the rect shrunk by 20 % (the gate holds it inside:
// src/ClickGate.cs CheckWidgetKey) and only a settled rect counts (scale 1 on its chain, not while a pop-up or a panel tweens in); otherwise the pilot's own point stays and the log says
// "src=geo why=...". The decisions are RFX.UiKeyPoint / RFX.UiCellKeyPoint (src/UiButtons.cs, unit-tested in tests/LiveClickTests.cs); this part only reads, sets the request and logs.
using System;

namespace RealmForge {
  sealed partial class OverlayController {
    /// <summary>The click of <paramref name="req"/> by its widget key: the live rect read fresh (at most UiFreshMs old, else one read now), the form on top of the fresh screen. A live point is set
    /// into the request (X, Y, Live, LiveRect) and the screen point (<paramref name="sx"/>, <paramref name="sy"/>); else nothing changes (the pilot's point). One log line per click.</summary>
    bool LiveClickKey(ClickRequest req, ScreenState fresh, W32.POINT o, ref int sx, ref int sy) {
      string key = req.WidgetKey;
      if (string.IsNullOrEmpty(key) || req.W <= 0 || req.H <= 0) return false;
      var ft = FrameTargets.Of(key); var w = FrameTargets.Widget(ft);
      if (w == null) { Log.Write(RFX.KeyClickLine(key, null, false, 0, 0, "no widget for the key")); return false; }
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
      Log.Write(RFX.KeyClickLine(key, rect, live, x, y, why));
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
      Log.Write(RFX.KeyClickLine(key, rect, live, x, y, why));
      return live;
    }
  }
}
