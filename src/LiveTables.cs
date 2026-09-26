// RealmForge extractor - the game's live Lua tables, found once per game session (READ-ONLY).
//
// The account and the equip helper both need a few tables of the game's Lua state: EquipData (items: equips, the
// hero on the gear screen), HeroData (heroes: m_CharactorDatas), the tables holding the artifacts and the faction
// rewards, and the hero screen (Form_CharactorMain, its gear list panel). They are created at login and live as long as
// the game runs (their contents change, the tables do not move), so the slow part - finding them in ~2 GB of memory -
// is done once: afterwards a sync reads them directly (a second or two instead of a minute and more).
//
// The search itself: short Lua strings are interned and 8-byte aligned (TString: tt at +8, length at +11, text at +24),
// so the key names are found by checking aligned positions only; then one pass finds the table nodes keyed by any of
// them and one more the tables owning those nodes. The passes run on all processor cores.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RealmForge {
  public sealed class LiveTables {
    public int Pid;
    public ulong EquipData, HeroData, CampOwner, ArtOwner, Form, Panel;
  }

  public static partial class RFX {
    static LiveTables live;
    const string KCamp = "m_CampHeroPerfectReward", KArts = "m_vArtifacts", KHeroes = "m_CharactorDatas";

    /// <summary>The live tables of the running game: the ones found before when they are still there, else searched for
    /// (again). <paramref name="needForm"/>: also the hero screen (created when the player first opens it).</summary>
    internal static LiveTables EnsureLive(int pid, bool needForm) {
      var lt = live;
      if (lt != null && lt.Pid == pid && HasTable(lt.EquipData, "equips") && HasTable(lt.HeroData, KHeroes)
          && (!needForm || HasTable(lt.Form, "m_InfinityGridProxy"))) return lt;
      var sw = Stopwatch.StartNew();
      lt = FindLive(pid);
      L("Live tables found in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s: equip " + (lt.EquipData != 0) + ", heroes " + (lt.HeroData != 0)
        + ", artifacts " + (lt.ArtOwner != 0) + ", rewards " + (lt.CampOwner != 0) + ", hero screen " + (lt.Form != 0));
      live = lt;
      return lt;
    }

    static bool HasTable(ulong t, string key) { ulong v; int tt; return t != 0 && Field(t, key, out v, out tt) && tt == T_TABLE; }

    static LiveTables FindLive(int pid) {
      regs = Regions();
      string[] anchors = { KHero, KHeroes, KCamp, KArts, KGrid, KPanel };
      var tstr = FindLuaStringsFast(anchors);
      var found = new List<string>(); foreach (var kv in tstr) found.Add(kv.Value + "@" + kv.Key.ToString("X"));
      L("Strings found: " + string.Join(", ", found.ToArray()));
      var owners = OwnersOf(tstr);
      var lt = new LiveTables { Pid = pid };
      List<ulong> l;
      // EquipData: the owner of m_CurrentSelectHeroUid with the item map (UI panels own that key too)
      if (owners.TryGetValue(KHero, out l)) {
        foreach (var t in l) if (HasTable(t, "equips") && HasTable(t, "m_EquipFilterConfigs")) { lt.EquipData = t; break; }
        if (lt.EquipData == 0) foreach (var t in l) if (HasTable(t, "equips")) { lt.EquipData = t; break; }
      }
      lt.HeroData = Largest(owners, KHeroes);
      lt.CampOwner = Largest(owners, KCamp);
      lt.ArtOwner = Largest(owners, KArts);
      if (owners.TryGetValue(KGrid, out l)) foreach (var t in l) if (HasTable(t, "m_InfinityGridProxy")) { lt.Form = t; break; }
      if (owners.TryGetValue(KPanel, out l)) foreach (var t in l) if (HasTable(t, KList) && HasTable(t, "m_Form")) { lt.Panel = t; break; }
      return lt;
    }

    // the owner of the key whose value (a table) has the most entries
    static ulong Largest(Dictionary<string, List<ulong>> owners, string key) {
      List<ulong> l; if (!owners.TryGetValue(key, out l)) return 0;
      ulong best = 0; int bestN = -1;
      foreach (var t in l) {
        ulong v; int tt; if (!Field(t, key, out v, out tt) || tt != T_TABLE) continue;
        int n = EntryCount(v);
        if (n > bestN) { bestN = n; best = t; }
      }
      return best;
    }

    static int EntryCount(ulong t) {
      var h = Read(t, 56); if (h == null || h[8] != 5) return 0;
      int lsize = h[11]; uint sizearray = BitConverter.ToUInt32(h, 12);
      if (lsize > 20 || sizearray > 1000000) return 0;
      int n = 0;
      if (sizearray > 0) { var ab = Read(BitConverter.ToUInt64(h, 16), (int)sizearray * 16); if (ab != null) for (int i = 0; i < sizearray; i++) if (BitConverter.ToInt32(ab, i * 16 + 8) != T_NIL) n++; }
      int nn = 1 << lsize; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32);
      if (nb != null) for (int i = 0; i < nn; i++) if (BitConverter.ToInt32(nb, i * 32 + 8) != T_NIL && BitConverter.ToInt32(nb, i * 32 + 24) != T_NIL) n++;
      return n;
    }

    // ------------------------------------------------------------------ parallel passes

    /// <summary>Calls <paramref name="f"/> for every 16 MB chunk of the game's writable memory, on all cores. The callback
    /// must be thread-safe.</summary>
    static void ScanParallel(ChunkFn f) {
      const int CH = 16 * 1024 * 1024;
      var chunks = new List<KeyValuePair<ulong, int>>();
      foreach (var r in regs) for (ulong off = 0; off < r.Size; off += CH) chunks.Add(new KeyValuePair<ulong, int>(r.Base + off, (int)Math.Min((ulong)CH, r.Size - off)));
      Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
        () => new byte[CH],
        (c, state, buf) => {
          IntPtr rd;
          if (ReadProcessMemory(H, (IntPtr)(long)c.Key, buf, (IntPtr)c.Value, out rd) && (long)rd > 0) f(c.Key, buf, (int)(long)rd);
          return buf;
        },
        buf => { });
    }

    /// <summary>Short Lua strings with these texts (TString address -> text): aligned positions only.</summary>
    static Dictionary<ulong, string> FindLuaStringsFast(string[] names) {
      var res = new Dictionary<ulong, string>();
      var byLen = new Dictionary<int, List<KeyValuePair<byte[], string>>>();
      foreach (var s in names) {
        var p = Encoding.ASCII.GetBytes(s);
        List<KeyValuePair<byte[], string>> l; if (!byLen.TryGetValue(p.Length, out l)) byLen[p.Length] = l = new List<KeyValuePair<byte[], string>>();
        l.Add(new KeyValuePair<byte[], string>(p, s));
      }
      ScanParallel((b0, buf, len) => {
        for (int i = 24; i + 48 < len; i += 8) {
          if (buf[i - 16] != 4) continue;                      // tt: short string
          List<KeyValuePair<byte[], string>> l;
          if (!byLen.TryGetValue(buf[i - 13], out l)) continue; // length
          foreach (var kv in l) {
            var p = kv.Key; if (buf[i] != p[0] || buf[i + p.Length] != 0) continue;
            bool ok = true; for (int j = 1; j < p.Length; j++) if (buf[i + j] != p[j]) { ok = false; break; }
            if (ok) lock (res) res[b0 + (ulong)i - 24] = kv.Value;
          }
        }
      });
      return res;
    }

    /// <summary>For every text: the tables that have a field with that name (two passes for all of them).</summary>
    static Dictionary<string, List<ulong>> OwnersOf(Dictionary<ulong, string> tstr) { return OwnersOf(tstr, 4096); }

    static Dictionary<string, List<ulong>> OwnersOf(Dictionary<ulong, string> tstr, int window) {
      var anchorTs = new HashSet<ulong>(tstr.Keys);
      var nodes = new List<KeyValuePair<ulong, string>>();
      ScanParallel((b0, buf, len) => {
        for (int i = 16; i + 16 <= len; i += 8) {
          if (buf[i + 8] != T_SSTR || buf[i + 9] != 0) continue;   // node key tt (the int is 0x44)
          ulong v = BitConverter.ToUInt64(buf, i);
          if (!anchorTs.Contains(v) || BitConverter.ToInt32(buf, i + 8) != T_SSTR) continue;
          lock (nodes) nodes.Add(new KeyValuePair<ulong, string>(b0 + (ulong)i - 16, tstr[v]));
        }
      });
      // the node array can start up to 4096 nodes before the key's node (big manager tables have hundreds of fields);
      // one table can own several of the keys (HeroData: the heroes and the faction rewards)
      var starts = new Dictionary<ulong, List<KeyValuePair<ulong, string>>>();
      foreach (var n in nodes) for (int i = 0; i < window; i++) {
        ulong s = n.Key - (ulong)(32 * i);
        List<KeyValuePair<ulong, string>> sl; if (!starts.TryGetValue(s, out sl)) starts[s] = sl = new List<KeyValuePair<ulong, string>>();
        sl.Add(n);
      }
      L("  nodes: " + nodes.Count);
      var res = new Dictionary<string, List<ulong>>();
      ScanParallel((b0, buf, len) => {
        for (int i = 0; i + 56 <= len; i += 8) {
          if (buf[i + 8] != 5) continue;
          ulong np = BitConverter.ToUInt64(buf, i + 24);
          List<KeyValuePair<ulong, string>> hits; if (!starts.TryGetValue(np, out hits)) continue;
          int lsize = buf[i + 11]; if (lsize > 12) continue;
          foreach (var hit in hits) {
            if (hit.Key >= np + (ulong)(32 << lsize)) continue;
            lock (res) { List<ulong> l; if (!res.TryGetValue(hit.Value, out l)) res[hit.Value] = l = new List<ulong>(); if (!l.Contains(b0 + (ulong)i)) l.Add(b0 + (ulong)i); }
          }
        }
      });
      foreach (var kv in res) L("  tables with '" + kv.Key + "': " + kv.Value.Count);
      return res;
    }

    // ------------------------------------------------------------------ values of the sub stats not revealed yet

    // The live item tables have 0 for the sub stats an item has not revealed yet (they open at +4/+8/+12/+16), and so has
    // the server's copy of an item sent after a change; the copies sent at login have the real values, which the site
    // shows dimmed («с заточки +N»). They are collected once per game session: uid -> attribute pool id -> value.
    static Dictionary<long, Dictionary<long, long>> subValues;
    static int subValuesPid;

    static void CollectSubValues(int pid) {
      if (subValues != null && subValuesPid == pid) return;
      var sw = Stopwatch.StartNew();
      var res = new Dictionary<long, Dictionary<long, long>>();
      var tstr = FindLuaStringsFast(new[] { "iStarLvl" });
      var owners = OwnersOf(tstr, 64);
      List<ulong> tables;
      if (owners.TryGetValue("iStarLvl", out tables))
        foreach (var t in tables) {
          ulong v; int tt;
          if (Field(t, "vConfig", out v, out tt)) continue;   // a live table: 0 for hidden values
          var d = ParseTable(t, 3, new HashSet<ulong>());
          long uid = d != null ? LNum(d, "iItemUid") : 0; if (uid <= 0) continue;
          object o; var list = d.TryGetValue("vViceAttrList", out o) ? o as Dictionary<string, object> : null; if (list == null) continue;
          foreach (var kv in list) {
            var a = kv.Value as Dictionary<string, object>; if (a == null) continue;
            long pool = LNum(a, "iAttrId"), val = LNum(a, "iValue"); if (pool <= 0 || val == 0) continue;
            Dictionary<long, long> m; if (!res.TryGetValue(uid, out m)) res[uid] = m = new Dictionary<long, long>();
            m[pool] = val;
          }
        }
      subValues = res; subValuesPid = pid;
      L("Sub stat values of " + res.Count + " items in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
    }

    // ------------------------------------------------------------------ the account from the live tables

    /// <summary>account.json from the live tables; null when they are not all there (the caller then does the full scan).</summary>
    static string AccountFromLive(LiveTables lt, Stopwatch sw) {
      ulong equips, heroes; int t1, t2;
      if (!Field(lt.EquipData, "equips", out equips, out t1) || t1 != T_TABLE) return null;
      if (!Field(lt.HeroData, KHeroes, out heroes, out t2) || t2 != T_TABLE) return null;
      var sb = new StringBuilder(); sb.Append("{\n\"equipment\":[\n");
      try { CollectSubValues(lt.Pid); } catch (Exception e) { L("Sub stat values: " + e.Message); }
      int ne = 0; var seen = new HashSet<long>();
      string[] cfg = { "vConfig", "vItemConfig", "vSuitConfig" };
      foreach (var v in TableValues(equips)) {
        var skip = new HashSet<ulong>();
        foreach (var k in cfg) { ulong fv; int ft; if (Field(v, k, out fv, out ft) && ft == T_TABLE) skip.Add(fv); }
        var d = ParseTable(v, 3, skip);
        var raw = ServerForm(d);
        if (raw == null || !seen.Add((long)raw["iItemUid"])) continue;
        if (ne++ > 0) sb.Append(",\n"); J(sb, raw);
      }
      L("  equipment items (owned): " + ne);
      sb.Append("\n],\n\"heroes\":[\n");
      int nh = 0; var seenH = new HashSet<long>();
      foreach (var v in TableValues(heroes)) {
        var d = ParseTable(v, 2, new HashSet<ulong>());
        object u; if (d == null || !d.TryGetValue("iHeroId", out u) || !(u is long) || (long)u <= 0 || !d.ContainsKey("iBaseId") || !d.ContainsKey("vEquipSlot")) continue;
        if (!seenH.Add((long)u)) continue;
        if (nh++ > 0) sb.Append(",\n"); J(sb, d);
      }
      L("  heroes (owned): " + nh);
      Dictionary<string, object> camp = null, arts = null;
      ulong cv, av; int ct, at;
      if (lt.CampOwner != 0 && Field(lt.CampOwner, KCamp, out cv, out ct) && ct == T_TABLE) camp = ParseTable(cv, 1, new HashSet<ulong>());
      if (lt.ArtOwner != 0 && Field(lt.ArtOwner, KArts, out av, out at) && at == T_TABLE) arts = ParseTable(av, 5, new HashSet<ulong>());
      L("  reward events: " + (camp == null ? 0 : camp.Count) + ", artifacts: " + (arts == null ? 0 : arts.Count));
      sb.Append("\n],\n\"campRewards\":"); J(sb, camp);
      sb.Append(",\n\"artifacts\":"); J(sb, arts);
      sb.Append("\n,\"meta\":{\"extractor\":\"" + ExtractorVersion + "\"");
      if (GameVersion != null) { sb.Append(",\"gameVersion\":"); J(sb, GameVersion); }
      sb.Append(",\"seconds\":" + (int)sw.Elapsed.TotalSeconds + ",\"equipment\":" + ne + ",\"heroes\":" + nh + ",\"live\":true}\n}\n");
      L("Done in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
      return sb.ToString();
    }

    /// <summary>An item of EquipData.equips (the game's reworked form, EquipData:_CreateEquip) back in the server's form,
    /// the one the site reads: iLevel for iIntensifyLvl, the attribute pool ids (iId) as iAttrId.</summary>
    internal static Dictionary<string, object> ServerForm(Dictionary<string, object> d) {
      if (d == null) return null;
      object u; if (!d.TryGetValue("iItemUid", out u) || !(u is long) || (long)u <= 0) return null;
      var r = new Dictionary<string, object>();
      r["iItemUid"] = u;
      foreach (var k in new[] { "iItemId", "iStarLvl", "iHeroId", "bLocked", "iExtraMasterAttrValue", "iIntensifyExp", "iVaryEffectId", "iExclusiveEffectId" }) {
        object v; if (d.TryGetValue(k, out v) && v != null) r[k] = v;
      }
      object lv; r["iLevel"] = d.TryGetValue("iIntensifyLvl", out lv) ? lv : 0L;
      if (!r.ContainsKey("iHeroId")) r["iHeroId"] = 0L;
      r["vMasterAttrList"] = PoolAttrs(d, "vMasterAttrList");
      r["vViceAttrList"] = PoolAttrs(d, "vViceAttrList");
      // hidden sub stats: the value the server sent at login
      Dictionary<long, long> known;
      var sv = subValues;
      if (sv != null && sv.TryGetValue((long)u, out known))
        foreach (var e in ((Dictionary<string, object>)r["vViceAttrList"]).Values) {
          var a = e as Dictionary<string, object>; long pool = LNum(a, "iAttrId"), kvv;
          if (pool > 0 && LNum(a, "iValue") == 0 && known.TryGetValue(pool, out kvv)) a["iValue"] = kvv;
        }
      object mc; if (d.TryGetValue("vViceAttrMilepostCnt", out mc) && mc is Dictionary<string, object>) r["vViceAttrMilepostCnt"] = mc;
      return r;
    }

    static Dictionary<string, object> PoolAttrs(Dictionary<string, object> d, string key) {
      var r = new Dictionary<string, object>();
      object o; var list = d.TryGetValue(key, out o) ? o as Dictionary<string, object> : null;
      if (list == null) return r;
      foreach (var kv in list) {
        var a = kv.Value as Dictionary<string, object>; if (a == null) continue;
        object id, val;
        if (!a.TryGetValue("iId", out id)) continue;
        var e = new Dictionary<string, object>(); e["iAttrId"] = id; if (a.TryGetValue("iValue", out val)) e["iValue"] = val;
        r[kv.Key] = e;
      }
      return r;
    }
  }
}
