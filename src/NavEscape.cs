// Wardsage - which form the game's Escape closes, predicted from the UI stacks (docs/specs/game-nav-research.md 1.3, 1.4).
//
// The game's back search (CUtil.GetBackspaceClickableUI): for each stack in turn it starts at the top form and walks down; an inactive form is skipped; the
// form's back behaviour Ignore / Follow passes to the one below, Block stops the search (nothing closes), Handle is the target. A target without a back
// delegate swallows the key; the city's own back delegate opens the quit dialog (so Escape on the bare city is never sent).
// Pure logic: the host reads the stacks, the tests pin the walk.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public sealed class StackForm {
    public string Name = ""; public bool Active = true, HasDelegate, Full, LuaBehav; public string Back = "default";
  }

  public sealed class EscapePrediction {
    /// <summary>close | exit-dialog | block | swallowed | none</summary>
    public string Kind = "none"; public string Form = "", Stack = "", Why = "";
    public override string ToString() { return Kind + (Form != "" ? " " + Form : "") + (Stack != "" ? " (" + Stack + ")" : "") + (Why != "" ? " - " + Why : ""); }
  }

  public static class EscapePredictor {
    /// <summary>The stacks in the order the game searches them (the order is the research's best reading of the disassembly: to be confirmed live).</summary>
    public static readonly string[] SearchOrder = { "Top", "Popup", "Flow", "Main" };

    /// <summary>Each stack's forms bottom to top (the top is the last). A stack that is not in the dictionary is empty.
    /// Round 2 (1.6.47 binary): a Block form ends the walk of ITS stack only, the search goes on with the next lower one; StackMain is last, so a Block there means "nothing".
    /// The quit dialog (Form_SystemTips, a pure C# form with no back delegate) shows as a form in StackTop; its name often does not resolve.</summary>
    public static EscapePrediction Predict(IDictionary<string, IList<StackForm>> stacks) {
      IList<StackForm> top;
      if (stacks.TryGetValue("Top", out top) && top != null)
        foreach (var f in top)
          if (f.Active && !f.HasDelegate && (f.Name == "Form_SystemTips" || f.Name.StartsWith("id:", StringComparison.Ordinal)))
            return new EscapePrediction { Kind = "exit-dialog-up", Form = f.Name, Stack = "Top", Why = "the quit dialog is up (a Top form with no back delegate); it handles Escape itself" };
      EscapePrediction block = null;
      foreach (var sn in SearchOrder) {
        IList<StackForm> l; if (!stacks.TryGetValue(sn, out l) || l == null) continue;
        for (int i = l.Count - 1; i >= 0; i--) {
          var f = l[i];
          if (!f.Active) continue;
          string b = f.Back ?? "default";
          if (b == "Ignore" || b == "Follow") continue;
          if (b == "Block") { if (block == null) block = new EscapePrediction { Kind = "block", Form = f.Name, Stack = sn, Why = "Block ends this stack's walk; lower stacks are still searched" }; break; }
          if (b == "default" && f.LuaBehav) return new EscapePrediction { Kind = "unknown", Form = f.Name, Stack = sn, Why = "its Lua GetBackspaceBehav is not known (the form is not named)" };
          if (b == "default" && !f.HasDelegate) continue;   // (the C# default with no back delegate: not a target)
          if (f.Name == "Form_MainCity") return new EscapePrediction { Kind = "exit-dialog", Form = f.Name, Stack = sn, Why = "the city's back opens the quit dialog: never send Escape" };
          if (!f.HasDelegate) return new EscapePrediction { Kind = "swallowed", Form = f.Name, Stack = sn, Why = "Handle with no back delegate swallows Escape" };
          return new EscapePrediction { Kind = "close", Form = f.Name, Stack = sn };
        }
      }
      return block ?? new EscapePrediction { Kind = "none", Why = "no form takes Escape" };
    }
  }
}
