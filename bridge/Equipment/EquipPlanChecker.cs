using RealmForge.Bridge.Account;

namespace RealmForge.Bridge.Equipment;

/// <summary>Checks an equip request against a snapshot: the hero and the items exist, and each item fits its slot.
/// Run when the command arrives (400 at once) and again right before it runs (the snapshot may have changed).</summary>
public static class EquipPlanChecker
{
    public static void Check(AccountView account, long heroId, IReadOnlyList<SlotAssignment> slots, ICollection<string> errors)
    {
        if (!account.Heroes.ContainsKey(heroId))
            errors.Add($"Hero {heroId} is not in the account snapshot.");

        foreach (var (slot, itemId) in slots)
        {
            if (!account.Gear.TryGetValue(itemId, out var item))
                errors.Add($"Item {itemId} is not in the account snapshot.");
            else if (item.SlotType is not { } itemSlot)
                errors.Add($"The slot of item {itemId} is unknown (reference data not loaded, see Bridge:ReferenceDir).");
            else if (itemSlot != slot)
                errors.Add($"Item {itemId} is {Name(itemSlot)}, not {Name(slot)}.");
        }
    }

    /// <summary>The slot as the API writes it ("weapon").</summary>
    public static string Name(SlotType slot) => slot.ToString().ToLowerInvariant();
}
