// The city screen from the game's memory: Heroes / Inventory buttons and the city on top (package P1) (docs/specs/2026-11-05-work-packages.md).
//
//   CityState { OnTop, Heroes, Inventory }   OnTop   = Form_MainCity is the first form that takes a click (the window table's z-order, ScreenState.OrderCover + offers)
//   CityButton { Shown, Clickable, Rect }    Shown   = the form is shown and the button's GameObject is active in the hierarchy (UiButtonRead: 0x56 / 0x57)
//                                            Clickable = Shown && OnTop && the game's input is open (UILocker) && its Selectable is not switched off
//                                                        (Selectable.m_Interactable 0xD0, m_GroupsAllowInteraction 0xE0) && the rect's centre is inside the client
//
// Every answer is a Mem3: Yes / No from memory, Unknown when memory cannot tell (the table or the form was not read, no Selectable found on the button):
// the host (app/OverlayCity.cs) then asks the picture, and tells a disagreement only when it lasts. A Selectable that was not found is Unknown, never "off":
// the list of the game's enabled Selectables (UnityEngine.UI.Selectable.s_Selectables) holds the enabled ones only, and the button's GameObject may carry none.
// Passive reading only.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RealmForge {
  /// <summary>One city bar button read from memory.</summary>
  public sealed class CityButton {
    /// <summary>The form is shown and the button is active in the hierarchy; Unknown = the read failed (<see cref="Why"/>).</summary>
    public Mem3 Shown = Mem3.Unknown;
    /// <summary>Shown and nothing between the player's click and the button: the city on top, the input open, the Selectable on, the rect inside the client.</summary>
    public Mem3 Clickable = Mem3.Unknown;
    /// <summary>The Selectable on the button (or under it) alone: Unknown = none found.</summary>
    public Mem3 Selectable = Mem3.Unknown;
    /// <summary>The live rect (client px), valid when <see cref="HasRect"/>.</summary>
    public bool HasRect; public double X, Y, W, H;
    /// <summary>What decided, short: for the log line and the unknown cases.</summary>
    public string Why = "";
    public string Line() {
      return Name(Shown) + "/" + Name(Clickable) + (Why != "" ? " (" + Why + ")" : "")
        + (HasRect ? " rect=" + Math.Round(X).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(Y).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(W).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(H).ToString(CultureInfo.InvariantCulture) : "");
    }
    static string Name(Mem3 m) { return m == Mem3.Yes ? "yes" : m == Mem3.No ? "no" : "unknown"; }
  }

  /// <summary>The city from memory: the screen and its two bar buttons.</summary>
  public sealed class CityState {
    public Mem3 OnTop = Mem3.Unknown;
    public CityButton Heroes = new CityButton(), Inventory = new CityButton();
    /// <summary>The input gate (UILocker) as read: true open, false locked, null not read.</summary>
    public bool? InputOpen;
    public int At;
    public string Why = "";
  }

  public static class CityMem {
    public const string Form = "Form_MainCity";
    public const string HeroesField = "m_Button_Heros", InventoryField = "m_Button_Backpack";

    /// <summary>The «Герои» button of the city (a GameObject field of Form_MainCity; its picture child is Image_Icon).</summary>
    public static UiWidget HeroesWidget() { return new UiWidget(Form, HeroesField) { Icon = true, IconChild = "Image_Icon" }; }
    /// <summary>The «Инвентарь» button (the same widget the pilots press: UiWidgetOf(ClickTarget.CityInventory)).</summary>
    public static UiWidget InventoryWidget() { return new UiWidget(Form, InventoryField) { Icon = true, IconChild = "Image_Icon" }; }

    /// <summary>Is Form_MainCity the form on top? <paramref name="order"/> = the shown forms top first (ScreenInputs.Order; null = not read or not certain).
    /// Yes = the city is in the order and no offer, pop-up, other screen, dangerous dialog or wait lies above it (overlays and the city's own flow forms do not count);
    /// No = it is not shown or something lies above; Unknown = no order.</summary>
    public static Mem3 OnTopOf(List<string> order) {
      if (order == null) return Mem3.Unknown;
      int at = order.IndexOf(Form);
      if (at < 0) return Mem3.No;
      for (int i = 0; i < at; i++) if (OfferForms.IsOffer(order[i])) return Mem3.No;
      return ScreenState.OrderCover(order, ScreenKind.City, "") == "" ? Mem3.Yes : Mem3.No;
    }

    /// <summary>The city is the screen: the table's order has it on top (Yes), or a form above it / no city form (No), or the order is not known (Unknown: the picture decides).
    /// A city under an offer is still the screen (the offer is a modal over it), so only a screen or a pop-up of the screen map above it ends it.</summary>
    public static Mem3 CityScreen(List<string> screens, List<string> order) {
      if (screens == null) return Mem3.Unknown;
      if (!screens.Contains(Form)) return Mem3.No;
      if (order == null) return screens.Count == 1 ? Mem3.Yes : Mem3.Unknown;
      return ScreenState.TopKind(screens) == ScreenKind.City ? Mem3.Yes : Mem3.No;
    }

    /// <summary>The rule of <see cref="CityButton.Clickable"/>: No as soon as one link is No (not shown, city not on top, input locked, Selectable off, rect outside);
    /// Yes only when shown, on top and the input is open (a Selectable not found does not block: Unknown there); else Unknown.</summary>
    public static Mem3 Combine(Mem3 shown, Mem3 selectable, Mem3 onTop, bool? inputOpen, bool rectInside, bool hasRect) {
      if (shown == Mem3.No || selectable == Mem3.No || onTop == Mem3.No || inputOpen == false) return Mem3.No;
      if (hasRect && !rectInside) return Mem3.No;
      if (shown == Mem3.Yes && onTop == Mem3.Yes && inputOpen == true && hasRect) return Mem3.Yes;
      return Mem3.Unknown;
    }

    /// <summary>A button from its live read <paramref name="r"/> (null = not read) and the Selectable's answer, on a client of W x H px.</summary>
    public static CityButton Make(UiButtonInfo r, Mem3 selectable, Mem3 onTop, bool? inputOpen, double W, double H) {
      var b = new CityButton { Selectable = selectable };
      if (r == null) { b.Why = "not read"; b.Clickable = Combine(Mem3.Unknown, selectable, onTop, inputOpen, true, false); return b; }
      if (!r.Ok) {
        // the city form is not in the window table / its node is rebuilt: Unknown (the order says whether the city is there at all)
        b.Why = r.FailStep + (string.IsNullOrEmpty(r.FailValue) ? "" : ": " + r.FailValue);
        b.Clickable = onTop == Mem3.No ? Mem3.No : Mem3.Unknown;
        return b;
      }
      b.Shown = r.Visible ? Mem3.Yes : Mem3.No;
      if (r.Visible) { b.HasRect = true; b.X = r.X; b.Y = r.Y; b.W = r.W; b.H = r.H; }
      else b.Why = "hidden";
      bool inside = true;
      if (b.HasRect && W > 0 && H > 0) { double cx = b.X + b.W / 2, cy = b.Y + b.H / 2; inside = cx >= 0 && cx <= W && cy >= 0 && cy <= H; if (!inside) b.Why = "outside the client"; }
      b.Clickable = Combine(b.Shown, selectable, onTop, inputOpen, inside, b.HasRect);
      if (b.Why == "") {
        if (selectable == Mem3.No) b.Why = "not interactable";
        else if (onTop == Mem3.No) b.Why = "city covered";
        else if (inputOpen == false) b.Why = "input locked";
        else if (b.Clickable == Mem3.Unknown) b.Why = onTop == Mem3.Unknown ? "no window order" : inputOpen == null ? "input not read" : "";
      }
      return b;
    }

    /// <summary>The city state from its parts (pure).</summary>
    public static CityState Make(List<string> order, bool? inputOpen, UiButtonInfo heroes, Mem3 heroesSel, UiButtonInfo inventory, Mem3 inventorySel, double W, double H) {
      var s = new CityState { OnTop = OnTopOf(order), InputOpen = inputOpen, At = Environment.TickCount };
      s.Heroes = Make(heroes, heroesSel, s.OnTop, inputOpen, W, H);
      s.Inventory = Make(inventory, inventorySel, s.OnTop, inputOpen, W, H);
      return s;
    }

    /// <summary>The decision word of a Mem3 for the log: "yes" / "no" / null (= not told, the picture decides).</summary>
    public static string Word(Mem3 m) { return m == Mem3.Yes ? "yes" : m == Mem3.No ? "no" : null; }
  }

  public static partial class RFX {
    // UnityEngine.UI.Selectable (dump 1.6.47.841.1, app-offsets.mjs): m_Interactable 0xD0, m_GroupsAllowInteraction 0xE0; the static s_Selectables array (the enabled ones) at the static block's 0
    // and its count at 8 (the NavProbe 'clickables' line reads the same); Component.m_CachedPtr 0x10 of the managed object = the native component
    const int SelInteractable = 0xD0, SelGroups = 0xE0, SelMax = 2500, SelKeepMs = 1500, SelNodes = 24;

    sealed class SelHit { public ulong[] Objs; public int At; }
    static readonly Dictionary<ulong, SelHit> selHits = new Dictionary<ulong, SelHit>();

    /// <summary>Forgets the Selectables found (a new game process, the tests).</summary>
    public static void ClickableReset() { lock (selHits) selHits.Clear(); }

    /// <summary>The game's list of enabled Selectables: the array and its count (false = the class or its static block is not there yet).</summary>
    static bool SelectablesList(ulong ga, out ulong arr, out int count) {
      arr = 0; count = 0;
      ulong k = ClassAt(ga, "UnityEngine.UI.Selectable"); if (k == 0) return false;
      ulong sf = Ptr(k + 0xB8); var sb = sf != 0 ? Read(sf, 0x10) : null; if (sb == null) return false;
      arr = BitConverter.ToUInt64(sb, 0); count = BitConverter.ToInt32(sb, 8);
      return arr != 0 && count >= 0 && count <= 20000;
    }

    /// <summary>The native components of a GameObject and of its descendants (children and their children, a few dozen nodes): a Selectable of the button sits on its GameObject or on a hit-area child.</summary>
    static void NativeComponents(ulong go, HashSet<ulong> into) {
      var q = new List<ulong> { go }; var depth = new List<int> { 0 };
      for (int i = 0; i < q.Count && i < SelNodes; i++) {
        var gb = Read(q[i] + GoComps, 0x18); if (gb == null) continue;
        ulong comps = BitConverter.ToUInt64(gb, 0); long n = BitConverter.ToInt64(gb, 0x10);
        if (comps == 0 || n <= 0 || n > 64) continue;
        ulong tr = 0;
        for (int c = 0; c < n; c++) { ulong comp = Ptr(comps + (ulong)(16 * c) + 8); if (comp == 0) continue; into.Add(comp); if (c == 0) tr = comp; }
        if (depth[i] >= 2 || tr == 0) continue;
        foreach (var kid in Kids(tr)) { ulong kg = Ptr(kid + CompGo); if (kg != 0) { q.Add(kg); depth.Add(depth[i] + 1); } }
      }
    }

    /// <summary>Is the button on the GameObject <paramref name="go"/> clickable by its Selectable: Yes = a Selectable of it (or of a child) is in <paramref name="arr"/> with m_Interactable and
    /// m_GroupsAllowInteraction on; No = every one found is off; Unknown = none found in the list (no Selectable there, switched off, or the list longer than read). The Selectables found are kept
    /// for the next read (checked still in the list and still the same native object), a miss is not searched again within SelKeepMs.</summary>
    public static Mem3 SelectableOf(ulong go, ulong arr, int count, out string why) {
      why = "";
      if (go == 0 || arr == 0 || count < 0) { why = "no list"; return Mem3.Unknown; }
      int n = Math.Min(count, SelMax);
      var ab = n > 0 ? Read(arr + 0x20, n * 8) : new byte[0]; if (ab == null) { why = "list unreadable"; return Mem3.Unknown; }
      var inList = new HashSet<ulong>(); for (int i = 0; i < n; i++) inList.Add(BitConverter.ToUInt64(ab, i * 8));
      int now = Environment.TickCount; SelHit hit;
      lock (selHits) selHits.TryGetValue(go, out hit);
      if (hit != null && hit.Objs != null) {
        bool all = true;
        foreach (var o in hit.Objs) if (!inList.Contains(o) || NativeOfSelectable(o) == 0) { all = false; break; }
        if (all && hit.Objs.Length > 0) return Verdict(hit.Objs, out why);
      }
      else if (hit != null && unchecked(now - hit.At) < SelKeepMs) { why = "no Selectable found"; return Mem3.Unknown; }
      var comps = new HashSet<ulong>(); NativeComponents(go, comps);
      var found = new List<ulong>();
      for (int i = 0; i < n; i++) {
        ulong o = BitConverter.ToUInt64(ab, i * 8); if (o == 0) continue;
        ulong nat = NativeOfSelectable(o);
        if (nat != 0 && comps.Contains(nat)) found.Add(o);
      }
      lock (selHits) selHits[go] = new SelHit { Objs = found.Count > 0 ? found.ToArray() : null, At = now };
      if (found.Count == 0) { why = count > n ? "no Selectable in the first " + n + " of " + count : "no Selectable found"; return Mem3.Unknown; }
      return Verdict(found.ToArray(), out why);
    }

    static ulong NativeOfSelectable(ulong o) { return Ptr(o + CachedPtr); }

    static Mem3 Verdict(ulong[] objs, out string why) {
      why = ""; bool any = false, on = false;
      foreach (var o in objs) {
        var b = Read(o + SelInteractable, SelGroups - SelInteractable + 1); if (b == null) continue;
        byte inter = b[0], grp = b[SelGroups - SelInteractable];
        if (inter > 1 || grp > 1) continue;   // not a bool: the object is not what it was (a pooled slot)
        any = true; if (inter != 0 && grp != 0) on = true;
      }
      if (!any) { why = "Selectable unreadable"; return Mem3.Unknown; }
      return on ? Mem3.Yes : Mem3.No;
    }

    /// <summary>Is the Selectable of the button on the native GameObject <paramref name="go"/> interactable and group-allowed now? The helper the pilots' clickability checks use
    /// (Yes / No from the game's fields, Unknown when it cannot tell). No game: Unknown.</summary>
    public static Mem3 Clickable(ulong go) {
      if (go == 0 || UiNoGame()) return Mem3.Unknown;
      ulong arr; int count;
      if (!SelectablesList(RootGa(), out arr, out count)) return Mem3.Unknown;
      string why; return SelectableOf(go, arr, count, out why);
    }

    /// <summary>The button of a live read (UiButtonRead: its form's table and widget path): its Selectable's answer; Unknown when the widget is a tree path or the walk to its GameObject fails.</summary>
    internal static Mem3 ClickableOfRead(ulong env, UiButtonInfo r, ulong arr, int count, bool listKnown, out string why) {
      why = "";
      if (r == null || !r.Ok || r.FormTable == 0 || string.IsNullOrEmpty(r.Widget) || r.Widget.StartsWith("tree:", StringComparison.Ordinal) || r.Widget.StartsWith("live:", StringComparison.Ordinal)) { why = "no widget field"; return Mem3.Unknown; }
      if (!listKnown) { why = "no Selectable list"; return Mem3.Unknown; }
      ulong v; int tt; string miss;
      if (!LuaPath(r.FormTable, r.Widget, out v, out tt, out miss) || tt != T_UDATA) { why = "widget field"; return Mem3.Unknown; }
      var probe = new UiButtonInfo(); string cls;
      ulong nat = NativeOf(env, v, "widget", probe, out cls); if (nat == 0) { why = "widget native"; return Mem3.Unknown; }
      ulong go = cls == "UnityEngine.GameObject" ? nat : Ptr(nat + CompGo);
      return SelectableOf(go, arr, count, out why);
    }

    /// <summary>The city from memory, read now: the window table's order (<paramref name="order"/>), the input gate, both bar buttons' live rects and Selectables. Null = no game or the Lua root is not reached.</summary>
    public static CityState CityRead(List<string> order, bool? inputOpen, double W, double H, double U) {
      if (UiNoGame()) return null;
      ulong env, ui, ga = 0, arr = 0; int count = 0; string why;
      if (!UiRoots(out env, out ui, out why)) return null;
      bool known = false;
      try { ga = RootGa(); known = SelectablesList(ga, out arr, out count); } catch (Exception) { known = false; }
      return CityReadAt(env, ui, arr, count, known, order, inputOpen, W, H, U);
    }

    /// <summary>The same on given roots and Selectable list (the tests).</summary>
    public static CityState CityReadAt(ulong env, ulong ui, ulong arr, int count, bool listKnown, List<string> order, bool? inputOpen, double W, double H, double U) {
      var hr = UiButtonRead(env, ui, CityMem.HeroesWidget(), W, H, U);
      var ir = UiButtonRead(env, ui, CityMem.InventoryWidget(), W, H, U);
      string w1, w2;
      var hs = ClickableOfRead(env, hr, arr, count, listKnown, out w1);
      var isel = ClickableOfRead(env, ir, arr, count, listKnown, out w2);
      var s = CityMem.Make(order, inputOpen, hr, hs, ir, isel, W, H);
      if (hr != null && hr.Ok && s.Heroes.Why == "" && w1 != "" && hs == Mem3.Unknown) s.Heroes.Why = w1;
      if (ir != null && ir.Ok && s.Inventory.Why == "" && w2 != "" && isel == Mem3.Unknown) s.Inventory.Why = w2;
      return s;
    }
  }
}
