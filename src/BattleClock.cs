// RealmForge — the clock of the fight going on, for the boss coach over the game (read-only).
//
// CSharpBattle.Battle.GameSimulation (and its kinds; class addresses of this game build):
// m_state 0xA4 (ESimulationStatus: 1 running, 2 ended), <CurrentFrameIdx> 0xC4 (one logic frame = 270/4096 s),
// _BattleData 0x48 -> BattleData.<iStageID> 0x18. The running one is the battle view's (BattleManager.instance), read
// every couple of seconds; then two fields are read every tick.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RealmForge {
  public static partial class RFX {
    // GameAssembly.dll's base in the game process (set by CurrentSim / FindRunningSim; src/BattleTimeline.cs checks
    // classes with it)
    static ulong simGa; static int simPid;

    /// <summary>The fight going on now, by a full memory pass (about a second; CurrentSim is the cheap way): its
    /// simulation and stage id (false = none running).</summary>
    public static bool FindRunningSim(out ulong sim, out int stage) {
      sim = 0; stage = 0;
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) return false;
      if (H == IntPtr.Zero) H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return false;
      ulong ga = 0;
      try { foreach (ProcessModule m in ps[0].Modules) if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) ga = (ulong)(long)m.BaseAddress; } catch (Exception) { }
      if (ga == 0) return false;
      simGa = ga;
      var klass = new HashSet<ulong>();
      foreach (var rva in new ulong[] { 0, 0, 0, 0 }) {
        var kb = Read(ga + rva, 8); ulong k = kb != null ? BitConverter.ToUInt64(kb, 0) : 0;
        if (k != 0) klass.Add(k);
      }
      if (klass.Count == 0) return false;
      regs = Regions();
      var found = new List<ulong>();
      ScanParallel((b0, buf, len) => {
        for (int i = 0; i + 0xC8 <= len; i += 8) {
          if (!klass.Contains(BitConverter.ToUInt64(buf, i))) continue;
          if (BitConverter.ToInt32(buf, i + 0xA4) != 1) continue;   // running
          uint fr = BitConverter.ToUInt32(buf, i + 0xC4); if (fr == 0 || fr > 200000) continue;
          lock (found) found.Add(b0 + (ulong)i);
        }
      });
      // several running (a replay kept?): the one whose clock moves; else the first
      ulong best = 0; uint bestFr = 0;
      foreach (var s in found) { uint fr; int st; if (SimClock(s, out fr, out st) && st == 1 && fr >= bestFr) { best = s; bestFr = fr; } }
      if (best == 0) return false;
      if (found.Count > 1) {
        System.Threading.Thread.Sleep(300);
        foreach (var s in found) { uint fr; int st; if (s != best && SimClock(s, out fr, out st) && st == 1 && fr > bestFr) { best = s; bestFr = fr; } }
      }
      sim = best;
      var bd = Read(sim + 0x48, 8); ulong data = bd != null ? BitConverter.ToUInt64(bd, 0) : 0;
      var sb = data != 0 ? Read(data + 0x18, 4) : null;
      stage = sb != null ? BitConverter.ToInt32(sb, 0) : 0;
      return true;
    }

    // BattleView.BattleManager: class address, static `instance` at static_fields + 0x0, `m_simulation` 0x10;
    // the class's static fields at 0xB8
    const ulong BattleManagerRva = 0;

    /// <summary>The simulation the battle view holds now (BattleManager.instance.m_simulation), or 0. Cheap: four
    /// pointer reads, no scan. The game keeps the last one after a fight ends, so check SimClock's state.</summary>
    public static ulong CurrentSim() {
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) { simPid = 0; return 0; }
      if (ps[0].Id != simPid || simGa == 0 || H == IntPtr.Zero) {
        // the game (re)started: its handle and GameAssembly's base anew
        if (ps[0].Id != simPid || H == IntPtr.Zero) H = OpenProcess(0x0410, false, ps[0].Id);
        if (H == IntPtr.Zero) return 0;
        simGa = 0;
        try { foreach (ProcessModule m in ps[0].Modules) if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) simGa = (ulong)(long)m.BaseAddress; } catch (Exception) { }
        if (simGa == 0) return 0;
        simPid = ps[0].Id;
      }
      ulong k = Ptr(simGa + BattleManagerRva); if (k == 0) return 0;
      ulong sf = Ptr(k + 0xB8); if (sf == 0) return 0;
      ulong bm = Ptr(sf); if (bm == 0) return 0;
      return Ptr(bm + 0x10);
    }

    /// <summary>The stage id of a simulation (_BattleData.iStageID), 0 if unknown.</summary>
    public static int SimStage(ulong sim) {
      ulong data = Ptr(sim + 0x48);
      var sb = data != 0 ? Read(data + 0x18, 4) : null;
      return sb != null ? BitConverter.ToInt32(sb, 0) : 0;
    }

    static ulong Ptr(ulong at) { var b = at != 0 ? Read(at, 8) : null; return b != null ? BitConverter.ToUInt64(b, 0) : 0; }

    /// <summary>The simulation's frame count and state (1 running, 2 ended). Cheap.</summary>
    public static bool SimClock(ulong sim, out uint frames, out int state) {
      frames = 0; state = 0;
      var b = sim != 0 ? Read(sim + 0xA4, 0x24) : null;
      if (b == null) return false;
      state = BitConverter.ToInt32(b, 0);
      frames = BitConverter.ToUInt32(b, 0x20);
      return true;
    }

    /// <summary>Seconds of a frame count (one logic frame = 270/4096 s).</summary>
    public static double FrameSeconds(uint frames) { return frames * 270.0 / 4096.0; }
  }
}
