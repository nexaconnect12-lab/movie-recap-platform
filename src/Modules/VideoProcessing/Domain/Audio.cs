namespace MovieRecap.VideoProcessing.Domain;

public sealed record ExtractedAudio(long Samples, string FfmpegVersion)
{
    public const string Profile = "wav-pcm-s16le-16000-mono-v1";
    public const int SampleRate = 16000;
    public double DurationSeconds => Samples / (double)SampleRate;
    public void Validate()
    {
        if (Samples <= 0 || Samples > (uint.MaxValue - 36L) / 2 || string.IsNullOrWhiteSpace(FfmpegVersion) || FfmpegVersion.Length > 256)
            throw new ArgumentException("Invalid decoded audio result.");
    }
}
public sealed record AudioArtifact(Guid AttemptToken, string Sha256, long SizeBytes, ExtractedAudio Audio)
{
    public void Validate()
    {
        Audio.Validate();
        if (AttemptToken == Guid.Empty || Sha256 is null || Sha256.Length != 64 || Sha256.Any(c => !"0123456789abcdef".Contains(c)) || SizeBytes != 44 + Audio.Samples * 2)
            throw new ArgumentException("Invalid audio artifact.");
    }
}
public sealed class AudioExtraction
{
    private AudioExtraction() { }
    public Guid Id { get; private set; }
    public Guid ImportId { get; private set; }
    public Guid AssetId { get; private set; }
    public Guid InventoryVersion { get; private set; }
    public Guid SelectionVersion { get; private set; }
    public int AudioIndex { get; private set; }
    public string SourceSha256 { get; private set; } = "";
    public long SourceSizeBytes { get; private set; }
    public Guid JobId { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public string RequestedBy { get; private set; } = "";
    public int MaxAttempts { get; private set; }
    public string Profile { get; private set; } = ExtractedAudio.Profile;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public Guid? ArtifactToken { get; private set; }
    public string? ArtifactSha256 { get; private set; }
    public long? ArtifactSizeBytes { get; private set; }
    public long? Samples { get; private set; }
    public string? FfmpegVersion { get; private set; }
    public double? DurationSeconds => Samples / (double?)ExtractedAudio.SampleRate;
    public static AudioExtraction Create(Guid importId, Guid assetId, Guid inventory, Guid selection, int index, string sha, long size,
        Guid job, string key, string actor, int attempts, DateTimeOffset now)
    {
        if (new[] { importId, assetId, inventory, selection, job }.Contains(Guid.Empty) || index < 0 || sha is null || sha.Length != 64 || sha.Any(c => !"0123456789abcdef".Contains(c)) || size <= 0 ||
            string.IsNullOrWhiteSpace(key) || key.Length > 128 || string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || attempts is < 1 or > 5) throw new ArgumentException("Invalid audio extraction binding.");
        return new() { Id = Guid.NewGuid(), ImportId = importId, AssetId = assetId, InventoryVersion = inventory, SelectionVersion = selection, AudioIndex = index, SourceSha256 = sha,
            SourceSizeBytes = size, JobId = job, IdempotencyKey = key, RequestedBy = actor, MaxAttempts = attempts, CreatedAt = now };
    }
    public void Complete(AudioArtifact artifact, DateTimeOffset now)
    {
        if (CompletedAt is not null) throw new InvalidOperationException("Audio extraction already completed.");
        artifact.Validate(); ArtifactToken = artifact.AttemptToken; ArtifactSha256 = artifact.Sha256; ArtifactSizeBytes = artifact.SizeBytes;
        Samples = artifact.Audio.Samples; FfmpegVersion = artifact.Audio.FfmpegVersion; CompletedAt = now;
    }
}
