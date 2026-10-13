using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.VideoProcessing.Application;

public sealed class FrameException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed record RequestFrames(string IdempotencyKey, string RequestedBy, int SamplesPerScene = 1, int MaxAttempts = 3);
public sealed record FrameSamplingView(FrameSampling Sampling, SceneAnalysis Analysis, JobView Job, bool IsCurrentSelection, IReadOnlyList<FrameSample> Samples);
public sealed record FrameWork(FrameSampling Sampling, SceneAnalysis Analysis, IReadOnlyList<FramePlan> Plans);
public interface IFrameStore
{
    Task<FrameSamplingView?> ReplayAsync(Guid analysisId, RequestFrames request, CancellationToken ct);
    Task<FrameSamplingView> CreateAsync(SceneAnalysisView scene, SelectedVideoSnapshot source, RequestFrames request, CancellationToken ct);
    Task<FrameSamplingView?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<FrameSamplingView>> ListAsync(Guid analysisId, int limit, CancellationToken ct);
    Task<FrameWork> WorkAsync(Guid jobId, CancellationToken ct);
    Task CompleteAsync(JobLease lease, SelectedVideoSnapshot source, IFrameAttempt attempt, FrameResult result, CancellationToken ct);
    Task<int> CleanupAsync(Guid id, CancellationToken ct);
}
public interface IFrameAttempt : IAsyncDisposable
{
    Guid SamplingId { get; }
    Guid Token { get; }
    Task WriteAsync(int ordinal, ReadOnlyMemory<byte> png, CancellationToken ct);
    Task<FrameResult> PrepareAsync(string version, IReadOnlyList<double> sourceSeconds, IReadOnlyList<FramePlan> plans, double offset, CancellationToken ct);
    Task PromoteAsync(FrameResult result, CancellationToken ct);
}
public interface IFrameArtifacts
{
    Task<IFrameAttempt> BeginAsync(Guid id, Guid token, CancellationToken ct);
    Task<Stream> OpenAsync(Guid id, Guid token, SampledFrame frame, CancellationToken ct);
    Task<int> CleanupAsync(Guid id, Guid? referencedToken, CancellationToken ct);
}
public interface IFrameExtractor
{
    Task<FrameResult> ExtractAsync(string sourcePath, SelectedVideoSnapshot source, IReadOnlyList<FramePlan> plans, double offset, IFrameAttempt attempt, Action<int>? progress, CancellationToken ct);
}
public sealed class FrameService(IFrameStore store, ISceneStore scenes, ISelectedVideoAccess media, IFrameArtifacts artifacts)
{
    public async Task<FrameSamplingView?> CreateAsync(Guid analysisId, RequestFrames request, CancellationToken ct)
    {
        if (request is null || request.IdempotencyKey is null || !System.Text.RegularExpressions.Regex.IsMatch(request.IdempotencyKey, "^[A-Za-z0-9._:-]{1,128}$") ||
            string.IsNullOrWhiteSpace(request.RequestedBy) || request.RequestedBy.Length > 256 || request.SamplesPerScene is < 1 or > 3 || request.MaxAttempts is < 1 or > 5) throw new FrameException("invalid-frame-request");
        var replay = await store.ReplayAsync(analysisId, request, ct); if (replay is not null) return replay;
        var scene = await scenes.GetAsync(analysisId, ct); if (scene is null) return null;
        if (scene.Analysis.CompletedAt is null || !scene.IsCurrentSelection || scene.Scenes.Count == 0) throw new ImportConflictException("Completed scenes for the current selection required.");
        var source = await media.GetAsync(scene.Analysis.ImportId, ct) ?? throw new ImportConflictException("Video source unavailable.");
        RequireBinding(scene.Analysis, source);
        try { FrameSampling.Plan(scene.Scenes, request.SamplesPerScene); } catch (ArgumentException) { throw new FrameException("frame-sample-limit-or-invalid-scenes"); }
        return await store.CreateAsync(scene, source, request, ct);
    }
    public static void RequireBinding(SceneAnalysis a, SelectedVideoSnapshot source)
    {
        if (a.ImportId != source.ImportId || a.AssetId != source.AssetId || a.InventoryVersion != source.InventoryVersion || a.SelectionVersion != source.SelectionVersion || a.VideoIndex != source.VideoIndex || a.SourceSha256 != source.Sha256 || a.SourceSizeBytes != source.SizeBytes)
            throw new ImportConflictException("Frame sampling selection changed.");
    }
    public async Task<Stream?> DownloadAsync(Guid id, int ordinal, CancellationToken ct)
    {
        var view = await store.GetAsync(id, ct); if (view is null) return null;
        if (view.Sampling.CompletedAt is null) throw new ImportConflictException("Frame artifacts are not ready.");
        var frame = view.Samples.SingleOrDefault(x => x.Ordinal == ordinal); if (frame is null) return null;
        return await artifacts.OpenAsync(id, view.Sampling.ArtifactToken!.Value, new(frame.Ordinal, frame.SourceSeconds!.Value, frame.NormalizedSeconds!.Value, frame.Sha256!, frame.SizeBytes!.Value), ct);
    }
}
public sealed class FrameExecutor(IFrameStore store, ISelectedVideoAccess media, IFrameArtifacts artifacts, IFrameExtractor extractor)
{
    public async Task ExecuteAsync(JobLease lease, CancellationToken ct, Action<int>? progress = null)
    {
        var work = await store.WorkAsync(lease.JobId, ct); await store.CleanupAsync(work.Sampling.Id, ct);
        try
        {
            var source = await media.GetAsync(work.Analysis.ImportId, ct) ?? throw new FrameException("frame-source-unavailable"); FrameService.RequireBinding(work.Analysis, source);
            progress?.Invoke(5); await using var verified = await media.OpenAsync(source, ct); progress?.Invoke(20);
            await using var attempt = await artifacts.BeginAsync(work.Sampling.Id, lease.Token, ct);
            var result = await extractor.ExtractAsync(verified.LocalPath, source, work.Plans, work.Analysis.SourceOffsetSeconds!.Value, attempt, progress, ct);
            await verified.VerifyUnchangedAsync(ct); progress?.Invoke(90); await store.CompleteAsync(lease, source, attempt, result, ct);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await store.CleanupAsync(work.Sampling.Id, cleanup.Token); } catch (Exception e) when (e is not OutOfMemoryException) { /* Retry/scoped cleanup reconciles leftovers without guessing commit outcome. */ }
        }
    }
}
