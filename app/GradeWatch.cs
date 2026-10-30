// Wardsage - hero ratings: while the game runs the app looks (every 800 ms, 2 s with the game away; read-only) whether the rating screen of a hero is
// open; when it is, the rows shown there are queued (src/HeroGrades.cs) and go to the site with the next account sync
// (<see cref="Flush"/>, called after a sync that was uploaded). Without a sync code nothing is read. Nothing is written to the
// game, nothing is clicked.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RealmForge {
  sealed class GradeWatch {
    volatile bool running, busy, finding, flushing;
    Timer timer;
    int lastHero, lastRows; string lastWhy;
    int findAt;
    readonly HeroGradeQueue queue = new HeroGradeQueue();
    readonly Func<string> syncCode;

    public GradeWatch(Func<string> syncCode) { this.syncCode = syncCode; }

    /// <summary>Called by the game timer.</summary>
    /// <param name="away">the game is minimized or behind another window: a look every 2 s</param>
    public void Tick(bool gameRunning, bool away = false) {
      if (timer != null) { int want = LoadPlan.GradeWatchMs(!away, !away); if (want != period) { period = want; try { timer.Change(want, want); } catch (Exception) { } } }
      if (!SyncClient.IsValidCode(syncCode())) gameRunning = false;
      bool was = running;
      running = gameRunning;
      if (timer == null) timer = new Timer(s => Look(), null, 1500, 800);
      if (!gameRunning && was) { queue.NewSession(); lastHero = 0; lastRows = 0; lastWhy = null; }
    }

    int period = 800;

    void Look() {
      if (!running || busy) return;
      busy = true;
      long t0 = LoadMeter.Now();
      try { Step(); }
      catch (Exception e) { Log.Write("hero grades: error " + e.Message); }
      finally { busy = false; LoadMeter.Done(LoadMeter.Grades, t0); }
    }

    void Step() {
      var r = RFX.ReadHeroGrades();
      if (!r.Known) {
        if (!RFX.UiKnown && !finding && Layout.Current != null && (findAt == 0 || Environment.TickCount - findAt > 20000)) {
          finding = true; findAt = Environment.TickCount; if (findAt == 0) findAt = 1;
          Task.Factory.StartNew(() => { try { RFX.FindUi(60000); } catch (Exception) { } finally { finding = false; } });
        }
        return;
      }
      if (!r.Open) { lastHero = 0; lastRows = 0; lastWhy = null; return; }
      if (r.Hero == 0 || r.Rows.Count == 0) {
        if (r.Why != null && r.Why != lastWhy) { lastWhy = r.Why; Log.Write("hero grades: not read (" + r.Why + ")"); }
        return;
      }
      lastWhy = null;
      queue.Offer(r.Hero, r.Rows);
      if (r.Hero != lastHero || r.Rows.Count != lastRows) {
        lastHero = r.Hero; lastRows = r.Rows.Count;
        Log.Write("hero grades: hero " + r.Hero + ", " + r.Rows.Count + " modes");
      }
    }

    /// <summary>Sends the queued rows (up to 8 requests of 64 rows) after an uploaded account sync. Off the caller's thread; never throws;
    /// a failed request puts its rows back for the next sync.</summary>
    public void Flush(string site, string code) {
      if (queue.Count == 0 || flushing || !SyncClient.IsValidCode(code) || string.IsNullOrEmpty(site)) return;
      flushing = true;
      var ly = Layout.Current; string game = ly != null ? ly.Game : null;
      Task.Factory.StartNew(() => {
        try {
          for (int i = 0; i < HeroGrades.MaxRequestsPerFlush; i++) {
            var batch = queue.Take(HeroGrades.MaxRowsPerRequest);
            if (batch.Count == 0) break;
            string body = HeroGrades.BuildBody(batch, DateTime.UtcNow, game), err;
            int http = body == null ? 422 : HeroGrades.Send(site, code, body, out err);
            if (http >= 200 && http < 300) { Log.Write("hero grades: sent " + batch.Count + " rows"); continue; }
            if (http == 422) { Log.Write("hero grades: the site did not take " + batch.Count + " rows"); continue; }   // the same rows would be refused again
            queue.Return(batch);
            Log.Write("hero grades: not sent (" + (http == 0 ? "no answer" : "HTTP " + http) + ")");
            break;
          }
        } catch (Exception e) { Log.Write("hero grades: error " + e.Message); }
        finally { flushing = false; }
      });
    }
  }
}
