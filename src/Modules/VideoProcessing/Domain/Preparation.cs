using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace MovieRecap.VideoProcessing.Domain;

public enum PreparationStatus { Pending, Approved, Rejected, Stale }
public sealed record PreparationBinding(Guid ImportId, Guid? AssetId, Guid? InventoryVersion, Guid? SelectionVersion, int? VideoIndex, int? AudioIndex, int? SubtitleIndex,
    string? SourceSha256, long? SourceSizeBytes, Guid? SceneId, Guid? AudioId, Guid? FrameId, Guid? SubtitleId = null, int SchemaVersion = 2)
{
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', "preparation-v1", ImportId.ToString("N"), AssetId?.ToString("N"), InventoryVersion?.ToString("N"), SelectionVersion?.ToString("N"),
        VideoIndex?.ToString(CultureInfo.InvariantCulture), AudioIndex?.ToString(CultureInfo.InvariantCulture), SubtitleIndex?.ToString(CultureInfo.InvariantCulture), SourceSha256, SourceSizeBytes?.ToString(CultureInfo.InvariantCulture), SceneId?.ToString("N"), AudioId?.ToString("N"), FrameId?.ToString("N")) + (SchemaVersion == 1 || SubtitleIndex is null ? "" : "|subtitle-v1|" + SubtitleId?.ToString("N")))));
    public bool IsReady => ImportId != Guid.Empty && AssetId is { } asset && asset != Guid.Empty && InventoryVersion is { } inventory && inventory != Guid.Empty && SelectionVersion is { } selection && selection != Guid.Empty && VideoIndex is >= 0 &&
        SchemaVersion is 1 or 2 && PreparationValidation.ValidHash(SourceSha256) && SourceSizeBytes > 0 && SceneId is { } scene && scene != Guid.Empty && FrameId is { } frame && frame != Guid.Empty && (AudioIndex is null || AudioIndex >= 0 && AudioId is { } audio && audio != Guid.Empty) && (SubtitleIndex is null || SubtitleIndex >= 0 && (SchemaVersion == 1 || SubtitleId is { } subtitle && subtitle != Guid.Empty));
}
public sealed record PreparationIssue(string Target, string Code);
public sealed class PreparationValidation
{
    private PreparationValidation() { }
    public Guid Id { get; private set; }
    public Guid ImportId { get; private set; }
    public Guid JobId { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public string RequestedBy { get; private set; } = "";
    public int MaxAttempts { get; private set; }
    public string SnapshotHash { get; private set; } = "";
    public PreparationBinding Binding { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public bool? Passed { get; private set; }
    public string? IssuesJson { get; private set; }
    public static PreparationValidation Create(Guid importId, Guid jobId, string key, string actor, int attempts, PreparationBinding binding, DateTimeOffset now)
    {
        if (importId == Guid.Empty || jobId == Guid.Empty || string.IsNullOrWhiteSpace(key) || key.Length > 128 || string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || attempts is < 1 or > 5 || binding is null || !binding.IsReady || binding.ImportId != importId) throw new ArgumentException("Invalid preparation validation.");
        return new() { Id = Guid.NewGuid(), ImportId = importId, JobId = jobId, IdempotencyKey = key, RequestedBy = actor, MaxAttempts = attempts, SnapshotHash = binding.Hash, Binding = binding, CreatedAt = now };
    }
    public void Complete(IReadOnlyList<PreparationIssue> issues, DateTimeOffset now)
    {
        if (CompletedAt is not null) throw new InvalidOperationException("Validation already completed.");
        if (issues.Count > 303 || issues.Any(x => x is null || string.IsNullOrWhiteSpace(x.Target) || x.Target.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(x.Code ?? "", "^[a-z][a-z0-9-]{0,127}$"))) throw new ArgumentException("Invalid validation issues.");
        IssuesJson = System.Text.Json.JsonSerializer.Serialize(issues); if (IssuesJson.Length > 65536) throw new ArgumentException("Validation report too large."); Passed = issues.Count == 0; CompletedAt = now;
    }
    public static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(c => "0123456789abcdef".Contains(c));
}
public sealed class PreparationReview
{
    private PreparationReview() { }
    public Guid ImportId { get; private set; }
    public Guid Version { get; private set; }
    public PreparationStatus Status { get; private set; }
    public Guid? LastValidationId { get; private set; }
    public string? ValidationSnapshotHash { get; private set; }
    public bool? ValidationPassed { get; private set; }
    public Guid? DecisionValidationId { get; private set; }
    public string? DecisionSnapshotHash { get; private set; }
    public string? DecidedBy { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public static PreparationReview Create(Guid id, DateTimeOffset now) { if (id == Guid.Empty) throw new ArgumentException("Import required."); return new() { ImportId = id, Version = Guid.NewGuid(), Status = PreparationStatus.Pending, UpdatedAt = now }; }
    public void Validated(PreparationValidation validation, DateTimeOffset now)
    {
        if (validation.ImportId != ImportId || validation.CompletedAt is null) throw new ArgumentException("Completed validation required.");
        LastValidationId = validation.Id; ValidationSnapshotHash = validation.SnapshotHash; ValidationPassed = validation.Passed;
        if (DecidedAt is not null) Status = PreparationStatus.Stale; Version = Guid.NewGuid(); UpdatedAt = now;
    }
    public PreparationStatus EffectiveStatus(string currentHash) => Status is PreparationStatus.Approved or PreparationStatus.Rejected && DecisionSnapshotHash != currentHash ? PreparationStatus.Stale : Status;
    public void Decide(Guid expectedVersion, string hash, PreparationStatus decision, string actor, string notes, DateTimeOffset now)
    {
        if (expectedVersion != Version) throw new InvalidOperationException("Preparation review version changed.");
        if (decision is not (PreparationStatus.Approved or PreparationStatus.Rejected) || !PreparationValidation.ValidHash(hash) || string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || notes is null || notes.Length > 2048) throw new ArgumentException("Invalid preparation decision.");
        if (decision == PreparationStatus.Approved && (LastValidationId is null || ValidationPassed != true || ValidationSnapshotHash != hash)) throw new InvalidOperationException("Current successful validation required before approval.");
        if (decision == PreparationStatus.Rejected && string.IsNullOrWhiteSpace(notes)) throw new ArgumentException("Rejection requires notes.");
        Status = decision; DecisionSnapshotHash = hash; DecisionValidationId = LastValidationId; DecidedBy = actor; Notes = notes; DecidedAt = now; UpdatedAt = now; Version = Guid.NewGuid();
    }
}
public sealed class PreparationAudit
{
    public Guid Id { get; set; } = Guid.NewGuid(); public long Sequence { get; set; } public Guid ImportId { get; set; }
    public string Action { get; set; } = ""; public Guid BeforeVersion { get; set; } public Guid AfterVersion { get; set; }
    public string SnapshotHash { get; set; } = ""; public Guid? ValidationId { get; set; } public string RequestedBy { get; set; } = ""; public string Notes { get; set; } = ""; public DateTimeOffset At { get; set; }
    public PreparationBinding Binding { get; set; } = null!;
}
