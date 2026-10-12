using Microsoft.EntityFrameworkCore;
using MovieRecap.Jobs.Application;
using MovieRecap.Jobs.Domain;
namespace MovieRecap.Infrastructure;

internal sealed class PostgresJobTransaction(PlatformDbContext db, TimeProvider clock) : IJobTransaction
{
    public async Task<JobView?> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return job is null ? null : JobView.From(job);
    }
    public Task<JobView> CreateAsync(CreateJob command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var job = ProcessingJob.Create(command.Kind, command.IdempotencyKey, command.MaxAttempts, clock.GetUtcNow());
        db.Jobs.Add(job); Audit(job, "created");
        return Task.FromResult(JobView.From(job));
    }
    public async Task CompleteAsync(JobLease lease, CancellationToken ct)
    {
        var job = await db.Jobs.FromSqlInterpolated($"SELECT * FROM jobs.\"ProcessingJobs\" WHERE \"Id\" = {lease.JobId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new JobConflictException("Job no longer exists.");
        if (job.Kind != lease.Kind) throw new JobConflictException("Job kind does not match.");
        job.Complete(lease.Token, clock.GetUtcNow()); Audit(job, "succeeded");
    }
    void Audit(ProcessingJob job, string action) => db.Audits.Add(new()
        { JobId = job.Id, Action = action, Status = job.Status, Attempt = job.Attempts, At = clock.GetUtcNow() });
}
