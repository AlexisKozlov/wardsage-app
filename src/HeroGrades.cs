// Wardsage - hero ratings: while the player has the rating screen of a hero open («Оценка», tab «Режимы») the app reads the rows
// the game shows there (mode, the players' score, the number of voters) and sends them to the site with the next sync.
// Read-only: nothing is written to the game, nothing is clicked, the game's servers are not asked for anything.
//
// Contract (implemented by the site, lib/heroGrades/handler.ts):
//   POST {site}/api/extractor/hero-grades      Authorization: Bearer <sync code>, X-RF-Player
//        body {v:1, at:"ISO time of the look", gameVersion?:"1.6.46.841.1", rows:[{heroId, modeId, score, voters}]}   (at most 64 rows)
//        200 {ok:true, stored:n}   401 invalid_token   422 invalid_payload{details}   429 rate_limited
// A row is taken once per hero and mode and game session; the queue is bounded (MaxQueue rows, the oldest are dropped).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealmForge {
  /// <summary>One row of a hero's rating screen: the mode, the score as the screen shows it (0.1 steps, at most 5), the voters.</summary>
  public sealed class HeroGradeRow {
    public int Mode, Voters; public double Score;
  }

  /// <summary>One look at the rating screen (<see cref="RFX.ReadHeroGrades"/>).</summary>
  public sealed class HeroGradeRead {
    public bool Known;                 // the game's windows were read (false = not yet / no layout data)
    public string Why;                 // when not Known: the reason
    public bool Open;                  // the rating screen is open now
    public int Hero;                   // the hero of the screen (0 = unreadable)
    public List<HeroGradeRow> Rows = new List<HeroGradeRow>();
  }

  public static class HeroGrades {
    public const int MinHero = 1000, MaxHero = 9999, MinMode = 1, MaxMode = 64;
    public const int MaxRowsPerHero = 64, MaxRowsPerRequest = 64, MaxQueue = 2000, MaxRequestsPerFlush = 8;
    public const double MaxAverage = 6.0;   // an average above this is not a score of 1..5 stars: the row is not taken

    public static bool HeroOk(long hero) { return hero >= MinHero && hero <= MaxHero; }
    public static bool ModeOk(long mode) { return mode >= MinMode && mode <= MaxMode; }

    /// <summary>The score the screen shows for a mode: the stars' sum over the voters, rounded up to 0.1 and at most 5
    /// (the screen's own rule). False when there are no voters or the numbers are not a star score.</summary>
    public static bool Shown(double sum, double voters, out double score) {
      score = 0;
      if (double.IsNaN(sum) || double.IsNaN(voters) || voters < 1 || voters > int.MaxValue || sum < 0) return false;
      double avg = sum / voters;
      if (avg > MaxAverage) return false;
      if (avg > 5) avg = 5;
      score = Math.Round(Math.Ceiling(avg * 10) / 10, 1);
      return true;
    }

    /// <summary>The request body for up to 64 queued rows; null when there is nothing to send.</summary>
    public static string BuildBody(IList<KeyValuePair<int, HeroGradeRow>> rows, DateTime atUtc, string gameVersion) {
      if (rows == null || rows.Count == 0) return null;
      var sb = new StringBuilder("{\"v\":1,\"at\":\"");
      sb.Append(atUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append("\"");
      if (!string.IsNullOrEmpty(gameVersion) && gameVersion.Length <= 64 && GameLayout.IsVersion(gameVersion)) sb.Append(",\"gameVersion\":").Append(MiniJson.Quote(gameVersion));
      sb.Append(",\"rows\":[");
      int n = 0;
      foreach (var kv in rows) {
        if (n >= MaxRowsPerRequest) break;
        if (n++ > 0) sb.Append(',');
        sb.Append("{\"heroId\":").Append(kv.Key.ToString(CultureInfo.InvariantCulture))
          .Append(",\"modeId\":").Append(kv.Value.Mode.ToString(CultureInfo.InvariantCulture))
          .Append(",\"score\":").Append(kv.Value.Score.ToString("0.0", CultureInfo.InvariantCulture))
          .Append(",\"voters\":").Append(kv.Value.Voters.ToString(CultureInfo.InvariantCulture)).Append('}');
      }
      return sb.Append("]}").ToString();
    }

    /// <summary>Sends one body; the HTTP status (0 = no answer, err says why).</summary>
    public static int Send(string site, string syncCode, string body, out string err) {
      int http; string text;
      if (!PlansClient.Send("POST", site.TrimEnd('/') + "/api/extractor/hero-grades", syncCode, body, out http, out text, out err)) return 0;
      return http;
    }
  }

  /// <summary>The rows read and not sent yet. Thread-safe; never throws.</summary>
  public sealed class HeroGradeQueue {
    readonly object gate = new object();
    readonly HashSet<long> seen = new HashSet<long>();                       // hero * 1000 + mode, this session
    readonly List<KeyValuePair<int, HeroGradeRow>> items = new List<KeyValuePair<int, HeroGradeRow>>();

    public int Count { get { lock (gate) return items.Count; } }

    /// <summary>The game restarted: everything seen is forgotten (the queue stays: it is still to be sent).</summary>
    public void NewSession() { lock (gate) seen.Clear(); }

    /// <summary>Queues the valid rows of a hero that were not taken yet this session (at most 64 rows read per hero).
    /// Returns how many were new.</summary>
    public int Offer(int hero, IList<HeroGradeRow> rows) {
      if (!HeroGrades.HeroOk(hero) || rows == null) return 0;
      int added = 0, looked = 0;
      lock (gate) {
        foreach (var r in rows) {
          if (r == null || ++looked > HeroGrades.MaxRowsPerHero) break;
          if (!HeroGrades.ModeOk(r.Mode) || r.Voters < 1 || r.Score < 0 || r.Score > 5 || double.IsNaN(r.Score)) continue;
          if (!seen.Add((long)hero * 1000 + r.Mode)) continue;
          if (items.Count >= HeroGrades.MaxQueue) items.RemoveAt(0);
          items.Add(new KeyValuePair<int, HeroGradeRow>(hero, r));
          added++;
        }
      }
      return added;
    }

    /// <summary>Takes up to <paramref name="max"/> rows out of the queue (the oldest first).</summary>
    public List<KeyValuePair<int, HeroGradeRow>> Take(int max) {
      lock (gate) {
        int n = Math.Min(max, items.Count);
        var r = items.GetRange(0, n); items.RemoveRange(0, n); return r;
      }
    }

    /// <summary>Puts rows back at the front after a failed send (the bound still holds).</summary>
    public void Return(List<KeyValuePair<int, HeroGradeRow>> rows) {
      if (rows == null || rows.Count == 0) return;
      lock (gate) {
        items.InsertRange(0, rows);
        if (items.Count > HeroGrades.MaxQueue) items.RemoveRange(0, items.Count - HeroGrades.MaxQueue);
      }
    }
  }

  public static partial class RFX {
    /// <summary>Reads the open rating screen of a hero: its hero id and the rows the game holds for that hero (the same list the
    /// screen draws). Not open: Known and !Open. Call off the interface thread; never throws.</summary>
    public static HeroGradeRead ReadHeroGrades() {
      var r = new HeroGradeRead();
      try {
        var ly = Layout.Current;
        long id = ly != null ? ly.FormOpt("ID_FORM_HEROAPPRAISE") : 0;
        if (id == 0) { r.Why = "the layout data has no rating screen"; return r; }
        ulong t = UiKnown ? uiTable : 0;
        if (t == 0) { r.Why = "the game's windows are not found yet"; return r; }
        var h = Read(t, 56); if (h == null || h[8] != 5 || h[11] > 20) { r.Why = "the windows table is unreadable"; return r; }
        int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) { r.Why = "the windows table is unreadable"; return r; }
        r.Known = true;
        ulong form = 0;
        for (int i = 0; i < nn; i++) {
          int o = i * 32;
          if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
          if (SameId(BitConverter.ToInt64(nb, o + 16), id)) { form = BitConverter.ToUInt64(nb, o); break; }
        }
        ulong v; int tt;
        if (form == 0 || !Field(form, "m_LuaLogicActive", out v, out tt) || tt != T_BOOL || (v & 0xFFFFFFFF) == 0) return r;
        r.Open = true;
        long hero;
        if (!Field(form, "m_BaseId", out v, out tt) || !AsLong(v, tt, out hero) || !HeroGrades.HeroOk(hero)) { r.Why = "the hero of the screen is unreadable"; return r; }
        r.Hero = (int)hero;
        var lt = live;
        if (lt == null || lt.HeroData == 0) { r.Why = "the hero data is not found yet"; EnsureLiveForGuides(); return r; }
        ulong all;
        if (!Field(lt.HeroData, "m_HeroScore", out all, out tt) || tt != T_TABLE) { r.Why = "no ratings in memory"; return r; }
        foreach (var e in IntEntries(all)) {
          if (e.Key != hero || e.Tt != T_TABLE) continue;
          ulong list;
          if (!Field(e.Val, "vScores", out list, out tt) || tt != T_TABLE) break;
          foreach (var row in IntEntries(list)) {
            if (row.Tt != T_TABLE || row.Key < 1 || r.Rows.Count >= HeroGrades.MaxRowsPerHero) continue;
            ulong a; int at; long mode, sum, voters; double score;
            if (!Field(row.Val, "iType", out a, out at) || !AsLong(a, at, out mode) || !HeroGrades.ModeOk(mode)) continue;
            if (!Field(row.Val, "iScore", out a, out at) || !AsLong(a, at, out sum)) continue;
            if (!Field(row.Val, "iMarkNum", out a, out at) || !AsLong(a, at, out voters)) continue;
            if (!HeroGrades.Shown(sum, voters, out score)) continue;
            r.Rows.Add(new HeroGradeRow { Mode = (int)mode, Score = score, Voters = (int)voters });
          }
          break;
        }
      } catch (Exception e) { r.Why = "error " + e.GetType().Name; r.Rows.Clear(); }
      return r;
    }
  }
}
