// The skill list of one hero in the DPS meter, grouped (no WinForms, testable): the game books damage under the ids of nested
// skills (bullets, talent effects); here they are folded into the named skill that owns them (DpsNames.SkillRoot), the small rest
// is folded into one "Other · N" row.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RealmForge {
  /// <summary>One row of the grouped skill list: the owning skill (Id) or the folded rest (Tail).</summary>
  public sealed class DpsSkillRow {
    /// <summary>The named skill that owns the row (for an unnamed group: its first id; 0 for the folded rest).</summary>
    public int Id;
    public long Amount; public double Share, Dps, Recent;
    /// <summary>How many of the raw skill ids sit in this row.</summary>
    public int Count;
    /// <summary>The folded rest (several small rows and the unknown ids): "Other · N".</summary>
    public bool Tail;

    public string Name(bool ru) {
      if (Tail) return DpsNames.Other(ru) + " · " + Count.ToString(CultureInfo.InvariantCulture);
      return DpsNames.Skill(Id, ru);
    }
  }

  public static class DpsSkillRows {
    /// <summary>Groups the raw skills of a hero (unit): sums Amount, Dps and Recent per owning skill, recomputes Share against the sum of all
    /// of them, biggest first. Rows under <paramref name="minShare"/> and the unknown ids become one "Other · N" row (when there are two or more
    /// of them; a lone small row keeps its own name). raw may be null: the result is then empty.</summary>
    public static List<DpsSkillRow> Group(int unit, List<DpsSkill> raw, double minShare = 0.01) {
      var rows = new List<DpsSkillRow>();
      if (raw == null) return rows;
      var byKey = new Dictionary<string, DpsSkillRow>();
      long total = 0;
      foreach (var s in raw) {
        total += s.Amount;
        int root = DpsNames.SkillRoot(unit, s.Id);
        string key;
        if (DpsNames.KnownSkill(root)) key = "s" + root.ToString(CultureInfo.InvariantCulture);
        else {
          string cat = DpsNames.Category(root, true);
          key = cat != null ? "c" + cat : "o";
        }
        DpsSkillRow r;
        if (!byKey.TryGetValue(key, out r)) { r = new DpsSkillRow { Id = root }; byKey[key] = r; rows.Add(r); }
        r.Amount += s.Amount; r.Dps += s.Dps; r.Recent += s.Recent; r.Count++;
      }
      DpsSkillRow other; byKey.TryGetValue("o", out other);
      foreach (var r in rows) r.Share = total > 0 ? r.Amount / (double)total : 0;
      var big = new List<DpsSkillRow>(); var small = new List<DpsSkillRow>();
      foreach (var r in rows) { if (r == other || r.Share < minShare) small.Add(r); else big.Add(r); }
      // two or more small rows (or the unknown ids next to a small row) fold into one; the unknown ids alone keep "Other"
      bool fold = small.Count >= 2;
      if (fold) {
        var t = new DpsSkillRow { Tail = true };
        foreach (var r in small) { t.Amount += r.Amount; t.Share += r.Share; t.Dps += r.Dps; t.Recent += r.Recent; t.Count += 1; }
        big.Sort(CompareRows);
        big.Add(t);
        return big;
      }
      big.AddRange(small);
      big.Sort(CompareRows);
      return big;
    }

    static int CompareRows(DpsSkillRow a, DpsSkillRow b) {
      int c = b.Amount.CompareTo(a.Amount);
      return c != 0 ? c : a.Id.CompareTo(b.Id);
    }
  }
}
