// Wardsage.exe - the overlay's city reads from memory (package P1) (docs/specs/2026-11-05-work-packages.md).
//
// «Герои» / «Инвентарь» of the city bar and «the city is the screen» as the host decides them: the game's memory first (src/CityMem.cs: the window table's z-order, the button's
// live rect, its Selectable, the input gate), the picture only while memory is Unknown. Each decision is logged once per change with its source ("city decision: heroes=yes src=mem ..."),
// and a picture that contradicts memory for over 1.5 s is told once ("disagree: city heroes mem=.. pix=.. for=..", the same tracker as the hero and gear flags); memory is trusted.
// The call sites of the old pictures (HeroesButtonSeen / InventoryButtonSeen / CityVisible) are switched over to these in a later commit (OverlayNav.Picture, Overlay.cs).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RealmForge {
  sealed partial class OverlayController {
    const int CityMemReuseMs = 80, CityPixCompareMs = 1000;
    CityState cityMemState; long cityMemAt; double cityMemW, cityMemH;
    readonly DisagreeTracker cityDisHeroes = new DisagreeTracker(), cityDisInv = new DisagreeTracker(), cityDisScreen = new DisagreeTracker();
    long cityPixHeroesAt, cityPixInvAt, cityPixScreenAt;
    readonly Dictionary<string, string> citySaid = new Dictionary<string, string>();

    /// <summary>The city from memory, read at most every 80 ms (one read serves every question of a tick); null = no game / no roots.</summary>
    CityState CityMemRead(double W, double H) {
      long now = NowMs();
      if (cityMemAt != 0 && now - cityMemAt < CityMemReuseMs && cityMemW == W && cityMemH == H) return cityMemState;
      List<string> order = null;
      try { if (RFX.UiKnown) order = RFX.ReadShownOrder(); } catch (Exception) { order = null; }
      CityState st = null;
      try { st = RFX.CityRead(order, RFX.NavInputOpen(), W, H, Ui.Unit(W, H)); } catch (Exception e) { Log.Write("city mem: read failed - " + e.Message); }
      cityMemState = st; cityMemAt = now; cityMemW = W; cityMemH = H;
      return st;
    }

    /// <summary>One line when a decision changes (its source and why), never every tick.</summary>
    void CitySay(string what, string word, string src, string why) {
      string key = word + "/" + src + "/" + why, was;
      if (citySaid.TryGetValue(what, out was) && was == key) return;
      citySaid[what] = key;
      Log.Write("city decision: " + what + "=" + word + " src=" + src + (why != "" ? " (" + why + ")" : ""));
    }

    /// <summary>The picture against memory, told only when it lasts (DisagreeTracker); the picture is looked at most once a second and only while the city is settled (on top, input open).</summary>
    void CityCompare(string name, DisagreeTracker dis, ref long at, Mem3 mem, bool settled, Func<bool> picture) {
      long now = NowMs();
      string w = CityMem.Word(mem);
      if (w == null || !settled) { dis.Reset(); return; }
      if (now - at < CityPixCompareMs) return;
      at = now;
      string line = dis.Update(name, w, picture() ? "yes" : "no", now);
      if (line != null) { Log.Write(line); NavNotes.Add(line); }
    }

    /// <summary>«Герои» of the city is there to press: memory first (shown, city on top, input open, Selectable on), the picture only while memory is Unknown.</summary>
    bool HeroesButtonNow(W32.POINT o, double W, double H) {
      var s = CityMemRead(W, H);
      Mem3 m = s != null ? s.Heroes.Clickable : Mem3.Unknown;
      if (m == Mem3.Unknown) {
        bool pix = HeroesButtonSeen(o, W, H);
        CitySay("heroes", pix ? "yes" : "no", "pix", s != null ? (s.Heroes.Why != "" ? s.Heroes.Why : "memory unknown") : "no read");
        cityDisHeroes.Reset();
        return pix;
      }
      CitySay("heroes", CityMem.Word(m), "mem", s.Heroes.Why != "" ? s.Heroes.Why : "shown=" + CityMem.Word(s.Heroes.Shown) + " onTop=" + CityMem.Word(s.OnTop));
      CityCompare("city heroes", cityDisHeroes, ref cityPixHeroesAt, s.Heroes.Shown, s.OnTop == Mem3.Yes && s.InputOpen == true, () => HeroesButtonSeen(o, W, H));
      return m == Mem3.Yes;
    }

    /// <summary>«Инвентарь» of the city is there to press (the same rule); the old picture path (live rect held still, the bar's search) answers only while memory is Unknown.</summary>
    bool InventoryButtonNow(W32.POINT o, double W, double H) {
      var s = CityMemRead(W, H);
      Mem3 m = s != null ? s.Inventory.Clickable : Mem3.Unknown;
      if (m == Mem3.Unknown) {
        bool pix = InventoryButtonSeen(o, W, H);
        CitySay("inventory", pix ? "yes" : "no", "pix", s != null ? (s.Inventory.Why != "" ? s.Inventory.Why : "memory unknown") : "no read");
        cityDisInv.Reset();
        return pix;
      }
      CitySay("inventory", CityMem.Word(m), "mem", s.Inventory.Why != "" ? s.Inventory.Why : "shown=" + CityMem.Word(s.Inventory.Shown) + " onTop=" + CityMem.Word(s.OnTop));
      CityCompare("city inventory", cityDisInv, ref cityPixInvAt, s.Inventory.Shown, s.OnTop == Mem3.Yes && s.InputOpen == true, () => InventoryButtonSeen(o, W, H));
      return m == Mem3.Yes;
    }

    /// <summary>The city is the screen on view: the window table's top form (<paramref name="screens"/>, <paramref name="order"/>); the bar's picture only while the table is not read.</summary>
    bool CityNow(IntPtr hwnd, List<string> screens, List<string> order) {
      Mem3 m = CityMem.CityScreen(screens, order);
      if (m == Mem3.Unknown) {
        bool pix = CityVisible(hwnd);
        CitySay("city", pix ? "yes" : "no", "pix", screens == null ? "window table not read" : "top form not certain");
        cityDisScreen.Reset();
        return pix;
      }
      CitySay("city", CityMem.Word(m), "mem", "forms=" + (screens != null ? screens.Count : 0));
      CityCompare("city screen", cityDisScreen, ref cityPixScreenAt, m, true, () => CityVisible(hwnd));
      return m == Mem3.Yes;
    }
  }
}
