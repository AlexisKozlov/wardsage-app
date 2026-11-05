// Wardsage.exe - the overlay's item tip and equip pop-up reads (package P4) (docs/specs/2026-11-05-work-packages.md): memory first, the picture only when memory has no answer.
using System;
namespace RealmForge {
  sealed partial class OverlayController {
    string artPopupSaid = "";
    long tipUidLogged;

    /// <summary>Form_EquipPopUp («the artifact is on another hero: Replace / Cancel») shown now: the game's window table answers (a form that is absent or hidden is not open); only when the
    /// script root cannot be read does the picture (gold + blue buttons) decide. A change of the memory answer is told once.</summary>
    bool ArtPopupNow(IntPtr hwnd) {
      string why; bool shown = RFX.ReadEquipPopupShown(out why);
      if (why == "no lua root" || why == "read failed") return ArtPopupSeen(hwnd);
      string said = shown ? "shown" : "no";
      if (said != artPopupSaid) { artPopupSaid = said; Log.Write("art popup: memory " + said + (shown ? "" : " (" + why + ")") + " src=mem"); }
      return shown;
    }

    /// <summary>The item card's button now, from memory: 1 = «Заменить», 2 = «Надеть», 0 = no card / fields cleared (Unknown: never taken for «Надеть»). <paramref name="st"/> carries the
    /// item's uid, the view that holds the button and whether the button is active. Told once per item with its source.</summary>
    int TipButtonNow(double W, double H, out TipState st) {
      st = RFX.ReadTipMem(W, H, W > 0 && H > 0 ? Ui.Unit(W, H) : 0);
      if (st.Shown && st.SelectUid != 0 && st.SelectUid != tipUidLogged) {
        tipUidLogged = st.SelectUid;
        Log.Write("tip: item " + st.SelectUid + " button " + (st.Kind == TipKind.Replace ? "replace" : st.Kind == TipKind.Wear ? "equip" : "unknown" + (st.Why != "" ? " (" + st.Why + ")" : "")) + " view " + st.View + (st.Clickable ? " clickable" : " not clickable") + " src=mem");
      }
      return st.KindNo;
    }
  }
}
