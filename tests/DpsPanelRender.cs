// Draws the DPS meter's panel (app/DpsPanel.cs) over a game picture (tests/fixtures/dps-bg.jpg), from the saved fights (tests/fixtures/dps-battles.json), into PNG files,
// and checks it: game windows 1024x576 ... 3840x2160, Russian and English, the four modes, the three segments, a hero's skills open (boss fight and arena), no fight yet,
// Ctrl. Checks: not blank; no text wider than its rectangle; the text/background contrast under the glyphs >= 4.5; a portrait for every hero
// and an icon for the skills (counters); "Other" <= 5% of the damage on the fixture's skills; the time of Render on 300x500.
// Needs GDI+ (Windows, or Mono with libgdiplus). Compiled with -define:TESTBUILD.
//   DpsPanelRender.exe <out dir>      exit 0 = all checks passed
#if TESTBUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using RealmForge;

static class DpsPanelRender {
  static double Num(Dictionary<string, object> d, string k) { object o; return d.TryGetValue(k, out o) && o is double ? (double)o : 0; }

  static DpsSnapshot Replay(Dictionary<string, object> fx, bool finish) {
    int stage = (int)Num(fx, "stage"); uint frames = (uint)Num(fx, "frames");
    var stats = (List<object>)fx["stats"]; var dmgT = (Dictionary<string, object>)fx["dmgT"];
    var fset = new SortedDictionary<long, bool>();
    foreach (var kv in dmgT) foreach (var p in (List<object>)kv.Value) fset[(long)(double)((List<object>)p)[0]] = true;
    var feed = new DpsFeed();
    long stop = finish ? long.MaxValue : (long)(frames * 0.6);
    foreach (var fr in fset.Keys) {
      if (fr >= frames || fr > stop) continue;
      var lines = new List<StatLine>();
      foreach (var kv in dmgT) {
        var pts = (List<object>)kv.Value; List<object> pt = null;
        foreach (var po in pts) { var q = (List<object>)po; if ((long)(double)q[0] <= fr) pt = q; else break; }
        if (pt == null) continue;
        string[] cu = kv.Key.Split(':');
        lines.Add(new StatLine { C = uint.Parse(cu[0]), Unit = int.Parse(cu[1]), Damage = (long)(double)pt[1], Heal = (int)(double)pt[2], Taken = (int)(double)pt[3] });
      }
      feed.Push(new DpsTick { Stage = stage, Frame = (uint)fr, Stats = lines });
    }
    if (finish) {
      var fin = new List<StatLine>(); var sk = new Dictionary<string, Dictionary<int, long>>();
      foreach (var so in stats) {
        var s = (Dictionary<string, object>)so;
        var l = new StatLine { C = (uint)Num(s, "c"), Unit = (int)Num(s, "unit"), Hero = (uint)Num(s, "hero"), Damage = (long)Num(s, "damage"), ToBoss = (long)Num(s, "toBoss"),
          Heal = (int)Num(s, "heal"), Taken = (int)Num(s, "taken"), Overflow = (long)Num(s, "overflow") };
        fin.Add(l);
        object so2;
        if (s.TryGetValue("skills", out so2)) { var d = new Dictionary<int, long>(); foreach (var kv in (Dictionary<string, object>)so2) d[int.Parse(kv.Key)] = (long)(double)kv.Value; sk[l.C + ":" + l.Unit] = d; }
      }
      feed.Push(new DpsTick { Stage = stage, Frame = frames, Final = true, Stats = fin, Skills = sk.Count > 0 ? sk : null });
    }
    return feed.Snapshot;
  }

  // ---- checks: failures are printed, the passes are counted per category
  static int fails; static readonly int FailLimit = Environment.GetEnvironmentVariable("DPS_FAILS") == "all" ? int.MaxValue : 60;
  static readonly Dictionary<string, int> passes = new Dictionary<string, int>();
  static void Check(string cat, bool ok, string what) {
    if (ok) { int n; passes.TryGetValue(cat, out n); passes[cat] = n + 1; }
    else { fails++; if (fails <= FailLimit) Console.WriteLine("FAIL [" + cat + "]: " + what); }
  }

  // ---- the backdrop
  static Bitmap bgSrc; static readonly Dictionary<string, Bitmap> bgScaled = new Dictionary<string, Bitmap>();
  static Bitmap Bg(int gw, int gh) {
    string key = gw + "x" + gh; Bitmap b;
    if (bgScaled.TryGetValue(key, out b)) return b;
    if (bgSrc == null && File.Exists("tests/fixtures/dps-bg.jpg")) bgSrc = new Bitmap("tests/fixtures/dps-bg.jpg");
    b = new Bitmap(gw, gh, PixelFormat.Format24bppRgb);
    using (var g = Graphics.FromImage(b)) {
      if (bgSrc != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(bgSrc, 0, 0, gw, gh); }
      else using (var br = new LinearGradientBrush(new Rectangle(0, 0, gw, gh), Color.FromArgb(52, 86, 64), Color.FromArgb(120, 104, 70), 60f)) g.FillRectangle(br, 0, 0, gw, gh);
    }
    if (bgScaled.Count > 2) { foreach (var o in bgScaled.Values) o.Dispose(); bgScaled.Clear(); }
    bgScaled[key] = b; return b;
  }

  static double Lum(Color c) {
    Func<int, double> f = v => { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); };
    return 0.2126 * f(c.R) + 0.7152 * f(c.G) + 0.0722 * f(c.B);
  }
  static double Contrast(Color a, Color b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

  static Bitmap PanelScene(Bitmap panel, int gw, int gh, int px, int py) {
    var o = new Bitmap(panel.Width, panel.Height, PixelFormat.Format24bppRgb);
    using (var g = Graphics.FromImage(o)) { g.DrawImage(Bg(gw, gh), new Rectangle(0, 0, panel.Width, panel.Height), px, py, panel.Width, panel.Height, GraphicsUnit.Pixel); g.DrawImage(panel, 0, 0); }
    return o;
  }


  /// <summary>Renders one view at one game size, runs the checks, saves the scene when asked (and returns the panel size).</summary>
  static void Shot(string dir, string name, DpsSnapshot snap, DpsView v, int gw, int gh, bool save, bool skillsView, bool needPortraits) {
    double U = Math.Min(gh, gw * 675.0 / 1200.0); float s = DpsPanel.ScaleFor(U);
    int w = DpsPanel.DefaultW(U), h = DpsPanel.DefaultH(U);
    int px = Math.Max(0, gw - w - (int)(0.012 * U)), py = (int)(0.22 * gh);
    var hits = new List<DpsHit>();
    DpsArt.PortraitReal = DpsArt.PortraitFallback = DpsArt.IconOwn = DpsArt.IconGeneric = DpsArt.IconNone = 0;
    var probe = new List<DpsArt.TextRec>(); DpsArt.Probe = probe;
    int pr = 0, pf = 0, io = 0, ig = 0, ino = 0;
    using (var panel = DpsPanel.Render(snap, v, w, h, s, hits)) {
      pr = DpsArt.PortraitReal; pf = DpsArt.PortraitFallback; io = DpsArt.IconOwn; ig = DpsArt.IconGeneric; ino = DpsArt.IconNone;
      DpsArt.Probe = null;
      // not blank
      int lit = 0;
      using (var sc = PanelScene(panel, gw, gh, px, py))
        for (int y = 10; y < h - 10; y += 3) for (int x = 10; x < w - 10; x += 3) { var c = sc.GetPixel(x, y); if (c.R > 190 && c.G > 190 && c.B > 190) lit++; }
      Check("not blank", lit > 10 && hits.Count >= 8, name + ": " + lit + " bright samples, " + hits.Count + " pressable places");
      // no text wider than its rectangle
      foreach (var r in probe) Check("text fits", r.Width <= r.Box.Width + 0.5f && r.Glyphs.Right <= r.Box.Right + 0.5f && r.Glyphs.X >= r.Box.X - 0.5f, name + ": \"" + r.Text + "\" " + r.Width.ToString("0.0") + " > " + r.Box.Width.ToString("0.0"));
      // contrast under the glyphs: the same panel without text, 8 points inside each text rectangle
      DpsArt.Suppress = true;
      try {
        using (var p2 = DpsPanel.Render(snap, v, w, h, s, null))
        using (var sc2 = PanelScene(p2, gw, gh, px, py)) {
          foreach (var r in probe) {
            double worst = 99;
            for (int i = 0; i < 8; i++) {
              int x = (int)(r.Glyphs.X + r.Glyphs.Width * ((i % 4) + 0.5) / 4.0), y = (int)(r.Glyphs.Y + r.Glyphs.Height * ((i / 4) * 0.5 + 0.25));
              if (x < 0 || y < 0 || x >= w || y >= h) continue;
              worst = Math.Min(worst, Contrast(r.Fill, sc2.GetPixel(x, y)));
            }
            Check("contrast >= 4.5", worst >= 4.5, name + ": \"" + r.Text + "\" contrast " + worst.ToString("0.0") + " at " + r.Glyphs + " panel " + w + "x" + h);
          }
        }
      } finally { DpsArt.Suppress = false; }
      if (save) using (var scene = new Bitmap(Bg(gw, gh))) {
        using (var g = Graphics.FromImage(scene)) g.DrawImage(panel, px, py);
        scene.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
      }
    }
    if (needPortraits) Check("portraits", pf == 0, name + ": portraits found " + pr + ", fallback " + pf);
    if (skillsView) Check("skill icons", io > 0 && ino == 0, name + ": own icons " + io + ", common " + ig + ", none " + ino);
    iconOwn += io; iconGen += ig; ptReal += pr;
  }
  static int iconOwn, iconGen, ptReal;

  static Dictionary<string, object> FightOf(List<object> fights, int stage) {
    foreach (var f in fights) { var d = (Dictionary<string, object>)f; if ((int)Num(d, "stage") == stage) return d; }
    return null;
  }

  static DpsView Open(DpsSnapshot snap, DpsView v) {
    DpsHero pick = null;
    foreach (var hh in snap.Current.Sorted(v.Mode)) { if (hh.Skills != null && hh.Skills.Count > 0) { pick = hh; break; } }
    if (pick == null) pick = snap.Current.Sorted(v.Mode)[0];
    v.HasOpen = true; v.OpenC = pick.C; v.OpenUnit = pick.Unit; return v;
  }

  static int Main(string[] args) {
    string dir = args.Length > 0 ? args[0] : "dps-png"; Directory.CreateDirectory(dir);
    var fights = (List<object>)MiniJson.Parse(File.ReadAllText("tests/fixtures/dps-battles.json"));
    var boss = FightOf(fights, 5001007); var arena = FightOf(fights, 6001642);
    var sb = Replay(boss, true); var sbMid = Replay(boss, false); var sa = Replay(arena, true);
    Check("atlases", DpsArt.HasAtlases, "the three atlases are read (app/res/dps_*)");
    Check("atlases", DpsArt.HasFira, "Fira Sans is loaded");
    int[][] sizes = { new[] { 1024, 576 }, new[] { 1280, 720 }, new[] { 1920, 1080 }, new[] { 2560, 1440 }, new[] { 3840, 2160 } };
    string[] langs = { "ru", "en" };
    foreach (var z in sizes) {
      string t = z[0] + "x" + z[1]; bool keep = z[0] == 1920 || z[0] == 1280;
      {
        string sn = "a";
        foreach (var lg in langs) {
          string pre = sn + "-" + lg + "-" + t + "-";
          foreach (var m in DpsPanel.Modes) {
            Shot(dir, pre + "boss-" + m.ToString().ToLower(), sb, new DpsView { Lang = lg, Mode = m }, z[0], z[1], keep && lg == "ru" && z[0] == 1920, false, true);
            Shot(dir, pre + "arena-" + m.ToString().ToLower(), sa, new DpsView { Lang = lg, Mode = m }, z[0], z[1], false, false, true);
            Shot(dir, pre + "boss-skills-" + m.ToString().ToLower(), sb, Open(sb, new DpsView { Lang = lg, Mode = m }), z[0], z[1], false, m == DpsMode.Damage || m == DpsMode.ToBoss, true);
          }
          foreach (var sg in DpsPanel.Segments) Shot(dir, pre + "boss-seg-" + sg.ToString().ToLower(), sb, new DpsView { Lang = lg, Segment = sg }, z[0], z[1], false, false, sg != DpsSegment.Previous);
          Shot(dir, pre + "arena-skills", sa, Open(sa, new DpsView { Lang = lg }), z[0], z[1], false, false, true);
          Shot(dir, pre + "live", sbMid, new DpsView { Lang = lg }, z[0], z[1], false, false, true);
          Shot(dir, pre + "empty", new DpsFeed().Snapshot, new DpsView { Lang = lg }, z[0], z[1], false, false, false);
          Shot(dir, pre + "ctrl", sb, new DpsView { Lang = lg, Interactive = true, Mode = DpsMode.Taken }, z[0], z[1], keep && lg == "en", false, true);
        }
        // the four key pictures: the list and a hero's skills (Russian, damage)
        Shot(dir, sn + "-list-" + t, sb, new DpsView(), z[0], z[1], keep, false, true);
        Shot(dir, sn + "-skills-" + t, sb, Open(sb, new DpsView()), z[0], z[1], keep, true, true);
      }
    }
    foreach (var f in new[] { "a-list", "a-skills" }) File.Copy(Path.Combine(dir, f + "-1920x1080.png"), Path.Combine(dir, f + ".png"), true);
    Console.WriteLine("portraits drawn " + ptReal + ", skill icons: own " + iconOwn + ", common " + iconGen);
    // "Other" on the fixture: the rest row stays small
    long total = 0, other = 0; int heroesWithSkills = 0; var ids = new HashSet<int>();
    foreach (var fo in fights) {
      var sn = Replay((Dictionary<string, object>)fo, true);
      if (sn.Current == null) continue;
      foreach (var hero in sn.Current.Heroes) {
        if (hero.Skills == null) continue;
        heroesWithSkills++;
        foreach (var k in hero.Skills) { total += k.Amount; ids.Add(k.Id); }
        foreach (var r in DpsSkillRows.Group(hero.Unit, hero.Skills)) if (r.Tail) other += r.Amount;
      }
    }
    Check("other", total > 0 && other <= total * 0.05, "Other " + other + " of " + total + " (" + (total > 0 ? other * 100.0 / total : 0).ToString("0.0") + "%), " + ids.Count + " skill ids, " + heroesWithSkills + " heroes");
    Console.WriteLine("Other = " + (total > 0 ? other * 100.0 / total : 0).ToString("0.00") + "% of the damage on " + ids.Count + " skill ids");
    AtlasBrightness();
    Timing(sb);
    Check("number format", DpsPanel.Short(1234567) == "1.23M" && DpsPanel.Short(950) == "950" && DpsPanel.Short(12345) == "12.3K" && DpsPanel.Short(2500000000) == "2.50B", "number format");
    foreach (var kv in passes) Console.WriteLine("ok: " + kv.Key + " (" + kv.Value + " cases)");
    Console.WriteLine("cache entries at the end: " + DpsArt.CacheCount);
    Console.WriteLine(fails == 0 ? "PASS" : "FAILED: " + fails);
    return fails == 0 ? 0 : 1;
  }

  // the mean brightness (0..255) of every icon cell of the skill atlas: an aspect icon (dark outline on alpha) must not vanish into the dark row (> 45);
  // 35 skill icons are dark art by themselves (30..45, opaque pictures): those only must not be blank (> 25)
  static void AtlasBrightness() {
    var atl = DpsArt.SkillAtlas; if (atl == null) return;
    var data = atl.LockBits(new Rectangle(0, 0, atl.Width, atl.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
    try {
      var buf = new byte[Math.Abs(data.Stride) * atl.Height]; System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
      double minAsp = 999, minSk = 999; string whoAsp = "", whoSk = ""; int nAsp = 0, nSk = 0, darkSk = 0;
      foreach (var kv in DpsArtIndex.Skills) {
        var r = kv.Value; double sum = 0; int n = 0;
        for (int y = r[1]; y < r[1] + r[3]; y += 2) for (int x = r[0]; x < r[0] + r[2]; x += 2) {
          int o = y * data.Stride + x * 4; sum += 0.299 * buf[o + 2] + 0.587 * buf[o + 1] + 0.114 * buf[o]; n++;
        }
        double m = n > 0 ? sum / n : 0;
        if (kv.Key.StartsWith("aspect:")) { nAsp++; if (m < minAsp) { minAsp = m; whoAsp = kv.Key; } Check("aspect icons bright", m > 45, kv.Key + " mean brightness " + m.ToString("0.0")); }
        else { nSk++; if (m < minSk) { minSk = m; whoSk = kv.Key; } if (m <= 45) darkSk++; Check("skill icons bright", m > 25, kv.Key + " mean brightness " + m.ToString("0.0")); }
      }
      Console.WriteLine("icon brightness: " + nAsp + " aspects, darkest " + whoAsp + " " + minAsp.ToString("0.0") + "; " + nSk + " skills, darkest " + whoSk + " " + minSk.ToString("0.0") + ", dark (<=45): " + darkSk);
    } finally { atl.UnlockBits(data); }
  }

  // Render on 300x500: 50 frames per view, warm cache
  static void Timing(DpsSnapshot sb) {
    foreach (bool skills in new[] { false, true }) {
      var v = new DpsView(); if (skills) Open(sb, v);
      var sw = Stopwatch.StartNew();
      using (var cold = DpsPanel.Render(sb, v, 300, 500, 1f, null)) { }
      double c0 = sw.Elapsed.TotalMilliseconds;
      double max = 0, sum = 0;
      for (int i = 0; i < 50; i++) {
        sw.Restart();
        using (var b = DpsPanel.Render(sb, v, 300, 500, 1f, null)) { }
        double ms = sw.Elapsed.TotalMilliseconds; sum += ms; if (ms > max) max = ms;
      }
      string nm = skills ? "skills" : "list";
      Console.WriteLine("Render 300x500 " + nm + ": first " + c0.ToString("0.0") + " ms, 50 frames avg " + (sum / 50).ToString("0.00") + " ms, max " + max.ToString("0.0") + " ms");
      Check("render time", sum / 50 <= 8.0, "Render " + nm + " avg " + (sum / 50).ToString("0.00") + " ms > 8");
    }
  }
}
#endif
