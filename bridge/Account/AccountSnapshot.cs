namespace RealmForge.Bridge.Account;

/// <summary>Gear slot, numbered as the game numbers them (m_FilterConfig.Part, the site's PageData slot).</summary>
public enum SlotType { Weapon = 0, Armor = 1, Bracer = 2, Amulet = 3, Ring = 4 }

public enum ItemKind { Gear, Artifact }

/// <summary>A main stat or substat. <paramref name="PoolId"/> is the game's iAttrId (a stat pool), <paramref name="StatId"/>
/// the stat it rolls (reference data); <paramref name="Value"/> is <paramref name="RawValue"/> divided by the stat's scale.</summary>
public sealed record StatDto(int PoolId, int? StatId, string? Name, bool? IsPercent, long RawValue, double Value, int? Rolls);

/// <summary>A gear item or an artifact. Rarity = the item's stars (iStarLvl), null for artifacts.
/// Name / SlotType / SetId come from the reference data and are null without it.</summary>
public sealed record ItemDto(
    long Id, ItemKind Kind, int ItemId, string? Name, SlotType? SlotType, int? SetId, int? Rarity, int Level, bool Locked,
    StatDto? PrimaryStat, IReadOnlyList<StatDto> Substats, long? HeroId);

public sealed record EquippedSlotDto(SlotType Slot, long ItemId);

/// <summary>Id is the hero's uid (baseId × 100000 + copy number).</summary>
public sealed record HeroDto(
    long Id, int BaseId, string? Name, int Level, int Stars, long Power, IReadOnlyList<EquippedSlotDto> Equipped, long? ArtifactId);

/// <summary>
/// The account as GET /api/state shows it. <paramref name="CapturedAt"/>: when RealmForge started reading the game;
/// <paramref name="Provisional"/>: equip results were applied on top of that reading and the next one will confirm them.
/// </summary>
public sealed record AccountSnapshotDto(
    long Version, DateTimeOffset CapturedAt, DateTimeOffset UpdatedAt, string? GameVersion, bool Provisional,
    IReadOnlyList<HeroDto> Heroes, IReadOnlyList<ItemDto> Items);

/// <summary>A published snapshot with its lookups; immutable, read without locks.</summary>
public sealed class AccountView
{
    public AccountView(AccountSnapshotDto snapshot)
    {
        Snapshot = snapshot;
        var heroes = new Dictionary<long, HeroDto>();
        foreach (var hero in snapshot.Heroes) heroes.TryAdd(hero.Id, hero);
        var gear = new Dictionary<long, ItemDto>();
        foreach (var item in snapshot.Items)
            if (item.Kind == ItemKind.Gear) gear.TryAdd(item.Id, item);
        Heroes = heroes;
        Gear = gear;
    }

    public AccountSnapshotDto Snapshot { get; }
    public IReadOnlyDictionary<long, HeroDto> Heroes { get; }
    public IReadOnlyDictionary<long, ItemDto> Gear { get; }
}
