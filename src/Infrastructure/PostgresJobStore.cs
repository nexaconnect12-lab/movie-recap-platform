using Microsoft.EntityFrameworkCore;
using MovieRecap.Jobs.Application;
using MovieRecap.Jobs.Domain;
using Npgsql;
namespace MovieRecap.Infrastructure;

public sealed class PostgresJobStore(IDbContextFactory<PlatformDbContext> factory, TimeProvider clock) : IJobStore
{
    void Audit(PlatformDbContext db, ProcessingJob job, string action) =>
        db.Audits.Add(new() { JobId = job.Id, Action = action, Status = job.Status, Attempt = job.Attempts, At = clock.GetUtcNow() });
    public async Task<JobView> CreateAsync(CreateJob command, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = ProcessingJob.Create(command.Kind, command.IdempotencyKey, command.MaxAttempts, clock.GetUtcNow());
        db.Jobs.Add(job); Audit(db, job, "created");
        try { await db.SaveChangesAsync(ct); return JobView.From(job); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear();
            var existing = await db.Jobs.SingleAsync(x => x.IdempotencyKey == command.IdempotencyKey, ct);
            if (existing.Kind != command.Kind || existing.MaxAttempts != command.MaxAttempts)
                throw new JobConflictException("Idempotency key already used with different parameters.");
            return JobView.From(existing);
        }
    }
    public async Task<JobView?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var j = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return j is null ? null : JobView.From(j);
    }
    public async Task<IReadOnlyList<JobView>> ListAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Jobs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct)).Select(JobView.From).ToList();
    }
    public async Task<IReadOnlyList<JobAudit>> AuditAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Audits.AsNoTracking().Where(x => x.JobId == id).OrderBy(x => x.Sequence).ToListAsync(ct);
    }
    public async Task<JobView?> CommandAsync(Guid id, JobCommand command, Guid expectedVersion, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var j = await Locked(db, id, ct);
        if (j is null) return null;
        if (j.Version != expectedVersion) throw new JobConflictException("Job version changed; reload before retrying.");
        var now = clock.GetUtcNow();
        switch (command)
        {
            case JobCommand.Pause: j.Pause(now); break;
            case JobCommand.Resume: j.Resume(now); break;
            case JobCommand.Cancel: j.Cancel(now); break;
            case JobCommand.Retry: j.Retry(now); break;
        }
        Audit(db, j, command.ToString().ToLowerInvariant()); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return JobView.From(j);
    }
    static Task<ProcessingJob?> Locked(PlatformDbContext db, Guid id, CancellationToken ct) =>
        db.Jobs.FromSqlInterpolated($"SELECT * FROM jobs.\"ProcessingJobs\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
    public async Task<JobLease?> ClaimAsync(string worker, TimeSpan lease, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var j = (await db.Jobs.FromSqlInterpolated($"""
            SELECT * FROM jobs."ProcessingJobs"
            WHERE ("Status" IN ('Queued', 'RetryWait') AND "AvailableAt" <= {now})
               OR ("Status" = 'Running' AND "LeaseExpiresAt" <= {now})
            ORDER BY "AvailableAt", "CreatedAt" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct)).SingleOrDefault();
        if (j is null) return null;
        JobLease? result = null;
        if (j.Status == JobStatus.Running) { j.Recover(now); Audit(db, j, "lease-recovered"); }
        else { result = new(j.Id, j.Start(worker, now, lease), j.Kind); Audit(db, j, "started"); }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result;
    }
    async Task Mutate(JobLease lease, Action<ProcessingJob, DateTimeOffset> action, string? audit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var j = await Locked(db, lease.JobId, ct) ?? throw new JobConflictException("Job no longer exists.");
        action(j, clock.GetUtcNow()); if (audit is not null) Audit(db, j, audit);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public Task HeartbeatAsync(JobLease j, int progress, TimeSpan lease, CancellationToken ct) =>
        Mutate(j, (x, now) => x.Heartbeat(j.Token, progress, now, lease), null, ct);
    public Task CompleteAsync(JobLease j, CancellationToken ct) => Mutate(j, (x, now) => x.Complete(j.Token, now), "succeeded", ct);
    public Task FailAsync(JobLease j, string errorCode, CancellationToken ct) => Mutate(j, (x, now) => x.Fail(j.Token, errorCode, now), "execution-failed", ct);
}

