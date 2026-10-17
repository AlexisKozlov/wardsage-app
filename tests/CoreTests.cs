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

  static string Tok(char c) { return "rf_" + new string(c, 32); }

  static int Main(string[] args) {
    string mock = args.Length > 0 ? args[0] : "http://localhost:3999";
    string realAccount = args.Length > 1 ? args[1] : null;
    // the fight timeline and the battle reading take their game values from the layout data (src/Layout.cs): a test copy
    Layout.Use(TestLayout());

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

    Console.WriteLine("Bridge: reply parsing");
    Eq(BridgeStatus.Ok, BridgeClient.StatusOf(204), "204 -> ok");
    Eq(BridgeStatus.Stale, BridgeClient.StatusOf(409), "409 -> stale snapshot");
    Eq(BridgeStatus.NotFound, BridgeClient.StatusOf(404), "404 -> nobody waits");
    Eq(BridgeStatus.Unreachable, BridgeClient.StatusOf(0), "no connection");
    Eq(BridgeStatus.NoToken, BridgeClient.StatusOf(-1), "no token file");
    Eq(BridgeStatus.Rejected, BridgeClient.StatusOf(401), "401 -> rejected");
    string g1 = "0b6b4a7e-1c2d-4e5f-8a9b-0c1d2e3f4a5b", g2 = "1b6b4a7e-1c2d-4e5f-8a9b-0c1d2e3f4a5b", g3 = "2b6b4a7e-1c2d-4e5f-8a9b-0c1d2e3f4a5b";
    var bc = BridgeClient.ParseCommands("{\"commands\":[" +
      "{\"id\":\"" + g1 + "\",\"type\":\"equip\",\"payload\":{\"commandId\":\"e1\",\"heroId\":229000000,\"heroName\":\"Сунь Укун\"," +
        "\"slots\":[{\"slot\":\"ring\",\"itemId\":74,\"setId\":721500,\"mainStatId\":13},{\"slot\":\"weapon\",\"itemId\":42}]}}," +
      "{\"id\":\"" + g2 + "\",\"type\":\"equip\",\"payload\":{\"heroId\":1,\"slots\":[{\"slot\":\"ring\",\"itemId\":1},{\"slot\":\"ring\",\"itemId\":2}]}}," +
      "{\"id\":\"" + g3 + "\",\"type\":\"reboot\"}," +
      "{\"id\":\"../../x\",\"type\":\"equip\"}]}");
    Check(bc != null && bc.Count == 3, "commands with a usable id: " + (bc == null ? -1 : bc.Count));
    Check(bc[0].Type == "equip" && bc[0].HeroUid == 229000000 && bc[0].HeroName == "Сунь Укун" && bc[0].CommandId == "e1", "equip command");
    Check(bc[0].Slots.Count == 2 && bc[0].Slots[0].Slot == 0 && bc[0].Slots[0].ItemUid == 42 && bc[0].Slots[1].Slot == 4, "slots by name, sorted");
    Check(bc[0].Slots[1].SetId == 721500 && bc[0].Slots[1].MainStatId == 13 && bc[0].Slots[0].SetId == 0, "set / main stat for the filter (optional)");
    Eq("invalid", bc[1].Type, "a slot given twice -> invalid (answered failed)");
    Eq("reboot", bc[2].Type, "unknown type kept (answered failed)");
    Check(BridgeClient.ParseCommands("{\"commands\":[]}").Count == 0, "no commands");
    Check(BridgeClient.ParseCommands("<html>") == null && BridgeClient.ParseCommands(null) == null, "not a reply -> null");

    Console.WriteLine("AutoPilot (open the slot, scroll, click the item):");
    AutoPilotTests(geo, HH);

    Console.WriteLine("FilterPilot (the game's gear filter: set + main stat):");
    FilterPilotTests();

    Console.WriteLine("HeroPilot (the plan's hero on the hero screen):");
    HeroPilotTests();
    SellPilotTests();
    FightPlanTests();
    LocateTests();
    LocatePilotTests();
    SellHintTests();

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
    GamePacksTests();
    UpdateKeysTests();

    // ---- WebViewRecovery
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
    TestLogTests(mock);

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
    string web = Path.Combine("..", "wardsage-web", "data", "app-layout");
    if (Directory.Exists(web)) {
      int n = 0;
      foreach (var p in Directory.GetFiles(web, "*.json")) {
        string v = Path.GetFileNameWithoutExtension(p);
        Check(GameLayout.Parse(File.ReadAllText(p, Encoding.UTF8), v) != null, "the site's layout file " + v + " has everything the app reads");
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
      bool all = l39 != null; if (all) foreach (var k in GameLayout.FieldKeys) if (l39.FieldOffset(k) <= 0) all = false;
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
      { "1.6.38.829.1", "87d1958cc51af52fc9b18d44638b06d0919c6f0904f3e3b9016f96a679e06d4d" },
      { "1.6.39.829.1", "144f8260ec82567c445743c3379196c1974f75e6c44b206e362946604228e750" } };
    foreach (var kv in sums) {
      string path = null;
      foreach (var repo in new[] { "wardsage-web", "realmforge-web" }) {
        string p = Path.Combine("..", repo, "data", "app-layout", kv.Key + ".json");
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
    public ulong Knock;   // the value of the running GameData's m_Knock: moves on every read (0 = the game shows no frames)
    public FakeLua(int size) { Mem = new byte[size]; }
    public IList<ulong[]> Writable() { return new[] { new ulong[] { Base, (ulong)Mem.Length } }; }
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
    PickHeroTests();
  }

  static void PickHeroTests() {
    int s;
    Eq(7L, RFX.PickHero(true, 7, false, 5, 6, out s), "hero screen: the form's displayed hero beats the selected one"); Eq(RFX.HeroSrcForm, s, "  source: form");
    Eq(5L, RFX.PickHero(true, 0, false, 5, 6, out s), "hero screen: no displayed hero, the selected one"); Eq(RFX.HeroSrcSelected, s, "  source: selected");
    Eq(6L, RFX.PickHero(true, 0, false, 0, 6, out s), "hero screen: last voiced hero as the last resort"); Eq(RFX.HeroSrcSound, s, "  source: sound");
    Eq(0L, RFX.PickHero(true, 0, true, 5, 6, out s), "an add slot shown: no hero"); Eq(RFX.HeroSrcNone, s, "  source: none");
    Eq(5L, RFX.PickHero(false, 7, false, 5, 6, out s), "form not alive: EquipData only"); Eq(RFX.HeroSrcSelected, s, "  source: selected");
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
    Check(TraceUpload.BuildPayload(fight.Replace("6001631", "5001631"), out skip) == null && skip == "not an arena fight", "trace upload: not an arena stage is not sent");
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
      Eq(5, p.Count, "trace upload: at most 5 pending");
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
    Check(e.State == "failed" && w2 <= 3, "list does not scroll -> failed after " + w2 + " drags");

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
    Eq(1, presses, "one press per item, even when it did not go on");
    Eq("unconfirmed", c1.State, "did not go on -> unconfirmed, the player looks");

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
    }
    double PY { get { return Small ? g.SmallPitchY : g.PitchY; } }
    double CH { get { return Small ? g.SmallCardH : g.CardH; } }   // the main city (the hero screen closed): its «Герои» opens CityHero
    readonly HeroGeometry g; readonly double W, H;
    public int Clicks, Drags, Backs;
    public bool Blocked;   // something over the grid takes the clicks (a pop-up left open)
    public FakeHeroGame(HeroGeometry g, double W, double H) { this.g = g; this.W = W; this.H = H; }
    public double MaxScroll { get { int rows = (Heroes.Length + 2) / 3; return Math.Max(0, g.Row1Top * H + rows * PY * H - g.ViewBottom * H + 10); } }
    public void Click(double x, double y) {
      Clicks++;
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
        FilterOpen = game.FilterOpen, ChosenCamps = game.Camps.ToArray(), ClassChosen = game.Class > 0, GridPtr = game.Grid,
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
    ars.AscendSel = 5; ars.AscendEquips = new long[] { 100 };
    ars.AscendParts = new[] { 4 }; ars.AscendSuits = new long[] { 720100 }; ars.AscendMainAttrs = new long[] { 12 }; ars.AscendSubAttrs = new long[] { 5, 7, 9, 3 };
    Eq(LocateKind.AscendNotInList, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: not in the list (filter complete)");
    ars = ar(); ars.CurType = 3; ars.AscendShown = false;
    Eq(LocateKind.OpenAscendTab, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: foundry on another tab");
    ars = ar(); ars.AscendShown = false;
    Eq(LocateKind.OpenAscendTab, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: tab switching, panel not on screen yet -> tab step");
    // the way to the foundry: nothing is chosen before it, the item is never framed in the inventory
    Eq(LocateKind.OpenInventory, LocateGuide.Next(areq, null, null, null, 8, 1).Kind, "ascend: nothing open (item in the bag) -> open the inventory");
    Eq(LocateKind.OpenFoundry, LocateGuide.Next(areq, inv(), null, null, 8, 1).Kind, "ascend: inventory on the equipment tab -> the Foundry button, not the item's cell");
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
    Check(st.Kind == LocateKind.AscendFilter && st.Pos == 12 && st.Row == 3 && st.Col == 4 && st.Scroll == "below" && st.ScrollRows == 1, "ascend: long list, filter unset -> filter; item at row 3 place 4, one row below the cells on screen");
    ars.AscendParts = new[] { 4 }; ars.AscendSuits = new long[] { 720100 }; ars.AscendMainAttrs = new long[] { 12 }; ars.AscendSubAttrs = new long[] { 5, 7, 9, 3 };
    st = LocateGuide.Next(areq, null, ars, null, 8, 1);
    Check(st.Kind == LocateKind.AscendPick && st.Scroll == "below" && st.ScrollRows == 1, "ascend: filter set -> pick, with the rows to scroll");
    ars.AscendList = new RFX.ListRead { ScrollY = 200, Cols = 4, RowUnits = 100, First = 8, Last = 15 };
    Eq("visible", LocateGuide.Next(areq, null, ars, null, 8, 1).Scroll, "ascend: item among the cells on screen");
    ars.AscendList = new RFX.ListRead { ScrollY = 500, Cols = 4, RowUnits = 100, First = 20, Last = 27 };
    st = LocateGuide.Next(areq, null, ars, null, 8, 1);
    Check(st.Scroll == "above" && st.ScrollRows == 3, "ascend: item above the cells on screen -> rows to scroll up");
    ars.AscendList = null;
    st = LocateGuide.Next(areq, null, ars, null, 8, 1);
    Check(st.Kind == LocateKind.AscendPick && st.Scroll == "" && st.Pos == 12 && st.Col == 1, "ascend: list not readable -> position only");
    ars.AscendEquips = new long[] { 100, 101 };
    Eq(LocateKind.AscendNotInList, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: filter complete, item not in the list -> not ascendable / extra stat");
    ars.AscendSubAttrs = new long[0];
    Eq(LocateKind.AscendFilter, LocateGuide.Next(areq, null, ars, null, 8, 1).Kind, "ascend: not in the list and the filter incomplete -> filter hint");
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
    Eq(LocateKind.AscendReady, LocateGuide.Next(areq, null, new RecastScreen { SublimingOpen = true, AscendShown = true, CurType = 2, AscendSel = 777 }, null, 8, 1).Kind, "ascend: ready needs no list");
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
