// The item tip's button (Wear / Replace) and the equip pop-up from the game's memory (package P4) (docs/specs/2026-11-05-work-packages.md).
// Form_CharactorEquipTip: m_CurrentEquipInfo nil = «Надеть», a table = «Заменить»; m_SelectEquipInfo.iItemUid = the item the card shows.
// The game clears these fields between items (live 539: select=- in 11 of 19 lines): cleared = Unknown, never «Надеть».
// Form_EquipPopUp («артефакт уже на герое») is a form of the window table (UIInstance), read the same way as every form.
using System;
namespace RealmForge {
  public enum TipKind { Unknown = 0, Replace = 1, Wear = 2 }
  public enum TipView { None = 0, One = 1, Select = 2 }

  /// <summary>One read of the item card: shown, the button's kind, the item's uid (0 = cleared), the view that holds the button, and whether the button is active.</summary>
  public sealed class TipState {
    public bool Shown;
    public TipKind Kind;
    public long SelectUid;
    public TipView View;
    public bool Clickable;
    public string Why = "";
    /// <summary>The old ReadTipKind number: 1 = two cards (Replace), 2 = one card (Wear), 0 = unknown / not shown.</summary>
    public int KindNo { get { return (int)Kind; } }
  }

  public static class TipMem {
    /// <summary>The kind from which of the form's fields are tables. Select (or the one-card view) and no current item = Wear; select and current = Replace;
    /// current without select, or nothing = Unknown (the fields were cleared between items).</summary>
    public static TipKind KindOf(bool sel, bool cur, bool one) {
      if (sel && cur) return TipKind.Replace;
      return (sel || one) && !cur ? TipKind.Wear : TipKind.Unknown;
    }
    public static TipView ViewOf(bool sel, bool one) { return sel ? TipView.Select : one ? TipView.One : TipView.None; }
    /// <summary>The live widget path of the card's own confirm button for a view (the same fields UiButtons.cs reads for EquipConfirm).</summary>
    public static string ButtonField(TipView v) { return v == TipView.One ? "m_OneEquipView.m_RightBtn.m_gameObject" : "m_SelectEquipView.m_RightBtn.m_gameObject"; }
  }

  public static partial class RFX {
    public const string TipForm = "Form_CharactorEquipTip", EquipPopupForm = "Form_EquipPopUp";

    /// <summary>The item card read now (Lua fields only; the button's activity from its GameObject: active in the hierarchy, until RFX.Clickable of package P1 stands in).
    /// W, H, U = the game window's client size and UI unit (0 = a stand-in size: only the activity is used).</summary>
    public static TipState ReadTipMem(double W, double H, double U) {
      string why; ulong env, ui;
      if (!UiRoots(out env, out ui, out why)) return new TipState { Why = "no lua root" };
      return ReadTipMemIn(env, ui, W, H, U);
    }

    /// <summary><see cref="ReadTipMem"/> on a given LuaEnv and UIInstance (the tests' stand-in memory).</summary>
    public static TipState ReadTipMemIn(ulong env, ulong ui, double W, double H, double U) {
      var s = new TipState();
      try {
        bool shown; int copies; var r = new UiButtonInfo();
        ulong f = FormOf(env, ui, TipForm, r, out shown, out copies);
        if (f == 0 || !shown) { s.Why = f == 0 ? "form absent" : "form hidden"; return s; }
        s.Shown = true;
        ulong t, v; int tt, ut;
        bool sel = Field(f, "m_SelectEquipInfo", out t, out tt) && tt == T_TABLE; ulong selT = sel ? t : 0;
        bool cur = Field(f, "m_CurrentEquipInfo", out t, out tt) && tt == T_TABLE;
        bool one = Field(f, "m_OneEquipInfo", out t, out tt) && tt == T_TABLE; ulong oneT = one ? t : 0;
        s.Kind = TipMem.KindOf(sel, cur, one); s.View = TipMem.ViewOf(sel, one);
        ulong card = sel ? selT : oneT;
        if (card != 0 && Field(card, "iItemUid", out v, out ut) && ut == T_INT) s.SelectUid = (long)v;
        if (s.Kind == TipKind.Unknown) s.Why = !sel && !cur && !one ? "fields cleared" : "current without select";
        if (s.View != TipView.None) {
          if (W <= 0 || H <= 0 || U <= 0) { W = 1280; H = 720; U = Ui.Unit(W, H); }
          var b = UiButtonRead(env, ui, new UiWidget(TipForm, TipMem.ButtonField(s.View)), W, H, U);
          s.Clickable = b.Ok && b.Visible;
        }
      } catch (Exception) { s.Why = "read failed"; }
      return s;
    }

    /// <summary>The tip's button kind for the old callers: 1 = Replace, 2 = Wear, 0 = not shown / unknown. Kept in NavProbe.cs as ReadTipKind (a call into <see cref="ReadTipKindMem"/>).</summary>
    internal static int ReadTipKindMem() {
      string why; ulong env, ui; if (!UiRoots(out env, out ui, out why)) return 0;
      bool shown; int copies; var r = new UiButtonInfo();
      ulong f = FormOf(env, ui, TipForm, r, out shown, out copies);
      if (f == 0 || !shown) return 0;
      ulong t; int tt;
      bool sel = Field(f, "m_SelectEquipInfo", out t, out tt) && tt == T_TABLE, cur = Field(f, "m_CurrentEquipInfo", out t, out tt) && tt == T_TABLE, one = Field(f, "m_OneEquipInfo", out t, out tt) && tt == T_TABLE;
      return (int)TipMem.KindOf(sel, cur, one);
    }

    /// <summary>Form_EquipPopUp («the artifact is on a hero already: Replace / Cancel») from the window table: shown with a live C# side.</summary>
    public static bool ReadEquipPopupShown(out string why) {
      string w; ulong env, ui;
      if (!UiRoots(out env, out ui, out w)) { why = "no lua root"; return false; }
      return ReadEquipPopupIn(env, ui, out why);
    }
    public static bool ReadEquipPopupIn(ulong env, ulong ui, out string why) {
      why = "";
      try {
        bool shown; int copies; var r = new UiButtonInfo();
        ulong f = FormOf(env, ui, EquipPopupForm, r, out shown, out copies);
        if (f == 0) { why = "form absent"; return false; }
        if (!shown) { why = "form hidden"; return false; }
        return true;
      } catch (Exception) { why = "read failed"; return false; }
    }
  }
}
