// Wardsage - promo codes: a read-only look at the game's code-redeem window (src/CodeWatch.cs reads it). When a code was
// entered and the game answered, the answer is classified (src/CodeReport.cs) and the code with that answer is sent to the site (with the sync code; without one nothing is sent),
// once per code and answer per 10 minutes. Only while the setting "Send promo codes to the site" is on (default); when it is off
// nothing is read and nothing is sent. The window, the input and the messages are also noted in the journal ("cdk:" lines).
// A look every 300 ms while the game runs. Nothing is written to the game, nothing is clicked.
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
    readonly Func<bool> enabled; readonly Func<string> site, syncCode;
    readonly CodeReportGate gate = new CodeReportGate();
    bool sentWarned, noKeySaid;

    public CodeWatch(Func<bool> enabled, Func<string> site, Func<string> syncCode) { this.enabled = enabled; this.site = site; this.syncCode = syncCode; }

    /// <summary>Called by the game timer: watches only while the setting is on.</summary>
    public void Tick(bool gameRunning) {
      if (!enabled()) gameRunning = false;
      running = gameRunning;
      if (timer == null) timer = new Timer(s => Look(), null, 1000, 300);
      if (!gameRunning) { wasOpen = false; whySaid = false; active = null; typed = null; loggedInput = null; tips.Clear(); }
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
          Report(typed, t, now);
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

    // the game answered for the code in the input field: classify and send it (off the look thread; never throws)
    void Report(string code, string message, DateTime now) {
      if (!enabled() || !CodeText.Looks(code)) return;
      code = code.Trim();
      string result = CodeResult.Classify(message);
      if (!gate.ShouldSend(code, result, now)) return;
      string key = syncCode();
      if (!SyncClient.IsValidCode(key)) { if (!noKeySaid) { noKeySaid = true; Log.Write("promo codes: no sync key"); } return; }
      var ly = Layout.Current;
      string body = CodeReportClient.BuildBody(code, result, message, ly != null ? ly.Game : null), url = site();
      if (body == null || string.IsNullOrEmpty(url)) return;
      Say("report " + result);
      Task.Factory.StartNew(() => {
        try {
          string err; int http = CodeReportClient.Send(url, key, body, out err);
          if (http >= 200 && http < 300) return;
          if (http == 401 && sentWarned) return;
          if (http == 401) sentWarned = true;
          Say("report not accepted: " + (http == 0 ? err : "HTTP " + http));
        } catch (Exception) { }
      });
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
