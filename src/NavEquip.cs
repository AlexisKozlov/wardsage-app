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
  }

  /// <summary>An action's progress mark (IAtTargetAction.ProgressAt): the time its signature last changed.</summary>
  sealed class ProgressMark {
    string sig = ""; public long At;
    public void Note(string s, long now) { if (s != sig) { sig = s; At = now; } }
  }

  static class NavAdapt {
    public static Act ToAct(AutoAction a, ClickTarget target, ClickOwner owner, string baseForm, ICollection<string> keep, string why) {
      return new Act { Owner = owner, Target = target, Kind = a.Kind == AutoKind.Drag ? ActKind.Drag : ActKind.Click, X = a.X, Y = a.Y, DY = a.DY, Cell = a.Cell,
        Base = baseForm, Keep = keep, Fire = true, Why = why };
    }
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
    public ItemPickAction(AutoPilot p, Func<Snapshot, NavTarget, long, AutoView> v, Func<AutoView, AutoAction, ClickTarget> targetOf, GearFilterAction filter = null) { pilot = p; view = v; this.targetOf = targetOf; this.filter = filter; }
    public string Name { get { return "item"; } }
    public ICollection<string> Keep { get { return filter != null && filter.ActedKey == key ? NavKeep.ItemPick : NavKeep.ItemPickNoFilter; } }
    public bool Wants(Snapshot s, NavTarget t) { key = t.Key(); return true; }
    public void Reset() { pilot.Reset(); }
    readonly ProgressMark mark = new ProgressMark();
    public long ProgressAt { get { return mark.At; } }
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) {
      var v = view(s, t, now); v.NavOwned = true;
      mark.Note(v.ListSig + "|" + (int)v.Row1Top + "|" + v.Sel + "|" + v.SelRow + "|" + v.SelCol + "|" + v.SlotOpen + "|" + v.TipShown + "|" + pilot.Drags + "|" + pilot.State, now);
      if (v.ListSig != lastSig) { lastSig = v.ListSig; sigAt = now; }
      if (filter != null && filter.ActedKey == key) {
        if (now - sigAt < ListSettleMs) {
          if (settleFrom == 0) settleFrom = now;
          if (now - settleFrom > ListSettleCapMs) { settleFrom = 0; return NavStep.Reset("the item list does not settle after the filter"); }
          return NavStep.Wait("list settling");
        }
        settleFrom = 0;
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
