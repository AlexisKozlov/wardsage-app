// RealmForge — the per-frame hero attack event log (TEST build only; nothing here reads memory: src/BattleTimeline.cs reads the
// snapshots, this decodes them). The game has no attack event or cast counter for heroes, so the casts are derived from two
// channels the recorder polls fast (a light thread, about every ms):
//   * the cooldown decode (primary): a cast sets the skill's SkillCDData to cd = cdMax; every game frame (66 ms) the cooldown
//     goes down by 66 once the cast time (iLastTime) has run out. At a poll at frame F with (cd, max, last, pause): last == 0,
//     not paused, cd >= 1 and (max - cd) % 66 == 0 give k = (max - cd) / 66 frames since the cast, castFrame = F - k (+ Shift, a
//     constant 0 or 1 to fix on a live fight; the raw value is stored so the site's script can fix it). A new event when the
//     castFrame differs from the skill's last one by more than 1. cd went up against the previous poll with the same max: a new
//     event even when the decode fails (flag nodecode). last > 0: an event at the first poll with cd == max (flag late).
//     (max - cd) % 66 != 0: flag odd.
//   * the cast script (secondary): SkillComponent.cUseSkill running false -> true (or another script / skill) = start, true ->
//     false = end; runFrames = end - start (the k of the site's attack-lock.json); merged with a cooldown event of the same skill
//     within 2 frames, else an event of its own flagged nocd.
// Loss rule: with the poll interval S frames and the hero's cooldown C = ceil(n / 66): S < C loses nothing; S >= C a second cast
// inside one interval overwrites the first (flagged by the site's gap rule, never invented here).
// JSON (inside timeline v3, TimelineBuilder.ToJson): "atk":{"<hero key as in heroes>":{"unit":U,"c":C,"s0":basic id,"s1":ultimate id,
//   "ev":[[castFrame,skillId,kind,n,cdMax,runFrames|-1,flags]],"nT":[[frame,nAttackCD]]}},
// "atkMeta":{"pollMs":P,"polls":N,"torn":T,"fpp":{"0":a,"1":b,"2":c,"3+":d},"cut":X}
// kind 0 basic attack (the skill is ArraySlots[0], or a converted id of it), 1 ultimate (ArraySlots[1]), 2 other; the raw skill id is
// always kept. n = the hero's live attack cooldown (BattleAttributeData.m_nAttackCD, ms) at the cast, cdMax = SkillCDData.iCoolDownMax.
// flags bits: 1 decoded exact, 2 late, 4 odd, 8 nocd (a cast script run no cooldown event explains), 16 nodecode. nT: the attack
// cooldown by frame, only on change. Caps: 3000 events a hero, 30000 in all (the overflow is counted in "cut").
// C# 5 (the core is compiled as C# 5).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealmForge {
  /// <summary>One SkillCDData as read: the dictionary key and the object's fields.</summary>
  public sealed class CdSnap {
    public int Key, SkillId, Cd, Max, Last;
    public bool Pause;
    public CdSnap() { }
    public CdSnap(int key, int skillId, int cd, int max, int last, bool pause) { Key = key; SkillId = skillId; Cd = cd; Max = max; Last = last; Pause = pause; }
  }

  /// <summary>The hero's cast script (SkillComponent.cUseSkill) as read.</summary>
  public sealed class UseSnap {
    public bool Running; public ulong Ptr; public int SkillId;
    public UseSnap() { }
    public UseSnap(bool running, ulong ptr, int skillId) { Running = running; Ptr = ptr; SkillId = skillId; }
  }

  public sealed class AttackLog {
    public const int FrameMs = 66;                                   // the game's frame in cooldown units
    public const int FlagExact = 1, FlagLate = 2, FlagOdd = 4, FlagNoCd = 8, FlagNoDecode = 16;
    public const int MaxEventsPerHero = 3000, MaxEvents = 30000;

    /// <summary>The constant c (0 or 1) added to the decoded cast frame; fixed by a live fight.</summary>
    public int Shift;
    /// <summary>The average ms per poll (set by the recorder).</summary>
    public double PollMs;

    sealed class Track { public bool Has, HasCast, LateOpen, FromLate; public int Cd, Max; public long CastFrame; }
    sealed class Hero {
      public uint C, Uid; public int Unit, S0, S1, CurN = -1;
      public readonly List<long[]> Ev = new List<long[]>();          // [castFrame, skill, kind, n, cdMax, runFrames, flags]
      public readonly List<long[]> NT = new List<long[]>();
      public readonly Dictionary<int, Track> Tracks = new Dictionary<int, Track>();
      public bool UseRun; public ulong UsePtr; public int UseSkill; public long UseStart;
    }

    readonly object gate = new object();
    readonly Dictionary<ulong, Hero> heroes = new Dictionary<ulong, Hero>();
    readonly List<ulong> order = new List<ulong>();
    int total, cut, polls, torn, pooled;
    readonly int[] fpp = new int[4];
    long lastPollFrame = -1;

    static ulong HKey(uint c, uint uid) { return ((ulong)c << 32) | uid; }

    public int Polls { get { lock (gate) return polls; } }
    public int Torn { get { lock (gate) return torn; } }
    public int Cut { get { lock (gate) return cut; } }
    public int Pooled { get { lock (gate) return pooled; } }
    public int EventCount { get { lock (gate) return total; } }
    public bool HasData { get { lock (gate) return order.Count > 0 || polls > 0; } }

    /// <summary>A poll thrown away: the clock moved while the hero block was read.</summary>
    public void NoteTorn() { lock (gate) torn++; }

    /// <summary>A poll that was read whole, at <paramref name="frame"/>: the frames-per-poll histogram.</summary>
    public void NotePoll(uint frame) {
      lock (gate) {
        polls++;
        if (lastPollFrame >= 0 && frame >= lastPollFrame) { long d = frame - lastPollFrame; fpp[d >= 3 ? 3 : (int)d]++; }
        lastPollFrame = frame;
      }
    }

    /// <summary>The events of one hero as [castFrame, skill, kind, n, cdMax, runFrames, flags] (a copy; for the tests).</summary>
    public List<long[]> Events(uint c, uint uid) {
      lock (gate) {
        Hero h; var res = new List<long[]>();
        if (heroes.TryGetValue(HKey(c, uid), out h)) foreach (var e in h.Ev) res.Add((long[])e.Clone());
        return res;
      }
    }

    static int KindOf(int skill, int s0, int s1, IDictionary<int, int> conv) {
      int mapped = skill, m;
      if (conv != null && conv.TryGetValue(skill, out m)) mapped = m;
      if (s0 != 0 && (skill == s0 || mapped == s0)) return 0;
      if (s1 != 0 && (skill == s1 || mapped == s1)) return 1;
      return 2;
    }

    void Add(Hero h, long frame, int skill, int s0, int s1, IDictionary<int, int> conv, int cdMax, long run, int flags) {
      if (h.Ev.Count >= MaxEventsPerHero || total >= MaxEvents) { cut++; return; }
      h.Ev.Add(new long[] { frame, skill, KindOf(skill, s0, s1, conv), h.CurN > 0 ? h.CurN : 0, cdMax, run, flags });
      total++;
    }

    /// <summary>One poll of one hero. <paramref name="cds"/>: its cooldown entries (null = unreadable), <paramref name="use"/>: its
    /// cast script, s0 / s1: ArraySlots [0] / [1], <paramref name="conv"/>: vSkillConvertRelations (optional),
    /// <paramref name="attackCd"/>: m_nAttackCD (negative = not read). Each hero's polls must come in frame order.</summary>
    public void Feed(uint c, uint uid, int unit, uint frame, IList<CdSnap> cds, UseSnap use, int s0, int s1, IDictionary<int, int> conv, int attackCd) {
      lock (gate) {
        Hero h; ulong hk = HKey(c, uid);
        if (!heroes.TryGetValue(hk, out h)) { h = new Hero { C = c, Uid = uid }; heroes[hk] = h; order.Add(hk); }
        h.Unit = unit; h.S0 = s0; h.S1 = s1;
        if (attackCd >= 0 && attackCd != h.CurN) {
          h.CurN = attackCd;
          if (h.NT.Count < MaxEventsPerHero) h.NT.Add(new long[] { frame, attackCd }); else cut++;
        }
        if (cds != null) foreach (var s in cds) Cooldown(h, frame, s, s0, s1, conv);
        if (use != null) Use(h, frame, use, s0, s1, conv);
      }
    }

    void Cooldown(Hero h, uint frame, CdSnap s, int s0, int s1, IDictionary<int, int> conv) {
      if (s.SkillId != s.Key) { h.Tracks.Remove(s.Key); pooled++; return; }   // a pooled object reused for another skill: nothing to say
      Track t;
      if (!h.Tracks.TryGetValue(s.Key, out t)) { t = new Track(); h.Tracks[s.Key] = t; }
      bool up = t.Has && t.Max == s.Max && s.Cd > t.Cd;
      if (s.Last > 0) {
        // the cast time is running: the cooldown waits at its start value; the first poll that sees it is the cast
        if (!t.LateOpen && s.Cd == s.Max && s.Max > 0) { Add(h, frame, s.Key, s0, s1, conv, s.Max, -1, FlagLate); t.CastFrame = frame; t.HasCast = true; }
        t.LateOpen = true;
      } else {
        if (t.LateOpen) { t.LateOpen = false; t.FromLate = true; }
        if (!s.Pause && s.Cd >= 1) {
          bool exact = s.Max > 0 && s.Cd <= s.Max && (s.Max - s.Cd) % FrameMs == 0;
          if (exact) {
            long cf = (long)frame - (s.Max - s.Cd) / FrameMs + Shift;
            if (t.FromLate) { t.CastFrame = cf; t.HasCast = true; t.FromLate = false; }   // the late event already reported this cast
            else if (!t.HasCast || Math.Abs(cf - t.CastFrame) > 1) { Add(h, cf, s.Key, s0, s1, conv, s.Max, -1, FlagExact); t.CastFrame = cf; t.HasCast = true; }
          } else if (up) {
            Add(h, frame, s.Key, s0, s1, conv, s.Max, -1, FlagOdd | FlagNoDecode); t.CastFrame = frame; t.HasCast = true; t.FromLate = false;
          }
        } else if (s.Cd < 1) t.FromLate = false;
      }
      t.Has = true; t.Cd = s.Cd; t.Max = s.Max;
    }

    void Use(Hero h, uint frame, UseSnap u, int s0, int s1, IDictionary<int, int> conv) {
      if (h.UseRun && (!u.Running || u.Ptr != h.UsePtr || u.SkillId != h.UseSkill)) {
        long run = (long)frame - h.UseStart;
        long[] hit = null;
        for (int i = h.Ev.Count - 1; i >= 0 && i >= h.Ev.Count - 16; i--) {
          var e = h.Ev[i];
          if (e[1] == h.UseSkill && e[5] < 0 && Math.Abs(e[0] - h.UseStart) <= 2) { hit = e; break; }
        }
        if (hit != null) hit[5] = run;
        else Add(h, h.UseStart, h.UseSkill, s0, s1, conv, 0, run, FlagNoCd);
        h.UseRun = false;
      }
      if (u.Running && !h.UseRun) { h.UseRun = true; h.UsePtr = u.Ptr; h.UseSkill = u.SkillId; h.UseStart = frame; }
    }

    /// <summary>Appends <c>,"atk":{...},"atkMeta":{...}</c> (the hero keys as the timeline's "heroes": a bare uid for the primary
    /// controller's heroes and the unknown, "controller:uid" for the others).</summary>
    public void AppendJson(StringBuilder sb, uint primary) {
      lock (gate) {
        var ci = CultureInfo.InvariantCulture;
        sb.Append(",\"atk\":{");
        bool first = true;
        foreach (var hk in order) {
          var h = heroes[hk];
          if (!first) sb.Append(','); first = false;
          sb.Append('"');
          if (!(h.C == 0 || h.C == primary)) sb.Append(h.C).Append(':');
          sb.Append(h.Uid).Append("\":{\"unit\":").Append(h.Unit).Append(",\"c\":").Append(h.C).Append(",\"s0\":").Append(h.S0).Append(",\"s1\":").Append(h.S1).Append(",\"ev\":[");
          for (int i = 0; i < h.Ev.Count; i++) {
            if (i > 0) sb.Append(',');
            var e = h.Ev[i];
            sb.Append('[').Append(e[0]).Append(',').Append(e[1]).Append(',').Append(e[2]).Append(',').Append(e[3]).Append(',').Append(e[4]).Append(',').Append(e[5]).Append(',').Append(e[6]).Append(']');
          }
          sb.Append("],\"nT\":[");
          for (int i = 0; i < h.NT.Count; i++) { if (i > 0) sb.Append(','); sb.Append('[').Append(h.NT[i][0]).Append(',').Append(h.NT[i][1]).Append(']'); }
          sb.Append("]}");
        }
        sb.Append("},\"atkMeta\":{\"pollMs\":").Append(Math.Round(PollMs, 2).ToString("0.##", ci)).Append(",\"polls\":").Append(polls).Append(",\"torn\":").Append(torn)
          .Append(",\"fpp\":{\"0\":").Append(fpp[0]).Append(",\"1\":").Append(fpp[1]).Append(",\"2\":").Append(fpp[2]).Append(",\"3+\":").Append(fpp[3])
          .Append("},\"cut\":").Append(cut).Append('}');
      }
    }
  }
}
