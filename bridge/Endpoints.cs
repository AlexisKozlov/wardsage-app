using System.Text.Json;
using RealmForge.Bridge.Account;
using RealmForge.Bridge.Actions;
using RealmForge.Bridge.Host;
using RealmForge.Bridge.Queue;
using RealmForge.Bridge.State;

namespace RealmForge.Bridge;

public sealed record QueueInfo(int Pending, int Capacity, bool Accepting);

public sealed record HostInfo(bool Connected, DateTimeOffset? LastSeen);

/// <param name="Data">Free-form dashboard data (state.set / state.remove).</param>
/// <param name="Account">The game account from RealmForge; null until the app has sent one.</param>
public sealed record StateResponse(
    long Version, DateTimeOffset UpdatedAt, JsonElement Data, AccountSnapshotDto? Account, QueueInfo Queue, HostInfo Host);

public static class Endpoints
{
    public static void MapBridgeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/state", GetState);
        api.MapPost("/actions/apply", ApplyAsync);
        api.MapGet("/jobs/{id:guid}", GetJob);
    }

    static StateResponse GetState(StateStore state, AccountSnapshotStore account, ActionQueue queue, HostLink host)
    {
        var snapshot = state.Snapshot;
        return new StateResponse(snapshot.Version, snapshot.UpdatedAt, snapshot.Data, account.Current?.Snapshot,
                                 new QueueInfo(queue.Pending, queue.Capacity, queue.Accepting),
                                 new HostInfo(host.IsConnected, host.LastSeen));
    }

    /// <summary>202 + Location: /api/jobs/{id} when queued; 400 with per-command errors when anything is invalid
    /// (then nothing is queued); 415 for a non-JSON body; 503 when the queue is full or the service is stopping.</summary>
    static async Task<IResult> ApplyAsync(
        HttpContext context, ActionBatchParser parser, ActionQueue queue, JobStore jobs, CancellationToken cancellationToken)
    {
        var request = context.Request;
        // application/json also forces a CORS preflight, so a foreign page cannot send this as a "simple" request.
        if (!request.HasJsonContentType())
            return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType,
                                   title: "Content-Type must be application/json.");

        ActionBatch batch;
        try
        {
            batch = await parser.ParseAsync(request.Body, cancellationToken);
        }
        catch (BadHttpRequestException e)   // Kestrel's MaxRequestBodySize
        {
            return Results.Problem(statusCode: e.StatusCode, title: e.Message);
        }
        if (!batch.IsValid)
            return Results.ValidationProblem(batch.Errors, title: "The command batch is invalid; nothing was queued.");

        var job = new ActionJob(batch.Commands);
        jobs.Add(job);
        var result = queue.TryEnqueue(job);
        if (result == EnqueueResult.Accepted)
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToView());

        jobs.Remove(job);
        if (result == EnqueueResult.Full) context.Response.Headers.RetryAfter = "1";
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                               title: result == EnqueueResult.Full ? "The action queue is full." : "The service is stopping.");
    }

    static IResult GetJob(Guid id, JobStore jobs) =>
        jobs.Find(id) is { } job ? Results.Ok(job.ToView()) : Results.Problem(statusCode: 404, title: "No such job.");
}
