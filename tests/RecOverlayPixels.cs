// The recorder's overlay compositing, on real GDI+ (Windows, or Linux with libgdiplus; tests/run-tests.sh builds it and runs it when it can):
// a fake hint frame, owl and hint box are drawn onto a dark bitmap by OverlayPaint.Composite (the code the overlay windows paint with) and the pixels are read.
//   RecOverlayPixels.exe        exit 0 = ok
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using RealmForge;

static class RecOverlayPixels {
  static int fails;
  static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } else Console.WriteLine("ok: " + what); }
  static bool Near(Color c, int rgb, int tol) { return Math.Abs(c.R - ((rgb >> 16) & 255)) <= tol && Math.Abs(c.G - ((rgb >> 8) & 255)) <= tol && Math.Abs(c.B - (rgb & 255)) <= tol; }

  static int Main() {
    const int W = 400, H = 300, SX = 50, SY = 40;   // the client at (50, 40) on the screen
    var bg = Color.FromArgb(10, 12, 20);
    using (var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb)) {
      using (var g = Graphics.FromImage(bmp)) g.Clear(bg);
      var els = new List<RecOverlayEl>();
      // a frame around a 80 x 60 target at client (60, 50) (screen px in the element's drawing: the scene's own px)
      var fs = OverlayPaint.FrameSpec(null, SX + 60, SY + 50, 80, 60, FrameStyle.Gold, 8f);
      els.Add(OverlayPaint.Element("frame", "glow", SX, SY, 60, 50, 80, 60, (g, ox, oy, bw, bh, f) => OverlayPaint.PaintFrame(g, fs, ox, oy)));
      // a hint box at client (200, 150), 150 x 50
      var plate = new OverlayPaint.Plate { Main = "Press here" };
      els.Add(OverlayPaint.Element("plate", "tip", SX, SY, 200, 150, 150, 50, (g, ox, oy, bw, bh, f) => {
        using (var fc = new Font("Arial", 10f, FontStyle.Bold, GraphicsUnit.Pixel)) using (var fm = new Font("Arial", 14f, FontStyle.Bold, GraphicsUnit.Pixel)) using (var fsub = new Font("Arial", 10f, GraphicsUnit.Pixel))
          OverlayPaint.PaintPlate(g, SX + 200 - ox, SY + 150 - oy, 150, 50, plate, fc, fm, fsub, 16f, 0f, 20f, 8f, null, SX - ox, SY - oy);
      }));
      int n;
      using (var g = Graphics.FromImage(bmp)) n = OverlayPaint.Composite(g, els, W, H, -1f);
      Check(n == 2, "both elements drawn");
      // the frame's line lies on the target's outline: 4 px gold at x = 60 (client), mid height
      Check(Near(bmp.GetPixel(60, 80), FrameStyle.Gold, 40), "frame line is gold on the target's outline");
      Check(Near(bmp.GetPixel(100, 80), 0x0A0C14, 3), "inside the frame stays the game's picture");
      Check(Near(bmp.GetPixel(300, 30), 0x0A0C14, 3), "far from any element stays the game's picture");
      // the hint box: dark navy body (0x121A2B at alpha 244), gold border 2 px at its top-left
      var body = bmp.GetPixel(210, 195);
      Check(body.B > bg.B + 10 && body.B < 80, "hint box body is dark navy, not the game's picture");
      Check(Near(bmp.GetPixel(201, 175), FrameStyle.Gold, 30), "hint box border is gold");
    }
    Console.WriteLine(fails == 0 ? "PASS" : "FAILED: " + fails);
    return fails == 0 ? 0 : 1;
  }
}
