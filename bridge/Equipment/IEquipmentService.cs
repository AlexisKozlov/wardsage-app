using RealmForge.Bridge.Account;

namespace RealmForge.Bridge.Equipment;

public sealed record SlotAssignment(SlotType Slot, long ItemId);

/// <param name="CommandId">The id of the "equipment.equip" command (for logs and for RealmForge's guide).</param>
public sealed record EquipRequest(string CommandId, long HeroId, IReadOnlyList<SlotAssignment> Slots);

public enum EquipOutcome { Equipped, AlreadyEquipped, Rejected, Cancelled, Failed, HostUnavailable, TimedOut }

public sealed record EquipResult(EquipOutcome Outcome, string? Message = null)
{
    public bool Succeeded => Outcome is EquipOutcome.Equipped or EquipOutcome.AlreadyEquipped;
}

/// <summary>Puts items on a hero in the game. The business logic behind "equipment.equip".</summary>
public interface IEquipmentService
{
    /// <summary>Completes when the items are on the hero or the attempt is over (cancelled, failed, timed out).
    /// <paramref name="cancellationToken"/> fires on shutdown.</summary>
    Task<EquipResult> EquipAsync(EquipRequest request, CancellationToken cancellationToken);
}
