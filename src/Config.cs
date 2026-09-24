// RealmForge extractor - settings in %APPDATA%\RealmForge\config.json.
//
// {"site":"https://realmforge.vercel.app","lang":"ru","saveCopy":false,"code":"dpapi:<base64>"}
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
      string code = string.IsNullOrEmpty(Code) ? "" : ProtectedPrefix + CodeProtector.Protect(Code);
      sb.Append(",\n  \"code\": ").Append(MiniJson.Quote(code));
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
      string code = MiniJson.GetString(d, "code");
      if (code != null && code.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) {
        try { c.Code = CodeProtector.Unprotect(code.Substring(ProtectedPrefix.Length)) ?? ""; }
        catch (Exception) { c.Code = ""; }   // other user / other PC: just ask for the code again
      }
      return c;
    }
  }
}
