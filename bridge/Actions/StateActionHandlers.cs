using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RealmForge.Bridge.State;

namespace RealmForge.Bridge.Actions;

/// <summary><c>{"type":"state.set","params":{"path":"heroes.1200001.note","value":...}}</c> - any JSON value, null included.</summary>
public sealed class SetStateHandler(StateStore state) : IActionHandler
{
    public string Type => "state.set";

    public void Validate(JsonElement parameters, ICollection<string> errors)
    {
        StatePath.Validate(parameters, errors);
        if (!parameters.TryGetProperty("value", out _)) errors.Add("'value' is required (null is allowed).");
        StatePath.RejectUnknown(parameters, errors, "path", "value");
    }

    public Task ExecuteAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        var path = StatePath.Of(command.Params);
        state.Set(path, JsonNode.Parse(command.Params.GetProperty("value").GetRawText()));
        return Task.CompletedTask;
    }
}

/// <summary><c>{"type":"state.remove","params":{"path":"heroes.1200001.note"}}</c> - removing a missing value is not an error.</summary>
public sealed class RemoveStateHandler(StateStore state) : IActionHandler
{
    public string Type => "state.remove";

    public void Validate(JsonElement parameters, ICollection<string> errors)
    {
        StatePath.Validate(parameters, errors);
        StatePath.RejectUnknown(parameters, errors, "path");
    }

    public Task ExecuteAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        state.Remove(StatePath.Of(command.Params));
        return Task.CompletedTask;
    }
}

/// <summary>"a.b.c": 1..16 segments of letters, digits, '_' and '-'.</summary>
static partial class StatePath
{
    const int MaxDepth = 16;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex Segment();

    public static void Validate(JsonElement parameters, ICollection<string> errors)
    {
        if (!parameters.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String)
        {
            errors.Add("'path' is required and must be a string like \"a.b.c\".");
            return;
        }
        var segments = path.GetString()!.Split('.');
        if (segments.Length > MaxDepth || !segments.All(s => Segment().IsMatch(s)))
            errors.Add($"'path' must be 1..{MaxDepth} dot-separated segments of letters, digits, '_' or '-'.");
    }

    public static void RejectUnknown(JsonElement parameters, ICollection<string> errors, params string[] allowed)
    {
        foreach (var prop in parameters.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name))
                errors.Add($"Unknown parameter '{prop.Name}'. Allowed: {string.Join(", ", allowed)}.");
        }
    }

    /// <summary>The path of already validated params.</summary>
    public static string[] Of(JsonElement parameters) => parameters.GetProperty("path").GetString()!.Split('.');
}
