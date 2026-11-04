// The per-frame attack event log (src/AttackLog.cs, test build only): the decoder over a synthetic per-frame cooldown model, the JSON shape,
// the caps, the trace strip / upload, the offsets (layout field section, the checked builds) and the base attributes' skill arrays.
// C# 5, no memory reading.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RealmForge;

static class AttackLogTests {
  const int FixtureHero = 100;

  // The game's cooldown for a basic attack: a cast sets cd = max = n; every later frame takes 66 while cd >= 1; ready (cd 0) when it has run out and
  // the hero casts again at once. Returns cd by frame (clamped at 0) and the true cast frames.
  static int[] Model(int frames, int n, int first, List<long> casts) {
    var cd = new int[frames + 1];
    int cur = 0;
    for (int f = 0; f <= frames; f++) {
      if (f > 0 && cur >= 1) cur -= 66;
      if (cur < 1 && f >= first) { cur = n; casts.Add(f); }
      cd[f] = cur < 0 ? 0 : cur;
    }
    return cd;
  }

  static List<long> Frames(List<long[]> ev) { var r = new List<long>(); foreach (var e in ev) r.Add(e[0]); return r; }

  static string Show(List<long> l) { return l.Count + " [" + string.Join(",", l.ConvertAll(x => x.ToString()).ToArray()) + "]"; }

  static List<CdSnap> One(int key, int cd, int max, int last, bool pause) { return new List<CdSnap> { new CdSnap(key, key, cd, max, last, pause) }; }

  // polls the model every s frames; returns the log and the last poll frame
  static AttackLog Poll(int[] cd, int n, int s, out long lastPoll) {
    var log = new AttackLog(); lastPoll = 0;
    for (int f = 0; f < cd.Length; f += s) {
      log.NotePoll((uint)f);
      log.Feed(1, FixtureHero, 11, (uint)f, One(10, cd[f], n, 0, false), null, 10, 20, null, n);
      lastPoll = f;
    }
    return log;
  }

  static GameLayout Fixture(string game) {
    string fx = File.ReadAllText(Path.Combine("tests", "layout-fixture.json"), Encoding.UTF8);
    if (game != "9.8.7.6.5") fx = fx.Replace("\"game\": \"9.8.7.6.5\"", "\"game\": \"" + game + "\"");
    return GameLayout.Parse(fx, game);
  }
  static GameLayout FixtureWithField(string game, string fields) {
    string fx = File.ReadAllText(Path.Combine("tests", "layout-fixture.json"), Encoding.UTF8);
    if (game != "9.8.7.6.5") fx = fx.Replace("\"game\": \"9.8.7.6.5\"", "\"game\": \"" + game + "\"");
    fx = fx.Substring(0, fx.LastIndexOf('}')) + ",\"field\": { " + fields + " } }";
    return GameLayout.Parse(fx, game);
  }

  public static void Run(Action<bool, string> Check) {
    // 1. exact at S = 1, 3, 9 (S < C = 16)
    foreach (int s in new[] { 1, 3, 9 }) {
      var casts = new List<long>(); var cd = Model(400, 1000, 5, casts);
      long lastPoll; var log = Poll(cd, 1000, s, out lastPoll);
      var want = casts.FindAll(c => c <= lastPoll); var got = Frames(log.Events(1, FixtureHero));
      Check(want.Count > 20 && Show(want) == Show(got), "attack log: S=" + s + " frames, every cast found at its exact frame (" + want.Count + " casts" + (Show(want) == Show(got) ? "" : "; got " + Show(got)) + ")");
      bool flags = true; foreach (var e in log.Events(1, FixtureHero)) if (e[6] != AttackLog.FlagExact || e[2] != 0 || e[1] != 10 || e[4] != 1000 || e[3] != 1000 || e[5] != -1) flags = false;
      Check(flags, "attack log: S=" + s + ": flags decoded-exact, kind basic (key = ArraySlots[0]), n and cdMax kept, no run frames");
    }
    // 2. S < C loses nothing, S >= C: overwritten casts are missed, never invented
    {
      var casts = new List<long>(); var cd = Model(400, 200, 5, casts);   // C = ceil(200 / 66) = 4 frames
      long lp; var l3 = Poll(cd, 200, 3, out lp);
      Check(Show(casts.FindAll(c => c <= lp)) == Show(Frames(l3.Events(1, FixtureHero))), "attack log: S=3 < C=4: no cast lost");
      var l9 = Poll(cd, 200, 9, out lp);
      var got = Frames(l9.Events(1, FixtureHero)); bool real = true; foreach (var g in got) if (!casts.Contains(g)) real = false;
      Check(real && got.Count > 0 && got.Count < casts.FindAll(c => c <= lp).Count, "attack log: S=9 >= C=4: casts inside one interval are lost, every event is a real cast (" + got.Count + " of " + casts.FindAll(c => c <= lp).Count + ")");
    }
    // 3. a torn poll is counted (the recorder drops it before Feed), the histogram counts frames per poll
    {
      var log = new AttackLog();
      log.NotePoll(10); log.NotePoll(10); log.NotePoll(11); log.NotePoll(13); log.NotePoll(20); log.NoteTorn(); log.NoteTorn();
      var sb = new StringBuilder(); log.AppendJson(sb, 1);
      var m = (MiniJson.Parse("{\"x\":0" + sb + "}") as Dictionary<string, object>)["atkMeta"] as Dictionary<string, object>;
      var fpp = m["fpp"] as Dictionary<string, object>;
      Check(Convert.ToInt32(m["torn"]) == 2 && Convert.ToInt32(m["polls"]) == 5 && Convert.ToInt32(fpp["0"]) == 1 && Convert.ToInt32(fpp["1"]) == 1 && Convert.ToInt32(fpp["2"]) == 1 && Convert.ToInt32(fpp["3+"]) == 1 && Convert.ToInt32(m["cut"]) == 0,
        "attack log: atkMeta counts polls, torn polls and the frames-per-poll histogram");
    }
    // 4. a pooled object (the id is not the dictionary key): nothing logged
    {
      var log = new AttackLog();
      log.Feed(1, 7, 1, 100, new List<CdSnap> { new CdSnap(10, 99, 1000, 1000, 0, false) }, null, 10, 20, null, 1000);
      Check(log.Events(1, 7).Count == 0 && log.Pooled == 1, "attack log: a pooled SkillCDData reused for another skill gives no event");
      log.Feed(1, 7, 1, 101, One(10, 1000, 1000, 0, false), null, 10, 20, null, 1000);
      Check(log.Events(1, 7).Count == 1, "attack log: the same key with its own id is read again");
    }
    // 5. (max - cd) % 66 != 0: odd; a rise of the cooldown is a new event even then
    {
      var log = new AttackLog();
      log.Feed(1, 7, 1, 100, One(10, 400, 1000, 0, false), null, 10, 20, null, 1000);
      Check(log.Events(1, 7).Count == 0, "attack log: a running cooldown that does not decode is not a cast by itself");
      log.Feed(1, 7, 1, 103, One(10, 950, 1000, 0, false), null, 10, 20, null, 1000);
      var ev = log.Events(1, 7);
      Check(ev.Count == 1 && ev[0][0] == 103 && ev[0][6] == (AttackLog.FlagOdd | AttackLog.FlagNoDecode), "attack log: the cooldown went up with the same max, (max-cd) % 66 != 0: an event flagged odd and nodecode");
    }
    // 6. last > 0: late; the decode of the same cast afterwards adds nothing
    {
      var log = new AttackLog();
      log.Feed(1, 7, 1, 100, One(10, 1000, 1000, 200, false), null, 10, 20, null, 1000);
      log.Feed(1, 7, 1, 102, One(10, 1000, 1000, 134, false), null, 10, 20, null, 1000);
      log.Feed(1, 7, 1, 105, One(10, 1000, 1000, 2, false), null, 10, 20, null, 1000);
      log.Feed(1, 7, 1, 106, One(10, 934, 1000, 0, false), null, 10, 20, null, 1000);   // the cooldown runs after the cast time
      log.Feed(1, 7, 1, 108, One(10, 802, 1000, 0, false), null, 10, 20, null, 1000);
      var ev = log.Events(1, 7);
      Check(ev.Count == 1 && ev[0][0] == 100 && ev[0][6] == AttackLog.FlagLate, "attack log: iLastTime > 0: one event at the first poll with cd == max, flagged late, not repeated or doubled by the later decode");
      log.Feed(1, 7, 1, 140, One(10, 1000, 1000, 100, false), null, 10, 20, null, 1000);
      Check(log.Events(1, 7).Count == 2, "attack log: the next cast after a late one is an event again");
    }
    // 7. the same castFrame twice: no duplicate
    {
      var log = new AttackLog();
      log.Feed(1, 7, 1, 110, One(10, 868, 1000, 0, false), null, 10, 20, null, 1000);   // k = 2: cast at 108
      log.Feed(1, 7, 1, 111, One(10, 802, 1000, 0, false), null, 10, 20, null, 1000);   // k = 3: cast at 108
      log.Feed(1, 7, 1, 111, One(10, 802, 1000, 0, false), null, 10, 20, null, 1000);
      var ev = log.Events(1, 7);
      Check(ev.Count == 1 && ev[0][0] == 108, "attack log: polls that decode the same cast frame add one event");
      log.Feed(1, 7, 1, 125, One(10, 1000, 1000, 0, false), null, 10, 20, null, 1000);
      Check(log.Events(1, 7).Count == 2, "attack log: a cast frame more than 1 away is a new event");
    }
    // 8. the cast script: start + a cooldown event within 2 frames merge (runFrames), else an event of its own (nocd)
    {
      var log = new AttackLog();
      log.Feed(1, 7, 1, 200, One(10, 1000, 1000, 0, false), new UseSnap(true, 0x1000, 10), 10, 20, null, 1000);
      log.Feed(1, 7, 1, 203, One(10, 802, 1000, 0, false), new UseSnap(true, 0x1000, 10), 10, 20, null, 1000);
      log.Feed(1, 7, 1, 207, One(10, 538, 1000, 0, false), new UseSnap(false, 0x1000, 10), 10, 20, null, 1000);
      var ev = log.Events(1, 7);
      Check(ev.Count == 1 && ev[0][0] == 200 && ev[0][5] == 7 && ev[0][6] == AttackLog.FlagExact, "attack log: cast script start and a cooldown event of the same skill merge, runFrames = end - start");
      log.Feed(1, 7, 1, 300, One(10, 0, 1000, 0, false), new UseSnap(true, 0x1000, 20), 10, 20, null, 1000);
      log.Feed(1, 7, 1, 312, One(10, 0, 1000, 0, false), new UseSnap(false, 0x1000, 20), 10, 20, null, 1000);
      ev = log.Events(1, 7);
      Check(ev.Count == 2 && ev[1][0] == 300 && ev[1][1] == 20 && ev[1][2] == 1 && ev[1][5] == 12 && ev[1][6] == AttackLog.FlagNoCd, "attack log: a cast script run no cooldown explains: an event flagged nocd, kind ultimate (ArraySlots[1])");
      log.Feed(1, 7, 1, 400, null, new UseSnap(true, 0x1000, 30), 10, 20, null, 1000);
      log.Feed(1, 7, 1, 401, null, new UseSnap(true, 0x2000, 30), 10, 20, null, 1000);   // another script object: the first run ended
      Check(log.Events(1, 7).Count == 3 && log.Events(1, 7)[2][1] == 30 && log.Events(1, 7)[2][2] == 2 && log.Events(1, 7)[2][5] == 1, "attack log: another script object ends the run; an unknown skill is kind other, its raw id kept");
      var conv = new Dictionary<int, int> { { 11, 10 } };
      var l2 = new AttackLog();
      l2.Feed(1, 8, 1, 50, One(11, 1000, 1000, 0, false), null, 10, 20, conv, 1000);
      Check(l2.Events(1, 8).Count == 1 && l2.Events(1, 8)[0][2] == 0 && l2.Events(1, 8)[0][1] == 11, "attack log: a converted id that maps to the basic attack is kind basic, the raw id kept");
    }
    // 9. JSON shape: the keys as the timeline's heroes, nT on change only
    {
      var log = new AttackLog { PollMs = 3.456 };
      log.Feed(1, 100, 11, 10, One(10, 1000, 1000, 0, false), null, 10, 20, null, 1000);
      log.Feed(1, 100, 11, 11, One(10, 934, 1000, 0, false), null, 10, 20, null, 1000);
      log.Feed(1, 100, 11, 12, One(10, 868, 1000, 0, false), null, 10, 20, null, 900);
      log.Feed(2, 200, 22, 12, One(10, 500, 500, 0, false), null, 10, 20, null, 500);
      var sb = new StringBuilder(); log.AppendJson(sb, 1);
      string text = "{\"v\":3" + sb + "}";
      var o = MiniJson.Parse(text) as Dictionary<string, object>;
      var atk = o["atk"] as Dictionary<string, object>;
      Check(atk.ContainsKey("100") && atk.ContainsKey("2:200") && atk.Count == 2, "attack log json: hero keys are a bare uid for the primary controller, controller:uid for the others");
      var h = atk["100"] as Dictionary<string, object>;
      Check(Convert.ToInt32(h["unit"]) == 11 && Convert.ToInt32(h["c"]) == 1 && Convert.ToInt32(h["s0"]) == 10 && Convert.ToInt32(h["s1"]) == 20, "attack log json: unit, c, s0, s1");
      Check(text.Contains("\"ev\":[[10,10,0,1000,1000,-1,1]],\"nT\":[[10,1000],[12,900]]"), "attack log json: ev [castFrame,skill,kind,n,cdMax,runFrames,flags]; nT only on change");
      var meta = o["atkMeta"] as Dictionary<string, object>;
      Check(Math.Abs(Convert.ToDouble(meta["pollMs"]) - 3.46) < 0.001 && meta.ContainsKey("fpp") && Convert.ToInt32(meta["cut"]) == 0, "attack log json: atkMeta has pollMs, polls, torn, fpp, cut");
    }
    // 10. caps: 3000 events a hero, 30000 in all; the overflow is counted in cut
    {
      var log = new AttackLog();
      for (int i = 0; i < 3100; i++) log.Feed(1, 1, 1, (uint)(100 + i * 10), One(10, 1000, 1000, 0, false), null, 10, 20, null, 1000);
      Check(log.Events(1, 1).Count == AttackLog.MaxEventsPerHero && log.Cut == 100, "attack log caps: 3000 events a hero, the rest counted in cut (" + log.Events(1, 1).Count + ", cut " + log.Cut + ")");
      var all = new AttackLog();
      for (uint hero = 1; hero <= 11; hero++)
        for (int i = 0; i < 3000; i++) all.Feed(1, hero, 1, (uint)(100 + i * 10), One(10, 1000, 1000, 0, false), null, 10, 20, null, 1000);
      Check(all.EventCount == AttackLog.MaxEvents && all.Cut == 3000, "attack log caps: 30000 events in all, the 11th hero's are cut (" + all.EventCount + ", cut " + all.Cut + ")");
    }
    // 11. the timeline's JSON carries atk / atkMeta after units; StripTrace removes them; BuildPayload takes a fight with only atk
    {
      var ly = Fixture("9.8.7.6.5");
      var tb = new TimelineBuilder(ly);
      var hs = new List<HeroSample> {
        new HeroSample { Uid = 100, C = 1, Unit = 11, Tower = 5 }, new HeroSample { Uid = 200, C = 2, Unit = 22, Tower = 6 } };
      tb.AddSample(10, hs);
      var log = new AttackLog();
      log.Feed(1, 100, 11, 10, One(10, 1000, 1000, 0, false), null, 10, 20, null, 1000);
      log.Feed(2, 200, 22, 10, One(10, 1000, 1000, 0, false), null, 10, 20, null, 1000);
      log.NotePoll(10);
      Check(!(MiniJson.Parse(tb.ToJson()) as Dictionary<string, object>).ContainsKey("atk"), "timeline: no attack log set, no atk");
      tb.Atk = log;
      string json = tb.ToJson();
      var o = MiniJson.Parse(json) as Dictionary<string, object>;
      var heroes = o["heroes"] as Dictionary<string, object>; var atk = o["atk"] as Dictionary<string, object>;
      bool same = heroes.Count == atk.Count; foreach (var k in heroes.Keys) if (!atk.ContainsKey(k)) same = false;
      Check(same && o.ContainsKey("atkMeta") && json.IndexOf("\"atk\":{", StringComparison.Ordinal) > json.IndexOf("\"heroes\":{", StringComparison.Ordinal), "timeline: atk has the keys of heroes, atkMeta follows it");
      string full = "{\"battle\":1,\"timeline\":" + json + ",\"after\":2}";
      var stripped = MiniJson.Parse(TimelineBuilder.StripTrace(full)) as Dictionary<string, object>;
      var stl = stripped["timeline"] as Dictionary<string, object>;
      Check(!stl.ContainsKey("atk") && !stl.ContainsKey("atkMeta") && stl.ContainsKey("heroes") && stl.ContainsKey("ops") && Convert.ToDouble(stripped["after"]) == 2, "strip: atk and atkMeta removed from the account snapshot, the rest kept");
      string fight = "{\"stage\":1500100,\"at\":\"2026-11-04T11:50:00Z\",\"timeline\":" + json + "}";
      string skip;
      Check(TraceUpload.BuildPayload(fight, true, out skip) != null && skip == null, "trace upload: a fight with only atk (any 7-digit stage) is sent in the test build");
      Check(TraceUpload.BuildPayload(fight, false, out skip) == null && skip == "not an arena, boss or Malric fight", "trace upload: without the test build, atk is not a trace");
      Check(TraceUpload.BuildPayload(fight, out skip) == null, "trace upload: this build (not the test channel) does not send it");
      string noAtk = fight.Replace("\"atk\":{", "\"atkX\":{");
      Check(TraceUpload.BuildPayload(noAtk, true, out skip) == null, "trace upload: no atk, a non-arena stage: not sent");
    }
    // 12. the offsets: the layout's field section, the built-in table for the checked builds only
    {
      Check(RFX.AtkOffsets(Fixture("9.8.7.6.5")) == null, "attack offsets: an unknown build without the field keys: none (the log stays off)");
      string f = "\"SkillComponent.cUseSkill\": 72, \"SkillComponent.vSkillConvertRelations\": 128, \"SkillComponent.vSkillCDs\": 160, \"SkillComponent.ArraySlots\": 168, \"StateScript.m_bRunning\": 73, \"StateScript.m_skillId\": 76, "
               + "\"SkillCDData.iSkillID\": 16, \"SkillCDData.iCoolDown\": 24, \"SkillCDData.iCoolDownMax\": 28, \"SkillCDData.iLastTime\": 32, \"SkillCDData.bPause\": 40, \"BattleAttributeData.m_nAttackCD\": 604";
      var o = RFX.AtkOffsets(FixtureWithField("9.8.7.6.5", f));
      Check(o != null && o.Use == 72 && o.CDs == 160 && o.Slots == 168 && o.Running == 73 && o.UseSkill == 76 && o.CdMax == 28 && o.CdLast == 32 && o.CdPause == 40 && o.AtkCd == 604, "attack offsets: all from the layout's field section");
      Check(RFX.AtkOffsets(FixtureWithField("9.8.7.6.5", f.Replace("\"StateScript.m_skillId\": 76, ", ""))) == null, "attack offsets: one key missing in an unknown build: none");
      foreach (var g in new[] { "1.6.46.841.1", "1.6.47.841.1" }) {
        var b = RFX.AtkOffsets(Fixture(g));
        Check(b != null && b.Use == 0x48 && b.Convert == 0x80 && b.CDs == 0xA0 && b.Slots == 0xA8 && b.Running == 0x49 && b.UseSkill == 0x4C && b.CdId == 0x10 && b.Cd == 0x18 && b.CdMax == 0x1C && b.CdLast == 0x20 && b.CdPause == 0x28 && b.AtkCd == 0x25C,
          "attack offsets: the built-in values for the checked build " + g);
      }
      Check(RFX.AtkOffsets(Fixture("1.6.39.829.1")) == null, "attack offsets: no built-in values for an older build");
    }
    // 13. T9: the base attributes' skill arrays moved by 8 with 1.6.46 (bIsHiddenShape at 0x180): iSkills 0x188, iSkillLevels 0x190
    {
      int a, b;
      Check(RFX.SkillArrayOffsets(Fixture("1.6.46.841.1"), out a, out b) && a == 0x188 && b == 0x190, "base attrs: 1.6.46 reads iSkills at 0x188 and iSkillLevels at 0x190 (the dump's, not 0x180 / 0x188)");
      Check(RFX.SkillArrayOffsets(Fixture("1.6.47.841.1"), out a, out b) && a == 0x188 && b == 0x190, "base attrs: 1.6.47 the same");
      Check(RFX.SkillArrayOffsets(Fixture("1.6.39.829.1"), out a, out b) && a == 0x180 && b == 0x188 && RFX.SkillArrayOffsets(Fixture("1.6.38.829.1"), out a, out b) && a == 0x180, "base attrs: the older builds (1.6.38, 1.6.39) keep 0x180 / 0x188");
      Check(!RFX.SkillArrayOffsets(Fixture("9.8.7.6.5"), out a, out b) && !RFX.SkillArrayOffsets(null, out a, out b), "base attrs: an unknown build, no skill arrays read");
      Check(RFX.SkillArrayOffsets(FixtureWithField("9.8.7.6.5", "\"stBattleBaseAttr.iSkills\": 400, \"stBattleBaseAttr.iSkillLevels\": 408"), out a, out b) && a == 400 && b == 408, "base attrs: the layout's field section wins");
      Check(RFX.SkillArrayOffsets(FixtureWithField("1.6.46.841.1", "\"stBattleBaseAttr.iSkills\": 400, \"stBattleBaseAttr.iSkillLevels\": 408"), out a, out b) && a == 400, "base attrs: the layout's field section wins over the built-in table");
    }
  }
}
