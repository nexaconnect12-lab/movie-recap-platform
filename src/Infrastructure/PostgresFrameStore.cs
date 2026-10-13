using Microsoft.EntityFrameworkCore;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class PostgresFrameStore(IDbContextFactory<PlatformDbContext> factory, ISelectedVideoAccess media, IFrameArtifacts artifacts, TimeProvider clock) : IFrameStore
{
    static void Match(FrameSampling s, RequestFrames r) { if (s.RequestedBy != r.RequestedBy || s.MaxAttempts != r.MaxAttempts || s.SamplesPerScene != r.SamplesPerScene) throw new ImportConflictException("Frame request key already used with different parameters."); }
    public async Task<FrameSamplingView?> ReplayAsync(Guid analysisId, RequestFrames request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var s = await db.FrameSamplings.AsNoTracking().SingleOrDefaultAsync(x => x.SceneAnalysisId == analysisId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (s is null) return null; Match(s, request); return await GetAsync(s.Id, ct);
    }
    public async Task<FrameSamplingView> CreateAsync(SceneAnalysisView scene, SelectedVideoSnapshot source, RequestFrames request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = scene.Analysis.Id.ToString("N") + ":" + request.IdempotencyKey; await LockAsync(db, "frame-request:" + key, ct);
        var existing = await db.FrameSamplings.SingleOrDefaultAsync(x => x.SceneAnalysisId == scene.Analysis.Id && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null) { Match(existing, request); await tx.CommitAsync(ct); return (await GetAsync(existing.Id, ct))!; }
        var analysis = await db.SceneAnalyses.AsNoTracking().SingleAsync(x => x.Id == scene.Analysis.Id, ct); if (analysis.CompletedAt is null) throw new ImportConflictException("Completed scenes required.");
        FrameService.RequireBinding(analysis, source); ISelectedVideoTransaction selected = new PostgresSelectedVideoTransaction(db); await selected.RequireAsync(source, ct);
        var scenes = await db.MovieScenes.AsNoTracking().Where(x => x.AnalysisId == analysis.Id).OrderBy(x => x.Ordinal).ToListAsync(ct); var plans = FrameSampling.Plan(scenes, request.SamplesPerScene);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); var job = await jobs.CreateAsync(new("video.frame-sampling", "frame-sampling:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))), request.MaxAttempts), ct);
        var s = FrameSampling.Create(analysis.Id, job.Id, request.IdempotencyKey, request.RequestedBy, request.SamplesPerScene, request.MaxAttempts, clock.GetUtcNow());
        var samples = plans.Select(p => FrameSample.Create(s.Id, p)).ToArray(); db.FrameSamplings.Add(s); db.FrameSamples.AddRange(samples); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(s, analysis, job, true, samples);
    }
    public async Task<FrameSamplingView?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var s = await db.FrameSamplings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (s is null) return null;
        var a = await db.SceneAnalyses.AsNoTracking().SingleAsync(x => x.Id == s.SceneAnalysisId, ct); var samples = await db.FrameSamples.AsNoTracking().Where(x => x.SamplingId == id).OrderBy(x => x.Ordinal).ToListAsync(ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); var job = await jobs.GetAsync(s.JobId, ct) ?? throw new InvalidOperationException("Frame job missing."); await tx.CommitAsync(ct);
        SelectedVideoSnapshot? current = null; try { current = await media.GetAsync(a.ImportId, ct); } catch (ImportConflictException) { }
        var isCurrent = false; if (current is not null) { try { FrameService.RequireBinding(a, current); isCurrent = true; } catch (ImportConflictException) { } }
        return new(s, a, job, isCurrent, samples);
    }
    public async Task<IReadOnlyList<FrameSamplingView>> ListAsync(Guid analysisId, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var ids = await db.FrameSamplings.AsNoTracking().Where(x => x.SceneAnalysisId == analysisId).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(Math.Clamp(limit, 1, 100)).Select(x => x.Id).ToListAsync(ct);
        var views = new List<FrameSamplingView>(); foreach (var id in ids) views.Add((await GetAsync(id, ct))!); return views;
    }
    public async Task<FrameWork> WorkAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var s = await db.FrameSamplings.AsNoTracking().SingleAsync(x => x.JobId == jobId, ct); var a = await db.SceneAnalyses.AsNoTracking().SingleAsync(x => x.Id == s.SceneAnalysisId, ct);
        var samples = await db.FrameSamples.AsNoTracking().Where(x => x.SamplingId == s.Id).OrderBy(x => x.Ordinal).ToListAsync(ct); return new(s, a, samples.Select(x => x.Plan).ToArray());
    }
    public async Task CompleteAsync(JobLease lease, SelectedVideoSnapshot source, IFrameAttempt attempt, FrameResult result, CancellationToken ct)
    {
        if (lease.Kind != "video.frame-sampling" || result.Token != lease.Token || attempt.Token != lease.Token) throw new FrameException("frame-attempt-binding-mismatch");
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(db, "frame-operation:" + attempt.SamplingId.ToString("N"), ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); await jobs.RequireLeaseAsync(lease, ct);
        ISelectedVideoTransaction selected = new PostgresSelectedVideoTransaction(db); await selected.RequireAsync(source, ct);
        var s = await db.FrameSamplings.SingleAsync(x => x.JobId == lease.JobId, ct); if (s.Id != attempt.SamplingId) throw new FrameException("frame-result-binding-mismatch");
        var a = await db.SceneAnalyses.AsNoTracking().SingleAsync(x => x.Id == s.SceneAnalysisId, ct); FrameService.RequireBinding(a, source);
        var samples = await db.FrameSamples.Where(x => x.SamplingId == s.Id).OrderBy(x => x.Ordinal).ToListAsync(ct); var plans = samples.Select(x => x.Plan).ToArray();
        s.Complete(result, plans, a.SourceOffsetSeconds!.Value, clock.GetUtcNow()); for (var i = 0; i < samples.Count; i++) samples[i].Complete(result.Frames[i], a.SourceOffsetSeconds.Value);
        await attempt.PromoteAsync(result, ct); await jobs.CompleteAsync(lease, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<int> CleanupAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(db, "frame-operation:" + id.ToString("N"), ct);
        var s = await db.FrameSamplings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (s is null) return 0;
        var removed = await artifacts.CleanupAsync(id, s.ArtifactToken, ct); await tx.CommitAsync(ct); return removed;
    }
    static Task<int> LockAsync(PlatformDbContext db, string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
}
