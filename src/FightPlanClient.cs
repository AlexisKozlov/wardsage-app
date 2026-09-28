// RealmForge — fight plans («Отправить план в игру») from the RealmForge site, for the boss coach over the game.
//
// Contract (implemented by the site, lib/fight/handler.ts; the plan: lib/fight/types.ts FightPlan):
//   GET {site}/api/extractor/fight-plans      Authorization: Bearer <sync code>
//       200 {ok:true, plans:[{v:1, boss, stage, name:{ru,en}, field:{w,h,t,boss:[x,y],half:[x,y]}|null,
//            steps:[{u, id, n:{ru,en}, sq, why, x, y, dir}], hold, holdU:[u], pts}]}   401 invalid_token
// Coordinates are the game's (x column from the left, y row from the bottom, t column-major x·h + y); dir 0 up (+y),
// 1 right, 2 down, 3 left. A plan stays until the player replaces or removes it on the site.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public sealed class FightStep {
    public long Uid; public int HeroId; public string NameRu, NameEn; public int Squad; public string Why;
    public int X = -1, Y = -1, Dir = -1;   // -1: no tile (a reserve)
    public bool Bench { get { return Why == "bench"; } }
  }

  public sealed class FightPlan {
    public string Boss, NameRu, NameEn; public int Stage;
    public int W, H; public int[] Cells;   // null: no field
    public int BossX, BossY, HalfX, HalfY;
    public List<FightStep> Steps = new List<FightStep>();
    public double Hold; public HashSet<long> HoldU = new HashSet<long>(); public long Pts;
    public int Cell(int x, int y) { return Cells != null && x >= 0 && y >= 0 && x < W && y < H ? Cells[x * H + y] : 0; }
  }

  public sealed class FightPlansResult {
    public PlansStatus Status; public int HttpCode; public string Details;
    public List<FightPlan> Plans = new List<FightPlan>();
  }

  public static class FightPlanClient {
    const int MaxSide = 32, MaxSteps = 12, MaxPlans = 100;

    public static FightPlansResult Get(string site, string code) {
      int http; string text, err;
      if (!PlansClient.Send("GET", site + "/api/extractor/fight-plans", code, null, out http, out text, out err))
        return new FightPlansResult { Status = PlansStatus.Unreachable, Details = err };
      return Interpret(http, text);
    }

    // Maps an HTTP reply to the plans; a malformed plan is skipped, not fatal. Pure function (tested without a network).
    public static FightPlansResult Interpret(int code, string body) {
      var r = new FightPlansResult { HttpCode = code };
      var obj = MiniJson.AsObject(MiniJson.TryParse(body));
      if (code >= 200 && code < 300) {
        if (obj == null || !MiniJson.GetBool(obj, "ok", false)) { r.Status = PlansStatus.Unexpected; return r; }
        r.Status = PlansStatus.Ok;
        object v; var list = obj.TryGetValue("plans", out v) ? v as List<object> : null;
        if (list != null) foreach (var x in list) {
          if (r.Plans.Count >= MaxPlans) break;
          var p = Plan(MiniJson.AsObject(x)); if (p != null) r.Plans.Add(p);
        }
        return r;
      }
      if (code == 401) r.Status = PlansStatus.InvalidToken;
      else if (code == 404) r.Status = PlansStatus.NotFound;
      else if (code >= 500 && code < 600) r.Status = PlansStatus.ServerError;
      else r.Status = PlansStatus.Unexpected;
      return r;
    }

    static FightPlan Plan(Dictionary<string, object> o) {
      if (o == null) return null;
      var p = new FightPlan { Boss = Clip(MiniJson.GetString(o, "boss"), 40), Stage = (int)Num(o, "stage", 0) };
      if (string.IsNullOrEmpty(p.Boss) || p.Stage <= 0) return null;
      var n = Obj(o, "name");
      p.NameRu = Clip(n != null ? MiniJson.GetString(n, "ru") : null, 100) ?? p.Boss;
      p.NameEn = Clip(n != null ? MiniJson.GetString(n, "en") : null, 100) ?? p.NameRu;
      var f = Obj(o, "field");
      if (f != null) {
        int w = (int)Num(f, "w", 0), h = (int)Num(f, "h", 0);
        var t = Arr(f, "t"); var b = Arr(f, "boss"); var hf = Arr(f, "half");
        if (w > 0 && h > 0 && w <= MaxSide && h <= MaxSide && t != null && t.Count == w * h && b != null && b.Count == 2 && hf != null && hf.Count == 2) {
          p.W = w; p.H = h; p.Cells = new int[w * h];
          for (int i = 0; i < t.Count; i++) p.Cells[i] = t[i] is double ? (int)(double)t[i] : 0;
          p.BossX = Int(b[0]); p.BossY = Int(b[1]); p.HalfX = Int(hf[0]); p.HalfY = Int(hf[1]);
        }
      }
      var steps = Arr(o, "steps");
      if (steps == null) return null;
      foreach (var x in steps) {
        var s = MiniJson.AsObject(x); if (s == null || p.Steps.Count >= MaxSteps) continue;
        long u = Num(s, "u", 0); if (u <= 0) continue;
        var sn = Obj(s, "n");
        var st = new FightStep {
          Uid = u, HeroId = (int)Num(s, "id", 0), Squad = (int)Num(s, "sq", 1), Why = Clip(MiniJson.GetString(s, "why"), 16) ?? "dmg",
          NameRu = Clip(sn != null ? MiniJson.GetString(sn, "ru") : null, 60), NameEn = Clip(sn != null ? MiniJson.GetString(sn, "en") : null, 60),
        };
        if (st.NameRu == null) st.NameRu = st.NameEn ?? u.ToString();
        if (st.NameEn == null) st.NameEn = st.NameRu;
        int sx = (int)Num(s, "x", -1), sy = (int)Num(s, "y", -1), sd = (int)Num(s, "dir", -1);
        if (p.Cells != null && sx >= 0 && sy >= 0 && sx < p.W && sy < p.H && sd >= 0 && sd <= 3) { st.X = sx; st.Y = sy; st.Dir = sd; }
        p.Steps.Add(st);
      }
      if (p.Steps.Count == 0) return null;
      double hold; object hv; p.Hold = o.TryGetValue("hold", out hv) && hv is double && (hold = (double)hv) > 0 && hold <= 60 ? hold : 0;
      var hu = Arr(o, "holdU");
      if (hu != null) foreach (var x in hu) if (x is double && (double)x > 0) p.HoldU.Add((long)(double)x);
      p.Pts = Math.Max(0, Num(o, "pts", 0));
      return p;
    }

    static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? MiniJson.AsObject(v) : null; }
    static List<object> Arr(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v as List<object> : null; }
    static int Int(object v) { return v is double ? (int)(double)v : 0; }
    static long Num(Dictionary<string, object> d, string key, long dflt) {
      object v; if (!d.TryGetValue(key, out v) || !(v is double)) return dflt;
      double x = (double)v; return x >= -1 && x < 9e15 ? (long)x : dflt;
    }
    static string Clip(string s, int n) { return s == null ? null : s.Length <= n ? s : s.Substring(0, n); }

    /// <summary>The plan for a fight: its stage, the one whose heroes the fight has most of (the two teams of a
    /// two-team boss share the stage), or null.</summary>
    public static FightPlan For(IList<FightPlan> plans, int stage, ICollection<long> fightHeroes) {
      FightPlan best = null; int bestN = -1;
      if (plans == null) return null;
      foreach (var p in plans) {
        if (p.Stage != stage) continue;
        int n = 0; if (fightHeroes != null) foreach (var s in p.Steps) if (fightHeroes.Contains(s.Uid)) n++;
        if (n > bestN) { best = p; bestN = n; }
      }
      return best;
    }

    /// <summary>What the plan says now: the step to do next (null: every hero of the field is placed), whether it is a
    /// reserve, and the first hero standing off its planned tile.</summary>
    public static void State(FightPlan p, IList<HeroSample> hs, out FightStep next, out FightStep wrong, out int wrongX, out int wrongY) {
      next = null; wrong = null; wrongX = wrongY = -1;
      var by = new Dictionary<long, HeroSample>();
      if (hs != null) foreach (var h in hs) by[h.Uid] = h;
      bool anyDown = false;
      foreach (var s in p.Steps) {
        HeroSample h; bool known = by.TryGetValue(s.Uid, out h);
        bool up = known && h.OnField && !h.Dead;
        if (!s.Bench) {
          if (known && h.CardState == 2) anyDown = true;   // fell, its card is being reborn
          if (up && s.X >= 0 && h.X >= 0 && (h.X != s.X || h.Y != s.Y) && wrong == null) { wrong = s; wrongX = h.X; wrongY = h.Y; }
          if (!up && next == null && !(known && h.CardState == 2)) next = s;
        }
      }
      if (next != null) return;
      // the field is set: a reserve goes in when a hero of the field has fallen
      if (!anyDown) return;
      foreach (var s in p.Steps) {
        if (!s.Bench) continue;
        HeroSample h; bool up = by.TryGetValue(s.Uid, out h) && h.OnField && !h.Dead;
        if (!up) { next = s; return; }
      }
    }

  }
}
