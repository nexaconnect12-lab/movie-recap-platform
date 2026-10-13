using Microsoft.EntityFrameworkCore;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

internal static class PreparationTransactions
{
    public static Task<int> LockAsync(PlatformDbContext db, Guid importId, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"preparation-import:" + importId.ToString("N")}, 0))", ct);
}
public sealed class PostgresPreparationStore(IDbContextFactory<PlatformDbContext> factory, TimeProvider clock) : IPreparationStore
{
    static void Match(PreparationValidation v, RequestPreparationValidation r)
    {
        if (v.SnapshotHash != r.SnapshotHash || v.RequestedBy != r.RequestedBy || v.MaxAttempts != r.MaxAttempts) throw new ImportConflictException("Validation request key already used with different parameters.");
    }
    static IReadOnlyList<PreparationIssue> Issues(PreparationValidation v) => v.IssuesJson is null ? [] : System.Text.Json.JsonSerializer.Deserialize<PreparationIssue[]>(v.IssuesJson)!;
    static async Task<PreparationSnapshot> SnapshotAsync(PlatformDbContext db, PreparationMedia media, CancellationToken ct)
    {
        var scene = await db.SceneAnalyses.AsNoTracking().Where(x => x.ImportId == media.ImportId && x.AssetId == media.AssetId && x.InventoryVersion == media.InventoryVersion && x.SelectionVersion == media.SelectionVersion && x.VideoIndex == media.VideoIndex && x.SourceSha256 == media.Sha256 && x.SourceSizeBytes == media.SizeBytes && x.CompletedAt != null)
            .OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        var scenes = scene is null ? [] : await db.MovieScenes.AsNoTracking().Where(x => x.AnalysisId == scene.Id).OrderBy(x => x.Ordinal).ToListAsync(ct);
        var frame = scene is null ? null : await db.FrameSamplings.AsNoTracking().Where(x => x.SceneAnalysisId == scene.Id && x.CompletedAt != null).OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        var samples = frame is null ? [] : await db.FrameSamples.AsNoTracking().Where(x => x.SamplingId == frame.Id).OrderBy(x => x.Ordinal).ToListAsync(ct);
        var audio = media.AudioIndex is null ? null : await db.AudioExtractions.AsNoTracking().Where(x => x.ImportId == media.ImportId && x.AssetId == media.AssetId && x.InventoryVersion == media.InventoryVersion && x.SelectionVersion == media.SelectionVersion && x.AudioIndex == media.AudioIndex && x.SourceSha256 == media.Sha256 && x.SourceSizeBytes == media.SizeBytes && x.CompletedAt != null)
            .OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return new(media, scene, scenes, audio, frame, samples);
    }
    public async Task<PreparationOverview> GetAsync(PreparationMedia media, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var result = await OverviewAsync(db, media, Math.Clamp(limit, 1, 20), ct); await tx.CommitAsync(ct); return result;
    }
    async Task<PreparationOverview> OverviewAsync(PlatformDbContext db, PreparationMedia media, int limit, CancellationToken ct)
    {
        var snapshot = await SnapshotAsync(db, media, ct); var review = await db.PreparationReviews.AsNoTracking().SingleOrDefaultAsync(x => x.ImportId == media.ImportId, ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); PreparationValidationView? validation = null;
        if (review?.LastValidationId is { } validationId) { var v = await db.PreparationValidations.AsNoTracking().SingleAsync(x => x.Id == validationId, ct); validation = new(v, (await jobs.GetAsync(v.JobId, ct))!, Issues(v)); }
        var scenes = await db.SceneAnalyses.AsNoTracking().Where(x => x.ImportId == media.ImportId).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit).Select(x => new { x.Id, x.JobId }).ToListAsync(ct);
        var audios = await db.AudioExtractions.AsNoTracking().Where(x => x.ImportId == media.ImportId).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit).Select(x => new { x.Id, x.JobId }).ToListAsync(ct);
        var frames = await db.FrameSamplings.AsNoTracking().Where(x => db.SceneAnalyses.Any(a => a.Id == x.SceneAnalysisId && a.ImportId == media.ImportId)).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit).Select(x => new { x.Id, x.JobId }).ToListAsync(ct);
        var validations = await db.PreparationValidations.AsNoTracking().Where(x => x.ImportId == media.ImportId).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit).Select(x => new { x.Id, x.JobId }).ToListAsync(ct);
        var history = new List<PreparationOperation>(); foreach (var row in scenes.Concat(audios).Concat(frames).Concat(validations)) { var job = (await jobs.GetAsync(row.JobId, ct))!; history.Add(new(row.Id, job.Kind, job)); }
        return new(snapshot, review, review?.EffectiveStatus(snapshot.SnapshotHash) ?? PreparationStatus.Pending, !snapshot.IsReady || review?.ValidationPassed != true || review.ValidationSnapshotHash != snapshot.SnapshotHash, validation, history);
    }
    public async Task<PreparationValidationView?> ReplayAsync(Guid importId, RequestPreparationValidation request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var v = await db.PreparationValidations.AsNoTracking().SingleOrDefaultAsync(x => x.ImportId == importId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (v is null) return null; Match(v, request); return await ValidationAsync(v.Id, ct);
    }
    public async Task<PreparationValidationView> QueueAsync(PreparationMedia media, RequestPreparationValidation request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); await PreparationTransactions.LockAsync(db, media.ImportId, ct);
        var existing = await db.PreparationValidations.AsNoTracking().SingleOrDefaultAsync(x => x.ImportId == media.ImportId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null) { Match(existing, request); await tx.CommitAsync(ct); return (await ValidationAsync(existing.Id, ct))!; }
        IPreparationMediaTransaction selected = new PostgresPreparationMediaTransaction(db); await selected.RequireAsync(media, ct);
        var snapshot = await SnapshotAsync(db, media, ct); if (!snapshot.IsReady || snapshot.SnapshotHash != request.SnapshotHash) throw new ImportConflictException("Reload the ready current preparation before validation.");
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); var key = media.ImportId.ToString("N") + ":" + request.IdempotencyKey;
        var job = await jobs.CreateAsync(new("video.preparation-validation", "preparation-validation:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))), request.MaxAttempts), ct);
        var v = PreparationValidation.Create(media.ImportId, job.Id, request.IdempotencyKey, request.RequestedBy, request.MaxAttempts, snapshot.Binding, clock.GetUtcNow()); db.PreparationValidations.Add(v);
        await EnsureReviewAsync(db, media.ImportId, request.RequestedBy, snapshot.Binding, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(v, job, []);
    }
    async Task<PreparationReview> EnsureReviewAsync(PlatformDbContext db, Guid importId, string actor, PreparationBinding binding, CancellationToken ct)
    {
        var review = await db.PreparationReviews.SingleOrDefaultAsync(x => x.ImportId == importId, ct); if (review is not null) return review;
        review = PreparationReview.Create(importId, clock.GetUtcNow()); db.PreparationReviews.Add(review); Audit(db, review, "created", Guid.Empty, binding, actor, "", null); return review;
    }
    public async Task<PreparationValidationView?> ValidationAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var v = await db.PreparationValidations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (v is null) return null; IJobTransaction jobs = new PostgresJobTransaction(db, clock);
        var result = new PreparationValidationView(v, (await jobs.GetAsync(v.JobId, ct))!, Issues(v)); await tx.CommitAsync(ct); return result;
    }
    public async Task<PreparationValidation> WorkAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); return await db.PreparationValidations.AsNoTracking().SingleAsync(x => x.JobId == jobId, ct);
    }
    public async Task CompleteAsync(JobLease lease, PreparationSnapshot snapshot, IReadOnlyList<PreparationIssue> issues, CancellationToken ct)
    {
        if (lease.Kind != "video.preparation-validation") throw new PreparationException("invalid-preparation-lease");
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); await PreparationTransactions.LockAsync(db, snapshot.Media.ImportId, ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); await jobs.RequireLeaseAsync(lease, ct);
        IPreparationMediaTransaction selected = new PostgresPreparationMediaTransaction(db); await selected.RequireAsync(snapshot.Media, ct);
        var current = await SnapshotAsync(db, snapshot.Media, ct); var v = await db.PreparationValidations.SingleAsync(x => x.JobId == lease.JobId, ct);
        if (!current.IsReady || current.SnapshotHash != snapshot.SnapshotHash || v.ImportId != snapshot.Media.ImportId || v.SnapshotHash != current.SnapshotHash) throw new ImportConflictException("Preparation changed during validation.");
        v.Complete(issues, clock.GetUtcNow()); var review = await db.PreparationReviews.SingleAsync(x => x.ImportId == v.ImportId, ct); var before = review.Version; review.Validated(v, clock.GetUtcNow());
        Audit(db, review, v.Passed == true ? "validation-passed" : "validation-failed", before, v.Binding, v.RequestedBy, "", v.Id);
        await jobs.CompleteAsync(lease, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<PreparationOverview> DecideAsync(PreparationMedia media, DecidePreparation request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); await PreparationTransactions.LockAsync(db, media.ImportId, ct);
        IPreparationMediaTransaction selected = new PostgresPreparationMediaTransaction(db); await selected.RequireAsync(media, ct);
        var snapshot = await SnapshotAsync(db, media, ct); if (snapshot.SnapshotHash != request.SnapshotHash) throw new ImportConflictException("Preparation snapshot changed; reload.");
        var existing = await db.PreparationReviews.SingleOrDefaultAsync(x => x.ImportId == media.ImportId, ct);
        if ((existing is null && request.Version != Guid.Empty) || (existing is not null && existing.Version != request.Version)) throw new ImportConflictException("Preparation review version changed; reload.");
        if (request.Decision == PreparationStatus.Approved && !snapshot.IsReady) throw new ImportConflictException("Ready preparation required.");
        var review = existing ?? await EnsureReviewAsync(db, media.ImportId, request.RequestedBy, snapshot.Binding, ct); var before = review.Version;
        try { review.Decide(review.Version, request.SnapshotHash, request.Decision, request.RequestedBy, request.Notes, clock.GetUtcNow()); } catch (InvalidOperationException e) { throw new ImportConflictException(e.Message); }
        Audit(db, review, request.Decision == PreparationStatus.Approved ? "approved" : "rejected", before, snapshot.Binding, request.RequestedBy, request.Notes, review.LastValidationId);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await GetAsync(media, 5, ct);
    }
    public async Task<IReadOnlyList<PreparationAudit>> AuditAsync(Guid importId, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); return await db.PreparationAudits.AsNoTracking().Where(x => x.ImportId == importId).OrderByDescending(x => x.Sequence).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
    }
    void Audit(PlatformDbContext db, PreparationReview review, string action, Guid before, PreparationBinding binding, string actor, string notes, Guid? validationId) => db.PreparationAudits.Add(new()
        { ImportId = review.ImportId, Action = action, BeforeVersion = before, AfterVersion = review.Version, SnapshotHash = binding.Hash, Binding = binding, RequestedBy = actor, Notes = notes, ValidationId = validationId, At = clock.GetUtcNow() });
}
