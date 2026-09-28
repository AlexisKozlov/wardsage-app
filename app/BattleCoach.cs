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
    public bool Running { get { return sim != 0; } }

    // the player's plan for this fight (the site's «Отправить план в игру», src/FightPlanClient.cs) and what the fight
    // recorder last saw of the heroes (src/BattleTimeline.cs; set from its thread)
    FightPlan plan;
    volatile List<HeroSample> seen;
    readonly GlowWindow mapWin = new GlowWindow();
    Bitmap mapBmp; string mapDrawn;
    // the tile of the hero that fell last (a reserve goes there)
    int fallX = -1, fallY = -1;
    readonly HashSet<long> wasUp = new HashSet<long>();

    /// <summary>Starts following the fight's clock (the simulation found at the fight's start): the boss's schedule
    /// when the coach knows the stage, the player's plan when there is one.</summary>
    public void Start(ulong simulation, int stage, FightPlan p) {
      Stage st; stages.TryGetValue(stage, out st);
      if (st == null && p == null) return;
      cur = st; plan = p; sim = simulation; drawn = null; mapDrawn = null; seen = null; fallX = fallY = -1; wasUp.Clear();
      Log.Write("coach: " + (st != null ? st.Boss : p.Boss) + " (stage " + stage + ")" + (p != null ? ", plan of " + p.Steps.Count + " heroes" : ""));
      timer.Start();
    }

    /// <summary>The heroes of the fight as the recorder saw them (any thread).</summary>
    public void Feed(List<HeroSample> hs) { if (sim != 0) seen = hs; }

    public void Stop() { timer.Stop(); cur = null; plan = null; sim = 0; seen = null; if (win.Visible) win.Hide(); if (mapWin.Visible) mapWin.Hide(); }

    public void Dispose() { timer.Dispose(); win.Dispose(); mapWin.Dispose(); if (bmp != null) bmp.Dispose(); if (mapBmp != null) mapBmp.Dispose(); }

    void Tick() {
      uint frames; int state;
      if ((cur == null && plan == null) || !RFX.SimClock(sim, out frames, out state) || state != 1) { Stop(); return; }
      var ps = System.Diagnostics.Process.GetProcessesByName("Watcher of Realms");
      IntPtr hwnd = ps.Length > 0 ? ps[0].MainWindowHandle : IntPtr.Zero;
      int fp = 0, gp = 0;
      if (hwnd != IntPtr.Zero) { W32.GetWindowThreadProcessId(W32.GetForegroundWindow(), out fp); W32.GetWindowThreadProcessId(hwnd, out gp); }
      W32.RECT cr;
      if (hwnd == IntPtr.Zero || W32.IsIconic(hwnd) || fp != gp || !W32.GetClientRect(hwnd, out cr) || cr.B < 200) { if (win.Visible) win.Hide(); if (mapWin.Visible) mapWin.Hide(); return; }
      double t = RFX.FrameSeconds(frames);
      string title, main, next; Lines(t, out title, out main, out next, out bool alarm);
      double U = Ui.Unit(cr.R, cr.B);
      int w = (int)(0.62 * U), h = (int)(0.118 * U);
      string key = title + "|" + main + "|" + next + "|" + alarm + "|" + w;
      if (key != drawn || bmp == null) { if (bmp != null) bmp.Dispose(); bmp = Render(w, h, title, main, next, alarm); drawn = key; }
      var o = new W32.POINT(0, 0); W32.ClientToScreen(hwnd, ref o);
      win.Put(bmp, o.X + cr.R / 2 - w / 2, o.Y + (int)(0.08 * U));
      if (plan != null) PlanTick(U, o, cr);
    }

    string Say(Cast c) { return Lang == "en" ? c.En : c.Ru; }
    string Name(FightStep s) { return Lang == "en" ? s.NameEn : s.NameRu; }

    void Lines(double t, out string title, out string main, out string next, out bool alarm) {
      int m = (int)(t / 60), s = (int)(t % 60);
      title = (cur != null ? (Lang == "en" ? cur.NameEn : cur.NameRu) : (Lang == "en" ? plan.NameEn : plan.NameRu)) + "   " + m + ":" + s.ToString("00");
      main = ""; next = ""; alarm = false;
      if (cur == null) return;
      // the plan's ultimate advice: hold them for the shield (the simulation found it pays), press them in it
      if (plan != null && plan.Hold > 0 && plan.HoldU.Count > 0) {
        var names = new List<string>();
        foreach (var st in plan.Steps) if (plan.HoldU.Contains(st.Uid)) names.Add(Name(st));
        string who = string.Join(", ", names.ToArray());
        foreach (var c in cur.Casts) {
          if (c.Kind != "shield") continue;
          if (c.T <= t && t < c.T + cur.Window) {
            alarm = true;
            main = (Lang == "en" ? "SHIELD: press the ultimates: " : "ЩИТ: жмите суперспособности: ") + who;
            next = (Lang == "en" ? "left " : "осталось ") + (int)Math.Ceiling(c.T + cur.Window - t) + (Lang == "en" ? " s" : " с");
            return;
          }
          if (c.T > t && c.T - t <= plan.Hold) {
            main = (Lang == "en" ? "Hold the ultimates: " : "Держите суперспособности: ") + who;
            next = (Lang == "en" ? "the shield in " : "щит через ") + (int)Math.Ceiling(c.T - t) + (Lang == "en" ? " s" : " с");
            return;
          }
        }
      }
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
        if (pre != null && up.T - t <= up.Warn && !(plan != null && plan.Hold > 0 && up.Kind == "shield")) main = pre.Replace("{0}", inS.ToString());
        else main = (Lang == "en" ? "In " + inS + " s: " : "Через " + inS + " с: ") + Say(up);
      }
      Cast after = active != null ? up : up2;
      if (after != null) next = (Lang == "en" ? "then in " : "потом через ") + (int)Math.Ceiling(after.T - t) + (Lang == "en" ? " s: " : " с: ") + Say(after);
    }

    // ------------------------------------------------------------------ the plan: next hero, its tile, the field map

    void PlanTick(double U, W32.POINT o, W32.RECT cr) {
      var hs = seen;
      // the tile a hero of the field fell on (its reserve goes there); remember who stood
      if (hs != null) foreach (var sm in hs) {
        bool up = sm.OnField && !sm.Dead;
        if (up) { wasUp.Add(sm.Uid); continue; }
        if (wasUp.Remove(sm.Uid)) foreach (var s in plan.Steps) if (s.Uid == sm.Uid && s.X >= 0) { fallX = s.X; fallY = s.Y; }
      }
      FightStep next, wrong; int wx, wy;
      FightPlanClient.State(plan, hs, out next, out wrong, out wx, out wy);
      int placed = 0, field = 0;
      var upNow = new HashSet<long>();
      if (hs != null) foreach (var sm in hs) if (sm.OnField && !sm.Dead) upNow.Add(sm.Uid);
      foreach (var s in plan.Steps) if (!s.Bench) { field++; if (upNow.Contains(s.Uid)) placed++; }
      // everything placed, no reserve due, nobody misplaced: the map steps aside
      if (next == null && wrong == null && hs != null) { if (mapWin.Visible) mapWin.Hide(); return; }

      int nx = next != null ? (next.Bench ? fallX : next.X) : -1, ny = next != null ? (next.Bench ? fallY : next.Y) : -1;
      int ndir = next != null && !next.Bench ? next.Dir : -1;
      string head, line, warn = null;
      if (next == null) { head = Lang == "en" ? "All heroes are on the field" : "Все герои на поле"; line = ""; }
      else if (next.Bench) {
        head = (Lang == "en" ? "From the reserve: " : "Из запаса: ") + Name(next);
        line = nx >= 0 ? (Lang == "en" ? "place on the fallen hero’s tile (marked)" : "на клетку павшего героя (отмечена)") : (Lang == "en" ? "place when a hero falls" : "выставьте, когда кто-то падёт");
      } else {
        int n = plan.Steps.IndexOf(next) + 1;
        head = (Lang == "en" ? "Step " + n + " of " + field + ": " : "Шаг " + n + " из " + field + ": ") + Name(next);
        line = nx >= 0 ? (Lang == "en" ? "the marked tile, facing " : "на отмеченную клетку, лицом ") + DirWord(ndir) : "";
      }
      if (wrong != null) warn = Name(wrong) + (Lang == "en" ? " is not on its tile" : " стоит не на своей клетке");
      int w = (int)(0.3 * U);
      int cs = plan.Cells != null ? Math.Max(6, Math.Min((w - 24) / plan.W, (int)(0.34 * U) / plan.H)) : 0;
      int mapH = plan.Cells != null ? cs * plan.H + 10 : 0;
      float fsz = Math.Max(10f, w * 0.052f);   // RenderMap's text size: two lines (+ the warning), then the map
      int h = (int)(14 + fsz * (warn != null ? 4.05f : 2.75f)) + mapH;
      // the next hero's marker pulses: redraw a few times a second
      int pulse = (int)(Environment.TickCount / 250) % 4;
      string key = head + "|" + line + "|" + warn + "|" + nx + "," + ny + "," + ndir + "|" + placed + "|" + w + "|" + pulse + "|" + (hs == null) + "|" + Dones(hs);
      if (key != mapDrawn || mapBmp == null) {
        if (mapBmp != null) mapBmp.Dispose();
        mapBmp = RenderMap(plan, w, h, cs, head, line, warn, hs, next, nx, ny, ndir, wrong, wx, wy, pulse, Lang);
        mapDrawn = key;
      }
      mapWin.Put(mapBmp, o.X + (int)(0.012 * U), o.Y + (int)(0.2 * U));
    }

    static string Dones(IList<HeroSample> hs) {
      if (hs == null) return "";
      var sb = new System.Text.StringBuilder();
      foreach (var h in hs) if (h.OnField && !h.Dead) sb.Append(h.Uid).Append(':').Append(h.X).Append(',').Append(h.Y).Append(';');
      return sb.ToString();
    }

    string DirWord(int d) {
      if (Lang == "en") return d == 0 ? "up" : d == 1 ? "right" : d == 2 ? "down" : d == 3 ? "left" : "";
      return d == 0 ? "вверх" : d == 1 ? "вправо" : d == 2 ? "вниз" : d == 3 ? "влево" : "";
    }

    // the plan's panel: the same dark plate with gold edges; the field from the game's map (road, high ground, the tiles
    // heroes may stand on, the boss's body), placed heroes green, the next one's tile glowing gold with its facing arrow,
    // a hero off its tile red
    internal static Bitmap RenderMap(FightPlan p, int w, int h, int cs, string head, string line, string warn, IList<HeroSample> hs, FightStep next,
                            int nx, int ny, int ndir, FightStep wrong, int wx, int wy, int pulse, string lang) {
      var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
      using (var g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var r = new Rectangle(2, 2, w - 5, h - 5);
        int rad = Math.Max(6, w / 28);
        using (var path = Round(r, rad))
        using (var fill = new LinearGradientBrush(r, Color.FromArgb(235, 14, 18, 28), Color.FromArgb(235, 26, 20, 12), LinearGradientMode.Vertical)) {
          g.FillPath(fill, path);
          using (var edge = new Pen(Color.FromArgb(230, 212, 170, 90), 2f)) g.DrawPath(edge, path);
          using (var inner = new Pen(Color.FromArgb(90, 255, 230, 170), 1f)) { var r2 = Rectangle.Inflate(r, -4, -4); using (var p2 = Round(r2, Math.Max(4, rad - 2))) g.DrawPath(inner, p2); }
        }
        float fs = Math.Max(10f, w * 0.052f);
        using (var fh = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var fl = new Font("Segoe UI", fs * 0.85f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var gold = new SolidBrush(Color.FromArgb(255, 240, 200, 110)))
        using (var grey = new SolidBrush(Color.FromArgb(255, 200, 206, 218)))
        using (var red = new SolidBrush(Color.FromArgb(255, 255, 120, 100)))
        using (var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0))) {
          Action<string, Font, Brush, float, float> text = (s, f, b, y0, hh) => {
            if (string.IsNullOrEmpty(s)) return;
            var rr = new RectangleF(8, y0, w - 16, hh);
            g.DrawString(s, f, shadow, new RectangleF(rr.X + 1, rr.Y + 1.5f, rr.Width, rr.Height), sf);
            g.DrawString(s, f, b, rr, sf);
          };
          float y = 6;
          text(head, fh, gold, y, fs * 1.5f); y += fs * 1.45f;
          text(line, fl, grey, y, fs * 1.3f); y += fs * 1.3f;
          if (warn != null) { text(warn, fl, red, y, fs * 1.3f); y += fs * 1.3f; }
          if (p.Cells == null) return bmp;
          int ox = (w - cs * p.W) / 2, oy = (int)y + 4;
          Func<int, int, Rectangle> cell = (x, yy) => new Rectangle(ox + x * cs, oy + (p.H - 1 - yy) * cs, cs, cs);
          using (var wall = new SolidBrush(Color.FromArgb(200, 22, 24, 30)))
          using (var road = new SolidBrush(Color.FromArgb(220, 104, 80, 52)))
          using (var high = new SolidBrush(Color.FromArgb(220, 62, 74, 96)))
          using (var place = new Pen(Color.FromArgb(120, 232, 207, 142), 1f))
          using (var grid = new Pen(Color.FromArgb(70, 0, 0, 0), 1f)) {
            for (int x = 0; x < p.W; x++) for (int yy = 0; yy < p.H; yy++) {
              int c = p.Cell(x, yy); var cr = cell(x, yy);
              g.FillRectangle((c & 4) != 0 ? high : (c & 2) != 0 ? road : wall, cr);
              g.DrawRectangle(grid, cr);
              if ((c & 32) != 0) g.DrawRectangle(place, Rectangle.Inflate(cr, -2, -2));
            }
          }
          // the boss's body
          var b0 = cell(Math.Max(0, p.BossX - p.HalfX), Math.Min(p.H - 1, p.BossY + p.HalfY));
          var b1 = cell(Math.Min(p.W - 1, p.BossX + p.HalfX), Math.Max(0, p.BossY - p.HalfY));
          var body = Rectangle.FromLTRB(b0.Left + 1, b0.Top + 1, b1.Right - 1, b1.Bottom - 1);
          using (var bf = new SolidBrush(Color.FromArgb(210, 120, 28, 24))) using (var bp = new Pen(Color.FromArgb(230, 212, 170, 90), 1.5f)) { g.FillRectangle(bf, body); g.DrawRectangle(bp, body); }
          using (var fb = new Font("Segoe UI", Math.Max(8f, cs * 0.42f), FontStyle.Bold, GraphicsUnit.Pixel)) {
            g.DrawString(lang == "en" ? "BOSS" : "БОСС", fb, gold, body, sf);
            // the heroes of the plan on their tiles: placed (green), the next (gold, pulsing, facing arrow), the rest (dim)
            var up = new Dictionary<long, HeroSample>();
            if (hs != null) foreach (var hh in hs) if (hh.OnField && !hh.Dead) up[hh.Uid] = hh;
            int n = 0;
            foreach (var s in p.Steps) {
              if (s.Bench) continue; n++;
              if (s.X < 0) continue;
              var cr = Rectangle.Inflate(cell(s.X, s.Y), -Math.Max(1, cs / 10), -Math.Max(1, cs / 10));
              bool done = up.ContainsKey(s.Uid), isNext = next == s;
              Color fillC = done ? Color.FromArgb(235, 52, 140, 72) : isNext ? Color.FromArgb(245, 232, 182, 76) : Color.FromArgb(200, 30, 34, 44);
              using (var hb = new SolidBrush(fillC)) g.FillEllipse(hb, cr);
              using (var hp = new Pen(isNext ? Color.FromArgb(255, 255, 240, 190) : Color.FromArgb(200, 212, 170, 90), isNext ? 2f : 1f)) g.DrawEllipse(hp, cr);
              using (var nb = new SolidBrush(isNext ? Color.FromArgb(255, 40, 26, 8) : Color.White)) g.DrawString(n.ToString(), fb, nb, cr, sf);
            }
            // the next tile: a pulsing ring and the facing arrow (dir 0 up, 1 right, 2 down, 3 left)
            if (nx >= 0 && ny >= 0) {
              var cr = cell(nx, ny);
              int grow = 1 + pulse;
              using (var ring = new Pen(Color.FromArgb(200 - pulse * 35, 255, 220, 120), 2f)) g.DrawEllipse(ring, Rectangle.Inflate(cr, grow, grow));
              if (ndir >= 0) {
                float cx = cr.X + cr.Width / 2f, cy = cr.Y + cr.Height / 2f, L = cs * 0.95f, s = cs * 0.28f;
                float dx = ndir == 1 ? 1 : ndir == 3 ? -1 : 0, dy = ndir == 0 ? -1 : ndir == 2 ? 1 : 0;   // screen y grows down
                var tip = new PointF(cx + dx * L, cy + dy * L);
                var bl = new PointF(cx + dx * (L - s) - dy * s, cy + dy * (L - s) + dx * s);
                var br = new PointF(cx + dx * (L - s) + dy * s, cy + dy * (L - s) - dx * s);
                using (var ab = new SolidBrush(Color.FromArgb(250, 255, 222, 130))) using (var ap = new Pen(Color.FromArgb(220, 60, 36, 10), 1f)) {
                  g.FillPolygon(ab, new[] { tip, bl, br }); g.DrawPolygon(ap, new[] { tip, bl, br });
                }
              }
            }
            // a hero off its tile: a red cross where it stands
            if (wrong != null && wx >= 0 && wy >= 0 && wx < p.W && wy < p.H) {
              var cr = Rectangle.Inflate(cell(wx, wy), -cs / 5, -cs / 5);
              using (var xp = new Pen(Color.FromArgb(255, 235, 70, 55), Math.Max(2f, cs / 8f))) { g.DrawLine(xp, cr.Left, cr.Top, cr.Right, cr.Bottom); g.DrawLine(xp, cr.Left, cr.Bottom, cr.Right, cr.Top); }
            }
          }
        }
      }
      return bmp;
    }

    // the panel: a dark plate with the game's gold edges; the alarm (shield) glows red
    internal static Bitmap Render(int w, int h, string title, string main, string next, bool alarm) {
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
