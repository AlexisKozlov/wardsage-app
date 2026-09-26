// Diagnostics: the hero screen tables and the inventory's bulk sale list in the game's memory (read-only).
using System;
using System.IO;
using System.Text;
using RealmForge;

static class FormDump {
  static int Main(string[] args) {
    string dir = args.Length > 0 ? args[0] : ".";
    Directory.CreateDirectory(dir);
    try {
      var sb = new StringBuilder(RFX.DumpForms() ?? "no game");
      var s = RFX.ReadInventory(0);
      if (s == null) sb.Append("inventory: none\n");
      else sb.Append("inventory: open=" + s.Open + " equip=" + s.EquipTab + " bulk=" + s.Bulk + " items=" + s.Uids.Count + " selected=" + s.Selected.Count + "\n");
      File.WriteAllText(Path.Combine(dir, "forms.txt"), sb.ToString());
    } catch (Exception e) { File.WriteAllText(Path.Combine(dir, "forms.txt"), e.ToString()); }
    return 0;
  }
}
