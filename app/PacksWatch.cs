// Wardsage — the addresses of the downloaded game update packs to the site (src/GamePacks.cs reads the game's log). Ticked by
// HostBridge's game timer (every 2 s): about a minute after the game starts, then at most every 30 minutes while it runs;
// sends only when the set of addresses differs from the last one sent. Failures are silent: the next look retries.
using System;
using System.Threading.Tasks;

namespace RealmForge {
  sealed class PacksWatch {
    const int EveryMin = 30;
    readonly Func<string> site, code;
    volatile bool busy;
    string lastHash; DateTime lastTry = DateTime.MinValue; DateTime pauseUntil = DateTime.MinValue;
    DateTime? gameSince;

    public PacksWatch(Func<string> site, Func<string> code) { this.site = site; this.code = code; }

    public void Tick(bool gameRunning) {
      if (!gameRunning) { gameSince = null; return; }
      if (gameSince == null) { gameSince = DateTime.UtcNow; lastTry = DateTime.MinValue; }
      if (busy || DateTime.UtcNow < pauseUntil) return;
      string c = code(), s = site();
      if (!SyncClient.IsValidCode(c) || string.IsNullOrEmpty(s)) return;
      var now = DateTime.UtcNow;
      if (now - gameSince.Value < TimeSpan.FromMinutes(1)) return;
      if (lastTry != DateTime.MinValue && now - lastTry < TimeSpan.FromMinutes(EveryMin)) return;
      lastTry = now; busy = true;
      Task.Factory.StartNew(() => {
        try {
          var urls = GamePacks.ReadLogs();
          if (urls.Count == 0) return;
          string json = GamePacks.Json(urls);
          string hash = GuildPayload.Hash(json);
          if (hash == lastHash) return;
          string details;
          var st = GamePacks.Send(s, c, json, out details);
          if (st == PlansStatus.Ok) { lastHash = hash; Log.Write("packs: " + urls.Count + " адресов обновлений -> сайт"); }
          else {
            Log.Write("packs: не отправлено (" + st + (details != null ? " " + details : "") + ")");
            if (st == PlansStatus.InvalidToken) pauseUntil = DateTime.UtcNow.AddMinutes(5);
          }
        } catch (Exception e) { Log.Write("packs: " + e.Message); }
        finally { busy = false; }
      });
    }
  }
}
