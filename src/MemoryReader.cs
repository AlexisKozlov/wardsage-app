// RealmForge extractor - memory reader (READ-ONLY).
//
// This is the Lua-table scanner of extractor v0.4, verified on the live game, moved here as is.
// It only calls OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ), VirtualQueryEx and
// ReadProcessMemory: nothing is ever written to the game process.
//
// Changes compared to v0.4 (none of them touch the scanning logic):
//   * wrapped in the RealmForge namespace;
//   * L() also forwards each log line to OnLog (progress in the window);
//   * Run() records LastError / LastOpenError so the window can show a clear message;
//   * meta.extractor comes from ExtractorVersion ("0.5"); meta.gameVersion is added when known.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;

namespace RealmForge {
  public static class RFX {
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int a, bool b, int pid);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI m, IntPtr len);
    [StructLayout(LayoutKind.Sequential)] public struct MBI { public ulong Base; public ulong AllocBase; public uint AllocProt; public uint pad1; public ulong Size; public uint State; public uint Protect; public uint Type; public uint pad2; }
    static IntPtr H;
    public static StringBuilder Log = new StringBuilder();
    // --- v0.5 hooks (the scanning logic below is unchanged from v0.4) ---
    public const string ExtractorVersion = "0.5";
    public static Action<string> OnLog;        // called for every log line (from the worker thread)
    public static string GameVersion;          // written to meta.gameVersion when not null
    public static string LastError;            // "not_running" | "open_failed" | null
    public static int LastOpenError;           // GetLastWin32Error() of OpenProcess (5 = access denied)
    static void L(string s) { Log.AppendLine(s); Console.WriteLine(s); var h = OnLog; if (h != null) h(s); }
    class Region { public ulong Base; public ulong Size; public uint Protect; }
    static List<Region> regs;

    static List<Region> Regions() {
      var r = new List<Region>(); ulong a = 0; MBI m;
      while (a < 0x7FFFFFFFFFFFUL) {
        if (VirtualQueryEx(H, (IntPtr)(long)a, out m, (IntPtr)Marshal.SizeOf(typeof(MBI))) == IntPtr.Zero) break;
        bool rw = m.State == 0x1000 && (m.Protect == 0x04 || m.Protect == 0x40);
        if (rw && m.Size <= 0x40000000UL) r.Add(new Region { Base = m.Base, Size = m.Size, Protect = m.Protect });
        ulong next = m.Base + m.Size; if (next <= a) break; a = next;
      }
      return r;
    }
    static byte[] Read(ulong addr, int n) {
      if (n <= 0 || n > 64 * 1024 * 1024) return null;
      var b = new byte[n]; IntPtr rd;
      if (!ReadProcessMemory(H, (IntPtr)(long)addr, b, (IntPtr)n, out rd) || (long)rd != n) return null;
      return b;
    }
    delegate void ChunkFn(ulong baseAddr, byte[] buf, int len);
    static void Scan(ChunkFn f) {
      const int CH = 16 * 1024 * 1024;
      foreach (var r in regs)
        for (ulong off = 0; off < r.Size; off += CH) {
          int n = (int)Math.Min((ulong)CH, r.Size - off);
          var b = new byte[n]; IntPtr rd;
          if (!ReadProcessMemory(H, (IntPtr)(long)(r.Base + off), b, (IntPtr)n, out rd)) continue;
          f(r.Base + off, b, (int)(long)rd);
        }
    }

    // ---- Lua 5.3 (64-bit) layout ----
    const int T_NIL = 0, T_BOOL = 1, T_FLT = 3, T_INT = 0x13, T_SSTR = 0x44, T_LSTR = 0x54, T_TABLE = 0x45;

    // Find TString objects (short strings) for given names. Content at TS+24, tt at TS+8 == 4, shrlen at TS+11.
    static Dictionary<ulong, string> FindLuaStrings(string[] names) {
      var res = new Dictionary<ulong, string>();
      var pats = new List<byte[]>(); foreach (var s in names) pats.Add(Encoding.ASCII.GetBytes(s));
      Scan((b0, buf, len) => {
        for (int i = 24; i < len - 64; i++) {
          byte c = buf[i];
          for (int k = 0; k < pats.Count; k++) {
            var p = pats[k]; if (p[0] != c) continue;
            if (i + p.Length >= len || buf[i + p.Length] != 0) continue;
            bool ok = true; for (int j = 1; j < p.Length; j++) if (buf[i + j] != p[j]) { ok = false; break; }
            if (!ok) continue;
            int h = i - 24; if (buf[h + 8] != 4 || buf[h + 11] != p.Length) continue;
            res[b0 + (ulong)h] = names[k];
          }
        }
      });
      return res;
    }

    static string ReadLuaString(ulong ts) {
      var hb = Read(ts, 24); if (hb == null) return null;
      int tt = hb[8]; long len;
      if (tt == 4) len = hb[11]; else if (tt == 0x14) len = BitConverter.ToInt64(hb, 16); else return null;
      if (len < 0 || len > 4096) return null;
      var b = Read(ts + 24, (int)len); if (b == null) return null;
      return Encoding.UTF8.GetString(b);
    }

    // Parse Lua table into ordered dictionary: string keys -> values, int keys -> values
    static object TV(byte[] b, int o, int depth, HashSet<ulong> seen) {
      ulong v = BitConverter.ToUInt64(b, o); int tt = BitConverter.ToInt32(b, o + 8);
      switch (tt) {
        case T_NIL: return null;
        case T_BOOL: return v != 0;
        case T_INT: return (long)v;
        case T_FLT: return BitConverter.Int64BitsToDouble((long)v);
        case T_SSTR: case T_LSTR: return ReadLuaString(v);
        case T_TABLE: return depth > 0 ? (object)ParseTable(v, depth - 1, seen) : "<table>";
        default: return "<tt " + tt + ">";
      }
    }
    static Dictionary<string, object> ParseTable(ulong t, int depth, HashSet<ulong> seen) {
      if (seen.Contains(t)) return null; seen.Add(t);
      var h = Read(t, 56); if (h == null || h[8] != 5) return null;
      int lsize = h[11]; uint sizearray = BitConverter.ToUInt32(h, 12);
      ulong arr = BitConverter.ToUInt64(h, 16), node = BitConverter.ToUInt64(h, 24);
      if (lsize > 16 || sizearray > 100000) return null;
      var d = new Dictionary<string, object>();
      if (sizearray > 0) {
        var ab = Read(arr, (int)sizearray * 16);
        if (ab != null) for (int i = 0; i < sizearray; i++) { var x = TV(ab, i * 16, depth, seen); if (x != null) d["[" + (i + 1) + "]"] = x; }
      }
      int nn = 1 << lsize; var nb = Read(node, nn * 32);
      if (nb != null) for (int i = 0; i < nn; i++) {
        int o = i * 32; int ktt = BitConverter.ToInt32(nb, o + 24); if (ktt == T_NIL) continue;
        string key;
        if (ktt == T_SSTR || ktt == T_LSTR) key = ReadLuaString(BitConverter.ToUInt64(nb, o + 16));
        else if (ktt == T_INT) key = "[" + BitConverter.ToInt64(nb, o + 16) + "]";
        else continue;
        if (key == null) continue;
        var x = TV(nb, o, depth, seen); if (x != null) d[key] = x;
      }
      return d;
    }

    // Return raw TValue (value, tt) of a string-keyed field in a Lua table
    static bool Field(ulong t, string key, out ulong val, out int tt) {
      val = 0; tt = 0;
      var h = Read(t, 56); if (h == null || h[8] != 5) return false;
      int lsize = h[11]; if (lsize > 20) return false;
      ulong node = BitConverter.ToUInt64(h, 24); int nn = 1 << lsize; var nb = Read(node, nn * 32); if (nb == null) return false;
      for (int i = 0; i < nn; i++) {
        int o = i * 32; int ktt = BitConverter.ToInt32(nb, o + 24);
        if (ktt != T_SSTR) continue;
        if (ReadLuaString(BitConverter.ToUInt64(nb, o + 16)) != key) continue;
        val = BitConverter.ToUInt64(nb, o); tt = BitConverter.ToInt32(nb, o + 8); return true;
      }
      return false;
    }
    // Among tables owning key, take the field whose parsed table is largest
    static Dictionary<string, object> BestField(Dictionary<ulong, string> tstr, string key, int depth) {
      Dictionary<string, object> best = null;
      foreach (var t in TablesWithKeys(tstr, key)) {
        ulong v; int tt; if (!Field(t, key, out v, out tt) || tt != T_TABLE) continue;
        var d = ParseTable(v, depth, new HashSet<ulong>());
        if (d != null && (best == null || d.Count > best.Count)) best = d;
      }
      return best;
    }

    static void J(StringBuilder sb, object o) {
      if (o == null) { sb.Append("null"); return; }
      if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
      if (o is long) { sb.Append(((long)o).ToString(CultureInfo.InvariantCulture)); return; }
      if (o is double) { var dd = (double)o; if (double.IsNaN(dd) || double.IsInfinity(dd)) sb.Append("null"); else sb.Append(dd.ToString("R", CultureInfo.InvariantCulture)); return; }
      if (o is string) { sb.Append('"'); foreach (char c in (string)o) { if (c == '"' || c == '\\') { sb.Append('\\'); sb.Append(c); } else if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4")); else sb.Append(c); } sb.Append('"'); return; }
      var d = o as Dictionary<string, object>;
      if (d != null) { sb.Append('{'); bool f = true; foreach (var kv in d) { if (!f) sb.Append(','); f = false; J(sb, kv.Key); sb.Append(':'); J(sb, kv.Value); } sb.Append('}'); return; }
      sb.Append("null");
    }

    // Collect tables that own a node whose key is one of the given TStrings
    static List<ulong> TablesWithKeys(Dictionary<ulong, string> tstr, string anchor) {
      var anchorTs = new HashSet<ulong>(); foreach (var kv in tstr) if (kv.Value == anchor) anchorTs.Add(kv.Key);
      var nodes = new List<ulong>();
      Scan((b0, buf, len) => {
        for (int i = 16; i + 16 <= len; i += 8) {
          ulong v = BitConverter.ToUInt64(buf, i);
          if (!anchorTs.Contains(v)) continue;
          if (BitConverter.ToInt32(buf, i + 8) != T_SSTR) continue;
          nodes.Add(b0 + (ulong)i - 16);
        }
      });
      L("  nodes keyed '" + anchor + "': " + nodes.Count);
      var starts = new Dictionary<ulong, ulong>(); // candidate node-array start -> hit node
      foreach (var n in nodes) for (int i = 0; i < 64; i++) { ulong s = n - (ulong)(32 * i); if (!starts.ContainsKey(s)) starts[s] = n; }
      var tables = new List<ulong>();
      Scan((b0, buf, len) => {
        for (int i = 0; i + 56 <= len; i += 8) {
          if (buf[i + 8] != 5) continue;
          ulong np = BitConverter.ToUInt64(buf, i + 24);
          ulong hit; if (!starts.TryGetValue(np, out hit)) continue;
          int lsize = buf[i + 11]; if (lsize > 12) continue;
          if (hit >= np + (ulong)(32 << lsize)) continue;
          tables.Add(b0 + (ulong)i);
        }
      });
      L("  tables: " + tables.Count);
      return tables;
    }

    // Find Lua tables that hold references (TValue tt=table) to the given target tables; returns container -> targets
    static Dictionary<ulong, List<ulong>> Containers(HashSet<ulong> targets) {
      var hitAt = new Dictionary<ulong, ulong>();
      Scan((b0, buf, len) => {
        for (int i = 0; i + 16 <= len; i += 8) {
          ulong v = BitConverter.ToUInt64(buf, i);
          if (!targets.Contains(v) || BitConverter.ToInt32(buf, i + 8) != T_TABLE) continue;
          hitAt[b0 + (ulong)i] = v;
        }
      });
      var hits = new List<ulong>(hitAt.Keys); hits.Sort(); var ha = hits.ToArray();
      L("  references: " + ha.Length);
      var res = new Dictionary<ulong, List<ulong>>();
      Scan((b0, buf, len) => {
        for (int i = 0; i + 56 <= len; i += 8) {
          if (buf[i + 8] != 5) continue;
          int lsize = buf[i + 11]; if (lsize > 20) continue;
          uint sa = BitConverter.ToUInt32(buf, i + 12); if (sa > 1000000) continue;
          ulong arr = BitConverter.ToUInt64(buf, i + 16), node = BitConverter.ToUInt64(buf, i + 24);
          List<ulong> got = null;
          if (sa > 0) got = Collect(ha, hitAt, arr, arr + (ulong)sa * 16, 16, got);
          got = Collect(ha, hitAt, node, node + ((ulong)32 << lsize), 32, got);
          if (got != null && got.Count >= 3) res[b0 + (ulong)i] = got;
        }
      });
      return res;
    }
    static List<ulong> Collect(ulong[] ha, Dictionary<ulong, ulong> hitAt, ulong from, ulong to, int stride, List<ulong> got) {
      if (to <= from) return got;
      int idx = Array.BinarySearch(ha, from); if (idx < 0) idx = ~idx;
      for (; idx < ha.Length && ha[idx] < to; idx++) {
        if ((ha[idx] - from) % (ulong)stride != 0) continue;
        if (got == null) got = new List<ulong>(); got.Add(hitAt[ha[idx]]);
      }
      return got;
    }
    static ulong Best(Dictionary<ulong, List<ulong>> c, string what) {
      var keys = new List<ulong>(c.Keys); keys.Sort((a, b) => c[b].Count.CompareTo(c[a].Count));
      for (int i = 0; i < Math.Min(5, keys.Count); i++) L("  " + what + " container #" + (i + 1) + ": " + c[keys[i]].Count + " entries");
      return keys.Count > 0 ? keys[0] : 0;
    }

    public static string Run() {
      LastError = null; LastOpenError = 0;
      var ps = Process.GetProcessesByName("Watcher of Realms");
      if (ps.Length == 0) { LastError = "not_running"; L("ERROR: game not running."); return null; }
      H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) { LastOpenError = Marshal.GetLastWin32Error(); LastError = "open_failed"; L("ERROR: cannot open game process (code " + LastOpenError + "). Run as administrator."); return null; }
      var sw = Stopwatch.StartNew();
      regs = Regions(); ulong total = 0; foreach (var r in regs) total += r.Size;
      L("RW regions: " + regs.Count + ", " + (total >> 20) + " MB");
      string[] names = { "iStarLvl", "iItemUid", "iBaseId", "vEquipSlot", "iStarLevel", "m_CampHeroPerfectReward", "m_vArtifacts" };
      var tstr = FindLuaStrings(names);
      foreach (var kv in tstr) L("  lua string '" + kv.Value + "' @ 0x" + kv.Key.ToString("X"));
      L("Strings found in " + (int)sw.Elapsed.TotalSeconds + " s");

      var sb = new StringBuilder(); sb.Append("{\n\"equipment\":[\n");
      L("Equipment tables...");
      var itemT = new Dictionary<ulong, Dictionary<string, object>>();
      foreach (var t in TablesWithKeys(tstr, "iStarLvl")) {
        var d = ParseTable(t, 3, new HashSet<ulong>());
        if (d == null || !(d.ContainsKey("iItemUid") && d["iItemUid"] is long && (long)d["iItemUid"] > 0) || d.ContainsKey("vConfig")) continue;
        itemT[t] = d;
      }
      L("  item tables: " + itemT.Count);
      var bag = Containers(new HashSet<ulong>(itemT.Keys)); ulong bagT = Best(bag, "equipment");
      int ne = 0; var seenUid = new HashSet<long>();
      if (bagT != 0) foreach (var t in bag[bagT]) { var d = itemT[t]; if (!seenUid.Add((long)d["iItemUid"])) continue; if (ne++ > 0) sb.Append(",\n"); J(sb, d); }
      L("  equipment items (owned): " + ne);
      sb.Append("\n],\n\"heroes\":[\n");
      L("Hero tables...");
      var heroT = new Dictionary<ulong, Dictionary<string, object>>();
      foreach (var t in TablesWithKeys(tstr, "vEquipSlot")) {
        var d = ParseTable(t, 2, new HashSet<ulong>());
        if (d == null || !(d.ContainsKey("iHeroId") && d["iHeroId"] is long && (long)d["iHeroId"] > 0) || !d.ContainsKey("iBaseId")) continue;
        heroT[t] = d;
      }
      L("  hero tables: " + heroT.Count);
      var roster = Containers(new HashSet<ulong>(heroT.Keys)); ulong rosT = Best(roster, "hero");
      int nh = 0; var seenH = new HashSet<long>();
      if (rosT != 0) foreach (var t in roster[rosT]) { var d = heroT[t]; if (!seenH.Add((long)d["iHeroId"])) continue; if (nh++ > 0) sb.Append(",\n"); J(sb, d); }
      L("  heroes (owned): " + nh);
      L("Faction rewards...");
      var camp = BestField(tstr, "m_CampHeroPerfectReward", 1);
      L("  reward events: " + (camp == null ? 0 : camp.Count));
      sb.Append("\n],\n\"campRewards\":"); J(sb, camp);
      L("Artifacts...");
      var arts = BestField(tstr, "m_vArtifacts", 5);
      L("  artifacts: " + (arts == null ? 0 : arts.Count));
      sb.Append(",\n\"artifacts\":"); J(sb, arts);
      sb.Append("\n,\"meta\":{\"extractor\":\"" + ExtractorVersion + "\"");
      if (GameVersion != null) { sb.Append(",\"gameVersion\":"); J(sb, GameVersion); }
      sb.Append(",\"seconds\":" + (int)sw.Elapsed.TotalSeconds + ",\"equipment\":" + ne + ",\"heroes\":" + nh + "}\n}\n");
      L("Done in " + (int)sw.Elapsed.TotalSeconds + " s");
      return sb.ToString();
    }
  }
}
