// RealmForge extractor - settings in %APPDATA%\RealmForge\config.json.
//
// {"site":"https://realmforge-wor.vercel.app","lang":"ru","saveCopy":false,"code":"dpapi:<base64>"}
// The sync code is never stored in plain text: it is encrypted with Windows DPAPI for the current
// user (see CodeProtector), so the file is useless on another PC or under another Windows account.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RealmForge {
  public sealed class AppConfig {
    public string Site = SyncClient.DefaultSite;
    public string Code = "";
    public string Lang = "ru";
    public bool SaveCopy;
    public bool AutoSync = true;    // send the account to the site by itself after gear changes and every few minutes
    public bool AutoClick = true;   // equip helper: open the slot, scroll the list and click the item in the game
    public bool AutoConfirm;        // ...and press «Заменить» itself (off by default: the player's explicit choice)
    // last successful sync (shown on the start screen; the busts of the strongest heroes decorate the banner)
    public string LastAt;                       // ISO 8601 UTC or null
    public int LastHeroes = -1, LastItems = -1, LastArtifacts = -1;
    public List<int> LastTop = new List<int>(); // base ids of the 3 strongest heroes

    const string ProtectedPrefix = "dpapi:";

    public static string DefaultDir {
      get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RealmForge"); }
    }
    public static string DefaultPath { get { return Path.Combine(DefaultDir, "config.json"); } }

    public static AppConfig Load() { return Load(DefaultPath); }

    // A missing or broken file gives the defaults: settings must never stop the extractor from starting.
    public static AppConfig Load(string path) {
      try { if (File.Exists(path)) return FromJson(File.ReadAllText(path, Encoding.UTF8)); }
      catch (Exception) { }
      return new AppConfig();
    }

    public void Save() { Save(DefaultPath); }

    public void Save(string path) {
      Directory.CreateDirectory(Path.GetDirectoryName(path));
      string tmp = path + ".tmp";
      File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
      if (File.Exists(path)) File.Delete(path);
      File.Move(tmp, path);
    }

    public string ToJson() {
      var sb = new StringBuilder();
      sb.Append("{\n  \"site\": ").Append(MiniJson.Quote(Site ?? ""));
      sb.Append(",\n  \"lang\": ").Append(MiniJson.Quote(Lang == "en" ? "en" : "ru"));
      sb.Append(",\n  \"saveCopy\": ").Append(SaveCopy ? "true" : "false");
      sb.Append(",\n  \"autoSync\": ").Append(AutoSync ? "true" : "false");
      sb.Append(",\n  \"autoClick\": ").Append(AutoClick ? "true" : "false");
      sb.Append(",\n  \"autoConfirm\": ").Append(AutoConfirm ? "true" : "false");
      string code = string.IsNullOrEmpty(Code) ? "" : ProtectedPrefix + CodeProtector.Protect(Code);
      sb.Append(",\n  \"code\": ").Append(MiniJson.Quote(code));
      if (!string.IsNullOrEmpty(LastAt)) {
        sb.Append(",\n  \"last\": {\"at\": ").Append(MiniJson.Quote(LastAt));
        sb.Append(", \"heroes\": ").Append(LastHeroes).Append(", \"items\": ").Append(LastItems).Append(", \"artifacts\": ").Append(LastArtifacts);
        sb.Append(", \"top\": [");
        for (int i = 0; i < LastTop.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(LastTop[i]); }
        sb.Append("]}");
      }
      sb.Append("\n}\n");
      return sb.ToString();
    }

    public static AppConfig FromJson(string json) {
      var c = new AppConfig();
      var d = MiniJson.AsObject(MiniJson.TryParse(json));
      if (d == null) return c;
      string site = MiniJson.GetString(d, "site");
      if (!string.IsNullOrEmpty(site)) c.Site = site;
      c.Lang = MiniJson.GetString(d, "lang") == "en" ? "en" : "ru";
      c.SaveCopy = MiniJson.GetBool(d, "saveCopy", false);
      c.AutoSync = MiniJson.GetBool(d, "autoSync", true);
      c.AutoClick = MiniJson.GetBool(d, "autoClick", true);
      c.AutoConfirm = MiniJson.GetBool(d, "autoConfirm", false);
      object lv;
      var last = d.TryGetValue("last", out lv) ? MiniJson.AsObject(lv) : null;
      if (last != null && MiniJson.GetString(last, "at") != null) {
        c.LastAt = MiniJson.GetString(last, "at");
        c.LastHeroes = MiniJson.GetCount(last, "heroes");
        c.LastItems = MiniJson.GetCount(last, "items");
        c.LastArtifacts = MiniJson.GetCount(last, "artifacts");
        object tv; var top = last.TryGetValue("top", out tv) ? tv as List<object> : null;
        if (top != null) foreach (var x in top) if (x is double && (double)x > 0 && (double)x < 1e7 && c.LastTop.Count < 3) c.LastTop.Add((int)(double)x);
      }
      string code = MiniJson.GetString(d, "code");
      if (code != null && code.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) {
        try { c.Code = CodeProtector.Unprotect(code.Substring(ProtectedPrefix.Length)) ?? ""; }
        catch (Exception) { c.Code = ""; }   // other user / other PC: just ask for the code again
      }
      return c;
    }
  }
}
