// The build channel: the normal build and the test build (tools/build-app.sh --test defines TESTBUILD) coexist on one PC
// and one site account, so everything that must not be shared lives here: names, folders, mutex, feed.
using System;

namespace RealmForge {
  public static class Channel {
#if TESTBUILD
    public static readonly bool IsTest = true;   // readonly, not const: no "unreachable code" warnings
    public const string ProductName = "Wardsage Test";
    public const string WindowTitle = "Wardsage TEST";
    public const string DefaultSite = "https://test.wardsage.com";
    public const string FolderName = "WardsageTest";
    public const string OldFolderName = "";   // the test build never had another name
    public const string DocsFolderName = "WardsageTest";
    public const string MutexName = "Wardsage.Test.SingleInstance";
    public const string EventName = "Wardsage.Test.Show";
    public const string AutostartName = "Wardsage Test";
    public const string FeedPath = "/downloads/test/latest.json";
    public const string StagedPrefix = "Wardsage-Test-";
#else
    public static readonly bool IsTest = false;   // readonly, not const: no "unreachable code" warnings
    public const string ProductName = "Wardsage";
    public const string WindowTitle = "Wardsage";
    public const string DefaultSite = "https://wardsage.com";
    // settings (%APPDATA%) and local data (%LOCALAPPDATA%); copied once from OldFolderName at start (src/FolderMigration.cs)
    public const string FolderName = "Wardsage";
    public const string OldFolderName = "RealmForge";
    // the account.json copies (Documents) only hold what the player saved on purpose: they can take the new name at once
    public const string DocsFolderName = "Wardsage";
    public const string MutexName = "RealmForge.Desktop.SingleInstance";
    public const string EventName = "RealmForge.Desktop.Show";
    public const string AutostartName = "Wardsage";
    public const string FeedPath = "/downloads/latest.json";
    public const string StagedPrefix = "RealmForge-";
#endif

#if STREAMER
    // tools/build-app.sh --streamer: the MAIN channel (folders, site, sync) for one streamer who tests the capture setting; no self-update
    public static readonly bool IsStreamer = true;
#else
    public static readonly bool IsStreamer = false;
#endif

    /// <summary>Is the setting «show hints in recordings and streams» available (the test channel, or the one-off streamer build)?</summary>
    public static bool CaptureSettingAvailable(bool test, bool streamer) { return test || streamer; }
    public static readonly bool CaptureSetting = CaptureSettingAvailable(IsTest, IsStreamer);

    /// <summary>Does this build check for, download and install updates (not the streamer build)?</summary>
    public static bool UpdatesEnabled(bool streamer) { return !streamer; }
  }
}
