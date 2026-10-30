// Wardsage - which of the game's offer / notice pop-ups (src/OfferForms.cs) is shown now, read from the game's window table
// (UIStatic's UIInstance: form id -> form, src/BattleCapture.cs). READ ONLY: a form is shown while its m_LuaLogicActive is true
// (UIBase:OnActive sets it, OnPreInactive clears it; a closed form stays in the table). A form is known by its class name
// (instance.class.__cname, common/functions.lua class()): read once per key of the table and kept, so a tick reads the table's
// nodes and the shown flag of the offer forms only.
using System;
using System.Collections.Generic;

namespace RealmForge {
  public static partial class RFX {
    static readonly Dictionary<long, string> formNames = new Dictionary<long, string>();
    static ulong formNamesOf;
    const int NewNamesPerRead = 24;   // class names read per call at most (the first reads of a new table are spread over a few ticks)

    static byte[] UiNodes(out int nn) {
      nn = 0;
      ulong t = UiKnown ? uiTable : 0; if (t == 0) return null;
      var h = Read(t, 56); if (h == null || h[8] != 5) return null;
      nn = 1 << h[11];
      return Read(BitConverter.ToUInt64(h, 24), nn * 32);
    }

    /// <summary>The class name of the form under a key of the table (cached; "" = not readable yet / not a form).</summary>
    static string NameOf(long key, ulong f, ref int budget) {
      if (formNamesOf != uiTable) { formNames.Clear(); formNameMiss.Clear(); formNamesOf = uiTable; }
      string n;
      if (formNames.TryGetValue(key, out n)) return n;
      NameMiss miss;
      if (formNameMiss.TryGetValue(key, out miss) && unchecked(Environment.TickCount - miss.At) < NameRetryDelay(miss.N)) return "";
      if (budget <= 0) return "";
      budget--;
      string why;
      n = FormClassName(f, out why);
      int now = Environment.TickCount;
      if (n != "") {
        formNames[key] = n; formNameMiss.Remove(key);
        if (miss != null) LL("forms: " + n + " named " + unchecked(now - miss.First) + " ms after its first unreadable try (" + miss.N + " misses)");
      } else if (miss == null) formNameMiss[key] = new NameMiss { First = now, At = now, N = 1 };
      else { miss.At = now; miss.N++; }
      return n;
    }
    // a name that could not be read is tried again after NameRetryDelay: soon at first (a form the game has just built gets its class a
    // moment later - live 1.6.66.391: one try in 10 s left a new screen unnamed for 9 s), then ever more seldom up to NameRetryMs: the
    // failing ones must not take the whole budget of every call (the forms after them in the table would never be named)
    sealed class NameMiss { public int First, At, N; }
    static readonly Dictionary<long, NameMiss> formNameMiss = new Dictionary<long, NameMiss>();
    const int NameRetryMs = 10000, NameRetryFirstMs = 100;
    /// <summary>The wait before a form's name is tried again after <paramref name="misses"/> unreadable tries: 100, 200, 400 ... ms, at most NameRetryMs.</summary>
    public static int NameRetryDelay(int misses) { return misses <= 0 ? 0 : misses > 7 ? NameRetryMs : Math.Min(NameRetryMs, NameRetryFirstMs << (misses - 1)); }

    /// <summary>The offer pop-ups shown now (their names, in the table's order); null = the window table is not known or not readable.</summary>
    public static List<string> ReadOffers() {
      int nn; var nb = UiNodes(out nn); if (nb == null) return null;
      var res = new List<string>();
      int budget = NewNamesPerRead;
      lock (formNames) {
        for (int i = 0; i < nn; i++) {
          int o = i * 32;
          if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
          ulong f = BitConverter.ToUInt64(nb, o);
          string n = NameOf(BitConverter.ToInt64(nb, o + 16), f, ref budget);
          if (n != "" && OfferForms.IsOffer(n) && Shown(f)) res.Add(n);
        }
      }
      return res;
    }

    static bool Shown(ulong f) { ulong v; int tt; return f != 0 && Field(f, "m_LuaLogicActive", out v, out tt) && tt == T_BOOL && (v & 0xFFFFFFFF) != 0; }

    /// <summary>The class name of a form ("" = not readable, <paramref name="why"/> says which step failed).</summary>
    static string FormClassName(ulong f, out string why) {
      ulong c, v; int tt;
      why = "";
      if (f == 0) { why = "no form"; return ""; }
      if (!Field(f, "class", out c, out tt)) { why = Field(f, "__cname", out v, out tt) ? "a class, not a form" : "no class field"; return ""; }
      if (tt != T_TABLE || c == 0) { why = "class is type " + tt; return ""; }
      if (!Field(c, "__cname", out v, out tt)) { why = "no __cname"; return ""; }
      if (tt != T_SSTR && tt != T_LSTR) { why = "__cname is type " + tt; return ""; }
      var s = ReadLuaString(v);
      if (s == null) { why = "__cname unreadable"; return ""; }
      if (s.Length >= 80) { why = "__cname too long"; return ""; }
      return s;
    }

    /// <summary>For the log while a pilot stands on an unknown screen: the shown forms of the window table by class name (or why a name is
    /// missing), at most <paramref name="max"/>; or why the table cannot be read. Reads a few fields per form: call it seldom.</summary>
    public static string ShownFormsDiag(int max) {
      if (uiTable == 0) return "window table not found yet";
      if (!UiKnown) return "window table lost (no longer a table)";
      int nn; var nb = UiNodes(out nn); if (nb == null) return "window table unreadable";
      int forms = 0, shown = 0, objs = 0, probed = 0; var names = new List<string>();
      lock (formNames) {
        for (int i = 0; i < nn; i++) {
          int o = i * 32;
          if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
          forms++;
          ulong f = BitConverter.ToUInt64(nb, o), v; int tt;
          if (probed < 8) { probed++; if (Field(f, "____instanceId", out v, out tt) && tt == T_INT) objs++; }
          if (!Shown(f)) continue;
          shown++;
          if (names.Count >= max) continue;
          string why, n = FormClassName(f, out why);
          names.Add(n != "" ? n : "id:" + BitConverter.ToInt64(nb, o + 16) + "(" + why + ")");
        }
      }
      return "window table: " + forms + " forms, " + shown + " shown" + (probed > 0 && objs == 0 ? " (holds no form objects: not UIInstance?)" : "")
        + (names.Count > 0 ? ": " + string.Join(", ", names.ToArray()) : "");
    }

    /// <summary>The shown forms that are screens the app knows (OfferForms.KnownScreens, the battle among them) other than the tip toast:
    /// "" = none, null = the window table is not known / not readable. The unknown-screen back press is never made while one is shown.</summary>
    public static string ReadShownKnownForms() {
      int nn; var nb = UiNodes(out nn); if (nb == null) return null;
      var res = new List<string>();
      int budget = NewNamesPerRead;
      lock (formNames) {
        for (int i = 0; i < nn; i++) {
          int o = i * 32;
          if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
          ulong f = BitConverter.ToUInt64(nb, o);
          if (!Shown(f)) continue;
          string n = NameOf(BitConverter.ToInt64(nb, o + 16), f, ref budget);
          if (n == "") return null;   // a shown form without a name could be a known one: not sure, so not "none"
          if (n != "Form_CommonTips" && Array.IndexOf(OfferForms.KnownScreens, n) >= 0) res.Add(n);
        }
      }
      return string.Join(",", res.ToArray());
    }

    /// <summary>The shown forms of the window table that are screens the app reads (ScreenState.KindOfForm), by class name: the screen's kind
    /// each tick (ScreenInputs.Screens). Null = the window table is not known or not readable. A read that finds no form at all (the node
    /// array read while the game rehashes the table) is tried once more at once. A shown form whose name is not read yet is left out.</summary>
    public static List<string> ReadShownScreens() {
      for (int attempt = 0; attempt < 2; attempt++) {
        int nn; var nb = UiNodes(out nn); if (nb == null) continue;
        var res = new List<string>(); int forms = 0;
        int budget = NewNamesPerRead;
        lock (formNames) {
          for (int i = 0; i < nn; i++) {
            int o = i * 32;
            if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
            forms++;
            ulong f = BitConverter.ToUInt64(nb, o);
            if (!Shown(f)) continue;
            string n = NameOf(BitConverter.ToInt64(nb, o + 16), f, ref budget);
            if (ScreenState.KindOfForm(n) != ScreenKind.Unknown) res.Add(n);
          }
        }
        if (forms > 0) return res;
      }
      return null;
    }

    /// <summary>The shown forms that are neither a screen the app knows (OfferForms.KnownScreens) nor an offer, by class name ("id:&lt;key&gt;"
    /// when the name is not readable); at most <paramref name="max"/>. Null = the window table is not known. A field read per form held:
    /// call it seldom (every few seconds, only while it is wanted).</summary>
    public static List<string> ReadUnknownShownForms(int max) {
      int nn; var nb = UiNodes(out nn); if (nb == null) return null;
      var res = new List<string>();
      int budget = 4 * NewNamesPerRead;
      lock (formNames) {
        for (int i = 0; i < nn && res.Count < max; i++) {
          int o = i * 32;
          if (BitConverter.ToInt32(nb, o + 24) != T_INT || BitConverter.ToInt32(nb, o + 8) != T_TABLE) continue;
          long k = BitConverter.ToInt64(nb, o + 16);
          ulong f = BitConverter.ToUInt64(nb, o);
          if (!Shown(f)) continue;
          string n = NameOf(k, f, ref budget);
          if (n == "") n = "id:" + k;
          if (OfferForms.IsOffer(n) || Array.IndexOf(OfferForms.KnownScreens, n) >= 0 || OfferForms.IsIgnoredOverlay(n)) continue;
          res.Add(n);
        }
      }
      return res;
    }
  }
}
