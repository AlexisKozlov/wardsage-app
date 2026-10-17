// Wardsage - screen geometry from the site for the running game version: GET {site}/api/extractor/app-geometry?v=<version>
// with Authorization: Bearer <sync code>. It is the rectangles of the game's screens (the Foundry's tabs, lists and buttons,
// the Inventory's buttons ...) in units of the game window's height, with edges as [anchor, offset]:
//   200 {"game":"<version>","v":1,"rects":{key:{"x0":["L"|"C"|"R",n],"x1":[...],"y0":["T"|"M"|"B",n],"y1":[...],"w":n,"h":n}},"grids":{key:{"cols":n,"lineSize":n,"padding":[l,t,r,b]}}}
//   404 = no data for that version (yet); 401/403 = the code is not (or no longer) valid: asked again later.
// A rectangle may carry "shape" ({"k":"circle"} | {"k":"bevel","c","cx","cy"} | {"k":"round","r"}, fractions of the visible art's height) and "inset" {l,t,r,b}
// (the visible art inside it, fractions of the sides, negative = sticks out); both optional (src/GuideShape.cs).
// Kept per game version in the app's data folder (geometry\<version>.json) like the layout data (src/Layout.cs) and used while
// the site cannot be reached. Without it «Показать в игре» keeps its text hints and the app clicks nothing in the Foundry:
// a missing or broken file never stops anything.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace RealmForge {
  /// <summary>A rectangle in client pixels.</summary>
  public sealed class GeoRect {
    public double X, Y, W, H;
    public double Right { get { return X + W; } }
    public double Bottom { get { return Y + H; } }
    public double CX { get { return X + W / 2; } }
    public double CY { get { return Y + H / 2; } }
  }

  /// <summary>The numbers of a list grid (canvas units: 640 = one UI unit).</summary>
  public sealed class GeoGrid {
    public int Cols;
    public double LineSize;     // one row, canvas units
    public double PadTop;       // the grid's top padding, canvas units (can be negative)
  }

  /// <summary>The geometry of one game version (immutable once parsed).</summary>
  public sealed class GameGeometry {
    public const int Format = 1;
    const int MaxBytes = 128 * 1024;
    /// <summary>The canvas height the numbers of grids are in (one UI unit).</summary>
    public const double CanvasH = 640.0;

    struct Edge { public char A; public double Off; }
    sealed class Box { public Edge X0, X1, Y0, Y1; public double W, H; public GeoShape Shape; public GeoInset Inset; }

    public string Game { get; private set; }
    readonly Dictionary<string, Box> rects = new Dictionary<string, Box>();
    readonly Dictionary<string, GeoGrid> grids = new Dictionary<string, GeoGrid>();

    GameGeometry() { }

    public int Count { get { return rects.Count; } }

    /// <summary>The data from the site's reply or the saved copy; null when it is not for <paramref name="game"/>, not this
    /// format, or has a rectangle that is not well formed.</summary>
    public static GameGeometry Parse(string json, string game) {
      if (string.IsNullOrEmpty(json) || json.Length > MaxBytes || !GameLayout.IsVersion(game)) return null;
      var o = MiniJson.AsObject(MiniJson.TryParse(json));
      if (o == null) return null;
      object v;
      if (!o.TryGetValue("game", out v) || !(v is string) || (string)v != game) return null;
      if (!o.TryGetValue("v", out v) || !(v is double) || (double)v != Format) return null;
      var rs = o.TryGetValue("rects", out v) ? MiniJson.AsObject(v) : null;
      if (rs == null || rs.Count == 0) return null;
      var g = new GameGeometry { Game = game };
      foreach (var kv in rs) {
        var r = MiniJson.AsObject(kv.Value);
        if (r == null) return null;
        var b = new Box();
        if (!ReadEdge(r, "x0", "LCR", out b.X0) || !ReadEdge(r, "x1", "LCR", out b.X1) || !ReadEdge(r, "y0", "TMB", out b.Y0) || !ReadEdge(r, "y1", "TMB", out b.Y1)) return null;
        if (!ReadNum(r, "w", out b.W) || !ReadNum(r, "h", out b.H)) return null;
        b.Shape = ReadShape(r); b.Inset = ReadInset(r);   // both optional: missing or odd = a rectangle, no inset
        g.rects[kv.Key] = b;
      }
      object gv;
      var gs = o.TryGetValue("grids", out gv) ? MiniJson.AsObject(gv) : null;
      if (gs != null) {
        foreach (var kv in gs) {
          var r = MiniJson.AsObject(kv.Value);
          double cols, line;
          if (r == null || !ReadNum(r, "cols", out cols) || !ReadNum(r, "lineSize", out line) || cols < 1 || cols > 20 || line <= 0 || line > 2000) continue;   // a bad grid is left out
          var gr = new GeoGrid { Cols = (int)cols, LineSize = line };
          object pv;
          var pad = r.TryGetValue("padding", out pv) ? pv as List<object> : null;
          if (pad != null && pad.Count >= 2 && pad[1] is double) gr.PadTop = (double)pad[1];
          g.grids[kv.Key] = gr;
        }
      }
      return g;
    }

    static GeoShape ReadShape(Dictionary<string, object> r) {
      object v;
      var s = r.TryGetValue("shape", out v) ? MiniJson.AsObject(v) : null;
      string k = s != null ? MiniJson.GetString(s, "k") : null;
      if (k != "circle" && k != "bevel" && k != "round") return null;
      var sh = new GeoShape { K = k };
      double c = 0, d;
      if (ReadNum(s, "c", out d)) c = d;
      sh.CX = ReadNum(s, "cx", out d) ? d : c; sh.CY = ReadNum(s, "cy", out d) ? d : c;
      if (ReadNum(s, "r", out d)) sh.R = d;
      if (Math.Abs(sh.CX) > 1 || Math.Abs(sh.CY) > 1 || Math.Abs(sh.R) > 1) return null;
      return sh;
    }

    static GeoInset ReadInset(Dictionary<string, object> r) {
      object v;
      var s = r.TryGetValue("inset", out v) ? MiniJson.AsObject(v) : null;
      if (s == null) return null;
      var i = new GeoInset();
      if (!ReadNum(s, "l", out i.L) | !ReadNum(s, "t", out i.T) | !ReadNum(s, "r", out i.R) | !ReadNum(s, "b", out i.B)) return null;
      return Math.Abs(i.L) > 1 || Math.Abs(i.T) > 1 || Math.Abs(i.R) > 1 || Math.Abs(i.B) > 1 || i.L + i.R >= 1 || i.T + i.B >= 1 ? null : i;
    }

    static bool ReadNum(Dictionary<string, object> r, string key, out double d) {
      object v; d = 0;
      if (!r.TryGetValue(key, out v) || !(v is double)) return false;
      d = (double)v;
      return !double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) <= 5000;
    }

    static bool ReadEdge(Dictionary<string, object> r, string key, string anchors, out Edge e) {
      e = new Edge(); object v;
      if (!r.TryGetValue(key, out v)) return false;
      var l = v as List<object>;
      if (l == null || l.Count != 2 || !(l[0] is string) || !(l[1] is double)) return false;
      string a = (string)l[0]; double off = (double)l[1];
      if (a.Length != 1 || anchors.IndexOf(a[0]) < 0 || double.IsNaN(off) || Math.Abs(off) > 100) return false;
      e.A = a[0]; e.Off = off;
      return true;
    }

    public bool Has(string key) { return rects.ContainsKey(key); }

    /// <summary>The rectangle <paramref name="key"/> on a client of W×H pixels with the UI unit U; null = no such entry.
    /// x: L = from the left edge, C = from the centre, R = from the right edge; y: T = from the top, M = from the middle,
    /// B = from the bottom (all offsets in units of U).</summary>
    public GeoRect Rect(string key, double W, double H, double U) {
      Box b;
      if (key == null || !rects.TryGetValue(key, out b)) return null;
      double x0 = X(b.X0, W, U), x1 = X(b.X1, W, U), y0 = Y(b.Y0, H, U), y1 = Y(b.Y1, H, U);
      return new GeoRect { X = Math.Min(x0, x1), Y = Math.Min(y0, y1), W = Math.Abs(x1 - x0), H = Math.Abs(y1 - y0) };
    }

    /// <summary>A rectangle's own width and height in units of U (0,0 = no such entry).</summary>
    public double[] Dims(string key) {
      Box b;
      return key != null && rects.TryGetValue(key, out b) ? new[] { b.W, b.H } : new double[] { 0, 0 };
    }

    /// <summary>The form of the key's visible art (null = a rectangle).</summary>
    public GeoShape Shape(string key) { Box b; return key != null && rects.TryGetValue(key, out b) ? b.Shape : null; }
    /// <summary>The visible art inside the key's rectangle (null = all of it).</summary>
    public GeoInset Inset(string key) { Box b; return key != null && rects.TryGetValue(key, out b) ? b.Inset : null; }

    public GeoGrid Grid(string key) { GeoGrid g; return key != null && grids.TryGetValue(key, out g) ? g : null; }

    static double X(Edge e, double W, double U) { return (e.A == 'L' ? 0 : e.A == 'C' ? W / 2 : W) + e.Off * U; }
    static double Y(Edge e, double H, double U) { return (e.A == 'T' ? 0 : e.A == 'M' ? H / 2 : H) + e.Off * U; }
  }

  /// <summary>The geometry in force (the running game version's), fetched from the site and kept on disk.</summary>
  public static class Geo {
    static volatile GameGeometry current;

    /// <summary>The data for the running game version, null = none (the Foundry steps stay text hints).</summary>
    public static GameGeometry Current { get { return current; } }
    public static void Use(GameGeometry g) { current = g; }

    /// <summary>A rectangle of the geometry in force on a client of W×H pixels (U = Ui.Unit); null = no data or no such entry.</summary>
    public static GeoRect Rect(string key, double W, double H, double U) { var g = current; return g == null ? null : g.Rect(key, W, H, U); }

    public static string CacheDir { get { return Path.Combine(AppConfig.DefaultDir, "geometry"); } }
    static string CachePath(string dir, string game) { return Path.Combine(dir, game + ".json"); }

    /// <summary>The saved copy for <paramref name="game"/>, or null (none, unreadable, not valid).</summary>
    public static GameGeometry LoadCache(string dir, string game) {
      if (!GameLayout.IsVersion(game)) return null;
      try {
        string p = CachePath(dir, game);
        if (File.Exists(p)) { string txt = File.ReadAllText(p, Encoding.UTF8); var r = GameGeometry.Parse(txt, game); if (r != null) Keep(game, txt); return r; }
      } catch (Exception) { }
      return null;
    }

    // the raw text of the data loaded from the site or the saved copy, kept in memory (the recorder needs it even when the file is not there)
    static readonly Dictionary<string, string> keptText = new Dictionary<string, string>();
    public static void Keep(string game, string json) { if (game == null || string.IsNullOrEmpty(json)) return; lock (keptText) keptText[game] = json; }
    static string Kept(string game) { string s; lock (keptText) return game != null && keptText.TryGetValue(game, out s) ? s : null; }

    /// <summary>The saved copy's text as it is (the recorder keeps it with a recording), null when there is none.</summary>
    public static string RawCache(string game) {
      if (!GameLayout.IsVersion(game)) return null;
      string kept = Kept(game); if (kept != null) return kept;
      try { string p = CachePath(CacheDir, game); return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null; } catch (Exception) { return null; }
    }

    public static void SaveCache(string dir, string game, string json) {
      if (!GameLayout.IsVersion(game)) return;
      try {
        Directory.CreateDirectory(dir);
        string path = CachePath(dir, game), tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
      } catch (Exception) { }
    }

    /// <summary>What a reply means (same meaning as Layout.Reply): Got, None (no data for this version), Denied (no valid sync
    /// code), NoReply (no connection / an error / something odd: what is held stays).</summary>
    public static Layout.Reply Interpret(int code, string body, string game, out GameGeometry geometry) {
      geometry = null;
      if (code == 404) return Layout.Reply.None;
      if (code == 401 || code == 403) return Layout.Reply.Denied;
      if (code < 200 || code >= 300) return Layout.Reply.NoReply;
      geometry = GameGeometry.Parse(body, game);
      if (geometry != null) Keep(game, body);
      return geometry != null ? Layout.Reply.Got : Layout.Reply.NoReply;
    }

    public static Layout.Reply Fetch(string site, string code, string game, out GameGeometry geometry, out string json) {
      geometry = null; json = null;
      if (!GameLayout.IsVersion(game) || string.IsNullOrEmpty(site)) return Layout.Reply.NoReply;
      if (!SyncClient.IsValidCode(code)) return Layout.Reply.Denied;
      try {
        SyncClient.EnableTls12();
        var req = (HttpWebRequest)WebRequest.Create(site.TrimEnd('/') + "/api/extractor/app-geometry?v=" + Uri.EscapeDataString(game));
        req.Method = "GET";
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Headers["Authorization"] = "Bearer " + code;
        req.Timeout = 10000;
        req.ReadWriteTimeout = 10000;
        req.AllowAutoRedirect = false;
        HttpWebResponse resp;
        try { resp = (HttpWebResponse)req.GetResponse(); }
        catch (WebException e) {
          resp = e.Response as HttpWebResponse;
          if (resp == null) return Layout.Reply.NoReply;
        }
        using (resp)
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream()) {
          var buf = new byte[8192]; int n;
          while ((n = s.Read(buf, 0, buf.Length)) > 0) {
            ms.Write(buf, 0, n);
            if (ms.Length > 128 * 1024) return Layout.Reply.NoReply;
          }
          string body = Encoding.UTF8.GetString(ms.ToArray());
          var r = Interpret((int)resp.StatusCode, body, game, out geometry);
          if (r == Layout.Reply.Got) json = body;
          return r;
        }
      } catch (Exception) { return Layout.Reply.NoReply; }
    }
  }
}
