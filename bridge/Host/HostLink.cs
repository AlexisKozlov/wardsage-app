using System.Collections.Concurrent;
using System.Threading.Channels;
using RealmForge.Bridge.Equipment;

namespace RealmForge.Bridge.Host;

public static class HostCommandTypes
{
    public const string Equip = "equip";
}

/// <summary>A command for RealmForge.exe; <paramref name="Payload"/> depends on <paramref name="Type"/>.</summary>
public sealed record HostCommand(Guid Id, string Type, DateTimeOffset IssuedAt, object Payload);

public sealed record HostEquipPayload(string CommandId, long HeroId, string? HeroName, IReadOnlyList<SlotAssignment> Slots);

public enum HostReplyStatus { Done, Cancelled, Failed, TimedOut, NotConnected }

public sealed record HostReply(HostReplyStatus Status, string? Message = null);

/// <summary>
/// Commands from the bridge to RealmForge.exe. The app has no server of its own: it long-polls
/// GET /api/host/commands (which also tells the bridge it is alive) and answers each command with
/// POST /api/host/commands/{id}/result. A sender waits for that answer, or gives up when the app stops polling.
/// </summary>
public sealed class HostLink(BridgeOptions options)
{
    const int MaxBatch = 16;
    static readonly TimeSpan AliveCheck = TimeSpan.FromSeconds(5);

    sealed class Pending(HostCommand command)
    {
        public HostCommand Command { get; } = command;
        public TaskCompletionSource<HostReply> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly Channel<Pending> outbox = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions { SingleReader = false });
    readonly ConcurrentDictionary<Guid, Pending> open = new();
    long lastSeenTicks;   // UTC ticks of the app's last poll, 0 = never

    public DateTimeOffset? LastSeen
    {
        get
        {
            long ticks = Interlocked.Read(ref lastSeenTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public bool IsConnected =>
        LastSeen is { } seen && DateTimeOffset.UtcNow - seen < TimeSpan.FromSeconds(options.HostOfflineAfterSeconds);

    /// <summary>Queues a command for the app and waits for its answer (or the timeout, or the app going away).
    /// <paramref name="cancellationToken"/> (shutdown) throws.</summary>
    public async Task<HostReply> SendAsync(string type, object payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsConnected) return new HostReply(HostReplyStatus.NotConnected);

        var pending = new Pending(new HostCommand(Guid.NewGuid(), type, DateTimeOffset.UtcNow, payload));
        open[pending.Command.Id] = pending;
        outbox.Writer.TryWrite(pending);
        try
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                var left = deadline - DateTimeOffset.UtcNow;
                if (left <= TimeSpan.Zero)
                    return new HostReply(HostReplyStatus.TimedOut, $"No answer from RealmForge in {timeout.TotalSeconds:0} s.");
                try
                {
                    return await pending.Reply.Task.WaitAsync(left < AliveCheck ? left : AliveCheck, cancellationToken);
                }
                catch (TimeoutException) when (!IsConnected)
                {
                    return new HostReply(HostReplyStatus.NotConnected, "RealmForge stopped polling the bridge.");
                }
                catch (TimeoutException)
                {
                }
            }
        }
        finally
        {
            open.TryRemove(pending.Command.Id, out _);
        }
    }

    /// <summary>The app's long poll: waits up to <paramref name="wait"/> for commands. Commands whose sender already
    /// gave up are dropped. <paramref name="cancellationToken"/> (client gone, shutdown) throws before taking any.</summary>
    public async Task<IReadOnlyList<HostCommand>> TakeAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        Touch();
        using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            waitCts.CancelAfter(wait);
            try
            {
                await outbox.Reader.WaitToReadAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
        Touch();

        var commands = new List<HostCommand>();
        while (commands.Count < MaxBatch && outbox.Reader.TryRead(out var pending))
            if (open.ContainsKey(pending.Command.Id)) commands.Add(pending.Command);
        return commands;
    }

    /// <returns>false when no sender waits for this id (answered already, timed out, unknown).</returns>
    public bool Complete(Guid id, HostReply reply) => open.TryGetValue(id, out var pending) && pending.Reply.TrySetResult(reply);

    void Touch() => Interlocked.Exchange(ref lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);
}
