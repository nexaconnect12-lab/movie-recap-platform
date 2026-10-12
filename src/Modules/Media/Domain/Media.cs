namespace MovieRecap.Media.Domain;

public enum AuthorizationStatus { Unknown, Authorized, Denied }
public sealed record MediaRights(string Owner, AuthorizationStatus Authorization, string LicenseEvidence,
    string PermittedUses, string PermittedPlatforms, string Attribution, DateTimeOffset? ExpiresAt)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Authorization)) throw new ArgumentException("Invalid authorization status.");
        if (string.IsNullOrWhiteSpace(Owner) || Owner.Length > 256) throw new ArgumentException("Owner is required (maximum 256 characters).");
        foreach (var value in new[] { LicenseEvidence, PermittedUses, PermittedPlatforms, Attribution })
            if (value is null || value.Length > 2048) throw new ArgumentException("Rights fields must be supplied (maximum 2048 characters).");
        if (Authorization == AuthorizationStatus.Authorized &&
            (string.IsNullOrWhiteSpace(LicenseEvidence) || string.IsNullOrWhiteSpace(PermittedUses) || string.IsNullOrWhiteSpace(PermittedPlatforms)))
            throw new ArgumentException("Authorized imports require license evidence, permitted uses and platforms.");
        if (ExpiresAt is { Offset: var offset } && offset != TimeSpan.Zero) throw new ArgumentException("Rights expiry must use UTC.");
    }
}
public sealed record MediaMetadata(string Container, double DurationSeconds, string VideoCodec,
    int Width, int Height, double FrameRate, string? AudioCodec, int? AudioChannels, int? AudioSampleRate)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Container) || Container.Length > 128 || string.IsNullOrWhiteSpace(VideoCodec) || VideoCodec.Length > 64 ||
            !double.IsFinite(DurationSeconds) || DurationSeconds <= 0 || Width <= 0 || Height <= 0 ||
            !double.IsFinite(FrameRate) || FrameRate <= 0 || AudioCodec?.Length > 64 || AudioChannels <= 0 || AudioSampleRate <= 0)
            throw new ArgumentException("Invalid media metadata.");
    }
}
public sealed class MovieImport
{
    private MovieImport() { }
    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public string Folder { get; private set; } = "";
    public string RelativePath { get; private set; } = "";
    public string Source { get; private set; } = "";
    public string RequestedBy { get; private set; } = "";
    public int MaxAttempts { get; private set; }
    public string? ExpectedFingerprint { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? AssetId { get; private set; }
    public bool IsDuplicate { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public static MovieImport Create(Guid jobId, string key, string folder, string path, string source, string requestedBy, int maxAttempts, DateTimeOffset now, string? expectedFingerprint = null)
    {
        if (jobId == Guid.Empty || string.IsNullOrWhiteSpace(key) || key.Length > 128 ||
            string.IsNullOrWhiteSpace(folder) || folder.Length > 64 || string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            string.IsNullOrWhiteSpace(source) || source.Length > 2048 || string.IsNullOrWhiteSpace(requestedBy) || requestedBy.Length > 256 || maxAttempts is < 1 or > 5)
            throw new ArgumentException("Invalid import metadata.");
        if (expectedFingerprint is not null && expectedFingerprint.Length != 64) throw new ArgumentException("Invalid discovery fingerprint.");
        return new() { Id = Guid.NewGuid(), JobId = jobId, IdempotencyKey = key, Folder = folder, RelativePath = path, ExpectedFingerprint = expectedFingerprint,
            Source = source, RequestedBy = requestedBy, MaxAttempts = maxAttempts, CreatedAt = now };
    }
    public void Complete(Guid assetId, bool duplicate, DateTimeOffset now)
    {
        if (assetId == Guid.Empty || AssetId is not null) throw new InvalidOperationException("Import cannot complete.");
        AssetId = assetId; IsDuplicate = duplicate; CompletedAt = now;
    }
}
public sealed class MediaAsset
{
    private MediaAsset() { }
    public Guid Id { get; private set; }
    public string Sha256 { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public MediaMetadata Metadata { get; private set; } = null!;
    public static MediaAsset Create(string hash, long size, MediaMetadata metadata, DateTimeOffset now)
    {
        if (hash.Length != 64 || hash.Any(c => !"0123456789abcdef".Contains(c)) || size <= 0) throw new ArgumentException("Invalid media fingerprint.");
        metadata.Validate();
        return new() { Id = Guid.NewGuid(), Sha256 = hash, SizeBytes = size, Metadata = metadata, CreatedAt = now };
    }
}
