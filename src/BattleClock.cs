// RealmForge — the clock of the fight going on, for the boss coach over the game (read-only).
//
// CSharpBattle.Battle.GameSimulation (and its kinds; TypeInfo RVAs of this game build, work/il2full/script.json):
// m_state 0xA4 (ESimulationStatus: 1 running, 2 ended), <CurrentFrameIdx> 0xC4 (one logic frame = 270/4096 s),
// _BattleData 0x48 -> BattleData.<iStageID> 0x18. The running one is found once when a fight starts (a full pass,
// about a second), then two fields are read every tick.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RealmForge {
  public static partial class RFX {
    // the battle screen (UIDefines id = str_hash("Form_Battle")): a new showing of it = a new fight
    const long FormBattleId = 0;
    // GameAssembly.dll's base in the game process (set by FindRunningSim; src/BattleTimeline.cs checks klasses with it)
    static ulong simGa;

    /// <summary>A mark of the battle screen's showing (table and instance id), or null (UIInstance not known / no
    /// battle screen held). Cheap.</summary>
    public static string BattleFormMark() {
      ulong t = UiKnown ? uiTable : 0; if (t == 0) return null;
      var h = Read(t, 56); if (h == null) return null;
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return null;
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
        if (!SameId(BitConverter.ToInt64(nb, o + 16), FormBattleId)) continue;
        ulong f = BitConverter.ToUInt64(nb, o), v; int tt;
        long inst = Field(f, "____instanceId", out v, out tt) && tt == T_INT ? (long)v : 0;
        return f.ToString("X") + ":" + inst;
      }
      return null;
    }

    /// <summary>The fight going on now: its simulation and stage id (false = none running).</summary>
    public static bool FindRunningSim(out ulong sim, out int stage) {
      sim = 0; stage = 0;
      var ps = Process.GetProcessesByName("Watcher of Realms");
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
