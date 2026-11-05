// Wardsage - the auto-equip pilots as actions of the navigator (docs/specs/navigator.md 3.5). They wrap the existing pilots and do not rewrite them: the host builds the
// views (it reads the screens), the adapters turn each pilot answer into an Act with the same click target TickAuto used, so the gate's whitelists stay as they are.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public static class NavKeep {
    public static readonly string[] HeroSeek = { "panel:heroFilter" };
    public static readonly string[] GearFilter = { "Form_EquipFilterWidget", "panel:gearFilter", "panel:gearSide", "Form_ArtifactFilter" };
    /// <summary>The item pick keeps the filter pop-up open only when this target's filter pilot set it (its × drops the filter); a filter left from before is closed.</summary>
    public static readonly string[] ItemPick = { "Form_EquipFilterWidget", "panel:gearFilter", "panel:gearSide", "Form_ArtifactFilter", "Form_CharactorEquipTip", "Form_EquipedCompared", "Form_EquipPopUp", "Form_EquipSellPopup" };
    public static readonly string[] ItemPickNoFilter = { "Form_CharactorEquipTip", "Form_EquipedCompared", "Form_EquipPopUp", "Form_EquipSellPopup" };
    /// <summary><paramref name="keep"/> plus the item card's forms (null keep = the card alone): the card of the item the step works on stays open whatever action wants the screen.</summary>
    public static ICollection<string> WithTip(ICollection<string> keep) {
      var l = new List<string>(); if (keep != null) l.AddRange(keep);
      foreach (var f in ClickGate.TipForms) if (!l.Contains(f)) l.Add(f);
      return l;
    }
  }

  /// <summary>An action's progress mark (IAtTargetAction.ProgressAt): the time its signature last changed.</summary>
  sealed class ProgressMark {
    string sig = ""; public long At;
    public void Note(string s, long now) { if (s != sig) { sig = s; At = now; } }
  }

  static class NavAdapt {
    public static Act ToAct(AutoAction a, ClickTarget target, ClickOwner owner, string baseForm, ICollection<string> keep, string why) {
      return new Act { Owner = owner, Target = target, Kind = a.Kind == AutoKind.Drag ? ActKind.Drag : ActKind.Click, X = a.X, Y = a.Y, DY = a.DY, Cell = a.Cell, Widget = a.Widget, CellKind = a.CellKind, CellId = a.CellId,
        Base = baseForm, Keep = keep, Fire = true, Why = why };
    }
  }

  /// <summary>How the plan's slot is handled (docs/specs/2026-11-04-shortest-paths.md 2): Skip = already on the hero, Direct = the item is in the first screenful, Scroll = computed drags (A3), Filter = the gear filter's set first.</summary>
  public enum SlotMode { Skip, Direct, Scroll, Filter }

  /// <summary>One item of the build as the planner sees it, read from the game's memory before the first click.</summary>
  public sealed class BuildItem {
    public long Uid; public int Slot; public long SetId;
    /// <summary>The artifact (its own tab, its own list): always last.</summary>
    public bool Artifact;
    /// <summary>Already on the hero.</summary>
    public bool Worn;
    /// <summary>Its row (1-based) in its slot's list now, 0 = not known (the list of that slot is not open: it counts as in view, the live row decides when the slot opens).</summary>
    public int Row;
    /// <summary>The player's flags that hide it from its list: worn by another hero («Скрыть надетое»), enhanced («Скрыть заточенные»), «только эксклюзив».</summary>
    public bool HiddenByWorn, HiddenByEnhanced, HiddenByExclusive;
  }

  public sealed class BuildStep {
    public BuildItem Item; public SlotMode Mode; public long SuitId; public int Group; public int EstimateMs; public string Why = "";
  }

  /// <summary>The player's list flags the build clears once at its start and puts back at its end (S3): what is on now and what the items need.</summary>
  public sealed class BuildFlags {
    public bool ClearWorn, ClearEnhanced, ClearExclusive;
    public bool Any { get { return ClearWorn || ClearEnhanced || ClearExclusive; } }
    public override string ToString() { return (ClearWorn ? "worn " : "") + (ClearEnhanced ? "enhanced " : "") + (ClearExclusive ? "exclusive " : "") ; }
  }

  public sealed class BuildPlan {
    public readonly List<BuildStep> Steps = new List<BuildStep>();
    public BuildFlags Flags = new BuildFlags();
    public int EstimateMs;
    public int FilterOpens;   // how many times the filter widget is opened for the build (one per set group's first item)
    public override string ToString() {
      var sb = new System.Text.StringBuilder();
      foreach (var s in Steps) sb.Append(s.Item.Slot).Append(':').Append(s.Mode).Append(s.Mode == SlotMode.Filter ? "(" + s.SuitId + ")" : "").Append(' ');
      return sb.ToString().TrimEnd();
    }
  }

  /// <summary>The build planner (docs/specs/2026-11-04-shortest-paths.md 2.2-2.4; pure): from the plan's items and what the game's memory says of them, the mode of every slot and the order that makes the build short:
  /// slots without the filter first (while the filter is empty), then the set groups (3 items before 2), the filter opened once per group and the set changed inside it without the ×, the artifact last. A slot costs the
  /// cheaper of the computed drags (0.9 s each) and the set filter (3 s the first item of a group, 0 the rest of it); the list flags that hide an item are cleared once for the whole build.</summary>
  public static class BuildPlanner {
    /// <summary>The costs, in ms (docs/specs/2026-11-04-shortest-paths.md 1, 2): a drag with its stop, the filter's first opening, a set change inside the open widget, the item itself (click, card, «Заменить»).</summary>
    public const int DragMs = 900, FilterOpenMs = 3000, SetChangeMs = 1200, ItemMs = 2200, ArtifactMs = 3200;
    /// <summary>The norms the pilots are judged by: one item (the slot to the confirm), its filter. Twice that is a reset of the step with its diagnosis, never a repeat (the slow PC doubles them).</summary>
    public const int ItemLimitMs = 6000, FilterLimitMs = 4000;
    /// <summary>The rows of the gear list in view (VISIBLE_ROWS of ui/guide.js: the 1080p window shows about 4.5) and the rows one drag brings.</summary>
    public const int ViewRows = 4;

    /// <summary>The norm of a recipe's step (its budgetMs, tools/recipes/*.json), <paramref name="dflt"/> when the recipe or its budget is not there.</summary>
    public static int NormMs(string recipe, string step, int dflt) {
      var r = NavRecipes.Get(recipe);
      if (r != null) foreach (var st in r.Steps) if (st.Id == step && st.BudgetMs > 0) return st.BudgetMs;
      return dflt;
    }

    /// <summary>The drags (A3) to bring a row into view: the rows beyond the first screenful, a drag moves a screenful less one row.</summary>
    public static int DragsFor(int row) {
      if (row <= ViewRows) return 0;
      return (row - ViewRows + (ViewRows - 1) - 1) / (ViewRows - 1);
    }

    /// <summary>True when the filter costs less than the drags for this row (the single-item test the page's guide uses as well: ui/guide.js farRow).</summary>
    public static bool FilterBeatsScroll(int row) { return DragsFor(row) * DragMs > FilterOpenMs; }

    public static BuildFlags FlagsFor(IList<BuildItem> items, bool worn, bool enhanced, bool exclusive) {
      var f = new BuildFlags();
      foreach (var it in items) {
        if (it.Worn) continue;
        if (it.HiddenByWorn && worn) f.ClearWorn = true;
        if (it.HiddenByEnhanced && enhanced) f.ClearEnhanced = true;
        if (it.HiddenByExclusive && exclusive) f.ClearExclusive = true;
      }
      return f;
    }

    /// <summary><paramref name="items"/> in the plan's order, the player's three flags as they are now (worn / enhanced / exclusive-only hide).</summary>
    public static BuildPlan Plan(IList<BuildItem> items, bool flagWorn, bool flagEnhanced, bool flagExclusive) {
      var plan = new BuildPlan();
      plan.Flags = FlagsFor(items, flagWorn, flagEnhanced, flagExclusive);
      var gear = new List<BuildStep>(); BuildStep art = null;
      foreach (var it in items) {
        var st = new BuildStep { Item = it };
        if (it.Worn) { st.Mode = SlotMode.Skip; st.Why = "on the hero"; }
        else {
          int drags = DragsFor(it.Row);
          st.Mode = drags == 0 ? SlotMode.Direct : SlotMode.Scroll;
          st.EstimateMs = ItemMs + drags * DragMs; st.Why = it.Row > 0 ? "row " + it.Row : "row unknown";
        }
        if (it.Artifact) { st.EstimateMs = ArtifactMs; art = st; continue; }
        gear.Add(st);
      }
      // a set group (2 or 3 items of one set) takes the filter when its items' drags cost more than one opening of it (the first of the group pays it); a lone item when its own drags do
      var bySet = new Dictionary<long, List<BuildStep>>();
      foreach (var st in gear) if (st.Mode != SlotMode.Skip && st.Item.SetId > 0) { List<BuildStep> l; if (!bySet.TryGetValue(st.Item.SetId, out l)) bySet[st.Item.SetId] = l = new List<BuildStep>(); l.Add(st); }
      var groups = new List<List<BuildStep>>();
      foreach (var kv in bySet) {
        int scroll = 0; var members = new List<BuildStep>();
        foreach (var st in kv.Value) if (st.Mode == SlotMode.Scroll) { scroll += DragsFor(st.Item.Row) * DragMs; members.Add(st); }
        if (members.Count == 0 || scroll <= FilterOpenMs) continue;
        foreach (var st in members) { st.Mode = SlotMode.Filter; st.SuitId = kv.Key; st.EstimateMs = ItemMs; st.Why += ", set " + kv.Key + " filter"; }
        groups.Add(members);
      }
      // the order: the slots with no filter, then the groups of 3 before the groups of 2 (the plan's slot order inside and between equals), Skip kept first (it costs nothing)
      var ordered = new List<BuildStep>();
      foreach (var st in gear) if (st.Mode == SlotMode.Skip) ordered.Add(st);
      foreach (var st in gear) if (st.Mode == SlotMode.Direct || st.Mode == SlotMode.Scroll) ordered.Add(st);
      var gl = new List<List<BuildStep>>(groups);
      gl.Sort((a, b) => { int c = b.Count.CompareTo(a.Count); return c != 0 ? c : gear.IndexOf(a[0]).CompareTo(gear.IndexOf(b[0])); });
      int gi = 0;
      foreach (var grp in gl) {
        gi++; plan.FilterOpens++;
        for (int i = 0; i < grp.Count; i++) { grp[i].Group = gi; if (i == 0) grp[i].EstimateMs += plan.FilterOpens == 1 ? FilterOpenMs : SetChangeMs; ordered.Add(grp[i]); }
      }
      if (art != null) ordered.Add(art);
      plan.Steps.AddRange(ordered);
      foreach (var st in ordered) plan.EstimateMs += st.EstimateMs;
      return plan;
    }
  }

  /// <summary>The player's list flags during one build (S3): read once at its start from the game's memory, what the build's items need cleared is cleared once, and is NOT put back (owner's decision). Pure: the host feeds it the flags it read (-1 unread) and does the clicks it asks for.</summary>
  public sealed class BuildFlagKeeper {
    public bool Begun, ClearedWorn, ClearedExclusive;
    public int WornWas = -1, ExclusiveWas = -1;
    BuildFlags need = new BuildFlags();
    /// <summary>The start of a build: the flags as read (1 on, 0 off, -1 unread) and what the plan's items need (BuildPlan.Flags). Enhanced cannot be switched here: the game's widget hides that toggle for the hero list.</summary>
    public void Begin(int worn, int exclusive, BuildFlags needed) {
      Begun = true; WornWas = worn; ExclusiveWas = exclusive; need = needed ?? new BuildFlags(); ClearedWorn = ClearedExclusive = false;
    }
    /// <summary>The flag to switch off now ("" = none): once, at the start, only when an item needs it and the read says it is on.</summary>
    public string ClearDue(int wornNow, int exclusiveNow) {
      if (!Begun) return "";
      if (need.ClearWorn && !ClearedWorn && wornNow == 1) return "worn";
      if (need.ClearExclusive && !ClearedExclusive && exclusiveNow == 1) return "exclusive";
      return "";
    }
    public void Cleared(string flag) { if (flag == "worn") ClearedWorn = true; else if (flag == "exclusive") ClearedExclusive = true; }
    public void End() { Begun = false; ClearedWorn = ClearedExclusive = false; }
    /// <summary>The applog line of the build's end (the lab checks it): «Скрыть надетое» as the build found it, and whether the build cleared it (only when an item needed it) or kept it. Never put back.</summary>
    public static string Line(int before, bool cleared) {
      Func<int, string> s = n => n == 1 ? "on" : n == 0 ? "off" : "unread";
      return "flags: hideWorn before=" + s(before) + " action=" + (cleared ? "cleared" : "kept");
    }
    public override string ToString() { return "flags worn=" + WornWas + " exclusive=" + ExclusiveWas + " need " + need + (ClearedWorn ? "cleared-worn " : "") + (ClearedExclusive ? "cleared-exclusive" : ""); }
  }

  /// <summary>HeroPilot with NavOwned: finds the plan's hero in the grid (scroll, class / faction filter, picks). Not the city button, the gear tab or the list's back arrow.</summary>
  public sealed class HeroSelectAction : IAtTargetAction {
    readonly HeroPilot pilot; readonly Func<Snapshot, NavTarget, long, HeroView> view;
    /// <summary>The host's log / recorder hook (the view and the pilot's answer).</summary>
    public Action<HeroView, AutoAction> OnStep;
    public HeroSelectAction(HeroPilot p, Func<Snapshot, NavTarget, long, HeroView> v) { pilot = p; view = v; }
    public string Name { get { return "hero"; } }
    public ICollection<string> Keep { get { return NavKeep.HeroSeek; } }
    public bool Wants(Snapshot s, NavTarget t) { return true; }
    public void Reset() { pilot.Reset(); }
    readonly ProgressMark mark = new ProgressMark();
    public long ProgressAt { get { return mark.At; } }
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) {
      var hv = view(s, t, now); hv.NavOwned = true;
      var ha = pilot.Step(hv);
      mark.Note(pilot.Progress + "|" + hv.Hero + "|" + hv.Tab + "|" + hv.Part + "|" + hv.GridPtr + "|" + (hv.ChosenCamps != null ? hv.ChosenCamps.Length : -1) + "|" + hv.ClassChosen + "|" + hv.FilterOpen, now);
      if (OnStep != null) OnStep(hv, ha);
      if (pilot.FunnelUnseen) t.BlockerMayLie = true;   // (the pop-up may lie over the screen: the way back to the city takes no Escape)
      if (ha == null) return null;
      if (ha.Kind == AutoKind.None && (pilot.State == "notfound" || pilot.State == "failed" || pilot.State == "small")) {
        // the pilot cannot finish: never a wait. The hero is not in an unfiltered grid after a reset already: the player does not have it (a named stop); anything else: back to the city and again
        if (pilot.State == "notfound" && !HeroPilot.FilterActive(hv) && t.Resets > 0 && hv.Heroes != null && hv.Heroes.Length > 0 && Array.IndexOf(hv.Heroes, hv.Plan) < 0) return NavStep.Halt("hero_not_in_grid");
        return NavStep.Reset("hero_" + pilot.State + (pilot.Why != "" ? ": " + pilot.Why : ""));
      }
      if (ha.Kind == AutoKind.None) return NavStep.Wait(pilot.State == "work" || pilot.State == "paused" || pilot.State == "background" ? pilot.State : "hero_" + pilot.State);
      var act = NavAdapt.ToAct(ha, hv.FilterOpen ? ClickTarget.EquipFilter : ClickTarget.EquipNav, t.Owner, "Form_CharactorMain", NavKeep.HeroSeek, "hero: " + pilot.State);
      act.OnNotSent = pilot.NotSent;
      return NavStep.Of(act, "seek:hero");
    }
  }

  /// <summary>FilterPilot: reads the game's gear filter and sets / resets it for the item (always read first, parts that do not match are reset).</summary>
  public sealed class GearFilterAction : IAtTargetAction {
    readonly FilterPilot pilot; readonly Func<Snapshot, NavTarget, long, FilterView> view; readonly Func<bool> wanted;
    public Action<FilterView, AutoAction> OnStep;
    /// <summary>The target key this filter pilot last clicked for: the filter pop-up on screen is its own, not a leftover.</summary>
    public string ActedKey = "";
    public GearFilterAction(FilterPilot p, Func<Snapshot, NavTarget, long, FilterView> v, Func<bool> wanted) { pilot = p; view = v; this.wanted = wanted; }
    public string Name { get { return "filter"; } }
    public ICollection<string> Keep { get { return NavKeep.GearFilter; } }
    public bool Wants(Snapshot s, NavTarget t) { return wanted == null || wanted(); }
    public void Reset() { pilot.Reset(); }
    readonly ProgressMark mark = new ProgressMark();
    public long ProgressAt { get { return mark.At; } }
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) {
      var fv = view(s, t, now);
      var fa = pilot.Step(fv);
      mark.Note(pilot.State + "|" + pilot.Note, now);
      if (OnStep != null) OnStep(fv, fa);
      if (pilot.State == "failed" && pilot.Note != "" && fa == null) return NavStep.Reset("filter: " + pilot.Note);   // a toggle that does not read back: verify-or-reset
      if (fa == null) return null;
      if (fa.Kind == AutoKind.None) return NavStep.Wait(pilot.State == "failed" ? "idle" : "work");
      var act = NavAdapt.ToAct(fa, ClickTarget.EquipFilter, t.Owner, "Form_CharactorMain", NavKeep.GearFilter, "filter: " + pilot.State);
      string key = t.Key(); act.OnSent = () => { ActedKey = key; }; act.OnNotSent = pilot.NotSent;
      return NavStep.Of(act, "act:filter");
    }
  }

  /// <summary>AutoPilot with NavOwned: the slot is open already (facet:slot); list scroll, anchor, the item click and «Заменить» (the player's auto confirm only).</summary>
  public sealed class ItemPickAction : IAtTargetAction {
    readonly AutoPilot pilot; readonly Func<Snapshot, NavTarget, long, AutoView> view; readonly Func<AutoView, AutoAction, ClickTarget> targetOf;
    public Action<AutoView, AutoAction> OnStep; public Action<AutoAction> OnSent;
    readonly GearFilterAction filter; string key = "";
    /// <summary>After the filter acted for this target the item list is rebuilt (the «Скрыть надетое» toggle, the set): no pick until its signature has stood still this long; the list that never settles is a reset.</summary>
    public const int ListSettleMs = 300, ListSettleCapMs = 6000;
    long lastSig = long.MinValue, sigAt, settleFrom;
    /// <summary>The time this item has taken while the pilot was working on it (not the player's pauses, the background, the settle of a rebuilt list): past the norm (BuildPlanner.ItemLimitMs, twice on a slow PC) the step is reset with its diagnosis, never repeated blind.</summary>
    long workMs, lastTickAt, filterMs, totalMs, lastTickAt0; string workKey = "", progSig = ""; bool lastWasFilter;
    /// <summary>The time of the filter's own work on this item (its clicks, the wait for the list to settle after it) is its own budget, not the pick's: eight filter clicks (lab 552: set + 4 sub stats) are 5-6 s and used to run the pick's 6 s out, which sent the pilot
    /// to the city and back for the same slot again. Past this (twice on a slow PC) the step is reset with its diagnosis.</summary>
    public const int FilterAllowMs = 9000;
    public const int DragAllowMs = 1200;
    public int LimitMs = BuildPlanner.NormMs("build", "pick", BuildPlanner.ItemLimitMs);
    public ItemPickAction(AutoPilot p, Func<Snapshot, NavTarget, long, AutoView> v, Func<AutoView, AutoAction, ClickTarget> targetOf, GearFilterAction filter = null) { pilot = p; view = v; this.targetOf = targetOf; this.filter = filter; }
    public string Name { get { return "item"; } }
    public ICollection<string> Keep { get { return filter != null && filter.ActedKey == key ? NavKeep.ItemPick : NavKeep.ItemPickNoFilter; } }
    public bool Wants(Snapshot s, NavTarget t) { key = t.Key(); return true; }
    public void Reset() { pilot.Reset(); workMs = 0; filterMs = 0; lastTickAt = 0; lastWasFilter = false; totalMs = 0; lastTickAt0 = 0; progSig = ""; }
    readonly ProgressMark mark = new ProgressMark();
    public long ProgressAt { get { return mark.At; } }
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) {
      var v = view(s, t, now); v.NavOwned = true;
      // the item's norm: the time of the pilot's own work on it (one tick at most 1 s: a stalled host clock is not the item's time)
      string wk = t.Key();
      if (wk != workKey) { workKey = wk; workMs = 0; filterMs = 0; lastTickAt = 0; lastWasFilter = false; totalMs = 0; lastTickAt0 = 0; progSig = ""; }
      if (lastTickAt > 0) { long dt = Math.Min(1000, now - lastTickAt); if (lastWasFilter) filterMs += dt; else if (pilot.State == "work") workMs += dt; }
      lastTickAt = now; lastWasFilter = false;
      if (filterMs > FilterAllowMs * (io.Slow ? 2 : 1)) {
        long fm = filterMs; filterMs = 0; workMs = 0; lastTickAt = 0;
        return NavStep.Reset("filter_limit " + fm + " ms (norm " + FilterAllowMs + "): the item's filter did not finish");
      }
      // the norm runs from the last real progress, not from the start of the step (lab 559: a legal scroll to the item and the filter's work were cut by the 6 s from the start, the step went back to the city
      // and began again): a change of the list's content position, of the made cells, of the selection or of the card, or a drag made, is progress. Only 6 s with none is a reset; the whole step is capped at 4 norms.
      string prog = v.Sel + "|" + (double.IsNaN(v.LiveScroll) ? "-" : ((int)Math.Round(v.LiveScroll)).ToString()) + "|" + v.LiveFirst + ":" + v.LiveLast + "|" + pilot.Drags + "|" + v.TipShown + "|" + v.SlotOpen;
      if (prog != progSig) { if (progSig != "" && workMs > 0) workMs = 0; progSig = prog; }
      int limit = (LimitMs + pilot.Drags * DragAllowMs) * (io.Slow ? 2 : 1);   // (every drag of the list is progress: a far item of no set takes its drags' time on top of the norm)
      if (pilot.State == "work" && lastTickAt0 > 0) { long dt0 = Math.Min(1000, now - lastTickAt0); totalMs += dt0; }
      lastTickAt0 = now;
      if (pilot.State == "work" && totalMs > 4L * limit) { long ms = totalMs; totalMs = 0; workMs = 0; filterMs = 0; lastTickAt = 0; return NavStep.Reset("item_limit " + ms + " ms (the whole step, norm " + limit + " from the last progress): " + pilot.State + " sel " + v.Sel + " target " + v.Target + " drags " + pilot.Drags + " clicks " + pilot.Clicks); }
      if (workMs > limit && pilot.State == "work") {
        long ms = workMs; workMs = 0; filterMs = 0; lastTickAt = 0;
        return NavStep.Reset("item_limit " + ms + " ms (norm " + limit + "): " + pilot.State + " sel " + v.Sel + " target " + v.Target + " live " + (v.LiveGrid ? (v.LiveTarget ? "target" : "no target cell") : "none") + " drags " + pilot.Drags + " clicks " + pilot.Clicks + (pilot.Why != "" ? " why " + pilot.Why : ""));
      }
      mark.Note(v.ListSig + "|" + (int)v.Row1Top + "|" + v.Sel + "|" + v.SelRow + "|" + v.SelCol + "|" + v.SlotOpen + "|" + v.TipShown + "|" + pilot.Drags + "|" + pilot.State, now);
      if (v.ListSig != lastSig) { lastSig = v.ListSig; sigAt = now; }
      if (filter != null && filter.ActedKey == key) {
        if (now - sigAt < ListSettleMs) {
          if (settleFrom == 0) settleFrom = now;
          if (now - settleFrom > ListSettleCapMs) { settleFrom = 0; return NavStep.Reset("the item list does not settle after the filter"); }
          lastWasFilter = true; return NavStep.Wait("list settling");
        }
        settleFrom = 0;
      }
      // the filter FIRST (it closes the item's card, and a list of 700 rows takes a dozen drags): the item is not at hand - no live cell of it, nothing selected - and the filter pilot is asked whatever the guide's kind says;
      // what it answers (a click, a wait) is this tick's step, no pick or drag is started under it. Nothing to do for it (null) = the pick goes on.
      if (filter != null && v.Sel != v.Target && !(v.LiveGrid && v.LiveTarget)) {
        var fs = filter.Step(s, t, io, now);
        if (fs != null) { lastWasFilter = true; return fs; }
      }
      var a = pilot.Step(v);
      if (OnStep != null) OnStep(v, a);
      if (a.Kind == AutoKind.None) return NavStep.Wait(pilot.State);
      var tg = targetOf(v, a);
      var act = NavAdapt.ToAct(a, tg, t.Owner, "Form_CharactorMain", Keep, "item: " + pilot.State);
      act.OnNotSent = pilot.NotSent;
      var cb = OnSent; var aa = a; if (cb != null) act.OnSent = () => cb(aa);
      return NavStep.Of(act, "act:item");
    }
  }
}
