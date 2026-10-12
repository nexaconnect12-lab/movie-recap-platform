using Microsoft.EntityFrameworkCore;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Infrastructure;

public sealed class SelectedAudioAccess(IStreamStore store, IMediaSourceVerifier verifier) : ISelectedAudioAccess
{
    public async Task<SelectedAudioSnapshot?> GetAsync(Guid importId, CancellationToken ct)
    {
        var view = await store.GetAsync(importId, ct); if (view is null) return null;
        if (view.Selection is not { AudioIndex: { } index } selection || view.InventoryVersion is not { } inventory || view.AssetId is not { } assetId ||
            !view.Streams.Any(x => x.Index == index && x.Kind == MediaStreamKind.Audio && x.IsSelectable))
            throw new ImportConflictException("Inspect and explicitly select an audio stream before extraction.");
        var source = await store.SourceAsync(importId, ct) ?? throw new ImportConflictException("Media source unavailable.");
        return new(importId, assetId, inventory, selection.Version, index, source.Asset.Sha256, source.Asset.SizeBytes);
    }
    public async Task<IVerifiedMediaSource> OpenAsync(SelectedAudioSnapshot snapshot, CancellationToken ct)
    {
        if (await GetAsync(snapshot.ImportId, ct) != snapshot) throw new ImportConflictException("Selected audio snapshot changed.");
        return await verifier.OpenAsync(await store.SourceAsync(snapshot.ImportId, ct) ?? throw new ImportConflictException("Media source unavailable."), ct);
    }
}
internal sealed class PostgresSelectedAudioTransaction(PlatformDbContext db) : ISelectedAudioTransaction
{
    public async Task RequireAsync(SelectedAudioSnapshot snapshot, CancellationToken ct)
    {
        var selection = await db.StreamSelections.FromSqlInterpolated($"SELECT * FROM media.\"StreamSelections\" WHERE \"ImportId\" = {snapshot.ImportId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
        if (selection is null || selection.AssetId != snapshot.AssetId || selection.InventoryVersion != snapshot.InventoryVersion || selection.Version != snapshot.SelectionVersion || selection.AudioIndex != snapshot.AudioIndex)
            throw new ImportConflictException("Audio extraction selection changed.");
        var source = await db.Imports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == snapshot.ImportId, ct);
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == snapshot.AssetId, ct);
        if (source?.AssetId != snapshot.AssetId || asset is null || asset.Sha256 != snapshot.Sha256 || asset.SizeBytes != snapshot.SizeBytes)
            throw new ImportConflictException("Audio extraction source changed.");
    }
}
