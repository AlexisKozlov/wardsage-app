// The tray and the start with Windows.
//
// Autostart: a value in HKCU\Software\Microsoft\Windows\CurrentVersion\Run («Wardsage» = "<exe>" --tray) — the
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
    const string Name = Channel.AutostartName;
    const string OldName = "RealmForge";   // before the rename of the product (the normal build only)
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

    /// <summary>The exe was renamed in place: an autostart entry (new or old name) that pointed at the old exe now starts the new one, same arguments.</summary>
    public static void Retarget(string oldExe, string newExe) {
      using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) {
        if (k == null) return;
        foreach (var n in new[] { Name, OldName }) {
          var v = k.GetValue(n) as string;
          int at = v == null ? -1 : v.IndexOf(oldExe, StringComparison.OrdinalIgnoreCase);
          if (at < 0) continue;
          k.SetValue(Name, v.Substring(0, at) + newExe + v.Substring(at + oldExe.Length));
          if (n != Name) k.DeleteValue(n, false);
          Log.Write("autostart: now starts " + newExe);
        }
      }
    }

    /// <summary>The exe moved (an update in another folder): the autostart entry follows it.</summary>
    public static void Refresh() {
      try {
        bool moveOld = false;
        if (!Channel.IsTest) {
          // the entry of the old name that points at this exe moves to the new one (on / off state is kept)
          using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false)) {
            var old = k != null ? k.GetValue(OldName) as string : null;
            moveOld = old != null && k.GetValue(Name) == null && old.IndexOf(System.Windows.Forms.Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
          }
          if (moveOld) {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) {
              if (k != null) { k.SetValue(Name, Command); k.DeleteValue(OldName, false); }
            }
            Log.Write("autostart: entry renamed to " + Name);
          }
        }
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false)) {
          var v = k != null ? k.GetValue(Name) as string : null;
          if (v != null && v != Command) Set(true);
        }
      } catch (Exception) { }
    }
  }

  /// <summary>«Show the window» from a second start of the exe to the running one.</summary>
  static class ShowSignal {
    const string Name = Channel.EventName;

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
