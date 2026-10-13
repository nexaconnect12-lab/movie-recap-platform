namespace MovieRecap.VideoProcessing.Domain;

public sealed record FramePlan(int Ordinal, int SceneOrdinal, int FrameIndex, double StartSeconds, double EndSeconds);
public sealed record SampledFrame(int Ordinal, double SourceSeconds, double NormalizedSeconds, string Sha256, long SizeBytes)
{
    public void Validate(FramePlan plan, double sourceOffset)
    {
        if (Ordinal != plan.Ordinal || !double.IsFinite(SourceSeconds) || !double.IsFinite(NormalizedSeconds) ||
            Math.Abs(SourceSeconds - sourceOffset - NormalizedSeconds) > 0.000001 || NormalizedSeconds < plan.StartSeconds - 0.000001 || NormalizedSeconds >= plan.EndSeconds ||
            Sha256 is null || Sha256.Length != 64 || Sha256.Any(c => !"0123456789abcdef".Contains(c)) || SizeBytes is < 1 or > 4_194_304)
            throw new ArgumentException("Frame artifact does not match its scene timeline.");
    }
}
public sealed record FrameResult(Guid Token, string FfmpegVersion, IReadOnlyList<SampledFrame> Frames)
{
    public void Validate(IReadOnlyList<FramePlan> plans, double offset)
    {
        if (Token == Guid.Empty || string.IsNullOrWhiteSpace(FfmpegVersion) || FfmpegVersion.Length > 256 || Frames is null || Frames.Count != plans.Count || Frames.Count is < 1 or > 300)
            throw new ArgumentException("Incomplete frame batch.");
        for (var i = 0; i < plans.Count; i++) { Frames[i].Validate(plans[i], offset); if (i > 0 && Frames[i].SourceSeconds <= Frames[i - 1].SourceSeconds) throw new ArgumentException("Frame timestamps must increase."); }
    }
}
public sealed class FrameSampling
{
    public const string FixedProfile = "png-rgb24-320x180-frame-quantiles-v1";
    private FrameSampling() { }
    public Guid Id { get; private set; }
    public Guid SceneAnalysisId { get; private set; }
    public Guid JobId { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public string RequestedBy { get; private set; } = "";
    public int MaxAttempts { get; private set; }
    public int SamplesPerScene { get; private set; }
    public string Profile { get; private set; } = FixedProfile;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public Guid? ArtifactToken { get; private set; }
    public string? FfmpegVersion { get; private set; }
    public static FrameSampling Create(Guid analysisId, Guid jobId, string key, string actor, int samples, int attempts, DateTimeOffset now)
    {
        if (analysisId == Guid.Empty || jobId == Guid.Empty || string.IsNullOrWhiteSpace(key) || key.Length > 128 || string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || samples is < 1 or > 3 || attempts is < 1 or > 5)
            throw new ArgumentException("Invalid frame sampling request.");
        return new() { Id = Guid.NewGuid(), SceneAnalysisId = analysisId, JobId = jobId, IdempotencyKey = key, RequestedBy = actor, SamplesPerScene = samples, MaxAttempts = attempts, CreatedAt = now };
    }
    public static IReadOnlyList<FramePlan> Plan(IReadOnlyList<MovieScene> scenes, int samplesPerScene)
    {
        if (samplesPerScene is < 1 or > 3 || scenes.Count == 0) throw new ArgumentException("Completed scenes required.");
        var plans = new List<FramePlan>(); var previousEnd = 0; var previousSeconds = 0.0;
        foreach (var scene in scenes)
        {
            if (scene.Ordinal != plans.Select(x => x.SceneOrdinal).Distinct().Count() || scene.StartFrame != previousEnd || scene.EndFrame <= scene.StartFrame ||
                !double.IsFinite(scene.StartSeconds) || !double.IsFinite(scene.EndSeconds) || Math.Abs(scene.StartSeconds - previousSeconds) > 0.000001 || scene.EndSeconds <= scene.StartSeconds)
                throw new ArgumentException("Invalid scene sequence.");
            var length = scene.EndFrame - scene.StartFrame; var count = Math.Min(samplesPerScene, length);
            for (var sample = 0; sample < count; sample++) plans.Add(new(plans.Count, scene.Ordinal, scene.StartFrame + (int)((2L * sample + 1) * length / (2L * count)), scene.StartSeconds, scene.EndSeconds));
            previousEnd = scene.EndFrame; previousSeconds = scene.EndSeconds;
            if (plans.Count > 300) throw new ArgumentException("Frame sampling is limited to 300 images per request.");
        }
        return plans;
    }
    public void Complete(FrameResult result, IReadOnlyList<FramePlan> plans, double offset, DateTimeOffset now)
    {
        if (CompletedAt is not null) throw new InvalidOperationException("Frame sampling already completed."); result.Validate(plans, offset);
        ArtifactToken = result.Token; FfmpegVersion = result.FfmpegVersion; CompletedAt = now;
    }
}
public sealed class FrameSample
{
    private FrameSample() { }
    public Guid SamplingId { get; private set; }
    public int Ordinal { get; private set; }
    public int SceneOrdinal { get; private set; }
    public int FrameIndex { get; private set; }
    public double StartSeconds { get; private set; }
    public double EndSeconds { get; private set; }
    public double? SourceSeconds { get; private set; }
    public double? NormalizedSeconds { get; private set; }
    public string? Sha256 { get; private set; }
    public long? SizeBytes { get; private set; }
    public FramePlan Plan => new(Ordinal, SceneOrdinal, FrameIndex, StartSeconds, EndSeconds);
    public static FrameSample Create(Guid id, FramePlan plan) => new() { SamplingId = id, Ordinal = plan.Ordinal, SceneOrdinal = plan.SceneOrdinal, FrameIndex = plan.FrameIndex, StartSeconds = plan.StartSeconds, EndSeconds = plan.EndSeconds };
    public void Complete(SampledFrame frame, double offset) { if (Sha256 is not null) throw new InvalidOperationException("Frame already completed."); frame.Validate(Plan, offset); SourceSeconds = frame.SourceSeconds; NormalizedSeconds = frame.NormalizedSeconds; Sha256 = frame.Sha256; SizeBytes = frame.SizeBytes; }
}
