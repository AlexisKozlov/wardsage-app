// Wardsage - the outline of a button for the new highlight (dimming with a hole + gold edge, app/Overlay.cs). Pure math, no Windows APIs.
// The site's geometry gives each button a "shape" (circle | bevel | round | rect, sizes as fractions of the visible art's height) and an
// optional "inset" (the visible art inside the rectangle, fractions of the rectangle's sides; negative = the art sticks out).
using System;
using System.Collections.Generic;

namespace RealmForge {
  /// <summary>A point in client pixels (no System.Drawing here: the core is built without it).</summary>
  public struct GuidePt {
    public double X, Y;
    public GuidePt(double x, double y) { X = x; Y = y; }
  }

  /// <summary>The form of a button's visible art.</summary>
  public sealed class GeoShape {
    public string K = "rect";        // circle | bevel | round | rect
    public double CX, CY;            // bevel: the cuts, fractions of the art's height (horizontal / vertical)
    public double R;                 // round: the corner radius, fraction of the art's height
  }

  /// <summary>The visible art inside a rectangle: how far each side is moved in, fractions of the rectangle's width (l, r) and height (t, b).</summary>
  public sealed class GeoInset {
    public double L, T, R, B;
  }

  public static class GuideShape {
    public const int CircleSides = 48, CornerSteps = 6;

    /// <summary>The visible art's box: the rectangle moved in by the inset (none = the rectangle itself).</summary>
    public static GeoRect ArtBox(GeoRect r, GeoInset inset) {
      if (inset == null) return new GeoRect { X = r.X, Y = r.Y, W = r.W, H = r.H };
      return new GeoRect { X = r.X + inset.L * r.W, Y = r.Y + inset.T * r.H, W = r.W * (1 - inset.L - inset.R), H = r.H * (1 - inset.T - inset.B) };
    }

    /// <summary>The same box grown by <paramref name="m"/> px on every side.</summary>
    public static GeoRect Grow(GeoRect r, double m) { return new GeoRect { X = r.X - m, Y = r.Y - m, W = r.W + 2 * m, H = r.H + 2 * m }; }

    /// <summary>The outline of the art as a closed polygon (clockwise from the top left, the last point is not repeated).
    /// A missing shape or an unknown kind is a rectangle.</summary>
    public static List<GuidePt> Outline(GeoRect rect, GeoShape shape, GeoInset inset) {
      var a = ArtBox(rect, inset);
      var pts = new List<GuidePt>();
      if (a.W <= 0 || a.H <= 0) return pts;
      string k = shape != null ? shape.K : "rect";
      double l = a.X, t = a.Y, r = a.Right, b = a.Bottom;
      if (k == "circle") {
        double rad = Math.Min(a.W, a.H) / 2;
        for (int i = 0; i < CircleSides; i++) {
          double ang = -Math.PI / 2 + 2 * Math.PI * i / CircleSides;
          pts.Add(new GuidePt(a.CX + rad * Math.Cos(ang), a.CY + rad * Math.Sin(ang)));
        }
      } else if (k == "bevel") {
        double cx = Math.Max(0, Math.Min(a.W / 2, shape.CX * a.H)), cy = Math.Max(0, Math.Min(a.H / 2, shape.CY * a.H));
        pts.Add(new GuidePt(l + cx, t)); pts.Add(new GuidePt(r - cx, t)); pts.Add(new GuidePt(r, t + cy)); pts.Add(new GuidePt(r, b - cy));
        pts.Add(new GuidePt(r - cx, b)); pts.Add(new GuidePt(l + cx, b)); pts.Add(new GuidePt(l, b - cy)); pts.Add(new GuidePt(l, t + cy));
      } else if (k == "round") {
        double rad = Math.Max(0, Math.Min(Math.Min(a.W, a.H) / 2, shape.R * a.H));
        if (rad < 0.5) { Rect4(pts, l, t, r, b); return pts; }
        double[] cxs = { r - rad, r - rad, l + rad, l + rad }, cys = { t + rad, b - rad, b - rad, t + rad };
        for (int c = 0; c < 4; c++)
          for (int s = 0; s <= CornerSteps; s++) {
            double ang = -Math.PI / 2 + Math.PI / 2 * (c + (double)s / CornerSteps);
            pts.Add(new GuidePt(cxs[c] + rad * Math.Cos(ang), cys[c] + rad * Math.Sin(ang)));
          }
      } else Rect4(pts, l, t, r, b);
      return pts;
    }

    /// <summary>The old frame along a button's shape: the outline of the art (inset applied) grown by <paramref name="margin"/> px, or null when the shape is a rectangle, missing or unknown (the old rectangle frame stays).</summary>
    public static List<GuidePt> FrameOutline(GeoRect rect, GeoShape shape, GeoInset inset, double margin) {
      if (rect == null || shape == null || (shape.K != "circle" && shape.K != "bevel" && shape.K != "round")) return null;
      var pts = Outline(Grow(ArtBox(rect, inset), margin), shape, null);
      return pts.Count >= 3 ? pts : null;
    }

    static void Rect4(List<GuidePt> pts, double l, double t, double r, double b) {
      pts.Add(new GuidePt(l, t)); pts.Add(new GuidePt(r, t)); pts.Add(new GuidePt(r, b)); pts.Add(new GuidePt(l, b));
    }

    /// <summary>Where the hand picture of hw × hh px goes: its top-left so that the fingertip (at <paramref name="fx"/>, <paramref name="fy"/> of the picture)
    /// is at (<paramref name="tipX"/>, <paramref name="tipY"/>), kept inside a client of w × h.</summary>
    public static GuidePt HandPos(double tipX, double tipY, double hw, double hh, double fx, double fy, double w, double h) {
      double x = Math.Max(0, Math.Min(w - hw, tipX - hw * fx)), y = Math.Max(0, Math.Min(h - hh, tipY - hh * fy));
      return new GuidePt(x, y);
    }
  }
}
