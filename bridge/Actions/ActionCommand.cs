using System.Text.Json;

namespace RealmForge.Bridge.Actions;

/// <summary>One validated command of a POST /api/actions/apply batch.</summary>
/// <param name="Id">Caller's id, unique within its batch.</param>
/// <param name="Type">Name of a registered <see cref="IActionHandler"/>.</param>
/// <param name="Params">A JSON object, detached from the request (safe to keep after the request ends).</param>
public sealed record ActionCommand(string Id, string Type, JsonElement Params);
