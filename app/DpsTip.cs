// Wardsage app - the DPS meter's skill tooltip picture: the game's description plate (common_description_tips, 9-sliced), the skill's name on
// top, an optional grey note, the wrapped description; Fira Sans, outlined like the panel's text. A pure drawing function (the window is app/DpsMeter.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RealmForge {
  static class DpsTip {
    static readonly Color TitleGold = Color.FromArgb(255, 255, 227, 160), BodyCol = Color.FromArgb(255, 236, 238, 244), NoteCol = Color.FromArgb(255, 184, 192, 208);
    public const int MaxBodyLines = 14;

    /// <summary>The widest tooltip for a game window of unit U (px): 0.28 U.</summary>
    public static int MaxWidth(double unit) { return (int)Math.Round(0.28 * unit); }

    /// <summary>Breaks a text into lines of at most maxW px (paragraphs at \n, words at spaces; a word wider than a line is cut). At most maxLines lines: the last one ends with "…".</summary>
    public static List<string> Wrap(string text, float px, bool bold, float maxW, int maxLines) {
      var lines = new List<string>();
      if (string.IsNullOrEmpty(text)) return lines;
      bool cut = false;
      foreach (var para in text.Split('\n')) {
        if (cut) break;
        string cur = "";
        foreach (var word0 in para.Split(' ')) {
          string word = word0;
          if (word.Length == 0) continue;
          while (DpsArt.Measure(word, px, bold) > maxW && word.Length > 1) {   // a word longer than a line
            int n = word.Length;
            while (n > 1 && DpsArt.Measure(word.Substring(0, n), px, bold) > maxW) n--;
            if (cur.Length > 0) { lines.Add(cur); cur = ""; }
            lines.Add(word.Substring(0, n)); word = word.Substring(n);
          }
          string t = cur.Length == 0 ? word : cur + " " + word;
          if (cur.Length > 0 && DpsArt.Measure(t, px, bold) > maxW) { lines.Add(cur); cur = word; } else cur = t;
        }
        lines.Add(cur);
      }
      if (lines.Count > maxLines) { lines.RemoveRange(maxLines, lines.Count - maxLines); cut = true; }
      if (cut) {
        string last = lines[lines.Count - 1].TrimEnd(' ', '.', ',', ';');
        while (last.Length > 1 && DpsArt.Measure(last + "…", px, bold) > maxW) last = last.Substring(0, last.Length - 1).TrimEnd();
        lines[lines.Count - 1] = last + "…";
      }
      return lines;
    }

    /// <summary>Where the tooltip goes: right and below the cursor; flipped to the left / above when it would leave the screen; always inside it.</summary>
    public static Point Place(Point cursor, int w, int h, Rectangle screen, int gap) {
      int x = cursor.X + gap, y = cursor.Y + gap;
      if (x + w > screen.Right) x = cursor.X - gap - w;
      if (y + h > screen.Bottom) y = cursor.Y - gap - h;
      x = Math.Max(screen.Left, Math.Min(screen.Right - w, x)); y = Math.Max(screen.Top, Math.Min(screen.Bottom - h, y));
      return new Point(x, y);
    }

    /// <summary>The tooltip picture (32-bit ARGB). s = the panel's scale (max(0.7, U/1000)), maxW = the widest it may be (px).</summary>
    public static Bitmap Render(DpsDescInfo info, float s, int maxW) {
      s = Math.Max(s, 0.9f);
      DpsArt.Begin(s);
      float pad = 9 * s, inner = Math.Max(60, maxW - 2 * pad);
      float tpx = Math.Max(13, 14 * s), bpx = Math.Max(12, 12 * s), npx = Math.Max(12, 11 * s);
      var title = Wrap(info.Title, tpx, true, inner, 2);
      var note = string.IsNullOrEmpty(info.Note) ? new List<string>() : Wrap(info.Note, npx, false, inner, 2);
      var body = string.IsNullOrEmpty(info.Body) ? new List<string>() : Wrap(info.Body, bpx, false, inner, MaxBodyLines);
      float widest = 0;
      foreach (var l in title) widest = Math.Max(widest, DpsArt.Measure(l, tpx, true));
      foreach (var l in note) widest = Math.Max(widest, DpsArt.Measure(l, npx, false));
      foreach (var l in body) widest = Math.Max(widest, DpsArt.Measure(l, bpx, false));
      int w = (int)Math.Ceiling(Math.Max(110 * s, widest + 2 * pad)) + 2;
      float lhT = (float)Math.Ceiling(tpx * 1.3), lhN = (float)Math.Ceiling(npx * 1.3), lhB = (float)Math.Ceiling(bpx * 1.3);
      float hh = pad * 2 + title.Count * lhT + note.Count * lhN + body.Count * lhB + (body.Count > 0 ? 4 * s : 0) + (note.Count > 0 ? 1 * s : 0);
      int h = (int)Math.Ceiling(hh);
      var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
      using (var g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half; g.InterpolationMode = InterpolationMode.Bilinear;
        var plate = DpsArt.Nine("common_description_tips", w, h, 0.5f * s, 0.97f);
        if (plate != null) g.DrawImageUnscaled(plate, 0, 0); else using (var b = new SolidBrush(Color.FromArgb(240, 30, 32, 42))) g.FillRectangle(b, 0, 0, w, h);
        float y = pad, x = pad;
        foreach (var l in title) { DpsArt.Text(g, l, tpx, true, TitleGold, new RectangleF(x, y, inner, lhT), 0); y += lhT; }
        foreach (var l in note) { DpsArt.Text(g, l, npx, false, NoteCol, new RectangleF(x, y, inner, lhN), 0); y += lhN; }
        if (note.Count > 0) y += 1 * s;
        if (body.Count > 0) y += 4 * s;
        foreach (var l in body) { DpsArt.Text(g, l, bpx, false, BodyCol, new RectangleF(x, y, inner, lhB), 0); y += lhB; }
      }
      return bmp;
    }
  }
}
