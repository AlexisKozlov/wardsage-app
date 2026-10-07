// RealmForge — the timeline of the fight going on (read-only): who was placed where and when, who fell, whose ultimate
// went off, sampled from the running simulation, plus the simulation's own record of the player's commands. The pure
// part (events from samples, merging, JSON) is src/TimelineBuilder.cs. Only ReadProcessMemory: nothing is written.
//
// Offsets of this game build. objects: class pointer at 0x0.
//   CSharpBattle.Battle.GameSimulation: _EntityWorld 0x28, _FrameDataManager 0x38, _BattleData 0x48,
//     _BattlePlayerManager 0x50, m_state 0xA4, <CurrentFrameIdx> 0xC4 (see src/BattleClock.cs).
//   EntityWorld: _idEntities 0x20 (Dictionary<uint, IEntity>), _lTypeComponents 0x28 (Dictionary<int,
//     DLRecordList<uint, IComponent>>, key = EComponentType), <SingleEntity> 0x58 (the world's single components).
//   Entity: <m_uID> 0x10, _componentQuerys 0x20 (Dictionary<int, DLNode<int, IComponent>>, key = EComponentType).
//   DLList/DLRecordList: _FirstNode 0x10; DLNode: iIndex 0x10, _data 0x18, _prev 0x20, _next 0x28.
//   AbstractComponent: IsEnable 0x10, _entity 0x18. EComponentType: Dead 2, Attribute 5, Skill 8, Position 19, Anger 35,
//     Card 42, OwnerRelation 62, Map 69.
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
//     the tower entity on each tile, [x, y] as MapMarker.GetTowerIdInGrid(x, y)).
//   BattlePlayerDataManager: _vPlayers 0x20 (Dictionary<uint, BattlePlayerInfo>); BattlePlayerInfo: <cUid> 0x10,
//     m_lHeros 0x28 (List<BattleHeroInfo>); BattleHeroInfo: iUid 0x220, iUnitID 0x224, iSquadID 0x228,
//     iLordPosition 0x22C (1 = the squad's lord), iLordFaction 0x230.
//   FrameDataManager: _FrameRecord 0x30 -> FrameDataRecord: _vFrames 0x10, _vSuccessFrames 0x18 (Dictionary<uint frame,
//     List<FrameIdxInfo>>); FrameIdxInfo: Idx 0x10, Cmd 0x14 (ushort), iUid 0x18 (the controller), Params 0x20 (int[]).
//     SYNC_PUT_TOWER 1000 [unitId, x, y, face], SYNC_USE_POWERSKILL 2000 [unitId, x, y].
//   OwnerRelationComponent (on every card entity, EntitySpawn.CreateCardEntity: AddComponent<OwnerRelation> then
//     cUid = the card's controller; also on the towers): cUid 0x20, iSquadID 0x24, iLordPosition 0x28.
//   Sides (the arena): heroes of controller 1 (the player) and 2 (the opponent's recorded defence) share one world and
//     often the same uid, so a hero is (controller, uid); the controller of a card = its entity's OwnerRelation cUid.
//   StatisticsComponent (world single, EComponentType Statistics 71): m_vDamageStatistics 0x20 (Dictionary<uint cUid,
//     Dictionary<int unit, DamageStatisticsData>>); DamageStatisticsData: iBaseID 0x10, iHeroID 0x14, iPower 0x18,
//     fDamageAmount 0x28 (long), fDamageAmountToBoss 0x30, fTreatmentAmount 0x38 (int), fAcceptDamageAmount 0x3C,
//     fDamageOverflowAmount 0x58 — the objects the result's statistic lines (src/LiveTables.cs DumpBattleStats) are.
//   Arena waves: WaveInfoComponent (world single, type 67): cWaveSysytem 0x28 -> WaveSystem: m_vActiveController 0x50
//     (Dictionary<uint ctl, WaveControllerNode>; 100 = side 1's waves, 101 = side 2's) -> WaveControllerNode: cUid 0x10,
//     m_NodeData 0x28 (stWaveNodeData: iMinFrame 0, iMaxFrame 4, iRemainCount 8, iMaxCount 0xC, iRemainPopulation 0x10,
//     iMaxPopulation 0x14, iActiveNode 0x18 -> 0x40 in the node), eState 0x44, iTimeDeviation 0x50, iMaxWaveCount 0x5C,
//     iCurWaveCount 0x60. TDStateData (GameSimulation._lIndependentDatas 0x98, a DLList of IIndependentData; found by its
//     class): vMonsterCount 0x58, vMonsterAdvanceIgnoreCount 0x60 (Dictionary<uint, int>; WaveSystem.CanAdvanceUidCellNode
//     reads them so). The bases: the units 4301–4306 / 4345–4350 (AttributeComponent m_tableData m_ID), their HP
//     as the heroes' (BattleAttributeData <fHp> 0x238, m_fMaxHp 0x240), their side from their OwnerRelation cUid.
//     Not verified on a live arena fight yet.
//   .NET collections: Dictionary entries 0x18, count 0x20; Entry<int|uint, ref> = {hashCode, next, key, value@8}, 0x18
//     bytes; Entry<uint, int> = {hashCode, next, key, value}, 0x10 bytes; List<T> _items 0x10, _size 0x18; arrays:
//     max_length 0x18, data 0x20; T[,] bounds 0x10 ({length, lower} x 2).
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace RealmForge {
  public static partial class RFX {
    const int CT_Dead = 2, CT_Attribute = 5, CT_Skill = 8, CT_Position = 19, CT_Anger = 35, CT_Card = 42, CT_Owner = 62, CT_WaveInfo = 67,
              CT_Map = 69, CT_Statistics = 71;
    // class addresses: CardComponent, AngerComponent, SkillComponent, FrameIdxInfo, BattleHeroInfo
    const ulong RvaCard = 0, RvaAnger = 0, RvaSkill = 0, RvaFrameIdxInfo = 0, RvaHeroInfo = 0;
    // OwnerRelationComponent, StatisticsComponent, DamageStatisticsData, TDStateData, WaveSystem, WaveControllerNode, AttributeComponent
    const ulong RvaOwner = 0, RvaStats = 0, RvaDmgStat = 0, RvaTDState = 0, RvaWaveSystem = 0,
                RvaWaveCtl = 0, RvaAttr = 0;

    static ulong P64(ulong a) { var b = a != 0 ? Read(a, 8) : null; return b != null ? BitConverter.ToUInt64(b, 0) : 0; }
    static double Fix(byte[] b, int o) { return BitConverter.ToInt64(b, o) / 4096.0; }

    /// <summary>A sample wants a hero's battle stats (set by the recorder: only until it has them).</summary>
    public static Func<HeroSample, bool> wantBase;
    /// <summary>BattleAttributeData.m_attr (stBattleBaseAttr at +0x10, Fix64 = raw / 4096; percent stats in percent units)
    /// by SoldierAttribute id as BattleAttributeData.GetOriAttr maps them, and its iPower.</summary>
    static readonly int[] BaseMap = {
      1, 0x88, 2, 0x00, 3, 0x08, 4, 0x18, 5, 0x40, 6, 0x38, 8, 0x80, 10, 0x78, 11, 0x28, 12, 0x58, 13, 0x30, 14, 0x90, 15, 0x10,
      16, 0x50, 22, 0xC0, 23, 0xA8, 24, 0xB0, 25, 0xD8, 26, 0xF0, 27, 0xF8, 28, 0x100, 29, 0x108, 30, 0x110, 31, 0x118, 36, 0x148,
      37, 0x98, 38, 0xA0, 42, 0x120, 45, 0xD0, 47, 0xC8, 48, 0xE0, 50, 0x20, 51, 0x48, 52, 0x68, 53, 0x60, 54, 0x70, 55, 0x128,
      70, 0x1E8, 71, 0x1F0, 72, 0x1F8, 73, 0x200 };
    static Dictionary<int, double> BaseAttrs(ulong ba) {
      var b = Read(ba + 0x10, 0x210);
      if (b == null) return null;
      var d = new Dictionary<int, double>();
      for (int i = 0; i < BaseMap.Length; i += 2) d[BaseMap[i]] = Fix(b, BaseMap[i + 1]);
      d[43] = BitConverter.ToInt32(b, 0x144);   // iBlock
      d[-1] = BitConverter.ToUInt32(b, 0x130);  // iPower
      d[-2] = Fix(b, 0xB8);                     // iCritResistance
      d[-3] = Fix(b, 0xE8);                     // iAttackAngerHedging
      // iSkills 0x180 / iSkillLevels 0x188 (int[]): the skills the hero fights with — its own, its gear sets' and its
      // artifact's, the opponent's too (the opponents' list has only their stats): -100-i = skill id, -200-i = its level
      int[] ids = IntArray(BitConverter.ToUInt64(b, 0x180), 40), lvs = IntArray(BitConverter.ToUInt64(b, 0x188), 40);
      for (int i = 0; ids != null && i < ids.Length; i++) {
        d[-100 - i] = ids[i];
        d[-200 - i] = lvs != null && i < lvs.Length ? lvs[i] : 0;
      }
      return d.ContainsKey(2) && d[2] > 0 ? d : null;
    }

    /// <summary>An int[] (max_length 0x18, data 0x20), null when unreadable or longer than max.</summary>
    static int[] IntArray(ulong arr, int max) {
      var h = arr != 0 ? Read(arr + 0x18, 8) : null;
      if (h == null) return null;
      long n = BitConverter.ToInt64(h, 0);
      if (n < 0 || n > max) return null;
      if (n == 0) return new int[0];
      var b = Read(arr + 0x20, (int)n * 4);
      if (b == null) return null;
      var r = new int[n];
      for (int i = 0; i < n; i++) r[i] = BitConverter.ToInt32(b, i * 4);
      return r;
    }

    /// <summary>The class of a class address (0 when GameAssembly's base is not known).</summary>
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

    /// <summary>The heroes of the fight from its players: uid -> {unit id, squad, lord position, controller}; a uid both
    /// sides have (the arena) -> the lowest controller's (the player's).</summary>
    public static Dictionary<uint, int[]> BattleHeroes(ulong sim) {
      var res = new Dictionary<uint, int[]>();
      foreach (var h in BattleHeroList(sim)) {
        int[] was;
        if (res.TryGetValue((uint)h[0], out was) && (uint)was[3] <= (uint)h[4]) continue;
        res[(uint)h[0]] = new[] { h[1], h[2], h[3], h[4] };
      }
      return res;
    }

    /// <summary>The heroes of the fight from its players, each {uid, unit id, squad, lord position, controller}: the
    /// same uid twice when both sides of an arena fight have the hero.</summary>
    public static List<int[]> BattleHeroList(ulong sim) {
      var res = new List<int[]>();
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
          if (uid != 0) res.Add(new[] { (int)uid, BitConverter.ToInt32(hb, 4), (int)BitConverter.ToUInt32(hb, 8), (int)BitConverter.ToUInt32(hb, 12), (int)cuid });
        }
      }
      return res;
    }

    /// <summary>The width (x) of the map's tower grid last read (0 = not yet): the arena mirrors the opponent's tiles with it.</summary>
    public static int TowerGridW;

    /// <summary>An entity's controller (its OwnerRelationComponent cUid; 0 = none).</summary>
    static uint OwnerOf(List<KeyValuePair<uint, ulong>> nodes) {
      ulong o = Comp(nodes, CT_Owner);
      if (o == 0 || !IsA(o, Klass(RvaOwner))) return 0;
      var b = Read(o + 0x20, 4);
      return b != null ? BitConverter.ToUInt32(b, 0) : 0;
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
      TowerGridW = (int)d0;
      var data = Read(arr + 0x20, (int)(d0 * d1 * 4)); if (data == null) return res;
      for (int i = 0; i < d0; i++)
        for (int j = 0; j < d1; j++) {
          uint t = BitConverter.ToUInt32(data, (int)((i * d1 + j) * 4));
          if (t != 0 && !res.ContainsKey(t)) res[t] = new[] { i, j };
        }
      return res;
    }

    /// <summary>One sample of the fight: every hero card with its tower on the field and its controller.
    /// <paramref name="heroes"/> from <see cref="BattleHeroList"/>; <paramref name="owners"/> (optional) keeps each card's
    /// controller between samples; <paramref name="diag"/> (optional) gets the raw details. Null when the simulation's
    /// world cannot be read.</summary>
    public static List<HeroSample> ReadHeroSamples(ulong sim, List<int[]> heroes, StringBuilder diag, Dictionary<ulong, uint> owners) {
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
        // the card's controller: its entity's OwnerRelation (kept: a card keeps its owner)
        uint own;
        if (owners == null || !owners.TryGetValue(card, out own)) {
          own = OwnerOf(CompNodes(BitConverter.ToUInt64(cb, 0x18)));
          if (owners != null && own != 0) owners[card] = own;
        }
        s.C = own;
        int[] hi = HeroOf(heroes, s.Uid, s.C);
        if (hi != null) { s.Unit = hi[1]; s.Squad = hi[2]; s.Leader = hi[3] == 1; if (s.C == 0) s.C = (uint)hi[4]; }
        if (diag != null) diag.Append("  card ").Append(card.ToString("X")).Append(" uid ").Append(s.Uid).Append(" controller ").Append(own).Append('\n');
        if (s.Tower != 0) {
          if (ents == null) {
            ents = new Dictionary<uint, ulong>();
            foreach (var kv in DictRefs(P64(world + 0x20), 100000)) ents[kv.Key] = kv.Value;
          }
          ulong te;
          if (ents.TryGetValue(s.Tower, out te) && te != 0) {
            s.OnField = true;
            var nodes = CompNodes(te);
            if (s.C == 0) s.C = OwnerOf(nodes);   // the tower's controller when the card's is not known
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
            // the battle stats it entered with (m_attr, stBattleBaseAttr): read once, while the builder has none for it
            if (ba != 0 && (wantBase == null || wantBase(s))) s.Base = BaseAttrs(ba);
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

    /// <summary>A hero of <see cref="BattleHeroList"/> by uid and controller; a controller not known (0): the hero when
    /// only one side has it.</summary>
    static int[] HeroOf(List<int[]> heroes, uint uid, uint c) {
      if (heroes == null) return null;
      int[] one = null; int n = 0;
      foreach (var h in heroes) {
        if ((uint)h[0] != uid) continue;
        if (c != 0 && (uint)h[4] == c) return h;
        one = h; n++;
      }
      return c == 0 && n == 1 ? one : null;
    }

    /// <summary>A Dictionary&lt;uint, int&gt;'s entries.</summary>
    static Dictionary<uint, int> DictInts(ulong dict, int max) {
      var res = new Dictionary<uint, int>();
      var h = dict != 0 ? Read(dict, 0x28) : null; if (h == null) return res;
      ulong ent = BitConverter.ToUInt64(h, 0x18); int count = BitConverter.ToInt32(h, 0x20);
      if (ent == 0 || count <= 0 || count > max) return res;
      var e = Read(ent + 0x18, 8 + count * 0x10); if (e == null) return res;
      if (BitConverter.ToInt64(e, 0) < count) return res;
      for (int i = 0; i < count; i++) {
        int o = 8 + i * 0x10;
        if (BitConverter.ToInt32(e, o) < 0) continue;
        res[BitConverter.ToUInt32(e, o + 8)] = BitConverter.ToInt32(e, o + 12);
      }
      return res;
    }

    /// <summary>The heroes' battle statistics by controller (StatisticsComponent.m_vDamageStatistics); empty when not found.</summary>
    public static List<StatLine> ReadStatsBySide(ulong sim) {
      var res = new List<StatLine>();
      ulong world = P64(sim + 0x28); if (world == 0) return res;
      ulong st = Comp(CompNodes(P64(world + 0x58)), CT_Statistics);
      if (st == 0 || !IsA(st, Klass(RvaStats))) return res;
      ulong kd = Klass(RvaDmgStat);
      foreach (var side in DictRefs(P64(st + 0x20), 16))
        foreach (var u in DictRefs(side.Value, 128)) {
          if (!IsA(u.Value, kd)) continue;
          var b = Read(u.Value, 0x68); if (b == null) continue;
          res.Add(new StatLine { C = side.Key, Unit = (int)u.Key, At = u.Value, Hero = BitConverter.ToUInt32(b, 0x14), Power = BitConverter.ToUInt32(b, 0x18),
                                 Damage = BitConverter.ToInt64(b, 0x28), ToBoss = BitConverter.ToInt64(b, 0x30), Heal = BitConverter.ToInt32(b, 0x38),
                                 Taken = BitConverter.ToInt32(b, 0x3C), Overflow = BitConverter.ToInt64(b, 0x58) });
        }
      return res;
    }

    /// <summary>What the arena reader keeps between samples: the TDStateData, the bases' attribute data.</summary>
    public sealed class ArenaRefs {
      public ulong State;
      public readonly List<ulong[]> Bases = new List<ulong[]>();   // {attribute component, battle attributes, unit, owner}
      public int Looks;
    }

    static bool IsBaseUnit(int id) { return (id >= 4301 && id <= 4306) || (id >= 4345 && id <= 4350); }

    /// <summary>One sample of the arena's wave controllers and bases (null: the waves cannot be read).</summary>
    public static ArenaSample ReadArena(ulong sim, ArenaRefs refs, StringBuilder diag) {
      ulong world = P64(sim + 0x28); if (world == 0) return null;
      var a = new ArenaSample();
      // TDStateData: the monsters alive per wave controller
      if (refs.State == 0) {
        ulong kt = Klass(RvaTDState);
        if (kt != 0) {
          ulong list = P64(sim + 0x98); var seen = new HashSet<ulong>();
          for (ulong n = P64(list + 0x10); n != 0 && seen.Count < 64 && seen.Add(n); n = P64(n + 0x28)) {
            ulong d = P64(n + 0x18);
            if (d != 0 && P64(d) == kt) { refs.State = d; break; }
          }
        }
        if (diag != null) diag.Append("TDStateData ").Append(refs.State.ToString("X")).Append('\n');
      }
      Dictionary<uint, int> mon = null, ign = null;
      if (refs.State != 0) { mon = DictInts(P64(refs.State + 0x58), 64); ign = DictInts(P64(refs.State + 0x60), 64); }
      // the wave controllers
      ulong wi = Comp(CompNodes(P64(world + 0x58)), CT_WaveInfo);
      ulong ws = wi != 0 ? P64(wi + 0x28) : 0;
      if (ws == 0 || !IsA(ws, Klass(RvaWaveSystem))) { if (diag != null) diag.Append("no wave system (wave info ").Append(wi.ToString("X")).Append(")\n"); return null; }
      ulong kc = Klass(RvaWaveCtl);
      foreach (var kv in DictRefs(P64(ws + 0x50), 16)) {
        if (!IsA(kv.Value, kc)) continue;
        var b = Read(kv.Value, 0x68); if (b == null) continue;
        var s = new ArenaSide { Ctl = kv.Key, Active = BitConverter.ToInt32(b, 0x40), State = BitConverter.ToInt32(b, 0x44),
                                MaxWave = BitConverter.ToInt32(b, 0x5C), Wave = BitConverter.ToInt32(b, 0x60) };
        int v;
        if (mon != null) s.Monsters = mon.TryGetValue(kv.Key, out v) ? v : 0;
        if (ign != null) s.Ignore = ign.TryGetValue(kv.Key, out v) ? v : 0;
        a.Sides.Add(s);
        if (diag != null) diag.Append("waves ").Append(kv.Key).Append(" (").Append(BitConverter.ToUInt32(b, 0x10)).Append("): wave ").Append(s.Wave).Append('/').Append(s.MaxWave)
                              .Append(" active ").Append(s.Active).Append(" state ").Append(s.State).Append(" remain ").Append(BitConverter.ToInt32(b, 0x30))
                              .Append(" deviation ").Append(BitConverter.ToUInt32(b, 0x50)).Append(" monsters ").Append(s.Monsters).Append(" ignore ").Append(s.Ignore).Append('\n');
      }
      // the bases: looked for (every 5th sample) until two are found, then read directly
      if (refs.Bases.Count < 2 && refs.Looks++ % 5 == 0) {
        ulong ka = Klass(RvaAttr);
        foreach (var at in WorldComps(world, CT_Attribute, 4000)) {
          var ab = Read(at, 0x30); if (ab == null) continue;
          if (ka != 0 && BitConverter.ToUInt64(ab, 0) != ka) continue;
          ulong ent = BitConverter.ToUInt64(ab, 0x18), td = BitConverter.ToUInt64(ab, 0x20), ba = BitConverter.ToUInt64(ab, 0x28);
          var ib = td != 0 ? Read(td + 0x14, 4) : null; if (ib == null || ba == 0) continue;
          int id = BitConverter.ToInt32(ib, 0);
          if (!IsBaseUnit(id)) continue;
          bool have = false; foreach (var x in refs.Bases) if (x[0] == at) have = true;
          if (have) continue;
          refs.Bases.Add(new[] { at, ba, (ulong)id, (ulong)OwnerOf(CompNodes(ent)) });
          if (diag != null) diag.Append("base ").Append(id).Append(" owner ").Append(refs.Bases[refs.Bases.Count - 1][3]).Append(" attr ").Append(at.ToString("X")).Append('\n');
        }
      }
      foreach (var x in refs.Bases) {
        // still the same component with the same attributes (a dead unit's memory can be reused)
        var cb = Read(x[0] + 0x28, 8); if (cb == null || BitConverter.ToUInt64(cb, 0) != x[1]) continue;
        var hb = Read(x[1] + 0x238, 16); if (hb == null) continue;
        double hp = Fix(hb, 0), max = Fix(hb, 8);
        if (max <= 0 || max > 1e13 || hp < 0 || hp > max) continue;
        a.Bases.Add(new ArenaBase { Unit = (int)x[2], Owner = (uint)x[3], Hp = hp, MaxHp = max });
        if (diag != null) diag.Append("base ").Append(x[2]).Append(" hp ").Append(hp.ToString("0")).Append('/').Append(max.ToString("0")).Append('\n');
      }
      return a;
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
        .Append(", hero info ").Append(Klass(RvaHeroInfo).ToString("X")).Append(", owner ").Append(Klass(RvaOwner).ToString("X"))
        .Append(", statistics ").Append(Klass(RvaStats).ToString("X")).Append(", stat line ").Append(Klass(RvaDmgStat).ToString("X"))
        .Append(", TDStateData ").Append(Klass(RvaTDState).ToString("X")).Append(", wave system ").Append(Klass(RvaWaveSystem).ToString("X"))
        .Append(", wave controller ").Append(Klass(RvaWaveCtl).ToString("X")).Append(", attribute ").Append(Klass(RvaAttr).ToString("X")).Append('\n');
      return sb.ToString();
    }

    /// <summary>The boss's HP in per mille: the unit with the most max HP that is not one of the heroes' towers
    /// (-1 = none found).</summary>
    public static int BossHpPermille(ulong sim, ICollection<uint> towers) { double hp, max; return BossHp(sim, towers, out hp, out max); }

    /// <summary>The boss's HP and max HP (absolute) and its per mille (-1 = none found).</summary>
    public static int BossHp(ulong sim, ICollection<uint> towers, out double hpOut, out double maxOut) {
      hpOut = 0; maxOut = 0;
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
      hpOut = hp; maxOut = best;
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
    List<int[]> heroes;
    readonly Dictionary<ulong, uint> owners = new Dictionary<ulong, uint>();
    readonly RFX.ArenaRefs arenaRefs = new RFX.ArenaRefs();
    Thread th;

    public FightRecorder(ulong sim, int stage) { Sim = sim; Stage = stage; }
    /// <summary>An arena stage (attack 6001xxx, the defence run 6002xxx): its waves and bases are recorded too.</summary>
    public bool Arena { get { return Stage / 1000 == 6001 || Stage / 1000 == 6002; } }
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
      var st = RFX.ReadStatsBySide(Sim);
      if (st.Count > 0) lock (gate) tl.SetStats(st);
    }

    void Loop() {
      uint lastFrame = 0; int n = 0, errors = 0;
      try {
        heroes = RFX.BattleHeroList(Sim);
        Say("fight recorder: " + heroes.Count + " heroes, stage " + Stage);
        while (!stop) {
          uint fr; int st;
          if (!RFX.SimClock(Sim, out fr, out st) || st != 1) break;   // ended (2) or gone
          // the clock going back or a fight of hours: the memory is no longer this fight's simulation
          if (fr < lastFrame || (DateTime.UtcNow - StartedUtc).TotalMinutes > 40) break;
          if (fr != lastFrame) {
            lastFrame = fr;
            try {
              if (heroes.Count == 0) heroes = RFX.BattleHeroList(Sim);
              var hs = RFX.ReadHeroSamples(Sim, heroes, null, owners);
              if (hs != null) {
                lock (gate) tl.AddSample(fr, hs);
                var cb = OnSample; if (cb != null) cb(fr, hs);
              }
              if (Arena) {
                var a = RFX.ReadArena(Sim, arenaRefs, null);
                lock (gate) { if (a != null) tl.AddArena(fr, a); if (tl.GridW == 0 && RFX.TowerGridW > 0) tl.GridW = RFX.TowerGridW; }
              }
              if (++n % 5 == 0) {   // about every second: the command record and the boss
                Commands();
                var towers = new List<uint>();
                if (hs != null) foreach (var h in hs) if (h.Tower != 0) towers.Add(h.Tower);
                double bh, bm;
                int bp = RFX.BossHp(Sim, towers, out bh, out bm);
                lock (gate) { tl.AddBoss(fr, bp); tl.AddBossHp(fr, bh, bm); }
              }
            } catch (Exception e) { if (errors++ < 3) Say("fight recorder: " + e.Message); }
          }
          Thread.Sleep(IntervalMs);
        }
        // the end: the final frame count and the whole record (the simulation stays in memory at the result screen)
        uint ef; int es;
        if (RFX.SimClock(Sim, out ef, out es) && (es == 1 || es == 2)) {
          // the last look at the arena (the round's end and the bases as the fight ended)
          if (Arena) { var a = RFX.ReadArena(Sim, arenaRefs, null); if (a != null) lock (gate) tl.AddArena(ef, a); }
          lock (gate) { if (ef > tl.Frames) tl.Frames = ef; }
        }
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
