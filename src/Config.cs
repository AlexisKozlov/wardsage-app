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
    public bool ErrorReports;       // send failures to the site with the journal's last lines (src/ErrorReport.cs; off by default)
    public bool Tray = true;        // minimizing hides the window in the tray (app/Tray.cs)
    public bool AutoUpdate = true;  // put a downloaded update in place by restarting in a quiet moment (HostBridge.TryAutoUpdate)
    // where the player dragged the boss coach's panel (Ctrl + drag): its centre across and its top down the game's
    // window, in fractions of it; -1 = the default place (app/BattleCoach.cs)
    public double CoachX = -1, CoachY = -1;
    public string TermsAccepted = "";   // version of the Terms of Use the player accepted on the first-run card (Config.TermsVersion)
    public const string TermsVersion = "2026-10-17";
    public string GuideStyle = "auto";   // TEST build: the new button highlight, "auto" (the site's switch) | "on" | "off"
    public bool RecButton = true;   // TEST build: the record button over the game (app/Overlay.cs)
    public bool SendCodes = true;   // promo codes the player enters in the game, with the game's answer, go to the site (app/CodeWatch.cs; the player can turn it off)
    public bool BossHints = true;   // the boss coach's panel over the game during boss fights (app/BattleCoach.cs)
    public bool ShowInCapture;      // the player's choice: hint windows stay in recordings / streams / screenshots (off by default: they are hidden from captures)
    // last successful sync (shown on the start screen; the busts of the strongest heroes decorate the banner)
    public string LastAt;                       // ISO 8601 UTC or null
    public int LastHeroes = -1, LastItems = -1, LastArtifacts = -1;
    public List<int> LastTop = new List<int>(); // base ids of the 3 strongest heroes
    public long LastPlayer;   // the game account (PlayerData m_Uid) last read on this install, 0 = none (SyncClient.LastPlayer)

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
      sb.Append(",\n  \"errorReports\": ").Append(ErrorReports ? "true" : "false");
      sb.Append(",\n  \"autoUpdate\": ").Append(AutoUpdate ? "true" : "false");
      sb.Append(",\n  \"tray\": ").Append(Tray ? "true" : "false");
      sb.Append(",\n  \"termsAccepted\": ").Append(MiniJson.Quote(TermsAccepted ?? ""));
      sb.Append(",\n  \"guideStyle\": ").Append(MiniJson.Quote(GuideStyle));
      sb.Append(",\n  \"recButton\": ").Append(RecButton ? "true" : "false");
      sb.Append(",\n  \"sendCodes\": ").Append(SendCodes ? "true" : "false");
      sb.Append(",\n  \"bossHints\": ").Append(BossHints ? "true" : "false");
      sb.Append(",\n  \"showInCapture\": ").Append(ShowInCapture ? "true" : "false");
      if (CoachX >= 0 && CoachY >= 0)
        sb.Append(",\n  \"coach\": [").Append(CoachX.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
          .Append(", ").Append(CoachY.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append("]");
      if (LastPlayer > 0) sb.Append(",\n  \"lastPlayer\": ").Append(MiniJson.Quote(LastPlayer.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
      c.ErrorReports = MiniJson.GetBool(d, "errorReports", false);
      c.AutoUpdate = MiniJson.GetBool(d, "autoUpdate", true);
      c.Tray = MiniJson.GetBool(d, "tray", true);
      c.BossHints = MiniJson.GetBool(d, "bossHints", true);
      c.ShowInCapture = MiniJson.GetBool(d, "showInCapture", false);
      c.SendCodes = MiniJson.GetBool(d, "sendCodes", true);
      c.RecButton = MiniJson.GetBool(d, "recButton", true);
      string gs = MiniJson.GetString(d, "guideStyle"); c.GuideStyle = gs == "on" || gs == "off" ? gs : "auto";
      c.TermsAccepted = MiniJson.GetString(d, "termsAccepted") ?? "";
      long lp; string lps = MiniJson.GetString(d, "lastPlayer");
      if (lps != null && long.TryParse(lps, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out lp) && lp > 0) c.LastPlayer = lp;
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

namespace RealmForge {
  /// <summary>What SetWindowDisplayAffinity gets for the hint windows: 0 = captures see them, 0x11 (WDA_EXCLUDEFROMCAPTURE) = hidden.</summary>
  public static class CaptureAffinity {
    /// <summary>The window is really hidden from captures only when the call worked and the affinity is not 0; otherwise the
    /// screen looks mask our drawn parts themselves (OverlayController.MaskOwn reads !Excluded).</summary>
    public static bool Hidden(bool callOk, uint affinity) { return callOk && affinity != 0; }
    public static uint For(bool showInCapture, bool capturable) { return For(showInCapture, capturable, false); }
    /// <summary>While the auto click works (<paramref name="pilot"/>) the player's setting gives way: hidden (0x11) again, so no look that decides a
    /// click sees our windows (a look that our own hint covers is not known and the click waits: live 2026-11-04, the recorder on, the click deadlocked).
    /// The pilot wins over the test build's recorder (<paramref name="capturable"/>) and over the setting alike; without the pilot (hints only) both keep them visible.</summary>
    public static uint For(bool showInCapture, bool capturable, bool pilot) { return For(showInCapture, capturable, pilot, false); }
    /// <summary>A window that is for the player's eyes only (the recorder's timer, <paramref name="never"/>) is hidden from captures in EVERY mode: no window of ours may mask a look
    /// (live 2026-11-04, build 534: the timer lay over the hero filter strip, the look stayed unknown and the run stopped). The recorder draws no timer onto its frames.</summary>
    public static uint For(bool showInCapture, bool capturable, bool pilot, bool never) { return !never && !pilot && (capturable || showInCapture) ? 0u : 0x11u; }
    /// <summary>The dimming around a hint is drawn only while every hint window is hidden from captures (shown to a recording or to a look it would cover the whole game).</summary>
    public static bool DimOn(bool wanted, bool allHidden) { return wanted && allHidden; }
    /// <summary>The setting «show hints in recordings and streams» (off by default) counts only where the channel offers it (every channel since 1.6.68).</summary>
    public static bool Allowed(bool configOn, bool available) { return configOn && available; }
    /// <summary>A window that already shows a picture keeps its old capture state until the compositor gets a new frame (live 2026-11-04: the setting switched on
    /// in the running streamer build, the hints stayed out of screenshots until a restart): after an affinity change on a shown window the picture is presented again.</summary>
    public static bool Repaint(uint previous, uint now, bool visible, bool hasPicture) { return previous != Never && previous != now && visible && hasPicture; }
    /// <summary>The pilot's hide of the windows from captures: on at once (no look may see our hint), off only 5 s after the last flip (every flip presents the windows again).</summary>
    public static bool PilotNext(bool current, bool want, int sinceFlipMs) { return want ? true : current && sinceFlipMs < 5000; }
    /// <summary>The "no affinity applied yet" marker of a window.</summary>
    public const uint Never = 0xFFFFFFFFu;
  }

  /// <summary>The windows that exist (with a handle): a change of the setting goes to every one of them now, and a window added later gets the
  /// current state at once (<paramref name="apply"/> is called for it by Add).</summary>
  public sealed class WindowSet<T> where T : class {
    readonly System.Collections.Generic.List<T> items = new System.Collections.Generic.List<T>();
    readonly Action<T> apply;
    public WindowSet(Action<T> apply) { this.apply = apply; }
    public void Add(T w) { lock (items) { if (!items.Contains(w)) items.Add(w); } apply(w); }
    public void Remove(T w) { lock (items) items.Remove(w); }
    public T[] ToArray() { lock (items) return items.ToArray(); }
    /// <summary>Applies the current state to every window in the set (only those <paramref name="alive"/> says are usable).</summary>
    public void ApplyAll(Func<T, bool> alive) { foreach (var w in ToArray()) if (alive(w)) apply(w); }
  }
}
