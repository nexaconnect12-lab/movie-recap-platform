namespace MovieRecap.Media.Domain;

public enum DiscoveryStatus { Observing, Ready, Unavailable, Submitted, Dismissed }
// A metadata fingerprint binds a discovery to a location and observed file revision; it is not a content hash.
public sealed record FileObservation(string Folder, string RelativePath, string PathKey, string Fingerprint, long SizeBytes, bool CanRead, string? ErrorCode);
public sealed class MediaDiscovery
{
    private MediaDiscovery() { }
    public Guid Id { get; private set; }
    public string Folder { get; private set; } = "";
    public string RelativePath { get; private set; } = "";
    public string PathKey { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public DiscoveryStatus Status { get; private set; }
    public bool IsCurrent { get; private set; } = true;
    public bool IsAvailable { get; private set; }
    public string? ErrorCode { get; private set; }
    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset LastObservedAt { get; private set; }
    public DateTimeOffset StableSince { get; private set; }
    public Guid Version { get; private set; }
    public Guid? SubmittedVersion { get; private set; }
    public Guid? ImportId { get; private set; }
    public static MediaDiscovery Create(FileObservation file, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(file.Folder) || file.Folder.Length > 64 || string.IsNullOrWhiteSpace(file.RelativePath) || file.RelativePath.Length > 1024 ||
            file.PathKey.Length != 64 || file.Fingerprint.Length != 64 || file.SizeBytes < 0) throw new ArgumentException("Invalid discovery observation.");
        return new() { Id = Guid.NewGuid(), Folder = file.Folder, RelativePath = file.RelativePath, PathKey = file.PathKey,
            Fingerprint = file.Fingerprint, SizeBytes = file.SizeBytes, Status = DiscoveryStatus.Observing,
            IsAvailable = file.CanRead && file.SizeBytes > 0, ErrorCode = file.ErrorCode, FirstSeenAt = now, LastObservedAt = now, StableSince = now, Version = Guid.NewGuid() };
    }
    void Touch() => Version = Guid.NewGuid();
    public void Observe(FileObservation file, DateTimeOffset now, TimeSpan stability)
    {
        if (!IsCurrent || now < LastObservedAt) return;
        if (file.Fingerprint != Fingerprint) throw new ArgumentException("A changed file needs a new discovery revision.");
        LastObservedAt = now;
        var available = file.CanRead && file.SizeBytes > 0;
        if (!IsAvailable && available) StableSince = now;
        if (IsAvailable != available || ErrorCode != file.ErrorCode) Touch();
        IsAvailable = available; ErrorCode = file.ErrorCode;
        if (Status is DiscoveryStatus.Submitted or DiscoveryStatus.Dismissed) return;
        if (!available) { if (Status != DiscoveryStatus.Observing) Touch(); Status = DiscoveryStatus.Observing; StableSince = now; return; }
        if (Status == DiscoveryStatus.Unavailable) { Status = DiscoveryStatus.Observing; StableSince = now; Touch(); }
        if (Status == DiscoveryStatus.Observing && now - StableSince >= stability) { Status = DiscoveryStatus.Ready; Touch(); }
    }
    public void Missing(DateTimeOffset now)
    {
        if (!IsCurrent || now < LastObservedAt) return;
        LastObservedAt = now;
        if (IsAvailable || ErrorCode != "discovery-file-unavailable") Touch();
        IsAvailable = false; ErrorCode = "discovery-file-unavailable";
        if (Status is not (DiscoveryStatus.Submitted or DiscoveryStatus.Dismissed)) Status = DiscoveryStatus.Unavailable;
    }
    public void Supersede(DateTimeOffset now) { Missing(now); IsCurrent = false; Touch(); }
    public void Dismiss(Guid version)
    {
        if (Version != version || Status == DiscoveryStatus.Submitted || !IsCurrent) throw new InvalidOperationException("Discovery changed or cannot be dismissed.");
        Status = DiscoveryStatus.Dismissed; Touch();
    }
    public void Submit(Guid version, Guid importId)
    {
        if (Version != version || Status != DiscoveryStatus.Ready || !IsAvailable || !IsCurrent || importId == Guid.Empty)
            throw new InvalidOperationException("Discovery changed or is not ready.");
        SubmittedVersion = version; ImportId = importId; Status = DiscoveryStatus.Submitted; Touch();
    }
}
