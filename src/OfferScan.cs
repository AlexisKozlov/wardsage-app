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
      if (formNamesOf != uiTable) { formNames.Clear(); formNamesOf = uiTable; }
      string n;
      if (formNames.TryGetValue(key, out n)) return n;
      if (budget <= 0) return "";
      budget--;
      n = FormClassName(f);
      if (n != "") formNames[key] = n;
      return n;
    }

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

    /// <summary>The class name of a form ("" = not readable).</summary>
    static string FormClassName(ulong f) {
      ulong c, v; int tt;
      if (f == 0 || !Field(f, "class", out c, out tt) || tt != T_TABLE || c == 0) return "";
      if (!Field(c, "__cname", out v, out tt) || (tt != T_SSTR && tt != T_LSTR)) return "";
      var s = ReadLuaString(v);
      return s != null && s.Length < 80 ? s : "";
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
          if (OfferForms.IsOffer(n) || Array.IndexOf(OfferForms.KnownScreens, n) >= 0) continue;
          res.Add(n);
        }
      }
      return res;
    }
  }
}
