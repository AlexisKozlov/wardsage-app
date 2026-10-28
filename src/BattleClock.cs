// RealmForge — the clock of the fight going on, for the boss coach over the game (read-only).
//
// CSharpBattle.Battle.GameSimulation (and its kinds): m_state 0xA4 (ESimulationStatus), <CurrentFrameIdx> 0xC4 (one
// logic frame = 270/4096 s), _BattleData 0x48 -> BattleData.<iStageID> 0x18. The running one is the battle view's
// (BattleManager.instance), read every couple of seconds; then two fields are read every tick. The classes and the
// status values come with the layout data from the server (src/Layout.cs): without it nothing here runs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

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
    static int klassGen;   // bumped by ForgetClasses: a scan started before it is dropped

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
      return FullName(ns, name);
    }

    /// <summary>"Namespace.Class" (just the name when the namespace is empty).</summary>
    static string FullName(string ns, string name) { return ns.Length > 0 ? ns + "." + name : name; }

    /// <summary>The class <paramref name="cls"/> of the game whose GameAssembly.dll is at <paramref name="ga"/>: from its
    /// address in this build (checked by name), the slot kept from an earlier scan (checked by name) or as recognised on an
    /// object; 0 when not known (yet). Never blocks: when it has no answer a scan of GameAssembly.dll's memory is asked for
    /// (in the background, see RequestScan) and a later call finds the class.</summary>
    public static ulong ClassAt(ulong ga, string cls) {
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
      if (k == 0) k = DiskSlotClass(ga, cls);
      // not cached when not found: the address is filled in once the game first uses the class
      if (k != 0) lock (klassCache) { if (ga == klassGa) klassCache[cls] = k; }
      else if (ly == null || ly.RvaDropped || rva == 0) { RequestScan(ga, cls); }   // an address of this build that is not filled yet is waited for, not scanned for
      return k;
    }

    /// <summary>Forget the classes found (the game restarted); a scan that is running drops its result.</summary>
    public static void ForgetClasses() { lock (klassCache) { klassCache.Clear(); klassGa = 0; klassGen++; } }

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
      EnsureClasses(ga, 5000);
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

    // ---- classes found by a scan of GameAssembly.dll's memory (src/KlassScan.cs) ----

    /// <summary>The folder of the app's data for the saved slots (tests set another); null = the app's.</summary>
    public static string KlassDir;
    static string KlassDirNow() { return KlassDir ?? AppConfig.DefaultDir; }

    static readonly object scanLock = new object();
    static int scanBusy;                       // 1 while a scan runs
    static readonly HashSet<string> missed = new HashSet<string>(StringComparer.Ordinal);   // classes asked for and not found
    /// <summary>Scans asked for so far (tests).</summary>
    public static int scanRequests;
    static ulong scanGa; static int scanPid, scanTries, scanFailTick; static bool scanFailed;   // per (pid, ga), under scanLock
    static string diskAsm; static Dictionary<string, ulong> diskSlots;     // the saved slots of the local build, under scanLock
    static readonly HashSet<string> diskOk = new HashSet<string>(), diskStale = new HashSet<string>();   // under scanLock
    const int ScanCapMs = 4000, ScanRetryMs = 30000, ScanMaxTries = 20;
    static readonly string[] ScanWanted = { "BattleView.BattleManager", "XLua.InternalGlobals", "XLua.ObjectTranslator", "Form_CommonTips" };

    /// <summary>The class from the slot kept for this build (an earlier scan), 0 when none or stale.</summary>
    static ulong DiskSlotClass(ulong ga, string cls) {
      string asm = Layout.LocalAsm;
      if (asm == null || KlassScan.CacheFile(KlassDirNow(), asm) == null) return 0;
      ulong slot; bool have;
      lock (scanLock) {
        if (diskSlots == null || !string.Equals(diskAsm, asm, StringComparison.OrdinalIgnoreCase)) { diskAsm = asm; diskSlots = KlassScan.Load(KlassDirNow(), asm) ?? new Dictionary<string, ulong>(); }
        have = diskSlots.TryGetValue(cls, out slot);
      }
      if (!have) return 0;
      ulong c = Ptr(ga + slot);
      bool ok = c != 0 && ClassName(c) == cls;
      string line = null;
      lock (scanLock) {
        if (ok) { if (diskOk.Add(cls)) diskStale.Remove(cls); else return c; }
        else if (!diskStale.Add(cls)) return 0;
        else diskOk.Remove(cls);
        line = "class slots from disk for asm " + asm.Substring(0, 8) + "..: " + diskOk.Count + " ok, " + diskStale.Count + " stale";
      }
      LL(line);
      return ok ? c : 0;
    }

    /// <summary>The names ("Namespace.Class") of the classes at <paramref name="ks"/> (sorted, unique), in the same order; null
    /// for one that is not readable as a class. Reads in windows of 64 KB: the name and namespace pointers (+0x10, +0x18),
    /// then the strings.</summary>
    public static IList<string> KlassNames(IList<ulong> ks) {
      const int Win = 64 * 1024;
      int n = ks.Count; var res = new string[n];
      var np = new ulong[n]; var sp = new ulong[n];
      for (int i = 0; i < n; ) {
        int j = i; while (j + 1 < n && ks[j + 1] - ks[i] + 0x10 <= (ulong)Win && ks[j + 1] > ks[j]) j++;
        var b = Read(ks[i] + 0x10, (int)(ks[j] - ks[i]) + 0x10);
        for (int t = i; t <= j; t++) {
          byte[] e = b != null ? null : Read(ks[t] + 0x10, 16);
          if (b != null) { int o = (int)(ks[t] - ks[i]); np[t] = BitConverter.ToUInt64(b, o); sp[t] = BitConverter.ToUInt64(b, o + 8); }
          else if (e != null) { np[t] = BitConverter.ToUInt64(e, 0); sp[t] = BitConverter.ToUInt64(e, 8); }
        }
        i = j + 1;
      }
      var ptrs = new List<ulong>();
      for (int i = 0; i < n; i++) if (np[i] != 0) { ptrs.Add(np[i]); ptrs.Add(sp[i]); }
      var strs = CStrs(ptrs);
      for (int i = 0; i < n; i++) {
        if (np[i] == 0) continue;
        string name, ns;
        if (!strs.TryGetValue(np[i], out name) || string.IsNullOrEmpty(name)) continue;
        if (!strs.TryGetValue(sp[i], out ns) || ns == null) continue;
        res[i] = FullName(ns, name);
      }
      return res;
    }

    /// <summary>The NUL-terminated strings (at most 96 bytes) at <paramref name="ptrs"/>, read in windows of 64 KB; a pointer
    /// without a string is not in the result.</summary>
    static Dictionary<ulong, string> CStrs(List<ulong> ptrs) {
      const int Win = 64 * 1024, Max = 96;
      var res = new Dictionary<ulong, string>();
      var u = new List<ulong>(new HashSet<ulong>(ptrs)); u.Remove(0); u.Sort();
      for (int i = 0; i < u.Count; ) {
        int j = i; while (j + 1 < u.Count && u[j + 1] - u[i] + Max <= (ulong)Win) j++;
        var b = Read(u[i], (int)(u[j] - u[i]) + Max);
        for (int t = i; t <= j; t++) {
          string s = null;
          if (b != null) {
            int o = (int)(u[t] - u[i]), len = Array.IndexOf(b, (byte)0, o, Max);
            if (len >= 0) s = System.Text.Encoding.ASCII.GetString(b, o, len - o);
          } else s = CStr(u[t]);
          if (s != null) res[u[t]] = s;
        }
        i = j + 1;
      }
      return res;
    }

    /// <summary>GameAssembly.dll's writable data sections (writable, not executable) read in 1 MB chunks, at most 64 MB each
    /// and 256 MB in all (a chunk that cannot be read is skipped); its size in memory and the number of sections come with.
    /// Empty when the header at <paramref name="ga"/> is not a PE image.</summary>
    public static List<KlassSection> GaSections(ulong ga, out ulong imageSize, out int sections) {
      const int Chunk = 1024 * 1024; const long PerSec = 64L * 1024 * 1024, Total = 256L * 1024 * 1024;
      var res = new List<KlassSection>(); imageSize = 0; sections = 0;
      var h = ga != 0 ? Read(ga, 0x1000) : null;
      if (h == null || h[0] != (byte)'M' || h[1] != (byte)'Z') return res;
      uint lf = BitConverter.ToUInt32(h, 0x3C);
      if (lf < 0x40 || (long)lf + 24 + 60 > 0x1000 || h[lf] != (byte)'P' || h[lf + 1] != (byte)'E' || h[lf + 2] != 0 || h[lf + 3] != 0) return res;
      int ns = BitConverter.ToUInt16(h, (int)lf + 6), opt = BitConverter.ToUInt16(h, (int)lf + 20);
      imageSize = BitConverter.ToUInt32(h, (int)lf + 24 + 56);
      long tab = lf + 24 + opt, got = 0;
      for (int i = 0; i < ns && i < 96 && tab + 40 * (i + 1) <= 0x1000; i++) {
        int e = (int)tab + 40 * i;
        uint vsize = BitConverter.ToUInt32(h, e + 8), va = BitConverter.ToUInt32(h, e + 12), ch = BitConverter.ToUInt32(h, e + 36);
        if ((ch & 0x80000000u) == 0 || (ch & 0x20000000u) != 0 || va == 0 || vsize == 0) continue;
        long size = Math.Min((long)vsize, PerSec); bool any = false;
        for (long off = 0; off < size && got < Total; off += Chunk) {
          int n = (int)Math.Min((long)Chunk, size - off);
          var b = Read(ga + va + (ulong)off, n);
          if (b == null) continue;
          res.Add(new KlassSection { Base = ga + va + (ulong)off, Bytes = b }); got += n; any = true;
        }
        if (any) sections++;
      }
      return res;
    }

    /// <summary>Tells whether a value may be an object's address: inside committed read-write memory of the process, not
    /// inside GameAssembly.dll itself ([ga, ga + imageSize)).</summary>
    public static Func<ulong, bool> HeapTest(ulong ga, ulong imageSize) {
      var rs = Regions(); rs.Sort((a, b) => a.Base.CompareTo(b.Base));
      var bases = new ulong[rs.Count]; var ends = new ulong[rs.Count];
      for (int i = 0; i < rs.Count; i++) { bases[i] = rs[i].Base; ends[i] = rs[i].Base + rs[i].Size; }
      ulong gaEnd = ga + imageSize;
      return v => {
        if (v >= ga && v < gaEnd) return false;
        int lo = 0, hi = bases.Length - 1, at = -1;
        while (lo <= hi) { int mid = (lo + hi) >> 1; if (bases[mid] <= v) { at = mid; lo = mid + 1; } else hi = mid - 1; }
        return at >= 0 && v < ends[at];
      };
    }

    /// <summary>Asks for a scan of GameAssembly.dll at <paramref name="ga"/> for the classes wanted, in the background: one at a
    /// time, not again within 30 s of a scan that missed a class asked for, at most 20 per game process (the slots fill in
    /// lazily, so early misses are normal).</summary>
    static void RequestScan(ulong ga, string cls) {
      Interlocked.Increment(ref scanRequests);
      if (ga == 0 || (TestMemory == null && H == IntPtr.Zero)) return;
      if (Interlocked.CompareExchange(ref scanBusy, 1, 0) != 0) return;
      int gen; IntPtr h = H; string asm = Layout.LocalAsm; var want = new HashSet<string>(ScanWanted, StringComparer.Ordinal); var asked = new List<string>();
      try {
        lock (scanLock) {
          int hp = HandlePid(h);
          if (ga != scanGa || hp != scanPid) { scanGa = ga; scanPid = hp; scanTries = 0; scanFailed = false; missed.Clear(); }
          if (cls != null && missed.Count < 64) missed.Add(cls);
          if (scanTries >= ScanMaxTries || (scanFailed && unchecked(Environment.TickCount - scanFailTick) < ScanRetryMs)) { Interlocked.Exchange(ref scanBusy, 0); return; }
          scanTries++;
          asked.AddRange(missed);
        }
        lock (klassCache) gen = klassGen;
        foreach (var c in SimClasses) want.Add(c);
        foreach (var c in asked) want.Add(c);
        Task.Factory.StartNew(() => RunScan(ga, gen, h, asm, want, asked), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
      } catch (Exception) { Interlocked.Exchange(ref scanBusy, 0); }
    }

    static void RunScan(ulong ga, int gen, IntPtr h, string asm, HashSet<string> want, List<string> asked) {
      try {
        int t0 = Environment.TickCount, deadline = t0 + ScanCapMs;
        ulong size; int nsec; bool timedOut;
        var secs = GaSections(ga, out size, out nsec);
        var stats = new KlassStats();
        var found = KlassScan.Find(secs, HeapTest(ga, size), KlassNames, want, () => KlassScan.Passed(deadline), stats, out timedOut);
        long bytes = 0; foreach (var s in secs) bytes += s.Bytes.Length;
        var ok = new Dictionary<string, KlassHit>(); var sb = new System.Text.StringBuilder();
        var miss = new List<string>();
        foreach (var w in want) {
          KlassHit k;
          if (found.TryGetValue(w, out k) && k.Distinct == 1) { ok[w] = k; if (sb.Length > 0) sb.Append(", "); sb.Append(w).Append("@slot+0x").Append((k.Slot - ga).ToString("X")); }
          else if (!found.ContainsKey(w)) miss.Add(w);
        }
        miss.Sort(StringComparer.Ordinal);
        double secsTaken = unchecked(Environment.TickCount - t0) / 1000.0;
        LL("class scan: " + (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB in " + nsec + " sections, " + (stats.Pointers / 1000) + "k pointers, "
          + secsTaken.ToString("0.0", CultureInfo.InvariantCulture) + " s: found " + (sb.Length > 0 ? sb.ToString() : "-") + "; missing " + (miss.Count > 0 ? string.Join(", ", miss.ToArray()) : "-")
          + "; ambiguous " + (stats.Ambiguous.Count > 0 ? string.Join(", ", stats.Ambiguous.ToArray()) : "-") + " (" + want.Count + " classes)" + (timedOut ? "; timed out" : ""));
        bool merged = false;
        lock (klassCache) {
          if (gen == klassGen && ga == klassGa && h == H) { foreach (var kv in ok) klassCache[kv.Key] = kv.Value.Klass; merged = true; }
        }
        if (!merged) return;
        bool failed = false;
        foreach (var c in asked) if (!ok.ContainsKey(c)) failed = true;
        var slots = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var kv in ok) slots[kv.Key] = kv.Value.Slot - ga;
        lock (scanLock) {
          scanFailed = failed; scanFailTick = Environment.TickCount;
          foreach (var kv in ok) missed.Remove(kv.Key);
          if (diskSlots != null && string.Equals(diskAsm, asm, StringComparison.OrdinalIgnoreCase)) foreach (var kv in slots) diskSlots[kv.Key] = kv.Value;
        }
        // the slots kept for the next start (outside the locks)
        if (!timedOut) SaveSlots(asm, slots);
      } catch (Exception e) { LL("class scan failed: " + e.Message); }
      finally { Interlocked.Exchange(ref scanBusy, 0); }
    }

    /// <summary>Keeps the slots found for the next start; never after a scan that timed out (a name not seen twice by a cut-short
    /// scan may be ambiguous). True when the file was written.</summary>
    public static bool SaveSlots(string asm, IDictionary<string, ulong> slots, bool timedOut) {
      if (timedOut) return false;
      return SaveSlots(asm, slots);
    }
    static bool SaveSlots(string asm, IDictionary<string, ulong> slots) {
      try {
        if (asm != null && slots.Count > 0 && KlassScan.CacheFile(KlassDirNow(), asm) != null) {
          var all = KlassScan.Load(KlassDirNow(), asm) ?? new Dictionary<string, ulong>(StringComparer.Ordinal);
          bool changed = false;
          foreach (var kv in slots) { ulong old; if (!all.TryGetValue(kv.Key, out old) || old != kv.Value) { all[kv.Key] = kv.Value; changed = true; } }
          if (changed) return KlassScan.Save(KlassDirNow(), asm, all);
        }
      } catch (Exception e) { LL("class slots not saved: " + e.Message); }
      return false;
    }

    /// <summary>The process id the handle <paramref name="h"/> belongs to; 0 when none (or no handle).</summary>
    static int HandlePid(IntPtr h) {
      if (h == IntPtr.Zero || TestMemory != null) return 0;
      try { return (int)GetProcessId(h); } catch (Exception) { return 0; }
    }

    /// <summary>Waits up to <paramref name="waitMs"/> ms for a running scan to end; true when none is running.</summary>
    public static bool ScanIdle(int waitMs) {
      int end = Environment.TickCount + waitMs;
      while (Volatile.Read(ref scanBusy) != 0) { if (unchecked(end - Environment.TickCount) <= 0) return false; Thread.Sleep(10); }
      return true;
    }

    /// <summary>For callers on slow worker threads: the classes of the simulations are asked for and waited for up to
    /// <paramref name="waitMs"/> ms while a scan finds them. True when at least one kind of GameSimulation is known.</summary>
    public static bool EnsureClasses(ulong ga, int waitMs) {
      if (ga == 0) return false;
      int end = Environment.TickCount + waitMs;
      while (true) {
        bool ready = false;
        ClassAt(ga, "BattleView.BattleManager");
        foreach (var c in SimClasses) if (ClassAt(ga, c) != 0) ready = true;
        if (ready) return true;
        if (waitMs <= 0 || unchecked(end - Environment.TickCount) <= 0 || Volatile.Read(ref scanBusy) == 0) return false;
        Thread.Sleep(50);
      }
    }
  }
}
