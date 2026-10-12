using Microsoft.EntityFrameworkCore;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class PostgresSceneStore(IDbContextFactory<PlatformDbContext> factory, ISelectedVideoAccess media, TimeProvider clock) : ISceneStore
{
    static void Match(SceneAnalysis a, RequestScenes request)
    {
        if (a.SelectionVersion != request.SelectionVersion || a.RequestedBy != request.RequestedBy || a.Threshold != request.Threshold || a.MinimumSceneFrames != request.MinimumSceneFrames || a.MaxAttempts != request.MaxAttempts)
            throw new ImportConflictException("Scene request key already used with different parameters.");
    }
    public async Task<SceneAnalysisView?> ReplayAsync(Guid importId, RequestScenes request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var a = await db.SceneAnalyses.AsNoTracking().SingleOrDefaultAsync(x => x.ImportId == importId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (a is null) return null; Match(a, request); return await GetAsync(a.Id, ct);
    }
    public async Task<SceneAnalysisView> CreateAsync(SelectedVideoSnapshot source, RequestScenes request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = source.ImportId.ToString("N") + ":" + request.IdempotencyKey;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"scene-analysis:" + key}, 0))", ct);
        var existing = await db.SceneAnalyses.SingleOrDefaultAsync(x => x.ImportId == source.ImportId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null) { Match(existing, request); await tx.CommitAsync(ct); return (await GetAsync(existing.Id, ct))!; }
        ISelectedVideoTransaction selection = new PostgresSelectedVideoTransaction(db); await selection.RequireAsync(source, ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock);
        var jobKey = "scene-analysis:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var job = await jobs.CreateAsync(new("video.scene-analysis", jobKey, request.MaxAttempts), ct);
        var analysis = SceneAnalysis.Create(source.ImportId, source.AssetId, source.InventoryVersion, source.SelectionVersion, source.VideoIndex, source.Sha256, source.SizeBytes,
            job.Id, request.IdempotencyKey, request.RequestedBy, request.Threshold, request.MinimumSceneFrames, request.MaxAttempts, clock.GetUtcNow());
        db.SceneAnalyses.Add(analysis); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(analysis, job, true, []);
    }
    public async Task<SceneAnalysisView?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var a = await db.SceneAnalyses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (a is null) return null;
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); var job = await jobs.GetAsync(a.JobId, ct) ?? throw new InvalidOperationException("Scene job missing.");
        var scenes = await db.MovieScenes.AsNoTracking().Where(x => x.AnalysisId == id).OrderBy(x => x.Ordinal).ToListAsync(ct); await tx.CommitAsync(ct);
        SelectedVideoSnapshot? current = null;
        try { current = await media.GetAsync(a.ImportId, ct); } catch (ImportConflictException) { }
        return new(a, job, current is not null && current.SelectionVersion == a.SelectionVersion && current.InventoryVersion == a.InventoryVersion && current.AssetId == a.AssetId && current.VideoIndex == a.VideoIndex, scenes);
    }
    public async Task<IReadOnlyList<SceneAnalysisView>> ListAsync(Guid importId, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = await db.SceneAnalyses.AsNoTracking().Where(x => x.ImportId == importId).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(Math.Clamp(limit, 1, 100)).Select(x => x.Id).ToListAsync(ct);
        var results = new List<SceneAnalysisView>(); foreach (var id in ids) results.Add((await GetAsync(id, ct))!); return results;
    }
    public async Task<SceneAnalysis> WorkAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); return await db.SceneAnalyses.AsNoTracking().SingleAsync(x => x.JobId == jobId, ct);
    }
    public async Task CompleteAsync(JobLease lease, SceneDetection detection, SelectedVideoSnapshot source, CancellationToken ct)
    {
        if (lease.Kind != "video.scene-analysis") throw new SceneException("invalid-scene-lease");
        detection.Validate();
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); await jobs.CompleteAsync(lease, ct);
        ISelectedVideoTransaction selection = new PostgresSelectedVideoTransaction(db); await selection.RequireAsync(source, ct);
        var a = await db.SceneAnalyses.SingleAsync(x => x.JobId == lease.JobId, ct);
        if (a.ImportId != source.ImportId || a.AssetId != source.AssetId || a.InventoryVersion != source.InventoryVersion || a.SelectionVersion != source.SelectionVersion || a.SourceSha256 != source.Sha256 || a.SourceSizeBytes != source.SizeBytes || a.VideoIndex != source.VideoIndex)
            throw new SceneException("scene-result-binding-mismatch");
        a.Complete(detection, clock.GetUtcNow());
        for (var ordinal = 0; ordinal < detection.Scenes.Count; ordinal++)
        {
            var s = detection.Scenes[ordinal]; db.MovieScenes.Add(new(a.Id, ordinal, s.StartFrame, s.EndFrame, s.StartSeconds, s.EndSeconds));
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
