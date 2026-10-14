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
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
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
      Text = clickable ? "Wardsage cancel" : "Wardsage highlight";
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get {
        var cp = base.CreateParams;
        // LAYERED | TRANSPARENT (clicks go through) | TOOLWINDOW (no taskbar / Alt+Tab) | NOACTIVATE | TOPMOST
        cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | 0x00000008;
        if (!clickable && !movable) cp.ExStyle |= 0x00000020;
        return cp;
      }
    }
    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      try { W32.SetWindowDisplayAffinity(Handle, 0x11); } catch (Exception) { }   // keep it out of our own screenshots
    }
    // a panel the player may move (the boss coach): while it is movable it takes the mouse and drags like a title bar
    bool movable;
    /// <summary>The player is dragging it now.</summary>
    public bool Dragging { get; private set; }
    /// <summary>A drag ended (the window is at its new place).</summary>
    public event Action Moved;
    /// <summary>A right click on a movable window: back to the default place.</summary>
    public event Action ResetPlace;
    /// <summary>Takes the mouse (to be dragged) instead of letting the clicks through.</summary>
    public bool Movable {
      get { return movable; }
      set {
        if (movable == value || clickable) return;
        movable = value;
        if (IsHandleCreated) { int ex = W32.GetWindowLong(Handle, -20); W32.SetWindowLong(Handle, -20, value ? ex & ~0x20 : ex | 0x20); }   // GWL_EXSTYLE, WS_EX_TRANSPARENT
      }
    }
    protected override void WndProc(ref Message m) {
      if (movable) {
        if (m.Msg == 0x0084) { m.Result = (IntPtr)2; return; }                 // WM_NCHITTEST -> HTCAPTION: Windows drags it
        if (m.Msg == 0x0021) { m.Result = (IntPtr)3; return; }                 // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        if (m.Msg == 0x00A5) { var r = ResetPlace; if (r != null) r(); return; }   // WM_NCRBUTTONUP
        if (m.Msg == 0x00A4 || m.Msg == 0x00A6 || m.Msg == 0x007B) return;      // no system menu on a right click
      }
      if (m.Msg == 0x0231) Dragging = true;                                     // WM_ENTERSIZEMOVE
      if (m.Msg == 0x0232) { Dragging = false; var mv = Moved; if (mv != null) mv(); }   // WM_EXITSIZEMOVE
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
    readonly Timer lookTimer = new Timer();   // quick looks at the gear list between the ticks (LookFast)
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
    readonly ScrollFollow follow = new ScrollFollow();
    string anchorSrc = "";   // what set the anchor: top (the list opened at the top) / click (the player's click)
    bool filterOpen;          // the game's gear filter pop-up is over the list this tick (its red ×)
    /// <summary>The text of the tip at the filter's × (in the interface language, from the page).</summary>
    public string CloseFilterText = "";
    readonly Action<string, bool> report;
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
    public bool AutoEnabled;
    /// <summary>Settings → «Автоматически подтверждать замену»: the pilot may also press «Заменить».</summary>
    public bool AutoConfirm;
    /// <summary>A build being put on or a sell run going: no restart for an update now.</summary>
    public bool Busy { get { return runHero != 0 || target != 0 || sellWanted != null || sending; } }
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

    /// <summary>A failure worth an error report (kind, message): the filter pilot gave up, «Заменить» not found with the
    /// auto-confirm on, an exception in the tick. HostBridge sends it when the player turned the reports on.</summary>
    public event Action<string, string> Problem;
    void RaiseProblem(string kind, string message) { var p = Problem; if (p != null) try { p(kind, message); } catch (Exception) { } }
    bool filterFailSaid; string tickError;

    public OverlayController(Action<string, bool> report, Action<string> autoReport, Action<SellPilot, string> sellReport) {
      this.report = report; this.autoReport = autoReport; this.sellReport = sellReport;
      spilot = new SellPilot(SellGeometry.Inventory(1920, 1009, 8));
      pilot = new AutoPilot(g);
      fpilot = new FilterPilot(fg);
      hpilot = new HeroPilot(heroG);
      timer.Interval = 100; timer.Tick += (s, e) => {
        try { Tick(); tickError = null; }
        catch (Exception ex) {
          // the same exception every 100 ms: journaled (with its stack) and reported once
          string m = ex.GetType().Name + ": " + ex.Message;
          if (m != tickError) { tickError = m; Log.Write("overlay: " + ex); RaiseProblem("overlay.exception", "overlay: " + m); }
          Hide("off");
        }
      };
      lookTimer.Interval = 30; lookTimer.Tick += (s, e) => {
        try { LookFast(); }
        catch (Exception ex) { string m = "look: " + ex.GetType().Name + ": " + ex.Message; if (m != tickError) { tickError = m; Log.Write("overlay: " + ex); } }
      };
      cancelWin.Pressed += () => { cancelWin.Hide(); var c = CancelPressed; if (c != null) c(); };
    }

    public void Dispose() { timer.Dispose(); lookTimer.Dispose(); glow.Dispose(); tip.Dispose(); cancelWin.Dispose(); }

    public void StartDiag(int minutes) {
      // one folder per run: a second run must not overwrite the first one's files
      diagDir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "debug", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
      Directory.CreateDirectory(diagDir);
      diagUntil = DateTime.Now.AddMinutes(minutes); diagN = 0; shotN = 0; diagLast = null;
      DiagLog("start " + DateTime.Now.ToString("s"));
      timer.Start();
    }

    void DiagLog(string line) { try { File.AppendAllText(Path.Combine(diagDir, "frame.log"), line + "\r\n"); } catch (Exception) { } }

    // every 5th tick: a log line and the panel's script data when it changed (≤ 60 files); every 10th: a screenshot (≤ 60),
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
          using (var gr = Graphics.FromImage(bmp)) {
            gr.CopyFromScreen(o.X, o.Y, 0, 0, new Size(cr.R, cr.B), CopyPixelOperation.SourceCopy);
            DiagMarks(gr);   // (the screen copy does not show our own windows: where they would be is drawn in)
          }
          bmp.Save(Path.Combine(diagDir, "screen_" + shotN.ToString("00") + ".png"), ImageFormat.Png);
        }
        DiagLog("  screen_" + shotN.ToString("00") + ".png");
      } catch (Exception e) { DiagLog("  shot failed: " + e.Message); }
    }

    // on a diagnostics screenshot: the frame (or the arrow's spot) the tick computed in red, and the rows the anchor puts in
    // the visible list (a tick and the row number at every row top), with the decision on top
    void DiagMarks(Graphics gr) {
      using (var red = new Pen(Color.FromArgb(255, 255, 40, 40), 2f))
      using (var f = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
      using (var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
      using (var txt = new SolidBrush(Color.FromArgb(255, 255, 90, 90))) {
        if (dKind.StartsWith("box")) gr.DrawRectangle(red, dX, dY, Math.Max(2, dW), Math.Max(2, dH));
        else if (dKind.StartsWith("up") || dKind.StartsWith("down")) {
          gr.DrawLine(red, dX, dY, dX + dW, dY);
          float cx = dX + dW / 2f, d = dKind.StartsWith("up") ? 10 : -10;
          gr.DrawLine(red, cx, dY + d, cx - 8, dY + 2 * d); gr.DrawLine(red, cx, dY + d, cx + 8, dY + 2 * d);
        }
        string vis = "";
        double H = dU;
        if (H > 0 && anchor.Has && types != null) {
          double vt = g.ViewTop * H, vb = g.ViewBottom * H, x0 = g.StripLeft * H, x1 = g.StripRight * H;
          int first = 0, last = 0;
          for (int r = 1; r < types.Length; r++) {
            double t = anchor.RowTop(g, types, r, H);
            if (t < vt - g.PitchY * H || t > vb) continue;
            if (t + g.CellW * H / 2 >= vt && t + g.CellW * H / 2 <= vb && (types[r] == 1 || types[r] == 0)) { if (first == 0) first = r; last = r; }
            if (t < vt || t > vb) continue;
            gr.DrawLine(red, (float)x0 - 2, (float)t, (float)x1 + 2, (float)t);
            gr.DrawString(r.ToString(), f, txt, (float)x1 + 3, (float)t);
          }
          vis = " rows " + first + "-" + last + (follow.Unsure ? " (unsure)" : "");
        }
        string line = dKind + vis + " src=" + anchorSrc + " follow=" + follow.Note + (filterOpen ? " filter open" : "");
        var sz = gr.MeasureString(line, f);
        gr.FillRectangle(bg, 2, 2, sz.Width + 6, sz.Height + 2);
        gr.DrawString(line, f, txt, 5, 3);
      }
    }

    public void SetAddrs(EquipAddrs a) { if (a != addrs) { addrs = a; anchor.Has = false; follow.Reset(); types = null; lastSel = -1; suitOrder.Clear(); statOrder.Clear(); subOrder.Clear(); } }

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

    /// <summary>«Найти в игре»: show where this item is (null = stop). Hints only: the frame in the inventory's list and a
    /// text plate; nothing is clicked. While it runs the build's frame is not shown (one target at a time).</summary>
    public void SetLocate(LocateRequest r) {
      locateReq = r != null && r.Item != null ? r : null;
      locateStep = null; locateSubs = null; locateKey = ""; locateReadAt = 0; locateLastCur = -1; locateSubsAt = 0; lgrid.Reset();
      Hide("off"); HideTip();
      Run();
    }

    /// <summary>The text plate for the current step (composed by the page in the interface language).</summary>
    public void SetLocateTip(string[] lines) { locateLines = lines ?? new string[0]; HideTip(); }

    void Run() {
      if (target != 0 || locateReq != null || filterItem != 0 || hintKind != "" || runHero != 0 || sellWanted != null || DateTime.Now < diagUntil) { timer.Start(); if (target != 0) lookTimer.Start(); else lookTimer.Stop(); return; }
      timer.Stop(); lookTimer.Stop(); HideTip(); if (cancelWin.Visible) cancelWin.Hide();
    }

    void HideTip() { if (tip.Visible) tip.Hide(); tipKey = null; }

    void Hide(string st) {
      if (glow.Visible) glow.Hide();
      drawnKey = null;
      dKind = "hidden:" + st;
      Say(st);
    }

    // the frame's state for the page, and for «above» / «below» whether the item is far (more than three screens of the list
    // to scroll: the game's filter is quicker then)
    bool saidFar;
    void Say(string st, int rows = 0) {
      bool far = rows > 3 * g.VisibleRows;
      if (far && target != 0) farFor = target;
      if (st == state && far == saidFar) return;
      state = st; saidFar = far;
      report(st, far);
    }

    void Tick() {
      curRow = curCol = 0; curSel = 0;
      if (locateReq != null) { TickLocate(); TickCancel(); return; }
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

    // what the frame tick decided (diagnostics): box / up / down / hidden:<state>, the frame or arrow spot in client px, the
    // row it took the bar phase from, the UI unit
    string dKind = ""; int dX, dY, dW, dH, dRefRow; double dU;

    void TickFrame() {
      dKind = ""; dX = dY = dW = dH = 0; dRefRow = 0; dU = 0;
      FrameStep();
      if (DateTime.Now < diagUntil) {   // also before the gear screen was scanned: the filter panel is often opened first
        IntPtr dh = GameWindow(); var dp = new W32.POINT(0, 0); W32.RECT dr;
        if (dh != IntPtr.Zero && W32.GetClientRect(dh, out dr)) {
          W32.ClientToScreen(dh, ref dp);
          diagInfo = (dKind == "" ? "-" : dKind) + " rect=" + dX + "," + dY + "," + dW + "x" + dH + " anchor=" + anchor.Has
            + " src=" + (anchorSrc == "" ? "-" : anchorSrc) + " follow=" + (follow.Note == "" ? "-" : follow.Note)
            + (follow.Note == "still" || follow.Note == "track" || follow.Note == "top" || follow.Note == "lost"
               ? " shift=" + Num(follow.Shift) + " pred=" + Num(follow.Pred) + " diff=" + Num(follow.Diff) + "/" + Num(follow.Second) : "")
            + " refRow=" + dRefRow + " filter=" + (filterOpen ? "open" : "closed") + " | " + diagInfo;
          DiagTick(dh, dp, dr, diagInfo);
        }
      }
    }

    static string Num(double v) { return v >= 1e9 ? "-" : v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); }

    void FrameStep() {
      diagInfo = "";
      TickHint();
      if (target == 0 || addrs == null) { Hide("off"); return; }
      int col; ulong list;
      int row = RFX.RowOfUid(addrs, target, out col, out list);
      if (row <= 0 || col <= 0 || list == 0) { Hide("off"); return; }
      curRow = row; curCol = col; frameRow = row; frameCol = col;

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
        anchor.Has = false; follow.Reset(); anchorSrc = ""; rebuiltMs = 0;
      }
      dU = H;

      // the player clicked an item: the cursor shows where its row is on the screen
      long sel = RFX.ReadSel(addrs);
      selRow = selCol = 0;
      if (sel > 0) {
        int scol; ulong sl; int srow = RFX.RowOfUid(addrs, sel, out scol, out sl);
        if (srow > 0 && sl == list) { selRow = srow; selCol = scol; }
      }
      if (sel != lastSel) {
        if (lastSel != -1 && sel > 0 && selRow > 0) {
          W32.POINT c;
          if (W32.GetCursorPos(out c)) {
            double cx = (c.X - o.X) / H, cy = (c.Y - o.Y) / H;
            if (cy >= g.ViewTop && cy <= g.ViewBottom && g.ColumnAt(cx) == selCol) {
              anchor.FromClick(g, types, selRow, c.Y - o.Y, H, list);
              follow.Anchored(null, NowMs());   // the next strip picture is taken as it is
              anchorSrc = "click";
            }
          }
        }
        lastSel = sel;
      }
      curSel = sel;
      diagInfo = "H=" + H.ToString("0.0") + " target=" + row + "/" + col + " sel=" + sel + " state=" + state;
      lookOk = false;
      if (!IsForeground(hwnd)) { Hide(anchor.Has ? "background" : "need_click"); return; }
      // the game's filter pop-up is over the list: a frame at the item's spot would sit on the pop-up. The list is not
      // followed meanwhile (the pop-up hides part of it); the last sure picture waits until it closes
      if (filterState != "") { Hide(filterState); return; }

      bool sure = LookAtList(o, H, NowMs());
      diagInfo += " phase=" + (lookPhase ? lookPhaseY.ToString("0.0") : "none") + " conf=" + lookConf.ToString("0.00") + (double.IsNaN(lookScroll) ? " scroll=none" : " scroll=" + lookScroll.ToString("0.0") + (lookCheck != "" ? " check=" + lookCheck : "")) + " row1=" + anchor.Row1Top.ToString("0.0");
      // (between these ticks the list is looked at more often: LookFast)
      lookO = o; lookH = H; lookList = list; lookTarget = target; lookOk = true;
      if (!anchor.Has || !sure) { Hide("need_click"); return; }
      PlaceFrame(o, H);
    }

    // the last frame tick's game window (for the quick looks between ticks), the list it saw
    W32.POINT lookO; double lookH; ulong lookList; long lookTarget; bool lookOk;
    // this look: the bars' phase (client px), how clear it was
    bool lookPhase; double lookPhaseY, lookConf, lookScroll = double.NaN; string lookCheck = "";
    long rebuiltMs;
    int selRow, selCol;   // the selected item's place in the list (0 = none / another list)

    /// <summary>The frame (or the arrow) for the target at the anchor's rows.</summary>
    void PlaceFrame(W32.POINT o, double H) {
      int row = curRow > 0 ? curRow : frameRow, col = curCol > 0 ? curCol : frameCol;
      if (row <= 0 || col <= 0) return;
      frameRow = row; frameCol = col;
      double pitch = g.PitchY * H;
      int sy = (int)(g.ViewTop * H), sh = (int)((g.ViewBottom - g.ViewTop) * H);
      double top = anchor.RowTop(g, types, row, H), left = g.ColLeft(col) * H;
      double cell = g.CellW * H, full = g.BarBottom * H, mid = top + cell / 2;
      frame++;
      if (mid < g.ViewTop * H) Draw("up", o.X + (int)left, o.Y + sy, (int)cell, 0, (int)Math.Ceiling((g.ViewTop * H - top) / pitch));
      else if (mid > g.ViewBottom * H) Draw("down", o.X + (int)left, o.Y + sy + sh, (int)cell, 0, (int)Math.Ceiling((top + full - g.ViewBottom * H) / pitch));
      else Draw("box", o.X + (int)left, o.Y + (int)top, (int)cell, (int)full, 0);
      dX -= o.X; dY -= o.Y;   // (Draw keeps screen px)
    }
    int frameRow, frameCol;

    // A quick look between the frame ticks (every 30 ms): a list flicked by the player moves up to a few thousand px a
    // second - in 100 ms further than the strip is high, and then its picture fits nowhere or somewhere wrong.
    void LookFast() {
      if (locateReq != null || target == 0 || target != lookTarget || addrs == null || types == null || !lookOk || !anchor.Has || filterState != "" || typesPtr != lookList) return;
      IntPtr hwnd = GameWindow();
      if (hwnd == IntPtr.Zero || !IsForeground(hwnd)) return;
      bool sure = LookAtList(lookO, lookH, NowMs());
      if (sure) PlaceFrame(lookO, lookH); else Hide("need_click");
    }

    /// <summary>One look at the list strip: the bars' phase, its picture, and what says where the rows are - the list just
    /// built at its top, the move since the last look, the selected item's glow, an end of the list. Moves the anchor; true =
    /// sure of it.</summary>
    bool LookAtList(W32.POINT o, double H, long now) {
      const int M = 8;   // the strip with the gaps left and right of it (the selected item's glow)
      int sx = (int)(g.StripLeft * H), sy = (int)(g.ViewTop * H), sw = (int)((g.StripRight - g.StripLeft) * H), sh = (int)((g.ViewBottom - g.ViewTop) * H);
      double pitch = g.PitchY * H;
      lookPhase = false; lookConf = 0;
      if (sw < 20 || sh < 40) return false;
      int w = sw + 2 * M;
      int[] px = Grab(o.X + sx - M, o.Y + sy, w, sh);
      if (px == null) return false;
      var xs = new int[ListGeometry.Columns * 2];
      for (int c = 1; c <= ListGeometry.Columns; c++) {
        double l = (g.ColLeft(c) - g.StripLeft) * H + M;
        xs[(c - 1) * 2] = (int)(l + g.CellW * H * 0.12); xs[(c - 1) * 2 + 1] = (int)(l + g.CellW * H * 0.88);
      }
      float[] sig = ListTracker.LineSig(px, w, sh, xs, SigSegs);
      double ph, conf;
      bool hasPhase = ListTracker.FindPhase(ListTracker.BarProfile(px, w, sh, xs), pitch, g.BarTop * H, g.BarBottom * H, out ph, out conf);
      lookPhase = hasPhase; lookPhaseY = sy + ph; lookConf = conf;
      // where the game says its list is scrolled: exact, whatever the pixels show (they only put it on the pixel)
      double scrollY;
      lookScroll = double.NaN; lookCheck = "";
      if (RFX.ReadScrollY(addrs, false, out scrollY)) {
        lookScroll = scrollY;
        double r1 = ScrollAnchor.TopRow1(g, H) - scrollY * H / 675.0;
        if (hasPhase) { double s = ListTracker.Snap(r1 - sy, ph, pitch) + sy; if (Math.Abs(s - r1) <= 0.1 * pitch) r1 = s; }
        anchor.Row1Top = r1; anchor.Has = true; anchor.ListPtr = typesPtr;
        follow.Anchored(sig, now); follow.Note = "scroll"; anchorSrc = "scroll"; dRefRow = 0;
        // (diagnostics: what the pixels say - the selected item's glow, an end of the list - against it, in rows)
        double cev; string evs;
        lookCheck = hasPhase && Evidence(px, w, sh, M, H, ph, out cev, out evs) ? evs + ":" + Math.Round((cev - r1) / pitch).ToString("+0;-0;0") : "";
        return true;
      }
      // a list just built opens at its top - mostly (the game may keep the last list's scroll): only a tight match, in the
      // first moments it is looked at (it may have been built under the filter pop-up)
      if (rebuiltMs == 0) rebuiltMs = now;
      if (!anchor.Has && hasPhase && now - rebuiltMs < 1500 && anchor.FromTop(g, types, sy + ph, H, typesPtr)) {
        follow.Reset(); follow.Anchored(sig, now); anchorSrc = "top";
      }
      bool sure = anchor.Has && follow.Follow(anchor, sig, SigN, pitch, now);
      // the selected item's glow or an end of the list say where the rows are, whatever the following thinks
      double ev; string src;
      if (hasPhase && Evidence(px, w, sh, M, H, ph, out ev, out src)) {
        if (!anchor.Has || !sure || Math.Abs(ev - anchor.Row1Top) > pitch / 2) {
          if (anchor.Has && sure) Log.Write("frame: " + src + " moves the anchor by " + (ev - anchor.Row1Top).ToString("0") + " px");
          anchor.Row1Top = ev; anchor.Has = true; anchor.ListPtr = typesPtr;
          follow.Anchored(sig, now); follow.Note = src; anchorSrc = src; sure = true;
        }
      }
      if (sure && hasPhase) {
        int refRow = anchor.VisibleRow(g, types, H);
        if (refRow > 0) anchor.Track(g, types, refRow, sy + ph, H);
        dRefRow = refRow;
      }
      // where the list can be: from its top to its end (pulled a little past them for a moment)
      if (sure) {
        double topRow1 = ScrollAnchor.TopRow1(g, H), endRow1 = Math.Min(topRow1, g.ViewBottom * H - ListGeometry.TopPad * H - g.RowOffset(types, types.Length) * H);
        double slack = 0.5 * (g.ViewBottom - g.ViewTop) * H;
        if (anchor.Row1Top > topRow1 + slack || anchor.Row1Top < endRow1 - slack) { follow.Unsure = true; follow.LostMs = now - ScrollFollow.RetryMs - 1; follow.Note = "bounds"; sure = false; }
      }
      if (sure) follow.Settled(anchor, sig, ScrollAnchor.TopRow1(g, H), pitch);
      return sure;
    }

    /// <summary>Where row 1 is (client px) by what the strip shows on its own: the selected item's glow (its row is known),
    /// or an end of the list (the slots past the last row or before the first one empty). ph: the bars' phase (strip px).</summary>
    bool Evidence(int[] px, int w, int h, int M, double H, double ph, out double row1, out string src) {
      row1 = 0; src = "";
      double pitch = g.PitchY * H, sy = (int)(g.ViewTop * H);
      if (selRow > 0 && selCol > 0 && selRow < types.Length) {
        int lx = (int)((g.ColLeft(selCol) - g.StripLeft) * H) + M, rx = lx + (int)(g.CellW * H);
        int y0, y1;
        if (ListTracker.GlowRun(px, w, h, lx, rx, g.BarBottom * H * 0.6, out y0, out y1)) {
          // the glow reaches about 1.2 % of the height above and below the cell and its bar
          double t = y0 > 0 ? y0 + 0.012 * H : y1 - g.BarBottom * H - 0.012 * H, s = ListTracker.Snap(t, ph, pitch);
          if (Math.Abs(s - t) <= pitch * 0.3) { row1 = sy + s - g.RowOffset(types, selRow) * H; src = "selected"; return true; }
        }
      }
      // the ends: only for a list of item rows (section titles have another height and no cells)
      for (int r = 1; r < types.Length; r++) if (types[r] != 1 && types[r] != 0) return false;
      if (types.Length < 2) return false;
      double[] tops; double top;
      int[] cells = ListTracker.SlotCells(px, w, h, M + (int)(g.CellW * H * 0.1), M + (int)(g.CellW * H * 0.9), ph, pitch, g.CellW * H, out tops);
      int e = ListTracker.ListEdge(cells, tops, out top);
      if (e == 1) { row1 = sy + top - g.RowOffset(types, types.Length - 1) * H; src = "end"; return true; }
      if (e == -1) { row1 = sy + top; src = "start"; return true; }
      return false;
    }

    void TickHint() {
      confirmBtn = null; filterOpen = false; filterState = "";
      if ((hintKind == "" && target == 0) || addrs == null) { HideTip(); return; }
      IntPtr hwnd = GameWindow();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || !IsForeground(hwnd)) { HideTip(); return; }
      W32.RECT cr; if (!W32.GetClientRect(hwnd, out cr) || cr.B < 200) { HideTip(); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double H = cr.B, W = cr.R, U = Ui.Unit(W, H);
      if (hintKind == "hero") { TickHeroHint(o, W, H); return; }
      // the gear filter pop-up (its red ×) while an item is to be framed
      if (target != 0) {
        double fx = fg.X(fg.MainCloseX, W, H), fy = fg.Y(fg.CloseY, W, H);
        filterOpen = Mark(o, fx, fy, U, ButtonCheck.IsCloseRed);
        if (filterOpen) {
          bool pilot = AutoEnabled && fpilot.State == "work";
          // the plan wants the filter (its filter step, or the item far down - the page's «faster» tip): what to pick in it
          // is lit; once it is set the frame goes on in the list beside the pop-up (its × would drop the filter again)
          // (far: reported by the frame, or the item's row alone - the frame may not know where the list is yet)
          bool far = (farFor != 0 && farFor == target) || frameRow > 3 * g.VisibleRows;
          int[] gr = null; string[] gl = null; int guide = hintKind == "filter" || far ? FilterGuideTip(o, cr, hintKind == "filter", out gr, out gl) : -1;
          if (guide >= 0) {
            if (pilot) { filterState = "filter_guide"; HideTip(); return; }
            if (guide == 1) {
              filterState = "filter_guide";
              DrawTip(o.X + gr[0], o.Y + gr[1], gr[2], gr[3], gl, H, false, gr[1] < 0.3 * H, gr[0] > W / 2);
              return;
            }
            filterOpen = false;   // set as wanted: the list beside the pop-up is followed and framed as usual
          } else if (!pilot) {
            // a filter the plan does not need over the list: the tip points at its ×
            filterState = "filter_open";
            int rr = Math.Max(8, (int)(0.028 * U));
            DrawTip(o.X + (int)fx - rr, o.Y + (int)fy - rr, 2 * rr, 2 * rr, string.IsNullOrEmpty(CloseFilterText) ? new string[0] : new[] { CloseFilterText }, H, false, true, true);
            return;
          } else filterState = "filter_open";
        }
      }
      if (hintKind == "") { HideTip(); return; }
      if (hintKind == "replace") confirmBtn = ConfirmButton(o, W, H);
      int[] r = hintKind == "filter" ? hg.Filter(W, H) : hintKind == "replace" ? confirmBtn ?? hg.Replace(W, H) : hintKind == "slot" ? hg.Slot(hintSlot, W, H) : null;
      // the hero is open on another tab: its «Снаряжение» tab (right edge)
      if (hintKind == "geartab") { int s = (int)(0.1 * U); r = new[] { (int)(W - hg2.GearTabDX * U) - s / 2, (int)(hg2.GearTabY * U) - s / 2, s, s }; }
      if (r == null) HideTip(); else DrawTip(o.X + r[0], o.Y + r[1], r[2], r[3], hintLines, H, false, false, hintKind == "geartab");
    }

    // the frame's state while the filter pop-up is open: "" (none, or set as wanted), filter_open (not needed: close it),
    // filter_guide (the plan wants it: what to pick is lit)
    string filterState = "";
    // the item the frame said was far down (more than three screens): the page offers the filter for it
    long farFor;
    /// <summary>The page's texts for the filter guide: the item's set and its main stat («Фильтр → Комплект: …»).</summary>
    public string FilterSetText = "", FilterStatText = "";
    string guideSide = "";   // the side panel the guide pointed at last ("set" / "stat")

    /// <summary>Where in the open filter pop-up the next pick is (client px rect) and its text; false = nothing to pick (the
    /// filter has the item's set / main stat - both for the plan's filter step, either for the «faster» tip - or they are
    /// not known).</summary>
    int FilterGuideTip(W32.POINT o, W32.RECT cr, bool both, out int[] rect, out string[] lines) {
      rect = null; lines = new string[0];
      var fv = FilterViewNow(o, cr, NowMs(), true, false);
      if (fv.Item <= 0 || fv.Suits == null || fv.MainAttrs == null) return -1;
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      int setCount = fv.SetOrder != null ? fv.SetOrder.Length : 2 * FilterGeometry.SetRows;
      bool setWanted = fv.SetId > 0 && fv.SetIndex >= 0 && fv.SetIndex < setCount;
      bool statWanted = fv.StatId > 0 && fv.StatIndex >= 0 && fv.StatIndex < 2 * FilterGeometry.StatRows;
      bool setOn = fv.SetId > 0 && Array.IndexOf(fv.Suits, fv.SetId) >= 0, statOn = fv.StatId > 0 && Array.IndexOf(fv.MainAttrs, fv.StatId) >= 0;
      if (!setWanted && !statWanted) return -1;   // nothing known to pick: the filter does not help
      bool done = both ? (setOn || !setWanted) && (statOn || !statWanted) : setOn || statOn;
      if (done) { guideSide = ""; return 0; }
      int s = Math.Max(10, (int)(0.03 * U)), cw = (int)(0.2 * U), ch = (int)(0.055 * U);
      Func<double, double, int[]> spot = (x, y) => new[] { (int)x - s, (int)y - s, 2 * s, 2 * s };
      Func<double[], int[]> cell = (c) => new[] { (int)c[0] - cw / 2, (int)c[1] - ch / 2, cw, ch };
      if (setWanted && !setOn) {
        lines = string.IsNullOrEmpty(FilterSetText) ? new string[0] : new[] { FilterSetText };
        if (fv.SetPanelOpen) {
          // the sets list shows 10 rows; further down it has to be scrolled (where it is scrolled is not known: the row is
          // lit as if it were not, the text names the set)
          int row = Math.Min(fv.SetIndex / 2, FilterGeometry.SetRows - 1);
          rect = cell(fg.SetCell(row * 2 + fv.SetIndex % 2, W, H)); guideSide = "set";
        } else rect = spot(fg.X(fg.SetArrowX, W, H), fg.Y(fg.SetArrowY, W, H));
        return 1;
      }
      lines = string.IsNullOrEmpty(FilterStatText) ? new string[0] : new[] { FilterStatText };
      // the main stats' side panel looks like the sub stats' one: its cell is lit only after the guide pointed at its arrow
      if (fv.SidePanelOpen && !fv.SetPanelOpen && guideSide == "stat") rect = cell(fg.StatCell(fv.StatIndex, W, H));
      else { rect = spot(fg.X(fg.StatArrowX, W, H), fg.Y(fg.StatArrowY, W, H)); guideSide = "stat"; }
      return 1;
    }

    // ---------------------------------------------------------------- the plan's hero on the hero screen (no auto-pilot)

    /// <summary>The page's texts for the hero hint: pick (the frame on the hero's card), find (where the list is is not known
    /// yet), up, down (the hero above / below the visible list), the city's «Герои», the back arrow.</summary>
    public string[] HeroTips = new string[0];
    readonly ScrollAnchor heroAnchor = new ScrollAnchor();
    readonly ScrollFollow heroFollow = new ScrollFollow();
    long heroReadAt, heroShownWas = -1, heroFreshMs; ulong heroGridWas; bool heroSmallWas;

    string[] HeroTip(int i) { return HeroTips != null && i < HeroTips.Length && !string.IsNullOrEmpty(HeroTips[i]) ? new[] { HeroTips[i] } : new string[0]; }

    /// <summary>Points at the plan's hero: the city's «Герои», the back arrow over another hero's gear list, the hero's card
    /// in the hero list (where the list is scrolled is found like in the gear list: a fresh list at its top, a click on a
    /// hero, the cards' row phase and the list's picture between looks).</summary>
    void TickHeroHint(W32.POINT o, double W, double H) {
      if (AutoEnabled && runHero > 0) { HideTip(); return; }   // the auto-pilot brings the hero up itself
      long now = NowMs();
      if (heroScreen == null || now - heroReadAt >= 250 || now < heroReadAt) { heroScreen = RFX.ReadHeroScreen(addrs, heroScreen); heroReadAt = now; }
      var hs = heroScreen; double U = Ui.Unit(W, H);
      if (hs == null || hs.Hero == 0) {
        heroShownWas = 0; heroAnchor.Has = false;
        if (HeroesButtonSeen(o, W, H)) {
          int s = (int)(0.1 * U);
          DrawTip(o.X + (int)(W - hg2.HeroesBtnDX * U) - s / 2, o.Y + (int)(H - 0.1156 * U) - s / 2, s, s, HeroTip(4), H, false, false, true);
        } else HideTip();
        return;
      }
      if (hs.Hero == hintHero || hs.Heroes == null) { heroShownWas = hs.Hero; HideTip(); return; }
      if (hs.Part >= 0) {   // another hero's gear list covers the hero list: back
        heroShownWas = hs.Hero; heroAnchor.Has = false;
        int s = (int)(0.07 * U);
        DrawTip(o.X + (int)(hg2.BackX * U) - s / 2, o.Y + (int)(hg2.BackY * U) - s / 2, s, s, HeroTip(5), H, false, true);
        return;
      }
      int t = Array.IndexOf(hs.Heroes, hintHero);
      if (t < 0) { heroShownWas = hs.Hero; HideTip(); return; }
      double pitch = (hs.SmallCards ? hg2.SmallPitchY : hg2.PitchY) * U, card = (hs.SmallCards ? hg2.SmallCardH : hg2.CardH) * U;
      int vt = (int)(hg2.ViewTop * U), vb = (int)(H - (1 - hg2.ViewBottom) * U);
      int x0 = (int)((hg2.Col1X - 0.06) * U), x1 = (int)((hg2.Col1X + 2 * hg2.PitchX + 0.06) * U), w = x1 - x0, h = vb - vt;
      int[] px = h > 40 && w > 20 ? Grab(o.X + x0, o.Y + vt, w, h) : null;
      if (px == null) { HideTip(); return; }
      var xs = new int[6];
      for (int c = 0; c < 3; c++) { double cx = hg2.ColX(c) * U - x0; xs[2 * c] = (int)(cx - 0.045 * U); xs[2 * c + 1] = (int)(cx + 0.045 * U); }
      var prof = new double[h];
      for (int y = 0; y < h; y++) {
        double sum = 0; int n = 0;
        for (int k = 0; k < 6; k += 2) for (int x = Math.Max(0, xs[k]); x < Math.Min(w, xs[k + 1]); x++) { int p = px[y * w + x]; sum += ((p >> 16) & 255) + ((p >> 8) & 255) + (p & 255); n++; }
        prof[y] = n > 0 ? sum / (3.0 * n) : 0;
      }
      double ph, conf;
      bool hasPhase = ListTracker.CardPhase(prof, pitch, card, out ph, out conf);
      float[] sig = ListTracker.LineSig(px, w, h, xs, SigSegs);
      double top0 = hg2.Row1Top * U;
      int rowsN = (hs.Heroes.Length + HeroGeometry.Columns - 1) / HeroGeometry.Columns;
      // sorted / filtered again, other cards, or the hero screen just opened: the game shows the list from its top
      bool opened = heroShownWas == 0;
      if (hs.GridPtr != heroGridWas || hs.SmallCards != heroSmallWas || opened) { heroAnchor.Has = false; heroFollow.Reset(); heroFreshMs = now; }
      bool freshOk = (hs.GridPtr != heroGridWas && heroGridWas != 0) || (opened && hs.Heroes.Length > 0 && hs.Hero == hs.Heroes[0]);
      if (hs.GridPtr != heroGridWas || hs.SmallCards != heroSmallWas || opened) heroFreshTop = freshOk;
      heroGridWas = hs.GridPtr; heroSmallWas = hs.SmallCards;
      // the player clicked a hero: the cursor was on its card (its row's top between the cursor and a card above it)
      if (heroShownWas > 0 && hs.Hero != heroShownWas && hasPhase) {
        int i = Array.IndexOf(hs.Heroes, hs.Hero); W32.POINT c;
        if (i >= 0 && W32.GetCursorPos(out c) && Math.Abs(c.X - o.X - hg2.ColX(i % HeroGeometry.Columns) * U) < 0.06 * U) {
          double off = (i / HeroGeometry.Columns) * pitch, cy = c.Y - o.Y, lo = cy - card - off, hi = cy - off;
          double s = ListTracker.Snap((lo + hi) / 2 - vt, ph, pitch) + vt;
          if (s >= lo - 4 && s <= hi + 4) { heroAnchor.Row1Top = s; heroAnchor.Has = true; heroFollow.Anchored(null, now); }
        }
      }
      heroShownWas = hs.Hero;
      // where the game says the hero list is scrolled: exact
      double heroY; bool exact = RFX.ReadScrollY(addrs, true, out heroY);
      if (exact) {
        double r1 = top0 - heroY * U / 675.0;
        if (hasPhase) { double s = ListTracker.Snap(r1 - vt, ph, pitch) + vt; if (Math.Abs(s - r1) <= 0.1 * pitch) r1 = s; }
        heroAnchor.Row1Top = r1; heroAnchor.Has = true; heroFollow.Anchored(sig, now);
      }
      // at the top: a fresh list (a tight phase match only) or one that fits on the screen
      if (!exact && !heroAnchor.Has && hasPhase) {
        double d = ((vt + ph - top0) % pitch + pitch) % pitch; if (d > pitch / 2) d -= pitch;
        bool fits = top0 + rowsN * pitch <= vb + (pitch - card);
        if ((fits || (heroFreshTop && now - heroFreshMs < 1500)) && Math.Abs(d) <= Math.Max(3.5, 0.06 * pitch)) {
          heroAnchor.Row1Top = top0 + d; heroAnchor.Has = true; heroFollow.Reset(); heroFollow.Anchored(sig, now);
        }
      }
      bool sure = exact || (heroAnchor.Has && heroFollow.Follow(heroAnchor, sig, SigN, pitch, now));
      if (!exact && sure && hasPhase) heroAnchor.Row1Top = ListTracker.Snap(heroAnchor.Row1Top - vt, ph, pitch) + vt;
      if (sure && !exact) {
        double end = Math.Min(top0, vb - rowsN * pitch), slack = 0.5 * h;
        if (heroAnchor.Row1Top > top0 + slack || heroAnchor.Row1Top < end - slack) { heroAnchor.Has = false; sure = false; }
      }
      if (sure) heroFollow.Settled(heroAnchor, sig, top0, pitch);
      if (!sure) { DrawTip(o.X + x0 + 8, o.Y + vt + 8, 4, 0, HeroTip(1), H, true, true); return; }
      double ctop = heroAnchor.Row1Top + (t / HeroGeometry.Columns) * pitch, ccx = hg2.ColX(t % HeroGeometry.Columns) * U, cwid = 0.115 * U;
      if (ctop + card / 2 < vt) DrawTip(o.X + x0 + 8, o.Y + vt + 8, 4, 0, HeroTip(2), H, true, true);
      else if (ctop + card / 2 > vb) DrawTip(o.X + x0 + 8, o.Y + vb - 8, 4, 0, HeroTip(3), H, true);
      else DrawTip(o.X + (int)(ccx - cwid / 2), o.Y + (int)ctop, (int)cwid, (int)card, HeroTip(0), H, false, ctop < 0.35 * H);
    }
    bool heroFreshTop;

    /// <summary>A pulsing gold frame around a button of the game (x, y, w, h: screen px) with a text plate above it.</summary>
    // (below: the plate under the button, for buttons at the top of the window; right: the plate's right edge at the button's,
    // for buttons right of the middle)
    void DrawTip(int x, int y, int w, int h, string[] lines, double H, bool plateOnly = false, bool below = false, bool right = false) {
      float pulse = (float)(0.6 + 0.4 * Math.Sin(frame * 0.45));
      string key = x + ":" + y + ":" + w + ":" + h + ":" + string.Join("|", lines) + ":" + (plateOnly ? 0 : (int)(pulse * 10)) + (below ? "b" : "") + (right ? "r" : "");
      if (key == tipKey) return;
      tipKey = key;
      float fs = (float)Math.Max(13.0, H * 0.022);
      using (var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel)) {
        float tw = 0, lh = fs * 1.35f;
        using (var probe = new Bitmap(1, 1)) using (var pg = Graphics.FromImage(probe)) foreach (var l in lines) tw = Math.Max(tw, pg.MeasureString(l, f).Width);
        int pw = lines.Length > 0 ? (int)tw + 28 : 0, ph = lines.Length > 0 ? (int)(lh * lines.Length) + 16 : 0;
        int bw = Math.Max(w + 52, pw + 8), bh = h + 52 + (ph > 0 ? ph + 14 : 0);
        int fx = right ? bw - w - 26 : 26, fy = below ? 26 : bh - h - 26;
        int px0 = right ? bw - pw : 0, py0 = below ? bh - ph : 0;
        using (var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb)) {
          using (var gr = Graphics.FromImage(bmp)) {
            gr.SmoothingMode = SmoothingMode.AntiAlias; gr.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; gr.Clear(Color.Transparent);
            if (!plateOnly) {
              for (int i = 24; i >= 2; i -= 2)
                using (var pen = new Pen(Color.FromArgb((int)(140 * pulse * (1 - i / 26f)), 255, 190, 60), 3f)) gr.DrawPath(pen, Round(fx - i, fy - i, w + 2 * i, h + 2 * i, 6 + i));
              using (var pen = new Pen(Color.FromArgb(220, 30, 18, 4), 8f)) gr.DrawPath(pen, Round(fx - 2, fy - 2, w + 4, h + 4, 8));
              using (var pen = new Pen(Color.FromArgb(255, 255, 214, 110), 4f)) gr.DrawPath(pen, Round(fx - 2, fy - 2, w + 4, h + 4, 8));
            }
            if (ph > 0) {
              var rc = new RectangleF(px0, py0, pw, ph);
              using (var bg = new SolidBrush(Color.FromArgb(235, 16, 12, 8))) gr.FillPath(bg, Round(rc.X + 1, rc.Y + 1, rc.Width - 2, rc.Height - 2, 6));
              using (var pen = new Pen(Color.FromArgb(240, 232, 190, 110), 2f)) gr.DrawPath(pen, Round(rc.X + 1, rc.Y + 1, rc.Width - 2, rc.Height - 2, 6));
              using (var fg = new SolidBrush(Color.FromArgb(255, 255, 236, 180))) for (int i = 0; i < lines.Length; i++) gr.DrawString(lines[i], f, fg, px0 + 14, py0 + 8 + i * lh);
              float ax = Math.Max(px0 + 20, Math.Min(px0 + pw - 20, fx + w / 2f));
              var tri = below ? new[] { new PointF(ax - 9, py0 + 1), new PointF(ax + 9, py0 + 1), new PointF(ax, py0 - 11) }
                              : new[] { new PointF(ax - 9, ph - 1), new PointF(ax + 9, ph - 1), new PointF(ax, ph + 11) };
              if (!plateOnly) using (var br = new SolidBrush(Color.FromArgb(240, 232, 190, 110))) gr.FillPolygon(br, tri);
            }
          }
          tip.Put(bmp, x - fx, y - fy);
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
        AnchorHas = anchor.Has && !follow.Unsure && types != null && typesPtr != 0, Row1Top = anchor.Row1Top, Types = types,
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
      if (fview.PanelOpen && fview.SidePanelOpen) v.SideClose = new[] { fg.X(fg.SideCloseX, cr.R, cr.B), fg.Y(fg.CloseY, cr.R, cr.B) };
      if (fpilot.State == "failed") {
        if (!filterFailSaid) { filterFailSaid = true; RaiseProblem("pilot.filter_failed", "filter failed: the game's gear filter could not be set for item " + fview.Item + " (kind " + filterKind + ")"); }
      } else filterFailSaid = false;
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
        SendInside(new AutoAction { Kind = AutoKind.Click, X = W - invDX * U, Y = H - hg2.HeroesBtnDY * U }, o, cr);
        return;
      }
      SellSay("no_city");
    }

    // ---------------------------------------------------------------- «Найти в игре»

    LocateRequest locateReq; string[] locateLines = new string[0];
    readonly LocateGrid lgrid = new LocateGrid();
    LocateStep locateStep; SubCheck locateSubs; string locateKey = "";
    long locateReadAt, locateLastCur = -1, locateSubsAt; int locateCols = 8, locateGeoCols; double locateGeoW, locateGeoH;
    SellGeometry locateGeo; InventoryScreen locateInv; bool locateNeedClick;
    /// <summary>The step changed (or its list place / sub stat check): the page shows it and answers with the text plate.</summary>
    public event Action<LocateStep, SubCheck> LocateChanged;

    void LocateHide() { if (glow.Visible) glow.Hide(); drawnKey = null; HideTip(); }

    void TickLocate() {
      var req = locateReq;
      IntPtr hwnd = GameWindow();
      W32.RECT cr = new W32.RECT();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { LocateHide(); return; }
      bool front = IsForeground(hwnd);
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      long now = NowMs();
      long uid = req.Item.Uid;
      // the game's screens, read a few times a second (a form the game never built is searched for at most every 8 s)
      if (locateStep == null || Math.Abs(now - locateReadAt) >= 300) {
        locateReadAt = now;
        int rescan = front ? 8000 : -1;
        locateInv = RFX.ReadInventory(rescan);
        var rs = RFX.ReadRecastScreen(rescan);
        if (front && locateInv != null && locateInv.Open && locateInv.EquipTab) { int c = InvColumns(o, cr, U); if (c > 0) locateCols = c; }
        heroScreen = RFX.ReadHeroScreen(addrs, heroScreen);
        var st = LocateGuide.Next(req, locateInv, rs, heroScreen, locateCols, RFX.ItemOnAccount(uid));
        if (Math.Abs(now - locateSubsAt) >= 1500 && (st.Kind == LocateKind.Selected || st.Kind == LocateKind.Done || st.Kind == LocateKind.RecastPick || st.Kind == LocateKind.InList)) {
          locateSubsAt = now;
          var live = RFX.ReadLiveItemSubs(uid);
          locateSubs = live != null ? LocateGuide.CheckSubs(req.Item, live) : null;
        }
        st.NeedClick = locateNeedClick;
        locateStep = st;
      }
      var step = locateStep;
      // the inventory's list: where the item's cell is on the screen
      locateNeedClick = false;
      if (front && step.Kind == LocateKind.InList && locateInv != null) {
        var inv = locateInv;
        if (locateGeo == null || locateGeoW != W || locateGeoH != H || locateGeoCols != locateCols) {
          locateGeoW = W; locateGeoH = H; locateGeoCols = locateCols; locateGeo = SellGeometry.Inventory(W, U, locateCols);
          lgrid.Reset();
        }
        if (lgrid.ListPtr != inv.ListPtr) lgrid.Reset();
        // the player clicked a cell: the pointer shows where its row is
        if (inv.Current != locateLastCur) {
          if (locateLastCur != -1 && inv.Current > 0) {
            var p = LocateGuide.Position(inv.Uids.ToArray(), inv.Current, locateCols);
            W32.POINT c;
            if (p != null && W32.GetCursorPos(out c) && c.Y - o.Y >= locateGeo.ViewTopPx(U) && c.Y - o.Y <= locateGeo.ViewBottomPx(H, U)) lgrid.FromClick(locateGeo, p[1], c.Y - o.Y, U, inv.ListPtr);
          }
          locateLastCur = inv.Current;
        }
        double phase = PhaseOf(locateGeo, o, cr, U);
        bool hasPhase = !double.IsNaN(phase);
        if (!lgrid.Has && hasPhase) lgrid.FromTop(locateGeo, phase, U, inv.ListPtr);
        if (lgrid.Has && hasPhase) lgrid.Track(locateGeo, lgrid.VisibleRow(locateGeo, (inv.Uids.Count + locateCols - 1) / locateCols, W, H, U), phase, U);
        locateNeedClick = !lgrid.Has;
        if (lgrid.Has) {
          var mk = lgrid.Place(locateGeo, step.Row, step.Col, W, H, U);
          frame++;
          HideTip();
          Draw(mk.Kind, o.X + mk.X, o.Y + mk.Y, mk.W, mk.H, mk.Rows);
        } else LocateHide();
      } else if (!front) { LocateHide(); }
      else { if (glow.Visible) glow.Hide(); drawnKey = null; }
      if (locateInv != null && step.Kind != LocateKind.InList) locateLastCur = locateInv.Current;
      step.NeedClick = locateNeedClick;
      // a plate with the step's text (the page composes it): in the list's top-left corner, no frame
      if (front && locateLines.Length > 0 && (step.Kind != LocateKind.InList || locateNeedClick)) {
        frame++;
        DrawTip(o.X + (int)(0.23 * U), o.Y + (int)(0.19 * U) + 150, 4, 0, locateLines, H, true);
      } else if (!front || locateLines.Length == 0 || step.Kind == LocateKind.InList) HideTip();
      string key = step.Key + "|" + step.Pos + "|" + step.Row + "|" + step.Col + "|" + step.Count + "|" + string.Join(",", step.Missing) + "|" + step.OnHero + "|" + step.NeedClick
        + "|" + (locateSubs != null ? string.Join(",", Array.ConvertAll(locateSubs.Match, x => x ? "1" : "0")) + locateSubs.Changed : "");
      if (key != locateKey) { locateKey = key; var h = LocateChanged; if (h != null) h(step, locateSubs); }
    }

    const double InvBtnDX = 0.5222 + 0.9316;   // «Инвентарь»: 0.9316 left of «Герои» in the city's bottom bar
    int sellCols = 8, sellGeoCols; double sellGeoW, sellGeoH; bool sellScanOnce;

    // the rows' phase on the screen: the stat bars under the cells (client px mod the row step), NaN = unclear
    double SellPhase(W32.POINT o, W32.RECT cr, double U) { return PhaseOf(spilot.Geometry, o, cr, U); }

    double PhaseOf(SellGeometry sg, W32.POINT o, W32.RECT cr, double U) {
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
      // only the round icon is compared (the top ICON part): the label under it is in the game's language
      const double ICON = 0.74;
      readonly string res; float[] g; int w, h; float[] scaled; int sw, sh, fullH; double su;
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
        double x; return Find(o, W, H, cx, cx, cy, out x);
      }

      /// <summary>The picture's centre x between x0 and x1 (client px; the bar moves when the game adds an event button),
      /// on the row cy; false = not there.</summary>
      public bool Find(W32.POINT o, double W, double H, double x0, double x1, double cy, out double found) {
        found = double.NaN;
        if (!Load()) return false;
        double U = Ui.Unit(W, H);
        if (scaled == null || Math.Abs(su - U) > 0.5) {
          su = U; double k = U / 900.0;
          sw = Math.Max(8, (int)Math.Round(w * k)); fullH = Math.Max(8, (int)Math.Round(h * k)); sh = Math.Max(8, (int)Math.Round(fullH * ICON));
          scaled = new float[sw * sh];
          for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
            scaled[y * sw + x] = g[Math.Min(h - 1, (int)(y / k)) * w + Math.Min(w - 1, (int)(x / k))];
        }
        const int R = 6;
        if (x1 < x0) { var tx = x0; x0 = x1; x1 = tx; }
        int span = (int)Math.Round(x1 - x0);
        int gx = (int)(x0 - sw / 2.0) - R, gy = (int)(cy - fullH / 2.0) - R, gw = sw + 2 * R + span, gh = sh + 2 * R;
        if (gx < 0) { span += gx; gw += gx; gx = 0; }
        if (gx + gw > W) { int cut = (int)(gx + gw - W); span -= cut; gw -= cut; }
        if (span < 0 || gy < 0 || gy + gh > H) return false;
        int[] px = Grab(o.X + gx, o.Y + gy, gw, gh);
        if (px == null) return false;
        var grey = new float[px.Length];
        for (int i = 0; i < px.Length; i++) { int c = px[i]; grey[i] = 0.299f * ((c >> 16) & 255) + 0.587f * ((c >> 8) & 255) + 0.114f * (c & 255); }
        double tm = 0; foreach (var t in scaled) tm += t; tm /= scaled.Length;
        double tv = 0; foreach (var t in scaled) tv += (t - tm) * (t - tm);
        double best = -1, bestStd = 0, bestX = 0, tstd = Math.Sqrt(tv / scaled.Length);
        for (int dy = 0; dy <= 2 * R; dy += 2)
          for (int dx = 0; dx <= 2 * R + span; dx += 2) {
            double m = 0;
            for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++) m += grey[(y + dy) * gw + x + dx];
            m /= scaled.Length;
            double num = 0, pv = 0;
            for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++) {
              double p = grey[(y + dy) * gw + x + dx] - m, t = scaled[y * sw + x] - tm;
              num += p * t; pv += p * p;
            }
            double ncc = pv > 0 && tv > 0 ? num / Math.Sqrt(pv * tv) : 0;
            if (ncc > best) { best = ncc; bestStd = Math.Sqrt(pv / scaled.Length); bestX = gx + dx + sw / 2.0; }
          }
        double contrast = tstd > 0 ? bestStd / tstd : 0;
        if (!(best >= 0.75 && contrast >= 0.6 && contrast <= 1.6)) return false;
        found = bestX;
        return true;
      }
    }
    static readonly ButtonTpl heroesBtn = new ButtonTpl("overlay/heroes_btn.png"), inventoryBtn = new ButtonTpl("overlay/inventory_btn.png");

    // the picture's centre (the click goes to the icon, a bit higher)
    bool HeroesButtonSeen(W32.POINT o, double W, double H) { double U = Ui.Unit(W, H); return heroesBtn.Seen(o, W, H, W - hg2.HeroesBtnDX * U, H - 0.1156 * U); }
    // «Инвентарь» / «Storage»: at its usual place, else searched along the bar to the left of «Герои» (an event button
    // such as «Demon Soldier» moves it); the place found is kept for the window
    double invDX = InvBtnDX; long invScanAt;
    bool InventoryButtonSeen(W32.POINT o, double W, double H) {
      double U = Ui.Unit(W, H), cy = H - 0.1156 * U;
      if (inventoryBtn.Seen(o, W, H, W - invDX * U, cy)) return true;
      long now = NowMs();
      if (now - invScanAt < 3000) return false;
      invScanAt = now;
      double x;
      if (!inventoryBtn.Find(o, W, H, W - (hg2.HeroesBtnDX + 0.3) * U, W - (hg2.HeroesBtnDX + 2.8) * U, cy, out x)) return false;
      invDX = (W - x) / U;
      Log.Write("sell: «Инвентарь» found in the bar at " + invDX.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " U from the right");
      return true;
    }

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
        + " panel=" + v.PanelOpen + "/" + v.SidePanelOpen + "/" + v.SetPanelOpen + " -> " + fpilot.State + (string.IsNullOrEmpty(fpilot.Note) ? "" : " (" + fpilot.Note + ")");
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
      if (btn != null || hintKind != "replace") confirmMissSince = 0;
      if (btn == null && hintKind == "replace") {
        // (the journal: why «Заменить» is not pressed - once per selected item). Right after a press the card closes while
        // the game still shows the item selected for a moment: only a miss that lasts 2.5 s and leaves the item off the
        // hero is one
        long nowMs = NowMs();
        if (confirmMissSince == 0 || confirmMissFor != target) { confirmMissSince = nowMs; confirmMissFor = target; }
        bool lasting = nowMs - confirmMissSince >= 2500;
        bool worn = lasting && addrs != null && hintHero > 0 && RFX.ReadOwner(addrs, target) == hintHero;
        if (lasting && !worn && confirmMissLogged != target) {
          confirmMissLogged = target;
          string miss = "confirm: no single blue button (replace " + (rep != null) + ", equip " + (eq != null) + ") for item " + target;
          Log.Write(miss);
          // reported only with the auto-confirm on: then the program cannot press «Заменить»; without it only the hint moves
          if (AutoConfirm && AutoEnabled) RaiseProblem("pilot.confirm_no_button", miss);
        }
      } else if (btn != null && (btn[1] != hg.Replace(W, H)[1] && btn[1] != hg.Equip(W, H)[1]) && confirmFoundLogged != target) {
        confirmFoundLogged = target;
        Log.Write("confirm: button found " + (btn[1] - (rep != null ? hg.Replace(W, H)[1] : hg.Equip(W, H)[1])) + " px off its usual place, item " + target);
      }
      return btn;
    }
    long confirmMissLogged, confirmFoundLogged, confirmMissSince, confirmMissFor;

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

    // values per line of the strip picture (ListTracker.LineSig): 4 parts of each of the 3 columns
    const int SigSegs = 4, SigN = ListGeometry.Columns * SigSegs;

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
      if (locateReq == null) Say(kind == "box" ? "on" : kind == "up" ? "above" : "below", rows);   // the plan's state; «Найти в игре» has its own
      dKind = kind + (rows > 0 ? ":" + rows : ""); dX = x; dY = y; dW = w; dH = h;
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
