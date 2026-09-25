using System.Globalization;
using System.Text.Json;
using RealmForge.Bridge.Account;
using RealmForge.Bridge.Equipment;

namespace RealmForge.Bridge.Actions;

/// <summary>
/// <c>{"type":"equipment.equip","params":{"heroId":228700000,"slots":[{"slot":"weapon","itemId":1},{"slot":4,"itemId":"57"}]}}</c>
/// heroId / itemId: positive integers, as numbers or digit strings; slot: weapon|armor|bracer|amulet|ring or 0..4.
/// Checked against the account snapshot on arrival; the command succeeds when RealmForge reports the items are on
/// the hero (or they already were).
/// </summary>
public sealed class EquipActionHandler(AccountSnapshotStore account, IEquipmentService equipment) : IActionHandler
{
    public string Type => "equipment.equip";

    public void Validate(JsonElement parameters, ICollection<string> errors)
    {
        if (!EquipParams.TryRead(parameters, errors, out long heroId, out var slots)) return;
        if (account.Current is not { } view)
        {
            errors.Add("No account snapshot yet: RealmForge has not sent one to the bridge.");
            return;
        }
        EquipPlanChecker.Check(view, heroId, slots, errors);
    }

    public async Task ExecuteAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (!EquipParams.TryRead(command.Params, errors, out long heroId, out var slots))
            throw new ActionFailedException(string.Join(" ", errors));

        var result = await equipment.EquipAsync(new EquipRequest(command.Id, heroId, slots), cancellationToken);
        if (!result.Succeeded)
            throw new ActionFailedException(result.Message is null ? result.Outcome.ToString() : $"{result.Outcome}: {result.Message}");
    }
}

static class EquipParams
{
    static readonly string[] SlotNames = Enum.GetNames<SlotType>();

    public static bool TryRead(JsonElement p, ICollection<string> errors, out long heroId, out List<SlotAssignment> slots)
    {
        int before = errors.Count;
        slots = [];
        heroId = 0;

        foreach (var prop in p.EnumerateObject())
            if (prop.Name is not ("heroId" or "slots"))
                errors.Add($"Unknown parameter '{prop.Name}'. Allowed: heroId, slots.");

        if (!p.TryGetProperty("heroId", out var hero) || Id(hero) is not { } h)
            errors.Add("'heroId' is required: a positive integer (number or string).");
        else
            heroId = h;

        if (!p.TryGetProperty("slots", out var list) || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() is 0 or > 5)
        {
            errors.Add("'slots' is required: an array of 1..5 {slot, itemId}.");
            return false;
        }

        int i = 0;
        foreach (var entry in list.EnumerateArray())
        {
            string at = $"slots[{i++}]";
            if (entry.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{at}: must be an object {{slot, itemId}}.");
                continue;
            }
            foreach (var prop in entry.EnumerateObject())
                if (prop.Name is not ("slot" or "itemId"))
                    errors.Add($"{at}: unknown property '{prop.Name}'.");

            SlotType? slot = entry.TryGetProperty("slot", out var s) ? Slot(s) : null;
            long? itemId = entry.TryGetProperty("itemId", out var it) ? Id(it) : null;
            if (slot is null) errors.Add($"{at}.slot: one of {string.Join(", ", SlotNames.Select(n => n.ToLowerInvariant()))} or 0..4.");
            if (itemId is null) errors.Add($"{at}.itemId: a positive integer (number or string).");
            if (slot is null || itemId is null) continue;

            if (slots.Any(x => x.Slot == slot)) errors.Add($"{at}: slot {EquipPlanChecker.Name(slot.Value)} is given twice.");
            else if (slots.Any(x => x.ItemId == itemId)) errors.Add($"{at}: item {itemId} is given twice.");
            else slots.Add(new SlotAssignment(slot.Value, itemId.Value));
        }
        return errors.Count == before;
    }

    static long? Id(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out long n) && n > 0 => n,
        JsonValueKind.String when v.GetString() is { Length: > 0 and <= 19 } s && s.All(char.IsAsciiDigit)
                                  && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n > 0 => n,
        _ => null,
    };

    // Names are matched one by one: Enum.TryParse would also take "1" or "Weapon, Ring".
    static SlotType? Slot(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt32(out int n) && Enum.IsDefined(typeof(SlotType), n) => (SlotType)n,
        JsonValueKind.String => SlotNames.FirstOrDefault(n => string.Equals(n, v.GetString(), StringComparison.OrdinalIgnoreCase))
                                    is { } name ? Enum.Parse<SlotType>(name) : null,
        _ => null,
    };
}
