using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using RealmForge.Bridge.Account;

namespace RealmForge.Bridge.Host;

public sealed record SnapshotAccepted(long Version, int Heroes, int Items);

public sealed record HostCommandBatch(IReadOnlyList<HostCommand> Commands);

/// <param name="Status">"done" | "cancelled" | "failed"</param>
public sealed record HostResultBody(string Status, string? Message);

/// <summary>
/// The API RealmForge.exe talks to (same token as the dashboard, read from the token file):
///   PUT  /api/host/snapshot                 body: account.json as the extractor produced it;
///                                           X-Captured-At: when the reading started (ISO 8601), X-Game-Version
///   GET  /api/host/commands?wait=25         long poll -> {commands:[{id, type, issuedAt, payload}]}
///   POST /api/host/commands/{id}/result     {status:"done"|"cancelled"|"failed", message?}
/// Pages never call these: a request with an Origin header is refused.
/// </summary>
public static class HostEndpoints
{
    const int MaxWaitSeconds = 30;

    public static void MapHostApi(this WebApplication app)
    {
        var host = app.MapGroup("/api/host").AddEndpointFilter(OnlyTheApp);
        host.MapPut("/snapshot", PutSnapshotAsync);
        host.MapGet("/commands", TakeCommandsAsync);
        host.MapPost("/commands/{id:guid}/result", PostResult);
    }

    static ValueTask<object?> OnlyTheApp(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Request.Headers.ContainsKey(HeaderNames.Origin)
            ? ValueTask.FromResult<object?>(Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                                                            title: "The host API is for RealmForge.exe, not for web pages."))
            : next(context);

    static async Task<IResult> PutSnapshotAsync(
        HttpContext context, GameReference reference, AccountSnapshotStore store, BridgeOptions options, ILogger<AccountSnapshotStore> log,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (!request.HasJsonContentType())
            return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Content-Type must be application/json.");

        // account.json is far bigger than an action batch
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = options.MaxSnapshotBytes;

        var capturedAt = DateTimeOffset.TryParse(request.Headers["X-Captured-At"], CultureInfo.InvariantCulture,
                                                 DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.UtcNow;
        string? gameVersion = request.Headers["X-Game-Version"];
        if (gameVersion is { Length: > 64 }) gameVersion = gameVersion[..64];

        AccountSnapshotDto snapshot;
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken);
            snapshot = AccountSnapshotMapper.Map(document.RootElement, reference, gameVersion, capturedAt);
        }
        catch (BadHttpRequestException e)
        {
            return Results.Problem(statusCode: e.StatusCode, title: e.Message);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not an account.json: " + e.Message);
        }

        if (!store.Replace(snapshot))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                                   title: "The reading started before the last equip made through the bridge; it was not applied.");

        var current = store.Current!.Snapshot;
        log.LogInformation("Snapshot v{Version}: {Heroes} heroes, {Items} items", current.Version, current.Heroes.Count, current.Items.Count);
        return Results.Ok(new SnapshotAccepted(current.Version, current.Heroes.Count, current.Items.Count));
    }

    static async Task<HostCommandBatch> TakeCommandsAsync(
        int? wait, HostLink link, IHostApplicationLifetime lifetime, CancellationToken cancellationToken)
    {
        var seconds = Math.Clamp(wait ?? 25, 0, MaxWaitSeconds);
        // a pending poll must not hold up shutdown
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        try
        {
            return new HostCommandBatch(await link.TakeAsync(TimeSpan.FromSeconds(seconds), cts.Token));
        }
        catch (OperationCanceledException)
        {
            return new HostCommandBatch([]);
        }
    }

    static IResult PostResult(Guid id, HostResultBody body, HostLink link)
    {
        HostReplyStatus? status = body.Status switch
        {
            "done" => HostReplyStatus.Done,
            "cancelled" => HostReplyStatus.Cancelled,
            "failed" => HostReplyStatus.Failed,
            _ => null,
        };
        if (status is null)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "status must be done, cancelled or failed.");

        string? message = body.Message is { Length: > 500 } m ? m[..500] : body.Message;
        return link.Complete(id, new HostReply(status.Value, message))
            ? Results.NoContent()
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No command waits for this answer (answered, timed out or unknown).");
    }
}
