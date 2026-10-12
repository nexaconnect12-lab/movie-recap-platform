using Microsoft.EntityFrameworkCore;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Infrastructure;

public sealed class PostgresStreamStore(IDbContextFactory<PlatformDbContext> factory, TimeProvider clock) : IStreamStore
{
    public async Task<StreamView?> GetAsync(Guid importId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var import = await db.Imports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == importId, ct); if (import is null) return null;
        if (import.AssetId is not { } assetId) return new(importId, null, "NotImported", null, null, [], null);
        var inventory = await db.StreamInventories.AsNoTracking().SingleOrDefaultAsync(x => x.AssetId == assetId, ct);
        if (inventory is null) return new(importId, assetId, "Uninspected", null, null, [], null);
        var rows = await db.MediaStreams.AsNoTracking().Where(x => x.AssetId == assetId).OrderBy(x => x.Index).ToListAsync(ct);
        var selection = await db.StreamSelections.AsNoTracking().SingleOrDefaultAsync(x => x.ImportId == importId, ct);
        await tx.CommitAsync(ct); return new(importId, assetId, "Inspected", inventory.Version, inventory.InspectedAt, rows.Select(x => x.Details).ToArray(), selection);
    }
    public async Task<StreamSource?> SourceAsync(Guid importId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var import = await db.Imports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == importId, ct);
        if (import is null) return null;
        if (import.AssetId is not { } assetId) throw new ImportConflictException("Import is not complete.");
        return new(import, await db.Assets.AsNoTracking().SingleAsync(x => x.Id == assetId, ct));
    }
    public async Task<StreamSelection?> SelectAsync(Guid importId, SelectStreams request, IVerifiedMediaSource verified, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var selection = await db.StreamSelections.FromSqlInterpolated($"SELECT * FROM media.\"StreamSelections\" WHERE \"ImportId\" = {importId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (selection is null) throw new ImportConflictException("Inspect this import before selecting streams.");
        var streams = await db.MediaStreams.AsNoTracking().Where(x => x.AssetId == selection.AssetId).OrderBy(x => x.Index).ToListAsync(ct);
        var before = selection.Version;
        try { selection.Select(request.Version, request.VideoIndex, request.AudioIndex, request.SubtitleIndex, request.RequestedBy, clock.GetUtcNow(), streams.Select(x => x.Details).ToArray()); }
        catch (InvalidOperationException e) { throw new ImportConflictException(e.Message); }
        catch (ArgumentException) { throw new ImportException("invalid-stream-selection"); }
        await verified.VerifyUnchangedAsync(ct);
        db.SelectionAudits.Add(new() { ImportId = importId, BeforeVersion = before, AfterVersion = selection.Version, InventoryVersion = selection.InventoryVersion,
            VideoIndex = request.VideoIndex, AudioIndex = request.AudioIndex, SubtitleIndex = request.SubtitleIndex, RequestedBy = request.RequestedBy, At = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return selection;
    }
    public async Task<IReadOnlyList<SelectionAuditView>> AuditAsync(Guid importId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SelectionAudits.AsNoTracking().Where(x => x.ImportId == importId).OrderBy(x => x.Sequence)
            .Select(x => new SelectionAuditView(x.Sequence, x.BeforeVersion, x.AfterVersion, x.InventoryVersion, x.VideoIndex, x.AudioIndex, x.SubtitleIndex, x.RequestedBy, x.At)).ToListAsync(ct);
    }
    public async Task<StreamInspectionView?> QueueInspectionAsync(Guid importId, ReinspectStreams request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = importId.ToString("N") + ":" + request.IdempotencyKey;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"stream-inspection:" + key}, 0))", ct);
        var import = await db.Imports.SingleOrDefaultAsync(x => x.Id == importId, ct); if (import is null) return null;
        if (import.AssetId is null) throw new ImportConflictException("Import is not complete.");
        IJobTransaction jobs = new PostgresJobTransaction(db, clock);
        var existing = await db.StreamInspections.SingleOrDefaultAsync(x => x.ImportId == importId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.MaxAttempts != request.MaxAttempts) throw new ImportConflictException("Inspection key already used with different parameters.");
            var oldJob = await jobs.GetAsync(existing.JobId, ct) ?? throw new InvalidOperationException("Inspection job missing.");
            await tx.CommitAsync(ct); return new(existing.Id, importId, oldJob);
        }
        var jobKey = "stream-inspection:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var job = await jobs.CreateAsync(new("media.stream-inspection", jobKey, request.MaxAttempts), ct);
        var inspection = new StreamInspectionRecord { ImportId = importId, JobId = job.Id, IdempotencyKey = request.IdempotencyKey, MaxAttempts = request.MaxAttempts };
        db.StreamInspections.Add(inspection); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(inspection.Id, importId, job);
    }
    public async Task<StreamSource> InspectionWorkAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var inspection = await db.StreamInspections.AsNoTracking().SingleAsync(x => x.JobId == jobId, ct);
        return await SourceAsync(inspection.ImportId, ct) ?? throw new ImportConflictException("Inspection source no longer exists.");
    }
    public async Task CompleteInspectionAsync(JobLease lease, InspectedMedia media, CancellationToken ct)
    {
        if (lease.Kind != "media.stream-inspection" || media.Streams is null) throw new ImportException("stream-inspection-incomplete");
        MediaStreamInfo.ValidateInventory(media.Streams);
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"sha256:" + media.Sha256}, 0))", ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); await jobs.CompleteAsync(lease, ct);
        var work = await db.StreamInspections.SingleAsync(x => x.JobId == lease.JobId, ct); var import = await db.Imports.SingleAsync(x => x.Id == work.ImportId, ct);
        var asset = await db.Assets.SingleAsync(x => x.Id == import.AssetId, ct);
        if (asset.Sha256 != media.Sha256 || asset.SizeBytes != media.SizeBytes) throw new ImportException("stream-source-content-changed");
        var inventory = await StreamPersistence.InventoryAsync(db, asset.Id, media.Streams, clock.GetUtcNow(), ct);
        await StreamPersistence.InitializeSelectionAsync(db, import, inventory, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
