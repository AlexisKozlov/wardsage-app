// Wardsage app - the TEST build's screen recorder: the game window's client area, with our overlay windows on it, as jpeg frames in
// 10 s zip parts, sent to the developers (src/ScreenRecorder.cs). Only the screen is looked at; nothing is written into the game.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;

namespace RealmForge {
  sealed class ScreenRecorder {
    public const int MaxMs = 5 * 60 * 1000, PartMs = 10000, PartMaxBytes = 7 * 1024 * 1024, FgEveryMs = 125, BgEveryMs = 2000;
    public const long MaxTotalBytes = 400L * 1024 * 1024;

    readonly Func<string> site, code;
    readonly Action<string> log;
    readonly Action<string> post;       // the state event for the interface (any thread)
    readonly Action<bool> setActive;    // the overlay: our windows visible to the capture, collecting on/off (it runs this on the UI thread)
    Thread th; volatile bool stopReq; volatile bool on;
    string stopWhy = "";
    // an automatic recording (src/AutoRec.cs): its reason (written as auto.json into the first part) and its length; null = a manual one
    string autoReason; long limitMs = MaxMs; string recId;
    // a lab recording (src/LabRec.cs): lab.json for its first part; tags added while it runs (written into the part being filled)
    string labJson;
    readonly object tagGate = new object();
    readonly System.Collections.Generic.List<string> tags = new System.Collections.Generic.List<string>();

    public ScreenRecorder(Func<string> site, Func<string> code, Action<string> log, Action<string> post, Action<bool> setActive) {
      this.site = site; this.code = code; this.log = log; this.post = post; this.setActive = setActive;
    }

    public bool On { get { return on; } }
    /// <summary>The running recording's id, "" when none runs.</summary>
    public string Id { get { return on ? (recId ?? "") : ""; } }

    public void Start() { StartAs(null, MaxMs, null); }

    /// <summary>A lab recording (src/LabRec.cs) of at most LabRecCmd.CapMs, tagged with the scenario and the run; its id ("" = one is running already).</summary>
    public string StartLab(string scenario, string run) { return on ? "" : StartAs(null, LabRecCmd.CapMs, LabRecCmd.LabJson(scenario, run, Program.Version)); }

    /// <summary>Adds a tag (an automatic recording's reason) to the running recording.</summary>
    public void Tag(string tag) { if (!on) return; lock (tagGate) if (tags.Count < 50) tags.Add(tag ?? ""); }

    /// <summary>An automatic recording of the next <paramref name="ms"/> with its reason; the recording's id ("" = one is running already).</summary>
    public string StartAuto(string reason, int ms) { return on ? "" : StartAs(reason ?? "", Math.Min(ms, MaxMs), null); }

    string StartAs(string reason, long ms, string lab) {
      if (on) return "";
      autoReason = reason; limitMs = ms; labJson = lab;
      lock (tagGate) tags.Clear();
      recId = lab != null ? LabRecCmd.NewId(DateTime.Now, new Random()) : RecNames.NewId(DateTime.Now, new Random());
      on = true; stopReq = false; stopWhy = "";
      setActive(true);
      th = new Thread(Run) { IsBackground = true, Name = "rec-capture" };
      th.Start();
      return recId;
    }

    public void Stop() { stopReq = true; }

    IntPtr hwnd; long hwndAt = -100000;
    IntPtr GameHwnd(long nowMs) {
      if (hwnd != IntPtr.Zero && nowMs - hwndAt < 2000 && W32.IsWindowVisible(hwnd)) return hwnd;
      hwndAt = nowMs; hwnd = IntPtr.Zero;
      try {
        int pid = GameInfo.FindGameProcess();
        if (pid != 0) using (var p = Process.GetProcessById(pid)) if (!p.HasExited) hwnd = p.MainWindowHandle;
      } catch (Exception) { }
      return hwnd;
    }

    // the raw inputs next to every frame (src/RecInputs.cs): a slot is written when it changed, every part starts with all of them
    readonly RecLineEncoder enc = new RecLineEncoder();
    GameLayout lyRef; GameGeometry geoRef; string lyHash = "", geoHash = "", env = "{}"; bool lyWritten, geoWritten; int envW, envH;

    // the layout / geometry data in use are kept once per recording (layout.json, geometry.json in the first part), the lines only name them by hash
    string Env(RecPartWriter w, int cw, int ch) {
      var ly = Layout.Current; var gg = Geo.Current;
      bool changed = false;
      // the text may come later than the object (retried each call until it is there), and is written into the first part that has it
      if (ly != lyRef || (ly != null && !lyWritten)) { string txt = ly != null ? Layout.RawCache(ly.Game) : null; if (ly != lyRef || txt != null) { lyRef = ly; lyHash = RecIn.Hash(txt); changed = true; if (txt != null && !lyWritten) { w.AddText("layout.json", txt); lyWritten = true; } } }
      if (gg != geoRef || (gg != null && !geoWritten)) { string txt = gg != null ? Geo.RawCache(gg.Game) : null; if (gg != geoRef || txt != null) { geoRef = gg; geoHash = RecIn.Hash(txt); changed = true; if (txt != null && !geoWritten) { w.AddText("geometry.json", txt); geoWritten = true; } } }
      if (changed || cw != envW || ch != envH) {
        envW = cw; envH = ch;
        env = "{\"U\":" + RecIn.D(Ui.Unit(cw, ch)) + ",\"ver\":" + MiniJson.Quote(ly != null ? ly.Game : gg != null ? gg.Game : "") + ",\"lh\":\"" + lyHash + "\",\"gh\":\"" + geoHash + "\",\"app\":" + MiniJson.Quote(Program.Version) + "}";
      }
      return env;
    }

    static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    void Run() {
      string rec = recId, why = autoReason, lab = labJson; long limit = limitMs; bool reasonWritten = false; int tagNo = 0;
      string dir = Path.Combine(AppConfig.DefaultDir, "recordings", rec);
      var up = new RecUploader(rec, site, code, log);
      var sw = Stopwatch.StartNew();
      int part = 0, frameNo = 0, parts = 0, lastSec = -1;
      long closedBytes = 0, partStart = 0, nextFg = 0, nextBg = 0;
      RecPartWriter w = null;
      log("recording " + rec + ": started");
      try {
        while (!stopReq) {
          long t = sw.ElapsedMilliseconds;
          if (t >= limit) { stopWhy = why != null ? "" : "time"; break; }
          if (closedBytes + (w != null ? w.Bytes : 0) > MaxTotalBytes) { stopWhy = "size"; break; }
          int sec = (int)(t / 1000);
          if (sec != lastSec) { lastSec = sec; post(RecJson.State(true, sec, up.Sent, up.Total, "")); }
          IntPtr h = GameHwnd(t);
          if (h != IntPtr.Zero && W32.IsWindowVisible(h) && !W32.IsIconic(h)) {
            bool fg = OverlayController.IsForeground(h);
            if (fg ? t >= nextFg : t >= nextBg) {
              if (fg) nextFg = t - nextFg < 2 * FgEveryMs ? nextFg + FgEveryMs : t + FgEveryMs; else nextBg = t + BgEveryMs;
              if (w == null) {
                w = new RecPartWriter(dir, part); partStart = t; enc.Rebase();
                if (why != null && !reasonWritten) { w.AddText("auto.json", "{\"auto\":true,\"reason\":" + MiniJson.Quote(why) + ",\"app\":" + MiniJson.Quote(Program.Version) + "}"); reasonWritten = true; }
                if (lab != null && !reasonWritten) { w.AddText("lab.json", lab); reasonWritten = true; }
              }
              string[] tg = null;
              lock (tagGate) if (tags.Count > 0) { tg = tags.ToArray(); tags.Clear(); }
              if (tg != null) foreach (var x in tg) { w.AddText("tag_" + (++tagNo).ToString("000", System.Globalization.CultureInfo.InvariantCulture) + ".json", LabRecCmd.TagJson(x, t)); log("recording " + rec + ": tagged " + x); }
              long c0 = LoadMeter.Now();
              try { Capture(h, t, !fg, w, ++frameNo); } catch (Exception e) { log("recording " + rec + ": frame failed: " + e.Message); }
              LoadMeter.Done(LoadMeter.RecFrame, c0);
            }
          }
          if (w != null && w.Frames > 0 && (t - partStart >= PartMs || w.Bytes >= PartMaxBytes)) {
            closedBytes += w.Bytes; Finish(ref w, ref part, ref parts, up);
            if (part > RecNames.MaxPart) { stopWhy = "parts"; break; }
          }
          Thread.Sleep(10);
        }
      } catch (Exception e) { log("recording " + rec + ": " + e); }
      if (w != null) Finish(ref w, ref part, ref parts, up);
      up.Close();
      log("recording " + rec + ": stopped, " + parts + " parts" + (stopWhy != "" ? " (" + stopWhy + " limit)" : ""));
      on = false;
      setActive(false);
      RecInfo.Clear();
      post(RecJson.State(false, (int)(sw.ElapsedMilliseconds / 1000), up.Sent, up.Total, stopWhy));
      // the state keeps coming while the parts are still on their way
      try {
        var wait = Stopwatch.StartNew(); int lastSent = -1;
        while (!up.Idle && !up.Off && wait.ElapsedMilliseconds < 20 * 60 * 1000 && !on) {
          if (up.Sent != lastSent) { lastSent = up.Sent; post(RecJson.State(false, (int)(sw.ElapsedMilliseconds / 1000), up.Sent, up.Total, stopWhy)); }
          Thread.Sleep(500);
        }
        if (!on) post(RecJson.State(false, (int)(sw.ElapsedMilliseconds / 1000), up.Sent, up.Total, up.Off ? "off" : stopWhy));
        if (why != null) log("rec: auto " + why + " " + (up.Idle && !up.Off && up.Sent > 0 ? "sent " + rec + " (" + up.Sent + " parts)" : "not sent " + rec + " (" + up.Sent + "/" + up.Total + " parts" + (up.Off ? ", the site takes no recordings" : "") + ")"));
      } catch (Exception) { }
    }

    void Finish(ref RecPartWriter w, ref int part, ref int parts, RecUploader up) {
      string path = w.Close(); w = null;
      if (path != null) { up.Add(part, path); parts++; part++; }
    }

    void Capture(IntPtr h, long t, bool bg, RecPartWriter w, int no) {
      W32.RECT cr; if (!W32.GetClientRect(h, out cr) || cr.R < 64 || cr.B < 64) return;
      var o = new W32.POINT(0, 0); W32.ClientToScreen(h, ref o);
      W32.POINT c; int cx = 0, cy = 0;
      if (W32.GetCursorPos(out c)) { cx = c.X - o.X; cy = c.Y - o.Y; }
      int nw, nh; double scale = RecSize.Fit(cr.R, cr.B, RecSize.MaxWidth, out nw, out nh);
      byte[] jpg;
      using (var full = new Bitmap(cr.R, cr.B, PixelFormat.Format24bppRgb)) {
        // the screen's pixels, so our overlay windows are in the picture (they are not hidden from captures while recording)
        using (var g = Graphics.FromImage(full)) g.CopyFromScreen(o.X, o.Y, 0, 0, new Size(cr.R, cr.B), CopyPixelOperation.SourceCopy);
        Bitmap small = null;
        try {
          var src = full;
          if (nw != cr.R || nh != cr.B) {
            small = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(full, new Rectangle(0, 0, nw, nh), 0, 0, cr.R, cr.B, GraphicsUnit.Pixel); }
            src = small;
          }
          using (var ms = new MemoryStream())
          using (var ep = new EncoderParameters(1)) {
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
            src.Save(ms, Jpeg, ep);
            jpg = ms.ToArray();
          }
        } finally { if (small != null) small.Dispose(); }
      }
      var info = RecInfo.Take();
      if (info != null && RecInfo.NowMs() - info.AtMs > 1500) info = null;   // the overlay's tick is not running: nothing known
      string inputs = null;
      try { if (info != null && info.Ctx != null) inputs = enc.EncodeInfo(info, Env(w, cr.R, cr.B)); } catch (Exception) { }
      w.Add(RecNames.FrameFile(no), jpg, RecJson.Line(t, cr.R, cr.B, scale, bg, info, cx, cy, inputs));
    }
  }
}
