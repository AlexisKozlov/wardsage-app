// Wardsage - every target a hint frame is drawn on, by its key, and the game widget it is: the form, the widget's place in the form's tree
// (the UI graph's / the geometry file's prefab path), the visual child the frame hugs and the sprite it draws. app/Overlay.cs FrameFor takes
// every frame through here: the live rect of the visual child, its sprite's contour (src/SpriteArt.cs), and the layout's rect only when the
// live read fails (logged "frame: <key> source=layout reason=...").
//
// Key                         form.widget (tree under the form's root)                                                visual child            sprite
// city.barButton              Form_MainCity.m_Button_Backpack (pilot read CityInventory)                              Image_Icon              (live)
// city.heroes                 Form_MainCity BottomPanel/Image/m_Down_Layout/m_Button_Heros                            Image_Icon              (live)
// inventory.leftTab.equip     Form_BackpackIntegration Main_Frame/m_ToggleLeftTabGroup/#1 (template copy)             (picked)                (live)
// inventory.leftTab.artifact  Form_BackpackIntegration Main_Frame/m_ToggleLeftTabGroup/#2 (template copy)             (picked)                (live)
// inventory.workshop          Form_BackpackIntegration ...Panel_BackpackEquipPanel/Root/LeftBottomBtnGroup/Right/m_BtnWorkShop (FoundryButton)
// inventory.gem               Form_BackpackIntegration ...LeftBottomBtnGroup/Right/m_BtnGem
// inventory.bulkSell          Form_BackpackIntegration ...LeftBottomBtnGroup/Left/m_BtnBatchSell                      BackpackIntegration_btn_icon_enhanced
// inventory.artBulkSell       Form_BackpackIntegration ...Panel_ArtifactPanel/LeftBottomBtnGroup/Left/m_BtnBatchSell  BackpackIntegration_btn_icon_enhanced
// inventory.bulkBack          Form_BackpackIntegration ...LeftBottomBtnGroup/Left/m_BtnBack
// inventory.cardLeft/Solo     Form_BackpackIntegration ...Root/LeftLayout/m_Info/c_Button/c_LeftBtn
// inventory.cardRight         Form_BackpackIntegration ...Root/LeftLayout/m_Info/c_Button/c_RightBtn
// inventory.artWorkshop       Form_BackpackIntegration.m_PanelContentCur.m_BtnIntensify (pilot read ArtWorkshop)
// hero.workshop / hero.gem    Form_CharactorMain ...Panel_Equip_Simple/Content/GameObject/m_BtnSubliming / m_BtnEquipGem   Icon
// hero.tab.equip              Form_CharactorMain m_Main_Frame/m_Right/m_ToggleGroup/m_Toggle_Equip                    - (the whole tab: icon and label)
// hero.artifactTab            Form_CharactorMain m_Main_Frame/m_Right/m_ToggleGroup/m_Toggle_Artifact                 (picked)
// hero.slot.0..4              Form_CharactorMain ...EquipPanel/m_EquipGrid/m_LeftGrid|m_RightGrid/m_EquipPart_N       (picked)
// hero.filter                 Form_CharactorMain ...Panel_Equip_Filter/m_BtnFilter                                    (picked)
// hero.filterClose            Form_CharactorMain ...Panel_EquipFilterWidget_Charactor/MainFrame/MainFrame/m_BtnMainClose
// hero.confirm                Form_CharactorEquipTip m_RightBtn (pilot read EquipConfirm)
// hero.back                   Form_CharactorMain m_ButtonBack.m_btnBack (pilot read HeroBack)
// hero.artOpenList            Form_CharactorMain ...Panel_Equip_Artifact_Info/m_Content/m_ArtifactPanel/m_BtnDecompose
// hero.artAdd                 Form_CharactorMain ...Panel_Equip_Artifact_Info/m_Content/m_EmptyPanel/m_BtnEmptyArtifactAdd
// hero.artReplace             Form_CharactorMain m_Panels.EquipArtifactInfoTip|EquipArtifactInfo.m_BtnEquip (pilot read ArtReplace)  Button_Normal_2  common_btn_normal*
// hero.artHideWorn            Form_CharactorMain m_Panels.EquipArtifact.m_HideEquiped (pilot read ArtHideWorn)
// popup.equipReplace          Form_EquipPopUp Main_Frame/m_Bg/ButtonLayout/m_Btn_Right/Button_Special_3               -                       common_btn_special*
// foundry.tab.ascend/recast   Form_EquipSubliming GameObject/m_RightTab/GameObject/m_ToggleSubliming / m_ToggleRecast (AscendTab)
// foundry.back                Form_EquipSubliming GameObject/m_Back/Button_Back_New/Btn_Back
// ascend|recast.slot.0..4     Form_EquipSubliming m_ContentRoot/m_PanelEquip{Subliming|Recast}Go/.../m_EquipToogle_N  equipment_icon
// ascend|recast.filter        Form_EquipSubliming .../m_Left/FilterContent/m_BtnFilter
// ascend.button               Form_EquipSubliming .../m_Right/ButtonContent/m_BtnSubliming («Вознести»)
// recast.button               Form_EquipSubliming .../m_Right/ButtonContent/m_RecastContent/m_BtnRecast
// recast.chooseOld/New        Form_EquipSubliming .../m_Right/ButtonContent/m_ChooseContent/m_BtnChooseOld|New
// gem.button                  Form_EquipGemMain NormalNode/ContentFullRoot/m_Equip{Recast|Refining|..}/Widget_EquipGem_*/c_GemBottom/EquipGemBottom/GameObject/c_BtnReforge   common_btn_special
// filter.*                    Form_EquipFilterWidget Root/m_Root/... m_BtnReset, m_BtnSuitOff, m_BtnMainAttrOff, m_BtnSubAttrOff, m_Left|RightSuitFilterToggle
// artifact.tab.promote/enhance Form_EquipArtifactIntensify m_RightTab/GameObject/m_ToggleBreak / m_ToggleEnhance
// artifact.promoteBtn         Form_EquipArtifactIntensify Panel_Equip_Artifact_Break/root/m_CouldBreak/Middle/m_BreakInfo/m_Break/m_ButtonBreak (made in code)   Button_Special_3
// cell.*, hero.card           the lists' live cells (src/UiButtons.cs UiCellRead): the whole cell (icon with stat plate, the card)   cell root   (live)
// A tab or a button with a label (a plate) is framed whole ("-"); the sprite's contour only on round icon buttons without a plate.
// layout only (a reason each): the gem function rows, the filter's set cells, the artifact tab's and the workshop's lists,
// the gear filter's part icons and rows (no live reader of those lists yet).
// Pure data (tests/CoreTests.cs: every key resolves to a widget of the UI graph or the geometry file; no key the frames use is missing).
using System;
using System.Collections.Generic;

namespace RealmForge {
  public sealed class FrameTarget {
    public string Key, Form;
    /// <summary>The widget's place under the form's root, alternatives between '|' (UiWidget.Trees); "" = read by its pilot ClickTarget.</summary>
    public string Tree = "";
    /// <summary>The visual child the frame hugs by name ("" = picked: the icon child, or a wide button's drawn part; "-" = the widget itself).</summary>
    public string Visual = "";
    /// <summary>The sprite the visual child draws, as far as the prefab tells ("" = read live).</summary>
    public string Sprite = "";
    /// <summary>Where the tree comes from: "graph" (src/UiGraphData.cs), "geometry" (the site's geometry file), "prefab" (a panel the form makes in code / a template copy).</summary>
    public string Src = "graph";
    /// <summary>Read by the pilots' own button read (src/UiButtons.cs UiWidgetOf); None = by the tree here.</summary>
    public ClickTarget Click = ClickTarget.None;
    /// <summary>A list cell: its live cell read (LiveCell); None = a button.</summary>
    public UiCellKind Cell = UiCellKind.None;
    /// <summary>Not read live, and why (the layout's rect is the frame).</summary>
    public string Layout;
  }

  public static class FrameTargets {
    const string Bpi = "Main_Frame/m_ParentPanel/Panel_BackpackEquipPanel/Root/";
    const string Hero = "m_Main_Frame/m_Right/m_PanelContent/m_EquipPanel/EquipPanel/";
    const string HeroArt = "m_Main_Frame/m_Right/m_PanelContent/m_ArtifactPanel/ArtifactPanel/m_Right/";
    const string Asc = "m_ContentRoot/m_PanelEquipSublimingGo/Panel_Equip_Subliming/root/";
    const string Rec = "m_ContentRoot/m_PanelEquipRecastGo/Panel_Equip_Recast/root/";
    const string Flt = "Root/m_Root/";
    const string GemBtn = "/c_GemBottom/EquipGemBottom/GameObject/c_BtnReforge";

    static readonly List<FrameTarget> all = new List<FrameTarget>();
    static readonly Dictionary<string, FrameTarget> byKey = new Dictionary<string, FrameTarget>(StringComparer.Ordinal);

    static void B(string key, string form, string tree, string visual = "", string sprite = "", string src = "graph") {
      Add(new FrameTarget { Key = key, Form = form, Tree = tree, Visual = visual, Sprite = sprite, Src = src });
    }
    static void P(string key, string form, ClickTarget t, string tree, string visual = "", string src = "graph") {
      Add(new FrameTarget { Key = key, Form = form, Click = t, Tree = tree, Visual = visual, Src = src });
    }
    static void C(string key, string form, UiCellKind k, string tree) { Add(new FrameTarget { Key = key, Form = form, Cell = k, Tree = tree, Visual = "root" }); }
    static void L(string key, string form, string why) { Add(new FrameTarget { Key = key, Form = form, Layout = why, Src = "layout" }); }
    static void Add(FrameTarget t) { all.Add(t); byKey[t.Key] = t; }

    static FrameTargets() {
      // the city
      P("city.barButton", "Form_MainCity", ClickTarget.CityInventory, "BottomPanel/Image/m_Down_Layout/m_Button_Backpack", "Image_Icon");
      B("city.heroes", "Form_MainCity", "BottomPanel/Image/m_Down_Layout/m_Button_Heros", "Image_Icon");
      // the inventory
      B("inventory.leftTab.equip", "Form_BackpackIntegration", "Main_Frame/m_ToggleLeftTabGroup/#1", "-", "", "prefab");
      B("inventory.leftTab.artifact", "Form_BackpackIntegration", "Main_Frame/m_ToggleLeftTabGroup/#2", "-", "", "prefab");
      P("inventory.workshop", "Form_BackpackIntegration", ClickTarget.FoundryButton, Bpi + "LeftBottomBtnGroup/Right/m_BtnWorkShop");
      B("inventory.gem", "Form_BackpackIntegration", Bpi + "LeftBottomBtnGroup/Right/m_BtnGem");
      B("inventory.bulkSell", "Form_BackpackIntegration", Bpi + "LeftBottomBtnGroup/Left/m_BtnBatchSell", "-");
      B("inventory.artBulkSell", "Form_BackpackIntegration", "Main_Frame/m_ParentPanel/Panel_ArtifactPanel/LeftBottomBtnGroup/Left/m_BtnBatchSell", "-", "", "prefab");
      B("inventory.bulkBack", "Form_BackpackIntegration", Bpi + "LeftBottomBtnGroup/Left/m_BtnBack", "-", "", "geometry");
      B("inventory.cardLeft", "Form_BackpackIntegration", Bpi + "LeftLayout/m_Info/c_Button/c_LeftBtn", "", "", "geometry");
      B("inventory.cardSolo", "Form_BackpackIntegration", Bpi + "LeftLayout/m_Info/c_Button/c_LeftBtn", "", "", "geometry");
      B("inventory.cardRight", "Form_BackpackIntegration", Bpi + "LeftLayout/m_Info/c_Button/c_RightBtn", "", "", "geometry");
      P("inventory.artWorkshop", "Form_BackpackIntegration", ClickTarget.ArtWorkshop, "Main_Frame/m_ParentPanel/Panel_ArtifactPanel/m_ButtonGroup/m_IntensifyContainer/m_BtnIntensify/Button_Special_2");
      C("cell.Inventory", "Form_BackpackIntegration", UiCellKind.Inventory, "Main_Frame/ParentPanel/m_Content");
      L("inventory.artCell", "Form_BackpackIntegration", "the artifact tab's list cells are not read live");
      // the hero screen
      B("hero.workshop", "Form_CharactorMain", Hero + "m_Right/Panel_Equip_Simple/Content/GameObject/m_BtnSubliming", "Icon");
      B("hero.gem", "Form_CharactorMain", Hero + "m_Right/Panel_Equip_Simple/Content/GameObject/m_BtnEquipGem", "Icon");
      B("hero.tab.equip", "Form_CharactorMain", "m_Main_Frame/m_Right/m_ToggleGroup/m_Toggle_Equip", "-");
      B("hero.artifactTab", "Form_CharactorMain", "m_Main_Frame/m_Right/m_ToggleGroup/m_Toggle_Artifact", "-");
      for (int i = 0; i < 5; i++) B("hero.slot." + i, "Form_CharactorMain", Hero + "m_EquipGrid/" + (i < 2 ? "m_LeftGrid" : "m_RightGrid") + "/m_EquipPart_" + i);
      B("hero.filter", "Form_CharactorMain", Hero + "m_Left/Panel_Equip_Filter/m_BtnFilter", "-");
      B("hero.filterClose", "Form_CharactorMain", Hero + "m_Middle/m_PanelFilter/Panel_EquipFilterWidget_Charactor/MainFrame/MainFrame/m_BtnMainClose");
      P("hero.confirm", "Form_CharactorEquipTip", ClickTarget.EquipConfirm, "root/GameObject/m_SelectEquip/Panel_CharactorEquipTip/Button/c_RightBtn/Button_Normal_2", "", "prefab");
      P("hero.back", "Form_CharactorMain", ClickTarget.HeroBack, "m_Main_Frame/m_Back/Button_Back_New/Btn_Back");
      B("hero.artOpenList", "Form_CharactorMain", HeroArt + "Panel_Equip_Artifact_Info/m_Content/m_ArtifactPanel/m_BtnDecompose|" + HeroArt + "Panel_Equip_Artifact_InfoTip/m_Root/m_BtnDecompose", "", "", "prefab");
      B("hero.artAdd", "Form_CharactorMain", HeroArt + "Panel_Equip_Artifact_Info/m_Content/m_EmptyPanel/m_BtnEmptyArtifactAdd", "", "", "prefab");
      P("hero.artReplace", "Form_CharactorMain", ClickTarget.ArtReplace, HeroArt + "Panel_Equip_Artifact_InfoTip/m_Root/m_ButtonGroup/m_EquipContainer/m_BtnEquip/Button_Normal_2", "Button_Normal_2");
      P("hero.artHideWorn", "Form_CharactorMain", ClickTarget.ArtHideWorn, "m_Main_Frame/m_Right/m_PanelContent/m_ArtifactPanel/ArtifactPanel/m_Left/Panel_Equip_Artifact/m_Tab_1/m_HideEquiped", "-");
      B("popup.equipReplace", "Form_EquipPopUp", "Main_Frame/m_Bg/ButtonLayout/m_Btn_Right/Button_Special_3", "-");
      C("cell.HeroGear", "Form_CharactorMain", UiCellKind.HeroGear, "m_Panels.EquipFilter.m_EquipList_InfinityGrid");
      C("cell.HeroArt", "Form_CharactorMain", UiCellKind.HeroArt, "m_Panels.EquipArtifact (artifact list)");
      C("hero.card", "Form_CharactorMain", UiCellKind.HeroList, "m_InfinityGridProxy.m_Data (the form's *_InfinityGrid)");
      L("hero.filterPart", "Form_CharactorMain", "the gear filter's part icons are not read live");
      L("hero.filterRow", "Form_CharactorMain", "the gear filter's rows are not read live");
      // the Foundry
      P("foundry.tab.ascend", "Form_EquipSubliming", ClickTarget.AscendTab, "GameObject/m_RightTab/GameObject/m_ToggleSubliming", "-");
      B("foundry.tab.recast", "Form_EquipSubliming", "GameObject/m_RightTab/GameObject/m_ToggleRecast", "-");
      B("foundry.back", "Form_EquipSubliming", "GameObject/m_Back/Button_Back_New/Btn_Back");
      for (int i = 0; i < 5; i++) {
        B("ascend.slot." + i, "Form_EquipSubliming", Asc + "m_Left/EquipFilterContent/m_EquipToogle_" + i, "equipment_icon");
        B("recast.slot." + i, "Form_EquipSubliming", Rec + "m_Left/EquipFilterContent/m_EquipToogle_" + i, "equipment_icon");
      }
      B("ascend.filter", "Form_EquipSubliming", Asc + "m_Left/FilterContent/m_BtnFilter", "-", "", "geometry");
      B("recast.filter", "Form_EquipSubliming", Rec + "m_Left/FilterContent/m_BtnFilter", "-", "", "geometry");
      B("ascend.button", "Form_EquipSubliming", Asc + "m_Right/ButtonContent/m_BtnSubliming");
      B("recast.button", "Form_EquipSubliming", Rec + "m_Right/ButtonContent/m_RecastContent/m_BtnRecast");
      B("recast.chooseOld", "Form_EquipSubliming", Rec + "m_Right/ButtonContent/m_ChooseContent/m_BtnChooseOld", "", "", "geometry");
      B("recast.chooseNew", "Form_EquipSubliming", Rec + "m_Right/ButtonContent/m_ChooseContent/m_BtnChooseNew", "", "", "geometry");
      C("cell.Ascend", "Form_EquipSubliming", UiCellKind.Ascend, Asc + "m_Left/m_EquipList");
      C("cell.Recast", "Form_EquipSubliming", UiCellKind.Recast, Rec + "m_Left/m_EquipList");
      // the gem screen: «Улучшить» of the function shown (one widget per function, the shown one wins)
      B("gem.button", "Form_EquipGemMain", "NormalNode/ContentFullRoot/m_EquipRecast/Widget_EquipGem_Recast" + GemBtn + "|NormalNode/ContentFullRoot/m_EquipRefining/Widget_EquipGem_Refining" + GemBtn
        + "|NormalNode/ContentFullRoot/m_EquipIncrease/Widget_EquipGem_Increase" + GemBtn + "|NormalNode/ContentFullRoot/m_EquipMutation/Widget_EquipGem_Mutation" + GemBtn
        + "|NormalNode/ContentFullRoot/m_EquipSublimation/Widget_EquipGem_Sublimation" + GemBtn, "common_btn_special", "common_btn_special", "prefab");
      L("gem.cell", "Form_EquipGemMain", "the gem function rows are not read live");
      // the Foundry's filter pop-up
      B("filter.reset", "Form_EquipFilterWidget", Flt + "m_Content/ResetContent/m_BtnReset", "", "", "geometry");
      B("filter.suitOpen", "Form_EquipFilterWidget", Flt + "m_Content/SuitContent/hero_grid_number/FilterBtn/m_BtnSuitOff", "", "", "geometry");
      B("filter.mainOpen", "Form_EquipFilterWidget", Flt + "m_Content/MainAttrContent/hero_grid_number/FilterBtn/m_BtnMainAttrOff", "", "", "geometry");
      B("filter.subOpen", "Form_EquipFilterWidget", Flt + "m_Content/SubAttrContent/hero_grid_number/FilterBtn/m_BtnSubAttrOff", "", "", "geometry");
      B("filter.suit.tab.0", "Form_EquipFilterWidget", Flt + "FilterFrame/RectMask/m_FilterContent/m_SuitFilter/ToggleContent/m_LeftSuitFilterToggle", "-");
      B("filter.suit.tab.1", "Form_EquipFilterWidget", Flt + "FilterFrame/RectMask/m_FilterContent/m_SuitFilter/ToggleContent/m_RightSuitFilterToggle", "-", "", "geometry");
      L("filter.suit.cell", "Form_EquipFilterWidget", "the filter's set cells are not read live");
      // the artifact workshop
      B("artifact.tab.promote", "Form_EquipArtifactIntensify", "m_RightTab/GameObject/m_ToggleBreak", "-", "", "prefab");
      B("artifact.tab.enhance", "Form_EquipArtifactIntensify", "m_RightTab/GameObject/m_ToggleEnhance", "-", "", "prefab");
      B("artifact.promoteBtn", "Form_EquipArtifactIntensify", "Panel_Equip_Artifact_Break/root/m_CouldBreak/Middle/m_BreakInfo/m_Break/m_ButtonBreak/Button_Special_3", "-", "", "prefab");
      L("artifact.matList", "Form_EquipArtifactIntensify", "the copies list area (its cells are placed from the game's scroll)");
      L("artifact.matCell", "Form_EquipArtifactIntensify", "the workshop's copies list cells are not read live");
    }

    public const string KeyCityHeroes = "city.heroes", KeyHeroBack = "hero.back", KeyConfirm = "hero.confirm";
    /// <summary>The keys app/Overlay.cs frames by name (besides the steps' own keys of src/LocateFrames.cs and src/ArtEquip.cs).</summary>
    public static readonly string[] OverlayKeys = { KeyCityHeroes, KeyHeroBack, KeyConfirm, "hero.filter", "hero.tab.equip", "hero.filterClose", "inventory.bulkBack",
      "inventory.bulkSell", "inventory.artBulkSell", "inventory.leftTab.artifact", "hero.slot.0", "hero.slot.1", "hero.slot.2", "hero.slot.3", "hero.slot.4" };

    public static IList<FrameTarget> All { get { return all; } }
    public static FrameTarget Of(string key) { FrameTarget t; return key != null && byKey.TryGetValue(key, out t) ? t : null; }
    /// <summary>The key a pilot button's frame is checked under (null = none).</summary>
    public static string KeyOf(ClickTarget c) { if (c == ClickTarget.None) return null; foreach (var t in all) if (t.Click == c) return t.Key; return null; }
    public static string KeyOf(UiCellKind k) { return k == UiCellKind.None ? null : k == UiCellKind.HeroList ? "hero.card" : "cell." + k; }

    /// <summary>The widget read of a target (null = read by its ClickTarget / a cell / layout only).</summary>
    public static UiWidget Widget(FrameTarget t) {
      if (t == null || t.Layout != null || t.Click != ClickTarget.None || t.Cell != UiCellKind.None || string.IsNullOrEmpty(t.Tree)) return null;
      return new UiWidget(t.Form) { Trees = t.Tree.Split('|'), Icon = t.Visual != "-", IconChild = t.Visual == "" || t.Visual == "-" ? null : t.Visual };
    }

    /// <summary>The frame's size rule: never past the sprite's visible box by more than <see cref="MaxOut"/> px (the drawn line and its glow included).</summary>
    public const double MaxOut = 3;
  }

  /// <summary>The hint frame's look (app/Overlay.cs PaintFrame): the frame before 4882eea - glow rings from G px out down to 4 px, a 6 px dark underline
  /// and a 3.5 px gold line 2 px out of the frame rect.</summary>
  public static class FrameStyle {
    public const float LineWidth = 3.5f, UnderWidth = 6f;
    public static int GlowAlpha(int i, int G, float pulse) { return Math.Max(0, Math.Min(255, (int)(110 * pulse * (1 - i / (float)(G + 2))))); }
  }

  /// <summary>An item cell's one frame: the whole cell (the icon with the stat plate under it), and held through a moment without a fresh read.</summary>
  public static class CellFrame {
    /// <summary>The live frame <paramref name="fr"/> (x, y, w, h, r) made the whole cell: when the list's own cell (<paramref name="calc"/>: the layout's
    /// x, y, w, h - icon and plate) is taller than wide and the live one is the icon square only, it grows down to the layout's proportion.</summary>
    public static double[] Whole(double[] fr, double[] calc) {
      if (fr == null || fr.Length < 4 || calc == null || calc.Length < 4 || calc[2] <= 0 || fr[2] <= 0) return fr;
      double k = calc[3] / calc[2];
      if (k < 1.1 || fr[3] >= fr[2] * k * 0.9) return fr;
      var r = (double[])fr.Clone(); r[3] = Math.Round(fr[2] * k);
      return r;
    }

    /// <summary>The hero list read <paramref name="s"/> binds <paramref name="hero"/> to exactly one shown cell, at list index <paramref name="index"/>
    /// (the scan's order of the hero grid); anything else is unsure and the card is not framed.</summary>
    public static bool HeroCellSure(UiCellSet s, long hero, int index) {
      if (s == null || !s.Ok || hero <= 0 || index < 0) return false;
      UiCell one = null;
      foreach (var c in s.Cells) if (c.Uid == hero) { if (one != null) return false; one = c; }
      return one != null && one.Index == index && one.Active && one.BW >= 4 && one.BH >= 4;
    }

    /// <summary>The last Shown frame of the item framed now; an Unknown (no fresh read) within <see cref="Ms"/> gives it back, a Hidden or another item drops it.</summary>
    public sealed class Hold {
      public const long Ms = 800;
      string key; double[] fr; double w, h; long at;
      public UiCellSee Pick(string k, UiCellSee st, ref double[] f, double W, double H, long now) {
        if (st == UiCellSee.Shown && f != null) { key = k; fr = f; w = W; h = H; at = now; return st; }
        if (st == UiCellSee.Unknown && key == k && fr != null && Math.Abs(w - W) < 0.5 && Math.Abs(h - H) < 0.5 && now - at >= 0 && now - at <= Ms) { f = fr; return UiCellSee.Shown; }
        if (st != UiCellSee.Unknown || key != k) { key = null; fr = null; }
        return st;
      }
    }
  }
}
