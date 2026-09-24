// RealmForge extractor - sending account.json to the RealmForge site.
//
// Contract (fixed, implemented by the site):
//   POST {site}/api/sync
//   Authorization: Bearer <sync code>        Content-Type: application/json
//   Content-Encoding: gzip (body = gzip of UTF-8 account.json)
//   X-RF-Extractor: 0.5                      User-Agent: RealmForge-Extractor/0.5
// Replies (JSON):
//   200 {ok:true, snapshotId, heroes, items, artifacts, viewUrl}
//   401 invalid_token | 413 too_large | 415 | 422 {error:"invalid_payload", details} | 429 rate_limited | 500
// Nothing else is sent anywhere: no telemetry, no third-party services.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  public enum SyncStatus {
    Ok, InvalidToken, TooLarge, UnsupportedMedia, InvalidPayload, RateLimited, ServerError,
    Redirect, Timeout, Unreachable, Unexpected
  }

  public sealed class SyncResult {
    public SyncStatus Status;
    public int HttpCode;              // 0 when there was no HTTP response
    public string SnapshotId;
    public int Heroes = -1, Items = -1, Artifacts = -1;   // -1 = not in the reply
    public string ViewUrl;            // absolute http(s) URL or null
    public string Details;            // 422 details / network error text / redirect target
    public int RetryAfterSeconds = -1;
  }

  public static class SyncClient {
    public const string Version = RFX.ExtractorVersion;
    public const string UserAgent = "RealmForge-Extractor/" + Version;
    public const string DefaultSite = "https://realmforge.vercel.app";
    public static int TimeoutMs = 60000;   // per request; a field (not const) so tests can shorten it
    const int MaxReplyBytes = 1024 * 1024;

    static readonly Regex CodeRx = new Regex("^rf_[0-9A-Za-z]{32}$");
    static readonly Regex CodeInTextRx = new Regex("(?<![0-9A-Za-z_])rf_[0-9A-Za-z]{32}(?![0-9A-Za-z])");

    // --- sync code ---

    public static bool IsValidCode(string code) { return code != null && CodeRx.IsMatch(code); }

    // Accepts pasted text such as "  rf_XXXX...  " or "Code: rf_XXXX..." and returns just the code.
    // Anything else is returned trimmed, so the user sees what they typed.
    public static string ExtractCode(string text) {
      if (text == null) return "";
      Match m = CodeInTextRx.Match(text);
      return m.Success ? m.Value : text.Trim();
    }

    // --- site address ---

    // Returns the normalized base address ("https://host[/path]", no trailing slash) or null.
    // error: null | "invalid" | "https" (plain http is allowed only for localhost, for testing).
    public static string NormalizeSite(string input, out string error) {
      error = null;
      string s = (input ?? "").Trim();
      if (s.Length == 0) { error = "invalid"; return null; }
      if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "https://" + s;
      Uri u;
      if (!Uri.TryCreate(s, UriKind.Absolute, out u) || u.Host.Length == 0 || u.Query.Length > 0 || u.Fragment.Length > 0 || u.UserInfo.Length > 0) {
        error = "invalid"; return null;
      }
      bool local = u.IsLoopback;
      if (u.Scheme == Uri.UriSchemeHttp && !local) { error = "https"; return null; }
      if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) { error = "invalid"; return null; }
      return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    // --- sending ---

    public static byte[] Gzip(string json) {
      byte[] raw = new UTF8Encoding(false).GetBytes(json);
      using (var ms = new MemoryStream()) {
        using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
      }
    }

    // .NET Framework 4.x may default to TLS 1.0/1.1 only; the site requires TLS 1.2+.
    // TLS 1.3 is not forced on purpose: Windows 10 has no TLS 1.3 client and would fail the handshake.
    public static void EnableTls12() {
      const SecurityProtocolType Tls12 = (SecurityProtocolType)3072;
      try { ServicePointManager.SecurityProtocol |= Tls12; } catch (NotSupportedException) { }
    }

    // Sends account.json. site must come from NormalizeSite. Never throws.
    public static SyncResult Send(string site, string code, string json) {
      try {
        EnableTls12();
        ServicePointManager.Expect100Continue = false;
        byte[] body = Gzip(json);
        var req = (HttpWebRequest)WebRequest.Create(site + "/api/sync");
        req.Method = "POST";
        req.ContentType = "application/json";
        req.Accept = "application/json";
        req.UserAgent = UserAgent;
        req.Headers["Authorization"] = "Bearer " + code;
        req.Headers["Content-Encoding"] = "gzip";
        req.Headers["X-RF-Extractor"] = Version;
        req.Timeout = TimeoutMs;
        req.ReadWriteTimeout = TimeoutMs;
        req.AllowAutoRedirect = false;   // a redirected POST would turn into a GET and lose the body
        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        req.ContentLength = body.Length;
        using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
        using (var resp = (HttpWebResponse)req.GetResponse()) return FromResponse(resp, site);
      } catch (WebException e) {
        var resp = e.Response as HttpWebResponse;
        if (resp != null) using (resp) return FromResponse(resp, site);
        var r = new SyncResult();
        r.Status = e.Status == WebExceptionStatus.Timeout ? SyncStatus.Timeout : SyncStatus.Unreachable;
        r.Details = e.Message;
        return r;
      } catch (Exception e) {
        var r = new SyncResult();
        r.Status = SyncStatus.Unreachable;
        r.Details = e.Message;
        return r;
      }
    }

    static SyncResult FromResponse(HttpWebResponse resp, string site) {
      string body = null;
      try {
        using (Stream s = resp.GetResponseStream())
        using (var ms = new MemoryStream()) {
          var buf = new byte[16384]; int n;
          while ((n = s.Read(buf, 0, buf.Length)) > 0 && ms.Length < MaxReplyBytes) ms.Write(buf, 0, n);
          body = Encoding.UTF8.GetString(ms.ToArray());
        }
      } catch (Exception) { /* keep body == null: the status code alone is still meaningful */ }
      return Interpret((int)resp.StatusCode, body, resp.Headers["Retry-After"], resp.Headers["Location"], site);
    }

    // Maps an HTTP reply to a SyncResult. Pure function (tested without a network).
    public static SyncResult Interpret(int code, string body, string retryAfter, string location, string site) {
      var r = new SyncResult();
      r.HttpCode = code;
      var obj = MiniJson.AsObject(MiniJson.TryParse(body));
      if (code >= 200 && code < 300) {
        if (obj == null || !MiniJson.GetBool(obj, "ok", false)) { r.Status = SyncStatus.Unexpected; return r; }
        r.Status = SyncStatus.Ok;
        r.SnapshotId = MiniJson.GetString(obj, "snapshotId");
        r.Heroes = MiniJson.GetCount(obj, "heroes");
        r.Items = MiniJson.GetCount(obj, "items");
        r.Artifacts = MiniJson.GetCount(obj, "artifacts");
        r.ViewUrl = ResolveUrl(site, MiniJson.GetString(obj, "viewUrl"));
        return r;
      }
      if (code >= 300 && code < 400) {
        r.Status = SyncStatus.Redirect;
        r.Details = ResolveUrl(site, location);
        return r;
      }
      switch (code) {
        case 401: r.Status = SyncStatus.InvalidToken; break;
        case 413: r.Status = SyncStatus.TooLarge; break;
        case 415: r.Status = SyncStatus.UnsupportedMedia; break;
        case 400:
        case 422: r.Status = SyncStatus.InvalidPayload; r.Details = DetailsText(obj); break;
        case 429:
          r.Status = SyncStatus.RateLimited;
          int sec;
          if (retryAfter != null && int.TryParse(retryAfter.Trim(), out sec) && sec >= 0) r.RetryAfterSeconds = sec;
          else if (MiniJson.GetCount(obj, "retryAfter") >= 0) r.RetryAfterSeconds = MiniJson.GetCount(obj, "retryAfter");
          break;
        default: r.Status = code >= 500 && code < 600 ? SyncStatus.ServerError : SyncStatus.Unexpected; break;
      }
      return r;
    }

    // Makes a server-provided URL absolute and accepts only http(s): the reply must never make
    // the extractor open anything else (file:, ms-settings:, ...).
    public static string ResolveUrl(string site, string url) {
      if (string.IsNullOrEmpty(url)) return null;
      Uri baseUri, u;
      if (!Uri.TryCreate(site + "/", UriKind.Absolute, out baseUri)) return null;
      if (!Uri.TryCreate(baseUri, url, out u)) return null;
      if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return null;
      return u.AbsoluteUri;
    }

    static string DetailsText(Dictionary<string, object> obj) {
      object d;
      if (obj == null || !obj.TryGetValue("details", out d) || d == null) return null;
      var parts = new List<string>();
      var list = d as List<object>;
      if (list != null) { foreach (var x in list) { if (parts.Count == 3) { parts.Add("..."); break; } parts.Add(Short(x)); } }
      else parts.Add(Short(d));
      string s = string.Join("; ", parts.ToArray());
      return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
    }

    static string Short(object x) {
      if (x == null) return "null";
      var s = x as string; if (s != null) return s;
      var o = x as Dictionary<string, object>;
      if (o != null) {
        string msg = MiniJson.GetString(o, "message") ?? MiniJson.GetString(o, "error");
        string path = MiniJson.GetString(o, "path");
        if (msg != null) return path != null ? path + ": " + msg : msg;
        return "{...}";
      }
      return Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture);
    }
  }
}
