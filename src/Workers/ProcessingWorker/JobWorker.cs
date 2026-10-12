using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Jobs.Domain;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
namespace MovieRecap.Worker;

public sealed class WorkerOptions { public int PollSeconds { get; set; } = 2; public int LeaseSeconds { get; set; } = 60; }
public sealed class JobWorker(IJobStore store, IDbContextFactory<PlatformDbContext> factory, ImportExecutor imports, StreamInspectionExecutor inspections, SceneExecutor scenes, IOptions<WorkerOptions> options, ILogger<JobWorker> log) : BackgroundService
{
    readonly string workerId = Environment.MachineName + ":" + Guid.NewGuid().ToString("N");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var leaseTime = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
        log.LogInformation("Worker {WorkerId} started; concurrency limit 1", workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var lease = await store.ClaimAsync(workerId, leaseTime, stoppingToken);
                if (lease is not null) await ExecuteJob(lease, leaseTime, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Worker poll failed; will retry"); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        log.LogInformation("Worker {WorkerId} stopped", workerId);
    }
    async Task ExecuteJob(JobLease job, TimeSpan leaseTime, CancellationToken stop)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var progress = 0;
        var heartbeat = KeepLease(job, leaseTime, execution, () => Volatile.Read(ref progress));
        try
        {
            if (job.Kind == "media.manual-import") await imports.ExecuteAsync(job, execution.Token, value => Volatile.Write(ref progress, value));
            else if (job.Kind == "media.stream-inspection") await inspections.ExecuteAsync(job, execution.Token, value => Volatile.Write(ref progress, value));
            else if (job.Kind == "video.scene-analysis") await scenes.ExecuteAsync(job, execution.Token, value => Volatile.Write(ref progress, value));
            else if (job.Kind == "foundation.database-check")
            {
                await using var db = await factory.CreateDbContextAsync(execution.Token);
                await db.Database.ExecuteSqlRawAsync("SELECT 1", execution.Token);
                await store.CompleteAsync(job, execution.Token);
            }
            else throw new InvalidOperationException("Unknown job kind.");
            log.LogInformation("Job {JobId} succeeded", job.JobId);
        }
        catch (JobConflictException) { log.LogInformation("Job {JobId} lease revoked", job.JobId); }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            log.LogInformation("Job {JobId} interrupted; expired leases recover automatically", job.JobId);
        }
        catch (Exception ex)
        {
            var error = ex is SceneException sceneError ? sceneError.Code : ex is ImportException importError ? importError.Code : job.Kind == "media.manual-import" ? "media-import-failed" : job.Kind == "media.stream-inspection" ? "stream-inspection-failed" : job.Kind == "video.scene-analysis" ? "scene-analysis-failed" : "database-check-failed";
            // FFprobe/provider output and user-supplied media metadata never enter logs.
            log.LogError("Job {JobId} failed with {ErrorCode}", job.JobId, error);
            try { await store.FailAsync(job, error, stop); }
            catch (JobConflictException) { log.LogInformation("Job {JobId} lease revoked before failure could be saved", job.JobId); }
        }
        finally { await execution.CancelAsync(); await heartbeat; }
    }
    async Task KeepLease(JobLease job, TimeSpan leaseTime, CancellationTokenSource execution, Func<int> progress)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(leaseTime.TotalSeconds / 3));
            while (await timer.WaitForNextTickAsync(execution.Token))
                await store.HeartbeatAsync(job, progress(), leaseTime, execution.Token);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
        catch (Exception ex) { log.LogWarning(ex, "Job {JobId} heartbeat lost; stopping execution", job.JobId); await execution.CancelAsync(); }
    }
}

