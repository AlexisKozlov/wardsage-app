// RealmForge — the arena and boss fights' detailed record (the v3 timeline: "dmgT", "units", src/TimelineBuilder.cs) to the site, once
// per fight. The snapshot upload strips the trace (BattleCapture.BattlesJson); here the whole kept fight file goes, gzipped.
//
// Contract (implemented by the site, lib/battleTrace/handler.ts):
//   POST {site}/api/extractor/trace      Authorization: Bearer <sync code>, X-RF-Player
//        body {v:1, at:"ISO time of the fight", stage:<7-digit stage id>, data:"<base64 of the gzipped fight file>"}
//        200 {ok:true, stored:true|false}   401 invalid_token   422 invalid_payload   429 too_many (300 a day per account)
// Fights already sent are listed by file name in trace-sent.txt (the app's data dir); unsent arena and boss v3 fights of the last
// 3 days are retried (at most 8 per run). Nothing here may throw into the recorder: every failure is logged and left.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace RealmForge {
  /// <summary>The file names of the fights already sent (one per line).</summary>
  public sealed class TraceSentList {
    readonly string path;
    readonly object gate = new object();
    readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public TraceSentList(string path) {
      this.path = path;
      if (path == null) return;
      try { if (File.Exists(path)) foreach (var l in File.ReadAllLines(path, Encoding.UTF8)) { var n = l.Trim(); if (n.Length > 0) names.Add(n); } }
      catch (Exception) { }   // a broken file only loses the marks
    }

    public bool Has(string name) { lock (gate) return names.Contains(name); }
    public int Count { get { lock (gate) return names.Count; } }

    /// <summary>Marks a fight sent; the list keeps the newest 500 names (file names sort by time).</summary>
    public void Add(string name) {
      lock (gate) {
        if (!names.Add(name)) return;
        if (names.Count > 500) { var all = new List<string>(names); all.Sort(StringComparer.Ordinal); for (int i = 0; i < all.Count - 500; i++) names.Remove(all[i]); }
        Save();
      }
    }

    void Save() {
      if (path == null) return;
      try {
        var all = new List<string>(names); all.Sort(StringComparer.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, string.Join("\n", all.ToArray()) + "\n", new UTF8Encoding(false));
      } catch (Exception) { }
    }
  }

  public static class TraceUpload {
    /// <summary>The site takes at most 2 MB of the decoded (gzipped) data.</summary>
    public const int MaxGzBytes = 2 * 1024 * 1024;
    public const int MaxPerRun = 8, MaxAgeDays = 3;   // 8: arena and boss fights share the run; the site takes 300 a day
    static int running;

    public static string SentPath { get { return Path.Combine(AppConfig.DefaultDir, "trace-sent.txt"); } }

    /// <summary>An arena stage (attack 6001xxx, the defence run 6002xxx).</summary>
    public static bool IsArenaStage(int stage) { return stage / 1000 == 6001 || stage / 1000 == 6002; }

    /// <summary>A boss fight: the guild boss result screens (the classic and the weekly boss), or a known boss stage
    /// (5001xxx / 5002xxx / 5201xxx guild bosses, 4601xxx the two-headed boss).</summary>
    public static bool IsBossFight(int stage, string screen) {
      if (screen == "Form_UnionBossBattleEnd" || screen == "Form_UnionWeekBossBattleEnd") return true;
      int g = stage / 1000;
      return g == 5001 || g == 5002 || g == 5201 || g == 4601;
    }

    /// <summary>The request body for a kept fight file's JSON, or null with the reason: neither an arena nor a boss fight
    /// (no stage, or another stage), no timeline v3 with a trace ("units", "dmgT" or the heroes' skill damage), or over the size limit.</summary>
    public static string BuildPayload(string fileJson, out string skip) {
      skip = null;
      var o = MiniJson.AsObject(MiniJson.TryParse(fileJson));
      if (o == null) { skip = "not a fight file"; return null; }
      int stage = Count(o, "stage");
      if (stage < 1000000 || stage > 9999999 || (!IsArenaStage(stage) && !IsBossFight(stage, MiniJson.GetString(o, "screen")))) { skip = "not an arena or boss fight"; return null; }
      var tl = Obj(o, "timeline");
      if (tl == null || Count(tl, "v") < 3) { skip = "no timeline v3"; return null; }
      if (!HasEntries(tl, "units") && !HasEntries(tl, "dmgT") && !HasSkills(tl)) { skip = "no trace in the timeline"; return null; }
      string at = MiniJson.GetString(o, "at");
      DateTime t;
      if (string.IsNullOrEmpty(at) || !DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) { skip = "no time"; return null; }
      byte[] gz = Gzip(Encoding.UTF8.GetBytes(fileJson));
      if (gz.Length > MaxGzBytes) { skip = "too large (" + gz.Length + " bytes gzipped)"; return null; }
      return "{\"v\":1,\"at\":\"" + t.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\",\"stage\":" + stage.ToString(CultureInfo.InvariantCulture)
        + ",\"data\":\"" + Convert.ToBase64String(gz) + "\"}";
    }

    public static byte[] Gzip(byte[] raw) {
      using (var ms = new MemoryStream()) {
        using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
      }
    }

    static Dictionary<string, object> Obj(Dictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) ? MiniJson.AsObject(v) : null; }
    static int Count(Dictionary<string, object> o, string key) { object v; if (!o.TryGetValue(key, out v) || v == null) return 0; try { return (int)Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch (Exception) { return 0; } }
    static bool HasSkills(Dictionary<string, object> tl) {
      var arr = tl.ContainsKey("stats") ? tl["stats"] as System.Collections.IList : null;
      if (arr == null) return false;
      foreach (var x in arr) { var o = MiniJson.AsObject(x); if (o != null && HasEntries(o, "skills")) return true; }
      return false;
    }
    static bool HasEntries(Dictionary<string, object> o, string key) { var d = Obj(o, key); return d != null && d.Count > 0; }

    /// <summary>The kept fight files not sent yet and not older than <paramref name="maxAgeDays"/> (by the file name's time,
    /// yyyyMMdd-HHmmss), the newest first, at most <paramref name="max"/>. The subfolders (accounts) are not looked at.</summary>
    public static List<string> Pending(string dir, TraceSentList sent, DateTime nowLocal, int maxAgeDays, int max) {
      var res = new List<string>();
      if (!Directory.Exists(dir)) return res;
      var files = Directory.GetFiles(dir, "*.json"); Array.Sort(files, StringComparer.Ordinal); Array.Reverse(files);
      foreach (var f in files) {
        if (res.Count >= max) break;
        string name = Path.GetFileName(f);
        if (sent.Has(name)) continue;
        DateTime at;
        if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out at)) continue;
        if (at < nowLocal.AddDays(-maxAgeDays)) continue;
        res.Add(f);
      }
      return res;
    }

    /// <summary>One run: sends the unsent arena and boss v3 fights of the last 3 days (at most 8). Safe to call from any thread; a
    /// second call while one runs does nothing. Returns how many fights were stored. <paramref name="log"/> gets a line per fight.</summary>
    public static int RunOnce(string dir, TraceSentList sent, string site, string code, Action<string> log) {
      if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return 0;
      int ok = 0;
      try {
        if (!SyncClient.IsValidCode(code) || string.IsNullOrEmpty(site)) return 0;
        foreach (var f in Pending(dir, sent, DateTime.Now, MaxAgeDays, MaxPerRun)) {
          string name = Path.GetFileName(f);
          try {
            string skip;
            string body = BuildPayload(File.ReadAllText(f, Encoding.UTF8), out skip);
            if (body == null) {
              // not an arena or boss v3 fight, or too large: nothing to send now or later
              if (skip != "not an arena or boss fight" && skip != "no timeline v3" && skip != "no trace in the timeline") log("трасса боя не отправлена (" + name + "): " + skip);
              sent.Add(name);
              continue;
            }
            int http; string text, err;
            if (!PlansClient.Send("POST", site + "/api/extractor/trace", code, body, out http, out text, out err)) { log("трасса боя: ошибка " + name + ": " + err); break; }
            var j = MiniJson.AsObject(MiniJson.TryParse(text));
            if (http >= 200 && http < 300 && j != null && MiniJson.GetBool(j, "ok", false)) {
              sent.Add(name); ok++;
              log("трасса боя отправлена: " + name + " (" + body.Length + " байт" + (MiniJson.GetBool(j, "stored", true) ? "" : ", уже была на сайте") + ")");
            } else if (http == 422) {
              // the site refused this fight itself: sending it again changes nothing
              sent.Add(name);
              log("трасса боя: ошибка " + name + ": сайт отклонил (" + Brief(text) + ")");
            } else {
              log("трасса боя: ошибка " + name + ": HTTP " + http + " " + Brief(text));
              break;   // 401 / 429 / 5xx: the rest would fail the same way; the next run tries again
            }
          } catch (Exception e) { log("трасса боя: ошибка " + name + ": " + e.Message); break; }
        }
      } catch (Exception e) { try { log("трасса боя: ошибка " + e.Message); } catch (Exception) { } }
      finally { Interlocked.Exchange(ref running, 0); }
      return ok;
    }

    static string Brief(string text) { text = (text ?? "").Replace('\n', ' '); return text.Length > 160 ? text.Substring(0, 160) : text; }
  }
}
