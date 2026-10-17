// Replays a TEST-build screen recording on the app's pure decision code, without the game (src/RecInputs.cs).
//
//   dotnet exec ... RecReplay.dll <recording dir with part_*.zip> <out dir>
//   dotnet exec ... RecReplay.dll --make-fixture <dir> <geometry.json>      (a tiny synthetic recording for the test run)
//
// For every frame line the raw inputs recorded with it are rebuilt, ScreenTracker.Update / LocateGuide.Next (or the storage cleanup's
// SellHint.BuildExact) and the scene rectangles (RecScenes) are run on them, and out/<frame>.json is written:
//   {frame, t, w, h, scale, mode, screenRecorded, screenReplayed, stepRecorded, stepReplayed, rectsRecorded, rectsReplayed (null = not replayable),
//    mismatch: [reasons]}
// plus out/summary.txt (the recording, the steps timeline, the mismatches). Mismatches are information, not a failure: the exit code is 0
// unless the recording cannot be read. tests/rec-draw.mjs draws the rectangles over the recorded pictures.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using RealmForge;

static class RecReplay {
  const int Tol = 1;   // px: the recorded rectangles come from doubles cut to ints, a pixel of difference is not a mismatch
  static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

  static Dictionary<string, object> O(object o) { return o as Dictionary<string, object>; }
  static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
  static double Num(Dictionary<string, object> d, string k, double def) { var v = Get(d, k); return v is double ? (double)v : def; }
  static string Str(Dictionary<string, object> d, string k) { return Get(d, k) as string; }

  sealed class Frame { public string Name; public Dictionary<string, object> J; }

  static string ReadText(ZipArchiveEntry e) { using (var r = new StreamReader(e.Open(), new UTF8Encoding(false))) return r.ReadToEnd(); }

  static string RectsJson(List<RecFrameRect> l) {
    if (l == null) return "null";
    var sb = new StringBuilder("[");
    for (int i = 0; i < l.Count; i++) {
      if (i > 0) sb.Append(',');
      sb.Append("{\"kind\":").Append(MiniJson.Quote(l[i].Kind)).Append(",\"x\":").Append(l[i].X).Append(",\"y\":").Append(l[i].Y).Append(",\"w\":").Append(l[i].W).Append(",\"h\":").Append(l[i].H).Append('}');
    }
    return sb.Append(']').ToString();
  }

  static List<RecFrameRect> RectsOf(object o) {
    var r = new List<RecFrameRect>(); var l = o as List<object>;
    if (l != null) foreach (var x in l) { var d = O(x); if (d != null) r.Add(new RecFrameRect { Kind = Str(d, "kind") ?? "", X = (int)Num(d, "x", 0), Y = (int)Num(d, "y", 0), W = (int)Num(d, "w", 0), H = (int)Num(d, "h", 0) }); }
    return r;
  }

  /// <summary>Same rectangles (any order, each within Tol px)? why = the first difference.</summary>
  static bool SameRects(List<RecFrameRect> a, List<RecFrameRect> b, out string why) {
    why = "";
    if (a.Count != b.Count) { why = "rects: " + a.Count + " recorded, " + b.Count + " replayed"; return false; }
    var used = new bool[b.Count];
    foreach (var x in a) {
      int hit = -1;
      for (int i = 0; i < b.Count && hit < 0; i++) {
        var y = b[i];
        if (!used[i] && y.Kind == x.Kind && Math.Abs(x.X - y.X) <= Tol && Math.Abs(x.Y - y.Y) <= Tol && Math.Abs(x.W - y.W) <= Tol && Math.Abs(x.H - y.H) <= Tol) hit = i;
      }
      if (hit < 0) { why = "rects: recorded " + x.Kind + " " + x.X + "," + x.Y + " " + x.W + "x" + x.H + " has no replayed match"; return false; }
      used[hit] = true;
    }
    return true;
  }

  static int Main(string[] args) {
    if (args.Length >= 3 && args[0] == "--make-fixture") return Fixture.Make(args[1], args[2]);
    if (args.Length < 2) { Console.WriteLine("usage: RecReplay.dll <recording dir with part_*.zip> <out dir>"); return 2; }
    string dir = args[0], outDir = args[1];
    var parts = new List<string>();
    if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "part_*.zip")) if (RecNames.PartOf(Path.GetFileName(f)) >= 0) parts.Add(f);
    parts.Sort(StringComparer.Ordinal);
    if (parts.Count == 0) { Console.WriteLine("no part_*.zip in " + dir); return 2; }
    Directory.CreateDirectory(outDir);

    // ---- read every part: the frames' names with their lines, the data files
    var frames = new List<Frame>(); string layoutText = null, geoText = null; var partNotes = new List<string>();
    foreach (var pf in parts) {
      try {
        using (var z = ZipFile.OpenRead(pf)) {
          var names = new List<string>();
          foreach (var e in z.Entries) if (e.FullName.StartsWith("frame_") && e.FullName.EndsWith(".jpg")) names.Add(e.FullName);
          names.Sort(StringComparer.Ordinal);
          var je = z.GetEntry("frames.jsonl");
          if (je == null) { partNotes.Add(Path.GetFileName(pf) + ": no frames.jsonl"); continue; }
          var lines = ReadText(je).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
          if (lines.Length != names.Count) partNotes.Add(Path.GetFileName(pf) + ": " + names.Count + " pictures, " + lines.Length + " lines");
          for (int i = 0; i < lines.Length && i < names.Count; i++) {
            var j = O(MiniJson.TryParse(lines[i]));
            if (j != null) frames.Add(new Frame { Name = names[i].Substring(0, names[i].Length - 4), J = j });
          }
          var le = z.GetEntry("layout.json"); if (le != null && layoutText == null) layoutText = ReadText(le);
          var ge = z.GetEntry("geometry.json"); if (ge != null && geoText == null) geoText = ReadText(ge);
        }
      } catch (Exception e) { partNotes.Add(Path.GetFileName(pf) + ": " + e.Message); }
    }
    GameGeometry geo = null;
    if (geoText != null) { var gj = O(MiniJson.TryParse(geoText)); string gv = Str(gj, "game"); if (gv != null) geo = GameGeometry.Parse(geoText, gv); }

    // ---- replay
    var tracker = new ScreenTracker();
    var readSlots = new Dictionary<string, object>();
    var tickSlots = new Dictionary<string, object>();   // ctx, env, sinv, sinvList
    HeroScreen lastHero = null; string listSig = ""; long listChangeAt = 0;
    int nScreen = 0, nStep = 0, nRect = 0, nReplayedStep = 0, nReplayedRect = 0, nNoInputs = 0, nAny = 0;
    var mismatches = new List<string>(); var timeline = new List<string>();
    string tlKey = null; string tlFrom = "", tlTo = ""; int tlN = 0;
    string envNote = "";
    foreach (var fr in frames) {
      var j = fr.J;
      long t = (long)Num(j, "t", 0); int w = (int)Num(j, "w", 0), h = (int)Num(j, "h", 0);
      string screenRec = Str(j, "screen") ?? "-", stepRec = Str(j, "step") ?? "-";
      var recRects = RectsOf(Get(j, "frames"));
      var inn = O(Get(j, "in"));
      string screenRep = "-", stepRep = "-", mode = "-"; List<RecFrameRect> repRects = null;
      var why = new List<string>();
      if (inn == null) { nNoInputs++; screenRep = null; stepRep = null; }
      else {
        // the reads since the last line: the tracker is given each one at its own clock
        var reads = Get(inn, "reads") as List<object>;
        if (reads != null) foreach (var ro in reads) {
          var r = O(ro); if (r == null) continue;
          foreach (var kv in r) if (kv.Key.StartsWith("r.")) readSlots[kv.Key] = kv.Value;
          if (Get(r, "base") == null) tracker.Update(RecIn.ParseInputs(readSlots), (long)Num(r, "at", 0));
        }
        foreach (var k in new[] { "ctx", "env", "sinv", "sinvList" }) { object v; if (inn.TryGetValue(k, out v)) tickSlots[k] = v; }
        if (tracker.Hero != null) lastHero = tracker.Hero;
        screenRep = tracker.Current != null ? tracker.Current.Brief : "-";
        var ctx = RecCtx.Parse(Get(tickSlots, "ctx"));
        var env = O(Get(tickSlots, "env"));
        double U = env != null && Get(env, "U") is double ? Num(env, "U", 0) : Ui.Unit(w, h);
        if (envNote == "" && env != null) {
          string gh = Str(env, "gh") ?? "";
          envNote = "game " + (Str(env, "ver") ?? "?") + ", app " + (Str(env, "app") ?? "?") + ", layout " + (Str(env, "lh") ?? "") + ", geometry " + gh
            + (geoText != null && gh != "" && RecIn.Hash(geoText) != gh ? " (geometry.json has another hash!)" : "");
        }
        mode = ctx != null ? ctx.Mode : "";
        long tn = (long)Num(inn, "tn", 0);
        if (ctx != null && mode == "locate" && ctx.Req != null) {
          var req = ctx.Req;
          var step = LocateGuide.Next(req, tracker.Inv, tracker.Rs, lastHero, ctx.Cols, ctx.OnAccount);
          var rs = tracker.Rs;
          if (rs != null && rs.OnAscend) {   // the overlay waits for the Ascension list to settle after it changed
            string sig = rs.AscendEquips.Length + "|" + string.Join(",", Array.ConvertAll(rs.AscendParts, x => x.ToString())) + "|" + (rs.AscendEquips.Length > 0 ? rs.AscendEquips[0].ToString() : "");
            if (sig != listSig) { listSig = sig; listChangeAt = tn; }
            if ((step.Kind == LocateKind.AscendFilter || step.Kind == LocateKind.AscendNotInList) && tn - listChangeAt < 1200) step.Kind = LocateKind.AscendLoading;
          }
          stepRep = step.Kind.ToString();
          var fd = O(Get(inn, "fd"));
          if (fd != null && rs != null && (step.Kind == LocateKind.AscendPick || step.Kind == LocateKind.RecastPick)) {   // the Foundry list as drawn (eased)
            var l = RecIn.ParseList(Get(fd, "l"));
            if (Get(fd, "asc") is bool && (bool)Get(fd, "asc")) rs.AscendList = l; else rs.RecastList = l;
          }
          double pr = Get(inn, "pr") is double ? Num(inn, "pr", double.NaN) : double.NaN;
          repRects = RecScenes.Locate(ctx, step, req.Item, rs, tracker.Inv, geo, pr, w, h, U);
        } else if (ctx != null && mode == "sell") {
          var inv = RecIn.ParseInv(Get(tickSlots, "sinv"), Get(tickSlots, "sinvList"));
          double sr = Get(inn, "sr") is double ? Num(inn, "sr", double.NaN) : double.NaN;
          SellHintStep ss;
          repRects = RecScenes.Sell(ctx, inv, geo, sr, w, h, U, out ss);
          stepRep = "Sell:" + ss.Kind;
        }
      }
      // ---- compare
      if (screenRep != null) { nAny++; if (screenRep != screenRec) { nScreen++; why.Add("screen: recorded '" + screenRec + "', replayed '" + screenRep + "'"); } }
      if (stepRep != null && (mode == "locate" || mode == "sell")) { nReplayedStep++; if (stepRep != stepRec) { nStep++; why.Add("step: recorded '" + stepRec + "', replayed '" + stepRep + "'"); } }
      if (repRects != null) {
        nReplayedRect++; string rw;
        if (!SameRects(recRects, repRects, out rw)) { nRect++; why.Add(rw); }
      }
      foreach (var m in why) mismatches.Add(fr.Name + " t=" + t + " " + m);
      // ---- the timeline: a run of frames with the same screen and step
      string key = screenRep == null ? "(no inputs)" : screenRep + " | " + (stepRep ?? "-");
      if (key != tlKey) {
        if (tlKey != null) timeline.Add(tlFrom + " .. " + tlTo + "  x" + tlN + "  " + tlKey);
        tlKey = key; tlFrom = fr.Name + " t=" + t; tlN = 0;
      }
      tlTo = fr.Name + " t=" + t; tlN++;
      // ---- the frame's file
      var sb = new StringBuilder("{\"frame\":").Append(MiniJson.Quote(fr.Name)).Append(",\"t\":").Append(t).Append(",\"w\":").Append(w).Append(",\"h\":").Append(h)
        .Append(",\"scale\":").Append(RecIn.D(Num(j, "scale", 1))).Append(",\"mode\":").Append(MiniJson.Quote(mode))
        .Append(",\"screenRecorded\":").Append(MiniJson.Quote(screenRec)).Append(",\"screenReplayed\":").Append(screenRep == null ? "null" : MiniJson.Quote(screenRep))
        .Append(",\"stepRecorded\":").Append(MiniJson.Quote(stepRec)).Append(",\"stepReplayed\":").Append(stepRep == null ? "null" : MiniJson.Quote(stepRep))
        .Append(",\"rectsRecorded\":").Append(RectsJson(recRects)).Append(",\"rectsReplayed\":").Append(RectsJson(repRects)).Append(",\"mismatch\":[");
      for (int i = 0; i < why.Count; i++) { if (i > 0) sb.Append(','); sb.Append(MiniJson.Quote(why[i])); }
      sb.Append("]}");
      File.WriteAllText(Path.Combine(outDir, fr.Name + ".json"), sb.ToString(), new UTF8Encoding(false));
    }
    if (tlKey != null) timeline.Add(tlFrom + " .. " + tlTo + "  x" + tlN + "  " + tlKey);

    // ---- summary.txt
    var s = new StringBuilder();
    s.AppendLine("recording: " + dir + " (" + parts.Count + " parts, " + frames.Count + " frames)");
    foreach (var n in partNotes) s.AppendLine("note: " + n);
    s.AppendLine(envNote != "" ? envNote : "no environment recorded (the recording has no inputs)");
    s.AppendLine("layout.json: " + (layoutText != null ? "yes" : "no") + ", geometry.json: " + (geoText != null ? (geo != null ? "yes" : "yes, but it does not load") : "no (rectangles of the Foundry steps cannot be replayed)"));
    s.AppendLine("frames without inputs: " + nNoInputs);
    s.AppendLine();
    s.AppendLine("steps timeline (screen | step):");
    foreach (var l in timeline) s.AppendLine("  " + l);
    s.AppendLine();
    s.AppendLine("mismatches recorded vs replayed:");
    int shown = 0;
    foreach (var m in mismatches) { if (shown++ >= 200) { s.AppendLine("  ... " + (mismatches.Count - 200) + " more"); break; } s.AppendLine("  " + m); }
    if (mismatches.Count == 0) s.AppendLine("  none");
    s.AppendLine();
    s.AppendLine("screens checked: " + nAny + ", mismatches " + nScreen);
    s.AppendLine("steps checked: " + nReplayedStep + ", mismatches " + nStep);
    s.AppendLine("rectangle sets checked: " + nReplayedRect + ", mismatches " + nRect);
    s.AppendLine("mismatches: " + mismatches.Count);
    File.WriteAllText(Path.Combine(outDir, "summary.txt"), s.ToString(), new UTF8Encoding(false));
    Console.Write(s.ToString());
    return 0;
  }
}

/// <summary>A tiny synthetic recording made with the recorder's own writers: 3 frames of the locate guide (the third has a wrong recorded step
/// on purpose, so a mismatch has to be found).</summary>
static class Fixture {
  // a 64x36 jpeg (the picture the lines are drawn over by tests/rec-draw.mjs)
  const string Jpg = "/9j/2wBDAA0JCgsKCA0LCgsODg0PEyAVExISEyccHhcgLikxMC4pLSwzOko+MzZGNywtQFdBRkxOUlNSMj5aYVpQYEpRUk//2wBDAQ4ODhMREyYVFSZPNS01T09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0//wAARCAAkAEADASIAAhEBAxEB/8QAGAABAQEBAQAAAAAAAAAAAAAAAgMABAb/xAAWEAEBAQAAAAAAAAAAAAAAAAABAAL/xAAYAQEBAQEBAAAAAAAAAAAAAAAEAgMGAP/EABYRAQEBAAAAAAAAAAAAAAAAAAIAAf/aAAwDAQACEQMRAD8A8yYmYqmJmLXXHDpGJGKxiZijXJDomJmKxiZijXJDomJmKxiZijXJDuMxMxWMTMXtdzAdExMxWMSMUa5IdIxMxVMTMUa5QdIxMxVMTMWeuSHcQEwLWtNucEwJgWtZ7JEwJgWtRskTAmBa1GyRf//Z";

  public static int Make(string dir, string geometryPath) {
    const int W = 1920, H = 1080;
    double U = Ui.Unit(W, H);
    string geoText = File.ReadAllText(geometryPath, new UTF8Encoding(false));
    var geoJ = MiniJson.AsObject(MiniJson.TryParse(geoText));
    string ver = (string)geoJ["game"];
    var geo = GameGeometry.Parse(geoText, ver);
    if (geo == null) { Console.WriteLine("the geometry does not load"); return 1; }
    Directory.CreateDirectory(dir);
    var item = new PlanItem { Uid = 113, Slot = 0, Quality = 6 };
    var req = new LocateRequest { Id = "r", Purpose = "recast", Item = item };
    var uids = new List<long>(); for (int i = 0; i < 40; i++) uids.Add(101 + i);
    var w = new RecPartWriter(dir, 0);
    w.AddText("geometry.json", geoText);
    var enc = new RecLineEncoder();
    var tracker = new ScreenTracker();
    string env = "{\"U\":" + RecIn.D(U) + ",\"ver\":" + MiniJson.Quote(ver) + ",\"lh\":\"\",\"gh\":\"" + RecIn.Hash(geoText) + "\",\"app\":\"test\"}";
    var sg = SellGeometry.Inventory(W, U, 8);
    double[] scrolls = { 0, 1500, 0 };
    RecReads.Clear();
    for (int n = 0; n < 3; n++) {
      long at = 1000 + n * 125;
      var inv = new InventoryScreen { Open = true, EquipTab = true, BulkKnown = true, Bulk = n == 2, ListPtr = 77 };
      inv.Uids.AddRange(uids);
      inv.List = new RFX.ListRead { ScrollY = scrolls[n], Cols = 8, RowUnits = 118, First = 0, Last = 15 };
      var inp = new ScreenInputs { Inv = inv };
      RecReads.Add(at, inp);
      tracker.Update(inp, at);
      var step = LocateGuide.Next(req, tracker.Inv, tracker.Rs, tracker.Hero, 8, 1);
      double row1 = new InvScroll().Row1(sg, inv.List, double.NaN, U);
      var ctx = new RecCtx { Mode = "locate", Front = true, Cols = 8, OnAccount = 1, Req = req, Lines = true, CityInvSeen = true, InvDX = 0.5, BtnDY = 0.1 };
      var info = new RecInfo { Screen = tracker.Current.Brief, Step = step.Kind.ToString(), Ctx = ctx.ToJson(), TickNow = at, ReadSeq = RecReads.Seq, PlaceRow1 = row1, InvSmooth = row1 };
      info.Frames.AddRange(RecScenes.Locate(ctx, step, item, tracker.Rs, tracker.Inv, geo, row1, W, H, U));
      if (n == 2) info.Step = "InList";   // recorded wrong on purpose
      string line = RecJson.Line(n * 125, W, H, 64.0 / W, false, info, 10, 20, enc.EncodeInfo(info, env));
      w.Add(RecNames.FrameFile(n + 1), Convert.FromBase64String(Jpg), line);
    }
    Console.WriteLine("fixture: " + w.Close());
    return 0;
  }
}
