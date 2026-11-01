// Wardsage - the navigator's one screen reader (docs/specs/navigator.md 3.1).
//
// A Snapshot wraps a ScreenState: the base screen, the layers above it with their roles (a pop-up, an offer, a pilot's flow form, a panel that is
// not a form), the confidence of the read and how long the picture has been the same. ScreenReader.Update debounces it: anything that makes a click
// less safe (a new layer, a dangerous or waiting form, the screen unknown, a worse read) takes effect at once, anything that makes it safer needs
// StableMs and two reads of the same signature.
//
// Pure logic, no Windows APIs (tests: tests/NavTests.cs).
using System;
using System.Collections.Generic;
using System.Text;

namespace RealmForge {
  public enum LayerRole { Base, Flow, Panel, Popup, Offer, Danger, Wait, Overlay, Unknown }
  public enum NavConfidence { None, Held, Partial, NoOrder, Sure }

  public sealed class Layer {
    /// <summary>"Form_X", or a pseudo-layer: "panel:heroFilter" | "panel:gearFilter" | "panel:gearSide".</summary>
    public string Form;
    public char Class;
    public LayerRole Role;
    public override string ToString() { return Form + ":" + Role; }
  }

  public sealed class Snapshot {
    public ScreenState State;
    public ScreenKind Kind;
    /// <summary>ScreenState.OwnForm(Kind), or the top full-screen form of an unknown screen; "" = none.</summary>
    public string BaseForm = "";
    /// <summary>Everything above the base, the top one first (the base itself is not in it).</summary>
    public readonly List<Layer> Stack = new List<Layer>();
    public List<string> Forms;
    public NavConfidence Confidence;
    public long ReadAtMs, StableSinceMs;
    /// <summary>How long the signature must stay the same before the snapshot may be acted on (ScreenReader.StableMs).</summary>
    public int NeedMs = 250;
    public string Signature = "";
    /// <summary>The signature without the layers: the screen and its facets (a pop-up that flickers is no progress).</summary>
    public string StateKey = "";
    public bool Blocking;
    public long StableForMs(long now) { return now - StableSinceMs; }
    public bool Stable(long now) { return now - StableSinceMs >= NeedMs; }
    public Layer Top { get { return Stack.Count > 0 ? Stack[0] : null; } }
    public bool Has(string form) { foreach (var l in Stack) if (l.Form == form) return true; return false; }
    public bool HasTip() { foreach (var l in Stack) if (Array.IndexOf(ClickGate.TipForms, l.Form) >= 0) return true; return false; }
    /// <summary>The layers as a joined list (the fresh check compares these).</summary>
    public string LayerKey() {
      var sb = new StringBuilder(BaseForm);
      foreach (var l in Stack) sb.Append('>').Append(l.Form);
      return sb.ToString();
    }
    public override string ToString() { return Signature; }
  }

  public sealed class ScreenReader {
    /// <summary>Adaptive: max(250, 1.5 * the observed read gap p50), capped at 800 (Observe).</summary>
    public int StableMs = 250;
    public Snapshot Current { get; private set; }

    Snapshot raw;
    string candSig = ""; long candSince; int candReads;
    readonly List<long> gaps = new List<long>(); long lastReadAt = -1;

    /// <summary>The time between two reads of the host (its tick); the stable time follows the slow ones.</summary>
    void Observe(long now) {
      if (lastReadAt >= 0 && now > lastReadAt) {
        gaps.Add(now - lastReadAt); if (gaps.Count > 9) gaps.RemoveAt(0);
        var s = new List<long>(gaps); s.Sort();
        long p50 = s[s.Count / 2];
        StableMs = (int)Math.Max(250, Math.Min(800, p50 * 3 / 2));
      }
      lastReadAt = now;
    }

    /// <summary>Forget everything (a new run).</summary>
    public void Reset() { Current = null; raw = null; candSig = ""; candReads = 0; gaps.Clear(); lastReadAt = -1; }

    public Snapshot Update(ScreenState s, long now) { return Update(s, now, null); }

    /// <summary>One host tick: the tick's ScreenState (ScreenTracker.Current) and the pictures (null = none).</summary>
    public Snapshot Update(ScreenState s, long now, Func<string, bool> pic) {
      Observe(now);
      var r = Build(s, now, pic); r.NeedMs = StableMs;
      raw = r;
      var cur = Current;
      if (cur == null) { Current = r; candReads = 0; return r; }
      if (r.Signature == cur.Signature) {   // the same picture: the time it has stood goes on, the read quality is the latest
        r.StableSinceMs = cur.StableSinceMs; Current = r; candReads = 0; candSig = ""; return r;
      }
      if (LessSafe(cur, r)) { Current = r; candReads = 0; candSig = ""; return r; }
      // a safer picture (a layer gone, a screen arrived): two reads the same, StableMs apart, before it is taken
      if (r.Signature == candSig) candReads++; else { candSig = r.Signature; candSince = now; candReads = 1; }
      if (candReads >= 2 && now - candSince >= StableMs) {
        r.StableSinceMs = candSince; Current = r; candReads = 0; candSig = ""; return r;
      }
      // not yet: the old picture stands, with the new read's quality (and its state, so the latest facts are there)
      cur.Confidence = r.Confidence; cur.NeedMs = StableMs; cur.StableSinceMs = now;   // (a read that differs: the picture is in flux, nothing is acted on until it stands still)
      return cur;
    }

    /// <summary>Does the new picture make a click less safe than the old one?</summary>
    static bool LessSafe(Snapshot old, Snapshot nw) {
      if (nw.Confidence < old.Confidence) return true;
      if (nw.Kind == ScreenKind.Unknown && old.Kind != ScreenKind.Unknown) return true;
      var have = new HashSet<string>(); foreach (var l in old.Stack) have.Add(l.Form);
      foreach (var l in nw.Stack) {
        if (!have.Contains(l.Form)) return true;                                    // a new layer
        if (l.Role == LayerRole.Danger || l.Role == LayerRole.Wait) { /* (already there) */ }
      }
      return false;
    }

    static string FirstFull(ScreenState s) {
      if (s.Order != null) foreach (var n in s.Order) if (ScreenMap.ClassOf(n) == 'F' && !OfferForms.IsOffer(n)) return n;
      if (s.Top != null && s.Top != "" && s.TopClass == 'F' && !OfferForms.IsOffer(s.Top)) return s.Top;
      if (!string.IsNullOrEmpty(s.TopForm)) foreach (var n in s.TopForm.Split(',')) if (ScreenMap.ClassOf(n.Trim()) == 'F') return n.Trim();
      return "";
    }

    static void AddUnique(List<string> l, string n) { if (!string.IsNullOrEmpty(n) && !l.Contains(n)) l.Add(n); }

    /// <summary>Pure: roles from the order, the screen map, the flow, the offers and the modal's reason. <paramref name="pic"/>: the host's
    /// pictures ("heroFilter", "gearFilterMain", "gearFilterSide"), null = none.</summary>
    public static Snapshot Build(ScreenState s, long now, Func<string, bool> pic) {
      var sn = new Snapshot { State = s, ReadAtMs = now, StableSinceMs = now };
      if (s == null) { sn.Kind = ScreenKind.Unknown; sn.Confidence = NavConfidence.None; sn.Signature = "none"; return sn; }
      sn.Kind = s.Kind; sn.Forms = s.Order;
      sn.BaseForm = s.Kind == ScreenKind.Unknown ? FirstFull(s) : ScreenState.OwnForm(s.Kind);
      sn.Confidence = s.Held ? NavConfidence.Held : s.Partial ? NavConfidence.Partial : s.Order == null ? NavConfidence.NoOrder : NavConfidence.Sure;
      // the forms above the base, top first
      var names = new List<string>();
      var flows = new HashSet<string>(); if (!string.IsNullOrEmpty(s.Flow)) foreach (var n in s.Flow.Split(',')) if (n.Trim() != "") flows.Add(n.Trim());
      var modal = new HashSet<string>();
      if (s.ModalOpen && s.ModalWhy != null && s.ModalWhy.StartsWith("form:")) foreach (var n in s.ModalWhy.Substring(5).Split(',')) if (n.Trim() != "") modal.Add(n.Trim());
      bool ordered = s.Order != null && (sn.BaseForm == "" || s.Order.Contains(sn.BaseForm));
      if (ordered) {
        foreach (var n in s.Order) { if (n == sn.BaseForm) break; AddUnique(names, n); }
      } else {
        if (s.OfferCount > 0) AddUnique(names, s.Offer);
        foreach (var n in modal) AddUnique(names, n);
        foreach (var n in flows) AddUnique(names, n);
        if (s.Kind == ScreenKind.Unknown && !string.IsNullOrEmpty(s.TopForm)) foreach (var n in s.TopForm.Split(',')) if (n.Trim() != sn.BaseForm) AddUnique(names, n.Trim());
      }
      foreach (var n in names) {
        char c = ScreenMap.ClassOf(n); var role = LayerRole.Popup;
        if (OfferForms.IsOffer(n)) role = LayerRole.Offer;
        else if (flows.Contains(n) || ScreenMap.IsFlow(s.Kind, n)) role = LayerRole.Flow;
        else if (c == 'O') continue;
        else if (c == 'D') role = LayerRole.Danger;
        else if (c == 'W') role = LayerRole.Wait;
        else if (ScreenState.KindOfForm(n) != ScreenKind.Unknown) { if (ScreenState.Rank(ScreenState.KindOfForm(n)) <= ScreenState.Rank(s.Kind)) continue; role = LayerRole.Popup; }
        else if (c == 'P' || c == 'E' || c == 'F') role = LayerRole.Popup;
        else if (c == 'K') continue;
        else {
          // nobody knows it: a layer only when the screen's own read counted it as a pop-up (a part the screen builds late is none)
          if (!modal.Contains(n) && !(s.Kind == ScreenKind.Unknown && !string.IsNullOrEmpty(s.TopForm) && Array.IndexOf(s.TopForm.Split(','), n) >= 0)) continue;
          role = LayerRole.Unknown;
        }
        sn.Stack.Add(new Layer { Form = n, Class = c, Role = role });
      }
      // the panels that are not forms: after the real pop-ups, over the pilot's flow forms
      if (s.Kind == ScreenKind.HeroScreen) {
        bool filterForm = sn.Has("Form_EquipFilterWidget");
        var pseudo = new List<Layer>();
        if (pic != null && pic("gearFilterSide")) pseudo.Add(new Layer { Form = "panel:gearSide", Class = 'P', Role = LayerRole.Panel });
        if (!filterForm && pic != null && pic("gearFilterMain")) pseudo.Add(new Layer { Form = "panel:gearFilter", Class = 'P', Role = LayerRole.Panel });
        if (s.ModalWhy == "heroFilter" || (pic != null && pic("heroFilter"))) pseudo.Add(new Layer { Form = "panel:heroFilter", Class = 'P', Role = LayerRole.Panel });
        int at = 0; while (at < sn.Stack.Count && sn.Stack[at].Role != LayerRole.Flow) at++;
        sn.Stack.InsertRange(at, pseudo);
      }
      var sb = new StringBuilder();
      sb.Append(sn.Kind).Append('|').Append(sn.BaseForm).Append('|');
      foreach (var l in sn.Stack) sb.Append(l).Append(',');
      string head = sb.ToString(); int cut = head.IndexOf('|', head.IndexOf('|') + 1);
      sb.Append('|').Append(s.HeroTab).Append('/').Append(s.HeroSlot).Append('/').Append(s.Hero).Append('/').Append(s.InvEquipTab ? 1 : 0).Append(s.InvArtTab ? 1 : 0).Append(s.InvBulk ? 1 : 0)
        .Append('/').Append(s.FoundryTab).Append('/').Append(s.ArtWsTab);
      sn.Signature = sb.ToString();
      sn.StateKey = sn.Kind + "|" + sn.BaseForm + sn.Signature.Substring(sn.Signature.LastIndexOf('|'));
      return sn;
    }
  }
}
