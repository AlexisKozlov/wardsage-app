// RealmForge extractor - the window.
//
// All slow work (memory reading, network) runs on a BackgroundWorker; the window only renders
// progress and results. Texts come from Strings (RU/EN), colors follow the RealmForge site.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RealmForge {
  static class Theme {
    public static readonly Color Bg = ColorTranslator.FromHtml("#0b101b");
    public static readonly Color Field = ColorTranslator.FromHtml("#141b2c");
    public static readonly Color Track = ColorTranslator.FromHtml("#1c2436");
    public static readonly Color Gold = ColorTranslator.FromHtml("#e8cf8e");
    public static readonly Color GoldDeep = ColorTranslator.FromHtml("#c9a45c");
    public static readonly Color GoldDim = ColorTranslator.FromHtml("#4a4130");
    public static readonly Color Text = ColorTranslator.FromHtml("#ece6d6");
    public static readonly Color Muted = ColorTranslator.FromHtml("#9aa1b1");
    public static readonly Color Error = ColorTranslator.FromHtml("#e8907c");
    public static readonly Color Ok = ColorTranslator.FromHtml("#9fd39a");
  }

  // Thin gold progress bar (the system ProgressBar ignores colors when visual styles are on).
  sealed class GoldBar : Control {
    int value;
    bool failed;
    public GoldBar() {
      SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
      TabStop = false;
    }
    public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
    public bool Failed { get { return failed; } set { failed = value; Invalidate(); } }
    protected override void OnPaint(PaintEventArgs e) {
      using (var b = new SolidBrush(Theme.Track)) e.Graphics.FillRectangle(b, ClientRectangle);
      int w = Width * value / 100;
      if (w <= 0) return;
      var r = new Rectangle(0, 0, w, Height);
      if (failed) { using (var b = new SolidBrush(Theme.Error)) e.Graphics.FillRectangle(b, r); return; }
      using (var b = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Height), Theme.GoldDeep, Theme.Gold, LinearGradientMode.Horizontal))
        e.Graphics.FillRectangle(b, r);
    }
  }

  enum Stage { Find = 0, Read = 1, Send = 2, Done = 3 }
  enum StepState { Pending, Active, Done, Failed }

  // What the worker was asked to do.
  sealed class Job {
    public bool Upload;        // false = "only save the file"
    public bool SaveCopy;
    public string Site, Code;
  }

  // What the worker produced.
  sealed class JobResult {
    public ExtractResult Extract;
    public SyncResult Sync;          // null when nothing was sent
    public string SavedPath;         // account.json copy, or null
    public string SaveError;         // exception text when saving failed
    public string InternalError;     // unexpected exception in the worker
  }

  // Progress message from the worker to the window.
  sealed class StageMsg {
    public Stage Stage; public string GameVersion;
    public StageMsg(Stage s, string v) { Stage = s; GameVersion = v; }
  }

  sealed class MainForm : Form {
    const int W = 472;                      // content width at 96 DPI

    readonly AppConfig config;
    readonly string scriptPath;             // for "restart as administrator"; null when unknown
    readonly BackgroundWorker worker = new BackgroundWorker();
    readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();

    // controls
    Label title, subtitle, langRu, langEn, codeLabel, codeCheck, codeHint, advToggle, siteLabel, siteError, status, footer;
    TextBox codeBox, siteBox;
    CheckBox showCode, saveCopy;
    Button primary, siteReset, openSite, openFolder, restartAdmin, showLog, equipBtn;
    HelperForm helper;
    Panel advPanel;
    Label[] steps = new Label[4];
    StepState[] stepStates = new StepState[4];
    GoldBar bar;

    // state
    bool busy, advOpen, uploadMode, fixingCode;
    bool idleStatus = true;                 // nothing has run yet: the status line shows the hint
    DateTime readStarted;
    Stage stage;
    string gameVersion, viewUrl, savedPath;
    Func<string> statusText;                // re-rendered when the language changes
    Color statusColor = Theme.Muted;

    public MainForm(AppConfig cfg, string scriptPath) {
      config = cfg;
      this.scriptPath = scriptPath;
      Strings.Lang = cfg.Lang;
      Build();
      siteBox.Text = string.IsNullOrEmpty(cfg.Site) ? SyncClient.DefaultSite : cfg.Site;
      saveCopy.Checked = cfg.SaveCopy;
      codeBox.Text = cfg.Code ?? "";
      advOpen = siteBox.Text != SyncClient.DefaultSite;   // show a non-default address right away
      worker.WorkerReportsProgress = true;
      worker.DoWork += DoWork;
      worker.ProgressChanged += OnProgress;
      worker.RunWorkerCompleted += OnCompleted;
      clock.Interval = 1000;
      clock.Tick += delegate { if (busy && stage == Stage.Read) RenderSteps(); };
      ResetProgress();
      ApplyTexts();
    }

    // ------------------------------------------------------------------ layout

    void Build() {
      SuspendLayout();
      AutoScaleDimensions = new SizeF(96F, 96F);
      AutoScaleMode = AutoScaleMode.Dpi;
      Text = "RealmForge";
      BackColor = Theme.Bg;
      ForeColor = Theme.Text;
      Font = new Font("Segoe UI", 9.75F);
      FormBorderStyle = FormBorderStyle.FixedSingle;
      MaximizeBox = false;
      StartPosition = FormStartPosition.CenterScreen;
      AutoSize = true;
      AutoSizeMode = AutoSizeMode.GrowAndShrink;
      try { Icon = MakeIcon(); } catch (Exception) { }

      var root = new FlowLayoutPanel();
      root.FlowDirection = FlowDirection.TopDown;
      root.WrapContents = false;
      root.AutoSize = true;
      root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
      root.Padding = new Padding(24, 18, 24, 16);
      root.BackColor = Theme.Bg;
      Controls.Add(root);

      // header: REALMFORGE ............ RU · EN
      var header = new Panel();
      header.Size = new Size(W, 40);
      header.Margin = new Padding(0);
      title = new Label();
      title.Text = "REALMFORGE";
      title.Font = new Font("Georgia", 20F, FontStyle.Bold);
      title.ForeColor = Theme.Gold;
      title.AutoSize = true;
      title.Location = new Point(-3, 0);
      header.Controls.Add(title);
      langRu = LangLink("RU", "ru");
      langEn = LangLink("EN", "en");
      var dot = new Label();
      dot.Text = "·"; dot.AutoSize = true; dot.ForeColor = Theme.Muted;
      langEn.Location = new Point(W - 26, 10);
      dot.Location = new Point(W - 38, 10);
      langRu.Location = new Point(W - 64, 10);
      header.Controls.Add(langRu); header.Controls.Add(dot); header.Controls.Add(langEn);
      root.Controls.Add(header);

      subtitle = NewLabel(Theme.Muted, 9F);
      subtitle.Margin = new Padding(0, 0, 0, 8);
      root.Controls.Add(subtitle);
      root.Controls.Add(Rule());

      // sync code
      codeLabel = NewLabel(Theme.Text, 9.75F);
      codeLabel.Font = new Font("Segoe UI", 9.75F, FontStyle.Bold);
      codeLabel.Margin = new Padding(0, 14, 0, 4);
      root.Controls.Add(codeLabel);

      var codeRow = new Panel();
      codeRow.Size = new Size(W, 28);
      codeRow.Margin = new Padding(0);
      codeBox = NewTextBox(W - 100);
      codeBox.Font = new Font("Consolas", 11F);
      codeBox.MaxLength = 200;           // long pastes are cut down to the code, see OnCodeChanged
      codeBox.UseSystemPasswordChar = true;
      codeBox.TextChanged += OnCodeChanged;
      codeRow.Controls.Add(codeBox);
      showCode = NewCheck();
      showCode.Location = new Point(W - 92, 4);
      showCode.CheckedChanged += delegate { codeBox.UseSystemPasswordChar = !showCode.Checked; };
      codeRow.Controls.Add(showCode);
      root.Controls.Add(codeRow);

      codeCheck = NewLabel(Theme.Muted, 8.75F);
      codeCheck.Margin = new Padding(0, 4, 0, 0);
      root.Controls.Add(codeCheck);
      codeHint = NewLabel(Theme.Muted, 8.75F);
      codeHint.Margin = new Padding(0, 2, 0, 8);
      root.Controls.Add(codeHint);

      saveCopy = NewCheck();
      saveCopy.Margin = new Padding(0, 2, 0, 4);
      root.Controls.Add(saveCopy);

      // "Advanced" (collapsed): site address
      advToggle = NewLabel(Theme.GoldDeep, 9F);
      advToggle.Cursor = Cursors.Hand;
      advToggle.Margin = new Padding(0, 4, 0, 4);
      advToggle.Click += delegate { advOpen = !advOpen; ApplyTexts(); };
      root.Controls.Add(advToggle);

      advPanel = new Panel();
      advPanel.Size = new Size(W, 70);
      advPanel.Margin = new Padding(0, 0, 0, 4);
      siteLabel = NewLabel(Theme.Muted, 9F);
      siteLabel.Location = new Point(0, 0);
      advPanel.Controls.Add(siteLabel);
      siteBox = NewTextBox(W - 130);
      siteBox.Location = new Point(0, 20);
      siteBox.TextChanged += delegate { if (!busy) UpdateMode(); };
      advPanel.Controls.Add(siteBox);
      siteReset = NewButton(false);
      siteReset.Size = new Size(122, 25);
      siteReset.Location = new Point(W - 122, 19);
      siteReset.Click += delegate { siteBox.Text = SyncClient.DefaultSite; };
      advPanel.Controls.Add(siteReset);
      siteError = NewLabel(Theme.Error, 8.75F);
      siteError.Location = new Point(0, 50);
      advPanel.Controls.Add(siteError);
      root.Controls.Add(advPanel);

      // main button
      primary = NewButton(true);
      primary.Size = new Size(W, 42);
      primary.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
      primary.Margin = new Padding(0, 10, 0, 12);
      primary.Click += OnPrimary;
      root.Controls.Add(primary);
      AcceptButton = primary;

      // equip helper («Переодевание»): builds sent from the site, hints over the game
      equipBtn = NewButton(false);
      equipBtn.Size = new Size(W, 32);
      equipBtn.Margin = new Padding(0, 0, 0, 12);
      equipBtn.Click += OnEquip;
      root.Controls.Add(equipBtn);

      // steps + progress
      for (int i = 0; i < steps.Length; i++) {
        steps[i] = NewLabel(Theme.Muted, 9.75F);
        steps[i].Margin = new Padding(0, 1, 0, 1);
        root.Controls.Add(steps[i]);
      }
      bar = new GoldBar();
      bar.Size = new Size(W, 6);
      bar.Margin = new Padding(0, 10, 0, 8);
      root.Controls.Add(bar);

      status = NewLabel(Theme.Muted, 9.75F);
      status.Margin = new Padding(0, 0, 0, 8);
      root.Controls.Add(status);

      // result actions
      var actions = new FlowLayoutPanel();
      actions.FlowDirection = FlowDirection.LeftToRight;
      actions.AutoSize = true;
      actions.MaximumSize = new Size(W, 0);
      actions.Margin = new Padding(0, 0, 0, 6);
      openSite = NewButton(true);
      openSite.Click += delegate { OpenUrl(viewUrl); };
      openFolder = NewButton(false);
      openFolder.Click += delegate { OpenFolder(savedPath); };
      restartAdmin = NewButton(true);
      restartAdmin.Click += OnRestartAdmin;
      showLog = NewButton(false);
      showLog.Click += delegate { OpenLog(); };
      foreach (var b in new[] { openSite, restartAdmin, openFolder, showLog }) {
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.MinimumSize = new Size(0, 32);
        b.Padding = new Padding(10, 0, 10, 0);
        b.Margin = new Padding(0, 0, 8, 0);
        b.Visible = false;
        actions.Controls.Add(b);
      }
      root.Controls.Add(actions);

      root.Controls.Add(Rule());
      footer = NewLabel(Theme.Muted, 8.25F);
      footer.Margin = new Padding(0, 6, 0, 0);
      root.Controls.Add(footer);

      ResumeLayout(false);
      PerformLayout();
    }

    Label NewLabel(Color color, float size) {
      var l = new Label();
      l.AutoSize = true;
      l.MaximumSize = new Size(W, 0);     // wrap long texts instead of widening the window
      l.ForeColor = color;
      l.Font = new Font("Segoe UI", size);
      l.Margin = new Padding(0);
      return l;
    }

    Label LangLink(string text, string lang) {
      var l = new Label();
      l.Text = text;
      l.AutoSize = true;
      l.Cursor = Cursors.Hand;
      l.Click += delegate { if (Strings.Lang != lang) { Strings.Lang = lang; config.Lang = lang; SaveConfig(); ApplyTexts(); } };
      return l;
    }

    Panel Rule() {
      var p = new Panel();
      p.Size = new Size(W, 1);
      p.BackColor = Theme.GoldDeep;
      p.Margin = new Padding(0, 4, 0, 4);
      return p;
    }

    TextBox NewTextBox(int width) {
      var t = new TextBox();
      t.Width = width;
      t.BackColor = Theme.Field;
      t.ForeColor = Theme.Text;
      t.BorderStyle = BorderStyle.FixedSingle;
      return t;
    }

    CheckBox NewCheck() {
      var c = new CheckBox();
      c.AutoSize = true;
      c.FlatStyle = FlatStyle.Flat;
      c.FlatAppearance.BorderColor = Theme.GoldDeep;
      c.FlatAppearance.CheckedBackColor = Theme.Field;
      c.ForeColor = Theme.Text;
      c.Font = new Font("Segoe UI", 9F);
      return c;
    }

    Button NewButton(bool gold) {
      var b = new Button();
      b.FlatStyle = FlatStyle.Flat;
      b.UseVisualStyleBackColor = false;
      b.Cursor = Cursors.Hand;
      b.Tag = gold;
      b.FlatAppearance.BorderColor = Theme.GoldDeep;
      b.FlatAppearance.MouseOverBackColor = gold ? Theme.Gold : Theme.Track;
      b.FlatAppearance.MouseDownBackColor = Theme.GoldDeep;
      b.EnabledChanged += delegate { PaintButton(b); };
      PaintButton(b);
      return b;
    }

    static void PaintButton(Button b) {
      bool gold = (bool)b.Tag;
      if (!b.Enabled) { b.BackColor = gold ? Theme.GoldDim : Theme.Field; b.ForeColor = Theme.Muted; return; }
      b.BackColor = gold ? Theme.GoldDeep : Theme.Bg;
      b.ForeColor = gold ? Theme.Bg : Theme.Gold;
    }

    static Icon MakeIcon() {
      using (var bmp = new Bitmap(32, 32)) {
        using (var g = Graphics.FromImage(bmp)) {
          g.SmoothingMode = SmoothingMode.AntiAlias;
          g.Clear(Theme.Bg);
          var diamond = new[] { new Point(16, 2), new Point(30, 16), new Point(16, 30), new Point(2, 16) };
          using (var b = new SolidBrush(Theme.GoldDeep)) g.FillPolygon(b, diamond);
          using (var f = new Font("Georgia", 13F, FontStyle.Bold, GraphicsUnit.Pixel))
          using (var tb = new SolidBrush(Theme.Bg)) {
            var sf = new StringFormat(); sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
            g.DrawString("R", f, tb, new RectangleF(0, 1, 32, 32), sf);
          }
        }
        return Icon.FromHandle(bmp.GetHicon());
      }
    }

    // ------------------------------------------------------------------ texts & state

    void ApplyTexts() {
      SuspendLayout();
      subtitle.Text = Strings.Get("subtitle");
      langRu.ForeColor = Strings.Lang == "ru" ? Theme.Gold : Theme.Muted;
      langEn.ForeColor = Strings.Lang == "en" ? Theme.Gold : Theme.Muted;
      langRu.Font = new Font("Segoe UI", 9.75F, Strings.Lang == "ru" ? FontStyle.Bold : FontStyle.Regular);
      langEn.Font = new Font("Segoe UI", 9.75F, Strings.Lang == "en" ? FontStyle.Bold : FontStyle.Regular);
      codeLabel.Text = Strings.Get("code_label");
      showCode.Text = Strings.Get("code_show");
      codeHint.Text = Strings.Get("code_hint");
      saveCopy.Text = Strings.Get("save_copy");
      advToggle.Text = (advOpen ? "▾ " : "▸ ") + Strings.Get("advanced");
      advPanel.Visible = advOpen;
      siteLabel.Text = Strings.Get("site_label");
      siteReset.Text = Strings.Get("site_reset");
      openSite.Text = Strings.Get("btn_open_site");
      openFolder.Text = Strings.Get("btn_open_folder");
      restartAdmin.Text = Strings.Get("btn_restart_admin");
      showLog.Text = Strings.Get("btn_log");
      equipBtn.Text = Strings.Get("btn_equip");
      footer.Text = Strings.Get("footer");
      UpdateMode();
      RenderSteps();
      RenderStatus();
      ResumeLayout(true);
    }

    // Decides between "Sync" and "Only save the file" and validates the inputs.
    void UpdateMode() {
      string code = codeBox.Text;
      bool empty = code.Length == 0;
      bool valid = SyncClient.IsValidCode(code);
      uploadMode = !empty;

      if (empty) { codeCheck.Text = ""; codeCheck.Visible = false; }
      else {
        codeCheck.Visible = true;
        codeCheck.Text = valid ? Strings.Get("code_ok") : Strings.Format("code_bad", code.Length);
        codeCheck.ForeColor = valid ? Theme.Ok : Theme.Error;
      }

      string siteErr;
      SyncClient.NormalizeSite(siteBox.Text, out siteErr);
      siteError.Text = siteErr == null ? "" : Strings.Get(siteErr == "https" ? "site_https" : "site_invalid");
      if (siteErr != null && uploadMode && !advOpen) { advOpen = true; advPanel.Visible = true; advToggle.Text = "▾ " + Strings.Get("advanced"); }

      saveCopy.Enabled = uploadMode && !busy;
      codeBox.ReadOnly = busy;
      siteBox.ReadOnly = busy;
      siteReset.Enabled = !busy;
      primary.Text = busy ? Strings.Get("btn_busy") : Strings.Get(uploadMode ? "btn_sync" : "btn_save_only");
      primary.Enabled = !busy && (!uploadMode || (valid && siteErr == null));
      equipBtn.Enabled = !busy && valid && siteErr == null;
      if (idleStatus) SetStatus(delegate { return Strings.Format("st_ready", primary.Text); }, Theme.Muted);
    }

    // Opens (or brings back) the equip helper window with the current site and code.
    void OnEquip(object sender, EventArgs e) {
      string err;
      string siteUrl = SyncClient.NormalizeSite(siteBox.Text, out err);
      if (siteUrl == null || !SyncClient.IsValidCode(codeBox.Text)) return;
      SaveConfig();
      if (helper != null && !helper.IsDisposed) { helper.Activate(); return; }
      helper = new HelperForm(siteUrl, codeBox.Text);
      helper.Show();
    }

    void OnCodeChanged(object sender, EventArgs e) {
      if (fixingCode) return;
      string fixedCode = SyncClient.ExtractCode(codeBox.Text);
      if (fixedCode != codeBox.Text) {
        fixingCode = true;
        codeBox.Text = fixedCode;
        codeBox.SelectionStart = fixedCode.Length;
        fixingCode = false;
      }
      UpdateMode();
    }

    void SetStatus(Func<string> text, Color color) {
      statusText = text;
      statusColor = color;
      RenderStatus();
    }

    void RenderStatus() {
      if (statusText == null) return;
      string s = statusText();
      if (!string.IsNullOrEmpty(gameVersion) && !busy && !idleStatus) s += "\n" + Strings.Format("game_version", gameVersion);
      status.Text = s;
      status.ForeColor = statusColor;
    }

    void ResetProgress() {
      for (int i = 0; i < stepStates.Length; i++) stepStates[i] = StepState.Pending;
      bar.Failed = false;
      bar.Value = 0;
      viewUrl = null;
      savedPath = null;
      openSite.Visible = openFolder.Visible = restartAdmin.Visible = showLog.Visible = false;
      RenderSteps();
    }

    void RenderSteps() {
      string[] keys = { "step_find", "step_read", uploadMode ? "step_send" : "step_save", "step_done" };
      for (int i = 0; i < steps.Length; i++) {
        string glyph; Color color;
        switch (stepStates[i]) {
          case StepState.Active: glyph = "▸"; color = Theme.Gold; break;
          case StepState.Done: glyph = "✓"; color = Theme.Text; break;
          case StepState.Failed: glyph = "✕"; color = Theme.Error; break;
          default: glyph = "○"; color = Theme.Muted; break;
        }
        string text = glyph + "  " + Strings.Get(keys[i]);
        if (i == (int)Stage.Read && stepStates[i] == StepState.Active)
          text += "  ·  " + Strings.Format("seconds", (int)(DateTime.Now - readStarted).TotalSeconds);
        steps[i].Text = text;
        steps[i].ForeColor = color;
      }
    }

    // ------------------------------------------------------------------ running

    void OnPrimary(object sender, EventArgs e) {
      if (busy) return;
      UpdateMode();
      if (!primary.Enabled) return;
      string siteErr;
      var job = new Job();
      job.Upload = uploadMode;
      job.SaveCopy = saveCopy.Checked;
      job.Code = codeBox.Text;
      job.Site = SyncClient.NormalizeSite(siteBox.Text, out siteErr);
      if (job.Upload && job.Site == null) return;
      SaveConfig();

      busy = true;
      idleStatus = false;
      gameVersion = null;
      ResetProgress();
      stage = Stage.Find;
      stepStates[0] = StepState.Active;
      bar.Value = 2;
      SetStatus(delegate { return Strings.Get("st_find"); }, Theme.Text);
      UpdateMode();
      RenderSteps();
      clock.Start();
      worker.RunWorkerAsync(job);
    }

    // Runs on a worker thread: no access to controls here.
    void DoWork(object sender, DoWorkEventArgs e) {
      var job = (Job)e.Argument;
      var res = new JobResult();
      e.Result = res;
      try {
        worker.ReportProgress(2, new StageMsg(Stage.Find, null));
        string version;
        int pid = Extractor.FindGame(out version);
        if (pid == 0) {
          res.Extract = new ExtractResult();
          res.Extract.Error = ExtractError.GameNotRunning;
          return;
        }
        worker.ReportProgress(5, new StageMsg(Stage.Read, version));
        var progress = new ReadProgress();
        res.Extract = Extractor.Read(version, delegate(string line) {
          try { worker.ReportProgress(5 + (int)(75 * progress.Feed(line)), line); } catch (Exception) { }
        });
        string log = RFX.Log.ToString();
        WriteRunLog(log);
        if (res.Extract.Error != ExtractError.None) return;

        if (!job.Upload || job.SaveCopy) {
          if (!job.Upload) worker.ReportProgress(85, new StageMsg(Stage.Send, version));
          try { res.SavedPath = Extractor.SaveCopy(res.Extract.Json, log); }
          catch (Exception ex) { res.SaveError = ex.Message; }
        }
        if (job.Upload) {
          worker.ReportProgress(82, new StageMsg(Stage.Send, version));
          res.Sync = SyncClient.Send(job.Site, job.Code, res.Extract.Json);
        }
      } catch (Exception ex) {
        res.InternalError = ex.GetType().Name + ": " + ex.Message;
      }
    }

    void OnProgress(object sender, ProgressChangedEventArgs e) {
      if (e.ProgressPercentage > bar.Value) bar.Value = e.ProgressPercentage;
      var msg = e.UserState as StageMsg;
      if (msg == null) return;                       // a plain log line: only the bar moves
      stage = msg.Stage;
      if (msg.GameVersion != null) gameVersion = msg.GameVersion;
      for (int i = 0; i < (int)stage; i++) stepStates[i] = StepState.Done;
      stepStates[(int)stage] = StepState.Active;
      if (stage == Stage.Read) {
        readStarted = DateTime.Now;
        SetStatus(delegate { return Strings.Get("st_read"); }, Theme.Text);
      } else if (stage == Stage.Send) {
        SetStatus(delegate { return Strings.Get(uploadMode ? "st_send" : "st_save"); }, Theme.Text);
      }
      RenderSteps();
    }

    void OnCompleted(object sender, RunWorkerCompletedEventArgs e) {
      clock.Stop();
      busy = false;
      var r = e.Error != null ? new JobResult { InternalError = e.Error.Message } : (JobResult)e.Result;
      ShowOutcome(r);
      UpdateMode();
    }

    // Renders the final state for a JobResult (success or an error message).
    internal void ShowOutcome(JobResult r) {
      busy = false;
      idleStatus = false;
      clock.Stop();
      showLog.Visible = File.Exists(RunLogPath);
      savedPath = r.SavedPath;
      openFolder.Visible = savedPath != null;
      if (r.Extract != null && r.Extract.GameVersion != null) gameVersion = r.Extract.GameVersion;

      Func<string> error = ErrorText(r);
      if (error != null) {
        int failedStep = FailedStep(r, stage);
        for (int i = 0; i < failedStep; i++) stepStates[i] = StepState.Done;
        stepStates[failedStep] = StepState.Failed;
        bar.Failed = true;
        bar.Value = Math.Max(bar.Value, 8);
        restartAdmin.Visible = r.Extract != null && r.Extract.Error == ExtractError.AccessDenied && scriptPath != null;
        SetStatus(error, Theme.Error);
        RenderSteps();
        return;
      }

      for (int i = 0; i < stepStates.Length; i++) stepStates[i] = StepState.Done;
      bar.Value = 100;
      var ex = r.Extract;
      int heroes = ex.Heroes, items = ex.Items, arts = ex.Artifacts;
      if (r.Sync != null) {                          // prefer the numbers the site actually stored
        if (r.Sync.Heroes >= 0) heroes = r.Sync.Heroes;
        if (r.Sync.Items >= 0) items = r.Sync.Items;
        if (r.Sync.Artifacts >= 0) arts = r.Sync.Artifacts;
        viewUrl = r.Sync.ViewUrl;
        if (viewUrl == null) {                       // no link in the reply: open the site itself
          string err;
          string site = SyncClient.NormalizeSite(siteBox.Text, out err);
          if (site != null) viewUrl = site + "/";
        }
        openSite.Visible = viewUrl != null;
      }
      string path = r.SavedPath, saveError = r.SaveError;
      SetStatus(delegate {
        string s = Strings.Format("done", Strings.Heroes(heroes), Strings.Items(items), Strings.Artifacts(arts));
        if (path != null) s += "\n" + Strings.Format("done_saved", path);
        if (saveError != null) s += "\n" + Strings.Format("err_save", saveError);
        return s;
      }, Theme.Ok);
      RenderSteps();
    }

    // Which of the four steps failed.
    static int FailedStep(JobResult r, Stage current) {
      if (r.InternalError != null) return (int)current;
      if (r.Extract == null || r.Extract.Error == ExtractError.GameNotRunning) return (int)Stage.Find;
      if (r.Extract.Error != ExtractError.None) return (int)Stage.Read;
      return (int)Stage.Send;                        // saving the file or sending it
    }

    // Error message for a result, or null when everything went fine.
    static Func<string> ErrorText(JobResult r) {
      if (r.InternalError != null) { string m = r.InternalError; return delegate { return Strings.Format("err_internal", m); }; }
      var ex = r.Extract;
      if (ex == null) return delegate { return Strings.Format("err_internal", "no result"); };
      switch (ex.Error) {
        case ExtractError.GameNotRunning: return delegate { return Strings.Get("err_not_running"); };
        case ExtractError.AccessDenied: return delegate { return Strings.Get("err_access"); };
        case ExtractError.OpenFailed: { string d = ex.Detail; return delegate { return Strings.Format("err_open", d); }; }
        case ExtractError.NoAccountData: return delegate { return Strings.Get("err_no_data"); };
        case ExtractError.Failed: { string d = ex.Detail ?? "?"; return delegate { return Strings.Format("err_failed", d); }; }
      }
      if (r.Sync == null) {
        if (r.SaveError != null) { string m = r.SaveError; return delegate { return Strings.Format("err_save", m); }; }
        return null;
      }
      var s = r.Sync;
      Func<string> main;
      switch (s.Status) {
        case SyncStatus.Ok: return null;
        case SyncStatus.InvalidToken: main = delegate { return Strings.Get("err_token"); }; break;
        case SyncStatus.RateLimited:
          main = delegate {
            return s.RetryAfterSeconds > 0 ? Strings.Format("err_rate", Strings.Wait(s.RetryAfterSeconds)) : Strings.Get("err_rate_nowait");
          };
          break;
        case SyncStatus.TooLarge: main = delegate { return Strings.Get("err_too_large"); }; break;
        case SyncStatus.UnsupportedMedia: main = delegate { return Strings.Get("err_media"); }; break;
        case SyncStatus.InvalidPayload: main = delegate { return Strings.Format("err_payload", s.HttpCode); }; break;
        case SyncStatus.ServerError: main = delegate { return Strings.Format("err_server", s.HttpCode); }; break;
        case SyncStatus.Redirect: main = delegate { return Strings.Format("err_redirect", s.Details ?? "?"); }; break;
        case SyncStatus.Timeout: main = delegate { return Strings.Get("err_timeout"); }; break;
        case SyncStatus.Unreachable: main = delegate { return Strings.Format("err_unreachable", s.Details ?? "?"); }; break;
        default: main = delegate { return Strings.Format("err_unexpected", s.HttpCode); }; break;
      }
      if (s.Status == SyncStatus.InvalidPayload && !string.IsNullOrEmpty(s.Details))
        return delegate { return main() + "\n" + Strings.Format("details", s.Details); };
      return main;
    }

    // ------------------------------------------------------------------ actions

    static string RunLogPath { get { return Path.Combine(AppConfig.DefaultDir, "last_run.log"); } }

    static void WriteRunLog(string log) {
      try {
        Directory.CreateDirectory(AppConfig.DefaultDir);
        File.WriteAllText(RunLogPath, log, new UTF8Encoding(false));
      } catch (Exception) { }
    }

    void SaveConfig() {
      config.Code = codeBox.Text;
      string err;
      string site = SyncClient.NormalizeSite(siteBox.Text, out err);
      config.Site = site ?? SyncClient.DefaultSite;
      config.SaveCopy = saveCopy.Checked;
      config.Lang = Strings.Lang;
      try { config.Save(); } catch (Exception) { }   // settings are a convenience, never a blocker
    }

    // Opens a web page via explorer.exe, so the browser starts as the normal user
    // even though this window runs as administrator. Only http(s) links get here.
    static void OpenUrl(string url) {
      if (url == null || !(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))) return;
      try { Process.Start("explorer.exe", "\"" + url + "\""); }
      catch (Exception) {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
      }
    }

    static void OpenFolder(string file) {
      if (file == null) return;
      try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch (Exception) { }
    }

    static void OpenLog() {
      try { Process.Start("notepad.exe", "\"" + RunLogPath + "\""); } catch (Exception) { }
    }

    void OnRestartAdmin(object sender, EventArgs e) {
      if (scriptPath == null) return;
      SaveConfig();
      var psi = new ProcessStartInfo("powershell.exe",
        "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"");
      psi.Verb = "runas";
      psi.UseShellExecute = true;
      try { Process.Start(psi); Close(); }
      catch (Win32Exception) { /* the user declined the UAC prompt: stay open */ }
    }

    protected override void OnShown(EventArgs e) {
      base.OnShown(e);
      // With a saved code, Enter starts the sync right away; otherwise the code field waits for input.
      if (SyncClient.IsValidCode(codeBox.Text)) primary.Focus(); else codeBox.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
      if (busy && e.CloseReason == CloseReason.UserClosing &&
          MessageBox.Show(this, Strings.Get("confirm_close"), "RealmForge", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) {
        e.Cancel = true;
        return;
      }
      SaveConfig();
      base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ native niceties (optional)

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch (Exception) { }  // dark title bar (Win10 20H1+)
      try { SendMessage(codeBox.Handle, 0x1501, (IntPtr)1, "rf_…"); } catch (Exception) { }       // EM_SETCUEBANNER
    }
  }
}
