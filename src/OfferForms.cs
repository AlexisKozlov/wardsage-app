// Wardsage - the game's offer / advert / notice / sign-in pop-ups over the city, and where their close button (×) is.
//
// The list is the game's own: the forms its pop-up queue opens after the login and in the city (the "face" pop-ups:
// Activity/ActivityFormMgr.lua, _TryPopupFlutterFace_New and the helpers it calls), by their class names (the form's class in
// UIStatic's UIInstance, src/OfferScan.cs; no form id is kept in the code). The × of each is the prefab's close button
// (m_BtnClose / m_ButtonClose / m_CloseBtn / m_Button_Close / m_BtnCloseShow / Button_Close; its click handler only hides the
// form - for the daily and the seven-day sign-in it also takes the day's free reward) measured like the site's geometry
// (tools/ui-geometry.py, game 1.6.46.841.1); the site's geometry file wins when it has "offer.close.<Form>". The other
// buttons of the prefab («Купить», «Перейти», «Получить», arrows, items ...) are the never-click rectangles of that form.
// A form without a known × (no close button in the prefab, or one that is not anchored to the screen) is never clicked:
// the pilot stops with the form's name.
// Also on the list: the reward / notice forms the game shows over the city in the login chain that close with a tap anywhere
// (UIHelper.NewClick2Close: a full-screen Click2Close button, «нажмите, чтобы закрыть» at the bottom centre - Form_ItemReward after the daily
// sign-in, live 1.6.66.393): their "×" is the strip of that text (Tap, tools/screen-map.py TAP_RECT), no other button of the prefab lies there.
// Also on the list: Form_EquipmentPlan («План сборки», the city bar's «Сборка»), a full screen a wrong click can open; it is left with its
// back arrow (UIHelper.NewButtonBack m_Back / Btn_Back, anchored top left), never with any of its plan buttons.
//
// Pure data and placement math, no Windows APIs (tests: tests/CoreTests.cs, tests/ScreenReplay.cs); the host
// (app/Overlay.cs) reads which form is shown (src/OfferScan.cs) and clicks through the gate (src/ClickGate.cs).
using System;
using System.Collections.Generic;

namespace RealmForge {
  public static class OfferForms {
    /// <summary>One pop-up: the form's class name, the × and the never-click rectangles
    /// ({x0 anchor, x0, x1 anchor, x1, y0 anchor, y0 (top), y1 anchor, y1 (bottom)}, U, anchors as the geometry file's: L C R / T M B).</summary>
    public sealed class Entry {
      public readonly string Name; public readonly object[] Close; public readonly object[][] Never;
      public Entry(string name, object[] close, params object[][] never) { Name = name; Close = close; Never = never ?? new object[0][]; }
    }
    /// <summary>The tap of a tap-to-close form: the strip of its «нажмите, чтобы закрыть» text at the bottom centre (tools/screen-map.py TAP_RECT).</summary>
    static readonly object[] Tap = { 'C', -0.1, 'C', 0.1, 'B', -0.07, 'B', -0.02 };
    static object[] R(char a0, double x0, char a1, double x1, char b0, double y0, char b1, double y1) { return new object[] { a0, x0, a1, x1, b0, y0, b1, y1 }; }

    /// <summary>A pop-up must have been on the screen this long before its × is clicked (its opening animation scales it in).</summary>
    public const int SettleMs = 1000;

    public static readonly Entry[] All = {
      new Entry("Form_ActGrowthFundLevelUp", R('C', 0.6641, 'C', 0.7422, 'M', -0.4297, 'M', -0.3516)),
      new Entry("Form_ActPopupFB", R('C', 0.6234, 'C', 0.7125, 'M', -0.4797, 'M', -0.3906), R('C', 0.3187, 'C', 0.5031, 'M', 0.3141, 'M', 0.4953)),
      new Entry("Form_ActPopupImage", R('C', 0.5844, 'C', 0.6734, 'M', -0.3813, 'M', -0.2922), R('C', -0.0797, 'C', 0.0969, 'M', 0.2391, 'M', 0.3969)),
      new Entry("Form_ActPopupLottery", R('C', 0.7346, 'C', 0.819, 'M', -0.4609, 'M', -0.3766), R('C', 0.4016, 'C', 0.7609, 'M', 0.1734, 'M', 0.3922)),
      new Entry("Form_ActPopupNewHero", R('C', 0.7562, 'C', 0.8344, 'M', -0.4562, 'M', -0.3781), R('C', -0.1827, 'C', 0.1783, 'M', 0.2794, 'M', 0.3981)),
      new Entry("Form_ActPopupPrivilege", R('C', 0.7578, 'C', 0.8359, 'M', -0.4461, 'M', -0.368), R('C', -0.0888, 'C', -0.0438, 'M', -0.297, 'M', -0.2521), R('C', 0.3016, 'C', 0.5927, 'M', 0.2431, 'M', 0.3359)),
      new Entry("Form_ActPopUpQuestionnaireNoReward", R('C', 0.7516, 'C', 0.8297, 'M', -0.4391, 'M', -0.3609), R('C', 0.05, 'C', 0.4109, 'M', 0.0984, 'M', 0.2172)),
      new Entry("Form_ActPopupVK", R('C', 0.6234, 'C', 0.7125, 'M', -0.4797, 'M', -0.3906), R('C', 0.3516, 'C', 0.5359, 'M', 0.3227, 'M', 0.5039)),
      new Entry("Form_AnniversaryPack", R('R', -0.1487, 'R', -0.0706, 'T', 0.0486, 'T', 0.1267), R('C', 0.3422, 'C', 0.4078, 'M', -0.3711, 'M', -0.3055), R('C', -0.25, 'C', 0.0312, 'M', -0.2062, 'M', 0.075), R('C', -0.1023, 'C', -0.0008, 'M', -0.1633, 'M', -0.0617)),
      new Entry("Form_AnnouncementHall", R('C', 0.7794, 'C', 0.8637, 'M', -0.4077, 'M', -0.3233)),
      new Entry("Form_ChampionshipBegin", R('C', 0.7667, 'C', 0.8511, 'M', -0.4828, 'M', -0.3984), R('C', -1.0687, 'C', -0.7078, 'M', 0.4251, 'M', 0.5438), R('C', -0.3421, 'C', 0.0189, 'M', 0.3813, 'M', 0.5001), R('C', 0.4058, 'C', 0.6942, 'M', 0.3255, 'M', 0.4332), R('C', 0.4516, 'C', 0.5297, 'M', -0.0453, 'M', 0.0297)),
      new Entry("Form_ChampionshipOver", Tap),
      new Entry("Form_DailyCheckIn", R('C', 0.7898, 'C', 0.8742, 'M', -0.4441, 'M', -0.3597), R('C', -0.6626, 'C', -0.5689, 'M', 0.2369, 'M', 0.3556), R('C', 0.0785, 'C', 0.3331, 'M', 0.2991, 'M', 0.3554)),
      new Entry("Form_EquipmentPlan", R('L', 0.0, 'L', 0.13, 'T', 0.0, 'T', 0.084), R('L', 0.14, 'L', 0.6, 'T', 0.0, 'T', 0.08), R('L', 0.0, 'L', 0.27, 'T', 0.115, 'T', 0.245), R('L', 0.3, 'R', 0.0, 'B', -0.11, 'B', 0.0)),
      new Entry("Form_Feedback", null),
      new Entry("Form_FirstCharge", R('C', 0.7578, 'C', 0.8359, 'M', -0.4484, 'M', -0.3703)),
      new Entry("Form_FrameLessPopup", R('C', 0.7812, 'C', 0.8594, 'M', -0.475, 'M', -0.3969), R('L', 0.2391, 'R', -0.2391, 'T', 0.1445, 'B', -0.1445)),
      new Entry("Form_FriendInvitePopup", R('C', 0.7219, 'C', 0.8, 'M', -0.4609, 'M', -0.3828), R('C', -0.2703, 'C', -0.1141, 'M', 0.0725, 'M', 0.2288), R('C', 0.325, 'C', 0.6859, 'M', 0.1906, 'M', 0.3094)),
      new Entry("Form_GiftPush", R('R', -0.1266, 'R', -0.0484, 'T', 0.0453, 'T', 0.1234), R('C', 0.75, 'C', 0.8281, 'M', -0.0375, 'M', 0.0375)),
      new Entry("Form_GvgPreviewPopUp", R('C', 0.7078, 'C', 0.7859, 'M', -0.3484, 'M', -0.2703), R('C', -0.0878, 'C', 0.0216, 'M', 3.9375, 'M', 4.0469), R('C', -0.1844, 'C', 0.1766, 'M', 0.1795, 'M', 0.2983)),
      new Entry("Form_IntegratedGift", R('R', -0.1477, 'R', -0.0695, 'T', 0.0484, 'T', 0.1266), R('C', -0.4599, 'C', -0.1307, 'M', 0.3044, 'M', 0.4219), R('C', 0.1083, 'C', 0.4374, 'M', 0.3044, 'M', 0.4219), R('C', 0.6152, 'C', 0.7152, 'M', -0.0091, 'M', 0.0909), R('C', -0.6948, 'C', -0.5542, 'M', 0.2822, 'M', 0.4025)),
      new Entry("Form_IOSATTPopup", R('C', 0.7106, 'C', 0.7887, 'M', -0.4238, 'M', -0.3457), R('C', 0.5611, 'C', 0.6471, 'M', 0.0764, 'M', 0.1623), R('C', 0.1409, 'C', 0.5019, 'M', 0.2376, 'M', 0.3563)),
      new Entry("Form_MonthlyCardBuy", R('C', 0.7328, 'C', 0.8109, 'M', -0.4281, 'M', -0.35)),
      new Entry("Form_ItemReward", Tap),
      new Entry("Form_MonthyCardGetReward", Tap),
      new Entry("Form_PhonecalendarOptimizePopup", R('C', 0.7562, 'C', 0.8344, 'M', -0.4484, 'M', -0.3703), R('C', -0.3753, 'C', -0.0144, 'M', 0.307, 'M', 0.4258), R('C', 0.0034, 'C', 0.3644, 'M', 0.307, 'M', 0.4258)),
      new Entry("Form_PhoneCalendarScheduledPopup", R('C', 0.6562, 'C', 0.7344, 'M', -0.4375, 'M', -0.3594), R('C', 0.1987, 'C', 0.3237, 'M', 0.0083, 'M', 0.1333), R('C', -0.3686, 'C', -0.0077, 'M', 0.2084, 'M', 0.3272), R('C', -0.0077, 'C', 0.3533, 'M', 0.2084, 'M', 0.3272)),
      new Entry("Form_PopUpShop", R('C', 0.7609, 'C', 0.8391, 'M', -0.4547, 'M', -0.3766)),
      new Entry("Form_PrivilegeFreeBuy", R('C', 0.7578, 'C', 0.8359, 'M', -0.4484, 'M', -0.3703)),
      new Entry("Form_PrivilegeFreeStart", R('C', 0.7578, 'C', 0.8359, 'M', -0.4484, 'M', -0.3703), R('C', 0.2509, 'C', 0.5105, 'M', 0.2315, 'M', 0.3116)),
      new Entry("Form_QuestionFace", R('C', 0.7812, 'C', 0.8594, 'M', -0.475, 'M', -0.3969), R('C', -0.6625, 'C', -0.3234, 'M', 0.1906, 'M', 0.2875)),
      new Entry("Form_Recruitment", R('C', 0.7609, 'C', 0.8391, 'M', -0.4547, 'M', -0.3766), R('C', -0.1304, 'C', 0.0782, 'M', 0.217, 'M', 0.2795), R('C', 0.0933, 'C', 0.302, 'M', 0.217, 'M', 0.2795), R('C', 0.3005, 'C', 0.6614, 'M', 0.188, 'M', 0.3067), R('C', 0.2469, 'C', 0.4031, 'M', 3.2719, 'M', 3.4281)),
      new Entry("Form_SevenDaySign", R('C', 0.6953, 'C', 0.7734, 'M', -0.3422, 'M', -0.2641), R('C', -0.6133, 'C', -0.4352, 'M', -0.2461, 'M', 0.3254), R('C', 0.2544, 'C', 0.2951, 'M', -0.3192, 'M', -0.2785)),
      new Entry("Form_SignReward", R('C', 0.7016, 'C', 0.7797, 'M', -0.3312, 'M', -0.2531)),
      new Entry("Form_SpecialItemReward", Tap),
      new Entry("Form_UnionWeekBossPopUp", R('C', 0.7078, 'C', 0.7859, 'M', -0.3484, 'M', -0.2703), R('C', -0.0964, 'C', 0.013, 'M', 4.0861, 'M', 4.1955), R('C', -0.1844, 'C', 0.1766, 'M', 0.2859, 'M', 0.4047)),
      new Entry("Form_WholeServerWalfarePopUp", R('C', 0.6562, 'C', 0.7344, 'M', -0.4375, 'M', -0.3594), R('C', 0.0, 'C', 0.3609, 'M', 0.1731, 'M', 0.2919), R('C', -0.3736, 'C', -0.0127, 'M', 0.1734, 'M', 0.2922)),
    };

    static readonly Dictionary<string, Entry> byName = new Dictionary<string, Entry>();
    static OfferForms() { foreach (var e in All) byName[e.Name] = e; }

    public static Entry ByName(string name) { Entry e; return name != null && byName.TryGetValue(name, out e) ? e : null; }
    public static bool IsOffer(string name) { return ByName(name) != null; }
    /// <summary>The form is on the list and its × is known (from the site's geometry or the prefab numbers above).</summary>
    public static bool Closable(GameGeometry g, string name) { return CloseRect(g, name, 1920, 1080, 1080) != null; }

    /// <summary>The forms the app knows as screens (not pop-ups to report): the city, the hero screen, the inventory, the Foundry, the gem
    /// screen, the workshop, the battle, the tips; their names never count as an unknown form on top.</summary>
    public static readonly string[] KnownScreens = { "Form_MainCity", "Form_CharactorMain", "Form_BackpackIntegration", "Form_EquipSubliming", "Form_EquipGemMain",
      "Form_EquipArtifactIntensify", "Form_Battle", "Form_CommonTips", "Form_EquipPopUp", "Form_EquipFilterWidget" };

    /// <summary>Always-on, non-blocking overlay forms: the game registers them on its flow layer (RegisterLuaUI ... StackFlow: toasts, the side tip
    /// panel, the marquee banner; client Lua UI/Form_*.lua, none has a pop-up body or a close button). They never make a screen Unknown, never
    /// count as an unknown top form or modal, and never trigger an auto recording. Other StackFlow forms (Dialog, Subtitle, GuideTextCutscenes,
    /// BattleTimeLineSkip) can cover or block a screen and are left out; so are the chat forms (StackMain).</summary>
    public static readonly string[] IgnoredOverlays = { "Form_LeftTip", "Form_Marquee", "Form_MiniSystemTip", "Form_PowerChangeTips", "Form_IntegralRewardTip" };
    public static bool IsIgnoredOverlay(string name) { return name != null && Array.IndexOf(IgnoredOverlays, name) >= 0; }

    static double X(char a, double off, double W, double U) { return (a == 'L' ? 0 : a == 'C' ? W / 2 : W) + off * U; }
    static double Y(char a, double off, double H, double U) { return (a == 'T' ? 0 : a == 'M' ? H / 2 : H) + off * U; }
    static GeoRect Place(object[] f, double W, double H, double U) {
      double x0 = X((char)f[0], (double)f[1], W, U), x1 = X((char)f[2], (double)f[3], W, U), y0 = Y((char)f[4], (double)f[5], H, U), y1 = Y((char)f[6], (double)f[7], H, U);
      return new GeoRect { X = Math.Min(x0, x1), Y = Math.Min(y0, y1), W = Math.Abs(x1 - x0), H = Math.Abs(y1 - y0) };
    }

    /// <summary>The × of form <paramref name="name"/> in client px: the site's geometry ("offer.close.&lt;Form&gt;"), else the prefab numbers; null = not known.</summary>
    public static GeoRect CloseRect(GameGeometry g, string name, double W, double H, double U) {
      var e = ByName(name); if (e == null) return null;
      var r = g != null ? g.Rect("offer.close." + name, W, H, U) : null;
      if (r != null && r.W > 2 && r.H > 2) return r;
      return e.Close != null ? Place(e.Close, W, H, U) : null;
    }

    /// <summary>The click point on the ×: its centre (null = no × known).</summary>
    public static GeoRect ClosePoint(GameGeometry g, string name, double W, double H, double U) {
      var r = CloseRect(g, name, W, H, U); if (r == null) return null;
      return new GeoRect { X = r.CX, Y = r.CY, W = 0, H = 0 };
    }

    /// <summary>The point lies in the inner part of the × (a margin of a fifth of its size on each side: never its edge).</summary>
    public static bool OnClose(GameGeometry g, string name, double x, double y, double W, double H, double U) {
      var r = CloseRect(g, name, W, H, U); if (r == null) return false;
      double mx = r.W / 5, my = r.H / 5;
      return x >= r.X + mx && x <= r.Right - mx && y >= r.Y + my && y <= r.Bottom - my;
    }

    /// <summary>The point lies inside one of the form's other buttons (the buy / go / claim buttons and the rest): never clicked.</summary>
    public static bool OnNeverButton(string name, double x, double y, double W, double H, double U) {
      var e = ByName(name); if (e == null) return false;
      foreach (var f in e.Never) {
        var r = Place(f, W, H, U);
        if (x >= r.X && x <= r.Right && y >= r.Y && y <= r.Bottom) return true;
      }
      return false;
    }

    /// <summary>Which of several offer pop-ups shown at once is on top (the window table has no order): the one that came last, alone, in a
    /// later read than the others (a form the game opens lies over the ones open). Note() gives the shown list with that one first; two that
    /// came in the same read leave the top unknown (the gate then closes none). One instance per host (app/Overlay.cs, the sim). Pure.</summary>
    public sealed class Order {
      readonly Dictionary<string, int> seq = new Dictionary<string, int>(); int reads;
      public bool TopKnown { get; private set; }
      public List<string> Note(List<string> shown) {
        if (shown == null) return null;
        reads++;
        foreach (var n in new List<string>(seq.Keys)) if (!shown.Contains(n)) seq.Remove(n);
        foreach (var n in shown) if (!seq.ContainsKey(n)) seq[n] = reads;
        string top = null; int best = -1; bool tie = false;
        foreach (var kv in seq) { if (kv.Value > best) { best = kv.Value; top = kv.Key; tie = false; } else if (kv.Value == best) tie = true; }
        TopKnown = shown.Count <= 1 || (top != null && !tie);
        if (shown.Count <= 1 || !TopKnown) return shown;
        var res = new List<string> { top }; foreach (var n in shown) if (n != top) res.Add(n);
        return res;
      }
    }

    /// <summary>The pop-up has been on the screen long enough (SettleMs) for its × to be where the prefab puts it.</summary>
    public static bool Settled(ScreenState s, long now) { return s != null && s.OfferCount > 0 && now - s.SinceMs >= SettleMs; }
    /// <summary>Its × read live and held still (LiveStill: the same rect for its gap, the scale-in is over): settled after LiveSettleMs already.</summary>
    public static bool Settled(ScreenState s, long now, bool liveStill) { return Settled(s, now) || (liveStill && s != null && s.OfferCount > 0 && now - s.SinceMs >= LiveSettleMs); }
    public const int LiveSettleMs = 200;
  }
}
