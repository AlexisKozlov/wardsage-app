// Wardsage - TEST build: the lab runner (tools/lab/WardsageLab.ps1) starts and stops the screen recorder (app/RecCapture.cs) without clicks,
// through a command file; app/HostBridge.cs reads it once a second and answers in an ack file. Both live in %LOCALAPPDATA%\WardsageTest\lab\.
//   rec.cmd  one line:  "<seq> start <scenario> <run id>"   or   "<seq> stop"
//            seq = the runner's clock (unix ms) at the command: a new one per command, an old file (over StaleMs) is never acted on
//   rec.ack  {"seq":N,"state":"recording|stopped|idle|busy|bad","rec":"<id>","scenario":"..","run":".."}
// A lab recording has its own id (lab-yyyyMMdd-HHmmss-xxxxxx), lab.json with the scenario and the run in its first part, at most CapMs, and
// is uploaded like any other one; the lab server deletes it when the scenario passed. This file is the pure part: parsing and the decision.
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RealmForge {
  public sealed class LabRecCmd {
    public long Seq;
    public string Op = "", Scenario = "", Run = "";

    public const long CapMs = 6 * 60 * 1000;          // a lab recording stops by itself after 6 min
    public const long StaleMs = 2 * 60 * 1000;        // a command older than this is left alone (an app started after the run)
    public const long StartWaitMs = 10000;            // the wait for another recording to stop before a lab one starts

    static readonly Regex LineRx = new Regex("^([0-9]{1,15}) (?:(start) ([a-z0-9_]{1,40}) ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})|(stop))$");
    static readonly Regex IdRx = new Regex("^lab-[0-9]{8}-[0-9]{6}-[0-9a-f]{6}$");

    /// <summary>The command of the file's text (its first line), null when it is not exactly one of the two forms.</summary>
    public static LabRecCmd Parse(string text) {
      if (text == null || text.Length > 200) return null;
      string line = text;
      int nl = line.IndexOfAny(new[] { '\r', '\n' });
      if (nl >= 0) line = line.Substring(0, nl);
      var m = LineRx.Match(line);
      if (!m.Success) return null;
      long seq;
      if (!long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out seq)) return null;
      if (m.Groups[2].Success) return new LabRecCmd { Seq = seq, Op = "start", Scenario = m.Groups[3].Value, Run = m.Groups[4].Value };
      return new LabRecCmd { Seq = seq, Op = "stop" };
    }

    public static string NewId(DateTime now, Random rnd) { return "lab-" + RecNames.NewId(now, rnd); }
    public static bool IsLabId(string id) { return id != null && IdRx.IsMatch(id); }

    /// <summary>lab.json of the recording's first part.</summary>
    public static string LabJson(string scenario, string run, string app) {
      return "{\"lab\":true,\"scenario\":" + MiniJson.Quote(scenario ?? "") + ",\"run\":" + MiniJson.Quote(run ?? "") + ",\"app\":" + MiniJson.Quote(app ?? "") + "}";
    }

    /// <summary>A tag added while a lab recording runs (an automatic recording's reason): tag_NNN.json, t = ms into the recording.</summary>
    public static string TagJson(string tag, long t) {
      return "{\"tag\":" + MiniJson.Quote(tag ?? "") + ",\"t\":" + t.ToString(CultureInfo.InvariantCulture) + "}";
    }

    public static string Ack(long seq, string state, string rec, string scenario, string run) {
      return "{\"seq\":" + seq.ToString(CultureInfo.InvariantCulture) + ",\"state\":" + MiniJson.Quote(state ?? "") + ",\"rec\":" + MiniJson.Quote(rec ?? "")
        + ",\"scenario\":" + MiniJson.Quote(scenario ?? "") + ",\"run\":" + MiniJson.Quote(run ?? "") + "}";
    }
  }

  /// <summary>What to do with a command, given the recorder. One instance per app run; it remembers the last command done.</summary>
  public sealed class LabRecControl {
    public enum Act { None, Start, Stop, StopThenStart }
    long lastSeq = -1;

    /// <summary>The action for <paramref name="c"/> at <paramref name="nowMs"/> (unix ms) while the recorder is <paramref name="recOn"/> with
    /// <paramref name="currentRec"/>; state = the ack's state (null = nothing to answer: the same or an old command).</summary>
    public Act Decide(LabRecCmd c, long nowMs, bool recOn, string currentRec, out string state) {
      state = null;
      if (c == null || c.Seq <= lastSeq) return Act.None;
      lastSeq = c.Seq;
      if (nowMs - c.Seq > LabRecCmd.StaleMs || c.Seq - nowMs > LabRecCmd.StaleMs) { state = "stale"; return Act.None; }
      if (c.Op == "start") { state = "recording"; return recOn ? Act.StopThenStart : Act.Start; }
      if (c.Op == "stop") {
        // only a lab recording is stopped: a manual one of the owner goes on
        if (recOn && LabRecCmd.IsLabId(currentRec)) { state = "stopped"; return Act.Stop; }
        state = "idle"; return Act.None;
      }
      return Act.None;
    }

    /// <summary>An automatic recording's trigger while a lab recording runs only tags it (no second recording).</summary>
    public static bool TagInstead(bool recOn, string currentRec) { return recOn && LabRecCmd.IsLabId(currentRec); }
  }
}
