// Replays saved fights (tests/fixtures/dps-battles.json: de-identified, only stage / frames / statistics / dmgT / skills of real
// battle traces, timeline v3 as in docs/specs/2026-11-07-dps-meter-stage0-data.md) through the DPS meter's data (src/DpsFeed.cs).
using System;
using System.Collections.Generic;
using System.IO;
using RealmForge;

static partial class CoreTests {
  static double Num(Dictionary<string, object> d, string k) { object o; return d.TryGetValue(k, out o) && o is double ? (double)o : 0; }

  static bool Sane(double v) { return !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0; }

  static void DpsReplayTests() {
    Console.WriteLine("DPS meter: saved fights replayed through DpsFeed (tests/fixtures/dps-battles.json):");
    string path = File.Exists("tests/fixtures/dps-battles.json") ? "tests/fixtures/dps-battles.json" : null;
    if (path == null) { Check(false, "replay: fixture file tests/fixtures/dps-battles.json not found"); return; }
    var fights = MiniJson.Parse(File.ReadAllText(path)) as List<object>;
    Check(fights != null && fights.Count >= 4, "replay: the fixture has several fights");
    if (fights == null) return;
    long grpTotal = 0, grpOther = 0; int grpBad = 0;
    int n = 0, withSkills = 0; var kinds = new HashSet<DpsKind>();
    foreach (var fo in fights) {
      var fx = fo as Dictionary<string, object>; if (fx == null) continue;
      n++;
      int stage = (int)Num(fx, "stage"); uint frames = (uint)Num(fx, "frames");
      string tag = "replay " + stage + ": ";
      var stats = (List<object>)fx["stats"]; var dmgT = (Dictionary<string, object>)fx["dmgT"];
      // the curves: every frame at which any hero had a point
      var fset = new SortedDictionary<long, bool>();
      foreach (var kv in dmgT) foreach (var p in (List<object>)kv.Value) fset[(long)(double)((List<object>)p)[0]] = true;
      var idx = new Dictionary<string, int>();
      var feed = new DpsFeed();
      foreach (var kv in dmgT) idx[kv.Key] = 0;
      foreach (var fr in fset.Keys) {
        if (fr >= frames) continue;
        var lines = new List<StatLine>();
        foreach (var kv in dmgT) {
          var pts = (List<object>)kv.Value; int i = idx[kv.Key];
          while (i + 1 < pts.Count && (long)(double)((List<object>)pts[i + 1])[0] <= fr) i++;
          idx[kv.Key] = i;
          var pt = (List<object>)pts[i]; if ((long)(double)pt[0] > fr) continue;
          string[] cu = kv.Key.Split(':');
          lines.Add(new StatLine { C = uint.Parse(cu[0]), Unit = int.Parse(cu[1]), Damage = (long)(double)pt[1], Heal = (int)(double)pt[2], Taken = (int)(double)pt[3] });
        }
        feed.Push(new DpsTick { Stage = stage, Frame = (uint)fr, Stats = lines });
      }
      var mid = feed.Snapshot;
      // the end: the statistics as the recorder reads them last, with the skills
      var fin = new List<StatLine>(); var sk = new Dictionary<string, Dictionary<int, long>>();
      foreach (var so in stats) {
        var s = (Dictionary<string, object>)so;
        var l = new StatLine { C = (uint)Num(s, "c"), Unit = (int)Num(s, "unit"), Hero = (uint)Num(s, "hero"), Damage = (long)Num(s, "damage"), ToBoss = (long)Num(s, "toBoss"),
          Heal = (int)Num(s, "heal"), Taken = (int)Num(s, "taken"), Overflow = (long)Num(s, "overflow") };
        fin.Add(l);
        object so2;
        if (s.TryGetValue("skills", out so2)) {
          var d = new Dictionary<int, long>();
          foreach (var kv in (Dictionary<string, object>)so2) d[int.Parse(kv.Key)] = (long)(double)kv.Value;
          sk[l.C + ":" + l.Unit] = d;
        }
      }
      feed.Push(new DpsTick { Stage = stage, Frame = frames, Final = true, Stats = fin, Skills = sk.Count > 0 ? sk : null });
      var sn = feed.Snapshot; var cu2 = sn.Current;
      Check(mid.Current != null && mid.Current.Open && cu2 != null && cu2.Final && !cu2.Open && cu2.Frames == frames && sn.Rejected == 0, tag + "one fight, closed by the final reading, nothing dropped (" + fset.Count + " readings)");
      kinds.Add(cu2.Kind);
      uint primary = 0; foreach (var l in fin) if (l.C != 0 && (primary == 0 || l.C < primary)) primary = l.C;
      int own = 0; foreach (var l in fin) if (l.C == primary) own++;
      bool side = cu2.Primary == primary && cu2.Heroes.Count == own;
      foreach (var h in cu2.Heroes) if (h.C != primary) side = false;
      Check(side, tag + "only the player's own side (controller " + primary + ", " + own + " heroes)" + (cu2.Kind == DpsKind.Arena ? " on the arena" : ""));
      bool tot = true; long sumDmg = 0;
      foreach (var h in cu2.Heroes) {
        long want = 0; foreach (var l in fin) if (l.C == h.C && l.Unit == h.Unit) want = l.Damage;
        if (h.Value[0] != want) tot = false;
        sumDmg += h.Value[0];
      }
      Check(tot, tag + "each hero's total == the statistics' damage (sum " + sumDmg + ")");
      bool shares = true, sane = true, fwd = true;
      for (int m = 0; m < 5; m++) {
        double sum = 0; long tv = 0;
        foreach (var h in cu2.Heroes) { sum += h.Share[m]; tv += h.Value[m]; }
        if (tv > 0 && Math.Abs(sum - 1) > 1e-9) shares = false;
        if (tv == 0 && sum != 0) shares = false;
      }
      foreach (var h in cu2.Heroes) {
        for (int m = 0; m < 5; m++) if (!Sane(h.Avg[m]) || !Sane(h.Recent[m]) || !Sane(h.Share[m]) || h.Value[m] < 0) sane = false;
        for (int i = 0; i < h.Series.Length; i++) {
          for (int c = 0; c < 6; c++) if (h.Series[i][c] < 0) sane = false;
          if (i > 0 && (h.Series[i][0] <= h.Series[i - 1][0] || h.Series[i][1] < h.Series[i - 1][1])) fwd = false;
        }
        if (h.Skills != null) foreach (var k in h.Skills) if (!Sane(k.Share) || !Sane(k.Dps) || !Sane(k.Recent) || k.Amount < 0) sane = false;
      }
      Check(shares, tag + "shares add up to 1 per metric");
      Check(sane && fwd, tag + "no NaN, no negatives, curves go forward");
      for (int i = 1; i < cu2.Heroes.Count; i++) if (cu2.Heroes[i].Value[0] > cu2.Heroes[i - 1].Value[0]) sane = false;
      Check(sane && Near(cu2.Seconds, frames * 270.0 / 4096, 1e-9), tag + "heroes by damage, the fight's seconds from the frames");
      if (sk.Count > 0) {
        withSkills++;
        bool ok = true; int heroesWith = 0, ownSk = 0;
        foreach (var key in sk.Keys) if (key.StartsWith(primary + ":", StringComparison.Ordinal)) ownSk++;
        foreach (var h in cu2.Heroes) {
          if (h.Skills == null) { if (sk.ContainsKey(h.C + ":" + h.Unit)) ok = false; continue; }
          heroesWith++;
          long a = 0; double sh = 0; foreach (var k in h.Skills) { a += k.Amount; sh += k.Share; }
          if (a != h.Value[0] || Math.Abs(sh - 1) > 1e-9) ok = false;
          // grouped by the owning skill: nothing is lost, shares add up, biggest first
          var rows = DpsSkillRows.Group(h.Unit, h.Skills);
          long ga = 0; double gs = 0;
          for (int i = 0; i < rows.Count; i++) {
            ga += rows[i].Amount; gs += rows[i].Share;
            if (i > 0 && !rows[i].Tail && rows[i].Amount > rows[i - 1].Amount) grpBad++;
          }
          foreach (var k in h.Skills) { int rt = DpsNames.SkillRoot(h.Unit, k.Id); if (!DpsNames.KnownSkill(rt) && DpsNames.Category(rt, true) == null) grpOther += k.Amount; }
          grpTotal += a;
          if (ga != a || Math.Abs(gs - 1) > 1e-9 || rows.Count > h.Skills.Count) grpBad++;
        }
        Check(ok && heroesWith == ownSk && cu2.SkillsOn && !sn.SkillsOff, tag + "skills: sum == damage for each hero (" + heroesWith + " heroes), shares add up");
      }
      Check(sn.Total != null && sn.Total.Fights == 1 && sn.Total.Heroes.Count == cu2.Heroes.Count && sn.Total.Heroes[0].Value[0] == cu2.Heroes[0].Value[0], tag + "the session total of one fight == the fight");
      // the mid-fight curve ends below or at the final value
      bool le = true;
      foreach (var h in mid.Current.Heroes) foreach (var h2 in cu2.Heroes) if (h.C == h2.C && h.Unit == h2.Unit && h.Value[0] > h2.Value[0]) le = false;
      Check(le, tag + "the curve's last reading <= the final statistics");
    }
    Check(grpTotal > 0 && grpBad == 0, "replay: grouped skill rows keep the sum, shares add up to 1, biggest first");
    double otherShare = grpTotal > 0 ? grpOther / (double)grpTotal : 1;
    Console.WriteLine("  (the unknown skill ids on the fixture: " + (otherShare * 100).ToString("0.00") + "% of the damage)");
    Check(otherShare <= 0.05, "replay: the unnamed rest (\"Прочее\") is at most 5% of the damage on the fixture");
    Check(n >= 4 && withSkills >= 2, "replay: " + n + " fights, " + withSkills + " with skills");
    Check(kinds.Contains(DpsKind.Arena) && kinds.Contains(DpsKind.Boss) && kinds.Contains(DpsKind.Malrik), "replay: arena, boss and Malric fights covered");
  }
}

static partial class CoreTests {
  static DpsTick DpsT(int stage, uint frame, long dmg, bool fin = false) {
    return new DpsTick { Stage = stage, Frame = frame, Final = fin, Stats = new List<StatLine> { new StatLine { C = 1, Unit = 2266, Damage = dmg, ToBoss = dmg } } };
  }

  // the window goes back to the general view at each new fight (src/DpsFeed.cs DpsFightWatch): a new stage, a frame going back, a tick after the final one, five rejected in a row
  static void DpsNewFightTests() {
    Console.WriteLine("DPS meter: a new fight is told to the window (back to the general view):");
    var feed = new DpsFeed(); var w = new DpsFightWatch();
    feed.Push(DpsT(5001007, 100, 1000));
    Check(!w.Changed(feed.Snapshot), "new fight: the first snapshot is not a change");
    feed.Push(DpsT(5001007, 400, 5000));
    Check(!w.Changed(feed.Snapshot), "new fight: the same fight going on is not a change");
    feed.Push(DpsT(5001007, 700, 9000, true));
    Check(!w.Changed(feed.Snapshot), "new fight: the final tick closes the fight, not a new one");
    feed.Push(DpsT(5001007, 800, 100));
    Check(w.Changed(feed.Snapshot), "new fight: a tick after the final one starts one");
    feed.Push(DpsT(5001007, 1000, 4000));
    Check(!w.Changed(feed.Snapshot), "new fight: going on again, no change");
    feed.Push(DpsT(5001008, 1100, 50));
    Check(w.Changed(feed.Snapshot), "new fight: another stage");
    feed.Push(DpsT(5001008, 1400, 6000));
    Check(!w.Changed(feed.Snapshot), "new fight: still the same");
    feed.Push(DpsT(5001008, 60, 20));
    Check(w.Changed(feed.Snapshot), "new fight: the frame went back");
    // five ticks in a row with the damage going down: the fight is thrown out and a new one starts
    feed.Push(DpsT(5001008, 400, 8000));
    w.Changed(feed.Snapshot);
    int before = feed.Snapshot.Serial;
    for (uint i = 0; i < 6; i++) feed.Push(DpsT(5001008, 600 + i * 60, 10 + i));
    Check(feed.Snapshot.Serial == before + 1 && w.Changed(feed.Snapshot), "new fight: five rejected ticks in a row start a new fight");
    feed.Push(DpsT(5001008, 1200, 500));
    Check(!w.Changed(feed.Snapshot), "new fight: no change after it");
    Check(!new DpsFightWatch().Changed(null), "new fight: no snapshot, no change");
  }
}

