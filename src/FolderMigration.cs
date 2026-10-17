// One-time move of the settings folders from the old product name (%APPDATA%\RealmForge, %LOCALAPPDATA%\RealmForge)
// to the new one. A COPY: the old folder is never deleted or changed. Pure file logic with the two paths as arguments,
// so the tests can run it in a temp folder.
using System;
using System.Collections.Generic;
using System.IO;

namespace RealmForge {
  public static class FolderMigration {
    public const string MarkerName = "migrated-from-realmforge.txt";

    // Folders that are not copied: they are rebuilt by the app itself (unpacked page of a build, staged updates).
    static readonly string[] SkipTop = { "app", "update" };

    /// <summary>Copies <paramref name="oldDir"/> into <paramref name="newDir"/> once. Returns true when it copied.
    /// Does nothing when the old folder is missing, or when the new one already has config.json or the marker
    /// (the new folder wins). Files that are locked or fail to copy are skipped and logged.</summary>
    public static bool Run(string oldDir, string newDir, Action<string> log) {
      if (log == null) log = delegate { };
      try {
        if (string.IsNullOrEmpty(oldDir) || string.IsNullOrEmpty(newDir)) return false;
        if (string.Equals(Path.GetFullPath(oldDir).TrimEnd('\\', '/'), Path.GetFullPath(newDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
        if (!Directory.Exists(oldDir)) { log("folders: no old folder " + oldDir + ", nothing to move"); return false; }
        if (File.Exists(Path.Combine(newDir, "config.json")) || File.Exists(Path.Combine(newDir, MarkerName))) {
          log("folders: " + newDir + " already in use, the old folder is ignored");
          return false;
        }
        log("folders: copying " + oldDir + " -> " + newDir);
        Directory.CreateDirectory(newDir);
        int copied = 0, skipped = 0;
        CopyDir(oldDir, newDir, true, log, ref copied, ref skipped);
        File.WriteAllText(Path.Combine(newDir, MarkerName),
          "Copied from " + oldDir + " on " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC. The old folder was left as it was." + Environment.NewLine);
        log("folders: done, " + copied + " files copied, " + skipped + " skipped");
        return true;
      } catch (Exception e) { log("folders: migration failed: " + e.Message); return false; }
    }

    static void CopyDir(string from, string to, bool top, Action<string> log, ref int copied, ref int skipped) {
      Directory.CreateDirectory(to);
      foreach (var f in Directory.GetFiles(from)) {
        string name = Path.GetFileName(f);
        string dest = Path.Combine(to, name);
        try {
          if (File.Exists(dest)) continue;
          File.Copy(f, dest);
          copied++;
        } catch (Exception e) { skipped++; log("folders: skipped " + f + " (" + e.Message + ")"); }
      }
      foreach (var d in Directory.GetDirectories(from)) {
        string name = Path.GetFileName(d);
        if (top && Array.IndexOf(SkipTop, name.ToLowerInvariant()) >= 0) continue;
        try { CopyDir(d, Path.Combine(to, name), false, log, ref copied, ref skipped); }
        catch (Exception e) { skipped++; log("folders: skipped " + d + " (" + e.Message + ")"); }
      }
    }
  }
}
