using RealmForge.Bridge.Actions;

namespace RealmForge.Bridge.Queue;

/// <summary>
/// Runs queued jobs one at a time, commands in order. On shutdown the running command gets the canceled token,
/// and the job's remaining commands and every job still in the queue are marked Canceled - never run half-way
/// through a stop (the actions may drive the game).
/// </summary>
public sealed class ActionWorker(ActionQueue queue, ActionHandlerRegistry handlers, ILogger<ActionWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = queue.Reader;
        try
        {
            // Not ReadAllAsync: it would still hand out an already queued job after the token fired.
            while (!stoppingToken.IsCancellationRequested && await reader.WaitToReadAsync(stoppingToken))
            {
                while (!stoppingToken.IsCancellationRequested && reader.TryRead(out var job))
                    await RunAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            queue.Complete();
            int canceled = 0;
            while (reader.TryRead(out var left))
            {
                left.Cancel(0);
                canceled++;
            }
            if (canceled > 0) log.LogInformation("Shutdown: {Count} queued job(s) canceled", canceled);
        }
    }

    async Task RunAsync(ActionJob job, CancellationToken stoppingToken)
    {
        log.LogInformation("Job {Job}: {Count} command(s)", job.Id, job.Commands.Count);
        job.Start();
        for (int i = 0; i < job.Commands.Count; i++)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                job.Cancel(i);
                return;
            }

            var command = job.Commands[i];
            job.CommandStarted(i);
            try
            {
                await handlers.Get(command.Type).ExecuteAsync(command, stoppingToken);
                job.CommandSucceeded(i);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Cancel(i);
                log.LogInformation("Job {Job} canceled at command {Command}", job.Id, command.Id);
                return;
            }
            catch (ActionFailedException e)
            {
                job.CommandFailed(i, e.Message);
                log.LogInformation("Job {Job}: command {Command} ({Type}) failed: {Error}", job.Id, command.Id, command.Type, e.Message);
                return;
            }
            catch (Exception e)
            {
                job.CommandFailed(i, e.Message);
                log.LogWarning(e, "Job {Job}: command {Command} ({Type}) failed", job.Id, command.Id, command.Type);
                return;
            }
        }
        job.Succeed();
    }
}
