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

  static string Tok(char c) { return "rf_" + new string(c, 32); }

  static int Main(string[] args) {
    string mock = args.Length > 0 ? args[0] : "http://localhost:3999";
    string realAccount = args.Length > 1 ? args[1] : null;

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

    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed");
    return failed == 0 ? 0 : 1;
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
