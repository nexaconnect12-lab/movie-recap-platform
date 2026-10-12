using Microsoft.EntityFrameworkCore;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class PostgresAudioStore(IDbContextFactory<PlatformDbContext> factory, ISelectedAudioAccess media, IAudioArtifacts artifacts, TimeProvider clock) : IAudioStore
{
    static void Match(AudioExtraction a, RequestAudio r)
    {
        if (a.SelectionVersion != r.SelectionVersion || a.RequestedBy != r.RequestedBy || a.MaxAttempts != r.MaxAttempts) throw new ImportConflictException("Audio request key already used with different parameters.");
    }
    public async Task<AudioExtractionView?> ReplayAsync(Guid importId, RequestAudio request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var a = await db.AudioExtractions.AsNoTracking().SingleOrDefaultAsync(x => x.ImportId == importId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (a is null) return null; Match(a, request); return await GetAsync(a.Id, ct);
    }
    public async Task<AudioExtractionView> CreateAsync(SelectedAudioSnapshot source, RequestAudio request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = source.ImportId.ToString("N") + ":" + request.IdempotencyKey;
        await LockAsync(db, "audio-request:" + key, ct);
        var existing = await db.AudioExtractions.SingleOrDefaultAsync(x => x.ImportId == source.ImportId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null) { Match(existing, request); await tx.CommitAsync(ct); return (await GetAsync(existing.Id, ct))!; }
        ISelectedAudioTransaction selected = new PostgresSelectedAudioTransaction(db); await selected.RequireAsync(source, ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock);
        var jobKey = "audio-extraction:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var job = await jobs.CreateAsync(new("video.audio-extraction", jobKey, request.MaxAttempts), ct);
        var a = AudioExtraction.Create(source.ImportId, source.AssetId, source.InventoryVersion, source.SelectionVersion, source.AudioIndex, source.Sha256, source.SizeBytes, job.Id, request.IdempotencyKey, request.RequestedBy, request.MaxAttempts, clock.GetUtcNow());
        db.AudioExtractions.Add(a); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(a, job, true);
    }
    public async Task<AudioExtractionView?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var a = await db.AudioExtractions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (a is null) return null;
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); var job = await jobs.GetAsync(a.JobId, ct) ?? throw new InvalidOperationException("Audio job missing."); await tx.CommitAsync(ct);
        SelectedAudioSnapshot? current = null; try { current = await media.GetAsync(a.ImportId, ct); } catch (ImportConflictException) { }
        return new(a, job, current is not null && current.SelectionVersion == a.SelectionVersion && current.AssetId == a.AssetId && current.InventoryVersion == a.InventoryVersion && current.AudioIndex == a.AudioIndex);
    }
    public async Task<IReadOnlyList<AudioExtractionView>> ListAsync(Guid importId, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = await db.AudioExtractions.AsNoTracking().Where(x => x.ImportId == importId).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(Math.Clamp(limit, 1, 100)).Select(x => x.Id).ToListAsync(ct);
        var result = new List<AudioExtractionView>(); foreach (var id in ids) result.Add((await GetAsync(id, ct))!); return result;
    }
    public async Task<AudioExtraction> WorkAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); return await db.AudioExtractions.AsNoTracking().SingleAsync(x => x.JobId == jobId, ct);
    }
    public async Task CompleteAsync(JobLease lease, SelectedAudioSnapshot source, IAudioAttempt attempt, AudioArtifact artifact, CancellationToken ct)
    {
        if (lease.Kind != "video.audio-extraction" || artifact.AttemptToken != lease.Token || attempt.Token != lease.Token) throw new AudioException("audio-attempt-binding-mismatch"); artifact.Validate();
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, "audio-operation:" + attempt.ExtractionId.ToString("N"), ct);
        IJobTransaction jobs = new PostgresJobTransaction(db, clock); await jobs.RequireLeaseAsync(lease, ct);
        ISelectedAudioTransaction selected = new PostgresSelectedAudioTransaction(db); await selected.RequireAsync(source, ct);
        var a = await db.AudioExtractions.SingleAsync(x => x.JobId == lease.JobId, ct);
        if (a.Id != attempt.ExtractionId || a.ImportId != source.ImportId || a.AssetId != source.AssetId || a.InventoryVersion != source.InventoryVersion || a.SelectionVersion != source.SelectionVersion || a.AudioIndex != source.AudioIndex || a.SourceSha256 != source.Sha256 || a.SourceSizeBytes != source.SizeBytes)
            throw new AudioException("audio-result-binding-mismatch");
        a.Complete(artifact, clock.GetUtcNow());
        await attempt.PromoteAsync(artifact, ct);
        // Recheck expiry after filesystem validation/promotion; rollback leaves an unreferenced orphan.
        await jobs.CompleteAsync(lease, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<int> CleanupAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(db, "audio-operation:" + id.ToString("N"), ct);
        var a = await db.AudioExtractions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (a is null) return 0;
        var count = await artifacts.CleanupAsync(id, a.ArtifactToken, ct); await tx.CommitAsync(ct); return count;
    }
    static Task<int> LockAsync(PlatformDbContext db, string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
}
