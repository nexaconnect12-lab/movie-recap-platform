using FluentValidation;
using MovieRecap.Jobs.Domain;
namespace MovieRecap.Jobs.Application;

public sealed record CreateJob(string Kind, string IdempotencyKey, int MaxAttempts = 3);
public sealed class CreateJobValidator : AbstractValidator<CreateJob>
{
    public CreateJobValidator()
    {
        RuleFor(x => x.Kind).Equal("foundation.database-check");
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(128).Matches(@"^[A-Za-z0-9._:-]+$");
        RuleFor(x => x.IdempotencyKey).Must(x => x is null || !(x.StartsWith("media-import:", StringComparison.Ordinal) || x.StartsWith("stream-inspection:", StringComparison.Ordinal) || x.StartsWith("scene-analysis:", StringComparison.Ordinal) || x.StartsWith("subtitle-extraction:", StringComparison.Ordinal) || x.StartsWith("audio-extraction:", StringComparison.Ordinal) || x.StartsWith("frame-sampling:", StringComparison.Ordinal) || x.StartsWith("preparation-validation:", StringComparison.Ordinal))).WithMessage("Module job key namespaces are reserved for their coordinators.");
        RuleFor(x => x.MaxAttempts).InclusiveBetween(1, 5);
    }
}
public enum JobCommand { Pause, Resume, Cancel, Retry }
public sealed record JobView(Guid Id, string Kind, JobStatus Status, int Attempts, int MaxAttempts,
    int Progress, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset AvailableAt,
    string? WorkerId, DateTimeOffset? LeaseExpiresAt, string? ErrorCode, Guid Version)
{
    public static JobView From(ProcessingJob j) => new(j.Id, j.Kind, j.Status, j.Attempts, j.MaxAttempts,
        j.Progress, j.CreatedAt, j.UpdatedAt, j.AvailableAt, j.WorkerId, j.LeaseExpiresAt, j.ErrorCode, j.Version);
}
public sealed record JobLease(Guid JobId, Guid Token, string Kind);
public interface IJobStore
{
    Task<JobView> CreateAsync(CreateJob command, CancellationToken ct);
    Task<JobView?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<JobView>> ListAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<JobAudit>> AuditAsync(Guid id, CancellationToken ct);
    Task<JobView?> CommandAsync(Guid id, JobCommand command, Guid expectedVersion, CancellationToken ct);
    Task<JobLease?> ClaimAsync(string worker, TimeSpan lease, CancellationToken ct);
    Task HeartbeatAsync(JobLease job, int progress, TimeSpan lease, CancellationToken ct);
    Task CompleteAsync(JobLease job, CancellationToken ct);
    Task FailAsync(JobLease job, string errorCode, CancellationToken ct);
}
public sealed class JobService(IJobStore store, IValidator<CreateJob> validator)
{
    public async Task<JobView> CreateAsync(CreateJob command, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(command, ct);
        return await store.CreateAsync(command, ct);
    }
}




