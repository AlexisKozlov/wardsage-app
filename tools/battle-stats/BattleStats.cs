// Battle study (development only): the heroes' battle statistics and the battle's length after a battle, read from the
// game's memory (read-only; run elevated like the game). Compare with: realmforge-web scripts/battle-dps-check.ts.
//   BattleStats.exe <out folder>   -> account.json, battle_stats.jsonl
using System;
using System.IO;
using RealmForge;

static class BattleStats {
  static int Main(string[] args) {
    string dir = args.Length > 0 ? args[0] : ".";
    Directory.CreateDirectory(dir);
    try {
      var acc = RFX.Run();
      if (acc != null) File.WriteAllText(Path.Combine(dir, "account.json"), acc);
      File.WriteAllText(Path.Combine(dir, "battle_stats.jsonl"), RFX.DumpBattleStats() ?? "");
      File.WriteAllText(Path.Combine(dir, "done.txt"), "ok");
    } catch (Exception e) { File.WriteAllText(Path.Combine(dir, "error.txt"), e.ToString()); }
    return 0;
  }
}
