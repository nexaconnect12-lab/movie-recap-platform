using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.VideoProcessing.Application;

public sealed class PreparationException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed record RequestPreparationValidation(string IdempotencyKey, string SnapshotHash, string RequestedBy, int MaxAttempts = 3);
public sealed record DecidePreparation(Guid Version, string SnapshotHash, PreparationStatus Decision, string RequestedBy, string Notes);
public sealed record PreparationValidationView(PreparationValidation Validation, JobView Job, IReadOnlyList<PreparationIssue> Issues);
public sealed record PreparationOperation(Guid Id, string Kind, JobView Job);
public sealed record PreparationSnapshot(PreparationMedia Media, SceneAnalysis? Scene, IReadOnlyList<MovieScene> Scenes, AudioExtraction? Audio, FrameSampling? Frames, IReadOnlyList<FrameSample> Samples)
{
    public PreparationBinding Binding => new(Media.ImportId, Media.AssetId, Media.InventoryVersion, Media.SelectionVersion, Media.VideoIndex, Media.AudioIndex, Media.SubtitleIndex, Media.Sha256, Media.SizeBytes, Scene?.Id, Audio?.Id, Frames?.Id);
    public string SnapshotHash => Binding.Hash;
    public IReadOnlyList<string> ReadinessIssues
    {
        get
        {
            var issues = new List<string>(); if (!Media.ImportCompleted) issues.Add("import-incomplete"); if (Media.InventoryVersion is null) issues.Add("streams-uninspected"); if (Media.VideoIndex is null) issues.Add("video-not-selected");
            if (Scene is null || Scenes.Count == 0) issues.Add("scenes-required"); if (Frames is null || Samples.Count is < 1 or > 300 || Samples.Any(x => x.Sha256 is null)) issues.Add("scene-frames-required"); if (Media.AudioIndex is not null && Audio is null) issues.Add("selected-audio-required"); return issues;
        }
    }
    public bool IsReady => Binding.IsReady && ReadinessIssues.Count == 0;
}
public sealed record PreparationOverview(PreparationSnapshot Snapshot, PreparationReview? Review, PreparationStatus EffectiveStatus, bool NeedsValidation,
    PreparationValidationView? LatestValidation, IReadOnlyList<PreparationOperation> RecentOperations);
public interface IPreparationStore
{
    Task<PreparationOverview> GetAsync(PreparationMedia media, int limit, CancellationToken ct);
    Task<PreparationValidationView?> ReplayAsync(Guid importId, RequestPreparationValidation request, CancellationToken ct);
    Task<PreparationValidationView> QueueAsync(PreparationMedia media, RequestPreparationValidation request, CancellationToken ct);
    Task<PreparationValidationView?> ValidationAsync(Guid id, CancellationToken ct);
    Task<PreparationValidation> WorkAsync(Guid jobId, CancellationToken ct);
    Task CompleteAsync(JobLease lease, PreparationSnapshot snapshot, IReadOnlyList<PreparationIssue> issues, CancellationToken ct);
    Task<PreparationOverview> DecideAsync(PreparationMedia media, DecidePreparation request, CancellationToken ct);
    Task<IReadOnlyList<PreparationAudit>> AuditAsync(Guid importId, int limit, CancellationToken ct);
}
public interface IPreparationCheck : IAsyncDisposable { IReadOnlyList<PreparationIssue> Issues { get; } }
public interface IPreparationChecker { Task<IPreparationCheck> CheckAsync(PreparationSnapshot snapshot, CancellationToken ct); }
public sealed class PreparationService(IPreparationStore store, IPreparationMediaAccess media, IPreparationChecker checker)
{
    public async Task<PreparationOverview?> GetAsync(Guid importId, int limit, CancellationToken ct)
    {
        var source = await media.GetAsync(importId, ct); if (source is null) return null;
        var view = await store.GetAsync(source, Math.Clamp(limit, 1, 20), ct); var after = await media.GetAsync(importId, ct);
        if (after is null || source.AssetId != after.AssetId || source.InventoryVersion != after.InventoryVersion || source.SelectionVersion != after.SelectionVersion || source.Sha256 != after.Sha256 || source.SizeBytes != after.SizeBytes || source.ImportCompleted != after.ImportCompleted)
            throw new ImportConflictException("Preparation changed while reading; reload.");
        return view;
    }
    public async Task<PreparationValidationView?> ValidateAsync(Guid importId, RequestPreparationValidation request, CancellationToken ct)
    {
        if (request is null || request.IdempotencyKey is null || !System.Text.RegularExpressions.Regex.IsMatch(request.IdempotencyKey, "^[A-Za-z0-9._:-]{1,128}$") || !PreparationValidation.ValidHash(request.SnapshotHash) ||
            string.IsNullOrWhiteSpace(request.RequestedBy) || request.RequestedBy.Length > 256 || request.MaxAttempts is < 1 or > 5) throw new PreparationException("invalid-preparation-validation");
        var replay = await store.ReplayAsync(importId, request, ct); if (replay is not null) return replay;
        var source = await media.GetAsync(importId, ct); return source is null ? null : await store.QueueAsync(source, request, ct);
    }
    public async Task<PreparationOverview?> DecideAsync(Guid importId, DecidePreparation request, CancellationToken ct)
    {
        if (request is null || !PreparationValidation.ValidHash(request.SnapshotHash) || request.Decision is not (PreparationStatus.Approved or PreparationStatus.Rejected) || string.IsNullOrWhiteSpace(request.RequestedBy) || request.RequestedBy.Length > 256 ||
            request.Notes is null || request.Notes.Length > 2048 || request.Decision == PreparationStatus.Rejected && string.IsNullOrWhiteSpace(request.Notes)) throw new PreparationException("invalid-preparation-decision");
        var source = await media.GetAsync(importId, ct); if (source is null) return null;
        if (request.Decision == PreparationStatus.Rejected) return await store.DecideAsync(source, request, ct);
        var overview = await store.GetAsync(source, 1, ct);
        if (overview.Snapshot.SnapshotHash != request.SnapshotHash || !overview.Snapshot.IsReady || overview.NeedsValidation || overview.Review?.Version != request.Version) throw new ImportConflictException("Reload and validate the current preparation before approving.");
        await using var check = await checker.CheckAsync(overview.Snapshot, ct);
        if (check.Issues.Count != 0) throw new ImportConflictException("Preparation files failed fresh checks; run validation to record the issues.");
        return await store.DecideAsync(source, request, ct);
    }
}
public sealed class PreparationExecutor(IPreparationStore store, IPreparationMediaAccess media, IPreparationChecker checker)
{
    public async Task ExecuteAsync(JobLease lease, CancellationToken ct, Action<int>? progress = null)
    {
        var work = await store.WorkAsync(lease.JobId, ct); var source = await media.GetAsync(work.ImportId, ct) ?? throw new PreparationException("preparation-import-unavailable");
        var view = await store.GetAsync(source, 1, ct); if (!view.Snapshot.IsReady || view.Snapshot.SnapshotHash != work.SnapshotHash) throw new ImportConflictException("Preparation changed before validation.");
        progress?.Invoke(10); await using var check = await checker.CheckAsync(view.Snapshot, ct); progress?.Invoke(90); await store.CompleteAsync(lease, view.Snapshot, check.Issues, ct);
    }
}
