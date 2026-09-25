// RealmForge.exe — highlight over the game: a click-through, always-on-top frame around the item to put on.
//
// Read-only like the rest: the game's memory is only read (which item is selected, list rows), the screen is only
// looked at (a screenshot of the list area, see src/ListTracker.cs), and the frame is a separate transparent window
// of ours that lets every click through to the game. Nothing is sent to the game.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RealmForge {
  static class W32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLEND { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLEND blend, int flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
  }

  /// <summary>A borderless per-pixel-alpha window that never takes focus or clicks.</summary>
  sealed class GlowWindow : Form {
    public GlowWindow() {
      FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
      Text = "RealmForge highlight";
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get {
        var cp = base.CreateParams;
        // LAYERED | TRANSPARENT (clicks go through) | TOOLWINDOW (no taskbar / Alt+Tab) | NOACTIVATE | TOPMOST
        cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000 | 0x00000008;
        return cp;
      }
    }
    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      try { W32.SetWindowDisplayAffinity(Handle, 0x11); } catch (Exception) { }   // keep it out of our own screenshots
    }
    protected override void WndProc(ref Message m) {
      if (m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; }   // WM_NCHITTEST -> HTTRANSPARENT
      base.WndProc(ref m);
    }

    /// <summary>Shows the bitmap (32-bit ARGB) at the screen position, in physical pixels.</summary>
    public void Put(Bitmap bmp, int x, int y) {
      if (!Visible) Show();
      IntPtr screen = W32.GetDC(IntPtr.Zero), mem = W32.CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
      try {
        hb = bmp.GetHbitmap(Color.FromArgb(0));
        old = W32.SelectObject(mem, hb);
        var pos = new W32.POINT(x, y); var size = new W32.SIZE(bmp.Width, bmp.Height); var src = new W32.POINT(0, 0);
        var blend = new W32.BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };   // AC_SRC_ALPHA
        W32.UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);   // ULW_ALPHA
      } finally {
        if (old != IntPtr.Zero) W32.SelectObject(mem, old);
        if (hb != IntPtr.Zero) W32.DeleteObject(hb);
        W32.DeleteDC(mem); W32.ReleaseDC(IntPtr.Zero, screen);
      }
    }
  }

  /// <summary>Finds the target item on the screen and keeps the frame on it (10 times a second while the gear list is open).</summary>
  sealed class OverlayController : IDisposable {
    readonly Timer timer = new Timer();
    readonly GlowWindow glow = new GlowWindow();
    readonly ListGeometry g = new ListGeometry();
    readonly ScrollAnchor anchor = new ScrollAnchor();
    readonly Action<string> report;
    EquipAddrs addrs; long target; int[] types; ulong typesPtr; long lastSel = -1; int tryTop;
    string state = "off"; int frame; string drawnKey;

    public OverlayController(Action<string> report) {
      this.report = report;
      timer.Interval = 100; timer.Tick += (s, e) => { try { Tick(); } catch (Exception ex) { Log.Write("overlay: " + ex.Message); Hide("off"); } };
    }

    public void Dispose() { timer.Dispose(); glow.Dispose(); }

    public void SetAddrs(EquipAddrs a) { if (a != addrs) { addrs = a; anchor.Has = false; types = null; lastSel = -1; } }

    /// <summary>Item to highlight (0 = none).</summary>
    public void SetTarget(long uid) {
      if (uid == target) return;
      target = uid;
      if (uid == 0) { timer.Stop(); Hide("off"); } else timer.Start();
    }

    void Hide(string st) {
      if (glow.Visible) glow.Hide();
      drawnKey = null;
      Say(st);
    }

    void Say(string st) { if (st != state) { state = st; report(st); } }

    void Tick() {
      if (target == 0 || addrs == null) { Hide("off"); return; }
      int col; ulong list;
      int row = RFX.RowOfUid(addrs, target, out col, out list);
      if (row <= 0 || col <= 0 || list == 0) { Hide("off"); return; }

      IntPtr hwnd = GameWindow();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd)) { Hide("off"); return; }
      W32.RECT cr; if (!W32.GetClientRect(hwnd, out cr) || cr.B < 200) { Hide("off"); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double H = cr.B;

      // the list was rebuilt (filter, slot, item put on): row kinds and the anchor are stale
      if (types == null || typesPtr != list) {
        types = RFX.RowTypes(list, 5000); typesPtr = list;
        int n = types.Length - 1; while (n > 0 && types[n] == 0) n--;
        Array.Resize(ref types, n + 1);
        anchor.Has = false; tryTop = 8;   // ~0.8 s: the screen may still be fading in
      }

      // the player clicked an item: the cursor shows where its row is on the screen
      long sel = RFX.ReadSel(addrs);
      if (sel != lastSel) {
        if (lastSel != -1 && sel > 0) {
          int scol; ulong sl; int srow = RFX.RowOfUid(addrs, sel, out scol, out sl);
          W32.POINT c;
          if (srow > 0 && sl == list && W32.GetCursorPos(out c)) {
            double cx = (c.X - o.X) / H, cy = (c.Y - o.Y) / H;
            if (cy >= g.ViewTop && cy <= g.ViewBottom && g.ColumnAt(cx) == scol) anchor.FromClick(g, types, srow, c.Y - o.Y, H, list);
          }
        }
        lastSel = sel;
      }
      if (!IsForeground(hwnd)) { Hide(anchor.Has ? "background" : "need_click"); return; }

      int sx = (int)(g.StripLeft * H), sy = (int)(g.ViewTop * H), sw = (int)((g.StripRight - g.StripLeft) * H), sh = (int)((g.ViewBottom - g.ViewTop) * H);
      double ph, conf;
      bool hasPhase = Phase(o.X + sx, o.Y + sy, sw, sh, H, out ph, out conf);
      // a fresh list opens at the top: anchor right away when the bars on the screen agree (no click needed)
      if (!anchor.Has && tryTop > 0) { tryTop--; if (hasPhase && anchor.FromTop(g, sy + ph, H, list)) tryTop = 0; }
      if (!anchor.Has) { Hide("need_click"); return; }

      // follow the scrolling by the stat bars' phase
      if (hasPhase) {
        int refRow = anchor.VisibleRow(g, types, H);
        if (refRow > 0) anchor.Track(g, types, refRow, sy + ph, H);
      }

      double top = anchor.RowTop(g, types, row, H), left = g.ColLeft(col) * H;
      double cell = g.CellW * H, full = g.BarBottom * H, mid = top + cell / 2;
      frame++;
      double pitch = g.PitchY * H;
      if (mid < g.ViewTop * H) Draw("up", o.X + (int)left, o.Y + sy, (int)cell, 0, (int)Math.Ceiling((g.ViewTop * H - top) / pitch));
      else if (mid > g.ViewBottom * H) Draw("down", o.X + (int)left, o.Y + sy + sh, (int)cell, 0, (int)Math.Ceiling((top + full - g.ViewBottom * H) / pitch));
      else Draw("box", o.X + (int)left, o.Y + (int)top, (int)cell, (int)full, 0);
    }

    IntPtr gameWnd; int gameWndAge;
    IntPtr GameWindow() {
      if (gameWnd != IntPtr.Zero && ++gameWndAge < 30 && W32.IsWindowVisible(gameWnd)) return gameWnd;
      gameWndAge = 0; gameWnd = IntPtr.Zero;
      try { using (var p = Process.GetProcessById(addrs.Pid)) if (!p.HasExited) gameWnd = p.MainWindowHandle; } catch (Exception) { }
      return gameWnd;
    }

    static readonly int SelfPid = Process.GetCurrentProcess().Id;
    static bool IsForeground(IntPtr game) {
      IntPtr f = W32.GetForegroundWindow(); if (f == game) return true;
      int fp, gp; W32.GetWindowThreadProcessId(f, out fp); W32.GetWindowThreadProcessId(game, out gp);
      return fp == gp || fp == SelfPid;   // the game, or our compact window over it
    }

    bool Phase(int x, int y, int w, int h, double H, out double phase, out double conf) {
      phase = 0; conf = 0;
      if (w < 20 || h < 40) return false;
      using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
        using (var gr = Graphics.FromImage(bmp)) gr.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[w * h];
        try {
          if (data.Stride == w * 4) Marshal.Copy(data.Scan0, px, 0, px.Length);
          else for (int r = 0; r < h; r++) Marshal.Copy(data.Scan0 + r * data.Stride, px, r * w, w);
        } finally { bmp.UnlockBits(data); }
        // inner part of every column's cell, in strip pixels
        var xs = new int[ListGeometry.Columns * 2];
        for (int c = 1; c <= ListGeometry.Columns; c++) {
          double l = (g.ColLeft(c) - g.StripLeft) * H;
          xs[(c - 1) * 2] = (int)(l + g.CellW * H * 0.12); xs[(c - 1) * 2 + 1] = (int)(l + g.CellW * H * 0.88);
        }
        var prof = ListTracker.BarProfile(px, w, h, xs);
        return ListTracker.FindPhase(prof, g.PitchY * H, g.BarTop * H, g.BarBottom * H, out phase, out conf);
      }
    }

    // ---------------------------------------------------------------- drawing

    // the game's own «selected» glow frame (common_frame_chosen_light), embedded in the exe
    static Image frameTex;
    static Image FrameTex() {
      if (frameTex == null) {
        try { using (var s = typeof(OverlayController).Assembly.GetManifestResourceStream("overlay/frame.png")) if (s != null) frameTex = new Bitmap(Image.FromStream(s)); }
        catch (Exception) { }
      }
      return frameTex;
    }

    /// <param name="rows">For the arrows: how many rows to scroll (shown next to the arrow).</param>
    void Draw(string kind, int x, int y, int w, int h, int rows) {
      const int M = 34;
      float pulse = (float)(0.6 + 0.4 * Math.Sin(frame * 0.45));
      string key = kind + x + ":" + y + ":" + w + ":" + h + ":" + rows + ":" + (int)(pulse * 10);
      Say(kind == "box" ? "on" : kind == "up" ? "above" : "below");
      if (key == drawnKey) return;
      drawnKey = key;
      if (kind == "box") {
        using (var bmp = new Bitmap(w + 2 * M, h + 2 * M, PixelFormat.Format32bppArgb)) {
          using (var gr = Graphics.FromImage(bmp)) {
            gr.SmoothingMode = SmoothingMode.AntiAlias; gr.Clear(Color.Transparent);
            // wide pulsing glow
            for (int i = M - 2; i >= 2; i -= 2) {
              int a = (int)(150 * pulse * (1 - i / (float)M));
              using (var pen = new Pen(Color.FromArgb(a, 255, 190, 60), 3f)) gr.DrawPath(pen, Round(M - i, M - i, w + 2 * i, h + 2 * i, 8 + i));
            }
            // dark outline so the frame shows on bright cells too, then a thick gold border
            using (var pen = new Pen(Color.FromArgb(220, 30, 18, 4), 10f)) gr.DrawPath(pen, Round(M - 3, M - 3, w + 6, h + 6, 10));
            using (var pen = new Pen(Color.FromArgb(255, 255, 214, 110), 6f)) gr.DrawPath(pen, Round(M - 3, M - 3, w + 6, h + 6, 10));
            using (var pen = new Pen(Color.FromArgb((int)(200 + 55 * pulse), 255, 250, 220), 2f)) gr.DrawPath(pen, Round(M - 3, M - 3, w + 6, h + 6, 10));
            // the game's selection frame texture on top, slightly bigger than the cell
            var tex = FrameTex();
            if (tex != null) {
              var ia = new ImageAttributes();
              ia.SetColorMatrix(new ColorMatrix { Matrix33 = (float)(0.7 + 0.3 * pulse) });
              int pad = 10;
              gr.DrawImage(tex, new Rectangle(M - pad, M - pad, w + 2 * pad, h + 2 * pad), 0, 0, tex.Width, tex.Height, GraphicsUnit.Pixel, ia);
            }
            // corner arrows pointing at the item
            using (var br = new SolidBrush(Color.FromArgb(255, 255, 220, 120)))
            using (var pen = new Pen(Color.FromArgb(230, 40, 22, 4), 2f)) {
              float cx = M + w / 2f;
              var tri = new[] { new PointF(cx - 14, 2), new PointF(cx + 14, 2), new PointF(cx, M - 8) };
              gr.FillPolygon(br, tri); gr.DrawPolygon(pen, tri);
            }
          }
          glow.Put(bmp, x - M, y - M);
        }
      } else {
        // the item is above / below the visible list: a pulsing arrow at the list edge in its column
        int aw = Math.Max(28, w / 2), ah = aw * 2 / 3, lh = Math.Max(22, w / 4);
        using (var bmp = new Bitmap(w, ah + 2 * M + lh, PixelFormat.Format32bppArgb)) {
          using (var gr = Graphics.FromImage(bmp)) {
            gr.SmoothingMode = SmoothingMode.AntiAlias; gr.Clear(Color.Transparent);
            float cx = w / 2f, t = M + (kind == "up" ? 0 : lh), b = t + ah;
            PointF[] tri = kind == "up" ? new[] { new PointF(cx, t), new PointF(cx - aw / 2f, b), new PointF(cx + aw / 2f, b) }
                                        : new[] { new PointF(cx, b), new PointF(cx - aw / 2f, t), new PointF(cx + aw / 2f, t) };
            for (int i = 6; i >= 1; i--) using (var pen = new Pen(Color.FromArgb((int)(40 * pulse), 255, 205, 90), i * 3f) { LineJoin = LineJoin.Round }) gr.DrawPolygon(pen, tri);
            using (var br = new SolidBrush(Color.FromArgb((int)(230 * (0.7 + 0.3 * pulse)), 255, 220, 120))) gr.FillPolygon(br, tri);
            using (var pen = new Pen(Color.FromArgb(220, 90, 60, 20), 2f)) gr.DrawPolygon(pen, tri);
            // «↓ 3»: rows left to scroll, on a dark plate next to the arrow
            if (rows > 0) {
              string txt = (kind == "up" ? "\u2191 " : "\u2193 ") + rows;
              using (var f = new Font("Segoe UI", lh * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel))
              using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
                float ly = kind == "up" ? b + 6 : t - lh - 6;
                var sz = gr.MeasureString(txt, f);
                var rc = new RectangleF(cx - sz.Width / 2 - 8, ly, sz.Width + 16, lh);
                using (var bg = new SolidBrush(Color.FromArgb(215, 20, 12, 4))) gr.FillPath(bg, Round(rc.X, rc.Y, rc.Width, rc.Height, lh / 2f - 1));
                using (var pen = new Pen(Color.FromArgb(230, 255, 210, 110), 1.5f)) gr.DrawPath(pen, Round(rc.X, rc.Y, rc.Width, rc.Height, lh / 2f - 1));
                using (var fg = new SolidBrush(Color.FromArgb(255, 255, 236, 180))) gr.DrawString(txt, f, fg, rc, sf);
              }
            }
          }
          glow.Put(bmp, x, kind == "up" ? y + 4 : y - ah - 2 * M - lh - 4);
        }
      }
    }

    static GraphicsPath Round(float x, float y, float w, float h, float r) {
      var p = new GraphicsPath(); float d = r * 2;
      p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
      p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
      p.CloseFigure(); return p;
    }
  }
}
