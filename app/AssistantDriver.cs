// The assistant's game driver: lets the owner's AI assistant play Watcher of Realms on this PC like a player — screenshots
// of the game window, mouse clicks / drags and a few keys inside it (SendInput, as the auto-pilot in Overlay.cs). Input only:
// it never reads or writes the game's memory for this, never touches the game's files or network.
//
// OFF unless config.json has "assistantDriver": "<folder>" (no switch in the interface: it is for the owner's own PC).
// Commands come one per file, <folder>\cmd.txt; the outcome goes to <folder>\out.txt ("ok …" / "error …"):
//   shot <png path>                       the game window's client area to a PNG
//   click <x> <y>                         a left click at client pixel (x, y)
//   drag <x1> <y1> <x2> <y2> [ms]         press at 1, move to 2 over ms (default 400), release
//   key <esc|space|enter|1..5>            one key press (the game's own hotkeys)
//   wait <ms>                             sleep (≤ 10000)
// Safety: points outside the game's client area are refused; the game is brought to the front first; F12 stops the driver
// until RealmForge restarts; every command is written to the journal.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RealmForge {
  sealed class AssistantDriver : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT {
      [FieldOffset(0)] public uint type;
      [FieldOffset(8)] public MOUSEINPUT mi;
      [FieldOffset(8)] public KEYBDINPUT ki;
    }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    readonly string dir, cmd, outF;
    readonly Thread loop;
    volatile bool stop;

    AssistantDriver(string dir) {
      this.dir = dir; cmd = Path.Combine(dir, "cmd.txt"); outF = Path.Combine(dir, "out.txt");
      loop = new Thread(Run) { IsBackground = true, Name = "assistant-driver" };
    }

    /** Started only when the config names the folder; null otherwise. */
    public static AssistantDriver StartIf(string dir) {
      if (string.IsNullOrEmpty(dir)) return null;
      try {
        Directory.CreateDirectory(dir);
        var d = new AssistantDriver(dir);
        d.loop.Start();
        Log.Write("assistant driver: on, commands in " + dir + " (F12 stops it)");
        return d;
      } catch (Exception e) { Log.Write("assistant driver: " + e.Message); return null; }
    }

    public void Dispose() { stop = true; }

    void Run() {
      while (!stop) {
        if ((GetAsyncKeyState(0x7B) & 0x8000) != 0) { stop = true; Log.Write("assistant driver: F12 — stopped until restart"); break; }
        if (!File.Exists(cmd)) { Thread.Sleep(100); continue; }
        string line;
        try { line = File.ReadAllText(cmd).Trim(); File.Delete(cmd); } catch (IOException) { Thread.Sleep(50); continue; }
        string res;
        try { res = Do(line); } catch (Exception e) { res = "error " + e.Message; }
        Log.Write("assistant driver: " + line + " -> " + res);
        try { File.WriteAllText(outF, res); } catch (IOException) { }
      }
    }

    string Do(string line) {
      var a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
      if (a.Length == 0) return "error empty";
      switch (a[0]) {
        case "wait": Thread.Sleep(Math.Min(10000, Math.Max(0, int.Parse(a[1])))); return "ok";
        case "shot": {
          var w = Game(); var r = Client(w);
          using (var bmp = new Bitmap(r.R - r.L, r.B - r.T)) {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.L, r.T, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
            bmp.Save(string.Join(" ", a, 1, a.Length - 1), ImageFormat.Png);
          }
          return "ok " + (r.R - r.L) + "x" + (r.B - r.T);
        }
        case "click": {
          var w = Game(); var p = Inside(w, int.Parse(a[1]), int.Parse(a[2]));
          SetCursorPos(p.X, p.Y); Thread.Sleep(40);
          Mouse(0x0002); Thread.Sleep(60); Mouse(0x0004);
          return "ok";
        }
        case "drag": {
          var w = Game(); var p1 = Inside(w, int.Parse(a[1]), int.Parse(a[2])); var p2 = Inside(w, int.Parse(a[3]), int.Parse(a[4]));
          int ms = a.Length > 5 ? Math.Min(5000, Math.Max(50, int.Parse(a[5]))) : 400;
          SetCursorPos(p1.X, p1.Y); Thread.Sleep(40);
          Mouse(0x0002); Thread.Sleep(80);
          const int Steps = 20;
          for (int i = 1; i <= Steps && !stop; i++) {
            SetCursorPos(p1.X + (p2.X - p1.X) * i / Steps, p1.Y + (p2.Y - p1.Y) * i / Steps);
            Thread.Sleep(ms / Steps);
          }
          Thread.Sleep(120); Mouse(0x0004);
          return "ok";
        }
        case "key": {
          ushort vk;
          switch (a.Length > 1 ? a[1] : "") {
            case "esc": vk = 0x1B; break; case "space": vk = 0x20; break; case "enter": vk = 0x0D; break;
            case "1": case "2": case "3": case "4": case "5": vk = (ushort)(0x30 + int.Parse(a[1])); break;
            default: return "error key not allowed";
          }
          Game();
          Key(vk, 0); Thread.Sleep(50); Key(vk, 2);
          return "ok";
        }
        default: return "error unknown command";
      }
    }

    static IntPtr Game() {
      var ps = Process.GetProcessesByName("Watcher of Realms");
      if (ps.Length == 0) throw new Exception("the game is not running");
      var h = ps[0].MainWindowHandle;
      if (h == IntPtr.Zero) throw new Exception("the game has no window");
      if (IsIconic(h)) ShowWindow(h, 9);
      if (GetForegroundWindow() != h) { SetForegroundWindow(h); Thread.Sleep(250); }
      return h;
    }

    static RECT Client(IntPtr h) {
      RECT c; GetClientRect(h, out c);
      var o = new POINT(); ClientToScreen(h, ref o);
      return new RECT { L = o.X, T = o.Y, R = o.X + c.R, B = o.Y + c.B };
    }

    static POINT Inside(IntPtr h, int x, int y) {
      var r = Client(h);
      if (x < 0 || y < 0 || x >= r.R - r.L || y >= r.B - r.T) throw new Exception("point outside the game window");
      return new POINT { X = r.L + x, Y = r.T + y };
    }

    void Mouse(uint flags) {
      if (stop) return;
      SendInput(1, new[] { new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } } }, Marshal.SizeOf(typeof(INPUT)));
    }

    void Key(ushort vk, uint flags) {
      if (stop) return;
      SendInput(1, new[] { new INPUT { type = 1, ki = new KEYBDINPUT { vk = vk, flags = flags } } }, Marshal.SizeOf(typeof(INPUT)));
    }
  }
}
