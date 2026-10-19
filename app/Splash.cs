// Wardsage.exe - the startup splash: covers the black window while WebView2 starts and the page boots.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace RealmForge {
  sealed class Splash : Control {
    static Image owl;
    readonly Timer timer = new Timer();
    readonly bool en;
    string status;
    float phase;   // 0..1, the position of the sliding segment

    public Splash() {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
      DoubleBuffered = true;
      Dock = DockStyle.Fill;
      BackColor = Color.FromArgb(11, 16, 27);
      string lang = "ru";
      try { lang = AppConfig.Load().Lang; } catch (Exception) { }
      en = lang == "en";
      status = en ? "Starting…" : "Запускаю…";
      timer.Interval = 33;
      timer.Tick += (s, e) => { phase = (phase + 0.012f) % 1f; Invalidate(); };
      VisibleChanged += (s, e) => { if (Visible && !IsDisposed) timer.Start(); else timer.Stop(); };
    }

    public void SetStatus(string ru, string en) {
      status = this.en ? en : ru;
      Invalidate();
    }

    static Image Owl() {
      if (owl == null) {
        try { using (var s = typeof(Splash).Assembly.GetManifestResourceStream("overlay/owl.png")) if (s != null) owl = new Bitmap(Image.FromStream(s)); }
        catch (Exception) { }
      }
      return owl;
    }

    static Font Pick(string name, float size, FontStyle st) {
      try { var f = new Font(name, size, st, GraphicsUnit.Pixel); if (f.Name == name) return f; f.Dispose(); } catch (Exception) { }
      return new Font(SystemFonts.DefaultFont.FontFamily, size, st, GraphicsUnit.Pixel);
    }

    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics;
      g.Clear(BackColor);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.InterpolationMode = InterpolationMode.HighQualityBicubic;
      g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
      float k = g.DpiX / 96f;
      int cx = Width / 2;
      int owlSize = (int)(108 * k);
      int total = owlSize + (int)(86 * k);
      int top = Math.Max(0, (Height - total) / 2);
      var img = Owl();
      if (img != null) g.DrawImage(img, new Rectangle(cx - owlSize / 2, top, owlSize, owlSize));
      var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
      int y = top + owlSize + (int)(8 * k);
      using (var f = Pick("Georgia", 30 * k, FontStyle.Bold))
      using (var b = new SolidBrush(Color.FromArgb(215, 170, 82)))
        g.DrawString("Wardsage", f, b, new RectangleF(0, y, Width, 40 * k), sf);
      y += (int)(46 * k);
      int tw = (int)(220 * k), th = Math.Max(3, (int)(3.5f * k));
      var track = new Rectangle(cx - tw / 2, y, tw, th);
      using (var b = new SolidBrush(Color.FromArgb(44, 50, 64))) g.FillRectangle(b, track);
      int sw = tw * 2 / 5;
      int sx = track.X - sw + (int)((tw + sw) * phase);
      var seg = Rectangle.Intersect(new Rectangle(sx, y, sw, th), track);
      if (seg.Width > 1) {
        using (var b = new LinearGradientBrush(new Rectangle(sx, y, sw, th), Color.FromArgb(0, 215, 170, 82), Color.FromArgb(0, 215, 170, 82), 0f)) {
          b.InterpolationColors = new ColorBlend {
            Positions = new[] { 0f, 0.5f, 1f },
            Colors = new[] { Color.FromArgb(60, 168, 128, 52), Color.FromArgb(255, 235, 190, 100), Color.FromArgb(60, 168, 128, 52) }
          };
          g.FillRectangle(b, seg);
        }
      }
      y += th + (int)(14 * k);
      using (var f = Pick("Segoe UI", 13 * k, FontStyle.Regular))
      using (var b = new SolidBrush(Color.FromArgb(157, 149, 131)))
        g.DrawString(status, f, b, new RectangleF(0, y, Width, 24 * k), sf);
      sf.Dispose();
    }

    protected override void Dispose(bool disposing) {
      if (disposing) { timer.Stop(); timer.Dispose(); }
      base.Dispose(disposing);
    }
  }
}
