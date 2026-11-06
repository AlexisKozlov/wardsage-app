// Wardsage - TEST build: names and files of a lab recording of the screen recorder (app/RecCapture.cs). The lab runner used to drive the app
// through a command file with clicks; that is gone (the program presses nothing in the game), only the recording's id, lab.json and tags stay.
//   A lab recording has its own id (lab-yyyyMMdd-HHmmss-xxxxxx), lab.json with the scenario and the run in its first part, at most CapMs.
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RealmForge {
  public static class LabRecCmd {
    public const long CapMs = 6 * 60 * 1000;          // a lab recording stops by itself after 6 min
    static readonly Regex IdRx = new Regex("^lab-[0-9]{8}-[0-9]{6}-[0-9a-f]{6}$");

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
  }
}
