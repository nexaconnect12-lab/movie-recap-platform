namespace MovieRecap.Media.Application;

public sealed record PreparationTrack(int Index, string Kind, string? Codec, string? Language, bool IsSelectable, bool IsDefault, bool IsForced, bool IsAttachedPicture);
public sealed record PreparationRights(string Owner, string Authorization, string LicenseEvidence, string PermittedUses, string PermittedPlatforms, string Attribution, DateTimeOffset? ExpiresAt);
public sealed record PreparationMedia(Guid ImportId, Guid? AssetId, Guid? InventoryVersion, Guid? SelectionVersion,
    int? VideoIndex, int? AudioIndex, int? SubtitleIndex, string? Sha256, long? SizeBytes, bool ImportCompleted,
    string Source, string RequestedBy, PreparationRights Rights, IReadOnlyList<PreparationTrack> Streams);
public interface IPreparationMediaAccess
{
    Task<PreparationMedia?> GetAsync(Guid importId, CancellationToken ct);
    Task<IVerifiedMediaSource> OpenAsync(PreparationMedia snapshot, CancellationToken ct);
}
public interface IPreparationMediaTransaction
{
    Task RequireAsync(PreparationMedia snapshot, CancellationToken ct);
}
