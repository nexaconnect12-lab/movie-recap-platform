using System.Text.RegularExpressions;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Media.Application;

public sealed class ImportException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed class ImportConflictException(string message) : Exception(message);
public sealed record ManualImport(string IdempotencyKey, string Folder, string RelativePath, string Source,
    string RequestedBy, MediaRights Rights, int MaxAttempts = 3);
public sealed record ImportView(MovieImport Import, MediaRights Rights, JobView Job, MediaAsset? Asset)
{
    // Phase 2 records a caller assertion; it does not perform a publication-rights review.
    public string RightsReviewStatus => "Unreviewed";
}
public sealed record ImportWork(MovieImport Import);
public sealed record InspectedMedia(string Sha256, long SizeBytes, MediaMetadata Metadata);
public interface IImportStore
{
    Task<ImportView> CreateAsync(ManualImport request, CancellationToken ct);
    Task<ImportView?> GetAsync(Guid id, CancellationToken ct);
    Task<ImportWork> GetWorkAsync(Guid jobId, CancellationToken ct);
    Task CompleteAsync(JobLease lease, InspectedMedia media, CancellationToken ct);
}
public interface IMediaInspector
{
    void ValidateLocation(string folder, string relativePath);
    Task<InspectedMedia> InspectAsync(string folder, string relativePath, CancellationToken ct);
}
public sealed class ImportService(IImportStore store, IMediaInspector inspector)
{
    public async Task<ImportView> CreateAsync(ManualImport request, CancellationToken ct)
    {
        if (request is null || request.IdempotencyKey is null || !Regex.IsMatch(request.IdempotencyKey, "^[A-Za-z0-9._:-]{1,128}$") || request.Rights is null)
            throw new ImportException("invalid-import-request");
        try
        {
            request.Rights.Validate();
            _ = MovieImport.Create(Guid.NewGuid(), request.IdempotencyKey, request.Folder, request.RelativePath,
                request.Source, request.RequestedBy, request.MaxAttempts, DateTimeOffset.UtcNow);
        }
        catch (ArgumentException) { throw new ImportException("invalid-import-metadata"); }
        inspector.ValidateLocation(request.Folder, request.RelativePath);
        return await store.CreateAsync(request, ct);
    }
}
public sealed class ImportExecutor(IImportStore store, IMediaInspector inspector)
{
    public async Task ExecuteAsync(JobLease lease, CancellationToken ct, Action<int>? progress = null)
    {
        var work = await store.GetWorkAsync(lease.JobId, ct);
        progress?.Invoke(10);
        var media = await inspector.InspectAsync(work.Import.Folder, work.Import.RelativePath, ct);
        ct.ThrowIfCancellationRequested();
        progress?.Invoke(90);
        await store.CompleteAsync(lease, media, ct);
    }
}
