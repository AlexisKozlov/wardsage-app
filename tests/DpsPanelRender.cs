// Draws the DPS meter's panel (app/DpsPanel.cs) over a fake game picture, from the saved fights (tests/fixtures/dps-battles.json), into PNG files:
// boss / arena, a hero's skills open, three game window sizes. Needs GDI+ (Windows, or Mono with libgdiplus). Compiled with -define:TESTBUILD.
//   DpsPanelRender.exe <out dir>      exit 0 = drawn, panels not blank
#if TESTBUILD
using System;
using System.Collections.Generic;
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

  static int fails;
  static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } else Console.WriteLine("ok: " + what); }

  static void Shot(string dir, string name, DpsSnapshot snap, DpsView v, int gw, int gh) {
    double U = Math.Min(gh, gw * 675.0 / 1200.0); float s = DpsPanel.ScaleFor(U);
    int w = DpsPanel.DefaultW(U), h = DpsPanel.DefaultH(U);
    var hits = new List<DpsHit>();
    using (var panel = DpsPanel.Render(snap, v, w, h, s, hits))
    using (var scene = new Bitmap(gw, gh, PixelFormat.Format24bppRgb)) {
      using (var g = Graphics.FromImage(scene)) {
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, gw, gh), Color.FromArgb(52, 86, 64), Color.FromArgb(120, 104, 70), 60f)) g.FillRectangle(bg, 0, 0, gw, gh);
        using (var gr = new Pen(Color.FromArgb(40, 255, 255, 255))) for (int x = 0; x < gw; x += (int)(U / 8)) g.DrawLine(gr, x, 0, x, gh);
        g.DrawImage(panel, gw - w - (int)(0.012 * U), (int)(0.22 * gh));
      }
      // not blank: some pixels inside the panel differ from the backdrop clearly
      int px = gw - w - (int)(0.012 * U), py = (int)(0.22 * gh), lit = 0;
      for (int y = py + 10; y < py + h - 10; y += 3) for (int x = px + 10; x < px + w - 10; x += 3) { var c = scene.GetPixel(x, y); if (c.R > 190 && c.G > 190 && c.B > 190) lit++; }
      Check(lit > 10 && hits.Count >= 7, name + ": drawn (" + lit + " bright samples, " + hits.Count + " pressable places)");
      scene.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
    }
  }

  static int Main(string[] args) {
    string dir = args.Length > 0 ? args[0] : "dps-png"; Directory.CreateDirectory(dir);
    var fights = (List<object>)MiniJson.Parse(File.ReadAllText("tests/fixtures/dps-battles.json"));
    Dictionary<string, object> boss = null, arena = null;
    foreach (var f in fights) { var d = (Dictionary<string, object>)f; int st = (int)Num(d, "stage"); if (st == 5001007) boss = d; if (st == 6001642) arena = d; }
    var sb = Replay(boss, true); var sbMid = Replay(boss, false); var sa = Replay(arena, true);
    int[][] sizes = { new[] { 1366, 705 }, new[] { 1920, 1080 }, new[] { 2560, 1440 } };
    foreach (var z in sizes) {
      string t = z[0] + "x" + z[1];
      Shot(dir, "boss-damage-" + t, sb, new DpsView(), z[0], z[1]);
      Shot(dir, "boss-live-damage-" + t, sbMid, new DpsView(), z[0], z[1]);
      Shot(dir, "boss-toboss-" + t, sb, new DpsView { Mode = DpsMode.ToBoss }, z[0], z[1]);
      var top = sb.Current.Sorted(DpsMode.Damage)[0];
      Shot(dir, "boss-skills-" + t, sb, new DpsView { HasOpen = true, OpenC = top.C, OpenUnit = top.Unit }, z[0], z[1]);
      Shot(dir, "arena-damage-" + t, sa, new DpsView(), z[0], z[1]);
      Shot(dir, "arena-taken-ctrl-" + t, sa, new DpsView { Mode = DpsMode.Taken, Interactive = true, Lang = "en" }, z[0], z[1]);
    }
    Shot(dir, "boss-total-1920x1080", sb, new DpsView { Segment = DpsSegment.Total }, 1920, 1080);
    Check(DpsPanel.Short(1234567) == "1.23M" && DpsPanel.Short(950) == "950" && DpsPanel.Short(12345) == "12.3K" && DpsPanel.Short(2500000000) == "2.50B", "number format");
    Console.WriteLine(fails == 0 ? "PASS" : "FAILED: " + fails);
    return fails == 0 ? 0 : 1;
  }
}
#endif
