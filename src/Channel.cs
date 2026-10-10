// The build channel: the normal build and the test build (tools/build-app.sh --test defines TESTBUILD) coexist on one PC
// and one site account, so everything that must not be shared lives here: names, folders, mutex, feed, bridge port.
using System;

namespace RealmForge {
  public static class Channel {
#if TESTBUILD
    public static readonly bool IsTest = true;   // readonly, not const: no "unreachable code" warnings
    public const string ProductName = "Wardsage Test";
    public const string WindowTitle = "Wardsage TEST";
    public const string DefaultSite = "https://test.wardsage.com";
    public const string FolderName = "WardsageTest";
    public const string MutexName = "Wardsage.Test.SingleInstance";
    public const string EventName = "Wardsage.Test.Show";
    public const string AutostartName = "Wardsage Test";
    public const string FeedPath = "/downloads/test/latest.json";
    public const string BridgePort = "5056";   // a string: used in a const URL
    public const string StagedPrefix = "Wardsage-Test-";
#else
    public static readonly bool IsTest = false;   // readonly, not const: no "unreachable code" warnings
    public const string ProductName = "Wardsage";
    public const string WindowTitle = "Wardsage";
    public const string DefaultSite = "https://wardsage.com";
    // the folders keep their old name: existing installs keep their settings, sync code and downloaded updates
    public const string FolderName = "RealmForge";
    public const string MutexName = "RealmForge.Desktop.SingleInstance";
    public const string EventName = "RealmForge.Desktop.Show";
    public const string AutostartName = "Wardsage";
    public const string FeedPath = "/downloads/latest.json";
    public const string BridgePort = "5055";   // a string: used in a const URL
    public const string StagedPrefix = "RealmForge-";
#endif
  }
}
