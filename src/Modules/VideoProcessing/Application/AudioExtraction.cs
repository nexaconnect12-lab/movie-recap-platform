using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.VideoProcessing.Application;

public sealed class AudioException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed record RequestAudio(string IdempotencyKey, Guid SelectionVersion, string RequestedBy, int MaxAttempts = 3);
public sealed record AudioExtractionView(AudioExtraction Extraction, JobView Job, bool IsCurrentSelection);
public interface IAudioStore
{
    Task<AudioExtractionView?> ReplayAsync(Guid importId, RequestAudio request, CancellationToken ct);
    Task<AudioExtractionView> CreateAsync(SelectedAudioSnapshot source, RequestAudio request, CancellationToken ct);
    Task<AudioExtractionView?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AudioExtractionView>> ListAsync(Guid importId, int limit, CancellationToken ct);
    Task<AudioExtraction> WorkAsync(Guid jobId, CancellationToken ct);
    Task CompleteAsync(JobLease lease, SelectedAudioSnapshot source, IAudioAttempt attempt, AudioArtifact artifact, CancellationToken ct);
    Task<int> CleanupAsync(Guid id, CancellationToken ct);
}
public interface IAudioAttempt : IAsyncDisposable
{
    Guid ExtractionId { get; }
    Guid Token { get; }
    Stream Output { get; }
    Task<AudioArtifact> PrepareAsync(ExtractedAudio audio, CancellationToken ct);
    Task PromoteAsync(AudioArtifact artifact, CancellationToken ct);
}
public interface IAudioArtifacts
{
    Task<IAudioAttempt> BeginAsync(Guid extractionId, Guid token, CancellationToken ct);
    Task<Stream> OpenAsync(Guid extractionId, AudioArtifact artifact, CancellationToken ct);
    // Caller holds the operation's database advisory lock; active attempt locks are skipped.
    Task<int> CleanupAsync(Guid extractionId, Guid? referencedToken, CancellationToken ct);
}
public interface IAudioExtractor
{
    Task<ExtractedAudio> ExtractAsync(string sourcePath, SelectedAudioSnapshot source, Stream output, Action<int>? progress, CancellationToken ct);
}
public sealed class AudioService(IAudioStore store, ISelectedAudioAccess media, IAudioArtifacts artifacts)
{
    public async Task<AudioExtractionView?> CreateAsync(Guid importId, RequestAudio request, CancellationToken ct)
    {
        if (request is null || request.IdempotencyKey is null || !System.Text.RegularExpressions.Regex.IsMatch(request.IdempotencyKey, "^[A-Za-z0-9._:-]{1,128}$") || request.SelectionVersion == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.RequestedBy) || request.RequestedBy.Length > 256 || request.MaxAttempts is < 1 or > 5) throw new AudioException("invalid-audio-request");
        var replay = await store.ReplayAsync(importId, request, ct); if (replay is not null) return replay;
        var source = await media.GetAsync(importId, ct); if (source is null) return null;
        if (source.SelectionVersion != request.SelectionVersion) throw new ImportConflictException("Selection version changed; reload before extraction.");
        return await store.CreateAsync(source, request, ct);
    }
    public async Task<Stream?> DownloadAsync(Guid id, CancellationToken ct)
    {
        var view = await store.GetAsync(id, ct); if (view is null) return null;
        var a = view.Extraction;
        if (a.CompletedAt is null) throw new ImportConflictException("Audio artifact is not ready.");
        return await artifacts.OpenAsync(id, new(a.ArtifactToken!.Value, a.ArtifactSha256!, a.ArtifactSizeBytes!.Value, new(a.Samples!.Value, a.FfmpegVersion!)), ct);
    }
}
public sealed class AudioExecutor(IAudioStore store, ISelectedAudioAccess media, IAudioArtifacts artifacts, IAudioExtractor extractor)
{
    public async Task ExecuteAsync(JobLease lease, CancellationToken ct, Action<int>? progress = null)
    {
        var work = await store.WorkAsync(lease.JobId, ct); await store.CleanupAsync(work.Id, ct);
        try
        {
            var source = await media.GetAsync(work.ImportId, ct) ?? throw new AudioException("audio-source-unavailable");
            if (source.SelectionVersion != work.SelectionVersion || source.InventoryVersion != work.InventoryVersion || source.AssetId != work.AssetId || source.AudioIndex != work.AudioIndex || source.Sha256 != work.SourceSha256 || source.SizeBytes != work.SourceSizeBytes)
                throw new AudioException("audio-selection-changed");
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
