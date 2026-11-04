// Wardsage - the navigator's read-only probe of the game's own UI state (docs/specs/game-nav-research.md; TEST builds only, changes nothing).
//
// One call reads, from the game's memory only: the input locks (UILocker), the five UI stacks with each form's flags (UIStack.m_uiStack, UIBase fields),
// the exit dialog's flag (Form_SystemTips.m_isShow), the city's hotkey table (DefaultOpenUIKeyCode and the player's rebinds), the hero grid's and the
// inventory list's ScrollRect (position, velocity, sensitivity). The result is a list of text lines; the host logs them tagged «navprobe» once a second
// and at once when something changed. Every offset is from the dump of 1.6.46.841.1 and is checked (a class name, a count in range): a miss is a
// line that says what failed, never an exception.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealmForge {
  public static partial class RFX {
    // UI.UIStatic static fields (the Il2CppClass static block): StackBase 0x8, StackMain 0x10, StackFlow 0x18, StackPopup 0x20, StackTop 0x28
    static readonly string[] NpStackNames = { "Base", "Main", "Flow", "Popup", "Top" };
    static readonly int[] NpStackOff = { 0x8, 0x10, 0x18, 0x20, 0x28 };

    static readonly CultureInfo NpInv = CultureInfo.InvariantCulture;

    /// <summary>A class of the game by its full name; not known yet = a scan for it is asked for (the probe needs classes the layout data has no address for).</summary>
    static ulong NpClass(ulong ga, string name) {
      ulong k = ClassAt(ga, name);
      if (k == 0) RequestScan(ga, name);
      return k;
    }

    static string NpHex(ulong v) { return "0x" + v.ToString("X"); }
    static int NpI32(byte[] b, int o) { return o + 4 <= b.Length ? BitConverter.ToInt32(b, o) : 0; }

    /// <summary>The probe's lines (null = no game / no layout data). Never throws.</summary>
    public static List<string> NavProbeLines() {
      var l = new List<string>(); npNameBudget = 12;
      if (TestMemory == null && (Layout.Current == null || GameInfo.GamePidFresh(1000) == 0)) return null;
      ulong ga;
      try { ga = RootGa(); } catch (Exception e) { l.Add("root: " + e.Message); return l; }
      if (ga == 0) { l.Add("root: no game assembly"); return l; }
      try { NpLocker(ga, l); } catch (Exception e) { l.Add("locker: exception " + e.Message); }
      try { NpStacks(ga, l); } catch (Exception e) { l.Add("stacks: exception " + e.Message); }
      try { NpExitDialog(ga, l); } catch (Exception e) { l.Add("exit dialog: exception " + e.Message); }
      try { NpScroll(l); } catch (Exception e) { l.Add("scroll: exception " + e.Message); }
      try { NpGeometry(ga, l); } catch (Exception e) { l.Add("geometry: exception " + e.Message); }
      try { NpPointer(ga, l); } catch (Exception e) { l.Add("pointer: exception " + e.Message); }
      return l;
    }

    /// <summary>The city hotkeys (the default table and the player's rebinds): seldom changes, read every 30 s by the host.</summary>
    public static List<string> NavProbeHotkeys() {
      var l = new List<string>();
      if (TestMemory == null && (Layout.Current == null || GameInfo.GamePidFresh(1000) == 0)) return null;
      try {
        ulong ga = RootGa(); if (ga == 0) { l.Add("hotkeys: no game assembly"); return l; }
        var lr = LuaRootWalk(ga);
        if (lr.G == 0) { l.Add("hotkeys: no Lua root (" + lr.FailStep + ")"); return l; }
        ulong t; int tt;
        if (!Field(lr.G, "DefaultOpenUIKeyCode", out t, out tt) || tt != T_TABLE) l.Add("hotkeys: DefaultOpenUIKeyCode " + (tt == 0 ? "missing" : "tt " + tt));
        else l.Add("hotkeys default: " + NpTable(t, 40, 1));
        // the player's rebinds: PlayerRecord's cache (mtPlayerRecord() keeps the record table in an upvalue); the keys that mention the hotkey prefs
        ulong cl; int ctt;
        if (Field(lr.G, "mtPlayerRecord", out cl, out ctt) && ctt == T_LCL) {
          var found = false;
          foreach (var up in NpUpvalueTables(cl)) {
            ulong cache; int ct;
            if (Field(up, "m_cache", out cache, out ct) && ct == T_TABLE) { found = true; l.Add("hotkeys rebinds: " + NpTableFilter(cache, "KeyCode", 60)); }
          }
          if (!found) {
            var kinds = new List<string>();
            foreach (var up in NpUpvalueTables(cl)) kinds.Add("{" + NpTable(up, 8, 0) + "}");
            l.Add("hotkeys rebinds: mtPlayerRecord has no m_cache upvalue table; its upvalue tables: " + (kinds.Count > 0 ? string.Join(" ", kinds.ToArray()) : "none"));
          }
        } else l.Add("hotkeys rebinds: mtPlayerRecord " + (ctt == 0 ? "missing" : "tt " + ctt));
      } catch (Exception e) { l.Add("hotkeys: exception " + e.Message); }
      try { l.Add("hotkeys rebinds (PlayerPrefs in the registry): " + NpPrefs()); } catch (Exception e) { l.Add("hotkeys rebinds (registry): exception " + e.Message); }
      return l;
    }

    /// <summary>The player's PlayerPrefs of the game (HKCU\Software\&lt;company&gt;\Watcher of Realms): the values whose name starts with KeyCodeOpen (Unity adds "_h&lt;hash&gt;" to each name).</summary>
    static string NpPrefs() {
      var sb = new StringBuilder(); int n = 0, keys = 0;
      using (var sw = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software")) {
        if (sw == null) return "no HKCU\\Software";
        foreach (var comp in sw.GetSubKeyNames()) {
          using (var k = sw.OpenSubKey(comp + "\\Watcher of Realms")) {
            if (k == null) continue;
            foreach (var name in k.GetValueNames()) {
              keys++;
              if (name.IndexOf("KeyCodeOpen", StringComparison.Ordinal) < 0) continue;
              int cut = name.LastIndexOf("_h", StringComparison.Ordinal);
              object v = k.GetValue(name);
              sb.Append(n++ > 0 ? ", " : "").Append(cut > 0 ? name.Substring(0, cut) : name).Append('=').Append(v is byte[] ? Encoding.UTF8.GetString((byte[])v).TrimEnd('\0') : Convert.ToString(v, NpInv));
              if (n >= 40) break;
            }
          }
        }
      }
      return n == 0 ? "(no KeyCodeOpen values among " + keys + ")" : sb.ToString();
    }

    static List<ulong> NpUpvalueTables(ulong cl) {
      var r = new List<ulong>();
      var cb = Read(cl, 0x20); if (cb == null || cb[8] != 6) return r;
      int n = cb[0xA]; if (n < 1 || n > 32) return r;
      var ub = Read(cl + 0x20, n * 8); if (ub == null) return r;
      for (int i = 0; i < n; i++) {
        ulong uv = BitConverter.ToUInt64(ub, i * 8); ulong vp = uv != 0 ? Ptr(uv) : 0;
        var tv = vp != 0 ? Read(vp, 16) : null; if (tv == null || BitConverter.ToInt32(tv, 8) != T_TABLE) continue;
        r.Add(BitConverter.ToUInt64(tv, 0));
      }
      return r;
    }

    static string NpKey(byte[] nb, int o) {
      int kt = BitConverter.ToInt32(nb, o + 24);
      if (kt == T_INT) return BitConverter.ToInt64(nb, o + 16).ToString();
      if (kt == T_SSTR) return ShortText(BitConverter.ToUInt64(nb, o + 16)) ?? "?";
      if (kt == T_FLT) return BitConverter.Int64BitsToDouble(BitConverter.ToInt64(nb, o + 16)).ToString("0.##", NpInv);
      return "k" + kt.ToString("X");
    }
    static string NpVal(byte[] nb, int o, int depth) {
      int vt = BitConverter.ToInt32(nb, o + 8); ulong v = BitConverter.ToUInt64(nb, o);
      if (vt == T_INT) return ((long)v).ToString();
      if (vt == T_FLT) return BitConverter.Int64BitsToDouble((long)v).ToString("0.##", NpInv);
      if (vt == T_BOOL) return (v & 0xFFFFFFFF) != 0 ? "true" : "false";
      if (vt == T_SSTR) return "'" + (ShortText(v) ?? "?") + "'";
      if (vt == T_TABLE) return depth > 0 ? "{" + NpTable(v, 12, depth - 1) + "}" : "{..}";
      return "t" + vt.ToString("X");
    }
    /// <summary>"k=v, k=v" of a script table's entries (hash part and array part), at most <paramref name="max"/>; tables inside to <paramref name="depth"/>.</summary>
    static string NpTable(ulong t, int max, int depth) {
      var h = Read(t, 56); if (h == null || h[8] != 5 || h[11] > 14) return "unreadable";
      var sb = new StringBuilder(); int n = 0;
      uint sa = BitConverter.ToUInt32(h, 12);
      if (sa > 0 && sa < 4096) {
        var ab = Read(BitConverter.ToUInt64(h, 16), (int)sa * 16);
        if (ab != null) for (int i = 0; i < sa && n < max; i++) {
          if (BitConverter.ToInt32(ab, i * 16 + 8) == T_NIL) continue;
          var one = new byte[32]; Array.Copy(ab, i * 16, one, 0, 16);
          sb.Append(n++ > 0 ? ", " : "").Append(i + 1).Append('=').Append(NpVal(one, 0, depth));
        }
      }
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32);
      if (nb != null) for (int i = 0; i < nn && n < max; i++) {
        int o = i * 32; if (BitConverter.ToInt32(nb, o + 8) == T_NIL || BitConverter.ToInt32(nb, o + 24) == T_NIL) continue;
        sb.Append(n++ > 0 ? ", " : "").Append(NpKey(nb, o)).Append('=').Append(NpVal(nb, o, depth));
      }
      return n == 0 ? "(empty)" : sb.ToString();
    }
    /// <summary>The entries of a script table whose key text contains <paramref name="part"/>.</summary>
    static string NpTableFilter(ulong t, string part, int max) {
      var h = Read(t, 56); if (h == null || h[8] != 5 || h[11] > 18) return "unreadable";
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return "unreadable";
      var sb = new StringBuilder(); int n = 0;
      for (int i = 0; i < nn && n < max; i++) {
        int o = i * 32; if (BitConverter.ToInt32(nb, o + 24) != T_SSTR || BitConverter.ToInt32(nb, o + 8) == T_NIL) continue;
        string k = ShortText(BitConverter.ToUInt64(nb, o + 16)); if (k == null || k.IndexOf(part, StringComparison.Ordinal) < 0) continue;
        sb.Append(n++ > 0 ? ", " : "").Append(k).Append('=').Append(NpVal(nb, o, 0));
      }
      return n == 0 ? "(none of " + part + ")" : sb.ToString();
    }

    // ---------------------------------------------------------------- UILocker
    static void NpLocker(ulong ga, List<string> l) {
      ulong k = NpClass(ga, "UI.UILocker"); if (k == 0) { l.Add("locker: class not found yet"); return; }
      ulong sf = Ptr(k + 0xB8); ulong inst = sf != 0 ? Ptr(sf) : 0;
      if (inst == 0 || ClassName(Ptr(inst)) != "UI.UILocker") { l.Add("locker: no instance (" + NpHex(inst) + ")"); return; }
      ulong d = Ptr(inst + 0x10);
      var db = d != 0 ? Read(d, 0x48) : null; if (db == null) { l.Add("locker: m_lockers unreadable " + NpHex(d)); return; }
      int count = NpI32(db, 0x20); ulong entries = BitConverter.ToUInt64(db, 0x18);
      if (count < 0 || count > 200) { l.Add("locker: count " + count + " out of range"); return; }
      var sb = new StringBuilder();
      var eb = count > 0 && entries != 0 ? Read(entries + 0x20, count * 16) : null;   // Dictionary<int,float>: {hash, next, key, value}
      if (eb != null) for (int i = 0; i < count; i++) sb.Append(i > 0 ? ", " : "").Append(NpI32(eb, i * 16 + 8)).Append("@").Append(BitConverter.ToSingle(eb, i * 16 + 12).ToString("0.00", NpInv));
      l.Add("locker: count=" + count + (sb.Length > 0 ? " [id@expiry " + sb + "]" : ""));
    }

    // ---------------------------------------------------------------- the stacks
    static readonly Dictionary<string, IList<StackForm>> npStackForms = new Dictionary<string, IList<StackForm>>();
    static readonly HashSet<string> npShownNames = new HashSet<string>();
    static bool npLockRead; static bool npLockOpen = true; static int npLockAt;

    static void NpStacks(ulong ga, List<string> l) {
      npStackForms.Clear(); npShownNames.Clear();
      ulong k = NpClass(ga, "UI.UIStatic"); if (k == 0) { l.Add("stacks: UI.UIStatic not found yet"); return; }
      ulong sf = Ptr(k + 0xB8); if (sf == 0) { l.Add("stacks: no static block"); return; }
      for (int s = 0; s < NpStackNames.Length; s++) {
        ulong st = Ptr(sf + (ulong)NpStackOff[s]);
        if (st == 0) { l.Add("stack " + NpStackNames[s] + ": null"); continue; }
        if (ClassName(Ptr(st)) != "UI.UIStack") { l.Add("stack " + NpStackNames[s] + ": not a UIStack (" + ClassName(Ptr(st)) + ")"); continue; }
        NpOneStack(NpStackNames[s], st, l);
      }
      var pr = EscapePredictor.Predict(npStackForms);
      l.Add("predict: Escape now → " + pr + (npShownNames.Contains("Form_SystemTips") ? " [Form_SystemTips is in a stack: the quit dialog is up]" : ""));
    }

    static void NpOneStack(string name, ulong st, List<string> l) {
      var hb = Read(st + 0x20, 0x28);   // m_uis 0x20, m_uiStack 0x28, m_nestedTransitions 0x30, m_bInTransition 0x38, m_transLockId 0x3C, m_inTransition 0x40
      if (hb == null) { l.Add("stack " + name + ": unreadable"); return; }
      ulong uis = BitConverter.ToUInt64(hb, 0), stack = BitConverter.ToUInt64(hb, 8), nested = BitConverter.ToUInt64(hb, 16);
      var tb = Read(st + 0x38, 0x10);
      string trans = tb != null ? "inTransition=" + tb[0] + "/" + tb[8] + " lockId=" + NpI32(tb, 4) : "inTransition=?";
      int nq = NpQueue(nested), shq = NpQueue(Ptr(st + 0x60)), hdq = NpQueue(Ptr(st + 0x58));
      // m_uiStack: StackEx<int> { list } -> LinkedList<int> { head 0x10, count 0x18 }, node { next 0x18, prev 0x20, item 0x28 }
      var ids = new List<int>(); string note = "";
      ulong list = stack != 0 ? Ptr(stack + 0x10) : 0;
      var lb = list != 0 ? Read(list, 0x20) : null;
      if (lb == null) note = " list unreadable (" + NpHex(stack) + ")";
      else {
        int cnt = NpI32(lb, 0x18); ulong node = BitConverter.ToUInt64(lb, 0x10);
        if (cnt < 0 || cnt > 60) note = " count " + cnt + " out of range";
        else for (int i = 0; i < cnt && node != 0; i++) {
          var nb = Read(node, 0x30); if (nb == null) { note = " node unreadable"; break; }
          ids.Add(NpI32(nb, 0x28)); node = BitConverter.ToUInt64(nb, 0x18);
        }
      }
      var sb = new StringBuilder(); var forms = new List<StackForm>(); npStackForms[name] = forms;
      for (int i = 0; i < ids.Count; i++) {
        var sf = new StackForm(); sb.Append(i > 0 ? " > " : "").Append(NpForm(uis, ids[i], sf)); forms.Add(sf);
        if (sf.Name != "") npShownNames.Add(sf.Name);
      }
      l.Add("stack " + name + " (list head→tail; the top is the end the game's GetTopUI takes): n=" + ids.Count + " " + trans + " nested=" + nq + " showAnims=" + shq + " hideAnims=" + hdq + note + (ids.Count > 0 ? " | " + sb : ""));
    }

    static int NpQueue(ulong q) { if (q == 0) return 0; var b = Read(q, 0x28); return b != null ? NpI32(b, 0x20) : -1; }   // Queue<T>._size

    // form id -> class name: the window table's names (the same source the screen state and the z-order use), else the form's own table found by its id in UIInstance
    static readonly Dictionary<int, string> npNames = new Dictionary<int, string>(); static ulong npUiAt; static int npNameBudget;
    static string NpName(int id) {
      string n; lock (formNames) { if (formNames.TryGetValue(id, out n)) return n; }
      if (npNames.TryGetValue(id, out n)) return n;
      if (npNameBudget > 0) {
        npNameBudget--;
        try {
          ulong env, ui; string why;
          if (UiRoots(out env, out ui, out why)) {
            if (ui != npUiAt) { npNames.Clear(); npUiAt = ui; }
            ulong f; int tt;
            if (IntKey(ui, id, out f, out tt) && tt == T_TABLE) { n = FormClassName(f, out why); if (!string.IsNullOrEmpty(n)) { npNames[id] = n; return n; } }
          }
        } catch (Exception) { }
      }
      return "id:" + id;
    }

    /// <summary>How the canvas scale fits the client: "height" (scale = client height / reference height), "width", "both", "none".</summary>
    public static string NavFit(double scale, double refW, double refH, double clientW, double clientH) {
      if (scale <= 0 || refW <= 0 || refH <= 0 || clientW <= 0 || clientH <= 0) return "?";
      double byH = clientH / refH, byW = clientW / refW;
      bool h = Math.Abs(scale - byH) < 0.002 * byH, w = Math.Abs(scale - byW) < 0.002 * byW;
      return h && w ? "both" : h ? "height" : w ? "width" : "none";
    }

    /// <summary>One form of a stack: name, full screen / pop-up, active, back delegate, the map's back behaviour, loading, animation, transition.</summary>
    static string NpForm(ulong uis, int id, StackForm sf) {
      string name = NpName(id);
      var e = ScreenMap.ByName(name);
      string back = e != null ? (e.Esc ?? "?") : "?";
      sf.Name = name; sf.Back = e != null ? (e.Esc ?? "default") : "default";
      ulong obj = NpFormObject(uis, id);
      if (obj == 0) return name + "[no object, back=" + back + "]";
      var b = Read(obj, 0x180); if (b == null) return name + "[unreadable]";
      bool hasBack = BitConverter.ToUInt64(b, 0x48) != 0, hasBehav = BitConverter.ToUInt64(b, 0xA0) != 0;
      bool active = b[0xE0] != 0, full = b[0x121] != 0, animValid = b[0x122] != 0, inited = b[0x109] != 0, loading = b[0x170] != 0, preloading = b[0x171] != 0;
      sf.Active = active; sf.HasDelegate = hasBack; sf.Full = full; sf.LuaBehav = hasBehav;
      int animLock = NpI32(b, 0x154); string trans = b[0x174] != 0 ? NpI32(b, 0x178).ToString() : "-";
      return name + "[" + (full ? "FULL" : "pop") + (active ? "" : " inactive") + " back=" + back + (hasBehav ? "(lua)" : "") + " delegate=" + (hasBack ? 1 : 0) + (inited ? "" : " !inited")
        + (loading || preloading ? " LOADING" : "") + (animValid ? " anim(lock " + animLock + ")" : "") + " trans=" + trans + "]";
    }

    /// <summary>The UIBase object of a form id in a stack's m_uis (Dictionary&lt;int, UIBase&gt;: entries {hash, next, key, value} 24 bytes from +0x20 of the array).</summary>
    static ulong NpFormObject(ulong uis, int id) {
      var db = uis != 0 ? Read(uis, 0x28) : null; if (db == null) return 0;
      int count = NpI32(db, 0x20); ulong entries = BitConverter.ToUInt64(db, 0x18);
      if (count <= 0 || count > 400 || entries == 0) return 0;
      var eb = Read(entries + 0x20, count * 24); if (eb == null) return 0;
      for (int i = 0; i < count; i++) if (NpI32(eb, i * 24 + 8) == id && NpI32(eb, i * 24) >= 0) return BitConverter.ToUInt64(eb, i * 24 + 16);
      return 0;
    }

    // ---------------------------------------------------------------- the exit dialog
    static void NpExitDialog(ulong ga, List<string> l) {
      ulong k = NpClass(ga, "Form_SystemTips"); if (k == 0) { l.Add("exit dialog: Form_SystemTips not found yet"); return; }
      ulong sf = Ptr(k + 0xB8); var b = sf != 0 ? Read(sf, 8) : null;
      var hex = new StringBuilder(); if (b != null) for (int i = 0; i < b.Length; i++) hex.Append(i > 0 ? " " : "").Append(b[i].ToString("X2"));
      // (the first byte read 1 on every screen: not the dialog's flag; the dialog is told by Form_SystemTips standing in a stack, see "predict")
      l.Add("exit dialog: static bytes " + (b != null ? hex.ToString() : "?") + " inStack=" + (npShownNames.Contains("Form_SystemTips") ? "yes" : "no"));
    }

    // ---------------------------------------------------------------- the canvas scale (the research 12): UIStatic.m_mainGroup -> UIGroup.<CanvasScaler> -> CanvasScalerRoot
    /// <summary>Client size of the game window, set by the host before a probe read (0 = unknown).</summary>
    public static double NavProbeClientW, NavProbeClientH;
    static void NpGeometry(ulong ga, List<string> l) {
      ulong k = NpClass(ga, "UI.UIStatic"); if (k == 0) return;
      ulong sf = Ptr(k + 0xB8); ulong grp = sf != 0 ? Ptr(sf) : 0;
      if (grp == 0) { l.Add("geometry: m_mainGroup null"); return; }
      string gn = ClassName(Ptr(grp)); ulong sc = Ptr(grp + 0x38);
      string sn = sc != 0 ? ClassName(Ptr(sc)) : null;
      var b = sc != 0 ? Read(sc, 0x40) : null;
      if (b == null) { l.Add("geometry: CanvasScaler unreadable (group " + gn + ", scaler " + sn + ")"); return; }
      double w = NavProbeClientW, h = NavProbeClientH;
      l.Add("geometry: scaler " + sn + " scale=" + BitConverter.ToSingle(b, 0x20).ToString("0.####", NpInv) + " ref=(" + BitConverter.ToSingle(b, 0x24).ToString("0", NpInv) + "x" + BitConverter.ToSingle(b, 0x28).ToString("0", NpInv)
        + ") padding=(" + BitConverter.ToSingle(b, 0x18).ToString("0.##", NpInv) + "," + BitConverter.ToSingle(b, 0x1C).ToString("0.##", NpInv) + ") designScale=" + BitConverter.ToSingle(b, 0x38).ToString("0.####", NpInv)
        + (w > 0 ? " fit=" + NavFit(BitConverter.ToSingle(b, 0x20), BitConverter.ToSingle(b, 0x24), BitConverter.ToSingle(b, 0x28), w, h) + " | client " + w.ToString("0", NpInv) + "x" + h.ToString("0", NpInv) + " Ui.Unit=" + Ui.Unit(w, h).ToString("0.##", NpInv) : ""));
    }

    // ---------------------------------------------------------------- the game's own pointer (the research 10): what the EventSystem says is under the cursor, and the last press
    static void NpPointer(ulong ga, List<string> l) {
      ulong k = NpClass(ga, "UnityEngine.EventSystems.EventSystem"); if (k == 0) { l.Add("pointer: EventSystem not found yet"); return; }
      ulong sf = Ptr(k + 0xB8), list = sf != 0 ? Ptr(sf) : 0;   // static m_EventSystems (List<EventSystem>)
      var lb = list != 0 ? Read(list, 0x20) : null; if (lb == null || NpI32(lb, 0x18) < 1) { l.Add("pointer: no EventSystem in the list"); return; }
      ulong arr = BitConverter.ToUInt64(lb, 0x10), es = arr != 0 ? Ptr(arr + 0x20) : 0;
      if (es == 0) { l.Add("pointer: EventSystem[0] null"); return; }
      ulong module = Ptr(es + 0x20); if (module == 0) { l.Add("pointer: no input module"); return; }
      ulong dict = Ptr(module + 0x48); var db = dict != 0 ? Read(dict, 0x28) : null; if (db == null) { l.Add("pointer: m_PointerData unreadable"); return; }
      int count = NpI32(db, 0x20); ulong entries = BitConverter.ToUInt64(db, 0x18);
      if (count <= 0 || count > 64 || entries == 0) { l.Add("pointer: m_PointerData count " + count); return; }
      var eb = Read(entries + 0x20, count * 24); ulong ped = 0;
      if (eb != null) for (int i = 0; i < count; i++) if (NpI32(eb, i * 24 + 8) == -1) ped = BitConverter.ToUInt64(eb, i * 24 + 16);
      if (ped == 0) { l.Add("pointer: no mouse pointer (key -1) among " + count); return; }
      var pb = Read(ped, 0x130); if (pb == null) { l.Add("pointer: PointerEventData unreadable"); return; }
      ulong enter = BitConverter.ToUInt64(pb, 0x20), press = BitConverter.ToUInt64(pb, 0x28), last = BitConverter.ToUInt64(pb, 0x30), ray = BitConverter.ToUInt64(pb, 0x48);
      // the UILocker's blocker image: the game's raycast hits it while the input is locked
      ulong locker = 0; ulong kl = ClassAt(ga, "UI.UILocker"); if (kl != 0) { ulong sfl = Ptr(kl + 0xB8), inst = sfl != 0 ? Ptr(sfl) : 0; locker = inst != 0 ? Ptr(inst + 0x18) : 0; }
      l.Add("pointer: position=(" + BitConverter.ToSingle(pb, 0xE8).ToString("0.0", NpInv) + "," + BitConverter.ToSingle(pb, 0xEC).ToString("0.0", NpInv) + ") raycast=" + NpHex(ray) + " enter=" + NpHex(enter)
        + " press=" + NpHex(press) + " lastPress=" + NpHex(last) + " clickTime=" + BitConverter.ToSingle(pb, 0x118).ToString("0.00", NpInv) + " clickCount=" + NpI32(pb, 0x11C)
        + " hitsLocker=" + (ray != 0 && ray == locker ? "yes" : "no") + " dragging=" + pb[0x129]);
    }

    // ---------------------------------------------------------------- the input gate (the research 2): any UILocker slot with an id, a stack transition or an animation queued = input closed
    /// <summary>Is the game's input open? true = open, false = locked (clicks land on the blocker, Escape is dropped), null = not readable (the caller keeps its timers).
    /// Read at most every 40 ms.</summary>
    public static bool? NavInputOpen() {
      int now = Environment.TickCount;
      if (npLockRead && unchecked(now - npLockAt) < 40) return npLockOpen;
      try {
        if (TestMemory == null && (Layout.Current == null || GameInfo.GamePidFresh(1000) == 0)) return null;
        ulong ga = RootGa(); if (ga == 0) return null;
        ulong kl = ClassAt(ga, "UI.UILocker"), ks = ClassAt(ga, "UI.UIStatic");
        if (kl == 0 || ks == 0) return null;
        ulong sfl = Ptr(kl + 0xB8), inst = sfl != 0 ? Ptr(sfl) : 0, d = inst != 0 ? Ptr(inst + 0x10) : 0;
        var db = d != 0 ? Read(d, 0x28) : null; if (db == null) return null;
        int count = NpI32(db, 0x20); ulong entries = BitConverter.ToUInt64(db, 0x18);
        if (count < 0 || count > 200) return null;
        bool open = true;
        if (count > 0 && entries != 0) {
          var eb = Read(entries + 0x20, count * 16); if (eb == null) return null;
          for (int i = 0; i < count; i++) if (NpI32(eb, i * 16 + 8) != 0) { open = false; break; }
        }
        if (open) {
          ulong sfs = Ptr(ks + 0xB8);
          if (sfs == 0) return null;
          foreach (int off in new[] { 0x10, 0x18, 0x20, 0x28 }) {   // Main, Flow, Popup, Top
            ulong st = Ptr(sfs + (ulong)off); if (st == 0) continue;
            var tb = Read(st + 0x38, 0x10);
            if (tb != null && (tb[0] != 0 || tb[8] != 0)) { open = false; break; }
            if (NpQueue(Ptr(st + 0x60)) > 0 || NpQueue(Ptr(st + 0x58)) > 0) { open = false; break; }
          }
        }
        npLockRead = true; npLockOpen = open; npLockAt = now;
        return open;
      } catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------- the lists' ScrollRects
    static void NpScroll(List<string> l) {
      string why; ulong env, ui;
      if (!UiRoots(out env, out ui, out why)) { l.Add("scroll: no Lua root (" + why + ")"); return; }
      int pid = ListPid(); if (pid == 0) { l.Add("scroll: no game process"); return; }
      NpScrollOf(env, ui, pid, "hero grid", "Form_CharactorMain", "m_CharactorGrid_ScrollRect", l);
      NpScrollOf(env, ui, pid, "inventory", "Form_BackpackIntegration", "m_Content_ScrollRect", l);
    }
    static void NpScrollOf(ulong env, ulong ui, int pid, string what, string form, string field, List<string> l) {
      bool shown; int copies; var r = new UiButtonInfo();
      ulong f = FormOf(env, ui, form, r, out shown, out copies);
      if (f == 0 || !shown) { l.Add("scroll " + what + ": " + form + " not shown"); return; }
      ulong rect = ScriptObject(f, field, "UnityEngine.UI.ScrollRect", pid);
      if (rect == 0) { l.Add("scroll " + what + ": " + field + " not readable"); return; }
      var b = Read(rect, 0xC8); if (b == null) { l.Add("scroll " + what + ": unreadable"); return; }
      l.Add("scroll " + what + ": pos=(" + BitConverter.ToSingle(b, 0xBC).ToString("0.0", NpInv) + "," + BitConverter.ToSingle(b, 0xC0).ToString("0.0", NpInv) + ") velocity=("
        + BitConverter.ToSingle(b, 0xB0).ToString("0.0", NpInv) + "," + BitConverter.ToSingle(b, 0xB4).ToString("0.0", NpInv) + ") dragging=" + b[0xB8] + " sensitivity=" + BitConverter.ToSingle(b, 0x34).ToString("0.##", NpInv)
        + " movement=" + NpI32(b, 0x24) + " inertia=" + b[0x2C]
        + " content.center=(" + BitConverter.ToSingle(b, 0x80).ToString("0.0", NpInv) + "," + BitConverter.ToSingle(b, 0x84).ToString("0.0", NpInv) + ") view.center=("
        + BitConverter.ToSingle(b, 0x98).ToString("0.0", NpInv) + "," + BitConverter.ToSingle(b, 0x9C).ToString("0.0", NpInv) + ")");
    }
  }
}
