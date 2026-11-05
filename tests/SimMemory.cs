// The game sim's memory model (docs/specs/2026-11-05-work-packages.md, package P6a): a plain model of what the game shows (the hero screen, its filter
// pop-ups, the item card, the equip pop-up, the inventory's cells) written into FakeLua / FakeUnity as the game's script tables and Unity objects,
// so the sim can call the REAL readers (RFX.ReadHeroScreen, ReadTipMemIn, UiCellRead ...) instead of reading its own model. Offsets and table shapes
// are the ones the other fake-memory tests use (FakeLua, FakeUnity, FakeState in CoreTests.cs): nothing new is invented here.
using System;
using System.Collections.Generic;
using RealmForge;

static partial class CoreTests {
  /// <summary>A lazily built panel of a form: no table yet (never built), the prefab loading (the slot holds false), built and closed, built and open.</summary>
  enum SimSlot { Absent, Loading, Closed, Open }
  /// <summary>A form in the window table: not in it, in it but not shown (m_LuaLogicActive nil), shown.</summary>
  enum SimForm { Absent, Hidden, Shown }

  /// <summary>Form_CharactorMain and EquipData as the model has them.</summary>
  sealed class SimHeroModel {
    public SimForm Form = SimForm.Shown;
    public long Hero;                       // EquipData.m_CurrentSelectHeroUid (and the form's m_SelectHeroData.iHeroId)
    public int Tab = 3, Part = -1;          // m_LastType; m_PanelDatas[3] (-1 = not written)
    public bool Small;
    public long[] Grid = new long[0];       // the hero grid in display order
    public bool PanelsNil;                  // m_Panels not created yet
    public SimSlot Filter = SimSlot.Closed; // Charactor_FilterOrder
    public long[] FactionList;              // its faction column (null = not written)
    public int ClassCount = 6;              // its class column's entries
    public long ClassId;                    // the class chosen (0 = «Все»)
    public long[] Camps = new long[0];      // the factions chosen
    public SimSlot Gear = SimSlot.Closed;   // EquipPanel.m_Panel_EquipFilterWidget_Charactor
    public long[] SideStates = new long[3]; // m_ButtonStates[1..3] while the gear pop-up is open
  }

  /// <summary>Form_CharactorEquipTip: which item cards it holds and its confirm button.</summary>
  sealed class SimTipModel {
    public SimForm Form = SimForm.Absent;
    public long Select, One;                // the uid of m_SelectEquipInfo / m_OneEquipInfo (0 = the field is nil)
    public bool Current;                    // m_CurrentEquipInfo is a table (the item worn in the slot)
    public bool ButtonActive = true;
    public double BX = 600, BY = 500, BW = 160, BH = 56;   // the button's rect in client px (x, y of the top left, size)
    public SimForm Popup = SimForm.Absent; // Form_EquipPopUp
  }

  /// <summary>Form_BackpackIntegration: its item grid. The cells the grid has made are the rows that touch the viewport.</summary>
  sealed class SimInvModel {
    public SimForm Form = SimForm.Absent;
    public long[] Uids = new long[0];       // m_FilterAndSortEquipUidList
    public double VX = 300, VY = 200, VW = 550, VH = 300;   // the viewport's rect (client px)
    public int Cols = 5;
    public double Cell = 80, Pitch = 110, Pad = 0, Scroll;  // cell side, step, the content's inner margin, how far the content is scrolled up (px)
    public int Rows { get { return (Uids.Length + Cols - 1) / Cols; } }
    public double CellX(int i) { return VX + Pad + (i % Cols) * Pitch; }
    public double CellY(int i) { return VY - Scroll + Pad + (i / Cols) * Pitch; }
    /// <summary>The cell is made (pooled) when it touches the viewport.</summary>
    public bool Made(int i) { return CellY(i) + Cell > VY && CellY(i) < VY + VH; }
    /// <summary>The picture's rect (client px) of cell i: the frame the game draws, 0.1 of the cell in, 0.8 of it wide.</summary>
    public double[] Frame(int i) { return new[] { CellX(i) + 0.1 * Cell, CellY(i) + 0.1 * Cell, 0.8 * Cell, 0.8 * Cell }; }
  }

  /// <summary>The game's memory of the sim: the models above rewritten by <see cref="Sync"/>, the real readers called through the Read* methods. One world per test; Dispose
  /// takes the stand-in memory out of RFX.</summary>
  sealed class SimMemory : IDisposable {
    public const int Size = 4 << 20, ArenaAt = 2 << 20, ArenaHalf = 512 << 10;
    public readonly FakeLua M = new FakeLua(Size);
    public readonly double W, H, U, K;
    public readonly SimHeroModel Hero = new SimHeroModel();
    public readonly SimTipModel Tip = new SimTipModel();
    public readonly SimInvModel Inv = new SimInvModel();
    public long EquipHero = 0;                // EquipData's hero when no hero model is set
    public EquipAddrs Addrs = new EquipAddrs();
    public ulong Env, UiTable;
    long[] gridWritten; int gridHalf; ulong gridAt;
    readonly RFX.IGameMemory prevMem;

    public SimMemory(double w, double h) {
      W = w; H = h; U = Ui.Unit(w, h); K = U / 640;
      prevMem = RFX.TestMemory; RFX.TestMemory = M; RFX.ForgetLive(); RFX.ForgetClasses(); RFX.UiCellReset();
    }
    public void Dispose() { RFX.TestMemory = prevMem; RFX.ForgetLive(); RFX.ForgetClasses(); }

    // ---------------------------------------------------------------- writing the model

    /// <summary>The hero grid's table, in its own arena: a new table (at the other half) when the list changed, as the game builds a new list when it sorts or filters.</summary>
    ulong GridTable(long[] ids) {
      if (gridWritten != null && SameIds(gridWritten, ids)) return gridAt;
      gridHalf ^= 1; int at = ArenaAt + gridHalf * ArenaHalf;
      Array.Clear(M.Mem, at, ArenaHalf);
      int saveTop = M.Top; M.Top = at; var gs = new FakeState(M);
      var kv = new List<object>(); for (int i = 0; i < ids.Length; i++) { kv.Add((long)(i + 1)); kv.Add(gs.T("iHeroId", ids[i])); }
      gridAt = gs.T(kv.ToArray()).A; M.Top = saveTop; gridWritten = (long[])ids.Clone();
      if (at + ArenaHalf < M.Top) throw new InvalidOperationException("sim memory: the arena overlaps");
      return gridAt;
    }
    static bool SameIds(long[] a, long[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }

    /// <summary>A rect placed by its client-px top left and size under <paramref name="parent"/> (a centred, unrotated parent at the screen's centre).</summary>
    ulong Place(FakeUnity fu, ulong parent, double x, double y, double w, double h, out ulong go) {
      var half = new[] { 0.5, 0.5 };
      return fu.Rect(parent, half, half, new[] { w / K, h / K }, half, new[] { (x + w / 2 - W / 2) / K, (H / 2 - (y + h / 2)) / K }, 1, out go);
    }

    /// <summary>Rewrites all of the memory from the models. The layout is the same each time (addresses stay where they were when the shape is unchanged), the hero grid keeps its own.</summary>
    public void Sync() {
      var h = Hero; var t = Tip; var v = Inv;
      ulong grid = h.Form != SimForm.Absent ? GridTable(h.Grid) : 0;
      Array.Clear(M.Mem, 0, ArenaAt); M.Top = 64;
      var fu = new FakeUnity(M); var st = new FakeState(M);
      var full = new[] { 0.0, 0 }; var one = new[] { 1.0, 1 }; var half = new[] { 0.5, 0.5 }; var tl = new[] { 0.0, 1 }; var tc = new[] { 0.5, 1 };
      ulong g0; ulong root = fu.Rect(0, full, one, full, half, full, 1, out g0);
      ulong csObj, csGo; ulong csui = fu.UiBase(g0, out csObj, out csGo);
      var forms = new List<object>(); long key = 1;
      Action<FTab> AddForm = f => { SetField(st, f, "m_csui", csui, 0x47); forms.Add(key++); forms.Add(f); };
      Func<string, SimForm, List<object>> Head = (cls, vis) => {
        var kv = new List<object> { "class", st.T("__cname", cls), "m_csui", 0L };
        if (vis == SimForm.Shown) { kv.Add("m_LuaLogicActive"); kv.Add(true); }
        return kv;
      };

      // EquipData + the hero screen
      var eq = st.T("m_CurrentSelectHeroUid", h.Form != SimForm.Absent || h.Hero != 0 ? h.Hero : EquipHero);
      Addrs = new EquipAddrs { EquipData = eq.A };
      if (h.Form != SimForm.Absent) {
        var kv = Head("Form_CharactorMain", h.Form);
        kv.AddRange(new object[] { "m_CharactorGrid_InfinityGrid", 0L, "m_IsUseSmallCard", h.Small, "m_LastType", (long)h.Tab });
        if (h.Hero > 0) { kv.Add("m_SelectHeroData"); kv.Add(st.T("iHeroId", h.Hero)); }
        // m_PanelDatas: [3] the slot whose gear list shows, [6] Hero_Filter { [1] the class entries, [3] the factions }
        var cls = new List<object>();
        cls.Add(1L); cls.Add(st.T("m_ProfessionID", h.ClassId > 0 ? h.ClassId : -1L));
        var camps = new List<object>(); for (int i = 0; i < h.Camps.Length; i++) { camps.Add((long)(i + 1)); camps.Add(h.Camps[i]); }
        var pd = new List<object>(); if (h.Part >= 0) { pd.Add(3L); pd.Add((long)h.Part); }
        pd.Add(6L); pd.Add(st.T(1L, st.T(cls.ToArray()), 3L, st.T(camps.ToArray())));
        kv.Add("m_PanelDatas"); kv.Add(st.T(pd.ToArray()));
        kv.Add("m_InfinityGridProxy"); kv.Add(st.T("m_Data", new FTab(grid)));
        if (!h.PanelsNil) {
          var panels = new List<object>();
          if (h.Filter == SimSlot.Loading) { panels.Add("Charactor_FilterOrder"); panels.Add(false); }
          else if (h.Filter != SimSlot.Absent) {
            var pk = new List<object> { "m_IsActive", h.Filter == SimSlot.Open };
            if (h.FactionList != null) { var fl = new List<object>(); for (int i = 0; i < h.FactionList.Length; i++) { fl.Add((long)(i + 1)); fl.Add(h.FactionList[i]); } pk.Add("m_FactionInfinityGridProxy"); pk.Add(st.T("m_Data", st.T(fl.ToArray()))); }
            var cl = new List<object>(); for (int i = 0; i < h.ClassCount; i++) { cl.Add((long)(i + 1)); cl.Add((long)i); }
            pk.Add("m_MainInfinityGridProxy"); pk.Add(st.T("m_Data", st.T(cl.ToArray())));
            panels.Add("Charactor_FilterOrder"); panels.Add(st.T(pk.ToArray()));
          }
          if (h.Gear != SimSlot.Absent) {
            object widget;
            if (h.Gear == SimSlot.Loading) widget = false;
            else {
              var wk = new List<object> { "m_bShow", h.Gear == SimSlot.Open };
              if (h.Gear == SimSlot.Open) { wk.Add("m_ButtonStates"); wk.Add(st.T(1L, h.SideStates[0], 2L, h.SideStates[1], 3L, h.SideStates[2])); }
              widget = st.T(wk.ToArray());
            }
            panels.Add("EquipPanel"); panels.Add(st.T("m_Panel_EquipFilterWidget_Charactor", widget));
          }
          kv.Add("m_Panels"); kv.Add(st.T(panels.ToArray()));
        }
        var f = st.T(kv.ToArray()); AddForm(f); Addrs.Form = f.A;
      }

      // the item card and the equip pop-up: the button is a real rect in the form's tree
      if (t.Form != SimForm.Absent) {
        ulong gB; Place(fu, root, t.BX, t.BY, t.BW, t.BH, out gB);
        M.Mem[gB - FakeLua.Base + 0x57] = (byte)(t.ButtonActive ? 1 : 0);
        int iGo; ulong btnGo = fu.Obj("UnityEngine", "GameObject", gB, out iGo);
        var rb = st.T("m_gameObject", 0L); SetField(st, rb, "m_gameObject", btnGo, 0x47);
        var view = st.T("m_RightBtn", rb);
        var kv = Head("Form_CharactorEquipTip", t.Form);
        kv.AddRange(new object[] { "m_SelectEquipView", view, "m_OneEquipView", view });
        if (t.Select != 0) { kv.Add("m_SelectEquipInfo"); kv.Add(st.T("iItemUid", t.Select)); }
        if (t.Current) { kv.Add("m_CurrentEquipInfo"); kv.Add(st.T("iItemId", 1L)); }
        if (t.One != 0) { kv.Add("m_OneEquipInfo"); kv.Add(st.T("iItemUid", t.One)); }
        AddForm(st.T(kv.ToArray()));
      }
      if (t.Popup != SimForm.Absent) AddForm(st.T(Head("Form_EquipPopUp", t.Popup).ToArray()));

      // the inventory: the viewport, the content scrolled under it, the cells made (a cell, its c_Item, its c_Frame) in the pool
      if (v.Form != SimForm.Absent) {
        ulong gv, gc; ulong vp = Place(fu, root, v.VX, v.VY, v.VW, v.VH, out gv);
        ulong content = fu.Rect(vp, tc, tc, new[] { v.VW / K, (v.Rows * v.Pitch + v.Pad) / K }, tc, new[] { 0.0, (v.VH / 2 + v.Scroll) / K }, 1, out gc);
        var idx = new List<int>(); for (int i = 0; i < v.Uids.Length; i++) if (v.Made(i)) idx.Add(i);
        if (idx.Count > 36) throw new InvalidOperationException("sim memory: " + idx.Count + " cells made, the fake transform table holds about 40");
        var mgos = new ulong[idx.Count]; var keys = new int[idx.Count];
        for (int n = 0; n < idx.Count; n++) {
          int i = idx[n]; ulong go, gi, gf;
          ulong cell = fu.Rect(content, tl, tl, new[] { v.Cell / K, v.Cell / K }, tl, new[] { (v.Pad + (i % v.Cols) * v.Pitch - v.VW / 2) / K, -(v.Pad + (i / v.Cols) * v.Pitch) / K }, 1, out go);
          ulong item = fu.Rect(cell, half, half, new[] { 0.9 * v.Cell / K, 0.9 * v.Cell / K }, half, new[] { 0.5 * v.Cell / K, -0.5 * v.Cell / K }, 1, out gi);
          ulong fr = fu.Rect(item, half, half, new[] { 0.8 * v.Cell / K, 0.8 * v.Cell / K }, half, full, 1, out gf);
          fu.Name(gi, "c_Item"); fu.Name(gf, "c_Frame"); fu.Name(go, "Cell"); fu.Kids(cell, item); fu.Kids(item, fr);
          mgos[n] = FakeWrap(M, "UnityEngine", "GameObject", go); keys[n] = i;
        }
        ulong gridObj; ulong gud = FakeManaged(fu, "UI", "InfinityGrid", 0x140, out gridObj);
        M.W64(gridObj + 0xC8, FakeWrap(M, "UnityEngine", "RectTransform", vp));
        M.W32(gridObj + 0xD0, idx.Count > 0 ? idx[0] : 0); M.W32(gridObj + 0xD4, idx.Count > 0 ? idx[idx.Count - 1] : 0);
        M.W64(gridObj + 0xD8, FakeCellDict(M, 0x20, keys, mgos));
        var uids = new List<object>(); for (int i = 0; i < v.Uids.Length; i++) { uids.Add((long)(i + 1)); uids.Add(v.Uids[i]); }
        var panel = st.T("m_FilterAndSortEquipUidList", st.T(uids.ToArray()));
        var kv = Head("Form_BackpackIntegration", v.Form);
        kv.AddRange(new object[] { "m_Content_InfinityGrid", 0L, "m_CachedContentPanels", st.T(-9998L, panel) });
        var f = st.T(kv.ToArray()); SetField(st, f, "m_Content_InfinityGrid", gud, 0x47); AddForm(f);
      }
      Env = fu.Env; UiTable = st.T(forms.ToArray()).A;
      // (the readers keep each field's node and the text of its key string between reads; the game never puts another text at the address of a live key string, but a rewrite of
      // the model does, so the readers' caches are dropped: a rewrite is "the game was restarted", not "the game changed a field")
      RFX.ForgetLive();
    }

    // ---------------------------------------------------------------- the real readers on it

    public HeroScreen ReadHeroScreen(HeroScreen prev) { return RFX.ReadHeroScreen(Addrs, prev); }
    public RFX.HeroChoice ReadHeroChoice() { return RFX.ReadHeroChoiceIds(Addrs.Form); }
    public TipState ReadTip() { return RFX.ReadTipMemIn(Env, UiTable, W, H, U); }
    public bool ReadPopup(out string why) { return RFX.ReadEquipPopupIn(Env, UiTable, out why); }
    public UiCellSet ReadCells(ICollection<long> want) { return RFX.UiCellRead(Env, UiTable, UiCellKind.Inventory, want, W, H, U); }
    public UiButtonInfo ReadTipButton(TipView view) { return RFX.UiButtonRead(Env, UiTable, new UiWidget("Form_CharactorEquipTip", TipMem.ButtonField(view)), W, H, U); }

    // ---------------------------------------------------------------- what the model says the readers must find (independent of the readers' own decisions)

    public static Mem3 ExpectFilter(SimHeroModel h) {
      if (h.PanelsNil || h.Filter == SimSlot.Loading) return Mem3.Unknown;
      return h.Filter == SimSlot.Open ? Mem3.Yes : Mem3.No;
    }
    public static Mem3 ExpectGear(SimHeroModel h) {
      if (h.PanelsNil) return Mem3.Unknown;
      if (h.Tab >= 0 && h.Tab != 3) return Mem3.No;   // another tab: the gear panel is hidden, whatever it holds
      if (h.Gear == SimSlot.Loading) return Mem3.Unknown;
      return h.Gear == SimSlot.Open ? Mem3.Yes : Mem3.No;
    }
    public static Mem3 ExpectSide(SimHeroModel h) {
      if (ExpectGear(h) != Mem3.Yes) return ExpectGear(h) == Mem3.No ? Mem3.No : Mem3.Unknown;
      return h.SideStates[0] > 0 || h.SideStates[1] > 0 || h.SideStates[2] > 0 ? Mem3.Yes : Mem3.No;
    }
    public static Mem3 ExpectSetPanel(SimHeroModel h) {
      if (ExpectGear(h) != Mem3.Yes) return ExpectGear(h) == Mem3.No ? Mem3.No : Mem3.Unknown;
      return h.SideStates[0] > 0 ? Mem3.Yes : Mem3.No;
    }
    /// <summary>The item card's button kind by the game's rule: a chosen item with an item worn = Replace, a chosen item alone or the one-card view = Wear, the fields cleared = Unknown.</summary>
    public static TipKind ExpectKind(SimTipModel t) {
      if (t.Form != SimForm.Shown) return TipKind.Unknown;
      if (t.Select != 0) return t.Current ? TipKind.Replace : TipKind.Wear;
      if (t.One != 0 && !t.Current) return TipKind.Wear;
      return TipKind.Unknown;
    }
    public static TipView ExpectView(SimTipModel t) { return t.Form != SimForm.Shown ? TipView.None : t.Select != 0 ? TipView.Select : t.One != 0 ? TipView.One : TipView.None; }
  }
}
