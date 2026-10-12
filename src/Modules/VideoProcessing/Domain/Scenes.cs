namespace MovieRecap.VideoProcessing.Domain;

public sealed record SceneBoundary(int StartFrame, int EndFrame, double StartSeconds, double EndSeconds);
public sealed record SceneDetection(int VideoIndex, int TotalFrames, double DurationSeconds, double SourceOffsetSeconds,
    bool VariableFrameRate, string TimingMode, string SceneDetectVersion, string PyAvVersion, string OpenCvVersion,
    string PythonVersion, string Algorithm, double Threshold, int MinimumSceneFrames, IReadOnlyList<SceneBoundary> Scenes)
{
    public void Validate()
    {
        if (VideoIndex < 0 || TotalFrames <= 0 || !double.IsFinite(DurationSeconds) || DurationSeconds <= 0 || !double.IsFinite(SourceOffsetSeconds) ||
            TimingMode != "presentation-timestamps" || Algorithm != "content/suppress" || !double.IsFinite(Threshold) || Threshold is < 1 or > 255 || MinimumSceneFrames is < 1 or > 3000 ||
            new[] { SceneDetectVersion, PyAvVersion, OpenCvVersion, PythonVersion }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64) || Scenes is null || Scenes.Count is < 1 or > 10000)
            throw new ArgumentException("Invalid scene detection result.");
        var frame = 0; var seconds = 0.0;
        foreach (var scene in Scenes)
        {
            if (scene is null || scene.StartFrame != frame || scene.EndFrame <= scene.StartFrame || scene.EndFrame > TotalFrames ||
                !double.IsFinite(scene.StartSeconds) || !double.IsFinite(scene.EndSeconds) || Math.Abs(scene.StartSeconds - seconds) > 0.000001 || scene.EndSeconds <= scene.StartSeconds)
                throw new ArgumentException("Scene boundaries must be ordered, contiguous and non-overlapping.");
            frame = scene.EndFrame; seconds = scene.EndSeconds;
        }
        if (frame != TotalFrames || Math.Abs(seconds - DurationSeconds) > 0.000001) throw new ArgumentException("Scenes must cover the decoded video timeline.");
    }
}
public sealed class SceneAnalysis
{
    private SceneAnalysis() { }
    public Guid Id { get; private set; }
    public Guid ImportId { get; private set; }
    public Guid AssetId { get; private set; }
    public Guid InventoryVersion { get; private set; }
    public Guid SelectionVersion { get; private set; }
    public int VideoIndex { get; private set; }
    public string SourceSha256 { get; private set; } = "";
    public long SourceSizeBytes { get; private set; }
    public Guid JobId { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public string RequestedBy { get; private set; } = "";
    public double Threshold { get; private set; }
    public int MinimumSceneFrames { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public int? TotalFrames { get; private set; }
    public double? DurationSeconds { get; private set; }
    public double? SourceOffsetSeconds { get; private set; }
    public bool? VariableFrameRate { get; private set; }
    public string? TimingMode { get; private set; }
    public string? SceneDetectVersion { get; private set; }
    public string? PyAvVersion { get; private set; }
    public string? OpenCvVersion { get; private set; }
    public string? PythonVersion { get; private set; }
    public string? Algorithm { get; private set; }
    public static SceneAnalysis Create(Guid importId, Guid assetId, Guid inventory, Guid selection, int videoIndex, string sha, long size,
        Guid jobId, string key, string actor, double threshold, int minimumSceneFrames, int attempts, DateTimeOffset now)
    {
        if (new[] { importId, assetId, inventory, selection, jobId }.Contains(Guid.Empty) || videoIndex < 0 || sha.Length != 64 || sha.Any(c => !"0123456789abcdef".Contains(c)) || size <= 0 ||
            string.IsNullOrWhiteSpace(key) || key.Length > 128 || string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || !double.IsFinite(threshold) || threshold is < 1 or > 255 ||
            minimumSceneFrames is < 1 or > 3000 || attempts is < 1 or > 5) throw new ArgumentException("Invalid scene analysis request.");
        return new() { Id = Guid.NewGuid(), ImportId = importId, AssetId = assetId, InventoryVersion = inventory, SelectionVersion = selection, VideoIndex = videoIndex,
            SourceSha256 = sha, SourceSizeBytes = size, JobId = jobId, IdempotencyKey = key, RequestedBy = actor, Threshold = threshold,
            MinimumSceneFrames = minimumSceneFrames, MaxAttempts = attempts, CreatedAt = now };
    }
    public void Complete(SceneDetection detection, DateTimeOffset now)
    {
        if (CompletedAt is not null) throw new InvalidOperationException("Scene analysis already completed.");
        detection.Validate();
        if (detection.VideoIndex != VideoIndex || detection.Threshold != Threshold || detection.MinimumSceneFrames != MinimumSceneFrames)
            throw new ArgumentException("Scene result does not match its bound stream/settings.");
        TotalFrames = detection.TotalFrames; DurationSeconds = detection.DurationSeconds; SourceOffsetSeconds = detection.SourceOffsetSeconds;
        VariableFrameRate = detection.VariableFrameRate; TimingMode = detection.TimingMode; SceneDetectVersion = detection.SceneDetectVersion;
        PyAvVersion = detection.PyAvVersion; OpenCvVersion = detection.OpenCvVersion; PythonVersion = detection.PythonVersion; Algorithm = detection.Algorithm; CompletedAt = now;
    }
}
public sealed record MovieScene(Guid AnalysisId, int Ordinal, int StartFrame, int EndFrame, double StartSeconds, double EndSeconds);
