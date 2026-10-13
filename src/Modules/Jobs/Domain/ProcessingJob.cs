namespace MovieRecap.Jobs.Domain;

public enum JobStatus { Queued, Running, RetryWait, Paused, Succeeded, Failed, Cancelled }
public sealed class JobConflictException(string message) : Exception(message);
public sealed class ProcessingJob
{
    private ProcessingJob() { }
    public Guid Id { get; private set; }
    public string Kind { get; private set; } = "";
    public string IdempotencyKey { get; private set; } = "";
    public JobStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public int MaxAttempts { get; private set; }
    public int Progress { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset AvailableAt { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public string? WorkerId { get; private set; }
    public string? ErrorCode { get; private set; }
    public Guid Version { get; private set; }

    public static ProcessingJob Create(string kind, string key, int maxAttempts, DateTimeOffset now)
    {
        if (kind is not ("foundation.database-check" or "media.manual-import" or "media.stream-inspection" or "video.scene-analysis" or "video.audio-extraction" or "video.frame-sampling" or "video.preparation-validation")) throw new ArgumentException("Unsupported job kind.");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) throw new ArgumentException("Invalid idempotency key.");
        if (maxAttempts is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        return new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            IdempotencyKey = key,
            MaxAttempts = maxAttempts,
            Status = JobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = now,
            Version = Guid.NewGuid()
        };
    }
    void Touch(DateTimeOffset now) { UpdatedAt = now; Version = Guid.NewGuid(); }
    void ClearLease() { LeaseToken = null; LeaseExpiresAt = null; WorkerId = null; }
    void Own(Guid token, DateTimeOffset now)
    {
        if (Status != JobStatus.Running || LeaseToken != token || LeaseExpiresAt <= now)
            throw new JobConflictException("Execution lease is no longer owned.");
    }
    public void RequireLease(Guid token, DateTimeOffset now) => Own(token, now);
    public Guid Start(string worker, DateTimeOffset now, TimeSpan lease)
    {
        if (Status is not (JobStatus.Queued or JobStatus.RetryWait) || AvailableAt > now || Attempts >= MaxAttempts)
            throw new JobConflictException("Job cannot start.");
        if (string.IsNullOrWhiteSpace(worker) || worker.Length > 128 || lease <= TimeSpan.Zero)
            throw new ArgumentException("Invalid worker or lease.");
        Status = JobStatus.Running; Attempts++; Progress = 0; ErrorCode = null;
        WorkerId = worker; LeaseToken = Guid.NewGuid(); LeaseExpiresAt = now + lease; Touch(now);
        return LeaseToken.Value;
    }
    public void Heartbeat(Guid token, int progress, DateTimeOffset now, TimeSpan lease)
    {
        Own(token, now);
        if (progress < Progress || progress is < 0 or > 99 || lease <= TimeSpan.Zero) throw new ArgumentException("Invalid progress or lease.");
        Progress = progress; LeaseExpiresAt = now + lease; Touch(now);
    }
    public void Complete(Guid token, DateTimeOffset now)
    {
        Own(token, now); Status = JobStatus.Succeeded; Progress = 100; ClearLease(); Touch(now);
    }
    public void Fail(Guid token, string errorCode, DateTimeOffset now)
    {
        Own(token, now); Failure(errorCode, now);
    }
    void Failure(string errorCode, DateTimeOffset now)
    {
        ErrorCode = errorCode; Status = Attempts >= MaxAttempts ? JobStatus.Failed : JobStatus.RetryWait;
        AvailableAt = now + TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Attempts - 1)));
        ClearLease(); Touch(now);
    }
    public void Recover(DateTimeOffset now)
    {
        if (Status != JobStatus.Running || LeaseExpiresAt > now) throw new JobConflictException("Lease has not expired.");
        Failure("lease-expired", now);
    }
    public void Pause(DateTimeOffset now)
    {
        if (Status is not (JobStatus.Queued or JobStatus.RetryWait or JobStatus.Running)) throw new JobConflictException("Job cannot pause.");
        Status = JobStatus.Paused; ClearLease(); Touch(now);
    }
    public void Resume(DateTimeOffset now)
    {
        if (Status != JobStatus.Paused) throw new JobConflictException("Job is not paused.");
        Status = Attempts >= MaxAttempts ? JobStatus.Failed : JobStatus.Queued; AvailableAt = now; Touch(now);
    }
    public void Cancel(DateTimeOffset now)
    {
        if (Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled) throw new JobConflictException("Job is terminal.");
        Status = JobStatus.Cancelled; ClearLease(); Touch(now);
    }
    public void Retry(DateTimeOffset now)
    {
        if (Status != JobStatus.Failed) throw new JobConflictException("Only failed jobs can retry.");
        Attempts = 0; Progress = 0; ErrorCode = null; Status = JobStatus.Queued; AvailableAt = now; Touch(now);
    }
}
public sealed class JobAudit
{
    public long Sequence { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string Action { get; set; } = "";
    public JobStatus Status { get; set; }
    public int Attempt { get; set; }
    public DateTimeOffset At { get; set; }
}



