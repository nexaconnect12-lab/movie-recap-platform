using Microsoft.EntityFrameworkCore;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Infrastructure;

public sealed class SelectedVideoAccess(IStreamStore store, IMediaSourceVerifier verifier) : ISelectedVideoAccess
{
    public async Task<SelectedVideoSnapshot?> GetAsync(Guid importId, CancellationToken ct)
    {
        var view = await store.GetAsync(importId, ct); if (view is null) return null;
        if (view.Selection is not { VideoIndex: { } index } selection || view.InventoryVersion is not { } inventory || view.AssetId is not { } assetId)
            throw new ImportConflictException("Inspect and explicitly select a video stream before scene analysis.");
        var video = view.Streams.SingleOrDefault(x => x.Index == index && x.Kind == MediaStreamKind.Video && x.IsSelectable)
            ?? throw new ImportConflictException("Selected video is unavailable in its inventory.");
        var source = await store.SourceAsync(importId, ct) ?? throw new ImportConflictException("Media source is unavailable.");
        return new(importId, assetId, inventory, selection.Version, index, source.Asset.Sha256, source.Asset.SizeBytes, video.FrameRate!.Value);
    }
    public async Task<IVerifiedMediaSource> OpenAsync(SelectedVideoSnapshot snapshot, CancellationToken ct)
    {
        var current = await GetAsync(snapshot.ImportId, ct);
        if (current != snapshot) throw new ImportConflictException("Selected video snapshot changed.");
        var source = await store.SourceAsync(snapshot.ImportId, ct) ?? throw new ImportConflictException("Media source is unavailable.");
        return await verifier.OpenAsync(source, ct);
    }
}
internal sealed class PostgresSelectedVideoTransaction(PlatformDbContext db) : ISelectedVideoTransaction
{
    public async Task RequireAsync(SelectedVideoSnapshot snapshot, CancellationToken ct)
    {
        var selection = await db.StreamSelections.FromSqlInterpolated($"SELECT * FROM media.\"StreamSelections\" WHERE \"ImportId\" = {snapshot.ImportId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
        if (selection is null || selection.AssetId != snapshot.AssetId || selection.InventoryVersion != snapshot.InventoryVersion || selection.Version != snapshot.SelectionVersion || selection.VideoIndex != snapshot.VideoIndex)
            throw new ImportConflictException("Scene analysis selection changed.");
        var source = await db.Imports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == snapshot.ImportId, ct);
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == snapshot.AssetId, ct);
        if (source?.AssetId != snapshot.AssetId || asset is null || asset.Sha256 != snapshot.Sha256 || asset.SizeBytes != snapshot.SizeBytes)
            throw new ImportConflictException("Scene analysis source changed.");
    }
}
