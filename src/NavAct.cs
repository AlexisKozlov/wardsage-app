// Wardsage - the navigator's verified-click primitive (docs/specs/navigator.md 3.2).
//
// An Act is one click, drag or Escape with its own success test. ActClick sends it once (through INavIo.Send, i.e. the click gate), then watches the
// stable snapshots until the test holds (Done), a new layer comes (Diverted), the gate halts, or the time runs out. It never sends twice for one Start.
//
// Pure logic, no Windows APIs.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public enum ActKind { Click, Drag, Escape }
  public enum ActOutcome { Running, Done, NoEffect, Diverted, WrongScreen, Denied, Halted, Timeout, NotSent }

  public sealed class Act {
    public ClickOwner Owner = ClickOwner.Equip;
    public ClickTarget Target;
    public ActKind Kind;
    /// <summary>Live rect source (RFX.UiFieldReadNow(Form, Field)); "" = the point only.</summary>
    public string Form = "", Field = "";
    /// <summary>Client px; DY: a drag's move.</summary>
    public double X, Y, DY;
    public long Cell;
    /// <summary>The edge's own success test on stable snapshots; null = none (with Fire: done once sent).</summary>
    public Func<Snapshot, bool> Expect;
    public int TimeoutMs;
    public string Why = "";
    public Action<ClickRequest> Fill;
    /// <summary>The base form the snapshot must have ("" = any).</summary>
    public string Base = "";
    /// <summary>Layers that may stay above the base while this act runs.</summary>
    public ICollection<string> Keep;
    /// <summary>The layer this act closes: it must be in the stack.</summary>
    public string Closing = "";
    /// <summary>A pilot's action: done once sent (the pilot watches its effect itself); not sent = OnNotSent, nothing counted.</summary>
    public bool Fire;
    /// <summary>A forward edge: needs the window table's order (Sure); exits and closes work without it.</summary>
    public bool Forward;
    /// <summary>The fresh read wants the city's picture / the shown known forms (the host's FreshScreen flags).</summary>
    public bool WantCity, WantKnown;
    public Action OnSent, OnNotSent;
    /// <summary>The edge key for the recovery ladder ("" = none).</summary>
    public string Edge = "";
  }

  public sealed class ActClick {
    public Act Cur { get; private set; }
    public ActOutcome Outcome { get; private set; }
    public string Detail = "";
    public long SentAt { get; private set; }
    /// <summary>The click went out with the item's tip over the screen (passed through): the tip's tap catcher may have taken it - no effect is then no failure of the step.</summary>
    public bool TipAtSend { get; private set; }
    bool sent; long startAt; string layersAtSend = ""; HashSet<string> formsAtSend = new HashSet<string>();

    public bool Active { get { return Cur != null && Outcome == ActOutcome.Running; } }

    public void Start(Act a, long now) { Cur = a; Outcome = ActOutcome.Running; Detail = ""; sent = false; startAt = now; SentAt = 0; }

    /// <summary>The run was paused (the player has the mouse / the game is behind) for <paramref name="dt"/> ms: the click's own clocks stand still.</summary>
    public void Pause(long dt) { startAt += dt; if (SentAt > 0) SentAt += dt; }

    ActOutcome End(ActOutcome o, string why) {
      Outcome = o; Detail = why;
      if (!sent && o != ActOutcome.Done && Cur != null && Cur.OnNotSent != null) Cur.OnNotSent();   // (a pilot's action that did not go out is taken back)
      return o;
    }

    static bool HasBlocking(Snapshot s, ICollection<string> keep) {
      foreach (var l in s.Stack) if (keep == null || !keep.Contains(l.Form)) return true;
      return false;
    }

    public ActOutcome Tick(INavIo io, Snapshot s, long now) {
      if (Cur == null || Outcome != ActOutcome.Running) return Outcome;
      var a = Cur;
      if (io.Gate.Halted(a.Owner)) return End(ActOutcome.Halted, io.Gate.HaltReason(a.Owner));
      if (!sent) return TickBeforeSend(io, s, now);
      // sent: the effect is awaited
      if (s.Confidence >= NavConfidence.NoOrder && !s.State.Held) {
        if (a.Expect != null && s.Stable(now) && a.Expect(s)) return End(ActOutcome.Done, "effect");
        // a close whose layer is gone is done even if the next pop-up of a chain is already there
        if (a.Closing != "" && a.Expect != null && s.Stable(now) && a.Expect(s)) return End(ActOutcome.Done, "closed");
        if (s.Stable(now)) foreach (var l in s.Stack) {   // (a layer that is there for one read - a picture flicker - is no divert)
          if (formsAtSend.Contains(l.Form)) continue;
          if (a.Keep != null && a.Keep.Contains(l.Form)) continue;
          return End(ActOutcome.Diverted, "new layer " + l.Form);
        }
      }
      int to = a.TimeoutMs > 0 ? a.TimeoutMs : (io.Slow ? ClickGate.SlowEffectMs : ClickGate.EffectMs);
      if (a.Expect == null) { if (!io.Gate.Pending(a.Owner)) return End(ActOutcome.Done, "no watch"); }
      if (now - SentAt > to) {
        if (a.Expect == null) return End(ActOutcome.Done, "late");
        return End(io.Gate.Pending(a.Owner) ? ActOutcome.Timeout : ActOutcome.NoEffect, (now - SentAt) + " ms");
      }
      return ActOutcome.Running;
    }

    ActOutcome TickBeforeSend(INavIo io, Snapshot s, long now) {
      var a = Cur;
      if (s.Confidence < NavConfidence.NoOrder || !s.Stable(now)) return NotSent(a, "snapshot not stable");
      if (!io.InputOpen) return NotSent(a, "the game's input is locked");
      if (a.Forward && s.Confidence < NavConfidence.Sure) return NotSent(a, "no order for a forward edge");
      if (a.Base != "" && s.BaseForm != a.Base) return End(ActOutcome.WrongScreen, "base " + s.BaseForm + " != " + a.Base);
      if (a.Closing != "") { if (!s.Has(a.Closing)) return End(ActOutcome.WrongScreen, a.Closing + " is gone"); }
      else if (HasBlocking(s, a.Keep)) return End(ActOutcome.WrongScreen, "a layer is over the base: " + s.Top);
      var fresh = io.Fresh(a.WantCity, a.WantKnown);
      if (fresh == null) return NotSent(a, "no fresh read");
      var fs = ScreenReader.Build(fresh, now, io.Picture);
      if (fs.BaseForm != s.BaseForm || (fresh.Order != null && fs.LayerKey() != s.LayerKey()) || (fresh.Order == null && (fs.Stack.Count == 0) != (s.Stack.Count == 0)))
        return NotSent(a, "the fresh read differs: " + fs.LayerKey() + " vs " + s.LayerKey());
      double x = a.X, y = a.Y;
      if (a.Kind == ActKind.Click && a.Form != "" && a.Field != "") {
        var lr = io.LiveRect(a.Form, a.Field);
        if (lr != null && lr.Length >= 4 && lr[2] > 0 && lr[3] > 0) { x = lr[0] + lr[2] / 2; y = lr[1] + lr[3] / 2; }
      }
      a.X = x; a.Y = y;
      var res = io.Send(a, fresh);
      if (res == null || res.Verdict == GateVerdict.Wait) return NotSent(a, res != null ? res.Reason : "not sent");
      if (res.Verdict == GateVerdict.Deny) {
        if (io.Gate.Halted(a.Owner)) return End(ActOutcome.Halted, io.Gate.HaltReason(a.Owner));
        return End(ActOutcome.Denied, res.Reason + ": " + res.Detail);
      }
      sent = true; SentAt = now; layersAtSend = s.LayerKey();
      formsAtSend = new HashSet<string>(); foreach (var l in s.Stack) formsAtSend.Add(l.Form);
      TipAtSend = s.HasTip() && a.Closing == "";
      if (a.OnSent != null) a.OnSent();
      if (a.Fire) return End(ActOutcome.Done, "sent");
      return ActOutcome.Running;
    }

    ActOutcome NotSent(Act a, string why) { return End(ActOutcome.NotSent, why); }
  }
}
