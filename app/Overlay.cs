// RealmForge.exe — highlight over the game: a click-through, always-on-top frame around the item to put on, and the
// auto-pilot that opens the gear slot, drags the list and clicks the item (src/AutoPilot.cs).
//
// The game's memory is only read (which item is selected, list rows), the screen is only looked at (a screenshot of
// the list area, see src/ListTracker.cs), and the frame is a separate transparent window of ours that lets every
// click through to the game. The auto-pilot moves the mouse like a player would (SendInput: a click on the slot or
// the item, a drag over the list) and only while the game is in front and the player is not using the mouse;
// «Заменить» only with Settings → «Автоматически подтверждать замену» (see src/AutoPilot.cs for the checks).
// Nothing is written into the game.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
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
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }   // type 0 = mouse; 40 bytes on x64
    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, WHEEL = 0x0800;
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

  /// <summary>A borderless per-pixel-alpha window that never takes focus; it lets clicks through, unless it is a
  /// button (<see cref="Pressed"/>: the «Отменить» over the game).</summary>
  sealed class GlowWindow : Form {
    readonly bool clickable;
    /// <summary>A click on a button window (it still never takes the focus from the game).</summary>
    public event Action Pressed;
    public GlowWindow() : this(false) { }
    public GlowWindow(bool clickable) {
      this.clickable = clickable;
      FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
      Text = clickable ? "RealmForge cancel" : "RealmForge highlight";
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get {
        var cp = base.CreateParams;
        // LAYERED | TRANSPARENT (clicks go through) | TOOLWINDOW (no taskbar / Alt+Tab) | NOACTIVATE | TOPMOST
        cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | 0x00000008;
        if (!clickable) cp.ExStyle |= 0x00000020;
        return cp;
      }
    }
    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      try { W32.SetWindowDisplayAffinity(Handle, 0x11); } catch (Exception) { }   // keep it out of our own screenshots
    }
    protected override void WndProc(ref Message m) {
      if (!clickable && m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; }   // WM_NCHITTEST -> HTTRANSPARENT
      if (clickable && m.Msg == 0x0021) { m.Result = (IntPtr)3; return; }     // WM_MOUSEACTIVATE -> MA_NOACTIVATE
      if (clickable && m.Msg == 0x0202) { var p = Pressed; if (p != null) p(); return; }   // WM_LBUTTONUP
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
    readonly GlowWindow tip = new GlowWindow();     // hint on the game's own buttons (filter, «Заменить», next slot)
    // «Отменить надевание» over the game while a build is being put on: the player's clicks in the game send our
    // window to the back, so the way to call it off is where the player looks
    readonly GlowWindow cancelWin = new GlowWindow(true);
    /// <summary>The player pressed «Отменить надевание» over the game.</summary>
    public event Action CancelPressed;
    /// <summary>The button's text in the interface language.</summary>
    public string CancelText = "Отменить надевание";
    string cancelDrawn; Bitmap cancelBmp;
    readonly ListGeometry g = new ListGeometry();
    readonly HintGeometry hg = new HintGeometry();
    readonly ScrollAnchor anchor = new ScrollAnchor();
    readonly Action<string> report;
    readonly Action<string> autoReport;
    readonly AutoPilot pilot;
    readonly FilterGeometry fg = new FilterGeometry();
    readonly FilterPilot fpilot;
    readonly HeroGeometry heroG = new HeroGeometry();
    HeroGeometry hg2 { get { return heroG; } }
    readonly HeroPilot hpilot;
    HeroScreen heroScreen;
    /// <summary>The plan the player started («Надеть»): its hero is brought up on the hero screen; 0 = none.</summary>
    long runHero;
    long focusAfter, slotCloseAfter;
    // the item the guide looks for and what it says about it (the filter pilot), and what the plans say about items
    long filterItem; string filterKind = "";
    readonly System.Collections.Generic.Dictionary<long, long[]> itemInfo = new System.Collections.Generic.Dictionary<long, long[]>();
    readonly System.Collections.Generic.Dictionary<int, long[]> suitOrder = new System.Collections.Generic.Dictionary<int, long[]>();
    readonly System.Collections.Generic.Dictionary<int, long[]> statOrder = new System.Collections.Generic.Dictionary<int, long[]>();
    readonly System.Collections.Generic.Dictionary<int, long[]> subOrder = new System.Collections.Generic.Dictionary<int, long[]>();
    /// <summary>Settings → «Автонажатие»: the pilot acts only when this is on.</summary>
    public bool AutoEnabled = true;
    /// <summary>Settings → «Автоматически подтверждать замену»: the pilot may also press «Заменить».</summary>
    public bool AutoConfirm;
    // what the frame tick found this tick (for the pilot)
    int curRow, curCol; long curSel;
    // player activity: the cursor moved by someone else than the pilot, or a mouse button went down
    volatile bool sending; W32.POINT seen; bool seenSet; long lastMoveMs; bool btnWasDown, userClick; string autoSaid = "";
    EquipAddrs addrs; long target; int[] types; ulong typesPtr; long lastSel = -1;
    string state = "off"; int frame; string drawnKey;
    // diagnostics (Settings → «Диагностика рамки»): screenshots and list data into debug\ for a few minutes
    DateTime diagUntil = DateTime.MinValue; int diagTick, diagN, shotN; string diagLast, diagDir, diagInfo = "";
    string hintKind = ""; int hintSlot = -1; string[] hintLines = new string[0]; string tipKey; long hintHero;
    int[] confirmBtn;   // «Заменить» / «Надеть» found on the screen this tick (TickHint), for the pilot
    // storage cleanup: the items to select on the game's sell screen (null = no run); the hero whose gear screen leads there
    readonly SellPilot spilot;
    readonly Action<SellPilot, string> sellReport;
    System.Collections.Generic.HashSet<long> sellWanted; System.Collections.Generic.HashSet<int> sellParts; System.Collections.Generic.Dictionary<long, int> sellLevels; long sellHero; long sellBtnAt; string sellSaid = "";

    public OverlayController(Action<string> report, Action<string> autoReport, Action<SellPilot, string> sellReport) {
      this.report = report; this.autoReport = autoReport; this.sellReport = sellReport;
      spilot = new SellPilot(SellGeometry.Inventory(1920, 1009, 8));
      pilot = new AutoPilot(g);
      fpilot = new FilterPilot(fg);
      hpilot = new HeroPilot(heroG);
      timer.Interval = 100; timer.Tick += (s, e) => { try { Tick(); } catch (Exception ex) { Log.Write("overlay: " + ex.Message); Hide("off"); } };
      cancelWin.Pressed += () => { cancelWin.Hide(); var c = CancelPressed; if (c != null) c(); };
    }

    public void Dispose() { timer.Dispose(); glow.Dispose(); tip.Dispose(); cancelWin.Dispose(); }

    public void StartDiag(int minutes) {
      // one folder per run: a second run must not overwrite the first one's files
      diagDir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "debug", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
      Directory.CreateDirectory(diagDir);
      diagUntil = DateTime.Now.AddMinutes(minutes); diagN = 0; shotN = 0; diagLast = null;
      DiagLog("start " + DateTime.Now.ToString("s"));
      timer.Start();
    }

    void DiagLog(string line) { try { File.AppendAllText(Path.Combine(diagDir, "frame.log"), line + "\r\n"); } catch (Exception) { } }

    // every 5th tick: a log line and the panel's Lua data when it changed (≤ 60 files); every 10th: a screenshot (≤ 60),
    // only while the game is in front - the screen area of its window shows whatever window is on top otherwise
    void DiagTick(IntPtr hwnd, W32.POINT o, W32.RECT cr, string info) {
      if (DateTime.Now > diagUntil || diagDir == null || ++diagTick % 5 != 0) return;
      DiagLog(DateTime.Now.ToString("HH:mm:ss.f") + " " + info);
      string panel = RFX.DumpPanel(addrs);
      if (panel != null && panel != diagLast && diagN < 60) {
        diagLast = panel; diagN++;
        try { File.WriteAllText(Path.Combine(diagDir, "panel_" + diagN.ToString("00") + ".json"), panel); } catch (Exception) { }
        DiagLog("  panel_" + diagN.ToString("00") + ".json");
      }
      if (shotN >= 60 || diagTick % 10 != 0 || cr.R <= 0 || cr.B <= 0 || !IsForeground(hwnd)) return;
      shotN++;
      try {
        using (var bmp = new Bitmap(cr.R, cr.B, PixelFormat.Format32bppArgb)) {
          using (var gr = Graphics.FromImage(bmp)) gr.CopyFromScreen(o.X, o.Y, 0, 0, new Size(cr.R, cr.B), CopyPixelOperation.SourceCopy);
          bmp.Save(Path.Combine(diagDir, "screen_" + shotN.ToString("00") + ".png"), ImageFormat.Png);
        }
        DiagLog("  screen_" + shotN.ToString("00") + ".png");
      } catch (Exception e) { DiagLog("  shot failed: " + e.Message); }
    }

    public void SetAddrs(EquipAddrs a) { if (a != addrs) { addrs = a; anchor.Has = false; types = null; lastSel = -1; suitOrder.Clear(); statOrder.Clear(); subOrder.Clear(); } }

    /// <summary>The item the guide is at and its verdict ("filter": far down, "hiddenEq" / "hiddenFilter" / "notIn": not in
    /// the list, ...): the filter pilot sets the game's filter for it when that helps.</summary>
    public void SetFilterGoal(long item, string kind) { filterItem = item; filterKind = kind ?? ""; if (item != 0) Run(); }

    /// <summary>An item's set and main stat (from the plan), for the game's filter.</summary>
    public void SetItemInfo(long uid, long setId, long statId) { if (uid > 0 && (setId > 0 || statId > 0)) itemInfo[uid] = new[] { setId, statId }; }

    /// <summary>The started plan's hero (0 = none): the pilot opens that hero's gear on the hero screen.</summary>
    public void SetRun(long hero) { if (hero == runHero) return; runHero = hero; Run(); }

    /// <summary>Every pilot starts over (a new «Надеть»: after a failure the player may have fixed the game's screen).</summary>
    /// <summary>What the helper is at while a started plan runs (null = no plan, or a pilot is clicking / waiting for the
    /// player): the same key for long means the plan stands still.</summary>
    public string StallKey() {
      if (runHero <= 0 || autoSaid == "work" || autoSaid == "paused" || autoSaid == "background") return null;
      return runHero + "|" + target + "|" + filterItem + "|" + filterKind + "|" + hintKind + "|" + autoSaid;
    }

    public void RestartPilots() { pilot.Reset(); fpilot.Reset(); hpilot.Reset(); }

    /// <summary>Item to highlight (0 = none).</summary>
    public void SetTarget(long uid) {
      if (uid == target) return;
      target = uid;
      if (uid == 0) Hide("off");
      Run();
    }

    /// <summary>Hint on the game's own buttons: "filter", "replace", "slot" (slot 0–4) or "" = none; up to two text lines;
    /// <paramref name="hero"/>: the plan's hero (for «Заменить»).</summary>
    public void SetHint(string kind, int slot, string[] lines, long hero) {
      kind = kind ?? ""; lines = lines ?? new string[0];
      if (kind == hintKind && slot == hintSlot && hero == hintHero && string.Join("\n", lines) == string.Join("\n", hintLines)) return;
      hintKind = kind; hintSlot = slot; hintLines = lines; hintHero = hero;
      HideTip(); Run();
    }

    /// <summary>Storage cleanup: select these items on the game's sell screen (null = stop). The app opens the screen from
    /// the gear list of <paramref name="hero"/> (or of the hero the game shows); «Продать» is pressed by the player.</summary>
    public void SetSell(System.Collections.Generic.ICollection<long> uids, System.Collections.Generic.ICollection<int> parts, System.Collections.Generic.Dictionary<long, int> levels, long hero) {
      sellLevels = levels;
      sellWanted = uids != null && uids.Count > 0 ? new System.Collections.Generic.HashSet<long>(uids) : null;
      sellParts = parts != null && parts.Count > 0 ? new System.Collections.Generic.HashSet<int>(parts) : null;
      sellHero = hero; sellSaid = ""; spilot.Reset(); hpilot.Reset(); sellBtnAt = 0; sellScanOnce = true;
      Run();
    }

    void Run() {
      if (target != 0 || filterItem != 0 || hintKind != "" || runHero != 0 || sellWanted != null || DateTime.Now < diagUntil) { timer.Start(); return; }
      timer.Stop(); HideTip(); if (cancelWin.Visible) cancelWin.Hide();
    }

    void HideTip() { if (tip.Visible) tip.Hide(); tipKey = null; }

    void Hide(string st) {
      if (glow.Visible) glow.Hide();
      drawnKey = null;
      Say(st);
    }

    void Say(string st) { if (st != state) { state = st; report(st); } }

    void Tick() {
      curRow = curCol = 0; curSel = 0;
      TickFrame();
      TickAuto();
      TickCancel();
    }

    // «Отменить надевание»: top centre of the game (free on the hero screen and in the gear list), only while a started
    // build is on and the game is in front
    void TickCancel() {
      IntPtr hwnd = GameWindow();
      int fp = 0, gp = 0;
      if (hwnd != IntPtr.Zero) { W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp); W32.GetWindowThreadProcessId(hwnd, out gp); }
      W32.RECT cr;
      if (runHero <= 0 || !AutoEnabled || hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || fp != gp || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) {
        if (cancelWin.Visible) cancelWin.Hide();
        return;
      }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double U = Ui.Unit(cr.R, cr.B);
      int w = (int)(0.25 * U), h = (int)(0.058 * U);
      string key = CancelText + "|" + w + "x" + h;
      if (key != cancelDrawn || cancelBmp == null) {
        if (cancelBmp != null) cancelBmp.Dispose();
        cancelBmp = CancelBitmap(w, h, CancelText); cancelDrawn = key;
      }
      cancelWin.Put(cancelBmp, o.X + cr.R / 2 - w / 2, o.Y + (int)(0.012 * U));
    }

    static Bitmap blueTex;
    /// <summary>The game's blue button (its texture, stretched by nine slices) with the text.</summary>
    static Bitmap CancelBitmap(int w, int h, string text) {
      if (blueTex == null)
        try { using (var s = typeof(OverlayController).Assembly.GetManifestResourceStream("overlay/btn_blue.png")) if (s != null) blueTex = new Bitmap(Image.FromStream(s)); }
        catch (Exception) { }
      var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
      using (var gr = Graphics.FromImage(bmp)) {
        gr.SmoothingMode = SmoothingMode.AntiAlias; gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
        gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        if (blueTex != null) {
          // slices of the texture (as the site's border-image: 22 26): corners kept, edges and centre stretched
          int sl = 26, st = 22, sw = blueTex.Width, sh = blueTex.Height;
          int dl = Math.Min(w / 3, (int)(h * 0.42)), dt = Math.Min(h / 3, (int)(h * 0.36));
          var ia = new ImageAttributes(); ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
          Action<int, int, int, int, int, int, int, int> part = (dx, dy, dw, dh, sx, sy, sww, shh) =>
            gr.DrawImage(blueTex, new Rectangle(dx, dy, dw, dh), sx, sy, sww, shh, GraphicsUnit.Pixel, ia);
          part(0, 0, dl, dt, 0, 0, sl, st); part(dl, 0, w - 2 * dl, dt, sl, 0, sw - 2 * sl, st); part(w - dl, 0, dl, dt, sw - sl, 0, sl, st);
          part(0, dt, dl, h - 2 * dt, 0, st, sl, sh - 2 * st); part(dl, dt, w - 2 * dl, h - 2 * dt, sl, st, sw - 2 * sl, sh - 2 * st);
          part(w - dl, dt, dl, h - 2 * dt, sw - sl, st, sl, sh - 2 * st);
          part(0, h - dt, dl, dt, 0, sh - st, sl, st); part(dl, h - dt, w - 2 * dl, dt, sl, sh - st, sw - 2 * sl, st); part(w - dl, h - dt, dl, dt, sw - sl, sh - st, sl, st);
        } else using (var b = new SolidBrush(Color.FromArgb(230, 40, 60, 130))) gr.FillRectangle(b, 0, 0, w, h);
        using (var f = new Font("Segoe UI", Math.Max(9f, h * 0.30f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
          var r = new RectangleF(0, -h * 0.13f, w, h);   // the texture's blue field sits above its middle
          using (var sh = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) gr.DrawString(text, f, sh, new RectangleF(r.X + 1, r.Y + 1.5f, r.Width, r.Height), sf);
          gr.DrawString(text, f, Brushes.White, r, sf);
        }
      }
      return bmp;
    }

    void TickFrame() {
      if (DateTime.Now < diagUntil) {   // also before the gear screen was scanned: the filter panel is often opened first
        IntPtr dh = GameWindow(); var dp = new W32.POINT(0, 0); W32.RECT dr;
        if (dh != IntPtr.Zero && W32.GetClientRect(dh, out dr)) { W32.ClientToScreen(dh, ref dp); DiagTick(dh, dp, dr, diagInfo); }
      }
      TickHint();
      if (target == 0 || addrs == null) { Hide("off"); return; }
      int col; ulong list;
      int row = RFX.RowOfUid(addrs, target, out col, out list);
      if (row <= 0 || col <= 0 || list == 0) { Hide("off"); return; }
      curRow = row; curCol = col;

      IntPtr hwnd = GameWindow();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd)) { Hide("off"); return; }
      W32.RECT cr; if (!W32.GetClientRect(hwnd, out cr) || cr.B < 200) { Hide("off"); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double H = Ui.Unit(cr.R, cr.B);   // the list is anchored to the top left: everything scales with the UI unit

      // the list was rebuilt (filter, slot, item put on): row kinds and the anchor are stale
      if (types == null || typesPtr != list) {
        types = RFX.RowTypes(list, 5000); typesPtr = list;
        int n = types.Length - 1; while (n > 0 && types[n] == 0) n--;
        Array.Resize(ref types, n + 1);
        anchor.Has = false;
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
      curSel = sel;
      if (!IsForeground(hwnd)) { Hide(anchor.Has ? "background" : "need_click"); return; }

      int sx = (int)(g.StripLeft * H), sy = (int)(g.ViewTop * H), sw = (int)((g.StripRight - g.StripLeft) * H), sh = (int)((g.ViewBottom - g.ViewTop) * H);
      double ph, conf;
      bool hasPhase = Phase(o.X + sx, o.Y + sy, sw, sh, H, out ph, out conf);
      // a fresh list opens at the top: anchor right away when the bars on the screen agree (no click needed)
      // (also later: a short filtered list cannot scroll, it stays at the top; a wrong guess is corrected by the next click)
      if (!anchor.Has && hasPhase) anchor.FromTop(g, types, sy + ph, H, list);
      if (!anchor.Has) { Hide("need_click"); return; }
      diagInfo = "H=" + H + " phase=" + (hasPhase ? (sy + ph).ToString("0.0") : "none") + " conf=" + conf.ToString("0.00")
        + " row1=" + anchor.Row1Top.ToString("0.0") + " target=" + row + "/" + col + " sel=" + sel + " state=" + state;

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

    void TickHint() {
      confirmBtn = null;
      if (hintKind == "" || addrs == null) { HideTip(); return; }
      IntPtr hwnd = GameWindow();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || !IsForeground(hwnd)) { HideTip(); return; }
      W32.RECT cr; if (!W32.GetClientRect(hwnd, out cr) || cr.B < 200) { HideTip(); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double H = cr.B, W = cr.R;
      if (hintKind == "replace") confirmBtn = ConfirmButton(o, W, H);
      int[] r = hintKind == "filter" ? hg.Filter(W, H) : hintKind == "replace" ? confirmBtn ?? hg.Replace(W, H) : hintKind == "slot" ? hg.Slot(hintSlot, W, H) : null;
      if (r == null) HideTip(); else DrawTip(o.X + r[0], o.Y + r[1], r[2], r[3], hintLines, H);
    }

    /// <summary>A pulsing gold frame around a button of the game (x, y, w, h: screen px) with a text plate above it.</summary>
    void DrawTip(int x, int y, int w, int h, string[] lines, double H) {
      float pulse = (float)(0.6 + 0.4 * Math.Sin(frame * 0.45));
      string key = x + ":" + y + ":" + w + ":" + h + ":" + string.Join("|", lines) + ":" + (int)(pulse * 10);
      if (key == tipKey) return;
      tipKey = key;
      float fs = (float)Math.Max(13.0, H * 0.022);
      using (var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel)) {
        float tw = 0, lh = fs * 1.35f;
        using (var probe = new Bitmap(1, 1)) using (var pg = Graphics.FromImage(probe)) foreach (var l in lines) tw = Math.Max(tw, pg.MeasureString(l, f).Width);
        int pw = lines.Length > 0 ? (int)tw + 28 : 0, ph = lines.Length > 0 ? (int)(lh * lines.Length) + 16 : 0;
        int bw = Math.Max(w + 52, pw + 8), bh = h + 52 + (ph > 0 ? ph + 14 : 0);
        using (var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb)) {
          using (var gr = Graphics.FromImage(bmp)) {
            gr.SmoothingMode = SmoothingMode.AntiAlias; gr.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; gr.Clear(Color.Transparent);
            int fx = 26, fy = bh - h - 26;
            for (int i = 24; i >= 2; i -= 2)
              using (var pen = new Pen(Color.FromArgb((int)(140 * pulse * (1 - i / 26f)), 255, 190, 60), 3f)) gr.DrawPath(pen, Round(fx - i, fy - i, w + 2 * i, h + 2 * i, 6 + i));
            using (var pen = new Pen(Color.FromArgb(220, 30, 18, 4), 8f)) gr.DrawPath(pen, Round(fx - 2, fy - 2, w + 4, h + 4, 8));
            using (var pen = new Pen(Color.FromArgb(255, 255, 214, 110), 4f)) gr.DrawPath(pen, Round(fx - 2, fy - 2, w + 4, h + 4, 8));
            if (ph > 0) {
              var rc = new RectangleF(0, 0, pw, ph);
              using (var bg = new SolidBrush(Color.FromArgb(235, 16, 12, 8))) gr.FillPath(bg, Round(rc.X + 1, rc.Y + 1, rc.Width - 2, rc.Height - 2, 6));
              using (var pen = new Pen(Color.FromArgb(240, 232, 190, 110), 2f)) gr.DrawPath(pen, Round(rc.X + 1, rc.Y + 1, rc.Width - 2, rc.Height - 2, 6));
              using (var fg = new SolidBrush(Color.FromArgb(255, 255, 236, 180))) for (int i = 0; i < lines.Length; i++) gr.DrawString(lines[i], f, fg, 14, 8 + i * lh);
              float ax = Math.Min(pw - 20, fx + w / 2f);
              var tri = new[] { new PointF(ax - 9, ph - 1), new PointF(ax + 9, ph - 1), new PointF(ax, ph + 11) };
              using (var br = new SolidBrush(Color.FromArgb(240, 232, 190, 110))) gr.FillPolygon(br, tri);
            }
          }
          tip.Put(bmp, x - 26, y - (bh - h - 26));
        }
      }
    }

    // ---------------------------------------------------------------- auto-pilot

    static long NowMs() { return Environment.TickCount & 0x7FFFFFFF; }

    void AutoSay(string st) { if (st != autoSaid) { autoSaid = st; autoReport(st); Log.Write("auto: " + st + (target != 0 ? " item " + target + " row " + curRow + "/" + curCol + " anchor=" + anchor.Has : "")); } }

    /// <summary>The pilot's step for this tick: gather what the game shows, ask src/AutoPilot.cs, do the action.</summary>
    void TickAuto() {
      if (!AutoEnabled || addrs == null || sending) { if (!AutoEnabled) AutoSay("off"); return; }
      long now = NowMs();
      // the player's own mouse use (the pilot's moves are remembered in `seen`)
      W32.POINT c;
      if (W32.GetCursorPos(out c)) {
        if (seenSet && (c.X != seen.X || c.Y != seen.Y)) lastMoveMs = now;
        seen = c; seenSet = true;
      }
      bool down = (W32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (W32.GetAsyncKeyState(0x02) & 0x8000) != 0;
      if (down && !btnWasDown) userClick = true;
      btnWasDown = down;

      IntPtr hwnd = GameWindow();
      // a started plan («Надеть» here, on the site, from the bridge): the player's click left our window in front, so
      // the game is brought forward (and restored) - only from our own window, never over another program
      int fp0; W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp0);
      if ((runHero > 0 || sellWanted != null) && hwnd != IntPtr.Zero && fp0 == SelfPid && now >= focusAfter) {
        focusAfter = now + 3000;
        if (W32.IsIconic(hwnd)) W32.ShowWindow(hwnd, 9);   // SW_RESTORE
        W32.SetForegroundWindow(hwnd);
        return;
      }
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd)) { AutoSay("idle"); return; }
      W32.RECT cr; if (!W32.GetClientRect(hwnd, out cr) || cr.B < 200) { AutoSay("idle"); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      int fp, gp; W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp); W32.GetWindowThreadProcessId(hwnd, out gp);
      if (sellWanted != null) { bool busy = down || now - lastMoveMs < 700; bool uc = userClick; userClick = false; TickSell(o, cr, now, fp == gp, busy, uc); return; }

      // «Заменить»: the guide says the right item is selected; the pilot re-checks hero and owner in memory right now
      bool confirm = AutoConfirm && hintKind == "replace" && target > 0;
      // the button is looked for only with the game in front (TickHint): the screen shows the game then
      var v = new AutoView {
        NowMs = now, Foreground = fp == gp, UserBusy = down || now - lastMoveMs < 700, UserClicked = userClick, H = Ui.Unit(cr.R, cr.B),
        Slot = hintKind == "slot" ? hg.Slot(hintSlot, cr.R, cr.B) : null,
        Target = target, Row = curRow, Col = curCol, Sel = curSel,
        AnchorHas = anchor.Has && types != null && typesPtr != 0, Row1Top = anchor.Row1Top, Types = types,
        AutoConfirm = confirm, Button = confirm ? confirmBtn : null, Hero = hintHero,
        ScreenHero = confirm ? RFX.ReadHero(addrs) : 0, Owner = confirm ? RFX.ReadOwner(addrs, target) : -1
      };
      userClick = false;
      // first the plan's hero on the hero screen (a started plan only), then the game's filter (the item far down or
      // hidden), then the item itself
      if (runHero > 0) {
        heroScreen = RFX.ReadHeroScreen(addrs, heroScreen);
        // the gear filter pop-up left open (the last item's) stays over the hero grid and takes its clicks: closed first
        if (heroScreen.Hero > 0 && heroScreen.Hero != runHero && v.Foreground && !v.UserBusy && now >= slotCloseAfter
            && Mark(o, fg.X(fg.MainCloseX, cr.R, cr.B), fg.Y(fg.CloseY, cr.R, cr.B), Ui.Unit(cr.R, cr.B), ButtonCheck.IsCloseRed)) {
          slotCloseAfter = now + 900;
          Log.Write("hero: a pop-up (the gear filter or an item card) is over the grid - closing it");
          Send(new AutoAction { Kind = AutoKind.Click }, o.X + (int)Math.Round(fg.X(fg.MainCloseX, cr.R, cr.B)), o.Y + (int)Math.Round(fg.Y(fg.CloseY, cr.R, cr.B)));
          return;
        }
        var hv = new HeroView {
          NowMs = now, Foreground = v.Foreground, UserBusy = v.UserBusy, UserClicked = v.UserClicked, W = cr.R, H = Ui.Unit(cr.R, cr.B), ClientH = cr.B,
          Plan = runHero, Hero = heroScreen.Hero, Tab = heroScreen.Tab, Part = heroScreen.Part, SmallCards = heroScreen.SmallCards,
          Heroes = heroScreen.Heroes,
          HeroesButton = heroScreen.Hero == 0 && v.Foreground && HeroesButtonSeen(o, cr.R, cr.B),
          FilterOpen = v.Foreground && HeroFilterSeen(o, cr.R, cr.B), ClassChosen = heroScreen.ClassChosen, FactionList = heroScreen.FactionList,
          ChosenCamps = heroScreen.ChosenCamps, ClassCount = heroScreen.ClassCount, GridPtr = heroScreen.GridPtr
        };
        long[] tags = HeroTags(runHero);
        if (tags != null) { hv.HeroClass = tags[0]; hv.HeroCamps = new long[tags.Length - 1]; Array.Copy(tags, 1, hv.HeroCamps, 0, tags.Length - 1); }
        var ha = hpilot.Step(hv);
        LogHero(hv);
        if (ha != null) {
          pilot.Reset();
          AutoSay(hpilot.State == "work" || hpilot.State == "paused" || hpilot.State == "background" ? hpilot.State : "hero_" + hpilot.State);
          if (ha.Kind == AutoKind.None) return;
          int hx = o.X + (int)Math.Round(ha.X), hy = o.Y + (int)Math.Round(ha.Y), hy2 = hy + (int)Math.Round(ha.DY);
          if (hx < o.X || hy < o.Y || hx >= o.X + cr.R || hy >= o.Y + cr.B || hy2 < o.Y || hy2 >= o.Y + cr.B) return;
          Send(ha, hx, hy);
          return;
        }
      }
      var fview = FilterViewNow(o, cr, now, v.Foreground, v.UserBusy);
      var fa = fpilot.Step(fview);
      LogFilter(fview);
      if (fa != null) {
        pilot.Reset();
        AutoSay(fpilot.State == "failed" ? "idle" : "work");
        if (fa.Kind == AutoKind.None) return;
        int fx = o.X + (int)Math.Round(fa.X), fy = o.Y + (int)Math.Round(fa.Y);
        if (fx < o.X || fy < o.Y || fx >= o.X + cr.R || fy >= o.Y + cr.B) return;
        Send(fa, fx, fy);
        return;
      }
      // the next slot: an open filter pop-up covers the hero's slots - close it first (the filter was for the item that is
      // on now; the next one gets its own)
      if (v.Slot != null && v.Foreground && !v.UserBusy && now >= slotCloseAfter
          && Mark(o, fg.X(fg.MainCloseX, cr.R, cr.B), fg.Y(fg.CloseY, cr.R, cr.B), Ui.Unit(cr.R, cr.B), ButtonCheck.IsCloseRed)) {
        slotCloseAfter = now + 900; pilot.Reset();
        int cx = o.X + (int)Math.Round(fg.X(fg.MainCloseX, cr.R, cr.B)), cy = o.Y + (int)Math.Round(fg.Y(fg.CloseY, cr.R, cr.B));
        Send(new AutoAction { Kind = AutoKind.Click }, cx, cy);
        return;
      }
      var a = pilot.Step(v);
      AutoSay(pilot.State);
      if (a.Kind == AutoKind.None) return;
      int x = o.X + (int)Math.Round(a.X), y = o.Y + (int)Math.Round(a.Y), y2 = y + (int)Math.Round(a.DY);
      if (x < o.X || y < o.Y || x >= o.X + cr.R || y >= o.Y + cr.B) return;   // never outside the game's client area
      if (y2 < o.Y || y2 >= o.Y + cr.B) return;
      Send(a, x, y);
    }

    // Storage cleanup, in the inventory: the city's «Инвентарь» → «Снаряжение» → «Массовая продажа», then the sell
    // pilot selects the items. Only known screens are clicked; elsewhere the player is asked to open the city.
    void TickSell(W32.POINT o, W32.RECT cr, long now, bool front, bool busy, bool userClicked) {
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      // the inventory in the game's window list: searched (a full pass, ~1 s) at the start and after a click that opens it
      var inv = RFX.ReadInventory(sellScanOnce || now - sellBtnAt < 8000 ? 1500 : -1);
      sellScanOnce = false;
      if (inv != null && inv.Open) {
        if (!inv.EquipTab || !inv.Bulk) {
          SellSay(front ? "open" : "background");
          if (!front || busy || now - sellBtnAt < 2000) return;
          sellBtnAt = now;
          // the left menu's «Снаряжение» (top left); «Массовая продажа» under the list (bottom left)
          var a0 = !inv.EquipTab ? new AutoAction { Kind = AutoKind.Click, X = 0.085 * U, Y = 0.315 * U }
                                 : new AutoAction { Kind = AutoKind.Click, X = 0.5867 * U, Y = H - 0.0723 * U };
          SendInside(a0, o, cr);
          return;
        }
        int cols = front && !busy ? InvColumns(o, cr, U) : 0;
        if (cols > 0) sellCols = cols;
        if (sellGeoW != W || sellGeoH != H || sellGeoCols != sellCols) {
          sellGeoW = W; sellGeoH = H; sellGeoCols = sellCols;
          spilot.Geometry = SellGeometry.Inventory(W, U, sellCols);
        }
        var a = spilot.Step(new SellView { NowMs = now, Foreground = front, UserBusy = busy, UserClicked = userClicked, W = W, H = H, U = U,
                                           Screen = inv.AsSell(sellCols), Wanted = sellWanted, Phase = front && !busy ? SellPhase(o, cr, U) : double.NaN });
        SellSay(spilot.State);
        if (spilot.Trace != "") { Log.Write("sell: " + spilot.Trace); spilot.Trace = ""; }
        if (a.Kind != AutoKind.None) SendInside(a, o, cr);
        return;
      }
      if (!front || busy) { SellSay(front ? "paused" : "background"); return; }
      // the hero screen (the gear screen as well): back to the city with the arrow top left
      heroScreen = RFX.ReadHeroScreen(addrs, heroScreen);
      if (heroScreen.Hero > 0 && heroScreen.Tab >= 0) {
        SellSay("open");
        if (now - sellBtnAt < 2000) return;
        sellBtnAt = now;
        SendInside(new AutoAction { Kind = AutoKind.Click, X = hg2.BackX * U, Y = hg2.BackY * U }, o, cr);
        return;
      }
      // the city: «Инвентарь» in the bottom bar (found by its picture, as «Герои»)
      if (InventoryButtonSeen(o, W, H)) {
        SellSay("open");
        if (now - sellBtnAt < 2500) return;
        sellBtnAt = now;
        SendInside(new AutoAction { Kind = AutoKind.Click, X = W - InvBtnDX * U, Y = H - hg2.HeroesBtnDY * U }, o, cr);
        return;
      }
      SellSay("no_city");
    }

    const double InvBtnDX = 0.5222 + 0.9316;   // «Инвентарь»: 0.9316 left of «Герои» in the city's bottom bar
    int sellCols = 8, sellGeoCols; double sellGeoW, sellGeoH; bool sellScanOnce;

    // the rows' phase on the screen: the stat bars under the cells (client px mod the row step), NaN = unclear
    double SellPhase(W32.POINT o, W32.RECT cr, double U) {
      var sg = spilot.Geometry;
      int x0 = (int)Math.Round(sg.X(sg.X0, cr.R, U)), x1 = (int)Math.Round(sg.X(sg.X0 + (sg.Columns - 1) * sg.PitchX + sg.CellW, cr.R, U));
      int y0 = (int)Math.Round(sg.ViewTopPx(U)), y1 = (int)Math.Round(sg.ViewBottomPx(cr.B, U));
      if (x0 < 0 || x1 > cr.R || y1 - y0 < 100) return double.NaN;
      int w = x1 - x0, h = y1 - y0;
      int[] px = Grab(o.X + x0, o.Y + y0, w, h);
      if (px == null) return double.NaN;
      var xs = new int[sg.Columns * 2];
      for (int c = 1; c <= sg.Columns; c++) {
        double l = (c - 1) * sg.PitchX * U;
        xs[(c - 1) * 2] = (int)(l + sg.CellW * U * 0.12); xs[(c - 1) * 2 + 1] = (int)(l + sg.CellW * U * 0.88);
      }
      double ph, conf, pitch = sg.PitchY * U;
      if (!ListTracker.FindPhase(ListTracker.BarProfile(px, w, h, xs), pitch, sg.BarTop * U, sg.BarBottom * U, out ph, out conf)) return double.NaN;
      double c0 = (y0 + ph) % pitch; return c0 < 0 ? c0 + pitch : c0;
    }

    // the inventory's column count: the game fits cells of a fixed size into the list's width (8 on 16:9 .. 4:3); the
    // stat bars' spacing on the screen tells which count it is (0 = not clear now: keep the last)
    int InvColumns(W32.POINT o, W32.RECT cr, double U) {
      int x0 = (int)Math.Round(0.205 * U), x1 = (int)Math.Round(cr.R - 0.522 * U);
      int y0 = (int)Math.Round(0.19 * U), y1 = (int)Math.Round(cr.B - 0.17 * U);
      if (x1 - x0 < 200 || y1 - y0 < 100) return 0;
      int w = x1 - x0, h = y1 - y0;
      int[] px = Grab(o.X + x0, o.Y + y0, w, h);
      return px == null ? 0 : ListTracker.GridColumns(px, w, h);
    }

    void SellSay(string st) {
      string key = st + "|" + spilot.Selected + "|" + spilot.Missing + "|" + spilot.Extra;
      if (key == sellSaid) return;
      sellSaid = key; sellReport(spilot, st);
      Log.Write("sell: " + st + " selected " + spilot.Selected + "/" + (sellWanted != null ? sellWanted.Count : 0) + " missing " + spilot.Missing + " extra " + spilot.Extra);
    }

    // a pilot action at client px, never outside the game's client area
    void SendInside(AutoAction a, W32.POINT o, W32.RECT cr) {
      int x = o.X + (int)Math.Round(a.X), y = o.Y + (int)Math.Round(a.Y), y2 = y + (int)Math.Round(a.DY);
      if (x < o.X || y < o.Y || x >= o.X + cr.R || y >= o.Y + cr.B || y2 < o.Y || y2 >= o.Y + cr.B) return;
      Send(a, x, y);
    }

    /// <summary>Moves the cursor and clicks / drags there, off the UI thread (short pauses let the game see the pointer
    /// over the button first). A drag moves in small steps and rests before the release, so the list does not coast.</summary>
    void Send(AutoAction a, int x, int y) {
      sending = true;
      seen = new W32.POINT(x, y); seenSet = true;
      // our own window (the compact one over the game) must not catch the click: for the moment it goes under the
      // game (z-order only: hiding it made its taskbar button vanish and come back on every click, 27.09) and back on
      // top after, without taking the focus
      IntPtr self = AppWindowHandle();
      bool hidden = false, wasTop = false;
      W32.RECT wr;
      const uint NoMoveSizeActivate = 0x0002 | 0x0001 | 0x0010;   // SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
      if (self != IntPtr.Zero && W32.IsWindowVisible(self) && W32.GetWindowRect(self, out wr)) {
        int y2 = y + (a.Kind == AutoKind.Drag ? (int)Math.Round(a.DY) : 0);
        bool over = x >= wr.L && x < wr.R && Math.Max(y, y2) >= wr.T && Math.Min(y, y2) < wr.B;
        if (over) {
          wasTop = (W32.GetWindowLong(self, -20) & 0x00000008) != 0;   // GWL_EXSTYLE, WS_EX_TOPMOST
          W32.SetWindowPos(self, (IntPtr)1, 0, 0, 0, 0, NoMoveSizeActivate);   // HWND_BOTTOM
          hidden = true;
        }
      }
      var t = new System.Threading.Thread(() => {
        try {
          if (hidden) System.Threading.Thread.Sleep(120);
          W32.SetCursorPos(x, y);
          System.Threading.Thread.Sleep(60);
          if (a.Kind == AutoKind.Click) {
            Mouse(W32.LEFTDOWN, 0);
            System.Threading.Thread.Sleep(60);
            Mouse(W32.LEFTUP, 0);
          } else {
            Mouse(W32.LEFTDOWN, 0);
            System.Threading.Thread.Sleep(80);
            const int Steps = 16;
            int yEnd = y + (int)Math.Round(a.DY);
            for (int i = 1; i <= Steps; i++) { W32.SetCursorPos(x, y + (int)Math.Round(a.DY * i / Steps)); System.Threading.Thread.Sleep(20); }
            seen = new W32.POINT(x, yEnd);          // the pilot's own move, not the player's
            System.Threading.Thread.Sleep(350);     // at rest: the ScrollRect's velocity decays, no coasting after the release
            Mouse(W32.LEFTUP, 0);
          }
          System.Threading.Thread.Sleep(30);
        } catch (Exception e) { Log.Write("auto: " + e.Message); }
        finally {
          // back over the game: topmost again when it was («Поверх игры»), else just above it
          if (hidden) W32.SetWindowPos(self, wasTop ? (IntPtr)(-1) : IntPtr.Zero, 0, 0, 0, 0, NoMoveSizeActivate | 0x4000);   // SWP_ASYNCWINDOWPOS
          sending = false;
        }
      });
      t.IsBackground = true; t.Start();
    }

    static IntPtr AppWindowHandle() {
      try { return Process.GetCurrentProcess().MainWindowHandle; } catch (Exception) { return IntPtr.Zero; }
    }

    static void Mouse(uint flags, uint data) {
      var inp = new[] { new W32.INPUT { type = 0, mi = new W32.MOUSEINPUT { dwFlags = flags, mouseData = data } } };
      W32.SendInput(1, inp, Marshal.SizeOf(typeof(W32.INPUT)));
    }

    IntPtr gameWnd; int gameWndAge;
    IntPtr GameWindow() {
      if (gameWnd != IntPtr.Zero && ++gameWndAge < 30 && W32.IsWindowVisible(gameWnd)) return gameWnd;
      gameWndAge = 0; gameWnd = IntPtr.Zero;
      try {
        int pid = addrs != null ? addrs.Pid : GameInfo.FindGameProcess();   // diagnostics may run before the scan
        if (pid != 0) using (var p = Process.GetProcessById(pid)) if (!p.HasExited) gameWnd = p.MainWindowHandle;
      } catch (Exception) { }
      return gameWnd;
    }

    /// <summary>What the filter pilot needs this tick: the filter in memory, the set / stat places in the game's lists, the
    /// open panels on the screen (only looked at with the game in front).</summary>
    FilterView FilterViewNow(W32.POINT o, W32.RECT cr, long now, bool front, bool busy) {
      var fv = new FilterView { NowMs = now, Foreground = front, UserBusy = busy, W = cr.R, H = cr.B };
      if (filterItem <= 0 || addrs == null) return fv;
      long[] suits, mains, subAttrs; int part; bool foreign, hideEq;
      if (!RFX.ReadFilter(addrs, out suits, out mains, out subAttrs, out part, out foreign, out hideEq)) return fv;
      fv.Item = filterItem; fv.Suits = suits; fv.MainAttrs = mains; fv.SubAttrs = subAttrs; fv.Foreign = foreign; fv.HideEquipped = hideEq;
      // the item is not on the screen: the filter (set + main stat) brings it to the first rows at once. Scrolling a long
      // list back from wherever the last item left it took the pilot up to a minute (live test 2026-09-26)
      bool onScreen = state == "on";
      fv.Wanted = filterKind == "filter" || filterKind == "hiddenEq" || filterKind == "hiddenFilter" || filterKind == "notIn"
        || ((filterKind == "pick" || filterKind == "rel") && !onScreen);
      long[] info;
      if (itemInfo.TryGetValue(filterItem, out info) && part >= 0) {
        // weapons and chest armour have one main stat each: filtering by it narrows nothing, two clicks saved
        fv.SetId = info[0]; fv.StatId = part <= 1 ? 0 : info[1];
        long[] so, mo;
        if (!suitOrder.TryGetValue(part, out so) || so == null) suitOrder[part] = so = RFX.ReadSuitOrder(addrs, part);
        if (!statOrder.TryGetValue(part, out mo) || mo == null) statOrder[part] = mo = RFX.ReadMainAttrOrder(addrs, part);
        if (so != null && fv.SetId > 0) fv.SetIndex = Array.IndexOf(so, fv.SetId);
        fv.SetOrder = so;
        // the item's sub stats straight from the game (its live table), the filter's list of them for this slot
        long[] subo;
        if (!subOrder.TryGetValue(part, out subo) || subo == null) subOrder[part] = subo = RFX.ReadSubAttrOrder(addrs, part);
        fv.SubOrder = subo; fv.SubIds = RFX.ReadSubIds(addrs, filterItem);
        if (mo != null && fv.StatId > 0) fv.StatIndex = Array.IndexOf(mo, fv.StatId);
      }
      long owner = RFX.ReadOwner(addrs, filterItem);
      fv.OnOtherHero = owner > 0 && hintHero > 0 && owner != hintHero;
      if (front) {
        double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
        fv.PanelOpen = Mark(o, fg.X(fg.MainCloseX, W, H), fg.Y(fg.CloseY, W, H), U, ButtonCheck.IsCloseRed);
        fv.SidePanelOpen = Mark(o, fg.X(fg.SideCloseX, W, H), fg.Y(fg.CloseY, W, H), U, ButtonCheck.IsCloseRed);
        fv.SetPanelOpen = fv.SidePanelOpen && Mark(o, fg.X(fg.SetButtonsX, W, H), fg.Y(fg.SetButtonsY, W, H), U, ButtonCheck.IsGold);
      }
      return fv;
    }

    // ---------------------------------------------------------------- the city's «Герои» button

    /// <summary>The hero grid's filter pop-up is on the screen: its dark background (or a chosen row's blue) left of the class
    /// check boxes: 0.93 of the strip when open, at most 0.73 over the hero cards.</summary>
    bool HeroFilterSeen(W32.POINT o, double W, double H) {
      double U = Ui.Unit(W, H);
      int x0 = (int)(0.030 * U), x1 = (int)(0.058 * U), y0 = (int)(0.30 * U), y1 = (int)(0.87 * U);
      int[] px = Grab(o.X + x0, o.Y + y0, x1 - x0, y1 - y0);
      if (px == null) return false;
      int dark = 0;
      // its dark background, or the blue of a chosen row (a class / faction just clicked)
      foreach (int c in px) {
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        if ((r + g + b < 170 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 45) || (b >= r + 25 && b >= g + 10 && b >= 70 && r < 140)) dark++;
      }
      if (dark < 0.85 * px.Length) return false;
      // a dark strip is not enough: a filtered grid of not-owned heroes (dark empty cards) looks the same, and a click
      // on the funnel then OPENS the pop-up again (27.09: the filter kept opening and closing on Лайя). The pop-up's own
      // mark: the thin light lines between its columns (Класс | Фракция | Фильтр), measured at 1920×1009: x 258 and 488
      return DividerSeen(o, (int)(0.2557 * U), (int)(0.16 * U), (int)(0.87 * U)) && DividerSeen(o, (int)(0.4836 * U), (int)(0.16 * U), (int)(0.87 * U));
    }

    // a thin vertical line brighter than the pop-up around it, near x (a few pixels of play for rounding)
    bool DividerSeen(W32.POINT o, int x, int y0, int y1) {
      const int half = 12;
      int w = 2 * half + 1, h = y1 - y0;
      int[] px = Grab(o.X + x - half, o.Y + y0, w, h);
      if (px == null || h <= 0) return false;
      var mean = new double[w];
      for (int c = 0; c < w; c++) {
        long sum = 0; int n = 0;
        for (int r = 0; r < h; r += 3) { int v = px[r * w + c]; sum += ((v >> 16) & 255) + ((v >> 8) & 255) + (v & 255); n++; }
        mean[c] = n > 0 ? sum / (3.0 * n) : 0;
      }
      for (int c = 4; c < w - 4; c++) {
        double side = (mean[c - 4] + mean[c + 4]) / 2;
        if (mean[c] >= side + 30 && mean[c] >= mean[c - 1] && mean[c] >= mean[c + 1]) return true;
      }
      return false;
    }

    // hero base id -> class id, faction ids (app/res/hero_tags.json, built from the site's game data)
    static System.Collections.Generic.Dictionary<long, long[]> heroTags;
    static long[] HeroTags(long heroUid) {
      if (heroTags == null) {
        heroTags = new System.Collections.Generic.Dictionary<long, long[]>();
        try {
          using (var s = typeof(OverlayController).Assembly.GetManifestResourceStream("overlay/hero_tags.json"))
          using (var r = new StreamReader(s)) {
            var d = MiniJson.Parse(r.ReadToEnd()) as System.Collections.Generic.Dictionary<string, object>;
            if (d != null) foreach (var kv in d) {
              var l = kv.Value as System.Collections.Generic.List<object>; long id;
              if (l == null || !long.TryParse(kv.Key, out id)) continue;
              var a = new long[l.Count]; for (int i = 0; i < l.Count; i++) a[i] = l[i] is double ? (long)(double)l[i] : 0;
              heroTags[id] = a;
            }
          }
        } catch (Exception e) { Log.Write("hero tags: " + e.Message); }
      }
      long[] t; return heroTags.TryGetValue(heroUid / 100000, out t) && t.Length > 0 ? t : null;
    }

    // the city's bottom bar buttons, found by their picture (grey levels at the reference scale, UI unit 900): the bar is
    // anchored bottom right and wraps on narrow windows, so a button is clicked only where its picture is
    sealed class ButtonTpl {
      readonly string res; float[] g; int w, h; float[] scaled; int sw, sh; double su;
      public ButtonTpl(string res) { this.res = res; }
      bool Load() {
        if (g != null) return true;
        try {
          using (var s = typeof(OverlayController).Assembly.GetManifestResourceStream(res)) {
            if (s == null) return false;
            using (var bmp = new Bitmap(s)) {
              var t = new float[bmp.Width * bmp.Height];
              for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); t[y * bmp.Width + x] = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B; }
              w = bmp.Width; h = bmp.Height; g = t;
            }
          }
        } catch (Exception) { return false; }
        return true;
      }
      /// <summary>Is the picture at (cx, cy) (client px, its centre, within a few px)? NCC ≥ 0.75, contrast 0.6–1.6.</summary>
      public bool Seen(W32.POINT o, double W, double H, double cx, double cy) {
        if (!Load()) return false;
        double U = Ui.Unit(W, H);
        if (scaled == null || Math.Abs(su - U) > 0.5) {
          su = U; double k = U / 900.0;
          sw = Math.Max(8, (int)Math.Round(w * k)); sh = Math.Max(8, (int)Math.Round(h * k));
          scaled = new float[sw * sh];
          for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
            scaled[y * sw + x] = g[Math.Min(h - 1, (int)(y / k)) * w + Math.Min(w - 1, (int)(x / k))];
        }
        const int R = 6;
        int gx = (int)(cx - sw / 2.0) - R, gy = (int)(cy - sh / 2.0) - R, gw = sw + 2 * R, gh = sh + 2 * R;
        if (gx < 0 || gy < 0 || gx + gw > W || gy + gh > H) return false;
        int[] px = Grab(o.X + gx, o.Y + gy, gw, gh);
        if (px == null) return false;
        var grey = new float[px.Length];
        for (int i = 0; i < px.Length; i++) { int c = px[i]; grey[i] = 0.299f * ((c >> 16) & 255) + 0.587f * ((c >> 8) & 255) + 0.114f * (c & 255); }
        double tm = 0; foreach (var t in scaled) tm += t; tm /= scaled.Length;
        double tv = 0; foreach (var t in scaled) tv += (t - tm) * (t - tm);
        double best = -1, bestStd = 0, tstd = Math.Sqrt(tv / scaled.Length);
        for (int dy = 0; dy <= 2 * R; dy += 2)
          for (int dx = 0; dx <= 2 * R; dx += 2) {
            double m = 0;
            for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++) m += grey[(y + dy) * gw + x + dx];
            m /= scaled.Length;
            double num = 0, pv = 0;
            for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++) {
              double p = grey[(y + dy) * gw + x + dx] - m, t = scaled[y * sw + x] - tm;
              num += p * t; pv += p * p;
            }
            double ncc = pv > 0 && tv > 0 ? num / Math.Sqrt(pv * tv) : 0;
            if (ncc > best) { best = ncc; bestStd = Math.Sqrt(pv / scaled.Length); }
          }
        double contrast = tstd > 0 ? bestStd / tstd : 0;
        return best >= 0.75 && contrast >= 0.6 && contrast <= 1.6;
      }
    }
    static readonly ButtonTpl heroesBtn = new ButtonTpl("overlay/heroes_btn.png"), inventoryBtn = new ButtonTpl("overlay/inventory_btn.png");

    // the picture's centre (the click goes to the icon, a bit higher)
    bool HeroesButtonSeen(W32.POINT o, double W, double H) { double U = Ui.Unit(W, H); return heroesBtn.Seen(o, W, H, W - hg2.HeroesBtnDX * U, H - 0.1156 * U); }
    bool InventoryButtonSeen(W32.POINT o, double W, double H) { double U = Ui.Unit(W, H); return inventoryBtn.Seen(o, W, H, W - InvBtnDX * U, H - 0.1156 * U); }

    string heroLogged;
    /// <summary>One journal line whenever the hero pilot's view or state changes.</summary>
    void LogHero(HeroView v) {
      int i = v.Heroes != null ? Array.IndexOf(v.Heroes, v.Plan) : -2, j = v.Heroes != null ? Array.IndexOf(v.Heroes, v.Hero) : -2;
      string line = "hero: plan " + v.Plan + "@" + i + " shown " + v.Hero + "@" + j + " tab=" + v.Tab + " list=" + v.Part
        + " filter=" + (v.FilterOpen ? "open" : "closed") + " camps=" + string.Join(",", v.ChosenCamps ?? new long[0]) + " class=" + v.ClassChosen
        + " small=" + v.SmallCards + " grid=" + (v.Heroes != null ? v.Heroes.Length : -1) + " -> " + hpilot.State;
      if (line == heroLogged) return;
      heroLogged = line;
      Log.Write(line);
    }

    string filterLogged;
    /// <summary>One journal line whenever what the filter pilot knows or does changes (why the filter was or was not set).</summary>
    void LogFilter(FilterView v) {
      if (v.Item <= 0) return;
      string line = "filter: item " + v.Item + " kind=" + filterKind + " wanted=" + v.Wanted + " set=" + v.SetId + "@" + v.SetIndex
        + " stat=" + v.StatId + "@" + v.StatIndex + " subs=" + string.Join(",", v.SubIds ?? new long[0])
        + " now suits=" + string.Join(",", v.Suits ?? new long[0]) + " subs=" + string.Join(",", v.SubAttrs ?? new long[0])
        + " stats=" + string.Join(",", v.MainAttrs ?? new long[0]) + " hideEq=" + v.HideEquipped + " otherHero=" + v.OnOtherHero
        + " panel=" + v.PanelOpen + "/" + v.SidePanelOpen + "/" + v.SetPanelOpen + " -> " + fpilot.State;
      if (line == filterLogged) return;
      filterLogged = line;
      Log.Write(line);
    }

    /// <summary>A small square (about 2% of the height) around a client point: is it mostly that mark?</summary>
    bool Mark(W32.POINT o, double x, double y, double H, Func<int, bool> test) {
      int r = Math.Max(4, (int)(H * 0.01));
      return ButtonCheck.Share(Grab(o.X + (int)x - r, o.Y + (int)y - r, 2 * r, 2 * r), test) >= ButtonCheck.MinMarkShare;
    }

    /// <summary>The confirm button the game shows now: «Заменить» (two cards) or «Надеть» (one card, the slot is empty) -
    /// the spot whose middle is the game's blue button. Null when neither is (the card is still opening, another screen)
    /// or both are (unexpected: better not to press).</summary>
    int[] ConfirmButton(W32.POINT o, double W, double H) {
      int[] rep = FindBlue(o, hg.Replace(W, H), H), eq = FindBlue(o, hg.Equip(W, H), H);
      int[] btn = (rep != null) == (eq != null) ? null : rep ?? eq;
      if (btn == null && hintKind == "replace") {
        // (the journal: why «Заменить» is not pressed - once per selected item)
        if (confirmMissLogged != target) { confirmMissLogged = target; Log.Write("confirm: no single blue button (replace " + (rep != null) + ", equip " + (eq != null) + ") for item " + target); }
      } else if (btn != null && (btn[1] != hg.Replace(W, H)[1] && btn[1] != hg.Equip(W, H)[1]) && confirmFoundLogged != target) {
        confirmFoundLogged = target;
        Log.Write("confirm: button found " + (btn[1] - (rep != null ? hg.Replace(W, H)[1] : hg.Equip(W, H)[1])) + " px off its usual place, item " + target);
      }
      return btn;
    }
    long confirmMissLogged, confirmFoundLogged;

    /// <summary>The button at its usual place, or moved: a card with more lines (sub-stats, set bonuses) is taller and its
    /// button lower (27.09: an item with more extras was not put on) - searched from a bit above down to 0.14 of the
    /// height below, in steps of a third of the button.</summary>
    int[] FindBlue(W32.POINT o, int[] rc, double H) {
      if (IsBlueButton(o, rc)) return rc;
      int step = Math.Max(4, rc[3] / 3), up = (int)(0.03 * H), down = (int)(0.14 * H);
      for (int d = step; d <= down; d += step) {
        var r2 = new[] { rc[0], rc[1] + d, rc[2], rc[3] };
        if (r2[1] + r2[3] < H && IsBlueButton(o, r2)) return r2;
      }
      for (int d = step; d <= up; d += step) {
        var r2 = new[] { rc[0], rc[1] - d, rc[2], rc[3] };
        if (r2[1] > 0 && IsBlueButton(o, r2)) return r2;
      }
      return null;
    }

    bool IsBlueButton(W32.POINT o, int[] rc) {
      int x = rc[0] + rc[2] * 15 / 100, y = rc[1] + rc[3] / 5, w = rc[2] * 70 / 100, h = rc[3] * 3 / 5;
      int[] px = Grab(o.X + x, o.Y + y, w, h);
      return px != null && ButtonCheck.BlueShare(px) >= ButtonCheck.MinShare;
    }

    /// <summary>Screen pixels (ARGB, row by row) of a screen rectangle; our own windows are not in it (display affinity).</summary>
    static int[] Grab(int x, int y, int w, int h) {
      if (w < 4 || h < 4) return null;
      using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
        using (var gr = Graphics.FromImage(bmp)) gr.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[w * h];
        try {
          if (data.Stride == w * 4) Marshal.Copy(data.Scan0, px, 0, px.Length);
          else for (int r = 0; r < h; r++) Marshal.Copy(data.Scan0 + r * data.Stride, px, r * w, w);
        } finally { bmp.UnlockBits(data); }
        return px;
      }
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
      int[] px = Grab(x, y, w, h);
      {
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
