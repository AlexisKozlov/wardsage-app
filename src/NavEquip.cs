// Wardsage - the auto-equip pilots as actions of the navigator (docs/specs/navigator.md 3.5). They wrap the existing pilots and do not rewrite them: the host builds the
// views (it reads the screens), the adapters turn each pilot answer into an Act with the same click target TickAuto used, so the gate's whitelists stay as they are.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public static class NavKeep {
    public static readonly string[] HeroSeek = { "panel:heroFilter" };
    public static readonly string[] GearFilter = { "Form_EquipFilterWidget", "panel:gearFilter", "panel:gearSide", "Form_ArtifactFilter" };
    public static readonly string[] ItemPick = { "Form_EquipFilterWidget", "panel:gearFilter", "panel:gearSide", "Form_ArtifactFilter", "Form_CharactorEquipTip", "Form_EquipedCompared", "Form_EquipPopUp", "Form_EquipSellPopup" };
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
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) {
      var hv = view(s, t, now); hv.NavOwned = true;
      var ha = pilot.Step(hv);
      if (OnStep != null) OnStep(hv, ha);
      if (ha == null) return null;
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
    public GearFilterAction(FilterPilot p, Func<Snapshot, NavTarget, long, FilterView> v, Func<bool> wanted) { pilot = p; view = v; this.wanted = wanted; }
    public string Name { get { return "filter"; } }
    public ICollection<string> Keep { get { return NavKeep.GearFilter; } }
    public bool Wants(Snapshot s, NavTarget t) { return wanted == null || wanted(); }
    public void Reset() { pilot.Reset(); }
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) {
      var fv = view(s, t, now);
      var fa = pilot.Step(fv);
      if (OnStep != null) OnStep(fv, fa);
      if (fa == null) return null;
      if (fa.Kind == AutoKind.None) return NavStep.Wait(pilot.State == "failed" ? "idle" : "work");
      var act = NavAdapt.ToAct(fa, ClickTarget.EquipFilter, t.Owner, "Form_CharactorMain", NavKeep.GearFilter, "filter: " + pilot.State);
      return NavStep.Of(act, "act:filter");
    }
  }

  /// <summary>AutoPilot with NavOwned: the slot is open already (facet:slot); list scroll, anchor, the item click and «Заменить» (the player's auto confirm only).</summary>
  public sealed class ItemPickAction : IAtTargetAction {
    readonly AutoPilot pilot; readonly Func<Snapshot, NavTarget, long, AutoView> view; readonly Func<AutoView, AutoAction, ClickTarget> targetOf;
    public Action<AutoView, AutoAction> OnStep; public Action<AutoAction> OnSent;
    public ItemPickAction(AutoPilot p, Func<Snapshot, NavTarget, long, AutoView> v, Func<AutoView, AutoAction, ClickTarget> targetOf) { pilot = p; view = v; this.targetOf = targetOf; }
    public string Name { get { return "item"; } }
    public ICollection<string> Keep { get { return NavKeep.ItemPick; } }
    public bool Wants(Snapshot s, NavTarget t) { return true; }
    public void Reset() { pilot.Reset(); }
    public NavStep Step(Snapshot s, NavTarget t, INavIo io, long now) {
      var v = view(s, t, now); v.NavOwned = true;
      var a = pilot.Step(v);
      if (OnStep != null) OnStep(v, a);
      if (a.Kind == AutoKind.None) return NavStep.Wait(pilot.State);
      var tg = targetOf(v, a);
      var act = NavAdapt.ToAct(a, tg, t.Owner, "Form_CharactorMain", NavKeep.ItemPick, "item: " + pilot.State);
      act.OnNotSent = pilot.NotSent;
      var cb = OnSent; var aa = a; if (cb != null) act.OnSent = () => cb(aa);
      return NavStep.Of(act, "act:item");
    }
  }
}
