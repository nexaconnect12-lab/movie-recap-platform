using Microsoft.EntityFrameworkCore;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class PostgresSubtitleStore(IDbContextFactory<PlatformDbContext> factory, ISelectedSubtitleAccess media, ISubtitleArtifacts artifacts, TimeProvider clock) : ISubtitleStore
{
    static void Match(SubtitleExtraction a, RequestSubtitle r)
    {
        if (a.SelectionVersion != r.SelectionVersion || a.RequestedBy != r.RequestedBy || a.MaxAttempts != r.MaxAttempts) throw new ImportConflictException("Subtitle request key already used with different parameters.");
    }
    public async Task<SubtitleExtractionView?> ReplayAsync(Guid importId, RequestSubtitle request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var a = await db.SubtitleExtractions.AsNoTracking().SingleOrDefaultAsync(x => x.ImportId == importId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (a is null) return null; Match(a, request); return await GetAsync(a.Id, ct);
    }
    public async Task<SubtitleExtractionView> CreateAsync(SelectedSubtitleSnapshot source, RequestSubtitle request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = source.ImportId.ToString("N") + ":" + request.IdempotencyKey;
        await LockAsync(db, "subtitle-request:" + key, ct);
        var existing = await db.SubtitleExtractions.SingleOrDefaultAsync(x => x.ImportId == source.ImportId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null) { Match(existing, request); await tx.CommitAsync(ct); return (await GetAsync(existing.Id, ct))!; }
        ISelectedSubtitleTransaction selected = new PostgresSelectedSubtitleTransaction(db); await selected.RequireAsync(source, ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock);
        var jobKey = "subtitle-extraction:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var job = await jobs.CreateAsync(new("video.subtitle-extraction", jobKey, request.MaxAttempts), ct);
        var a = SubtitleExtraction.Create(source.ImportId, source.AssetId, source.InventoryVersion, source.SelectionVersion, source.SubtitleIndex, source.Sha256, source.SizeBytes, job.Id, request.IdempotencyKey, request.RequestedBy, request.MaxAttempts, clock.GetUtcNow());
        db.SubtitleExtractions.Add(a); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(a, job, true, []);
    }
    public async Task<SubtitleExtractionView?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var a = await db.SubtitleExtractions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (a is null) return null;
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); var job = await jobs.GetAsync(a.JobId, ct) ?? throw new InvalidOperationException("Subtitle job missing.");
        var cues = await db.SubtitleCues.AsNoTracking().Where(x => x.ExtractionId == id).OrderBy(x => x.Ordinal).ToListAsync(ct);
        await tx.CommitAsync(ct);
        SelectedSubtitleSnapshot? current = null; try { current = await media.GetAsync(a.ImportId, ct); } catch (ImportConflictException) { }
        return new(a, job, current is not null && current.SelectionVersion == a.SelectionVersion && current.AssetId == a.AssetId && current.InventoryVersion == a.InventoryVersion && current.SubtitleIndex == a.SubtitleIndex, cues);
    }
    public async Task<IReadOnlyList<SubtitleExtractionView>> ListAsync(Guid importId, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = await db.SubtitleExtractions.AsNoTracking().Where(x => x.ImportId == importId).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(Math.Clamp(limit, 1, 100)).Select(x => x.Id).ToListAsync(ct);
        var result = new List<SubtitleExtractionView>(); foreach (var id in ids) result.Add((await GetAsync(id, ct))!); return result;
    }
    public async Task<SubtitleExtraction> WorkAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); return await db.SubtitleExtractions.AsNoTracking().SingleAsync(x => x.JobId == jobId, ct);
    }
    public async Task CompleteAsync(JobLease lease, SelectedSubtitleSnapshot source, ISubtitleAttempt attempt, SubtitleArtifact artifact, CancellationToken ct)
    {
        if (source.Codec != "subrip") throw new SubtitleException("subtitle-codec-unsupported");
        if (lease.Kind != "video.subtitle-extraction" || artifact.AttemptToken != lease.Token || attempt.Token != lease.Token) throw new SubtitleException("subtitle-attempt-binding-mismatch"); artifact.Validate();
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, "subtitle-operation:" + attempt.ExtractionId.ToString("N"), ct);
        await PreparationTransactions.LockAsync(db, source.ImportId, ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); await jobs.RequireLeaseAsync(lease, ct);
        ISelectedSubtitleTransaction selected = new PostgresSelectedSubtitleTransaction(db); await selected.RequireAsync(source, ct);
        var a = await db.SubtitleExtractions.SingleAsync(x => x.JobId == lease.JobId, ct);
        if (a.Id != attempt.ExtractionId || a.ImportId != source.ImportId || a.AssetId != source.AssetId || a.InventoryVersion != source.InventoryVersion || a.SelectionVersion != source.SelectionVersion || a.SubtitleIndex != source.SubtitleIndex || a.SourceSha256 != source.Sha256 || a.SourceSizeBytes != source.SizeBytes)
            throw new SubtitleException("subtitle-result-binding-mismatch");
        a.Complete(artifact, clock.GetUtcNow());
        db.SubtitleCues.AddRange(artifact.Subtitle.Cues.Select(x => SubtitleCue.Create(a.Id, x)));
        await attempt.PromoteAsync(artifact, ct);
        // Recheck expiry after filesystem validation/promotion; rollback leaves an unreferenced orphan.
        await jobs.CompleteAsync(lease, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<int> CleanupAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(db, "subtitle-operation:" + id.ToString("N"), ct);
        var a = await db.SubtitleExtractions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (a is null) return 0;
        var count = await artifacts.CleanupAsync(id, a.ArtifactToken, ct); await tx.CommitAsync(ct); return count;
    }
    static Task<int> LockAsync(PlatformDbContext db, string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
}

