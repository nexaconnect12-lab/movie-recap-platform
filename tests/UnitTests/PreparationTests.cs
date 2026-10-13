using MovieRecap.Jobs.Application;
using MovieRecap.VideoProcessing.Domain;
using Xunit;
namespace MovieRecap.UnitTests;

public sealed class PreparationTests
{
    static PreparationBinding Binding() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 3, null, new string('a', 64), 100, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    static PreparationValidation Validation(PreparationBinding binding, bool passed = true)
    {
        var v = PreparationValidation.Create(binding.ImportId, Guid.NewGuid(), "validate", "operator", 3, binding, DateTimeOffset.UtcNow); v.Complete(passed ? [] : [new("audio", "audio-artifact-unavailable")], DateTimeOffset.UtcNow); return v;
    }
    [Fact]
    public void Preparation_hash_binds_choices_source_and_completed_artifact_identities()
    {
        var b = Binding(); Assert.True(b.IsReady); Assert.Equal(b.Hash, (b with { }).Hash); Assert.NotEqual(b.Hash, (b with { SelectionVersion = Guid.NewGuid() }).Hash);
        Assert.NotEqual(b.Hash, (b with { FrameId = Guid.NewGuid() }).Hash); Assert.NotEqual(b.Hash, (b with { SourceSha256 = new string('b', 64) }).Hash);
        Assert.False((b with { AudioId = null }).IsReady); Assert.True((b with { AudioIndex = null, AudioId = null }).IsReady);
        Assert.False((b with { SceneId = null }).IsReady); Assert.False((b with { AssetId = Guid.Empty }).IsReady); Assert.False((b with { SourceSha256 = new string('x', 64) }).IsReady);
    }
    [Fact]
    public void Approval_requires_current_successful_validation_and_version()
    {
        var b = Binding(); var review = PreparationReview.Create(b.ImportId, DateTimeOffset.UtcNow); var version = review.Version;
        Assert.Throws<InvalidOperationException>(() => review.Decide(version, b.Hash, PreparationStatus.Approved, "operator", "", DateTimeOffset.UtcNow));
        review.Validated(Validation(b), DateTimeOffset.UtcNow); Assert.NotEqual(version, review.Version);
        Assert.Throws<InvalidOperationException>(() => review.Decide(version, b.Hash, PreparationStatus.Approved, "operator", "", DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => review.Decide(review.Version, (b with { FrameId = Guid.NewGuid() }).Hash, PreparationStatus.Approved, "operator", "", DateTimeOffset.UtcNow));
        review.Decide(review.Version, b.Hash, PreparationStatus.Approved, "operator", "Reviewed", DateTimeOffset.UtcNow); Assert.Equal(PreparationStatus.Approved, review.EffectiveStatus(b.Hash));
    }
    [Fact]
    public void Changed_preparation_and_later_validation_make_prior_decisions_stale()
    {
        var b = Binding(); var review = PreparationReview.Create(b.ImportId, DateTimeOffset.UtcNow); review.Validated(Validation(b), DateTimeOffset.UtcNow);
        review.Decide(review.Version, b.Hash, PreparationStatus.Approved, "operator", "Good", DateTimeOffset.UtcNow);
        Assert.Equal(PreparationStatus.Stale, review.EffectiveStatus((b with { SceneId = Guid.NewGuid() }).Hash));
        review.Validated(Validation(b, false), DateTimeOffset.UtcNow); Assert.Equal(PreparationStatus.Stale, review.Status);
        Assert.Throws<InvalidOperationException>(() => review.Decide(review.Version, b.Hash, PreparationStatus.Approved, "operator", "", DateTimeOffset.UtcNow));
        review.Validated(Validation(b), DateTimeOffset.UtcNow); Assert.Equal(PreparationStatus.Stale, review.Status);
        review.Decide(review.Version, b.Hash, PreparationStatus.Approved, "operator", "Rechecked", DateTimeOffset.UtcNow); Assert.Equal(PreparationStatus.Approved, review.Status);
    }
    [Fact]
    public void Rejection_is_available_without_artifacts_but_requires_notes_and_does_not_grant_approval()
    {
        var b = Binding() with { FrameId = null }; var review = PreparationReview.Create(b.ImportId, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => review.Decide(review.Version, b.Hash, PreparationStatus.Rejected, "operator", "", DateTimeOffset.UtcNow));
        review.Decide(review.Version, b.Hash, PreparationStatus.Rejected, "operator", "Frames missing", DateTimeOffset.UtcNow); Assert.Equal(PreparationStatus.Rejected, review.Status); Assert.Null(review.DecisionValidationId);
        Assert.Equal(PreparationStatus.Stale, review.EffectiveStatus((b with { FrameId = Guid.NewGuid() }).Hash));
    }
    [Theory]
    [InlineData("bad code")]
    [InlineData("C:\\secret\\file")]
    public void Validation_issue_codes_cannot_persist_raw_provider_messages(string code)
    {
        var binding = Binding(); var v = PreparationValidation.Create(binding.ImportId, Guid.NewGuid(), "key", "actor", 1, binding, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => v.Complete([new("source", code)], DateTimeOffset.UtcNow)); Assert.Null(v.CompletedAt);
    }
    [Fact]
    public void Validation_reports_are_bounded_and_complete_once()
    {
        var v = Validation(Binding(), false); Assert.False(v.Passed); Assert.Contains("audio-artifact-unavailable", v.IssuesJson!);
        Assert.Throws<InvalidOperationException>(() => v.Complete([], DateTimeOffset.UtcNow));
        var binding = Binding(); var other = PreparationValidation.Create(binding.ImportId, Guid.NewGuid(), "key", "actor", 1, binding, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => other.Complete(Enumerable.Range(0, 303).Select(i => new PreparationIssue("frame-" + i, "frame-artifact-unavailable")).ToArray(), DateTimeOffset.UtcNow));
    }
    [Fact]
    public void Generic_job_creation_cannot_impersonate_preparation_validation()
    {
        Assert.False(new CreateJobValidator().Validate(new CreateJob("video.preparation-validation", "orphan")).IsValid);
        Assert.False(new CreateJobValidator().Validate(new CreateJob("foundation.database-check", "preparation-validation:reserved")).IsValid);
    }
}

