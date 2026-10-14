namespace MovieRecap.VideoProcessing.Domain;

public sealed record SubtitleCueData(int Ordinal, long StartMilliseconds, long EndMilliseconds, string Text)
{
    public void Validate()
    {
        if (Ordinal < 0 || StartMilliseconds < 0 || EndMilliseconds <= StartMilliseconds || EndMilliseconds >= 360_000_000 || string.IsNullOrWhiteSpace(Text) || Text.Length > 4096 ||
            Text.Contains('\r') || Text.Contains("\n\n") || Text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
            throw new ArgumentException("Invalid subtitle cue.");
    }
}
public sealed record ExtractedSubtitle(IReadOnlyList<SubtitleCueData> Cues, string FfmpegVersion)
{
    public const string Profile = "srt-utf8-source-timestamps-v1";
    public void Validate()
    {
        if (Cues is null || Cues.Count is < 1 or > 100000 || string.IsNullOrWhiteSpace(FfmpegVersion) || FfmpegVersion.Length > 256) throw new ArgumentException("Invalid subtitle result.");
        long previous = -1;
        for (var i = 0; i < Cues.Count; i++) { Cues[i].Validate(); if (Cues[i].Ordinal != i || Cues[i].StartMilliseconds < previous) throw new ArgumentException("Invalid cue order."); previous = Cues[i].StartMilliseconds; }
    }
}
public sealed record SubtitleArtifact(Guid AttemptToken, string Sha256, long SizeBytes, ExtractedSubtitle Subtitle)
{
    public void Validate()
    {
        Subtitle.Validate();
        if (AttemptToken == Guid.Empty || Sha256 is null || Sha256.Length != 64 || Sha256.Any(c => !"0123456789abcdef".Contains(c)) || SizeBytes is < 1 or > 16777216) throw new ArgumentException("Invalid subtitle artifact.");
    }
}
public sealed class SubtitleExtraction
{
    private SubtitleExtraction() { }
    public Guid Id { get; private set; }
    public Guid ImportId { get; private set; }
    public Guid AssetId { get; private set; }
    public Guid InventoryVersion { get; private set; }
    public Guid SelectionVersion { get; private set; }
    public int SubtitleIndex { get; private set; }
    public string SourceSha256 { get; private set; } = "";
    public long SourceSizeBytes { get; private set; }
    public Guid JobId { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public string RequestedBy { get; private set; } = "";
    public int MaxAttempts { get; private set; }
    public string Profile { get; private set; } = ExtractedSubtitle.Profile;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public Guid? ArtifactToken { get; private set; }
    public string? ArtifactSha256 { get; private set; }
    public long? ArtifactSizeBytes { get; private set; }
    public int? CueCount { get; private set; }
    public string? FfmpegVersion { get; private set; }
    public static SubtitleExtraction Create(Guid importId, Guid assetId, Guid inventory, Guid selection, int index, string sha, long size, Guid job, string key, string actor, int attempts, DateTimeOffset now)
    {
        if (new[] { importId, assetId, inventory, selection, job }.Contains(Guid.Empty) || index < 0 || sha is null || sha.Length != 64 || sha.Any(c => !"0123456789abcdef".Contains(c)) || size <= 0 ||
            string.IsNullOrWhiteSpace(key) || key.Length > 128 || string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || attempts is < 1 or > 5) throw new ArgumentException("Invalid subtitle extraction binding.");
        return new() { Id = Guid.NewGuid(), ImportId = importId, AssetId = assetId, InventoryVersion = inventory, SelectionVersion = selection, SubtitleIndex = index, SourceSha256 = sha, SourceSizeBytes = size,
            JobId = job, IdempotencyKey = key, RequestedBy = actor, MaxAttempts = attempts, CreatedAt = now };
    }
    public void Complete(SubtitleArtifact artifact, DateTimeOffset now)
    {
        if (CompletedAt is not null) throw new InvalidOperationException("Subtitle extraction already completed.");
        artifact.Validate(); ArtifactToken = artifact.AttemptToken; ArtifactSha256 = artifact.Sha256; ArtifactSizeBytes = artifact.SizeBytes;
        CueCount = artifact.Subtitle.Cues.Count; FfmpegVersion = artifact.Subtitle.FfmpegVersion; CompletedAt = now;
    }
}
public sealed class SubtitleCue
{
    private SubtitleCue() { }
    public Guid ExtractionId { get; private set; }
    public int Ordinal { get; private set; }
    public long StartMilliseconds { get; private set; }
    public long EndMilliseconds { get; private set; }
    public string Text { get; private set; } = "";
    public SubtitleCueData Data => new(Ordinal, StartMilliseconds, EndMilliseconds, Text);
    public static SubtitleCue Create(Guid id, SubtitleCueData cue) { cue.Validate(); if (id == Guid.Empty) throw new ArgumentException("Invalid extraction."); return new() { ExtractionId = id, Ordinal = cue.Ordinal, StartMilliseconds = cue.StartMilliseconds, EndMilliseconds = cue.EndMilliseconds, Text = cue.Text }; }
}
