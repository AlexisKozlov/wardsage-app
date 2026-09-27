// RealmForge — the timeline of the fight going on (read-only): who was placed where and when, who fell, whose ultimate
// went off, sampled from the running simulation, plus the simulation's own record of the player's commands. The pure
// part (events from samples, merging, JSON) is src/TimelineBuilder.cs. Only ReadProcessMemory: nothing is written.
//
// Offsets (this game build, work/il2full/dump.cs; TypeInfo RVAs from script.json). IL2CPP objects: klass 0x0.
//   CSharpBattle.Battle.GameSimulation: _EntityWorld 0x28, _FrameDataManager 0x38, _BattleData 0x48,
//     _BattlePlayerManager 0x50, m_state 0xA4, <CurrentFrameIdx> 0xC4 (see src/BattleClock.cs).
//   EntityWorld: _idEntities 0x20 (Dictionary<uint, IEntity>), _lTypeComponents 0x28 (Dictionary<int,
//     DLRecordList<uint, IComponent>>, key = EComponentType), <SingleEntity> 0x58 (the world's single components).
//   Entity: <m_uID> 0x10, _componentQuerys 0x20 (Dictionary<int, DLNode<int, IComponent>>, key = EComponentType).
//   DLList/DLRecordList: _FirstNode 0x10; DLNode: iIndex 0x10, _data 0x18, _prev 0x20, _next 0x28.
//   AbstractComponent: IsEnable 0x10, _entity 0x18. EComponentType: Dead 2, Attribute 5, Skill 8, Position 19, Anger 35,
//     Card 42, OwnerRelation 62, Map 69 (the numbers the game's own GetComponent calls pass, gh/fe decompiles).
//   CardComponent (one per hero of the deck): iUid 0x20 (= hero uid), eState 0x24 (ECardStatus 0 running, 1 frozen,
//     2 reborn, 3 delete), iCurRebornTime 0x28, iMaxRebornTime 0x2C, ePlaceFace 0x3C, iTowerUid 0x4C (the hero's entity
//     on the field), eCardType 0x50 (0 hero, 1 soldier).
//   DeadComponent: bTrigger 0x20, m_deadType 0x24 (EDeadType 0 killed, 2 retreat, 6 forced retreat).
//   AngerComponent (rage): curState 0x20 (EAngerStatus 0 up, 1 down), fAnger 0x30, fMaxAnger 0x38 (Fix64: raw / 4096).
//   SkillComponent: bNextUseSuperManualSkill 0xB0, iUseSuperManualSkillFrameIdx 0xB4 (frame of the last ultimate).
//   AttributeComponent: m_tableData 0x20 (CData_Unit_Element: m_ID 0x14), m_battleAttr 0x28 (BattleAttributeData:
//     <fHp> 0x238, m_fMaxHp 0x240, Fix64).
//   PositionComponent: m_faceType 0x30 (FaceType 0 right, 90 down, 180 left, 270 up), m_position 0x48 (FixVector3).
//   MapComponent: iMapSize 0x20, cMapMarker 0x40 -> MapMarker: m_iPosMapSize 0x18, m_vUidTowerExist 0x20 (uint[,]:
//     the tower entity on each tile, [x, y] as MapMarker.GetTowerIdInGrid(x, y) — confirm with build/t/TimelineDiag).
//   BattlePlayerDataManager: _vPlayers 0x20 (Dictionary<uint, BattlePlayerInfo>); BattlePlayerInfo: <cUid> 0x10,
//     m_lHeros 0x28 (List<BattleHeroInfo>); BattleHeroInfo: iUid 0x220, iUnitID 0x224, iSquadID 0x228,
//     iLordPosition 0x22C (1 = the squad's lord), iLordFaction 0x230.
//   FrameDataManager: _FrameRecord 0x30 -> FrameDataRecord: _vFrames 0x10, _vSuccessFrames 0x18 (Dictionary<uint frame,
//     List<FrameIdxInfo>>); FrameIdxInfo: Idx 0x10, Cmd 0x14 (ushort), iUid 0x18 (the controller), Params 0x20 (int[]).
//     SYNC_PUT_TOWER 1000 [unitId, x, y, face], SYNC_USE_POWERSKILL 2000 [unitId, x, y] (gh/fe decompiles).
//   .NET collections: Dictionary entries 0x18, count 0x20; Entry<int|uint, ref> = {hashCode, next, key, value@8}, 0x18
//     bytes; List<T> _items 0x10, _size 0x18; arrays: max_length 0x18, data 0x20; T[,] bounds 0x10 ({length, lower} x 2).
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace RealmForge {
  public static partial class RFX {
    const int CT_Dead = 2, CT_Attribute = 5, CT_Skill = 8, CT_Position = 19, CT_Anger = 35, CT_Card = 42, CT_Owner = 62, CT_Map = 69;
    // TypeInfo RVAs: CardComponent, AngerComponent, SkillComponent, FrameIdxInfo, BattleHeroInfo
    const ulong RvaCard = 0, RvaAnger = 0, RvaSkill = 0, RvaFrameIdxInfo = 0, RvaHeroInfo = 0;

    static ulong P64(ulong a) { var b = a != 0 ? Read(a, 8) : null; return b != null ? BitConverter.ToUInt64(b, 0) : 0; }
    static double Fix(byte[] b, int o) { return BitConverter.ToInt64(b, o) / 4096.0; }

    /// <summary>The klass of a TypeInfo RVA (0 when GameAssembly's base is not known).</summary>
    static ulong Klass(ulong rva) { return simGa != 0 ? P64(simGa + rva) : 0; }
    static bool IsA(ulong obj, ulong klass) { return klass == 0 || (obj != 0 && P64(obj) == klass); }

    /// <summary>A Dictionary&lt;int|uint, reference&gt;'s entries (key, value).</summary>
    static List<KeyValuePair<uint, ulong>> DictRefs(ulong dict, int max) {
      var res = new List<KeyValuePair<uint, ulong>>();
      var h = dict != 0 ? Read(dict, 0x28) : null; if (h == null) return res;
      ulong ent = BitConverter.ToUInt64(h, 0x18); int count = BitConverter.ToInt32(h, 0x20);
      if (ent == 0 || count <= 0 || count > max) return res;
      var e = Read(ent + 0x18, 8 + count * 0x18); if (e == null) return res;
      if (BitConverter.ToInt64(e, 0) < count) return res;
      for (int i = 0; i < count; i++) {
        int o = 8 + i * 0x18;
        if (BitConverter.ToInt32(e, o) < 0) continue;   // a free entry
        res.Add(new KeyValuePair<uint, ulong>(BitConverter.ToUInt32(e, o + 8), BitConverter.ToUInt64(e, o + 0x10)));
      }
      return res;
    }

    /// <summary>An entity's component of one type (0 = none).</summary>
    static ulong Comp(List<KeyValuePair<uint, ulong>> nodes, int type) {
      foreach (var kv in nodes) if (kv.Key == (uint)type) return P64(kv.Value + 0x18);
      return 0;
    }
    static List<KeyValuePair<uint, ulong>> CompNodes(ulong entity) { return DictRefs(P64(entity + 0x20), 256); }

    /// <summary>The components of one type in the world (walks its DLRecordList).</summary>
    static List<ulong> WorldComps(ulong world, int type, int max) {
      var res = new List<ulong>();
      ulong list = 0;
      foreach (var kv in DictRefs(P64(world + 0x28), 512)) if (kv.Key == (uint)type) { list = kv.Value; break; }
      if (list == 0) return res;
      var seen = new HashSet<ulong>();
      for (ulong n = P64(list + 0x10); n != 0 && res.Count < max && seen.Add(n); ) {
        var b = Read(n, 0x30); if (b == null) break;
        ulong d = BitConverter.ToUInt64(b, 0x18); if (d != 0) res.Add(d);
        n = BitConverter.ToUInt64(b, 0x28);
      }
      return res;
    }

    /// <summary>The heroes of the fight from its players: uid -> {unit id, squad, lord position, controller}.</summary>
    public static Dictionary<uint, int[]> BattleHeroes(ulong sim) {
      var res = new Dictionary<uint, int[]>();
      ulong mgr = P64(sim + 0x50); if (mgr == 0) return res;
      ulong kh = Klass(RvaHeroInfo);
      foreach (var pl in DictRefs(P64(mgr + 0x20), 16)) {
        var pb = Read(pl.Value, 0x30); if (pb == null) continue;
        uint cuid = BitConverter.ToUInt32(pb, 0x10);
        var lb = Read(BitConverter.ToUInt64(pb, 0x28), 0x20); if (lb == null) continue;
        int n = BitConverter.ToInt32(lb, 0x18); if (n <= 0 || n > 64) continue;
        var items = Read(BitConverter.ToUInt64(lb, 0x10) + 0x20, n * 8); if (items == null) continue;
        for (int i = 0; i < n; i++) {
          ulong hi = BitConverter.ToUInt64(items, i * 8);
          if (!IsA(hi, kh)) continue;
          var hb = Read(hi + 0x220, 0x14); if (hb == null) continue;
          uint uid = BitConverter.ToUInt32(hb, 0);
          if (uid != 0) res[uid] = new[] { BitConverter.ToInt32(hb, 4), (int)BitConverter.ToUInt32(hb, 8), (int)BitConverter.ToUInt32(hb, 12), (int)cuid };
        }
      }
      return res;
    }

    /// <summary>The towers on the map's tiles: tower entity id -> {x, y} (MapMarker.m_vUidTowerExist).</summary>
    static Dictionary<uint, int[]> TowerTiles(ulong world, StringBuilder diag) {
      var res = new Dictionary<uint, int[]>();
      ulong single = P64(world + 0x58); if (single == 0) return res;
      ulong map = Comp(CompNodes(single), CT_Map); if (map == 0) return res;
      ulong marker = P64(map + 0x40); if (marker == 0) return res;
      var mb = Read(marker, 0x28); if (mb == null) return res;
      ulong arr = BitConverter.ToUInt64(mb, 0x20); if (arr == 0) return res;
      var ah = Read(arr, 0x20); if (ah == null) return res;
      var bb = Read(BitConverter.ToUInt64(ah, 0x10), 0x20); if (bb == null) return res;
      long d0 = BitConverter.ToInt64(bb, 0), d1 = BitConverter.ToInt64(bb, 0x10);
      if (diag != null) {
        var ms = Read(map + 0x20, 8);
        diag.Append("map size ").Append(ms != null ? BitConverter.ToInt32(ms, 0) + "x" + BitConverter.ToInt32(ms, 4) : "?")
            .Append(", marker pos size ").Append(BitConverter.ToInt32(mb, 0x18)).Append('x').Append(BitConverter.ToInt32(mb, 0x1C))
            .Append(", tower grid ").Append(d0).Append('x').Append(d1).Append('\n');
      }
      if (d0 <= 0 || d1 <= 0 || d0 > 256 || d1 > 256) return res;
      var data = Read(arr + 0x20, (int)(d0 * d1 * 4)); if (data == null) return res;
      for (int i = 0; i < d0; i++)
        for (int j = 0; j < d1; j++) {
          uint t = BitConverter.ToUInt32(data, (int)((i * d1 + j) * 4));
          if (t != 0 && !res.ContainsKey(t)) res[t] = new[] { i, j };
        }
      return res;
    }

    /// <summary>One sample of the fight: every hero card with its tower on the field. <paramref name="heroes"/> from
    /// <see cref="BattleHeroes"/>; <paramref name="diag"/> (optional) gets the raw details. Null when the simulation's
    /// world cannot be read.</summary>
    public static List<HeroSample> ReadHeroSamples(ulong sim, Dictionary<uint, int[]> heroes, StringBuilder diag) {
      ulong world = P64(sim + 0x28); if (world == 0) return null;
      var res = new List<HeroSample>();
      var cards = WorldComps(world, CT_Card, 128);
      ulong kc = Klass(RvaCard), ka = Klass(RvaAnger), ks = Klass(RvaSkill);
      Dictionary<uint, ulong> ents = null; Dictionary<uint, int[]> tiles = null;
      if (diag != null) diag.Append("cards ").Append(cards.Count).Append(", card klass ").Append(kc.ToString("X")).Append('\n');
      foreach (var card in cards) {
        var cb = Read(card, 0x58); if (cb == null) continue;
        if (kc != 0 && BitConverter.ToUInt64(cb, 0) != kc) { if (diag != null) diag.Append("  not a card: ").Append(card.ToString("X")).Append('\n'); continue; }
        if (BitConverter.ToInt32(cb, 0x50) != 0) continue;   // a soldier card
        var s = new HeroSample {
          Uid = BitConverter.ToUInt32(cb, 0x20), CardState = BitConverter.ToInt32(cb, 0x24), RebornLeft = BitConverter.ToInt32(cb, 0x28),
          Tower = BitConverter.ToUInt32(cb, 0x4C), Face = BitConverter.ToInt32(cb, 0x3C)
        };
        int[] hi;
        if (heroes != null && heroes.TryGetValue(s.Uid, out hi)) { s.Unit = hi[0]; s.Squad = hi[1]; s.Leader = hi[2] == 1; }
        if (s.Tower != 0) {
          if (ents == null) {
            ents = new Dictionary<uint, ulong>();
            foreach (var kv in DictRefs(P64(world + 0x20), 100000)) ents[kv.Key] = kv.Value;
          }
          ulong te;
          if (ents.TryGetValue(s.Tower, out te) && te != 0) {
            s.OnField = true;
            var nodes = CompNodes(te);
            ulong dead = Comp(nodes, CT_Dead);
            if (dead != 0) { var db = Read(dead + 0x20, 8); s.Dead = true; s.DeadType = db != null ? BitConverter.ToInt32(db, 4) : -1; }
            ulong ang = Comp(nodes, CT_Anger);
            var ab = ang != 0 && IsA(ang, ka) ? Read(ang, 0x40) : null;
            if (ab != null) { s.AngerState = BitConverter.ToInt32(ab, 0x20); s.Anger = Fix(ab, 0x30); s.MaxAnger = Fix(ab, 0x38); }
            ulong sk = Comp(nodes, CT_Skill);
            var sb = sk != 0 && IsA(sk, ks) ? Read(sk + 0xB0, 8) : null;
            if (sb != null) s.UltFrame = BitConverter.ToUInt32(sb, 4);
            ulong at = Comp(nodes, CT_Attribute);
            ulong ba = at != 0 ? P64(at + 0x28) : 0;
            var hb = ba != 0 ? Read(ba + 0x238, 16) : null;
            if (hb != null) { s.Hp = Fix(hb, 0); s.MaxHp = Fix(hb, 8); }
            ulong pos = Comp(nodes, CT_Position);
            var pb = pos != 0 ? Read(pos + 0x30, 4) : null;
            if (pb != null) s.Face = BitConverter.ToInt32(pb, 0);
            if (tiles == null) tiles = TowerTiles(world, diag);
            int[] xy;
            if (tiles.TryGetValue(s.Tower, out xy)) { s.X = xy[0]; s.Y = xy[1]; }
            if (diag != null && pos != 0) {
              var wp = Read(pos + 0x48, 0x18);
              if (wp != null) diag.Append("  tower ").Append(s.Tower).Append(" world ").Append(Fix(wp, 0).ToString("0.00")).Append(',')
                                  .Append(Fix(wp, 8).ToString("0.00")).Append(',').Append(Fix(wp, 0x10).ToString("0.00")).Append('\n');
            }
          }
        }
        res.Add(s);
      }
      return res;
    }

    /// <summary>The player's commands the simulation recorded (only the executed ones unless <paramref name="all"/>).</summary>
    public static List<FrameCmd> ReadFrameCommands(ulong sim, bool all) {
      var res = new List<FrameCmd>();
      ulong rec = P64(P64(sim + 0x38) + 0x30); if (rec == 0) return res;
      ulong kf = Klass(RvaFrameIdxInfo);
      foreach (var kv in DictRefs(P64(rec + (all ? 0x10UL : 0x18UL)), 200000)) {
        var lb = Read(kv.Value, 0x20); if (lb == null) continue;
        int n = BitConverter.ToInt32(lb, 0x18); if (n <= 0 || n > 1000) continue;
        var items = Read(BitConverter.ToUInt64(lb, 0x10) + 0x20, n * 8); if (items == null) continue;
        for (int i = 0; i < n; i++) {
          ulong f = BitConverter.ToUInt64(items, i * 8);
          var fb = f != 0 ? Read(f, 0x28) : null; if (fb == null) continue;
          if (kf != 0 && BitConverter.ToUInt64(fb, 0) != kf) continue;
          var c = new FrameCmd { Frame = BitConverter.ToUInt32(fb, 0x10), Cmd = BitConverter.ToUInt16(fb, 0x14), CUid = BitConverter.ToUInt32(fb, 0x18) };
          ulong pa = BitConverter.ToUInt64(fb, 0x20);
          var ph = pa != 0 ? Read(pa + 0x18, 8) : null;
          long pn = ph != null ? BitConverter.ToInt64(ph, 0) : 0;
          if (pn > 0 && pn <= 32) {
            var pd = Read(pa + 0x20, (int)pn * 4);
            if (pd != null) { c.Params = new int[pn]; for (int k = 0; k < pn; k++) c.Params[k] = BitConverter.ToInt32(pd, k * 4); }
          }
          res.Add(c);
        }
      }
      return res;
    }

    /// <summary>Diagnostics (build/t/TimelineDiag): the simulation's world and command record as the timeline sees them.</summary>
    public static string TimelineProbe(ulong sim) {
      var sb = new StringBuilder();
      ulong world = P64(sim + 0x28), fdm = P64(sim + 0x38), mgr = P64(sim + 0x50), rec = P64(fdm + 0x30);
      sb.Append("GameAssembly ").Append(simGa.ToString("X")).Append(", sim ").Append(sim.ToString("X")).Append(", world ").Append(world.ToString("X"))
        .Append(", frame data ").Append(fdm.ToString("X")).Append(", record ").Append(rec.ToString("X")).Append(", players ").Append(mgr.ToString("X")).Append('\n');
      if (world != 0) {
        sb.Append("entities ").Append(DictRefs(P64(world + 0x20), 100000).Count).Append("; components by type:");
        foreach (var kv in DictRefs(P64(world + 0x28), 512)) {
          int n = 0; var seen = new HashSet<ulong>();
          for (ulong nd = P64(kv.Value + 0x10); nd != 0 && n < 5000 && seen.Add(nd); nd = P64(nd + 0x28)) n++;
          sb.Append(' ').Append(kv.Key).Append('=').Append(n);
        }
        sb.Append('\n');
        ulong single = P64(world + 0x58);
        sb.Append("single entity ").Append(single.ToString("X")).Append(", its types:");
        foreach (var kv in CompNodes(single)) sb.Append(' ').Append(kv.Key);
        sb.Append('\n');
        TowerTiles(world, sb);
      }
      if (rec != 0) {
        sb.Append("record: frames with commands ").Append(DictRefs(P64(rec + 0x10), 200000).Count)
          .Append(", executed ").Append(DictRefs(P64(rec + 0x18), 200000).Count).Append('\n');
      }
      sb.Append("klasses: card ").Append(Klass(RvaCard).ToString("X")).Append(", anger ").Append(Klass(RvaAnger).ToString("X"))
        .Append(", skill ").Append(Klass(RvaSkill).ToString("X")).Append(", command ").Append(Klass(RvaFrameIdxInfo).ToString("X"))
        .Append(", hero info ").Append(Klass(RvaHeroInfo).ToString("X")).Append('\n');
      return sb.ToString();
    }

    /// <summary>The boss's HP in per mille: the unit with the most max HP that is not one of the heroes' towers
    /// (-1 = none found).</summary>
    public static int BossHpPermille(ulong sim, ICollection<uint> towers) {
      ulong world = P64(sim + 0x28); if (world == 0) return -1;
      double best = 0, hp = 0;
      foreach (var at in WorldComps(world, CT_Attribute, 2000)) {
        var ab = Read(at + 0x18, 0x18); if (ab == null) continue;
        ulong ent = BitConverter.ToUInt64(ab, 0), ba = BitConverter.ToUInt64(ab, 0x10);
        if (ba == 0) continue;
        if (towers != null && ent != 0) { var ib = Read(ent + 0x10, 4); if (ib != null && towers.Contains(BitConverter.ToUInt32(ib, 0))) continue; }
        var hb = Read(ba + 0x238, 16); if (hb == null) continue;
        double h = Fix(hb, 0), m = Fix(hb, 8);
        if (m > best && m < 1e13 && h >= 0 && h <= m) { best = m; hp = h; }
      }
      return best > 0 ? (int)Math.Round(hp * 1000 / best) : -1;
    }
  }

  /// <summary>Follows one fight from its start to its end on its own thread (~5 samples a second while the clock moves)
  /// and gives its timeline as JSON. Read-only.</summary>
  public sealed class FightRecorder {
    public readonly ulong Sim; public readonly int Stage; public readonly DateTime StartedUtc = DateTime.UtcNow;
    public int IntervalMs = 200;
    public Action<string> Log;
    public Action<uint, List<HeroSample>> OnSample;   // diagnostics
    readonly TimelineBuilder tl = new TimelineBuilder();
    readonly object gate = new object();
    readonly ManualResetEvent done = new ManualResetEvent(false);
    volatile bool stop, running;
    Dictionary<uint, int[]> heroes;
    Thread th;

    public FightRecorder(ulong sim, int stage) { Sim = sim; Stage = stage; }
    public bool Running { get { return running; } }
    public DateTime EndedUtc { get; private set; }

    public void Start() {
      running = true;
      th = new Thread(Loop) { IsBackground = true, Name = "fight-recorder" };
      th.Start();
    }
    public void Stop() { stop = true; }

    void Say(string s) { var l = Log; if (l != null) try { l(s); } catch (Exception) { } }

    void Commands() {
      var c = RFX.ReadFrameCommands(Sim, false);
      if (c.Count > 0) lock (gate) tl.AddCommands(c);
    }

    void Loop() {
      uint lastFrame = 0; int n = 0, errors = 0;
      try {
        heroes = RFX.BattleHeroes(Sim);
        Say("fight recorder: " + heroes.Count + " heroes, stage " + Stage);
        while (!stop) {
          uint fr; int st;
          if (!RFX.SimClock(Sim, out fr, out st) || st != 1) break;   // ended (2) or gone
          // the clock going back or a fight of hours: the memory is no longer this fight's simulation
          if (fr < lastFrame || (DateTime.UtcNow - StartedUtc).TotalMinutes > 40) break;
          if (fr != lastFrame) {
            lastFrame = fr;
            try {
              if (heroes.Count == 0) heroes = RFX.BattleHeroes(Sim);
              var hs = RFX.ReadHeroSamples(Sim, heroes, null);
              if (hs != null) {
                lock (gate) tl.AddSample(fr, hs);
                var cb = OnSample; if (cb != null) cb(fr, hs);
              }
              if (++n % 5 == 0) {   // about every second: the command record and the boss
                Commands();
                var towers = new List<uint>();
                if (hs != null) foreach (var h in hs) if (h.Tower != 0) towers.Add(h.Tower);
                int bp = RFX.BossHpPermille(Sim, towers);
                lock (gate) tl.AddBoss(fr, bp);
              }
            } catch (Exception e) { if (errors++ < 3) Say("fight recorder: " + e.Message); }
          }
          Thread.Sleep(IntervalMs);
        }
        // the end: the final frame count and the whole record (the simulation stays in memory at the result screen)
        uint ef; int es;
        if (RFX.SimClock(Sim, out ef, out es) && (es == 1 || es == 2)) lock (gate) { if (ef > tl.Frames) tl.Frames = ef; }
        Commands();
        lock (gate) Say("fight recorder: ended at frame " + tl.Frames + ", " + tl.Samples + " samples, " + tl.Commands + " commands");
      } catch (Exception e) { Say("fight recorder: " + e.Message); }
      finally { running = false; EndedUtc = DateTime.UtcNow; done.Set(); }
    }

    /// <summary>The timeline as JSON; waits up to <paramref name="waitMs"/> for the fight to end first (a capture made
    /// during the fight reads the command record at once).</summary>
    public string Json(int waitMs) {
      if (running && waitMs > 0) done.WaitOne(waitMs);
      if (running) try { Commands(); } catch (Exception) { }
      lock (gate) return tl.Samples > 0 || tl.Commands > 0 ? tl.ToJson() : null;
    }
  }
}
