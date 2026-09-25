namespace RealmForge.Bridge.Queue;

/// <summary>Jobs by id for GET /api/jobs/{id}. Keeps every unfinished job and the last N finished ones.</summary>
public sealed class JobStore(BridgeOptions options)
{
    readonly object gate = new();
    readonly Dictionary<Guid, ActionJob> jobs = new();
    readonly LinkedList<ActionJob> order = new();

    public void Add(ActionJob job)
    {
        lock (gate)
        {
            jobs.Add(job.Id, job);
            order.AddLast(job);
            Prune();
        }
    }

    /// <summary>Takes back a job the queue refused.</summary>
    public void Remove(ActionJob job)
    {
        lock (gate)
        {
            if (jobs.Remove(job.Id)) order.Remove(job);
        }
    }

    public ActionJob? Find(Guid id)
    {
        lock (gate) return jobs.GetValueOrDefault(id);
    }

    void Prune()
    {
        int finished = order.Count(j => j.IsFinished);
        for (var node = order.First; node is not null && finished > options.KeepFinishedJobs;)
        {
            var nextNode = node.Next;
            if (node.Value.IsFinished)
            {
                jobs.Remove(node.Value.Id);
                order.Remove(node);
                finished--;
            }
            node = nextNode;
        }
    }
}
