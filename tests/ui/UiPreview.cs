// Renders the extractor window in a given state, for screenshots on Linux (Mono WinForms + Xvfb).
// Mono draws WinForms with its own theme and fonts, so this is only an approximation of Windows.
//
//   mono UiPreview.exe <state> <ru|en>
//   states: idle, code, reading, done, done-save, err-admin, err-token, err-rate, err-game

using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using RealmForge;

static class UiPreview {
  static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

  static void Set(object o, string field, object value) { o.GetType().GetField(field, Any).SetValue(o, value); }
  static object Call(object o, string method, params object[] args) { return o.GetType().GetMethod(method, Any).Invoke(o, args); }

  [STAThread]
  static void Main(string[] args) {
    string state = args.Length > 0 ? args[0] : "idle";
    var cfg = new AppConfig();
    cfg.Lang = args.Length > 1 ? args[1] : "ru";
    if (state != "idle" && state != "done-save") cfg.Code = "rf_7Qm2xKp9LbT4vWc8NzY3hJd6RfS1aGe5";
    if (state == "err-admin") cfg.Site = "https://realmforge.vercel.app";

    Application.EnableVisualStyles();
    var form = new MainForm(cfg, "C:\\Games\\RealmForge\\RealmForge-Extractor.ps1");
    form.StartPosition = FormStartPosition.Manual;
    form.Location = new System.Drawing.Point(0, 0);
    form.Shown += delegate { Apply(form, state); };
    var quit = new Timer(); quit.Interval = 4000; quit.Tick += delegate { Environment.Exit(0); }; quit.Start();
    Application.Run(form);
  }

  static void Apply(MainForm form, string state) {
    if (state == "idle") return;
    if (state == "code") {
      Set(form, "advOpen", true);
      Call(form, "ApplyTexts");
      return;
    }
    if (state == "reading") {                  // what the window shows ~17 s into reading memory
      Set(form, "busy", true);
      Set(form, "idleStatus", false);
      Call(form, "UpdateMode");
      Call(form, "OnProgress", form, new ProgressChangedEventArgs(5, new StageMsg(Stage.Read, "1.0.83.2")));
      Call(form, "OnProgress", form, new ProgressChangedEventArgs(38, "  tables: 1200"));
      Set(form, "readStarted", DateTime.Now.AddSeconds(-17));
      Call(form, "RenderSteps");
      return;
    }
    var r = new JobResult();
    r.Extract = new ExtractResult();
    r.Extract.GameVersion = "1.0.83.2";
    switch (state) {
      case "done":
      case "done-save":
        r.Extract.Heroes = 128; r.Extract.Items = 1109; r.Extract.Artifacts = 41;
        if (state == "done") {
          r.Sync = SyncClient.Interpret(200, "{\"ok\":true,\"snapshotId\":\"snap_1\",\"heroes\":128,\"items\":1109,\"artifacts\":41,\"viewUrl\":\"/app/heroes\"}", null, null, SyncClient.DefaultSite);
        } else {
          r.SavedPath = "C:\\Users\\Player\\Documents\\RealmForge\\account.json";
        }
        break;
      case "err-admin": r.Extract.Error = ExtractError.AccessDenied; break;
      case "err-game": r.Extract.Error = ExtractError.GameNotRunning; r.Extract.GameVersion = null; break;
      case "err-token": r.Sync = SyncClient.Interpret(401, "{\"ok\":false,\"error\":\"invalid_token\"}", null, null, SyncClient.DefaultSite); break;
      case "err-rate": r.Sync = SyncClient.Interpret(429, "{\"ok\":false,\"error\":\"rate_limited\"}", "600", null, SyncClient.DefaultSite); break;
    }
    form.ShowOutcome(r);
  }
}
