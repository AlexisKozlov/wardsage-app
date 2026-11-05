// Tests of src/TipMem.cs (package P4) (docs/specs/2026-11-05-work-packages.md): the item card's button kind, the item and the button's activity from the game's memory,
// and the equip pop-up from the window table. FakeLua tables of the forms; the fields are cleared between items in the real game (select=- in 11 of 19 lines of live 539).
using System;
using RealmForge;

static partial class CoreTests {
  static void TipMemTests() {
    Console.WriteLine("Item card and equip pop-up from memory (src/TipMem.cs):");
    // the pure part
    Check(TipMem.KindOf(true, true, false) == TipKind.Replace && TipMem.KindOf(true, false, false) == TipKind.Wear && TipMem.KindOf(false, false, true) == TipKind.Wear, "tip kind: select+current = Replace, select alone / one card = Wear");
    Check(TipMem.KindOf(false, false, false) == TipKind.Unknown && TipMem.KindOf(false, true, false) == TipKind.Unknown, "tip kind: cleared fields and a lone current item = Unknown, not Wear");
    Check(TipMem.ViewOf(true, true) == TipView.Select && TipMem.ViewOf(false, true) == TipView.One && TipMem.ViewOf(false, false) == TipView.None, "tip view: select first, then the one-card view");
    var m = new FakeLua(4 << 20); RFX.TestMemory = m; RFX.ForgetLive(); RFX.ForgetClasses();
    try {
      var fu = new FakeUnity(m); var st = new FakeState(m);
      double W = 1280, H = 720, U = Ui.Unit(W, H);
      var full = new[] { 0.0, 0 }; var one = new[] { 1.0, 1 }; var half = new[] { 0.5, 0.5 };
      ulong g0, gA, gB;
      ulong root = fu.Rect(0, full, one, full, half, full, 1, out g0);
      ulong a = fu.Rect(root, full, one, full, half, full, 1, out gA);
      ulong b = fu.Rect(a, new[] { 0.0, 1 }, new[] { 0.0, 1 }, new[] { 200.0, 80 }, new[] { 0.0, 1 }, new[] { 100.0, -40 }, 1, out gB);
      int iGo; ulong csObj, csGo;
      ulong csui = fu.UiBase(g0, out csObj, out csGo), btnGo = fu.Obj("UnityEngine", "GameObject", gB, out iGo);
      Func<bool, FTab> Info = (withUid) => withUid ? st.T("iItemUid", 7001L) : st.T("iItemId", 1L);
      // a tip form: any of select / current / one set or nil, the view tables with m_RightBtn (the button's GameObject)
      Func<string, bool, FTab, FTab, FTab, FTab> Form = (cls, active, sel, cur, onec) => {
        var rb = st.T("m_gameObject", 0L);
        SetField(st, rb, "m_gameObject", btnGo, 0x47);
        var view = st.T("m_RightBtn", rb);
        var kv = new System.Collections.Generic.List<object> { "class", st.T("__cname", cls), "m_LuaLogicActive", active, "m_csui", 0L, "m_SelectEquipView", view, "m_OneEquipView", view };
        if (sel.A != 0) { kv.Add("m_SelectEquipInfo"); kv.Add(sel); }
        if (cur.A != 0) { kv.Add("m_CurrentEquipInfo"); kv.Add(cur); }
        if (onec.A != 0) { kv.Add("m_OneEquipInfo"); kv.Add(onec); }
        var f = st.T(kv.ToArray()); SetField(st, f, "m_csui", csui, 0x47); return f;
      };
      var none = new FTab(0);
      Func<FTab, TipState> Read = f => RFX.ReadTipMemIn(fu.Env, st.T(1L, f).A, W, H, U);
      var s1 = Read(Form("Form_CharactorEquipTip", true, Info(true), none, none));
      Check(s1.Shown && s1.Kind == TipKind.Wear && s1.SelectUid == 7001 && s1.View == TipView.Select && s1.Clickable, "empty slot: one card, Wear, the item's uid, the button clickable (" + s1.Why + ")");
      var s2 = Read(Form("Form_CharactorEquipTip", true, Info(true), Info(false), none));
      Check(s2.Kind == TipKind.Replace && s2.SelectUid == 7001 && s2.View == TipView.Select, "occupied slot / item swap: two cards, Replace");
      var s3 = Read(Form("Form_CharactorEquipTip", true, none, none, Info(true)));
      Check(s3.Kind == TipKind.Wear && s3.View == TipView.One && s3.SelectUid == 7001 && s3.Clickable, "the one-card view: Wear, view One, the uid of m_OneEquipInfo");
      var s4 = Read(Form("Form_CharactorEquipTip", true, none, none, none));
      Check(s4.Shown && s4.Kind == TipKind.Unknown && s4.SelectUid == 0 && s4.View == TipView.None && !s4.Clickable && s4.Why == "fields cleared", "select=-: Unknown, never Wear (" + s4.Why + ")");
      var s5 = Read(Form("Form_CharactorEquipTip", true, none, Info(false), none));
      Check(s5.Kind == TipKind.Unknown, "a current item with no select (the fields half cleared): Unknown");
      var s6 = Read(Form("Form_CharactorEquipTip", false, Info(true), none, none));
      Check(!s6.Shown && s6.Kind == TipKind.Unknown && s6.Why == "form hidden", "the form not shown: not shown, Unknown");
      System.Threading.Thread.Sleep(600);   // (a form read last within 500 ms stands in for a missing one)
      var s7 = Read(Form("Form_Other", true, Info(true), none, none));
      Check(!s7.Shown && s7.Why == "form absent", "another form only: absent");
      // the second read of the item changes the kind at once (the fields rewritten): a new table, a new answer
      var f8 = Form("Form_CharactorEquipTip", true, Info(true), none, none); var ui8 = st.T(1L, f8);
      Check(RFX.ReadTipMemIn(fu.Env, ui8.A, W, H, U).Kind == TipKind.Wear, "item A: Wear");
      ulong nd = st.ValueAt(f8, "m_SelectEquipInfo"); m.W32(nd + 8, 0);   // the game clears the field (nil)
      var s8 = RFX.ReadTipMemIn(fu.Env, ui8.A, W, H, U);
      Check(s8.Kind == TipKind.Unknown && s8.SelectUid == 0, "the field cleared between items: Unknown on the next read");
      // the button's activity: its GameObject off in the hierarchy
      var f9 = Form("Form_CharactorEquipTip", true, Info(true), Info(false), none); var ui9 = st.T(1L, f9);
      m.Mem[gB - FakeLua.Base + 0x57] = 0;
      Check(!RFX.ReadTipMemIn(fu.Env, ui9.A, W, H, U).Clickable, "the button's object inactive: not clickable");
      m.Mem[gB - FakeLua.Base + 0x57] = 1;
      Check(RFX.ReadTipMemIn(fu.Env, ui9.A, W, H, U).Clickable, "the button's object active again: clickable");
      // Form_EquipPopUp from the window table
      string why;
      Check(!RFX.ReadEquipPopupIn(fu.Env, st.T(1L, Form("Form_CharactorEquipTip", true, Info(true), none, none)).A, out why) && why == "form absent", "pop-up: not in the window table: closed (" + why + ")");
      Check(RFX.ReadEquipPopupIn(fu.Env, st.T(1L, Form("Form_CharactorEquipTip", true, Info(true), none, none), 2L, Form("Form_EquipPopUp", true, none, none, none)).A, out why), "pop-up: shown in the window table");
      Check(!RFX.ReadEquipPopupIn(fu.Env, st.T(1L, Form("Form_EquipPopUp", false, none, none, none)).A, out why) && why == "form hidden", "pop-up: built but hidden: closed (" + why + ")");
    } finally { RFX.TestMemory = null; RFX.ForgetLive(); RFX.ForgetClasses(); }
  }
}
