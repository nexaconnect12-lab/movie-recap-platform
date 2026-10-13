using Microsoft.EntityFrameworkCore;
using MovieRecap.Media.Application;
namespace MovieRecap.Infrastructure;

// Media owns this adapter and every Media-table read/lock used by preparation orchestration.
public sealed class PreparationMediaAccess(IDbContextFactory<PlatformDbContext> factory, IMediaSourceVerifier verifier) : IPreparationMediaAccess
{
    public async Task<PreparationMedia?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var view = await ReadAsync(db, id, ct); await tx.CommitAsync(ct); return view;
    }
    internal static async Task<PreparationMedia?> ReadAsync(PlatformDbContext db, Guid id, CancellationToken ct)
    {
        var import = await db.Imports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (import is null) return null;
        var asset = import.AssetId is { } a ? await db.Assets.AsNoTracking().SingleAsync(x => x.Id == a, ct) : null;
        var inventory = asset is null ? null : await db.StreamInventories.AsNoTracking().SingleOrDefaultAsync(x => x.AssetId == asset.Id, ct);
        var selection = await db.StreamSelections.AsNoTracking().SingleOrDefaultAsync(x => x.ImportId == id, ct);
        var rights = (await db.ImportRights.AsNoTracking().SingleAsync(x => x.ImportId == id, ct)).Details;
        var streams = inventory is null ? [] : await db.MediaStreams.AsNoTracking().Where(x => x.AssetId == inventory.AssetId).OrderBy(x => x.Index).ToListAsync(ct);
        return new(id, asset?.Id, inventory?.Version, selection?.Version, selection?.VideoIndex, selection?.AudioIndex, selection?.SubtitleIndex, asset?.Sha256, asset?.SizeBytes, import.CompletedAt is not null,
            import.Source, import.RequestedBy, new(rights.Owner, rights.Authorization.ToString(), rights.LicenseEvidence, rights.PermittedUses, rights.PermittedPlatforms, rights.Attribution, rights.ExpiresAt),
            streams.Select(x => new PreparationTrack(x.Index, x.Details.Kind.ToString(), x.Details.Codec, x.Details.Language, x.Details.IsSelectable, x.Details.IsDefault, x.Details.IsForced, x.Details.IsAttachedPicture)).ToArray());
    }
    public async Task<IVerifiedMediaSource> OpenAsync(PreparationMedia snapshot, CancellationToken ct)
    {
        var current = await GetAsync(snapshot.ImportId, ct) ?? throw new ImportConflictException("Preparation source unavailable."); RequireEqual(snapshot, current);
        await using var db = await factory.CreateDbContextAsync(ct); var import = await db.Imports.AsNoTracking().SingleAsync(x => x.Id == snapshot.ImportId, ct);
        var asset = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == snapshot.AssetId, ct); return await verifier.OpenAsync(new(import, asset), ct);
    }
    internal static void RequireEqual(PreparationMedia expected, PreparationMedia actual)
    {
        if (expected.ImportId != actual.ImportId || expected.AssetId != actual.AssetId || expected.InventoryVersion != actual.InventoryVersion || expected.SelectionVersion != actual.SelectionVersion ||
            expected.VideoIndex != actual.VideoIndex || expected.AudioIndex != actual.AudioIndex || expected.SubtitleIndex != actual.SubtitleIndex || expected.Sha256 != actual.Sha256 || expected.SizeBytes != actual.SizeBytes || expected.ImportCompleted != actual.ImportCompleted)
            throw new ImportConflictException("Preparation selection/source changed; reload.");
    }
}
internal sealed class PostgresPreparationMediaTransaction(PlatformDbContext db) : IPreparationMediaTransaction
{
    public async Task RequireAsync(PreparationMedia snapshot, CancellationToken ct)
    {
        await db.Imports.FromSqlInterpolated($"SELECT * FROM media.\"MovieImports\" WHERE \"Id\" = {snapshot.ImportId} FOR SHARE").AsNoTracking().SingleAsync(ct);
        await db.StreamSelections.FromSqlInterpolated($"SELECT * FROM media.\"StreamSelections\" WHERE \"ImportId\" = {snapshot.ImportId} FOR SHARE").AsNoTracking().ToListAsync(ct);
        var current = await PreparationMediaAccess.ReadAsync(db, snapshot.ImportId, ct) ?? throw new ImportConflictException("Preparation import unavailable."); PreparationMediaAccess.RequireEqual(snapshot, current);
    }
}
