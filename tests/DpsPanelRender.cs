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
  static void Shot(string dir, string name, DpsSnapshot snap, DpsView v, int gw, int gh, bool save, bool skillsView, bool needPortraits, int ow = 0, int oh = 0, string crop = null) {
    double U = Math.Min(gh, gw * 675.0 / 1200.0); float s = DpsPanel.ScaleFor(U);
    int w = ow > 0 ? ow : DpsPanel.DefaultW(U), h = oh > 0 ? oh : DpsPanel.DefaultH(U);
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
      if (crop != null) {
        // x3 crops (nearest neighbour: every pixel of the panel is visible): the top with the tabs, two hero rows, a hero header with two skill rows
        float ls = Math.Max(s, 0.9f);
        var rects = new List<KeyValuePair<string, Rectangle>>();
        if (crop == "list") {
          rects.Add(new KeyValuePair<string, Rectangle>("top", new Rectangle(0, 0, w, (int)(100 * ls))));
          rects.Add(new KeyValuePair<string, Rectangle>("hero", new Rectangle(0, (int)(96 * ls), w, (int)(80 * ls))));
        } else rects.Add(new KeyValuePair<string, Rectangle>("skill", new Rectangle(0, (int)(96 * ls), w, (int)(112 * ls))));
        using (var sc = PanelScene(panel, gw, gh, px, py))
          foreach (var kv in rects) {
            var rc = Rectangle.Intersect(kv.Value, new Rectangle(0, 0, w, h));
            using (var big = new Bitmap(rc.Width * 3, rc.Height * 3, PixelFormat.Format24bppRgb))
            using (var gg = Graphics.FromImage(big)) {
              gg.InterpolationMode = InterpolationMode.NearestNeighbor; gg.PixelOffsetMode = PixelOffsetMode.Half;
              gg.DrawImage(sc, new Rectangle(0, 0, big.Width, big.Height), rc.X, rc.Y, rc.Width, rc.Height, GraphicsUnit.Pixel);
              big.Save(Path.Combine(dir, "crop3-" + name + "-" + kv.Key + ".png"), ImageFormat.Png);
            }
          }
      }
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
    // the narrow window the owner has (about 0.25 x 0.39 of the game height, 250 x 393 on 1920x1009) and the default one, with x3 crops, three game sizes
    foreach (var z in new[] { new[] { 1920, 1009 }, new[] { 2560, 1440 }, new[] { 3840, 2160 } }) {
      double U = Math.Min(z[1], z[0] * 675.0 / 1200.0); string t = z[0] + "x" + z[1];
      foreach (var win in new[] { new[] { (int)Math.Round(0.248 * U), (int)Math.Round(0.39 * U) }, new[] { DpsPanel.DefaultW(U), DpsPanel.DefaultH(U) } }) {
        string tag = "w" + win[0] + "-" + t;
        Shot(dir, "narrow-list-ru-" + tag, sb, new DpsView { Lang = "ru" }, z[0], z[1], true, false, true, win[0], win[1], "list");
        Shot(dir, "narrow-skills-ru-" + tag, sb, Open(sb, new DpsView { Lang = "ru" }), z[0], z[1], true, true, true, win[0], win[1], "skills");
        Shot(dir, "narrow-list-en-" + tag, sb, new DpsView { Lang = "en", Mode = DpsMode.Heal }, z[0], z[1], false, false, true, win[0], win[1], null);
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
    HintTests(sb, dir);
    TipTests(dir);
    Timing(sb);
    Check("number format", DpsPanel.Short(1234567) == "1.23M" && DpsPanel.Short(950) == "950" && DpsPanel.Short(12345) == "12.3K" && DpsPanel.Short(2500000000) == "2.50B", "number format");
    foreach (var kv in passes) Console.WriteLine("ok: " + kv.Key + " (" + kv.Value + " cases)");
    Console.WriteLine("cache entries at the end: " + DpsArt.CacheCount);
    Console.WriteLine(fails == 0 ? "PASS" : "FAILED: " + fails);
    return fails == 0 ? 0 : 1;
  }

  // the skill tooltip: the lookup (own text, a nested skill's root with a note, categories, nothing), the wrapping, the place, and the pictures at three sizes
  static void TipTests(string dir) {
    Check("tip data", DpsDesc.Count > 1500, "descriptions loaded: " + DpsDesc.Count);
    var basic = DpsDesc.Find(2266, 226601, true);
    Check("tip lookup", basic.Title == DpsNames.Skill(226601, true) && basic.Body.Contains("Солнечный режим") && basic.Note == null, "own text of 226601");
    var en = DpsDesc.Find(2266, 226601, false);
    Check("tip lookup", en.Body.Contains("Solar Mode") && en.Title == DpsNames.Skill(226601, false), "english text of 226601");
    var nested = DpsDesc.Find(2266, 226609, true);
    Check("tip lookup", DpsDesc.Get(226609, true) == null && nested.Body == DpsDesc.Get(226601, true) && nested.Title == DpsNames.Skill(226601, true) && nested.Note == "часть скилла: " + DpsNames.Skill(226601, true), "nested skill 226609: the root's text and the note (" + nested.Note + ")");
    var nen = DpsDesc.Find(2266, 226609, false);
    Check("tip lookup", nen.Note == "part of skill: " + DpsNames.Skill(226601, false), "nested skill, english note (" + nen.Note + ")");
    var tal = DpsDesc.Find(2266, 226630, true);
    Check("tip lookup", tal.Title.StartsWith("Талант", StringComparison.Ordinal) && tal.Body.Length > 20, "a talent has its text: " + tal.Title);
    var asp = DpsDesc.Find(2266, 2290104, true); var set = DpsDesc.Find(2266, 102000201, true); var fld = DpsDesc.Find(2266, 13000926, false);
    Check("tip lookup", asp.Title == DpsNames.Category(2290104, true) && asp.Body == "" && asp.Note == null && set.Title == "Комплект" && set.Body == "" && fld.Title == "Field effect" && fld.Body == "", "aspect, set, field effect: the category name only");
    var none = DpsDesc.Find(2266, 987654321, true); var none2 = DpsDesc.Find(2266, 987654321, false);
    Check("tip lookup", none.Title == "Прочее" && none.Body == "Описание недоступно" && none2.Body == "No description", "unknown id: no description");
    Check("tip lookup", DpsDesc.Tail(true).Title == "Прочее" && DpsDesc.Tail(false).Body == "", "the folded row: its name");
    // the table format: shared ids, escaped line breaks and backslashes
    byte[] gz;
    using (var ms = new MemoryStream()) {
      using (var z = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress)) { var b = System.Text.Encoding.UTF8.GetBytes("1,2\tодин\\nдва\\\\x\tone\n3\tтри\tthree\nbad line"); z.Write(b, 0, b.Length); }
      gz = ms.ToArray();
    }
    var tbl = DpsDesc.Parse(gz);
    Check("tip data", tbl.Count == 3 && ReferenceEquals(tbl[1], tbl[2]) && tbl[1][0] == "один\nдва\\x" && tbl[3][1] == "three", "the table format: " + tbl.Count + " ids");
    // wrapping
    var f = DpsArt.Semi(12);
    foreach (float mw in new[] { 90f, 150f, 240f }) {
      var lines = DpsTip.Wrap("Солнечный режим: 1 раз наносит 120% масс. УРН нескольким (до 8) врагам.\nСверхдлинноесловобезпробеловсверхдлинноесловобезпробелов конец", 12, false, mw, 14);
      bool ok = lines.Count > 1; foreach (var l in lines) if (DpsArt.Measure(l, 12, false) > mw + 0.5f) ok = false;
      Check("tip wrap", ok, "wrapped to " + mw + " px: " + lines.Count + " lines");
    }
    var cutl = DpsTip.Wrap(new string('a', 5) + " " + string.Join(" ", new string[60]).Replace("  ", " слово "), 12, false, 100, 3);
    Check("tip wrap", cutl.Count == 3 && cutl[2].EndsWith("…", StringComparison.Ordinal), "at most 3 lines, the last ends with an ellipsis: " + cutl.Count + " " + cutl[cutl.Count - 1]);
    // the place: beside the cursor, flipped at the right and bottom edges, always inside the screen
    var scr = new Rectangle(0, 0, 1920, 1080);
    var pa = DpsTip.Place(new Point(100, 100), 300, 200, scr, 18); var pb = DpsTip.Place(new Point(1800, 100), 300, 200, scr, 18);
    var pc = DpsTip.Place(new Point(100, 1000), 300, 200, scr, 18); var pd = DpsTip.Place(new Point(1900, 1075), 300, 200, scr, 18); var pe = DpsTip.Place(new Point(5, 5), 5000, 5000, scr, 18);
    Check("tip place", pa.X == 118 && pa.Y == 118 && pb.X == 1482 && pc.Y == 782 && pd.X + 300 <= 1920 && pd.Y + 200 <= 1080 && pe.X >= 0 && pe.Y >= 0, "tooltip places: " + pa + " " + pb + " " + pc + " " + pd);
    // the pictures
    int[][] sizes = { new[] { 1920, 1009 }, new[] { 2560, 1440 }, new[] { 3840, 2160 } };
    var cases = new[] { new { N = "long", I = DpsDesc.Find(2266, 226605, true) }, new { N = "short", I = DpsDesc.Find(2266, 226698, true) }, new { N = "nested", I = nested }, new { N = "nested-en", I = nen },
      new { N = "none", I = none }, new { N = "aspect", I = asp }, new { N = "talent", I = tal }, new { N = "long-en", I = DpsDesc.Find(2266, 226605, false) } };
    foreach (var z in sizes) {
      double U = Math.Min(z[1], z[0] * 675.0 / 1200.0); float sc = DpsPanel.ScaleFor(U); int maxW = DpsTip.MaxWidth(U);
      foreach (var cs in cases) {
        string nm = "tip-" + cs.N + "-" + z[0] + "x" + z[1];
        var probe = new List<DpsArt.TextRec>(); DpsArt.Probe = probe;
        using (var tip = DpsTip.Render(cs.I, sc, maxW)) {
          DpsArt.Probe = null;
          Check("tip size", tip.Width <= maxW + 2 && tip.Width >= 60 && tip.Height <= z[1] * 0.6, nm + ": " + tip.Width + "x" + tip.Height + " (max width " + maxW + ")");
          foreach (var r in probe) Check("tip text fits", r.Width <= r.Box.Width + 0.5f && r.Glyphs.Right <= tip.Width && r.Glyphs.Bottom <= tip.Height + 2, nm + ": \"" + r.Text + "\" " + r.Width.ToString("0.0") + " in " + r.Box.Width.ToString("0.0"));
          Check("tip text fits", probe.Count > 0, nm + ": text drawn");
          if (z[0] == 1920 || cs.N == "long" || cs.N == "nested") using (var scn = PanelScene(tip, z[0], z[1], z[0] / 2, z[1] / 3)) scn.Save(Path.Combine(dir, nm + ".png"), ImageFormat.Png);
        }
      }
    }
  }

  // the Ctrl hint line: the counter file (5 fights, or the first use of Ctrl) and the picture with the line in the narrow and the default window
  static void HintTests(DpsSnapshot sb, string dir) {
    string p = Path.Combine(Path.GetTempPath(), "dps-hint-test-" + Guid.NewGuid().ToString("N") + ".json");
    try {
      var st = new DpsHintStore(p);
      Check("hint store", st.Show && st.Fights == 0 && !File.Exists(p), "a fresh store shows the hint and writes nothing");
      for (int i = 0; i < DpsHintStore.MaxFights; i++) st.FightSeen();
      Check("hint store", st.Show && new DpsHintStore(p).Show && new DpsHintStore(p).Fights == DpsHintStore.MaxFights, "the hint is still shown in the fifth fight (and after a restart)");
      st.FightSeen();
      Check("hint store", !st.Show && !new DpsHintStore(p).Show && !File.Exists(p + ".tmp"), "the sixth fight: no hint, kept over a restart, no temp file left");
      string p2 = p + ".ctrl"; var s2 = new DpsHintStore(p2); s2.FightSeen(); s2.UsedCtrl();
      Check("hint store", !s2.Show && !new DpsHintStore(p2).Show && new DpsHintStore(p2).CtrlUsed, "Ctrl used once: the hint is gone for good");
      File.Delete(p2);
      File.WriteAllText(p2, "{ not json"); Check("hint store", new DpsHintStore(p2).Show, "a broken file gives the defaults"); File.Delete(p2);
    } finally { try { File.Delete(p); File.Delete(p + ".tmp"); } catch (Exception) { } }
    Check("hint text", DpsPanel.HintText(false, 12, 1000) == "Ctrl: двигать, менять размер, нажимать" && DpsPanel.HintText(true, 12, 1000) == "Ctrl: move, resize, click" && DpsPanel.HintText(false, 12, 60).Length < 30, "the hint wording and its short form");
    foreach (var z in new[] { new[] { 1920, 1009 }, new[] { 2560, 1440 }, new[] { 3840, 2160 } }) {
      double U = Math.Min(z[1], z[0] * 675.0 / 1200.0); string t = z[0] + "x" + z[1];
      foreach (var lg in new[] { "ru", "en" }) {
        int nw = (int)Math.Round(0.248 * U), nh = (int)Math.Round(0.39 * U);
        Shot(dir, "hint-" + lg + "-narrow-" + t, sb, new DpsView { Lang = lg, Hint = true }, z[0], z[1], lg == "ru", false, true, nw, nh);
        Shot(dir, "hint-" + lg + "-" + t, sb, new DpsView { Lang = lg, Hint = true }, z[0], z[1], false, false, true);
        Shot(dir, "hint-" + lg + "-skills-narrow-" + t, sb, Open(sb, new DpsView { Lang = lg, Hint = true }), z[0], z[1], false, false, true, nw, nh);
      }
    }
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
