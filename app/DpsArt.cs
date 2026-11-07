// Wardsage app - the DPS meter's art (TEST build): the fonts (Fira Sans, SIL OFL 1.1, app/res/OFL-fira-sans.txt) and, later, the picture atlases.
// Only GDI+ draws with these fonts (Graphics.DrawString / GraphicsPath.AddString), sizes are in pixels.
// Also the picture side: lazy atlases (app/res/dps_*.png|jpg), a bitmap cache (9-slice, portraits, icons, outlined text) that is dropped when the scale changes
// or it grows past CacheLimit; every frame only copies cached bitmaps (DrawImageUnscaled / DrawImage with a source rectangle).
#if TESTBUILD
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RealmForge {
  static class DpsArt {
    static readonly object gate = new object();
    static bool loaded;
    // the collections and the font data stay alive for the whole run (the memory is not freed on purpose: GDI+ keeps reading it)
    static PrivateFontCollection semiCol, boldCol;
    static FontFamily semiFam, boldFam;
    static string fallbackName;

    /// <summary>A SemiBold face (Fira Sans, else Segoe UI Semibold, else DejaVu Sans) in pixels.</summary>
    public static Font Semi(float px) { Load(); return Make(semiFam, px, FontStyle.Regular); }
    /// <summary>A Bold face (Fira Sans, else Segoe UI, else DejaVu Sans) in pixels.</summary>
    public static Font Bold(float px) { Load(); return Make(boldFam, px, FontStyle.Bold); }
    /// <summary>True when the Fira Sans files were read (false: the system fallback is used).</summary>
    public static bool HasFira { get { Load(); return semiFam != null && boldFam != null; } }

    static Font Make(FontFamily fam, float px, FontStyle want) {
      if (fam != null) {
        FontStyle st = fam.IsStyleAvailable(want) ? want : fam.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : fam.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : want;
        try { return new Font(fam, px, st, GraphicsUnit.Pixel); } catch (Exception) { }
      }
      return new Font(fallbackName, px, want, GraphicsUnit.Pixel);
    }

    static void Load() {
      if (loaded) return;
      lock (gate) {
        if (loaded) return;
        semiFam = FromResource("dps/FiraSans-SemiBold.ttf", out semiCol);
        boldFam = FromResource("dps/FiraSans-Bold.ttf", out boldCol);
        // Mono's GDI+ draws only boxes with a private-collection font; there the same Fira Sans, when installed in the system, is used by name (tests on the test server)
        if (Type.GetType("Mono.Runtime") != null) {
          try {
            if (Installed("Fira Sans SemiBold")) semiFam = new FontFamily("Fira Sans SemiBold");
            if (Installed("Fira Sans")) boldFam = new FontFamily("Fira Sans");
          } catch (Exception) { }
        }
        fallbackName = Installed("Segoe UI Semibold") ? "Segoe UI Semibold" : Installed("Segoe UI") ? "Segoe UI" : "DejaVu Sans";
        loaded = true;
      }
    }

    static bool Installed(string name) {
      try { foreach (var f in FontFamily.Families) if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return true; } catch (Exception) { }
      return false;
    }

    static FontFamily FromResource(string name, out PrivateFontCollection col) {
      col = null;
      try {
        byte[] data;
        using (var s = typeof(DpsArt).Assembly.GetManifestResourceStream(name)) {
          if (s == null) return FromFile(name, out col);
          data = new byte[s.Length]; int got = 0;
          while (got < data.Length) { int n = s.Read(data, got, data.Length - got); if (n <= 0) break; got += n; }
        }
        var pc = new PrivateFontCollection();
        IntPtr mem = Marshal.AllocCoTaskMem(data.Length);   // never freed
        Marshal.Copy(data, 0, mem, data.Length);
        pc.AddMemoryFont(mem, data.Length);
        if (pc.Families.Length > 0) { col = pc; return pc.Families[0]; }
      } catch (Exception) { }
      return null;
    }

    // a run outside the exe (the render test, Mono): the same file from app/res next to the sources
    static FontFamily FromFile(string name, out PrivateFontCollection col) {
      col = null;
      try {
        string file = Path.GetFileName(name);
        foreach (var dir in new[] { "app/res", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res"), "." }) {
          string p = Path.Combine(dir, file);
          if (!File.Exists(p)) continue;
          var pc = new PrivateFontCollection(); pc.AddFontFile(p);
          if (pc.Families.Length > 0) { col = pc; return pc.Families[0]; }
        }
      } catch (Exception) { }
      return null;
    }

    // ================================================================ pictures
    public enum Ico { Own, Generic, None }
    /// <summary>One text drawn by Text() (a test hook: set Probe to a list to record them).</summary>
    public sealed class TextRec { public string Text; public RectangleF Box, Glyphs; public float Width; public Color Fill; }
    public static List<TextRec> Probe = null;
    /// <summary>A test hook: Text() records but draws nothing (to read the background under the glyphs).</summary>
    public static bool Suppress = false;
    /// <summary>Counters of what Portrait / SkillIcon found (the render test resets and reads them).</summary>
    public static int PortraitReal, PortraitFallback, IconOwn, IconGeneric, IconNone;
    public const int CacheLimit = 400;

    static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
    static readonly Dictionary<string, float> widths = new Dictionary<string, float>();
    static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    static float cacheS = -1;
    static Bitmap uiAtlas, headAtlas, skillAtlas, frameTex;
    static ImageAttributes wrap;
    static Bitmap measBmp; static Graphics meas;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Call at the start of each frame: a new scale or a too big cache drops the cached bitmaps.</summary>
    public static void Begin(float s) {
      lock (gate) {
        if (Math.Abs(s - cacheS) > 0.0001f || cache.Count > CacheLimit) { foreach (var b in cache.Values) b.Dispose(); cache.Clear(); widths.Clear(); }
        if (widths.Count > 4000) widths.Clear();
        cacheS = s;
      }
    }
    public static int CacheCount { get { return cache.Count; } }

    public static Bitmap Cached(string key, Func<Bitmap> make) {
      Bitmap b; if (cache.TryGetValue(key, out b)) return b;
      b = make(); if (b != null) cache[key] = b; return b;
    }

    static Bitmap Detach(Bitmap im) {
      var o = new Bitmap(im.Width, im.Height, PixelFormat.Format32bppPArgb);
      using (var g = Graphics.FromImage(o)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(im, 0, 0, im.Width, im.Height); }
      return o;
    }
    static Bitmap LoadBitmap(string res, string file) {
      try {
        using (var s = typeof(DpsArt).Assembly.GetManifestResourceStream(res))
          if (s != null) using (var im = new Bitmap(s)) return Detach(im);
        foreach (var dir in new[] { "app/res", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res"), "." }) {
          string p = Path.Combine(dir, file);
          if (File.Exists(p)) using (var im = new Bitmap(p)) return Detach(im);
        }
      } catch (Exception) { }
      return null;
    }
    public static Bitmap UiAtlas { get { lock (gate) { return uiAtlas ?? (uiAtlas = LoadBitmap("dps/dps_ui.png", "dps_ui.png")); } } }
    public static Bitmap HeadAtlas { get { lock (gate) { return headAtlas ?? (headAtlas = LoadBitmap("dps/dps_heads.jpg", "dps_heads.jpg")); } } }
    public static Bitmap SkillAtlas { get { lock (gate) { return skillAtlas ?? (skillAtlas = LoadBitmap("dps/dps_skills.jpg", "dps_skills.jpg")); } } }
    public static Bitmap FrameTex { get { lock (gate) { return frameTex ?? (frameTex = LoadBitmap("overlay/frame.png", "frame.png")); } } }
    /// <summary>True when the three atlases were read.</summary>
    public static bool HasAtlases { get { return UiAtlas != null && HeadAtlas != null && SkillAtlas != null; } }

    /// <summary>Clamp edges: no seams between the slices.</summary>
    static ImageAttributes Wrap {
      get { if (wrap == null) { wrap = new ImageAttributes(); wrap.SetWrapMode(WrapMode.TileFlipXY); } return wrap; }
    }
    static ImageAttributes Tint(float r, float g, float b, float a) {
      var ia = new ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipXY);
      ia.SetColorMatrix(new ColorMatrix(new[] { new[] { r, 0, 0, 0, 0f }, new[] { 0, g, 0, 0, 0f }, new[] { 0, 0, b, 0, 0f }, new[] { 0, 0, 0, a, 0f }, new[] { 0, 0, 0, 0, 1f } }));
      return ia;
    }
    static void HqSetup(Graphics g) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.Half; g.SmoothingMode = SmoothingMode.AntiAlias; }

    static void Blit(Graphics g, Bitmap src, float sx, float sy, float sw, float sh, int dx, int dy, int dw, int dh, ImageAttributes ia) {
      if (sw <= 0.01f || sh <= 0.01f || dw <= 0 || dh <= 0) return;
      g.DrawImage(src, new Rectangle(dx, dy, dw, dh), sx, sy, sw, sh, GraphicsUnit.Pixel, ia ?? Wrap);
    }

    // GDI+ (Mono's above all) lets a wrap mode read past the source rectangle into the neighbours of an atlas, so every piece is cut out into its own bitmap first.
    static Bitmap Crop(Bitmap atl, float x, float y, float w, float h) {
      int ix = (int)Math.Round(x), iy = (int)Math.Round(y), iw = Math.Max(1, (int)Math.Round(w)), ih = Math.Max(1, (int)Math.Round(h));
      var o = new Bitmap(iw, ih, PixelFormat.Format32bppPArgb);
      using (var g = Graphics.FromImage(o)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(atl, new Rectangle(0, 0, iw, ih), ix, iy, iw, ih, GraphicsUnit.Pixel); }
      return o;
    }
    /// <summary>A UI piece cut out of the atlas into its own bitmap (cached).</summary>
    static Bitmap PieceBmp(string name) {
      var p = DpsArtIndex.Piece(name); var a = UiAtlas; if (p == null || a == null) return null;
      return Cached("pc|" + name, () => Crop(a, p[0], p[1], p[2], p[3]));
    }
    /// <summary>The same for a rectangle of the head / skill atlas.</summary>
    static Bitmap RectBmp(string key, Bitmap atl, int[] r) {
      if (atl == null || r == null) return null;
      return Cached(key, () => Crop(atl, r[0], r[1], r[2], r[3]));
    }

    /// <summary>A UI piece of the atlas stretched into dest (per frame; for bars and strips).</summary>
    public static void DrawPiece(Graphics g, string name, RectangleF dest) {
      var a = PieceBmp(name);
      if (a == null) return;
      var m = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.Half;
      Blit(g, a, 0, 0, a.Width, a.Height, (int)Math.Round(dest.X), (int)Math.Round(dest.Y), (int)Math.Round(dest.Width), (int)Math.Round(dest.Height), null);
      g.PixelOffsetMode = m;
    }

    /// <summary>The bar fill by a sprite name (bar_red, bar_yellow, bar_purple, or "green" = bar_blue recoloured).</summary>
    public static void DrawFill(Graphics g, string kind, RectangleF r) {
      if (r.Width < 1 || r.Height < 1) return;
      if (kind == "green") {
        var gb = Cached("green", () => {
          var a = PieceBmp("bar_blue"); if (a == null) return null;
          var o = new Bitmap(a.Width, a.Height, PixelFormat.Format32bppPArgb);
          var cm = new ColorMatrix(new[] { new[] { 0f, 0f, 0.5f, 0, 0 }, new[] { 0.45f, 0f, 0f, 0, 0 }, new[] { 0f, 0.95f, 0f, 0, 0 }, new[] { 0f, 0f, 0f, 1, 0 }, new[] { 0f, 0f, 0f, 0, 1 } });
          using (var g2 = Graphics.FromImage(o)) using (var ia = new ImageAttributes()) { ia.SetColorMatrix(cm); g2.DrawImage(a, new Rectangle(0, 0, a.Width, a.Height), 0, 0, a.Width, a.Height, GraphicsUnit.Pixel, ia); }
          return o;
        });
        if (gb == null) return;
        var m = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.Half;
        Blit(g, gb, 0, 0, gb.Width, gb.Height, (int)Math.Round(r.X), (int)Math.Round(r.Y), Math.Max(1, (int)Math.Round(r.Width)), (int)Math.Round(r.Height), null);
        g.PixelOffsetMode = m;
        return;
      }
      var q = new RectangleF(r.X, r.Y, Math.Max(1, r.Width), r.Height);
      DrawPiece(g, kind, q);
    }

    // ---------------------------------------------------------------- 9-slice
    /// <summary>The sprite cut by its visible box and 9-sliced (borders from src/SpriteArtData.cs, l,b,r,t, scaled by k) into w x h; alpha 0..1.</summary>
    public static Bitmap Nine(string name, int w, int h, float k, float alpha, float bright = 1f) {
      if (w < 2 || h < 2) return null;
      return Cached("9|" + name + "|" + w + "|" + h + "|" + k.ToString("0.###", Inv) + "|" + alpha.ToString("0.##", Inv) + "|" + bright.ToString("0.##", Inv), () => MakeNine(name, w, h, k, alpha, bright));
    }

    static void Visible(string name, int[] p, out float vx, out float vy, out float vw, out float vh, out float bl, out float bt, out float br, out float bb) {
      var a = SpriteArt.Of(name); double W = p[2], H = p[3];
      double L = a != null ? a.L : 0, T = a != null ? a.T : 0, R = a != null ? a.R : 1, B = a != null ? a.B : 1;
      vx = p[0] + (float)(L * W); vy = p[1] + (float)(T * H); vw = (float)((R - L) * W); vh = (float)((B - T) * H);
      bl = bt = br = bb = 0;
      if (a != null) {
        bl = (float)Math.Max(0, a.BL - L * W); br = (float)Math.Max(0, a.BR - (1 - R) * W);
        bt = (float)Math.Max(0, a.BT - T * H); bb = (float)Math.Max(0, a.BB - (1 - B) * H);
      }
      if (bl + br > vw - 1) { float f = (vw - 1) / (bl + br); bl *= f; br *= f; }
      if (bt + bb > vh - 1) { float f = (vh - 1) / (bt + bb); bt *= f; bb *= f; }
    }

    /// <summary>Scale k that makes the visible height of a sprite equal h (for buttons).</summary>
    public static float HeightScale(string name, float h) {
      var p = DpsArtIndex.Piece(name); if (p == null) return 1;
      float vx, vy, vw, vh, a, b, c, d; Visible(name, p, out vx, out vy, out vw, out vh, out a, out b, out c, out d);
      return vh > 0 ? h / vh : 1;
    }

    static Bitmap MakeNine(string name, int w, int h, float k, float alpha, float bright) {
      var p = DpsArtIndex.Piece(name); var ua = UiAtlas; if (p == null || ua == null) return null;
      float vx, vy, vw, vh, bl, bt, br, bb; Visible(name, p, out vx, out vy, out vw, out vh, out bl, out bt, out br, out bb);
      var atl = Cached("vis|" + name, () => Crop(ua, vx, vy, vw, vh));
      vw = atl.Width; vh = atl.Height; vx = 0; vy = 0;
      float dl = bl * k, dr = br * k, dt = bt * k, db = bb * k;
      if (dl + dr > w - 1) { float f = (w - 1) / (dl + dr); dl *= f; dr *= f; }
      if (dt + db > h - 1) { float f = (h - 1) / (dt + db); dt *= f; db *= f; }
      var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
      float[] sx = { vx, vx + bl, vx + vw - br, vx + vw }, sy = { vy, vy + bt, vy + vh - bb, vy + vh };
      int[] dx = { 0, (int)Math.Round(dl), w - (int)Math.Round(dr), w }, dy = { 0, (int)Math.Round(dt), h - (int)Math.Round(db), h };
      using (var g = Graphics.FromImage(bmp)) {
        HqSetup(g);
        ImageAttributes ia = alpha < 0.999f || bright < 0.999f ? Tint(bright, bright, bright, alpha) : null;
        try {
          for (int j = 0; j < 3; j++) for (int i = 0; i < 3; i++)
            Blit(g, atl, sx[i], sy[j], sx[i + 1] - sx[i], sy[j + 1] - sy[j], dx[i], dy[j], dx[i + 1] - dx[i], dy[j + 1] - dy[j], ia);
        } finally { if (ia != null) ia.Dispose(); }
      }
      return bmp;
    }

    /// <summary>The popup title plaque (common_bg_popup_titel, no borders in the data): the left 120 px and the right 60 px are fixed, the middle stretches.</summary>
    public static Bitmap Plaque(int w, int h, float bright = 1f) {
      if (w < 8 || h < 4) return null;
      return Cached("pl|" + w + "|" + h + "|" + bright.ToString("0.##", Inv), () => {
        const string name = "common_bg_popup_titel";
        var p = DpsArtIndex.Piece(name); var ua = UiAtlas; if (p == null || ua == null) return null;
        float vx, vy, vw, vh, a, b, c, d; Visible(name, p, out vx, out vy, out vw, out vh, out a, out b, out c, out d);
        var atl = Cached("vis|" + name, () => Crop(ua, vx, vy, vw, vh));
        float lsrc = Math.Min(120 - (vx - p[0]), vw * 0.4f); vx = 0; vy = 0; vw = atl.Width; vh = atl.Height;
        lsrc = Math.Min(lsrc, vw * 0.4f); float rsrc = Math.Min(60, vw * 0.25f);
        float k = h / vh, dl = lsrc * k, dr = rsrc * k;
        if (dl + dr > w - 2) { float f = (w - 2) / (dl + dr); dl *= f; dr *= f; }
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp)) {
          HqSetup(g);
          int l = (int)Math.Round(dl), r = (int)Math.Round(dr);
          using (var ia = bright < 0.999f ? Tint(bright, bright, bright, 1f) : null) {
            Blit(g, atl, vx, vy, lsrc, vh, 0, 0, l, h, ia);
            Blit(g, atl, vx + lsrc, vy, vw - lsrc - rsrc, vh, l, 0, w - l - r, h, ia);
            Blit(g, atl, vx + vw - rsrc, vy, rsrc, vh, w - r, 0, r, h, ia);
          }
        }
        return bmp;
      });
    }

    /// <summary>A UI piece stretched to w x h, darkened (f 0..1 on the colour) and see-through (alpha); cached.</summary>
    public static Bitmap Darkened(string name, int w, int h, float f, float alpha) {
      if (w < 2 || h < 2) return null;
      return Cached("dk|" + name + "|" + w + "|" + h + "|" + f.ToString("0.##", Inv) + "|" + alpha.ToString("0.##", Inv), () => {
        var atl = PieceBmp(name); if (atl == null) return null;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp)) using (var ia = Tint(f, f, f, alpha)) { HqSetup(g); Blit(g, atl, 0, 0, atl.Width, atl.Height, 0, 0, w, h, ia); }
        return bmp;
      });
    }

    /// <summary>The glowing frame (frame.png) 9-sliced around w x h.</summary>
    public static Bitmap GlowFrame(int w, int h, float k) {
      if (w < 8 || h < 8) return null;
      return Cached("gf|" + w + "|" + h + "|" + k.ToString("0.##", Inv), () => {
        var t = FrameTex; if (t == null) return null;
        float b = 30, d = Math.Min(b * k, Math.Min(w, h) / 2f - 1);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        float[] s = { 0, b, t.Width - b, t.Width }, sy = { 0, b, t.Height - b, t.Height };
        int di = (int)Math.Round(d);
        int[] dx = { 0, di, w - di, w }, dy = { 0, di, h - di, h };
        using (var g = Graphics.FromImage(bmp)) {
          HqSetup(g);
          for (int j = 0; j < 3; j++) for (int i = 0; i < 3; i++) {
            if (i == 1 && j == 1) continue;   // the middle stays empty
            Blit(g, t, s[i], sy[j], s[i + 1] - s[i], sy[j + 1] - sy[j], dx[i], dy[j], dx[i + 1] - dx[i], dy[j + 1] - dy[j], null);
          }
        }
        return bmp;
      });
    }

    // ---------------------------------------------------------------- portraits and icons
    static readonly Color[] RarityCol = {
      Color.FromArgb(255, 120, 124, 136), Color.FromArgb(255, 150, 154, 164), Color.FromArgb(255, 96, 184, 100),
      Color.FromArgb(255, 84, 146, 235), Color.FromArgb(255, 176, 106, 236), Color.FromArgb(255, 244, 204, 96) };

    /// <summary>Draws a hero's portrait px x px at (x, y): a square with a rarity frame, or a circle with a thin gold ring and the class icon.
    /// The picture is found by the unit, the base id, then hero/100000; else the class icon on a plate. Returns true when the portrait itself was found.</summary>
    public static bool Portrait(Graphics g, int unit, int baseId, uint hero, float x, float y, int px, bool circle) {
      int id = unit; int[] r = unit > 0 ? DpsArtIndex.Head(id) : null;
      if (r == null && baseId > 0) { id = baseId; r = DpsArtIndex.Head(id); }
      if (r == null && hero >= 100000) { id = (int)(hero / 100000); r = DpsArtIndex.Head(id); }
      bool real = r != null;
      if (real) PortraitReal++; else PortraitFallback++;
      int clsOf = real ? id : (unit > 0 ? unit : baseId > 0 ? baseId : (int)(hero / 100000));
      string key = "pt|" + (real ? id.ToString(Inv) : "x" + clsOf.ToString(Inv)) + "|" + px + "|" + (circle ? "c" : "s");
      var bmp = Cached(key, () => MakePortrait(real ? r : null, id, clsOf, px, circle));
      if (bmp != null) g.DrawImageUnscaled(bmp, (int)Math.Round(x), (int)Math.Round(y));
      return real;
    }

    static Bitmap MakePortrait(int[] r, int id, int clsOf, int px, bool circle) {
      var bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
      float s = Math.Max(0.7f, cacheS);
      using (var g = Graphics.FromImage(bmp)) {
        HqSetup(g);
        var atl = r != null ? RectBmp("hd|" + id, HeadAtlas, r) : null;
        int rar = DpsArtIndex.Rarity(id); if (rar < 0 || rar >= RarityCol.Length) rar = 0;
        float bw = Math.Max(1.5f, 2 * s);
        if (atl != null) {
          if (!circle) {
            using (var b = new SolidBrush(RarityCol[rar])) g.FillRectangle(b, 0, 0, px, px);
            int bi = (int)Math.Round(bw);
            Blit(g, atl, 0, 0, atl.Width, atl.Height, bi, bi, px - 2 * bi, px - 2 * bi, null);
          } else {
            using (var inner = new Bitmap(px, px, PixelFormat.Format32bppPArgb)) {
              using (var g2 = Graphics.FromImage(inner)) { HqSetup(g2); Blit(g2, atl, 0, 0, atl.Width, atl.Height, 0, 0, px, px, null); }
              using (var tb = new TextureBrush(inner, WrapMode.Clamp)) g.FillEllipse(tb, 0.8f, 0.8f, px - 1.6f, px - 1.6f);
            }
            using (var pen = new Pen(Color.FromArgb(235, 226, 184, 96), Math.Max(1.2f, 1.4f * s))) g.DrawEllipse(pen, 1f, 1f, px - 2f, px - 2f);
            string cp = DpsArtIndex.ClassPiece(id);
            DrawClassBadge(g, cp, px, px * 0.46f);
          }
        } else {
          // no portrait: the class icon on a plate (or a plain grey one)
          using (var b = new SolidBrush(Color.FromArgb(255, 44, 48, 60))) { if (circle) g.FillEllipse(b, 0.8f, 0.8f, px - 1.6f, px - 1.6f); else g.FillRectangle(b, 0, 0, px, px); }
          string cp = DpsArtIndex.ClassPiece(clsOf); var ua = cp.Length > 0 ? PieceBmp(cp) : null;
          if (ua != null) { int m = px / 5; Blit(g, ua, 0, 0, ua.Width, ua.Height, m, m, px - 2 * m, px - 2 * m, null); }
          using (var pen = new Pen(Color.FromArgb(200, 150, 154, 164), Math.Max(1f, 1.2f * s))) { if (circle) g.DrawEllipse(pen, 1f, 1f, px - 2f, px - 2f); else g.DrawRectangle(pen, 0.5f, 0.5f, px - 1.5f, px - 1.5f); }
        }
      }
      return bmp;
    }

    static void DrawClassBadge(Graphics g, string piece, int px, float d) {
      var ua = piece.Length > 0 ? PieceBmp(piece) : null;
      if (ua == null) return;
      float x = px - d, y = px - d;
      using (var b = new SolidBrush(Color.FromArgb(220, 14, 12, 20))) g.FillEllipse(b, x, y, d, d);
      int m = (int)Math.Round(d * 0.12f);
      Blit(g, ua, 0, 0, ua.Width, ua.Height, (int)Math.Round(x) + m, (int)Math.Round(y) + m, (int)Math.Round(d) - 2 * m, (int)Math.Round(d) - 2 * m, null);
    }

    /// <summary>A skill icon px x px in a thin dark frame; its own icon, else the common one (skill_20810), else a grey square.</summary>
    public static Ico SkillIcon(Graphics g, int skillId, float x, float y, int px) {
      int[] r = skillId > 0 ? DpsArtIndex.Skill(skillId) : null; Ico k = Ico.Own;
      if (r == null) { k = Ico.Generic; DpsArtIndex.Skills.TryGetValue("skill_20810", out r); }
      if (r == null) k = Ico.None;
      if (k == Ico.Own) IconOwn++; else if (k == Ico.Generic) IconGeneric++; else IconNone++;
      string key = "si|" + (k == Ico.Own ? skillId.ToString(Inv) : k == Ico.Generic ? "g" : "n") + "|" + px;
      var bmp = Cached(key, () => {
        var o = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
        using (var g2 = Graphics.FromImage(o)) {
          HqSetup(g2);
          using (var b = new SolidBrush(k == Ico.None ? Color.FromArgb(255, 96, 100, 110) : Color.FromArgb(255, 14, 12, 20))) g2.FillRectangle(b, 0, 0, px, px);
          var atl = k != Ico.None ? RectBmp("sk|" + (k == Ico.Own ? skillId.ToString(Inv) : "g"), SkillAtlas, r) : null;
          if (atl != null) Blit(g2, atl, 0, 0, atl.Width, atl.Height, 1, 1, px - 2, px - 2, null);
          using (var pen = new Pen(Color.FromArgb(230, 8, 6, 12), 1f)) g2.DrawRectangle(pen, 0, 0, px - 1, px - 1);
        }
        return o;
      });
      if (bmp != null) g.DrawImageUnscaled(bmp, (int)Math.Round(x), (int)Math.Round(y));
      return k;
    }

    // ---------------------------------------------------------------- outlined text
    static Font TextFont(float px, bool bold) {
      string key = (bold ? "b" : "s") + px.ToString("0.##", Inv);
      Font f; if (fonts.TryGetValue(key, out f)) return f;
      f = bold ? Bold(px) : Semi(px); fonts[key] = f; return f;
    }

    // the line height in px from the family's metrics (Font.Height fails on private fonts under Mono's GDI+)
    static int LineH(Font f) {
      try {
        var fam = f.FontFamily; var st = f.Style; int em = fam.GetEmHeight(st);
        if (em > 0) return (int)Math.Ceiling(f.Size * (fam.GetCellAscent(st) + fam.GetCellDescent(st)) / (double)em);
      } catch (Exception) { }
      return (int)Math.Ceiling(f.Size * 1.3);
    }

    /// <summary>The width of a text in px (no outline), cached.</summary>
    public static float Measure(string t, float px, bool bold) {
      if (string.IsNullOrEmpty(t)) return 0;
      string key = (bold ? "b" : "s") + px.ToString("0.##", Inv) + "|" + t;
      float w; if (widths.TryGetValue(key, out w)) return w;
      lock (gate) { if (meas == null) { measBmp = new Bitmap(4, 4); meas = Graphics.FromImage(measBmp); meas.TextRenderingHint = TextRenderingHint.AntiAlias; } }
      w = meas.MeasureString(t, TextFont(px, bold), new PointF(0, 0), StringFormat.GenericTypographic).Width;
      widths[key] = w; return w;
    }

    /// <summary>Draws a text with a dark outline (Pen 2.6*s, round joins) and a fill, from a cached bitmap, inside box (align 0 left, 1 centre, 2 right,
    /// vertically centred); a text wider than the box is cut with "…". Returns the width drawn.</summary>
    public static float Text(Graphics g, string t, float px, bool bold, Color fill, RectangleF box, int align) {
      if (string.IsNullOrEmpty(t) || box.Width < 4) return 0;
      float tw = Measure(t, px, bold);
      if (tw > box.Width) {
        int n = t.Length;
        while (n > 1) { n--; string c = t.Substring(0, n).TrimEnd() + "…"; float cw = Measure(c, px, bold); if (cw <= box.Width) { t = c; tw = cw; break; } }
        if (tw > box.Width) return 0;
      }
      float s = Math.Max(0.7f, cacheS);
      float pen = Math.Max(2f, 2.6f * s);
      int m = (int)Math.Ceiling(pen / 2) + 1;
      Font f = TextFont(px, bold);
      int fh = LineH(f);
      string key = "tx|" + (bold ? "b" : "s") + px.ToString("0.##", Inv) + "|" + fill.ToArgb().ToString("x") + "|" + t;
      var bmp = Cached(key, () => {
        int bw = (int)Math.Ceiling(Measure(t, px, bold)) + 2 * m + 2, bh = fh + 2 * m;
        var o = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
        using (var g2 = Graphics.FromImage(o))
        using (var path = new GraphicsPath()) {
          g2.SmoothingMode = SmoothingMode.AntiAlias;
          path.AddString(t, f.FontFamily, (int)f.Style, f.Size, new PointF(m, m), StringFormat.GenericTypographic);
          using (var op = new Pen(Color.FromArgb(230, 10, 8, 14), pen) { LineJoin = LineJoin.Round }) g2.DrawPath(op, path);
          using (var fb = new SolidBrush(fill)) g2.FillPath(fb, path);
        }
        return o;
      });
      float x = align == 0 ? box.X : align == 1 ? box.X + (box.Width - tw) / 2 : box.Right - tw;
      float y = box.Y + (box.Height - fh) / 2f;
      if (bmp != null && !Suppress) g.DrawImageUnscaled(bmp, (int)Math.Round(x) - m, (int)Math.Round(y) - m);
      var pr = Probe;
      if (pr != null) pr.Add(new TextRec { Text = t, Box = box, Width = tw, Glyphs = new RectangleF(x, y, tw, fh), Fill = fill });
      return tw;
    }
  }
}
#endif
