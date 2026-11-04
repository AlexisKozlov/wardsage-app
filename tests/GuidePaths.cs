// The game's own tutorial click paths (tests/fixtures/guide-paths.txt, made by tools/guide-paths.py from the decompiled Lua) as the reference for widget paths:
// every guide form is in the UI graph, the share of guide paths the graph also has does not fall, and the widgets the navigator presses (the city's «Герои», the hero
// screen's back arrow and gear tab) are paths the game's own guide clicks too. A game update that renames one fails here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using RealmForge;

static partial class CoreTests {
  static string GuideNorm(string p) { return Regex.Replace(Regex.Replace(p, @"\(Clone\)", ""), @"/BtnHitArea$", ""); }

  static void GuidePathTests() {
    Console.WriteLine("The game's guide paths as the reference");
    string file = "tests/fixtures/guide-paths.txt";
    if (!File.Exists(file)) { Check(false, "guide paths: fixture missing"); return; }
    var pairs = new List<string[]>();
    foreach (var line in File.ReadAllLines(file)) { var t = line.Split('\t'); if (t.Length == 2) pairs.Add(t); }
    int formKnown = 0, widgetKnown = 0, unsafeHit = 0; var guideKeys = new HashSet<string>();
    foreach (var t in pairs) {
      guideKeys.Add(t[0] + "|" + GuideNorm(t[1]));
      var f = UiGraph.ByName(t[0]); if (f == null) continue;
      formKnown++;
      string n = GuideNorm(t[1]); bool hit = false;
      foreach (var w in f.Widgets) if (GuideNorm(w.Path) == n) { hit = true; if (!w.Safe) unsafeHit++; break; }
      if (hit) widgetKnown++;
    }
    Check(pairs.Count >= 250, "guide paths: " + pairs.Count + " distinct (form, path) pairs in the fixture");
    Check(formKnown == pairs.Count, "guide paths: every guide form is in the UI graph (" + formKnown + "/" + pairs.Count + ")");
    Check(widgetKnown * 100 >= pairs.Count * 30, "guide paths: the UI graph has the same widget for " + widgetKnown + "/" + pairs.Count + " guide paths (list cells and nested clones are runtime-bound: not all)");
    Console.WriteLine("  (of those, " + unsafeHit + " are widgets the graph marks dangerous or a confirm: the guide presses them in its own flows, the navigator never does)");
    // the widgets the navigator presses are the ones the game's guide presses
    foreach (var pin in new[] {
      new[] { "Form_MainCity", "BottomPanel/Image/m_Down_Layout/m_Button_Heros" },
      new[] { "Form_CharactorMain", "m_Main_Frame/m_Back/Button_Back_New/Btn_Back" },
      new[] { "Form_CharactorMain", "m_Main_Frame/m_Right/m_ToggleGroup/m_Toggle_Equip" } }) {
      var f = UiGraph.ByName(pin[0]); bool inGraph = false; if (f != null) foreach (var w in f.Widgets) if (GuideNorm(w.Path) == pin[1]) inGraph = true;
      Check(guideKeys.Contains(pin[0] + "|" + pin[1]) && inGraph, "guide paths: " + pin[0] + " " + pin[1] + " is a path of the game's guide and of the UI graph");
    }
  }
}
