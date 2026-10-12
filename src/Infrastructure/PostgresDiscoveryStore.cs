using Microsoft.EntityFrameworkCore;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Infrastructure;

public sealed class PostgresDiscoveryStore(IDbContextFactory<PlatformDbContext> factory, PostgresImportStore imports) : IDiscoveryStore
{
    public async Task ReconcileAsync(FolderScan scan, TimeSpan stability, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"discovery-folder:" + scan.Folder}, 0))", ct);
        var current = await db.Discoveries.FromSqlInterpolated($"SELECT * FROM media.\"MediaDiscoveries\" WHERE \"Folder\" = {scan.Folder} AND \"IsCurrent\" FOR UPDATE").ToListAsync(ct);
        var byPath = current.ToDictionary(x => x.PathKey, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in scan.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (observation.Folder != scan.Folder || !seen.Add(observation.PathKey)) continue;
            if (byPath.TryGetValue(observation.PathKey, out var previous))
            {
                if (previous.LastObservedAt > scan.StartedAt) continue;
                if (previous.Fingerprint == observation.Fingerprint) { previous.Observe(observation, scan.StartedAt, stability); continue; }
                previous.Supersede(scan.StartedAt);
                // Free the filtered unique index before inserting its replacement; the transaction is still atomic.
                await db.SaveChangesAsync(ct);
            }
            db.Discoveries.Add(MediaDiscovery.Create(observation, scan.StartedAt));
        }
        if (scan.Complete)
            foreach (var candidate in current.Where(x => x.IsCurrent && !seen.Contains(x.PathKey))) candidate.Missing(scan.StartedAt);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<MediaDiscovery>> ListAsync(int limit, DiscoveryStatus? status, CancellationToken ct)
    {
        if (status is { } value && !Enum.IsDefined(value)) throw new ImportException("invalid-discovery-status");
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Discoveries.AsNoTracking().AsQueryable();
        if (status is not null) query = query.Where(x => x.Status == status);
        return await query.OrderByDescending(x => x.FirstSeenAt).ThenBy(x => x.Id).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
    }
    public async Task<MediaDiscovery?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Discoveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }
    static Task<MediaDiscovery?> Locked(PlatformDbContext db, Guid id, CancellationToken ct) =>
        db.Discoveries.FromSqlInterpolated($"SELECT * FROM media.\"MediaDiscoveries\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
    public async Task<MediaDiscovery?> DismissAsync(Guid id, Guid version, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var candidate = await Locked(db, id, ct); if (candidate is null) return null;
        try { candidate.Dismiss(version); } catch (InvalidOperationException e) { throw new ImportConflictException(e.Message); }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return candidate;
    }
    public async Task<ImportView?> SubmitAsync(Guid id, SubmitDiscovery request, FileObservation? observed, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var candidate = await Locked(db, id, ct); if (candidate is null) return null;
        if (candidate.Status == DiscoveryStatus.Submitted)
        {
            if (candidate.SubmittedVersion != request.Version) throw new ImportConflictException("Use the version of the original submission for replay.");
        }
        else if (candidate.Status != DiscoveryStatus.Ready || candidate.Version != request.Version || !candidate.IsAvailable || !candidate.IsCurrent ||
            observed is null || !observed.CanRead || observed.SizeBytes <= 0 || observed.Fingerprint != candidate.Fingerprint || observed.Folder != candidate.Folder || observed.PathKey != candidate.PathKey)
            throw new ImportConflictException("Discovery changed or source is unavailable; rescan before submission.");
        var command = new ManualImport("discovery:" + candidate.Id.ToString("N"), candidate.Folder, candidate.RelativePath, request.Source, request.RequestedBy, request.Rights, request.MaxAttempts);
        var result = await imports.CreateInTransactionAsync(db, command, candidate.Fingerprint, ct);
        if (candidate.Status != DiscoveryStatus.Submitted) candidate.Submit(request.Version, result.Import.Id);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result;
    }
}
