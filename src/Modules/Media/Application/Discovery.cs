using MovieRecap.Media.Domain;
namespace MovieRecap.Media.Application;

public sealed record FolderScan(string Folder, DateTimeOffset StartedAt, IReadOnlyList<FileObservation> Files, bool Complete, string? ErrorCode);
public sealed record SubmitDiscovery(Guid Version, string Source, string RequestedBy, MediaRights Rights, int MaxAttempts = 3);
public interface IDiscoveryFiles
{
    FileObservation Observe(string folder, string relativePath);
    Task<FolderScan> ScanAsync(string folder, CancellationToken ct);
}
public interface IDiscoveryStore
{
    Task ReconcileAsync(FolderScan scan, TimeSpan stability, CancellationToken ct);
    Task<IReadOnlyList<MediaDiscovery>> ListAsync(int limit, DiscoveryStatus? status, CancellationToken ct);
    Task<MediaDiscovery?> GetAsync(Guid id, CancellationToken ct);
    Task<MediaDiscovery?> DismissAsync(Guid id, Guid version, CancellationToken ct);
    Task<ImportView?> SubmitAsync(Guid id, SubmitDiscovery request, FileObservation? observed, CancellationToken ct);
}
public sealed class DiscoveryService(IDiscoveryStore store, IDiscoveryFiles files)
{
    public async Task<ImportView?> SubmitAsync(Guid id, SubmitDiscovery request, CancellationToken ct)
    {
        if (request is null || request.Rights is null) throw new ImportException("invalid-import-request");
        try
        {
            request.Rights.Validate();
            _ = MovieImport.Create(Guid.NewGuid(), "discovery", "folder", "file.mp4", request.Source, request.RequestedBy, request.MaxAttempts, DateTimeOffset.UtcNow);
        }
        catch (ArgumentException) { throw new ImportException("invalid-import-metadata"); }
        var candidate = await store.GetAsync(id, ct);
        if (candidate is null) return null;
        // Submitted replay does not require the source to remain on disk.
        FileObservation? observed = null;
        if (candidate.Status != DiscoveryStatus.Submitted)
        {
            if (candidate.Version != request.Version || candidate.Status != DiscoveryStatus.Ready || !candidate.IsCurrent)
                throw new ImportConflictException("Discovery changed or is not ready.");
            try { observed = files.Observe(candidate.Folder, candidate.RelativePath); }
            catch (ImportException) { throw new ImportConflictException("Discovery source is unavailable; rescan before submission."); }
        }
        return await store.SubmitAsync(id, request, observed, ct);
    }
}
