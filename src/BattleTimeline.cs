// RealmForge — the timeline of the fight going on (read-only): who was placed where and when, who fell, whose ultimate
// went off, sampled from the running simulation, plus the simulation's own record of the player's commands. The pure
// part (events from samples, merging, JSON) is src/TimelineBuilder.cs. Only ReadProcessMemory: nothing is written.
//
// Offsets of this game build; the classes, the type and status values and the read sizes come with the layout data
// from the server (src/Layout.cs).
//   CSharpBattle.Battle.GameSimulation: _EntityWorld 0x28, _FrameDataManager 0x38, _BattleData 0x48,
//     _BattlePlayerManager 0x50, m_state 0xA4, <CurrentFrameIdx> 0xC4 (see src/BattleClock.cs).
//   EntityWorld: _idEntities 0x20 (Dictionary<uint, IEntity>), _lTypeComponents 0x28 (Dictionary<int,
//     DLRecordList<uint, IComponent>>, key = EComponentType), <SingleEntity> 0x58 (the world's single components).
//   Entity: <m_uID> 0x10, _componentQuerys 0x20 (Dictionary<int, DLNode<int, IComponent>>, key = EComponentType).
//   DLList/DLRecordList: _FirstNode 0x10; DLNode: iIndex 0x10, _data 0x18, _prev 0x20, _next 0x28.
//   AbstractComponent: IsEnable 0x10, _entity 0x18. EComponentType: the key of the component lists.
//   CardComponent (one per hero of the deck): iUid 0x20 (= hero uid), eState 0x24 (ECardStatus), iCurRebornTime 0x28,
//     iMaxRebornTime 0x2C, ePlaceFace 0x3C, iTowerUid 0x4C (the hero's entity on the field), eCardType 0x50 (heroes and
//     soldiers).
//   DeadComponent: bTrigger 0x20, m_deadType 0x24 (EDeadType: killed, retreat, forced retreat).
//   AngerComponent (rage): curState 0x20 (EAngerStatus), fAnger 0x30, fMaxAnger 0x38 (Fix64).
//   SkillComponent: bNextUseSuperManualSkill 0xB0, iUseSuperManualSkillFrameIdx 0xB4 (frame of the last ultimate).
//   AttributeComponent: m_tableData 0x20 (CData_Unit_Element: m_ID 0x14), m_battleAttr 0x28 (BattleAttributeData:
//     <fHp> 0x248, m_fMaxHp 0x250, Fix64).
//   PositionComponent: m_faceType 0x30 (FaceType 0 right, 90 down, 180 left, 270 up), m_position 0x48 (FixVector3).
//   MapComponent: iMapSize 0x20, cMapMarker 0x40 -> MapMarker: m_iPosMapSize 0x18, m_vUidTowerExist 0x20 (uint[,]:
//     the tower entity on each tile, [x, y] as MapMarker.GetTowerIdInGrid(x, y)).
//   BattlePlayerDataManager: _vPlayers 0x20 (Dictionary<uint, BattlePlayerInfo>); BattlePlayerInfo: <cUid> 0x10,
//     m_lHeros 0x28 (List<BattleHeroInfo>); BattleHeroInfo: iUid 0x230, iUnitID 0x234, iSquadID 0x238,
//     iLordPosition 0x23C (1 = the squad's lord), iLordFaction 0x240.
//   FrameDataManager: _FrameRecord 0x30 -> FrameDataRecord: _vFrames 0x10, _vSuccessFrames 0x18 (Dictionary<uint frame,
//     List<FrameIdxInfo>>); FrameIdxInfo: Idx 0x10, Cmd 0x14 (ushort), iUid 0x18 (the controller), Params 0x20 (int[]).
//     SYNC_PUT_TOWER [unitId, x, y, face], SYNC_USE_POWERSKILL [unitId, x, y].
//   OwnerRelationComponent (on every card entity, EntitySpawn.CreateCardEntity: AddComponent<OwnerRelation> then
//     cUid = the card's controller; also on the towers): cUid 0x20, iSquadID 0x24, iLordPosition 0x28.
//   Sides (the arena): heroes of controller 1 (the player) and 2 (the opponent's recorded defence) share one world and
//     often the same uid, so a hero is (controller, uid); the controller of a card = its entity's OwnerRelation cUid.
//   StatisticsComponent (world single): m_vDamageStatistics 0x20 (Dictionary<uint cUid,
//     Dictionary<int unit, DamageStatisticsData>>); DamageStatisticsData: iBaseID 0x10, iHeroID 0x14, iPower 0x18,
//     fDamageAmount 0x28 (long), fDamageAmountToBoss 0x30, fTreatmentAmount 0x38 (int), fAcceptDamageAmount 0x3C,
//     fDamageOverflowAmount 0x58; m_vSkillDamage 0x78 (Dictionary<uint, Dictionary<int, SkillDamageStatisticsData>>, one field
//     vDamageAmount 0x10: Dictionary<int skill, long>; build 1.6.46 only, else from the layout's fields) — the objects the result's statistic lines (src/LiveTables.cs DumpBattleStats) are.
//   Arena waves: WaveInfoComponent (world single): cWaveSysytem 0x28 -> WaveSystem: m_vActiveController 0x50
//     (Dictionary<uint ctl, WaveControllerNode>; 100 = side 1's waves, 101 = side 2's) -> WaveControllerNode: cUid 0x10,
//     m_NodeData 0x28 (stWaveNodeData: iMinFrame 0, iMaxFrame 4, iRemainCount 8, iMaxCount 0xC, iRemainPopulation 0x10,
//     iMaxPopulation 0x14, iActiveNode 0x18 -> 0x40 in the node), eState 0x44, iTimeDeviation 0x50, iMaxWaveCount 0x5C,
//     iCurWaveCount 0x60. TDStateData (GameSimulation._lIndependentDatas 0x98, a DLList of IIndependentData; found by its
//     class): vMonsterCount 0x58, vMonsterAdvanceIgnoreCount 0x60 (Dictionary<uint, int>; WaveSystem.CanAdvanceUidCellNode
//     reads them so). The bases: the units 4301–4306 / 4345–4350 (AttributeComponent m_tableData m_ID), their HP
//     as the heroes' (BattleAttributeData <fHp> 0x248, m_fMaxHp 0x250), their side from their OwnerRelation cUid.
//     Not verified on a live arena fight yet.
//   Unit trace (arena, ReadUnits): every PositionComponent of the world -> _entity 0x18 -> Entity <m_uID> 0x10; once per
//     entity: its AttributeComponent (m_tableData m_ID, m_battleAttr) and OwnerRelation cUid.
//   Attack log (test build, FightRecorder.AttackLog, src/AttackLog.cs; ReadAttackState): SkillComponent cUseSkill 0x48 (StateScript:
//     m_bRunning 0x49, m_skillId 0x4C), vSkillConvertRelations 0x80, vSkillCDs 0xA0 (Dictionary<int, SkillCDData>), ArraySlots 0xA8
//     (int[]: [0] basic attack, [1] ultimate); SkillCDData: iSkillID 0x10, iCoolDown 0x18, iCoolDownMax 0x1C, iLastTime 0x20, bPause
//     0x28; BattleAttributeData m_nAttackCD 0x25C. Layout "field" section (RFX.AtkOffsets), built-in only for 1.6.46 / 1.6.47.
//   .NET collections: Dictionary entries 0x18, count 0x20; Entry<int|uint, ref> = {hashCode, next, key, value@8};
//     Entry<uint, int> = {hashCode, next, key, value}; List<T> _items 0x10, _size 0x18.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace RealmForge {
  public static partial class RFX {
    /// <summary>A component type (EComponentType) from the layout data.</summary>
    static int CT(string name) { return Layout.Current.Const("EComponentType." + name); }
    static int Hdr(string name) { return Layout.Current.Hdr(name); }
    static int Span(string name) { return Layout.Current.Span(name); }
    // the classes checked on the objects read (their addresses: layout data from the server)
    const string ClsCard = NsBattle + "CardComponent", ClsAnger = NsBattle + "AngerComponent", ClsSkill = NsBattle + "SkillComponent",
                 ClsFrameIdxInfo = NsBattle + "FrameIdxInfo", ClsHeroInfo = NsBattle + "BattleHeroInfo";
    const string ClsOwner = NsBattle + "OwnerRelationComponent", ClsStats = NsBattle + "StatisticsComponent",
                 ClsDmgStat = NsBattle + "DamageStatisticsData", ClsTDState = NsBattle + "TDStateData", ClsWaveSystem = NsBattle + "WaveSystem",
                 ClsWaveCtl = NsBattle + "WaveControllerNode", ClsAttr = NsBattle + "AttributeComponent";

    static ulong P64(ulong a) { var b = a != 0 ? Read(a, 8) : null; return b != null ? BitConverter.ToUInt64(b, 0) : 0; }
    static double Fix(byte[] b, int o) { return BitConverter.ToInt64(b, o) / (double)Layout.Current.Const("Fix64.ONE"); }

    /// <summary>A sample wants a hero's battle stats (set by the recorder: only until it has them).</summary>
    public static Func<HeroSample, bool> wantBase;
    /// <summary>BattleAttributeData.m_attr (stBattleBaseAttr at +0x10, Fix64; percent stats in percent units)
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
      // iSkills / iSkillLevels (int[]; m_attr relative): the skills the hero fights with — its own, its gear sets' and its
      // artifact's, the opponent's too (the opponents' list has only their stats): -100-i = skill id, -200-i = its level.
      // Their offsets moved with the game build (see SkillArrayOffsets): none known = the skills are left out
      int oIds, oLvs;
      int[] ids = null, lvs = null;
      if (SkillArrayOffsets(Layout.Current, out oIds, out oLvs) && oLvs + 8 <= b.Length) {
        ids = IntArray(BitConverter.ToUInt64(b, oIds), 40); lvs = IntArray(BitConverter.ToUInt64(b, oLvs), 40);
      }
      for (int i = 0; ids != null && i < ids.Length; i++) {
        d[-100 - i] = ids[i];
        d[-200 - i] = lvs != null && i < lvs.Length ? lvs[i] : 0;
      }
      return d.ContainsKey(2) && d[2] > 0 ? d : null;
    }

    /// <summary>The builds whose stBattleBaseAttr skill arrays were checked in the game's dumps: game version -> {iSkills, iSkillLevels}
    /// offsets (bIsHiddenShape took 0x180 in 1.6.46, the arrays moved by 8). The layout's "field" section wins over this table.</summary>
    static readonly Dictionary<string, int[]> SkillArrayBuilds = new Dictionary<string, int[]> {
      { "1.6.38.829.1", new[] { 0x180, 0x188 } }, { "1.6.39.829.1", new[] { 0x180, 0x188 } },
      { "1.6.46.841.1", new[] { 0x188, 0x190 } }, { "1.6.47.841.1", new[] { 0x188, 0x190 } } };

    /// <summary>The offsets of stBattleBaseAttr.iSkills and iSkillLevels (relative to m_attr): the layout's "field" section
    /// (stBattleBaseAttr.iSkills / .iSkillLevels), else the table of the checked builds; false = not known for this build.</summary>
    public static bool SkillArrayOffsets(GameLayout ly, out int iSkills, out int iSkillLevels) {
      iSkills = -1; iSkillLevels = -1;
      if (ly == null) return false;
      iSkills = ly.FieldOffset("stBattleBaseAttr.iSkills"); iSkillLevels = ly.FieldOffset("stBattleBaseAttr.iSkillLevels");
      int[] built;
      if ((iSkills < 0 || iSkillLevels < 0) && ly.Game != null && SkillArrayBuilds.TryGetValue(ly.Game, out built)) { iSkills = built[0]; iSkillLevels = built[1]; }
      return iSkills >= 0 && iSkillLevels >= 0;
    }

    /// <summary>An int[], null when unreadable or longer than max.</summary>
    static int[] IntArray(ulong arr, int max) {
      var h = arr != 0 ? Read(arr + (ulong)Hdr("arrLen"), 8) : null;
      if (h == null) return null;
      long n = BitConverter.ToInt64(h, 0);
      if (n < 0 || n > max) return null;
      if (n == 0) return new int[0];
      var b = Read(arr + (ulong)Hdr("arrData"), (int)n * 4);
      if (b == null) return null;
      var r = new int[n];
      for (int i = 0; i < n; i++) r[i] = BitConverter.ToInt32(b, i * 4);
      return r;
    }

    /// <summary>A class of the current game process (0 when not known yet), see ClassAt.</summary>
    static ulong Klass(string cls) { return ClassAt(simGa, cls); }

    /// <summary>A Dictionary&lt;int|uint, reference&gt;'s entries (key, value).</summary>
    static List<KeyValuePair<uint, ulong>> DictRefs(ulong dict, int max) {
      var res = new List<KeyValuePair<uint, ulong>>();
      var h = dict != 0 ? Read(dict, 0x28) : null; if (h == null) return res;
      ulong ent = BitConverter.ToUInt64(h, 0x18); int count = BitConverter.ToInt32(h, 0x20);
      if (ent == 0 || count <= 0 || count > max) return res;
      int al = Hdr("arrLen"), d0 = Hdr("arrData") - al, es = Layout.Current.Size("DictEntryRef");
      var e = Read(ent + (ulong)al, d0 + count * es); if (e == null) return res;
      if (BitConverter.ToInt64(e, 0) < count) return res;
      for (int i = 0; i < count; i++) {
        int o = d0 + i * es;
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
        var b = Read(n, Span("DLNode")); if (b == null) break;
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
      foreach (var pl in DictRefs(P64(mgr + 0x20), 16)) {
        var pb = Read(pl.Value, Span("BattlePlayerInfo")); if (pb == null) continue;
        uint cuid = BitConverter.ToUInt32(pb, 0x10);
        var lb = Read(BitConverter.ToUInt64(pb, 0x28), 0x20); if (lb == null) continue;
        int n = BitConverter.ToInt32(lb, 0x18); if (n <= 0 || n > 64) continue;
        var items = Read(BitConverter.ToUInt64(lb, 0x10) + (ulong)Hdr("arrData"), n * 8); if (items == null) continue;
        for (int i = 0; i < n; i++) {
          ulong hi = BitConverter.ToUInt64(items, i * 8);
          if (!IsA(hi, ClsHeroInfo)) continue;
          var hb = Read(hi + 0x230, 0x14); if (hb == null) continue;
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
      ulong o = Comp(nodes, CT("OwnerRelation"));
      if (o == 0 || !IsA(o, ClsOwner)) return 0;
      var b = Read(o + 0x20, 4);
      return b != null ? BitConverter.ToUInt32(b, 0) : 0;
    }

    /// <summary>The towers on the map's tiles: tower entity id -> {x, y} (MapMarker.m_vUidTowerExist).</summary>
    static Dictionary<uint, int[]> TowerTiles(ulong world, StringBuilder diag) {
      var res = new Dictionary<uint, int[]>();
      ulong single = P64(world + 0x58); if (single == 0) return res;
      ulong map = Comp(CompNodes(single), CT("Map")); if (map == 0) return res;
      ulong marker = P64(map + 0x40); if (marker == 0) return res;
      var mb = Read(marker, Span("MapMarker")); if (mb == null) return res;
      ulong arr = BitConverter.ToUInt64(mb, 0x20); if (arr == 0) return res;
      ulong bp = P64(arr + (ulong)Hdr("arrBounds")); if (bp == 0) return res;
      int bl = Hdr("boundsLen"), bs = Hdr("boundsStride");
      var bb = Read(bp, 2 * bs); if (bb == null) return res;
      long d0 = BitConverter.ToInt64(bb, bl), d1 = BitConverter.ToInt64(bb, bs + bl);
      if (diag != null) {
        var ms = Read(map + 0x20, 8);
        diag.Append("map size ").Append(ms != null ? BitConverter.ToInt32(ms, 0) + "x" + BitConverter.ToInt32(ms, 4) : "?")
            .Append(", marker pos size ").Append(BitConverter.ToInt32(mb, 0x18)).Append('x').Append(BitConverter.ToInt32(mb, 0x1C))
            .Append(", tower grid ").Append(d0).Append('x').Append(d1).Append('\n');
      }
      if (d0 <= 0 || d1 <= 0 || d0 > 256 || d1 > 256) return res;
      TowerGridW = (int)d0;
      var data = Read(arr + (ulong)Hdr("arrData"), (int)(d0 * d1 * 4)); if (data == null) return res;
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
      var cards = WorldComps(world, CT("Card"), 128);
      Dictionary<uint, ulong> ents = null; Dictionary<uint, int[]> tiles = null;
      if (diag != null) diag.Append("cards ").Append(cards.Count).Append(", card klass ").Append(Klass(ClsCard).ToString("X")).Append('\n');
      foreach (var card in cards) {
        var cb = Read(card, Span("CardComponent")); if (cb == null) continue;
        if (!IsKlass(BitConverter.ToUInt64(cb, Hdr("klass")), ClsCard)) { if (diag != null) diag.Append("  not a card: ").Append(card.ToString("X")).Append('\n'); continue; }
        if (BitConverter.ToInt32(cb, 0x50) != Layout.Current.Const("ECardEntityType.Normal")) continue;   // a soldier card
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
            ulong dead = Comp(nodes, CT("Dead"));
            if (dead != 0) { var db = Read(dead + 0x20, 8); s.Dead = true; s.DeadType = db != null ? BitConverter.ToInt32(db, 4) : -1; }
            ulong ang = Comp(nodes, CT("Anger"));
            var ab = ang != 0 && IsA(ang, ClsAnger) ? Read(ang, Span("AngerComponent")) : null;
            if (ab != null) { s.AngerState = BitConverter.ToInt32(ab, 0x20); s.Anger = Fix(ab, 0x30); s.MaxAnger = Fix(ab, 0x38); }
            ulong sk = Comp(nodes, CT("Skill"));
            var sb = sk != 0 && IsA(sk, ClsSkill) ? Read(sk + 0xB0, 8) : null;
            if (sb != null) s.UltFrame = BitConverter.ToUInt32(sb, 4);
            ulong at = Comp(nodes, CT("Attribute"));
            ulong ba = at != 0 ? P64(at + 0x28) : 0;
            var hb = ba != 0 ? Read(ba + 0x248, 16) : null;
            if (hb != null) { s.Hp = Fix(hb, 0); s.MaxHp = Fix(hb, 8); }
            // the battle stats it entered with (m_attr, stBattleBaseAttr): read once, while the builder has none for it
            if (ba != 0 && (wantBase == null || wantBase(s))) s.Base = BaseAttrs(ba);
            ulong pos = Comp(nodes, CT("Position"));
            var pb = pos != 0 ? Read(pos + 0x30, 4) : null;
            if (pb != null) s.Face = BitConverter.ToInt32(pb, 0);
            if (tiles == null) tiles = TowerTiles(world, diag);
            int[] xy;
            if (tiles.TryGetValue(s.Tower, out xy)) { s.X = xy[0]; s.Y = xy[1]; }
            if (diag != null && pos != 0) {
              var wp = Read(pos + 0x48, Layout.Current.Size("FixVector3"));
              if (wp != null) diag.Append("  tower ").Append(s.Tower).Append(" world ").Append(Fix(wp, 0).ToString("0.00")).Append(',')
                                  .Append(Fix(wp, 8).ToString("0.00")).Append(',').Append(Fix(wp, 0x10).ToString("0.00")).Append('\n');
            }
          }
        }
        res.Add(s);
      }
      return res;
    }

    /// <summary>The offsets the attack log reads (layout "field" keys, see tools/app-offsets.mjs).</summary>
    public sealed class AtkOff {
      public int Use, Convert, CDs, Slots, Running, UseSkill, CdId, Cd, CdMax, CdLast, CdPause, AtkCd;
    }
    static readonly string[] AtkKeys = {
      "SkillComponent.cUseSkill", "SkillComponent.vSkillConvertRelations", "SkillComponent.vSkillCDs", "SkillComponent.ArraySlots",
      "StateScript.m_bRunning", "StateScript.m_skillId", "SkillCDData.iSkillID", "SkillCDData.iCoolDown", "SkillCDData.iCoolDownMax",
      "SkillCDData.iLastTime", "SkillCDData.bPause", "BattleAttributeData.m_nAttackCD" };
    // the same values in the dumps of 1.6.46.841.1 and 1.6.47.841.1 (the only builds checked for them)
    static readonly int[] AtkBuilt = { 0x48, 0x80, 0xA0, 0xA8, 0x49, 0x4C, 0x10, 0x18, 0x1C, 0x20, 0x28, 0x25C };
    static readonly string[] AtkBuilds = { "1.6.46.841.1", "1.6.47.841.1" };

    /// <summary>The attack log's offsets for a layout: its "field" section, a missing key from the built-in table for a checked
    /// build only; null = not known (the log stays off).</summary>
    public static AtkOff AtkOffsets(GameLayout ly) {
      if (ly == null) return null;
      bool built = Array.IndexOf(AtkBuilds, ly.Game) >= 0;
      var v = new int[AtkKeys.Length];
      for (int i = 0; i < v.Length; i++) {
        v[i] = ly.FieldOffset(AtkKeys[i]);
        if (v[i] < 0 && built) v[i] = AtkBuilt[i];
        if (v[i] < 0) return null;
      }
      return new AtkOff { Use = v[0], Convert = v[1], CDs = v[2], Slots = v[3], Running = v[4], UseSkill = v[5], CdId = v[6], Cd = v[7], CdMax = v[8], CdLast = v[9], CdPause = v[10], AtkCd = v[11] };
    }

    /// <summary>One hero the attack poller follows: its card, tower and the addresses read again and again.</summary>
    public sealed class AtkHero {
      public ulong Card, Skill, Battle; public uint C, Uid, Tower; public int Unit;
      public List<KeyValuePair<uint, ulong>> Cds; public int S0, S1; public Dictionary<int, int> Conv; public int Polls;
    }

    /// <summary>What the attack poller keeps between polls (one thread uses it).</summary>
    public sealed class AttackRefs {
      public List<AtkHero> Heroes = new List<AtkHero>();
      public AtkOff Off; public int Cycle;
      public readonly Dictionary<ulong, uint> Owners = new Dictionary<ulong, uint>();
    }

    sealed class AtkPending {
      public AtkHero H; public List<CdSnap> Cds = new List<CdSnap>(); public UseSnap Use; public int AttackCd = -1;
    }

    /// <summary>The cards of the fight's heroes with a stable address (the hero's tower and its components are resolved later).</summary>
    static void AtkCards(ulong sim, List<int[]> heroes, AttackRefs refs) {
      ulong world = P64(sim + 0x28); if (world == 0) return;
      var have = new Dictionary<ulong, AtkHero>();
      foreach (var h in refs.Heroes) have[h.Card] = h;
      var list = new List<AtkHero>();
      foreach (var card in WorldComps(world, CT("Card"), 128)) {
        var cb = Read(card, Span("CardComponent")); if (cb == null) continue;
        if (!IsKlass(BitConverter.ToUInt64(cb, Hdr("klass")), ClsCard)) continue;
        if (BitConverter.ToInt32(cb, 0x50) != Layout.Current.Const("ECardEntityType.Normal")) continue;
        AtkHero h;
        if (!have.TryGetValue(card, out h)) h = new AtkHero { Card = card };
        h.Uid = BitConverter.ToUInt32(cb, 0x20);
        uint own;
        if (!refs.Owners.TryGetValue(card, out own)) { own = OwnerOf(CompNodes(BitConverter.ToUInt64(cb, 0x18))); if (own != 0) refs.Owners[card] = own; }
        h.C = own;
        int[] hi = HeroOf(heroes, h.Uid, h.C);
        if (hi != null) { h.Unit = hi[1]; if (h.C == 0) h.C = (uint)hi[4]; }
        list.Add(h);
      }
      refs.Heroes = list;
    }

    /// <summary>The hero's tower entity -> its skill and attribute components (Skill = 0 when it is not on the field).</summary>
    static void AtkResolve(ulong world, AtkHero h, uint tower, ref Dictionary<uint, ulong> ents) {
      h.Tower = tower; h.Skill = 0; h.Battle = 0; h.Cds = null;
      if (tower == 0) return;
      if (ents == null) {
        ents = new Dictionary<uint, ulong>();
        foreach (var kv in DictRefs(P64(world + 0x20), 100000)) ents[kv.Key] = kv.Value;
      }
      ulong te;
      if (!ents.TryGetValue(tower, out te) || te == 0) return;
      var nodes = CompNodes(te);
      ulong sk = Comp(nodes, CT("Skill")), at = Comp(nodes, CT("Attribute"));
      if (sk == 0 || !IsA(sk, ClsSkill)) return;
      h.Skill = sk; h.Battle = at != 0 ? P64(at + 0x28) : 0;
    }

    /// <summary>One poll of the attack log: the clock, every hero's cooldowns, cast script and attack cooldown, the clock again;
    /// a poll in which the clock moved is dropped (log.NoteTorn). Only cached addresses are read (the hero's card for its tower,
    /// its components, the cooldown objects), the cooldown dictionary and the skill slots again every 30 polls and when the tower
    /// changes; the cards every 300 polls. False = nothing logged (torn, no clock, no offsets).</summary>
    public static bool ReadAttackState(ulong sim, List<int[]> heroes, AttackRefs refs, AttackLog log, out uint frame) {
      frame = 0;
      var ly = Layout.Current; if (ly == null) return false;
      if (refs.Off == null) { refs.Off = AtkOffsets(ly); if (refs.Off == null) return false; }
      var o = refs.Off;
      uint f1; int st1;
      if (!SimClock(sim, out f1, out st1) || st1 != SimRunning) return false;
      ulong world = P64(sim + 0x28); if (world == 0) return false;
      if (refs.Cycle++ % (refs.Heroes.Count == 0 ? 30 : 300) == 0) AtkCards(sim, heroes, refs);
      Dictionary<uint, ulong> ents = null;
      var pend = new List<AtkPending>();
      int cdSize = Math.Max(Math.Max(Math.Max(o.CdId, o.Cd), Math.Max(o.CdMax, o.CdLast)), o.CdPause) + 4;
      int useLo = Math.Min(o.Running, o.UseSkill), useHi = Math.Max(o.Running, o.UseSkill) + 4;
      foreach (var h in refs.Heroes) {
        var tb = Read(h.Card + 0x4C, 4); if (tb == null) continue;
        uint tower = BitConverter.ToUInt32(tb, 0);
        if (tower != h.Tower || (tower != 0 && h.Skill == 0 && h.Polls % 30 == 0)) AtkResolve(world, h, tower, ref ents);
        h.Polls++;
        if (h.Skill == 0) continue;
        if (h.Cds == null || h.Polls % 30 == 0) {
          h.Cds = DictRefs(P64(h.Skill + (ulong)o.CDs), 64);
          var sl = IntArray(P64(h.Skill + (ulong)o.Slots), 8);
          h.S0 = sl != null && sl.Length > 0 ? sl[0] : 0; h.S1 = sl != null && sl.Length > 1 ? sl[1] : 0;
          var cv = DictInts(P64(h.Skill + (ulong)o.Convert), 64);
          h.Conv = new Dictionary<int, int>();
          foreach (var kv in cv) h.Conv[(int)kv.Key] = kv.Value;
        }
        var p = new AtkPending { H = h };
        foreach (var kv in h.Cds) {
          var cb = Read(kv.Value, cdSize); if (cb == null) continue;
          p.Cds.Add(new CdSnap((int)kv.Key, BitConverter.ToInt32(cb, o.CdId), BitConverter.ToInt32(cb, o.Cd), BitConverter.ToInt32(cb, o.CdMax),
                               BitConverter.ToInt32(cb, o.CdLast), cb[o.CdPause] != 0));
        }
        ulong us = P64(h.Skill + (ulong)o.Use);
        var ub = us != 0 ? Read(us + (ulong)useLo, useHi - useLo) : null;
        if (ub != null) p.Use = new UseSnap(ub[o.Running - useLo] != 0, us, BitConverter.ToInt32(ub, o.UseSkill - useLo));
        var ab = h.Battle != 0 ? Read(h.Battle + (ulong)o.AtkCd, 4) : null;
        if (ab != null) p.AttackCd = BitConverter.ToInt32(ab, 0);
        pend.Add(p);
      }
      uint f2; int st2;
      if (!SimClock(sim, out f2, out st2) || f2 != f1) { log.NoteTorn(); return false; }
      foreach (var p in pend) log.Feed(p.H.C, p.H.Uid, p.H.Unit, f1, p.Cds, p.Use, p.H.S0, p.H.S1, p.H.Conv, p.AttackCd);
      log.NotePoll(f1);
      frame = f1;
      return true;
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
      int al = Hdr("arrLen"), d0 = Hdr("arrData") - al, es = Layout.Current.Size("DictEntryInt");
      var e = Read(ent + (ulong)al, d0 + count * es); if (e == null) return res;
      if (BitConverter.ToInt64(e, 0) < count) return res;
      for (int i = 0; i < count; i++) {
        int o = d0 + i * es;
        if (BitConverter.ToInt32(e, o) < 0) continue;
        res[BitConverter.ToUInt32(e, o + 8)] = BitConverter.ToInt32(e, o + 12);
      }
      return res;
    }

    /// <summary>The heroes' battle statistics by controller (StatisticsComponent.m_vDamageStatistics); empty when not found.</summary>
    public static List<StatLine> ReadStatsBySide(ulong sim) {
      var res = new List<StatLine>();
      ulong world = P64(sim + 0x28); if (world == 0) return res;
      ulong st = Comp(CompNodes(P64(world + 0x58)), CT("Statistics"));
      if (st == 0 || !IsA(st, ClsStats)) return res;
      foreach (var side in DictRefs(P64(st + 0x20), 16))
        foreach (var u in DictRefs(side.Value, 128)) {
          if (!IsA(u.Value, ClsDmgStat)) continue;
          var b = Read(u.Value, Span("DamageStatisticsData")); if (b == null) continue;
          res.Add(new StatLine { C = side.Key, Unit = (int)u.Key, At = u.Value, Hero = BitConverter.ToUInt32(b, 0x14), Power = BitConverter.ToUInt32(b, 0x18),
                                 Damage = BitConverter.ToInt64(b, 0x28), ToBoss = BitConverter.ToInt64(b, 0x30), Heal = BitConverter.ToInt32(b, 0x38),
                                 Taken = BitConverter.ToInt32(b, 0x3C), Overflow = BitConverter.ToInt64(b, 0x58) });
        }
      return res;
    }

    /// <summary>A Dictionary&lt;int, long&gt;'s entries (Entry: hashCode 0, next 4, key 8, value 0x10, stride 0x18); empty on any doubt
    /// (more than <paramref name="max"/> entries, a bad array, a negative value).</summary>
    static Dictionary<int, long> DictLongs(ulong dict, int max) {
      var res = new Dictionary<int, long>();
      try {
        var h = dict != 0 ? Read(dict, 0x28) : null; if (h == null) return res;
        ulong ent = BitConverter.ToUInt64(h, 0x18); int count = BitConverter.ToInt32(h, 0x20);
        if (ent == 0 || count <= 0 || count > max) return res;
        int al = Hdr("arrLen"), d0 = Hdr("arrData") - al; const int es = 0x18;
        var e = Read(ent + (ulong)al, d0 + count * es); if (e == null) return res;
        if (BitConverter.ToInt64(e, 0) < count) return res;
        var tmp = new Dictionary<int, long>();
        for (int i = 0; i < count; i++) {
          int o = d0 + i * es;
          if (BitConverter.ToInt32(e, o) < 0) continue;
          int k = BitConverter.ToInt32(e, o + 8); long v = BitConverter.ToInt64(e, o + 0x10);
          if (k <= 0 || v < 0) return res;
          tmp[k] = v;
        }
        return tmp;
      } catch (Exception) { return new Dictionary<int, long>(); }
    }

    // the builds whose dumps were checked for the skill statistics offsets (the same values in 1.6.46 and 1.6.47)
    static readonly string[] SkillBuilds = { "1.6.46.841.1", "1.6.47.841.1" };

    /// <summary>The skill statistics offsets for a layout: its "field" section, the built-in values for a checked build only; false = unknown.</summary>
    public static bool SkillOffsets(GameLayout ly, out int oSk, out int oDa) {
      oSk = -1; oDa = -1;
      if (ly == null) return false;
      oSk = ly.FieldOffset("StatisticsComponent.m_vSkillDamage"); oDa = ly.FieldOffset("SkillDamageStatisticsData.vDamageAmount");
      if ((oSk < 0 || oDa < 0) && Array.IndexOf(SkillBuilds, ly.Game) >= 0) { oSk = 0x78; oDa = 0x10; }
      return oSk >= 0 && oDa >= 0;
    }

    /// <summary>The per-skill battle statistics: "controller:unit" -> {skill id: amount} (StatisticsComponent.m_vSkillDamage). Offsets
    /// come from the layout's optional fields, or the built-in ones for the checked builds only; null when unknown.</summary>
    public static Dictionary<string, Dictionary<int, long>> ReadSkillStats(ulong sim) {
      try {
        var ly = Layout.Current; if (ly == null) return null;
        int oSk, oDa; if (!SkillOffsets(ly, out oSk, out oDa)) return null;
        ulong world = P64(sim + 0x28); if (world == 0) return null;
        ulong st = Comp(CompNodes(P64(world + 0x58)), CT("Statistics"));
        if (st == 0 || !IsA(st, ClsStats)) return null;
        return ReadSkillStatsAt(st, oSk, oDa);
      } catch (Exception) { return null; }
    }

    /// <summary>The reader behind <see cref="ReadSkillStats"/>, for a known statistics object and offsets: at most 64 heroes and 64
    /// skills each; null (nothing) on any doubt.</summary>
    public static Dictionary<string, Dictionary<int, long>> ReadSkillStatsAt(ulong st, int oSk, int oDa) {
      try {
        var res = new Dictionary<string, Dictionary<int, long>>();
        int heroes = 0;
        foreach (var side in DictRefs(P64(st + (ulong)oSk), 16))
          foreach (var u in DictRefs(side.Value, 64)) {
            if (u.Value == 0) continue;
            if (++heroes > 64) return null;
            var sk = DictLongs(P64(u.Value + (ulong)oDa), 64);
            if (sk.Count > 0) res[side.Key + ":" + (int)u.Key] = sk;
          }
        return res;
      } catch (Exception) { return null; }
    }

    /// <summary>What the unit trace keeps of an entity between passes (entity address -> this).</summary>
    public sealed class UnitCache {
      public uint Uid, Owner; public int Unit; public ulong Attr, Battle;
      public bool Skip;   // no attribute / unit id: not a unit
    }

    /// <summary>The units with a position in the world (PositionComponent list): unit id, owner, position, face, HP. The
    /// entity's components are read once (<paramref name="cache"/>, keyed by entity address and checked by uid); each pass
    /// reads the position and the HP only. <paramref name="towers"/> (optional) marks the heroes' towers.</summary>
    public static List<UnitSample> ReadUnits(ulong sim, Dictionary<ulong, UnitCache> cache, ICollection<uint> towers) {
      var res = new List<UnitSample>();
      ulong world = P64(sim + 0x28); if (world == 0) return res;
      foreach (var pos in WorldComps(world, CT("Position"), 4000)) {
        var pb = Read(pos, 0x60); if (pb == null) continue;
        ulong ent = BitConverter.ToUInt64(pb, 0x18); if (ent == 0) continue;
        var ub = Read(ent + 0x10, 4); if (ub == null) continue;
        uint uid = BitConverter.ToUInt32(ub, 0);
        UnitCache c;
        if (!cache.TryGetValue(ent, out c) || c.Uid != uid) {
          c = new UnitCache { Uid = uid };
          var nodes = CompNodes(ent);
          ulong at = Comp(nodes, CT("Attribute"));
          var ab = at != 0 ? Read(at + 0x20, 16) : null;
          ulong td = ab != null ? BitConverter.ToUInt64(ab, 0) : 0, ba = ab != null ? BitConverter.ToUInt64(ab, 8) : 0;
          var ib = td != 0 ? Read(td + 0x14, 4) : null;
          if (ib == null || ba == 0) c.Skip = true;
          else { c.Attr = at; c.Battle = ba; c.Unit = BitConverter.ToInt32(ib, 0); c.Owner = OwnerOf(nodes); }
          cache[ent] = c;
        }
        if (c.Skip) continue;
        // the same attribute data (a dead unit's memory can be reused)
        var cb = Read(c.Attr + 0x28, 8); if (cb == null || BitConverter.ToUInt64(cb, 0) != c.Battle) continue;
        var hb = Read(c.Battle + 0x248, 16); if (hb == null) continue;
        double hp = Fix(hb, 0), max = Fix(hb, 8);
        double x = Fix(pb, 0x48), y = Fix(pb, 0x50), z = Fix(pb, 0x58);
        if (!Sane(x) || !Sane(y) || !Sane(z)) continue;
        if (max < 0 || max > 1e13 || hp < 0 || hp > max) continue;
        res.Add(new UnitSample { Uid = uid, Unit = c.Unit, Owner = c.Owner, Tower = towers != null && towers.Contains(uid),
                                 X = x, Y = y, Z = z, Face = BitConverter.ToInt32(pb, 0x30), Hp = hp, MaxHp = max });
      }
      return res;
    }
    static bool Sane(double v) { return !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < 1e4; }

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
        ulong list = P64(sim + 0x98); var seen = new HashSet<ulong>();
        for (ulong n = P64(list + 0x10); n != 0 && seen.Count < 64 && seen.Add(n); n = P64(n + 0x28)) {
          ulong d = P64(n + 0x18);
          if (IsA(d, ClsTDState)) { refs.State = d; break; }
        }
        if (diag != null) diag.Append("TDStateData ").Append(refs.State.ToString("X")).Append('\n');
      }
      Dictionary<uint, int> mon = null, ign = null;
      if (refs.State != 0) { mon = DictInts(P64(refs.State + 0x58), 64); ign = DictInts(P64(refs.State + 0x60), 64); }
      // the wave controllers
      ulong wi = Comp(CompNodes(P64(world + 0x58)), CT("WaveInfo"));
      ulong ws = wi != 0 ? P64(wi + 0x28) : 0;
      if (ws == 0 || !IsA(ws, ClsWaveSystem)) { if (diag != null) diag.Append("no wave system (wave info ").Append(wi.ToString("X")).Append(")\n"); return null; }
      foreach (var kv in DictRefs(P64(ws + 0x50), 16)) {
        if (!IsA(kv.Value, ClsWaveCtl)) continue;
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
        foreach (var at in WorldComps(world, CT("Attribute"), 4000)) {
          var ab = Read(at, 0x30); if (ab == null) continue;
          if (!IsKlass(BitConverter.ToUInt64(ab, Hdr("klass")), ClsAttr)) continue;
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
        var hb = Read(x[1] + 0x248, 16); if (hb == null) continue;
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
      foreach (var kv in DictRefs(P64(rec + (all ? 0x10UL : 0x18UL)), 200000)) {
        var lb = Read(kv.Value, 0x20); if (lb == null) continue;
        int n = BitConverter.ToInt32(lb, 0x18); if (n <= 0 || n > 1000) continue;
        var items = Read(BitConverter.ToUInt64(lb, 0x10) + (ulong)Hdr("arrData"), n * 8); if (items == null) continue;
        for (int i = 0; i < n; i++) {
          ulong f = BitConverter.ToUInt64(items, i * 8);
          var fb = f != 0 ? Read(f, Span("FrameIdxInfo")) : null; if (fb == null) continue;
          if (!IsKlass(BitConverter.ToUInt64(fb, Hdr("klass")), ClsFrameIdxInfo)) continue;
          var c = new FrameCmd { Frame = BitConverter.ToUInt32(fb, 0x10), Cmd = BitConverter.ToUInt16(fb, 0x14), CUid = BitConverter.ToUInt32(fb, 0x18) };
          ulong pa = BitConverter.ToUInt64(fb, 0x20);
          var ph = pa != 0 ? Read(pa + (ulong)Hdr("arrLen"), 8) : null;
          long pn = ph != null ? BitConverter.ToInt64(ph, 0) : 0;
          if (pn > 0 && pn <= 32) {
            var pd = Read(pa + (ulong)Hdr("arrData"), (int)pn * 4);
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
      if (world != 0) {
        var us = ReadUnits(sim, new Dictionary<ulong, UnitCache>(), null);
        sb.Append("units with a position: ").Append(us.Count).Append('\n');
        for (int i = 0; i < us.Count && i < 5; i++) {
          var u = us[i];
          sb.Append("  unit ").Append(u.Unit).Append(" uid ").Append(u.Uid).Append(" owner ").Append(u.Owner).Append(" world ").Append(u.X.ToString("0.00")).Append(',')
            .Append(u.Y.ToString("0.00")).Append(',').Append(u.Z.ToString("0.00")).Append(" hp ").Append(u.Hp.ToString("0")).Append('/').Append(u.MaxHp.ToString("0")).Append('\n');
        }
      }
      sb.Append("klasses: card ").Append(Klass(ClsCard).ToString("X")).Append(", anger ").Append(Klass(ClsAnger).ToString("X"))
        .Append(", skill ").Append(Klass(ClsSkill).ToString("X")).Append(", command ").Append(Klass(ClsFrameIdxInfo).ToString("X"))
        .Append(", hero info ").Append(Klass(ClsHeroInfo).ToString("X")).Append(", owner ").Append(Klass(ClsOwner).ToString("X"))
        .Append(", statistics ").Append(Klass(ClsStats).ToString("X")).Append(", stat line ").Append(Klass(ClsDmgStat).ToString("X"))
        .Append(", TDStateData ").Append(Klass(ClsTDState).ToString("X")).Append(", wave system ").Append(Klass(ClsWaveSystem).ToString("X"))
        .Append(", wave controller ").Append(Klass(ClsWaveCtl).ToString("X")).Append(", attribute ").Append(Klass(ClsAttr).ToString("X")).Append('\n');
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
      foreach (var at in WorldComps(world, CT("Attribute"), 2000)) {
        var ab = Read(at + 0x18, 0x18); if (ab == null) continue;
        ulong ent = BitConverter.ToUInt64(ab, 0), ba = BitConverter.ToUInt64(ab, 0x10);
        if (ba == 0) continue;
        if (towers != null && ent != 0) { var ib = Read(ent + 0x10, 4); if (ib != null && towers.Contains(BitConverter.ToUInt32(ib, 0))) continue; }
        var hb = Read(ba + 0x248, 16); if (hb == null) continue;
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
    /// <summary>The per-frame attack event log (src/AttackLog.cs): off unless the host sets it before Start (the test build only).</summary>
    public bool AttackLog;
    public Action<string> Log;
    public Action<uint, List<HeroSample>> OnSample;   // diagnostics
    readonly TimelineBuilder tl = new TimelineBuilder();
    readonly object gate = new object();
    readonly ManualResetEvent done = new ManualResetEvent(false);
    volatile bool stop, running;
    List<int[]> heroes;
    readonly Dictionary<ulong, uint> owners = new Dictionary<ulong, uint>();
    readonly RFX.ArenaRefs arenaRefs = new RFX.ArenaRefs();
    readonly Dictionary<ulong, RFX.UnitCache> unitCache = new Dictionary<ulong, RFX.UnitCache>();
    readonly HashSet<uint> towerSet = new HashSet<uint>();
    long tracePassMs; int tracePasses;
    Thread th, atkTh;
    RealmForge.AttackLog atkLog;

    public FightRecorder(ulong sim, int stage) { Sim = sim; Stage = stage; }
    /// <summary>An arena stage (attack 6001xxx, the defence run 6002xxx): its waves and bases are recorded too.</summary>
    public bool Arena { get { return Stage / 1000 == 6001 || Stage / 1000 == 6002; } }
    public bool Running { get { return running; } }
    public DateTime EndedUtc { get; private set; }

    public void Start() {
      running = true;
      th = new Thread(Loop) { IsBackground = true, Name = "fight-recorder" };
      th.Start();
      if (AttackLog) {
        atkLog = new RealmForge.AttackLog();
        tl.Atk = atkLog;
        atkTh = new Thread(AtkLoop) { IsBackground = true, Name = "fight-atk" };   // a light poller: only cached addresses, Sleep(5)
        atkTh.Start();
      }
    }
    public void Stop() { stop = true; }

    void Say(string s) { var l = Log; if (l != null) try { l(s); } catch (Exception) { } }

    void Commands() {
      var c = RFX.ReadFrameCommands(Sim, false);
      if (c.Count > 0) lock (gate) tl.AddCommands(c);
      var st = RFX.ReadStatsBySide(Sim);
      if (st.Count > 0) {
        uint f; int cs;
        lock (gate) { tl.SetStats(st); if (RFX.SimClock(Sim, out f, out cs)) tl.AddStatSample(f, st); }
      }
    }

    /// <summary>One pass of the arena's unit trace (timed).</summary>
    void UnitTrace(uint fr) {
      var sw = Stopwatch.StartNew();
      var us = RFX.ReadUnits(Sim, unitCache, towerSet);
      sw.Stop();
      tracePassMs += sw.ElapsedMilliseconds; tracePasses++;
      lock (gate) tl.AddUnitSample(fr, us);
    }

    /// <summary>The attack log's poller (read-only): about every few ms while the fight runs.</summary>
    void AtkLoop() {
      var refs = new RFX.AttackRefs(); var hl = new List<int[]>();
      var sw = Stopwatch.StartNew(); long tries = 0; int errors = 0;
      try {
        while (!stop && running) {
          try {
            if (hl.Count == 0) hl = RFX.BattleHeroList(Sim);
            uint f;
            RFX.ReadAttackState(Sim, hl, refs, atkLog, out f);
            if (refs.Off == null) { Say("attack log: no offsets for this game build (layout field section), off"); break; }
          } catch (Exception e) { if (errors++ < 3) Say("attack log: " + e.Message); }
          tries++;
          if ((tries & 127) == 0) atkLog.PollMs = sw.ElapsedMilliseconds / (double)tries;
          Thread.Sleep(5);
        }
      } finally {
        if (tries > 0) atkLog.PollMs = sw.ElapsedMilliseconds / (double)tries;
        Say("attack log: " + atkLog.Polls + " polls, " + atkLog.EventCount + " events, torn " + atkLog.Torn + ", " + atkLog.PollMs.ToString("0.0") + " ms/poll");
      }
    }

    void Loop() {
      uint lastFrame = 0; int n = 0, errors = 0;
      try {
        heroes = RFX.BattleHeroList(Sim);
        Say("fight recorder: " + heroes.Count + " heroes, stage " + Stage);
        while (!stop) {
          uint fr; int st;
          if (!RFX.SimClock(Sim, out fr, out st) || st != RFX.SimRunning) break;   // ended or gone
          // the clock going back or a fight of hours: the memory is no longer this fight's simulation
          if (fr < lastFrame || (DateTime.UtcNow - StartedUtc).TotalMinutes > 40) break;
          if (fr != lastFrame) {
            lastFrame = fr;
            try {
              if (heroes.Count == 0) heroes = RFX.BattleHeroList(Sim);
              var hs = RFX.ReadHeroSamples(Sim, heroes, null, owners);
              if (hs != null) {
                foreach (var h in hs) if (h.Tower != 0) towerSet.Add(h.Tower);
                lock (gate) tl.AddSample(fr, hs);
                var cb = OnSample; if (cb != null) cb(fr, hs);
              }
              if (Arena) {
                var a = RFX.ReadArena(Sim, arenaRefs, null);
                lock (gate) { if (a != null) tl.AddArena(fr, a); if (tl.GridW == 0 && RFX.TowerGridW > 0) tl.GridW = RFX.TowerGridW; }
              }
              if (Arena && n % 2 == 0) UnitTrace(fr);   // about every 0.4 s
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
        if (RFX.SimClock(Sim, out ef, out es) && (es == RFX.SimRunning || es == RFX.SimEnded)) {
          // the last look at the arena (the round's end and the bases as the fight ended)
          if (Arena) { var a = RFX.ReadArena(Sim, arenaRefs, null); if (a != null) lock (gate) tl.AddArena(ef, a); }
          lock (gate) { if (ef > tl.Frames) tl.Frames = ef; }
        }
        Commands();
        try {
          var sk = RFX.ReadSkillStats(Sim);
          if (sk != null) lock (gate) {
            bool ok; int n2 = tl.SetSkills(sk, out ok);
            Say("battle stats: " + n2 + " heroes with per-skill data, sum check " + (n2 == 0 ? "n/a" : ok ? "ok" : "mismatch"));
          }
        } catch (Exception) { }
        if (tracePasses > 0) lock (gate) Say("unit trace: " + tl.UnitCount + " units, avg " + (tracePassMs / (double)tracePasses).ToString("0.0") + " ms, " + tracePasses + " passes"
                                             + (tl.UnitsCut > 0 || tl.PointsCut > 0 ? ", cut " + tl.UnitsCut + " units / " + tl.PointsCut + " points" : ""));
        lock (gate) Say("fight recorder: ended at frame " + tl.Frames + ", " + tl.Samples + " samples, " + tl.Commands + " commands");
      } catch (Exception e) { Say("fight recorder: " + e.Message); }
      finally {
        running = false;
        var at = atkTh; if (at != null) try { at.Join(1000); } catch (Exception) { }
        EndedUtc = DateTime.UtcNow; done.Set();
      }
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
