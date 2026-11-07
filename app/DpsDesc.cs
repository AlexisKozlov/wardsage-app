// Wardsage app - skill descriptions for the DPS meter's tooltip: the hero pages' texts (app/res/dps_desc.bin, tools/dps-desc.mjs), read lazily
// on the first hover, and the lookup: a skill's own text; a nested skill gets its root's text with a note; an aspect, set or field effect gets its category.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace RealmForge {
  /// <summary>What the tooltip shows: the title, the body (may be empty) and a grey note under the title (may be null).</summary>
  sealed class DpsDescInfo {
    public string Title, Body, Note;
  }

  static class DpsDesc {
    static readonly object gate = new object();
    static Dictionary<int, string[]> map;

    /// <summary>Descriptions known (loads the table on the first call).</summary>
    public static int Count { get { return Map().Count; } }

    /// <summary>The own text of a skill (ru or en; the other language when this one is empty), null when there is none.</summary>
    public static string Get(int id, bool ru) {
      string[] t; if (!Map().TryGetValue(id, out t)) return null;
      string s = ru ? t[0] : t[1];
      if (string.IsNullOrEmpty(s)) s = ru ? t[1] : t[0];
      return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>The tooltip for a row of a hero's skill list: unit = the hero (for the nested-skill roots), id = the row's skill.</summary>
    public static DpsDescInfo Find(int unit, int id, bool ru) {
      string body = Get(id, ru);
      if (body != null) return new DpsDescInfo { Title = DpsNames.Skill(id, ru), Body = body };
      int root = DpsNames.SkillRoot(unit, id);
      if (root != id) {
        string rb = Get(root, ru);
        if (rb != null) {
          string rn = DpsNames.Skill(root, ru);
          return new DpsDescInfo { Title = rn, Body = rb, Note = (ru ? "часть скилла: " : "part of skill: ") + rn };
        }
      }
      string cat = DpsNames.Category(id, ru) ?? DpsNames.Category(root, ru);
      if (cat != null) return new DpsDescInfo { Title = cat, Body = "" };   // an aspect, a set, a field effect, an awakening: the name only
      string name = DpsNames.KnownSkill(id) ? DpsNames.Skill(id, ru) : DpsNames.KnownSkill(root) ? DpsNames.Skill(root, ru) : DpsNames.Other(ru);
      return new DpsDescInfo { Title = name, Body = ru ? "Описание недоступно" : "No description" };
    }

    /// <summary>The folded "Other" row: its name only.</summary>
    public static DpsDescInfo Tail(bool ru) { return new DpsDescInfo { Title = DpsNames.Other(ru), Body = "" }; }

    static Dictionary<int, string[]> Map() {
      lock (gate) {
        if (map != null) return map;
        var d = new Dictionary<int, string[]>();
        try { Parse(ReadBytes(), d); } catch (Exception) { d.Clear(); }
        map = d; return d;
      }
    }

    /// <summary>Test hook: parse a table from gzip bytes instead of the resource.</summary>
    public static Dictionary<int, string[]> Parse(byte[] gz) { var d = new Dictionary<int, string[]>(); Parse(gz, d); return d; }

    static void Parse(byte[] gz, Dictionary<int, string[]> d) {
      if (gz == null) return;
      string text;
      using (var ms = new MemoryStream(gz))
      using (var z = new GZipStream(ms, CompressionMode.Decompress))
      using (var rd = new StreamReader(z, new UTF8Encoding(false))) text = rd.ReadToEnd();
      foreach (var line in text.Split('\n')) {
        var p = line.Split('\t');
        if (p.Length < 3) continue;
        var t = new[] { Unescape(p[1]), Unescape(p[2]) };
        foreach (var ids in p[0].Split(',')) { int id; if (int.TryParse(ids, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) d[id] = t; }
      }
    }

    static string Unescape(string s) {
      if (s.IndexOf('\\') < 0) return s;
      var sb = new StringBuilder(s.Length);
      for (int i = 0; i < s.Length; i++) {
        char c = s[i];
        if (c == '\\' && i + 1 < s.Length) { char n = s[++i]; sb.Append(n == 'n' ? '\n' : n); } else sb.Append(c);
      }
      return sb.ToString();
    }

    static byte[] ReadBytes() {
      using (var s = typeof(DpsDesc).Assembly.GetManifestResourceStream("dps/dps_desc.bin")) {
        if (s != null) { var b = new byte[s.Length]; int got = 0; while (got < b.Length) { int n = s.Read(b, got, b.Length - got); if (n <= 0) break; got += n; } return b; }
      }
      foreach (var dir in new[] { "app/res", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res"), "." }) {
        string p = Path.Combine(dir, "dps_desc.bin");
        if (File.Exists(p)) return File.ReadAllBytes(p);
      }
      return null;
    }
  }
}
