namespace RealmForge.Bridge;

/// <summary>The "Bridge" section of appsettings.json (any value can be overridden, e.g. <c>--Bridge:Port=5056</c>).</summary>
public sealed class BridgeOptions
{
    public const string Section = "Bridge";

    /// <summary>Loopback port; the service never listens on other interfaces.</summary>
    public int Port { get; set; } = 5055;

    /// <summary>Accept <c>Origin: null</c> (a dashboard opened from a local file). Sandboxed iframes of any site send it too,
    /// which is why the token below stays required.</summary>
    public bool AllowNullOrigin { get; set; } = true;

    /// <summary>Every /api request must carry the <c>X-RealmForge-Token</c> header.</summary>
    public bool RequireToken { get; set; } = true;

    /// <summary>Where the token lives; default <c>%LOCALAPPDATA%\RealmForge\bridge-token.txt</c> (created on first start).</summary>
    public string? TokenFile { get; set; }

    /// <summary>Optional JSON object the state starts from; empty state when not set.</summary>
    public string? StateFile { get; set; }

    /// <summary>Game reference folder (heroes.json, equipment.json, stat_pools.json, stats.json), e.g. D:\RealmForge\reference\data.
    /// Without it hero/item names and gear slots are unknown and "equipment.equip" is refused.</summary>
    public string? ReferenceDir { get; set; }

    /// <summary>Language of the names in the snapshot: "ru" or "en".</summary>
    public string Language { get; set; } = "ru";

    /// <summary>Largest account.json RealmForge may upload.</summary>
    public long MaxSnapshotBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>How long an equip command waits for the player to finish RealmForge's guide.</summary>
    public int EquipTimeoutSeconds { get; set; } = 600;

    /// <summary>RealmForge counts as disconnected when it has not polled for commands for this long.</summary>
    public int HostOfflineAfterSeconds { get; set; } = 60;

    public int QueueCapacity { get; set; } = 256;
    public int MaxCommandsPerRequest { get; set; } = 100;
    public long MaxRequestBodyBytes { get; set; } = 256 * 1024;
    public int KeepFinishedJobs { get; set; } = 500;
    public int ShutdownTimeoutSeconds { get; set; } = 10;

    public void Validate()
    {
        static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException($"{Section}: {message}");
        }

        Check(Port is > 0 and <= 65535, "Port must be 1..65535.");
        Check(QueueCapacity > 0, "QueueCapacity must be positive.");
        Check(MaxCommandsPerRequest > 0, "MaxCommandsPerRequest must be positive.");
        Check(MaxRequestBodyBytes > 0, "MaxRequestBodyBytes must be positive.");
        Check(KeepFinishedJobs > 0, "KeepFinishedJobs must be positive.");
        Check(ShutdownTimeoutSeconds > 0, "ShutdownTimeoutSeconds must be positive.");
        Check(Language is "ru" or "en", "Language must be ru or en.");
        Check(MaxSnapshotBytes > 0, "MaxSnapshotBytes must be positive.");
        Check(EquipTimeoutSeconds > 0, "EquipTimeoutSeconds must be positive.");
        Check(HostOfflineAfterSeconds > 30, "HostOfflineAfterSeconds must be above 30 (the longest poll).");
    }
}
