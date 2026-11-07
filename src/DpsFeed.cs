// The live DPS meter's data (no WinForms): what the fight recorder hands over each second.
using System;
using System.Collections.Generic;

namespace RealmForge {
  /// <summary>One reading of the fight's statistics (FightRecorder.OnStats): about every second, and once at the end (Final).</summary>
  public sealed class DpsTick {
    public int Stage; public uint Frame; public bool Final;
    public List<StatLine> Stats;
    /// <summary>"controller:unit" -> skill id -> amount; null when not read.</summary>
    public Dictionary<string, Dictionary<int, long>> Skills;
  }
}
