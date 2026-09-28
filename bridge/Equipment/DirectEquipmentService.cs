using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RealmForge.Bridge.Account;
using RealmForge.Bridge.Host;

namespace RealmForge.Bridge.Equipment;

/// <summary>
/// Equips items by injecting a call to <c>LuaNetworkManager.SendMessage</c>
/// inside the running game process.  No UI clicks, no window focus needed.
///
/// <para>On failure (game not running, injection error, anti-cheat block)
/// the request is forwarded to <see cref="HostEquipmentService"/> as a
/// fallback so the player still sees the guide and can finish manually.</para>
/// </summary>
public sealed class DirectEquipmentService : IEquipmentService
{
    readonly AccountSnapshotStore account;
    readonly HostLink host;
    readonly BridgeOptions options;
    readonly ILogger<IEquipmentService>? log;
    readonly GameProcessInjector injector = new();
    readonly ConcurrentDictionary<long, DateTime> lastEquip = new();

    public DirectEquipmentService(AccountSnapshotStore account, HostLink host,
                                  BridgeOptions options, ILogger<IEquipmentService>? log)
    {
        this.account = account;
        this.host = host;
        this.options = options;
        this.log = log;
    }

    public async Task<EquipResult> EquipAsync(EquipRequest request, CancellationToken cancellationToken)
    {
        var view = account.Current;
        if (view is null)
            return new EquipResult(EquipOutcome.Rejected, "No account snapshot yet.");

        var errors = new List<string>();
        EquipPlanChecker.Check(view, request.HeroId, request.Slots, errors);
        if (errors.Count > 0)
            return new EquipResult(EquipOutcome.Rejected, string.Join(" ", errors));

        var changes = request.Slots.Where(s => view.Gear[s.ItemId].HeroId != request.HeroId).ToList();
        if (changes.Count == 0)
            return new EquipResult(EquipOutcome.AlreadyEquipped);

        // Attempt direct injection first
        if (injector.Connect())
        {
            bool anyFailed = false;
            string? lastError = null;
            int okCount = 0;

            foreach (var slot in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Throttle: do not send the same item+hero more than once per 2 s
                var key = (slot.ItemId << 32) | request.HeroId;
                if (lastEquip.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(2))
                    continue;

                log?.LogInformation("DirectEquip {Command}: hero {Hero}, slot {Slot}, item {Item}",
                    request.CommandId, request.HeroId, slot.Slot, slot.ItemId);

                uint seq = injector.SendEquip(request.HeroId, slot.ItemId);
                if (seq == 0)
                {
                    lastError = injector.Error;
                    anyFailed = true;
                    log?.LogWarning("DirectEquip failed for item {Item}: {Error}", slot.ItemId, injector.Error);
                    break; // stop, fallback will handle the rest
                }

                lastEquip[key] = DateTime.UtcNow;
                okCount++;

                // Small delay between items so the server can process
                if (changes.Count > 1)
                    await Task.Delay(150, cancellationToken);
            }

            if (!anyFailed)
            {
                // Optimistically apply changes to the local snapshot
                account.ApplyEquip(request.HeroId, changes);
                log?.LogInformation("DirectEquip {Command}: {Count} item(s) sent via injection", request.CommandId, okCount);
                return new EquipResult(EquipOutcome.Equipped);
            }

            log?.LogWarning("DirectEquip failed ({Error}), falling back to RealmForge clicks.", lastError);
        }
        else
        {
            log?.LogInformation("DirectEquip cannot connect ({Error}), using RealmForge fallback.", injector.Error);
        }

        // Fallback to the click-based path (HostEquipmentService)
        var fallback = new HostEquipmentService(account, host, options,
            log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HostEquipmentService>.Instance);
        return await fallback.EquipAsync(request, cancellationToken);
    }
}
