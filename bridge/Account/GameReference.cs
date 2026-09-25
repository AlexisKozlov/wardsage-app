using System.Text.Json;

namespace RealmForge.Bridge.Account;

public sealed record GearRef(SlotType Slot, int SetId, string? Name);

public sealed record StatRef(string? Name, bool IsPercent, double Scale);

/// <summary>
/// What account.json does not say by itself: hero names, each gear item's slot and set, which stat a pool rolls.
/// Read from the game reference (<c>reference/data</c>: heroes.json, equipment.json, stat_pools.json, stats.json).
/// Without it the snapshot still works, but names and slots are null and equip commands are refused.
/// </summary>
public sealed class GameReference
{
    GameReference() { }

    public IReadOnlyDictionary<int, string> HeroNames { get; private init; } = new Dictionary<int, string>();
    public IReadOnlyDictionary<int, GearRef> Gear { get; private init; } = new Dictionary<int, GearRef>();
    public IReadOnlyDictionary<int, int> MainPools { get; private init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, int> SubPools { get; private init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, StatRef> Stats { get; private init; } = new Dictionary<int, StatRef>();

    public bool HasGearSlots => Gear.Count > 0;

    public static GameReference Load(string? dir, string lang, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            log.LogWarning("No reference data ({Dir}): names and gear slots are unknown, equip commands are refused",
                           string.IsNullOrWhiteSpace(dir) ? "Bridge:ReferenceDir not set" : dir);
            return new GameReference();
        }

        var heroNames = new Dictionary<int, string>();
        foreach (var h in Array(dir, "heroes.json"))
            if (Int(h, "id") is { } id && Name(h, lang) is { } name) heroNames[id] = name;

        var gear = new Dictionary<int, GearRef>();
        foreach (var e in Array(dir, "equipment.json"))
        {
            if (Int(e, "id") is { } id && Int(e, "slot") is { } slot && Enum.IsDefined(typeof(SlotType), slot))
                gear[id] = new GearRef((SlotType)slot, Int(e, "set_id") ?? 0, Name(e, lang));
        }

        var mainPools = new Dictionary<int, int>();
        var subPools = new Dictionary<int, int>();
        using (var pools = Document(dir, "stat_pools.json"))
        {
            if (pools is not null)
            {
                ReadPools(pools.RootElement, "main", mainPools);
                ReadPools(pools.RootElement, "sub", subPools);
            }
        }

        var stats = new Dictionary<int, StatRef>();
        foreach (var s in Array(dir, "stats.json"))
        {
            if (Int(s, "id") is not { } id) continue;
            double scale = s.TryGetProperty("value_scale", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.TryGetDouble(out var d) && d > 0 ? d : 1;
            stats[id] = new StatRef(Name(s, lang), s.TryGetProperty("is_percent", out var p) && p.ValueKind == JsonValueKind.True, scale);
        }

        log.LogInformation("Reference data: {Heroes} heroes, {Gear} gear items, {Stats} stats ({Dir})",
                           heroNames.Count, gear.Count, stats.Count, dir);
        return new GameReference { HeroNames = heroNames, Gear = gear, MainPools = mainPools, SubPools = subPools, Stats = stats };
    }

    static JsonDocument? Document(string dir, string file)
    {
        string path = Path.Combine(dir, file);
        return File.Exists(path) ? JsonDocument.Parse(File.ReadAllBytes(path)) : null;
    }

    static List<JsonElement> Array(string dir, string file)
    {
        using var doc = Document(dir, file);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    static void ReadPools(JsonElement root, string kind, Dictionary<int, int> into)
    {
        if (!root.TryGetProperty(kind, out var pools) || pools.ValueKind != JsonValueKind.Object) return;
        foreach (var pool in pools.EnumerateObject())
            if (int.TryParse(pool.Name, out int id) && Int(pool.Value, "stat_id") is { } stat) into[id] = stat;
    }

    static int? Int(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

    static string? Name(JsonElement obj, string lang) =>
        obj.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.Object
        && n.TryGetProperty(lang, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
}
