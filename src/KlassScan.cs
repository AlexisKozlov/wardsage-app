// Wardsage - finds il2cpp classes by a read-only scan of GameAssembly.dll's data sections (pure part: no process access).
//
// The game's code keeps a pointer to each Il2CppClass in a slot of its writable data (filled lazily, the first time the
// class is used). The layout data's "rva" says where that slot is in ONE build; for another build of the same game
// version the slot is elsewhere. Here the slots are found by what they hold: a value that points into the heap and, read
// as an Il2CppClass, has the wanted name (name 0x10, namespace 0x18; the reading is done by the caller). A name that
// matches two different class values is not accepted (fail closed). The slot's rva is stable for a build, so it is kept
// on disk (layout\klass-<asm>.json) and the next start needs no scan.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  /// <summary>A piece of GameAssembly.dll's memory: <see cref="Bytes"/> read at <see cref="Base"/>.</summary>
  public sealed class KlassSection { public ulong Base; public byte[] Bytes; }

  /// <summary>A class found: its Il2CppClass address and the slot that holds it. <see cref="Distinct"/> &gt; 1 = several
  /// different classes have the name: not accepted (<see cref="Klass"/> and <see cref="Slot"/> are 0).</summary>
  public sealed class KlassHit { public ulong Klass; public ulong Slot; public int Distinct; }

  /// <summary>What a pass saw (for the log).</summary>
  public sealed class KlassStats { public int Pointers; public readonly List<string> Ambiguous = new List<string>(); }

  public static class KlassScan {
    public const int BatchSize = 4096;

    /// <summary>The deadline (Environment.TickCount value) has passed.</summary>
    public static bool Passed(int deadlineTick) { return unchecked(Environment.TickCount - deadlineTick) >= 0; }

    public static Dictionary<string, KlassHit> Find(IList<KlassSection> secs, Func<ulong, bool> isHeap, Func<IList<ulong>, IList<string>> namesOf,
        ICollection<string> wanted, int deadlineTick, out bool timedOut) {
      return Find(secs, isHeap, namesOf, wanted, () => Passed(deadlineTick), null, out timedOut);
    }

    /// <summary>The wanted classes found in <paramref name="secs"/>: name -> hit (a name not found is not in the result; one
    /// with several different classes is, with Distinct &gt; 1 and no address). <paramref name="isHeap"/> says whether a
    /// value may be an object's address (the module itself excluded); <paramref name="namesOf"/> gives the full names of
    /// classes at the addresses (null for one that is not a class). <paramref name="expired"/> is asked every 64 KB and
    /// before each batch of names: true = stop and return what is found (timedOut).</summary>
    public static Dictionary<string, KlassHit> Find(IList<KlassSection> secs, Func<ulong, bool> isHeap, Func<IList<ulong>, IList<string>> namesOf,
        ICollection<string> wanted, Func<bool> expired, KlassStats stats, out bool timedOut) {
      timedOut = false;
      var res = new Dictionary<string, KlassHit>(StringComparer.Ordinal);
      if (secs == null || wanted == null || wanted.Count == 0) return res;
      var want = new HashSet<string>(wanted, StringComparer.Ordinal);
      // pass 1: the slots (a value kept once, with the first slot that holds it)
      var slots = new Dictionary<ulong, ulong>();
      foreach (var s in secs) {
        if (s == null || s.Bytes == null) continue;
        var b = s.Bytes;
        for (int i = 0; i + 8 <= b.Length; i += 8) {
          if ((i & 0xFFFF) == 0 && expired()) { timedOut = true; break; }
          ulong v = BitConverter.ToUInt64(b, i);
          if ((v & 7) != 0 || v == 0) continue;   // odd = a metadata token (slot not filled yet), misaligned = no object
          if (slots.ContainsKey(v) || !isHeap(v)) continue;
          slots[v] = s.Base + (ulong)i;
        }
        if (timedOut) break;
      }
      if (stats != null) stats.Pointers = slots.Count;
      // pass 2: their names
      var vals = new List<ulong>(slots.Keys); vals.Sort();
      for (int at = 0; at < vals.Count && !timedOut; at += BatchSize) {
        if (expired()) { timedOut = true; break; }
        var batch = vals.GetRange(at, Math.Min(BatchSize, vals.Count - at));
        var names = namesOf(batch);
        if (names == null || names.Count != batch.Count) continue;
        for (int j = 0; j < batch.Count; j++) {
          string n = names[j]; if (n == null || !want.Contains(n)) continue;
          KlassHit h;
          if (!res.TryGetValue(n, out h)) res[n] = new KlassHit { Klass = batch[j], Slot = slots[batch[j]], Distinct = 1 };
          else { h.Distinct++; h.Klass = 0; h.Slot = 0; }
        }
      }
      if (stats != null) foreach (var kv in res) if (kv.Value.Distinct > 1) stats.Ambiguous.Add(kv.Key);
      return res;
    }

    // ---- the slots kept on disk ----

    static bool GoodAsm(string asm) { return asm != null && Regex.IsMatch(asm, "^[0-9a-fA-F]{64}$"); }

    /// <summary>layout\klass-&lt;asm&gt;.json under <paramref name="dir"/>; null when <paramref name="asm"/> is not a SHA-256 in hex.</summary>
    public static string CacheFile(string dir, string asm) {
      if (string.IsNullOrEmpty(dir) || !GoodAsm(asm)) return null;
      return Path.Combine(Path.Combine(dir, "layout"), "klass-" + asm.ToLowerInvariant() + ".json");
    }

    /// <summary>The saved slots of this build (class -> rva of its slot in GameAssembly.dll), null when none or not valid.</summary>
    public static Dictionary<string, ulong> Load(string dir, string asm) {
      string p = CacheFile(dir, asm); if (p == null) return null;
      try {
        if (!File.Exists(p) || new FileInfo(p).Length > 64 * 1024) return null;
        var o = MiniJson.AsObject(MiniJson.TryParse(File.ReadAllText(p, Encoding.UTF8)));
        object v;
        if (o == null || !o.TryGetValue("asm", out v) || !string.Equals(v as string, asm, StringComparison.OrdinalIgnoreCase)) return null;
        var s = o.TryGetValue("slot", out v) ? MiniJson.AsObject(v) : null; if (s == null) return null;
        var res = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var kv in s) {
          if (!(kv.Value is double)) return null;
          double d = (double)kv.Value;
          if (d != Math.Floor(d) || d <= 0 || d > 0x40000000) return null;
          res[kv.Key] = (ulong)d;
        }
        return res;
      } catch (Exception) { return null; }
    }

    /// <summary>Saves the slots (tmp file, then move). False when not written.</summary>
    public static bool Save(string dir, string asm, IDictionary<string, ulong> slots) {
      string p = CacheFile(dir, asm); if (p == null || slots == null) return false;
      try {
        var sb = new StringBuilder("{\"asm\":" + MiniJson.Quote(asm.ToLowerInvariant()) + ",\"slot\":{");
        bool first = true;
        foreach (var kv in slots) {
          if (!first) sb.Append(',');
          first = false;
          sb.Append(MiniJson.Quote(kv.Key)).Append(':').Append(kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append("}}");
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        string tmp = p + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        if (File.Exists(p)) File.Delete(p);
        File.Move(tmp, p);
        return true;
      } catch (Exception) { return false; }
    }
  }
}
