// RealmForge extractor - equip helper: what to tell the player next. Pure logic, tested without the game.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum GuideKind {
    GameClosed,        // the game has exited
    OpenHero,          // open the plan's hero -> gear
    OpenSlot,          // press the slot of the next item
    Pick,              // the item is in the list: row / position
    HiddenEquipped,    // the item is on another hero and the list hides worn items
    HiddenEnhanced,    // the list hides enhanced items
    HiddenFilter,      // a set / stat / level / quality filter is on
    NotInList,         // not in the list for another reason (sold, stale data): sync again
    Done               // every item of the plan is on the hero
  }

  public enum ItemState { Waiting, Current, Done }

  public sealed class GuideStep {
    public GuideKind Kind;
    public PlanItem Item;                 // the item the step is about (null for GameClosed/OpenHero/Done)
    public int Row, Col;                  // Pick: 1-based
    public string OtherHero;              // HiddenEquipped: who wears it now (name from the plan, may be null)
    public Dictionary<long, ItemState> States = new Dictionary<long, ItemState>();
    public int DoneCount;
  }

  public static class EquipGuide {
    // Is the item on the plan's hero now? Unknown owner (table not found) counts as "not yet".
    public static bool IsOn(Plan p, EquipLive s, PlanItem it) {
      long owner; return s.Owner.TryGetValue(it.Uid, out owner) && owner == p.HeroUid;
    }

    public static GuideStep Next(Plan p, EquipLive s) {
      var g = new GuideStep();
      PlanItem next = null;
      foreach (var it in p.Items) {
        bool on = IsOn(p, s, it);
        if (on) g.DoneCount++;
        g.States[it.Uid] = on ? ItemState.Done : ItemState.Waiting;
        if (!on && next == null) next = it;
      }
      if (next != null) g.States[next.Uid] = ItemState.Current;
      if (!s.GameRunning) { g.Kind = GuideKind.GameClosed; return g; }
      if (next == null) { g.Kind = GuideKind.Done; return g; }
      g.Item = next;
      if (s.HeroUid != p.HeroUid) { g.Kind = GuideKind.OpenHero; return g; }
      if (!s.PanelOk || s.Part != next.Slot) { g.Kind = GuideKind.OpenSlot; return g; }

      int row;
      if (s.Row.TryGetValue(next.Uid, out row)) {
        int col; s.Col.TryGetValue(next.Uid, out col);
        g.Kind = GuideKind.Pick; g.Row = row; g.Col = col;
        return g;
      }
      long owner;
      if (!s.Owner.TryGetValue(next.Uid, out owner)) owner = next.FromHeroUid;
      if (owner > 0 && owner != p.HeroUid && s.HideEquipped) {
        g.Kind = GuideKind.HiddenEquipped;
        g.OtherHero = owner == next.FromHeroUid ? next.FromHeroName : null;
        return g;
      }
      if (s.HideEnhanced && next.Level > 0) { g.Kind = GuideKind.HiddenEnhanced; return g; }
      if (s.FilterActive) { g.Kind = GuideKind.HiddenFilter; return g; }
      g.Kind = GuideKind.NotInList;
      return g;
    }

    // Plan for the hero open in the game, else the current choice (index into plans), else the first.
    public static int PickPlan(List<Plan> plans, EquipLive s, int current) {
      if (plans == null || plans.Count == 0) return -1;
      if (s != null && s.HeroUid > 0) for (int i = 0; i < plans.Count; i++) if (plans[i].HeroUid == s.HeroUid) return i;
      return current >= 0 && current < plans.Count ? current : 0;
    }

    public static string ItemLine(PlanItem it) {
      string s = it.Name;
      if (it.Level > 0) s += " +" + it.Level;
      if (!string.IsNullOrEmpty(it.MainStat)) s += " · " + it.MainStat;
      return s;
    }
  }
}
