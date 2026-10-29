// Wardsage - the TEST build records the screen by itself when something goes wrong (app/HostBridge.cs starts app/RecCapture.cs): a pilot
// stopped by the click gate (any reason), an unknown form on top of an unrecognised screen, a failed close of an offer pop-up. The
// recording is the next RecordMs (the recorder keeps no buffer of the past) and goes to the developers like a manual one, with the
// reason inside (auto.json in its first part). This file is the pure decision: which events record, and how often.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public sealed class AutoRecPolicy {
    /// <summary>How long an automatic recording runs.</summary>
    public const int RecordMs = 20000;
    /// <summary>The least gap between two automatic recordings, and the most of them in an hour.</summary>
    public const long MinGapMs = 2 * 60 * 1000, HourMs = 60 * 60 * 1000;
    public const int MaxPerHour = 10;

    readonly List<long> starts = new List<long>();

    /// <summary>The reason tag of a click gate stop (owner, reason, the detail cut short); an offer that was not closed is tagged by its form.</summary>
    public static string HaltTag(string owner, string reason, string detail) {
      string d = detail ?? "";
      int k = d.IndexOf("OfferClose(", StringComparison.Ordinal);
      if (k >= 0) {
        int e = d.IndexOf(')', k);
        if (e > k) return "offer-fail " + d.Substring(k + 11, e - k - 11) + " " + reason;
      }
      if (reason == "offer" || reason == "offers") {
        foreach (var f in OfferForms.All) if (d.Contains(f.Name)) return "offer-fail " + f.Name + " " + reason;
      }
      if (d.Length > 80) d = d.Substring(0, 80);
      return "halt " + owner + " " + reason + (d != "" ? ": " + d : "");
    }

    /// <summary>The tag of an unknown form seen on top of an unrecognised screen.</summary>
    public static string UnknownFormTag(string form) { return "unknown-form " + form; }

    /// <summary>May a recording start now (the test build only, no recording running)? True = it starts and is counted;
    /// <paramref name="why"/> says why not otherwise (for the log).</summary>
    public bool Want(bool testBuild, bool recording, long nowMs, out string why) {
      why = "";
      if (!testBuild) { why = "not the test build"; return false; }
      if (recording) { why = "a recording is running"; return false; }
      starts.RemoveAll(t => nowMs - t >= HourMs);
      if (starts.Count > 0 && nowMs - starts[starts.Count - 1] < MinGapMs) { why = "the last one was " + (nowMs - starts[starts.Count - 1]) / 1000 + " s ago"; return false; }
      if (starts.Count >= MaxPerHour) { why = MaxPerHour + " in the last hour"; return false; }
      starts.Add(nowMs);
      return true;
    }
  }
}
