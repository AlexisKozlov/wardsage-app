// Wardsage app - the live DPS meter's picture (TEST build): bars of the heroes by a mode, tabs, segments, a hero's skills, a small curve.
// A pure drawing function of (snapshot, state, size): no window, no game, no input - the window is app/DpsMeter.cs, the numbers src/DpsFeed.cs.
#if TESTBUILD
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace RealmForge {
  /// <summary>What the player chose in the panel.</summary>
  sealed class DpsView {
    public DpsMode Mode = DpsMode.Damage;
    public DpsSegment Segment = DpsSegment.Current;
    /// <summary>The hero whose skills are open (controller, unit); HasOpen = none open when false.</summary>
    public bool HasOpen; public uint OpenC; public int OpenUnit;
    public string Lang = "ru";
    /// <summary>Ctrl is held: the panel takes the mouse (the border glows, the resize grip shows).</summary>
    public bool Interactive;
  }

  /// <summary>A pressable place of the picture (client px): Kind = mode / seg / hero / back / reset.</summary>
  sealed class DpsHit {
    public Rectangle R; public string Kind; public int Arg; public uint C;
  }

  static class DpsPanel {
    public static readonly DpsMode[] Modes = { DpsMode.Damage, DpsMode.ToBoss, DpsMode.Heal, DpsMode.Taken };
    public static readonly DpsSegment[] Segments = { DpsSegment.Current, DpsSegment.Previous, DpsSegment.Total };
    // the bar colours in turn
    static readonly int[] Palette = { 0xC79C6E, 0xF58CBA, 0xABD473, 0xFFF569, 0xE6E6E6, 0x0070DE, 0x69CCF0, 0x9482C9, 0xFF7D0A, 0x00FF96 };

    /// <summary>The scale for a game window: 1 at a 1000 px high game, never below 0.7 (src/ListTracker.cs Ui.Unit).</summary>
    public static float ScaleFor(double unit) { return (float)Math.Max(0.7, unit / 1000.0); }
    public static int DefaultW(double unit) { return (int)(0.30 * unit); }
    public static int DefaultH(double unit) { return (int)(0.46 * unit); }
    public static int MinW(double unit) { return (int)(0.20 * unit); }
    public static int MinH(double unit) { return (int)(0.20 * unit); }

    /// <summary>The resize grip at the bottom right (client px).</summary>
    public static Rectangle Grip(int w, int h, float s) { int g = (int)Math.Round(14 * s); return new Rectangle(w - g - 1, h - g - 1, g, g); }

    public static string Short(long v) {
      double a = Math.Abs((double)v), x = v;
      string u = "";
      if (a >= 1e9) { x = v / 1e9; u = "B"; } else if (a >= 1e6) { x = v / 1e6; u = "M"; } else if (a >= 1e4) { x = v / 1e3; u = "K"; }
      else return v.ToString(CultureInfo.InvariantCulture);
      double ax = Math.Abs(x);
      return x.ToString(ax >= 100 ? "0" : ax >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + u;
    }
    static string ShortD(double v) { return Short((long)Math.Round(v)); }
    public static string Clock(double sec) { int t = (int)sec; return (t / 60) + ":" + (t % 60).ToString("00"); }

    static string ModeName(DpsMode m, bool en) {
      switch (m) {
        case DpsMode.Damage: return en ? "Damage" : "Урон";
        case DpsMode.ToBoss: return en ? "Boss" : "Босс";
        case DpsMode.Heal: return en ? "Healing" : "Лечение";
        case DpsMode.Taken: return en ? "Taken" : "Получено";
        default: return en ? "Overflow" : "Перелив";
      }
    }
    static string SegName(DpsSegment s, bool en) { return s == DpsSegment.Current ? (en ? "Current" : "Текущий") : s == DpsSegment.Previous ? (en ? "Previous" : "Прошлый") : (en ? "Total" : "Общий"); }

    static Color Pal(int rgb, int a) { return Color.FromArgb(a, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

    /// <summary>The colour of a hero's bar: by the hero's place among the units of the segment (stable across the modes).</summary>
    static Color HeroColor(DpsSegmentView seg, DpsHero h, int a) {
      var units = new List<long>();
      foreach (var x in seg.Heroes) units.Add(((long)x.C << 32) | (uint)x.Unit);
      units.Sort();
      int i = units.IndexOf(((long)h.C << 32) | (uint)h.Unit);
      return Pal(Palette[(i < 0 ? 0 : i) % Palette.Length], a);
    }

    static string HeroName(DpsHero h, bool en) { return DpsNames.Hero(h.Unit, h.Base, h.Hero, !en); }

    static GraphicsPath Round(RectangleF r, float rad) {
      var p = new GraphicsPath(); float d = Math.Max(0.5f, rad * 2);
      p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
      p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
      p.CloseFigure(); return p;
    }

    sealed class Ctx : IDisposable {
      public Graphics G; public Font Bold, Norm, Small; public StringFormat Left, Right, Center;
      public Brush White, Grey, Gold, Shadow, Dark;
      public void Dispose() {
        foreach (var d in new IDisposable[] { Bold, Norm, Small, Left, Right, Center, White, Grey, Gold, Shadow, Dark }) if (d != null) d.Dispose();
      }
    }

    static StringFormat Fmt(StringAlignment a) {
      return new StringFormat { Alignment = a, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    }

    static void Text(Ctx c, string t, Font f, Brush b, RectangleF r, StringFormat sf) {
      if (string.IsNullOrEmpty(t) || r.Width < 2) return;
      c.G.DrawString(t, f, c.Shadow, new RectangleF(r.X + 1, r.Y + 1, r.Width, r.Height), sf);
      c.G.DrawString(t, f, b, r, sf);
    }

    static float Measure(Ctx c, string t, Font f) { return c.G.MeasureString(t, f, 10000, c.Left).Width; }

    /// <summary>Draws the panel into a new w x h bitmap (32-bit ARGB, see-through). hits gets the pressable places (may be null).</summary>
    public static Bitmap Render(DpsSnapshot snap, DpsView v, int w, int h, float s, List<DpsHit> hits) {
      var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
      if (hits != null) hits.Clear();
      bool en = v.Lang == "en";
      using (var g = Graphics.FromImage(bmp))
      using (var c = new Ctx {
        G = g, Bold = new Font("Segoe UI", Math.Max(9f, 12f * s), FontStyle.Bold, GraphicsUnit.Pixel), Norm = new Font("Segoe UI", Math.Max(9f, 12f * s), FontStyle.Regular, GraphicsUnit.Pixel),
        Small = new Font("Segoe UI", Math.Max(8f, 10f * s), FontStyle.Regular, GraphicsUnit.Pixel),
        Left = Fmt(StringAlignment.Near), Right = Fmt(StringAlignment.Far), Center = Fmt(StringAlignment.Center),
        White = new SolidBrush(Color.White), Grey = new SolidBrush(Color.FromArgb(255, 176, 184, 200)), Gold = new SolidBrush(Color.FromArgb(255, 240, 200, 110)),
        Shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0)), Dark = new SolidBrush(Color.FromArgb(120, 0, 0, 0)) }) {
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var plate = new RectangleF(1, 1, w - 3, h - 3);
        using (var path = Round(plate, 6 * s))
        using (var fill = new LinearGradientBrush(plate, Color.FromArgb(170, 12, 16, 26), Color.FromArgb(170, 24, 18, 12), LinearGradientMode.Vertical)) {
          g.FillPath(fill, path);
          using (var edge = v.Interactive ? new Pen(Color.FromArgb(255, 255, 226, 150), 3f) : new Pen(Color.FromArgb(220, 212, 170, 90), 1.5f)) g.DrawPath(edge, path);
        }
        float pad = 6 * s, rh = 22 * s, x0 = pad, x1 = w - pad;
        float y = pad;
        // ---- row 1: the segments, the fight's clock, reset
        var seg = snap == null ? null : snap.Get(v.Segment);
        float bh = 18 * s;
        float sx = x0;
        for (int i = 0; i < Segments.Length; i++) {
          string name = SegName(Segments[i], en); float tw = Measure(c, name, c.Small) + 10 * s;
          var r = new RectangleF(sx, y, tw, bh);
          bool on = Segments[i] == v.Segment;
          Pill(c, r, on, snap != null && snap.Get(Segments[i]) != null || on);
          Text(c, name, c.Small, on ? c.Gold : c.Grey, r, c.Center);
          if (hits != null) hits.Add(new DpsHit { R = Rectangle.Round(r), Kind = "seg", Arg = i });
          sx += tw + 3 * s;
        }
        string rs = en ? "Reset" : "Сброс"; float rw = Measure(c, rs, c.Small) + 10 * s;
        var rr = new RectangleF(x1 - rw, y, rw, bh);
        Pill(c, rr, false, true); Text(c, rs, c.Small, c.Grey, rr, c.Center);
        if (hits != null) hits.Add(new DpsHit { R = Rectangle.Round(rr), Kind = "reset" });
        if (seg != null) {
          string clk = Clock(seg.Seconds) + (seg.Fights > 1 ? " x" + seg.Fights : "");
          var cr = new RectangleF(sx + 2 * s, y, rr.X - sx - 6 * s, bh);
          if (cr.Width > Measure(c, clk, c.Small)) Text(c, clk, c.Small, c.White, cr, c.Right);
        }
        y += bh + 4 * s;
        // ---- row 2: the modes
        float tabW = (x1 - x0 - 3 * s * 3) / 4f;
        for (int i = 0; i < Modes.Length; i++) {
          var r = new RectangleF(x0 + i * (tabW + 3 * s), y, tabW, bh);
          bool on = Modes[i] == v.Mode;
          Pill(c, r, on, true);
          Text(c, ModeName(Modes[i], en), c.Small, on ? c.Gold : c.Grey, r, c.Center);
          if (hits != null) hits.Add(new DpsHit { R = Rectangle.Round(r), Kind = "mode", Arg = i });
        }
        y += bh + 5 * s;
        // ---- the bottom: the curve (when there is room), the sum line
        float gh = 44 * s, sumH = 16 * s;
        float bottom = h - pad;
        bool graphOn = (bottom - y) >= rh * 4 + gh + sumH + 6 * s;
        RectangleF graph = graphOn ? new RectangleF(x0, bottom - gh, x1 - x0, gh) : RectangleF.Empty;
        if (graphOn) bottom -= gh + 3 * s;
        var sumR = new RectangleF(x0, bottom - sumH, x1 - x0, sumH);
        bottom -= sumH + 2 * s;
        // ---- the list
        bool open = false; DpsHero openHero = null;
        if (seg != null && v.HasOpen) foreach (var hh in seg.Heroes) if (hh.C == v.OpenC && hh.Unit == v.OpenUnit) { openHero = hh; open = true; }
        if (seg == null) {
          Text(c, en ? "No fight yet" : "Боя ещё не было", c.Norm, c.Grey, new RectangleF(x0, y, x1 - x0, Math.Max(rh, bottom - y)), c.Center);
        } else if (open) {
          DrawSkills(c, seg, openHero, v, x0, x1, y, bottom, rh, s, hits, en);
        } else {
          DrawHeroes(c, seg, v, x0, x1, y, bottom, rh, s, hits, en);
        }
        if (seg != null) DrawSum(c, seg, v, openHero, sumR, en);
        if (graphOn) DrawGraph(c, snap, seg, v, openHero, graph, s, en);
        if (v.Interactive) {
          var gr = Grip(w, h, s);
          using (var pen = new Pen(Color.FromArgb(255, 255, 226, 150), 1.5f)) {
            g.DrawLine(pen, gr.Right - 2, gr.Y + 3, gr.X + 3, gr.Bottom - 2);
            g.DrawLine(pen, gr.Right - 2, gr.Y + gr.Height / 2f + 1, gr.X + gr.Width / 2f + 1, gr.Bottom - 2);
          }
        }
      }
      return bmp;
    }

    static void Pill(Ctx c, RectangleF r, bool on, bool live) {
      using (var p = Round(r, 4))
      using (var f = new SolidBrush(on ? Color.FromArgb(150, 90, 66, 24) : Color.FromArgb(live ? 110 : 60, 0, 0, 0)))
      using (var e = new Pen(on ? Color.FromArgb(230, 212, 170, 90) : Color.FromArgb(live ? 90 : 40, 255, 255, 255), 1f)) { c.G.FillPath(f, p); c.G.DrawPath(e, p); }
    }

    static void Bar(Ctx c, RectangleF row, double frac, Color col) {
      c.G.FillRectangle(c.Dark, row);
      float bw = (float)Math.Max(0, Math.Min(1, frac)) * row.Width;
      if (bw >= 1) using (var b = new SolidBrush(col)) c.G.FillRectangle(b, row.X, row.Y, bw, row.Height);
    }

    static void DrawHeroes(Ctx c, DpsSegmentView seg, DpsView v, float x0, float x1, float y, float bottom, float rh, float s, List<DpsHit> hits, bool en) {
      var list = seg.Sorted(v.Mode);
      int mi = (int)v.Mode;
      long max = list.Count > 0 ? list[0].Value[mi] : 0;
      string per = en ? "/s" : "/с";
      int shown = 0;
      float gap = 2 * s;
      for (int i = 0; i < list.Count; i++) {
        var hero = list[i];
        if (hero.Value[mi] <= 0) continue;
        if (y + rh > bottom + 0.5f) break;
        var row = new RectangleF(x0, y, x1 - x0, rh);
        Bar(c, row, max > 0 ? hero.Value[mi] / (double)max : 0, HeroColor(seg, hero, 205));
        string right = Short(hero.Value[mi]) + "  " + ShortD(hero.Avg[mi]) + per + "  " + (hero.Share[mi] * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        string name = (shown + 1) + ". " + HeroName(hero, en);
        float rw = Measure(c, right, c.Norm);
        if (rw > row.Width * 0.62f) {   // narrow: the value and the share only
          right = Short(hero.Value[mi]) + "  " + (hero.Share[mi] * 100).ToString("0", CultureInfo.InvariantCulture) + "%"; rw = Measure(c, right, c.Norm);
        }
        var rt = new RectangleF(row.X + 4 * s, row.Y, row.Width - 8 * s, row.Height);
        Text(c, right, c.Norm, c.White, rt, c.Right);
        Text(c, name, c.Bold, c.White, new RectangleF(rt.X, rt.Y, Math.Max(10, rt.Width - rw - 6 * s), rt.Height), c.Left);
        if (hits != null) hits.Add(new DpsHit { R = Rectangle.Round(row), Kind = "hero", C = hero.C, Arg = hero.Unit });
        y += rh + gap; shown++;
      }
      if (shown == 0) Text(c, en ? "Nothing in this mode yet" : "В этом режиме пока пусто", c.Norm, c.Grey, new RectangleF(x0, y, x1 - x0, rh * 2), c.Center);
    }

    static void DrawSkills(Ctx c, DpsSegmentView seg, DpsHero hero, DpsView v, float x0, float x1, float y, float bottom, float rh, float s, List<DpsHit> hits, bool en) {
      var head = new RectangleF(x0, y, x1 - x0, rh);
      Bar(c, head, 1, HeroColor(seg, hero, 150));
      string back = (en ? "< Back  " : "< Назад  ");
      Text(c, back + HeroName(hero, en), c.Bold, c.White, new RectangleF(head.X + 4 * s, head.Y, head.Width - 8 * s, head.Height), c.Left);
      Text(c, Short(hero.Value[0]), c.Norm, c.White, new RectangleF(head.X + 4 * s, head.Y, head.Width - 8 * s, head.Height), c.Right);
      if (hits != null) hits.Add(new DpsHit { R = Rectangle.Round(head), Kind = "back" });
      y += rh + 3 * s;
      if (hero.Skills == null || hero.Skills.Count == 0) {
        Text(c, en ? "Skills were not read in this fight" : "Скиллы в этом бою не прочитаны", c.Norm, c.Grey, new RectangleF(x0, y, x1 - x0, rh * 2), c.Center);
        return;
      }
      long max = hero.Skills[0].Amount;
      string per = en ? "/s" : "/с";
      float gap = 2 * s;
      var col = HeroColor(seg, hero, 205);
      foreach (var sk in hero.Skills) {
        if (y + rh > bottom + 0.5f) break;
        var row = new RectangleF(x0, y, x1 - x0, rh);
        Bar(c, row, max > 0 ? sk.Amount / (double)max : 0, col);
        string right = Short(sk.Amount) + "  " + ShortD(sk.Dps) + per + "  " + (sk.Share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        float rw = Measure(c, right, c.Norm);
        if (rw > row.Width * 0.62f) { right = Short(sk.Amount) + "  " + (sk.Share * 100).ToString("0", CultureInfo.InvariantCulture) + "%"; rw = Measure(c, right, c.Norm); }
        var rt = new RectangleF(row.X + 4 * s, row.Y, row.Width - 8 * s, row.Height);
        Text(c, right, c.Norm, c.White, rt, c.Right);
        Text(c, DpsNames.Skill(sk.Id, !en), c.Norm, c.White, new RectangleF(rt.X, rt.Y, Math.Max(10, rt.Width - rw - 6 * s), rt.Height), c.Left);
        y += rh + gap;
      }
    }

    static void DrawSum(Ctx c, DpsSegmentView seg, DpsView v, DpsHero only, RectangleF r, bool en) {
      int mi = (int)v.Mode; long sum = 0;
      if (only != null) sum = only.Value[mi]; else foreach (var h in seg.Heroes) sum += h.Value[mi];
      double dps = seg.Seconds > 0 ? sum / seg.Seconds : 0;
      string t = (en ? "Total " : "Всего ") + Short(sum) + "   " + ShortD(dps) + (en ? "/s" : "/с");
      Text(c, t, c.Small, c.Gold, r, c.Left);
    }

    /// <summary>The curve of the selected mode over the fight (the current fight only: the other segments have no curve).</summary>
    static void DrawGraph(Ctx c, DpsSnapshot snap, DpsSegmentView seg, DpsView v, DpsHero only, RectangleF r, float s, bool en) {
      c.G.FillRectangle(c.Dark, r);
      using (var e = new Pen(Color.FromArgb(70, 255, 255, 255), 1f)) c.G.DrawRectangle(e, r.X, r.Y, r.Width, r.Height);
      double[] curve = Curve(seg, v.Mode, only, Math.Max(8, (int)(r.Width / 3)));
      if (v.Segment != DpsSegment.Current || curve == null) {
        Text(c, en ? "The curve is for the current fight" : "График есть только у текущего боя", c.Small, c.Grey, r, c.Center);
        return;
      }
      double peak = 0; foreach (var d in curve) if (d > peak) peak = d;
      if (peak <= 0) { Text(c, "-", c.Small, c.Grey, r, c.Center); return; }
      var pts = new PointF[curve.Length];
      float ih = r.Height - 4 * s, iw = r.Width - 4 * s;
      for (int i = 0; i < curve.Length; i++) pts[i] = new PointF(r.X + 2 * s + iw * i / (curve.Length - 1), r.Bottom - 2 * s - (float)(curve[i] / peak) * ih);
      var area = new List<PointF>(pts); area.Add(new PointF(pts[pts.Length - 1].X, r.Bottom - 2 * s)); area.Add(new PointF(pts[0].X, r.Bottom - 2 * s));
      using (var fb = new SolidBrush(Color.FromArgb(70, 240, 200, 110))) c.G.FillPolygon(fb, area.ToArray());
      using (var pen = new Pen(Color.FromArgb(255, 240, 200, 110), Math.Max(1.2f, 1.5f * s))) c.G.DrawLines(pen, pts);
      Text(c, ShortD(peak) + (en ? "/s" : "/с"), c.Small, c.Grey, new RectangleF(r.X + 4 * s, r.Y, r.Width / 2, 12 * s), c.Left);
      Text(c, Clock(seg.Seconds), c.Small, c.Grey, new RectangleF(r.X, r.Bottom - 12 * s, r.Width - 4 * s, 12 * s), c.Right);
    }

    static long At(long[][] pts, long frame, int col) {
      int lo = 0, hi = pts.Length - 1, at = -1;
      while (lo <= hi) { int mid = (lo + hi) >> 1; if (pts[mid][0] <= frame) { at = mid; lo = mid + 1; } else hi = mid - 1; }
      return at < 0 ? 0 : pts[at][col];
    }

    /// <summary>DPS per bucket over the fight (team or one hero), lightly smoothed; null when no hero has a curve.</summary>
    public static double[] Curve(DpsSegmentView seg, DpsMode mode, DpsHero only, int n) {
      if (seg == null || seg.Frames < 2 || n < 2) return null;
      var src = new List<DpsHero>();
      if (only != null) { if (only.Series != null) src.Add(only); } else foreach (var h in seg.Heroes) if (h.Series != null) src.Add(h);
      if (src.Count == 0) return null;
      int col = (int)mode + 1;
      var cum = new double[n + 1];
      for (int i = 0; i <= n; i++) {
        long fr = (long)(seg.Frames * (double)i / n);
        double sum = 0; foreach (var h in src) sum += At(h.Series, fr, col);
        cum[i] = sum;
      }
      double dt = seg.Frames / (double)n * DpsFeed.FrameSec;
      var raw = new double[n];
      for (int i = 0; i < n; i++) raw[i] = Math.Max(0, cum[i + 1] - cum[i]) / dt;
      var o = new double[n];
      for (int i = 0; i < n; i++) {
        double a = 0; int k = 0;
        for (int j = Math.Max(0, i - 2); j <= Math.Min(n - 1, i + 2); j++) { a += raw[j]; k++; }
        o[i] = a / k;
      }
      return o;
    }
  }
}
#endif
