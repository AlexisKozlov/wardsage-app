// Wardsage app - the live DPS meter's picture (TEST build): heroes by a mode, tabs, segments, a hero's skills, a small curve.
// A pure drawing function of (snapshot, state, size): no window, no game, no input - the window is app/DpsMeter.cs, the numbers src/DpsFeed.cs.
// Two looks (DpsStyle, to be cut to one): A "hero window" (slate plate, game buttons, framed bars), B "battle strip" (a dark gradient per row, thin bars).
// Text never lies on a coloured bar: bars sit under or beside the text, the text lies on the plate / the dark strip.
#if TESTBUILD
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

namespace RealmForge {
  enum DpsStyle { A, B }

  /// <summary>What the player chose in the panel.</summary>
  sealed class DpsView {
    public DpsMode Mode = DpsMode.Damage;
    public DpsSegment Segment = DpsSegment.Current;
    /// <summary>The hero whose skills are open (controller, unit); HasOpen = none open when false.</summary>
    public bool HasOpen; public uint OpenC; public int OpenUnit;
    public string Lang = "ru";
    /// <summary>Ctrl is held: the panel takes the mouse (the border glows, the resize grip shows).</summary>
    public bool Interactive;
    /// <summary>The look (A or B): the test build shows one of the two until the owner picks.</summary>
    public DpsStyle Style = DpsStyle.A;
  }

  /// <summary>A pressable place of the picture (client px): Kind = mode / seg / hero / back / reset.</summary>
  sealed class DpsHit {
    public Rectangle R; public string Kind; public int Arg; public uint C;
  }

  static class DpsPanel {
    public static readonly DpsMode[] Modes = { DpsMode.Damage, DpsMode.ToBoss, DpsMode.Heal, DpsMode.Taken };
    public static readonly DpsSegment[] Segments = { DpsSegment.Current, DpsSegment.Previous, DpsSegment.Total };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Color White = Color.White, TitleGold = Color.FromArgb(255, 255, 227, 160), Idle = Color.FromArgb(255, 201, 206, 218),
      ValueCol = Color.FromArgb(255, 255, 242, 200), SubCol = Color.FromArgb(255, 184, 192, 208), SumGold = Color.FromArgb(255, 255, 217, 120);

    /// <summary>The scale for a game window: 1 at a 1000 px high game, never below 0.7 (src/ListTracker.cs Ui.Unit).</summary>
    public static float ScaleFor(double unit) { return (float)Math.Max(0.7, unit / 1000.0); }
    public static int DefaultW(double unit) { return (int)(0.30 * unit); }
    public static int DefaultH(double unit) { return (int)(0.50 * unit); }
    public static int MinW(double unit) { return (int)(0.22 * unit); }
    public static int MinH(double unit) { return (int)(0.22 * unit); }

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
    static string ModeShort(DpsMode m, bool en) {
      switch (m) {
        case DpsMode.Damage: return en ? "Dmg" : "Урон";
        case DpsMode.ToBoss: return en ? "Boss" : "Босс";
        case DpsMode.Heal: return en ? "Heal" : "Леч";
        default: return en ? "Got" : "Пол";
      }
    }
    static string SegShort(DpsSegment s, bool en) { return s == DpsSegment.Current ? (en ? "Now" : "Тек") : s == DpsSegment.Previous ? (en ? "Prev" : "Прош") : (en ? "All" : "Все"); }
    static string SegName(DpsSegment s, bool en) { return s == DpsSegment.Current ? (en ? "Current" : "Текущий") : s == DpsSegment.Previous ? (en ? "Previous" : "Прошлый") : (en ? "Total" : "Общий"); }
    static string HeroName(DpsHero h, bool en) { return DpsNames.Hero(h.Unit, h.Base, h.Hero, !en); }
    static string Pct(double share) { return (share * 100).ToString("0", Inv) + "%"; }
    static string FillKind(DpsMode m) { return m == DpsMode.ToBoss ? "bar_yellow" : m == DpsMode.Heal ? "green" : m == DpsMode.Taken ? "bar_purple" : "bar_red"; }

    sealed class Ctx {
      public Graphics G; public float S; public bool En, A; public List<DpsHit> Hits; public DpsView V; public string Fill;
    }

    /// <summary>B: a dark base under a row or a message (so that the fading strip never leaves the text on a bright game picture).</summary>
    static void Base(Ctx c, RectangleF r) {
      if (c.A) return;
      using (var b = new SolidBrush(Color.FromArgb(150, 8, 8, 14))) c.G.FillRectangle(b, r);
    }

    static float T(Ctx c, string t, float px, bool bold, Color col, RectangleF r, int al) { return DpsArt.Text(c.G, t, px, bold, col, r, al); }
    /// <summary>The biggest size (down to minPx) at which the text fits maxW.</summary>
    static float FitPx(string t, float px, float minPx, bool bold, float maxW) {
      while (px > minPx && DpsArt.Measure(t, px, bold) > maxW) px -= 0.5f;
      return Math.Max(px, minPx);
    }
    static void Hit(Ctx c, RectangleF r, string kind, int arg, uint cc) { if (c.Hits != null) c.Hits.Add(new DpsHit { R = Rectangle.Round(r), Kind = kind, Arg = arg, C = cc }); }

    static GraphicsPath Round(RectangleF r, float rad) {
      var p = new GraphicsPath(); float d = Math.Max(0.5f, rad * 2);
      p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
      p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
      p.CloseFigure(); return p;
    }

    /// <summary>The plate under everything (cached by size, scale, Ctrl and look): A the slate tips plate, B nothing; Ctrl adds the glowing frame.</summary>
    static Bitmap Chrome(int w, int h, float s, bool inter, bool a) {
      return DpsArt.Cached("ch|" + w + "|" + h + "|" + s.ToString("0.###", Inv) + "|" + inter + "|" + a, () => {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp)) {
          if (a) { var n = DpsArt.Nine("common_description_tips", w, h, 0.5f * s, 0.88f); if (n != null) g.DrawImageUnscaled(n, 0, 0); else using (var b = new SolidBrush(Color.FromArgb(225, 30, 32, 42))) g.FillRectangle(b, 0, 0, w, h); }
          if (inter) { var f = DpsArt.GlowFrame(w, h, 0.5f * s); if (f != null) g.DrawImageUnscaled(f, 0, 0); }
        }
        return bmp;
      });
    }

    /// <summary>Draws the panel into a new w x h bitmap (32-bit ARGB, see-through). hits gets the pressable places (may be null).
    /// Sizes follow the plan (s = max(0.7, U/1000)); the layout never goes below 0.9 so that the 11/13 px text still fits the rows.</summary>
    public static Bitmap Render(DpsSnapshot snap, DpsView v, int w, int h, float s, List<DpsHit> hits) {
      s = Math.Max(s, 0.9f);
      DpsArt.Begin(s);
      var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
      if (hits != null) hits.Clear();
      bool en = v.Lang == "en", A = v.Style == DpsStyle.A;
      using (var g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half; g.InterpolationMode = InterpolationMode.Bilinear;
        var c = new Ctx { G = g, S = s, En = en, A = A, Hits = hits, V = v, Fill = FillKind(v.Mode) };
        var chrome = Chrome(w, h, s, v.Interactive, A);
        if (chrome != null) g.DrawImageUnscaled(chrome, 0, 0);
        float pad = 8 * s, x0 = pad, x1 = w - pad, iw = x1 - x0, y = pad;
        var seg = snap == null ? null : snap.Get(v.Segment);
        // ---- title: the mode on the left, the fight's clock on the right
        float hh = 30 * s;
        var hr = new RectangleF(x0, y, iw, hh);
        if (A) { var pl = DpsArt.Plaque((int)Math.Round(iw), (int)Math.Round(hh), 0.55f); if (pl != null) g.DrawImageUnscaled(pl, (int)Math.Round(x0), (int)Math.Round(y)); }
        else { Base(c, hr); DpsArt.DrawPiece(g, "title_bg", hr); }
        float tx = A ? x0 + 64 * s : x0 + 8 * s;   // A: right of the emblem
        float clkW = 0;
        if (seg != null) {
          string clk = Clock(seg.Seconds) + (seg.Fights > 1 ? " x" + seg.Fights : "");
          clkW = T(c, clk, Math.Max(13, 16 * s), true, White, new RectangleF(tx, y, x1 - tx - 8 * s, hh), 2);
        }
        T(c, ModeName(v.Mode, en), Math.Max(13, 17 * s), true, TitleGold, new RectangleF(tx, y, Math.Max(10, x1 - tx - clkW - 14 * s), hh), 0);
        y += hh + 3 * s;
        // ---- segments and reset
        float sh = 22 * s;
        string rs = en ? "Reset" : "Сброс";
        var names = new string[3]; var tws = new float[4]; bool shortSeg = false;
        float avail = iw - 3 * 3 * s, pp = 8 * s, sumw = 0;
        float spx = Math.Max(11, 12 * s);
        for (int attempt = 0; attempt < 2; attempt++) {
          for (int i = 0; i < 3; i++) names[i] = shortSeg ? SegShort(Segments[i], en) : SegName(Segments[i], en);
          string rl = shortSeg ? "\u00D7" : rs; pp = 8 * s;
          do {
            sumw = 0;
            for (int i = 0; i < 3; i++) { tws[i] = DpsArt.Measure(names[i], spx, false) + 2 * pp; sumw += tws[i]; }
            tws[3] = DpsArt.Measure(rl, spx, false) + 2 * pp; sumw += tws[3];
            if (sumw > avail) pp -= 0.5f * s;
          } while (sumw > avail && pp > 1.5f * s);
          if (sumw <= avail || shortSeg) break;
          shortSeg = true;   // too narrow for the words: short names and a cross for the reset
        }
        if (shortSeg) rs = "\u00D7";
        if (sumw > avail) { float f = avail / sumw; for (int i = 0; i < 4; i++) tws[i] *= f; }
        float sx = x0;
        for (int i = 0; i < 4; i++) {
          var r = new RectangleF(sx, y, tws[i], sh);
          bool on = i < 3 && Segments[i] == v.Segment;
          Button(c, r, on, i < 3 ? names[i] : rs, spx, false, A ? (on ? "common_btn_special_s" : "common_btn_normal_s") : (on ? "common_btn_special_s" : null));
          if (i < 3) Hit(c, r, "seg", i, 0); else Hit(c, r, "reset", 0, 0);
          sx += tws[i] + 3 * s;
        }
        y += sh + 3 * s;
        // ---- modes
        float mh = 26 * s, gapM = 3 * s, tabW = (iw - gapM * 3) / 4f;
        bool shortMode = false;
        foreach (var mm in Modes) if (DpsArt.Measure(ModeName(mm, en), Math.Max(11, 13 * s), true) > tabW - 4 * s && DpsArt.Measure(ModeName(mm, en), 11, true) > tabW - 4 * s) shortMode = true;
        for (int i = 0; i < Modes.Length; i++) {
          var r = new RectangleF(x0 + i * (tabW + gapM), y, tabW, mh);
          bool on = Modes[i] == v.Mode;
          string nm = shortMode ? ModeShort(Modes[i], en) : ModeName(Modes[i], en);
          Button(c, r, on, nm, FitPx(nm, Math.Max(11, 13 * s), 11, true, tabW - 6 * s), true, A ? (on ? "common_btn_hortab2" : "common_btn_hortab0") : (on ? "common_btn_special_s" : null));
          Hit(c, r, "mode", i, 0);
        }
        y += mh + 4 * s;
        // ---- the bottom: the curve (when 4 hero rows still fit), the sum line
        float rowH = 36 * s, gap = 3 * s, gh = 52 * s, sumH = 16 * s;
        float bottom = h - pad;
        bool graphOn = seg != null && (bottom - y - sumH - 3 * s - gh - 3 * s) >= 4 * (rowH + gap) - gap;
        RectangleF graph = RectangleF.Empty;
        if (graphOn) { graph = new RectangleF(x0, bottom - gh, iw, gh); bottom -= gh + 3 * s; }
        var sumR = new RectangleF(x0, bottom - sumH, iw, sumH);
        if (seg != null) bottom -= sumH + 3 * s;
        // ---- the list
        DpsHero openHero = null;
        if (seg != null && v.HasOpen) foreach (var hh2 in seg.Heroes) if (hh2.C == v.OpenC && hh2.Unit == v.OpenUnit) openHero = hh2;
        if (seg == null) {
          var nr = new RectangleF(x0, y, iw, 36 * s); Base(c, nr);
          T(c, en ? "No fight yet" : "Боя ещё не было", Math.Max(13, 14 * s), false, Idle, nr, 1);
        } else if (openHero != null) {
          DrawSkills(c, seg, openHero, x0, x1, y, bottom);
        } else {
          DrawHeroes(c, seg, x0, x1, y, bottom);
        }
        if (seg != null) DrawSum(c, seg, openHero, sumR);
        if (graphOn) DrawGraph(c, snap, seg, openHero, graph);
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

    /// <summary>A button: a game sprite (9-sliced by its borders) or, for B's idle ones, a dark plate; the label lies on it.</summary>
    static void Button(Ctx c, RectangleF r, bool on, string text, float px, bool bold, string sprite) {
      int bw = (int)Math.Round(r.Width), bh = (int)Math.Round(r.Height);
      Bitmap b = null;
      if (sprite != null) b = DpsArt.Nine(sprite, bw, bh, DpsArt.HeightScale(sprite, bh), 1f, sprite == "common_btn_hortab2" ? 0.38f : on ? 0.46f : 0.5f);   // dimmed: the light label must stand on a dark button
      if (b == null) {
        float s = c.S;
        b = DpsArt.Cached("bd|" + bw + "|" + bh + "|" + s.ToString("0.###", Inv), () => {
          var o = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
          using (var g = Graphics.FromImage(o)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(new RectangleF(0.5f, 0.5f, bw - 1.5f, bh - 1.5f), 4 * s))
            using (var f = new SolidBrush(Color.FromArgb(185, 12, 12, 20)))
            using (var e = new Pen(Color.FromArgb(150, 200, 170, 110), 1f)) { g.FillPath(f, p); g.DrawPath(e, p); }
          }
          return o;
        });
      }
      if (b != null) c.G.DrawImageUnscaled(b, (int)Math.Round(r.X), (int)Math.Round(r.Y));
      T(c, text, px, bold, on ? TitleGold : Idle, new RectangleF(r.X + 2 * c.S, r.Y, r.Width - 4 * c.S, r.Height), 1);
    }

    /// <summary>The bar beside the text: A a framed bar (game frame, dark track, coloured fill), B a thin track with the fill; frac 0..1.</summary>
    static void DrawBar(Ctx c, RectangleF r, double frac, bool framed) {
      var g = c.G; float s = c.S;
      float f = (float)Math.Max(0, Math.Min(1, frac));
      RectangleF inner = r;
      if (framed) {
        var fr = new RectangleF(r.X - 1 * s, r.Y - 2.5f * s, r.Width + 2 * s, r.Height + 5 * s);
        int fw = (int)Math.Round(fr.Width), fh = (int)Math.Round(fr.Height);
        var nb = DpsArt.Nine("common_bg_barframe_01", fw, fh, DpsArt.HeightScale("common_bg_barframe_01", fh), 1f);
        if (nb != null) g.DrawImageUnscaled(nb, (int)Math.Round(fr.X), (int)Math.Round(fr.Y));
        inner = new RectangleF(r.X + 1.5f * s, r.Y + 0.5f * s, r.Width - 3 * s, r.Height - 1 * s);
      }
      DpsArt.DrawPiece(g, "bar_bg", inner);
      DpsArt.DrawFill(g, c.Fill, new RectangleF(inner.X, inner.Y, inner.Width * f, inner.Height));
    }

    static void DrawHeroes(Ctx c, DpsSegmentView seg, float x0, float x1, float y, float bottom) {
      var v = c.V; float s = c.S; bool en = c.En;
      var list = seg.Sorted(v.Mode);
      int mi = (int)v.Mode;
      long max = list.Count > 0 ? list[0].Value[mi] : 0;
      string per = en ? "/s" : "/с";
      float rowH = 36 * s, gap = 3 * s;
      int shown = 0;
      for (int i = 0; i < list.Count; i++) {
        var hero = list[i];
        if (hero.Value[mi] <= 0) continue;
        if (y + rowH > bottom + 0.5f) break;
        var row = new RectangleF(x0, y, x1 - x0, rowH);
        string sub = ShortD(hero.Avg[mi]) + per + " · " + Pct(hero.Share[mi]);
        HeroRow(c, hero, (shown + 1) + ". " + HeroName(hero, en), row, shown == 0, max > 0 ? hero.Value[mi] / (double)max : 0, Short(hero.Value[mi]), sub, Pct(hero.Share[mi]), null);
        Hit(c, row, "hero", hero.Unit, hero.C);
        y += rowH + gap; shown++;
      }
      if (shown == 0) { var nr = new RectangleF(x0, y, x1 - x0, rowH); Base(c, nr); T(c, en ? "Nothing in this mode yet" : "В этом режиме пока пусто", Math.Max(13, 14 * s), false, Idle, nr, 1); }
    }

    /// <summary>One hero row: the portrait, the name and the value on the first line, the bar and the small figures on the second.</summary>
    static void HeroRow(Ctx c, DpsHero hero, string name, RectangleF row, bool leader, double frac, string value, string sub, string subShort, string back) {
      var g = c.G; float s = c.S; bool A = c.A;
      if (!A) {
        Base(c, row);
        if (leader) { var pl = DpsArt.Darkened("plate5", (int)Math.Round(row.Width), (int)Math.Round(row.Height), 0.5f, 0.95f); if (pl != null) g.DrawImageUnscaled(pl, (int)Math.Round(row.X), (int)Math.Round(row.Y)); }
        else DpsArt.DrawPiece(g, "title_bg", row);
      }
      int px = (int)Math.Round(30 * s);
      DpsArt.Portrait(g, hero.Unit, hero.Base, hero.Hero, row.X + 3 * s, row.Y + (row.Height - px) / 2f, px, !A);
      float tx = row.X + 3 * s + px + 7 * s, tr = row.Right - 5 * s, zone = tr - tx;
      float vpx = Math.Max(13, 16 * s), npx = Math.Max(11, 14 * s), spx = Math.Max(11, 11 * s);
      var l1 = new RectangleF(tx, row.Y + 2 * s, zone, 17 * s);
      float vw = T(c, value, vpx, true, ValueCol, l1, 2);
      T(c, name, npx, true, White, new RectangleF(tx, l1.Y, Math.Max(10, zone - vw - 8 * s), l1.Height), 0);
      var l2 = new RectangleF(tx, row.Y + 20 * s, zone, 13 * s);
      string st = back ?? sub;
      float sw = DpsArt.Measure(st, spx, false);
      if (back == null && sw > zone * 0.38f) { st = subShort; sw = DpsArt.Measure(st, spx, false); }
      float barW = Math.Min(zone * 0.62f, zone - sw - 8 * s);
      if (barW > 12 * s) DrawBar(c, new RectangleF(tx, l2.Y + (l2.Height - 9 * s) / 2f, barW, 9 * s), frac, A);
      T(c, st, spx, false, back != null ? TitleGold : SubCol, l2, 2);
    }

    static void DrawSkills(Ctx c, DpsSegmentView seg, DpsHero hero, float x0, float x1, float y, float bottom) {
      var v = c.V; float s = c.S; bool en = c.En; int mi = (int)v.Mode;
      float rowH = 36 * s;
      var head = new RectangleF(x0, y, x1 - x0, rowH);
      HeroRow(c, hero, HeroName(hero, en), head, true, 1, Short(hero.Value[mi]), null, null, en ? "‹ Back" : "‹ Назад");
      Hit(c, head, "back", 0, 0);
      y += rowH + 3 * s;
      if (v.Mode == DpsMode.Heal) {
        var nr = new RectangleF(x0, y, x1 - x0, 36 * s); Base(c, nr);
        T(c, en ? "The game does not track healing by skill" : "Лечение по скиллам игра не ведёт", Math.Max(11, 13 * s), false, Idle, nr, 1);
        return;
      }
      var rows = DpsSkillRows.Group(hero.Unit, hero.Skills);
      if (rows.Count == 0) {
        var nr = new RectangleF(x0, y, x1 - x0, 36 * s); Base(c, nr);
        T(c, en ? "Skills were not read in this fight" : "Скиллы в этом бою не прочитаны", Math.Max(11, 13 * s), false, Idle, nr, 1);
        return;
      }
      long max = rows[0].Amount;
      string per = en ? "/s" : "/с";
      float sh = 30 * s, gap = 2 * s;
      int ipx = (int)Math.Round(28 * s);
      foreach (var sk in rows) {
        if (y + sh > bottom + 0.5f) break;
        var row = new RectangleF(x0, y, x1 - x0, sh);
        if (!c.A) { Base(c, row); DpsArt.DrawPiece(c.G, "title_bg", row); }
        DpsArt.SkillIcon(c.G, sk.Tail ? 0 : sk.Id, row.X + 2 * s, row.Y + (sh - ipx) / 2f, ipx);
        float tx = row.X + 2 * s + ipx + 6 * s, tr = row.Right - 5 * s, zone = tr - tx;
        var l1 = new RectangleF(tx, row.Y + 1 * s, zone, 15 * s);
        float vw = T(c, Short(sk.Amount), Math.Max(13, 14 * s), true, ValueCol, l1, 2);
        T(c, sk.Name(!en), Math.Max(11, 13 * s), false, White, new RectangleF(tx, l1.Y, Math.Max(10, zone - vw - 8 * s), l1.Height), 0);
        var l2 = new RectangleF(tx, row.Y + 16 * s, zone, 12 * s);
        string st = ShortD(sk.Dps) + per + " · " + Pct(sk.Share);
        float spx = Math.Max(11, 11 * s), sw = DpsArt.Measure(st, spx, false);
        if (sw > zone * 0.45f) { st = Pct(sk.Share); sw = DpsArt.Measure(st, spx, false); }
        float barW = Math.Min(zone * 0.62f, zone - sw - 8 * s);
        if (barW > 10 * s) {
          var br = new RectangleF(tx, l2.Y + (l2.Height - 7 * s) / 2f, barW, 7 * s);
          DpsArt.DrawPiece(c.G, "bar_bg", br);
          DpsArt.DrawFill(c.G, c.Fill, new RectangleF(br.X, br.Y, br.Width * (float)(max > 0 ? sk.Amount / (double)max : 0), br.Height));
        }
        T(c, st, spx, false, SubCol, l2, 2);
        y += sh + gap;
      }
    }

    static void DrawSum(Ctx c, DpsSegmentView seg, DpsHero only, RectangleF r) {
      int mi = (int)c.V.Mode; long sum = 0;
      if (only != null) sum = only.Value[mi]; else foreach (var h in seg.Heroes) sum += h.Value[mi];
      double dps = seg.Seconds > 0 ? sum / seg.Seconds : 0;
      string t = (c.En ? "Total " : "Всего ") + Short(sum) + " · " + ShortD(dps) + (c.En ? "/s" : "/с");
      Base(c, r);
      T(c, t, Math.Max(13, 14 * c.S), true, SumGold, r, 0);
      if (!c.A) DpsArt.DrawPiece(c.G, "art-sep", new RectangleF(r.X, r.Y - 2 * c.S, r.Width, 2));
    }

    /// <summary>The curve of the selected mode over the fight (the current fight only: the other segments have no curve).
    /// A label strip on top (the peak, the clock) keeps the text off the curve.</summary>
    static void DrawGraph(Ctx c, DpsSnapshot snap, DpsSegmentView seg, DpsHero only, RectangleF r) {
      var g = c.G; float s = c.S; bool en = c.En;
      using (var b = new SolidBrush(Color.FromArgb(160, 6, 8, 14))) g.FillRectangle(b, r);
      using (var e = new Pen(Color.FromArgb(90, 200, 170, 110), 1f)) g.DrawRectangle(e, r.X, r.Y, r.Width, r.Height);
      double[] curve = Curve(seg, c.V.Mode, only, Math.Max(8, (int)(r.Width / 3)));
      if (c.V.Segment != DpsSegment.Current || curve == null) {
        T(c, en ? "The curve is for the current fight" : "График есть только у текущего боя", Math.Max(11, 11 * s), false, Idle, r, 1);
        return;
      }
      double peak = 0; foreach (var d in curve) if (d > peak) peak = d;
      if (peak <= 0) { T(c, "-", 13, false, Idle, r, 1); return; }
      float strip = Math.Max(16, 16 * s);
      var cr = new RectangleF(r.X, r.Y + strip, r.Width, r.Height - strip);
      var pts = new PointF[curve.Length];
      float ih = cr.Height - 4 * s, iw = cr.Width - 4 * s;
      for (int i = 0; i < curve.Length; i++) pts[i] = new PointF(cr.X + 2 * s + iw * i / (curve.Length - 1), cr.Bottom - 2 * s - (float)(curve[i] / peak) * ih);
      var area = new List<PointF>(pts); area.Add(new PointF(pts[pts.Length - 1].X, cr.Bottom - 2 * s)); area.Add(new PointF(pts[0].X, cr.Bottom - 2 * s));
      using (var fb = new SolidBrush(Color.FromArgb(70, 240, 200, 110))) g.FillPolygon(fb, area.ToArray());
      using (var pen = new Pen(Color.FromArgb(255, 240, 200, 110), Math.Max(1.2f, 1.5f * s))) g.DrawLines(pen, pts);
      float spx = Math.Max(11, 11 * s);
      T(c, (en ? "peak " : "пик ") + ShortD(peak) + (en ? "/s" : "/с"), spx, false, SubCol, new RectangleF(r.X + 4 * s, r.Y, r.Width / 2, strip), 0);
      T(c, Clock(seg.Seconds), spx, false, SubCol, new RectangleF(r.X, r.Y, r.Width - 4 * s, strip), 2);
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
