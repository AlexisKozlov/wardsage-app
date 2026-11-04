// tools/rec-to-recipe.mjs asks here which widget of the UI graph (or which geo key) lies under a recorded click point: RECIPE_FROM=<tsv: form, x, y, w, h> dotnet ... --sim 1.
// One answer line per input line: "widget:<path>", "geo:<key>\t<value>", or "-".
using System;
using System.Globalization;
using System.IO;
using RealmForge;

static partial class CoreTests {
  static void RecipeFromResolve(string tsv) {
    var inv = CultureInfo.InvariantCulture; var hint = new HintGeometry();
    foreach (var line in File.ReadAllLines(tsv)) {
      var t = line.Split((char)9); if (t.Length < 5) { Console.WriteLine("-"); continue; }
      double x = double.Parse(t[1], inv), y = double.Parse(t[2], inv), w = double.Parse(t[3], inv), h = double.Parse(t[4], inv), U = Ui.Unit(w, h);
      // the hero screen's gear slots are placed by the site's geometry, not by a widget of the graph
      if (t[0] == "Form_CharactorMain") {
        int hit = -1; for (int s = 0; s < 5; s++) { var r = hint.Slot(s, w, h); if (r != null && x >= r[0] && x <= r[0] + r[2] && y >= r[1] && y <= r[1] + r[3]) hit = s; }
        if (hit >= 0) { Console.WriteLine("geo:hero.slot($slot)\t" + hit); continue; }
      }
      var wd = UiGraph.WidgetAt(t[0], x, y, w, h, U, false);
      if (wd != null) { Console.WriteLine("widget:" + wd.Path); continue; }
      // widgets placed only by a live read have no rect in the graph: the one safe widget of the form that opens the form the click led to (when there is exactly one)
      if (t.Length > 5 && t[5] != "") {
        var f = UiGraph.ByName(t[0]); UiGraph.Widget only = null; int n = 0;
        if (f != null) foreach (var cw in f.Widgets) { if (!cw.Shown || cw.Template || !cw.Safe) continue; foreach (var o in cw.Opens()) if (o == t[5]) { only = cw; n++; break; } }
        if (n == 1) { Console.WriteLine("widget:" + only.Path); continue; }
      }
      // the screens' fixed places (HeroGeometry): within a twentieth of a unit of a geo point
      var hg = new HeroGeometry(); double tol = 0.05 * U;
      Func<double, double, bool> near = (px, py) => Math.Abs(x - px) <= tol && Math.Abs(y - py) <= tol;
      if (t[0] == "Form_MainCity" && near(w - hg.HeroesBtnDX * U, h - hg.HeroesBtnDY * U)) { Console.WriteLine("geo:hero.heroesBtn"); continue; }
      if (t[0] == "Form_CharactorMain" && near(w - hg.GearTabDX * U, hg.GearTabY * U)) { Console.WriteLine("geo:hero.gearTab"); continue; }
      if (near(hg.BackX * U, hg.BackY * U)) { Console.WriteLine("std:back"); continue; }
      Console.WriteLine("-");
    }
  }
}
