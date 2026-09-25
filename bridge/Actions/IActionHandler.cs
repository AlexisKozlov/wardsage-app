using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace RealmForge.Bridge.Actions;

/// <summary>
/// One command type. To connect the system worker, add a handler for each of its commands and register it in
/// Program.cs (<c>AddSingleton&lt;IActionHandler, MyHandler&gt;()</c>).
/// </summary>
public interface IActionHandler
{
    /// <summary>The command's "type", e.g. "state.set".</summary>
    string Type { get; }

    /// <summary>Checks "params" when the request arrives (a JSON object); every problem goes to <paramref name="errors"/>.
    /// Nothing is queued if any command of the batch has errors.</summary>
    void Validate(JsonElement parameters, ICollection<string> errors);

    /// <summary>Runs on the worker, one command at a time. Throwing fails the command and skips the rest of the batch;
    /// long work must observe <paramref name="cancellationToken"/> (it fires on shutdown).</summary>
    Task ExecuteAsync(ActionCommand command, CancellationToken cancellationToken);
}

public sealed class ActionHandlerRegistry
{
    readonly Dictionary<string, IActionHandler> handlers;

    public ActionHandlerRegistry(IEnumerable<IActionHandler> all)
    {
        handlers = new Dictionary<string, IActionHandler>(StringComparer.Ordinal);
        foreach (var handler in all)
        {
            if (!handlers.TryAdd(handler.Type, handler))
                throw new InvalidOperationException($"Two action handlers for type '{handler.Type}'.");
        }
    }

    public IEnumerable<string> Types => handlers.Keys.Order(StringComparer.Ordinal);

    public bool TryGet(string type, [NotNullWhen(true)] out IActionHandler? handler) =>
        handlers.TryGetValue(type, out handler);

    public IActionHandler Get(string type) =>
        TryGet(type, out var handler) ? handler : throw new KeyNotFoundException($"No handler for '{type}'.");
}
