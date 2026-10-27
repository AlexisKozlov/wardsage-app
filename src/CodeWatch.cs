// Wardsage - a read-only look at the game's code-redeem window (for the promo-code setting, app/CodeWatch.cs).
// While the window is open the app reads the text in its input field and the short messages ("tips") the game shows,
// and notes which other windows open right after; app/CodeWatch.cs logs it. Nothing is written to the game, nothing is clicked.
// Reads go by the layout data of the running game version (src/Layout.cs: the form id, the "field" offsets); without them
// nothing is read.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RealmForge {
  /// <summary>What a promo code looks like: letters and digits, 4 to 24.</summary>
  public static class CodeText {
    static readonly Regex Pattern = new Regex("^[A-Za-z0-9]{4,24}$");

    /// <summary>The text (spaces around it ignored) looks like a code.</summary>
    public static bool Looks(string s) { return s != null && Pattern.IsMatch(s.Trim()); }

    /// <summary>A message of the game for the log: one line, quotes and control characters removed, at most 160 characters.</summary>
    public static string ForLog(string s) {
      if (s == null) return "";
      var sb = new StringBuilder();
      foreach (char c in s) {
        if (sb.Length >= 160) break;
        sb.Append(c < ' ' || c == '"' ? ' ' : c);
      }
      return sb.ToString().Trim();
    }
  }

  /// <summary>One look at the code window (<see cref="RFX.ReadCdk"/>).</summary>
  public sealed class CdkRead {
    public bool Known;                                  // the game's windows were read (false = not yet / no layout data)
    public string Why;                                  // when not Known: the reason
    public bool Open;                                   // the window is open now
    public string Input;                                // the input field's text, null = unreadable
    public List<string> Tips = new List<string>();      // the messages the game shows now
    public HashSet<int> Active;                         // ids of the windows open now (only when asked for)
  }

  public static partial class RFX {
    static ulong[] tipForms = new ulong[0]; static int tipsPid, tipsScanAt;

    /// <summary>Reads the code-redeem window: open or not, its input text and the messages shown. <paramref name="deep"/> = also
    /// the shown messages and the ids of all open windows (only while something is going on: it costs more reads). Call off the
    /// interface thread.</summary>
    public static CdkRead ReadCdk(bool deep) {
      var r = new CdkRead();
      var ly = Layout.Current;
      long id = ly != null ? ly.FormOpt("ID_FORM_PLAYERCDK") : 0;
      if (id == 0) { r.Why = "the layout data has no code window"; return r; }
      ulong t = UiKnown ? uiTable : 0;
      if (t == 0) { r.Why = "the game's windows are not found yet"; return r; }
      var h = Read(t, 56); if (h == null) { r.Why = "the windows table is unreadable"; return r; }
      int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) { r.Why = "the windows table is unreadable"; return r; }
      r.Known = true;
      if (deep) r.Active = new HashSet<int>();
      ulong cdk = 0;
      for (int i = 0; i < nn; i++) {
        int o = i * 32;
        if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
        long k = BitConverter.ToInt64(nb, o + 16);
        ulong f = BitConverter.ToUInt64(nb, o), v; int tt;
        bool isCdk = SameId(k, id);
        if (isCdk) cdk = f;
        if (!isCdk && !deep) continue;
        bool active = Field(f, "m_LuaLogicActive", out v, out tt) && tt == T_BOOL && v != 0;
        if (isCdk) r.Open = active;
        if (deep && active) r.Active.Add(unchecked((int)k));
      }
      if (!deep && (cdk == 0 || !r.Open)) return r;
      var ps = GameInfo.GameProcesses();
      if (ps.Length == 0) return r;
      int pid = ps[0].Id;
      // the input field: the window's script table holds it under m_CDKName_InputField
      int oText = ly.FieldOffset("InputField.m_Text");
      if (cdk != 0 && r.Open && oText >= 0) {
        ulong input = ScriptObject(cdk, "m_CDKName_InputField", "UnityEngine.UI.InputField", pid);
        if (input != 0) r.Input = ManagedString(Ptr(input + (ulong)oText));
      }
      if (deep) ReadTips(ly, pid, r.Tips);
      return r;
    }

    /// <summary>The rewards window (the list of obtained items) as it is shown now: item id and amount, from its own list
    /// (<c>m_ItemList</c>) and, while the window still has it, the list of new heroes (<c>m_HeroList</c>, one each). Null = the window is not
    /// open / not readable / holds nothing usable. Read-only, bounded (at most 40 entries per list), never throws.</summary>
    public static List<KeyValuePair<long, long>> ReadRewardWindow() {
      try {
        var ly = Layout.Current;
        long id = ly != null ? ly.FormOpt("ID_FORM_ITEMREWARD") : 0;
        ulong t = UiKnown ? uiTable : 0;
        if (id == 0 || t == 0) return null;
        var h = Read(t, 56); if (h == null || h[8] != 5 || h[11] > 12) return null;
        int nn = 1 << h[11]; var nb = Read(BitConverter.ToUInt64(h, 24), nn * 32); if (nb == null) return null;
        ulong form = 0;
        for (int i = 0; i < nn; i++) {
          int o = i * 32;
          if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
          if (SameId(BitConverter.ToInt64(nb, o + 16), id)) { form = BitConverter.ToUInt64(nb, o); break; }
        }
        ulong v; int tt;
        if (form == 0 || !Field(form, "m_LuaLogicActive", out v, out tt) || tt != T_BOOL || v == 0) return null;
        var res = new List<KeyValuePair<long, long>>();
        if (Field(form, "m_ItemList", out v, out tt) && tt == T_TABLE) {
          int seen = 0;
          foreach (var e in IntEntries(v)) {
            if (++seen > 40) break;
            long iid, num;
            if (e.Tt == T_TABLE && ArtInt(e.Val, "iID", out iid) && ArtInt(e.Val, "iNum", out num)) res.Add(new KeyValuePair<long, long>(iid, num));
          }
        }
        if (Field(form, "m_HeroList", out v, out tt) && tt == T_TABLE) {
          int seen = 0;
          foreach (var e in IntEntries(v)) {
            if (++seen > 40) break;
            long iid;
            if ((e.Tt == T_INT || e.Tt == T_FLT) && AsLong(e.Val, e.Tt, out iid)) res.Add(new KeyValuePair<long, long>(iid, 1));
            else if (e.Tt == T_TABLE && ArtInt(e.Val, "iID", out iid)) res.Add(new KeyValuePair<long, long>(iid, 1));
          }
        }
        return res.Count > 0 ? res : null;
      } catch (Exception) { return null; }
    }

    /// <summary>The messages shown by the game's tip form now (its lists of tips on the screen).</summary>
    static void ReadTips(GameLayout ly, int pid, List<string> into) {
      int oMove = ly.FieldOffset("Form_CommonTips.m_MoveTips"), oShow = ly.FieldOffset("Form_CommonTips.m_ShowTips"),
          oGone = ly.FieldOffset("Form_CommonTips.m_DisappearTips"), oStr = ly.FieldOffset("Form_CommonTips.CommonData.str");
      if (oMove < 0 || oShow < 0 || oGone < 0 || oStr < 0 || ly.Rva("Form_CommonTips") == 0) return;
      if (pid != tipsPid) { tipsPid = pid; tipForms = new ulong[0]; tipsScanAt = 0; }
      ulong ga = GameAssemblyOf(pid), klass = ClassAt(ga, "Form_CommonTips");
      if (klass == 0) return;
      // the tip form is found once (a pass over the memory for objects of its class) and checked again on every look
      bool ok = tipForms.Length > 0;
      foreach (var f in tipForms) if (Ptr(f) != klass) ok = false;
      if (!ok) {
        if (tipsScanAt != 0 && Environment.TickCount - tipsScanAt < 20000) return;
        tipsScanAt = Environment.TickCount;
        regs = Regions();
        var found = new List<ulong>();
        ScanParallel((b0, buf, len) => {
          for (int i = 0; i + 8 <= len; i += 8)
            if (BitConverter.ToUInt64(buf, i) == klass) lock (found) found.Add(b0 + (ulong)i);
        });
        var good = new List<ulong>();
        foreach (var f in found) if (good.Count < 8 && Ptr(f + 0x1E8) != 0) good.Add(f);   // m_canvas set: a form that was set up
        tipForms = good.ToArray();
      }
      int arrData = ly.Hdr("arrData");
      foreach (var f in tipForms)
        foreach (int off in new[] { oMove, oShow, oGone }) {
          ulong list = Ptr(f + (ulong)off), items = list != 0 ? Ptr(list + 0x10) : 0;
          var sb = list != 0 ? Read(list + 0x18, 4) : null;
          if (items == 0 || sb == null) continue;
          int n = BitConverter.ToInt32(sb, 0); if (n < 0 || n > 16) continue;
          for (int i = 0; i < n; i++) {
            ulong cd = Ptr(items + (ulong)arrData + (ulong)i * 8); if (cd == 0) continue;
            string s = ManagedString(Ptr(cd + (ulong)oStr));
            if (!string.IsNullOrEmpty(s) && !into.Contains(s)) into.Add(s);
          }
        }
    }

    /// <summary>A .NET string at an object address (length at 0x10, UTF-16 characters from 0x14); null when unreadable.</summary>
    static string ManagedString(ulong p) {
      if (p == 0) return null;
      var lb = Read(p + 0x10, 4); if (lb == null) return null;
      int n = BitConverter.ToInt32(lb, 0);
      if (n < 0 || n > 400) return null;
      if (n == 0) return "";
      var cb = Read(p + 0x14, n * 2); if (cb == null) return null;
      return Encoding.Unicode.GetString(cb);
    }
  }
}
