// Wardsage app - the DPS meter's art (TEST build): the fonts (Fira Sans, SIL OFL 1.1, app/res/OFL-fira-sans.txt) and, later, the picture atlases.
// Only GDI+ draws with these fonts (Graphics.DrawString / GraphicsPath.AddString), sizes are in pixels.
#if TESTBUILD
using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RealmForge {
  static class DpsArt {
    static readonly object gate = new object();
    static bool loaded;
    // the collections and the font data stay alive for the whole run (the memory is not freed on purpose: GDI+ keeps reading it)
    static PrivateFontCollection semiCol, boldCol;
    static FontFamily semiFam, boldFam;
    static string fallbackName;

    /// <summary>A SemiBold face (Fira Sans, else Segoe UI Semibold, else DejaVu Sans) in pixels.</summary>
    public static Font Semi(float px) { Load(); return Make(semiFam, px, FontStyle.Regular); }
    /// <summary>A Bold face (Fira Sans, else Segoe UI, else DejaVu Sans) in pixels.</summary>
    public static Font Bold(float px) { Load(); return Make(boldFam, px, FontStyle.Bold); }
    /// <summary>True when the Fira Sans files were read (false: the system fallback is used).</summary>
    public static bool HasFira { get { Load(); return semiFam != null && boldFam != null; } }

    static Font Make(FontFamily fam, float px, FontStyle want) {
      if (fam != null) {
        FontStyle st = fam.IsStyleAvailable(want) ? want : fam.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : fam.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : want;
        try { return new Font(fam, px, st, GraphicsUnit.Pixel); } catch (Exception) { }
      }
      return new Font(fallbackName, px, want, GraphicsUnit.Pixel);
    }

    static void Load() {
      if (loaded) return;
      lock (gate) {
        if (loaded) return;
        semiFam = FromResource("dps/FiraSans-SemiBold.ttf", out semiCol);
        boldFam = FromResource("dps/FiraSans-Bold.ttf", out boldCol);
        fallbackName = Installed("Segoe UI Semibold") ? "Segoe UI Semibold" : Installed("Segoe UI") ? "Segoe UI" : "DejaVu Sans";
        loaded = true;
      }
    }

    static bool Installed(string name) {
      try { foreach (var f in FontFamily.Families) if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return true; } catch (Exception) { }
      return false;
    }

    static FontFamily FromResource(string name, out PrivateFontCollection col) {
      col = null;
      try {
        byte[] data;
        using (var s = typeof(DpsArt).Assembly.GetManifestResourceStream(name)) {
          if (s == null) return FromFile(name, out col);
          data = new byte[s.Length]; int got = 0;
          while (got < data.Length) { int n = s.Read(data, got, data.Length - got); if (n <= 0) break; got += n; }
        }
        var pc = new PrivateFontCollection();
        IntPtr mem = Marshal.AllocCoTaskMem(data.Length);   // never freed
        Marshal.Copy(data, 0, mem, data.Length);
        pc.AddMemoryFont(mem, data.Length);
        if (pc.Families.Length > 0) { col = pc; return pc.Families[0]; }
      } catch (Exception) { }
      return null;
    }

    // a run outside the exe (the render test, Mono): the same file from app/res next to the sources
    static FontFamily FromFile(string name, out PrivateFontCollection col) {
      col = null;
      try {
        string file = Path.GetFileName(name);
        foreach (var dir in new[] { "app/res", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res"), "." }) {
          string p = Path.Combine(dir, file);
          if (!File.Exists(p)) continue;
          var pc = new PrivateFontCollection(); pc.AddFontFile(p);
          if (pc.Families.Length > 0) { col = pc; return pc.Families[0]; }
        }
      } catch (Exception) { }
      return null;
    }
  }
}
#endif
