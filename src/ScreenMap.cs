// Wardsage - the screen map: every form of the game (src/ScreenMapData.cs, generated from the client's Lua and UI prefabs by
// tools/screen-map.py) and how a pilot leaves it: the back arrow (F), the close button × (P), one Escape key press (E), wait (W: a
// fight, a loading, a result, a tutorial), stop (D: a buy / spend / draw / fight start / quit dialog with no × or back arrow), ignore
// (O: an always-on overlay). K = a screen the app reads itself (its pilots decide there); U and forms not in the map: no exit known (the
// pilot keeps the older rules: the standard back arrow on an unknown full screen, the unknown-screen stop).
//
// Pure data and placement math, no Windows APIs (tests: tests/CoreTests.cs). The host (app/Overlay.cs) reads the shown forms
// (src/OfferScan.cs), ScreenState.Top names the one on top, ClickGate.MapExit decides and the gate checks the click (MapClose).
using System;
using System.Collections.Generic;

namespace RealmForge {
  public static partial class ScreenMap {
    /// <summary>One form: its class name, class letter, what GetBackspaceBehav returns ("default" = no such function, "cs" = a C# form),
    /// the exit's rect (Std = HeroGeometry's back point; null = none), the never-click buttons by name, why, and the rects of the never-click
    /// buttons near the exit ({x0 anchor, x0, x1 anchor, x1, y0 anchor, y0, y1 anchor, y1} in U, OfferForms' format).</summary>
    public sealed class Entry {
      public readonly string Name, Esc, Why; public readonly char Class; public readonly object[] Rect; public readonly string[] Never; public readonly object[][] NearNever;
      public Entry(string name, char cls, string esc, object[] rect, string never, string why, params object[][] near) {
        Name = name; Class = cls; Esc = esc; Rect = rect; Why = why;
        Never = string.IsNullOrEmpty(never) ? new string[0] : never.Split(',');
        NearNever = near ?? new object[0][];
      }
      public bool StdBack { get { return ReferenceEquals(Rect, Std); } }
    }
    /// <summary>The back arrow at HeroGeometry's spot top left (the standard Button_Back of the game).</summary>
    public static readonly object[] Std = new object[0];
    static object[] R(char a0, double x0, char a1, double x1, char b0, double y0, char b1, double y1) { return new object[] { a0, x0, a1, x1, b0, y0, b1, y1 }; }

    static Dictionary<string, Entry> byName;
    static Dictionary<string, Entry> Map() {
      var m = byName;
      if (m == null) { m = new Dictionary<string, Entry>(); foreach (var e in Data) m[e.Name] = e; byName = m; }
      return m;
    }
    public static IEnumerable<Entry> All { get { return Data; } }
    public static int Count { get { return Data.Length; } }

    public static Entry ByName(string name) { Entry e; return name != null && Map().TryGetValue(name, out e) ? e : null; }
    /// <summary>The form's class letter; 'U' for a form not in the map (or an unreadable "id:" name).</summary>
    public static char ClassOf(string name) { var e = ByName(name); return e != null ? e.Class : 'U'; }
    public static string ClassName(char c) {
      switch (c) {
        case 'F': return "back"; case 'P': return "close"; case 'E': return "escape"; case 'O': return "overlay";
        case 'D': return "dangerous"; case 'W': return "wait"; case 'K': return "known";
      }
      return "unknown";
    }

    // which of several shown forms is on top: the window table has no order, so the one that decides most safely wins:
    // a dangerous one, then a fight / loading, a pop-up with its ×, one closed by Escape, a full screen with its back arrow, then the rest
    static readonly string Rank = "DWPEFKU";
    /// <summary>The top form of a comma list of shown forms (overlays left out); "" = none.</summary>
    public static string Top(string list) {
      if (string.IsNullOrEmpty(list)) return "";
      string best = ""; int br = int.MaxValue;
      foreach (var raw in list.Split(',')) {
        string n = raw.Trim(); if (n == "") continue;
        char c = ClassOf(n); if (c == 'O') continue;
        int r = Rank.IndexOf(c); if (r < 0) r = Rank.Length;
        if (r < br) { br = r; best = n; }
      }
      return best;
    }

    /// <summary>A form shown when a known screen came counts as a part of that screen (the base: no pop-up on top), except a dangerous dialog or a
    /// wait: those are never a screen's own part (a buy dialog already open when the pilot starts is on top, not under the screen's buttons).</summary>
    public static bool MayBeBase(string name) { char c = ClassOf(name); return c != 'D' && c != 'W'; }
    /// <summary>A form that comes a moment AFTER the known screen (within its settle time) counts as a part the game builds late only when the map
    /// does not call it a screen or a pop-up of its own: a back-arrow screen, a close-button or Escape pop-up opened then lies on top (a reward
    /// shown as the hero screen opens took the pilot's grid clicks), as do a dangerous dialog and a wait.</summary>
    /// <summary>The forms of a fresh window table read that the last read did not have and the map calls a screen, a pop-up, a dangerous dialog or a
    /// wait: one that came since (the table is read every 2 s; a pop-up a moment before a click lies on top). "" = none; a joined list otherwise.</summary>
    public static string Newcomers(ICollection<string> last, ICollection<string> fresh) {
      if (last == null || fresh == null) return "";
      var l = new List<string>();
      foreach (var n in fresh) if (!last.Contains(n) && "FPEDW".IndexOf(ClassOf(n)) >= 0) l.Add(n);
      return string.Join(",", l.ToArray());
    }
    public static bool MayBeLatePart(string name) { char c = ClassOf(name); return MayBeBase(name) && c != 'F' && c != 'P' && c != 'E'; }

    static double X(char a, double off, double W, double U) { return (a == 'L' ? 0 : a == 'C' ? W / 2 : W) + off * U; }
    static double Y(char a, double off, double H, double U) { return (a == 'T' ? 0 : a == 'M' ? H / 2 : H) + off * U; }
    /// <summary>A rect of the table in client px.</summary>
    public static GeoRect Place(object[] f, double W, double H, double U) {
      double x0 = X((char)f[0], (double)f[1], W, U), x1 = X((char)f[2], (double)f[3], W, U), y0 = Y((char)f[4], (double)f[5], H, U), y1 = Y((char)f[6], (double)f[7], H, U);
      return new GeoRect { X = Math.Min(x0, x1), Y = Math.Min(y0, y1), W = Math.Abs(x1 - x0), H = Math.Abs(y1 - y0) };
    }

    /// <summary>The form's exit button in client px (the standard back arrow: a small square round HeroGeometry's point); null = no button.</summary>
    public static GeoRect ExitRect(string name, double W, double H, double U) {
      var e = ByName(name); if (e == null || e.Rect == null || (e.Class != 'F' && e.Class != 'P')) return null;
      if (e.StdBack) { var g = new HeroGeometry(); double t = ClickGate.UnknownBackTol * U; return new GeoRect { X = g.BackX * U - t, Y = g.BackY * U - t, W = 2 * t, H = 2 * t }; }
      return Place(e.Rect, W, H, U);
    }
    /// <summary>The click point of the exit (its centre); null = none.</summary>
    public static GeoRect ExitPoint(string name, double W, double H, double U) {
      var r = ExitRect(name, W, H, U); if (r == null) return null;
      return new GeoRect { X = r.CX, Y = r.CY, W = 0, H = 0 };
    }
    /// <summary>The point lies in the inner part of the exit button (a fifth of its size off each edge).</summary>
    public static bool OnExit(string name, double x, double y, double W, double H, double U) {
      var r = ExitRect(name, W, H, U); if (r == null) return false;
      double mx = r.W / 5, my = r.H / 5;
      return x >= r.X + mx && x <= r.Right - mx && y >= r.Y + my && y <= r.Bottom - my;
    }
    /// <summary>The point lies on one of the form's never-click buttons near its exit (buy / go / claim / fight ...).</summary>
    public static bool OnNever(string name, double x, double y, double W, double H, double U) {
      var e = ByName(name); if (e == null) return false;
      foreach (var f in e.NearNever) {
        var r = Place(f, W, H, U);
        if (x >= r.X && x <= r.Right && y >= r.Y && y <= r.Bottom) return true;
      }
      return false;
    }
  }
}
