// RealmForge — the clock of the fight going on, for the boss coach over the game (read-only).
//
// CSharpBattle.Battle.GameSimulation (and its kinds): m_state 0xA4 (ESimulationStatus), <CurrentFrameIdx> 0xC4 (one
// logic frame = 270/4096 s), _BattleData 0x48 -> BattleData.<iStageID> 0x18. The running one is the battle view's
// (BattleManager.instance), read every couple of seconds; then two fields are read every tick. The classes and the
// status values come with the layout data from the server (src/Layout.cs): without it nothing here runs.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RealmForge {
  public static partial class RFX {
    // GameAssembly.dll's base in the game process (set by CurrentSim / FindRunningSim; src/BattleTimeline.cs checks
    // classes with it)
    static ulong simGa; static int simPid;

    // A class counts only when the class found at its address (layout data from the server) has the expected name
    // (Il2CppClass: name 0x10, namespaze 0x18). A class the data has no address for is recognised by its name on the
    // objects read and then remembered.
    const string NsBattle = "CSharpBattle.Battle.";
    /// <summary>The simulation states as the app uses them (SimClock maps the game's values to these).</summary>
    public const int SimOther = 0, SimRunning = 1, SimEnded = 2;
    static readonly string[] SimClasses = { NsBattle + "GameSimulation", NsBattle + "TDGameSimulation", NsBattle + "TCGameSimulation", NsBattle + "PAGameSimulation" };
    static readonly Dictionary<string, ulong> klassCache = new Dictionary<string, ulong>();
    static ulong klassGa;

    /// <summary>A NUL-terminated ASCII string in the game's memory (read within its page), null when unreadable.</summary>
    static string CStr(ulong p) {
      if (p == 0) return null;
      int n = (int)Math.Min(96UL, 0x1000UL - (p & 0xFFF));
      var b = Read(p, n); if (b == null) return null;
      int len = Array.IndexOf(b, (byte)0);
      if (len < 0) return null;
      return System.Text.Encoding.ASCII.GetString(b, 0, len);
    }

    /// <summary>The full name ("Namespace.Class") of a class, null when <paramref name="k"/> is not readable as one.</summary>
    static string ClassName(ulong k) {
      var b = k != 0 ? Read(k + 0x10, 16) : null; if (b == null) return null;
      string name = CStr(BitConverter.ToUInt64(b, 0)); if (string.IsNullOrEmpty(name)) return null;
      string ns = CStr(BitConverter.ToUInt64(b, 8)); if (ns == null) return null;
      return ns.Length > 0 ? ns + "." + name : name;
    }

    /// <summary>The class <paramref name="cls"/> of the game whose GameAssembly.dll is at <paramref name="ga"/>: from its
    /// address in this build (checked by name) or as recognised on an object; 0 when not known (yet).</summary>
    static ulong ClassAt(ulong ga, string cls) {
      if (ga == 0) return 0;
      ulong k;
      lock (klassCache) {
        if (ga != klassGa) { klassCache.Clear(); klassGa = ga; }
        if (klassCache.TryGetValue(cls, out k)) return k;
      }
      k = 0;
      var ly = Layout.Current;
      ulong rva = ly != null ? ly.Rva(cls) : 0;
      if (rva != 0) { ulong c = Ptr(ga + rva); if (c != 0 && ClassName(c) == cls) k = c; }
      // not cached when not found: the address is filled in once the game first uses the class
      if (k != 0) lock (klassCache) { if (ga == klassGa) klassCache[cls] = k; }
      return k;
    }

    /// <summary>Forget the classes found (the game restarted).</summary>
    static void ForgetClasses() { lock (klassCache) { klassCache.Clear(); klassGa = 0; } }

    /// <summary>Is <paramref name="k"/> the class <paramref name="cls"/> of the current game process?</summary>
    static bool IsKlass(ulong k, string cls) {
      if (k == 0) return false;
      ulong want = ClassAt(simGa, cls);
      if (want != 0) return k == want;
      if (ClassName(k) != cls) return false;
      if (simGa != 0) lock (klassCache) { if (simGa == klassGa) klassCache[cls] = k; }
      return true;
    }

    /// <summary>Is the object at <paramref name="obj"/> of the class <paramref name="cls"/>?</summary>
    static bool IsA(ulong obj, string cls) { return obj != 0 && IsKlass(Ptr(obj + (ulong)Layout.Current.Hdr("klass")), cls); }

    /// <summary>The simulations' classes (class -> short name): by address, plus the class of the battle view's
    /// simulation when it is another kind of GameSimulation.</summary>
    static Dictionary<ulong, string> SimKlasses(ulong ga) {
      var res = new Dictionary<ulong, string>();
      foreach (var c in SimClasses) { ulong k = ClassAt(ga, c); if (k != 0) res[k] = c.Substring(NsBattle.Length); }
      ulong cur = ViewSim(ga), ck = Ptr(cur);
      if (ck != 0 && !res.ContainsKey(ck)) {
        string n = ClassName(ck);
        if (n != null && n.StartsWith(NsBattle, StringComparison.Ordinal) && n.EndsWith("GameSimulation", StringComparison.Ordinal))
          res[ck] = n.Substring(NsBattle.Length);
      }
      return res;
    }

    /// <summary>BattleManager.instance.m_simulation of the game whose GameAssembly.dll is at <paramref name="ga"/>, or 0.</summary>
    static ulong ViewSim(ulong ga) {
      ulong k = ClassAt(ga, "BattleView.BattleManager"); if (k == 0) return 0;
      ulong sf = Ptr(k + 0xB8); if (sf == 0) return 0;
      ulong bm = Ptr(sf); if (bm == 0) return 0;
      return Ptr(bm + 0x10);
    }

    /// <summary>The fight going on now, by a full memory pass (about a second; CurrentSim is the cheap way): its
    /// simulation and stage id (false = none running).</summary>
    public static bool FindRunningSim(out ulong sim, out int stage) {
      sim = 0; stage = 0;
      var ly = Layout.Current; if (ly == null) return false;
      int span = ly.Span("GameSimulation"), running = ly.Const("ESimulationStatus.Runing"), ko = ly.Hdr("klass");
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) return false;
      if (H == IntPtr.Zero) H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return false;
      ulong ga = 0;
      try { foreach (ProcessModule m in ps[0].Modules) if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) ga = (ulong)(long)m.BaseAddress; } catch (Exception) { }
      if (ga == 0) return false;
      if (ga != simGa || ps[0].Id != simPid) ForgetClasses();
      simGa = ga;
      var klass = new HashSet<ulong>(SimKlasses(ga).Keys);
      if (klass.Count == 0) return false;
      regs = Regions();
      var found = new List<ulong>();
      ScanParallel((b0, buf, len) => {
        for (int i = 0; i + span <= len; i += 8) {
          if (!klass.Contains(BitConverter.ToUInt64(buf, i + ko))) continue;
          if (BitConverter.ToInt32(buf, i + 0xA4) != running) continue;
          uint fr = BitConverter.ToUInt32(buf, i + 0xC4); if (fr == 0 || fr > 200000) continue;
          lock (found) found.Add(b0 + (ulong)i);
        }
      });
      // several running (a replay kept?): the one whose clock moves; else the first
      ulong best = 0; uint bestFr = 0;
      foreach (var s in found) { uint fr; int st; if (SimClock(s, out fr, out st) && st == SimRunning && fr >= bestFr) { best = s; bestFr = fr; } }
      if (best == 0) return false;
      if (found.Count > 1) {
        System.Threading.Thread.Sleep(300);
        foreach (var s in found) { uint fr; int st; if (s != best && SimClock(s, out fr, out st) && st == SimRunning && fr > bestFr) { best = s; bestFr = fr; } }
      }
      sim = best;
      var bd = Read(sim + 0x48, 8); ulong data = bd != null ? BitConverter.ToUInt64(bd, 0) : 0;
      var sb = data != 0 ? Read(data + 0x18, 4) : null;
      stage = sb != null ? BitConverter.ToInt32(sb, 0) : 0;
      return true;
    }

    // BattleView.BattleManager (ViewSim): static `instance` at static_fields + 0x0, `m_simulation` 0x10; the class's
    // static fields at 0xB8

    /// <summary>The simulation the battle view holds now (BattleManager.instance.m_simulation), or 0. Cheap: four
    /// pointer reads, no scan. The game keeps the last one after a fight ends, so check SimClock's state.</summary>
    public static ulong CurrentSim() {
      if (Layout.Current == null) return 0;
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) { simPid = 0; return 0; }
      if (ps[0].Id != simPid || simGa == 0 || H == IntPtr.Zero) {
        // the game (re)started: its handle and GameAssembly's base anew
        if (ps[0].Id != simPid || H == IntPtr.Zero) H = OpenProcess(0x0410, false, ps[0].Id);
        if (H == IntPtr.Zero) return 0;
        simGa = 0; ForgetClasses();
        try { foreach (ProcessModule m in ps[0].Modules) if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) simGa = (ulong)(long)m.BaseAddress; } catch (Exception) { }
        if (simGa == 0) return 0;
        simPid = ps[0].Id;
      }
      return ViewSim(simGa);
    }

    /// <summary>The stage id of a simulation (_BattleData.iStageID), 0 if unknown.</summary>
    public static int SimStage(ulong sim) {
      ulong data = Ptr(sim + 0x48);
      var sb = data != 0 ? Read(data + 0x18, 4) : null;
      return sb != null ? BitConverter.ToInt32(sb, 0) : 0;
    }

    static ulong Ptr(ulong at) { var b = at != 0 ? Read(at, 8) : null; return b != null ? BitConverter.ToUInt64(b, 0) : 0; }

    /// <summary>The simulation's frame count and state (SimRunning, SimEnded, SimOther). Cheap.</summary>
    public static bool SimClock(ulong sim, out uint frames, out int state) {
      frames = 0; state = SimOther;
      var ly = Layout.Current;
      var b = sim != 0 && ly != null ? Read(sim + 0xA4, 0x24) : null;
      if (b == null) return false;
      state = SimState(ly, BitConverter.ToInt32(b, 0));
      frames = BitConverter.ToUInt32(b, 0x20);
      return true;
    }

    /// <summary>The game's simulation status as SimRunning / SimEnded / SimOther.</summary>
    static int SimState(GameLayout ly, int raw) {
      return raw == ly.Const("ESimulationStatus.Runing") ? SimRunning : raw == ly.Const("ESimulationStatus.End") ? SimEnded : SimOther;
    }

    /// <summary>Seconds of a frame count (one logic frame = 270/4096 s).</summary>
    public static double FrameSeconds(uint frames) { return frames * 270.0 / 4096.0; }
  }
}
