using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.VideoProcessing.Application;

public sealed class SubtitleException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed record RequestSubtitle(string IdempotencyKey, Guid SelectionVersion, string RequestedBy, int MaxAttempts = 3);
public sealed record SubtitleExtractionView(SubtitleExtraction Extraction, JobView Job, bool IsCurrentSelection, IReadOnlyList<SubtitleCue> Cues);
public interface ISubtitleStore
{
    Task<SubtitleExtractionView?> ReplayAsync(Guid importId, RequestSubtitle request, CancellationToken ct);
    Task<SubtitleExtractionView> CreateAsync(SelectedSubtitleSnapshot source, RequestSubtitle request, CancellationToken ct);
    Task<SubtitleExtractionView?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<SubtitleExtractionView>> ListAsync(Guid importId, int limit, CancellationToken ct);
    Task<SubtitleExtraction> WorkAsync(Guid jobId, CancellationToken ct);
    Task CompleteAsync(JobLease lease, SelectedSubtitleSnapshot source, ISubtitleAttempt attempt, SubtitleArtifact artifact, CancellationToken ct);
    Task<int> CleanupAsync(Guid id, CancellationToken ct);
}
public interface ISubtitleAttempt : IAsyncDisposable
{
    Guid ExtractionId { get; }
    Guid Token { get; }
    Stream Output { get; }
    Task<SubtitleArtifact> PrepareAsync(ExtractedSubtitle subtitle, CancellationToken ct);
    Task PromoteAsync(SubtitleArtifact artifact, CancellationToken ct);
}
public interface ISubtitleArtifacts
{
    Task<ISubtitleAttempt> BeginAsync(Guid extractionId, Guid token, CancellationToken ct);
    Task<Stream> OpenAsync(Guid extractionId, SubtitleArtifact artifact, CancellationToken ct);
    // Caller holds the operation's database advisory lock; active attempt locks are skipped.
    Task<int> CleanupAsync(Guid extractionId, Guid? referencedToken, CancellationToken ct);
}
public interface ISubtitleExtractor
{
    Task<ExtractedSubtitle> ExtractAsync(string sourcePath, SelectedSubtitleSnapshot source, Stream output, Action<int>? progress, CancellationToken ct);
}
public sealed class SubtitleService(ISubtitleStore store, ISelectedSubtitleAccess media, ISubtitleArtifacts artifacts)
{
    public async Task<SubtitleExtractionView?> CreateAsync(Guid importId, RequestSubtitle request, CancellationToken ct)
    {
        if (request is null || request.IdempotencyKey is null || !System.Text.RegularExpressions.Regex.IsMatch(request.IdempotencyKey, "^[A-Za-z0-9._:-]{1,128}$") || request.SelectionVersion == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.RequestedBy) || request.RequestedBy.Length > 256 || request.MaxAttempts is < 1 or > 5) throw new SubtitleException("invalid-subtitle-request");
        var replay = await store.ReplayAsync(importId, request, ct); if (replay is not null) return replay;
        var source = await media.GetAsync(importId, ct); if (source is null) return null;
        if (source.Codec != "subrip") throw new SubtitleException("subtitle-codec-unsupported");
        if (source.SelectionVersion != request.SelectionVersion) throw new ImportConflictException("Selection version changed; reload before extraction.");
        return await store.CreateAsync(source, request, ct);
    }
    public async Task<Stream?> DownloadAsync(Guid id, CancellationToken ct)
    {
        var view = await store.GetAsync(id, ct); if (view is null) return null;
        var a = view.Extraction;
        if (a.CompletedAt is null) throw new ImportConflictException("Subtitle artifact is not ready.");
        return await artifacts.OpenAsync(id, new(a.ArtifactToken!.Value, a.ArtifactSha256!, a.ArtifactSizeBytes!.Value, new(view.Cues.Select(x => x.Data).ToArray(), a.FfmpegVersion!)), ct);
    }
}
public sealed class SubtitleExecutor(ISubtitleStore store, ISelectedSubtitleAccess media, ISubtitleArtifacts artifacts, ISubtitleExtractor extractor)
{
    public async Task ExecuteAsync(JobLease lease, CancellationToken ct, Action<int>? progress = null)
    {
        var work = await store.WorkAsync(lease.JobId, ct); await store.CleanupAsync(work.Id, ct);
        try
        {
            var source = await media.GetAsync(work.ImportId, ct) ?? throw new SubtitleException("subtitle-source-unavailable");
            if (source.SelectionVersion != work.SelectionVersion || source.InventoryVersion != work.InventoryVersion || source.AssetId != work.AssetId || source.SubtitleIndex != work.SubtitleIndex || source.Sha256 != work.SourceSha256 || source.SizeBytes != work.SourceSizeBytes)
                throw new SubtitleException("subtitle-selection-changed");
            progress?.Invoke(5); await using var verified = await media.OpenAsync(source, ct); progress?.Invoke(20);
            await using var attempt = await artifacts.BeginAsync(work.Id, lease.Token, ct);
            var result = await extractor.ExtractAsync(verified.LocalPath, source, attempt.Output, progress, ct);
            var artifact = await attempt.PrepareAsync(result, ct); await verified.VerifyUnchangedAsync(ct); progress?.Invoke(90);
            await store.CompleteAsync(lease, source, attempt, artifact, ct);
        }
        finally
        {
            // Commit outcomes can be uncertain: query the durable reference before deleting anything.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await store.CleanupAsync(work.Id, cleanup.Token); } catch (Exception e) when (e is not OutOfMemoryException) { /* Later retry or explicit cleanup reconciles leftovers. */ }
        }
    }
}
