// RealmForge extractor - entry point called from RealmForge-Extractor.ps1.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RealmForge {
  public static class App {
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] list, uint count);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    // scriptPath: full path of the .ps1 (used by "Restart as administrator"); may be null.
    public static void Run(string scriptPath) {
      HideOwnConsole();
      if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) { RunUi(scriptPath); return; }
      // WinForms needs an STA thread (powershell.exe is STA by default, but -MTA exists).
      var t = new Thread(delegate() { RunUi(scriptPath); });
      t.SetApartmentState(ApartmentState.STA);
      t.Start();
      t.Join();
    }

    static void RunUi(string scriptPath) {
      try { SetProcessDPIAware(); } catch (Exception) { }   // crisp text on scaled screens
      Application.EnableVisualStyles();
      try { Application.SetCompatibleTextRenderingDefault(false); } catch (InvalidOperationException) { }
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
      Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) {
        MessageBox.Show(Strings.Format("err_internal", e.Exception.Message), "RealmForge", MessageBoxButtons.OK, MessageBoxIcon.Error);
      };
      using (var form = new MainForm(AppConfig.Load(), scriptPath)) Application.Run(form);
    }

    // Hides the PowerShell console behind the window, but only when nobody else uses it
    // (started from Run-RealmForge.bat or Explorer, not typed into an open terminal).
    static void HideOwnConsole() {
      try {
        IntPtr h = GetConsoleWindow();
        if (h == IntPtr.Zero) return;
        var list = new uint[4];
        if (GetConsoleProcessList(list, (uint)list.Length) <= 1) ShowWindow(h, 0 /* SW_HIDE */);
      } catch (Exception) { }
    }
  }
}
