// Wardsage - the navigator's pop-up handler (docs/specs/navigator.md 3.3): one place that closes every layer above the base that the step does not keep.
// Data first (the fixed recipes), then the offer rule, then the screen map's exit, then the unknown ladder. Every close goes through the existing
// click targets and the gate's own due checks and caps (no new gate rules).
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum CloseHow { None, MapClose, OfferClose, Escape, GraphClose, Funnel, RedX, TipEscape, UnknownBack, Wait, Stop }

  public sealed class CloseRecipe {
    public CloseHow How; public string Field = ""; public ClickTarget Target; public string Why = "";
  }

  public static class ModalHandler {
    static readonly HeroGeometry Hg = new HeroGeometry();
    static readonly FilterGeometry Fg = new FilterGeometry();
    public const int TipStillMs = 600, TipAgainMs = 3000, TipPassMs = 4000, FunnelAgainMs = 1200, RedXAgainMs = 900;

    /// <summary>The fixed table (hand-written, small) and the offer rule; everything else is None (the screen map decides in Next).</summary>
    public static CloseRecipe RecipeOf(Layer l, Snapshot s) {
      switch (l.Form) {
        case "panel:heroFilter": return new CloseRecipe { How = CloseHow.Funnel, Target = ClickTarget.EquipFilter, Why = "hero grid filter: the funnel closes it" };
        case "panel:gearFilter": case "panel:gearSide": case "Form_EquipFilterWidget":
          if (s.Kind != ScreenKind.HeroScreen) break;
          return new CloseRecipe { How = CloseHow.RedX, Target = ClickTarget.EquipClosePopup, Why = "gear filter: its red cross" };
        case "Form_CharactorEquipTip": case "Form_EquipedCompared":
          if (s.Kind != ScreenKind.HeroScreen) break;   // (the tip's Escape is the hero screen's flow rule; elsewhere the screen map decides)
          return new CloseRecipe { How = CloseHow.TipEscape, Target = ClickTarget.EquipTipEscape, Why = "item tip: one Escape" };
      }
      if (l.Role == LayerRole.Offer) return new CloseRecipe { How = CloseHow.OfferClose, Target = ClickTarget.OfferClose, Why = "offer: its cross" };
      return new CloseRecipe { How = CloseHow.None };
    }

    static NavStep GateHalted(INavIo io, ClickOwner owner) { return NavStep.Halt("gate_" + io.Gate.HaltReason(owner)); }

    /// <summary>The next close action for the stack outside <paramref name="keep"/>; Kind None = nothing to close.</summary>
    public static NavStep Next(Snapshot s, ICollection<string> keep, NavMemory m, INavIo io, ClickOwner owner) {
      long now = io.Now; var gate = io.Gate; var st = s.State;
      s.Blocking = false;
      Layer layer = null;
      foreach (var l in s.Stack) { m.Seen(l.Form, now); if (layer == null && (keep == null || !keep.Contains(l.Form))) layer = l; }
      if (layer == null) return NavStep.Nothing;
      s.Blocking = true;
      if (gate.Halted(owner)) return GateHalted(io, owner);
      // an Escape owed by an earlier map exit comes first
      if (gate.EscapeDue(owner, st, now)) return Escape(owner, s, layer, ClickTarget.EscapeBack);
      var rc = RecipeOf(layer, s);
      double W = io.W, H = io.H, U = io.U;
      string closing = layer.Form; string baseForm = s.BaseForm;
      Func<Snapshot, bool> gone = n => !n.Has(closing);
      switch (rc.How) {
        case CloseHow.Funnel: {
          if (!m.Due("funnel", now, FunnelAgainMs)) return NavStep.Wait("funnel just pressed");
          io.Log("nav: modal " + closing + " how=Funnel");
          return NavStep.Of(new Act { Owner = owner, Target = rc.Target, Kind = ActKind.Click, X = Hg.FunnelX * U, Y = H - Hg.FunnelDY * U, Closing = closing, Expect = gone, Why = rc.Why }, "modal:" + closing);
        }
        case CloseHow.RedX: {
          if (!m.Due("redx", now, RedXAgainMs)) return NavStep.Wait("close cross just pressed");
          double fx = layer.Form == "panel:gearSide" ? Fg.SideCloseX : Fg.MainCloseX;
          io.Log("nav: modal " + closing + " how=RedX");
          return NavStep.Of(new Act { Owner = owner, Target = rc.Target, Kind = ActKind.Click, X = Fg.X(fx, W, H), Y = Fg.Y(Fg.CloseY, W, H), Closing = closing, Expect = gone, Why = rc.Why }, "modal:" + closing);
        }
        case CloseHow.TipEscape: {
          if (!ClickGate.OnlyTip(st)) {
            // the gate lets an Escape close the tip only when nothing but the tip is read as a flow form. When that read does not come (or something else is over it) for a few seconds
            // the tip is passed through: the next click beside it closes it (its tap catcher takes the click), as the pilots have always counted on
            if (now - m.Seen(closing, now) >= TipPassMs) return NavStep.Nothing;
            return NavStep.Wait("tip with another form over it");
          }
          if (now - m.Seen(closing, now) < TipStillMs) return NavStep.Wait("tip: waiting to see if it closes by itself");
          if (!m.Due("tip", now, TipAgainMs)) return NavStep.Wait("tip escape just sent");
          io.Log("nav: modal " + closing + " how=TipEscape");
          return NavStep.Of(new Act { Owner = owner, Target = ClickTarget.EquipTipEscape, Kind = ActKind.Escape, Closing = closing, Expect = gone, Why = rc.Why, WantKnown = true }, "modal:" + closing);
        }
        case CloseHow.OfferClose: return Offer(s, layer, m, io, owner);
      }
      return ByMap(s, layer, m, io, owner);
    }

    static NavStep Escape(ClickOwner owner, Snapshot s, Layer layer, ClickTarget target) {
      string closing = layer != null ? layer.Form : ""; string lk = s.LayerKey(); var kind = s.Kind;
      return NavStep.Of(new Act { Owner = owner, Target = target, Kind = ActKind.Escape, Closing = closing, WantKnown = true, Why = "escape",
        Expect = n => closing != "" ? !n.Has(closing) : (n.LayerKey() != lk || n.Kind != kind) }, "modal:escape");
    }

    static NavStep Offer(Snapshot s, Layer layer, NavMemory m, INavIo io, ClickOwner owner) {
      var gate = io.Gate; var st = s.State; long now = io.Now;
      if (st.OfferCount == 0 || st.Offer != layer.Form) return NavStep.Wait("offer not on top yet");
      if (gate.Pending(owner)) return NavStep.Wait("a click's effect is awaited");
      string form = layer.Form; double W = io.W, H = io.H, U = io.U;
      var lr = io.LiveFor(ClickTarget.OfferClose, form);   // (its cross read while it opens)
      if (form != m.OfferLiveForm) { m.OfferLiveForm = form; m.OfferLive.Reset(); }
      bool still = m.OfferLive.Note(lr, now);
      if (!OfferForms.Settled(st, now, still)) return NavStep.Wait("offer still opening");
      var pt = OfferForms.ClosePoint(io.Geo, form, W, H, U);
      if (pt == null) {
        gate.Halt(owner, "offer", "the offer pop-up " + form + (OfferForms.IsOffer(form) ? " has no known close button" : " is not on the list") + " - not clicked");
        return NavStep.Halt("gate_offer");
      }
      var g = io.Geo;
      io.Log("nav: modal " + form + " how=OfferClose");
      return NavStep.Of(new Act { Owner = owner, Target = ClickTarget.OfferClose, Kind = ActKind.Click, X = pt.X, Y = pt.Y, Form = "", Closing = form, Why = "offer: its cross",
        Expect = n => !n.Has(form), Fill = rq => { rq.Form = form; rq.W = W; rq.H = H; rq.U = U; rq.Geo = g; } }, "modal:" + form);
    }

    static NavStep ByMap(Snapshot s, Layer layer, NavMemory m, INavIo io, ClickOwner owner) {
      var gate = io.Gate; var st = s.State; long now = io.Now;
      double W = io.W, H = io.H, U = io.U; string form = layer.Form;
      var d = ClickGate.MapExit(st, W, H, U);
      if (d.Move == MapMove.Back) return NavStep.Wait("screen on top: " + d.Form);
      if (d.Move == MapMove.Wait) {
        long since; if (!m.WaitSince.TryGetValue(form, out since)) { since = now; m.WaitSince[form] = now; }
        if (now - since > Navigator.WaitCapMs) return NavStep.Halt("wait_cap:" + form);
        return NavStep.Wait("wait: " + d.Form + " (" + d.Why + ")");
      }
      if (d.Move != MapMove.None) {
        if (d.Form != form) return NavStep.Wait("the map's top " + d.Form + " is not the layer " + form);
        if (!gate.MapDue(owner, st, now)) return gate.Halted(owner) ? GateHalted(io, owner) : NavStep.Wait("map exit not due");
        if (d.Move == MapMove.Stop) {
          gate.MapStop(owner, d, st);
          return gate.Halted(owner) ? NavStep.Halt("danger:" + form) : NavStep.Wait("danger: retry policy");
        }
        if (d.Move == MapMove.Escape) {
          gate.MapEscape(owner);
          io.Log("nav: modal " + form + " how=Escape");
          return Escape(owner, s, layer, ClickTarget.EscapeBack);
        }
        io.Log("nav: modal " + form + " how=MapClose");
        double px = d.Point.X, py = d.Point.Y;
        return NavStep.Of(new Act { Owner = owner, Target = ClickTarget.MapClose, Kind = ActKind.Click, X = px, Y = py, Closing = form, Why = "pop-up: its exit",
          Expect = n => !n.Has(form), Fill = rq => { rq.Form = form; rq.W = W; rq.H = H; rq.U = U; } }, "modal:" + form);
      }
      if (s.Kind == ScreenKind.Unknown) return NavStep.Nothing;   // the unknown screen's own top form: the planner's unknown-screen back press
      return Ladder(s, layer, m, io, owner);
    }

    public const int UnknownLayerWaitMs = 5000, UnknownLayerHaltMs = 9000;
    /// <summary>A layer nobody has a recipe for: it may be transient (a part the screen builds, a flicker), so it is waited for a few seconds; then one
    /// unknown-screen back press where the gate allows it (the gate's Escape needs a back press before it, so the Escape rung is the gate's own EscapeDue
    /// that follows that press), then a stop with its name. Never a hang.</summary>
    static NavStep Ladder(Snapshot s, Layer layer, NavMemory m, INavIo io, ClickOwner owner) {
      var st = s.State; string form = layer.Form; long now = io.Now;
      int rung; m.Rung.TryGetValue(form, out rung);
      long seen = now - m.Seen(form, now);
      if (m.Blocked("ladder:" + form, now)) return NavStep.Wait("ladder: waiting for the last rung");
      if (rung >= 1) io.Log("nav: unknown-overlay " + form + " over " + s.BaseForm + " rung=b result=stays");
      if (seen < UnknownLayerWaitMs) return NavStep.Wait("unknown layer " + form + " (waiting to see if it goes)");
      if (rung == 0 && ClickGate.UnknownBackBlock(st) == "") {
        if (!io.Gate.UnknownBackDue(owner, st, now)) return NavStep.Wait("unknown back not due");
        m.Rung[form] = 1; m.Hold("ladder:" + form, now + ClickGate.EffectMs);
        double U = io.U, W = io.W, H = io.H; string closing = form;
        return NavStep.Of(new Act { Owner = owner, Target = ClickTarget.UnknownBack, Kind = ActKind.Click, X = Hg.BackX * U, Y = Hg.BackY * U, Closing = closing, WantKnown = true,
          Why = "unknown pop-up: back", Expect = n => !n.Has(closing), Fill = rq => { rq.U = U; rq.W = W; rq.H = H; } }, "modal:" + form);
      }
      if (seen < UnknownLayerHaltMs) return NavStep.Wait("unknown layer " + form);
      m.Rung[form] = 2;
      io.Log("nav: unknown-overlay " + form + " over " + s.BaseForm + " rung=c result=stays");
      return NavStep.Halt("unknown_popup:" + form);
    }
  }
}
