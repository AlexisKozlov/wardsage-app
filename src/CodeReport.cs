// Wardsage - promo codes: the game's answer to a code the player entered is classified and, when the setting
// "Send promo codes to the site" is on (default), reported as {code, result, toast, region, gameVersion}: nothing else.
// No account, name, uid or sync-code header goes with it. Contract (site: POST /api/codes/report, lib/codes/handler.ts):
//   body {code: 4..24 letters/digits, result: ok|invalid|expired|used|limit|notstarted|unknown, toast?: <=200, region?: <=32, gameVersion?: <=64}
//   200 {ok:true}   401 invalid_token   413/415/422 refused   429 rate_limited
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  /// <summary>The game's answer to an entered code, as the site names them.</summary>
  public static class CodeResult {
    public const string Ok = "ok", Invalid = "invalid", Expired = "expired", Used = "used", Limit = "limit", NotStarted = "notstarted", Unknown = "unknown";

    static Regex R(string p) { return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
    // checked in this order: the first group that matches wins (a message like "code already redeemed" is «used», not «ok»)
    static readonly KeyValuePair<string, Regex>[] Rules = {
      new KeyValuePair<string, Regex>(Expired, R(@"expired|out of date|no longer valid|истек|истёк|просроч|срок.{0,20}(действ|вышел|истек|истёк)|expir|abgelaufen|caduc|vencid|süresi|kedaluwarsa|hết hạn")),
      new KeyValuePair<string, Regex>(Used, R(@"already (been )?(used|redeemed|claimed|exchanged)|has been (used|redeemed|claimed|exchanged)|been (used|redeemed)|уже (был|использ|активир|получ|обмен)|использован|bereits|déjà|ya (fue|ha sido|se)|já foi|zaten|sudah digunakan|đã được")),
      new KeyValuePair<string, Regex>(Limit, R(@"limit|too many|maximum|exceed|лимит|превыш|слишком много|максимум")),
      new KeyValuePair<string, Regex>(NotStarted, R(@"not (yet )?(open|started|begun|available|active)|has not started|ещё не|еще не|не начал|не открыт")),
      new KeyValuePair<string, Regex>(Invalid, R(@"invalid|incorrect|wrong|not exist|doesn.?t exist|does not exist|not found|unknown (code|key)|неверн|недействител|не существует|не найден|ошибочн|ungültig|invalide|inválid|geçersiz|tidak valid|không hợp lệ")),
      new KeyValuePair<string, Regex>(Ok, R(@"success|redeemed|congratulations|reward|получен|успешн|поздравл|награда|erfolg|réussi|éxito|exitos|sucesso|başarı|berhasil|thành công")),
    };

    /// <summary>The result for the game's message; "unknown" when it matches nothing known (the site keeps the raw text to learn new ones).</summary>
    public static string Classify(string message) {
      if (string.IsNullOrWhiteSpace(message)) return Unknown;
      foreach (var r in Rules) if (r.Value.IsMatch(message)) return r.Key;
      return Unknown;
    }

    /// <summary>The language of a message: "ru" for Cyrillic, "en" for Latin only, null otherwise (sent as the report's region).</summary>
    public static string LanguageOf(string message) {
      if (string.IsNullOrEmpty(message)) return null;
      bool cyr = false, lat = false, other = false;
      foreach (char c in message) {
        if (c >= 'Ѐ' && c <= 'ӿ') cyr = true;
        else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) lat = true;
        else if (c > 'ɏ') other = true;
      }
      return other ? null : cyr ? "ru" : lat ? "en" : null;
    }
  }

  /// <summary>One report per code + result per 10 minutes (the letter case of the code does not matter).</summary>
  public sealed class CodeReportGate {
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();

    public bool ShouldSend(string code, string result, DateTime nowUtc) {
      if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(result)) return false;
      string key = code.ToLowerInvariant() + "|" + result;
      lock (seen) {
        if (seen.Count > 200) { var old = new List<string>(); foreach (var kv in seen) if (nowUtc - kv.Value >= Window) old.Add(kv.Key); foreach (var k in old) seen.Remove(k); }
        DateTime at;
        if (seen.TryGetValue(key, out at) && nowUtc - at < Window) return false;
        seen[key] = nowUtc;
        return true;
      }
    }
  }

  public static class CodeReportClient {
    public const int MaxToast = 160;

    /// <summary>The request body; null when the code does not look like one. Only these fields, nothing about the player.</summary>
    public static string BuildBody(string code, string result, string message, string gameVersion) {
      if (!CodeText.Looks(code)) return null;
      code = code.Trim();
      string toast = CodeText.ForLog(message);
      var sb = new StringBuilder("{\"code\":");
      sb.Append(MiniJson.Quote(code)).Append(",\"result\":").Append(MiniJson.Quote(result ?? CodeResult.Unknown));
      if (toast.Length > 0) sb.Append(",\"toast\":").Append(MiniJson.Quote(toast));
      string lang = CodeResult.LanguageOf(toast);
      if (lang != null) sb.Append(",\"region\":").Append(MiniJson.Quote(lang));
      if (!string.IsNullOrEmpty(gameVersion) && gameVersion.Length <= 64) sb.Append(",\"gameVersion\":").Append(MiniJson.Quote(gameVersion));
      return sb.Append("}").ToString();
    }

    /// <summary>POSTs the body to {site}/api/codes/report. No Authorization, no player header (anonymous). Returns the HTTP status (0 = no answer).</summary>
    public static int Send(string site, string body, out string err) {
      err = null;
      try {
        SyncClient.EnableTls12();
        ServicePointManager.Expect100Continue = false;
        var req = (HttpWebRequest)WebRequest.Create(site.TrimEnd('/') + "/api/codes/report");
        req.Method = "POST";
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Timeout = 20000; req.ReadWriteTimeout = 20000;
        req.AllowAutoRedirect = false;
        byte[] b = new UTF8Encoding(false).GetBytes(body);
        req.ContentType = "application/json";
        req.ContentLength = b.Length;
        using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
        using (var resp = (HttpWebResponse)req.GetResponse()) return (int)resp.StatusCode;
      } catch (WebException e) {
        var resp = e.Response as HttpWebResponse;
        if (resp != null) { int c = (int)resp.StatusCode; resp.Close(); return c; }
        err = e.Message; return 0;
      } catch (Exception e) { err = e.Message; return 0; }
    }
  }
}
