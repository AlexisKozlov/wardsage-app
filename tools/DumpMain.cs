// Local account dump. Reads the game and writes one JSON file. No window and no site.
using System;
using System.IO;
using System.Text;
using RealmForge;

static class DumpMain {
  static int Main(string[] args) {
    if (args.Length != 1) {
      Console.Error.WriteLine("usage: dump-account <account.json>");
      return 2;
    }
    string json = RFX.Run();
    if (json == null) {
      Console.Error.WriteLine("read failed: " + RFX.LastError + " win32=" + RFX.LastOpenError);
      return 2;
    }
    string path = args[0];
    string folder = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
    File.WriteAllText(path, json, new UTF8Encoding(false));
    Console.WriteLine("DUMP_OK bytes=" + json.Length);
    return 0;
  }
}
