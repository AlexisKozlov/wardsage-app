// Battle study: dumps the account (as a sync reads it) and every battle hero the game still holds in memory
// (MTTDProto.CmdHeroFight with the server's mAttr) - READ-ONLY, like the extractor. Needs to run elevated (the game is).
//
//   BattleDump.exe [out folder]   -> account.json, hero_fights.jsonl
// Compare with: (realmforge-web) npx tsx scripts/battle-attr-check.ts <out folder>
using System;
using System.IO;
using RealmForge;

static class BattleDump {
  static int Main(string[] args) {
    string dir = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
    Directory.CreateDirectory(dir);
    try {
      var acc = RFX.Run();
      if (acc == null) { File.WriteAllText(Path.Combine(dir, "error.txt"), "account: " + RFX.LastError + " " + RFX.LastOpenError); return 1; }
      File.WriteAllText(Path.Combine(dir, "account.json"), acc);
      var fights = RFX.DumpHeroFights() ?? "";
      File.WriteAllText(Path.Combine(dir, "hero_fights.jsonl"), fights);
      File.WriteAllText(Path.Combine(dir, "log.txt"), RFX.Log.ToString());
      File.WriteAllText(Path.Combine(dir, "done.txt"), "ok " + fights.Split('\n').Length);
      return 0;
    } catch (Exception e) {
      File.WriteAllText(Path.Combine(dir, "error.txt"), e.ToString());
      return 2;
    }
  }
}
