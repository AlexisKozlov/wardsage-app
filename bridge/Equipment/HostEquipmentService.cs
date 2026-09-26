using RealmForge.Bridge.Account;
using RealmForge.Bridge.Host;

namespace RealmForge.Bridge.Equipment;

/// <summary>
/// Equipping is done by RealmForge.exe, the process that sees the game: the bridge hands it the request
/// (<see cref="HostLink"/>), the app runs its guide (opens the slot, scrolls, clicks the item - «Заменить» stays the
/// player's) and answers done / cancelled / failed. Done moves the items in the snapshot at once.
/// </summary>
public sealed class HostEquipmentService(
    AccountSnapshotStore account, HostLink host, BridgeOptions options, ILogger<HostEquipmentService> log) : IEquipmentService
{
    public async Task<EquipResult> EquipAsync(EquipRequest request, CancellationToken cancellationToken)
    {
        var view = account.Current;
        if (view is null) return new EquipResult(EquipOutcome.Rejected, "No account snapshot yet.");
        var errors = new List<string>();
        EquipPlanChecker.Check(view, request.HeroId, request.Slots, errors);
        if (errors.Count > 0) return new EquipResult(EquipOutcome.Rejected, string.Join(" ", errors));

        var changes = request.Slots.Where(s => view.Gear[s.ItemId].HeroId != request.HeroId).ToList();
        if (changes.Count == 0) return new EquipResult(EquipOutcome.AlreadyEquipped);

        var slots = changes.Select(s => new HostEquipSlot(s.Slot, s.ItemId, view.Gear[s.ItemId].SetId, view.Gear[s.ItemId].PrimaryStat?.StatId)).ToList();
        var payload = new HostEquipPayload(request.CommandId, request.HeroId, view.Heroes[request.HeroId].Name, slots);
        log.LogInformation("Equip {Command}: hero {Hero}, {Count} item(s) -> RealmForge", request.CommandId, request.HeroId, changes.Count);
        var reply = await host.SendAsync(HostCommandTypes.Equip, payload, TimeSpan.FromSeconds(options.EquipTimeoutSeconds), cancellationToken);

        switch (reply.Status)
        {
            case HostReplyStatus.Done:
                account.ApplyEquip(request.HeroId, changes);
                return new EquipResult(EquipOutcome.Equipped);
            case HostReplyStatus.Cancelled:
                return new EquipResult(EquipOutcome.Cancelled, reply.Message ?? "Cancelled in RealmForge.");
            case HostReplyStatus.NotConnected:
                return new EquipResult(EquipOutcome.HostUnavailable, reply.Message ?? "RealmForge is not connected to the bridge.");
            case HostReplyStatus.TimedOut:
                return new EquipResult(EquipOutcome.TimedOut, reply.Message);
            default:
                return new EquipResult(EquipOutcome.Failed, reply.Message);
        }
    }
}
