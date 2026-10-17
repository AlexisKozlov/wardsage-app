// Wardsage - layout data from the server for the running game version: GET {site}/api/extractor/app-layout?game=<version>
// with Authorization: Bearer <sync code> (without a code nothing is asked).
//   200 {"game":"<version>","v":1,"rva":{class: n},"const":{name: n},"span":{name: n},"size":{name: n},"hdr":{name: n},"form":{name: n},"field":{name: n}}
//   404 = no data for that version (yet); 401/403 = the code is not (or no longer) valid: asked again later.
// The reply is kept per game version in the app's data folder (layout\<version>.json) and used while the site cannot be
// reached. Without data for the running game version the features that need it stay off: fight recording and the boss
// coach, the arena watch, the game's screens for the equip helper, «Найти в игре», the storage cleanup and the recast
// screens. The account sync does not need it (it finds the game's tables by their key names).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  /// <summary>The layout data of one game version (immutable once parsed).</summary>
  public sealed class GameLayout {
    public const int Format = 1;
    const int MaxBytes = 64 * 1024;

    // what the app reads; a reply without one of these is not used (the classes are optional: one the data has no entry
    // for is recognised by its name on the objects read)
    public static readonly string[] NeedConst = {
      "ESimulationStatus.Runing", "ESimulationStatus.End",
      "EComponentType.Dead", "EComponentType.Attribute", "EComponentType.Skill", "EComponentType.Position", "EComponentType.Anger",
      "EComponentType.Card", "EComponentType.OwnerRelation", "EComponentType.WaveInfo", "EComponentType.Map", "EComponentType.Statistics",
      "Fix64.ONE", "ECardEntityType.Normal", "ECardStatus.Reborn", "EAngerStatus.Up", "EAngerStatus.Down",
      "EDeadType.Retreat", "EDeadType.ForceRetreat",
      "FrameCommand.SYNC_PUT_TOWER", "FrameCommand.SYNC_RETREAT_TOWER", "FrameCommand.SYNC_USE_POWERSKILL" };
    public static readonly string[] NeedSpan = {
      "GameSimulation", "DLNode", "BattlePlayerInfo", "MapMarker", "CardComponent", "AngerComponent", "FrameIdxInfo",
      "DamageStatisticsData", "CmdHeroFight" };
    public static readonly string[] NeedSize = { "FixVector3", "SdpPair", "DictEntryRef", "DictEntryInt" };
    public static readonly string[] NeedHdr = { "arrBounds", "arrLen", "arrData", "boundsLen", "boundsStride", "klass", "ptr" };
    public static readonly string[] NeedForm = {
      "ID_FORM_BATTLE", "ID_FORM_CHARACTORMAIN", "ID_FORM_BACKPACKINTEGRATION", "ID_FORM_EQUIPSUBLIMING", "ID_FORM_EQUIPGEMMAIN",
      "ID_FORM_PVPMAIN", "ID_FORM_UNIONBOSSBATTLEEND", "ID_FORM_UNIONWEEKBOSSBATTLEEND", "ID_FORM_BATTLEEND", "ID_FORM_PVPBATTLEEND",
      "ID_FORM_SPARRINGPVPBATTLEEND" };

    /// <summary>Field offsets the app reads when the data has them (the "field" section, optional: without one the feature that
    /// needs it falls back to the picture). The list grid's fields: columns, row size, the cells the grid has on the screen.</summary>
    public static readonly string[] FieldKeys = {
      "InfinityGrid.CellPerLine", "InfinityGrid.LineSize", "InfinityGrid.m_finalLineSize",
      "InfinityGrid.m_crtFirstInstanceIndex", "InfinityGrid.m_crtLastInstanceIndex", "InfinityGrid.m_fitScale" };

    public string Game { get; private set; }
    readonly Dictionary<string, long> rva = new Dictionary<string, long>(), cons = new Dictionary<string, long>(),
      span = new Dictionary<string, long>(), size = new Dictionary<string, long>(), hdr = new Dictionary<string, long>(),
      form = new Dictionary<string, long>(), field = new Dictionary<string, long>();

    GameLayout() { }

    /// <summary>A game version as the game names it: digits and dots ("1.6.39.829.1").</summary>
    public static bool IsVersion(string v) { return v != null && v.Length <= 40 && Regex.IsMatch(v, @"^\d{1,4}(\.\d{1,6}){1,5}$"); }

    /// <summary>The data from the site's reply or the saved copy; null when it is not for <paramref name="game"/>, not
    /// this format, or lacks something the app reads.</summary>
    public static GameLayout Parse(string json, string game) {
      if (string.IsNullOrEmpty(json) || json.Length > MaxBytes || !IsVersion(game)) return null;
      var o = MiniJson.AsObject(MiniJson.TryParse(json));
      if (o == null) return null;
      object v;
      if (!o.TryGetValue("game", out v) || !(v is string) || (string)v != game) return null;
      if (!o.TryGetValue("v", out v) || !(v is double) || (double)v != Format) return null;
      var l = new GameLayout { Game = game };
      if (!Section(o, "rva", l.rva) || !Section(o, "const", l.cons) || !Section(o, "span", l.span) || !Section(o, "size", l.size)
          || !Section(o, "hdr", l.hdr) || !Section(o, "form", l.form)) return null;
      if (o.ContainsKey("field") && !Section(o, "field", l.field)) return null;
      if (!Has(l.cons, NeedConst) || !Has(l.span, NeedSpan) || !Has(l.size, NeedSize) || !Has(l.hdr, NeedHdr) || !Has(l.form, NeedForm)) return null;
      foreach (var kv in l.span) if (kv.Value <= 0 || kv.Value > 0x10000) return null;
      foreach (var kv in l.size) if (kv.Value <= 0 || kv.Value > 0x1000) return null;
      foreach (var kv in l.hdr) if (kv.Value < 0 || kv.Value > 0x100) return null;
      foreach (var kv in l.field) if (kv.Value < 0 || kv.Value > 0x1000) return null;
      foreach (var kv in l.rva) if (kv.Value <= 0 || kv.Value > 0x40000000) return null;
      return l;
    }

    static bool Section(Dictionary<string, object> o, string name, Dictionary<string, long> into) {
      object v;
      var s = o.TryGetValue(name, out v) ? MiniJson.AsObject(v) : null;
      if (s == null) return false;
      foreach (var kv in s) {
        if (!(kv.Value is double)) return false;
        double d = (double)kv.Value;
        if (d != Math.Floor(d) || Math.Abs(d) > 1e15) return false;
        into[kv.Key] = (long)d;
      }
      return true;
    }
    static bool Has(Dictionary<string, long> d, string[] keys) { foreach (var k in keys) if (!d.ContainsKey(k)) return false; return true; }

    static long Get(Dictionary<string, long> d, string section, string key) {
      long v;
      if (!d.TryGetValue(key, out v)) throw new KeyNotFoundException("layout " + section + " " + key);
      return v;
    }

    /// <summary>A class's address in GameAssembly.dll (0 = none: recognised by its name).</summary>
    public ulong Rva(string cls) { long v; return rva.TryGetValue(cls, out v) ? (ulong)v : 0; }
    public int Const(string key) { return (int)Get(cons, "const", key); }
    public int Span(string key) { return (int)Get(span, "span", key); }
    public int Size(string key) { return (int)Get(size, "size", key); }
    public int Hdr(string key) { return (int)Get(hdr, "hdr", key); }
    public long Form(string key) { return Get(form, "form", key); }
    /// <summary>A form id the data may lack (older data): 0 = none.</summary>
    public long FormOpt(string key) { long v; return form.TryGetValue(key, out v) ? v : 0; }
    /// <summary>A field offset from the optional "field" section; -1 = the data has none (the feature then falls back).</summary>
    public int FieldOffset(string key) { long v; return field.TryGetValue(key, out v) ? (int)v : -1; }
  }

  /// <summary>The layout data in force (the running game version's), fetched from the site and kept on disk.</summary>
  public static class Layout {
    static volatile GameLayout current;

    /// <summary>The data for the running game version, null = none (the features that need it are off).</summary>
    public static GameLayout Current { get { return current; } }
    public static void Use(GameLayout l) { current = l; }

    public static string CacheDir { get { return Path.Combine(AppConfig.DefaultDir, "layout"); } }
    static string CachePath(string dir, string game) { return Path.Combine(dir, game + ".json"); }

    /// <summary>The saved copy for <paramref name="game"/>, or null (none, unreadable, not valid).</summary>
    public static GameLayout LoadCache(string dir, string game) {
      if (!GameLayout.IsVersion(game)) return null;
      try { string p = CachePath(dir, game); if (File.Exists(p)) return GameLayout.Parse(File.ReadAllText(p, Encoding.UTF8), game); } catch (Exception) { }
      return null;
    }

    public static void SaveCache(string dir, string game, string json) {
      if (!GameLayout.IsVersion(game)) return;
      try {
        Directory.CreateDirectory(dir);
        string path = CachePath(dir, game), tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
      } catch (Exception) { }
    }

    /// <summary>What a reply means: Got (the data, in <paramref name="layout"/>), None (the site has none for this version),
    /// Denied (no valid sync code: none entered, or the site refused it with 401/403) or NoReply (no connection, an error,
    /// something odd: what is held stays).</summary>
    public enum Reply { Got, None, Denied, NoReply }

    public static Reply Interpret(int code, string body, string game, out GameLayout layout) {
      layout = null;
      if (code == 404) return Reply.None;
      if (code == 401 || code == 403) return Reply.Denied;
      if (code < 200 || code >= 300) return Reply.NoReply;
      layout = GameLayout.Parse(body, game);
      return layout != null ? Reply.Got : Reply.NoReply;
    }

    /// <summary>Asks the site for <paramref name="game"/>'s data with the sync code; <paramref name="json"/> = the reply's
    /// text when it is Got.</summary>
    public static Reply Fetch(string site, string code, string game, out GameLayout layout, out string json) {
      layout = null; json = null;
      if (!GameLayout.IsVersion(game) || string.IsNullOrEmpty(site)) return Reply.NoReply;
      if (!SyncClient.IsValidCode(code)) return Reply.Denied;
      try {
        SyncClient.EnableTls12();
        var req = (HttpWebRequest)WebRequest.Create(site.TrimEnd('/') + "/api/extractor/app-layout?game=" + Uri.EscapeDataString(game));
        req.Method = "GET";
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Headers["Authorization"] = "Bearer " + code;
        req.Timeout = 10000;
        req.ReadWriteTimeout = 10000;
        req.AllowAutoRedirect = false;
        HttpWebResponse resp;
        try { resp = (HttpWebResponse)req.GetResponse(); }
        catch (WebException e) {
          resp = e.Response as HttpWebResponse;
          if (resp == null) return Reply.NoReply;
        }
        using (resp)
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream()) {
          var buf = new byte[8192]; int n;
          while ((n = s.Read(buf, 0, buf.Length)) > 0) {
            ms.Write(buf, 0, n);
            if (ms.Length > 64 * 1024) return Reply.NoReply;
          }
          string body = Encoding.UTF8.GetString(ms.ToArray());
          var r = Interpret((int)resp.StatusCode, body, game, out layout);
          if (r == Reply.Got) json = body;
          return r;
        }
      } catch (Exception) { return Reply.NoReply; }
    }

    /// <summary>Minutes between two asks while there is no data for the running version.</summary>
    public static int RetryMinutes(bool testBuild) { return testBuild ? 2 : 10; }
  }
}
