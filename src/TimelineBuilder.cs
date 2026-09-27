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
// A sampled event up to MatchFrames after a command of the same kind for the same hero is that command.
//
// JSON: {"v":1,"frames":N,"frameSec":0.06591796875,"samples":K,
//        "heroes":{"<uid>":{"unit":U,"squad":S,"leader":false,"placed":[[frame,x,y,face,exact]],"fell":[frame],
//                  "retreat":[frame],"ult":[frame]}},
//        "ops":[[frame,cmd,cuid,params…]], "boss":[[frame,hp‰]]}
// exact = 1 for a placement from a command, 0 for a sampled one (tile from the map's tower grid, frame ≤ ~3 late).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealmForge {
  /// <summary>One hero (its card and its tower on the field) at one sample.</summary>
  public sealed class HeroSample {
    public uint Uid;                 // hero uid (CardComponent.iUid = BattleHeroInfo.iUid)
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

  public sealed class TimelineBuilder {
    public const int CmdPutTower = 1000, CmdRetreatTower = 1001, CmdPowerSkill = 2000;
    /// <summary>A sampled event this many frames (~3 s) after a command's frame is that command.</summary>
    public const int MatchFrames = 45;
    public const double FrameSec = 270.0 / 4096.0;

    sealed class Hero {
      public uint Uid; public int Unit, Squad; public bool Leader;
      public bool On; public uint Tower; public int PX = -1, PY = -1; public uint LastUlt;
      public HeroSample Prev;
      public List<int[]> Placed = new List<int[]>();   // [frame, x, y, face, exact]
      public List<uint> Fell = new List<uint>(), Retreat = new List<uint>(), UltExact = new List<uint>(), UltGuess = new List<uint>();
    }

    readonly Dictionary<uint, Hero> heroes = new Dictionary<uint, Hero>();
    readonly List<uint> order = new List<uint>();
    readonly Dictionary<string, FrameCmd> cmds = new Dictionary<string, FrameCmd>();
    readonly List<int[]> boss = new List<int[]>();
    int lastBoss = -1;
    public uint Frames;
    public int Samples;
    public int Commands { get { return cmds.Count; } }

    static bool IsRetreat(int deadType) { return deadType == 2 || deadType == 6; }

    /// <summary>One sample of all heroes at <paramref name="frame"/>.</summary>
    public void AddSample(uint frame, IList<HeroSample> list) {
      Samples++;
      if (frame > Frames) Frames = frame;
      foreach (var s in list) {
        Hero h;
        if (!heroes.TryGetValue(s.Uid, out h)) { h = new Hero { Uid = s.Uid }; heroes[s.Uid] = h; order.Add(s.Uid); }
        if (s.Unit != 0) h.Unit = s.Unit;
        if (s.Squad != 0) h.Squad = s.Squad;
        if (s.Leader) h.Leader = true;
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

    static int Param(FrameCmd c, int k) { return c.Params.Length > k ? c.Params[k] : -1; }

    static bool Near(uint sampled, uint cmd) { return sampled + 2 >= cmd && sampled <= cmd + MatchFrames; }

    List<FrameCmd> CmdsOf(int cmd, int unit) {
      var r = new List<FrameCmd>();
      if (unit == 0) return r;
      foreach (var c in cmds.Values) if (c.Cmd == cmd && c.Params.Length > 0 && c.Params[0] == unit) r.Add(c);
      r.Sort((a, b) => a.Frame.CompareTo(b.Frame));
      return r;
    }

    static void Frames1(StringBuilder sb, string name, List<uint> l) {
      l.Sort();
      sb.Append(",\"").Append(name).Append("\":[");
      for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(l[i]); }
      sb.Append(']');
    }

    public string ToJson() {
      var sb = new StringBuilder();
      sb.Append("{\"v\":1,\"frames\":").Append(Frames).Append(",\"frameSec\":").Append(FrameSec.ToString("R", CultureInfo.InvariantCulture))
        .Append(",\"samples\":").Append(Samples).Append(",\"heroes\":{");
      bool first = true;
      foreach (var uid in order) {
        var h = heroes[uid];
        // placements: every command, and the sampled ones no command explains
        var put = CmdsOf(CmdPutTower, h.Unit);
        var placed = new List<int[]>();
        var used = new bool[h.Placed.Count];
        foreach (var c in put) {
          for (int i = 0; i < h.Placed.Count; i++) if (!used[i] && Near((uint)h.Placed[i][0], c.Frame)) { used[i] = true; break; }
          placed.Add(new[] { (int)c.Frame, Param(c, 1), Param(c, 2), Param(c, 3), 1 });
        }
        for (int i = 0; i < h.Placed.Count; i++) if (!used[i]) placed.Add(h.Placed[i]);
        placed.Sort((a, b) => a[0].CompareTo(b[0]));
        // taken back: every command; a sampled "fell" right after one is that retreat
        var fell = new List<uint>(); var retreat = new List<uint>();
        var ret = CmdsOf(CmdRetreatTower, h.Unit);
        foreach (var c in ret) retreat.Add(c.Frame);
        foreach (var f in h.Fell) { bool r = false; foreach (var c in ret) if (Near(f, c.Frame)) { r = true; break; } if (!r) fell.Add(f); }
        foreach (var f in h.Retreat) { bool r = false; foreach (var c in ret) if (Near(f, c.Frame)) { r = true; break; } if (!r) retreat.Add(f); }
        // ultimates: the skill's own frames, then commands and rage drops not near one already kept
        var ult = new List<uint>(h.UltExact);
        foreach (var c in CmdsOf(CmdPowerSkill, h.Unit)) {
          bool near = false; foreach (var u in ult) if (u + MatchFrames >= c.Frame && u <= c.Frame + MatchFrames) { near = true; break; }
          if (!near) ult.Add(c.Frame);
        }
        foreach (var g in h.UltGuess) {
          bool near = false; foreach (var u in ult) if (u + MatchFrames >= g && u <= g + MatchFrames) { near = true; break; }
          if (!near) ult.Add(g);
        }
        if (!first) sb.Append(','); first = false;
        sb.Append('"').Append(uid).Append("\":{\"unit\":").Append(h.Unit).Append(",\"squad\":").Append(h.Squad)
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
        sb.Append('}');
      }
      sb.Append('}');
      // the player's commands as recorded (chat emojis and connection states left out)
      var all = new List<FrameCmd>();
      foreach (var c in cmds.Values) if (c.Cmd < 50000) all.Add(c);
      all.Sort((a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) : a.Cmd.CompareTo(b.Cmd));
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
      return sb.Append('}').ToString();
    }
  }
}
