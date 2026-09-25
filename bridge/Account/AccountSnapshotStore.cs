using RealmForge.Bridge.Equipment;

namespace RealmForge.Bridge.Account;

/// <summary>
/// The latest account snapshot. Two writers:
/// - RealmForge uploads a fresh reading (PUT /api/host/snapshot) - replaces everything;
/// - a finished equip command moves the items at once (<see cref="ApplyEquip"/>), so the next command and the dashboard
///   already see them on the new hero without waiting for the next reading (tens of seconds).
/// A reading that STARTED before the last such move is refused as stale: it would put the items back.
/// </summary>
public sealed class AccountSnapshotStore
{
    readonly object gate = new();
    long version;
    DateTimeOffset lastLocalChange = DateTimeOffset.MinValue;
    volatile AccountView? current;

    public AccountView? Current => current;

    /// <returns>false when the reading is older than a change made through the bridge (nothing is replaced).</returns>
    public bool Replace(AccountSnapshotDto reading)
    {
        lock (gate)
        {
            if (current is not null && reading.CapturedAt < lastLocalChange) return false;
            Publish(reading with { Provisional = false, UpdatedAt = DateTimeOffset.UtcNow });
            return true;
        }
    }

    /// <summary>The items are now on the hero: each leaves its previous owner, and what the hero wore in those slots
    /// goes back to the bag.</summary>
    public void ApplyEquip(long heroId, IReadOnlyList<SlotAssignment> slots)
    {
        lock (gate)
        {
            var snapshot = (current ?? throw new InvalidOperationException("No account snapshot.")).Snapshot;
            var moved = slots.Select(s => s.ItemId).ToHashSet();
            var freed = slots.Select(s => s.Slot).ToHashSet();
            var items = snapshot.Items.Select(item =>
            {
                if (item.Kind != ItemKind.Gear) return item;
                if (moved.Contains(item.Id)) return item with { HeroId = heroId };
                if (item.HeroId == heroId && item.SlotType is { } slot && freed.Contains(slot)) return item with { HeroId = null };
                return item;
            }).ToList();

            var now = DateTimeOffset.UtcNow;
            lastLocalChange = now;
            Publish(snapshot with
            {
                Heroes = AccountSnapshotMapper.AttachGear(snapshot.Heroes, items),
                Items = items,
                UpdatedAt = now,
                Provisional = true,
            });
        }
    }

    void Publish(AccountSnapshotDto snapshot) => current = new AccountView(snapshot with { Version = ++version });
}
