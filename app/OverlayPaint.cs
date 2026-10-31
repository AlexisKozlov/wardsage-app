// Wardsage app - the overlay's drawing code (hint frame, owl, step plate, scroll strip) as static functions that paint into any Graphics: the
// overlay windows use them (app/Overlay.cs) and so does the screen recorder, which draws the same elements onto its captured frames (Composite).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace RealmForge {
  static class OverlayPaint {
    /// <summary>What the new highlight outlines: the art's rectangle (screen px) with its shape and inset; Important = variant B.</summary>
    internal sealed class Hl { public GeoRect Rect; public GeoShape Shape; public GeoInset Inset; public bool Important; }
    internal sealed class Plate { public string Caps = "", Main = "", Sub = ""; public bool Pill, Warn; public int Arrow; /* -1 = an up arrow before the main text, 1 = down */ }
    // the plate's fonts: 0 = the caps line, 1 = the action, 2 = the next step; in U (src/HintLayout.cs HintSize), scaled by sc, never below the readable minimum
    internal static Font PlateFont(int which, Plate p, double U, float sc) {
      double px = HintSize.FontPx(which, p != null && p.Pill, U);
      return new Font("Segoe UI", (float)Math.Max(HintSize.MinPx(which), px * sc), which == 2 ? FontStyle.Regular : FontStyle.Bold, GraphicsUnit.Pixel);
    }
    internal static float PlatePad(double U, float sc) { return (float)Math.Max(HintSize.MinPad, HintSize.Pad(U) * sc); }
    // the branded hint frame (owner-approved mockup C1, src/FrameTargets.cs FrameStyle): a 4 px line exactly on the target's outline, a soft 4 px
    // glow outside it, an owl badge on the top edge; the pulse line is drawn by PulseTick in a small window of its own. A frame is a rounded rect
    // (corner 4 px) or, for a round button, the sprite's contour; Path(offset, ox, oy) = its outline moved <offset> px outward, in a bitmap at (ox, oy)
    internal sealed class FrameSpecT { public Func<float, int, int, GraphicsPath> Path; public int Rgb; public float Gap; public int[] Box; }

    internal static FrameSpecT FrameSpec(Hl hl, double x, double y, double w, double h, int rgb, float gap) {
      var sp = new FrameSpecT { Rgb = rgb, Gap = gap, Box = new[] { (int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Ceiling(w), (int)Math.Ceiling(h) } };
      if (hl != null && GuideShape.FrameOutline(hl.Rect, hl.Shape, hl.Inset, 0) != null) {
        var r = hl.Rect;
        sp.Box = new[] { (int)Math.Floor(r.X), (int)Math.Floor(r.Y), (int)Math.Ceiling(r.W), (int)Math.Ceiling(r.H) };
        sp.Path = (o, ox, oy) => {
          var pts = GuideShape.FrameOutline(hl.Rect, hl.Shape, hl.Inset, o);
          var path = new GraphicsPath(); if (pts == null || pts.Count < 3) return path;
          var pf = new PointF[pts.Count];
          for (int i = 0; i < pf.Length; i++) pf[i] = new PointF((float)(pts[i].X - ox), (float)(pts[i].Y - oy));
          path.AddPolygon(pf); return path;
        };
      } else {
        float fx = (float)x, fy = (float)y, fw = (float)w, fh = (float)h;
        sp.Path = (o, ox, oy) => Round(fx - ox - o, fy - oy - o, fw + 2 * o, fh + 2 * o, FrameStyle.CornerR + o);
      }
      return sp;
    }

    internal static Color FrameColor(int rgb, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

    internal static void StrokeLayer(Graphics gr, FrameSpecT sp, FrameStyle.Layer l, int ox, int oy) {
      using (var pen = new Pen(FrameColor(l.Rgb, l.Alpha), l.Width) { LineJoin = LineJoin.Round })
      using (var path = sp.Path(l.Offset, ox, oy)) gr.DrawPath(pen, path);
    }

    // the still part (glow rings, then the line) of a frame into a bitmap at (ox, oy)
    internal static void PaintFrame(Graphics gr, FrameSpecT sp, int ox, int oy) {
      foreach (var l in FrameStyle.Still(sp.Rgb, sp.Gap)) StrokeLayer(gr, sp, l, ox, oy);
    }

    // the owl badge (the Wardsage mark: the shield, dark navy with a gold outline and gold eyes), at {cx, cy, flipped}
    internal static void PaintOwl(Graphics gr, double[] at, int ox, int oy) {
      double cx = at[0] - ox, cy = at[1] - oy;
      var sh = FrameStyle.OwlShield(cx, cy);
      var pf = new PointF[sh.Length / 2];
      for (int i = 0; i < pf.Length; i++) pf[i] = new PointF((float)sh[2 * i], (float)sh[2 * i + 1]);
      using (var br = new SolidBrush(FrameColor(FrameStyle.Navy, 255))) gr.FillPolygon(br, pf);
      using (var pen = new Pen(FrameColor(FrameStyle.Gold, 255), 1.5f) { LineJoin = LineJoin.Round }) gr.DrawPolygon(pen, pf);
      foreach (var e in FrameStyle.OwlEyes(cx, cy)) {
        float r = (float)e[2], q = (float)e[3];
        using (var br = new SolidBrush(FrameColor(FrameStyle.Gold, 255))) gr.FillEllipse(br, (float)e[0] - r, (float)e[1] - r, 2 * r, 2 * r);
        using (var br = new SolidBrush(FrameColor(FrameStyle.Navy, 255))) gr.FillEllipse(br, (float)e[0] - q, (float)e[1] - q, 2 * q, 2 * q);
      }
    }

    // the step plate: strictly rectangular, dark, a thin bronze border with a gold line inside; the owl and the caps line,
    // the action in white, the next step smaller; a pill (the strip's «Вещь здесь») has rounded ends and one line
    internal static void PaintPlate(Graphics gr, float x, float y, int w, int h, Plate p, Font fCaps, Font fMain, Font fSub, float owl, float capsH, float mainH, float pad, double[] ptr, int ox, int oy) {
      using (var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) gr.FillRectangle(shadow, x + 3, y + 5, w, h);
      if (p.Pill) {
        var path = Round(x, y, w, h, h / 2f - 0.5f);
        using (var bg = new SolidBrush(Color.FromArgb(244, 0x12, 0x1A, 0x2B))) gr.FillPath(bg, path);
        using (var pen = new Pen(FrameColor(FrameStyle.Gold, 255), 2f)) gr.DrawPath(pen, path);
      } else {
        // a warning plate (a stray item is ticked) is red: dark red body, a bright red border
        using (var bg = new SolidBrush(p.Warn ? Color.FromArgb(246, 52, 12, 12) : Color.FromArgb(244, 0x12, 0x1A, 0x2B))) gr.FillRectangle(bg, x, y, w, h);
        using (var pen = new Pen(FrameColor(p.Warn ? FrameStyle.Red : FrameStyle.Gold, 255), 2f)) gr.DrawRectangle(pen, x + 1, y + 1, w - 2, h - 2);
      }
      if (ptr != null) {
        var tri = new[] { new PointF((float)ptr[0] + ox, (float)ptr[1] + oy), new PointF((float)ptr[2] + ox, (float)ptr[3] + oy), new PointF((float)ptr[4] + ox, (float)ptr[5] + oy) };
        using (var br = new SolidBrush(FrameColor(p.Warn ? FrameStyle.Red : FrameStyle.Gold, 255))) gr.FillPolygon(br, tri);
      }
      float ty = y + (p.Pill ? pad / 2 : pad), tx = x + pad;
      if (p.Caps != "") {
        var owlImg = OwlTex();
        if (owlImg != null) gr.DrawImage(owlImg, tx, ty, owl, owl);
        using (var gold = new SolidBrush(FrameColor(FrameStyle.Gold, 255)))
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
    internal static void PaintStrip(Graphics gr, double[] s, int ox, int oy, float pulse, double U) {
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
        try { using (var s = typeof(OverlayPaint).Assembly.GetManifestResourceStream("overlay/owl.png")) if (s != null) owlTex = new Bitmap(Image.FromStream(s)); }
        catch (Exception) { }
      }
      return owlTex;
    }

    internal static GraphicsPath Round(float x, float y, float w, float h, float r) {
      var p = new GraphicsPath(); float d = r * 2;
      p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
      p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
      p.CloseFigure(); return p;
    }

    /// <summary>The elements of one overlay window as a painter (a scene's drawing, in the order it is drawn). Paint(gr, ox, oy, bw, bh): the
    /// bitmap's origin on the screen is (ox, oy), its size bw x bh.</summary>
    internal static RecOverlayEl Element(string key, string src, int sceneX, int sceneY, int x, int y, int w, int h, Action<Graphics, int, int, int, int, float> paint, bool listed = true) {
      return new RecOverlayEl { Key = key, Src = src, X = x, Y = y, W = w, H = h, SceneX = sceneX, SceneY = sceneY, Listed = listed,
        Draw = (g, ox, oy, bw, bh, ph) => paint((Graphics)g, ox, oy, bw, bh, ph) };
    }

    /// <summary>Draws the elements onto a captured client bitmap (client px, w x h), with the same code the overlay windows paint with;
    /// <paramref name="phase"/> = the pulse line's phase (FrameStyle.Phase), negative = no pulse. An element that fails is skipped.</summary>
    public static int Composite(Graphics gr, IList<RecOverlayEl> els, int w, int h, float phase) {
      if (els == null || els.Count == 0) return 0;
      var sm = gr.SmoothingMode; var tx = gr.TextRenderingHint; var ip = gr.InterpolationMode; var cm = gr.CompositingMode;
      gr.SmoothingMode = SmoothingMode.AntiAlias; gr.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; gr.InterpolationMode = InterpolationMode.HighQualityBicubic; gr.CompositingMode = CompositingMode.SourceOver;
      int n = 0;
      try {
        foreach (var e in els) {
          if (e == null || e.Draw == null) continue;
          // a scene's elements are in the screen px of its own client origin: that is the captured bitmap's origin
          // (the scene's own bitmap is clipped to the client; the capture is the client itself)
          try {
            if (e.Layer) {
              using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
                using (var g2 = Graphics.FromImage(bmp)) {
                  g2.SmoothingMode = SmoothingMode.AntiAlias; g2.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                  g2.Clear(Color.Transparent); e.Draw(g2, e.SceneX, e.SceneY, w, h, phase);
                }
                gr.DrawImage(bmp, 0, 0, w, h);
              }
            } else e.Draw(gr, e.SceneX, e.SceneY, w, h, phase);
            n++;
          }
          catch (Exception) { try { gr.ResetClip(); gr.ResetTransform(); gr.CompositingMode = CompositingMode.SourceOver; } catch (Exception) { } }
        }
      } finally { gr.SmoothingMode = sm; gr.TextRenderingHint = tx; gr.InterpolationMode = ip; gr.CompositingMode = cm; }
      return n;
    }
  }
}
