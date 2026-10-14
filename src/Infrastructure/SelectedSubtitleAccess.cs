using Microsoft.EntityFrameworkCore;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Infrastructure;

public sealed class SelectedSubtitleAccess(IStreamStore store, IMediaSourceVerifier verifier) : ISelectedSubtitleAccess
{
    public async Task<SelectedSubtitleSnapshot?> GetAsync(Guid importId, CancellationToken ct)
    {
        var view = await store.GetAsync(importId, ct); if (view is null) return null;
        if (view.Selection is not { SubtitleIndex: { } index } selection || view.InventoryVersion is not { } inventory || view.AssetId is not { } assetId ||
            !view.Streams.Any(x => x.Index == index && x.Kind == MediaStreamKind.Subtitle && x.IsSelectable))
            throw new ImportConflictException("Inspect and explicitly select an subtitle stream before extraction.");
        var source = await store.SourceAsync(importId, ct) ?? throw new ImportConflictException("Media source unavailable.");
        return new(importId, assetId, inventory, selection.Version, index, source.Asset.Sha256, source.Asset.SizeBytes, view.Streams.Single(x => x.Index == index).Codec!);
    }
    public async Task<IVerifiedMediaSource> OpenAsync(SelectedSubtitleSnapshot snapshot, CancellationToken ct)
    {
        if (await GetAsync(snapshot.ImportId, ct) != snapshot) throw new ImportConflictException("Selected subtitle snapshot changed.");
        return await verifier.OpenAsync(await store.SourceAsync(snapshot.ImportId, ct) ?? throw new ImportConflictException("Media source unavailable."), ct);
    }
}
internal sealed class PostgresSelectedSubtitleTransaction(PlatformDbContext db) : ISelectedSubtitleTransaction
{
    public async Task RequireAsync(SelectedSubtitleSnapshot snapshot, CancellationToken ct)
    {
        var selection = await db.StreamSelections.FromSqlInterpolated($"SELECT * FROM media.\"StreamSelections\" WHERE \"ImportId\" = {snapshot.ImportId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
        if (selection is null || selection.AssetId != snapshot.AssetId || selection.InventoryVersion != snapshot.InventoryVersion || selection.Version != snapshot.SelectionVersion || selection.SubtitleIndex != snapshot.SubtitleIndex)
            throw new ImportConflictException("Subtitle extraction selection changed.");
        var stream = await db.MediaStreams.AsNoTracking().SingleOrDefaultAsync(x => x.AssetId == snapshot.AssetId && x.Index == snapshot.SubtitleIndex, ct);
        if (stream?.Details.Kind != MediaStreamKind.Subtitle || stream.Details.Codec != snapshot.Codec) throw new ImportConflictException("Subtitle codec binding changed.");
        var source = await db.Imports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == snapshot.ImportId, ct);
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == snapshot.AssetId, ct);
        if (source?.AssetId != snapshot.AssetId || asset is null || asset.Sha256 != snapshot.Sha256 || asset.SizeBytes != snapshot.SizeBytes)
            throw new ImportConflictException("Subtitle extraction source changed.");
    }
}
