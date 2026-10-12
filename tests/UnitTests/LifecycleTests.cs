using MovieRecap.Jobs.Application;
using MovieRecap.Jobs.Domain;
using Xunit;
namespace MovieRecap.UnitTests;

public class LifecycleTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    static ProcessingJob New(int attempts = 3) => ProcessingJob.Create("foundation.database-check", "test-key", attempts, Now);
    [Fact]
    public void Success_requires_owner_and_sets_full_progress()
    {
        var j = New(); var token = j.Start("worker", Now, TimeSpan.FromSeconds(60));
        Assert.Throws<JobConflictException>(() => j.Complete(Guid.NewGuid(), Now));
        j.Heartbeat(token, 50, Now.AddSeconds(1), TimeSpan.FromSeconds(60));
        j.Complete(token, Now.AddSeconds(2)); Assert.Equal(JobStatus.Succeeded, j.Status); Assert.Equal(100, j.Progress); Assert.Null(j.LeaseToken);
        Assert.Throws<JobConflictException>(() => j.Start("other", Now, TimeSpan.FromSeconds(60)));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Pause_and_cancel_revoke_active_execution(bool pause)
    {
        var j = New(); var token = j.Start("worker", Now, TimeSpan.FromSeconds(60));
        if (pause) j.Pause(Now); else j.Cancel(Now);
        Assert.Throws<JobConflictException>(() => j.Complete(token, Now));
        Assert.Null(j.LeaseToken);
        if (pause) { j.Resume(Now); Assert.Equal(JobStatus.Queued, j.Status); }
    }
    [Fact]
    public void Retry_uses_backoff_and_stops_at_budget()
    {
        var j = New(2); var token = j.Start("worker", Now, TimeSpan.FromSeconds(60));
        j.Fail(token, "test-failure", Now); Assert.Equal(JobStatus.RetryWait, j.Status); Assert.Equal(Now.AddSeconds(5), j.AvailableAt);
        Assert.Throws<JobConflictException>(() => j.Start("worker", Now, TimeSpan.FromSeconds(60)));
        token = j.Start("worker", Now.AddSeconds(5), TimeSpan.FromSeconds(60)); j.Fail(token, "test-failure", Now.AddSeconds(5));
        Assert.Equal(JobStatus.Failed, j.Status); j.Retry(Now.AddSeconds(6)); Assert.Equal(0, j.Attempts);
    }
    [Fact]
    public void Expired_lease_cannot_write_and_recovers()
    {
        var j = New(); var token = j.Start("worker", Now, TimeSpan.FromSeconds(15));
        Assert.Throws<JobConflictException>(() => j.Recover(Now.AddSeconds(14)));
        Assert.Throws<JobConflictException>(() => j.Complete(token, Now.AddSeconds(15)));
        j.Recover(Now.AddSeconds(15)); Assert.Equal(JobStatus.RetryWait, j.Status); Assert.Equal("lease-expired", j.ErrorCode);
        var next = j.Start("worker2", Now.AddSeconds(20), TimeSpan.FromSeconds(15));
        Assert.NotEqual(token, next); Assert.Throws<JobConflictException>(() => j.Complete(token, Now.AddSeconds(20)));
    }
    [Fact]
    public void Last_expired_attempt_fails()
    {
        var j = New(1); j.Start("worker", Now, TimeSpan.FromSeconds(15)); j.Recover(Now.AddSeconds(15));
        Assert.Equal(JobStatus.Failed, j.Status);
    }
    [Fact]
    public void Progress_cannot_decrease()
    {
        var j = New(); var token = j.Start("worker", Now, TimeSpan.FromSeconds(60));
        j.Heartbeat(token, 50, Now, TimeSpan.FromSeconds(60));
        Assert.Throws<ArgumentException>(() => j.Heartbeat(token, 49, Now, TimeSpan.FromSeconds(60)));
    }
    [Fact]
    public void Stale_version_changes_each_mutation()
    {
        var j = New(); var version = j.Version; j.Pause(Now); Assert.NotEqual(version, j.Version);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Invalid_retry_budget_rejected(int max) => Assert.Throws<ArgumentOutOfRangeException>(() => New(max));
    [Theory]
    [InlineData("media.import", "abc", 3)]
    [InlineData("foundation.database-check", "", 3)]
    [InlineData("foundation.database-check", "with space", 3)]
    [InlineData("foundation.database-check", "abc", 6)]
    public void Validation_rejects_unsupported_or_invalid_jobs(string kind, string key, int max) =>
        Assert.False(new CreateJobValidator().Validate(new CreateJob(kind, key, max)).IsValid);
    [Fact]
    public void Unsupported_movie_processing_cannot_be_enqueued() =>
        Assert.Throws<ArgumentException>(() => ProcessingJob.Create("media.import", "abc", 3, Now));
    [Theory]
    [InlineData(JobStatus.Succeeded)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Failed)]
    public void Terminal_jobs_cannot_pause_or_cancel(JobStatus status)
    {
        var j = New(1);
        if (status == JobStatus.Cancelled) j.Cancel(Now);
        else { var token = j.Start("worker", Now, TimeSpan.FromSeconds(60)); if (status == JobStatus.Succeeded) j.Complete(token, Now); else j.Fail(token, "test", Now); }
        Assert.Throws<JobConflictException>(() => j.Pause(Now)); Assert.Throws<JobConflictException>(() => j.Cancel(Now));
    }
}

