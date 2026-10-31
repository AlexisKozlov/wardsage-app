// Wardsage - TEST build: the frame audit. Every hint frame the overlay draws (app/Overlay.cs Scene) is followed per (scenario, step, target);
// once one has stood still for StableMs it is checked once per lab run: the game's client with our overlay on it is captured (app/RecCapture.cs
// FrameAuditSink, a lab recording of its own: lab-<date>-<time>-<first 6 hex of the run>, one shot a part, audit.json + audit_NNN.jpg/.json),
// and the frame is flagged ("frame audit: FLAG <key> <why>") when:
//   - the live widget rect and the layout / graph rect of the same widget disagree by more than max(12 px, 15 % of the size) (centre or size)
//   - the frame is outside the game's client
//   - the frame overlaps a hint plate
//   - two or more frames are drawn at once
//   - the frame stands on a hidden widget, or on one whose form is covered (the live read said so, the layout's rect was drawn)
// tools/lab/frame-audit.sh downloads the shots and builds a contact sheet. This file is the pure part (rules, stability, the sidecar JSON).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  /// <summary>What is known of one stable frame when it is checked (client px).</summary>
  public sealed class FrameAuditShot {
    public string Scenario = "", Run = "", Step = "", Target = "", Source = "", Widget = "", Sprite = "", Shape = "", Why = "";
    /// <summary>The drawn frame, the live widget rect (null = none read), the layout / graph rect of the same target (null = none): x, y, w, h.</summary>
    public double[] Frame, Live, Layout;
    /// <summary>The hint plates shown at the same time (x, y, w, h).</summary>
    public List<double[]> Plates = new List<double[]>();
    public int ClientW, ClientH;
    /// <summary>Frames drawn in the same tick (this one included).</summary>
    public int FramesNow = 1;
    public bool Hidden, Covered;
    /// <summary>The capture's image px per client px (0 = no image).</summary>
    public double Scale;
    public string Image = "";
    public long AtMs;
  }

  public static class FrameAudit {
    public const long StableMs = 400;
    /// <summary>The frame may move this much (px) and still be the same one.</summary>
    public const double SamePx = 3;
    public const int MaxShotsPerRun = 300;
    public const int ImageMaxWidth = 1280, ImageMaxBytes = 150 * 1024;

    /// <summary>The tolerance of a disagreement on an axis of <paramref name="size"/> px.</summary>
    public static double Tol(double size) { return Math.Max(12, 0.15 * Math.Abs(size)); }

    /// <summary>Live minus layout: {centre dx, centre dy, dw, dh}; null when one is missing.</summary>
    public static double[] Disagree(double[] live, double[] layout) {
      if (live == null || layout == null || live.Length < 4 || layout.Length < 4) return null;
      return new[] { (live[0] + live[2] / 2) - (layout[0] + layout[2] / 2), (live[1] + live[3] / 2) - (layout[1] + layout[3] / 2), live[2] - layout[2], live[3] - layout[3] };
    }

    /// <summary>The overlap of two rects (w, h; 0 when apart).</summary>
    public static double[] Overlap(double[] a, double[] b) {
      if (a == null || b == null) return new double[] { 0, 0 };
      double w = Math.Min(a[0] + a[2], b[0] + b[2]) - Math.Max(a[0], b[0]), h = Math.Min(a[1] + a[3], b[1] + b[3]) - Math.Max(a[1], b[1]);
      return new[] { Math.Max(0, w), Math.Max(0, h) };
    }

    /// <summary>Intersection over union of two rects (0..1).</summary>
    public static double IoU(double[] a, double[] b) {
      if (a == null || b == null) return 0;
      var o = Overlap(a, b); double i = o[0] * o[1], u = a[2] * a[3] + b[2] * b[3] - i;
      return u > 0 ? i / u : 0;
    }

    /// <summary>Why the frame is suspect ("" each when not); empty list = clean.</summary>
    public static List<string> Flags(FrameAuditShot s) {
      var res = new List<string>();
      if (s == null || s.Frame == null || s.Frame.Length < 4) { res.Add("no frame rect"); return res; }
      var f = s.Frame;
      var d = Disagree(s.Live, s.Layout);
      if (d != null) {
        double tw = Tol(s.Layout[2]), th = Tol(s.Layout[3]);
        if (Math.Abs(d[0]) > tw || Math.Abs(d[1]) > th) res.Add("live vs layout centre dx=" + R(d[0]) + " dy=" + R(d[1]) + " (tolerance " + R(tw) + "x" + R(th) + ")");
        if (Math.Abs(d[2]) > tw || Math.Abs(d[3]) > th) res.Add("live vs layout size dw=" + R(d[2]) + " dh=" + R(d[3]) + " (tolerance " + R(tw) + "x" + R(th) + ")");
      }
      if (s.ClientW > 0 && s.ClientH > 0 && (f[0] < -2 || f[1] < -2 || f[0] + f[2] > s.ClientW + 2 || f[1] + f[3] > s.ClientH + 2 || f[2] < 2 || f[3] < 2))
        res.Add("outside the client [" + R(f[0]) + "," + R(f[1]) + "," + R(f[2]) + "," + R(f[3]) + "] of " + s.ClientW + "x" + s.ClientH);
      if (s.Plates != null)
        foreach (var p in s.Plates) {
          var o = Overlap(f, p);
          if (o[0] > 2 && o[1] > 2) { res.Add("overlaps the hint box by " + R(o[0]) + "x" + R(o[1])); break; }
        }
      if (s.FramesNow >= 2) res.Add(s.FramesNow + " frames at once");
      if (s.Hidden) res.Add("on a hidden widget" + (s.Why != "" ? " (" + s.Why + ")" : ""));
      else if (s.Covered) res.Add("on a covered widget" + (s.Why != "" ? " (" + s.Why + ")" : ""));
      return res;
    }

    /// <summary>The live read's reason for the layout's rect, as hidden / covered.</summary>
    public static void HiddenOrCovered(string why, out bool hidden, out bool covered) {
      hidden = why == "not visible";
      covered = why == "not on top" || why == "form not on top";
    }

    /// <summary>The step's key from its plate's text: digits dropped (a row count is not another step), spaces folded, at most 60 chars.</summary>
    public static string StepKey(string caps, string main) {
      string t = ((caps ?? "") + " " + (main ?? "")).Trim();
      t = Regex.Replace(t, "[0-9]+", "#");
      t = Regex.Replace(t, "[\\s|]+", " ").Trim();
      if (t.Length > 60) t = t.Substring(0, 60);
      return t == "" ? "-" : t;
    }

    public static string Key(FrameAuditShot s) { return s.Step + " / " + s.Target; }
    public static string FlagLine(FrameAuditShot s, string why) { return "frame audit: FLAG " + Key(s) + " " + why; }

    /// <summary>The audit recording's id: lab-yyyyMMdd-HHmmss-&lt;the run's first 6 hex&gt; (the lab server finds it by the run).</summary>
    public static string RecId(DateTime now, string run) {
      string h = run != null && Regex.IsMatch(run, "^[0-9a-f]{6}") ? run.Substring(0, 6) : "000000";
      return "lab-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + h;
    }

    public static string MetaJson(string run, string app) {
      return "{\"audit\":true,\"run\":" + MiniJson.Quote(run ?? "") + ",\"app\":" + MiniJson.Quote(app ?? "") + "}";
    }

    /// <summary>The shot's sidecar JSON (one line).</summary>
    public static string Json(FrameAuditShot s, List<string> flags) {
      var sb = new StringBuilder("{");
      sb.Append("\"scenario\":").Append(MiniJson.Quote(s.Scenario)).Append(",\"run\":").Append(MiniJson.Quote(s.Run))
        .Append(",\"step\":").Append(MiniJson.Quote(s.Step)).Append(",\"target\":").Append(MiniJson.Quote(s.Target))
        .Append(",\"source\":").Append(MiniJson.Quote(s.Source)).Append(",\"widget\":").Append(MiniJson.Quote(s.Widget))
        .Append(",\"sprite\":").Append(MiniJson.Quote(s.Sprite)).Append(",\"shape\":").Append(MiniJson.Quote(s.Shape)).Append(",\"why\":").Append(MiniJson.Quote(s.Why))
        .Append(",\"frame\":").Append(Arr(s.Frame)).Append(",\"live\":").Append(Arr(s.Live)).Append(",\"layout\":").Append(Arr(s.Layout))
        .Append(",\"plates\":[");
      for (int i = 0; s.Plates != null && i < s.Plates.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Arr(s.Plates[i])); }
      sb.Append("],\"client\":[").Append(s.ClientW).Append(',').Append(s.ClientH).Append("],\"framesNow\":").Append(s.FramesNow)
        .Append(",\"hidden\":").Append(s.Hidden ? "true" : "false").Append(",\"covered\":").Append(s.Covered ? "true" : "false");
      var d = Disagree(s.Live, s.Layout);
      sb.Append(",\"disagree\":").Append(Arr(d));
      sb.Append(",\"scale\":").Append(s.Scale.ToString("0.####", CultureInfo.InvariantCulture)).Append(",\"image\":").Append(MiniJson.Quote(s.Image))
        .Append(",\"at\":").Append(s.AtMs.ToString(CultureInfo.InvariantCulture)).Append(",\"flags\":[");
      for (int i = 0; flags != null && i < flags.Count; i++) { if (i > 0) sb.Append(','); sb.Append(MiniJson.Quote(flags[i])); }
      return sb.Append("]}").ToString();
    }

    static string Arr(double[] a) {
      if (a == null) return "null";
      var p = new string[a.Length];
      for (int i = 0; i < a.Length; i++) p[i] = R(a[i]);
      return "[" + string.Join(",", p) + "]";
    }
    static string R(double v) { return Math.Round(v).ToString(CultureInfo.InvariantCulture); }
  }

  /// <summary>When a frame has stood still long enough to be checked: once per (scenario, step, target) per run.</summary>
  public sealed class FrameAuditTracker {
    sealed class Seen { public double[] Rect; public long Since, Last; }
    readonly Dictionary<string, Seen> seen = new Dictionary<string, Seen>();
    readonly HashSet<string> done = new HashSet<string>();
    string run = "";

    public int Done { get { return done.Count; } }

    /// <summary>A new run forgets what was checked.</summary>
    public void Run(string r) { if ((r ?? "") != run) { run = r ?? ""; done.Clear(); seen.Clear(); } }

    /// <summary>The frame <paramref name="rect"/> of <paramref name="key"/> is drawn at <paramref name="now"/> (ms); true once when it has
    /// stood (within SamePx, seen at least every 1 s) for StableMs and was not checked in this run yet.</summary>
    public bool Observe(string key, double[] rect, long now) {
      if (key == null || rect == null || rect.Length < 4) return false;
      Seen s;
      if (!seen.TryGetValue(key, out s) || now - s.Last > 1000 || now < s.Last || Moved(s.Rect, rect)) {
        if (seen.Count > 500) seen.Clear();
        seen[key] = new Seen { Rect = (double[])rect.Clone(), Since = now, Last = now };
        return false;
      }
      s.Last = now;
      if (now - s.Since < FrameAudit.StableMs || done.Contains(key) || done.Count >= FrameAudit.MaxShotsPerRun) return false;
      done.Add(key);
      return true;
    }

    static bool Moved(double[] a, double[] b) {
      for (int i = 0; i < 4; i++) if (Math.Abs(a[i] - b[i]) > FrameAudit.SamePx) return true;
      return false;
    }
  }
}
