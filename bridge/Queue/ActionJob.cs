using RealmForge.Bridge.Actions;

namespace RealmForge.Bridge.Queue;

public enum JobStatus { Queued, Running, Succeeded, Failed, Canceled }

public enum CommandStatus { Pending, Running, Succeeded, Failed, Skipped, Canceled }

public sealed record CommandView(string Id, string Type, CommandStatus Status, string? Error);

public sealed record JobView(
    Guid Id, JobStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    IReadOnlyList<CommandView> Commands);

/// <summary>
/// One accepted batch. Its commands run in order on the worker; the first failure fails the job and skips the rest.
/// Only the worker changes a job, the API reads it: every access goes through the lock.
/// </summary>
public sealed class ActionJob
{
    readonly object gate = new();
    readonly CommandStatus[] statuses;
    readonly string?[] errors;
    JobStatus status = JobStatus.Queued;
    DateTimeOffset? startedAt, finishedAt;

    public ActionJob(IReadOnlyList<ActionCommand> commands)
    {
        Commands = commands;
        statuses = new CommandStatus[commands.Count];
        errors = new string?[commands.Count];
    }

    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ActionCommand> Commands { get; }

    public bool IsFinished
    {
        get { lock (gate) return status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled; }
    }

    public void Start()
    {
        lock (gate)
        {
            status = JobStatus.Running;
            startedAt = DateTimeOffset.UtcNow;
        }
    }

    public void CommandStarted(int index)
    {
        lock (gate) statuses[index] = CommandStatus.Running;
    }

    public void CommandSucceeded(int index)
    {
        lock (gate) statuses[index] = CommandStatus.Succeeded;
    }

    public void CommandFailed(int index, string error)
    {
        lock (gate)
        {
            statuses[index] = CommandStatus.Failed;
            errors[index] = error;
            for (int i = index + 1; i < statuses.Length; i++) statuses[i] = CommandStatus.Skipped;
            Finish(JobStatus.Failed);
        }
    }

    /// <summary>Shutdown: the command at <paramref name="fromIndex"/> and all after it are canceled.</summary>
    public void Cancel(int fromIndex)
    {
        lock (gate)
        {
            for (int i = fromIndex; i < statuses.Length; i++) statuses[i] = CommandStatus.Canceled;
            Finish(JobStatus.Canceled);
        }
    }

    public void Succeed()
    {
        lock (gate) Finish(JobStatus.Succeeded);
    }

    void Finish(JobStatus final)
    {
        status = final;
        finishedAt = DateTimeOffset.UtcNow;
    }

    public JobView ToView()
    {
        lock (gate)
        {
            var commands = new CommandView[Commands.Count];
            for (int i = 0; i < commands.Length; i++)
                commands[i] = new CommandView(Commands[i].Id, Commands[i].Type, statuses[i], errors[i]);
            return new JobView(Id, status, CreatedAt, startedAt, finishedAt, commands);
        }
    }
}
