// RealmForge — a recorded fight's timeline, for the site's battle engine to replay the fight exactly: for every hero
// when it was placed and on which tile, when it fell or was taken back, when its ultimate went off; its squad and
// whether it leads it. Pure: built from samples of the running simulation and from the simulation's own record of the
// player's commands, both read by src/BattleTimeline.cs (read-only). No memory reading here, so it is unit-tested.
//
// Two sources, merged when the JSON is made:
//   * commands (FrameDataRecord, exact frames): SYNC_PUT_TOWER 1000 [unit, x, y, face], SYNC_RETREAT_TOWER 1001,
//     SYNC_USE_POWERSKILL 2000 [unit, x, y] — only what the player did by hand (the game's own auto placement and
//     auto ultimates call the simulation directly and leave no command);
//   * samples (~every 200 ms = ~3 frames): each hero card's state, its tower on the field (tile, dead flag), rage and
//     the frame of its last ultimate (SkillComponent.iUseSuperManualSkillFrameIdx, exact).
// A sampled event up to MatchFrames after a command of the same kind for the same hero (same controller) is that command.
//
// Sides. Every hero belongs to a controller (its card's OwnerRelation cUid = BattlePlayerInfo cUid): 1 = the player;
// on the arena 2 = the opponent's recorded defence, both on one field and often with the same heroes (the same uid), so
// a hero is (controller, uid). Its key: "<uid>" for the player's heroes (the lowest controller seen, or unknown), as
// before; "<controller>:<uid>" for the others. Every hero has "c". The commands' tiles are each side's own: the
// opponent's defence was recorded on the left half and the game mirrors it (the real fight 20261003-134630: its command
// [601,1000,2,2034,3,5,270] stood on the map tile 11,5 = 14 − 3). Tiles in "placed" are each side's own (local) tiles:
// a sampled tile of a non-player side is mirrored back, x = gridW − 1 − x, face 0 <-> 180, when gridW is set (arena).
//
// Arena (AddArena, stages 6001xxx/6002xxx): the wave controllers (WaveSystem m_vActiveController: 100 = side 1's waves,
// 101 = side 2's) and the bases. Per round (the controller's iCurWaveCount) and side: when the wave started, its first
// monster, when it was cleared (the game's own test, WaveControllerNode.DoAdvanceCellNode: NodeData.iActiveNode < 1 and
// TDStateData.vMonsterCount[c] <= vMonsterAdvanceIgnoreCount[c]), who cleared first; each base's HP; the judge's hits
// on the slower side's base (its HP drops between the other side's clear and its own whose size is one of the judge's
// rates, % of max HP: 2/4/6, 3.5/5.5/7.5, 6/8/10, 8/10/12, 12/16/20 — work/sim/ARENA.md §5).
//
// JSON: {"v":2,"frames":N,"frameSec":0.06591796875,"samples":K,["gridW":W,]
//        "heroes":{"<key>":{"c":C,"uid":U,"unit":U,"squad":S,"leader":false,"placed":[[frame,x,y,face,exact]],"fell":[frame],
//                  "retreat":[frame],"ult":[frame]}},
//        "ops":[[frame,cmd,cuid,params…]], "boss":[[frame,hp‰]],
//        "stats":[{"c":C,"unit":U,"hero":H,"at":"<hex>","power":P,"damage":D,"toBoss":B,"heal":H,"taken":T,"overflow":O}],
//        "arena":{"rounds":[{"wave":W,"start":{"1":f,"2":f},"first":{..},"clear":{..},"won":1|2|0,
//                            "judge":{"<side>":[[frame,‰ of max]]},"judgeLost":{"<side>":‰}}],
//                 "bases":[{"unit":U,"side":S,"owner":O,"max":M,"hp":[[frame,hp]]}],
//                 "raw":[[frame,ctl,wave,active,monsters,ignore,state]]}}
// exact = 1 for a placement from a command, 0 for a sampled one (tile from the map's tower grid, frame ≤ ~3 late).
// v1 files (before 1.6.24) have no "c": every hero by uid, both arena sides merged.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealmForge {
  /// <summary>One hero (its card and its tower on the field) at one sample.</summary>
  public sealed class HeroSample {
    public uint Uid;                 // hero uid (CardComponent.iUid = BattleHeroInfo.iUid)
    public uint C;                   // its controller (the card's OwnerRelation cUid = BattlePlayerInfo cUid), 0 = not known
    public int Unit;                 // unit table id (BattleHeroInfo.iUnitID; the commands name heroes by it)
    public int Squad; public bool Leader;
    public int CardState;            // ECardStatus: 0 running, 1 frozen, 2 reborn (after a death), 3 deleted
    public int RebornLeft;           // CardComponent.iCurRebornTime
    public uint Tower;               // the hero's entity on the field (CardComponent.iTowerUid), 0 = none
    public bool OnField;             // that entity exists
    public bool Dead; public int DeadType = -1;   // it has a DeadComponent (EDeadType: 0 killed, 2 retreat, 6 forced retreat)
    public int X = -1, Y = -1;       // its tile (MapMarker tower grid), -1 = not known
    public int Face = -1;
    public double Anger, MaxAnger; public int AngerState = -1;   // EAngerStatus 0 up, 1 down (after an ultimate)
    public uint UltFrame;            // frame of its last ultimate, 0 = none yet
    public double Hp, MaxHp;
    public Dictionary<int, double> Base;   // its battle stats as it entered (m_attr by SoldierAttribute id; -1 iPower), null = not read
  }

  /// <summary>One command of the simulation's record (FrameIdxInfo).</summary>
  public sealed class FrameCmd {
    public uint Frame; public int Cmd; public uint CUid; public int[] Params = new int[0];
    public string Key() {
      var sb = new StringBuilder();
      sb.Append(Frame).Append(':').Append(Cmd).Append(':').Append(CUid);
      foreach (var p in Params) sb.Append(':').Append(p);
      return sb.ToString();
    }
  }

  /// <summary>One wave controller of the arena at a sample (WaveControllerNode + TDStateData). -1 = not read.</summary>
  public sealed class ArenaSide {
    public uint Ctl;                 // 100 = side 1's waves, 101 = side 2's
    public int Wave = -1;            // iCurWaveCount
    public int MaxWave = -1;         // iMaxWaveCount
    public int Active = -1;          // m_NodeData.iActiveNode (wave count nodes still spawning)
    public int State = -1;           // EWaveNodeStatus 0 none, 1 running, 2 finish
    public int Monsters = -1;        // TDStateData.vMonsterCount[ctl]
    public int Ignore = -1;          // TDStateData.vMonsterAdvanceIgnoreCount[ctl]
  }
  /// <summary>One base (gate) unit of the arena at a sample.</summary>
  public sealed class ArenaBase { public int Unit; public uint Owner; public double Hp, MaxHp; }
  public sealed class ArenaSample {
    public List<ArenaSide> Sides = new List<ArenaSide>();
    public List<ArenaBase> Bases = new List<ArenaBase>();
  }

  /// <summary>One hero's battle statistics with its controller (StatisticsComponent.m_vDamageStatistics[cUid][unit]).</summary>
  public sealed class StatLine {
    public uint C; public int Unit; public uint Hero, Power; public ulong At;
    public long Damage, ToBoss, Overflow; public int Heal, Taken;
  }

  public sealed class TimelineBuilder {
    public const int CmdPutTower = 1000, CmdRetreatTower = 1001, CmdPowerSkill = 2000;
    /// <summary>A sampled event this many frames (~3 s) after a command's frame is that command.</summary>
    public const int MatchFrames = 45;
    public const double FrameSec = 270.0 / 4096.0;
    /// <summary>The judge's rates (‰ of the base's max HP a hit), all rounds (work/sim/ARENA.md §5).</summary>
    static readonly int[] JudgeRates = { 20, 35, 40, 55, 60, 75, 80, 100, 120, 160, 200 };

    sealed class Hero {
      public uint Uid, C; public int Unit, Squad; public bool Leader;
      public bool On; public uint Tower; public int PX = -1, PY = -1; public uint LastUlt;
      public HeroSample Prev;
      public Dictionary<int, double> Base;
      public List<int[]> Placed = new List<int[]>();   // [frame, x, y, face, exact]; sampled tiles as on the map
      public List<uint> Fell = new List<uint>(), Retreat = new List<uint>(), UltExact = new List<uint>(), UltGuess = new List<uint>();
    }

    readonly Dictionary<ulong, Hero> heroes = new Dictionary<ulong, Hero>();
    readonly List<ulong> order = new List<ulong>();
    readonly Dictionary<string, FrameCmd> cmds = new Dictionary<string, FrameCmd>();
    readonly List<int[]> boss = new List<int[]>();
    int lastBoss = -1;
    List<StatLine> stats;
    public uint Frames;
    public int Samples;
    /// <summary>The map's tower grid width (arena): the tiles of the non-player sides are mirrored with it. 0 = none.</summary>
    public int GridW;
    public int Commands { get { return cmds.Count; } }

    static ulong HeroKey(uint c, uint uid) { return ((ulong)c << 32) | uid; }
    static bool IsRetreat(int deadType) { return deadType == 2 || deadType == 6; }

    /// <summary>One sample of all heroes at <paramref name="frame"/>.</summary>
    public void AddSample(uint frame, IList<HeroSample> list) {
      Samples++;
      if (frame > Frames) Frames = frame;
      foreach (var s in list) {
        Hero h;
        ulong key = HeroKey(s.C, s.Uid);
        if (!heroes.TryGetValue(key, out h)) { h = new Hero { Uid = s.Uid, C = s.C }; heroes[key] = h; order.Add(key); }
        if (s.Unit != 0) h.Unit = s.Unit;
        if (s.Squad != 0) h.Squad = s.Squad;
        if (s.Leader) h.Leader = true;
        if (h.Base == null && s.Base != null) h.Base = s.Base;
        bool alive = s.OnField && !s.Dead && s.Tower != 0;
        // left the field (or a new tower since the last sample: it went down in between)
        if (h.On && (!alive || s.Tower != h.Tower)) {
          bool retreat;
          if (s.OnField && s.Dead && s.Tower == h.Tower) retreat = IsRetreat(s.DeadType);
          else if (h.Prev != null && h.Prev.Dead && h.Prev.Tower == h.Tower) retreat = IsRetreat(h.Prev.DeadType);
          else retreat = s.CardState != 2;   // a death puts the card into its reborn countdown
          (retreat ? h.Retreat : h.Fell).Add(frame);
          h.On = false;
        }
        if (!h.On && alive) {
          h.Placed.Add(new[] { (int)frame, s.X, s.Y, s.Face, 0 });
          h.On = true; h.Tower = s.Tower; h.PX = s.X; h.PY = s.Y;
        } else if (h.On && alive && s.X >= 0) {
          if (h.PX < 0) {   // the tile became known after the placement was seen
            var last = h.Placed[h.Placed.Count - 1]; last[1] = s.X; last[2] = s.Y;
          } else if (s.X != h.PX || s.Y != h.PY) {
            h.Placed.Add(new[] { (int)frame, s.X, s.Y, s.Face, 0 });
          }
          h.PX = s.X; h.PY = s.Y;
        }
        // the ultimate: its exact frame when the skill keeps it, else rage dropping from full
        if (s.UltFrame != 0 && s.UltFrame != h.LastUlt) {
          if (s.UltFrame <= frame + 1) h.UltExact.Add(s.UltFrame);
          h.LastUlt = s.UltFrame;
        }
        var p = h.Prev;
        if (p != null && alive && p.OnField && !p.Dead && p.Tower == s.Tower) {
          bool drop = (p.AngerState == 0 && s.AngerState == 1)
                   || (p.MaxAnger > 0 && p.Anger >= p.MaxAnger * 0.95 && s.Anger <= p.Anger * 0.5);
          if (drop) h.UltGuess.Add(frame);
        }
        h.Prev = s;
      }
    }

    /// <summary>The boss's HP in per mille (kept when it moved by 1 % or more).</summary>
    public void AddBoss(uint frame, int permille) {
      if (permille < 0) return;
      if (lastBoss >= 0 && Math.Abs(permille - lastBoss) < 10) return;
      boss.Add(new[] { (int)frame, permille }); lastBoss = permille;
    }

    /// <summary>Commands of the simulation's record (read again and again: the union is kept).</summary>
    public void AddCommands(IEnumerable<FrameCmd> list) {
      foreach (var c in list) { if (c == null || c.Params == null) continue; cmds[c.Key()] = c; }
    }

    /// <summary>The heroes' statistics by controller (read again and again: the last reading is kept).</summary>
    public void SetStats(List<StatLine> list) { if (list != null && list.Count > 0) stats = list; }

    // ------------------------------------------------------------------ arena

    sealed class SideTrack { public int Wave = int.MinValue; public bool Seen, Cleared; public string Last; }
    sealed class Round { public int Wave; public Dictionary<int, int> Start = new Dictionary<int, int>(), First = new Dictionary<int, int>(), Clear = new Dictionary<int, int>(); }
    sealed class BaseTrack { public int Unit; public uint Owner; public double Max; public List<long[]> Hp = new List<long[]>(); public long Last = long.MinValue; }
    readonly Dictionary<uint, SideTrack> sideTracks = new Dictionary<uint, SideTrack>();
    readonly SortedDictionary<int, Round> rounds = new SortedDictionary<int, Round>();
    readonly Dictionary<int, BaseTrack> bases = new Dictionary<int, BaseTrack>();
    readonly List<int[]> arenaRaw = new List<int[]>();
    public bool HasArena { get { return arenaRaw.Count > 0 || bases.Count > 0; } }

    /// <summary>The side of a wave controller: 100 -> 1, 101 -> 2 (others as they are).</summary>
    public static int SideOfCtl(uint ctl) { return ctl >= 100 && ctl < 110 ? (int)ctl - 99 : (int)ctl; }
    /// <summary>The side of a base: its owner when that is a player (1, 2) or a wave controller (100, 101), else by its
    /// unit (4303 / 4305 / 4301 side 1, 4304 / 4306 / 4302 side 2: the odd ones are player 1's, ARENA.md §6).</summary>
    public static int SideOfBase(int unit, uint owner) {
      if (owner == 1 || owner == 2) return (int)owner;
      if (owner == 100 || owner == 101) return (int)owner - 99;
      return unit % 2 == 1 ? 1 : 2;
    }

    /// <summary>One sample of the arena's waves and bases at <paramref name="frame"/>.</summary>
    public void AddArena(uint frame, ArenaSample a) {
      if (a == null) return;
      if (frame > Frames) Frames = frame;
      foreach (var s in a.Sides) {
        SideTrack t;
        if (!sideTracks.TryGetValue(s.Ctl, out t)) { t = new SideTrack(); sideTracks[s.Ctl] = t; }
        string k = s.Wave + "|" + s.Active + "|" + s.Monsters + "|" + s.Ignore + "|" + s.State;
        if (k != t.Last) { arenaRaw.Add(new[] { (int)frame, (int)s.Ctl, s.Wave, s.Active, s.Monsters, s.Ignore, s.State }); t.Last = k; }
        if (s.Wave < 0) continue;
        int side = SideOfCtl(s.Ctl);
        Round r;
        if (!rounds.TryGetValue(s.Wave, out r)) { r = new Round { Wave = s.Wave }; rounds[s.Wave] = r; }
        if (s.Wave != t.Wave) {
          t.Wave = s.Wave; t.Seen = false; t.Cleared = false;
          if (!r.Start.ContainsKey(side)) r.Start[side] = (int)frame;
        }
        int ign = Math.Max(0, s.Ignore);
        if (s.Monsters > ign) {
          t.Seen = true;
          if (!r.First.ContainsKey(side)) r.First[side] = (int)frame;
        }
        // cleared: as WaveControllerNode.DoAdvanceCellNode tests it (nothing left to spawn, no counted monster alive) —
        // after the wave's monsters were seen (a wave that has not spawned yet is not cleared)
        if (!t.Cleared && t.Seen && s.Monsters >= 0 && s.Monsters <= ign && s.Active < 1) {
          t.Cleared = true;
          if (!r.Clear.ContainsKey(side)) r.Clear[side] = (int)frame;
        }
      }
      foreach (var b in a.Bases) {
        BaseTrack bt;
        if (!bases.TryGetValue(b.Unit, out bt)) { bt = new BaseTrack { Unit = b.Unit, Owner = b.Owner }; bases[b.Unit] = bt; }
        if (b.MaxHp > bt.Max) bt.Max = b.MaxHp;
        long hp = (long)Math.Round(b.Hp);
        if (hp != bt.Last) { bt.Hp.Add(new[] { (long)frame, hp }); bt.Last = hp; }
      }
    }

    static int Get(Dictionary<int, int> d, int k) { int v; return d.TryGetValue(k, out v) ? v : -1; }

    static void Sides(StringBuilder sb, string name, Dictionary<int, int> d) {
      sb.Append(",\"").Append(name).Append("\":{");
      bool f = true;
      var keys = new List<int>(d.Keys); keys.Sort();
      foreach (var k in keys) { if (!f) sb.Append(','); f = false; sb.Append('"').Append(k).Append("\":").Append(d[k]); }
      sb.Append('}');
    }

    /// <summary>The winner of a round: the side that cleared first (0: a tie or nobody).</summary>
    static int Won(Round r) {
      int c1 = Get(r.Clear, 1), c2 = Get(r.Clear, 2);
      if (c1 >= 0 && (c2 < 0 || c1 < c2)) return 1;
      if (c2 >= 0 && (c1 < 0 || c2 < c1)) return 2;
      return 0;
    }

    void ArenaJson(StringBuilder sb) {
      sb.Append(",\"arena\":{\"rounds\":[");
      var list = new List<Round>(rounds.Values);
      bool first = true;
      for (int i = 0; i < list.Count; i++) {
        var r = list[i];
        if (!first) sb.Append(','); first = false;
        int won = Won(r);
        sb.Append("{\"wave\":").Append(r.Wave);
        Sides(sb, "start", r.Start); Sides(sb, "first", r.First); Sides(sb, "clear", r.Clear);
        sb.Append(",\"won\":").Append(won);
        // the judge: on the slower side's base from the winner's clear to its own clear (or the next round, or the end)
        if (won > 0) {
          int lose = 3 - won, from = Get(r.Clear, won), to = Get(r.Clear, lose);
          if (to < 0) { to = (int)Frames; for (int j = i + 1; j < list.Count; j++) { int s = Get(list[j].Start, lose); if (s >= 0) { to = s; break; } } }
          BaseTrack bt = null;
          foreach (var b in bases.Values) if (SideOfBase(b.Unit, b.Owner) == lose) bt = b;
          if (bt != null && bt.Max > 0) {
            var hits = new List<long[]>(); long lost = 0, prev = long.MinValue;
            foreach (var p in bt.Hp) {
              if (p[0] <= from) { prev = p[1]; continue; }
              if (p[0] > to + 3) break;
              if (prev != long.MinValue && p[1] < prev) {
                long d = prev - p[1]; lost += d;
                double pm = d * 1000.0 / bt.Max;
                foreach (int jr in JudgeRates) if (Math.Abs(pm - jr) <= 2.5) { hits.Add(new[] { p[0], (long)Math.Round(pm) }); break; }
              }
              prev = p[1];
            }
            sb.Append(",\"judge\":{\"").Append(lose).Append("\":[");
            for (int j = 0; j < hits.Count; j++) { if (j > 0) sb.Append(','); sb.Append('[').Append(hits[j][0]).Append(',').Append(hits[j][1]).Append(']'); }
            sb.Append("]},\"judgeLost\":{\"").Append(lose).Append("\":").Append((long)Math.Round(lost * 1000.0 / bt.Max)).Append('}');
          }
        }
        sb.Append('}');
      }
      sb.Append("],\"bases\":[");
      first = true;
      var units = new List<int>(bases.Keys); units.Sort();
      foreach (var u in units) {
        var b = bases[u];
        if (!first) sb.Append(','); first = false;
        sb.Append("{\"unit\":").Append(b.Unit).Append(",\"side\":").Append(SideOfBase(b.Unit, b.Owner)).Append(",\"owner\":").Append(b.Owner)
          .Append(",\"max\":").Append(Math.Round(b.Max).ToString("0", CultureInfo.InvariantCulture)).Append(",\"hp\":[");
        for (int j = 0; j < b.Hp.Count; j++) { if (j > 0) sb.Append(','); sb.Append('[').Append(b.Hp[j][0]).Append(',').Append(b.Hp[j][1]).Append(']'); }
        sb.Append("]}");
      }
      sb.Append("],\"raw\":[");
      for (int j = 0; j < arenaRaw.Count; j++) {
        if (j > 0) sb.Append(',');
        var x = arenaRaw[j];
        sb.Append('[');
        for (int q = 0; q < x.Length; q++) { if (q > 0) sb.Append(','); sb.Append(x[q]); }
        sb.Append(']');
      }
      sb.Append("]}");
    }

    // ------------------------------------------------------------------ JSON

    static int Param(FrameCmd c, int k) { return c.Params.Length > k ? c.Params[k] : -1; }

    static bool Near(uint sampled, uint cmd) { return sampled + 2 >= cmd && sampled <= cmd + MatchFrames; }

    /// <summary>The commands of one kind for one hero: its unit, its controller (any when not known).</summary>
    List<FrameCmd> CmdsOf(int cmd, int unit, uint ctl) {
      var r = new List<FrameCmd>();
      if (unit == 0) return r;
      foreach (var c in cmds.Values) if (c.Cmd == cmd && c.Params.Length > 0 && c.Params[0] == unit && (ctl == 0 || c.CUid == ctl)) r.Add(c);
      r.Sort((a, b) => a.Frame.CompareTo(b.Frame));
      return r;
    }

    static void Frames1(StringBuilder sb, string name, List<uint> l) {
      l.Sort();
      sb.Append(",\"").Append(name).Append("\":[");
      for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(l[i]); }
      sb.Append(']');
    }

    /// <summary>The player's controller: the lowest one seen (0 when none is known).</summary>
    uint Primary() {
      uint p = 0;
      foreach (var h in heroes.Values) if (h.C != 0 && (p == 0 || h.C < p)) p = h.C;
      return p;
    }

    public string ToJson() {
      var sb = new StringBuilder();
      uint primary = Primary();
      sb.Append("{\"v\":2,\"frames\":").Append(Frames).Append(",\"frameSec\":").Append(FrameSec.ToString("R", CultureInfo.InvariantCulture))
        .Append(",\"samples\":").Append(Samples);
      if (GridW > 0) sb.Append(",\"gridW\":").Append(GridW);
      sb.Append(",\"heroes\":{");
      bool first = true;
      foreach (var key in order) {
        var h = heroes[key];
        bool mine = h.C == 0 || h.C == primary;
        // placements: every command, and the sampled ones no command explains (a sampled tile of another side mirrored to
        // that side's own tiles, as its commands give them)
        var put = CmdsOf(CmdPutTower, h.Unit, h.C);
        var placed = new List<int[]>();
        var used = new bool[h.Placed.Count];
        foreach (var c in put) {
          for (int i = 0; i < h.Placed.Count; i++) if (!used[i] && Near((uint)h.Placed[i][0], c.Frame)) { used[i] = true; break; }
          placed.Add(new[] { (int)c.Frame, Param(c, 1), Param(c, 2), Param(c, 3), 1 });
        }
        for (int i = 0; i < h.Placed.Count; i++) {
          if (used[i]) continue;
          var p = (int[])h.Placed[i].Clone();
          if (!mine && GridW > 0) {
            if (p[1] >= 0) p[1] = GridW - 1 - p[1];
            if (p[3] == 0) p[3] = 180; else if (p[3] == 180) p[3] = 0;
          }
          placed.Add(p);
        }
        placed.Sort((a, b) => a[0].CompareTo(b[0]));
        // taken back: every command; a sampled "fell" right after one is that retreat
        var fell = new List<uint>(); var retreat = new List<uint>();
        var ret = CmdsOf(CmdRetreatTower, h.Unit, h.C);
        foreach (var c in ret) retreat.Add(c.Frame);
        foreach (var f in h.Fell) { bool r = false; foreach (var c in ret) if (Near(f, c.Frame)) { r = true; break; } if (!r) fell.Add(f); }
        foreach (var f in h.Retreat) { bool r = false; foreach (var c in ret) if (Near(f, c.Frame)) { r = true; break; } if (!r) retreat.Add(f); }
        // ultimates: the skill's own frames, then commands and rage drops not near one already kept
        var ult = new List<uint>(h.UltExact);
        foreach (var c in CmdsOf(CmdPowerSkill, h.Unit, h.C)) {
          bool near = false; foreach (var u in ult) if (u + MatchFrames >= c.Frame && u <= c.Frame + MatchFrames) { near = true; break; }
          if (!near) ult.Add(c.Frame);
        }
        foreach (var g in h.UltGuess) {
          bool near = false; foreach (var u in ult) if (u + MatchFrames >= g && u <= g + MatchFrames) { near = true; break; }
          if (!near) ult.Add(g);
        }
        if (!first) sb.Append(','); first = false;
        sb.Append('"');
        if (!mine) sb.Append(h.C).Append(':');
        sb.Append(h.Uid).Append("\":{\"c\":").Append(h.C).Append(",\"uid\":").Append(h.Uid).Append(",\"unit\":").Append(h.Unit).Append(",\"squad\":").Append(h.Squad)
          .Append(",\"leader\":").Append(h.Leader ? "true" : "false").Append(",\"placed\":[");
        for (int i = 0; i < placed.Count; i++) {
          if (i > 0) sb.Append(',');
          var a = placed[i];
          sb.Append('[').Append(a[0]).Append(',').Append(a[1]).Append(',').Append(a[2]).Append(',').Append(a[3]).Append(',').Append(a[4]).Append(']');
        }
        sb.Append(']');
        Frames1(sb, "fell", fell);
        if (retreat.Count > 0) Frames1(sb, "retreat", retreat);
        Frames1(sb, "ult", ult);
        if (h.Base != null) {
          sb.Append(",\"base\":{");
          bool f1 = true;
          foreach (var kv in h.Base) { if (!f1) sb.Append(','); f1 = false; sb.Append('"').Append(kv.Key).Append("\":").Append(Math.Round(kv.Value, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
          sb.Append('}');
        }
        sb.Append('}');
      }
      sb.Append('}');
      // the players' commands as recorded (chat emojis and connection states left out)
      var all = new List<FrameCmd>();
      foreach (var c in cmds.Values) if (c.Cmd < 50000) all.Add(c);
      all.Sort((a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) : a.Cmd != b.Cmd ? a.Cmd.CompareTo(b.Cmd) : a.CUid.CompareTo(b.CUid));
      sb.Append(",\"ops\":[");
      for (int i = 0; i < all.Count; i++) {
        if (i > 0) sb.Append(',');
        sb.Append('[').Append(all[i].Frame).Append(',').Append(all[i].Cmd).Append(',').Append(all[i].CUid);
        foreach (var p in all[i].Params) sb.Append(',').Append(p);
        sb.Append(']');
      }
      sb.Append(']');
      if (boss.Count > 0) {
        sb.Append(",\"boss\":[");
        for (int i = 0; i < boss.Count; i++) { if (i > 0) sb.Append(','); sb.Append('[').Append(boss[i][0]).Append(',').Append(boss[i][1]).Append(']'); }
        sb.Append(']');
      }
      if (stats != null) {
        sb.Append(",\"stats\":[");
        for (int i = 0; i < stats.Count; i++) {
          var s = stats[i];
          if (i > 0) sb.Append(',');
          sb.Append("{\"c\":").Append(s.C).Append(",\"unit\":").Append(s.Unit).Append(",\"hero\":").Append(s.Hero).Append(",\"at\":\"").Append(s.At.ToString("X"))
            .Append("\",\"power\":").Append(s.Power).Append(",\"damage\":").Append(s.Damage).Append(",\"toBoss\":").Append(s.ToBoss)
            .Append(",\"heal\":").Append(s.Heal).Append(",\"taken\":").Append(s.Taken).Append(",\"overflow\":").Append(s.Overflow).Append('}');
        }
        sb.Append(']');
      }
      if (HasArena) ArenaJson(sb);
      return sb.Append('}').ToString();
    }

    /// <summary>The controller of each statistics object of a timeline's "stats" (its address, hex) — for marking the
    /// result's statistic lines (the same objects, read by their own scan) with their side.</summary>
    public static Dictionary<string, uint> StatSides(string timelineJson) {
      var res = new Dictionary<string, uint>();
      if (string.IsNullOrEmpty(timelineJson)) return res;
      var m = System.Text.RegularExpressions.Regex.Matches(timelineJson, "\\{\"c\":(\\d+),\"unit\":-?\\d+,\"hero\":\\d+,\"at\":\"([0-9A-F]+)\"");
      foreach (System.Text.RegularExpressions.Match x in m) res[x.Groups[2].Value] = uint.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
      return res;
    }

    /// <summary>A statistic line ({"at":"&lt;hex&gt;",…}) with "c" (its controller) first when <paramref name="sides"/> knows it.</summary>
    public static string MarkSide(string line, Dictionary<string, uint> sides) {
      const string head = "{\"at\":\"";
      if (sides == null || sides.Count == 0 || line == null || !line.StartsWith(head, StringComparison.Ordinal)) return line;
      int end = line.IndexOf('"', head.Length); if (end < 0) return line;
      uint c;
      if (!sides.TryGetValue(line.Substring(head.Length, end - head.Length), out c)) return line;
      return "{\"c\":" + c + "," + line.Substring(1);
    }
  }
}
