// RealmForge extractor - the extraction pipeline around the memory reader:
// find the game -> read its version -> read memory (RFX) -> check the result.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RealmForge {
  public enum ExtractError { None, GameNotRunning, AccessDenied, OpenFailed, NoAccountData, Failed }

  /// <summary>What the host does with a finished reading.</summary>
  public enum SyncKind { Ok, NotRunning, Access, NotReady, ReadFailed }

  public static class SyncOutcome {
    /// <summary>An automatic sync that finds no account yet is not a failure: the game is still loading or at the login
    /// screen - no error card, no report, a later retry. A manual sync reports it.</summary>
    public static SyncKind Classify(bool auto, ExtractError e) {
      switch (e) {
        case ExtractError.None: return SyncKind.Ok;
        case ExtractError.GameNotRunning: return SyncKind.NotRunning;
        case ExtractError.AccessDenied: return SyncKind.Access;
        case ExtractError.NoAccountData: return auto ? SyncKind.NotReady : SyncKind.ReadFailed;
        default: return SyncKind.ReadFailed;
      }
    }

    /// <summary>The scan figures of the reader's log (regions / MB, strings, tables, heroes) on one line, so a real failure
    /// can be told apart from a game that has not loaded the account.</summary>
    public static string ScanStats(string log) {
      if (string.IsNullOrEmpty(log)) return null;
      var keep = new List<string>();
      foreach (var raw in log.Split('\n')) {
        var l = raw.Trim();
        if (l.StartsWith("RW regions", StringComparison.Ordinal) || l.StartsWith("Strings found", StringComparison.Ordinal)
            || l.StartsWith("item tables", StringComparison.Ordinal) || l.StartsWith("hero tables", StringComparison.Ordinal)
            || l.StartsWith("live equipment map", StringComparison.Ordinal) || l.StartsWith("live hero map", StringComparison.Ordinal)
            || l.StartsWith("equipment items", StringComparison.Ordinal) || l.StartsWith("heroes (owned)", StringComparison.Ordinal))
          keep.Add(l);
      }
      return keep.Count == 0 ? null : string.Join("; ", keep.ToArray());
    }
  }

  public sealed class ExtractResult {
    public ExtractError Error;
    public string Detail;         // extra text for the error message (Win32 code, exception message)
    public string Json;           // account.json content when Error == None
    public string GameVersion;    // may be null
    public int Heroes, Items, Artifacts;
    public int Seconds;
  }

  // Turns the log lines of RFX.Run() into a 0..1 progress value. RFX makes 13 full passes over the
  // game memory; each of the lines below is written right after one of them finishes.
  public sealed class ReadProgress {
    const int TotalPasses = 13;
    static readonly string[] PassDone = {
      "Strings found", "  nodes keyed", "  tables:", "  references:", "  equipment items (owned)", "  heroes (owned)", "  tables with '"
    };
    int passes;

    public double Feed(string line) {
      if (line != null)
        foreach (var p in PassDone)
          if (line.StartsWith(p, StringComparison.Ordinal)) { passes = Math.Min(TotalPasses, passes + 1); break; }
      return (double)passes / TotalPasses;
    }
  }

  public static class Extractor {
    internal static readonly object Gate = new object();   // RFX keeps its state in static fields: one run at a time

    public static string OutputDir {
      get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Channel.FolderName); }
    }

    // Step 1: is the game running? Returns the process id (0 = not running) and the game version.
    public static int FindGame(out string gameVersion) {
      gameVersion = null;
      int pid = GameInfo.FindGameProcess();
      if (pid != 0) gameVersion = GameInfo.TryReadVersion(GameInfo.TryGetExePath(pid));
      return pid;
    }

    // Step 2: read the account from game memory. onLog receives every log line of the reader
    // (it is called on the calling thread and must not throw).
    public static ExtractResult Read(string gameVersion, Action<string> onLog) {
      var res = new ExtractResult();
      res.GameVersion = gameVersion;
      string json;
      var started = DateTime.UtcNow;
      lock (Gate) {
        RFX.Log.Length = 0;
        RFX.GameVersion = gameVersion;
        RFX.OnLog = onLog;
        try { json = RFX.Run(); }
        catch (Exception e) { res.Error = ExtractError.Failed; res.Detail = e.GetType().Name + ": " + e.Message; return res; }
        finally { RFX.OnLog = null; }

        if (json == null) {
          if (RFX.LastError == "not_running") res.Error = ExtractError.GameNotRunning;
          else if (RFX.LastError == "open_failed" && RFX.LastOpenError == 5) res.Error = ExtractError.AccessDenied;
          else if (RFX.LastError == "open_failed") { res.Error = ExtractError.OpenFailed; res.Detail = RFX.LastOpenError.ToString(); }
          else res.Error = ExtractError.Failed;
          return res;
        }
      }
      res.Seconds = (int)(DateTime.UtcNow - started).TotalSeconds;
      Check(json, res);
      if (res.Error == ExtractError.NoAccountData && res.Detail == null) res.Detail = "no heroes or items; " + SyncOutcome.ScanStats(RFX.Log.ToString());
      return res;
    }

    // Validates the produced JSON and counts what is in it.
    public static ExtractResult Check(string json, ExtractResult res) {
      Dictionary<string, object> root;
      try { root = MiniJson.Parse(json) as Dictionary<string, object>; }
      catch (FormatException e) { res.Error = ExtractError.Failed; res.Detail = "invalid JSON: " + e.Message; return res; }
      if (root == null) { res.Error = ExtractError.Failed; res.Detail = "invalid JSON root"; return res; }
      res.Heroes = Math.Max(0, MiniJson.CountOf(root, "heroes"));
      res.Items = Math.Max(0, MiniJson.CountOf(root, "equipment"));
      res.Artifacts = Math.Max(0, MiniJson.CountOf(root, "artifacts"));
      if (res.Heroes == 0 && res.Items == 0) { res.Error = ExtractError.NoAccountData; return res; }
      res.Json = json;
      return res;
    }

    // Writes account.json and extract_log.txt to Documents\RealmForge. Returns the account.json path.
    public static string SaveCopy(string json, string log) {
      string dir = OutputDir;
      Directory.CreateDirectory(dir);
      var enc = new UTF8Encoding(false);
      string path = Path.Combine(dir, "account.json");
      File.WriteAllText(path, json, enc);
      if (log != null) File.WriteAllText(Path.Combine(dir, "extract_log.txt"), log, enc);
      return path;
    }
  }
}
