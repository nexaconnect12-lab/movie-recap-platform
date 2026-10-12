using MovieRecap.Jobs.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Media.Application;

public sealed record MediaProbe(MediaMetadata Metadata, IReadOnlyList<MediaStreamInfo> Streams);
public sealed record StreamSource(MovieImport Import, MediaAsset Asset);
public sealed record StreamView(Guid ImportId, Guid? AssetId, string Status, Guid? InventoryVersion,
    DateTimeOffset? InspectedAt, IReadOnlyList<MediaStreamInfo> Streams, StreamSelection? Selection);
public sealed record SelectStreams(Guid Version, [property: System.Text.Json.Serialization.JsonRequired] int VideoIndex, int? AudioIndex, int? SubtitleIndex, string RequestedBy);
public sealed record ReinspectStreams(string IdempotencyKey, int MaxAttempts = 3);
public sealed record StreamInspectionView(Guid Id, Guid ImportId, JobView Job);
public sealed record SelectionAuditView(long Sequence, Guid BeforeVersion, Guid AfterVersion, Guid InventoryVersion,
    int VideoIndex, int? AudioIndex, int? SubtitleIndex, string RequestedBy, DateTimeOffset At);
public interface IVerifiedMediaSource : IAsyncDisposable
{
    Task VerifyUnchangedAsync(CancellationToken ct);
}
public interface IMediaSourceVerifier
{
    Task<IVerifiedMediaSource> OpenAsync(StreamSource source, CancellationToken ct);
}
public interface IStreamStore
{
    Task<StreamView?> GetAsync(Guid importId, CancellationToken ct);
    Task<StreamSource?> SourceAsync(Guid importId, CancellationToken ct);
    Task<StreamSelection?> SelectAsync(Guid importId, SelectStreams request, IVerifiedMediaSource verified, CancellationToken ct);
    Task<IReadOnlyList<SelectionAuditView>> AuditAsync(Guid importId, CancellationToken ct);
    Task<StreamInspectionView?> QueueInspectionAsync(Guid importId, ReinspectStreams request, CancellationToken ct);
    Task<StreamSource> InspectionWorkAsync(Guid jobId, CancellationToken ct);
    Task CompleteInspectionAsync(JobLease lease, InspectedMedia media, CancellationToken ct);
}
public sealed class StreamService(IStreamStore store, IMediaSourceVerifier verifier)
{
    public async Task<StreamSelection?> SelectAsync(Guid id, SelectStreams request, CancellationToken ct)
    {
        if (request is null || request.Version == Guid.Empty || string.IsNullOrWhiteSpace(request.RequestedBy) || request.RequestedBy.Length > 256)
            throw new ImportException("invalid-stream-selection");
        var view = await store.GetAsync(id, ct); if (view is null) return null;
        if (view.Selection is null || view.InventoryVersion is null) throw new ImportConflictException("Inspect this import before selecting streams.");
        try
        {
            // Validate on a detached domain instance before expensive source hashing; the store revalidates under lock.
            view.Selection.Select(request.Version, request.VideoIndex, request.AudioIndex, request.SubtitleIndex, request.RequestedBy, DateTimeOffset.UtcNow, view.Streams);
        }
        catch (InvalidOperationException e) { throw new ImportConflictException(e.Message); }
        catch (ArgumentException) { throw new ImportException("invalid-stream-selection"); }
        var source = await store.SourceAsync(id, ct) ?? throw new ImportConflictException("Import is not complete.");
        await using var verified = await verifier.OpenAsync(source, ct);
        return await store.SelectAsync(id, request, verified, ct);
    }
    public Task<StreamInspectionView?> InspectAsync(Guid id, ReinspectStreams request, CancellationToken ct)
    {
        if (request is null || request.IdempotencyKey is null || !System.Text.RegularExpressions.Regex.IsMatch(request.IdempotencyKey, "^[A-Za-z0-9._:-]{1,128}$") || request.MaxAttempts is < 1 or > 5)
            throw new ImportException("invalid-stream-inspection-request");
        return store.QueueInspectionAsync(id, request, ct);
    }
}
public sealed class StreamInspectionExecutor(IStreamStore store, IMediaInspector inspector)
{
    public async Task ExecuteAsync(JobLease lease, CancellationToken ct, Action<int>? progress = null)
    {
        var source = await store.InspectionWorkAsync(lease.JobId, ct); progress?.Invoke(10);
        var media = await inspector.InspectAsync(source.Import.Folder, source.Import.RelativePath, ct, source.Import.ExpectedFingerprint);
        if (media.Sha256 != source.Asset.Sha256 || media.SizeBytes != source.Asset.SizeBytes) throw new ImportException("stream-source-content-changed");
        progress?.Invoke(90); await store.CompleteInspectionAsync(lease, media, ct);
    }
}
