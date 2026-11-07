// The live DPS meter's data (no WinForms, testable): the fight recorder's statistics ticks in, an immutable snapshot out.
// Read-only: nothing here touches the game or the fight record. The recorder calls Push on its own thread about once a second;
// the window reads Snapshot (a volatile reference, no lock).
using System;
using System.Collections.Generic;

namespace RealmForge {
  /// <summary>One reading of the fight's statistics (FightRecorder.OnStats): about every second, and once at the end (Final).</summary>
  public sealed class DpsTick {
    public int Stage; public uint Frame; public bool Final;
    public List<StatLine> Stats;
    /// <summary>"controller:unit" -> skill id -> amount; null when not read.</summary>
    public Dictionary<string, Dictionary<int, long>> Skills;
  }

  public enum DpsMode { Damage = 0, ToBoss = 1, Heal = 2, Taken = 3, Overflow = 4 }
  public enum DpsSegment { Current, Previous, Total }
  public enum DpsKind { Other, Arena, Boss, Malrik }

  /// <summary>One skill of a hero: the amount, its share of the hero's skill sum, DPS over the fight and over the last N seconds.</summary>
  public sealed class DpsSkill {
    public int Id; public long Amount; public double Share, Dps, Recent;
  }

  /// <summary>One hero in one segment (immutable once published).</summary>
  public sealed class DpsHero {
    public uint C; public int Unit, Base; public uint Hero, Power;
    /// <summary>Totals by <see cref="DpsMode"/>.</summary>
    public long[] Value = new long[5];
    /// <summary>Share of the visible heroes' total, per second over the whole fight, per second over the last N seconds (0 in the total segment).</summary>
    public double[] Share = new double[5], Avg = new double[5], Recent = new double[5];
    /// <summary>Per skill, biggest first; null when the skills are off or did not add up.</summary>
    public List<DpsSkill> Skills;
    /// <summary>The curve (current fight only): points [frame, damage, toBoss, heal, taken, overflow]; null otherwise.</summary>
    public long[][] Series;
    public long Get(DpsMode m) { return Value[(int)m]; }
  }

  /// <summary>One segment: the current fight, the previous one, or the whole session.</summary>
  public sealed class DpsSegmentView {
    public int Stage; public DpsKind Kind; public uint Primary;
    public uint Frames; public double Seconds; public bool Final, Open; public int Fights;
    public bool SkillsOn;
    public List<DpsHero> Heroes = new List<DpsHero>();   // by damage, biggest first
    /// <summary>The heroes by a mode, biggest first (a new list).</summary>
    public List<DpsHero> Sorted(DpsMode m) {
      var l = new List<DpsHero>(Heroes);
      int i = (int)m;
      l.Sort(delegate(DpsHero a, DpsHero b) { int c = b.Value[i].CompareTo(a.Value[i]); return c != 0 ? c : a.Unit.CompareTo(b.Unit); });
      return l;
    }
  }

  public sealed class DpsSnapshot {
    public int Version, Rejected; public bool SkillsOff; public int WindowSec;
    public DpsSegmentView Current, Previous, Total;
    public DpsSegmentView Get(DpsSegment s) { return s == DpsSegment.Current ? Current : s == DpsSegment.Previous ? Previous : Total; }
  }

  public sealed class DpsFeed {
    public const double FrameSec = TimelineBuilder.FrameSec;
    /// <summary>A point of a curve about every second of the fight (15 frames).</summary>
    public const uint StepFrames = 15;
    public const int MaxPoints = 2400;
    /// <summary>The skills' sum may differ from the damage by this much while the two are read a moment apart (a share, and a floor).</summary>
    public const double SkillTolerance = 0.03; public const long SkillToleranceMin = 200;
    public const int SkillErrorsOff = 3, RejectsNewFight = 5;

    /// <summary>The window of the "recent" DPS, seconds.</summary>
    public int WindowSec = 10;
    /// <summary>Show every controller. Off by default: the live statistics of boss, Malric and stage fights carry the monsters' side too
    /// (controllers 100 and 101, unit 0), so only the player's own side (the lowest controller) is shown, as on the arena.</summary>
    public bool AllSides;

    readonly object gate = new object();
    volatile DpsSnapshot snap = new DpsSnapshot { WindowSec = 10 };
    public DpsSnapshot Snapshot { get { return snap; } }

    sealed class SkState { public long Amount; public List<long[]> Pts = new List<long[]>(); }
    sealed class HeroState {
      public string Key; public uint C; public int Unit, Base; public uint Hero, Power;
      public long[] Cur = new long[5]; public List<long[]> Pts = new List<long[]>();
      public Dictionary<int, SkState> Skills = new Dictionary<int, SkState>(); public bool SkillsBad;
    }
    sealed class Fight {
      public int Stage; public DpsKind Kind; public uint Primary, LastFrame, LastPt, FirstFrame; public bool Final, Closed, HasPt;
      public Dictionary<string, HeroState> Heroes = new Dictionary<string, HeroState>(); public List<string> Order = new List<string>();
      public long[] Sums = new long[4]; public int RejectStreak, SkillErrors; public bool SkillsOff, SkillsSeen;
    }
    sealed class Acc {
      public uint C; public int Unit, Base; public uint Hero, Power; public long[] Value = new long[5]; public Dictionary<int, long> Skills = new Dictionary<int, long>();
    }

    Fight cur, prev;
    Dictionary<string, Acc> closed = new Dictionary<string, Acc>(); uint closedFrames; int closedFights;
    int version, rejected;

    public static DpsKind KindOf(int stage) {
      if (TraceUpload.IsArenaStage(stage)) return DpsKind.Arena;
      if (TraceUpload.IsMalrikStage(stage)) return DpsKind.Malrik;
      if (TraceUpload.IsBossFight(stage, null)) return DpsKind.Boss;
      return DpsKind.Other;
    }

    /// <summary>Forgets every fight and the session total.</summary>
    public void Reset() {
      lock (gate) {
        cur = null; prev = null; closed = new Dictionary<string, Acc>(); closedFrames = 0; closedFights = 0; rejected = 0;
        Publish();
      }
    }

    /// <summary>Takes a reading. Never throws.</summary>
    public void Push(DpsTick t) {
      if (t == null || t.Stats == null) return;
      try { lock (gate) { Take(t); Publish(); } } catch (Exception) { }
    }

    void Take(DpsTick t) {
      bool fresh = cur == null || t.Stage != cur.Stage || t.Frame < cur.LastFrame;
      if (!fresh && cur.Final) { if (t.Frame == cur.LastFrame) return; fresh = true; }
      if (fresh) StartFight(t);
      var f = cur;
      if (!t.Final && f.HasPt && t.Frame < f.LastPt + StepFrames) return;   // about one point a second
      if (!Accept(f, t)) {
        rejected++;
        if (++f.RejectStreak >= RejectsNewFight && !t.Final) { f.RejectStreak = 0; StartFight(t); f = cur; if (!Accept(f, t)) { rejected++; return; } }
        else { if (t.Final) { f.Final = true; Close(f); } return; }
      }
      f.RejectStreak = 0;
      if (t.Final) { f.Final = true; Close(f); }
    }

    void StartFight(DpsTick t) {
      if (cur != null) { if (!cur.Closed) Close(cur); if (cur.Order.Count > 0) prev = cur; }
      cur = new Fight { Stage = t.Stage, Kind = KindOf(t.Stage) };
    }

    static bool Arena(Fight f) { return f.Kind == DpsKind.Arena; }

    bool Accept(Fight f, DpsTick t) {
      uint primary = f.Primary;
      foreach (var s in t.Stats) if (s != null && s.C != 0 && (primary == 0 || s.C < primary)) primary = s.C;
      var vis = new List<StatLine>();
      var sums = new long[4];
      foreach (var s in t.Stats) {
        if (s == null) continue;
        if (!AllSides && s.C != primary) continue;
        if (s.Damage < 0 || s.ToBoss < 0 || s.Heal < 0 || s.Taken < 0 || s.Overflow < 0) return false;
        vis.Add(s);
        sums[0] += s.Damage; sums[1] += s.ToBoss; sums[2] += s.Heal; sums[3] += s.Taken;
      }
      for (int i = 0; i < 4; i++) if (sums[i] < f.Sums[i]) return false;   // the totals only grow
      f.Primary = primary;
      if (!f.HasPt) f.FirstFrame = t.Frame;
      for (int i = 0; i < 4; i++) f.Sums[i] = sums[i];
      foreach (var s in vis) {
        string key = s.C + ":" + s.Unit;
        HeroState h;
        if (!f.Heroes.TryGetValue(key, out h)) { h = new HeroState { Key = key, C = s.C, Unit = s.Unit }; f.Heroes[key] = h; f.Order.Add(key); }
        h.Base = s.Base; h.Hero = s.Hero; h.Power = s.Power;
        var v = new long[] { s.Damage, s.ToBoss, s.Heal, s.Taken, s.Overflow };
        bool changed = h.Pts.Count == 0;
        for (int i = 0; i < 5 && !changed; i++) if (h.Cur[i] != v[i]) changed = true;
        h.Cur = v;
        if (changed) AddPoint(h.Pts, new long[] { t.Frame, v[0], v[1], v[2], v[3], v[4] });
      }
      f.LastFrame = t.Frame; f.LastPt = t.Frame; f.HasPt = true;
      if (t.Skills != null && !f.SkillsOff) TakeSkills(f, t, vis);
      return true;
    }

    static void AddPoint(List<long[]> pts, long[] p) {
      if (pts.Count >= MaxPoints) {   // thin out: every second point goes, the last stays
        var keep = new List<long[]>(MaxPoints / 2 + 1);
        for (int i = 0; i < pts.Count; i += 2) keep.Add(pts[i]);
        if (keep[keep.Count - 1] != pts[pts.Count - 1]) keep.Add(pts[pts.Count - 1]);
        pts.Clear(); pts.AddRange(keep);
      }
      pts.Add(p);
    }

    void TakeSkills(Fight f, DpsTick t, List<StatLine> vis) {
      bool bad = false; int n = 0;
      foreach (var s in vis) {
        Dictionary<int, long> d;
        if (!t.Skills.TryGetValue(s.C + ":" + s.Unit, out d) || d == null || d.Count == 0) continue;
        n++;
        long sum = 0;
        foreach (var kv in d) { if (kv.Value < 0) bad = true; sum += kv.Value; }
        long tol = t.Final ? 0 : Math.Max(SkillToleranceMin, (long)(s.Damage * SkillTolerance));
        if (Math.Abs(sum - s.Damage) > tol) bad = true;
        HeroState h;
        if (f.Heroes.TryGetValue(s.C + ":" + s.Unit, out h)) foreach (var kv in d) {
          SkState k; if (h.Skills.TryGetValue(kv.Key, out k) && kv.Value < k.Amount) bad = true;
        }
      }
      if (n == 0) return;
      if (bad) {
        if (t.Final) { foreach (var s in vis) { HeroState h; if (f.Heroes.TryGetValue(s.C + ":" + s.Unit, out h)) h.SkillsBad = true; } }
        if (++f.SkillErrors >= SkillErrorsOff) { f.SkillsOff = true; foreach (var h in f.Heroes.Values) { h.Skills.Clear(); h.SkillsBad = true; } }
        return;
      }
      f.SkillErrors = 0; f.SkillsSeen = true;
      foreach (var s in vis) {
        Dictionary<int, long> d; HeroState h;
        if (!t.Skills.TryGetValue(s.C + ":" + s.Unit, out d) || d == null || !f.Heroes.TryGetValue(s.C + ":" + s.Unit, out h)) continue;
        foreach (var kv in d) {
          SkState k;
          if (!h.Skills.TryGetValue(kv.Key, out k)) { k = new SkState(); h.Skills[kv.Key] = k; }
          else if (k.Amount == kv.Value) continue;
          k.Amount = kv.Value;
          AddPoint(k.Pts, new long[] { t.Frame, kv.Value });
        }
      }
    }

    /// <summary>The fight is over: its end values join the session total.</summary>
    void Close(Fight f) {
      if (f.Closed) return;
      f.Closed = true;
      if (f.Order.Count == 0) return;
      AddInto(closed, f);
      closedFrames += f.LastFrame; closedFights++;
    }

    static void AddInto(Dictionary<string, Acc> into, Fight f) {
      foreach (var key in f.Order) {
        var h = f.Heroes[key];
        Acc a;
        if (!into.TryGetValue(key, out a)) { a = new Acc { C = h.C, Unit = h.Unit }; into[key] = a; }
        a.Base = h.Base; a.Hero = h.Hero; a.Power = h.Power;
        for (int i = 0; i < 5; i++) a.Value[i] += h.Cur[i];
        if (!f.SkillsOff && !h.SkillsBad)
          foreach (var kv in h.Skills) { long x; a.Skills.TryGetValue(kv.Key, out x); a.Skills[kv.Key] = x + kv.Value.Amount; }
      }
    }

    // ------------------------------------------------------------------ the snapshot

    void Publish() {
      var s = new DpsSnapshot { Version = ++version, Rejected = rejected, WindowSec = WindowSec };
      if (cur != null) { s.Current = ViewOf(cur, !cur.Final); s.SkillsOff = cur.SkillsOff; }
      if (prev != null) s.Previous = ViewOf(prev, false);
      s.Total = TotalView();
      snap = s;
    }

    static void Shares(List<DpsHero> heroes) {
      for (int m = 0; m < 5; m++) {
        long sum = 0; foreach (var h in heroes) sum += h.Value[m];
        foreach (var h in heroes) h.Share[m] = sum > 0 ? h.Value[m] / (double)sum : 0;
      }
    }

    static void SortByDamage(List<DpsHero> l) {
      l.Sort(delegate(DpsHero a, DpsHero b) { int c = b.Value[0].CompareTo(a.Value[0]); return c != 0 ? c : a.Unit.CompareTo(b.Unit); });
    }

    static long ValueAt(List<long[]> pts, long frame, int col) {
      int lo = 0, hi = pts.Count - 1, at = -1;
      while (lo <= hi) { int mid = (lo + hi) >> 1; if (pts[mid][0] <= frame) { at = mid; lo = mid + 1; } else hi = mid - 1; }
      return at < 0 ? 0 : pts[at][col];
    }

    DpsSegmentView ViewOf(Fight f, bool open) {
      var v = new DpsSegmentView { Stage = f.Stage, Kind = f.Kind, Primary = f.Primary, Frames = f.LastFrame, Seconds = f.LastFrame * FrameSec, Final = f.Final, Open = open, Fights = 1, SkillsOn = f.SkillsSeen && !f.SkillsOff };
      long winFrames = (long)Math.Round(WindowSec / FrameSec), target = (long)f.LastFrame - winFrames;
      double winSec = Math.Min((double)f.LastFrame, (double)winFrames) * FrameSec;
      foreach (var key in f.Order) {
        var s = f.Heroes[key];
        var h = new DpsHero { C = s.C, Unit = s.Unit, Base = s.Base, Hero = s.Hero, Power = s.Power };
        for (int m = 0; m < 5; m++) {
          h.Value[m] = s.Cur[m];
          h.Avg[m] = v.Seconds > 0 ? s.Cur[m] / v.Seconds : 0;
          h.Recent[m] = winSec > 0 ? (s.Cur[m] - ValueAt(s.Pts, target, m + 1)) / winSec : 0;
        }
        h.Series = s.Pts.ToArray();
        if (v.SkillsOn && !s.SkillsBad && s.Skills.Count > 0) {
          long total = 0; foreach (var k in s.Skills.Values) total += k.Amount;
          h.Skills = new List<DpsSkill>();
          foreach (var kv in s.Skills)
            h.Skills.Add(new DpsSkill { Id = kv.Key, Amount = kv.Value.Amount, Share = total > 0 ? kv.Value.Amount / (double)total : 0,
              Dps = v.Seconds > 0 ? kv.Value.Amount / v.Seconds : 0,
              Recent = winSec > 0 ? (kv.Value.Amount - ValueAt(kv.Value.Pts, target, 1)) / winSec : 0 });
          h.Skills.Sort(delegate(DpsSkill a, DpsSkill b) { int c = b.Amount.CompareTo(a.Amount); return c != 0 ? c : a.Id.CompareTo(b.Id); });
        }
        v.Heroes.Add(h);
      }
      Shares(v.Heroes); SortByDamage(v.Heroes);
      return v;
    }

    DpsSegmentView TotalView() {
      var acc = new Dictionary<string, Acc>(); uint frames = closedFrames; int fights = closedFights;
      foreach (var kv in closed) {
        var a = new Acc { C = kv.Value.C, Unit = kv.Value.Unit, Base = kv.Value.Base, Hero = kv.Value.Hero, Power = kv.Value.Power };
        Array.Copy(kv.Value.Value, a.Value, 5);
        foreach (var s in kv.Value.Skills) a.Skills[s.Key] = s.Value;
        acc[kv.Key] = a;
      }
      if (cur != null && !cur.Closed && cur.Order.Count > 0) { AddInto(acc, cur); frames += cur.LastFrame; fights++; }
      if (acc.Count == 0) return null;
      var v = new DpsSegmentView { Frames = frames, Seconds = frames * FrameSec, Fights = fights, Final = cur == null || cur.Closed, Open = cur != null && !cur.Closed };
      foreach (var kv in acc) {
        var a = kv.Value;
        var h = new DpsHero { C = a.C, Unit = a.Unit, Base = a.Base, Hero = a.Hero, Power = a.Power };
        for (int m = 0; m < 5; m++) { h.Value[m] = a.Value[m]; h.Avg[m] = v.Seconds > 0 ? a.Value[m] / v.Seconds : 0; }
        if (a.Skills.Count > 0) {
          long total = 0; foreach (var x in a.Skills.Values) total += x;
          h.Skills = new List<DpsSkill>();
          foreach (var s in a.Skills) h.Skills.Add(new DpsSkill { Id = s.Key, Amount = s.Value, Share = total > 0 ? s.Value / (double)total : 0, Dps = v.Seconds > 0 ? s.Value / v.Seconds : 0 });
          h.Skills.Sort(delegate(DpsSkill x, DpsSkill y) { int c = y.Amount.CompareTo(x.Amount); return c != 0 ? c : x.Id.CompareTo(y.Id); });
          v.SkillsOn = true;
        }
        v.Heroes.Add(h);
      }
      Shares(v.Heroes); SortByDamage(v.Heroes);
      return v;
    }
  }
}
