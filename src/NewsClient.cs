// Wardsage - the unified «What's new» feed from the site: GET {site}/api/changelog?lang=ru|en&limit=30 (public, no code).
//   200 [{v, date, items:[{text, kind:"app"|"site", appVersion?}]}]
// The page cannot fetch the site itself (it runs on a virtual host), so the app fetches it and hands the JSON over.
using System;
using System.IO;
using System.Net;
using System.Text;

namespace RealmForge {
  public static class NewsClient {
    const int MaxBytes = 256 * 1024;

    /// <summary>The feed as a JSON array text, or null (no connection, not a 2xx, too big, not an array of entries).</summary>
    public static string Fetch(string site, string lang) {
      try {
        SyncClient.EnableTls12();
        var req = (HttpWebRequest)WebRequest.Create(site.TrimEnd('/') + "/api/changelog?lang=" + (lang == "en" ? "en" : "ru") + "&limit=30");
        req.Method = "GET";
        req.Accept = "application/json";
        req.UserAgent = SyncClient.UserAgent;
        req.Timeout = 8000;
        req.ReadWriteTimeout = 8000;
        req.AllowAutoRedirect = false;
        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream()) {
          var buf = new byte[16384]; int n;
          while ((n = s.Read(buf, 0, buf.Length)) > 0) {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBytes) return null;
          }
          return Interpret((int)resp.StatusCode, Encoding.UTF8.GetString(ms.ToArray()));
        }
      } catch (Exception) { return null; }
    }

    /// <summary>The body when it is a non-empty JSON array of objects with a number v (pure, tested without a network); else null.</summary>
    public static string Interpret(int code, string body) {
      if (code < 200 || code >= 300 || string.IsNullOrEmpty(body) || body.Length > MaxBytes) return null;
      var list = MiniJson.TryParse(body) as System.Collections.Generic.List<object>;
      if (list == null || list.Count == 0) return null;
      foreach (var x in list) {
        var o = MiniJson.AsObject(x); object v;
        if (o == null || !o.TryGetValue("v", out v) || !(v is double) || !(o.TryGetValue("items", out v) && v is System.Collections.Generic.List<object>)) return null;
      }
      return body;
    }
  }
}
