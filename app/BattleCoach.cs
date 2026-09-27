// RealmForge — the boss coach over the game: during a boss fight a panel at the top of the game shows the fight's
// clock and what comes next, second by second: the boss's skills from its AI (res/boss_coach.json, built by the site's
// scripts/export-coach.mjs from the AI replayed frame by frame), and what to do (hold the ultimates before the shield,
// use them on it). Click-through; shown only with the game in front. Reads the fight's clock only (src/BattleClock.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace RealmForge {
  sealed class BattleCoach : IDisposable {
    sealed class Cast { public double T; public string Kind, Ru, En, PreRu, PreEn; public double Warn; }
    sealed class Stage { public string Boss, NameRu, NameEn; public double Window; public List<Cast> Casts = new List<Cast>(); }

    readonly Timer timer = new Timer { Interval = 100 };
    readonly GlowWindow win = new GlowWindow();
    readonly Dictionary<int, Stage> stages = new Dictionary<int, Stage>();
    Stage cur; ulong sim; string drawn; Bitmap bmp;
    /// <summary>Interface language (ru / en).</summary>
    public string Lang = "ru";

    public BattleCoach() {
      timer.Tick += (s, e) => { try { Tick(); } catch (Exception ex) { Log.Write("coach: " + ex.Message); Stop(); } };
      try {
        using (var s = typeof(BattleCoach).Assembly.GetManifestResourceStream("overlay/boss_coach.json"))
        using (var r = new StreamReader(s)) {
          var d = MiniJson.Parse(r.ReadToEnd()) as Dictionary<string, object>;
          if (d != null) foreach (var kv in d) {
            int id; var o = kv.Value as Dictionary<string, object>;
            if (!int.TryParse(kv.Key, out id) || o == null) continue;
            var st = new Stage { Boss = MiniJson.GetString(o, "boss") };
            var n = o.ContainsKey("name") ? o["name"] as Dictionary<string, object> : null;
            st.NameRu = n != null ? MiniJson.GetString(n, "ru") : st.Boss; st.NameEn = n != null ? MiniJson.GetString(n, "en") : st.Boss;
            st.Window = o["window"] is double ? (double)o["window"] : 21;
            var casts = o["casts"] as List<object>;
            if (casts != null) foreach (var c in casts) {
              var a = c as List<object>; if (a == null || a.Count < 5) continue;
              st.Casts.Add(new Cast { T = (double)a[0], Kind = a[1] as string, Ru = a[2] as string, En = a[3] as string, Warn = (double)a[4],
                                      PreRu = a.Count > 5 ? a[5] as string : null, PreEn = a.Count > 6 ? a[6] as string : null });
            }
            stages[id] = st;
          }
        }
      } catch (Exception e) { Log.Write("coach data: " + e.Message); }
    }

    public bool Knows(int stage) { return stages.ContainsKey(stage); }
    public bool Running { get { return cur != null; } }

    /// <summary>Starts following the fight's clock (the simulation found at the fight's start).</summary>
    public void Start(ulong simulation, int stage) {
      Stage st; if (!stages.TryGetValue(stage, out st)) return;
      cur = st; sim = simulation; drawn = null;
      Log.Write("coach: " + st.Boss + " (stage " + stage + ")");
      timer.Start();
    }

    public void Stop() { timer.Stop(); cur = null; sim = 0; if (win.Visible) win.Hide(); }

    public void Dispose() { timer.Dispose(); win.Dispose(); if (bmp != null) bmp.Dispose(); }

    void Tick() {
      uint frames; int state;
      if (cur == null || !RFX.SimClock(sim, out frames, out state) || state != 1) { Stop(); return; }
      var ps = System.Diagnostics.Process.GetProcessesByName("Watcher of Realms");
      IntPtr hwnd = ps.Length > 0 ? ps[0].MainWindowHandle : IntPtr.Zero;
      int fp = 0, gp = 0;
      if (hwnd != IntPtr.Zero) { W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp); W32.GetWindowThreadProcessId(hwnd, out gp); }
      W32.RECT cr;
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || fp != gp || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { if (win.Visible) win.Hide(); return; }
      double t = RFX.FrameSeconds(frames);
      string title, main, next; Lines(t, out title, out main, out next, out bool alarm);
      double U = Ui.Unit(cr.R, cr.B);
      int w = (int)(0.62 * U), h = (int)(0.118 * U);
      string key = title + "|" + main + "|" + next + "|" + alarm + "|" + w;
      if (key != drawn || bmp == null) { if (bmp != null) bmp.Dispose(); bmp = Render(w, h, title, main, next, alarm); drawn = key; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      win.Put(bmp, o.X + cr.R / 2 - w / 2, o.Y + (int)(0.08 * U));
    }

    string Say(Cast c) { return Lang == "en" ? c.En : c.Ru; }

    void Lines(double t, out string title, out string main, out string next, out bool alarm) {
      int m = (int)(t / 60), s = (int)(t % 60);
      title = (Lang == "en" ? cur.NameEn : cur.NameRu) + "   " + m + ":" + s.ToString("00");
      main = ""; next = ""; alarm = false;
      Cast active = null; foreach (var c in cur.Casts) {
        double len = c.Kind == "shield" ? cur.Window : 3.5;
        if (c.T <= t && t < c.T + len) active = c;
      }
      Cast up = null, up2 = null;
      foreach (var c in cur.Casts) if (c.T > t) { if (up == null) up = c; else { up2 = c; break; } }
      if (active != null) {
        alarm = active.Kind == "shield";
        main = active.Kind == "shield" ? Say(active) + " · " + (Lang == "en" ? "left " : "осталось ") + (int)Math.Ceiling(active.T + cur.Window - t) + (Lang == "en" ? " s" : " с") : Say(active);
      } else if (up != null) {
        int inS = (int)Math.Ceiling(up.T - t);
        string pre = Lang == "en" ? up.PreEn : up.PreRu;
        if (pre != null && up.T - t <= up.Warn) main = pre.Replace("{0}", inS.ToString());
        else main = (Lang == "en" ? "In " + inS + " s: " : "Через " + inS + " с: ") + Say(up);
      }
      Cast after = active != null ? up : up2;
      if (after != null) next = (Lang == "en" ? "then in " : "потом через ") + (int)Math.Ceiling(after.T - t) + (Lang == "en" ? " s: " : " с: ") + Say(after);
    }

    // the panel: a dark plate with the game's gold edges; the alarm (shield) glows red
    static Bitmap Render(int w, int h, string title, string main, string next, bool alarm) {
      var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
      using (var g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var r = new Rectangle(2, 2, w - 5, h - 5);
        using (var path = Round(r, h / 6))
        using (var fill = new LinearGradientBrush(r, Color.FromArgb(235, 14, 18, 28), Color.FromArgb(235, 26, 20, 12), LinearGradientMode.Vertical)) {
          g.FillPath(fill, path);
          if (alarm) using (var red = new SolidBrush(Color.FromArgb(70, 200, 40, 30))) g.FillPath(red, path);
          using (var edge = new Pen(Color.FromArgb(230, 212, 170, 90), 2f)) g.DrawPath(edge, path);
          using (var inner = new Pen(Color.FromArgb(90, 255, 230, 170), 1f)) { var r2 = Rectangle.Inflate(r, -4, -4); using (var p2 = Round(r2, h / 7)) g.DrawPath(inner, p2); }
        }
        float fs = h * 0.19f;
        using (var ft = new Font("Segoe UI", fs * 0.85f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var fm = new Font("Segoe UI", fs * 1.08f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var fn = new Font("Segoe UI", fs * 0.8f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var gold = new SolidBrush(Color.FromArgb(255, 240, 200, 110)))
        using (var white = new SolidBrush(alarm ? Color.FromArgb(255, 255, 214, 200) : Color.White))
        using (var grey = new SolidBrush(Color.FromArgb(255, 176, 184, 200)))
        using (var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0))) {
          Action<string, Font, Brush, float, float> line = (s, f, b, y0, hh) => {
            var rr = new RectangleF(10, y0, w - 20, hh);
            g.DrawString(s, f, shadow, new RectangleF(rr.X + 1, rr.Y + 1.5f, rr.Width, rr.Height), sf);
            g.DrawString(s, f, b, rr, sf);
          };
          line(title, ft, gold, h * 0.06f, h * 0.28f);
          line(main, fm, white, h * 0.32f, h * 0.36f);
          line(next, fn, grey, h * 0.66f, h * 0.28f);
        }
      }
      return bmp;
    }

    static GraphicsPath Round(Rectangle r, int rad) {
      var p = new GraphicsPath(); int d = rad * 2;
      p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
      p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
      p.CloseFigure(); return p;
    }
  }
}
