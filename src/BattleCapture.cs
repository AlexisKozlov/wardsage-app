// RealmForge — the result of a boss fight, read from the game's memory when its result screen opens (read-only), for
// comparing the site's battle simulation with real fights: each hero's damage to the boss, healing, damage taken, and
// the fight's length. Kept in %APPDATA%\Wardsage\battles (the last 30) and sent with the account snapshot.
//
// The game keeps its open windows in UIStatic's UIInstance: a script table form id -> form, the id being the hash of the
// form's name (str_hash, as the language keys; the ids come with the layout data from the server, src/Layout.cs). The
// table is found once (from a node of a known form), then read every poll: a few kilobytes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RealmForge {
  public static partial class RFX {
    // the result screens: the guild dragon, the weekly guild boss, the rest, the arena (CHAPTERTYPE.Pvp ->
    // Form_PvpBattleEnd), the friendly arena fight (Form_SparringPvpBattleEnd) and the Malric halls (Form_ExpeditionBattleEnd); ids = UIDefines (str_hash of the name)
    static readonly string[] EndFormKeys = { "ID_FORM_UNIONBOSSBATTLEEND", "ID_FORM_UNIONWEEKBOSSBATTLEEND", "ID_FORM_BATTLEEND", "ID_FORM_PVPBATTLEEND", "ID_FORM_SPARRINGPVPBATTLEEND", "ID_FORM_EXPEDITIONBATTLEEND" };
    static readonly string[] EndFormNames = { "Form_UnionBossBattleEnd", "Form_UnionWeekBossBattleEnd", "Form_BattleEnd", "Form_PvpBattleEnd", "Form_SparringPvpBattleEnd", "Form_ExpeditionBattleEnd" };
    // forms whose node leads to UIInstance: the hero screen, the inventory, the battle screen, the result screens, the city (built at the login:
    // the only one there before the player opens any of the others)
    static readonly string[] UiAnchorKeys = { "ID_FORM_CHARACTORMAIN", "ID_FORM_BACKPACKINTEGRATION", "ID_FORM_BATTLE", "ID_FORM_UNIONBOSSBATTLEEND",
      "ID_FORM_UNIONWEEKBOSSBATTLEEND", "ID_FORM_BATTLEEND", "ID_FORM_PVPBATTLEEND", "ID_FORM_SPARRINGPVPBATTLEEND", "ID_FORM_MAINCITY" };

    /// <summary>The ids of these forms from the layout data; empty without it.</summary>
    static long[] FormIds(string[] keys) {
      var ly = Layout.Current; if (ly == null) return new long[0];
      var r = new long[keys.Length];
      for (int i = 0; i < keys.Length; i++) r[i] = keys[i] == "ID_FORM_EXPEDITIONBATTLEEND" || keys[i] == "ID_FORM_MAINCITY" ? ly.FormOpt(keys[i]) : ly.Form(keys[i]);   // newer forms: older layout data lacks them (0 = skipped)
      return r;
    }
    static ulong uiTable; static int uiLastFind;

    static bool SameId(long key, long id) { return key == id || (id < 0 && key == (long)(uint)id); }

    /// <summary>UIInstance, searched when not known (a full pass, at most every <paramref name="rescanMs"/>).</summary>
    static ulong UiTable(int rescanMs) {
      if (uiTable != 0 && IsTable(uiTable)) return uiTable;
      uiTable = 0;
      if (LuaRootFirst && Layout.Current != null) {
        // the game's own pointers first (src/LuaRoot.cs): no scan when they lead to the table
        string rwhy; ulong r = RootUi(out rwhy);
        string rs = r != 0 && IsTable(r) ? "forms: window table from the script root at " + r.ToString("X") + " (" + ShownNames(r, 3) + ")" : "forms: window table not reached from the script root: " + rwhy + "; the scan looks for it";
        if (rs != uiRootSaid) { uiRootSaid = rs; LL(rs); }
        if (r != 0 && IsTable(r)) { uiTable = r; return r; }
      }
      if (uiLastFind != 0 && Environment.TickCount - uiLastFind < rescanMs) return 0;
      uiLastFind = Environment.TickCount;
      var anchors = FormIds(UiAnchorKeys);
      if (anchors.Length == 0) return 0;   // no layout data: the game's windows are not looked for
      // its own look at the game: the handle and the memory regions of now (no equip scan may have run since the start,
      // and the regions change as the game allocates)
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) return 0;
      if (H == IntPtr.Zero) H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return 0;
      regs = Regions();
      string said;
      ulong found = FindUiIn(WithAllForms(anchors), out said);
      if (said != uiPickSaid) { uiPickSaid = said; LL(said); }
      uiTable = found;
      return found;
    }
    static string uiPickSaid = "", uiRootSaid = "";

    /// <summary>The id of a form by its name: the game's UIDefines.ID_FORM_* is the 32-bit String.GetHashCode of the name (the
    /// legacy non-randomized 64-bit variant, il2cpp's).</summary>
    public static long FormIdOf(string name) {
      uint h1 = 5381, h2 = 5381;
      for (int i = 0; i < name.Length; i += 2) {
        h1 = unchecked(((h1 << 5) + h1) ^ name[i]);
        if (i + 1 < name.Length) h2 = unchecked(((h2 << 5) + h2) ^ name[i + 1]);
      }
      return unchecked((int)(h1 + h2 * 0x5D588B65u));
    }
    static long[] allFormIds;
    /// <summary>The ids of every form of the screen map (~1200): whatever screen is open, one of them is in UIInstance.</summary>
    static long[] AllFormIds() {
      var a = allFormIds;
      if (a == null) { var l = new List<long>(); foreach (var e in ScreenMap.All) l.Add(FormIdOf(e.Name)); allFormIds = a = l.ToArray(); }
      return a;
    }
    /// <summary>The anchor ids and the id of every form of the screen map, without repeats.</summary>
    static long[] WithAllForms(long[] anchors) {
      var set = new HashSet<long>(); var res = new List<long>();
      foreach (var id in anchors) if (id != 0 && set.Add(id)) res.Add(id);
      foreach (var id in AllFormIds()) if (id != 0 && set.Add(id)) res.Add(id);
      return res.ToArray();
    }

    /// <summary>UIInstance from any screen (two full passes). The anchors are the layout's forms and every form of the screen
    /// map by its id (the name's hash): whatever forms are open (the guild, the foundry... with the city destroyed) one of
    /// them is a key of UIInstance. UIFormClass is no help: the game clears the classes of the destroyed forms and a form
    /// required again is never registered again (live 2026-10-30 on the guild: 3 classes left, none of an open form).
    /// <paramref name="said"/> = the log line.</summary>
    internal static ulong FindUiIn(long[] anchors, out string said) {
      string why; ulong classes;
      List<ulong> nodes;
      var cands = UiCandidates(anchors, out nodes);
      ulong found = PickUiTable(cands, anchors, out why, out classes);
      said = "forms: window table " + (found != 0 ? "found from " + ShownNames(found, 3) : "not found") + " (by " + anchors.Length + " form ids: " + why + ")";
      if (found == 0 && Channel.IsTest && !uiDiagSaid) { uiDiagSaid = true; said += "\n" + UiCandidatesDiag(cands, anchors, 32) + "\n" + UiNodesDiag(nodes, cands, 8); }
      return found;
    }
    static bool uiDiagSaid;

    /// <summary>For the next lab run (test builds, once a process): the candidate tables of a failed search, at most
    /// <paramref name="maxLines"/> lines: the size of each, its first 5 keys and values by type, and whether a table value
    /// carries ____instanceId / class (with its __cname) / m_LuaLogicActive / __cname.</summary>
    internal static string UiCandidatesDiag(IList<ulong> cands, long[] ids, int maxLines) {
      var idSet = new HashSet<long>(); foreach (var id in ids) if (id != 0) { idSet.Add(id); if (id < 0) idSet.Add((long)(uint)id); }
      var lines = new List<string> { "  window table search: " + cands.Count + " candidate table(s) by " + ids.Length + " form ids" };
      foreach (var t in cands) {
        if (lines.Count >= maxLines) { lines.Add("  ..."); break; }
        var h = Read(t, 56);
        if (h == null || h[8] != 5 || h[11] > 12) { lines.Add("  " + t.ToString("X") + ": unreadable"); continue; }
        int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32);
        if (nb == null) { lines.Add("  " + t.ToString("X") + ": nodes unreadable"); continue; }
        int used = 0, anchors = 0; var first = new List<string>();
        for (int i = 0; i < nn; i++) {
          int o = i * 32, ktt = BitConverter.ToInt32(nb, o + 24), vtt = BitConverter.ToInt32(nb, o + 8);
          if (vtt == 0) continue;
          used++;
          long k = BitConverter.ToInt64(nb, o + 16);
          bool anchor = ktt == T_INT && idSet.Contains(k); if (anchor) anchors++;
          if (first.Count >= 5) continue;
          string d = "k" + ktt + (ktt == T_INT ? "=" + k + (anchor ? "*" : "") : "") + " v" + vtt;
          if (vtt == T_TABLE) d += "[" + TableMarks(BitConverter.ToUInt64(nb, o)) + "]";
          first.Add(d);
        }
        lines.Add("  " + t.ToString("X") + ": " + nn + " nodes, " + used + " used, " + anchors + " form ids, array " + BitConverter.ToInt32(h, 12) + "; " + string.Join(", ", first.ToArray()));
      }
      return string.Join("\n", lines.ToArray());
    }
    /// <summary>For the next lab run: the nodes keyed by a form id with a table value that lie in no candidate's node array (a
    /// table whose header the scan missed), at most <paramref name="max"/>: the key's form name and the value's marks.</summary>
    internal static string UiNodesDiag(IList<ulong> nodes, IList<ulong> cands, int max) {
      var inCands = new List<KeyValuePair<ulong, ulong>>();
      foreach (var t in cands) { var h = Read(t, 56); if (h != null && h[11] <= 12) { ulong np = BitConverter.ToUInt64(h, 24); inCands.Add(new KeyValuePair<ulong, ulong>(np, np + (ulong)(32 << h[11]))); } }
      var outside = new List<ulong>();
      foreach (var n in nodes) { bool inside = false; foreach (var r in inCands) if (n >= r.Key && n < r.Value) { inside = true; break; } if (!inside) outside.Add(n); }
      var lines = new List<string> { "  form id nodes: " + nodes.Count + ", " + outside.Count + " outside the candidates" };
      Dictionary<long, string> names = null;
      foreach (var n in outside) {
        if (lines.Count > max) { lines.Add("  ..."); break; }
        var nb = Read(n, 32); if (nb == null) { lines.Add("  " + n.ToString("X") + ": unreadable"); continue; }
        if (names == null) { names = new Dictionary<long, string>(); foreach (var e in ScreenMap.All) { long id = FormIdOf(e.Name); names[id] = e.Name; names[(long)(uint)id] = e.Name; } }
        long k = BitConverter.ToInt64(nb, 16); string nm;
        lines.Add("  " + n.ToString("X") + ": " + (names.TryGetValue(k, out nm) ? nm : "id " + k) + " next " + BitConverter.ToInt32(nb, 28) + " [" + TableMarks(BitConverter.ToUInt64(nb, 0)) + "]");
      }
      return string.Join("\n", lines.ToArray());
    }
    static string TableMarks(ulong f) {
      ulong v, c; int tt, ct; var m = new List<string>();
      if (Field(f, "____instanceId", out v, out tt)) m.Add("inst" + (tt == T_INT ? "" : ":" + tt));
      if (Field(f, "class", out c, out ct)) { string w, n = ct == T_TABLE ? FormClassName(f, out w) : ""; m.Add("class" + (n != "" ? "=" + n : ":" + ct)); }
      if (Field(f, "m_LuaLogicActive", out v, out tt)) m.Add("active" + (tt == T_BOOL ? "=" + ((v & 0xFFFFFFFF) != 0 ? "1" : "0") : ":" + tt));
      if (Field(f, "__cname", out v, out tt)) { var s = tt == T_SSTR || tt == T_LSTR ? ReadLuaString(v) : null; m.Add("cname" + (s != null && s.Length < 60 ? "=" + s : ":" + tt)); }
      return m.Count > 0 ? string.Join(" ", m.ToArray()) : "-";
    }

    /// <summary>The tables keyed by one of <paramref name="ids"/> at a node holding a table (at most 128): one pass for the nodes,
    /// one for the table headers whose node array holds one of them (an old array can linger after a rehash: the live table
    /// points at the array in use, so a table header found this way is the one). Any size: UIInstance shrinks to 1 or 2 nodes
    /// when the game destroys its forms (the guild alone open). <paramref name="nodes"/> = the nodes found.</summary>
    static List<ulong> UiCandidates(long[] ids, out List<ulong> nodes) {
      var idSet = new HashSet<long>();
      foreach (var id in ids) if (id != 0) { idSet.Add(id); if (id < 0) idSet.Add((long)(uint)id); }
      nodes = new List<ulong>(); var found = nodes;
      var cands = new List<ulong>();
      if (idSet.Count == 0) return cands;
      ScanParallel((b0, buf, len) => {
        List<ulong> mine = null;
        for (int i = 0; i + 32 <= len; i += 8) {
          if (BitConverter.ToInt32(buf, i + 24) != T_INT || BitConverter.ToInt32(buf, i + 8) != T_TABLE) continue;
          if (idSet.Contains(BitConverter.ToInt64(buf, i + 16))) (mine ?? (mine = new List<ulong>())).Add(b0 + (ulong)i);
        }
        if (mine != null) lock (found) found.AddRange(mine);
      });
      if (nodes.Count == 0) return cands;
      var ns = nodes.ToArray(); Array.Sort(ns);
      ScanParallel((b0, buf, len) => {
        for (int i = 0; i + 56 <= len; i += 8) {
          if (buf[i + 8] != 5) continue;
          int lsize = buf[i + 11]; if (lsize > 12) continue;
          ulong np = BitConverter.ToUInt64(buf, i + 24); if (np == 0) continue;
          ulong end = np + (ulong)(32 << lsize);
          int lo = 0, hi = ns.Length;
          while (lo < hi) { int mid = (lo + hi) >> 1; if (ns[mid] < np) lo = mid + 1; else hi = mid; }
          for (int j = lo; j < ns.Length && ns[j] < end; j++)
            if ((ns[j] - np) % 32 == 0) { lock (cands) { if (cands.Count < 128) cands.Add(b0 + (ulong)i); } break; }
        }
      });
      return cands;
    }

    /// <summary>The class names of the shown forms of a window table (at most <paramref name="max"/>), for the log.</summary>
    static string ShownNames(ulong t, int max) {
      var h = Read(t, 56); if (h == null) return "?";
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return "?";
      var names = new List<string>();
      for (int i = 0; i < nn && names.Count < max; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
        ulong f = BitConverter.ToUInt64(nb, o); if (!Shown(f)) continue;
        string w, n = FormClassName(f, out w); names.Add(n != "" ? n : "id:" + BitConverter.ToInt64(nb, o + 16));
      }
      return names.Count > 0 ? string.Join(", ", names.ToArray()) : "no shown form";
    }

    /// <summary>What a table keyed by form ids holds at the anchor ids: "forms" (form objects: UIInstance), "classes" (form classes:
    /// UIFormClass), "none" (no anchor id), "unreadable". <paramref name="shown"/> = anchor forms shown now.</summary>
    internal static string UiTableKind(ulong t, long[] ids, out int shown) { int n; return UiTableKind(t, ids, out shown, out n); }
    internal static string UiTableKind(ulong t, long[] ids, out int shown, out int forms) {
      shown = 0; forms = 0;
      var h = t != 0 ? Read(t, 56) : null; if (h == null || h[8] != 5 || h[11] > 12) return "unreadable";
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return "unreadable";
      int classes = 0;
      HashSet<long> idSet = null;
      if (ids.Length > 16) { idSet = new HashSet<long>(); foreach (var id in ids) if (id != 0) { idSet.Add(id); if (id < 0) idSet.Add((long)(uint)id); } }
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
        long k = BitConverter.ToInt64(nb, o + 16);
        if (idSet != null && !idSet.Contains(k)) continue;
        bool anchor = idSet != null; if (!anchor) foreach (var id in ids) if (id != 0 && SameId(k, id)) { anchor = true; break; }
        if (!anchor) continue;
        ulong f = BitConverter.ToUInt64(nb, o), v; int tt;
        if (Field(f, "____instanceId", out v, out tt) && tt == T_INT) { string w; if (FormClassName(f, out w).StartsWith("Form_", StringComparison.Ordinal)) { forms++; if (Shown(f)) shown++; } }
        else if (Field(f, "__cname", out v, out tt) && (tt == T_SSTR || tt == T_LSTR)) classes++;
      }
      return forms > 0 ? "forms" : classes > 0 ? "classes" : "none";
    }

    /// <summary>Of the tables found by the anchor nodes, UIInstance: one that holds form objects, the one with a shown form first;
    /// 0 = none of them. <paramref name="why"/> = the verdicts, for the log.</summary>
    internal static ulong PickUiTable(IList<ulong> cands, long[] ids, out string why) { ulong c; return PickUiTable(cands, ids, out why, out c); }
    /// <summary>The same; <paramref name="classes"/> = the first table of form classes (UIFormClass), 0 = none.</summary>
    internal static ulong PickUiTable(IList<ulong> cands, long[] ids, out string why, out ulong classes) {
      ulong best = 0; int bestShown = -1, bestForms = 0; int nf = 0, nc = 0, nx = 0; classes = 0;
      foreach (var c in cands) {
        int sh, fm; string kind = UiTableKind(c, ids, out sh, out fm);
        if (kind == "forms") { nf++; if (sh > bestShown || (sh == bestShown && fm > bestForms)) { best = c; bestShown = sh; bestForms = fm; } }
        else if (kind == "classes") { nc++; if (classes == 0) classes = c; } else nx++;
      }
      why = cands.Count + " candidate(s): forms " + nf + ", classes " + nc + ", other " + nx;
      return best;
    }

    /// <summary>For the tests: the table picked from <paramref name="cands"/> as UIInstance (0 = none).</summary>
    public static ulong PickUiTableForTest(IList<ulong> cands, long[] ids) { string w; return PickUiTable(cands, ids, out w); }
    /// <summary>For the tests: the diagnostic lines of a failed search over these candidate tables.</summary>
    public static string UiCandidatesDiagForTest(IList<ulong> cands, long[] ids, int maxLines) { return UiCandidatesDiag(cands, WithAllForms(ids), maxLines); }
    /// <summary>For the tests: the node lines of the diagnostic over the test memory, by these anchor ids and every screen-map form's.</summary>
    public static string UiNodesDiagForTest(long[] anchors) { regs = Regions(); List<ulong> nodes; var ids = WithAllForms(anchors); var c = UiCandidates(ids, out nodes); return UiNodesDiag(nodes, c, 8); }
    /// <summary>For the tests: the full search over the test memory by these anchor ids and every screen-map form's, with its log line
    /// (the test build's one-shot diagnostic of a failed search armed again).</summary>
    public static ulong FindUiTableForTest(long[] anchors, out string said) { regs = Regions(); uiDiagSaid = false; return FindUiIn(WithAllForms(anchors), out said); }

    /// <summary>UIInstance's address (0 = not found yet), for the log.</summary>
    public static ulong UiAddress { get { return uiTable; } }

    /// <summary>UIInstance is known and still a table (cheap).</summary>
    public static bool UiKnown { get { return uiTable != 0 && IsTable(uiTable); } }
    /// <summary>Looks for UIInstance (a full pass, about a second; at most every <paramref name="rescanMs"/>): call it off
    /// the interface thread.</summary>
    public static void FindUi(int rescanMs) { UiTable(rescanMs); }

    static bool IsTable(ulong t) { var h = t != 0 ? Read(t, 56) : null; return h != null && h[8] == 5 && h[11] <= 12; }

    /// <summary>The result screens held by the game now, each with a mark of its showing: the table, its instance id and
    /// the fight's frame count. The game keeps a closed result screen in UIInstance, so a screen being there is not a new
    /// fight - a changed mark is. Empty when UIInstance is not known yet (<see cref="FindUi"/>). Cheap.</summary>
    public static List<KeyValuePair<int, ulong>> BattleEndForms(out List<string> marks) {
      var res = new List<KeyValuePair<int, ulong>>(); marks = new List<string>();
      ulong t = UiKnown ? uiTable : 0; if (t == 0) return res;
      var endIds = FormIds(EndFormKeys); if (endIds.Length == 0) return res;
      var h = Read(t, 56); if (h == null) return res;
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return res;
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
        long k = BitConverter.ToInt64(nb, o + 16);
        for (int e = 0; e < endIds.Length; e++) {
          if (endIds[e] == 0 || !SameId(k, endIds[e])) continue;
          ulong f = BitConverter.ToUInt64(nb, o), v; int tt;
          long inst = Field(f, "____instanceId", out v, out tt) && tt == T_INT ? (long)v : 0;
          long frames = Field(f, "m_FightFramIdx", out v, out tt) && tt == T_INT ? (long)v : 0;
          long stg = Field(f, "m_StageId", out v, out tt) && tt == T_INT ? (long)v : 0;
          long fuid = Field(f, "m_FightUid", out v, out tt) && tt == T_INT ? (long)v : 0;
          if (EndFormNeverShown(stg, fuid, frames)) continue;
          res.Add(new KeyValuePair<int, ulong>(e, f));
          marks.Add(e + ":" + f.ToString("X") + ":" + inst + ":" + frames + ":" + fuid);
        }
      }
      return res;
    }

    /// <summary>A result screen object the battle loader preloaded and nobody opened: no stage, no fight uid, no frames.</summary>
    public static bool EndFormNeverShown(long stage, long fightUid, long frames) { return stage == 0 && fightUid == 0 && frames == 0; }

    /// <summary>One fight as JSON: the result screen's name and its plain fields, the heroes' battle statistics and the
    /// simulations' lengths (DumpBattleStats). Null when nothing was found.</summary>
    public static string CaptureBattle(int endKind, ulong endForm) { return CaptureBattle(endKind, endForm, null); }

    /// <summary>The same with the fight's timeline (FightRecorder.Json, src/BattleTimeline.cs) as "timeline".</summary>
    public static string CaptureBattle(int endKind, ulong endForm, string timeline) {
      string stats = DumpBattleStats();
      if (string.IsNullOrEmpty(stats)) return null;
      var sb = new StringBuilder("{\"at\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",\"screen\":\"" + (endKind >= 0 ? EndFormNames[endKind] : "manual") + "\"");
      // the result screen's own plain values (score, damage, boss, stage…: whatever it keeps)
      if (endForm != 0) {
        var d = ParseTable(endForm, 1, new HashSet<ulong>());
        var plain = new Dictionary<string, object>();
        if (d != null) foreach (var kv in d) if (kv.Value is long || kv.Value is double || kv.Value is bool || (kv.Value is string && ((string)kv.Value).Length < 80)) plain[kv.Key] = kv.Value;
        sb.Append(",\"end\":"); J(sb, plain);
      }
      sb.Append(",\"lines\":[");
      bool first = true;
      // each hero line marked with its side ("c": its controller) when the timeline read the same statistics object by side
      var sides = TimelineBuilder.StatSides(timeline);
      foreach (var line in stats.Split('\n')) {
        if (line.Trim().Length == 0) continue;
        if (!first) sb.Append(','); first = false;
        sb.Append(TimelineBuilder.MarkSide(line.Trim(), sides));
      }
      sb.Append(']');
      if (!string.IsNullOrEmpty(timeline) && timeline[0] == '{') sb.Append(",\"timeline\":").Append(timeline);
      sb.Append('}');
      return sb.ToString();
    }

    /// <summary>The stage the result screen itself names (Form_BattleEnd.m_StageId, a plain value of the screen kept in the
    /// fight's "end"), or 0 when the fight has none or it is out of the 7-digit range.</summary>
    public static int StageFromEnd(string fightJson) {
      var o = MiniJson.AsObject(MiniJson.TryParse(fightJson));
      var e = o != null && o.ContainsKey("end") ? o["end"] as Dictionary<string, object> : null;
      object v; if (e == null || !e.TryGetValue("m_StageId", out v) || !(v is double)) return 0;
      double x = (double)v;
      return x >= 1000000 && x <= 9999999 && Math.Floor(x) == x ? (int)x : 0;
    }

    /// <summary>The stage id a captured fight gets. Sources, best first: the result screen's own stage (formStage);
    /// the stage of the fight's start (lastStage, when it is not older than 20 minutes and the simulation it was read from
    /// is the one the battle view holds now, or no simulation is known); the simulation's stage read at the result screen
    /// (simStage: _BattleData.iStageID of the battle view's last simulation, which stays in memory after the fight). A
    /// start that was missed leaves an older fight's stage: then the simulation's, being another object, wins.</summary>
    public static int PickStage(int formStage, int simStage, bool simIsNew, int lastStage, double lastAgeMin) {
      if (formStage > 0) return formStage;
      bool lastOk = lastStage > 0 && lastAgeMin < 20;
      if (simStage > 0 && (simIsNew || !lastOk)) return simStage;
      return lastOk ? lastStage : 0;
    }

    /// <summary>Diagnostics: finds UIInstance and lists its forms (id, and the name when known).</summary>
    public static string DiagUi() {
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) return "no game\n";
      H = OpenProcess(0x0410, false, ps[0].Id);
      if (H == IntPtr.Zero) return "open failed\n";
      regs = Regions(); uiTable = 0; uiLastFind = 0;
      FindUi(0);
      var sb = new StringBuilder("ui table " + uiTable.ToString("X") + "\n");
      if (uiTable == 0) return sb.ToString();
      var h = Read(uiTable, 56); int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32);
      var ends = FormIds(EndFormKeys); long battleId = FormId("ID_FORM_BATTLE"), mainId = FormCharactorMainId, invId = FormBackpackId;
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_INT) continue;
        long k = BitConverter.ToInt64(nb, o + 16);
        string name = mainId != 0 && k == mainId ? "Form_CharactorMain" : invId != 0 && k == invId ? "Form_BackpackIntegration" : "";
        for (int e = 0; e < ends.Length; e++) if (SameId(k, ends[e])) name = EndFormNames[e];
        if (battleId != 0 && SameId(k, battleId)) name = "Form_Battle";
        sb.Append(k).Append(' ').Append(BitConverter.ToInt32(nb, o + 8) == T_TABLE ? "table " : "other ").Append(name).Append('\n');
      }
      List<string> marks; BattleEndForms(out marks); sb.Append("battle end marks: ").Append(string.Join(" ", marks.ToArray())).Append('\n');
      return sb.ToString();
    }

    public static string BattlesDir { get { return Path.Combine(AppConfig.DefaultDir, "battles"); } }

    /// <summary>Keeps a captured fight (the last 30 files stay).</summary>
    public static string SaveBattle(string json) {
      Directory.CreateDirectory(BattlesDir);
      // a replay of a kept fight (the game's battle report shows it again): the same heroes' statistics to the unit —
      // not a new fight, not kept (it would count twice in the site's checks and the crowd's calibration)
      string sig = StatsSignature(json);
      if (sig.Length > 0)
        foreach (var f in Directory.GetFiles(BattlesDir, "*.json"))
          try { if (StatsSignature(File.ReadAllText(f, Encoding.UTF8)) == sig) { L("battle: a replay of " + Path.GetFileName(f) + ", not kept"); return null; } } catch (Exception) { }
      string path = Path.Combine(BattlesDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
      File.WriteAllText(path, json, new UTF8Encoding(false));
      var files = Directory.GetFiles(BattlesDir, "*.json"); Array.Sort(files);
      for (int i = 0; i < files.Length - 30; i++) try { File.Delete(files[i]); } catch (Exception) { }
      return path;
    }

    /// <summary>The account as it was for a kept fight (the sync's reading), in battles\accounts\ under the fight's name:
    /// with the fight's statistics and timeline it lets the site's simulation be checked against the real fight. Not in
    /// BattlesJson (a subfolder). The last 30 are kept, as the fights.</summary>
    public static void SaveBattleAccount(string battlePath, string accountJson) {
      string dir = Path.Combine(BattlesDir, "accounts");
      Directory.CreateDirectory(dir);
      File.WriteAllText(Path.Combine(dir, Path.GetFileName(battlePath)), accountJson, new UTF8Encoding(false));
      var files = Directory.GetFiles(dir, "*.json"); Array.Sort(files);
      for (int i = 0; i < files.Length - 30; i++) try { File.Delete(files[i]); } catch (Exception) { }
    }

    /// <summary>The heroes' battle statistics of a kept fight as one string (hero, damage, heal, taken), sorted; empty
    /// when there are none.</summary>
    public static string StatsSignature(string json) {
      var m = System.Text.RegularExpressions.Regex.Matches(json ?? "", "\"iHeroID\":(\\d+)[^{}]*?\"damage\":(\\d+)[^{}]*?\"heal\":(\\d+)[^{}]*?\"taken\":(\\d+)");
      var parts = new List<string>();
      foreach (System.Text.RegularExpressions.Match x in m) parts.Add(x.Groups[1].Value + ":" + x.Groups[2].Value + ":" + x.Groups[3].Value + ":" + x.Groups[4].Value);
      if (parts.Count == 0) return "";
      parts.Sort(StringComparer.Ordinal);
      return string.Join(",", parts.ToArray());
    }

    /// <summary>The kept fights as a JSON array (for the account snapshot), newest last; the timelines without their v3 trace.</summary>
    static string BattlesJson() {
      try {
        if (!Directory.Exists(BattlesDir)) return "[]";
        var files = Directory.GetFiles(BattlesDir, "*.json"); Array.Sort(files);
        var sb = new StringBuilder("["); bool first = true;
        foreach (var f in files) {
          string s = File.ReadAllText(f, Encoding.UTF8).Trim();
          if (s.Length == 0 || s[0] != '{') continue;
          if (!first) sb.Append(','); first = false;
          sb.Append(TimelineBuilder.StripTrace(s));   // the local file keeps the v3 trace (dmgT, units); the upload copy does not, for now
        }
        return sb.Append(']').ToString();
      } catch (Exception) { return "[]"; }
    }
  }
}
