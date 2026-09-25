using System.Text.Json;
using System.Text.RegularExpressions;

namespace RealmForge.Bridge.Actions;

public sealed record ActionBatch(IReadOnlyList<ActionCommand> Commands, IDictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Reads and validates the body of POST /api/actions/apply:
/// <code>[{ "id": "c1", "type": "state.set", "params": { ... } }, ...]</code>
/// Strict: unknown or duplicate properties are errors, "params" (optional, default {}) must be an object and pass its
/// handler's own checks. Error keys are JSON paths ("[0].id", "[1].params") for a 400 ValidationProblem response.
/// </summary>
public sealed partial class ActionBatchParser(ActionHandlerRegistry registry, BridgeOptions options)
{
    const int MaxIdLength = 64;
    static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    [GeneratedRegex("^[A-Za-z0-9._:-]+$")]
    private static partial Regex IdPattern();

    public async Task<ActionBatch> ParseAsync(Stream body, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Error(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list)) errors[key] = list = new List<string>();
            list.Add(message);
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
        }
        catch (JsonException e)
        {
            Error("$", "The body is not valid JSON: " + e.Message);
            return Result([], errors);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                Error("$", "Expected a JSON array of commands.");
                return Result([], errors);
            }

            int count = root.GetArrayLength();
            if (count == 0) Error("$", "At least one command is required.");
            if (count > options.MaxCommandsPerRequest) Error("$", $"At most {options.MaxCommandsPerRequest} commands per request.");
            if (errors.Count > 0) return Result([], errors);

            var commands = new List<ActionCommand>(count);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            int index = 0;
            foreach (var item in root.EnumerateArray())
            {
                string at = $"[{index++}]";
                if (item.ValueKind != JsonValueKind.Object)
                {
                    Error(at, "A command must be an object.");
                    continue;
                }

                string? id = null, type = null;
                JsonElement parameters = EmptyObject;
                var seenProps = new HashSet<string>(StringComparer.Ordinal);
                foreach (var prop in item.EnumerateObject())
                {
                    if (!seenProps.Add(prop.Name))
                    {
                        Error(at, $"Duplicate property '{prop.Name}'.");
                        continue;
                    }
                    switch (prop.Name)
                    {
                        case "id": id = ReadString(prop.Value, at + ".id", Error); break;
                        case "type": type = ReadString(prop.Value, at + ".type", Error); break;
                        case "params": parameters = prop.Value; break;
                        default: Error(at, $"Unknown property '{prop.Name}'. Allowed: id, type, params."); break;
                    }
                }

                if (id is null) { if (!item.TryGetProperty("id", out _)) Error(at + ".id", "Required."); }
                else if (id.Length is 0 or > MaxIdLength || !IdPattern().IsMatch(id))
                    Error(at + ".id", $"1..{MaxIdLength} characters: letters, digits, '.', '_', ':', '-'.");
                else if (!seenIds.Add(id))
                    Error(at + ".id", $"Duplicate id '{id}' in this batch.");

                IActionHandler? handler = null;
                if (type is null) { if (!item.TryGetProperty("type", out _)) Error(at + ".type", "Required."); }
                else if (!registry.TryGet(type, out handler))
                    Error(at + ".type", $"Unknown command type '{type}'. Known: {string.Join(", ", registry.Types)}.");

                if (parameters.ValueKind != JsonValueKind.Object)
                {
                    Error(at + ".params", "Must be an object.");
                }
                else if (FindDuplicateKey(parameters, "params") is { } duplicate)
                {
                    Error(at + ".params", $"Duplicate property at {duplicate}.");
                }
                else if (handler is not null)
                {
                    var handlerErrors = new List<string>();
                    handler.Validate(parameters, handlerErrors);
                    foreach (var message in handlerErrors) Error(at + ".params", message);
                }

                if (id is not null && type is not null)
                    commands.Add(new ActionCommand(id, type, parameters.Clone()));
            }
            return Result(commands, errors);
        }
    }

    static ActionBatch Result(List<ActionCommand> commands, Dictionary<string, List<string>> errors) =>
        errors.Count == 0
            ? new ActionBatch(commands, new Dictionary<string, string[]>())
            : new ActionBatch([], errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

    static string? ReadString(JsonElement value, string at, Action<string, string> error)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        error(at, "Must be a string.");
        return null;
    }

    /// <summary>JSON allows repeated keys, JsonObject does not: reject them here rather than fail later on the worker.</summary>
    static string? FindDuplicateKey(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var prop in element.EnumerateObject())
                {
                    string at = $"{path}.{prop.Name}";
                    if (!names.Add(prop.Name)) return at;
                    if (FindDuplicateKey(prop.Value, at) is { } inner) return inner;
                }
                return null;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (FindDuplicateKey(item, $"{path}[{i++}]") is { } inner) return inner;
                }
                return null;
            default:
                return null;
        }
    }
}
