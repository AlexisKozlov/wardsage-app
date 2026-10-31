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
using static RealmForge.OverlayPaint;

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
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
    /// <summary>ms since the last mouse / keyboard input of anyone (SendInput included); -1 = not known.</summary>
    public static long InputAgeMs() {
      var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
      if (!GetLastInputInfo(ref li)) return -1;
      return unchecked((uint)Environment.TickCount - li.dwTime);
    }
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }   // type 0 = mouse; 40 bytes on x64
    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, WHEEL = 0x0800;
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendInput")] public static extern uint SendKeyInput(uint n, KINPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KINPUT { public uint type; public KEYBDINPUT ki; public uint pad1, pad2; }   // type 1 = keyboard; padded to the mouse INPUT's size (40 bytes on x64)
    public const uint KEYUP = 0x0002;
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLEND blend, int flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr WineVersionFn();

    /// <summary>The window's DPI (GetDpiForWindow on Windows 10+, else the screen's LOGPIXELSY); 0 if unknown.</summary>
    public static int WindowDpi(IntPtr h) {
      try { return (int)GetDpiForWindow(h); } catch (EntryPointNotFoundException) { }
      catch (Exception) { return 0; }
      IntPtr dc = GetDC(IntPtr.Zero);
      try { return dc == IntPtr.Zero ? 0 : GetDeviceCaps(dc, 90); } finally { if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc); }
    }

    static string wine;
    /// <summary>Wine's version when the app runs under Wine / CrossOver (ntdll exports wine_get_version), else "".</summary>
    public static string WineVersion() {
      if (wine != null) return wine;
      wine = "";
      try {
        IntPtr nt = GetModuleHandle("ntdll.dll"), f = nt == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(nt, "wine_get_version");
        if (f != IntPtr.Zero) {
          IntPtr v = ((WineVersionFn)Marshal.GetDelegateForFunctionPointer(f, typeof(WineVersionFn)))();
          wine = v != IntPtr.Zero ? (Marshal.PtrToStringAnsi(v) ?? "yes") : "yes";
          if (wine == "") wine = "yes";
        }
      } catch (Exception) { wine = "yes"; }
      return wine;
    }
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
      // the same picture again (the callers keep a bitmap while its key stays): nothing to copy; only moved = the window is moved
      if (Visible && ReferenceEquals(bmp, putBmp)) {
        if (x != putX || y != putY) { W32.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); putX = x; putY = y; }   // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
        return;
      }
      long t0 = LoadMeter.Now();
      if (!Visible) Show();
      putBmp = bmp; putX = x; putY = y;
      PutNow(bmp, x, y);
      LoadMeter.Done(LoadMeter.Put, t0);
    }
    Bitmap putBmp; int putX, putY;

    void PutNow(Bitmap bmp, int x, int y) {
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
    readonly GlowWindow glowPulse = new GlowWindow(), tipPulse = new GlowWindow();   // the frames' pulse lines: a small window of their own beside each of the two
    readonly Timer pulseTimer = new Timer();   // runs only while a frame is shown
    // «Отменить надевание» over the game while a build is being put on: the player's clicks in the game send our
    // window to the back, so the way to call it off is where the player looks
    readonly GlowWindow cancelWin = new GlowWindow(true);
    /// <summary>The player pressed «Отменить надевание» over the game.</summary>
    public event Action CancelPressed;
    /// <summary>The button's text in the interface language.</summary>
    public string CancelText = "Отменить надевание";
    /// <summary>The same button while «Показать в игре» clicks by itself: it only stops that.</summary>
    public string LocateCancelText = "Остановить автонажатие";
    public string SellCancelText = "Отменить чистку склада";
    /// <summary>The on-screen button during the storage cleanup's auto click: the player ends the whole cleanup run (the page cancels the list).</summary>
    public event Action SellCancelPressed;
    string cancelDrawn; Bitmap cancelBmp;
    // TEST build: the record button over the game (left edge, mid-height); a clickable window like cancelWin, drawn only while the central gate allows
    readonly GlowWindow recWin = new GlowWindow(true);
    readonly Timer recBtnTimer = new Timer();
    readonly Stopwatch recSw = new Stopwatch();
    string recDrawn; Bitmap recBmp; bool recOn; long recToggleAt;
    /// <summary>Settings → «Кнопка записи поверх игры» (the test build only).</summary>
    public bool RecButton;
    /// <summary>The player pressed the record button over the game: the host toggles the recorder.</summary>
    public event Action RecPressed;
    /// <summary>TEST build: something went wrong (a pilot stopped, an unknown form on top, an offer not closed): the host may record the screen (src/AutoRec.cs). The tag says why.</summary>
    public event Action<string> AutoRec;
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
    const int TipStillMs = 600, TipAgainMs = 3000;
    string tipSeenKey; long tipSeenAt, tipEscapedAt; int tipEscapes;   // the item's tip over the next slot: seen unchanged since, Escapes pressed for it
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
    // for the error report of a pilot failure (AutoDetail): what the pilot was given and did last
    AutoView lastAutoView; FilterView lastFilterView; HeroView lastHeroView; string lastPilotTrace = ""; int lastClientW, lastClientH;
    /// <summary>Public to the host: set PanelOk from the live poll (the game's panel table is readable).</summary>
    public bool PanelOk = true;
    /// <summary>Public to the host: no gear list panel yet (the game creates it when a slot is first opened).</summary>
    public bool PanelNone;
    /// <summary>What the report of a pilot failure says about it (no account or user data): filled when the state changes to a failure.</summary>
    public System.Collections.Generic.Dictionary<string, string> AutoDetail = new System.Collections.Generic.Dictionary<string, string>();
    // player activity: the cursor moved by someone else than the pilot, or a mouse button went down
    volatile bool sending; W32.POINT seen; bool seenSet; long lastMoveMs; bool btnWasDown, userClick; string autoSaid = "";
    EquipAddrs addrs; long target; int[] types; ulong typesPtr; long lastSel = -1;
    string state = "off"; int frame; string drawnKey;
    // diagnostics (Settings → «Диагностика рамки»): screenshots and list data into debug\ for a few minutes
    DateTime diagUntil = DateTime.MinValue; int diagTick, diagN, shotN; string diagLast, diagDir, diagInfo = "";
    string hintKind = ""; int hintSlot = -1; string[] hintLines = new string[0]; string tipKey; long hintHero;
    int[] confirmHintRect;   // the live rect of «Заменить» the hint frames (held still), this tick
    int[] confirmBtn;   // «Заменить» / «Надеть» found on the screen this tick (TickHint), for the pilot
    // storage cleanup: the items to select on the game's sell screen (null = no run); the hero whose gear screen leads there
    readonly SellPilot spilot;
    readonly Action<SellPilot, string> sellReport;
    System.Collections.Generic.HashSet<long> sellWanted; System.Collections.Generic.HashSet<int> sellParts; System.Collections.Generic.Dictionary<long, int> sellLevels; long sellHero; long sellBtnAt; string sellSaid = "";

    // storage cleanup without auto click («Подсказать в игре»): the same list, but the program only reads the inventory and
    // draws frames on the cells to select; every click is the player's
    bool sellHint;
    bool sellArt;   // the list holds ARTIFACTS: a hint run on the artifact tab, nothing is ever clicked
    string shFrameSaid = "";
    string[] sellHintLines = new string[0]; string sellPillUp = "", sellPillDown = "";
    readonly LocateGrid sgrid = new LocateGrid();
    InventoryScreen shInv; bool shHave; string shReadSig = ""; long shReadAt;
    int shCols = 8, shGeoCols; double shGeoW, shGeoH; SellGeometry shGeo;
    List<SellFrame> shFrames; string shKey = "";
    /// <summary>The final plate's format ("Готово: отмечено {0} — нажмите «Продать»", the interface's language; set by the host): every target is marked.</summary>
    public string SellDoneText = "";
    /// <summary>The final step is shown (everything is marked): the stop plate «Отменить чистку склада» goes away.</summary>
    bool sellFinal;
    // the last scene of the hint's frames (kept for a moment when the screen reads unknown, see SellHint.KeepFramesInHold)
    List<int[]> shLastMarks; double[] shLastStrip; Plate shLastPill; int[] shLastPt;
    /// <summary>The hint's step changed (kind, counts, rows out of view): the page shows it and answers with the plate texts.</summary>
    public event Action<SellHintStep> SellHintChanged;

    /// <summary>A failure worth an error report (kind, message): the filter pilot gave up, «Заменить» not found with the
    /// auto-confirm on, an exception in the tick. HostBridge sends it when the player turned the reports on.</summary>
    /// <summary>A problem worth an error report: kind, message and what the pilot saw (null = nothing more).</summary>
    public event Action<string, string, System.Collections.Generic.Dictionary<string, string>> Problem;
    void RaiseProblem(string kind, string message, System.Collections.Generic.Dictionary<string, string> ctx = null) {
      // every pilot report carries the game window's geometry self-check (client origin by two methods, DPI, Wine)
      if (kind.StartsWith("pilot.", StringComparison.Ordinal) && geomLine != null) {
        if (ctx == null) ctx = new System.Collections.Generic.Dictionary<string, string>();
        if (!ctx.ContainsKey("geom")) ctx["geom"] = geomLine;
      }
      var p = Problem; if (p != null) try { p(kind, message, ctx); } catch (Exception) { }
    }

    /// <summary>The geometry self-check of the game window (GameGeomCheck.Line): journaled whenever it changes (start, move, resize).</summary>
    string geomLine;
    void GeomCheck(IntPtr hwnd, W32.POINT o, W32.RECT cr) {
      W32.RECT wr; if (!W32.GetWindowRect(hwnd, out wr)) return;
      string line = GameGeomCheck.Line(o.X, o.Y, wr.L, wr.T, wr.R, wr.B, cr.R, cr.B, W32.WindowDpi(hwnd), W32.WineVersion());
      if (line == geomLine) return;
      geomLine = line;
      Log.Write("game geometry: " + line);
    }
    bool filterFailSaid; string tickError;

    public OverlayController(Action<string, bool> report, Action<string> autoReport, Action<SellPilot, string> sellReport) {
      this.report = report; this.autoReport = autoReport; this.sellReport = sellReport;
      spilot = new SellPilot(SellGeometry.Inventory(1920, 1009, 8));
      screens.Sink = s => Log.Write(s); gate.Sink = s => Log.Write(s);
      // a slow PC: the effect of «Инвентарь» / «Литейная» is awaited while the search for its form still runs (ClickGate.SearchCapMs)
      gate.FormSearchPending = t => RFX.FormSearchRunning && (t == ClickTarget.CityInventory ? RFX.InvFormMissing() : t == ClickTarget.FoundryButton && RFX.FoundryFormMissing());
      gate.FormFound = t => t == ClickTarget.CityInventory ? !RFX.InvFormMissing() : t == ClickTarget.FoundryButton && !RFX.FoundryFormMissing();
      gate.DangerLive = (form, field, w, h, u) => RFX.UiFieldReadNow(form, field, w, h, u);   // (a dangerous widget of a form over the click's one, where the game has it now)
      LoadMeter.Hook();   // (the memory passes and the forms checks counted in the load line)
      RFX.LiveLog = s => Log.Write(s);   // the live reads' lines (the lists' scroll binding ...) reach the app's log
      pilot = new AutoPilot(g);
      fpilot = new FilterPilot(fg);
      hpilot = new HeroPilot(heroG);
      pulseTimer.Interval = FrameStyle.PulseMs; pulseTimer.Tick += (s, e) => { long pt0 = LoadMeter.Now(); try { PulseTick(); } catch (Exception ex) { pulseTimer.Stop(); Log.Write("overlay: pulse: " + ex.Message); } finally { LoadMeter.Done(LoadMeter.Pulse, pt0); } };
      timer.Interval = 100; timer.Tick += (s, e) => {
        long lt0 = LoadMeter.Now();
        try { Tick(); tickError = null; }
        catch (Exception ex) {
          // the same exception every 100 ms: journaled (with its stack) and reported once
          string m = ex.GetType().Name + ": " + ex.Message;
          if (m != tickError) { tickError = m; Log.Write("overlay: " + ex); RaiseProblem("overlay.exception", "overlay: " + m); }
          Hide("off");
        }
        LoadMeter.Done(LoadMeter.Tick, lt0);
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
      recBtnTimer.Interval = LoadPlan.RecButtonMs(false); recBtnTimer.Tick += (s, e) => {
        int rbi = LoadPlan.RecButtonMs(recOn); if (recBtnTimer.Interval != rbi) recBtnTimer.Interval = rbi;
        long rt0 = LoadMeter.Now();
        try { TickRecBtn(); } catch (Exception ex) { if (recWin.Visible) recWin.Hide(); string m = "recbtn: " + ex.GetType().Name + ": " + ex.Message; if (m != tickError) { tickError = m; Log.Write("overlay: " + ex); } }
        LoadMeter.Done(LoadMeter.RecBtn, rt0);
      };
      if (Channel.IsTest) recBtnTimer.Start();
      // TEST build: the load line once a minute, also while no guide runs (the overlay tick is stopped then)
      if (Channel.IsTest) { loadTimer.Interval = 60000; loadTimer.Tick += (s, e) => { try { LogLoad(); } catch (Exception) { } }; loadTimer.Start(); }
      // the game coming to the front or back from minimized: the slow tick of a game away is cut short (the hints are back at once)
      try {
        winEventProc = (h, ev, hwnd, idObject, idChild, thread, time) => { if (timer.Enabled && timer.Interval > LoadPlan.ActiveMs) timer.Interval = 15; };
        winHookFg = W32.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, winEventProc, 0, 0, 0x0000 | 0x0002);   // EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
        winHookMin = W32.SetWinEventHook(0x0017, 0x0017, IntPtr.Zero, winEventProc, 0, 0, 0x0000 | 0x0002);  // EVENT_SYSTEM_MINIMIZEEND
      } catch (Exception) { }
      cancelWin.Pressed += () => {
        cancelWin.Hide();
        if (SellClicking) { Log.Write("sell: cancelled over the game"); var sc = SellCancelPressed; if (sc != null) sc(); return; }   // storage cleanup: the whole run ends (clicks and hints), the page cancels the list
        if (LocateWins && runHero <= 0) { lpilot.Cancel(); artPilot.Cancel(); Log.Write("locate auto: stopped over the game"); return; }   // «Показать в игре»: only the clicks stop
        var c = CancelPressed; if (c != null) c();
      };
    }

    readonly Timer loadTimer = new Timer();
    W32.WinEventProc winEventProc; IntPtr winHookFg, winHookMin;   // (the delegate is kept: the hook calls it for as long as it is set)

    public void Dispose() {
      if (winHookFg != IntPtr.Zero) W32.UnhookWinEvent(winHookFg); if (winHookMin != IntPtr.Zero) W32.UnhookWinEvent(winHookMin);
      winHookFg = winHookMin = IntPtr.Zero; loadTimer.Dispose(); pulseTimer.Dispose(); glowPulse.Dispose(); tipPulse.Dispose();
      timer.Dispose(); lookTimer.Dispose(); glow.Dispose(); tip.Dispose(); cancelWin.Dispose(); recBtnTimer.Dispose(); recWin.Dispose(); }

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
    public void SetRun(long hero) { if (hero == runHero) return; if (hero > 0) ResumeClicks(ClickOwner.Equip); bool was = PlanOn; if (hero > 0) artLatch.NewPlan(); runHero = hero; PlanStamp(was); Run(); }

    // ---- «Надеть в игре» with an artifact (src/ArtEquip.cs): after the gear the guide puts the plan's artifact on the hero (hint kind "art")
    long artHero, artUid;
    readonly ArtEquipPilot artEq = new ArtEquipPilot();
    ArtEquipStep artEqStep; long artWaitSince; string artEqSaid = "", artEqAutoSaid = "";
    /// <summary>The artifact steps are on: the gear of the plan is done and the page asks for the artifact (hint "art" with the hero and the artifact).</summary>
    bool ArtPhase { get { return hintKind == "art" && artUid != 0 && artHero != 0 && !artLatch.IsDone; } }
    readonly ArtGoalLatch artLatch = new ArtGoalLatch();

    /// <summary>The plan's artifact the guide puts on <paramref name="hero"/> (uid 0 = none). The page sends it with the hint "art".</summary>
    public void SetArtGoal(long hero, long uid) {
      if (uid <= 0 || hero <= 0) { hero = 0; uid = 0; }
      if (artLatch.Ignores(hero, uid)) return;   // Done is final for this artifact: the page's repeated hint does not re-arm it
      if (hero == artHero && uid == artUid) return;
      artHero = hero; artUid = uid;
      RFX.SetArtGoal(hero, uid);
      artEq.Reset(); artEqStep = null; artEqSaid = ""; artEqAutoSaid = "";
      Log.Write(uid != 0 ? "art equip: goal artifact " + uid + " for hero " + hero : "art equip: no goal");
    }

    /// <summary>The artifact step for the page's live event (JSON: uid, step name, done, owner); null = no goal.</summary>
    public string ArtLive() {
      var st = artEqStep;
      if (st == null || !ArtGoalLatch.LiveShown(ArtPhase, artLatch.IsDone)) return null;
      return "{\"uid\":" + artUid + ",\"step\":\"" + st.Name + "\",\"done\":" + (st.Kind == ArtEquipKind.Done ? "true" : "false") + ",\"owner\":" + st.Owner + "}";
    }

    /// <summary>Every pilot starts over (a new «Надеть»: after a failure the player may have fixed the game's screen).</summary>
    /// <summary>What the helper is at while a started plan runs (null = no plan, or a pilot is clicking / waiting for the
    /// player): the same key for long means the plan stands still.</summary>
    /// <summary>The hero the plan wants opened while its step is «open the hero» (the hint, or the started plan); 0 = none.</summary>
    public long HeroStepHero() { return hintKind == "hero" ? hintHero : (hintKind == "" || hintKind == "bulk" ? runHero : 0); }

    public string StallKey() {
      if (runHero <= 0 || autoSaid == "work" || autoSaid == "paused" || autoSaid == "background") return null;
      return runHero + "|" + target + "|" + filterItem + "|" + filterKind + "|" + hintKind + "|" + autoSaid + (ArtPhase && artEqStep != null ? "|" + artEqStep.Key : "");
    }

    public void RestartPilots() { RFX.LuaRootShadow("pilot start"); pilot.Reset(); fpilot.Reset(); hpilot.Reset(); artEq.Reset(); ResumeClicks(ClickOwner.Equip); }

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
    /// <param name="artifact">the list is artifacts (the artifact tab's «Массовая продажа»): frames and plates whatever <paramref name="hintOnly"/> says; clicks only with ArtAutoOn (the site's artAuto switch and the player's auto click), see TickArtSellAuto</param>
    public void SetSell(System.Collections.Generic.ICollection<long> uids, System.Collections.Generic.ICollection<int> parts, System.Collections.Generic.Dictionary<long, int> levels, long hero, bool hintOnly = false, bool artifact = false) {
      bool wasHint = sellHint;
      bool run = uids != null && uids.Count > 0;
      if (artifact) hintOnly = true;   // the artifact cleanup is always a hint run (frames, plates); its clicks come on top of it, only with ArtAutoOn (TickArtSellAuto)
      sellHint = hintOnly && run;
      bool nowArt = artifact && run;
      if (nowArt != sellArt) { shCols = 8; shGeo = null; }   // the artifact and the gear list have 8 columns at a 1.9:1 window (7 artifacts at the authored width: refined from the list's width at run time)
      sellArt = nowArt;
      ApplyArtGate();
      artSellBtnAt = 0; artSellSaid = "";
      if (wasHint || sellHint) {
        // a hint run starts (or ends) clean: no frames, no plate, the rows' place unknown
        sgrid.Reset(); shInv = null; shHave = false; shReadSig = ""; shReadAt = 0; shFrames = null; shKey = ""; shLast = null; shUnkSince = -1; shHold = false; shLastMarks = null; shLastStrip = null; shLastPill = null; shLastPt = null;
        sellHintLines = new string[0]; sellPillUp = sellPillDown = "";
        Hide("off"); HideTip();
      }
      sellFinal = false;
      sellLevels = levels;
      sellWanted = uids != null && uids.Count > 0 ? new System.Collections.Generic.HashSet<long>(uids) : null;
      sellParts = parts != null && parts.Count > 0 ? new System.Collections.Generic.HashSet<int>(parts) : null;
      sellHero = hero; sellSaid = ""; cellRunN++; spilot.Reset(); hpilot.Reset(); ResumeClicks(ClickOwner.Sell); sellBtnAt = 0; sellScanOnce = true;
      Run();
    }

    /// <summary>«Найти в игре»: show where this item is (null = stop). Hints only: the frame in the inventory's list and a
    /// text plate; nothing is clicked. While it runs the build's frame is not shown (one target at a time).</summary>
    public void SetLocate(LocateRequest r) {
      locateReq = r != null && (r.Item != null || (r.Purpose == "artPromote" && r.Art != null)) ? r : null;
      bool artReq = locateReq != null && locateReq.Purpose == "artPromote";
      RFX.InvFilterWanted = locateReq != null && !artReq && locateReq.Purpose != "ascend";   // the recast's long inventory search filters the list (ascend: the Foundry's own list; artPromote: the artifact tab)
      if (!RFX.InvFilterWanted) { RFX.InvFilterW = RFX.InvFilterH = RFX.InvFilterU = 0; }
      RFX.ArtListWanted = artReq; ApplyArtGate();   // an artifact request is hints only unless ArtAutoOn (the site's artAuto switch and the player's auto click): then only its Art* targets
      artPilot.Reset(); artAutoSaid = "";
      artFin.Reset(); artStep = null; artKey = ""; artSaid = ""; artFormAskedAt = 0;
      locateDoneUntil = 0; locateLive = null; lfin.Reset();
      locateSince = Stamp(); locateCaps = ""; locateLines = new string[0];
      locateStep = null; locateSubs = null; locateKey = ""; locateReadAt = 0; locateLastCur = -1; locateSubsAt = 0; lgrid.Reset();
      locateScreensSaid = ""; locateMarkerSaid = ""; locateCardSaid = ""; locateListSig = ""; locateDecisionSaid = ""; locatePlaceSaid = "";
      lpilot.Reset(); ResumeClicks(ClickOwner.Locate); ResumeClicks(ClickOwner.LocateFilter); lpilot.ItemUid = locateReq != null ? (artReq ? locateReq.Art.Uid : locateReq.Item.Uid) : 0; locateDriving = false; locateAutoSaid = "";
      Hide("off"); HideTip(); locHint.Reset();
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
    // (the plate is not hidden here: the page answers every step change, often with the same text, and a hide here showed as a blink;
    // the next tick redraws it when the text changed, the scene key holds the text)
    public void SetLocateTip(string[] lines, string caps, Dictionary<string, string> texts = null) { locateLines = lines ?? new string[0]; locateCaps = caps ?? ""; if (texts != null) locateTexts = texts; }

    void Run() {
      if (target != 0 || locateReq != null || filterItem != 0 || hintKind != "" || runHero != 0 || sellWanted != null || DateTime.Now < diagUntil || RecInfo.Active) { timer.Start(); if (target != 0) lookTimer.Start(); else lookTimer.Stop(); return; }
      timer.Stop(); lookTimer.Stop(); HideTip(); if (cancelWin.Visible) cancelWin.Hide();
    }

    void HideTip() { if (tip.Visible) tip.Hide(); tipKey = null; frameWins.Remove(tip); }

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
      pendingRects.Clear();
      var cv = covered;
      if (cv != null) { covered = null; gate.Halt(cv.Item1, "covered", "the click point is covered by " + cv.Item2 + ", not the game - no press"); AnnounceHalts(); }
      var lt = lateTop;
      if (lt != null) { lateTop = null; Log.Write("click gate " + lt.Item1 + ": not pressed - " + lt.Item2); gate.Halt(lt.Item1, "popup", lt.Item2 + " - not pressed"); AnnounceHalts(); }
      recB = RecInfo.Active ? new RecInfo() : null;
      RFX.LuaRootShadowTick();
      RFX.UiButtonShadowTick();
      AuditBegin();
      try { TickWork(); }
      finally {
        try { TickFramesOne(); } catch (Exception e) { Log.Write("frames: " + e.Message); }
        try { AuditEnd(); } catch (Exception e) { Log.Write("frame audit: " + e.Message); }
        if (recB != null) { var b = recB; recB = null; b.Screen = screens.Current != null ? screens.Current.Brief : "-"; RecOverlayNow(b); RecInputs(b); RecInfo.Publish(b); }
      }
    }

    // the recorder: the elements of every overlay window that is shown now (the frames, hint boxes, scroll indicator), for it to draw onto its frames
    void RecOverlayNow(RecInfo b) {
      try {
        if (!GameDrawable()) return;
        foreach (var w in new[] { glow, tip }) {
          List<RecOverlayEl> e;
          if (w.Visible && sceneEls.TryGetValue(w, out e)) b.Overlay.AddRange(e);
        }
      } catch (Exception ex) { Log.Write("rec overlay: " + ex.Message); }
    }

    // the recorder's raw inputs of this tick (src/RecInputs.cs): the guides' arguments and what decides whether they draw; the screen reads are queued by ReadScreens
    void RecInputs(RecInfo b) {
      try {
        IntPtr hwnd = GameWindow();
        bool sell = !LocateWins && sellHint && sellWanted != null;
        var want = new List<long>(sellWanted ?? new HashSet<long>()); want.Sort();
        var c = new RecCtx { Mode = LocateWins ? "locate" : sell ? "sell" : "", Front = hwnd != IntPtr.Zero && IsForeground(hwnd), Cols = locateCols, OnAccount = locateOnAccount,
          Req = LocateWins && locateReq.Item != null ? locateReq : null, Lines = LocateWins ? locateLines.Length > 0 : sellHintLines.Length > 0, CityInvSeen = cityInvSeen, InvDX = invDX, BtnDY = hg2.HeroesBtnDY,
          Want = sell ? want.ToArray() : new long[0], ShCols = shCols, Art = sell && sellArt, PillUp = sellPillUp != "", PillDown = sellPillDown != "" };
        b.Ctx = c.ToJson(); b.TickNow = NowMs(); b.ReadSeq = RecReads.Seq;
        if (sell) { b.SInv = RecIn.InvCore(b.SellInv); b.SInvList = RecIn.List(b.SellInv != null ? b.SellInv.List : null); }
      } catch (Exception) { }
    }

    // ---- TEST build: the explorer (src/Explorer.cs), started by the lab runner's explore.cmd (app/HostBridge.cs): it walks the game's safe UI
    // over the UI graph, every press through the click gate (owner Explore), and gives its result (explore.json) to ExploreDone
    Explorer explorer; long exploreBusyUntil; W32.POINT exploreCursor; bool exploreCursorSet;
    public bool ExploreRunning { get { return explorer != null; } }
    /// <summary>The run's result JSON when it ended (done or stopped).</summary>
    public event Action<string> ExploreDone;
    public string ExploreApp = "", ExploreGame = "";
    public void ExploreStart(int budget, int rot, string run) {
      if (!Channel.IsTest) return;
      if (explorer != null) ExploreStop("a new explore command");
      gate.Resume(ClickOwner.Explore); gate.ExploreOn = true; exploreCursorSet = false; exploreBusyUntil = 0;
      explorer = new Explorer(budget, rot, run);
      foreach (var l in explorer.Lines) Log.Write(l); explorer.Lines.Clear();
      Run();
    }
    public void ExploreStop(string why) {
      var ex = explorer; if (ex == null) return;
      ex.Stop(why); ExploreEnd(ex);
    }
    void ExploreEnd(Explorer ex) {
      foreach (var l in ex.Lines) Log.Write(l); ex.Lines.Clear();
      explorer = null; gate.ExploreOn = false;
      string json = ex.Json(NowMs(), ExploreApp, ExploreGame);
      var h = ExploreDone; if (h != null) try { h(json); } catch (Exception e) { Log.Write("explore: result not written: " + e.Message); }
    }
    void TickExplore() {
      var ex = explorer; long now = NowMs();
      if (gate.Halted(ClickOwner.Explore) && !ex.Over) ex.Stop("the click gate stopped: " + gate.HaltReason(ClickOwner.Explore) + " - " + gate.HaltDetail(ClickOwner.Explore));
      if (ex.Over) { ExploreEnd(ex); return; }
      if (sending || now < exploreBusyUntil) return;
      IntPtr hwnd = GameWindow();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd)) return;
      W32.RECT cr; if (!W32.GetClientRect(hwnd, out cr) || cr.B < 200) return;
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      bool front = IsForeground(hwnd);
      // the player's own mouse: the cursor left the explorer's last point (or a button is down) - the run stops at once
      W32.POINT c;
      if (W32.GetCursorPos(out c)) {
        bool down = (W32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (W32.GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (exploreCursorSet && (c.X != exploreCursor.X || c.Y != exploreCursor.Y || down)) { ex.Stop("the player's own input"); ExploreEnd(ex); return; }
      }
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      var v = new ExploreView { Now = now, Front = front, Order = RFX.UiKnown ? RFX.ReadShownOrder() : null, W = W, H = H, U = U,
                                Live = (f, fld) => RFX.UiFieldReadNow(f, fld, W, H, U), OfferClose = f => OfferForms.ClosePoint(Geo.Current, f, W, H, U) };
      var a = ex.Tick(v);
      foreach (var l in ex.Lines) Log.Write(l); ex.Lines.Clear();
      if (ex.Over) { ExploreEnd(ex); return; }
      if (a.Kind != ExploreKind.Click && a.Kind != ExploreKind.Escape) return;
      if (!front) return;   // (the game behind another window: waited for - the run's cap ends it)
      var fresh = FreshScreen(false);
      var req = new ClickRequest { Owner = ClickOwner.Explore, Target = a.Target, AutoOn = AutoEnabled, Front = front, X = a.X, Y = a.Y, W = W, H = H, U = U,
                                   Form = a.Form, Widget = a.Widget ?? "", LiveRect = a.LiveRect, Geo = Geo.Current };
      var res = gate.Check(req, fresh, now);
      if (!res.Allowed) { ex.Denied(a, res, gate.Halted(ClickOwner.Explore), now); foreach (var l in ex.Lines) Log.Write(l); ex.Lines.Clear(); return; }
      if (a.Kind == ExploreKind.Escape) {
        ex.Sent(a, now); Log.Write("explore: press " + a);
        SendEscapeKey(ClickOwner.Explore); exploreBusyUntil = now + 400; return;
      }
      int sx = o.X + (int)Math.Round(a.X), sy = o.Y + (int)Math.Round(a.Y);
      var vs = SystemInformation.VirtualScreen;
      if (!OwnButtons.AllInside(new[] { vs.X, vs.Y, vs.Width, vs.Height }, sx, sy, sy)) { ex.Stop(a + " lies off the screen (the game window is larger than the screen)"); ExploreEnd(ex); return; }
      ex.Sent(a, now); Log.Write("explore: press " + a);
      Send(new AutoAction { Kind = AutoKind.Click, X = a.X, Y = a.Y }, sx, sy, ClickOwner.Explore, a.Target, a.Form, freshOffers, unknownForms);
      exploreCursor = new W32.POINT(sx, sy); exploreCursorSet = true; exploreBusyUntil = now + 400;
    }

    /// <summary>TEST build: the recorder starts / stops. Our windows stay in screen captures while it runs (then the screen looks mask them themselves).</summary>
    public void SetRecording(bool on) {
      RecInfo.Active = on; GlowWindow.Capturable = on;
      recOn = on; if (on) recSw.Restart(); else recSw.Reset();
      if (on) RecReads.Clear();
      foreach (var w in new[] { glow, tip, glowPulse, tipPulse, cancelWin, recWin }) if (w.IsHandleCreated) w.ApplyAffinity();
      Run();
    }

    void TickWork() {
      curRow = curCol = 0; curSel = 0;
      // the frames over the inventory / Foundry lists follow a scroll the game renders at its own rate: those hints tick often
      // (the scroll is read every frame we draw); everything else stays at the slow tick
      // (src/LoadPlan.cs) a pilot's click on its way: its effect polled at 50 ms (the sell pilot's drag: its move); a list moving under
      // the frames: 33 ms; someone using the mouse: 100 ms; a hint or a pilot nobody touches: 250 ms; the game minimized or behind: 2 s
      inputAge = W32.InputAgeMs();
      IntPtr tw = GameWindow();
      bool twShown = tw != IntPtr.Zero && W32.IsWindowVisible(tw) && !W32.IsIconic(tw), twFront = twShown && IsForeground(tw);
      bool effect = gate.AnyPending || SellPolling || EquipHot;
      int wantInterval = LoadPlan.OverlayMs(ScreensWanted(), DateTime.Now < diagUntil, effect, twShown, twFront, LocateWins || (sellHint && sellWanted != null), listMoving, inputAge, SellGuideShown);
      if (timer.Interval != wantInterval) timer.Interval = wantInterval;
      loadMode = !ScreensWanted() ? "idle" : !twShown || !twFront ? "away" : effect || EquipClicking || SellClicking || (LocateWins && LocateClicking) ? "pilot" : "hint";
      if (Channel.IsTest) { long tn = NowMs(); if (modeAt != 0 && tn - modeAt > 0 && tn - modeAt < 5000) modeMs[loadMode == "away" ? 0 : loadMode == "hint" ? 1 : loadMode == "pilot" ? 2 : 3] += tn - modeAt; modeAt = tn; }
      ApplyArtGate();
      ReadScreens(NowMs());   // the game's screen, once per tick: every feature below takes it from here
      if (explorer != null) { TickExplore(); return; }   // (TEST build: the lab's explorer runs alone)
      UpdateArtStep();
      // the game is minimized / covered / alt-tabbed: nothing of ours stays over the desktop (the features go on reading)
      if (!GameDrawable()) { if (glow.Visible) glow.Hide(); HideTip(); drawnKey = null; }
      if (LocateWins) { TickLocate(); TickCancel(); return; }
      if (sellHint && sellWanted != null) { TickSellHint(); TickArtSellAuto(); TickCancel(); return; }
      if (sellWanted != null && spilot.State == "done") { TickAuto(); TickSellFinalAuto(); TickCancel(); return; }   // the auto run marked everything: the final plate and the frame on «Продать»
      TickFrame();
      TickAuto();
      TickCancel();
    }

    // «Отменить надевание»: top centre of the game (free on the hero screen and in the gear list), only while a started
    // build is on and the game is in front
    /// <summary>A storage cleanup guide is on the screen (the hint's frames and plates, or the final plate after the auto run): its reads stay active (LoadPlan), nothing hides for idleness.</summary>
    bool SellGuideShown { get { return sellWanted != null && (sellHint || spilot.State == "done"); } }

    /// <summary>The storage cleanup's auto click is running (not halted, not finished): the stop button is shown for it.</summary>
    bool SellClicking { get { return sellWanted != null && !sellFinal && (!sellHint || (sellArt && ArtAutoOn)) && runHero <= 0 && !LocateWins && !gate.Halted(ClickOwner.Sell) && spilot.State != "done" && spilot.State != "failed"; } }

    /// <summary>The artifact flows (storage cleanup, promotion) may click: the player's auto click AND the site's "artAuto" switch (off by default).</summary>
    public bool ArtAutoEnabled;
    bool ArtAutoOn { get { return AutoEnabled && ArtAutoEnabled; } }
    /// <summary>The Locate owner's way clicks (back arrows, «Инвентарь», Escape): an artifact request follows the artifact switch, the others the locateAuto one.</summary>
    bool LocateAutoOn { get { return locateReq != null && locateReq.Purpose == "artPromote" ? ArtAutoOn : AutoEnabled && LocateAutoEnabled; } }

    /// <summary>The gate's artifact rules follow the switches at every tick (a switch turned off mid-run: hints only again, at once).</summary>
    void ApplyArtGate() {
      bool on = ArtAutoOn, artReq = locateReq != null && locateReq.Purpose == "artPromote";
      gate.NoSellClicks = sellArt && !on; gate.ArtSellAuto = sellArt && on;
      gate.NoLocateClicks = artReq && !on; gate.ArtLocateAuto = artReq && on;
      gate.ArtEquipAuto = ArtPhase && on;
    }

    /// <summary>«Показать в игре» with auto click enabled for it: the stop button shows during the WHOLE run (not only while a click is being made),
    /// until the pilot stops (gate, cancel, stuck), the job is done or the last step (the player's own button) is reached.</summary>
    bool LocateClicking {
      get {
        if (locateReq != null && locateReq.Purpose == "artPromote") {
          // the artifact promotion: the stop button shows until the pilot stops, the job is done or the step is the player's own (the final «Продвинуть», «Улучшить» first ...)
          if (!ArtAutoOn || artPilot.Stopped || gate.Halted(ClickOwner.Locate) || locateDoneUntil != 0) return false;
          var ak = artStep != null ? artStep.Kind : LocateKind.None;
          return ak != LocateKind.ArtPromote && ak != LocateKind.ArtLevelFirst && ak != LocateKind.ArtMatNone && ak != LocateKind.ArtMatGuess && ak != LocateKind.ArtWrongArtifact && ak != LocateKind.NoItem && ak != LocateKind.None;
        }
        if (locateReq == null || !AutoEnabled || !LocateAutoEnabled || lpilot.Stopped || gate.Halted(ClickOwner.Locate) || locateDoneUntil != 0) return false;
        var k = locateStep != null ? locateStep.Kind : LocateKind.None;
        return k != LocateKind.Done && k != LocateKind.AscendReady && k != LocateKind.NoItem && k != LocateKind.OtherAccount;
      }
    }

    /// <summary>The equip pilots may click: a started build, or (auto click switched on while the job was already shown) the hint of a build with the game scanned.
    /// Follows AutoEnabled at every tick, so the stop button shows when auto click is switched on mid-job and hides when it is switched off.</summary>
    bool EquipClicking { get { return AutoEnabled && (runHero > 0 || (addrs != null && !LocateWins && sellWanted == null && (target != 0 || (hintKind != "" && hintKind != "done" && hintKind != "bulk")))); } }

    /// <summary>Something is shown or running that auto click could take over (the host starts the game scan for it when auto click is switched on).</summary>
    public bool HasJob { get { return PlanOn || locateReq != null || sellWanted != null; } }

    void TickCancel() {
      IntPtr hwnd = GameWindow();
      int fp = 0, gp = 0;
      if (hwnd != IntPtr.Zero) { W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp); W32.GetWindowThreadProcessId(hwnd, out gp); }
      W32.RECT cr;
      if (!(EquipClicking || (LocateWins && LocateClicking) || SellClicking) || !AutoEnabled || hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || fp != gp || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) {
        if (cancelWin.Visible) cancelWin.Hide();
        return;
      }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double U = Ui.Unit(cr.R, cr.B);
      int w = (int)(0.25 * U), h = (int)(0.058 * U);
      string cancelText = runHero > 0 ? CancelText : SellClicking ? SellCancelText : LocateWins && LocateClicking ? LocateCancelText : CancelText;   // (here only for a build, for «Показать в игре» or for the storage cleanup)
      var mp = Control.MousePosition; var cx = o.X + cr.R / 2 - w / 2; var cy = o.Y + (int)(0.012 * U);
      bool hot = mp.X >= cx && mp.X < cx + w && mp.Y >= cy && mp.Y < cy + h, down = hot && (Control.MouseButtons & MouseButtons.Left) != 0;
      string key = cancelText + "|" + w + "x" + h + "|" + (down ? "d" : hot ? "h" : "n");
      if (key != cancelDrawn || cancelBmp == null) {
        if (cancelBmp != null) cancelBmp.Dispose();
        cancelBmp = CancelBitmap(w, h, cancelText, hot, down); cancelDrawn = key;
      }
      cancelWin.Put(cancelBmp, o.X + cr.R / 2 - w / 2, o.Y + (int)(0.012 * U));
    }

    // the record button (TEST build): a round gold-rimmed dark button on the game's left edge at mid-height (d = 0.034 U): a red dot when idle,
    // a red rounded square while recording, with a plate «0:42» beside it. The central gate (GameDrawable) decides, as for every overlay drawing
    void TickRecBtn() {
      IntPtr hwnd = GameWindow();
      W32.RECT cr;
      if (!Channel.IsTest || !RecButton || !GameDrawable() || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { if (recWin.Visible) recWin.Hide(); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double U = Ui.Unit(cr.R, cr.B);
      int d = OwnButtons.RecDiameter(U);
      int[] rbox = OwnButtons.RecBox(cr.R, cr.B);
      int sec = recOn ? (int)(recSw.ElapsedMilliseconds / 1000) : 0;
      string key = d + "|" + (recOn ? "r" + sec : "i");
      if (key != recDrawn || recBmp == null) {
        if (recBmp != null) recBmp.Dispose();
        recBmp = RecBtnBitmap(d, recOn, sec / 60 + ":" + (sec % 60).ToString("00")); recDrawn = key;
      }
      recWin.HitW = recBmp.Height;
      recWin.Put(recBmp, o.X + rbox[0], o.Y + rbox[1]);
      recWin.Parts.Clear(); recWin.Parts.Add(new[] { o.X + rbox[0], o.Y + rbox[1], recBmp.Width, recBmp.Height });
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

    /// <summary>The cancel button in the plates' style: dark body, double gold frame, shadow, cream-gold text; hot = the mouse is over it (brighter inner line), down = pressed.</summary>
    static Bitmap CancelBitmap(int w, int h, string text, bool hot, bool down) {
      var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
      using (var gr = Graphics.FromImage(bmp)) {
        gr.SmoothingMode = SmoothingMode.AntiAlias; gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; gr.Clear(Color.Transparent);
        int bw = w - 4, bh = h - 6;   // room for the shadow (3 right, 5 below, as the plates)
        float x = 0, y = 0;
        using (var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) gr.FillRectangle(shadow, x + 3, y + 5, bw, bh);
        using (var bg = new SolidBrush(down ? Color.FromArgb(246, 34, 30, 22) : hot ? Color.FromArgb(244, 26, 26, 34) : Color.FromArgb(242, 16, 18, 26))) gr.FillRectangle(bg, x, y, bw, bh);
        using (var pen = new Pen(Color.FromArgb(255, hot || down ? 150 : 122, hot || down ? 114 : 92, hot || down ? 58 : 46), 2f)) gr.DrawRectangle(pen, x + 1, y + 1, bw - 2, bh - 2);
        using (var pen = new Pen(hot || down ? Color.FromArgb(255, 255, 214, 110) : Color.FromArgb(170, 224, 182, 96), 1f)) gr.DrawRectangle(pen, x + 3.5f, y + 3.5f, bw - 7, bh - 7);
        using (var f = new Font("Segoe UI", Math.Max(9f, bh * 0.34f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
          var r = new RectangleF(x, y + (down ? 1 : 0), bw, bh);
          using (var sh = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) gr.DrawString(text, f, sh, new RectangleF(r.X + 1, r.Y + 1.5f, r.Width, r.Height), sf);
          using (var cream = new SolidBrush(hot || down ? Color.FromArgb(255, 255, 226, 160) : Color.FromArgb(255, 255, 222, 150))) gr.DrawString(text, f, cream, r, sf);
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
      // the held «selected» step (hint "replace", the page's guide state): «Заменить» is the only frame; the item's frame and its «pick» plate never come back
      // from the list tracking (they alternated with it on mouse moves, 1.6.66.439)
      // (the pilot still needs the item's row and the game's selection to press «Заменить»: read here, nothing drawn)
      if (HintStepRule.ListFrameOff(hintKind)) {
        if (target > 0 && addrs != null) {
          int pc; ulong pl; int pr = RFX.RowOfUid(addrs, target, out pc, out pl);
          if (pr > 0 && pc > 0 && pl != 0) { curRow = pr; curCol = pc; }
          long ps = RFX.ReadSel(addrs); curSel = ps; selRow = selCol = 0;
          if (ps > 0) { int sc; ulong sl; int sr = RFX.RowOfUid(addrs, ps, out sc, out sl); if (sr > 0 && sl == pl) { selRow = sr; selCol = sc; } }
        }
        return;
      }
      if (target == 0 || addrs == null) { Hide("off"); return; }
      int col; ulong list;
      int row = RFX.RowOfUid(addrs, target, out col, out list);
      // (the selected item is a hot field of its own: read also while the target is not in the list read - a rebuilt list, the item shown as worn by another hero)
      if (row <= 0 || col <= 0 || list == 0) { curSel = RFX.ReadSel(addrs); selRow = selCol = 0; Hide("off"); return; }
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
      // where the game has put the item's cell (live, fresh): the frame hugs its picture; no cell of it in view or the list covered: no box
      W32.RECT gcr; IntPtr gwin = GameWindow(); double[] lcf = null; var see = UiCellSee.Unknown;
      if (target > 0 && gwin != IntPtr.Zero && W32.GetClientRect(gwin, out gcr) && gcr.B > 0)
        see = LiveCell(UiCellKind.HeroGear, target, null, gcr.R, H, Ui.Unit(gcr.R, H), new[] { left, top, cell, full }, "equip:" + target, out lcf);
      if (see == UiCellSee.Shown) { gearLiveSeen = target; gearLiveMs = NowMs(); Draw("box", o.X + (int)Math.Round(lcf[0]), o.Y + (int)Math.Round(lcf[1]), (int)Math.Round(lcf[2]), (int)Math.Round(lcf[3]), 0); }
      else if (see == UiCellSee.Unknown && gearLiveSeen == target && target > 0 && NowMs() - gearLiveMs < 2500 && mid >= g.ViewTop * H && mid <= g.ViewBottom * H) { dKind = "held"; return; }   // read live before: the live frame stays, never the layout's in between
      else if (see == UiCellSee.Hidden && mid >= g.ViewTop * H && mid <= g.ViewBottom * H) { if (glow.Visible) glow.Hide(); drawnKey = null; return; }
      else if (mid < g.ViewTop * H) Draw("up", o.X + (int)left, o.Y + sy, (int)cell, 0, (int)Math.Ceiling((g.ViewTop * H - top) / pitch), strip);
      else if (mid > g.ViewBottom * H) Draw("down", o.X + (int)left, o.Y + sy + sh, (int)cell, 0, (int)Math.Ceiling((top + full - g.ViewBottom * H) / pitch), strip);
      else Draw("box", o.X + (int)left, o.Y + (int)top, (int)cell, (int)full, 0);
      dX -= o.X; dY -= o.Y;   // (Draw keeps screen px)
    }
    int frameRow, frameCol;
    long gearLiveSeen, gearLiveMs;   // the item whose cell was read live in this hint and when (its frame stays live up to 2.5 s without a read)

    // A quick look between the frame ticks (every 30 ms): a list flicked by the player moves up to a few thousand px a
    // second - in 100 ms further than the strip is high, and then its picture fits nowhere or somewhere wrong.
    void LookFast() {
      if (!LoadPlan.LookFast(W32.InputAgeMs())) return;   // a list nobody scrolls stays where the tick's own look saw it
      if (LocateWins || HintStepRule.ListFrameOff(hintKind) || target == 0 || target != lookTarget || addrs == null || types == null || !lookOk || !anchor.Has || filterState != "" || typesPtr != lookList) return;
      IntPtr hwnd = GameWindow();
      if (hwnd == IntPtr.Zero || !IsForeground(hwnd)) return;
      long l0 = LoadMeter.Now();
      bool sure = LookAtList(lookO, lookH, NowMs());
      if (sure) PlaceFrame(lookO, lookH); else NeedClick();
      LoadMeter.Done(LoadMeter.Look, l0);
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
      confirmBtn = null; confirmStill = false; confirmBlueNone = false; filterOpen = false; filterState = "";
      if ((hintKind == "" && target == 0) || addrs == null) { HideTip(); return; }
      IntPtr hwnd = GameWindow();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || !IsForeground(hwnd)) { HideTip(); return; }
      W32.RECT cr; if (!W32.GetClientRect(hwnd, out cr) || cr.B < 200) { HideTip(); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      GeomCheck(hwnd, o, cr);
      double H = cr.B, W = cr.R, U = Ui.Unit(W, H);
      SceneArea(o, cr);
      // the Inventory is in «Массовая продажа»: the hero and the items are not on screen - the way out is framed (the app clicks nothing there)
      var bs = screens.Current;
      if (bs != null && bs.Kind == ScreenKind.Inventory && bs.InvEquipTab && bs.InvBulk && Tx("bulkExit") != "") {
        var br = Geo.Current != null ? Geo.Current.Rect("inventory.bulkBack", W, H, U) : null;
        bool bl; var bb = br != null ? FrameForI("inventory.bulkBack", new[] { (int)br.X, (int)br.Y, (int)br.W, (int)br.H }, W, H, U, out bl) : null;
        if (bb != null) DrawTip(o.X + bb[0], o.Y + bb[1], bb[2], bb[3], new[] { Tx("bulkExit") }, H);
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
      if (hintKind == "art") { TickArtHint(o, W, H); return; }
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
      // the next slot with the item's tip / compare still open: it covers the slots - no frame on a slot under it (the pilot closes it first:
      // EquipTipEscape; live 1.6.66.354 the frame sat on the compare card's ATK row and the pilot clicked there)
      if (hintKind == "slot" && ClickGate.OnlyTip(screens.Current)) { HideTip(); return; }
      if (hintKind == "replace") {
        confirmBtn = ConfirmButton(o, W, H);
        // «Заменить» where the game has put it (live rect, fresh: its drawn part, 2 px round it), else the picture's / the geometry's
        var lb = hg.Replace(W, H); var lay = confirmBtn ?? lb;
        bool cl; var cf = FrameForI(FrameTargets.KeyConfirm, lay, W, H, U, out cl);
        if (cl && cf != null) confirmBtn = cf;
        confirmStill = confirmLive.Note(cl && cf != null ? new double[] { cf[0], cf[1], cf[2], cf[3] } : null, NowMs());   // (the card has stopped opening: AutoView.ButtonStill)
        // the frame and the plate beside it stand on the live rect only, from its first read on the shown tip (the frame's own steadiness holds the
        // jitter; only the pilot's click waits for the confirming read, AutoView.ButtonStill); until then the plate alone at the top
        if (!ConfirmFrame.Ready(cl && cf != null, EquipTipShown())) { DrawTipTop(hintLines); return; }
        confirmHintRect = cf;
      }
      bool rl;
      int[] r = hintKind == "filter" ? FrameForI("hero.filter", hg.Filter(W, H), W, H, U, out rl) : hintKind == "replace" ? confirmHintRect
        : hintKind == "slot" ? FrameForI("hero.slot." + hintSlot, hg.Slot(hintSlot, W, H), W, H, U, out rl) : null;
      // the hero is open on another tab: its «Снаряжение» tab (right edge)
      if (hintKind == "geartab") { int s = (int)(0.1 * U); r = FrameForI("hero.tab.equip", new[] { (int)(W - hg2.GearTabDX * U) - s / 2, (int)(hg2.GearTabY * U) - s / 2, s, s }, W, H, U, out rl); }
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
      int rr = Math.Max(8, (int)(0.028 * U)); bool cl;
      var cr = FrameForI("hero.filterClose", new[] { (int)fx - rr, (int)fy - rr, 2 * rr, 2 * rr }, sceneW, H, U, out cl);
      DrawTip(o.X + cr[0], o.Y + cr[1], cr[2], cr[3], string.IsNullOrEmpty(text) ? new string[0] : new[] { text }, H);
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
          int s = (int)(0.1 * U); bool hl;
          var hb = FrameForI(FrameTargets.KeyCityHeroes, new[] { (int)(W - hg2.HeroesBtnDX * U) - s / 2, (int)(H - 0.1156 * U) - s / 2, s, s }, W, H, U, out hl);
          DrawTip(o.X + hb[0], o.Y + hb[1], hb[2], hb[3], HeroTip(4), H, false, false, true);
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
        bool bl; var bk = FrameForI(FrameTargets.KeyHeroBack, new[] { (int)(hg2.BackX * U) - s / 2, (int)(hg2.BackY * U) - s / 2, s, s }, W, H, U, out bl);   // the back arrow where the game has put it, else the geometry's
        DrawTip(o.X + bk[0], o.Y + bk[1], bk[2], bk[3], HeroTip(5), H, false, true);
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
      double ctop = heroAnchor.Row1Top + (t / HeroGeometry.Columns) * pitch, ccx = hg2.ColX(t % HeroGeometry.Columns) * U, cwid = 0.115 * U;
      // the card is framed only on its live cell (the hero grid's cell root), and only when that cell is bound to this hero at this hero's place in
      // the list (the read's index = the scan's); otherwise no frame on the cards - a frame from the layout stood between two cards
      double[] hcf; var hsee = LiveCell(UiCellKind.HeroList, hintHero, null, W, H, U, new double[] { ccx - cwid / 2, ctop, cwid, card }, "hero:" + hintHero, out hcf);
      if (hsee == UiCellSee.Shown && hcf != null && HeroCellSure(hintHero, t, W, H)) {
        heroDiag += " card=live";
        DrawTip(o.X + (int)Math.Round(hcf[0]), o.Y + (int)Math.Round(hcf[1]), (int)Math.Round(hcf[2]), (int)Math.Round(hcf[3]), HeroTip(0), H, false, hcf[1] < 0.35 * H);
        return;
      }
      if (!sure) { DrawTipTop(HeroTip(1)); return; }
      if (hsee != UiCellSee.Shown && ctop + card / 2 < vt) DrawTip(o.X + lx0, o.Y + lvt, lw, 4, HeroTip(2), H, true);
      else if (hsee != UiCellSee.Shown && ctop + card / 2 > vb) DrawTip(o.X + lx0, o.Y + lvt + lh - 4, lw, 4, HeroTip(3), H, true);
      else { heroDiag += " card=unsure"; DrawTipTop(HeroTip(1)); }
    }

    /// <summary>The hero list's live read binds hero <paramref name="hero"/> to exactly one cell, at list index <paramref name="index"/> (the scan's
    /// order), fresh, for this window size: then its cell is the card. Else unsure (no frame).</summary>
    bool HeroCellSure(long hero, int index, double W, double H) {
      var s = RFX.UiCellFresh(UiCellKind.HeroList, hero, W, H, Environment.TickCount, RFX.UiFrameFreshMs);
      return CellFrame.HeroCellSure(s, hero, index);
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

    // the game window's client area on the screen for the scene being drawn (plates are kept inside it)
    int sceneX, sceneY, sceneW, sceneH;
    void SceneArea(W32.POINT o, W32.RECT cr) { sceneX = o.X; sceneY = o.Y; sceneW = cr.R; sceneH = cr.B; }

    /// <summary>A pulsing gold frame around a button of the game (x, y, w, h: screen px) with the step plate beside it
    /// (plateOnly: the plate alone, beside that spot). below / right: kept for the callers, the plate finds its own side.</summary>
    /// <summary>A plate with no target on screen (nothing to frame): top centre of the game window, for every guide.</summary>
    void DrawTipTop(string[] lines, string caps = null, int[][] avoid = null, bool warn = false) {
      if (lines == null || lines.Length == 0) { HideTip(); return; }
      Scene(tip, ref tipKey, null, new Plate { Caps = caps ?? Tx("caps"), Main = lines[0], Sub = lines.Length > 1 ? lines[1] : "", Warn = warn }, null, null, null, null, avoid);
    }

    /// <param name="avoid">the step's other click targets (screen px {x, y, w, h}): the plate is never put over them (nor over the target)</param>
    void DrawTip(int x, int y, int w, int h, string[] lines, double H, bool plateOnly = false, bool below = false, bool right = false, string caps = null, bool warn = false, Hl hl = null, int[][] avoid = null) {
      var p = lines != null && lines.Length > 0 ? new Plate { Caps = caps ?? Tx("caps"), Main = lines[0], Sub = lines.Length > 1 ? lines[1] : "", Warn = warn } : null;
      if (hl == null && !GuideStyle && !plateOnly) hl = HlLive(x, y, w, h);   // a live frame with its sprite's outline (LiveFrame / LiveCell)
      Scene(tip, ref tipKey, plateOnly ? null : new[] { x, y, w, h }, p, new[] { x, y, w, h }, null, null, hl, avoid);
    }

    // the plate's sizes tried in turn when it does not fit beside its target (the smallest is taken anyway)
    static readonly float[] PlateScales = { 1f, 0.85f, 0.72f, 0.6f };


    // the plate's texts cut with «…» so that it is at most maxW px wide (0 = no limit); the plate itself is not changed
    static Plate FitPlate(Plate p, Font fCaps, Font fMain, Font fSub, float pad, int maxW, Graphics pg) {
      if (p == null || maxW <= 0) return p;
      float owl = fCaps.Height * 1.5f, room = maxW - 2 * pad;
      float arrow = p.Arrow != 0 && p.Main != "" ? fMain.Height * 0.8f + 10 : 0;
      return new Plate {
        Caps = p.Caps == "" ? "" : HintSize.Ellipsize(p.Caps.ToUpperInvariant(), room - owl - 6, t => pg.MeasureString(t, fCaps).Width),
        Main = HintSize.Ellipsize(p.Main, room - arrow, t => pg.MeasureString(t, fMain).Width),
        Sub = HintSize.Ellipsize(p.Sub, room, t => pg.MeasureString(t, fSub).Width),
        Pill = p.Pill, Warn = p.Warn, Arrow = p.Arrow
      };
    }

    static Plate PlateSize(Plate p, Font fCaps, Font fMain, Font fSub, float pad, int maxW, out int pw, out int ph, out float owl, out float capsH, out float mainH, out float subH) {
      owl = fCaps.Height * 1.5f;
      using (var probe = new Bitmap(1, 1)) using (var pg = Graphics.FromImage(probe)) {
        p = FitPlate(p, fCaps, fMain, fSub, pad, maxW, pg);
        float cw = p.Caps != "" ? owl + 6 + pg.MeasureString(p.Caps.ToUpperInvariant(), fCaps).Width : 0;
        float mw = p.Main != "" ? pg.MeasureString(p.Main, fMain).Width : 0, sw = p.Sub != "" ? pg.MeasureString(p.Sub, fSub).Width : 0;
        capsH = p.Caps != "" ? Math.Max(owl, fCaps.Height) : 0; mainH = p.Main != "" ? fMain.Height : 0; subH = p.Sub != "" ? fSub.Height : 0;
        if (p.Arrow != 0 && p.Main != "") mw += fMain.Height * 0.8f + 10;   // the arrow before the text
        pw = (int)(Math.Max(cw, Math.Max(mw, sw)) + 2 * pad);
        ph = (int)(pad * (p.Pill ? 1.0f : 2f) + capsH + mainH + subH + (capsH > 0 ? 2 : 0) + (subH > 0 ? 2 : 0));
      }
      return p;
    }

    static void MeasurePlate(Plate p, double U, float sc, int maxW, out int pw, out int ph) {
      float pad = PlatePad(U, sc), owl, capsH, mainH, subH;
      using (var a = PlateFont(0, p, U, sc)) using (var b = PlateFont(1, p, U, sc)) using (var c = PlateFont(2, p, U, sc)) PlateSize(p, a, b, c, pad, maxW, out pw, out ph, out owl, out capsH, out mainH, out subH);
    }

    // the plate's layout per window, held while its step stays (src/HintLayout.cs PlateHold): no move or resize tick by tick
    readonly Dictionary<GlowWindow, PlateHold> plateHold = new Dictionary<GlowWindow, PlateHold>();

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

    /// <summary>The hints pulse (a repaint every tick); off: they stand still and are drawn only on a change (the player's PC is not loaded).</summary>
    static readonly bool HintPulse = false;

    GlowWindow frameWinTick; readonly HashSet<GlowWindow> frameWins = new HashSet<GlowWindow>(), sceneWins = new HashSet<GlowWindow>();
    /// <summary>End of a tick: a window that still shows a frame while another one drew a frame this tick, and was not drawn itself, is hidden
    /// (the new frame replaces the old one in the same paint).</summary>
    void TickFramesOne() {
      if (frameWinTick != null)
        foreach (var w in new[] { tip, glow })
          if (w != frameWinTick && !sceneWins.Contains(w) && frameWins.Contains(w)) {
            frameWins.Remove(w); if (w.Visible) w.Hide();
            if (w == tip) tipKey = null; else drawnKey = null;
          }
      frameWinTick = null; sceneWins.Clear();
    }

    void Scene(GlowWindow win, ref string key, int[] frameR, Plate p, int[] target, double[] strip, List<int[]> marks = null, Hl hl = null, int[][] avoid = null) {
      if (strip == null && PendingFrame(frameR)) { frameR = null; hl = null; }   // the target's live frame is awaited: the plate alone, the frame comes once, live
      if (!GameDrawable()) { if (win.Visible) win.Hide(); key = null; frameWins.Remove(win); return; }
      // one frame at a time: the first frame of a tick is drawn, a second window's own frame waits (its plate stays); the window of an older frame
      // that no scene reached this tick is taken down in the same tick (TickFramesOne)
      if (frameR != null && frameWinTick != null && frameWinTick != win) { frameR = null; hl = null; }
      if (frameR != null) { frameWinTick = win; frameWins.Add(win); } else frameWins.Remove(win);
      sceneWins.Add(win);
      if (recB != null) RecScene(win, frameR, strip, marks);
      if (auditOn) auditScenes.Add(new AuditScene { Win = win, Frame = frameR, Step = p != null ? FrameAudit.StepKey(p.Caps, p.Main) : "-" });
      // the frames and the new highlight stand still at their brightest (they pulsed: a repaint and a copy of the whole picture, up to the
      // game's client with the dimming, every tick a hint was shown); the picture is drawn again only when what it shows changes
      float pulse = HintPulse ? (float)(0.6 + 0.4 * Math.Sin(frame * 0.45)) : 1f;
      // the new highlight (switch guideStyle): a frame with a Hl is drawn by PaintGuide; off = the old frame, nothing below changes
      bool ng = GuideStyle && hl != null && frameR != null, dim = ng && !hl.Important;
      // the old frame (guideStyle off) follows a round / bevelled button's outline; rect shapes and targets without geometry keep the rectangle
      bool oldShape = !GuideStyle && hl != null && frameR != null && GuideShape.FrameOutline(hl.Rect, hl.Shape, hl.Inset, 2) != null;
      long tms = (uint)Environment.TickCount;
      guideLvl = HintPulse ? (int)Math.Round(5 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * (tms % 1200) / 1200.0))) : 5;   // the pulse, 1.2 s, in 6 steps
      int bob = dim ? (HintPulse ? (int)Math.Round(2 + 2 * Math.Sin(2 * Math.PI * (tms % 1600) / 1600.0)) : 2) : 0;   // the hand's bob, 4 px
      Image hand = dim ? GuideHand.Get(GuideSite) : null;
      string k = (frameR != null ? string.Join(",", frameR) : "-") + "|" + (target != null ? string.Join(",", target) : "-") + "|"
        + (p != null ? p.Caps + "/" + p.Main + "/" + p.Sub + "/" + p.Pill + "/" + p.Warn : "-") + "|" + (strip != null ? string.Join(",", Array.ConvertAll(strip, v => ((int)v).ToString())) : "-")
        + "|" + (frameR != null || strip != null ? (ng ? 0 : (int)(pulse * 10)) : 0) + "|" + sceneW + "x" + sceneH
        + (ng ? "|g" + guideLvl + "," + bob + (hand != null ? "h" : "") + (hl.Important ? "i" : "") + (hl.Shape != null ? hl.Shape.K : "") : GuideStyle && marks != null && marks.Count > 0 ? "|g" + guideLvl : "")
        + (oldShape ? "|o" + hl.Shape.K + (int)hl.Rect.X + "," + (int)hl.Rect.Y + "," + (int)hl.Rect.W + "," + (int)hl.Rect.H : "")
        + (marks != null && marks.Count > 0 ? "|m" + string.Join(";", marks.ConvertAll(m => string.Join(",", m))) : "")
        + (avoid != null && avoid.Length > 0 ? "|a" + string.Join(";", Array.ConvertAll(avoid, a => a != null ? string.Join(",", a) : "-")) : "");
      if (k == key) return;
      key = k;
      long d0 = LoadMeter.Now();
      try { SceneDraw(win, frameR, p, target, strip, marks, hl, avoid, pulse, ng, dim, oldShape, hand, bob); } finally { LoadMeter.Done(LoadMeter.Draw, d0); }
    }

    void SceneDraw(GlowWindow win, int[] frameR, Plate p, int[] target, double[] strip, List<int[]> marks, Hl hl, int[][] avoid, float pulse, bool ng, bool dim, bool oldShape, Image hand, int bob) {
      double U = Ui.Unit(Math.Max(1, sceneW), Math.Max(1, sceneH));
      SetPulse(win, null); var pulseSpecs = new List<FrameSpecT>();
      // the step's other click targets in client px (the plate keeps off them and off its own target)
      int[][] av = avoid == null ? null : Array.ConvertAll(avoid, a => a == null ? null : new[] { a[0] - sceneX, a[1] - sceneY, a[2], a[3] });
      int[] tcl = target != null ? new[] { target[0] - sceneX, target[1] - sceneY, target[2], target[3] } : null;
      // a small game window: one short line (the step and the action); the app's window has the whole text
      string stepId = p != null ? p.Caps + "|" + (p.Pill ? "pill" : "") + "|" + sceneW + "x" + sceneH : null;
      if (p != null && !p.Pill && HintSize.Compact(U)) p = new Plate { Main = HintSize.CompactLine(p.Caps, p.Main != "" ? p.Main : p.Sub), Warn = p.Warn, Arrow = p.Arrow };
      int maxW = p != null && !p.Pill ? HintSize.MaxWidth(sceneW) : 0;
      // the layout held for this step (same target and neighbours within a few px): scale and place stay, only the text is drawn anew
      PlateHold hold; if (!plateHold.TryGetValue(win, out hold)) plateHold[win] = hold = new PlateHold();
      bool held = p != null && strip == null && hold.Keep(stepId, tcl, av);
      // the plate's size: full, or smaller steps until it fits beside the target without covering a button (a tiny game window)
      float sc = held ? hold.Scale : 1f;
      if (!held && p != null && strip == null && tcl != null) {
        foreach (float s in PlateScales) {
          sc = s; int tw, th; MeasurePlate(p, U, s, maxW, out tw, out th);
          char sd; if (HintLayout.TryPlace(tcl, av, tw, th, sceneW, sceneH, out sd) != null) break;
        }
      }
      float pad = PlatePad(U, sc);
      using (var fCaps = PlateFont(0, p, U, sc))
      using (var fMain = PlateFont(1, p, U, sc))
      using (var fSub = PlateFont(2, p, U, sc)) {
        // the plate's size
        int pw = 0, ph = 0; float owl = fCaps.Height * 1.5f, capsH = 0, mainH = 0, subH = 0;
        if (p != null) p = PlateSize(p, fCaps, fMain, fSub, pad, maxW, out pw, out ph, out owl, out capsH, out mainH, out subH);
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
            if (held) { pos = new[] { hold.Pos[0], hold.Pos[1] }; side = hold.Side; if (target != null && HintLayout.OnTarget(pos[0], pos[1], pw, ph, tc)) { held = false; pos = HintLayout.Place(tc, av, pw, ph, sceneW, sceneH, out side); } }
            else if (target == null) pos = HintLayout.TopPlateFree(pw, ph, sceneW, sceneH, U, av);   // no target: top centre (off the step's buttons)
            else pos = HintLayout.Place(tc, av, pw, ph, sceneW, sceneH, out side);
            // the text grew on a held place: kept inside the window
            pos[0] = Math.Max(HintLayout.Margin, Math.Min(sceneW - pw - HintLayout.Margin, pos[0])); pos[1] = Math.Max(HintLayout.Margin, Math.Min(sceneH - ph - HintLayout.Margin, pos[1]));
            if (target != null && (frameR != null || tc[2] > 0)) ptr = HintLayout.Pointer(pos, pw, ph, side, tc, Math.Max(7, 0.01 * U));
            if (!held && hold.Made(stepId, tcl, av, sc, new[] { pos[0], pos[1] }, side) && hold.LastStep != null)
              Log.Write("hint layouts: " + hold.LastCount + " for step «" + hold.LastStep + "»");   // 1 = laid out once, never moved
          }
        }
        auditPlate[win] = pos != null ? new double[] { sceneX + pos[0], sceneY + pos[1], pw, ph } : null;   // (the frame audit: the plate it must keep off)
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
        // the scene as elements: each is painted by the same code into the overlay window's bitmap here and onto the recorder's frames (Composite)
        var els = SceneElements(win, frameR, p, strip, marks, hl, ng, dim, oldShape, hand, bob, G, Gm, U, pos, pw, ph, sc, owl, capsH, mainH, pad, ptr, pulseSpecs, avoid);
        sceneEls[win] = els;
        using (var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb)) {
          using (var gr = Graphics.FromImage(bmp)) {
            gr.SmoothingMode = SmoothingMode.AntiAlias; gr.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; gr.Clear(Color.Transparent);
            gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
            foreach (var el in els) el.Draw(gr, x0, y0, bw, bh, -1f);
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
      SetPulse(win, pulseSpecs);
    }

    // the last scene of each overlay window as elements (what the recorder draws onto its frames while the window is shown)
    readonly Dictionary<GlowWindow, List<RecOverlayEl>> sceneEls = new Dictionary<GlowWindow, List<RecOverlayEl>>();

    List<RecOverlayEl> SceneElements(GlowWindow win, int[] frameR, Plate p, double[] strip, List<int[]> marks, Hl hl, bool ng, bool dim, bool oldShape, Image hand, int bob,
        int G, int Gm, double U, int[] pos, int pw, int ph, float sc, float owl, float capsH, float mainH, float pad, double[] ptr, List<FrameSpecT> pulseSpecs, int[][] avoid) {
      var els = new List<RecOverlayEl>();
      string src = win == tip ? "tip" : "glow"; int sx = sceneX, sy = sceneY;
      Func<int, int, int, int, string, Action<Graphics, int, int, int, int, float>, RecOverlayEl> add = (x, y, w, h, key, paint) => {
        var e = Element(key, src, sx, sy, x - sx, y - sy, w, h, paint); els.Add(e); return e;
      };
      if (ng) {
        var e = add((int)hl.Rect.X, (int)hl.Rect.Y, (int)hl.Rect.W, (int)hl.Rect.H, "guide", (g, ox, oy, bw, bh, f) => PaintGuide(g, hl, ox, oy, bw, bh, dim, G, hand, bob, U));
        e.Layer = dim;
      } else if (frameR != null) {
        var fo = new List<double[]>(); if (avoid != null) foreach (var a in avoid) if (a != null) fo.Add(new double[] { a[0], a[1], a[2], a[3] });
        var fs = FrameSpec(oldShape ? hl : null, frameR[0], frameR[1], frameR[2], frameR[3], FrameStyle.Gold, FrameStyle.Gap(frameR[0], frameR[1], frameR[2], frameR[3], fo));
        add(frameR[0], frameR[1], frameR[2], frameR[3], "frame", (g, ox, oy, bw, bh, f) => PaintFrame(g, fs, ox, oy));
        if (!oldShape) {
          var at = FrameStyle.OwlAt(frameR[0], frameR[1], frameR[2], frameR[3], sy);
          add((int)at[0] - 9, (int)at[1] - 9, 18, 18, "owl", (g, ox, oy, bw, bh, f) => PaintOwl(g, at, ox, oy));
        }
        pulseSpecs.Add(fs);
      }
      if (strip != null) {
        var s = strip;
        add((int)s[0] - 12, (int)s[1], 24, (int)(s[2] - s[1]), "strip", (g, ox, oy, bw, bh, f) => PaintStrip(g, s, ox, oy, 1f, U));
      }
      if (marks != null) {
        var mo = new List<double[]>(); foreach (var m in marks) mo.Add(new double[] { m[0], m[1], m[2], m[3] });
        foreach (var m0 in marks) {
          var m = m0;
          var ms = m[4] != 1 && !(m[4] == 0 && GuideStyle) ? FrameSpec(null, m[0], m[1], m[2], m[3], m[4] == 2 ? FrameStyle.Red : FrameStyle.Gold, FrameStyle.Gap(m[0], m[1], m[2], m[3], mo)) : null;
          add(m[0], m[1], m[2], m[3], "mark" + m[4], (g, ox, oy, bw, bh, f) => PaintMark(g, m[0] - ox, m[1] - oy, m[2], m[3], m[4], Gm, ms, ox, oy));
          if (m[4] == 0 && !GuideStyle) {   // the owl badge on the frames to mark, as on every other hint frame
            var at = FrameStyle.OwlAt(m[0], m[1], m[2], m[3], sy);
            add((int)at[0] - 9, (int)at[1] - 9, 18, 18, "owl", (g, ox, oy, bw, bh, f) => PaintOwl(g, at, ox, oy));
          }
          if (ms != null) pulseSpecs.Add(ms);
        }
      }
      if (p != null) {
        var pp = p;
        add(sx + pos[0], sy + pos[1], pw, ph, "plate", (g, ox, oy, bw, bh, f) => {
          using (var fC = PlateFont(0, pp, U, sc)) using (var fM = PlateFont(1, pp, U, sc)) using (var fS = PlateFont(2, pp, U, sc))
            PaintPlate(g, sx + pos[0] - ox, sy + pos[1] - oy, pw, ph, pp, fC, fM, fS, owl, capsH, mainH, pad, ptr, sx - ox, sy - oy);
        });
      }
      // the pulse line of the frames (a window of its own on screen): over everything, a recording catches the phase of the moment
      if (pulseSpecs.Count > 0) {
        var specs = new List<FrameSpecT>(pulseSpecs); int cw = sceneW, ch = sceneH;
        var pe = Element("pulse", src, sx, sy, 0, 0, 0, 0, (g, ox, oy, bw, bh, phase) => {
          if (phase < 0) return;
          int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, M = 12;
          foreach (var sp in specs) { x0 = Math.Min(x0, sp.Box[0] - M); y0 = Math.Min(y0, sp.Box[1] - M); x1 = Math.Max(x1, sp.Box[0] + sp.Box[2] + M); y1 = Math.Max(y1, sp.Box[1] + sp.Box[3] + M); }
          if (cw > 0 && ch > 0) { x0 = Math.Max(x0, sx); y0 = Math.Max(y0, sy); x1 = Math.Min(x1, sx + cw); y1 = Math.Min(y1, sy + ch); }
          if (x1 - x0 <= 0 || y1 - y0 <= 0 || (long)(x1 - x0) * (y1 - y0) > 250000) return;   // a big spread of frames stays still
          foreach (var sp in specs) { var l = FrameStyle.Pulse(sp.Rgb, phase, sp.Gap); if (l != null) StrokeLayer(g, sp, l, ox, oy); }
        }, false);
        els.Add(pe);
      }
      return els;
    }

    /// <summary>Where our own windows showed something that a screen look sees (only when they cannot be hidden from
    /// captures): those pixels of a grab at (x, y) w × h are set to the dark background, so they are no bars, no glow, no cells.</summary>
    void MaskOwn(int[] px, int x, int y, int w, int h) {
      if (px == null) return;
      foreach (var win in new[] { glow, tip, glowPulse, tipPulse, recWin })
        if (!win.Excluded && win.Visible)
          foreach (var r in win.Parts) {
            int ax = Math.Max(x, r[0]), ay = Math.Max(y, r[1]), bx = Math.Min(x + w, r[0] + r[2]), by = Math.Min(y + h, r[1] + r[3]);
            for (int yy = ay; yy < by; yy++) for (int xx = ax; xx < bx; xx++) px[(yy - y) * w + (xx - x)] = ListTracker.Masked;
          }
    }

    // the pulse: one small window beside the scene window, repainted at most 15 times a second, only while a frame is shown
    sealed class PulseState { public List<FrameSpecT> Specs; }
    readonly Dictionary<GlowWindow, PulseState> pulses = new Dictionary<GlowWindow, PulseState>();

    void SetPulse(GlowWindow win, List<FrameSpecT> specs) {
      if (specs == null || specs.Count == 0) { pulses.Remove(win); GlowWindow pw0 = win == tip ? tipPulse : glowPulse; if (pw0.Visible) pw0.Hide(); return; }
      pulses[win] = new PulseState { Specs = specs };
      if (!pulseTimer.Enabled) pulseTimer.Start();
    }

    void PulseTick() {
      float phase = FrameStyle.Phase((uint)Environment.TickCount);
      int live = 0;
      foreach (var kv in new List<KeyValuePair<GlowWindow, PulseState>>(pulses)) {
        var win = kv.Key; var pw = win == tip ? tipPulse : glowPulse;
        if (!win.Visible || !GameDrawable()) { if (pw.Visible) pw.Hide(); continue; }
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, M = 12;
        foreach (var sp in kv.Value.Specs) { x0 = Math.Min(x0, sp.Box[0] - M); y0 = Math.Min(y0, sp.Box[1] - M); x1 = Math.Max(x1, sp.Box[0] + sp.Box[2] + M); y1 = Math.Max(y1, sp.Box[1] + sp.Box[3] + M); }
        if (sceneW > 0 && sceneH > 0) { x0 = Math.Max(x0, sceneX); y0 = Math.Max(y0, sceneY); x1 = Math.Min(x1, sceneX + sceneW); y1 = Math.Min(y1, sceneY + sceneH); }
        int bw = x1 - x0, bh = y1 - y0;
        if (bw <= 0 || bh <= 0 || (long)bw * bh > 250000) { if (pw.Visible) pw.Hide(); continue; }   // a big spread of frames stays still
        live++;
        using (var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb)) {
          using (var gr = Graphics.FromImage(bmp)) {
            gr.SmoothingMode = SmoothingMode.AntiAlias; gr.Clear(Color.Transparent);
            foreach (var sp in kv.Value.Specs) { var l = FrameStyle.Pulse(sp.Rgb, phase, sp.Gap); if (l != null) StrokeLayer(gr, sp, l, x0, y0); }
          }
          pw.Put(bmp, x0, y0);
          pw.Parts.Clear(); pw.Parts.Add(new[] { x0, y0, bw, bh });
        }
      }
      if (live == 0) pulseTimer.Stop();
    }

    // a cell frame among many (the storage cleanup): style 0 = to select (the game's gold frame, steady), 1 = selected (a thin
    // dim gold line with a check mark), 2 = selected but not on the list (red)
    void PaintMark(Graphics gr, float x, float y, float w, float h, int style, int G, FrameSpecT sp, int ox, int oy) {
      if (style == 0) {
        if (GuideStyle) PaintGuide(gr, HlCell(0, 0, 0, 0), 0, 0, 0, 0, false, G, null, 0, 0, new GeoRect { X = x, Y = y, W = w, H = h });   // storage cleanup: the gold edge of the new highlight
        else PaintFrame(gr, sp, ox, oy);
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
      // style 2: the same branded frame in red (no owl), and a clear red ✕ badge in the cell's top right corner: this item is marked but is not on the list
      PaintFrame(gr, sp, ox, oy);
      float br = Math.Max(9f, Math.Min(w, h) * 0.14f), bx = x + w - br - 4, by = y + br + 4;
      using (var bg = new SolidBrush(Color.FromArgb(240, 150, 22, 22))) gr.FillEllipse(bg, bx - br, by - br, 2 * br, 2 * br);
      using (var rim = new Pen(Color.FromArgb(255, 255, 214, 214), 1.8f)) gr.DrawEllipse(rim, bx - br, by - br, 2 * br, 2 * br);
      using (var cross = new Pen(Color.FromArgb(255, 255, 255, 255), Math.Max(2.4f, br * 0.26f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
        float k = br * 0.45f;
        gr.DrawLine(cross, bx - k, by - k, bx + k, by + k); gr.DrawLine(cross, bx - k, by + k, bx + k, by - k);
      }
    }

    // ---------------------------------------------------------------- auto-pilot

    static long NowMs() { return Environment.TickCount & 0x7FFFFFFF; }

    void AutoDetailFor(string st) {
      var d = new System.Collections.Generic.Dictionary<string, string>();
      var v = lastAutoView; var f = lastFilterView;
      string tr = pilot.Trace != "" ? pilot.Trace : lastPilotTrace;
      // (a hero pilot's stop: its own reason, not the item pilot's last line - that was the previous item's)
      bool heroSt = st == "hero_failed";
      string why = heroSt ? hpilot.Why : pilot.Why;
      if (heroSt) tr = "hero: " + hpilot.Why;
      d["why"] = why;
      d["item"] = target.ToString();
      d["place"] = "row " + curRow + "/" + curCol + " slot " + hintSlot + " guide " + hintKind;
      d["trace"] = tr;
      d["tries"] = "clicks " + pilot.Clicks + " drags " + pilot.Drags;
      d["sel"] = "sel " + curSel + " owner " + (v != null ? v.Owner : -1) + " hero " + hintHero + " screen hero " + (v != null ? v.ScreenHero : 0);
      d["filter"] = f == null ? "-" : "hideEq " + f.HideEquipped + " otherHero " + f.OnOtherHero + " panel " + f.PanelOpen + "/" + f.SidePanelOpen + " note " + fpilot.Note
        + " kind " + filterKind + " listed " + f.ItemListed + " state " + fpilot.State + " suits " + (f.Suits != null ? f.Suits.Length : -1) + " subs " + (f.SubAttrs != null ? f.SubAttrs.Length : -1);
      d["client"] = lastClientW + "x" + lastClientH + " row1top " + (v != null ? ((int)v.Row1Top).ToString() : "-") + " anchor " + anchor.Has + " src " + (anchorSrc == "" ? "-" : anchorSrc) + " panelok " + PanelOk
        + " toprow1 " + (int)ScrollAnchor.TopRow1(g, Ui.Unit(Math.Max(1, lastClientW), Math.Max(1, lastClientH))) + " rows " + (types != null ? types.Length - 1 : -1);
      // the hero pilot (the plan's hero on the hero screen, its grid filter pop-up): the slot's and the hero's stops need it
      d["hero"] = hpilot.Detail(lastHeroView);
      // the item pilot's slot step: the slot rect and what the screen read said
      d["slot"] = v == null ? "-" : (v.Slot != null ? "rect " + v.Slot[0] + "," + v.Slot[1] + "," + v.Slot[2] + "x" + v.Slot[3] : "none") + " open " + v.SlotOpen + " panelNone " + v.PanelNone + " panelOk " + v.PanelOk;
      AutoDetail = d;
    }

    void AutoSay(string st) { if (st != autoSaid) { autoSaid = st; AutoDetailFor(st); autoReport(st); Log.Write("auto: " + st + (target != 0 ? " item " + target + " row " + curRow + "/" + curCol + " anchor=" + anchor.Has : "")); } }

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
      // a started plan («Надеть» here or on the site): the player's click left our window in front, so
      // the game is brought forward (and restored) - only from our own window, never over another program
      if (RaiseGame(hwnd, runHero > 0 || sellWanted != null, now, (runHero > 0 && AutoEnabled) || SellClicking)) return;
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
        Target = target, Row = curRow, Col = curCol, Sel = curSel, SelRow = selRow, SelCol = selCol,
        SlotOpen = hintKind == "slot" && screens.Hero != null && screens.Hero.FormOk && screens.Hero.Tab == HeroGeometry.GearTab && screens.Hero.Part == hintSlot,
        PanelOk = PanelOk, PanelNone = PanelNone,
        AnchorHas = anchor.Has && !follow.Unsure && types != null && typesPtr != 0, Row1Top = anchor.Row1Top, Types = types,
        AutoConfirm = confirm, Button = confirm ? confirmBtn : null, Hero = hintHero,
        ScreenHero = confirm ? RFX.ReadHero(addrs) : 0, Owner = AutoConfirm && target > 0 ? RFX.ReadOwner(addrs, target) : -1
      };
      { var cs = screens.Current;   // the item's tip among the shown forms (a pilot's flow form of the hero screen: ScreenMap.FlowForms)
        v.TipKnown = RFX.UiKnown && !formsUnread && cs != null && !cs.Held && cs.Kind == ScreenKind.HeroScreen && cs.OfferRead && formsKind == ScreenKind.HeroScreen;
        v.TipShown = EquipTipShown();
        v.BlueNone = confirm && confirmBlueNone; v.ButtonStill = confirm && confirmBtn != null && confirmStill; }
      userClick = false; lastAutoView = v; lastClientW = cr.R; lastClientH = cr.B;
      // an offer pop-up over the city is closed before the plan's next step (the equip helper has no preflight of its own here)
      if ((runHero > 0 || target > 0) && v.Foreground && !v.UserBusy && OfferStep(ClickOwner.Equip, screens.Current, o, cr, v.Foreground, now)) return;
      // a screen nobody knows for 20 s (or one a form search did not find): stop instead of waiting for ever
      // a full-screen screen nobody knows: its back arrow top left (at most 3 presses a run, each has to change the screen)
      if ((runHero > 0 || target > 0) && v.Foreground && !v.UserBusy && UnknownBackStep(ClickOwner.Equip, screens.Current, o, cr, v.Foreground, now)) return;
      // a known screen the plan cannot start from (the Foundry, the inventory, the gem screen ...): back to the city with its arrow top left
      if (runHero > 0 && v.Foreground && !v.UserBusy && BackStep(ClickOwner.Equip, screens.Current, CityFirst.NeedsCity(ArtPhase ? PilotFlow.ArtEquip : PilotFlow.Equip, screens.Current), o, cr, v.Foreground, now)) { AutoSay("work"); return; }
      if ((runHero > 0 || target > 0) && v.Foreground && !v.UserBusy && gate.UnknownStall(ClickOwner.Equip, screens.Current, now)) { AnnounceHalts(); return; }
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
          Plan = runHero, Hero = heroScreen.FormOk ? heroScreen.Hero : 0, Tab = ArtPhase ? HeroGeometry.GearTab : heroScreen.Tab, Part = heroScreen.Part, SmallCards = heroScreen.SmallCards,
          Heroes = heroScreen.Heroes,
          HeroesButton = heroScreen.Hero == 0 && v.Foreground && HeroesButtonSeen(o, cr.R, cr.B),
          FilterOpen = v.Foreground && HeroFilterSeen(o, cr.R, cr.B), ClassChosen = heroScreen.ClassChosen, FactionList = heroScreen.FactionList,
          ChosenCamps = heroScreen.ChosenCamps, ClassCount = heroScreen.ClassCount, GridPtr = heroScreen.GridPtr
        };
        long[] tags = HeroTags(runHero);
        if (tags != null) { hv.HeroClass = tags[0]; hv.HeroCamps = new long[tags.Length - 1]; Array.Copy(tags, 1, hv.HeroCamps, 0, tags.Length - 1); }
        var ha = hpilot.Step(hv);
        lastHeroView = hv;
        LogHero(hv);
        if (ha != null) {
          pilot.Reset();
          AutoSay(hpilot.State == "work" || hpilot.State == "paused" || hpilot.State == "background" ? hpilot.State : "hero_" + hpilot.State);
          if (ha.Kind == AutoKind.None) return;
          int hx = o.X + (int)Math.Round(ha.X), hy = o.Y + (int)Math.Round(ha.Y), hy2 = hy + (int)Math.Round(ha.DY);
          if (hx < o.X || hy < o.Y || hx >= o.X + cr.R || hy >= o.Y + cr.B || hy2 < o.Y || hy2 >= o.Y + cr.B) return;
          if (!EquipSend(hv.FilterOpen ? ClickTarget.EquipFilter : ClickTarget.EquipNav, ha, hx, hy, o, v.Foreground)) hpilot.NotSent();   // not counted as a try
          return;
        }
      }
      // the plan's gear is on: the artifact steps (the gear pilots below stay idle)
      if (ArtPhase) { TickArtEquip(o, cr, v); return; }
      var fview = FilterViewNow(o, cr, now, v.Foreground, v.UserBusy);
      if (fview.PanelOpen) filterTouchAt = now;   // (the list rebuilds under the panel and after it closes: the item pilot waits for it)
      var fa = fpilot.Step(fview);
      LogFilter(fview); lastFilterView = fview;
      if (fview.PanelOpen && fview.SidePanelOpen) v.SideClose = new[] { fg.X(fg.SideCloseX, cr.R, cr.B), fg.Y(fg.CloseY, cr.R, cr.B) };
      if (fpilot.State == "failed") {
        if (!filterFailSaid) {
          filterFailSaid = true;
          AutoDetailFor("filter_failed");
          var fc = new System.Collections.Generic.Dictionary<string, string>(AutoDetail);
          fc["why"] = fpilot.Note;
          RaiseProblem("pilot.filter_failed", "filter failed: the game's gear filter could not be set for item " + fview.Item + " (kind " + filterKind + ")" + (fpilot.Note != "" ? " - " + fpilot.Note : ""), fc);
        }
      } else filterFailSaid = false;
      if (fa != null) {
        pilot.Reset();
        AutoSay(fpilot.State == "failed" ? "idle" : "work");
        if (fa.Kind == AutoKind.None) return;
        int fx = o.X + (int)Math.Round(fa.X), fy = o.Y + (int)Math.Round(fa.Y);
        if (fx < o.X || fy < o.Y || fx >= o.X + cr.R || fy >= o.Y + cr.B) return;
        if (EquipSend(ClickTarget.EquipFilter, fa, fx, fy, o, v.Foreground)) filterTouchAt = now;
        return;
      }
      // the next slot with the item's tip / compare still open over the slots (live 1.6.66.354: the pilot clicked the compare card where the
      // slot is behind it): the tip usually closes by itself after «Заменить»; if it stays 600 ms unchanged ONE Escape closes it (a second only
      // after 3 s), the slot is not clicked meanwhile
      if (v.Slot != null && v.TipKnown && v.TipShown && ClickGate.OnlyTip(screens.Current)) {
        var tcs = screens.Current; string tk = (tcs.Flow ?? "") + "|" + (tcs.ModalWhy ?? "");
        if (tk != tipSeenKey) { tipSeenKey = tk; tipSeenAt = now; }
        pilot.Reset();
        if (v.Foreground && !v.UserBusy && now - tipSeenAt >= TipStillMs && (tipEscapes == 0 || (tipEscapes < 2 && now - tipEscapedAt >= TipAgainMs))) {
          if (TipEscapeSend(cr, v.Foreground)) { tipEscapes++; tipEscapedAt = now; Log.Write("auto: the item's tip is open over the slots - closing it (Escape " + tipEscapes + ")"); }
        }
        return;
      }
      if (tipEscapes > 0 || tipSeenKey != null) { tipEscapes = 0; tipSeenKey = null; gate.ResetTipEscapes(ClickOwner.Equip); }
      // the next slot: an open filter pop-up covers the hero's slots - close it first (the filter was for the item that is
      // on now; the next one gets its own)
      if (v.Slot != null && v.Foreground && !v.UserBusy && now >= slotCloseAfter
          && Mark(o, fg.X(fg.MainCloseX, cr.R, cr.B), fg.Y(fg.CloseY, cr.R, cr.B), Ui.Unit(cr.R, cr.B), ButtonCheck.IsCloseRed)) {
        slotCloseAfter = now + ClosePopupAgainMs; pilot.Reset();   // (a slow laptop closes it 0.6-1.5 s later: not pressed twice meanwhile)
        int cx = o.X + (int)Math.Round(fg.X(fg.MainCloseX, cr.R, cr.B)), cy = o.Y + (int)Math.Round(fg.Y(fg.CloseY, cr.R, cr.B));
        EquipSend(ClickTarget.EquipClosePopup, new AutoAction { Kind = AutoKind.Click }, cx, cy, o, v.Foreground);
        return;
      }
      v.FilterAt = filterTouchAt; v.ListSig = typesPtr != 0 ? (long)typesPtr * 31 + (types != null ? types.Length : 0) : 0;
      LiveGearView(v, cr);
      var a = pilot.Step(v);
      AutoSay(pilot.State);
      if (pilot.Trace != "") { Log.Write("auto: " + pilot.Trace); lastPilotTrace = pilot.Trace; pilot.Trace = ""; }
      if (a.Kind == AutoKind.None) return;
      int x = o.X + (int)Math.Round(a.X), y = o.Y + (int)Math.Round(a.Y), y2 = y + (int)Math.Round(a.DY);
      if (x < o.X || y < o.Y || x >= o.X + cr.R || y >= o.Y + cr.B) return;   // never outside the game's client area
      if (y2 < o.Y || y2 >= o.Y + cr.B) return;
      // «Заменить» (the player's auto confirm) is its own target; everything else of the item pilot is the gear list
      bool onConfirm = confirmBtn != null && a.X >= confirmBtn[0] && a.X <= confirmBtn[0] + confirmBtn[2] && a.Y >= confirmBtn[1] && a.Y <= confirmBtn[1] + confirmBtn[3];
      if (!EquipSend(confirm && onConfirm ? ClickTarget.EquipConfirm : ClickTarget.EquipNav, a, x, y, o, v.Foreground)) pilot.NotSent();
      else if (cellLivePt != null) pilot.LivePoint(cellLivePt[0], cellLivePt[1]);   // (clicked on the cell's live rect: the pilot learns from there)
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
        if (pre.Reason == "offer" && OfferStep(ClickOwner.Sell, scr, o, cr, front, now)) { SellSay("open"); return; }
        if (pre.Reason == "unknown" && UnknownBackStep(ClickOwner.Sell, scr, o, cr, front, now)) { SellSay("open"); return; }
        if (pre.Verdict == GateVerdict.Wait) { if (pre.Reason == "unknown") SellSay("no_city"); return; }   // pending: the last click's effect is awaited
        // a pop-up over the inventory (a wait, a notice that came while marking): closed / waited for first, as on every other screen
        if (MapStep(ClickOwner.Sell, scr, o, cr, front, now)) { SellSay("open"); return; }
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
        if (double.IsNaN(srow1)) { sph = front && !busy ? (double.IsNaN(sph) ? SellPhase(o, cr, U) : sph) : double.NaN; if (gridSaid != "sellpilot:px") { gridSaid = "sellpilot:px"; Log.Write("sell: the inventory's scroll cannot be read (" + (inv.List == null ? RFX.ListWhy : "list read, no scroll value") + ") - the picture is the fallback (the gate will not let a cell be clicked without the exact scroll)"); } }
        else sph = double.NaN;
        if (!invSettle.Settled) return;   // the list still moves (or this tick's read failed): the cells are judged on a settled read only
        var a = spilot.Step(new SellView { NowMs = now, Foreground = front, UserBusy = busy, UserClicked = userClicked, W = W, H = H, U = U,
                                           Screen = inv.AsSell(sellCols), Wanted = sellWanted, Phase = sph, Row1 = srow1, NeedExact = true });
        if (spilot.State == "failed" && spilot.FailWhy == "scroll") { gate.Halt(ClickOwner.Sell, "scroll", "the inventory's exact scroll stayed unreadable - no cell was clicked on the picture's estimate"); AnnounceHalts(); }
        SellSay(spilot.State);
        if (spilot.Trace != "") { Log.Write("sell: " + spilot.Trace); spilot.Trace = ""; }
        if (a.Kind != AutoKind.None) {
          // a cell click must hit a cell of an item the run is after (or one it ticked, to take it off again): the item under the
          // click point from the fresh list order and the game's exact scroll
          int ccols = sellCols; var geo = spilot.Geometry;
          bool sent = SendInside(a, o, cr, ClickOwner.Sell, a.Kind == AutoKind.Drag ? ClickTarget.InvListDrag : ClickTarget.InvBulkCell, front, (rq, fr) => {
            rq.CellUid = ClickGate.InvCellUid(fr.Inv, geo, sellScroll.Row1(geo, fr.Inv != null ? fr.Inv.List : null, double.NaN, U, now), ccols, rq.X, rq.Y, W, U);
            var ok = new System.Collections.Generic.HashSet<long>(sellWanted);
            if (fr.Inv != null) foreach (var u in fr.Inv.Selected) ok.Add(u);
            rq.CellOk = ok;
            rq.NoScroll = fr.Inv == null || fr.Inv.List == null || !fr.Inv.List.Has;
          });
          if (sent) { if (cellLivePt != null) spilot.LivePoint(cellLivePt[0], cellLivePt[1]); spilot.Sent(); } else spilot.NotSent();   // a refused click / drag is taken back: no miss, no drag the list ignored
        }
        return;
      }
      if (!front || busy) { SellSay(front ? "paused" : "background"); return; }
      // the Foundry, the gem screen, the workshop: back with the arrow top left (towards the inventory / the city)
      if (BackStep(ClickOwner.Sell, scr, CityFirst.NeedsCity(PilotFlow.Sell, scr), o, cr, front, now)) { SellSay("open"); return; }
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
      bool art = sellArt;
      bool formActive; var live = art ? RFX.ReadArtifactLive(out formActive) : RFX.ReadInventoryLive(out formActive);
      if (live != null && live.Cols > 0 && live.Cols != shCols) shCols = live.Cols;
      else if (art && (live == null || live.Cols <= 0)) { int ac = SellGeometry.ArtCols(Geo.Rect("inventory.artList", W, H, U), U, shCols); if (ac != shCols) shCols = ac; }   // not read: the game's own rule on the list's width
      if (shGeo == null || shGeoW != W || shGeoH != H || shGeoCols != shCols) {
        shGeoW = W; shGeoH = H; shGeoCols = shCols; shGeo = art ? SellGeometry.Artifacts(W, H, U, shCols, Geo.Rect("inventory.artList", W, H, U)) : SellGeometry.Inventory(W, U, shCols);
        sgrid.Reset();
      }
      // the inventory, read a few times a second (the form the game never built is searched for at most every 8 s). A new
      // reading is taken over when the next one says the same (a selection that flickers in one read does not show)
      if (!shHave || Math.Abs(now - shReadAt) >= 200) {
        shReadAt = now;
        var raw = screens.Inv;   // the screen of this tick (ReadScreens)
        bool bulk = raw != null && raw.Open && (art ? raw.ArtTab && raw.ArtBulk : raw.EquipTab && raw.Bulk);
        if (bulk) {
          // the columns: the game's own grid says (the picture only when it cannot be read; never for the artifact list: its columns come from the grid)
          int c = raw.List != null && raw.List.Cols > 0 ? raw.List.Cols : front && !art ? InvColumns(o, cr, U) : 0;
          if (c > 0 && c != shCols) { shCols = c; shGeoCols = -1; }
        }
        if (bulk && sgrid.ListPtr != raw.ListPtr) sgrid.Reset();
        string sig = SellHint.Classify(raw, sellWanted, art).StateKey;
        if (!shHave || sig == shReadSig) { shInv = raw; shHave = true; }
        shReadSig = sig;
      }
      // a screen the game no longer shows is not read from the old reading: the step falls back to «open the inventory»
      var inv = formActive && screens.Current != null && screens.Current.Kind == ScreenKind.Inventory ? shInv : null;
      bool inBulk = inv != null && inv.Open && (art ? inv.ArtTab && inv.ArtBulk : inv.EquipTab && inv.Bulk);
      if (inBulk && sgrid.ListPtr != inv.ListPtr) sgrid.Reset();
      // row 1's place: the game's exact scroll, nothing else. Unreadable = no item frames at all (the text plate and the counts
      // stay), never guessed ones
      double row1 = double.NaN;
      if (inBulk && front && shGeo != null && live != null && live.Has) {
        inv.List = live;
        if (art ? FollowArtifactGrid(sgrid, shGeo, inv, U, now) : FollowInventoryGrid(sgrid, sellScroll, shGeo, inv, o, cr, U, now, "sell")) row1 = sgrid.Row1;
      }
      // the artifact list's own scroll cannot be read: its rows from the picture (the cards' borders and the list's movement), never no frames at all
      bool picRows = false;
      if (art && inBulk && front && shGeo != null && double.IsNaN(row1)) { row1 = ArtPicRow1(shGeo, inv, o, cr, U, now); picRows = !double.IsNaN(row1); }
      else if (art && !inBulk) artPic.Reset();
      // the drawn row 1 eases toward the scroll's value (no jumping between old and new reads)
      row1 = shSmooth.Step(row1, now, 4 * shGeo.PitchY * U);
      if (recB != null) recB.InvSmooth = row1;
      if (inBulk && front) {
        string why = !double.IsNaN(row1) ? (picRows ? "picture" : "exact") : "none";
        if (why != shFrameSaid) { shFrameSaid = why; if (why == "none") Log.Write((art ? "art sell hint" : "sell hint") + ": the list's exact scroll cannot be read - no item frames, only the text plate"); else if (why == "picture") Log.Write("art sell hint: the game's own scroll is not available - the list's rows are followed from the picture"); }
      }
      var step = SellHint.BuildExact(inv, sellWanted, shCols, row1, shGeo, W, H, U, art);
      if (art) { string ak = step.StateKey; if (ak != artStepSaid) { artStepSaid = ak; Log.Write("art sell hint: step " + step.Key + " selected " + step.Selected + "/" + step.Total + " missing " + step.Missing + " extra " + step.Extra + " cols " + shCols + (double.IsNaN(row1) ? " row1 none" : " row1 " + row1.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))); } }
      // the screen is Unknown (a form is being searched for): the last step and its plate stay for up to 15 s, without frames
      var scrS = screens.Current;
      if (scrS != null && scrS.Kind == ScreenKind.Unknown) { if (shUnkSince < 0) shUnkSince = now; } else shUnkSince = -1;
      shHold = shLast != null && scrS != null && scrS.Kind == ScreenKind.Unknown && shUnkSince >= 0 && now - shUnkSince < LocateGuide.HoldUnknownMs;
      if (shHold) step = shLast; else shLast = step;
      if (recB != null) { recB.SellInv = inv; recB.Step = "Sell:" + step.Kind; }
      shFrames = shHold ? new List<SellFrame>() : step.Frames;
      string key = step.FullKey;
      if (key != shKey) { shKey = key; var h = SellHintChanged; if (h != null) h(step); }
      sellFinal = !shHold && SellHint.IsFinal(step);

      if (!GameDrawable(true)) { SellHintHide(); return; }
      bool haveText = sellHintLines.Length > 0;
      double vt = shGeo.ViewTopPx(U), vb = shGeo.ViewBottomPx(H, U);
      int gx = (int)shGeo.X(shGeo.X0, W, U), gw = (int)(shGeo.PitchX * (shCols - 1) * U + shGeo.CellW * U);
      // a moment of «unknown» screen (a read that came late): the frames stay for a short time with the plate, they never blink out while the player holds still
      if (shHold && SellHint.KeepFramesInHold(now - shUnkSince) && shLastMarks != null) {
        Scene(glow, ref drawnKey, null, shLastPill, shLastPt, shLastStrip, shLastMarks);
        if (haveText) DrawTipTop(sellHintLines, null, null, false); else HideTip();
        return;
      }
      if (!shHold && step.Kind == SellHintKind.Select) {
        var marks = new List<int[]>();
        var wantU = new List<long>(); foreach (var fr in step.Frames) if (fr.Uid > 0) wantU.Add(fr.Uid);
        foreach (var fr in step.Frames) {   // each cell where the game has put it (live, fresh): its picture framed; out of the viewport or covered: none
          double[] lcf; var see = LiveCell(UiCellKind.Inventory, fr.Uid, wantU, W, H, U, new double[] { fr.X, fr.Y, fr.W, fr.H }, "sellhint:" + cellRunN, out lcf);
          if (see == UiCellSee.Hidden) continue;
          int sty = fr.Style == "todo" ? 0 : fr.Style == "done" ? 1 : 2;
          if (see == UiCellSee.Shown) marks.Add(new[] { o.X + (int)Math.Round(lcf[0]), o.Y + (int)Math.Round(lcf[1]), (int)Math.Round(lcf[2]), (int)Math.Round(lcf[3]), sty });
          else marks.Add(new[] { o.X + fr.X, o.Y + fr.Y, fr.W, fr.H, sty });
        }
        marks.Sort((p, q) => p[4].CompareTo(q[4]));   // a stray (red) item is painted last, on top
        // list items out of view: a pill with an arrow at the list's edge (as the equip helper's «Вещь здесь ↓»)
        Plate pill = null; int[] pt = null;
        if (step.Below > 0 && sellPillDown != "") { pill = new Plate { Pill = true, Main = sellPillDown }; pt = new[] { o.X + gx, o.Y + (int)vb - 2, (int)(shGeo.CellW * U), 2 }; }
        else if (step.Above > 0 && sellPillUp != "") { pill = new Plate { Pill = true, Main = sellPillUp }; pt = new[] { o.X + gx, o.Y + (int)vt, (int)(shGeo.CellW * U), 2 }; }
        // the list's scroll bar: the viewport and a dot for EACH target still to mark (the inventory's list; the artifact list has no bar geometry)
        double[] strip = null;
        if (!art && !double.IsNaN(row1) && step.TodoRows.Count > 0) {
          var sv = LocateFrames.ListStrip(Geo.Current, row1, vt, shGeo.TopPad * U, shGeo.PitchY * U, (inv.Uids.Count + shCols - 1) / shCols, step.TodoRows,
            SellHint.LeadRow(step.TodoRows, row1, shGeo, H, U, step.Below > 0, step.Above > 0), W, H, U);
          if (sv != null) { strip = new double[sv.Length]; for (int i = 0; i < sv.Length; i++) strip[i] = i == 0 ? o.X + sv[i] : i == 6 ? sv[i] : o.Y + sv[i]; }
          if (strip != null && pill != null) pt = null;
        }
        shLastMarks = marks; shLastStrip = strip; shLastPill = pill; shLastPt = pt;
        Scene(glow, ref drawnKey, null, pill, pt, strip, marks);
        // the step's plate above the list
        // (the plate in the free top centre of the window: above the list lie the inventory's tabs, the cells' own frames never meet it)
        if (haveText) DrawTipTop(sellHintLines, null, null, SellHint.Warn(step)); else HideTip();
        return;
      }
      if (glow.Visible) glow.Hide();
      drawnKey = null; shLastMarks = null;
      if (shHold && !haveText) { HideTip(); return; }
      if (shHold) { DrawTipTop(sellHintLines); return; }   // held: the plate only
      if (SellHint.IsFinal(step)) { DrawSellFinal(o, W, H, U, art, step.Selected, sellHintLines); return; }   // everything is marked: the plate and the frame on «Продать»
      if (!haveText) { HideTip(); return; }
      if (step.Kind == SellHintKind.PressBulk) {
        // «Массовая продажа» under the list (bottom left): where the auto mode clicks
        bool pl; var pb = FrameForI(art ? "inventory.artBulkSell" : "inventory.bulkSell", art ? SellHint.ArtBulkRect(W, H, U) : SellHint.PressBulkRect(W, H, U), W, H, U, out pl);
        frame++;
        DrawTip(o.X + pb[0], o.Y + pb[1], pb[2], pb[3], sellHintLines, H);
        return;
      }
      if (step.Kind == SellHintKind.OpenArtTab) {
        // the left menu's third toggle «Артефакт»
        bool tl; var tb = FrameForI("inventory.leftTab.artifact", SellHint.ArtTabRect(W, H, U), W, H, U, out tl);
        frame++;
        DrawTip(o.X + tb[0], o.Y + tb[1], tb[2], tb[3], sellHintLines, H);
        return;
      }
      if (step.Kind == SellHintKind.NeedPosition) { DrawTipTop(sellHintLines); return; }
      int[] cityBtn;
      if (step.Kind == SellHintKind.OpenInventory && CityInventoryButton(o, W, H, U, out cityBtn)) {   // the city: frame the bar's «Инвентарь»
        frame++;
        DrawTip(cityBtn[0], cityBtn[1], cityBtn[2], cityBtn[3], sellHintLines, H, false, false, false, null, false, GuideStyle ? null : HlCity(cityBtn), BarRow(null, cityBtn, o, W, H));
        return;
      }
      DrawTipTop(sellHintLines);   // no target on screen: the plate at the top centre
    }

    /// <summary>The final step of the storage cleanup (the player marked everything, or the auto run did): «Готово: отмечено N — нажмите «Продать»» and the gold branded
    /// frame on the live «Продать» button. A frame only: the app never presses «Продать» (nothing here clicks).</summary>
    void DrawSellFinal(W32.POINT o, double W, double H, double U, bool art, int selected, string[] fallback) {
      string line = SellHint.DoneLine(SellDoneText, selected);
      string[] lines = line != "" ? new[] { line } : fallback;
      bool pl; var pb = FrameForI(art ? "inventory.artSellBtn" : "inventory.sellBtn", SellHint.SellBtnRect(W, H, U), W, H, U, out pl);
      frame++;
      if (lines == null || lines.Length == 0) { HideTip(); return; }
      DrawTip(o.X + pb[0], o.Y + pb[1], pb[2], pb[3], lines, H);
    }

    // The auto run ended (the pilot's state is «done»): the same final step as the hint's, from the screen read of this tick; nothing is clicked.
    void TickSellFinalAuto() {
      IntPtr hwnd = GameWindow(); W32.RECT cr = new W32.RECT();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { SellHintHide(); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      SceneArea(o, cr);
      var scr = screens.Current; var inv = scr != null && scr.Kind == ScreenKind.Inventory ? screens.Inv : null;
      var step = SellHint.Classify(inv, sellWanted, false);
      sellFinal = SellHint.IsFinal(step);
      if (!sellFinal || !GameDrawable(true)) { if (glow.Visible) glow.Hide(); drawnKey = null; HideTip(); return; }
      if (glow.Visible) glow.Hide();
      drawnKey = null;
      DrawSellFinal(o, W, H, U, false, step.Selected, null);
    }

    // ---------------------------------------------------------------- «Найти в игре»

    LocateRequest locateReq; string[] locateLines = new string[0]; string locateCaps = "";
    readonly LocateGrid lgrid = new LocateGrid();
    LocateStep locateStep; SubCheck locateSubs; string locateKey = "", locateCoverSaid = "";
    string locateScreensSaid = "", locateMarkerSaid = "", locateCardSaid = "", locateListSig = "", locateDecisionSaid = "", locatePlaceSaid = ""; long locateListChangeAt;   // the Ascension list's shape (to wait after it changed), the log keys
    long locateReadAt, locateLastCur = -1, locateSubsAt; int locateCols = 8, locateGeoCols; double locateGeoW, locateGeoH;
    SellGeometry locateGeo; InventoryScreen locateInv; bool locateNeedClick;
    /// <summary>The step changed (or its list place / sub stat check): the page shows it and answers with the text plate.</summary>
    public event Action<LocateStep, SubCheck> LocateChanged;

    void LocateHide() { if (glow.Visible) glow.Hide(); drawnKey = null; HideTip(); locHint.Reset(); }
    readonly HintHold locHint = new HintHold();

    // the buttons of the step's screen the plate must leave free (screen px): the geometry's rects, and a button's live rect
    // (uibtn, read before a pilot's click) when one is fresh for this window size
    int[][] LocateAvoid(LocateStep step, W32.POINT o, double W, double H, double U) {
      var g = Geo.Current; var res = new List<int[]>();
      foreach (var k in LocateFrames.AvoidKeys(step)) {
        var r = g != null ? g.Rect(k, W, H, U) : null;
        if (r != null && r.W > 0 && r.H > 0) res.Add(new[] { o.X + (int)r.X, o.Y + (int)r.Y, (int)Math.Ceiling(r.W), (int)Math.Ceiling(r.H) });
        var t = k == "inventory.workshop" ? ClickTarget.FoundryButton : k == "foundry.tab.ascend" ? ClickTarget.AscendTab : (ClickTarget?)null;
        var lv = t != null ? RFX.UiButtonLive(t.Value, W, H, Environment.TickCount) : null;
        if (lv != null) res.Add(new[] { o.X + (int)lv[0], o.Y + (int)lv[1], (int)Math.Ceiling(lv[2]), (int)Math.Ceiling(lv[3]) });
      }
      foreach (var r in LocateFrames.AvoidAreas(step, g, W, H, U)) res.Add(new[] { o.X + (int)r.X, o.Y + (int)r.Y, (int)Math.Ceiling(r.W), (int)Math.Ceiling(r.H) });   // the hero's tab column and button row
      return res.Count > 0 ? res.ToArray() : null;
    }

    /// <summary>The frame of a ClosePopup step: the pop-up's × (the gear filter's by its live rect through FrameFor, any other form's by the screen map's
    /// exit rect); null = the pop-up has no known ×: the plate alone.</summary>
    string locateCitySaid = "";
    /// <summary>The frame of a BackToCity step: the back arrow of the screen on top where the game has put it (the Foundry's, the hero screen's, the inventory's by
    /// their live rects through FrameFor), else the standard back spot (the same one the pilot's CityBack press uses).</summary>
    LocateFrame BackFrame(ScreenState sc, double W, double H, double U) {
      string key = sc == null ? "" : sc.Kind == ScreenKind.Foundry ? "foundry.back" : sc.Kind == ScreenKind.HeroScreen ? "hero.back" : sc.Kind == ScreenKind.Inventory ? "inventory.back" : "";
      var g = new HeroGeometry(); double t = ClickGate.UnknownBackTol * U;
      var r = new GeoRect { X = g.BackX * U - t, Y = g.BackY * U - t, W = 2 * t, H = 2 * t };
      return new LocateFrame { Target = key != "" ? key : "popup.exit", Mark = new LocateMark { Kind = "box", X = (int)Math.Round(r.X), Y = (int)Math.Round(r.Y), W = (int)Math.Round(r.W), H = (int)Math.Round(r.H) }, Raw = r };
    }

    LocateFrame PopupCloseFrame(LocateStep step, double W, double H, double U) {
      GeoRect r = null; string key = "popup.exit";
      if (step.PopupClose == "hero.filterClose") {
        double fx = fg.X(fg.MainCloseX, W, H), fy = fg.Y(fg.CloseY, W, H); int rr = Math.Max(8, (int)(0.028 * U));
        r = new GeoRect { X = fx - rr, Y = fy - rr, W = 2 * rr, H = 2 * rr }; key = step.PopupClose;
      } else r = ScreenMap.ExitRect(step.Popup, W, H, U);
      if (r == null || r.W < 2 || r.H < 2) return null;
      return new LocateFrame { Target = key, Mark = new LocateMark { Kind = "box", X = (int)Math.Round(r.X), Y = (int)Math.Round(r.Y), W = (int)Math.Round(r.W), H = (int)Math.Round(r.H) }, Raw = r };
    }

    // the job is done in the game (src/LocateGuide.cs LocateFinish): a short plate, the request is reported (LocateFinished), then the hints end
    readonly LocateFinish lfin = new LocateFinish(); long[][] locateLive; long locateDoneUntil, locateDoneCap; bool locateDoneAuto; string locateDoneWhat = "", locateSuccessSaid = "";
    /// <summary>The job of the request is done in the game: request id and what ("ascend" | "recast").</summary>
    public event Action<string, string> LocateFinished;

    void FinishLocateJob(string what, long now) {
      var req = locateReq; if (req == null) return;
      Log.Write("locate: job done (" + what + ") for " + (req.Item != null ? "item " + req.Item.Uid : "artifact " + (req.Art != null ? req.Art.Uid : 0)));
      locateDoneUntil = now + 4000; locateDoneCap = now + 15000; locateDoneAuto = LocateAutoOn; locateDoneWhat = what; locateDriving = false; lpilot.Cancel();
      var h = LocateFinished; if (h != null) h(req.Id, what);
    }

    void TickLocateDone(string what) {
      long now = NowMs();
      // the game's result form of the job still on top (ScreenMap.IsSuccessForm): with auto click on, a tap-to-close one (class P) is closed by
      // its tap strip through the gate (MapClose, never an Escape); the choice one (Form_EquipRecastSuccess, class W) is the player's. The plate
      // stays while it is shown (at most locateDoneCap)
      var scr = screens.Current;
      bool succ = scr != null && ScreenMap.IsSuccessForm(scr.Top);
      if (succ && now < locateDoneCap && locateDoneUntil < now + 500) locateDoneUntil = now + 500;
      if (now >= locateDoneUntil) { SetLocate(null); return; }
      IntPtr hwnd = GameWindow(); W32.RECT cr;
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || !FrontSteady(hwnd) || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { LocateHide(); return; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o); SceneArea(o, cr);
      if (succ && locateDoneAuto && scr.TopClass == 'P' && !sending) {
        string said = scr.Top + "|" + scr.SinceMs;
        if (said != locateSuccessSaid) { locateSuccessSaid = said; Log.Write("locate: result form " + scr.Top + " on " + scr.Brief + " - closed by its tap"); }
        MapStep(ClickOwner.Locate, scr, o, cr, IsForeground(hwnd), now);
      }
      if (glow.Visible) glow.Hide(); drawnKey = null;
      string txt = Tx(what == "artPromote" ? "doneArtPromote" : what == "ascend" ? "doneAscend" : "doneRecast");
      DrawTipTop(new[] { txt != "" ? txt : (what == "artPromote" ? "Готово: артефакт продвинут" : what == "ascend" ? "Готово: вещь вознесена" : "Готово: вещь преобразована") }, locateCaps);
    }

    void TickLocate() {
      if (locateDoneUntil != 0) { TickLocateDone(locateDoneWhat); return; }
      var req = locateReq;
      if (req.Purpose == "artPromote") { TickArtPromote(); return; }
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
        string fin = lfin.Update(req.Purpose == "ascend", uid, st.Kind, rs, locateLive, now, ScreenMap.SuccessShown(screens.Current, req.Purpose));
        if (fin != "") { FinishLocateJob(fin, now); return; }
        // the step's target lies under a shown pop-up (the gear filter over the hero screen, a flow form): «close the pop-up» is the step shown until it
        // is closed, then the step comes back; no frame on the hidden target
        if (!LocateAutoOn && LocateGuide.CoverStep(st, screens.Current)) {
          string cov = st.Covered + "|" + st.Popup;
          if (cov != locateCoverSaid) { locateCoverSaid = cov; Log.Write("locate: step " + st.Covered + " is covered by " + st.Popup + " (z-order " + (screens.Current.Order != null ? string.Join(",", screens.Current.Order.ToArray()) : "unknown") + ") - close it first"); }
        } else locateCoverSaid = "";
        // hint mode, city first (the auto pilot's BackStep does the same): a screen the purpose's flow does not go on from - the first hint is its back arrow
        if (!LocateAutoOn && LocateGuide.CityFirstStep(st, req.Purpose, screens.Current)) {
          string cf = st.Covered + "|" + screens.Current.Kind;
          if (cf != locateCitySaid) { locateCitySaid = cf; Log.Write("locate: step " + st.Covered + " is not for the screen " + screens.Current.Brief + " (city first) - back first"); }
        } else locateCitySaid = "";
        st.NeedClick = locateNeedClick;
        locateStep = st;
      }
      var step = locateStep;
      if (recB != null) recB.Step = step.Kind.ToString();
      TickLocateDrive(step, req, hwnd, o, cr, now, W, H, U);   // auto click on: the app opens the item itself
      { string rw = gate.RetryShown(ClickOwner.Locate, now); if (rw != "" && step.Auto != "stopped" && step.Auto != "") { step.Auto = "retry"; step.AutoWhy = rw; } }   // a step retried: the window says why
      FoundryScrollNow(step, now);
      // the step's frame from the screen geometry (the site's file, src/LocateFrames.cs): «Литейная», the tabs, the slot, the list cell, the final button
      LocateFrame lf = front && !hold && step.Kind == LocateKind.BackToCity ? BackFrame(screens.Current, W, H, U)
        : front && !hold && step.Kind == LocateKind.ClosePopup ? PopupCloseFrame(step, W, H, U)
        : front && !hold && step.Kind != LocateKind.InList ? LocateFrames.For(step, req.Item, locateRs, null, Geo.Current, W, H, U) : null;
      // every button / tab of the step framed where the game has put it (FrameFor: live rect, its sprite's outline; the layout's when unread)
      bool lfLive = LiveInto(lf, W, H, U);
      if (lf != null && lf.Target == "cell" && step.Kind == LocateKind.GemFunction) FrameLayoutSay("gem.cell", FrameTargets.Of("gem.cell").Layout, null);
      if (lf != null && lf.Target == "cell" && step.Kind == LocateKind.AscendFilter) FrameLayoutSay("filter.suit.cell", FrameTargets.Of("filter.suit.cell").Layout, null);
      if (lf != null && lf.Target == "cell" && (step.Kind == LocateKind.AscendPick || step.Kind == LocateKind.RecastPick)) {   // the list cell: where the game has put it (live, fresh)
        double[] lcf; var m0 = lf.Mark;
        var see = LiveCell(step.Kind == LocateKind.AscendPick ? UiCellKind.Ascend : UiCellKind.Recast, uid, null, W, H, U, new double[] { m0.X, m0.Y, m0.W, m0.H }, "locate:" + req.Id, out lcf);
        if (see == UiCellSee.Shown) {
          lf.Raw = new GeoRect { X = lcf[0], Y = lcf[1], W = lcf[2], H = lcf[3] };
          lf.Mark = new LocateMark { Kind = "box", X = (int)Math.Round(lcf[0]), Y = (int)Math.Round(lcf[1]), W = (int)Math.Round(lcf[2]), H = (int)Math.Round(lcf[3]) };
          lf.ClickX = lcf[0] + lcf[2] / 2; lf.ClickY = lcf[1] + lcf[3] / 2;
        } else if (see == UiCellSee.Hidden && m0.Kind == "box") lf = null;   // out of the viewport or covered: no frame on the layout's guess
      }
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
          // where the game has put the item's cell (live, fresh): the frame hugs its picture; no cell of it in view or the list covered: no box
          double[] lcf; var see = LiveCell(UiCellKind.Inventory, uid, null, W, H, U, new double[] { mk.X, mk.Y, mk.W, mk.H }, "locate:" + req.Id, out lcf);
          if (see == UiCellSee.Shown) mk = new LocateMark { Kind = "box", X = (int)Math.Round(lcf[0]), Y = (int)Math.Round(lcf[1]), W = (int)Math.Round(lcf[2]), H = (int)Math.Round(lcf[3]) };
          frame++;
          // the item is off the screen: its place on the list's scroll bar too (the pill beside it says which way and how far)
          double[] strip = null;
          if (mk.Kind != "box") {
            string msig = mkr == null ? "none" : string.Join(",", Array.ConvertAll(mkr, d => ((int)d).ToString()));
            if (msig != locateMarkerSaid) { locateMarkerSaid = msig; Log.Write("locate marker: " + msig + " row=" + step.Row + " of " + ((inv.Uids.Count + locateCols - 1) / locateCols) + " row1=" + (int)lgrid.Row1 + " bar=" + (Geo.Current != null && Geo.Current.Rect("inventory.scrollbar", W, H, U) != null ? "geometry" : "no geometry")); }
            if (mkr != null) strip = new[] { o.X + mkr[0], o.Y + mkr[1], o.Y + mkr[2], o.Y + mkr[3], o.Y + mkr[4], o.Y + mkr[5], 1 };
          }
          if (see == UiCellSee.Hidden && mk.Kind == "box") { if (glow.Visible) glow.Hide(); drawnKey = null; }
          else Draw(mk.Kind, o.X + mk.X, o.Y + mk.Y, mk.W, mk.H, mk.Rows, strip);
        } else { if (glow.Visible) glow.Hide(); drawnKey = null; }
      } else if (!front) { if (glow.Visible) glow.Hide(); drawnKey = null; }
      else if (lf != null && lf.Mark.Kind != "box") { frame++; Draw(lf.Mark.Kind, o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, lf.Mark.Rows, FoundryStrip(step, o, W, H, U)); }   // the list cell is off the screen: the pill at the list's edge (by the Ascension / recast list's scroll strip)
      else { if (glow.Visible) glow.Hide(); drawnKey = null; }
      if (locateInv != null && step.Kind != LocateKind.InList) locateLastCur = locateInv.Current;
      step.NeedClick = locateNeedClick;
      // a plate with the step's text (the page composes it): beside the step's target, at the top when there is none. What this tick
      // wants goes through locHint (HintHold): a step is shown / hidden / moved only after it held 350 ms, and a tick without a target
      // (a held or failing read, the game out of front for a moment) keeps the plate where it was
      int[] cityBtn; string wantKey = null; int[] wantT = null; Hl wantHl = null;
      if (front && !hold && locateLines.Length > 0 && step.Kind == LocateKind.OpenInventory && !step.OnHero && CityInventoryButton(o, W, H, U, out cityBtn)) {
        wantKey = step.Key; wantT = cityBtn;   // the city: frame the bar's «Инвентарь» (where the auto mode clicks) with the step's plate beside it
        wantHl = GuideStyle ? null : HlCity(cityBtn);
      } else if (front && locateLines.Length > 0 && lf != null && lf.Mark.Kind == "box") {
        wantKey = step.Key; wantT = new[] { o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H };   // the button / tab / cell: its frame with the plate beside it
        wantHl = (lf.Target == "cell" || lfLive) && !GuideStyle ? null : HlLocate(lf, step, o);   // a list cell / a live frame: its sprite's outline (DrawTip's HlLive); the layout's: the old frame's outline
      } else if (locateLines.Length > 0 && (hold || step.Kind != LocateKind.InList || locateNeedClick)) wantKey = step.Key;   // no target: the plate at the top
      if (locateLines.Length == 0) { locHint.Update(null, null, null, now); HideTip(); }
      else if (!locHint.Update(wantKey, wantT, wantHl, now)) HideTip();
      else {
        frame++;
        var avoid = LocateAvoid(step, o, W, H, U);
        var t = locHint.Target;
        if (t != null && step.Kind == LocateKind.OpenInventory && !step.OnHero && t[1] - o.Y > 0.6 * H) avoid = BarRow(avoid, t, o, W, H);   // the city bar's «Инвентарь»
        if (t == null) DrawTipTop(locateLines, locateCaps, avoid);   // its own header, not the plan's «Шаг N из M»
        else DrawTip(t[0], t[1], t[2], t[3], locateLines, H, locHint.Stale, false, false, locateCaps, false, locHint.Stale ? null : locHint.Tag as Hl, avoid);
      }
      string key = step.Key + "|" + step.Pos + "|" + step.Row + "|" + step.Col + "|" + step.Count + "|" + string.Join(",", step.Missing) + "|" + step.OnHero + "|" + step.NeedClick
        + "|" + (locateSubs != null ? string.Join(",", Array.ConvertAll(locateSubs.Match, x => x ? "1" : "0")) + locateSubs.Changed : "") + "|" + step.Auto + "|" + step.AutoWhy + "|" + step.Popup;
      if (key != locateKey) { locateKey = key; var h = LocateChanged; if (h != null) h(step, locateSubs); }
    }

    // ---- «Показать в игре» for an ARTIFACT promotion (purpose artPromote): hints only, nothing is ever clicked (the gate's NoLocateClicks).
    // Inventory -> «Артефакт» -> the artifact's cell -> «Мастерская» -> «Продвинуть» tab -> the copy -> the button (the player's); src/LocateGuide.cs NextArt, src/LocateFrames.cs ArtFrames.
    readonly ArtFinish artFin = new ArtFinish();
    LocateStep artStep; string artKey = "", artSaid = "", artWsDiagSaid = ""; long artFormAskedAt, artWsWaitSince;

    void TickArtPromote() {
      var req = locateReq; var art = req.Art;
      IntPtr hwnd = GameWindow();
      W32.RECT cr = new W32.RECT();
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { LocateHide(); return; }
      bool front = IsForeground(hwnd);
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      SceneArea(o, cr);
      long now = NowMs();
      var scr = screens.Current;
      if (scr != null && scr.Kind == ScreenKind.Unknown) { if (locUnkSince < 0) locUnkSince = now; } else locUnkSince = -1;
      bool hold = LocateGuide.HoldOnUnknown(artStep, scr != null ? scr.Kind : ScreenKind.City, locUnkSince, now);
      InventoryScreen inv = null; ArtWorkshopScreen ws = null;
      if (!hold) {
        inv = scr != null && scr.Kind == ScreenKind.Inventory ? screens.Inv : null;
        ws = scr != null && scr.Kind == ScreenKind.ArtWorkshop ? screens.ArtWs : null;
        // the lists' exact scroll, read every tick we draw (the frames follow a scroll the game renders at its own rate)
        if (inv != null && inv.ArtTab) {
          bool fa; var live = RFX.ReadArtifactLive(out fa); if (live != null && live.Has) inv.List = live;
          // no exact scroll: the rows from the picture (the same list ListRead the frame is placed from)
          if ((inv.List == null || !inv.List.Has) && front && inv.Uids.Count > 0) {
            var ag = ArtGeo(W, H, U);
            double pr1 = ArtPicRow1(ag, inv, o, cr, U, now);
            if (!double.IsNaN(pr1)) inv.List = ArtGridPicture.ToListRead(ag, U, pr1);
          }
        } else artPic.Reset();
        if (ws != null && ws.Tab == 2) { bool wa; var ml = RFX.ReadArtMatLive(out wa); if (ml != null && ml.Has) ws.MatList = ml; }
        var st = LocateGuide.NextArt(req, inv, ws);
        // the workshop's data cannot be read: after a moment the screen's own geometry gives the hint (never a click), so the player always sees a frame
        if (LocateGuide.ArtWsWaiting(st, ws)) {
          if (artWsWaitSince == 0 || now < artWsWaitSince) artWsWaitSince = now;
          if (now - artWsWaitSince >= LocateGuide.ArtWsWaitMs) st = LocateGuide.ArtWsGuess(st, ws);
        } else artWsWaitSince = 0;
        if (ws != null && ws.Diag != null && ws.Diag != "" && ws.Diag != artWsDiagSaid) { artWsDiagSaid = ws.Diag; Log.Write("art workshop: " + ws.Diag); }
        if (artFin.Update(art.Uid, ws, now, ScreenMap.SuccessShown(scr, "artPromote"))) { FinishLocateJob("artPromote", now); return; }
        artStep = st;
        string sig = st.Key + "|" + st.Pos + "|" + st.MatChosen + "|" + st.MatWrong + "|" + st.MatKind;
        if (sig != artSaid) { artSaid = sig; Log.Write("art promote: step " + st.Key + " pos " + st.Pos + " of " + st.Count + " scroll " + st.Scroll + " chosen " + st.MatChosen + "/" + st.MatNeed + (st.MatWrong ? " WRONG" : "") + " level " + st.ArtLevel + "/" + st.ArtCap + " screen " + (scr != null ? scr.Brief : "?")); }
      }
      var step = artStep;
      if (step == null) { LocateHide(); return; }
      if (recB != null) recB.Step = step.Kind.ToString();
      TickArtDrive(step, inv, ws, hold, hwnd, o, cr, now, W, H, U);
      LocateFrame lf = front && !hold ? ArtFrames.For(step, inv, ws, Geo.Current, W, H, U) : null;
      bool afLive = LiveInto(lf, W, H, U);   // «Мастерская», the tabs, «Продвинуть»: where the game has put them (FrameFor)
      if (lf != null && (lf.Target == "cell" || lf.Target == "artifact.matList")) { string ck = step.Kind == LocateKind.ArtInList ? "inventory.artCell" : lf.Target == "artifact.matList" ? "artifact.matList" : "artifact.matCell"; FrameLayoutSay(ck, FrameTargets.Of(ck).Layout, null); }
      if (!front) { LocateHide(); }
      else if (lf != null && lf.Mark.Kind != "box") { frame++; Draw(lf.Mark.Kind, o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, lf.Mark.Rows); }   // the cell is off the screen: the pill at the list's edge
      else { if (glow.Visible) glow.Hide(); drawnKey = null; }
      int[] cityBtn;
      if (front && !hold && locateLines.Length > 0 && step.Kind == LocateKind.OpenInventory && CityInventoryButton(o, W, H, U, out cityBtn)) {
        frame++;   // the city: frame the bar's «Инвентарь» with the step's plate beside it
        DrawTip(cityBtn[0], cityBtn[1], cityBtn[2], cityBtn[3], locateLines, H, false, false, false, locateCaps, false, GuideStyle ? null : HlCity(cityBtn), BarRow(null, cityBtn, o, W, H));
      } else if (front && locateLines.Length > 0 && lf != null && lf.Mark.Kind == "box") {
        frame++;
        DrawTip(o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, locateLines, H, false, false, false, locateCaps, false, (lf.Target == "cell" || afLive) && !GuideStyle ? null : HlLocate(lf, step, o));
      } else if (front && locateLines.Length > 0) {
        frame++;
        DrawTipTop(locateLines, locateCaps);
      } else HideTip();
      string key = step.Key + "|" + step.Pos + "|" + step.Row + "|" + step.Col + "|" + step.Count + "|" + step.MatChosen + "|" + step.MatWrong + "|" + step.MatKind + "|" + step.ArtLevel + "|" + step.Scroll;
      if (key != artKey) { artKey = key; var h = LocateChanged; if (h != null) h(step, null); }
    }

    // ---- «Надеть в игре» with an artifact (src/ArtEquip.cs) ----

    /// <summary>The step of this tick from the hero screen the readers gave (hint kind "art" only).</summary>
    void UpdateArtStep() {
      if (artLatch.IsDone) return;   // finished: the last step stays for the page's live event
      if (!ArtPhase) { artEqStep = null; return; }
      var st = screens.Current;
      bool popup = st != null && st.ModalOpen && st.ModalWhy == ClickGate.ModalEquipPopup;
      var step = ArtEquipGuide.Next(artHero, artUid, screens.Hero, popup);
      // the artifact data cannot be read: after a moment the screen's own geometry gives the hint (never a click), so the player always sees a frame
      if (ArtEquipFallback.Waiting(step)) {
        long wn = NowMs();
        if (artWaitSince == 0 || wn < artWaitSince) artWaitSince = wn;
        if (wn - artWaitSince >= ArtEquipFallback.WaitMs) {
          var gs = ArtEquipFallback.Guess(artUid, screens.Hero, step);
          if (gs.Kind != ArtEquipKind.Wait) step = gs;
        }
      } else artWaitSince = 0;
      // the list's own filter is the auto click's step before a long scroll; the hints (and the pilot once it is done with it) show the cell
      if (step.Filter && step.Kind == ArtEquipKind.PickCell && (!ArtAutoOn || artEq.FilterSpent)) step.Filter = false;
      artEqStep = step;
      var hs = screens.Hero;
      if (step.Kind == ArtEquipKind.Done) {
        Log.Write("art equip: step " + step.Key + " owner " + step.Owner + " - done for artifact " + artUid + ", not armed again");
        artLatch.MarkDone(artHero, artUid); artEq.Reset(); RFX.SetArtGoal(0, 0);
        return;
      }
      string sig = step.Key + "|" + step.Why + "|" + step.Owner;
      if (sig != artEqSaid) {
        artEqSaid = sig;
        Log.Write("art equip: step " + step.Key + (step.Why != "" ? " (" + step.Why + ")" : "") + " owner " + step.Owner + (hs != null ? " tab " + hs.Tab + " list " + hs.ArtListOpen + " sel " + hs.ArtSel + " rows " + hs.ArtUids.Count + " worn " + hs.ArtWorn + " hideWorn " + hs.ArtHideWorn + " fit " + hs.ArtGoalFit + (hs.ArtGoalExclusive ? " exclusive" : "") + " wornAhead " + hs.ArtWornBefore + " scroll " + (hs.ArtList != null ? hs.ArtList.ToString() : "none") : " (no hero screen)") + (popup ? " POPUP" : ""));
      }
    }

    /// <summary>Form_EquipPopUp («<artifact> в ячейке героя: <имя>», «Отмена» blue left, «Заменить» gold right) seen on the picture, only asked for while the artifact's card is open.</summary>
    void FillArtPopup(ScreenInputs inp, IntPtr hwnd) {
      var hs = inp.Hero;
      if (!ArtPhase || hs == null || !hs.FormOk || hs.Tab != RFX.HeroArtTab || !hs.ArtListOpen || hs.ArtSel != artUid) return;
      if (ArtPopupSeen(hwnd)) { inp.ModalExtra = true; inp.ModalExtraWhy = ClickGate.ModalEquipPopup; }
    }

    bool ArtPopupSeen(IntPtr hwnd) {
      W32.RECT cr;
      if (hwnd == IntPtr.Zero || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) return false;
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      var rr = ArtFrames.Rect(Geo.Current, ArtEquipFrames.KeyPopup, W, H, U);
      if (rr == null) return false;
      int x = (int)(rr.X + rr.W * 0.06), w = (int)(rr.W * 0.88), y = (int)(rr.Y + rr.H * 0.12), h = (int)(rr.H * 0.55);
      int mx = (int)(W - x - w);   // the left button is the right one mirrored about the box's centre
      var gold = Grab(o.X + x, o.Y + y, w, h); var blue = Grab(o.X + mx, o.Y + y, w, h);
      return gold != null && blue != null && ButtonCheck.Share(gold, ButtonCheck.IsGold) >= ButtonCheck.MinShare && ButtonCheck.BlueShare(blue) >= ButtonCheck.MinShare;
    }

    Hl HlArtEq(LocateFrame lf, ArtEquipStep st, W32.POINT o) {
      var g = Geo.Current;
      if (lf.Target == "cell") {
        // the card's own outline (a rounded card) from the geometry file when the whole card is on the screen; a card cut by the list's edge keeps the plain rounded box
        var cs = g != null ? g.Shape("hero.artCell") : null; var ci = g != null ? g.Inset("hero.artCell") : null;
        bool whole = cs != null && lf.Mark.H >= lf.Mark.W * 110.0 / 80.0 * 0.98;
        if (!whole) return HlRound(o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, true);
        return new Hl { Rect = new GeoRect { X = o.X + lf.Mark.X, Y = o.Y + lf.Mark.Y, W = lf.Mark.W, H = lf.Mark.H }, Shape = cs, Inset = ci, Important = true };
      }
      var ins = g != null ? g.Inset(lf.Target) : null; var sh = g != null ? g.Shape(lf.Target) : null;
      var r = ins != null && lf.Raw != null ? new GeoRect { X = o.X + lf.Raw.X, Y = o.Y + lf.Raw.Y, W = lf.Raw.W, H = lf.Raw.H } : new GeoRect { X = o.X + lf.Mark.X, Y = o.Y + lf.Mark.Y, W = lf.Mark.W, H = lf.Mark.H };
      return new Hl { Rect = r, Shape = sh, Inset = ins, Important = st.Kind == ArtEquipKind.PressReplace || st.Kind == ArtEquipKind.ConfirmTakeOver };
    }

    // the plate and the frame of the artifact step: the tab, the list button, the artifact's cell (a pill at the list's edge when it is off screen), «Заменить», the pop-up's «Заменить»
    void TickArtHint(W32.POINT o, double W, double H) {
      double U = Ui.Unit(W, H);
      var st = artEqStep; var hs = screens.Hero;
      // the hero is not open: the same way in as the gear steps (the «Герои» button, the hero list with the plan's hero framed, back out of a gear list)
      if (st != null && st.Kind == ArtEquipKind.Hero) { TickHeroHint(o, W, H); return; }
      if (st == null || st.Kind == ArtEquipKind.Done) { if (glow.Visible) glow.Hide(); drawnKey = null; HideTip(); return; }
      string main = Tx("art_" + st.Name), caps = Tx("art_caps");
      var lines = main != "" ? new[] { main } : new string[0];
      var lf = ArtEquipFrames.For(st, hs, Geo.Current, W, H, U);
      frame++;
      bool aeLive = false;
      if (lf != null && lf.Mark.Kind == "box" && lf.Target != "cell") {   // the tab, the list button, «Скрыть надетое», «Заменить» (its plate Button_Normal_2), the pop-up's: where the game has put them (FrameFor)
        var lay = new[] { (double)lf.Mark.X, lf.Mark.Y, lf.Mark.W, lf.Mark.H };
        aeLive = LiveInto(lf, W, H, U);
        if (lf.Target == ArtEquipFrames.KeyReplace) ArtReplaceSay(lay, aeLive ? new[] { lf.Raw.X, lf.Raw.Y, lf.Raw.W, lf.Raw.H } : null);
      } else if (st.Kind == ArtEquipKind.PickCell && st.Pos > 0 && lf != null) {
        // the artifact's card where the game has put it (the grid's live cells): its frame; no live cell in the viewport: the layout's pill / nothing on a guessed box
        double[] lcf; var see = LiveCell(UiCellKind.HeroArt, artUid, null, W, H, U, new double[] { lf.Mark.X, lf.Mark.Y, lf.Mark.W, lf.Mark.H }, "art:" + artUid, out lcf);
        if (see == UiCellSee.Shown) lf = new LocateFrame { Target = "cell", Raw = new GeoRect { X = lcf[0], Y = lcf[1], W = lcf[2], H = lcf[3] }, Mark = new LocateMark { Kind = "box", X = (int)Math.Round(lcf[0]), Y = (int)Math.Round(lcf[1]), W = (int)Math.Round(lcf[2]), H = (int)Math.Round(lcf[3]) } };
        else if (see == UiCellSee.Hidden && lf.Mark.Kind == "box") lf = null;
      }
      if (st.Kind == ArtEquipKind.PickCell && st.Pos > 0 && !st.Filter) {
        // find the artifact: one steady hint for the whole step - the plate at the top while the list moves (no plate and frame laid out anew per
        // drag), the side scroll strip (the part on the screen, the artifact's dot) redrawn only when the scroll moves; the card framed once the list stands still
        bool still = artScroll.Note(hs != null && hs.ArtList != null ? hs.ArtList.ScrollY : double.NaN, NowMs());
        var sp = hs != null ? ArtFrames.Strip(Geo.Current, ArtEquipFrames.KeyList, hs.ArtList, Math.Max(hs.ArtUids.Count, st.Pos), st.Pos - 1, 3, 116, W, H, U) : null;
        if (sp != null) Scene(glow, ref drawnKey, null, null, null, new[] { o.X + sp[0], o.Y + sp[1], o.Y + sp[2], o.Y + sp[3], o.Y + sp[4], o.Y + sp[5] });
        else { if (glow.Visible) glow.Hide(); drawnKey = null; }
        if (still && lf != null && lf.Mark.Kind == "box") DrawTip(o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, lines, H, false, false, false, caps != "" ? caps : null, false, HlArtEq(lf, st, o));
        else if (lines.Length > 0) DrawTipTop(lines, caps != "" ? caps : null);
        else HideTip();
        return;
      }
      artScroll.Reset();
      if (lf != null && lf.Mark.Kind != "box") {   // the cell is off the screen: the pill at the list's edge
        Draw(lf.Mark.Kind, o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, lf.Mark.Rows);
        if (lines.Length > 0) DrawTipTop(lines, caps != "" ? caps : null); else HideTip();
        return;
      }
      if (glow.Visible) glow.Hide(); drawnKey = null;
      Hl hl = null;
      if (lf != null && aeLive) {   // a live frame: its sprite's outline (FrameFor), else a rounded box on it (the geometry's inset is of its own rect)
        hl = HlLive(o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H) ?? HlRound(o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, true);
      } else if (lf != null) hl = HlArtEq(lf, st, o);
      if (lf != null) DrawTip(o.X + lf.Mark.X, o.Y + lf.Mark.Y, lf.Mark.W, lf.Mark.H, lines, H, false, false, false, caps != "" ? caps : null, false, hl);
      else if (lines.Length > 0) DrawTipTop(lines, caps != "" ? caps : null);
      else HideTip();
    }
    /// <summary>The Ascension / recast list's side scroll strip for the step's item (screen px: {x, viewTop, viewBottom, dotY, b0, b1}), from the game's
    /// scroll reading and the list's length; null = not a list step, no reading or no geometry (the pill alone).</summary>
    double[] FoundryStrip(LocateStep st, W32.POINT o, double W, double H, double U) {
      var rs = locateRs;
      if (st == null || rs == null || st.Pos <= 0 || (st.Kind != LocateKind.AscendPick && st.Kind != LocateKind.RecastPick)) return null;
      bool asc = st.Kind == LocateKind.AscendPick;
      int n = Math.Max(asc ? rs.AscendEquips.Length : rs.Equips.Length, st.Pos);
      var sp = FoundryPlace.Strip(Geo.Current, asc ? "ascend" : "recast", asc ? rs.AscendList : rs.RecastList, n, st.Pos - 1, W, H, U);
      return sp == null ? null : new[] { o.X + sp[0], o.Y + sp[1], o.Y + sp[2], o.Y + sp[3], o.Y + sp[4], o.Y + sp[5] };
    }
    readonly ScrollStill artScroll = new ScrollStill();
    string artReplaceSaid;
    /// <summary>The test build's frame check of the card's «Заменить», once per change: the geometry's rect against the live plate's frame and how far it moved.</summary>
    void ArtReplaceSay(double[] lay, double[] lv) {
      if (!Channel.IsTest) return;
      Func<double[], string> r4 = a => a == null ? "none" : "[" + (int)Math.Round(a[0]) + "," + (int)Math.Round(a[1]) + "," + (int)Math.Round(a[2]) + "," + (int)Math.Round(a[3]) + "]";
      string k = "frame check: art replace layout=" + r4(lay) + " live=" + r4(lv)
        + (lv != null ? " dx=" + (int)Math.Round(lv[0] + lv[2] / 2 - lay[0] - lay[2] / 2) + " dy=" + (int)Math.Round(lv[1] + lv[3] / 2 - lay[1] - lay[3] / 2) : "");
      if (k == artReplaceSaid) return;
      artReplaceSaid = k; Log.Write(k);
    }

    // the auto click for the artifact steps (src/ArtEquip.cs ArtEquipPilot): only with ArtAutoOn; «Заменить» of the card and of the pop-up also need the player's auto confirm.
    void TickArtEquip(W32.POINT o, W32.RECT cr, AutoView v) {
      var step = artEqStep;
      if (step == null || !ArtAutoOn) { artEqAutoSaid = ""; return; }
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      if (v.Foreground && !v.UserBusy) {
        var pre = gate.Preflight(ClickOwner.Equip, screens.Current, v.NowMs);
        AnnounceHalts();
        if (pre.Reason == "offer" && OfferStep(ClickOwner.Equip, screens.Current, o, cr, v.Foreground, v.NowMs)) return;
        if (artEq.Stopped || pre.Verdict != GateVerdict.Allow) return;   // the last click's effect is awaited / the screen is not recognised yet
      }
      bool btnSeen = false;
      if (step.Kind == ArtEquipKind.PressReplace && v.Foreground) {
        var rr = ArtFrames.Rect(Geo.Current, ArtEquipFrames.KeyReplace, W, H, U);
        btnSeen = rr != null && IsBlueButton(o, new[] { (int)rr.X, (int)rr.Y, (int)rr.W, (int)rr.H });
      }
      var view = new ArtEquipView { NowMs = v.NowMs, Foreground = v.Foreground, UserBusy = v.UserBusy, UserClicked = v.UserClicked, W = W, H = H, U = U, Step = step, Hero = screens.Hero,
                                    Geometry = Geo.Current, AutoConfirm = AutoConfirm, ReplaceBtnSeen = btnSeen };
      if (step.Kind == ArtEquipKind.PickCell && !step.Filter) view.CellLive = ArtCellLive(W, H, U);
      if (step.Filter) view.FilterLive = ArtFilterLive(W, H, U);
      var mv = artEq.Step(view);
      if (artEq.Trace != "") { Log.Write("art equip auto: " + artEq.Trace); artEq.Trace = ""; }
      string said = mv.State + "|" + mv.Why + "|" + step.Key;
      if (said != artEqAutoSaid) { artEqAutoSaid = said; Log.Write("art equip auto: " + mv.State + (mv.Why != "" ? " (" + mv.Why + ")" : "") + " at step " + step.Key); AutoSay(mv.State == "stopped" ? "failed" : mv.State == "hint" ? "idle" : mv.State); }
      if (mv.Action.Kind == AutoKind.None) return;
      long hero = artHero, uid = artUid; bool drag = mv.Drag; var geo = Geo.Current;
      ClickTarget tg;
      switch (mv.Todo) {
        case ArtEquipTodo.Tab: tg = ClickTarget.ArtHeroTab; break;
        case ArtEquipTodo.OpenList: tg = ClickTarget.ArtOpenList; break;
        case ArtEquipTodo.Cell: tg = drag ? ClickTarget.ArtHeroDrag : ClickTarget.ArtHeroCell; break;
        case ArtEquipTodo.Replace: tg = ClickTarget.ArtReplace; break;
        case ArtEquipTodo.Confirm: tg = ClickTarget.ArtTakeOverConfirm; break;
        case ArtEquipTodo.Filter: tg = ClickTarget.ArtHideWorn; break;
        default: return;
      }
      bool filterOff = screens.Hero != null && screens.Hero.ArtHideWorn == 1;
      if ((tg == ClickTarget.ArtReplace || tg == ClickTarget.ArtTakeOverConfirm) && !AutoConfirm) { gate.Halt(ClickOwner.Equip, "target", "«Заменить» without the player's auto confirm"); AnnounceHalts(); return; }
      SendInside(mv.Action, o, cr, ClickOwner.Equip, tg, v.Foreground, (rq, fr) => {
        rq.Hero = hero; rq.ArtUid = uid; rq.CellOk = new[] { uid };
        if (tg == ClickTarget.ArtHideWorn) rq.Untick = filterOff;
        if (tg == ClickTarget.ArtHeroCell) {
          int i = fr.HeroScr != null ? ClickGate.ListCardIndexAt(geo, ArtEquipFrames.KeyList, fr.HeroScr.ArtList, 3, 116, rq.X, rq.Y, W, H, U) : -1;
          rq.CellUid = i >= 0 && fr.HeroScr != null && i < fr.HeroScr.ArtUids.Count ? fr.HeroScr.ArtUids[i] : 0;
        }
      });
    }

    /// <summary>The artifact's card where the game has put it (client px x, y, w, h), when the live read is fresh, the card wholly in the viewport and the list on top; else null.</summary>
    double[] ArtCellLive(double W, double H, double U) {
      if (artUid <= 0) return null;
      RFX.UiCellWant(UiCellKind.HeroArt, new[] { artUid }, W, H, U);
      int now = Environment.TickCount;
      var s = RFX.UiCellFresh(UiCellKind.HeroArt, artUid, W, H, now, RFX.UiFreshMs);
      double x, y; string why;
      if (s == null || !RFX.UiCellPoint(s, artUid, W, H, now, out x, out y, out why) || !RFX.UiCellTopIs(UiCellKind.HeroArt, screens.Current)) return null;
      var c = s.Of(artUid);
      return c != null ? new[] { c.X, c.Y, c.W, c.H } : null;
    }

    /// <summary>«Скрыть надетое»'s live rect (client px) when the read is fresh, visible and on the form on top; else null (the geometry's).</summary>
    double[] ArtFilterLive(double W, double H, double U) {
      RFX.UiButtonWant(ClickTarget.ArtHideWorn, null, W, H, U);
      int now = Environment.TickCount;
      var r = RFX.UiButtonFresh(ClickTarget.ArtHideWorn, null, W, H, now, RFX.UiFreshMs);
      double x, y; string why;
      if (!RFX.UiLivePoint(r, W, H, now, out x, out y, out why) || !RFX.UiTopIs(ClickTarget.ArtHideWorn, null, screens.Current)) return null;
      return new[] { r.X, r.Y, r.W, r.H };
    }

    // ---- auto click for the artifact promotion (src/LocatePilot.cs ArtPilot): only with ArtAutoOn. The pilot opens the «Артефакт» tab, selects the artifact's cell,
    // «Мастерская», the «Продвинуть» tab and the planned copy; the final «Продвинуть» (and «Улучшить») is the player's. Every click goes through the gate.
    readonly ArtPilot artPilot = new ArtPilot();
    string artAutoSaid = "";

    void TickArtDrive(LocateStep step, InventoryScreen inv, ArtWorkshopScreen ws, bool hold, IntPtr hwnd, W32.POINT o, W32.RECT cr, long now, double W, double H, double U) {
      step.Auto = ""; step.AutoWhy = "";
      if (!ArtAutoOn || hold || locateReq == null || locateReq.Art == null) return;
      if (sending) return;   // a click is on its way: the screens are read after it
      W32.POINT c;
      if (W32.GetCursorPos(out c)) {
        if (seenSet && (c.X != seen.X || c.Y != seen.Y)) lastMoveMs = now;
        seen = c; seenSet = true;
      }
      bool down = (W32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (W32.GetAsyncKeyState(0x02) & 0x8000) != 0;
      if (down && !btnWasDown) userClick = true;
      btnWasDown = down;
      if (artPilot.Stopped) { step.Auto = "stopped"; step.AutoWhy = artPilot.Reason; return; }
      bool busy = down || now - lastMoveMs < 700, uc = userClick; userClick = false;
      int fp, gp; W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp); W32.GetWindowThreadProcessId(hwnd, out gp);
      bool gameFront = fp == gp;
      var scr = screens.Current;
      if (gameFront && !busy) {
        var pre = gate.Preflight(ClickOwner.Locate, scr, now);
        AnnounceHalts();
        if (artPilot.Stopped) { step.Auto = "stopped"; step.AutoWhy = artPilot.Reason; return; }
        if (pre.Reason == "offer" && OfferStep(ClickOwner.Locate, scr, o, cr, gameFront, now)) return;
        if (pre.Reason == "unknown" && UnknownBackStep(ClickOwner.Locate, scr, o, cr, gameFront, now)) { step.Auto = "work"; step.AutoWhy = ""; return; }
        if (pre.Verdict != GateVerdict.Allow) return;   // the last click's effect is awaited / the screen is not recognised yet
      }
      var req = locateReq;
      gate.Flows[ClickOwner.Locate] = PilotFlow.LocateArtPromote;   // (its work screen, the workshop: never an Escape there)
      bool heroOpen = scr != null && scr.Kind == ScreenKind.HeroScreen;
      bool formOpen = scr != null && (scr.Kind == ScreenKind.Foundry || scr.Kind == ScreenKind.Gem);
      var view = new ArtView { NowMs = now, Foreground = gameFront, UserBusy = busy, UserClicked = uc, W = W, H = H, U = U, Step = step, Inv = inv, Ws = ws, Art = req.Art, Geometry = Geo.Current,
        HeroScreenOpen = heroOpen, OtherFormOpen = formOpen, InvDX = invDX };
      if (gameFront && !busy && ArtLocateAuto.NeedsCityButton(step, heroOpen, formOpen)) { view.CityButton = InventoryButtonSeen(o, W, H); view.InvDX = invDX; }
      // city first: the Foundry / the gem screen (an earlier request left it) or the step's screen not on top: back with its arrow first
      if (gameFront && !busy && BackStep(ClickOwner.Locate, scr, CityFirst.NeedsCity(PilotFlow.LocateArtPromote, scr, step.Kind, ArtLocateAuto.NeedsCityBack(view)), o, cr, gameFront, now)) { step.Auto = "work"; step.AutoWhy = ""; return; }
      var mv = artPilot.Step(view);
      if (artPilot.Trace != "") { Log.Write("art promote auto: " + artPilot.Trace); artPilot.Trace = ""; }
      string said = mv.State + "|" + mv.Why + "|" + step.Key;
      if (said != artAutoSaid) { artAutoSaid = said; Log.Write("art promote auto: " + mv.State + (mv.Why != "" ? " (" + mv.Why + ")" : "") + " at step " + step.Key); }
      if (mv.State == "background") RaiseGame(hwnd, true, now, true);
      if (mv.Action.Kind != AutoKind.None) SendArt(mv, req, ws, o, cr, gameFront, W, H, U);
      if (artPilot.Stopped) { step.Auto = "stopped"; step.AutoWhy = artPilot.Reason; }
    }

    // the artifact pilot's action -> the gate's target (and for a list cell the item that has to be under the click)
    void SendArt(ArtMove mv, LocateRequest req, ArtWorkshopScreen ws, W32.POINT o, W32.RECT cr, bool front, double W, double H, double U) {
      var art = req.Art; bool drag = mv.Drag;
      ClickTarget tg = ClickTarget.None;
      Action<ClickRequest, ScreenState> fill = null;
      var geo = Geo.Current;
      switch (mv.Todo) {
        case ArtTodo.HeroBack: tg = ClickTarget.HeroBack; break;
        case ArtTodo.InventoryButton: tg = ClickTarget.CityInventory; break;
        case ArtTodo.ArtTab: tg = ClickTarget.ArtTab; break;
        case ArtTodo.Workshop: tg = ClickTarget.ArtWorkshop; fill = (rq, fr) => { rq.CellOk = new[] { art.Uid }; }; break;
        case ArtTodo.PromoteTab: tg = ClickTarget.ArtPromoteTab; fill = (rq, fr) => { rq.CellOk = new[] { art.Uid }; }; break;
        case ArtTodo.Cell:
          tg = drag ? ClickTarget.ArtListDrag : ClickTarget.ArtCell;
          fill = (rq, fr) => {
            int i = fr.Inv != null ? ClickGate.ListCardIndexAt(geo, "inventory.artList", fr.Inv.List, LocateGuide.ArtInvCols, 116, rq.X, rq.Y, W, H, U) : -1;
            rq.CellUid = i >= 0 && fr.Inv != null && i < fr.Inv.Uids.Count ? fr.Inv.Uids[i] : 0;
            rq.CellOk = new[] { art.Uid };
            rq.NoScroll = fr.Inv == null || fr.Inv.List == null || !fr.Inv.List.Has;
          };
          break;
        case ArtTodo.MatCell: {
          tg = drag ? ClickTarget.ArtMatDrag : ClickTarget.ArtMatCell;
          // the essence the pilot planned (its item id from the read it decided on); a copy is judged by its uid against the plan
          var itemOk = new List<int>();
          if (art.Via == "essence" && ws != null && mv.MatIndex >= 1 && mv.MatIndex <= ws.MatItemIds.Count && mv.MatIndex <= ws.Mats.Count && ws.Mats[mv.MatIndex - 1] == 0) itemOk.Add(ws.MatItemIds[mv.MatIndex - 1]);
          fill = (rq, fr) => {
            if (drag) { rq.CellOk = new[] { art.Uid }; return; }   // (the rule needs the open artifact only)
            var w2 = fr.ArtWs;
            int i = w2 != null ? ClickGate.ListCardIndexAt(geo, "artifact.matList", w2.MatList, LocateGuide.ArtMatCols, 114, rq.X, rq.Y, W, H, U) : -1;
            rq.MatIndex = i >= 0 && w2 != null && i < w2.Mats.Count ? i + 1 : 0;
            rq.CellUid = rq.MatIndex > 0 ? w2.Mats[i] : 0;
            var copies = new List<long>(art.Copies); copies.Add(art.Uid);   // (CellOk also names the open artifact for the workshop check)
            rq.CellOk = copies; rq.ItemOk = itemOk;
          };
          break;
        }
      }
      SendInside(mv.Action, o, cr, ClickOwner.Locate, tg, front, fill);
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
      if (s == null || NowMs() - s.ReadAtMs > 5000) return "";   // (the screen is read every 2 s while the game is away)
      bulk = s.Kind == ScreenKind.Inventory && s.InvEquipTab && s.InvBulk;
      return s.Kind.ToString().ToLowerInvariant();
    }
    /// <summary>«Показать в игре» may click: the player's auto click and the site's "locateAuto" switch (off until the owner enables it).</summary>
    public bool LocateAutoEnabled;
    long screenReadAt, cityAt; bool cityNow; long locUnkSince = -1, shUnkSince = -1; SellHintStep shLast; bool shHold;
    readonly Dictionary<ClickOwner, string> haltSaid = new Dictionary<ClickOwner, string>();

    /// <summary>The game is gone, minimized or behind another window (the watches look less often then).</summary>
    public bool GameAway() {
      IntPtr h = GameWindow();
      return h == IntPtr.Zero || !W32.IsWindowVisible(h) || W32.IsIconic(h) || !IsForeground(h);
    }
    /// <summary>The equip helper's live poll period (src/LoadPlan.cs).</summary>
    public int LivePollMs() {
      IntPtr h = GameWindow();
      bool sh = h != IntPtr.Zero && W32.IsWindowVisible(h) && !W32.IsIconic(h);
      return LoadPlan.LivePollMs(ScreensWanted(), sh, sh && IsForeground(h), EquipLiveHot);
    }

    bool ScreensWanted() { return target != 0 || locateReq != null || filterItem != 0 || hintKind != "" || runHero != 0 || sellWanted != null; }

    /// <summary>THE read of the game's screens, once per tick (at most every 120 ms). The equip helper, «Показать в игре», the storage
    /// cleanup and the click gate take the screen from here (a short read failure is held for 1.5 s, see ScreenTracker).</summary>
    void ReadScreens(long now) {
      if (!ScreensWanted()) return;
      RFX.EnsureLiveForGuides();   // the readers' basis (handle, regions, live tables) without waiting for the first account read; throttled and off-thread
      // every 120 ms; 50 ms while a pilot's click awaits its effect (the expected field is polled until it changes); once a second while the
      // game is minimised or gone (nothing to click, nothing to draw)
      int readGap = LoadPlan.ReadGapMs(gameAway, gate.AnyPending || SellPolling || EquipHot, listMoving, inputAge, SellGuideShown);   // (a list that moves: read until it settles; nobody touching anything: 240 ms)
      if (screens.Current != null && now >= screenReadAt && now - screenReadAt < readGap) return;
      screenReadAt = now;
      long rs0 = LoadMeter.Now();
      try { ReadScreensNow(now); } finally { LoadMeter.Done(LoadMeter.Screens, rs0); }
    }

    void ReadScreensNow(long now) {
      IntPtr hwnd = GameWindow();
      gameAway = hwnd == IntPtr.Zero || W32.IsIconic(hwnd);
      bool front = hwnd != IntPtr.Zero && !W32.IsIconic(hwnd) && IsForeground(hwnd);
      // the forms from the game's window table: a cheap check of it each read (no region scan while it reads)
      RFX.FormsTick(40);
      long readT0 = System.Diagnostics.Stopwatch.GetTimestamp();
      // the form searches (a full pass, ~1 s) run on a worker thread, never here: the readers below only use the nodes found so far.
      // A form the game never built is searched for at the start of a storage run and after a click that opens it
      if ((sellScanOnce || now - sellBtnAt < 8000) && front && RFX.InvFormMissing()) RFX.RequestFormSearch("storage run, inventory not built", 1500);
      sellScanOnce = false;
      var inp = new ScreenInputs { Inv = RFX.ReadInventory(-1), Rs = RFX.ReadRecastScreenSafe(-1), Hero = RFX.ReadHeroScreen(addrs, screens.Hero) };
      if (front) FillArtPopup(inp, hwnd);
      inp.CityForm = RFX.CityShown();
      if (RFX.UiKnown) { inp.Screens = RFX.ReadShownScreens(); inp.Order = RFX.ReadShownOrder(); NoteOrder(inp.Order); }   // the screen's kind first from the window table (the readers fill its details); the forms' z-order
      bool artGuide = locateReq != null && locateReq.Purpose == "artPromote";
      if (WantArtWs(inp)) inp.ArtWs = RFX.ReadArtWorkshop();
      ReadTiming.Field(System.Diagnostics.Stopwatch.GetTimestamp() - readT0);
      string ik = ListSettle.KeyOf(inp.Inv), rk = ListSettle.KeyOf(inp.Rs);
      invSettle.Update(ik, now); rsSettle.Update(rk, now);
      listMoving = (ik != null && !invSettle.Settled) || (rk != null && rk != "" && !rsSettle.Settled);
      ReadOffersInto(inp, front, now);
      if (front && inp.CityForm != true && ScreenState.NeedsCityPicture(ScreenState.Classify(inp, now).Kind, inp.CityForm)) {   // the city form is not readable or reads closed: the city's bottom bar (a picture, at most every 400 ms)
        if (now - cityAt >= 400) { cityAt = now; cityNow = CityVisible(hwnd); }
        inp.CitySeen = cityNow;
      } else cityNow = false;
      if (RecInfo.Active) RecReads.Add(now, inp);   // the recorder: what the tracker is given (src/RecInputs.cs)
      var st = screens.Update(inp, now);
      // an unknown screen during a run: which forms the game's window table shows (or why it cannot say), at most every 10 s, the same text every 60 s
      if (front && st.Kind == ScreenKind.Unknown && now - shownDiagAt >= 10000) {
        shownDiagAt = now;
        string d = RFX.ShownFormsDiag(8);
        if (d != shownDiagSaid || now - shownDiagSaidAt >= 60000) { shownDiagSaid = d; shownDiagSaidAt = now; Log.Write("forms: unknown screen, " + d); }
      }
      if (openClickAt != 0) {
        if ((openClickWhat == "inventory" && st.Kind == ScreenKind.Inventory) || (openClickWhat == "heroes" && st.Kind == ScreenKind.HeroScreen) || (openClickWhat == "foundry" && (st.Kind == ScreenKind.Foundry || st.Kind == ScreenKind.Gem))) { Log.Write("screen: " + openClickWhat + " first read " + (now - openClickAt) + " ms after the pilot's click"); openClickAt = 0; }
        else if (now - openClickAt > 20000) { Log.Write("screen: " + openClickWhat + " not read within 20 s of the pilot's click"); openClickAt = 0; }
      }
      // search policy: a form that read fine last tick and reads 0 now = the game moved it (soon, at most every 0.5 s); an Unknown screen = look every 2 s, no count limit
      if (front) {
        string lost = RFX.TakeFormsLost();
        bool openMissing = openClickAt != 0 && (openClickWhat == "inventory" ? RFX.InvFormMissing() : openClickWhat == "foundry" && RFX.FoundryFormMissing());
        int wantGap; string wantSearch = FormSearchPolicy.Want(st.Kind, st.Held, openClickAt != 0 ? now - openClickAt : -1, openMissing, out wantGap);
        // (with the window table known the search is done at once: nothing to wait for)
        if (lost != null) { RFX.RequestFormSearch("lost: " + lost, 500); if (RFX.FormSearchRunning) { lostSearchAt = now; lostSearchGen = RFX.FormSearchGeneration; } }
        else if (wantSearch != null) RFX.RequestFormSearch(wantSearch, wantGap);
        // the workshop's form exists once the game has built it (the first open): with the artifact's card open, look for it every 2.5 s until found
        else if (artGuide && RFX.ArtFormMissing() && st.Kind == ScreenKind.Inventory && st.InvArtTab && st.InvArtSelected == locateReq.Art.Uid && now - artFormAskedAt >= 2500) {
          artFormAskedAt = now; RFX.RequestFormSearch("art workshop not built", 500);
        }
      }
      // a known form moved and is searched for again: the gate waits for the search (and the next one, which may be the one that starts) at most 40 s
      bool sw = lostSearchAt != 0 && now - lostSearchAt < 40000 && (now - lostSearchAt < 2500 || (RFX.FormSearchRunning && RFX.FormSearchGeneration <= lostSearchGen + 1));
      if (!sw) lostSearchAt = 0;
      if (gate.SearchWait != sw) { gate.SearchWait = sw; if (sw) Log.Write("screen: a known form is searched for - the pilots wait for the search"); }
      if (screens.Hero != null) heroScreen = screens.Hero;
      if (foundryBuiltPid != GamePid(hwnd) && !RFX.FoundryFormMissing()) foundryBuiltPid = GamePid(hwnd);
      gate.Observe(st, now);
      // a city «Инвентарь» press changed nothing (the gate lets it be pressed once more): the bar's place is dropped and searched again first
      if (gate.CityAgainDue.Count > 0) { gate.CityAgainDue.Clear(); cityBar.Drop(); cityInvAt = 0; Log.Write("city bar: «Инвентарь» changed nothing - the bar is searched again"); }
      // the gate retries a failed step (a pop-up took the press, nothing changed ...): what is kept of the screen is read again, the step's tries start over
      if (gate.RetryDue.Count > 0) {
        foreach (var ro in new List<ClickOwner>(gate.RetryDue)) {
          if (ro == ClickOwner.Locate) { lpilot.StepAgain(); artPilot.StepAgain(); }
          else if (ro == ClickOwner.Equip) hpilot.StepAgain();
        }
        gate.RetryDue.Clear(); cityBar.Drop(); cityInvAt = 0;
      }
      // event confirmation: the gate saw the effect of the locate pilot's button / tab click - its fixed wait after the click ends soon
      long le = gate.EffectAt(ClickOwner.Locate); if (le != locEffectSeen) { locEffectSeen = le; lpilot.Confirmed(now); }
      AnnounceHalts();
    }

    const int ConfirmPollMs = 50;
    /// <summary>The storage cleanup's pilot awaits its click's answer or its drag's move: the game is read every 50 ms.</summary>
    bool SellPolling { get { return sellWanted != null && !sellHint && spilot.Polling; } }
    /// <summary>An equip pilot awaits its click's effect, or clicked within LoadPlan.EquipHotMs: the hot fields (the hero, the slot, the selection,
    /// the filter, the tip, the owners) are read at the effect rate, so each step follows its effect instead of a fixed wait.</summary>
    bool EquipHot { get { long n = NowMs(); return runHero > 0 && AutoEnabled && (hpilot.Polling(n) || fpilot.Polling(n) || pilot.Polling(n)); } }
    /// <summary>The live poll (the guide's step) at the effect rate also for LoadPlan.EquipHotMs after an equip click: its effect moves the step.</summary>
    bool EquipLiveHot { get { return EquipHot || (runHero > 0 && AutoEnabled && NowMs() - equipClickAt < LoadPlan.EquipHotMs); } }
    long equipClickAt = -100000;
    const int ClosePopupAgainMs = 2000;
    readonly LiveStill offerLive = new LiveStill(), confirmLive = new LiveStill(); string offerLiveForm = ""; bool confirmStill;
    bool gameAway, listMoving; long locEffectSeen;
    // the lists the pilots click into: their list steps run on a settled list only (two equal reads in a row, see ListSettle)
    readonly ListSettle invSettle = new ListSettle(), rsSettle = new ListSettle();

    // TEST build: the app's own load and the read timings, one line each a minute (the lab reports them), on its own timer. The CPU is the
    // whole process (every thread: the overlay, the watches, the memory passes, the recorder) over the wall time, given as a share of all
    // the PC's cores and of one core; the mode is the overlay's at the line (idle = no guide, away = the game minimized / behind, hint =
    // a guide nobody clicks for, pilot = a pilot clicking), with the seconds of the minute in each; per activity calls and ms a minute.
    long loadAt; TimeSpan loadCpu; double loadRec; long loadReads; int loadGc0, loadGc2; string loadMode = "idle"; long inputAge = -1;
    readonly long[] modeMs = new long[4]; long modeAt;   // the guides' ms in the minute: away, hint, pilot, other (the recorder alone)
    void LogLoad() {
      if (!Channel.IsTest) return;
      long now = NowMs();
      using (var me = Process.GetCurrentProcess()) {
        var cpu = me.TotalProcessorTime; double recCpu = RecCpu.TotalMs; long reads = System.Threading.Interlocked.Read(ref RFX.Reads);
        int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
        if (loadAt != 0) {
          double wall = Math.Max(1, now - loadAt);
          string mode = (timer.Enabled ? loadMode : "idle") + (RecInfo.Active ? "+rec" : "");   // (+rec: the screen recorder ran, its frames are in the cpu)
          string guides = "guides: away " + modeMs[0] / 1000 + " s, hint " + modeMs[1] / 1000 + " s, pilot " + modeMs[2] / 1000 + " s";
          string extra = guides + ", mem " + (me.PrivateMemorySize64 >> 20) + " MB, reads " + (long)((reads - loadReads) * 1000 / wall) + "/s, gc " + (gc0 - loadGc0) + "/" + (gc2 - loadGc2) + " (gen0/gen2)";
          Log.Write(LoadMeter.Line((cpu - loadCpu).TotalMilliseconds, recCpu - loadRec, wall, Environment.ProcessorCount, mode, extra));   // (rec = the recorder's threads; the rest = a player's load)
          string rt = ReadTiming.Take(); if (rt != null) Log.Write(rt);
        } else LoadMeter.Line(0, 1, 1, "", null);   // (the counters start from here)
        loadAt = now; loadCpu = cpu; loadRec = recCpu; loadReads = reads; loadGc0 = gc0; loadGc2 = gc2; Array.Clear(modeMs, 0, modeMs.Length);
      }
    }

    /// <summary>The game process the Foundry's form was found in, or the pilot opened it in (0 = none): from then on «Литейная» needs the Foundry read closed.</summary>
    int foundryBuiltPid;
    static int GamePid(IntPtr hwnd) { int p = 0; if (hwnd != IntPtr.Zero) W32.GetWindowThreadProcessId(hwnd, out p); return p; }

    bool CityVisible(IntPtr hwnd) {
      W32.RECT cr;
      if (hwnd == IntPtr.Zero || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) return false;
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      return InventoryButtonSeen(o, cr.R, cr.B) || HeroesButtonSeen(o, cr.R, cr.B);
    }

    /// <summary>The artifact workshop is read this tick (ScreenState.WantArtWs).</summary>
    bool WantArtWs(ScreenInputs inp) {
      return ScreenState.WantArtWs(locateReq != null && locateReq.Purpose == "artPromote", inp.Screens);
    }

    string orderSaid;
    /// <summary>Test build: the window table's z-order when it changes (zorder: top first, or "not certain").</summary>
    void NoteOrder(List<string> order) {
      if (!Channel.IsTest) return;
      string s = order == null ? "not certain" : string.Join(" > ", order.ToArray());
      if (s != orderSaid) { orderSaid = s; Log.Write("zorder: " + s); }
    }

    /// <summary>A fresh read for the click gate: not held, not throttled (the screen right before the click).</summary>
    ScreenState FreshScreen(bool wantCity, bool wantKnownForms = false) {
      long now = NowMs();
      var inp = new ScreenInputs { Inv = RFX.ReadInventory(-1), Rs = RFX.ReadRecastScreenSafe(-1), Hero = RFX.ReadHeroScreen(addrs, screens.Hero), CityForm = RFX.CityShown(), Offers = offerOrder.Note(RFX.ReadOffers()) }; inp.OfferTopKnown = offerOrder.TopKnown; freshOffers = inp.Offers;
      if (RFX.UiKnown) { inp.Screens = RFX.ReadShownScreens(); inp.Order = RFX.ReadShownOrder(); NoteOrder(inp.Order); }   // (the click gate reads every shown form by its place)
      gate.OrderRead = RFX.UiKnown;   // (the window table read: its z-order is expected - without it no click where the top is not certain)
      if (wantKnownForms) inp.ShownKnown = RFX.ReadShownKnownForms();   // the unknown-screen back press: no known screen's form (the battle among them) may be shown
      { IntPtr gw = GameWindow(); if (gw != IntPtr.Zero && !W32.IsIconic(gw) && IsForeground(gw)) FillArtPopup(inp, gw); }
      if (WantArtWs(inp)) inp.ArtWs = RFX.ReadArtWorkshop();   // the artifact guide's clicks are judged by the workshop too
      var st = ScreenState.Classify(inp, now);
      // the forms nobody knows (read every 2 s by the tick): over the same known screen a later one is a pop-up on top; on an unknown screen, its name
      if (topFlowNow != "" && st.Kind == topFlowKind) { inp.Flow = topFlowNow; st = ScreenState.Classify(inp, now); }
      if (topNewNow != "" && st.Kind == topNewKind) { inp.TopNew = topNewNow; st = ScreenState.Classify(inp, now); }
      else if (topUnknownNow != "" && st.Kind == ScreenKind.Unknown) { inp.TopUnknown = topUnknownNow; st = ScreenState.Classify(inp, now); }
      else if (topCityNow != "" && st.Kind == ScreenKind.City) { inp.TopCity = topCityNow; st = ScreenState.Classify(inp, now); }
      // a pop-up or screen that came since the tick's read of the forms (every 2 s) and before this click: it lies on top. No click now (the
      // forms count as unread); the next tick takes the new read and closes it first as any pop-up (a gate check now would stop the run on it)
      if (unknownForms != null && now - unknownFormsAt >= FreshFormsMs && (inp.Offers == null || inp.Offers.Count == 0)) {
        var fl = RFX.ReadUnknownShownForms(4);
        if (fl != null && ScreenMap.Newcomers(unknownForms, fl) != "") { unknownForms = fl; unknownFormsAt = now; formsUnread = true; }
      }
      // a click on a city button also needs the picture of the bar (the form alone is not enough for a click)
      if (wantCity && ScreenState.NeedsCityPicture(st.Kind, inp.CityForm)) {
        bool seen = CityVisible(GameWindow());
        if (inp.CityForm == true && !seen) inp.CityForm = false;
        inp.CitySeen = seen; st = ScreenState.Classify(inp, now);
      }
      return st;
    }

    /// <summary>A pilot was stopped by the gate (or by a click that changed nothing): each owner's pilot shows it once and goes on as hints only.</summary>
    void AnnounceHalts() {
      foreach (var o in new[] { ClickOwner.Locate, ClickOwner.Sell, ClickOwner.Equip, ClickOwner.LocateFilter }) {
        string why = gate.HaltReason(o), said;
        haltSaid.TryGetValue(o, out said);
        if (why == (said ?? "")) continue;
        haltSaid[o] = why;
        if (why == "") continue;
        Log.Write("auto click stopped (" + o + "): " + why);
        RaiseAutoRec(AutoRecPolicy.HaltTag(o.ToString(), why, gate.HaltDetail(o)));
        if (o == ClickOwner.Locate) { lpilot.Halt(why); artPilot.Halt(why); }
        else if (o == ClickOwner.LocateFilter) lpilot.FilterHalt(why);   // (its own stop: the search goes on without the filter)
        else if (o == ClickOwner.Sell) { if (!sellArt) SellSay("gate_" + why); }   // (the artifact cleanup is a hint run for the page: the stop is in the log only)
        else { artEq.Halt(why); AutoSay("gate_" + why); }
      }
    }

    /// <summary>A new request / run of this owner: it may click again.</summary>
    void ResumeClicks(ClickOwner o) { gate.Resume(o); haltSaid.Remove(o); }

    void RaiseAutoRec(string tag) { if (!Channel.IsTest) return; var a = AutoRec; if (a != null) { try { a(tag); } catch (Exception e) { Log.Write("rec: auto failed: " + e.Message); } } }

    // ---- the game's offer / notice pop-ups over the city (src/OfferForms.cs): read from the window table, closed by the pilots (OfferClose)
    volatile bool uiFinding; long uiFindAt, unknownFormsAt, lostSearchAt; int lostSearchGen; List<string> unknownForms;
    // «Заменить» is the hint (the hero screen's item tip is the pilot's own flow form, found by this read only): the forms are read every tick, not every 2 s
    // (live 1.6.66.440: the tip was named 1.4 s after it opened, the «Заменить» frame waited for it)
    bool HintFlowWanted(ScreenKind cls) { return cls == ScreenKind.HeroScreen && hintKind == "replace"; }
    // the shown unknown forms when the screen became the known one it is (TopNew: the ones that came later are a pop-up on top), and the last results
    const int TopSettleMs = 3000, TopFlickerMs = 2000, KnownFormsMs = 60000, FreshFormsMs = 0, SettleReadMs = 500, UnknownReadMs = 250;   // (UnknownReadMs: on an unknown screen - its top form named within a tick or two)   // (FreshFormsMs: the forms read again before every click - cheap with the window table; the pilots click faster now)
    List<string> knownForms, firstForms; long knownFormsAt, firstFormsAt;
    // the window table's read failed this tick (the offers or the shown forms): a pop-up over the screen cannot be ruled out, so no click or key
    // goes to the game until a read succeeds (the read is tried again on the next tick); never set while the table is not known (RFX.UiKnown)
    bool formsUnread; ScreenKind formsKind = ScreenKind.Unknown;   // formsKind: the screen the tick's forms were read for
    long topBaseAt, topLastAt; ScreenKind topLastKind = ScreenKind.Unknown, topBaseKind = ScreenKind.Unknown; HashSet<string> topBase = new HashSet<string>(); string topNewNow = "", topUnknownNow = "", topCityNow = ""; ScreenKind topNewKind = ScreenKind.Unknown;
    string topFlowNow = ""; ScreenKind topFlowKind = ScreenKind.Unknown;   // the pilots' own flow forms on the screen (ScreenMap.FlowForms) and the screen they lie on
    readonly HashSet<string> unknownSaid = new HashSet<string>();
    readonly OfferForms.Order offerOrder = new OfferForms.Order();   // which of several offers shown is on top (the one that came last)
    string offerSaid = ""; long unknownFromAt; bool unknownNamedSaid;   // (the timing line: the unknown screen's top form named how long after it came)

    /// <summary>The offer pop-ups shown (and, on an unrecognised screen or the city, the shown forms nobody knows, every 2 s) into this tick's inputs.
    /// The window table is looked for off this thread when it is not known yet.</summary>
    void ReadOffersInto(ScreenInputs inp, bool front, long now) {
      if (!RFX.UiKnown) {
        if (front && !uiFinding && Layout.Current != null && (uiFindAt == 0 || now - uiFindAt > 20000)) {
          uiFindAt = now; uiFinding = true;
          System.Threading.Tasks.Task.Factory.StartNew(() => { try { RFX.FindUi(20000); } catch (Exception) { } finally { uiFinding = false; } });
        }
        return;
      }
      inp.Offers = offerOrder.Note(RFX.ReadOffers()); inp.OfferTopKnown = offerOrder.TopKnown;   // (several at once: the one that came last is on top)
      formsUnread = inp.Offers == null;
      string os = inp.Offers == null ? "?" : string.Join(",", inp.Offers.ToArray());
      if (os != offerSaid) { offerSaid = os; if (os != "" && os != "?") Log.Write("offer: shown " + os); }
      var cls = ScreenState.Classify(inp, now).Kind;
      topNewNow = ""; topUnknownNow = ""; topCityNow = ""; topFlowNow = "";
      if (cls == ScreenKind.Unknown && formsKind != ScreenKind.Unknown) { unknownFormsAt = 0; unknownFromAt = now; unknownNamedSaid = false; }   // just became unknown: its forms read at once
      formsKind = cls;
      if (front && (inp.Offers == null || inp.Offers.Count == 0)) {
        // the forms of the last read on the city or a known screen (before this tick's): one not in it came with the screen. A short unknown
        // stretch between (the slow laptop: the inventory's form is found seconds after it opened) is skipped: a pop-up that came then is new too
        // (before any: the first read of the run, the forms there when it began - a pop-up that came after it is new as well)
        var prevForms = knownForms != null && now - knownFormsAt <= KnownFormsMs ? knownForms : firstForms != null && now - firstFormsAt <= KnownFormsMs ? firstForms : unknownForms;
        // (every 2 s; within a known screen's settle time every SettleReadMs: a part it builds late must be read before that time ends)
        bool settling = topBaseKind != ScreenKind.Unknown && topBaseKind != ScreenKind.City && now - topBaseAt < TopSettleMs;
        if (now - unknownFormsAt >= ConfirmFrame.FormsReadMs(HintFlowWanted(cls), settling ? SettleReadMs : cls == ScreenKind.Unknown ? UnknownReadMs : 2000) || unknownFormsAt == 0) { unknownFormsAt = now; unknownForms = RFX.ReadUnknownShownForms(4); if (unknownForms == null) { formsUnread = true; unknownFormsAt = 0; }  else if (firstForms == null) { firstForms = unknownForms; firstFormsAt = now; } }
        var cur = unknownForms ?? new List<string>();
        // the pilots' own flow forms on this screen (the item tip the item select opens ...; on an unknown moment: the last known screen's): its sub-state
        ScreenKind flowOn = cls == ScreenKind.Unknown ? topLastKind : cls;
        var flow = cur.FindAll(n => ScreenMap.IsFlow(flowOn, n));
        if (flow.Count > 0) {
          cur = cur.FindAll(n => !ScreenMap.IsFlow(flowOn, n));
          inp.Flow = topFlowNow = string.Join(",", flow.ToArray()); topFlowKind = cls;
          if (unknownSaid.Add("flow:" + flowOn + ":" + topFlowNow)) Log.Write("screen: " + topFlowNow + " is the pilot's own flow on " + flowOn + " (not a pop-up to close)");
        }
        foreach (var n in cur) if (unknownSaid.Add(n)) Log.Write("offer: unknown top form " + n + " (not in list)");
        if (cls == ScreenKind.Unknown) {
          if (cur.Count > 0) {
            inp.TopUnknown = topUnknownNow = string.Join(",", cur.ToArray());
            if (!unknownNamedSaid && cur.Exists(n => !n.StartsWith("id:"))) { unknownNamedSaid = true; Log.Write("screen: top form " + topUnknownNow + " named " + (now - unknownFromAt) + " ms after the screen became unknown"); }
            if (unknownSaid.Add("rec:" + inp.TopUnknown)) RaiseAutoRec(AutoRecPolicy.UnknownFormTag(inp.TopUnknown));
          }
        } else if (cls == ScreenKind.City) {
          // over the city: a form the screen map calls a pop-up, a dangerous dialog or a wait covers its buttons (the pilots close it first: MapStep)
          var cov = cur.FindAll(n => "PEDW".IndexOf(ScreenMap.ClassOf(n)) >= 0);
          if (unknownForms != null) { knownForms = unknownForms; knownFormsAt = now; }
          if (cov.Count > 0) {
            inp.TopCity = topCityNow = string.Join(",", cov.ToArray());
            if (unknownSaid.Add("city:" + topCityNow)) Log.Write("screen map: the city is covered by " + topCityNow + " (" + ScreenMap.ClassName(ScreenMap.ClassOf(ScreenMap.Top(topCityNow))) + ")");
          }
        } else if (unknownForms != null) {
          // over a known screen: the forms there when it came are its own; one that came later is a pop-up on top (never pressed: the gate stops on it)
          // (the forms read within TopSettleMs of the screen's coming are its own too: a part the game builds a moment later; the read right after the change is fresh)
          // (a read that failed for a tick or two flips the screen to unknown and back: the same screen within TopFlickerMs keeps its base, else a
          // pop-up shown meanwhile would be taken for the screen's own form - the slow laptop's grid clicks went into one)
          if (cls != topBaseKind && cls == topLastKind && now - topLastAt <= TopFlickerMs) topBaseKind = cls;
          if (cls != topBaseKind) { topBaseKind = cls; topBaseAt = now; topBase = new HashSet<string>(); unknownForms = RFX.ReadUnknownShownForms(4); unknownFormsAt = now; cur = unknownForms ?? new List<string>(); if (unknownForms == null) { formsUnread = true; unknownFormsAt = 0; } }
          topLastKind = cls; topLastAt = now;
          // (one shown already is the screen's own, a pop-up may lie under the full screen - the window table has no order; a pop-up or screen that came
          // with it or later lies on top, a part the game builds later does not)
          if (now - topBaseAt < TopSettleMs) foreach (var n in cur) if (now == topBaseAt && (prevForms == null || prevForms.Contains(n)) ? ScreenMap.MayBeBase(n) : ScreenMap.MayBeLatePart(n)) topBase.Add(n);
          var fresh = cur.FindAll(n => !topBase.Contains(n));
          if (unknownForms != null) { knownForms = cur.FindAll(n => topBase.Contains(n)); knownFormsAt = now; }   // (a pop-up on top stays one on the next screen)
          if (fresh.Count > 0) { inp.TopNew = topNewNow = string.Join(",", fresh.ToArray()); topNewKind = cls; }
        }
        if (cls == ScreenKind.Unknown || cls == ScreenKind.City) topBaseKind = cls;
      } else { unknownForms = null; unknownFormsAt = 0; }   // (an offer list on top: the forms are read again the moment it goes - a pop-up that came under it lies on top)
    }

    /// <summary>Before a pilot's step: an offer pop-up on top is closed first (its ×, through the gate). True = the tick is taken (a close was
    /// sent, its effect is awaited, the pop-up is still opening, or the pilot stopped on it); false = no pop-up, the pilot goes on.</summary>
    bool OfferStep(ClickOwner owner, ScreenState scr, W32.POINT o, W32.RECT cr, bool front, long now) {
      if (scr == null || scr.OfferCount == 0) return false;
      if (gate.Halted(owner)) return true;
      if (gate.Pending(owner) || sending) return true;
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      string form = scr.Offer;
      RFX.UiButtonWant(ClickTarget.OfferClose, form, W, H, U);   // its × read while it opens: the click takes the live rect (fresh)
      // still opening (its scale-in animation): its × live and held still ends the wait early (LiveStill), else OfferForms.SettleMs
      if (form != offerLiveForm) { offerLiveForm = form; offerLive.Reset(); }
      var olr = RFX.UiButtonFresh(ClickTarget.OfferClose, form, W, H, Environment.TickCount, RFX.UiFreshMs);
      bool ostill = offerLive.Note(olr != null ? new[] { olr.X, olr.Y, olr.W, olr.H } : null, now);
      if (!OfferForms.Settled(scr, now, ostill)) return true;
      var pt = OfferForms.ClosePoint(Geo.Current, form, W, H, U);
      { var cr0 = OfferForms.CloseRect(Geo.Current, form, W, H, U); LiveFrame(ClickTarget.OfferClose, form, W, H, U, true, cr0 != null ? new[] { cr0.X, cr0.Y, cr0.W, cr0.H } : null); }   // (the test build's frame line of the ×)
      if (pt == null) {
        gate.Halt(owner, "offer", "the offer pop-up " + form + (OfferForms.IsOffer(form) ? " has no known close button" : " is not on the list") + " - not clicked");
        AnnounceHalts();
        return true;
      }
      var g = Geo.Current;
      SendInside(new AutoAction { Kind = AutoKind.Click, X = pt.X, Y = pt.Y }, o, cr, owner, ClickTarget.OfferClose, front, (rq, fr) => { rq.Form = form; rq.W = W; rq.H = H; rq.U = U; rq.Geo = g; });
      return true;
    }

    /// <summary>Before a pilot's step on a screen nobody knows: the game's back arrow top left (HeroBack's spot), through the gate (UnknownBack:
    /// no pop-up, no known screen's form shown, not the city or a fight, at most ClickGate.MaxUnknownBacks a run, each press has to change the
    /// screen). True = a press was tried this tick; false = not due, the pilot goes on (it waits on the unknown screen as before).</summary>
    bool UnknownBackStep(ClickOwner owner, ScreenState scr, W32.POINT o, W32.RECT cr, bool front, long now) {
      if (MapStep(owner, scr, o, cr, front, now)) return true;
      // the last back press changed nothing: one Escape instead (EscapeBack; never on the city or in a fight)
      if (front && !sending && scr != null && scr.Kind == ScreenKind.Unknown && gate.EscapeDue(owner, scr, now)) { EscapeSend(owner, cr, front); AnnounceHalts(); return true; }
      if (!front || sending || !gate.UnknownBackDue(owner, scr, now)) return false;
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      // the screen map's back arrow of the form on top (the standard one top left for nearly every form)
      var md = ClickGate.MapExit(scr, W, H, U);
      double bx = md.Move == MapMove.Back && md.Point != null ? md.Point.X : hg2.BackX * U, by = md.Move == MapMove.Back && md.Point != null ? md.Point.Y : hg2.BackY * U;
      SendInside(new AutoAction { Kind = AutoKind.Click, X = bx, Y = by }, o, cr, owner, ClickTarget.UnknownBack, front, (rq, fr) => { rq.U = U; rq.W = W; rq.H = H; });
      AnnounceHalts();
      return true;
    }

    /// <summary>Back to the city, shared by every pilot: the pilot cannot go on from this known screen (<paramref name="need"/>) - the game's back
    /// arrow top left (CityBack, through the gate: no pop-up, not the city or a fight, at most ClickGate.MaxCityBacks a run, each press has to
    /// change the screen). A press that changed nothing is followed by ONE Escape key press (EscapeBack, at most ClickGate.MaxEscapes a run).
    /// True = a press was tried this tick, or one is due on this screen and the pilot waits for it (city first); false = the pilot goes on.</summary>
    bool BackStep(ClickOwner owner, ScreenState scr, bool need, W32.POINT o, W32.RECT cr, bool front, long now) {
      if (MapStep(owner, scr, o, cr, front, now)) return true;   // a pop-up over the screen (or the city) first
      if (!need) { if (scr != null && scr.Kind != ScreenKind.Unknown) gate.DropEscape(owner); return false; }
      if (!front || sending) return false;
      if (gate.EscapeDue(owner, scr, now)) { EscapeSend(owner, cr, front); AnnounceHalts(); return true; }
      // (the screen is not the flow's: the pilot never goes on from it - it waits for the press, the screen to settle, the retry time)
      // (a block of the press - a pop-up over the screen, the screen held - leaves the pilot its own reason: it clicks nothing past the gate)
      if (!gate.CityBackDue(owner, scr, now)) { AnnounceHalts(); return !gate.Halted(owner) && ClickGate.CityBackBlock(scr) == ""; }
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      SendInside(new AutoAction { Kind = AutoKind.Click, X = hg2.BackX * U, Y = hg2.BackY * U }, o, cr, owner, ClickTarget.CityBack, front, (rq, fr) => { rq.U = U; });
      AnnounceHalts();
      return true;
    }

    /// <summary>The screen map's exit of the form on top (ClickGate.MapExit), shared by every pilot: a dangerous form stops the pilot, a fight /
    /// loading waits (the tick is taken), a pop-up is closed with its × (MapClose, through the gate) or with one Escape (never on the city).
    /// A back arrow and the rest: false, the older steps decide (UnknownBack / CityBack).</summary>
    bool MapStep(ClickOwner owner, ScreenState scr, W32.POINT o, W32.RECT cr, bool front, long now) {
      if (scr == null || !front) return false;
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      var d = ClickGate.MapExit(scr, W, H, U);
      if (d.Move == MapMove.None || d.Move == MapMove.Back) return false;
      if (d.Move == MapMove.Wait || gate.Halted(owner) || sending) return true;
      if (gate.EscapeDue(owner, scr, now)) { EscapeSend(owner, cr, front); AnnounceHalts(); return true; }
      if (!gate.MapDue(owner, scr, now)) { AnnounceHalts(); return true; }
      if (d.Move == MapMove.Stop) { gate.MapStop(owner, d, scr); AnnounceHalts(); return true; }
      if (d.Move == MapMove.Escape) { gate.MapEscape(owner); EscapeSend(owner, cr, front); AnnounceHalts(); return true; }
      string form = d.Form;
      SendInside(new AutoAction { Kind = AutoKind.Click, X = d.Point.X, Y = d.Point.Y }, o, cr, owner, ClickTarget.MapClose, front, (rq, fr) => { rq.Form = form; rq.W = W; rq.H = H; rq.U = U; });
      AnnounceHalts();
      return true;
    }

    /// <summary>The window table (the offers, the shown forms) was not read this tick or for the fresh state: a dialog over the screen is not ruled out.</summary>
    /// A known screen the tick took for another one (its read failed then): the forms over it were not read as its pop-ups either.
    bool FormsUnread(ScreenState fresh) {
      return RFX.UiKnown && (formsUnread || fresh == null || !fresh.OfferRead || (fresh.Kind != ScreenKind.City && fresh.Kind != ScreenKind.Unknown && fresh.Kind != formsKind)
                             || TickBehind(fresh, screens.Current));
    }

    /// <summary>The fresh read shows a pop-up the tick's screen does not have yet (it came a moment ago; the tracker takes it on the next ticks): the
    /// pilots' step was made without it - no click now, the pilots close it or wait for it first (a gate check now would stop the run on it).</summary>
    internal static bool TickBehind(ScreenState fresh, ScreenState tick) { return fresh != null && fresh.ModalOpen && (tick == null || !tick.ModalOpen || tick.Held); }

    /// <summary>The one Escape key press (EscapeBack): the gate judges a fresh read (never the city - it asks to quit the game there -, a fight, a
    /// pop-up, the exit dialog); the key goes only to the game in front.</summary>
    bool EscapeSend(ClickOwner owner, W32.RECT cr, bool front) {
      long now = NowMs();
      var fresh = FreshScreen(false, true);
      if (FormsUnread(fresh)) return false;
      var req = new ClickRequest { Owner = owner, Target = ClickTarget.EscapeBack, AutoOn = owner == ClickOwner.Locate ? LocateAutoOn : AutoEnabled, Front = front, U = Ui.Unit(cr.R, cr.B) };
      var res = gate.Check(req, fresh, now);
      AnnounceHalts();
      if (!res.Allowed) return false;
      gate.Arm(owner, ClickTarget.EscapeBack, ClickGate.Expect(req, fresh), now);
      SendEscapeKey(owner);
      return true;
    }

    /// <summary>EquipTipEscape: the item's tip over the slots of the next step, closed with one Escape (the gate: only the tip shown, at most 2 a run).</summary>
    bool TipEscapeSend(W32.RECT cr, bool front) {
      long now = NowMs();
      var fresh = FreshScreen(false, true);
      if (FormsUnread(fresh)) return false;
      var req = new ClickRequest { Owner = ClickOwner.Equip, Target = ClickTarget.EquipTipEscape, AutoOn = AutoEnabled, Front = front, U = Ui.Unit(cr.R, cr.B) };
      var res = gate.Check(req, fresh, now);
      AnnounceHalts();
      if (!res.Allowed) return false;
      gate.Arm(ClickOwner.Equip, ClickTarget.EquipTipEscape, ClickGate.Expect(req, fresh), now);
      SendEscapeKey(ClickOwner.Equip);
      return true;
    }

    /// <summary>VK_ESCAPE down and up, off the UI thread, only while the game's window is in front (else no key, the owner stops: "covered").</summary>
    void SendEscapeKey(ClickOwner owner) {   // ONLY called by EscapeSend, TipEscapeSend and TickExplore (each after the gate's yes)
      sending = true; userIdle.OwnStart((uint)Environment.TickCount);
      IntPtr game = GameWindow();
      var t = new System.Threading.Thread(() => {
        try {
          if (game == IntPtr.Zero || !IsForeground(game)) { covered = Tuple.Create(owner, "another window in front of the game (no Escape sent)"); return; }
          int sz = Marshal.SizeOf(typeof(W32.KINPUT));
          W32.SendKeyInput(1, new[] { new W32.KINPUT { type = 1, ki = new W32.KEYBDINPUT { vk = 0x1B, scan = 0x01 } } }, sz);
          System.Threading.Thread.Sleep(60);
          W32.SendKeyInput(1, new[] { new W32.KINPUT { type = 1, ki = new W32.KEYBDINPUT { vk = 0x1B, scan = 0x01, dwFlags = W32.KEYUP } } }, sz);
          System.Threading.Thread.Sleep(30);
        } catch (Exception e) { Log.Write("auto: " + e.Message); }
        finally { userIdle.OwnEnd((uint)Environment.TickCount); sending = false; }
      });
      t.IsBackground = true; t.Start();
    }

    /// <summary>THE way a click leaves the app: the gate says yes (the target is on the whitelist of the screen the game shows right now,
    /// no pop-up, the cell is the item's, the last click showed its effect), then it is sent and its effect is awaited.</summary>
    bool GatedSend(ClickRequest req, ScreenState fresh, AutoAction a, int sx, int sy) {
      long now = NowMs();
      if (FormsUnread(fresh)) return false;
      var res = gate.Check(req, fresh, now);
      AnnounceHalts();
      if (!res.Allowed) return false;
      // a game window larger than the screen (not maximized, live 2026-10-29): a point off the desktop is never sent (the cursor would land on the edge)
      var vs = SystemInformation.VirtualScreen;
      int sy2 = sy + (a.Kind == AutoKind.Drag ? (int)Math.Round(a.DY) : 0);
      if (!OwnButtons.AllInside(new[] { vs.X, vs.Y, vs.Width, vs.Height }, sx, sy, sy2)) {
        gate.Halt(req.Owner, "offscreen", req.Target + " @" + (int)req.X + "," + (int)req.Y + " lies off the screen (the game window is larger than the screen)"); AnnounceHalts(); return false;
      }
      gate.Arm(req.Owner, req.Target, ClickGate.Expect(req, fresh), now, req.Form);
      // measurement only: the pilot's click that opens the Inventory / Heroes screen, timed to the first reading of that screen (ReadScreens)
      if (req.Target == ClickTarget.CityInventory) { openClickAt = now; openClickWhat = "inventory"; }
      else if (req.Target == ClickTarget.FoundryButton) { openClickAt = now; openClickWhat = "foundry"; foundryBuiltPid = GamePid(GameWindow()); }
      else if (req.Target == ClickTarget.EquipNav && fresh != null && fresh.Kind == ScreenKind.City) { openClickAt = now; openClickWhat = "heroes"; }
      RFX.UiButtonClicked(req.Target, req.Form, req.X, req.Y, req.W, req.H, req.U);   // (the test build reads it again a minute on: uibtn ... [minute])
      Send(a, sx, sy, req.Owner, req.Target, req.Form, freshOffers, unknownForms);
      // the form this click opens was never found: its search starts with the click (a slow PC needs every second of it)
      if ((req.Target == ClickTarget.CityInventory && RFX.InvFormMissing()) || (req.Target == ClickTarget.FoundryButton && RFX.FoundryFormMissing())) RFX.RequestFormSearch("opened by the pilot's click", 0);
      return true;
    }
    long openClickAt; string openClickWhat = "";
    long shownDiagAt, shownDiagSaidAt; string shownDiagSaid = "";

    /// <summary>An equip helper click (screen px). Target: what it presses (city / hero / gear list = EquipNav, the filter pop-up, its ×, «Заменить»).</summary>
    bool EquipSend(ClickTarget t, AutoAction a, int sx, int sy, W32.POINT o, bool front) {
      if (t == ClickTarget.EquipConfirm && !AutoConfirm) { gate.Halt(ClickOwner.Equip, "target", "«Заменить» without the player's auto confirm"); AnnounceHalts(); return false; }
      var fresh = FreshScreen(t == ClickTarget.EquipNav);
      var req = new ClickRequest { Owner = ClickOwner.Equip, Target = t, AutoOn = AutoEnabled, Front = front, X = sx - o.X, Y = sy - o.Y };
      W32.RECT cr; IntPtr hwnd = GameWindow();
      if (hwnd != IntPtr.Zero && W32.GetClientRect(hwnd, out cr)) {   // (the client size: the live read needs it - live 2026-10-30 "uibtn ... failed at window (no size)")
        req.W = cr.R; req.H = cr.B; req.U = Ui.Unit(cr.R, cr.B);
        if (a.Kind == AutoKind.Click) LiveClick(req, fresh, o, ref sx, ref sy);
        if (a.Kind == AutoKind.Click && a.Cell > 0 && t == ClickTarget.EquipNav) LiveCellClick(req, fresh, a.Cell, o, ref sx, ref sy); else { cellLivePt = null; cellRefused = false; }
      } else { cellLivePt = null; cellRefused = false; }
      if (cellRefused) { cellRefused = false; Log.Write("auto: the item's live cell is not in view now - no click on the layout's point"); return false; }
      if (!GatedSend(req, fresh, a, sx, sy)) return false;
      equipClickAt = NowMs(); return true;
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

    string artStepSaid = "";

    // ---- the artifact list's rows from the picture: the fallback of the exact scroll (src/ArtGridPicture.cs)
    readonly ArtGridPicture artPic = new ArtGridPicture();
    long artPicAt; double artPicRow1 = double.NaN; string artPicSaid = "";
    HashSet<long> artPicSel; int artPicArtSel, artPicClickRow = -1; double artPicCursorY; long artPicClickAt;
    SellGeometry artGeo; double artGeoW, artGeoH;
    SellGeometry ArtGeo(double W, double H, double U) {
      if (artGeo == null || artGeoW != W || artGeoH != H) {
        var rc = Geo.Rect("inventory.artList", W, H, U);
        artGeo = SellGeometry.Artifacts(W, H, U, SellGeometry.ArtCols(rc, U, 8), rc); artGeoW = W; artGeoH = H;
      }
      return artGeo;
    }
    double ArtPicRow1(SellGeometry g, InventoryScreen inv, W32.POINT o, W32.RECT cr, double U, long now) {
      if (inv == null || g == null) return double.NaN;
      // the player's own click tells where the list is when the picture lost it: a card ticked (the cleanup) or chosen (the list's open card) under the pointer
      int cr0 = -1;
      long one = artPicSel != null ? SellHint.OneChange(artPicSel, inv.Selected) : 0;
      if (one != 0) { int ix = inv.Uids.IndexOf(one); if (ix >= 0) cr0 = ix / g.Columns; }
      else if (inv.ArtSelKnown && inv.ArtSel > 0 && inv.ArtSel != artPicArtSel && artPicArtSel != 0) cr0 = (inv.ArtSel - 1) / g.Columns;
      artPicSel = new HashSet<long>(inv.Selected); artPicArtSel = inv.ArtSelKnown ? inv.ArtSel : 0;
      W32.POINT cp;
      if (cr0 >= 0 && W32.GetCursorPos(out cp)) { artPicClickRow = cr0; artPicCursorY = cp.Y - o.Y; artPicClickAt = now; }
      if (artPicAt != 0 && now >= artPicAt && now - artPicAt < 50) return artPicRow1;   // the picture look at most every 50 ms
      artPicAt = now;
      int clickRow = artPicClickRow >= 0 && now - artPicClickAt < 400 ? artPicClickRow : -1; artPicClickRow = -1;
      int sx, sy, sw, sh; artPicRow1 = double.NaN;
      if (!ArtGridPicture.Strip(g, cr.R, cr.B, U, out sx, out sy, out sw, out sh)) return double.NaN;
      int[] px = Grab(o.X + sx, o.Y + sy, sw, sh);
      if (px == null) return double.NaN;
      MaskOwn(px, o.X + sx, o.Y + sy, sw, sh);
      double r; int rows = (inv.Uids.Count + g.Columns - 1) / g.Columns;
      if (artPic.Update(px, sw, sh, sx, sy, g, cr.R, U, rows, inv.ListPtr, now, out r, clickRow, artPicCursorY)) artPicRow1 = r;
      string st = double.IsNaN(artPicRow1) ? "unsure" : "ok";
      if (st != artPicSaid) { artPicSaid = st; Log.Write("art list: rows from the picture (" + st + "): " + artPic.Note); }
      return artPicRow1;
    }
    /// <summary>The artifact list's rows from the game's own scroll alone (no picture: the stat bars are the gear list's). True = placed.</summary>
    bool FollowArtifactGrid(LocateGrid grid, SellGeometry geo, InventoryScreen inv, double U, long now) {
      if (inv == null || inv.List == null || !inv.List.Has) {
        if (gridSaid != "art:none") { gridSaid = "art:none"; Log.Write("art sell hint: the artifact list's scroll cannot be read (or the layout data has no grid fields)"); }
        return false;
      }
      double r1 = sellScroll.Row1(geo, inv.List, double.NaN, U, now);
      if (double.IsNaN(r1)) return false;
      if (recB != null) { recB.InvRaw = inv.List.ScrollY; recB.InvRow1 = r1; }
      grid.FromScroll(r1, inv.ListPtr);
      if (gridSaid != "art:scroll") { gridSaid = "art:scroll"; Log.Write("art sell hint: the artifact grid from the game's scroll (" + inv.List + ")"); }
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
      gate.Flows[ClickOwner.Locate] = CityFirst.OfLocate(req.Purpose);   // (its work screen - the Foundry, the gem screen - never an Escape there)
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
        if (pre.Reason == "offer" && OfferStep(ClickOwner.Locate, scr, o, cr, gameFront, now)) { step.Auto = "work"; step.AutoWhy = ""; locateDriving = true; return; }
        if (pre.Reason == "unknown" && UnknownBackStep(ClickOwner.Locate, scr, o, cr, gameFront, now)) { step.Auto = "work"; step.AutoWhy = ""; locateDriving = true; return; }
        if (pre.Verdict == GateVerdict.Wait) {
          if (pre.Reason == "pending") { step.Auto = "work"; step.AutoWhy = ""; locateDriving = true; }   // the last click's effect is awaited
          else { step.Auto = "hint"; step.AutoWhy = "no_city"; }                                          // the screen is not recognised yet: hints only
          return;
        }
      }
      bool heroOpen = scr != null && scr.Kind == ScreenKind.HeroScreen;
      bool formOpen = scr != null && (scr.Kind == ScreenKind.Foundry || scr.Kind == ScreenKind.Gem);
      var dstep = LocateGuide.Next(req, inv, locateRs, heroScreen, locateCols, locateOnAccount);
      // (a pop-up over the target: BackStep above closes it by the screen map's × / Escape through the gate; the «close the window» step is the hint mode's only)
      var view = new LocateView { NowMs = now, Foreground = gameFront, UserBusy = busy, UserClicked = uc, W = W, H = H, U = U, Step = dstep, Inv = inv, Cols = locateCols,
        HeroScreenOpen = heroOpen, OtherFormOpen = formOpen, InvDX = invDX, Geometry = Geo.Current, Rs = locateRs, Item = req.Item, Purpose = req.Purpose };
      if (gameFront && !busy && LocateAuto.NeedsCityButton(dstep, heroOpen, formOpen)) { view.CityButton = InventoryButtonSeen(o, W, H); view.InvDX = invDX; }   // the place the bar search may have found just now
      if (gameFront && !busy && dstep.Kind == LocateKind.InList) {
        var pg = SellGeometry.Inventory(W, U, locateCols);
        view.Row1 = locateScroll.Row1(pg, inv != null ? inv.List : null, double.NaN, U, now);   // the game's scroll (the constant is learned by the frame)
        if (double.IsNaN(view.Row1)) view.Phase = PhaseCached(pg, o, cr, U, now);                    // last resort
      }
      if (RFX.InvFilterWanted && !gate.Halted(ClickOwner.LocateFilter) && inv != null && inv.Open && inv.EquipTab && !inv.Bulk && (dstep.Kind == LocateKind.InList || dstep.Kind == LocateKind.Selected))
        view.Filter = FilterView(inv, dstep, req, gameFront, busy, now, W, H, U);
      // city first: a screen the purpose's flow does not go on from (the workshop, the gem screen for an Ascension, the Foundry for a recast), or
      // the step is the inventory's while the Foundry / the gem screen is on top: back with its arrow first
      bool needBack = CityFirst.NeedsCity(CityFirst.OfLocate(req.Purpose), scr, dstep.Kind, LocateAuto.NeedsCityBack(view));
      if (gameFront && !busy && BackStep(ClickOwner.Locate, scr, needBack, o, cr, gameFront, now)) { step.Auto = "work"; step.AutoWhy = ""; locateDriving = true; return; }
      if (gate.CellAgainDue.Remove(ClickOwner.Locate)) lpilot.CellAgain();   // (ClickGate.Observe: a cell click without effect, placed again once)
      // a list step on a list that still moves: the next read (the click point from a settled one)
      if (gameFront && !busy && ((dstep.Kind == LocateKind.InList && !invSettle.Settled) || (dstep.Kind == LocateKind.AscendPick && !rsSettle.Settled))) { step.Auto = "work"; step.AutoWhy = ""; locateDriving = true; return; }
      var mv = lpilot.Step(view);
      if (lpilot.Trace != "") { Log.Write("locate auto: " + lpilot.Trace); lpilot.Trace = ""; }
      locateMoveState = mv.State; locateMoveWhy = mv.Why;
      string said = mv.State + "|" + mv.Why + "|" + dstep.Key;
      if (said != locateAutoSaid) { locateAutoSaid = said; Log.Write("locate auto: " + mv.State + (mv.Why != "" ? " (" + mv.Why + ")" : "") + " at step " + dstep.Key); }
      // started from our own window: the game comes forward (and is restored), never over another program
      if (mv.State == "background") RaiseGame(hwnd, true, now, true);
      if (mv.Action.Kind != AutoKind.None) SendLocate(mv, req, o, cr, gameFront, now, W, H, U);
      if (lpilot.Stopped) { step.Auto = "stopped"; step.AutoWhy = lpilot.Reason; locateDriving = false; return; }
      step.Auto = mv.State; step.AutoWhy = mv.Why;
      locateDriving = mv.State == "work" || mv.State == "paused" || mv.State == "background";
    }

    /// <summary>The inventory's filter for a long search (src/LocatePilot.cs InvFilterPilot): what the game shows now. Wanted = the item is in the list but off the first
    /// screen of cells (the game's own cell indices when read, else its row).</summary>
    InvFilterView FilterView(InventoryScreen inv, LocateStep dstep, LocateRequest req, bool gameFront, bool busy, long now, double W, double H, double U) {
      RFX.InvFilterW = W; RFX.InvFilterH = H; RFX.InvFilterU = U;
      var fv = new InvFilterView { NowMs = now, Foreground = gameFront, UserBusy = busy, W = W, H = H, U = U, Item = req.Item, F = inv.Filter };
      var l = inv.List; int idx = dstep.Pos - 1;
      bool shown = idx >= 0 && l != null && l.First >= 0 && l.Last >= l.First && idx >= l.First && idx <= l.Last;
      fv.Wanted = dstep.Kind == LocateKind.InList && idx >= 0 && !shown && dstep.Row > InvFilterPilot.MinRow;
      var f = inv.Filter;
      if (f != null && f.Known && f.SuitOpen && req.Item != null && req.Item.SetId > 0) {
        var cs = RFX.UiCellFresh(UiCellKind.InvSuit, req.Item.SetId, W, H, Environment.TickCount, RFX.UiFreshMs);
        RFX.UiCellWant(UiCellKind.InvSuit, new[] { req.Item.SetId }, W, H, U);
        double[] fr; string why; var st = RFX.UiCellState(cs, req.Item.SetId, W, H, Environment.TickCount, out fr, out why, RFX.UiFreshMs);
        if (cs != null && cs.Ok) { fv.GridView = new[] { cs.VX, cs.VY, cs.VW, cs.VH }; fv.CellFirst = cs.First; fv.CellLast = cs.Last; }
        fv.SuitCell = st == UiCellSee.Shown && fr != null ? 1 : cs != null && cs.Ok ? 2 : 0;
        if (fv.SuitCell == 1) fv.CellRect = fr;
        var wr = RFX.UiKeyRead("grid", W, H, U);
        if (wr != null && wr.Ok && wr.Visible && fv.GridView == null) fv.GridView = new[] { wr.X, wr.Y, wr.W, wr.H };
      }
      return fv;
    }

    /// <summary>A filter button's click point: the live rect of its widget (read now) when it is visible and inside the client, else the prefab's layout point the pilot gave.</summary>
    void FilterLivePoint(LocateMove mv, double W, double H, double U) {
      var r = RFX.UiKeyRead(mv.FilterKey, W, H, U); double x, y; string why;
      bool live = RFX.UiLivePoint(r, W, H, Environment.TickCount, out x, out y, out why);
      if (live) { mv.Action.X = x; mv.Action.Y = y; }
      Log.Write("uibtn: filter " + mv.FilterKey + (r != null ? " " + (r.Ok ? "[" + (int)r.X + "," + (int)r.Y + " " + (int)r.W + "x" + (int)r.H + (r.Visible ? "" : " hidden") + "]" : "failed at " + r.FailStep) : " no read") + (live ? " [click: live @" + (int)x + "," + (int)y + "]" : " [click: layout, " + why + "]"));
    }

    const double InvBtnDX = CityBar.DefaultDX;   // «Инвентарь»: 0.9316 left of «Герои» in the city's bottom bar
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

    // ---- the artifact storage cleanup's clicks (only with ArtAutoOn; the frames and plates of TickSellHint go on). The pilot opens the «Артефакт» tab and
    // «Массовая продажа», then SellPilot (with the artifact list's geometry) ticks the wanted cards, scrolling the list by dragging it, and takes a wrong tick off.
    // Never «Продать», «Все» (select all), a lock icon or anything else; every cell click is judged by the gate on a fresh read (the card under the click is the
    // one meant, and the game's choice must hold it within 1 s).
    long artSellBtnAt; string artSellSaid = "";

    void TickArtSellAuto() {
      if (!sellArt || sellWanted == null || !ArtAutoOn || sending || runHero > 0) return;
      long now = NowMs();
      W32.POINT c;
      if (W32.GetCursorPos(out c)) {
        if (seenSet && (c.X != seen.X || c.Y != seen.Y)) lastMoveMs = now;
        seen = c; seenSet = true;
      }
      bool down = (W32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (W32.GetAsyncKeyState(0x02) & 0x8000) != 0;
      if (down && !btnWasDown) userClick = true;
      btnWasDown = down;
      IntPtr hwnd = GameWindow();
      int fp, gp;
      if (hwnd == IntPtr.Zero) return;
      W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp); W32.GetWindowThreadProcessId(hwnd, out gp);
      // started from our own window: the game comes forward (and is restored), never over another program
      if (RaiseGame(hwnd, true, now, true)) return;
      if (W32.IsIconic(hwnd)) return;
      W32.RECT cr; if (!W32.GetClientRect(hwnd, out cr) || cr.B < 200) return;
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      double W = cr.R, H = cr.B, U = Ui.Unit(W, H);
      bool front = fp == gp, busy = down || now - lastMoveMs < 700, uc = userClick; userClick = false;
      if (gate.Halted(ClickOwner.Sell)) return;
      var scr = screens.Current;
      if (front && !busy) {
        var pre = gate.Preflight(ClickOwner.Sell, scr, now);
        AnnounceHalts();
        if (pre.Reason == "offer" && OfferStep(ClickOwner.Sell, scr, o, cr, front, now)) return;
        if (pre.Reason == "unknown" && UnknownBackStep(ClickOwner.Sell, scr, o, cr, front, now)) return;
        if (pre.Verdict != GateVerdict.Allow) return;
        // city first: the Foundry, the gem screen, the workshop ... back with the arrow top left
        bool eqBulk = scr != null && scr.Kind == ScreenKind.Inventory && scr.InvEquipTab && scr.InvBulkKnown && scr.InvBulk;   // the equipment sale: never clicked in
        if (BackStep(ClickOwner.Sell, scr, CityFirst.NeedsCity(PilotFlow.ArtSell, scr, LocateKind.None, eqBulk), o, cr, front, now)) return;
        // then the normal way to the inventory: the hero screen's back arrow, the city's «Инвентарь»
        if (scr != null && scr.Kind == ScreenKind.HeroScreen) {
          if (now - artSellBtnAt < 2000) return;
          artSellBtnAt = now;
          SendInside(new AutoAction { Kind = AutoKind.Click, X = hg2.BackX * U, Y = hg2.BackY * U }, o, cr, ClickOwner.Sell, ClickTarget.HeroBack, front);
          return;
        }
        if (scr != null && scr.Kind == ScreenKind.City && InventoryButtonSeen(o, W, H)) {
          if (now - artSellBtnAt < 2500) return;
          artSellBtnAt = now;
          SendInside(new AutoAction { Kind = AutoKind.Click, X = W - invDX * U, Y = H - hg2.HeroesBtnDY * U }, o, cr, ClickOwner.Sell, ClickTarget.CityInventory, front);
          return;
        }
      }
      if (scr == null || scr.Kind != ScreenKind.Inventory || screens.Inv == null) return;   // not the city's bar (a fight, a covered city): the hint says so
      var inv = screens.Inv;
      if (!front || busy) return;
      if (!inv.ArtTab) {
        if (inv.EquipTab && (!inv.BulkKnown || inv.Bulk)) return;   // the equipment sale is left by the player
        if (now - artSellBtnAt < 2000) return;
        artSellBtnAt = now;
        var tr = ArtFrames.Rect(Geo.Current, "inventory.leftTab.artifact", W, H, U);
        if (tr != null) SendInside(new AutoAction { Kind = AutoKind.Click, X = tr.CX, Y = tr.CY }, o, cr, ClickOwner.Sell, ClickTarget.ArtTab, front);
        return;
      }
      if (!inv.ArtBulkKnown) return;
      if (!inv.ArtBulk) {
        if (inv.ArtMode != -1) return;   // «Масс. организовать»: the player leaves it
        if (now - artSellBtnAt < 2000) return;
        artSellBtnAt = now;
        var pb = SellHint.ArtBulkRect(W, H, U);
        SendInside(new AutoAction { Kind = AutoKind.Click, X = pb[0] + pb[2] / 2.0, Y = pb[1] + pb[3] / 2.0 }, o, cr, ClickOwner.Sell, ClickTarget.ArtBulkEnter, front);
        return;
      }
      // «Массовая продажа» of the artifact tab: the geometry and the columns are the hint's (TickSellHint keeps them); no cell without the game's exact scroll
      var geo = shGeo; int cols = shCols;
      if (geo == null || shGeoW != W || shGeoH != H || shGeoCols != cols || inv.List == null || !inv.List.Has) return;
      spilot.Geometry = geo;
      double row1 = sellScroll.Row1(geo, inv.List, double.NaN, U, now);
      if (double.IsNaN(row1)) return;
      var a = spilot.Step(new SellView { NowMs = now, Foreground = front, UserBusy = busy, UserClicked = uc, W = W, H = H, U = U, Screen = inv.AsSellArt(cols), Wanted = sellWanted, Phase = double.NaN, Row1 = row1 });
      if (spilot.Trace != "") { Log.Write("art sell auto: " + spilot.Trace); spilot.Trace = ""; }
      string key = spilot.State + "|" + spilot.Selected + "|" + spilot.Missing + "|" + spilot.Extra;
      if (key != artSellSaid) { artSellSaid = key; Log.Write("art sell auto: " + spilot.State + " selected " + spilot.Selected + "/" + sellWanted.Count + " missing " + spilot.Missing + " extra " + spilot.Extra); }
      if (a.Kind == AutoKind.None) return;
      var wanted = sellWanted;
      SendInside(a, o, cr, ClickOwner.Sell, a.Kind == AutoKind.Drag ? ClickTarget.ArtListDrag : ClickTarget.ArtBulkCell, front, (rq, fr) => {
        if (a.Kind == AutoKind.Drag) return;
        rq.CellUid = ClickGate.InvCellUid(fr.Inv, geo, sellScroll.Row1(geo, fr.Inv != null ? fr.Inv.List : null, double.NaN, U, now), cols, rq.X, rq.Y, W, U);
        rq.CellOk = wanted;
        rq.Untick = rq.CellUid > 0 && !wanted.Contains(rq.CellUid);   // a ticked card that is not wanted comes off; the gate checks both ways
      });
    }

    // a pilot action at client px, never outside the game's client area
    // the click gate decides (src/ClickGate.cs): <paramref name="fill"/> adds what only the fresh read can say (the item under a cell click)
    bool SendInside(AutoAction a, W32.POINT o, W32.RECT cr, ClickOwner owner, ClickTarget target, bool front, Action<ClickRequest, ScreenState> fill = null) {
      int x = o.X + (int)Math.Round(a.X), y = o.Y + (int)Math.Round(a.Y), y2 = y + (int)Math.Round(a.DY);
      if (x < o.X || y < o.Y || x >= o.X + cr.R || y >= o.Y + cr.B || y2 < o.Y || y2 >= o.Y + cr.B) return false;
      bool artT = ClickGate.IsArtTarget(target);
      // an artifact flow never presses «Продвинуть» / «Улучшить»: on the workshop no click or drag point may lie inside those buttons
      if (ClickGate.IsWorkshopTarget(target)) {
        double aw = cr.R, ah = cr.B, au = Ui.Unit(aw, ah);
        if (ClickGate.OnNeverButton(Geo.Current, a.X, a.Y, aw, ah, au) || (a.Kind == AutoKind.Drag && ClickGate.OnNeverButton(Geo.Current, a.X, a.Y + a.DY, aw, ah, au))) {
          gate.Halt(owner, "target", target + " @" + (int)a.X + "," + (int)a.Y + " lies on a button the artifact flows never press"); AnnounceHalts(); return false;
        }
      }
      // the artifact steps of the hero screen never press the card's sell icon or «Быстро надеть»
      if (ClickGate.IsHeroArtTarget(target)) {
        double aw = cr.R, ah = cr.B, au = Ui.Unit(aw, ah);
        if (ArtEquipFrames.OnNeverButton(Geo.Current, a.X, a.Y, aw, ah, au) || (a.Kind == AutoKind.Drag && ArtEquipFrames.OnNeverButton(Geo.Current, a.X, a.Y + a.DY, aw, ah, au))) {
          gate.Halt(owner, "target", target + " @" + (int)a.X + "," + (int)a.Y + " lies on a button the artifact steps never press"); AnnounceHalts(); return false;
        }
      }
      var fresh = FreshScreen(target == ClickTarget.CityInventory, target == ClickTarget.UnknownBack || target == ClickTarget.CityBack || target == ClickTarget.FoundryButton);   // (FoundryButton: the Foundry's form shown unread, ClickGate)
      var req = new ClickRequest { Owner = owner, Target = target, AutoOn = artT ? ArtAutoOn : owner == ClickOwner.Locate || owner == ClickOwner.LocateFilter ? LocateAutoOn : AutoEnabled, Front = front, X = a.X, Y = a.Y };
      if (fill != null) fill(req, fresh);
      if (req.U <= 0) { req.W = cr.R; req.H = cr.B; req.U = Ui.Unit(cr.R, cr.B); }
      req.Shown = unknownForms;   // (every unknown form shown, a screen's own ones too: ClickGate)
      if (a.Kind == AutoKind.Click) LiveClick(req, fresh, o, ref x, ref y);
      if (a.Kind == AutoKind.Click && a.Cell > 0) LiveCellClick(req, fresh, a.Cell, o, ref x, ref y); else cellLivePt = null;
      return GatedSend(req, fresh, a, x, y);
    }

    /// <summary>A pilot button's click (HeroBack, OfferClose, EquipConfirm, CityInventory, FoundryButton, AscendTab): the centre of its live rect
    /// (src/UiButtons.cs) when the read is fresh (at most 200 ms; else one read now), visible, inside the client and on the form on top of the
    /// fresh screen - the click lands on the real button at any window size; otherwise the geometry's point. One log line per click.</summary>
    void LiveClick(ClickRequest req, ScreenState fresh, W32.POINT o, ref int sx, ref int sy) {
      if (RFX.UiWidgetOf(req.Target, req.Form) == null || req.W <= 0 || req.H <= 0) return;
      var r = RFX.UiButtonFresh(req.Target, req.Form, req.W, req.H, Environment.TickCount, RFX.UiFreshMs) ?? RFX.UiButtonReadNow(req.Target, req.Form, req.W, req.H, req.U);
      double lx = req.X, ly = req.Y, x, y; string why;
      bool live = RFX.UiLivePoint(r, req.W, req.H, Environment.TickCount, out x, out y, out why);
      if (live && !RFX.UiTopIs(req.Target, req.Form, fresh)) { live = false; why = "the form on top is " + (fresh != null ? fresh.Top + " (" + fresh.Brief + ")" : "not read"); }
      if (live) { req.X = x; req.Y = y; req.Live = true; sx = o.X + (int)Math.Round(x); sy = o.Y + (int)Math.Round(y); }
      if (r != null) Log.Write(RFX.UiButtonLine(r, lx, ly) + (live ? " [click: live @" + (int)Math.Round(x) + "," + (int)Math.Round(y) + "]" : " [click: layout, " + why + "]"));
      else Log.Write("uibtn: " + req.Target + " [click: layout, no read]");
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
        case LocateTodo.FoundryButton:
          tg = ClickTarget.FoundryButton;
          // the Foundry built before in this game but not readable now: it may be open over the inventory unseen (the gate waits; a search is asked for)
          fill = (rq, fr) => {
            rq.FoundryUnread = foundryBuiltPid != 0 && foundryBuiltPid == GamePid(GameWindow()) && (fr.Rs == null || RFX.FoundryFormMissing());
            if (rq.FoundryUnread) RFX.RequestFormSearch("foundry not readable before «Литейная»", 2000);
          };
          break;
        case LocateTodo.AscendTab: tg = ClickTarget.AscendTab; break;
        case LocateTodo.SlotTab: tg = ClickTarget.SlotTab; break;
        case LocateTodo.CardGem: case LocateTodo.GemFunc: {
          bool gemBtn = mv.Todo == LocateTodo.CardGem;
          tg = gemBtn ? ClickTarget.InvCardGem : ClickTarget.GemFuncRow;
          fill = (rq, fr) => { rq.CellOk = new[] { req.Item.Uid }; rq.Func = GemRows.FuncOfQuality(req.Item.Quality); };   // the screen's selected item has to be this one
          break;
        }
        case LocateTodo.InvFilter: case LocateTodo.InvFilterCell: case LocateTodo.InvFilterDrag: case LocateTodo.InvFilterClose: {
          // the inventory's filter (owner LocateFilter: its own gate stop): a toggle of the panel, a set of the pop-up, a drag on its list, the click outside
          if (mv.Todo == LocateTodo.InvFilter) FilterLivePoint(mv, W, H, U);
          var ftg = mv.Todo == LocateTodo.InvFilter ? ClickTarget.InvFilterBtn : mv.Todo == LocateTodo.InvFilterCell ? ClickTarget.InvFilterCell : mv.Todo == LocateTodo.InvFilterDrag ? ClickTarget.InvFilterDrag : ClickTarget.InvFilterClose;
          long fset = req.Item != null ? req.Item.SetId : 0; string fkey = mv.FilterKey; int farg = (int)mv.FilterArg; int ffn = mv.Todo == LocateTodo.InvFilterClose ? farg : 0;
          bool fsent = SendInside(mv.Action, o, cr, ClickOwner.LocateFilter, ftg, front, (rq, fr) => {
            rq.Form = fkey; rq.W = W; rq.H = H; rq.U = U; rq.Func = ffn;
            if (ftg == ClickTarget.InvFilterCell) rq.CellOk = new[] { fset };   // (the set under the click: LiveCellClick names it from the live cell; none = the gate stops the filter)
          });
          if (!fsent) lpilot.FilterNotSent(); else lpilot.Sent();
          return;
        }
        case LocateTodo.PickItem:
          tg = drag ? ClickTarget.InvListDrag : ClickTarget.InvListCell;
          fill = (rq, fr) => {
            var g2 = SellGeometry.Inventory(W, U, locateCols);
            rq.CellUid = ClickGate.InvCellUid(fr.Inv, g2, locateScroll.Row1(g2, fr.Inv != null ? fr.Inv.List : null, double.NaN, U, now), locateCols, rq.X, rq.Y, W, U);
            rq.CellOk = new[] { req.Item.Uid };
            rq.NoScroll = fr.Inv == null || fr.Inv.List == null || !fr.Inv.List.Has;
          };
          break;
        case LocateTodo.AscendCell:
          tg = drag ? ClickTarget.AscendDrag : ClickTarget.AscendCell;
          fill = (rq, fr) => { rq.CellUid = ClickGate.AscendCellUid(Geo.Current, fr.Rs, rq.X, rq.Y, W, H, U); rq.CellOk = new[] { req.Item.Uid }; };
          break;
      }
      if (!drag && (mv.Todo == LocateTodo.PickItem || mv.Todo == LocateTodo.AscendCell) && req.Item != null) mv.Action.Cell = req.Item.Uid;   // (its live cell is clicked when fresh)
      if (SendInside(mv.Action, o, cr, ClickOwner.Locate, tg, front, fill)) lpilot.Sent(); else lpilot.NotSent();   // ClickTarget.None is on nobody's whitelist: stopped
    }

    /// <summary>Moves the cursor and clicks / drags there, off the UI thread (short pauses let the game see the pointer
    /// over the button first). A drag moves in small steps and rests before the release, so the list does not coast.</summary>
    void Send(AutoAction a, int x, int y, ClickOwner owner, ClickTarget target, string form, List<string> offersBefore, List<string> formsBefore) {   // ONLY called by GatedSend and TickExplore (each after the gate's yes)
      var ob = offersBefore != null ? new List<string>(offersBefore) : null; var fb = formsBefore != null ? new List<string>(formsBefore) : null; bool uiKnown = RFX.UiKnown;
      sending = true;
      int dy2 = y + (a.Kind == AutoKind.Drag ? (int)Math.Round(a.DY) : 0);
      // our own buttons over the game (the TEST build's record button lies on the back arrow at every size, live 2026-10-29:
      // Locate's HeroBack click started a recording) let this click through to the game, until it is done
      var through = new List<GlowWindow>();
      foreach (var w in new[] { recWin, cancelWin })
        if (w.Visible && w.IsHandleCreated && OwnButtons.Covers(w.Parts, x, y, dy2)) { PassThrough(w, true); through.Add(w); }
      IntPtr game = GameWindow(); int gamePid = 0;
      if (game != IntPtr.Zero) W32.GetWindowThreadProcessId(game, out gamePid);
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
      userIdle.OwnStart((uint)Environment.TickCount);
      var t = new System.Threading.Thread(() => {
        try {
          if (hidden) System.Threading.Thread.Sleep(120);
          W32.SetCursorPos(x, y);
          System.Threading.Thread.Sleep(60);
          // the press goes only to the game: another window at the point (a foreign one, or ours that did not step aside) = no press, the pilot stops
          string cov = CoveredBy(x, y, gamePid);
          if (cov != null) { covered = Tuple.Create(owner, cov); return; }
          // the top form checked again right before the press (a pop-up of the game's chain may come while the cursor moves): no press, a retry
          if (uiKnown && a.Kind == AutoKind.Click) {
            var on = RFX.ReadOffers(); var fn = on != null && on.Count == 0 && fb != null ? RFX.ReadUnknownShownForms(4) : null;
            string late = ClickGate.LateTop(target, form, ob, on, fb, fn);
            if (late != null) { lateTop = Tuple.Create(owner, target + ": " + late); return; }
          }
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
          foreach (var w in through) try { PassThrough(w, false); } catch (Exception) { }
          userIdle.OwnEnd((uint)Environment.TickCount);
          sending = false;
        }
      });
      t.IsBackground = true; t.Start();
    }

    // a pilot click that found another window over the game's point (set by the click thread, turned into a halt by Tick)
    volatile Tuple<ClickOwner, string> covered;
    // a pilot click not pressed: a pop-up came on top right before the press (set by the click thread, turned into a retry by Tick)
    volatile Tuple<ClickOwner, string> lateTop;
    List<string> freshOffers;   // the offers of the last fresh read (FreshScreen): the click thread's re-check compares with them

    static void PassThrough(GlowWindow w, bool on) {   // WS_EX_TRANSPARENT on a layered window: the mouse goes to what is under it
      int ex = W32.GetWindowLong(w.Handle, -20);
      W32.SetWindowLong(w.Handle, -20, on ? ex | 0x20 : ex & ~0x20);
    }

    /// <summary>Null when the window at the screen point belongs to the game's process; else what is there (class and process name).</summary>
    static string CoveredBy(int x, int y, int gamePid) {
      if (gamePid == 0) return null;   // the game's window is unknown here: the gate has already checked the screen
      IntPtr h = W32.WindowFromPoint(new W32.POINT(x, y));
      if (h == IntPtr.Zero) return "no window";
      int pid; W32.GetWindowThreadProcessId(h, out pid);
      if (pid == gamePid) return null;
      var cls = new System.Text.StringBuilder(128); W32.GetClassName(h, cls, cls.Capacity);
      string proc = "?";
      try { using (var p = Process.GetProcessById(pid)) proc = p.ProcessName; } catch (Exception) { }
      return "the window «" + cls + "» of " + proc;
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

    /// <summary>The guide's verdicts that mean the item is in the game's list now (it found its row).</summary>
    internal static bool ListedKind(string kind) { return kind == "pick" || kind == "rel" || kind == "filter" || kind == "selected"; }

    /// <summary>What the filter pilot needs this tick: the filter in memory, the set / stat places in the game's lists, the
    /// open panels on the screen (only looked at with the game in front).</summary>
    FilterView FilterViewNow(W32.POINT o, W32.RECT cr, long now, bool front, bool busy) {
      var fv = new FilterView { NowMs = now, Foreground = front, UserBusy = busy, W = cr.R, H = cr.B };
      if (filterItem <= 0 || addrs == null) return fv;
      long[] suits, mains, subAttrs; int part; bool foreign, hideEq;
      if (!RFX.ReadFilter(addrs, out suits, out mains, out subAttrs, out part, out foreign, out hideEq)) return fv;
      fv.Item = filterItem; fv.Suits = suits; fv.MainAttrs = mains; fv.SubAttrs = subAttrs; fv.Foreign = foreign; fv.HideEquipped = hideEq; fv.ItemListed = ListedKind(filterKind);
      // the item is not on the screen: the filter (set + main stat) brings it to the first rows at once. Scrolling a long
      // list back from wherever the last item left it took the pilot up to a minute (live test 2026-09-26)
      bool onScreen = state == "on";
      fv.Wanted = FilterPilot.WantedFor(filterKind, onScreen);
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
      return HeroFilterLook.Seen((x, y, w, h) => Grab(o.X + x, o.Y + y, w, h), Ui.Unit(W, H));
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
    // such as «Demon Soldier» moves it); the place found is kept for the city form it was found on (CityBar: the game builds the city
    // again after the guild / an event screen - then the place is searched again on the new city and the bar has to settle first)
    readonly CityBar cityBar = new CityBar();
    double invDX { get { return cityBar.InvDX; } }
    bool InventoryButtonSeen(W32.POINT o, double W, double H) {
      long now = NowMs();
      // the game's own rect of the button (uibtn), fresh and held still over two reads: pressed at once, the bar's search skipped (its fallback)
      if (CityLiveStill(W, H, now)) return true;
      if (cityLive.Fresh) return false;   // (the rect still moves: the new bar slides in - waited for, never pressed by the picture)
      if (cityBar.Note(RFX.CityFormFresh())) { cityInvAt = 0; Log.Write("city bar: the city was built again - «Инвентарь» is searched again on the new city"); }
      bool seen = cityBar.Look(InventoryButtonLook(o, W, H, now), now);
      if (seen) CityReadySay(now, "the bar's picture");
      return seen;
    }
    bool InventoryButtonLook(W32.POINT o, double W, double H, long now) {
      double U = Ui.Unit(W, H), cy = H - 0.1156 * U;
      if (!cityBar.MustSearch && inventoryBtn.Seen(o, W, H, W - invDX * U, cy)) return true;
      if (!cityBar.MustSearch && now - cityBar.ScanAt < 3000) return false;
      cityBar.ScanAt = now; cityBar.MustSearch = false;
      double x;
      if (!inventoryBtn.Find(o, W, H, W - (hg2.HeroesBtnDX + 0.3) * U, W - (hg2.HeroesBtnDX + 2.8) * U, cy, out x)) return false;
      cityBar.InvDX = (W - x) / U;
      Log.Write("sell: «Инвентарь» found in the bar at " + invDX.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " U from the right ("
        + (int)Math.Round(x) + " px of " + (int)W + "x" + (int)H + ", U " + U.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ")");
      return true;
    }

    readonly LiveStill cityLive = new LiveStill(); long cityReadyFor = -1;
    /// <summary>The city's «Инвентарь» by its live rect: fresh (UiFreshMs), visible, on the city on top, the same over two reads GapMs apart.
    /// One timing line a city stretch: how long after the city came the button was ready, and by which way.</summary>
    bool CityLiveStill(double W, double H, long now) {
      double U = Ui.Unit(W, H);
      var lv = LiveFrame(ClickTarget.CityInventory, null, W, H, U);
      var r = lv != null ? RFX.UiButtonFresh(ClickTarget.CityInventory, null, W, H, Environment.TickCount, RFX.UiFrameFreshMs) : null;
      bool ok = cityLive.Note(r != null ? lv : null, r != null ? r.At : 0);
      // the live rect's place is the bar's place: the layout's fallback (a missed read) and its clicks stay on it (live 1.6.66.397: the default
      // offset put them 37 px left, onto the neighbour, after the place was dropped)
      if (r != null && U > 0 && Math.Abs(r.ForW - W) <= 0.5) cityBar.InvDX = (W - (r.X + r.W / 2)) / U;
      if (ok) CityReadySay(now, "live rect held still, no bar search");
      return ok;
    }
    void CityReadySay(long now, string how) {
      var c = screens.Current;
      if (c == null || c.Kind != ScreenKind.City || c.SinceMs == cityReadyFor) return;
      cityReadyFor = c.SinceMs;
      Log.Write("city bar: «Инвентарь» ready " + (now - c.SinceMs) + " ms after the city came (" + how + ")");
    }

    // the city's «Инвентарь» button in the bottom bar (where the auto mode clicks): its frame (client px -> screen px), seen by its
    // picture (looked at most every 0.4 s); false = the city bar is not on the screen
    long cityInvAt; bool cityInvSeen;
    bool cityBtnLive;   // the last CityInventoryButton rect is the live one (its icon's frame, 2 px out), not the layout's hit area
    bool CityInventoryButton(W32.POINT o, double W, double H, double U, out int[] r) {
      long now = NowMs();
      if (now - cityInvAt >= 400) { cityInvAt = now; cityInvSeen = InventoryButtonSeen(o, W, H); }
      var cb = LocateFrames.CityButton(Geo.Current, W, H, U, invDX, hg2.HeroesBtnDY);   // the bar's button size from the geometry (else the estimate)
      r = new[] { o.X + cb[0], o.Y + cb[1], cb[2], cb[3] };
      var lv = LiveFrame(ClickTarget.CityInventory, null, W, H, U, true, new double[] { cb[0], cb[1], cb[2], cb[3] });   // where the game has put it (live rect, fresh; its icon, the label left out) wins
      cityBtnLive = lv != null;
      if (lv != null) { r = new[] { o.X + (int)Math.Round(lv[0]), o.Y + (int)Math.Round(lv[1]), (int)Math.Round(lv[2]), (int)Math.Round(lv[3]) }; return true; }
      return cityInvSeen;
    }

    /// <summary>A pilot button's frame where the game has put it: its live rect (client px) when the read is fresh (at most 200 ms), visible,
    /// inside the client and on the form on top; null = the geometry's frame. Asks for the next background read (every 100 ms while framed).
    /// <paramref name="icon"/>: a bar button's frame hugs its icon (UiIcon.Frame, 2 px out: x, y, w, h, radius) when the read found it.</summary>
    double[] LiveFrame(ClickTarget t, string form, double W, double H, double U, bool icon = true, double[] layout = null) {
      RFX.UiButtonWant(t, form, W, H, U);
      int now = Environment.TickCount;
      var r = RFX.UiButtonFresh(t, form, W, H, now, RFX.UiFrameFreshMs);
      double x, y; string why; var sc = screens.Current;
      bool ok = RFX.UiLivePoint(r, W, H, now, out x, out y, out why, RFX.UiFrameFreshMs), top = RFX.UiTopIs(t, form, sc);
      // the picture of the button (its icon, or a wide button's visual child), 2 px round it; none found: its rect, 2 px round it as well
      // (the city bar's «Инвентарь»: its Image_Icon also draws a tip and side lines: the frame hugs the round rim inside it)
      // the Image's sprite in the table (src/SpriteArt.cs): the frame hugs the sprite's visible art mapped through the live rect, outlined by its contour
      var art = ok && top && r != null && (icon || r.IW <= 0) ? SpriteArt.Of(r.Sprite) : null;
      var fit = art != null ? (r.IW > 0 ? SpriteArt.FrameOf(art, r.Art, r.IX, r.IY, r.IW, r.IH, W, H) : SpriteArt.FrameOf(art, r.Art, r.X, r.Y, r.W, r.H, W, H)) : null;
      bool rim = fit == null && t == ClickTarget.CityInventory && r != null && r.IW > 0 && r.IconName == "Image_Icon";   // (no sprite read: the plate's measured circle)
      double[] f = ok && top ? (fit != null ? fit.Frame : rim ? UiIcon.BarRimFrame(r.IX, r.IY, r.IW, r.IH, W, H) : icon && r.IW > 0 ? UiIcon.Frame(r.IX, r.IY, r.IW, r.IH, W, H) : UiIcon.Frame(r.X, r.Y, r.W, r.H, W, H)) : null;
      if (f != null) {
        if (fit != null) liveShapes.Put(f, fit.Box, fit.Shape, NowMs());
        FrameCheck(FrameTargets.KeyOf(t) ?? r.Form + "." + r.Widget, r.Form + "." + r.Widget, r.Sprite, r.Art, fit, f, new[] { r.X, r.Y, r.W, r.H }, "live", r.Sprite == "" ? r.ArtWhy : art == null ? "not in the table" : null);
      }
      // one source per button (FrameStick): a missing or old read, a held screen keep the last live frame for 1.5 s; a fresh read that says the
      // button is hidden, or another form named on top, drop it at once
      bool gone = (r != null && why == "not visible") || (!top && sc != null && !sc.Held);
      string sk = t + "|" + (form ?? ""); FrameStick st;
      if (!frameStick.TryGetValue(sk, out st)) frameStick[sk] = st = new FrameStick();
      var g = st.Pick(f, gone, W, H, NowMs());
      if (g == null) { if (layout != null) FrameSay(t, form, layout, false); return null; }
      FrameSay(t, form, g, true, r != null ? r.Form + "." + r.Widget : null);
      return g;
    }
    readonly Dictionary<string, FrameStick> frameStick = new Dictionary<string, FrameStick>();

    /// <summary>An item cell's frame where the game has put it: the live read of the list's cells (src/UiButtons.cs UiCellRead; asked again every
    /// tick while framed) - Shown: <paramref name="fr"/> = the item's picture 2 px out (x, y, w, h, radius, client px); Hidden: no cell of it in the
    /// viewport, or the list covered by another form; Unknown: no fresh read (the layout's frame). <paramref name="calc"/> (the layout's rect) and
    /// <paramref name="run"/> (the pilot run) for the shadow log of the run's first minute.</summary>
    UiCellSee LiveCell(UiCellKind k, long uid, ICollection<long> want, double W, double H, double U, double[] calc, string run, out double[] fr) {
      fr = null;
      if (uid <= 0) return UiCellSee.Unknown;
      RFX.UiCellWant(k, want ?? new[] { uid }, W, H, U);
      int now = Environment.TickCount;
      var s = RFX.UiCellFresh(k, uid, W, H, now, RFX.UiFrameFreshMs);
      if (s != null) { var sl = RFX.UiCellShadowLine(run, s, uid, calc, now); if (sl != null) Log.Write(sl); }
      string why; var st = RFX.UiCellState(s, uid, W, H, now, out fr, out why, RFX.UiFrameFreshMs);
      if (st == UiCellSee.Shown && fr != null) {   // one frame on the whole cell (the icon with its stat plate)
        fr = CellFrame.Whole(fr, calc);
        var c = s.Of(uid);
        if (c != null) FrameCheck(FrameTargets.KeyOf(k), "cell." + k + ".root", c.Sprite, c.Art, null, fr, new[] { c.BX, c.BY, c.BW, c.BH }, "live", "cell root");
      }
      // no fresh read for a moment: the last live frame of this item stays (never a jump to the layout's and back, no flicker)
      st = cellHold.Pick(k + ":" + uid, st, ref fr, W, H, NowMs());
      if (st == UiCellSee.Unknown || RFX.UiCellTopIs(k, screens.Current)) return st;
      fr = null; var sc = screens.Current;   // covered: hidden when another form is named on top (a pop-up, a modal); nothing named: the layout's
      return sc != null && !sc.Held && (sc.OfferCount > 0 || sc.ModalOpen || !string.IsNullOrEmpty(sc.Top)) ? UiCellSee.Hidden : UiCellSee.Unknown;
    }

    readonly CellFrame.Hold cellHold = new CellFrame.Hold();

    /// <summary>The point of the last item pick clicked on its live cell (client px), null = the layout's (the pilot is told where it went).</summary>
    double[] cellLivePt;
    long filterTouchAt;
    int cellRefuseN; bool cellRefused;   // the last LiveCellClick refused: the gear list's live cells are read but not the one asked for (never the layout's point then)
    /// <summary>The pilot's view of the game's own gear cells: readable now (LiveGrid), the plan's item among them and wholly in view (LiveTarget, its centre).</summary>
    void LiveGearView(AutoView v, W32.RECT cr) {
      if (target <= 0 || cr.R <= 0 || cr.B <= 0) return;
      double W = cr.R, Hc = cr.B, U = Ui.Unit(cr.R, cr.B);
      RFX.UiCellWant(UiCellKind.HeroGear, new[] { target }, W, Hc, U);
      var s = RFX.UiCellFresh(UiCellKind.HeroGear, target, W, Hc, Environment.TickCount, RFX.UiFreshMs);
      if (s == null || !s.Ok || !s.Visible || s.Rotated || s.Copies != 1 || s.VW < 4 || s.VH < 4) return;
      if (!RFX.UiCellTopIs(UiCellKind.HeroGear, screens.Current)) return;
      v.LiveGrid = true;
      double x, y; string why;
      if (RFX.UiCellPoint(s, target, W, Hc, Environment.TickCount, out x, out y, out why)) { v.LiveTarget = true; v.LiveX = x; v.LiveY = y; }
    }
    /// <summary>An item pick (<paramref name="uid"/>) clicks its cell's live rect when the read is fresh (at most 200 ms, else one read now), the cell
    /// holds that item, is wholly in the viewport and the list's form is on top; the item under the click is then that one. Else the layout's point.</summary>
    void LiveCellClick(ClickRequest req, ScreenState fresh, long uid, W32.POINT o, ref int sx, ref int sy) {
      cellLivePt = null; cellRefused = false;
      var k = RFX.UiCellKindOf(req.Target); if (k == UiCellKind.None || uid <= 0 || req.W <= 0 || req.H <= 0) return;
      var s = RFX.UiCellFresh(k, uid, req.W, req.H, Environment.TickCount, RFX.UiFreshMs) ?? RFX.UiCellReadNow(k, new[] { uid }, req.W, req.H, req.U);
      double lx = req.X, ly = req.Y, x, y; string why;
      bool live = RFX.UiCellPoint(s, uid, req.W, req.H, Environment.TickCount, out x, out y, out why);
      if (live && !RFX.UiCellTopIs(k, fresh)) { live = false; why = "the form on top is " + (fresh != null ? fresh.Top + " (" + fresh.Brief + ")" : "not read"); }
      if (live) {
        cellRefuseN = 0;
        req.X = x; req.Y = y; req.Live = true; if (req.CellOk != null) req.CellUid = uid;
        sx = o.X + (int)Math.Round(x); sy = o.Y + (int)Math.Round(y); cellLivePt = new[] { x, y };
      }
      // the grid is read (its form shown, its viewport known) but the item's cell is not wholly in view / not that item: never the layout's point
      // (live 1.6.66.442: a click on stale rows picked another item after a filter rebuilt the list)
      if (!live && k == UiCellKind.HeroGear && req.Target == ClickTarget.EquipNav && s != null && s.Ok && s.Visible && s.Copies == 1 && s.VW >= 4 && cellRefuseN++ < 12) cellRefused = true;   // (twelve in a row at most: a cell that never shows is the old way's)
      if (s != null) Log.Write(RFX.UiCellLine(s, uid, new[] { lx, ly }) + (live ? " [click: live @" + (int)Math.Round(x) + "," + (int)Math.Round(y) + "]" : " [click: layout, " + why + "]"));
    }
    int cellRunN;

    /// <summary>The test build's line of a pilot button's frame, once per change: "frame: Form.widget rect=[x,y,w,h] source=live|layout".</summary>
    void FrameSay(ClickTarget t, string form, double[] f, bool live, string who = null) {
      if (!Channel.IsTest) return;
      var w = RFX.UiWidgetOf(t, form); if (w == null) return;
      if (f == null) return;
      string key = UiIcon.FrameLine(who ?? w.Form + "." + w.Paths[0], f, live);
      string was; if (frameSaid.TryGetValue(t, out was) && was == key) return;
      frameSaid[t] = key;
      Log.Write(key);
    }
    readonly Dictionary<ClickTarget, string> frameSaid = new Dictionary<ClickTarget, string>();

    /// <summary><paramref name="avoid"/> plus the city bar's row (screen px): from the bar button's top to the client's bottom, the whole width, so
    /// the plate beside «Инвентарь» goes above the row and never over the other bar buttons or their labels.</summary>
    static int[][] BarRow(int[][] avoid, int[] btn, W32.POINT o, double W, double H) {
      int top = btn[1] - 2;
      var band = new[] { o.X, top, (int)W, Math.Max(1, o.Y + (int)H - top) };
      var res = new List<int[]>(); if (avoid != null) res.AddRange(avoid); res.Add(band);
      return res.ToArray();
    }

    // the test build's self-check of a framed target, once per target and sprite: "frame check: <form>.<widget> sprite=<name> shape=… frame=[…] visible=[…]"
    // (tools/lab/frames-sheet.sh crops each from the lab's screenshots)
    // "frame check: <key> sprite=... shape=... frame=[...] visible=[...] client=[...] screen=[...] live=[...] src=live|layout widget=<form.widget>"
    readonly LiveShapes liveShapes = new LiveShapes();
    readonly HashSet<string> frameChecked = new HashSet<string>();
    void FrameCheck(string key, string widget, string sprite, SpriteArt.Draw d, SpriteArt.Fit fit, double[] f, double[] live, string src, string why) {
      if (!Channel.IsTest || f == null) return;
      if (auditOn && src == "live") auditLive[key] = new AuditFor { Widget = widget ?? "", Sprite = sprite ?? "", Shape = fit != null ? fit.Kind : "rect", Live = live };
      if (!frameChecked.Add(key + "|" + src + "|" + sprite + "|" + (fit != null ? fit.Kind : "rect")) || frameChecked.Count > 400) return;
      var mon = System.Windows.Forms.Screen.FromPoint(new Point(sceneX, sceneY)).Bounds;
      string where = " client=[" + sceneX + "," + sceneY + "," + sceneW + "," + sceneH + "] screen=[" + mon.X + "," + mon.Y + "," + mon.Width + "," + mon.Height + "]";
      Log.Write(SpriteArt.CheckLine(key, sprite, d, fit, f, sprite == "" || fit == null ? why : null, where) + SpriteArt.CheckTail(live, src, widget));
    }

    // ---- every hint frame through one function (src/FrameTargets.cs): the live rect of the target's visual child -> its sprite's contour ->
    // the outline; the layout's rect only when the live read fails ("frame: <key> source=layout reason=...", once per key and reason)
    // every frame stands on its live rect only (a layout's / estimated rect is never drawn first: the plate comes alone until the live one is
    // known) and, once drawn, moves only when the target really moves (more than FrameSteady.TolPx): no size jumps, no source switching
    readonly Dictionary<string, FrameSteady> frameSteady = new Dictionary<string, FrameSteady>();
    FrameSteady FrameSteadyOf(string key) { FrameSteady st; if (!frameSteady.TryGetValue(key, out st)) frameSteady[key] = st = new FrameSteady(); return st; }
    readonly Dictionary<string, string> frameLayoutSaid = new Dictionary<string, string>();

    /// <summary>The frame of target <paramref name="key"/> (client px: x, y, w, h[, radius]): live when the read is fresh, shown and on the form on top
    /// (<paramref name="live"/> true), else <paramref name="layout"/> (null stays null).</summary>
    double[] FrameFor(string key, double[] layout, double W, double H, double U, out bool live) {
      live = false; string why;
      var ft = FrameTargets.Of(key);
      if (ft == null) why = "unmapped key";
      else if (ft.Layout != null) why = ft.Layout;
      else if (ft.Cell != UiCellKind.None) why = "a list cell (LiveCell)";
      else if (ft.Click != ClickTarget.None) {
        var lv = LiveFrame(ft.Click, null, W, H, U, ft.Visual != "-", layout);
        if (lv != null) { live = true; FrameLiveSeen(key); lv = FrameSteadyOf(key).Pick(lv); AuditFrameFor(key, layout, lv, true, null); return lv; }
        var r = RFX.UiButtonFresh(ft.Click, null, W, H, Environment.TickCount, 1000);
        why = r == null ? "no fresh read" : !r.Ok ? "failed at " + r.FailStep : !r.Visible ? "not visible" : "not on top";
      } else {
        var lv = LiveFrameKey(ft, W, H, U, out why);
        if (lv != null) { live = true; FrameLiveSeen(key); lv = FrameSteadyOf(key).Pick(lv); AuditFrameFor(key, layout, lv, true, null); return lv; }
      }
      // «Заменить» / «Надеть»: no live rect, no frame (never the layout's: it sat beside the card's real button, the plate covered it)
      if (key == FrameTargets.KeyConfirm) { FrameNoneSay(key, why); AuditFrameFor(key, layout, layout, false, why); return null; }
      // a target with a live reader whose first read has not come yet: the frame waits for it (up to FramePendingMs) instead of standing on the
      // layout's rect first and jumping to the live one (the flicker of 1.6.66.423); the plate is shown meanwhile
      if (ft != null && ft.Layout == null && ft.Cell == UiCellKind.None && layout != null) pendingRects.Add(new[] { layout[0], layout[1], layout[2], layout[3] });
      FrameLayoutSay(key, why, layout);
      AuditFrameFor(key, layout, layout, false, why);
      return layout;
    }

    // ---- TEST build, lab runs: the frame audit (src/FrameAudit.cs). Every frame Scene draws this tick is matched to the FrameFor target it came
    // from; once it stood still for FrameAudit.StableMs it is checked once per (scenario, step, target) and run: the flags are logged
    // ("frame audit: FLAG <key> <why>"), and while the overlay is left in captures (a lab recording runs) the client is shot (FrameAuditSink)
    sealed class AuditScene { public GlowWindow Win; public int[] Frame; public string Step; }
    sealed class AuditFor { public string Widget = "", Sprite = "", Shape = "", Why = ""; public double[] Layout, Frame, Live; public bool IsLive, Plate; }
    /// <summary>The lab scenario and run the audit runs for ("" scenario = off), set by the lab's recorder command (app/HostBridge.cs).</summary>
    public string AuditScenario = "", AuditRun = "";
    public Func<string> AuditSite = () => "", AuditCode = () => "";
    bool auditOn;
    readonly List<AuditScene> auditScenes = new List<AuditScene>();
    readonly Dictionary<GlowWindow, double[]> auditPlate = new Dictionary<GlowWindow, double[]>();
    readonly Dictionary<string, AuditFor> auditFor = new Dictionary<string, AuditFor>(), auditLive = new Dictionary<string, AuditFor>();
    readonly FrameAuditTracker auditTrack = new FrameAuditTracker();
    FrameAuditSink auditSink;
    int auditShots, auditFlags;

    void AuditBegin() {
      auditOn = Channel.IsTest && AuditScenario != "" && AuditRun != "";
      auditScenes.Clear(); auditFor.Clear(); auditLive.Clear();
    }

    /// <summary>The layout rect the audit compares a live frame with: a target framed by its whole plate (a tab) has the graph's plate, others the caller's rect.</summary>
    double[] PlateLayoutFor(string key, double[] layout, out bool plate) {
      var pl = sceneW > 0 && sceneH > 0 ? FrameTargets.PlateLayout(FrameTargets.Of(key), sceneW, sceneH, Ui.Unit(sceneW, sceneH)) : null;
      plate = pl != null;
      return pl ?? (layout != null ? (double[])layout.Clone() : null);
    }

    void AuditFrameFor(string key, double[] layout, double[] frame, bool live, string why) {
      if (!auditOn || frame == null) return;
      AuditFor a; auditLive.TryGetValue(key, out a); bool plate;
      auditFor[key] = new AuditFor { Widget = a != null ? a.Widget : "", Sprite = a != null ? a.Sprite : "", Shape = a != null ? a.Shape : "", Live = live && a != null ? a.Live : null,
        Layout = PlateLayoutFor(key, layout, out plate), Plate = plate, Frame = (double[])frame.Clone(), IsLive = live, Why = why ?? "" };
    }

    void AuditEnd() {
      if (!auditOn || sceneW <= 0 || sceneH <= 0) return;
      auditTrack.Run(AuditRun);
      var frames = new List<AuditScene>();
      foreach (var sc in auditScenes) if (sc.Frame != null && sc.Frame.Length >= 4 && !frames.Exists(f => f.Frame[0] == sc.Frame[0] && f.Frame[1] == sc.Frame[1] && f.Frame[2] == sc.Frame[2] && f.Frame[3] == sc.Frame[3])) frames.Add(sc);
      if (frames.Count == 0) return;
      var plates = new List<double[]>();
      foreach (var sc in auditScenes) { double[] pr; if (auditPlate.TryGetValue(sc.Win, out pr) && pr != null && !plates.Contains(pr)) plates.Add(pr); }
      long now = NowMs();
      foreach (var sc in frames) {
        var fr = new double[] { sc.Frame[0] - sceneX, sc.Frame[1] - sceneY, sc.Frame[2], sc.Frame[3] };
        // the target: the FrameFor rect this frame is (best overlap, at least half)
        string target = "unmapped"; AuditFor best = null; double bi = 0.5;
        foreach (var kv in auditFor) { double i = FrameAudit.IoU(fr, kv.Value.Frame); if (i > bi) { bi = i; best = kv.Value; target = kv.Key; } }
        if (!auditTrack.Observe(AuditScenario + "|" + sc.Step + "|" + target, fr, now)) continue;
        var s = new FrameAuditShot { Scenario = AuditScenario, Run = AuditRun, Step = sc.Step, Target = target, Frame = fr, ClientW = sceneW, ClientH = sceneH, FramesNow = frames.Count, AtMs = now };
        if (best != null) {
          s.Source = best.IsLive ? "live" : "layout"; s.Widget = best.Widget; s.Sprite = best.Sprite; s.Shape = best.Shape; s.Why = best.Why;
          s.Live = best.Live; s.Layout = best.Layout; s.PlateLayout = best.Plate;
          FrameAudit.HiddenOrCovered(best.Why, out s.Hidden, out s.Covered);
        } else s.Source = "none";
        foreach (var pr in plates) s.Plates.Add(new[] { pr[0] - sceneX, pr[1] - sceneY, pr[2], pr[3] });
        var flags = FrameAudit.Flags(s);
        auditShots++; if (flags.Count > 0) auditFlags++;
        foreach (var why in flags) Log.Write(FrameAudit.FlagLine(s, why));
        Log.Write("frame audit: " + (flags.Count > 0 ? "suspect " : "ok ") + FrameAudit.Key(s) + " source=" + s.Source + " frame=[" + string.Join(",", Array.ConvertAll(fr, v => ((int)Math.Round(v)).ToString())) + "]"
          + " (" + auditShots + " checked, " + auditFlags + " suspect in this app run)");
        if (auditSink == null) auditSink = new FrameAuditSink(AuditSite, AuditCode, m => Log.Write(m));
        auditSink.Add(s, sceneX, sceneY, sceneW, sceneH, GlowWindow.Capturable);
      }
    }

    /// <summary>The live frame of a target is still awaited: asked first under <see cref="FramePendingMs"/> ago and never read live since (a key not
    /// asked for 2 s starts again).</summary>
    bool FramePending(string key) {
      long now = NowMs(); long[] a;
      if (!frameAsk.TryGetValue(key, out a) || now - a[1] > 2000 || now < a[1]) frameAsk[key] = a = new[] { now, now, 0 };
      a[1] = now;
      return a[2] == 0 && now - a[0] < FramePendingMs;
    }
    void FrameLiveSeen(string key) { long now = NowMs(); long[] a; if (!frameAsk.TryGetValue(key, out a)) frameAsk[key] = a = new[] { now, now, 0 }; a[1] = now; a[2] = 1; }
    const long FramePendingMs = 1200;
    readonly Dictionary<string, long[]> frameAsk = new Dictionary<string, long[]>();
    /// <summary>The layout rects (client px) FrameFor gave this tick while their target's live read is awaited: a scene framing one of them draws the plate alone.</summary>
    readonly List<double[]> pendingRects = new List<double[]>();
    bool PendingFrame(int[] fr) {
      if (fr == null) return false;
      foreach (var p in pendingRects)
        if (Math.Abs(fr[2] - p[2]) <= 2 && Math.Abs(fr[3] - p[3]) <= 2 && Math.Abs(fr[0] - sceneX - p[0]) <= 2 && Math.Abs(fr[1] - sceneY - p[1]) <= 2) return true;
      return false;
    }

    void FrameNoneSay(string key, string why) {
      string was; if (frameLayoutSaid.TryGetValue(key, out was) && was == "none " + why) return;
      frameLayoutSaid[key] = "none " + why;
      Log.Write("frame: " + key + " source=none reason=" + why);
    }

    void FrameLayoutSay(string key, string why, double[] layout) {
      string was; if (frameLayoutSaid.TryGetValue(key, out was) && was == why) return;
      frameLayoutSaid[key] = why;
      Log.Write("frame: " + key + " source=layout reason=" + why);
      if (layout != null) FrameCheck(key, "layout", "", null, null, layout, null, "layout", why);
    }

    /// <summary>A frame target's live frame (FrameTargets.Widget: read by its tree), as LiveFrame does for the pilot buttons; null = none (why says).</summary>
    double[] LiveFrameKey(FrameTarget ft, double W, double H, double U, out string why) {
      var w = FrameTargets.Widget(ft);
      if (w == null) { why = "no widget"; return null; }
      RFX.UiTargetWant(ft.Key, w, W, H, U);
      int now = Environment.TickCount;
      var r = RFX.UiTargetFresh(ft.Key, W, H, now, RFX.UiFrameFreshMs);
      double x, y; string pw; var sc = screens.Current;
      bool ok = RFX.UiLivePoint(r, W, H, now, out x, out y, out pw, RFX.UiFrameFreshMs), top = RFX.UiFormTopIs(ft.Form, sc);
      var art = ok && top ? SpriteArt.Of(r.Sprite) : null;
      var fit = art != null ? (r.IW > 0 ? SpriteArt.FrameOf(art, r.Art, r.IX, r.IY, r.IW, r.IH, W, H) : SpriteArt.FrameOf(art, r.Art, r.X, r.Y, r.W, r.H, W, H)) : null;
      double[] f = ok && top ? (fit != null ? fit.Frame : r.IW > 0 ? UiIcon.Frame(r.IX, r.IY, r.IW, r.IH, W, H) : UiIcon.Frame(r.X, r.Y, r.W, r.H, W, H)) : null;
      if (f != null) {
        if (fit != null) liveShapes.Put(f, fit.Box, fit.Shape, NowMs());
        FrameCheck(ft.Key, r.Form + "." + r.Widget, r.Sprite, r.Art, fit, f, r.IW > 0 ? new[] { r.IX, r.IY, r.IW, r.IH } : new[] { r.X, r.Y, r.W, r.H }, "live", r.Sprite == "" ? r.ArtWhy : art == null ? "not in the table" : null);
      }
      bool gone = (r != null && pw == "not visible") || (!top && sc != null && !sc.Held);
      string sk = "key|" + ft.Key; FrameStick st;
      if (!frameStick.TryGetValue(sk, out st)) frameStick[sk] = st = new FrameStick();
      var g = st.Pick(f, gone, W, H, NowMs());
      why = g != null ? null : !ok ? (r != null && !r.Ok ? "failed at " + r.FailStep + " (" + r.FailValue + ")" : pw) : !top ? "form not on top" : "held";
      return g;
    }

    /// <summary>The step's box frame <paramref name="lf"/> moved onto its target's live frame (FrameFor); the click point too for a pilot button
    /// (its read's centre). False = the layout's box stays.</summary>
    bool LiveInto(LocateFrame lf, double W, double H, double U) {
      if (lf == null || lf.Mark == null || lf.Mark.Kind != "box" || lf.Target == "cell") return false;
      var lay = lf.Raw != null ? new[] { lf.Raw.X, lf.Raw.Y, lf.Raw.W, lf.Raw.H } : new double[] { lf.Mark.X, lf.Mark.Y, lf.Mark.W, lf.Mark.H };
      bool live; var lv = FrameFor(lf.Target, lay, W, H, U, out live);
      if (!live) return false;
      var ft = FrameTargets.Of(lf.Target);
      var lb = ft != null && ft.Click != ClickTarget.None ? RFX.UiButtonFresh(ft.Click, null, W, H, Environment.TickCount, RFX.UiFreshMs) : null;
      lf.Raw = new GeoRect { X = lv[0], Y = lv[1], W = lv[2], H = lv[3] };
      lf.Mark = new LocateMark { Kind = "box", X = (int)Math.Round(lv[0]), Y = (int)Math.Round(lv[1]), W = (int)Math.Round(lv[2]), H = (int)Math.Round(lv[3]) };
      if (lb != null) { lf.ClickX = lb.X + lb.W / 2; lf.ClickY = lb.Y + lb.H / 2; }
      return true;
    }

    /// <summary>An int rect (client px) through FrameFor: the target's live frame, else the rect itself.</summary>
    int[] FrameForI(string key, int[] layout, double W, double H, double U, out bool live) {
      live = false;
      if (layout == null) return null;
      var f = FrameFor(key, new double[] { layout[0], layout[1], layout[2], layout[3] }, W, H, U, out live);
      if (!live) return key == FrameTargets.KeyConfirm ? null : layout;
      return new[] { (int)Math.Round(f[0]), (int)Math.Round(f[1]), (int)Math.Round(f[2]), (int)Math.Round(f[3]) };
    }

    string heroLogged;
    /// <summary>One journal line whenever the hero pilot's view or state changes.</summary>
    void LogHero(HeroView v) {
      int i = v.Heroes != null ? Array.IndexOf(v.Heroes, v.Plan) : -2, j = v.Heroes != null ? Array.IndexOf(v.Heroes, v.Hero) : -2;
      string line = "hero: plan " + v.Plan + "@" + i + " shown " + v.Hero + "@" + j + " tab=" + v.Tab + " list=" + v.Part
        + " filter=" + (v.FilterOpen ? "open" : "closed") + " camps=" + string.Join(",", v.ChosenCamps ?? new long[0]) + " class=" + v.ClassChosen
        + " small=" + v.SmallCards + " grid=" + (v.Heroes != null ? v.Heroes.Length : -1) + " -> " + hpilot.State + (hpilot.Why != "" ? " (" + hpilot.Why + ")" : "");
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
    const string EquipTipForm = "Form_CharactorEquipTip";
    /// <summary>The item's tip is among the shown forms (a flow form of the hero screen).</summary>
    public bool EquipTipShown() { var cs = screens.Current; return cs != null && ((cs.Flow ?? "") + "," + (cs.ModalWhy ?? "")).Contains(EquipTipForm); }
    int[] ConfirmButton(W32.POINT o, double W, double H) {
      int[] rep = FindBlue(o, hg.Replace(W, H), H), eq = FindBlue(o, hg.Equip(W, H), H);
      confirmBlueNone = rep == null && eq == null;
      // the item a single button was found for is on the hero now: that button kind is the one at this window size
      if (confirmChoice.PendingItem > 0 && addrs != null && hintHero > 0 && RFX.ReadOwner(addrs, confirmChoice.PendingItem) == hintHero) confirmChoice.Worn(confirmChoice.PendingItem);
      int[] btn = confirmChoice.Pick(rep, eq, target, W, H);
      if (btn != null && rep != null && eq != null && confirmTieLogged != target) {
        confirmTieLogged = target;
        Log.Write("confirm: both blue, using the " + confirmChoice.Learned(W, H) + " place that worked at " + ConfirmChoice.SizeKey(W, H) + ", item " + target);
      }
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
          if (AutoConfirm && AutoEnabled) RaiseProblem("pilot.confirm_no_button", miss, ConfirmMissDetail(o, W, H, rep, eq));
        }
      } else if (btn != null && (btn[1] != hg.Replace(W, H)[1] && btn[1] != hg.Equip(W, H)[1]) && confirmFoundLogged != target) {
        confirmFoundLogged = target;
        Log.Write("confirm: button found " + (btn[1] - (btn == eq ? hg.Equip(W, H)[1] : hg.Replace(W, H)[1])) + " px off its usual place, item " + target);
      }
      return btn;
    }
    bool confirmBlueNone;   // the last confirm search saw no blue button at all (AutoView.BlueNone)
    long confirmMissLogged, confirmFoundLogged, confirmMissSince, confirmMissFor, confirmTieLogged;
    readonly ConfirmChoice confirmChoice = new ConfirmChoice();

    /// <summary>What the confirm search saw (for the report): the window, both buttons' usual rects and their blue share
    /// there, where each search found blue (or none), and the selection / owner / hero shown.</summary>
    System.Collections.Generic.Dictionary<string, string> ConfirmMissDetail(W32.POINT o, double W, double H, int[] rep, int[] eq) {
      var d = new System.Collections.Generic.Dictionary<string, string>();
      int[] r0 = hg.Replace(W, H), e0 = hg.Equip(W, H);
      Func<int[], string> rc = r => r == null ? "none" : r[0] + "," + r[1] + "," + r[2] + "x" + r[3];
      Func<int[], string> share = r => {
        int[] px = Grab(o.X + r[0] + r[2] * 15 / 100, o.Y + r[1] + r[3] / 5, r[2] * 70 / 100, r[3] * 3 / 5);
        return px == null ? "-" : ButtonCheck.BlueShare(px).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
      };
      d["client"] = (int)W + "x" + (int)H + " unit " + (int)Ui.Unit(W, H) + " aspect " + (W / Math.Max(1, H)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
      d["replace"] = "usual " + rc(r0) + " blue " + share(r0) + " found " + rc(rep) + (rep != null ? " blue " + share(rep) : "");
      d["equip"] = "usual " + rc(e0) + " blue " + share(e0) + " found " + rc(eq) + (eq != null ? " blue " + share(eq) : "");
      long owner = addrs != null && target > 0 ? RFX.ReadOwner(addrs, target) : -1;
      d["sel"] = "item " + target + " sel " + curSel + " owner " + owner + " hero " + hintHero + " screen hero " + (addrs != null ? RFX.ReadHero(addrs) : 0) + " slot " + hintSlot + " guide " + hintKind;
      d["item"] = target.ToString();
      d["learned"] = confirmChoice.Learned(W, H);
      return d;
    }

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

    /// <summary>A pilot run is starting and the game is not in front while our window is (or covers the game): the app brings the game forward
    /// itself (allowed: we are the foreground process; fallback AttachThreadInput to the foreground thread; never a key press) and steps our main
    /// window off the game when it covers it. Never when no pilot run is starting (FrontPolicy). <paramref name="pilotWaiting"/>: an auto pilot
    /// waits only for the game to come forward - then another program on top (Telegram) gives way too, once the player gave no input for
    /// FrontPolicy.IdleRaiseMs (GetLastInputInfo, our own clicks and keys left out; never while the player is active). Returns true when it raised.</summary>
    bool RaiseGame(IntPtr hwnd, bool pilotStarting, long now, bool pilotWaiting) {
      if (hwnd == IntPtr.Zero) return false;
      IntPtr f = W32.GetForegroundWindow();
      int fp; W32.GetWindowThreadProcessId(f, out fp);
      IntPtr self = AppWindowHandle();
      bool selfVis = self != IntPtr.Zero && W32.IsWindowVisible(self), selfMin = selfVis && W32.IsIconic(self), over = false;
      W32.RECT sr, gr;
      if (selfVis && !selfMin && W32.GetWindowRect(self, out sr) && W32.GetWindowRect(hwnd, out gr))
        over = FrontPolicy.Overlaps(sr.L, sr.T, sr.R, sr.B, gr.L, gr.T, gr.R, gr.B);
      bool gameFront = !W32.IsIconic(hwnd) && IsForeground(hwnd);
      long idle = -1;
      if (!FrontPolicy.ShouldRaise(true, pilotStarting, gameFront, fp == SelfPid, over, now, focusAfter)) {
        if (!pilotWaiting || gameFront) return false;
        bool down = (W32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (W32.GetAsyncKeyState(0x02) & 0x8000) != 0;
        idle = UserIdleMs();
        if (!FrontPolicy.ShouldRaiseIdle(true, pilotWaiting, gameFront, W32.IsIconic(hwnd), down, idle, now, focusAfter)) return false;
      }
      focusAfter = now + 3000;
      string was = "?"; try { var pr = Process.GetProcessById(fp); was = pr.ProcessName; } catch { }
      if (W32.IsIconic(hwnd)) W32.ShowWindow(hwnd, 9);   // SW_RESTORE (only when minimized)
      bool ok = W32.SetForegroundWindow(hwnd);
      if (!ok || !IsForeground(hwnd)) {
        int d; uint ft = W32.GetWindowThreadProcessId(f, out d), me = W32.GetCurrentThreadId();
        bool att = ft != 0 && ft != me && W32.AttachThreadInput(me, ft, true);
        try { W32.BringWindowToTop(hwnd); W32.SetForegroundWindow(hwnd); }
        finally { if (att) W32.AttachThreadInput(me, ft, false); }
      }
      if (FrontPolicy.ShouldMoveSelf(selfVis, selfMin, over) && (W32.GetWindowLong(self, -20) & 0x00000008) == 0)
        W32.SetWindowPos(self, (IntPtr)1, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);   // HWND_BOTTOM, no move / size / activate: our window goes under the game
      Log.Write("front: game raised by the app (was " + was + (idle >= 0 ? ", the pilot waited, no input for " + (idle / 1000) + " s" : "") + ")");
      return true;
    }

    // the player's last input (GetLastInputInfo), our own injected clicks / keys left out (UserIdle: the time of our sends)
    readonly UserIdle userIdle = new UserIdle();
    long UserIdleMs() {
      var li = new W32.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(W32.LASTINPUTINFO)) };
      if (!W32.GetLastInputInfo(ref li)) return 0;
      return userIdle.IdleMs(li.dwTime, (uint)Environment.TickCount);
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
      return OverlayGate.CanDraw(has, has && W32.IsWindowVisible(hwnd), has && W32.IsIconic(hwnd), has && FrontSteady(hwnd), screenActive);
    }

    // the game in front for drawing: a foreground change shorter than HintHold.DebounceMs (the pilot raising the game, a click's focus
    // hand-over, the foreground window 0 for a moment) does not hide our hints; the pilots' own checks use IsForeground itself
    long frontSeenAt = -1;
    bool FrontSteady(IntPtr hwnd) {
      long now = NowMs();
      if (IsForeground(hwnd)) { frontSeenAt = now; return true; }
      return frontSeenAt >= 0 && now - frontSeenAt >= 0 && now - frontSeenAt < HintHold.DebounceMs;
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

    // a rectangle with rounded corners (a list cell, a button of unknown shape)
    Hl HlRound(double x, double y, double w, double h, bool important) {
      return new Hl { Rect = new GeoRect { X = x, Y = y, W = w, H = h }, Shape = new GeoShape { K = "round", R = 0.08 }, Important = important };
    }
    Hl HlCell(double x, double y, double w, double h) { return HlRound(x, y, w, h, true); }

    /// <summary>The outline of the live frame drawn at x, y, w, h (screen px): the sprite's contour / shape LiveFrame or LiveCell found for it; null = none.</summary>
    Hl HlLive(double x, double y, double w, double h) {
      GeoRect b; GeoShape sh;
      if (!liveShapes.Find(x - sceneX, y - sceneY, w, h, NowMs(), out b, out sh) || sh.K == "rect") return null;
      return new Hl { Rect = new GeoRect { X = b.X + sceneX, Y = b.Y + sceneY, W = b.W, H = b.H }, Shape = sh };
    }

    // the city bar's «Инвентарь» (screen px rect): its geometry shape for the old frame
    Hl HlCity(int[] r) {
      var g = Geo.Current;
      var lh = cityBtnLive ? HlLive(r[0], r[1], r[2], r[3]) : null; if (lh != null) return lh;   // its sprite's contour
      // the live frame is the rim's own square already (2 px out): the outline is that circle, no inset (the inset is of the hit area)
      if (cityBtnLive) { double p = UiIcon.FramePad; return new Hl { Rect = new GeoRect { X = r[0] + p, Y = r[1] + p, W = r[2] - 2 * p, H = r[3] - 2 * p }, Shape = new GeoShape { K = "circle" } }; }
      return new Hl { Rect = new GeoRect { X = r[0], Y = r[1], W = r[2], H = r[3] }, Shape = g != null ? g.Shape("city.barButton") : null, Inset = g != null ? g.Inset("city.barButton") : null };
    }

    // «Показать в игре»: the step's button with the geometry's shape; the final presses (Done, the Foundry's «Вознести») and list cells are B, the rest A
    Hl HlLocate(LocateFrame lf, LocateStep step, W32.POINT o) {
      var g = Geo.Current;
      bool important = step.Kind == LocateKind.Done || step.Kind == LocateKind.AscendReady || step.Kind == LocateKind.ArtPromote || lf.Target == "cell";
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

  }
}
