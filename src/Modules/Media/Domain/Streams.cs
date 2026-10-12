namespace MovieRecap.Media.Domain;

public enum MediaStreamKind { Video, Audio, Subtitle, Other }
public sealed record MediaStreamInfo(int Index, MediaStreamKind Kind, string? Codec, string? Language,
    double? DurationSeconds, bool IsDefault, bool IsForced, bool IsAttachedPicture,
    int? Width, int? Height, double? FrameRate, int? Channels, int? SampleRate, string? ReportedType = null)
{
    public bool IsSelectable => Kind switch
    {
        MediaStreamKind.Video => !IsAttachedPicture && Codec is not null && Width > 0 && Height > 0 && FrameRate > 0,
        MediaStreamKind.Audio => Codec is not null && Channels > 0 && SampleRate > 0,
        MediaStreamKind.Subtitle => Codec is not null,
        _ => false
    };
    public void Validate()
    {
        if (Index < 0 || !Enum.IsDefined(Kind) || Codec is { Length: > 64 } || Codec is "" || Language is { Length: > 64 } ||
            ReportedType is { Length: > 32 } || DurationSeconds is { } duration && (!double.IsFinite(duration) || duration < 0) ||
            FrameRate is { } rate && (!double.IsFinite(rate) || rate < 0) || Width < 0 || Height < 0 || Channels < 0 || SampleRate < 0)
            throw new ArgumentException("Invalid stream metadata.");
    }
    public static void ValidateInventory(IReadOnlyList<MediaStreamInfo> streams)
    {
        if (streams.Count is < 1 or > 256 || streams.Select(x => x.Index).Distinct().Count() != streams.Count)
            throw new ArgumentException("Invalid stream inventory.");
        foreach (var stream in streams) stream.Validate();
        if (!streams.Any(x => x.Kind == MediaStreamKind.Video && x.IsSelectable)) throw new ArgumentException("Selectable video stream required.");
    }
}
public sealed class StreamSelection
{
    private StreamSelection() { }
    public Guid ImportId { get; private set; }
    public Guid AssetId { get; private set; }
    public Guid InventoryVersion { get; private set; }
    public Guid Version { get; private set; }
    public int? VideoIndex { get; private set; }
    public int? AudioIndex { get; private set; }
    public int? SubtitleIndex { get; private set; }
    public DateTimeOffset? SelectedAt { get; private set; }
    public string? SelectedBy { get; private set; }
    public static StreamSelection Create(Guid importId, Guid assetId, Guid inventoryVersion)
    {
        if (importId == Guid.Empty || assetId == Guid.Empty || inventoryVersion == Guid.Empty) throw new ArgumentException("Invalid selection identity.");
        return new() { ImportId = importId, AssetId = assetId, InventoryVersion = inventoryVersion, Version = Guid.NewGuid() };
    }
    public void Select(Guid version, int video, int? audio, int? subtitle, string actor, DateTimeOffset now, IReadOnlyList<MediaStreamInfo> streams)
    {
        if (Version != version) throw new InvalidOperationException("Selection version changed; reload before retrying.");
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256) throw new ArgumentException("Operator is required (maximum 256 characters).");
        Check(video, MediaStreamKind.Video); if (audio is { } a) Check(a, MediaStreamKind.Audio); if (subtitle is { } s) Check(s, MediaStreamKind.Subtitle);
        VideoIndex = video; AudioIndex = audio; SubtitleIndex = subtitle; SelectedBy = actor; SelectedAt = now; Version = Guid.NewGuid();
        void Check(int index, MediaStreamKind kind)
        {
            if (!streams.Any(x => x.Index == index && x.Kind == kind && x.IsSelectable)) throw new ArgumentException("Stream index is missing, incompatible or not selectable.");
        }
    }
}
