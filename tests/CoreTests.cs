// Tests for the parts of the extractor that do not need Windows: JSON, sync code / site checks,
// game version parsing, progress mapping, config, and the HTTP client against tests/mock-server.mjs.
//
//   CoreTests <mock base url> [account.json to send]
//
// Built by tests/run-tests.sh with the .NET 8 SDK's csc (no NuGet needed).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RealmForge;

static class CoreTests {
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
    Check(PlansClient.RetryableCancel(PlansStatus.Unreachable) && PlansClient.RetryableCancel(PlansStatus.ServerError) && !PlansClient.RetryableCancel(PlansStatus.Ok) && !PlansClient.RetryableCancel(PlansStatus.NotFound) && !PlansClient.RetryableCancel(PlansStatus.InvalidToken), "a cancel to the site is repeated only when the site was not reached / was busy");
    Check(PlansClient.CancelRetryMs == 10000 && PlansClient.CancelRetryTotalMs == 300000, "cancel retries: every 10 s for 5 min");

    // the long poll for new work (src/PlansWait.cs)
    var w1 = PlansWait.Interpret(200, "{\"changed\":true,\"at\":\"2026-10-17T12:00:01.250Z\"}");
    Check(w1.Kind == WaitKind.Ok && w1.Changed && w1.At == "2026-10-17T12:00:01.250Z", "wait: changed + at");
    var w2 = PlansWait.Interpret(200, "{\"changed\":false,\"at\":\"not a date\"}");
    Check(w2.Kind == WaitKind.Ok && !w2.Changed && w2.At == "", "wait: unchanged, bad at dropped");
    Check(PlansWait.Interpret(200, "<html>").Kind == WaitKind.Error && PlansWait.Interpret(200, "").Kind == WaitKind.Error, "wait: junk body");
    Check(PlansWait.Interpret(401, "{}").Kind == WaitKind.Unauthorized && PlansWait.Interpret(429, "{}").Kind == WaitKind.TooMany, "wait: 401 / 429");
    Check(PlansWait.Interpret(404, "{}").Kind == WaitKind.NotDeployed && PlansWait.Interpret(502, "x").Kind == WaitKind.Error, "wait: 404 not deployed, 502 error");
    Check(PollGate.ShouldPoll(false, 0, 0, 0) && PollGate.ShouldPoll(false, 0, 999999, 0) && PollGate.ShouldPoll(false, 0, 0, 77), "poll gate: no game, polls");
    Check(!PollGate.ShouldPoll(true, 0, 0, 77) && !PollGate.ShouldPoll(true, 0, 59999, 77), "poll gate: game up, no player, waits");
    Check(PollGate.ShouldPoll(true, 0, 60000, 77) && PollGate.ShouldPoll(true, 0, 90000, 77), "poll gate: after 60 s polls for the account last read here");
    Check(!PollGate.ShouldPoll(true, 0, 60000, 0) && !PollGate.ShouldPoll(true, 0, 299999, 0) && PollGate.ShouldPoll(true, 0, 300000, 0), "poll gate: no account ever read here, the default account only after 5 min");
    Check(PollGate.ShouldPoll(true, 123, 0, 0) && PollGate.ShouldPoll(true, 123, 0, 77), "poll gate: player known polls at once");
    // the account the plan / sell-list requests name: the one read now, else the one last read on this install, never a guess
    Check(SyncClient.PlanPlayer(123, 77) == 123 && SyncClient.PlanPlayer(0, 77) == 77 && SyncClient.PlanPlayer(0, 0) == 0 && SyncClient.PlanPlayer(-1, 0) == 0, "plan player: read, else last read, else none");
    // the lists are asked again once the game account read differs from the one they were asked for
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
    Check(PlansWait.Backoff(1) == 2000 && PlansWait.Backoff(2) == 4000 && PlansWait.Backoff(3) == 8000 && PlansWait.Backoff(5) == 32000, "backoff: 2 s, 4 s, 8 s ...");
    Check(PlansWait.Backoff(6) == 60000 && PlansWait.Backoff(50) == 60000, "backoff: capped at 60 s");
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
    AutoPilotTests(geo, HH);
    AutoPilotGroupedListTests(geo, HH);

    Console.WriteLine("FilterPilot (the game's gear filter: set + main stat):");
    FilterPilotTests();

    Console.WriteLine("HeroPilot (the plan's hero on the hero screen):");
    HeroPilotTests();
    SellPilotTests();
    FightPlanTests();
    LocateTests();
    OwnButtonTests();
    ConfirmChoiceTests();
    LocatePilotTests();
    GeometryTests();
    GuideShapeTests();
    LocateFoundryPilotTests();
    LocateGemAndListTests();
    SellHintTests();
    ArtifactSellTests();
    CityGateAndPanelTests();
    Console.WriteLine("Offer pop-ups over the city (OfferForms, OfferClose, the window table, the automatic recording):");
    OfferTests();
    PlanScreenTests();
    WindowTableTests();
    UnknownBackTests();
    CityBackTests();
    ArtGridPictureTests();
    ArtPromoteTests();
    ArtAutoTests();
    ArtEquipTests();

    Console.WriteLine("Fight timeline (src/TimelineBuilder.cs: samples of the running fight + its command record):");
    TimelineTests();

    Console.WriteLine("Error reports (src/ErrorReport.cs: opt-in, scrubbed, deduped, capped):");
    ErrorReportTests(mock);

    Console.WriteLine("Window shapes (the game's UI scale: height, or width below 16:9):");
    {
      double W = 1600, H = 1000;   // 16:10, measured on the live game 2026-09-26
      Check(Math.Abs(Ui.Unit(W, H) - 900) < 0.01 && Math.Abs(Ui.Unit(1920, 1009) - 1009) < 0.01, "UI unit: 900 at 1600×1000, the height at 1920×1009");
      var fgx = new FilterGeometry();
      Check(Math.Abs(fgx.X(fgx.MainCloseX, W, H) - 1088) < 4 && Math.Abs(fgx.Y(fgx.CloseY, W, H) - 130) < 4, "filter pop-up × (centred): 1088,130");
      var hgx = new HintGeometry(); var sl = hgx.Slot(0, W, H);
      Check(Math.Abs(sl[0] + sl[2] / 2.0 - 481) < 4 && Math.Abs(sl[1] + sl[3] / 2.0 - 540) < 6, "weapon slot (centred): 481,540");
      var rpx = hgx.Replace(W, H);
      Check(Math.Abs(rpx[0] + rpx[2] / 2.0 - 692) < 6 && Math.Abs(rpx[1] + rpx[3] / 2.0 - 752) < 6, "«Заменить» (top left): 692,752");
      var eqb = hgx.Equip(W, H);
      Check(Math.Abs(eqb[0] + eqb[2] / 2.0 - 896) < 8 && Math.Abs(eqb[1] + eqb[3] / 2.0 - 824) < 8, "«Надеть» of the single card (centred): 896,824");
      var fb = hgx.Filter(W, H);
      Check(Math.Abs(fb[1] + fb[3] / 2.0 - 936) < 6, "«Фильтр» bar (bottom): y 936");
      var hgeo = new HeroGeometry();
      Check(Math.Abs(hgeo.PitchY * 900 - 167) < 2 && Math.Abs(hgeo.Row1Top * 900 - 149) < 3, "hero grid: rows every 167 px from 149");
    }

    ArenaOppTests();
    GuildTests();
    LiveStateTests();
    FormSearchTests();
    HeroFormFollowTests();
    FormSearchPolicyTests();
    LocateAuditTests();
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
    LayoutTests(mock);
    KlassScanTests();
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
      bool all = l39 != null; if (all) foreach (var k in GameLayout.FieldKeys) if (!k.StartsWith("StatisticsComponent.") && !k.StartsWith("SkillDamage") && l39.FieldOffset(k) <= 0) all = false;
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
      if (Path.GetFileName(f) == "Layout.cs") continue;
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
    Eq(CodeResult.Unknown, CodeResult.Classify(null), "promo result: no message is unknown");
    Eq("ru", CodeResult.LanguageOf("\u041d\u0435\u0432\u0435\u0440\u043d\u044b\u0439 \u043a\u043e\u0434"), "promo language: Russian");
    Eq("en", CodeResult.LanguageOf("Invalid code"), "promo language: English");
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
        var sd = RFX.ReadInventory(0);
        Check(sd != null && sd.Open, "forms: the window table lacks the inventory: the scan still finds it");
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

  // the read-only audit of «Показать в игре» (2026-10-29, the lab laptop: one form search ~17 s): slow form searches, the long Ascension list,
  // the inventory's «Литейная» inside the Foundry's «Вознести», the fixed «Массовая продажа» point
  static void LocateAuditTests() {
    Console.WriteLine("Locate audit: slow form searches, waits with a cap, the hidden «Вознести» overlap, the bulk-sale point");
    var invNormal = ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, CityForm = false }, 0);
    var city = ScreenState.Classify(new ScreenInputs { CityForm = true }, 0);
    var foundry = ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, Rs = new RecastScreen { SublimingOpen = true, CurType = 1 } }, 0);
    Func<ClickTarget, ScreenState, ClickRequest> Rq = (t, f) => new ClickRequest { Owner = ClickOwner.Locate, Target = t, AutoOn = true, Front = true };
    // (1) the effect deadline: unchanged without a search ...
    foreach (var hooked in new[] { false, true }) {
      foreach (var tg in new[] { ClickTarget.CityInventory, ClickTarget.FoundryButton }) {
        var g = new ClickGate(); if (hooked) g.FormSearchPending = x => false;
        var before = tg == ClickTarget.CityInventory ? city : invNormal;
        g.Arm(ClickOwner.Locate, tg, ClickGate.Expect(Rq(tg, before), before), 0);
        g.Observe(before, ClickGate.SlowEffectMs - 10); Check(!g.Halted(ClickOwner.Locate), "audit fast: " + tg + " still awaited just inside " + ClickGate.SlowEffectMs + " ms (hook " + hooked + ")");
        g.Observe(before, ClickGate.SlowEffectMs + 20); Eq("nochange", g.HaltReason(ClickOwner.Locate), "audit fast: " + tg + " with no search running: nochange right after " + ClickGate.SlowEffectMs + " ms as before (hook " + hooked + ")");
        g = new ClickGate(); if (hooked) g.FormSearchPending = x => false;
        g.Arm(ClickOwner.Locate, tg, ClickGate.Expect(Rq(tg, before), before), 0);
        g.Observe(tg == ClickTarget.CityInventory ? invNormal : foundry, 900);
        Check(!g.Halted(ClickOwner.Locate) && !g.Pending(ClickOwner.Locate), "audit fast: " + tg + " that shows its effect in time is done (hook " + hooked + ")");
      }
    }
    {   // ... a slow PC: the search started with the click ends at 17 s, the screen is read right after it
      bool busy = true; var g = new ClickGate(); var log = new List<string>(); g.Sink = x => log.Add(x);
      ClickTarget asked = ClickTarget.None; g.FormSearchPending = x => { asked = x; return busy; };
      g.Arm(ClickOwner.Locate, ClickTarget.CityInventory, ClickGate.Expect(Rq(ClickTarget.CityInventory, city), city), 0);
      var unk = new ScreenState();
      long t = 0; for (; t < 17000; t += 100) g.Observe(unk, t);
      Check(!g.Halted(ClickOwner.Locate) && g.Pending(ClickOwner.Locate) && asked == ClickTarget.CityInventory, "audit slow: no stop while the search for the inventory runs (17 s > " + ClickGate.SlowEffectMs + " ms)");
      Check(g.Preflight(ClickOwner.Locate, unk, t).Reason == "pending", "audit slow: ... the pilot waits for it (pending), no new click");
      busy = false; g.Observe(unk, t); g.Observe(invNormal, t + 200);
      Check(!g.Halted(ClickOwner.Locate) && !g.Pending(ClickOwner.Locate), "audit slow: the inventory read right after the search ended = the click's effect");
      int said = 0; foreach (var l in log) if (l.Contains("form search still runs")) said++;
      Eq(1, said, "audit slow: the wait for the search is logged once");
    }
    {   // ... the search ends and finds nothing: a stop a moment later (its result read once)
      bool busy = true; var g = new ClickGate(); g.FormSearchPending = x => busy;
      g.Arm(ClickOwner.Locate, ClickTarget.FoundryButton, ClickGate.Expect(Rq(ClickTarget.FoundryButton, invNormal), invNormal), 0);
      long t = 0; for (; t < 12000; t += 100) g.Observe(invNormal, t);
      busy = false;
      for (; t <= 12000 + ClickGate.SearchGraceMs - 100; t += 100) g.Observe(invNormal, t);
      Check(!g.Halted(ClickOwner.Locate), "audit slow: the search's end is followed by " + ClickGate.SearchGraceMs + " ms for its result");
      for (; t <= 12000 + ClickGate.SearchGraceMs + 300 && !g.Halted(ClickOwner.Locate); t += 100) g.Observe(invNormal, t);
      Eq("nochange", g.HaltReason(ClickOwner.Locate), "audit slow: ... then nochange (the search found no Foundry)");
      // a later search does not extend again
      busy = true; g = new ClickGate(); g.FormSearchPending = x => busy;
      g.Arm(ClickOwner.Locate, ClickTarget.FoundryButton, ClickGate.Expect(Rq(ClickTarget.FoundryButton, invNormal), invNormal), 0);
      for (t = 0; t < 8000; t += 100) g.Observe(invNormal, t);
      busy = false; for (; t < 8000 + ClickGate.SearchGraceMs - 200; t += 100) g.Observe(invNormal, t);
      busy = true; for (; t < 12000 && !g.Halted(ClickOwner.Locate); t += 100) g.Observe(invNormal, t);
      Check(g.HaltReason(ClickOwner.Locate) == "nochange" && t <= 8000 + ClickGate.SearchGraceMs + 200, "audit slow: a second search does not move the deadline again (stop at " + t + " ms)");
    }
    {   // ... the form was FOUND by the search: the screen is awaited up to the cap (lab 22:40: found 1 s after the deadline, Foundry read 6 s later)
      foreach (var tg in new[] { ClickTarget.FoundryButton, ClickTarget.CityInventory }) {
        bool busy = true, found = false; var g = new ClickGate(); g.FormSearchPending = x => busy; g.FormFound = x => found;
        var bf = tg == ClickTarget.CityInventory ? city : invNormal; var unk = new ScreenState();
        g.Arm(ClickOwner.Locate, tg, ClickGate.Expect(Rq(tg, bf), bf), 0);
        long t = 0; for (; t < 9000; t += 100) g.Observe(unk, t);
        busy = false; found = true;
        for (; t < 20000; t += 100) g.Observe(unk, t);
        Check(!g.Halted(ClickOwner.Locate), "audit found: " + tg + " still awaited long after the search ended with the form found");
        g.Observe(tg == ClickTarget.CityInventory ? invNormal : foundry, t);
        Check(!g.Halted(ClickOwner.Locate) && !g.Pending(ClickOwner.Locate), "audit found: " + tg + " done when the screen is read");
        g = new ClickGate(); g.FormSearchPending = x => busy; g.FormFound = x => true; busy = true;
        g.Arm(ClickOwner.Locate, tg, ClickGate.Expect(Rq(tg, bf), bf), 0);
        for (t = 0; t < 9000; t += 100) g.Observe(unk, t);
        busy = false;
        for (; t <= ClickGate.SearchCapMs + 200 && !g.Halted(ClickOwner.Locate); t += 100) g.Observe(unk, t);
        Check(g.HaltReason(ClickOwner.Locate) == "nochange" && t <= ClickGate.SearchCapMs + 200, "audit found: " + tg + " stops at the cap when the screen never shows");
      }
      // no search ran: a found form does not extend the fast path
      var gf = new ClickGate(); gf.FormSearchPending = x => false; gf.FormFound = x => true;
      gf.Arm(ClickOwner.Locate, ClickTarget.FoundryButton, ClickGate.Expect(Rq(ClickTarget.FoundryButton, invNormal), invNormal), 0);
      gf.Observe(invNormal, ClickGate.SlowEffectMs + 20);
      Eq("nochange", gf.HaltReason(ClickOwner.Locate), "audit found: no search ran = the fast path is unchanged");
    }
    {   // ... the cap
      var g = new ClickGate(); g.FormSearchPending = x => true;
      g.Arm(ClickOwner.Locate, ClickTarget.CityInventory, ClickGate.Expect(Rq(ClickTarget.CityInventory, city), city), 0);
      long t = 0; for (; t < ClickGate.SearchCapMs - 100; t += 100) g.Observe(city, t);
      Check(!g.Halted(ClickOwner.Locate), "audit slow: waited while the search runs, up to the cap");
      for (; t <= ClickGate.SearchCapMs + 200 && !g.Halted(ClickOwner.Locate); t += 100) g.Observe(city, t);
      Check(g.HaltReason(ClickOwner.Locate) == "nochange" && t <= ClickGate.SearchCapMs + 200, "audit slow: ... a stop at the cap (" + ClickGate.SearchCapMs + " ms) even with a search running");
      Check(ClickGate.SearchCapMs <= 30000, "audit slow: the cap is at most 30 s");
    }
    foreach (var tg in new[] { ClickTarget.InvEquipTab, ClickTarget.AscendTab, ClickTarget.ArtWorkshop }) {   // only the two clicks that open a searched-for form
      var g = new ClickGate(); g.FormSearchPending = x => true; g.ArtLocateAuto = true;
      g.Arm(ClickOwner.Locate, tg, s => false, 0);
      g.Observe(invNormal, (tg == ClickTarget.ArtWorkshop ? ClickGate.SlowEffectMs : ClickGate.EffectMs) + 20);
      Eq("nochange", g.HaltReason(ClickOwner.Locate), "audit slow: " + tg + " is not extended by a search");
    }
    {   // the search window of the pilot's open click
      int gap; string w;
      w = FormSearchPolicy.Want(ScreenKind.Inventory, false, 6000, true, out gap);
      Check(w != null && gap == FormSearchPolicy.OwnOpenGapMs, "audit policy: the form not found yet: searches go on after 6 s");
      w = FormSearchPolicy.Want(ScreenKind.Inventory, false, FormSearchPolicy.SlowOpenWindowMs, true, out gap);
      Check(w == null, "audit policy: ... up to the cap only");
      w = FormSearchPolicy.Want(ScreenKind.Inventory, false, 6000, false, out gap);
      Check(w == null, "audit policy: the form found (a fast PC): the 6 s window as before");
      Check(FormSearchPolicy.Want(ScreenKind.City, false, 6000, out gap) == null && FormSearchPolicy.Want(ScreenKind.City, false, 5999, out gap) != null, "audit policy: the old overload is unchanged");
    }
    // (5) «Литейная» needs a read where the Foundry is NOT open
    {
      string gp = Path.Combine("tests", "fixtures", "screens", "_geometry.json");
      if (File.Exists(gp)) {
        var geo = GameGeometry.Parse(File.ReadAllText(gp, Encoding.UTF8), "1.6.39.829.1");
        foreach (var wh in new[] { new[] { 1920.0, 1009 }, new[] { 1366.0, 705 }, new[] { 1920.0, 1080 } }) {   // 16:9-ish (on ultrawide the two part)
          double W = wh[0], H = wh[1], U = Ui.Unit(W, H);
          var wk = geo.Rect("inventory.workshop", W, H, U); var asc = geo.Rect("ascend.button", W, H, U);
          Check(wk != null && asc != null && wk.CX >= asc.X && wk.CX <= asc.Right && wk.CY >= asc.Y && wk.CY <= asc.Bottom, "audit overlap: at " + W + "x" + H + " the inventory's «Литейная» centre lies inside the Foundry's «Вознести» (why the Foundry must read closed)");
        }
      } else Console.WriteLine("  (skip: no " + gp + ")");
      var g = new ClickGate();
      Check(g.Check(Rq(ClickTarget.FoundryButton, invNormal), invNormal, 0).Allowed, "audit foundry: the first open (the Foundry never built: nothing to read) is allowed as before");
      var invRsClosed = ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, Rs = new RecastScreen { SublimingOpen = false } }, 0);
      Check(new ClickGate().Check(Rq(ClickTarget.FoundryButton, invRsClosed), invRsClosed, 0).Allowed, "audit foundry: the Foundry read closed -> allowed");
      var rf = new ClickGate().Check(Rq(ClickTarget.FoundryButton, foundry), foundry, 0);
      Check(rf.Verdict == GateVerdict.Deny && rf.Reason == "screen", "audit foundry: the Foundry read open -> never (the click would be «Вознести»)");
      g = new ClickGate(); var rq = Rq(ClickTarget.FoundryButton, invNormal); rq.FoundryUnread = true;
      var r1 = g.Check(rq, invNormal, 0);
      Check(r1.Verdict == GateVerdict.Wait && r1.Reason == "unknown", "audit foundry: built before but not readable now -> wait (it may be open unseen)");
      var r2 = g.Check(rq, invNormal, ClickGate.SearchCapMs - 100);
      Check(r2.Verdict == GateVerdict.Wait, "audit foundry: ... waited for while a search can find it");
      rq.FoundryUnread = false; Check(g.Check(rq, invRsClosed, ClickGate.SearchCapMs - 50).Allowed, "audit foundry: ... read closed again -> allowed");
      g = new ClickGate(); rq = Rq(ClickTarget.FoundryButton, invNormal); rq.FoundryUnread = true;
      g.Check(rq, invNormal, 0); var r3 = g.Check(rq, invNormal, ClickGate.SearchCapMs + 100);
      Check(r3.Verdict == GateVerdict.Deny && g.HaltReason(ClickOwner.Locate) == "unknown", "audit foundry: ... unreadable past the cap -> stop");
      g.Resume(ClickOwner.Locate); rq.FoundryUnread = true; Check(g.Check(rq, invNormal, ClickGate.SearchCapMs + 200).Verdict == GateVerdict.Wait, "audit foundry: a new request starts the wait over");
      foreach (var tg in new[] { ClickTarget.InvListCell, ClickTarget.InvEquipTab, ClickTarget.CityInventory }) {
        var rr = Rq(tg, invNormal); rr.FoundryUnread = true;
        var gr = new ClickGate().Check(rr, tg == ClickTarget.CityInventory ? city : invNormal, 0);
        Check(gr.Reason != "unknown", "audit foundry: " + tg + " does not look at the Foundry flag");
      }
    }
    // (6) the fixed «Массовая продажа» point (app/Overlay.cs TickSell: 0.5867 U, H - 0.0723 U). In bulk-sale mode the game shows (Panel_BackpackEquipPanel
    // _RefreshBottomButtons, state BulkSales) the left group's «Назад» only, and in the right group «Быстрый выбор» + «Продать» (m_BtnSell, from the right edge:
    // x R-0.7761..R-0.5125, y B-0.1124..B-0.0282 U; «Быстрый выбор» R-1.049..R-0.7855): nothing at the point, so no never-press zone is needed there.
    {
      string gp = Path.Combine("tests", "fixtures", "screens", "_geometry.json");
      if (File.Exists(gp)) {
        var geo = GameGeometry.Parse(File.ReadAllText(gp, Encoding.UTF8), "1.6.39.829.1");
        foreach (var wh in new[] { new[] { 1920.0, 1009 }, new[] { 1366.0, 705 }, new[] { 1280.0, 800 }, new[] { 2560.0, 1080 }, new[] { 3440.0, 1440 } }) {
          double W = wh[0], H = wh[1], U = Ui.Unit(W, H), x = 0.5867 * U, y = H - 0.0723 * U;
          var bs = geo.Rect("inventory.bulkSell", W, H, U); var back = geo.Rect("inventory.bulkBack", W, H, U);
          Check(bs != null && x >= bs.X && x <= bs.Right && y >= bs.Y && y <= bs.Bottom, "audit bulk point: at " + W + "x" + H + " it is on «Массовая продажа» (normal mode)");
          Check(back != null && x > back.Right, "audit bulk point: ... right of the bulk mode's «Назад»");
          double sellX0 = W - 0.7761 * U, quickX0 = W - 1.049 * U;
          Check(x < quickX0 && x < sellX0, "audit bulk point: ... left of «Быстрый выбор» / «Продать» of the bulk mode (" + (int)x + " < " + (int)quickX0 + ")");
        }
      } else Console.WriteLine("  (skip: no " + gp + ")");
    }
    // (3) the pilot never waits forever: a list that does not load, an item not in the list, a filter that hides it
    {
      var item = LocItem(777, 3, 720100, 12, 5, 7, 9, 3);
      Func<LocateKind, long, LocateView> V = (k, now) => new LocateView { NowMs = now, Foreground = true, W = 2000, H = 1000, U = 1000, Step = new LocateStep { Kind = k }, Cols = 8, Item = item,
        Rs = new RecastScreen { SublimingOpen = true, AscendShown = true, CurType = 2 }, OtherFormOpen = k != LocateKind.HiddenFilter, Inv = new InventoryScreen { Open = true, EquipTab = true } };
      foreach (var c in new[] { new object[] { LocateKind.AscendLoading, LocatePilot.LoadingCapMs, "loading" }, new object[] { LocateKind.AscendNotInList, LocatePilot.SettledMs, "notinlist" }, new object[] { LocateKind.HiddenFilter, LocatePilot.SettledMs, "hidden" } }) {
        var k = (LocateKind)c[0]; int cap = (int)c[1]; string why = (string)c[2];
        var p = new LocatePilot(); LocateMove m = null; long t = 1000;
        for (; t < 1000 + cap; t += 200) { m = p.Step(V(k, t)); if (m.State != "hint") break; }
        Check(m.State == "hint" && !p.Stopped, "audit wait: " + k + " is a hint (waited for) up to " + cap + " ms");
        m = p.Step(V(k, 1000 + cap));
        Check(m.State == "stopped" && m.Why == why && p.Reason == why && m.Action.Kind == AutoKind.None, "audit wait: " + k + " -> a stop with the reason '" + why + "' after " + cap + " ms");
        p.Reset(); Check(!p.Stopped && p.Step(V(k, 1000 + cap + 100)).State == "hint", "audit wait: " + k + ": a new request starts over");
        p = new LocatePilot(); p.Step(V(k, 0)); m = p.Step(V(k, cap + 100));
        Check(m.State == "hint", "audit wait: " + k + ": a gap (the gate held the pilot) starts the clock over");
      }
      {   // a list rebuilt after the slot tab says "not in the list" for a moment: no stop
        var p = new LocatePilot();
        for (long t = 0; t < 1200; t += 200) p.Step(V(LocateKind.AscendNotInList, t));
        p.Step(V(LocateKind.AscendLoading, 1200));
        for (long t = 1400; t < 1400 + LocatePilot.SettledMs - 200; t += 200) p.Step(V(LocateKind.AscendNotInList, t));
        Check(!p.Stopped, "audit wait: a short 'not in the list' (each run under " + LocatePilot.SettledMs + " ms) is no stop");
      }
      {   // the normal path keeps going: a step the pilot clicks is not timed
        var p = new LocatePilot(); var v = V(LocateKind.OpenAscendTab, 0); v.Rs = new RecastScreen { SublimingOpen = true, CurType = 0 }; v.Geometry = GameGeometry.Parse(GeoJson(), GeoGame);
        var m = p.Step(v); Check(m.State == "work" && m.Todo == LocateTodo.AscendTab, "audit wait: a clickable step works as before");
        v.NowMs = LocatePilot.LoadingCapMs + 10; m = p.Step(v); Check(!p.Stopped, "audit wait: ... and is never stopped by the wait caps");
      }
    }
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

  // A grouped, short gear list (the sub stats filter: a section title, then item rows; live report 2026-10-18, Guan Yu, row 6 of 6):
  // a drag moves the list with the pointer but not past its ends; a click on an item selects it, on the selected one again clears the
  // selection, on nothing changes nothing. The host re-anchors only from a newly selected item (no scroll reading).
  static string GroupedListRun(ListGeometry geo, double H, int[] types, int targetRow, int targetCol, bool barHit, double gain, bool exactScroll, out int drags, out int clicks) {
    int n = types.Length - 1;
    double pitch = geo.PitchY * H, cell = geo.CellW * H, bar = geo.BarBottom * H;
    double topMax = ScrollAnchor.TopRow1(geo, H);
    double topMin = Math.Min(topMax, geo.ViewBottom * H - ListGeometry.TopPad * H - geo.RowOffset(types, types.Length) * H);
    double top = topMax;
    long target = 9000 + targetRow * 10 + targetCol;
    Func<double, double, long> itemAt = (x, y) => {
      int col = geo.ColumnAt(x / H); if (col == 0) return 0;
      if (y < geo.ViewTop * H || y > geo.ViewBottom * H) return 0;
      for (int r = 1; r <= n; r++) {
        if (types[r] != 1) continue;
        double rt = top + geo.RowOffset(types, r) * H;
        if (y >= rt && y <= rt + (barHit ? bar : cell)) return 9000 + r * 10 + col;
      }
      return 0;
    };
    var p = new AutoPilot(geo); var anchor = new ScrollAnchor(); long sel = 0; long now = 0; drags = clicks = 0;
    for (int tick = 0; tick < 600 && sel != target && p.State != "failed"; tick++, now += 100) {
      if (exactScroll) { anchor.Row1Top = top; anchor.Has = true; }
      var a = p.Step(new AutoView { NowMs = now, Foreground = true, H = H, Target = target, Row = targetRow, Col = targetCol, Sel = sel,
                                    AnchorHas = anchor.Has, Row1Top = anchor.Row1Top, Types = types });
      if (a.Kind == AutoKind.Click) {
        clicks++;
        long hit = itemAt(a.X, a.Y);
        if (hit != 0 && hit == sel) sel = 0;
        else if (hit != 0) {
          sel = hit; int row = (int)((hit - 9000) / 10);
          anchor.FromClick(geo, types, row, a.Y, H, 1);
          anchor.Track(geo, types, row, top + geo.RowOffset(types, row) * H, H);
        }
      } else if (a.Kind == AutoKind.Drag) {
        drags++;
        top = Math.Max(topMin, Math.Min(topMax, top + a.DY * gain));
      }
    }
    p.Step(new AutoView { NowMs = now, Foreground = true, H = H, Target = target, Row = targetRow, Col = targetCol, Sel = sel, AnchorHas = anchor.Has, Row1Top = anchor.Row1Top, Types = types });
    return sel == target ? "ok" : p.State;
  }

  static void AutoPilotGroupedListTests(ListGeometry geo, double H) {
    foreach (int rows in new[] { 6, 7, 8, 10 }) {
      foreach (bool barHit in new[] { false, true }) foreach (bool exact in new[] { false, true }) foreach (double gain in new[] { 0.93, 1.0 }) {
        var types = new int[rows + 1]; types[1] = 2; for (int i = 2; i <= rows; i++) types[i] = 1;
        int d, c; string r = GroupedListRun(geo, H, types, rows, 2, barHit, gain, exact, out d, out c);
        Check(r == "ok", "grouped list, " + rows + " rows, target in the last row, bar " + barHit + ", exact scroll " + exact + ", gain " + gain + ": " + r + " (" + d + " drags, " + c + " clicks)");
      }
    }
    // two sections (full / partial match) with an empty-section row between, the item in the last row
    var sec = new[] { 0, 2, 1, 1, 2, 1, 1, 1, 3, 2, 1, 1 };
    foreach (bool barHit in new[] { false, true }) foreach (bool exact in new[] { false, true }) {
      int d, c; string r = GroupedListRun(geo, H, sec, 11, 2, barHit, 0.93, exact, out d, out c);
      Check(r == "ok", "grouped list with three sections, target in the last row, bar " + barHit + ", exact scroll " + exact + ": " + r + " (" + d + " drags, " + c + " clicks)");
    }
  }

  // A simulated game list: 40 item rows; a drag moves it with the pointer (gain 0.93); clicking a cell selects the item there.
  static void AutoPilotTests(ListGeometry geo, double H) {
    var types = new int[41]; for (int i = 1; i <= 40; i++) types[i] = 1;
    double pitch = geo.PitchY * H, cell = geo.CellW * H;
    double top = ScrollAnchor.TopRow1(geo, H), maxScroll = 36 * pitch;   // real row 1 top on the screen
    int targetRow = 23, targetCol = 2; long target = 9023;
    Func<double, double, long> itemAt = (x, y) => {
      int col = geo.ColumnAt(x / H); if (col == 0) return 0;
      int row = (int)Math.Floor((y - top) / pitch) + 1;
      double inRow = y - (top + (row - 1) * pitch);
      return row >= 1 && row <= 40 && inRow <= cell ? 9000 + row + (col == targetCol && row == targetRow ? 0 : col * 100) : 0;
    };
    var p = new AutoPilot(geo);
    var anchor = new ScrollAnchor(); long sel = 0; long now = 0; int clicks = 0, drags = 0; double gain = 0.93;   // the list lags the pointer a little
    for (int tick = 0; tick < 400 && sel != target; tick++, now += 100) {
      var a = p.Step(new AutoView { NowMs = now, Foreground = true, H = H, Target = target, Row = targetRow, Col = targetCol, Sel = sel,
                                    AnchorHas = anchor.Has, Row1Top = anchor.Row1Top, Types = types });
      if (a.Kind != AutoKind.None && Environment.GetEnvironmentVariable("AP_DEBUG") != null) Console.WriteLine("    t=" + now + " " + a.Kind + " x=" + a.X.ToString("0") + " y=" + a.Y.ToString("0") + " dy=" + a.DY.ToString("0") + " top=" + top.ToString("0") + " anchor=" + anchor.Row1Top.ToString("0") + " sel=" + sel);
      if (a.Kind == AutoKind.Click) {
        clicks++;
        long hit = itemAt(a.X, a.Y);
        if (hit != 0 && hit != sel) {
          sel = hit; int row = (int)(hit % 100);
          anchor.FromClick(geo, types, row, a.Y, H, 1);
          anchor.Track(geo, types, row, top + (row - 1) * pitch, H);   // the stat bars on the screen snap it exactly
        }
      } else if (a.Kind == AutoKind.Drag) {
        drags++;
        Check(a.Y >= geo.ViewTop * H && a.Y + a.DY <= geo.ViewBottom * H && a.Y + a.DY >= geo.ViewTop * H, "drag stays over the list");
        top = Math.Max(ScrollAnchor.TopRow1(geo, H) - maxScroll, Math.Min(ScrollAnchor.TopRow1(geo, H), top + a.DY * gain));
      }
    }
    p.Step(new AutoView { NowMs = now, Foreground = true, H = H, Target = target, Row = targetRow, Col = targetCol, Sel = sel, AnchorHas = anchor.Has, Row1Top = anchor.Row1Top, Types = types });
    Eq(target, sel, "the target item gets selected (row 23 of 40, list dragged)");
    Check(drags <= 7 && clicks <= 10, "few drags and clicks (" + drags + " drags, " + clicks + " clicks)");
    Check(Math.Abs(p.Gain - gain) < 0.05, "drag gain measured: " + p.Gain.ToString("0.00"));
    Eq("done", p.State, "state after the selection");

    // the player uses the mouse: the pilot waits; background window: nothing happens
    var q = new AutoPilot(geo);
    var busy = q.Step(new AutoView { NowMs = 0, Foreground = true, UserBusy = true, H = H, Target = 5, Row = 1, Col = 1, Types = types, AnchorHas = true, Row1Top = ScrollAnchor.TopRow1(geo, H) });
    Check(busy.Kind == AutoKind.None && q.State == "paused", "mouse in use -> pause");
    var bg = q.Step(new AutoView { NowMs = 100, Foreground = false, H = H, Target = 5, Row = 1, Col = 1, Types = types, AnchorHas = true, Row1Top = ScrollAnchor.TopRow1(geo, H) });
    Check(bg.Kind == AutoKind.None && q.State == "background", "game not in front -> nothing");
    var click = q.Step(new AutoView { NowMs = 200, Foreground = true, H = H, Target = 5, Row = 1, Col = 1, Types = types, AnchorHas = true, Row1Top = ScrollAnchor.TopRow1(geo, H) });
    Check(click.Kind == AutoKind.Click && Math.Abs(click.Y - (ScrollAnchor.TopRow1(geo, H) + cell / 2)) < 0.01, "visible item -> click its centre");
    var user = q.Step(new AutoView { NowMs = 1200, Foreground = true, UserClicked = true, H = H, Target = 5, Row = 1, Col = 1, Types = types, AnchorHas = true, Row1Top = ScrollAnchor.TopRow1(geo, H) });
    Check(user.Kind == AutoKind.None && q.State == "paused", "the player clicked himself -> the pilot steps back");

    // slot: click its centre, give up after 3 tries
    var r = new AutoPilot(geo); int slotClicks = 0;
    for (long t = 0; t < 10000; t += 100) {
      var a = r.Step(new AutoView { NowMs = t, Foreground = true, H = H, Slot = new[] { 100, 200, 60, 60 } });
      if (a.Kind == AutoKind.Click) { slotClicks++; Check(a.X == 130 && a.Y == 230, "slot centre"); }
    }
    Eq(3, slotClicks, "slot: 3 tries");
    Eq("slot_failed", r.State, "slot that does not open (closed until a story stage) -> slot_failed");

    // the end of the list: a drag does not move it -> give up instead of dragging forever
    var e = new AutoPilot(geo); double stuckTop = ScrollAnchor.TopRow1(geo, H); int w2 = 0;
    for (long t = 0; t < 30000 && e.State != "failed"; t += 100) {
      var a = e.Step(new AutoView { NowMs = t, Foreground = true, H = H, Target = 7, Row = 30, Col = 1, Sel = 1, Types = types, AnchorHas = true, Row1Top = stuckTop });
      if (a.Kind == AutoKind.Drag) w2++;
    }
    Check(e.State == "failed" && w2 <= 4, "list does not scroll -> failed after " + w2 + " drags");

    {   // live 1.6.66 (3440x1440 and 1920x1051): after the filter the list is short and sits at its top, the scroll read says row 1 is
        // at -223 px (stale): the item at row 2 looks above the view, the drags down do nothing -> "the list does not move". The list
        // is taken at its top then, and the item is clicked there.
      var st = new[] { 0, 1, 1, 1 }; double pitchS = geo.PitchY * H, cellS = geo.CellW * H, realTop = ScrollAnchor.TopRow1(geo, H), stale = realTop - 3.4 * pitchS;
      long tgt = 9021; long selS = 0; int dragsS = 0;
      Func<double, double, long> at = (x, y) => {
        int col = geo.ColumnAt(x / H); if (col == 0) return 0;
        int row = (int)Math.Floor((y - realTop) / pitchS) + 1; double inRow = y - (realTop + (row - 1) * pitchS);
        return row >= 1 && row <= 3 && inRow <= cellS ? 9000 + row * 10 + col : 0;
      };
      var ps = new AutoPilot(geo);
      for (long t = 0; t < 30000 && selS != tgt && ps.State != "failed"; t += 100) {
        var a = ps.Step(new AutoView { NowMs = t, Foreground = true, H = H, Target = tgt, Row = 2, Col = 1, Sel = selS, SelRow = selS > 0 ? (int)((selS - 9000) / 10) : 0,
                                       AnchorHas = true, Row1Top = stale, Types = st });
        if (a.Kind == AutoKind.Drag) dragsS++;
        else if (a.Kind == AutoKind.Click) { long hit = at(a.X, a.Y); if (hit != 0) selS = hit == selS ? 0 : hit; }
      }
      Check(selS == tgt, "stale scroll read on a short list at its top: the item is clicked after " + dragsS + " drags (" + ps.State + ", " + ps.Why + ")");
    }

    // as in the live test: row 119 of 130, column 3 (the wheel could not get there at all)
    var lt = new int[131]; for (int i = 1; i <= 130; i++) lt[i] = 1;
    double ltop = ScrollAnchor.TopRow1(geo, H), lmax = 126 * pitch; long lsel = 0; int ldrags = 0, lclicks = 0;
    var la = new ScrollAnchor(); var lp = new AutoPilot(geo);
    Func<double, double, long> lat = (x, y) => {
      int col = geo.ColumnAt(x / H); if (col == 0) return 0;
      int row = (int)Math.Floor((y - ltop) / pitch) + 1;
      double inRow = y - (ltop + (row - 1) * pitch);
      return row >= 1 && row <= 130 && inRow <= cell ? (col == 3 && row == 119 ? 7119 : 10000 * col + row) : 0;
    };
    long lt0 = 0;
    for (; lt0 < 300000 && lsel != 7119 && lp.State != "failed"; lt0 += 100) {
      var a = lp.Step(new AutoView { NowMs = lt0, Foreground = true, H = H, Target = 7119, Row = 119, Col = 3, Sel = lsel,
                                     AnchorHas = la.Has, Row1Top = la.Row1Top, Types = lt });
      if (a.Kind == AutoKind.Click) {
        lclicks++;
        long hit = lat(a.X, a.Y);
        if (hit != 0 && hit != lsel) {
          lsel = hit; int row = hit == 7119 ? 119 : (int)(hit % 10000);
          la.FromClick(geo, lt, row, a.Y, H, 1);
          la.Track(geo, lt, row, ltop + (row - 1) * pitch, H);
        }
      } else if (a.Kind == AutoKind.Drag) {
        ldrags++;
        ltop = Math.Max(ScrollAnchor.TopRow1(geo, H) - lmax, Math.Min(ScrollAnchor.TopRow1(geo, H), ltop + a.DY * 0.93));
      }
    }
    Eq(7119, lsel, "row 119 of 130 reached by dragging (" + ldrags + " drags, " + lclicks + " clicks, " + (lt0 / 1000) + " s)");
    Check(ldrags < AutoPilot.DragTries, "within the drag budget");

    // «Заменить» (auto-confirm): once, only for the selected item on the plan's hero screen, never on an item that hero wears
    int[] rep = { 900, 600, 280, 40 };
    double row1 = ScrollAnchor.TopRow1(geo, H);
    Func<long, long, long, int[], AutoView> cv = (t, owner, screen, button) => new AutoView {
      NowMs = t, Foreground = true, H = H, Target = 5, Row = 1, Col = 1, Sel = 5, Types = types, AnchorHas = true, Row1Top = row1,
      AutoConfirm = button != null, Button = button, Hero = 77, ScreenHero = screen, Owner = owner };
    var c1 = new AutoPilot(geo); int presses = 0; AutoAction first = null;
    for (long t = 0; t < 10000; t += 100) {
      var a = c1.Step(cv(t, 0, 77, rep));
      if (a.Kind == AutoKind.Click) { presses++; if (first == null) { first = a; Check(t >= AutoPilot.ConfirmSettleMs, "waits for the item card (" + t + " ms)"); } }
    }
    Check(first != null && first.X == 1040 && first.Y == 620, "presses the centre of «Заменить»");
    Check(WebViewRecovery.IsUiaMissing(new DllNotFoundException("UIAutomationCore.dll assembly:<unknown assembly> type:<unknown type> member:(null)")) && WebViewRecovery.IsUiaMissing(new AggregateException(new InvalidOperationException("x", new DllNotFoundException("Unable to load UIAutomationCore.dll")))) && !WebViewRecovery.IsUiaMissing(new DllNotFoundException("WebView2Loader.dll")) && !WebViewRecovery.IsUiaMissing(new InvalidOperationException("UIAutomationCore")) && !WebViewRecovery.IsUiaMissing(null), "a missing UIAutomationCore.dll is recognised (only that one)");
    Check(SyncClient.IsConnectionClosed(new WebException("x", WebExceptionStatus.KeepAliveFailure)) && SyncClient.IsConnectionClosed(new WebException("Базовое соединение закрыто", WebExceptionStatus.ConnectionClosed))
          && SyncClient.IsConnectionClosed(new Exception("outer", new WebException("y", WebExceptionStatus.KeepAliveFailure)))
          && SyncClient.IsConnectionClosed(new System.IO.IOException("reset", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)))
          && !SyncClient.IsConnectionClosed(new WebException("t", WebExceptionStatus.Timeout)) && !SyncClient.IsConnectionClosed(new WebException("n", WebExceptionStatus.NameResolutionFailure))
          && !SyncClient.IsConnectionClosed(new WebException("The underlying connection was closed", WebExceptionStatus.TrustFailure)) && !SyncClient.IsConnectionClosed(new Exception("kept alive")) && !SyncClient.IsConnectionClosed(null),
          "sync: a closed idle connection is told by the status (not the localized text) and retried; timeouts and other failures are not");
    Eq(1 + AutoPilot.ConfirmRetries, presses, "one press per item, one more when it did not go on, then it stops");
    Eq("unconfirmed", c1.State, "did not go on twice -> unconfirmed, the player looks");
    Check(c1.Trace.StartsWith("confirm "), "the confirm press is traced for the journal (" + c1.Trace + ")");

    var c2 = new AutoPilot(geo); int p2 = 0;
    for (long t = 0; t < 3000; t += 100) if (c2.Step(cv(t, 77, 77, rep)).Kind != AutoKind.None) p2++;
    Check(p2 == 0 && c2.State == "done", "the hero already wears it (the button would be «Снять») -> never pressed");

    var c3 = new AutoPilot(geo); int p3 = 0;
    for (long t = 0; t < 3000; t += 100) if (c3.Step(cv(t, 0, 12, rep)).Kind != AutoKind.None) p3++;
    Check(p3 == 0 && c3.State == "work", "another hero's screen in the game -> waits, no press");

    var c4 = new AutoPilot(geo); int p4 = 0;
    for (long t = 0; t < 3000; t += 100) if (c4.Step(cv(t, -1, 77, rep)).Kind != AutoKind.None) p4++;
    Check(p4 == 0, "owner unreadable -> no press");

    var c5 = new AutoPilot(geo); int p5 = 0;
    for (long t = 0; t < 3000; t += 100) if (c5.Step(cv(t, 0, 77, null)).Kind != AutoKind.None) p5++;
    Check(p5 == 0 && c5.State == "done", "auto-confirm off -> the player presses «Заменить»");

    var c6 = new AutoPilot(geo); long owner6 = 0; int p6 = 0;
    for (long t = 0; t < 10000; t += 100) {
      var a = c6.Step(cv(t, owner6, 77, rep));
      if (a.Kind == AutoKind.Click) { p6++; owner6 = 77; }   // the server puts it on
    }
    Check(p6 == 1 && c6.State == "done", "pressed once, the item goes on -> done (the guide moves to the next slot)");

    // the button is not on the screen yet (the card is opening): wait, then press the one that appears («Надеть» here)
    var c7 = new AutoPilot(geo); int p7 = 0; AutoAction a7 = null; int[] equipBtn = { 980, 640, 190, 50 };
    for (long t = 0; t < 4000; t += 100) {
      var v7 = cv(t, 0, 77, rep); v7.Button = t < 2000 ? null : equipBtn;
      var a = c7.Step(v7);
      if (a.Kind == AutoKind.Click) { p7++; a7 = a; Check(t >= 2000, "no press before the button is seen"); }
    }
    Check(p7 == 1 && a7.X == 1075 && a7.Y == 665, "presses the button found on the screen («Надеть» of the single card)");

    // the click selected ANOTHER item: the rows are not where the pilot thinks; never the same point again, then stop with the reason
    double wtop = ScrollAnchor.TopRow1(geo, H);
    Func<long, long, int, AutoView> wv = (t, wsel, wrow) => new AutoView { NowMs = t, Foreground = true, H = H, Target = 5, Row = 3, Col = 1, Sel = wsel,
      SelRow = wrow, SelCol = wrow > 0 ? 1 : 0, Types = types, AnchorHas = true, Row1Top = wtop };
    var w1 = new AutoPilot(geo);
    var wa = w1.Step(wv(0, 0, 0));
    var wb = w1.Step(wv(1000, 66, 4));
    Check(wa.Kind == AutoKind.Click && wb.Kind == AutoKind.Click && (Math.Abs(wa.X - wb.X) > 2 || Math.Abs(wa.Y - wb.Y) > 2) && w1.State == "work",
          "another item selected: re-anchored from its row, not the same point again");
    w1.Step(wv(2000, 67, 5));
    Eq("failed", w1.State, "a second wrong item -> failed, no more toggling");
    Check(w1.Why.StartsWith("wrong item 67 at row 5/1"), "the reason names the item and its place (" + w1.Why + ")");
    var wr2 = new AutoPilot(geo);
    var wr2a = wr2.Step(wv(0, 0, 0)); var wr2b = wr2.Step(wv(1000, 66, 0));
    Check(wr2b.Kind != AutoKind.Click || Math.Abs(wr2a.X - wr2b.X) > 2 || Math.Abs(wr2a.Y - wr2b.Y) > 2, "another item whose row is unknown: calibrates, not the same point again (" + wr2a.X + "," + wr2a.Y + " -> " + wr2b.Kind + " " + wr2b.X + "," + wr2b.Y + ")");

    // «Заменить» pressed, the selection went away and the item is not on the hero: no item clicks, the player is asked
    var c8 = new AutoPilot(geo); int p8 = 0, after8 = 0; bool pressed8 = false;
    for (long t = 0; t < 10000; t += 100) {
      var v8 = cv(t, 0, 77, rep); if (pressed8) v8.Sel = 0;
      var a = c8.Step(v8);
      if (a.Kind == AutoKind.Click) { if (pressed8) after8++; else { p8++; pressed8 = true; } }
    }
    Check(p8 == 1 && after8 == 0, "selection gone after the press: no item click (" + after8 + ")");
    Eq("unconfirmed", c8.State, "selection gone after the press, item not on the hero -> unconfirmed, not failed");
    Check(c8.Why != "", "unconfirmed says why (" + c8.Why + ")");

    // a slot the screen read shows open, or the addresses being read again: not a try; the tries still end in slot_failed
    var s1 = new AutoPilot(geo); int s1Clicks = 0; long st1 = 0;
    for (; st1 < 10000; st1 += 100) if (s1.Step(new AutoView { NowMs = st1, Foreground = true, H = H, Slot = new[] { 100, 200, 60, 60 }, SlotOpen = true }).Kind == AutoKind.Click) s1Clicks++;
    for (; st1 < 20000; st1 += 100) if (s1.Step(new AutoView { NowMs = st1, Foreground = true, H = H, Slot = new[] { 100, 200, 60, 60 }, PanelOk = false }).Kind == AutoKind.Click) s1Clicks++;
    Check(s1Clicks == 0 && s1.State == "work", "slot shown open / addresses being scanned: no click, no try, not failed");
    for (; st1 < 30000; st1 += 100) if (s1.Step(new AutoView { NowMs = st1, Foreground = true, H = H, Slot = new[] { 100, 200, 60, 60 } }).Kind == AutoKind.Click) s1Clicks++;
    Eq(AutoPilot.SlotTries, s1Clicks, "slot: the tries start when it is not shown open");
    Eq("slot_failed", s1.State, "slot that stays closed -> slot_failed");
    {   // no panel yet (the game creates it when a slot is first opened): the slot is clicked; a panel known but stale: waits
      var sn = new AutoPilot(geo); bool clickedNone = false;
      for (long t = 0; t < 1000 && !clickedNone; t += 100) if (sn.Step(new AutoView { NowMs = t, Foreground = true, H = H, Slot = new[] { 100, 200, 60, 60 }, PanelOk = false, PanelNone = true }).Kind == AutoKind.Click) clickedNone = true;
      Check(clickedNone, "no panel yet (PanelOk false, PanelNone true): the slot is clicked within 1 s");
      var sw = new AutoPilot(geo); int swClicks = 0;
      for (long t = 0; t < 3000; t += 100) if (sw.Step(new AutoView { NowMs = t, Foreground = true, H = H, Slot = new[] { 100, 200, 60, 60 }, PanelOk = false, PanelNone = false }).Kind == AutoKind.Click) swClicks++;
      Check(swClicks == 0 && sw.State == "work", "a panel known but stale (PanelOk false, PanelNone false): waits, no click");
    }

    Console.WriteLine("Blue button test (measured on the live game)");
    int blue = unchecked((int)0xFF5160B3), dark = unchecked((int)0xFF49423F), gold = unchecked((int)0xFFC98F3A), white = unchecked((int)0xFFF0F0F0);
    Check(ButtonCheck.IsBlueFill(blue) && !ButtonCheck.IsBlueFill(dark) && !ButtonCheck.IsBlueFill(gold) && !ButtonCheck.IsBlueFill(white), "blue fill vs background, gold button, text");
    Check(ButtonCheck.BlueShare(new[] { blue, blue, white, dark, blue, dark, white, blue }) >= ButtonCheck.MinShare, "a button with text: enough blue");
    Check(ButtonCheck.BlueShare(new[] { gold, gold, white, dark, dark, dark }) < ButtonCheck.MinShare, "«Улучшить» / background: no");
  }

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
  sealed class FakeHeroGame {
    public long[] Heroes; public long Shown; public int Tab = 3, Part = -1; public double Scroll;   // Scroll: px the grid is moved up
    public double Gain = 1;   // how far the grid follows the pointer
    public bool City; public long CityHero;
    public bool Small;   // small cards: shorter rows
    // the grid's filter: the funnel opens the pop-up; class / faction columns (row 0 = «Все»)
    public bool FilterOpen; public List<long> Camps = new List<long>(); public long Class; public ulong Grid = 1;
    public long[] All; public Dictionary<long, long[]> Tags;   // every hero, uid -> [class, factions...]
    public static readonly long[] FactionList = { 0, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110 };
    public void Refilter() {
      var l = new List<long>();
      foreach (var u in All) {
        long[] t; Tags.TryGetValue(u, out t);
        if (Class > 0 && (t == null || t[0] != Class)) continue;
        if (Camps.Count > 0) { bool any = false; if (t != null) for (int i = 1; i < t.Length; i++) if (Camps.Contains(t[i])) any = true; if (!any) continue; }
        l.Add(u);
      }
      Heroes = l.ToArray(); Scroll = 0; Grid++;
      if (SelectFirst && Heroes.Length > 0) Shown = Heroes[0];
    }
    public bool SelectFirst;   // as the game does: a grid filtered again shows its first hero
    double PY { get { return Small ? g.SmallPitchY : g.PitchY; } }
    double CH { get { return Small ? g.SmallCardH : g.CardH; } }   // the main city (the hero screen closed): its «Герои» opens CityHero
    readonly HeroGeometry g; readonly double W, H;
    public int Clicks, Drags, Backs;
    public bool Blind;   // the filter pop-up is not recognised: the host reports it closed whatever it shows
    public List<double[]> Pts = new List<double[]>();
    public bool Blocked;   // something over the grid takes the clicks (a pop-up left open)
    public FakeHeroGame(HeroGeometry g, double W, double H) { this.g = g; this.W = W; this.H = H; }
    public double MaxScroll { get { int rows = (Heroes.Length + 2) / 3; return Math.Max(0, g.Row1Top * H + rows * PY * H - g.ViewBottom * H + 10); } }
    public void Click(double x, double y) {
      Clicks++; Pts.Add(new[] { x, y });
      if (Blocked) return;
      if (Tags != null && Math.Abs(x - g.FunnelX * H) < 20 && Math.Abs(y - (H - g.FunnelDY * H)) < 20) { FilterOpen = !FilterOpen; return; }
      if (FilterOpen) {
        int r = (int)Math.Round((y / H - g.FilterRow0) / g.FilterPitch);
        if (r < 0 || r >= HeroGeometry.FilterRows) return;
        if (Math.Abs(x - g.ClassX * H) < 25) { Class = r == 0 ? 0 : HeroGeometry.ClassOrder[r - 1]; Refilter(); }
        else if (Math.Abs(x - g.FactionX * H) < 25) { if (r == 0) Camps.Clear(); else if (Camps.Contains(FactionList[r])) Camps.Remove(FactionList[r]); else Camps.Add(FactionList[r]); Refilter(); }
        return;   // the pop-up covers the grid
      }
      if (City) { if (Math.Abs(x - (W - g.HeroesBtnDX * H)) < 20 && Math.Abs(y - (H - g.HeroesBtnDY * H)) < 20) { City = false; Shown = CityHero; } return; }
      if (Math.Abs(x - g.BackX * H) < 15 && Math.Abs(y - g.BackY * H) < 15) { if (Part >= 0) Part = -1; else Shown = 0; Backs++; return; }
      if (Math.Abs(x - (W - g.GearTabDX * H)) < 30 && Math.Abs(y - g.GearTabY * H) < 30) { Tab = 3; return; }
      if (Part >= 0 || y < g.ViewTop * H || y > g.ViewBottom * H) return;
      for (int c = 0; c < 3; c++) {
        if (Math.Abs(x - g.ColX(c) * H) > 0.109 * H / 2) continue;
        double rel = y - (g.Row1Top * H - Scroll);
        int row = (int)Math.Floor(rel / (PY * H));
        if (rel - row * PY * H > CH * H) return;   // the gap between rows
        int i = row * 3 + c;
        if (i >= 0 && i < Heroes.Length) Shown = Heroes[i];
        return;
      }
    }
    public void Drag(double dy) { Drags++; Scroll = Math.Max(0, Math.Min(MaxScroll, Scroll - dy * Gain)); }
  }

  static string RunHero(HeroPilot p, FakeHeroGame game, long plan, int maxSteps, out HeroView last) { return RunHero(p, game, plan, maxSteps, out last, null); }
  static string RunHero(HeroPilot p, FakeHeroGame game, long plan, int maxSteps, out HeroView last, long[] tags) {
    last = null;
    for (long t = 0; t < maxSteps * 100L; t += 100) {
      var v = new HeroView { NowMs = t, Foreground = true, W = 1920, H = 1009, Plan = plan, Hero = game.Shown, Tab = game.Tab,
        Part = game.Part, Heroes = game.Part >= 0 ? game.Heroes : game.Heroes, HeroesButton = game.City, ClientH = 1009, SmallCards = game.Small,
        FilterOpen = game.FilterOpen && !game.Blind, ChosenCamps = game.Camps.ToArray(), ClassChosen = game.Class > 0, GridPtr = game.Grid,
        FactionList = game.Tags != null ? FakeHeroGame.FactionList : null, ClassCount = 7 };
      if (tags != null) { v.HeroClass = tags[0]; v.HeroCamps = new long[tags.Length - 1]; Array.Copy(tags, 1, v.HeroCamps, 0, tags.Length - 1); }
      last = v;
      var a = p.Step(v);
      if (a == null) return p.State;
      if (a.Kind == AutoKind.Click) game.Click(a.X, a.Y);
      else if (a.Kind == AutoKind.Drag) game.Drag(a.DY);
      if (p.State == "failed" || p.State == "notfound" || p.State == "noscreen" || p.State == "small") return p.State;
    }
    return "timeout:" + p.State;
  }

  // The game's sell screen: rows of 6 items with title rows; a click toggles the item's selection and shows it as
  // m_CurrentEquipUid; a drag moves the list by gain × pointer move; the part icons filter the list (and clear the choice,
  // as the game does), «Сбросить» clears the filter.
  sealed class FakeSellGame {
    public SellGeometry G; public double W = 1920, H = 1009, U = 1009, Gain = 1.12, GainDown = 0.84, Scroll;   // the list follows drags down less (live)   // Scroll: px the list is moved up
    public int[] Types; public Dictionary<long, int[]> Pos = new Dictionary<long, int[]>();
    public HashSet<long> Sel = new HashSet<long>(); public long Cur; public ulong Ptr = 7; public int Clicks, Drags, FilterClicks;
    public Dictionary<long, int> PartOf = new Dictionary<long, int>(); public HashSet<int> Parts = new HashSet<int>();
    public bool Hidden;   // the player's own filter hides the first rows' items until «Сбросить»
    // «Быстрое улучшение» after a click on an item below +16 (every third): over the lower middle of the list; a click on it
    // is a disaster (DangerClicks), the empty spot right of the list closes it
    public bool Popup, Shown, HasPopup = true; public int DangerClicks;   // HasPopup: the sell screen has one, the inventory not
    public int RealCols = 6; public double? RealX0, RealPitchX;   // the game's own columns (a wrong guess of the pilot's)
    public static int Level(long uid) { return uid % 3 == 0 ? 16 : (int)(uid % 4) * 4; }   // Popup: the game's m_bShow (stays set), Shown: on the screen
    readonly List<long> all = new List<long>(); readonly int titleEvery;
    public FakeSellGame(SellGeometry g, int rows, int titleEvery, int cols = 6) {
      G = g; this.titleEvery = titleEvery; RealCols = cols;
      for (int k = 0; k < rows * cols; k++) { long u = 5000 + k; all.Add(u); PartOf[u] = k % 5; }
      Build();
    }
    public void Build() {
      Pos.Clear(); var t = new List<int> { 0 }; int n = 0; var items = new List<long>();
      foreach (var u in all) if ((Parts.Count == 0 || Parts.Contains(PartOf[u])) && !(Hidden && u < 5012)) items.Add(u);
      for (int r = 1; n < items.Count; r++) {
        if (titleEvery > 0 && r % titleEvery == 1) { t.Add(2); continue; }
        t.Add(1); for (int c = 1; c <= RealCols && n < items.Count; c++) Pos[items[n++]] = new[] { t.Count - 1, c };
      }
      Types = t.ToArray(); Scroll = 0; Ptr++; Sel.Clear(); Cur = 0;
    }
    double Row1 { get { return G.ViewTopPx(U) + G.TopPad * U - Scroll; } }
    // the list stops when its last row reaches the bottom of the view
    public double MaxScroll { get { return Math.Max(0, G.RowOffset(Types, Types.Length) * U + G.TopPad * U - (G.ViewBottomPx(H, U) - G.ViewTopPx(U))); } }
    public double Phase() {   // the row tops of the item rows on the screen, mod the row step
      double pitch = G.PitchY * U; int r = 1; while (r < Types.Length && Types[r] != 1) r++;
      double y = Row1 + G.RowOffset(Types, r) * U; double ph = y % pitch; return ph < 0 ? ph + pitch : ph;
    }
    public SellScreen Screen() {
      var s = new SellScreen { Open = true, ListPtr = Ptr, Types = Types, Current = Cur, Popup = Popup };
      foreach (var kv in Pos) s.Pos[kv.Key] = kv.Value; foreach (var u in Sel) s.Selected.Add(u); foreach (var p in Parts) s.Parts.Add(p);
      return s;
    }
    public void Act(AutoAction a) {
      if (a.Kind == AutoKind.Drag) { Drags++; Scroll = Math.Max(0, Math.Min(MaxScroll, Scroll - a.DY * (a.DY > 0 ? GainDown : Gain))); return; }
      if (a.Kind != AutoKind.Click) return;
      Clicks++;
      var cl = G.Close(W, H, U); if (Math.Abs(a.X - cl[0]) < 20 && Math.Abs(a.Y - cl[1]) < 20) { Shown = false; return; }
      if (Shown && a.X > W / 2 - 0.19 * U && a.X < W / 2 + 0.32 * U && a.Y > 0.505 * U && a.Y < 0.892 * U) { DangerClicks++; return; }
      for (int p = 0; p <= 4; p++) { var c = G.Part(p, W, H, U); if (Math.Abs(a.X - c[0]) < 20 && Math.Abs(a.Y - c[1]) < 20) { FilterClicks++; if (!Parts.Remove(p)) Parts.Add(p); Build(); return; } }
      var rs = G.Reset(W, H, U); if (Math.Abs(a.X - rs[0]) < 20 && Math.Abs(a.Y - rs[1]) < 20) { FilterClicks++; Parts.Clear(); Hidden = false; Build(); return; }
      if (a.Y < G.ViewTopPx(U) || a.Y > G.ViewBottomPx(H, U)) return;
      double y = a.Y - Row1; int row = 0; double top = 0;
      for (int r = 1; r < Types.Length; r++) { double h = G.RowHeight(Types[r]) * U; if (y >= top && y < top + h) { row = r; break; } top += h; }
      if (row == 0 || Types[row] != 1 || y - top > G.CellHeight * U) return;    // a title, a gap under the cells
      double xr = (a.X - W / 2) / U - (RealX0 ?? G.X0), px = RealPitchX ?? G.PitchX; int col = (int)Math.Floor(xr / px) + 1;
      if (col < 1 || col > RealCols || xr - (col - 1) * px > G.CellW) return;
      foreach (var kv in Pos) if (kv.Value[0] == row && kv.Value[1] == col) {
        if (!Sel.Remove(kv.Key)) Sel.Add(kv.Key);
        Cur = Cur == kv.Key ? 0 : kv.Key; Popup = Shown = HasPopup && Level(kv.Key) < 16; return;
      }
    }
  }

  static string RunSell(SellPilot p, FakeSellGame game, ICollection<long> wanted, int maxSteps, bool phase = true) {
    long now = 0; var traces = new List<string>(); var parts = new HashSet<int>(); foreach (var u in wanted) { int pt; if (game.PartOf.TryGetValue(u, out pt)) parts.Add(pt); }
    for (int i = 0; i < maxSteps; i++) {
      var lv = new Dictionary<long, int>(); foreach (var u in wanted) lv[u] = FakeSellGame.Level(u);
      var a = p.Step(new SellView { NowMs = now, Foreground = true, W = game.W, H = game.H, U = game.U, Screen = game.Screen(), Wanted = wanted,
                                    Parts = parts, Levels = lv, Phase = phase ? game.Phase() : double.NaN });
      if (p.Trace != "") { traces.Add(p.Trace); if (traces.Count > 8) traces.RemoveAt(0); p.Trace = ""; }
      game.Act(a); now += 100;   // (a stopped pilot may still take a stray item back: the app performs that click)
      if ((p.State == "done" || p.State == "failed") && a.Kind == AutoKind.None) { if (p.State == "failed") Console.WriteLine("    last steps: " + string.Join(" | ", traces)); return p.State; }
    }
    return "timeout";
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

  // «Показать в игре» with auto click: which steps the app clicks itself, which stay hints, and the safety stops
  static void LocatePilotTests() {
    Console.WriteLine("Locate pilot (auto click up to the last button)");
    const double W = 1920, H = 1009, U = 1009;
    var item = LocItem(777, 4, 720100, 12, 5, 7, 9, 3);
    Func<LocateKind, LocateView> V = k => new LocateView { NowMs = 0, Foreground = true, W = W, H = H, U = U, Step = new LocateStep { Kind = k }, Cols = 8 };
    Func<InventoryScreen> inv = () => new InventoryScreen { Open = true, EquipTab = true, Uids = new List<long>(new long[] { 1, 2, 777 }) };
    string why; LocateView v;

    // the decision per step
    v = V(LocateKind.None); Eq(LocateTodo.Nothing, LocateAuto.Decide(v, out why), "auto: no request -> nothing");
    v = V(LocateKind.OpenInventory); v.CityButton = true; Eq(LocateTodo.InventoryButton, LocateAuto.Decide(v, out why), "auto: the city with its button -> press «Инвентарь»");
    v = V(LocateKind.OpenInventory); v.HeroScreenOpen = true; Eq(LocateTodo.BackToCity, LocateAuto.Decide(v, out why), "auto: the hero screen -> back to the city");
    v = V(LocateKind.OpenInventory); Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: not the city -> the player brings it up"); Eq("no_city", why, "auto: ... and why");
    v = V(LocateKind.OpenInventory); v.CityButton = true; v.OtherFormOpen = true; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: the Foundry is open -> no click");
    v = V(LocateKind.OpenEquipTab); Eq(LocateTodo.EquipTab, LocateAuto.Decide(v, out why), "auto: the inventory on another tab -> «Снаряжение»");
    v = V(LocateKind.OpenEquipTab); v.OtherFormOpen = true; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: another tab behind the Foundry -> no click");
    v = V(LocateKind.InList); v.Inv = inv(); Eq(LocateTodo.PickItem, LocateAuto.Decide(v, out why), "auto: item in the list -> pick it");
    v = V(LocateKind.InList); v.Inv = inv(); v.Inv.Bulk = true; Eq(LocateTodo.Stop, LocateAuto.Decide(v, out why), "auto: bulk sale -> stop (a click toggles a sale)"); Eq("bulk", why, "auto: ... bulk");
    v = V(LocateKind.InList); v.Inv = inv(); v.OtherFormOpen = true; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: the list behind the Foundry is not clicked");
    v = V(LocateKind.InList); v.Inv = null; Eq(LocateTodo.Stop, LocateAuto.Decide(v, out why), "auto: the inventory vanished -> stop");
    v = V(LocateKind.InList); v.Inv = inv(); v.Inv.EquipTab = false; Eq(LocateTodo.Stop, LocateAuto.Decide(v, out why), "auto: not on the equipment tab -> stop");
    // everything after the item is selected stays the player's: the Foundry button, tabs, lists, the final buttons, the pop-ups
    foreach (LocateKind k in Enum.GetValues(typeof(LocateKind))) {
      if (k == LocateKind.OpenInventory || k == LocateKind.OpenEquipTab || k == LocateKind.InList || k == LocateKind.None) continue;
      v = V(k); v.Inv = inv(); v.HeroScreenOpen = true; v.CityButton = true;
      var todo = LocateAuto.Decide(v, out why);
      Check(todo == LocateTodo.Hint, "auto: step " + k + " is a hint, never a click (" + todo + ")");
    }
    Check(LocateAuto.NeedsCityButton(new LocateStep { Kind = LocateKind.OpenInventory }, false, false) && !LocateAuto.NeedsCityButton(new LocateStep { Kind = LocateKind.InList }, false, false)
      && !LocateAuto.NeedsCityButton(new LocateStep { Kind = LocateKind.OpenInventory }, true, false), "auto: the city button is looked for only when needed");

    // the clicks and their places
    var hg = new HeroGeometry();
    var p = new LocatePilot();
    v = V(LocateKind.OpenEquipTab); var m = p.Step(v);
    Check(m.State == "work" && m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - 0.085 * U) < 1 && Math.Abs(m.Action.Y - 0.315 * U) < 1, "auto: «Снаряжение» at the storage cleanup's spot");
    v = V(LocateKind.OpenEquipTab); v.NowMs = 100; Eq(AutoKind.None, p.Step(v).Action.Kind, "auto: waits for the game after a click");
    p = new LocatePilot(); v = V(LocateKind.OpenInventory); v.CityButton = true; m = p.Step(v);
    Check(m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - (W - v.InvDX * U)) < 1 && Math.Abs(m.Action.Y - (H - hg.HeroesBtnDY * U)) < 1, "auto: «Инвентарь» in the city's bar");
    p = new LocatePilot(); v = V(LocateKind.OpenInventory); v.HeroScreenOpen = true; m = p.Step(v);
    Check(m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - hg.BackX * U) < 1 && Math.Abs(m.Action.Y - hg.BackY * U) < 1, "auto: the back arrow of the hero screen");
    p = new LocatePilot(); v = V(LocateKind.Done); m = p.Step(v);
    Check(m.State == "hint" && m.Action.Kind == AutoKind.None && m.Why == "foundry", "auto: the final step: a hint, no click");

    // recast: the item is selected -> the card's gem button; the gem screen holds it -> the wanted function's row; Done -> nothing
    {
      var gg = GameGeometry.Parse(GeoJson(), GeoGame);
      var recastItem = LocItem(777, 4, 0, 0); recastItem.Quality = 8;
      Func<LocateKind, LocateView> RV = k => { var x = V(k); x.Geometry = gg; x.Item = recastItem; x.Purpose = "recast"; return x; };
      v = RV(LocateKind.Selected); v.Step.Worn = true; v.Inv = inv(); v.Inv.Current = 777;
      Eq(LocateTodo.CardGem, LocateAuto.Decide(v, out why), "auto: recast, item selected -> the card's gem button");
      p = new LocatePilot(); m = p.Step(v); var cardF = LocateFrames.For(v.Step, recastItem, null, null, gg, W, H, U);
      Check(m.State == "work" && m.Todo == LocateTodo.CardGem && m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - cardF.ClickX) < 1 && Math.Abs(m.Action.Y - cardF.ClickY) < 1, "auto: ... clicks the frame's rect (cardLeft for a worn item)");
      v.NowMs = 100; Eq(AutoKind.None, p.Step(v).Action.Kind, "auto: ... and waits for the gem screen");
      v = RV(LocateKind.Selected); v.Inv = inv(); v.Inv.Current = 777; p = new LocatePilot(); m = p.Step(v);
      Check(m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - LocateFrames.For(v.Step, recastItem, null, null, gg, W, H, U).ClickX) < 1 && LocateFrames.For(v.Step, recastItem, null, null, gg, W, H, U).Target == "inventory.cardSolo", "auto: not worn -> the lone gem button (cardSolo)");
      v = RV(LocateKind.Selected); v.Purpose = "ascend"; v.Inv = inv(); v.Inv.Current = 777; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: Ascension never clicks the card");
      v = RV(LocateKind.Selected); v.Inv = inv(); v.Inv.Current = 778; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: another item selected -> no click");
      v = RV(LocateKind.Selected); v.Inv = inv(); v.Inv.Current = 777; v.Inv.Bulk = true; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: bulk sale -> no click on the card");
      v = RV(LocateKind.Selected); v.Inv = inv(); v.Inv.Current = 777; v.Geometry = null; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: no geometry -> hint");
      v = RV(LocateKind.Selected); v.Inv = inv(); v.Inv.Current = 777; v.OtherFormOpen = true; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: a form on top -> no click on the card");
      // the gem screen
      Func<int, RecastScreen> gemRs = fn => new RecastScreen { GemOpen = true, GemFunc = fn, GemSel = 777 };
      v = RV(LocateKind.GemFunction); v.Step.Func = 8; v.Rs = gemRs(-1);
      Eq(LocateTodo.GemFunc, LocateAuto.Decide(v, out why), "auto: gem screen with the item and no function -> the wanted row");
      p = new LocatePilot(); m = p.Step(v); var rowF = LocateFrames.GemRow(gg, v.Rs, 8, W, H, U, true);
      Check(m.Todo == LocateTodo.GemFunc && m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - rowF.ClickX) < 1 && Math.Abs(m.Action.Y - rowF.ClickY) < 1, "auto: ... clicks the row of Advanced Recast (the frame's rect)");
      v = RV(LocateKind.GemFunction); v.Step.Func = 7; v.Rs = gemRs(8); v.Step.WrongFunc = true; Eq(LocateTodo.GemFunc, LocateAuto.Decide(v, out why), "auto: the other recast function selected -> the wanted row");
      p = new LocatePilot(); m = p.Step(v); Check(Math.Abs(m.Action.Y - LocateFrames.GemRow(gg, v.Rs, 7, W, H, U, true).ClickY) < 1 && m.Action.Y < rowF.ClickY, "auto: ... Recast is the row above Advanced Recast");
      v = RV(LocateKind.GemFunction); v.Step.Func = 8; v.Rs = gemRs(8); Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: the wanted function is already selected -> nothing");
      v = RV(LocateKind.GemFunction); v.Step.Func = 0; v.Rs = gemRs(-1); Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: quality unknown (no wanted function) -> hint, no guess");
      v = RV(LocateKind.GemFunction); v.Step.Func = 8; v.Rs = gemRs(-1); v.Rs.GemSel = 5; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: another item on the gem screen -> no click");
      v = RV(LocateKind.GemFunction); v.Step.Func = 8; v.Rs = gemRs(-1); v.Rs.GemFuncList = new RFX.ListRead { ScrollY = 5000, Cols = 1, RowUnits = 110, First = 3, Last = 8 };
      Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "auto: the function list scrolled so the row is out of view -> hint");
      // after the function is selected (step Done) and at the final steps: no click, whatever the screen
      v = RV(LocateKind.Done); v.Rs = gemRs(8); v.Step.OnGem = true; p = new LocatePilot(); m = p.Step(v);
      Check(m.State == "hint" && m.Action.Kind == AutoKind.None, "auto: the gem screen done -> the pilot stops clicking («Улучшить» is the player's)");
      foreach (LocateKind k in new[] { LocateKind.Done, LocateKind.AscendReady }) { v = RV(k); v.Rs = gemRs(8); p = new LocatePilot(); Eq(AutoKind.None, p.Step(v).Action.Kind, "auto: final step " + k + " never clicks"); }
      // the click targets of the pilot's moves are the recast ones only
      Check(!Enum.IsDefined(typeof(LocateTodo), "GemButton") && !Enum.IsDefined(typeof(LocateTodo), "Upgrade") && !Enum.IsDefined(typeof(LocateTodo), "Recast") && !Enum.IsDefined(typeof(LocateTodo), "Ascend"), "auto: no todo names a final button");
    }

    // the player and the game window come first
    p = new LocatePilot(); v = V(LocateKind.OpenEquipTab); v.Foreground = false; m = p.Step(v);
    Check(m.State == "background" && m.Action.Kind == AutoKind.None, "auto: the game is not in front -> nothing");
    v = V(LocateKind.OpenEquipTab); v.UserBusy = true; m = p.Step(v);
    Check(m.State == "paused" && m.Action.Kind == AutoKind.None, "auto: the player moves the mouse -> paused");
    v = V(LocateKind.OpenEquipTab); v.NowMs = 1000; v.UserClicked = true; m = p.Step(v);
    Check(m.State == "paused", "auto: the player clicked -> paused");
    v = V(LocateKind.OpenEquipTab); v.NowMs = 4000; m = p.Step(v); Eq("paused", m.State, "auto: ... for a few seconds");
    v = V(LocateKind.OpenEquipTab); v.NowMs = 5200; m = p.Step(v); Check(m.State == "work" && m.Action.Kind == AutoKind.Click, "auto: ... then goes on");

    // a step that does not move on stops after a few clicks; cancel stops for good
    p = new LocatePilot(); int n = 0; long t = 0;
    for (int i = 0; i < 20; i++, t += 3000) { v = V(LocateKind.OpenEquipTab); v.NowMs = t; m = p.Step(v); if (m.Action.Kind == AutoKind.Click) n++; if (m.State == "stopped") break; }
    Check(n == LocatePilot.MaxStepClicks && p.Stopped && p.Reason == "stuck", "auto: the tab never opens -> " + n + " clicks, then stop (" + p.Reason + ")");
    v = V(LocateKind.OpenEquipTab); v.NowMs = t + 99999; m = p.Step(v);
    Check(m.State == "stopped" && m.Action.Kind == AutoKind.None, "auto: stopped stays stopped");
    p.Reset(); v = V(LocateKind.OpenEquipTab); v.NowMs = 0; Eq("work", p.Step(v).State, "auto: a new request starts again");
    p.Cancel(); m = p.Step(V(LocateKind.OpenEquipTab));
    Check(m.State == "stopped" && m.Why == "cancelled" && m.Action.Kind == AutoKind.None, "auto: the overlay's cancel stops the clicks");
    p = new LocatePilot(); v = V(LocateKind.InList); v.Inv = inv(); v.Inv.Bulk = true; m = p.Step(v);
    Check(m.State == "stopped" && m.Why == "bulk" && m.Action.Kind == AutoKind.None, "auto: bulk sale -> stopped without a click");

    // the whole way in a fake inventory: the item deep in the list, the list scrolled a bit, then it is selected and the pilot stops clicking
    foreach (double offset in new[] { 0.0, 0.6 }) {
      var g = SellGeometry.Inventory(W, U, 8);
      var game = new FakeSellGame(g, 60, 0, 8) { HasPopup = false }; game.Scroll = offset * g.PitchY * U;
      long target = 5000 + 8 * 41 + 3;
      var rq = new LocateRequest { Id = "x", Purpose = "recast", Item = new PlanItem { Uid = target, Slot = 1 } };
      var lp = new LocatePilot { ItemUid = target };
      var order = new List<KeyValuePair<long, int[]>>(game.Pos);
      order.Sort((a, b) => a.Value[0] != b.Value[0] ? a.Value[0].CompareTo(b.Value[0]) : a.Value[1].CompareTo(b.Value[1]));
      int acts = 0, afterSelected = 0, quiet = 0; bool selected = false; string last = "";
      for (long now = 0; now < 600 * 100; now += 100) {
        var iv = new InventoryScreen { Open = true, EquipTab = true, Current = game.Cur };
        foreach (var kv in order) iv.Uids.Add(kv.Key);
        var st = LocateGuide.Next(rq, iv, null, null, 8, 1);
        var mv = lp.Step(new LocateView { NowMs = now, Foreground = true, W = W, H = H, U = U, Step = st, Inv = iv, Cols = 8, Phase = game.Phase() });
        last = mv.State + "/" + mv.Why;
        if (st.Kind == LocateKind.Selected) { selected = true; if (mv.Action.Kind != AutoKind.None) afterSelected++; if (++quiet > 30) break; continue; }
        if (mv.State == "stopped") break;
        if (mv.Action.Kind != AutoKind.None) acts++;
        game.Act(mv.Action);
      }
      Check(selected && afterSelected == 0 && !lp.Stopped, "auto: the item (row 42 of 60, list offset " + offset + ") is selected by the pilot, then no more clicks (" + acts + " actions, " + game.Drags + " drags, " + last + ")");
      Check(game.Drags >= 1 && acts < 60, "auto: ... scrolling by drags, a handful of actions (" + acts + ")");
    }
  }

  // ------------------------------------------------------------------ screen geometry (src/Geometry.cs) and the frames built on it (src/LocateFrames.cs)
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

  // the pilot's Foundry steps (src/LocatePilot.cs) on the fixture geometry
  static void LocateFoundryPilotTests() {
    Console.WriteLine("Locate pilot in the Foundry (tabs, slot, list cell: clicks; the final buttons and the filter: never)");
    const double W = 2000, H = 1000, U = 1000;
    var g = GameGeometry.Parse(GeoJson(), GeoGame);
    var item = LocItem(777, 3, 720100, 12, 5, 7, 9, 3);
    Func<double, RFX.ListRead> L = s => new RFX.ListRead { ScrollY = s, Cols = 5, RowUnits = 108, First = 0, Last = 14 };
    Func<double, RecastScreen> R = s => new RecastScreen { SublimingOpen = true, AscendShown = true, CurType = 2, AscendList = L(s) };
    Func<LocateStep, RecastScreen, LocateView> V = (st, rs) => new LocateView { NowMs = 0, Foreground = true, W = W, H = H, U = U, Step = st, Cols = 8, Geometry = g, Rs = rs, Item = item,
      Inv = new InventoryScreen { Open = true, EquipTab = true }, OtherFormOpen = rs != null && rs.SublimingOpen };
    string why; LocateView v; LocatePilot p; LocateMove m;

    // «Литейная»
    v = V(new LocateStep { Kind = LocateKind.OpenFoundry }, null); v.OtherFormOpen = false;
    Eq(LocateTodo.FoundryButton, LocateAuto.Decide(v, out why), "foundry auto: the inventory's «Литейная» -> click it");
    p = new LocatePilot(); m = p.Step(v); var wk = g.Rect("inventory.workshop", W, H, U);
    Check(m.State == "work" && m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - wk.CX) < 1 && Math.Abs(m.Action.Y - wk.CY) < 1, "foundry auto: ... at the button's centre from the geometry");
    // the hero gear screen on top (live 2026-10-29: the equip helper left it open with a slot's gear list): back to the city first, never its «Литейная»
    v = V(new LocateStep { Kind = LocateKind.OpenFoundry, OnGear = true }, null); v.OtherFormOpen = false; v.HeroScreenOpen = true; v.Inv = null;
    Eq(LocateTodo.BackToCity, LocateAuto.Decide(v, out why), "foundry auto: the hero gear screen on top -> back to the city, not «Литейная»");
    p = new LocatePilot(); m = p.Step(v); var hgeo = new HeroGeometry(); wk = g.Rect("hero.workshop", W, H, U);
    Check(m.State == "work" && m.Todo == LocateTodo.BackToCity && m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - hgeo.BackX * U) < 1 && Math.Abs(m.Action.Y - hgeo.BackY * U) < 1, "foundry auto: ... the first click is the hero screen's back arrow");
    Check(!(Math.Abs(m.Action.X - wk.CX) < 1 && Math.Abs(m.Action.Y - wk.CY) < 1), "foundry auto: ... not the hero screen's «Литейная»");
    m = p.Step(v); Check(m.Action.Kind == AutoKind.None, "foundry auto: ... then it waits for the screen to change");
    v.NowMs = LocatePilot.AfterBackMs + 1; m = p.Step(v); Check(m.Todo == LocateTodo.BackToCity && m.Action.Kind == AutoKind.Click, "foundry auto: ... still the hero screen (the gear list closed): the back arrow again");
    v = V(new LocateStep { Kind = LocateKind.OpenFoundry, OnGear = true }, null); v.OtherFormOpen = false; v.HeroScreenOpen = true; v.Geometry = null;
    Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "foundry auto: the hero gear screen without the geometry -> a hint, no click");
    v = V(new LocateStep { Kind = LocateKind.OpenFoundry }, null); v.OtherFormOpen = true; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "foundry auto: the gem screen on top -> no click");
    v = V(new LocateStep { Kind = LocateKind.OpenFoundry }, null); v.OtherFormOpen = false; v.Geometry = null; Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "foundry auto: no geometry -> a hint");
    v = V(new LocateStep { Kind = LocateKind.OpenFoundry }, null); v.OtherFormOpen = false; v.Inv.EquipTab = false; Eq(LocateTodo.Stop, LocateAuto.Decide(v, out why), "foundry auto: «Литейная» is not on this inventory tab -> stop");

    // the tab
    v = V(new LocateStep { Kind = LocateKind.OpenAscendTab }, new RecastScreen { SublimingOpen = true, CurType = 0 });
    Eq(LocateTodo.AscendTab, LocateAuto.Decide(v, out why), "foundry auto: the Foundry on another tab -> «Вознесение»");
    p = new LocatePilot(); m = p.Step(v); var tab = FoundryPlace.Tab(g, "ascend", null, W, H, U);
    Check(m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - tab.CX) < 1 && Math.Abs(m.Action.Y - tab.CY) < 1, "foundry auto: ... at the third tab's centre");
    v.FoundryTabs = new[] { true, false, true, true, true }; p = new LocatePilot(); m = p.Step(v); tab = FoundryPlace.Tab(g, "ascend", v.FoundryTabs, W, H, U);
    Check(Math.Abs(m.Action.Y - tab.CY) < 1, "foundry auto: ... a hidden tab before it moves the click up");
    v = V(new LocateStep { Kind = LocateKind.OpenAscendTab }, new RecastScreen { SublimingOpen = false }); Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "foundry auto: the Foundry is not shown -> no click");

    // the slot tab; the filter is never touched
    v = V(new LocateStep { Kind = LocateKind.AscendFilter, Missing = new[] { "slot", "subs" } }, R(0));
    Eq(LocateTodo.SlotTab, LocateAuto.Decide(v, out why), "foundry auto: the slot is not set -> the slot tab");
    p = new LocatePilot(); m = p.Step(v); var st3 = g.Rect("ascend.slot.3", W, H, U); Check(Math.Abs(m.Action.X - st3.CX) < 1 && Math.Abs(m.Action.Y - st3.CY) < 1, "foundry auto: ... the item's slot (3)");
    foreach (var miss in new[] { new[] { "suit" }, new[] { "main" }, new[] { "subs" }, new[] { "main", "subs" } }) {
      v = V(new LocateStep { Kind = LocateKind.AscendFilter, Missing = miss }, R(0));
      Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "foundry auto: missing " + string.Join("+", miss) + " -> the filter is the player's, no click");
    }

    // the cell: click when in view, drag when not, stop when the list does not move
    v = V(new LocateStep { Kind = LocateKind.AscendPick, Pos = 8 }, R(0));
    Eq(LocateTodo.AscendCell, LocateAuto.Decide(v, out why), "foundry auto: the item in the Ascension list -> its cell");
    p = new LocatePilot(); m = p.Step(v);
    Check(m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - 350) <= 1 && Math.Abs(m.Action.Y - (387.5 + 81.25)) <= 1, "foundry auto: ... in view: one click on the cell's centre");
    v = V(new LocateStep { Kind = LocateKind.AscendPick, Pos = 41 }, R(0)); p = new LocatePilot(); m = p.Step(v);
    Check(m.Action.Kind == AutoKind.Drag && m.Action.DY < 0 && m.Action.Y > 200 && m.Action.Y + m.Action.DY < 700 && m.Action.Y + m.Action.DY > 200, "foundry auto: ... far below: a drag up inside the list (later rows come in)");
    v = V(new LocateStep { Kind = LocateKind.AscendPick, Pos = 8 }, R(640)); p = new LocatePilot(); m = p.Step(v);
    Check(m.Action.Kind == AutoKind.Drag && m.Action.DY > 0, "foundry auto: ... far above: a drag down");
    // a fake list: the pilot scrolls to the item, then clicks it once
    {
      double scroll = 0; int drags = 0, clicks = 0; bool clicked = false; p = new LocatePilot(); long t = 0; string trace = "";
      for (int i = 0; i < 80 && !clicked; i++, t += 2500) {
        v = V(new LocateStep { Kind = LocateKind.AscendPick, Pos = 43 }, R(scroll)); v.NowMs = t;
        v.Step.Pos = 43; m = p.Step(v);
        if (m.State == "stopped") { trace = m.Why; break; }
        if (m.Action.Kind == AutoKind.Drag) { drags++; scroll = Math.Max(0, Math.Min(900, scroll - m.Action.DY * 640 / U * 0.9)); }   // the content follows the pointer (a bit less), the list ends at 900
        else if (m.Action.Kind == AutoKind.Click) { clicks++; clicked = true; }
      }
      Check(clicked && clicks == 1 && drags >= 1 && drags < 12, "foundry auto: scrolls to the item by dragging (" + drags + " drags), then clicks it once (" + clicks + ", " + trace + ")");
    }
    p = new LocatePilot(); { int still = 0; long t = 0; string last = "";
      for (int i = 0; i < 10; i++, t += 2500) { v = V(new LocateStep { Kind = LocateKind.AscendPick, Pos = 41 }, R(100)); v.NowMs = t; m = p.Step(v); last = m.State + "/" + m.Why; if (m.Action.Kind == AutoKind.Drag) still++; if (m.State == "stopped") break; }
      Check(p.Stopped && p.Reason == "lost" && still <= 3, "foundry auto: a list that does not move on drags stops the pilot (" + still + " drags, " + last + ")"); }
    // the item is in the long all-slots list and off the screen: the slot tab first, then the scroll and the cell (the filter is never needed)
    v = V(new LocateStep { Kind = LocateKind.AscendPick, Pos = 41, Scroll = "below", ScrollRows = 4, Missing = new[] { "slot" } }, R(0));
    Eq(LocateTodo.SlotTab, LocateAuto.Decide(v, out why), "foundry auto: item in the list but off screen and the slot not set -> the slot tab first");
    p = new LocatePilot(); m = p.Step(v);
    Check(m.Action.Kind == AutoKind.Click && Math.Abs(m.Action.X - st3.CX) < 1 && Math.Abs(m.Action.Y - st3.CY) < 1, "foundry auto: ... the click is on the item's slot tab, not on the list");
    v = V(new LocateStep { Kind = LocateKind.AscendPick, Pos = 8, Scroll = "visible", Missing = new[] { "slot" } }, R(0));
    Eq(LocateTodo.AscendCell, LocateAuto.Decide(v, out why), "foundry auto: the item is on the screen already -> its cell, no slot tab");
    v = V(new LocateStep { Kind = LocateKind.AscendPick, Pos = 41, Scroll = "below", ScrollRows = 4, Missing = new string[0] }, R(0));
    Eq(LocateTodo.AscendCell, LocateAuto.Decide(v, out why), "foundry auto: the slot is set, the item is far below -> scroll to it");
    v = V(new LocateStep { Kind = LocateKind.AscendLoading }, R(0)); Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "foundry auto: the list is being read -> wait, no click");
    v = V(new LocateStep { Kind = LocateKind.AscendFilter, Missing = new[] { "suit" }, FilterGo = "suitRow" }, R(0));
    Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "foundry auto: the pop-up's buttons are the player's");
    v = V(new LocateStep { Kind = LocateKind.AscendPick, Pos = 8 }, new RecastScreen { SublimingOpen = true, AscendShown = true, CurType = 2, AscendList = new RFX.ListRead() });
    Eq(LocateTodo.Hint, LocateAuto.Decide(v, out why), "foundry auto: the list's scroll is unreadable -> a hint");

    // never clicked, whatever else is true: the final buttons, the card's button, the recast picks and every confirmation
    foreach (LocateKind k in Enum.GetValues(typeof(LocateKind))) {
      if (k == LocateKind.None || k == LocateKind.OpenInventory || k == LocateKind.OpenEquipTab || k == LocateKind.InList || k == LocateKind.OpenFoundry || k == LocateKind.OpenAscendTab || k == LocateKind.AscendPick) continue;
      v = V(new LocateStep { Kind = k, Missing = new[] { "main" }, Pos = 8 }, R(0)); v.HeroScreenOpen = true; v.OtherFormOpen = false;
      var todo = LocateAuto.Decide(v, out why);
      Check(todo == LocateTodo.Hint, "foundry auto: step " + k + " stays a hint with the geometry (" + todo + ")");
    }
    // the player and the window come first
    v = V(new LocateStep { Kind = LocateKind.OpenAscendTab }, new RecastScreen { SublimingOpen = true }); v.Foreground = false; p = new LocatePilot(); m = p.Step(v);
    Check(m.State == "background" && m.Action.Kind == AutoKind.None, "foundry auto: the game is not in front -> no click");
    v = V(new LocateStep { Kind = LocateKind.OpenAscendTab }, new RecastScreen { SublimingOpen = true }); v.UserBusy = true; m = p.Step(v); Check(m.State == "paused" && m.Action.Kind == AutoKind.None, "foundry auto: the player moves the mouse -> paused");
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

  static void LocateTests() {
    Console.WriteLine("Locate (\u00abНайти в игре\u00bb: request parsing, steps, list place):");
    string body = "{\"ok\":true,\"plans\":[],\"locate\":{\"id\":\"l1\",\"purpose\":\"recast\",\"createdAt\":\"2026-10-12T10:00:00Z\",\"item\":{\"slot\":4,\"uid\":777,\"slotName\":\"Кольцо\"," +
      "\"name\":\"Кольцо\",\"setId\":720100,\"setName\":\"Проклятие\",\"level\":16,\"stars\":6,\"icon\":\"Item_1\",\"heroUid\":214700000,\"heroName\":\"Байек\"," +
      "\"main\":[{\"name\":\"Крит\",\"value\":\"64%\",\"stat\":12}],\"subs\":[{\"text\":\"АТК\",\"rolls\":2,\"stat\":5},{\"text\":\"СКР\",\"rolls\":1,\"stat\":7}]}}}";
    var lr = PlansClient.Interpret(200, body, true);
    Check(lr.Status == PlansStatus.Ok && lr.Locate != null && lr.Locate.Id == "l1" && lr.Locate.Purpose == "recast", "locate parsed");
    Check(lr.Locate.Item != null && lr.Locate.Item.Uid == 777 && lr.Locate.Item.Slot == 4 && lr.Locate.Item.SetId == 720100, "locate item");
    Check(lr.Locate.HeroUid == 214700000 && lr.Locate.HeroName == "Байек", "locate hero");
    Check(lr.Locate.Item.Main.Count == 1 && lr.Locate.Item.Main[0].Stat == 12 && lr.Locate.Item.Subs.Count == 2 && lr.Locate.Item.Subs[1].Stat == 7, "locate stats");
    Check(PlansClient.Interpret(200, "{\"ok\":true,\"plans\":[]}", true).Locate == null, "no locate field");
    Check(PlansClient.Interpret(200, "{\"ok\":true,\"locate\":null}", true).Locate == null, "locate null");
    Check(PlansClient.Interpret(200, "{\"ok\":true,\"locate\":{\"id\":\"\",\"item\":null}}", true).Locate == null, "locate without id dropped");
    Check(PlansClient.Interpret(200, "{\"ok\":true,\"locate\":{\"id\":\"x\",\"purpose\":\"other\",\"item\":null}}", true).Locate == null, "unknown purpose dropped");
    var noItem = PlansClient.Interpret(200, "{\"ok\":true,\"locate\":{\"id\":\"x\",\"purpose\":\"recast\",\"item\":null}}", true).Locate;
    Check(noItem != null && noItem.Item == null, "item gone from the snapshot: request kept, item null");
    var badItem = PlansClient.Interpret(200, "{\"ok\":true,\"locate\":{\"id\":\"x\",\"item\":{\"slot\":9,\"uid\":5}}}", true).Locate;
    Check(badItem != null && badItem.Item == null, "bad slot: item dropped");
    Check(PlansClient.Interpret(200, "{\"ok\":true,\"locate\":{\"id\":\"x\",\"item\":{\"slot\":1,\"uid\":-3}}}", true).Locate.Item == null, "uid <= 0 dropped");
    Check(PlansClient.Interpret(200, "{\"ok\":true,\"locate\":{\"id\":\"x\",\"item\":{\"slot\":1,\"uid\":3,\"icon\":\"../a\"}}}", true).Locate.Item.Icon == "", "locate icon name sanitised");
    Check(LocateClient.Finish("http://x", "c", "", true).Status == PlansStatus.NotFound, "finish: empty id");

    // Position
    var l9 = new long[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
    int[] ps = LocateGuide.Position(l9, 19, 9);
    Check(ps != null && ps[0] == 10 && ps[1] == 2 && ps[2] == 1, "position 10th of 9 columns: row 2, place 1");
    ps = LocateGuide.Position(l9, 15, 6); Check(ps[1] == 1 && ps[2] == 6, "6th of 6 columns: row 1, place 6");
    ps = LocateGuide.Position(l9, 17, 6); Check(ps[1] == 2 && ps[2] == 2, "8th of 6 columns: row 2, place 2");
    ps = LocateGuide.Position(l9, 20, 8); Check(ps[0] == 11 && ps[1] == 2 && ps[2] == 3, "11th of 8 columns: row 2, place 3");
    Check(LocateGuide.Position(l9, 99, 8) == null && LocateGuide.Position(null, 1, 8) == null && LocateGuide.Position(l9, 0, 8) == null, "position: absent");
    ps = LocateGuide.Position(l9, 12, 0); Check(ps[1] == 3, "position: zero columns count as one");

    // steps
    var item = LocItem(777, 4, 720100, 12, 5, 7, 9, 3);
    var req = new LocateRequest { Id = "l1", Purpose = "recast", Item = item, HeroUid = 200 };
    Func<InventoryScreen> inv = () => new InventoryScreen { Open = true, EquipTab = true, Uids = new List<long>(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 777 }) };
    Eq(LocateKind.None, LocateGuide.Next(null, null, null, null, 8, -1).Kind, "step: no request");
    Eq(LocateKind.NoItem, LocateGuide.Next(new LocateRequest { Id = "x" }, null, null, null, 8, -1).Kind, "step: item gone from the snapshot");
    Eq(LocateKind.OtherAccount, LocateGuide.Next(req, inv(), null, null, 8, 0).Kind, "step: another account in the game");
    var st = LocateGuide.Next(req, null, null, null, 8, 1);
    Check(st.Kind == LocateKind.OpenInventory && !st.OnHero, "step: nothing open -> open the inventory");
    st = LocateGuide.Next(req, new InventoryScreen { Open = false }, null, new HeroScreen { Hero = 200, Tab = 3 }, 8, 1);
    Check(st.Kind == LocateKind.OpenInventory && st.OnHero, "step: the item's hero is open -> press the slot there");
    st = LocateGuide.Next(req, new InventoryScreen { Open = false }, null, new HeroScreen { Hero = 300, Tab = 3 }, 8, 1);
    Check(!st.OnHero, "step: another hero is open");
    Eq(LocateKind.OpenEquipTab, LocateGuide.Next(req, new InventoryScreen { Open = true, EquipTab = false }, null, null, 8, 1).Kind, "step: inventory on another tab");
    st = LocateGuide.Next(req, inv(), null, null, 8, 1);
    Check(st.Kind == LocateKind.InList && st.Pos == 10 && st.Row == 2 && st.Col == 2 && st.Count == 10, "step: in the list, row 2 place 2");
    var iv = inv(); iv.Uids.Remove(777);
    Eq(LocateKind.HiddenFilter, LocateGuide.Next(req, iv, null, null, 8, 1).Kind, "step: not in the list but on the account -> a filter hides it");
    Eq(LocateKind.HiddenFilter, LocateGuide.Next(req, iv, null, null, 8, -1).Kind, "step: not in the list, account unknown");
    iv = inv(); iv.Current = 777;
    Eq(LocateKind.Selected, LocateGuide.Next(req, iv, null, null, 8, 1).Kind, "step: selected in the inventory");
    iv.Current = 5;
    Eq(LocateKind.InList, LocateGuide.Next(req, iv, null, null, 8, 1).Kind, "step: another item selected -> still in the list");

    // the overlay's owner when a plan and a locate request are both on: the later one wins
    Eq("none", OverlayPriority.Winner(false, 0, false, 0), "priority: nothing on");
    Eq("plan", OverlayPriority.Winner(true, 5, false, 0), "priority: only a plan");
    Eq("locate", OverlayPriority.Winner(false, 0, true, 5), "priority: only a locate request");
    Eq("locate", OverlayPriority.Winner(true, 100, true, 200), "priority: locate started after the plan -> locate");
    Eq("plan", OverlayPriority.Winner(true, 300, true, 200), "priority: plan started after locate -> plan");
    Eq("locate", OverlayPriority.Winner(true, 200, true, 200), "priority: same moment -> locate");

    // recast path: the gem screen holds the item, its function is still to choose; the card's button comes first
    var gs = new RecastScreen { GemOpen = true, GemSel = 777, GemFunc = 1 };
    Eq(LocateKind.GemFunction, LocateGuide.Next(req, null, gs, null, 8, 1).Kind, "step: gem screen with the item, function not chosen -> choose «Преобразование»");
    gs.GemFunc = 7;
    st = LocateGuide.Next(req, null, gs, null, 8, 1);
    Check(st.Kind == LocateKind.Done && st.OnGem, "step: gem screen with Recast chosen and the item -> done (press «Улучшить»)");
    var arq = new LocateRequest { Id = "a", Purpose = "ascend", Item = req.Item };
    iv = inv(); iv.Current = 777;
    Eq(LocateKind.OpenFoundry, LocateGuide.Next(arq, iv, null, null, 8, 1).Kind, "step: ascend with the item selected in the inventory -> the Foundry button (never the card's buttons)");

    // the foundry
    Func<RecastScreen> fr = () => new RecastScreen { SublimingOpen = true, PanelShown = true, CurType = 3 };
    var rs = fr(); rs.CurType = 2;
    Eq(LocateKind.OpenRecastTab, LocateGuide.Next(req, null, rs, null, 8, 1).Kind, "step: foundry on another tab");
    rs = fr(); rs.Equips = new long[40]; for (int i = 0; i < 40; i++) rs.Equips[i] = 100 + i; rs.Equips[30] = 777;
    st = LocateGuide.Next(req, null, rs, null, 8, 1);
    Check(st.Kind == LocateKind.RecastFilter && st.Missing.Length == 4 && st.Missing[0] == "slot" && st.Missing[3] == "subs" && st.Pos == 31 && st.Count == 40, "step: long list, nothing set -> filter (all four parts missing)");
    rs.SelectParts = new[] { 4 }; rs.Suits = new long[] { 720100 }; rs.MainAttrs = new long[] { 12 }; rs.SubAttrs = new long[] { 5, 7 };
    st = LocateGuide.Next(req, null, rs, null, 8, 1);
    Check(st.Kind == LocateKind.RecastFilter && st.Missing.Length == 1 && st.Missing[0] == "subs", "step: only the sub stats missing (the other parts are marked done)");
    rs.SubAttrs = new long[] { 5, 7, 9, 3 };
    Eq(LocateKind.RecastPick, LocateGuide.Next(req, null, rs, null, 8, 1).Kind, "step: filter complete, long list -> pick");
    rs.Equips = new long[] { 100, 777 };
    st = LocateGuide.Next(req, null, rs, null, 8, 1);
    Check(st.Kind == LocateKind.RecastPick && st.Pos == 2 && st.Count == 2, "step: short list -> position 2 of 2");
    rs.Equips = new long[] { 100, 101 };
    Eq(LocateKind.RecastNotInList, LocateGuide.Next(req, null, rs, null, 8, 1).Kind, "step: filter set but the item is not in the list");
    rs.SubAttrs = new long[] { 5 }; rs.Equips = new long[] { 777, 100 };
    Eq(LocateKind.RecastPick, LocateGuide.Next(req, null, rs, null, 8, 1).Kind, "step: short list with an incomplete filter -> pick");
    rs.Equips = new long[] { 100, 101 };
    Eq(LocateKind.RecastFilter, LocateGuide.Next(req, null, rs, null, 8, 1).Kind, "step: not in the list and filter incomplete -> filter");
    rs.ChoosePending = true;
    Eq(LocateKind.ChoosePending, LocateGuide.Next(req, null, rs, null, 8, 1).Kind, "step: a recast result waits for the choice");
    rs = fr(); rs.Sel = 777; rs.Equips = new long[] { 777 };
    Eq(LocateKind.Done, LocateGuide.Next(req, null, rs, null, 8, 1).Kind, "step: the foundry has the item selected -> done");
    rs.Sel = 5; rs.Equips = new long[] { 777, 5 };
    Eq(LocateKind.RecastPick, LocateGuide.Next(req, null, rs, null, 8, 1).Kind, "step: another item selected in the foundry");
    rs = fr(); rs.PanelShown = false;
    Eq(LocateKind.OpenInventory, LocateGuide.Next(req, null, rs, null, 8, 1).Kind, "step: foundry form without its panel on screen is not the recast screen");

    // the gem screen
    var gm = new RecastScreen { GemOpen = true, GemFunc = 7, GemSel = 5, GemList = new long[] { 5, 6, 777 } };
    st = LocateGuide.Next(req, inv(), gm, null, 8, 1);
    Check(st.Kind == LocateKind.GemPick && st.Pos == 3 && st.Count == 3, "step: gem screen, other item selected -> pick it in the pop-up");
    gm.GemSel = 777; Eq(LocateKind.Done, LocateGuide.Next(req, inv(), gm, null, 8, 1).Kind, "step: gem screen with the item -> done");
    gm.GemSel = 5; gm.GemFunc = 8; gm.GemList = new long[] { 5, 6 };
    Eq(LocateKind.RecastNotInList, LocateGuide.Next(req, inv(), gm, null, 8, 1).Kind, "step: gem screen, the item is not in the pop-up's list");
    gm.GemFunc = 3; gm.GemSel = 777;
    Eq(LocateKind.GemFunction, LocateGuide.Next(req, inv(), gm, null, 8, 1).Kind, "step: gem screen holds the item, another function -> choose the recast function");
    gm.GemSel = 5;
    Eq(LocateKind.InList, LocateGuide.Next(req, inv(), gm, null, 8, 1).Kind, "step: another gem function and another item -> the inventory's step");
    Eq("inList", LocateGuide.Next(req, inv(), null, null, 8, 1).Key, "step key");
    Eq("openRecastTab", new LocateStep { Kind = LocateKind.OpenRecastTab }.Key, "step key camel case");

    // Ascension (purpose "ascend")
    var areq = new LocateRequest { Id = "a1", Purpose = "ascend", Item = item, HeroUid = 200 };
    Func<RecastScreen> ar = () => new RecastScreen { SublimingOpen = true, AscendShown = true, CurType = 2, AscendEquips = new long[] { 100, 777, 101 } };
    var ars = ar();
    st = LocateGuide.Next(areq, null, ars, null, 8, 1);
    Check(st.Kind == LocateKind.AscendPick && st.Pos == 2 && st.Count == 3, "ascend: item in the Ascension list -> pick");
    ars.AscendSel = 777; Eq(LocateKind.AscendReady, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: selected -> press Ascend");
    ars.AscendSel = 5; ars.AscendEquips = new long[] { 100 }; ars.AscendList = new RFX.ListRead { ScrollY = 0, Cols = 5, RowUnits = 108, First = 0, Last = 0 };
    ars.AscendParts = new[] { 4 }; ars.AscendSuits = new long[] { 720100 }; ars.AscendMainAttrs = new long[] { 12 }; ars.AscendSubAttrs = new long[] { 5, 7, 9, 3 };
    Eq(LocateKind.AscendNotInList, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: not in the list (filter complete)");
    ars = ar(); ars.CurType = 3; ars.AscendShown = false;
    Eq(LocateKind.OpenAscendTab, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: foundry on another tab");
    ars = ar(); ars.AscendShown = false;
    Eq(LocateKind.OpenAscendTab, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: tab switching, panel not on screen yet -> tab step");
    // regression: the filter pop-up fields are all absent (pop-up closed, Forge tab): the foundry is still open, never the inventory step
    ars = new RecastScreen { SublimingOpen = true, CurType = 3, PanelShown = true };   // Forge tab, defaults everywhere else
    Check(!ars.AscendFilterOpen && ars.FilterPanel == 0 && ars.FilterShowSuits.Length == 0 && ars.AscendList == null && ars.GemFuncList == null, "foundry state: optional pop-up / gem fields default to empty");
    Eq(LocateKind.OpenAscendTab, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: Foundry on the Forge tab, no pop-up fields -> open the Ascension tab");
    Eq(LocateKind.OpenAscendTab, LocateGuide.Next(areq, inv(), ars, null, 8, 1).Kind, "ascend: Foundry on the Forge tab with the inventory still open -> the tab step, not step 1");
    ars = new RecastScreen { SublimingOpen = true, CurType = 2, AscendShown = true, AscendEquips = new long[] { 100, 777 } };   // Ascend tab, pop-up fields absent, list unread
    Eq(LocateKind.AscendPick, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: Ascension tab, pop-up fields absent, item in the list -> pick");
    ars.AscendEquips = new long[] { 100 };
    Eq(LocateKind.AscendLoading, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: Ascension tab, pop-up fields absent, list not read -> loading");
    // regression: the gem screen with its optional list fields absent still reads as the gem screen (never the inventory step)
    var rreq = new LocateRequest { Id = "g1", Purpose = "recast", Item = item, HeroUid = 200 };
    var gsOnly = new RecastScreen { GemOpen = true, GemFunc = -1, GemSel = 777 };
    Eq(LocateKind.GemFunction, LocateGuide.Next(rreq, null, gsOnly, null, 8, 1).Kind, "recast: gem screen, function list fields absent, item selected -> choose the function");
    // the way to the foundry: nothing is chosen before it, the item is never framed in the inventory
    Eq(LocateKind.OpenInventory, LocateGuide.Next(areq, null, null, null, 8, 1).Kind, "ascend: nothing open (item in the bag) -> open the inventory");
    Eq(LocateKind.OpenFoundry, LocateGuide.Next(areq, inv(), null, null, 8, 1).Kind, "ascend: inventory on the equipment tab -> the Foundry button, not the item's cell");
    // the job done: Ascension (ready -> the item is gone from the shrunk list) and recast (sub stats changed / a result waits)
    {
      var flist = new RFX.ListRead { ScrollY = 0, Cols = 5, RowUnits = 108, First = 0, Last = 0 };
      var fnz = new LocateFinish();
      var fre = ar(); fre.AscendSel = 777; fre.AscendList = flist; fre.AscendParts = new[] { 4 };
      Eq("", fnz.Update(true, 777, LocateKind.AscendReady, fre, null), "finish: ready is not done");
      var gne = ar(); gne.AscendEquips = new long[] { 100, 101 }; gne.AscendList = flist; gne.AscendParts = new[] { 4 };
      Eq("", fnz.Update(true, 777, LocateKind.AscendLoading, gne, null, 1000), "finish: gone on the first read only -> wait");
      Eq("", fnz.Update(true, 777, LocateKind.AscendLoading, gne, null, 1100), "finish: gone, second read too close -> wait");
      Eq("ascend", fnz.Update(true, 777, LocateKind.AscendLoading, gne, null, 1300), "finish: ready, then the item stays gone from the shorter list -> done");
      fnz.Reset(); fnz.Update(true, 777, LocateKind.AscendReady, fre, null);
      Eq("", fnz.Update(true, 777, LocateKind.AscendLoading, gne, null, 1000), "finish: blip start");
      Eq("", fnz.Update(true, 777, LocateKind.AscendReady, fre, null, 1300), "finish: the item is back (one-read blip) -> not done");
      Eq("", fnz.Update(true, 777, LocateKind.AscendLoading, gne, null, 1600), "finish: gone again, the clock starts over");
      Eq("", fnz.Update(true, 777, LocateKind.AscendLoading, gne, null, 1700), "finish: ... still waiting");
      fnz.Reset(); fnz.Update(true, 777, LocateKind.AscendReady, fre, null);
      var fch = ar(); fch.AscendEquips = new long[] { 100, 101 }; fch.AscendList = flist; fch.AscendParts = new[] { 4 }; fch.AscendSuits = new long[] { 720100 };
      fnz.Update(true, 777, LocateKind.AscendFilter, fch, null, 1000);
      Eq("", fnz.Update(true, 777, LocateKind.AscendFilter, fch, null, 1400), "finish: the set filter changed -> not done");
      fnz.Reset(); fnz.Update(true, 777, LocateKind.AscendReady, fre, null);
      var tabx = ar(); tabx.AscendEquips = new long[] { 100 }; tabx.AscendList = flist; tabx.AscendParts = new[] { 2 };
      Eq("", fnz.Update(true, 777, LocateKind.AscendNotInList, tabx, null), "finish: another slot tab (the list changed) is not done");
      fnz.Reset();
      Eq("", fnz.Update(true, 777, LocateKind.AscendFilter, gne, null), "finish: never ready -> not done");
      fnz.Reset(); fnz.Update(true, 777, LocateKind.AscendReady, fre, null);
      var unrd = ar(); unrd.AscendEquips = new long[0];
      Eq("", fnz.Update(true, 777, LocateKind.AscendLoading, unrd, null), "finish: the list is not readable -> wait");
      var rcf = new LocateFinish();
      var subs0 = new[] { new long[] { 5, 10 }, new long[] { 7, 20 } };
      Eq("", rcf.Update(false, 777, LocateKind.GemPick, null, subs0), "finish: recast, the Done step not reached yet");
      Eq("", rcf.Update(false, 777, LocateKind.Done, null, subs0), "finish: Done reached, subs the same");
      Eq("", rcf.Update(false, 777, LocateKind.RecastPick, null, subs0), "finish: left the Done step, subs the same -> wait");
      Eq("recast", rcf.Update(false, 777, LocateKind.RecastPick, null, new[] { new long[] { 5, 10 }, new long[] { 9, 20 } }), "finish: sub stats differ -> done");
      rcf.Reset(); rcf.Update(false, 777, LocateKind.Done, null, subs0);
      Eq("recast", rcf.Update(false, 777, LocateKind.ChoosePending, new RecastScreen { ChoosePending = true }, subs0), "finish: a result waits for the choice -> done");
    }
    iv = inv(); iv.Current = 777;
    Eq(LocateKind.OpenFoundry, LocateGuide.Next(areq, iv, null, null, 8, 1).Kind, "ascend: the item selected in the inventory does not matter (the foundry starts empty)");
    iv = inv(); iv.EquipTab = false;
    Eq(LocateKind.OpenEquipTab, LocateGuide.Next(areq, iv, null, null, 8, 1).Kind, "ascend: inventory on another tab -> the equipment tab");
    var gearHero = new HeroScreen { Hero = 200, Tab = 3 };
    st = LocateGuide.Next(areq, null, null, gearHero, 8, 1);
    Check(st.Kind == LocateKind.OpenFoundry && st.OnGear && st.OnHero, "ascend: worn item, hero gear screen open -> the Foundry button there (the card has no Ascension button)");
    Eq(LocateKind.OpenFoundry, LocateGuide.Next(areq, inv(), null, gearHero, 8, 1).Kind, "ascend: the inventory is open too -> its button (the screen the player is on)");
    Eq(LocateKind.OpenInventory, LocateGuide.Next(areq, null, null, new HeroScreen { Hero = 200, Tab = 1 }, 8, 1).Kind, "ascend: hero screen on another tab -> back to the inventory way");
    // the foundry's own list: columns, the game's rows on screen, the filter
    ars = ar(); ars.AscendEquips = new long[] { 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 777 };
    ars.AscendList = new RFX.ListRead { ScrollY = 0, Cols = 4, RowUnits = 100, First = 0, Last = 7 };
    st = LocateGuide.Next(areq, null, ars, null, 8, 1);
    Check(st.Kind == LocateKind.AscendPick && st.Pos == 12 && st.Row == 3 && st.Col == 4 && st.Scroll == "below" && st.ScrollRows == 1 && st.Missing.Length > 0, "ascend: long list, item in it, filter unset -> pick anyway (not the filter); item at row 3 place 4, one row below the cells on screen");
    ars.AscendParts = new[] { 4 }; ars.AscendSuits = new long[] { 720100 }; ars.AscendMainAttrs = new long[] { 12 }; ars.AscendSubAttrs = new long[] { 5, 7, 9, 3 };
    st = LocateGuide.Next(areq, null, ars, null, 8, 1);
    Check(st.Kind == LocateKind.AscendPick && st.Scroll == "below" && st.ScrollRows == 1, "ascend: filter set -> pick, with the rows to scroll");
    ars.AscendList = new RFX.ListRead { ScrollY = 200, Cols = 4, RowUnits = 100, First = 8, Last = 15 };
    Eq("visible", LocateGuide.Next(areq, null, ars, null, 8, 1).Scroll, "ascend: item among the cells on screen");
    ars.AscendList = new RFX.ListRead { ScrollY = 500, Cols = 4, RowUnits = 100, First = 20, Last = 27 };
    st = LocateGuide.Next(areq, null, ars, null, 8, 1);
    Check(st.Scroll == "above" && st.ScrollRows == 3, "ascend: item above the cells on screen -> rows to scroll up");
    {   // a long list (3+ screens), the item off the screen: the slot tab, then the pick (the scroll); the set filter is never a step here (the pilot could not go on from it)
      var lg = ar(); var ids = new List<long>(); for (int q = 0; q < 112; q++) ids.Add(1000 + q); ids.Insert(90, 777); lg.AscendEquips = ids.ToArray();
      lg.AscendList = new RFX.ListRead { ScrollY = 0, Cols = 5, RowUnits = 100, First = 0, Last = 14 };
      lg.AscendParts = new int[0]; lg.AscendSuits = new long[0]; lg.AscendMainAttrs = new long[0]; lg.AscendSubAttrs = new long[0];
      st = LocateGuide.Next(areq, null, lg, null, 8, 1);
      Check(st.Kind == LocateKind.AscendFilter && st.Pos == 91 && st.Missing.Length == 1 && st.Missing[0] == "slot", "ascend: 113 items, item off the screen, nothing set -> the slot tab only (not the set; main / subs not asked)");
      string fwhy;
      lg.AscendParts = new[] { item.Slot };
      st = LocateGuide.Next(areq, null, lg, null, 8, 1);
      Check(st.Kind == LocateKind.AscendPick && st.Pos == 91 && Array.IndexOf(st.Missing, "suit") >= 0, "ascend: slot set, set missing, still long -> pick (scroll to it), not the set filter (live audit: the pilot waited forever there)");
      Eq(LocateTodo.AscendCell, LocateAuto.Decide(new LocateView { Step = st, Rs = lg, Item = item, W = 2000, H = 1000, U = 1000, Geometry = GameGeometry.Parse(GeoJson(), GeoGame), Foreground = true }, out fwhy), "ascend: ... the pilot scrolls the list (AscendCell), it does not wait at the filter");
      lg.AscendSuits = new long[] { item.SetId };
      Eq(LocateKind.AscendPick, LocateGuide.Next(areq, null, lg, null, 8, 1).Kind, "ascend: slot and set set -> pick (main / sub stats are not asked before the scroll)");
      lg.AscendParts = new int[0]; lg.AscendSuits = new long[0]; lg.AscendList = new RFX.ListRead { ScrollY = 9000, Cols = 5, RowUnits = 100, First = 85, Last = 99 };
      Eq(LocateKind.AscendPick, LocateGuide.Next(areq, null, lg, null, 8, 1).Kind, "ascend: the item is on the screen -> pick at once, no filters");
      lg.AscendList = new RFX.ListRead { ScrollY = 0, Cols = 5, RowUnits = 100, First = 0, Last = 59 };
      Eq(LocateKind.AscendPick, LocateGuide.Next(areq, null, lg, null, 8, 1).Kind, "ascend: the list is at most two screens -> pick (scroll)");
    }
    ars.AscendList = null;
    st = LocateGuide.Next(areq, null, ars, null, 8, 1);
    Check(st.Kind == LocateKind.AscendPick && st.Scroll == "" && st.Pos == 12 && st.Col == 1, "ascend: list not readable -> position only");
    ars.AscendEquips = new long[] { 100, 101 };
    Eq(LocateKind.AscendLoading, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: the list is not readable yet and the item is not in it -> wait, no filter advice");
    ars.AscendList = new RFX.ListRead { ScrollY = 0, Cols = 4, RowUnits = 100, First = 0, Last = 1 };
    Eq(LocateKind.AscendNotInList, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: filter complete, item not in the list -> not ascendable / extra stat");
    ars.AscendSubAttrs = new long[0];
    Eq(LocateKind.AscendFilter, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: not in the list and the filter incomplete -> filter hint");
    ars.AscendSel = 777; ars.AscendEquips = new long[] { 100 };
    Check(LocateGuide.Next(areq, null, ars, null, 8, 1).Kind != LocateKind.AscendReady, "ascend: a stale selection that the list does not hold is not 'ready'");
    // the filter pop-up: what to press (the filter is hints only)
    {
      var pr = ar(); pr.AscendEquips = new long[] { 100 }; pr.AscendList = new RFX.ListRead { ScrollY = 0, Cols = 5, RowUnits = 108, First = 0, Last = 0 };
      pr.AscendParts = new[] { 4 }; pr.AscendMainAttrs = new long[] { 12 }; pr.AscendSubAttrs = new long[] { 5, 7, 9, 3 };
      pr.FilterLeftSuits = new long[] { 720200 }; pr.FilterRightSuits = new long[] { 720100, 720101 }; pr.FilterShowSuits = new long[] { 720101, 720100 };
      st = LocateGuide.Next(areq, null, pr, null, 8, 1);
      Check(st.Kind == LocateKind.AscendFilter && st.FilterGo == "" && Array.IndexOf(st.Missing, "suit") >= 0, "pop-up closed: the filter step, nothing to press inside");
      pr.AscendFilterOpen = true; pr.FilterPanel = 0;
      Eq("suitRow", LocateGuide.Next(areq, null, pr, null, 8, 1).FilterGo, "pop-up open, set panel closed -> the set row");
      pr.FilterPanel = 1; pr.FilterShowSuits = new long[] { 720200 };
      st = LocateGuide.Next(areq, null, pr, null, 8, 1);
      Check(st.FilterGo == "suitTab" && st.SuitTab == 1, "set panel shows the 2-piece tab, the item's set is 3-piece -> the 3-piece tab");
      pr.FilterShowSuits = new long[] { 720101, 720100 };
      st = LocateGuide.Next(areq, null, pr, null, 8, 1);
      Check(st.FilterGo == "suitCell" && st.SuitTab == 1 && st.SuitIndex == 1, "right tab shown -> the set's cell, its place in the grid");
      pr.AscendSuits = new long[] { 720100 }; pr.AscendSubAttrs = new long[] { 5, 7, 9, 3, 99 };
      st = LocateGuide.Next(areq, null, pr, null, 8, 1);
      Check(st.Kind == LocateKind.AscendFilter && st.FilterGo == "reset", "a sub stat the item lacks hides it -> «Сбросить»");
      pr.AscendParts = new[] { 0 };
      Eq("", LocateGuide.Next(areq, null, pr, null, 8, 1).FilterGo, "the slot is still wrong -> the slot first, nothing in the pop-up");
      pr.AscendParts = new[] { 4 }; pr.AscendSubAttrs = new long[] { 5, 7, 9, 3 }; pr.AscendMainAttrs = new long[0];
      Eq("mainRow", LocateGuide.Next(areq, null, pr, null, 8, 1).FilterGo, "set chosen, main stat missing -> the main stat row");
      pr.AscendEquips = new long[] { 100, 777 };
      Eq(LocateKind.AscendPick, LocateGuide.Next(areq, null, pr, null, 8, 1).Kind, "the item shows in the list -> pick, the pop-up is no longer needed");
    }
    // the list math with fake data
    var wl = new LocateStep(); LocateGuide.Where(wl, new RFX.ListRead { Cols = 8, First = 16, Last = 40 }, 100);
    Check(wl.Scroll == "below" && wl.ScrollRows == 7, "list math: index 100 of 8 columns = row 12, cells end at row 5 -> 7 rows below");
    wl = new LocateStep(); LocateGuide.Where(wl, new RFX.ListRead { Cols = 8, First = -1, Last = -1 }, 3);
    Eq("", wl.Scroll, "list math: cells unknown -> nothing said");
    // the inventory grid's row 1 from the game's scroll (no picture): scrolled 2.5 rows of 118 units
    var isg = SellGeometry.Inventory(1920, 1009, 8); var iscr = new InvScroll();
    double r1 = iscr.Row1(isg, new RFX.ListRead { ScrollY = 295, Cols = 8, RowUnits = 118 }, double.NaN, 1009);
    Check(Math.Abs(r1 - (isg.ViewTopPx(1009) + isg.TopPad * 1009 - 2.5 * isg.PitchY * 1009)) < 1e-6, "inventory scroll: row 1 = list top minus the scrolled rows");
    Check(double.IsNaN(iscr.Row1(isg, null, double.NaN, 1009)) && double.IsNaN(iscr.Row1(isg, new RFX.ListRead(), double.NaN, 1009)), "inventory scroll: unreadable -> NaN (the picture is the fallback)");
    double r1b = new InvScroll().Row1(isg, new RFX.ListRead { ScrollY = 118, Cols = 8 }, double.NaN, 1009);
    Check(Math.Abs((isg.ViewTopPx(1009) + isg.TopPad * 1009 - r1b) - isg.PitchY * 1009) < 1e-6, "inventory scroll: row units unknown -> 118 per row");
    var sv = new LocateGrid(); sv.FromScroll(-340.5, 42);
    Check(sv.Has && sv.Exact && sv.Row1 == -340.5 && sv.ListPtr == 42, "grid: placed from the scroll"); sv.Reset(); Check(!sv.Has && !sv.Exact, "grid: reset");
    // the exact row 1 drives the sell pilot: it clicks the item's cell with no drag when the scroll says it is in view
    {
      var gg = SellGeometry.Inventory(1920, 1009, 8); var sp2 = new SellPilot(gg);
      var uidsL = new List<long>(); for (int k = 1; k <= 40; k++) uidsL.Add(9000 + k);
      var scr2 = new InventoryScreen { Open = true, EquipTab = true, Bulk = true, ListPtr = 5 }; scr2.Uids.AddRange(uidsL);
      double row1Want = gg.ViewTopPx(1009) + gg.TopPad * 1009 - 1.0 * gg.PitchY * 1009;   // scrolled one row down
      var act = sp2.Step(new SellView { NowMs = 10000, Foreground = true, W = 1920, H = 1080, U = 1009, Screen = scr2.AsSell(8), Wanted = new HashSet<long> { 9020 }, Row1 = row1Want });
      double wantY = row1Want + gg.RowOffset(null, 3) * 1009 + gg.CellHeight * 1009 / 2;
      Check(act.Kind == AutoKind.Click && Math.Abs(act.Y - wantY) < 1, "sell pilot: with the game's scroll it clicks the item's row where the scroll puts it (no drag)");
    }
    Eq(LocateKind.OtherAccount, LocateGuide.Next(areq, inv(), ar(), null, 8, 0).Kind, "ascend: another account");
    var gmA = new RecastScreen { GemOpen = true, GemFunc = 7, GemSel = 777, GemList = new long[] { 777 } };
    Eq(LocateKind.OpenFoundry, LocateGuide.Next(areq, inv(), gmA, null, 8, 1).Kind, "ascend: the gem screen is not part of it");
    Eq(LocateKind.AscendReady, LocateGuide.Next(areq, null, new RecastScreen { SublimingOpen = true, AscendShown = true, CurType = 2, AscendSel = 777, AscendEquips = new long[] { 777 } }, null, 8, 1).Kind, "ascend: ready needs the item selected and in the shown list");
    Check(LocateGuide.Next(areq, null, new RecastScreen { SublimingOpen = true, AscendShown = true, CurType = 2, AscendSel = 777 }, null, 8, 1).Kind != LocateKind.AscendReady, "ascend: a selection the list does not hold (stale right after the tab opens) is not ready");
    var aj = PlansClient.Interpret(200, "{\"ok\":true,\"locate\":{\"id\":\"a1\",\"purpose\":\"ascend\",\"item\":null}}", true).Locate;
    Check(aj != null && aj.Purpose == "ascend", "locate: purpose ascend accepted");
    Eq("ascendReady", new LocateStep { Kind = LocateKind.AscendReady }.Key, "ascend: step key");

    // sub stats against the game's
    var chk = LocateGuide.CheckSubs(item, new[] { new long[] { 5, 10 }, new long[] { 7, 0 }, new long[] { 9, 0 }, new long[] { 3, 0 } });
    Check(chk.Match.Length == 4 && chk.Match[0] && chk.Match[3] && !chk.Changed, "sub check: same four stats");
    chk = LocateGuide.CheckSubs(item, new[] { new long[] { 5, 10 }, new long[] { 7, 0 }, new long[] { 9, 0 }, new long[] { 8, 0 } });
    Check(chk.Match[2] && !chk.Match[3] && chk.Changed, "sub check: a stat changed after the snapshot");
    Check(LocateGuide.CheckSubs(item, null).Match.Length == 0, "sub check: unreadable");

    // the inventory's grid: row positions from the stat bars
    {
      double W = 1920, H = 1009, U = Ui.Unit(W, H);
      var g = SellGeometry.Inventory(W, U, 8);
      var grid = new LocateGrid();
      double top = g.ViewTopPx(U) + g.TopPad * U, pitch = g.PitchY * U;
      Check(grid.FromTop(g, top, U, 5), "grid: bars at the top -> anchored");
      Check(Math.Abs(grid.Row1 - top) < 0.01 && grid.ListPtr == 5, "grid: row 1 at the top");
      var mk = grid.Place(g, 1, 3, W, H, U);
      Check(mk.Kind == "box" && mk.X == (int)g.X(g.X0 + 2 * g.PitchX, W, U) && mk.Y == (int)top, "grid: box around row 1, place 3");
      mk = grid.Place(g, 30, 1, W, H, U);
      Check(mk.Kind == "down" && mk.Rows >= 20, "grid: far down -> arrow with the rows to scroll (" + mk.Rows + ")");
      var g2 = new LocateGrid(); g2.FromTop(g, top, U, 1);
      g2.Row1 -= 10 * pitch;   // scrolled 10 rows
      mk = g2.Place(g, 2, 1, W, H, U);
      Check(mk.Kind == "up" && mk.Rows == 9, "grid: scrolled past -> arrow up (" + mk.Rows + ")");
      Check(!new LocateGrid().FromTop(g, top + pitch * 0.4, U, 1), "grid: bars not where the top would put them -> no anchor");
      var g3 = new LocateGrid(); g3.FromTop(g, top, U, 1);
      g3.Row1 -= 3 * pitch + 7;   // the list moved by 3 rows and a bit; the bars say where
      int rr = g3.VisibleRow(g, 20, W, H, U);
      Check(rr > 0, "grid: a visible row to take the phase from");
      double truth = top - 3 * pitch;   // the real position (an estimate off by 7 px)
      g3.Track(g, rr, ((truth % pitch) + pitch) % pitch, U);
      Check(Math.Abs(g3.Row1 - truth) < 0.5, "grid: the bars' phase corrects the estimate (" + g3.Row1.ToString("0.0") + " vs " + truth.ToString("0.0") + ")");
      var g4 = new LocateGrid(); g4.FromClick(g, 4, 500, U, 9);
      Check(g4.Has && g4.ListPtr == 9 && Math.Abs(g4.RowTop(g, 4, U) - (500 - g.BarBottom * U / 2)) < 0.01, "grid: a click on row 4 anchors the list");
      g4.Reset(); Check(!g4.Has, "grid: reset");
    }
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
      RFX.SetUiTableForTest(0);
      Eq("window table not found yet", RFX.ShownFormsDiag(8), "window table: not found - the log line says so");
      Check(RFX.ReadShownKnownForms() == null, "window table not found: the known forms are not read (null)");
    } finally { RFX.SetUiTableForTest(0); RFX.TestMemory = null; RFX.ForgetLive(); }
  }

  // the unknown-screen back press (ClickTarget.UnknownBack): only on a full-screen screen nobody knows, at most 3 a run, each has to change the screen
  static void UnknownBackTests() {
    Console.WriteLine("Unknown screen: the back arrow");
    const double W = 1366, H = 705; double U = Ui.Unit(W, H);
    var hg = new HeroGeometry(); double bx = hg.BackX * U, by = hg.BackY * U;
    Func<string, string, ScreenState> Unk = (top, known) => { var s = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = top, ShownKnown = known }, 0); return s; };
    Func<ClickOwner, ClickRequest> Rq = o => new ClickRequest { Owner = o, Target = ClickTarget.UnknownBack, AutoOn = true, Front = true, X = bx, Y = by, U = U };
    var plain = Unk("Form_Odd", "");
    Eq("", ClickGate.UnknownBackBlock(plain), "unknown back: a full-screen unknown screen without a pop-up allows it");
    Check(ClickGate.OnBackArrow(bx, by, U) && !ClickGate.OnBackArrow(bx + 0.05 * U, by, U) && !ClickGate.OnBackArrow(W / 2, H / 2, U), "unknown back: the point is the hero screen's back arrow, nothing else");
    // known screens and everything else that is not "a full-screen unknown screen" never get it
    var inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true };
    var known = new Dictionary<string, ScreenState> {
      { "City (form)", ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string>(), ShownKnown = "" }, 0) },
      { "City (picture)", ScreenState.Classify(new ScreenInputs { CityForm = false, CitySeen = true, Offers = new List<string>(), ShownKnown = "" }, 0) },
      { "Inventory", ScreenState.Classify(new ScreenInputs { Inv = inv, Offers = new List<string>(), ShownKnown = "" }, 0) },
      { "HeroScreen", ScreenState.Classify(new ScreenInputs { Hero = new HeroScreen { Hero = 5, FormOk = true }, Offers = new List<string>(), ShownKnown = "" }, 0) },
      { "Foundry", ScreenState.Classify(new ScreenInputs { Rs = new RecastScreen { SublimingOpen = true }, Offers = new List<string>(), ShownKnown = "" }, 0) },
      { "Gem", ScreenState.Classify(new ScreenInputs { Rs = new RecastScreen { GemOpen = true }, Offers = new List<string>(), ShownKnown = "" }, 0) },
      { "ArtWorkshop", ScreenState.Classify(new ScreenInputs { ArtWs = new ArtWorkshopScreen { Open = true }, Offers = new List<string>(), ShownKnown = "" }, 0) },
      { "an offer over the city", ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string> { "Form_PopUpShop" }, ShownKnown = "" }, 0) },
      { "an offer on an unknown screen", ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string> { "Form_EquipmentPlan" }, ShownKnown = "" }, 0) },
      { "a pop-up seen on the picture", ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), ModalExtra = true, ModalExtraWhy = "popup", ShownKnown = "" }, 0) },
      { "the battle shown", Unk("", "Form_Battle") },
      { "the hero screen's form shown", Unk("", "Form_CharactorMain") },
      { "the known forms not read", Unk("Form_Odd", null) },
      { "the window table not read", ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = null, ShownKnown = "" }, 0) },
      { "a fight's result", Unk("Form_BattleEnd", "") },
    };
    foreach (var kv in known) {
      var s = kv.Value; s.SinceMs = 0;
      Check(ClickGate.UnknownBackBlock(s) != "", "unknown back: never on " + kv.Key + " (" + ClickGate.UnknownBackBlock(s) + ")");
      foreach (ClickOwner o in new[] { ClickOwner.Equip, ClickOwner.Locate, ClickOwner.Sell }) {
        var g = new ClickGate();
        var r = g.Check(Rq(o), s, 10000);
        Check(!r.Allowed && !g.Halted(o), "unknown back: " + o + " gets no press on " + kv.Key + " (" + r.Reason + ")");
        if (s.Kind != ScreenKind.Unknown || s.ModalOpen || s.OfferCount > 0 || !s.OfferRead) Check(!g.UnknownBackDue(o, s, 10000), "unknown back: " + o + " is never due on " + kv.Key);
      }
    }
    var held = Unk("Form_Odd", ""); held.Held = true;
    Check(ClickGate.UnknownBackBlock(held) != "" && !new ClickGate().UnknownBackDue(ClickOwner.Locate, held, 10000), "unknown back: never on a screen held from a known one");
    // due only after the screen settled, not twice within a second, not in a hint run, not while a form search runs
    {
      var g = new ClickGate();
      Check(!g.UnknownBackDue(ClickOwner.Locate, plain, ClickGate.UnknownBackSettleMs - 1), "unknown back: not before the screen settled");
      Check(g.UnknownBackDue(ClickOwner.Locate, plain, ClickGate.UnknownBackSettleMs), "unknown back: due once it settled");
      Check(!g.UnknownBackDue(ClickOwner.Locate, plain, ClickGate.UnknownBackSettleMs + 500), "unknown back: not again within a second");
      var gh = new ClickGate { NoLocateClicks = true };
      Check(!gh.UnknownBackDue(ClickOwner.Locate, plain, 10000) && gh.Check(Rq(ClickOwner.Locate), plain, 10000).Reason == "off", "unknown back: a hint run never presses");
      var gs = new ClickGate { SearchWait = true };
      Check(!gs.UnknownBackDue(ClickOwner.Locate, plain, 10000), "unknown back: not while a form search runs");
      var go = new ClickGate();
      Check(!go.Check(new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.UnknownBack, AutoOn = true, Front = true, X = W / 2, Y = H / 2, U = U }, plain, 10000).Allowed && go.HaltReason(ClickOwner.Locate) == "target", "unknown back: a point off the arrow is a stop");
    }
    // the press changes the screen: to the city (done, no more presses), to another unknown form (the next press may follow, no own-click stop)
    {
      var g = new ClickGate(); var rq = Rq(ClickOwner.Locate);
      Check(g.Check(rq, plain, 10000).Allowed, "unknown back: allowed on the unknown screen");
      g.Arm(ClickOwner.Locate, ClickTarget.UnknownBack, ClickGate.Expect(rq, plain), 10000);
      var city = ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string>() }, 10500);
      g.Observe(city, 10500);
      Check(!g.Pending(ClickOwner.Locate) && !g.Halted(ClickOwner.Locate) && g.UnknownBacks(ClickOwner.Locate) == 1, "unknown back: back in the city is its effect");
      Check(!g.UnknownBackDue(ClickOwner.Locate, city, 20000) && g.Preflight(ClickOwner.Locate, city, 10600).Allowed, "unknown back: the city is known - no more presses, the pilot goes on");
      var g2 = new ClickGate();
      g2.Arm(ClickOwner.Sell, ClickTarget.UnknownBack, ClickGate.Expect(Rq(ClickOwner.Sell), plain), 10000);
      var other = Unk("Form_Other", ""); other.SinceMs = 10400;
      g2.Observe(other, 10400);
      Check(!g2.Pending(ClickOwner.Sell) && !g2.Halted(ClickOwner.Sell), "unknown back: another unknown form on top is a change");
      var pf = g2.Preflight(ClickOwner.Sell, other, 10500);
      Check(pf.Verdict == GateVerdict.Wait && !g2.Halted(ClickOwner.Sell), "unknown back: the next unknown screen right after it is waited for, no own-click stop (" + pf.Reason + ")");
      Check(!g2.UnknownBackDue(ClickOwner.Sell, other, 10500) && g2.UnknownBackDue(ClickOwner.Sell, other, 10400 + ClickGate.UnknownBackSettleMs), "unknown back: the next press once that screen settled");
    }
    // a press that changes nothing: "nochange"
    {
      var g = new ClickGate(); var rq = Rq(ClickOwner.Equip);
      g.Arm(ClickOwner.Equip, ClickTarget.UnknownBack, ClickGate.Expect(rq, plain), 10000);
      g.Observe(plain, 11000);
      Check(g.Pending(ClickOwner.Equip) && !g.Halted(ClickOwner.Equip), "unknown back: the effect is awaited for a moment");
      g.Observe(plain, 10000 + ClickGate.EffectMs + 1);
      Check(!g.Halted(ClickOwner.Equip) && g.EscapePending(ClickOwner.Equip), "unknown back: the same screen after the press: one Escape next, no stop yet");
      var gn = new ClickGate(); var nameless = Unk("", "");
      gn.Arm(ClickOwner.Equip, ClickTarget.UnknownBack, ClickGate.Expect(Rq(ClickOwner.Equip), nameless), 0);
      gn.Observe(nameless, ClickGate.EffectMs + 1);
      Check(!gn.Halted(ClickOwner.Equip) && gn.EscapePending(ClickOwner.Equip), "unknown back: an unknown screen without a name before and after is no change either (one Escape next)");
      // the Escape changes nothing as well: the stop
      var er = new ClickRequest { Owner = ClickOwner.Equip, Target = ClickTarget.EscapeBack, AutoOn = true, Front = true, U = U };
      Check(gn.EscapeDue(ClickOwner.Equip, nameless, 10000) && !gn.Check(er, Unk("", null), 10000).Allowed, "unknown back: no Escape while the shown forms are not read (the unknown-back rules)");
      Check(g.EscapeDue(ClickOwner.Equip, plain, 20000) && g.Check(er, plain, 20000).Allowed, "unknown back: the Escape on that full-screen unknown screen");
      g.Arm(ClickOwner.Equip, ClickTarget.EscapeBack, ClickGate.Expect(er, plain), 20000);
      g.Observe(plain, 20000 + ClickGate.EffectMs + 1);
      Eq("nochange", g.HaltReason(ClickOwner.Equip), "unknown back: an Escape that changes nothing is a stop");
    }
    // at most MaxUnknownBacks presses a run; a new run starts the count again
    {
      var g = new ClickGate(); int n = 0; long t = 10000;
      for (int i = 0; i < ClickGate.MaxUnknownBacks + 2; i++, t += 5000) {
        var s = Unk("Form_" + i, ""); s.SinceMs = t - ClickGate.UnknownBackSettleMs;
        if (!g.UnknownBackDue(ClickOwner.Locate, s, t)) break;
        var rq = Rq(ClickOwner.Locate);
        if (!g.Check(rq, s, t).Allowed) break;
        g.Arm(ClickOwner.Locate, ClickTarget.UnknownBack, ClickGate.Expect(rq, s), t); n++;
        g.Observe(Unk("Form_" + (i + 1), ""), t + 300);
      }
      Eq(ClickGate.MaxUnknownBacks, n, "unknown back: presses in one run");
      var last = Unk("Form_x", ""); last.SinceMs = 0;
      Check(!g.UnknownBackDue(ClickOwner.Locate, last, t + 10000), "unknown back: none due after the last one");
      var r = g.Check(Rq(ClickOwner.Locate), last, t + 10000);
      Check(!r.Allowed && g.HaltReason(ClickOwner.Locate) == "unknown", "unknown back: a fourth press is a stop (" + g.HaltDetail(ClickOwner.Locate) + ")");
      g.Resume(ClickOwner.Locate);
      Check(g.UnknownBacks(ClickOwner.Locate) == 0 && g.UnknownBackDue(ClickOwner.Locate, last, t + 20000), "unknown back: a new run may press again");
    }
  }

  // back to the city (ClickTarget.CityBack) from a known screen the pilot cannot start from, and the one Escape after a back press that changed nothing
  static void CityBackTests() {
    Console.WriteLine("Back to the city: the back arrow, then Escape");
    const double W = 1366, H = 705; double U = Ui.Unit(W, H);
    var hg = new HeroGeometry(); double bx = hg.BackX * U, by = hg.BackY * U;
    Func<string, ScreenInputs> In = known => new ScreenInputs { Offers = new List<string>(), ShownKnown = known, CityForm = false };
    Func<string, ScreenState> City = known => { var i = In(known); i.CityForm = true; return ScreenState.Classify(i, 0); };
    Func<string, ScreenState> Inv = known => { var i = In(known); i.Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }; return ScreenState.Classify(i, 0); };
    Func<string, ScreenState> Foundry = known => { var i = In(known); i.Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }; i.Rs = new RecastScreen { SublimingOpen = true, CurType = 1 }; return ScreenState.Classify(i, 0); };
    Func<string, ScreenState> Gem = known => { var i = In(known); i.Rs = new RecastScreen { GemOpen = true }; return ScreenState.Classify(i, 0); };
    Func<string, ScreenState> Hero = known => { var i = In(known); i.Hero = new HeroScreen { Hero = 5, FormOk = true }; return ScreenState.Classify(i, 0); };
    Func<ClickOwner, ClickRequest> Rq = o => new ClickRequest { Owner = o, Target = ClickTarget.CityBack, AutoOn = true, Front = true, X = bx, Y = by, U = U };
    Func<ClickOwner, ClickRequest> Esc = o => new ClickRequest { Owner = o, Target = ClickTarget.EscapeBack, AutoOn = true, Front = true, U = U };
    const string fdForms = "Form_BackpackIntegration,Form_EquipSubliming";
    Check(Foundry(null).Kind == ScreenKind.Foundry && Gem(null).Kind == ScreenKind.Gem && Inv(null).Kind == ScreenKind.Inventory && Hero(null).Kind == ScreenKind.HeroScreen && City(null).Kind == ScreenKind.City, "city back: the test screens are read as meant");

    // who needs it: the pilot's start screen never (the fast paths stay), every other known screen does
    Check(!ClickGate.AwayFromStart(ClickOwner.Equip, City(null)) && !ClickGate.AwayFromStart(ClickOwner.Equip, Hero(null)), "city back: equip starts in the city / on the hero screen as before");
    foreach (var s in new[] { Foundry(null), Inv(null), Gem(null) }) Check(ClickGate.AwayFromStart(ClickOwner.Equip, s), "city back: equip on " + s.Kind + " goes back first");
    Check(!ClickGate.AwayFromStart(ClickOwner.Sell, Inv(null)) && !ClickGate.AwayFromStart(ClickOwner.Sell, City(null)) && !ClickGate.AwayFromStart(ClickOwner.Sell, Hero(null)), "city back: sell starts in the inventory / the city / with its own hero back as before");
    Check(ClickGate.AwayFromStart(ClickOwner.Sell, Foundry(null)) && ClickGate.AwayFromStart(ClickOwner.Sell, Gem(null)), "city back: sell on the Foundry / the gem screen goes back first");
    Check(!ClickGate.AwayFromStart(ClickOwner.Equip, ScreenState.Classify(In(""), 0)), "city back: an unknown screen is the unknown back's");
    {   // Locate: the inventory's steps with the Foundry / the gem screen on top; never the recast's own gem steps, never the hero screen (its HeroBack)
      Func<LocateKind, bool, bool, bool> L = (k, form, hero) => LocateAuto.NeedsCityBack(new LocateView { Step = new LocateStep { Kind = k }, OtherFormOpen = form, HeroScreenOpen = hero, W = W, H = H, U = U });
      foreach (var k in new[] { LocateKind.OpenInventory, LocateKind.OpenEquipTab, LocateKind.InList }) Check(L(k, true, false), "city back: locate step " + k + " with the Foundry on top goes back");
      Check(!L(LocateKind.OpenInventory, false, false) && !L(LocateKind.OpenInventory, false, true) && !L(LocateKind.InList, false, false), "city back: locate on its own screens / the hero screen: as before");
      Check(!L(LocateKind.GemFunction, true, false) && !L(LocateKind.Selected, true, false) && !L(LocateKind.AscendPick, true, false), "city back: never in the recast's / ascension's own steps");
    }

    // from the Foundry: back (-> the inventory), back (-> the city), then the normal first click
    {
      var g = new ClickGate(); long t = 10000;
      var fd = Foundry(null); fd.SinceMs = t - ClickGate.CityBackSettleMs;
      var fresh0 = Foundry(null); fresh0.SinceMs = t - 10;
      Check(!g.CityBackDue(ClickOwner.Equip, fresh0, t), "city back: not before the screen settled");
      Check(g.CityBackDue(ClickOwner.Equip, fd, t), "city back: due on the Foundry");
      Check(!g.CityBackDue(ClickOwner.Equip, fd, t + 200), "city back: not again within a second");
      var r = g.Check(Rq(ClickOwner.Equip), Foundry(fdForms), t);
      Check(r.Allowed, "city back: the back arrow on the Foundry (" + r.Detail + ")");
      Check(!new ClickGate().Check(new ClickRequest { Owner = ClickOwner.Equip, Target = ClickTarget.CityBack, AutoOn = true, Front = true, X = W / 2, Y = H / 2, U = U }, Foundry(fdForms), t).Allowed, "city back: never a point off the arrow");
      g.Arm(ClickOwner.Equip, ClickTarget.CityBack, ClickGate.Expect(Rq(ClickOwner.Equip), fd), t);
      Eq("pending", g.Preflight(ClickOwner.Equip, fd, t + 300).Reason, "city back: its effect is awaited");
      var iv = Inv(null); iv.SinceMs = t + 500;
      g.Observe(iv, t + 500);
      Check(!g.Pending(ClickOwner.Equip) && !g.Halted(ClickOwner.Equip), "city back: the inventory is its effect");
      Check(!g.CityBackDue(ClickOwner.Equip, iv, t + 600), "city back: the inventory settles first");
      t = t + 500 + ClickGate.CityBackSettleMs;
      Check(g.CityBackDue(ClickOwner.Equip, iv, t) && g.Check(Rq(ClickOwner.Equip), Inv("Form_BackpackIntegration"), t).Allowed, "city back: the second press, on the inventory");
      g.Arm(ClickOwner.Equip, ClickTarget.CityBack, ClickGate.Expect(Rq(ClickOwner.Equip), iv), t);
      var city = City(null); city.SinceMs = t + 400;
      g.Observe(city, t + 400);
      Check(!g.Pending(ClickOwner.Equip) && g.CityBacks(ClickOwner.Equip) == 2, "city back: the city came after 2 presses");
      Check(!g.CityBackDue(ClickOwner.Equip, city, t + 5000) && !g.Check(Rq(ClickOwner.Equip), City(""), t + 5000).Allowed, "city back: never a press on the city");
      Check(!g.Halted(ClickOwner.Equip), "city back: not stopped by its own press on the city (" + g.HaltDetail(ClickOwner.Equip) + ")");
      var nav = new ClickRequest { Owner = ClickOwner.Equip, Target = ClickTarget.EquipNav, AutoOn = true, Front = true, X = W - hg.HeroesBtnDX * U, Y = H - hg.HeroesBtnDY * U };
      var nr = g.Check(nav, City(""), t + 5000);
      Check(nr.Allowed, "city back: then the normal first click («Герои» of the city): " + nr.Reason + " " + nr.Detail);
      Check(!g.EscapePending(ClickOwner.Equip) && g.Escapes(ClickOwner.Equip) == 0, "city back: no Escape on the way");
    }
    // from the gem screen (sell) and the hero screen of another flow (its gear list closing counts)
    {
      var g = new ClickGate(); var gm = Gem(null);
      Check(g.CityBackDue(ClickOwner.Sell, gm, 10000) && g.Check(Rq(ClickOwner.Sell), Gem("Form_EquipGemMain"), 10000).Allowed, "city back: sell on the gem screen");
      g.Arm(ClickOwner.Sell, ClickTarget.CityBack, ClickGate.Expect(Rq(ClickOwner.Sell), gm), 10000);
      g.Observe(Inv(null), 10400);
      Check(!g.Pending(ClickOwner.Sell) && !ClickGate.AwayFromStart(ClickOwner.Sell, Inv(null)), "city back: sell is in the inventory after one press, its own steps go on");
      var gh = new ClickGate(); var hs = Hero(null); hs.HeroSlot = 2;
      Check(gh.CityBackDue(ClickOwner.Locate, hs, 10000) && gh.Check(Rq(ClickOwner.Locate), hs, 10000).Allowed, "city back: allowed on a hero screen");
      gh.Arm(ClickOwner.Locate, ClickTarget.CityBack, ClickGate.Expect(Rq(ClickOwner.Locate), hs), 10000);
      var hs2 = Hero(null); hs2.HeroSlot = -1;
      gh.Observe(hs2, 10300);
      Check(!gh.Pending(ClickOwner.Locate) && !gh.Halted(ClickOwner.Locate), "city back: a gear list closing on the hero screen is a change");
    }
    // never: a pop-up, an offer, a held screen, a fight's form, a hint run
    {
      var md = Foundry(null); md.ModalOpen = true; md.ModalWhy = "form:Form_X";
      var hd = Foundry(null); hd.Held = true;
      var bt = Foundry("Form_Battle");
      foreach (var s in new[] { md, hd, bt }) {
        Check(ClickGate.CityBackBlock(s) != "" && !new ClickGate().CityBackDue(ClickOwner.Equip, s, 10000), "city back: never on " + s.Brief + " (" + ClickGate.CityBackBlock(s) + ")");
        Check(!new ClickGate().Check(Rq(ClickOwner.Equip), s, 10000).Allowed, "city back: the gate waits on " + s.Brief);
      }
      var gl = new ClickGate { NoLocateClicks = true };
      Check(!gl.CityBackDue(ClickOwner.Locate, Foundry(null), 10000) && !gl.Check(Rq(ClickOwner.Locate), Foundry(fdForms), 10000).Allowed, "city back: a hint run never presses");
    }
    // a back press that changes nothing: ONE Escape (never before such a press, never on the city or in a fight), then the stop
    {
      var g = new ClickGate(); long t = 10000; var fd = Foundry(null);
      Check(!g.EscapeDue(ClickOwner.Equip, fd, t), "escape: never without a back press that changed nothing");
      Eq("target", new ClickGate().Check(Esc(ClickOwner.Equip), Foundry(fdForms), t).Reason, "escape: the gate refuses one that was not due");
      Check(g.CityBackDue(ClickOwner.Equip, fd, t) && g.Check(Rq(ClickOwner.Equip), Foundry(fdForms), t).Allowed, "escape: first the back arrow");
      g.Arm(ClickOwner.Equip, ClickTarget.CityBack, ClickGate.Expect(Rq(ClickOwner.Equip), fd), t);
      g.Observe(fd, t + ClickGate.EffectMs + 1);
      Check(!g.Halted(ClickOwner.Equip) && g.EscapePending(ClickOwner.Equip), "escape: the back press changed nothing - an Escape is next, no stop");
      t += ClickGate.EffectMs + 2000;
      Check(!g.CityBackDue(ClickOwner.Equip, fd, t), "escape: no other back press while the Escape is due");
      Check(g.EscapeDue(ClickOwner.Equip, fd, t), "escape: due on the same Foundry");
      Check(!g.Check(Esc(ClickOwner.Equip), Foundry(null), t).Allowed && !g.Halted(ClickOwner.Equip), "escape: not without the shown forms read (a fight could be on)");
      Check(!g.Check(Esc(ClickOwner.Equip), Foundry(fdForms + ",Form_Battle"), t).Allowed, "escape: never with the fight's form shown");
      var r = g.Check(Esc(ClickOwner.Equip), Foundry(fdForms), t);
      Check(r.Allowed, "escape: one Escape on the Foundry (" + r.Detail + ")");
      g.Arm(ClickOwner.Equip, ClickTarget.EscapeBack, ClickGate.Expect(Esc(ClickOwner.Equip), fd), t);
      Check(!g.EscapePending(ClickOwner.Equip) && g.Escapes(ClickOwner.Equip) == 1, "escape: one sent");
      g.Observe(Inv(null), t + 300);
      Check(!g.Pending(ClickOwner.Equip) && !g.Halted(ClickOwner.Equip), "escape: the inventory is its effect");
      Check(!g.EscapeDue(ClickOwner.Equip, Inv(null), t + 5000), "escape: only one per back press that changed nothing");
    }
    {   // the city on top: never an Escape (it asks to quit the game), the due one is dropped
      var g = new ClickGate(); var fd = Foundry(null);
      g.CityBackDue(ClickOwner.Locate, fd, 10000);
      g.Arm(ClickOwner.Locate, ClickTarget.CityBack, ClickGate.Expect(Rq(ClickOwner.Locate), fd), 10000);
      g.Observe(fd, 10000 + ClickGate.EffectMs + 1);
      Check(g.EscapePending(ClickOwner.Locate), "escape: due after the back press that changed nothing");
      Check(!g.Check(Esc(ClickOwner.Locate), City("Form_MainCity"), 20000).Allowed, "escape: never on the city (fresh read)");
      Check(!g.EscapeDue(ClickOwner.Locate, City(null), 20000) && !g.EscapePending(ClickOwner.Locate), "escape: the city came - nothing due any more");
      Check(ClickGate.EscapeBlock(City("")) != "" && ClickGate.EscapeBlock(City(null)) != "", "escape: blocked on the city whatever the forms say");
      var ub = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = "Form_BattleEnd", ShownKnown = "" }, 0);
      Check(ClickGate.EscapeBlock(ub) != "", "escape: never on a fight's result");
    }
    {   // the game's exit question after an Escape: the stop, nothing of it pressed
      var g = new ClickGate(); var un = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = "Form_Odd", ShownKnown = "" }, 0);
      g.Arm(ClickOwner.Sell, ClickTarget.UnknownBack, ClickGate.Expect(new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.UnknownBack, U = U }, un), 0);
      g.Observe(un, ClickGate.EffectMs + 1);
      Check(g.EscapeDue(ClickOwner.Sell, un, 10000) && g.Check(Esc(ClickOwner.Sell), un, 10000).Allowed, "exit dialog: the Escape on an unknown full-screen form");
      g.Arm(ClickOwner.Sell, ClickTarget.EscapeBack, ClickGate.Expect(Esc(ClickOwner.Sell), un), 10000);
      var ex = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = "Form_SystemTips", ShownKnown = "" }, 0);
      Check(ClickGate.ExitDialogShown(ex) && ClickGate.EscapeBlock(ex) != "", "exit dialog: seen, no Escape on it");
      g.Observe(ex, 10300);
      Eq("exitdialog", g.HaltReason(ClickOwner.Sell), "exit dialog: after the Escape it stops the pilot");
    }
    // caps: MaxCityBacks presses a run (each changing the screen without reaching the city), MaxEscapes Escapes a run
    {
      var g = new ClickGate(); long t = 10000; int n = 0;
      Func<int, ScreenState> Alt = i => i % 2 == 0 ? Foundry(null) : Gem(null);
      for (int i = 0; i < ClickGate.MaxCityBacks + 2; i++, t += 5000) {
        var s = Alt(i); s.SinceMs = t - ClickGate.CityBackSettleMs;
        if (!g.CityBackDue(ClickOwner.Locate, s, t)) break;
        if (!g.Check(Rq(ClickOwner.Locate), s, t).Allowed) break;
        g.Arm(ClickOwner.Locate, ClickTarget.CityBack, ClickGate.Expect(Rq(ClickOwner.Locate), s), t); n++;
        g.Observe(Alt(i + 1), t + 300);
      }
      Eq(ClickGate.MaxCityBacks, n, "city back: presses in one run");
      Eq("nochange", g.HaltReason(ClickOwner.Locate), "city back: the next one due is a stop (" + g.HaltDetail(ClickOwner.Locate) + ")");
      g.Resume(ClickOwner.Locate);
      Check(g.CityBacks(ClickOwner.Locate) == 0 && g.CityBackDue(ClickOwner.Locate, Foundry(null), t + 20000), "city back: a new run may press again");
    }
    {
      var g = new ClickGate(); long t = 10000; int esc = 0;
      for (int i = 0; i < 4 && !g.Halted(ClickOwner.Equip); i++, t += 20000) {
        var s = i % 2 == 0 ? Foundry(null) : Gem(null); s.SinceMs = 0;
        if (!g.CityBackDue(ClickOwner.Equip, s, t)) break;
        g.Check(Rq(ClickOwner.Equip), s, t);
        g.Arm(ClickOwner.Equip, ClickTarget.CityBack, ClickGate.Expect(Rq(ClickOwner.Equip), s), t);
        g.Observe(s, t + ClickGate.EffectMs + 1);   // the back changes nothing
        if (g.Halted(ClickOwner.Equip)) break;
        long te = t + ClickGate.EffectMs + 2000;
        if (!g.EscapeDue(ClickOwner.Equip, s, te)) break;
        if (!g.Check(Esc(ClickOwner.Equip), i % 2 == 0 ? Foundry(fdForms) : Gem("Form_EquipGemMain"), te).Allowed) break;
        g.Arm(ClickOwner.Equip, ClickTarget.EscapeBack, ClickGate.Expect(Esc(ClickOwner.Equip), s), te); esc++;
        g.Observe(i % 2 == 0 ? Gem(null) : Foundry(null), te + 300);   // the Escape changed the screen
      }
      Eq(ClickGate.MaxEscapes, esc, "escape: at most this many a run");
      Eq("nochange", g.HaltReason(ClickOwner.Equip), "escape: a back press that changes nothing with no Escape left is a stop");
    }
  }

  // Storage cleanup of ARTIFACTS (the artifact tab's «Массовая продажа»): the panel reader on fake tables, the screen state, the steps and frames, no clicks
  // the build plan screen a wrong click opened (laptop, 1366x705), the unknown-screen stop, the form search wait, a pop-up over a known screen
  static void PlanScreenTests() {
    const double W = 1366, H = 705; double U = Ui.Unit(W, H);
    // «План сборки»: left with its back arrow (top left, where the photo of the laptop shows it), never its plan buttons
    var bk = OfferForms.ClosePoint(null, "Form_EquipmentPlan", W, H, U);
    Check(bk != null && bk.X > 20 && bk.X < 75 && bk.Y > 10 && bk.Y < 45, "plan screen: the back arrow at the top left (" + (bk == null ? "-" : (int)bk.X + "," + (int)bk.Y) + ")");
    var plan = ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string> { "Form_EquipmentPlan" } }, 0);
    Check(plan.Kind == ScreenKind.Unknown && plan.ModalWhy == "offer:Form_EquipmentPlan", "plan screen: read as the pop-up on top (" + plan.Brief + ")");
    Func<double, double, ClickRequest> Back = (x, y) => new ClickRequest { Owner = ClickOwner.Equip, Target = ClickTarget.OfferClose, AutoOn = true, Front = true, Form = "Form_EquipmentPlan", X = x, Y = y, W = W, H = H, U = U };
    var g0 = new ClickGate();
    Eq("offer", g0.Preflight(ClickOwner.Equip, plan, 0).Reason, "plan screen: the pilot's preflight leaves it first");
    Check(g0.Check(Back(bk.X, bk.Y), plan, 0).Allowed, "plan screen: its back arrow may be pressed");
    foreach (var p in new[] { new[] { 0.3 * U, 0.04 * U }, new[] { 0.13 * U, 0.18 * U }, new[] { W / 2, H - 0.05 * U }, new[] { W - 0.3 * U, H - 0.05 * U } })
      Check(!new ClickGate().Check(Back(p[0], p[1]), plan, 0).Allowed, "plan screen: never a point off the arrow (" + (int)p[0] + "," + (int)p[1] + ")");
    var gb = new ClickGate(); gb.Arm(ClickOwner.Equip, ClickTarget.OfferClose, ClickGate.Expect(Back(bk.X, bk.Y), plan), 0, "Form_EquipmentPlan");
    gb.Observe(ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string>() }, 0), 800);
    Check(!gb.Pending(ClickOwner.Equip) && !gb.Halted(ClickOwner.Equip), "plan screen: back in the city = the effect, the pilot goes on");

    // the unknown-screen stop: 20 s on the same unrecognised screen, the pilot running
    Func<string, ScreenState> Unk = top => ScreenState.Classify(new ScreenInputs { CityForm = false, Offers = new List<string>(), TopUnknown = top }, 0);
    {
      var g = new ClickGate(); var log = new List<string>(); g.Sink = x => log.Add(x);
      bool hit = false; long t;
      for (t = 0; t <= ClickGate.UnknownStallMs + 500 && !hit; t += 200) hit = g.UnknownStall(ClickOwner.Equip, Unk(""), t);
      Check(hit && t > ClickGate.UnknownStallMs && g.HaltReason(ClickOwner.Equip) == "unknown" && g.HaltDetail(ClickOwner.Equip).StartsWith("unknown screen ?"), "unknown stop: 20 s on an unknown screen stops the pilot (" + g.HaltDetail(ClickOwner.Equip) + ")");
      Check(log.Exists(l => l.Contains("click gate Equip: STOP (unknown) unknown screen")), "unknown stop: the lab's stop pattern is in the log");
      var g2 = new ClickGate(); bool h2 = false;
      for (t = 0; t <= ClickGate.UnknownStallMs + 2000 && !h2; t += 200) h2 = g2.UnknownStall(ClickOwner.Locate, Unk("Form_Odd"), t);
      Check(h2 && g2.HaltDetail(ClickOwner.Locate).StartsWith("unknown screen Form_Odd"), "unknown stop: the form's name is in the reason");
      // Preflight (Locate / Sell / the artifact steps) stops the same way
      var g3 = new ClickGate(); GateResult r3 = null;
      for (t = 0; t <= ClickGate.UnknownStallMs + 500; t += 200) { r3 = g3.Preflight(ClickOwner.Sell, Unk("Form_Odd"), t); if (r3.Verdict == GateVerdict.Deny) break; }
      Check(r3.Verdict == GateVerdict.Deny && g3.HaltReason(ClickOwner.Sell) == "unknown" && t >= ClickGate.UnknownStallMs, "unknown stop: Preflight waits 20 s, then stops");
      // a gap (background / the player busy) or another unknown screen starts the count again
      var g4 = new ClickGate();
      for (t = 0; t < 15000; t += 200) g4.UnknownStall(ClickOwner.Equip, Unk(""), t);
      for (t = 18000; t < 30000; t += 200) g4.UnknownStall(ClickOwner.Equip, Unk(""), t);
      Check(!g4.Halted(ClickOwner.Equip), "unknown stop: a gap of the calls starts the count again");
      var g5 = new ClickGate();
      for (t = 0; t < 15000; t += 200) g5.UnknownStall(ClickOwner.Equip, Unk("Form_A"), t);
      for (t = 15000; t < 30000; t += 200) g5.UnknownStall(ClickOwner.Equip, Unk("Form_B"), t);
      Check(!g5.Halted(ClickOwner.Equip), "unknown stop: a change of the unknown screen is progress");
      g5.Resume(ClickOwner.Equip);
    }
    {   // screens the pilots handle never count, however long
      var hero = new HeroScreen { FormOk = true, Hero = 204200000, Tab = 1, Part = -1 };
      var known = new Dictionary<string, ScreenState> {
        { "city", ScreenState.Classify(new ScreenInputs { CityForm = true, Offers = new List<string>() }, 0) },
        { "city by picture", ScreenState.Classify(new ScreenInputs { CitySeen = true }, 0) },
        { "hero screen", ScreenState.Classify(new ScreenInputs { Hero = hero }, 0) },
        { "inventory", ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true } }, 0) },
        { "sell list", ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true, Bulk = true } }, 0) },
        { "foundry", ScreenState.Classify(new ScreenInputs { Rs = new RecastScreen { SublimingOpen = true, CurType = 2 } }, 0) },
        { "gem", ScreenState.Classify(new ScreenInputs { Rs = new RecastScreen { GemOpen = true, GemFunc = 7 } }, 0) },
        { "offer", ScreenState.Classify(new ScreenInputs { Offers = new List<string> { "Form_PopUpShop" } }, 0) },
        { "plan screen", plan },
        { "fight result", Unk("Form_BattleEnd") },
        { "arena result", Unk("Form_PvpBattleEnd") },
        { "hero filter pop-up", ScreenState.Classify(new ScreenInputs { Hero = new HeroScreen { FormOk = true, Hero = 204200000, Tab = 1, Part = -1, FilterOpen = true } }, 0) },
      };
      foreach (var kv in known) {
        var g = new ClickGate(); bool any = false;
        for (long t = 0; t <= 3 * ClickGate.UnknownStallMs; t += 200) any |= g.UnknownStall(ClickOwner.Equip, kv.Value, t);
        Check(!any && !g.Halted(ClickOwner.Equip), "unknown stop: never on " + kv.Key + " (" + kv.Value.Brief + ")");
        var gp = new ClickGate(); GateResult last = null;
        for (long t = 0; t <= 3 * ClickGate.UnknownStallMs; t += 200) last = gp.Preflight(ClickOwner.Locate, kv.Value, t);
        Check(!gp.Halted(ClickOwner.Locate), "unknown stop: Preflight never stops on " + kv.Key + " (" + last.Reason + ")");
      }
      // a held screen (the reads fail for a moment) is the known one
      var tr = new ScreenTracker(); tr.Update(new ScreenInputs { Hero = hero }, 0);
      var held = tr.Update(new ScreenInputs(), 1000);
      Check(held.Held && held.Kind == ScreenKind.HeroScreen && !new ClickGate().UnknownStall(ClickOwner.Equip, held, 1000), "unknown stop: never on a held screen");
    }
    {   // a known form moved: while the host's search for it runs, an unknown screen is waited for (no own-click stop, no effect timeout, no 20 s count)
      var unk = Unk("");
      var g = new ClickGate { SearchWait = true };
      g.Arm(ClickOwner.Locate, ClickTarget.AscendTab, null, 0);
      Eq("search", g.Preflight(ClickOwner.Locate, unk, 1700).Reason, "form search: an unknown screen right after the pilot's click waits for the search");
      for (long t = 1700; t < 40000; t += 200) g.UnknownStall(ClickOwner.Locate, unk, t);
      Check(!g.Halted(ClickOwner.Locate), "form search: no unknown-screen stop while it runs");
      var rq = new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.CityInventory, AutoOn = true, Front = true };
      Eq("search", g.Check(rq, unk, 40000).Reason, "form search: a click on an unknown screen waits for it");
      g.SearchWait = false;
      var rf = g.Preflight(ClickOwner.Locate, unk, 40200);
      Check(rf.Verdict == GateVerdict.Deny && g.HaltDetail(ClickOwner.Locate).Contains("form search ended without finding"), "form search: ended, the screen still unknown -> stop (" + g.HaltDetail(ClickOwner.Locate) + ")");
      // found: the screen is known again, nothing stops
      var gf = new ClickGate { SearchWait = true }; gf.Arm(ClickOwner.Locate, ClickTarget.AscendTab, null, 0);
      gf.Preflight(ClickOwner.Locate, unk, 1700);
      gf.SearchWait = false;
      var foundry = ScreenState.Classify(new ScreenInputs { Rs = new RecastScreen { SublimingOpen = true, CurType = 2 } }, 0);
      Check(gf.Preflight(ClickOwner.Locate, foundry, 18000).Allowed && !gf.Halted(ClickOwner.Locate), "form search: found = the pilot goes on");
      // a click's effect is not timed out while the search runs, and is after it
      var ge = new ClickGate { SearchWait = true };
      ge.Arm(ClickOwner.Locate, ClickTarget.AscendTab, s => s.Kind == ScreenKind.Foundry && s.FoundryTab == 2, 0);
      ge.Observe(unk, ClickGate.EffectMs + 10000);
      Check(ge.Pending(ClickOwner.Locate) && !ge.Halted(ClickOwner.Locate), "form search: the click's effect is still awaited while it runs");
      ge.Observe(foundry, ClickGate.EffectMs + 17000);
      Check(!ge.Pending(ClickOwner.Locate) && !ge.Halted(ClickOwner.Locate), "form search: the screen found = the click's effect");
      var gn = new ClickGate { SearchWait = true };
      gn.Arm(ClickOwner.Locate, ClickTarget.AscendTab, s => s.Kind == ScreenKind.Foundry, 0);
      gn.Observe(unk, ClickGate.EffectMs + 10000); gn.SearchWait = false;
      gn.Observe(unk, ClickGate.EffectMs + 12000);
      Eq("nochange", gn.HaltReason(ClickOwner.Locate), "form search: ended without the screen -> the click changed nothing");
      // without a search the old rules stand: unknown right after the own click stops
      var go = new ClickGate(); go.Arm(ClickOwner.Locate, ClickTarget.AscendTab, null, 0);
      Check(go.Preflight(ClickOwner.Locate, unk, 1700).Verdict == GateVerdict.Deny && go.HaltReason(ClickOwner.Locate) == "unknown", "form search: none running -> the own-click rule as before");
    }
    {   // a form nobody knows that came over a known screen is a pop-up on top: the gate stops instead of clicking under it
      var inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true };
      var top = ScreenState.Classify(new ScreenInputs { Inv = inv, TopNew = "Form_ItemReward" }, 0);
      Check(top.Kind == ScreenKind.Inventory && top.ModalOpen && top.ModalWhy == "form:Form_ItemReward", "pop-up on top: modal over the inventory (" + top.Brief + ")");
      var rq = new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.InvEquipTab, AutoOn = true, Front = true };
      var r = new ClickGate().Check(rq, top, 0);
      Check(r.Verdict == GateVerdict.Deny && r.Reason == "modal", "pop-up on top: a click under it stops the pilot (" + r.Reason + ")");
      Check(!ScreenState.Classify(new ScreenInputs { Inv = inv }, 0).ModalOpen, "pop-up on top: none = no modal");
      Check(!ScreenState.Classify(new ScreenInputs { CityForm = true, TopNew = "Form_X" }, 0).ModalOpen, "pop-up on top: the city keeps its old rules");
      var f = ScreenState.Classify(new ScreenInputs { Rs = new RecastScreen { SublimingOpen = true, CurType = 2, AscendFilterOpen = true }, TopNew = "Form_X" }, 0);
      Eq("filter", f.ModalWhy, "pop-up on top: a modal the readers know keeps its name");
      var tr = new ScreenTracker(); var st = tr.Update(new ScreenInputs { Inv = inv, TopNew = "Form_X" }, 0);
      Check(st.ModalOpen && st.ModalWhy == "form:Form_X", "pop-up on top: the tracker passes it on");
    }
  }

  static void OfferTests() {
    const double W = 1920, H = 1009; double U = Ui.Unit(W, H);
    // the list: the game's pop-up queue, 34 forms, and the build plan screen; the × known for 32 of them, and never on another button of the form
    foreach (var n in new[] { "Form_LeftTip", "Form_Marquee", "Form_MiniSystemTip", "Form_PowerChangeTips", "Form_IntegralRewardTip" })
      Check(OfferForms.IsIgnoredOverlay(n) && !OfferForms.IsOffer(n) && Array.IndexOf(OfferForms.KnownScreens, n) < 0, "overlay forms: " + n + " is ignored (not an offer, not a known screen)");
    Check(!OfferForms.IsIgnoredOverlay("Form_ItemReward") && !OfferForms.IsIgnoredOverlay("Form_Dialog") && !OfferForms.IsIgnoredOverlay(null), "overlay forms: other forms still count");
    Eq(35, OfferForms.All.Length, "offer list: the forms of the game's pop-up queue and the build plan");
    int closable = 0;
    foreach (var e in OfferForms.All) {
      var r = OfferForms.CloseRect(null, e.Name, W, H, U);
      if (r == null) continue;
      closable++;
      Check(r.X >= 0 && r.Right <= W && r.Y >= 0 && r.Bottom <= H && r.W > 20 && r.H > 20, "offer list: the × of " + e.Name + " lies in the window");
      Check(!OfferForms.OnNeverButton(e.Name, r.CX, r.CY, W, H, U), "offer list: the × of " + e.Name + " is on none of its other buttons");
    }
    Eq(32, closable, "offer list: forms with a known ×");
    foreach (var nm in new[] { "Form_ChampionshipOver", "Form_MonthyCardGetReward", "Form_Feedback" }) Check(OfferForms.IsOffer(nm) && OfferForms.CloseRect(null, nm, W, H, U) == null, "offer list: " + nm + " has no × known: never clicked");
    Check(!OfferForms.IsOffer("Form_MainCity") && !OfferForms.IsOffer("Form_BackpackIntegration"), "offer list: the game's screens are not offers");

    Func<string[], bool, ScreenState> St = (offers, city) => ScreenState.Classify(new ScreenInputs { CityForm = city, Offers = offers == null ? null : new List<string>(offers) }, 0);
    var shop = St(new[] { "Form_PopUpShop" }, true);
    Check(shop.Kind == ScreenKind.City && shop.ModalOpen && shop.ModalWhy == "offer:Form_PopUpShop" && shop.Brief.Contains("modal(offer:Form_PopUpShop)"), "screen: an offer over the city is the modal offer:<Form> (" + shop.Brief + ")");
    Check(!St(new string[0], true).ModalOpen && St(null, true).Kind == ScreenKind.City && !St(null, true).OfferRead, "screen: no offer / the table not read = no modal");
    var two = St(new[] { "Form_GiftPush", "Form_DailyCheckIn" }, true);
    Check(two.OfferCount == 2 && two.ModalWhy == "offer:Form_GiftPush+1", "screen: two offers at once are counted");
    var unkTop = ScreenState.Classify(new ScreenInputs { TopUnknown = "Form_ItemReward" }, 0);
    Check(unkTop.Kind == ScreenKind.Unknown && unkTop.TopForm == "Form_ItemReward" && unkTop.Brief.Contains("top(Form_ItemReward)"), "screen: an unknown form on an unrecognised screen is named");

    Func<ClickOwner, string, double, double, ClickRequest> Rq = (ow, form, x, y) => new ClickRequest { Owner = ow, Target = ClickTarget.OfferClose, AutoOn = true, Front = true, Form = form, X = x, Y = y, W = W, H = H, U = U };
    var c = OfferForms.ClosePoint(null, "Form_PopUpShop", W, H, U);
    {   // the × is allowed for every owner; the effect is the pop-up gone, logged "offer: closed <Form>"; then the pilot's own click
      foreach (ClickOwner ow in Enum.GetValues(typeof(ClickOwner))) Check(new ClickGate().Check(Rq(ow, "Form_PopUpShop", c.X, c.Y), shop, 0).Allowed, "offer close: " + ow + " may close the pop-up by its ×");
      var g = new ClickGate(); var log = new List<string>(); g.Sink = x => log.Add(x);
      var rq = Rq(ClickOwner.Locate, "Form_PopUpShop", c.X, c.Y);
      Eq("offer", g.Preflight(ClickOwner.Locate, shop, 0).Reason, "offer close: the pilot's preflight waits for the offer to be closed");
      var inv = new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.CityInventory, AutoOn = true, Front = true };
      var r0 = g.Check(inv, shop, 0);
      Check(r0.Verdict == GateVerdict.Wait && r0.Reason == "offer" && !g.Halted(ClickOwner.Locate), "offer close: the city button waits under an offer (no click, no stop)");
      Check(g.Check(rq, shop, 0).Allowed, "offer close: allowed");
      g.Arm(ClickOwner.Locate, ClickTarget.OfferClose, ClickGate.Expect(rq, shop), 0, "Form_PopUpShop");
      g.Observe(St(new string[0], true), 500);
      Check(!g.Pending(ClickOwner.Locate) && log.Exists(l => l == "offer: closed Form_PopUpShop"), "offer close: the pop-up gone = its effect, logged");
      Check(g.Check(inv, St(new string[0], true), 600).Allowed, "offer close: then the pilot's own first click");
      // a failed read of the table is not the effect
      var g2 = new ClickGate(); g2.Arm(ClickOwner.Sell, ClickTarget.OfferClose, ClickGate.Expect(Rq(ClickOwner.Sell, "Form_PopUpShop", c.X, c.Y), shop), 0, "Form_PopUpShop");
      g2.Observe(St(null, true), 500); Check(g2.Pending(ClickOwner.Sell), "offer close: an unread table is no effect");
      g2.Observe(St(new[] { "Form_PopUpShop" }, true), ClickGate.EffectMs + 10);
      Check(g2.HaltReason(ClickOwner.Sell) == "nochange", "offer close: the pop-up still there after the time -> nochange stop");
    }
    {   // never the buy / go / claim buttons; never off the ×; not on another screen; not an unknown form; one at a time
      var lot = St(new[] { "Form_ActPopupLottery" }, true);
      var go = OfferForms.All[Array.FindIndex(OfferForms.All, e => e.Name == "Form_ActPopupLottery")].Never[0];
      double gx = W / 2 + ((double)go[1] + (double)go[3]) / 2 * U, gy = H / 2 + ((double)go[5] + (double)go[7]) / 2 * U;
      Check(OfferForms.OnNeverButton("Form_ActPopupLottery", gx, gy, W, H, U), "offer close: the lottery's «GO» is a never-click button");
      var g = new ClickGate(); var r = g.Check(Rq(ClickOwner.Equip, "Form_ActPopupLottery", gx, gy), lot, 0);
      Check(r.Verdict == GateVerdict.Deny && r.Reason == "target", "offer close: a click on the «GO» button is never allowed");
      foreach (var e in OfferForms.All) foreach (var f in e.Never) {
        double fx = (f[0].Equals('L') ? 0 : f[0].Equals('C') ? W / 2 : W) + (double)f[1] * U, fy = (f[4].Equals('T') ? 0 : f[4].Equals('M') ? H / 2 : H) + (double)f[5] * U;
        double fx1 = (f[2].Equals('L') ? 0 : f[2].Equals('C') ? W / 2 : W) + (double)f[3] * U, fy1 = (f[6].Equals('T') ? 0 : f[6].Equals('M') ? H / 2 : H) + (double)f[7] * U;
        var st = St(new[] { e.Name }, true);
        if (!new ClickGate().Check(Rq(ClickOwner.Sell, e.Name, (fx + fx1) / 2, (fy + fy1) / 2), st, 0).Allowed) continue;
        Check(false, "offer close: " + e.Name + " has a button that could be clicked as its ×");
      }
      r = new ClickGate().Check(Rq(ClickOwner.Sell, "Form_PopUpShop", c.X - 60, c.Y + 40), shop, 0);
      Check(r.Verdict == GateVerdict.Deny && r.Reason == "target", "offer close: a point off the × is refused");
      var overInv = ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, Offers = new List<string> { "Form_PopUpShop" } }, 0);
      Eq("screen", new ClickGate().Check(Rq(ClickOwner.Sell, "Form_PopUpShop", c.X, c.Y), overInv, 0).Reason, "offer close: not over another screen than the city");
      Eq("modal", new ClickGate().Check(Rq(ClickOwner.Sell, "Form_GiftPush", c.X, c.Y), two, 0).Reason, "offer close: two offers at once -> stop (which is on top is not known)");
      Eq("offer", new ClickGate().Check(Rq(ClickOwner.Sell, "Form_Feedback", c.X, c.Y), St(new[] { "Form_Feedback" }, true), 0).Reason, "offer close: a form with no known × -> stop with its name");
      Eq("cell", new ClickGate().Check(Rq(ClickOwner.Sell, "Form_GiftPush", c.X, c.Y), shop, 0).Reason, "offer close: the form shown is another one -> stop");
      Eq("off", new ClickGate { NoSellClicks = true }.Check(Rq(ClickOwner.Sell, "Form_PopUpShop", c.X, c.Y), shop, 0).Reason, "offer close: a hint run never clicks");
      var unk = ScreenState.Classify(new ScreenInputs { Offers = new List<string> { "Form_PopUpShop" } }, 0);
      Check(new ClickGate().Check(Rq(ClickOwner.Locate, "Form_PopUpShop", c.X, c.Y), unk, 0).Allowed, "offer close: over a hidden city (screen unknown) it is allowed");
      var gu = new ClickGate(); gu.Arm(ClickOwner.Locate, ClickTarget.OfferClose, null, 0);
      Eq("offer", gu.Preflight(ClickOwner.Locate, unk, 500).Reason, "offer close: an unknown screen with an offer right after the pilot's click is no 'unknown' stop");
      var gt = new ClickGate(); gt.Arm(ClickOwner.Locate, ClickTarget.OfferClose, null, 0);
      Check(gt.Preflight(ClickOwner.Locate, unkTop, 500).Detail.Contains("Form_ItemReward") && gt.HaltReason(ClickOwner.Locate) == "unknown", "offer close: an unknown top form after the pilot's click stops with its name");
    }
    {   // the loop cap: MaxOfferCloses in a row, then a stop; a click of the pilot's own resets the count
      var g = new ClickGate();
      int n = 0;
      for (int i = 0; i < ClickGate.MaxOfferCloses + 2; i++) {
        var rq = Rq(ClickOwner.Equip, "Form_PopUpShop", c.X, c.Y);
        var r = g.Check(rq, shop, i * 5000);
        if (!r.Allowed) break;
        n++; g.Arm(ClickOwner.Equip, ClickTarget.OfferClose, ClickGate.Expect(rq, shop), i * 5000, "Form_PopUpShop");
        g.Observe(St(new string[0], true), i * 5000 + 100);
      }
      Eq(ClickGate.MaxOfferCloses, n, "offer loop: closes allowed in a row");
      Eq("offers", g.HaltReason(ClickOwner.Equip), "offer loop: then a stop");
      var g2 = new ClickGate();
      for (int i = 0; i < ClickGate.MaxOfferCloses; i++) g2.Arm(ClickOwner.Equip, ClickTarget.OfferClose, null, i, "x");
      g2.Arm(ClickOwner.Equip, ClickTarget.EquipNav, null, 100);
      Check(g2.Check(Rq(ClickOwner.Equip, "Form_PopUpShop", c.X, c.Y), shop, 200).Allowed && g2.OfferRun(ClickOwner.Equip) == 0, "offer loop: the pilot's own click resets the count");
    }
    {   // the window table: a shown offer by its class name, a closed one not, an unknown shown form named
      var m = new FakeLua(1 << 20); RFX.TestMemory = m; RFX.ForgetLive();
      try {
        var fs = new FakeState(m);
        Func<string, FTab> Cls = nm => fs.T("__cname", nm);
        var city = fs.T("m_LuaLogicActive", true, "class", Cls("Form_MainCity"));
        var shopF = fs.T("m_LuaLogicActive", true, "class", Cls("Form_PopUpShop"));
        var giftF = fs.T("m_InitOK", true, "class", Cls("Form_GiftPush"));   // closed: m_LuaLogicActive nil
        var odd = fs.T("m_LuaLogicActive", true, "class", Cls("Form_ItemReward"));
        var ui = fs.T(11L, city, 22L, shopF, 33L, giftF, 44L, odd);
        RFX.SetUiTableForTest(ui.A);
        var offers = RFX.ReadOffers();
        Check(offers != null && offers.Count == 1 && offers[0] == "Form_PopUpShop", "window table: the shown offer is read by its class (" + (offers == null ? "null" : string.Join(",", offers.ToArray())) + ")");
        var unknown = RFX.ReadUnknownShownForms(4);
        Check(unknown != null && unknown.Count == 1 && unknown[0] == "Form_ItemReward", "window table: the unknown shown form is named, the city and the offers are not");
        RFX.SetUiTableForTest(0);
        Check(RFX.ReadOffers() == null, "window table not known: offers not read (null)");
      } finally { RFX.SetUiTableForTest(0); RFX.TestMemory = null; RFX.ForgetLive(); }
    }
    {   // the automatic recording (TEST build): what is recorded and how often
      var p = new AutoRecPolicy(); string why;
      Check(!p.Want(false, false, 0, out why), "auto rec: never in the normal build");
      Check(p.Want(true, false, 1000, out why), "auto rec: the first event records");
      Check(!p.Want(true, false, 1000 + AutoRecPolicy.MinGapMs - 1, out why), "auto rec: not again within 2 minutes (" + why + ")");
      Check(!p.Want(true, true, 1000 + AutoRecPolicy.MinGapMs * 3, out why), "auto rec: not while a recording runs");
      int got = 1; long t = 1000;
      for (int i = 0; i < 20; i++) { t += AutoRecPolicy.MinGapMs; if (p.Want(true, false, t, out why)) got++; }
      Eq(AutoRecPolicy.MaxPerHour, got, "auto rec: an event every 2 minutes for 40 minutes records " + AutoRecPolicy.MaxPerHour + " times");
      var q = new AutoRecPolicy(); int inHour = 0;
      for (long ms = 0; ms < AutoRecPolicy.HourMs; ms += 60000) if (q.Want(true, false, ms, out why)) inHour++;
      Check(inHour <= AutoRecPolicy.MaxPerHour, "auto rec: at most " + AutoRecPolicy.MaxPerHour + " an hour (" + inHour + ")");
      Check(q.Want(true, false, AutoRecPolicy.HourMs + 5 * 60000, out why), "auto rec: an hour later again");
      Eq("offer-fail Form_PopUpShop nochange", AutoRecPolicy.HaltTag("Sell", "nochange", "OfferClose(Form_PopUpShop) changed nothing within the time (screen City)"), "auto rec: a failed offer close is tagged by its form");
      Eq("offer-fail Form_Feedback offer", AutoRecPolicy.HaltTag("Locate", "offer", "the offer pop-up Form_Feedback has no known close button - not clicked"), "auto rec: an offer without × is tagged by its form");
      Check(AutoRecPolicy.HaltTag("Equip", "screen", "EquipNav @1,2 needs City").StartsWith("halt Equip screen: "), "auto rec: any other stop is tagged by owner and reason");
      Eq("unknown-form Form_ItemReward", AutoRecPolicy.UnknownFormTag("Form_ItemReward"), "auto rec: an unknown form on top is tagged by its name");
    }
  }

  static void CityGateAndPanelTests() {
    Check(ScreenState.NeedsCityPicture(ScreenKind.Unknown, false), "city picture: Unknown with the city form reading closed needs the picture");
    Check(!ScreenState.NeedsCityPicture(ScreenKind.Unknown, true), "city picture: Unknown with the city form open does not");
    Check(ScreenState.NeedsCityPicture(ScreenKind.Unknown, null), "city picture: Unknown with the form unreadable needs the picture");
    Check(ScreenState.NeedsCityPicture(ScreenKind.City, false) && ScreenState.NeedsCityPicture(ScreenKind.City, true), "city picture: City needs it");
    Check(!ScreenState.NeedsCityPicture(ScreenKind.Inventory, false), "city picture: another screen does not");
    {   // the pilot loop: Preflight on a held City every tick, Check on an Unknown fresh state: ends in the unknown halt, not an endless Wait
      var gate = new ClickGate(); var log = new List<string>(); gate.Sink = x => log.Add(x);
      var city = ScreenState.Classify(new ScreenInputs { CityForm = true }, 0);
      var unk = new ScreenState();
      var rq = new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.CityInventory, AutoOn = true, Front = true };
      GateResult res = null; bool halted = false; long t = 0;
      for (; t <= ClickGate.UnknownGraceMs + 600 && !halted; t += 100) {
        gate.Preflight(ClickOwner.Sell, city, t);
        res = gate.Check(rq, unk, t);
        halted = res.Verdict == GateVerdict.Deny;
      }
      Check(halted && gate.HaltReason(ClickOwner.Sell) == "unknown", "gate loop: Preflight(City) + Check(Unknown) past the grace -> halt unknown");
      int waits = 0; foreach (var l in log) if (l.Contains("wait (unknown)")) waits++;
      Eq(1, waits, "gate loop: the unknown wait is logged once");
    }
    {   // the hero screen covers the city (live 2026-10-29: «Литейная» allowed on HeroScreen tab 3 slot 0, nothing changed): no city / Foundry button on it
      var heroUp = ScreenState.Classify(new ScreenInputs { Hero = new HeroScreen { Hero = 204200000, FormOk = true, Tab = 3, Part = 0 }, CityForm = true, CitySeen = true }, 0);
      Eq(ScreenKind.HeroScreen, heroUp.Kind, "city gate: the hero screen over an open city form is the hero screen");
      foreach (ClickTarget ct in new[] { ClickTarget.FoundryButton, ClickTarget.CityInventory }) {
        var g1 = new ClickGate(); var res = g1.Check(new ClickRequest { Owner = ClickOwner.Locate, Target = ct, AutoOn = true, Front = true }, heroUp, 0);
        Check(res.Verdict == GateVerdict.Deny && res.Reason == "screen", "city gate: " + ct + " refused while the hero screen is on top, the city open beneath");
        g1 = new ClickGate(); res = g1.Check(new ClickRequest { Owner = ClickOwner.Sell, Target = ct, AutoOn = true, Front = true }, heroUp, 0);
        Check(!res.Allowed, "city gate: " + ct + " refused for the storage cleanup too");
      }
      var heroGrid = ScreenState.Classify(new ScreenInputs { Hero = new HeroScreen { Hero = 204200000, FormOk = true, Tab = 3, Part = -1 } }, 0);
      Check(!new ClickGate().Check(new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.FoundryButton, AutoOn = true, Front = true }, heroGrid, 0).Allowed, "city gate: the hero gear screen's «Литейная» is not a target either");
      var g2 = new ClickGate(); var back = new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.HeroBack, AutoOn = true, Front = true };
      Check(g2.Check(back, heroUp, 0).Allowed, "city gate: the back arrow is allowed on the hero screen");
      var exp = ClickGate.Expect(back, heroUp);
      Check(exp(heroGrid), "city gate: the back arrow closing the gear list is its effect (the hero grid shows again)");
      Check(exp(ScreenState.Classify(new ScreenInputs { CityForm = true }, 0)), "city gate: ... and so is the city");
      Check(!exp(heroUp), "city gate: ... the gear list still open is no effect");
      var expGrid = ClickGate.Expect(back, heroGrid);
      Check(!expGrid(heroGrid) && expGrid(ScreenState.Classify(new ScreenInputs { CityForm = true }, 0)), "city gate: from the hero grid only leaving the hero screen counts");
      var city = ScreenState.Classify(new ScreenInputs { CityForm = true }, 0);
      Check(new ClickGate().Check(new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.CityInventory, AutoOn = true, Front = true }, city, 0).Allowed, "city gate: «Инвентарь» on the city still allowed");
    }
    Eq(0UL, RFX.PanelAfterForm(0x1000UL, 0x10UL, 0x20UL, 0), "panel: the form changed, no EquipFilter found -> 0");
    Eq(0x1000UL, RFX.PanelAfterForm(0x1000UL, 0x10UL, 0x10UL, 0), "panel: same form -> kept");
    Eq(0x2000UL, RFX.PanelAfterForm(0x1000UL, 0x10UL, 0x20UL, 0x2000UL), "panel: the new form's panel found -> it");
  }

  static void ArtifactSellTests() {
    Console.WriteLine("Artifact storage cleanup (hints only)");
    // --- the panel reader (READ ONLY) on fake script tables
    var m = new FakeLua(1 << 20); RFX.TestMemory = m; RFX.ForgetLive();
    try {
      var st = new FakeState(m);
      Func<long, FTab> art = uid => st.T("iItemUid", uid, "iItemId", 100L + uid % 7, "iLevel", 1L, "iStageLvl", 0L, "iHeroId", 0L, "m_AfCfg", st.T("m_Id", 5L));
      Func<long, bool, long, FTab, FTab, FTab> panelOf = (flag, active, cols, list, sel) => st.T("_isInited", true, "_isActive", active, "_btFlagBatch", flag, "_countCol", cols, "_listDataCur", list, "_btDicSelected", sel);
      var list3 = st.T(1L, art(9001), 2L, art(9002), 3L, art(9003));
      var sel = st.T(9002L, true, 9001L, false, 9003L, true);   // a deselected artifact stays with false
      var panels = st.T(-9998L, st.T("m_CurrentBottomButtonState", 1L), -9997L, panelOf(2, true, 7, list3, sel));
      var s = new InventoryScreen { Open = true, ArtTab = true };
      RFX.ReadArtifactPanel(panels.A, s);
      Check(s.ArtBulkKnown && s.ArtBulk, "art panel: _btFlagBatch 2 = bulk sale");
      Eq("9001,9002,9003", string.Join(",", s.Uids), "art panel: the list in screen order (iItemUid of _listDataCur)");
      Eq("9002,9003", string.Join(",", new SortedSet<long>(s.Selected)), "art panel: only the true entries of _btDicSelected are selected");
      Check(s.ListPtr == list3.A && !s.EquipTab && !s.Bulk, "art panel: the list table is the list pointer; the gear fields stay off");
      // normal mode: known, not bulk, no list read
      s = new InventoryScreen { Open = true, ArtTab = true };
      RFX.ReadArtifactPanel(st.T(-9997L, panelOf(-1, true, 7, list3, sel)).A, s);
      Check(s.ArtBulkKnown && !s.ArtBulk && s.Uids.Count == 0 && s.Selected.Count == 0, "art panel: normal mode (-1): not bulk, nothing read");
      // arrange mode (1) is not bulk sale
      s = new InventoryScreen { Open = true, ArtTab = true };
      RFX.ReadArtifactPanel(st.T(-9997L, panelOf(1, true, 7, list3, sel)).A, s);
      Check(s.ArtBulkKnown && !s.ArtBulk, "art panel: «Масс. организовать» (1) is not the sale mode");
      // a panel that is not shown yet: the mode is not known (never assumed)
      s = new InventoryScreen { Open = true, ArtTab = true };
      RFX.ReadArtifactPanel(st.T(-9997L, panelOf(2, false, 7, list3, sel)).A, s);
      Check(!s.ArtBulkKnown && !s.ArtBulk && s.Uids.Count == 0, "art panel: _isActive false -> mode unknown, no list");
      // no panel for the tab: nothing changes
      s = new InventoryScreen { Open = true, ArtTab = true };
      RFX.ReadArtifactPanel(st.T(-9998L, st.T("x", 1L)).A, s);
      Check(!s.ArtBulkKnown && !s.ArtBulk && s.Uids.Count == 0, "art panel: no panel for -9997 -> nothing read");
      // a hole in the array ends the list (a script array is 1..n)
      s = new InventoryScreen { Open = true, ArtTab = true };
      RFX.ReadArtifactPanel(st.T(-9997L, panelOf(2, true, 7, st.T(1L, art(9001), 3L, art(9003)), st.T())).A, s);
      Eq("9001", string.Join(",", s.Uids), "art panel: a gap in the array ends the list");
      // uids the script holds as strings or whole floats, a choice keyed by strings (the live list read nothing: the types are not known)
      var artS = st.T("iItemUid", "9101", "iHeroId", 0L); var artF = st.T("iItemUid", 9102.0, "iHeroId", 0L);
      s = new InventoryScreen { Open = true, ArtTab = true };
      RFX.ReadArtifactPanel(st.T(-9997L, panelOf(2, true, 8, st.T(1L, artS, 2L, artF, 3L, art(9103)), st.T("9101", true, "9102", false, "9103", true))).A, s);
      Eq("9101,9102,9103", string.Join(",", s.Uids), "art panel: string and float uids are read");
      Eq("9101,9103", string.Join(",", new SortedSet<long>(s.Selected)), "art panel: a choice keyed by strings counts its true entries");
      // the diagnostics line: the panel's fields with their types and the entry counts of tables
      var diag = RFX.ArtDiagText(st.T("m_Content_InfinityGrid", 1L, "m_Other", 2L).A, st.T(-9997L, panelOf(2, true, 8, list3, sel)).A);
      Check(diag != null && diag.StartsWith("art panel fields: ") && diag.Contains("_listDataCur=table[3]") && diag.Contains("_btFlagBatch=num") && diag.Contains("m_Content_InfinityGrid=num") && diag.Contains("_listDataCur[1]: ") && diag.Contains("iItemUid=num") && diag.Length <= 1500, "art diag: panel fields with type and count (" + diag + ")");
      Check(RFX.ArtDiagText(0, st.T(-9998L, st.T("x", 1L)).A) == null, "art diag: no panel -> no line");
    } finally { RFX.TestMemory = null; RFX.ForgetLive(); }

    // --- the screen state
    var artInv = new InventoryScreen { Open = true, ArtTab = true, ArtBulk = true, ArtBulkKnown = true, ListPtr = 5 }; artInv.Uids.AddRange(new long[] { 1, 2, 3 }); artInv.Selected.Add(2);
    var scr = ScreenState.Classify(new ScreenInputs { Inv = artInv }, 0);
    Check(scr.Kind == ScreenKind.Inventory && scr.InvArtTab && scr.InvArtBulk && scr.InvArtBulkKnown && !scr.InvEquipTab && !scr.InvBulk, "art screen: the inventory on the artifact tab, bulk sale");
    Check(scr.Brief.Contains("artifact") && scr.Brief.Contains("ART-BULK-SALE") && !scr.Brief.Contains(" BULK-SALE") && scr.InvSelKey == "1:2", "art screen: brief says artifact bulk sale, the choice key counts the selection (" + scr.Brief + ")");
    var scrN = ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true } }, 0);
    Check(scrN.Brief.Contains("artifact normal") && scrN.Key != scr.Key, "art screen: normal mode has its own key");
    var tr = new ScreenTracker(); tr.Update(new ScreenInputs { Inv = artInv }, 0);
    var held = tr.Update(new ScreenInputs(), 500);
    Check(held.Held && held.InvArtTab && held.InvArtBulk, "art screen: a short read failure keeps the artifact tab");

    // --- no clicks, whatever the auto click says
    {
      var gate = new ClickGate();
      Func<ClickTarget, ClickRequest> R = tg => new ClickRequest { Owner = ClickOwner.Sell, Target = tg, AutoOn = true, Front = true, CellUid = 2, CellOk = new HashSet<long> { 2 } };
      var res = gate.Check(R(ClickTarget.InvBulkCell), scr, 0);
      Check(!res.Allowed && gate.HaltReason(ClickOwner.Sell) == "artifact", "art gate: a cell click on the artifact tab is stopped even with the auto click on (" + res.Reason + ")");
      gate = new ClickGate(); res = gate.Check(R(ClickTarget.InvBulkEnter), scrN, 0);
      Check(!res.Allowed && gate.HaltReason(ClickOwner.Sell) == "artifact", "art gate: «Массовая продажа» is never pressed on the artifact tab");
      gate = new ClickGate(); gate.NoSellClicks = true;
      var eq = ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, Bulk = true, BulkKnown = true } }, 0);
      res = gate.Check(R(ClickTarget.InvBulkCell), eq, 0);
      Check(!res.Allowed && gate.HaltReason(ClickOwner.Sell) == "artifact", "art gate: while an artifact cleanup runs the Sell owner never clicks, on any screen");
      // the list's exact scroll cannot be read (the layout data has no list fields): the drag is refused before anything is scrolled, with its own reason
      var eqBulk = ScreenState.Classify(new ScreenInputs { Inv = new InventoryScreen { Open = true, EquipTab = true, Bulk = true, BulkKnown = true } }, 0);
      gate = new ClickGate(); res = gate.Check(new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.InvListDrag, AutoOn = true, Front = true, NoScroll = true }, eqBulk, 0);
      Check(!res.Allowed && gate.HaltReason(ClickOwner.Sell) == "scroll", "sell gate: a list drag without a readable scroll stops the run as 'scroll' (" + res.Reason + ")");
      gate = new ClickGate(); res = gate.Check(new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.InvListDrag, AutoOn = true, Front = true }, eqBulk, 0);
      Check(res.Allowed, "sell gate: a list drag with a readable scroll is allowed (" + res.Reason + ")");
      // a cell click with an unreadable scroll: the item under it cannot be told - the reason is the scroll, not "the click would hit another item"
      gate = new ClickGate(); res = gate.Check(new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.InvBulkCell, AutoOn = true, Front = true, NoScroll = true, CellUid = 0, CellOk = new HashSet<long> { 2 } }, eqBulk, 0);
      Check(!res.Allowed && gate.HaltReason(ClickOwner.Sell) == "scroll", "sell gate: a bulk-sale cell click without a readable scroll stops as 'scroll', not 'cell' (" + res.Reason + ")");
      gate = new ClickGate(); res = gate.Check(new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.InvBulkCell, AutoOn = true, Front = true, CellUid = 7, CellOk = new HashSet<long> { 2 } }, eqBulk, 0);
      Check(!res.Allowed && gate.HaltReason(ClickOwner.Sell) == "cell", "sell gate: a readable scroll and a wrong item stays 'cell' (" + res.Reason + ")");
      gate = new ClickGate(); res = gate.Check(new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.InvBulkCell, AutoOn = true, Front = true, CellUid = 2, CellOk = new HashSet<long> { 2 } }, eqBulk, 0);
      Check(res.Allowed, "sell gate: a bulk-sale cell click with the exact scroll and a wanted item is allowed (" + res.Reason + ")");
      // the pilot: the game's scroll unreadable on every tick -> it waits, never sends a cell click, and stops as 'scroll'
      {
        var gs = new SellGeometry(); var fg = new FakeSellGame(gs, 40, 0); var ws = new HashSet<long> { 5000, 5001, 5002 };
        var ps = new SellPilot(gs); long ts = 0; int cellClicks = 0;
        for (int i = 0; i < 100 && ps.State != "failed"; i++, ts += 100) {
          var a = ps.Step(new SellView { NowMs = ts, Foreground = true, W = fg.W, H = fg.H, U = fg.U, Screen = fg.Screen(), Wanted = ws, Phase = fg.Phase(), NeedExact = true });
          if (a.Kind == AutoKind.Click) cellClicks++;
          fg.Act(a);
        }
        Check(cellClicks == 0 && ps.State == "failed" && ps.FailWhy == "scroll", "sell pilot: Row1 NaN on every tick: no cell click, ends 'scroll' (clicks " + cellClicks + ", " + ps.State + "/" + ps.FailWhy + ")");
        Check(ts >= SellPilot.ScrollWaitMs, "sell pilot: it re-reads the scroll for about " + SellPilot.ScrollWaitMs + " ms first (" + ts + " ms)");
        var pe = new SellPilot(gs); var fe = new FakeSellGame(gs, 40, 0);
        var ae = pe.Step(new SellView { NowMs = 0, Foreground = true, W = fe.W, H = fe.H, U = fe.U, Screen = fe.Screen(), Wanted = ws, Phase = double.NaN, Row1 = gs.ViewTopPx(fe.U) + gs.TopPad * fe.U, NeedExact = true });
        Check(ae.Kind == AutoKind.Click && pe.State == "work", "sell pilot: with an exact Row1 the cell click goes out (" + ae.Kind + ", " + pe.State + ")");
        var pl = new SellPilot(gs);   // a scroll that comes back in time: no stop
        var al = pl.Step(new SellView { NowMs = 0, Foreground = true, W = fg.W, H = fg.H, U = fg.U, Screen = fe.Screen(), Wanted = ws, Phase = double.NaN, NeedExact = true });
        al = pl.Step(new SellView { NowMs = 800, Foreground = true, W = fg.W, H = fg.H, U = fg.U, Screen = fe.Screen(), Wanted = ws, Phase = double.NaN, Row1 = gs.ViewTopPx(fe.U) + gs.TopPad * fe.U, NeedExact = true });
        Check(al.Kind == AutoKind.Click && pl.FailWhy == "", "sell pilot: a scroll read that comes back within the wait lets the click go (" + al.Kind + ")");
      }
      // a pause in the middle of the scroll wait: the wait starts over, no instant "scroll" fail
      {
        var gs = new SellGeometry(); var fg = new FakeSellGame(gs, 40, 0); var ws = new HashSet<long> { 5000, 5001, 5002 };
        var pp = new SellPilot(gs); long tp = 0;
        Func<bool, bool, SellView> vw = (busy, front) => new SellView { NowMs = tp, Foreground = front, UserBusy = busy, W = fg.W, H = fg.H, U = fg.U, Screen = fg.Screen(), Wanted = ws, Phase = fg.Phase(), NeedExact = true };
        for (int i = 0; i < 8; i++, tp += 100) fg.Act(pp.Step(vw(false, true)));
        Check(pp.State != "failed", "sell pilot: waiting for the scroll, not failed yet");
        for (int i = 0; i < 60; i++, tp += 100) pp.Step(vw(true, true));   // the player works for 6 s
        pp.Step(vw(false, false)); tp += 100;                                // then the window is in the background
        pp.Step(vw(false, true));
        Check(pp.State != "failed" && pp.FailWhy == "", "sell pilot: after a pause the scroll wait starts over (" + pp.State + "/" + pp.FailWhy + ")");
        for (int i = 0; i < 100 && pp.State != "failed"; i++, tp += 100) fg.Act(pp.Step(vw(false, true)));
        Check(pp.State == "failed" && pp.FailWhy == "scroll", "sell pilot: an unreadable scroll still ends 'scroll' after a full wait");
      }
      gate = new ClickGate();
      res = gate.Check(new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.InvEquipTab, AutoOn = true, Front = true }, scrN, 0);
      Check(res.Allowed, "art gate: a gear cleanup (not an artifact run) may still go from the artifact tab back to «Снаряжение»");
      Check(!ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtTab") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtBulk") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtCell") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtWorkshop") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtMat") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtPromote") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtListDrag"), "art gate: the equip helper has none of the inventory / workshop artifact targets on its whitelist (only its own hero-screen ones, see ArtEquipTests; the others have theirs, behind ArtSellAuto / ArtLocateAuto)");
    }

    // --- the steps
    double W = 1136, H = 640, U = Ui.Unit(W, H);
    var g = SellGeometry.Artifacts(W, H, U, 7);
    var uids = new List<long>(); for (long i = 1; i <= 56; i++) uids.Add(9000 + i);   // 8 rows of 7
    Func<InventoryScreen> bulk = () => new InventoryScreen { Open = true, ArtTab = true, ArtBulk = true, ArtBulkKnown = true, ListPtr = 7, Uids = new List<long>(uids) };
    var wanted = new HashSet<long> { 9001, 9003, 9009, 9050 };   // rows 1, 1, 2, 8
    Eq(SellHintKind.OpenInventory, SellHint.Classify(null, wanted, true).Kind, "art hint: no inventory -> open it");
    Eq(SellHintKind.OpenArtTab, SellHint.Classify(new InventoryScreen { Open = true, EquipTab = true, Bulk = true }, wanted, true).Kind, "art hint: the gear tab (even in its bulk sale) -> open the «Артефакт» tab");
    Eq(SellHintKind.PressBulk, SellHint.Classify(new InventoryScreen { Open = true, ArtTab = true }, wanted, true).Kind, "art hint: artifact tab, normal mode -> press «Массовая продажа»");
    Eq(SellHintKind.OpenEquipTab, SellHint.Classify(new InventoryScreen { Open = true, ArtTab = true }, wanted).Kind, "gear hint is unchanged: the artifact tab is just another tab for it");
    Eq("openArtTab", SellHint.Classify(new InventoryScreen { Open = true }, wanted, true).Key, "art hint: page key of the tab step");

    // frames: the cell geometry from the research numbers (1136 x 640: list 125..801 x 123..550.4, 7 columns, row 116, card 80 x 110 centred)
    var gr = new LocateGrid(); gr.FromScroll(g.ViewTopPx(U) + g.TopPad * U, 7);
    var step = SellHint.Build(bulk(), wanted, 7, gr, g, W, H, U, true);
    Check(step.Kind == SellHintKind.Select && step.Total == 4 && step.Selected == 0, "art hint: nothing ticked: 0 of 4");
    var f1 = step.Frames.Find(fr => fr.Uid == 9001); var f3 = step.Frames.Find(fr => fr.Uid == 9003);
    double cellW = (1136 - (0.5231 + 0.195) * 640) / 7;
    Check(f1 != null && Math.Abs(f1.X - (124.8 + (cellW - 80) / 2)) <= 1.5 && Math.Abs(f1.Y - 126) <= 1.5 && Math.Abs(f1.W - 80) <= 1.5 && Math.Abs(f1.H - 110) <= 1.5,
          "art hint: the frame of cell 1 is the 80 x 110 card, centred in the 97 px cell (" + (f1 == null ? "none" : f1.X + "," + f1.Y + " " + f1.W + "x" + f1.H) + ")");
    Check(f3 != null && Math.Abs(f3.X - f1.X - 2 * cellW) <= 1.5, "art hint: cell 3 is two cells to the right");
    var f9 = step.Frames.Find(fr => fr.Uid == 9009);
    Check(f9 != null && Math.Abs(f9.Y - f1.Y - 116) <= 1.5 && Math.Abs(f9.X - f1.X - 1 * cellW) <= 1.5, "art hint: item 9 is row 2, column 2 (one row = 116 units)");
    Check(step.Frames.Count == 3 && step.Below == 1, "art hint: three in view, one below (frames " + step.Frames.Count + ", below " + step.Below + ")");
    // scrolled: the content moved up by 3 rows (ScrollY = 3 x 116 canvas units)
    var scroll = new InvScroll();
    double row1 = scroll.Row1(g, new RFX.ListRead { ScrollY = 3 * 116, Cols = 7, RowUnits = 116 }, double.NaN, U);
    var stS = SellHint.BuildExact(bulk(), wanted, 7, row1, g, W, H, U, true);
    Check(stS.Above == 3 && stS.Below == 1 && stS.Frames.Count == 0, "art hint: scrolled down 3 rows -> the first three are above, the last one still below (above " + stS.Above + ", below " + stS.Below + ")");
    double row1b = scroll.Row1(g, new RFX.ListRead { ScrollY = 58, Cols = 7, RowUnits = 116 }, double.NaN, U);
    var stH = SellHint.BuildExact(bulk(), wanted, 7, row1b, g, W, H, U, true);
    var f1h = stH.Frames.Find(fr => fr.Uid == 9001);
    Check(f1h != null && Math.Abs((f1.Y - f1h.Y) - 58 * U / 640) <= 1.01, "art hint: the frame moves with ScrollY exactly (58 units = 58 px at 640)");
    var nx = SellHint.BuildExact(bulk(), wanted, 7, double.NaN, g, W, H, U, true);
    Check(nx.Kind == SellHintKind.Select && nx.Frames.Count == 0 && nx.Total == 4, "art hint: no exact scroll -> no frames, the counts stay");

    // ticks and strays
    var inv = bulk(); inv.Selected.Add(9003);
    step = SellHint.Build(inv, wanted, 7, gr, g, W, H, U, true);
    Check(step.Selected == 1 && step.Frames.Find(fr => fr.Uid == 9003).Style == "done" && step.Frames.Find(fr => fr.Uid == 9001).Style == "todo", "art hint: a ticked artifact gets the checked frame (1 of 4)");
    inv.Selected.Add(9002);   // not on the list
    step = SellHint.Build(inv, wanted, 7, gr, g, W, H, U, true);
    Check(step.Extra == 1 && step.Frames.Find(fr => fr.Uid == 9002).Style == "extra" && SellHint.Warn(step), "art hint: a wrongly ticked artifact: red frame and the warning");
    var allT = bulk(); foreach (var u in wanted) allT.Selected.Add(u);
    Eq(SellHintKind.AllSelected, SellHint.Classify(allT, wanted, true).Kind, "art hint: all ticked -> press «Продать»");
    var noneT = bulk(); noneT.Uids.RemoveAll(u => wanted.Contains(u));
    Eq(SellHintKind.NoneHere, SellHint.Classify(noneT, wanted, true).Kind, "art hint: none of the list in the storage list");

    // the rectangles: research numbers, or the site's geometry
    var tb = SellHint.ArtTabRect(W, H, U); var bb = SellHint.ArtBulkRect(W, H, U);
    {
      Check(SellGeometry.ArtCols(new GeoRect { X = 0.195 * 705, Y = 0, W = (1366.0 / 705 - 0.5231 - 0.195) * 705, H = 400 }, 705, 7) == 8 && SellGeometry.ArtCols(new GeoRect { X = 0.195 * U, Y = 0, W = (W / U - 0.5231 - 0.195) * U, H = 400 }, U, 8) == 7 && SellGeometry.ArtCols(null, U, 8) == 8,
            "art hint: columns from the list's width: 8 at the live window, 7 at the authored one, the fallback without a rectangle");
    }
    Check(Math.Abs(tb[0] - 0.0023 * U) <= 1.5 && Math.Abs(tb[1] - 0.4022 * U) <= 1.5 && Math.Abs(tb[2] - 0.1688 * U) <= 1.5 && Math.Abs(tb[3] - 0.125 * U) <= 1.5, "art hint: the «Артефакт» toggle (research numbers) " + string.Join(",", tb));
    Check(Math.Abs(bb[0] - 0.4728 * U) <= 1.5 && Math.Abs(bb[1] - (H - 0.0983 * U)) <= 1.5 && Math.Abs(bb[2] - 0.2466 * U) <= 1.5 && Math.Abs(bb[3] - 0.0575 * U) <= 1.5, "art hint: «Массовая продажа» (live recording numbers) " + string.Join(",", bb));
    string webGeo = Path.Combine(WebRoot(), "data", "app-geometry", "1.6.39.829.1.json");
    if (File.Exists(webGeo)) {
      var rg = GameGeometry.Parse(File.ReadAllText(webGeo, Encoding.UTF8), "1.6.39.829.1");
      Check(rg != null && rg.Has("inventory.leftTab.artifact") && rg.Has("inventory.artBulkSell") && rg.Has("inventory.artList") && rg.Grid("inventory.artList") != null && rg.Grid("inventory.artList").Cols == 8 && Math.Abs(rg.Grid("inventory.artList").LineSize - 116) < 0.01,
            "art hint: the site's geometry file has the tab, the button, the list and the 8 x 116 grid");
      if (rg != null) {
        var lr = rg.Rect("inventory.artList", W, H, U); var tr2 = rg.Rect("inventory.leftTab.artifact", W, H, U); var br = rg.Rect("inventory.artBulkSell", W, H, U);
        Check(Math.Abs(lr.X - 124.8) <= 1 && Math.Abs(lr.Y - 123) <= 1 && Math.Abs(lr.Right - 801.2) <= 1.5 && Math.Abs(lr.Bottom - 550.4) <= 1, "art hint: the list rectangle from the file (125..801 x 123..550)");
        Check(Math.Abs(tr2.Y - 257.4) <= 1 && Math.Abs(br.X - 302.6) <= 1, "art hint: the tab and the button from the file");
        var ga = SellGeometry.Artifacts(W, H, U, 7, lr);
        Check(Math.Abs(ga.PitchX - g.PitchX) < 0.002 && Math.Abs(ga.ViewTop - g.ViewTop) < 0.002 && Math.Abs(ga.ViewBottomPx(H, U) - g.ViewBottomPx(H, U)) < 1.5, "art hint: the geometry from the file agrees with the research numbers");
      }
    }
    // other window shapes: the game's 8 columns (floor((width - 6) / 90) of canvas units) stay inside the list and the list's right edge follows the window
    foreach (var wh in new[] { new[] { 1920.0, 1009.0 }, new[] { 1366.0, 705.0 } }) {
      double u2 = Ui.Unit(wh[0], wh[1]); var g2 = SellGeometry.Artifacts(wh[0], wh[1], u2, 8);
      double right = g2.X(g2.X0 + 7 * g2.PitchX + g2.CellW, wh[0], u2);
      Check(right <= wh[0] - 0.5231 * u2 + 1 && g2.ViewTopPx(u2) > 0 && g2.ViewBottomPx(wh[1], u2) < wh[1], "art hint: " + wh[0] + "x" + wh[1] + ": the columns end inside the list");
    }
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

  // «Показать в игре» for an ARTIFACT promotion (purpose artPromote): the request, the readers on fake tables, the screen, the steps, the frames, the finish; never a click
  static void ArtPromoteTests() {
    Console.WriteLine("Artifact promotion guide (hints only)");
    // --- the request from the site
    var body = "{\"ok\":true,\"plans\":[],\"locate\":{\"id\":\"r1\",\"purpose\":\"artPromote\",\"createdAt\":\"2026-10-18T10:00:00Z\",\"item\":null,\"art\":{\"uid\":5001,\"id\":200088,\"name\":\"Клинок\",\"lv\":10,\"cap\":10,\"st\":0,\"need\":2,\"via\":\"copy\",\"copy\":5002,\"copies\":[5002,5003]}}}";
    var pr = PlansClient.Interpret(200, body, true);
    Check(pr.Locate != null && pr.Locate.Purpose == "artPromote" && pr.Locate.Item == null && pr.Locate.Art != null && pr.Locate.Art.Uid == 5001 && pr.Locate.Art.Need == 2 && pr.Locate.Art.Cap == 10
          && pr.Locate.Art.Copy == 5002 && pr.Locate.Art.Copies.Count == 2 && pr.Locate.Art.Via == "copy" && pr.Locate.Art.Name == "Клинок", "art request: parsed with its copies");
    var none = PlansClient.Interpret(200, "{\"ok\":true,\"plans\":[],\"locate\":{\"id\":\"r2\",\"purpose\":\"artPromote\",\"item\":null,\"art\":null}}", true);
    Check(none.Locate != null && none.Locate.Art == null && none.Locate.Item == null, "art request: an artifact the snapshot lacks stays a request without a card");
    Check(PlansClient.Interpret(200, "{\"ok\":true,\"plans\":[],\"locate\":{\"id\":\"r3\",\"purpose\":\"mystery\"}}", true).Locate == null, "art request: an unknown purpose is dropped");
    var req = pr.Locate;

    // --- the readers on fake tables
    var m = new FakeLua(1 << 20); RFX.TestMemory = m; RFX.ForgetLive();
    try {
      var st = new FakeState(m);
      Func<long, long, FTab> art = (uid, lv) => st.T("iItemUid", uid, "iItemId", 200088L, "iLevel", lv, "iStageLvl", 0L, "iHeroId", 0L);
      Func<long, bool, long, long, FTab, FTab, FTab> panelOf = (flag, active, sel, cols, list, dic) => st.T("_isInited", true, "_isActive", active, "_btFlagBatch", flag, "_selectedIndexMain", sel, "_countCol", cols, "_listDataCur", list, "_btDicSelected", dic);
      var list3 = st.T(1L, art(5001, 10), 2L, art(5002, 1), 3L, art(5003, 1));
      // normal mode is not read unless a guide wants it
      RFX.ArtListWanted = false;
      var s0 = new InventoryScreen { Open = true, ArtTab = true };
      RFX.ReadArtifactPanel(st.T(-9997L, panelOf(-1, true, 2, 7, list3, st.T())).A, s0);
      Check(s0.ArtBulkKnown && s0.ArtMode == -1 && s0.Uids.Count == 0 && s0.ArtSel == 0, "art guide reader: normal mode, nothing read while no guide wants it");
      RFX.ArtListWanted = true;
      try {
        var s1 = new InventoryScreen { Open = true, ArtTab = true };
        RFX.ReadArtifactPanel(st.T(-9997L, panelOf(-1, true, 2, 7, list3, st.T())).A, s1);
        Eq("5001,5002,5003", string.Join(",", s1.Uids), "art guide reader: the list in screen order in normal mode");
        Check(s1.ArtSelKnown && s1.ArtSel == 2 && !s1.ArtBulk && s1.ArtMode == -1, "art guide reader: _selectedIndexMain = the open card's place");
        var s2 = new InventoryScreen { Open = true, ArtTab = true };
        RFX.ReadArtifactPanel(st.T(-9997L, panelOf(1, true, 1, 7, list3, st.T())).A, s2);
        Check(s2.ArtMode == 1 && !s2.ArtBulk && s2.Uids.Count == 3, "art guide reader: «Масс. организовать» is mode 1");
        var s3 = new InventoryScreen { Open = true, ArtTab = true };
        RFX.ReadArtifactPanel(st.T(-9997L, panelOf(-1, true, 9, 7, list3, st.T())).A, s3);
        Check(s3.ArtSelKnown && s3.ArtSel == 0, "art guide reader: a selected index outside the list is no selection");
        var s4 = new InventoryScreen { Open = true, ArtTab = true };
        RFX.ReadArtifactPanel(st.T(-9997L, panelOf(-1, false, 2, 7, list3, st.T())).A, s4);
        Check(!s4.ArtBulkKnown && s4.Uids.Count == 0, "art guide reader: a panel that is not shown reads nothing");
      } finally { RFX.ArtListWanted = false; }

      // the workshop form
      Func<bool, long, long, long, long, long, FTab, FTab> formOf = (active, tab, uid, lv, stage, x, brk) => st.T("m_LuaLogicActive", active, "m_iItemType", tab,
        "m_ArtifactData", st.T("iItemUid", uid, "iItemId", 200088L, "iLevel", lv, "iStageLvl", stage), "m_PanelEquipBreak", brk);
      var mats = st.T(1L, st.T("iItemUid", 5002L, "iItemId", 200088L, "iLevel", 1L), 2L, st.T("iItemUid", 5003L, "iItemId", 200088L, "iLevel", 7L), 3L, st.T("iItemId", 16100L));
      var brk0 = st.T("m_vMatData", mats, "m_vMatSelect", st.T(2L, true, 1L, false, 3L, true));
      var ws = new ArtWorkshopScreen();
      RFX.ReadArtWorkshopForm(formOf(true, 2, 5001, 10, 0, 0, brk0).A, ws);
      Check(ws.Open && ws.Tab == 2 && ws.Uid == 5001 && ws.Level == 10 && ws.Stage == 0 && ws.ItemId == 200088, "art workshop reader: tab, artifact, level and stage");
      Eq("5002,5003,0", string.Join(",", ws.Mats), "art workshop reader: copies first, an essence stub has no uid");
      Eq("1,7,-1", string.Join(",", ws.MatLevels), "art workshop reader: the copies' levels");
      Eq("200088,200088,16100", string.Join(",", ws.MatItemIds), "art workshop reader: every entry's item id (an essence stub has only that)");
      Eq("2,3", string.Join(",", new SortedSet<int>(ws.MatSel)), "art workshop reader: only the true entries of m_vMatSelect (indices)");
      var wsT = new ArtWorkshopScreen();
      RFX.ReadArtWorkshopForm(formOf(true, 1, 5001, 8, 0, 0, brk0).A, wsT);
      Check(wsT.Tab == 1 && wsT.Mats.Count == 0 && !wsT.PanelKnown, "art workshop reader: the promote panel is read on its own tab only");
      var wsC = new ArtWorkshopScreen();
      RFX.ReadArtWorkshopForm(formOf(false, 2, 5001, 10, 0, 0, brk0).A, wsC);
      Check(!wsC.Open && wsC.Uid == 0 && wsC.Mats.Count == 0, "art workshop reader: a form that is not shown reads nothing");
      Check(ws.Diag.Contains("m_PanelEquipBreak=table") && ws.Diag.Contains("m_vMatData=3 entries") && ws.Diag.Contains("2 copies, 1 essences") && ws.Diag.Contains("m_vMatSelect=2 chosen") && ws.Diag.Contains("m_ArtifactData.iItemUid=read"), "art workshop reader: the diagnostics line names the bound fields and counts (" + ws.Diag + ")");
      // uids the script holds as strings: the open artifact and the copies are still read
      var matsS = st.T(1L, st.T("iItemUid", "5002", "iItemId", 200088L, "iLevel", 1L), 2L, st.T("iItemId", 16100L));
      var wsS = new ArtWorkshopScreen();
      RFX.ReadArtWorkshopForm(st.T("m_LuaLogicActive", true, "m_iItemType", 2L, "m_ArtifactData", st.T("iItemUid", "5001", "iLevel", 10L), "m_PanelEquipBreak", st.T("m_vMatData", matsS, "m_vMatSelect", st.T())).A, wsS);
      Check(wsS.Uid == 5001 && wsS.PanelKnown && string.Join(",", wsS.Mats) == "5002,0", "art workshop reader: uids written as strings are read (" + wsS.Diag + ")");
      // the promote panel not built yet / its list under another name: nothing to read, the line says what the form holds
      var wsNoPanel = new ArtWorkshopScreen();
      RFX.ReadArtWorkshopForm(st.T("m_LuaLogicActive", true, "m_iItemType", 2L, "m_ArtifactData", st.T("iItemUid", 5001L, "iLevel", 10L), "m_PanelEquipEnhanced", st.T("x", 1L)).A, wsNoPanel);
      Check(wsNoPanel.Open && wsNoPanel.Tab == 2 && !wsNoPanel.PanelKnown && wsNoPanel.Diag.Contains("m_PanelEquipBreak=missing") && wsNoPanel.Diag.Contains("m_PanelEquipEnhanced:table"), "art workshop reader: no promote panel yet -> the line lists the form's panels (" + wsNoPanel.Diag + ")");
      var wsN = new ArtWorkshopScreen();
      RFX.ReadArtWorkshopForm(st.T("m_LuaLogicActive", true, "m_iItemType", 2L, "m_ArtifactData", st.T("iItemUid", 5001L), "m_PanelEquipBreak", st.T("m_vOtherData", mats, "m_vMatSelect", st.T())).A, wsN);
      Check(!wsN.PanelKnown && wsN.Diag.Contains("m_vMatData=missing") && wsN.Diag.Contains("m_vOtherData"), "art workshop reader: the list under another name is named in the line (" + wsN.Diag + ")");
      var wsU = new ArtWorkshopScreen();
      RFX.ReadArtWorkshopForm(st.T("m_LuaLogicActive", true, "m_iItemType", 2L, "m_PanelEquipBreak", brk0).A, wsU);
      Check(wsU.Uid == 0 && wsU.PanelKnown && wsU.Mats.Count == 3 && wsU.Diag.Contains("m_ArtifactData=missing"), "art workshop reader: no artifact data on the form, the list is still read");
    } finally { RFX.TestMemory = null; RFX.ForgetLive(); }

    // --- the screen state
    var wsOpen = new ArtWorkshopScreen { Open = true, Tab = 2, Uid = 5001, Level = 10, Stage = 0 };
    var scr = ScreenState.Classify(new ScreenInputs { ArtWs = wsOpen, Inv = new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = -1 } }, 0);
    Check(scr.Kind == ScreenKind.ArtWorkshop && scr.ArtWsTab == 2 && scr.ArtWsUid == 5001 && scr.Brief.Contains("ArtWorkshop tab 2"), "art screen: the workshop sits above the inventory (" + scr.Brief + ")");
    var invN = new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = -1, ArtSelKnown = true, ArtSel = 2 }; invN.Uids.AddRange(new long[] { 5001, 5002, 5003 });
    var scrI = ScreenState.Classify(new ScreenInputs { Inv = invN, ArtWs = new ArtWorkshopScreen { Open = false } }, 0);
    Check(scrI.Kind == ScreenKind.Inventory && scrI.InvArtSelected == 5002 && scrI.InvArtMode == -1, "art screen: a closed workshop leaves the inventory; the open card's uid");
    var tr = new ScreenTracker(); tr.Update(new ScreenInputs { ArtWs = wsOpen }, 0);
    var held = tr.Update(new ScreenInputs(), 500);
    Check(held.Kind == ScreenKind.ArtWorkshop && tr.ArtWs != null, "art screen: a short read failure keeps the workshop");
    var gone = tr.Update(new ScreenInputs(), 5000);
    Check(gone.Kind == ScreenKind.Unknown && tr.ArtWs == null, "art screen: gone for good after the hold");

    // --- the steps
    Func<long[], int, int, int, InventoryScreen> inv = (uids, sel, mode, scroll) => { var i = new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = mode, ArtBulk = mode == 2, ArtSelKnown = true, ArtSel = sel, ListPtr = 7,
      List = new RFX.ListRead { ScrollY = scroll, Cols = 7, RowUnits = 116, First = 0, Last = 27 } }; i.Uids.AddRange(uids); return i; };
    var u3 = new long[] { 5010, 5001, 5002 };
    Eq(LocateKind.NoItem, LocateGuide.NextArt(new LocateRequest { Purpose = "artPromote" }, null, null).Kind, "art guide: an artifact the snapshot lacks");
    Eq(LocateKind.OpenInventory, LocateGuide.NextArt(req, null, null).Kind, "art guide: nothing open -> open the inventory");
    Eq(LocateKind.OpenInventory, LocateGuide.NextArt(req, new InventoryScreen { Open = false }, null).Kind, "art guide: inventory closed -> open it");
    Eq(LocateKind.ArtOpenTab, LocateGuide.NextArt(req, new InventoryScreen { Open = true, EquipTab = true }, null).Kind, "art guide: the gear tab -> «Артефакт»");
    Eq(LocateKind.ArtExitMode, LocateGuide.NextArt(req, inv(u3, 0, 2, 0), null).Kind, "art guide: bulk sale -> leave it");
    Eq(LocateKind.ArtExitMode, LocateGuide.NextArt(req, inv(u3, 0, 1, 0), null).Kind, "art guide: arrange mode -> leave it");
    Eq(LocateKind.ArtListLoading, LocateGuide.NextArt(req, inv(new long[0], 0, -1, 0), null).Kind, "art guide: an empty read is a list not read yet");
    Eq(LocateKind.ArtNotInList, LocateGuide.NextArt(req, inv(new long[] { 5010, 5011 }, 0, -1, 0), null).Kind, "art guide: not in the tab's list (a filter hides it)");
    var stL = LocateGuide.NextArt(req, inv(u3, 1, -1, 0), null);
    Check(stL.Kind == LocateKind.ArtInList && stL.Pos == 2 && stL.Row == 1 && stL.Col == 2 && stL.Scroll == "visible", "art guide: in the list -> its cell (place 2, row 1, column 2)");
    Eq(LocateKind.ArtSelected, LocateGuide.NextArt(req, inv(u3, 2, -1, 0), null).Kind, "art guide: its card is open -> «Мастерская»");
    var far = new long[60]; for (int i = 0; i < 60; i++) far[i] = 6000 + i; far[50] = 5001;
    var stF = LocateGuide.NextArt(req, new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = -1, ArtSelKnown = true, ArtSel = 1, Uids = new List<long>(far), List = new RFX.ListRead { ScrollY = 0, Cols = 7, RowUnits = 116, First = 0, Last = 27 } }, null);
    Check(stF.Kind == LocateKind.ArtInList && stF.Scroll == "below" && stF.ScrollRows >= 1 && stF.Row == 8, "art guide: far down the list -> scroll below (" + stF.Scroll + " " + stF.ScrollRows + ")");

    Func<long, int, int, int, long[], long[], int[], ArtWorkshopScreen> W = (uid, tab, lv, stage, mats, levels, sel) => { var w = new ArtWorkshopScreen { Open = true, Tab = tab, Uid = uid, Level = lv, Stage = stage, PanelKnown = mats != null };
      if (mats != null) { w.Mats.AddRange(mats); foreach (var l in levels) w.MatLevels.Add((int)l); foreach (var x in sel) w.MatSel.Add(x); w.MatList = new RFX.ListRead { ScrollY = 0, Cols = 3, RowUnits = 114, First = 0, Last = 8 }; } return w; };
    var matsA = new long[] { 5003, 5002, 0 }; var lvA = new long[] { 7, 1, -1 };
    Eq(LocateKind.ArtWrongArtifact, LocateGuide.NextArt(req, null, W(9999, 2, 10, 0, matsA, lvA, new int[0])).Kind, "art guide: the workshop holds another artifact");
    var stLv = LocateGuide.NextArt(req, null, W(5001, 1, 8, 0, null, null, null));
    Check(stLv.Kind == LocateKind.ArtLevelFirst && stLv.ArtCap == 10 && stLv.ArtLevel == 8, "art guide: below the cap -> level it first (cap 10, now 8)");
    Eq(LocateKind.ArtOpenPromote, LocateGuide.NextArt(req, null, W(5001, 1, 10, 0, null, null, null)).Kind, "art guide: at the cap on «Улучшить» -> open «Продвинуть»");
    Eq(LocateKind.ArtMatLoading, LocateGuide.NextArt(req, null, W(5001, 2, 10, 0, null, null, null)).Kind, "art guide: the copy list not read yet");
    // need 2: the planned copies are 5002 (place 2) and 5003 (place 1), in that order
    var p1 = LocateGuide.NextArt(req, null, W(5001, 2, 10, 0, matsA, lvA, new int[0]));
    Check(p1.Kind == LocateKind.ArtMatPick && p1.Pos == 2 && p1.MatKind == "copy" && p1.MatNeed == 2 && p1.MatChosen == 0 && !p1.MatWrong && p1.Row == 1 && p1.Col == 2, "art guide: the planned copy first (place 2), 2 needed");
    var p2 = LocateGuide.NextArt(req, null, W(5001, 2, 10, 0, matsA, lvA, new[] { 2 }));
    Check(p2.Kind == LocateKind.ArtMatPick && p2.Pos == 1 && p2.MatChosen == 1, "art guide: then the next planned copy");
    Eq(LocateKind.ArtPromote, LocateGuide.NextArt(req, null, W(5001, 2, 10, 0, matsA, lvA, new[] { 2, 1 })).Kind, "art guide: all chosen -> «Продвинуть» is the player's");
    var pw = LocateGuide.NextArt(req, null, W(5001, 2, 10, 0, matsA, lvA, new[] { 2, 3 }));
    Check(pw.Kind == LocateKind.ArtMatPick && pw.MatWrong && pw.Pos == 3 && pw.MatKind == "essence", "art guide: an essence ticked while copies were planned -> untick it");
    var reqOne = new LocateRequest { Id = "r", Purpose = "artPromote", Art = new ArtPromoteInfo { Uid = 5001, Need = 1, Cap = 10, Via = "copy", Copy = 5002, Copies = { 5002 } } };
    var pl = LocateGuide.NextArt(reqOne, null, W(5001, 2, 10, 0, matsA, lvA, new[] { 1 }));
    Check(pl.Kind == LocateKind.ArtMatPick && pl.MatWrong && pl.Pos == 1, "art guide: a levelled copy ticked instead of the planned one -> untick it");
    var reqEss = new LocateRequest { Id = "r", Purpose = "artPromote", Art = new ArtPromoteInfo { Uid = 5001, Need = 1, Cap = 10, Via = "essence" } };
    var pe = LocateGuide.NextArt(reqEss, null, W(5001, 2, 10, 0, matsA, lvA, new int[0]));
    Check(pe.Kind == LocateKind.ArtMatPick && pe.Pos == 3 && pe.MatKind == "essence", "art guide: the plan spends an essence -> the essence stub");
    var reqNo = new LocateRequest { Id = "r", Purpose = "artPromote", Art = new ArtPromoteInfo { Uid = 5001, Need = 1, Cap = 10, Via = "none" } };
    Eq(LocateKind.ArtMatNone, LocateGuide.NextArt(reqNo, null, W(5001, 2, 10, 0, new long[0], new long[0], new int[0])).Kind, "art guide: no copy and no essence in the list");
    var pAny = LocateGuide.NextArt(reqNo, null, W(5001, 2, 10, 0, matsA, lvA, new int[0]));
    Check(pAny.Kind == LocateKind.ArtMatPick && pAny.Pos == 2, "art guide: no plan -> the first copy that was never levelled");
    Eq("artMatPick", p1.Key, "art guide: page key of the pick step");
    // the form's artifact uid unreadable (0) on the promote tab: the list still decides, the request's artifact is the target
    var pNoUid = LocateGuide.NextArt(req, null, W(0, 2, 10, 0, matsA, lvA, new int[0]));
    Check(pNoUid.Kind == LocateKind.ArtMatPick && pNoUid.Pos == 2, "art guide: workshop uid unreadable but the list read -> the planned copy (" + pNoUid.Key + ")");
    Eq(LocateKind.ArtMatLoading, LocateGuide.NextArt(req, null, W(0, 0, -1, -1, null, null, null)).Kind, "art guide: tab and uid unreadable -> loading");
    // the unreadable workshop: after the wait the list's area is framed (no cell), the plate says to pick the copy / essence
    var wsWait = W(5001, 2, 10, 0, null, null, null);
    var stWait = LocateGuide.NextArt(req, null, wsWait);
    Check(LocateGuide.ArtWsWaiting(stWait, wsWait) && !LocateGuide.ArtWsWaiting(p1, W(5001, 2, 10, 0, matsA, lvA, new int[0])) && !LocateGuide.ArtWsWaiting(stWait, null), "art guide: only an open workshop with no list waits");
    var stGuess = LocateGuide.ArtWsGuess(stWait, wsWait);
    Eq("artMatGuess", stGuess.Key, "art guide: the geometry hint after the wait");
    Eq(LocateKind.ArtOpenPromote, LocateGuide.ArtWsGuess(new LocateStep { Kind = LocateKind.ArtMatLoading }, W(5001, 1, 10, 0, null, null, null)).Kind, "art guide: the geometry hint on another tab asks for «Продвинуть»");
    Eq(LocateKind.ArtMatGuess, LocateGuide.ArtWsGuess(new LocateStep { Kind = LocateKind.ArtMatLoading }, W(0, 0, -1, -1, null, null, null)).Kind, "art guide: tab not read -> the list's area");
    var gfr = ArtFrames.For(stGuess, null, wsWait, null, 1136, 640, Ui.Unit(1136, 640));
    Check(gfr != null && gfr.Mark.Kind == "box" && gfr.Mark.W > 100 && gfr.Mark.H > 100 && gfr.Target == "artifact.matList", "art frames: the guess frames the whole copy list area");
    Eq("artOpenTab", LocateGuide.NextArt(req, new InventoryScreen { Open = true, EquipTab = true }, null).Key, "art guide: page key of the tab step");

    // --- the job is done when the stage rises (over two reads)
    var fin = new ArtFinish();
    Check(!fin.Update(5001, W(5001, 2, 10, 0, matsA, lvA, new[] { 2, 1 }), 0), "art finish: the first reading is the baseline");
    Check(!fin.Update(5001, W(5001, 2, 10, 0, matsA, lvA, new[] { 2, 1 }), 100), "art finish: the same stage is not done");
    Check(!fin.Update(5001, W(5001, 2, 10, 1, matsA, lvA, new int[0]), 200), "art finish: a first read of a higher stage waits for a second");
    Check(!fin.Update(5001, new ArtWorkshopScreen { Open = false }, 300), "art finish: not readable now -> nothing is known");
    Check(fin.Update(5001, W(5001, 2, 10, 1, matsA, lvA, new int[0]), 500), "art finish: the stage rose over two reads -> done");
    var fin2 = new ArtFinish(); fin2.Update(5001, W(5001, 2, 10, 3, matsA, lvA, new int[0]), 0);
    Check(!fin2.Update(5001, W(9999, 2, 10, 5, matsA, lvA, new int[0]), 400) && !fin2.Update(5001, W(5001, 2, 10, 3, matsA, lvA, new int[0]), 800), "art finish: another artifact's stage does not count, a stale site stage is no baseline");

    // --- the frames: the research numbers stand in when the site's file lacks the keys; the cells follow the game's scroll
    double Wd = 1136, Hd = 640, U = Ui.Unit(Wd, Hd);
    var fTab = ArtFrames.For(LocateGuide.NextArt(req, new InventoryScreen { Open = true, EquipTab = true }, null), null, null, null, Wd, Hd, U);
    Check(fTab != null && Math.Abs(fTab.Mark.Y - 257.4) <= 1.5 && Math.Abs(fTab.Mark.X - 1.5) <= 1.5, "art frames: the «Артефакт» toggle (research numbers)");
    var iv = inv(u3, 1, -1, 0);
    var fCell = ArtFrames.For(stL, iv, null, null, Wd, Hd, U);
    double cellW = (1136 - (0.5231 + 0.195) * 640) / 7;
    Check(fCell != null && fCell.Mark.Kind == "box" && Math.Abs(fCell.Mark.X - (124.8 + cellW + (cellW - 80) / 2)) <= 1.5 && Math.Abs(fCell.Mark.Y - 126) <= 1.5 && Math.Abs(fCell.Mark.W - 80) <= 1.5 && Math.Abs(fCell.Mark.H - 110) <= 1.5,
          "art frames: the cell of place 2 is the 80 x 110 card, centred in its column (" + (fCell == null ? "none" : fCell.Mark.X + "," + fCell.Mark.Y + " " + fCell.Mark.W + "x" + fCell.Mark.H) + ")");
    var ivS = inv(u3, 1, -1, 3);
    var fS = ArtFrames.For(stL, ivS, null, null, Wd, Hd, U);
    Check(fS != null && Math.Abs((fCell.Mark.Y - fS.Mark.Y) - 3) <= 1.01 && !fCell.Clickable, "art frames: the frame moves with ScrollY exactly, and is never clickable");
    var stFar = LocateGuide.NextArt(req, new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = -1, Uids = new List<long>(far), List = new RFX.ListRead { ScrollY = 0, Cols = 7, RowUnits = 116 } }, null);
    var fFar = ArtFrames.For(stFar, new InventoryScreen { List = new RFX.ListRead { ScrollY = 0, Cols = 7, RowUnits = 116 } }, null, null, Wd, Hd, U);
    Check(fFar != null && fFar.Mark.Kind == "down" && fFar.Mark.Rows >= 1, "art frames: a cell below the list -> the pill at its edge");
    Check(ArtFrames.For(stL, new InventoryScreen { List = null }, null, null, Wd, Hd, U) == null, "art frames: the list's scroll unreadable -> no cell frame (the plate stays)");
    var fWs = ArtFrames.For(LocateGuide.NextArt(req, iv = inv(u3, 2, -1, 0), null), iv, null, null, Wd, Hd, U);
    Check(fWs != null && Math.Abs(fWs.Mark.X - 972) <= 1.5 && Math.Abs(fWs.Mark.Y - 574.1) <= 1.5, "art frames: «Мастерская» (972, 574)");
    var fPt = ArtFrames.For(LocateGuide.NextArt(req, null, W(5001, 1, 10, 0, null, null, null)), null, W(5001, 1, 10, 0, null, null, null), null, Wd, Hd, U);
    Check(fPt != null && Math.Abs(fPt.Mark.X - 1030.6) <= 2 && Math.Abs(fPt.Mark.Y - 80) <= 1.5, "art frames: the «Продвинуть» tab (right strip, second)");
    var wsP = W(5001, 2, 10, 0, matsA, lvA, new[] { 2, 1 });
    var fBt = ArtFrames.For(LocateGuide.NextArt(req, null, wsP), null, wsP, null, Wd, Hd, U);
    Check(fBt != null && Math.Abs(fBt.Mark.X - 369) <= 1.5 && Math.Abs(fBt.Mark.W - 231) <= 1.5, "art frames: «Продвинуть» (369..600)");
    var wsM = W(5001, 2, 10, 0, matsA, lvA, new int[0]);
    var fMat = ArtFrames.For(LocateGuide.NextArt(req, null, wsM), null, wsM, null, Wd, Hd, U);
    double slotW = 245.0 / 3;
    Check(fMat != null && fMat.Mark.Kind == "box" && Math.Abs(fMat.Mark.X - (18.5 + slotW + (slotW - 80) / 2)) <= 1.5 && Math.Abs(fMat.Mark.Y - (73 + 2)) <= 1.5, "art frames: place 2 of the copy list: column 2, row 1 (" + (fMat == null ? "none" : fMat.Mark.X + "," + fMat.Mark.Y) + ")");
    string webGeo = Path.Combine(WebRoot(), "data", "app-geometry", "1.6.39.829.1.json");
    if (File.Exists(webGeo)) {
      var rg = GameGeometry.Parse(File.ReadAllText(webGeo, Encoding.UTF8), "1.6.39.829.1");
      Check(rg != null && rg.Has("inventory.artWorkshop") && rg.Has("artifact.tab.promote") && rg.Has("artifact.promoteBtn") && rg.Has("artifact.matList") && rg.Grid("artifact.matList") != null && rg.Grid("artifact.matList").Cols == 3 && Math.Abs(rg.Grid("artifact.matList").LineSize - 114) < 0.01,
            "art frames: the site's geometry file has the workshop button, the promote tab and button, the 3 x 114 copy list");
      if (rg != null) {
        var a1 = ArtFrames.Rect(rg, "inventory.artWorkshop", Wd, Hd, U); var a2 = ArtFrames.Rect(null, "inventory.artWorkshop", Wd, Hd, U);
        var b1 = ArtFrames.Rect(rg, "artifact.promoteBtn", Wd, Hd, U); var b2 = ArtFrames.Rect(null, "artifact.promoteBtn", Wd, Hd, U);
        var c1 = ArtFrames.Rect(rg, "artifact.matList", Wd, Hd, U); var c2 = ArtFrames.Rect(null, "artifact.matList", Wd, Hd, U);
        Check(Math.Abs(a1.X - a2.X) <= 1 && Math.Abs(a1.Y - a2.Y) <= 1 && Math.Abs(b1.X - b2.X) <= 1 && Math.Abs(b1.W - b2.W) <= 1 && Math.Abs(c1.X - c2.X) <= 1 && Math.Abs(c1.H - c2.H) <= 1, "art frames: the file and the research numbers agree");
      }
    }

    // --- no clicks, whatever the auto click says
    {
      var gate = new ClickGate(); gate.NoLocateClicks = true;
      var res = gate.Check(new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.InvEquipTab, AutoOn = true, Front = true }, scrI, 0);
      Check(!res.Allowed && gate.HaltReason(ClickOwner.Locate) == "artifact", "art gate: the Locate owner never clicks while an artifact request runs (" + res.Reason + ")");
      gate = new ClickGate();
      res = gate.Check(new ClickRequest { Owner = ClickOwner.Locate, Target = ClickTarget.InvEquipTab, AutoOn = true, Front = true }, scrI, 0);
      Check(gate.HaltReason(ClickOwner.Locate) != "artifact", "art gate: a normal request is not stopped by the artifact rule");
      Check(!ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtTab") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtBulk") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtCell") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtWorkshop") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtMat") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtPromote") && !ClickGate.WhitelistText(ClickOwner.Equip).Contains("ArtListDrag"), "art gate: the equip helper has none of the inventory / workshop artifact targets on its whitelist (only its own hero-screen ones, see ArtEquipTests; the others have theirs, behind ArtSellAuto / ArtLocateAuto)");
    }
  }

  // The artifact flows with auto click (the site's artAuto switch): the gate's whitelist checks, the switch gating, the pilots' decisions
  // «Надеть в игре» with an artifact: the plan's artifact, the hero screen's artifact fields on fake tables, the steps, the frames, the pilot and the gate (src/ArtEquip.cs)
  static void ArtEquipTests() {
    Console.WriteLine("Equip plan with an artifact (plan reader, hero screen fields, steps, frames, pilot, gate)");
    // --- the plan: the artifact rides on the reply (the site sends it only to an app that asked with art=1)
    string pj = "{\"ok\":true,\"plans\":[{\"id\":\"p1\",\"heroUid\":214700000,\"heroName\":\"Hero\",\"createdAt\":\"2026-10-17T10:00:00Z\",\"items\":[{\"slot\":0,\"uid\":11,\"name\":\"Sword\"}]ART}]}";
    var pr = PlansClient.Interpret(200, pj.Replace("ART", ",\"artifact\":{\"uid\":9005,\"id\":3012}"), true);
    Check(pr.Status == PlansStatus.Ok && pr.Plans.Count == 1 && pr.Plans[0].Artifact != null && pr.Plans[0].Artifact.Uid == 9005 && pr.Plans[0].Artifact.Id == 3012 && pr.Plans[0].Artifact.Name == "", "art plan: {uid, id} of the artifact is read");
    pr = PlansClient.Interpret(200, pj.Replace("ART", ",\"artifact\":{\"uid\":9005,\"id\":3012,\"name\":\"Ring\"}"), true);
    Check(pr.Plans[0].Artifact != null && pr.Plans[0].Artifact.Name == "Ring", "art plan: an optional name is kept");
    pr = PlansClient.Interpret(200, pj.Replace("ART", ""), true);
    Check(pr.Plans.Count == 1 && pr.Plans[0].Artifact == null, "art plan: no artifact -> none (a plan of an older site)");
    foreach (var bad in new[] { ",\"artifact\":{\"uid\":0,\"id\":1}", ",\"artifact\":{\"uid\":\"x\",\"id\":1}", ",\"artifact\":7", ",\"artifact\":null", ",\"artifact\":{\"uid\":-5,\"id\":1}" }) {
      pr = PlansClient.Interpret(200, pj.Replace("ART", bad), true);
      Check(pr.Plans.Count == 1 && pr.Plans[0].Artifact == null, "art plan: an odd artifact " + bad + " is ignored, the gear plan stays");
    }
    string src = File.ReadAllText(Path.Combine("src", "PlansClient.cs"));
    Check(src.Contains("&art=1"), "art plan: the app asks the site with art=1");

    // --- the hero screen's artifact fields on fake script tables
    var m = new FakeLua(1 << 20); RFX.TestMemory = m; RFX.ForgetLive(); RFX.ArtScanMs = 0;
    const long hero = 214700000, other = 200100000;
    try {
      var st = new FakeState(m);
      Func<long, long, FTab> art = (uid, owner) => st.T("iItemUid", uid, "iItemId", 3000L + uid % 7, "iHeroId", owner);
      Func<long[], long[], FTab> artsOf = (uids, owners) => {
        var kv = new List<object>(); for (int i = 0; i < uids.Length; i++) { kv.Add(uids[i]); kv.Add(art(uids[i], owners[i])); }
        return st.T("m_vArtifacts", st.T(kv.ToArray()), "class", st.T("__cname", "ArtifactData"));
      };
      var arts = artsOf(new long[] { 9001, 9002, 9003, 9004 }, new long[] { 0, other, hero, 0 });
      var artsNone = artsOf(new long[] { 9001, 9002, 9004 }, new long[] { 0, other, 0 });
      Func<object, object, FTab, FTab> form = (open, sel, rows) => {
        var pd = new List<object> { 3L, 5L };
        if (open != null) { pd.Add(10L); pd.Add(open); }
        if (sel != null) { pd.Add(11L); pd.Add(sel); }
        return st.T("m_PanelDatas", st.T(pd.ToArray()), "m_Panels", st.T("EquipArtifact", st.T("m_InfinityGridProxy", st.T("m_Data", rows))));
      };
      var rows = st.T(1L, art(9004, 0), 2L, art(9002, other), 3L, art(9001, 0), 4L, art(9003, hero));   // the list in screen order
      var h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(true, null, rows).A, arts.A, hero, 9002, h, null);
      Check(h.ArtRead && h.ArtListOpen && h.ArtSel == 0, "art fields: [10] true = the list is open, [11] nil = nothing selected");
      Eq("9004,9002,9001,9003", string.Join(",", h.ArtUids), "art fields: the list in screen order (m_Data)");
      Check(h.ArtTargetOwner == other && h.ArtWorn == 9003, "art fields: the goal is worn by another hero (" + h.ArtTargetOwner + "), the hero wears 9003 (" + h.ArtWorn + ")");
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(true, 9002L, rows).A, arts.A, hero, 9002, h, null);
      Check(h.ArtSel == 9002, "art fields: [11] = the selected artifact's uid");
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(true, 9002.0, rows).A, arts.A, hero, 9002, h, null);
      Check(h.ArtSel == 9002, "art fields: a whole float uid is read");
      // the list closed: nothing of the list is read, the owner comes from the account's table
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(null, null, rows).A, arts.A, hero, 9002, h, null);
      Check(h.ArtRead && !h.ArtListOpen && h.ArtUids.Count == 0 && h.ArtTargetOwner == other && h.ArtWorn == 9003, "art fields: list closed -> no rows; owner and worn artifact from m_vArtifacts");
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(false, null, rows).A, arts.A, hero, 9002, h, null);
      Check(!h.ArtListOpen, "art fields: [10] false = closed");
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(null, null, rows).A, artsNone.A, hero, 9002, h, null);
      Check(h.ArtRead && h.ArtWorn == 0 && h.ArtTargetOwner == other, "art fields: the hero wears nothing -> worn 0 (the empty slot)");
      // the goal worn by the hero: the owner is the hero (the step machine skips it)
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(null, null, rows).A, arts.A, hero, 9003, h, null);
      Check(h.ArtTargetOwner == hero && h.ArtWorn == 9003, "art fields: the goal on the hero -> owner = hero");
      // an artifact the account does not hold: unreadable, not 'free'
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(null, null, rows).A, arts.A, hero, 7777, h, null);
      Check(!h.ArtRead && h.ArtTargetOwner == -1, "art fields: the goal is not in the account's table -> not read");
      // live 2026-10-18: the server leaves iHeroId out of a FREE artifact (the script reads the default 0): that is "free", not "unreadable"
      var freeArt = st.T("iItemUid", 9010L, "iItemId", 3001L);
      var artsFree = st.T("m_vArtifacts", st.T(1L, art(9001, 0), 2L, freeArt, 3L, art(9003, hero)), "class", st.T("__cname", "ArtifactData"));   // an array, as the game keeps it
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 3 };
      RFX.ReadHeroArt(form(null, null, rows).A, artsFree.A, hero, 9010, h, null);
      Check(h.ArtRead && h.ArtTargetOwner == 0, "art fields: an artifact with no iHeroId key is free (owner 0), the data is read (" + h.ArtTargetOwner + ")");
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(null, null, rows).A, artsFree.A, hero, 9010, h, null);
      Check(h.ArtRead && h.ArtTargetOwner == 0 && h.ArtWorn == 9003, "art fields: the free artifact is found by the scan and the hero's worn artifact too");
      var rowsFree = st.T(1L, art(9004, 0), 2L, freeArt);
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(true, null, rowsFree).A, artsFree.A, hero, 9010, h, null);
      Check(h.ArtRead && h.ArtTargetOwner == 0 && h.ArtUids.Count == 2, "art fields: a free goal's list row without iHeroId is owner 0");
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 3 };
      RFX.ReadHeroArt(form(null, null, rows).A, arts.A, hero, 9002, h, null);
      Check(h.ArtRead && h.ArtWorn == -1 && h.ArtTargetOwner == other, "art fields: on the gear tab the worn artifact is not looked for");
      // a later read with the list open carries the worn artifact over and keeps the rows while the goal's row holds
      var prev = new HeroScreen { Hero = hero, FormOk = true, Tab = 7, ArtWorn = 9003 };
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(true, null, rows).A, arts.A, hero, 9002, h, prev);
      Check(h.ArtWorn == 9003, "art fields: the worn artifact is carried over while the list is open");
      var prevRows = new HeroScreen(); prevRows.ArtListPtr = h.ArtListPtr; prevRows.ArtRows = h.ArtRows; prevRows.ArtUids.AddRange(h.ArtUids);
      var h2 = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(true, null, rows).A, arts.A, hero, 9002, h2, prevRows);
      Eq("9004,9002,9001,9003", string.Join(",", h2.ArtUids), "art fields: the same table and the goal's row verified -> the rows are reused");
      var stale = new HeroScreen(); stale.ArtListPtr = prevRows.ArtListPtr; stale.ArtRows = prevRows.ArtRows; stale.ArtUids.AddRange(new long[] { 9002, 9004, 9001, 9003 });   // the goal moved
      // a cache of the SAME table object is refused when the goal's row differs (the list was sorted)
      stale.ArtListPtr = h.ArtListPtr;
      var h4 = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(true, null, rows).A, arts.A, hero, 9002, h4, stale);
      Eq("9004,9002,9001,9003", string.Join(",", h4.ArtUids), "art fields: the goal moved in the list -> the rows are read again");
      // uids written as text (the game compares them with tostring): the account's entries and the selected uid
      Func<string, long, FTab> artT = (uid, owner) => st.T("iItemUid", uid, "iItemId", 3001L, "iHeroId", owner);
      var artsText = st.T("m_vArtifacts", st.T(1L, artT("9001", 0), 2L, artT("9002", other), 3L, artT("9004", 0)));
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(null, null, rows).A, artsText.A, hero, 9002, h, null);
      Check(h.ArtRead && h.ArtTargetOwner == other, "art fields: entries with a text iItemUid are found by the scan");
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(form(true, "9004", rows).A, arts.A, hero, 9004, h, null);
      Check(h.ArtSel == 9004, "art fields: the selected uid written as text is read");
      // the list's rows are not in the grid proxy's m_Data: an array of artifact entries in another field of the panel is found
      var panelAlt = st.T("m_Other", st.T("m_Output", rows), "m_InfinityGridProxy", st.T("m_Data", st.T()));
      var formAlt = st.T("m_PanelDatas", st.T(3L, 5L, 10L, true), "m_Panels", st.T("EquipArtifact", panelAlt));
      h = new HeroScreen { Hero = hero, FormOk = true, Tab = 7 };
      RFX.ReadHeroArt(formAlt.A, arts.A, hero, 9002, h, null);
      Eq("9004,9002,9001,9003", string.Join(",", h.ArtUids), "art fields: the rows are found in another table field when the proxy's m_Data is empty");
      Check(h.ArtTargetOwner == other, "art fields: and the goal's owner is read from them");
      // the structure dump: field names with value types, no values
      string keys = RFX.KeysOf(art(9002, other).A, 20);
      Check(keys.Contains("iItemUid:int") && keys.Contains("iHeroId:int") && !keys.Contains("9002"), "art dump: keys with Lua types, no values (" + keys + ")");
      long tv; Check(RFX.LuaIntText(" 1284195435405 ", out tv) && tv == 1284195435405L && !RFX.LuaIntText("x1", out tv) && !RFX.LuaIntText("", out tv), "art fields: a uid written as text is a number");
    } finally { RFX.TestMemory = null; RFX.ForgetLive(); RFX.ArtScanMs = 800; }

    // --- the steps
    var list = new RFX.ListRead { ScrollY = 0, Cols = 3, RowUnits = 116 };
    Func<int, bool, long, long, long, long[], HeroScreen> HS = (tab, open, sel, owner, worn, uids) => {
      var x = new HeroScreen { Hero = hero, FormOk = true, Tab = tab, ArtRead = true, ArtListOpen = open, ArtSel = sel, ArtTargetOwner = owner, ArtWorn = worn, ArtList = list };
      if (uids != null) x.ArtUids.AddRange(uids);
      return x;
    };
    var u5 = new long[] { 9001, 9002, 9003, 9004, 9005 };
    Func<HeroScreen, bool, ArtEquipStep> N = (hs, popup) => ArtEquipGuide.Next(hero, 9004, hs, popup);
    Check(N(null, false).Kind == ArtEquipKind.Hero && N(new HeroScreen { FormOk = false, Hero = hero }, false).Kind == ArtEquipKind.Hero, "art steps: no hero screen -> open the hero");
    Check(N(new HeroScreen { FormOk = true, Hero = 5 }, false).Kind == ArtEquipKind.Hero, "art steps: another hero is open -> open the hero");
    Check(N(HS(3, false, 0, hero, 9004, null), false).Kind == ArtEquipKind.Done && N(HS(7, true, 0, hero, 9004, u5), false).Kind == ArtEquipKind.Done, "art steps: the artifact is on the hero -> done, whatever the tab (skipped when it was there before)");
    Check(N(new HeroScreen { Hero = hero, FormOk = true, Tab = 3, ArtRead = false }, false).Kind == ArtEquipKind.Wait, "art steps: the artifact data not read -> wait");
    Check(N(HS(3, false, 0, 0, 9001, null), false).Kind == ArtEquipKind.ArtTab, "art steps: the gear tab -> the «Артефакт» tab");
    var s1 = N(HS(7, false, 0, 0, 9001, null), false);
    Check(s1.Kind == ArtEquipKind.OpenList && s1.HasWorn && s1.Name == "open", "art steps: the tab is on, the hero wears one -> the worn card's button");
    var s2 = N(HS(7, false, 0, 0, 0, null), false);
    Check(s2.Kind == ArtEquipKind.OpenList && !s2.HasWorn && s2.Name == "openAdd", "art steps: an empty slot -> the «+»");
    Check(N(HS(7, false, 0, 0, -1, null), false).Kind == ArtEquipKind.Wait, "art steps: the worn artifact unknown -> wait (no guess between the two buttons)");
    var s3 = N(HS(7, true, 0, other, 9001, u5), false);
    Check(s3.Kind == ArtEquipKind.PickCell && s3.Pos == 4 && s3.Key == "PickCell:4", "art steps: the list is open -> the artifact's cell, place 4");
    Check(N(HS(7, true, 9001, other, 9001, u5), false).Kind == ArtEquipKind.PickCell, "art steps: another artifact selected -> pick the right one");
    var s4 = N(HS(7, true, 9004, other, 9001, u5), false);
    Check(s4.Kind == ArtEquipKind.PressReplace && s4.HasWorn && s4.Name == "replace", "art steps: its card is open -> «Заменить»");
    Check(N(HS(7, true, 9004, other, 0, u5), false).Name == "equip", "art steps: the hero wears none -> «Надеть»");
    Check(N(HS(7, true, 9004, other, 9001, u5), true).Kind == ArtEquipKind.ConfirmTakeOver, "art steps: the pop-up with the artifact selected -> its «Заменить»");
    Check(N(HS(7, true, 9001, other, 9001, u5), true).Kind == ArtEquipKind.Wait, "art steps: a pop-up with another artifact selected -> wait (not ours)");
    Check(N(HS(7, true, 0, other, 9001, new long[] { 9001, 9002 }), false).Kind == ArtEquipKind.NotInList, "art steps: the list has no such artifact -> the player's filter");
    Check(N(HS(7, true, 0, other, 9001, new long[0]), false).Kind == ArtEquipKind.Wait, "art steps: the list is still empty -> wait (it fills after the panel slides in)");

    // --- unreadable artifact data: after the wait the screen's geometry gives the hint, never a click
    var wt = N(new HeroScreen { Hero = hero, FormOk = true, Tab = 3, ArtRead = false }, false);
    Check(ArtEquipFallback.Waiting(wt) && !ArtEquipFallback.Waiting(N(HS(7, true, 9001, other, 9001, u5), true)), "art guess: only data waits are guessed (a foreign pop-up is not)");
    var g1 = ArtEquipFallback.Guess(9004, new HeroScreen { Hero = hero, FormOk = true, Tab = 3, ArtRead = false, ArtWorn = -1 }, wt);
    Check(g1.Guess && g1.Kind == ArtEquipKind.ArtTab && g1.Name == "tab" && g1.Key.StartsWith("guess:"), "art guess: another tab -> the «Артефакт» tab");
    var g2 = ArtEquipFallback.Guess(9004, new HeroScreen { Hero = hero, FormOk = true, Tab = 7, ArtRead = false, ArtWorn = -1 }, wt);
    Check(g2.Kind == ArtEquipKind.OpenList && g2.HasWorn && g2.Name == "open", "art guess: the tab is on, list closed -> the worn card's button");
    var g3 = ArtEquipFallback.Guess(9004, new HeroScreen { Hero = hero, FormOk = true, Tab = 7, ArtListOpen = true, ArtSel = 0 }, wt);
    Check(g3.Kind == ArtEquipKind.PickCell && g3.Pos == 0 && ArtEquipFrames.For(g3, new HeroScreen(), null, 1136, 640, Ui.Unit(1136, 640)) != null && ArtEquipFrames.For(g3, new HeroScreen(), null, 1136, 640, Ui.Unit(1136, 640)).Target == ArtEquipFrames.KeyList, "art guess: the list is open -> pick it, the list's area is framed for a cell it cannot place");
    var g4 = ArtEquipFallback.Guess(9004, new HeroScreen { Hero = hero, FormOk = true, Tab = 7, ArtListOpen = true, ArtSel = 9004 }, wt);
    Check(g4.Kind == ArtEquipKind.PressReplace, "art guess: its card is selected -> «Заменить»");
    var gf = ArtEquipFrames.For(g1, null, null, 1136, 640, Ui.Unit(1136, 640));
    Check(gf != null && gf.Mark.Kind == "box", "art guess: the tab step has its frame");
    string gwhy;
    Check(ArtEquipPilot.Decide(new ArtEquipView { Step = g1, Hero = new HeroScreen { Hero = hero, FormOk = true } }, out gwhy) == ArtEquipTodo.Hint && gwhy == "guess", "art guess: the pilot never clicks on a guess");
    Check(ArtEquipFallback.Guess(9004, null, wt).Kind == ArtEquipKind.Wait, "art guess: no hero screen -> still waiting");

    // --- the frames: the built-in numbers (no geometry) and the site's file
    double Wd = 1136, Hd = 640, U = Ui.Unit(Wd, Hd);
    var tabF = ArtEquipFrames.For(N(HS(3, false, 0, 0, 9001, null), false), null, null, Wd, Hd, U);
    Check(tabF != null && Math.Abs(tabF.Mark.X - 1030) <= 1.5 && Math.Abs(tabF.Mark.Y - 478) <= 1.5 && Math.Abs(tabF.Mark.W - 106) <= 1.5 && Math.Abs(tabF.Mark.H - 80) <= 1.5, "art frames: the «Артефакт» tab 1030..1136 x 478..558");
    var openF = ArtEquipFrames.For(s1, null, null, Wd, Hd, U); var addF = ArtEquipFrames.For(s2, null, null, Wd, Hd, U);
    Check(openF != null && Math.Abs(openF.Mark.X - 947) <= 1.5 && Math.Abs(openF.Mark.Y - 126) <= 1.5 && Math.Abs(openF.Mark.W - 43) <= 1.5, "art frames: the worn card's swap icon 947..990 x 126..168 (the art inside it matches the live recording 1920x1009: x 1629..1687, y 202..262)");
    { var of2 = ArtEquipFrames.For(s1, null, null, 1920, 1009, Ui.Unit(1920, 1009)); Check(of2 != null && Math.Abs(of2.Mark.X + of2.Mark.W / 2 - 1658) <= 4 && Math.Abs(of2.Mark.Y + of2.Mark.H / 2 - 232) <= 4, "art frames: at 1920x1009 the swap icon is where the live screen has it (1658,232)"); }
    Check(addF != null && Math.Abs(addF.Mark.X - 814) <= 1.5 && Math.Abs(addF.Mark.Y - 64) <= 1.5 && Math.Abs(addF.Mark.W - 78) <= 1.5 && Math.Abs(addF.Mark.H - 104) <= 1.5, "art frames: the empty slot's «+» 814..892 x 64..169 (beside the shown panel's right edge 1011)");
    var repF = ArtEquipFrames.For(s4, null, null, Wd, Hd, U);
    Check(repF != null && Math.Abs(repF.Mark.X - 450) <= 1.5 && Math.Abs(repF.Mark.Y - 549.6) <= 1.5 && Math.Abs(repF.Mark.W - 142) <= 1.5 && Math.Abs(repF.Mark.H - 53) <= 1.5, "art frames: the card's «Заменить» 450..592 x 549.6..602.6");
    var popF = ArtEquipFrames.For(N(HS(7, true, 9004, other, 9001, u5), true), null, null, Wd, Hd, U);
    Check(popF != null && Math.Abs(popF.Mark.X - 570) <= 1.5 && Math.Abs(popF.Mark.Y - 356) <= 1.5 && Math.Abs(popF.Mark.W - 231) <= 1.5 && Math.Abs(popF.Mark.H - 76) <= 1.5, "art frames: the pop-up's «Заменить» 570..801 x 356..432");
    var cellF = ArtEquipFrames.For(s3, HS(7, true, 0, other, 9001, u5), null, Wd, Hd, U);
    double slotW = 245.0 / 3;   // the list's viewport 18.5..263.5
    Check(cellF != null && cellF.Mark.Kind == "box" && Math.Abs(cellF.Mark.X - (18.5 + (slotW - 80) / 2)) <= 1.5 && Math.Abs(cellF.Mark.Y - (103 + 116 + 3)) <= 1.5, "art frames: place 4 is column 1 of row 2");
    var cell1 = ArtEquipFrames.For(new ArtEquipStep { Kind = ArtEquipKind.PickCell, Pos = 1 }, HS(7, true, 0, other, 9001, u5), null, Wd, Hd, U);
    var cell5 = ArtEquipFrames.For(new ArtEquipStep { Kind = ArtEquipKind.PickCell, Pos = 5 }, HS(7, true, 0, other, 9001, u5), null, Wd, Hd, U);
    Check(cell1 != null && cell5 != null && cell1.Mark.Kind == "box" && Math.Abs(cell1.Mark.X - (18.5 + (slotW - 80) / 2)) <= 1.5 && Math.Abs(cell1.Mark.Y - (103 + 3)) <= 1.5, "art frames: place 1 is column 1, row 1 of the list viewport");
    Check(cell5 != null && Math.Abs(cell5.Mark.X - (18.5 + slotW + (slotW - 80) / 2)) <= 1.5 && Math.Abs(cell5.Mark.Y - (103 + 116 + 3)) <= 1.5, "art frames: place 5 is column 2, row 2 (3 columns, 116 per row)");
    var scrolled = HS(7, true, 0, other, 9001, u5); scrolled.ArtList = new RFX.ListRead { ScrollY = 116, Cols = 3, RowUnits = 116 };
    var cellS = ArtEquipFrames.For(new ArtEquipStep { Kind = ArtEquipKind.PickCell, Pos = 5 }, scrolled, null, Wd, Hd, U);
    Check(cellS != null && Math.Abs((cell5.Mark.Y - cellS.Mark.Y) - 116) <= 1.01, "art frames: the cell follows the list's scroll exactly");
    var far = new long[60]; for (int i = 0; i < far.Length; i++) far[i] = 8000 + i;
    var farH = HS(7, true, 0, other, 9001, far);
    var farF = ArtEquipFrames.For(new ArtEquipStep { Kind = ArtEquipKind.PickCell, Pos = 55 }, farH, null, Wd, Hd, U);
    Check(farF != null && farF.Mark.Kind == "down" && farF.Mark.Rows >= 1 && farF.ScrollPx < 0, "art frames: a cell far below -> the pill at the list's edge, the list is to be dragged up");
    var noScroll = HS(7, true, 0, other, 9001, u5); noScroll.ArtList = null;
    Check(ArtEquipFrames.For(s3, noScroll, null, Wd, Hd, U) == null, "art frames: the list's scroll unreadable -> no cell frame (the plate stays)");
    string webGeo = Path.Combine(WebRoot(), "data", "app-geometry", "1.6.39.829.1.json");
    if (File.Exists(webGeo)) {
      var rg = GameGeometry.Parse(File.ReadAllText(webGeo, Encoding.UTF8), "1.6.39.829.1");
      Check(rg != null && rg.Has("hero.artifactTab") && rg.Has("hero.artOpenList") && rg.Has("hero.artAdd") && rg.Has("hero.artList") && rg.Has("hero.artReplace") && rg.Has("popup.equipReplace") && rg.Has("hero.artSell") && rg.Has("hero.artQuickEquip"), "art frames: the site's geometry file has every key");
      if (rg != null) {
        Check(rg.Grid("hero.artList") != null && rg.Grid("hero.artList").Cols == 3 && Math.Abs(rg.Grid("hero.artList").LineSize - 116) < 0.01, "art frames: the file's list grid is 3 x 116");
        foreach (var kv in new[] { ArtEquipFrames.KeyTab, ArtEquipFrames.KeyOpenList, ArtEquipFrames.KeyAdd, ArtEquipFrames.KeyList, ArtEquipFrames.KeyReplace, ArtEquipFrames.KeyPopup, ArtEquipFrames.KeySell, ArtEquipFrames.KeyQuick }) {
          var a = rg.Rect(kv, Wd, Hd, U); var b = ArtFrames.Rect(null, kv, Wd, Hd, U);
          Check(a != null && b != null && Math.Abs(a.X - b.X) <= 0.6 && Math.Abs(a.Y - b.Y) <= 0.6 && Math.Abs(a.W - b.W) <= 0.6 && Math.Abs(a.H - b.H) <= 0.6, "art frames: " + kv + " in the file = the built-in numbers");
        }
        var fr2 = ArtEquipFrames.For(s4, null, rg, Wd, Hd, U);
        Check(fr2 != null && Math.Abs(fr2.Mark.X - repF.Mark.X) <= 1, "art frames: «Заменить» from the file");
      }
    }
    // the buttons never pressed
    Check(ArtEquipFrames.OnNeverButton(null, 596, 188, Wd, Hd, U) && ArtEquipFrames.OnNeverButton(null, 780, 600, Wd, Hd, U), "art never: the card's sell icon and «Быстро надеть» are never pressed");
    Check(!ArtEquipFrames.OnNeverButton(null, 521, 576, Wd, Hd, U) && !ArtEquipFrames.OnNeverButton(null, 969, 147, Wd, Hd, U) && !ArtEquipFrames.OnNeverButton(null, 1083, 518, Wd, Hd, U), "art never: «Заменить», the list button and the tab are not on them");
    foreach (var step in new[] { s1, s2, s4, N(HS(7, true, 9004, other, 9001, u5), true), N(HS(3, false, 0, 0, 9001, null), false) }) {
      var fr = ArtEquipFrames.For(step, null, null, Wd, Hd, U);
      Check(fr != null && !ArtEquipFrames.OnNeverButton(null, fr.Mark.X + fr.Mark.W / 2.0, fr.Mark.Y + fr.Mark.H / 2.0, Wd, Hd, U), "art never: the centre of the " + step.Kind + " frame is not on a never-pressed button");
    }

    // --- Done is final for the goal (the page re-sends the "art" hint while the plan stays open)
    var latch = new ArtGoalLatch();
    Check(!latch.Ignores(231, 9004), "art latch: before Done the goal is taken");
    latch.MarkDone(231, 9004);
    Check(latch.Ignores(231, 9004) && latch.Ignores(0, 0) && latch.Ignores(231, 9004), "art latch: after Done the same goal and its clearing are ignored");
    Check(!latch.Ignores(231, 9005) && !latch.IsDone, "art latch: another artifact opens it");
    Check(ArtGoalLatch.LiveShown(false, true) && ArtGoalLatch.LiveShown(true, false) && !ArtGoalLatch.LiveShown(false, false), "art latch: the live step shows while the goal runs and after it is latched, whatever the hint (art or done)");
    latch.MarkDone(231, 9004); latch.NewPlan();
    Check(!latch.Ignores(231, 9004), "art latch: a new plan takes the same artifact again");

    // --- the pilot
    Func<ArtEquipStep, HeroScreen, bool, bool, long, ArtEquipView> V = (step, hs, autoConfirm, btnSeen, now) => new ArtEquipView { NowMs = now, Foreground = true, W = Wd, H = Hd, U = U, Step = step, Hero = hs, AutoConfirm = autoConfirm, ReplaceBtnSeen = btnSeen };
    var hTab = HS(3, false, 0, 0, 9001, null);
    var p1 = new ArtEquipPilot(); var m1 = p1.Step(V(N(hTab, false), hTab, false, false, 0));
    Check(m1.State == "work" && m1.Todo == ArtEquipTodo.Tab && m1.Action.Kind == AutoKind.Click && Math.Abs(m1.Action.X - 1083) <= 1.5 && Math.Abs(m1.Action.Y - 518) <= 1.5, "art pilot: the tab -> a click on the tab's centre");
    Check(p1.Step(V(N(hTab, false), hTab, false, false, 100)).Action.Kind == AutoKind.None, "art pilot: a second click waits for the first one's effect");
    var hOpen = HS(7, false, 0, 0, 9001, null);
    var p2 = new ArtEquipPilot(); var m2 = p2.Step(V(N(hOpen, false), hOpen, false, false, 0));
    Check(m2.Todo == ArtEquipTodo.OpenList && Math.Abs(m2.Action.X - 969.4) <= 1.5 && Math.Abs(m2.Action.Y - 147.6) <= 1.5, "art pilot: the worn card's swap icon (centre 969.4, 147.6)");
    var hEmpty = HS(7, false, 0, 0, 0, null);
    var p2b = new ArtEquipPilot(); var m2b = p2b.Step(V(N(hEmpty, false), hEmpty, false, false, 0));
    Check(m2b.Todo == ArtEquipTodo.OpenList && Math.Abs(m2b.Action.X - 853) <= 1.5 && Math.Abs(m2b.Action.Y - 116.5) <= 1.5, "art pilot: the empty slot's «+» (centre 853, 116.5)");
    var hPick = HS(7, true, 0, other, 9001, u5);
    var p3 = new ArtEquipPilot(); var m3 = p3.Step(V(N(hPick, false), hPick, false, false, 0));
    var f4 = ArtEquipFrames.For(N(hPick, false), hPick, null, Wd, Hd, U);
    Check(m3.Todo == ArtEquipTodo.Cell && m3.Action.Kind == AutoKind.Click && Math.Abs(m3.Action.X - (f4.Mark.X + f4.Mark.W / 2.0)) <= 1 && Math.Abs(m3.Action.Y - (f4.Mark.Y + f4.Mark.H / 2.0)) <= 1, "art pilot: the artifact's cell in view -> a click on its centre");
    var hFar = HS(7, true, 0, other, 9001, far); var sFar = new ArtEquipStep { Kind = ArtEquipKind.PickCell, Pos = 55 };
    var p4 = new ArtEquipPilot(); var m4 = p4.Step(V(sFar, hFar, false, false, 0));
    Check(m4.Todo == ArtEquipTodo.Cell && m4.Action.Kind == AutoKind.Drag && m4.Action.DY < 0 && m4.Action.X > 18 && m4.Action.X < 264, "art pilot: a cell far below -> a drag up inside the list (" + m4.Action.DY + ")");
    // a list that does not move on two drags -> stop
    var p4b = new ArtEquipPilot(); long tt = 0; ArtEquipMove last = null;
    for (int i = 0; i < 6 && !p4b.Stopped; i++) { last = p4b.Step(V(sFar, hFar, false, false, tt)); tt += 2500; }
    Check(p4b.Stopped && p4b.Reason == "lost", "art pilot: the list does not move on the drags -> stop 'lost'");
    var hRep = HS(7, true, 9004, other, 9001, u5); var sRep = N(hRep, false);
    var p5 = new ArtEquipPilot(); var m5 = p5.Step(V(sRep, hRep, false, true, 0));
    Check(m5.State == "hint" && m5.Why == "player" && m5.Action.Kind == AutoKind.None, "art pilot: «Заменить» without the player's auto confirm is the player's");
    var m5b = p5.Step(V(sRep, hRep, true, false, 10)); Check(m5b.State == "hint" && m5b.Why == "button", "art pilot: «Заменить» not seen as the game's blue button -> no click");
    var m5c = p5.Step(V(sRep, hRep, true, true, 20));
    Check(m5c.Todo == ArtEquipTodo.Replace && Math.Abs(m5c.Action.X - 521) <= 1.5 && Math.Abs(m5c.Action.Y - 576.1) <= 1.5, "art pilot: with the auto confirm and the button seen -> «Заменить» (centre 521, 576)");
    var hPop = HS(7, true, 9004, other, 9001, u5); var sPop = N(hPop, true);
    var p6 = new ArtEquipPilot(); var m6 = p6.Step(V(sPop, hPop, false, false, 0));
    Check(m6.State == "hint" && m6.Why == "player", "art pilot: the pop-up's «Заменить» without the auto confirm is the player's");
    var m6b = p6.Step(V(sPop, hPop, true, false, 10));
    Check(m6b.Todo == ArtEquipTodo.Confirm && Math.Abs(m6b.Action.X - 685.5) <= 1.5 && Math.Abs(m6b.Action.Y - 394) <= 1.5, "art pilot: the pop-up's «Заменить» (centre 685.5, 394)");
    Check(new ArtEquipPilot().Step(V(N(HS(7, true, 0, other, 9001, new long[] { 9001 }), false), HS(7, true, 0, other, 9001, new long[] { 9001 }), true, true, 0)).State == "hint", "art pilot: the artifact not in the list -> hints only");
    var pb = new ArtEquipPilot(); var vb = V(N(hTab, false), hTab, false, false, 0); vb.Foreground = false;
    Check(pb.Step(vb).State == "background", "art pilot: the game is not in front -> waits");
    var pu = new ArtEquipPilot(); var vu = V(N(hTab, false), hTab, false, false, 0); vu.UserBusy = true;
    Check(pu.Step(vu).State == "paused", "art pilot: the player moves the mouse -> waits");
    var ps = new ArtEquipPilot(); long ts = 0; int n = 0;
    while (!ps.Stopped && n++ < 20) { ps.Step(V(N(hTab, false), hTab, false, false, ts)); ts += 2000; }
    Check(ps.Stopped && ps.Reason == "stuck", "art pilot: the same step again and again -> stop 'stuck'");
    ps.Reset(); Check(!ps.Stopped, "art pilot: a new run starts again");

    // --- the gate: the Equip owner's artifact targets
    Func<HeroScreen, bool, ScreenState> SC = (hs, popup) => ScreenState.Classify(new ScreenInputs { Hero = hs, ModalExtra = popup, ModalExtraWhy = popup ? ClickGate.ModalEquipPopup : "" }, 0);
    Func<ClickTarget, ClickRequest> RQ = tg => new ClickRequest { Owner = ClickOwner.Equip, Target = tg, AutoOn = true, Front = true, Hero = hero, ArtUid = 9004, CellOk = new HashSet<long> { 9004 } };
    Func<ClickGate> GOn = () => new ClickGate { ArtEquipAuto = true };
    var gOff = new ClickGate(); var rOff = gOff.Check(RQ(ClickTarget.ArtHeroTab), SC(hTab, false), 0);
    Check(!rOff.Allowed && gOff.HaltReason(ClickOwner.Equip) == "artifact", "art gate: the switch / the auto click off -> hints only (" + rOff.Reason + ")");
    Check(GOn().Check(RQ(ClickTarget.ArtHeroTab), SC(hTab, false), 0).Allowed, "art gate: the tab from the gear tab -> allowed");
    var g = GOn(); var r = g.Check(RQ(ClickTarget.ArtHeroTab), SC(hOpen, false), 0);
    Check(!r.Allowed && r.Reason == "screen", "art gate: the tab while on it already -> stop");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtHeroTab), SC(HS(3, false, 0, hero, 9004, null), false), 0);
    Check(!r.Allowed && r.Reason == "screen", "art gate: the artifact is on the hero already -> stop");
    g = GOn(); var rr0 = RQ(ClickTarget.ArtHeroTab); rr0.Hero = 5;
    r = g.Check(rr0, SC(hTab, false), 0); Check(!r.Allowed && r.Reason == "screen", "art gate: the screen shows another hero -> stop");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtHeroTab), SC(new HeroScreen { Hero = hero, FormOk = true, Tab = 3, ArtRead = false }, false), 0);
    Check(r.Verdict == GateVerdict.Wait, "art gate: the artifact fields unreadable -> waited for");
    Check(GOn().Check(RQ(ClickTarget.ArtOpenList), SC(hOpen, false), 0).Allowed, "art gate: the list button on the artifact tab with the list closed -> allowed");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtOpenList), SC(hTab, false), 0); Check(!r.Allowed && r.Reason == "screen", "art gate: the list button on the gear tab -> stop");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtOpenList), SC(hPick, false), 0); Check(!r.Allowed && r.Reason == "screen", "art gate: the list button with the list open -> stop");
    // the cell: the uid under the click is the plan's
    Func<long, ClickRequest> CellQ = uid => { var q = RQ(ClickTarget.ArtHeroCell); q.CellUid = uid; return q; };
    Check(GOn().Check(CellQ(9004), SC(hPick, false), 0).Allowed, "art gate: the cell of the plan's artifact -> allowed");
    g = GOn(); r = g.Check(CellQ(9003), SC(hPick, false), 0); Check(!r.Allowed && r.Reason == "cell", "art gate: a neighbour's cell -> stop");
    g = GOn(); r = g.Check(CellQ(0), SC(hPick, false), 0); Check(!r.Allowed && r.Reason == "cell", "art gate: the cell under the click not worked out -> stop");
    g = GOn(); r = g.Check(CellQ(9004), SC(hOpen, false), 0); Check(!r.Allowed && r.Reason == "screen", "art gate: a cell click with the list closed -> stop");
    g = GOn(); r = g.Check(CellQ(9004), SC(HS(7, true, 9004, other, 9001, u5), false), 0); Check(r.Verdict == GateVerdict.Wait, "art gate: the artifact is selected already -> nothing to click");
    Check(GOn().Check(RQ(ClickTarget.ArtHeroDrag), SC(hPick, false), 0).Allowed, "art gate: scrolling the open list -> allowed");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtHeroDrag), SC(hOpen, false), 0); Check(!r.Allowed && r.Reason == "screen", "art gate: scrolling with the list closed -> stop");
    // «Заменить» of the card: the plan's artifact selected
    Check(GOn().Check(RQ(ClickTarget.ArtReplace), SC(hRep, false), 0).Allowed, "art gate: «Заменить» with the plan's artifact selected -> allowed");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtReplace), SC(HS(7, true, 9001, other, 9001, u5), false), 0); Check(!r.Allowed && r.Reason == "cell", "art gate: «Заменить» of another artifact's card -> stop");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtReplace), SC(hPick, false), 0); Check(!r.Allowed && r.Reason == "cell", "art gate: «Заменить» with nothing selected -> stop");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtReplace), SC(hRep, true), 0); Check(!r.Allowed && r.Reason == "modal", "art gate: «Заменить» of the card under the pop-up -> stop");
    // the pop-up's «Заменить»
    Check(GOn().Check(RQ(ClickTarget.ArtTakeOverConfirm), SC(hRep, true), 0).Allowed, "art gate: the pop-up of the plan's artifact (worn by another hero) -> its «Заменить» allowed");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtTakeOverConfirm), SC(hRep, false), 0); Check(!r.Allowed && r.Reason == "modal", "art gate: no pop-up -> stop");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtTakeOverConfirm), SC(HS(7, true, 9001, other, 9001, u5), true), 0); Check(!r.Allowed && r.Reason == "cell", "art gate: a pop-up while another artifact is selected -> stop");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtTakeOverConfirm), SC(HS(7, true, 9004, 0, 9001, u5), true), 0); Check(!r.Allowed && r.Reason == "cell", "art gate: a pop-up for an artifact worn by nobody is not the expected one -> stop");
    g = GOn(); r = g.Check(RQ(ClickTarget.ArtTakeOverConfirm), SC(new HeroScreen { Hero = hero, FormOk = true, Tab = 7, FilterOpen = true, ArtRead = true, ArtListOpen = true, ArtSel = 9004, ArtTargetOwner = other, ArtWorn = 9001 }, false), 0);
    Check(!r.Allowed, "art gate: another pop-up (the hero filter) is not the artifact's");
    // the other owners never click them
    foreach (var tg in new[] { ClickTarget.ArtHeroTab, ClickTarget.ArtOpenList, ClickTarget.ArtHeroCell, ClickTarget.ArtHeroDrag, ClickTarget.ArtReplace, ClickTarget.ArtTakeOverConfirm }) {
      foreach (var ow in new[] { ClickOwner.Locate, ClickOwner.Sell }) {
        g = new ClickGate { ArtEquipAuto = true, ArtLocateAuto = true, ArtSellAuto = true };
        var q = RQ(tg); q.Owner = ow; q.CellUid = 9004;
        Check(!g.Check(q, SC(hRep, true), 0).Allowed, "art gate: " + ow + " never clicks " + tg);
      }
    }
    // the effects
    {
      var rq = RQ(ClickTarget.ArtHeroTab); var gE = GOn();
      gE.Arm(ClickOwner.Equip, ClickTarget.ArtHeroTab, ClickGate.Expect(rq, SC(hTab, false)), 0);
      gE.Observe(SC(hTab, false), ClickGate.EffectMs + 20);
      Check(gE.HaltReason(ClickOwner.Equip) == "nochange", "art gate: the tab does not switch within the time -> stop");
      gE = GOn(); gE.Arm(ClickOwner.Equip, ClickTarget.ArtHeroTab, ClickGate.Expect(rq, SC(hTab, false)), 0); gE.Observe(SC(hOpen, false), 500);
      Check(!gE.Pending(ClickOwner.Equip) && !gE.Halted(ClickOwner.Equip), "art gate: the tab is on -> done");
      rq = RQ(ClickTarget.ArtOpenList); gE = GOn(); gE.Arm(ClickOwner.Equip, ClickTarget.ArtOpenList, ClickGate.Expect(rq, SC(hOpen, false)), 0); gE.Observe(SC(hPick, false), 600);
      Check(!gE.Pending(ClickOwner.Equip), "art gate: the list opened -> done");
      rq = CellQ(9004); gE = GOn(); gE.Arm(ClickOwner.Equip, ClickTarget.ArtHeroCell, ClickGate.Expect(rq, SC(hPick, false)), 0);
      gE.Observe(SC(HS(7, true, 9003, other, 9001, u5), false), 500);
      Check(gE.Pending(ClickOwner.Equip), "art gate: a neighbour got selected -> not the effect");
      gE.Observe(SC(hRep, false), 900); Check(!gE.Pending(ClickOwner.Equip), "art gate: the plan's artifact selected -> done");
      // «Заменить»: the pop-up appears (worn by another hero) or the artifact is on the hero
      rq = RQ(ClickTarget.ArtReplace); gE = GOn(); gE.Arm(ClickOwner.Equip, ClickTarget.ArtReplace, ClickGate.Expect(rq, SC(hRep, false)), 0); gE.Observe(SC(hRep, true), 400);
      Check(!gE.Pending(ClickOwner.Equip) && !gE.Halted(ClickOwner.Equip), "art gate: «Заменить» -> the pop-up opened -> done");
      gE = GOn(); gE.Arm(ClickOwner.Equip, ClickTarget.ArtReplace, ClickGate.Expect(rq, SC(HS(7, true, 9004, 0, 9001, u5), false)), 0); gE.Observe(SC(HS(7, true, 9004, hero, 9004, u5), false), 400);
      Check(!gE.Pending(ClickOwner.Equip), "art gate: «Заменить» of a free artifact -> on the hero -> done");
      gE = GOn(); gE.Arm(ClickOwner.Equip, ClickTarget.ArtReplace, ClickGate.Expect(rq, SC(hRep, false)), 0); gE.Observe(SC(hRep, false), ClickGate.SlowEffectMs + 20);
      Check(gE.HaltReason(ClickOwner.Equip) == "nochange", "art gate: «Заменить» changes nothing within the time -> stop");
      rq = RQ(ClickTarget.ArtTakeOverConfirm); gE = GOn(); gE.Arm(ClickOwner.Equip, ClickTarget.ArtTakeOverConfirm, ClickGate.Expect(rq, SC(hRep, true)), 0);
      gE.Observe(SC(HS(7, true, 9004, hero, 9004, u5), false), 1500);
      Check(!gE.Pending(ClickOwner.Equip), "art gate: the pop-up's «Заменить» -> the artifact on the hero -> done");
      gE = GOn(); gE.Arm(ClickOwner.Equip, ClickTarget.ArtTakeOverConfirm, ClickGate.Expect(rq, SC(hRep, true)), 0); gE.Observe(SC(hRep, true), ClickGate.SlowEffectMs + 20);
      Check(gE.HaltReason(ClickOwner.Equip) == "nochange", "art gate: the pop-up stays -> stop");
    }
    string wl = ClickGate.WhitelistText(ClickOwner.Equip);
    Check(wl.Contains("ArtHeroTab@HeroScreen") && wl.Contains("ArtTakeOverConfirm@HeroScreen") && !wl.Contains("ArtBulk") && !wl.Contains("ArtWorkshop"), "art gate: the equip owner's whitelist has the hero-screen artifact targets only");
    foreach (var oo in new[] { ClickOwner.Locate, ClickOwner.Sell }) { string w2 = ClickGate.WhitelistText(oo); Check(!w2.Contains("ArtHero") && !w2.Contains("ArtOpenList") && !w2.Contains("ArtReplace") && !w2.Contains("ArtTakeOver"), oo + ": none of the equip owner's artifact targets"); }
  }

  static void ArtAutoTests() {
    Console.WriteLine("Artifact flows with auto click (switch artAuto, gate whitelist, pilots)");
    // --- the switch: off until the site says true
    var sw = RemoteSwitches.Permissive();
    Check(!sw.Allows("artAuto") && sw.Allows("autoclick"), "artAuto: off by default, the others on");
    Check(!RemoteSwitches.Parse("{}").Allows("artAuto") && !RemoteSwitches.Parse("{\"features\":{\"artAuto\":\"yes\"}}").Allows("artAuto"), "artAuto: a missing or odd value is off");
    Check(RemoteSwitches.Parse("{\"features\":{\"artAuto\":true}}").Allows("artAuto") && RemoteSwitches.Parse(RemoteSwitches.Parse("{\"features\":{\"artAuto\":true}}").ToJson()).Allows("artAuto"), "artAuto: only a plain true switches it on, the saved copy keeps it");
    Check(RemoteSwitches.Parse("{\"features\":{\"artAuto\":true},\"readEnabled\":false}").Allows("artAuto") == false, "artAuto: reading off switches it off too");
    Check(RemoteSwitches.Parse("{\"readEnabled\":true}").Off().Count == 0, "artAuto: starting off is not listed as 'switched off'");

    double W = 1136, H = 640, U = Ui.Unit(W, H);
    Func<InventoryScreen, ScreenState> S = i => ScreenState.Classify(new ScreenInputs { Inv = i }, 0);
    var uids = new List<long>(); for (long i = 1; i <= 56; i++) uids.Add(9000 + i);   // 8 rows of 7
    Func<bool, List<long>, InventoryScreen> artBulk = (ticked, sel) => { var v = new InventoryScreen { Open = true, ArtTab = true, ArtBulk = true, ArtBulkKnown = true, ArtMode = 2, ListPtr = 7, Uids = new List<long>(uids) }; foreach (var u in sel) v.Selected.Add(u); return v; };
    var bulkNone = S(artBulk(false, new List<long>()));
    var bulkTicked = S(artBulk(true, new List<long> { 9001, 9002 }));
    var artNormal = S(new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = -1, Uids = new List<long>(uids) });
    var artOrganize = S(new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = 1, Uids = new List<long>(uids) });
    var artModeUnknown = S(new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = false });
    var equipNormal = S(new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true });
    var equipBulk = S(new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true, Bulk = true });
    var otherTab = S(new InventoryScreen { Open = true, BulkKnown = true });

    // --- the cleanup (Sell owner)
    Func<ClickTarget, ClickRequest> R = tg => new ClickRequest { Owner = ClickOwner.Sell, Target = tg, AutoOn = true, Front = true };
    Func<ClickTarget, long, bool, ClickRequest> Cell = (tg, uid, untick) => { var q = R(tg); q.CellUid = uid; q.CellOk = new HashSet<long> { 9003, 9004 }; q.Untick = untick; return q; };
    Func<ClickGate> Off = () => new ClickGate { NoSellClicks = true };
    Func<ClickGate> On = () => new ClickGate { ArtSellAuto = true };
    foreach (var tg in new[] { ClickTarget.ArtTab, ClickTarget.ArtBulkEnter, ClickTarget.ArtBulkCell, ClickTarget.ArtListDrag }) {
      var g0 = Off(); var r0 = g0.Check(Cell(tg, 9003, false), bulkNone, 0);
      Check(!r0.Allowed && g0.HaltReason(ClickOwner.Sell) == "artifact", "art auto: switch off -> " + tg + " is never clicked (" + r0.Reason + ")");
      var g1 = new ClickGate(); var r1 = g1.Check(Cell(tg, 9003, false), bulkNone, 0);
      Check(!r1.Allowed && g1.HaltReason(ClickOwner.Sell) == "artifact", "art auto: a run that is not an auto artifact run never clicks " + tg);
    }
    var gT = On();
    Check(gT.Check(R(ClickTarget.ArtTab), equipNormal, 0).Allowed, "art auto: «Артефакт» tab from the equipment tab -> allowed");
    gT = On(); Check(gT.Check(R(ClickTarget.ArtTab), otherTab, 0).Allowed, "art auto: «Артефакт» tab from another tab -> allowed");
    gT = On(); var rb = gT.Check(R(ClickTarget.ArtTab), equipBulk, 0); Check(!rb.Allowed && rb.Reason == "bulk", "art auto: the equipment tab in bulk sale is not left by the app");
    gT = On(); rb = gT.Check(R(ClickTarget.ArtTab), artNormal, 0); Check(!rb.Allowed && rb.Reason == "screen", "art auto: already on the artifact tab -> stop");
    gT = On(); Check(gT.Check(R(ClickTarget.ArtBulkEnter), artNormal, 0).Allowed, "art auto: «Массовая продажа» in the artifact tab's normal mode -> allowed");
    gT = On(); rb = gT.Check(R(ClickTarget.ArtBulkEnter), bulkNone, 0); Check(!rb.Allowed && rb.Reason == "bulk", "art auto: «Массовая продажа» while it is on already -> stop");
    gT = On(); rb = gT.Check(R(ClickTarget.ArtBulkEnter), artOrganize, 0); Check(!rb.Allowed && rb.Reason == "bulk", "art auto: the arrange mode is not left by the app");
    gT = On(); rb = gT.Check(R(ClickTarget.ArtBulkEnter), equipNormal, 0); Check(!rb.Allowed && rb.Reason == "screen", "art auto: «Массовая продажа» on the equipment tab -> stop");
    gT = On(); rb = gT.Check(R(ClickTarget.ArtBulkEnter), artModeUnknown, 0); Check(rb.Verdict == GateVerdict.Wait, "art auto: the artifact tab's mode unreadable -> waited for");
    gT = On(); Check(gT.Check(Cell(ClickTarget.ArtBulkCell, 9003, false), bulkNone, 0).Allowed, "art auto: a wanted, unticked card -> tick it");
    gT = On(); rb = gT.Check(Cell(ClickTarget.ArtBulkCell, 9005, false), bulkNone, 0); Check(!rb.Allowed && rb.Reason == "cell", "art auto: a card that is not wanted is never ticked");
    gT = On(); rb = gT.Check(Cell(ClickTarget.ArtBulkCell, 0, false), bulkNone, 0); Check(!rb.Allowed && rb.Reason == "cell", "art auto: the card under the click not worked out (no exact scroll) -> stop");
    var bulkWantedTicked = S(artBulk(true, new List<long> { 9003 }));
    gT = On(); rb = gT.Check(Cell(ClickTarget.ArtBulkCell, 9003, false), bulkWantedTicked, 0); Check(!rb.Allowed && rb.Reason == "cell", "art auto: a wanted card that is ticked already would be taken off -> stop");
    gT = On(); Check(gT.Check(Cell(ClickTarget.ArtBulkCell, 9001, true), bulkTicked, 0).Allowed, "art auto: a ticked card that is not wanted -> taken off");
    gT = On(); rb = gT.Check(Cell(ClickTarget.ArtBulkCell, 9003, true), bulkWantedTicked, 0); Check(!rb.Allowed && rb.Reason == "cell", "art auto: a wanted card is never taken off");
    gT = On(); rb = gT.Check(Cell(ClickTarget.ArtBulkCell, 9005, true), bulkNone, 0); Check(!rb.Allowed && rb.Reason == "cell", "art auto: 'untick' of a card that is not ticked -> stop");
    gT = On(); rb = gT.Check(Cell(ClickTarget.ArtBulkCell, 9003, false), artNormal, 0); Check(!rb.Allowed && rb.Reason == "screen", "art auto: a cell click outside the bulk-sale mode -> stop");
    gT = On(); rb = gT.Check(R(ClickTarget.ArtListDrag), bulkNone, 0); Check(rb.Allowed, "art auto: scrolling the list in bulk sale -> allowed");
    gT = On(); rb = gT.Check(R(ClickTarget.ArtListDrag), artNormal, 0); Check(!rb.Allowed, "art auto: scrolling in the normal mode is not the cleanup's");
    // nothing else on the artifact run: the gear targets, the equipment cell, and the equip helper's
    foreach (var tg in new[] { ClickTarget.InvBulkCell, ClickTarget.InvBulkEnter, ClickTarget.InvEquipTab, ClickTarget.InvListCell, ClickTarget.CityInventory, ClickTarget.FoundryButton }) {
      gT = On(); rb = gT.Check(Cell(tg, 9003, false), bulkNone, 0);
      Check(!rb.Allowed && gT.HaltReason(ClickOwner.Sell) == "artifact", "art auto: the artifact cleanup clicks no " + tg);
    }
    var gE = new ClickGate { ArtSellAuto = true, ArtLocateAuto = true }; var re = gE.Check(new ClickRequest { Owner = ClickOwner.Equip, Target = ClickTarget.ArtTab, AutoOn = true, Front = true }, otherTab, 0);
    Check(!re.Allowed && gE.HaltReason(ClickOwner.Equip) == "target", "art auto: the equip helper has no artifact target");
    gT = On(); rb = gT.Check(new ClickRequest { Owner = ClickOwner.Sell, Target = ClickTarget.ArtTab, AutoOn = false, Front = true }, otherTab, 0); Check(!rb.Allowed && rb.Reason == "off" && !gT.Halted(ClickOwner.Sell), "art auto: the player's auto click off -> no click, not a stop");
    // the effect: the game's choice holds the card within 1 s, else stop
    {
      var q = Cell(ClickTarget.ArtBulkCell, 9003, false);
      var after = S(artBulk(true, new List<long> { 9003 }));
      var g2 = On(); g2.Arm(ClickOwner.Sell, ClickTarget.ArtBulkCell, ClickGate.Expect(q, bulkNone), 0);
      g2.Observe(bulkNone, ClickGate.ArtTickMs + 20);
      Check(g2.HaltReason(ClickOwner.Sell) == "nochange", "art auto: the tick does not show in the game's choice within 1 s -> stop");
      g2 = On(); g2.Arm(ClickOwner.Sell, ClickTarget.ArtBulkCell, ClickGate.Expect(q, bulkNone), 0);
      g2.Observe(S(artBulk(true, new List<long> { 9004 })), 500);
      Check(g2.Pending(ClickOwner.Sell), "art auto: another card ticked (a neighbour was hit) is not the effect");
      g2.Observe(S(artBulk(true, new List<long> { 9004 })), ClickGate.ArtTickMs + 20);
      Check(g2.HaltReason(ClickOwner.Sell) == "nochange", "art auto: ... and stops the run after the second");
      g2 = On(); g2.Arm(ClickOwner.Sell, ClickTarget.ArtBulkCell, ClickGate.Expect(q, bulkNone), 0); g2.Observe(after, 300);
      Check(!g2.Pending(ClickOwner.Sell) && !g2.Halted(ClickOwner.Sell), "art auto: the card is in the choice -> done");
      var qu = Cell(ClickTarget.ArtBulkCell, 9001, true);
      g2 = On(); g2.Arm(ClickOwner.Sell, ClickTarget.ArtBulkCell, ClickGate.Expect(qu, bulkTicked), 0); g2.Observe(bulkTicked, 300);
      Check(g2.Pending(ClickOwner.Sell), "art auto: a tick that is still there is not the effect of an untick");
      g2.Observe(S(artBulk(true, new List<long> { 9002 })), 400);
      Check(!g2.Pending(ClickOwner.Sell), "art auto: the tick gone -> done");
    }

    // --- the cleanup pilot ticks the cards it means (the card under its click point is the one meant)
    {
      var g = SellGeometry.Artifacts(W, H, U, 7);
      var wanted = new HashSet<long> { 9001, 9010, 9043 };
      var inv = artBulk(true, new List<long>());
      var p = new SellPilot(g); long t = 1000;
      double row1 = g.ViewTopPx(U) + g.TopPad * U;
      var a = p.Step(new SellView { NowMs = t, Foreground = true, W = W, H = H, U = U, Screen = inv.AsSellArt(7), Wanted = wanted, Row1 = row1 });
      long under = a.Kind == AutoKind.Click ? ClickGate.InvCellUid(inv, g, row1, 7, a.X, a.Y, W, U) : -1;
      Check(a.Kind == AutoKind.Click && under == 9001, "art pilot: the first click lands on the first wanted card (card under it: " + under + ")");
      inv.Selected.Add(9001); t += 400;
      a = p.Step(new SellView { NowMs = t, Foreground = true, W = W, H = H, U = U, Screen = inv.AsSellArt(7), Wanted = wanted, Row1 = row1 });
      under = a.Kind == AutoKind.Click ? ClickGate.InvCellUid(inv, g, row1, 7, a.X, a.Y, W, U) : -1;
      Check(a.Kind == AutoKind.Click && under == 9010, "art pilot: then the next one (card under the click: " + under + ")");
      inv.Selected.Add(9010); t += 400;
      a = p.Step(new SellView { NowMs = t, Foreground = true, W = W, H = H, U = U, Screen = inv.AsSellArt(7), Wanted = wanted, Row1 = row1 });
      Check(a.Kind == AutoKind.Drag, "art pilot: a card below the view -> the list is dragged first");
      Check(!inv.AsSellArt(7).Open == false && !new InventoryScreen { Open = true, ArtTab = true, ArtBulk = false }.AsSellArt(7).Open && !new InventoryScreen { Open = true, EquipTab = true, Bulk = true }.AsSellArt(7).Open, "art pilot: the list is open only in the artifact tab's bulk sale");
      // a ticked card that is not wanted comes off
      var inv2 = artBulk(true, new List<long> { 9001, 9002 }); var p2 = new SellPilot(g);
      var wanted2 = new HashSet<long> { 9001 };
      a = p2.Step(new SellView { NowMs = 1000, Foreground = true, W = W, H = H, U = U, Screen = inv2.AsSellArt(7), Wanted = wanted2, Row1 = row1 });
      Check(a.Kind == AutoKind.None, "art pilot: nothing wanted left to tick (done or waiting)");
    }

    // --- the promotion (Locate owner)
    var art = new ArtPromoteInfo { Uid = 5001, Need = 2, Cap = 10, Via = "copy", Copy = 5002 }; art.Copies.AddRange(new long[] { 5002, 5003 });
    var req = new LocateRequest { Id = "r", Purpose = "artPromote", Art = art };
    Func<ClickTarget, ClickRequest> L = tg => new ClickRequest { Owner = ClickOwner.Locate, Target = tg, AutoOn = true, Front = true, CellOk = new HashSet<long> { 5001 } };
    Func<ClickGate> LOn = () => new ClickGate { ArtLocateAuto = true };
    var u3 = new long[] { 5010, 5001, 5002 };
    Func<int, InventoryScreen> artSel = sel => { var i = new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = -1, ArtSelKnown = true, ArtSel = sel, ListPtr = 7, List = new RFX.ListRead { ScrollY = 0, Cols = 7, RowUnits = 116, First = 0, Last = 27 } }; i.Uids.AddRange(u3); return i; };
    foreach (var tg in new[] { ClickTarget.ArtTab, ClickTarget.ArtCell, ClickTarget.ArtWorkshop, ClickTarget.ArtPromoteTab, ClickTarget.ArtMatCell, ClickTarget.ArtListDrag, ClickTarget.ArtMatDrag }) {
      var g0 = new ClickGate { NoLocateClicks = true }; var r0 = g0.Check(L(tg), S(artSel(2)), 0);
      Check(!r0.Allowed && g0.HaltReason(ClickOwner.Locate) == "artifact", "art auto: switch off -> " + tg + " is never clicked on the promotion (" + r0.Reason + ")");
      var g1 = new ClickGate(); r0 = g1.Check(L(tg), S(artSel(2)), 0);
      Check(!r0.Allowed && g1.HaltReason(ClickOwner.Locate) == "artifact", "art auto: a request that is not an auto artifact one never clicks " + tg);
    }
    var gL = LOn(); var rl = gL.Check(L(ClickTarget.InvEquipTab), S(artSel(2)), 0); Check(!rl.Allowed && gL.HaltReason(ClickOwner.Locate) == "artifact", "art auto: the promotion clicks no gear target");
    gL = LOn(); Check(gL.Check(L(ClickTarget.ArtTab), equipNormal, 0).Allowed, "art auto: promotion: «Артефакт» tab -> allowed");
    gL = LOn(); rl = gL.Check(L(ClickTarget.ArtTab), equipBulk, 0); Check(!rl.Allowed && rl.Reason == "bulk", "art auto: promotion: the equipment sale is not left by the app");
    var cellReq = L(ClickTarget.ArtCell); cellReq.CellUid = 5001;
    gL = LOn(); Check(gL.Check(cellReq, S(artSel(0)), 0).Allowed, "art auto: promotion: the artifact's cell in the normal list -> allowed");
    cellReq = L(ClickTarget.ArtCell); cellReq.CellUid = 5002;
    gL = LOn(); rl = gL.Check(cellReq, S(artSel(0)), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: promotion: a neighbour's cell -> stop");
    cellReq = L(ClickTarget.ArtCell); cellReq.CellUid = 0;
    gL = LOn(); rl = gL.Check(cellReq, S(artSel(0)), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: promotion: a cell not worked out -> stop");
    cellReq = L(ClickTarget.ArtCell); cellReq.CellUid = 5001;
    gL = LOn(); rl = gL.Check(cellReq, bulkNone, 0); Check(!rl.Allowed && rl.Reason == "bulk", "art auto: promotion: never a cell in the bulk-sale mode (" + rl.Reason + ")");
    gL = LOn(); Check(gL.Check(L(ClickTarget.ArtWorkshop), S(artSel(2)), 0).Allowed, "art auto: «Мастерская» with the artifact's card open -> allowed");
    gL = LOn(); rl = gL.Check(L(ClickTarget.ArtWorkshop), S(artSel(3)), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: «Мастерская» with another card open -> stop");
    gL = LOn(); rl = gL.Check(L(ClickTarget.ArtWorkshop), S(artSel(0)), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: «Мастерская» with no card open -> stop");
    Func<long, int, long[], long[], int[], int[], ArtWorkshopScreen> Wk = (uid, tab, mats, lv, sel, itemIds) => { var w = new ArtWorkshopScreen { Open = true, Tab = tab, Uid = uid, Level = 10, Stage = 0, PanelKnown = mats != null };
      if (mats != null) { w.Mats.AddRange(mats); foreach (var l in lv) w.MatLevels.Add((int)l); foreach (var x in sel) w.MatSel.Add(x); foreach (var x in itemIds) w.MatItemIds.Add(x); w.MatList = new RFX.ListRead { ScrollY = 0, Cols = 3, RowUnits = 114, First = 0, Last = 8 }; } return w; };
    Func<ArtWorkshopScreen, ScreenState> SW = w => ScreenState.Classify(new ScreenInputs { ArtWs = w, Inv = artSel(2) }, 0);
    var matsA = new long[] { 5003, 5002, 0 }; var lvA = new long[] { 7, 1, -1 }; var idsA = new[] { 200088, 200088, 16100 };
    gL = LOn(); Check(gL.Check(L(ClickTarget.ArtPromoteTab), SW(Wk(5001, 1, null, null, null, null)), 0).Allowed, "art auto: the «Продвинуть» tab from «Улучшить» -> allowed");
    gL = LOn(); rl = gL.Check(L(ClickTarget.ArtPromoteTab), SW(Wk(5001, 2, matsA, lvA, new int[0], idsA)), 0); Check(!rl.Allowed && rl.Reason == "screen", "art auto: already on the «Продвинуть» tab -> stop");
    gL = LOn(); rl = gL.Check(L(ClickTarget.ArtPromoteTab), SW(Wk(9999, 1, null, null, null, null)), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: the workshop holds another artifact -> stop");
    gL = LOn(); rl = gL.Check(L(ClickTarget.ArtPromoteTab), artNormal, 0); Check(!rl.Allowed && rl.Reason == "screen", "art auto: «Продвинуть» tab on the inventory -> stop");
    Func<int, long, ICollection<int>, ClickRequest> MatReq = (idx, uid, items) => { var q = L(ClickTarget.ArtMatCell); q.MatIndex = idx; q.CellUid = uid; q.CellOk = new HashSet<long> { 5001, 5002, 5003 }; q.ItemOk = items; return q; };
    var wsP = Wk(5001, 2, matsA, lvA, new int[0], idsA);
    gL = LOn(); Check(gL.Check(MatReq(2, 5002, null), SW(wsP), 0).Allowed, "art auto: the planned copy (entry 2) -> allowed");
    gL = LOn(); Check(gL.Check(MatReq(1, 5003, null), SW(wsP), 0).Allowed, "art auto: the other planned copy (entry 1) -> allowed");
    var art2 = Wk(5001, 2, new long[] { 5003, 5002, 5099 }, new long[] { 7, 1, 1 }, new int[0], idsA);
    gL = LOn(); rl = gL.Check(MatReq(3, 5099, null), SW(art2), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: a copy the plan did not name -> stop");
    gL = LOn(); rl = gL.Check(MatReq(2, 5003, null), SW(wsP), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: the click's uid must be the entry's own -> stop");
    gL = LOn(); rl = gL.Check(MatReq(0, 0, null), SW(wsP), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: no entry under the click -> stop");
    gL = LOn(); rl = gL.Check(MatReq(4, 0, null), SW(wsP), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: an entry past the list -> stop");
    gL = LOn(); rl = gL.Check(MatReq(2, 5002, null), SW(Wk(5001, 2, matsA, lvA, new[] { 2 }, idsA)), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: a copy that is chosen already would be taken off -> stop");
    gL = LOn(); rl = gL.Check(MatReq(3, 0, null), SW(wsP), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: an essence the plan did not name (no item id given) -> stop");
    gL = LOn(); rl = gL.Check(MatReq(3, 0, new HashSet<int> { 16100 }), SW(wsP), 0); Check(rl.Allowed, "art auto: the planned essence (its item id) -> allowed");
    gL = LOn(); rl = gL.Check(MatReq(3, 0, new HashSet<int> { 16101 }), SW(wsP), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: another essence item -> stop");
    gL = LOn(); rl = gL.Check(MatReq(1, 5003, null), SW(Wk(5001, 1, null, null, null, null)), 0); Check(!rl.Allowed && rl.Reason == "screen", "art auto: a copy click on the «Улучшить» tab -> stop");
    gL = LOn(); rl = gL.Check(MatReq(1, 5001, null), SW(Wk(5001, 2, new long[] { 5001 }, new long[] { 3 }, new int[0], new[] { 1 })), 0); Check(!rl.Allowed && rl.Reason == "cell", "art auto: the open artifact itself is never a copy");
    // the effects
    {
      var g2 = LOn(); var q = MatReq(2, 5002, null);
      g2.Arm(ClickOwner.Locate, ClickTarget.ArtMatCell, ClickGate.Expect(q, SW(wsP)), 0);
      g2.Observe(SW(wsP), ClickGate.GemEffectMs + 10);
      Check(g2.HaltReason(ClickOwner.Locate) == "nochange", "art auto: the copy does not get chosen -> stop");
      g2 = LOn(); g2.Arm(ClickOwner.Locate, ClickTarget.ArtMatCell, ClickGate.Expect(q, SW(wsP)), 0);
      g2.Observe(SW(Wk(5001, 2, matsA, lvA, new[] { 2 }, idsA)), 500);
      Check(!g2.Pending(ClickOwner.Locate) && !g2.Halted(ClickOwner.Locate), "art auto: m_vMatSelect holds the copy -> done");
      g2 = LOn(); var qc = cellReq; g2.Arm(ClickOwner.Locate, ClickTarget.ArtCell, ClickGate.Expect(qc, S(artSel(0))), 0);
      g2.Observe(S(artSel(2)), 300);
      Check(!g2.Pending(ClickOwner.Locate), "art auto: the artifact's card opens -> done");
      g2 = LOn(); g2.Arm(ClickOwner.Locate, ClickTarget.ArtWorkshop, ClickGate.Expect(L(ClickTarget.ArtWorkshop), S(artSel(2))), 0);
      g2.Observe(S(artSel(2)), ClickGate.EffectMs + 100);
      Check(!g2.Halted(ClickOwner.Locate), "art auto: the workshop may take a moment to open (slow effect window)");
      g2.Observe(S(artSel(2)), ClickGate.SlowEffectMs + 100);
      Check(g2.HaltReason(ClickOwner.Locate) == "nochange", "art auto: ... but not forever");
    }

    // --- the promotion pilot: what it decides, and never the last button
    var geo = (GameGeometry)null;
    Func<LocateStep, InventoryScreen, ArtWorkshopScreen, ArtView> V = (st, iv, ws) => new ArtView { NowMs = 10000, Foreground = true, W = W, H = H, U = U, Step = st, Inv = iv, Ws = ws, Art = art, Geometry = geo };
    string why;
    Func<InventoryScreen, ArtWorkshopScreen, ArtView> VN = (iv, ws) => V(LocateGuide.NextArt(req, iv, ws), iv, ws);
    Eq(ArtTodo.ArtTab, ArtLocateAuto.Decide(VN(new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, null), out why), "art pilot: the equipment tab -> «Артефакт»");
    Eq(ArtTodo.Hint, ArtLocateAuto.Decide(VN(new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true, Bulk = true }, null), out why), "art pilot: the equipment sale -> the player leaves it"); Eq("bulk", why, "art pilot: ... because of the bulk sale");
    Eq(ArtTodo.Hint, ArtLocateAuto.Decide(VN(new InventoryScreen { Open = true, EquipTab = true }, null), out why), "art pilot: the equipment mode unreadable -> no click");
    Eq(ArtTodo.Hint, ArtLocateAuto.Decide(VN(null, null), out why), "art pilot: nothing open -> a hint (the player opens the Inventory)");
    Eq(ArtTodo.Hint, ArtLocateAuto.Decide(VN(new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = 2, ArtBulk = true, Uids = { 5001 } }, null), out why), "art pilot: the artifact tab's bulk sale -> a hint");
    Eq(ArtTodo.Cell, ArtLocateAuto.Decide(VN(artSel(0), null), out why), "art pilot: the artifact in the list -> its cell");
    Eq(ArtTodo.Workshop, ArtLocateAuto.Decide(VN(artSel(2), null), out why), "art pilot: its card open -> «Мастерская»");
    Eq(ArtTodo.Cell, ArtLocateAuto.Decide(VN(artSel(3), null), out why), "art pilot: another card open -> the artifact's cell again, no «Мастерская»");
    Eq(ArtTodo.PromoteTab, ArtLocateAuto.Decide(VN(null, Wk(5001, 1, null, null, null, null)), out why), "art pilot: the workshop on «Улучшить» -> «Продвинуть» tab");
    Eq(ArtTodo.Hint, ArtLocateAuto.Decide(VN(null, new ArtWorkshopScreen { Open = true, Tab = 1, Uid = 5001, Level = 8 }), out why), "art pilot: below the cap -> «Улучшить» first is the player's");
    Eq(ArtTodo.Hint, ArtLocateAuto.Decide(VN(null, Wk(9999, 2, matsA, lvA, new int[0], idsA)), out why), "art pilot: the workshop holds another artifact -> a hint");
    var pk = VN(null, wsP);
    Eq(ArtTodo.MatCell, ArtLocateAuto.Decide(pk, out why), "art pilot: «Продвинуть» panel -> the planned copy"); Eq(2, pk.Step.Pos, "art pilot: ... place 2");
    Eq(ArtTodo.Hint, ArtLocateAuto.Decide(VN(null, Wk(5001, 2, matsA, lvA, new[] { 2, 1 }, idsA)), out why), "art pilot: all chosen -> «Продвинуть» is the player's"); Eq("player", why, "art pilot: ... said so");
    Eq(ArtTodo.Hint, ArtLocateAuto.Decide(VN(null, Wk(5001, 2, matsA, lvA, new[] { 2, 3 }, idsA)), out why), "art pilot: a wrong essence chosen -> the player takes it off"); Eq("wrong", why, "art pilot: ... wrong");
    var artNoPlan = new ArtPromoteInfo { Uid = 5001, Need = 1, Cap = 10, Via = "none" };
    var reqNo = new LocateRequest { Id = "r", Purpose = "artPromote", Art = artNoPlan };
    var vNo = new ArtView { NowMs = 10000, Foreground = true, W = W, H = H, U = U, Step = LocateGuide.NextArt(reqNo, null, wsP), Ws = wsP, Art = artNoPlan };
    Eq(ArtTodo.Hint, ArtLocateAuto.Decide(vNo, out why), "art pilot: no plan -> a copy of the game's own order is the player's to pick"); Eq("plan", why, "art pilot: ... not planned");
    var artEss = new ArtPromoteInfo { Uid = 5001, Need = 1, Cap = 10, Via = "essence" };
    var vEss = new ArtView { NowMs = 10000, Foreground = true, W = W, H = H, U = U, Step = LocateGuide.NextArt(new LocateRequest { Id = "r", Purpose = "artPromote", Art = artEss }, null, wsP), Ws = wsP, Art = artEss };
    Eq(ArtTodo.MatCell, ArtLocateAuto.Decide(vEss, out why), "art pilot: the plan spends an essence -> the essence stub is clicked");
    var artEss2 = new ArtPromoteInfo { Uid = 5001, Need = 1, Cap = 10, Via = "copy" };
    var vEss2 = new ArtView { NowMs = 10000, Foreground = true, W = W, H = H, U = U, Step = new LocateStep { Kind = LocateKind.ArtMatPick, Pos = 3 }, Ws = wsP, Art = artEss2 };
    Eq(ArtTodo.Hint, ArtLocateAuto.Decide(vEss2, out why), "art pilot: an essence with a copy plan -> a hint");

    // the pilot's actions: a click at the card's centre, a drag when it is off screen, the buttons' centres; never the last button
    {
      var p = new ArtPilot();
      var mv = p.Step(VN(new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, null));
      var tabRect = ArtFrames.Rect(geo, "inventory.leftTab.artifact", W, H, U);
      Check(mv.State == "work" && mv.Todo == ArtTodo.ArtTab && mv.Action.Kind == AutoKind.Click && Math.Abs(mv.Action.X - tabRect.CX) < 1 && Math.Abs(mv.Action.Y - tabRect.CY) < 1, "art pilot: the tab click is at the middle of the «Артефакт» button");
      p = new ArtPilot(); mv = p.Step(VN(artSel(0), null));
      var iv = artSel(0); var stIn = LocateGuide.NextArt(req, iv, null);
      int ci = ClickGate.ListCardIndexAt(geo, "inventory.artList", iv.List, 7, 116, mv.Action.X, mv.Action.Y, W, H, U);
      Check(mv.Todo == ArtTodo.Cell && mv.Action.Kind == AutoKind.Click && ci == 1 && iv.Uids[ci] == 5001, "art pilot: the cell click lands on the artifact's own card (index " + ci + ")");
      var far = new long[60]; for (int i = 0; i < 60; i++) far[i] = 6000 + i; far[50] = 5001;
      var ivF = new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = -1, ArtSelKnown = true, ArtSel = 1, Uids = new List<long>(far), List = new RFX.ListRead { ScrollY = 0, Cols = 7, RowUnits = 116, First = 0, Last = 27 } };
      p = new ArtPilot(); mv = p.Step(VN(ivF, null));
      Check(mv.Todo == ArtTodo.Cell && mv.Action.Kind == AutoKind.Drag && mv.Action.DY < 0 && mv.Drag, "art pilot: a card far down the list -> the list is dragged up");
      var wsRect = ArtFrames.Rect(geo, "inventory.artWorkshop", W, H, U);
      p = new ArtPilot(); mv = p.Step(VN(artSel(2), null));
      Check(mv.Todo == ArtTodo.Workshop && Math.Abs(mv.Action.X - wsRect.CX) < 1 && Math.Abs(mv.Action.Y - wsRect.CY) < 1, "art pilot: «Мастерская» at the middle of its button");
      var ptRect = ArtFrames.Rect(geo, "artifact.tab.promote", W, H, U);
      p = new ArtPilot(); mv = p.Step(VN(null, Wk(5001, 1, null, null, null, null)));
      Check(mv.Todo == ArtTodo.PromoteTab && Math.Abs(mv.Action.X - ptRect.CX) < 1 && Math.Abs(mv.Action.Y - ptRect.CY) < 1, "art pilot: the «Продвинуть» tab at the middle of its button");
      p = new ArtPilot(); mv = p.Step(VN(null, wsP));
      int mi = ClickGate.ListCardIndexAt(geo, "artifact.matList", wsP.MatList, 3, 114, mv.Action.X, mv.Action.Y, W, H, U);
      Check(mv.Todo == ArtTodo.MatCell && mv.Action.Kind == AutoKind.Click && mi == 1 && wsP.Mats[mi] == 5002 && mv.MatIndex == 2, "art pilot: the copy click lands on the planned copy's card (index " + mi + ")");
      // user busy / background / paused: no action
      p = new ArtPilot(); var vb = VN(artSel(2), null); vb.UserBusy = true; mv = p.Step(vb);
      Check(mv.State == "paused" && mv.Action.Kind == AutoKind.None, "art pilot: the player uses the mouse -> it waits");
      p = new ArtPilot(); vb = VN(artSel(2), null); vb.Foreground = false; mv = p.Step(vb);
      Check(mv.State == "background" && mv.Action.Kind == AutoKind.None, "art pilot: the game is not in front -> it waits");
      // the same click again and again stops it
      p = new ArtPilot(); int made = 0; bool stuck = false;
      for (int i = 0; i < 12 && !stuck; i++) { var v = VN(new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, null); v.NowMs = 10000 + i * 4000; mv = p.Step(v); if (mv.Action.Kind == AutoKind.Click) made++; if (mv.State == "stopped") stuck = true; }
      Check(stuck && made == ArtPilot.MaxStepClicks && p.Stopped && p.Reason == "stuck", "art pilot: a click that changes nothing is repeated only " + ArtPilot.MaxStepClicks + " times, then it stops (" + made + ")");
      // whatever the screen says, no action of the pilot is on a button it never presses
      var screens = new List<ArtView>();
      var kinds = new[] { null, new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true }, artSel(0), artSel(2), artSel(3), ivF, new InventoryScreen { Open = true, ArtTab = true, ArtBulkKnown = true, ArtMode = 2, ArtBulk = true } };
      var wss = new ArtWorkshopScreen[] { null, Wk(5001, 1, null, null, null, null), Wk(5001, 2, matsA, lvA, new int[0], idsA), Wk(5001, 2, matsA, lvA, new[] { 2 }, idsA), Wk(5001, 2, matsA, lvA, new[] { 2, 1 }, idsA), Wk(5001, 2, matsA, lvA, new[] { 2, 3 }, idsA),
        new ArtWorkshopScreen { Open = true, Tab = 1, Uid = 5001, Level = 5 }, Wk(9999, 2, matsA, lvA, new int[0], idsA) };
      int acts = 0, bad = 0;
      foreach (var ik in kinds) foreach (var wk in wss) {
        var v = VN(ik, wk); var pp = new ArtPilot(); mv = pp.Step(v);
        if (mv.Action.Kind == AutoKind.None) continue;
        acts++;
        bool onWs = mv.Todo == ArtTodo.PromoteTab || mv.Todo == ArtTodo.MatCell;   // the workshop is where those buttons are
        if (onWs && (ClickGate.OnNeverButton(geo, mv.Action.X, mv.Action.Y, W, H, U) || (mv.Action.Kind == AutoKind.Drag && ClickGate.OnNeverButton(geo, mv.Action.X, mv.Action.Y + mv.Action.DY, W, H, U)))) bad++;
      }
      Check(acts >= 6 && bad == 0, "art pilot: of " + acts + " actions over every screen, none lies on «Продвинуть» / «Улучшить» (" + bad + ")");
    }

    // --- the buttons never pressed: in no whitelist, and no click point of the artifact flows is inside them
    foreach (string nm in Enum.GetNames(typeof(ClickTarget))) Check(!(nm.Contains("PromoteBtn") || nm.Contains("PromoteButton") || nm.Contains("Upgrade") || nm.Contains("Enhance") || nm.Contains("SellButton") || nm.Contains("SelectAll") || nm.Contains("Lock") || nm.EndsWith("Break")), "art gate: no target named " + nm + " is a final button, «Все» or a lock");
    foreach (var nk in ClickGate.NeverKeys) {
      bool placed = false;
      foreach (ClickTarget tg in Enum.GetValues(typeof(ClickTarget))) if (ClickGate.KeyOf(tg) == nk) placed = true;
      Check(!placed, "art gate: the button key " + nk + " places no target");
      foreach (ClickOwner ow in Enum.GetValues(typeof(ClickOwner))) Check(!ClickGate.WhitelistText(ow).Contains(nk) && !ClickGate.WhitelistText(ow).Contains("PromoteBtn"), "art gate: " + ow + " whitelist does not name " + nk);
    }
    foreach (ClickTarget tg in Enum.GetValues(typeof(ClickTarget))) {
      string key = ClickGate.KeyOf(tg); if (key == "") continue;
      var rc = key == "inventory.artBulkSell" ? null : ArtFrames.Rect(null, key, W, H, U);
      double cx, cy;
      if (rc != null) { cx = rc.CX; cy = rc.CY; } else { var pb = SellHint.ArtBulkRect(W, H, U); cx = pb[0] + pb[2] / 2.0; cy = pb[1] + pb[3] / 2.0; }
      Check(!ClickGate.OnNeverButton(null, cx, cy, W, H, U), "art gate: the middle of " + tg + " (" + key + ") is not on «Продвинуть» / «Улучшить»");
    }
    var pbtn = ArtFrames.Rect(null, "artifact.promoteBtn", W, H, U);
    Check(ClickGate.OnNeverButton(null, pbtn.CX, pbtn.CY, W, H, U) && !ClickGate.OnNeverButton(null, 5, 5, W, H, U), "art gate: the guard knows «Продвинуть» (research numbers) and lets the rest of the screen be");
    string rw = Path.Combine(WebRoot(), "data", "app-geometry", "1.6.39.829.1.json");
    if (File.Exists(rw)) {
      var rg = GameGeometry.Parse(File.ReadAllText(rw, Encoding.UTF8), "1.6.39.829.1");
      var pb2 = rg != null ? ArtFrames.Rect(rg, "artifact.promoteBtn", W, H, U) : null;
      Check(pb2 != null && ClickGate.OnNeverButton(rg, pb2.CX, pb2.CY, W, H, U), "art gate: the guard knows «Продвинуть» by the site's geometry too");
      bool ok = true;
      foreach (ClickTarget tg in Enum.GetValues(typeof(ClickTarget))) { string key = ClickGate.KeyOf(tg); if (key == "" || key == "inventory.artBulkSell") continue; var rc = ArtFrames.Rect(rg, key, W, H, U); if (rc == null || ClickGate.OnNeverButton(rg, rc.CX, rc.CY, W, H, U)) ok = false; }
      Check(ok, "art gate: with the site's geometry no artifact button's middle is on «Продвинуть» / «Улучшить»");
    }
    var wsEss = new ArtWorkshopScreen(); Check(wsEss.MatItemIds.Count == 0, "art workshop: item ids start empty");
  }

  // Storage cleanup without auto click (src/SellHint.cs): the step and the frames from what the inventory shows
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

  static void SellPilotTests() {
    Console.WriteLine("Sell pilot");
    var g = new SellGeometry();   // as measured on the game
    var game = new FakeSellGame(g, 150, 60);
    var rnd = new Random(3); var all = new List<long>(game.Pos.Keys); var want = new HashSet<long>();
    while (want.Count < 60) want.Add(all[rnd.Next(all.Count)]);
    var p = new SellPilot(g);
    string st = RunSell(p, game, want, 30000);
    Check(st == "done" && want.IsSubsetOf(game.Sel) && game.Sel.Count == want.Count && p.Extra == 0,
          "60 items over 150 rows (all parts): all selected, nothing else (" + game.Clicks + " clicks, " + game.Drags + " drags, " + st + ")");
    Check(game.Clicks <= want.Count * 2.0, "few extra clicks with the rows' phase from the screen (" + game.Clicks + " for " + want.Count + ", «Быстрое улучшение» closed too)");
    Check(game.DangerClicks == 0, "never a click on «Быстрое улучшение» (it opens after a click on an item below +16)");

    // only rings and amulets: the part filter first (a shorter list), then the items
    var gp = new FakeSellGame(g, 150, 0); var wp = new HashSet<long>();
    foreach (var kv in gp.PartOf) if ((kv.Value == 3 || kv.Value == 4) && kv.Key % 7 == 0) wp.Add(kv.Key);
    var pp = new SellPilot(g);
    string sp = RunSell(pp, gp, wp, 30000);
    Check(sp == "done" && gp.Parts.SetEquals(new[] { 3, 4 }) && wp.IsSubsetOf(gp.Sel) && gp.Sel.Count == wp.Count,
          "two parts only: the list filtered to them, then all " + wp.Count + " selected (" + gp.Drags + " drags, " + sp + ")");

    // the player's filter hides some: «Сбросить» once, before anything is selected
    var gh = new FakeSellGame(g, 30, 0) { Hidden = true }; gh.Build(); var wh = new HashSet<long> { 5001, 5050, 5100 };
    var ph = new SellPilot(g);
    string sh = RunSell(ph, gh, wh, 5000);
    Check(sh == "done" && wh.IsSubsetOf(gh.Sel) && gh.Sel.Count == 3, "items hidden by the player's filter: «Сбросить», then selected (" + sh + ")");

    // no phase on the screen (drags only estimated): still done, by the clicks' answers
    var gn = new FakeSellGame(g, 80, 30); var wn = new HashSet<long>(); var alln = new List<long>(gn.Pos.Keys);
    while (wn.Count < 40) wn.Add(alln[rnd.Next(alln.Count)]);
    var pn = new SellPilot(g);
    string sn = RunSell(pn, gn, wn, 30000, false);
    Check(sn == "done" && wn.IsSubsetOf(gn.Sel) && gn.Sel.Count == wn.Count, "without the screen's phase: all selected, nothing else (" + gn.Clicks + " clicks, " + sn + ")");

    // the list is not at the top when the pilot starts (the player scrolled it): a neighbour hit is taken back at once
    var off = new FakeSellGame(g, 40, 0); off.Scroll = 0.6 * g.PitchY * 1009;
    var w2 = new HashSet<long>(); foreach (var kv in off.Pos) if (kv.Value[0] % 3 == 0 && kv.Value[1] % 2 == 0) w2.Add(kv.Key);
    var p2 = new SellPilot(g);
    string st2 = RunSell(p2, off, w2, 20000, false);
    Check(st2 == "done" && w2.IsSubsetOf(off.Sel) && off.Sel.Count == w2.Count, "list scrolled by the player: neighbours hit by mistake are taken back, all selected (" + st2 + ")");

    // the last rows: the list stops at its end, the pilot clicks them there (no endless drags, no stray clicks)
    var ge = new FakeSellGame(g, 60, 0); var we = new HashSet<long>(); foreach (var kv in ge.Pos) if (kv.Value[0] >= 58 || kv.Value[0] == 2) we.Add(kv.Key);
    var pe = new SellPilot(g);
    string se = RunSell(pe, ge, we, 20000);
    Check(se == "done" && we.IsSubsetOf(ge.Sel) && ge.Sel.Count == we.Count, "items in the last rows (the list's end): all selected, nothing else (" + se + ", " + ge.Drags + " drags, missing " + pe.Missing + ")");

    // the player's own choice stays and is reported (and no filter click then: it would clear it)
    var g3 = new FakeSellGame(g, 20, 0); g3.Sel.Add(5003); var w3 = new HashSet<long> { 5001, 5010, 99999 };
    var p3 = new SellPilot(g);
    string st3 = RunSell(p3, g3, w3, 5000);
    Check(st3 == "done" && g3.Sel.Contains(5001) && g3.Sel.Contains(5010) && g3.Sel.Contains(5003) && p3.Extra == 1 && p3.Missing == 1 && g3.FilterClicks == 0,
          "the player's own choice kept (extra 1, no filter clicks), an item not in the list counted missing");

    // the inventory's bulk sale (the app's way): 8 columns, no pop-up, no filter; 1920×1009 and a 4:3-ish 1300×1000
    foreach (var sz in new[] { new[] { 1920.0, 1009 }, new[] { 1300.0, 1000 } }) {
      double W = sz[0], H = sz[1], U = Ui.Unit(W, H);
      var gi = SellGeometry.Inventory(W, U, 8);
      var gv = new FakeSellGame(gi, 130, 0, 8) { W = W, H = H, U = U, HasPopup = false };
      var wi = new HashSet<long>(); var ri = new Random(7); var alli = new List<long>(gv.Pos.Keys);
      while (wi.Count < 150) wi.Add(alli[ri.Next(alli.Count)]);
      foreach (var kv in gv.Pos) if (kv.Value[0] >= 129) wi.Add(kv.Key);   // the list's last rows too
      var pi = new SellPilot(gi);
      string si = RunSell(pi, gv, wi, 40000);
      Check(si == "done" && wi.IsSubsetOf(gv.Sel) && gv.Sel.Count == wi.Count && gv.FilterClicks == 0,
            "inventory " + W + "×" + H + ": " + wi.Count + " items over 130 rows selected, nothing else, no filter clicks (" + gv.Clicks + " clicks, " + gv.Drags + " drags, " + si + ")");
      Check(gv.Clicks <= wi.Count * 1.25, "inventory: about one click per item (" + gv.Clicks + " for " + wi.Count + ")");
    }
    // the game has 9 columns where the pilot counts 8: the first click lands in another column - it is taken back, stop
    {
      double W = 1920, H = 1009, U = 1009;
      var g8 = SellGeometry.Inventory(W, U, 8); var g9 = SellGeometry.Inventory(W, U, 9);
      var gw = new FakeSellGame(g8, 40, 0, 9) { W = W, H = H, U = U, RealX0 = g9.X0, RealPitchX = g9.PitchX, HasPopup = false };
      var ww = new HashSet<long>(); foreach (var kv in gw.Pos) if (kv.Value[1] >= 6 && kv.Value[0] % 2 == 0) ww.Add(kv.Key);
      var screen = gw.Screen(); screen.Pos.Clear();   // the pilot's own idea of the list: rows of 8
      var pw = new SellPilot(g8); long nowW = 0; string sw8 = "timeout"; var order = new List<long>(); foreach (var kv in gw.Pos) order.Add(kv.Key); order.Sort();
      for (int i = 0; i < 5000; i++) {
        var sc = gw.Screen(); sc.Pos.Clear(); for (int k = 0; k < order.Count; k++) sc.Pos[order[k]] = new[] { k / 8 + 1, k % 8 + 1 };
        var t8 = new int[(order.Count + 7) / 8 + 1]; for (int r = 1; r < t8.Length; r++) t8[r] = 1; sc.Types = t8;
        var a = pw.Step(new SellView { NowMs = nowW, Foreground = true, W = W, H = H, U = U, Screen = sc, Wanted = ww, Phase = double.NaN });
        gw.Act(a); nowW += 100;   // (a stopped pilot may still take a stray item back: the app performs that click)
        if ((pw.State == "done" || pw.State == "failed") && a.Kind == AutoKind.None) { sw8 = pw.State; break; }
      }
      bool noExtra = true; foreach (var u in gw.Sel) if (!ww.Contains(u)) noExtra = false;
      Check(sw8 == "failed" && noExtra, "wrong column count: stopped with nothing extra selected (" + sw8 + ", " + gw.Sel.Count + " selected)");
    }

    // never clicks without the game in front or while the player moves the mouse
    var g4 = new FakeSellGame(g, 10, 0); var p4 = new SellPilot(g);
    var a4 = p4.Step(new SellView { NowMs = 0, Foreground = false, W = 1920, H = 1009, U = 1009, Screen = g4.Screen(), Wanted = new HashSet<long> { 5001 } });
    var a5 = p4.Step(new SellView { NowMs = 100, Foreground = true, UserBusy = true, W = 1920, H = 1009, U = 1009, Screen = g4.Screen(), Wanted = new HashSet<long> { 5001 } });
    Check(a4.Kind == AutoKind.None && a5.Kind == AutoKind.None, "no clicks with the game behind or the player busy");
  }

  static void HeroPilotTests() {
    var g = new HeroGeometry(); double W = 1920, H = 1009;
    {   // live 1.6.66 (3440x1440): a faction filter hid the plan's hero, its «Все» brought the hero up (the game shows the first hero of
        // the new grid) - the pilot said done with the grid's filter pop-up still open over the gear slots, the slot did not open
        // after 3 clicks. The pop-up is closed with the funnel first, then done.
      var allF = new long[60]; var tagsF = new Dictionary<long, long[]>();
      for (int i = 0; i < allF.Length; i++) { allF[i] = (200100 + i) * 100000L; tagsF[allF[i]] = new long[] { HeroGeometry.ClassOrder[i % 6], FakeHeroGame.FactionList[1 + i % 10] }; }
      var gp = new FakeHeroGame(g, W, H) { All = allF, Tags = tagsF, SelectFirst = true };
      gp.Camps.Add(FakeHeroGame.FactionList[5]); gp.Refilter();   // the plan's hero (faction 1) is hidden; the grid shows a faction-5 hero
      Check(Array.IndexOf(gp.Heroes, allF[0]) < 0 && gp.Shown != allF[0], "pop-up case: the plan's hero starts hidden by the faction filter");
      HeroView lvp; string stp = RunHero(new HeroPilot(g), gp, allF[0], 3000, out lvp, tagsF[allF[0]]);
      Check(stp == "done" && gp.Shown == allF[0] && !gp.FilterOpen, "the filter's «Все» brings up the plan's hero: its pop-up closed before done (" + stp + ", pop-up open " + gp.FilterOpen + ")");
      // a pop-up the read keeps seeing (it does not close, or a misread): one funnel click, never a second (that would open it again)
      var hp2 = new HeroPilot(g); int funnels = 0; string st2 = "";
      for (long t = 0; t < 10000; t += 100) {
        var a = hp2.Step(new HeroView { NowMs = t, Foreground = true, W = W, H = H, ClientH = H, Plan = allF[0], Hero = allF[0], Tab = 3, Part = -1, Heroes = allF, FilterOpen = true });
        if (a == null) { st2 = hp2.State; break; }
        if (a.Kind == AutoKind.Click && Math.Abs(a.X - g.FunnelX * H) < 2) funnels++;
      }
      Check(st2 == "done" && funnels == 1, "pop-up seen open at the plan's hero: one funnel click, then done (" + funnels + " clicks, " + st2 + ")");
      // a stop says why (the report had an empty reason)
      var gb0 = new FakeHeroGame(g, W, H) { Heroes = allF, Shown = allF[7], Blocked = true };
      var pb0 = new HeroPilot(g);
      for (long t = 0; t < 20000 && pb0.State != "failed"; t += 100) {
        var v0 = new HeroView { NowMs = t, Foreground = true, W = W, H = H, ClientH = H, Plan = allF[5], Hero = gb0.Shown, Tab = 3, Part = -1, Heroes = gb0.Heroes, GridPtr = gb0.Grid };
        var a0 = pb0.Step(v0); if (a0 != null && a0.Kind == AutoKind.Click) gb0.Click(a0.X, a0.Y);
      }
      Check(pb0.State == "failed" && pb0.Why.Contains("clicks") && pb0.Detail(new HeroView { Plan = allF[5], Hero = allF[7], Heroes = allF }).Contains("plan@5 shown@7"), "hero pilot stop: a reason and a detail line (" + pb0.Why + ")");
    }
    {   // noscreen does not stick: the city count starts again once the hero screen was reached; the game behind: waits
      var hc = new HeroPilot(g); long plan = 200100 * 100000L;
      Func<long, long, bool, bool, HeroView> hv = (t, hero, btn, fg) => new HeroView { NowMs = t, Foreground = fg, W = W, H = H, ClientH = H, Plan = plan, Hero = hero, HeroesButton = btn };
      int cityClicks = 0; long ct = 0;
      for (int i = 0; i < 2; i++) { ct += 4000; if (hc.Step(hv(ct, 0, true, true)).Kind == AutoKind.Click) cityClicks++; }
      Check(cityClicks == 2, "hero pilot: two «Герои» clicks");
      ct += 4000; hc.Step(hv(ct, 1, false, true));   // the hero screen was reached
      ct += 4000; var ha = hc.Step(hv(ct, 0, true, true));
      Check(hc.State == "work" && ha != null && ha.Kind == AutoKind.Click, "hero pilot: city again after the hero screen: work + click, not noscreen (" + hc.State + ")");
      var hb = new HeroPilot(g); var hba = hb.Step(hv(1000, 0, false, false));
      Check(hb.State == "background" && hba != null && hba.Kind != AutoKind.Click, "hero pilot: game not in front: background, not noscreen (" + hb.State + ")");
    }
    var heroes = new long[130]; for (int i = 0; i < heroes.Length; i++) heroes[i] = (200100 + i) * 100000L;
    HeroView lv;

    {   // the shown hero in the middle column at the grid's middle (live 27.09, Элизия / Хассу): the first probe must not
        // keep clicking its own card; the plan's hero near the top, half scrolled out
      int okMid = 0, nMid = 0;
      for (double sc = 0; sc <= 400; sc += 25) {
        var gm = new FakeHeroGame(g, W, H) { Heroes = heroes, Scroll = sc };
        double mid = (g.ViewTop * H + H - (1 - g.ViewBottom) * H) / 2, rel = mid - (g.Row1Top * H - sc);
        int row = (int)Math.Floor(rel / (g.PitchY * H)); if (row < 0) continue;
        gm.Shown = heroes[row * 3 + 1];
        nMid++;
        if (RunHero(new HeroPilot(g), gm, heroes[1], 3000, out lv) == "done" && gm.Shown == heroes[1]) okMid++;
      }
      Check(okMid == nMid, "shown hero under the first probe: " + okMid + "/" + nMid + " found");
    }
    {   // clicks do nothing for 20 s (a pop-up over the grid, live 27.09): failed, then tried again once it is gone
      var gb = new FakeHeroGame(g, W, H) { Heroes = heroes, Shown = heroes[7], Blocked = true };
      var pb = new HeroPilot(g); bool sawFail = false; string stb = "";
      for (long t = 0; t < 60000; t += 100) {
        if (t >= 20000) gb.Blocked = false;
        var v = new HeroView { NowMs = t, Foreground = true, W = W, H = H, ClientH = H, Plan = heroes[5], Hero = gb.Shown, Tab = gb.Tab, Part = gb.Part, Heroes = gb.Heroes, GridPtr = gb.Grid };
        var a = pb.Step(v);
        if (pb.State == "failed") sawFail = true;
        if (a == null) { stb = pb.State; break; }
        if (a.Kind == AutoKind.Click) gb.Click(a.X, a.Y); else if (a.Kind == AutoKind.Drag) gb.Drag(a.DY);
      }
      Check(sawFail && stb == "done" && gb.Shown == heroes[5], "blocked grid: failed, retried after the pause -> " + stb);
    }

    {   // live 2026-10-18 (1.6.62): 720 heroes in the grid, the plan's hero (Count Dracula) at place 4, the grid left scrolled at place ~233
        // by the last hero; the game's grid follows the pointer about twice as far as it is dragged
      var big = new long[720]; for (int i = 0; i < big.Length; i++) big[i] = (200100 + i) * 100000L;
      foreach (double gn in new[] { 0.4, 0.5, 0.7, 1.0, 2.0 }) {
        var gb = new FakeHeroGame(g, W, H) { Heroes = big, Shown = big[233], Gain = gn };
        gb.Scroll = Math.Min(gb.MaxScroll, (233 / 3) * g.PitchY * H - g.ViewTop * H * 2);
        string sb2 = RunHero(new HeroPilot(g), gb, big[4], 3000, out lv);
        Check(sb2 == "done" && gb.Shown == big[4], "720 heroes, the plan's hero at place 4, the grid left at place 233, no tags, drag gain " + gn + ": " + sb2 + " (" + gb.Clicks + " clicks, " + gb.Drags + " drags)");
      }
      // with the hero's class and faction known: the grid is filtered first (the hero is near the top of the grid, but the view is far from it)
      var tagsBig = new Dictionary<long, long[]>(); var rb = new Random(3);
      foreach (var u in big) tagsBig[u] = new long[] { HeroGeometry.ClassOrder[rb.Next(6)], FakeHeroGame.FactionList[1 + rb.Next(10)] };
      var gf = new FakeHeroGame(g, W, H) { All = big, Tags = tagsBig, Gain = 1 }; gf.Refilter();
      gf.Shown = big[233]; gf.Scroll = Math.Min(gf.MaxScroll, (233 / 3) * g.PitchY * H - g.ViewTop * H * 2);
      string sf2 = RunHero(new HeroPilot(g), gf, big[4], 3000, out lv, tagsBig[big[4]]);
      Check(sf2 == "done" && gf.Shown == big[4] && gf.Class == tagsBig[big[4]][0] && gf.Drags <= 2, "720 heroes, the plan's hero at place 4, the grid left at place 233, tags known: filtered first (" + sf2 + ", " + gf.Clicks + " clicks, " + gf.Drags + " drags)");
      // the same hero with the view at the top: no filter, it is right there
      var gn2 = new FakeHeroGame(g, W, H) { All = big, Tags = tagsBig, Gain = 1 }; gn2.Refilter(); gn2.Shown = big[8];
      string sn2 = RunHero(new HeroPilot(g), gn2, big[4], 3000, out lv, tagsBig[big[4]]);
      Check(sn2 == "done" && gn2.Shown == big[4] && gn2.Class == 0 && gn2.Camps.Count == 0, "the plan's hero a few rows from the view: no filter (" + sn2 + ")");
    }

    var game = new FakeHeroGame(g, W, H) { Heroes = heroes, Shown = heroes[4], Part = 2 };
    string st = RunHero(new HeroPilot(g), game, heroes[100], 3000, out lv);
    Check(st == "done" && game.Shown == heroes[100] && game.Tab == 3, "gear list open, hero 101 of 130 far below: back, scroll, click -> " + st
      + " (" + game.Clicks + " clicks, " + game.Drags + " drags)");
    Check(game.Backs == 1, "the back arrow once (not twice: that would close the hero screen)");
    Check(game.Clicks <= 12, "few clicks: " + game.Clicks);

    int ok = 0, worst = 0; var rnd = new Random(7);
    for (int k = 0; k < 40; k++) {
      var gm = new FakeHeroGame(g, W, H) { Heroes = heroes, Shown = heroes[rnd.Next(130)], Gain = 0.85 + rnd.NextDouble() * 0.3 };
      gm.Scroll = rnd.NextDouble() * gm.MaxScroll;
      long want = heroes[rnd.Next(130)];
      if (RunHero(new HeroPilot(g), gm, want, 3000, out lv) == "done" && gm.Shown == want) ok++;
      worst = Math.Max(worst, gm.Clicks + gm.Drags);
    }
    Check(ok == 40, "random scroll, target and drag gain (0.85-1.15): " + ok + "/40, worst " + worst + " actions");

    int okSmall = 0; var rs = new Random(11);
    for (int k = 0; k < 20; k++) {
      var gs = new FakeHeroGame(g, W, H) { Heroes = heroes, Shown = heroes[rs.Next(130)], Small = true };
      gs.Scroll = rs.NextDouble() * gs.MaxScroll; long want = heroes[rs.Next(130)];
      if (RunHero(new HeroPilot(g), gs, want, 3000, out lv) == "done" && gs.Shown == want) okSmall++;
    }
    Check(okSmall == 20, "small cards (shorter rows): " + okSmall + "/20");
    // the grid filter: every hero has a class and a faction
    var tagsAll = new Dictionary<long, long[]>(); var rt = new Random(5);
    foreach (var u in heroes) tagsAll[u] = new long[] { HeroGeometry.ClassOrder[rt.Next(6)], FakeHeroGame.FactionList[1 + rt.Next(10)] };
    Func<FakeHeroGame> mk = () => { var f = new FakeHeroGame(g, W, H) { All = heroes, Tags = tagsAll, Shown = heroes[2] }; f.Refilter(); return f; };
    var fg1 = mk(); long far = heroes[125];
    string sf = RunHero(new HeroPilot(g), fg1, far, 3000, out lv, tagsAll[far]);
    Check(sf == "done" && fg1.Shown == far && fg1.Class == tagsAll[far][0] && !fg1.FilterOpen && fg1.Drags <= 1,
          "hero far down: the grid filtered by its class and faction, then picked (" + fg1.Clicks + " clicks, " + fg1.Drags + " drags)");
    // the next plan's hero is not in that filtered grid: the filter is cleared, then it is found
    long next = heroes[3]; if (Array.IndexOf(fg1.Heroes, next) >= 0) next = heroes[4];
    string sn = RunHero(new HeroPilot(g), fg1, next, 3000, out lv, null);
    Check(sn == "done" && fg1.Shown == next, "the next plan's hero hidden by the filter left from before: cleared, found");
    var fg2 = mk(); fg2.Camps.Add(101); fg2.Class = 8; fg2.Refilter(); long hid = heroes[40];
    if (Array.IndexOf(fg2.Heroes, hid) >= 0) { fg2.Camps.Clear(); fg2.Camps.Add(tagsAll[hid][1] == 101 ? 102 : 101); fg2.Refilter(); }
    string sh = RunHero(new HeroPilot(g), fg2, hid, 3000, out lv, null);
    Check(sh == "done" && fg2.Shown == hid, "hero hidden by the player's grid filter: «Все», then found (" + fg2.Clicks + " clicks)");
    var fg3 = mk(); long wrong = heroes[110]; var badTags = new long[] { tagsAll[wrong][0] == 1 ? 2 : 1, tagsAll[wrong][1] };
    string sw = RunHero(new HeroPilot(g), fg3, wrong, 3000, out lv, badTags);
    Check(sw == "done" && fg3.Shown == wrong, "wrong class in the tags (a game patch): the hero vanishes, «Все» undoes it, found anyway");
    // the pop-up is never recognised (a game build that draws it differently): the funnel clicks run out, then a stop - no click
    // inside the pop-up's area (a grid click there picked a wrong class filter), whatever the hero
    var fg4 = mk(); fg4.Blind = true; long blindHero = heroes[125];
    var hp4 = new HeroPilot(g);
    string s4 = RunHero(hp4, fg4, blindHero, 3000, out lv, tagsAll[blindHero]);
    int inPop = 0; foreach (var pt in fg4.Pts) if (!(Math.Abs(pt[0] - g.FunnelX * H) < 20 && Math.Abs(pt[1] - (H - g.FunnelDY * H)) < 20) && pt[0] < 0.73 * H && pt[1] > 0.16 * H && pt[1] < 0.87 * H) inPop++;
    Check(s4 == "failed" && fg4.Clicks == 3 && inPop == 0 && hp4.Why != "", "pop-up never recognised: 3 funnel clicks, then a stop, no click inside the pop-up area (" + s4 + ", " + fg4.Clicks + " clicks, " + inPop + " inside)");
    {   // HeroFilterLook: 1.6.46 at 526×296 - no Class|Faction divider, a blue «Все» cell in the class column
      double U = 295.875;
      Func<bool, bool, bool, bool, double, double, Func<int, int, int, int, int[]>> frame2 = (strip, filterDiv, blueCell, classDiv, blueX0, blueRow) => (gx, gy, gw, gh) => {
        var a = new int[gw * gh];
        for (int j = 0; j < gh; j++) for (int i = 0; i < gw; i++) {
          int x = gx + i, y = gy + j, c = (30 << 16) | (30 << 8) | 40;
          if (!strip) c = (120 << 16) | (110 << 8) | 100;   // hero cards instead of the dark pop-up
          if (filterDiv && Math.Abs(x - (int)(0.4836 * U)) <= 1 && y > 0.16 * U && y < 0.87 * U) c = (130 << 16) | (130 << 8) | 130;
          if (classDiv && Math.Abs(x - (int)(0.2557 * U)) <= 1 && y > 0.16 * U && y < 0.87 * U) c = (130 << 16) | (130 << 8) | 130;
          if (blueCell && x > blueX0 * U && x < blueX0 * U + 0.2 * U && Math.Abs(y - (0.2379 + blueRow * 0.1004) * U) < 0.05 * U) c = (30 << 16) | (90 << 8) | 170;
          a[j * gw + i] = c;
        }
        return a;
      };
      Func<bool, bool, bool, bool, Func<int, int, int, int, int[]>> frame = (a1, a2, a3, a4) => frame2(a1, a2, a3, a4, 0.04, 0);
      Check(HeroFilterLook.Seen(frame2(true, true, true, false, 0.04, 3), U), "filter pop-up seen: a class chosen (row 3 blue, «Все» not), no Класс|Фракция divider");
      Check(HeroFilterLook.Seen(frame2(true, true, true, false, 0.30, 0), U), "filter pop-up seen: only the faction column's «Все» is blue");
      Check(HeroFilterLook.Seen(frame(true, true, true, false), U), "filter pop-up seen: dark strip + Фракция|Фильтр divider + blue «Все» cell, no Класс|Фракция divider (1.6.46)");
      Check(HeroFilterLook.Seen(frame(true, true, false, true), U), "filter pop-up seen: older layout (both dividers, no blue cell)");
      Check(!HeroFilterLook.Seen(frame(true, false, false, false), U), "dark filtered grid without the Фильтр divider: not the pop-up");
      Check(!HeroFilterLook.Seen(frame(true, false, true, true), U), "no Фильтр divider: not the pop-up even with the other marks");
      Check(!HeroFilterLook.Seen(frame(true, true, false, false), U), "Фильтр divider alone (a grid line): not the pop-up");
      Check(!HeroFilterLook.Seen(frame(false, true, true, true), U), "no dark strip: not the pop-up");
    }
    var top = new FakeHeroGame(g, W, H) { Heroes = heroes, Shown = heroes[120] }; top.Scroll = top.MaxScroll;
    Check(RunHero(new HeroPilot(g), top, heroes[0], 3000, out lv) == "done" && top.Shown == heroes[0], "grid at its end, the first hero: up to the top");

    var tab = new FakeHeroGame(g, W, H) { Heroes = heroes, Shown = heroes[3], Tab = 1 };
    Check(RunHero(new HeroPilot(g), tab, heroes[3], 100, out lv) == "done" && tab.Tab == 3 && tab.Clicks == 1, "the right hero on another tab: «Снаряжение» once");

    var none = new FakeHeroGame(g, W, H) { Heroes = heroes, Shown = 0 };
    Check(RunHero(new HeroPilot(g), none, heroes[3], 50, out lv) == "noscreen" && none.Clicks == 0, "hero screen closed, no «Герои» button seen: nothing clicked, the player is asked");
    // the main city with its «Герои» button: pressed, the hero screen opens with the last hero, then as usual
    var cityG = new FakeHeroGame(g, W, H) { Heroes = heroes, Shown = 0, City = true, CityHero = heroes[40] };
    string stC = RunHero(new HeroPilot(g), cityG, heroes[2], 3000, out lv);
    Check(stC == "done" && cityG.Shown == heroes[2] && !cityG.City, "from the city: «Герои», then the hero (" + cityG.Clicks + " clicks)");

    var gone = new FakeHeroGame(g, W, H) { Heroes = heroes, Shown = heroes[1] };
    Check(RunHero(new HeroPilot(g), gone, 999L, 50, out lv) == "notfound" && gone.Clicks == 0, "hero not in the grid (the game's hero filter): nothing clicked");
    var idle = new FakeHeroGame(g, W, H) { Heroes = heroes, Shown = heroes[1] };
    Check(RunHero(new HeroPilot(g), idle, 0, 50, out lv) == "idle" && idle.Clicks == 0, "no started plan: the pilot leaves the hero screen alone");

    // the player clicks: the pilot steps back
    var hp = new HeroPilot(g);
    var busy = new HeroView { NowMs = 0, Foreground = true, W = W, H = H, Plan = heroes[50], Hero = heroes[1], Tab = 3, Part = -1, Heroes = heroes, UserClicked = true };
    var ba = hp.Step(busy);
    Check(ba != null && ba.Kind == AutoKind.None && hp.State == "paused", "the player clicked: paused");
    busy.UserClicked = false; busy.NowMs = HeroPilot.UserPauseMs + 100;
    ba = hp.Step(busy);
    Check(ba != null && ba.Kind == AutoKind.Click, "then goes on");
    var bg = new HeroPilot(g);
    ba = bg.Step(new HeroView { NowMs = 0, Foreground = false, W = W, H = H, Plan = heroes[50], Hero = heroes[1], Tab = 3, Heroes = heroes });
    Check(ba != null && ba.Kind == AutoKind.None && bg.State == "background", "game not in front: waits");
  }

  // The game's filter pop-up as the pilot sees it: clicks at the measured places toggle what they toggle in the game.
  sealed class FakeFilterGame {
    public bool Open, Side, SetSide, SubSide, HideEq;
    public List<long> Suits = new List<long>(), Mains = new List<long>(), Subs = new List<long>();
    public long[] SubList;
    public long[] SetList, StatList;
    public int SetShift;   // a wrong layout: a set click lands this many places off
    public double SetScroll;   // rows the sets list is scrolled by
    public double DragGain = 1;
    readonly FilterGeometry g; readonly double W, H;
    public FakeFilterGame(FilterGeometry g, double W, double H) { this.g = g; this.W = W; this.H = H; }
    public void Drag(double dy) {
      if (!(Side && SetSide)) return;
      int rows = (SetList.Length + 1) / 2;
      SetScroll = Math.Max(0, Math.Min(rows - FilterGeometry.SetRows, Math.Round(SetScroll - dy * DragGain / (g.SetPitch * H))));
    }
    static bool Near(double x, double y, double px, double py) { return Math.Abs(x - px) < 12 && Math.Abs(y - py) < 12; }
    static void Toggle(List<long> l, long id) { if (l.Contains(id)) l.Remove(id); else l.Add(id); }
    public void Click(double x, double y) {
      if (!Open) { if (Near(x, y, 0.2185 * H, 0.9275 * H)) Open = true; return; }
      // the × drops the filter (the game applies it only while the pop-up is open)
      if (Near(x, y, g.X(g.MainCloseX, W, H), g.Y(g.CloseY, W, H))) { Open = Side = SetSide = SubSide = false; Suits.Clear(); Mains.Clear(); Subs.Clear(); return; }
      if (Side && Near(x, y, g.X(g.SideCloseX, W, H), g.Y(g.CloseY, W, H))) { Side = SetSide = SubSide = false; return; }   // keeps the filter
      if (Near(x, y, g.X(g.ResetX, W, H), g.Y(g.ResetY, W, H))) { Suits.Clear(); Mains.Clear(); Subs.Clear(); return; }
      if (Near(x, y, g.X(g.SubArrowX, W, H), g.Y(g.SubArrowY, W, H))) { bool was = Side && SubSide; Side = SubSide = !was; SetSide = false; return; }
      if (Near(x, y, g.X(g.SetArrowX, W, H), g.Y(g.SetArrowY, W, H))) { bool was = Side && SetSide; Side = SetSide = !was; return; }   // the list stays scrolled
      if (Near(x, y, g.X(g.StatArrowX, W, H), g.Y(g.StatArrowY, W, H))) { bool was = Side && !SetSide && !SubSide; Side = !was; SetSide = SubSide = false; return; }
      if (Near(x, y, g.X(g.HideEquippedX, W, H), g.Y(g.HideEquippedY, W, H))) { HideEq = !HideEq; return; }
      if (Side && SetSide)
        for (int i = 0; i < 2 * FilterGeometry.SetRows; i++) {
          var c = g.SetCell(i, W, H); if (!Near(x, y, c[0], c[1])) continue;
          int k = (int)Math.Round(i / 2 + SetScroll) * 2 + i % 2 + SetShift;
          if (k >= 0 && k < SetList.Length) Toggle(Suits, SetList[k]);
          return;
        }
      if (Side && SubSide && SubList != null)
        for (int i = 0; i < SubList.Length; i++) { var c = g.StatCell(i, W, H); if (Near(x, y, c[0], c[1])) { if (Subs.Contains(SubList[i]) || Subs.Count < 4) Toggle(Subs, SubList[i]); return; } }
      if (Side && !SetSide && !SubSide)
        for (int i = 0; i < StatList.Length; i++) { var c = g.StatCell(i, W, H); if (Near(x, y, c[0], c[1])) { Toggle(Mains, StatList[i]); return; } }
    }
  }

  // Runs the pilot against the fake game until it lets go; returns the clicks made.
  static int RunFilter(FilterPilot p, FakeFilterGame game, long setId, long statId, bool onOther, bool wanted) { return RunFilter(p, game, setId, statId, onOther, wanted, null); }
  static int RunFilter(FilterPilot p, FakeFilterGame game, long setId, long statId, bool onOther, bool wanted, long[] subIds) {
    int clicks = 0;
    for (long t = 0; t < 120000; t += 100) {
      bool filtered = (setId <= 0 || game.Suits.Contains(setId)) && (statId <= 0 || game.Mains.Contains(statId));
      var v = new FilterView { NowMs = t, Foreground = true, W = 1920, H = 1009, Item = 5, Wanted = wanted && !filtered,
        SetId = setId, StatId = statId, SetIndex = Array.IndexOf(game.SetList, setId), StatIndex = Array.IndexOf(game.StatList, statId),
        Suits = game.Suits.ToArray(), MainAttrs = game.Mains.ToArray(), HideEquipped = game.HideEq, OnOtherHero = onOther, SetOrder = game.SetList,
        SubIds = subIds, SubOrder = game.SubList, SubAttrs = game.Subs.ToArray(),
        PanelOpen = game.Open, SidePanelOpen = game.Open && game.Side, SetPanelOpen = game.Open && game.Side && game.SetSide };
      var a = p.Step(v);
      if (a == null) break;
      if (a.Kind == AutoKind.Click) { clicks++; game.Click(a.X, a.Y); }
      else if (a.Kind == AutoKind.Drag) game.Drag(a.DY);
    }
    return clicks;
  }

  static void FilterPilotTests() {
    var g = new FilterGeometry(); double W = 1920, H = 1009;
    // the ring's lists as the game showed them (sets by m_SuitSort, main stats by id)
    long[] sets = { 723000, 722900, 722800, 722600, 722500, 722200, 722100, 723100, 722400, 722300, 722000, 721900, 721600, 721500,
                    721400, 721300, 721200, 721100, 721000, 720900 };
    long[] stats = { 1, 2, 7, 13, 14, 19, 23, 25, 27 };
    var c = g.SetCell(13, W, H);
    Check(Math.Abs(c[0] - 1690) < 3 && Math.Abs(c[1] - 650) < 3, "«Интуиция» (14th set) where the screenshot has it: " + c[0].ToString("0") + "," + c[1].ToString("0"));
    c = g.StatCell(3, W, H);
    Check(Math.Abs(c[0] - 1685) < 3 && Math.Abs(c[1] - 308) < 3, "«Бонус к АТК» (4th stat) where the screenshot has it: " + c[0].ToString("0") + "," + c[1].ToString("0"));

    var game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats };
    int n = RunFilter(new FilterPilot(g), game, 721500, 13, false, true);
    Check(game.Suits.Count == 1 && game.Suits[0] == 721500 && game.Mains.Count == 1 && game.Mains[0] == 13 && game.Open,
          "set + main stat set, the pop-up left open: its × drops the filter (" + n + " clicks)");
    Check(n <= 7, "few clicks");

    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats };
    game.Suits.Add(723000); game.Mains.Add(1);
    RunFilter(new FilterPilot(g), game, 721500, 13, false, true);
    Check(game.Suits.Count == 1 && game.Suits[0] == 721500 && game.Mains.Count == 1 && game.Mains[0] == 13, "an old filter is reset first");

    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, SetShift = 1 };
    var fp = new FilterPilot(g);
    RunFilter(fp, game, 721500, 13, false, true);
    Check(game.Suits.Count == 0 && game.Mains.Count == 1 && game.Mains[0] == 13 && game.Open,
          "wrong set clicked twice -> reset, the set is skipped, the stat still set, the pop-up open");

    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, HideEq = true };
    RunFilter(new FilterPilot(g), game, 721500, 13, true, true);
    Check(!game.HideEq && game.Open, "item on another hero: «Скрыть надетое» off");

    // the sub stats' side panel is still open: it swallows the click on «Скрыть надетое» - closed first, the filter is not done before
    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, HideEq = true, Open = true, Side = true, SubSide = true };
    var fph = new FilterPilot(g); int swallowed = 0; double hx = g.X(g.HideEquippedX, W, H), hy = g.Y(g.HideEquippedY, W, H);
    for (long t = 0; t < 20000; t += 100) {
      var v = new FilterView { NowMs = t, Foreground = true, W = W, H = H, Item = 5, Wanted = true, Suits = game.Suits.ToArray(), MainAttrs = game.Mains.ToArray(),
        HideEquipped = game.HideEq, OnOtherHero = true, SetOrder = game.SetList, SubOrder = game.SubList, SubAttrs = game.Subs.ToArray(),
        PanelOpen = game.Open, SidePanelOpen = game.Open && game.Side, SetPanelOpen = false };
      var a = fph.Step(v);
      if (a == null) break;
      if (a.Kind == AutoKind.Click) { if (game.Side && Math.Abs(a.X - hx) < 12 && Math.Abs(a.Y - hy) < 12) swallowed++; game.Click(a.X, a.Y); }
    }
    Check(swallowed == 0 && !game.HideEq, "side panel open: it is closed before «Скрыть надетое» is clicked (" + swallowed + " clicks into the panel)");
    // the read of «Скрыть надетое» is stale (always "hidden") while the game already applied the untick: ONE click, never a second one
    foreach (bool listed in new[] { false, true }) {
      game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, HideEq = true, Open = true };
      var fpst = new FilterPilot(g); int hideClicks = 0, hideClicksAfterOn = 0; bool doneStale = false, itemShown = false;
      for (long t = 0; t < 20000; t += 100) {
        var v = new FilterView { NowMs = t, Foreground = true, W = W, H = H, Item = 5, Wanted = true, Suits = new long[0], MainAttrs = new long[0],
          HideEquipped = true, OnOtherHero = true, ItemListed = listed && itemShown, SetOrder = game.SetList, SubOrder = game.SubList, SubAttrs = new long[0], PanelOpen = true };
        var a = fpst.Step(v);
        if (fpst.State == "done") doneStale = true;
        if (a != null && a.Kind == AutoKind.Click) {
          if (Math.Abs(a.X - g.X(g.HideEquippedX, W, H)) < 12 && Math.Abs(a.Y - g.Y(g.HideEquippedY, W, H)) < 12) { hideClicks++; if (!game.HideEq) hideClicksAfterOn++; }
          game.Click(a.X, a.Y); if (!game.HideEq) itemShown = true;
        }
      }
      Check(hideClicks == 1 && hideClicksAfterOn == 0 && !game.HideEq, "stale hide-worn read (item listed turns " + listed + "): exactly one click, the game's toggle stays off (" + hideClicks + " clicks)");
      if (listed) Check(doneStale && fpst.State == "done", "stale hide-worn read, item listed after the click: the filter finishes (" + fpst.State + ")");
      else Check(!doneStale && fpst.State == "failed" && fpst.Note.Contains("did not switch off"), "stale read and the item never listed: failed after 2 s with a note, no second click (" + fpst.State + ": " + fpst.Note + ")");
    }

    {   // live 1.6.66: the item on another hero is listed under the filter (the guide says «pick»), the read of «Скрыть надетое» stays
        // "on": no click - the one click switched it ON and hid the item, then "did not switch off"
      var gl = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, HideEq = false, Open = true };
      var fpl = new FilterPilot(g); int hideL = 0; string stL = "";
      for (long t = 0; t < 10000; t += 100) {
        var v = new FilterView { NowMs = t, Foreground = true, W = W, H = H, Item = 5, Wanted = true, Suits = new long[0], MainAttrs = new long[0],
          HideEquipped = true, OnOtherHero = true, ItemListed = !gl.HideEq, SetOrder = gl.SetList, SubOrder = gl.SubList, SubAttrs = new long[0], PanelOpen = true };
        var a = fpl.Step(v); stL = fpl.State;
        if (a == null) break;
        if (a.Kind == AutoKind.Click) { if (Math.Abs(a.X - g.X(g.HideEquippedX, W, H)) < 12 && Math.Abs(a.Y - g.Y(g.HideEquippedY, W, H)) < 12) hideL++; gl.Click(a.X, a.Y); }
      }
      Check(hideL == 0 && !gl.HideEq && stL != "failed", "item listed, hide-worn read stale «on»: no click, the item stays listed (" + hideL + " clicks, " + stL + ")");
    }

    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, Open = true };
    int m = RunFilter(new FilterPilot(g), game, 721500, 13, false, false);
    Check(m == 0 && game.Open, "a panel the player opened (item not far): not touched");

    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats };
    RunFilter(new FilterPilot(g), game, 720900, 13, false, true);   // index 19: below the rows visible without scrolling
    Check(game.Suits.Count == 1 && game.Suits[0] == 720900 && game.Mains.Count == 1 && game.Mains[0] == 13 && game.Open,
          "set below the visible rows: the sets list is dragged, then the set clicked");
    // sub stats: the set, then the item's four sub stats (the game's limit is 4; the main stat of a weapon is skipped)
    long[] subList = { 1, 2, 7, 13, 14, 19, 23, 24, 25, 27, 29 };
    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, SubList = subList };
    int ns = RunFilter(new FilterPilot(g), game, 721500, 0, false, true, new long[] { 25, 13, 7, 29 });
    game.Subs.Sort();
    Check(game.Suits.Count == 1 && game.Mains.Count == 0 && string.Join(",", game.Subs) == "7,13,25,29" && game.Open,
          "set + the item's 4 sub stats chosen, the pop-up open (" + ns + " clicks)");
    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, SubList = subList };
    game.Subs.Add(1);   // a sub stat left from before: reset, then the right ones
    RunFilter(new FilterPilot(g), game, 721500, 13, false, true, new long[] { 24, 25 });
    game.Subs.Sort();
    Check(string.Join(",", game.Subs) == "24,25" && game.Mains.Count == 1, "an old sub stat is cleared first; main stat + sub stats");
    // the side panel is still open after the last pick: closed (the filter stays) before the pilot says done
    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, SubList = subList };
    var fpc = new FilterPilot(g);
    RunFilter(fpc, game, 721500, 13, false, true, new long[] { 24, 25 });
    Check(!game.Side && game.Open && game.Subs.Count == 2 && game.Suits.Count == 1 && fpc.State == "done", "sub stats picked: the side panel closed, the filter kept, then done (" + fpc.State + ")");
    // a side panel that does not close: gives up with a note instead of done
    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, SubList = subList };
    var fps = new FilterPilot(g);
    for (long t = 0; t < 30000 && fps.State != "failed"; t += 100) {
      bool sat = game.Suits.Contains(721500) && game.Mains.Contains(13);
      var sv = new FilterView { NowMs = t, Foreground = true, W = W, H = H, Item = 5, Wanted = !sat, SetId = 721500, StatId = 13,
        SetIndex = Array.IndexOf(sets, 721500L), StatIndex = Array.IndexOf(stats, 13L), Suits = game.Suits.ToArray(), MainAttrs = game.Mains.ToArray(),
        SetOrder = sets, SubAttrs = new long[0], PanelOpen = game.Open, SidePanelOpen = game.Open && (game.Side || sat), SetPanelOpen = game.Open && game.Side && game.SetSide };
      var sa = fps.Step(sv);
      if (sa != null && sa.Kind == AutoKind.Click) game.Click(sa.X, sa.Y);
    }
    Check(fps.State == "failed" && fps.Note.Length > 0, "side panel stuck open: failed with a note, not done (" + fps.State + ")");
    // the game drops a filter that was set (the hero screen opened again) for the same item: the second pass gets fresh
    // tries (live 10.10: the sub stat and side panel tries left from the first pass made it give up after two sub stats)
    game = new FakeFilterGame(g, W, H) { SetList = sets, StatList = stats, SubList = subList };
    var fpr = new FilterPilot(g);
    var subs4 = new long[] { 24, 25, 19, 29 };
    RunFilter(fpr, game, 722800, 13, false, true, subs4);
    game.Side = true;   // the side panel opened again over the done filter: closed once more
    RunFilter(fpr, game, 722800, 13, false, true, subs4);
    game.Open = game.Side = game.SetSide = game.SubSide = false; game.Suits.Clear(); game.Mains.Clear(); game.Subs.Clear();
    RunFilter(fpr, game, 722800, 13, false, true, subs4);
    game.Subs.Sort();
    Check(fpr.State == "done" && game.Suits.Count == 1 && game.Mains.Count == 1 && string.Join(",", game.Subs) == "19,24,25,29" && !game.Side,
          "filter dropped by the game after it was set: set again in full (" + fpr.State + ", subs " + string.Join(",", game.Subs) + ")");
    // dropped again and again: gives up after a few passes instead of clicking forever
    for (int k = 0; k < 4; k++) {
      game.Open = game.Side = game.SetSide = game.SubSide = false; game.Suits.Clear(); game.Mains.Clear(); game.Subs.Clear();
      RunFilter(fpr, game, 722800, 13, false, true, subs4);
    }
    Check(fpr.State == "failed", "filter dropped over and over: failed after " + FilterPilot.MaxPasses + " passes (" + fpr.State + ")");
    // the panel closes before anything is applied (nothing opens it for good): it is opened again, not given up at once
    {
      var fpo = new FilterPilot(g);
      int opens = 0; bool failedEarly = false, failedAtEnd = false;
      for (long t = 0; t < 60000; t += 100) {
        var v = new FilterView { NowMs = t, Foreground = true, W = 1920, H = 1009, Item = 9, Wanted = true, SetId = 722800, StatId = 13,
          SetIndex = Array.IndexOf(sets, 722800L), StatIndex = Array.IndexOf(stats, 13L), SetOrder = sets,
          Suits = new long[0], MainAttrs = new long[0], SubAttrs = new long[0], PanelOpen = false };
        var a = fpo.Step(v);
        if (a != null && a.Kind == AutoKind.Click) { opens++; if (opens <= 2 * (1 + FilterPilot.MaxReopens) && fpo.State == "failed") failedEarly = true; }
        if (fpo.State == "failed") { failedAtEnd = true; break; }
      }
      Check(!failedEarly && opens > 2, "panel closed without settings: opened again, not failed after " + FilterPilot.PartTries + " tries (" + opens + " clicks)");
      Check(failedAtEnd && opens == 2 * (1 + FilterPilot.MaxReopens), "panel never stays open: failed after " + FilterPilot.MaxReopens + " retries (" + opens + " clicks, " + fpo.State + ")");
    }
    // the item pilot: a side panel open over the list is closed first, the click is not counted
    var ap = new AutoPilot(new ListGeometry());
    var av = new AutoView { NowMs = 0, Foreground = true, H = 1009, Target = 77, Row = 2, Col = 1, SideClose = new[] { 1640.0, 91.0 } };
    var aa = ap.Step(av);
    Check(aa.Kind == AutoKind.Click && Math.Abs(aa.X - 1640) < 1 && Math.Abs(aa.Y - 91) < 1, "item pilot: the open side panel is closed first");
    var longSets = new long[31]; for (int i = 0; i < longSets.Length; i++) longSets[i] = 730000 + i;
    int okDrag = 0;
    foreach (double gain in new[] { 0.7, 0.85, 1.0, 1.2, 1.4 })
      foreach (int want in new[] { 18, 23, 30 }) {
        game = new FakeFilterGame(g, W, H) { SetList = longSets, StatList = stats, DragGain = gain };
        RunFilter(new FilterPilot(g), game, longSets[want], 13, false, true);
        if (game.Suits.Count == 1 && game.Suits[0] == longSets[want] && game.Mains.Count == 1) okDrag++;
      }
    // the game keeps the list scrolled from before (another item's set): the first click is off, then corrected
    game = new FakeFilterGame(g, W, H) { SetList = longSets, StatList = stats, SetScroll = 4 };
    RunFilter(new FilterPilot(g), game, longSets[18], 13, false, true);
    Check(game.Suits.Count == 1 && game.Suits[0] == longSets[18], "sets list left scrolled by 4 rows from before: found anyway");
    Check(okDrag == 15, "31 sets, the wanted one at 19/24/31, drag gain 0.7-1.4 (a wrong set corrects the position): " + okDrag + "/15");
  }
}
