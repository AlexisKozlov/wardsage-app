// Wardsage - test build only: logs what the game's code-redeem window shows (src/CodeWatch.cs reads it, read-only), as
// "cdk:" lines: the window opening and closing, the code in its input field, the game's messages, and the windows that open
// within 5 s after. A look every 300 ms while the game runs; nothing is sent anywhere but the test log.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RealmForge {
  sealed class CodeWatch {
    static readonly TimeSpan After = TimeSpan.FromSeconds(5), Stable = TimeSpan.FromMilliseconds(800);
    volatile bool running; volatile bool busy;
    Timer timer;
    bool wasOpen, whySaid, uiFinding;
    string typed, loggedInput; DateTime typedAt, watchUntil = DateTime.MinValue;
    HashSet<int> active; HashSet<string> tips = new HashSet<string>();
    string lastWhy;

    /// <summary>Called by the game timer: only the test build watches.</summary>
    public void Tick(bool gameRunning) {
      if (!Channel.IsTest) return;
      running = gameRunning;
      if (timer == null) timer = new Timer(s => Look(), null, 1000, 300);
      if (!gameRunning) { wasOpen = false; whySaid = false; active = null; }
    }

    void Look() {
      if (!running || busy) return;
      busy = true;
      try { Step(); }
      catch (Exception e) { Say("error " + e.Message); }
      finally { busy = false; }
    }

    static void Say(string s) { Log.Write("cdk: " + s); }

    void Step() {
      var now = DateTime.UtcNow;
      bool armed = wasOpen || now < watchUntil;
      var r = RFX.ReadCdk(armed);
      if (!r.Known) {
        if (!RFX.UiKnown && !uiFinding) {
          uiFinding = true;
          Task.Factory.StartNew(() => { try { RFX.FindUi(60000); } catch (Exception) { } finally { uiFinding = false; } });
        }
        if (r.Why != lastWhy) { lastWhy = r.Why; Say("not watching: " + r.Why); }
        return;
      }
      lastWhy = null;
      if (r.Open && !wasOpen) {
        wasOpen = true; typed = null; loggedInput = null; tips.Clear(); active = null;
        Say("window open");
        // the baseline for "windows opened after": read at once (deeper look from now on)
        var d = RFX.ReadCdk(true);
        if (d.Known) { active = d.Active; foreach (var t in d.Tips) tips.Add(t); r = d; }
      }
      if (r.Open) {
        if (r.Input == null && !whySaid) { whySaid = true; Say("input text not readable"); }
        string cur = r.Input != null ? r.Input.Trim() : null;
        if (cur != typed) { typed = cur; typedAt = now; }
        if (typed != null && now - typedAt >= Stable) LogInput(now);
      }
      if (armed || r.Open) {
        if (r.Tips != null) foreach (var t in r.Tips) if (tips.Add(t)) {
          LogInput(now);   // the code the toast is about
          Say("toast \"" + CodeText.ForLog(t) + "\"");
          watchUntil = now + After;
        }
        if (r.Active != null) {
          if (active != null && now < watchUntil + TimeSpan.FromSeconds(1))
            foreach (var id in r.Active) if (!active.Contains(id) && now <= watchUntil) Say("form " + id + " opened");
          active = r.Active;
        }
      }
      if (!r.Open && wasOpen) {
        LogInput(now);
        wasOpen = false; whySaid = false;
        Say("window closed");
        watchUntil = now + After;
      }
      // the toasts forgotten once nothing is going on, so the same message shows again next time
      if (!wasOpen && now >= watchUntil) { tips.Clear(); active = null; }
    }

    // the code in the input field: a pasted or finished one stays still for a moment, a half-typed one is replaced
    void LogInput(DateTime now) {
      if (typed == null || !CodeText.Looks(typed) || typed == loggedInput) return;
      loggedInput = typed;
      Say("input \"" + typed + "\"");
      if (now + After > watchUntil) watchUntil = now + After;
    }
  }
}
