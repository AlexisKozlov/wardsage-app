// Wardsage - the TEST build's recorder keeps, next to every frame, the raw inputs the app's pure decision code ran on, so the
// code can be run again on a server without the game (tests/RecReplay.cs) and its answers drawn over the recorded pictures.
// Pure: JSON writers with their parsers (round-trip tested), the per-tick context, the queue of the screen reads, the per-line
// encoder (a slot is written only when it changed since the last line of the part), and the scene rectangles of the guides.
//
// A line's "in" object: {"tn": overlay clock of the tick, "reads":[{"at":clock, "base":1?, "r.inv":.., "r.invList":.., "r.rs":.., "r.rsLists":..,
// "r.hero":.., "r.flags":..}], "ctx":{..}, "env":{..}, "sinv":.., "sinvList":.., "fd":{..}}. A missing "r.*" / "ctx" / "env" / "sinv*" slot
// = the same as the last one written (every part starts with all slots again), "fd" is only there when the tick had it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealmForge {
  /// <summary>JSON of the readers' results (what ScreenInputs is made of) and back.</summary>
  public static class RecIn {
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string D(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", Inv); }
    static string B(bool v) { return v ? "true" : "false"; }
    static string L(long v) { return v.ToString(Inv); }
    static string Arr(IEnumerable<long> a) {
      if (a == null) return "null";
      var sb = new StringBuilder("[");
      bool first = true;
      foreach (var x in a) { if (!first) sb.Append(','); first = false; sb.Append(x.ToString(Inv)); }
      return sb.Append(']').ToString();
    }
    static string Arr(int[] a) { if (a == null) return "null"; var l = new List<long>(); foreach (var x in a) l.Add(x); return Arr(l); }

    /// <summary>A short fingerprint of a data file (the first 6 bytes of its SHA-1, hex); "" for none.</summary>
    public static string Hash(string text) {
      if (text == null) return "";
      using (var sha = System.Security.Cryptography.SHA1.Create()) {
        var h = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text)); var sb = new StringBuilder();
        for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
      }
    }

    // ---- parse helpers
    public static Dictionary<string, object> O(object o) { return o as Dictionary<string, object>; }
    static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
    public static double Num(Dictionary<string, object> d, string k, double def) { var v = Get(d, k); return v is double ? (double)v : def; }
    public static bool Bool(Dictionary<string, object> d, string k, bool def) { var v = Get(d, k); return v is bool ? (bool)v : def; }
    public static string Str(Dictionary<string, object> d, string k) { return Get(d, k) as string; }
    static long[] Longs(object o, bool nullIfAbsent) {
      var l = o as List<object>;
      if (l == null) return nullIfAbsent ? null : new long[0];
      var r = new List<long>(); foreach (var x in l) if (x is double) r.Add((long)(double)x);
      return r.ToArray();
    }
    static int[] Ints(object o) { var l = Longs(o, false); var r = new int[l.Length]; for (int i = 0; i < l.Length; i++) r[i] = (int)l[i]; return r; }
    static ulong Ptr(Dictionary<string, object> d, string k) { ulong p; return ulong.TryParse(Str(d, k) ?? "0", NumberStyles.None, Inv, out p) ? p : 0; }

    // ---- one list's reading
    public static string List(RFX.ListRead l) {
      if (l == null) return "null";
      return "{\"y\":" + D(l.ScrollY) + ",\"c\":" + l.Cols.ToString(Inv) + ",\"u\":" + D(l.RowUnits) + ",\"f\":" + l.First.ToString(Inv) + ",\"l\":" + l.Last.ToString(Inv)
        + ",\"fu\":" + D(l.FinalRowUnits) + ",\"fs\":" + D(l.FitScale) + "}";
    }
    public static RFX.ListRead ParseList(object o) {
      var d = O(o); if (d == null) return null;
      return new RFX.ListRead { ScrollY = Get(d, "y") is double ? (double)Get(d, "y") : double.NaN, Cols = (int)Num(d, "c", 0), RowUnits = Num(d, "u", 0), First = (int)Num(d, "f", -1), Last = (int)Num(d, "l", -1),
        FinalRowUnits = Num(d, "fu", 0), FitScale = Num(d, "fs", 0) };
    }

    // ---- the inventory (the list's reading apart: it changes with every scroll step, the uids do not)
    public static string InvCore(InventoryScreen s) {
      if (s == null) return "null";
      var sel = new List<long>(s.Selected); sel.Sort();
      return "{\"open\":" + B(s.Open) + ",\"eq\":" + B(s.EquipTab) + ",\"bulk\":" + B(s.Bulk) + ",\"known\":" + B(s.BulkKnown) + ",\"art\":" + B(s.ArtTab) + ",\"abulk\":" + B(s.ArtBulk) + ",\"aknown\":" + B(s.ArtBulkKnown) + ",\"ptr\":\"" + s.ListPtr.ToString(Inv) + "\",\"cur\":" + L(s.Current)
        + ",\"uids\":" + Arr(s.Uids) + ",\"sel\":" + Arr(sel) + "}";
    }
    public static InventoryScreen ParseInv(object core, object list) {
      var d = O(core); if (d == null) return null;
      var s = new InventoryScreen { Open = Bool(d, "open", false), EquipTab = Bool(d, "eq", false), Bulk = Bool(d, "bulk", false), BulkKnown = Bool(d, "known", false), ArtTab = Bool(d, "art", false), ArtBulk = Bool(d, "abulk", false), ArtBulkKnown = Bool(d, "aknown", false), ListPtr = Ptr(d, "ptr"), Current = (long)Num(d, "cur", 0) };
      s.Uids.AddRange(Longs(Get(d, "uids"), false));
      foreach (var u in Longs(Get(d, "sel"), false)) s.Selected.Add(u);
      s.List = ParseList(list);
      return s;
    }

    // ---- the Foundry / gem screen
    public static string RsCore(RecastScreen s) {
      if (s == null) return "null";
      var sb = new StringBuilder("{");
      sb.Append("\"sub\":").Append(B(s.SublimingOpen)).Append(",\"cur\":").Append(s.CurType.ToString(Inv)).Append(",\"panel\":").Append(B(s.PanelShown))
        .Append(",\"eq\":").Append(Arr(s.Equips)).Append(",\"sel\":").Append(L(s.Sel)).Append(",\"parts\":").Append(Arr(s.SelectParts))
        .Append(",\"suits\":").Append(Arr(s.Suits)).Append(",\"mains\":").Append(Arr(s.MainAttrs)).Append(",\"subs\":").Append(Arr(s.SubAttrs))
        .Append(",\"gem\":").Append(B(s.GemOpen)).Append(",\"gf\":").Append(s.GemFunc.ToString(Inv)).Append(",\"gs\":").Append(L(s.GemSel)).Append(",\"gl\":").Append(Arr(s.GemList))
        .Append(",\"asc\":").Append(B(s.AscendShown)).Append(",\"ae\":").Append(Arr(s.AscendEquips)).Append(",\"as\":").Append(L(s.AscendSel)).Append(",\"ap\":").Append(Arr(s.AscendParts))
        .Append(",\"asu\":").Append(Arr(s.AscendSuits)).Append(",\"am\":").Append(Arr(s.AscendMainAttrs)).Append(",\"asb\":").Append(Arr(s.AscendSubAttrs))
        .Append(",\"fo\":").Append(B(s.AscendFilterOpen)).Append(",\"fp\":").Append(s.FilterPanel.ToString(Inv))
        .Append(",\"fss\":").Append(Arr(s.FilterShowSuits)).Append(",\"fls\":").Append(Arr(s.FilterLeftSuits)).Append(",\"frs\":").Append(Arr(s.FilterRightSuits))
        .Append(",\"cp\":").Append(B(s.ChoosePending)).Append('}');
      return sb.ToString();
    }
    public static string RsLists(RecastScreen s) {
      if (s == null) return "null";
      return "{\"r\":" + List(s.RecastList) + ",\"g\":" + List(s.GemFuncList) + ",\"a\":" + List(s.AscendList) + ",\"s\":" + List(s.FilterSuitGrid) + "}";
    }
    public static RecastScreen ParseRs(object core, object lists) {
      var d = O(core); if (d == null) return null;
      var s = new RecastScreen {
        SublimingOpen = Bool(d, "sub", false), CurType = (int)Num(d, "cur", -1), PanelShown = Bool(d, "panel", false), Equips = Longs(Get(d, "eq"), false), Sel = (long)Num(d, "sel", 0),
        SelectParts = Ints(Get(d, "parts")), Suits = Longs(Get(d, "suits"), false), MainAttrs = Longs(Get(d, "mains"), false), SubAttrs = Longs(Get(d, "subs"), false),
        GemOpen = Bool(d, "gem", false), GemFunc = (int)Num(d, "gf", -1), GemSel = (long)Num(d, "gs", 0), GemList = Longs(Get(d, "gl"), false),
        AscendShown = Bool(d, "asc", false), AscendEquips = Longs(Get(d, "ae"), false), AscendSel = (long)Num(d, "as", 0), AscendParts = Ints(Get(d, "ap")),
        AscendSuits = Longs(Get(d, "asu"), false), AscendMainAttrs = Longs(Get(d, "am"), false), AscendSubAttrs = Longs(Get(d, "asb"), false),
        AscendFilterOpen = Bool(d, "fo", false), FilterPanel = (int)Num(d, "fp", 0),
        FilterShowSuits = Longs(Get(d, "fss"), false), FilterLeftSuits = Longs(Get(d, "fls"), false), FilterRightSuits = Longs(Get(d, "frs"), false), ChoosePending = Bool(d, "cp", false) };
      var ls = O(lists);
      if (ls != null) { s.RecastList = ParseList(Get(ls, "r")); s.GemFuncList = ParseList(Get(ls, "g")); s.AscendList = ParseList(Get(ls, "a")); s.FilterSuitGrid = ParseList(Get(ls, "s")); }
      return s;
    }

    // ---- the hero screen
    public static string Hero(HeroScreen h) {
      if (h == null) return "null";
      return "{\"hero\":" + L(h.Hero) + ",\"ok\":" + B(h.FormOk) + ",\"tab\":" + h.Tab.ToString(Inv) + ",\"part\":" + h.Part.ToString(Inv) + ",\"small\":" + B(h.SmallCards)
        + ",\"heroes\":" + Arr(h.Heroes) + ",\"gptr\":\"" + h.GridPtr.ToString(Inv) + "\",\"fo\":" + B(h.FilterOpen) + ",\"fl\":" + Arr(h.FactionList) + ",\"cc\":" + h.ClassCount.ToString(Inv)
        + ",\"camps\":" + Arr(h.ChosenCamps) + ",\"cls\":" + B(h.ClassChosen) + "}";
    }
    public static HeroScreen ParseHero(object o) {
      var d = O(o); if (d == null) return null;
      return new HeroScreen { Hero = (long)Num(d, "hero", 0), FormOk = Bool(d, "ok", false), Tab = (int)Num(d, "tab", -1), Part = (int)Num(d, "part", -1), SmallCards = Bool(d, "small", false),
        Heroes = Longs(Get(d, "heroes"), true), GridPtr = Ptr(d, "gptr"), FilterOpen = Bool(d, "fo", false), FactionList = Longs(Get(d, "fl"), true), ClassCount = (int)Num(d, "cc", 0),
        ChosenCamps = Longs(Get(d, "camps"), true), ClassChosen = Bool(d, "cls", false) };
    }

    // ---- what is left of ScreenInputs
    public static string Flags(ScreenInputs i) {
      return "{\"city\":" + B(i.CitySeen) + ",\"cf\":" + (i.CityForm.HasValue ? B(i.CityForm.Value) : "null") + ",\"mx\":" + B(i.ModalExtra) + ",\"mxw\":" + MiniJson.Quote(i.ModalExtraWhy ?? "")
        + (i.Screens != null ? ",\"scr\":[" + string.Join(",", i.Screens.ConvertAll(x => MiniJson.Quote(x ?? "")).ToArray()) + "]" : "") + "}";
    }

    /// <summary>The five slots of one read, as JSON text each.</summary>
    public static Dictionary<string, string> ReadSlots(ScreenInputs i) {
      var m = new Dictionary<string, string>();
      m["r.inv"] = InvCore(i.Inv); m["r.invList"] = List(i.Inv != null ? i.Inv.List : null);
      m["r.rs"] = RsCore(i.Rs); m["r.rsLists"] = RsLists(i.Rs);
      m["r.hero"] = Hero(i.Hero); m["r.flags"] = Flags(i);
      return m;
    }

    /// <summary>The ScreenInputs of the parsed slots (a missing slot = the reader failed).</summary>
    public static ScreenInputs ParseInputs(Dictionary<string, object> slots) {
      var s = new ScreenInputs { Inv = ParseInv(Get(slots, "r.inv"), Get(slots, "r.invList")), Rs = ParseRs(Get(slots, "r.rs"), Get(slots, "r.rsLists")), Hero = ParseHero(Get(slots, "r.hero")) };
      var f = O(Get(slots, "r.flags"));
      if (f != null) { s.CitySeen = Bool(f, "city", false); s.CityForm = Get(f, "cf") is bool ? (bool?)Bool(f, "cf", false) : null; s.ModalExtra = Bool(f, "mx", false); s.ModalExtraWhy = Str(f, "mxw") ?? "";
        var scr = Get(f, "scr") as List<object>; if (scr != null) { s.Screens = new List<string>(); foreach (var x in scr) if (x is string) s.Screens.Add((string)x); } }
      return s;
    }

    // ---- the locate request
    public static string Request(LocateRequest r) {
      if (r == null) return "null";
      var it = r.Item;
      var sb = new StringBuilder("{\"id\":").Append(MiniJson.Quote(r.Id ?? "")).Append(",\"purpose\":").Append(MiniJson.Quote(r.Purpose ?? "")).Append(",\"hero\":").Append(L(r.HeroUid)).Append(",\"item\":");
      if (it == null) sb.Append("null");
      else {
        var subs = new List<long>(); foreach (var s in it.Subs) subs.Add(s.Stat);
        var main = new List<long>(); foreach (var s in it.Main) main.Add(s.Stat);
        sb.Append("{\"uid\":").Append(L(it.Uid)).Append(",\"slot\":").Append(it.Slot.ToString(Inv)).Append(",\"quality\":").Append(it.Quality.ToString(Inv)).Append(",\"setId\":").Append(L(it.SetId))
          .Append(",\"subs\":").Append(Arr(subs)).Append(",\"main\":").Append(Arr(main)).Append('}');
      }
      return sb.Append('}').ToString();
    }
    public static LocateRequest ParseRequest(object o) {
      var d = O(o); if (d == null) return null;
      var r = new LocateRequest { Id = Str(d, "id") ?? "", Purpose = Str(d, "purpose") ?? "", HeroUid = (long)Num(d, "hero", 0) };
      var i = O(Get(d, "item"));
      if (i != null) {
        r.Item = new PlanItem { Uid = (long)Num(i, "uid", 0), Slot = (int)Num(i, "slot", 0), Quality = (int)Num(i, "quality", 0), SetId = (long)Num(i, "setId", 0) };
        foreach (var s in Longs(Get(i, "subs"), false)) r.Item.Subs.Add(new SubStat { Stat = (int)s });
        foreach (var s in Longs(Get(i, "main"), false)) r.Item.Main.Add(new SubStat { Stat = (int)s });
      }
      return r;
    }
  }

  /// <summary>What the overlay's tick worked with besides the screen reads (the guides' arguments and the draw conditions).</summary>
  public sealed class RecCtx {
    /// <summary>"locate" (the guide «Показать в игре»), "sell" (the storage cleanup hints), "" = another feature (not replayed).</summary>
    public string Mode = "";
    public bool Front;                         // the game window is in front
    public int Cols = 8, OnAccount = -1;       // LocateGuide.Next's cols and onAccount
    public LocateRequest Req;                  // the locate request in effect
    public bool Lines;                         // the page gave the step's text (a plate is drawn)
    public bool CityInvSeen;                   // the city bar's «Инвентарь» was seen by its picture
    public double InvDX = double.NaN, BtnDY = double.NaN;   // where that button is (U from the right / from the bottom)
    public long[] Want = new long[0];          // the storage cleanup's list
    public int ShCols = 6;                     // its columns
    public bool Art;                           // the storage cleanup of the artifact tab
    public bool PillUp, PillDown;              // the page gave the pills' texts

    public string ToJson() {
      return "{\"mode\":" + MiniJson.Quote(Mode) + ",\"front\":" + (Front ? "true" : "false") + ",\"cols\":" + Cols.ToString(CultureInfo.InvariantCulture) + ",\"onAcc\":" + OnAccount.ToString(CultureInfo.InvariantCulture)
        + ",\"req\":" + RecIn.Request(Req) + ",\"lines\":" + (Lines ? "true" : "false") + ",\"city\":" + (CityInvSeen ? "true" : "false")
        + ",\"invDX\":" + RecIn.D(InvDX) + ",\"btnDY\":" + RecIn.D(BtnDY) + ",\"want\":" + ArrOf(Want) + ",\"shCols\":" + ShCols.ToString(CultureInfo.InvariantCulture)
        + ",\"art\":" + (Art ? "true" : "false") + ",\"pu\":" + (PillUp ? "true" : "false") + ",\"pd\":" + (PillDown ? "true" : "false") + "}";
    }
    static string ArrOf(long[] a) { var sb = new StringBuilder("["); for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(','); sb.Append(a[i].ToString(CultureInfo.InvariantCulture)); } return sb.Append(']').ToString(); }

    public static RecCtx Parse(object o) {
      var d = RecIn.O(o); if (d == null) return null;
      var c = new RecCtx { Mode = RecIn.Str(d, "mode") ?? "", Front = RecIn.Bool(d, "front", false), Cols = (int)RecIn.Num(d, "cols", 8), OnAccount = (int)RecIn.Num(d, "onAcc", -1),
        Lines = RecIn.Bool(d, "lines", false), CityInvSeen = RecIn.Bool(d, "city", false), InvDX = Get(d, "invDX"), BtnDY = Get(d, "btnDY"), ShCols = (int)RecIn.Num(d, "shCols", 6),
        PillUp = RecIn.Bool(d, "pu", false), PillDown = RecIn.Bool(d, "pd", false), Art = RecIn.Bool(d, "art", false) };
      object r; if (d.TryGetValue("req", out r)) c.Req = RecIn.ParseRequest(r);
      var w = new List<long>(); object wv; if (d.TryGetValue("want", out wv) && wv is List<object>) foreach (var x in (List<object>)wv) if (x is double) w.Add((long)(double)x);
      c.Want = w.ToArray();
      return c;
    }
    static double Get(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is double ? (double)v : double.NaN; }
  }

  /// <summary>One screen read waiting for the recorder (the overlay thread adds, the capture thread takes).</summary>
  public sealed class RecRead { public long Seq, At; public Dictionary<string, string> Slots; }

  public static class RecReads {
    static readonly object gate = new object();
    static readonly List<RecRead> queue = new List<RecRead>();
    static long seq;
    public const int MaxQueue = 2000;

    /// <summary>The number of the last read added (0 = none yet).</summary>
    public static long Seq { get { lock (gate) return seq; } }
    public static void Clear() { lock (gate) { queue.Clear(); } }

    /// <summary>A read is added (only while a recording runs). <paramref name="inp"/> is serialised at once: the objects are changed later in the tick.</summary>
    public static void Add(long at, ScreenInputs inp) {
      var slots = RecIn.ReadSlots(inp);
      lock (gate) {
        seq++;
        queue.Add(new RecRead { Seq = seq, At = at, Slots = slots });
        if (queue.Count > MaxQueue) queue.RemoveRange(0, queue.Count - MaxQueue);
      }
    }

    /// <summary>The reads up to <paramref name="upToSeq"/>, in order (removed from the queue).</summary>
    public static List<RecRead> Take(long upToSeq) {
      var r = new List<RecRead>();
      lock (gate) {
        int n = 0;
        while (n < queue.Count && queue[n].Seq <= upToSeq) n++;
        if (n > 0) { r.AddRange(queue.GetRange(0, n)); queue.RemoveRange(0, n); }
      }
      return r;
    }
  }

  /// <summary>Writes the "in" object of a line; a slot is written only when it differs from the last one written (Rebase: every slot again,
  /// at the start of each part so a part can be replayed alone).</summary>
  public sealed class RecLineEncoder {
    readonly Dictionary<string, string> last = new Dictionary<string, string>();
    readonly Dictionary<string, string> held = new Dictionary<string, string>();   // the latest value of every read slot (for a rebased part)
    long heldAt;
    bool rebase = true;

    public void Rebase() { rebase = true; last.Clear(); }

    bool Changed(string slot, string json) {
      string was;
      if (last.TryGetValue(slot, out was) && was == json) return false;
      last[slot] = json; return true;
    }

    /// <summary>The "in" object of a line for the tick <paramref name="info"/> (it must have a context): takes the screen reads up to the tick from the queue.
    /// <paramref name="env"/>: the line's environment (U, game version, data hashes) as JSON.</summary>
    public string EncodeInfo(RecInfo info, string env) {
      var persistent = new Dictionary<string, string>();
      persistent["env"] = env; persistent["ctx"] = info.Ctx;
      if (info.SInv != null) { persistent["sinv"] = info.SInv; persistent["sinvList"] = info.SInvList; }
      var vol = new Dictionary<string, string>();
      if (info.Fd != null) vol["fd"] = info.Fd;
      if (!double.IsNaN(info.PlaceRow1)) vol["pr"] = RecIn.D(info.PlaceRow1);
      if (!double.IsNaN(info.InvSmooth)) vol["sr"] = RecIn.D(info.InvSmooth);
      return Encode(info.TickNow, RecReads.Take(info.ReadSeq), persistent, vol);
    }

    /// <param name="tn">the tick's clock</param><param name="reads">the reads since the last line</param>
    /// <param name="persistent">tick slots kept from line to line (ctx, env, sinv, sinvList)</param><param name="volatileSlots">tick slots written whenever present (fd)</param>
    public string Encode(long tn, List<RecRead> reads, Dictionary<string, string> persistent, Dictionary<string, string> volatileSlots) {
      var sb = new StringBuilder("{\"tn\":").Append(tn.ToString(CultureInfo.InvariantCulture)).Append(",\"reads\":[");
      bool first = true;
      if (rebase && held.Count > 0) {
        // the part starts: the state the first read of the part builds on
        sb.Append("{\"at\":").Append(heldAt.ToString(CultureInfo.InvariantCulture)).Append(",\"base\":1");
        foreach (var kv in held) { sb.Append(",\"").Append(kv.Key).Append("\":").Append(kv.Value); last[kv.Key] = kv.Value; }
        sb.Append('}'); first = false;
      }
      foreach (var r in reads) {
        if (!first) sb.Append(','); first = false;
        sb.Append("{\"at\":").Append(r.At.ToString(CultureInfo.InvariantCulture));
        foreach (var kv in r.Slots) {
          held[kv.Key] = kv.Value;
          if (Changed(kv.Key, kv.Value)) sb.Append(",\"").Append(kv.Key).Append("\":").Append(kv.Value);
        }
        sb.Append('}');
        heldAt = r.At;
      }
      sb.Append(']');
      if (persistent != null) foreach (var kv in persistent) if (kv.Value != null && (rebase || Changed(kv.Key, kv.Value))) { last[kv.Key] = kv.Value; sb.Append(",\"").Append(kv.Key).Append("\":").Append(kv.Value); }
      if (volatileSlots != null) foreach (var kv in volatileSlots) if (kv.Value != null) sb.Append(",\"").Append(kv.Key).Append("\":").Append(kv.Value);
      sb.Append('}');
      rebase = false;
      return sb.ToString();
    }
  }

  /// <summary>The rectangles the guides draw, from the same inputs as the overlay (client px). The placement itself is the pure code the overlay
  /// calls (LocateFrames.For / ListScene / CityButton, LocateGrid.Place, SellHint.BuildExact / PressBulkRect); only the choice of the scene
  /// mirrors app/Overlay.cs (TickLocate, TickSellHint), so a drift shows as a recorded-vs-replayed mismatch.</summary>
  public static class RecScenes {
    static RecFrameRect R(string kind, int x, int y, int w, int h) { return new RecFrameRect { Kind = kind, X = x, Y = y, W = w, H = h }; }

    /// <param name="placeRow1">client y of the list's row 1 as the overlay placed the item (NaN = it had no place)</param>
    public static List<RecFrameRect> Locate(RecCtx c, LocateStep step, PlanItem item, RecastScreen rs, InventoryScreen inv, GameGeometry g, double placeRow1, double W, double H, double U) {
      var r = new List<RecFrameRect>();
      if (c == null || !c.Front || step == null) return r;   // nothing is painted unless the game is in front
      LocateFrame lf = step.Kind != LocateKind.InList ? LocateFrames.For(step, item, rs, null, g, W, H, U) : null;
      if (step.Kind == LocateKind.InList && inv != null) {
        if (!double.IsNaN(placeRow1)) {
          double[] mkr;
          var mk = LocateFrames.ListScene(g, placeRow1, SellGeometry.Inventory(W, U, c.Cols), inv.Uids.Count, c.Cols, step.Row, step.Col, W, H, U, out mkr);
          if (mk.Kind == "box") r.Add(R("frame", mk.X, mk.Y, mk.W, mk.H));
          else if (mkr != null) r.Add(R("strip", (int)mkr[0] - 12, (int)mkr[1], 24, (int)(mkr[2] - mkr[1])));
        }
      }
      if (c.Lines && step.Kind == LocateKind.OpenInventory && !step.OnHero && c.CityInvSeen) {
        var b = LocateFrames.CityButton(g, W, H, U, c.InvDX, c.BtnDY);
        r.Add(R("tip-frame", b[0], b[1], b[2], b[3]));
      } else if (c.Lines && lf != null && lf.Mark.Kind == "box") r.Add(R("tip-frame", lf.Mark.X, lf.Mark.Y, lf.Mark.W, lf.Mark.H));
      return r;
    }

    /// <summary>The storage cleanup's step and its rectangles. <paramref name="row1"/>: the drawn row 1 (NaN = none).</summary>
    public static List<RecFrameRect> Sell(RecCtx c, InventoryScreen inv, GameGeometry g, double row1, double W, double H, double U, out SellHintStep step) {
      var r = new List<RecFrameRect>();
      // the artifact tab's list has its own geometry (the overlay's shGeo): the list's rectangle from the geometry file
      bool art = c.Art || (inv != null && inv.ArtTab && !inv.EquipTab);
      var sg = art ? SellGeometry.Artifacts(W, H, U, c.ShCols, g != null ? g.Rect("inventory.artList", W, H, U) : null) : SellGeometry.Inventory(W, U, c.ShCols);
      step = SellHint.BuildExact(inv, c.Want, c.ShCols, row1, sg, W, H, U, art);
      if (!c.Front) return r;
      if (step.Kind == SellHintKind.Select || step.Kind == SellHintKind.AllSelected) {
        var marks = new List<SellFrame>(step.Frames);
        marks.Sort(delegate (SellFrame a, SellFrame b) { return Style(a).CompareTo(Style(b)); });
        foreach (var f in marks) r.Add(R("mark" + Style(f), f.X, f.Y, f.W, f.H));
        return r;
      }
      if (!c.Lines) return r;
      if (step.Kind == SellHintKind.PressBulk) { var p = SellHint.PressBulkRect(W, H, U); r.Add(R("tip-frame", p[0], p[1], p[2], p[3])); }
      else if (step.Kind == SellHintKind.OpenInventory && c.CityInvSeen) { var b = LocateFrames.CityButton(g, W, H, U, c.InvDX, c.BtnDY); r.Add(R("tip-frame", b[0], b[1], b[2], b[3])); }
      return r;
    }
    static int Style(SellFrame f) { return f.Style == "todo" ? 0 : f.Style == "done" ? 1 : 2; }
  }
}
