// RealmForge extractor - equip helper window («Переодевание»).
//
// Loads the builds the player sent from the site («Надеть в игре»), finds the game's gear screen once
// (read-only memory scan), then re-reads it several times a second and tells the player which slot / item
// to press. The player equips everything in the game; when all items of a build are on the hero, the build is
// marked done on the site. Nothing is written to the game and no input is sent to it.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RealmForge {
  sealed class HelperForm : Form {
    const int W = 380;
    readonly string site, code;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

    Label title, status, instruction, progress, note;
    ComboBox planBox;
    Label[] rows = new Label[5];
    Button reload, rescan, removePlan;

    List<Plan> plans = new List<Plan>();
    int planIdx = -1;
    EquipAddrs addrs;
    EquipLive live;
    bool loading, scanning, finishing;
    string statusKey; object[] statusArgs;
    readonly HashSet<string> reported = new HashSet<string>();

    public HelperForm(string site, string code) {
      this.site = site;
      this.code = code;
      Build();
      ApplyTexts();
      timer.Interval = 400;
      timer.Tick += delegate { Tick(); };
      Shown += delegate { LoadPlans(true); };
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
      FormBorderStyle = FormBorderStyle.FixedToolWindow;
      TopMost = true;                    // stays above the game window
      ShowInTaskbar = true;
      AutoSize = true;
      AutoSizeMode = AutoSizeMode.GrowAndShrink;
      StartPosition = FormStartPosition.Manual;
      var wa = Screen.PrimaryScreen.WorkingArea;
      Location = new Point(wa.Right - W - 60, wa.Top + 60);

      var root = new FlowLayoutPanel();
      root.FlowDirection = FlowDirection.TopDown;
      root.WrapContents = false;
      root.AutoSize = true;
      root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
      root.Padding = new Padding(16, 12, 16, 12);
      Controls.Add(root);

      title = Lbl(Theme.Gold, 13F, FontStyle.Bold);
      title.Font = new Font("Georgia", 13F, FontStyle.Bold);
      root.Controls.Add(title);

      planBox = new ComboBox();
      planBox.DropDownStyle = ComboBoxStyle.DropDownList;
      planBox.Width = W;
      planBox.BackColor = Theme.Field;
      planBox.ForeColor = Theme.Text;
      planBox.FlatStyle = FlatStyle.Flat;
      planBox.Margin = new Padding(0, 8, 0, 8);
      planBox.SelectedIndexChanged += delegate { if (planBox.SelectedIndex >= 0) planIdx = planBox.SelectedIndex; Render(); };
      root.Controls.Add(planBox);

      for (int i = 0; i < rows.Length; i++) {
        rows[i] = Lbl(Theme.Muted, 9.5F, FontStyle.Regular);
        rows[i].Margin = new Padding(0, 1, 0, 1);
        root.Controls.Add(rows[i]);
      }

      instruction = Lbl(Theme.Gold, 12F, FontStyle.Bold);
      instruction.Margin = new Padding(0, 12, 0, 4);
      root.Controls.Add(instruction);
      progress = Lbl(Theme.Muted, 9F, FontStyle.Regular);
      root.Controls.Add(progress);
      status = Lbl(Theme.Muted, 9F, FontStyle.Regular);
      status.Margin = new Padding(0, 6, 0, 6);
      root.Controls.Add(status);

      var buttons = new FlowLayoutPanel();
      buttons.AutoSize = true;
      buttons.Margin = new Padding(0, 4, 0, 4);
      reload = Btn(); reload.Click += delegate { LoadPlans(false); };
      rescan = Btn(); rescan.Click += delegate { StartScan(); };
      removePlan = Btn(); removePlan.Click += delegate { RemoveCurrent(); };
      buttons.Controls.Add(reload); buttons.Controls.Add(rescan); buttons.Controls.Add(removePlan);
      root.Controls.Add(buttons);

      note = Lbl(Theme.Muted, 8.25F, FontStyle.Regular);
      root.Controls.Add(note);
      ResumeLayout(false);
      PerformLayout();
    }

    Label Lbl(Color c, float size, FontStyle style) {
      var l = new Label();
      l.AutoSize = true;
      l.MaximumSize = new Size(W, 0);
      l.ForeColor = c;
      l.Font = new Font("Segoe UI", size, style);
      l.Margin = new Padding(0);
      return l;
    }

    Button Btn() {
      var b = new Button();
      b.FlatStyle = FlatStyle.Flat;
      b.UseVisualStyleBackColor = false;
      b.BackColor = Theme.Field;
      b.ForeColor = Theme.Text;
      b.FlatAppearance.BorderColor = Theme.GoldDeep;
      b.FlatAppearance.MouseOverBackColor = Theme.Track;
      b.AutoSize = true;
      b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
      b.MinimumSize = new Size(0, 30);
      b.Padding = new Padding(6, 0, 6, 0);
      b.Margin = new Padding(0, 0, 6, 0);
      b.Cursor = Cursors.Hand;
      return b;
    }

    void ApplyTexts() {
      title.Text = Strings.Get("eq_title");
      reload.Text = Strings.Get("eq_reload");
      rescan.Text = Strings.Get("eq_rescan");
      removePlan.Text = Strings.Get("eq_skip");
      note.Text = Strings.Get("eq_note");
    }

    void SetStatus(string key, params object[] args) { statusKey = key; statusArgs = args; Render(); }

    // ------------------------------------------------------------------ work

    void LoadPlans(bool scanAfter) {
      if (loading) return;
      loading = true;
      SetStatus("eq_loading");
      var w = new BackgroundWorker();
      w.DoWork += (s, e) => { e.Result = PlansClient.List(site, code, Strings.Lang); };
      w.RunWorkerCompleted += (s, e) => {
        loading = false;
        var r = e.Error == null ? e.Result as PlansResult : null;
        if (r == null || r.Status != PlansStatus.Ok) {
          if (r != null && r.Status == PlansStatus.InvalidToken) SetStatus("eq_code");
          else SetStatus("eq_net", r == null ? (e.Error != null ? e.Error.Message : "?") : (r.Details ?? ("HTTP " + r.HttpCode)));
          return;
        }
        plans = r.Plans;
        planBox.Items.Clear();
        foreach (var p in plans) planBox.Items.Add(p.HeroName + "  ·  " + p.Items.Count);
        planIdx = plans.Count > 0 ? 0 : -1;
        if (planIdx >= 0) planBox.SelectedIndex = 0;
        if (plans.Count == 0) { SetStatus("eq_none"); return; }
        SetStatus(null);
        if (scanAfter || addrs == null || MissingItems()) StartScan();
      };
      w.RunWorkerAsync();
    }

    bool MissingItems() {
      if (addrs == null) return true;
      foreach (var p in plans) foreach (var it in p.Items) if (!addrs.Items.ContainsKey(it.Uid)) return true;
      return false;
    }

    List<long> AllUids() {
      var l = new List<long>();
      foreach (var p in plans) foreach (var it in p.Items) if (!l.Contains(it.Uid)) l.Add(it.Uid);
      return l;
    }

    void StartScan() {
      if (scanning || plans.Count == 0) return;
      scanning = true;
      timer.Stop();
      SetStatus("eq_scanning");
      var uids = AllUids();
      var w = new BackgroundWorker();
      w.DoWork += (s, e) => { lock (Extractor.Gate) e.Result = RFX.FindEquip(uids, null); };
      w.RunWorkerCompleted += (s, e) => {
        scanning = false;
        addrs = e.Error == null ? e.Result as EquipAddrs : null;
        if (addrs == null) { SetStatus("eq_scan_fail"); return; }
        SetStatus(addrs.Panel == 0 ? "eq_no_panel" : null);
        timer.Start();
        Tick();
      };
      w.RunWorkerAsync();
    }

    void Tick() {
      if (addrs == null || scanning) return;
      try { live = RFX.Poll(addrs, AllUids()); } catch (Exception) { live = null; }
      if (live != null && !live.GameRunning) timer.Stop();
      int auto = EquipGuide.PickPlan(plans, live, planIdx);
      if (auto != planIdx && auto >= 0) { planIdx = auto; planBox.SelectedIndex = auto; }
      Render();
    }

    void RemoveCurrent() {
      if (planIdx < 0 || planIdx >= plans.Count) return;
      var p = plans[planIdx];
      Report(p, false);
      DropPlan(p);
    }

    void DropPlan(Plan p) {
      int i = plans.IndexOf(p); if (i < 0) return;
      plans.RemoveAt(i);
      planBox.Items.RemoveAt(i);
      planIdx = plans.Count == 0 ? -1 : Math.Min(i, plans.Count - 1);
      if (planIdx >= 0) planBox.SelectedIndex = planIdx;
      if (plans.Count == 0) SetStatus("eq_none"); else Render();
    }

    // Marks the plan done (or cancelled) on the site, once.
    void Report(Plan p, bool done) {
      if (!reported.Add(p.Id)) return;
      finishing = true;
      var w = new BackgroundWorker();
      w.DoWork += (s, e) => { e.Result = PlansClient.Finish(site, code, p.Id, done); };
      w.RunWorkerCompleted += delegate { finishing = false; Render(); };
      w.RunWorkerAsync();
    }

    // ------------------------------------------------------------------ view

    void Render() {
      status.Text = statusKey == null ? "" : Strings.Format(statusKey, statusArgs ?? new object[0]);
      Plan p = planIdx >= 0 && planIdx < plans.Count ? plans[planIdx] : null;
      for (int i = 0; i < rows.Length; i++) rows[i].Visible = false;
      removePlan.Enabled = p != null;
      rescan.Enabled = !scanning && plans.Count > 0;
      reload.Enabled = !loading && !scanning;
      if (p == null) { instruction.Text = ""; progress.Text = ""; return; }

      GuideStep g = live != null ? EquipGuide.Next(p, live) : null;
      for (int i = 0; i < rows.Length && i < p.Items.Count; i++) {
        var it = p.Items[i];
        ItemState st = ItemState.Waiting;
        if (g != null) g.States.TryGetValue(it.Uid, out st);
        rows[i].Visible = true;
        rows[i].Text = (st == ItemState.Done ? "✓ " : st == ItemState.Current ? "▶ " : "• ") + it.SlotName + ": " + EquipGuide.ItemLine(it)
          + (st != ItemState.Done && it.FromHeroUid > 0 && it.FromHeroUid != p.HeroUid && it.FromHeroName != null ? "  (" + it.FromHeroName + ")" : "");
        rows[i].ForeColor = st == ItemState.Done ? Theme.Ok : st == ItemState.Current ? Theme.Text : Theme.Muted;
      }
      if (g == null) { instruction.Text = ""; progress.Text = ""; return; }
      progress.Text = Strings.Format("eq_progress", g.DoneCount, p.Items.Count);
      instruction.ForeColor = g.Kind == GuideKind.Done ? Theme.Ok : g.Kind == GuideKind.GameClosed || g.Kind == GuideKind.NotInList ? Theme.Error : Theme.Gold;
      switch (g.Kind) {
        case GuideKind.GameClosed: instruction.Text = Strings.Get("eq_closed"); break;
        case GuideKind.OpenHero: instruction.Text = Strings.Format("eq_open_hero", p.HeroName); break;
        case GuideKind.OpenSlot: instruction.Text = Strings.Format("eq_open_slot", g.Item.SlotName); break;
        case GuideKind.Pick:
          string pick = g.Col > 0 ? Strings.Format("eq_pick", g.Row, g.Col) : Strings.Format("eq_pick_row", g.Row);
          instruction.Text = (g.Row > 4 ? Strings.Format("eq_scroll", g.Row) + " " : "") + pick;
          break;
        case GuideKind.HiddenEquipped: instruction.Text = Strings.Format("eq_hidden_equipped", g.OtherHero ?? Strings.Get("eq_other_hero")); break;
        case GuideKind.HiddenEnhanced: instruction.Text = Strings.Get("eq_hidden_enh"); break;
        case GuideKind.HiddenFilter: instruction.Text = Strings.Get("eq_hidden_filter"); break;
        case GuideKind.NotInList: instruction.Text = Strings.Get("eq_not_in_list"); break;
        case GuideKind.Done:
          if (!reported.Contains(p.Id)) Report(p, true);
          instruction.Text = Strings.Get(finishing ? "eq_done" : "eq_done_sent");
          break;
      }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
      timer.Stop();
      timer.Dispose();
      base.OnFormClosed(e);
    }
  }
}
