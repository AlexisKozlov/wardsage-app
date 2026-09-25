using System.Text.Json;
using System.Text.Json.Nodes;

namespace RealmForge.Bridge.State;

/// <summary>An immutable view of the state: readers get it without locks and without copying.</summary>
public sealed record StateSnapshot(long Version, DateTimeOffset UpdatedAt, JsonElement Data);

/// <summary>
/// The current data as one JSON object. Writers (the action worker) mutate it under a lock; every change bumps the
/// version and publishes a fresh immutable snapshot for GET /api/state.
/// </summary>
public sealed class StateStore
{
    readonly object gate = new();
    readonly JsonObject data;
    long version;
    volatile StateSnapshot current;

    public StateStore(BridgeOptions options, ILogger<StateStore> log)
    {
        data = Load(options.StateFile, log);
        current = new StateSnapshot(0, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(data));
    }

    public StateSnapshot Snapshot => current;

    /// <summary>Sets the value at the path, creating missing parent objects.</summary>
    /// <exception cref="InvalidOperationException">A parent on the path exists but is not an object.</exception>
    public void Set(IReadOnlyList<string> path, JsonNode? value)
    {
        lock (gate)
        {
            // Nothing is created before a conflict can be found: a conflict needs an existing non-object parent, and
            // once one parent had to be created every deeper one is new too - so a failed Set leaves the state as it was.
            JsonObject parent = data;
            for (int i = 0; i < path.Count - 1; i++)
            {
                switch (parent[path[i]])
                {
                    case JsonObject child:
                        parent = child;
                        break;
                    case null:
                        var created = new JsonObject();
                        parent[path[i]] = created;
                        parent = created;
                        break;
                    default:
                        throw new InvalidOperationException($"'{string.Join('.', path.Take(i + 1))}' is not an object.");
                }
            }
            parent[path[^1]] = value;
            Commit();
        }
    }

    /// <summary>Removes the value at the path; false (and no new version) when there was nothing to remove.</summary>
    public bool Remove(IReadOnlyList<string> path)
    {
        lock (gate)
        {
            JsonObject? parent = data;
            for (int i = 0; i < path.Count - 1 && parent is not null; i++)
                parent = parent[path[i]] as JsonObject;

            if (parent is null || !parent.Remove(path[^1])) return false;
            Commit();
            return true;
        }
    }

    void Commit()
    {
        version++;
        current = new StateSnapshot(version, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(data));
    }

    static JsonObject Load(string? file, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(file)) return new JsonObject();
        if (!File.Exists(file))
        {
            log.LogWarning("State file {File} not found, starting with an empty state", file);
            return new JsonObject();
        }
        return JsonNode.Parse(File.ReadAllText(file)) as JsonObject
               ?? throw new InvalidOperationException($"State file {file} must contain a JSON object.");
    }
}
