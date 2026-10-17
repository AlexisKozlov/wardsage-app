// RealmForge extractor - settings in %APPDATA%\Wardsage\config.json.
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
    const string OldSite = "https://realmforge-wor.vercel.app";
    public string Code = "";
    public string Lang = SystemLang();   // until the player picks one: Windows's language (Russian for ru/uk/be/kk, else English)
    public bool SaveCopy;
    public bool AutoSync = true;    // send the account to the site by itself after gear changes and every few minutes
    public bool AutoClick;          // equip helper: open the slot, scroll the list and click the item in the game (off by default: the player turns it on)
    public bool AutoConfirm;        // ...and press «Заменить» itself (off by default: the player's explicit choice)
    public bool ErrorReports;       // send failures to the site with the journal's last lines (src/ErrorReport.cs; off by default)
    public bool Tray = true;        // minimizing hides the window in the tray (app/Tray.cs)
    public bool AutoUpdate = true;  // put a downloaded update in place by restarting in a quiet moment (HostBridge.TryAutoUpdate)
    // where the player dragged the boss coach's panel (Ctrl + drag): its centre across and its top down the game's
    // window, in fractions of it; -1 = the default place (app/BattleCoach.cs)
    public double CoachX = -1, CoachY = -1;
    public string TermsAccepted = "";   // version of the Terms of Use the player accepted on the first-run card (Config.TermsVersion)
    public const string TermsVersion = "2026-10-17";
    public bool RecButton = true;   // TEST build: the record button over the game (app/Overlay.cs)
    public bool BossHints = true;   // the boss coach's panel over the game during boss fights (app/BattleCoach.cs)
    // last successful sync (shown on the start screen; the busts of the strongest heroes decorate the banner)
    public string LastAt;                       // ISO 8601 UTC or null
    public int LastHeroes = -1, LastItems = -1, LastArtifacts = -1;
    public List<int> LastTop = new List<int>(); // base ids of the 3 strongest heroes

    const string ProtectedPrefix = "dpapi:";

    public static string SystemLang() {
      try {
        string l = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return l == "ru" || l == "uk" || l == "be" || l == "kk" ? "ru" : "en";
      } catch (Exception) { return "en"; }
    }

    public static string DefaultDir {
      get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Channel.FolderName); }
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

    public const string InstallerOptionsName = "installer-options.json";

    /// <summary>The installer leaves {"errorReports":bool,"autostart":bool,"lang":"ru"|"en"} in the settings folder (the installer never edits config.json).
    /// Read once: errorReports goes into config.json, the file is deleted whatever it held (a broken one is only logged).
    /// "autostart" is applied by the installer itself (the Run registry value), here it is only logged. Returns true when a setting was applied.</summary>
    public static bool ApplyInstallerOptions(string dir, Action<string> log) {
      if (log == null) log = delegate { };
      string f = Path.Combine(dir, InstallerOptionsName);
      try {
        if (!File.Exists(f)) return false;
        string text = null;
        try { text = File.ReadAllText(f, Encoding.UTF8); } catch (Exception e) { log("installer options: unreadable (" + e.Message + ")"); }
        try { File.Delete(f); } catch (Exception e) { log("installer options: cannot delete the file (" + e.Message + ")"); }
        var d = text == null ? null : MiniJson.AsObject(MiniJson.TryParse(text));
        object v;
        bool hasEr = d != null && d.TryGetValue("errorReports", out v) && v is bool;
        string lang = d == null ? null : MiniJson.GetString(d, "lang");
        bool hasLang = lang == "ru" || lang == "en";
        if (!hasEr && !hasLang) { log("installer options: malformed file ignored"); return false; }
        var c = Load(Path.Combine(dir, "config.json"));
        if (hasEr) c.ErrorReports = MiniJson.GetBool(d, "errorReports", false);
        if (hasLang) c.Lang = lang;   // the same field the language switch of the settings writes
        c.Save(Path.Combine(dir, "config.json"));
        object a;
        log("installer options: error reports " + (c.ErrorReports ? "on" : "off") + ", language " + (hasLang ? lang : "unchanged") + ", autostart " + (d.TryGetValue("autostart", out a) && a is bool ? ((bool)a ? "on" : "off") : "?") + " (chosen in the installer)");
        return true;
      } catch (Exception e) { log("installer options: " + e.Message); return false; }
    }

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
      sb.Append(",\n  \"errorReports\": ").Append(ErrorReports ? "true" : "false");
      sb.Append(",\n  \"autoUpdate\": ").Append(AutoUpdate ? "true" : "false");
      sb.Append(",\n  \"tray\": ").Append(Tray ? "true" : "false");
      sb.Append(",\n  \"termsAccepted\": ").Append(MiniJson.Quote(TermsAccepted ?? ""));
      sb.Append(",\n  \"recButton\": ").Append(RecButton ? "true" : "false");
      sb.Append(",\n  \"bossHints\": ").Append(BossHints ? "true" : "false");
      if (CoachX >= 0 && CoachY >= 0)
        sb.Append(",\n  \"coach\": [").Append(CoachX.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
          .Append(", ").Append(CoachY.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append("]");
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
      // the site moved from realmforge-wor.vercel.app: an old saved address follows (written on the next Save)
      string siteErr;
      string norm = SyncClient.NormalizeSite(site, out siteErr);
      if (norm != null && string.Equals(norm, OldSite, StringComparison.OrdinalIgnoreCase)) c.Site = SyncClient.DefaultSite;
      string lang = MiniJson.GetString(d, "lang");
      c.Lang = lang == "en" ? "en" : lang == "ru" ? "ru" : SystemLang();
      c.SaveCopy = MiniJson.GetBool(d, "saveCopy", false);
      c.AutoSync = MiniJson.GetBool(d, "autoSync", true);
      c.AutoClick = MiniJson.GetBool(d, "autoClick", false);
      c.AutoConfirm = MiniJson.GetBool(d, "autoConfirm", false);
      c.ErrorReports = MiniJson.GetBool(d, "errorReports", false);
      c.AutoUpdate = MiniJson.GetBool(d, "autoUpdate", true);
      c.Tray = MiniJson.GetBool(d, "tray", true);
      c.BossHints = MiniJson.GetBool(d, "bossHints", true);
      c.RecButton = MiniJson.GetBool(d, "recButton", true);
      c.TermsAccepted = MiniJson.GetString(d, "termsAccepted") ?? "";
      object lv;
      var co = d.TryGetValue("coach", out lv) ? lv as List<object> : null;
      if (co != null && co.Count == 2 && co[0] is double && co[1] is double) {
        double x = (double)co[0], y = (double)co[1];
        if (x >= 0 && x <= 1 && y >= 0 && y <= 1) { c.CoachX = x; c.CoachY = y; }
      }
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
