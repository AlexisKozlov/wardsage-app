// Tests for the parts of the extractor that do not need Windows: JSON, sync code / site checks,
// game version parsing, progress mapping, config, and the HTTP client against tests/mock-server.mjs.
//
//   CoreTests <mock base url> [account.json to send]
//
// Built by tests/run-tests.sh with the .NET 8 SDK's csc (no NuGet needed).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RealmForge;

static partial class CoreTests {
  static int passed, failed;

  // ConfirmChoice (a tie of both blue confirm buttons) and GameGeomCheck (the window geometry self-check), app/HintGeometry.cs
  static void ConfirmChoiceTests() {
    Console.WriteLine("ConfirmChoice / GameGeomCheck (confirm button tie, window geometry self-check):");
    var c = new ConfirmChoice();
    int[] rep = { 962, 1160, 285, 78 }, eq = { 1741, 1294, 275, 78 };
    Check(c.Pick(rep, eq, 1, 3440, 1440) == null, "tie with nothing learned: no button");
    Check(c.Pick(null, eq, 2, 3440, 1440) == eq, "a single «Надеть» is used");
    c.Worn(3); Check(c.Learned(3440, 1440) == "", "another item worn: nothing learned");
    c.Worn(2); Check(c.Learned(3440, 1440) == "equip", "the pending item worn: «Надеть» place learned for the size");
    Check(c.Pick(rep, eq, 4, 3440, 1440) == eq, "tie at that size: the learned «Надеть» place");
    Check(c.Pick(rep, eq, 4, 1920, 1009) == null, "tie at another size: still no button");
    Check(c.Pick(null, null, 4, 3440, 1440) == null, "neither: no button");
    Check(c.Pick(rep, null, 5, 1920, 1009) == rep, "a single «Заменить» is used");
    c.Worn(5); Check(c.Learned(1920, 1009) == "replace" && c.Pick(rep, eq, 6, 1920, 1009) == rep, "«Заменить» learned at 1920x1009 decides its ties");
    int x, y;
    GameGeomCheck.OriginFromWindow(100, 50, 100 + 1936, 50 + 1048, 1920, 1009, out x, out y);
    Check(x == 108 && y == 81, "origin from a framed window: border 8, caption 31 (" + x + "," + y + ")");
    GameGeomCheck.OriginFromWindow(0, 0, 3440, 1440, 3440, 1440, out x, out y);
    Check(x == 0 && y == 0, "borderless full screen: origin 0,0");
    string l = GameGeomCheck.Line(0, 0, 0, 0, 3440, 1440, 3440, 1440, 96, "");
    Check(l.Contains("3440x1440 at 0,0") && l.Contains(" same)") && l.Contains("wine no"), "line, Windows: " + l);
    l = GameGeomCheck.Line(0, 0, 0, 25, 3440, 1465, 3440, 1440, 144, "8.0.1");
    Check(l.Contains("DIFFERS 0,25") && l.Contains("dpi 144") && l.Contains("wine 8.0.1"), "line, a shifted window under Wine: " + l);
  }

  /// <summary>The periods of the periodic work (src/LoadPlan.cs) and the load line (LoadMeter).</summary>
  static void LoadPlanTests() {
    Console.WriteLine("Load plan and meter (src/LoadPlan.cs):");
    // overlay tick: job, diag, effect, shown, front, listHint, listMoving, inputMs
    Eq(LoadPlan.IdleMs, LoadPlan.OverlayMs(false, false, false, true, true, false, false, 99999), "no guide: once a second (only the recorder keeps the tick then)");
    Eq(LoadPlan.DiagMs, LoadPlan.OverlayMs(false, true, false, true, true, false, false, 99999), "frame diagnostics: 100 ms as before");
    Eq(LoadPlan.EffectMs, LoadPlan.OverlayMs(true, false, true, false, false, false, false, 99999), "a click awaiting its effect: 50 ms, even with the game behind");
    Eq(LoadPlan.AwayMs, LoadPlan.OverlayMs(true, false, false, false, false, true, true, 0), "the game minimized: 2 s");
    Eq(LoadPlan.AwayMs, LoadPlan.OverlayMs(true, false, false, true, false, false, false, 0), "the game behind another window: 2 s");
    Eq(LoadPlan.FollowMs, LoadPlan.OverlayMs(true, false, false, true, true, true, true, 99999), "a list moving under the frames: 33 ms");
    Eq(LoadPlan.FollowMs, LoadPlan.OverlayMs(true, false, false, true, true, true, false, 300), "a list hint, the wheel or the mouse just used: 33 ms (the list may be scrolling)");
    Eq(LoadPlan.ActiveMs, LoadPlan.OverlayMs(true, false, false, true, true, true, false, 1500), "a list hint, the mouse used a moment ago: 100 ms");
    Eq(LoadPlan.ActiveMs, LoadPlan.OverlayMs(true, false, false, true, true, false, false, 300), "a guide without list frames, the mouse used: 100 ms");
    Eq(LoadPlan.CalmMs, LoadPlan.OverlayMs(true, false, false, true, true, true, false, 5000), "a list hint nobody touches: 250 ms");
    Eq(LoadPlan.CalmMs, LoadPlan.OverlayMs(true, false, false, true, true, false, true, 5000), "a moving list without frames over it: not followed");
    Eq(LoadPlan.ActiveMs, LoadPlan.OverlayMs(true, false, false, true, true, false, false, -1), "input age not known: as if active");
    // screen reads
    Eq(2000, LoadPlan.ReadGapMs(true, true, true, 0), "screens: the game away, every 2 s");
    Eq(45, LoadPlan.ReadGapMs(false, true, false, 99999), "screens: a click awaiting its effect, 45 ms");
    Eq(45, LoadPlan.ReadGapMs(false, false, true, 99999), "screens: a moving list, 45 ms");
    Eq(120, LoadPlan.ReadGapMs(false, false, false, 100), "screens: the mouse used, 120 ms as before");
    Eq(240, LoadPlan.ReadGapMs(false, false, false, 5000), "screens: nobody touching anything, 240 ms");
    Check(LoadPlan.LookFast(100) && !LoadPlan.LookFast(LoadPlan.InputRecentMs + 1), "the gear list's quick looks only while the mouse is used");
    Eq(400, LoadPlan.LivePollMs(true, true, true), "live poll: a guide on, 400 ms as before");
    Eq(2000, LoadPlan.LivePollMs(false, true, true), "live poll: no guide, 2 s");
    Check(LoadPlan.RecButtonMs(false) >= 1000 && LoadPlan.RecButtonMs(true) == 200, "record button: 1 s idle, 200 ms while recording");
    Eq(2000, LoadPlan.LivePollMs(true, true, false), "live poll: the game behind, 2 s");
    Eq(300, LoadPlan.CodeWatchMs(true, false, false), "code watch: the code window open, 300 ms");
    Eq(1000, LoadPlan.CodeWatchMs(false, true, true), "code watch: closed, 1 s");
    Eq(2000, LoadPlan.CodeWatchMs(false, false, false), "code watch: the game away, 2 s");
    Eq(800, LoadPlan.GradeWatchMs(true, true), "grades: 800 ms as before with the game in front");
    Eq(2000, LoadPlan.GradeWatchMs(true, false), "grades: 2 s with the game behind");
    Check(LoadPlan.StallRescanSec(0) == 20 && LoadPlan.StallRescanSec(1) == 60 && LoadPlan.StallRescanSec(2) == 120 && LoadPlan.StallRescanSec(3) == 300 && LoadPlan.StallRescanSec(9) == 300,
      "stall rescans: 20, 60, 120, then 300 s");
    double tot, one; LoadMeter.Pct(600, 60000, 4, out tot, out one);
    Check(Math.Abs(one - 1.0) < 1e-9 && Math.Abs(tot - 0.25) < 1e-9, "cpu: 600 ms in a minute = 1% of one core, 0.25% of all 4 cores");
    LoadMeter.Line(0, 1, 1, "", null);   // (the counters start from here)
    long f = System.Diagnostics.Stopwatch.Frequency;
    LoadMeter.Add(LoadMeter.Screens, f / 100); LoadMeter.Add(LoadMeter.Screens, f / 100); LoadMeter.AddMs(LoadMeter.Pass, 3000);
    string l = LoadMeter.Line(1200, 30000, 4, "hint", "mem 80 MB");
    Check(l.StartsWith("load: cpu 1.0% of all 4 cores (4.0% of one core) (hint), mem 80 MB; per min: ", StringComparison.Ordinal), "the line's head: per all cores and per one core: " + l);
    Check(l.Contains("screens 4x 40 ms") && l.Contains("pass 2x 6000 ms") && !l.Contains("tick "), "per activity: calls and ms a minute, only the ones that ran");
    string l2 = LoadMeter.Line(0, 60000, 4, "idle", null);
    Check(!l2.Contains("per min"), "the next line counts from the last one: " + l2);
    string l3 = LoadMeter.Line(2400, 1600, 60000, 4, "pilot+rec", null);
    Check(l3.StartsWith("load: cpu 1.0% of all 4 cores (4.0% of one core) (pilot+rec) = rec 0.7% (2.7% of one core) + player load 0.3% (1.3% of one core)", StringComparison.Ordinal),
          "the line splits the recorder's CPU from the rest (a player's load): " + l3);
  }

  /// <summary>--load-bench: the periodic work a minute in each situation, the plan before (origin/main 464c94a) and now (src/LoadPlan.cs), with
  /// the per call costs measured on the lab's slow laptop (1.6.66.403-409 logs: the screen read 3-4 ms, 10-30 ms while a search runs beside it; a memory pass 3.3-4.8 s, a fresh
  /// table search 13.6-13.9 s, a forms check 30-70 us) and of a managed tick here. A model: the lab's new load line gives the real numbers.</summary>
  static void LoadBench() {
    // a managed tick of the plan itself, measured here (the overlay's own tick is Win32 and not run here)
    var sw = System.Diagnostics.Stopwatch.StartNew(); int n = 0; long sink = 0;
    while (sw.ElapsedMilliseconds < 300) { sink += LoadPlan.OverlayMs(true, false, (n & 1) == 0, true, true, true, (n & 2) == 0, n & 4095) + LoadPlan.ReadGapMs(false, false, false, n); n++; }
    Console.WriteLine("LoadPlan decision: " + (sw.Elapsed.TotalMilliseconds * 1000 / n).ToString("0.000") + " us a call (" + sink % 7 + ")");
    const double ScreenMs = 4, PassS = 3.8, FreshS = 13.7, ShotMs = 8, DrawMs = 15, LiveMs = 1.5, CdkMs = 0.6, GradeMs = 0.6;
    // situations: name, old periods {tick, screens, look, draws per min, live, cdk, grades, fresh scans per min}, new ones
    var rows = new[] {
      new { N = "idle (no guide)",        O = new double[] { 0, 0, 0, 0, 400, 300, 800, 0 },   W = new double[] { 0, 0, 0, 0, 1000, 1000, 800, 0 } },
      new { N = "game behind / minimized", O = new double[] { 100, 1000, 0, 0, 400, 300, 800, 0 }, W = new double[] { 2000, 2000, 0, 0, 2000, 2000, 2000, 0 } },
      new { N = "hint, nobody touching",   O = new double[] { 100, 120, 30, 600, 400, 300, 800, 3 }, W = new double[] { 250, 240, 0, 0, 400, 1000, 800, 0.4 } },
      new { N = "hint, mouse used",        O = new double[] { 100, 120, 30, 600, 400, 300, 800, 3 }, W = new double[] { 100, 120, 30, 20, 400, 1000, 800, 0.4 } },
      new { N = "pilot clicking",          O = new double[] { 50, 45, 30, 600, 400, 300, 800, 0 }, W = new double[] { 50, 45, 30, 30, 400, 1000, 800, 0 } },
    };
    Func<double, double> perMin = ms => ms > 0 ? 60000 / ms : 0;
    Func<double[], double> cost = p => perMin(p[1]) * ScreenMs + perMin(p[2]) * ShotMs + p[3] * DrawMs + perMin(p[4]) * LiveMs + perMin(p[5]) * CdkMs + perMin(p[6]) * GradeMs + p[7] * FreshS * 1000;
    Console.WriteLine("situation: ms of CPU a minute before -> now (screen reads, quick looks, repaints, live poll, code watch, grades, fresh searches); % of one core");
    foreach (var r in rows) {
      double a = cost(r.O), b = cost(r.W);
      Console.WriteLine("  " + r.N.PadRight(24) + " " + a.ToString("0").PadLeft(6) + " -> " + b.ToString("0").PadLeft(5) + " ms/min  (" + (a / 600).ToString("0.0") + "% -> " + (b / 600).ToString("0.0") + "% of one core)"
        + "  screens " + perMin(r.O[1]).ToString("0") + "->" + perMin(r.W[1]).ToString("0") + "/min, looks " + perMin(r.O[2]).ToString("0") + "->" + perMin(r.W[2]).ToString("0") + ", repaints " + r.O[3] + "->" + r.W[3]
        + ", live " + perMin(r.O[4]).ToString("0") + "->" + perMin(r.W[4]).ToString("0") + ", cdk " + perMin(r.O[5]).ToString("0") + "->" + perMin(r.W[5]).ToString("0") + ", fresh searches " + r.O[7] + "->" + r.W[7]);
    }
    Console.WriteLine("  (a memory pass: " + PassS + " s; the lab's 15 passes a minute came from the fresh searches of a plan standing still)");
  }

  static void Check(bool ok, string what) {
    if (ok) { passed++; Console.WriteLine("  ok   " + what); }
    else { failed++; Console.WriteLine("  FAIL " + what); }
  }

  static void Eq<T>(T expected, T actual, string what) {
    Check(EqualityComparer<T>.Default.Equals(expected, actual), what + " (expected " + expected + ", got " + actual + ")");
  }

  static int Argb(int r, int g, int b) { return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b; }

  // A drawn gear list strip: night background, red cells with icons, slate stat bars with white text; row 1 top at 6 - scroll.
  static int[] FakeList(ListGeometry geo, double H, double scroll, out int w, out int h) {
    w = (int)((geo.StripRight - geo.StripLeft) * H); h = (int)((geo.ViewBottom - geo.ViewTop) * H);
    var px = new int[w * h]; var rnd = new Random(1);
    for (int i = 0; i < px.Length; i++) px[i] = Argb(15 + rnd.Next(15), 22 + rnd.Next(15), 40 + rnd.Next(20));
    double pitch = geo.PitchY * H;
    for (int r = 0; r < 60; r++) {
      double top = 6 - scroll + r * pitch;
      for (int c = 1; c <= 3; c++) {
        int l = (int)((geo.ColLeft(c) - geo.StripLeft) * H), cw = (int)(geo.CellW * H);
        for (int y = (int)top; y < top + geo.BarBottom * H; y++) {
          if (y < 0 || y >= h) continue;
          bool bar = y >= top + geo.BarTop * H;
          for (int x = l; x < l + cw && x < w; x++) {
            int v;
            if (bar) v = (x - l) % 9 < 2 && (y - (int)top) % 7 < 4 ? Argb(240, 240, 240) : Argb(74 + rnd.Next(10), 90 + rnd.Next(10), 124 + rnd.Next(10));
            else v = (x - l - cw / 2) * (x - l - cw / 2) + (y - (int)top - cw / 2) * (y - (int)top - cw / 2) < cw * cw / 9 ? Argb(60 + rnd.Next(80), 70 + rnd.Next(40), 120 + rnd.Next(60)) : Argb(150, 38, 48);
            px[y * w + x] = v;
          }
        }
      }
    }
    return px;
  }

  // a list strip drawn from list coordinates (row 1's top 6 px below the strip's top at scroll 0), so a scroll is an exact
  // shift; every item its own colours and icon spot, or (alike) every item the same
  static int[] FakeRows(ListGeometry geo, double H, int scroll, bool alike, out int w, out int h, int rows = int.MaxValue) {
    w = (int)((geo.StripRight - geo.StripLeft) * H); h = (int)((geo.ViewBottom - geo.ViewTop) * H);
    var px = new int[w * h]; double pitch = geo.PitchY * H; int cw = (int)(geo.CellW * H);
    for (int y = 0; y < h; y++) {
      int cy = y + scroll - 6;
      int r = (int)Math.Floor(cy / pitch); double t = cy - r * pitch;
      for (int x = 0; x < w; x++) {
        int v = Argb(20, 28, 45), c = 0, l = 0;
        for (int k = 1; k <= 3; k++) { int lk = (int)((geo.ColLeft(k) - geo.StripLeft) * H); if (x >= lk && x < lk + cw) { c = k; l = lk; } }
        if (c > 0 && r >= 0 && r < rows && t < geo.BarBottom * H) {
          int id = alike ? 1 : (int)(((((uint)r * 2654435761u) ^ ((uint)c * 40503u)) >> 11) % 23);   // no repeating pattern
          if (t >= geo.BarTop * H) v = Argb(80, 96, 130);
          else {
            int ix = x - l, iy = (int)t;
            bool icon = Math.Abs(ix - cw / 2 - (id % 5 - 2) * cw / 10) + Math.Abs(iy - cw / 2 - (id % 3 - 1) * cw / 8) < cw / 3;
            v = icon ? Argb(60 + id * 8, 200 - id * 6, 90 + id * 5) : Argb(120 + id * 5, 40 + id * 3, 50);
          }
        }
        px[y * w + x] = v;
      }
    }
    return px;
  }

  // the site checkout the tests read its data from: RF_WEB when set, else ../wardsage-web
  static string WebRoot() { string e = Environment.GetEnvironmentVariable("RF_WEB"); return string.IsNullOrEmpty(e) ? Path.Combine("..", "wardsage-web") : e; }

  static string Tok(char c) { return "rf_" + new string(c, 32); }

  static int Main(string[] args) {
    if (args.Length > 0 && args[0] == "--load-bench") { LoadBench(); return 0; }
    string mock = args.Length > 0 ? args[0] : "http://localhost:3999";
    string realAccount = args.Length > 1 ? args[1] : null;
    // the fight timeline and the battle reading take their game values from the layout data (src/Layout.cs): a test copy
    Layout.Use(TestLayout());

    Console.WriteLine("Per-skill battle statistics (m_vSkillDamage reader, fake memory):");
    {
      var fm = new FakeLua(1 << 16); RFX.TestMemory = fm;
      try {
        // Dictionary header: entries 0x18, count 0x20; entry arrays: length at +16, items at +24
        Func<int, int, ulong> MkDict = (stride, n) => {
          ulong d = fm.Alloc(0x30), arr = fm.Alloc(24 + stride * n + 8);
          fm.W64(d + 0x18, arr); fm.W32(d + 0x20, n); fm.W64(arr + 16, (ulong)n); return d;
        };
        Action<ulong, int, int, int, ulong, long> Ent = (d, stride, i, key, rv, lv) => {
          ulong arr = fm.R64(d + 0x18), e = arr + 24 + (ulong)(stride * i);
          fm.W32(e, 1); fm.W32(e + 4, -1); fm.W32(e + 8, key);
          if (stride == 0x18) { Array.Copy(BitConverter.GetBytes(lv), 0, fm.Mem, (int)(e + 16 - FakeLua.Base), 8); } else fm.W64(e + 16, rv);
        };
        ulong skills = MkDict(0x18, 2); Ent(skills, 0x18, 0, 101, 0, 700L); Ent(skills, 0x18, 1, 205, 0, 300L);
        ulong obj = fm.Alloc(0x40); fm.W64(obj + 0x10, skills);
        ulong inner = MkDict(32, 1); Ent(inner, 32, 0, 2034, obj, 0);
        ulong outer = MkDict(32, 1); Ent(outer, 32, 0, 1, inner, 0);
        ulong st = fm.Alloc(0x90); fm.W64(st + 0x78, outer);
        var skr = RFX.ReadSkillStatsAt(st, 0x78, 0x10);
        Check(skr != null && skr.ContainsKey("1:2034") && skr["1:2034"][101] == 700 && skr["1:2034"][205] == 300 && skr["1:2034"].Count == 2, "skill stats read: controller 1, unit 2034, two skills");
        var sl = new StatLine { C = 1, Unit = 2034, Damage = 1000 };
        var tb0 = new TimelineBuilder(); tb0.SetStats(new List<StatLine> { sl });
        bool sok; Eq(1, tb0.SetSkills(skr, out sok), "skills attached to one hero"); Check(sok, "sum check ok when skills add up to damage");
        string js = tb0.ToJson();
        Check(js != null && js.Contains("\"skSum\":1000,\"skills\":{\"101\":700,\"205\":300}"), "json has skills");
        sl.Damage = 999; tb0.SetSkills(skr, out sok); Check(!sok, "sum check mismatch is seen");
        fm.W64(st + 0x78, 0x1234);   // a bad pointer: nothing, no throw
        var bad = RFX.ReadSkillStatsAt(st, 0x78, 0x10);
        Check(bad == null || bad.Count == 0, "bad pointer: nothing read");
        fm.W32(skills + 0x20, 100000); fm.W64(st + 0x78, outer);
        var big = RFX.ReadSkillStatsAt(st, 0x78, 0x10);
        Check(big == null || big.Count == 0, "too many skills: nothing read");
        // the offsets by build: built-in for the checked builds 1.6.46 and 1.6.47, none for an unknown or an older one
        string lfx = File.ReadAllText(Path.Combine("tests", "layout-fixture.json"), Encoding.UTF8);
        foreach (var g in new[] { "1.6.46.841.1", "1.6.47.841.1", "1.6.39.829.1" }) {
          int sk, da; bool known = RFX.SkillOffsets(GameLayout.Parse(lfx.Replace("\"game\": \"9.8.7.6.5\"", "\"game\": \"" + g + "\""), g), out sk, out da);
          bool want = g != "1.6.39.829.1";
          Check(known == want && (!want || (sk == 0x78 && da == 0x10)), "skill stats offsets for " + g + (want ? ": built-in 0x78 / 0x10" : ": unknown, none"));
        }
      } finally { RFX.TestMemory = null; RFX.ForgetLive(); }
    }

    Console.WriteLine("Key name cache");
    {
      var kn = new RFX.KeyNames(); string kt;
      Check(!kn.TryGet(10, "m_Hero", 0, out kt), "unknown key address is read");
      kn.Set(10, "other", 0);
      Check(kn.TryGet(10, "m_Hero", 5000, out kt) && kt == "other", "a miss is trusted for a short time");
      Check(!kn.TryGet(10, "m_Hero", RFX.KeyNames.MissRetryMs + 1, out kt), "a miss is read again after the retry time");
      kn.Set(11, "m_Hero", 0);
      Check(kn.TryGet(11, "m_Hero", 600000, out kt) && kt == "m_Hero", "a match stays cached");
      kn.Remove(11); Check(!kn.TryGet(11, "m_Hero", 0, out kt), "removed entry is unknown");
    }

    Console.WriteLine("MiniJson");
    var o = (Dictionary<string, object>)MiniJson.Parse("\uFEFF {\"a\":[1,2.5,-3e2,true,false,null],\"s\":\"x\\\"\\u0416\\n\",\"o\":{}} ");
    Eq(3, o.Count, "object keys");
    Eq(6, ((List<object>)o["a"]).Count, "array length");
    Eq("x\"Ж\n", (string)o["s"], "string escapes");
    Eq(-300.0, (double)((List<object>)o["a"])[2], "exponent number");
    Check(MiniJson.TryParse("{\"a\":1,}") == null, "rejects trailing comma");
    Check(MiniJson.TryParse("{\"a\":1} x") == null, "rejects trailing data");
    Check(MiniJson.TryParse("") == null && MiniJson.TryParse(null) == null, "rejects empty");
    Eq("\"a\\\"b\\\\c\\u0001\"", MiniJson.Quote("a\"b\\c\u0001"), "Quote escapes");

    Console.WriteLine("Sync code");
    Check(SyncClient.IsValidCode(Tok('A')), "valid code");
    Check(SyncClient.IsValidCode("rf_0123456789abcdefghijABCDEFGHIJxy"), "valid mixed base62 code");
    Check(!SyncClient.IsValidCode("rf_" + new string('A', 31)), "31 chars rejected");
    Check(!SyncClient.IsValidCode("rf_" + new string('A', 33)), "33 chars rejected");
    Check(!SyncClient.IsValidCode("RF_" + new string('A', 32)), "prefix is case-sensitive");
    Check(!SyncClient.IsValidCode("rf_" + new string('A', 31) + "-"), "non-base62 rejected");
    Eq(Tok('A'), SyncClient.ExtractCode("  " + Tok('A') + "\r\n"), "paste with whitespace");
    Eq(Tok('A'), SyncClient.ExtractCode("Ваш код: " + Tok('A') + "."), "paste with surrounding text");
    Eq("rf_abc", SyncClient.ExtractCode(" rf_abc "), "incomplete code kept as typed");

    Console.WriteLine("Site address");
    string err;
    Eq("https://wardsage.com", SyncClient.NormalizeSite("https://wardsage.com/", out err), "trailing slash");
    Eq("https://wardsage.com", SyncClient.NormalizeSite("wardsage.com", out err), "scheme added");
    Eq("https://example.com/rf", SyncClient.NormalizeSite(" https://example.com/rf/ ", out err), "path prefix kept");
    Eq("http://localhost:3999", SyncClient.NormalizeSite("http://localhost:3999", out err), "http allowed for localhost");
    Check(SyncClient.NormalizeSite("http://example.com", out err) == null && err == "https", "http refused for remote hosts");
    Check(SyncClient.NormalizeSite("ftp://example.com", out err) == null && err == "invalid", "ftp refused");
    Check(SyncClient.NormalizeSite("https://example.com/?q=1", out err) == null, "query refused");
    Check(SyncClient.NormalizeSite("", out err) == null, "empty refused");
    Eq("https://x.app/app/h?s=1", SyncClient.ResolveUrl("https://x.app", "/app/h?s=1"), "relative viewUrl resolved");
    Eq("https://y.app/a", SyncClient.ResolveUrl("https://x.app", "https://y.app/a"), "absolute viewUrl kept");
    Check(SyncClient.ResolveUrl("https://x.app", "file:///C:/Windows/notepad.exe") == null, "file: viewUrl refused");
    Check(SyncClient.ResolveUrl("https://x.app", "javascript:alert(1)") == null, "javascript: viewUrl refused");

    Console.WriteLine("Reply mapping");
    var r = SyncClient.Interpret(200, "{\"ok\":true,\"snapshotId\":\"s1\",\"heroes\":2,\"items\":3,\"artifacts\":4,\"viewUrl\":\"/v\"}", null, null, "https://x.app");
    Check(r.Status == SyncStatus.Ok && r.Heroes == 2 && r.Items == 3 && r.Artifacts == 4 && r.ViewUrl == "https://x.app/v", "200 ok");
    Eq(SyncStatus.Unexpected, SyncClient.Interpret(200, "{\"ok\":false}", null, null, "https://x.app").Status, "200 without ok:true");
    Eq(SyncStatus.Unexpected, SyncClient.Interpret(200, "<html>", null, null, "https://x.app").Status, "200 html");
    Eq(SyncStatus.InvalidToken, SyncClient.Interpret(401, "{\"ok\":false,\"error\":\"invalid_token\"}", null, null, "https://x.app").Status, "401");
    Eq(SyncStatus.Forgotten, SyncClient.Interpret(409, "{\"ok\":false,\"error\":\"account_forgotten\"}", null, null, "https://x.app").Status, "409 account_forgotten");
    Eq(SyncStatus.Unexpected, SyncClient.Interpret(409, "{\"ok\":false,\"error\":\"conflict\"}", null, null, "https://x.app").Status, "409 other");
    Eq(SyncStatus.Unexpected, SyncClient.Interpret(409, null, null, null, "https://x.app").Status, "409 no body");
    {   // forgotten state: for that code only; a later 401 changes nothing here; ends by Clear (manual sync) or another code
      string c1 = "rf_" + new string('a', 32), c2 = "rf_" + new string('b', 32);
      SyncGate.Clear();
      Eq(false, SyncGate.IsForgotten(c1), "gate off");
      SyncGate.Mark(c1);
      Eq(true, SyncGate.IsForgotten(c1), "gate on");
      Eq(false, SyncGate.IsForgotten(c2), "gate: another code");
      Eq(SyncStatus.InvalidToken, SyncClient.Interpret(401, "{\"ok\":false,\"error\":\"invalid_token\"}", null, null, "https://x.app").Status, "401 while forgotten stays a plain 401 result");
      Eq(true, SyncGate.IsForgotten(c1), "gate not touched by a 401");
      SyncGate.Clear();
      Eq(false, SyncGate.IsForgotten(c1), "gate cleared");
    }
    Eq(SyncStatus.TooLarge, SyncClient.Interpret(413, null, null, null, "https://x.app").Status, "413");
    Eq(SyncStatus.UnsupportedMedia, SyncClient.Interpret(415, "", null, null, "https://x.app").Status, "415");
    r = SyncClient.Interpret(422, "{\"ok\":false,\"error\":\"invalid_payload\",\"details\":[\"a\",{\"path\":\"heroes\",\"message\":\"bad\"}]}", null, null, "https://x.app");
    Check(r.Status == SyncStatus.InvalidPayload && r.Details == "a; heroes: bad", "422 details: " + r.Details);
    r = SyncClient.Interpret(429, "{\"ok\":false,\"error\":\"rate_limited\"}", "90", null, "https://x.app");
    Check(r.Status == SyncStatus.RateLimited && r.RetryAfterSeconds == 90, "429 Retry-After header");
    r = SyncClient.Interpret(429, "{\"ok\":false,\"retryAfter\":30}", null, null, "https://x.app");
    Check(r.RetryAfterSeconds == 30, "429 retryAfter in body");
    Eq(SyncStatus.ServerError, SyncClient.Interpret(500, null, null, null, "https://x.app").Status, "500");
    Eq(SyncStatus.ServerError, SyncClient.Interpret(503, null, null, null, "https://x.app").Status, "503");
    Eq(SyncStatus.Unexpected, SyncClient.Interpret(404, null, null, null, "https://x.app").Status, "404");
    r = SyncClient.Interpret(308, null, null, "https://www.x.app/api/sync", "https://x.app");
    Check(r.Status == SyncStatus.Redirect && r.Details == "https://www.x.app/api/sync", "308 redirect");

    Console.WriteLine("Game version (realversion.xml)");
    Eq("1.2.3", GameInfo.ParseVersionXml("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root><version>1.2.3</version></root>"), "<version> element");
    Eq("2.4.0.51", GameInfo.ParseVersionXml("<?xml version=\"1.0\"?><Config RealVersion=\"2.4.0.51\" />"), "attribute, xml declaration ignored");
    Eq("1.10.2", GameInfo.ParseVersionXml("<a><b>1.10.2</b></a>"), "first version-like text node");
    Eq("1.0.7", GameInfo.ParseVersionXml("\uFEFF1.0.7\r\n"), "plain text file");
    Check(GameInfo.ParseVersionXml("<a><b>hello</b></a>") == null, "no version -> null");
    Check(GameInfo.ParseVersionXml(null) == null, "null input");
    Check(GameInfo.TryReadVersion(null) == null && GameInfo.TryReadVersion("/nonexistent/game.exe") == null, "missing file -> null");

    Console.WriteLine("Russian plurals / texts");
    Strings.Lang = "ru";
    Eq("1 герой", Strings.Heroes(1), "1"); Eq("3 героя", Strings.Heroes(3), "3"); Eq("5 героев", Strings.Heroes(5), "5");
    Eq("11 героев", Strings.Heroes(11), "11"); Eq("21 герой", Strings.Heroes(21), "21"); Eq("112 предметов", Strings.Items(112), "112");
    Eq("1109 предметов", Strings.Items(1109), "1109"); Eq("22 артефакта", Strings.Artifacts(22), "22");
    Strings.Lang = "en";
    Eq("1 hero", Strings.Heroes(1), "en 1"); Eq("128 heroes", Strings.Heroes(128), "en 128");
    Eq("2 min", Strings.Wait(90), "wait 90 s");
    Strings.Lang = "ru";

    Console.WriteLine("Read progress");
    var rp = new ReadProgress();
    string[] v04Log = {
      "RW regions: 812, 2400 MB", "  lua string 'iStarLvl' @ 0x1", "Strings found in 6 s",
      "Equipment tables...", "  nodes keyed 'iStarLvl': 2300", "  tables: 1200", "  item tables: 1150", "  references: 5000",
      "  equipment container #1: 1109 entries", "  equipment items (owned): 1109",
      "Hero tables...", "  nodes keyed 'vEquipSlot': 300", "  tables: 140", "  hero tables: 130", "  references: 900",
      "  hero container #1: 128 entries", "  heroes (owned): 128",
      "Faction rewards...", "  nodes keyed 'm_CampHeroPerfectReward': 2", "  tables: 1", "  reward events: 9",
      "Artifacts...", "  nodes keyed 'm_vArtifacts': 2", "  tables: 1", "  artifacts: 40", "Done in 41 s" };
    double last = 0; bool monotonic = true;
    foreach (var line in v04Log) { double p = rp.Feed(line); if (p < last) monotonic = false; last = p; }
    Check(monotonic && Math.Abs(last - 1.0) < 1e-9, "13 passes reach 100% (" + last + ")");

    Console.WriteLine("Extractor.Check");
    string payload = realAccount != null ? File.ReadAllText(realAccount, Encoding.UTF8) : SyntheticAccount();
    var ex = Extractor.Check(payload, new ExtractResult());
    Check(ex.Error == ExtractError.None && ex.Heroes > 0 && ex.Items > 0, "payload counts: " + ex.Heroes + " heroes, " + ex.Items + " items, " + ex.Artifacts + " artifacts");
    Check(SyncOutcome.Classify(true, ExtractError.NoAccountData) == SyncKind.NotReady, "auto sync, no account yet: not ready (no error)");
    Check(SyncOutcome.Classify(false, ExtractError.NoAccountData) == SyncKind.ReadFailed, "manual sync, no account: a read error");
    Check(SyncOutcome.Classify(true, ExtractError.Failed) == SyncKind.ReadFailed && SyncOutcome.Classify(true, ExtractError.None) == SyncKind.Ok, "other outcomes unchanged");
    Eq("RW regions: 120, 293 MB; Strings found in 3 s; heroes (owned): 0", SyncOutcome.ScanStats("noise\nRW regions: 120, 293 MB\n  lua x\nStrings found in 3 s\n  heroes (owned): 0\n"), "scan stats from the log");
    Check(SyncOutcome.AccountNotLoaded("Strings found: \n  nodes: 0\nEquipment tables...\n  nodes keyed 'iStarLvl': 0\n  tables: 0\nHero tables...\n  nodes keyed 'vEquipSlot': 0\n"), "no account keys at all: the game has not loaded the account");
    Check(!SyncOutcome.AccountNotLoaded("  lua string 'iStarLvl' @ 0x1\n  nodes keyed 'iStarLvl': 5\n  item tables: 0\n  nodes keyed 'vEquipSlot': 0\n"), "keys found but no tables: a real reading failure");
    Check(!SyncOutcome.AccountNotLoaded("") && !SyncOutcome.AccountNotLoaded("Done in 3 s"), "no scan lines: not told apart");
    Eq(ExtractError.NoAccountData, Extractor.Check("{\"equipment\":[],\"heroes\":[],\"campRewards\":null,\"artifacts\":null,\"meta\":{}}", new ExtractResult()).Error, "empty account -> NoAccountData");
    Eq(ExtractError.Failed, Extractor.Check("{\"equipment\":[", new ExtractResult()).Error, "broken JSON -> Failed");

    Console.WriteLine("Config");
    var lc = new AppConfig(); lc.LastAt = "2026-09-25T07:00:00Z"; lc.LastHeroes = 128; lc.LastItems = 1109; lc.LastArtifacts = 380;
    lc.LastTop.Add(2085); lc.LastTop.Add(2016);
    var lc2 = AppConfig.FromJson(lc.ToJson());
    Check(lc2.LastAt == lc.LastAt && lc2.LastHeroes == 128 && lc2.LastItems == 1109 && lc2.LastArtifacts == 380, "last sync round-trip");
    Check(lc2.LastTop.Count == 2 && lc2.LastTop[0] == 2085, "top heroes round-trip");
    Check(AppConfig.FromJson("{\"last\":{\"at\":\"x\",\"top\":[1,2,3,4,-5]}}").LastTop.Count == 3, "top capped at 3");
    var cfg = new AppConfig(); cfg.Code = Tok('A'); cfg.Site = "https://example.com"; cfg.Lang = "en"; cfg.SaveCopy = true;
    string cj = cfg.ToJson();
    Check(cj.IndexOf(Tok('A'), StringComparison.Ordinal) < 0 && cj.Contains("\"code\": \"dpapi:"), "code not stored in plain text");
    var back = AppConfig.FromJson(cj);
    Check(back.Code == Tok('A') && back.Site == "https://example.com" && back.Lang == "en" && back.SaveCopy, "config round trip");
    var cc = new AppConfig(); cc.CoachX = 0.25; cc.CoachY = 0.4;
    var cc2 = AppConfig.FromJson(cc.ToJson());
    Check(Math.Abs(cc2.CoachX - 0.25) < 1e-6 && Math.Abs(cc2.CoachY - 0.4) < 1e-6, "coach panel place round-trips");
    Check(AppConfig.FromJson("{}").CoachX < 0 && !new AppConfig().ToJson().Contains("coach"), "coach panel: default place unless moved");
    {
      var tc = new AppConfig(); Check(tc.TermsAccepted == "" && AppConfig.FromJson("{}").TermsAccepted != AppConfig.TermsVersion, "terms: not accepted by default");
      tc.TermsAccepted = AppConfig.TermsVersion;
      Check(AppConfig.FromJson(tc.ToJson()).TermsAccepted == AppConfig.TermsVersion, "terms: acceptance survives save and load");
      // settings folder move (src/FolderMigration.cs)
      string root = Path.Combine(Path.GetTempPath(), "rf-mig-" + Guid.NewGuid().ToString("N"));
      try {
        var logs = new List<string>(); Action<string> lg = delegate(string l) { logs.Add(l); };
        string o1 = Path.Combine(root, "a", "old"), n1 = Path.Combine(root, "a", "new");
        Directory.CreateDirectory(Path.Combine(o1, "WebView2", "Default")); Directory.CreateDirectory(Path.Combine(o1, "update")); Directory.CreateDirectory(Path.Combine(o1, "app", "x"));
        File.WriteAllText(Path.Combine(o1, "config.json"), "{\"lang\":\"en\"}");
        File.WriteAllText(Path.Combine(o1, "WebView2", "Default", "d.txt"), "d");
        File.WriteAllText(Path.Combine(o1, "update", "RealmForge-9.exe"), "x");
        File.WriteAllText(Path.Combine(o1, "app", "x", "y.dll"), "y");
        Check(FolderMigration.Run(o1, n1, lg), "folder move: copies when only the old folder exists");
        Check(File.ReadAllText(Path.Combine(n1, "config.json")) == "{\"lang\":\"en\"}" && File.Exists(Path.Combine(n1, "WebView2", "Default", "d.txt")), "folder move: files and subfolders copied");
        Check(!Directory.Exists(Path.Combine(n1, "update")) && !Directory.Exists(Path.Combine(n1, "app")), "folder move: staged updates and unpacked pages are not copied");
        Check(File.Exists(Path.Combine(n1, FolderMigration.MarkerName)), "folder move: marker written");
        Check(File.Exists(Path.Combine(o1, "config.json")) && File.Exists(Path.Combine(o1, "update", "RealmForge-9.exe")), "folder move: old folder left untouched");
        File.WriteAllText(Path.Combine(n1, "config.json"), "{\"lang\":\"ru\"}");
        Check(!FolderMigration.Run(o1, n1, lg) && File.ReadAllText(Path.Combine(n1, "config.json")) == "{\"lang\":\"ru\"}", "folder move: does not run twice or overwrite");
        string o2 = Path.Combine(root, "b", "old"), n2 = Path.Combine(root, "b", "new");
        Directory.CreateDirectory(o2); Directory.CreateDirectory(n2);
        File.WriteAllText(Path.Combine(o2, "config.json"), "old"); File.WriteAllText(Path.Combine(n2, "config.json"), "new");
        Check(!FolderMigration.Run(o2, n2, lg) && File.ReadAllText(Path.Combine(n2, "config.json")) == "new" && !File.Exists(Path.Combine(n2, FolderMigration.MarkerName)), "folder move: both exist, the new one is used as it is");
        string n3 = Path.Combine(root, "c", "new");
        Check(!FolderMigration.Run(Path.Combine(root, "c", "old"), n3, lg) && !Directory.Exists(n3), "folder move: no old folder, nothing created");
        string o4 = Path.Combine(root, "d", "old"), n4 = Path.Combine(root, "d", "new");
        Directory.CreateDirectory(o4); Directory.CreateDirectory(n4); File.WriteAllText(Path.Combine(o4, "config.json"), "old"); File.WriteAllText(Path.Combine(n4, "wardsage.log"), "log");
        Check(FolderMigration.Run(o4, n4, lg) && File.ReadAllText(Path.Combine(n4, "config.json")) == "old", "folder move: a new folder with only a log still receives the old settings");
        Check(logs.Count > 0, "folder move: steps are logged");
      } finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }
    {
      // choices of the installer (AppConfig.ApplyInstallerOptions)
      string io = Path.Combine(Path.GetTempPath(), "rf-inst-" + Guid.NewGuid().ToString("N"));
      try {
        var il = new List<string>(); Action<string> ilog = delegate(string l) { il.Add(l); };
        string optf = Path.Combine(io, AppConfig.InstallerOptionsName), cfgf = Path.Combine(io, "config.json");
        Directory.CreateDirectory(io);
        Check(!AppConfig.ApplyInstallerOptions(io, ilog) && !File.Exists(cfgf) && il.Count == 0, "installer options: no file, nothing happens");
        File.WriteAllText(optf, "{\"errorReports\": true, \"autostart\": false}");
        Check(AppConfig.ApplyInstallerOptions(io, ilog), "installer options: applied");
        Check(AppConfig.Load(cfgf).ErrorReports && !File.Exists(optf) && il.Count == 1, "installer options: error reports on, file deleted, logged");
        File.WriteAllText(optf, "{\"errorReports\":false,\"autostart\":true}");
        Check(AppConfig.ApplyInstallerOptions(io, ilog) && !AppConfig.Load(cfgf).ErrorReports && !File.Exists(optf), "installer options: switched off again, other settings kept");
        File.WriteAllText(optf, "{\"errorReports\":true,\"lang\":\"en\"}");
        Check(AppConfig.ApplyInstallerOptions(io, ilog) && AppConfig.Load(cfgf).Lang == "en" && AppConfig.Load(cfgf).ErrorReports && !File.Exists(optf), "installer options: language applied");
        File.WriteAllText(optf, "{\"lang\":\"ru\"}");
        Check(AppConfig.ApplyInstallerOptions(io, ilog) && AppConfig.Load(cfgf).Lang == "ru" && AppConfig.Load(cfgf).ErrorReports, "installer options: language alone applied, error reports kept");
        File.WriteAllText(optf, "{\"errorReports\":false,\"lang\":\"xx\"}");
        Check(AppConfig.ApplyInstallerOptions(io, ilog) && AppConfig.Load(cfgf).Lang == "ru" && !AppConfig.Load(cfgf).ErrorReports, "installer options: unknown language ignored");
        File.WriteAllText(optf, "{ not json");
        int before = il.Count;
        Check(!AppConfig.ApplyInstallerOptions(io, ilog) && !File.Exists(optf) && il.Count == before + 1 && !AppConfig.Load(cfgf).ErrorReports, "installer options: malformed file ignored, deleted, logged");
        File.WriteAllText(optf, "{\"autostart\":true}");
        Check(!AppConfig.ApplyInstallerOptions(io, ilog) && !File.Exists(optf), "installer options: no errorReports key, ignored and deleted");
      } finally { try { Directory.Delete(io, true); } catch (Exception) { } }
    }
    Check(AppConfig.FromJson("{\"coach\":[3,0.5]}").CoachX < 0, "coach panel: a place off the window is ignored");
    Check(AppConfig.FromJson("{}").BossHints && !AppConfig.FromJson(new AppConfig { BossHints = false }.ToJson()).BossHints, "boss hints: on by default, off round-trips");
    Check(!AppConfig.FromJson("{}").ShowInCapture && AppConfig.FromJson(new AppConfig { ShowInCapture = true }.ToJson()).ShowInCapture, "show in capture: off by default, on round-trips");
    Check(CaptureAffinity.For(false, false) == 0x11u && CaptureAffinity.For(true, false) == 0u && CaptureAffinity.For(false, true) == 0u && CaptureAffinity.For(true, true) == 0u, "capture affinity: hidden (0x11) unless the setting or the recorder says visible (0)");
    Check(CaptureAffinity.Hidden(true, CaptureAffinity.For(false, false)) && !CaptureAffinity.Hidden(true, CaptureAffinity.For(true, false)) && !CaptureAffinity.Hidden(true, CaptureAffinity.For(false, true)) && !CaptureAffinity.Hidden(false, 0x11u), "capture affinity: shown in captures (setting / recorder) or a failed call = not Excluded, so the screen looks mask our parts");
    var def = AppConfig.FromJson("not json");
    Check(def.Site == SyncClient.DefaultSite && def.Lang == AppConfig.SystemLang() && def.Code == "" && !def.SaveCopy, "broken config -> defaults");
    Eq("", AppConfig.FromJson("{\"code\":\"" + Tok('A') + "\"}").Code, "plain-text code in config is ignored");
    Eq(SyncClient.DefaultSite, AppConfig.FromJson("{\"site\":\"https://realmforge-wor.vercel.app\"}").Site, "old site address migrates to the new one");
    Eq(SyncClient.DefaultSite, AppConfig.FromJson("{\"site\":\"HTTPS://RealmForge-Wor.Vercel.App/\"}").Site, "old site address migrates (case, trailing slash)");
    Eq("https://example.com", AppConfig.FromJson("{\"site\":\"https://example.com\"}").Site, "another site address is kept");
    string tmp = Path.Combine(Path.GetTempPath(), "rf-test-" + Guid.NewGuid().ToString("N"), "config.json");
    cfg.Save(tmp); cfg.Save(tmp);
    Check(AppConfig.Load(tmp).Code == Tok('A'), "config save/load file (overwrite)");
    Directory.Delete(Path.GetDirectoryName(tmp), true);

    Console.WriteLine("Gzip");
    byte[] gz = SyncClient.Gzip(payload);
    Check(gz.Length > 2 && gz[0] == 0x1f && gz[1] == 0x8b, "gzip magic bytes");
    byte[] unz;
    using (var ms = new MemoryStream(gz)) using (var z = new GZipStream(ms, CompressionMode.Decompress)) using (var outMs = new MemoryStream()) {
      z.CopyTo(outMs); unz = outMs.ToArray();
    }
    Check(Convert.ToBase64String(unz) == Convert.ToBase64String(new UTF8Encoding(false).GetBytes(payload)),
      "gzip round trip, UTF-8 without BOM (" + unz.Length + " -> " + gz.Length + " bytes)");

    Console.WriteLine("HTTP against " + mock);
    string site = SyncClient.NormalizeSite(mock, out err);
    string sha = Sha256(payload);
    r = SyncClient.Send(site, Tok('A'), payload);
    Check(r.Status == SyncStatus.Ok, "200: status " + r.Status + " (HTTP " + r.HttpCode + ")");
    Check(r.Heroes == ex.Heroes && r.Items == ex.Items && r.Artifacts == ex.Artifacts, "200: server counted " + r.Heroes + "/" + r.Items + "/" + r.Artifacts);
    Check(r.SnapshotId != null && r.SnapshotId.StartsWith("snap_"), "200: snapshotId " + r.SnapshotId);
    Check(r.ViewUrl != null && r.ViewUrl.StartsWith(site + "/app/"), "200: viewUrl " + r.ViewUrl);
    var lastReq = (Dictionary<string, object>)MiniJson.Parse(new WebClient().DownloadString(site + "/__last"));
    var h = (Dictionary<string, object>)lastReq["headers"];
    Eq(sha, (string)lastReq["sha256"], "server decoded exactly the same JSON (sha256)");
    Eq("Bearer " + Tok('A'), (string)h["authorization"], "Authorization header");
    Eq("application/json", (string)h["contentType"], "Content-Type header");
    Eq("gzip", (string)h["contentEncoding"], "Content-Encoding header");
    Eq(RFX.ExtractorVersion, (string)h["extractor"], "X-RF-Extractor header");
    Eq("Wardsage/" + RFX.ExtractorVersion, (string)h["userAgent"], "User-Agent header");
    Eq(gz.Length.ToString(), (string)h["contentLength"], "Content-Length = gzip size");

    r = SyncClient.Send(site, "rf_" + new string('Z', 32), payload);
    Check(r.Status == SyncStatus.InvalidToken && r.HttpCode == 401, "401 invalid token");
    r = SyncClient.Send(site, Tok('R'), payload);
    Check(r.Status == SyncStatus.RateLimited && r.RetryAfterSeconds == 120, "429 rate limited, retry after " + r.RetryAfterSeconds);
    r = SyncClient.Send(site, Tok('S'), payload);
    Check(r.Status == SyncStatus.ServerError && r.HttpCode == 500, "500 server error");
    r = SyncClient.Send(site, Tok('L'), payload);
    Check(r.Status == SyncStatus.TooLarge, "413 too large");
    r = SyncClient.Send(site, Tok('M'), payload);
    Check(r.Status == SyncStatus.UnsupportedMedia, "415 unsupported");
    r = SyncClient.Send(site, Tok('P'), payload);
    Check(r.Status == SyncStatus.InvalidPayload && r.Details != null && r.Details.EndsWith("..."), "422 details: " + r.Details);
    r = SyncClient.Send(site, Tok('A'), "{\"heroes\":{},\"equipment\":[]}");
    Check(r.Status == SyncStatus.InvalidPayload && r.Details.Contains("heroes"), "422 from payload validation: " + r.Details);
    r = SyncClient.Send(site, Tok('D'), payload);
    Check(r.Status == SyncStatus.Redirect && r.Details == "https://realmforge.example/api/sync", "308 not followed, reported");
    r = SyncClient.Send(site, Tok('H'), payload);
    Check(r.Status == SyncStatus.Unexpected && r.HttpCode == 200, "200 HTML -> unexpected");
    r = SyncClient.Send(site + "/nope", Tok('A'), payload);
    Check(r.Status == SyncStatus.Unexpected && r.HttpCode == 404, "404 -> unexpected");
    int saved = SyncClient.TimeoutMs; SyncClient.TimeoutMs = 1500;
    r = SyncClient.Send(site, Tok('T'), payload);
    SyncClient.TimeoutMs = saved;
    Check(r.Status == SyncStatus.Timeout, "timeout (" + r.Status + ": " + r.Details + ")");
    r = SyncClient.Send("http://127.0.0.1:1", Tok('A'), payload);
    Check(r.Status == SyncStatus.Unreachable, "connection refused -> unreachable (" + r.Details + ")");
    r = SyncClient.Send("https://realmforge-nonexistent.invalid", Tok('A'), payload);
    Check(r.Status == SyncStatus.Unreachable || r.Status == SyncStatus.Timeout, "unknown host -> unreachable (" + r.Details + ")");

    Console.WriteLine("Equip plans: reply parsing");
    string plansJson = "{\"ok\":true,\"plans\":[{\"id\":\"p1\",\"heroUid\":214700000,\"heroName\":\"Сунь Укун\",\"createdAt\":\"2026-09-25T10:00:00Z\"," +
      "\"items\":[{\"slot\":4,\"uid\":33,\"slotName\":\"Кольцо\",\"name\":\"Кольцо\",\"setName\":null,\"level\":0,\"stars\":5,\"mainStat\":\"\",\"fromHeroUid\":0,\"fromHeroName\":null}," +
      "{\"slot\":2,\"uid\":6423,\"slotName\":\"Браслет\",\"name\":\"Браслет «Проклятие»\",\"setName\":\"Проклятие\",\"setId\":720100,\"level\":16,\"stars\":6,\"mainStat\":\"Крит. УРН 50%\",\"fromHeroUid\":200,\"fromHeroName\":\"Байек\"," +
      "\"icon\":\"Item_720104\",\"subs\":[{\"text\":\"АТК +35\",\"rolls\":2},{\"rolls\":1}],\"setBonus\":[{\"pieces\":2,\"text\":\"Крит. УРН +20%\"}],\"cur\":{\"uid\":44,\"name\":\"Старый\",\"level\":8,\"stars\":4,\"icon\":\"../x\"}}," +
      "{\"slot\":9,\"uid\":1}]}, {\"id\":\"\",\"heroUid\":1,\"items\":[{\"slot\":0,\"uid\":1}]}]}";
    var pr = PlansClient.Interpret(200, plansJson, true);
    Check(pr.Status == PlansStatus.Ok, "plans ok");
    Eq(1, pr.Plans.Count, "plan without id dropped");
    var plan = pr.Plans[0];
    Eq(2, plan.Items.Count, "bad slot dropped");
    Check(plan.Items[0].SetId == 720100 && plan.Items[1].SetId == 0, "setId for the game's filter (absent in older plans)");
    Eq(2, plan.Items[0].Slot, "items sorted by slot");
    Eq(6423L, plan.Items[0].Uid, "uid");
    Eq("Байек", plan.Items[0].FromHeroName, "from hero");
    Eq("Item_720104", plan.Items[0].Icon, "icon");
    Check(plan.Items[0].Subs.Count == 1 && plan.Items[0].Subs[0].Text == "АТК +35" && plan.Items[0].Subs[0].Rolls == 2, "substats");
    Check(plan.Items[0].SetBonus.Count == 1 && plan.Items[0].SetBonus[0].Rolls == 2, "set bonus");
    Check(plan.Items[0].Cur != null && plan.Items[0].Cur.Uid == 44 && plan.Items[0].Cur.Level == 8, "current item");
    Eq("", plan.Items[0].Cur.Icon, "unsafe icon name dropped");
    Check(plan.Items[1].Cur == null && plan.Items[1].Icon == "" && !plan.Items[1].CurKnown, "old plan: no icon, current item unknown");
    Check(plan.Items[0].CurKnown, "current item known");
    Eq(214700000L, plan.HeroUid, "hero uid");
    Check(PlansClient.Interpret(401, "{\"ok\":false,\"error\":\"invalid_token\"}", true).Status == PlansStatus.InvalidToken, "401");
    Check(PlansClient.Interpret(404, "{}", false).Status == PlansStatus.NotFound, "404");
    Check(PlansClient.Interpret(200, "{\"ok\":false}", true).Status == PlansStatus.Unexpected, "ok:false");
    Check(PlansClient.Interpret(502, "<html>", true).Status == PlansStatus.ServerError, "502");
    // NetBackoff: exponential 5 s .. 5 min, +-20% jitter, Retry-After first, reset on success
    Eq(5000, NetBackoff.DelayMs(1, -1, 0.5), "backoff: first failure 5 s");
    Eq(10000, NetBackoff.DelayMs(2, -1, 0.5), "backoff: second 10 s");
    Eq(300000, NetBackoff.DelayMs(20, -1, 0.5), "backoff: capped at 5 min");
    Eq(3000, NetBackoff.DelayMs(1, -1, 0.0), "backoff: jitter low bound -40%");
    Eq(7000, NetBackoff.DelayMs(1, -1, 1.0), "backoff: jitter high bound +40%");
    Check(NetBackoff.DelayMs(30, -1, 1.0) <= 420000 && NetBackoff.DelayMs(30, -1, 0.0) >= 180000, "backoff: jitter around the cap");
    Eq(5000, NetBackoff.DelayMs(0, -1, 0.5), "backoff: failure 0 treated as the first");
    Eq(42000, NetBackoff.DelayMs(5, 42, 0.0), "backoff: Retry-After wins, no jitter");
    Eq(3600000, NetBackoff.DelayMs(1, 999999, 0.5), "backoff: Retry-After clamped to 1 h");
    Eq(1000, NetBackoff.DelayMs(3, 0, 0.5), "backoff: Retry-After 0 -> 1 s");
    Eq(30, NetBackoff.ParseRetryAfter("30", DateTime.UtcNow), "Retry-After seconds");
    Eq(-1, NetBackoff.ParseRetryAfter("soon", DateTime.UtcNow), "Retry-After junk");
    Eq(-1, NetBackoff.ParseRetryAfter(null, DateTime.UtcNow), "Retry-After absent");
    Eq(90, NetBackoff.ParseRetryAfter("Wed, 21 Oct 2015 07:29:30 GMT", new DateTime(2015, 10, 21, 7, 28, 0, DateTimeKind.Utc)), "Retry-After HTTP date");
    Check(NetBackoff.IsFailure(0) && NetBackoff.IsFailure(429) && NetBackoff.IsFailure(503) && NetBackoff.IsFailure(500) && !NetBackoff.IsFailure(200) && !NetBackoff.IsFailure(401) && !NetBackoff.IsFailure(404), "backoff: 429 / 5xx / no answer are failures");
    {
      var bo = new NetBackoff(); var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
      Check(!bo.Blocked(t0), "backoff: not blocked at the start");
      Check(bo.Note(503, -1, t0, 0.5) && bo.Blocked(t0.AddSeconds(4)) && !bo.Blocked(t0.AddSeconds(5)), "backoff: blocked 5 s after the first failure");
      bo.Note(0, -1, t0, 0.5); Check(bo.Failures == 2 && bo.Blocked(t0.AddSeconds(9)) && !bo.Blocked(t0.AddSeconds(10)), "backoff: 10 s after the second");
      bo.Note(429, 120, t0, 0.5); Check(bo.Blocked(t0.AddSeconds(119)) && !bo.Blocked(t0.AddSeconds(120)), "backoff: Retry-After 120 s");
      Check(!bo.Note(200, -1, t0, 0.5) && bo.Failures == 0 && !bo.Blocked(t0), "backoff: success resets");
    }
    // maintenance in NetBackoff and PlansWait (no network)
    { int ra;
      NetBackoff.Reset(); NetBackoff.Record(503, "120", "1"); Check(NetBackoff.LastMaintenance && NetBackoff.LastRetryAfterSec == 120 && NetBackoff.LastHttp == 503, "backoff: X-Wardsage-Maintenance: 1 and Retry-After seconds are recorded");
      NetBackoff.Reset(); NetBackoff.Record(503, "30", "0"); Check(!NetBackoff.LastMaintenance, "backoff: header 0 is no maintenance");
      NetBackoff.Reset(); NetBackoff.Record(200, null, null); Check(!NetBackoff.LastMaintenance && NetBackoff.LastRetryAfterSec == -1, "backoff: no header, no Retry-After");
      NetBackoff.Record(503, "Wed, 21 Oct 2099 07:28:00 GMT", "1"); ra = NetBackoff.LastRetryAfterSec;
      Check(ra > 3600 && NetBackoff.DelayMs(1, ra, 0.5, false) == 3600000, "backoff: a Retry-After date far ahead is clamped to 1 h");
      NetBackoff.Reset(); NetBackoff.Record(503, "-5", "1"); Check(NetBackoff.LastRetryAfterSec == -1, "backoff: negative Retry-After is ignored");
      Eq(42000, NetBackoff.DelayMs(2, 42, 0.9, false), "backoff: no spread outside the reopening");
      Eq(42000, NetBackoff.DelayMs(2, 42, 0.0, true), "reopening: spread low bound is the Retry-After itself");
      Eq(63000, NetBackoff.DelayMs(2, 42, 1.0, true), "reopening: spread high bound is +50%");
      Eq(52500, NetBackoff.DelayMs(2, 42, 0.5, true), "reopening: spread in the middle");
      Eq(5000, NetBackoff.DelayMs(1, -1, 0.5, true), "reopening: no Retry-After, the ordinary backoff");
      NetBackoff.Reopening = true; Eq(63000, NetBackoff.DelayMs(2, 42, 1.0), "reopening flag used by the short form"); NetBackoff.Reopening = false; Eq(42000, NetBackoff.DelayMs(2, 42, 1.0), "flag off: no spread");
      var bo2 = new NetBackoff(); var tt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
      bo2.Note(503, 600, tt, 0.5); Check(bo2.Blocked(tt.AddSeconds(599)) && !bo2.Blocked(tt.AddSeconds(601)), "backoff: a maintenance 503 with Retry-After 600 blocks 600 s");
      // PlansWait.Interpret in maintenance
      var mw = PlansWait.Interpret(503, "{\"error\":\"maintenance\",\"maintenance\":true,\"until\":\"2026-11-10T03:00:00Z\"}", false, 90);
      Check(mw.Kind == WaitKind.Maintenance && mw.Until == new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc) && mw.RetryAfterSec == 90, "wait: 503 body maintenance with until");
      var mh = PlansWait.Interpret(503, "<html>works</html>", true, -1);
      Check(mh.Kind == WaitKind.Maintenance && mh.Until == DateTime.MinValue, "wait: the header alone is maintenance");
      Check(PlansWait.Interpret(503, "<html>", false, -1).Kind == WaitKind.Error && PlansWait.Interpret(503, null, false, 30).Kind == WaitKind.Error, "wait: a plain 503 is an ordinary error");
      Check(PlansWait.Interpret(200, "{\"changed\":true}", true, -1).Kind == WaitKind.Ok, "wait: a 200 is a reply even with the header");
      var now1 = new DateTime(2026, 11, 10, 1, 0, 0, DateTimeKind.Utc);
      int ms1 = PlansWait.MaintenanceWaitMs(mw, now1, 20, 5);
      Check(ms1 >= 2 * 3600 * 1000 && ms1 <= 2 * 3600 * 1000 + 20 * 60 * 1000, "wait: sleeps until the end + the spread, not a loop (" + ms1 / 1000 + " s)");
      Eq(120000, PlansWait.MaintenanceWaitMs(new WaitReply { Kind = WaitKind.Maintenance, RetryAfterSec = 120 }, now1, 20, 5), "wait: Retry-After when no end time");
      Eq(300000, PlansWait.MaintenanceWaitMs(new WaitReply { Kind = WaitKind.Maintenance }, now1, 20, 5), "wait: 5 minutes when nothing is known");
      Eq(21600000, PlansWait.MaintenanceWaitMs(new WaitReply { Kind = WaitKind.Maintenance, Until = now1.AddDays(3) }, now1, 20, 5), "wait: never more than 6 h");
      // a busy site (not maintenance): short, jittered, bounded pauses
      Eq(20000, PlansWait.WaitMs(1, 30, 0.5), "wait: Retry-After 30 s is cut to 20 s outside maintenance");
      Eq(20000, PlansWait.WaitMs(1, 99999, 0.5), "wait: a huge Retry-After is cut to 20 s");
      Eq(8000, PlansWait.WaitMs(1, 8, 0.0), "wait: Retry-After 8 s, no spread at the low end");
      Check(PlansWait.WaitMs(1, 8, 1.0) == 10400, "wait: Retry-After spread is at most +30%");
      Eq(2000, PlansWait.WaitMs(3, 0, 0.0), "wait: Retry-After 0 is raised to the 2 s minimum");
      Eq(20000, NetBackoff.DelayMs(1, 3600, 0.0, false, false), "backoff: busy site, Retry-After 1 h is cut to 20 s");
      Eq(3600000, NetBackoff.DelayMs(1, 3600, 0.0, false, true), "backoff: maintenance keeps the long Retry-After");
      Eq(2000, NetBackoff.DelayMs(1, 0, 0.0, false, false), "backoff: busy site, Retry-After 0 is 2 s");
      { var bo3 = new NetBackoff(); var t3 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        bo3.Note(503, 3600, t3, 0.0, false); Check(bo3.Blocked(t3.AddSeconds(19)) && !bo3.Blocked(t3.AddSeconds(21)), "backoff: a busy 503 with Retry-After 1 h blocks 20 s only");
        var bo4 = new NetBackoff(); bo4.Note(503, 3600, t3, 0.0, true); Check(bo4.Blocked(t3.AddSeconds(3599)), "backoff: a maintenance 503 keeps the 1 h Retry-After"); }
      // growth, bounds and jitter of the pause (rnd 0 .. 1)
      { int prevMax = 0; bool grows = true, inRange = true;
        for (int n = 1; n <= 12; n++) {
          int lo = PlansWait.WaitMs(n, -1, 0.0), hi = PlansWait.WaitMs(n, -1, 1.0), mid = PlansWait.WaitMs(n, -1, 0.5);
          if (lo < 1500 || hi > 20000 || lo > mid || mid > hi) inRange = false;
          if (hi < prevMax) grows = false; prevMax = hi;
        }
        Check(grows && inRange, "wait: pauses grow, stay within 1.5 s .. 20 s, jitter is inside the bounds");
        Check(PlansWait.WaitMs(1, -1, 0.5) == 3000 && PlansWait.WaitMs(2, -1, 0.5) == 6000 && PlansWait.WaitMs(3, -1, 0.5) == 12000 && PlansWait.WaitMs(4, -1, 0.5) == 15000 && PlansWait.WaitMs(30, -1, 0.5) == 15000, "wait: 3, 6, 12, 15 s");
        Eq(1800, PlansWait.WaitMs(1, -1, 0.0), "wait: first pause -40% = 1.8 s");
        Eq(4200, PlansWait.WaitMs(1, -1, 1.0), "wait: first pause +40% = 4.2 s");
        Eq(20000, PlansWait.WaitMs(4, -1, 1.0), "wait: the top is cut at 20 s");
        Eq(6000, NetBackoff.Spread(10000, 20000, 0.0), "spread: 10 s -40%"); Eq(14000, NetBackoff.Spread(10000, 20000, 1.0), "spread: 10 s +40%");
        Eq(1500, NetBackoff.Spread(1000, 20000, 0.0), "spread: never under 1.5 s"); }
    }
    Check(NetBackoff.IdleThrottled(false, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 0, 4, 0, DateTimeKind.Utc))
      && !NetBackoff.IdleThrottled(false, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc))
      && !NetBackoff.IdleThrottled(true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc))
      && !NetBackoff.IdleThrottled(false, DateTime.MinValue, DateTime.UtcNow), "idle polls: at most once per 5 min while the game is not running");
    // arena snapshot dedup: same hash only as a pulse every 10 min
    {
      var a0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
      Check(ArenaClient.ShouldSend("h1", null, DateTime.MinValue, a0), "arena: first snapshot is sent");
      Check(!ArenaClient.ShouldSend("h1", "h1", a0, a0.AddSeconds(599)), "arena: same hash within 10 min is not sent");
      Check(ArenaClient.ShouldSend("h1", "h1", a0, a0.AddSeconds(600)), "arena: pulse after 10 min");
      Check(ArenaClient.ShouldSend("h2", "h1", a0, a0.AddSeconds(1)), "arena: changed hash is sent at once");
    }
    Check(PlansClient.RetryableCancel(PlansStatus.Unreachable) && PlansClient.RetryableCancel(PlansStatus.ServerError) && !PlansClient.RetryableCancel(PlansStatus.Ok) && !PlansClient.RetryableCancel(PlansStatus.NotFound) && !PlansClient.RetryableCancel(PlansStatus.InvalidToken), "a cancel to the site is repeated only when the site was not reached / was busy");
    Check(PlansClient.CancelRetryMs == 10000 && PlansClient.CancelRetryTotalMs == 300000, "cancel retries: every 10 s for 5 min");
    Check(PlansClient.ShouldRetryFinish(PlansStatus.Unreachable, 0) && PlansClient.ShouldRetryFinish(PlansStatus.ServerError, 100000) && !PlansClient.ShouldRetryFinish(PlansStatus.Ok, 0) && !PlansClient.ShouldRetryFinish(PlansStatus.NotFound, 0) && !PlansClient.ShouldRetryFinish(PlansStatus.Unreachable, 295000), "done / cancelled reports are repeated while the site is not reached, within 5 min");
    Check(PlansClient.ShouldRetryGet("GET", true, false) && !PlansClient.ShouldRetryGet("GET", true, true) && !PlansClient.ShouldRetryGet("GET", false, false) && !PlansClient.ShouldRetryGet("POST", true, false), "a closed keep-alive connection: GET once more on a fresh one, a POST never");

    // the long poll for new work (src/PlansWait.cs)
    var w1 = PlansWait.Interpret(200, "{\"changed\":true,\"at\":\"2026-10-17T12:00:01.250Z\"}");
    Check(w1.Kind == WaitKind.Ok && w1.Changed && w1.At == "2026-10-17T12:00:01.250Z", "wait: changed + at");
    var w2 = PlansWait.Interpret(200, "{\"changed\":false,\"at\":\"not a date\"}");
    Check(w2.Kind == WaitKind.Ok && !w2.Changed && w2.At == "", "wait: unchanged, bad at dropped");
    Check(PlansWait.Interpret(200, "<html>").Kind == WaitKind.Error && PlansWait.Interpret(200, "").Kind == WaitKind.Error, "wait: junk body");
    Check(PlansWait.Interpret(401, "{}").Kind == WaitKind.Unauthorized && PlansWait.Interpret(429, "{}").Kind == WaitKind.TooMany, "wait: 401 / 429");
    Check(PlansWait.Interpret(404, "{}").Kind == WaitKind.NotDeployed && PlansWait.Interpret(502, "x").Kind == WaitKind.Error, "wait: 404 not deployed, 502 error");
    Check(PollGate.ShouldPoll(false, 0, 0, 0) && PollGate.ShouldPoll(false, 0, 999999, 0) && PollGate.ShouldPoll(false, 0, 0, 77), "poll gate: no game, polls");
    Check(!PollGate.ShouldPoll(true, 0, 0, 0) && !PollGate.ShouldPoll(true, 0, 59999, 0), "poll gate: game up, no player, no history, waits");
    Check(PollGate.ShouldPoll(true, 0, 0, 77) && PollGate.ShouldPoll(true, 0, 1000, 77), "poll gate: the account last read here, polls at once");
    Check(!PollGate.ShouldPoll(true, 0, 60000, 0) && !PollGate.ShouldPoll(true, 0, 299999, 0) && PollGate.ShouldPoll(true, 0, 300000, 0), "poll gate: no account ever read here, the default account only after 5 min");
    Check(PollGate.ShouldPoll(true, 123, 0, 0) && PollGate.ShouldPoll(true, 123, 0, 77), "poll gate: player known polls at once");
    // the account the plan / sell-list requests name: the one read now, else the one last read on this install, never a guess
    Check(SyncClient.PlanPlayer(123, 77) == 123 && SyncClient.PlanPlayer(0, 77) == 77 && SyncClient.PlanPlayer(0, 0) == 0 && SyncClient.PlanPlayer(-1, 0) == 0, "plan player: read, else last read, else none");
    // the lists are asked again once the game account read differs from the one they were asked for
    SyncClient.EnableTls12();
    Check(System.Net.ServicePointManager.DefaultConnectionLimit >= 10, "connections: limit raised to 10 per host");
    { int hh; string tx; Check(!PlansWait.Request("http://127.0.0.1:1", "x", "", null, out hh, out tx) && !PlansWait.LastClosed, "plans wait: refused connection is not a closed keep-alive"); }
    Check(!PollGate.Reload(-1, 123), "reload: not asked yet");
    Check(PollGate.Reload(0, 123) && PollGate.Reload(77, 123), "reload: asked for the default / another account");
    Check(!PollGate.Reload(123, 123) && !PollGate.Reload(77, 0), "reload: same account, or none read");
    {   // the account last read on this install survives a restart (as text: a uid never loses digits)
      var pc = new AppConfig(); pc.LastPlayer = 9007199254740993L;
      Check(AppConfig.FromJson(pc.ToJson()).LastPlayer == 9007199254740993L, "config: lastPlayer round trip");
      Check(AppConfig.FromJson("{}").LastPlayer == 0 && AppConfig.FromJson("{\"lastPlayer\":\"-5\"}").LastPlayer == 0 && AppConfig.FromJson("{\"lastPlayer\":12}").LastPlayer == 0, "config: lastPlayer missing / bad");
      Check(new AppConfig().ToJson().IndexOf("lastPlayer") < 0, "config: no lastPlayer before one is read");
    }
    Eq(0, PlansWait.Backoff(0), "backoff: none yet");
    Check(PlansWait.Backoff(1) == 3000 && PlansWait.Backoff(2) == 6000 && PlansWait.Backoff(3) == 12000 && PlansWait.Backoff(5) == 15000, "backoff: 3 s, 6 s, 12 s ...");
    Check(PlansWait.Backoff(6) == 15000 && PlansWait.Backoff(50) == 15000, "backoff: capped at 15 s");
    Eq("2026-10-17T12:00:01.250Z", PlansWait.Since("2026-10-17T12:00:01.250Z", DateTime.UtcNow), "since: the server's last at");
    Eq("2026-10-17T11:59:00.000Z", PlansWait.Since("", new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc)), "since: now - 60 s at first");

    Console.WriteLine("Equip guide");
    var lv = new EquipLive();
    Check(EquipGuide.Next(plan, lv).Kind == GuideKind.OpenHero, "other hero -> open hero");
    lv.HeroUid = plan.HeroUid;
    Check(EquipGuide.Next(plan, lv).Kind == GuideKind.OpenSlot, "no slot -> open slot");
    lv.PanelOk = true; lv.Part = 4;
    Check(EquipGuide.Next(plan, lv).Kind == GuideKind.OpenSlot, "wrong slot -> open slot");
    lv.Part = 2; lv.HideEquipped = true;
    var gs = EquipGuide.Next(plan, lv);
    Check(gs.Kind == GuideKind.HiddenEquipped && gs.OtherHero == "Байек", "on another hero + hide equipped");
    lv.HideEquipped = false; lv.FilterActive = true;
    Check(EquipGuide.Next(plan, lv).Kind == GuideKind.HiddenFilter, "filter hides it");
    lv.FilterActive = false; lv.HideEnhanced = true;
    Check(EquipGuide.Next(plan, lv).Kind == GuideKind.HiddenEnhanced, "enhanced hidden");
    lv.HideEnhanced = false;
    Check(EquipGuide.Next(plan, lv).Kind == GuideKind.NotInList, "not in list");
    lv.Row[6423] = 7; lv.Col[6423] = 3;
    gs = EquipGuide.Next(plan, lv);
    Check(gs.Kind == GuideKind.Pick && gs.Row == 7 && gs.Col == 3 && gs.Item.Uid == 6423, "pick row/col");
    Check(gs.States[6423] == ItemState.Current && gs.States[33] == ItemState.Waiting, "states");
    lv.Owner[6423] = plan.HeroUid;
    gs = EquipGuide.Next(plan, lv);
    Check(gs.Kind == GuideKind.OpenSlot && gs.Item.Uid == 33 && gs.DoneCount == 1, "first done -> next slot");
    lv.Owner[33] = plan.HeroUid;
    Check(EquipGuide.Next(plan, lv).Kind == GuideKind.Done, "all on -> done");
    lv.GameRunning = false;
    Check(EquipGuide.Next(plan, lv).Kind == GuideKind.GameClosed || EquipGuide.Next(plan, lv).Kind == GuideKind.Done, "closed game");
    var two = new List<Plan> { new Plan { Id = "a", HeroUid = 1 }, plan };
    var lv2 = new EquipLive(); lv2.HeroUid = plan.HeroUid;
    Eq(1, EquipGuide.PickPlan(two, lv2, 0), "plan follows the open hero");
    lv2.HeroUid = 5;
    Eq(0, EquipGuide.PickPlan(two, lv2, 0), "keeps the choice otherwise");
    Eq(-1, EquipGuide.PickPlan(new List<Plan>(), lv2, 0), "no plans");

    Console.WriteLine("Highlight: list position on the screen");
    Check(ListTracker.IsBar(Argb(74, 90, 124)) && ListTracker.IsBar(Argb(90, 108, 140)), "stat bar colour");
    Check(!ListTracker.IsBar(Argb(150, 40, 50)) && !ListTracker.IsBar(Argb(120, 70, 170)) && !ListTracker.IsBar(Argb(50, 90, 170))
      && !ListTracker.IsBar(Argb(100, 100, 106)) && !ListTracker.IsBar(Argb(20, 30, 52)) && !ListTracker.IsBar(Argb(235, 235, 240)), "rarity / background colours are not bars");
    var geo = new ListGeometry();
    double HH = 1058;   // client height
    foreach (double scroll in new[] { 0.0, 37.3, 120.0, 1234.5 }) {
      int fw, fh; int[] img = FakeList(geo, HH, scroll, out fw, out fh);
      var xs = new int[6];
      for (int c = 1; c <= 3; c++) { double l = (geo.ColLeft(c) - geo.StripLeft) * HH; xs[(c - 1) * 2] = (int)(l + 12); xs[(c - 1) * 2 + 1] = (int)(l + geo.CellW * HH - 12); }
      double ph, cf;
      bool found = ListTracker.FindPhase(ListTracker.BarProfile(img, fw, fh, xs), geo.PitchY * HH, geo.BarTop * HH, geo.BarBottom * HH, out ph, out cf);
      // row 1 top in strip px = pad - scroll; its phase:
      double pitchPx = geo.PitchY * HH, want = ((6 - scroll) % pitchPx + pitchPx) % pitchPx;
      double perr = Math.Abs(ph - want); perr = Math.Min(perr, pitchPx - perr);
      Check(found && perr < 1.5, "phase at scroll " + scroll + " (found " + ph.ToString("0.0") + ", want " + want.ToString("0.0") + ", conf " + cf.ToString("0.00") + ")");
    }
    int[] noise = new int[300 * 400]; var rnd = new Random(7); for (int i = 0; i < noise.Length; i++) noise[i] = Argb(rnd.Next(256), rnd.Next(60), rnd.Next(60));
    double nph, ncf;
    Check(!ListTracker.FindPhase(ListTracker.BarProfile(noise, 300, 400, new[] { 0, 300 }), geo.PitchY * HH, geo.BarTop * HH, geo.BarBottom * HH, out nph, out ncf), "no list on the screen -> no phase");
    Eq(100.0, ListTracker.Snap(95, 100 % 166.2, 166.2), "snap forward");
    Eq(-66.2, Math.Round(ListTracker.Snap(-10, 100, 166.2), 1), "snap backward over the pitch");
    var types = new[] { 0, 2, 1, 1, 1, 2, 1, 1 };
    Eq(Math.Round(geo.PitchY * (0.44 + 3 + 0.44), 6), Math.Round(geo.RowOffset(types, 6), 6), "row offset with section titles");
    var an = new ScrollAnchor();
    // row 6 top really at 400 px; the click was 30 px below its top
    an.FromClick(geo, types, 6, 430, HH, 1);
    double realRow1 = 400 - geo.RowOffset(types, 6) * HH;
    Check(Math.Abs(an.Row1Top - realRow1) < geo.PitchY * HH / 2, "click gives the row within half a pitch");
    an.Track(geo, types, 6, 400 - 3 * geo.PitchY * HH, HH);   // phase seen on the screen: some other row top
    Check(Math.Abs(an.Row1Top - realRow1) < 0.001, "bar phase snaps the anchor exactly");
    an.Track(geo, types, 6, 400 - 40 + 5 * geo.PitchY * HH, HH);   // the list scrolled down by 40 px
    Check(Math.Abs(an.Row1Top - (realRow1 - 40)) < 0.001, "scrolling is followed");
    var at = new ScrollAnchor();
    double top1 = ScrollAnchor.TopRow1(geo, HH);
    Check(at.FromTop(geo, top1 + 3 + 2 * geo.PitchY * HH, HH, 1) && Math.Abs(at.Row1Top - (top1 + 3)) < 0.001, "fresh list at the top: anchored without a click");
    var at2 = new ScrollAnchor();
    Check(!at2.FromTop(geo, top1 + geo.PitchY * HH * 0.4, HH, 1) && !at2.Has, "scrolled list: no guess, waits for a click");
    var at3 = new ScrollAnchor(); int[] titled = { 0, 2, 1, 2, 3, 2, 1 };   // «Полное соотв.», items, «Частичное», empty, «Несовпадение», items
    double firstItem = top1 + geo.PitchY * ListGeometry.TitleRow * HH;
    Check(at3.FromTop(geo, titled, firstItem + 2 + geo.PitchY * HH, HH, 1) && Math.Abs(at3.Row1Top - (top1 + 2)) < 0.001,
          "list opening with a section title (sub stats filter): anchored by the first item row");
    Eq(3, geo.ColumnAt(geo.ColLeft(3) + 0.05), "column under the cursor");
    Eq(0, geo.ColumnAt(geo.ColLeft(1) - 0.03), "left of the list");

    Console.WriteLine("Highlight: following the list by its picture");
    {
      double pitch = geo.PitchY * HH;
      Func<int, bool, float[]> pic = (scroll, alike) => {
        int fw, fh; int[] img = FakeRows(geo, HH, scroll, alike, out fw, out fh);
        var sxs = new int[6];
        for (int c = 1; c <= 3; c++) { double l = (geo.ColLeft(c) - geo.StripLeft) * HH; sxs[(c - 1) * 2] = (int)(l + geo.CellW * HH * 0.12); sxs[(c - 1) * 2 + 1] = (int)(l + geo.CellW * HH * 0.88); }
        return ListTracker.LineSig(img, fw, fh, sxs, 4);
      };
      double sh, sd, ss;
      Check(ListTracker.Register(pic(100, false), pic(100, false), 12, pitch, out sh, out sd, out ss) && sh == 0, "still list: no move");
      Check(ListTracker.Register(pic(100, false), pic(220, false), 12, pitch, out sh, out sd, out ss) && Math.Abs(sh + 120) <= 0.5,
            "list scrolled 120 px (over half a row: the bar phase alone takes it for 46 px the other way): found " + sh + " (" + sd.ToString("0.0") + "/" + ss.ToString("0.0") + ")");
      Check(ListTracker.Register(pic(400, false), pic(68, false), 12, pitch, out sh, out sd, out ss) && Math.Abs(sh - 332) <= 0.5, "list scrolled up two rows: found " + sh);
      Check(!ListTracker.Register(pic(100, true), pic(220, true), 12, pitch, out sh, out sd, out ss), "rows of alike items: the move is not clear");
      Check(!ListTracker.Register(pic(100, false), pic(2100, false), 12, pitch, out sh, out sd, out ss), "moved further than the strip: not clear");
      Check(!ListTracker.Register(pic(100, false), new float[12], 12, pitch, out sh, out sd, out ss), "another picture size: not clear");

      var fa = new ScrollAnchor { Has = true, Row1Top = top1 }; var fl = new ScrollFollow();
      double topRow1 = top1; long now = 0;
      fl.Anchored(pic(0, false), now);
      Check(fl.Follow(fa, pic(0, false), 12, pitch, now += 30) && fl.Note == "still", "anchored at the top, still");
      fl.Settled(fa, pic(0, false), topRow1, pitch);
      bool ok = true;
      for (int s = 150; s <= 1050; s += 150) ok &= fl.Follow(fa, pic(s, false), 12, pitch, now += 30);
      Check(ok && Math.Abs(fa.Row1Top - (top1 - 1050)) < 0.001, "followed down 150 px a look (row 1 at " + fa.Row1Top.ToString("0.0") + ", want " + (top1 - 1050).ToString("0.0") + ")");
      Check(!fl.Follow(fa, pic(1500, false), 12, pitch, now += 30) && fl.Unsure && fl.Note == "lost" && Math.Abs(fa.Row1Top - (top1 - 1050)) < 0.001,
            "a jump of 450 px while the list went 150 a look: not trusted (" + fl.Note + " " + fl.Shift + " pred " + fl.Pred + ")");
      Check(!fl.Follow(fa, pic(4000, false), 12, pitch, now += 30) && fl.Unsure, "a jump further than the strip: unsure (no frame)");
      now += ScrollFollow.RetryMs + 1;
      Check(!fl.Follow(fa, pic(1500, false), 12, pitch, now) && fl.Unsure && fl.Note == "gone", "after a moment unsure the old picture is not matched again");
      Check(fl.Follow(fa, pic(30, false), 12, pitch, now += 30) && !fl.Unsure && fl.Note == "top" && Math.Abs(fa.Row1Top - (top1 - 30)) < 0.001,
            "back near the top: anchored by the list-top picture (row 1 at " + fa.Row1Top.ToString("0.0") + ")");

      var fb = new ScrollAnchor { Has = true, Row1Top = top1 - 600 }; var fl2 = new ScrollFollow(); now = 0;
      fl2.Anchored(pic(600, true), now);
      Check(fl2.Follow(fb, pic(620, true), 12, pitch, now += 30) && Math.Abs(fb.Row1Top - (top1 - 620)) < 0.6,
            "alike rows, a slow move: the shift the speed predicts (row 1 at " + fb.Row1Top.ToString("0.0") + ")");
      Check(fl2.Follow(fb, pic(645, true), 12, pitch, now += 30) && Math.Abs(fb.Row1Top - (top1 - 645)) < 0.6, "alike rows: followed on");
      Check(!fl2.Follow(fb, pic(800, true), 12, pitch, now += 200) && fl2.Unsure && Math.Abs(fb.Row1Top - (top1 - 645)) < 0.6,
            "alike rows, a look long after the last: the anchor is not moved by a guess");
      fl2.Anchored(null, now); fb.Row1Top = top1 - 800;   // the player clicked an item
      Check(fl2.Follow(fb, pic(800, true), 12, pitch, now += 30) && !fl2.Unsure && fl2.Note == "first", "a click makes it sure again");

      // the ends of a 20-row list: pulled past its end / its top for a moment
      {
        int fw, fh; double cellPx = geo.CellW * HH; double[] tops; double et;
        int x0 = (int)(cellPx * 0.1), x1 = (int)(cellPx * 0.9);
        int sEnd = (int)(20 * pitch - 300);
        int[] img = FakeRows(geo, HH, sEnd, false, out fw, out fh, 20);
        double ph = ((6 - sEnd) % pitch + pitch) % pitch;
        int e = ListTracker.ListEdge(ListTracker.SlotCells(img, fw, fh, x0, x1, ph, pitch, cellPx, out tops), tops, out et);
        Check(e == 1 && Math.Abs(et - (6 - sEnd + 19 * pitch)) < 0.001, "the list's end on the screen: the last row's top (" + e + ", " + et.ToString("0.0") + ")");
        img = FakeRows(geo, HH, -100, false, out fw, out fh, 20);
        ph = (106 % pitch + pitch) % pitch;
        e = ListTracker.ListEdge(ListTracker.SlotCells(img, fw, fh, x0, x1, ph, pitch, cellPx, out tops), tops, out et);
        Check(e == -1 && Math.Abs(et - 106) < 0.001, "the list pulled past its top: row 1's top (" + e + ", " + et.ToString("0.0") + ")");
        // our own plate masked over the last visible row of the list at its top: not an end
        img = FakeRows(geo, HH, 0, false, out fw, out fh, 40);
        for (int y = fh - 140; y < fh; y++) for (int x = 0; x < fw; x++) img[y * fw + x] = ListTracker.Masked;
        ph = ((6 - 0) % pitch + pitch) % pitch;
        Check(ListTracker.ListEdge(ListTracker.SlotCells(img, fw, fh, x0, x1, ph, pitch, cellPx, out tops), tops, out et) == 0, "a row under our own (masked) plate is not the list's end");
        img = FakeRows(geo, HH, 900, false, out fw, out fh, 20);
        ph = ((6 - 900) % pitch + pitch) % pitch;
        Check(ListTracker.ListEdge(ListTracker.SlotCells(img, fw, fh, x0, x1, ph, pitch, cellPx, out tops), tops, out et) == 0, "the middle of the list: no end");
      }
      // the rows from the scroll value: a steady offset, not re-snapped to the wandering phase every look
      {
        var so = new ScrollOffset(); double sp = 110.76, rawTop = 116.5, got;
        got = so.Place(rawTop, 119.5, sp, 705);
        Check(Math.Abs(got - 119.5) < 0.01, "first look: the offset from the bars' phase");
        bool steady = true;
        foreach (double phy in new[] { 119.9, 120.3, 119.1, 120.6, 119.5 }) steady &= Math.Abs(so.Place(rawTop, phy, sp, 705) - 119.5) < 0.01;
        Check(steady, "the phase wandering by a pixel does not move the rows");
        steady = true;
        foreach (double phy in new[] { 129.5, 119.5, 127.0, 121.0, 129.5, 119.5 }) steady &= Math.Abs(so.Place(rawTop, phy, sp, 705) - 119.5) < 0.01;
        Check(steady, "a phase jumping back and forth by 10 px (card glow) does not move the rows");
        for (int n = 0; n < 4; n++) so.Place(rawTop, 124.5, sp, 705);
        Check(Math.Abs(so.Place(rawTop, 124.5, sp, 705) - 124.5) < 0.01, "the same 5 px difference five looks in a row: moved");
        Check(Math.Abs(so.Place(rawTop - 300, double.NaN, sp, 705) - (124.5 - 300)) < 0.01, "scrolling: the rows move with the value");
      }
      // the hints' plate beside its target, kept in the window
      {
        char side;
        // a plate with no target on screen: top centre of the window, 0.12 U down, inside it
        {
          double Ut = Ui.Unit(1366, 705);
          int[] tp = HintLayout.TopPlate(300, 90, 1366, 705, Ut);
          Check(tp[0] == 1366 / 2 - 150 && tp[1] == (int)(0.12 * Ut), "no target: the plate at the top centre (" + tp[0] + "," + tp[1] + ")");
          tp = HintLayout.TopPlate(2000, 90, 1366, 705, Ut);
          Check(tp[0] >= 0 && tp[1] + 90 <= 705, "no target, a wide plate: still inside the window");
        }
        // the Inventory form (Form_BackpackIntegration: equipment panel inside, NO hero-screen grid field) counts as shown while
        // m_LuaLogicActive is set; the hero screen's FormActive must not decide it (live 0.61.193 regression)
        {
          var mi = new FakeLua(1 << 20); RFX.TestMemory = mi;
          try {
            var si = new FakeState(mi);
            var panel = si.T("m_CurrentBottomButtonState", 2L);
            var shown = si.T("m_LuaLogicActive", true, "m_CurrentMainTabId2", -9998L, "m_CachedContentPanels", si.T(-9998L, panel));
            var hidden = si.T("m_CurrentMainTabId2", -9998L, "m_CachedContentPanels", si.T(-9998L, panel));
            Check(RFX.InvFormShown(shown.A), "inventory form with m_LuaLogicActive: shown");
            Check(!RFX.InvFormShown(hidden.A) && !RFX.InvFormShown(0), "inventory form without it, or none: not shown");
          } finally { RFX.TestMemory = null; RFX.ForgetLive(); }
        }
        int[] pl = HintLayout.Plate(new[] { 100, 300, 80, 100 }, 300, 90, 1366, 705, out side);
        Check(side == 'r' && pl[0] == 100 + 80 + HintLayout.Gap && pl[1] == 300 + 50 - 45, "plate right of the item (" + side + " " + pl[0] + "," + pl[1] + ")");
        pl = HintLayout.Plate(new[] { 1200, 300, 80, 100 }, 300, 90, 1366, 705, out side);
        Check(side == 'l' && pl[0] + 300 + HintLayout.Gap == 1200, "no room on the right: left of it");
        pl = HintLayout.Plate(new[] { 500, 400, 400, 60 }, 700, 90, 1366, 705, out side);
        Check(side == 't' && pl[1] + 90 + HintLayout.Gap == 400 && pl[0] >= HintLayout.Margin && pl[0] + 700 <= 1366 - HintLayout.Margin, "no room beside: above, inside the window");
        pl = HintLayout.Plate(new[] { 500, 20, 400, 60 }, 700, 90, 1366, 705, out side);
        Check(side == 'b' && pl[1] == 20 + 60 + HintLayout.Gap, "at the top: below");
        pl = HintLayout.Plate(new[] { 100, 690, 80, 100 }, 300, 90, 1366, 705, out side);
        Check(pl[1] + 90 <= 705 - HintLayout.Margin, "kept inside the window at the bottom");
        pl = HintLayout.Plate(new[] { 670, 870, 200, 60 }, 300, 70, 1920, 1009, out side);
        Check(side == 't' && pl[1] + 70 + HintLayout.Gap == 870, "a wide button: the plate above it, not over its neighbours");
        // the plate never covers its target nor the step's other buttons (TryPlace / Place), on a normal and on a tiny client
        {
          Func<int[], int, int, int[], bool> hits = (q, w, h, r) => q[0] < r[0] + r[2] && r[0] < q[0] + w && q[1] < r[1] + r[3] && r[1] < q[1] + h;
          Func<int[], int, int, int, int, bool> inside = (q, w, h, W, H) => q[0] >= HintLayout.Margin && q[1] >= HintLayout.Margin && q[0] + w <= W - HintLayout.Margin && q[1] + h <= H - HintLayout.Margin;
          // normal client, nothing to avoid: the same place as Plate
          int[] q = HintLayout.TryPlace(new[] { 100, 300, 80, 100 }, null, 300, 90, 1366, 705, out side);
          Check(q != null && side == 'r' && q[0] == 194 && q[1] == 305, "place: right of the item as before");
          // the 526x296 Ascension screen: the item's cell on the left, «Вознести» bottom right; the plate right of the cell would cover the button
          int[] cell = { 120, 200, 40, 40 }, ascend = { 300, 236, 110, 34 }, filter = { 30, 40, 60, 24 };
          q = HintLayout.TryPlace(cell, new int[][] { ascend, filter }, 200, 66, 526, 296, out side);
          Check(q != null && !hits(q, 200, 66, ascend) && !hits(q, 200, 66, cell) && !hits(q, 200, 66, filter) && inside(q, 200, 66, 526, 296), "tiny client: beside the cell, off «Вознести» (" + (q == null ? "none" : side + " " + q[0] + "," + q[1]) + ")");
          // the target is «Вознести» itself at the bottom right corner: above it, inside, not over it
          int[] btn = { 400, 250, 110, 36 };
          q = HintLayout.TryPlace(btn, new int[][] { btn, filter }, 220, 66, 526, 296, out side);
          Check(q != null && side == 't' && !hits(q, 220, 66, btn) && inside(q, 220, 66, 526, 296), "tiny client, target at the bottom right edge: above it (" + (q == null ? "none" : side + " " + q[0] + "," + q[1]) + ")");
          // target at the top left edge: right of it or below, never over it
          int[] tl = { 6, 6, 50, 30 };
          q = HintLayout.TryPlace(tl, null, 220, 66, 526, 296, out side);
          Check(q != null && !hits(q, 220, 66, tl) && inside(q, 220, 66, 526, 296), "target at the top left edge: beside it");
          // the owner's recast case: a wide button at the far right, another click target above it covering the usual centred spot: the label
          // stays next to the frame (above or below, over its span), never slid to the screen's far left
          int[] rb = { 1034, 633, 147, 51 }, upper = { 900, 480, 280, 120 };
          q = HintLayout.Place(rb, new int[][] { rb, upper }, 367, 85, 1190, 705, out side);
          Check(q != null && inside(q, 367, 85, 1190, 705) && !hits(q, 367, 85, rb) && q[0] + 367 > rb[0] && q[0] < rb[0] + rb[2] && (side == 't' || side == 'b'), "frame at the right edge: the label adjacent, inside (" + (q == null ? "none" : side + " " + q[0] + "," + q[1]) + ")");
          q = HintLayout.Place(new[] { 1100, 300, 80, 100 }, null, 367, 85, 1190, 705, out side);
          Check(q != null && inside(q, 367, 85, 1190, 705) && side == 'l' && q[0] + 367 + HintLayout.Gap == 1100, "a tall frame at the right edge: left of it, next to it");
          // a plate too big for any free place: TryPlace says none (the caller shrinks), Place still keeps off the target as far as it can
          int[] mid = { 200, 100, 120, 90 };
          Check(HintLayout.TryPlace(mid, null, 500, 200, 526, 296, out side) == null, "too big for the tiny client: no free place");
          q = HintLayout.Place(mid, null, 500, 200, 526, 296, out side);
          Check(q != null && inside(q, 500, 200, 526, 296), "the least bad place stays inside the window");
          // the smaller plate fits
          Check(HintLayout.TryPlace(mid, null, 180, 60, 526, 296, out side) != null, "a shrunk plate fits");
          // no target: the top plate moves off a button it would cover
          int[] topBtn = { 200, 30, 120, 40 };
          int[] tp2 = HintLayout.TopPlateFree(200, 60, 526, 296, Ui.Unit(526, 296), new int[][] { topBtn });
          Check(!hits(tp2, 200, 60, topBtn) && inside(tp2, 200, 60, 526, 296), "top plate: off the button at the top centre");
        }
        // the plate's steadiness (HintHold): shown at once, a new step or a hide only after it held DebounceMs, the last place kept on failing reads
        {
          var hh = new HintHold();
          int[] a = { 10, 10, 20, 20 }, b = { 300, 200, 20, 20 };
          Check(hh.Update("pick", a, null, 1000) && hh.Key == "pick" && hh.Target == a, "hold: the first hint at once");
          Check(hh.Update(null, null, null, 1050) && hh.Key == "pick", "hold: a hide for one tick does not hide");
          Check(hh.Update("pick", null, null, 1080) && hh.Target == a && hh.Stale, "hold: the same step without a target keeps the last place (plate only)");
          Check(hh.Update("pick", a, null, 1100) && !hh.Stale, "hold: the target back: fresh again");
          hh.Update("ready", b, null, 1200); hh.Update("pick", a, null, 1250); hh.Update("ready", b, null, 1300);
          Check(hh.Key == "pick", "hold: steps flipping back and forth do not switch the hint");
          bool shown = true; string seen = "";
          for (long t = 1300; t <= 1300 + HintHold.DebounceMs + 33; t += 33) { shown &= hh.Update("ready", b, null, t); if (seen.IndexOf(hh.Key) < 0) seen += hh.Key + ";"; }
          Check(shown && hh.Key == "ready" && hh.Target == b && seen == "pick;ready;", "hold: a step that holds is taken over once, never hidden in between (" + seen + ")");
          long t0 = 2000; hh.Update(null, null, null, t0);
          Check(hh.Update(null, null, null, t0 + HintHold.DebounceMs - 1), "hold: still shown before the debounce");
          Check(!hh.Update(null, null, null, t0 + HintHold.DebounceMs) && hh.Key == null, "hold: hidden once the hide held");
          Check(HintHold.DebounceMs >= 300, "hold: the debounce is at least 300 ms");
          Check(HintStepRule.ListFrameOff("replace") && !HintStepRule.ListFrameOff("pick") && !HintStepRule.ListFrameOff("filter") && !HintStepRule.ListFrameOff("slot"), "step rule: only the held «replace» step has no list frame (hover / mouse moves cannot bring the pick plate back)");
          hh.Reset(); hh.Update("x", a, null, 5000); hh.Update("x", null, null, 5000 + HintHold.KeepMs + 1);
          Check(hh.Key == "x" && hh.Target == null, "hold: a target missing for longer than KeepMs: the plate goes to the top");
        }
        // a button's live rect (uibtn) stands for the layout only when fresh and read for this window size
        {
          RFX.UiButtonKeep(ClickTarget.AscendTab, new UiButtonInfo { X = 10, Y = 20, W = 30, H = 40, Visible = true }, 526, 296, 1000);
          var lvr = RFX.UiButtonLive(ClickTarget.AscendTab, 526, 296, 2000);
          Check(lvr != null && lvr[0] == 10 && lvr[3] == 40, "live rect: taken when fresh");
          Check(RFX.UiButtonLive(ClickTarget.AscendTab, 1366, 705, 2000) == null && RFX.UiButtonLive(ClickTarget.FoundryButton, 526, 296, 2000) == null
            && RFX.UiButtonLive(ClickTarget.AscendTab, 526, 296, 1000 + RFX.UiLiveMs + 1) == null, "live rect: not for another size, button or when old");
        }
        Check(Array.IndexOf(LocateFrames.AvoidKeys(new LocateStep { Kind = LocateKind.AscendPick }), "ascend.button") >= 0, "avoid: the Ascension pick keeps the plate off «Вознести»");
        var ptr = HintLayout.Pointer(new[] { 194, 305 }, 300, 90, 'r', new[] { 100, 300, 80, 100 }, 8);
        Check(ptr[4] < 194 && Math.Abs(ptr[5] - 350) < 0.01, "the pointer points left at the item's middle");
        double dot, sb0, sb1;
        HintLayout.Strip(100, 500, 4000, 1000, 3000, out dot, out sb0, out sb1);
        Check(Math.Abs(dot - 400) < 0.01 && Math.Abs(sb0 - 200) < 0.01 && Math.Abs(sb1 - 240) < 0.01, "scroll strip: the dot and the visible part");
        Eq(0, HintLayout.Plural(21, true), "21 ряд"); Eq(1, HintLayout.Plural(3, true), "3 ряда"); Eq(2, HintLayout.Plural(12, true), "12 рядов"); Eq(2, HintLayout.Plural(2, false), "2 rows");
      }
      // the hero list: cards with dark gaps between the rows
      {
        double hp = 187.3, hc = 164; var hprof = new double[700];
        for (int y = 0; y < hprof.Length; y++) { double tt = ((y - 41.0) % hp + hp) % hp; hprof[y] = tt < hc ? 0.1 + (y * 37 % 50) / 500.0 : 0.01; }
        double hph, hcf;
        Check(ListTracker.CardPhase(hprof, hp, hc, out hph, out hcf) && Math.Abs(hph - 41) <= 3, "hero list: the cards' row phase (" + hph + ", " + hcf.ToString("0") + ")");
        Check(!ListTracker.CardPhase(new double[700], hp, hc, out hph, out hcf), "hero list: no cards, no phase");
      }
      // the game's «selected» glow around a cell
      {
        int gw = 120, gh = 400; var gimg = new int[gw * gh];
        for (int i = 0; i < gimg.Length; i++) gimg[i] = Argb(20, 26, 40);
        for (int y = 100; y < 260; y++) for (int x = 13; x < 47; x++) if (x < 18 || x > 41) gimg[y * gw + x] = Argb(150, 110, 60);
        int y0, y1;
        Check(ListTracker.GlowRun(gimg, gw, gh, 20, 40, 90, out y0, out y1) && y0 == 100 && y1 == 259, "selected glow: its lines (" + y0 + "-" + y1 + ")");
        Check(!ListTracker.GlowRun(gimg, gw, gh, 80, 100, 90, out y0, out y1), "no glow at another cell");
      }
    }

    Console.WriteLine("AutoPilot (open the slot, scroll, click the item):");

    Console.WriteLine("FilterPilot (the game's gear filter: set + main stat):");

    Console.WriteLine("HeroPilot (the plan's hero on the hero screen):");
    CityMemTests();
    HeroChoiceTests();
    TipMemTests();
    ListMemTests();
    SimMemoryTests();
    FightPlanTests();
    UpdateDownloadTests();
    RealRangeOpenerTests();
    Backoff409Tests();
    SiteRouteTests();
    SiteRouteNetTests();
    ScreenTopTests();
    ScreenMapTests();
    SellHintTests();
    OwnButtonTests();
    ConfirmChoiceTests();
    GeometryTests();
    GuideShapeTests();
    LocateGemAndListTests();
    Console.WriteLine("Offer pop-ups over the city (OfferForms, OfferClose, the window table, the automatic recording):");
    WindowTableTests();
    HintFallbackTests();
    ArtGridPictureTests();

    Console.WriteLine("Fight timeline (src/TimelineBuilder.cs: samples of the running fight + its command record):");
    TimelineTests();
    Console.WriteLine("Attack event log (src/AttackLog.cs, test build only: the cooldown decode, caps, upload, offsets):");
    AttackLogTests.Run(Check);
    Console.WriteLine("Live DPS meter data (src/DpsFeed.cs):");
    DpsFeedTests();
    DpsNamesTests();
    DpsReplayTests();
    DpsNewFightTests();

    Console.WriteLine("Error reports (src/ErrorReport.cs: opt-in, scrubbed, deduped, capped):");
    ErrorReportTests(mock);

    Console.WriteLine("Window shapes (the game's UI scale: height, or width below 16:9):");
    {
      double W = 1600, H = 1000;   // 16:10, measured on the live game 2026-09-26
      Check(Math.Abs(Ui.Unit(W, H) - 900) < 0.01 && Math.Abs(Ui.Unit(1920, 1009) - 1009) < 0.01, "UI unit: 900 at 1600×1000, the height at 1920×1009");
      var fgx = new FilterGeometry();
      Check(Math.Abs(fgx.X(fgx.MainCloseX, W, H) - 1088) < 4 && Math.Abs(fgx.Y(fgx.CloseY, W, H) - 130) < 4, "filter pop-up × (centred): 1088,130");
      Check(!FilterGeometry.SetBelowView(19) && FilterGeometry.SetBelowView(20) && FilterGeometry.SetBelowView(25), "set list: index 20+ is below the visible rows (no cell is lit)");
      { var lr = fgx.SetListRect(W, H, 100, 50); var c0 = fgx.SetCell(0, W, H); var c19 = fgx.SetCell(19, W, H);
        Check(lr[0] <= c0[0] && lr[1] <= c0[1] && lr[0] + lr[2] >= c19[0] && lr[1] + lr[3] >= c19[1], "set list rect holds the first and the last visible cell"); }
      var hgx = new HintGeometry(); var sl = hgx.Slot(0, W, H);
      Check(Math.Abs(sl[0] + sl[2] / 2.0 - 481) < 4 && Math.Abs(sl[1] + sl[3] / 2.0 - 540) < 6, "weapon slot (centred): 481,540");
      var rpx = hgx.Replace(W, H);
      Check(Math.Abs(rpx[0] + rpx[2] / 2.0 - 692) < 6 && Math.Abs(rpx[1] + rpx[3] / 2.0 - 752) < 6, "«Заменить» (top left): 692,752");
      var eqb = hgx.Equip(W, H);
      Check(Math.Abs(eqb[0] + eqb[2] / 2.0 - 896) < 8 && Math.Abs(eqb[1] + eqb[3] / 2.0 - 824) < 8, "«Надеть» of the single card (centred): 896,824");
      var fb = hgx.Filter(W, H);
      Check(Math.Abs(fb[1] + fb[3] / 2.0 - 936) < 6, "«Фильтр» bar (bottom): y 936");
      {   // lab 548 equip_hint_filter_open: live m_BtnFilter 3,637,275x40 at 1366x705; the layout of hero.filter (frame audit) within 12x12 of it
        var gbr = hgx.GridBar(1366, 705); int[] liveR = { 3, 637, 275, 40 };
        Check(Math.Abs((gbr[0] + gbr[2] / 2.0) - (liveR[0] + liveR[2] / 2.0)) <= 12 && Math.Abs((gbr[1] + gbr[3] / 2.0) - (liveR[1] + liveR[3] / 2.0)) <= 12
          && Math.Abs(gbr[2] - liveR[2]) <= 12 && Math.Abs(gbr[3] - liveR[3]) <= 12, "hero.filter layout vs live rect (lab 548): in the audit's 12x12 tolerance (" + string.Join(",", gbr) + ")");
      }
      var hgeo = new HeroGeometry();
      Check(Math.Abs(hgeo.PitchY * 900 - 167) < 2 && Math.Abs(hgeo.Row1Top * 900 - 149) < 3, "hero grid: rows every 167 px from 149");
    }

    ArenaOppTests();
    GuildTests();
    LiveStateTests();
    FormSearchTests();
    FieldNodeTests();
    HeroFormFollowTests();
    FormSearchPolicyTests();
    GamePacksTests();
    CodeTextTests();
    CodeReportTests();
    HeroGradeTests();
    UpdateKeysTests();

    // ---- WebViewRecovery
    Check(!WebViewRecovery.ShouldShowFallback(false, true, true, 500), "fallback: never once the page is ready");
    Check(!WebViewRecovery.ShouldShowFallback(true, true, false, -1), "fallback: ready wins over a start error");
    Check(WebViewRecovery.ShouldShowFallback(true, false, false, -1), "fallback: the start failed");
    Check(!WebViewRecovery.ShouldShowFallback(false, false, false, -1), "fallback: not before the navigation starts");
    Check(!WebViewRecovery.ShouldShowFallback(false, false, true, 19.9), "fallback: not before 20 s");
    Check(WebViewRecovery.ShouldShowFallback(false, false, true, 20), "fallback: 20 s after a finished navigation without ready");
    Check(!WebViewRecovery.ShouldShowFallback(false, false, false, 30), "fallback: a page still loading is not cut off at 20 s");
    Check(WebViewRecovery.ShouldShowFallback(false, false, false, 60), "fallback: a page that never finishes loading, after 60 s");
    Check(WebViewRecovery.FallbackTitle("ru").StartsWith("Окно программы не открылось") && WebViewRecovery.FallbackTitle("en").StartsWith("The program window"), "fallback texts per language");
    Console.WriteLine("WebView2 recovery");
    Eq(2000, WebViewRecovery.DelayMs(1), "first retry waits 2 s");
    Eq(30000, WebViewRecovery.DelayMs(4), "fourth retry waits 30 s");
    Eq(60000, WebViewRecovery.DelayMs(5), "then a minute");
    Eq(60000, WebViewRecovery.DelayMs(99), "still a minute");
    { long total = 0; for (int i = 1; i <= WebViewRecovery.MaxRetries; i++) total += WebViewRecovery.DelayMs(i); Check(total >= 540000 && total <= 660000, "the retries add up to about 10 minutes (" + total / 1000 + " s)"); }
    var abort = new System.Runtime.InteropServices.COMException("Operation aborted", WebViewRecovery.E_ABORT);
    Check(WebViewRecovery.IsTransient(abort), "E_ABORT is transient");
    Check(WebViewRecovery.IsAbort(abort), "E_ABORT is an abort");
    Check(WebViewRecovery.IsTransient(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80070005))), "another COMException is transient");
    Check(WebViewRecovery.IsTransient(new InvalidOperationException("x")), "InvalidOperationException is transient");
    Check(!WebViewRecovery.IsTransient(new ArgumentException("x")), "other errors are permanent");
    Check(!WebViewRecovery.IsAbort(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80070005))), "another HRESULT is not an abort");
    Check(WebViewRecovery.IsTransient(new AggregateException(abort)), "an AggregateException is unwrapped");
    Eq("0x80004004", WebViewRecovery.HResultHex(abort), "HRESULT in hex");
    Check(WebViewRecovery.FriendlyMessage("ru", abort).StartsWith("Не удалось") && WebViewRecovery.FriendlyMessage("en", abort).StartsWith("Could not"), "friendly message in both languages");
    { var lim = new RecoveryLimiter(5, TimeSpan.FromMinutes(10)); var t0 = new DateTime(2026, 1, 1, 8, 0, 0); int ok = 0;
      for (int i = 0; i < 7; i++) if (lim.TryAcquire(t0.AddSeconds(i))) ok++;
      Eq(5, ok, "at most 5 recoveries in 10 minutes");
      Check(!lim.TryAcquire(t0.AddMinutes(9)), "still blocked after 9 minutes");
      Check(lim.TryAcquire(t0.AddMinutes(11)), "allowed again after the window passed"); }

    RemoteSwitchesTests();
    MaintenanceTests();
    LayoutTests(mock);
    KlassScanTests();
    LuaRootTests();
    UiButtonTests();
    UiCellTests();
    LoadPlanTests();
    TestLogTests(mock);
    ScreenRecorderTests();
    RecInputsTests();

    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed");
    return failed == 0 ? 0 : 1;
  }

  // ------------------------------------------------------------------ layout data from the server (src/Layout.cs)
  const string FixtureGame = "9.8.7.6.5";
  static string FixtureJson() { return File.ReadAllText(Path.Combine("tests", "layout-fixture.json"), Encoding.UTF8); }
  static GameLayout TestLayout() {
    var l = GameLayout.Parse(FixtureJson(), FixtureGame);
    if (l == null) throw new Exception("tests/layout-fixture.json does not parse");
    return l;
  }
  static string Without(string json, string key) {
    // the fixture with one "key": value pair dropped (keys are unique in it)
    int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal); if (i < 0) throw new Exception("no " + key);
    int j = json.IndexOfAny(new[] { ',', '}' }, i);
    return json.Substring(0, i) + json.Substring(json[j] == ',' ? j + 1 : j);
  }

  // ------------------------------------------------------------------ classes found by a scan (src/KlassScan.cs, RFX in src/BattleClock.cs)
  static byte[] Slots(params ulong[] vs) { var b = new byte[vs.Length * 8]; for (int i = 0; i < vs.Length; i++) Array.Copy(BitConverter.GetBytes(vs[i]), 0, b, i * 8, 8); return b; }

  static void KlassScanTests() {
    Console.WriteLine("Class scan (src/KlassScan.cs):");
    const ulong H0 = 0x100000;
    Func<ulong, bool> heap = v => v >= H0 && v < 0x10000000;
    var names = new Dictionary<ulong, string> { { H0 + 0x100, "A.One" }, { H0 + 0x200, "A.Two" }, { H0 + 0x300, "A.Twin" }, { H0 + 0x400, "A.Twin" }, { H0 + 0x500, "A.Other" } };
    Func<IList<ulong>, IList<string>> namesOf = ks => { var r = new List<string>(); foreach (var k in ks) { string n; r.Add(names.TryGetValue(k, out n) ? n : null); } return r; };
    var want = new[] { "A.One", "A.Two", "A.Twin", "A.Gone" };
    bool to; int far = Environment.TickCount + 60000;
    var sec = new KlassSection { Base = 0x7000, Bytes = Slots(0, H0 + 0x100, 5, H0 + 0x201, H0 + 0x200) };
    var res = KlassScan.Find(new[] { sec }, heap, namesOf, want, far, out to);
    Check(!to && res.ContainsKey("A.One") && res["A.One"].Klass == H0 + 0x100 && res["A.One"].Slot == 0x7008 && res["A.One"].Distinct == 1, "a class is found from its slot");
    Check(res.ContainsKey("A.Two") && res["A.Two"].Slot == 0x7020, "  ... an odd value (not filled yet) is skipped, the real slot found");
    Check(!res.ContainsKey("A.Gone"), "  ... a class not there is not in the result");
    sec = new KlassSection { Base = 0x7000, Bytes = Slots(H0 + 0x104, 0x5000, 0x20000000, H0 + 0x100) };
    res = KlassScan.Find(new[] { sec }, heap, namesOf, want, far, out to);
    Check(res.Count == 1 && res["A.One"].Slot == 0x7018, "misaligned and non-heap values are ignored");
    sec = new KlassSection { Base = 0x7000, Bytes = Slots(H0 + 0x100, H0 + 0x100, H0 + 0x300, H0 + 0x400) };
    var st = new KlassStats();
    res = KlassScan.Find(new[] { sec }, heap, namesOf, want, () => false, st, out to);
    Check(res["A.One"].Slot == 0x7000, "a value in two slots: the first slot");
    Check(res["A.Twin"].Distinct == 2 && res["A.Twin"].Klass == 0 && res["A.Twin"].Slot == 0, "two different classes with one name: not accepted");
    Check(st.Pointers == 3 && st.Ambiguous.Count == 1 && st.Ambiguous[0] == "A.Twin", "  ... counted and named in the stats");
    // the deadline
    res = KlassScan.Find(new[] { sec }, heap, namesOf, want, Environment.TickCount - 1000, out to);
    Check(to && res.Count == 0, "an expired deadline: timed out, nothing");
    var many = new ulong[BatchSizeForTest * 2]; for (int i = 0; i < many.Length; i++) many[i] = H0 + 0x1000 + (ulong)i * 16; many[5] = H0 + 0x100;
    int calls = 0; bool stop = false;
    res = KlassScan.Find(new[] { new KlassSection { Base = 0x7000, Bytes = Slots(many) } }, heap, ks => { calls++; stop = true; return namesOf(ks); }, want, () => stop, null, out to);
    Check(to && calls == 1 && res.ContainsKey("A.One"), "the deadline in the second pass: the first batch's hits are kept (partial)");
    // a timed-out scan: the hits are merged in memory, but nothing is saved to disk
    string tdir = Path.Combine(Path.GetTempPath(), "rf-klasst-" + Guid.NewGuid().ToString("N")), tasm = new string('d', 64);
    var saveKd = RFX.KlassDir; RFX.KlassDir = tdir;
    try {
      var one = new Dictionary<string, ulong> { { "A.One", 0x2040 } };
      Check(!RFX.SaveSlots(tasm, one, true) && KlassScan.Load(tdir, tasm) == null, "a scan that timed out: its slots are not saved");
      Check(RFX.SaveSlots(tasm, one, false) && KlassScan.Load(tdir, tasm) != null, "  ... a complete scan's are");
    } finally { RFX.KlassDir = saveKd; try { Directory.Delete(tdir, true); } catch (Exception) { } }
    // the cache file
    string asm = new string('c', 64), dir = Path.Combine(Path.GetTempPath(), "rf-klass-" + Guid.NewGuid().ToString("N"));
    try {
      Check(KlassScan.CacheFile(dir, asm) == Path.Combine(dir, "layout", "klass-" + asm + ".json"), "cache file: layout\\klass-<asm>.json");
      Check(KlassScan.CacheFile(dir, "../x") == null && KlassScan.CacheFile(dir, asm.Substring(1)) == null && KlassScan.CacheFile(dir, null) == null && KlassScan.CacheFile(dir, asm.Replace('c', 'z')) == null, "cache file: a bad asm is never a path");
      Check(KlassScan.Load(dir, asm) == null, "cache: nothing saved: null");
      var slots = new Dictionary<string, ulong> { { "A.One", 0x1234 }, { "B.Two", 0x20000 } };
      Check(KlassScan.Save(dir, asm, slots), "cache: saved");
      Check(!File.Exists(KlassScan.CacheFile(dir, asm) + ".tmp"), "  ... no tmp file left");
      var back = KlassScan.Load(dir, asm);
      Check(back != null && back.Count == 2 && back["A.One"] == 0x1234 && back["B.Two"] == 0x20000, "cache: round trip");
      Check(KlassScan.Load(dir, asm.ToUpperInvariant()) != null, "  ... letter case of the asm does not matter");
      string asm2 = new string('d', 64);
      File.Copy(KlassScan.CacheFile(dir, asm), KlassScan.CacheFile(dir, asm2));
      Check(KlassScan.Load(dir, asm2) == null, "cache: a file whose asm differs gives nothing");
      File.WriteAllText(KlassScan.CacheFile(dir, asm), "{\"asm\":\"" + asm + "\",\"slot\":{\"A\":", Encoding.UTF8);
      Check(KlassScan.Load(dir, asm) == null, "cache: a broken file gives null");
      File.WriteAllText(KlassScan.CacheFile(dir, asm), "{\"asm\":\"" + asm + "\",\"slot\":{\"A\":\"x\"}}", Encoding.UTF8);
      Check(KlassScan.Load(dir, asm) == null, "cache: a non-number slot gives null");
      Check(!KlassScan.Save(dir, "nope", slots), "cache: a bad asm is not saved");
    } finally { try { Directory.Delete(dir, true); } catch (Exception) { } }

    Console.WriteLine("Class scan on the stand-in game (RFX):");
    string saveAsm = Layout.LocalAsm; var saveDir = RFX.KlassDir;
    string kd = Path.Combine(Path.GetTempPath(), "rf-klassr-" + Guid.NewGuid().ToString("N"));
    try {
      var m = new FakeLua(8 << 20); RFX.TestMemory = m; RFX.ForgetLive(); RFX.ForgetClasses();
      ulong ga = FakeImage(m);
      ulong data = ga + 0x2000;
      ulong kBm = FakeKlass(m, "BattleView", "BattleManager"), kSim = FakeKlass(m, "CSharpBattle.Battle", "GameSimulation"), kTip = FakeKlass(m, "", "Form_CommonTips");
      ulong imgSize; int nsec; List<KlassSection> gs = RFX.GaSections(ga, out imgSize, out nsec);
      Check(gs.Count == 1 && nsec == 1 && gs[0].Base == data && gs[0].Bytes.Length == 0x2000 && imgSize == 0x10000, "GaSections: only the writable, non-executable section; image size read");
      Check(RFX.GaSections(m.Alloc(0x1000), out imgSize, out nsec).Count == 0, "GaSections: no MZ: nothing");
      { // a garbage e_lfanew (near 2^32) does not wrap
        ulong gh = m.Alloc(0x1000); m.Mem[(int)(gh - FakeLua.Base)] = (byte)'M'; m.Mem[(int)(gh - FakeLua.Base) + 1] = (byte)'Z';
        m.W32(gh + 0x3C, -8);
        Check(RFX.GaSections(gh, out imgSize, out nsec).Count == 0 && imgSize == 0, "GaSections: a huge e_lfanew: nothing, no exception");
      }
      var nm = RFX.KlassNames(new[] { kBm, kSim, kTip, kTip + 0x30000 });
      Check(nm[0] == "BattleView.BattleManager" && nm[1] == "CSharpBattle.Battle.GameSimulation" && nm[2] == "Form_CommonTips" && nm[3] == null, "KlassNames: namespace.name, no namespace, unreadable = null");
      var heapOk = RFX.HeapTest(ga, 0x10000);
      Check(heapOk(kBm) && !heapOk(data) && !heapOk(ga + 8) && !heapOk(5) && !heapOk(FakeLua.Base + (ulong)m.Mem.Length + 8), "HeapTest: inside the memory, not inside the image");
      // the slots in the data section
      m.W64(data + 0x40, kBm); m.W64(data + 0x108, kSim); m.W64(data + 0x200, kTip);
      Layout.LocalAsm = new string('e', 64); RFX.KlassDir = kd;
      // the scans below are asked for by a layout of another build (RvaDropped)
      Layout.Use(GameLayout.Parse(FixtureJson().Replace("\"v\": 1", "\"v\": 1, \"asm\": \"" + new string('a', 64) + "\""), FixtureGame, new string('b', 64)));
      Check(RFX.ClassAt(ga, "BattleView.BattleManager") == 0, "ClassAt: no layout address, no disk slot: 0 at once (scan asked for)");
      Check(RFX.EnsureClasses(ga, 5000), "EnsureClasses: the scan finds the simulation class");
      Eq(kBm, RFX.ClassAt(ga, "BattleView.BattleManager"), "ClassAt: found by the scan");
      Eq(kSim, RFX.ClassAt(ga, "CSharpBattle.Battle.GameSimulation"), "  ... the simulation class");
      Check(RFX.ScanIdle(3000), "  ... the scan ends");
      { // a matching layout with the class's address set but the slot not filled yet (an odd value): no scan asked for
        var saveLy = Layout.Current; var lyOk = TestLayout();
        Check(lyOk.Rva("BattleView.BattleManager") == 0x2000, "  (a layout with an address for BattleManager)");
        Layout.Use(lyOk); RFX.ForgetClasses(); Layout.LocalAsm = null;
        ulong slot0 = m.R64(ga + 0x2000); m.W64(ga + 0x2000, 0x1235);   // not filled: a metadata token
        int rq = RFX.scanRequests;
        Check(RFX.ClassAt(ga, "BattleView.BattleManager") == 0 && RFX.scanRequests == rq, "ClassAt: this build's address, slot not filled yet: 0, no scan asked for");
        Check(RFX.ClassAt(ga, "Form_CommonTips") == 0 && RFX.scanRequests == rq, "  ... a class without an address in the matching layout: no scan asked for");
        var lyDrop = GameLayout.Parse(FixtureJson().Replace("\"v\": 1", "\"v\": 1, \"asm\": \"" + new string('a', 64) + "\""), FixtureGame, new string('b', 64)); Layout.Use(lyDrop); RFX.ForgetClasses();
        Check(lyDrop.RvaDropped && RFX.ClassAt(ga, "Form_CommonTips") == 0 && RFX.scanRequests == rq + 1, "  ... a layout of another build (RvaDropped): a scan is asked for");
        RFX.ScanIdle(3000); m.W64(ga + 0x2000, slot0); Layout.Use(saveLy); Layout.LocalAsm = new string('e', 64); RFX.ForgetClasses();
      }
      var disk = KlassScan.Load(kd, Layout.LocalAsm);
      Check(disk != null && disk["BattleView.BattleManager"] == 0x2040 && disk["CSharpBattle.Battle.GameSimulation"] == 0x2108 && disk["Form_CommonTips"] == 0x2200, "the slots are kept on disk as rvas");
      // a restart: the slots from disk, no scan
      RFX.ForgetClasses(); long reads0 = RFX.Reads;
      Eq(kTip, RFX.ClassAt(ga, "Form_CommonTips"), "ClassAt: from the slot kept on disk");
      Check(RFX.Reads - reads0 < 20, "  ... without a scan");
      // a stale slot: another class there now
      m.W64(data + 0x200, kBm); RFX.ForgetClasses();
      Check(RFX.ClassAt(ga, "Form_CommonTips") == 0, "ClassAt: a stale slot is not believed");
      Check(RFX.ScanIdle(3000), "  ... the scan it asked for ends");
      Check(RFX.ClassAt(ga, "Form_CommonTips") == 0, "  ... and the class is not found (not in a slot)");
      RFX.ForgetClasses();
      // two classes with one name, another process (another base address): none
      Layout.LocalAsm = null;
      var m2 = new FakeLua(8 << 20); m2.Alloc(0x3000); RFX.TestMemory = m2; RFX.ForgetClasses();
      ulong ga2 = FakeImage(m2), data2 = ga2 + 0x2000;
      ulong a1 = FakeKlass(m2, "BattleView", "BattleManager"), a2 = FakeKlass(m2, "BattleView", "BattleManager"), s1 = FakeKlass(m2, "CSharpBattle.Battle", "GameSimulation");
      m2.W64(data2 + 0x40, a1); m2.W64(data2 + 0x48, a2); m2.W64(data2 + 0x80, s1);
      Check(ga2 != ga, "  (a second image elsewhere)");
      Check(RFX.ClassAt(ga2, "BattleView.BattleManager") == 0, "scan: two different classes named alike: asked for");
      Check(RFX.EnsureClasses(ga2, 5000) && RFX.ScanIdle(3000), "  ... the scan ends, the simulation class found");
      Check(RFX.ClassAt(ga2, "BattleView.BattleManager") == 0, "  ... the ambiguous name is not accepted");
      Eq(s1, RFX.ClassAt(ga2, "CSharpBattle.Battle.GameSimulation"), "  ... the unique one is");
    } finally {
      RFX.ScanIdle(3000);
      Layout.Use(TestLayout());
      RFX.TestMemory = null; RFX.ForgetLive(); RFX.ForgetClasses(); Layout.LocalAsm = saveAsm; RFX.KlassDir = saveDir;
      try { Directory.Delete(kd, true); } catch (Exception) { }
    }
    // the windows: strings 64 KB apart, a name at the window's end, a NUL at the very end of the memory
    var w = new FakeLua(1 << 20); RFX.TestMemory = w;
    try {
      Func<ulong, string, ulong> Put = (at, str) => { Array.Copy(Encoding.ASCII.GetBytes(str + "\0"), 0, w.Mem, (int)(at - FakeLua.Base), str.Length + 1); return at; };
      ulong b0 = FakeLua.Base + 0x1000, ns = Put(b0 + 0x300, "Ns");
      ulong pa = b0 + 0x1000, pb = pa + 0x10000 - 96, pc = pa + 0x10000 + 1, pd = b0 + 0x30000, pe = FakeLua.Base + (ulong)w.Mem.Length - 9;
      string n95 = new string('x', 95);
      Put(pa, "Alpha"); Put(pb, n95); Put(pc, "Gamma"); Put(pd, new string('y', 96)); Put(pe, "EndOfBuf");
      ulong k1 = b0 + 0x8000, k2 = k1 + 0xFFF0, k3 = k2 + 0x10, k4 = b0 + 0x50000, k5 = k4 + 0x20;
      Action<ulong, ulong> Cls = (k, np) => { w.W64(k + 0x10, np); w.W64(k + 0x18, ns); };
      Cls(k1, pa); Cls(k2, pb); Cls(k3, pc); Cls(k4, pd); Cls(k5, pe);
      var nn = RFX.KlassNames(new[] { k1, k2, k3, k4, k5 });
      Eq("Ns.Alpha", nn[0], "windows: first name");
      Eq("Ns." + n95, nn[1], "windows: a name 64 KB on, its NUL the last byte of the window");
      Eq("Ns.Gamma", nn[2], "windows: a name just past the window (a new window)");
      Check(nn[3] == null, "windows: 96 characters and no NUL in reach: refused");
      Eq("Ns.EndOfBuf", nn[4], "windows: a NUL at the last byte of the memory (the window cannot be read: string by string)");
    } finally { RFX.TestMemory = null; RFX.ForgetLive(); }
  }

  // ------------------------------------------------------------------ a button's live rect by name (src/UiButtons.cs)
  static void WF(FakeLua m, ulong a, params double[] v) { for (int i = 0; i < v.Length; i++) Array.Copy(BitConverter.GetBytes((float)v[i]), 0, m.Mem, (int)(a - FakeLua.Base) + 4 * i, 4); }

  /// <summary>A stand-in xLua object pool and Unity's native objects (2019.4 offsets) for the button reader.</summary>
  sealed class FakeUnity {
    public readonly FakeLua M; public readonly ulong Env, List; int next = 1; ulong hier, trs; int nTr;
    public FakeUnity(FakeLua m) {
      M = m;
      ulong kEnv = FakeKlass(m, "XLua", "LuaEnv"), kTr = FakeKlass(m, "XLua", "ObjectTranslator"), tr = m.Alloc(0x1D0), pool = m.Alloc(0x20);
      Env = m.Alloc(0x70); List = m.Alloc(0x20 + 16 * 64);
      m.W64(Env, kEnv); m.W64(Env + 0x20, tr); m.W64(tr, kTr); m.W64(tr + 0xC8, pool); m.W64(pool + 0x10, List); m.W64(List + 0x18, 64);
      hier = m.Alloc(0x30); trs = m.Alloc(48 * 128); m.W64(hier + 0x18, trs);   // (room for 128 transforms: the list cells test makes ~60)
    }
    /// <summary>A GameObject with its RectTransform: anchors, anchored position, sizeDelta, pivot, local position and scale. Returns the transform.</summary>
    public ulong Rect(ulong parent, double[] amin, double[] amax, double[] sd, double[] pv, double[] pos, double scale, out ulong go) {
      go = M.Alloc(0x70); ulong comps = M.Alloc(16), t = M.Alloc(0xF0);
      M.W64(go + 0x30, comps); M.W64(go + 0x40, 1); M.W64(comps + 8, t); M.Mem[go - FakeLua.Base + 0x56] = 1; M.Mem[go - FakeLua.Base + 0x57] = 1;
      M.W64(t + 0x30, go); M.W64(t + 0x38, hier); M.W32(t + 0x40, nTr); M.W64(t + 0x90, parent);
      ulong e = trs + (ulong)(48 * nTr++);
      WF(M, e, pos[0], pos[1], 0); WF(M, e + 0x10, 0, 0, 0, 1); WF(M, e + 0x20, scale, scale, 1);
      WF(M, t + 0xC8, amin[0], amin[1], amax[0], amax[1], 0, 0, sd[0], sd[1], pv[0], pv[1]);
      return t;
    }
    /// <summary>A managed object of class <paramref name="ns"/>.<paramref name="cls"/> over <paramref name="native"/>, as a Lua userdata (its address).</summary>
    public ulong Obj(string ns, string cls, ulong native, out int idx) {
      ulong o = M.Alloc(0x20); M.W64(o, FakeKlass(M, ns, cls)); M.W64(o + 0x10, native);
      idx = next++; M.W64(List + 0x20 + (ulong)(16 * idx) + 8, o);
      ulong u = M.Alloc(0x30); M.Mem[u - FakeLua.Base + 8] = 7; M.W64(u + 0x18, 4); M.W32(u + 0x28, idx);
      return u;
    }
    /// <summary>A component on the GameObject of transform <paramref name="t"/> (Component.m_GameObject 0x30).</summary>
    public ulong Trs(ulong t) { return trs + (ulong)(48 * BitConverter.ToInt32(M.Mem, (int)(t + 0x40 - FakeLua.Base))); }
    /// <summary>A form's C# side as the game has it: a Form_LuaBase (UIBase, a plain C# object: m_param 0x10 empty, m_uiGameObject 0xB8 a managed
    /// GameObject over <paramref name="rootGo"/>, Destroyed 0xF8 off), as a Lua userdata. <paramref name="obj"/> = the managed object.</summary>
    public ulong UiBase(ulong rootGo, out ulong obj, out ulong mgo) {
      obj = M.Alloc(0x1B0); M.W64(obj, FakeKlass(M, "", "Form_LuaBase"));
      int i0; ulong u0 = Obj("UnityEngine", "GameObject", rootGo, out i0);
      mgo = M.R64(List + 0x20 + (ulong)(16 * i0) + 8); M.W64(obj + 0xB8, mgo);
      int idx = next++; M.W64(List + 0x20 + (ulong)(16 * idx) + 8, obj);
      ulong u = M.Alloc(0x30); M.Mem[u - FakeLua.Base + 8] = 7; M.W64(u + 0x18, 4); M.W32(u + 0x28, idx);
      return u;
    }
    public ulong Comp(ulong t) { ulong c = M.Alloc(0x40); M.W64(c + 0x30, M.R64(t + 0x30)); return c; }
    /// <summary>GameObject.m_Name (0x60, a C string).</summary>
    public void Name(ulong go, string n) { ulong p = M.Alloc(n.Length + 1); for (int i = 0; i < n.Length; i++) M.Mem[p - FakeLua.Base + (ulong)i] = (byte)n[i]; M.W64(go + 0x60, p); }
    /// <summary>Transform.m_Children (0x70 data, 0x80 size).</summary>
    public void Kids(ulong t, params ulong[] kids) { ulong a = M.Alloc(8 * kids.Length); for (int i = 0; i < kids.Length; i++) M.W64(a + (ulong)(8 * i), kids[i]); M.W64(t + 0x70, a); M.W64(t + 0x80, (ulong)kids.Length); }
  }


  /// <summary>The bar buttons' frames hug the icon (UiIcon): the picture child picked by name / shape, the label, a red dot, the backdrop left
  /// out; the frame 2 px round the icon, inside the client, above the label.</summary>
  static void UiIconTests() {
    Console.WriteLine("A bar button's icon frame (src/UiButtons.cs UiIcon):");
    double bx = 100, by = 600, bw = 80, bh = 90;
    Func<string, double, double, double, double, UiIcon.Cand> C = (n, x, y, w, h) => new UiIcon.Cand { Name = n, X = x, Y = y, W = w, H = h, Active = true, Depth = 1 };
    var c = new List<UiIcon.Cand> { C("Bg", 100, 600, 80, 90), C("Text_Backpack", 100, 668, 80, 20), C("Image_Backpack", 112, 604, 56, 56), C("RedDot", 160, 600, 14, 14) };
    Check(UiIcon.Pick(c, bx, by, bw, bh) == 2, "icon: the picture is picked, not the backdrop, the label or the red dot");
    var c2 = new List<UiIcon.Cand> { C("Text", 100, 668, 80, 20), C("Obj_1", 116, 606, 48, 48), C("Obj_2", 130, 620, 20, 20) };
    Check(UiIcon.Pick(c2, bx, by, bw, bh) == 1, "icon: no telling name - the biggest near-square child");
    var c3 = new List<UiIcon.Cand> { C("Image_Icon", 112, 604, 56, 56) }; c3[0].Active = false;
    Check(UiIcon.Pick(c3, bx, by, bw, bh) == -1 && UiIcon.Pick(new List<UiIcon.Cand> { C("Image_Far", 600, 100, 56, 56) }, bx, by, bw, bh) == -1, "icon: an inactive child or one off the button is none (the button's rect frames it)");
    Check(UiIcon.Pick(new List<UiIcon.Cand> { C("Icon_Wide", 100, 640, 80, 20) }, bx, by, bw, bh) == -1, "icon: a strip is no icon");
    var f = UiIcon.Frame(112, 604, 56, 56, 1280, 720);
    Check(f[0] == 110 && f[1] == 602 && f[2] == 60 && f[3] == 60 && f[4] > 0 && f[4] <= 30, "icon frame: 2 px round the icon, rounded corners to its size ([" + string.Join(",", Array.ConvertAll(f, v => v.ToString())) + "])");
    Check(f[1] + f[3] <= 668, "icon frame: above the label (bottom " + (f[1] + f[3]) + " <= 668)");
    var e = UiIcon.Frame(1, 700, 40, 19, 1280, 720);
    Check(e[0] == 0 && e[1] + e[3] == 720, "icon frame: kept inside the client");
    // the city's «Инвентарь» on the owner's 526x296 client (1.6.66.399, rec frame_0040): the live button 113,240,40,40 (centre 133,260), its
    // Image_Icon 108 x 88 units centred on it at 296/640 px a unit = 49.95 x 40.7 px. The round rim in the frame is about x 117-148, y 241-272
    double k = 296 / 640.0, iw = 108 * k, ih = 88 * k, ix = 133 - iw / 2, iy = 260 - ih / 2;
    var rf = UiIcon.BarRimFrame(ix, iy, iw, ih, 526, 296);
    string rs = "[" + string.Join(",", Array.ConvertAll(rf, v => v.ToString("0.0"))) + "]";
    Check(Math.Abs(rf[2] - rf[3]) < 0.01 && Math.Abs(rf[2] - (72 * k + 2 * UiIcon.FramePad)) < 0.6 && rf[4] == Math.Round(rf[2] / 2), "city rim frame: a circle round the plate, 2 px out (" + rs + ")");
    Check(rf[0] >= 112 && rf[0] + rf[2] <= 152 && rf[1] >= 238 && rf[1] + rf[3] <= 278, "city rim frame: inside the icon's place in frame_0040 (x 112-150, y ~240-277) " + rs);
    Check(Math.Abs(rf[0] + rf[2] / 2 - 132.7) < 0.5 && Math.Abs(rf[1] + rf[3] / 2 - 257.9) < 0.5, "city rim frame: centred on the plate (132.7, 257.9), above the button's centre (the tip below)");
    // the old way (the Image_Icon's frame with the hit area's inset): a bigger circle, lower, over the label «Инвентарь» (its top ~270)
    var of = UiIcon.Frame(ix, iy, iw, ih, 526, 296);
    var oa = GuideShape.ArtBox(new GeoRect { X = of[0], Y = of[1], W = of[2], H = of[3] }, new GeoInset { L = 0.0473, T = 0.1453, R = 0.1673, B = 0.0693 });
    double ob = oa.CY + Math.Min(oa.W, oa.H) / 2 + 2;
    Check(ob > rf[1] + rf[3] + 3, "city rim frame: the old frame's bottom " + ob.ToString("0") + " was well over the label, now " + (rf[1] + rf[3]).ToString("0"));
    // live 1.6.66.400 (rec2 frame_0141): the button 113,248,40,40 (the recorded frame 111,246,44,44 was it + 2 px: the Image_Icon child was taken
    // for a backdrop, wider than the button); the dark plate measured at (132, 264), 34 px across. Image_Icon by name, centred on the button
    var lc = new List<UiIcon.Cand> { new UiIcon.Cand { Name = "BtnHitArea", X = 113, Y = 248, W = 40, H = 40, Active = true, Depth = 1 },
      new UiIcon.Cand { Name = "Image_Icon", X = 133 - iw / 2, Y = 268 - ih / 2, W = iw, H = ih, Active = true, Depth = 1 },
      new UiIcon.Cand { Name = "m_Text_Backpack", X = 112, Y = 258, W = 42, H = 17, Active = true, Depth = 1 } };
    Check(UiIcon.Pick(lc, 113, 248, 40, 40) == -1 && UiIcon.Named(lc, "Image_Icon") == 1, "city plate: the shape rules miss the wide Image_Icon, its name finds it");
    var lp = UiIcon.BarPlate(lc[1].X, lc[1].Y, lc[1].W, lc[1].H); var lf = UiIcon.BarRimFrame(lc[1].X, lc[1].Y, lc[1].W, lc[1].H, 526, 296);
    string ls = "plate " + lp[0].ToString("0.0") + "," + lp[1].ToString("0.0") + " d " + lp[2].ToString("0.0") + ", frame [" + string.Join(",", Array.ConvertAll(lf, v => v.ToString("0.0"))) + "]";
    Check(Math.Abs(lp[0] - 132) <= 2 && Math.Abs(lp[1] - 264) <= 2 && Math.Abs(lp[2] - 34) <= 2, "city plate: on the live plate (132, 264, 34 px) " + ls);
    Check(Math.Abs(lf[2] / 2 - (lp[2] / 2 + 2)) < 0.01 && lf[2] < 44 - 4, "city plate: the frame = the plate's radius + 2 px, smaller than the old 44 px " + ls);
    var tiny = UiIcon.BarRimFrame(-20, 280, 50, 40, 526, 296);
    Check(tiny[0] == 0 && tiny[1] + tiny[3] <= 296, "city rim frame: kept inside the client");
  }

  /// <summary>A UI sprite's visible art (src/SpriteArtData.cs from tools/sprite-bounds.py) mapped through the Image's live rect (src/SpriteArt.cs).</summary>
  static void SpriteArtTests() {
    Console.WriteLine("Sprite art and its frame (src/SpriteArt.cs, src/SpriteArtData.cs):");
    Check(SpriteArt.Count > 500, "sprite table: " + SpriteArt.Count + " sprites of the forms (game " + SpriteArt.GameVersion + ")");
    // the city bar's «Инвентарь»: the dark round plate inside main_btn_Backpack (108 x 88, a tip below, side lines) - the numbers measured by hand
    // before (d9b30e5: the solid body (0.494, 0.429) d 0.759; 1d3b137: the visible edge (0.495, 0.447) d 0.8175) come out of the table
    var bp = SpriteArt.Of("main_btn_Backpack");
    Check(bp != null && bp.W == 108 && bp.H == 88 && bp.Kind == 'c', "Inventory plate: in the table, 108 x 88, a circle");
    if (bp != null && bp.Core != null && bp.Circle != null) {
      string cs = string.Join(",", Array.ConvertAll(bp.Core, v => v.ToString("0.000"))), vs = string.Join(",", Array.ConvertAll(bp.Circle, v => v.ToString("0.000")));
      Check(Math.Abs(bp.Core[0] - 0.494) <= 0.01 && Math.Abs(bp.Core[1] - 0.429) <= 0.01 && Math.Abs(bp.Core[2] - 0.759) <= 0.01, "Inventory plate: solid body centre (0.494, 0.429), d 0.759 of the height: " + cs);
      Check(Math.Abs(bp.Circle[0] - 0.495) <= 0.01 && Math.Abs(bp.Circle[1] - 0.447) <= 0.01 && Math.Abs(bp.Circle[2] - 0.8175) <= 0.015, "Inventory plate: visible edge centre (0.495, 0.447), d 0.8175: " + vs);
      Check(bp.Poly != null && bp.Poly.Length / 2 >= 8 && bp.Poly.Length / 2 <= 64, "Inventory plate: a contour of " + (bp.Poly != null ? bp.Poly.Length / 2 : 0) + " points (at most 64)");
      // the side lines are no part of it: the visible box is the plate and its tip (about the circle's width), not the whole sprite
      Check(bp.L > 0.12 && bp.R < 0.88 && bp.B > 0.85, "Inventory plate: the box leaves the side lines out, keeps the tip (" + bp.L.ToString("0.000") + ".." + bp.R.ToString("0.000") + ", bottom " + bp.B.ToString("0.000") + ")");
      // live 1.6.66.400 (526x296): Image_Icon 106.3,247.4 at 108 x 88 units of 0.4625 px: the frame round the plate, 2 px out
      double iw = 108 * 0.4625, ih = 88 * 0.4625, ix = 133 - iw / 2, iy = 268 - ih / 2;
      var fit = SpriteArt.FrameOf(bp, new SpriteArt.Draw(), ix, iy, iw, ih, 526, 296);
      double cx = ix + bp.Circle[0] * iw, d = bp.Circle[2] * ih;
      Check(fit != null && fit.Kind == "poly" && Math.Abs(fit.Frame[0] + fit.Frame[2] / 2 - cx) <= 1.5 && Math.Abs(fit.Frame[2] - (d + 4)) <= 2.5, "Inventory frame: on the plate (centre x " + cx.ToString("0.0") + ", " + (d + 4).ToString("0.0") + " px wide), the contour drawn: " + (fit != null ? string.Join(",", Array.ConvertAll(fit.Frame, v => v.ToString("0.0"))) : "none"));
      var pts = fit != null ? GuideShape.FrameOutline(fit.Box, fit.Shape, null, 2) : null;
      bool inside = pts != null; if (pts != null) foreach (var q in pts) if (q.X < fit.Frame[0] - 2.5 || q.X > fit.Frame[0] + fit.Frame[2] + 2.5 || q.Y < fit.Frame[1] - 2.5 || q.Y > fit.Frame[1] + fit.Frame[3] + 2.5) inside = false;
      Check(inside, "Inventory frame: the contour 2 px out stays on the frame's box");
    }
    // preserveAspect: a 100 x 50 sprite in a 100 x 100 rect is drawn 100 x 50, the spare 50 shared on the pivot (y up: pivot 0 = at the bottom)
    var a = new SpriteArt.Art { Name = "t", W = 100, H = 50, L = 0, T = 0, R = 1, B = 1, Kind = 'x' };
    var dc = SpriteArt.Drawn(a, new SpriteArt.Draw { Preserve = true }, 0, 0, 100, 100);
    var db = SpriteArt.Drawn(a, new SpriteArt.Draw { Preserve = true, PivY = 0 }, 0, 0, 100, 100);
    var dn = SpriteArt.Drawn(a, new SpriteArt.Draw(), 0, 0, 100, 100);
    var dt = SpriteArt.Drawn(a, new SpriteArt.Draw { Preserve = true, PivX = 1 }, 0, 0, 300, 100);   // a wide rect: 200 x 100, pushed right by pivot 1
    Check(dc[1] == 25 && dc[3] == 50 && db[1] == 50 && dn[3] == 100 && dt[0] == 100 && dt[2] == 200, "preserveAspect: letterboxed on the pivot (centre 25, pivot 0 at 50, off: stretched, wide rect x 100)");
    var ds = SpriteArt.Drawn(a, new SpriteArt.Draw { Preserve = true, Type = 1 }, 0, 0, 100, 100);
    Check(ds[3] == 100, "preserveAspect: not for a Sliced Image");
    // Sliced: 20 px borders of a 100 px sprite at 2 client px per sprite px stay 40 px, the middle stretches; borders that do not fit shrink together
    Check(SpriteArt.SliceAxis(10, 100, 20, 20, 300, 2) == 20 && SpriteArt.SliceAxis(90, 100, 20, 20, 300, 2) == 280 && SpriteArt.SliceAxis(50, 100, 20, 20, 300, 2) == 150, "sliced: borders kept at their size, the middle stretched");
    Check(SpriteArt.SliceAxis(20, 100, 20, 20, 40, 2) == 20, "sliced: borders wider than the rect shrink together");
    var sl = new SpriteArt.Art { Name = "s", W = 100, H = 100, BL = 20, BR = 20, BT = 20, BB = 20, L = 0.1, T = 0.1, R = 0.9, B = 0.9, Kind = 'r', Round = 0.1 };
    var sf = SpriteArt.FrameOf(sl, new SpriteArt.Draw { Type = 1, Px = 1 }, 0, 0, 400, 100, 1000, 1000);
    Check(sf != null && Math.Abs(sf.Box.X - 10) < 1e-6 && Math.Abs(sf.Box.W - 380) < 1e-6 && Math.Abs(sf.Box.H - 80) < 1e-6, "sliced: the visible box 10 px in on each side of a 400 px wide button (the border's own 10 px), not 40");
    // the shapes: a table line parses; a poly outline is offset outwards
    var pa = SpriteArt.Parse("x|10|10|100|0,0,0,0|0.1,0.1,0.9,0.9|p|0|||100,100,900,100,900,900,100,900");
    Check(pa != null && pa.Poly != null && pa.Poly.Length == 8 && pa.Kind == 'p' && pa.Circle == null, "table line: parsed (a square contour, no circle)");
    var sq = GuideShape.Offset(new List<GuidePt> { new GuidePt(0, 0), new GuidePt(10, 0), new GuidePt(10, 10), new GuidePt(0, 10) }, 2);
    Check(Math.Abs(sq[0].X + 2) < 1e-6 && Math.Abs(sq[0].Y + 2) < 1e-6 && Math.Abs(sq[2].X - 12) < 1e-6, "contour offset: 2 px out at each corner");
    var ls = new LiveShapes(); ls.Put(new double[] { 10, 10, 50, 50, 5 }, new GeoRect { X = 12, Y = 12, W = 46, H = 46 }, new GeoShape { K = "circle" }, 1000);
    GeoRect fb; GeoShape fs;
    Check(ls.Find(10.6, 10, 50, 51, 1500, out fb, out fs) && fs.K == "circle" && !ls.Find(10, 10, 50, 50, 3500, out fb, out fs) && !ls.Find(20, 10, 50, 50, 1500, out fb, out fs), "live shapes: found by the frame rect within 1.5 px and 2 s");
  }

  /// <summary>Every hint frame target (src/FrameTargets.cs) is a widget of the UI graph / the geometry file, and «Улучшить» of the gem screen is framed
  /// on its plate (the owner's photo 2026-10-31: the glow far past the button, the hint over its top).</summary>
  static void FrameTargetTests() {
    Console.WriteLine("Frame targets (src/FrameTargets.cs) and the «Улучшить» frame:");
    // the keys the frames use: the steps' own keys, the plates' neighbours, the overlay's
    var used = new HashSet<string>(FrameTargets.OverlayKeys);
    foreach (LocateKind lk in Enum.GetValues(typeof(LocateKind)))
      foreach (bool gear in new[] { false, true }) foreach (var key in LocateFrames.AvoidKeys(new LocateStep { Kind = lk, OnGear = gear })) used.Add(key);
    foreach (var key in new[] { "hero.workshop", "inventory.workshop", "foundry.tab.ascend", "foundry.tab.recast", "ascend.filter", "recast.filter", "filter.reset", "filter.suitOpen",
      "filter.mainOpen", "filter.subOpen", "filter.suit.tab.0", "filter.suit.tab.1", "ascend.button", "recast.button", "gem.button", "inventory.bulkBack", "inventory.cardLeft", "inventory.cardSolo",
      "inventory.leftTab.artifact", "inventory.artWorkshop", "artifact.tab.promote", "artifact.tab.enhance", "artifact.promoteBtn", "artifact.matList", "city.barButton",
      ArtEquipFrames.KeyTab, ArtEquipFrames.KeyOpenList, ArtEquipFrames.KeyAdd, ArtEquipFrames.KeyReplace, ArtEquipFrames.KeyPopup, ArtEquipFrames.KeyHideWorn }) used.Add(key);
    for (int i = 0; i < 5; i++) { used.Add("ascend.slot." + i); used.Add("recast.slot." + i); }
    foreach (UiCellKind ck in Enum.GetValues(typeof(UiCellKind))) if (ck != UiCellKind.None) used.Add(FrameTargets.KeyOf(ck));
    var unmapped = new List<string>(); foreach (var key in used) if (FrameTargets.Of(key) == null) unmapped.Add(key);
    Check(unmapped.Count == 0, "every key the frames use is mapped (" + used.Count + " keys)" + (unmapped.Count > 0 ? ": unmapped " + string.Join(", ", unmapped.ToArray()) : ""));
    // each target: its form in the UI graph, its tree a widget path of the graph (or the geometry file's path of the key); a prefab-only path is one of the known panels made in code
    string gp = Path.Combine("tests", "fixtures", "screens", "_geometry.json");
    var geo = File.Exists(gp) ? MiniJson.Parse(File.ReadAllText(gp)) as Dictionary<string, object> : null;
    var rects = geo != null && geo.ContainsKey("rects") ? geo["rects"] as Dictionary<string, object> : null;
    var prefabOk = new HashSet<string> { "inventory.leftTab.equip", "inventory.leftTab.artifact", "inventory.artBulkSell", "hero.confirm", "hero.artOpenList", "hero.artAdd",
      "artifact.tab.promote", "artifact.tab.enhance", "artifact.promoteBtn", "gem.button" };
    var bad = new List<string>(); int live = 0, layout = 0;
    foreach (var t in FrameTargets.All) {
      if (t.Layout != null) { layout++; if (t.Layout.Length < 10) bad.Add(t.Key + " (no reason)"); continue; }
      live++;
      var f = UiGraph.ByName(t.Form);
      if (f == null) { bad.Add(t.Key + " (form " + t.Form + " not in the graph)"); continue; }
      if (t.Cell != UiCellKind.None) continue;
      if (t.Click != ClickTarget.None && RFX.UiWidgetOf(t.Click, null) == null) { bad.Add(t.Key + " (no pilot read)"); continue; }
      if (t.Click == ClickTarget.None && (FrameTargets.Widget(t) == null || FrameTargets.Widget(t).Trees.Length == 0)) { bad.Add(t.Key + " (no widget)"); continue; }
      string t0 = t.Tree.Split('|')[0];
      if (t.Src == "graph") {
        bool hit = false; foreach (var w in f.Widgets) if (w.Path == t0 || t0.StartsWith(w.Path + "/", StringComparison.Ordinal) || w.Path.StartsWith(t0 + "/", StringComparison.Ordinal)) hit = true;
        if (!hit) bad.Add(t.Key + " (" + t0 + " not a widget of " + t.Form + " in the graph)");
      } else if (t.Src == "geometry") {
        var r = rects != null && rects.ContainsKey(t.Key) ? rects[t.Key] as Dictionary<string, object> : null;
        string path = r != null && r.ContainsKey("path") ? r["path"] as string : null;
        if (rects != null && (path == null || !path.EndsWith("/" + t0, StringComparison.Ordinal))) bad.Add(t.Key + " (geometry path " + (path ?? "none") + ")");
      } else if (!prefabOk.Contains(t.Key)) bad.Add(t.Key + " (prefab path not on the known list)");
    }
    Check(bad.Count == 0, "every target resolves to a widget path (" + live + " read live, " + layout + " layout only)" + (bad.Count > 0 ? ": " + string.Join("; ", bad.ToArray()) : ""));
    Console.WriteLine("    targets: " + live + " live (" + FrameTargets.All.Count + " keys), " + layout + " layout only");
    var gw = FrameTargets.Widget(FrameTargets.Of("gem.button"));
    Check(gw != null && gw.Form == "Form_EquipGemMain" && gw.Trees.Length == 5 && gw.Trees[0].EndsWith("/c_BtnReforge", StringComparison.Ordinal) && gw.IconChild == "common_btn_special" && gw.Icon,
      "«Улучшить»: read by its tree (the shown function's widget wins), framed on its plate common_btn_special");

    // «Улучшить» on a 1366 x 705 client (the owner's laptop): the button 216 x 70 canvas units at the layout's place, its plate the same rect
    double W = 1366, H = 705, U = 705, k = U / 640;
    double bw = 216 * k, bh = 70 * k, bx = W / 2 - bw / 2, by = H / 2 + 0.3453 * U;
    var art = SpriteArt.Of("common_btn_special");
    Check(art != null, "the plate's sprite is in the table");
    if (art != null) {
      var fit = SpriteArt.FrameOf(art, new SpriteArt.Draw { Type = 1, Px = k }, bx, by, bw, bh, W, H);
      var b = fit.Box; var fr = fit.Frame;
      Check(b.X >= bx - 0.01 && b.Y >= by - 0.01 && b.X + b.W <= bx + bw + 0.01 && b.Y + b.H <= by + bh + 0.01, "the plate's visible art lies inside the button");
      // the frame drawn (PaintFrame: the gold line 1 px out of the frame rect, the glow inside it) never more than 3 px past the art
      double outL = b.X - (fr[0] - 1), outT = b.Y - (fr[1] - 1), outR = fr[0] + fr[2] + 1 - (b.X + b.W), outB = fr[1] + fr[3] + 1 - (b.Y + b.H);
      Check(Math.Max(Math.Max(outL, outT), Math.Max(outR, outB)) <= FrameTargets.MaxOut + 0.01 && Math.Min(Math.Min(outL, outT), Math.Min(outR, outB)) >= 0,
        "the frame hugs the plate: at most " + FrameTargets.MaxOut + " px past its art (was the texture 20 % and a 14 px glow out)");
      Check(fr[2] <= bw + 2 * FrameTargets.MaxOut && fr[3] <= bh + 2 * FrameTargets.MaxOut, "the frame is no bigger than the button + 3 px a side");
      // the hint of the photo: 1.42 x the button's width, 1.07 x its height; the gem screen's other buttons to keep off
      var tgt = new[] { (int)Math.Round(fr[0]), (int)Math.Round(fr[1]), (int)Math.Round(fr[2]), (int)Math.Round(fr[3]) };
      int pw = (int)(1.42 * bw), ph = (int)(1.07 * bh); char side;
      var avoid = new[] { new[] { (int)bx, (int)by, (int)bw, (int)bh } };
      var pos = HintLayout.Place(tgt, avoid, pw, ph, (int)W, (int)H, out side);
      bool clear = !HintLayout.OnTarget(pos[0], pos[1], pw, ph, tgt);
      int gap = side == 't' ? tgt[1] - (pos[1] + ph) : side == 'b' ? pos[1] - (tgt[1] + tgt[3]) : side == 'r' ? pos[0] - (tgt[0] + tgt[2]) : tgt[0] - (pos[0] + pw);
      Check(clear && gap >= HintLayout.TargetGap, "the hint stays off «Улучшить»: side " + side + ", " + gap + " px from the frame (at least " + HintLayout.TargetGap + ")");
      // a cramped client (the button near the bottom, no room above for the plate at its size): still never on the button
      var pos2 = HintLayout.Place(tgt, avoid, pw, (int)(tgt[1] - 2), (int)W, (int)H, out side);
      Check(!HintLayout.OnTarget(pos2[0], pos2[1], pw, (int)(tgt[1] - 2), tgt), "no free place: the least covering place OFF the button is taken (side " + side + ")");
    }

    Func<double, double, bool> Nr = (a1, b1) => Math.Abs(a1 - b1) < 0.01;
    // owner 2026-10-31 on 1.6.66.423: frames too thin, two frames on a cell, «Снаряжение» on its icon contour, a hero frame between two cards, flicker
    // a frame on a cell never reaches the neighbouring cells (the line and the glow stay in the gap), inventory and artifact grids, small and big windows
    foreach (var sz in new[] { new[] { 1366.0, 705.0 }, new[] { 1920.0, 1009.0 }, new[] { 1280.0, 720.0 } })
      foreach (bool artG in new[] { false, true }) {
        double gW = sz[0], gH = sz[1], gU = Ui.Unit(gW, gH); int cols = 8;
        var sg = artG ? SellGeometry.Artifacts(gW, gH, gU, cols) : SellGeometry.Inventory(gW, gU, cols);
        double cellW = sg.CellW * gU, cellH = sg.CellHeight * gU, px = sg.PitchX * gU, py = sg.PitchY * gU, x0 = gW / 2 + sg.X0 * gU;
        var c0 = new[] { x0, 100.0, cellW, cellH };
        var right = new[] { x0 + px, 100.0, cellW, cellH }; var below = new[] { x0, 100.0 + py, cellW, cellH };
        foreach (bool glowToo in new[] { false }) {
          double reach = 4;   // the drawn line: the 4 px line centred on the edge reaches 2 px out; 4 leaves room for the cut-down glow
          var fb = new[] { c0[0] - reach, c0[1] - reach, c0[2] + 2 * reach, c0[3] + 2 * reach };
          Func<double[], bool> hit = nb => { var ov = FrameAudit.Overlap(fb, nb); return ov[0] > 0 && ov[1] > 0; };
          Check(!hit(right) && !hit(below),
            "frame geometry: a frame on a cell (" + (artG ? "artifact" : "gear") + " grid, " + gW + "x" + gH + ", " + (glowToo ? "with the glow" : "the line") + ") does not meet the neighbouring cells (gaps " + Math.Round(px - cellW, 1) + " / " + Math.Round(py - cellH, 1) + " px)");
        }
      }
    FrameStyleTests.Run(Check);
    // a cell: the c_Frame icon square 80 x 80 and the cell root with the stat plate under it 84 x 112 -> one frame on the root
    var cell = new UiCell { Uid = 7, Index = 3, X = 102, Y = 202, W = 80, H = 80, Icon = "c_Frame", Active = true, InView = 1 };
    cell.BX = cell.X; cell.BY = cell.Y; cell.BW = cell.W; cell.BH = cell.H;
    RFX.CellBox(cell, 100, 200, 84, 112);
    Check(Nr(cell.BX, 100) && Nr(cell.BY, 200) && Nr(cell.BW, 84) && Nr(cell.BH, 112), "a cell's frame box is the cell root: icon and stat plate (" + cell.BX + "," + cell.BY + " " + cell.BW + "x" + cell.BH + ")");
    var wide = new UiCell { X = 102, Y = 202, W = 80, H = 80 }; wide.BX = wide.X; wide.BY = wide.Y; wide.BW = wide.W; wide.BH = wide.H;
    RFX.CellBox(wide, 0, 200, 700, 112);
    Check(Nr(wide.BW, 80) && Nr(wide.BH, 80), "a row strip or the list's content is not taken for the cell");
    var tallItem = new UiCell { X = 102, Y = 202, W = 80, H = 80 }; tallItem.BX = tallItem.X; tallItem.BY = tallItem.Y; tallItem.BW = tallItem.W; tallItem.BH = tallItem.H;
    RFX.CellBox(tallItem, 100, 200, 84, 230, true);
    var tallCell = new UiCell { X = 102, Y = 202, W = 80, H = 80 }; tallCell.BX = tallCell.X; tallCell.BY = tallCell.Y; tallCell.BW = tallCell.W; tallCell.BH = tallCell.H;
    RFX.CellBox(tallCell, 100, 200, 84, 230);
    Check(Nr(tallItem.BH, 230) && Nr(tallCell.BH, 80), "a gear row's own item child with a tall stat plate is the cell root; a tall root of another cell is still left out");
    var okR = new UiButtonInfo { Visible = true, X = 900, Y = 500, W = 120, H = 40 }; var topR = new UiButtonInfo { Visible = true, X = 100, Y = -300, W = 120, H = 40 };
    var hidR = new UiButtonInfo { Visible = false, X = 900, Y = 500, W = 120, H = 40 }; var leftR = new UiButtonInfo { Visible = true, X = 100, Y = 500, W = 120, H = 40 };
    Check(RFX.UiPickScore(okR, 1366, 705) > RFX.UiPickScore(leftR, 1366, 705) && RFX.UiPickScore(leftR, 1366, 705) > 0 && RFX.UiPickScore(topR, 1366, 705) == 0 && RFX.UiPickScore(hidR, 1366, 705) == 0 && RFX.UiPickScore(null, 1366, 705) == 0,
      "«Заменить»: the shown tip's button inside the client, in the lower half and right-hand wins; a hidden one or one above the client never");
    var oldR = new UiButtonInfo { Visible = true, X = 900, Y = 500, W = 120, H = 40, ForW = 1366, ForH = 705, Copies = 1, At = 1000 }; double lx, ly; string lwhy;
    Check(!RFX.UiLivePoint(oldR, 1366, 705, 1500, out lx, out ly, out lwhy) && RFX.UiLivePoint(oldR, 1366, 705, 1500, out lx, out ly, out lwhy, RFX.UiFrameFreshMs),
      "a frame takes a read of the calm 250 ms rate (500 ms old); a click still needs one of 200 ms");
    var cs = new UiCellSet { Kind = UiCellKind.Inventory, Visible = true, Copies = 1, ForW = 1366, ForH = 705, At = Environment.TickCount, VX = 0, VY = 0, VW = 900, VH = 600 };
    cs.Cells.Add(cell);
    double[] cfr; string cwhy;
    var cst = RFX.UiCellState(cs, 7, 1366, 705, Environment.TickCount, out cfr, out cwhy);
    Check(cst == UiCellSee.Shown && cfr != null && Nr(cfr[0], 98) && Nr(cfr[1], 198) && Nr(cfr[2], 88) && Nr(cfr[3], 116) && cell.Fit == null,
      "the cell's one frame is the whole cell 2 px out, a rect (no quality-frame contour): " + (cfr != null ? string.Join(",", Array.ConvertAll(cfr, v => v.ToString("0"))) : cst + " " + cwhy));
    double[] sq = { 98, 198, 88, 88, 16 }, wh = CellFrame.Whole(sq, new double[] { 100, 200, 84, 112 });
    Check(Nr(wh[3], Math.Round(88 * 112 / 84.0)) && Nr(wh[0], 98) && Nr(wh[2], 88) && CellFrame.Whole(new double[] { 98, 198, 88, 120, 16 }, new double[] { 100, 200, 84, 112 })[3] == 120,
      "an icon-only live frame grows down over the stat plate to the list's own cell proportion; a whole one stays");
    // one frame per target: a moment without a fresh read keeps the live frame (no jump to the layout's and back); another item or Hidden drops it
    var hold = new CellFrame.Hold(); double[] f1 = { 10, 20, 30, 40, 5 }, fa = f1;
    var h1 = hold.Pick("a", UiCellSee.Shown, ref fa, 1366, 705, 1000);
    double[] g = null; var h2 = hold.Pick("a", UiCellSee.Unknown, ref g, 1366, 705, 1500);
    double[] g2 = null; var h3 = hold.Pick("b", UiCellSee.Unknown, ref g2, 1366, 705, 1600);
    double[] g3 = null; var h4 = hold.Pick("a", UiCellSee.Unknown, ref g3, 1366, 705, 1700);
    var hold2 = new CellFrame.Hold(); double[] k1 = f1; hold2.Pick("a", UiCellSee.Shown, ref k1, 1366, 705, 0);
    double[] k2 = null; var h5 = hold2.Pick("a", UiCellSee.Unknown, ref k2, 1366, 705, CellFrame.Hold.Ms + 1);
    Check(h1 == UiCellSee.Shown && h2 == UiCellSee.Shown && g == f1 && h3 == UiCellSee.Unknown && g2 == null && h4 == UiCellSee.Unknown && h5 == UiCellSee.Unknown,
      "one frame per cell: held through a short gap in the reads, dropped for another item and after " + CellFrame.Hold.Ms + " ms");
    // menu / tab buttons with a label: the whole plate; round icon buttons without a plate: the sprite's contour
    var plates = new List<string>();
    foreach (var key in new[] { "hero.tab.equip", "hero.artifactTab", "inventory.leftTab.equip", "inventory.leftTab.artifact", "foundry.tab.ascend", "foundry.tab.recast",
      "hero.filter", "ascend.filter", "recast.filter", "artifact.tab.promote", "artifact.tab.enhance", "inventory.bulkSell", "inventory.artBulkSell", "inventory.bulkBack" }) {
      var ft = FrameTargets.Of(key); var fw = ft != null ? FrameTargets.Widget(ft) : null;
      if (ft == null || ft.Visual != "-" || (fw != null && (fw.Icon || fw.IconChild != null))) plates.Add(key);
    }
    Check(plates.Count == 0, "tabs and labelled buttons («Снаряжение», «Фильтр», ...) are framed on their whole plate, not the icon" + (plates.Count > 0 ? ": " + string.Join(", ", plates.ToArray()) : ""));
    var hf = FrameTargets.Widget(FrameTargets.Of("hero.filter"));
    Check(hf != null && hf.Trees.Length == 1 && hf.Trees[0].EndsWith("/m_BtnFilter", StringComparison.Ordinal), "«Фильтр» framed on m_BtnFilter (the bar with its label), not its m_FilterOff icon");
    var rw = FrameTargets.Widget(FrameTargets.Of("hero.workshop")); var ch = FrameTargets.Widget(FrameTargets.Of("city.heroes"));
    Check(rw != null && !rw.Icon && rw.IconChild == null && FrameTargets.Of("hero.workshop").Round && ch != null && ch.Icon, "the hero gear row's round buttons («Литейная») are framed on their own root widget, never the icon child; the city bar keeps the contour");
    foreach (var rk in new[] { "hero.workshop", "hero.gem", "hero.switching" }) Check(FrameTargets.Of(rk).Round && FrameTargets.Of(rk).Visual == "-", "round button " + rk + " is on its root widget (Round)");
    // the hero card: the live cell of the hero grid, only when the read binds this hero to one cell at its place in the list
    var hc = FrameTargets.Of("hero.card");
    Check(hc != null && hc.Cell == UiCellKind.HeroList && hc.Layout == null && FrameTargets.KeyOf(UiCellKind.HeroList) == "hero.card", "the hero card is read live (the hero grid's cell), never the layout's rect");
    var hs1 = new UiCellSet { Kind = UiCellKind.HeroList };
    var hcell = new UiCell { Uid = 1050, Index = 4, X = 300, Y = 200, W = 90, H = 125, Active = true }; hcell.BX = 300; hcell.BY = 200; hcell.BW = 90; hcell.BH = 125;
    hs1.Cells.Add(hcell);
    bool sure = CellFrame.HeroCellSure(hs1, 1050, 4), wrongIdx = CellFrame.HeroCellSure(hs1, 1050, 5), noRead = CellFrame.HeroCellSure(null, 1050, 4);
    var hs2 = new UiCellSet { Kind = UiCellKind.HeroList }; hs2.Cells.Add(hcell); hs2.Cells.Add(new UiCell { Uid = 1050, Index = 7, Active = true, BW = 90, BH = 125 });
    var hs3 = new UiCellSet { Kind = UiCellKind.HeroList, FailStep = "grid" }; hs3.Cells.Add(hcell);
    Check(sure && !wrongIdx && !noRead && !CellFrame.HeroCellSure(hs2, 1050, 4) && !CellFrame.HeroCellSure(hs3, 1050, 4),
      "the hero card is framed only when sure: its cell at its list index; another index, two cells, a failed read or none = no frame");
  }

  /// <summary>The step plate scales with the game client (src/HintLayout.cs HintSize) and keeps its place while the step stays (PlateHold).</summary>
  static void HintSizeTests() {
    Console.WriteLine("The step plate's size and its hold (src/HintLayout.cs HintSize, PlateHold):");
    double Us = Ui.Unit(526, 296), Ub = Ui.Unit(1920, 1080);
    Check(HintSize.FontPx(1, false, Us) == HintSize.MinMain && HintSize.FontPx(0, false, Us) == HintSize.MinCaps && HintSize.Pad(Us) == HintSize.MinPad, "plate on 526x296: fonts and padding at the readable minimum (" + HintSize.FontPx(1, false, Us) + " px)");
    Check(Math.Abs(HintSize.FontPx(1, false, Ub) - 0.026 * Ub) < 1e-9 && HintSize.FontPx(1, false, Ub) > 2.5 * HintSize.MinMain, "plate on 1920x1080: fonts in U (" + HintSize.FontPx(1, false, Ub).ToString("0.0") + " px)");
    Check(HintSize.MaxWidth(526) == 360 && HintSize.MaxWidth(1136) == 454 && HintSize.MaxWidth(1920) == 768 && HintSize.MaxWidth(300) == 288, "plate: at most 40 % of the client wide, at least 360 px, inside the client");
    Check(HintSize.Compact(Us) && !HintSize.Compact(Ub) && !HintSize.Compact(Ui.Unit(1280, 720)), "plate: one compact line on a small client only");
    Check(HintSize.StepShort("Показать в игре · Браслет «Ночной ужас» · шаг 1 из 6") == "1/6" && HintSize.StepShort("Show in game · Ring · step 12 of 14") == "12/14" && HintSize.StepShort("Показать в игре · Браслет") == "", "plate: the step number from the caps line");
    Check(HintSize.CompactLine("Показать в игре · Браслет · шаг 1 из 6", "Откройте Инвентарь") == "1/6 · Откройте Инвентарь" && HintSize.CompactLine("", "Откройте") == "Откройте", "plate: the compact line is the step and the action");
    // a fixed-width measure: 6 px a character
    Func<string, double> m6 = t => 6.0 * t.Length;
    string el = HintSize.Ellipsize("Программа открывает вещь сама", 100, m6);
    Check(m6(el) <= 100 && el.EndsWith("…") && el.StartsWith("Программа"), "plate: a long line cut with «…» to the width (" + el + ")");
    Check(HintSize.Ellipsize("Коротко", 100, m6) == "Коротко" && HintSize.Ellipsize("", 10, m6) == "", "plate: a short line stays whole");
    // live 1.6.68.605: «В фильтре остался выбор от прошлой вещи — смен…» was cut to one line: now broken at spaces into up to 3 lines, «…» only past the third
    { string longT = "В фильтре остался выбор от прошлой вещи — смените комплект и главный стат";
      Func<string, double> m13 = t => 13.0 * t.Length;   // about the 26 px font of a 1920x1009 plate
      Func<string, double> m9 = t => 9.0 * t.Length;     // the 1136x640 plate's font
      foreach (var sz in new[] { new[] { 1920, 1009, 13 }, new[] { 1136, 640, 9 } }) {
        Func<string, double> m = sz[2] == 13 ? m13 : m9;
        double room = HintSize.MaxWidth(sz[0]) - 2 * HintSize.Pad(Ui.Unit(sz[0], sz[1]));
        var ls = HintSize.Wrap(longT, room, HintSize.MaxTextLines, m);
        bool fitsAll = true; foreach (var l in ls) if (m(l) > room) fitsAll = false;
        Check(ls.Length >= 1 && ls.Length <= 3 && fitsAll && string.Join(" ", ls) == longT, "plate on " + sz[0] + "x" + sz[1] + ": the long filter text is broken into " + ls.Length + " lines, whole, each within the width");
        var two = HintSize.Wrap(longT, 150, 2, m);
        Check(two.Length == 2 && two[1].EndsWith("…") && m(two[1]) <= 150 && m(two[0]) <= 150, "plate: text past the last line ends it with «…» (" + two[1] + ")");
      }
      var one = HintSize.Wrap("Коротко", 400, 3, m6);
      Check(one.Length == 1 && one[0] == "Коротко" && HintSize.Wrap("", 100, 3, m6).Length == 0, "plate: a short text is one line, an empty one none");
      var chop = HintSize.Wrap("Абвгдеёжзийклмнопрстуфхцчшщ", 60, 3, m6);
      Check(chop.Length == 3 && m6(chop[0]) <= 60 && m6(chop[1]) <= 60 && m6(chop[2]) <= 60, "plate: a word wider than the plate is broken by letters (" + string.Join("|", chop) + ")");
      // the plate beside the filter pop-up (1920x1009: panel x 504..1298, side panel to 1830): off it, inside the client, room 484 px at most
      foreach (var sz in new[] { new[] { 1920, 1009 }, new[] { 1136, 640 } }) {
        double Uf = Ui.Unit(sz[0], sz[1]);
        foreach (bool side in new[] { false, true }) {
          int px0 = (int)(sz[0] / 2 - 0.452 * Uf), px1 = (int)(sz[0] / 2 + (side ? 0.87 : 0.335) * Uf); int[] pop = { px0, 0, px1 - px0, sz[1] };
          int avail = Math.Max(pop[0], sz[0] - px1) - HintLayout.Gap - HintLayout.Margin, pw = Math.Min(HintSize.MaxWidth(sz[0]), Math.Max(160, avail)), ph = (int)(0.2 * Uf); char sd;
          var pos = HintLayout.Place(pop, null, pw, ph, sz[0], sz[1], out sd);
          bool offPop = pos[0] + pw <= pop[0] || pos[0] >= pop[0] + pop[2];
          Check(offPop && pos[0] >= 0 && pos[0] + pw <= sz[0] && pos[1] >= 0 && pos[1] + ph <= sz[1], "plate on " + sz[0] + "x" + sz[1] + (side ? " (side panel open)" : "") + ": beside the filter pop-up, not over it (" + pos[0] + "," + pos[1] + " " + pw + "x" + ph + ")");
        }
      }
    }
    // live 1.6.66.400 (rec2 frame_0141, 526x296): the compact plate beside «Инвентарь» (frame 111,246,44,44) lay over Одиссея / Демон..;
    // with the bar's row (button top to the client's bottom) to keep off, it goes above the row, near the button
    { char sd; int[] bt = { 111, 246, 44, 44 }; int[][] band = { new[] { 0, 244, 526, 52 } };
      var bp = HintLayout.Place(bt, band, 200, 24, 526, 296, out sd);
      Check(sd == 't' && bp[1] + 24 <= 244 - 4 && bp[0] < 111 + 44 && bp[0] + 200 > 111, "plate: a bar button's plate above the bar row, not over the next buttons (" + sd + " " + bp[0] + "," + bp[1] + ")"); }
    // lab 1.6.66.434: the hint of «Заменить» sometimes covered the button: whatever the live rect and the client, the plate never overlaps it (nor its frame), 1 layout a step
    { int bad = 0, n = 0; var rnd = new Random(434);
      foreach (var sz in new[] { new[] { 526, 296 }, new[] { 1366, 705 }, new[] { 1920, 1009 }, new[] { 1280, 720 } }) {
        int W = sz[0], Hh = sz[1]; double Uu = Ui.Unit(W, Hh);
        for (int i = 0; i < 200; i++) {
          int bw = (int)(0.2 * Uu + rnd.Next((int)(0.05 * Uu))), bh = (int)(0.07 * Uu + rnd.Next((int)(0.02 * Uu)));
          var cf = new[] { rnd.Next(W - bw), rnd.Next(Hh - bh), bw, bh };
          var fr = new[] { cf[0] - 2, cf[1] - 2, cf[2] + 4, cf[3] + 4 };   // (the frame is the rect 2 px out)
          int pw = (int)(HintSize.MaxWidth(W) * (0.5 + 0.5 * rnd.NextDouble())), ph = (int)(0.09 * Uu + rnd.Next((int)(0.05 * Uu))); char sd;
          var pos = HintLayout.Place(fr, null, pw, ph, W, Hh, out sd); n++;
          bool inside = pos[0] >= 0 && pos[1] >= 0 && pos[0] + pw <= W && pos[1] + ph <= Hh;
          bool over = pos[0] < cf[0] + cf[2] && pos[0] + pw > cf[0] && pos[1] < cf[1] + cf[3] && pos[1] + ph > cf[1];
          if (over && inside) bad++;
        }
      }
      Check(bad == 0, "hint of «Заменить»: the plate never overlaps the live button when it fits the client (" + n + " placements, " + bad + " over it)");
      var hc = new PlateHold(); int[] cr0 = { 600, 300, 160, 50 };
      hc.Made("Шаг 2 из 5 · Нагрудник|||1920x1009", cr0, null, 1f, new[] { 780, 300 }, 'r');
      Check(hc.Keep("Шаг 2 из 5 · Нагрудник|||1920x1009", new[] { 601, 299, 160, 50 }, null) && hc.Count == 1, "hint of «Заменить»: the held live rect 1 px off keeps the one layout (the step's count stays 1)"); }
    // the hold: a live target jittering by a pixel keeps the layout; another step, a moved target or a new neighbour lay it out again
    var h = new PlateHold(); int[] t0 = { 113, 240, 35, 35 };
    Check(!h.Keep("s1", t0, null), "plate hold: nothing held at first");
    h.Made("s1", t0, null, 1f, new[] { 160, 230 }, 'r');
    Check(h.Keep("s1", new[] { 114, 239, 35, 36 }, null) && h.Count == 1, "plate hold: the target 1 px off - the same layout (1 layout for the step)");
    Check(!h.Keep("s1", new[] { 120, 240, 35, 35 }, null) && !h.Keep("s2", t0, null) && !h.Keep("s1", t0, new[] { new[] { 10, 10, 20, 20 } }), "plate hold: moved, another step or a new neighbour - laid out again");
    h.Made("s1", new[] { 120, 240, 35, 35 }, null, 1f, new[] { 167, 230 }, 'r');
    bool fresh = h.Made("s2", t0, null, 1f, new[] { 160, 230 }, 'r');
    Check(fresh && h.LastStep == "s1" && h.LastCount == 2 && h.Count == 1, "plate hold: a new step - the one before counted (2 layouts)");
  }

  /// <summary>A managed object (its class, <paramref name="size"/> bytes) put in the xLua pool, as a Lua userdata; <paramref name="obj"/> = the object.</summary>
  static ulong FakeManaged(FakeUnity fu, string ns, string cls, int size, out ulong obj) {
    var m = fu.M; obj = m.Alloc(size); m.W64(obj, FakeKlass(m, ns, cls));
    int idx; ulong u = fu.Obj("X", "Y", 0, out idx);
    m.W64(fu.List + 0x20 + (ulong)(16 * idx) + 8, obj);
    return u;
  }
  /// <summary>A managed wrapper (class <paramref name="cls"/>, m_CachedPtr 0x10 = <paramref name="native"/>), its address.</summary>
  static ulong FakeWrap(FakeLua m, string ns, string cls, ulong native) { ulong o = m.Alloc(0x20); m.W64(o, FakeKlass(m, ns, cls)); m.W64(o + 0x10, native); return o; }
  /// <summary>A Dictionary&lt;int, ItemInstance&gt; of the grid: list index -> the cell's managed GameObject (stride 0x20; V2 0x28).</summary>
  static ulong FakeCellDict(FakeLua m, int stride, int[] keys, ulong[] mgos) {
    ulong d = m.Alloc(0x50), e = m.Alloc(0x20 + stride * keys.Length);
    m.W64(d + 0x18, e); m.W32(d + 0x20, keys.Length); m.W64(e + 0x18, (ulong)keys.Length);
    for (int i = 0; i < keys.Length; i++) { ulong at = e + 0x20 + (ulong)(stride * i); m.W32(at, keys[i] & 0x7FFFFFFF); m.W32(at + 8, keys[i]); m.W64(at + 0x18, mgos[i]); }
    return d;
  }

  /// <summary>Item cells read live (src/UiButtons.cs UiCellRead): pooled cells of a scrolled InfinityGrid mapped to their items by the bound index,
  /// the picture framed 2 px out, out of the viewport = hidden, the click point only wholly in view and fresh, the hero list's rows (V2) by column.</summary>
  static void UiCellTests() {
    Console.WriteLine("Item cells read live (src/UiButtons.cs UiCellRead):");
    var m = new FakeLua(4 << 20); RFX.TestMemory = m; RFX.ForgetLive(); RFX.ForgetClasses(); RFX.UiCellReset();
    try {
      var fu = new FakeUnity(m); var st = new FakeState(m);
      double W = 1280, H = 720, U = Ui.Unit(W, H), k = U / 640;
      Func<double, double, bool> Near = (x, y) => Math.Abs(x - y) < 0.05;
      var full = new[] { 0.0, 0 }; var one = new[] { 1.0, 1 }; var half = new[] { 0.5, 0.5 }; var tl = new[] { 0.0, 1 }; var tc = new[] { 0.5, 1 };
      ulong g0, gv, gc;
      ulong root = fu.Rect(0, full, one, full, half, full, 1, out g0);
      ulong vp = fu.Rect(root, half, half, new[] { 550.0, 300 }, half, full, 1, out gv);
      ulong content = fu.Rect(vp, tc, tc, new[] { 550.0, 1000 }, tc, new[] { 0.0, 150 }, 1, out gc);   // (local positions from the parent's pivot: its top at the viewport's)
      // 15 pooled cells (3 rows of 5) for list indices 0..14, each with c_Item -> c_Frame
      var cellT = new ulong[15]; var cellMgo = new ulong[15]; var keys = new int[15];
      for (int i = 0; i < 15; i++) {
        ulong go, gi, gf; int r = i / 5, cc = i % 5;
        cellT[i] = fu.Rect(content, tl, tl, new[] { 100.0, 100 }, tl, new[] { cc * 110.0 - 275, -r * 110.0 }, 1, out go);
        ulong it = fu.Rect(cellT[i], half, half, new[] { 90.0, 90 }, half, new[] { 50.0, -50 }, 1, out gi);
        ulong fr = fu.Rect(it, half, half, new[] { 80.0, 80 }, half, full, 1, out gf);
        fu.Name(gi, "c_Item"); fu.Name(gf, "c_Frame"); fu.Name(go, "Cell"); fu.Kids(cellT[i], it); fu.Kids(it, fr);
        cellMgo[i] = FakeWrap(m, "UnityEngine", "GameObject", go); keys[i] = i;
      }
      ulong gridObj; ulong gud = FakeManaged(fu, "UI", "InfinityGrid", 0x140, out gridObj);
      m.W64(gridObj + 0xC8, FakeWrap(m, "UnityEngine", "RectTransform", vp));
      m.W32(gridObj + 0xD0, 0); m.W32(gridObj + 0xD4, 14);
      m.W64(gridObj + 0xD8, FakeCellDict(m, 0x20, keys, cellMgo));
      var uids = new List<object>(); for (int i = 0; i < 20; i++) { uids.Add((long)(i + 1)); uids.Add((long)(101 + i)); }
      var panel = st.T("m_FilterAndSortEquipUidList", st.T(uids.ToArray()));
      ulong csObj, csGo; ulong csui = fu.UiBase(g0, out csObj, out csGo);
      var form = st.T("class", st.T("__cname", "Form_BackpackIntegration"), "m_LuaLogicActive", true, "m_csui", 0L, "m_Content_InfinityGrid", 0L, "m_CachedContentPanels", st.T(-9998L, panel));
      SetField(st, form, "m_csui", csui, 0x47); SetField(st, form, "m_Content_InfinityGrid", gud, 0x47);
      var ui = st.T(1L, form);
      double vx = W / 2 - 275 * k, vy = H / 2 - 150 * k;
      var s = RFX.UiCellRead(fu.Env, ui.A, UiCellKind.Inventory, new long[] { 102, 113 }, W, H, U);
      var c = s.Ok ? s.Of(102) : null;
      Check(s.Ok && s.Created == 15 && s.Cells.Count == 2 && Near(s.VX, vx) && Near(s.VY, vy) && Near(s.VW, 550 * k), "inventory: 15 cells made, the 2 wanted kept, the viewport read (" + (s.Ok ? "" : s.FailStep + " " + s.FailValue + " " + s.Trace) + ")");
      Check(c != null && c.Icon == "c_Frame" && c.Index == 1 && Near(c.X, vx + 120 * k) && Near(c.Y, vy + 10 * k) && Near(c.W, 80 * k) && c.InView == 1, "  ... item 102 = index 1: its c_Frame's rect (" + (c != null ? c.X + "," + c.Y + " " + c.W : "none") + ")");
      int now = s.At; double[] fr0; string why;
      Check(RFX.UiCellState(s, 102, W, H, now, out fr0, out why) == UiCellSee.Shown && Near(fr0[0], c.BX - 2) && Near(fr0[2], c.BW + 4) && fr0[4] > 0 && c.BX <= c.X && c.BX + c.BW >= c.X + c.W && c.BY + c.BH >= c.Y + c.H, "  ... framed 2 px round the whole cell (c_Item with its picture), rounded");
      double px, py;
      Check(RFX.UiCellPoint(s, 102, W, H, now, out px, out py, out why) && Near(px, c.X + c.W / 2) && Near(py, c.Y + c.H / 2), "  ... the click point is its picture's centre");
      var c13 = s.Of(113);
      Check(c13 != null && c13.InView > 0.5 && c13.InView < 0.95 && RFX.UiCellState(s, 113, W, H, now, out fr0, out why) == UiCellSee.Shown && !RFX.UiCellPoint(s, 113, W, H, now, out px, out py, out why) && fr0[1] + fr0[3] <= vy + 300 * k + 2.01,
        "  ... row 3 partly in view: framed (clipped to the viewport), not clicked (" + why + ")");
      Check(RFX.UiCellState(s, 102, W, H, now + 250, out fr0, out why) == UiCellSee.Unknown && !RFX.UiCellPoint(s, 102, W, H, now + 250, out px, out py, out why), "  ... a read 250 ms old: unknown (the layout's), no live click");
      Check(RFX.UiCellState(s, 102, 1000, H, now, out fr0, out why) == UiCellSee.Unknown, "  ... another window size: unknown");
      Check(RFX.UiCellState(s, 150, W, H, now, out fr0, out why) == UiCellSee.Hidden, "  ... an item with no cell made: hidden");
      // scrolled down a row: index 1 out of the viewport, row 3 wholly in
      WF(m, fu.Trs(content), 0, 260);
      s = RFX.UiCellRead(fu.Env, ui.A, UiCellKind.Inventory, new long[] { 102, 113 }, W, H, U); now = s.At;
      c13 = s.Of(113);
      Check(RFX.UiCellState(s, 102, W, H, now, out fr0, out why) == UiCellSee.Hidden && c13 != null && Near(c13.Y, vy + 120 * k) && RFX.UiCellPoint(s, 113, W, H, now, out px, out py, out why),
        "scroll: the frame follows the content (row 3 at " + (c13 != null ? c13.Y.ToString("0.#") : "?") + "), a row scrolled out is hidden: " + why);
      // the pool binds the cell of index 0 again to index 15 (item 116) a row below: found by its bound index
      WF(m, fu.Trs(cellT[0]), -275, -330);
      keys[0] = 15; m.W64(gridObj + 0xD8, FakeCellDict(m, 0x20, keys, cellMgo)); m.W32(gridObj + 0xD0, 1); m.W32(gridObj + 0xD4, 15);
      s = RFX.UiCellRead(fu.Env, ui.A, UiCellKind.Inventory, null, W, H, U); now = s.At;
      var c16 = s.Of(116);
      Check(s.Ok && s.Of(101) == null && c16 != null && c16.Index == 15 && Near(c16.Y, vy + (330 - 110 + 10) * k), "pool: a cell bound again shows its new item where it is now (" + (s.Ok ? "" : s.FailStep) + ")");
      m.W32(gridObj + 0xD4, 10);
      s = RFX.UiCellRead(fu.Env, ui.A, UiCellKind.Inventory, null, W, H, U);
      Check(!s.Ok && s.FailStep == "m_crtItems", "a cell index outside the grid's first..last: the read is refused (" + s.FailValue + ")");
      m.W32(gridObj + 0xD4, 15);
      // the shadow line (the pilot's first minute) and the form on top
      s = RFX.UiCellRead(fu.Env, ui.A, UiCellKind.Inventory, null, W, H, U); now = s.At;
      string l1 = RFX.UiCellShadowLine("t:1", s, 116, new[] { c16.X + 10, c16.Y, c16.W, c16.H }, now), l2 = RFX.UiCellShadowLine("t:1", s, 116, new[] { c16.X + 10, c16.Y, c16.W, c16.H }, now + 1500),
             l3 = RFX.UiCellShadowLine("t:1", s, 116, new[] { c16.X + 90, c16.Y, c16.W, c16.H }, now + 61000);
      Check(l1 != null && l1.StartsWith("uibtn cell: Inventory 116 live=[") && l1.Contains(" calc=[") && l1.Contains(" diff=10px") && l2 == null && l3 == null, "shadow: one line with the diff, none when nothing changed, none after the first minute: " + l1);
      Check(RFX.UiCellTopIs(UiCellKind.Inventory, new ScreenState { Kind = ScreenKind.Inventory, Top = "Form_BackpackIntegration" }) && !RFX.UiCellTopIs(UiCellKind.Inventory, new ScreenState { Kind = ScreenKind.Inventory, Top = "Form_BackpackIntegration", OfferCount = 1 })
        && !RFX.UiCellTopIs(UiCellKind.Ascend, new ScreenState { Kind = ScreenKind.Inventory, Top = "Form_BackpackIntegration" }) && RFX.UiCellTopIs(UiCellKind.HeroGear, new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorEquipTip" }),
        "top: the list's form on top, no pop-up (the hero list under the equip tip still)");
      SetField(st, form, "m_LuaLogicActive", 0, 1);
      s = RFX.UiCellRead(fu.Env, ui.A, UiCellKind.Inventory, null, W, H, U);
      Check(s.Ok && !s.Visible && RFX.UiCellState(s, 116, W, H, s.At, out fr0, out why) == UiCellSee.Unknown, "the form not shown: unknown (the layout's)");
      // the hero's gear list (InfinityGridV2): a row cell, the items in c_Equip1..N, the uid by row and column
      {
        ulong h0, hv, hc, hg; ulong hroot = fu.Rect(0, full, one, full, half, full, 1, out h0);
        ulong hvp = fu.Rect(hroot, half, half, new[] { 400.0, 400 }, half, full, 1, out hv);
        ulong hcont = fu.Rect(hvp, tc, tc, new[] { 400.0, 800 }, tc, new[] { 0.0, 200 }, 1, out hc);
        ulong row = fu.Rect(hcont, tl, tl, new[] { 400.0, 120 }, tl, new[] { -200.0, 0 }, 1, out hg);
        var kids = new List<ulong>();
        for (int i = 1; i <= 3; i++) { ulong ge, gf; ulong e = fu.Rect(row, tl, tl, new[] { 100.0, 100 }, tl, new[] { 10 + (i - 1) * 120.0, -10 }, 1, out ge); ulong f2 = fu.Rect(e, half, half, new[] { 90.0, 90 }, half, new[] { 50.0, -62 }, 1, out gf); fu.Name(ge, "c_Equip" + i); fu.Name(gf, "c_Frame");
          if (i == 3) { ulong gs; ulong sel = fu.Rect(e, tl, tl, new[] { 100.0, 124 }, tl, new[] { 0.0, -12 }, 1, out gs); fu.Name(gs, "c_EquipSelected"); fu.Kids(e, f2, sel); } else fu.Kids(e, f2);   // (the item root stands 17 px above its picture; item 3 shows the game's own selection frame)
          kids.Add(e); }
        fu.Kids(row, kids.ToArray());
        ulong g2; ulong gud2 = FakeManaged(fu, "UI", "InfinityGridV2", 0x140, out g2);
        m.W64(g2 + 0xF0, FakeWrap(m, "UnityEngine", "RectTransform", hvp)); m.W32(g2 + 0xF8, 0); m.W32(g2 + 0xFC, 1);
        m.W64(g2 + 0x100, FakeCellDict(m, 0x28, new[] { 1 }, new[] { FakeWrap(m, "UnityEngine", "GameObject", hg) }));
        var data = st.T(1L, st.T("Type", 2L), 2L, st.T("Type", 1L, "Item", st.T(1L, 201L, 2L, 202L, 3L, 203L)));
        var ep = st.T("m_EquipListRealData", data, "m_EquipList_InfinityGrid", 0L);
        SetField(st, ep, "m_EquipList_InfinityGrid", gud2, 0x47);
        ulong o2, mg2; ulong cs2 = fu.UiBase(h0, out o2, out mg2);
        var hero = st.T("class", st.T("__cname", "Form_CharactorMain"), "m_LuaLogicActive", true, "m_csui", 0L, "m_Panels", st.T("EquipFilter", ep));
        SetField(st, hero, "m_csui", cs2, 0x47);
        var ui2 = st.T(1L, hero);
        var hs = RFX.UiCellRead(fu.Env, ui2.A, UiCellKind.HeroGear, new long[] { 202 }, W, H, U);
        var hc2 = hs.Ok ? hs.Of(202) : null;
        Check(hc2 != null && hc2.Col == 2 && hc2.Index == 1 && Near(hc2.X, W / 2 - 200 * k + (130 + 5) * k) && Near(hc2.W, 90 * k) && hs.Cells.Count == 1,
          "hero gear (V2): row 2's second item from c_Equip2's c_Frame (" + (hs.Ok ? "" : hs.FailStep + " " + hs.FailValue) + ")");
        // the cell's frame never reaches above its picture (the row above's plate), and the game's own selection frame is the cell where there is one
        Check(hc2 != null && hc2.BY >= hc2.Y - 0.01 && hc2.BY + hc2.BH >= hc2.Y + hc2.H - 0.01 && hc2.BH >= hc2.H - 0.01,
          "hero gear cell: the frame box starts at the picture's top, not above it (the item root stood 17 px higher): " + (hc2 != null ? hc2.BY + " vs " + hc2.Y : "none"));
        var hs3 = RFX.UiCellRead(fu.Env, ui2.A, UiCellKind.HeroGear, new long[] { 203 }, W, H, U); var hc3 = hs3.Ok ? hs3.Of(203) : null;
        Check(hc3 != null && Math.Abs(hc3.BY - (hc3.Y - 5 * k)) < 0.05 && Math.Abs(hc3.BH - 124 * k * RFX.SelectedH) < 0.05 && Math.Abs(hc3.BW - 100 * k) < 0.05,
          "hero gear cell: the game's c_EquipSelected rect is the frame's cell (" + (hc3 != null ? hc3.BY + "," + hc3.BH : "none") + ")");
        // the cell layout: rows 130 px apart (picture 90 + plate), the frame of a cell never meets the row above
        {
          var up = new UiCell { X = 100, Y = 100, W = 90, H = 90, BX = 95, BY = 100 - 17, BW = 100, BH = 124 + 17 };   // a root 17 px above its picture
          var above = new UiCell { X = 100, Y = -30, W = 90, H = 90, BX = 100, BY = -30, BW = 90, BH = 120 };   // the row above: picture to y 60, its plate to 90
          RFX.CellClampTop(up);
          Check(up.BY >= above.BY + above.BH && up.BY >= 100 - 0.01 && up.BY + up.BH >= 190, "hero gear cell layout: the clamped frame (top " + up.BY + ") does not intersect the plate of the row above (bottom " + (above.BY + above.BH) + ")");
          var bad = new UiCell { X = 100, Y = 100, W = 90, H = 90 }; bad.BX = bad.X; bad.BY = bad.Y; bad.BW = bad.W; bad.BH = bad.H;
          {   // the recorded cell (1.6.66.440, 1366x705): selection rect x 112 y 247 w 88 h 115; its lower border at 359.5 -> 112 high, the frame 2 px out: y 245 h 116
          var rc = new UiCell { X = 112, Y = 247, W = 88, H = 88 }; rc.BX = rc.X; rc.BY = rc.Y; rc.BW = rc.W; rc.BH = rc.H;
          Check(RFX.CellSelected(rc, 112, 247, 88, 115) && Math.Abs(rc.BH - 112) < 0.01 && rc.BY == 247 && rc.BX == 112 && rc.BW == 88 && Math.Abs(UiIcon.Frame(rc.BX, rc.BY, rc.BW, rc.BH, 1366, 705)[3] - 116) < 0.01,
            "item cell: the selection rect's lower 3 px are not the cell (112 of 115 high, as measured on the recording): " + rc.BH);
          Check(Math.Abs(RFX.HeroCardW * 81 - 75.4) < 0.01 && Math.Abs(210 + 81 * RFX.HeroCardW - 285.4) < 0.01, "hero card: 75.4 of the root's 81 px wide (root x 210..291 -> the card's edges 210.8 and 286.1 measured)");
        }
        Check(!RFX.CellSelected(bad, 100, 20, 90, 300) && !RFX.CellSelected(bad, 400, 100, 90, 90) && RFX.CellSelected(bad, 98, 96, 94, 120) && bad.BY == 96, "hero gear cell: an unusable selection rect (too tall, beside the picture) is not the cell; a fitting one is");
        }
        Check(ConfirmFrame.FormsReadMs(true, 2000) == 100 && ConfirmFrame.FormsReadMs(true, 500) == 100 && ConfirmFrame.FormsReadMs(false, 2000) == 2000, "«Заменить» hint on the hero screen: the forms (the item tip's flow) are read every 100 ms, not every 2 s");
        Check(ConfirmFrame.Ready(true, true) && !ConfirmFrame.Ready(true, false) && !ConfirmFrame.Ready(false, true), "«Заменить»: the frame stands from the first live rect on the shown tip, never without the tip or the live rect");
        { var ls = new LiveStill(); var rc = new double[] { 470, 568, 139, 38 };
          Check(!ls.Note(rc, 1000) && ls.Fresh && ConfirmFrame.Ready(ls.Fresh, true) && !ls.Note(rc, 1050) && ls.Note(rc, 1100), "«Заменить»: the frame is ready at the first read (Fresh), the click waits for the confirming read 100 ms later"); }
      }
    } finally { RFX.TestMemory = null; RFX.ForgetLive(); RFX.UiCellReset(); }
  }

  static void UiButtonTests() {
    UiIconTests(); HintSizeTests(); SpriteArtTests(); FrameTargetTests(); OwnMaskTests();
    Console.WriteLine("A button's live rect by name (src/UiButtons.cs):");
    var m = new FakeLua(4 << 20); RFX.TestMemory = m; RFX.ForgetLive(); RFX.ForgetClasses();
    try {
      var fu = new FakeUnity(m); var st = new FakeState(m);
      double W = 1280, H = 720, U = Ui.Unit(W, H), k = U / 640, cw = W / k, ch = H / k;
      ulong g0, gA, gB, gW;
      var full = new[] { 0.0, 0 }; var one = new[] { 1.0, 1 }; var half = new[] { 0.5, 0.5 };
      ulong root = fu.Rect(0, full, one, full, half, full, 1, out g0);
      ulong a = fu.Rect(root, full, one, full, half, full, 1, out gA);
      ulong b = fu.Rect(a, new[] { 0.0, 1 }, new[] { 0.0, 1 }, new[] { 200.0, 80 }, new[] { 0.0, 1 }, new[] { -cw / 2 + 10, ch / 2 - 5 }, 1, out gB);
      ulong wt = fu.Rect(b, half, half, new[] { 60.0, 50 }, half, new[] { 40.0, -40 }, 1, out gW);
      int iBtn, iBack;
      ulong csObj, csGo;
      ulong csui = fu.UiBase(g0, out csObj, out csGo), btn = fu.Obj("UnityEngine.UI", "Button", fu.Comp(wt), out iBtn), back = fu.Obj("UnityEngine", "GameObject", gB, out iBack);
      var bb = st.T("m_btnBack", 0L);
      var hero = st.T("class", st.T("__cname", "Form_CharactorMain"), "m_LuaLogicActive", true, "m_csui", 0L, "m_ButtonBack", bb, "m_Back", 0L);
      SetField(st, hero, "m_csui", csui, 0x47); SetField(st, bb, "m_btnBack", btn, 0x47); SetField(st, hero, "m_Back", back, 0x47);
      var city = st.T("class", st.T("__cname", "Form_MainCity"), "m_LuaLogicActive", true);
      var ui = st.T(1L, city, 2L, hero);
      var wBack = RFX.UiWidgetOf(ClickTarget.HeroBack, null);
      Func<double, double, bool> Near = (x, y) => Math.Abs(x - y) < 0.01;
      string line; var r = RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out line);
      Check(r.Ok && Near(r.X, 22.5) && Near(r.Y, 22.5) && Near(r.W, 67.5) && Near(r.H, 56.25) && r.Visible && !r.Rotated, "HeroBack: the Button's rect from the chain to the form's root: " + line);
      Check(line.StartsWith("uibtn: Form_CharactorMain.m_ButtonBack.m_btnBack live=[22,22,68,56] layout=[56,51] diff=0px visible=T scale=unit UnityEngine.UI.Button in "), "  ... the log line: " + line);
      Check(r.IW == 0, "no icon looked for on a plain button");
      {   // a bar button (Icon): its picture child, the label under it left out
        ulong gI, gL, gD;
        ulong ic = fu.Rect(wt, half, half, new[] { 40.0, 40 }, half, new[] { 0.0, 6 }, 1, out gI);
        ulong lb = fu.Rect(wt, half, half, new[] { 60.0, 12 }, half, new[] { 0.0, -24 }, 1, out gL);
        ulong dt = fu.Rect(ic, half, half, new[] { 8.0, 8 }, half, new[] { 18.0, 18 }, 1, out gD);
        fu.Name(gI, "Image_Backpack"); fu.Name(gL, "Text_Backpack"); fu.Name(gD, "RedDot"); fu.Kids(wt, lb, ic); fu.Kids(ic, dt);
        var wi = new UiWidget("Form_CharactorMain", "m_ButtonBack.m_btnBack") { Icon = true };
        r = RFX.UiButtonForTest(fu.Env, ui.A, wi, 56, 51, W, H, U, out line);
        Check(r.Ok && Near(r.X, 22.5) && Near(r.W, 67.5) && r.IconName == "Image_Backpack" && Near(r.IX, 33.75) && Near(r.IY, 21.375) && Near(r.IW, 45) && Near(r.IH, 45),
          "icon: the bar button's picture child read live (" + r.IconName + " " + r.IX + "," + r.IY + " " + r.IW + "x" + r.IH + "), the click rect stays the button's: " + line);
        Check(line.Contains(" icon=Image_Backpack[34,21,45,45] "), "  ... the log line names it: " + line);
        var fr = UiIcon.Frame(r.IX, r.IY, r.IW, r.IH, W, H);
        Check(Near(fr[0], 31.75) && Near(fr[3], 49) && fr[1] + fr[3] < 50.625 + 24 * 1.125 - 6.75, "icon: the frame hugs the picture and stays above the label");
        fu.Kids(wt);
        r = RFX.UiButtonForTest(fu.Env, ui.A, wi, 56, 51, W, H, U, out line);
        Check(r.Ok && r.IW == 0, "icon: no children - none (the button's rect frames it)");
      }
      r = RFX.UiButtonForTest(fu.Env, ui.A, new UiWidget("Form_CharactorMain", "m_Nope", "m_Back"), 0, 0, W, H, U, out line);
      Check(r.Ok && r.Widget == "m_Back" && Near(r.X, 11.25) && Near(r.Y, 5.625) && Near(r.W, 225) && Near(r.H, 90) && r.ClassName == "UnityEngine.GameObject", "a GameObject field, the second path (pivot top left): " + line);
      Check(line.Contains(" outside "), "  ... a layout point outside the live rect is flagged: " + line);
      // shown / active
      m.Mem[gW - FakeLua.Base + 0x57] = 0;
      Check(!RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out line).Visible, "inactive in the hierarchy (cached flag 0): visible=F: " + line);
      m.Mem[gW - FakeLua.Base + 0x57] = 0xFF;
      Check(RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out line).Visible, "flag not computed: every activeSelf up to the root is on: visible=T");
      m.Mem[gA - FakeLua.Base + 0x56] = 0;
      Check(!RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out line).Visible, "  ... a parent switched off: visible=F");
      m.Mem[gA - FakeLua.Base + 0x56] = 1; m.Mem[gW - FakeLua.Base + 0x57] = 1;
      SetField(st, hero, "m_LuaLogicActive", 0, 1);
      Check(!RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out line).Visible, "the form not shown: visible=F");
      SetField(st, hero, "m_LuaLogicActive", 1, 1);
      // broken links: the step is named
      Action<string, Action, Action> Broken = (step, brk, fix) => {
        brk(); string ln; var x = RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out ln);
        Check(!x.Ok && x.FailStep == step && ln.Contains(" failed at " + step + " ("), "broken " + step + ": " + ln); fix();
      };
      r = RFX.UiButtonForTest(fu.Env, ui.A, new UiWidget("Form_Nope", "m_Back"), 0, 0, W, H, U, out line);
      Check(!r.Ok && r.FailStep == "form" && line == "uibtn: Form_Nope.m_Back failed at form (not in UIInstance) in " + r.Ms + " ms", "a form not in UIInstance: " + line);
      Broken("field", () => SetField(st, bb, "m_btnBack", 5, 0x13), () => SetField(st, bb, "m_btnBack", btn, 0x47));
      ulong slot = fu.List + 0x20 + (ulong)(16 * iBtn) + 8, obj = m.R64(slot);
      Broken("slot widget", () => m.W64(slot, 0), () => m.W64(slot, obj));
      Broken("m_CachedPtr widget", () => m.W64(obj + 0x10, 0), () => m.W64(obj + 0x10, fu.Comp(wt)));
      ulong tr = m.R64(fu.Env + 0x20);
      Broken("translator", () => m.W64(fu.Env + 0x20, fu.Env), () => m.W64(fu.Env + 0x20, tr));
      Broken("transform widget", () => m.W64(wt + 0x30, gA), () => m.W64(wt + 0x30, gW));
      Broken("parents", () => m.W64(a + 0x90, 0), () => m.W64(a + 0x90, root));
      Broken("rect fields", () => WF(m, b + 0xC8, 5), () => WF(m, b + 0xC8, 0));
      Broken("userdata widget", () => m.W64(btn + 0x18, 8), () => m.W64(btn + 0x18, 4));
      Check(RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out line).Ok, "  ... all fixed again: ok: " + line);
      // rotation flagged
      WF(m, fu.Trs(wt) + 0x10, 0, 0, 0.38, 0.92);
      Check(RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out line).Rotated && line.Contains(" rotated "), "a rotated transform is flagged: " + line);
      WF(m, fu.Trs(wt) + 0x10, 0, 0, 0, 1);
      // the form's C# side is a Form_LuaBase (a plain C# object; live 2026-10-30 every read stopped at "m_CachedPtr m_csui (Form_LuaBase destroyed)")
      Check(m.R64(csObj + 0x10) == 0 && RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out line).Ok, "Form_LuaBase (m_param 0x10 empty): the root from m_uiGameObject: " + line);
      Broken("m_csui", () => m.Mem[csObj - FakeLua.Base + 0xF8] = 1, () => m.Mem[csObj - FakeLua.Base + 0xF8] = 0);
      Broken("m_uiGameObject", () => m.W64(csGo + 0x10, 0), () => m.W64(csGo + 0x10, g0));
      Broken("m_uiGameObject", () => m.W64(csObj + 0xB8, 0), () => m.W64(csObj + 0xB8, csGo));
      Broken("window", () => { W = 0; }, () => { W = 1280; });
      // the city rebuilt (after the guild): the old copy still in UIInstance, shown, its C# side Destroyed - the new copy is read
      {
        ulong oObj, oGo, nObj, nGo, og, ng;
        ulong oRoot = fu.Rect(0, full, one, full, half, full, 1, out og);
        ulong gx;
        ulong oBtn = fu.Rect(oRoot, half, half, new[] { 40.0, 40 }, half, new[] { -100.0, 0 }, 1, out gx);
        ulong nRoot = fu.Rect(0, full, one, full, half, full, 1, out ng);
        ulong nBtn = fu.Rect(nRoot, half, half, new[] { 40.0, 40 }, half, new[] { 100.0, 0 }, 1, out gx);
        int io, inn;
        ulong oCs = fu.UiBase(og, out oObj, out oGo), nCs = fu.UiBase(ng, out nObj, out nGo);
        ulong oB = fu.Obj("UnityEngine.UI", "Button", fu.Comp(oBtn), out io), nB = fu.Obj("UnityEngine.UI", "Button", fu.Comp(nBtn), out inn);
        var oldCity = st.T("class", st.T("__cname", "Form_MainCity"), "m_LuaLogicActive", true, "m_csui", 0L, "m_Button_Backpack", 0L);
        var newCity = st.T("class", st.T("__cname", "Form_MainCity"), "m_LuaLogicActive", true, "m_csui", 0L, "m_Button_Backpack", 0L);
        SetField(st, oldCity, "m_csui", oCs, 0x47); SetField(st, oldCity, "m_Button_Backpack", oB, 0x47);
        SetField(st, newCity, "m_csui", nCs, 0x47); SetField(st, newCity, "m_Button_Backpack", nB, 0x47);
        m.Mem[oObj - FakeLua.Base + 0xF8] = 1;
        var ui2 = st.T(1L, oldCity, 2L, newCity, 3L, hero);
        var wInv = RFX.UiWidgetOf(ClickTarget.CityInventory, null);
        r = RFX.UiButtonForTest(fu.Env, ui2.A, wInv, 0, 0, W, H, U, out line);
        Check(r.Ok && r.FormTable == newCity.A && r.Copies == 1 && Near(r.X + r.W / 2, W / 2 + 100 * k), "a rebuilt form: the live copy is read, the Destroyed one passed over: " + line);
        m.Mem[oObj - FakeLua.Base + 0xF8] = 0;
        r = RFX.UiButtonForTest(fu.Env, ui2.A, wInv, 0, 0, W, H, U, out line);
        double px0, py0; string why0;
        Check(r.Ok && r.Copies == 2 && line.Contains(" copies=2") && !RFX.UiLivePoint(r, W, H, r.At, out px0, out py0, out why0) && why0.Contains("copies"),
          "two shown live copies of the form: the instance is not certain, no live click: " + line);
        m.W64(oGo + 0x10, 0);
        r = RFX.UiButtonForTest(fu.Env, ui2.A, wInv, 0, 0, W, H, U, out line);
        Check(r.Ok && r.FormTable == newCity.A && r.Copies == 1, "  ... the old copy's GameObject destroyed: the new one, certain: " + line);
      }
      // a small window: the scale from the root canvas (its size against the client), whatever Ui.Unit says - live 2026-10-30: 526x296
      {
        double w2 = 526, h2 = 296, kc = w2 / 1136, ccw = 1136, cch = h2 / kc;   // a CanvasScaler matching the width: 1136 units wide
        ulong gc, gr, gb;
        ulong canv = fu.Rect(0, full, full, new[] { ccw, cch }, half, full, kc, out gc);
        ulong froot = fu.Rect(canv, full, one, full, half, full, 1, out gr);
        ulong fbtn = fu.Rect(froot, half, half, new[] { 60.0, 40 }, half, new[] { 100.0, -50 }, 1, out gb);
        ulong cObj, cGo; ulong cCs = fu.UiBase(gr, out cObj, out cGo); int ib;
        ulong cB = fu.Obj("UnityEngine.UI", "Button", fu.Comp(fbtn), out ib);
        var bag = st.T("class", st.T("__cname", "Form_BackpackIntegration"), "m_LuaLogicActive", true, "m_csui", 0L, "m_PanelContentCur", st.T("m_BtnWorkShop", 0L));
        SetField(st, bag, "m_csui", cCs, 0x47);
        SetField(st, new FTab(m.R64(st.ValueAt(bag, "m_PanelContentCur"))), "m_BtnWorkShop", cB, 0x47);
        var ui3 = st.T(1L, bag);
        var wF = RFX.UiWidgetOf(ClickTarget.FoundryButton, null);
        r = RFX.UiButtonForTest(fu.Env, ui3.A, wF, 0, 0, w2, h2, 0, out line);
        Check(r.Ok && r.Scale == "canvas" && Near(r.X + r.W / 2, w2 / 2 + 100 * kc) && Near(r.Y + r.H / 2, h2 / 2 + 50 * kc) && Near(r.W, 60 * kc) && Near(r.H, 40 * kc) && line.Contains(" scale=canvas"),
          "526x296: the rect from the root canvas's scale (no unit needed): " + line);
        r = RFX.UiButtonForTest(fu.Env, ui3.A, wF, 0, 0, w2, h2, Ui.Unit(w2, h2), out line);
        Check(r.Ok && r.Scale == "canvas" && Near(r.W, 60 * kc), "  ... the canvas wins over Ui.Unit (" + (Ui.Unit(w2, h2) / 640).ToString("0.###") + " vs " + kc.ToString("0.###") + " px/unit)");
        r = RFX.UiButtonForTest(fu.Env, ui3.A, wF, 0, 0, 600, 296, Ui.Unit(600, 296), out line);
        Check(r.Ok && r.Scale == "unit", "  ... a canvas of another shape than the client (not resized yet) is not taken: the unit: " + line);
        // the click point: fresh, visible, inside, one copy
        r = RFX.UiButtonForTest(fu.Env, ui3.A, wF, 0, 0, w2, h2, 0, out line);
        double px, py; string why;
        Check(RFX.UiLivePoint(r, w2, h2, r.At + 150, out px, out py, out why) && Near(px, w2 / 2 + 100 * kc) && Near(py, h2 / 2 + 50 * kc), "live point: the rect's centre when fresh (" + why + ")");
        Check(!RFX.UiLivePoint(r, w2, h2, r.At + RFX.UiFreshMs + 1, out px, out py, out why) && why.StartsWith("old"), "  ... not when older than 200 ms: " + why);
        Check(RFX.UiLivePoint(r, w2, h2, r.At - 16, out px, out py, out why) && Near(px, w2 / 2 + 100 * kc), "  ... a read that finished 16 ms AFTER the caller's clock is fresh (live 582 why=old (-16 ms)): " + why);
        Check(!RFX.UiLivePoint(r, w2, h2, r.At - RFX.UiFutureMs - 1, out px, out py, out why) && why.StartsWith("old"), "  ... a read from far in the future is not: " + why);
        Check(!RFX.UiLivePoint(r, 1280, 720, r.At, out px, out py, out why) && why == "another window size", "  ... not for another window size");
        Check(!RFX.UiLivePoint(null, w2, h2, 0, out px, out py, out why), "  ... no read: the layout");
        WF(m, fu.Trs(fbtn), 900, -50, 0);
        r = RFX.UiButtonForTest(fu.Env, ui3.A, wF, 0, 0, w2, h2, 0, out line);
        Check(!RFX.UiLivePoint(r, w2, h2, r.At, out px, out py, out why) && why.Contains("outside"), "  ... not when its centre is outside the client: " + why);
        WF(m, fu.Trs(fbtn), 100, -50, 0);
        // the kept read: fresh for the clicks / frames of this button and size only
        r = RFX.UiButtonForTest(fu.Env, ui3.A, wF, 0, 0, w2, h2, 0, out line);
        RFX.UiButtonKeep(ClickTarget.FoundryButton, r, w2, h2, 5000);
        Check(RFX.UiButtonFresh(ClickTarget.FoundryButton, null, w2, h2, 5100, RFX.UiFreshMs) == r && RFX.UiButtonFresh(ClickTarget.FoundryButton, null, w2, h2, 5300, RFX.UiFreshMs) == null
          && RFX.UiButtonFresh(ClickTarget.FoundryButton, null, 1280, 720, 5100, RFX.UiFreshMs) == null && RFX.UiButtonFresh(ClickTarget.AscendTab, null, w2, h2, 5100, RFX.UiFreshMs) == null,
          "kept read: fresh for 200 ms, this size and button only");
        RFX.UiButtonKeep(ClickTarget.FoundryButton, new UiButtonInfo { FailStep = "form" }, w2, h2, 5150);
        Check(RFX.UiButtonFresh(ClickTarget.FoundryButton, null, w2, h2, 5160, RFX.UiFreshMs) == null, "  ... a failed read drops it");
        var ro = new UiButtonInfo { Form = "Form_A", Visible = true, W = 5, H = 5 }; RFX.UiButtonKeep(ClickTarget.OfferClose, ro, w2, h2, 5000);
        Check(RFX.UiButtonFresh(ClickTarget.OfferClose, "Form_A", w2, h2, 5010, 200) == ro && RFX.UiButtonFresh(ClickTarget.OfferClose, "Form_B", w2, h2, 5010, 200) == null, "  ... OfferClose: for the same pop-up only");
      }
      // the form on top: no live click into a covering screen
      {
        var inv = new ScreenState { Kind = ScreenKind.Inventory, Top = "Form_BackpackIntegration" };
        var boss = new ScreenState { Kind = ScreenKind.Unknown, Top = "Form_UnionBoss" };
        var tip = new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorMain", Flow = "Form_CharactorEquipTip" };
        var off = new ScreenState { Kind = ScreenKind.City, Top = "Form_X", Offer = "Form_X", OfferCount = 1, ModalOpen = true };
        Check(RFX.UiTopIs(ClickTarget.FoundryButton, null, inv) && !RFX.UiTopIs(ClickTarget.FoundryButton, null, boss) && !RFX.UiTopIs(ClickTarget.FoundryButton, null, null)
          && !RFX.UiTopIs(ClickTarget.FoundryButton, null, new ScreenState { Kind = ScreenKind.Inventory, Top = "Form_BackpackIntegration", Held = true }), "top: «Литейная» only on the inventory on top (not under Form_UnionBoss, not on a held read)");
        Check(RFX.UiTopIs(ClickTarget.EquipConfirm, null, tip) && !RFX.UiTopIs(ClickTarget.EquipConfirm, null, new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorMain" }), "top: «Заменить» on the item's tip over the hero screen");
        // lab 1.6.66.434 (test 2): the tip is the equip pilot's flow form above the hero screen - it IS the top form of «Заменить», named on top or as a flow form
        var tipTop = new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorEquipTip", Flow = "Form_CharactorEquipTip" };
        var tipTopOnly = new ScreenState { Kind = ScreenKind.Unknown, Top = "Form_CharactorEquipTip" };
        var tipOver = new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorMain", Flow = "Form_CharactorEquipTip,Form_EquipedCompared" };
        var tipPop = new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_X", Offer = "Form_X", OfferCount = 1, Flow = "Form_CharactorEquipTip" };
        Check(RFX.UiTopIs(ClickTarget.EquipConfirm, null, tipTop) && RFX.UiTopIs(ClickTarget.EquipConfirm, null, tipTopOnly) && RFX.UiTopIs(ClickTarget.EquipConfirm, null, tipOver),
          "top: the equip pilot's flow form (the tip) is the top form of «Заменить», as the named top or as a flow form with the compare card");
        { var fst = new FrameSteady(); var lv0 = new double[] { 400, 300, 120, 44, 8 };
          var first = fst.Pick(lv0);
          Check(first[0] == 400 && first[1] == 300 && first[2] == 120 && first[3] == 44, "frame: the first drawn rect equals the live rect (no layout rect first)");
          var jit = fst.Pick(new double[] { 402, 301, 118, 45, 8 });
          Check(jit[2] == 120 && jit[0] == 400, "frame: a live rect within 3 px keeps the drawn one (no size flicker)");
          var mv = fst.Pick(new double[] { 410, 300, 120, 44, 8 });
          Check(mv[0] == 410, "frame: a real move (over 3 px) follows the live rect"); }
        Check(!RFX.UiTopIs(ClickTarget.EquipConfirm, null, tipPop) && !RFX.UiTopIs(ClickTarget.EquipConfirm, null, new ScreenState { Kind = ScreenKind.HeroScreen, Top = "Form_CharactorEquipTip", Held = true }),
          "top: «Заменить» not under a pop-up, not on a held read");
        Check(RFX.UiTopIs(ClickTarget.OfferClose, "Form_X", off) && !RFX.UiTopIs(ClickTarget.OfferClose, "Form_Y", off) && !RFX.UiTopIs(ClickTarget.CityInventory, null, off), "top: the × of the pop-up shown; the city's «Инвентарь» not under a pop-up");
      }
      // the diagnostic dump: a failing read step by step, one a minute
      {
        RFX.UiDiagReset();
        m.Mem[csObj - FakeLua.Base + 0xF8] = 1;
        r = RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out line);
        string d1 = RFX.UiButtonDiagLine(r, 100000), d2 = RFX.UiButtonDiagLine(r, 130000), d3 = RFX.UiButtonDiagLine(r, 160001);
        Check(d1 != null && d1.StartsWith("uibtn diag: Form_CharactorMain.m_ButtonBack.m_btnBack failed at m_csui (Form_LuaBase Destroyed): ") && d1.Contains("forms=") && d1.Contains("m_csui=ud ")
          && d1.Contains("widget.native=") && d1.Contains("m_csui.Destroyed=1"), "diag: every step's addresses and values: " + d1);
        Check(d2 == null && d3 != null, "  ... one a minute");
        m.Mem[csObj - FakeLua.Base + 0xF8] = 0;
        Check(RFX.UiButtonDiagLine(RFX.UiButtonForTest(fu.Env, ui.A, wBack, 56, 51, W, H, U, out line), 999999) == null, "  ... none for a good read");
      }
      // the targets by name
      Check(RFX.UiWidgetOf(ClickTarget.CityInventory, null).Form == "Form_MainCity" && RFX.UiWidgetOf(ClickTarget.FoundryButton, null).Paths[0] == "m_PanelContentCur.m_BtnWorkShop"
        && RFX.UiWidgetOf(ClickTarget.AscendTab, null).Paths[0] == "m_ToggleSubliming" && RFX.UiWidgetOf(ClickTarget.EquipConfirm, null).Paths.Length == 2
        && RFX.UiWidgetOf(ClickTarget.OfferClose, "Form_X").Form == "Form_X" && RFX.UiWidgetOf(ClickTarget.OfferClose, null) == null && RFX.UiWidgetOf(ClickTarget.InvEquipTab, null) == null,
        "the pilot targets read by name (others none)");
    } finally { RFX.TestMemory = null; RFX.ForgetLive(); RFX.ForgetClasses(); }
  }

  // ------------------------------------------------------------------ the script state's tables from its root (src/LuaRoot.cs)
  /// <summary>A Lua function with table upvalues (LClosure 5.3: tt 6, nupvalues 0xA, p 0x18, upvals 0x20; UpVal.v 0x0; Proto
  /// sizeupvalues 0x10, upvalues 0x58); <paramref name="names"/> null = stripped (no upvalue names).</summary>
  static ulong FakeClosure(FakeState st, ulong[] ups, string[] names) {
    var m = st.M; int n = ups.Length;
    ulong cl = m.Alloc(0x20 + 8 * n), p = m.Alloc(0x60);
    m.Mem[cl - FakeLua.Base + 8] = 6; m.Mem[cl - FakeLua.Base + 0xA] = (byte)n; m.W64(cl + 0x18, p);
    if (names != null) { ulong d = m.Alloc(16 * n); m.W32(p + 0x10, n); m.W64(p + 0x58, d); for (int i = 0; i < n; i++) m.W64(d + (ulong)(16 * i), st.S(names[i])); }
    for (int i = 0; i < n; i++) { ulong uv = m.Alloc(0x20); m.W64(uv, uv + 0x10); m.W64(uv + 0x10, ups[i]); m.W32(uv + 0x18, 0x45); m.W64(cl + 0x20 + (ulong)(8 * i), uv); }
    return cl;
  }
  static void SetField(FakeState st, FTab t, string key, ulong v, int tt) { ulong nd = st.ValueAt(t, key); st.M.W64(nd, v); st.M.W32(nd + 8, tt); }

  static void LuaRootTests() {
    Console.WriteLine("Script tables from the root (src/LuaRoot.cs):");
    var saveLy = Layout.Current; string saveAsm = Layout.LocalAsm; bool saveFirst = RFX.LuaRootFirst;
    var m = new FakeLua(8 << 20); RFX.TestMemory = m; RFX.ForgetLive(); RFX.ForgetClasses();
    try {
      ulong ga = FakeImage(m);
      Layout.LocalAsm = null;
      var ly = GameLayout.Parse(FixtureJson().Replace("\"BattleView.BattleManager\": 8192", "\"BattleView.BattleManager\": 8192, \"LuaManager\": 8256"), FixtureGame);
      Check(ly != null && ly.Rva("LuaManager") == 0x2040, "  (a layout with the address of LuaManager)");
      Layout.Use(ly);
      var st = new FakeState(m);
      // the forms, UIInstance, Managers, _G
      var city = st.T("class", st.T("__cname", "Form_MainCity"), "____instanceId", 30001L, "m_LuaLogicActive", true);
      var hero = st.T("class", st.T("__cname", "Form_CharactorMain"), "____instanceId", 30002L);
      var ui = st.T(-11111L, city, 22222L, hero);
      var eq = st.T("equips", st.T(), "class", st.T("__cname", "EquipData")); var hd = st.T("m_CharactorDatas", st.T());
      var mgr = st.T("EquipData", eq, "HeroData", hd, "PlayerData", st.T("m_Uid", 5L), "RedpointDefine", st.T());
      var G = st.T("_G", st.T(), "GetUIInstanceLua", st.T(), "mtEquipData", st.T(), "print", 1L, "UIInstanceNot", st.T());
      SetField(st, G, "_G", G.A, 0x45);
      SetField(st, G, "GetUIInstanceLua", FakeClosure(st, new[] { ui.A }, new[] { "UIInstance" }), 0x46);
      SetField(st, G, "mtEquipData", FakeClosure(st, new[] { mgr.A, G.A }, new[] { "Managers", "_ENV" }), 0x46);
      // lua_State -> global_State -> registry {[1] = main thread, [2] = _G}
      ulong L = m.Alloc(0x40), g = m.Alloc(0x60); var reg = st.T("x", 1L); ulong arr = m.Alloc(32);
      m.Mem[L - FakeLua.Base + 8] = 8; m.W64(L + 0x18, g);
      m.W64(g + 0x40, reg.A); m.W32(g + 0x48, 0x45);
      m.W32(reg.A + 12, 2); m.W64(reg.A + 16, arr); m.W64(arr, L); m.W32(arr + 8, 0x48); m.W64(arr + 16, G.A); m.W32(arr + 24, 0x45);
      // il2cpp: LuaManager -> static_fields 0xB8 -> m_luaEnv -> rawL 0x10
      ulong kLm = FakeKlass(m, "", "LuaManager"), kEnv = FakeKlass(m, "XLua", "LuaEnv"), sf = m.Alloc(0x10), env = m.Alloc(0x70);
      m.W64(kLm + 0xB8, sf); m.W64(sf, env); m.W64(env, kEnv); m.W64(env + 0x10, L);
      m.W64(ga + 0x2040, kLm);
      RFX.LuaRootGaForTest = ga;
      RFX.SetUiTableForTest(0);
      string line; var r = RFX.LuaRootForTest(out line);
      Check(r.Ok, "the walk gets to the end: " + line);
      Eq(L, r.RawL, "  ... rawL from LuaManager.m_luaEnv");
      Eq(G.A, r.G, "  ... _G from the registry's array [2]");
      Eq(ui.A, r.Ui, "  ... UIInstance = the upvalue of GetUIInstanceLua");
      Eq(mgr.A, r.Managers, "  ... Managers = the upvalue of mtEquipData (named, next to _ENV)");
      Check(r.Mgr.ContainsKey("EquipData") && r.Mgr["EquipData"] == eq.A && r.Mgr["HeroData"] == hd.A && !r.Mgr.ContainsKey("ArtifactData"), "  ... the manager tables by name");
      Eq(2, r.UiForms, "  ... the forms of UIInstance");
      Check(r.FormNames.Count == 2 && r.FormNames[0] == "Form_MainCity*" && r.FormNames[1] == "Form_CharactorMain", "  ... their names, the shown one first (*)");
      Eq(4, r.ManagerCount, "  ... Managers' table count");
      Check(line.StartsWith("luaroot: ok in ") && line.Contains("_G nodes=5") && line.Contains("UIInstance forms=2 (Form_MainCity*, Form_CharactorMain)") && line.EndsWith("matches scan: n/a"), "  ... the log line: " + line);
      // compared with the scan's results
      var lt = new LiveTables { EquipData = eq.A, HeroData = hd.A };
      Eq("yes", RFX.LuaRootMatches(r, ui.A, lt), "matches: the same tables as the scans");
      Check(RFX.LuaRootMatches(r, ui.A + 0x100, lt).StartsWith("no (UIInstance "), "matches: another window table: no");
      lt.ArtOwner = 0x1234;
      Check(RFX.LuaRootMatches(r, 0, lt).StartsWith("no (ArtifactData 0 vs scan 1234"), "matches: a manager the root lacks: no");
      // stripped bytecode (no upvalue names): the one table that is not _G
      SetField(st, G, "mtEquipData", FakeClosure(st, new[] { G.A, mgr.A }, null), 0x46);
      Eq(mgr.A, RFX.LuaRootForTest(out line).Managers, "no upvalue names: the table upvalue that is not _G");
      SetField(st, G, "mtEquipData", FakeClosure(st, new[] { mgr.A, ui.A }, null), 0x46);
      r = RFX.LuaRootForTest(out line);
      Check(!r.Ok && r.FailStep == "Managers" && line.StartsWith("luaroot: failed at Managers (2 table upvalues"), "no names, two tables: not guessed: " + line);
      SetField(st, G, "mtEquipData", FakeClosure(st, new[] { mgr.A, G.A }, new[] { "Managers", "_ENV" }), 0x46);
      // the window table from the root first (UiTable)
      RFX.LuaRootFirst = true; RFX.SetUiTableForTest(0);
      RFX.FindUi(0);
      Eq(ui.A, RFX.UiAddress, "LuaRootFirst: the window table comes from the root");
      // broken links: the step is named
      Action<string, Action, Action> Broken = (step, brk, fix) => { brk(); string ln; var b = RFX.LuaRootForTest(out ln); Check(!b.Ok && b.FailStep == step && ln.StartsWith("luaroot: failed at " + step + " ("), "broken " + step + ": " + ln); fix(); };
      Broken("_G._G", () => SetField(st, G, "_G", reg.A, 0x45), () => SetField(st, G, "_G", G.A, 0x45));
      Broken("registry[2]", () => m.W32(arr + 24, 0), () => m.W32(arr + 24, 0x45));
      Broken("registry[1]", () => m.W64(arr, L + 8), () => m.W64(arr, L));
      { // a big array part (luaL_ref): still read; [2] wrong: the slots are logged
        ulong big = m.Alloc(16 * 16384); Array.Copy(m.Mem, (int)(arr - FakeLua.Base), m.Mem, (int)(big - FakeLua.Base), 32);
        m.W32(reg.A + 12, 16384); m.W64(reg.A + 16, big);
        string ln; Check(RFX.LuaRootForTest(out ln).Ok, "registry with 16384 array slots: _G from [2]: " + ln);
        m.W32(big + 24, 0x13); var b = RFX.LuaRootForTest(out ln);
        Check(!b.Ok && b.FailStep == "registry[2]" && ln.Contains("array 16384 at ") && ln.Contains("[0] tt=0x48 v=" + L.ToString("X")) && ln.Contains("[1] tt=0x13") && ln.Contains("[2] tt=0x0"), "  ... a wrong [2]: the slots' tt and values are logged: " + ln);
        m.W32(reg.A + 12, 2); m.W64(reg.A + 16, arr);
      }
      Broken("l_registry", () => m.W32(g + 0x48, 0x13), () => m.W32(g + 0x48, 0x45));
      Broken("lua_State", () => m.Mem[L - FakeLua.Base + 8] = 5, () => m.Mem[L - FakeLua.Base + 8] = 8);
      Broken("m_luaEnv class", () => m.W64(env, kLm), () => m.W64(env, kEnv));
      Broken("m_luaEnv", () => m.W64(sf, 0), () => m.W64(sf, env));
      Broken("static_fields", () => m.W64(kLm + 0xB8, 0), () => m.W64(kLm + 0xB8, sf));
      Broken("GetUIInstanceLua", () => SetField(st, G, "GetUIInstanceLua", ui.A, 0x45), () => SetField(st, G, "GetUIInstanceLua", FakeClosure(st, new[] { ui.A }, new[] { "UIInstance" }), 0x46));
      Check(RFX.LuaRootForTest(out line).Ok, "  ... all fixed again: ok");
      RFX.LuaRootFirst = true; RFX.SetUiTableForTest(0); m.W64(sf, 0);
      RFX.FindUi(0);
      Check(RFX.UiAddress != ui.A, "LuaRootFirst: a broken root falls back to the scan");
      m.W64(sf, env);
    } finally {
      RFX.LuaRootGaForTest = 0; RFX.LuaRootFirst = saveFirst; RFX.SetUiTableForTest(0);
      RFX.TestMemory = null; RFX.ForgetLive(); RFX.ForgetClasses(); Layout.LocalAsm = saveAsm; Layout.Use(saveLy);
    }
  }

  /// <summary>A PE image in the stand-in memory: header, a code section, a writable data section (rva 0x2000, 8 KB) and a read-only one; 64 KB.</summary>
  static ulong FakeImage(FakeLua m) {
    ulong ga = m.Alloc(0x11000); ga = (ga + 0xFFF) & ~0xFFFUL;
    byte[] hd = new byte[0x1000]; hd[0] = (byte)'M'; hd[1] = (byte)'Z';
    Array.Copy(BitConverter.GetBytes(0x80), 0, hd, 0x3C, 4);
    hd[0x80] = (byte)'P'; hd[0x81] = (byte)'E';
    Array.Copy(BitConverter.GetBytes((ushort)3), 0, hd, 0x80 + 6, 2);
    Array.Copy(BitConverter.GetBytes((ushort)0xF0), 0, hd, 0x80 + 20, 2);
    Array.Copy(BitConverter.GetBytes(0x10000), 0, hd, 0x80 + 24 + 56, 4);
    int tab = 0x80 + 24 + 0xF0;
    Action<int, uint, uint, uint> Sec = (i, vs, va, ch) => { int e = tab + 40 * i; Array.Copy(BitConverter.GetBytes(vs), 0, hd, e + 8, 4); Array.Copy(BitConverter.GetBytes(va), 0, hd, e + 12, 4); Array.Copy(BitConverter.GetBytes(ch), 0, hd, e + 36, 4); };
    Sec(0, 0x1000, 0x1000, 0x60000020u);      // code: not scanned
    Sec(1, 0x2000, 0x2000, 0xC0000040u);      // data, writable: scanned
    Sec(2, 0x1000, 0x4000, 0x40000040u);      // read-only data: not scanned
    Array.Copy(hd, 0, m.Mem, (int)(ga - FakeLua.Base), 0x1000);
    return ga;
  }

  /// <summary>An Il2CppClass in the stand-in memory: name at +0x10, namespace at +0x18 (plain C strings).</summary>
  static ulong FakeKlass(FakeLua m, string ns, string name) {
    ulong k = m.Alloc(0x60), sa = m.Alloc(ns.Length + 1), sb = m.Alloc(name.Length + 1);
    Array.Copy(Encoding.ASCII.GetBytes(ns), 0, m.Mem, (int)(sa - FakeLua.Base), ns.Length);
    Array.Copy(Encoding.ASCII.GetBytes(name), 0, m.Mem, (int)(sb - FakeLua.Base), name.Length);
    m.W64(k + 0x10, sb); m.W64(k + 0x18, sa);
    return k;
  }
  const int BatchSizeForTest = KlassScan.BatchSize;

  static void TestLogTests(string mock) {
    Console.WriteLine("Test-build log (src/TestLog.cs: bounded queue, send to the site):");
    var q = new TestLogQueue();
    for (int i = 0; i < 3005; i++) q.Add("l" + i);
    Eq(3000, q.Count, "queue: at most 3000 lines");
    var t = q.Take(500, 1000000);
    Eq(500, t.Count, "queue: Take limited by count");
    Eq("l5", t[0], "queue: the oldest 5 were dropped, Take starts at the oldest kept");
    Eq(2500, q.Count, "queue: taken lines are removed");
    q.Return(t);
    Eq(3000, q.Count, "queue: Return puts them back");
    var t2 = q.Take(3, 1000000);
    Eq("l5,l6,l7", string.Join(",", t2.ToArray()), "queue: Return keeps the order at the front");
    var q2 = new TestLogQueue();
    q2.Add(new string('x', 5000)); q2.Add("b"); q2.Add("c");
    var t3 = q2.Take(10, 100);
    Eq(1, t3.Count, "queue: Take limited by bytes (a first long line still goes alone)");
    Eq(2000, t3[0].Length, "queue: a line is cut to 2000 characters");
    var q3 = new TestLogQueue();
    q3.Add("a"); q3.Add("b"); q3.Add("c");
    q3.Return(new List<string> { "x", "y" });
    Eq("x,y,a", string.Join(",", q3.Take(3, 1000).ToArray()), "queue: Return goes before the waiting lines");
    var lines = new List<string> { "one \"q\"\n", "two" };
    string TlCode = "rf_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    Eq("{\"v\":1,\"app\":\"1.0\",\"lines\":[\"one \\\"q\\\"\\n\",\"two\"]}", TestLogClient.BuildJson("1.0", lines), "json: escaped");
    Eq(TestLogClient.Result.Ok, TestLogClient.Send(mock, TlCode, "1.0", lines), "send: ok");
    Eq(TestLogClient.Result.Off, TestLogClient.Send(mock, TlCode, "off", lines), "send: 404 is off");
    Eq(TestLogClient.Result.Later, TestLogClient.Send(mock, "rf_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", "1.0", lines), "send: 401 is later");
    Eq(TestLogClient.Result.Later, TestLogClient.Send(mock, "", "1.0", lines), "send: no code is later");
    Eq(TestLogClient.Result.Later, TestLogClient.Send("http://127.0.0.1:1", TlCode, "1.0", lines), "send: no connection is later");
  }

  // the raw inputs the recorder keeps next to every frame (src/RecInputs.cs): serialisation round trips, the per-line encoder, the scene rectangles
  static Dictionary<string, object> RecSlotsParsed(Dictionary<string, string> s) {
    var d = new Dictionary<string, object>();
    foreach (var kv in s) d[kv.Key] = MiniJson.Parse(kv.Value);
    return d;
  }

  static void RecInputsTests() {
    Console.WriteLine("Recorder inputs (src/RecInputs.cs: json round trips, line encoder, scene rectangles):");
    var inv = new InventoryScreen { Open = true, EquipTab = true, Bulk = true, BulkKnown = true, ListPtr = 18446744073709551000UL, Current = 5 };
    inv.Uids.AddRange(new long[] { 11, 12, 13, 14 }); inv.Selected.Add(13); inv.Selected.Add(11);
    inv.List = new RFX.ListRead { ScrollY = 123.456789012345, Cols = 8, RowUnits = 118, First = 2, Last = 20, FinalRowUnits = 117.5, FitScale = 0.9 };
    var rs = new RecastScreen { SublimingOpen = true, CurType = 2, PanelShown = true, Equips = new long[] { 1, 2 }, Sel = 2, SelectParts = new[] { 0, 3 }, Suits = new long[] { 7 }, MainAttrs = new long[] { 8 }, SubAttrs = new long[] { 9, 10 },
      GemOpen = true, GemFunc = 7, GemSel = 4, GemList = new long[] { 4, 5 }, AscendShown = true, AscendEquips = new long[] { 21, 22, 23 }, AscendSel = 22, AscendParts = new[] { 1 },
      AscendSuits = new long[] { 3 }, AscendMainAttrs = new long[] { 6 }, AscendSubAttrs = new long[] { 2 }, AscendFilterOpen = true, FilterPanel = 1,
      FilterShowSuits = new long[] { 5, 6 }, FilterLeftSuits = new long[] { 5 }, FilterRightSuits = new long[] { 6 }, ChoosePending = true,
      AscendList = new RFX.ListRead { ScrollY = -3.5, Cols = 4, RowUnits = 140, First = 0, Last = 11 }, RecastList = null, GemFuncList = new RFX.ListRead { ScrollY = double.NaN, Cols = 1, RowUnits = 100 } };
    var hero = new HeroScreen { Hero = 99, FormOk = true, Tab = 3, Part = 2, SmallCards = true, Heroes = new long[] { 9, 8 }, GridPtr = 42, FilterOpen = true, FactionList = null, ClassCount = 5, ChosenCamps = new long[] { 1 }, ClassChosen = true };
    var inp = new ScreenInputs { Inv = inv, Rs = rs, Hero = hero, CitySeen = true, CityForm = false, ModalExtra = true, ModalExtraWhy = "pop \"up\"" };
    var s1 = RecIn.ReadSlots(inp);
    foreach (var kv in s1) Check(MiniJson.TryParse(kv.Value) != null || kv.Value == "null", "inputs json: slot " + kv.Key + " is valid json");
    var back = RecIn.ParseInputs(RecSlotsParsed(s1));
    var s2 = RecIn.ReadSlots(back);
    foreach (var kv in s1) Eq(kv.Value, s2[kv.Key], "inputs round trip: " + kv.Key);
    Check(back.Inv.ListPtr == inv.ListPtr && back.Inv.Selected.Contains(13) && back.Inv.Selected.Count == 2 && back.Inv.Uids.Count == 4 && back.Inv.Current == 5 && back.Inv.Bulk, "inputs round trip: the inventory's fields");
    Check(Math.Abs(back.Inv.List.ScrollY - inv.List.ScrollY) < 1e-12 && back.Inv.List.Last == 20 && back.Inv.List.FitScale == 0.9, "inputs round trip: the list reading keeps full precision");
    Check(back.Rs.AscendFilterOpen && back.Rs.AscendEquips.Length == 3 && back.Rs.AscendList.Cols == 4 && back.Rs.RecastList == null && !back.Rs.GemFuncList.Has && back.Rs.FilterRightSuits[0] == 6, "inputs round trip: the Foundry screen (a null list stays null, an unreadable one stays unreadable)");
    Check(back.Hero.Hero == 99 && back.Hero.FactionList == null && back.Hero.Heroes.Length == 2 && back.Hero.GridPtr == 42 && back.CityForm == false && back.ModalExtraWhy == "pop \"up\"", "inputs round trip: the hero screen and the flags");
    var none = RecIn.ParseInputs(RecSlotsParsed(RecIn.ReadSlots(new ScreenInputs())));
    Check(none.Inv == null && none.Rs == null && none.Hero == null && none.CityForm == null && !none.CitySeen, "inputs round trip: failed reads stay failed");
    Eq(ScreenState.Classify(inp, 5).Key, ScreenState.Classify(back, 5).Key, "inputs round trip: the screen state is the same");

    var item = new PlanItem { Uid = 12, Slot = 3, Quality = 8, SetId = 77 };
    item.Subs.Add(new SubStat { Stat = 24 }); item.Subs.Add(new SubStat { Stat = -1 }); item.Main.Add(new SubStat { Stat = 5 });
    var req = new LocateRequest { Id = "abc", Purpose = "ascend", Item = item, HeroUid = 99 };
    var rq2 = RecIn.ParseRequest(MiniJson.Parse(RecIn.Request(req)));
    Eq(RecIn.Request(req), RecIn.Request(rq2), "request round trip");
    Check(rq2.Item.Subs.Count == 2 && rq2.Item.Subs[1].Stat == -1 && rq2.Item.Main[0].Stat == 5 && rq2.Item.SetId == 77 && rq2.HeroUid == 99, "request round trip: the fields the guide reads");
    Eq("null", RecIn.Request(null), "request: none");
    var cx = new RecCtx { Mode = "sell", Front = true, Cols = 7, OnAccount = 1, Req = req, Lines = true, CityInvSeen = true, InvDX = 0.62, BtnDY = double.NaN, Want = new long[] { 3, 4 }, ShCols = 8, PillDown = true };
    var cx2 = RecCtx.Parse(MiniJson.Parse(cx.ToJson()));
    Eq(cx.ToJson(), cx2.ToJson(), "context round trip");
    Check(double.IsNaN(cx2.BtnDY) && cx2.Want.Length == 2 && cx2.PillDown && !cx2.PillUp, "context round trip: fields");

    // the guide on the rebuilt inputs gives the same answer as on the originals
    var guideInv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true };
    for (int i = 0; i < 30; i++) guideInv.Uids.Add(100 + i);
    var greq = new LocateRequest { Id = "g", Purpose = "recast", Item = new PlanItem { Uid = 112, Slot = 1, Quality = 6 } };
    var gin = new ScreenInputs { Inv = guideInv };
    var gback = RecIn.ParseInputs(RecSlotsParsed(RecIn.ReadSlots(gin)));
    var gs1 = LocateGuide.Next(greq, gin.Inv, null, null, 8, 1); var gs2 = LocateGuide.Next(greq, gback.Inv, null, null, 8, 1);
    Check(gs1.Kind == LocateKind.InList && gs2.Kind == gs1.Kind && gs2.Row == gs1.Row && gs2.Col == gs1.Col && gs2.Pos == 13, "guide on rebuilt inputs: the same step and place");

    // the line encoder: a slot is written once; a new part starts with everything again
    RecReads.Clear();
    var enc = new RecLineEncoder();
    RecReads.Add(100, inp); RecReads.Add(220, inp);
    var info = new RecInfo { Ctx = cx.ToJson(), TickNow = 230, ReadSeq = RecReads.Seq, PlaceRow1 = 12.5 };
    string l1 = enc.EncodeInfo(info, "{\"U\":1}");
    var j1 = MiniJson.AsObject(MiniJson.Parse(l1));
    var reads1 = (List<object>)j1["reads"];
    Eq(2, reads1.Count, "line encoder: both reads of the tick");
    Check(MiniJson.AsObject(reads1[0]).ContainsKey("r.inv") && !MiniJson.AsObject(reads1[1]).ContainsKey("r.inv"), "line encoder: the second read repeats nothing");
    Check(j1.ContainsKey("ctx") && j1.ContainsKey("env") && (double)j1["pr"] == 12.5 && !j1.ContainsKey("sr") && !j1.ContainsKey("fd"), "line encoder: context, environment, the placed row; no eased scroll");
    RecReads.Add(340, inp);
    info = new RecInfo { Ctx = cx.ToJson(), TickNow = 350, ReadSeq = RecReads.Seq };
    var j2 = MiniJson.AsObject(MiniJson.Parse(enc.EncodeInfo(info, "{\"U\":1}")));
    int keys2 = MiniJson.AsObject(((List<object>)j2["reads"])[0]).Count;
    Check(!j2.ContainsKey("ctx") && !j2.ContainsKey("env") && keys2 == 1, "line encoder: an unchanged line is only the read's time (" + keys2 + " keys)");
    // reads that came after the tick stay for the next line
    RecReads.Add(400, inp); long seqNow = RecReads.Seq; RecReads.Add(520, inp);
    info = new RecInfo { Ctx = cx.ToJson(), TickNow = 410, ReadSeq = seqNow };
    Eq(1, ((List<object>)MiniJson.AsObject(MiniJson.Parse(enc.EncodeInfo(info, "{}")))["reads"]).Count, "line encoder: a read after the tick waits");
    enc.Rebase();
    info = new RecInfo { Ctx = cx.ToJson(), TickNow = 530, ReadSeq = RecReads.Seq };
    var j3 = MiniJson.AsObject(MiniJson.Parse(enc.EncodeInfo(info, "{}")));
    var r3 = (List<object>)j3["reads"];
    Check(r3.Count == 2 && MiniJson.AsObject(r3[0]).ContainsKey("base") && MiniJson.AsObject(r3[0]).ContainsKey("r.inv") && j3.ContainsKey("ctx") && j3.ContainsKey("env"), "line encoder: a new part starts with the whole state");
    string withIn = RecJson.Line(1, 10, 5, 1, false, info, 0, 0, l1);
    Check(MiniJson.AsObject(MiniJson.Parse(withIn)).ContainsKey("in") && withIn.EndsWith(",\"in\":" + l1 + "}"), "jsonl line: the inputs go last");
    RecReads.Clear();

    // the scene rectangles: a cell of the inventory list, the bulk-sale frame
    const double W = 1920, H = 1080; double U = Ui.Unit(W, H);
    var lc = new RecCtx { Mode = "locate", Front = true, Cols = 8, Req = greq, Lines = true };
    var lstep = LocateGuide.Next(greq, guideInv, null, null, 8, 1);
    var sgeo = SellGeometry.Inventory(W, U, 8);
    guideInv.List = new RFX.ListRead { ScrollY = 0, Cols = 8, RowUnits = 118, First = 0, Last = 15 };
    double row1 = new InvScroll().Row1(sgeo, guideInv.List, double.NaN, U);
    var rects = RecScenes.Locate(lc, lstep, greq.Item, null, guideInv, null, row1, W, H, U);
    Check(rects.Count == 1 && rects[0].Kind == "frame" && rects[0].W == (int)(sgeo.CellW * U), "scene: the item's cell in the list is one frame (" + rects.Count + ")");
    lc.Front = false;
    Eq(0, RecScenes.Locate(lc, lstep, greq.Item, null, guideInv, null, row1, W, H, U).Count, "scene: nothing is drawn while the game is not in front");
    Eq(0, RecScenes.Locate(new RecCtx { Mode = "locate", Front = true, Cols = 8, Req = greq }, lstep, greq.Item, null, guideInv, null, double.NaN, W, H, U).Count, "scene: a list with no place has no frame");
    var bulk = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true, Bulk = true }; bulk.Uids.AddRange(new long[] { 1, 2, 3 }); bulk.Selected.Add(2); bulk.Selected.Add(3);
    var sc = new RecCtx { Mode = "sell", Front = true, Want = new long[] { 1, 2 }, ShCols = 8, Lines = true };
    SellHintStep sstep;
    double srow = new InvScroll().Row1(SellGeometry.Inventory(W, U, 8), new RFX.ListRead { ScrollY = 0, Cols = 8, RowUnits = 118 }, double.NaN, U);
    var srects = RecScenes.Sell(sc, bulk, null, srow, W, H, U, out sstep);
    Check(sstep.Kind == SellHintKind.Select && srects.Count == 3 && srects.Exists(x => x.Kind == "mark1") && srects.Exists(x => x.Kind == "mark0") && srects.Exists(x => x.Kind == "mark2"), "scene: the cleanup marks todo / done / extra cells (" + srects.Count + ")");
    var pb = RecScenes.Sell(sc, new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, null, double.NaN, W, H, U, out sstep);
    var pr = SellHint.PressBulkRect(W, H, U);
    Check(sstep.Kind == SellHintKind.PressBulk && pb.Count == 1 && pb[0].Kind == "tip-frame" && pb[0].X == pr[0] && pb[0].W == pr[2], "scene: the bulk-sale button is framed where the auto mode clicks");
  }

  static void ScreenRecorderTests() {
    Console.WriteLine("Test-build screen recorder (src/ScreenRecorder.cs: names, sizes, jsonl, zip parts, upload queue):");
    string id = RecNames.NewId(new DateTime(2026, 10, 17, 9, 5, 7), new Random(1));
    Check(System.Text.RegularExpressions.Regex.IsMatch(id, "^20261017-090507-[0-9a-f]{6}$") && RecNames.IsValidId(id), "rec id: yyyyMMdd-HHmmss-6 hex (" + id + ")");
    Check(!RecNames.IsValidId("short") && !RecNames.IsValidId("has space 12345") && !RecNames.IsValidId(new string('a', 41)) && !RecNames.IsValidId(null), "rec id: the site's pattern");
    Eq("part_000.zip", RecNames.PartFile(0), "part name 0"); Eq("part_119.zip", RecNames.PartFile(119), "part name 119");
    Eq("frame_0001.jpg", RecNames.FrameFile(1), "frame name");
    Eq("ov_0001.json", RecNames.OverlayFile(1), "overlay sidecar name");
    {
      var els = new List<RecOverlayEl> {
        new RecOverlayEl { Key = "frame", Src = "glow", X = 10, Y = 20, W = 74, H = 74 },
        new RecOverlayEl { Key = "pulse", Src = "glow", Listed = false },
        new RecOverlayEl { Key = "plate", Src = "tip", X = 100, Y = 30, W = 200, H = 60 } };
      Eq("{\"t\":1250,\"els\":[{\"key\":\"frame\",\"src\":\"glow\",\"x\":10,\"y\":20,\"w\":74,\"h\":74},{\"key\":\"plate\",\"src\":\"tip\",\"x\":100,\"y\":30,\"w\":200,\"h\":60}]}", RecOverlayJson.Line(1250, els), "overlay sidecar: key, src, rect of the listed elements");
      Check(RecOverlayJson.Line(5, new List<RecOverlayEl>()) == null && RecOverlayJson.Line(5, null) == null, "overlay sidecar: none when nothing was drawn");
    }
    Eq(7, RecNames.PartOf("part_007.zip"), "part number from a name"); Eq(-1, RecNames.PartOf("part_7.zip.tmp"), "not a part name");
    Eq(12, RecNames.PartOf("012.zip"), "part number from the server's name (NNN.zip)");
    {   // the layout / geometry text is kept in memory, so the recorder has it without a saved file
      string gv = "1.6.39.829.1";
      Layout.Keep(gv, "{\"k\":1}"); Geo.Keep(gv, "{\"g\":2}");
      Eq("{\"k\":1}", Layout.RawCache(gv), "layout raw text: from memory"); Eq("{\"g\":2}", Geo.RawCache(gv), "geometry raw text: from memory");
    }
    {   // the locate guide keeps its last step while the screen is Unknown, for 15 s
      var prev = new LocateStep { Kind = LocateKind.OpenFoundry };
      Check(LocateGuide.HoldOnUnknown(prev, ScreenKind.Unknown, 1000, 1500), "hold: Unknown for 0.5 s keeps the step");
      Check(LocateGuide.HoldOnUnknown(prev, ScreenKind.Unknown, 1000, 15999), "hold: still kept just before 15 s");
      Check(!LocateGuide.HoldOnUnknown(prev, ScreenKind.Unknown, 1000, 16000), "hold: after 15 s the step is recomputed");
      Check(!LocateGuide.HoldOnUnknown(prev, ScreenKind.Inventory, 1000, 1500), "hold: a known screen never holds");
      Check(!LocateGuide.HoldOnUnknown(prev, ScreenKind.Unknown, -1, 1500), "hold: not Unknown (since < 0) -> no hold");
      Check(!LocateGuide.HoldOnUnknown(null, ScreenKind.Unknown, 1000, 1500), "hold: no previous step -> nothing to hold");
      Check(!LocateGuide.HoldOnUnknown(new LocateStep { Kind = LocateKind.None }, ScreenKind.Unknown, 1000, 1500), "hold: an empty step is not held");
    }
    int nw, nh; double sc;
    sc = RecSize.Fit(1920, 1080, 1280, out nw, out nh); Check(nw == 1280 && nh == 720 && Math.Abs(sc - 2.0 / 3) < 1e-9, "size: 1920x1080 -> 1280x720");
    sc = RecSize.Fit(1366, 705, 1280, out nw, out nh); Check(nw == 1280 && nh == 661 && sc < 1, "size: 1366x705 -> 1280x661");
    sc = RecSize.Fit(1280, 720, 1280, out nw, out nh); Check(nw == 1280 && nh == 720 && sc == 1.0, "size: already 1280 wide: unchanged");
    sc = RecSize.Fit(800, 600, 1280, out nw, out nh); Check(nw == 800 && nh == 600 && sc == 1.0, "size: small is never enlarged");
    var info = new RecInfo { Screen = "Inventory equip normal", Step = "InList", InvRaw = 120.5, InvRow1 = -33.126, FdRaw = double.NaN };
    info.Frames.Add(new RecFrameRect { Kind = "frame", X = 10, Y = 20, W = 30, H = 40 });
    string line = RecJson.Line(1250, 1920, 1080, 2.0 / 3, false, info, 640, 360);
    Eq("{\"t\":1250,\"w\":1920,\"h\":1080,\"scale\":0.6667,\"screen\":\"Inventory equip normal\",\"step\":\"InList\",\"frames\":[{\"kind\":\"frame\",\"x\":10,\"y\":20,\"w\":30,\"h\":40}],\"scroll\":{\"invRaw\":120.5,\"invRow1\":-33.13,\"invSmooth\":null,\"fdRaw\":null,\"fdSmooth\":null},\"cursor\":[640,360]}", line, "jsonl line");
    Eq("{\"t\":0,\"w\":10,\"h\":5,\"scale\":1,\"bg\":true,\"screen\":\"-\",\"step\":\"-\",\"frames\":[],\"scroll\":{\"invRaw\":null,\"invRow1\":null,\"invSmooth\":null,\"fdRaw\":null,\"fdSmooth\":null},\"cursor\":[-3,4]}", RecJson.Line(0, 10, 5, 1, true, null, -3, 4), "jsonl line: no info, background");
    Check(MiniJson.Parse(line) != null, "jsonl line is valid json");
    Eq("{\"ev\":\"rec\",\"on\":true,\"sec\":42,\"sent\":1,\"total\":4,\"why\":\"\"}", RecJson.State(true, 42, 1, 4, ""), "state event");
    // a zip part: frames + frames.jsonl, whole only after Close
    string dir = Path.Combine(Path.GetTempPath(), "rf-rec-test-" + Guid.NewGuid().ToString("N"));
    try {
      var w = new RecPartWriter(dir, 3);
      w.Add(RecNames.FrameFile(1), new byte[] { 1, 2, 3 }, "{\"t\":0}"); w.Add(RecNames.FrameFile(2), new byte[] { 4, 5 }, "{\"t\":125}");
      Check(!File.Exists(Path.Combine(dir, "part_003.zip")) && w.Frames == 2, "zip part: no part file before Close");
      string p = w.Close();
      Check(p != null && Path.GetFileName(p) == "part_003.zip" && File.Exists(p) && !File.Exists(p + ".tmp"), "zip part: closed to part_003.zip");
      using (var z = ZipFile.OpenRead(p)) {
        Eq(3, z.Entries.Count, "zip part: 2 frames + frames.jsonl");
        string txt; using (var r = new StreamReader(z.GetEntry("frames.jsonl").Open())) txt = r.ReadToEnd();
        Eq("{\"t\":0}\n{\"t\":125}\n", txt, "zip part: jsonl lines");
        Check(z.GetEntry("frame_0002.jpg").Length == 2, "zip part: frame bytes");
      }
      Check(new RecPartWriter(dir, 4).Close() == null && !File.Exists(Path.Combine(dir, "part_004.zip")), "zip part: empty part leaves no file");
      // the log part: 60 s around the recording, repeating navprobe lines dropped, bounded zip
      var lg = new List<string> {
        "2026-11-04 10:00:00  too early", "2026-11-04 10:59:30  in margin", "2026-11-04 11:00:00  navprobe locker open=1",
        "continuation line", "2026-11-04 11:00:01  navprobe locker open=1", "2026-11-04 11:00:02  navprobe locker open=2",
        "2026-11-04 11:00:03  navprobe locker open=1", "2026-11-04 11:00:04  navprobe other x", "2026-11-04 11:00:05  navprobe other x",
        "2026-11-04 11:10:50  after stop", "2026-11-04 11:11:30  too late" };
      var sel = RecLogPart.Select(lg, new DateTime(2026, 11, 4, 11, 0, 0), new DateTime(2026, 11, 4, 11, 10, 0));
      Eq(8, sel.Count, "log part: window + dedup of identical navprobe locker lines");
      Check(sel[0].EndsWith("in margin") && sel[sel.Count - 1].EndsWith("after stop") && !sel.Contains(lg[4]), "log part: first, last, duplicate dropped");
      int kept; string lp = RecLogPart.Write(dir, 7, sel, out kept);
      Check(lp != null && Path.GetFileName(lp) == "part_007.zip" && kept == 8, "log part: written as part_007.zip");
      using (var z = ZipFile.OpenRead(lp)) Check(z.GetEntry("applog.txt") != null, "log part: applog.txt inside");
      var big = new List<string>(); var rnd = new Random(1);
      for (int i = 0; i < 200000; i++) big.Add("2026-11-04 11:00:00  " + rnd.Next().ToString() + rnd.Next().ToString() + rnd.Next().ToString());
      int kept2; string bp = RecLogPart.Write(dir, 8, big, out kept2);
      Check(bp != null && new FileInfo(bp).Length <= RecLogPart.MaxZipBytes && kept2 < big.Count && kept2 > 0, "log part: bounded to 2 MB, oldest dropped");
      // the upload queue: a part that fails stays and goes again; 404 stops and keeps the files
      var logs = new List<string>(); int calls = 0;
      var up = new RecUploader(id, () => "http://x", () => "c", s => { lock (logs) logs.Add(s); }) { RetryMs = 50 };
      up.Sender = (site, code, rec, part, file) => ++calls == 1 ? TestLogClient.Result.Later : TestLogClient.Result.Ok;
      up.Add(3, p); up.Close();
      for (int i = 0; i < 100 && !up.Idle; i++) System.Threading.Thread.Sleep(50);
      Check(up.Idle && up.Sent == 1 && up.Total == 1 && calls == 2 && !File.Exists(p), "upload: retried after a failure, sent, the file removed (calls " + calls + ")");
      lock (logs) Check(logs.Exists(x => x.StartsWith("recording " + id + ": part 3 sent (")), "upload: log line 'part N sent (bytes)'");
      string p2 = Path.Combine(dir, "part_005.zip"); File.WriteAllBytes(p2, new byte[] { 1 });
      var up2 = new RecUploader(id, () => "http://x", () => "c", null) { Sender = (a, b, c, d, e) => TestLogClient.Result.Off };
      up2.Add(5, p2); up2.Close();
      for (int i = 0; i < 100 && !up2.Off; i++) System.Threading.Thread.Sleep(50);
      Check(up2.Off && File.Exists(p2), "upload: 404 stops and keeps the file");
      Eq(TestLogClient.Result.Later, RecClient.Send("http://127.0.0.1:1", "rf_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", id, 0, p2), "send: no connection is later");
      // 413: the part is dropped with a log line (no endless retry); 429: the recording stops uploading, files stay
      string p3 = Path.Combine(dir, "part_006.zip"); File.WriteAllBytes(p3, new byte[] { 1, 2 });
      var logs3 = new List<string>(); int calls3 = 0;
      var up3 = new RecUploader(id, () => "http://x", () => "c", s => { lock (logs3) logs3.Add(s); }) { RetryMs = 50 };
      up3.Sender = (x1, x2, x3, x4, x5) => { calls3++; RecClient.LastStatus = 413; RecClient.LastKind = "http 413"; return TestLogClient.Result.Later; };
      up3.Add(6, p3); up3.Close();
      for (int i = 0; i < 100 && !up3.Idle; i++) System.Threading.Thread.Sleep(50);
      Check(up3.Idle && up3.Dropped == 1 && calls3 == 1, "upload: 413 drops the part, no retry (calls " + calls3 + ")");
      lock (logs3) Check(logs3.Exists(x => x.Contains("part 6 dropped") && x.Contains("413")), "upload: 413 log line");
      var logs4 = new List<string>(); int calls4 = 0;
      var up4 = new RecUploader(id, () => "http://x", () => "c", s => { lock (logs4) logs4.Add(s); }) { RetryMs = 50 };
      up4.Sender = (x1, x2, x3, x4, x5) => { calls4++; RecClient.LastStatus = 429; RecClient.LastKind = "http 429"; return TestLogClient.Result.Later; };
      up4.Add(6, p3); up4.Add(7, p3); up4.Close();
      for (int i = 0; i < 100 && !up4.Off; i++) System.Threading.Thread.Sleep(50);
      Check(up4.Off && up4.Limited && calls4 == 1 && File.Exists(p3), "upload: 429 stops the recording, keeps the file (calls " + calls4 + ")");
      lock (logs4) Check(logs4.Exists(x => x.Contains("quota") && x.Contains("429")), "upload: 429 log line");
      var logs5 = new List<string>(); int calls5 = 0;
      var up5 = new RecUploader(id, () => "http://x", () => "c", s => { lock (logs5) logs5.Add(s); }) { RetryMs = 50 };
      up5.Sender = (x1, x2, x3, x4, x5) => { if (++calls5 == 1) { RecClient.LastStatus = 401; RecClient.LastKind = "http 401"; return TestLogClient.Result.Later; } return TestLogClient.Result.Ok; };
      up5.Add(6, p3); up5.Close();
      for (int i = 0; i < 100 && !up5.Idle; i++) System.Threading.Thread.Sleep(50);
      lock (logs5) Check(logs5.Exists(x => x.Contains("not sent (http 401, 2 bytes)")), "upload: not-sent line has the status and the size");
    } finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
  }

  static void LayoutTests(string mock) {
    Console.WriteLine("Layout data");
    string fx = FixtureJson();
    var l = GameLayout.Parse(fx, FixtureGame);
    Check(l != null && l.Game == FixtureGame, "the fixture parses for its version");
    Eq(4096UL, l.Rva("CSharpBattle.Battle.GameSimulation"), "a class address");
    Eq(0UL, l.Rva("CSharpBattle.Battle.CardComponent"), "a class without an address: 0 (recognised by name)");
    Eq(906, l.Const("EComponentType.Card"), "a const");
    Eq(-108L, l.Form("ID_FORM_UNIONWEEKBOSSBATTLEEND"), "a negative form id");
    bool threw = false; try { l.Const("Nope.Nope"); } catch (KeyNotFoundException) { threw = true; }
    Check(threw, "an unknown key throws");
    Check(GameLayout.Parse(fx, "9.8.7.6.4") == null, "data for another version is not used");
    Check(GameLayout.Parse(fx.Replace("\"v\": 1", "\"v\": 2"), FixtureGame) == null, "another format is not used");
    foreach (var k in new[] { "EComponentType.Card", "DLNode", "SdpPair", "arrData", "ID_FORM_BATTLE" })
      Check(GameLayout.Parse(Without(fx, k), FixtureGame) == null, "a reply without " + k + " is not used");
    Check(GameLayout.Parse(Without(fx, "CSharpBattle.Battle.GameSimulation"), FixtureGame) != null, "a class may be missing");
    Check(GameLayout.Parse(fx.Replace("\"DLNode\": 64", "\"DLNode\": \"64\""), FixtureGame) == null, "a string value is refused");
    Check(GameLayout.Parse(fx.Replace("\"DLNode\": 64", "\"DLNode\": 1.5"), FixtureGame) == null, "a fraction is refused");
    Check(GameLayout.Parse(fx.Replace("\"DLNode\": 64", "\"DLNode\": -64"), FixtureGame) == null, "a negative size is refused");
    Check(GameLayout.Parse("[1]", FixtureGame) == null && GameLayout.Parse("<html>", FixtureGame) == null && GameLayout.Parse(null, FixtureGame) == null, "not an object: null");
    Check(GameLayout.Parse(fx.Replace("\"game\"", "\"pad\": \"" + new string('x', 70000) + "\", \"game\""), FixtureGame) == null, "an oversized reply is refused");
    // the build fingerprint: data made for another GameAssembly.dll is not used
    string asmA = new string('a', 64), asmB = new string('b', 64);
    string fxAsm = fx.Replace("\"v\": 1", "\"v\": 1, \"asm\": \"" + asmA + "\"");
    Check(GameLayout.Parse(fxAsm, FixtureGame, asmA) != null, "asm: the same build is accepted");
    Check(GameLayout.Parse(fxAsm, FixtureGame, asmA.ToUpperInvariant()) != null, "asm: letter case does not matter");
    {
      var lo = GameLayout.Parse(fxAsm.Replace("\"v\": 1", "\"v\": 1, \"field\": {\"InfinityGrid.CellPerLine\": 12}"), FixtureGame, asmB);
      Check(lo != null && lo.RvaDropped, "asm: another build is used, with the class addresses dropped");
      Eq(0UL, lo.Rva("BattleView.BattleManager"), "  ... no class address");
      Eq(909, lo.Const("EComponentType.Map"), "  ... consts intact");
      Eq(-108L, lo.Form("ID_FORM_UNIONWEEKBOSSBATTLEEND"), "  ... forms intact");
      Eq(64, lo.Span("DLNode"), "  ... spans intact");
      Eq(12, lo.FieldOffset("InfinityGrid.CellPerLine"), "  ... field offsets intact");
      var same = GameLayout.Parse(fxAsm, FixtureGame, asmA);
      Check(same != null && !same.RvaDropped, "asm: the same build keeps the class addresses");
      Eq(8192UL, same.Rva("BattleView.BattleManager"), "  ... the address");
      Check(GameLayout.Parse(fxAsm.Replace("\"v\": 1", "\"v\": 2"), FixtureGame, asmB) == null, "asm: another build, another format: refused");
      Check(GameLayout.Parse(Without(fxAsm, "DLNode"), FixtureGame, asmB) == null, "asm: another build, a needed entry missing: refused");
    }
    Check(GameLayout.Parse(fx, FixtureGame, asmB) != null, "asm: data without one is accepted for any build");
    Check(GameLayout.Parse(fxAsm, FixtureGame, null) != null, "asm: no local hash, nothing to compare: accepted");
    Check(GameLayout.Parse(fxAsm.Replace(asmA, "7"), FixtureGame, asmB) == null, "asm: a malformed fingerprint is refused");
    GameLayout gotA;
    Eq(Layout.Reply.Got, Layout.Interpret(200, fxAsm, FixtureGame, asmA, out gotA), "asm: reply for this build: got");
    Eq(Layout.Reply.Got, Layout.Interpret(200, fxAsm, FixtureGame, asmB, out gotA), "asm: reply for another build: got");
    Check(gotA != null && gotA.RvaDropped, "  ... without the class addresses");
    Eq(Layout.Reply.OtherBuild, Layout.Interpret(200, fxAsm.Replace(asmA, "7"), FixtureGame, asmB, out gotA), "asm: reply with a malformed fingerprint: refused");
    Check(gotA == null, "  ... and no data");
    Eq(GameLayout.Build.Other, GameLayout.BuildOf(fxAsm, asmB), "asm: build of: other");
    Eq(GameLayout.Build.Same, GameLayout.BuildOf(fxAsm, asmA), "asm: build of: same");
    Eq(GameLayout.Build.Malformed, GameLayout.BuildOf(fxAsm.Replace(asmA, "7"), asmB), "asm: build of: malformed");
    string asmDir = Path.Combine(Path.GetTempPath(), "rf-layout-asm-" + Guid.NewGuid().ToString("N"));
    Layout.SaveCache(asmDir, FixtureGame, fxAsm);
    Check(Layout.LoadCache(asmDir, FixtureGame, asmA) != null, "asm: the saved copy of this build is used");
    var cacheB = Layout.LoadCache(asmDir, FixtureGame, asmB);
    Check(cacheB != null && cacheB.RvaDropped, "asm: the saved copy of another build is used without its class addresses");
    Check(Layout.CacheIsOtherBuild(asmDir, FixtureGame) == (Layout.LocalAsm != null), "asm: the cache is seen as another build only with a different local hash");
    try { Directory.Delete(asmDir, true); } catch (Exception) { }
    Check(GameInfo.TitleIsGame("Watcher of Realms") && GameInfo.TitleIsGame("Watcher of Realms - US") && GameInfo.TitleIsGame("[Store] Watcher of Realms")
          && GameInfo.TitleIsGame("워처 오브 렐름") && !GameInfo.TitleIsGame("Watcher") && !GameInfo.TitleIsGame(""), "window title: the name anywhere, or the Korean one");
    Check(GameInfo.ForeignGameWindow(0, new[] { "Watcher of Realms - Alex" }, new[] { "ApplicationFrameHost" }), "foreign window: an emulator-like window and no client -> hint");
    Check(!GameInfo.ForeignGameWindow(1234, new[] { "Watcher of Realms - Alex" }, new[] { "HD-Player" }), "foreign window: the client runs -> no hint");
    Check(!GameInfo.ForeignGameWindow(0, new[] { "Watcher of Realms forum - Chrome", "Notes" }, new[] { "chrome", "notepad" }) && !GameInfo.ForeignGameWindow(0, null, null)
          && !GameInfo.ForeignGameWindow(0, new string[0], new string[0]), "foreign window: a browser tab, other windows or none -> no hint");
    string lowDir = Path.Combine(Path.GetTempPath(), "rf-low-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(lowDir, "Other Co", "Other Game")); Directory.CreateDirectory(Path.Combine(lowDir, "Pub", "Watcher of Realms US"));
    File.WriteAllText(Path.Combine(lowDir, "Other Co", "Other Game", "Player.log"), "Initialize engine version\nsomething else", Encoding.UTF8);
    File.WriteAllText(Path.Combine(lowDir, "Pub", "Watcher of Realms US", "Player.log"), "Initialize engine version\nD:/Games/Watcher of Realms_Data", Encoding.UTF8);
    Eq(Path.Combine(lowDir, "Pub", "Watcher of Realms US"), GamePacks.FindLogDir(lowDir, null), "log folder: found by the game's name in the log");
    Check(GamePacks.FindLogDir(Path.Combine(lowDir, "Other Co"), null) == null, "log folder: none when no log mentions the game");
    try { Directory.Delete(lowDir, true); } catch (Exception) { }
    Check(GameLayout.IsVersion("1.6.39.829.1") && GameLayout.IsVersion("1.2") && !GameLayout.IsVersion("1") && !GameLayout.IsVersion("../1.2")
          && !GameLayout.IsVersion("1.2.3.4.5.6.7") && !GameLayout.IsVersion(null), "game versions: digits and dots only");
    GameLayout got;
    Eq(Layout.Reply.Got, Layout.Interpret(200, fx, FixtureGame, out got), "200 with the data: got");
    Check(got != null, "  ... and parsed");
    Eq(Layout.Reply.None, Layout.Interpret(404, "{\"error\":\"unknown_version\"}", FixtureGame, out got), "404: none for this version");
    Eq(Layout.Reply.NoReply, Layout.Interpret(500, fx, FixtureGame, out got), "5xx: no reply (what is held stays)");
    Eq(Layout.Reply.NoReply, Layout.Interpret(200, "{}", FixtureGame, out got), "200 with junk: no reply");
    Eq(2, Layout.RetryMinutes(true), "test build asks again every 2 minutes");
    Eq(10, Layout.RetryMinutes(false), "release build asks again every 10 minutes");
    // the saved copies, per version
    string dir = Path.Combine(Path.GetTempPath(), "rf-layout-" + Guid.NewGuid().ToString("N"));
    Check(Layout.LoadCache(dir, FixtureGame) == null, "no saved copy: null");
    Layout.SaveCache(dir, FixtureGame, fx);
    var c = Layout.LoadCache(dir, FixtureGame);
    Check(c != null && c.Const("EComponentType.Map") == 909, "the saved copy is used offline");
    Check(Layout.LoadCache(dir, "9.8.7.6.4") == null, "no copy for another version");
    Layout.SaveCache(dir, "1.1.1", fx);
    Check(Layout.LoadCache(dir, "1.1.1") == null, "a copy whose data names another version is not used");
    File.WriteAllText(Path.Combine(dir, FixtureGame + ".json"), "{broken");
    Check(Layout.LoadCache(dir, FixtureGame) == null, "a broken copy: null");
    Layout.SaveCache(dir, "../x", fx);
    Check(!File.Exists(Path.Combine(dir, "..", "x.json")), "a bad version is never a path");
    try { Directory.Delete(dir, true); } catch (Exception) { }
    // the site (tests/mock-server.mjs)
    string json; const string LayoutCode = "rf_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    Eq(Layout.Reply.Got, Layout.Fetch(mock, LayoutCode, FixtureGame, out got, out json), "fetch: the data for the version");
    Check(got != null && json != null && json.Contains(FixtureGame), "  ... with the text to keep");
    Eq(Layout.Reply.None, Layout.Fetch(mock, LayoutCode, "1.2.3", out got, out json), "fetch: an unknown version is none");
    Eq(Layout.Reply.NoReply, Layout.Fetch(mock, LayoutCode, "5.0.0", out got, out json), "fetch: a server error is no reply");
    Eq(Layout.Reply.NoReply, Layout.Fetch("http://127.0.0.1:1", LayoutCode, FixtureGame, out got, out json), "fetch: no connection is no reply");
    Eq(Layout.Reply.NoReply, Layout.Fetch(mock, LayoutCode, "../../x", out got, out json), "fetch: a bad version is not asked for");
    Eq(Layout.Reply.Denied, Layout.Fetch(mock, "", FixtureGame, out got, out json), "fetch: without a sync code nothing is asked (denied)");
    Eq(Layout.Reply.Denied, Layout.Fetch(mock, "rf_" + new string('Z', 32), FixtureGame, out got, out json), "fetch: a code the site refuses (401) is denied");
    Eq(Layout.Reply.Denied, Layout.Interpret(403, "{\"error\":\"banned\"}", FixtureGame, out got), "403: denied");
    // without data the features that need it are off; the timeline builder refuses to start
    var held = Layout.Current;
    Layout.Use(null);
    uint fr; int st;
    Check(!RFX.SimClock(0x10000, out fr, out st) && st == RFX.SimOther, "no data: no fight clock");
    Eq(0UL, RFX.CurrentSim(), "no data: no fight");
    Check(RFX.DumpBattleStats() == null && RFX.DumpHeroFights() == null, "no data: no battle study");
    threw = false; try { new TimelineBuilder(); } catch (InvalidOperationException) { threw = true; }
    Check(threw, "no data: no fight timeline");
    Layout.Use(held);
    // the layout files the site serves (the web repo next to this one, when it is there): each is complete for the app
    string web = Path.Combine(WebRoot(), "data", "app-layout");
    if (Directory.Exists(web)) {
      int n = 0;
      foreach (var p in Directory.GetFiles(web, "*.json")) {
        string v = Path.GetFileNameWithoutExtension(p);
        int at = v.IndexOf('@');
        if (at < 0) {
          Check(GameLayout.Parse(File.ReadAllText(p, Encoding.UTF8), v) != null, "the site's layout file " + v + " has everything the app reads");
        } else {
          // a per-build file: <version>@<asm>.json, parsed with its own asm as the local one (so it is read fully)
          string ver = v.Substring(0, at), asm = v.Substring(at + 1);
          var lb = GameLayout.Parse(File.ReadAllText(p, Encoding.UTF8), ver, asm);
          Check(lb != null && !lb.RvaDropped, "the site's layout file " + v + " has everything the app reads");
          Check(lb != null && lb.Asm == asm, "the site's layout file " + v + " carries the asm of its name");
        }
        n++;
      }
      Check(n > 0, "the site has layout files");
    } else Console.WriteLine("  (no ../wardsage-web: the site's layout files not checked)");
    // the optional "field" section (the list grid's field offsets): data without it still parses, the lookup says "none" (-1)
    string fxj = FixtureJson(); string withField = fxj.Substring(0, fxj.LastIndexOf('}')) + ",\"field\": { \"InfinityGrid.CellPerLine\": 48, \"InfinityGrid.LineSize\": 120 } }";
    var lf = GameLayout.Parse(withField, FixtureGame);
    Check(lf != null && lf.FieldOffset("InfinityGrid.CellPerLine") == 48 && lf.FieldOffset("InfinityGrid.LineSize") == 120 && lf.FieldOffset("InfinityGrid.m_fitScale") == -1, "layout: the field section is read, a key it lacks is -1");
    Check(TestLayout().FieldOffset("InfinityGrid.LineSize") == -1, "layout: data without a field section parses, offsets are -1 (the picture is the fallback)");
    Check(GameLayout.Parse(withField.Replace("\"InfinityGrid.LineSize\": 120", "\"InfinityGrid.LineSize\": 99999"), FixtureGame) == null, "layout: a field offset out of range is not used");
    Check(GameLayout.Parse(withField.Replace("\"InfinityGrid.LineSize\": 120", "\"InfinityGrid.LineSize\": \"x\""), FixtureGame) == null, "layout: a field offset that is not a number is not used");
    // no grid fields in the data: the list reading says "unreadable" and never throws
    Layout.Use(TestLayout());
    Check(RFX.ReadList(1, "m_Content_ScrollRect", "m_Content_InfinityGrid", 1) == null, "list grid: no grid fields in the layout -> unreadable");
    Layout.Use(held);
    string v39 = Path.Combine(web, "1.6.39.829.1.json");
    if (File.Exists(v39)) {
      var l39 = GameLayout.Parse(File.ReadAllText(v39, Encoding.UTF8), "1.6.39.829.1");
      bool all = l39 != null; if (all) foreach (var k in GameLayout.FieldKeys) if (!k.StartsWith("StatisticsComponent.") && !k.StartsWith("Skill") && !k.StartsWith("StateScript.") && !k.StartsWith("BattleAttributeData.") && !k.StartsWith("stBattleBaseAttr.") && l39.FieldOffset(k) <= 0) all = false;
      Check(all, "the site's layout file 1.6.39.829.1 has the list grid's field offsets");
    }
    LayoutChecksums();
    SourceLiteralTests();
  }

  // The site's layout data for each known game version still has the values it was published with: a SHA-256 over
  // "section.key=integer" lines (sections and keys sorted, ordinal). Only the checksums live here, not the values.
  static string LayoutChecksum(string json) {
    var o = MiniJson.AsObject(MiniJson.TryParse(json));
    if (o == null) return null;
    var secs = new List<string>();
    foreach (var kv in o) if (MiniJson.AsObject(kv.Value) != null) secs.Add(kv.Key);
    secs.Sort(StringComparer.Ordinal);
    var sb = new StringBuilder();
    foreach (var sec in secs) {
      var s = MiniJson.AsObject(o[sec]);
      var keys = new List<string>(s.Keys); keys.Sort(StringComparer.Ordinal);
      foreach (var k in keys) {
        if (!(s[k] is double)) return null;
        sb.Append(sec).Append('.').Append(k).Append('=').Append(((long)(double)s[k]).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
      }
    }
    return Sha256(sb.ToString());
  }

  static void LayoutChecksums() {
    var sums = new Dictionary<string, string> {
      { "1.6.38.829.1", "8d00e7891af329c886a61260a9e3f73c8cc4b5d48dcd68f2a671ad6289b21b99" },
      { "1.6.39.829.1", "7138548316cfb63c9ca01d6b6211eb331390ed555b3f6ed3ed032673e9172da5" } };
    foreach (var kv in sums) {
      string path = null;
      foreach (var root in new[] { WebRoot(), Path.Combine("..", "realmforge-web") }) {
        string p = Path.Combine(root, "data", "app-layout", kv.Key + ".json");
        if (File.Exists(p)) { path = p; break; }
      }
      if (path == null) { Console.WriteLine("  (no site layout file for " + kv.Key + ": checksum not checked)"); continue; }
      var l = GameLayout.Parse(File.ReadAllText(path, Encoding.UTF8), kv.Key);
      Check(l != null, "the layout data for " + kv.Key + " parses");
      Check(LayoutChecksum(File.ReadAllText(path, Encoding.UTF8)) == kv.Value, "the layout data for " + kv.Key + " matches its checksum");
    }
  }

  // The app's code holds no game-build values: they come with the layout data. Every name the code looks up is one the
  // data must have; no long number literal (a class address or a form id) is in src/ or app/.
  static void SourceLiteralTests() {
    var files = new List<string>();
    foreach (var d in new[] { "src", "app" }) if (Directory.Exists(d)) files.AddRange(Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories));
    Check(files.Count > 20, "the app's sources found (" + files.Count + ")");
    var need = new HashSet<string>();
    foreach (var a in new[] { GameLayout.NeedConst, GameLayout.NeedSpan, GameLayout.NeedSize, GameLayout.NeedHdr, GameLayout.NeedForm }) foreach (var k in a) need.Add(k);
    // numbers that are not game-build values (limits, a date in a comment)
    var allowed = new HashSet<string> { "4294967295", "2000000000", "1000000000000", "10000000", "20261003" };
    foreach (var k in GameLayout.FieldKeys) need.Add(k);   // (optional keys: a lookup must name one of these)
    var bad = new List<string>(); var unknown = new List<string>(); int lookups = 0;
    var big = new System.Text.RegularExpressions.Regex(@"(?<![0-9A-Za-z_.""])-?([0-9]{8,})(?![0-9])");
    var look = new System.Text.RegularExpressions.Regex(@"\.(Const|Span|Size|Hdr|Form|FieldOffset)\(""([^""]+)""\)|\b(CT|Span|Hdr|FormId)\(""([^""]+)""\)");
    var consts = new System.Text.RegularExpressions.Regex(@"\bClassRvas\b|\bconst\s+int\s+CT_\w+\s*=\s*\d|\bCmd(PutTower|RetreatTower|PowerSkill)\s*=\s*\d|\bForm\w*Id\s*=\s*-?\d");
    foreach (var f in files) {
      if (Path.GetFileName(f) == "Layout.cs" || Path.GetFileName(f) == "DpsNames.cs") continue;   // (DpsNames.cs: generated skill and effect ids of the game data, not build values)
      string[] lines = File.ReadAllLines(f, Encoding.UTF8);
      for (int i = 0; i < lines.Length; i++) {
        foreach (System.Text.RegularExpressions.Match m in big.Matches(lines[i]))
          if (!allowed.Contains(m.Groups[1].Value)) bad.Add(f + ":" + (i + 1) + " " + m.Value);
        if (consts.IsMatch(lines[i])) bad.Add(f + ":" + (i + 1) + " " + lines[i].Trim());
        foreach (System.Text.RegularExpressions.Match m in look.Matches(lines[i])) {
          lookups++;
          string k = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value;
          if (m.Groups[3].Value == "CT") k = "EComponentType." + k;
          if (!need.Contains(k)) unknown.Add(f + ":" + (i + 1) + " " + k);
        }
      }
    }
    Check(bad.Count == 0, "no game-build numbers in src/ app/" + (bad.Count > 0 ? ": " + string.Join("; ", bad.ToArray()) : ""));
    Check(lookups > 40, "the code reads its game values by name (" + lookups + " lookups)");
    Check(unknown.Count == 0, "every name the code looks up is one the layout data must have" + (unknown.Count > 0 ? ": " + string.Join("; ", unknown.ToArray()) : ""));
  }

  // the site's switches (src/RemoteConfig.cs)
  static void MaintenanceTests() {
    Console.WriteLine("maintenance mode");
    Func<string, DateTime> T = x => DateTime.Parse(x, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
    // Parse: always present, off by default, unknown fields ignored
    Check(RemoteSwitches.Parse("{}").Maint.State == "off" && RemoteSwitches.Parse("{\"maintenance\":null}").Maint.State == "off" && RemoteSwitches.Parse("{\"maintenance\":{\"state\":\"off\"}}").Maint.State == "off", "maintenance: absent / null / off is off");
    Check(RemoteSwitches.Parse("{\"maintenance\":{\"state\":\"soon\"}}").Maint.State == "off" && RemoteSwitches.Parse("{\"maintenance\":\"on\"}").Maint.State == "off", "maintenance: unknown state / not an object is off");
    var full = RemoteSwitches.Parse("{\"readEnabled\":false,\"maintenance\":{\"state\":\"scheduled\",\"startsAt\":\"2026-11-10T01:00:00.000Z\",\"endsAt\":\"2026-11-10T03:00:00Z\",\"mode\":\"full\",\"ru\":\"Работы\",\"en\":\"Works\",\"link\":\"https://t.me/x\",\"preStopMin\":15,\"reopenMin\":20,\"reopenPct\":30,\"future\":{\"a\":1}}}");
    var fm = full.Maint;
    Check(fm.State == "scheduled" && fm.Mode == "full" && fm.StartsAt == T("2026-11-10T01:00:00Z") && fm.EndsAt == T("2026-11-10T03:00:00Z") && fm.Ru == "Работы" && fm.En == "Works" && fm.Link == "https://t.me/x"
      && fm.PreStopMin == 15 && fm.ReopenMin == 20 && fm.ReopenPct == 30, "maintenance: every field read");
    Check(RemoteSwitches.Parse("{\"maintenance\":{\"state\":\"on\",\"link\":{\"href\":\"https://d.gg/y\"}}}").Maint.Link == "https://d.gg/y", "maintenance: link as an object");
    var odd = RemoteSwitches.Parse("{\"maintenance\":{\"state\":\"on\",\"startsAt\":\"junk\",\"mode\":\"x\",\"preStopMin\":-5,\"reopenMin\":\"20\",\"reopenPct\":500}}").Maint;
    Check(odd.State == "on" && !odd.HasStart && !odd.HasEnd && odd.Mode == "readonly" && odd.PreStopMin == 15 && odd.ReopenMin == 20 && odd.ReopenPct == 100, "maintenance: odd values fall back to the defaults");
    Check(!Array.Exists(RemoteSwitches.Features, f => f == "maintenance"), "maintenance is no feature");
    Check(full.Allows("battle") == false && RemoteSwitches.Parse("{\"maintenance\":{\"state\":\"on\"}}").Allows("battle"), "maintenance does not change readEnabled by itself");
    // the saved copy and SameAs
    var back = RemoteSwitches.Parse(full.ToJson());
    Check(back.Maint.SameAs(fm) && back.SameAs(full), "maintenance survives the saved copy");
    Check(!full.SameAs(RemoteSwitches.Parse("{\"readEnabled\":false}")) && RemoteSwitches.Parse("{}").ToJson().Contains("\"maintenance\":{\"state\":\"off\"}"), "maintenance: SameAs sees it, off is saved as {state:off}");
    string path = Path.Combine(Path.GetTempPath(), "rf-maint-" + Guid.NewGuid().ToString("N") + ".json");
    full.SaveCache(path); var lc = RemoteSwitches.LoadCache(path); File.Delete(path);
    Check(lc != null && lc.Maint.State == "scheduled" && lc.Maint.EndsAt == fm.EndsAt, "maintenance: cache round trip");
    // the gate
    var starts = T("2026-11-10T01:00:00Z"); var ends = T("2026-11-10T03:00:00Z");
    var sch = new Maintenance { State = "scheduled", StartsAt = starts, EndsAt = ends };
    Check(MaintenanceGate.StopAt(sch) == T("2026-11-10T00:45:00Z"), "gate: stop = startsAt - preStop");
    Check(!MaintenanceGate.Active(sch, T("2026-11-10T00:44:59Z"), 7) && MaintenanceGate.Active(sch, T("2026-11-10T00:45:00Z"), 7) && MaintenanceGate.Active(sch, T("2026-11-10T02:00:00Z"), 7), "gate: active from startsAt - preStop");
    Eq("soon", MaintenanceGate.Phase(sch, T("2026-11-10T00:00:00Z"), 7), "phase: soon");
    Eq("stopped", MaintenanceGate.Phase(sch, T("2026-11-10T00:50:00Z"), 7), "phase: stopped (reading off, works not begun)");
    Eq("on", MaintenanceGate.Phase(sch, T("2026-11-10T01:30:00Z"), 7), "phase: on");
    Eq("off", MaintenanceGate.Phase(new Maintenance(), T("2026-11-10T01:30:00Z"), 7), "phase: off");
    Eq("off", MaintenanceGate.Phase(sch, T("2026-11-10T05:00:00Z"), 7), "phase: a stale 'scheduled' long after the end is off");
    // resume: endsAt + rand(0..reopenMin) by the seed, stable, different seeds differ
    bool allIn = true; var seen = new System.Collections.Generic.HashSet<long>();
    for (int sd = 1; sd <= 200; sd++) {
      var r = MaintenanceGate.ResumeAt(sch, sd);
      if (r < ends || r > ends.AddMinutes(20)) allIn = false;
      if (r != MaintenanceGate.ResumeAt(sch, sd)) allIn = false;
      seen.Add(r.Ticks);
    }
    Check(allIn, "gate: ResumeAt is within endsAt .. endsAt + reopenMin and stable per seed");
    Check(seen.Count > 50, "gate: different seeds wake at different moments (" + seen.Count + " of 200)");
    Check(MaintenanceGate.ResumeAt(new Maintenance { State = "on", EndsAt = ends, ReopenMin = 0 }, 9) == ends, "gate: reopenMin 0 wakes at endsAt");
    Check(MaintenanceGate.ResumeAt(new Maintenance { State = "on" }, 9) == DateTime.MinValue, "gate: no end, no resume time");
    int s1 = 3, s2 = 4; while (MaintenanceGate.ResumeAt(sch, s2) == MaintenanceGate.ResumeAt(sch, s1)) s2++;
    var w1 = MaintenanceGate.ResumeAt(sch, s1); var w2 = MaintenanceGate.ResumeAt(sch, s2); var early = w1 < w2 ? w1 : w2; var late = w1 < w2 ? w2 : w1;
    int sEarly = w1 < w2 ? s1 : s2, sLate = w1 < w2 ? s2 : s1;
    Check(!MaintenanceGate.Active(sch, early.AddSeconds(1), sEarly) && MaintenanceGate.Active(sch, early.AddSeconds(1), sLate) && !MaintenanceGate.Active(sch, late.AddSeconds(1), sLate), "gate: the early seed wakes first, the late one later");
    Check(MaintenanceGate.Active(new Maintenance { State = "on" }, T("2026-11-10T02:00:00Z"), 1), "gate: 'on' without an end stays quiet");
    Check(!MaintenanceGate.Active(new Maintenance { State = "scheduled" }, T("2026-11-10T02:00:00Z"), 1), "gate: 'scheduled' without a start is not active");
    // the rollout
    int in30 = 0; for (int sd = 0; sd < 1000; sd++) if (MaintenanceGate.InRollout(sd, 30)) in30++;
    Check(in30 > 200 && in30 < 400, "gate: InRollout(30) is about 30 % (" + in30 + " of 1000)");
    Check(MaintenanceGate.InRollout(5, 100) && !MaintenanceGate.InRollout(5, 0) && MaintenanceGate.InRollout(5, 50) == MaintenanceGate.InRollout(5, 50), "gate: rollout 100 / 0 / stable");
    var reo = new Maintenance { State = "reopening", EndsAt = ends, ReopenMin = 20, ReopenPct = 50 };
    int sIn = 0, sOut = 0; for (int sd = 1; sd < 100 && (sIn == 0 || sOut == 0); sd++) { if (MaintenanceGate.InRollout(sd, 50)) { if (sIn == 0) sIn = sd; } else if (sOut == 0) sOut = sd; }
    Check(MaintenanceGate.WakeAt(reo, sOut) == ends.AddMinutes(20) && MaintenanceGate.WakeAt(reo, sIn) == MaintenanceGate.ResumeAt(reo, sIn), "gate: outside the rollout waits the full reopenMin, inside wakes at its moment");
    Check(MaintenanceGate.Active(reo, ends.AddMinutes(19), sOut) && !MaintenanceGate.Active(reo, ends.AddMinutes(21), sOut), "gate: reopening, outside the rollout, quiet until endsAt + reopenMin");
    Check(!MaintenanceGate.Active(new Maintenance { State = "reopening" }, ends, 1), "gate: reopening without an end is not active");
    // flag from a 503
    DateTime until; var now0 = T("2026-11-10T01:00:00Z");
    Check(MaintenanceGate.ParseBody("{\"error\":\"maintenance\",\"maintenance\":true,\"until\":\"2026-11-10T03:00:00Z\"}", out until) && until == ends, "503 body: flag and until");
    Check(MaintenanceGate.ParseBody("{\"maintenance\":true}", out until) && until == DateTime.MinValue && MaintenanceGate.ParseBody("{\"error\":\"maintenance\"}", out until), "503 body: flag without until");
    Check(!MaintenanceGate.ParseBody("{\"error\":\"busy\"}", out until) && !MaintenanceGate.ParseBody("<html>", out until) && !MaintenanceGate.ParseBody(null, out until) && !MaintenanceGate.ParseBody("{\"maintenance\":\"true\"}", out until), "503 body: not a flag");
    Check(MaintenanceGate.IsHeader("1") && MaintenanceGate.IsHeader(" 1 ") && !MaintenanceGate.IsHeader("0") && !MaintenanceGate.IsHeader(null) && !MaintenanceGate.IsHeader("true"), "header X-Wardsage-Maintenance: 1");
    var fr = MaintenanceGate.FlagResume(ends, -1, 20, 5, now0);
    Check(fr >= ends && fr <= ends.AddMinutes(20), "flag: until plus the spread");
    Check(MaintenanceGate.FlagResume(DateTime.MinValue, 120, 20, 5, now0) == now0.AddSeconds(120), "flag: no until, Retry-After seconds");
    Check(MaintenanceGate.FlagResume(DateTime.MinValue, -1, 20, 5, now0) == now0.AddSeconds(300), "flag: nothing known, 5 minutes");
    Check(MaintenanceGate.FlagResume(now0.AddDays(5), -1, 20, 5, now0) == now0.AddHours(6), "flag: capped at 6 hours");
    Check(MaintenanceGate.FlagResume(DateTime.MinValue, 999999, 20, 5, now0) == now0.AddSeconds(3600), "flag: Retry-After capped at 1 hour");
    // the silence of the network reports
    var quietTo = MaintenanceGate.SilentUntil(ends, now0);
    Check(quietTo == ends.AddMinutes(15) && MaintenanceGate.SilentUntil(DateTime.MinValue, now0) == now0.AddMinutes(15), "silence: until the end + 15 minutes");
    Check(MaintenanceGate.ReportSilenced("sync.net", now0, quietTo) && MaintenanceGate.ReportSilenced("sync.server", ends.AddMinutes(14), quietTo) && MaintenanceGate.ReportSilenced("net", now0, quietTo), "silence: network kinds are quiet during the window and 15 minutes after");
    Check(!MaintenanceGate.ReportSilenced("sync.net", ends.AddMinutes(15), quietTo) && !MaintenanceGate.ReportSilenced("sync.net", now0, DateTime.MinValue), "silence: ends 15 minutes after, none without a window");
    Check(!MaintenanceGate.ReportSilenced("sync.read", now0, quietTo) && !MaintenanceGate.ReportSilenced("battle.no_stats", now0, quietTo) && !MaintenanceGate.ReportSilenced("unhandled", now0, quietTo) && !MaintenanceGate.ReportSilenced("live_poll_failed", now0, quietTo), "silence: other kinds go on");
  }

  static void RemoteSwitchesTests() {
    Console.WriteLine("Remote switches");
    var d = RemoteSwitches.Permissive();
    Check(d.ReadEnabled && d.Allows("guild") && d.Allows("locate") && d.Off().Count == 0, "defaults: everything on");
    Check(!d.Allows("arena") && !d.Allows(null), "an unknown feature is never allowed");
    var r = RemoteSwitches.Interpret(200, "{\"readEnabled\":true,\"features\":{\"guild\":false,\"gvg\":true,\"battle\":false,\"autoclick\":true,\"equip\":true,\"locate\":true},\"at\":\"2026-10-12T10:00:00.000Z\"}");
    Check(r != null && r.ReadEnabled && !r.Allows("guild") && !r.Allows("battle") && r.Allows("gvg") && r.Allows("equip"), "features off as sent");
    Eq("guild,battle", r == null ? "" : string.Join(",", r.Off().ToArray()), "the features off, in order");
    Eq("2026-10-12T10:00:00.000Z", r == null ? null : r.At, "reply time kept");
    var m = RemoteSwitches.Parse("{\"readEnabled\":false,\"features\":{\"guild\":true}}");
    Check(m != null && !m.ReadEnabled && !m.Allows("guild") && !m.Allows("equip") && m.Off().Count == 0, "reading off: nothing is allowed, the notice covers all");
    var junk = RemoteSwitches.Parse("{\"readEnabled\":\"false\",\"features\":{\"equip\":0,\"locate\":null},\"extra\":1}");
    Check(junk != null && junk.ReadEnabled && junk.Allows("equip") && junk.Allows("locate"), "odd values read as on");
    Check(RemoteSwitches.Parse("{}") != null && RemoteSwitches.Parse("{}").Allows("battle"), "missing fields are on");
    Check(RemoteSwitches.Interpret(500, "{\"readEnabled\":false}") == null, "a 5xx is no reply (the held switches stay)");
    Check(RemoteSwitches.Interpret(200, "<html>") == null && RemoteSwitches.Interpret(200, "[1]") == null && RemoteSwitches.Interpret(200, null) == null, "not a JSON object: no reply");
    Check(RemoteSwitches.Interpret(200, "{\"a\":\"" + new string('x', 20000) + "\"}") == null, "an oversized reply is refused");
    var back = RemoteSwitches.Parse(r.ToJson());
    Check(back != null && back.SameAs(r) && back.At == r.At, "the saved copy reads back the same");
    Check(!back.SameAs(m) && !back.SameAs(null), "SameAs tells switches apart");
    // SiteFallback: on by default, off only by an explicit features.siteFallback === false; not a UI feature; kept in the saved copy
    Check(new RemoteSwitches().SiteFallback && RemoteSwitches.Parse("{}").SiteFallback && RemoteSwitches.Parse("{\"features\":{}}").SiteFallback, "siteFallback: on by default");
    Check(RemoteSwitches.Parse("{\"features\":{\"siteFallback\":true}}").SiteFallback && RemoteSwitches.Parse("{\"features\":{\"siteFallback\":0}}").SiteFallback
      && RemoteSwitches.Parse("{\"features\":{\"siteFallback\":\"false\"}}").SiteFallback && RemoteSwitches.Parse("{\"features\":{\"siteFallback\":null}}").SiteFallback, "siteFallback: an odd value keeps it on");
    var sfOff = RemoteSwitches.Parse("{\"readEnabled\":true,\"features\":{\"siteFallback\":false,\"guild\":true}}");
    Check(!sfOff.SiteFallback && sfOff.Off().Count == 0 && sfOff.Allows("guild"), "siteFallback false: off, and not shown among the switched-off features");
    var sfBack = RemoteSwitches.Parse(sfOff.ToJson());
    Check(!sfBack.SiteFallback && sfBack.SameAs(sfOff) && !sfBack.SameAs(new RemoteSwitches()), "siteFallback false survives the saved copy, SameAs sees it");
    Eq(2, RemoteSwitches.EveryMinutes(true), "test build asks every 2 minutes");
    Eq(10, RemoteSwitches.EveryMinutes(false), "release build asks every 10 minutes");
    string dir = Path.Combine(Path.GetTempPath(), "rf-remote-" + Guid.NewGuid().ToString("N"));
    string path = Path.Combine(dir, "app-config.json");
    Check(RemoteSwitches.LoadCache(path) == null, "no saved copy: null (the caller uses the defaults)");
    m.SaveCache(path);
    var c = RemoteSwitches.LoadCache(path);
    Check(c != null && !c.ReadEnabled, "the saved copy is used while the site cannot be reached");
    File.WriteAllText(path, "{broken");
    Check(RemoteSwitches.LoadCache(path) == null, "a broken copy: null");
    // the site's announcement: parsed, kept in the saved copy, cleared when the site sends null
    var an = RemoteSwitches.Parse("{\"readEnabled\":true,\"features\":{},\"announcement\":{\"id\":\"a1\",\"ru\":\"Рестарт <b>сервера</b>\",\"en\":\"Restart\",\"tone\":\"warn\",\"until\":\"2099-01-01T00:00:00.000Z\",\"audience\":\"all\",\"link\":{\"href\":\"/news\",\"ru\":\"Подробнее\",\"en\":\"More\"}}}");
    Check(an != null && an.Notice != null && an.Notice.Ru == "Рестарт <b>сервера</b>" && an.Notice.En == "Restart" && an.Notice.Tone == "warn" && an.Notice.LinkHref == "/news" && an.Notice.Live(DateTime.UtcNow), "announcement parsed");
    Check(!an.Notice.Live(new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc)), "an expired announcement is not live");
    Check(RemoteSwitches.Parse("{\"announcement\":null}").Notice == null && RemoteSwitches.Parse("{}").Notice == null && RemoteSwitches.Parse("{\"announcement\":{\"ru\":\" \",\"en\":\"\"}}").Notice == null, "no announcement: null");
    string dir2 = Path.Combine(Path.GetTempPath(), "rf-remote-" + Guid.NewGuid().ToString("N"));
    string path2 = Path.Combine(dir2, "app-config.json");
    an.SaveCache(path2);
    var an2 = RemoteSwitches.LoadCache(path2);
    Check(an2 != null && an2.Notice != null && an2.Notice.SameAs(an.Notice) && an2.Notice.LinkRu == "Подробнее" && an2.SameAs(an), "the announcement survives the saved copy (restart, site down)");
    Check(!an.SameAs(RemoteSwitches.Parse("{\"readEnabled\":true,\"features\":{}}")), "a cleared announcement is a change");
    try { Directory.Delete(dir2, true); } catch (Exception) { }
    try { Directory.Delete(dir, true); } catch (Exception) { }
  }

  // ParseTable-shaped script tables (arrays "[1]".., numbers long) for the arena opponents (src/ArenaOpponents.cs)
  static Dictionary<string, object> Lt(params object[] kv) {
    var d = new Dictionary<string, object>();
    for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
    return d;
  }
  static Dictionary<string, object> LArr(params object[] xs) {
    var d = new Dictionary<string, object>();
    for (int i = 0; i < xs.Length; i++) d["[" + (i + 1) + "]"] = xs[i];
    return d;
  }

  static void UpdateKeysTests() {
    Console.WriteLine("UpdateKeys");
    var testKey = RSA.Create(); testKey.KeySize = 2048;
    var other = RSA.Create(); other.KeySize = 2048;
    string n = Convert.ToBase64String(testKey.ExportParameters(false).Modulus).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var data = Encoding.UTF8.GetBytes("build");
    byte[] sig = testKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    Check(UpdateKeys.VerifyAny(data, sig, new[] { n }), "a signature verifies with its key");
    Check(!UpdateKeys.VerifyAny(data, other.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), new[] { n }), "another key's signature is rejected");
    Check(!UpdateKeys.VerifyAny(Encoding.UTF8.GetBytes("changed"), sig, new[] { n }), "a changed file is rejected");
    Check(Array.IndexOf(UpdateKeys.Accepted(false), UpdateKeys.TestKeyN) < 0, "the normal build does not trust the test key");
    Check(Array.IndexOf(UpdateKeys.Accepted(false), UpdateKeys.KeyN) >= 0 && UpdateKeys.Accepted(false).Length == 1, "the normal build trusts the main key only");
    Check(Array.IndexOf(UpdateKeys.Accepted(true), UpdateKeys.TestKeyN) >= 0 && Array.IndexOf(UpdateKeys.Accepted(true), UpdateKeys.KeyN) >= 0, "the test build trusts the main and the test key");
    Check(UpdateKeys.KeyN != UpdateKeys.TestKeyN, "the two keys differ");
  }

  static void ArenaOppTests() {
    Console.WriteLine("Arena opponents");
    var hero = Lt("iHeroId", 203400000L, "iBaseId", 2034L, "iLevel", 60L, "iStarLevel", 6L, "iSublimLevel", 6L, "iAwakeningFlag", 3L,
                  "iPower", 93829L, "iSquadId", 1L, "iLordPosition", 0L, "mSkillLevel", Lt("[0]", 5L, "[1]", 3L),
                  "mAttr", Lt("[1]", 12000L, "[7]", 250000L, "[24]", 3500L), "vSkills", LArr(101L, 102L));
    var hero2 = Lt("iHeroId", 209700000L, "iBaseId", 2097L, "iLevel", 60L, "iStarLevel", 6L, "iSublimLevel", 5L, "iPower", 70294L, "RealPower", 70300L,
                   "mAttr", Lt("[1]", 9000L));
    var opp = Lt("stRole", Lt("iZoneId", 4L, "iUid", 1234567L, "iRoleId", 1234567L), "sName", "Враг \"1\"", "iLevel", 70L, "iScore", 2450L,
                 "iRank", 0L, "iRankId", 13L, "bRobot", false, "iPower", 164123L, "iChallengedTimes", 1L,
                 "vHeroData", LArr(hero, hero2),
                 "vFrameInfos", LArr(Lt("iIdx", 43L, "iCmd", 1000L, "iUid", 2L, "vParams", LArr(2097L, 0L, 7L, 0L)), Lt("iIdx", 601L, "iCmd", 1000L, "iUid", 2L, "vParams", LArr(2034L, 3L, 5L, 270L))),
                 "mStageHeroBasicData", Lt("[6002631]", LArr(hero)));
    var bot = Lt("stRole", Lt("iZoneId", 4L, "iUid", 77L), "sName", "Bot", "iScore", 2300L, "bRobot", true, "vHeroData", LArr(hero2), "iPower", 70300L);
    var pvp = Lt("m_iPVPLastStageID", 6002631L, "m_iWeekIndex", 12L, "m_PvpRuleId", 4L, "m_iScore", 2400L, "m_iPower", 380000L,
                 "m_iFreeRefreshOpponentNum", 1L, "m_iFreeRefreshCountInterval", 900L, "m_LastManualRefreshTime", 1790000000L,
                 "m_iLastPushOpponentTime", 1790000001L, "m_vHeroData", LArr(Lt("iHeroId", 235300001L, "iBaseId", 2353L)),
                 "m_stFightRole", Lt("iUid", 1234567L, "iZoneId", 4L));
    var opps = new List<Dictionary<string, object>> { opp, bot };
    string json = ArenaOpp.Json(pvp, opps);
    var o = MiniJson.AsObject(MiniJson.TryParse(json));
    Check(o != null, "opponents JSON parses: " + (json.Length > 300 ? json.Substring(0, 300) : json));
    if (o == null) return;
    Eq(6002631.0, (double)o["stage"], "stage");
    Eq(1, ((List<object>)o["team"]).Count, "the player's attack team");
    var refresh = (Dictionary<string, object>)o["refresh"];
    Eq(900.0, (double)refresh["interval"], "refresh interval");
    Eq(1790000001.0, (double)refresh["pushAt"], "last push");
    var list = (List<object>)o["opponents"];
    Eq(2, list.Count, "two opponents");
    var o1 = (Dictionary<string, object>)list[0];
    Eq("Враг \"1\"", (string)o1["name"], "name with quotes");
    Eq(2450.0, (double)o1["score"], "score");
    Eq(false, (bool)o1["robot"], "a player");
    var hs = (List<object>)o1["heroes"];
    Eq(2, hs.Count, "two defence heroes");
    var h1 = (Dictionary<string, object>)hs[0];
    Eq(2034.0, (double)h1["id"], "hero base id");
    Eq(250000.0, (double)((Dictionary<string, object>)h1["attr"])["7"], "mAttr HP by attr id");
    Eq(5.0, (double)((Dictionary<string, object>)h1["sl"])["0"], "skill level 0");
    Eq(70300.0, (double)((Dictionary<string, object>)hs[1])["pw"], "RealPower preferred");
    var fr = (List<object>)o1["frames"];
    Eq(2, fr.Count, "two recorded commands");
    Eq("601,1000,2,2034,3,5,270", string.Join(",", ((List<object>)fr[1]).ConvertAll(x => ((double)x).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()), "command [frame, cmd, ctl, params]");
    Check(((Dictionary<string, object>)o1["stageHeroes"]).ContainsKey("6002631"), "stage heroes by stage");
    Eq(true, (bool)((Dictionary<string, object>)list[1])["robot"], "a bot");
    // the signature: the same reading gives the same one, a score change another
    string s1 = ArenaOpp.Signature(pvp, opps);
    Eq(s1, ArenaOpp.Signature(pvp, opps), "signature stable");
    bot["iScore"] = 2310L;
    Check(s1 != ArenaOpp.Signature(pvp, opps), "a score change changes the signature");
    // the fight's start: the level and the opponent of the fight
    var fj = MiniJson.AsObject(MiniJson.TryParse(ArenaOpp.FightJson(6001631, 1700, 3, pvp, opps)));
    Check(fj != null && (double)fj["level"] == 1700 && fj.ContainsKey("opp"), "fight JSON with the level and the opponent");
    if (fj != null && fj.ContainsKey("opp")) {
      var fo = (Dictionary<string, object>)fj["opp"];
      Eq(164123.0, (double)fo["power"], "opponent power"); Eq(164129.0, (double)fo["heroes"], "opponent heroes power"); Eq(93829.0, (double)fo["top"], "opponent top hero");
    }
    Check(!MiniJson.AsObject(MiniJson.TryParse(ArenaOpp.FightJson(6001631, 1700, 3, null, null))).ContainsKey("opp"), "no PVPData: level only");
    // a kept fight -> a record of the level fit (the sides from the statistic lines "c")
    string battle = "{\"arenaInfo\":" + ArenaOpp.FightJson(6001631, 1700, 3, pvp, opps) + ",\"stage\":6001631,\"at\":\"2026-10-03T10:46:30Z\",\"lines\":[" +
      "{\"c\":1,\"at\":\"A\",\"iBaseID\":2353,\"iPower\":84241},{\"c\":1,\"at\":\"B\",\"iBaseID\":2034,\"iPower\":66809}," +
      "{\"c\":2,\"at\":\"C\",\"iBaseID\":2034,\"iPower\":93829},{\"at\":\"D\",\"iBaseID\":1,\"iPower\":5},{\"sim\":\"TDGameSimulation\"}]}";
    var rec = MiniJson.AsObject(MiniJson.TryParse(ArenaOpp.FightRecord(battle)));
    Check(rec != null && (double)rec["level"] == 1700 && (double)rec["mySum"] == 151050 && (double)rec["myTop"] == 84241 && (double)rec["oppSum"] == 93829,
          "fight record: level and both sides power");
    Check(ArenaOpp.FightRecord("{\"stage\":6001631,\"lines\":[]}") == null, "a fight without arenaInfo is no record");
    // the player's own switches: controller 1, the listed commands only, the controller dropped
    string battleOps = battle.Substring(0, battle.Length - 1) + ",\"timeline\":{\"ops\":[[1,2015,1,3],[37,2001,1,1],[40,2001,2,1],[50,7,1],[140,2003,1,1]]}}";
    var recOps = ArenaOpp.FightRecord(battleOps, true);
    Check(recOps != null && recOps.Contains("\"myOps\":[[1,2015,3],[37,2001,1],[140,2003,1]]"), "fight record: myOps " + recOps);
    Check(!ArenaOpp.FightRecord(battle, true).Contains("myOps"), "no ops, no myOps");
    Eq("{\"v\":1,\"fights\":[]}", ArenaClient.WithFights("{\"v\":1}", null), "fights added");
    string det;
    Eq(PlansStatus.Ok, ArenaClient.Interpret(200, "{\"ok\":true}", out det), "200 ok");
    Eq(PlansStatus.InvalidToken, ArenaClient.Interpret(401, "{\"ok\":false,\"error\":\"invalid_token\"}", out det), "401");
    Check(ArenaClient.Interpret(422, "{\"ok\":false,\"error\":\"invalid_payload\",\"details\":[\"opponents: bad\"]}", out det) == PlansStatus.Unexpected && det != null && det.Contains("opponents: bad"), "422 details: " + det);
  }

  // the rating screen of one hero (Pretus, 22 modes): mode id, the score the screen shows, the voters
  static readonly double[][] PretusGrades = {
    new[] { 15, 5.0, 3061 }, new[] { 14, 5.0, 3032 }, new[] { 19, 5.0, 2340 }, new[] { 16, 5.0, 2314 }, new[] { 9, 5.0, 2450 }, new[] { 4, 5.0, 2713 },
    new[] { 5, 5.0, 2414 }, new[] { 11, 5.0, 2113 }, new[] { 2, 5.0, 2133 }, new[] { 12, 5.0, 2042 }, new[] { 1, 4.9, 1988 }, new[] { 8, 4.9, 1807 },
    new[] { 17, 4.9, 1604 }, new[] { 7, 4.9, 1738 }, new[] { 10, 4.9, 1668 }, new[] { 6, 4.9, 1717 }, new[] { 18, 4.8, 1434 }, new[] { 20, 4.8, 349 },
    new[] { 21, 4.8, 360 }, new[] { 3, 4.8, 1620 }, new[] { 23, 4.7, 28 }, new[] { 22, 4.5, 85 } };

  static void HeroGradeTests() {
    Console.WriteLine("Hero ratings");
    var rows = new List<HeroGradeRow>();
    foreach (var g in PretusGrades) {
      // the game keeps the stars' sum: an average a little under the shown (rounded up) value
      double sum = g[1] >= 5.0 ? g[1] * g[2] : Math.Round((g[1] - 0.03) * g[2]), shown;
      Check(HeroGrades.Shown(sum, g[2], out shown) && Math.Abs(shown - g[1]) < 1e-9, "mode " + g[0] + ": shown " + g[1].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " for " + g[2] + " voters");
      rows.Add(new HeroGradeRow { Mode = (int)g[0], Score = shown, Voters = (int)g[2], Sum = (long)sum });
    }
    double sc;
    Check(!HeroGrades.Shown(10, 0, out sc), "no voters: no score");
    Check(!HeroGrades.Shown(7000, 1000, out sc), "an average over 6 is not a star score");
    Check(HeroGrades.Shown(5400, 1000, out sc) && sc == 5.0, "an average a little over 5 shows 5.0");
    Check(HeroGrades.Shown(4001, 1000, out sc) && sc == 4.1, "rounded up to 0.1");
    Check(HeroGrades.Shown(4000, 1000, out sc) && sc == 4.0, "exact tenth stays");

    var q = new HeroGradeQueue();
    Eq(22, q.Offer(2282, rows), "22 modes of a hero queued");
    Eq(0, q.Offer(2282, rows), "the same hero and modes again this session: nothing new");
    Eq(1, q.Offer(2283, new List<HeroGradeRow> { rows[0] }), "another hero, same mode: new");
    q.NewSession();
    Eq(22, q.Offer(2282, rows), "a new game session takes the rows again");
    Eq(45, q.Count, "queue holds 22 + 1 + 22");
    Eq(0, new HeroGradeQueue().Offer(999, rows) + new HeroGradeQueue().Offer(10000, rows), "hero id outside the range: nothing");
    Eq(0, new HeroGradeQueue().Offer(2282, new List<HeroGradeRow> { new HeroGradeRow { Mode = 0, Score = 5, Voters = 5 }, new HeroGradeRow { Mode = 65, Score = 5, Voters = 5 },
      new HeroGradeRow { Mode = 4, Score = 5, Voters = 0 }, new HeroGradeRow { Mode = 4, Score = 5.5, Voters = 3 }, null }), "bad mode, no voters, score over 5: nothing");

    var big = new HeroGradeQueue(); int total = 0;
    for (int h = 2001; h < 2001 + 120; h++) total += big.Offer(h, rows);
    Eq(22 * 120, total, "every hero's rows are taken");
    Eq(HeroGrades.MaxQueue, big.Count, "the queue is bounded");
    var batch = big.Take(64);
    Eq(64, batch.Count, "a request takes 64 rows");
    big.Return(batch);
    Eq(HeroGrades.MaxQueue, big.Count, "rows put back keep the bound");

    var b22 = q.Take(22);
    string body = HeroGrades.BuildBody(b22, new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), "1.6.46.841.1");
    Check(body != null && body.StartsWith("{\"v\":1,\"at\":\"2026-10-27T12:00:00Z\",\"gameVersion\":\"1.6.46.841.1\",\"rows\":[{\"heroId\":2282,\"modeId\":15,\"score\":5.0,\"voters\":3061,\"sum\":15305},"), "body: version, time and the first row");
    Check(body.Contains("{\"heroId\":2282,\"modeId\":22,\"score\":4.5,\"voters\":85,\"sum\":380}]}"), "body: the last row");
    var parsed = MiniJson.AsObject(MiniJson.TryParse(body));
    Check(parsed != null && ((List<object>)parsed["rows"]).Count == 22, "body is valid JSON with 22 rows");
    var odd = new List<KeyValuePair<int, HeroGradeRow>> { new KeyValuePair<int, HeroGradeRow>(2282, new HeroGradeRow { Mode = 4, Score = 5, Voters = 10, Sum = 51 }), new KeyValuePair<int, HeroGradeRow>(2282, new HeroGradeRow { Mode = 5, Score = 5, Voters = 10 }) };
    string ob = HeroGrades.BuildBody(odd, DateTime.UtcNow, null);
    Check(!ob.Contains("sum"), "a sum over voters*5 or an unknown sum is left out of the row");
    Check(HeroGrades.BuildBody(new List<KeyValuePair<int, HeroGradeRow>>(), DateTime.UtcNow, null) == null, "no rows: no body");
  }

  static void CodeReportTests() {
    Eq(CodeResult.Ok, CodeResult.Classify("Successful redemption"), "promo result: success");
    Eq(CodeResult.Ok, CodeResult.Classify("\u041f\u0440\u043e\u043c\u043e\u043a\u043e\u0434 \u0443\u0441\u043f\u0435\u0448\u043d\u043e \u0430\u043a\u0442\u0438\u0432\u0438\u0440\u043e\u0432\u0430\u043d"), "promo result: success in Russian");
    Eq(CodeResult.Invalid, CodeResult.Classify("Invalid code"), "promo result: invalid");
    Eq(CodeResult.Invalid, CodeResult.Classify("\u041d\u0435\u0432\u0435\u0440\u043d\u044b\u0439 \u043a\u043e\u0434"), "promo result: invalid in Russian");
    Eq(CodeResult.Expired, CodeResult.Classify("This code has expired"), "promo result: expired");
    Eq(CodeResult.Expired, CodeResult.Classify("\u0421\u0440\u043e\u043a \u0434\u0435\u0439\u0441\u0442\u0432\u0438\u044f \u043a\u043e\u0434\u0430 \u0438\u0441\u0442\u0451\u043a"), "promo result: expired in Russian");
    Eq(CodeResult.Used, CodeResult.Classify("Code already redeemed"), "promo result: already used wins over success");
    Eq(CodeResult.Used, CodeResult.Classify("\u041a\u043e\u0434 \u0443\u0436\u0435 \u0438\u0441\u043f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u043d"), "promo result: already used in Russian");
    Eq(CodeResult.Limit, CodeResult.Classify("Redemption limit reached"), "promo result: limit");
    Eq(CodeResult.Unknown, CodeResult.Classify("Network busy"), "promo result: unknown stays unknown");
    // the game's own texts for the code errors 1601-1604, 1606, 1610, 1611 (the popup's m_strContent), English and Russian
    var real = new[] {
      new[] { CodeResult.Invalid, "The Redeem Code does not exist.", "\u0422\u0430\u043a\u043e\u0433\u043e \u043a\u043e\u0434\u0430 \u0432\u044b\u043a\u0443\u043f\u0430 \u043d\u0435 \u0441\u0443\u0449\u0435\u0441\u0442\u0432\u0443\u0435\u0442." },
      new[] { CodeResult.Expired, "The Redeem Code has expired.", "\u0421\u0440\u043e\u043a \u0434\u0435\u0439\u0441\u0442\u0432\u0438\u044f \u043a\u043e\u0434\u0430 \u0432\u044b\u043a\u0443\u043f\u0430 \u0438\u0441\u0442\u0435\u043a." },
      new[] { CodeResult.Invalid, "The Redeem Code is invalid.", "\u041d\u0435\u0432\u0435\u0440\u043d\u044b\u0439 \u043a\u043e\u0434 \u0432\u044b\u043a\u0443\u043f\u0430." },
      new[] { CodeResult.Used, "This Redeem Code has already been used.", "\u042d\u0442\u043e\u0442 \u043a\u043e\u0434 \u0432\u044b\u043a\u0443\u043f\u0430 \u0443\u0436\u0435 \u0438\u0441\u043f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u043d." },
      new[] { CodeResult.Limit, "Redemption limit reached.", "\u0414\u043e\u0441\u0442\u0438\u0433\u043d\u0443\u0442 \u043b\u0438\u043c\u0438\u0442 \u0432\u044b\u043a\u0443\u043f\u0430." },
      new[] { CodeResult.NotStarted, "Redemption hasn't started yet.", "\u0412\u044b\u043a\u0443\u043f \u0435\u0449\u0435 \u043d\u0435 \u043d\u0430\u0447\u0430\u043b\u0441\u044f." },
      new[] { CodeResult.Limit, "The redemption quota has been reached.", "\u0414\u043e\u0441\u0442\u0438\u0433\u043d\u0443\u0442\u0430 \u043a\u0432\u043e\u0442\u0430 \u043d\u0430 \u0432\u044b\u043a\u0443\u043f." } };
    foreach (var rt in real) {
      Eq(rt[0], CodeResult.Classify(rt[1]), "promo result: game text \"" + rt[1] + "\" is " + rt[0]);
      Eq(rt[0], CodeResult.Classify(rt[2]), "promo result: game text in Russian is " + rt[0]);
    }
    // more game languages: the code results of the redeem toast, one text per language and class
    var langs = new[] {
      new[] { CodeResult.Invalid, "Il codice di riscatto non \u00e8 valido.", "it" },
      new[] { CodeResult.Expired, "Il codice di riscatto \u00e8 scaduto.", "it" },
      new[] { CodeResult.Used, "Il codice \u00e8 gi\u00e0 stato riscattato.", "it" },
      new[] { CodeResult.NotStarted, "Il codice non \u00e8 ancora valido.", "it" },
      new[] { CodeResult.Invalid, "Questo codice non esiste.", "it" },
      new[] { CodeResult.Ok, "Codice riscattato con successo!", "it" },
      new[] { CodeResult.Limit, "Hai superato il limite di riscatti.", "it" },
      new[] { CodeResult.Expired, "Le code a expir\u00e9.", "fr" },
      new[] { CodeResult.Used, "Ce code a d\u00e9j\u00e0 \u00e9t\u00e9 utilis\u00e9.", "fr" },
      new[] { CodeResult.Invalid, "Ce code n'est pas valide.", "fr" },
      new[] { CodeResult.NotStarted, "Pas encore disponible.", "fr" },
      new[] { CodeResult.Ok, "F\u00e9licitations, r\u00e9compense re\u00e7ue !", "fr" },
      new[] { CodeResult.Expired, "El c\u00f3digo ha vencido.", "es" },
      new[] { CodeResult.Used, "El c\u00f3digo ya ha sido canjeado.", "es" },
      new[] { CodeResult.Invalid, "El c\u00f3digo no es v\u00e1lido.", "es" },
      new[] { CodeResult.NotStarted, "El canje a\u00fan no ha comenzado.", "es" },
      new[] { CodeResult.Limit, "Se alcanz\u00f3 el l\u00edmite de canjes.", "es" },
      new[] { CodeResult.Ok, "\u00a1Felicidades! Recompensa obtenida.", "es" },
      new[] { CodeResult.Expired, "O c\u00f3digo expirou.", "pt" },
      new[] { CodeResult.Used, "O c\u00f3digo j\u00e1 foi resgatado.", "pt" },
      new[] { CodeResult.Invalid, "O c\u00f3digo \u00e9 inv\u00e1lido.", "pt" },
      new[] { CodeResult.NotStarted, "O resgate ainda n\u00e3o come\u00e7ou.", "pt" },
      new[] { CodeResult.Ok, "Parab\u00e9ns! Recompensa recebida.", "pt" },
      new[] { CodeResult.Expired, "Der Code ist abgelaufen.", "de" },
      new[] { CodeResult.Used, "Der Code wurde bereits eingel\u00f6st.", "de" },
      new[] { CodeResult.Invalid, "Der Code ist ung\u00fcltig.", "de" },
      new[] { CodeResult.NotStarted, "Der Code ist noch nicht aktiv.", "de" },
      new[] { CodeResult.Ok, "Code erfolgreich eingel\u00f6st.", "de" },
      new[] { CodeResult.Expired, "Kod wygas\u0142.", "pl" },
      new[] { CodeResult.Used, "Kod zosta\u0142 ju\u017c wykorzystany.", "pl" },
      new[] { CodeResult.Invalid, "Kod jest nieprawid\u0142owy.", "pl" },
      new[] { CodeResult.NotStarted, "Kod jeszcze nie jest aktywny.", "pl" },
      new[] { CodeResult.Ok, "Kod zrealizowano pomy\u015blnie.", "pl" },
      new[] { CodeResult.Expired, "\u30b3\u30fc\u30c9\u306e\u6709\u52b9\u671f\u9650\u304c\u5207\u308c\u3066\u3044\u307e\u3059\u3002", "ja" },
      new[] { CodeResult.Used, "\u3053\u306e\u30b3\u30fc\u30c9\u306f\u4f7f\u7528\u6e08\u307f\u3067\u3059\u3002", "ja" },
      new[] { CodeResult.Invalid, "\u30b3\u30fc\u30c9\u304c\u7121\u52b9\u3067\u3059\u3002", "ja" },
      new[] { CodeResult.NotStarted, "\u307e\u3060\u958b\u59cb\u3055\u308c\u3066\u3044\u307e\u305b\u3093\u3002", "ja" },
      new[] { CodeResult.Limit, "\u4ea4\u63db\u56de\u6570\u306e\u4e0a\u9650\u306b\u9054\u3057\u307e\u3057\u305f\u3002", "ja" },
      new[] { CodeResult.Ok, "\u4ea4\u63db\u306b\u6210\u529f\u3057\u307e\u3057\u305f\u3002", "ja" },
      new[] { CodeResult.Expired, "\ucf54\ub4dc\uac00 \ub9cc\ub8cc\ub418\uc5c8\uc2b5\ub2c8\ub2e4.", "ko" },
      new[] { CodeResult.Used, "\uc774\ubbf8 \uc0ac\uc6a9\ub41c \ucf54\ub4dc\uc785\ub2c8\ub2e4.", "ko" },
      new[] { CodeResult.Invalid, "\uc720\ud6a8\ud558\uc9c0 \uc54a\uc740 \ucf54\ub4dc\uc785\ub2c8\ub2e4.", "ko" },
      new[] { CodeResult.NotStarted, "\uc544\uc9c1 \uc2dc\uc791\ub418\uc9c0 \uc54a\uc558\uc2b5\ub2c8\ub2e4.", "ko" },
      new[] { CodeResult.Limit, "\uad50\ud658 \ud55c\ub3c4\ub97c \ucd08\uacfc\ud588\uc2b5\ub2c8\ub2e4.", "ko" },
      new[] { CodeResult.Ok, "\uad50\ud658\uc5d0 \uc131\uacf5\ud588\uc2b5\ub2c8\ub2e4.", "ko" },
      new[] { CodeResult.Expired, "\u5151\u6362\u7801\u5df2\u8fc7\u671f\u3002", "zh-Hans" },
      new[] { CodeResult.Used, "\u5151\u6362\u7801\u5df2\u4f7f\u7528\u3002", "zh-Hans" },
      new[] { CodeResult.Invalid, "\u5151\u6362\u7801\u65e0\u6548\u3002", "zh-Hans" },
      new[] { CodeResult.Invalid, "\u5151\u6362\u7801\u4e0d\u5b58\u5728\u3002", "zh-Hans" },
      new[] { CodeResult.NotStarted, "\u5151\u6362\u5c1a\u672a\u5f00\u59cb\u3002", "zh-Hans" },
      new[] { CodeResult.Limit, "\u5df2\u8fbe\u5230\u5151\u6362\u4e0a\u9650\u3002", "zh-Hans" },
      new[] { CodeResult.Ok, "\u5151\u6362\u6210\u529f\u3002", "zh-Hans" },
      new[] { CodeResult.Expired, "\u514c\u63db\u78bc\u5df2\u904e\u671f\u3002", "zh-Hant" },
      new[] { CodeResult.Used, "\u514c\u63db\u78bc\u5df2\u514c\u63db\u3002", "zh-Hant" },
      new[] { CodeResult.Invalid, "\u514c\u63db\u78bc\u7121\u6548\u3002", "zh-Hant" },
      new[] { CodeResult.Invalid, "\u514c\u63db\u78bc\u4e0d\u5b58\u5728\u3002", "zh-Hant" },
      new[] { CodeResult.NotStarted, "\u514c\u63db\u5c1a\u672a\u958b\u59cb\u3002", "zh-Hant" },
      new[] { CodeResult.Limit, "\u5df2\u9054\u5230\u514c\u63db\u4e0a\u9650\u3002", "zh-Hant" },
      new[] { CodeResult.Ok, "\u514c\u63db\u6210\u529f\u3002", "zh-Hant" },
      new[] { CodeResult.Expired, "\u0e23\u0e2b\u0e31\u0e2a\u0e2b\u0e21\u0e14\u0e2d\u0e32\u0e22\u0e38\u0e41\u0e25\u0e49\u0e27", "th" },
      new[] { CodeResult.Used, "\u0e23\u0e2b\u0e31\u0e2a\u0e19\u0e35\u0e49\u0e43\u0e0a\u0e49\u0e41\u0e25\u0e49\u0e27", "th" },
      new[] { CodeResult.Invalid, "\u0e23\u0e2b\u0e31\u0e2a\u0e44\u0e21\u0e48\u0e16\u0e39\u0e01\u0e15\u0e49\u0e2d\u0e07", "th" },
      new[] { CodeResult.NotStarted, "\u0e22\u0e31\u0e07\u0e44\u0e21\u0e48\u0e40\u0e23\u0e34\u0e48\u0e21\u0e41\u0e25\u0e01\u0e23\u0e2b\u0e31\u0e2a", "th" },
      new[] { CodeResult.Ok, "\u0e41\u0e25\u0e01\u0e2a\u0e33\u0e40\u0e23\u0e47\u0e08", "th" },
      new[] { CodeResult.Expired, "Kodun s\u00fcresi doldu.", "tr" },
      new[] { CodeResult.Used, "Kod zaten kullan\u0131ld\u0131.", "tr" },
      new[] { CodeResult.Invalid, "Kod ge\u00e7ersiz.", "tr" },
      new[] { CodeResult.NotStarted, "Kod hen\u00fcz aktif de\u011fil.", "tr" },
      new[] { CodeResult.Ok, "Tebrikler, \u00f6d\u00fcl al\u0131nd\u0131.", "tr" },
      new[] { CodeResult.Expired, "Kode sudah kedaluwarsa.", "id" },
      new[] { CodeResult.Used, "Kode sudah digunakan.", "id" },
      new[] { CodeResult.Invalid, "Kode tidak valid.", "id" },
      new[] { CodeResult.NotStarted, "Penukaran belum dimulai.", "id" },
      new[] { CodeResult.Ok, "Berhasil ditukar.", "id" },
      new[] { CodeResult.Expired, "M\u00e3 \u0111\u00e3 h\u1ebft h\u1ea1n.", "vi" },
      new[] { CodeResult.Used, "M\u00e3 \u0111\u00e3 \u0111\u01b0\u1ee3c s\u1eed d\u1ee5ng.", "vi" },
      new[] { CodeResult.Invalid, "M\u00e3 kh\u00f4ng h\u1ee3p l\u1ec7.", "vi" },
      new[] { CodeResult.NotStarted, "Ch\u01b0a b\u1eaft \u0111\u1ea7u \u0111\u1ed5i m\u00e3.", "vi" },
      new[] { CodeResult.Ok, "Ch\u00fac m\u1eebng, \u0111\u1ed5i th\u00e0nh c\u00f4ng.", "vi" } };
    foreach (var lt in langs) Eq(lt[0], CodeResult.Classify(lt[1]), "promo result: " + lt[2] + " text \"" + lt[1] + "\" is " + lt[0]);
    Eq(CodeResult.Unknown, CodeResult.Classify(null), "promo result: no message is unknown");
    Eq("ru", CodeResult.LanguageOf("\u041d\u0435\u0432\u0435\u0440\u043d\u044b\u0439 \u043a\u043e\u0434"), "promo language: Russian");
    Eq("en", CodeResult.LanguageOf("Invalid code"), "promo language: English");
    foreach (var lt in langs) if ("it es pt de fr pl tr id vi".Contains(lt[2])) Eq(lt[2], CodeResult.LanguageOf(lt[1]), "promo language: " + lt[2] + " text \"" + lt[1] + "\"");
    Eq("en", CodeResult.LanguageOf("Code already redeemed"), "promo language: English with the word code");
    Eq("it", CodeResult.LanguageOf("Il codice non \u00e8 valido"), "promo language: Italian toast is it, not en");
    Check(CodeResult.LanguageOf("\u65e5\u672c\u8a9e") == null, "promo language: non-Latin, non-Cyrillic stays null");
    // a game hint that is not a redeem answer is not reported (prod: result=unknown for the clipboard hint)
    var hints = new[] { "\u0421\u043e\u0434\u0435\u0440\u0436\u0438\u043c\u043e\u0435 \u0441\u043a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u043d\u043e \u0432 \u0431\u0443\u0444\u0435\u0440 \u043e\u0431\u043c\u0435\u043d\u0430...", "Copied to clipboard", "Contenuto copiato negli appunti", "Contenido copiado", "Conte\u00fado copiado", "In die Zwischenablage kopiert", "Copi\u00e9 dans le presse-papiers", "Skopiowano do schowka", "Panoya kopyaland\u0131", "Disalin ke papan klip", "\u0110\u00e3 sao ch\u00e9p v\u00e0o b\u1ed9 nh\u1edb t\u1ea1m", "\u30af\u30ea\u30c3\u30d7\u30dc\u30fc\u30c9\u306b\u30b3\u30d4\u30fc\u3057\u307e\u3057\u305f", "\ud074\ub9bd\ubcf4\ub4dc\uc5d0 \ubcf5\uc0ac\ub428", "\u5df2\u590d\u5236\u5230\u526a\u8d34\u677f", "\u5df2\u8907\u88fd", "\u0e04\u0e31\u0e14\u0e25\u0e2d\u0e01\u0e41\u0e25\u0e49\u0e27" };
    foreach (var h in hints) Check(CodeResult.IsNotAnswer(h), "promo hint dropped: " + h);
    Check(!CodeResult.IsNotAnswer("The Redeem Code has expired."), "promo hint: a redeem answer is kept");
    Check(!CodeResult.IsNotAnswer("Code copied but already redeemed"), "promo hint: a matching redeem rule wins");
    Check(!CodeResult.IsNotAnswer("Network busy") && !CodeResult.IsNotAnswer(null), "promo hint: other unknown texts are kept");
    var g = new CodeReportGate(); var t0 = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    Check(g.ShouldSend("Gift2026", CodeResult.Ok, t0), "promo debounce: the first report goes");
    Check(!g.ShouldSend("gift2026", CodeResult.Ok, t0.AddMinutes(9)), "promo debounce: same code and result within 10 minutes is dropped (case ignored)");
    Check(g.ShouldSend("Gift2026", CodeResult.Invalid, t0.AddMinutes(1)), "promo debounce: another result goes");
    Check(g.ShouldSend("Gift2026", CodeResult.Ok, t0.AddMinutes(10)), "promo debounce: after 10 minutes it goes again");
    Eq("{\"code\":\"Gift2026\",\"result\":\"invalid\",\"toast\":\"Invalid code\",\"region\":\"en\",\"gameVersion\":\"1.6.46.841.1\"}", CodeReportClient.BuildBody(" Gift2026 ", CodeResult.Invalid, "Invalid code", "1.6.46.841.1"), "promo body: only code, result, text, language, game version");
    Eq("Bearer rf_abc", CodeReportClient.AuthHeader("rf_abc"), "promo request: the sync code goes as Bearer");
    Check(CodeReportClient.BuildBody("bad code!", CodeResult.Ok, "x", null) == null, "promo body: not a code, nothing to send");
    string longBody = CodeReportClient.BuildBody("ABCD", CodeResult.Unknown, new string('x', 500), null);
    Check(longBody.Length < 300 && !longBody.Contains("player") && !longBody.Contains("uid"), "promo body: text cut at 160, nothing about the player");
    var raw = new List<KeyValuePair<long, long>> {
      new KeyValuePair<long, long>(900, 5), new KeyValuePair<long, long>(12, 1), new KeyValuePair<long, long>(900, 2),
      new KeyValuePair<long, long>(0, 4), new KeyValuePair<long, long>(7, 0), new KeyValuePair<long, long>(-3, 1), new KeyValuePair<long, long>(5000000000L, 1) };
    var rw = CodeRewards.Normalize(raw);
    Eq("[{\"id\":12,\"n\":1},{\"id\":900,\"n\":7}]", CodeRewards.Json(rw), "promo rewards: one entry per id, summed, sorted, bad entries dropped");
    Check(CodeRewards.Normalize(null) == null && CodeRewards.Normalize(new List<KeyValuePair<long, long>>()) == null && CodeRewards.Normalize(new[] { new KeyValuePair<long, long>(0, 1) }) == null, "promo rewards: nothing usable is none");
    var many = new List<KeyValuePair<long, long>>(); for (int i = 1; i <= 21; i++) many.Add(new KeyValuePair<long, long>(i, 1));
    Check(CodeRewards.Normalize(many) == null, "promo rewards: more than 20 ids is none (a cut list would mislead)");
    many.RemoveAt(20);
    Eq(20, CodeRewards.Normalize(many).Count, "promo rewards: 20 ids are kept");
    Check(CodeRewards.Same(rw, CodeRewards.Normalize(raw)) && !CodeRewards.Same(rw, CodeRewards.Normalize(many)) && !CodeRewards.Same(rw, null), "promo rewards: set comparison");
    Eq("{\"code\":\"Gift2026\",\"result\":\"ok\",\"toast\":\"Success\",\"region\":\"en\",\"gameVersion\":\"1.6.46.841.1\",\"rewards\":[{\"id\":12,\"n\":1},{\"id\":900,\"n\":7}]}", CodeReportClient.BuildBody("Gift2026", CodeResult.Ok, "Success", "1.6.46.841.1", rw), "promo body: the rewards of an accepted code");
    Check(!CodeReportClient.BuildBody("Gift2026", CodeResult.Used, "Used", null, rw).Contains("rewards") && !CodeReportClient.BuildBody("Gift2026", CodeResult.Ok, "Success", null, null).Contains("rewards"), "promo body: no rewards unless accepted and read");
    Check(AppConfig.FromJson("{}").SendCodes, "promo setting: on for a config without the key");
    Check(AppConfig.FromJson("{\"autoSync\":true}").SendCodes && new AppConfig().SendCodes, "promo setting: on by default");
    Check(!AppConfig.FromJson(new AppConfig { SendCodes = false }.ToJson()).SendCodes, "promo setting: off round-trips");
  }

  static void CodeTextTests() {
    Check(CodeText.Looks("ABCD") && CodeText.Looks("wardsage2026") && CodeText.Looks("a1B2c3D4e5F6g7H8i9J0k1L2"), "promo code: letters and digits, 4 to 24, look like a code");
    Check(CodeText.Looks("  Gift2026 "), "promo code: spaces around it are ignored");
    Check(!CodeText.Looks("ABC") && !CodeText.Looks("a1B2c3D4e5F6g7H8i9J0k1L2M"), "promo code: too short / too long");
    Check(!CodeText.Looks("AB CD") && !CodeText.Looks("AB-CD") && !CodeText.Looks("code!") && !CodeText.Looks("\u043a\u043e\u0434\u043a\u043e\u0434"), "promo code: no spaces inside, no symbols, no other alphabets");
    Check(!CodeText.Looks("") && !CodeText.Looks(null) && !CodeText.Looks("   "), "promo code: empty is not one");
    Eq("a b c", CodeText.ForLog("a\nb\"c"), "promo code: a game message in one line, no quotes");
    Eq(160, CodeText.ForLog(new string('x', 500)).Length, "promo code: a game message is cut at 160");
  }

  static void GamePacksTests() {
    Console.WriteLine("Game packs");
    string hot = "http://akmcdn.ztdheros.com/pc_res_hotfix5/829.1/zg_hotfix_2372_ziyi_20260914_1_hotfix.tgrp";
    string lang = "http://akmcdn.ztdheros.com/pc_res_version5/out/829.1/zg_languages_20260911142512.tgrp";
    string add = "http://alxcdn.ztdheros.com/pc_res_version5/out/829.1/zg_addres20260910132436464113_829.1.tgrp";
    string log =
      "I20261011 10:20:34.918098 24128 windows_platform.cpp:147] init TGRP. url = " + hot + ", time = 0, index = 0\n" +
      "I20261011 10:20:35.000000 24128 windows_platform.cpp:147] init TGRP. url = " + lang + ", time = 0, index = 1\r\n" +
      "I20261011 10:20:35.100000 24128 x.cpp:1] init TGRP. url = http://evil.example.com/pc_res_hotfix5/829.1/zg_a.tgrp, time = 0\n" +
      "I20261011 10:20:35.200000 24128 x.cpp:1] url = http://akmcdn.ztdheros.com/other/829.1/zg_a.tgrp\n" +
      "I20261011 10:20:35.300000 24128 x.cpp:1] init TGRP. url = " + hot + ", time = 0, index = 0\n" +
      "I20261011 10:20:35.400000 24128 x.cpp:1] url = " + add + "\n";
    var urls = GamePacks.ParseLog(log);
    Eq(3, urls.Count, "game packs: three valid, distinct addresses");
    Check(urls.Count == 3 && urls[0] == hot && urls[1] == lang && urls[2] == add, "game packs: first-seen order");
    Eq(0, GamePacks.ParseLog("").Count, "game packs: empty log");
    Eq(0, GamePacks.ParseLog(null).Count, "game packs: no log");
    Eq("{\"v\":1,\"urls\":[\"" + hot + "\",\"" + lang + "\"]}", GamePacks.Json(new List<string> { hot, lang }), "game packs: json");
    Eq("{\"v\":1,\"urls\":[]}", GamePacks.Json(new List<string>()), "game packs: empty json");
    Check(GamePacks.ReadLogs() != null, "game packs: reading the logs never throws");
  }

  static void GuildTests() {
    Console.WriteLine("Guild table");
    var fights = new List<object>();
    for (int i = 1; i <= 8; i++) fights.Add(Lt("iDamageNum", i * 1000L, "vFightHero", LArr(
      Lt("iHeroId", 2290L, "iLevel", 80L, "iStarLevel", 5L, "iDamageNum", i * 600L), Lt("iHeroId", 2301L, "iLevel", 79L, "iStarLevel", 4L, "iDamageNum", i * 400L), Lt("iHeroId", 0L))));
    var m1 = Lt("stPlayerIdType", Lt("iZoneId", 4L, "iUid", 111L), "iPostId", 1L, "iJoinTime", 1700000000L, "iSevenActive", 350L,
                "vSevenActive", LArr(Lt("iTime", 1789990000L, "iActive", 10L), Lt("iTime", 1789900000L, "iActive", 20L), Lt("iTime", 1700000000L, "iActive", 999L)),
                "stRoleSimpleInf", Lt("sName", "Лидер \"A\"", "iLevel", 80L, "iPower", 1234567L, "iLogoutTime", 0L),
                "mWeekBossData", Lt("[2]", Lt("iDamageNum", "9007199254740993", "iPrevDamageNum", 5.5e9, "iFightNum", 3L, "iLastFightTime", 1790000000L, "vvFightData", LArr(fights.ToArray()),
                                         "mBoss3FightData", Lt("[5002006]", Lt("iDamageNum", 77L, "vFightHero", LArr(Lt("iHeroId", 2290L, "iLevel", 80L, "iStarLevel", 5L, "iDamageNum", 77L))))),
                                    "[1]", Lt("iDamageNum", 100L, "iPrevDamageNum", 0L, "iFightNum", 0L, "iLastFightTime", 0L)),
                "mBossData", Lt("[1]", Lt("iDamageNum", 77L, "iPrevDamageNum", 1L, "iUseItemNum", 2L, "iLastFightTime", 1790000100L,
                                    "vFightData", LArr(Lt("iHeroId", 3001L, "iLevel", 70L, "iStarLevel", 3L, "iDamageNum", 30L), Lt("iHeroId", 3002L, "iLevel", 71L, "iStarLevel", 2L, "iDamageNum", 47L)))));
    var m2 = Lt("stPlayerIdType", Lt("iZoneId", 4L, "iUid", 222L), "iPostId", 4L, "stRoleSimpleInf", Lt("sName", "Bob", "iLogoutTime", 1789990000L));
    var union = Lt("iUnionId", 55L, "iLevel", 7L, "stBaseAttr", Lt("sUnionName", "Стража"));
    long now = 1790000000L * 1000;
    var week = LArr(Lt("iRefreshTime", 1790001800L, "iCurBossHp", "123", "iPrevBossHp", 500L, "bKill", false), Lt("iRefreshTime", 0L, "bKill", true));
    var classic = LArr(Lt("iRefreshTime", 1790900000L));
    bool soon; int cnt;
    string json = GuildPayload.Build(union, new List<Dictionary<string, object>> { m1, m2 }, week, classic, 111L, now, out soon, out cnt);
    Check(json != null && cnt == 2, "guild payload built");
    var o = MiniJson.AsObject(MiniJson.TryParse(json));
    Check(o != null && ArenaOpp.Num(o["zone"]) == 4 && ArenaOpp.Num(o["selfUid"]) == 111 && ArenaOpp.Num(o["at"]) == now, "zone/self/at");
    Check(json.Contains("\"union\":{\"id\":55,\"name\":\"Стража\",\"level\":7}"), "union: " + json);
    Check(json.Contains("\"week\":[{\"id\":1,\"refresh\":1790001800,\"hp\":123,\"prevHp\":500,\"kill\":false},{\"id\":2,\"refresh\":null,\"kill\":true}]"), "week bosses: " + json);
    Check(json.Contains("\"classic\":[{\"id\":1,\"refresh\":1790900000}]"), "classic bosses");
    Check(soon, "a refresh within the hour is noticed");
    Check(json.Contains("\"2\":{\"fights\":3,\"dmg\":9007199254740993,\"prevDmg\":5500000000,\"last\":1790000000,"), "string and float damage: " + json);
    Check(json.Contains("\"classic\":{\"1\":{\"fights\":2,\"dmg\":77,\"prevDmg\":1,\"last\":1790000100,"), "classic uses iUseItemNum");
    Check(json.Contains("\"activeWeek\":30,"), "activeWeek sums only the last 7 days: " + json);
    Check(json.Contains("\"fightsDetail\":[{\"dmg\":4000,\"heroes\":[{\"id\":2290,\"lv\":80,\"star\":5,\"dmg\":2400},{\"id\":2301,\"lv\":79,\"star\":4,\"dmg\":1600}]}"), "week fights, last 6 of 9: " + json);
    Check(!json.Contains("{\"dmg\":3000,\"heroes\"") && json.Contains("{\"dmg\":8000,\"heroes\"") && json.Contains("{\"stage\":5002006,\"dmg\":77,\"heroes\":[{\"id\":2290"), "oldest dropped, stage fight kept");
    Check(json.Contains("\"fightsDetail\":[{\"dmg\":77,\"heroes\":[{\"id\":3001,\"lv\":70,\"star\":3,\"dmg\":30},{\"id\":3002,\"lv\":71,\"star\":2,\"dmg\":47}]}]"), "classic flat hero list: " + json);
    Check(json.Contains("\"name\":\"Bob\"") && !json.Substring(json.IndexOf("\"Bob\"")).Contains("activeWeek") && !json.Substring(json.IndexOf("\"Bob\"")).Contains("fightsDetail"), "no lists, no extra fields");
    Check(json.Contains("\"logout\":0,") && json.Contains("\"logout\":1789990000,"), "online = 0");
    Check(json.Contains("\"level\":null,\"power\":null,\"post\":4,\"join\":null"), "missing numbers are null: " + json);
    // activity, Two-Heads boss, the weekly activity ranking
    Check(!json.Contains("twoHeads") && !json.Contains("activeRank") && !json.Contains("weekActive"), "no extras, no extra fields");
    Check(json.Contains("\"activeDays\":[{\"t\":1789990000,\"a\":10},{\"t\":1789900000,\"a\":20},{\"t\":1700000000,\"a\":999}]"), "activeDays lists all entries: " + json);
    var m3 = Lt("stPlayerIdType", Lt("iZoneId", 4L, "iUid", 333L), "iPostId", 4L, "iTotalHistoryActive", 4321L,
                "vSevenActive", LArr(Lt("iTime", 1789900000L, "iActive", 20L), Lt("iTime", 1789990000L, "iActive", 10L)),
                "stRoleSimpleInf", Lt("sName", "Cy"),
                "mTwoHeadsBossData", Lt("[101]", Lt("ulDamageNum", "7000000000", "ulPrevDamageNum", 5L, "uiFightNum", 2L,
                    "vFightData", LArr(Lt("ulDamageNum", 900L, "vFightData", LArr(Lt("iHeroId", 2290L, "iLevel", 80L, "iStarLevel", 5L, "iDamageNum", 500L), Lt("iHeroId", 2301L, "iLevel", 79L, "iStarLevel", 4L, "iDamageNum", 400L))),
                                       Lt("ulDamageNum", 0L, "vFightData", LArr())))));
    var union2 = Lt("iUnionId", 55L, "iLevel", 7L, "stBaseAttr", Lt("sUnionName", "S"), "iWeekActive", 900L, "iSevenTotalActive", 5000L, "iCurDayTotalActive", 120L);
    var extra = new GuildExtra {
      TwoHp = Lt("[101]", 8000000000L, "[102]", "5"), TwoRefresh = Lt("[101]", 1790001800L),
      RankThis = new List<Dictionary<string, object>> { Lt("stRole", Lt("iUid", 333L, "iZoneId", 4L), "iActive", 77L), Lt("iActive", 5L) },
      RankLast = new List<Dictionary<string, object>>()
    };
    string j3 = GuildPayload.Build(union2, new List<Dictionary<string, object>> { m3 }, week, classic, 333L, now, out soon, out cnt, extra);
    Check(j3 != null && MiniJson.TryParse(j3) != null, "extras: valid JSON " + j3);
    Check(j3.Contains("\"level\":7,\"weekActive\":900,\"sevenActive\":5000,\"dayActive\":120}"), "union activity: " + j3);
    Check(j3.Contains("\"activeDays\":[{\"t\":1789900000,\"a\":20},{\"t\":1789990000,\"a\":10}],\"activeTotal\":4321,"), "member activity: " + j3);
    Check(j3.Contains("\"twoHeads\":{\"101\":{\"fights\":2,\"dmg\":7000000000,\"prevDmg\":5,\"fightsDetail\":[{\"dmg\":900,\"heroes\":[{\"id\":2290,\"lv\":80,\"star\":5,\"dmg\":500},{\"id\":2301,\"lv\":79,\"star\":4,\"dmg\":400}]}]}}"), "member twoHeads: " + j3);
    Check(j3.Contains("\"twoHeads\":[{\"id\":101,\"refresh\":1790001800,\"hp\":8000000000},{\"id\":102,\"refresh\":null,\"hp\":5}]"), "twoHeads bosses: " + j3);
    Check(!j3.Contains("bannerId") && !j3.Contains("bannerBgId"), "no banner when absent");
    var union3 = Lt("iUnionId", 55L, "iLevel", 7L, "stBaseAttr", Lt("sUnionName", "S", "iBannerId", 12L, "iBannerBgId", 0L));
    string jb = GuildPayload.Build(union3, new List<Dictionary<string, object>> { m3 }, week, classic, 333L, now, out soon, out cnt);
    Check(jb != null && jb.Contains("\"level\":7,\"bannerId\":12}") && !jb.Contains("bannerBgId"), "banner id only when > 0: " + jb);
    Check(soon, "a Two-Heads refresh within the hour is noticed");
    Check(j3.Contains("\"activeRank\":{\"thisWeek\":[{\"uid\":333,\"zone\":4,\"a\":77}]}"), "activeRank only for loaded lists: " + j3);
    var m4 = Lt("stPlayerIdType", Lt("iZoneId", 4L, "iUid", 444L), "stRoleSimpleInf", Lt("sName", "Di"), "mTwoHeadsBossData", Lt());
    string j4 = GuildPayload.Build(union, new List<Dictionary<string, object>> { m4 }, week, classic, 444L, now, out soon, out cnt, new GuildExtra());
    Check(j4 != null && !j4.Contains("twoHeads") && !j4.Contains("activeRank") && !j4.Contains("activeDays") && !j4.Contains("activeTotal"), "empty extras add nothing: " + j4);
    // the guild war block
    Check(!j4.Contains("\"gvg\"") && !j3.Contains("\"gvg\""), "no war data, no gvg block");
    var role1 = Lt("stRoleId", Lt("iZoneId", 4L, "iUid", 111L), "sName", new string('Z', 60), "iLevel", 80L, "iAttackedNum", 3L, "uiGVGPos", 1L,
                   "stAttackerStatistic", Lt("iVictoryNum", 2L, "iDefeatNum", 1L, "iTotalVictoryNum", 20L, "iTotalDefeatNum", 5L),
                   "stDefenderStatistic", Lt("iVictoryNum", 4L, "iDefeatNum", "6", "iTotalVictoryNum", 40L, "iTotalDefeatNum", 7L),
                   "iAttackedOpponentHp", 1234L, "iTotalContribution", 999L);
    var role2 = Lt("stRoleId", Lt("iZoneId", 4L, "iUid", 222L), "sName", "Bob");
    var role0 = Lt("stRoleId", Lt("iZoneId", 4L, "iUid", 0L), "sName", "Nobody");
    var gvgU = Lt("iIssueId", 12L, "iScore", 5000L, "iDivision", 3L, "iFightingRound", 2L, "iLastRank", 0L,
                  "vGarrisonRole", LArr(role1, role2, role0),
                  "vRoundInfo", LArr(Lt("iOpponentUnionId", 77L, "iWinState", 3L, "iDefeatNum", 0L, "iStartScore", 1000L), Lt("iOpponentUnionId", 78L, "iWinState", 1L, "iDefeatNum", 4L, "iStartScore", 1020L),
                                     Lt("iOpponentUnionId", 79L, "iWinState", 0L), Lt("iOpponentUnionId", 80L, "iWinState", 3L, "uiFakeGuildId", 9L, "iStartScore", 1014L)));
    var gvgG = Lt("iIssueId", 12L, "iPeriodType", 2L, "iNextChangeStateTime", 1790003600L, "iFightingRound", 2L);
    string gv = GvgPayload.Build(new GvgInfo { Union = gvgU, Global = gvgG });
    Check(gv != null && MiniJson.TryParse(gv) != null, "gvg: valid JSON " + gv);
    Check(gv.StartsWith("{\"v\":1,\"issue\":12,\"round\":2,\"phase\":2,\"nextChange\":1790003600,\"score\":5000,\"division\":3,\"rank\":null,\"maxKeys\":null,\"cmdExtra\":null,\"rounds\":["), "gvg header: " + gv);
    Check(gv.Contains("\"rounds\":[{\"round\":1,\"opp\":77,\"win\":1,\"defeatNum\":0,\"startScore\":1000},{\"round\":2,\"opp\":78,\"win\":0,\"defeatNum\":4,\"startScore\":1020},{\"round\":3,\"opp\":79,\"win\":null,\"defeatNum\":0,\"startScore\":null},{\"round\":4,\"opp\":null,\"win\":1,\"defeatNum\":0,\"startScore\":1014,\"bot\":true}]"), "gvg rounds: " + gv);
    Check(gv.Contains("{\"zone\":4,\"uid\":111,\"name\":\"" + new string('Z', 40) + "\",\"attacks\":3,\"cmd\":true,\"atkW\":2,\"atkL\":1,\"atkTotW\":20,\"atkTotL\":5,\"defW\":4,\"defL\":6,\"defTotW\":40,\"defTotL\":7,\"contrib\":999,\"oppHp\":1234}"), "gvg member, name cut to 40: " + gv);
    Check(gv.Contains("\"uid\":222,\"name\":\"Bob\",\"attacks\":0,\"cmd\":false,\"atkW\":0") && !gv.Contains("Nobody"), "gvg: missing numbers are 0, uid 0 skipped");
    string gv2 = GvgPayload.Build(new GvgInfo { Union = Lt("iScore", 1L, "iLastRank", 15L, "vGarrisonRole", LArr(role2)), MaxKeys = 15, CmdExtra = 3 });
    Check(gv2 != null && gv2.Contains("\"rank\":15,\"maxKeys\":15,\"cmdExtra\":3,\"rounds\":[],") && gv2.Contains("\"phase\":0,\"nextChange\":null"), "gvg: limits, rank, no global: " + gv2);
    string gvE = GvgPayload.Build(new GvgInfo { Union = Lt("iIssueId", 9L, "vGarrisonRole", LArr()) });
    Check(GvgPayload.Build(null) == null && gvE != null && gvE.Contains("\"issue\":9") && gvE.EndsWith("\"members\":[]}"), "gvg: no table = no block, no members = the season still goes: " + gvE);
    // the shape of a real client: no vGarrisonRole, the players sit in m_vGarrisons[..].vSub[..].Role (fake ids)
    Func<long, long, string, long, long, Dictionary<string, object>> post = (zone, uid, nm, att, contrib) => Lt("Side", 1L, "status", 2L, "Role",
      Lt("stRoleId", Lt("iZoneId", zone, "iUid", uid), "sName", nm, "iLevel", 90L, "iAttackedNum", att, "iTotalContribution", contrib, "uiGVGPos", 0L,
         "stAttackerStatistic", Lt("iVictoryNum", 1L, "iDefeatNum", 0L, "iTotalVictoryNum", 3L, "iTotalDefeatNum", 1L), "stDefenderStatistic", Lt("iTotalVictoryNum", 2L),
         "vTeams", "<table>", "vAttackedBuildHp", "<table>"));
    var emptyPost = Lt("Side", 1L, "status", 1L, "m_HP", 5L);
    var realU = Lt("iIssueId", 1016L, "iUnionId", 31L, "iScore", 1199L, "iDivision", 2003L, "iLastRank", 1071L, "iFightingRound", 16L, "iPeriodType", 3L,
      "vGarrisons", LArr(Lt("stRoleId", Lt("iZoneId", 7001L, "iUid", 9001L), "iId", 1001L, "iSubId", 2001L), Lt("stRoleId", Lt("iZoneId", 7002L, "iUid", 9002L), "iId", 1001L, "iSubId", 2002L),
                         Lt("stRoleId", Lt("iZoneId", 7003L, "iUid", 9003L), "iId", 1002L, "iSubId", 2003L), Lt("stRoleId", Lt("iZoneId", 0L, "iUid", 0L), "iId", 1002L, "iSubId", 2004L),
                         Lt("stRoleId", Lt("iZoneId", 4294967295L, "iUid", 5L), "iId", 1002L, "iSubId", 2005L)),
      "m_vGarrisons", Lt("[1001]", Lt("Side", 1L, "vSub", Lt("[2001]", post(7001L, 9001L, "Ann", 2L, 960L), "[2002]", post(7002L, 9002L, "Bo", 0L, 100L), "[2003]", emptyPost)),
                         "[1002]", Lt("Side", 1L, "vSub", Lt("[2001]", post(7003L, 9003L, "Cy", 1L, 0L), "[2002]", post(7001L, 9001L, "Ann again", 2L, 960L),
                                                              "[2003]", post(4294967295L, 5L, "Robot", 0L, 0L), "[2004]", post(7009L, 9009L, "Enemy", 0L, 0L)))),
      "vRoundInfo", LArr(Lt("iOpponentUnionId", 41L, "iWinState", 1L, "iDefeatNum", 3L, "uiFakeGuildId", 0L), Lt("iOpponentUnionId", 42L, "iWinState", 0L, "uiFakeGuildId", 0L)),
      "vGarrisonRole", Lt(), "vGarrisonRobot", Lt(), "vReport", Lt());
    var realG = Lt("iIssueId", 1016L, "iPeriodType", 3L, "iFightingRound", 16L, "iNextChangeStateTime", 1791792000L);
    string gr = GvgPayload.Build(new GvgInfo { Union = realU, Global = realG });
    var gro = MiniJson.AsObject(MiniJson.TryParse(gr));
    Check(gro != null && ((List<object>)gro["members"]).Count == 3 && ((List<object>)gro["rounds"]).Count == 2, "gvg real shape: 3 own players (robot, empty, other guild and duplicate skipped): " + gr);
    Check(gr.StartsWith("{\"v\":1,\"issue\":1016,\"round\":16,\"phase\":3,\"nextChange\":1791792000,\"score\":1199,\"division\":2003,\"rank\":1071,"), "gvg real header: " + gr);
    Check(gr.Contains("{\"zone\":7001,\"uid\":9001,\"name\":\"Ann\",\"attacks\":2,\"cmd\":false,\"atkW\":1,\"atkL\":0,\"atkTotW\":3,\"atkTotL\":1,\"defW\":0,\"defL\":0,\"defTotW\":2,\"defTotL\":0,\"contrib\":960,\"oppHp\":0}"), "gvg real member: " + gr);
    Check(!gr.Contains("Robot") && !gr.Contains("Enemy") && !gr.Contains("Ann again"), "gvg real: only the guild's own players");
    // without the post list the lowest side is taken
    string gr2 = GvgPayload.Build(new GvgInfo { Union = Lt("iIssueId", 1L, "m_vGarrisons", Lt("[1]", Lt("vSub", Lt("[1]", post(7001L, 9001L, "Ann", 1L, 1L), "[2]", Lt("Side", 2L, "Role", Lt("stRoleId", Lt("iZoneId", 7L, "iUid", 8L), "sName", "Other")))))) });
    Check(gr2.Contains("Ann") && !gr2.Contains("Other"), "gvg: no post list, the own side only: " + gr2);
    var bigRoles = new List<object>(); for (int i = 0; i < 250; i++) bigRoles.Add(Lt("stRoleId", Lt("iZoneId", 4L, "iUid", 5000L + i), "sName", "G" + i));
    var bigRounds = new List<object>(); for (int i = 0; i < 80; i++) bigRounds.Add(Lt("iOpponentUnionId", 100L + i, "iWinState", 1L));
    string gb = GvgPayload.Build(new GvgInfo { Union = Lt("vGarrisonRole", LArr(bigRoles.ToArray()), "vRoundInfo", LArr(bigRounds.ToArray())) });
    var gbo = MiniJson.AsObject(MiniJson.TryParse(gb));
    Check(gbo != null && ((List<object>)gbo["members"]).Count == 200 && ((List<object>)gbo["rounds"]).Count == 50, "gvg bounds: 200 members, 50 rounds");
    var gx = new GuildExtra { Gvg = new GvgInfo { Union = gvgU, Global = gvgG } };
    string jg = GuildPayload.Build(union, new List<Dictionary<string, object>> { m1, m2 }, week, classic, 111L, now, out soon, out cnt, gx);
    Check(jg != null && jg.EndsWith(",\"gvg\":" + gv + "}") && MiniJson.TryParse(jg) != null, "gvg block is last in the guild payload");
    gvgU["iScore"] = 5001L;
    string jg2 = GuildPayload.Build(union, new List<Dictionary<string, object>> { m1, m2 }, week, classic, 111L, now + 1000, out soon, out cnt, gx);
    Check(GuildPayload.Hash(jg) != GuildPayload.Hash(jg2), "the hash includes the gvg block");
    // the size cap: many members with many attacks stay under 200 KB (the fight details are cut)
    var many = new List<Dictionary<string, object>>();
    for (int i = 0; i < 100; i++) {
      var sq = new List<object>();
      for (int k = 0; k < 6; k++) sq.Add(Lt("ulDamageNum", 1000L, "vFightData", LArr(Lt("iHeroId", 2290L, "iLevel", 80L, "iStarLevel", 5L, "iDamageNum", 500L), Lt("iHeroId", 2301L, "iLevel", 79L, "iStarLevel", 4L, "iDamageNum", 400L), Lt("iHeroId", 2302L, "iLevel", 79L, "iStarLevel", 4L, "iDamageNum", 100L))));
      many.Add(Lt("stPlayerIdType", Lt("iZoneId", 4L, "iUid", 1000L + i), "stRoleSimpleInf", Lt("sName", "M" + i),
                  "mTwoHeadsBossData", Lt("[101]", Lt("ulDamageNum", 1L, "uiFightNum", 6L, "vFightData", LArr(sq.ToArray())), "[102]", Lt("ulDamageNum", 1L, "uiFightNum", 6L, "vFightData", LArr(sq.ToArray())))));
    }
    string jm = GuildPayload.Build(union, many, week, classic, 1000L, now, out soon, out cnt, extra);
    Check(jm != null && Encoding.UTF8.GetByteCount(jm) <= GuildPayload.MaxBytes, "payload under the cap: " + (jm == null ? 0 : jm.Length));
    bool s2; int c2;
    Check(GuildPayload.Build(union, new List<Dictionary<string, object>>(), week, classic, 111L, now, out s2, out c2) == null, "no members, no payload");
    Check(GuildPayload.Build(Lt(), new List<Dictionary<string, object>> { m1 }, week, classic, 111L, now, out s2, out c2) == null, "no union id, no payload");
    // a member list without the player is not the whole guild: not sent; the player not known (0) is not checked
    var pair = new List<Dictionary<string, object>> { m1, m2 };
    Check(GuildPayload.Build(union, pair, week, classic, 999L, now, out s2, out c2) == null && c2 == 0, "player not among the members, no payload");
    Check(GuildPayload.SelfMissing(pair, 999L) && !GuildPayload.SelfMissing(pair, 222L) && !GuildPayload.SelfMissing(pair, 0L), "SelfMissing: missing / present / not known");
    Check(!GuildPayload.SelfMissing(new List<Dictionary<string, object>>(), 999L) && !GuildPayload.SelfMissing(null, 999L), "SelfMissing: no members is not 'missing'");
    Check(GuildPayload.SelfMissing(new List<Dictionary<string, object>> { Lt("iPostId", 4L), m2 }, 111L), "SelfMissing: a record without an id does not count");
    Check(GuildPayload.Build(union, pair, week, classic, 0L, now, out s2, out c2) != null && c2 == 2, "player not known: sent as before");
    var gxBad = new GuildExtra { Gvg = new GvgInfo { Union = Lt("vRoundInfo", "<table>", "m_vGarrisons", Lt("[1]", "x")) } };
    string jgb = GuildPayload.Build(union, pair, week, classic, 111L, now, out s2, out c2, gxBad);
    Check(jgb != null && c2 == 2 && MiniJson.TryParse(jgb) != null, "odd guild war data leaves the members as they are");
    // a list short of the guild's stated member count (an old copy in memory) is not sent; a couple missing is the usual drift
    var unionN = Lt("iUnionId", 55L, "iLevel", 7L, "iMemberNum", 29L, "stBaseAttr", Lt("sUnionName", "S"));
    Check(GuildPayload.Incomplete(unionN, pair, 111L) == "short" && GuildPayload.Build(unionN, pair, week, classic, 111L, now, out s2, out c2) == null, "short list: not sent");
    Check(GuildPayload.Incomplete(unionN, pair, 999L) == "noSelf", "the player missing is told first");
    Check(GuildPayload.Incomplete(union, pair, 111L) == null && GuildPayload.Incomplete(unionN, new List<Dictionary<string, object>>(), 111L) == null, "no stated count / no members: not 'incomplete'");
    Check(GuildPayload.ListShort(26, 29) && !GuildPayload.ListShort(27, 29) && !GuildPayload.ListShort(30, 29) && !GuildPayload.ListShort(5, 0), "ListShort slack of 2");
    var unionS = Lt("iUnionId", 55L, "iLevel", 7L, "iMemberNum", 4L, "stBaseAttr", Lt("sUnionName", "S"));
    string jn = GuildPayload.Build(unionS, pair, week, classic, 111L, now, out s2, out c2);
    Check(jn != null && jn.Contains("\"level\":7,\"memberNum\":4}"), "stated count within the slack: sent with memberNum: " + jn);
    Check(json != null && !json.Contains("memberNum"), "no stated count, no memberNum");
    // a member record shaped like the game's (fake values): the app's own check agrees with the site's (selfUid among members[].uid)
    Func<long, long, Dictionary<string, object>> real = (zone, uid) => Lt("stPlayerIdType", Lt("iZoneId", zone, "iUid", uid), "iPostId", 4L, "iJoinTime", 1760000000L,
      "iTotalHistoryActive", 52000L, "iSevenActive", 1400L, "vSevenActive", LArr(Lt("iTime", 1789990000L, "iActive", 200L)),
      "stRoleSimpleInf", Lt("stRoleId", Lt("iZoneId", zone, "iUid", uid), "sName", "Fake" + uid, "iLevel", 100L, "iPower", 9000000L, "iLogoutTime", 1789995000L, "iHeadId", 1L),
      "mWeekBossData", Lt("[1]", Lt("iDamageNum", "123456789", "iPrevDamageNum", 0L, "iFightNum", 2L, "iLastFightTime", 1789990000L)), "mBossData", Lt());
    var realList = new List<Dictionary<string, object>>();
    for (int i = 0; i < 29; i++) realList.Add(real(50100L + i, 12000000L + i * 7));
    var unionR = Lt("iUnionId", 68000L, "iLevel", 12L, "iMemberNum", 29L, "stBaseAttr", Lt("sUnionName", "Fake"));
    string jr = GuildPayload.Build(unionR, realList, week, classic, 0L, now, out s2, out c2);
    var uids = new HashSet<long>();
    foreach (var mo in (List<object>)MiniJson.AsObject(MiniJson.TryParse(jr))["members"]) uids.Add(ArenaOpp.Num(MiniJson.AsObject(mo)["uid"]));
    foreach (long probe in new[] { 12000000L, 12000000L + 28 * 7, 12000001L, 50100L, 99L }) {
      Check(GuildPayload.SelfMissing(realList, probe) == !uids.Contains(probe), "the app's self check matches the site's for " + probe);
      Check((GuildPayload.Build(unionR, realList, week, classic, probe, now, out s2, out c2) != null) == uids.Contains(probe), "sent exactly when the site would accept the player " + probe);
    }
    Check(GuildPayload.Incomplete(unionR, realList, 12000000L + 14 * 7) == null, "a whole real-shaped list with the player is sent");
    // the player's id in other forms: a numeric string, a float, only under the role info's stRoleId
    var strSelf = Lt("stPlayerIdType", Lt("iZoneId", "50167", "iUid", "12538984"), "iPostId", 4L, "stRoleSimpleInf", Lt("sName", "S"));
    var fltSelf = Lt("stPlayerIdType", Lt("iZoneId", 50167.0, "iUid", 12538984.0), "iPostId", 4L, "stRoleSimpleInf", Lt("sName", "S"));
    var roleSelf = Lt("iPostId", 4L, "stRoleSimpleInf", Lt("stRoleId", Lt("iZoneId", 50167L, "iUid", 12538984L), "sName", "S"));
    foreach (var sm in new[] { strSelf, fltSelf, roleSelf }) {
      var lst = new List<Dictionary<string, object>>(realList); lst.Add(sm);
      long z; Check(GuildPayload.MemberUid(sm, out z) == 12538984L && z == 50167L, "MemberUid reads the id form");
      Check(!GuildPayload.SelfMissing(lst, 12538984L), "the player found in that form");
      string js = GuildPayload.Build(unionR, lst, week, classic, 12538984L, now, out s2, out c2);
      Check(js != null && c2 == 30 && js.Contains("\"zone\":50167,\"selfUid\":12538984") && js.Contains("{\"uid\":12538984,\"zone\":50167,"), "payload with the player in that form: " + (js == null ? "null" : js.Substring(0, 60)));
    }
    // the diagnostics run without a game: a file with valid JSON, no exception
    string dumpPath = Path.Combine(Path.GetTempPath(), "rf-dump-guild-test.json");
    string dumpRes = RFX.DumpGuild(dumpPath, "test");
    var dumpObj = MiniJson.AsObject(MiniJson.TryParse(File.ReadAllText(dumpPath)));
    Check(dumpRes != null && dumpObj != null && (string)dumpObj["app"] == "test", "dump-guild without a game: " + dumpRes);
    try { File.Delete(dumpPath); } catch (Exception) { }
    // far refresh: not soon; the hash ignores "at"
    string far = GuildPayload.Build(union, new List<Dictionary<string, object>> { m1, m2 }, LArr(Lt("iRefreshTime", 1790900000L)), classic, 111L, now, out s2, out c2);
    Check(!s2, "refresh in 10 days is not soon");
    string later = GuildPayload.Build(union, new List<Dictionary<string, object>> { m1, m2 }, LArr(Lt("iRefreshTime", 1790900000L)), classic, 111L, now + 600000, out s2, out c2);
    string h1 = GuildPayload.Hash(far);
    Check(far != later && h1 == GuildPayload.Hash(later), "the hash ignores at");
    Check(!GuildPayload.ShouldSend(h1, h1, false), "unchanged and no reset near: not sent");
    Check(GuildPayload.ShouldSend(h1, h1, true), "unchanged but the reset is near: sent");
    Check(GuildPayload.ShouldSend(h1, null, false) && GuildPayload.ShouldSend(h1, "x", false), "first / changed: sent");
    var t0 = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    Check(GuildPayload.Due(true, t0, t0, 10) && !GuildPayload.Due(false, t0.AddMinutes(9), t0, 10) && GuildPayload.Due(false, t0.AddMinutes(10), t0, 10), "cadence");
    long n;
    Check(GuildPayload.Try("12", out n) && n == 12 && GuildPayload.Try(3.0e9, out n) && n == 3000000000L && !GuildPayload.Try("abc", out n) && !GuildPayload.Try(null, out n), "number parsing");
  }

// ------------------------------------------------------------------ the live tables of more than one login in memory

  /// <summary>A stand-in game memory holding script tables (strings, tables, nodes laid out as the reader expects).</summary>
  sealed class FakeLua : RFX.IGameMemory {
    public const ulong Base = 0x20000000;
    public readonly byte[] Mem;
    int top = 64;
    /// <summary>The bytes of the memory the regions report as writable (-1 = all of it): a region the game allocates later is "not visible" yet.</summary>
    public int Visible = -1;
    /// <summary>The next allocation's offset (to place a table in the hidden part).</summary>
    public int Top { get { return top; } set { top = value; } }
    public ulong Knock;   // the value of the running GameData's m_Knock: moves on every read (0 = the game shows no frames)
    public FakeLua(int size) { Mem = new byte[size]; }
    public IList<ulong[]> Writable() { return new[] { new ulong[] { Base, (ulong)(Visible >= 0 ? Visible : Mem.Length) } }; }
    public bool Read(ulong addr, byte[] buf, int n, out int got) {
      got = 0;
      if (addr < Base || addr + (ulong)n > Base + (ulong)Mem.Length) return false;
      if (Knock != 0 && Knock >= addr && Knock < addr + (ulong)n) {
        int ko = (int)(Knock - Base); double d = BitConverter.ToDouble(Mem, ko) + 0.016; if (d > 1) d -= 1;
        Array.Copy(BitConverter.GetBytes(d), 0, Mem, ko, 8);
      }
      Buffer.BlockCopy(Mem, (int)(addr - Base), buf, 0, n); got = n; return true;
    }
    public ulong Alloc(int n) { top = (top + 15) & ~15; ulong a = Base + (ulong)top; top += n; return a; }
    public void W64(ulong a, ulong v) { Array.Copy(BitConverter.GetBytes(v), 0, Mem, (int)(a - Base), 8); }
    public void W32(ulong a, int v) { Array.Copy(BitConverter.GetBytes(v), 0, Mem, (int)(a - Base), 4); }
    public ulong R64(ulong a) { return BitConverter.ToUInt64(Mem, (int)(a - Base)); }
    public ulong Str(string s) {
      var b = Encoding.ASCII.GetBytes(s); ulong a = Alloc(24 + b.Length + 1);
      Mem[a - Base + 8] = 4; Mem[a - Base + 11] = (byte)b.Length; Array.Copy(b, 0, Mem, (int)(a - Base) + 24, b.Length);
      return a;
    }
  }
  struct FTab { public ulong A; public FTab(ulong a) { A = a; } }

  /// <summary>One script state of the stand-in game: its own key strings (one string object per text).</summary>
  sealed class FakeState {
    public readonly FakeLua M; readonly Dictionary<string, ulong> strs = new Dictionary<string, ulong>();
    public FakeState(FakeLua m) { M = m; }
    public ulong S(string s) { ulong a; if (!strs.TryGetValue(s, out a)) strs[s] = a = M.Str(s); return a; }
    public FTab T(params object[] kv) {
      int n = kv.Length / 2, lsize = 0; while ((1 << lsize) < n) lsize++;
      ulong t = M.Alloc(56), np = M.Alloc(32 << lsize);
      M.Mem[t - FakeLua.Base + 8] = 5; M.Mem[t - FakeLua.Base + 11] = (byte)lsize; M.W64(t + 24, np);
      for (int i = 0; i < n; i++) {
        ulong nd = np + (ulong)(32 * i); object k = kv[2 * i], v = kv[2 * i + 1];
        if (k is string) { M.W64(nd + 16, S((string)k)); M.W32(nd + 24, 0x44); } else { M.W64(nd + 16, (ulong)(long)k); M.W32(nd + 24, 0x13); }
        if (v is long) { M.W64(nd, (ulong)(long)v); M.W32(nd + 8, 0x13); }
        else if (v is double) { M.W64(nd, (ulong)BitConverter.DoubleToInt64Bits((double)v)); M.W32(nd + 8, 3); }
        else if (v is bool) { M.W64(nd, (bool)v ? 1UL : 0UL); M.W32(nd + 8, 1); }
        else if (v is string) { M.W64(nd, S((string)v)); M.W32(nd + 8, 0x44); }
        else { M.W64(nd, ((FTab)v).A); M.W32(nd + 8, 0x45); }
      }
      return new FTab(t);
    }
    /// <summary>The address of the value of a field (its node).</summary>
    public ulong ValueAt(FTab t, string key) {
      ulong np = M.R64(t.A + 24); int nn = 1 << M.Mem[t.A - FakeLua.Base + 11];
      for (int i = 0; i < nn; i++) { ulong nd = np + (ulong)(32 * i); if (M.R64(nd + 16) == S(key)) return nd; }
      return 0;
    }
  }

  /// <summary>One login's tables: GameData (with its frame counter) unless <paramref name="gameData"/> is false, EquipData,
  /// HeroData, PlayerData. Returns the state; <paramref name="knock"/> = GameData's m_Knock value, <paramref name="uidAt"/> =
  /// PlayerData's m_Uid value.</summary>
  static FakeState FakeLogin(FakeLua m, long uid, long[] heroes, long[] items, bool gameData, out ulong knock, out ulong uidAt) {
    var st = new FakeState(m); long inst = 20001;
    knock = 0;
    if (gameData) { var gd = st.T("m_bInitElva", false, "m_Knock", 0.25, "class", st.T("__cname", "GameData"), "____instanceId", inst++); knock = st.ValueAt(gd, "m_Knock"); }
    var eq = new List<object>();
    for (int i = 0; i < items.Length; i++)
      eq.AddRange(new object[] { items[i], st.T("iItemUid", items[i], "iItemId", 1001L, "iStarLvl", 5L, "iHeroId", heroes[i % heroes.Length], "iIntensifyLvl", 12L,
        "bLocked", false, "vConfig", st.T("m_ID", 1001L), "vMasterAttrList", st.T(), "vViceAttrList", st.T()) });
    st.T("suitId2EquipIdExt", st.T(), "equips", st.T(eq.ToArray()), "class", st.T("__cname", "EquipData"), "____instanceId", inst++);
    var hs = new List<object>();
    foreach (var h in heroes) hs.AddRange(new object[] { h, st.T("iHeroId", h, "iBaseId", h / 100000, "iLevel", 60L, "vEquipSlot", st.T()) });
    st.T("m_CharactorDatas", st.T(hs.ToArray()), "m_CampHeroPerfectReward", st.T(), "class", st.T("__cname", "HeroData"), "____instanceId", inst++);
    var pd = st.T("m_mNegSpecialItem", st.T(), "m_Uid", uid, "m_Name", "P" + uid, "m_Level", 50L, "m_Coin", 1000L, "m_AllBeastInfo", st.T(),
                  "class", st.T("__cname", "PlayerData"), "____instanceId", inst++);
    uidAt = st.ValueAt(pd, "m_Uid");
    return st;
  }

  static List<long> AccUids(string json, string list, string key) {
    var r = new List<long>(); var o = MiniJson.AsObject(MiniJson.TryParse(json));
    object l; if (o == null || !o.TryGetValue(list, out l) || !(l is List<object>)) return r;
    foreach (var e in (List<object>)l) { var d = MiniJson.AsObject(e); if (d != null) r.Add(ArenaOpp.Num(d[key])); }
    return r;
  }
  static long AccPlayer(string json) { var o = MiniJson.AsObject(MiniJson.TryParse(json)); var meta = o != null ? MiniJson.AsObject(o["meta"]) : null; object p; return meta != null && meta.TryGetValue("player", out p) ? ArenaOpp.Num(p) : 0; }

  static bool WaitFormSearch() { for (int i = 0; i < 200 && RFX.FormSearchRunning; i++) System.Threading.Thread.Sleep(25); return !RFX.FormSearchRunning; }

  // a form the game creates after the account read: it can lie outside the regions the read cached
  static void FormSearchTests() {
    Console.WriteLine("Forms found late (inventory opened after the account read)");
    var lines = new List<string>(); var saveLog = RFX.LiveLog; RFX.LiveLog = l => { lock (lines) lines.Add(l); };
    var heroes = new long[] { 203400001, 209700001 }; var items = new long[] { 900000001, 900000002 };
    Func<FakeLua, int, FTab> makeInv = (mm, at) => {
      mm.Top = at; var si = new FakeState(mm);
      var panel = si.T("m_CurrentBottomButtonState", 2L);
      var inv = si.T("m_LuaLogicActive", true, "m_CurrentMainTabId2", -9998L, "m_CachedContentPanels", si.T(-9998L, panel));
      return si.T(103L, inv);   // the node: ID_FORM_BACKPACKINTEGRATION -> the form
    };
    try {
      // A: the background search
      var m = new FakeLua(4 << 20); RFX.TestMemory = m; RFX.ForgetLive();
      ulong knock, uid; FakeLogin(m, 111, heroes, items, true, out knock, out uid); m.Knock = knock;
      m.Visible = 2 << 20;
      Check(RFX.ReadLiveAccount(61) != null, "forms: the account read with half the memory visible");
      makeInv(m, 3 << 20);
      Check(RFX.ReadInventory(-1) == null, "forms: the inventory in the hidden half is not known");
      Check(RFX.RequestFormSearch("test a", 0) && WaitFormSearch(), "forms: a search runs and ends");
      Check(RFX.RequestFormSearch("test b", 0) && WaitFormSearch(), "forms: a second search runs");
      int nf; lock (lines) nf = lines.FindAll(x => x.StartsWith("forms: inventory not found")).Count;
      Check(nf == 1 && RFX.ReadInventory(-1) == null, "forms: two misses, one app-log line (" + nf + ")");
      m.Visible = 4 << 20;   // the game's memory grew
      Check(RFX.RequestFormSearch("test c", 0) && WaitFormSearch(), "forms: the search after the growth runs");
      var sa = RFX.ReadInventory(-1);
      Check(sa != null && sa.Open, "forms: the search refreshes the regions and finds the inventory");
      lock (lines) Check(lines.Exists(x => x.StartsWith("forms: inventory found after ")), "forms: the find is in the app log");
      // B: a rescan from the reader
      var m2 = new FakeLua(4 << 20); RFX.TestMemory = m2; RFX.ForgetLive();
      FakeLogin(m2, 112, heroes, items, true, out knock, out uid); m2.Knock = knock; m2.Visible = 2 << 20;
      Check(RFX.ReadLiveAccount(62) != null, "forms: second account read");
      makeInv(m2, (3 << 20) + (512 << 10));
      Check(RFX.ReadInventory(0) == null, "forms: hidden: not found");
      m2.Visible = 4 << 20;
      var sb = RFX.ReadInventory(0);
      Check(sb != null && sb.Open, "forms: LiveInvForm(0) finds it after the memory grew");
      // C: the window table gives it without a scan
      var m3 = new FakeLua(4 << 20); RFX.TestMemory = m3; RFX.ForgetLive();
      FakeLogin(m3, 113, heroes, items, true, out knock, out uid); m3.Knock = knock; m3.Visible = 2 << 20;
      Check(RFX.ReadLiveAccount(63) != null, "forms: third account read");
      var node = makeInv(m3, 3 << 20);
      Check(RFX.ReadInventory(0) == null, "forms: hidden, no window table: not found");
      // the window table: a table whose node 103 holds the form table (the form is in the hidden half; the scan cannot see it)
      ulong formAt = m3.R64(m3.R64(node.A + 24));
      m3.Top = 1 << 20; var ui2 = new FakeState(m3).T(103L, new FTab(formAt));
      RFX.SetUiTableForTest(ui2.A);
      try {
        var sc = RFX.ReadInventory(0);
        Check(sc != null && sc.Open, "forms: the window table lookup finds the form without a scan");
      } finally { RFX.SetUiTableForTest(0); }
      // C2: the window table holds another form but not the inventory; the inventory is in visible memory: the scan runs and finds it
      var m4 = new FakeLua(4 << 20); RFX.TestMemory = m4; RFX.ForgetLive();
      FakeLogin(m4, 114, heroes, items, true, out knock, out uid); m4.Knock = knock;
      Check(RFX.ReadLiveAccount(64) != null, "forms: fourth account read");
      makeInv(m4, 3 << 20);   // all memory visible
      m4.Top = 1 << 20;
      var otherForm = new FakeState(m4).T("m_LuaLogicActive", true);
      var ui4 = new FakeState(m4).T(104L, otherForm);   // ID_FORM_EQUIPSUBLIMING only
      RFX.SetUiTableForTest(ui4.A);
      try {
        // (a form the window table does not hold is not created: a scan would find only a dead copy, so none runs)
        int p4 = RFX.FullPasses;
        var sd = RFX.ReadInventory(0);
        Check(sd == null, "forms: the window table lacks the inventory: not taken from a scan");
        Check(RFX.RequestFormSearch("test d", 0) && !RFX.FormSearchRunning, "forms: a search with the window table known runs at once, on the caller");
        Eq(p4, RFX.FullPasses, "forms: the window table known: no full pass");
        // the game builds the inventory: the window table gets its node (a rehash: a new node array of two slots) and the next tick takes it
        m4.Top = (1 << 20) + (64 << 10); var st4 = new FakeState(m4);
        var inv4 = st4.T("m_LuaLogicActive", true, "m_CurrentMainTabId2", -9998L, "m_CachedContentPanels", st4.T(-9998L, st4.T("m_CurrentBottomButtonState", 2L)));
        var ui4b = st4.T(104L, otherForm, 103L, inv4);
        m4.W64(ui4.A + 24, m4.R64(ui4b.A + 24)); m4.Mem[ui4.A - FakeLua.Base + 11] = m4.Mem[ui4b.A - FakeLua.Base + 11];
        Check(RFX.FormsTick(0) && RFX.FormsFromTable, "forms: the tick check reads the window table");
        var se = RFX.ReadInventory(-1);
        Check(se != null && se.Open, "forms: the inventory the game built is read on the next tick, without a search");
        Eq(p4, RFX.FullPasses, "forms: following the window table makes no full pass");
        // the game closes it for good (its node goes): the tick check drops it
        m4.W64(ui4.A + 24, m4.R64(ui4b.A + 24)); m4.W32(m4.R64(ui4.A + 24) + 32 + 24, 0);   // the second slot's key: nil
        RFX.FormsTick(0);
        Check(RFX.ReadInventory(-1) == null, "forms: a form gone from the window table is not read any more");
        Eq(p4, RFX.FullPasses, "forms: still no full pass");
      } finally { RFX.SetUiTableForTest(0); }
      // D: the busy flag after a failed search
      RFX.TestSearchHook = () => { throw new InvalidOperationException("boom"); };
      try {
        Check(RFX.RequestFormSearch("boom", 0), "forms: a search that throws starts");
        Check(WaitFormSearch(), "forms: the busy flag is reset after the exception");
        Check(RFX.RequestFormSearch("boom again", 0) && WaitFormSearch(), "forms: a new search can start after it");
      } finally { RFX.TestSearchHook = null; }
    } finally { RFX.LiveLog = saveLog; RFX.TestMemory = null; RFX.ForgetLive(); }
  }

  // a field read before is taken from its node alone: the value read fresh, the node array not read again; a rehash / a changed slot is a full lookup
  static void FieldNodeTests() {
    Console.WriteLine("Field reads: a known field from its node");
    try {
      var m = new FakeLua(1 << 20); RFX.TestMemory = m; RFX.ForgetLive();
      var st = new FakeState(m);
      var kv = new List<object>(); for (int i = 0; i < 100; i++) { kv.Add("m_F" + i); kv.Add((long)i); }
      kv.Add("m_ScrollY"); kv.Add(1.5);
      var t = st.T(kv.ToArray());
      ulong v; int tt;
      Check(RFX.FieldForTest(t.A, "m_ScrollY", out v, out tt) && tt == 3 && BitConverter.Int64BitsToDouble((long)v) == 1.5, "field: first read finds it");
      long h0 = RFX.FieldHits, b0 = RFX.ReadBytes;
      m.W64(st.ValueAt(t, "m_ScrollY"), (ulong)BitConverter.DoubleToInt64Bits(7.25));
      Check(RFX.FieldForTest(t.A, "m_ScrollY", out v, out tt) && BitConverter.Int64BitsToDouble((long)v) == 7.25, "field: the second read sees the new value");
      Eq(h0 + 1, RFX.FieldHits, "field: the second read is taken from the node");
      Check(RFX.ReadBytes - b0 <= 56 + 32, "field: the second read reads the header and one node (" + (RFX.ReadBytes - b0) + " bytes)");
      // the table rebuilt its node array (a rehash): the old node is not trusted, the field found again
      var t2 = st.T(kv.ToArray()); ulong np2 = m.R64(t2.A + 24);
      m.W64(st.ValueAt(t2, "m_ScrollY"), (ulong)BitConverter.DoubleToInt64Bits(9.0));
      m.W64(t.A + 24, np2);
      long h1 = RFX.FieldHits;
      Check(RFX.FieldForTest(t.A, "m_ScrollY", out v, out tt) && BitConverter.Int64BitsToDouble((long)v) == 9.0, "field: after a rehash the new node array is read");
      Eq(h1, RFX.FieldHits, "field: a rehash is a full lookup");
      // the slot holds another key now: not taken from it
      ulong nd = st.ValueAt(t2, "m_ScrollY"); m.W64(nd + 16, st.S("m_Other"));
      Check(!RFX.FieldForTest(t.A, "m_ScrollY", out v, out tt), "field: a slot that holds another key is no hit, and the key is gone");
      Check(RFX.FieldForTest(t.A, "m_F7", out v, out tt) && (long)v == 7, "field: the other fields still read");
      // the bench: one field of a 128-slot form table, read 20 000 times with the node kept and without (the reader before)
      var alloc = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread");
      var t3 = st.T(kv.ToArray());
      foreach (bool off in new[] { true, false }) {
        RFX.FieldSlotsOff = off; RFX.FieldForTest(t3.A, "m_ScrollY", out v, out tt);
        long a0 = alloc != null ? (long)alloc.Invoke(null, null) : 0; long r0 = RFX.Reads, by0 = RFX.ReadBytes;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20000; i++) RFX.FieldForTest(t3.A, "m_ScrollY", out v, out tt);
        double us = sw.Elapsed.TotalMilliseconds * 1000 / 20000; long ab = alloc != null ? ((long)alloc.Invoke(null, null) - a0) / 20000 : -1;
        Console.WriteLine("  bench field read (" + (off ? "whole node array" : "node kept") + "): " + us.ToString("0.00") + " µs, " + ((RFX.Reads - r0) / 20000.0).ToString("0.0") + " reads / "
          + ((RFX.ReadBytes - by0) / 20000) + " bytes, " + ab + " bytes allocated a read");
        if (!off && alloc != null) Check(ab == 0, "field: a read from the kept node allocates nothing (" + ab + " bytes)");
      }
      RFX.FieldSlotsOff = false;
      Eq(1, RFX.ScanThreads(2), "scan: one thread on 2 cores"); Eq(1, RFX.ScanThreads(4), "scan: one thread on 4 cores"); Eq(7, RFX.ScanThreads(8), "scan: all cores but one on 8");
    } finally { RFX.FieldSlotsOff = false; RFX.TestMemory = null; RFX.ForgetLive(); }
  }

  // the hero screen's form is followed from the node a background search published: no full pass on the polling (UI) thread
  static void HeroFormFollowTests() {
    Console.WriteLine("Hero form: CheckForm takes the published node, no full pass on the caller");
    var heroes = new long[] { 203400001, 209700001 }; var items = new long[] { 900000001, 900000002 };
    try {
      var m = new FakeLua(4 << 20); RFX.TestMemory = m; RFX.ForgetLive();
      ulong knock, uid; FakeLogin(m, 121, heroes, items, true, out knock, out uid); m.Knock = knock;
      Check(RFX.ReadLiveAccount(71) != null, "hero form: the account read");
      m.Top = 3 << 20; var st = new FakeState(m);
      var dead = st.T("m_Other", 1L);   // a rebuilt screen's old copy: no grid component
      var alive = st.T("m_CharactorGrid_InfinityGrid", st.T(), "m_InfinityGridProxy", st.T());
      var node = st.T(102L, alive);     // ID_FORM_CHARACTORMAIN -> the form
      ulong slot = m.R64(node.A + 24);
      int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
      Check(RFX.RequestFormSearch("test hero", 0) && WaitFormSearch(), "hero form: the background search runs");
      RFX.TestSearchHook = () => { throw new InvalidOperationException("no pass in this test"); };   // a search the poll asks for must not run a pass that would count
      // 1: no form known, the search published a live one: followed, zero full passes on the caller
      var a = new EquipAddrs { Pid = pid };
      int p0 = RFX.FullPasses;
      RFX.Poll(a, new List<long>());
      Check(a.Form == alive.A, "hero form: no form known, the published live node is taken");
      Eq(p0, RFX.FullPasses, "hero form: taking the published node makes no full pass");
      // 2: the form is dead and the node now holds a new live form: followed, the panel dropped
      var a2 = new EquipAddrs { Pid = pid, Form = dead.A, Panel = 0x1234UL };
      RFX.Poll(a2, new List<long>());
      Check(a2.Form == alive.A && a2.Panel == 0UL, "hero form: a dead form whose node holds a new live form is followed, panel -> 0");
      Eq(p0, RFX.FullPasses, "hero form: following the rebuilt form makes no full pass");
      // 3: the form is dead and the node holds only a dead copy: lost without a pass on the caller (a search is asked for)
      m.W64(slot, dead.A);
      var a3 = new EquipAddrs { Pid = pid, Form = dead.A, Panel = 0x1234UL };
      RFX.Poll(a3, new List<long>());
      Check(a3.Form == 0UL && a3.Panel == 0UL, "hero form: a dead form with no live node is dropped, panel -> 0");
      Eq(p0, RFX.FullPasses, "hero form: a dead form makes no full pass on the caller");
      // 4: nothing known and nothing published: still no pass on the caller
      var a4 = new EquipAddrs { Pid = pid };
      RFX.Poll(a4, new List<long>());
      Check(a4.Form == 0UL, "hero form: nothing published: still unknown");
      Eq(p0, RFX.FullPasses, "hero form: nothing published makes no full pass on the caller");
      WaitFormSearch();
    } finally { RFX.TestSearchHook = null; RFX.TestMemory = null; RFX.ForgetLive(); }
  }

  static void FormSearchPolicyTests() {
    Console.WriteLine("Form search policy: the pilot's own open click, held screens");
    int gap; string w;
    w = FormSearchPolicy.Want(ScreenKind.City, false, 0, out gap);
    Check(w != null && gap == 1000, "policy: right after the pilot's open click a search is wanted, gap 1000 ms");
    w = FormSearchPolicy.Want(ScreenKind.City, false, 5999, out gap);
    Check(w != null && gap == 1000, "policy: still wanted just inside 6 s");
    w = FormSearchPolicy.Want(ScreenKind.City, false, 6000, out gap);
    Check(w == null, "policy: not after 6 s (a click that changed nothing is not searched for forever)");
    w = FormSearchPolicy.Want(ScreenKind.City, false, -1, out gap);
    Check(w == null, "policy: no open click awaited, a known screen: no search");
    w = FormSearchPolicy.Want(ScreenKind.Unknown, false, -1, out gap);
    Check(w != null && gap == 2000, "policy: Unknown screen: search every 2 s");
    w = FormSearchPolicy.Want(ScreenKind.Inventory, true, -1, out gap);
    Check(w != null && gap == 2000, "policy: a held screen (reads failing) is treated like Unknown");
    w = FormSearchPolicy.Want(ScreenKind.Unknown, true, 100, out gap);
    Check(w != null && gap == 1000, "policy: the open click's faster gap wins over Unknown");
    Check(FormSearchPolicy.OwnOpenGapMs >= 1000, "policy: the gap stays >= 1000 ms");
  }


  static void LiveStateTests() {
    Console.WriteLine("Live tables: re-login and account switch");
    try {
      // login A, the game running
      var m = new FakeLua(4 << 20); RFX.TestMemory = m; RFX.ForgetLive();
      ulong knockA, knockB, uidA, uidB;
      FakeLogin(m, 111, new long[] { 203400001, 209700001, 210000001 }, new long[] { 900000001, 900000002, 900000003 }, true, out knockA, out uidA);
      m.Knock = knockA;
      RFX.Log.Length = 0;
      string a = RFX.ReadLiveAccount(41);
      Check(a != null && AccPlayer(a) == 111 && AccUids(a, "heroes", "iHeroId").Count == 3 && AccUids(a, "equipment", "iItemUid").Count == 3, "login A read: player 111, 3 heroes, 3 items");
      Check(RFX.Log.ToString().Contains("Script state: the only one (running)"), "login A: one login in memory, seen running");
      // the game logs in again as B: A's tables stay in memory with the same key texts in other string objects; the reader
      // remembers A's tables and key addresses (the old copy). A's frame counter stops, B's runs.
      FakeLogin(m, 222, new long[] { 205000002, 206000002 }, new long[] { 900000101, 900000102 }, true, out knockB, out uidB);
      m.Knock = knockB;
      RFX.Log.Length = 0;
      string b = RFX.ReadLiveAccount(41);
      string lg = RFX.Log.ToString();
      var bh = AccUids(b, "heroes", "iHeroId"); var bi = AccUids(b, "equipment", "iItemUid");
      Check(b != null && AccPlayer(b) == 222, "after the switch: player 222 (" + (b == null ? "null" : AccPlayer(b).ToString()) + ")");
      Check(bh.Count == 2 && bh.Contains(205000002) && !bh.Contains(203400001), "after the switch: only B's heroes " + string.Join(",", bh));
      Check(bi.Count == 2 && bi.Contains(900000101) && !bi.Contains(900000001), "after the switch: only B's items, never A's with B's player " + string.Join(",", bi));
      Check(lg.Contains("Key names forgotten: the game showed no frames"), "the invalidation is logged with its reason");
      Check(lg.Contains("Script state: the running one of 2; a new login since the last search"), "the running login chosen of two");
      Check(lg.Contains("Player of this reading: 222 (was 111)"), "the player change is logged");
      // the next reading keeps B without a search
      RFX.Log.Length = 0;
      string b2 = RFX.ReadLiveAccount(41);
      Check(b2 != null && AccPlayer(b2) == 222 && !RFX.Log.ToString().Contains("Live tables found"), "B again: no new search");
      // neither login shows frames (the game paused): which one is live is not known, nothing is sent
      m.Knock = 0; RFX.ForgetLive();
      RFX.Log.Length = 0; RFX.LastError = null;
      string c = RFX.ReadLiveAccount(41);
      Check(c == null && RFX.LastError == "not_ready" && RFX.LastDetail != null && RFX.LastDetail.Contains("more than one login"), "two logins, none running: not sent (" + RFX.LastError + ": " + RFX.LastDetail + ")");
      Check(SyncOutcome.Classify(true, ExtractError.NoAccountData) == SyncKind.NotReady, "an automatic sync then waits and looks again");

      // a game without the frame counter: the player read differs from the last reading's, the tables are looked for again
      var m2 = new FakeLua(4 << 20); RFX.TestMemory = m2; RFX.ForgetLive();
      ulong k0, u0;
      FakeLogin(m2, 333, new long[] { 203400003 }, new long[] { 900000201 }, false, out k0, out u0);
      string d1 = RFX.ReadLiveAccount(42);
      Check(d1 != null && AccPlayer(d1) == 333, "no frame counter, one login: read as before");
      m2.W64(u0, 444);   // the same tables now say another player
      RFX.Log.Length = 0;
      string d2 = RFX.ReadLiveAccount(42);
      Check(d2 != null && AccPlayer(d2) == 444 && RFX.Log.ToString().Contains("Player 444 is not the player of the last reading (333): the live tables are looked for again")
            && RFX.Log.ToString().Contains("Live tables found"), "another player than the last reading: a full search before sending");
      // two logins and no frame counter: not sent
      FakeLogin(m2, 555, new long[] { 203400005, 203400006 }, new long[] { 900000301 }, false, out k0, out u0);
      RFX.ForgetLive(); RFX.LastError = null;
      string d3 = RFX.ReadLiveAccount(42);
      Check(d3 == null && RFX.LastError == "not_ready", "two logins, no frame counter: not sent");

      // speed: a big account read from the live tables found before
      var mb = new FakeLua(32 << 20); RFX.TestMemory = mb; RFX.ForgetLive();
      var hb = new long[600]; for (int i = 0; i < hb.Length; i++) hb[i] = (2000 + i) * 100000L + 1;
      var ib = new long[3000]; for (int i = 0; i < ib.Length; i++) ib[i] = 910000000 + i;
      ulong kb, ub; FakeLogin(mb, 777, hb, ib, true, out kb, out ub); mb.Knock = kb;
      string w = RFX.ReadLiveAccount(43);
      var sw = System.Diagnostics.Stopwatch.StartNew(); long r0 = RFX.Reads; int runs = 5;
      for (int i = 0; i < runs; i++) w = RFX.ReadLiveAccount(43);
      Console.WriteLine("  bench: account of 600 heroes / 3000 items from the live tables: " + (sw.ElapsedMilliseconds / runs) + " ms, " + ((RFX.Reads - r0) / runs) + " memory reads per reading");
      Check(w != null && AccUids(w, "heroes", "iHeroId").Count == 600 && AccUids(w, "equipment", "iItemUid").Count == 3000, "bench reading complete");
    } finally { RFX.TestMemory = null; RFX.ForgetLive(); }
  }

  static void ErrorReportTests(string mock) {
    string code = Tok('A');
    // the setting: off by default, kept in config.json
    Check(!new AppConfig().ErrorReports && !AppConfig.FromJson("{}").ErrorReports, "reports are off by default");
    var rc = new AppConfig(); rc.ErrorReports = true;
    Check(AppConfig.FromJson(rc.ToJson()).ErrorReports, "the switch survives save/load");

    Eq("pilot.hero_failed", ErrorReports.NormalizeKind("pilot.hero_failed"), "kind kept");
    Eq("sync_net", ErrorReports.NormalizeKind("Sync Net!"), "kind lower-cased, spaces -> _");
    Eq("other", ErrorReports.NormalizeKind("  ###"), "nothing usable -> other");
    Eq(64, ErrorReports.NormalizeKind(new string('a', 100)).Length, "kind capped at 64");

    string prof = @"C:\Users\Alex";
    string dirty = "send with " + code + " and Authorization: Bearer abc.DEF-123 saved " + @"c:\users\alex\AppData\Roaming\RealmForge\battles\b1.json";
    string clean = ErrorReports.Scrub(dirty, prof);
    Check(clean.IndexOf(code, StringComparison.Ordinal) < 0 && clean.Contains("rf_***"), "sync code masked: " + clean);
    Check(clean.Contains("Bearer ***") && !clean.Contains("abc.DEF"), "bearer token masked");
    Check(clean.Contains(@"%USERPROFILE%\AppData") && clean.IndexOf("alex", StringComparison.OrdinalIgnoreCase) < 0, "user folder hidden (any case)");
    Check(ErrorReports.Scrub("item rf_" + new string('A', 31) + " ok", prof).Contains("rf_" + new string('A', 31)), "not a code (31 chars) left alone");

    var lines = new StringBuilder();
    for (int i = 1; i <= 1000; i++) lines.Append("2026-09-28 12:00:00  line ").Append(i).Append("\r\n");
    string tail = ErrorReports.Tail(lines.ToString(), 300, 100000);
    Check(tail.StartsWith("2026-09-28 12:00:00  line 701\n", StringComparison.Ordinal) && tail.EndsWith("line 1000\n", StringComparison.Ordinal), "tail: the last 300 lines");
    Eq(300, tail.Split('\n').Length - 1, "tail line count");
    string capped = ErrorReports.Tail(lines.ToString(), 300, 1000);
    Check(capped.Length <= 1000 && capped.StartsWith("2026-", StringComparison.Ordinal) && capped.EndsWith("line 1000\n", StringComparison.Ordinal), "tail: char cap cuts at a line start");
    Eq("a\nb", ErrorReports.Tail("a\nb", 300, 1000), "short text whole");
    Eq("", ErrorReports.Tail(null, 300, 1000), "no text");

    string logPath = Path.Combine(Path.GetTempPath(), "rf-report-test-" + Guid.NewGuid().ToString("N") + ".log");
    try {
      var big = new StringBuilder();
      for (int i = 1; i <= 3000; i++) big.Append("2026-09-28 12:00:00  live: hero 229000000 part ").Append(i).Append(" Ж\r\n");
      big.Append("2026-09-28 12:00:01  send: Ok 200 code " + code + "\r\n");
      File.WriteAllText(logPath, big.ToString(), new UTF8Encoding(false));
      using (var hold = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) {   // the program keeps it open
        string lt = ErrorReports.ReadLogTail(logPath);
        Check(lt.Split('\n').Length - 1 <= ErrorReports.LogLines && lt.Length <= ErrorReports.MaxLogChars, "journal tail: ≤300 lines, ≤20 000 chars (" + lt.Length + ")");
        Check(lt.Contains("part 3000 Ж") && lt.TrimEnd('\n').EndsWith("code " + code, StringComparison.Ordinal), "journal tail read while the file is open, UTF-8");
      }
      Eq("", ErrorReports.ReadLogTail(logPath + ".missing"), "no journal -> empty");

      var ctx = new Dictionary<string, string> { { "state", "hero_failed" }, { "exception", "at " + code + " " + new string('x', 5000) } };
      string json = ErrorReports.BuildJson("Pilot.Hero_Failed", "auto: hero_failed with " + code, "1.6.16", "1.1.2", ctx,
                                           ErrorReports.ReadLogTail(logPath), prof);
      Check(json.IndexOf(code, StringComparison.Ordinal) < 0, "the sync code is nowhere in the report body");
      var o = MiniJson.AsObject(MiniJson.Parse(json));
      Check(o != null && MiniJson.GetString(o, "source") == "app" && MiniJson.GetString(o, "kind") == "pilot.hero_failed", "body: source app, kind normalized");
      Check(MiniJson.GetString(o, "appVersion") == "1.6.16" && MiniJson.GetString(o, "gameVersion") == "1.1.2", "body: versions");
      var co = MiniJson.AsObject(o["context"]);
      Check(co != null && MiniJson.GetString(co, "state") == "hero_failed" && MiniJson.GetString(co, "exception").Length <= ErrorReports.MaxContextValue, "body: context values capped");
      string lt2 = MiniJson.GetString(o, "logTail");
      Check(lt2 != null && lt2.Length <= ErrorReports.MaxLogChars && lt2.Contains("rf_***"), "body: journal tail, capped and scrubbed");
      string noMsg = ErrorReports.BuildJson("sync.net", "   ", null, null, null, null, prof);
      var o2 = MiniJson.AsObject(MiniJson.Parse(noMsg));
      Check(MiniJson.GetString(o2, "message") == "sync.net" && !o2.ContainsKey("logTail") && !o2.ContainsKey("gameVersion"), "empty message -> the kind; no journal / game version -> left out");
      Check(MiniJson.GetString(MiniJson.AsObject(MiniJson.Parse(ErrorReports.BuildJson("k", new string('m', 5000), null, null, null, null, null))), "message").Length <= ErrorReports.MaxMessage, "message capped at 2000");

      // limiter: the same kind once per 10 minutes, 20 a day, remembered across restarts
      string marks = logPath + ".sent";
      var t0 = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
      var lim = new ErrorReportLimiter(marks);
      Check(lim.TryTake("sync.net", t0), "first report goes");
      Check(!lim.TryTake("sync.net", t0.AddMinutes(9)), "same kind within 10 minutes: skipped");
      Check(lim.TryTake("pilot.failed", t0.AddMinutes(1)), "another kind goes");
      Check(lim.TryTake("sync.net", t0.AddMinutes(10).AddSeconds(1)), "same kind after 10 minutes goes");
      var lim2 = new ErrorReportLimiter(marks);
      Check(!lim2.TryTake("sync.net", t0.AddMinutes(15)), "marks survive a restart (a crash loop is deduped)");
      Eq(3, lim2.CountToday(t0.AddMinutes(20)), "3 sent today");
      int ok = 0;
      for (int i = 0; i < 30; i++) if (lim2.TryTake("k" + i, t0.AddMinutes(30 + i))) ok++;
      Eq(ErrorReportLimiter.MaxPerDay - 3, ok, "at most 20 a day");
      Check(!lim2.TryTake("fresh", t0.AddHours(23)), "still full 23 h later");
      Check(lim2.TryTake("fresh", t0.AddHours(24).AddMinutes(31)), "a day later there is room again");
      var mem = new ErrorReportLimiter(null);
      Check(mem.TryTake("a", t0) && !mem.TryTake("a", t0), "memory-only limiter");
      File.Delete(marks);

      // the site's contract, against tests/mock-server.mjs
      Eq(ReportStatus.Sent, ErrorReports.Send(mock, code, json, 10000), "POST /api/report -> sent");
      var lr = MiniJson.AsObject(MiniJson.Parse(new WebClient().DownloadString(mock + "/__lastReport")));
      var lh = MiniJson.AsObject(lr["headers"]); var lb = MiniJson.AsObject(lr["body"]);
      Check(MiniJson.GetString(lh, "authorization") == "Bearer " + code && MiniJson.GetString(lh, "contentType").StartsWith("application/json", StringComparison.Ordinal), "signed with the code in the header only, JSON");
      Check(MiniJson.GetString(lb, "kind") == "pilot.hero_failed" && MiniJson.GetString(lb, "logTail").Length > 1000, "the mock got the report with the journal");
      Eq(ReportStatus.RateLimited, ErrorReports.Send(mock, Tok('R'), json, 10000), "429 -> rate limited");
      Eq(ReportStatus.InvalidToken, ErrorReports.Send(mock, Tok('Q'), json, 10000), "unknown code -> invalid token");
      Eq(ReportStatus.Rejected, ErrorReports.Send(mock, code, "{\"source\":\"app\",\"kind\":\"Bad Kind\",\"message\":\"x\"}", 10000), "422 -> rejected");
      Eq(ReportStatus.Unreachable, ErrorReports.Send("http://127.0.0.1:1", code, json, 3000), "no server -> unreachable");
    } finally { try { File.Delete(logPath); } catch (Exception) { } }
  }

  static HeroSample Hs(uint uid, int unit, uint tower, int x, int y, int card = 0, bool dead = false, int deadType = -1,
                       double anger = 0, int angerState = 0, uint ult = 0) {
    return new HeroSample { Uid = uid, Unit = unit, Squad = 1, Tower = tower, OnField = tower != 0, X = x, Y = y, Face = 0, CardState = card,
                            Dead = dead, DeadType = deadType, Anger = anger, MaxAnger = 1000, AngerState = angerState, UltFrame = ult };
  }

  static void TimelineTests() {
    // hero A (uid 100100001, unit 11001): placed by hand at frame 100 (command) and seen at 103; ultimate at 400 (the skill's
    // frame) seen at 403 with rage falling; killed at ~700 (card reborn), placed again (auto, no command) at 900 on another tile
    // hero B (uid 200200002, unit 22002, squad leader): never placed; hero C: placed by hand, retreated by command at 505
    var tb = new TimelineBuilder();
    uint A = 100100001, B = 200200002, C = 300300003;
    Func<uint, HeroSample[], List<HeroSample>> S = (f, arr) => { var l = new List<HeroSample>(arr); tb.AddSample(f, l); return l; };
    S(97, new[] { Hs(A, 11001, 0, -1, -1), Hs(B, 22002, 0, -1, -1), Hs(C, 33003, 0, -1, -1) });
    S(103, new[] { Hs(A, 11001, 7, -1, -1, anger: 100), Hs(B, 22002, 0, -1, -1), Hs(C, 33003, 0, -1, -1) });
    S(106, new[] { Hs(A, 11001, 7, 4, 3, anger: 200), Hs(B, 22002, 0, -1, -1), Hs(C, 33003, 9, 6, 2) });
    S(400, new[] { Hs(A, 11001, 7, 4, 3, anger: 1000), Hs(B, 22002, 0, -1, -1), Hs(C, 33003, 9, 6, 2, anger: 1000) });
    S(403, new[] { Hs(A, 11001, 7, 4, 3, anger: 50, angerState: 1, ult: 401), Hs(B, 22002, 0, -1, -1), Hs(C, 33003, 9, 6, 2, anger: 20, angerState: 1) });
    S(506, new[] { Hs(A, 11001, 7, 4, 3, anger: 300), Hs(B, 22002, 0, -1, -1), Hs(C, 33003, 0, -1, -1) });
    S(700, new[] { Hs(A, 11001, 7, 4, 3, dead: true, deadType: 0, ult: 401), Hs(B, 22002, 0, -1, -1), Hs(C, 33003, 0, -1, -1) });
    S(703, new[] { Hs(A, 11001, 0, -1, -1, card: 2, ult: 401), Hs(B, 22002, 0, -1, -1), Hs(C, 33003, 0, -1, -1) });
    S(900, new[] { Hs(A, 11001, 15, 5, 1, ult: 401), Hs(B, 22002, 0, -1, -1), Hs(C, 33003, 0, -1, -1) });
    var bl = new List<HeroSample> { Hs(B, 22002, 0, -1, -1) }; bl[0].Leader = true; tb.AddSample(905, bl);
    tb.AddBoss(100, 1000); tb.AddBoss(200, 995); tb.AddBoss(300, 980);
    // the record, read twice (the union is kept): placements [unit, x, y, face], C's ultimate and retreat, an emoji
    var cmds = new List<FrameCmd> {
      new FrameCmd { Frame = 100, Cmd = 1000, CUid = 1, Params = new[] { 11001, 4, 3, 90 } },
      new FrameCmd { Frame = 104, Cmd = 1000, CUid = 1, Params = new[] { 33003, 6, 2, 0 } },
      new FrameCmd { Frame = 398, Cmd = 2000, CUid = 1, Params = new[] { 33003, 6, 2 } },
      new FrameCmd { Frame = 505, Cmd = 1001, CUid = 1, Params = new[] { 33003, 6, 2 } },
      new FrameCmd { Frame = 600, Cmd = 50001, CUid = 1, Params = new[] { 3 } } };
    tb.AddCommands(cmds); tb.AddCommands(cmds.GetRange(0, 2));
    Eq(5, tb.Commands, "timeline: commands kept once each (5 distinct of 7 read)");
    var root = MiniJson.Parse(tb.ToJson()) as Dictionary<string, object>;
    Check(root != null, "timeline JSON parses");
    if (root == null) return;
    Eq(905.0, Convert.ToDouble(root["frames"]), "timeline: frames = the last sampled frame");
    Check(Math.Abs(Convert.ToDouble(root["frameSec"]) - 270.0 / 4096) < 1e-12, "timeline: frameSec 270/4096");
    var hs = root["heroes"] as Dictionary<string, object>;
    var a = hs[A.ToString()] as Dictionary<string, object>;
    Func<object, string> Js = o => { var sb = new StringBuilder(); var l = o as List<object>; if (l == null) return "null";
      sb.Append('['); for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(l[i] is List<object> ? Js2(l[i]) : Convert.ToDouble(l[i]).ToString(System.Globalization.CultureInfo.InvariantCulture)); } return sb.Append(']').ToString(); };
    Eq("[[100,4,3,90,1],[900,5,1,0,0]]", Js(a["placed"]), "A placed: the command (exact tile, face), then the sampled re-placement");
    Eq("[700]", Js(a["fell"]), "A fell at the first sample with its tower dead");
    Eq("[401]", Js(a["ult"]), "A ultimate: the skill's own frame, the rage drop at 403 merged into it");
    Check(!a.ContainsKey("retreat"), "A: no retreat");
    Eq(1.0, Convert.ToDouble(a["squad"]), "A squad 1");
    var b = hs[B.ToString()] as Dictionary<string, object>;
    Check(b["leader"] is bool && (bool)b["leader"], "B: squad leader");
    Eq("[]", Js(b["placed"]), "B: never placed");
    var c = hs[C.ToString()] as Dictionary<string, object>;
    Eq("[[104,6,2,0,1]]", Js(c["placed"]), "C placed by command; the sample 2 frames later is the same placement");
    Eq("[505]", Js(c["retreat"]), "C retreat: the command; the sampled leaving at 506 is that retreat, not a fall");
    Eq("[]", Js(c["fell"]), "C: no fall");
    Eq("[398]", Js(c["ult"]), "C ultimate: the command's frame (the rage drop at 403 is the same ultimate)");
    var ops = root["ops"] as List<object>;
    Check(ops != null && ops.Count == 4 && Js(ops[0]) == "[100,1000,1,11001,4,3,90]", "ops: the commands by frame, emojis left out");
    Eq("[[100,1000],[300,980]]", Js(root["boss"]), "boss HP: kept when it moved by 1 % or more");

    // placement seen before its tile is known; a sampled move; an ultimate seen only as a rage drop; a new tower between
    // two samples (it went down in between: a fall, the card's reborn state says so)
    var t2 = new TimelineBuilder();
    t2.AddSample(10, new List<HeroSample> { Hs(A, 11001, 3, -1, -1, anger: 990) });
    t2.AddSample(13, new List<HeroSample> { Hs(A, 11001, 3, 2, 2, anger: 1000) });
    t2.AddSample(16, new List<HeroSample> { Hs(A, 11001, 3, 2, 2, anger: 100) });
    t2.AddSample(300, new List<HeroSample> { Hs(A, 11001, 3, 3, 2) });
    t2.AddSample(600, new List<HeroSample> { Hs(A, 11001, 8, 1, 1, card: 2) });
    var r2 = MiniJson.Parse(t2.ToJson()) as Dictionary<string, object>;
    var a2 = (r2["heroes"] as Dictionary<string, object>)[A.ToString()] as Dictionary<string, object>;
    Eq("[[10,2,2,0,0],[300,3,2,0,0],[600,1,1,0,0]]", Js(a2["placed"]), "sampled: tile filled in later, a move, a new tower");
    Eq("[16]", Js(a2["ult"]), "sampled: rage from full to low = an ultimate");
    Eq("[600]", Js(a2["fell"]), "sampled: a new tower after an unseen death = a fall");
    Eq(0.0, Convert.ToDouble(r2["samples"]) - 5, "sampled: 5 samples");
    ArenaTimelineTests();
    TraceTests();
    TraceUploadTests();
    BattleStageTests();
    PickHeroTests();
  }

  static void PickHeroTests() {
    int s;
    Eq(7L, RFX.PickHero(true, 7, false, 5, 6, out s), "hero screen: the form's displayed hero beats the selected one"); Eq(RFX.HeroSrcForm, s, "  source: form");
    Eq(5L, RFX.PickHero(true, 0, false, 5, 6, out s), "hero screen: no displayed hero, the selected one"); Eq(RFX.HeroSrcSelected, s, "  source: selected");
    Eq(6L, RFX.PickHero(true, 0, false, 0, 6, out s), "hero screen: last voiced hero as the last resort"); Eq(RFX.HeroSrcSound, s, "  source: sound");
    Eq(0L, RFX.PickHero(true, 0, true, 5, 6, out s), "an add slot shown: no hero"); Eq(RFX.HeroSrcNone, s, "  source: none");
    Eq(5L, RFX.PickHero(false, 7, false, 5, 6, out s), "form not alive: EquipData only"); Eq(RFX.HeroSrcSelected, s, "  source: selected");
    Check(RFX.GuideTryDue(0, 5000, 3000), "hints' live tables: the first try is always due");
    Check(!RFX.GuideTryDue(1000, 3999, 3000), "hints' live tables: no retry before 3 s");
    Check(RFX.GuideTryDue(1000, 4000, 3000), "hints' live tables: retry after 3 s");
    Check(RFX.GuideTryDue(int.MaxValue - 100, int.MinValue + 3000, 3000), "hints' live tables: the tick counter wrapping round");
    Eq(0L, RFX.PickHero(false, 7, false, 0, 6, out s), "form not alive, nothing selected: unknown");
    Eq(0L, RFX.GateHero(true, false, 7), "hero form present but hidden: no hero screen");
    Eq(7L, RFX.GateHero(true, true, 7), "hero form active: the hero as before");
    Eq(5L, RFX.GateHero(false, false, 5), "no form known: EquipData only, as before");
  }

  // The arena: both sides have Hassu (uid 203400000, unit 2034) — the player (controller 1) at 2,5 by command, the
  // opponent (2) by its recorded command at its own 3,5 = the map's 11,5 (15 wide); the opponent's 2096 placed by itself
  // (no command) on the map's 12,1 face 0. Waves: controllers 100 / 101; side 2 clears round 1 first, the judge hits base
  // 4305 (side 1, max 300 000) 2 % twice, a monster hit of 1 000 between; then side 1 clears.
  static void ArenaTimelineTests() {
    var tb = new TimelineBuilder { GridW = 15 };
    uint H = 203400000, M = 209600000;
    Func<uint, uint, int, uint, int, int, int, HeroSample> Sa = (c, uid, unit, tower, x, y, face) =>
      new HeroSample { Uid = uid, C = c, Unit = unit, Squad = 1, Tower = tower, OnField = tower != 0, X = x, Y = y, Face = face, MaxAnger = 1000 };
    tb.AddSample(600, new List<HeroSample> { Sa(1, H, 2034, 0, -1, -1, 0), Sa(2, H, 2034, 0, -1, -1, 0), Sa(2, M, 2096, 0, -1, -1, 0) });
    tb.AddSample(603, new List<HeroSample> { Sa(1, H, 2034, 0, -1, -1, 0), Sa(2, H, 2034, 40, 11, 5, 270), Sa(2, M, 2096, 0, -1, -1, 0) });
    tb.AddSample(856, new List<HeroSample> { Sa(1, H, 2034, 50, 2, 5, 90), Sa(2, H, 2034, 40, 11, 5, 270), Sa(2, M, 2096, 0, -1, -1, 0) });
    tb.AddSample(930, new List<HeroSample> { Sa(1, H, 2034, 50, 2, 5, 90), Sa(2, H, 2034, 40, 11, 5, 270), Sa(2, M, 2096, 60, 12, 1, 0) });
    for (uint f = 933; f < 960; f += 3)
      tb.AddSample(f, new List<HeroSample> { Sa(1, H, 2034, 50, 2, 5, 90), Sa(2, H, 2034, 40, 11, 5, 270), Sa(2, M, 2096, 60, 12, 1, 0) });
    tb.AddCommands(new List<FrameCmd> {
      new FrameCmd { Frame = 601, Cmd = 1000, CUid = 2, Params = new[] { 2034, 3, 5, 270 } },
      new FrameCmd { Frame = 854, Cmd = 1000, CUid = 1, Params = new[] { 2034, 2, 5, 90 } } });
    Func<uint, int, int, int, int, ArenaSide> Sd = (ctl, wave, active, mon, ign) => new ArenaSide { Ctl = ctl, Wave = wave, Active = active, Monsters = mon, Ignore = ign, State = 1, MaxWave = 10 };
    Func<double, double, ArenaBase[]> Bs = (h1, h2) => new[] { new ArenaBase { Unit = 4305, Owner = 0, Hp = h1, MaxHp = 300000 }, new ArenaBase { Unit = 4306, Owner = 0, Hp = h2, MaxHp = 300000 } };
    Action<uint, ArenaSide, ArenaSide, ArenaBase[]> Ar = (f, s1, s2, b) => { var a = new ArenaSample(); a.Sides.Add(s1); a.Sides.Add(s2); a.Bases.AddRange(b); tb.AddArena(f, a); };
    Ar(30, Sd(100, 1, 1, 0, 0), Sd(101, 1, 1, 0, 0), Bs(300000, 300000));      // round 1 starts, nothing spawned yet
    Ar(33, Sd(100, 1, 1, 0, 0), Sd(101, 1, 1, 0, 0), Bs(300000, 300000));      // not cleared: no monster seen yet
    Ar(36, Sd(100, 1, 1, 3, 0), Sd(101, 1, 1, 4, 0), Bs(300000, 300000));
    Ar(200, Sd(100, 1, 0, 2, 0), Sd(101, 1, 0, 0, 0), Bs(300000, 300000));     // side 2 cleared
    Ar(215, Sd(100, 1, 0, 2, 0), Sd(101, 1, 0, 0, 0), Bs(294000, 300000));     // judge 2 %
    Ar(222, Sd(100, 1, 0, 1, 0), Sd(101, 1, 0, 0, 0), Bs(293000, 300000));     // a monster's hit
    Ar(230, Sd(100, 1, 0, 1, 0), Sd(101, 1, 0, 0, 0), Bs(287000, 300000));     // judge 2 %
    Ar(240, Sd(100, 1, 0, 0, 0), Sd(101, 1, 0, 0, 0), Bs(287000, 300000));     // side 1 cleared
    Ar(290, Sd(100, 2, 1, 5, 0), Sd(101, 2, 1, 5, 0), Bs(287000, 300000));     // round 2 for both
    tb.SetStats(new List<StatLine> { new StatLine { C = 1, Unit = 2034, Hero = H, At = 0x239876D15B0, Damage = 3538111 },
                                     new StatLine { C = 2, Unit = 2034, Hero = H, At = 0x239871EF8C0, Damage = 4427865 } });
    string json = tb.ToJson();
    var root = MiniJson.Parse(json) as Dictionary<string, object>;
    Check(root != null, "arena timeline JSON parses");
    if (root == null) return;
    Func<object, string> Jss = o => { var l = o as List<object>; var sb = new StringBuilder("["); for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Js2(l[i])); } return sb.Append(']').ToString(); };
    var hs = root["heroes"] as Dictionary<string, object>;
    Check(hs.ContainsKey(H.ToString()) && hs.ContainsKey("2:" + H) && hs.ContainsKey("2:" + M), "arena: Hassu twice — the player's by uid, the opponent's as 2:uid");
    var mine = hs[H.ToString()] as Dictionary<string, object>; var his = hs["2:" + H] as Dictionary<string, object>; var m2 = hs["2:" + M] as Dictionary<string, object>;
    Eq(1.0, Convert.ToDouble(mine["c"]), "arena: the player's hero has c 1");
    Eq("[[854,2,5,90,1]]", Jss(mine["placed"]), "arena: the player's Hassu placed once (its command), no flipping between tiles");
    Eq("[[601,3,5,270,1]]", Jss(his["placed"]), "arena: the opponent's Hassu: its own command (its local tile), the sample 11,5 is that placement");
    Eq("[[930,2,1,180,0]]", Jss(m2["placed"]), "arena: a sampled opponent tile mirrored to its side (14 − 12 = 2), face 0 -> 180");
    Eq(15.0, Convert.ToDouble(root["gridW"]), "arena: gridW kept");
    var ar = root["arena"] as Dictionary<string, object>;
    var rounds = ar["rounds"] as List<object>;
    Eq(2, rounds.Count, "arena: two rounds seen");
    var r1 = rounds[0] as Dictionary<string, object>;
    Eq(2.0, Convert.ToDouble(r1["won"]), "round 1: side 2 cleared first");
    Eq(200.0, Convert.ToDouble((r1["clear"] as Dictionary<string, object>)["2"]), "round 1: side 2 cleared at 200");
    Eq(240.0, Convert.ToDouble((r1["clear"] as Dictionary<string, object>)["1"]), "round 1: side 1 cleared at 240 (not at 33: nothing spawned yet)");
    Eq(30.0, Convert.ToDouble((r1["start"] as Dictionary<string, object>)["1"]), "round 1: started at 30");
    Eq(36.0, Convert.ToDouble((r1["first"] as Dictionary<string, object>)["1"]), "round 1: first monster at 36");
    Eq("[[215,20],[230,20]]", Jss((r1["judge"] as Dictionary<string, object>)["1"]), "round 1: the judge's two 2 % hits on side 1's base, the monster's hit left out");
    Eq(43.0, Convert.ToDouble((r1["judgeLost"] as Dictionary<string, object>)["1"]), "round 1: side 1's base lost 4.3 % while the judge worked");
    var r2 = rounds[1] as Dictionary<string, object>;
    Eq(0.0, Convert.ToDouble(r2["won"]), "round 2: nobody cleared yet");
    var bases = ar["bases"] as List<object>;
    var b1 = bases[0] as Dictionary<string, object>;
    Check(Convert.ToDouble(b1["unit"]) == 4305 && Convert.ToDouble(b1["side"]) == 1 && Jss(b1["hp"]) == "[[30,300000],[215,294000],[222,293000],[230,287000]]", "arena: base 4305 = side 1, its HP on change");
    Check((ar["raw"] as List<object>).Count >= 6, "arena: the raw wave samples kept on change");
    var sides = TimelineBuilder.StatSides(json);
    Eq(2, sides.Count, "stats: two statistic objects by side");
    Eq("{\"c\":2,\"at\":\"239871EF8C0\",\"iBaseID\":2034}", TimelineBuilder.MarkSide("{\"at\":\"239871EF8C0\",\"iBaseID\":2034}", sides), "stats: a result line marked with its side");
    Eq("{\"at\":\"1\",\"iBaseID\":2034}", TimelineBuilder.MarkSide("{\"at\":\"1\",\"iBaseID\":2034}", sides), "stats: an unknown line left as it is");
    // a pack gap inside a wave: the clear is the LAST zero before the next wave / the end of the fight
    var tg = new TimelineBuilder();
    Action<uint, ArenaSide, ArenaSide> Ag = (f, s1, s2) => { var a = new ArenaSample(); a.Sides.Add(s1); a.Sides.Add(s2); tg.AddArena(f, a); };
    Ag(10, Sd(100, 1, 1, 3, 0), Sd(101, 1, 1, 3, 0));
    Ag(50, Sd(100, 1, 0, 0, 0), Sd(101, 1, 0, 0, 0));     // gap between packs (both sides 0)
    Ag(80, Sd(100, 1, 0, 2, 0), Sd(101, 1, 0, 0, 0));     // side 1: the next pack; side 2 stays cleared
    Ag(120, Sd(100, 1, 0, 0, 0), Sd(101, 1, 0, 0, 0));    // side 1 really cleared
    Ag(200, Sd(100, 2, 1, 4, 0), Sd(101, 2, 1, 4, 0));    // round 2 starts
    Ag(230, Sd(100, 2, 0, 0, 0), Sd(101, 2, 0, 2, 0));    // side 1 clears round 2; side 2 not
    Ag(260, Sd(100, 2, 0, 0, 0), Sd(101, 2, 0, 0, 0));    // side 2 clears at the very end (fight ends mid-wave 2)
    var rg = ((MiniJson.Parse(tg.ToJson()) as Dictionary<string, object>)["arena"] as Dictionary<string, object>)["rounds"] as List<object>;
    var g1 = (rg[0] as Dictionary<string, object>)["clear"] as Dictionary<string, object>;
    var g2 = (rg[1] as Dictionary<string, object>)["clear"] as Dictionary<string, object>;
    Eq(120.0, Convert.ToDouble(g1["1"]), "clear: a pack gap inside the wave — side 1 clears at the last zero (120, not 50)");
    Eq(50.0, Convert.ToDouble(g1["2"]), "clear: a normal wave — side 2 unchanged (50)");
    Eq(230.0, Convert.ToDouble(g2["1"]), "clear: the fight ends mid-wave — side 1's pending clear is kept (230)");
    Eq(260.0, Convert.ToDouble(g2["2"]), "clear: side 2's clear after its extra pack (260)");
    // ToJson twice with more samples between: the pending clear is not frozen
    Ag(300, Sd(100, 2, 0, 1, 0), Sd(101, 2, 0, 0, 0));
    var rg2 = ((MiniJson.Parse(tg.ToJson()) as Dictionary<string, object>)["arena"] as Dictionary<string, object>)["rounds"] as List<object>;
    Check(!((rg2[1] as Dictionary<string, object>)["clear"] as Dictionary<string, object>).ContainsKey("1"), "clear: a new pack after a pending clear cancels it");
    // a boss fight (no controller known, or only 1): the old keys
    var t3 = new TimelineBuilder();
    t3.AddSample(5, new List<HeroSample> { Sa(1, H, 2034, 9, 4, 4, 0) });
    var r3 = MiniJson.Parse(t3.ToJson()) as Dictionary<string, object>;
    Check((r3["heroes"] as Dictionary<string, object>).ContainsKey(H.ToString()) && !r3.ContainsKey("arena") && !r3.ContainsKey("gridW"), "boss fight: heroes by uid, no arena part");
  }

  static UnitSample Us(uint uid, int unit, uint owner, double x, double z, double hp, int face = 0, double max = 1000) {
    return new UnitSample { Uid = uid, Unit = unit, Owner = owner, X = x, Y = 0, Z = z, Hp = hp, MaxHp = max, Face = face };
  }

  // The v3 trace: damage curves (dedup) and the arena's unit trace (move threshold, gone frame, caps, JSON shape).
  static void TraceTests() {
    Func<object, string> Jss = o => { var l = o as List<object>; var sb = new StringBuilder("["); for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Js2(l[i])); } return sb.Append(']').ToString(); };
    var tb = new TimelineBuilder();
    Func<long, int, int, StatLine> St = (d, h, t) => new StatLine { C = 1, Unit = 2034, Damage = d, Heal = h, Taken = t };
    tb.AddStatSample(10, new List<StatLine> { St(0, 0, 0) });
    tb.AddStatSample(15, new List<StatLine> { St(0, 0, 0) });
    tb.AddStatSample(20, new List<StatLine> { St(500, 0, 30) });
    tb.AddStatSample(25, new List<StatLine> { St(500, 0, 30) });
    tb.AddStatSample(30, new List<StatLine> { St(900, 7, 30), new StatLine { C = 2, Unit = 2034, Damage = 5 } });
    tb.AddUnitSample(10, new List<UnitSample> { Us(7, 4301, 100, 1.0, 2.0, 1000), Us(8, 2034, 1, 0, 0, 800) });
    tb.AddUnitSample(12, new List<UnitSample> { Us(7, 4301, 100, 1.03, 2.0, 1000), Us(8, 2034, 1, 0, 0, 800) });   // moved 0.03: no point
    tb.AddUnitSample(14, new List<UnitSample> { Us(7, 4301, 100, 1.2, 2.0, 1000), Us(8, 2034, 1, 0, 0, 800) });   // moved 0.2: a point
    tb.AddUnitSample(16, new List<UnitSample> { Us(7, 4301, 100, 1.2, 2.0, 500) });                                // hp changed; 8 gone
    tb.AddUnitSample(18, new List<UnitSample> { Us(7, 4301, 100, 1.21, 2.0, 500) });                               // tiny move: only the last point
    var r = MiniJson.Parse(tb.ToJson()) as Dictionary<string, object>;
    Eq(3.0, Convert.ToDouble(r["v"]), "trace: json version 3");
    var dt = r["dmgT"] as Dictionary<string, object>;
    Eq("[[10,0,0,0],[20,500,0,30],[30,900,7,30]]", Jss(dt["1:2034"]), "dmgT: points only when a value changed");
    Eq("[[30,5,0,0]]", Jss(dt["2:2034"]), "dmgT: each controller has its own curve");
    var us = r["units"] as Dictionary<string, object>;
    var u7 = us["7"] as Dictionary<string, object>;
    Check(Convert.ToDouble(u7["unit"]) == 4301 && Convert.ToDouble(u7["c"]) == 100 && Convert.ToDouble(u7["max"]) == 1000 && !u7.ContainsKey("gone"), "units: unit, owner, first max, still listed");
    Eq("[[10,100,0,200,1000,0],[14,120,0,200,1000,0],[16,120,0,200,500,0],[18,121,0,200,500,0]]", Jss(u7["pts"]), "units: moves under 0.05 skipped, hp change kept, the last sample always kept");
    var u8 = us["8"] as Dictionary<string, object>;
    Eq(16.0, Convert.ToDouble(u8["gone"]), "units: gone = the first sample it was not listed in");
    Eq("[[10,0,0,0,800,0],[14,0,0,0,800,0]]", Jss(u8["pts"]), "units: a still unit keeps its first and its last sample");
    Check(!r.ContainsKey("unitsCut"), "units: nothing cut, no unitsCut");
    // the caps
    var tc = new TimelineBuilder();
    var many = new List<UnitSample>();
    for (uint i = 1; i <= TimelineBuilder.MaxUnits + 5; i++) many.Add(Us(i, 1, 1, 0, 0, 10));
    tc.AddUnitSample(1, many);
    Eq(TimelineBuilder.MaxUnits, tc.UnitCount, "caps: 400 units kept");
    Eq(5, tc.UnitsCut, "caps: the units cut are counted");
    for (int k = 0; k < TimelineBuilder.MaxPts + 20; k++) tc.AddUnitSample((uint)(2 + k), new List<UnitSample> { Us(1, 1, 1, k, 0, 10) });
    var rc = MiniJson.Parse(tc.ToJson()) as Dictionary<string, object>;
    Eq(TimelineBuilder.MaxPts + 1, (((rc["units"] as Dictionary<string, object>)["1"] as Dictionary<string, object>)["pts"] as List<object>).Count, "caps: 4000 points and the last one");
    var cut = rc["unitsCut"] as Dictionary<string, object>;
    Check(Convert.ToDouble(cut["units"]) == 5 && Convert.ToDouble(cut["pts"]) == 20, "caps: unitsCut says what was cut");
    // the upload copy: the trace stripped, the rest as it was
    string full = "{\"battle\":1,\"timeline\":" + tb.ToJson() + ",\"after\":2}";
    var stripped = MiniJson.Parse(TimelineBuilder.StripTrace(full)) as Dictionary<string, object>;
    var stl = stripped["timeline"] as Dictionary<string, object>;
    Check(!stl.ContainsKey("dmgT") && !stl.ContainsKey("units") && stl.ContainsKey("heroes") && Convert.ToDouble(stripped["after"]) == 2, "strip: dmgT and units removed from the upload copy, the rest kept");
  }

  // The stage of a captured fight (src/BattleCapture.cs): the result screen's own, the start's, the simulation's.
  static void BattleStageTests() {
    Check(RFX.StageFromEnd("{\"end\":{\"m_StageId\":4809001,\"m_Score\":3},\"lines\":[]}") == 4809001, "stage: read from the result screen's m_StageId");
    Check(RFX.StageFromEnd("{\"end\":{\"____instanceId\":24104},\"lines\":[]}") == 0, "stage: a blank result screen names none");
    Check(RFX.StageFromEnd("{\"end\":{\"m_StageId\":0}}") == 0 && RFX.StageFromEnd("{\"lines\":[]}") == 0 && RFX.StageFromEnd("nope") == 0, "stage: 0, none and bad json give 0");
    Check(RFX.PickStage(4809001, 1001001, true, 1001001, 1) == 4809001, "stage: the result screen wins");
    Check(RFX.PickStage(0, 4809001, false, 4809001, 5) == 4809001, "stage: the start's stage is kept");
    Check(RFX.PickStage(0, 0, false, 4809001, 5) == 4809001, "stage: the start's stage without a simulation");
    Check(RFX.PickStage(0, 4809002, true, 4809001, 5) == 4809002, "stage: a missed start - the new simulation's stage wins over the old one");
    Check(RFX.PickStage(0, 4809002, false, 4809001, 30) == 4809002, "stage: an old start - the simulation's stage is used");
    Check(RFX.PickStage(0, 4809002, false, 0, 99) == 4809002, "stage: no start seen - the simulation's stage");
    Check(RFX.PickStage(0, 0, false, 4809001, 30) == 0 && RFX.PickStage(0, 0, false, 0, 0) == 0, "stage: nothing known gives 0");
  }

  // The arena fight trace upload (src/TraceUpload.cs): the payload (gzip + base64), what is skipped, the sent list, the pending files.
  static void TraceUploadTests() {
    string fight = "{\"stage\":6001631,\"at\":\"2026-10-10T11:50:00Z\",\"timeline\":{\"v\":3,\"dmgT\":{\"1:2\":[[10,5,0,0]]},\"units\":{\"7\":{\"unit\":1,\"c\":2,\"max\":9,\"pts\":[[10,100,200,0,1000,1]]}}}}";
    string skip;
    string body = TraceUpload.BuildPayload(fight, out skip);
    Check(body != null && skip == null, "trace upload: an arena v3 fight gives a payload");
    var o = MiniJson.Parse(body) as Dictionary<string, object>;
    Check(Convert.ToInt32(o["v"]) == 1 && Convert.ToInt32(o["stage"]) == 6001631 && (string)o["at"] == "2026-10-10T11:50:00Z", "trace upload: v, stage and at");
    byte[] gz = Convert.FromBase64String((string)o["data"]);
    string back;
    using (var gs = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress)) using (var sr = new StreamReader(gs, Encoding.UTF8)) back = sr.ReadToEnd();
    Eq(fight, back, "trace upload: data is the base64 of the gzipped file");
    Check(TraceUpload.BuildPayload(fight.Replace("\"v\":3", "\"v\":2"), out skip) == null && skip == "no timeline v3", "trace upload: timeline v2 is not sent");
    Check(TraceUpload.BuildPayload(fight.Replace("6001631", "1001631"), out skip) == null && skip == "not an arena, boss or Malric fight", "trace upload: another stage is not sent");
    Check(TraceUpload.BuildPayload(fight.Replace("\"stage\":6001631,", ""), out skip) == null && skip == "not an arena, boss or Malric fight", "trace upload: a fight without a stage is not sent");
    // a boss fight: by stage or by result screen; no units / arenaInfo, only the heroes' skill damage
    string boss = "{\"stage\":5002005,\"at\":\"2026-10-10T11:50:00Z\",\"screen\":\"Form_UnionWeekBossBattleEnd\",\"timeline\":{\"v\":3,\"stats\":[{\"c\":1,\"unit\":7,\"damage\":9,\"skills\":{\"101\":9}}]}}";
    Check(TraceUpload.BuildPayload(boss, out skip) != null, "trace upload: a boss fight with skill damage only gives a payload");
    Check(TraceUpload.BuildPayload(boss.Replace("5002005", "5201001").Replace("Form_UnionWeekBossBattleEnd", "Form_BattleEnd"), out skip) != null, "trace upload: a boss stage is sent");
    Check(TraceUpload.BuildPayload(boss.Replace("5002005", "4809017").Replace("Form_UnionWeekBossBattleEnd", "Form_BattleEnd"), out skip) != null, "trace upload: a Malric's halls stage is sent");
    Check(TraceUpload.IsMalrikStage(4809001) && TraceUpload.IsMalrikStage(4909045) && TraceUpload.IsMalrikStage(4819002) && !TraceUpload.IsMalrikStage(4709001) && !TraceUpload.IsMalrikStage(4909046), "trace upload: Malric's stages are 4809001-4909045");
    Check(RFX.EndFormNeverShown(0, 0, 0) && !RFX.EndFormNeverShown(4809001, 0, 0) && !RFX.EndFormNeverShown(0, 77, 0) && !RFX.EndFormNeverShown(0, 0, 900), "battle end forms: a preloaded form (no stage, uid, frames) is ignored, a shown one is not");
    Check(RFX.StageFromEnd("{\"screen\":\"Form_ExpeditionBattleEnd\",\"end\":{\"m_StageId\":4809017,\"m_FightUid\":5}}") == 4809017, "battle end forms: the Expedition screen gives its stage");
    Check(TraceUpload.EndedByRecorder(4809017) && TraceUpload.EndedByRecorder(6001631) && !TraceUpload.EndedByRecorder(5002005), "battle end: the recorder ends arena and Malric fights");
    Check(TraceUpload.BuildPayload(boss.Replace("5002005", "1500100"), out skip) != null, "trace upload: a guild boss result screen is sent");
    Check(TraceUpload.BuildPayload(boss.Replace("5002005", "1500100").Replace("Form_UnionWeekBossBattleEnd", "Form_BattleEnd"), out skip) == null, "trace upload: a plain fight is not sent");
    Check(TraceUpload.BuildPayload("{\"stage\":6002001,\"at\":\"2026-10-10T11:50:00Z\",\"timeline\":{\"v\":3,\"units\":{},\"dmgT\":{}}}", out skip) == null && skip == "no trace in the timeline", "trace upload: an empty trace is not sent");
    var rnd = new Random(5); var noise = new byte[3 * 1024 * 1024]; rnd.NextBytes(noise);
    string big = "{\"stage\":6001631,\"at\":\"2026-10-10T11:50:00Z\",\"junk\":\"" + Convert.ToBase64String(noise) + "\",\"timeline\":{\"v\":3,\"dmgT\":{\"1:2\":[[1,1,0,0]]}}}";
    Check(TraceUpload.BuildPayload(big, out skip) == null && skip != null && skip.StartsWith("too large"), "trace upload: over 2 MB gzipped is skipped");

    string dir = Path.Combine(Path.GetTempPath(), "rf-trace-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      string sentPath = Path.Combine(dir, "trace-sent.txt");
      var sent = new TraceSentList(sentPath);
      DateTime now = new DateTime(2026, 10, 10, 12, 0, 0);
      foreach (var n in new[] { "20261010-110000", "20261009-110000", "20261008-110000", "20261001-110000", "20261010-100000", "20261010-090000", "20261010-080000", "20261010-070000" })
        File.WriteAllText(Path.Combine(dir, n + ".json"), "{}");
      File.WriteAllText(Path.Combine(dir, "notes.json"), "{}");
      Directory.CreateDirectory(Path.Combine(dir, "accounts")); File.WriteAllText(Path.Combine(dir, "accounts", "20261010-120000.json"), "{}");
      var p = TraceUpload.Pending(dir, sent, now, 3, 5);
      Eq(5, TraceUpload.Pending(dir, sent, now, 3, 5).Count, "trace upload: pending is capped by max");
      Eq(TraceUpload.MaxPerRun, 8, "trace upload: 8 per run");
      Eq("20261010-110000.json", Path.GetFileName(p[0]), "trace upload: newest first");
      Check(!p.Exists(x => x.Contains("20261001") || x.Contains("notes")), "trace upload: older than 3 days and odd names are not pending");
      sent.Add("20261010-110000.json");
      Check(sent.Has("20261010-110000.json") && !sent.Has("20261009-110000.json"), "trace upload: the sent list remembers a name");
      Check(new TraceSentList(sentPath).Has("20261010-110000.json"), "trace upload: the sent list is kept in its file");
      p = TraceUpload.Pending(dir, sent, now, 3, 50);
      Check(p.Count == 6 && !p.Exists(x => x.Contains("20261010-110000")), "trace upload: a sent fight is not pending again");
    } finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
  }

  static string Js2(object o) {
    var l = o as List<object>; var sb = new StringBuilder("[");
    for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Convert.ToDouble(l[i]).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    return sb.Append(']').ToString();
  }




  // ---- the one mask of the screen looks (src/OwnMask.cs): read(mask(F + S)) is read(F) or «not known», never another definite value
  static void OwnMaskTests() {
    Console.WriteLine("Own hints in a look: masked = not known, never a click");
    const int Mk = ListTracker.Masked;
    // the mask itself
    {
      var px = new int[10 * 10]; for (int i = 0; i < px.Length; i++) px[i] = Argb(200, 200, 200);
      OwnMask.Apply(px, 100, 50, 10, 10, new[] { new[] { 104, 52, 3, 2 }, new[] { 0, 0, 5, 5 }, new[] { 108, 58, 10, 10 } });
      Eq(3 * 2 + 2 * 2, OwnMask.Count(px), "mask: a part inside the grab, one outside it, one cut at the edge");
      Check(px[2 * 10 + 4] == Mk && px[2 * 10 + 3] != Mk && px[9 * 10 + 9] == Mk, "mask: the right pixels");
      Check(!OwnMask.Covered(new int[100]) && OwnMask.Covered(null) && OwnMask.Covered(new int[0]), "covered: none masked = no, no grab = yes");
      var half = new int[100]; for (int i = 0; i < 21; i++) half[i] = Mk;
      Check(OwnMask.Covered(half) && !OwnMask.Covered(half, 0.25), "covered: more than 20 % masked");
      var none = new int[100]; for (int i = 0; i < none.Length; i++) none[i] = Argb(9, 9, 9);
      var before = (int[])none.Clone(); OwnMask.Apply(none, 0, 0, 10, 10, new List<int[]>());
      Check(before.SequenceEqual(none), "no window to mask (all hidden from captures): the grab is as it was");
    }
    // ButtonCheck: a button's blue and a mark, tri-state, for every position of our scene over it
    {
      int blue = unchecked((int)0xFF5160B3), dark = unchecked((int)0xFF49423F), gold = unchecked((int)0xFFC98F3A), white = unchecked((int)0xFFF0F0F0), red = Argb(210, 40, 40);
      Func<int, int[]> button = k => { var a = new int[24 * 14]; for (int i = 0; i < a.Length; i++) a[i] = (i % 24 < 9) ? blue : (i % 7 == 0 ? white : dark); return k == 0 ? a : Array.ConvertAll(a, v => v == blue ? gold : v); };
      int bad = 0, unk = 0, tot = 0;
      for (int variant = 0; variant < 2; variant++) {
        var f = button(variant);
        Seen3 truth = ButtonCheck.BlueLook(f);
        for (int sx = -8; sx < 28; sx += 3) for (int sy = -6; sy < 16; sy += 3) for (int sw = 2; sw <= 18; sw += 4) {
          var g = (int[])f.Clone(); OwnMask.Apply(g, 0, 0, 24, 14, new[] { new[] { sx, sy, sw, sw / 2 + 1 } });
          Seen3 r = ButtonCheck.BlueLook(g); tot++;
          if (r == Seen3.Unknown) unk++; else if (r != truth) bad++;
        }
        Check(truth == (variant == 0 ? Seen3.Yes : Seen3.No), "button look F: blue button = yes, gold one = no");
      }
      Check(bad == 0 && unk > 0 && unk < tot, "button look: " + tot + " scenes over it - never the other answer (" + bad + "), sometimes not known (" + unk + ")");
      var m = new int[100]; for (int i = 0; i < m.Length; i++) m[i] = (i < 40 ? red : dark);
      Check(ButtonCheck.Look(m, ButtonCheck.IsCloseRed, ButtonCheck.MinMarkShare) == Seen3.Yes, "mark: a red cross = yes");
      for (int i = 0; i < 30; i++) m[i] = Mk;
      Check(ButtonCheck.Look(m, ButtonCheck.IsCloseRed, ButtonCheck.MinMarkShare) == Seen3.Unknown, "mark: 30 of 40 red pixels under our scene = not known (it may or may not be there)");
      var none = new int[100]; for (int i = 0; i < none.Length; i++) none[i] = dark; for (int i = 0; i < 10; i++) none[i] = Mk;
      Check(ButtonCheck.Look(none, ButtonCheck.IsCloseRed, ButtonCheck.MinMarkShare) == Seen3.No, "mark: a few masked pixels over nothing red = no");
      Check(double.IsNaN(ButtonCheck.Share(half(), ButtonCheck.IsGold)) && !double.IsNaN(ButtonCheck.Share(new[] { gold, dark, dark, dark }, ButtonCheck.IsGold)), "share: NaN when more than 20 % is covered");
    }
    // HeroFilterLook through OwnMask.Read: the pop-up open / closed, with our scene over any part of the strips
    {
      double U = 295.875;
      Func<bool, Func<int, int, int, int, int[]>> frame = open => (gx, gy, gw, gh) => {
        var a = new int[gw * gh];
        for (int j = 0; j < gh; j++) for (int i = 0; i < gw; i++) {
          int x = gx + i, y = gy + j, c = open ? (30 << 16) | (30 << 8) | 40 : (120 << 16) | (110 << 8) | 100;
          if (open && Math.Abs(x - (int)(0.4836 * U)) <= 1 && y > 0.16 * U && y < 0.87 * U) c = (130 << 16) | (130 << 8) | 130;
          if (open && x > 0.04 * U && x < 0.04 * U + 0.2 * U && Math.Abs(y - 0.2379 * U) < 0.05 * U) c = (30 << 16) | (90 << 8) | 170;
          a[j * gw + i] = c;
        }
        return a;
      };
      Func<Func<int, int, int, int, int[]>, int[], Func<int, int, int, int, int[]>> over = (f, r) => (gx, gy, gw, gh) => { var a = f(gx, gy, gw, gh); OwnMask.Apply(a, gx, gy, gw, gh, new[] { r }); return a; };
      int bad = 0, unk = 0, yes = 0, tot = 0;
      foreach (bool open in new[] { true, false }) {
        var f = frame(open);
        Seen3 truth = OwnMask.Read(g => HeroFilterLook.Seen(g, U), f);
        Check(truth == (open ? Seen3.Yes : Seen3.No), "hero filter F: open = yes, closed = no");
        for (int sx = 0; sx < (int)U; sx += 17) for (int sy = 0; sy < (int)U; sy += 23) {
          var r = OwnMask.Read(g => HeroFilterLook.Seen(g, U), over(f, new[] { sx, sy, 40, 40 })); tot++;
          if (r == Seen3.Unknown) unk++; else if (r != truth) bad++; if (r == Seen3.Yes) yes++;
        }
      }
      Check(bad == 0 && unk > 0, "hero filter look: " + tot + " scenes over it - never the other answer (" + bad + "), not known " + unk + ", yes " + yes);
      // Look: the strip is judged on its known pixels. A 50x50 mask at the funnel (x-7, y243), clipping the strip's last rows: closed grid = no, open = yes, never the opposite
      int bad2 = 0, hit = 0, yes2 = 0, no2 = 0;
      foreach (bool open in new[] { true, false }) {
        var f = frame(open);
        for (int dx = -30; dx <= 30; dx += 6) for (int dy = -30; dy <= 30; dy += 6) {
          var r = HeroFilterLook.Look(over(f, new[] { -7 + dx, 243 + dy, 50, 50 }), U);
          if (r == Seen3.Yes) { yes2++; if (!open) bad2++; } else if (r == Seen3.No) { no2++; if (open) bad2++; }
          hit++;
        }
        var at = HeroFilterLook.Look(over(f, new[] { -7, 243, 50, 50 }), U);
        Check(at == (open ? Seen3.Yes : Seen3.No), "hero filter look: a 50x50 mask at the funnel (-7, 243), pop-up " + (open ? "open" : "closed") + " -> " + at);
      }
      Check(bad2 == 0 && yes2 > 0 && no2 > 0, "hero filter look over " + hit + " masks at the funnel: never the opposite (" + bad2 + "), yes " + yes2 + ", no " + no2);
      Check(OwnMask.ShareAtLeast(new[] { 1, 1, 1, ListTracker.Masked }, p => p == 1, 0.7) == Seen3.Yes && OwnMask.ShareAtLeast(new[] { 1, 0, 0, ListTracker.Masked }, p => p == 1, 0.7) == Seen3.No
        && OwnMask.ShareAtLeast(new[] { 1, 1, 0, ListTracker.Masked }, p => p == 1, 0.7) == Seen3.Unknown && OwnMask.ShareAtLeast(null, p => true, 0.5) == Seen3.No, "ShareAtLeast: yes / no / not known");
    }
    // the list's phase, the grid's columns and the slots: unchanged or not known
    {
      var geo = new ListGeometry(); double HH = 1058; int fw, fh; int[] img = FakeList(geo, HH, 120.0, out fw, out fh);
      var xs = new int[6];
      for (int c = 1; c <= 3; c++) { double l = (geo.ColLeft(c) - geo.StripLeft) * HH; xs[(c - 1) * 2] = (int)(l + 12); xs[(c - 1) * 2 + 1] = (int)(l + geo.CellW * HH - 12); }
      double ph0, cf0, pitchPx = geo.PitchY * HH;
      bool f0 = ListTracker.FindPhase(ListTracker.BarProfile(img, fw, fh, xs), pitchPx, geo.BarTop * HH, geo.BarBottom * HH, out ph0, out cf0);
      int cols0 = ListTracker.GridColumns(img, fw, fh);
      int bad = 0, known = 0, tot = 0;
      for (int sy = 0; sy < fh - 40; sy += 61) for (int sx = 0; sx < fw - 40; sx += 97) {
        var g = (int[])img.Clone(); OwnMask.Apply(g, 0, 0, fw, fh, new[] { new[] { sx, sy, 60, 40 } });
        double ph, cf; bool f = ListTracker.FindPhase(ListTracker.BarProfile(g, fw, fh, xs), pitchPx, geo.BarTop * HH, geo.BarBottom * HH, out ph, out cf); tot++;
        double d = Math.Abs(ph - ph0); d = Math.Min(d, pitchPx - d);
        if (!OwnMask.Covered(g)) { known++; if (!f0 || !f || d > 1.5) bad++; }
        int cols = ListTracker.GridColumns(g, fw, fh);
        if (cols != 0 && cols != cols0) bad++;
      }
      Check(f0 && bad == 0 && known > 0, "list phase / grid columns: a small scene over the strip changes neither (" + known + " of " + tot + " looks, " + bad + " off)");
      var big = (int[])img.Clone(); OwnMask.Apply(big, 0, 0, fw, fh, new[] { new[] { 0, 0, fw, fh / 2 } });
      Check(OwnMask.Covered(big) && ListTracker.GridColumns(big, fw, fh) == 0, "list covered over 20 %: the look is not known (columns 0 = keep the last)");
      // the slots: every slot is as without the scene or -1
      double cellPx = geo.CellW * HH; int sx0 = (int)(cellPx * 0.1), sx1 = (int)(cellPx * 0.9); double[] tops;
      int[] rows = FakeRows(geo, HH, 0, false, out fw, out fh, 40);
      double sph = ((6 - 0) % pitchPx + pitchPx) % pitchPx;
      int[] truth = ListTracker.SlotCells(rows, fw, fh, sx0, sx1, sph, pitchPx, cellPx, out tops);
      int sbad = 0, sunk = 0;
      for (int sy = 0; sy < fh - 50; sy += 37) for (int w2 = 20; w2 <= fw; w2 += 90) {
        var g = (int[])rows.Clone(); OwnMask.Apply(g, 0, 0, fw, fh, new[] { new[] { 0, sy, w2, 70 } });
        int[] got = ListTracker.SlotCells(g, fw, fh, sx0, sx1, sph, pitchPx, cellPx, out tops);
        for (int i = 0; i < got.Length; i++) { if (got[i] == -1 && truth[i] != -1) sunk++; else if (got[i] != truth[i]) sbad++; }
      }
      Check(sbad == 0 && sunk > 0, "slots under a scene: as without it or -1 (" + sbad + " other answers, " + sunk + " not known)");
    }
    // the unknown looks: no click while one is, a timeout ends in a report
    {
      var u = new UnknownLooks();
      Check(u.Blocking(0) == null, "unknown looks: none, nothing held back");
      u.Note("confirm button", true, 1000);
      Check(u.Blocking(990) == "confirm button" && u.Blocking(1000 - UnknownLooks.SkewMs - 1) == null && u.Overdue(990) == null, "unknown look noted by a clock a few ms ahead of the reader's is still fresh (the tick-start clock vs the fresh one)");
      Check(u.Blocking(1100) == "confirm button" && u.Blocking(1000 + UnknownLooks.StaleMs + 1) == null, "unknown look: no click while it is fresh, the flow that moved on is not held back");
      u.Note("confirm button", false, 1200);
      Check(u.Blocking(1210) == null, "a known look clears it");
      long t = 10000; string late = null;
      for (int i = 0; i < 100 && late == null; i++, t += 100) { u.Note("confirm button", true, t); if (u.Blocking(t) == null) { late = "free"; break; } late = u.Overdue(t); }
      Check(late == "confirm button" && t - 10000 >= UnknownLooks.TimeoutMs - 100 && t - 10000 <= UnknownLooks.TimeoutMs + 200, "a look unknown for the whole timeout (" + (t - 10000) + " ms) is reported, not guessed");
      Check(u.Overdue(t) == null, "reported once");
      // the confirm look: unknown in every tick for the whole timeout never lets a click through and ends in one report
      var conf = new UnknownLooks(); int clicks = 0, reports = 0;
      for (long now = 0; now < 20000; now += 100) {
        conf.Note("confirm button", true, now);                     // (the look, covered by our hint, every tick)
        if (conf.Blocking(now) == null) clicks++;                    // (what GatedSend asks before every click)
        else if (conf.Overdue(now) != null) reports++;
      }
      Check(clicks == 0 && reports == 1, "confirm-look-covered: " + clicks + " clicks in 20 s, " + reports + " report");
    }
    // the platform side: the affinity, the dimming, the channel
    {
      Check(CaptureAffinity.For(false, false, false) == 0x11u && CaptureAffinity.For(true, false, false) == 0u && CaptureAffinity.For(true, false, true) == 0x11u && CaptureAffinity.For(false, false, true) == 0x11u, "affinity: hidden, shown by the setting, hidden again while the auto click works");
      Check(CaptureAffinity.For(false, true, false) == 0u && CaptureAffinity.For(true, true, false) == 0u, "affinity: the test build's recorder keeps them visible (hints only)");
      Check(CaptureAffinity.For(false, true, true) == 0x11u && CaptureAffinity.For(true, true, true) == 0x11u, "affinity: the pilot wins over the recorder and the setting: hidden");
      Check(CaptureAffinity.Hidden(true, CaptureAffinity.For(false, true, true)) && !OwnMask.Covered(new int[100]) , "pilot working, recorder on: windows hidden (Excluded), so no mask and the look is not covered");
      {   // the decision table: (setting, recorder, pilot) x (hint window, never-captured window like the recorder's timer)
        bool all = true;
        foreach (bool st in new[] { false, true }) foreach (bool rc in new[] { false, true }) foreach (bool pl in new[] { false, true }) {
          all &= CaptureAffinity.For(st, rc, pl, true) == 0x11u;
          all &= CaptureAffinity.For(st, rc, pl, false) == CaptureAffinity.For(st, rc, pl);
        }
        Check(all, "affinity: a never-captured window (the recorder's timer) is 0x11 in every mode; the others follow the old table");
        Check(CaptureAffinity.Hidden(true, CaptureAffinity.For(true, true, false, true)) && !CaptureAffinity.Hidden(true, CaptureAffinity.For(true, false, false, false)), "affinity: the timer is Excluded (no mask) even with the recorder and the setting on");
      }
      Check(CaptureAffinity.DimOn(true, true) && !CaptureAffinity.DimOn(true, false) && !CaptureAffinity.DimOn(false, true), "dimming: only while every window is hidden from captures (the setting on = windows visible = no dimming)");
      Check(Channel.CaptureSettingAvailable(true, false) && Channel.CaptureSettingAvailable(false, true) && Channel.CaptureSettingAvailable(false, false) && Channel.CaptureSetting, "capture setting: open in every channel (release, test, streamer)");
      Check(!CaptureAffinity.Allowed(false, Channel.CaptureSetting) && CaptureAffinity.For(false, false, false) == 0x11u, "capture setting: off by default, the hints stay hidden from captures");
      Check(CaptureAffinity.For(true, false, false) == 0u && CaptureAffinity.For(true, false, true) == 0x11u && CaptureAffinity.For(true, true, true, false) == 0x11u, "capture setting: on shows the hints, the pilot (auto-click) hides them even then");
      Check(!Channel.UpdatesEnabled(true) && Channel.UpdatesEnabled(false), "streamer build: no self-update; every other build keeps it");
      Check(CaptureAffinity.Allowed(true, true) && !CaptureAffinity.Allowed(true, false) && !CaptureAffinity.Allowed(false, true), "the setting: counts only when on and offered by the channel");
      Check(CaptureAffinity.PilotNext(false, true, 0) && CaptureAffinity.PilotNext(true, false, 4999) && !CaptureAffinity.PilotNext(true, false, 5000) && !CaptureAffinity.PilotNext(false, false, 0) && CaptureAffinity.PilotNext(true, true, 9), "pilot affinity: hide at once, show again only 5 s after the last flip");
      Check(CaptureAffinity.Repaint(0x11u, 0u, true, true) && !CaptureAffinity.Repaint(0x11u, 0x11u, true, true) && !CaptureAffinity.Repaint(CaptureAffinity.Never, 0u, true, true) && !CaptureAffinity.Repaint(0x11u, 0u, false, true) && !CaptureAffinity.Repaint(0x11u, 0u, true, false), "affinity change on a shown window: the picture is presented again (the live switch works without a restart); not at the first apply, not when unchanged / hidden / nothing drawn");
      {
        bool show = false; var seen = new Dictionary<string, uint>();
        var set = new WindowSet<string>(w => seen[w] = CaptureAffinity.For(show, false, false));
        set.Add("glow"); set.Add("cancel");
        show = true; set.ApplyAll(w => true);
        bool both = seen["glow"] == 0u && seen["cancel"] == 0u;
        set.Add("coach");   // created after the switch
        bool later = seen["coach"] == 0u;
        set.Add("glow");    // the same window again: still once
        set.Remove("cancel"); show = false; set.ApplyAll(w => true);
        Check(both && later && set.ToArray().Length == 2 && seen["glow"] == 0x11u && seen["coach"] == 0x11u && seen["cancel"] == 0u, "window set: the setting reaches every existing window at once and a window added later gets the current state; a removed one is left alone");
      }
    }
  }
  static int[] half() { var a = new int[100]; for (int i = 0; i < 30; i++) a[i] = ListTracker.Masked; return a; }

  static string Sha256(string s) {
    using (var sha = SHA256.Create()) {
      var sb = new StringBuilder();
      foreach (byte b in sha.ComputeHash(new UTF8Encoding(false).GetBytes(s))) sb.Append(b.ToString("x2"));
      return sb.ToString();
    }
  }

  // Same shape as the v0.4 output: equipment[], heroes[], campRewards{}, artifacts{}, meta{}.
  static string SyntheticAccount() {
    var sb = new StringBuilder("{\n\"equipment\":[\n");
    for (int i = 1; i <= 2000; i++) {
      if (i > 1) sb.Append(",\n");
      sb.Append("{\"iItemUid\":" + i + ",\"iItemId\":" + (710100 + i % 50) + ",\"iStarLvl\":" + (i % 6) +
        ",\"vViceAttrList\":{\"[1]\":{\"iAttrId\":310,\"iValue\":1350},\"[2]\":{\"iAttrId\":305,\"iValue\":109}},\"fRate\":0.125}");
    }
    sb.Append("\n],\n\"heroes\":[\n");
    for (int i = 1; i <= 128; i++) {
      if (i > 1) sb.Append(",\n");
      sb.Append("{\"iHeroId\":" + i + ",\"iBaseId\":" + (2000 + i) + ",\"sName\":\"Герой №" + i + " \\\"тест\\\"\",\"vEquipSlot\":{\"[1]\":" + i + "}}");
    }
    sb.Append("\n],\n\"campRewards\":{\"[1]\":true},\n\"artifacts\":{\"[1]\":{\"iId\":1},\"[2]\":{\"iId\":2},\"[3]\":{\"iId\":3}}\n");
    sb.Append(",\"meta\":{\"extractor\":\"0.5\",\"gameVersion\":\"1.2.3\",\"seconds\":41,\"equipment\":2000,\"heroes\":128}\n}\n");
    return sb.ToString();
  }

  // The hero screen as the pilot sees it: a grid of cards that scrolls (stopping at its ends), the gear list over it, tabs.


  // The game's sell screen: rows of 6 items with title rows; a click toggles the item's selection and shows it as
  // m_CurrentEquipUid; a drag moves the list by gain × pointer move; the part icons filter the list (and clear the choice,
  // as the game does), «Сбросить» clears the filter.


  static void ScreenTopTests() {
    Console.WriteLine("The screen from the window table's top form, frame sources, the small client's scale (src/GameScreen.cs, src/UiButtons.cs):");
    Check(ScreenState.KindOfForm("Form_MainCity") == ScreenKind.City && ScreenState.KindOfForm("Form_BackpackIntegration") == ScreenKind.Inventory
      && ScreenState.KindOfForm("Form_EquipSubliming") == ScreenKind.Foundry && ScreenState.KindOfForm("Form_CharactorMain") == ScreenKind.HeroScreen
      && ScreenState.KindOfForm("Form_UnionBoss") == ScreenKind.Unknown, "table kind: the forms the app reads map to their screens, others to none");
    Check(ScreenState.TopKind(new[] { "Form_MainCity", "Form_BackpackIntegration" }) == ScreenKind.Inventory && ScreenState.TopKind(new[] { "Form_BackpackIntegration", "Form_EquipSubliming" }) == ScreenKind.Foundry
      && ScreenState.TopKind(new string[0]) == ScreenKind.Unknown && ScreenState.TopKind(null) == ScreenKind.Unknown, "table kind: a screen opened from another is on top");
    // lab 1.6.66.403: the workshop on top with no artifact request - read all the same (held, it got no back press: the storage run waited for good)
    var wsTop = new List<string> { "Form_BackpackIntegration", "Form_EquipArtifactIntensify" };
    Check(ScreenState.WantArtWs(false, wsTop) && ScreenState.WantArtWs(true, null) && !ScreenState.WantArtWs(false, new List<string> { "Form_BackpackIntegration" }) && !ScreenState.WantArtWs(false, null),
      "workshop read: while an artifact request is guided, or the window table shows it on top - not else");
    var wsSt = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), Screens = wsTop, ArtWs = new ArtWorkshopScreen { Open = true, Tab = 1, Uid = 7 } }, 0);
    var wsNo = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), Screens = wsTop }, 0);
    Check(wsSt.Kind == ScreenKind.ArtWorkshop && !wsSt.Held && wsNo.Held,
      "workshop read: read, the workshop takes the back press (" + wsSt.Brief + "); unread it is held and blocks it (" + wsNo.Brief + ")");
    // the log: City, then the inventory opened and its reader failing for seconds - the table names it: Inventory (details unknown, held), never Unknown
    var tr = new ScreenTracker(); long t = 0;
    var cityIn = new ScreenInputs { CityForm = true, Offers = new List<string>(), Screens = new List<string> { "Form_MainCity" } };
    Check(tr.Update(cityIn, t).Kind == ScreenKind.City, "table kind: the city");
    bool stable = true, unknown = false; ScreenState st = null;
    for (t = 100; t <= 6000; t += 100) {
      st = tr.Update(new ScreenInputs { CityForm = false, Offers = new List<string>(), Screens = new List<string> { "Form_BackpackIntegration", "Form_LeftTip" } }, t);
      if (st.Kind != ScreenKind.Inventory) stable = false;
      if (st.Kind == ScreenKind.Unknown) unknown = true;
    }
    Check(stable && !unknown && st.Partial && st.Held && st.FromTable && st.Brief == "Inventory details?", "table kind: the inventory's reader failing for 6 s - Inventory all along, its details unknown and held (" + st.Brief + ")");
    // the reader back: its details, no longer held
    var inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true };
    st = tr.Update(new ScreenInputs { Inv = inv, CityForm = false, Offers = new List<string>(), Screens = new List<string> { "Form_BackpackIntegration" } }, 6100);
    Check(st.Kind == ScreenKind.Inventory && !st.Partial && !st.Held && st.InvNormalEquip, "table kind: the reader back - the details, not held (" + st.Brief + ")");
    // a sub-read failing every other tick: the kind and its details stay (the last good reading stands in), the brief never changes
    string b0 = st.Brief; bool same = true;
    for (t = 6200; t <= 9000; t += 100) {
      var x = tr.Update(new ScreenInputs { Inv = (t / 100) % 2 == 0 ? inv : null, CityForm = false, Offers = new List<string>(), Screens = new List<string> { "Form_BackpackIntegration" } }, t);
      if (x.Brief != b0) same = false;
    }
    Check(same, "table kind: a sub-read failing every other tick changes nothing (" + b0 + ")");
    // the table not read (null): the readers alone, as before (a failing inventory read held 1.5 s, then Unknown)
    var tr2 = new ScreenTracker();
    tr2.Update(new ScreenInputs { Inv = inv, Offers = new List<string>() }, 0);
    Check(tr2.Update(new ScreenInputs { Offers = new List<string>() }, 1000).Kind == ScreenKind.Inventory && tr2.Update(new ScreenInputs { Offers = new List<string>() }, 3000).Kind == ScreenKind.Unknown,
      "table kind: no window table - the readers decide alone (held 1.5 s, then Unknown)");
    // a reader that reads its own screen open above the table's top keeps it (the table names a new form a moment later)
    var rs = new RecastScreen { SublimingOpen = true, CurType = 2, AscendParts = new int[0], AscendEquips = new long[0], SelectParts = new int[0], Equips = new long[0] };
    Check(ScreenState.Classify(new ScreenInputs { Rs = rs, Inv = inv, Screens = new List<string> { "Form_BackpackIntegration" } }, 0).Kind == ScreenKind.Foundry, "table kind: the Foundry read open over the table's inventory stays the Foundry");
    Check(ScreenState.Classify(new ScreenInputs { Inv = inv, CityForm = true, Screens = new List<string> { "Form_EquipSubliming", "Form_BackpackIntegration" } }, 0).Kind == ScreenKind.Foundry, "table kind: the table's Foundry over a reader's inventory");

    // frames: one source per button
    var fs = new FrameStick(); var live = new double[] { 111, 238, 44, 44, 8 };
    Check(fs.Pick(null, false, 526, 296, 0) == null, "frame source: nothing read yet - the layout's");
    Check(fs.Pick(live, false, 526, 296, 100) == live, "frame source: a live read - the live frame");
    bool kept = true;
    for (long ft = 200; ft <= 1600; ft += 100) if (fs.Pick(ft % 300 == 0 ? live : null, false, 526, 296, ft) != live) kept = false;
    Check(kept, "frame source: live reads missing now and then - the live frame all along, never the layout's");
    Check(fs.Pick(null, false, 526, 296, 1500 + FrameStick.HoldMs - 1) != null && fs.Pick(null, false, 526, 296, 1600 + FrameStick.HoldMs + 1) == null, "frame source: 1.5 s without a live read - the layout's");
    fs.Pick(live, false, 526, 296, 5000);
    Check(fs.Pick(null, true, 526, 296, 5100) == null && fs.Pick(null, false, 526, 296, 5200) == null, "frame source: the button gone (hidden / covered) - dropped at once");
    fs.Pick(live, false, 526, 296, 6000);
    Check(fs.Pick(null, false, 1280, 720, 6100) == null, "frame source: another window size - no old frame");
    // the small client's scale: the canvas has the client's shape, both ways give the same px a unit (0.46 on 526x296)
    double byH, k = UiIcon.CanvasScale(526, 296, 526 / (296 / 640.0), 640, out byH);
    Check(Math.Abs(k - byH) < 1e-9 && Math.Abs(k - 296 / 640.0) < 1e-9 && Math.Abs(k - Ui.Unit(526, 296) / 640) < 0.01, "small client: the canvas scale by width = by height = the unit's (" + k.ToString("0.000") + ")");
    // the live rect of the city's «Инвентарь» on 526x296 (113,240,40,40: its centre 133) and the bar's place it gives: the layout's click on it
    double U = Ui.Unit(526, 296), dx = (526 - (113 + 20)) / U;
    Check(Math.Abs(dx - 1.320) < 0.01 && Math.Abs(dx - CityBar.DefaultDX) * U > 30, "small client: the live «Инвентарь» is 1.32 U from the right (the bar search's 136 px), the default offset " + Math.Round(Math.Abs(dx - CityBar.DefaultDX) * U) + " px off");
    Check(UiIcon.Pick(new List<UiIcon.Cand> { new UiIcon.Cand { Name = "BtnHitArea", X = 116, Y = 243, W = 34, H = 34, Active = true, Depth = 1 } }, 113, 240, 40, 40) == -1, "icon: the touch area (BtnHitArea) is no picture");
  }

  static void ScreenMapTests() {
    Console.WriteLine("Screen map: every form's exit");
    int n = 0, bad = 0, never = 0; var seen = new HashSet<string>();
    foreach (var e in ScreenMap.All) {
      n++;
      if (!seen.Add(e.Name) || "FPEODWKU".IndexOf(e.Class) < 0 || !e.Name.StartsWith("Form_")) { bad++; Console.WriteLine("  bad entry " + e.Name); }
    }
    Check(n > 1000 && bad == 0, "screen map: " + n + " forms, one entry each, a known class letter");
    foreach (var sz in new[] { new[] { 1920.0, 1080.0 }, new[] { 1366.0, 705.0 }, new[] { 1280.0, 1024.0 } }) {
      double W = sz[0], H = sz[1], U = Ui.Unit(W, H);
      int fp = 0, fails = 0, dang = 0;
      foreach (var e in ScreenMap.All) {
        if (e.Class == 'F' || e.Class == 'P') {
          fp++;
          // the form alone on an unknown screen (F) / over the inventory (P): the map's verdict
          var s = e.Class == 'F'
            ? ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = e.Name, ShownKnown = "" }, 0)
            : ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, Offers = new List<string>(), TopNew = e.Name, ShownKnown = "" }, 0);
          var d = ClickGate.MapExit(s, W, H, U);
          bool ok = d.Form == e.Name && (e.Class == 'F' ? d.Move == MapMove.Back : d.Move == MapMove.Close) && d.Point != null
                    && d.Point.X >= 0 && d.Point.X <= W && d.Point.Y >= 0 && d.Point.Y <= H
                    && ScreenMap.OnExit(e.Name, d.Point.X, d.Point.Y, W, H, U) && !ScreenMap.OnNever(e.Name, d.Point.X, d.Point.Y, W, H, U);
          if (!ok) { fails++; if (fails <= 5) Console.WriteLine("  no exit: " + e.Name + " " + d); }
          if (e.NearNever.Length > 0) never++;
        } else if (e.Class == 'D') {
          dang++;
          foreach (var s in new[] {
            ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = e.Name, ShownKnown = "" }, 0),
            ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string>(), TopCity = e.Name, ShownKnown = "" }, 0),
            ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, Offers = new List<string>(), TopNew = e.Name, ShownKnown = "" }, 0) }) {
            var d = ClickGate.MapExit(s, W, H, U);
            if (d.Move != MapMove.Stop || d.Point != null) { fails++; Console.WriteLine("  dangerous not a stop: " + e.Name + " on " + s.Brief + " " + d); }
          }
        }
      }
      Check(fails == 0 && fp > 600 && dang > 5, "screen map " + W + "x" + H + ": " + fp + " full screens / pop-ups have a back arrow or × off their never-click buttons, " + dang + " dangerous forms only stop (" + fails + " fails)");
    }
    Check(never > 50, "screen map: never-click buttons near the exit are kept (" + never + " forms)");
    // the classes the gate relies on
    Eq('K', ScreenMap.ClassOf("Form_MainCity"), "screen map: the city is the app's own screen");
    Eq('D', ScreenMap.ClassOf("Form_SystemTips"), "screen map: the exit question is dangerous");
    Eq('K', ScreenMap.ClassOf("Form_Battle"), "screen map: the fight is a known screen (ShownKnown)"); Eq('W', ScreenMap.ClassOf("Form_BattleLoading"), "screen map: a loading waits");
    Eq('W', ScreenMap.ClassOf("Form_BattleEnd"), "screen map: a fight's result waits");
    // the results of a player's action (client Lua): a choice waits (the player's), a tap-to-close one is closed by its tap, never an Escape
    Eq('W', ScreenMap.ClassOf("Form_EquipRecastSuccess"), "screen map: the recast result holds the old / new choice -> wait");
    Eq('W', ScreenMap.ClassOf("Form_EquipReforgeSuccess"), "screen map: the reforge result holds a choice -> wait");
    Eq('W', ScreenMap.ClassOf("Form_ArtifactRefineSuccess2"), "screen map: the artifact refine choice -> wait");
    foreach (var sf in new[] { "Form_EquipSublimedSuccess", "Form_ArtifactBreakSuccess", "Form_ArtifactRefineSuccess", "Form_EquipEnhancedSuccess", "Form_EquipGemSuccess2", "Form_EquipGemSuccess3" })
      Check(ScreenMap.ClassOf(sf) == 'P' && ScreenMap.ExitPoint(sf, 1920, 1080, 1080) != null && ScreenMap.ExitPoint(sf, 1920, 1080, 1080).Y > 1080 * 0.9, "screen map: " + sf + " is closed by its tap strip at the bottom");
    foreach (var e in ScreenMap.All) if (ScreenMap.IsSuccessForm(e.Name)) Check(e.Class != 'E', "screen map: no Escape on the result form " + e.Name);
    Check(ScreenMap.IsSuccessForm("Form_EquipGemSuccess3") && !ScreenMap.IsSuccessForm("Form_EquipSuccessList") && !ScreenMap.IsSuccessForm("Form_EquipSubliming"), "screen map: the result forms by name");
    Eq("Form_EquipRecastSuccess", ScreenMap.SuccessFormOf("foundry"), "screen map: the recast's result form");
    Eq("Form_EquipSublimedSuccess", ScreenMap.SuccessFormOf("ascend"), "screen map: the ascension's result form");
    Eq("Form_ArtifactBreakSuccess", ScreenMap.SuccessFormOf("artPromote"), "screen map: the promotion's result form");
    {
      var sg = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopNew = "Form_EquipRecastSuccess", Rs = new RecastScreen { GemOpen = true, GemFunc = 7, GemSel = 777 } }, 0);
      Check(ScreenMap.SuccessShown(sg, "foundry") && !ScreenMap.SuccessShown(sg, "ascend"), "screen map: the recast result over the gem screen is the recast's (" + sg.Brief + ")");
      Check(!ScreenMap.SuccessShown(ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), Rs = new RecastScreen { GemOpen = true, GemFunc = 7 } }, 0), "foundry"), "screen map: no result form shown");
    }
    Eq('F', ScreenMap.ClassOf("Form_Union"), "screen map: the guild has its back arrow");
    Eq('O', ScreenMap.ClassOf("Form_LeftTip"), "screen map: the tip panel is ignored");
    foreach (var o in OfferForms.IgnoredOverlays) Eq('O', ScreenMap.ClassOf(o), "screen map: " + o + " is an overlay");
    foreach (var k in OfferForms.KnownScreens) Eq('K', ScreenMap.ClassOf(k), "screen map: " + k + " is a screen the app reads");
    Eq("Form_BuyPopup", ScreenMap.Top("Form_Union,Form_BuyPopup,Form_LeftTip"), "screen map: the dangerous form decides among several");
    Eq("Form_AccountInfo", ScreenMap.Top("Form_Union,Form_AccountInfo"), "screen map: a pop-up over a full screen is on top");
    Eq("", ScreenMap.Top("Form_Marquee"), "screen map: an overlay alone is no top form");
    // every screen names its top form; the known screens' Brief is unchanged (the fast paths), an unknown one names the map's verdict
    var inv = ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, Offers = new List<string>(), ShownKnown = "" }, 0);
    Eq("Form_BackpackIntegration", inv.Top, "top form: the inventory"); Eq("Inventory equip normal", inv.Brief, "top form: the known screen's brief as before");
    var city = ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string>(), ShownKnown = "" }, 0);
    Eq("Form_MainCity", city.Top, "top form: the city"); Eq("City", city.Brief, "top form: the city's brief as before");
    var guild = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = "Form_Union", ShownKnown = "" }, 0);
    Eq("Form_Union", guild.Top, "top form: the guild"); Eq("Unknown top(Form_Union) map(Form_Union:back)", guild.Brief, "top form: an unknown screen names the map's verdict");
    var offer = ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string> { "Form_PopUpShop" }, ShownKnown = "" }, 0);
    Eq("Form_PopUpShop", offer.Top, "top form: the offer pop-up");
    var cov = ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string>(), TopCity = "Form_AccountInfo", ShownKnown = "" }, 0);
    Check(cov.Kind == ScreenKind.City && cov.ModalOpen && cov.Top == "Form_AccountInfo", "top form: a pop-up over the city makes it modal (" + cov.Brief + ")");
    var odd = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = "Form_NotInTheGame", ShownKnown = "" }, 0);
    Eq(MapMove.None, ClickGate.MapExit(odd, 1920, 1080, 1080).Move, "screen map: a form not in the map gets no map exit");
    var sold = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = "Form_SoldierDetail", ShownKnown = "" }, 0);
    Eq(MapMove.Back, ClickGate.MapExit(sold, 1920, 1080, 1080).Move, "screen map: the demon soldier's details leave by the back arrow");
    // an E pop-up over the city: never Escape there, the pilot stops
    string ecity = null; foreach (var e in ScreenMap.All) if (e.Class == 'E') { ecity = e.Name; break; }
    var ec = ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string>(), TopCity = ecity, ShownKnown = "" }, 0);
    Eq(MapMove.Stop, ClickGate.MapExit(ec, 1920, 1080, 1080).Move, "screen map: a back-key-only pop-up over the city stops (no Escape on the city)");
  }

  static void SellHintTests() {
    Console.WriteLine("Sell hint (storage cleanup without auto click)");
    double W = 1920, H = 1009, U = Ui.Unit(W, H);
    var g = SellGeometry.Inventory(W, U, 8);
    var uids = new List<long>(); for (long i = 1; i <= 80; i++) uids.Add(1000 + i);   // 10 rows of 8
    Func<InventoryScreen> bulk = () => new InventoryScreen { Open = true, EquipTab = true, Bulk = true, ListPtr = 7, Uids = new List<long>(uids) };
    var wanted = new HashSet<long> { 1001, 1003, 1012, 1075 };   // rows 1, 1, 2 and 10
    Func<double, LocateGrid> gridAt = scrolledRows => {
      var gr = new LocateGrid(); gr.FromTop(g, g.ViewTopPx(U) + g.TopPad * U, U, 7); gr.Row1 -= scrolledRows * g.PitchY * U; return gr;
    };

    Eq(SellHintKind.OpenInventory, SellHint.Build(null, wanted, 8, null, g, W, H, U).Kind, "hint: no inventory -> open it");
    Eq(SellHintKind.OpenInventory, SellHint.Build(new InventoryScreen { Open = false }, wanted, 8, null, g, W, H, U).Kind, "hint: inventory closed -> open it");
    Eq(SellHintKind.OpenEquipTab, SellHint.Build(new InventoryScreen { Open = true, EquipTab = false }, wanted, 8, null, g, W, H, U).Kind, "hint: another tab");
    Eq(SellHintKind.PressBulk, SellHint.Build(new InventoryScreen { Open = true, EquipTab = true, Bulk = false }, wanted, 8, null, g, W, H, U).Kind, "hint: normal mode -> press «Массовая продажа»");

    var inv = bulk();
    var st = SellHint.Build(inv, wanted, 8, gridAt(0), g, W, H, U);
    Check(st.Kind == SellHintKind.Select && st.Total == 4 && st.Selected == 0 && st.Missing == 0 && st.Extra == 0, "hint: nothing selected yet: 0 of 4");
    Check(st.Frames.Count == 3 && st.Below == 1 && st.Above == 0, "hint: three list items in view framed, one below (" + st.Frames.Count + ", below " + st.Below + ")");
    Check(st.Frames.TrueForAll(f => f.Style == "todo"), "hint: all frames gold");
    var f1 = st.Frames.Find(f => f.Uid == 1003);
    Check(f1 != null && f1.X == (int)g.X(g.X0 + 2 * g.PitchX, W, U) && f1.Y == (int)(g.ViewTopPx(U) + g.TopPad * U), "hint: the frame sits on row 1, place 3");
    Check(st.Frames[0].Y <= st.Frames[st.Frames.Count - 1].Y, "hint: frames in reading order");

    inv.Selected.Add(1003);
    st = SellHint.Build(inv, wanted, 8, gridAt(0), g, W, H, U);
    Check(st.Selected == 1 && st.Frames.Find(f => f.Uid == 1003).Style == "done" && st.Frames.Find(f => f.Uid == 1001).Style == "todo", "hint: a selected item gets the checked frame (1 of 4)");

    inv.Selected.Add(1005);   // not on the list
    st = SellHint.Build(inv, wanted, 8, gridAt(0), g, W, H, U);
    Check(st.Extra == 1 && st.Frames.Find(f => f.Uid == 1005).Style == "extra" && st.Kind == SellHintKind.Select, "hint: a selected item that is not on the list: red frame");
    inv.Selected.Remove(1005);

    // scrolled down 8 rows: row 1 and 2 are above, row 10 in view
    st = SellHint.Build(inv, wanted, 8, gridAt(8), g, W, H, U);
    Check(st.Above == 2 && st.Below == 0 && st.Frames.Exists(f => f.Uid == 1075 && f.Style == "todo") && !st.Frames.Exists(f => f.Uid == 1001), "hint: scrolled down -> two list items above (the selected one is not counted), the last in view (above " + st.Above + ")");

    // an extra item out of view gets no frame and no count of rows
    var inv2 = bulk(); inv2.Selected.Add(1079);
    st = SellHint.Build(inv2, wanted, 8, gridAt(0), g, W, H, U);
    Check(st.Extra == 1 && !st.Frames.Exists(f => f.Uid == 1079) && st.Below == 1, "hint: an extra item out of view: counted, not framed");

    // the rows' place unknown
    st = SellHint.Build(inv, wanted, 8, null, g, W, H, U);
    Check(st.Kind == SellHintKind.NeedPosition && st.Frames.Count == 0 && st.Total == 4 && st.Selected == 1, "hint: rows' place unknown -> ask to scroll to the top, counts kept");
    st = SellHint.Build(inv, wanted, 8, new LocateGrid(), g, W, H, U);
    Eq(SellHintKind.NeedPosition, st.Kind, "hint: a grid without an anchor is unknown too");

    // all selected: no frames needed, the position does not matter
    var all = bulk(); foreach (var u in wanted) all.Selected.Add(u);
    st = SellHint.Build(all, wanted, 8, null, g, W, H, U);
    Check(st.Kind == SellHintKind.AllSelected && st.Selected == 4 && st.Total == 4, "hint: all selected -> press «Продать»");
    all.Selected.Add(1002);
    Eq(SellHintKind.Select, SellHint.Classify(all, wanted).Kind, "hint: all selected but a stray one too -> not done");

    // list items not in the grid (sold, hidden by the filter) are not counted in N
    var some = bulk(); some.Uids.Remove(1075); some.Uids.Remove(1012);
    st = SellHint.Classify(some, wanted);
    Check(st.Total == 2 && st.Missing == 2, "hint: items missing from the grid: N counts only the present (" + st.Total + ", missing " + st.Missing + ")");
    var none = bulk(); none.Uids.RemoveAll(u => wanted.Contains(u));
    Eq(SellHintKind.NoneHere, SellHint.Classify(none, wanted).Kind, "hint: none of the list is in the grid");
    none.Selected.Add(1002);
    Eq(SellHintKind.Select, SellHint.Classify(none, wanted).Kind, "hint: none of the list here but a stray selected -> red frame step");

    // keys: scrolling changes the full key only, not the state key (a selection read must repeat to be shown)
    var a = SellHint.Build(bulk(), wanted, 8, gridAt(0), g, W, H, U); var b = SellHint.Build(bulk(), wanted, 8, gridAt(8), g, W, H, U);
    Check(a.StateKey == b.StateKey && a.FullKey != b.FullKey, "hint: scrolling does not change the state key");
    Eq("select", a.Key, "hint: page key");

    // stable frames: sub-pixel / 2 px tracking noise does not move a frame; a real move does
    var prev = SellHint.Build(bulk(), wanted, 8, gridAt(0), g, W, H, U).Frames;
    var jit = SellHint.Build(bulk(), wanted, 8, gridAt(0), g, W, H, U).Frames;
    foreach (var fr in jit) { fr.Y += 2; fr.X -= 1; }
    SellHint.Stabilize(prev, jit, 3);
    bool same = true; for (int i = 0; i < jit.Count; i++) same &= jit[i].Y == prev[i].Y && jit[i].X == prev[i].X;
    Check(same, "hint: jitter within 3 px keeps the drawn frames");
    var moved = SellHint.Build(bulk(), wanted, 8, gridAt(0.5), g, W, H, U).Frames;
    int y0 = moved[0].Y; SellHint.Stabilize(prev, moved, 3);
    Check(moved[0].Y == y0, "hint: a real move (half a row) is followed");
    var sty = SellHint.Build(bulk(), wanted, 8, gridAt(0), g, W, H, U).Frames; sty[0].Style = "done"; int sy = sty[0].Y; sty[0].Y += 1;
    SellHint.Stabilize(prev, sty, 3);
    Check(sty[0].Y == sy + 1, "hint: a frame whose style changed is not held");

    // the click that anchors the list
    Eq(1003L, SellHint.OneChange(new HashSet<long>(), new HashSet<long> { 1003 }), "hint: one item selected -> its uid");
    Eq(1003L, SellHint.OneChange(new HashSet<long> { 1003 }, new HashSet<long>()), "hint: one item unselected -> its uid");
    Eq(0L, SellHint.OneChange(new HashSet<long> { 1 }, new HashSet<long> { 2 }), "hint: two changes -> none");
    Eq(0L, SellHint.OneChange(new HashSet<long> { 1 }, new HashSet<long> { 1 }), "hint: no change -> none");
    var cell = SellHint.Cell(bulk(), 1012, 8);
    Check(cell != null && cell[0] == 2 && cell[1] == 4 && SellHint.Cell(bulk(), 5, 8) == null, "hint: the cell of an item (row 2, place 4)");

    // the exact scroll: frames sit on the cells and move with ScrollY, by the scroll's units exactly
    var scr = new InvScroll();
    Func<double, double> row1At = y => scr.Row1(g, new RFX.ListRead { ScrollY = y, Cols = 8, RowUnits = 118 }, double.NaN, U);
    var ex0 = SellHint.BuildExact(bulk(), wanted, 8, row1At(0), g, W, H, U);
    var ex1 = SellHint.BuildExact(bulk(), wanted, 8, row1At(59), g, W, H, U);   // half a row down
    Check(ex0.Kind == SellHintKind.Select && ex0.Frames.Count == 3 && ex1.Frames.Count >= 1, "hint exact: frames from the exact read");
    double half = g.PitchY * U * 59 / 118;
    Func<SellHintStep, long, SellFrame> frameOf = (stp, u) => stp.Frames.Find(fr => fr.Uid == u);
    Check(frameOf(ex0, 1012) != null && frameOf(ex1, 1012) != null && Math.Abs((frameOf(ex0, 1012).Y - frameOf(ex1, 1012).Y) - half) <= 1.01, "hint exact: a frame moves with ScrollY exactly (half a row = half the pitch)");
    var ex2 = SellHint.BuildExact(bulk(), wanted, 8, row1At(118 * 100), g, W, H, U);   // far down: everything above the view
    Check(ex2.Frames.Count == 0 && ex2.Above > 0, "hint exact: scrolled far away -> no frames, the pill counts rows above");
    var nx = SellHint.BuildExact(bulk(), wanted, 8, double.NaN, g, W, H, U);
    Check(nx.Kind == SellHintKind.Select && nx.Frames.Count == 0 && nx.Total == 4 && nx.Selected == 0 && nx.Below == 0, "hint exact: no exact read -> no frames at all, the counts stay");
    Check(double.IsNaN(scr.Row1(g, new RFX.ListRead(), double.NaN, U)), "hint exact: an unreadable list gives NaN");

    // remaining targets: the grid rows of every item still to select, in view or not (the markers on the scroll bar)
    {
      var tr = SellHint.Build(bulk(), wanted, 8, gridAt(0), g, W, H, U);
      Check(tr.TodoRows.Count == 4 && tr.TodoRows[0] == 1 && tr.TodoRows[1] == 1 && tr.TodoRows[2] == 2 && tr.TodoRows[3] == 10, "final step: a marker row for each of the 4 targets (rows 1, 1, 2, 10), also the one below the view");
      var inv3 = bulk(); inv3.Selected.Add(1001); inv3.Selected.Add(1075);
      var tr2 = SellHint.Build(inv3, wanted, 8, gridAt(0), g, W, H, U);
      Check(tr2.TodoRows.Count == 2 && tr2.TodoRows[0] == 1 && tr2.TodoRows[1] == 2, "final step: a marked target has no marker any more");
      var r1 = g.ViewTopPx(U) + g.TopPad * U;
      Eq(10, SellHint.LeadRow(tr.TodoRows, r1, g, H, U, true, false), "scroll strip: the pill's dot is the first target row below the view");
      Eq(2, SellHint.LeadRow(tr.TodoRows, r1 - 8 * g.PitchY * U, g, H, U, false, true), "scroll strip: ... the last target row above the view when none is below");
      Eq(1, SellHint.LeadRow(tr.TodoRows, r1, g, H, U, false, false), "scroll strip: ... the first target when all are in view");
    }
    // the final step: everything marked -> «Готово: отмечено N — нажмите «Продать»» and the frame on «Продать» (never a click)
    {
      var fin = bulk(); foreach (var u in wanted) fin.Selected.Add(u);
      var stF = SellHint.BuildExact(fin, wanted, 8, row1At(0), g, W, H, U);
      Check(SellHint.IsFinal(stF) && stF.Selected == 4 && stF.Frames.Count == 0 && stF.TodoRows.Count == 0, "final step: all 4 marked -> the final step, no cell frames, no markers");
      Check(!SellHint.IsFinal(SellHint.BuildExact(bulk(), wanted, 8, row1At(0), g, W, H, U)) && !SellHint.IsFinal(null), "final step: not while a target is left");
      fin.Selected.Add(1002);
      Check(!SellHint.IsFinal(SellHint.Classify(fin, wanted)), "final step: a stray marked item is not the final step");
      Eq("Готово: отмечено 4 — нажмите «Продать»", SellHint.DoneLine("Готово: отмечено {0} — нажмите «Продать»", 4), "final step: the plate's text with the count");
      Eq("", SellHint.DoneLine("", 4), "final step: no format -> no text of its own");
      var br = SellHint.SellBtnRect(1366, 705, Ui.Unit(1366, 705));   // measured on the recording: the button at x 820.4..996.5, y 633.7..676.3
      Check(Math.Abs(br[0] - 820.4) <= 1 && Math.Abs(br[0] + br[2] - 996.5) <= 1 && Math.Abs(br[1] - 633.7) <= 1 && Math.Abs(br[1] + br[3] - 676.3) <= 1, "final step: «Продать»'s layout rect agrees with the measured button (" + string.Join(",", Array.ConvertAll(br, v => v.ToString())) + ")");
      var tgt = FrameTargets.Of("inventory.sellBtn"); var tga = FrameTargets.Of("inventory.artSellBtn");
      Check(tgt != null && tga != null && FrameTargets.Widget(tgt) != null && FrameTargets.Widget(tga) != null && Array.IndexOf(FrameTargets.OverlayKeys, "inventory.sellBtn") >= 0, "final step: «Продать» is a frame target with a live widget (both tabs)");
    }
    // nothing hides for idleness while a sell guide is shown (live 1.6.66.442: the frames blinked out with the mouse still, back on a mouse move)
    {
      Eq(LoadPlan.ActiveMs, LoadPlan.OverlayMs(true, false, false, true, true, true, false, 99999, true), "no-hide: a sell guide keeps the active tick with no input at all");
      Eq(LoadPlan.CalmMs, LoadPlan.OverlayMs(true, false, false, true, true, true, false, 99999, false), "no-hide: without a guide the idle tick stays calm (the load plan is unchanged)");
      Eq(120, LoadPlan.ReadGapMs(false, false, false, 99999, true), "no-hide: a sell guide keeps its reads at the active gap with the mouse still");
      Eq(LoadPlan.CalmMs - 10, LoadPlan.ReadGapMs(false, false, false, 99999, false), "no-hide: no guide, no input: the calm read gap, as before");
      Eq(LoadPlan.AwayMs, LoadPlan.OverlayMs(true, false, false, true, false, true, false, 99999, true), "no-hide: a game behind another window still goes to the slow tick");
      Check(SellHint.KeepFramesInHold(0) && SellHint.KeepFramesInHold(SellHint.HoldFramesMs - 1) && !SellHint.KeepFramesInHold(SellHint.HoldFramesMs) && !SellHint.KeepFramesInHold(-1), "no-hide: a moment of an unknown screen keeps the frames for " + SellHint.HoldFramesMs + " ms, a real pop-up drops them");
    }
    // the cell frames of the inventory's list (live 1.6.66.442 recordings, 1366x705, 8 columns): cells 107 px apart in x and 129 in y; the game's own gold
    // selection frame on a selected cell 88.3 x 122.7 px (the picture 88.3 wide); the cell root joined with the picture made a box of 101 x 145, so frames of
    // 105 x 149 that overlapped the neighbours by 2 px at the sides and 20 px below
    {
      const double px = 107, py = 129;
      Func<double[], double[], bool> meet = (a, b) => a[0] < b[0] + b[2] && b[0] < a[0] + a[2] && a[1] < b[1] + b[3] && b[1] < a[1] + a[3];
      var old = new double[] { 0, 0, 105, 149 };
      Check(meet(old, new double[] { 0, py, 105, 149 }) && 149 - py == 20 && px - 105 == 2, "cell frames: the old 105 x 149 frames overlapped the row below by 20 px and left 2 px at the sides (their stroke met the neighbour), as recorded");
      // c_EquipSelected: the rect's visible part is 122.7 high = 112 of 115 of the rect
      var pic = new UiCell { X = 147.3, Y = 143.4, W = 88.3, H = 88.3 };
      pic.BX = pic.X; pic.BY = pic.Y; pic.BW = pic.W; pic.BH = pic.H;
      Check(RFX.CellSelected(pic, 147.3, 141.4, 88.3, 122.7 * 115 / 112), "cell frames: the game's selection rect is usable for an inventory cell");
      var fr = UiIcon.Frame(pic.BX, pic.BY, pic.BW, pic.BH, 1366, 705);
      Check(Math.Abs(fr[2] - 92.3) < 0.1 && Math.Abs(fr[3] - 126.7) < 0.1, "cell frames: 92.3 x 126.7 px with the 2 px pad (" + fr[2].ToString("0.0") + " x " + fr[3].ToString("0.0") + ")");
      // the fallback without a selection rect: the picture's width, 122.7 / 89.4 of it high, from 2 px above
      var fb = new UiCell { X = 147.3, Y = 143.4, W = 89.4, H = 89.4 };
      RFX.InvCellFallback(fb);
      var ff = UiIcon.Frame(fb.BX, fb.BY, fb.BW, fb.BH, 1366, 705);
      foreach (var f in new[] { fr, ff }) {
        var cells = new List<double[]>();
        for (int r = 0; r < 3; r++) for (int c = 0; c < 8; c++) cells.Add(new double[] { f[0] + c * px, f[1] + r * py, f[2], f[3] });
        bool any = false; for (int i = 0; i < cells.Count; i++) for (int j = i + 1; j < cells.Count; j++) if (meet(cells[i], cells[j])) any = true;
        Check(!any && f[2] <= px - 10 && f[3] <= py - 1.5, "cell frames: " + f[2].ToString("0.0") + " x " + f[3].ToString("0.0") + " frames of a 107 x 129 grid never meet a neighbour (gaps " + (px - f[2]).ToString("0.0") + " and " + (py - f[3]).ToString("0.0") + " px)");
      }
    }

    // smooth motion: the drawn value eases, jumps for a new list, holds a small reversal within 100 ms
    {
      var sm = new FrameSmoother(); double snap = 400;
      Check(double.IsNaN(sm.Step(double.NaN, 0, snap)) && !sm.Has, "smoother: no target -> NaN, nothing kept");
      Check(sm.Step(100, 0, snap) == 100, "smoother: the first value is taken as is");
      double v = sm.Step(130, 33, snap);
      Check(v > 100 && v < 130, "smoother: a step is eased, not jumped (" + v + ")");
      double v2 = sm.Step(130, 66, snap), v3 = sm.Step(130, 99, snap);
      Check(v2 > v && v3 >= v2 && v3 > 125 && v3 <= 130, "smoother: arrives within about 50-70 ms and never overshoots (" + v3 + ")");
      Check(sm.Step(130, 132, snap) == 130, "smoother: settled exactly on the target");
      Check(sm.Step(1000, 165, snap) == 1000, "smoother: a jump beyond the snap distance is taken at once");
      // the shiver: the target alternates by 2 px around a settled value - the drawn value does not go back and forth
      sm = new FrameSmoother(); sm.Step(500, 0, snap);
      double lo = 1e9, hi = -1e9; long t = 0;
      sm.Step(502, t += 33, snap);
      for (int k = 0; k < 12; k++) { double w = sm.Step(k % 2 == 0 ? 500 : 502, t += 33, snap); lo = Math.Min(lo, w); hi = Math.Max(hi, w); }
      Check(hi - lo <= 2.01, "smoother: a 2 px alternating read does not make the frame travel (" + lo + ".." + hi + ")");
      // a reversal within 100 ms is held, the same reversal after 100 ms (a real scroll turning) is followed
      sm = new FrameSmoother(); sm.Step(0, 0, snap); sm.Step(40, 33, snap); double at = sm.Value;
      Check(sm.Step(at - 3, 50, snap) == at, "smoother: a 3 px step back 17 ms after moving forward is held");
      double back = sm.Step(at - 3, 140, snap);
      Check(back < at, "smoother: after 100 ms the reversal is followed (" + back + ")");
      sm = new FrameSmoother(); sm.Step(0, 0, snap); sm.Step(40, 33, snap); at = sm.Value;
      Check(sm.Step(at - 30, 50, snap) < at, "smoother: a big reversal (a real scroll turn) is not held");
      // steady scrolling: a stream of increasing values moves the value only forward
      sm = new FrameSmoother(); sm.Step(0, 0, snap); bool mono = true; double prevV = 0;
      for (int k = 1; k <= 30; k++) { double w = sm.Step(k * 12.0, k * 33L, snap); mono &= w >= prevV; prevV = w; }
      Check(mono && prevV > 340, "smoother: steady scrolling moves only forward and follows (" + prevV + ")");
    }
    // the form node pick: a shown copy beats an earlier dead one
    {
      var hits = new List<ulong> { 10, 20, 30 };
      Eq(20UL, RFX.PickNode(hits, n => n == 20), "pick node: the shown hit wins over an earlier dead one");
      Eq(10UL, RFX.PickNode(hits, n => false), "pick node: none shown -> the first");
      Eq(0UL, RFX.PickNode(new List<ulong>(), n => true), "pick node: no hits -> 0");
    }
    // velocity prediction: the reads show the scroll one read interval late; the drawn value leads them by the smoothed velocity
    {
      double snap = 400, vel = 0.5;   // px/ms (16.5 px per 33 ms read)
      var sm = new FrameSmoother(); double drawn = 0, truth = 0; long t = 0; sm.Step(0, 0, snap);
      for (int k = 1; k <= 5; k++) { t = 33L * k; truth = vel * t; drawn = sm.Step(vel * (t - 33), t, snap); }
      Check(Math.Abs(drawn - truth) <= 3, "predict: constant speed, after 5 reads the drawn value is within 3 px of the true one (" + drawn + " vs " + truth + ")");
      Check(Math.Abs(vel * (t - 33) - truth) > 3, "predict: the raw read of the same run lags by more than 3 px (" + (truth - vel * (t - 33)) + ")");
      // a stop: the reads stop changing, the value settles exactly on the target
      double fin = drawn; long tt = t; double last = vel * (t - 33);
      for (int k = 1; k <= 6; k++) fin = sm.Step(last, tt += 33, snap);
      Check(fin == last, "predict: a stop settles exactly on the target (" + fin + " vs " + last + ")");
      // the lead never exceeds one row pitch (snap / 4)
      sm = new FrameSmoother(); sm.Step(0, 0, 100); double fast = 0; for (int k = 1; k <= 8; k++) fast = sm.Step(200.0 * k, 33L * k, 400);
      Check(fast - 1600 <= 100.01, "predict: the lead is capped at one row pitch (" + (fast - 1600) + ")");
      // a reversal: no lead for the turning read
      sm = new FrameSmoother(); sm.Step(0, 0, snap); for (int k = 1; k <= 4; k++) sm.Step(16.5 * k, 33L * k, snap);
      double turned = sm.Step(66 - 40, 165 + 33, snap);
      Check(turned <= 66 + 0.01 && turned >= 26 - 0.01, "predict: after a reversal the value does not run on ahead (" + turned + ")");
    }
    // the picture offset is not re-learned while the list moves; a learned one changes only after stillness and > 3 px
    {
      var so = new ScrollOffset(); double sp = 110.76, raw = 116.5;
      so.Place(raw, double.NaN, sp, 705, 0);
      Check(Math.Abs(so.Place(raw, 119.5, sp, 705, 50) - raw) < 0.01 && !so.Known, "offset: not learned before 150 ms of stillness");
      Check(Math.Abs(so.Place(raw, 119.5, sp, 705, 200) - 119.5) < 0.01 && so.Known, "offset: learned once the list stood still");
      double m = so.Place(raw - 40, 130, sp, 705, 300);   // moving: the picture is ignored
      Check(Math.Abs(m - (119.5 - 40)) < 0.01, "offset: a moving list is placed by the value alone");
      double w = 0; for (int k = 0; k < 8; k++) w = so.Place(raw - 40, 130, sp, 705, 400 + k * 100);
      Check(Math.Abs(w - (119.5 - 40)) < 0.01 || !so.StillFor(1200, 500), "offset: still < 500 ms never relearns");
      for (int k = 0; k < 6; k++) w = so.Place(raw, 121.5, sp, 705, 3000 + k * 1000);   // 2 px: below 3, ignored
      Check(Math.Abs(w - 119.5) < 0.01, "offset: a 2 px disagreement is not followed");
      for (int k = 0; k < 6; k++) w = so.Place(raw, 124.5, sp, 705, 10000 + k * 1000);
      Check(Math.Abs(w - 124.5) < 0.01, "offset: a 5 px disagreement is followed after stillness");
      var invS = new InvScroll(); var gq = SellGeometry.Inventory(1920, 1009, 8);
      invS.Row1(gq, new RFX.ListRead { ScrollY = 0, Cols = 8, RowUnits = 118 }, double.NaN, 1009, 1000);
      Check(!invS.WantPhase(1050) && invS.WantPhase(1200), "inventory scroll: the picture is wanted only after the list stood still");
      invS.Row1(gq, new RFX.ListRead { ScrollY = 5, Cols = 8, RowUnits = 118 }, double.NaN, 1009, 1300);
      Check(!invS.WantPhase(1400), "inventory scroll: not while it moves");
    }
    // the plate: a ticked item that is not on the list switches it to the red warning
    {
      var stw = SellHint.Classify(new InventoryScreen { Open = true, EquipTab = true, Bulk = true, Uids = { 1, 2 }, Selected = { 2 } }, new HashSet<long> { 1 });
      Check(stw.Kind == SellHintKind.Select && stw.Extra == 1 && SellHint.Warn(stw), "plate: a stray ticked item -> the red warning");
      var stn = SellHint.Classify(new InventoryScreen { Open = true, EquipTab = true, Bulk = true, Uids = { 1, 2 }, Selected = { 1 } }, new HashSet<long> { 1 });
      Check(!SellHint.Warn(stn) && !SellHint.Warn(null), "plate: all ticks are on the list (or no step) -> the normal plate");
      var stx = SellHint.Classify(new InventoryScreen { Open = true, EquipTab = true, Bulk = true, Uids = { 1, 2 }, Selected = { 1, 2 } }, new HashSet<long> { 1 });
      Check(SellHint.Warn(stx), "plate: the warning stays while the stray item is ticked, even when the list's items are done");
      stx = SellHint.Classify(new InventoryScreen { Open = true, EquipTab = true, Bulk = true, Uids = { 1, 2 }, Selected = { 1 } }, new HashSet<long> { 1 });
      Check(!SellHint.Warn(stx) && stx.Kind == SellHintKind.AllSelected, "plate: unticked -> the warning is gone");
    }

    // when the app raises the game itself
    Check(FrontPolicy.ShouldRaise(true, true, false, true, false, 10, 0), "front: pilot starting, our window foreground -> raise");
    Check(FrontPolicy.ShouldRaise(true, true, false, false, true, 10, 0), "front: pilot starting, our window covers the game -> raise");
    Check(!FrontPolicy.ShouldRaise(true, false, false, true, true, 10, 0), "front: no pilot run -> never steal focus");
    Check(!FrontPolicy.ShouldRaise(true, true, true, true, true, 10, 0), "front: game already in front -> nothing");
    Check(!FrontPolicy.ShouldRaise(true, true, false, false, false, 10, 0), "front: another program on top, not ours -> nothing");
    Check(!FrontPolicy.ShouldRaise(false, true, false, true, true, 10, 0), "front: no game window -> nothing");
    Check(!FrontPolicy.ShouldRaise(true, true, false, true, true, 10, 20), "front: throttled");
    Check(FrontPolicy.Overlaps(0, 0, 100, 100, 50, 50, 200, 200) && !FrontPolicy.Overlaps(0, 0, 100, 100, 100, 0, 200, 100), "front: overlap rect test");
    Check(FrontPolicy.ShouldMoveSelf(true, false, true) && !FrontPolicy.ShouldMoveSelf(true, true, true) && !FrontPolicy.ShouldMoveSelf(true, false, false), "front: move our window only when visible, not minimized and overlapping");
    // a pilot waiting behind another program (Telegram over the game): the game comes forward after 3 s without the player's input, never while he is active
    Check(FrontPolicy.ShouldRaiseIdle(true, true, false, false, false, 3000, 10, 0), "front idle: the pilot waits, no input for 3 s -> raise");
    Check(!FrontPolicy.ShouldRaiseIdle(true, true, false, false, false, 2999, 10, 0), "front idle: input within 3 s -> nothing");
    Check(!FrontPolicy.ShouldRaiseIdle(true, true, false, false, true, 9000, 10, 0), "front idle: a mouse button down -> nothing");
    Check(!FrontPolicy.ShouldRaiseIdle(true, false, false, false, false, 9000, 10, 0), "front idle: no pilot waiting -> nothing");
    Check(!FrontPolicy.ShouldRaiseIdle(true, true, true, false, false, 9000, 10, 0) && !FrontPolicy.ShouldRaiseIdle(true, true, false, true, false, 9000, 10, 0), "front idle: game in front / minimized -> nothing");
    Check(!FrontPolicy.ShouldRaiseIdle(true, true, false, false, false, 9000, 10, 20), "front idle: throttled");
    {
      var ui = new UserIdle();
      Eq(0L, ui.IdleMs(1000, 1000), "user idle: the first read starts the count");
      Eq(2500L, ui.IdleMs(1000, 3500), "user idle: no new input = idle grows");
      ui.OwnStart(4000); Eq(4000L, ui.IdleMs(4010, 5000), "user idle: our own click while it is sent is not the player's");
      ui.OwnEnd(4400); Eq(5000L, ui.IdleMs(4500, 6000), "user idle: input just after our send (within the slack) is ours");
      Eq(0L, ui.IdleMs(7000, 7000), "user idle: the player's input after our send resets it");
      ui.OwnStart(8000); Eq(0L, ui.IdleMs(7900, 7900), "user idle: the player's input before our send is his");
      ui.OwnEnd(8100);
      var w = new UserIdle(); w.IdleMs(4294967000u, 4294967000u); w.OwnStart(100); w.OwnEnd(200);
      Eq(4296L, w.IdleMs(150, 4000), "user idle: the tick count wraps");
    }
    // the city bar's place: dropped when the city is built again, searched on the new city and settled before a press
    {
      var cb = new CityBar();
      Check(!cb.Note(0) && !cb.Note(0xA0) && cb.Look(true, 0) && !cb.Look(false, 10), "city bar: the first city form keeps the place, seen = ready");
      cb.InvDX = 1.6; Check(!cb.Note(0xA0) && cb.InvDX == 1.6, "city bar: the same city keeps the place found");
      Check(cb.Note(0xB0) && cb.MustSearch && cb.InvDX == CityBar.DefaultDX && cb.Settling, "city bar: a new city form drops the place, the bar is searched again");
      Check(!cb.Look(true, 1000), "city bar: before the search on the new city no press");
      cb.MustSearch = false; cb.InvDX = 1.32;
      Check(!cb.Look(true, 1000) && !cb.Look(true, 1000 + CityBar.SettleMs - 1), "city bar: found on the new city, still settling");
      cb.InvDX = 1.5; Check(!cb.Look(true, 1000 + CityBar.SettleMs + 10), "city bar: the place moved (the bar comes in): settling again");
      Check(cb.Look(true, 1000 + 2 * CityBar.SettleMs + 20) && !cb.Settling && cb.Look(true, 5000), "city bar: settled at one place: ready");
      cb.Drop(); cb.MustSearch = false;
      Check(!cb.Look(true, 6000) && !cb.Look(false, 6000 + CityBar.SettleMs + 10) && !cb.Look(true, 6000 + CityBar.SettleMs + 20), "city bar: after a drop a lost picture starts the settling over");
    }
    // a tiny game window (the owner's second PC, client 526x296): U, the bar's click point and its frame
    {
      double tW = 526, tH = 296, tU = Ui.Unit(tW, tH); var hgt = new HeroGeometry();
      double cx = tW - 1.320 * tU, cy = tH - hgt.HeroesBtnDY * tU;
      var fr = LocateFrames.CityButton(null, tW, tH, tU, 1.320, hgt.HeroesBtnDY);
      Check(Math.Abs(tU - 295.875) < 0.01 && (int)cx == 135 && (int)cy == 254, "tiny window: U " + tU + ", «Инвентарь» at " + (int)cx + "," + (int)cy + " (live 135,254)");
      Check(fr[0] <= cx && cx < fr[0] + fr[2] && fr[1] <= cy && cy < fr[1] + fr[3] && fr[0] >= 0 && fr[1] + fr[3] <= tH && fr[2] >= 20, "tiny window: the frame holds the click point inside the client (" + string.Join(",", Array.ConvertAll(fr, x => x.ToString())) + ")");
    }

    // the visibility rule for everything the overlay draws
    Check(OverlayGate.CanDraw(true, true, false, true, true), "gate: game visible and in front -> draw");
    Check(!OverlayGate.CanDraw(false, false, false, false, true), "gate: no game window -> nothing");
    Check(!OverlayGate.CanDraw(true, true, true, true, true), "gate: game minimized -> nothing");
    Check(!OverlayGate.CanDraw(true, false, false, true, true), "gate: game window hidden -> nothing");
    Check(!OverlayGate.CanDraw(true, true, false, false, true), "gate: another window in front -> nothing");
    Check(!OverlayGate.CanDraw(true, true, false, true, false), "gate: the game screen is not active -> nothing");
    Check(OverlayGate.InFront(true, false, false) && !OverlayGate.InFront(false, false, false), "gate: the game in front counts, a stranger does not");
    Check(OverlayGate.InFront(false, true, true) && !OverlayGate.InFront(false, true, false), "gate: our always-on-top window counts, our normal app window does not");
    int[] clip;
    Check(OverlayGate.Clip(new[] { -20, 10, 100, 50 }, 0, 0, 1920, 1009, out clip) && clip[0] == 0 && clip[2] == 80, "gate: a frame is clipped to the game's client rect");
    Check(!OverlayGate.Clip(new[] { 1925, 10, 100, 50 }, 0, 0, 1920, 1009, out clip), "gate: a frame outside the client rect is dropped");
  }


  // ---------------------------------------------------------------- 1.6.68: update download, hosts, backoff, 409
  /// <summary>A fake connection: serves at most <c>Limit</c> bytes and then "stands still" (the read timeout fires: IOException), like a filtered link.</summary>
  sealed class StallStream : System.IO.Stream {
    readonly byte[] data; int pos; readonly int end, limit; int served;
    public StallStream(byte[] d, int from, int to, int limit) { data = d; pos = from; end = to; this.limit = limit; }
    public override int Read(byte[] b, int o, int c) {
      if (pos >= end) return 0;
      if (served >= limit) throw new System.IO.IOException("the read timed out");
      int n = Math.Min(Math.Min(c, end - pos), limit - served);
      Array.Copy(data, pos, b, o, n); pos += n; served += n; return n;
    }
    public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long o, System.IO.SeekOrigin so) { throw new NotSupportedException(); }
    public override void SetLength(long v) { throw new NotSupportedException(); }
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
  }

  static void UpdateDownloadTests() {
    Console.WriteLine("Update download: hosts, backoff, chunks");
    // hosts: the feed's own host and scheme, or a listed mirror over https
    var site = new Uri("https://staging.example.org");
    Check(UpdateHosts.Allowed(site, new Uri("https://staging.example.org/downloads/a.exe")), "hosts: the same host and scheme");
    Check(!UpdateHosts.Allowed(site, new Uri("http://staging.example.org/a.exe")), "hosts: another scheme of the same host refused");
    Check(UpdateHosts.Allowed(site, new Uri("https://wardsage.com/downloads/a.exe")), "hosts: the listed mirror wardsage.com");
    Check(UpdateHosts.Allowed(site, new Uri("https://WARDSAGE.com/a.exe")), "hosts: the list is case-insensitive");
    Check(!UpdateHosts.Allowed(site, new Uri("http://wardsage.com/a.exe")), "hosts: the mirror only over https");
    Check(!UpdateHosts.Allowed(site, new Uri("https://evil.wardsage.com.example/a.exe")) && !UpdateHosts.Allowed(site, new Uri("https://example.com/a.exe")), "hosts: any other host refused");
    Check(UpdateHosts.Allowed(new Uri("http://localhost:3999"), new Uri("http://localhost:3999/a.exe")), "hosts: a local test site serves its own file");
    Check(UpdateHosts.Allowed(new Uri("https://wardsage.com"), new Uri("https://ru.wardsage.com/downloads/a.exe")) && UpdateHosts.Allowed(new Uri("https://ru.wardsage.com"), new Uri("https://wardsage.com/downloads/a.exe")), "hosts: ru.wardsage.com is a mirror both ways (https)");
    Check(!UpdateHosts.Allowed(site, new Uri("http://ru.wardsage.com/a.exe")) && !UpdateHosts.Allowed(site, new Uri("https://ru.wardsage.com.evil.example/a.exe")) && !UpdateHosts.Allowed(site, new Uri("https://xru.wardsage.com/a.exe")), "hosts: ru.wardsage.com only over https, look-alikes refused");
    // backoff: 1, 5, 30, 30, 30, 30 minutes, +-20 %, never above 30 minutes; six failed rounds end it
    Eq(60000, UpdateRetry.DelayMs(1, 0.5), "update backoff: first 1 min");
    Eq(300000, UpdateRetry.DelayMs(2, 0.5), "update backoff: second 5 min");
    Eq(1800000, UpdateRetry.DelayMs(3, 0.5), "update backoff: third 30 min");
    Eq(1800000, UpdateRetry.DelayMs(9, 0.5), "update backoff: stays at 30 min");
    Eq(48000, UpdateRetry.DelayMs(1, 0.0), "update backoff: jitter -20 %");
    Eq(72000, UpdateRetry.DelayMs(1, 1.0), "update backoff: jitter +20 %");
    Eq(1800000, UpdateRetry.DelayMs(4, 1.0), "update backoff: the ceiling is 30 min even with the jitter");
    Eq(60000, UpdateRetry.DelayMs(0, 0.5), "update backoff: failure 0 is the first");
    Check(!UpdateRetry.GiveUp(5) && UpdateRetry.GiveUp(6) && UpdateRetry.GiveUp(7), "update backoff: six rounds in a row, then the long timer");
    Check(UpdateRetry.GiveUpWaitMs >= 3600000, "update backoff: after giving up the next check is hours away, no 20 s loop");

    // the file: 700 000 bytes, a source that serves 100 000 bytes per connection and stands still (the logs: 66-197 KB)
    var data = new byte[700000]; var rnd = new Random(7); rnd.NextBytes(data);
    string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf-upd-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(dir);
    try {
      int opens = 0;
      Func<int, bool, RangeOpener> src = (limit, range) => (off, len) => {
        opens++;
        if (!range) return new RangeReply { Status = 200, RangeStart = 0, Total = data.Length, Body = new StallStream(data, 0, data.Length, limit) };
        int to = (int)Math.Min(data.Length, off + len);
        return new RangeReply { Status = 206, RangeStart = off, Total = data.Length, Body = new StallStream(data, (int)off, to, limit) };
      };
      string part = System.IO.Path.Combine(dir, "a.part"); opens = 0;
      var r = ChunkedDownload.Run(src(100000, true), part, data.Length, 256 * 1024, 5, null, null);
      Check(r.Complete && r.Have == data.Length && r.Stalls >= 3, "chunks: a source that stalls after 100 KB per connection still completes (stalls " + r.Stalls + ")");
      Check(opens >= 7 && opens <= 12, "chunks: in a few connections, no endless loop (" + opens + " opens)");
      Check(System.IO.File.ReadAllBytes(part).Length == data.Length && BitConverter.ToString(System.IO.File.ReadAllBytes(part)) == BitConverter.ToString(data), "chunks: the assembled file is the same bytes");
      // a source that never gives a byte: five attempts without progress, then the round ends
      opens = 0; string p2 = System.IO.Path.Combine(dir, "b.part");
      r = ChunkedDownload.Run(src(0, true), p2, data.Length, 256 * 1024, 5, null, null);
      Check(!r.Complete && r.Why == "stalled" && opens == 5, "chunks: five attempts without a byte, then stalled (" + opens + " opens, " + r.Why + ")");
      // the pause hook is called between failed attempts (the host sleeps there), with the count
      int pauses = 0; r = ChunkedDownload.Run(src(0, true), p2, data.Length, 256 * 1024, 5, n => { pauses++; }, null);
      Eq(5, pauses, "chunks: a pause after each attempt without progress");
      // progress kept between runs: what the stalled run wrote stays in the .part, the next run continues from there
      string p3 = System.IO.Path.Combine(dir, "c.part");
      int firstOnly = 0;   // the first connection gives 60 000 bytes, every later one stands still at once
      r = ChunkedDownload.Run((off, len) => { bool first = firstOnly++ == 0; int to = (int)Math.Min(data.Length, off + len); return new RangeReply { Status = 206, RangeStart = off, Total = data.Length, Body = new StallStream(data, (int)off, to, first ? 60000 : 0) }; }, p3, data.Length, 256 * 1024, 2, null, null);
      long kept = new System.IO.FileInfo(p3).Length;
      Check(!r.Complete && kept == 60000, "resume: a round that gives up keeps its bytes (" + kept + ")");
      opens = 0; r = ChunkedDownload.Run(src(1 << 30, true), p3, data.Length, 256 * 1024, 5, null, null);
      Check(r.Complete && BitConverter.ToString(System.IO.File.ReadAllBytes(p3)) == BitConverter.ToString(data) && opens == (int)Math.Ceiling((data.Length - kept) / 262144.0), "resume: the next run continues at the same offset (" + opens + " opens)");
      // a part longer than the file is not trusted
      string p4 = System.IO.Path.Combine(dir, "d.part"); System.IO.File.WriteAllBytes(p4, new byte[data.Length + 5]);
      r = ChunkedDownload.Run(src(1 << 30, true), p4, data.Length, 256 * 1024, 5, null, null);
      Check(r.Complete && System.IO.File.ReadAllBytes(p4).Length == data.Length && BitConverter.ToString(System.IO.File.ReadAllBytes(p4)) == BitConverter.ToString(data), "resume: a too long part starts over");
      // a server without Range (answers 200 with the whole file): read whole; one that stands still before the end never completes, and does not loop
      string p5 = System.IO.Path.Combine(dir, "e.part"); opens = 0;
      r = ChunkedDownload.Run(src(1 << 30, false), p5, data.Length, 256 * 1024, 5, null, null);
      Check(r.Complete && opens == 1, "no Range: the whole file in one connection (" + opens + ")");
      string p6 = System.IO.Path.Combine(dir, "f.part"); opens = 0;
      r = ChunkedDownload.Run(src(100000, false), p6, data.Length, 256 * 1024, 5, null, null);
      Check(!r.Complete && r.Why == "stalled" && opens <= 7, "no Range and a stalling link: stalled after a few tries, no loop (" + opens + ")");
      // another build on the server: the part is not continued
      string p7 = System.IO.Path.Combine(dir, "g.part");
      r = ChunkedDownload.Run((off, len) => new RangeReply { Status = 206, RangeStart = off, Total = data.Length + 1, Body = new System.IO.MemoryStream(new byte[10]) }, p7, data.Length, 256 * 1024, 5, null, null);
      Check(!r.Complete && r.Mismatch, "a changed file size on the server stops the download");
      // a wrong range start, an error status
      r = ChunkedDownload.Run((off, len) => new RangeReply { Status = 206, RangeStart = off + 1, Total = data.Length, Body = new System.IO.MemoryStream(new byte[10]) }, System.IO.Path.Combine(dir, "h.part"), data.Length, 256 * 1024, 3, null, null);
      Check(!r.Complete && r.Why == "stalled", "a reply for another range is not written");
      r = ChunkedDownload.Run((off, len) => new RangeReply { Status = 416, Body = new System.IO.MemoryStream(new byte[0]) }, System.IO.Path.Combine(dir, "i.part"), data.Length, 256 * 1024, 3, null, null);
      Check(!r.Complete && r.Why == "stalled", "an error status counts as a failed attempt");
    } finally { try { System.IO.Directory.Delete(dir, true); } catch (Exception) { } }
  }

  /// <summary>The REAL opener (HttpWebRequest, not the fake one): a local HttpListener answers 206 with Content-Range by the Range header and
  /// hangs after 20 000 bytes of its first connection. Catches what the fakes cannot: restricted headers (Headers["Range"] throws on .NET Framework).</summary>
  static void RealRangeOpenerTests() {
    Console.WriteLine("Update download: the real HttpWebRequest opener against a local server");
    var data = new byte[90000]; new Random(11).NextBytes(data);
    var listener = new System.Net.HttpListener(); int port = 0; bool started = false;
    for (int i = 0; i < 20 && !started; i++) {
      port = 20000 + new Random(Guid.NewGuid().GetHashCode()).Next(30000);
      listener.Prefixes.Clear(); listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
      try { listener.Start(); started = true; } catch (Exception) { }
    }
    Check(started, "real opener: the local listener started");
    if (!started) return;
    int conns = 0; string lastRange = null; var stop = new System.Threading.ManualResetEvent(false);
    var loop = new System.Threading.Thread(() => {
      while (listener.IsListening) {
        System.Net.HttpListenerContext ctx;
        try { ctx = listener.GetContext(); } catch (Exception) { return; }
        var c = ctx;
        new System.Threading.Thread(() => {
          try {
            int n = System.Threading.Interlocked.Increment(ref conns);
            string rg = c.Request.Headers["Range"]; lastRange = rg;
            long a = 0, b = data.Length - 1;
            if (rg != null && rg.StartsWith("bytes=")) {
              var p = rg.Substring(6).Split('-'); a = long.Parse(p[0]); b = Math.Min(long.Parse(p[1]), data.Length - 1);
              c.Response.StatusCode = 206;
              c.Response.AddHeader("Content-Range", "bytes " + a + "-" + b + "/" + data.Length);
            }
            c.Response.ContentLength64 = b - a + 1;
            int send = (int)(b - a + 1); bool hang = n == 1 && send > 20000; if (hang) send = 20000;
            c.Response.OutputStream.Write(data, (int)a, send); c.Response.OutputStream.Flush();
            if (hang) { stop.WaitOne(30000); c.Response.Abort(); } else c.Response.Close();
          } catch (Exception) { }
        }).Start();
      }
    });
    loop.IsBackground = true; loop.Start();
    string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf-real-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(dir);
    try {
      string url = "http://127.0.0.1:" + port + "/a.exe";
      // one request on its own: the Range header reaches the server as sent
      using (var rep = RangeHttp.Open(url, 1000, 500)) {
        var ms = new System.IO.MemoryStream(); var buf = new byte[4096]; int k;
        while ((k = rep.Body.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, k);
        Check(rep.Status == 206 && rep.RangeStart == 1000 && rep.Total == data.Length && ms.Length == 500, "real opener: a ranged request gives 206, Content-Range parsed, 500 bytes");
      }
      Eq("bytes=1000-1499", lastRange, "real opener: the Range header the server saw");
      // the whole download: the first connection stalls after 20 000 bytes, the idle timeout drops it, the next one continues
      conns = 0; string part = System.IO.Path.Combine(dir, "a.part");
      var res = ChunkedDownload.Run((off, len) => RangeHttp.Open(url, off, len), part, data.Length, 40000, 5, null, null);
      Check(res.Complete && res.Stalls >= 1 && BitConverter.ToString(System.IO.File.ReadAllBytes(part)) == BitConverter.ToString(data), "real opener: a stalled connection, then a complete file with the same bytes (stalls " + res.Stalls + ", conns " + conns + ")");
    } finally {
      stop.Set(); try { listener.Stop(); } catch (Exception) { }
      try { System.IO.Directory.Delete(dir, true); } catch (Exception) { }
    }
  }

  static void SiteRouteTests() {
    Console.WriteLine("SiteRoute: wardsage.com -> ru.wardsage.com by the owner's rule");
    string M = SiteRoute.Main, R = SiteRoute.Ru;
    var clock = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    var probed = new List<string>(); bool mainUp = false, ruUp = true;
    string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf-route-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(dir);
    string file = System.IO.Path.Combine(dir, "site-route.json");
    var logs = new List<string>();
    Func<SiteRoute> mk = () => { var r = new SiteRoute(null, () => clock, h => { probed.Add(h); return h == R ? ruUp : mainUp; }, file, s => logs.Add(s)); r.Run = a => a(); return r; };
    try {
      var rt = mk();
      Eq(M, rt.Resolve(M, true), "the main site stays while nothing failed");
      Eq("http://localhost:3000", rt.Resolve("http://localhost:3000", true), "localhost: no automatic choice");
      rt.NoteNoAnswer("http://localhost:3000"); rt.NoteNoAnswer("http://localhost:3000"); rt.NoteNoAnswer("http://localhost:3000");
      Eq(M, rt.Resolve(M, true), "a failure of a foreign address changes nothing");
      Check(probed.Count == 0, "no probe for a foreign address");
      Eq("https://test.wardsage.com", rt.Resolve("https://test.wardsage.com", true), "test.wardsage.com: no automatic choice");
      rt.Resolve(M, true);
      // one failure is not enough; the reports of an inactive host are ignored
      rt.NoteNoAnswer(M);
      Check(probed.Count == 0 && rt.Resolve(M, true) == M, "one failure: stays");
      rt.NoteNoAnswer(R);
      Check(probed.Count == 0, "a failure of the inactive spare is ignored");
      rt.NoteAnswer(M);   // a success does not reset the window
      clock = clock.AddMinutes(9);
      rt.NoteNoAnswer(M);
      Check(probed.Count == 1 && probed[0] == R && rt.Resolve(M, true) == R, "two failures in 10 minutes (a success between): the spare answers -> ru");
      Check(System.IO.File.Exists(file) && System.IO.File.ReadAllText(file).Contains("ru.wardsage.com"), "the state is saved");
      Check(logs.Count > 0 && logs[logs.Count - 1].Contains("switched"), "the switch is logged");
      Eq(R, mk().Resolve(M, true), "a restart keeps ru");
      Eq(M, rt.Resolve(M, false), "SiteFallback off: the settings' site");
      clock = clock.AddDays(3).AddMinutes(-1);
      Eq(R, rt.Resolve(M, true), "ru for the whole 3 days");
      clock = clock.AddMinutes(2);
      Eq(M, rt.Resolve(M, true), "after 3 days: a trial return to the main site");
      probed.Clear(); rt.NoteNoAnswer(M);
      Check(probed.Count == 1 && rt.Resolve(M, true) == R, "a trial with one failure: ru for 3 days again");
      clock = clock.AddDays(3).AddMinutes(1);
      Eq(M, rt.Resolve(M, true), "the second trial");
      clock = clock.AddMinutes(11);
      Eq(M, rt.Resolve(M, true), "the trial passed: the main site stays");
      probed.Clear(); rt.NoteNoAnswer(M);
      Check(probed.Count == 0, "after the trial one failure is not enough again");
      ruUp = false; clock = clock.AddMinutes(1); rt.NoteNoAnswer(M);
      Check(probed.Count == 1 && rt.Resolve(M, true) == M, "ru does not answer: stays on the main site");
      rt.NoteNoAnswer(M); Check(probed.Count == 1, "no second probe within 5 minutes");
      clock = clock.AddMinutes(5); ruUp = true; rt.NoteNoAnswer(M);
      Eq(R, rt.Resolve(M, true), "after the pause the probe goes again and ru takes over");
      probed.Clear(); mainUp = true; clock = clock.AddMinutes(30);
      rt.NoteNoAnswer(R); Check(probed.Count == 0, "insurance: one failure of ru: stays");
      rt.NoteNoAnswer(R);
      Check(probed.Count == 1 && probed[0] == M && rt.Resolve(M, true) == M, "insurance: two failures of ru and the main site answers -> back at once");
      mainUp = false; ruUp = true; clock = clock.AddMinutes(30); rt.NoteNoAnswer(M); rt.NoteNoAnswer(M);
      Eq(R, rt.Resolve(M, true), "back on ru");
      clock = clock.AddMinutes(30); rt.NoteNoAnswer(R); rt.NoteNoAnswer(R);
      Eq(R, rt.Resolve(M, true), "insurance: the main site does not answer: ru stays");
      rt.Reset();
      Check(!System.IO.File.Exists(file) && rt.Resolve(M, true) == M, "Reset forgets the state");
      ruUp = true; mainUp = true; clock = clock.AddMinutes(30); var rt2 = mk(); probed.Clear();
      rt2.Resolve(R, true);
      rt2.NoteNoAnswer(R); rt2.NoteNoAnswer(R);
      Eq(M, rt2.Resolve(R, true), "the settings' site ru, no answer twice: the main site");
      Check(rt2.IsGroupHost("ru.wardsage.com") && rt2.IsGroupHost("wardsage.com") && !rt2.IsGroupHost("evil.example"), "the hosts of the group");
      System.IO.File.WriteAllText(file, "{\"active\":\"https://evil.example\",\"until\":\"2999-01-01T00:00:00Z\",\"strikes\":1}");
      Eq(M, mk().Resolve(M, true), "a foreign host in site-route.json is ignored");
    } finally { try { System.IO.Directory.Delete(dir, true); } catch (Exception) { } }
  }

  sealed class RouteServer {
    public System.Net.HttpListener L = new System.Net.HttpListener(); public int Port;
    public readonly List<string> Hits = new List<string>(); public readonly List<string> Auth = new List<string>();
    public Action<System.Net.HttpListenerContext> Handler;
    public System.Threading.ManualResetEvent Stop = new System.Threading.ManualResetEvent(false);
    public string Url { get { return "http://127.0.0.1:" + Port; } }
    public bool Start(Action<System.Net.HttpListenerContext> h) {
      Handler = h;
      for (int i = 0; i < 30; i++) {
        Port = 20000 + new Random(Guid.NewGuid().GetHashCode()).Next(30000);
        L.Prefixes.Clear(); L.Prefixes.Add(Url + "/");
        try { L.Start(); break; } catch (Exception) { }
      }
      if (!L.IsListening) return false;
      var t = new System.Threading.Thread(() => {
        while (L.IsListening) {
          System.Net.HttpListenerContext ctx;
          try { ctx = L.GetContext(); } catch (Exception) { return; }
          var c = ctx;
          lock (Hits) { Hits.Add(c.Request.HttpMethod + " " + c.Request.Url.AbsolutePath); Auth.Add(c.Request.Headers["Authorization"]); }
          new System.Threading.Thread(() => { try { Handler(c); } catch (Exception) { } }).Start();
        }
      });
      t.IsBackground = true; t.Start();
      return true;
    }
    public int Count(string prefix) { lock (Hits) { int n = 0; foreach (var h in Hits) if (h.StartsWith(prefix)) n++; return n; } }
    public void Close() { Stop.Set(); try { L.Stop(); } catch (Exception) { } }
    public static void Json(System.Net.HttpListenerContext c, int code, string body) {
      byte[] b = System.Text.Encoding.UTF8.GetBytes(body); c.Response.StatusCode = code; c.Response.ContentType = "application/json";
      c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
    }
  }

  static void SiteRouteNetTests() {
    Console.WriteLine("SiteRoute: two real local servers (HttpListener), the clients' own timeouts and statuses");
    var a = new RouteServer(); var b = new RouteServer();
    int oldTimeout = SyncClient.TimeoutMs;
    try {
      bool both = a.Start(c => { a.Stop.WaitOne(30000); try { c.Response.Abort(); } catch (Exception) { } }) && b.Start(c => {
        if (c.Request.Url.AbsolutePath == "/api/extractor/app-config") RouteServer.Json(c, 200, "{\"readEnabled\":true,\"features\":{}}");
        else RouteServer.Json(c, 200, "{\"plans\":[]}");
      });
      Check(both, "route net: both local listeners started");
      if (!both) return;
      string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf-routenet-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(dir);
      try {
        string A = a.Url, B = b.Url;
        var rt = new SiteRoute(new[] { A, B }, () => DateTime.UtcNow, null, System.IO.Path.Combine(dir, "site-route.json"), s => { });
        rt.Run = act => act();
        SyncClient.TimeoutMs = 2000;
        Eq(A, rt.Resolve(A, true), "route net: the preferred server is used first");
        // failure 1: the main server takes the connection and says nothing (8 s timeout of RemoteSwitches.Fetch: reached == false)
        bool reached; var sw = RemoteSwitches.Fetch(rt.Resolve(A, true), out reached);
        Check(sw == null && !reached, "route net: Fetch against a silent server: reached == false");
        Check(RemoteSwitches.LastNoAnswer, "route net: Fetch says no answer"); rt.Note(A, RemoteSwitches.LastNoAnswer);
        Eq(A, rt.Resolve(A, true), "route net: one failure: still the preferred server");
        // failure 2: the main server reads 16 KB of the sync body and then says nothing (what the Contabo block does): Timeout
        a.Handler = c => { var buf = new byte[16384]; int got = 0, k; try { while (got < 16384 && (k = c.Request.InputStream.Read(buf, got, 16384 - got)) > 0) got += k; } catch (Exception) { } a.Stop.WaitOne(20000); try { c.Response.Abort(); } catch (Exception) { } };
        var rnd = new Random(5); var junk = new byte[30000]; rnd.NextBytes(junk);
        string json = "{\"pad\":\"" + Convert.ToBase64String(junk) + "\"}";
        var sr = SyncClient.Send(rt.Resolve(A, true), "rf_testcode", json);
        Eq(SyncStatus.Timeout, sr.Status, "route net: /api/sync cut after 16 KB: Timeout");
        Check(SiteRoute.IsNoAnswer(sr.Status), "route net: Timeout counts as no answer");
        rt.Note(A, SiteRoute.IsNoAnswer(sr.Status));
        string site = rt.Resolve(A, true);
        Eq(B, site, "route net: after the second failure the route goes to the spare server");
        Check(b.Count("GET /api/extractor/app-config") == 1 && b.Auth[0] == null, "route net: the probe is one GET of app-config without a token");
        var pl = PlansClient.List(site, "rf_plancode", "ru");
        Check(pl.HttpCode == 200 && b.Count("GET /api/extractor/plans") == 1, "route net: PlansClient.List reached the second server");
        string seen = null; lock (b.Hits) { for (int i = 0; i < b.Hits.Count; i++) if (b.Hits[i].StartsWith("GET /api/extractor/plans")) seen = b.Auth[i]; }
        Eq("Bearer rf_plancode", seen, "route net: the second server got the Authorization header");
        Check(!SiteRoute.IsNoAnswer(pl.Status), "route net: an answered list is no failure");
        Check(SiteRoute.IsNoAnswer(SyncStatus.Unreachable) && !SiteRoute.IsNoAnswer(SyncStatus.InvalidToken) && !SiteRoute.IsNoAnswer(SyncStatus.RateLimited) && !SiteRoute.IsNoAnswer(SyncStatus.ServerError)
          && !SiteRoute.IsNoAnswer(PlansStatus.ServerError) && !SiteRoute.IsNoAnswer(PlansStatus.NotFound) && SiteRoute.IsNoAnswer(PlansStatus.Unreachable), "route net: only no-answer statuses count");

        // a main server that answers 401 / 429 / 503 / 404 / 409 / 422 does not switch, however often
        a.Close();
        var a2 = new RouteServer(); int code = 401;
        Check(a2.Start(c => RouteServer.Json(c, code, "{\"ok\":false}")), "route net: a second main listener started");
        string A2 = a2.Url;
        var rt2 = new SiteRoute(new[] { A2, B }, () => DateTime.UtcNow, null, null, s => { });
        rt2.Run = act => act();
        int probesBefore = b.Count("GET /api/extractor/app-config");
        foreach (int cd in new[] { 401, 404, 409, 422, 429, 503, 500 }) {
          code = cd;
          for (int i = 0; i < 3; i++) {
            var r1 = SyncClient.Send(rt2.Resolve(A2, true), "rf_x", "{}"); rt2.Note(A2, SiteRoute.IsNoAnswer(r1.Status));
            var r2 = PlansClient.List(rt2.Resolve(A2, true), "rf_x", "ru"); rt2.Note(A2, SiteRoute.IsNoAnswer(r2.Status));
            bool rc; RemoteSwitches.Fetch(rt2.Resolve(A2, true), out rc); Check(!RemoteSwitches.LastNoAnswer, "route net: Fetch: HTTP " + cd + " is an answer"); rt2.Note(A2, RemoteSwitches.LastNoAnswer);
          }
        }
        Eq(A2, rt2.Resolve(A2, true), "route net: 401, 404, 409, 422, 429, 5xx answers never switch");
        Eq(probesBefore, b.Count("GET /api/extractor/app-config"), "route net: and nothing is probed for them");
        a2.Close();
      } finally { try { System.IO.Directory.Delete(dir, true); } catch (Exception) { } }
    } finally { SyncClient.TimeoutMs = oldTimeout; a.Close(); b.Close(); }
  }

  static void Backoff409Tests() {
    Console.WriteLine("409 is a refusal with a pause (every client but /api/sync)");
    Check(NetBackoff.IsFailure(409) && NetBackoff.IsFailure(429) && NetBackoff.IsFailure(503) && NetBackoff.IsFailure(0), "409, 429, 5xx and no answer are failures");
    Check(!NetBackoff.IsFailure(200) && !NetBackoff.IsFailure(401) && !NetBackoff.IsFailure(404) && !NetBackoff.IsFailure(422), "other answers are not backed off");
    var bo = new NetBackoff(); var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    Check(bo.Note(409, -1, now, 0.5) && bo.Blocked(now) && bo.Blocked(now.AddSeconds(4)) && !bo.Blocked(now.AddSeconds(6)), "a 409 blocks the next call: 5 s, not a poll every 20 s of the timer");
    bo.Note(409, -1, now.AddSeconds(6), 0.5);
    Check(bo.Failures == 2 && bo.Blocked(now.AddSeconds(6).AddSeconds(9)) && !bo.Blocked(now.AddSeconds(6).AddSeconds(11)), "the second 409 in a row waits longer (10 s)");
    for (int i = 0; i < 12; i++) bo.Note(409, -1, now, 0.5);
    Check(bo.Until <= now.AddMinutes(5).AddSeconds(1), "the wait stops growing at 5 minutes");
    Check(!bo.Note(200, -1, now.AddMinutes(10), 0.5) && !bo.Blocked(now.AddMinutes(10)) && bo.Failures == 0, "one good answer resets it");
    // the arena / guild watches ask Blocked() before reading and sending: after a 409 the unchanged payload is not sent again until the wait is over
    bo.Note(409, -1, now, 0.5);
    Check(bo.Blocked(now.AddSeconds(1)), "arena / guild: nothing is sent while the 409 pause runs");
    // /api/sync keeps its own rule: 409 account_forgotten is SyncStatus.Forgotten (no retries), any other 409 is Unexpected
    Eq(SyncStatus.Forgotten, SyncClient.Interpret(409, "{\"ok\":false,\"error\":\"account_forgotten\"}", null, null, "https://x.app").Status, "sync: 409 account_forgotten stays the special case");
    Eq(SyncStatus.Unexpected, SyncClient.Interpret(409, "{\"ok\":false,\"error\":\"conflict\"}", null, null, "https://x.app").Status, "sync: another 409 is an unexpected answer");
  }

  static void FightPlanTests() {
    Console.WriteLine("Fight plans");
    string body = "{\"ok\":true,\"plans\":[{\"v\":1,\"boss\":\"guild-7\",\"stage\":5001007,\"name\":{\"ru\":\"Кошмар\",\"en\":\"Nightmare\"},"
      + "\"field\":{\"w\":3,\"h\":2,\"t\":[32,32,2,2,36,36],\"boss\":[1,1],\"half\":[0,0]},"
      + "\"steps\":[{\"u\":200,\"id\":2380,\"n\":{\"ru\":\"Ригар\",\"en\":\"Rigar\"},\"sq\":1,\"why\":\"leader\",\"x\":0,\"y\":0,\"dir\":1},"
      + "{\"u\":300,\"id\":2147,\"n\":{\"ru\":\"Сунь Укун\",\"en\":\"Sun\"},\"sq\":1,\"why\":\"dmg\",\"x\":2,\"y\":1,\"dir\":3},"
      + "{\"u\":100,\"id\":2001,\"n\":{\"ru\":\"Мика\",\"en\":\"Mika\"},\"sq\":1,\"why\":\"bench\",\"x\":null,\"y\":null,\"dir\":null}],"
      + "\"hold\":10,\"holdU\":[300],\"pts\":8726},"
      + "{\"boss\":\"bad\",\"stage\":0,\"steps\":[]}]}";
    var r = FightPlanClient.Interpret(200, body);
    Check(r.Status == PlansStatus.Ok && r.Plans.Count == 1, "one good plan, the malformed one skipped");
    var p = r.Plans[0];
    Check(p.Stage == 5001007 && p.W == 3 && p.H == 2 && p.Cell(2, 1) == 36 && p.BossX == 1, "field and stage");
    Check(p.Steps.Count == 3 && p.Steps[0].X == 0 && p.Steps[0].Dir == 1 && p.Steps[2].Bench && p.Steps[2].X == -1, "steps with tiles, reserve without");
    Check(p.Hold == 10 && p.HoldU.Contains(300) && p.Pts == 8726, "ultimate advice");
    Eq(PlansStatus.InvalidToken, FightPlanClient.Interpret(401, "{}").Status, "401");
    var feedOk = "[{\"v\":132,\"date\":\"2026-10-11\",\"items\":[{\"text\":\"x\",\"kind\":\"site\"}]}]";
    Check(NewsClient.Interpret(200, feedOk) == feedOk && NewsClient.Interpret(500, feedOk) == null && NewsClient.Interpret(200, "{}") == null
      && NewsClient.Interpret(200, "[]") == null && NewsClient.Interpret(200, "[{\"v\":1}]") == null && NewsClient.Interpret(200, "<html>") == null, "news feed parsing");
    var unreadOk = "{\"ok\":true,\"total\":2,\"topics\":[],\"newTopicUrl\":\"https://x/community/new?kind=bug&area=app\"}";
    Check(NewsClient.InterpretUnread(200, unreadOk) == unreadOk && NewsClient.InterpretUnread(401, "{}").Contains("invalid_token")
      && NewsClient.InterpretUnread(500, unreadOk).Contains("\"error\":\"error\"") && NewsClient.InterpretUnread(200, "<html>").Contains("\"ok\":false")
      && NewsClient.Unread("https://x", "").Contains("no_code"), "forum unread parsing");
    Check(FightPlanClient.For(r.Plans, 5001007, new HashSet<long> { 200 }) == p && FightPlanClient.For(r.Plans, 1, null) == null, "plan by stage");

    FightStep next, wrong; int wx, wy;
    FightPlanClient.State(p, new List<HeroSample>(), out next, out wrong, out wx, out wy);
    Check(next == p.Steps[0] && wrong == null, "nothing placed: step 1");
    var hs = new List<HeroSample> { new HeroSample { Uid = 200, OnField = true, X = 0, Y = 0 } };
    FightPlanClient.State(p, hs, out next, out wrong, out wx, out wy);
    Check(next == p.Steps[1], "step 1 placed: step 2");
    hs.Add(new HeroSample { Uid = 300, OnField = true, X = 1, Y = 0 });
    FightPlanClient.State(p, hs, out next, out wrong, out wx, out wy);
    Check(next == null && wrong == p.Steps[1] && wx == 1 && wy == 0, "all placed, one off its tile");
    hs[1] = new HeroSample { Uid = 300, OnField = false, CardState = 2 };
    FightPlanClient.State(p, hs, out next, out wrong, out wx, out wy);
    Check(next == p.Steps[2], "a hero fell: the reserve is next");
  }

  static PlanItem LocItem(long uid, int slot, long setId, int main, params int[] subs) {
    var it = new PlanItem { Uid = uid, Slot = slot, SetId = setId };
    if (main > 0) it.Main.Add(new SubStat { Text = "m", Stat = main });
    foreach (var x in subs) it.Subs.Add(new SubStat { Text = "s", Stat = x });
    return it;
  }




  // hint mode, city first (live 1.6.66.442: a recast started on the Foundry's Ascension tab: the first hint said to open a tab on the right, which that screen
  // does not have, and only then «go to the city»): on a screen the purpose's flow does not go on from the first hint is the back arrow
  static void CityFirstHintTests() {
    Console.WriteLine("Hint mode: city first");
    var req = new LocateRequest { Id = "r1", Purpose = "recast", Item = new PlanItem { Uid = 77, Slot = 3, SetId = 5, Quality = 6 } };
    // the Foundry on the Ascension tab, a recast request: the step of the Foundry's own list says «open the recast tab»; the first hint is the back arrow
    var rs = new RecastScreen { SublimingOpen = true, CurType = 2 };
    var st = LocateGuide.Next(req, null, rs, null, 8, 1);
    Check(st.Kind == LocateKind.OpenRecastTab, "city first: the plain step of the Foundry's Ascension tab for a recast is OpenRecastTab");
    var fo = new ScreenState { Kind = ScreenKind.Foundry, FoundryTab = 2 };
    Check(LocateGuide.CityFirstStep(st, "recast", fo) && st.Kind == LocateKind.BackToCity && st.Covered == LocateKind.OpenRecastTab && st.Key == "backToCity", "city first: ... a recast on the Foundry's Ascension tab: the first hint is BackToCity");
    Check(FrameTargets.Of("foundry.back") != null && FrameTargets.Of("hero.back") != null && FrameTargets.Of("inventory.back") != null && FrameTargets.Of("popup.exit") != null, "city first: the back arrow keys are mapped");
    // an Ascension request on that tab goes on (the Foundry is its continuation screen)
    var reqA = new LocateRequest { Id = "r2", Purpose = "ascend", Item = req.Item };
    var rsA = new RecastScreen { SublimingOpen = true, CurType = 2, AscendShown = true };
    st = LocateGuide.Next(reqA, null, rsA, null, 8, 1);
    Check(!LocateGuide.CityFirstStep(st, "ascend", fo) && st.Kind != LocateKind.BackToCity, "city first: an Ascension request on the Ascension tab is not sent back");
    // the gem screen for an Ascension request: not a continuation screen -> back first
    st = new LocateStep { Kind = LocateKind.OpenInventory };
    Check(LocateGuide.CityFirstStep(st, "ascend", new ScreenState { Kind = ScreenKind.Gem }) && st.Kind == LocateKind.BackToCity, "city first: the gem screen for an Ascension request -> back first");
    // the recast with the Foundry over the inventory's own step (the sub-state): back first
    st = new LocateStep { Kind = LocateKind.InList };
    Check(LocateGuide.CityFirstStep(st, "recast", fo) && st.Kind == LocateKind.BackToCity, "city first: the inventory's step under the Foundry -> back first");
    // the city, the hero screen, the inventory: the flow's own screens, nothing changes; no item / another account: never a back step
    foreach (var k in new[] { ScreenKind.City, ScreenKind.HeroScreen, ScreenKind.Inventory }) {
      st = new LocateStep { Kind = LocateKind.OpenInventory };
      Check(!LocateGuide.CityFirstStep(st, "recast", new ScreenState { Kind = k }) && st.Kind == LocateKind.OpenInventory, "city first: " + k + " is a continuation screen of the recast: no back step");
    }
    st = new LocateStep { Kind = LocateKind.NoItem }; Check(!LocateGuide.CityFirstStep(st, "recast", fo), "city first: no item: no back step");
    st = new LocateStep { Kind = LocateKind.OtherAccount }; Check(!LocateGuide.CityFirstStep(st, "recast", fo), "city first: another account: no back step");
    st = new LocateStep { Kind = LocateKind.OpenRecastTab }; Check(!LocateGuide.CityFirstStep(st, "recast", new ScreenState { Kind = ScreenKind.Unknown }) && !LocateGuide.CityFirstStep(st, "recast", new ScreenState { Kind = ScreenKind.Foundry, Held = true }), "city first: an unknown or held screen changes nothing");
    // after the back press the city is shown: the original step is there again (the step is recomputed every tick)
    st = new LocateStep { Kind = LocateKind.OpenInventory };
    Check(!LocateGuide.CityFirstStep(st, "recast", new ScreenState { Kind = ScreenKind.City }), "city first: back in the city -> the original step");
  }

  static string GeoBox(string key, string x0, string x1, string y0, string y1, double w, double h) {
    // "L:0.1" style edges
    Func<string, string> e = s => "[\"" + s.Split(':')[0] + "\"," + s.Split(':')[1] + "]";
    return "\"" + key + "\":{\"x0\":" + e(x0) + ",\"x1\":" + e(x1) + ",\"y0\":" + e(y0) + ",\"y1\":" + e(y1) + ",\"w\":" + w.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"h\":" + h.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
  }
  const string GeoGame = "9.8.7.6.5";
  static string GeoJson(string game = GeoGame, int v = 1) {
    var b = new List<string> {
      GeoBox("foundry.tab.forge", "R:-0.159", "R:0.0066", "T:0.0328", "T:0.1578", 0.1656, 0.125),
      GeoBox("ascend.slot.3", "L:0.443", "L:0.5695", "M:-0.3969", "M:-0.3312", 0.1266, 0.0656),
      GeoBox("ascend.filter", "L:0.4531", "L:0.6969", "M:-0.3136", "M:-0.2573", 0.2438, 0.0563),
      GeoBox("ascend.button", "C:0.1039", "C:0.4414", "M:0.3488", "M:0.4581", 0.3375, 0.1093),
      GeoBox("ascend.list", "L:0.1", "L:0.6", "T:0.2", "T:0.7", 0.5, 0.5),
      GeoBox("ascend.cell", "L:0.3", "L:0.4", "T:0.3", "T:0.4625", 0.1, 0.1625),
      GeoBox("inventory.workshop", "R:-0.6565", "R:-0.5125", "B:-0.1205", "B:-0.0201", 0.144, 0.1004),
      GeoBox("hero.workshop", "R:-0.2515", "R:-0.1249", "M:0.2948", "M:0.4245", 0.1266, 0.1297),
      GeoBox("inventory.cardLeft", "R:-0.4797", "R:-0.2578", "T:0.8505", "T:0.9333", 0.2219, 0.0828),
      GeoBox("inventory.cardSolo", "R:-0.3688", "R:-0.1469", "B:-0.103", "B:-0.0202", 0.2219, 0.0828),
      GeoBox("gem.list", "C:-0.8438", "C:-0.0938", "M:-0.3312", "M:0.4003", 0.75, 0.7316),
      GeoBox("gem.cell", "L:-0.7703", "L:-0.0422", "T:0.1094", "T:0.2687", 0.7281, 0.1594),
      GeoBox("gem.button", "R:-0.3688", "R:-0.1469", "B:-0.103", "B:-0.0202", 0.2219, 0.0828),
      GeoBox("city.barButton", "L:0.0672", "L:0.2016", "B:0.3306", "B:0.465", 0.1344, 0.1344) };
    return "{\"game\":\"" + game + "\",\"v\":" + v + ",\"rects\":{" + string.Join(",", b.ToArray()) + "},\"grids\":{\"ascend.list\":{\"cols\":5,\"lineSize\":108,\"padding\":[0,-10,0,0]},\"gem.list\":{\"cols\":1,\"lineSize\":110,\"padding\":[0,0,0,0]}}}";
  }

  // ------------------------------------------------------------------ the outline of a button (src/GuideShape.cs)
  static void GuideShapeTests() {
    Console.WriteLine("Button outlines (shape + inset from the geometry)");
    string extra = ",\"shape\":{\"k\":\"bevel\",\"c\":0.1,\"cy\":0.2},\"inset\":{\"l\":0.1,\"t\":0,\"r\":0.1,\"b\":0.2}";
    string j = GeoJson().Replace("\"w\":0.3375,\"h\":0.1093}", "\"w\":0.3375,\"h\":0.1093" + extra + "}");
    var g = GameGeometry.Parse(j, GeoGame);
    var sh = g != null ? g.Shape("ascend.button") : null; var ins = g != null ? g.Inset("ascend.button") : null;
    Check(sh != null && sh.K == "bevel" && Math.Abs(sh.CX - 0.1) < 1e-9 && Math.Abs(sh.CY - 0.2) < 1e-9, "shape parsed (cx falls back to c)");
    Check(ins != null && Math.Abs(ins.L - 0.1) < 1e-9 && Math.Abs(ins.B - 0.2) < 1e-9, "inset parsed");
    var plain = GameGeometry.Parse(GeoJson(), GeoGame);
    Check(plain.Shape("ascend.button") == null && plain.Inset("ascend.button") == null && plain.Shape("nope") == null, "missing shape / inset: a rectangle, no inset");
    Check(GameGeometry.Parse(GeoJson().Replace("\"h\":0.1093}", "\"h\":0.1093,\"shape\":{\"k\":\"blob\"},\"inset\":{\"l\":\"x\"}}"), GeoGame) != null, "an odd shape / inset does not refuse the file");
    var rect = new GeoRect { X = 100, Y = 50, W = 200, H = 100 };
    var o = GuideShape.Outline(rect, null, null);
    Eq(4, o.Count, "rect: 4 points"); Check(o[0].X == 100 && o[0].Y == 50 && o[2].X == 300 && o[2].Y == 150, "rect: the corners");
    var art = GuideShape.ArtBox(rect, new GeoInset { L = 0.1, T = 0, R = 0.1, B = 0.2 });
    Check(Math.Abs(art.X - 120) < 1e-9 && Math.Abs(art.W - 160) < 1e-9 && Math.Abs(art.H - 80) < 1e-9, "inset: the art box");
    var neg = GuideShape.ArtBox(rect, new GeoInset { L = -0.1, T = -0.1, R = -0.1, B = -0.1 });
    Check(Math.Abs(neg.X - 80) < 1e-9 && Math.Abs(neg.Y - 40) < 1e-9 && Math.Abs(neg.W - 240) < 1e-9 && Math.Abs(neg.H - 120) < 1e-9, "negative inset: the art sticks out");
    var c = GuideShape.Outline(rect, new GeoShape { K = "circle" }, null);
    Eq(48, c.Count, "circle: 48 points");
    bool onCircle = true; foreach (var p in c) onCircle &= Math.Abs(Math.Sqrt((p.X - 200) * (p.X - 200) + (p.Y - 100) * (p.Y - 100)) - 50) < 1e-6;
    Check(onCircle, "circle: diameter = the shorter side, centred");
    var b = GuideShape.Outline(rect, new GeoShape { K = "bevel", CX = 0.1, CY = 0.2 }, null);
    Eq(8, b.Count, "bevel: an octagon"); Check(Math.Abs(b[0].X - 110) < 1e-9 && b[0].Y == 50 && b[2].X == 300 && Math.Abs(b[2].Y - 70) < 1e-9, "bevel: cuts cx*h across, cy*h down");
    var rd = GuideShape.Outline(rect, new GeoShape { K = "round", R = 0.3 }, null);
    Eq(4 * (GuideShape.CornerSteps + 1), rd.Count, "round: arcs at the four corners");
    bool inside = true; foreach (var p in rd) inside &= p.X >= 100 - 1e-9 && p.X <= 300 + 1e-9 && p.Y >= 50 - 1e-9 && p.Y <= 150 + 1e-9;
    Check(inside, "round: inside the rectangle");
    Check(GuideShape.Outline(new GeoRect { X = 0, Y = 0, W = 0, H = 10 }, null, null).Count == 0, "an empty box: no outline");
    var hp = GuideShape.HandPos(5, 10, 40, 60, 0.5, 0.9, 1000, 500);
    Check(hp.X == 0 && hp.Y == 0, "hand: kept inside the client");
    var hp2 = GuideShape.HandPos(500, 300, 40, 60, 0.5, 0.9, 1000, 500);
    Check(Math.Abs(hp2.X - 480) < 1e-9 && Math.Abs(hp2.Y - 246) < 1e-9, "hand: the fingertip lands on the point");
    Check(GuideShape.FrameOutline(rect, null, null, 2) == null && GuideShape.FrameOutline(rect, new GeoShape { K = "rect" }, null, 2) == null && GuideShape.FrameOutline(rect, new GeoShape { K = "blob" }, null, 2) == null, "old frame: a rect / missing / unknown shape keeps the rectangle (null)");
    var fo = GuideShape.FrameOutline(rect, new GeoShape { K = "circle" }, new GeoInset { L = 0.25, T = 0, R = 0.25, B = 0 }, 2);
    bool onFo = fo != null; if (onFo) foreach (var p in fo) onFo &= Math.Abs(Math.Sqrt((p.X - 200) * (p.X - 200) + (p.Y - 100) * (p.Y - 100)) - 52) < 1e-6;
    Check(onFo, "old frame: the circle of the inset art, grown by the margin");
    var gr = GuideShape.Grow(rect, 5); Check(gr.X == 95 && gr.W == 210, "grown by a margin");
    var rs = RemoteSwitches.Parse("{\"features\":{\"guideStyle\":true}}");
    Check(rs.Allows("guideStyle") && !RemoteSwitches.Permissive().Allows("guideStyle") && !RemoteSwitches.Parse("{}").Allows("guideStyle"), "guideStyle: off until the site says true");
    Check(AppConfig.FromJson("{\"guideStyle\":\"on\"}").GuideStyle == "on" && AppConfig.FromJson("{\"guideStyle\":\"x\"}").GuideStyle == "auto" && AppConfig.FromJson("{}").GuideStyle == "auto", "config: guideStyle auto / on / off");
  }

  static void GeometryTests() {
    Console.WriteLine("Screen geometry (the site's file per game version, frames and Foundry clicks)");
    const double W = 2000, H = 1000, U = 1000;
    var g = GameGeometry.Parse(GeoJson(), GeoGame);
    Check(g != null && g.Game == GeoGame && g.Count == 14, "geometry: the fixture parses");
    var r = g.Rect("ascend.button", W, H, U);
    Check(r != null && Math.Abs(r.X - (W / 2 + 0.1039 * U)) < 0.01 && Math.Abs(r.W - 0.3375 * U) < 0.01 && Math.Abs(r.Y - (H / 2 + 0.3488 * U)) < 0.01 && Math.Abs(r.H - 0.1093 * U) < 0.01, "geometry: C / M anchors from the centre");
    r = g.Rect("inventory.workshop", W, H, U);
    Check(Math.Abs(r.X - (W - 0.6565 * U)) < 0.01 && Math.Abs(r.Y - (H - 0.1205 * U)) < 0.01, "geometry: R / B anchors from the right and bottom edges");
    r = g.Rect("ascend.list", W, H, U); Check(Math.Abs(r.X - 100) < 0.01 && Math.Abs(r.Y - 200) < 0.01 && Math.Abs(r.W - 500) < 0.01, "geometry: L / T anchors");
    var r2 = g.Rect("ascend.list", 1000, 562, 562); Check(Math.Abs(r2.X - 56.2) < 0.01 && Math.Abs(r2.H - 281) < 0.01, "geometry: another window size scales with U");
    Check(g.Rect("nope", W, H, U) == null && g.Rect(null, W, H, U) == null, "geometry: an unknown key -> null");
    Eq(5, g.Grid("ascend.list").Cols, "geometry: the grid's columns"); Eq(-10.0, g.Grid("ascend.list").PadTop, "geometry: the grid's top padding"); Check(g.Grid("x") == null, "geometry: no such grid");
    // parse refusals
    Check(GameGeometry.Parse(GeoJson(), "9.8.7.6.4") == null, "geometry: another version is not used");
    Check(GameGeometry.Parse(GeoJson(GeoGame, 2), GeoGame) == null, "geometry: another format is not used");
    Check(GameGeometry.Parse(GeoJson().Replace("\"R\",-0.159", "\"Q\",-0.159"), GeoGame) == null, "geometry: an unknown anchor is refused");
    Check(GameGeometry.Parse(GeoJson().Replace("\"w\":0.1656", "\"w\":\"x\""), GeoGame) == null, "geometry: a non-number size is refused");
    Check(GameGeometry.Parse("{\"game\":\"" + GeoGame + "\",\"v\":1,\"rects\":{}}", GeoGame) == null, "geometry: no rectangles -> not used");
    Check(GameGeometry.Parse("[1]", GeoGame) == null && GameGeometry.Parse("<html>", GeoGame) == null && GameGeometry.Parse(null, GeoGame) == null && GameGeometry.Parse("", GeoGame) == null, "geometry: not an object -> null");
    Check(GameGeometry.Parse(GeoJson().Replace("\"game\"", "\"pad\":\"" + new string('x', 140000) + "\",\"game\""), GeoGame) == null, "geometry: an oversized reply is refused");
    Check(GameGeometry.Parse(GeoJson().Replace("\"cols\":5", "\"cols\":0"), GeoGame).Grid("ascend.list") == null, "geometry: a bad grid is left out");
    GameGeometry got;
    Eq(Layout.Reply.Got, Geo.Interpret(200, GeoJson(), GeoGame, out got), "geometry reply 200: got"); Check(got != null, "  ... and parsed");
    Eq(Layout.Reply.None, Geo.Interpret(404, "{}", GeoGame, out got), "geometry reply 404: none for this version");
    Eq(Layout.Reply.Denied, Geo.Interpret(401, "{}", GeoGame, out got), "geometry reply 401: no valid code");
    Eq(Layout.Reply.NoReply, Geo.Interpret(500, GeoJson(), GeoGame, out got), "geometry reply 5xx: no reply (what is held stays)");
    Eq(Layout.Reply.NoReply, Geo.Interpret(200, "{}", GeoGame, out got), "geometry reply 200 with junk: no reply");
    // the saved copies, per version
    string dir = Path.Combine(Path.GetTempPath(), "rf-geo-" + Guid.NewGuid().ToString("N"));
    Check(Geo.LoadCache(dir, GeoGame) == null, "geometry: no saved copy -> null");
    Geo.SaveCache(dir, GeoGame, GeoJson());
    Check(Geo.LoadCache(dir, GeoGame) != null, "geometry: the saved copy loads (offline use)");
    Check(Geo.LoadCache(dir, "9.8.7.6.4") == null, "geometry: a copy of another version is not used");
    File.WriteAllText(Path.Combine(dir, GeoGame + ".json"), "{broken");
    Check(Geo.LoadCache(dir, GeoGame) == null, "geometry: a broken saved copy -> null, no crash");
    Check(Geo.LoadCache(dir, "../x") == null, "geometry: a bad version is no path");
    try { Directory.Delete(dir, true); } catch (Exception) { }
    var heldGeo = Geo.Current;
    Geo.Use(null); Check(Geo.Rect("ascend.list", W, H, U) == null, "geometry: none in force -> every rectangle is null (text hints)");
    Geo.Use(g); Check(Geo.Rect("ascend.list", W, H, U) != null, "geometry: in force -> the accessor places it");
    Geo.Use(heldGeo);

    // tabs: the k-th shown tab sits k tab heights below the first position
    Eq(2, FoundryPlace.TabIndex(null, "ascend"), "tabs: all shown -> Ascension is third");
    Eq(1, FoundryPlace.TabIndex(new[] { true, false, true, true, true }, "ascend"), "tabs: Identify hidden -> Ascension moves up");
    Eq(-1, FoundryPlace.TabIndex(new[] { true, true, false, true, true }, "ascend"), "tabs: Ascension hidden -> -1");
    Eq(2, FoundryPlace.TabIndex(new[] { true, true, true, false, false }, "ascend"), "tabs: Recast / Reforge hidden do not move Ascension");
    Eq(3, FoundryPlace.TabIndex(null, "recast"), "tabs: Recast is fourth");
    Eq(-1, FoundryPlace.TabIndex(null, "nope"), "tabs: unknown name");
    var tr = FoundryPlace.Tab(g, "ascend", null, W, H, U);
    Check(tr != null && Math.Abs(tr.Y - (0.0328 + 2 * 0.125) * U) < 0.01 && Math.Abs(tr.X - (W - 0.159 * U)) < 0.01, "tabs: Ascension = the first position + 2 tab heights");
    tr = FoundryPlace.Tab(g, "ascend", new[] { true, false, true, true, true }, W, H, U); Check(Math.Abs(tr.Y - (0.0328 + 0.125) * U) < 0.01, "tabs: one hidden before it -> one height");
    Check(FoundryPlace.Tab(g, "ascend", new[] { true, true, false, true, true }, W, H, U) == null && FoundryPlace.Tab(null, "ascend", null, W, H, U) == null, "tabs: hidden or no geometry -> null");

    // the list cell from the game's own scroll reading: 5 columns, row = 108 canvas units = 168.75 px at U 1000, the list at (100, 200) 500 x 500
    Func<double, RFX.ListRead> L = s => new RFX.ListRead { ScrollY = s, Cols = 5, RowUnits = 108, First = 0, Last = 14 };
    var c = FoundryPlace.Cell(g, "ascend", L(0), 7, W, H, U);   // row 1, column 2
    Check(c != null && c.Mark.Kind == "box" && Math.Abs(c.Mark.X - 300) <= 1 && Math.Abs(c.Mark.Y - 387.5) <= 1 && c.Mark.W == 100 && Math.Abs(c.Mark.H - 162.5) <= 1, "cell: index 7 at scroll 0 -> column 2, row 2 (" + (c != null ? c.Mark.Kind + " " + c.Mark.X + "," + c.Mark.Y + " " + c.Mark.W + "x" + c.Mark.H : "null") + ")");
    Check(c.Clickable && Math.Abs(c.ClickX - 350) <= 1 && Math.Abs(c.ClickY - (387.5 + 81.25)) <= 1, "cell: clickable at its centre");
    c = FoundryPlace.Cell(g, "ascend", L(0), 0, W, H, U); Check(Math.Abs(c.Mark.X - 100) <= 1 && Math.Abs(c.Mark.Y - 218.75) <= 1, "cell: index 0 = the first cell, at the list's top + the padding");
    c = FoundryPlace.Cell(g, "ascend", L(40), 7, W, H, U);   // scrolled by 40 units = 62.5 px: the frame moves exactly with the scrolling
    Check(Math.Abs(c.Mark.Y - (387.5 - 62.5)) <= 1, "cell: the frame moves exactly with the scroll (" + c.Mark.Y + ")");
    c = FoundryPlace.Cell(g, "ascend", L(640), 7, W, H, U); Check(c.Mark.Kind == "up" && c.Mark.Rows == 5 && c.ScrollPx > 0 && !c.Clickable, "cell: far above -> 'up', 5 rows, the content has to move down (" + c.Mark.Kind + " " + c.Mark.Rows + ")");
    c = FoundryPlace.Cell(g, "ascend", L(0), 40, W, H, U); Check(c.Mark.Kind == "down" && c.Mark.Rows == 7 && c.ScrollPx < 0 && !c.Clickable, "cell: far below -> 'down', 7 rows, the content has to move up (" + c.Mark.Kind + " " + c.Mark.Rows + ")");
    c = FoundryPlace.Cell(g, "ascend", L(0), 12, W, H, U);   // row 2: top at 215.6 + 337.5 = 553: bottom 715 is past the view's end (700): cut, not clickable
    Check(c.Mark.Kind == "box" && !c.Clickable && c.Mark.Y + c.Mark.H <= 700, "cell: cut by the viewport edge -> clipped box, scrolled in before a click (" + c.Mark.H + ")");
    var l2 = L(0); l2.Cols = 0; l2.RowUnits = 0; c = FoundryPlace.Cell(g, "ascend", l2, 7, W, H, U); Check(c != null && c.Mark.Kind == "box", "cell: columns / row size missing in the reading -> the authored numbers");
    Check(FoundryPlace.Cell(g, "ascend", new RFX.ListRead(), 7, W, H, U) == null && FoundryPlace.Cell(g, "ascend", null, 7, W, H, U) == null && FoundryPlace.Cell(null, "ascend", L(0), 7, W, H, U) == null && FoundryPlace.Cell(g, "recast", L(0), 7, W, H, U) == null,
      "cell: unreadable scroll / no geometry / no recast entry -> null");

    // frames per step
    var item = LocItem(777, 3, 720100, 12, 5, 7, 9, 3);
    var rs = new RecastScreen { SublimingOpen = true, AscendShown = true, CurType = 2, AscendList = L(0) };
    Func<LocateKind, LocateStep> S = k => new LocateStep { Kind = k };
    var f = LocateFrames.For(S(LocateKind.OpenAscendTab), item, rs, null, g, W, H, U);
    Check(f != null && f.Clickable && Math.Abs(f.ClickY - (0.0328 + 0.25 + 0.0625) * U) < 0.01, "frame: the «Вознесение» tab, clickable");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.OpenFoundry }, item, rs, null, g, W, H, U); Check(f != null && f.Clickable && f.Target == "inventory.workshop", "frame: «Литейная» in the inventory, clickable");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.OpenFoundry, OnGear = true }, item, rs, null, g, W, H, U); Check(f != null && f.Clickable && f.Target == "hero.workshop", "frame: «Литейная» on the hero's gear screen, clickable");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.AscendFilter, Missing = new[] { "slot", "main" } }, item, rs, null, g, W, H, U); Check(f != null && f.Clickable && f.Target == "ascend.slot.3", "frame: the item's slot tab first, clickable");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.AscendFilter, Missing = new[] { "main", "subs" } }, item, rs, null, g, W, H, U); Check(f != null && !f.Clickable && f.Target == "ascend.filter", "frame: the filter button is only pointed at");
    f = LocateFrames.For(S(LocateKind.AscendReady), item, rs, null, g, W, H, U); Check(f != null && !f.Clickable && f.Target == "ascend.button", "frame: «Вознести», never clickable");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.Selected, Worn = true }, item, rs, null, g, W, H, U); Check(f != null && f.Clickable && f.Target == "inventory.cardLeft", "frame: the card's gem button (worn item), clickable for the recast path");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.Selected }, item, rs, null, g, W, H, U); Check(f != null && f.Clickable && f.Target == "inventory.cardSolo", "frame: the card's gem button alone (not worn), clickable");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.Done, OnGem = true }, item, rs, null, g, W, H, U); Check(f != null && !f.Clickable && f.Target == "gem.button", "frame: «Улучшить» on the gem screen, never clickable");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.GemFunction, Func = 8 }, item, new RecastScreen { GemOpen = true }, null, g, W, H, U); Check(f != null && !f.Clickable, "frame: the gem function row as a hint, not clickable");
    f = LocateFrames.GemRow(g, new RecastScreen { GemOpen = true }, 8, W, H, U, true); Check(f != null && f.Clickable && f.ClickY > 0, "frame: the gem function row, clickable when asked and in view");
    f = LocateFrames.GemRow(g, new RecastScreen { GemOpen = true, GemFuncList = new RFX.ListRead { ScrollY = 5000, Cols = 1, RowUnits = 110, First = 3, Last = 8 } }, 8, W, H, U, true); Check(f != null && !f.Clickable, "frame: the gem function row scrolled out of the list is not clicked");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.AscendPick, Pos = 8 }, item, rs, null, g, W, H, U); Check(f != null && f.Clickable && f.ViewH > 0, "frame: the item's cell in the Ascension list, clickable");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.AscendPick, Pos = 8 }, item, new RecastScreen { SublimingOpen = true }, null, g, W, H, U); Check(f == null, "frame: the list is unreadable and the game's cell range is unknown -> none");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.AscendPick, Pos = 8, Scroll = "below", ScrollRows = 3 }, item, new RecastScreen { SublimingOpen = true }, null, g, W, H, U); Check(f != null && f.Mark.Kind == "down" && f.Mark.Rows == 3 && !f.Clickable, "frame: scroll not readable but the cell range says 'below' -> only the pill");
    f = LocateFrames.For(new LocateStep { Kind = LocateKind.RecastPick, Pos = 1 }, item, rs, null, g, W, H, U); Check(f == null, "frame: no recast rectangles in the data -> none");
    f = LocateFrames.For(S(LocateKind.AscendReady), item, rs, null, null, W, H, U); Check(f == null, "frame: no geometry -> none (text hints)");
    f = LocateFrames.For(S(LocateKind.InList), item, rs, null, g, W, H, U); Check(f == null, "frame: the inventory list keeps its own placement");
    var cs = LocateFrames.CityButtonSize(g, U); Check(Math.Abs(cs[0] - 134.4) < 0.01 && Math.Abs(cs[1] - 134.4) < 0.01, "frame: the city button's size from the geometry");
    cs = LocateFrames.CityButtonSize(null, U); Check(Math.Abs(cs[0] - 110) < 0.01 && Math.Abs(cs[1] - 130) < 0.01, "frame: ... else the old estimate");

    // the real file the site serves (the web repo next to this one): everything the frames use is in it and sits where the app's older constants say
    string real = Path.Combine(WebRoot(), "data", "app-geometry");
    if (Directory.Exists(real)) {
      int n = 0;
      foreach (var p in Directory.GetFiles(real, "*.json")) {
        string v = Path.GetFileNameWithoutExtension(p);
        var rg = GameGeometry.Parse(File.ReadAllText(p, Encoding.UTF8), v);
        Check(rg != null, "the site's geometry file " + v + " parses");
        if (rg == null) continue;
        n++;
        bool all = true; foreach (var k in new[] { "foundry.tab.forge", "ascend.list", "ascend.cell", "ascend.filter", "ascend.button", "ascend.slot.0", "ascend.slot.4", "recast.list", "recast.cell", "recast.button",
                                                    "inventory.workshop", "hero.workshop", "inventory.cardLeft", "city.barButton" }) if (!rg.Has(k)) { all = false; Console.WriteLine("    missing " + k); }
        Check(all && rg.Grid("ascend.list") != null && rg.Grid("recast.list") != null, "the site's geometry file " + v + " has every rectangle and grid the frames use");
        var il = rg.Rect("inventory.list", 1920, 1009, 1009);
        Check(il != null && Math.Abs(il.Y / 1009 - 0.190) < 0.003, "the site's geometry file " + v + ": the inventory list top agrees with the app's constant (" + (il != null ? (il.Y / 1009).ToString("0.0000") : "-") + ")");
        var cell = FoundryPlace.Cell(rg, "ascend", L(0), 0, 1920, 1009, 1009); var vw = rg.Rect("ascend.list", 1920, 1009, 1009);
        Check(cell != null && cell.Mark.Kind == "box" && cell.Mark.X >= vw.X - 1 && cell.Mark.X + cell.Mark.W <= vw.Right + 1 && cell.Mark.Y >= vw.Y - 1, "the site's geometry file " + v + ": the first Ascension cell sits inside the list");
        // the filter pop-up: its frames sit to the right of the filter button, the set cell inside the set grid
        if (rg.Has("filter.root")) {
          var fbtn = rg.Rect("ascend.filter", 1920, 1009, 1009);
          var pstep = new LocateStep { Kind = LocateKind.AscendFilter, Missing = new[] { "suit" }, FilterGo = "suitRow" };
          var pf = LocateFrames.For(pstep, item, null, null, rg, 1920, 1009, 1009);
          Check(pf != null && pf.Target == "filter.suitOpen" && !pf.Clickable && pf.Mark.X >= fbtn.Right - 1 && pf.Mark.Y > 0, "pop-up: the set row's button sits right of the filter button, never clickable");
          pstep.FilterGo = "reset"; pf = LocateFrames.For(pstep, item, null, null, rg, 1920, 1009, 1009); Check(pf != null && pf.Target == "filter.reset" && pf.Mark.X >= fbtn.Right - 1, "pop-up: «Сбросить» frame");
          pstep.FilterGo = "suitTab"; pstep.SuitTab = 1; var tab1 = LocateFrames.For(pstep, item, null, null, rg, 1920, 1009, 1009); pstep.SuitTab = 0; var tab0 = LocateFrames.For(pstep, item, null, null, rg, 1920, 1009, 1009);
          Check(tab0 != null && tab1 != null && tab1.Mark.X > tab0.Mark.X && tab1.Mark.Y == tab0.Mark.Y, "pop-up: the 3-piece tab is right of the 2-piece tab");
          var prs = new RecastScreen { FilterSuitGrid = new RFX.ListRead { ScrollY = 0, Cols = 2, RowUnits = 42, First = 0, Last = 17 } };
          pstep.FilterGo = "suitCell"; pstep.SuitIndex = 0; var c0 = LocateFrames.For(pstep, item, prs, null, rg, 1920, 1009, 1009);
          pstep.SuitIndex = 3; var c3 = LocateFrames.For(pstep, item, prs, null, rg, 1920, 1009, 1009);
          Check(c0 != null && c3 != null && !c0.Clickable && c3.Mark.X > c0.Mark.X && c3.Mark.Y > c0.Mark.Y, "pop-up: set cells fill the grid two per row (index 3 = second row, right column)");
          prs.FilterSuitGrid = null; pstep.SuitIndex = 3; var fb = LocateFrames.For(pstep, item, prs, null, rg, 1920, 1009, 1009); Check(fb != null && fb.Target == "ascend.filter", "pop-up: set grid unreadable -> only the filter button is pointed at");
        }
        var ab = LocateFrames.For(new LocateStep { Kind = LocateKind.AscendReady }, item, null, null, rg, 1920, 1009, 1009); var abr = rg.Rect("ascend.button", 1920, 1009, 1009);
        Check(ab != null && ab.Mark.H < abr.H - 5 && ab.Mark.Y > abr.Y && ab.Mark.Y + ab.Mark.H < abr.Bottom, "«Вознести»: the frame hugs the visible picture, inside the image rectangle");
        var t3 = FoundryPlace.Tab(rg, "ascend", null, 1920, 1009, 1009); Check(t3 != null && t3.X > 1920 / 2 && t3.Right <= 1920 + 10, "the site's geometry file " + v + ": the Ascension tab is at the right edge");
      }
      Check(n > 0, "the site has geometry files");
    } else Console.WriteLine("  (no ../wardsage-web: the site's geometry files not checked)");
  }

  // the recast path through the card's gem button, the gem screen's function list, the item's mark on the inventory's scroll bar
  static void LocateGemAndListTests() {
    Console.WriteLine("Locate: card button, gem function rows and steps, the list's scroll bar mark");
    // function rows: the config order, locked functions drop out
    Eq(0, GemRows.Index(7), "gem rows: Recast is the first row");
    Eq(1, GemRows.Index(8), "gem rows: Advanced Recast the second");
    Eq(2, GemRows.Index(5), "gem rows: Refine the third");
    Eq(8, GemRows.Index(4), "gem rows: the last function is the ninth row");
    Eq(-1, GemRows.Index(99), "gem rows: an unknown function is not in the list");
    Eq(0, GemRows.Index(8, new[] { 7 }), "gem rows: Recast locked -> Advanced Recast moves up to the first row");
    Eq(-1, GemRows.Index(7, new[] { 7 }), "gem rows: a locked function has no row");
    Eq(7, GemRows.FuncOfQuality(6), "gem func: mythic -> Recast"); Eq(7, GemRows.FuncOfQuality(10), "gem func: variant of mythic's table row (10) -> Recast");
    Eq(8, GemRows.FuncOfQuality(8), "gem func: ancient mythic -> Advanced Recast"); Eq(8, GemRows.FuncOfQuality(11), "gem func: 11 -> Advanced Recast");
    Eq(8, GemRows.FuncOfQuality(12), "gem func: 12 -> Advanced Recast"); Eq(8, GemRows.FuncOfQuality(13), "gem func: 13 -> Advanced Recast");
    Eq(0, GemRows.FuncOfQuality(0), "gem func: quality unknown -> either function"); Eq(0, GemRows.FuncOfQuality(3), "gem func: a lower quality has no recast");

    // steps on the gem screen: the function the item needs; done as soon as it is chosen (no press of «Улучшить» waited for)
    var mythic = LocItem(777, 4, 720100, 12, 5, 7, 9, 3); mythic.Quality = 6;
    var ancient = LocItem(778, 4, 720100, 12, 5, 7, 9, 3); ancient.Quality = 8;
    var rq = new LocateRequest { Id = "g1", Purpose = "recast", Item = mythic, HeroUid = 0 };
    var ra = new LocateRequest { Id = "g2", Purpose = "recast", Item = ancient, HeroUid = 0 };
    var gm = new RecastScreen { GemOpen = true, GemSel = 777, GemFunc = -1 };
    var st = LocateGuide.Next(rq, null, gm, null, 8, 1);
    Check(st.Kind == LocateKind.GemFunction && st.Func == 7 && !st.WrongFunc, "gem step: mythic, no function chosen -> «Преобразование»");
    gm.GemFunc = 8; st = LocateGuide.Next(rq, null, gm, null, 8, 1);
    Check(st.Kind == LocateKind.GemFunction && st.Func == 7 && st.WrongFunc, "gem step: mythic with Advanced Recast chosen -> still choosing, the other function is named");
    gm.GemFunc = 7; st = LocateGuide.Next(rq, null, gm, null, 8, 1);
    Check(st.Kind == LocateKind.Done && st.OnGem && st.Func == 7, "gem step: mythic with Recast chosen -> done at once");
    gm.GemSel = 778; gm.GemFunc = 7; st = LocateGuide.Next(ra, null, gm, null, 8, 1);
    Check(st.Kind == LocateKind.GemFunction && st.Func == 8 && st.WrongFunc, "gem step: ancient mythic with Recast chosen -> not done, «Продв. преобразование» is named");
    gm.GemFunc = 8; st = LocateGuide.Next(ra, null, gm, null, 8, 1);
    Check(st.Kind == LocateKind.Done && st.OnGem && st.Func == 8, "gem step: ancient mythic with Advanced Recast chosen -> done at once");
    gm.GemFunc = 1; st = LocateGuide.Next(ra, null, gm, null, 8, 1);
    Check(st.Kind == LocateKind.GemFunction && !st.WrongFunc, "gem step: a gem enhancement chosen -> choose the recast (not 'the other recast')");
    gm.GemFunc = 8; gm.GemSel = 5; gm.GemList = new long[] { 5, 778 }; st = LocateGuide.Next(ra, null, gm, null, 8, 1);
    Check(st.Kind == LocateKind.GemPick && st.Pos == 2, "gem step: the function is chosen but another item is on the screen -> pick the item in the pop-up");
    // the card: both buttons for a worn item, the gem button alone otherwise
    var invS = new InventoryScreen { Open = true, EquipTab = true, Uids = new List<long>(new long[] { 777 }), Current = 777 };
    st = LocateGuide.Next(rq, invS, null, null, 8, 1); Check(st.Kind == LocateKind.Selected && !st.Worn, "card step: an item that is not worn -> the gem button is alone");
    st = LocateGuide.Next(new LocateRequest { Id = "g3", Purpose = "recast", Item = mythic, HeroUid = 200 }, invS, null, null, 8, 1); Check(st.Kind == LocateKind.Selected && st.Worn, "card step: a worn item -> both buttons");

    // frames on a fixture: the gem rows and the button
    const double W = 2000, H = 1000, U = 1000;
    var j = "{\"game\":\"" + GeoGame + "\",\"v\":1,\"rects\":{" + string.Join(",", new[] {
      GeoBox("gem.list", "L:0.1", "L:0.58", "T:0.2", "T:0.7", 0.48, 0.5), GeoBox("gem.cell", "L:0", "L:0.45", "T:0.3", "T:0.4", 0.45, 0.1),
      GeoBox("gem.button", "C:-0.17", "C:0.17", "M:0.345", "M:0.455", 0.34, 0.11),
      GeoBox("inventory.cardLeft", "R:-0.48", "R:-0.26", "B:-0.1", "B:-0.02", 0.22, 0.08), GeoBox("inventory.cardSolo", "R:-0.37", "R:-0.15", "B:-0.1", "B:-0.02", 0.22, 0.08),
      GeoBox("inventory.scrollbar", "L:0.8", "L:0.815", "T:0.2", "T:0.8", 0.015, 0.6), GeoBox("inventory.listView", "L:0.1", "L:0.8", "T:0.2", "T:0.75", 0.7, 0.55) })
      + "},\"grids\":{\"gem.list\":{\"cols\":1,\"lineSize\":110,\"padding\":[0,0,0,0]}}}";
    var g = GameGeometry.Parse(j, GeoGame);
    var f7 = LocateFrames.For(new LocateStep { Kind = LocateKind.GemFunction, Func = 7 }, mythic, new RecastScreen { GemOpen = true }, null, g, W, H, U);
    var f8 = LocateFrames.For(new LocateStep { Kind = LocateKind.GemFunction, Func = 8 }, ancient, new RecastScreen { GemOpen = true }, null, g, W, H, U);
    double pitch = 110.0 / 640 * U;
    Check(f7 != null && f8 != null && f7.Mark.Kind == "box" && !f7.Clickable, "gem frame: the function's row is framed, a hint only");
    Check(Math.Abs((f7.Mark.Y) - (200 + (pitch - 100) / 2)) <= 1 && Math.Abs(f8.Mark.Y - f7.Mark.Y - pitch) <= 1 && f7.Mark.H == 100, "gem frame: the rows follow each other by the list's line size (Recast row 1, Advanced Recast row 2)");
    Check(Math.Abs(f7.Mark.X - (100 + (480 - 450) / 2.0)) <= 1 && f7.Mark.W == 450, "gem frame: the row sits centred in the list's width");
    var scr = new RecastScreen { GemOpen = true, GemFuncList = new RFX.ListRead { ScrollY = 220, Cols = 1, RowUnits = 110, First = 2, Last = 6 } };
    var f7s = LocateFrames.For(new LocateStep { Kind = LocateKind.GemFunction, Func = 7 }, mythic, scr, null, g, W, H, U);
    Check(f7s != null && f7s.Mark.Kind == "up" && f7s.Mark.Rows >= 1, "gem frame: the list scrolled past the row -> the pill says which way");
    scr.GemFuncList.ScrollY = 110; var f8s = LocateFrames.For(new LocateStep { Kind = LocateKind.GemFunction, Func = 8 }, ancient, scr, null, g, W, H, U);
    Check(f8s != null && Math.Abs(f8s.Mark.Y - (200 + (pitch - 100) / 2)) <= 1, "gem frame: the list's own scroll moves the rows (one row down = the second function is where the first was)");
    Check(LocateFrames.For(new LocateStep { Kind = LocateKind.GemFunction, Func = 7 }, mythic, null, null, null, W, H, U) == null, "gem frame: no geometry -> none");
    var fb = LocateFrames.For(new LocateStep { Kind = LocateKind.Done, OnGem = true, Func = 7 }, mythic, new RecastScreen(), null, g, W, H, U);
    var br = g.Rect("gem.button", W, H, U);
    Check(fb != null && fb.Target == "gem.button" && !fb.Clickable && fb.Mark.X == (int)Math.Round(br.X) && fb.Mark.W == (int)Math.Round(br.W), "gem frame: done -> «Улучшить» is framed, never clicked");
    var fw = LocateFrames.For(new LocateStep { Kind = LocateKind.Selected, Worn = true }, mythic, null, null, g, W, H, U);
    var fs2 = LocateFrames.For(new LocateStep { Kind = LocateKind.Selected, Worn = false }, mythic, null, null, g, W, H, U);
    Check(fw != null && fs2 != null && fw.Target == "inventory.cardLeft" && fs2.Target == "inventory.cardSolo" && fs2.Mark.X > fw.Mark.X, "card frame: worn = left of the row, not worn = the centred button");
    Check(fw.Mark.Y + fw.Mark.H == (int)Math.Round(H - 0.02 * U) || Math.Abs(fw.Mark.Y + fw.Mark.H - (H - 0.02 * U)) <= 1, "card frame: it sits on the card's bottom edge");

    // the item's mark on the list's scroll bar: the bar is 600 px, the content 20 rows of 100 + 12 padding twice
    double pad = 12, vt = 200, p100 = 100;
    var mk = LocateFrames.ListMarker(g, vt + pad, vt, pad, p100, 20, 20, W, H, U);   // scrolled to the top, the item in the last row
    double total = 20 * p100 + 2 * pad;
    Check(mk != null && Math.Abs(mk[0] - (0.8 + 0.0075) * U) < 1 && mk[1] == 200 && mk[2] == 800, "list mark: on the bar's middle line, over the bar's whole height");
    Check(Math.Abs(mk[3] - (200 + 600 * (pad + 19.5 * p100) / total)) < 0.5, "list mark: the dot at the item's share of the whole list (" + (mk != null ? mk[3].ToString("0.0") : "-") + ")");
    Check(Math.Abs(mk[4] - 200) < 0.5 && Math.Abs(mk[5] - (200 + 600 * 550 / total)) < 0.5, "list mark: the bracket shows the part on the screen (top of the list)");
    var mk1 = LocateFrames.ListMarker(g, vt + pad, vt, pad, p100, 20, 1, W, H, U);
    Check(mk1[3] < mk[3] && Math.Abs(mk1[3] - (200 + 600 * (pad + 0.5 * p100) / total)) < 0.5, "list mark: the first row's dot is near the top");
    var mk2 = LocateFrames.ListMarker(g, vt + pad - 500, vt, pad, p100, 20, 20, W, H, U);   // scrolled down by 500 px
    Check(Math.Abs(mk2[4] - (200 + 600 * 500 / total)) < 0.5 && Math.Abs(mk2[3] - mk[3]) < 0.01, "list mark: scrolling moves the bracket, not the item's dot");
    Check(LocateFrames.ListMarker(g, vt + pad, vt, pad, p100, 20, 21, W, H, U) == null && LocateFrames.ListMarker(g, vt + pad, vt, pad, p100, 20, 0, W, H, U) == null, "list mark: a row outside the list -> none");
    Check(LocateFrames.ListMarker(null, vt + pad, vt, pad, p100, 20, 5, W, H, U) == null, "list mark: no geometry -> none (only the pill)");
    {   // the storage cleanup's strip: the viewport's bracket and a dot for EACH target still to mark (a row of two targets = two dots)
      var rowsT = new List<int> { 1, 5, 5, 20 };
      var sv = LocateFrames.ListStrip(g, vt + pad - 500, vt, pad, p100, 20, rowsT, 20, W, H, U);
      Check(sv != null && sv.Length == 7 + 2 * 3 && sv[6] == 1 && sv[8] == 1 && sv[10] == 2 && sv[12] == 1, "scroll strip: 4 targets, the two of one row merge into one marker with a count (" + (sv != null ? (sv.Length - 7).ToString() : "-") + ")");
      var mkL = LocateFrames.ListMarker(g, vt + pad - 500, vt, pad, p100, 20, 20, W, H, U);
      Check(sv != null && Math.Abs(sv[3] - mkL[3]) < 0.01 && Math.Abs(sv[4] - mkL[4]) < 0.01 && Math.Abs(sv[5] - mkL[5]) < 0.01 && Math.Abs(sv[7] - LocateFrames.ListMarker(g, vt + pad - 500, vt, pad, p100, 20, 1, W, H, U)[3]) < 0.01 && sv[7] > sv[1],
        "scroll strip: the viewport's bracket and the lead dot as the single marker's; every marker at its row's share");
      {   // 100 targets: clustered, at most 12 markers, all inside the bar, counts add up
        var many = new List<int>(); for (int i = 1; i <= 20; i++) for (int jj = 0; jj < 5; jj++) many.Add(i);
        var sm = LocateFrames.ListStrip(g, vt + pad, vt, pad, p100, 20, many, 1, W, H, U);
        int nm = sm != null ? (sm.Length - 7) / 2 : -1, tot = 0; bool inside = sm != null, asc = true;
        for (int i = 7; sm != null && i + 1 < sm.Length; i += 2) { tot += (int)sm[i + 1]; inside &= sm[i] >= sm[1] && sm[i] <= sm[2]; if (i > 7 && sm[i] < sm[i - 2]) asc = false; }
        Check(sm != null && nm >= 1 && nm <= 12 && tot == 100 && inside && asc, "scroll strip: 100 targets -> " + nm + " markers (max 12), counts sum to 100, inside the bar");
        var cl = LocateFrames.ClusterMarks(new List<double> { 100, 104, 109, 300 }, 50, 400);
        Check(cl.Count == 2 && (int)cl[0][1] == 3 && (int)cl[1][1] == 1, "scroll strip: markers within 10 px merge");
        var cl2 = LocateFrames.ClusterMarks(new List<double> { 0, 500 }, 50, 400);
        Check(cl2[0][0] >= 50 && cl2[1][0] <= 400, "scroll strip: a marker is kept inside the bar");
      }
      Check(LocateFrames.ListStrip(g, vt + pad, vt, pad, p100, 20, new List<int>(), 1, W, H, U) == null && LocateFrames.ListStrip(null, vt + pad, vt, pad, p100, 20, rowsT, 1, W, H, U) == null, "scroll strip: no targets or no bar geometry -> none");
    }

    // the side scroll strip of a live list (ListStrip): view 200..800, 20 rows of 100 px, row 0's top now at the view's top
    var ls0 = ListStrip.Of(500, 200, 800, 200, 100, 20, 19);
    Check(ls0 != null && ls0[0] == 500 && ls0[1] == 200 && ls0[2] == 800, "list strip: the track beside the viewport, over its whole height");
    Check(Math.Abs(ls0[3] - (200 + 600 * 1950.0 / 2000)) < 0.01 && Math.Abs(ls0[4] - 200) < 0.01 && Math.Abs(ls0[5] - (200 + 600 * 600.0 / 2000)) < 0.01, "list strip: the last row's dot near the bottom, the bracket the top 600 of 2000 px");
    var ls1 = ListStrip.Of(500, 200, 800, 200 - 700, 100, 20, 19);   // scrolled down 700 px
    Check(Math.Abs(ls1[4] - (200 + 600 * 700.0 / 2000)) < 0.01 && Math.Abs(ls1[5] - (200 + 600 * 1300.0 / 2000)) < 0.01 && Math.Abs(ls1[3] - ls0[3]) < 0.01, "list strip: the scroll moves the bracket, the dot stays on the item");
    var ls2 = ListStrip.Of(500, 200, 800, 200 - 5000, 100, 20, 0);   // past the end: clamped inside the track
    Check(Math.Abs(ls2[4] - (200 + 600 * 1400.0 / 2000)) < 0.01 && Math.Abs(ls2[5] - 800) < 0.01 && Math.Abs(ls2[3] - (200 + 600 * 50.0 / 2000)) < 0.01, "list strip: an overscroll stays on the track, the first row's dot at the top");
    var ls3 = ListStrip.Of(500, 200, 800, 200, 100, 3, 1);   // a list shorter than the view: the bracket covers the track
    Check(Math.Abs(ls3[4] - 200) < 0.01 && Math.Abs(ls3[5] - 800) < 0.01 && Math.Abs(ls3[3] - (200 + 150)) < 0.01, "list strip: a short list -> the whole track bracketed, the dot at the row's place");
    Check(ListStrip.Of(500, 200, 800, 200, 100, 20, 20) == null && ListStrip.Of(500, 200, 800, 200, 100, 0, 0) == null && ListStrip.Of(500, 200, 800, double.NaN, 100, 20, 1) == null, "list strip: a row outside / no rows / no scroll -> none");
    var lv0 = new GeoRect { X = 10, Y = 200, W = 300, H = 600 };
    var lf0 = ListStrip.For(lv0, 200, 100, 3, 59, 58, 1000, 1000);   // 59 items in 3 columns: 20 rows, the last item in row 19
    Check(lf0 != null && Math.Abs(lf0[0] - (310 + 12)) < 0.01 && Math.Abs(lf0[3] - ls0[3]) < 0.01, "list strip: a grid's item index -> its row (index / columns), the track 0.012 H right of the view");
    Check(ListStrip.For(lv0, 200, 100, 3, 59, 59, 1000, 1000) == null && Math.Abs(ListStrip.For(lv0, 200, 100, 3, 59, 0, 315, 1000)[0] - 309) < 0.01, "list strip: an index past the list -> none; the track kept inside the client");
    // the hero screen's artifact list (ArtFrames.Strip): the same rows as its card frames (ArtFrames.Cell)
    var arl = new RFX.ListRead { ScrollY = 232, Cols = 3, RowUnits = 116 };
    var ars = ArtFrames.Strip(null, ArtEquipFrames.KeyList, arl, 30, 29, 3, 116, W, H, U);
    var arv = ArtFrames.Rect(null, ArtEquipFrames.KeyList, W, H, U); double ak = U / GameGeometry.CanvasH;
    Check(ars != null && Math.Abs(ars[1] - arv.Y) < 0.01 && Math.Abs(ars[2] - arv.Bottom) < 0.01, "art list strip: the track over the artifact list's viewport");
    Check(Math.Abs(ars[3] - (arv.Y + arv.H * 9.5 / 10)) < 0.01 && Math.Abs(ars[4] - (arv.Y + arv.H * Math.Min(1, 232 * ak / (10 * 116 * ak)))) < 0.01, "art list strip: 30 cards = 10 rows, the last card's dot in row 10, the bracket at the scroll (2 rows down)");
    Check(ArtFrames.Strip(null, ArtEquipFrames.KeyList, new RFX.ListRead(), 30, 1, 3, 116, W, H, U) == null && ArtFrames.Strip(null, ArtEquipFrames.KeyList, arl, 30, 30, 3, 116, W, H, U) == null, "art list strip: no scroll reading / an index past the list -> none");
    Check(FoundryPlace.Strip(null, "ascend", arl, 30, 1, W, H, U) == null, "ascension strip: no geometry -> none (the pill alone)");
    // the find step's still list: framed once the scroll has not moved for ScrollStill.StillMs
    var ss = new ScrollStill();
    Check(ss.Note(100, 1000), "scroll still: the first reading counts as still (the card framed at once)");
    Check(!ss.Note(140, 1100) && !ss.Note(180, 1200) && !ss.Note(180.3, 1200 + ScrollStill.StillMs - 1) && ss.Note(180, 1200 + ScrollStill.StillMs), "scroll still: moving -> the steady plate; held StillMs -> still");
    Check(ss.Note(double.NaN, 1300), "scroll still: no reading -> still (nothing to wait for)");
    ss.Reset(); Check(ss.Note(5, 9000), "scroll still: after a reset the next reading is still again");

    // the real file: the card's button sits on the card's bottom edge at any window height, the bar next to the list
    string real = Path.Combine(WebRoot(), "data", "app-geometry");
    if (Directory.Exists(real)) foreach (var p in Directory.GetFiles(real, "*.json")) {
      string v = Path.GetFileNameWithoutExtension(p);
      var rg = GameGeometry.Parse(File.ReadAllText(p, Encoding.UTF8), v);
      if (rg == null) continue;
      bool all = true; foreach (var k in new[] { "inventory.cardSolo", "inventory.listView", "inventory.scrollbar", "gem.list", "gem.cell", "gem.button" }) if (!rg.Has(k)) { all = false; Console.WriteLine("    missing " + k); }
      Check(all && rg.Grid("gem.list") != null, "the site's geometry file " + v + " has the card's solo button, the list's scroll bar and the gem screen");
      foreach (var hh in new[] { 640.0, 1009.0, 1440.0 }) {
        double ww = hh * 16 / 9, uu = hh;
        var l = rg.Rect("inventory.cardLeft", ww, hh, uu); var r = rg.Rect("inventory.cardRight", ww, hh, uu); var so = rg.Rect("inventory.cardSolo", ww, hh, uu);
        Check(Math.Abs(l.Bottom - (hh - 12.9 / 640 * uu)) < 1 && Math.Abs(so.CY - l.CY) < 0.5, "the site's geometry file " + v + ": the card's buttons sit on the card's bottom edge at " + hh + " px (" + l.Bottom.ToString("0.0") + ")");
        Check(Math.Abs(so.CX - (l.X + r.Right) / 2) < 1.5, "the site's geometry file " + v + ": the lone button is in the middle of the pair's span");
        var bar = rg.Rect("inventory.scrollbar", ww, hh, uu); var lv = rg.Rect("inventory.listView", ww, hh, uu);
        Check(bar != null && lv != null && Math.Abs(bar.Y - lv.Y) < 1 && bar.X > lv.Right - 25 && bar.X < lv.Right + 25 && bar.Bottom > lv.Bottom - 5, "the site's geometry file " + v + ": the scroll bar runs beside the list at its right edge");
        var gl = rg.Rect("gem.list", ww, hh, uu); var gb = rg.Rect("gem.button", ww, hh, uu);
        Check(gl != null && gb != null && gb.CX > gl.CX && gb.Y > hh * 0.7 && gb.CX > ww * 0.5 && gb.CX < ww * 0.85, "the site's geometry file " + v + ": the function list is left of «Улучшить» (bottom right of the centre)");
        var gf = FoundryPlace.Cell(rg, "gem", new RFX.ListRead { ScrollY = 0, Cols = 1, RowUnits = 110 }, GemRows.Index(8), ww, hh, uu);
        Check(gf != null && gf.Mark.Kind == "box" && gf.Mark.Y >= gl.Y && gf.Mark.Y + gf.Mark.H <= gl.Bottom + 1, "the site's geometry file " + v + ": the second function's row is inside the list at the top");
      }
    }
  }


  static void OwnButtonTests() {
    Console.WriteLine("Own buttons over the game (record button vs the back arrow, off-screen points):");
    var hg = new HeroGeometry();
    // live 2026-10-29 (laptop, game window not maximized): Locate's HeroBack @39,28 started a recording instead of pressing the game's arrow
    var hint = new HintGeometry(); var fg = new FilterGeometry(); var sg = new SellGeometry(); var lg = new ListGeometry();
    foreach (double U in new[] { 600.0, 757.0, 1009.0, 1440.0 }) {
      foreach (double asp in new[] { 16.0 / 9, 1.6, 4.0 / 3 }) {
        double H = U, W = Math.Round(H * asp);
        if (Ui.Unit(W, H) != U) continue;
        int[] rb0 = OwnButtons.RecBox(W, H);
        var one = new List<int[]> { rb0 };
        int bx = (int)Math.Round(hg.BackX * U), by = (int)Math.Round(hg.BackY * U);
        string at = "U " + U + " W " + W + ": ";
        Check(!OwnButtons.Covers(one, bx, by, by), at + "the record button does not cover the back arrow");
        Check(rb0[0] >= 0 && rb0[1] >= 0 && rb0[0] + rb0[2] <= W && rb0[1] + rb0[3] <= H, at + "the record button lies in the client area");
        // every known target box: the gear screen's, the hero grid's, the filter, the sell screen's and the list's, left edge / centre anchored
        var boxes = new List<int[]> { hint.Filter(W, H), hint.Replace(W, H), hint.Equip(W, H) };
        for (int sl = 0; sl < 5; sl++) boxes.Add(hint.Slot(sl, W, H));
        double hx = hg.ColX(0) * U, cw = hg.PitchX * U * 0.9;
        boxes.Add(new[] { (int)(hx - cw / 2), (int)(hg.ViewTop * H), (int)(cw * 3), (int)((hg.ViewBottom - hg.ViewTop) * H) });                       // the hero grid
        boxes.Add(new[] { (int)(lg.X0 * U), (int)(lg.ViewTop * H), (int)(lg.CellW * U), (int)((lg.ViewBottom - lg.ViewTop) * H) });                   // the list's first column
        boxes.Add(new[] { (int)(sg.SellBtnX * U) - 4, (int)(sg.SellBtnY * U) - 4, 8, 8 });
        var pts = new List<double[]> {
          new[] { hg.BackX * U, hg.BackY * U },
          new[] { hg.FunnelX * U, H - hg.FunnelDY * U },
          new[] { hg.ClassX * U, hg.FilterRow0 * U }, new[] { hg.FactionX * U, hg.FilterRow0 * U },
          new[] { W - hg.GearTabDX * U, hg.GearTabY * U },
          new[] { W - hg.HeroesBtnDX * U, H - hg.HeroesBtnDY * U },
          new[] { W / 2 + fg.MainCloseX * U, fg.CloseY * U }, new[] { W / 2 + fg.SideCloseX * U, fg.CloseY * U },
          new[] { W / 2 + fg.ResetX * U, Ui.FromMiddle(fg.ResetY, W, H) }, new[] { W / 2 + fg.HideEquippedX * U, Ui.FromMiddle(fg.HideEquippedY, W, H) },
          new[] { W / 2 + sg.ResetX * U, Ui.FromMiddle(sg.ResetY, W, H) }, new[] { W / 2 + sg.CloseX * U, sg.CloseY * U },
        };
        for (int pi = 0; pi < 5; pi++) pts.Add(new[] { W / 2 + (sg.PartX0 + pi * sg.PartPitch) * U, Ui.FromMiddle(sg.PartY, W, H) });
        for (int bi = 0; bi < boxes.Count; bi++) {
          var bb = boxes[bi];
          bool hit = bb[0] < rb0[0] + rb0[2] && rb0[0] < bb[0] + bb[2] && bb[1] < rb0[1] + rb0[3] && rb0[1] < bb[1] + bb[3];
          Check(!hit, at + "the record button misses known box #" + bi);
        }
        for (int pi = 0; pi < pts.Count; pi++) {
          int px = (int)Math.Round(pts[pi][0]), py = (int)Math.Round(pts[pi][1]);
          Check(!OwnButtons.Covers(one, px, py, py), at + "the record button misses known click point #" + pi);
        }
      }
    }
    int[] rb = OwnButtons.RecBox(1346, 757);
    Check(rb[0] == 0 && rb[2] == 28, "record box at U 757: flush with the left edge, 25 + 3 px");
    Check(Math.Abs(rb[1] + rb[2] / 2 - 757 / 2) <= 1, "record box centred at mid-height");
    Check(OwnButtons.Covers(new List<int[]> { rb }, 10, 378, 378), "a point on it is covered");
    Check(!OwnButtons.Covers(new List<int[]> { rb }, 39, 28, 28) && !OwnButtons.Covers(new List<int[]> { rb }, 60, 378, 378), "the back arrow's live point and points beside it are free");
    Check(OwnButtons.Covers(new List<int[]> { new[] { 100, 100, 20, 20 } }, 105, 50, 300), "a drag crossing a part touches it");
    Check(!OwnButtons.Covers(new List<int[]>(), 1, 1, 1) && !OwnButtons.Covers(null, 1, 1, 1), "no parts: nothing covered");
    int[] desk = { 0, 0, 1366, 768 };
    Check(OwnButtons.AllInside(desk, 39, 28, 28), "on the desktop");
    Check(!OwnButtons.AllInside(desk, 1400, 28, 28) && !OwnButtons.AllInside(desk, 100, 700, 800) && !OwnButtons.AllInside(desk, -1, 5, 5), "off the desktop (a window larger than the screen): refused");
  }


  // the game's window table (UIInstance) and its twin of the same shape, UIFormClass (form id -> the form's class): which one is read, the names,
  // the shown flag, the reasons the log gives (live 2026-10-29: the finder took UIFormClass, no form name was ever read)
  static void WindowTableTests() {
    Console.WriteLine("Window table: UIInstance, not UIFormClass");
    var m = new FakeLua(1 << 21); RFX.TestMemory = m; RFX.ForgetLive();
    try {
      var fs = new FakeState(m); long inst = 20001;
      Func<string, FTab> Cls = nm => fs.T("__cname", nm, "ctor", 0L, "__index", 0L);
      var cCity = Cls("Form_MainCity"); var cPlan = Cls("Form_EquipmentPlan"); var cHero = Cls("Form_CharactorMain"); var cBattle = Cls("Form_Battle");
      Func<FTab, bool, FTab> Obj = (c, shown) => shown ? fs.T("m_LuaLogicActive", true, "class", c, "____instanceId", inst++) : fs.T("m_InitOK", true, "class", c, "____instanceId", inst++);
      const long idCity = -1796900587, idPlan = 657588872, idHero = 1456276573, idBattle = -1008207313;
      var classes = fs.T(idCity, cCity, idPlan, cPlan, idHero, cHero, idBattle, cBattle);                        // UIFormClass: every form file loaded
      var forms = fs.T(idCity, Obj(cCity, false), idPlan, Obj(cPlan, true));                                    // UIInstance: the city (covered) and the plan screen
      var ids = new[] { idHero, idBattle, idCity };
      Check(RFX.PickUiTableForTest(new[] { classes.A, forms.A }, ids) == forms.A, "window table: of the two tables keyed by form ids the one with form objects is taken");
      Check(RFX.PickUiTableForTest(new[] { forms.A, classes.A }, ids) == forms.A, "window table: ... whatever order the scan found them in");
      Eq(0UL, RFX.PickUiTableForTest(new[] { classes.A }, ids), "window table: the class table alone is never taken");
      Check(RFX.PickUiTableForTest(new[] { classes.A, forms.A }, new[] { idHero, idBattle }) == 0, "window table: no anchor form object (only the classes hold them): nothing is taken");
      // read from the class table (the old bug): nothing shown, no names, and the log line says why
      RFX.SetUiTableForTest(classes.A);
      var off0 = RFX.ReadOffers();
      Check(off0 != null && off0.Count == 0, "window table (class table): no offer read - what the lab saw");
      string d0 = RFX.ShownFormsDiag(8);
      Check(d0.Contains("0 shown") && d0.Contains("no form objects"), "window table (class table): the log line says it holds no form objects (" + d0 + ")");
      // read from UIInstance: the build plan screen is the shown offer, its name is in the log line
      RFX.SetUiTableForTest(forms.A);
      var off = RFX.ReadOffers();
      Check(off != null && off.Count == 1 && off[0] == "Form_EquipmentPlan", "window table: the build plan screen is read by its class name (" + (off == null ? "null" : string.Join(",", off.ToArray())) + ")");
      string d1 = RFX.ShownFormsDiag(8);
      Check(d1 == "window table: 2 forms, 1 shown: Form_EquipmentPlan", "window table: the log line lists the shown forms (" + d1 + ")");
      Eq("", RFX.ReadShownKnownForms(), "window table: no known screen's form is shown (the city is covered)");
      // a shown form without a class field: named by its key and the reason; the known-forms read is "not sure" (null)
      var odd = fs.T(idCity, Obj(cCity, false), 777L, fs.T("m_LuaLogicActive", true, "m_Foo", 1L));
      RFX.SetUiTableForTest(odd.A);
      string d2 = RFX.ShownFormsDiag(8);
      Check(d2.Contains("id:777(no class field)"), "window table: a form without a name is listed with the reason (" + d2 + ")");
      Check(RFX.ReadShownKnownForms() == null, "window table: a shown form without a name: the known forms are not sure (null)");
      // the battle and the hero screen shown are known screens (the back press is never made then); the tip toast is not one
      var cTips = Cls("Form_CommonTips");
      var fight = fs.T(idBattle, Obj(cBattle, true), 99L, Obj(cTips, true));
      RFX.SetUiTableForTest(fight.A);
      Eq("Form_Battle", RFX.ReadShownKnownForms(), "window table: the battle shown is a known screen's form, the tip toast is not");
      // more forms without a name than one call may read: the ones after them are named on the next call (a miss is not read again for 10 s)
      var many = new List<object>();
      for (int i = 0; i < 30; i++) { many.Add((long)(1000 + i)); many.Add(fs.T("m_LuaLogicActive", true, "m_X", 1L)); }
      many.Add(idPlan); many.Add(Obj(cPlan, true));
      RFX.SetUiTableForTest(fs.T(many.ToArray()).A);
      RFX.ReadOffers(); var off2 = RFX.ReadOffers();
      Check(off2 != null && off2.Contains("Form_EquipmentPlan"), "window table: 30 nameless forms before it do not hide the plan screen for good (" + (off2 == null ? "null" : string.Join(",", off2.ToArray())) + ")");
      // a form's id is the hash of its name (UIDefines): every screen-map form is an anchor of the search
      Eq(idCity, RFX.FormIdOf("Form_MainCity"), "form id: the city's id from its name");
      Eq(idHero, RFX.FormIdOf("Form_CharactorMain"), "form id: the hero screen's id from its name");
      Eq(idBattle, RFX.FormIdOf("Form_Battle"), "form id: the battle's id from its name");
      Eq(idPlan, RFX.FormIdOf("Form_EquipmentPlan"), "form id: the build plan's id from its name (odd length)");
      // the full search from any screen (the scans over the test memory): the layout's anchors are the city, the hero screen and the battle
      {
        var m2 = new FakeLua(1 << 21); RFX.TestMemory = m2; RFX.ForgetLive();
        var fs2 = new FakeState(m2); long inst2 = 30001;
        Func<string, FTab> Cls2 = nm => fs2.T("__cname", nm, "ctor", 0L, "__index", 0L);
        const long idGuild = -1859031432, idFoundry = -1395031855, idChat = 748358717, idPrivacy = -1015447067, idOdd = 123456789;
        var k2City = Cls2("Form_MainCity"); var k2Guild = Cls2("Form_Union"); var k2Chat = Cls2("Form_MinChat");
        // live 2026-10-30, the game on the guild after the app restarted: UIFormClass kept 3 classes (the city's and two never opened;
        // the game cleared the destroyed forms' classes and a form required again is never registered again), the city destroyed
        var cls2 = fs2.T(idCity, k2City, idPrivacy, Cls2("Form_PopupPrivacy"), idOdd, Cls2("Form_Something"), 1L, 0L);
        var guildOnly = fs2.T(idGuild, fs2.T("m_LuaLogicActive", true, "class", k2Guild, "____instanceId", inst2++),
                              idChat, fs2.T("m_LuaLogicActive", false, "class", k2Chat, "____instanceId", inst2++), 1L, 0L, 2L, 0L);   // UIInstance: the guild and the chat
        var notForms = fs2.T(idFoundry, fs2.T("m_LuaLogicActive", true, "class", Cls2("UIStackItem"), "____instanceId", inst2++), 1L, 0L, 2L, 0L, 3L, 0L);   // keyed by a form id, not a form
        string said;
        ulong got = RFX.FindUiTableForTest(ids, out said);
        Check(got == guildOnly.A, "window table: found on the guild with the city destroyed and UIFormClass nearly empty, by the screen map's form ids (" + said + ")");
        Check(said.StartsWith("forms: window table found from Form_Union"), "window table: the log line names the screen it was found from (" + said + ")");
        Check(!said.Contains("\n"), "window table found: no diagnostic lines");
        // the test build's diagnostic of a failed search: each candidate's size, first keys and value marks
        string dg = RFX.UiCandidatesDiagForTest(new[] { cls2.A, guildOnly.A }, ids, 40);
        Check(dg.Contains("2 candidate table(s)") && dg.Contains("cname=Form_PopupPrivacy") && dg.Contains("class=Form_Union") && dg.Contains("inst") && dg.Contains("active=1"),
              "window table diagnostic: sizes, keys and marks of the candidates (" + dg.Replace("\n", " | ") + ")");
        Check(RFX.UiCandidatesDiagForTest(new[] { cls2.A, guildOnly.A, notForms.A }, ids, 2).Split('\n').Length <= 3, "window table diagnostic: capped");
        Eq(0UL, RFX.PickUiTableForTest(new[] { notForms.A }, new[] { idFoundry }), "window table: objects whose class is not a Form_ are not forms");
        // live 2026-10-30 (1.6.66.346): UIInstance shrunk to one or two nodes (the guild alone, the rest destroyed): a table of any size is a candidate
        foreach (int n in new[] { 1, 2 }) {
          var m5 = new FakeLua(1 << 21); RFX.TestMemory = m5; RFX.ForgetLive();
          var fs5 = new FakeState(m5); long inst5 = 50001;
          Func<string, FTab> Cls5 = nm => fs5.T("__cname", nm, "ctor", 0L, "__index", 0L);
          var k5Guild = Cls5("Form_Union");
          var cls5 = fs5.T(idCity, Cls5("Form_MainCity"), RFX.FormIdOf("Form_Login"), Cls5("Form_Login"), 631657439L, Cls5("Form_UnionAdd"));
          var g5 = fs5.T("m_LuaLogicActive", true, "class", k5Guild, "____instanceId", inst5++);
          var small = n == 1 ? fs5.T(idGuild, g5) : fs5.T(idGuild, g5, idChat, fs5.T("m_LuaLogicActive", false, "class", Cls5("Form_MinChat"), "____instanceId", inst5++));
          got = RFX.FindUiTableForTest(ids, out said);
          Check(got == small.A, "window table: UIInstance of " + n + " node(s) is found (" + said.Replace("\n", " | ") + ")");
          Check(said.StartsWith("forms: window table found from Form_Union"), "window table: the small table names the guild (" + said + ")");
          string nd = RFX.UiNodesDiagForTest(ids);
          Check(nd.StartsWith("  form id nodes: ") && nd.Contains(", 0 outside the candidates"), "window table diagnostic: every form id node lies in a candidate (" + nd + ")");
        }
        // a node whose table header is not found: named in the diagnostic by its form name and its value's marks
        {
          var m6 = new FakeLua(1 << 21); RFX.TestMemory = m6; RFX.ForgetLive();
          var fs6 = new FakeState(m6);
          var lost = fs6.T(idGuild, fs6.T("m_LuaLogicActive", true, "class", fs6.T("__cname", "Form_Union"), "____instanceId", 60001L));
          m6.Mem[lost.A - FakeLua.Base + 8] = 0;   // the header unreadable as a table: the node alone remains
          string nd = RFX.UiNodesDiagForTest(ids);
          Check(nd.Contains(", 1 outside the candidates") && nd.Contains("Form_Union") && nd.Contains("class=Form_Union") && nd.Contains("active=1"),
                "window table diagnostic: a node outside the candidates is named with its marks (" + nd.Replace("\n", " | ") + ")");
        }
        // the city open: found in the first round, as before
        var m3 = new FakeLua(1 << 21); RFX.TestMemory = m3; RFX.ForgetLive();
        var fs3 = new FakeState(m3); long inst3 = 40001;
        Func<string, FTab> Cls3 = nm => fs3.T("__cname", nm, "ctor", 0L, "__index", 0L);
        var k3City = Cls3("Form_MainCity"); var k3Guild = Cls3("Form_Union");
        var cls3 = fs3.T(idCity, k3City, idGuild, k3Guild, 1L, 0L, 2L, 0L);
        var city = fs3.T(idCity, fs3.T("m_LuaLogicActive", true, "class", k3City, "____instanceId", inst3++), 1L, 0L, 2L, 0L, 3L, 0L);
        got = RFX.FindUiTableForTest(ids, out said);
        Check(got == city.A, "window table: found with the city open (" + said + ")");
        Check(said.StartsWith("forms: window table found from Form_MainCity"), "window table: the city open (" + said + ")");
        // only UIFormClass in memory (no form open at all): it is never taken
        var m4 = new FakeLua(1 << 21); RFX.TestMemory = m4; RFX.ForgetLive();
        var fs4 = new FakeState(m4);
        var cls4 = fs4.T(idCity, fs4.T("__cname", "Form_MainCity", "ctor", 0L), idGuild, fs4.T("__cname", "Form_Union", "ctor", 0L), 1L, 0L, 2L, 0L);
        got = RFX.FindUiTableForTest(ids, out said);
        Eq(0UL, got, "window table: not fooled by UIFormClass (" + said + ")");
        Check(said.StartsWith("forms: window table not found"), "window table: not found is logged (" + said + ")");
        RFX.TestMemory = m; RFX.ForgetLive();
      }
      RFX.SetUiTableForTest(0);
      Eq("window table not found yet", RFX.ShownFormsDiag(8), "window table: not found - the log line says so");
      Check(RFX.ReadShownKnownForms() == null, "window table not found: the known forms are not read (null)");
    } finally { RFX.SetUiTableForTest(0); RFX.TestMemory = null; RFX.ForgetLive(); }
  }

  // the hint-mode fallback of every flow (HintFallback): an unknown screen / a screen the flow does not continue from / a pop-up nobody knows -> «back to the city»
  static void HintFallbackTests() {
    Console.WriteLine("Hint fallback: back to the city");
    const double W = 1366, H = 705; double U = Ui.Unit(W, H);
    var hg = new HeroGeometry(); double bx = hg.BackX * U, by = hg.BackY * U;
    Func<string, string, ScreenState> Unk = (top, known) => ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = top, ShownKnown = known }, 0);
    var inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true };
    var invS = ScreenState.Classify(new ScreenInputs { Inv = inv, Offers = new List<string>(), ShownKnown = "" }, 0);
    var city = ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string>(), ShownKnown = "" }, 0);
    var heroS = ScreenState.Classify(new ScreenInputs { Hero = new HeroScreen { Hero = 5, FormOk = true }, Offers = new List<string>(), ShownKnown = "" }, 0);
    var foundry = ScreenState.Classify(new ScreenInputs { Rs = new RecastScreen { SublimingOpen = true }, Offers = new List<string>(), ShownKnown = "" }, 0);
    Func<PilotFlow, ScreenState, bool, HintFallbackPlan> D = (f, sc, live) => HintFallback.Decide(f, sc, LocateKind.None, live, W, H, U);
    Func<GeoRect, bool> onBack = r => r != null && Math.Abs(r.CX - bx) <= 0.03 * U + 1 && Math.Abs(r.CY - by) <= 0.03 * U + 1;
    // scenario 10 (live 10:5x): the gear filter pop-up is open (the game's flag, gear filter mem=yes) on the hero screen and the flow cannot go on from it: «Закройте фильтр» on its red ×, not «Вернитесь в город»
    {
      Func<Mem3, ScreenState> gearHero = open => { var gs = ScreenState.Classify(new ScreenInputs { Hero = new HeroScreen { Hero = 5, FormOk = true, Tab = HeroGeometry.GearTab, Part = 2, Gear = new GearMem { Open = open, Side = Mem3.No, SetPanel = Mem3.No } }, Offers = new List<string>(), ShownKnown = "Form_CharactorMain" }, 0);
        if (open == Mem3.Yes) { gs.ModalOpen = true; gs.ModalWhy = "form:Form_EquipFilterWidget"; } return gs; };   // (the pop-up as a form nobody of the map names: a stray one)
      var fgm = new FilterGeometry(); double xx = fgm.X(fgm.MainCloseX, W, H), yy = fgm.Y(fgm.CloseY, W, H);
      foreach (var f in new[] { PilotFlow.LocateAscend, PilotFlow.LocateRecast, PilotFlow.Sell }) {
        var pl = D(f, gearHero(Mem3.Yes), false);
        Check(pl.Kind == HintLeave.Close && pl.Text == "closeFilter" && pl.Rect != null && Math.Abs(pl.Rect.CX - xx) < 2 && Math.Abs(pl.Rect.CY - yy) < 2, "fallback: " + f + " with the gear filter open (mem=yes): the plate says to close the filter, its × framed (" + pl.Kind + " '" + pl.Text + "')");
        var pn = D(f, gearHero(Mem3.No), false);
        Check(pn.Text == "", "fallback: " + f + " with the gear filter shut: not the filter's text (" + pn.Kind + ")");
      }
      // live 2026-11-06 1.6.68.600: the filter open with no pick, the item in the list on the left: no «close the filter»; covered: close; a slot: the row in the filter
      Check(GearFilterHint.Decide("pick", -1, 5, false) == GearFilterMove.ListBeside, "filter hint: open, nothing picked, the item visible beside it: no close tip");
      Check(GearFilterHint.Decide("pick", -1, 5, true) == GearFilterMove.Close, "filter hint: the pop-up covers the item's cell: close it");
      Check(GearFilterHint.Decide("slot", 2, 5, false) == GearFilterMove.SlotInFilter && GearFilterHint.Decide("slot", 2, 5, true) == GearFilterMove.SlotInFilter, "filter hint: the slot step: the slot row in the filter, covered or not");
      Check(GearFilterHint.Decide("slot", 2, 5, false, false) == GearFilterMove.FixFilter && GearFilterHint.Decide("slot", 2, 5, true, false) == GearFilterMove.FixFilter, "filter hint: the slot step with another item's set left in the filter: fix the filter, no frame on the slot row");
      Check(GearFilterHint.Decide("slot", 2, 5, false, true) == GearFilterMove.SlotInFilter, "filter hint: the slot step with a fitting filter: the slot row");
      Check(GearFilterHint.Decide("pick", -1, 5, false, false) == GearFilterMove.ListBeside, "filter hint: only the slot step cares about the fit");
      Check(GearFilterHint.Fits(new long[0], new long[0], new long[0], 8, 3, null) && GearFilterHint.Fits(null, null, null, 8, 3, null), "filter fits: an empty filter lets everything through");
      Check(!GearFilterHint.Fits(new long[] { 5 }, null, null, 8, 3, null) && GearFilterHint.Fits(new long[] { 5, 8 }, null, null, 8, 3, null), "filter fits: the item's set must be among the chosen");
      Check(!GearFilterHint.Fits(null, new long[] { 2 }, null, 8, 3, null) && GearFilterHint.Fits(null, new long[] { 3 }, null, 8, 3, null) && GearFilterHint.Fits(null, new long[] { 2 }, null, 8, 0, null), "filter fits: the main stat (0 = not judged)");
      Check(!GearFilterHint.Fits(null, null, new long[] { 9 }, 8, 3, new long[] { 1, 2 }) && GearFilterHint.Fits(null, null, new long[] { 1 }, 8, 3, new long[] { 1, 2 }) && GearFilterHint.Fits(null, null, new long[] { 9 }, 8, 3, null), "filter fits: the sub stats");
      Check(GearFilterHint.Fits(new long[] { 8 }, new long[] { 3 }, null, 0, 0, null), "filter fits: nothing known about the item: no verdict");
      Check(GearFilterHint.Decide("slot", 7, 5, false) == GearFilterMove.ListBeside, "filter hint: a slot out of the icons' range is not framed in the filter");
      // live 1.6.68.605 (item 55032, slot 0): the filter held another item's set 721900, the item's set 711500 is not in the filter's list; the main stat 1 is chosen
      { long[] su = { 721900 }, no = new long[0];
        Check(GearFilterHint.Extra(su, no, no, 711500, 0, null) == FilterExtra.Set && !GearFilterHint.Fits(su, no, no, 711500, 0, null), "filter extra: another item's set left in the filter = a set to clear");
        Check(GearFilterHint.Extra(su, new long[] { 1 }, no, 711500, 0, null) == FilterExtra.Set, "filter extra: a one-stat slot's main stat is not judged: still only the set");
        Check(GearFilterHint.Extra(no, no, no, 711500, 0, null) == FilterExtra.None && GearFilterHint.Fits(no, no, no, 711500, 0, null), "filter extra: an empty filter holds nothing (the item has no set in the list: none needed)");
        Check(GearFilterHint.Extra(no, new long[] { 2 }, no, 8, 3, null) == FilterExtra.Stat, "filter extra: another main stat = a stat to clear");
        Check(GearFilterHint.Extra(no, new long[] { 3 }, no, 8, 3, null) == FilterExtra.None, "filter extra: the item's own main stat is no leftover");
        Check(GearFilterHint.Extra(no, no, new long[] { 9 }, 8, 3, new long[] { 1, 2 }) == FilterExtra.Sub && GearFilterHint.Extra(no, no, new long[] { 1 }, 8, 3, new long[] { 1, 2 }) == FilterExtra.None, "filter extra: a sub stat the item lacks");
        var all = GearFilterHint.Extra(su, new long[] { 2 }, new long[] { 9 }, 8, 3, new long[] { 1 });
        Check(all == (FilterExtra.Set | FilterExtra.Stat | FilterExtra.Sub) && GearFilterHint.Several(all) && GearFilterHint.First(all) == FilterExtra.Set, "filter extra: several leftovers = «Сбросить»; the set is the first of them");
        Check(!GearFilterHint.Several(FilterExtra.Stat) && !GearFilterHint.Several(FilterExtra.None) && GearFilterHint.First(FilterExtra.Stat | FilterExtra.Sub) == FilterExtra.Stat && GearFilterHint.First(FilterExtra.None) == FilterExtra.None, "filter extra: one leftover is named alone; then the stat before the sub stats");
        Check(GearFilterHint.Decide("slot", 0, 5, false, GearFilterHint.Fits(su, no, no, 711500, 0, null)) == GearFilterMove.FixFilter, "filter extra: the slot step with the set left in the filter fixes the filter");
      }
      Check(D(PilotFlow.Equip, gearHero(Mem3.Yes), false).Text != "closeFilter", "fallback: the equip flow's own open gear filter is not told to be closed by this plate");
      // the page's step: the open gear filter covers the hero screen's buttons: «close the filter window» with the × key of the gear filter, whatever coverable step it hides
      foreach (var k in new[] { LocateKind.OpenFoundry, LocateKind.OpenInventory }) {
        var stp = new LocateStep { Kind = k, OnGear = true }; bool rep = LocateGuide.CoverStep(stp, gearHero(Mem3.Yes));
        Check(rep && stp.Kind == LocateKind.ClosePopup && stp.Covered == k && stp.PopupTitle == "filter" && stp.PopupClose == "hero.filterClose", "locate step " + k + ": the gear filter open (mem=yes): the step is «close the filter window» on its ×, not the way back (" + stp.Kind + ")");
        Check(!LocateGuide.CoverStep(new LocateStep { Kind = k, OnGear = true }, gearHero(Mem3.No)), "locate step " + k + ": the gear filter shut: the step stays");
      }
    }
    // an unknown screen whose form the map knows with a back arrow: the arrow framed (every flow alike)
    foreach (var f in CityFirst.All) {
      var pl = D(f, Unk("Form_ActALinkageMain", ""), false);
      Check(pl.Kind == HintLeave.Back && onBack(pl.Rect), "fallback: " + f + " on a screen with a back arrow: the arrow framed (" + pl.Kind + ")");
    }
    // an unknown screen nobody has in the map: the arrow when the host read it live, else the text alone with Escape (no frame)
    var odd = Unk("Form_Odd", "");
    Check(D(PilotFlow.Equip, odd, true).Kind == HintLeave.Back && onBack(D(PilotFlow.Equip, odd, true).Rect), "fallback: an unknown screen, back arrow read live: framed");
    var esc = D(PilotFlow.Sell, odd, false);
    Check(esc.Kind == HintLeave.Esc && esc.Rect == null, "fallback: an unknown screen, nothing read: «Esc», no frame");
    // a known screen the flow does not go on from: the screen's back arrow (every flow has its own set)
    Check(D(PilotFlow.Equip, foundry, false).Kind == HintLeave.Back && onBack(D(PilotFlow.Equip, foundry, false).Rect), "fallback: equip on the Foundry: back arrow");
    Check(D(PilotFlow.LocateRecast, ScreenState.Classify(new ScreenInputs { Rs = new RecastScreen { GemOpen = true }, Offers = new List<string>(), ShownKnown = "" }, 0), false).Kind == HintLeave.None, "fallback: the recast flow goes on from the gem screen: no fallback");
    Check(D(PilotFlow.LocateRecast, foundry, false).Kind == HintLeave.Back, "fallback: the recast flow on the Foundry's tab: back arrow");
    // screens the flow goes on from, and the city: never
    Check(D(PilotFlow.Sell, invS, false).Kind == HintLeave.None && D(PilotFlow.Equip, heroS, false).Kind == HintLeave.None && D(PilotFlow.Equip, city, false).Kind == HintLeave.None && D(PilotFlow.Equip, null, false).Kind == HintLeave.None, "fallback: a screen the flow goes on from, the city, no screen: nothing");
    // a pop-up with a known close button over a screen: the close button framed (not the back arrow, it is covered)
    var pop = ScreenState.Classify(new ScreenInputs { Rs = new RecastScreen { SublimingOpen = true }, Offers = new List<string>(), ShownKnown = "", TopNew = "Form_AccountInfo" }, 0);
    var pc = D(PilotFlow.Equip, pop, false);
    Check(pc.Kind == HintLeave.Close && pc.Rect != null && !onBack(pc.Rect), "fallback: a known pop-up over a screen the flow leaves: its close button framed (" + pc.Kind + ")");
    // a pop-up nobody knows: no close button to frame - the text with Escape
    var stray = ScreenState.Classify(new ScreenInputs { Inv = inv, Offers = new List<string>(), ShownKnown = "", TopNew = "Form_NobodyKnowsThis" }, 0);
    Check(D(PilotFlow.Sell, stray, false).Kind == HintLeave.Esc, "fallback: an unrecognised pop-up over the inventory: «Esc»");
    // the hero grid's filter pop-up open: the funnel that closes it is framed, for every flow, whatever step the flow has (never a step frame under it)
    var hfp = ScreenState.Classify(new ScreenInputs { Hero = new HeroScreen { Hero = 5, FormOk = true, FilterOpen = true }, Offers = new List<string>(), ShownKnown = "" }, 0);
    Check(hfp.ModalOpen && hfp.ModalWhy == "heroFilter", "fallback: the hero filter pop-up reads as open (" + hfp.ModalWhy + ")");
    foreach (var f in CityFirst.All) {
      var fp = D(f, hfp, false);
      var fn = new HeroGeometry().GridBarRect(W, H);
      Check(fp.Kind == HintLeave.Close && fp.Rect != null && Math.Abs(fp.Rect.CX - fn.CX) < 1 && Math.Abs(fp.Rect.CY - fn.CY) < 1, "fallback: " + f + " with the hero filter pop-up open: the filter bar framed (" + fp.Kind + ")");
    }
    {   // lab 552 equip_hint_filter_open (frame audit): the Equip fallback's layout of hero.filter was the funnel square [16,641,56,56]; the live m_BtnFilter is [3,637,275,40] at 1366x705
      double W2 = 1366, H2 = 705, U2 = Ui.Unit(W2, H2); var fq = HintFallback.Decide(PilotFlow.Equip, hfp, LocateKind.None, false, W2, H2, U2); int[] lv = { 3, 637, 275, 40 };
      Check(fq.Kind == HintLeave.Close && fq.Key == "hero.filter" && Math.Abs(fq.Rect.CX - (lv[0] + lv[2] / 2.0)) <= 12 && Math.Abs(fq.Rect.CY - (lv[1] + lv[3] / 2.0)) <= 12 && Math.Abs(fq.Rect.W - lv[2]) <= 12 && Math.Abs(fq.Rect.H - lv[3]) <= 12, "fallback: hero.filter layout vs the audit's live rect (lab 552): the whole bar (" + fq.Rect.X + "," + fq.Rect.Y + "," + fq.Rect.W + "x" + fq.Rect.H + ")");
    }
    // live state of the lab run: the game's own flag not read (ModalWhy empty) but the pop-up is on the picture: the same funnel
    var hnf = ScreenState.Classify(new ScreenInputs { Hero = new HeroScreen { Hero = 5, FormOk = true }, Offers = new List<string>(), ShownKnown = "" }, 0);
    Check(!hnf.ModalOpen && HintFallback.Decide(PilotFlow.Equip, hnf, LocateKind.None, false, W, H, U).Kind == HintLeave.None && HintFallback.Decide(PilotFlow.Equip, hnf, LocateKind.None, false, W, H, U, true).Kind == HintLeave.Close, "fallback: the hero filter pop-up seen on the picture only: the funnel framed; nothing when it is closed");
    Check(HintFallback.Decide(PilotFlow.Equip, hnf, LocateKind.None, false, W, H, U, true).Key == "hero.filter" && FrameTargets.Of("hero.filter") != null, "fallback: the funnel frame carries the key hero.filter (FrameFor reads its live rect, the audit sees it)");
    // a held screen is not judged; a locate request with nothing to drive never goes back
    var held = Unk("Form_ActALinkageMain", ""); held.Held = true;
    Check(D(PilotFlow.Equip, held, false).Kind == HintLeave.None, "fallback: a held screen is not judged");
    Check(HintFallback.Decide(PilotFlow.LocateRecast, foundry, LocateKind.NoItem, false, W, H, U).Kind == HintLeave.None, "fallback: nothing to drive: no back");
  }


  // back to the city (ClickTarget.CityBack) from a known screen the pilot cannot start from, and the one Escape after a back press that changed nothing
  // the screen map (src/ScreenMap.cs, src/ScreenMapData.cs): every form of the game has a class; every pop-up / full screen an exit that is not
  // one of its never-click buttons; a dangerous form never gets a click



  static ScreenState MapModel(List<string> stack) {
    int fi = stack.Count - 1;
    while (fi > 0 && "FKU".IndexOf(ScreenMap.ClassOf(stack[fi])) < 0) fi--;
    string full = stack[fi];
    var above = stack.GetRange(fi + 1, stack.Count - fi - 1);
    var i = new ScreenInputs { CityForm = false, Offers = new List<string>(), ShownKnown = "" };
    var inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true };
    switch (full) {
      case "Form_MainCity": i.CityForm = true; i.CitySeen = true; i.TopCity = string.Join(",", above.ToArray()); break;
      case "Form_BackpackIntegration": i.Inv = inv; i.ShownKnown = full; i.TopNew = string.Join(",", above.ToArray()); break;
      case "Form_EquipSubliming": i.Inv = inv; i.Rs = new RecastScreen { SublimingOpen = true, CurType = 1 }; i.ShownKnown = "Form_BackpackIntegration,Form_EquipSubliming"; i.TopNew = string.Join(",", above.ToArray()); break;
      case "Form_CharactorMain": i.Hero = new HeroScreen { Hero = 5, FormOk = true }; i.ShownKnown = full; i.TopNew = string.Join(",", above.ToArray()); break;
      default: { var l = new List<string> { full }; l.AddRange(above); i.TopUnknown = string.Join(",", l.ToArray()); break; }
    }
    var s = ScreenState.Classify(i, 0); s.SinceMs = 0;
    return s;
  }








  // the artifact list's rows from the picture (the fallback of the game's own scroll): a drawn list at 1920 x 1009 (dark background, a dark gap of 6 canvas units between
  // the rows, a card = a red body under a bright crest line), looked at as the overlay does (the strip of the list's viewport)
  static int[] ArtListPicture(SellGeometry g, double W, double H, double U, double row1, int rowsN, out int sx, out int sy, out int sw, out int sh) {
    ArtGridPicture.Strip(g, W, H, U, out sx, out sy, out sw, out sh);
    var px = new int[sw * sh];
    int bg = (33 << 16) | (36 << 8) | 43, body = (120 << 16) | (50 << 8) | 45, crest = (215 << 16) | (185 << 8) | 110;
    for (int i = 0; i < px.Length; i++) px[i] = bg;
    double pitch = g.PitchY * U, card = g.CellHeight * U, cardW = g.CellW * U;
    for (int r = 0; r < rowsN; r++) {
      double top = row1 + r * pitch;
      for (int c = 0; c < g.Columns; c++) {
        double left = g.X(g.X0 + c * g.PitchX, W, U) - sx;
        for (int y = (int)Math.Round(top) - sy; y < (int)Math.Round(top + card) - sy; y++) {
          if (y < 0 || y >= sh) continue;
          int col = y < (int)Math.Round(top) - sy + 5 ? crest : body;
          for (int x = (int)left; x < (int)(left + cardW); x++) if (x >= 0 && x < sw) px[y * sw + x] = col;
        }
      }
    }
    return px;
  }

  static void ArtGridPictureTests() {
    double W = 1920, H = 1009, U = Ui.Unit(W, H);
    var g = SellGeometry.Artifacts(W, H, U, 8);
    double top0 = g.ViewTopPx(U) + g.TopPad * U, pitch = g.PitchY * U; int rowsN = 40;
    int sx, sy, sw, sh;
    var pic = new ArtGridPicture(); double r1;
    // a list just built is at its top: row 1 where the geometry says, to a pixel
    var px = ArtListPicture(g, W, H, U, top0, rowsN, out sx, out sy, out sw, out sh);
    Check(pic.Update(px, sw, sh, sx, sy, g, W, U, rowsN, 7, 1000, out r1) && Math.Abs(r1 - top0) <= 1.5, "art picture: a new list is placed at its top (" + r1.ToString("0.0") + " of " + top0.ToString("0.0") + "; " + pic.Note + ")");
    // the list moves by 41 px, then by 60: followed (the rows' phase gives the exact place)
    px = ArtListPicture(g, W, H, U, top0 - 41, rowsN, out sx, out sy, out sw, out sh);
    Check(pic.Update(px, sw, sh, sx, sy, g, W, U, rowsN, 7, 1030, out r1) && Math.Abs(r1 - (top0 - 41)) <= 1.5, "art picture: followed after a move of 41 px (" + r1.ToString("0.0") + "; " + pic.Note + ")");
    px = ArtListPicture(g, W, H, U, top0 - 101, rowsN, out sx, out sy, out sw, out sh);
    Check(pic.Update(px, sw, sh, sx, sy, g, W, U, rowsN, 7, 1060, out r1) && Math.Abs(r1 - (top0 - 101)) <= 1.5, "art picture: followed after another 60 px (" + r1.ToString("0.0") + "; " + pic.Note + ")");
    // the same list opened already scrolled (no top seen): no place from the picture alone ...
    var pic2 = new ArtGridPicture();
    double far = top0 - 7 * pitch - 23;
    px = ArtListPicture(g, W, H, U, far, rowsN, out sx, out sy, out sw, out sh);
    Check(!pic2.Update(px, sw, sh, sx, sy, g, W, U, rowsN, 8, 3000, out r1) && double.IsNaN(r1), "art picture: a list first seen scrolled far down has no place by itself");
    // ... until the player ticks a card: row 10 (0-based 9) under the pointer
    int clickRow = 9; double rowTop = far + clickRow * pitch, cursorY = rowTop + 60;
    Check(pic2.Update(px, sw, sh, sx, sy, g, W, U, rowsN, 8, 3100, out r1, clickRow, cursorY) && Math.Abs(r1 - far) <= 1.5, "art picture: the player's tick of row 10 places the list (" + r1.ToString("0.0") + " of " + far.ToString("0.0") + ")");
    // the picture's list reading drives the same card frames as the game's: ScrollY from row 1
    var lr = ArtGridPicture.ToListRead(g, U, far);
    Check(Math.Abs(lr.ScrollY * U / 640 - (top0 - far)) <= 0.01 && lr.Cols == 8 && Math.Abs(lr.RowUnits - 116) <= 0.01, "art picture: ToListRead gives the scroll in canvas units (" + lr.ScrollY.ToString("0.0") + ")");
    // nothing but background (another screen): no place
    var pic3 = new ArtGridPicture(); var blank = new int[sw * sh]; for (int i = 0; i < blank.Length; i++) blank[i] = (33 << 16) | (36 << 8) | 43;
    Check(!pic3.Update(blank, sw, sh, sx, sy, g, W, U, rowsN, 9, 5000, out r1), "art picture: an empty list strip gives no place");
    // the recorder's replay draws the artifact cleanup with the artifact list's geometry (ctx.Art), cards 80 x 110 units
    var rinv = new InventoryScreen { Open = true, ArtTab = true, ArtBulk = true, ArtBulkKnown = true, ListPtr = 5 }; for (long i = 1; i <= 16; i++) rinv.Uids.Add(500 + i);
    var rctx = new RecCtx { Mode = "sell", Front = true, ShCols = 8, Art = true, Want = new long[] { 501, 509 } };
    SellHintStep rstep; var rrects = RecScenes.Sell(rctx, rinv, null, top0, W, H, U, out rstep);
    Check(rstep.Kind == SellHintKind.Select && rrects.Count == 2 && Math.Abs(rrects[0].H - 110.0 / 640 * U) <= 2 && Math.Abs(rrects[1].Y - rrects[0].Y - pitch) <= 2, "replay: the artifact cleanup's frames use the artifact list's geometry (" + rrects.Count + " frames)");
    Check(RecCtx.Parse(MiniJson.Parse(rctx.ToJson())).Art, "replay: the context keeps the artifact flag");
  }




  // Storage cleanup without auto click (src/SellHint.cs): the step and the frames from what the inventory shows



  // The game's filter pop-up as the pilot sees it: clicks at the measured places toggle what they toggle in the game.





  // Live 1.6.66.572 (applog_008, 10:29:01-10:29:44): what the filter pilot did with the bar (gear.filterBtn) and with the item changes.
  static FilterView ReplayView(long now, long item, Mem3 mem, bool pix, bool wanted) {
    return new FilterView { NowMs = now, Foreground = true, W = 1920, H = 1009, Item = item, Wanted = wanted, SetId = 722800, StatId = 13, SetIndex = 2, StatIndex = 3,
      Suits = new long[0], MainAttrs = new long[0], SubAttrs = new long[0], SetOrder = new long[] { 723000, 722900, 722800, 722600 },
      PanelMem = mem, PanelOpen = pix };
  }

  // ---------------------------------------------------------------- the live DPS meter (src/DpsFeed.cs)
  static StatLine DL(uint c, int unit, long dmg, long boss = 0, int heal = 0, int taken = 0) {
    return new StatLine { C = c, Unit = unit, Hero = (uint)(1000 + unit), Base = 7000 + unit, Power = 5, Damage = dmg, ToBoss = boss, Heal = heal, Taken = taken };
  }
  static DpsTick DT(int stage, uint frame, bool final, params StatLine[] st) {
    return new DpsTick { Stage = stage, Frame = frame, Final = final, Stats = new List<StatLine>(st) };
  }
  static bool Near(double a, double b, double eps) { return Math.Abs(a - b) <= eps; }

  static void DpsNamesTests() {
    Check(DpsNames.Hero(2001, 0, 0, true) == "Рекс" && DpsNames.Hero(2001, 0, 0, false) == "Rex", "names: known hero by unit, both languages");
    Check(DpsNames.Hero(0, 0, 200100000u, false) == "Rex", "names: hero found by hero id / 100000");
    Check(DpsNames.Hero(99999, 2001, 0, false) == "Rex", "names: unknown unit falls back to the base id");
    Check(DpsNames.Hero(98765, 0, 0, true) == "#98765" && !DpsNames.KnownHero(98765), "names: unknown hero is #id");
    Check(DpsNames.Skill(200117, true) == "Физ. АТК" && DpsNames.Skill(200117, false) == "P. ATK", "names: known skill, both languages");
    Check(DpsNames.Skill(987654321, true) == "Прочее" && DpsNames.Skill(987654321, false) == "Other" && !DpsNames.KnownSkill(987654321), "names: unknown skill is Other, no raw id");
    // nested skills of a hero are owned by the named skill (Ingrid 2266, Sun Wukong 2290)
    Check(DpsNames.SkillRoot(2266, 226609) == 226601 && DpsNames.Skill(DpsNames.SkillRoot(2266, 226609), true) == "Маг. АТК", "names: 226609 is the basic attack of Ingrid");
    Check(DpsNames.Skill(DpsNames.SkillRoot(2266, 226610), true) == "Гнев небес" && DpsNames.Skill(DpsNames.SkillRoot(2266, 222410), true) == "Гнев небес", "names: 226610 and its buff 222410 are Celestial Wrath");
    Check(DpsNames.Skill(DpsNames.SkillRoot(2290, 229003), true) == "Волос разделения" && DpsNames.Skill(DpsNames.SkillRoot(2290, 229003), false) == "Splitting Hairs", "names: 229003 is Splitting Hairs");
    Check(DpsNames.SkillRoot(2266, 987654321) == 987654321 && DpsNames.SkillRoot(0, 226609) == 226601, "names: no parent keeps the id; the hero comes from the id when the unit is unknown");
    Check(DpsNames.Category(2290104, true) != null && DpsNames.Category(2173101, true) == "Пробуждение" && DpsNames.Category(2173101, false) == "Awakening", "names: aspect and awakening categories");
    Check(DpsNames.Category(102000201, true) == "Комплект" && DpsNames.Category(13000926, true) == "Эффект поля" && DpsNames.Category(226601, true) == null, "names: set, field effect, none");
    Check(DpsNames.SkillIcon(226601) == "skill_22660" && DpsNames.SkillIcon(987654321) == "", "names: skill icon");
    bool talent = false; foreach (var tid in new[] { 226630, 226640, 226650 }) if (DpsNames.Skill(tid, true).StartsWith("Талант", StringComparison.Ordinal)) talent = true;
    Check(talent && (DpsNames.Skill(226650, true).Contains("5") || DpsNames.Skill(226650, true).Contains("«")), "names: a talent is labelled by its star or its quoted word");
    // the picture atlases (tools/dps-art.py): sizes of app/res/dps_*.* are 1536 wide for the two JPEG grids
    var hd = DpsArtIndex.Head(2266); var si = DpsArtIndex.Skill(226601);
    Check(hd != null && hd.Length == 4 && hd[2] == 64 && hd[0] + 64 <= 1536 && hd[1] + 64 <= 832 && DpsArtIndex.Head(98765) == null, "art: a hero's portrait is a 64 px cell of the heads grid");
    Check(si != null && si[2] == 64 && si[0] + 64 <= 1536 && DpsArtIndex.Skill(987654321) == null, "art: a skill's icon is a 64 px cell of the skills grid");
    Check(DpsArtIndex.Piece("bar_bg") != null && DpsArtIndex.Piece("common_bg_popup_titel")[2] == 539 && DpsArtIndex.Piece("nope") == null, "art: UI pieces keep their size");
    Check(DpsArtIndex.Rarity(2266) == 5 && DpsArtIndex.ClassPiece(2266) == "prof_Mage" && DpsArtIndex.Faction(2266) > 0 && DpsArtIndex.Rarity(98765) == 0 && DpsArtIndex.ClassPiece(98765) == "", "art: rarity, class and faction of a hero");
    // the grouped list
    var raw = new List<DpsSkill> {
      new DpsSkill { Id = 226609, Amount = 600, Share = 0.6, Dps = 6 }, new DpsSkill { Id = 226601, Amount = 100, Share = 0.1, Dps = 1 },
      new DpsSkill { Id = 226610, Amount = 200, Share = 0.2, Dps = 2 }, new DpsSkill { Id = 222410, Amount = 95, Share = 0.095, Dps = 1 },
      new DpsSkill { Id = 987654321, Amount = 3, Share = 0.003 }, new DpsSkill { Id = 987654322, Amount = 2, Share = 0.002 }, new DpsSkill { Id = 226605, Amount = 4, Share = 0.004 } };
    var gr = DpsSkillRows.Group(2266, raw);
    Check(gr.Count == 3 && gr[0].Id == 226601 && gr[0].Amount == 700 && Near(gr[0].Share, 700 / 1004.0, 1e-9) && Near(gr[0].Dps, 7, 1e-9), "group: 226609 and 226601 add up under the basic attack, share recomputed");
    Check(gr[1].Amount == 295 && gr[1].Name(true) == "Гнев небес" && gr[2].Tail && gr[2].Count == 2 && gr[2].Name(true) == "Прочее · 2" && gr[2].Name(false) == "Other · 2" && gr[2].Amount == 9, "group: the small unknown rest folds into 'Other · N'");
    var one = DpsSkillRows.Group(2266, new List<DpsSkill> { new DpsSkill { Id = 226601, Amount = 1000 }, new DpsSkill { Id = 226610, Amount = 5 } });
    Check(one.Count == 2 && !one[1].Tail && one[1].Name(true) == "Гнев небес", "group: a lone small row keeps its own name");
    Check(DpsSkillRows.Group(1, null).Count == 0 && DpsSkillRows.Group(1, new List<DpsSkill>()).Count == 0, "group: null and empty give nothing");
  }

  static void DpsFeedTests() {
    const int Other = 1001001, Arena = 6001001, Boss = 5001001;
    double fs = 270.0 / 4096;
    // DPS and share
    var f = new DpsFeed();
    f.Push(DT(Other, 300, false, DL(1, 10, 3000), DL(1, 11, 1000)));
    var sn = f.Snapshot; var cu = sn.Current;
    Check(cu != null && cu.Heroes.Count == 2 && cu.Heroes[0].Unit == 10 && cu.Heroes[1].Unit == 11, "dps: heroes by damage, biggest first");
    Check(Near(cu.Heroes[0].Avg[0], 3000 / (300 * fs), 1e-9) && Near(cu.Seconds, 300 * fs, 1e-9), "dps: damage / (frames x 270/4096 s)");
    Check(Near(cu.Heroes[0].Share[0], 0.75, 1e-9) && Near(cu.Heroes[1].Share[0], 0.25, 1e-9), "share: 3000 and 1000 = 75% and 25%");
    Check(cu.Kind == DpsKind.Other && cu.Open && !cu.Final && cu.Heroes[0].Base == 7010 && cu.Heroes[0].Hero == 1010, "segment: kind, open, identity of the hero");
    Check(cu.Heroes[0].Share[1] == 0 && cu.Sorted(DpsMode.Damage)[0].Unit == 10, "share of an empty metric is 0 (no NaN)");
    // the window of N seconds
    f = new DpsFeed();
    long d = 0;
    for (uint fr = 15; fr <= 300; fr += 15) { d += 100; f.Push(DT(Other, fr, false, DL(1, 10, d))); }
    double exp = (2000 - 900) / (152 * fs);   // the point at or before frame 300 - 152 is frame 135 (900)
    Check(Near(f.Snapshot.Current.Heroes[0].Recent[0], exp, 1e-6) && f.WindowSec == 10, "window: damage of the last 10 s / the window (" + f.Snapshot.Current.Heroes[0].Recent[0].ToString("0.00") + ")");
    f.WindowSec = 5; f.Push(DT(Other, 315, false, DL(1, 10, d + 100)));
    long w5 = (long)Math.Round(5 / fs), tgt = 315 - w5;   // 76 frames: 240 -> value at <= 239 is frame 225 = 1500
    Check(Near(f.Snapshot.Current.Heroes[0].Recent[0], (2100 - 1500) / (w5 * fs), 1e-6), "window: N = 5 s");
    // a short fight: the window is the fight
    f = new DpsFeed(); f.Push(DT(Other, 60, false, DL(1, 10, 600)));
    Check(Near(f.Snapshot.Current.Heroes[0].Recent[0], f.Snapshot.Current.Heroes[0].Avg[0], 1e-9), "window: a fight shorter than N = the average");
    // a point about a second
    f = new DpsFeed();
    f.Push(DT(Other, 15, false, DL(1, 10, 10))); f.Push(DT(Other, 20, false, DL(1, 10, 20))); f.Push(DT(Other, 29, false, DL(1, 10, 30))); f.Push(DT(Other, 30, false, DL(1, 10, 40)));
    Check(f.Snapshot.Current.Heroes[0].Series.Length == 2 && f.Snapshot.Current.Heroes[0].Value[0] == 40, "step: a reading within a second of the last point is skipped");
    // no change, no point
    f.Push(DT(Other, 45, false, DL(1, 10, 40))); f.Push(DT(Other, 60, false, DL(1, 10, 40)));
    Check(f.Snapshot.Current.Heroes[0].Series.Length == 2 && f.Snapshot.Current.Frames == 60, "points only when a value changes; the clock goes on");
    // monotonic
    f = new DpsFeed();
    f.Push(DT(Other, 15, false, DL(1, 10, 500), DL(1, 11, 100)));
    f.Push(DT(Other, 30, false, DL(1, 10, 400), DL(1, 11, 300)));   // the sum would fall? 700 vs 600: grows, but a hero went back
    var h10 = f.Snapshot.Current.Heroes;
    f.Push(DT(Other, 45, false, DL(1, 10, 100), DL(1, 11, 100)));   // the sum falls: dropped
    Check(f.Snapshot.Rejected == 1 && f.Snapshot.Current.Frames == 30 && f.Snapshot.Current.Heroes[0].Value[0] >= 100, "monotonic: a reading with a smaller sum is dropped");
    f.Push(DT(Other, 60, false, DL(1, 10, 900), DL(1, 11, 300)));
    Check(f.Snapshot.Rejected == 1 && f.Snapshot.Current.Frames == 60, "monotonic: the next good reading goes on");
    f.Push(DT(Other, 75, false, DL(1, 10, -5)));
    Check(f.Snapshot.Rejected == 2 && f.Snapshot.Current.Frames == 60, "a negative value drops the reading");
    // five dropped readings in a row: a new fight
    f = new DpsFeed();
    f.Push(DT(Other, 500, false, DL(1, 10, 9000)));
    for (uint i = 0; i < 4; i++) f.Push(DT(Other, 520 + i * 20, false, DL(1, 10, 10 + i)));
    Check(f.Snapshot.Rejected == 4 && f.Snapshot.Previous == null, "streak: four dropped, still the same fight");
    f.Push(DT(Other, 600, false, DL(1, 10, 50)));
    Check(f.Snapshot.Previous != null && f.Snapshot.Previous.Heroes[0].Value[0] == 9000 && f.Snapshot.Current.Heroes[0].Value[0] == 50, "streak: the fifth starts a new fight");
    // skills: the check against the damage
    f = new DpsFeed();
    Func<uint, bool, long, long, DpsTick> SK = (fr, fin, a, b) => {
      var t = DT(Other, fr, fin, DL(1, 10, 1000 + (long)fr));
      t.Skills = new Dictionary<string, Dictionary<int, long>> { { "1:10", new Dictionary<int, long> { { 101, a }, { 102, b } } } };
      return t;
    };
    f.Push(SK(15, false, 700, 315));   // sum 1015 = damage 1015
    var sk = f.Snapshot.Current.Heroes[0].Skills;
    Check(sk != null && sk.Count == 2 && sk[0].Id == 101 && Near(sk[0].Share, 700 / 1015.0, 1e-9) && f.Snapshot.Current.SkillsOn, "skills: by amount, share of the sum");
    Check(Near(sk[0].Dps, 700 / (15 * fs), 1e-9), "skills: DPS over the fight");
    f.Push(SK(30, false, 710, 330));   // 1040 vs 1030: within the tolerance
    Check(!f.Snapshot.SkillsOff && f.Snapshot.Current.Heroes[0].Skills[0].Amount == 710, "skills: a small gap (read a moment apart) passes");
    f.Push(SK(45, false, 2000, 2000)); f.Push(SK(60, false, 2000, 2000));
    Check(!f.Snapshot.SkillsOff && f.Snapshot.Current.Heroes[0].Skills[0].Amount == 710, "skills: a wrong reading keeps the last good one");
    f.Push(SK(75, false, 2000, 2000));
    Check(f.Snapshot.SkillsOff && f.Snapshot.Current.Heroes[0].Skills == null && !f.Snapshot.Current.SkillsOn, "skills: three errors in a row switch them off for the fight");
    f.Push(SK(90, false, 750, 340));
    Check(f.Snapshot.SkillsOff && f.Snapshot.Current.Heroes[0].Skills == null && f.Snapshot.Current.Heroes[0].Value[0] == 1090, "skills: off stays off; the damage goes on");
    f.Push(DT(Boss, 15, false, DL(1, 10, 100)));
    Check(!f.Snapshot.SkillsOff, "skills: a new fight starts with them on");
    // an exact check at the end
    f = new DpsFeed();
    f.Push(SK(15, false, 700, 315)); f.Push(SK(30, true, 700, 330));   // final: 1030 vs 1030 exact
    Check(f.Snapshot.Current.Heroes[0].Skills != null && f.Snapshot.Current.Final, "skills: a final reading that adds up exactly");
    f = new DpsFeed(); f.Push(SK(15, false, 700, 315)); f.Push(SK(30, true, 700, 331));
    Check(f.Snapshot.Current.Heroes[0].Skills == null && f.Snapshot.Current.Final && f.Snapshot.Current.Heroes[0].Value[0] == 1030, "skills: a final reading 1 off hides the skills, the damage stays");
    // arena side filter
    f = new DpsFeed();
    f.Push(DT(Arena, 30, false, DL(1, 10, 500), DL(2, 10, 9999), DL(2, 11, 800), DL(1, 11, 100)));
    cu = f.Snapshot.Current;
    Check(cu.Kind == DpsKind.Arena && cu.Primary == 1 && cu.Heroes.Count == 2 && cu.Heroes[0].C == 1 && cu.Heroes[0].Value[0] == 500 && cu.Heroes[1].C == 1, "arena: only the lowest controller; the same unit on the other side is not mixed in");
    Check(Near(cu.Heroes[0].Share[0], 500 / 600.0, 1e-9), "arena: shares count the own side only");
    f.Push(DT(6002001, 15, false, DL(1, 10, 1)));
    Check(f.Snapshot.Current.Kind == DpsKind.Arena, "arena: the defence run (6002xxx) too");
    f = new DpsFeed();
    f.Push(DT(Boss, 30, false, DL(1, 10, 500, 400), DL(100, 0, 50)));
    Check(f.Snapshot.Current.Kind == DpsKind.Boss && f.Snapshot.Current.Heroes.Count == 1 && f.Snapshot.Current.Heroes[0].Value[1] == 400, "boss: the monsters' side (controller 100) is left out, to-boss is a metric");
    f = new DpsFeed { AllSides = true }; f.Push(DT(Boss, 30, false, DL(1, 10, 500, 400), DL(100, 0, 50)));
    Check(f.Snapshot.Current.Heroes.Count == 2, "AllSides: every controller shown");
    Check(DpsFeed.KindOf(4809017) == DpsKind.Malrik && DpsFeed.KindOf(5201003) == DpsKind.Boss && DpsFeed.KindOf(4601001) == DpsKind.Boss && DpsFeed.KindOf(Other) == DpsKind.Other && DpsFeed.KindOf(0) == DpsKind.Other, "kinds of fight by stage");
    // metrics
    f = new DpsFeed();
    f.Push(DT(Other, 150, false, DL(1, 10, 100, 60, 30, 20), DL(1, 11, 100, 40, 10, 80)));
    cu = f.Snapshot.Current;
    Check(Near(cu.Heroes[0].Share[2], 0.75, 1e-9) || Near(cu.Heroes[1].Share[2], 0.75, 1e-9), "metrics: heal share");
    Check(cu.Sorted(DpsMode.Taken)[0].Unit == 11 && cu.Sorted(DpsMode.ToBoss)[0].Unit == 10, "metrics: sorted by taken / to boss");
    // a change of the fight
    f = new DpsFeed();
    f.Push(DT(Other, 300, false, DL(1, 10, 1000)));
    f.Push(DT(Other, 330, false, DL(1, 10, 1500)));
    f.Push(DT(Boss, 15, false, DL(1, 10, 70)));
    sn = f.Snapshot;
    Check(sn.Previous != null && sn.Previous.Stage == Other && sn.Previous.Heroes[0].Value[0] == 1500 && sn.Current.Stage == Boss && sn.Current.Heroes[0].Value[0] == 70, "new fight: the old one is the previous segment");
    Check(sn.Total != null && sn.Total.Heroes[0].Value[0] == 1570 && sn.Total.Fights == 2 && sn.Total.Frames == 345, "total: the old fight and the running one together");
    f.Push(DT(Boss, 30, true, DL(1, 10, 100)));
    sn = f.Snapshot;
    Check(sn.Current.Final && !sn.Current.Open && sn.Total.Heroes[0].Value[0] == 1600 && sn.Total.Fights == 2 && !sn.Total.Open, "final: the fight closes, the total takes it once");
    f.Push(DT(Boss, 30, true, DL(1, 10, 100)));
    Check(f.Snapshot.Total.Heroes[0].Value[0] == 1600 && f.Snapshot.Previous.Stage == Other, "final: a second final reading changes nothing");
    f.Push(DT(Other, 15, false, DL(1, 10, 20)));
    sn = f.Snapshot;
    Check(sn.Previous.Stage == Boss && sn.Current.Stage == Other && sn.Total.Heroes[0].Value[0] == 1620 && sn.Total.Fights == 3, "next fight after a final: the finished one is previous, total adds up");
    // the clock going back in the same stage: a new fight
    f.Push(DT(Other, 300, false, DL(1, 10, 400)));
    f.Push(DT(Other, 45, false, DL(1, 10, 30)));
    Check(f.Snapshot.Previous.Heroes[0].Value[0] == 400 && f.Snapshot.Current.Heroes[0].Value[0] == 30, "the frame going back = a new fight");
    // the total merges a hero by (controller, unit) over fights; skills too
    f = new DpsFeed();
    f.Push(SK(15, true, 700, 315));
    f.Push(DT(Other, 10, false, DL(1, 11, 5)));
    var tsk = f.Snapshot.Total;
    Check(tsk.Heroes.Count == 2 && tsk.Heroes[0].Unit == 10 && tsk.Heroes[0].Skills != null && tsk.Heroes[0].Skills[0].Amount == 700, "total: skills of a finished fight carry over");
    // the buffer limit and the thinning
    f = new DpsFeed();
    long dd = 0;
    for (uint fr = 15; fr <= 15 * 6000; fr += 15) { dd += 10; f.Push(DT(Other, fr, false, DL(1, 10, dd))); }
    var ser = f.Snapshot.Current.Heroes[0].Series;
    Check(ser.Length <= DpsFeed.MaxPoints && ser.Length > 1000 && ser[ser.Length - 1][1] == dd && ser[0][0] == 15, "buffer: at most " + DpsFeed.MaxPoints + " points, first and last kept (" + ser.Length + ")");
    bool inc = true; for (int i = 1; i < ser.Length; i++) if (ser[i][0] <= ser[i - 1][0] || ser[i][1] < ser[i - 1][1]) inc = false;
    Check(inc, "buffer: the thinned curve still goes forward");
    // reset
    f.Reset();
    Check(f.Snapshot.Current == null && f.Snapshot.Previous == null && f.Snapshot.Total == null, "reset: nothing left");
    f.Push(null); f.Push(new DpsTick());
    Check(f.Snapshot.Current == null, "empty readings are ignored");
    // no NaN, no negatives
    f = new DpsFeed(); f.Push(DT(Other, 0, false, DL(1, 10, 0)));
    var z = f.Snapshot.Current.Heroes[0];
    Check(z.Avg[0] == 0 && z.Recent[0] == 0 && z.Share[0] == 0, "an empty first reading: zeros, no NaN");
  }

}
