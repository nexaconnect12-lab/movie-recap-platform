using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.VideoProcessing.Application;

public sealed class SceneException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed record RequestScenes(string IdempotencyKey, Guid SelectionVersion, string RequestedBy, double Threshold = 27, int MinimumSceneFrames = 15, int MaxAttempts = 3);
public sealed record SceneAnalysisView(SceneAnalysis Analysis, JobView Job, bool IsCurrentSelection, IReadOnlyList<MovieScene> Scenes);
public interface ISceneStore
{
    Task<SceneAnalysisView?> ReplayAsync(Guid importId, RequestScenes request, CancellationToken ct);
    Task<SceneAnalysisView> CreateAsync(SelectedVideoSnapshot source, RequestScenes request, CancellationToken ct);
    Task<SceneAnalysisView?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<SceneAnalysisView>> ListAsync(Guid importId, int limit, CancellationToken ct);
    Task<SceneAnalysis> WorkAsync(Guid jobId, CancellationToken ct);
    Task CompleteAsync(JobLease lease, SceneDetection detection, SelectedVideoSnapshot source, CancellationToken ct);
}
public interface ISceneDetector
{
    Task<SceneDetection> DetectAsync(string verifiedPath, SelectedVideoSnapshot source, double threshold, int minimumSceneFrames, Action<int>? progress, CancellationToken ct);
}
public sealed class SceneService(ISceneStore store, ISelectedVideoAccess media)
{
    public async Task<SceneAnalysisView?> CreateAsync(Guid importId, RequestScenes request, CancellationToken ct)
    {
        if (request is null || request.IdempotencyKey is null || !System.Text.RegularExpressions.Regex.IsMatch(request.IdempotencyKey, "^[A-Za-z0-9._:-]{1,128}$") || request.SelectionVersion == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.RequestedBy) || request.RequestedBy.Length > 256 || !double.IsFinite(request.Threshold) || request.Threshold is < 1 or > 255 ||
            request.MinimumSceneFrames is < 1 or > 3000 || request.MaxAttempts is < 1 or > 5) throw new SceneException("invalid-scene-request");
        var replay = await store.ReplayAsync(importId, request, ct); if (replay is not null) return replay;
        var source = await media.GetAsync(importId, ct); if (source is null) return null;
        if (source.SelectionVersion != request.SelectionVersion) throw new ImportConflictException("Selection version changed; reload before analysis.");
        return await store.CreateAsync(source, request, ct);
    }
}
public sealed class SceneExecutor(ISceneStore store, ISelectedVideoAccess media, ISceneDetector detector)
{
    public async Task ExecuteAsync(JobLease lease, CancellationToken ct, Action<int>? progress = null)
    {
        var work = await store.WorkAsync(lease.JobId, ct);
        var source = await media.GetAsync(work.ImportId, ct) ?? throw new SceneException("scene-source-unavailable");
        if (source.SelectionVersion != work.SelectionVersion || source.InventoryVersion != work.InventoryVersion || source.AssetId != work.AssetId || source.VideoIndex != work.VideoIndex || source.Sha256 != work.SourceSha256 || source.SizeBytes != work.SourceSizeBytes)
            throw new SceneException("scene-selection-changed");
        progress?.Invoke(5); await using var verified = await media.OpenAsync(source, ct); progress?.Invoke(20);
        var result = await detector.DetectAsync(verified.LocalPath, source, work.Threshold, work.MinimumSceneFrames, progress, ct);
        await verified.VerifyUnchangedAsync(ct); progress?.Invoke(90);
        await store.CompleteAsync(lease, result, source, ct);
    }
}
