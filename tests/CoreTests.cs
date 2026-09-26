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

    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed");
    return failed == 0 ? 0 : 1;
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
    public FakeHeroGame(HeroGeometry g, double W, double H) { this.g = g; this.W = W; this.H = H; }
    public double MaxScroll { get { int rows = (Heroes.Length + 2) / 3; return Math.Max(0, g.Row1Top * H + rows * PY * H - g.ViewBottom * H + 10); } }
    public void Click(double x, double y) {
      Clicks++;
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

  static void HeroPilotTests() {
    var g = new HeroGeometry(); double W = 1920, H = 1009;
    var heroes = new long[130]; for (int i = 0; i < heroes.Length; i++) heroes[i] = (200100 + i) * 100000L;
    HeroView lv;

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
