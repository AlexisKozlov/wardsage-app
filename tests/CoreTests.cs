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
    Eq("https://realmforge-wor.vercel.app", SyncClient.NormalizeSite("https://realmforge-wor.vercel.app/", out err), "trailing slash");
    Eq("https://realmforge-wor.vercel.app", SyncClient.NormalizeSite("realmforge-wor.vercel.app", out err), "scheme added");
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
    Check(def.Site == SyncClient.DefaultSite && def.Lang == "ru" && def.Code == "" && !def.SaveCopy, "broken config -> defaults");
    Eq("", AppConfig.FromJson("{\"code\":\"" + Tok('A') + "\"}").Code, "plain-text code in config is ignored");
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
    Eq("RealmForge-Extractor/" + RFX.ExtractorVersion, (string)h["userAgent"], "User-Agent header");
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
      "{\"slot\":2,\"uid\":6423,\"slotName\":\"Браслет\",\"name\":\"Браслет «Проклятие»\",\"setName\":\"Проклятие\",\"level\":16,\"stars\":6,\"mainStat\":\"Крит. УРН 50%\",\"fromHeroUid\":200,\"fromHeroName\":\"Байек\"," +
      "\"icon\":\"Item_720104\",\"subs\":[{\"text\":\"АТК +35\",\"rolls\":2},{\"rolls\":1}],\"setBonus\":[{\"pieces\":2,\"text\":\"Крит. УРН +20%\"}],\"cur\":{\"uid\":44,\"name\":\"Старый\",\"level\":8,\"stars\":4,\"icon\":\"../x\"}}," +
      "{\"slot\":9,\"uid\":1}]}, {\"id\":\"\",\"heroUid\":1,\"items\":[{\"slot\":0,\"uid\":1}]}]}";
    var pr = PlansClient.Interpret(200, plansJson, true);
    Check(pr.Status == PlansStatus.Ok, "plans ok");
    Eq(1, pr.Plans.Count, "plan without id dropped");
    var plan = pr.Plans[0];
    Eq(2, plan.Items.Count, "bad slot dropped");
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
    Eq(3, geo.ColumnAt(geo.ColLeft(3) + 0.05), "column under the cursor");
    Eq(0, geo.ColumnAt(geo.ColLeft(1) - 0.03), "left of the list");

    Console.WriteLine("AutoPilot (open the slot, scroll, click the item):");
    AutoPilotTests(geo, HH);

    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed");
    return failed == 0 ? 0 : 1;
  }

  // A simulated game list: 40 item rows; one wheel notch moves it `notch` px; clicking a cell selects the item there.
  static void AutoPilotTests(ListGeometry geo, double H) {
    var types = new int[41]; for (int i = 1; i <= 40; i++) types[i] = 1;
    double pitch = geo.PitchY * H, cell = geo.CellW * H, notch = 37.5;
    double top = ScrollAnchor.TopRow1(geo, H), maxScroll = 36 * pitch;   // real row 1 top on the screen
    int targetRow = 23, targetCol = 2; long target = 9023;
    Func<double, double, long> itemAt = (x, y) => {
      int col = geo.ColumnAt(x / H); if (col == 0) return 0;
      int row = (int)Math.Floor((y - top) / pitch) + 1;
      double inRow = y - (top + (row - 1) * pitch);
      return row >= 1 && row <= 40 && inRow <= cell ? 9000 + row + (col == targetCol && row == targetRow ? 0 : col * 100) : 0;
    };
    var p = new AutoPilot(geo);
    var anchor = new ScrollAnchor(); long sel = 0; long now = 0; int clicks = 0, wheels = 0;
    for (int tick = 0; tick < 400 && sel != target; tick++, now += 100) {
      var a = p.Step(new AutoView { NowMs = now, Foreground = true, H = H, Target = target, Row = targetRow, Col = targetCol, Sel = sel,
                                    AnchorHas = anchor.Has, Row1Top = anchor.Row1Top, Types = types });
      if (a.Kind != AutoKind.None && Environment.GetEnvironmentVariable("AP_DEBUG") != null) Console.WriteLine("    t=" + now + " " + a.Kind + " x=" + a.X.ToString("0") + " y=" + a.Y.ToString("0") + " n=" + a.Notches + " top=" + top.ToString("0") + " anchor=" + anchor.Row1Top.ToString("0") + " sel=" + sel);
      if (a.Kind == AutoKind.Click) {
        clicks++;
        long hit = itemAt(a.X, a.Y);
        if (hit != 0 && hit != sel) {
          sel = hit; int row = (int)(hit % 100);
          anchor.FromClick(geo, types, row, a.Y, H, 1);
          anchor.Track(geo, types, row, top + (row - 1) * pitch, H);   // the stat bars on the screen snap it exactly
        }
      } else if (a.Kind == AutoKind.Wheel) {
        wheels++;
        top = Math.Max(ScrollAnchor.TopRow1(geo, H) - maxScroll, Math.Min(ScrollAnchor.TopRow1(geo, H), top - a.Notches * notch));
      }
    }
    p.Step(new AutoView { NowMs = now, Foreground = true, H = H, Target = target, Row = targetRow, Col = targetCol, Sel = sel, AnchorHas = anchor.Has, Row1Top = anchor.Row1Top, Types = types });
    Eq(target, sel, "the target item gets selected (row 23 of 40, list scrolled by the wheel)");
    Check(wheels <= 3 && clicks <= 6, "few wheel batches and clicks (" + wheels + " wheel, " + clicks + " clicks)");
    Check(Math.Abs(p.PxPerNotch - notch) < 1, "wheel step measured: " + p.PxPerNotch.ToString("0.0") + " px");
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
    Eq("failed", r.State, "slot that does not open -> failed");

    // the end of the list: the wheel does not move it -> give up instead of spinning
    var e = new AutoPilot(geo); double stuckTop = ScrollAnchor.TopRow1(geo, H); int w2 = 0;
    for (long t = 0; t < 30000 && e.State != "failed"; t += 100) {
      var a = e.Step(new AutoView { NowMs = t, Foreground = true, H = H, Target = 7, Row = 30, Col = 1, Sel = 1, Types = types, AnchorHas = true, Row1Top = stuckTop });
      if (a.Kind == AutoKind.Wheel) w2++;
    }
    Check(e.State == "failed" && w2 <= 3, "list does not scroll -> failed after " + w2 + " wheel batches");
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
}
