// Wardsage — the guild table to the site (src/GuildData.cs reads it, read-only; GuildClient sends it). Ticked by HostBridge's
// game timer (every 2 s): looks right after each account sync and otherwise at most every 10 minutes while the game runs; sends
// when the payload changed (the hash without "at") or when a boss period ends within the hour. Failures are silent: the next
// look retries.
using System;
using System.Threading.Tasks;

namespace RealmForge {
  sealed class GuildWatch {
    const int EveryMin = 10;
    readonly Func<string> site, code;
    volatile bool busy, afterSync;
    string lastHash; DateTime lastTry = DateTime.MinValue; DateTime pauseUntil = DateTime.MinValue; bool missLogged;

    public GuildWatch(Func<string> site, Func<string> code) { this.site = site; this.code = code; }

    /// <summary>An account sync has just read the game: look at the guild at the next tick.</summary>
    public void AfterSync() { afterSync = true; }

    public void Tick(bool gameRunning) {
      if (!gameRunning || busy || DateTime.UtcNow < pauseUntil) return;
      string c = code(), s = site();
      if (!SyncClient.IsValidCode(c) || string.IsNullOrEmpty(s)) return;
      long self = SyncClient.Player;
      if (self <= 0) return;   // the account is not read yet
      if (!GuildPayload.Due(afterSync, DateTime.UtcNow, lastTry, EveryMin)) return;
      afterSync = false; lastTry = DateTime.UtcNow; busy = true;
      Task.Factory.StartNew(() => {
        try {
          long at = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
          bool soon; int count;
          string json = RFX.ReadGuildJson(30000, self, at, out soon, out count);
          if (json == null) {
            if (!missLogged) { missLogged = true; Log.Write("guild: no guild table (no guild, or the data is not found yet)"); }
            return;
          }
          missLogged = false;
          string hash = GuildPayload.Hash(json);
          if (!GuildPayload.ShouldSend(hash, lastHash, soon)) return;
          string details;
          var st = GuildClient.Send(s, c, json, out details);
          Log.Write("guild: " + count + " участников (" + json.Length + " байт) -> сайт " + st + (details != null ? " " + details : ""));
          if (st == PlansStatus.Ok) lastHash = hash;
          else if (st == PlansStatus.InvalidToken) pauseUntil = DateTime.UtcNow.AddMinutes(5);
        } catch (Exception e) { Log.Write("guild: " + e.Message); }
        finally { busy = false; }
      });
    }
  }
}
