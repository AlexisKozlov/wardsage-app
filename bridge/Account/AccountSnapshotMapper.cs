using System.Globalization;
using System.Text.Json;

namespace RealmForge.Bridge.Account;

/// <summary>
/// RealmForge's account.json (the game's Lua tables as the extractor dumps them) -> <see cref="AccountSnapshotDto"/>.
/// Same rules as the site (realmforge-web lib/game/normalize.ts): Lua lists are objects keyed "[1]", "[2]", ...;
/// attribute lists may hold the same attributes twice (the second half wins); a repeated item uid keeps its first
/// position and its last value; tables with vConfig are item templates, not the player's items.
/// </summary>
public static class AccountSnapshotMapper
{
    public static AccountSnapshotDto Map(JsonElement root, GameReference reference, string? gameVersion, DateTimeOffset capturedAt)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("account.json must be a JSON object.");
        bool hasHeroes = root.TryGetProperty("heroes", out var heroesJson) && heroesJson.ValueKind == JsonValueKind.Array;
        bool hasGear = root.TryGetProperty("equipment", out var gearJson) && gearJson.ValueKind == JsonValueKind.Array;
        if (!hasHeroes && !hasGear) throw new FormatException("account.json has neither 'heroes' nor 'equipment'.");

        var items = new List<ItemDto>();
        var index = new Dictionary<(ItemKind, long), int>();
        void Put(ItemDto item)
        {
            if (index.TryGetValue((item.Kind, item.Id), out int at)) items[at] = item;
            else
            {
                index[(item.Kind, item.Id)] = items.Count;
                items.Add(item);
            }
        }

        if (hasGear)
            foreach (var e in gearJson.EnumerateArray())
                if (Gear(e, reference) is { } item) Put(item);
        if (root.TryGetProperty("artifacts", out var artifacts))
            foreach (var a in LuaList(artifacts))
                if (Artifact(a) is { } item) Put(item);

        var heroes = new List<HeroDto>();
        if (hasHeroes)
            foreach (var h in heroesJson.EnumerateArray())
                if (Hero(h, reference) is { } hero) heroes.Add(hero);

        return new AccountSnapshotDto(0, capturedAt, capturedAt, gameVersion, false, AttachGear(heroes, items), items);
    }

    /// <summary>Fills every hero's Equipped / ArtifactId from the items' HeroId (the game keeps ownership on the item).</summary>
    public static IReadOnlyList<HeroDto> AttachGear(IEnumerable<HeroDto> heroes, IReadOnlyList<ItemDto> items)
    {
        var gear = new Dictionary<long, List<EquippedSlotDto>>();
        var artifact = new Dictionary<long, long>();
        foreach (var item in items)
        {
            if (item.HeroId is not { } owner) continue;
            if (item.Kind == ItemKind.Artifact) artifact.TryAdd(owner, item.Id);
            else if (item.SlotType is { } slot)
            {
                if (!gear.TryGetValue(owner, out var list)) gear[owner] = list = [];
                list.Add(new EquippedSlotDto(slot, item.Id));
            }
        }
        return heroes.Select(hero => hero with
        {
            Equipped = gear.TryGetValue(hero.Id, out var list) ? list.OrderBy(s => s.Slot).ToList() : [],
            ArtifactId = artifact.TryGetValue(hero.Id, out long a) ? a : null,
        }).ToList();
    }

    static HeroDto? Hero(JsonElement h, GameReference reference)
    {
        if (Int(h, "iHeroId") is not > 0 || Int(h, "iBaseId") is not { } baseId) return null;
        return new HeroDto(
            Int(h, "iHeroId")!.Value, (int)baseId, reference.HeroNames.GetValueOrDefault((int)baseId),
            (int)(Int(h, "iLevel") ?? 0), (int)(Int(h, "iStarLevel") ?? 0), Int(h, "iPower") ?? 0, [], null);
    }

    static ItemDto? Gear(JsonElement e, GameReference reference)
    {
        if (e.ValueKind != JsonValueKind.Object || e.TryGetProperty("vConfig", out _)) return null;
        if (Int(e, "iItemUid") is not > 0 || Int(e, "iItemId") is not > 0) return null;
        long uid = Int(e, "iItemUid")!.Value;
        int itemId = (int)Int(e, "iItemId")!.Value;
        var gearRef = reference.Gear.GetValueOrDefault(itemId);

        var main = DedupHalf(LuaList(Prop(e, "vMasterAttrList")));
        var vice = DedupHalf(LuaList(Prop(e, "vViceAttrList")));
        var rolls = LuaList(Prop(e, "vViceAttrMilepostCnt")).Select(Int).ToList();
        var substats = vice.Select((a, i) => Stat(a, reference.SubPools, reference, i < rolls.Count ? (int?)rolls[i] : null)).ToList();

        return new ItemDto(
            uid, ItemKind.Gear, itemId, gearRef?.Name, gearRef?.Slot, gearRef?.SetId, (int?)Int(e, "iStarLvl"),
            (int)(Int(e, "iLevel") ?? 0), Prop(e, "bLocked").ValueKind == JsonValueKind.True,
            main.Count > 0 ? Stat(main[0], reference.MainPools, reference, null) : null, substats,
            Int(e, "iHeroId") is > 0 ? Int(e, "iHeroId") : null);
    }

    static ItemDto? Artifact(JsonElement a)
    {
        if (Int(a, "iItemUid") is not > 0 || Int(a, "iItemId") is not > 0) return null;
        return new ItemDto(
            Int(a, "iItemUid")!.Value, ItemKind.Artifact, (int)Int(a, "iItemId")!.Value, null, null, null, null,
            (int)(Int(a, "iLevel") ?? 0), false, null, [], Int(a, "iHeroId") is > 0 ? Int(a, "iHeroId") : null);
    }

    static StatDto Stat(JsonElement attr, IReadOnlyDictionary<int, int> pools, GameReference reference, int? rolls)
    {
        int poolId = (int)(Int(attr, "iAttrId") ?? 0);
        long raw = Int(attr, "iValue") ?? 0;
        int? statId = pools.TryGetValue(poolId, out int s) ? s : null;
        var stat = statId is { } id ? reference.Stats.GetValueOrDefault(id) : null;
        double value = stat is { Scale: not 1 } ? Math.Round(raw / stat.Scale, 2, MidpointRounding.ToEven) : raw;
        return new StatDto(poolId, statId, stat?.Name, stat?.IsPercent, raw, value, rolls);
    }

    /// <summary>The dump sometimes lists the attributes twice ([a, b, a, b]): keep one copy (the later one, as the site does).</summary>
    static List<JsonElement> DedupHalf(List<JsonElement> list)
    {
        int n = list.Count;
        if (n == 0 || n % 2 != 0) return list;
        int half = n / 2;
        for (int i = 0; i < half; i++)
            if (Int(list[i], "iAttrId") != Int(list[i + half], "iAttrId")) return list;
        return list.GetRange(half, half);
    }

    /// <summary>A Lua array: {"[1]": x, "[2]": y} in key order (plain JSON arrays are accepted too).</summary>
    static List<JsonElement> LuaList(JsonElement table)
    {
        if (table.ValueKind == JsonValueKind.Array) return table.EnumerateArray().ToList();
        if (table.ValueKind != JsonValueKind.Object) return [];
        var entries = new List<(int Key, JsonElement Value)>();
        foreach (var p in table.EnumerateObject())
            if (int.TryParse(p.Name.Trim('[', ']'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int k))
                entries.Add((k, p.Value));
        return entries.OrderBy(e => e.Key).Select(e => e.Value).ToList();
    }

    static JsonElement Prop(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : default;

    static long? Int(JsonElement obj, string name) => Int(Prop(obj, name));

    /// <summary>Integers come as JSON numbers, big uids (artifacts) as strings.</summary>
    static long? Int(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out long n) => n,
        JsonValueKind.Number when v.TryGetDouble(out double d) && d == Math.Floor(d) && Math.Abs(d) < 9e15 => (long)d,
        JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) => s,
        _ => null,
    };
}
