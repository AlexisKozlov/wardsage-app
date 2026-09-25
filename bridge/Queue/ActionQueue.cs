using System.Threading.Channels;

namespace RealmForge.Bridge.Queue;

public enum EnqueueResult { Accepted, Full, Closed }

/// <summary>
/// Bounded Channel between the HTTP endpoint (many writers) and <see cref="ActionWorker"/> (the single reader).
/// A full queue is refused at once (503 + Retry-After) instead of holding the request open.
/// </summary>
public sealed class ActionQueue
{
    readonly Channel<ActionJob> channel;
    volatile bool closed;

    public ActionQueue(BridgeOptions options)
    {
        Capacity = options.QueueCapacity;
        channel = Channel.CreateBounded<ActionJob>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public int Capacity { get; }
    public int Pending => channel.Reader.Count;
    public bool Accepting => !closed;
    public ChannelReader<ActionJob> Reader => channel.Reader;

    public EnqueueResult TryEnqueue(ActionJob job)
    {
        if (closed) return EnqueueResult.Closed;
        if (channel.Writer.TryWrite(job)) return EnqueueResult.Accepted;
        return closed ? EnqueueResult.Closed : EnqueueResult.Full;
    }

    /// <summary>No more jobs from now on (shutdown). The reader still gets everything already queued.</summary>
    public void Complete()
    {
        closed = true;
        channel.Writer.TryComplete();
    }
}
