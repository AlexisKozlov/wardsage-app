// Wardsage - TEST build: the lab runner (tools/lab/WardsageLab.ps1) starts and stops the screen recorder (app/RecCapture.cs) without clicks,
// through a command file; app/HostBridge.cs reads it once a second and answers in an ack file. Both live in %LOCALAPPDATA%\WardsageTest\lab\.
//   rec.cmd  one line:  "<seq> start <scenario> <run id>"   or   "<seq> stop"
//            or "<seq> city" / "<seq> prep <name>" (lab setup, below)
//            seq = the runner's clock (unix ms) at the command: a new one per command, an old file (over StaleMs) is never acted on
//   rec.ack  {"seq":N,"state":"recording|stopped|idle|busy|bad","rec":"<id>","scenario":"..","run":".."}
// Lab setup (TEST builds only, the app refuses it otherwise): "city" = the app's own road back to the city (CityBack / OfferClose / MapClose through the
// click gate) until the screen is the city, ack state "city" (or "cityfail" with "why"); "prep <name>" = the game put into a non-standard start state
// (LabPrep.Names) by clicking only through the click gate (the equip / sell owners' own whitelists: never «Продать», a purchase, a spend or a
// confirm), ack state "prepok" (or "prepfail" with "why"), the name in "scenario". The ack comes when the work is over (the runner waits for it).
// A lab recording has its own id (lab-yyyyMMdd-HHmmss-xxxxxx), lab.json with the scenario and the run in its first part, at most CapMs, and
// is uploaded like any other one; the lab server deletes it when the scenario passed. This file is the pure part: parsing and the decision.
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RealmForge {
  public sealed class LabRecCmd {
    public long Seq;
    public string Op = "", Scenario = "", Run = "";
    /// <summary>The prep's name (Op "prep").</summary>
    public string Name = "";

    public const long CapMs = 6 * 60 * 1000;          // a lab recording stops by itself after 6 min
    public const long StaleMs = 2 * 60 * 1000;        // a command older than this is left alone (an app started after the run)
    public const long StartWaitMs = 10000;            // the wait for another recording to stop before a lab one starts

    static readonly Regex LineRx = new Regex("^([0-9]{1,15}) (?:(start) ([a-z0-9_]{1,40}) ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})|(stop)|(settings)|(city)|(prep) ([a-z_]{1,30}))$");
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
      if (m.Groups[6].Success) return new LabRecCmd { Seq = seq, Op = "settings" };
      if (m.Groups[7].Success) return new LabRecCmd { Seq = seq, Op = "city" };
      if (m.Groups[8].Success) return LabPrep.Known(m.Groups[9].Value) ? new LabRecCmd { Seq = seq, Op = "prep", Name = m.Groups[9].Value } : null;
      return new LabRecCmd { Seq = seq, Op = "stop" };
    }

    /// <summary>A hint-mode scenario (equip_city_hint, locate_hint, equip_hint_filter_open): the app runs it with auto click off.</summary>
    public static bool IsHintScenario(string scenario) { return scenario != null && (scenario.EndsWith("_hint", StringComparison.Ordinal) || scenario.StartsWith("equip_hint_", StringComparison.Ordinal)); }

    /// <summary>A scenario that starts with the app's auto click OFF and gets it ON later through the «settings» command (equip_toggle_auto_late: the plan is seeded
    /// while the player's auto-equip setting is off, ten seconds on it is switched on and the running plan has to be picked up).</summary>
    public static bool IsAutoOffScenario(string scenario) { return scenario == "equip_toggle_auto_late"; }

    /// <summary>The hint mode (auto click off for a «_hint» scenario) ends by itself after this long (a lost stop command): longer than the
    /// runner's longest hint scenario (180 s) with its waits - at 3 min the auto click came back mid-scenario and the page ran the plan.</summary>
    public const long HintMaxMs = 10 * 60 * 1000;
    /// <summary>The hint mode that began at <paramref name="atMs"/> is over by now.</summary>
    public static bool HintExpired(long atMs, long nowMs) { return nowMs - atMs > HintMaxMs; }
    /// <summary>The app's auto click (and the page's: the state event carries the same value): the player's setting or the lab's forcing, the site's
    /// switch, and never in the lab's hint mode.</summary>
    public static bool AutoClick(bool cfgAuto, bool labForce, bool siteSwitch, bool hintOnly) { return (cfgAuto || labForce) && siteSwitch && !hintOnly; }

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

    /// <summary>The ack of a «city» / «prep» command: state city | cityfail | prepok | prepfail, the prep's name in "scenario", the reason in "why".</summary>
    public static string AckSetup(long seq, string state, string name, string why) {
      return "{\"seq\":" + seq.ToString(CultureInfo.InvariantCulture) + ",\"state\":" + MiniJson.Quote(state ?? "") + ",\"rec\":\"\",\"scenario\":" + MiniJson.Quote(name ?? "")
        + ",\"run\":\"\",\"why\":" + MiniJson.Quote(why ?? "") + "}";
    }

    public static string Ack(long seq, string state, string rec, string scenario, string run) {
      return "{\"seq\":" + seq.ToString(CultureInfo.InvariantCulture) + ",\"state\":" + MiniJson.Quote(state ?? "") + ",\"rec\":" + MiniJson.Quote(rec ?? "")
        + ",\"scenario\":" + MiniJson.Quote(scenario ?? "") + ",\"run\":" + MiniJson.Quote(run ?? "") + "}";
    }
  }

  /// <summary>What to do with a command, given the recorder. One instance per app run; it remembers the last command done.</summary>
  public sealed class LabRecControl {
    public enum Act { None, Start, Stop, StopThenStart, Settings, City, Prep }
    long lastSeq = -1;

    /// <summary>The action for <paramref name="c"/> at <paramref name="nowMs"/> (unix ms) while the recorder is <paramref name="recOn"/> with
    /// <paramref name="currentRec"/>; state = the ack's state (null = nothing to answer: the same or an old command).</summary>
    public Act Decide(LabRecCmd c, long nowMs, bool recOn, string currentRec, out string state) {
      state = null;
      if (c == null || c.Seq <= lastSeq) return Act.None;
      lastSeq = c.Seq;
      if (nowMs - c.Seq > LabRecCmd.StaleMs || c.Seq - nowMs > LabRecCmd.StaleMs) { state = "stale"; return Act.None; }
      if (c.Op == "settings") { state = "settings"; return Act.Settings; }
      if (c.Op == "city") { state = "city"; return Act.City; }
      if (c.Op == "prep") { state = "prep"; return Act.Prep; }
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

  // ------------------------------------------------------------------------------------------------------------------------------------------------
  // Lab setup (TEST builds): the pure part of «city» and «prep <name>». The host (app/Overlay.cs TickLabPrep) reads the game's screen once a tick
  // into a LabPrepView, asks LabPrep.Step for the next move and sends a click ONLY through the click gate (ClickGate.Check with the equip or the sell
  // owner: the targets of those owners' own whitelists - the city's «Герои» / «Инвентарь», the hero grid's filter, a hero's slot, the gear filter,
  // the inventory's tabs and «Массовая продажа»; the one new target is the inventory's materials tab). No target of a purchase, a spend, a confirm or
  // «Продать» exists in this file. Every move is checked against the screen before the next one; a click with no effect is tried a few times, then
  // the prep fails with the reason (the lab reports it, the scenario then runs from where the game stands).

  public enum LabMove { Wait, Click, Back, Done, Fail }

  public sealed class LabPrepAct {
    public LabMove Move; public ClickOwner Owner; public ClickTarget Target; public double X, Y; public string Why = "";
    public override string ToString() { return Move + (Move == LabMove.Click ? " " + Owner + "/" + Target + " @" + (int)Math.Round(X) + "," + (int)Math.Round(Y) : "") + (Why != "" ? " (" + Why + ")" : ""); }
  }

  /// <summary>What the host reads of the game this tick (client pixels).</summary>
  public sealed class LabPrepView {
    public long Now; public bool Front;
    public double W, H, U;                       // the client size and the UI unit (Ui.Unit)
    public ScreenKind Kind; public bool Modal;
    // the city
    public bool HeroesButton, InvButton;         // the bottom bar's buttons are seen with nothing over them
    public double[] CityInvPt;                   // where «Инвентарь» is {x, y}
    // the inventory
    public bool InvEquipTab, InvArtTab, InvBulk, InvBulkKnown, InvArtBulk, InvArtBulkKnown;
    public double[] ArtTabPt, MatTabPt;          // the left menu's «Артефакт» / «Материалы» toggles {x, y}
    // the hero screen
    public bool HeroFormOk; public int Tab = -1, Part = -1;
    public bool GridFilterOpen; public long[] FactionList, ChosenCamps; public bool ClassChosen;
    public double[] SlotPt, FilterPt;            // the first gear slot / the gear list's filter button {x, y}
    // the gear list's filter pop-up
    public bool PanelOpen, SidePanelOpen, SetPanelOpen, FilterRead, HideEquipped; public long[] Suits;
  }

  public sealed class LabPrep {
    public static readonly string[] Names = { "hero_filter", "hero_filter_closed", "item_filter", "hide_worn_on", "start_inventory_materials", "start_bulk_sale", "start_artifact" };
    public const string CityName = "city";
    public static bool Known(string name) { return Array.IndexOf(Names, name) >= 0; }
    public const long CityCapMs = 30000, PrepCapMs = 20000, GapMs = 1600, SlowGapMs = 3000;   // the lab's speed-up: a prep ends in 20 s, the road to the city in 30 s, or it fails with its step
    public const int SetPick = 3;                 // the set cell the item filter ticks (the 4th of the pop-up's grid: not the first, which a plan's item often is)

    public readonly string Name;
    public string Phase = "";
    long t0 = -1, lastAt = -1000000;
    readonly System.Collections.Generic.Dictionary<string, int> tries = new System.Collections.Generic.Dictionary<string, int>();
    readonly HeroGeometry g = new HeroGeometry();
    readonly FilterGeometry fg = new FilterGeometry();

    public LabPrep(string name) { Name = name; }
    public bool IsCity { get { return Name == CityName; } }
    public long CapMs { get { return IsCity ? CityCapMs : PrepCapMs; } }
    /// <summary>The click owner of the prep's inventory work (the sell owner's whitelist) or of the hero work (the equip owner's).</summary>
    public bool InventoryPrep { get { return Name.StartsWith("start_", StringComparison.Ordinal); } }

    LabPrepAct Wait(string why) { return new LabPrepAct { Move = LabMove.Wait, Why = why }; }
    LabPrepAct Fail(string why) { return new LabPrepAct { Move = LabMove.Fail, Why = why }; }
    LabPrepAct Done(string why) { return new LabPrepAct { Move = LabMove.Done, Why = why }; }
    LabPrepAct Back(string why) { return new LabPrepAct { Move = LabMove.Back, Why = why }; }
    /// <summary>One click of a step: not before the gap since the last one, at most <paramref name="max"/> times (then the prep fails: the click has no effect).</summary>
    LabPrepAct Click(LabPrepView v, ClickOwner owner, ClickTarget target, double x, double y, string key, int max, long gap) {
      if (v.Now - lastAt < gap) return Wait("after " + key);
      int n; tries.TryGetValue(key, out n);
      if (n >= max) return Fail(key + " has no effect after " + n + " clicks (screen " + v.Kind + ")");
      tries[key] = n + 1; lastAt = v.Now; Phase = key;
      return new LabPrepAct { Move = LabMove.Click, Owner = owner, Target = target, X = x, Y = y, Why = key };
    }
    int Tries(string key) { int n; tries.TryGetValue(key, out n); return n; }

    public LabPrepAct Step(LabPrepView v) {
      if (t0 < 0) t0 = v.Now;
      if (v.Now - t0 > CapMs) return Fail("not done in " + CapMs / 1000 + " s (" + (Phase == "" ? "no step" : Phase) + ", screen " + v.Kind + ")");
      if (IsCity) { Phase = "city"; return v.Kind == ScreenKind.City && !v.Modal ? Done("the city") : Back("screen " + v.Kind); }
      if (!v.Front) return Wait("the game is not in front");
      switch (Name) {
        case "hero_filter": case "hero_filter_closed": return StepHeroFilter(v, Name == "hero_filter_closed");
        case "item_filter": case "hide_worn_on": return StepGearFilter(v, Name == "hide_worn_on");
        case "start_inventory_materials": case "start_bulk_sale": case "start_artifact": return StepInventory(v);
      }
      return Fail("unknown prep " + Name);
    }

    /// <summary>From the city to the hero screen (the equip owner's «Герои»); other screens go back to the city first. Null = the hero screen is up.</summary>
    LabPrepAct ToHeroes(LabPrepView v) {
      if (v.Kind == ScreenKind.HeroScreen) return v.HeroFormOk ? null : Wait("the hero screen is not read");
      if (v.Kind != ScreenKind.City) return Back("screen " + v.Kind);
      if (!v.HeroesButton) return Wait("«Герои» is not seen");
      return Click(v, ClickOwner.Equip, ClickTarget.EquipNav, v.W - g.HeroesBtnDX * v.U, v.H - g.HeroesBtnDY * v.U, "heroes", 4, SlowGapMs);
    }
    LabPrepAct GridFilterCell(LabPrepView v, double x, int row, string key, int max) {
      return Click(v, ClickOwner.Equip, ClickTarget.EquipFilter, x * v.U, (g.FilterRow0 + row * g.FilterPitch) * v.U, key, max, GapMs);
    }
    LabPrepAct Funnel(LabPrepView v, bool open, string key, int max) {
      return Click(v, ClickOwner.Equip, open ? ClickTarget.EquipFilter : ClickTarget.EquipNav, g.FunnelX * v.U, v.H - g.FunnelDY * v.U, key, max, GapMs);
    }

    // hero_filter / hero_filter_closed: the hero grid's filter pop-up with ONE faction ticked (never «Все»): open, or closed again (the filter stays on)
    LabPrepAct StepHeroFilter(LabPrepView v, bool closed) {
      var a = ToHeroes(v); if (a != null) return a;
      if (v.Part >= 0 && !v.GridFilterOpen) return Click(v, ClickOwner.Equip, ClickTarget.EquipNav, g.BackX * v.U, g.BackY * v.U, "back", 4, GapMs + 600);
      bool chosen = v.ChosenCamps != null && v.ChosenCamps.Length > 0;
      if (v.GridFilterOpen) {
        if (chosen) return closed ? Funnel(v, true, "funnel-close", 3) : Done("the faction is ticked, the panel is open");
        if (v.ClassChosen) return GridFilterCell(v, g.ClassX, 0, "class-all", 2);
        if (v.FactionList == null || v.FactionList.Length < 2) return Wait("the faction list is not read");
        return GridFilterCell(v, g.FactionX, 1, "faction", 3);
      }
      if (chosen && closed) return Done("the faction is ticked, the panel is closed");
      return Funnel(v, false, "funnel-open", 4);
    }

    // item_filter / hide_worn_on: on the first gear slot's item list, the gear filter's pop-up: a set ticked (the side panel closed, the pop-up left open),
    // or «Скрыть надетое» ticked
    LabPrepAct StepGearFilter(LabPrepView v, bool hideWorn) {
      var a = ToHeroes(v); if (a != null) return a;
      if (v.GridFilterOpen && v.Part < 0) return Funnel(v, true, "grid-close", 3);   // (the roster's pop-up covers the slots)
      if (v.Tab >= 0 && v.Tab != HeroGeometry.GearTab) return Click(v, ClickOwner.Equip, ClickTarget.EquipNav, v.W - g.GearTabDX * v.U, g.GearTabY * v.U, "gear-tab", 4, GapMs + 600);
      if (v.Part < 0) {
        if (v.SlotPt == null) return Fail("the first slot's place is unknown");
        return Click(v, ClickOwner.Equip, ClickTarget.EquipNav, v.SlotPt[0], v.SlotPt[1], "slot", 4, SlowGapMs);
      }
      if (!v.PanelOpen) {
        if (v.FilterPt == null) return Fail("the gear filter button's place is unknown");
        return Click(v, ClickOwner.Equip, ClickTarget.EquipFilter, v.FilterPt[0], v.FilterPt[1], "filter-open", 4, SlowGapMs);
      }
      if (!v.FilterRead) return Wait("the gear filter is not read");
      if (hideWorn) {
        if (v.HideEquipped) return Done("«Скрыть надетое» is ticked");
        return Click(v, ClickOwner.Equip, ClickTarget.EquipFilter, fg.X(fg.HideEquippedX, v.W, v.H), fg.Y(fg.HideEquippedY, v.W, v.H), "hide-worn", 3, GapMs);
      }
      bool chosen = v.Suits != null && v.Suits.Length > 0;
      double closeX = fg.X(fg.SideCloseX, v.W, v.H), closeY = fg.Y(fg.CloseY, v.W, v.H);
      if (chosen) return v.SidePanelOpen ? Click(v, ClickOwner.Equip, ClickTarget.EquipFilter, closeX, closeY, "side-close", 3, GapMs + 600) : Done("a set is ticked, the pop-up is open");
      if (!v.SidePanelOpen) return Click(v, ClickOwner.Equip, ClickTarget.EquipFilter, fg.X(fg.SetArrowX, v.W, v.H), fg.Y(fg.SetArrowY, v.W, v.H), "set-arrow", 3, GapMs + 600);
      if (!v.SetPanelOpen) return Click(v, ClickOwner.Equip, ClickTarget.EquipFilter, closeX, closeY, "side-reset", 2, GapMs + 600);   // (the main stats' panel opened instead)
      var c = fg.SetCell(SetPick, v.W, v.H);
      return Click(v, ClickOwner.Equip, ClickTarget.EquipFilter, c[0], c[1], "set-cell", 3, GapMs + 400);
    }

    // start_inventory_materials / start_bulk_sale / start_artifact: the sell owner's own way to the inventory (HeroBack, «Инвентарь», the left menu's tabs,
    // «Массовая продажа» entered with nothing selected)
    LabPrepAct StepInventory(LabPrepView v) {
      if (v.Kind == ScreenKind.HeroScreen) return Click(v, ClickOwner.Sell, ClickTarget.HeroBack, g.BackX * v.U, g.BackY * v.U, "hero-back", 4, GapMs + 600);
      if (v.Kind == ScreenKind.City) {
        if (!v.InvButton || v.CityInvPt == null) return Wait("«Инвентарь» is not seen");
        return Click(v, ClickOwner.Sell, ClickTarget.CityInventory, v.CityInvPt[0], v.CityInvPt[1], "inventory", 4, SlowGapMs);
      }
      if (v.Kind != ScreenKind.Inventory) return Back("screen " + v.Kind);
      switch (Name) {
        case "start_inventory_materials":
          if (Tries("materials") > 0 && !v.InvEquipTab && !v.InvArtTab && v.Now - lastAt >= GapMs) return Done("the inventory is on a tab other than equipment / artifacts");
          // (the gate lets a non-artifact click through on the artifact tab only for «Снаряжение»: that tab first)
          if (v.InvArtTab) return Click(v, ClickOwner.Sell, ClickTarget.InvEquipTab, 0.085 * v.U, 0.315 * v.U, "equip-tab", 4, GapMs + 600);
          if (v.MatTabPt == null) return Fail("the materials tab's place is unknown");
          return Click(v, ClickOwner.Sell, ClickTarget.InvMaterialsTab, v.MatTabPt[0], v.MatTabPt[1], "materials", 3, SlowGapMs);
        case "start_bulk_sale":
          if (!v.InvEquipTab) return Click(v, ClickOwner.Sell, ClickTarget.InvEquipTab, 0.085 * v.U, 0.315 * v.U, "equip-tab", 4, GapMs + 600);
          if (!v.InvBulkKnown) return Wait("the sale mode is not read");
          if (v.InvBulk) return Done("«Массовая продажа» is open, nothing selected by the lab");
          return Click(v, ClickOwner.Sell, ClickTarget.InvBulkEnter, 0.5867 * v.U, v.H - 0.0723 * v.U, "bulk-enter", 4, GapMs + 600);
        default:   // start_artifact
          if (!v.InvArtTab) {
            if (v.ArtTabPt == null) return Fail("the artifact tab's place is unknown");
            return Click(v, ClickOwner.Sell, ClickTarget.ArtTab, v.ArtTabPt[0], v.ArtTabPt[1], "art-tab", 4, GapMs + 600);
          }
          if (v.InvArtBulkKnown && v.InvArtBulk) return Fail("the artifact sale mode is open (the lab does not leave it)");
          return Done("the artifact tab is open");
      }
    }
  }
}
