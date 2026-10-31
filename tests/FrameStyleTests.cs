// The hint frame's look (src/FrameTargets.cs FrameStyle): a small software renderer draws the frame's layers for a 74 x 74 cell and a 60 px circle
// and the tests read pixel colours on the line, inside, outside and in the gap to a neighbour. The renders are saved as PNG when the folder in the
// environment variable FRAME_RENDER_DIR exists (4x enlarged), for the owner to look at. C# 5, no WinForms.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using RealmForge;

static class FrameStyleTests {
  const int W = 140, H = 140, SS = 4;

  static double Round(double px, double py, double x, double y, double w, double h, double r) {   // signed distance to a rounded rect (outside +)
    double cx = x + w / 2, cy = y + h / 2, qx = Math.Abs(px - cx) - (w / 2 - r), qy = Math.Abs(py - cy) - (h / 2 - r);
    return Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - r;
  }

  delegate double Sd(double px, double py);

  static void Blend(double[] px, int i, int rgb, double a) {
    double[] c = { (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255 };
    for (int k = 0; k < 3; k++) px[i * 3 + k] = px[i * 3 + k] * (1 - a) + c[k] * a;
  }

  static double[] NewCanvas(int bg) {
    var px = new double[W * H * 3];
    for (int i = 0; i < W * H; i++) Blend(px, i, bg, 1);
    return px;
  }

  static void Layer(double[] px, Sd sd, FrameStyle.Layer l) {
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
      int hit = 0;
      for (int a = 0; a < SS; a++) for (int b = 0; b < SS; b++)
        if (Math.Abs(sd(x + (a + 0.5) / SS, y + (b + 0.5) / SS) - l.Offset) <= l.Width / 2) hit++;
      if (hit > 0) Blend(px, y * W + x, l.Rgb, l.Alpha / 255.0 * hit / (SS * SS));
    }
  }

  static bool InPoly(double[] p, double x, double y) {
    bool inside = false; int n = p.Length / 2;
    for (int i = 0, j = n - 1; i < n; j = i++)
      if ((p[2 * i + 1] > y) != (p[2 * j + 1] > y) && x < (p[2 * j] - p[2 * i]) * (y - p[2 * i + 1]) / (p[2 * j + 1] - p[2 * i + 1]) + p[2 * i]) inside = !inside;
    return inside;
  }

  static void Owl(double[] px, double[] at) {
    var sh = FrameStyle.OwlShield(at[0], at[1]);
    var eyes = FrameStyle.OwlEyes(at[0], at[1]);
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
      double fx = x + 0.5, fy = y + 0.5;
      if (!InPoly(sh, fx, fy)) continue;
      int rgb = FrameStyle.Navy;
      foreach (var e in eyes) { double d = Math.Sqrt((fx - e[0]) * (fx - e[0]) + (fy - e[1]) * (fy - e[1])); if (d <= e[2] && d > e[3]) rgb = FrameStyle.Gold; }
      Blend(px, y * W + x, rgb, 1);
    }
  }

  static int At(double[] px, int x, int y) { int i = (y * W + x) * 3; return ((int)Math.Round(px[i]) << 16) | ((int)Math.Round(px[i + 1]) << 8) | (int)Math.Round(px[i + 2]); }
  static bool Near(int a, int b) { return Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= 3 && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= 3 && Math.Abs((a & 255) - (b & 255)) <= 3; }
  static string Hex(int a) { return "#" + a.ToString("X6"); }

  const int Bg = 0x3A2C2C, Cell = 0x8A4444, Nb = 0x44668A;

  static double[] RenderRect(int rgb, float gap, float phase, bool pulse, bool neighbour, double clientTop, bool owl) {
    var px = NewCanvas(Bg);
    // the cell and its neighbour (after a gap of 3 px) are painted first, the frame over them
    for (int y = 33; y < 107; y++) for (int x = 33; x < 107; x++) Blend(px, y * W + x, Cell, 1);
    if (neighbour) for (int y = 33; y < 107; y++) for (int x = 110; x < 140; x++) Blend(px, y * W + x, Nb, 1);
    Sd sd = (a, b) => Round(a, b, 33, 33, 74, 74, FrameStyle.CornerR);
    foreach (var l in FrameStyle.Still(rgb, gap)) Layer(px, sd, l);
    if (pulse) { var pl = FrameStyle.Pulse(rgb, phase, gap); if (pl != null) Layer(px, sd, pl); }
    if (owl) Owl(px, FrameStyle.OwlAt(33, 33, 74, 74, clientTop));
    return px;
  }

  static double[] RenderCircle(int rgb, float phase) {
    var px = NewCanvas(Bg);
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { double d = Math.Sqrt((x + 0.5 - 70) * (x + 0.5 - 70) + (y + 0.5 - 70) * (y + 0.5 - 70)); if (d < 30) Blend(px, y * W + x, Cell, 1); }
    Sd sd = (a, b) => Math.Sqrt((a - 70) * (a - 70) + (b - 70) * (b - 70)) - 30;
    foreach (var l in FrameStyle.Still(rgb, FrameStyle.NoLimit)) Layer(px, sd, l);
    var pl = FrameStyle.Pulse(rgb, phase, FrameStyle.NoLimit); if (pl != null) Layer(px, sd, pl);
    return px;
  }

  static void Save(string name, double[] px) {
    string dir = Environment.GetEnvironmentVariable("FRAME_RENDER_DIR");
    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
    int S = 4, ow = W * S, oh = H * S;
    var raw = new byte[(ow * 3 + 1) * oh];
    for (int y = 0; y < oh; y++) for (int x = 0; x < ow; x++) for (int k = 0; k < 3; k++) raw[y * (ow * 3 + 1) + 1 + x * 3 + k] = (byte)Math.Max(0, Math.Min(255, Math.Round(px[((y / S) * W + x / S) * 3 + k])));
    var z = new MemoryStream();
    z.WriteByte(0x78); z.WriteByte(0x01);
    using (var d = new DeflateStream(z, CompressionMode.Compress, true)) d.Write(raw, 0, raw.Length);
    uint a1 = 1, a2 = 0; foreach (byte b in raw) { a1 = (a1 + b) % 65521; a2 = (a2 + a1) % 65521; }
    uint ad = (a2 << 16) | a1; z.WriteByte((byte)(ad >> 24)); z.WriteByte((byte)(ad >> 16)); z.WriteByte((byte)(ad >> 8)); z.WriteByte((byte)ad);
    using (var f = new FileStream(Path.Combine(dir, name), FileMode.Create)) {
      f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
      Chunk(f, "IHDR", new byte[] { (byte)(ow >> 24), (byte)(ow >> 16), (byte)(ow >> 8), (byte)ow, (byte)(oh >> 24), (byte)(oh >> 16), (byte)(oh >> 8), (byte)oh, 8, 2, 0, 0, 0 });
      Chunk(f, "IDAT", z.ToArray()); Chunk(f, "IEND", new byte[0]);
    }
  }
  static void Chunk(Stream f, string type, byte[] data) {
    var t = new byte[4 + data.Length]; for (int i = 0; i < 4; i++) t[i] = (byte)type[i]; Array.Copy(data, 0, t, 4, data.Length);
    f.Write(new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }, 0, 4);
    f.Write(t, 0, t.Length);
    uint crc = 0xFFFFFFFF; foreach (byte b in t) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
    crc ^= 0xFFFFFFFF; f.Write(new byte[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }, 0, 4);
  }

  public static void Run(Action<bool, string> Check) {
    int G = FrameStyle.Gold;
    Func<double[], int, int, int, string, bool> Px = (p, x, y, want, what) => { int got = At(p, x, y); bool ok = Near(got, want); Check(ok, "frame pixel (" + x + "," + y + ") " + what + ": " + Hex(got) + (ok ? "" : ", wanted " + Hex(want))); return ok; };

    // the 74 x 74 cell: the main line is 4 px centred on the outline (x 31..35 at the left edge), then a soft glow out to 6 px, nothing beyond
    var cell = RenderRect(G, FrameStyle.NoLimit, 0f, true, false, 0, true);
    Px(cell, 31, 70, G, "line, outer pixel"); Px(cell, 34, 70, G, "line, inner pixel"); Px(cell, 50, 31, G, "top line"); Px(cell, 70, 108, G, "bottom line");
    Px(cell, 38, 70, Cell, "inside the cell, off the line"); Px(cell, 20, 70, Bg, "far outside is untouched");
    Check(!Near(At(cell, 29, 70), Bg) && !Near(At(cell, 29, 70), G), "the glow / pulse next to the line is a soft tint, not the line (" + Hex(At(cell, 29, 70)) + ")");
    Px(cell, 26, 70, Bg, "beyond the 6 px glow");
    var still = RenderRect(G, FrameStyle.NoLimit, 0f, false, false, 0, false);
    Check(!Near(At(cell, 28, 70), At(still, 28, 70)), "the pulse line (4 px out) adds to the still frame at phase 0");
    var late = RenderRect(G, FrameStyle.NoLimit, 0.99f, true, false, 0, false);
    Check(Near(At(late, 27, 70), At(still, 27, 70)) && Near(At(late, 28, 70), At(still, 28, 70)), "the pulse has faded out by the end of its period");
    var drift = FrameStyle.Pulse(G, 0.5f, FrameStyle.NoLimit);
    Check(drift != null && drift.Offset > FrameStyle.PulseOffset && drift.Alpha < FrameStyle.PulseAlpha && drift.Alpha > 0 && Math.Abs(drift.Width - 2f) < 0.01f, "mid-period the 2 px pulse has drifted outward and half faded (offset " + (drift != null ? drift.Offset.ToString() : "-") + ")");
    var first = FrameStyle.Pulse(G, 0f, FrameStyle.NoLimit);
    Check(first != null && Math.Abs(first.Offset - 4f) < 0.01f && first.Alpha == 128 && Math.Abs(first.Width - 2f) < 0.01f, "the pulse starts 4 px out, 2 px wide, 50 % opaque");
    Check(FrameStyle.PeriodMs == 1400 && 1000 / FrameStyle.PulseMs <= 15, "the pulse period is 1.4 s and it repaints at most 15 times a second");
    // the owl: centred on the top edge; navy body, gold eyes
    var at = FrameStyle.OwlAt(33, 33, 74, 74, 0);
    Check(Math.Abs(at[0] - 70) < 0.01 && Math.Abs(at[1] - 33) < 0.01 && at[2] == 0, "the owl sits at the middle of the top edge");
    var eye = FrameStyle.OwlEyes(at[0], at[1])[0]; double ring = (eye[2] + eye[3]) / 2;
    Px(cell, (int)Math.Floor(eye[0] - ring), (int)Math.Floor(eye[1]), G, "owl eye is gold");
    var eyes = FrameStyle.OwlEyes(at[0], at[1]);
    Px(cell, (int)Math.Floor(at[0]), (int)Math.Floor(eyes[0][1]), FrameStyle.Navy, "owl body is dark navy between the eyes");
    var flip = FrameStyle.OwlAt(33, 33, 74, 74, 30);
    Check(flip[2] == 1 && Math.Abs(flip[1] - 107) < 0.01, "the owl flips to the bottom edge when the top would leave the client");
    Save("frame-cell-74.png", cell);
    Save("frame-cell-74-flipped-owl.png", RenderRect(G, FrameStyle.NoLimit, 0f, true, false, 30, true));

    // never over a neighbour: 3 px to the next cell -> glow and pulse are cut to the gap, the line stays on the edge
    float gap = FrameStyle.Gap(33, 33, 74, 74, new List<double[]> { new double[] { 110, 33, 30, 74 }, new double[] { 33, 33, 74, 74 } });
    Check(Math.Abs(gap - 3f) < 0.01f, "the gap to the neighbour is 3 px (itself is skipped): " + gap);
    var nb = RenderRect(G, gap, 0f, true, true, 0, false);
    Px(nb, 108, 70, G, "the line stays on the edge (right)"); Px(nb, 110, 70, Nb, "the neighbour's first pixel is untouched"); Px(nb, 112, 70, Nb, "the neighbour is untouched inside");
    for (int y = 40; y < 100; y += 7) if (!Near(At(nb, 110, y), Nb)) Check(false, "glow reaches the neighbour at y " + y);
    var tight = FrameStyle.Still(G, 2);
    Check(tight.Count == 1 && tight[0].Width == FrameStyle.MainWidth && FrameStyle.Pulse(G, 0f, 2) == null, "a 2 px gap (the line alone): only the main line, no glow and no pulse");
    Save("frame-cell-74-neighbour.png", nb);

    // the 60 px round button: the circle's own outline
    var circ = RenderCircle(G, 0f);
    Px(circ, 100, 70, G, "circle line (right, r 30)"); Px(circ, 70, 39, G, "circle line (top, inside edge)"); Px(circ, 90, 70, Cell, "inside the circle"); Px(circ, 120, 70, Bg, "far outside the circle");
    Save("frame-circle-60.png", circ);

    // red: the same style, the wrong-item colour
    var red = RenderRect(FrameStyle.Red, FrameStyle.NoLimit, 0f, true, false, 0, false);
    Px(red, 31, 70, FrameStyle.Red, "red line"); Px(red, 70, 108, FrameStyle.Red, "red line (bottom)"); Px(red, 38, 70, Cell, "red frame, inside untouched");
    Save("frame-cell-74-red.png", red);
  }
}
