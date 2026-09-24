// Renders the equip helper window with a sample build, for screenshots on Linux (Mono WinForms + Xvfb).
//   mono HelperPreview.exe <pick|slot|hidden|done> <ru|en>
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using RealmForge;

static class HelperPreview {
  static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
  static void Set(object o, string f, object v) { o.GetType().GetField(f, Any).SetValue(o, v); }
  static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }

  static PlanItem It(int slot, long uid, string slotName, string name, int lvl, string main, long from, string fromName) {
    var i = new PlanItem(); i.Slot = slot; i.Uid = uid; i.SlotName = slotName; i.Name = name; i.Level = lvl; i.MainStat = main;
    i.FromHeroUid = from; i.FromHeroName = fromName; return i;
  }

  [STAThread]
  static void Main(string[] args) {
    string state = args.Length > 0 ? args[0] : "pick";
    Strings.Lang = args.Length > 1 ? args[1] : "ru";
    var p = new Plan(); p.Id = "p1"; p.HeroUid = 214700000; p.HeroName = "Сунь Укун";
    p.Items.Add(It(0, 11, "Оружие", "Меч «Ярость»", 16, "АТК 1 250", 0, null));
    p.Items.Add(It(1, 12, "Нагрудник", "Нагрудник «Ярость»", 16, "ЗАЩ% 60%", 0, null));
    p.Items.Add(It(2, 6423, "Браслет", "Браслет «Проклятие»", 16, "Крит. УРН 80%", 200, "Байек"));
    p.Items.Add(It(3, 14, "Амулет", "Амулет «Проклятие»", 12, "Шанс крит. 40%", 0, null));
    p.Items.Add(It(4, 15, "Кольцо", "Кольцо «Ярость»", 16, "АТК% 60%", 0, null));
    var live = new EquipLive(); live.HeroUid = p.HeroUid; live.PanelOk = true;
    live.Owner[11] = p.HeroUid; live.Owner[12] = p.HeroUid; live.Owner[6423] = 200; live.Owner[14] = 0; live.Owner[15] = 0;
    if (state == "pick") { live.Part = 2; live.Row[6423] = 7; live.Col[6423] = 2; }
    if (state == "slot") { live.Part = -1; }
    if (state == "hidden") { live.Part = 2; live.HideEquipped = true; }
    if (state == "done") foreach (var it in p.Items) live.Owner[it.Uid] = p.HeroUid;

    Application.EnableVisualStyles();
    var f = new HelperForm("http://127.0.0.1:9", "rf_7Qm2xKp9LbT4vWc8NzY3hJd6RfS1aGe5");
    Set(f, "loading", true);           // no network in the preview
    Set(f, "finishing", true);
    ((HashSet<string>)Get(f, "reported")).Add("p1");
    f.StartPosition = FormStartPosition.Manual;
    f.Shown += delegate {
      f.Location = new System.Drawing.Point(0, 0);
      Set(f, "plans", new List<Plan> { p });
      Set(f, "planIdx", 0);
      Set(f, "live", live);
      var box = (ComboBox)Get(f, "planBox"); box.Items.Add(p.HeroName + "  ·  5"); box.SelectedIndex = 0;
      f.GetType().GetMethod("Render", Any).Invoke(f, null);
    };
    var quit = new Timer(); quit.Interval = 3500; quit.Tick += delegate { Environment.Exit(0); }; quit.Start();
    Application.Run(f);
  }
}
