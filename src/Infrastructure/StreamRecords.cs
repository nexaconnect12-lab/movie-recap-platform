using Microsoft.EntityFrameworkCore;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Infrastructure;

public sealed class StreamInventoryRecord
{
    public Guid AssetId { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTimeOffset InspectedAt { get; set; }
}
public sealed class MediaStreamRecord
{
    public Guid AssetId { get; set; }
    public int Index { get; set; }
    public MediaStreamInfo Details { get; set; } = null!;
}
public sealed class StreamInspectionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImportId { get; set; }
    public Guid JobId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public int MaxAttempts { get; set; }
}
public sealed class SelectionAuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Sequence { get; set; }
    public Guid ImportId { get; set; }
    public Guid BeforeVersion { get; set; }
    public Guid AfterVersion { get; set; }
    public Guid InventoryVersion { get; set; }
    public int VideoIndex { get; set; }
    public int? AudioIndex { get; set; }
    public int? SubtitleIndex { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset At { get; set; }
}
internal static class StreamPersistence
{
    // Caller owns the content advisory lock and transaction; inventory is immutable after real inspection.
    internal static async Task<StreamInventoryRecord?> InventoryAsync(PlatformDbContext db, Guid assetId,
        IReadOnlyList<MediaStreamInfo>? streams, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.StreamInventories.SingleOrDefaultAsync(x => x.AssetId == assetId, ct);
        if (streams is null) return existing; // Legacy or low-level test input: never manufacture streams from summaries.
        MediaStreamInfo.ValidateInventory(streams);
        if (existing is not null)
        {
            var rows = await db.MediaStreams.Where(x => x.AssetId == assetId).OrderBy(x => x.Index).ToListAsync(ct);
            if (!rows.Select(x => x.Details).SequenceEqual(streams.OrderBy(x => x.Index))) throw new ImportException("stream-inventory-conflict");
            return existing;
        }
        var inventory = new StreamInventoryRecord { AssetId = assetId, InspectedAt = now }; db.StreamInventories.Add(inventory);
        foreach (var stream in streams) db.MediaStreams.Add(new() { AssetId = assetId, Index = stream.Index, Details = stream });
        return inventory;
    }
    internal static async Task InitializeSelectionAsync(PlatformDbContext db, MovieImport import, StreamInventoryRecord? inventory, CancellationToken ct)
    {
        if (inventory is not null && !await db.StreamSelections.AnyAsync(x => x.ImportId == import.Id, ct))
            db.StreamSelections.Add(StreamSelection.Create(import.Id, inventory.AssetId, inventory.Version));
    }
}
