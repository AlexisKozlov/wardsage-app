// The tray and the start with Windows.
//
// Autostart: a value in HKCU\Software\Microsoft\Windows\CurrentVersion\Run («RealmForge» = "<exe>" --tray) — the
// current user's own list, no administrator rights; the app then starts hidden in the tray (Program.Main, --tray) and
// keeps syncing and recording fights in the background. Off by default: the player turns it on in the settings.
// A second start of the exe (a shortcut, the start menu) shows the running one's window: Program signals it through a
// named event (a window hidden in the tray has no main window handle to bring to the front).
using System;
using System.Threading;
using Microsoft.Win32;

namespace RealmForge {
  static class Autostart {
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "RealmForge";
    public const string TrayArg = "--tray";

    static string Command { get { return "\"" + System.Windows.Forms.Application.ExecutablePath + "\" " + TrayArg; } }

    /// <summary>Is the app in the user's autostart list (with this exe's path)?</summary>
    public static bool IsOn() {
      try {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false)) {
          var v = k != null ? k.GetValue(Name) as string : null;
          return v != null && v.IndexOf(System.Windows.Forms.Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
        }
      } catch (Exception e) { Log.Write("autostart: " + e.Message); return false; }
    }

    public static void Set(bool on) {
      try {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) {
          if (k == null) return;
          if (on) k.SetValue(Name, Command);
          else if (k.GetValue(Name) != null) k.DeleteValue(Name, false);
        }
        Log.Write("autostart " + (on ? "on" : "off"));
      } catch (Exception e) { Log.Write("autostart: " + e.Message); }
    }

    /// <summary>The exe moved (an update in another folder): the autostart entry follows it.</summary>
    public static void Refresh() {
      try {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false)) {
          var v = k != null ? k.GetValue(Name) as string : null;
          if (v != null && v != Command) Set(true);
        }
      } catch (Exception) { }
    }
  }

  /// <summary>«Show the window» from a second start of the exe to the running one.</summary>
  static class ShowSignal {
    const string Name = "RealmForge.Desktop.Show";

    /// <summary>The running instance: call `show` (on any thread) whenever another start asks.</summary>
    public static void Listen(Action show) {
      EventWaitHandle ev;
      try { ev = new EventWaitHandle(false, EventResetMode.AutoReset, Name); }
      catch (Exception e) { Log.Write("show signal: " + e.Message); return; }
      var t = new Thread(() => { while (true) { try { ev.WaitOne(); show(); } catch (Exception) { return; } } });
      t.IsBackground = true;
      t.Start();
    }

    /// <summary>A second start: ask the running one to show its window. False = no one listens (an old version).</summary>
    public static bool Send() {
      try {
        EventWaitHandle ev;
        if (!EventWaitHandle.TryOpenExisting(Name, out ev)) return false;
        using (ev) ev.Set();
        return true;
      } catch (Exception) { return false; }
    }
  }
}
