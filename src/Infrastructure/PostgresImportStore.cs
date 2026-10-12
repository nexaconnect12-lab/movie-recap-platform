using Microsoft.EntityFrameworkCore;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Infrastructure;

public sealed class ImportRightsRecord
{
    public Guid ImportId { get; set; }
    public MediaRights Details { get; set; } = null!;
}
public sealed class PostgresImportStore(IDbContextFactory<PlatformDbContext> factory, TimeProvider clock) : IImportStore
{
    // Serialize equal keys/hashes before evaluating uniqueness. Hash collisions only serialize unrelated work.
    static Task Lock(PlatformDbContext db, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    public async Task<ImportView> CreateAsync(ManualImport request, CancellationToken ct)
    {
        request.Rights.Validate();
        // PostgreSQL timestamptz stores microseconds. Normalize before both equality checks and writes.
        if (request.Rights.ExpiresAt is { } expiry)
            request = request with { Rights = request.Rights with { ExpiresAt = expiry.AddTicks(-(expiry.Ticks % 10)) } };
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, "import:" + request.IdempotencyKey, ct);
        var existing = await db.Imports.SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null)
        {
            var rights = await db.ImportRights.SingleAsync(x => x.ImportId == existing.Id, ct);
            if (existing.Folder != request.Folder || existing.RelativePath != request.RelativePath || existing.Source != request.Source ||
                existing.RequestedBy != request.RequestedBy || existing.MaxAttempts != request.MaxAttempts || rights.Details != request.Rights)
                throw new ImportConflictException("Import idempotency key already used with different parameters.");
            await tx.CommitAsync(ct);
            return (await GetAsync(existing.Id, ct))!;
        }
        IJobTransaction jobTransaction = new PostgresJobTransaction(db, clock);
        var jobKey = "media-import:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.IdempotencyKey)));
        var job = await jobTransaction.CreateAsync(new("media.manual-import", jobKey, request.MaxAttempts), ct);
        var import = MovieImport.Create(job.Id, request.IdempotencyKey, request.Folder, request.RelativePath, request.Source,
            request.RequestedBy, request.MaxAttempts, clock.GetUtcNow());
        db.Imports.Add(import); db.ImportRights.Add(new() { ImportId = import.Id, Details = request.Rights });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(import, request.Rights, job, null);
    }
    public async Task<ImportView?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var import = await db.Imports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (import is null) return null;
        var rights = await db.ImportRights.AsNoTracking().SingleAsync(x => x.ImportId == id, ct);
        var asset = import.AssetId is { } assetId ? await db.Assets.AsNoTracking().SingleAsync(x => x.Id == assetId, ct) : null;
        IJobTransaction jobTransaction = new PostgresJobTransaction(db, clock);
        var job = await jobTransaction.GetAsync(import.JobId, ct) ?? throw new InvalidOperationException("Import job missing.");
        await tx.CommitAsync(ct);
        return new(import, rights.Details, job, asset);
    }
    public async Task<ImportWork> GetWorkAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return new(await db.Imports.AsNoTracking().SingleAsync(x => x.JobId == jobId, ct));
    }
    public async Task CompleteAsync(JobLease lease, InspectedMedia media, CancellationToken ct)
    {
        if (lease.Kind != "media.manual-import") throw new ImportConflictException("Expected import lease.");
        // Validate before a database write, even if an inspector supplied invalid results.
        var candidate = MediaAsset.Create(media.Sha256, media.SizeBytes, media.Metadata, clock.GetUtcNow());
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, "sha256:" + media.Sha256, ct);
        IJobTransaction jobTransaction = new PostgresJobTransaction(db, clock);
        await jobTransaction.CompleteAsync(lease, ct);
        var import = await db.Imports.SingleAsync(x => x.JobId == lease.JobId, ct);
        var asset = await db.Assets.SingleOrDefaultAsync(x => x.Sha256 == media.Sha256, ct);
        var duplicate = asset is not null;
        if (asset is null) { asset = candidate; db.Assets.Add(asset); }
        else if (asset.SizeBytes != media.SizeBytes || asset.Metadata != media.Metadata) throw new ImportException("duplicate-metadata-conflict");
        import.Complete(asset.Id, duplicate, clock.GetUtcNow());
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
