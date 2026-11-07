// Wardsage app - the live DPS meter's window over the game (TEST build). Like the boss coach (app/BattleCoach.cs): shown only with the game in
// front, click-through; with Ctrl held it takes the mouse (drag it, press the tabs, heroes and buttons, resize by the corner, a right click puts it back).
// Reads only the DPS feed (src/DpsFeed.cs) that the fight recorder fills; nothing is sent to the game.
#if TESTBUILD
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RealmForge {
  sealed class DpsMeter : IDisposable {
    /// <summary>The numbers: the recorder's OnStats goes here.</summary>
    public readonly DpsFeed Feed = new DpsFeed();
    readonly Timer timer = new Timer { Interval = 400 };
    readonly GlowWindow win = new GlowWindow { Name = "dps" };
    readonly DpsView view = new DpsView();
    readonly DpsFightWatch watch = new DpsFightWatch();
    readonly List<DpsHit> hits = new List<DpsHit>();
    Bitmap bmp; string drawn;
    int lastVersion = -1; DateTime versionAt = DateTime.UtcNow;
    // the game window's client area on screen at the last tick
    W32.POINT lastO; W32.RECT lastCr; bool haveCr;
    int curW, curH; float curS = 1f; double curU = 700;
    // a resize by the corner
    bool sizing; Point sizeFrom; int sizeW0, sizeH0;

    /// <summary>Interface language (ru / en).</summary>
    public string Lang { get { return view.Lang; } set { view.Lang = value == "en" ? "en" : "ru"; drawn = null; } }
    /// <summary>The window's left, top, width and height as fractions of the game's window; -1 = default.</summary>
    public double X = -1, Y = -1, W = -1, H = -1;
    /// <summary>The player moved / resized the window or sent it back (-1 x4): to be saved.</summary>
    public Action<double, double, double, double> PlaceChanged;
    public bool Running { get; private set; }
    /// <summary>Seconds a finished fight stays on screen.</summary>
    public const int LingerSec = 60;

    public DpsMeter() {
      timer.Tick += (s, e) => { try { Tick(); } catch (Exception ex) { Log.Write("dps: " + ex.Message); Stop(); } };
      win.HitTest = HitTest;
      win.Mouse = Mouse;
      win.Moved += () => { try { Moved(); } catch (Exception ex) { Log.Write("dps move: " + ex.Message); } };
      win.ResetPlace += () => BackToPlace();
    }

    public void Start() { if (Running) return; Running = true; drawn = null; timer.Start(); }

    public void Stop() { Running = false; timer.Stop(); sizing = false; win.Movable = false; if (win.Visible) win.Hide(); }

    public void Dispose() { timer.Dispose(); win.Dispose(); if (bmp != null) bmp.Dispose(); }

    void BackToPlace() {
      X = Y = W = H = -1; drawn = null;
      Log.Write("dps: window back to its place");
      var pc = PlaceChanged; if (pc != null) pc(-1, -1, -1, -1);
    }

    void Moved() {
      W32.RECT wr;
      if (!haveCr || lastCr.R <= 0 || lastCr.B <= 0 || !W32.GetWindowRect(win.Handle, out wr)) return;
      X = Math.Max(0, Math.Min(1, (wr.L - lastO.X) / (double)lastCr.R));
      Y = Math.Max(0, Math.Min(1, (wr.T - lastO.Y) / (double)lastCr.B));
      if (W < 0 || H < 0) { W = curW / (double)lastCr.R; H = curH / (double)lastCr.B; }
      drawn = null;
      var pc = PlaceChanged; if (pc != null) pc(X, Y, W, H);
    }

    int HitTest(int x, int y) {
      if (sizing || DpsPanel.Grip(curW, curH, curS).Contains(x, y)) return 1;
      foreach (var h in hits) if (h.R.Contains(x, y)) return 1;
      return 2;   // the rest drags the window
    }

    void Mouse(int kind, int x, int y) {
      try {
        if (kind == 3) { BackToPlace(); return; }
        if (kind == 0) {
          if (DpsPanel.Grip(curW, curH, curS).Contains(x, y)) { sizing = true; sizeFrom = Cursor.Position; sizeW0 = curW; sizeH0 = curH; win.Capture = true; }
          return;
        }
        if (kind == 1) { if (sizing) Resize(); return; }
        if (kind == 2) {
          if (sizing) { Resize(); sizing = false; win.Capture = false; SaveSize(); return; }
          foreach (var h in hits) if (h.R.Contains(x, y)) { Press(h); break; }
        }
      } catch (Exception ex) { Log.Write("dps mouse: " + ex.Message); }
    }

    void Resize() {
      if (!haveCr) return;
      var p = Cursor.Position;
      int w = sizeW0 + p.X - sizeFrom.X, h = sizeH0 + p.Y - sizeFrom.Y;
      curW = Math.Max(DpsPanel.MinW(curU), Math.Min(lastCr.R, w)); curH = Math.Max(DpsPanel.MinH(curU), Math.Min(lastCr.B, h));
      Redraw(true);
    }

    void SaveSize() {
      if (!haveCr || lastCr.R <= 0 || lastCr.B <= 0) return;
      W = curW / (double)lastCr.R; H = curH / (double)lastCr.B;
      if (X < 0 || Y < 0) {
        W32.RECT wr;
        if (W32.GetWindowRect(win.Handle, out wr)) { X = Math.Max(0, (wr.L - lastO.X) / (double)lastCr.R); Y = Math.Max(0, (wr.T - lastO.Y) / (double)lastCr.B); }
      }
      Log.Write("dps: window size " + W.ToString("0.00") + " x " + H.ToString("0.00"));
      var pc = PlaceChanged; if (pc != null) pc(X, Y, W, H);
    }

    void Press(DpsHit h) {
      if (h.Kind == "mode") view.Mode = DpsPanel.Modes[h.Arg];
      else if (h.Kind == "seg") { view.Segment = DpsPanel.Segments[h.Arg]; view.HasOpen = false; }
      else if (h.Kind == "hero") { view.HasOpen = true; view.OpenC = h.C; view.OpenUnit = h.Arg; }
      else if (h.Kind == "back") view.HasOpen = false;
      else if (h.Kind == "reset") { view.HasOpen = false; Feed.Reset(); }
      else return;
      Redraw(true);
    }

    /// <summary>Draws again at the current size and puts it where it is (after a press or a resize).</summary>
    void Redraw(bool force) {
      if (!haveCr) return;
      var snap = Feed.Snapshot;
      var nb = DpsPanel.Render(snap, view, curW, curH, curS, hits);
      var old = bmp; bmp = nb; drawn = null;
      var wr = new W32.RECT();
      int px, py;
      if (win.IsHandleCreated && W32.GetWindowRect(win.Handle, out wr)) { px = wr.L; py = wr.T; } else { px = lastO.X; py = lastO.Y; }
      win.Put(bmp, px, py);
      if (old != null) old.Dispose();
    }

    void Tick() {
      var snap = Feed.Snapshot;
      // a new fight: back to the general view (the open hero closed, the segment Current); the mode tab, the size and the place stay
      if (watch.Changed(snap)) { view.HasOpen = false; view.Segment = DpsSegment.Current; drawn = null; }
      if (snap.Version != lastVersion) { lastVersion = snap.Version; versionAt = DateTime.UtcNow; }
      bool any = snap.Current != null || snap.Total != null;
      bool live = snap.Current != null && snap.Current.Open;
      // nothing yet, or a finished fight shown long enough: hide (Ctrl brings it back while there is something)
      bool move = (Control.ModifierKeys & Keys.Control) != 0;
      if (!any || (!live && !move && (DateTime.UtcNow - versionAt).TotalSeconds > LingerSec)) { win.Movable = false; if (win.Visible) win.Hide(); return; }
      var ps = GameInfo.GameProcesses();
      IntPtr hwnd = ps.Length > 0 ? ps[0].MainWindowHandle : IntPtr.Zero;
      int fp = 0, gp = 0;
      if (hwnd != IntPtr.Zero) {
        IntPtr fg = W32.GetForegroundWindow();
        W32.GetWindowThreadProcessId(fg, out fp); W32.GetWindowThreadProcessId(hwnd, out gp);
        if (win.IsHandleCreated && fg == win.Handle) fp = gp;   // a click on the panel still counts as the game
      }
      if (win.Dragging || sizing) return;
      W32.RECT cr;
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || fp != gp || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { win.Movable = false; if (win.Visible) win.Hide(); return; }
      win.Movable = move;
      view.Interactive = move;
      double U = Ui.Unit(cr.R, cr.B);
      int w = W > 0 ? (int)(W * cr.R) : DpsPanel.DefaultW(U), h = H > 0 ? (int)(H * cr.B) : DpsPanel.DefaultH(U);
      w = Math.Max(DpsPanel.MinW(U), Math.Min(cr.R, w)); h = Math.Max(DpsPanel.MinH(U), Math.Min(cr.B, h));
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      lastO = o; lastCr = cr; haveCr = true; curU = U; curS = DpsPanel.ScaleFor(U); curW = w; curH = h;
      string key = snap.Version + "|" + view.Mode + "|" + view.Segment + "|" + view.HasOpen + view.OpenC + ":" + view.OpenUnit + "|" + w + "x" + h + "|" + move + "|" + view.Lang;
      Bitmap old = null;
      if (key != drawn || bmp == null) { old = bmp; bmp = DpsPanel.Render(snap, view, w, h, curS, hits); drawn = key; }
      int px = X >= 0 ? (int)(X * cr.R) : cr.R - w - (int)(0.012 * U), py = Y >= 0 ? (int)(Y * cr.B) : (int)(0.22 * cr.B);
      px = Math.Max(0, Math.Min(cr.R - w, px)); py = Math.Max(0, Math.Min(cr.B - h, py));
      win.Put(bmp, o.X + px, o.Y + py);
      if (old != null) old.Dispose();   // the window shows the new picture now
    }
  }
}
#endif
