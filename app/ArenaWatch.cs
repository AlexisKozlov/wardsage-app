// RealmForge — the arena's opponents to the site while the game's arena screen is open (src/ArenaOpponents.cs reads them,
// read-only; src/ArenaClient.cs sends them). Ticked by HostBridge's game timer (every 2 s): when Form_PVPMain is open, the
// list is read off the interface thread and sent when it changed (and at least every 60 s while the screen stays open, so
// the site knows the reading is fresh). Every send logs what was read.
using System;
using System.Threading.Tasks;

namespace RealmForge {
  sealed class ArenaWatch {
    readonly Func<string> site, code;
    volatile bool busy;
    string lastSig; DateTime lastSent = DateTime.MinValue; bool lastOpen; int failures;
    DateTime pauseUntil = DateTime.MinValue, lastBlind = DateTime.MinValue; bool blindLogged, missLogged;

    public ArenaWatch(Func<string> site, Func<string> code) { this.site = site; this.code = code; }

    /// <summary>One look (cheap unless the arena screen is open); call on the game timer.</summary>
    public void Tick(bool gameRunning) {
      if (!gameRunning || busy || DateTime.UtcNow < pauseUntil) return;
      string c = code(), s = site();
      if (!SyncClient.IsValidCode(c) || string.IsNullOrEmpty(s)) return;
      bool? open;
      try { open = RFX.PvpScreenOpen(); } catch (Exception) { open = null; }
      // the arena screen's own flag (Form_PVPMain m_LuaLogicActive) did not tell it live: the list is read every 10 s
      // whatever the screen (cheap once PVPData is known) and sent when it changes; at once when the flag says open
      if (open != true) {
        if (DateTime.UtcNow - lastBlind < TimeSpan.FromSeconds(10)) return;
        lastBlind = DateTime.UtcNow;
        if (!blindLogged) { blindLogged = true; Log.Write("arena: reading the opponents every 10 s"); }
      } else if (!lastOpen) { lastOpen = true; Log.Write("arena: the arena screen is open"); }
      busy = true;
      Task.Factory.StartNew(() => {
        try {
          string sig;
          string json = RFX.ReadArenaOpponents(30000, out sig);
          if (json == null) { if (!missLogged) { missLogged = true; Log.Write("arena: the arena's data (PVPData) not found yet (open the arena once)"); } pauseUntil = DateTime.UtcNow.AddSeconds(30); return; }
          missLogged = false;
          if (sig == lastSig && (DateTime.UtcNow - lastSent).TotalSeconds < 60) return;
          bool changed = sig != lastSig;
          string body = ArenaClient.WithFights(json, ArenaOpp.FightRecords(RFX.BattlesDir));
          string details;
          var st = ArenaClient.Send(s, c, body, out details);
          if (changed) Log.Write("arena: opponents read (" + json.Length + " bytes): " + Summary(json) + " -> site " + st + (details != null ? " " + details : ""));
          if (st == PlansStatus.Ok) { lastSig = sig; lastSent = DateTime.UtcNow; failures = 0; }
          else if (st == PlansStatus.InvalidToken) pauseUntil = DateTime.UtcNow.AddMinutes(5);
          else if (++failures >= 3) { pauseUntil = DateTime.UtcNow.AddSeconds(30); failures = 0; }
        } catch (Exception e) { Log.Write("arena: " + e.Message); pauseUntil = DateTime.UtcNow.AddSeconds(30); }
        finally { busy = false; }
      });
    }

    /// <summary>A line for the log: the stage and each opponent's name, power, score and hero count.</summary>
    static string Summary(string json) {
      var o = MiniJson.AsObject(MiniJson.TryParse(json));
      if (o == null) return "?";
      var sb = new System.Text.StringBuilder("stage " + ArenaOpp.Num(o.ContainsKey("stage") ? o["stage"] : null));
      object v; var list = o.TryGetValue("opponents", out v) ? v as System.Collections.Generic.List<object> : null;
      if (list != null) foreach (var x in list) {
        var p = MiniJson.AsObject(x); if (p == null) continue;
        object h; var hs = p.TryGetValue("heroes", out h) ? h as System.Collections.Generic.List<object> : null;
        object f; var fr = p.TryGetValue("frames", out f) ? f as System.Collections.Generic.List<object> : null;
        sb.Append("; ").Append(MiniJson.GetString(p, "name")).Append(" power ").Append(ArenaOpp.Num(p.ContainsKey("power") ? p["power"] : null))
          .Append(" score ").Append(ArenaOpp.Num(p.ContainsKey("score") ? p["score"] : null))
          .Append(" heroes ").Append(hs != null ? hs.Count : 0).Append(" commands ").Append(fr != null ? fr.Count : 0)
          .Append(MiniJson.GetBool(p, "robot", false) ? " (bot)" : "");
      }
      return sb.ToString();
    }
  }
}
