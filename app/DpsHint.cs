// Wardsage app - the DPS meter's one-line hint about the Ctrl control: shown in the first fights after the install, until the player has
// used Ctrl once. The counter lives in its own small file (local, per channel), written atomically; nothing else reads it.
using System;
using System.IO;
using System.Text;

namespace RealmForge {
  sealed class DpsHintStore {
    /// <summary>The hint stays for this many fights.</summary>
    public const int MaxFights = 5;
    readonly string path;
    public int Fights;
    public bool CtrlUsed;

    public DpsHintStore(string path) { this.path = path; Load(); }

    public static string DefaultPath {
      get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Channel.FolderName, "dps-hint.json"); }
    }

    /// <summary>Show the hint: Ctrl was never used and no more than MaxFights fights were seen.</summary>
    public bool Show { get { return !CtrlUsed && Fights <= MaxFights; } }

    /// <summary>A new fight is on the screen.</summary>
    public void FightSeen() { if (Fights > MaxFights) return; Fights++; Save(); }
    /// <summary>The player held Ctrl over the game: the hint is no longer needed.</summary>
    public void UsedCtrl() { if (CtrlUsed) return; CtrlUsed = true; Save(); }

    void Load() {
      try {
        if (!File.Exists(path)) return;
        var d = MiniJson.AsObject(MiniJson.TryParse(File.ReadAllText(path, Encoding.UTF8)));
        int n = MiniJson.GetCount(d, "fights"); Fights = n < 0 ? 0 : Math.Min(n, MaxFights + 1);
        CtrlUsed = MiniJson.GetBool(d, "ctrl", false);
      } catch (Exception) { Fights = 0; CtrlUsed = false; }
    }

    void Save() {
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, "{\"fights\": " + Fights + ", \"ctrl\": " + (CtrlUsed ? "true" : "false") + "}", new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
      } catch (Exception) { }
    }
  }
}
