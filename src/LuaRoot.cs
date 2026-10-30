// Wardsage — the script state's tables reached by pointers from its root (read-only), no memory scan.
//
// il2cpp: LuaManager (no namespace) -> Il2CppClass.static_fields 0xB8 -> m_luaEnv (static 0x0, XLua.LuaEnv) -> rawL 0x10
// (lua_State*). Lua 5.3.5 (xlua.dll, 64-bit): lua_State.l_G 0x18 -> global_State.l_registry 0x40 (a TValue) -> the
// registry's array part [LUA_RIDX_GLOBALS = 2] = _G (checked: _G._G == _G). From _G:
//   UIInstance = the upvalue of the Lua function GetUIInstanceLua (UI/Common/UIStatic.lua: `local UIInstance = {}`);
//   Managers   = the upvalue of mtEquipData (common/Managers.lua: `local Managers = {}`): EquipData, HeroData, ...
// LClosure: tt 0x8 (6), nupvalues 0xA, p 0x18, upvals[] 0x20; UpVal.v 0x0 -> TValue; Proto.sizeupvalues 0x10,
// Proto.upvalues 0x58 (Upvaldesc 16 bytes, name 0x0). Test build: run next to the scans and logged (luaroot: ...);
// LuaRootFirst lets the window table be taken from here before the scan.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RealmForge {
  /// <summary>What a walk from the script state's root found; <see cref="FailStep"/> null = all of it.</summary>
  public sealed class LuaRootInfo {
    public ulong Klass, Env, RawL, Global, Registry, G, Ui, Managers;
    public int GNodes, UiForms, ManagerCount;
    public readonly List<string> FormNames = new List<string>();
    /// <summary>The manager tables found in Managers by name (EquipData, HeroData, ArtifactData, ItemData, PlayerData).</summary>
    public readonly Dictionary<string, ulong> Mgr = new Dictionary<string, ulong>(StringComparer.Ordinal);
    public string FailStep, FailValue;
    public long Ms;
    public bool Ok { get { return FailStep == null; } }
    internal LuaRootInfo Fail(string step, string value) { FailStep = step; FailValue = value; return this; }
  }

  public static partial class RFX {
    const int T_LCL = 0x46, T_THREAD = 0x48;
    /// <summary>The managers the walk looks up in Managers.</summary>
    public static readonly string[] RootManagers = { "EquipData", "HeroData", "ArtifactData", "ItemData", "PlayerData" };
    /// <summary>The window table (UIInstance) is taken from the script state's root before the scan looks for it (test build on,
    /// release off).</summary>
    public static bool LuaRootFirst = Channel.IsTest;
    /// <summary>For the tests: GameAssembly.dll's base in the stand-in memory (no game process there).</summary>
    public static ulong LuaRootGaForTest;


    static string HexA(ulong v) { return v.ToString("X"); }

    /// <summary>The walk from the class LuaManager of the game whose GameAssembly.dll is at <paramref name="ga"/>. A class not
    /// known yet is asked for (a background scan of GameAssembly.dll's data, as for the battle classes).</summary>
    public static LuaRootInfo LuaRootWalk(ulong ga) {
      var sw = Stopwatch.StartNew();
      var r = new LuaRootInfo();
      try {
        if (ga == 0) return r.Fail("GameAssembly.dll", "0");
        ulong k = ClassAt(ga, "LuaManager");
        if (k == 0) {
          var ly = Layout.Current;
          // ClassAt asks for a scan only for a layout of another build; this class has no address in the layout data yet
          if (ly != null && !ly.RvaDropped && ly.Rva("LuaManager") == 0) RequestScan(ga, "LuaManager");
          return r.Fail("LuaManager class", "not known yet");
        }
        LuaRootFromClass(k, r);
      } catch (Exception e) { r.Fail("exception", e.Message); }
      finally { r.Ms = sw.ElapsedMilliseconds; }
      return r;
    }

    /// <summary>The walk from the Il2CppClass of LuaManager at <paramref name="k"/> (filled into <paramref name="r"/>).</summary>
    public static LuaRootInfo LuaRootFromClass(ulong k, LuaRootInfo r) {
      r.Klass = k;
      ulong sf = Ptr(k + 0xB8); if (sf == 0) return r.Fail("static_fields", "0");
      ulong env = Ptr(sf); if (env == 0) return r.Fail("m_luaEnv", "0");
      string en = ClassName(Ptr(env)); if (en != "XLua.LuaEnv") return r.Fail("m_luaEnv class", en ?? "unreadable");
      r.Env = env;
      ulong L = Ptr(env + 0x10); if (L == 0) return r.Fail("rawL", "0");
      return LuaRootFromState(L, r);
    }

    /// <summary>The Lua part of the walk, from the lua_State at <paramref name="L"/>.</summary>
    public static LuaRootInfo LuaRootFromState(ulong L, LuaRootInfo r) {
      r.RawL = L;
      var lb = Read(L, 0x20); if (lb == null) return r.Fail("lua_State", "unreadable " + HexA(L));
      if (lb[8] != 8) return r.Fail("lua_State", "tt " + lb[8]);
      ulong g = BitConverter.ToUInt64(lb, 0x18); r.Global = g;
      var gb = g != 0 ? Read(g + 0x40, 16) : null; if (gb == null) return r.Fail("l_G", "unreadable " + HexA(g));
      if (BitConverter.ToInt32(gb, 8) != T_TABLE) return r.Fail("l_registry", "tt " + BitConverter.ToInt32(gb, 8));
      ulong reg = BitConverter.ToUInt64(gb, 0); r.Registry = reg;
      var rh = Read(reg, 56); if (rh == null || rh[8] != 5) return r.Fail("registry", "not a table " + HexA(reg));
      // the array part is big (luaL_ref fills it: 16384 live); only [1] (main thread) and [2] (_G) are read
      uint sa = BitConverter.ToUInt32(rh, 12); ulong ap = BitConverter.ToUInt64(rh, 16);
      if (sa < 2) return r.Fail("registry", "array " + sa + " at " + HexA(ap));
      int na = sa >= 3 ? 3 : 2;
      var ra = ap != 0 ? Read(ap, 16 * na) : null; if (ra == null) return r.Fail("registry", "array " + sa + " at " + HexA(ap) + " unreadable");
      if (BitConverter.ToInt32(ra, 8) != T_THREAD || BitConverter.ToUInt64(ra, 0) != L) return r.Fail("registry[1]", "not the main thread; " + RegSlots(ra, na, sa, ap));
      if (BitConverter.ToInt32(ra, 24) != T_TABLE) return r.Fail("registry[2]", "not a table; " + RegSlots(ra, na, sa, ap));
      ulong G = BitConverter.ToUInt64(ra, 16);
      ulong v; int tt;
      if (!Field(G, "_G", out v, out tt) || tt != T_TABLE || v != G) return r.Fail("_G._G", (tt == T_TABLE ? HexA(v) : "tt " + tt) + " vs _G " + HexA(G) + "; " + RegSlots(ra, na, sa, ap));
      r.G = G; r.GNodes = UsedNodes(G);
      r.Ui = UpvalueTable(G, "GetUIInstanceLua", "UIInstance", r); if (r.Ui == 0) return r;
      FormsOf(r.Ui, r);
      r.Managers = UpvalueTable(G, "mtEquipData", "Managers", r); if (r.Managers == 0) return r;
      r.ManagerCount = TableValues(r.Managers).Count;
      foreach (var n in RootManagers) if (Field(r.Managers, n, out v, out tt) && tt == T_TABLE && v != 0) r.Mgr[n] = v;
      return r;
    }

    /// <summary>The registry's first array slots for the log: "array N at A: [0] tt=.. v=.., [1] ..".</summary>
    static string RegSlots(byte[] ra, int na, uint sa, ulong ap) {
      var p = new List<string>();
      for (int i = 0; i < na; i++) p.Add("[" + i + "] tt=0x" + BitConverter.ToInt32(ra, i * 16 + 8).ToString("X") + " v=" + HexA(BitConverter.ToUInt64(ra, i * 16)));
      return "array " + sa + " at " + HexA(ap) + ": " + string.Join(", ", p.ToArray());
    }

    /// <summary>The table upvalue of the Lua function _G[<paramref name="fn"/>]: the one named <paramref name="name"/> when the
    /// function keeps its upvalue names, else the only table upvalue that is not _G. 0 = not found (the step is in <paramref name="r"/>).</summary>
    static ulong UpvalueTable(ulong G, string fn, string name, LuaRootInfo r) {
      ulong cl; int tt;
      if (!Field(G, fn, out cl, out tt)) { r.Fail(fn, "missing"); return 0; }
      if (tt != T_LCL) { r.Fail(fn, "tt " + tt); return 0; }
      var cb = Read(cl, 0x20); if (cb == null || cb[8] != 6) { r.Fail(fn, "not a Lua closure"); return 0; }
      int n = cb[0xA]; if (n < 1 || n > 32) { r.Fail(fn + " upvalues", n.ToString()); return 0; }
      var ub = Read(cl + 0x20, n * 8); if (ub == null) { r.Fail(fn + " upvalues", "unreadable"); return 0; }
      // the names (Proto.upvalues), when the bytecode keeps them
      ulong p = BitConverter.ToUInt64(cb, 0x18);
      var pb = p != 0 ? Read(p, 0x60) : null;
      string[] names = new string[n];
      if (pb != null && BitConverter.ToInt32(pb, 0x10) == n) {
        var db = Read(BitConverter.ToUInt64(pb, 0x58), n * 16);
        if (db != null) for (int i = 0; i < n; i++) { ulong s = BitConverter.ToUInt64(db, i * 16); if (s != 0) names[i] = ShortText(s); }
      }
      ulong only = 0; int tables = 0;
      for (int i = 0; i < n; i++) {
        ulong uv = BitConverter.ToUInt64(ub, i * 8);
        ulong vp = uv != 0 ? Ptr(uv) : 0;
        var tv = vp != 0 ? Read(vp, 16) : null; if (tv == null) continue;
        if (BitConverter.ToInt32(tv, 8) != T_TABLE) continue;
        ulong t = BitConverter.ToUInt64(tv, 0);
        if (names[i] == name) return t;
        if (t != G) { tables++; only = t; }
      }
      if (tables == 1) return only;
      r.Fail(name, tables == 0 ? "no table upvalue of " + fn : tables + " table upvalues of " + fn);
      return 0;
    }

    static int UsedNodes(ulong t) {
      var h = Read(t, 56); if (h == null || h[8] != 5 || h[11] > 20) return 0;
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return 0;
      int used = 0; for (int i = 0; i < nn; i++) if (BitConverter.ToInt32(nb, i * 32 + 8) != T_NIL) used++;
      return used;
    }

    /// <summary>The forms of a window table: their count and class names (the shown ones first, at most 8).</summary>
    static void FormsOf(ulong t, LuaRootInfo r) {
      var h = Read(t, 56); if (h == null || h[8] != 5 || h[11] > 12) return;
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return;
      var shown = new List<string>(); var rest = new List<string>();
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
        r.UiForms++;
        if (shown.Count + rest.Count >= 8) continue;
        ulong f = BitConverter.ToUInt64(nb, o);
        string w, n = FormClassName(f, out w); if (n == "") n = "id:" + BitConverter.ToInt64(nb, o + 16);
        if (Shown(f)) shown.Add(n + "*"); else rest.Add(n);
      }
      r.FormNames.AddRange(shown); r.FormNames.AddRange(rest);
    }

    /// <summary>Compares the walk with what the scans found (UIInstance, the live tables): "yes", "no (...)" or "n/a" when
    /// the scans found none of them yet.</summary>
    public static string LuaRootMatches(LuaRootInfo r, ulong scanUi, LiveTables lt) {
      var diff = new List<string>(); int same = 0;
      if (scanUi != 0) { if (scanUi == r.Ui) same++; else diff.Add("UIInstance " + HexA(r.Ui) + " vs scan " + HexA(scanUi)); }
      if (lt != null) {
        var pairs = new[] { new KeyValuePair<string, ulong>("EquipData", lt.EquipData), new KeyValuePair<string, ulong>("HeroData", lt.HeroData),
          new KeyValuePair<string, ulong>("ArtifactData", lt.ArtOwner), new KeyValuePair<string, ulong>("ItemData", lt.ItemData), new KeyValuePair<string, ulong>("PlayerData", lt.PlayerData) };
        foreach (var kv in pairs) {
          if (kv.Value == 0) continue;
          ulong mine; r.Mgr.TryGetValue(kv.Key, out mine);
          if (mine == kv.Value) same++; else diff.Add(kv.Key + " " + HexA(mine) + " vs scan " + HexA(kv.Value));
        }
      }
      if (diff.Count > 0) return "no (" + string.Join("; ", diff.ToArray()) + ")";
      return same > 0 ? "yes" : "n/a";
    }

    /// <summary>The log line of a walk.</summary>
    public static string LuaRootLine(LuaRootInfo r, string match) {
      if (!r.Ok) return "luaroot: failed at " + r.FailStep + " (" + r.FailValue + ") in " + r.Ms + " ms" + (r.RawL != 0 ? ", rawL=" + HexA(r.RawL) : "");
      var mg = new List<string>(r.Mgr.Keys);
      return "luaroot: ok in " + r.Ms + " ms, rawL=" + HexA(r.RawL) + ", _G nodes=" + r.GNodes + ", UIInstance forms=" + r.UiForms
        + " (" + string.Join(", ", r.FormNames.ToArray()) + "), Managers=" + r.ManagerCount + " (" + string.Join(" ", mg.ToArray()) + "); matches scan: " + match;
    }

    /// <summary>GameAssembly.dll's base in the running game (0 = no game, no layout data); the tests' stand-in base when set.</summary>
    static ulong RootGa() {
      if (TestMemory != null) return LuaRootGaForTest;
      CurrentSim();   // opens the process, finds GameAssembly.dll's base (simGa)
      return simPid != 0 ? simGa : 0;
    }

    /// <summary>UIInstance from the root (0 = the walk did not get there); the reason of a miss in <paramref name="why"/>.</summary>
    static ulong RootUi(out string why) {
      var r = LuaRootWalk(RootGa());
      why = r.Ui != 0 ? null : r.FailStep + " (" + r.FailValue + ")";
      return r.Ui;
    }

    // ---- the shadow run (test build): once a minute and on each pilot start, logged; changes nothing
    static int shadowBusy, shadowAt;

    /// <summary>Test build: the walk once a minute (call it from a timer or tick; cheap when not due).</summary>
    public static void LuaRootShadowTick() {
      if (!Channel.IsTest) return;
      int now = Environment.TickCount;
      if (shadowAt != 0 && unchecked(now - shadowAt) < 60000) return;
      LuaRootShadow("minute");
    }

    /// <summary>Test build: one walk in the background, logged next to what the scans found (skipped while one runs or with no game).</summary>
    public static void LuaRootShadow(string why) {
      if (!Channel.IsTest) return;
      if (Interlocked.CompareExchange(ref shadowBusy, 1, 0) != 0) return;
      shadowAt = Environment.TickCount; if (shadowAt == 0) shadowAt = 1;
      try {
        Task.Factory.StartNew(() => {
          try {
            if (TestMemory == null && (Layout.Current == null || GameInfo.GameProcesses().Length == 0)) return;
            var r = LuaRootWalk(RootGa());
            LL(LuaRootLine(r, r.Ok ? LuaRootMatches(r, uiTable, live) : "-") + " [" + why + "]");
          } catch (Exception e) { LL("luaroot: failed at exception (" + e.Message + ")"); }
          finally { Interlocked.Exchange(ref shadowBusy, 0); }
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
      } catch (Exception) { Interlocked.Exchange(ref shadowBusy, 0); }
    }

    /// <summary>For the tests: one walk now (no task), with its log line.</summary>
    public static LuaRootInfo LuaRootForTest(out string line) {
      var r = LuaRootWalk(RootGa());
      line = LuaRootLine(r, r.Ok ? LuaRootMatches(r, uiTable, live) : "-");
      return r;
    }
  }
}
