// Wardsage - promo codes: the game's answer to a code the player entered is classified and, when the setting
// "Send promo codes to the site" is on (default), reported as {code, result, toast, region, gameVersion}: nothing else.
// The sync code goes as Authorization: Bearer (the site keeps only a keyed hash, to count distinct reporters); no player name, uid or X-RF-Player. Contract (site: POST /api/codes/report, lib/codes/handler.ts):
//   body {code: 4..24 letters/digits, result: ok|invalid|expired|used|limit|notstarted|unknown, toast?: <=200, region?: <=32, gameVersion?: <=64,
//         rewards?: [{id, n}] (what the game's rewards window showed after an accepted code: at most 20, positive whole numbers)}
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
      new KeyValuePair<string, Regex>(Expired, R(@"expired|out of date|no longer valid|истек|истёк|просроч|срок.{0,20}(действ|вышел|истек|истёк)|expir|abgelaufen|caduc|vencid|süresi|kedaluwarsa|hết hạn|scadut|wygas|nicht mehr gültig|期限切れ|期限が切れ|만료|기간이 지났|过期|過期|已失效|หมดอายุ")),
      new KeyValuePair<string, Regex>(Used, R(@"already (been )?(used|redeemed|claimed|exchanged)|has been (used|redeemed|claimed|exchanged)|been (used|redeemed)|уже (был|использ|активир|получ|обмен)|использован|bereits|déjà|ya (fue|ha sido|se)|já foi|zaten|sudah digunakan|đã được|già\b.{0,30}(usat|utilizzat|riscattat)|ya.{0,20}(canjead|utilizad|usad)|já (está|utiliz|resgat|usad)|schon (eingelöst|benutzt|verwendet)|już.{0,20}(wykorzyst|użyt|zrealiz|odebran|zużyt)|daha önce kullanıl|sudah (dipakai|ditukar|diklaim)|使用済み|既に|すでに|이미|已使用|已被使用|已经使用|已兑换|已兌換|已领取|已領取|ใช้แล้ว|ถูกใช้")),
      new KeyValuePair<string, Regex>(Limit, R(@"limit|too many|maximum|exceed|лимит|превыш|слишком много|максимум|quota|квот|límite|massimo|troppi|superat|höchst|maximal|zu viele|przekrocz|sınır|batas|giới hạn|vượt quá|上限|回数制限|한도|초과|超过|超過|已达到|已達到|จำกัด|เกิน")),
      new KeyValuePair<string, Regex>(NotStarted, R(@"not (yet )?(open|started|begun|available|active)|has not started|hasn.?t (yet )?(open|started|begun)|ещё не|еще не|не начал|не открыт|non (è )?ancora|pas encore|aún no|todavía no|ainda não|noch nicht|jeszcze nie|henüz|belum (dimulai|dibuka)|chưa (bắt đầu|mở)|まだ|開始前|아직|시작되지 않|尚未|未开始|未開始|还没|還沒|ยังไม่")),
      new KeyValuePair<string, Regex>(Invalid, R(@"invalid|incorrect|wrong|not exist|doesn.?t exist|does not exist|not found|unknown (code|key)|неверн|недействител|не существует|не найден|ошибочн|ungültig|invalide|inválid|geçersiz|tidak valid|không hợp lệ|non (è )?valid|non esiste|errat|non è corrett|no (es |está )?válid|no existe|não (é )?válid|não existe|inexistente|nicht gültig|existiert nicht|falsch|pas valide|n.existe pas|inexistant|erroné|nieprawid|niepoprawn|nieważn|nie istnieje|błędn|bulunamadı|yanlış|tidak (ditemukan|berlaku)|salah|không tồn tại|無効|存在しません|正しくありません|間違|유효하지 않|존재하지 않|올바르지 않|잘못|无效|無效|不存在|错误|錯誤|不正确|ไม่ถูกต้อง|ไม่พบ")),
      new KeyValuePair<string, Regex>(Ok, R(@"success|redeemed|congratulations|reward|получен|успешн|поздравл|награда|erfolg|réussi|éxito|exitos|sucesso|başarı|berhasil|thành công|successo|riscattat|recompens|complimenti|ricompensa|premio|canje|felicidades|parabéns|resgatad|succès|félicitations|eingelöst|erfolgreich|sukces|pomyślnie|odebrano|zrealizowano|tebrikler|ödül|selamat|hadiah|chúc mừng|phần thưởng|成功|交換しました|受け取|獲得|성공|교환되었|수령|보상|恭喜|领取|兑换|兌換|สำเร็จ|ขอแสดงความยินดี|รางวัล")),
    };

    /// <summary>The result for the game's message; "unknown" when it matches nothing known (the site keeps the raw text to learn new ones).</summary>
    public static string Classify(string message) {
      if (string.IsNullOrWhiteSpace(message)) return Unknown;
      foreach (var r in Rules) if (r.Value.IsMatch(message)) return r.Key;
      return Unknown;
    }

    // game hints that are not an answer to a redeem (the clipboard copy confirmation), in the game's languages
    static readonly Regex NotAnswer = R(@"\u0441\u043a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u043d|copied|copiat|copiad|kopiert|copi\u00e9|skopiowan|kopyalan|disalin|\u0111\u00e3 sao ch\u00e9p|\u30b3\u30d4\u30fc|\ubcf5\uc0ac|\u590d\u5236|\u8907\u88fd|\u0e04\u0e31\u0e14\u0e25\u0e2d\u0e01");

    /// <summary>True when the message is a known game hint that is not the answer to a redeem and no redeem rule matches it.</summary>
    public static bool IsNotAnswer(string message) {
      if (string.IsNullOrWhiteSpace(message)) return false;
      return Classify(message) == Unknown && NotAnswer.IsMatch(message);
    }

    // cheap per-language signals for Latin text: words of the redeem toasts and letters typical of the language
    static readonly KeyValuePair<string, Regex>[] Latin = {
      new KeyValuePair<string, Regex>("vi", R(@"[\u0111\u01b0\u01a1\u1ea0-\u1ef9]")),
      new KeyValuePair<string, Regex>("pl", R(@"[\u0105\u0119\u0142\u0144\u015b\u017a\u017c\u0107]|\bjeszcze\b|\bnie jest\b|nieprawid|\bkod\b.{0,40}(jest|zosta)")),
      new KeyValuePair<string, Regex>("tr", R(@"[\u0131\u011f\u015f\u0130]|ge\u00e7ersiz|\bzaten\b|hen\u00fcz|s\u00fcresi|kullan")),
      new KeyValuePair<string, Regex>("de", R(@"\bcode\b.{0,30}\b(ist|wurde)\b|ung\u00fcltig|eingel\u00f6st|abgelaufen|\bbereits\b|\bnoch nicht\b|erfolgreich|\bder code\b|[\u00df\u00e4]")),
      new KeyValuePair<string, Regex>("id", R(@"\bkode\b|\bsudah\b|\bbelum\b|\btidak\b|berhasil|ditukar|kedaluwarsa")),
      new KeyValuePair<string, Regex>("pt", R(@"resgat|n\u00e3o|[\u00e3\u00f5]|\bo c\u00f3digo\b|\bainda\b|expirou|\bfoi\b|parab\u00e9ns")),
      new KeyValuePair<string, Regex>("es", R(@"canje|\u00f1|[\u00a1\u00bf]|\bel c\u00f3digo\b|\bno es\b|\ba\u00fan\b|felicidades|vencido|\bha sido\b")),
      new KeyValuePair<string, Regex>("fr", R(@"\u00e9chang|n.est\b|\bce code\b|d\u00e9j\u00e0|\bpas encore\b|disponible|f\u00e9licitations|expir\u00e9|\u00e7u\b")),
      new KeyValuePair<string, Regex>("it", R(@"\bcodice\b|non \u00e8|\b\u00e8\b|riscatt|scadut|\bgi\u00e0\b|\bancora\b|\bil codice\b|\bnon esiste\b")),
    };

    /// <summary>The language of a message (sent as the report's region): "ru" for Cyrillic; for Latin text one of
    /// it/es/pt/de/fr/pl/tr/id/vi by cheap signals, "en" when none stands out; null for anything else.</summary>
    public static string LanguageOf(string message) {
      if (string.IsNullOrEmpty(message)) return null;
      bool cyr = false, lat = false, other = false;
      foreach (char c in message) {
        if (c >= '\u0400' && c <= '\u04ff') cyr = true;
        else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) lat = true;
        else if (c > '\u024f' && !(c >= '\u1e00' && c <= '\u1eff')) other = true;
      }
      if (other) return null;
      if (cyr) return "ru";
      if (!lat) return null;
      string best = "en"; int bestN = 0;
      foreach (var l in Latin) {
        int n = l.Value.Matches(message).Count;
        if (n > bestN) { bestN = n; best = l.Key; }
      }
      return best;
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

  /// <summary>The reward list of an accepted code: item id and amount, as the rewards window shows them.</summary>
  public static class CodeRewards {
    public const int MaxEntries = 20;
    const long MaxValue = 2000000000;

    /// <summary>The (id, amount) pairs as the list the site takes: one entry per id (amounts summed), sorted by id, whole
    /// positive numbers only (others are dropped). Null when nothing is left or there are more than 20 different ids (such a list would
    /// be cut and mislead).</summary>
    public static List<KeyValuePair<long, long>> Normalize(IEnumerable<KeyValuePair<long, long>> raw) {
      if (raw == null) return null;
      var sum = new SortedDictionary<long, long>();
      foreach (var kv in raw) {
        if (kv.Key < 1 || kv.Key > MaxValue || kv.Value < 1 || kv.Value > MaxValue) continue;
        long cur; sum.TryGetValue(kv.Key, out cur);
        sum[kv.Key] = Math.Min(MaxValue, cur + kv.Value);
      }
      if (sum.Count == 0 || sum.Count > MaxEntries) return null;
      return new List<KeyValuePair<long, long>>(sum);
    }

    /// <summary>[{"id":1,"n":2},...] for a normalized list.</summary>
    public static string Json(List<KeyValuePair<long, long>> list) {
      var sb = new StringBuilder("[");
      for (int i = 0; i < list.Count; i++) {
        if (i > 0) sb.Append(',');
        sb.Append("{\"id\":").Append(list[i].Key.ToString(CultureInfo.InvariantCulture)).Append(",\"n\":").Append(list[i].Value.ToString(CultureInfo.InvariantCulture)).Append('}');
      }
      return sb.Append(']').ToString();
    }

    /// <summary>The same set of rewards (both normalized).</summary>
    public static bool Same(List<KeyValuePair<long, long>> a, List<KeyValuePair<long, long>> b) {
      if (a == null || b == null) return a == b;
      if (a.Count != b.Count) return false;
      for (int i = 0; i < a.Count; i++) if (a[i].Key != b[i].Key || a[i].Value != b[i].Value) return false;
      return true;
    }
  }

  public static class CodeReportClient {
    public const int MaxToast = 160;

    /// <summary>The request body; null when the code does not look like one. Only these fields, nothing about the player.</summary>
    public static string BuildBody(string code, string result, string message, string gameVersion) { return BuildBody(code, result, message, gameVersion, null); }

    /// <summary>As above, with the rewards of an accepted code (normalized by <see cref="CodeRewards.Normalize"/>; sent only for «ok»).</summary>
    public static string BuildBody(string code, string result, string message, string gameVersion, List<KeyValuePair<long, long>> rewards) {
      if (!CodeText.Looks(code)) return null;
      code = code.Trim();
      string toast = CodeText.ForLog(message);
      var sb = new StringBuilder("{\"code\":");
      sb.Append(MiniJson.Quote(code)).Append(",\"result\":").Append(MiniJson.Quote(result ?? CodeResult.Unknown));
      if (toast.Length > 0) sb.Append(",\"toast\":").Append(MiniJson.Quote(toast));
      string lang = CodeResult.LanguageOf(toast);
      if (lang != null) sb.Append(",\"region\":").Append(MiniJson.Quote(lang));
      if (!string.IsNullOrEmpty(gameVersion) && gameVersion.Length <= 64) sb.Append(",\"gameVersion\":").Append(MiniJson.Quote(gameVersion));
      if (rewards != null && rewards.Count > 0 && rewards.Count <= CodeRewards.MaxEntries && result == CodeResult.Ok) sb.Append(",\"rewards\":").Append(CodeRewards.Json(rewards));
      return sb.Append("}").ToString();
    }

    /// <summary>POSTs the body to {site}/api/codes/report with the sync code as Bearer; no player header. Returns the HTTP status (0 = no answer).</summary>
    public static string AuthHeader(string syncCode) { return "Bearer " + syncCode; }

    public static int Send(string site, string syncCode, string body, out string err) {
      err = null;
      try {
        SyncClient.EnableTls12();
        ServicePointManager.Expect100Continue = false;
        var req = (HttpWebRequest)WebRequest.Create(site.TrimEnd('/') + "/api/codes/report");
        req.Method = "POST";
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Headers["Authorization"] = AuthHeader(syncCode);
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
