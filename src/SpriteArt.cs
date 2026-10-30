// Wardsage - a UI sprite's visible art inside its rect, and the frame it gets on the screen. Pure (tests/CoreTests.cs).
//
// Many buttons draw their visible shape inside a larger sprite with transparent padding and decorations (the city bar's «Инвентарь»:
// a round plate with a pointed tip and side lines in a 108 x 88 sprite), so the RectTransform's rect is not what a frame should hug.
// tools/sprite-bounds.py measures every sprite the UI graph's forms draw (src/SpriteArtData.cs, per game version); the live read
// (src/UiButtons.cs) gives the Image's sprite name, type and preserveAspect with the rect; this maps the sprite's art through the rect
// the way the Image draws it (Simple / Filled stretched, preserveAspect letterboxed on the pivot, Sliced with fixed borders) and gives
// the frame (the visible box, FramePad px out) and its outline (the sprite's contour, else circle / rounded rect).
//
// Data line: name|w|h|ppu|border l,b,r,t (px)|visible box l,t,r,b (fractions, y down)|kind c/r/x/p|round radius (of the box height)
//            |circle cx,cy,d (the visible edge)|circle cx,cy,d (the solid body)|contour x,y pairs (0..1000 of the rect)
// (circle: cx of the width, cy and d of the height.)
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RealmForge {
  public static partial class SpriteArt {
    /// <summary>One sprite's art.</summary>
    public sealed class Art {
      public string Name = ""; public double W, H, Ppu = 100;
      public double BL, BB, BR, BT;          // the 9-slice border, px
      public double L, T, R, B;              // the visible box, fractions of the rect (y down)
      public char Kind = 'x';                // c circle, r rounded rect, x rect, p irregular
      public double Round;                   // r: the corner radius, fraction of the box height
      public double[] Circle, Core;          // {cx, cy, d}: the visible edge / the solid body (null = none fits)
      public double[] Poly;                  // the contour: x, y pairs, fractions of the rect (null = none)
    }

    /// <summary>How the Image draws its sprite: m_Type (0 Simple, 1 Sliced, 2 Tiled, 3 Filled), m_PreserveAspect, m_PixelsPerUnitMultiplier,
    /// the rect's pivot (y up, Unity's), client px per local unit of the rect (0 = unknown: a Sliced sprite is then stretched) and a flip.</summary>
    public sealed class Draw {
      public int Type; public bool Preserve; public double Ppum = 1, PivX = 0.5, PivY = 0.5, Px; public bool FlipX, FlipY;
    }

    /// <summary>The frame of a sprite drawn into a rect: Frame {x, y, w, h, corner radius} (the visible box FramePad px out, within the client),
    /// Box (the visible box, client px) and Shape (its outline: poly / circle / round / rect).</summary>
    public sealed class Fit {
      public double[] Frame; public GeoRect Box; public GeoShape Shape; public string Kind = "rect";
    }

    static Dictionary<string, Art> byName;
    static readonly object gate = new object();

    /// <summary>The art of sprite <paramref name="name"/>, null when the table does not have it.</summary>
    public static Art Of(string name) {
      if (string.IsNullOrEmpty(name)) return null;
      lock (gate) {
        if (byName == null) {
          var d = new Dictionary<string, Art>(StringComparer.Ordinal);
          foreach (var line in Data) { var a = Parse(line); if (a != null && !d.ContainsKey(a.Name)) d[a.Name] = a; }
          byName = d;
        }
      }
      Art r; return byName.TryGetValue(name, out r) ? r : null;
    }
    public static int Count { get { Of("-"); return byName.Count; } }

    static double[] Nums(string s, double scale = 1) {
      if (string.IsNullOrEmpty(s)) return null;
      var p = s.Split(','); var r = new double[p.Length];
      for (int i = 0; i < p.Length; i++) if (!double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i])) return null; else r[i] /= scale;
      return r;
    }

    /// <summary>One data line -> its art (null = malformed).</summary>
    public static Art Parse(string line) {
      var f = (line ?? "").Split('|'); if (f.Length < 11) return null;
      var wh = Nums(f[1] + "," + f[2] + "," + f[3]); var b = Nums(f[4]); var box = Nums(f[5]); double rr;
      if (wh == null || b == null || b.Length != 4 || box == null || box.Length != 4 || f[6].Length != 1) return null;
      double.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out rr);
      var a = new Art { Name = f[0], W = wh[0], H = wh[1], Ppu = wh[2] > 0 ? wh[2] : 100, BL = b[0], BB = b[1], BR = b[2], BT = b[3],
        L = box[0], T = box[1], R = box[2], B = box[3], Kind = f[6][0], Round = rr, Circle = Nums(f[8]), Core = Nums(f[9]), Poly = Nums(f[10], 1000) };
      if (a.Circle != null && a.Circle.Length != 3) a.Circle = null;
      if (a.Core != null && a.Core.Length != 3) a.Core = null;
      if (a.Poly != null && (a.Poly.Length < 6 || a.Poly.Length % 2 != 0)) a.Poly = null;
      return a;
    }

    /// <summary>The rect the Image draws into: preserveAspect (Simple / Filled) fits the sprite's aspect inside the rect, the spare room
    /// shared on the pivot (Unity's Image.GetDrawingDimensions); else the rect. x, y top left, client px (y down).</summary>
    public static double[] Drawn(Art a, Draw d, double x, double y, double w, double h) {
      if (d == null || !d.Preserve || (d.Type != 0 && d.Type != 3) || a.W <= 0 || a.H <= 0 || w <= 0 || h <= 0) return new[] { x, y, w, h };
      double sr = a.W / a.H, rr = w / h;
      if (sr > rr) { double nh = w / sr; return new[] { x, y + (h - nh) * (1 - d.PivY), w, nh }; }   // (the pivot is y up: its share goes below)
      double nw = h * sr; return new[] { x + (w - nw) * d.PivX, y, nw, h };
    }

    /// <summary>A Sliced axis: sprite px <paramref name="p"/> of <paramref name="S"/> with borders b0 (start) / b1 (end) into <paramref name="len"/>
    /// client px, k client px per sprite px for the borders (scaled down together when they do not fit, as Unity does).</summary>
    public static double SliceAxis(double p, double S, double b0, double b1, double len, double k) {
      double d0 = b0 * k, d1 = b1 * k;
      if (d0 + d1 > len && d0 + d1 > 0) { double f = len / (d0 + d1); d0 *= f; d1 *= f; }
      if (p <= b0) return b0 > 0 ? p / b0 * d0 : 0;
      if (p >= S - b1) return b1 > 0 ? len - (S - p) / b1 * d1 : len;
      double mid = S - b0 - b1; return mid > 0 ? d0 + (p - b0) / mid * (len - d0 - d1) : d0;
    }

    /// <summary>The point (u, v) of the sprite (fractions of its rect, y down) where the Image draws it in the rect x, y, w, h (client px).</summary>
    public static GuidePt Map(Art a, Draw d, double x, double y, double w, double h, double u, double v) {
      if (d != null && d.FlipX) u = 1 - u;
      if (d != null && d.FlipY) v = 1 - v;
      var r = Drawn(a, d, x, y, w, h);
      bool sliced = d != null && d.Type == 1 && d.Px > 0 && (a.BL > 0 || a.BR > 0 || a.BT > 0 || a.BB > 0);
      if (!sliced) return new GuidePt(r[0] + u * r[2], r[1] + v * r[3]);
      double k = 100.0 / (a.Ppu * (d.Ppum > 0 ? d.Ppum : 1)) * d.Px;
      return new GuidePt(r[0] + SliceAxis(u * a.W, a.W, a.BL, a.BR, r[2], k), r[1] + SliceAxis(v * a.H, a.H, a.BT, a.BB, r[3], k));
    }

    /// <summary>The frame of sprite art <paramref name="a"/> drawn as <paramref name="d"/> into the rect x, y, w, h (client px) of a W x H client:
    /// the visible box FramePad px out; the outline the sprite's contour (poly), else its circle (c), rounded rect (r) or the box.</summary>
    public static Fit FrameOf(Art a, Draw d, double x, double y, double w, double h, double W, double H) {
      if (a == null || w <= 0 || h <= 0) return null;
      var p0 = Map(a, d, x, y, w, h, a.L, a.T); var p1 = Map(a, d, x, y, w, h, a.R, a.B);
      double bx = Math.Min(p0.X, p1.X), by = Math.Min(p0.Y, p1.Y), bw = Math.Abs(p1.X - p0.X), bh = Math.Abs(p1.Y - p0.Y);
      if (bw < 2 || bh < 2) return null;
      var fit = new Fit { Box = new GeoRect { X = bx, Y = by, W = bw, H = bh } };
      if (a.Poly != null) {
        var pts = new double[a.Poly.Length];
        for (int i = 0; i < pts.Length; i += 2) {
          var q = Map(a, d, x, y, w, h, a.Poly[i], a.Poly[i + 1]);
          pts[i] = (q.X - bx) / bw; pts[i + 1] = (q.Y - by) / bh;
        }
        fit.Shape = new GeoShape { K = "poly", P = pts }; fit.Kind = "poly";
      } else if (a.Kind == 'c') { fit.Shape = new GeoShape { K = "circle" }; fit.Kind = "circle"; }
      else if (a.Kind == 'r') { fit.Shape = new GeoShape { K = "round", R = a.Round }; fit.Kind = "round"; }
      else fit.Shape = new GeoShape { K = "rect" };
      double pad = UiIcon.FramePad;
      double l = Math.Max(0, bx - pad), t = Math.Max(0, by - pad), r = Math.Min(W, bx + bw + pad), b = Math.Min(H, by + bh + pad);
      double rad = a.Kind == 'c' ? Math.Min(r - l, b - t) / 2 : a.Kind == 'r' ? a.Round * bh + pad : Math.Min(r - l, b - t) * 0.18;
      fit.Frame = new[] { l, t, r - l, b - t, Math.Round(rad) };
      return fit;
    }

    /// <summary>The test build's line of a framed target: "frame check: who sprite=name shape=k frame=[x,y,w,h] visible=[x,y,w,h]" (box = the
    /// visible art, client px); no art: "sprite=name|none (why) shape=rect". <paramref name="where"/> (the client's top left and size and its monitor,
    /// screen px: " client=[x,y,w,h] screen=[x,y,w,h]") lets tools/lab/frames-sheet.sh find the frame on the lab's screenshot.</summary>
    public static string CheckLine(string who, string sprite, Draw d, Fit f, double[] frame, string why = null, string where = null) {
      Func<double, string> n0 = v => Math.Round(v).ToString(CultureInfo.InvariantCulture);
      string s = "frame check: " + who + " sprite=" + (string.IsNullOrEmpty(sprite) ? "none" + (why != null ? " (" + why + ")" : "") : sprite);
      if (d != null && !string.IsNullOrEmpty(sprite)) s += " type=" + (d.Type == 0 ? "simple" : d.Type == 1 ? "sliced" : d.Type == 2 ? "tiled" : "filled") + (d.Preserve ? " preserve" : "");
      s += " shape=" + (f != null ? f.Kind : "rect");
      if (frame != null) s += " frame=[" + n0(frame[0]) + "," + n0(frame[1]) + "," + n0(frame[2]) + "," + n0(frame[3]) + "]";
      if (f != null) s += " visible=[" + n0(f.Box.X) + "," + n0(f.Box.Y) + "," + n0(f.Box.W) + "," + n0(f.Box.H) + "]";
      return s + (where ?? "");
    }
  }

  /// <summary>The outlines of the live frames drawn lately (client px), found again by the frame rect the hint is drawn with (within 1.5 px,
  /// at most <see cref="KeepMs"/> old): the hint code passes only the rect, the outline comes from here. Pure.</summary>
  public sealed class LiveShapes {
    public const int KeepMs = 2000, Max = 16;
    sealed class E { public double[] F; public GeoRect Box; public GeoShape Shape; public long At; }
    readonly List<E> es = new List<E>();
    public void Put(double[] frame, GeoRect box, GeoShape shape, long now) {
      if (frame == null || box == null || shape == null) return;
      es.RemoveAll(e => now - e.At > KeepMs || now < e.At || Near(e.F, frame[0], frame[1], frame[2], frame[3]));
      es.Add(new E { F = frame, Box = box, Shape = shape, At = now });
      if (es.Count > Max) es.RemoveAt(0);
    }
    static bool Near(double[] f, double x, double y, double w, double h) {
      return Math.Abs(f[0] - x) <= 1.5 && Math.Abs(f[1] - y) <= 1.5 && Math.Abs(f[2] - w) <= 1.5 && Math.Abs(f[3] - h) <= 1.5;
    }
    /// <summary>The outline of the frame drawn at x, y, w, h (client px); false = none known (the plain frame).</summary>
    public bool Find(double x, double y, double w, double h, long now, out GeoRect box, out GeoShape shape) {
      box = null; shape = null;
      for (int i = es.Count - 1; i >= 0; i--) {
        var e = es[i];
        if (now - e.At > KeepMs || now < e.At || !Near(e.F, x, y, w, h)) continue;
        box = e.Box; shape = e.Shape; return true;
      }
      return false;
    }
  }
}
