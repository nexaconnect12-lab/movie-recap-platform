using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
using Xunit;
namespace MovieRecap.UnitTests;

public sealed class StreamTests
{
    const string Json = """
        {"format":{"format_name":"mov,mp4","duration":"1.25"},"streams":[
          {"index":0,"codec_type":"video","codec_name":"mjpeg","width":64,"height":64,"avg_frame_rate":"0/0","disposition":{"attached_pic":1}},
          {"index":2,"codec_type":"video","codec_name":"h264","width":160,"height":90,"avg_frame_rate":"10/1","disposition":{"default":1}},
          {"index":4,"codec_type":"audio","codec_name":"aac","channels":1,"sample_rate":"48000","tags":{"language":"eng"}},
          {"index":6,"codec_type":"subtitle","codec_name":"mov_text","duration":"N/A","disposition":{"forced":1}},
          {"index":9,"codec_type":"data","codec_name":"bin_data"}]}
        """;
    [Fact]
    public void Inventory_preserves_global_indexes_optional_values_flags_and_reported_types()
    {
        var probe = FfprobeAdapter.ParseDetailed(Json, ".mp4"); Assert.Equal(5, probe.Streams.Count);
        Assert.Equal(new[] { 0, 2, 4, 6, 9 }, probe.Streams.Select(x => x.Index)); Assert.False(probe.Streams[0].IsSelectable);
        Assert.True(probe.Streams[1].IsDefault); Assert.Equal("eng", probe.Streams[2].Language);
        Assert.True(probe.Streams[3].IsForced); Assert.Null(probe.Streams[3].DurationSeconds); Assert.Null(probe.Streams[3].Language);
        Assert.Equal("data", probe.Streams[4].ReportedType); Assert.Equal(MediaStreamKind.Other, probe.Streams[4].Kind);
    }
    [Theory]
    [InlineData("\"index\":4", "\"index\":2")]
    [InlineData("\"index\":4", "\"index\":-1")]
    [InlineData("\"duration\":\"N/A\"", "\"duration\":\"NaN\"")]
    [InlineData("\"duration\":\"N/A\"", "\"duration\":\"-1\"")]
    public void Invalid_stream_inventory_is_rejected(string before, string after) =>
        Assert.Throws<ImportException>(() => FfprobeAdapter.ParseDetailed(Json.Replace(before, after), ".mp4"));
    [Fact]
    public void Undetermined_language_remains_unknown()
    {
        var probe = FfprobeAdapter.ParseDetailed(Json.Replace("\"eng\"", "\"und\""), ".mp4"); Assert.Null(probe.Streams.Single(x => x.Index == 4).Language);
    }
    [Fact]
    public void Selection_is_explicit_versioned_and_can_disable_audio_and_subtitles()
    {
        var streams = FfprobeAdapter.ParseDetailed(Json, ".mp4").Streams;
        var selection = StreamSelection.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); Assert.Null(selection.VideoIndex); var version = selection.Version;
        selection.Select(version, 2, 4, 6, "operator", DateTimeOffset.UtcNow, streams);
        Assert.Equal(2, selection.VideoIndex); Assert.Equal(4, selection.AudioIndex); Assert.Equal(6, selection.SubtitleIndex); Assert.NotEqual(version, selection.Version);
        Assert.Throws<InvalidOperationException>(() => selection.Select(version, 2, null, null, "operator", DateTimeOffset.UtcNow, streams));
        selection.Select(selection.Version, 2, null, null, "operator", DateTimeOffset.UtcNow, streams); Assert.Null(selection.AudioIndex); Assert.Null(selection.SubtitleIndex);
    }
    [Theory]
    [InlineData(0, 4, 6)]
    [InlineData(4, 4, 6)]
    [InlineData(2, 6, 6)]
    [InlineData(2, 4, 9)]
    [InlineData(99, 4, 6)]
    public void Cover_art_wrong_types_and_missing_indexes_cannot_be_selected(int video, int audio, int subtitle)
    {
        var selection = StreamSelection.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); var version = selection.Version;
        Assert.Throws<ArgumentException>(() => selection.Select(version, video, audio, subtitle, "operator", DateTimeOffset.UtcNow, FfprobeAdapter.ParseDetailed(Json, ".mp4").Streams));
        Assert.Equal(version, selection.Version); Assert.Null(selection.VideoIndex);
    }
    [Fact]
    public void Generic_jobs_cannot_enqueue_or_impersonate_stream_inspection()
    {
        var validator = new CreateJobValidator(); Assert.False(validator.Validate(new CreateJob("media.stream-inspection", "orphan")).IsValid);
        Assert.False(validator.Validate(new CreateJob("foundation.database-check", "stream-inspection:reserved")).IsValid);
    }
    [Fact]
    public async Task Source_verifier_detects_changed_bytes_even_when_size_and_write_time_are_restored()
    {
        using var f = new Folder(); var stamp = File.GetLastWriteTimeUtc(f.Path);
        File.WriteAllBytes(f.Path, [3, 2, 1]); File.SetLastWriteTimeUtc(f.Path, stamp);
        Assert.Equal("stream-source-content-changed", (await Assert.ThrowsAsync<ImportException>(() => f.Verifier.OpenAsync(f.Source, default))).Code);
        Assert.Equal(new byte[] { 3, 2, 1 }, File.ReadAllBytes(f.Path));
    }
    [Fact]
    public async Task Verified_source_holds_a_read_only_lease_and_cancellation_releases_failed_opens()
    {
        using var f = new Folder(); await using (var verified = await f.Verifier.OpenAsync(f.Source, default))
        {
            await verified.VerifyUnchangedAsync(default);
            if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => { using var writer = new FileStream(f.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verified.VerifyUnchangedAsync(cancelled.Token));
        }
        using var duringOpen = new CancellationTokenSource(20); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Verifier.OpenAsync(f.Source, duringOpen.Token));
        using var writable = new FileStream(f.Path, FileMode.Open, FileAccess.Write, FileShare.None); Assert.Equal(3, writable.Length);
    }
    sealed class Folder : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "recap_stream_unit_" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "movie.mp4");
        public StreamSource Source { get; }
        public MediaSourceVerifier Verifier { get; }
        public Folder()
        {
            Directory.CreateDirectory(root); File.WriteAllBytes(Path, [1, 2, 3]);
            var asset = MediaAsset.Create(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })), 3, new("mp4", 1, "h264", 160, 90, 10, null, null, null), DateTimeOffset.UtcNow);
            var import = MovieImport.Create(Guid.NewGuid(), "key", "library", "movie.mp4", "Owned test", "operator", 1, DateTimeOffset.UtcNow); import.Complete(asset.Id, false, DateTimeOffset.UtcNow); Source = new(import, asset);
            var options = Options.Create(new MediaOptions { Folders = new() { ["library"] = root }, StabilityMilliseconds = 100 }); Verifier = new(new MediaInspector(options, new(options)), options);
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
