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
using System.Collections.Generic;
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
      // kept out of screen captures - our own looks at the list included. Older Windows (before 10 2004) cannot: then the
      // looks see our drawings (live 2026-10-14, a 1366x705 laptop: the plates were on the diagnostics screenshots) and
      // mask the parts we drew (Parts) themselves
      ApplyAffinity();
      if (!Excluded && !Capturable) Log.Write("overlay: the hint windows cannot be hidden from screen captures on this Windows: the screen looks mask them");
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
    bool downIn;
    /// <summary>Width of the pressable square at the window's left (0 = the whole window is the button): the record button's timer plate beside it is not.</summary>
    public int HitW;
    bool Inside(IntPtr lParam) {
      long l = lParam.ToInt64(); int x = (short)(l & 0xFFFF), y = (short)((l >> 16) & 0xFFFF);
      return x >= 0 && y >= 0 && (HitW <= 0 ? x < Width && y < Height : x < HitW && y < Math.Min(HitW, Height));
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
      if (clickable && m.Msg == 0x0201) { downIn = Inside(m.LParam); return; }   // WM_LBUTTONDOWN: only remembered, the press counts on the release
      if (clickable && m.Msg == 0x0203) return;                                  // WM_LBUTTONDBLCLK: no second press
      if (clickable && m.Msg == 0x0202) { bool ok = downIn && Inside(m.LParam); downIn = false; if (ok) { var p = Pressed; if (p != null) p(); } return; }   // WM_LBUTTONUP inside the button, after a press inside it
      base.WndProc(ref m);
    }

    /// <summary>Hidden from screen captures (false: the screen looks see what this window shows).</summary>
    public bool Excluded = true;
    /// <summary>TEST build, while the screen recorder runs: the windows are left in screen captures (so the recording shows them).</summary>
    public static bool Capturable;
    public void ApplyAffinity() {
      try { Excluded = W32.SetWindowDisplayAffinity(Handle, Capturable ? 0u : 0x11u) && !Capturable; } catch (Exception) { Excluded = false; }
    }
    /// <summary>What it shows now: its parts in screen px {x, y, w, h} (empty when hidden).</summary>
    public System.Collections.Generic.List<int[]> Parts = new System.Collections.Generic.List<int[]>();
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) Parts.Clear(); }

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

  /// <summary>The guide hand's picture ({site}/art/ui/guide-hand-down.png): fetched once into the app's data folder, then read from there;
  /// not available = no hand is drawn (the highlight works without it).</summary>
  static class GuideHand {
    /// <summary>Where the fingertip is on the picture (fractions of its width and height).</summary>
    public const double TipX = 0.36, TipY = 0.85;
    static volatile Image img;
    static int state;            // 0 = not tried, 1 = loading, 2 = done
    static long retryAt;
    static string Cache { get { return Path.Combine(AppConfig.DefaultDir, "art", "guide-hand-down.png"); } }

    public static Image Get(string site) {
      var i = img; if (i != null) return i;
      if (string.IsNullOrEmpty(site)) return null;
      long now = Environment.TickCount;
      if (state == 1 || (state == 2 && now - retryAt < 0)) return null;
      state = 1;
      System.Threading.ThreadPool.QueueUserWorkItem(delegate {
        try {
          if (!File.Exists(Cache)) Download(site.TrimEnd('/') + "/art/ui/guide-hand-down.png");
          using (var s = new MemoryStream(File.ReadAllBytes(Cache))) img = new Bitmap(Image.FromStream(s));
        } catch (Exception) { try { if (File.Exists(Cache)) File.Delete(Cache); } catch (Exception) { } }
        retryAt = Environment.TickCount + 10 * 60 * 1000; state = 2;   // a failed try is repeated after 10 minutes
        if (img != null) state = 3;
      });
      return null;
    }

    static void Download(string url) {
      SyncClient.EnableTls12();
      var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
      req.UserAgent = SyncClient.UserAgent; req.Timeout = 8000; req.ReadWriteTimeout = 8000; req.AllowAutoRedirect = false;
      using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
      using (var s = resp.GetResponseStream())
      using (var ms = new MemoryStream()) {
        var buf = new byte[8192]; int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > 2 * 1024 * 1024) throw new IOException("too big"); }
        Directory.CreateDirectory(Path.GetDirectoryName(Cache));
        File.WriteAllBytes(Cache, ms.ToArray());
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
    /// <summary>The same button while «Показать в игре» clicks by itself: it only stops that.</summary>
    public string LocateCancelText = "Остановить автонажатие";
    string cancelDrawn; Bitmap cancelBmp;
    // TEST build: the record button over the game (top left); a clickable window like cancelWin, drawn only while the central gate allows
    readonly GlowWindow recWin = new GlowWindow(true);
    readonly Timer recBtnTimer = new Timer();
    readonly Stopwatch recSw = new Stopwatch();
    string recDrawn; Bitmap recBmp; bool recOn; long recToggleAt;
    /// <summary>Settings → «Кнопка записи поверх игры» (the test build only).</summary>
    public bool RecButton;
    /// <summary>The player pressed the record button over the game: the host toggles the recorder.</summary>
    public event Action RecPressed;
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
    public bool Busy { get { return runHero != 0 || target != 0 || (sellWanted != null && !sellHint) || sending; } }
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

    // storage cleanup without auto click («Подсказать в игре»): the same list, but the program only reads the inventory and
    // draws frames on the cells to select; every click is the player's
    bool sellHint;
    string shFrameSaid = "";
    string[] sellHintLines = new string[0]; string sellPillUp = "", sellPillDown = "";
    readonly LocateGrid sgrid = new LocateGrid();
    InventoryScreen shInv; bool shHave; string shReadSig = ""; long shReadAt;
    int shCols = 8, shGeoCols; double shGeoW, shGeoH; SellGeometry shGeo;
    List<SellFrame> shFrames; string shKey = "";
    /// <summary>The hint's step changed (kind, counts, rows out of view): the page shows it and answers with the plate texts.</summary>
    public event Action<SellHintStep> SellHintChanged;

    /// <summary>A failure worth an error report (kind, message): the filter pilot gave up, «Заменить» not found with the
    /// auto-confirm on, an exception in the tick. HostBridge sends it when the player turned the reports on.</summary>
    public event Action<string, string> Problem;
    void RaiseProblem(string kind, string message) { var p = Problem; if (p != null) try { p(kind, message); } catch (Exception) { } }
    bool filterFailSaid; string tickError;

    public OverlayController(Action<string, bool> report, Action<string> autoReport, Action<SellPilot, string> sellReport) {
      this.report = report; this.autoReport = autoReport; this.sellReport = sellReport;
      spilot = new SellPilot(SellGeometry.Inventory(1920, 1009, 8));
      screens.Sink = s => Log.Write(s); gate.Sink = s => Log.Write(s);
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
      recWin.Text = "Wardsage rec";
      recWin.Pressed += () => {
        long n = NowMs(); if (recToggleAt != 0 && Math.Abs(n - recToggleAt) < 1000) return;   // one toggle a second at most
        recToggleAt = n;
        recOn = !recOn; if (recOn) recSw.Restart(); else recSw.Reset();                       // shown at once; SetRecording(..) confirms
        try { TickRecBtn(); } catch (Exception) { }
        var r = RecPressed; if (r != null) r();
      };
      recBtnTimer.Interval = 200; recBtnTimer.Tick += (s, e) => {
        try { TickRecBtn(); } catch (Exception ex) { if (recWin.Visible) recWin.Hide(); string m = "recbtn: " + ex.GetType().Name + ": " + ex.Message; if (m != tickError) { tickError = m; Log.Write("overlay: " + ex); } }
      };
      if (Channel.IsTest) recBtnTimer.Start();
      cancelWin.Pressed += () => {
        cancelWin.Hide();
        if (SellClicking) { gate.Halt(ClickOwner.Sell, "player", "stopped by the player"); AnnounceHalts(); return; }   // storage cleanup: the clicks stop, the hints stay
        if (LocateWins && runHero <= 0) { lpilot.Cancel(); Log.Write("locate auto: stopped over the game"); return; }   // «Показать в игре»: only the clicks stop
        var c = CancelPressed; if (c != null) c();
      };
    }

    public void Dispose() { timer.Dispose(); lookTimer.Dispose(); glow.Dispose(); tip.Dispose(); cancelWin.Dispose(); recBtnTimer.Dispose(); recWin.Dispose(); }

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
    public void SetFilterGoal(long item, string kind) { bool was = PlanOn; filterItem = item; filterKind = kind ?? ""; PlanStamp(was); if (item != 0) Run(); }

    // Overlay owner when an equip plan and a locate request are both on: the one started later (OverlayPriority).
    long planSince, locateSince;
    bool PlanOn { get { return runHero != 0 || target != 0 || hintKind != ""; } }
    void PlanStamp(bool was) { if (!was && PlanOn) planSince = Stamp(); }
    static long Stamp() { return DateTime.UtcNow.Ticks / 10000; }
    /// <summary>«Показать в игре» owns the overlay now (its request is on and no later plan took it over).</summary>
    bool LocateWins { get { return locateReq != null && OverlayPriority.Winner(PlanOn, planSince, true, locateSince) == OverlayPriority.Locate; } }

    /// <summary>An item's set and main stat (from the plan), for the game's filter.</summary>
    public void SetItemInfo(long uid, long setId, long statId) { if (uid > 0 && (setId > 0 || statId > 0)) itemInfo[uid] = new[] { setId, statId }; }

    /// <summary>The started plan's hero (0 = none): the pilot opens that hero's gear on the hero screen.</summary>
    public void SetRun(long hero) { if (hero == runHero) return; if (hero > 0) ResumeClicks(ClickOwner.Equip); bool was = PlanOn; runHero = hero; PlanStamp(was); Run(); }

    /// <summary>Every pilot starts over (a new «Надеть»: after a failure the player may have fixed the game's screen).</summary>
    /// <summary>What the helper is at while a started plan runs (null = no plan, or a pilot is clicking / waiting for the
    /// player): the same key for long means the plan stands still.</summary>
    /// <summary>The hero the plan wants opened while its step is «open the hero» (the hint, or the started plan); 0 = none.</summary>
    public long HeroStepHero() { return hintKind == "hero" ? hintHero : (hintKind == "" ? runHero : 0); }

    public string StallKey() {
      if (runHero <= 0 || autoSaid == "work" || autoSaid == "paused" || autoSaid == "background") return null;
      return runHero + "|" + target + "|" + filterItem + "|" + filterKind + "|" + hintKind + "|" + autoSaid;
    }

    public void RestartPilots() { pilot.Reset(); fpilot.Reset(); hpilot.Reset(); ResumeClicks(ClickOwner.Equip); }

    /// <summary>Item to highlight (0 = none).</summary>
    public void SetTarget(long uid) {
      if (uid == target) return;
      bool was = PlanOn;
      target = uid; PlanStamp(was);
      if (uid == 0) Hide("off");
      Run();
    }

    /// <summary>Hint on the game's own buttons: "filter", "replace", "slot" (slot 0–4) or "" = none; up to two text lines;
    /// <paramref name="hero"/>: the plan's hero (for «Заменить»).</summary>
    public void SetHint(string kind, int slot, string[] lines, long hero) {
      kind = kind ?? ""; lines = lines ?? new string[0];
      if (kind == hintKind && slot == hintSlot && hero == hintHero && string.Join("\n", lines) == string.Join("\n", hintLines)) return;
      if (kind != hintKind) doneSince = 0;
      bool was = PlanOn;
      hintKind = kind; hintSlot = slot; hintLines = lines; hintHero = hero;
      PlanStamp(was);
      HideTip(); Run();
    }

    /// <summary>Storage cleanup: select these items on the game's sell screen (null = stop). The app opens the screen from
    /// the gear list of <paramref name="hero"/> (or of the hero the game shows); «Продать» is pressed by the player.</summary>
    public void SetSell(System.Collections.Generic.ICollection<long> uids, System.Collections.Generic.ICollection<int> parts, System.Collections.Generic.Dictionary<long, int> levels, long hero, bool hintOnly = false) {
      bool wasHint = sellHint;
      sellHint = hintOnly && uids != null && uids.Count > 0;
      if (wasHint || sellHint) {
        // a hint run starts (or ends) clean: no frames, no plate, the rows' place unknown
        sgrid.Reset(); shInv = null; shHave = false; shReadSig = ""; shReadAt = 0; shFrames = null; shKey = ""; shLast = null; shUnkSince = -1; shHold = false;
        sellHintLines = new string[0]; sellPillUp = sellPillDown = "";
        Hide("off"); HideTip();
      }
      sellLevels = levels;
      sellWanted = uids != null && uids.Count > 0 ? new System.Collections.Generic.HashSet<long>(uids) : null;
      sellParts = parts != null && parts.Count > 0 ? new System.Collections.Generic.HashSet<int>(parts) : null;
      sellHero = hero; sellSaid = ""; spilot.Reset(); hpilot.Reset(); ResumeClicks(ClickOwner.Sell); sellBtnAt = 0; sellScanOnce = true;
      Run();
    }

    /// <summary>«Найти в игре»: show where this item is (null = stop). Hints only: the frame in the inventory's list and a
    /// text plate; nothing is clicked. While it runs the build's frame is not shown (one target at a time).</summary>
    public void SetLocate(LocateRequest r) {
      locateReq = r != null && r.Item != null ? r : null;
      locateDoneUntil = 0; locateLive = null; lfin.Reset();
      locateSince = Stamp(); locateCaps = ""; locateLines = new string[0];
      locateStep = null; locateSubs = null; locateKey = ""; locateReadAt = 0; locateLastCur = -1; locateSubsAt = 0; lgrid.Reset();
      locateScreensSaid = ""; locateMarkerSaid = ""; locateCardSaid = ""; locateListSig = ""; locateDecisionSaid = ""; locatePlaceSaid = "";
      lpilot.Reset(); ResumeClicks(ClickOwner.Locate); lpilot.ItemUid = locateReq != null ? locateReq.Item.Uid : 0; locateDriving = false; locateAutoSaid = "";
      Hide("off"); HideTip();
      Run();
    }

    /// <summary>The plates of the storage cleanup hint (composed by the page in the interface language): the step's lines and
    /// the pills for the list items out of view.</summary>
    public void SetSellTip(string[] lines, string pillUp, string pillDown) {
      sellHintLines = lines ?? new string[0]; sellPillUp = pillUp ?? ""; sellPillDown = pillDown ?? "";
      HideTip(); drawnKey = null;
    }

    /// <summary>The text plate for the current step (composed by the page in the interface language).</summary>
    /// <param name="caps">the plate's header line («Показать в игре · вещь · шаг N из M»); its own, never the equip plan's.</param>
    public void SetLocateTip(string[] lines, string caps, Dictionary<string, string> texts = null) { locateLines = lines ?? new string[0]; locateCaps = caps ?? ""; if (texts != null) locateTexts = texts; HideTip(); }

    void Run() {
      if (target != 0 || locateReq != null || filterItem != 0 || hintKind != "" || runHero != 0 || sellWanted != null || DateTime.Now < diagUntil || RecInfo.Active) { timer.Start(); if (target != 0) lookTimer.Start(); else lookTimer.Stop(); return; }
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

    // the screen recorder (TEST build): what this tick decided, collected while a recording runs and published at its end
    RecInfo recB;
    void Tick() {
      recB = RecInfo.Active ? new RecInfo() : null;
      try { TickWork(); }
      finally {
        if (recB != null) { var b = recB; recB = null; b.Screen = screens.Current != null ? screens.Current.Brief : "-"; RecInputs(b); RecInfo.Publish(b); }
      }
    }

    // the recorder's raw inputs of this tick (src/RecInputs.cs): the guides' arguments and what decides whether they draw; the screen reads are queued by ReadScreens
    void RecInputs(RecInfo b) {
      try {
        IntPtr hwnd = GameWindow();
        bool sell = !LocateWins && sellHint && sellWanted != null;
        var want = new List<long>(sellWanted ?? new HashSet<long>()); want.Sort();
        var c = new RecCtx { Mode = LocateWins ? "locate" : sell ? "sell" : "", Front = hwnd != IntPtr.Zero && IsForeground(hwnd), Cols = locateCols, OnAccount = locateOnAccount,
          Req = LocateWins ? locateReq : null, Lines = LocateWins ? locateLines.Length > 0 : sellHintLines.Length > 0, CityInvSeen = cityInvSeen, InvDX = invDX, BtnDY = hg2.HeroesBtnDY,
          Want = sell ? want.ToArray() : new long[0], ShCols = shCols, PillUp = sellPillUp != "", PillDown = sellPillDown != "" };
        b.Ctx = c.ToJson(); b.TickNow = NowMs(); b.ReadSeq = RecReads.Seq;
        if (sell) { b.SInv = RecIn.InvCore(b.SellInv); b.SInvList = RecIn.List(b.SellInv != null ? b.SellInv.List : null); }
      } catch (Exception) { }
    }

    /// <summary>TEST build: the recorder starts / stops. Our windows stay in screen captures while it runs (then the screen looks mask them themselves).</summary>
    public void SetRecording(bool on) {
      RecInfo.Active = on; GlowWindow.Capturable = on;
      recOn = on; if (on) recSw.Restart(); else recSw.Reset();
      if (on) RecReads.Clear();
      foreach (var w in new[] { glow, tip, cancelWin, recWin }) if (w.IsHandleCreated) w.ApplyAffinity();
      Run();
    }

    void TickWork() {
      curRow = curCol = 0; curSel = 0;
      // the frames over the inventory / Foundry lists follow a scroll the game renders at its own rate: those hints tick often
      // (the scroll is read every frame we draw); everything else stays at the slow tick
      int wantInterval = LocateWins || (sellHint && sellWanted != null) ? 33 : 100;
      if (timer.Interval != wantInterval) timer.Interval = wantInterval;
      ReadScreens(NowMs());   // the game's screen, once per tick: every feature below takes it from here
      // the game is minimized / covered / alt-tabbed: nothing of ours stays over the desktop (the features go on reading)
      if (!GameDrawable()) { if (glow.Visible) glow.Hide(); HideTip(); drawnKey = null; }
      if (LocateWins) { TickLocate(); TickCancel(); return; }
      if (sellHint && sellWanted != null) { TickSellHint(); TickCancel(); return; }
      TickFrame();
      TickAuto();
      TickCancel();
    }

    // «Отменить надевание»: top centre of the game (free on the hero screen and in the gear list), only while a started
    // build is on and the game is in front
    /// <summary>The storage cleanup's auto click is running (not halted, not finished): the stop button is shown for it.</summary>
    bool SellClicking { get { return sellWanted != null && !sellHint && runHero <= 0 && !LocateWins && !gate.Halted(ClickOwner.Sell) && spilot.State != "done" && spilot.State != "failed"; } }

    /// <summary>«Показать в игре» with auto click enabled for it: the stop button shows during the WHOLE run (not only while a click is being made),
    /// until the pilot stops (gate, cancel, stuck), the job is done or the last step (the player's own button) is reached.</summary>
    bool LocateClicking {
      get {
        if (locateReq == null || !AutoEnabled || !LocateAutoEnabled || lpilot.Stopped || gate.Halted(ClickOwner.Locate) || locateDoneUntil != 0) return false;
        var k = locateStep != null ? locateStep.Kind : LocateKind.None;
        return k != LocateKind.Done && k != LocateKind.AscendReady && k != LocateKind.NoItem && k != LocateKind.OtherAccount;
      }
    }

    void TickCancel() {
      IntPtr hwnd = GameWindow();
      int fp = 0, gp = 0;
      if (hwnd != IntPtr.Zero) { W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp); W32.GetWindowThreadProcessId(hwnd, out gp); }
      W32.RECT cr;
      if ((runHero <= 0 && !(LocateWins && LocateClicking) && !SellClicking) || !AutoEnabled || hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || fp != gp || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) {
        if (cancelWin.Visible) cancelWin.Hide();
        return;
      }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double U = Ui.Unit(cr.R, cr.B);
      int w = (int)(0.25 * U), h = (int)(0.058 * U);
      string cancelText = runHero <= 0 ? LocateCancelText : CancelText;   // (here only for a build, for «Показать в игре» or for the storage cleanup)
      string key = cancelText + "|" + w + "x" + h;
      if (key != cancelDrawn || cancelBmp == null) {
        if (cancelBmp != null) cancelBmp.Dispose();
        cancelBmp = CancelBitmap(w, h, cancelText); cancelDrawn = key;
      }
      cancelWin.Put(cancelBmp, o.X + cr.R / 2 - w / 2, o.Y + (int)(0.012 * U));
    }

    // the record button (TEST build): a round gold-rimmed dark button at the game's top left (d = 0.055 U, inset 0.012 U): a red dot when idle,
    // a red rounded square while recording, with a plate «0:42» beside it. The central gate (GameDrawable) decides, as for every overlay drawing
    void TickRecBtn() {
      IntPtr hwnd = GameWindow();
      W32.RECT cr;
      if (!Channel.IsTest || !RecButton || !GameDrawable() || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { if (recWin.Visible) recWin.Hide(); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double U = Ui.Unit(cr.R, cr.B);
      int d = Math.Max(16, (int)(0.055 * U)), inset = Math.Max(2, (int)(0.012 * U));
      int sec = recOn ? (int)(recSw.ElapsedMilliseconds / 1000) : 0;
      string key = d + "|" + (recOn ? "r" + sec : "i");
      if (key != recDrawn || recBmp == null) {
        if (recBmp != null) recBmp.Dispose();
        recBmp = RecBtnBitmap(d, recOn, sec / 60 + ":" + (sec % 60).ToString("00")); recDrawn = key;
      }
      recWin.HitW = recBmp.Height;
      recWin.Put(recBmp, o.X + inset, o.Y + inset);
      recWin.Parts.Clear(); recWin.Parts.Add(new[] { o.X + inset, o.Y + inset, recBmp.Width, recBmp.Height });
    }

    static Bitmap RecBtnBitmap(int d, bool recording, string time) {
      int pad = 3, gap = Math.Max(4, d / 6);
      float fs = Math.Max(9f, d * 0.36f);
      int pw = 0, ph = (int)(d * 0.72);
      using (var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel)) {
        if (recording) using (var probe = new Bitmap(1, 1)) using (var g0 = Graphics.FromImage(probe)) pw = (int)g0.MeasureString(time, f).Width + (int)(d * 0.5);
        int w = d + (recording ? gap + pw : 0) + pad, h = d + pad;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var gr = Graphics.FromImage(bmp)) {
          gr.SmoothingMode = SmoothingMode.AntiAlias; gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; gr.Clear(Color.Transparent);
          using (var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) gr.FillEllipse(shadow, 2, 3, d - 2, d - 2);
          using (var bg = new SolidBrush(Color.FromArgb(242, 16, 18, 26))) gr.FillEllipse(bg, 1, 1, d - 3, d - 3);
          using (var pen = new Pen(Color.FromArgb(255, 122, 92, 46), 2f)) gr.DrawEllipse(pen, 1.5f, 1.5f, d - 4, d - 4);
          using (var pen = new Pen(Color.FromArgb(170, 224, 182, 96), 1f)) gr.DrawEllipse(pen, 4f, 4f, d - 9, d - 9);
          var red = Color.FromArgb(255, 226, 52, 44); float c = d / 2f - 0.5f;
          using (var br = new SolidBrush(red)) {
            if (recording) { float q = d * 0.17f; using (var p = Round(c - q, c - q, q * 2, q * 2, q * 0.35f)) gr.FillPath(br, p); }
            else { float r = d * 0.17f; gr.FillEllipse(br, c - r, c - r, r * 2, r * 2); }
          }
          if (recording) {
            float px = d + gap, py = (d - ph) / 2f;
            using (var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) gr.FillRectangle(shadow, px + 2, py + 3, pw, ph);
            using (var bg = new SolidBrush(Color.FromArgb(242, 16, 18, 26))) gr.FillRectangle(bg, px, py, pw, ph);
            using (var pen = new Pen(Color.FromArgb(255, 122, 92, 46), 2f)) gr.DrawRectangle(pen, px + 1, py + 1, pw - 2, ph - 2);
            using (var pen = new Pen(Color.FromArgb(170, 224, 182, 96), 1f)) gr.DrawRectangle(pen, px + 3.5f, py + 3.5f, pw - 7, ph - 7);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
              gr.DrawString(time, f, Brushes.White, new RectangleF(px, py, pw, ph), sf);
          }
        }
        return bmp;
      }
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
            + " refRow=" + dRefRow + " filter=" + (filterOpen ? "open" : "closed") + (hintKind == "hero" ? " hero: " + heroDiag : "") + " | " + diagInfo;
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
      SceneArea(o, cr);

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
      if (!anchor.Has || !sure) { NeedClick(); return; }
      PlaceFrame(o, H);
    }

    // the last frame tick's game window (for the quick looks between ticks), the list it saw
    W32.POINT lookO; double lookH; ulong lookList; long lookTarget; bool lookOk;
    // this look: the bars' phase (client px), how clear it was
    /// <summary>The gear list's row height in its own units (the content position is in them).</summary>
    public const double ListRowUnits = 100;
    readonly ScrollOffset listOff = new ScrollOffset(), heroOff = new ScrollOffset();
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
      // the scroll strip along the list's right edge: the whole list against the visible part, the item's dot
      double contentH = g.RowOffset(types, types.Length) * H + 2 * ListGeometry.TopPad * H, dotY, b0, b1;
      HintLayout.Strip(g.ViewTop * H, g.ViewBottom * H, contentH, ScrollAnchor.TopRow1(g, H) - anchor.Row1Top, (ListGeometry.TopPad + g.RowOffset(types, row)) * H + cell / 2, out dotY, out b0, out b1);
      var strip = new[] { o.X + g.StripRight * H + 0.012 * H, o.Y + g.ViewTop * H, o.Y + g.ViewBottom * H, o.Y + dotY, o.Y + b0, o.Y + b1 };
      if (mid < g.ViewTop * H) Draw("up", o.X + (int)left, o.Y + sy, (int)cell, 0, (int)Math.Ceiling((g.ViewTop * H - top) / pitch), strip);
      else if (mid > g.ViewBottom * H) Draw("down", o.X + (int)left, o.Y + sy + sh, (int)cell, 0, (int)Math.Ceiling((top + full - g.ViewBottom * H) / pitch), strip);
      else Draw("box", o.X + (int)left, o.Y + (int)top, (int)cell, (int)full, 0);
      dX -= o.X; dY -= o.Y;   // (Draw keeps screen px)
    }
    int frameRow, frameCol;

    // A quick look between the frame ticks (every 30 ms): a list flicked by the player moves up to a few thousand px a
    // second - in 100 ms further than the strip is high, and then its picture fits nowhere or somewhere wrong.
    void LookFast() {
      if (LocateWins || target == 0 || target != lookTarget || addrs == null || types == null || !lookOk || !anchor.Has || filterState != "" || typesPtr != lookList) return;
      IntPtr hwnd = GameWindow();
      if (hwnd == IntPtr.Zero || !IsForeground(hwnd)) return;
      bool sure = LookAtList(lookO, lookH, NowMs());
      if (sure) PlaceFrame(lookO, lookH); else NeedClick();
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
      MaskOwn(px, o.X + sx - M, o.Y + sy, w, sh);
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
        // (in the list's own units: an item row is 100 of them - the grid scales its rows to fit, 106 UI units each - so
        // a UI-unit conversion drifted one row behind over 16 rows, live 2026-10-14)
        double r1 = listOff.Place(ScrollAnchor.TopRow1(g, H) - scrollY * pitch / ListRowUnits, hasPhase ? sy + ph : double.NaN, pitch, H);
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
      SceneArea(o, cr);
      // the Inventory is in «Массовая продажа»: the hero and the items are not on screen - the way out is framed (the app clicks nothing there)
      var bs = screens.Current;
      if (bs != null && bs.Kind == ScreenKind.Inventory && bs.InvEquipTab && bs.InvBulk && Tx("bulkExit") != "") {
        var br = Geo.Current != null ? Geo.Current.Rect("inventory.bulkBack", W, H, U) : null;
        if (br != null) DrawTip(o.X + (int)br.X, o.Y + (int)br.Y, (int)br.W, (int)br.H, new[] { Tx("bulkExit") }, H);
        else DrawTipTop(new[] { Tx("bulkExit") });
        return;
      }
      // the build is on: «Готово» for a moment
      if (hintKind == "done") {
        long nd = NowMs(); if (doneSince == 0) doneSince = nd;
        if (nd - doneSince > 2000 || Tx("done") == "") HideTip(); else Scene(tip, ref tipKey, null, new Plate { Main = Tx("done") }, null, null);
        return;
      }
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
            // set as wanted: the list beside the pop-up is followed and framed as usual - unless the pop-up covers the item
            if (!CoveredByFilter(o, cr, U)) filterOpen = false;
            else { CloseFilterTip(o, fx, fy, U, H, Tx("filterCovered")); return; }
          } else if (FilterHasChoice()) {
            // the filter narrows the list now: closing the pop-up drops that again (live 2026-10-14, twice: the set went
            // from the filter and the item back from row 4 to row 18) - the list beside the pop-up is framed as usual,
            // unless the pop-up covers the item
            if (!CoveredByFilter(o, cr, U)) filterOpen = false;
            else { CloseFilterTip(o, fx, fy, U, H, Tx("filterCovered")); return; }
          } else if (!pilot) {
            // a filter the plan does not need over the list: the tip points at its ×
            CloseFilterTip(o, fx, fy, U, H, CloseFilterText);
            return;
          } else filterState = "filter_open";
        }
      }
      if (hintKind == "") { HideTip(); return; }
      // the next slot with the filter pop-up open: the pop-up covers the hero's slots, but its own row «Выбрать часть» switches
      // the list to that slot (the icons in slot order, measured at 1366x705 and 1920x1009)
      if (hintKind == "slot" && hintSlot >= 0 && hintSlot < PartIconX.Length && Mark(o, fg.X(fg.MainCloseX, W, H), fg.Y(fg.CloseY, W, H), U, ButtonCheck.IsCloseRed)) {
        int s = (int)(0.1 * U), cx = (int)fg.X(PartIconX[hintSlot], W, H), cy = (int)fg.Y(PartIconY, W, H);
        var pl = new System.Collections.Generic.List<string>(hintLines); if (pl.Count > 1) pl.RemoveAt(1); if (Tx("slotInFilter") != "") pl.Add(Tx("slotInFilter"));
        DrawTip(o.X + cx - s / 2, o.Y + cy - s / 2, s, s, pl.ToArray(), H);
        return;
      }
      if (hintKind == "replace") confirmBtn = ConfirmButton(o, W, H);
      int[] r = hintKind == "filter" ? hg.Filter(W, H) : hintKind == "replace" ? confirmBtn ?? hg.Replace(W, H) : hintKind == "slot" ? hg.Slot(hintSlot, W, H) : null;
      // the hero is open on another tab: its «Снаряжение» tab (right edge)
      if (hintKind == "geartab") { int s = (int)(0.1 * U); r = new[] { (int)(W - hg2.GearTabDX * U) - s / 2, (int)(hg2.GearTabY * U) - s / 2, s, s }; }
      if (r == null) HideTip(); else DrawTip(o.X + r[0], o.Y + r[1], r[2], r[3], hintLines, H, false, false, hintKind == "geartab", null, false, GuideStyle && hintKind == "replace" ? HlRound(o.X + r[0], o.Y + r[1], r[2], r[3], true) : null);   // «Заменить»: the player's own press, the edge only
    }

    // the filter pop-up's «Выбрать часть» icons: x of their middles from the window centre and y (in the pop-up's terms)
    static readonly double[] PartIconX = { -0.368, -0.2345, -0.0418, 0.0915, 0.2265 };
    const double PartIconY = 0.206;

    /// <summary>The frame's item cell (by the anchor) lies under the filter pop-up (the side panel included when open).</summary>
    bool CoveredByFilter(W32.POINT o, W32.RECT cr, double U) {
      if (!anchor.Has || types == null || frameRow <= 0 || frameCol <= 0) return false;
      double W = cr.R, H = cr.B, top = anchor.RowTop(g, types, frameRow, U), left = g.ColLeft(frameCol) * U, w = g.CellW * U;
      if (top + w < g.ViewTop * U || top > g.ViewBottom * U) return false;   // not in view anyway
      bool side = Mark(o, fg.X(fg.SideCloseX, W, H), fg.Y(fg.CloseY, W, H), U, ButtonCheck.IsCloseRed);
      double x0 = W / 2 - 0.452 * U, x1 = W / 2 + (side ? 0.87 : 0.335) * U;
      return left + w > x0 && left < x1;
    }

    // the tip at the filter pop-up's ×
    void CloseFilterTip(W32.POINT o, double fx, double fy, double U, double H, string text) {
      filterState = "filter_open";
      int rr = Math.Max(8, (int)(0.028 * U));
      DrawTip(o.X + (int)fx - rr, o.Y + (int)fy - rr, 2 * rr, 2 * rr, string.IsNullOrEmpty(text) ? new string[0] : new[] { text }, H);
    }

    /// <summary>The game's gear filter has a set or a main stat chosen now.</summary>
    bool FilterHasChoice() {
      long[] suits, mains, subs; int part; bool foreign, hideEq;
      return RFX.ReadFilter(addrs, out suits, out mains, out subs, out part, out foreign, out hideEq)
        && ((suits != null && suits.Length > 0) || (mains != null && mains.Length > 0));
    }

    // the frame's state while the filter pop-up is open: "" (none, or set as wanted), filter_open (not needed: close it),
    // filter_guide (the plan wants it: what to pick is lit)
    string filterState = "";
    long doneSince;
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
      if (screens.Hero != null) heroScreen = screens.Hero;   // the screen of this tick (ReadScreens)
      else if (heroScreen == null || now - heroReadAt >= 250 || now < heroReadAt) { heroScreen = RFX.ReadHeroScreen(addrs, heroScreen); heroReadAt = now; }
      var hs = heroScreen; double U = Ui.Unit(W, H);
      // the hero screen hidden behind another one (the inventory ...) keeps its hero set: it is not on, no hero hints over that screen
      if (hs == null || hs.Hero == 0 || !hs.FormOk) {
        heroShownWas = 0; heroAnchor.Has = false; heroDiag = "screen closed";
        if (HeroesButtonSeen(o, W, H)) {
          int s = (int)(0.1 * U);
          DrawTip(o.X + (int)(W - hg2.HeroesBtnDX * U) - s / 2, o.Y + (int)(H - 0.1156 * U) - s / 2, s, s, HeroTip(4), H, false, false, true);
        } else HideTip();
        return;
      }
      if (hs.Hero == hintHero) { heroShownWas = hs.Hero; heroDiag = "shown"; HideTip(); return; }
      // the list not readable now: the plate still says whom to press, beside the hero list
      int lx0 = (int)((hg2.Col1X - 0.06) * U), lvt = (int)(hg2.ViewTop * U), lw = (int)((2 * hg2.PitchX + 0.12) * U), lh = (int)(H - (1 - hg2.ViewBottom) * U) - lvt;
      if (hs.Heroes == null) { heroShownWas = hs.Hero; heroDiag = "no list"; DrawTipTop(HeroTip(1)); return; }
      if (hs.Part >= 0) {   // another hero's gear list covers the hero list: back
        heroShownWas = hs.Hero; heroAnchor.Has = false;
        int s = (int)(0.07 * U);
        DrawTip(o.X + (int)(hg2.BackX * U) - s / 2, o.Y + (int)(hg2.BackY * U) - s / 2, s, s, HeroTip(5), H, false, true);
        return;
      }
      int t = Array.IndexOf(hs.Heroes, hintHero);
      if (t < 0) { heroShownWas = hs.Hero; heroDiag = "not in the list"; DrawTipTop(HeroTip(1)); return; }
      double pitch = (hs.SmallCards ? hg2.SmallPitchY : hg2.PitchY) * U, card = (hs.SmallCards ? hg2.SmallCardH : hg2.CardH) * U;
      int vt = (int)(hg2.ViewTop * U), vb = (int)(H - (1 - hg2.ViewBottom) * U);
      int x0 = (int)((hg2.Col1X - 0.06) * U), x1 = (int)((hg2.Col1X + 2 * hg2.PitchX + 0.06) * U), w = x1 - x0, h = vb - vt;
      int[] px = h > 40 && w > 20 ? Grab(o.X + x0, o.Y + vt, w, h) : null;
      MaskOwn(px, o.X + x0, o.Y + vt, w, h);
      if (px == null) { HideTip(); return; }
      var xs = new int[6];
      for (int c = 0; c < 3; c++) { double cx = hg2.ColX(c) * U - x0; xs[2 * c] = (int)(cx - 0.045 * U); xs[2 * c + 1] = (int)(cx + 0.045 * U); }
      // the cards' gold side borders, line by line
      var prof = new double[h];
      int bw = Math.Max(1, (int)(0.012 * U));
      for (int y = 0; y < h; y++) {
        int n = 0, c = 0;
        for (int k = 0; k < 3; k++) {
          double cx = hg2.ColX(k) * U - x0;
          foreach (double e in new[] { cx - 0.057 * U, cx + 0.057 * U })
            for (int x = (int)(e - bw); x <= (int)(e + bw); x++) {
              if (x < 0 || x >= w) continue;
              int p = px[y * w + x], pr = (p >> 16) & 255, pg = (p >> 8) & 255, pb = p & 255;
              n++; if (pr >= 150 && pg >= 100 && pr >= pb + 50 && pg >= pb + 20) c++;
            }
        }
        prof[y] = n > 0 ? c / (double)n : 0;
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
        double r1 = heroOff.Place(top0 - heroY * pitch / (hs.SmallCards ? 85.0 : 125.0), hasPhase ? vt + ph : double.NaN, pitch, U);   // a card row is 125 (small: 85) of the list's units
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
      heroDiag = (exact ? "scroll=" + heroY.ToString("0") : "pixels") + " phase=" + (hasPhase ? (vt + ph).ToString("0") : "none") + " row1=" + (heroAnchor.Has ? heroAnchor.Row1Top.ToString("0") : "-") + " sure=" + sure + " index=" + t;
      if (!sure) { DrawTipTop(HeroTip(1)); return; }
      double ctop = heroAnchor.Row1Top + (t / HeroGeometry.Columns) * pitch, ccx = hg2.ColX(t % HeroGeometry.Columns) * U, cwid = 0.115 * U;
      if (ctop + card / 2 < vt) DrawTip(o.X + lx0, o.Y + lvt, lw, 4, HeroTip(2), H, true);
      else if (ctop + card / 2 > vb) DrawTip(o.X + lx0, o.Y + lvt + lh - 4, lw, 4, HeroTip(3), H, true);
      else DrawTip(o.X + (int)(ccx - cwid / 2), o.Y + (int)ctop, (int)cwid, (int)card, HeroTip(0), H, false, ctop < 0.35 * H);
    }
    bool heroFreshTop;
    string heroDiag = "";

    // ---------------------------------------------------------------- the hints' look: the game's gold frame around what to
    // press and a rectangular step plate beside it (the owl and «Шаг N из M · slot», the action in big white letters, what
    // comes next), a gold scroll strip with a pill when the item is out of view (src/HintLayout.cs places them)

    /// <summary>The page's texts for the plates (in the interface language): caps, pick, then, replace, pillDown, pillUp,
    /// rows1 / rows2 / rows5, unknown, unknownSub, done, wrong, wrongSub.</summary>
    public System.Collections.Generic.Dictionary<string, string> Texts = new System.Collections.Generic.Dictionary<string, string>();
    /// <summary>The player put on another item than the build's (the plate says so at the frame).</summary>
    public bool WrongItem;
    string Tx(string k) { var d = LocateWins && locateTexts.Count > 0 ? locateTexts : Texts; string v; return d != null && d.TryGetValue(k, out v) && v != null ? v : ""; }
    // «Показать в игре»: the page's texts for its pills (pillUp, pillDown, rows1 / rows2 / rows5, lang); the plan's Texts are not set while it runs
    Dictionary<string, string> locateTexts = new Dictionary<string, string>();

    sealed class Plate { public string Caps = "", Main = "", Sub = ""; public bool Pill, Warn; public int Arrow; /* -1 = an up arrow before the main text, 1 = down */ }
    // the game window's client area on the screen for the scene being drawn (plates are kept inside it)
    int sceneX, sceneY, sceneW, sceneH;
    void SceneArea(W32.POINT o, W32.RECT cr) { sceneX = o.X; sceneY = o.Y; sceneW = cr.R; sceneH = cr.B; }

    /// <summary>A pulsing gold frame around a button of the game (x, y, w, h: screen px) with the step plate beside it
    /// (plateOnly: the plate alone, beside that spot). below / right: kept for the callers, the plate finds its own side.</summary>
    /// <summary>A plate with no target on screen (nothing to frame): top centre of the game window, for every guide.</summary>
    void DrawTipTop(string[] lines, string caps = null) {
      if (lines == null || lines.Length == 0) { HideTip(); return; }
      Scene(tip, ref tipKey, null, new Plate { Caps = caps ?? Tx("caps"), Main = lines[0], Sub = lines.Length > 1 ? lines[1] : "" }, null, null);
    }

    void DrawTip(int x, int y, int w, int h, string[] lines, double H, bool plateOnly = false, bool below = false, bool right = false, string caps = null, bool warn = false, Hl hl = null) {
      var p = lines != null && lines.Length > 0 ? new Plate { Caps = caps ?? Tx("caps"), Main = lines[0], Sub = lines.Length > 1 ? lines[1] : "", Warn = warn } : null;
      Scene(tip, ref tipKey, plateOnly ? null : new[] { x, y, w, h }, p, new[] { x, y, w, h }, null, null, hl);
    }

    /// <summary>Draws one hint into <paramref name="win"/>: <paramref name="frameR"/> (screen px, null = no frame), the plate
    /// beside <paramref name="target"/>, and the scroll strip (null = none: {x, viewTop, viewBottom, dotY, b0, b1}, screen px)
    /// with the plate as a pill at its dot. <paramref name="key"/>: what is drawn now (unchanged = nothing to do).</summary>
    // the recorder: the frames of this tick's scene in client px (every tick, also when the picture is unchanged)
    void RecScene(GlowWindow win, int[] frameR, double[] strip, List<int[]> marks) {
      string pre = win == tip ? "tip-" : "";
      if (frameR != null) recB.Frames.Add(new RecFrameRect { Kind = pre + "frame", X = frameR[0] - sceneX, Y = frameR[1] - sceneY, W = frameR[2], H = frameR[3] });
      if (strip != null) recB.Frames.Add(new RecFrameRect { Kind = pre + "strip", X = (int)strip[0] - 12 - sceneX, Y = (int)strip[1] - sceneY, W = 24, H = (int)(strip[2] - strip[1]) });
      if (marks != null) foreach (var m in marks) recB.Frames.Add(new RecFrameRect { Kind = pre + "mark" + (m.Length > 4 ? m[4].ToString() : ""), X = m[0] - sceneX, Y = m[1] - sceneY, W = m[2], H = m[3] });
    }

    void Scene(GlowWindow win, ref string key, int[] frameR, Plate p, int[] target, double[] strip, List<int[]> marks = null, Hl hl = null) {
      if (!GameDrawable()) { if (win.Visible) win.Hide(); key = null; return; }
      if (recB != null) RecScene(win, frameR, strip, marks);
      float pulse = (float)(0.6 + 0.4 * Math.Sin(frame * 0.45));
      // the new highlight (switch guideStyle): a frame with a Hl is drawn by PaintGuide; off = the old frame, nothing below changes
      bool ng = GuideStyle && hl != null && frameR != null, dim = ng && !hl.Important;
      // the old frame (guideStyle off) follows a round / bevelled button's outline; rect shapes and targets without geometry keep the rectangle
      bool oldShape = !GuideStyle && hl != null && frameR != null && GuideShape.FrameOutline(hl.Rect, hl.Shape, hl.Inset, 2) != null;
      long tms = (uint)Environment.TickCount;
      guideLvl = (int)Math.Round(5 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * (tms % 1200) / 1200.0)));   // the pulse, 1.2 s, in 6 steps
      int bob = dim ? (int)Math.Round(2 + 2 * Math.Sin(2 * Math.PI * (tms % 1600) / 1600.0)) : 0;   // the hand's bob, 4 px
      Image hand = dim ? GuideHand.Get(GuideSite) : null;
      string k = (frameR != null ? string.Join(",", frameR) : "-") + "|" + (target != null ? string.Join(",", target) : "-") + "|"
        + (p != null ? p.Caps + "/" + p.Main + "/" + p.Sub + "/" + p.Pill + "/" + p.Warn : "-") + "|" + (strip != null ? string.Join(",", Array.ConvertAll(strip, v => ((int)v).ToString())) : "-")
        + "|" + (frameR != null || strip != null ? (ng ? 0 : (int)(pulse * 10)) : 0) + "|" + sceneW + "x" + sceneH
        + (ng ? "|g" + guideLvl + "," + bob + (hand != null ? "h" : "") + (hl.Important ? "i" : "") + (hl.Shape != null ? hl.Shape.K : "") : GuideStyle && marks != null && marks.Count > 0 ? "|g" + guideLvl : "")
        + (oldShape ? "|o" + hl.Shape.K + (int)hl.Rect.X + "," + (int)hl.Rect.Y + "," + (int)hl.Rect.W + "," + (int)hl.Rect.H : "")
        + (marks != null && marks.Count > 0 ? "|m" + string.Join(";", marks.ConvertAll(m => string.Join(",", m))) : "");
      if (k == key) return;
      key = k;
      double U = Ui.Unit(Math.Max(1, sceneW), Math.Max(1, sceneH));
      float pad = (float)Math.Max(8, 0.012 * U);
      using (var fCaps = new Font("Segoe UI", (float)Math.Max(10, 0.0155 * U), FontStyle.Bold, GraphicsUnit.Pixel))
      using (var fMain = new Font("Segoe UI", (float)Math.Max(14, (p != null && p.Pill ? 0.019 : 0.026) * U), FontStyle.Bold, GraphicsUnit.Pixel))
      using (var fSub = new Font("Segoe UI", (float)Math.Max(11, 0.018 * U), FontStyle.Regular, GraphicsUnit.Pixel)) {
        // the plate's size
        int pw = 0, ph = 0; float owl = fCaps.Height * 1.5f, capsH = 0, mainH = 0, subH = 0;
        if (p != null) {
          using (var probe = new Bitmap(1, 1)) using (var pg = Graphics.FromImage(probe)) {
            float cw = p.Caps != "" ? owl + 6 + pg.MeasureString(p.Caps.ToUpperInvariant(), fCaps).Width : 0;
            float mw = p.Main != "" ? pg.MeasureString(p.Main, fMain).Width : 0, sw = p.Sub != "" ? pg.MeasureString(p.Sub, fSub).Width : 0;
            capsH = p.Caps != "" ? Math.Max(owl, fCaps.Height) : 0; mainH = p.Main != "" ? fMain.Height : 0; subH = p.Sub != "" ? fSub.Height : 0;
            if (p.Arrow != 0 && p.Main != "") mw += fMain.Height * 0.8f + 10;   // the arrow before the text
            pw = (int)(Math.Max(cw, Math.Max(mw, sw)) + 2 * pad);
            ph = (int)(pad * (p.Pill ? 1.0f : 2f) + capsH + mainH + subH + (capsH > 0 ? 2 : 0) + (subH > 0 ? 2 : 0));
          }
        }
        // where the plate goes (client px), and the pointer toward the target
        int[] pos = null; char side = 'r'; double[] ptr = null;
        if (p != null) {
          if (strip != null) {   // a pill at the strip's dot, right of the strip (left when there is no room)
            int px = (int)strip[0] + 12 - sceneX, py = (int)(strip[3] - ph / 2.0) - sceneY;
            if (strip.Length > 6 && strip[6] > 0) { px = (int)strip[0] - 12 - pw - sceneX; if (px < HintLayout.Margin) px = (int)strip[0] + 12 - sceneX; }   // «Показать в игре»: left of the scroll bar (the card is right of it)
            else if (px + pw > sceneW - HintLayout.Margin) px = (int)strip[0] - 12 - pw - sceneX;
            pos = new[] { px, Math.Max(HintLayout.Margin, Math.Min(sceneH - ph - HintLayout.Margin, py)) };
          } else {
            var tc = target != null ? new[] { target[0] - sceneX, target[1] - sceneY, target[2], target[3] } : new[] { sceneW / 2, (int)(0.3 * sceneH), 0, 0 };
            if (target == null) pos = HintLayout.TopPlate(pw, ph, sceneW, sceneH, U);   // no target: top centre
            else {
              pos = HintLayout.Plate(tc, pw, ph, sceneW, sceneH, out side);
              if (frameR != null || tc[2] > 0) ptr = HintLayout.Pointer(pos, pw, ph, side, tc, Math.Max(7, 0.01 * U));
            }
          }
        }
        // the bitmap covers everything: the frame with its glow, the plate, the strip
        int G = (int)Math.Max(14, 0.022 * U);
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        Action<int, int, int, int> grow = (a, b, c, d) => { x0 = Math.Min(x0, a); y0 = Math.Min(y0, b); x1 = Math.Max(x1, c); y1 = Math.Max(y1, d); };
        int fm = frameR != null ? Math.Max(G, (int)(0.21 * Math.Max(frameR[2], frameR[3])) + 2) : 0;
        if (frameR != null) grow(frameR[0] - fm, frameR[1] - fm, frameR[0] + frameR[2] + fm, frameR[1] + frameR[3] + fm);
        if (dim) grow(sceneX, sceneY, sceneX + sceneW, sceneY + sceneH);   // the dimming covers the whole client
        if (pos != null) grow(sceneX + pos[0] - 16, sceneY + pos[1] - 16, sceneX + pos[0] + pw + 16, sceneY + pos[1] + ph + 16);
        if (strip != null) grow((int)strip[0] - 12, (int)strip[1] - 12, (int)strip[0] + 12, (int)strip[2] + 12);
        int Gm = (int)Math.Max(6, 0.008 * U);   // the glow of a cell frame among many: small, the neighbours are close
        if (marks != null) foreach (var m in marks) grow(m[0] - Gm - 4, m[1] - Gm - 4, m[0] + m[2] + Gm + 4, m[1] + m[3] + Gm + 4);
        if (x0 == int.MaxValue) { win.Hide(); return; }
        // clipped to the game's client rect: nothing is drawn outside the window
        if (sceneW > 0 && sceneH > 0) {
          x0 = Math.Max(x0, sceneX); y0 = Math.Max(y0, sceneY); x1 = Math.Min(x1, sceneX + sceneW); y1 = Math.Min(y1, sceneY + sceneH);
          if (x1 <= x0 || y1 <= y0) { win.Hide(); return; }
        }
        int bw = Math.Max(1, x1 - x0), bh = Math.Max(1, y1 - y0);
        using (var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb)) {
          using (var gr = Graphics.FromImage(bmp)) {
            gr.SmoothingMode = SmoothingMode.AntiAlias; gr.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; gr.Clear(Color.Transparent);
            gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (ng) PaintGuide(gr, hl, x0, y0, bw, bh, dim, G, hand, bob, U);
            else if (oldShape) PaintFrameShape(gr, hl, x0, y0, pulse, G);
            else if (frameR != null) PaintFrame(gr, frameR[0] - x0, frameR[1] - y0, frameR[2], frameR[3], pulse, G);
            if (strip != null) PaintStrip(gr, strip, x0, y0, pulse, U);
            if (marks != null) foreach (var m in marks) PaintMark(gr, m[0] - x0, m[1] - y0, m[2], m[3], m[4], Gm);
            if (p != null) {
              float px = sceneX + pos[0] - x0, py = sceneY + pos[1] - y0;
              PaintPlate(gr, px, py, pw, ph, p, fCaps, fMain, fSub, owl, capsH, mainH, pad, ptr, sceneX - x0, sceneY - y0);
            }
          }
          win.Put(bmp, x0, y0);
          win.Parts.Clear();
          if (frameR != null) win.Parts.Add(new[] { frameR[0] - fm, frameR[1] - fm, frameR[2] + 2 * fm, frameR[3] + 2 * fm });
          if (dim) win.Parts.Add(new[] { sceneX, sceneY, sceneW, sceneH });
          if (pos != null) win.Parts.Add(new[] { sceneX + pos[0] - 4, sceneY + pos[1] - 4, pw + 20, ph + 20 });
          if (strip != null) win.Parts.Add(new[] { (int)strip[0] - 14, (int)strip[1] - 14, 28, (int)(strip[2] - strip[1]) + 28 });
          if (marks != null) foreach (var m in marks) win.Parts.Add(new[] { m[0] - 4, m[1] - 4, m[2] + 8, m[3] + 8 });
        }
      }
    }

    /// <summary>Where our own windows showed something that a screen look sees (only when they cannot be hidden from
    /// captures): those pixels of a grab at (x, y) w × h are set to the dark background, so they are no bars, no glow, no cells.</summary>
    void MaskOwn(int[] px, int x, int y, int w, int h) {
      if (px == null) return;
      foreach (var win in new[] { glow, tip, recWin })
        if (!win.Excluded && win.Visible)
          foreach (var r in win.Parts) {
            int ax = Math.Max(x, r[0]), ay = Math.Max(y, r[1]), bx = Math.Min(x + w, r[0] + r[2]), by = Math.Min(y + h, r[1] + r[3]);
            for (int yy = ay; yy < by; yy++) for (int xx = ax; xx < bx; xx++) px[(yy - y) * w + (xx - x)] = ListTracker.Masked;
          }
    }

    // the game's gold frame (its texture), bolder: drawn twice over the cell with its glow, a gold line on the edge
    void PaintFrame(Graphics gr, float x, float y, float w, float h, float pulse, int G) {
      var tex = FrameTex();
      for (int i = G; i >= 4; i -= 3)
        using (var pen = new Pen(Color.FromArgb((int)(110 * pulse * (1 - i / (float)(G + 2))), 255, 190, 60), 3f)) gr.DrawPath(pen, Round(x - i, y - i, w + 2 * i, h + 2 * i, 6 + i));
      if (tex != null) {
        // the texture's gold line runs 14.5 % inside its picture: stretched so that line lies on the cell's edge
        float mx = w * 0.204f, my = h * 0.204f;
        var r = new RectangleF(x - mx, y - my, w + 2 * mx, h + 2 * my);
        gr.DrawImage(tex, r); gr.DrawImage(tex, r);
      }
      using (var pen = new Pen(Color.FromArgb(230, 40, 22, 4), 6f)) gr.DrawPath(pen, Round(x - 2, y - 2, w + 4, h + 4, 7));
      using (var pen = new Pen(Color.FromArgb(255, 255, 214, 110), 3.5f)) gr.DrawPath(pen, Round(x - 2, y - 2, w + 4, h + 4, 7));
    }

    // the old frame along a button's outline (circle / bevel / round): the same glow rings, dark underline and gold line as PaintFrame, same widths and pulse, on the art's polygon
    void PaintFrameShape(Graphics gr, Hl hl, int ox, int oy, float pulse, int G) {
      Func<double, GraphicsPath> poly = m => {
        var pts = GuideShape.FrameOutline(hl.Rect, hl.Shape, hl.Inset, m);
        var path = new GraphicsPath();
        if (pts == null) return path;
        var pf = new PointF[pts.Count];
        for (int i = 0; i < pf.Length; i++) pf[i] = new PointF((float)(pts[i].X - ox), (float)(pts[i].Y - oy));
        path.AddPolygon(pf);
        return path;
      };
      for (int i = G; i >= 4; i -= 3)
        using (var pen = new Pen(Color.FromArgb((int)(110 * pulse * (1 - i / (float)(G + 2))), 255, 190, 60), 3f) { LineJoin = LineJoin.Round })
        using (var path = poly(i)) gr.DrawPath(pen, path);
      using (var path = poly(2)) {
        using (var pen = new Pen(Color.FromArgb(230, 40, 22, 4), 6f) { LineJoin = LineJoin.Round }) gr.DrawPath(pen, path);
        using (var pen = new Pen(Color.FromArgb(255, 255, 214, 110), 3.5f) { LineJoin = LineJoin.Round }) gr.DrawPath(pen, path);
      }
    }

    // a cell frame among many (the storage cleanup): style 0 = to select (the game's gold frame, steady), 1 = selected (a thin
    // dim gold line with a check mark), 2 = selected but not on the list (red)
    void PaintMark(Graphics gr, float x, float y, float w, float h, int style, int G) {
      if (style == 0) {
        if (GuideStyle) PaintGuide(gr, HlCell(0, 0, 0, 0), 0, 0, 0, 0, false, G, null, 0, 0, new GeoRect { X = x, Y = y, W = w, H = h });   // storage cleanup: the gold edge of the new highlight
        else PaintFrame(gr, x, y, w, h, 1f, G);
        return;
      }
      if (style == 1) {
        using (var pen = new Pen(Color.FromArgb(200, 150, 130, 84), 2.5f)) gr.DrawPath(pen, Round(x - 1, y - 1, w + 2, h + 2, 7));
        float r = Math.Max(7f, Math.Min(w, h) * 0.12f), cx = x + w - r - 3, cy = y + r + 3;
        using (var bg = new SolidBrush(Color.FromArgb(215, 36, 30, 16))) gr.FillEllipse(bg, cx - r, cy - r, 2 * r, 2 * r);
        using (var pen = new Pen(Color.FromArgb(235, 214, 178, 98), 1.5f)) gr.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
        using (var pen = new Pen(Color.FromArgb(255, 240, 206, 120), Math.Max(2f, r * 0.22f))) {
          gr.DrawLines(pen, new[] { new PointF(cx - r * 0.5f, cy + r * 0.02f), new PointF(cx - r * 0.12f, cy + r * 0.4f), new PointF(cx + r * 0.52f, cy - r * 0.38f) });
        }
        return;
      }
      // style 2: unmistakable - a red tint over the whole cell, a thick red frame with a glow, a red cross badge in the corner
      for (int i = G; i >= 2; i -= 3)
        using (var pen = new Pen(Color.FromArgb((int)(150 * (1 - i / (float)(G + 2))), 255, 70, 50), 3f)) gr.DrawPath(pen, Round(x - i, y - i, w + 2 * i, h + 2 * i, 6 + i));
      using (var tint = new SolidBrush(Color.FromArgb(95, 230, 30, 24))) gr.FillPath(tint, Round(x - 2, y - 2, w + 4, h + 4, 7));
      using (var pen = new Pen(Color.FromArgb(240, 40, 6, 4), 9f)) gr.DrawPath(pen, Round(x - 2, y - 2, w + 4, h + 4, 7));
      using (var pen = new Pen(Color.FromArgb(255, 240, 56, 44), 6f)) gr.DrawPath(pen, Round(x - 2, y - 2, w + 4, h + 4, 7));
      float br = Math.Max(10f, Math.Min(w, h) * 0.17f), bx = x + w - br - 2, by = y + br + 2;
      using (var bg = new SolidBrush(Color.FromArgb(250, 190, 24, 20))) gr.FillEllipse(bg, bx - br, by - br, 2 * br, 2 * br);
      using (var pen = new Pen(Color.FromArgb(255, 255, 214, 204), 2f)) gr.DrawEllipse(pen, bx - br, by - br, 2 * br, 2 * br);
      using (var pen = new Pen(Color.White, Math.Max(2.5f, br * 0.24f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
        float d = br * 0.42f;
        gr.DrawLine(pen, bx - d, by - d, bx + d, by + d); gr.DrawLine(pen, bx + d, by - d, bx - d, by + d);
      }
    }

    // the step plate: strictly rectangular, dark, a thin bronze border with a gold line inside; the owl and the caps line,
    // the action in white, the next step smaller; a pill (the strip's «Вещь здесь») has rounded ends and one line
    void PaintPlate(Graphics gr, float x, float y, int w, int h, Plate p, Font fCaps, Font fMain, Font fSub, float owl, float capsH, float mainH, float pad, double[] ptr, int ox, int oy) {
      using (var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) gr.FillRectangle(shadow, x + 3, y + 5, w, h);
      if (p.Pill) {
        var path = Round(x, y, w, h, h / 2f - 0.5f);
        using (var bg = new SolidBrush(Color.FromArgb(240, 18, 16, 12))) gr.FillPath(bg, path);
        using (var pen = new Pen(Color.FromArgb(255, 224, 182, 96), 2f)) gr.DrawPath(pen, path);
      } else {
        // a warning plate (a stray item is ticked) is red: dark red body, a bright red border
        using (var bg = new SolidBrush(p.Warn ? Color.FromArgb(246, 52, 12, 12) : Color.FromArgb(242, 16, 18, 26))) gr.FillRectangle(bg, x, y, w, h);
        using (var pen = new Pen(p.Warn ? Color.FromArgb(255, 214, 54, 44) : Color.FromArgb(255, 122, 92, 46), p.Warn ? 3f : 2f)) gr.DrawRectangle(pen, x + 1, y + 1, w - 2, h - 2);
        using (var pen = new Pen(p.Warn ? Color.FromArgb(200, 255, 130, 110) : Color.FromArgb(170, 224, 182, 96), 1f)) gr.DrawRectangle(pen, x + 3.5f, y + 3.5f, w - 7, h - 7);
      }
      if (ptr != null) {
        var tri = new[] { new PointF((float)ptr[0] + ox, (float)ptr[1] + oy), new PointF((float)ptr[2] + ox, (float)ptr[3] + oy), new PointF((float)ptr[4] + ox, (float)ptr[5] + oy) };
        using (var br = new SolidBrush(Color.FromArgb(255, 224, 182, 96))) gr.FillPolygon(br, tri);
      }
      float ty = y + (p.Pill ? pad / 2 : pad), tx = x + pad;
      if (p.Caps != "") {
        var owlImg = OwlTex();
        if (owlImg != null) gr.DrawImage(owlImg, tx, ty, owl, owl);
        using (var gold = new SolidBrush(Color.FromArgb(255, 216, 176, 96)))
          gr.DrawString(p.Caps.ToUpperInvariant(), fCaps, gold, tx + owl + 6, ty + (owl - fCaps.Height) / 2);
        ty += capsH + 2;
      }
      if (p.Main != "") {
        if (p.Arrow != 0) {   // the direction of the item: a gold arrow before the text
          float aw = mainH * 0.8f, acx = tx + aw / 2, acy = ty + mainH / 2, ah = mainH * 0.6f, dir = p.Arrow < 0 ? -1 : 1;
          var tri = new[] { new PointF(acx, acy + dir * ah / 2), new PointF(acx - aw / 2, acy - dir * ah / 2), new PointF(acx + aw / 2, acy - dir * ah / 2) };
          using (var br = new SolidBrush(Color.FromArgb(255, 255, 214, 110))) gr.FillPolygon(br, tri);
          using (var pen = new Pen(Color.FromArgb(230, 60, 36, 6), 1.5f)) gr.DrawPolygon(pen, tri);
          tx += aw + 10;
        }
        using (var sh = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) gr.DrawString(p.Main, fMain, sh, tx + 1, ty + 1.5f);
        using (var white = new SolidBrush(p.Warn ? Color.FromArgb(255, 255, 196, 184) : p.Pill ? Color.FromArgb(255, 255, 222, 150) : Color.White)) gr.DrawString(p.Main, fMain, white, tx, ty);
        ty += mainH + 2;
      }
      if (p.Sub != "") using (var muted = new SolidBrush(Color.FromArgb(255, 207, 198, 176))) gr.DrawString(p.Sub, fSub, muted, tx, ty);
    }

    // the scroll strip along the list: a thin gold line over the visible part, a bracket for where the view is in the whole
    // list and a gold dot where the item is
    void PaintStrip(Graphics gr, double[] s, int ox, int oy, float pulse, double U) {
      float x = (float)s[0] - ox, top = (float)s[1] - oy, bottom = (float)s[2] - oy, dot = (float)s[3] - oy, b0 = (float)s[4] - oy, b1 = (float)s[5] - oy;
      using (var line = new Pen(Color.FromArgb(150, 224, 182, 96), 2f)) gr.DrawLine(line, x, top, x, bottom);
      using (var br = new Pen(Color.FromArgb(255, 240, 200, 110), 2f)) gr.DrawPath(br, Round(x - 4, b0, 8, Math.Max(8, b1 - b0), 4));
      float r = (float)Math.Max(5, 0.008 * U);
      for (int i = 3; i >= 1; i--) using (var glowB = new SolidBrush(Color.FromArgb((int)(60 * pulse), 255, 190, 60))) gr.FillEllipse(glowB, x - r - 2 * i, dot - r - 2 * i, 2 * (r + 2 * i), 2 * (r + 2 * i));
      using (var g2 = new SolidBrush(Color.FromArgb(255, 255, 214, 110))) gr.FillEllipse(g2, x - r, dot - r, 2 * r, 2 * r);
      using (var pen = new Pen(Color.FromArgb(230, 60, 36, 6), 1.5f)) gr.DrawEllipse(pen, x - r, dot - r, 2 * r, 2 * r);
    }

    static Image owlTex;
    static Image OwlTex() {
      if (owlTex == null) {
        try { using (var s = typeof(OverlayController).Assembly.GetManifestResourceStream("overlay/owl.png")) if (s != null) owlTex = new Bitmap(Image.FromStream(s)); }
        catch (Exception) { }
      }
      return owlTex;
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
      // the click gate stopped the equip pilot (an unexpected screen, no effect of a click ...): hints only until the next «Надеть»
      if (gate.Halted(ClickOwner.Equip)) { AutoSay("gate_" + gate.HaltReason(ClickOwner.Equip)); return; }

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
        if (screens.Hero != null) heroScreen = screens.Hero;   // the screen of this tick (ReadScreens)
        // the gear filter pop-up left open (the last item's) stays over the hero grid and takes its clicks: closed first
        if (heroScreen.FormOk && heroScreen.Hero > 0 && heroScreen.Hero != runHero && v.Foreground && !v.UserBusy && now >= slotCloseAfter
            && Mark(o, fg.X(fg.MainCloseX, cr.R, cr.B), fg.Y(fg.CloseY, cr.R, cr.B), Ui.Unit(cr.R, cr.B), ButtonCheck.IsCloseRed)) {
          slotCloseAfter = now + 900;
          Log.Write("hero: a pop-up (the gear filter or an item card) is over the grid - closing it");
          EquipSend(ClickTarget.EquipClosePopup, new AutoAction { Kind = AutoKind.Click }, o.X + (int)Math.Round(fg.X(fg.MainCloseX, cr.R, cr.B)), o.Y + (int)Math.Round(fg.Y(fg.CloseY, cr.R, cr.B)), o, v.Foreground);
          return;
        }
        var hv = new HeroView {
          NowMs = now, Foreground = v.Foreground, UserBusy = v.UserBusy, UserClicked = v.UserClicked, W = cr.R, H = Ui.Unit(cr.R, cr.B), ClientH = cr.B,
          Plan = runHero, Hero = heroScreen.FormOk ? heroScreen.Hero : 0, Tab = heroScreen.Tab, Part = heroScreen.Part, SmallCards = heroScreen.SmallCards,
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
          EquipSend(hv.FilterOpen ? ClickTarget.EquipFilter : ClickTarget.EquipNav, ha, hx, hy, o, v.Foreground);
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
        EquipSend(ClickTarget.EquipFilter, fa, fx, fy, o, v.Foreground);
        return;
      }
      // the next slot: an open filter pop-up covers the hero's slots - close it first (the filter was for the item that is
      // on now; the next one gets its own)
      if (v.Slot != null && v.Foreground && !v.UserBusy && now >= slotCloseAfter
          && Mark(o, fg.X(fg.MainCloseX, cr.R, cr.B), fg.Y(fg.CloseY, cr.R, cr.B), Ui.Unit(cr.R, cr.B), ButtonCheck.IsCloseRed)) {
        slotCloseAfter = now + 900; pilot.Reset();
        int cx = o.X + (int)Math.Round(fg.X(fg.MainCloseX, cr.R, cr.B)), cy = o.Y + (int)Math.Round(fg.Y(fg.CloseY, cr.R, cr.B));
        EquipSend(ClickTarget.EquipClosePopup, new AutoAction { Kind = AutoKind.Click }, cx, cy, o, v.Foreground);
        return;
      }
      var a = pilot.Step(v);
      AutoSay(pilot.State);
      if (a.Kind == AutoKind.None) return;
      int x = o.X + (int)Math.Round(a.X), y = o.Y + (int)Math.Round(a.Y), y2 = y + (int)Math.Round(a.DY);
      if (x < o.X || y < o.Y || x >= o.X + cr.R || y >= o.Y + cr.B) return;   // never outside the game's client area
      if (y2 < o.Y || y2 >= o.Y + cr.B) return;
      // «Заменить» (the player's auto confirm) is its own target; everything else of the item pilot is the gear list
      bool onConfirm = confirmBtn != null && a.X >= confirmBtn[0] && a.X <= confirmBtn[0] + confirmBtn[2] && a.Y >= confirmBtn[1] && a.Y <= confirmBtn[1] + confirmBtn[3];
      EquipSend(confirm && onConfirm ? ClickTarget.EquipConfirm : ClickTarget.EquipNav, a, x, y, o, v.Foreground);
    }

    // Storage cleanup, in the inventory: the city's «Инвентарь» → «Снаряжение» → «Массовая продажа», then the sell
    // pilot selects the items. Only known screens are clicked; elsewhere the player is asked to open the city.
    void TickSell(W32.POINT o, W32.RECT cr, long now, bool front, bool busy, bool userClicked) {
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      // the screen of this tick (ReadScreens) and the click gate (src/ClickGate.cs): the pilot stops on anything unexpected and clicks
      // only what the whitelist of the screen allows (in «Массовая продажа»: the list's cells the run is after, nothing else)
      var scr = screens.Current;
      if (gate.Halted(ClickOwner.Sell)) { SellSay("gate_" + gate.HaltReason(ClickOwner.Sell)); return; }
      if (front && !busy) {
        var pre = gate.Preflight(ClickOwner.Sell, scr, now);
        AnnounceHalts();
        if (pre.Verdict == GateVerdict.Deny) return;
        if (pre.Verdict == GateVerdict.Wait) { if (pre.Reason == "unknown") SellSay("no_city"); return; }   // pending: the last click's effect is awaited
      }
      var inv = screens.Inv;
      if (scr != null && scr.Kind == ScreenKind.Inventory && inv != null) {
        if (!inv.EquipTab || !inv.Bulk) {
          SellSay(front ? "open" : "background");
          if (!front || busy || now - sellBtnAt < 2000) return;
          sellBtnAt = now;
          // the left menu's «Снаряжение» (top left); «Массовая продажа» under the list (bottom left)
          var a0 = !inv.EquipTab ? new AutoAction { Kind = AutoKind.Click, X = 0.085 * U, Y = 0.315 * U }
                                 : new AutoAction { Kind = AutoKind.Click, X = 0.5867 * U, Y = H - 0.0723 * U };
          SendInside(a0, o, cr, ClickOwner.Sell, !inv.EquipTab ? ClickTarget.InvEquipTab : ClickTarget.InvBulkEnter, front);
          return;
        }
        int cols = inv.List != null && inv.List.Cols > 0 ? inv.List.Cols : front && !busy ? InvColumns(o, cr, U) : 0;
        if (cols > 0) sellCols = cols;
        if (sellGeoW != W || sellGeoH != H || sellGeoCols != sellCols) {
          sellGeoW = W; sellGeoH = H; sellGeoCols = sellCols;
          spilot.Geometry = SellGeometry.Inventory(W, U, sellCols);
        }
        // the rows' place: the game's own scroll reading (the picture only learns its constant; it is the last resort without it)
        double sph = front && !busy && sellScroll.WantPhase(now) ? SellPhase(o, cr, U) : double.NaN;
        double srow1 = sellScroll.Row1(spilot.Geometry, inv.List, sph, U);
        if (double.IsNaN(srow1)) { sph = front && !busy ? (double.IsNaN(sph) ? SellPhase(o, cr, U) : sph) : double.NaN; if (gridSaid != "sellpilot:px") { gridSaid = "sellpilot:px"; Log.Write("sell: the inventory's scroll cannot be read - the picture is the fallback (the gate will not let a cell be clicked without the exact scroll)"); } }
        else sph = double.NaN;
        var a = spilot.Step(new SellView { NowMs = now, Foreground = front, UserBusy = busy, UserClicked = userClicked, W = W, H = H, U = U,
                                           Screen = inv.AsSell(sellCols), Wanted = sellWanted, Phase = sph, Row1 = srow1 });
        SellSay(spilot.State);
        if (spilot.Trace != "") { Log.Write("sell: " + spilot.Trace); spilot.Trace = ""; }
        if (a.Kind != AutoKind.None) {
          // a cell click must hit a cell of an item the run is after (or one it ticked, to take it off again): the item under the
          // click point from the fresh list order and the game's exact scroll
          int ccols = sellCols; var geo = spilot.Geometry;
          SendInside(a, o, cr, ClickOwner.Sell, a.Kind == AutoKind.Drag ? ClickTarget.InvListDrag : ClickTarget.InvBulkCell, front, (rq, fr) => {
            rq.CellUid = ClickGate.InvCellUid(fr.Inv, geo, sellScroll.Row1(geo, fr.Inv != null ? fr.Inv.List : null, double.NaN, U, now), ccols, rq.X, rq.Y, W, U);
            var ok = new System.Collections.Generic.HashSet<long>(sellWanted);
            if (fr.Inv != null) foreach (var u in fr.Inv.Selected) ok.Add(u);
            rq.CellOk = ok;
          });
        }
        return;
      }
      if (!front || busy) { SellSay(front ? "paused" : "background"); return; }
      // the hero screen (the gear screen as well): back to the city with the arrow top left
      if (scr != null && scr.Kind == ScreenKind.HeroScreen) {
        SellSay("open");
        if (now - sellBtnAt < 2000) return;
        sellBtnAt = now;
        SendInside(new AutoAction { Kind = AutoKind.Click, X = hg2.BackX * U, Y = hg2.BackY * U }, o, cr, ClickOwner.Sell, ClickTarget.HeroBack, front);
        return;
      }
      // the city: «Инвентарь» in the bottom bar (found by its picture, as «Герои»)
      if (scr != null && scr.Kind == ScreenKind.City && InventoryButtonSeen(o, W, H)) {
        SellSay("open");
        if (now - sellBtnAt < 2500) return;
        sellBtnAt = now;
        SendInside(new AutoAction { Kind = AutoKind.Click, X = W - invDX * U, Y = H - hg2.HeroesBtnDY * U }, o, cr, ClickOwner.Sell, ClickTarget.CityInventory, front);
        return;
      }
      SellSay("no_city");
    }

    // Storage cleanup without auto click: nothing is clicked. The inventory's «Массовая продажа» grid is read (as the sell
    // pilot reads it), the rows' place on the screen is followed like «Найти в игре» does, and every list item in view gets
    // a gold frame; the ones already selected a dim checked one, a selected item that is not on the list a red one.
    void SellHintHide() { if (glow.Visible) glow.Hide(); drawnKey = null; HideTip(); }

    readonly FrameSmoother shSmooth = new FrameSmoother(), locSmooth = new FrameSmoother(), locListSmooth = new FrameSmoother();

    void TickSellHint() {
      IntPtr hwnd = GameWindow();
      W32.RECT cr = new W32.RECT();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { SellHintHide(); return; }
      bool front = IsForeground(hwnd);
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      SceneArea(o, cr);
      long now = NowMs();
      // the game's own reading of the inventory form, EVERY tick (not the 200 ms full read): is the screen on now, and the exact
      // scroll / columns of its list. The frames are placed from this alone; no picture decides where an item is.
      bool formActive; var live = RFX.ReadInventoryLive(out formActive);
      if (live != null && live.Cols > 0 && live.Cols != shCols) shCols = live.Cols;
      if (shGeo == null || shGeoW != W || shGeoH != H || shGeoCols != shCols) {
        shGeoW = W; shGeoH = H; shGeoCols = shCols; shGeo = SellGeometry.Inventory(W, U, shCols);
        sgrid.Reset();
      }
      // the inventory, read a few times a second (the form the game never built is searched for at most every 8 s). A new
      // reading is taken over when the next one says the same (a selection that flickers in one read does not show)
      if (!shHave || Math.Abs(now - shReadAt) >= 200) {
        shReadAt = now;
        var raw = screens.Inv;   // the screen of this tick (ReadScreens)
        bool bulk = raw != null && raw.Open && raw.EquipTab && raw.Bulk;
        if (bulk) {
          // the columns: the game's own grid says (the picture only when it cannot be read)
          int c = raw.List != null && raw.List.Cols > 0 ? raw.List.Cols : front ? InvColumns(o, cr, U) : 0;
          if (c > 0 && c != shCols) { shCols = c; shGeoCols = -1; }
        }
        if (bulk && sgrid.ListPtr != raw.ListPtr) sgrid.Reset();
        string sig = SellHint.Classify(raw, sellWanted).StateKey;
        if (!shHave || sig == shReadSig) { shInv = raw; shHave = true; }
        shReadSig = sig;
      }
      // a screen the game no longer shows is not read from the old reading: the step falls back to «open the inventory»
      var inv = formActive && screens.Current != null && screens.Current.Kind == ScreenKind.Inventory ? shInv : null;
      bool inBulk = inv != null && inv.Open && inv.EquipTab && inv.Bulk;
      if (inBulk && sgrid.ListPtr != inv.ListPtr) sgrid.Reset();
      // row 1's place: the game's exact scroll, nothing else. Unreadable = no item frames at all (the text plate and the counts
      // stay), never guessed ones
      double row1 = double.NaN;
      if (inBulk && front && shGeo != null && live != null && live.Has) {
        inv.List = live;
        if (FollowInventoryGrid(sgrid, sellScroll, shGeo, inv, o, cr, U, now, "sell")) row1 = sgrid.Row1;
      }
      // the drawn row 1 eases toward the scroll's value (no jumping between old and new reads)
      row1 = shSmooth.Step(row1, now, 4 * shGeo.PitchY * U);
      if (recB != null) recB.InvSmooth = row1;
      if (inBulk && front) {
        string why = !double.IsNaN(row1) ? "exact" : "none";
        if (why != shFrameSaid) { shFrameSaid = why; if (why == "none") Log.Write("sell hint: the list's exact scroll cannot be read - no item frames, only the text plate"); }
      }
      var step = SellHint.BuildExact(inv, sellWanted, shCols, row1, shGeo, W, H, U);
      // the screen is Unknown (a form is being searched for): the last step and its plate stay for up to 15 s, without frames
      var scrS = screens.Current;
      if (scrS != null && scrS.Kind == ScreenKind.Unknown) { if (shUnkSince < 0) shUnkSince = now; } else shUnkSince = -1;
      shHold = shLast != null && scrS != null && scrS.Kind == ScreenKind.Unknown && shUnkSince >= 0 && now - shUnkSince < LocateGuide.HoldUnknownMs;
      if (shHold) step = shLast; else shLast = step;
      if (recB != null) { recB.SellInv = inv; recB.Step = "Sell:" + step.Kind; }
      shFrames = shHold ? new List<SellFrame>() : step.Frames;
      string key = step.FullKey;
      if (key != shKey) { shKey = key; var h = SellHintChanged; if (h != null) h(step); }

      if (!GameDrawable(true)) { SellHintHide(); return; }
      bool haveText = sellHintLines.Length > 0;
      double vt = shGeo.ViewTopPx(U), vb = shGeo.ViewBottomPx(H, U);
      int gx = (int)shGeo.X(shGeo.X0, W, U), gw = (int)(shGeo.PitchX * (shCols - 1) * U + shGeo.CellW * U);
      if (!shHold && (step.Kind == SellHintKind.Select || step.Kind == SellHintKind.AllSelected)) {
        var marks = new List<int[]>();
        foreach (var fr in step.Frames) marks.Add(new[] { o.X + fr.X, o.Y + fr.Y, fr.W, fr.H, fr.Style == "todo" ? 0 : fr.Style == "done" ? 1 : 2 });
        marks.Sort((p, q) => p[4].CompareTo(q[4]));   // a stray (red) item is painted last, on top
        // list items out of view: a pill with an arrow at the list's edge (as the equip helper's «Вещь здесь ↓»)
        Plate pill = null; int[] pt = null;
        if (step.Below > 0 && sellPillDown != "") { pill = new Plate { Pill = true, Main = sellPillDown }; pt = new[] { o.X + gx, o.Y + (int)vb - 2, (int)(shGeo.CellW * U), 2 }; }
        else if (step.Above > 0 && sellPillUp != "") { pill = new Plate { Pill = true, Main = sellPillUp }; pt = new[] { o.X + gx, o.Y + (int)vt, (int)(shGeo.CellW * U), 2 }; }
        Scene(glow, ref drawnKey, null, pill, pt, null, marks);
        // the step's plate above the list
        if (haveText) DrawTip(o.X + gx, o.Y + (int)vt - 2, gw, 2, sellHintLines, H, true, false, false, null, SellHint.Warn(step)); else HideTip();
        return;
      }
      if (glow.Visible) glow.Hide();
      drawnKey = null;
      if (!haveText) { HideTip(); return; }
      if (shHold) { DrawTipTop(sellHintLines); return; }   // held: the plate only
      if (step.Kind == SellHintKind.PressBulk) {
        // «Массовая продажа» under the list (bottom left): where the auto mode clicks
        var pb = SellHint.PressBulkRect(W, H, U);
        frame++;
        DrawTip(o.X + pb[0], o.Y + pb[1], pb[2], pb[3], sellHintLines, H);
        return;
      }
      if (step.Kind == SellHintKind.NeedPosition) { DrawTipTop(sellHintLines); return; }
      int[] cityBtn;
      if (step.Kind == SellHintKind.OpenInventory && CityInventoryButton(o, W, H, U, out cityBtn)) {   // the city: frame the bar's «Инвентарь»
        frame++;
        DrawTip(cityBtn[0], cityBtn[1], cityBtn[2], cityBtn[3], sellHintLines, H, false, false, false, null, false, GuideStyle ? null : HlCity(cityBtn));
        return;
      }
      DrawTipTop(sellHintLines);   // no target on screen: the plate at the top centre
    }

    // ---------------------------------------------------------------- «Найти в игре»

    LocateRequest locateReq; string[] locateLines = new string[0]; string locateCaps = "";
    readonly LocateGrid lgrid = new LocateGrid();
    LocateStep locateStep; SubCheck locateSubs; string locateKey = "";
    string locateScreensSaid = "", locateMarkerSaid = "", locateCardSaid = "", locateListSig = "", locateDecisionSaid = "", locatePlaceSaid = ""; long locateListChangeAt;   // the Ascension list's shape (to wait after it changed), the log keys
    long locateReadAt, locateLastCur = -1, locateSubsAt; int locateCols = 8, locateGeoCols; double locateGeoW, locateGeoH;
    SellGeometry locateGeo; InventoryScreen locateInv; bool locateNeedClick;
    /// <summary>The step changed (or its list place / sub stat check): the page shows it and answers with the text plate.</summary>
    public event Action<LocateStep, SubCheck> LocateChanged;

    void LocateHide() { if (glow.Visible) glow.Hide(); drawnKey = null; HideTip(); }

    // the job is done in the game (src/LocateGuide.cs LocateFinish): a short plate, the request is reported (LocateFinished), then the hints end
    readonly LocateFinish lfin = new LocateFinish(); long[][] locateLive; long locateDoneUntil; string locateDoneWhat = "";
    /// <summary>The job of the request is done in the game: request id and what ("ascend" | "recast").</summary>
    public event Action<string, string> LocateFinished;

    void FinishLocateJob(string what, long now) {
      var req = locateReq; if (req == null) return;
      Log.Write("locate: job done (" + what + ") for item " + req.Item.Uid);
      locateDoneUntil = now + 4000; locateDoneWhat = what; locateDriving = false; lpilot.Cancel();
      var h = LocateFinished; if (h != null) h(req.Id, what);
    }

    void TickLocateDone(string what) {
      if (NowMs() >= locateDoneUntil) { SetLocate(null); return; }
      IntPtr hwnd = GameWindow(); W32.RECT cr;
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || !IsForeground(hwnd) || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { LocateHide(); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o); SceneArea(o, cr);
      if (glow.Visible) glow.Hide(); drawnKey = null;
      string txt = Tx(what == "ascend" ? "doneAscend" : "doneRecast");
      DrawTipTop(new[] { txt != "" ? txt : (what == "ascend" ? "Готово: вещь вознесена" : "Готово: вещь преобразована") }, locateCaps);
    }

    void TickLocate() {
      if (locateDoneUntil != 0) { TickLocateDone(locateDoneWhat); return; }
      var req = locateReq;
      IntPtr hwnd = GameWindow();
      W32.RECT cr = new W32.RECT();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { LocateHide(); return; }
      bool front = IsForeground(hwnd);
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      SceneArea(o, cr);
      long now = NowMs();
      long uid = req.Item.Uid;
      // the screen is Unknown (a form is being searched for): the last step and its plate stay for up to 15 s, no frames (the positions are unknown)
      var scrNow = screens.Current;
      if (scrNow != null && scrNow.Kind == ScreenKind.Unknown) { if (locUnkSince < 0) locUnkSince = now; } else locUnkSince = -1;
      bool hold = LocateGuide.HoldOnUnknown(locateStep, scrNow != null ? scrNow.Kind : ScreenKind.City, locUnkSince, now);
      // the game's screens, read a few times a second (the forms are searched for off this thread, see ReadScreens)
      if (!hold && (locateStep == null || Math.Abs(now - locateReadAt) >= 300)) {
        locateReadAt = now;
        locateInv = screens.Inv;   // the screens are read once per tick (ReadScreens), a short failure held for 1.5 s
        var rs = screens.Rs;
        locateRs = rs; locateOnAccount = RFX.ItemOnAccount(uid);
        {   // which screens are seen, once per change within a request
          string scr = "inv=" + (locateInv != null && locateInv.Open ? "open" : locateInv != null ? "closed" : "none") + " foundry=" + (rs == null ? "none" : rs.SublimingOpen ? "open(tab " + rs.CurType + ")" : "closed")
            + " ascendTab=" + (rs != null && rs.OnAscend) + " gem=" + (rs == null ? "none" : rs.GemOpen ? "open(func " + rs.GemFunc + ")" : "closed");
          if (scr != locateScreensSaid) { locateScreensSaid = scr; Log.Write("locate: screens " + scr); }
        }
        // the columns: the game's own grid says (the picture only when it cannot be read)
        if (locateInv != null && locateInv.Open && locateInv.EquipTab) {
          if (locateInv.List != null && locateInv.List.Cols > 0) locateCols = locateInv.List.Cols;
          else if (front) { int c = InvColumns(o, cr, U); if (c > 0) locateCols = c; }
        }
        if (screens.Hero != null) heroScreen = screens.Hero;
        var st = LocateGuide.Next(req, locateInv, rs, heroScreen, locateCols, locateOnAccount);
        LogListRead("foundry", rs != null && rs.OnAscend ? rs.AscendList : rs != null && rs.OnRecast ? rs.RecastList : null);
        // the Ascension list just changed (a slot tab was pressed, the tab opened): the game rebuilds it a moment later, so no filter advice yet
        if (rs != null && rs.OnAscend) {
          string sig = rs.AscendEquips.Length + "|" + string.Join(",", Array.ConvertAll(rs.AscendParts, x => x.ToString())) + "|" + (rs.AscendEquips.Length > 0 ? rs.AscendEquips[0].ToString() : "");
          if (sig != locateListSig) { locateListSig = sig; locateListChangeAt = now; }
          if ((st.Kind == LocateKind.AscendFilter || st.Kind == LocateKind.AscendNotInList) && now - locateListChangeAt < 1200) st.Kind = LocateKind.AscendLoading;
          string dec = st.Kind + "|" + st.Pos + "|" + string.Join(",", st.Missing) + "|" + st.FilterGo + "|" + sig;
          if (dec != locateDecisionSaid) {
            locateDecisionSaid = dec;
            Log.Write("locate decision: " + st.Kind + " inList=" + (st.Pos > 0) + " listLen=" + rs.AscendEquips.Length + " slotSel=[" + string.Join(",", Array.ConvertAll(rs.AscendParts, x => x.ToString())) + "] itemSlot=" + req.Item.Slot
              + " missing=[" + string.Join(",", st.Missing) + "] popup=" + rs.AscendFilterOpen + "/" + rs.FilterPanel + " go=" + st.FilterGo + " list=" + (rs.AscendList != null && rs.AscendList.Has ? "ok" : "none"));
          }
        }
        if (Math.Abs(now - locateSubsAt) >= 1500 && (st.Kind == LocateKind.Selected || st.Kind == LocateKind.Done || st.Kind == LocateKind.RecastPick || st.Kind == LocateKind.AscendPick || st.Kind == LocateKind.AscendReady || st.Kind == LocateKind.InList || lfin.DoneSeen)) {
          locateSubsAt = now;
          var live = RFX.ReadLiveItemSubs(uid);
          locateLive = live;
          locateSubs = live != null ? LocateGuide.CheckSubs(req.Item, live) : null;
        }
        // the job itself done (ascended: the item left the Ascension list; recast: its sub stats changed / a result waits): report it
        string fin = lfin.Update(req.Purpose == "ascend", uid, st.Kind, rs, locateLive, now);
        if (fin != "") { FinishLocateJob(fin, now); return; }
        st.NeedClick = locateNeedClick;
        locateStep = st;
      }
      var step = locateStep;
      if (recB != null) recB.Step = step.Kind.ToString();
      TickLocateDrive(step, req, hwnd, o, cr, now, W, H, U);   // auto click on: the app opens the item itself
      FoundryScrollNow(step, now);
      // the step's frame from the screen geometry (the site's file, src/LocateFrames.cs): «Литейная», the tabs, the slot, the list cell, the final button
      LocateFrame lf = front && !hold && step.Kind != LocateKind.InList ? LocateFrames.For(step, req.Item, locateRs, null, Geo.Current, W, H, U) : null;
      if (lf != null && step.Kind == LocateKind.Selected) {   // the card button's rect, once per change (to measure against a frame diagnostics capture)
        string csig = lf.Target + "|" + lf.Mark.X + "," + lf.Mark.Y + "," + lf.Mark.W + "," + lf.Mark.H;
        if (csig != locateCardSaid) { locateCardSaid = csig; Log.Write("locate card frame: " + lf.Target + " rect=" + lf.Mark.X + "," + lf.Mark.Y + " " + lf.Mark.W + "x" + lf.Mark.H + " window=" + (int)W + "x" + (int)H + " U=" + (int)U + " worn=" + step.Worn); }
      }
      if (step.Kind == LocateKind.AscendPick && locatePlaceSaid != step.Pos + "|" + (lf != null)) {
        locatePlaceSaid = step.Pos + "|" + (lf != null);
        Log.Write("locate " + LocateFrames.PlacementInputs(step, locateRs, Geo.Current, W, H, U) + " -> " + (lf == null ? "no frame" : lf.Mark.Kind + " " + lf.Mark.X + "," + lf.Mark.Y + " " + lf.Mark.W + "x" + lf.Mark.H));
      }
      // the inventory's list: where the item's cell is on the screen
      locateNeedClick = false;
      if (front && !hold && step.Kind == LocateKind.InList && locateInv != null) {
        var inv = locateInv;
        if (locateGeo == null || locateGeoW != W || locateGeoH != H || locateGeoCols != locateCols) {
          locateGeoW = W; locateGeoH = H; locateGeoCols = locateCols; locateGeo = SellGeometry.Inventory(W, U, locateCols);
          lgrid.Reset();
        }
        if (lgrid.ListPtr != inv.ListPtr) lgrid.Reset();
        // where the game says the list is scrolled: exact, no click needed (read anew every frame we draw, not with the 300 ms look)
        bool liveShown; var liveList = RFX.ReadInventoryLive(out liveShown);
        if (liveList != null && liveList.Has) inv.List = liveList;
        bool exactGrid = FollowInventoryGrid(lgrid, locateScroll, locateGeo, inv, o, cr, U, now, "locate");
        if (exactGrid) { lgrid.Row1 = locSmooth.Step(lgrid.Row1, now, 4 * locateGeo.PitchY * U); if (recB != null) recB.InvSmooth = lgrid.Row1; } else locSmooth.Reset();
        if (inv.Current != locateLastCur) {
          // the player clicked a cell: the pointer shows where its row is (the last resort, when the game's scroll is unreadable)
          if (!exactGrid && locateLastCur != -1 && inv.Current > 0) {
            var p = LocateGuide.Position(inv.Uids.ToArray(), inv.Current, locateCols);
            W32.POINT c;
            if (p != null && W32.GetCursorPos(out c) && c.Y - o.Y >= locateGeo.ViewTopPx(U) && c.Y - o.Y <= locateGeo.ViewBottomPx(H, U)) lgrid.FromClick(locateGeo, p[1], c.Y - o.Y, U, inv.ListPtr);
          }
          locateLastCur = inv.Current;
        }
        if (!exactGrid) {   // last resort: the stat bars on the screen
          double phase = PhaseCached(locateGeo, o, cr, U, now);
          bool hasPhase = !double.IsNaN(phase);
          if (!lgrid.Has && hasPhase) lgrid.FromTop(locateGeo, phase, U, inv.ListPtr);
          if (lgrid.Has && hasPhase) lgrid.Track(locateGeo, lgrid.VisibleRow(locateGeo, (inv.Uids.Count + locateCols - 1) / locateCols, W, H, U), phase, U);
        }
        locateNeedClick = !lgrid.Has;
        if (lgrid.Has) {
          if (recB != null) recB.PlaceRow1 = lgrid.Row1;
          double[] mkr;
          var mk = LocateFrames.ListScene(Geo.Current, lgrid, locateGeo, inv.Uids.Count, locateCols, step.Row, step.Col, W, H, U, out mkr);
          frame++;
          HideTip();
          // the item is off the screen: its place on the list's scroll bar too (the pill beside it says which way and how far)
          double[] strip = null;
          if (mk.Kind != "box") {
            string msig = mkr == null ? "none" : string.Join(",", Array.ConvertAll(mkr, d => ((int)d).ToString()));
            if (msig != locateMarkerSaid) { locateMarkerSaid = msig; Log.Write("locate marker: " + msig + " row=" + step.Row + " of " + ((inv.Uids.Count + locateCols - 1) / locateCols) + " row1=" + (int)lgrid.Row1 + " bar=" + (Geo.Current != null && Geo.Current.Rect("inventory.scrollbar", W, H, U) != null ? "geometry" : "no geometry")); }
            if (mkr != null) strip = new[] { o.X + mkr[0], o.Y + mkr[1], o.Y + mkr[2], o.Y + mkr[3], o.Y + mkr[4], o.Y + mkr[5], 1 };
          }
          Draw(mk.Kind, o.X + mk.X, o.Y + mk.Y, mk.W, mk.H, mk.Rows, strip);
        } else LocateHide();
      } else if (!front) { LocateHide(); }
      else if (lf != null && lf.Mark.Kind != "box") { frame++; Draw(lf.Mark.Kind, o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, lf.Mark.Rows); }   // the list cell is off the screen: the pill at the list's edge
      else { if (glow.Visible) glow.Hide(); drawnKey = null; }
      if (locateInv != null && step.Kind != LocateKind.InList) locateLastCur = locateInv.Current;
      step.NeedClick = locateNeedClick;
      // a plate with the step's text (the page composes it): in the list's top-left corner, no frame
      int[] cityBtn;
      if (front && !hold && locateLines.Length > 0 && step.Kind == LocateKind.OpenInventory && !step.OnHero && CityInventoryButton(o, W, H, U, out cityBtn)) {
        frame++;   // the city: frame the bar's «Инвентарь» (where the auto mode clicks) with the step's plate beside it
        DrawTip(cityBtn[0], cityBtn[1], cityBtn[2], cityBtn[3], locateLines, H, false, false, false, locateCaps, false, GuideStyle ? null : HlCity(cityBtn));
      } else if (front && locateLines.Length > 0 && lf != null && lf.Mark.Kind == "box") {
        frame++;   // the button / tab / cell: its frame with the step's plate beside it
        DrawTip(o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, locateLines, H, false, false, false, locateCaps, false, HlLocate(lf, step, o));   // off: only the old frame's outline uses it
      } else if (front && locateLines.Length > 0 && (hold || step.Kind != LocateKind.InList || locateNeedClick)) {
        frame++;
        DrawTipTop(locateLines, locateCaps);   // its own header, not the plan's «Шаг N из M»
      } else if (!front || locateLines.Length == 0 || (!hold && step.Kind == LocateKind.InList)) HideTip();
      string key = step.Key + "|" + step.Pos + "|" + step.Row + "|" + step.Col + "|" + step.Count + "|" + string.Join(",", step.Missing) + "|" + step.OnHero + "|" + step.NeedClick
        + "|" + (locateSubs != null ? string.Join(",", Array.ConvertAll(locateSubs.Match, x => x ? "1" : "0")) + locateSubs.Changed : "") + "|" + step.Auto + "|" + step.AutoWhy;
      if (key != locateKey) { locateKey = key; var h = LocateChanged; if (h != null) h(step, locateSubs); }
    }

    // ---- auto click for «Показать в игре» (src/LocatePilot.cs): the app opens the Inventory, the «Снаряжение» tab and the item
    // itself, and stops where the places of the next buttons are not known (those steps stay hints). The last button of the
    // game (and every confirmation) is the player's. Off without «Автонажатие».
    readonly LocatePilot lpilot = new LocatePilot();

    // ---- the game's screen (src/GameScreen.cs) and the click gate (src/ClickGate.cs): ONE read per tick, ONE gate for every click
    readonly ScreenTracker screens = new ScreenTracker();
    readonly ClickGate gate = new ClickGate();
    /// <summary>The screen as of this tick (null before the first read).</summary>
    public ScreenState Screen { get { return screens.Current; } }
    /// <summary>The name of the screen for the page (the equip helper's live event), "" when it was not read lately.</summary>
    public string ScreenName(out bool bulk) {
      var s = screens.Current; bulk = false;
      if (s == null || NowMs() - s.ReadAtMs > 2000) return "";
      bulk = s.Kind == ScreenKind.Inventory && s.InvEquipTab && s.InvBulk;
      return s.Kind.ToString().ToLowerInvariant();
    }
    /// <summary>«Показать в игре» may click: the player's auto click and the site's "locateAuto" switch (off until the owner enables it).</summary>
    public bool LocateAutoEnabled;
    long screenReadAt, cityAt; bool cityNow; long locUnkSince = -1, shUnkSince = -1; SellHintStep shLast; bool shHold;
    readonly Dictionary<ClickOwner, string> haltSaid = new Dictionary<ClickOwner, string>();

    bool ScreensWanted() { return target != 0 || locateReq != null || filterItem != 0 || hintKind != "" || runHero != 0 || sellWanted != null; }

    /// <summary>THE read of the game's screens, once per tick (at most every 120 ms). The equip helper, «Показать в игре», the storage
    /// cleanup and the click gate take the screen from here (a short read failure is held for 1.5 s, see ScreenTracker).</summary>
    void ReadScreens(long now) {
      if (!ScreensWanted()) return;
      RFX.EnsureLiveForGuides();   // the readers' basis (handle, regions, live tables) without waiting for the first account read; throttled and off-thread
      if (screens.Current != null && now >= screenReadAt && now - screenReadAt < 120) return;
      screenReadAt = now;
      IntPtr hwnd = GameWindow();
      bool front = hwnd != IntPtr.Zero && !W32.IsIconic(hwnd) && IsForeground(hwnd);
      // the form searches (a full pass, ~1 s) run on a worker thread, never here: the readers below only use the nodes found so far.
      // A form the game never built is searched for at the start of a storage run and after a click that opens it
      if ((sellScanOnce || now - sellBtnAt < 8000) && front && RFX.InvFormMissing()) RFX.RequestFormSearch("storage run, inventory not built", 1500);
      sellScanOnce = false;
      var inp = new ScreenInputs { Inv = RFX.ReadInventory(-1), Rs = RFX.ReadRecastScreenSafe(-1), Hero = RFX.ReadHeroScreen(addrs, screens.Hero) };
      inp.CityForm = RFX.CityShown();
      if (front && inp.CityForm != true && ScreenState.Classify(inp, now).Kind == ScreenKind.Unknown) {   // the city form is not readable or reads closed: the city's bottom bar (a picture, at most every 400 ms)
        if (now - cityAt >= 400) { cityAt = now; cityNow = CityVisible(hwnd); }
        inp.CitySeen = cityNow;
      } else cityNow = false;
      if (RecInfo.Active) RecReads.Add(now, inp);   // the recorder: what the tracker is given (src/RecInputs.cs)
      var st = screens.Update(inp, now);
      // search policy: a form that read fine last tick and reads 0 now = the game moved it (soon, at most every 0.5 s); an Unknown screen = look every 2 s, no count limit
      if (front) {
        string lost = RFX.TakeFormsLost();
        if (lost != null) RFX.RequestFormSearch("lost: " + lost, 500);
        else if (st.Kind == ScreenKind.Unknown) RFX.RequestFormSearch("screen unknown", 2000);
      }
      if (screens.Hero != null) heroScreen = screens.Hero;
      gate.Observe(st, now);
      AnnounceHalts();
    }

    bool CityVisible(IntPtr hwnd) {
      W32.RECT cr;
      if (hwnd == IntPtr.Zero || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) return false;
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      return InventoryButtonSeen(o, cr.R, cr.B) || HeroesButtonSeen(o, cr.R, cr.B);
    }

    /// <summary>A fresh read for the click gate: not held, not throttled (the screen right before the click).</summary>
    ScreenState FreshScreen(bool wantCity) {
      long now = NowMs();
      var inp = new ScreenInputs { Inv = RFX.ReadInventory(-1), Rs = RFX.ReadRecastScreenSafe(-1), Hero = RFX.ReadHeroScreen(addrs, screens.Hero), CityForm = RFX.CityShown() };
      var st = ScreenState.Classify(inp, now);
      // a click on a city button also needs the picture of the bar (the form alone is not enough for a click)
      if (wantCity && (st.Kind == ScreenKind.City || (st.Kind == ScreenKind.Unknown && inp.CityForm == null))) {
        bool seen = CityVisible(GameWindow());
        if (inp.CityForm == true && !seen) inp.CityForm = false;
        inp.CitySeen = seen; st = ScreenState.Classify(inp, now);
      }
      return st;
    }

    /// <summary>A pilot was stopped by the gate (or by a click that changed nothing): each owner's pilot shows it once and goes on as hints only.</summary>
    void AnnounceHalts() {
      foreach (var o in new[] { ClickOwner.Locate, ClickOwner.Sell, ClickOwner.Equip }) {
        string why = gate.HaltReason(o), said;
        haltSaid.TryGetValue(o, out said);
        if (why == (said ?? "")) continue;
        haltSaid[o] = why;
        if (why == "") continue;
        Log.Write("auto click stopped (" + o + "): " + why);
        if (o == ClickOwner.Locate) lpilot.Halt(why);
        else if (o == ClickOwner.Sell) SellSay("gate_" + why);
        else AutoSay("gate_" + why);
      }
    }

    /// <summary>A new request / run of this owner: it may click again.</summary>
    void ResumeClicks(ClickOwner o) { gate.Resume(o); haltSaid.Remove(o); }

    /// <summary>THE way a click leaves the app: the gate says yes (the target is on the whitelist of the screen the game shows right now,
    /// no pop-up, the cell is the item's, the last click showed its effect), then it is sent and its effect is awaited.</summary>
    bool GatedSend(ClickRequest req, ScreenState fresh, AutoAction a, int sx, int sy) {
      long now = NowMs();
      var res = gate.Check(req, fresh, now);
      AnnounceHalts();
      if (!res.Allowed) return false;
      gate.Arm(req.Owner, req.Target, ClickGate.Expect(req, fresh), now);
      Send(a, sx, sy);
      return true;
    }

    /// <summary>An equip helper click (screen px). Target: what it presses (city / hero / gear list = EquipNav, the filter pop-up, its ×, «Заменить»).</summary>
    bool EquipSend(ClickTarget t, AutoAction a, int sx, int sy, W32.POINT o, bool front) {
      if (t == ClickTarget.EquipConfirm && !AutoConfirm) { gate.Halt(ClickOwner.Equip, "target", "«Заменить» without the player's auto confirm"); AnnounceHalts(); return false; }
      var fresh = FreshScreen(t == ClickTarget.EquipNav);
      var req = new ClickRequest { Owner = ClickOwner.Equip, Target = t, AutoOn = AutoEnabled, Front = front, X = sx - o.X, Y = sy - o.Y };
      return GatedSend(req, fresh, a, sx, sy);
    }
    RecastScreen locateRs; int locateOnAccount = -1; bool locateDriving;
    readonly InvScroll locateScroll = new InvScroll(), sellScroll = new InvScroll();
    string gridSaid = "", listSaid = "";

    /// <summary>The inventory grid's rows from the game's own scroll reading, for every user of the grid («Найти в игре», the
    /// storage cleanup's hints). True = the grid is placed that way; false = the game's scroll is unreadable (said once in the
    /// log): the caller falls back to the picture. The picture only fixes the constant offset (once, then now and then).</summary>
    bool FollowInventoryGrid(LocateGrid grid, InvScroll sc, SellGeometry geo, InventoryScreen inv, W32.POINT o, W32.RECT cr, double U, long now, string who) {
      if (inv == null || inv.List == null || !inv.List.Has) {
        if (gridSaid != who + ":px") { gridSaid = who + ":px"; Log.Write(who + ": the inventory's scroll cannot be read (or the layout data has no grid fields) - the picture is the fallback"); }
        return false;
      }
      double phase = sc.WantPhase(now) ? PhaseOf(geo, o, cr, U) : double.NaN;
      double r1 = sc.Row1(geo, inv.List, phase, U, now);
      if (double.IsNaN(r1)) return false;
      if (recB != null) { recB.InvRaw = inv.List.ScrollY; recB.InvRow1 = r1; }
      grid.FromScroll(r1, inv.ListPtr);
      if (gridSaid != who + ":scroll") { gridSaid = who + ":scroll"; Log.Write(who + ": the inventory grid from the game's scroll (" + inv.List + ")"); }
      return true;
    }

    // a foundry list's reading, to the log when it changes kind (what the next live test needs to compare)
    void LogListRead(string who, RFX.ListRead l) {
      string t = who + ":" + (l == null ? "none" : l.Has ? "ok" : "unreadable");
      if (t != listSaid) { listSaid = t; Log.Write("locate: the " + who + " list's reading: " + (l == null ? "none" : l.ToString())); }
    }
    string locateMoveState = "", locateMoveWhy = "", locateAutoSaid = "";

    /// <summary>The Foundry list's scroll read anew and eased, for the frame of a list cell (the screen look is only a few times
    /// a second): the cell's frame follows the list smoothly while it scrolls.</summary>
    void FoundryScrollNow(LocateStep step, long now) {
      var rs = locateRs;
      bool asc = step.Kind == LocateKind.AscendPick;
      if (rs == null || (!asc && step.Kind != LocateKind.RecastPick)) { locListSmooth.Reset(); return; }
      var l = asc ? rs.AscendList : rs.RecastList;
      var live = RFX.ReadFoundryListLive(asc);
      if (live != null && live.Has) l = live;
      if (l == null || !l.Has) { locListSmooth.Reset(); return; }
      double y = locListSmooth.Step(l.ScrollY, now, 4 * (l.RowUnits > 0 ? l.RowUnits : 120));
      if (recB != null) { recB.FdRaw = l.ScrollY; recB.FdSmooth = y; }
      var sm = new RFX.ListRead { ScrollY = y, Cols = l.Cols, RowUnits = l.RowUnits, First = l.First, Last = l.Last, FinalRowUnits = l.FinalRowUnits, FitScale = l.FitScale };
      if (recB != null) recB.Fd = "{\"asc\":" + (asc ? "true" : "false") + ",\"l\":" + RecIn.List(sm) + "}";
      if (asc) rs.AscendList = sm; else rs.RecastList = sm;
    }

    void TickLocateDrive(LocateStep step, LocateRequest req, IntPtr hwnd, W32.POINT o, W32.RECT cr, long now, double W, double H, double U) {
      locateDriving = false;
      if (!AutoEnabled || !LocateAutoEnabled) { step.Auto = ""; step.AutoWhy = ""; return; }   // hints only (the site's locateAuto switch is off by default)
      if (sending) {   // a click is on its way (its button press is not the player's): the screens are read after it
        step.Auto = locateMoveState == "" ? "work" : locateMoveState; step.AutoWhy = locateMoveWhy; locateDriving = true;
        return;
      }
      // the player's own mouse use (the pilot's moves are remembered in `seen`); the normal tick does not run during a request
      W32.POINT c;
      if (W32.GetCursorPos(out c)) {
        if (seenSet && (c.X != seen.X || c.Y != seen.Y)) lastMoveMs = now;
        seen = c; seenSet = true;
      }
      bool down = (W32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (W32.GetAsyncKeyState(0x02) & 0x8000) != 0;
      if (down && !btnWasDown) userClick = true;
      btnWasDown = down;
      if (lpilot.Stopped) { step.Auto = "stopped"; step.AutoWhy = lpilot.Reason; return; }
      bool busy = down || now - lastMoveMs < 700, uc = userClick; userClick = false;
      int fp, gp; W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp); W32.GetWindowThreadProcessId(hwnd, out gp);
      bool gameFront = fp == gp;
      // the screen of this tick (ReadScreens); the click gate judges the pilot's last click and every new one by it
      var scr = screens.Current;
      var inv = screens.Inv;
      if (screens.Rs != null) locateRs = screens.Rs;
      if (gameFront && !busy) {
        var pre = gate.Preflight(ClickOwner.Locate, scr, now);
        AnnounceHalts();
        if (lpilot.Stopped) { step.Auto = "stopped"; step.AutoWhy = lpilot.Reason; return; }
        if (pre.Verdict == GateVerdict.Wait) {
          if (pre.Reason == "pending") { step.Auto = "work"; step.AutoWhy = ""; locateDriving = true; }   // the last click's effect is awaited
          else { step.Auto = "hint"; step.AutoWhy = "no_city"; }                                          // the screen is not recognised yet: hints only
          return;
        }
      }
      bool heroOpen = scr != null && scr.Kind == ScreenKind.HeroScreen;
      bool formOpen = scr != null && (scr.Kind == ScreenKind.Foundry || scr.Kind == ScreenKind.Gem);
      var dstep = LocateGuide.Next(req, inv, locateRs, heroScreen, locateCols, locateOnAccount);
      var view = new LocateView { NowMs = now, Foreground = gameFront, UserBusy = busy, UserClicked = uc, W = W, H = H, U = U, Step = dstep, Inv = inv, Cols = locateCols,
        HeroScreenOpen = heroOpen, OtherFormOpen = formOpen, InvDX = invDX, Geometry = Geo.Current, Rs = locateRs, Item = req.Item, Purpose = req.Purpose };
      if (gameFront && !busy && LocateAuto.NeedsCityButton(dstep, heroOpen, formOpen)) view.CityButton = InventoryButtonSeen(o, W, H);
      if (gameFront && !busy && dstep.Kind == LocateKind.InList) {
        var pg = SellGeometry.Inventory(W, U, locateCols);
        view.Row1 = locateScroll.Row1(pg, inv != null ? inv.List : null, double.NaN, U, now);   // the game's scroll (the constant is learned by the frame)
        if (double.IsNaN(view.Row1)) view.Phase = PhaseCached(pg, o, cr, U, now);                    // last resort
      }
      var mv = lpilot.Step(view);
      if (lpilot.Trace != "") { Log.Write("locate auto: " + lpilot.Trace); lpilot.Trace = ""; }
      locateMoveState = mv.State; locateMoveWhy = mv.Why;
      string said = mv.State + "|" + mv.Why + "|" + dstep.Key;
      if (said != locateAutoSaid) { locateAutoSaid = said; Log.Write("locate auto: " + mv.State + (mv.Why != "" ? " (" + mv.Why + ")" : "") + " at step " + dstep.Key); }
      // started from our own window: the game comes forward (and is restored), never over another program
      if (mv.State == "background" && fp == SelfPid && now >= focusAfter) {
        focusAfter = now + 3000;
        if (W32.IsIconic(hwnd)) W32.ShowWindow(hwnd, 9);   // SW_RESTORE
        W32.SetForegroundWindow(hwnd);
      }
      if (mv.Action.Kind != AutoKind.None) SendLocate(mv, req, o, cr, gameFront, now, W, H, U);
      if (lpilot.Stopped) { step.Auto = "stopped"; step.AutoWhy = lpilot.Reason; locateDriving = false; return; }
      step.Auto = mv.State; step.AutoWhy = mv.Why;
      locateDriving = mv.State == "work" || mv.State == "paused" || mv.State == "background";
    }

    const double InvBtnDX = 0.5222 + 0.9316;   // «Инвентарь»: 0.9316 left of «Герои» in the city's bottom bar
    int sellCols = 8, sellGeoCols; double sellGeoW, sellGeoH; bool sellScanOnce;

    // the rows' phase on the screen: the stat bars under the cells (client px mod the row step), NaN = unclear
    long phaseAt; double phaseVal = double.NaN;
    // the picture look (a screen grab) at most every 100 ms: the fallback must not grab at the fast tick
    double PhaseCached(SellGeometry sg, W32.POINT o, W32.RECT cr, double U, long now) {
      if (now - phaseAt >= 100 || phaseAt == 0) { phaseAt = now; phaseVal = PhaseOf(sg, o, cr, U); }
      return phaseVal;
    }

    double SellPhase(W32.POINT o, W32.RECT cr, double U) { return PhaseOf(spilot.Geometry, o, cr, U); }

    double PhaseOf(SellGeometry sg, W32.POINT o, W32.RECT cr, double U) {
      int x0 = (int)Math.Round(sg.X(sg.X0, cr.R, U)), x1 = (int)Math.Round(sg.X(sg.X0 + (sg.Columns - 1) * sg.PitchX + sg.CellW, cr.R, U));
      int y0 = (int)Math.Round(sg.ViewTopPx(U)), y1 = (int)Math.Round(sg.ViewBottomPx(cr.B, U));
      if (x0 < 0 || x1 > cr.R || y1 - y0 < 100) return double.NaN;
      int w = x1 - x0, h = y1 - y0;
      int[] px = Grab(o.X + x0, o.Y + y0, w, h);
      if (px == null) return double.NaN;
      MaskOwn(px, o.X + x0, o.Y + y0, w, h);
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
    // the click gate decides (src/ClickGate.cs): <paramref name="fill"/> adds what only the fresh read can say (the item under a cell click)
    bool SendInside(AutoAction a, W32.POINT o, W32.RECT cr, ClickOwner owner, ClickTarget target, bool front, Action<ClickRequest, ScreenState> fill = null) {
      int x = o.X + (int)Math.Round(a.X), y = o.Y + (int)Math.Round(a.Y), y2 = y + (int)Math.Round(a.DY);
      if (x < o.X || y < o.Y || x >= o.X + cr.R || y >= o.Y + cr.B || y2 < o.Y || y2 >= o.Y + cr.B) return false;
      var fresh = FreshScreen(target == ClickTarget.CityInventory);
      var req = new ClickRequest { Owner = owner, Target = target, AutoOn = owner == ClickOwner.Locate ? AutoEnabled && LocateAutoEnabled : AutoEnabled, Front = front, X = a.X, Y = a.Y };
      if (fill != null) fill(req, fresh);
      return GatedSend(req, fresh, a, x, y);
    }

    // «Показать в игре»: the pilot's action -> the gate's target (and for a list cell the item that has to be under the click)
    void SendLocate(LocateMove mv, LocateRequest req, W32.POINT o, W32.RECT cr, bool front, long now, double W, double H, double U) {
      bool drag = mv.Action.Kind == AutoKind.Drag;
      ClickTarget tg = ClickTarget.None;
      Action<ClickRequest, ScreenState> fill = null;
      switch (mv.Todo) {
        case LocateTodo.BackToCity: tg = ClickTarget.HeroBack; break;
        case LocateTodo.InventoryButton: tg = ClickTarget.CityInventory; break;
        case LocateTodo.EquipTab: tg = ClickTarget.InvEquipTab; break;
        case LocateTodo.FoundryButton: tg = ClickTarget.FoundryButton; break;
        case LocateTodo.AscendTab: tg = ClickTarget.AscendTab; break;
        case LocateTodo.SlotTab: tg = ClickTarget.SlotTab; break;
        case LocateTodo.CardGem: case LocateTodo.GemFunc: {
          bool gemBtn = mv.Todo == LocateTodo.CardGem;
          tg = gemBtn ? ClickTarget.InvCardGem : ClickTarget.GemFuncRow;
          fill = (rq, fr) => { rq.CellOk = new[] { req.Item.Uid }; rq.Func = GemRows.FuncOfQuality(req.Item.Quality); };   // the screen's selected item has to be this one
          break;
        }
        case LocateTodo.PickItem:
          tg = drag ? ClickTarget.InvListDrag : ClickTarget.InvListCell;
          fill = (rq, fr) => {
            var g2 = SellGeometry.Inventory(W, U, locateCols);
            rq.CellUid = ClickGate.InvCellUid(fr.Inv, g2, locateScroll.Row1(g2, fr.Inv != null ? fr.Inv.List : null, double.NaN, U, now), locateCols, rq.X, rq.Y, W, U);
            rq.CellOk = new[] { req.Item.Uid };
          };
          break;
        case LocateTodo.AscendCell:
          tg = drag ? ClickTarget.AscendDrag : ClickTarget.AscendCell;
          fill = (rq, fr) => { rq.CellUid = ClickGate.AscendCellUid(Geo.Current, fr.Rs, rq.X, rq.Y, W, H, U); rq.CellOk = new[] { req.Item.Uid }; };
          break;
      }
      SendInside(mv.Action, o, cr, ClickOwner.Locate, tg, front, fill);   // ClickTarget.None is on nobody's whitelist: stopped
    }

    /// <summary>Moves the cursor and clicks / drags there, off the UI thread (short pauses let the game see the pointer
    /// over the button first). A drag moves in small steps and rests before the release, so the list does not coast.</summary>
    void Send(AutoAction a, int x, int y) {   // ONLY called by GatedSend
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

    // the city's «Инвентарь» button in the bottom bar (where the auto mode clicks): its frame (client px -> screen px), seen by its
    // picture (looked at most every 0.4 s); false = the city bar is not on the screen
    long cityInvAt; bool cityInvSeen;
    bool CityInventoryButton(W32.POINT o, double W, double H, double U, out int[] r) {
      long now = NowMs();
      if (now - cityInvAt >= 400) { cityInvAt = now; cityInvSeen = InventoryButtonSeen(o, W, H); }
      var cb = LocateFrames.CityButton(Geo.Current, W, H, U, invDX, hg2.HeroesBtnDY);   // the bar's button size from the geometry (else the estimate)
      r = new[] { o.X + cb[0], o.Y + cb[1], cb[2], cb[3] };
      return cityInvSeen;
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
    internal static bool IsForeground(IntPtr game) {
      IntPtr f = W32.GetForegroundWindow(); if (f == game) return true;
      int fp, gp; W32.GetWindowThreadProcessId(f, out fp); W32.GetWindowThreadProcessId(game, out gp);
      // the game, or our always-on-top compact window over it (OverlayGate.InFront); our normal app window never counts
      bool own = fp == SelfPid, top = own && (W32.GetWindowLong(f, -20) & 0x00000008) != 0;   // GWL_EXSTYLE, WS_EX_TOPMOST
      return OverlayGate.InFront(fp == gp, own, top);
    }

    /// <summary>The central rule for every overlay drawing (OverlayGate.CanDraw): the game window is visible, not minimized and in
    /// front. Applied in Tick (everything is hidden at once) and in Scene (nothing is painted); the features do not repeat it.</summary>
    bool GameDrawable(bool screenActive = true) {
      IntPtr hwnd = GameWindow();
      bool has = hwnd != IntPtr.Zero;
      return OverlayGate.CanDraw(has, has && W32.IsWindowVisible(hwnd), has && W32.IsIconic(hwnd), has && IsForeground(hwnd), screenActive);
    }

    // values per line of the strip picture (ListTracker.LineSig): 4 parts of each of the 3 columns
    const int SigSegs = 4, SigN = ListGeometry.Columns * SigSegs;

    // ---------------------------------------------------------------- drawing

    // ---------------------------------------------------------------- the new button highlight (site switch guideStyle, test build: Settings)
    // A (normal steps): the client dimmed (black 59 %) with a hole in the button's shape (+8 px), a warm-gold edge (max(4, 0.006*U) px) with a soft glow (max(18, 0.022*U) px, a wider faint halo) pulsing
    // 0.45..1.0 every 1.2 s, and the guide hand above the button bobbing 4 px. B (the player's own final press, list cells): the edge and glow only.
    /// <summary>The new highlight is on (the site's guideStyle switch, or the test build's own setting).</summary>
    public bool GuideStyle;
    /// <summary>The site's address (the hand picture is fetched from it once).</summary>
    public string GuideSite = "";
    int guideLvl;   // the pulse of this draw, 0..5

    /// <summary>What the new highlight outlines: the art's rectangle (screen px) with its shape and inset; Important = variant B.</summary>
    sealed class Hl { public GeoRect Rect; public GeoShape Shape; public GeoInset Inset; public bool Important; }

    // a rectangle with rounded corners (a list cell, a button of unknown shape)
    Hl HlRound(double x, double y, double w, double h, bool important) {
      return new Hl { Rect = new GeoRect { X = x, Y = y, W = w, H = h }, Shape = new GeoShape { K = "round", R = 0.08 }, Important = important };
    }
    Hl HlCell(double x, double y, double w, double h) { return HlRound(x, y, w, h, true); }

    // the city bar's «Инвентарь» (screen px rect): its geometry shape for the old frame
    Hl HlCity(int[] r) {
      var g = Geo.Current;
      return new Hl { Rect = new GeoRect { X = r[0], Y = r[1], W = r[2], H = r[3] }, Shape = g != null ? g.Shape("city.barButton") : null, Inset = g != null ? g.Inset("city.barButton") : null };
    }

    // «Показать в игре»: the step's button with the geometry's shape; the final presses (Done, the Foundry's «Вознести») and list cells are B, the rest A
    Hl HlLocate(LocateFrame lf, LocateStep step, W32.POINT o) {
      var g = Geo.Current;
      bool important = step.Kind == LocateKind.Done || step.Kind == LocateKind.AscendReady || lf.Target == "cell";
      var ins = g != null && lf.Target != "cell" ? g.Inset(lf.Target) : null;
      var sh = g != null && lf.Target != "cell" ? g.Shape(lf.Target) : null;
      if (lf.Target == "cell") return HlRound(o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, true);
      // with an inset from the geometry the outline starts from the raw rectangle, else from the frame's own (already trimmed) box
      var r = ins != null && lf.Raw != null ? new GeoRect { X = o.X + lf.Raw.X, Y = o.Y + lf.Raw.Y, W = lf.Raw.W, H = lf.Raw.H } : new GeoRect { X = o.X + lf.Mark.X, Y = o.Y + lf.Mark.Y, W = lf.Mark.W, H = lf.Mark.H };
      return new Hl { Rect = r, Shape = sh, Inset = ins, Important = important };
    }

    // draws the highlight into the bitmap (its origin on the screen: ox, oy); marksBox: a cell given in bitmap px already (storage cleanup marks)
    void PaintGuide(Graphics gr, Hl hl, int ox, int oy, int bw, int bh, bool dim, int G, Image hand, int bob, double U, GeoRect marksBox = null) {
      float a = 0.45f + 0.55f * guideLvl / 5f;
      var box = marksBox ?? new GeoRect { X = hl.Rect.X - ox, Y = hl.Rect.Y - oy, W = hl.Rect.W, H = hl.Rect.H };
      var art = GuideShape.ArtBox(box, marksBox != null ? null : hl.Inset);
      var pts = GuideShape.Outline(GuideShape.Grow(art, dim ? 8 : 2), hl.Shape, null);
      if (pts.Count < 3) return;
      var poly = new PointF[pts.Count];
      for (int i = 0; i < poly.Length; i++) poly[i] = new PointF((float)pts[i].X, (float)pts[i].Y);
      using (var path = new GraphicsPath()) {
        path.AddPolygon(poly);
        if (dim) {
          using (var br = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) gr.FillRectangle(br, 0, 0, bw, bh);
          gr.CompositingMode = CompositingMode.SourceCopy;
          using (var hole = new SolidBrush(Color.FromArgb(0, 0, 0, 0))) gr.FillPath(hole, path);
          gr.CompositingMode = CompositingMode.SourceOver;
        }
        int glow = (int)Math.Max(18, 0.022 * U);
        float edge = (float)Math.Max(4, 0.006 * U);
        gr.SetClip(path, CombineMode.Exclude);   // the glow lies outside the hole only
        for (int i = glow + 10; i > glow; i -= 4)   // the second, wider faint halo
          using (var pen = new Pen(Color.FromArgb((int)(14 * a), 232, 196, 106), 2f * i) { LineJoin = LineJoin.Round }) gr.DrawPath(pen, path);
        for (int i = glow; i >= 2; i -= 3)
          using (var pen = new Pen(Color.FromArgb((int)(34 * a), 232, 196, 106), 2f * i) { LineJoin = LineJoin.Round }) gr.DrawPath(pen, path);
        for (int i = 6; i >= 2; i -= 2)   // the inner glow: next to the edge at 0.85 of its alpha
          using (var pen = new Pen(Color.FromArgb((int)(255 * a * 0.85f), 232, 196, 106), 2f * i) { LineJoin = LineJoin.Round }) gr.DrawPath(pen, path);
        gr.ResetClip();
        using (var pen = new Pen(Color.FromArgb((int)(255 * a), 232, 196, 106), edge) { LineJoin = LineJoin.Round }) gr.DrawPath(pen, path);
      }
      if (hand != null && hl != null) {
        double hh = 0.14 * U, hw = hh * hand.Width / hand.Height;
        double tipX = hl.Rect.X + hl.Rect.W / 2 - ox, tipY = hl.Rect.Y + hl.Rect.H * 0.3 - oy + bob;   // the fingertip on the button's upper part (the label stays readable)
        var hp = GuideShape.HandPos(tipX, tipY, hw, hh, GuideHand.TipX, GuideHand.TipY, bw, bh);
        gr.DrawImage(hand, (float)hp.X, (float)hp.Y, (float)hw, (float)hh);
      }
    }

    // the game's own «selected» glow frame (common_frame_chosen_light), embedded in the exe
    static Image frameTex;
    static Image FrameTex() {
      if (frameTex == null) {
        try { using (var s = typeof(OverlayController).Assembly.GetManifestResourceStream("overlay/frame.png")) if (s != null) frameTex = new Bitmap(Image.FromStream(s)); }
        catch (Exception) { }
      }
      return frameTex;
    }

    // the item's frame with the step plate beside it, or (above / below the visible list) the gold scroll strip with a pill
    // «Вещь здесь ↓ N рядов» at the item's dot (strip: {x, viewTop, viewBottom, dotY, b0, b1}, screen px; null = a pill
    // beside the spot alone)
    void Draw(string kind, int x, int y, int w, int h, int rows, double[] strip = null) {   // a box here is a list cell: the new highlight's edge only (no dimming over lists)
      if (!LocateWins) Say(kind == "box" ? "on" : kind == "up" ? "above" : "below", rows);   // the plan's state; «Найти в игре» has its own
      dKind = kind + (rows > 0 ? ":" + rows : ""); dX = x; dY = y; dW = w; dH = h;
      if (kind == "box") {
        Plate p = null;
        if (!LocateWins && Tx("pick") != "")
          p = WrongItem ? new Plate { Caps = Tx("caps"), Main = Tx("wrong"), Sub = Tx("wrongSub") } : new Plate { Caps = Tx("caps"), Main = Tx("pick"), Sub = Tx("then") };
        Scene(glow, ref drawnKey, new[] { x, y, w, h }, p, new[] { x, y, w, h }, null, null, GuideStyle ? HlCell(x, y, w, h) : null);
        return;
      }
      string tpl = Tx(kind == "up" ? "pillUp" : "pillDown");
      string text = tpl != "" ? tpl.Replace("{0}", RowsText(rows)) : (kind == "up" ? "↑ " : "↓ ") + rows;
      var pill = LocateWins ? new Plate { Main = text, Arrow = kind == "up" ? -1 : 1 } : new Plate { Pill = true, Main = text };   // «Показать в игре»: the step's plate with an arrow, bigger
      if (strip != null) Scene(glow, ref drawnKey, null, pill, null, strip);
      else Scene(glow, ref drawnKey, null, pill, new[] { x, kind == "up" ? y : y - 2, w, 2 }, null);
    }

    // «6 рядов» in the page's language (rows1 / rows2 / rows5 hold «{0} ряд» / «{0} ряда» / «{0} рядов»)
    string RowsText(int n) {
      string[] forms = { Tx("rows1"), Tx("rows2"), Tx("rows5") };
      string f = forms[HintLayout.Plural(n, Tx("lang") != "en")];
      return f != "" ? f.Replace("{0}", n.ToString()) : n.ToString();
    }

    // where the list is not known: the plate beside the list asks for a click on any item (no frame)
    void NeedClick() {
      Say("need_click"); dKind = "hidden:need_click";
      string m = Tx("unknown");
      if (m == "" || sceneW <= 0) { if (glow.Visible) glow.Hide(); drawnKey = null; return; }
      double U = Ui.Unit(sceneW, sceneH);
      var list = new[] { sceneX + (int)(g.StripLeft * U), sceneY + (int)(g.ViewTop * U), (int)((g.StripRight - g.StripLeft) * U), (int)((g.ViewBottom - g.ViewTop) * U) };
      Scene(glow, ref drawnKey, null, new Plate { Caps = Tx("caps"), Main = m, Sub = Tx("unknownSub") }, null, null);
    }

    static GraphicsPath Round(float x, float y, float w, float h, float r) {
      var p = new GraphicsPath(); float d = r * 2;
      p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
      p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
      p.CloseFigure(); return p;
    }
  }
}
