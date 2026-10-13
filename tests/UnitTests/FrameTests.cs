using System.Diagnostics;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
using Xunit;
namespace MovieRecap.UnitTests;

public sealed class FrameTests
{
    static readonly FramePlan Plan = new(0, 0, 10, 0, 2);
    static readonly byte[] BluePng = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-frame-blue.png"));
    [Fact]
    public void Sampling_uses_decoded_quantiles_and_never_selects_an_exclusive_end_or_duplicate_frame()
    {
        var id = Guid.NewGuid(); var plans = FrameSampling.Plan([new(id, 0, 0, 1, 0, 0.1), new(id, 1, 1, 21, 0.1, 2.1)], 3);
        Assert.Equal(new[] { 0, 4, 11, 17 }, plans.Select(x => x.FrameIndex)); Assert.Equal(new[] { 0, 1, 2, 3 }, plans.Select(x => x.Ordinal));
        Assert.Throws<ArgumentException>(() => FrameSampling.Plan(Enumerable.Range(0, 301).Select(x => new MovieScene(id, x, x, x + 1, x, x + 1)).ToArray(), 1));
        Assert.Throws<ArgumentException>(() => FrameSampling.Plan([new(id, 0, 0, 10, 0, 1), new(id, 1, 11, 20, 1, 2)], 1));
    }
    [Theory]
    [InlineData(6, 1)]
    [InlineData(5, 0)]
    public void Actual_source_timestamp_maps_through_offset_into_scene(double source, double normalized)
    {
        new SampledFrame(0, source, normalized, new string('a', 64), 100).Validate(Plan, 5);
    }
    [Fact]
    public void Timeline_mismatches_partial_results_and_repeated_completion_are_rejected()
    {
        var result = new FrameResult(Guid.NewGuid(), "ffmpeg version test", [new(0, 6, 1, new string('a', 64), 100)]); result.Validate([Plan], 5);
        Assert.Throws<ArgumentException>(() => new SampledFrame(0, 7, 2, new string('a', 64), 100).Validate(Plan, 5));
        Assert.Throws<ArgumentException>(() => new SampledFrame(0, 6, 0.5, new string('a', 64), 100).Validate(Plan, 5));
        Assert.Throws<ArgumentException>(() => (result with { Frames = [] }).Validate([Plan], 5));
        var s = FrameSampling.Create(Guid.NewGuid(), Guid.NewGuid(), "key", "actor", 1, 1, DateTimeOffset.UtcNow); s.Complete(result, [Plan], 5, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => s.Complete(result, [Plan], 5, DateTimeOffset.UtcNow));
        Assert.False(new CreateJobValidator().Validate(new CreateJob("video.frame-sampling", "orphan")).IsValid);
        Assert.False(new CreateJobValidator().Validate(new CreateJob("foundation.database-check", "frame-sampling:reserved")).IsValid);
    }
    [Fact]
    public void Full_png_validation_rejects_corruption_truncation_and_wrong_profile()
    {
        var pixels = PngFrames.DecodeRgb(BluePng); var center = (90 * 320 + 160) * 3; Assert.True(pixels[center + 2] > 200); Assert.True(pixels[center] < 20);
        var corrupt = BluePng.ToArray(); corrupt[^5] ^= 1; Assert.Throws<InvalidDataException>(() => PngFrames.DecodeRgb(corrupt));
        Assert.Throws<InvalidDataException>(() => PngFrames.DecodeRgb(BluePng.AsSpan(0, BluePng.Length - 1)));
        var dimensions = BluePng.ToArray(); dimensions[19] ^= 1; Assert.Throws<InvalidDataException>(() => PngFrames.DecodeRgb(dimensions));
    }
    [Fact]
    public async Task Batch_cleanup_preserves_active_committed_and_unknown_files_but_removes_orphan_images()
    {
        using var folder = new Folder(); var id = Guid.NewGuid(); var token = Guid.NewGuid();
        var attempt = await folder.Store.BeginAsync(id, token, default); await attempt.WriteAsync(0, BluePng, default);
        var result = await attempt.PrepareAsync("ffmpeg version test", [6.0], [Plan], 5, default); await attempt.PromoteAsync(result, default);
        Assert.Equal(0, await folder.Store.CleanupAsync(id, null, default)); await attempt.DisposeAsync(); Assert.Equal(0, await folder.Store.CleanupAsync(id, token, default));
        await using (var read = await folder.Store.OpenAsync(id, token, result.Frames[0], default)) { Assert.Equal(BluePng.Length, read.Length); }
        var directory = Path.Combine(folder.Root, "frames-v1", id.ToString("N"), token.ToString("N") + ".ready"); File.WriteAllText(Path.Combine(directory, "user.txt"), "preserve");
        Assert.Equal(1, await folder.Store.CleanupAsync(id, null, default)); Assert.Equal("preserve", File.ReadAllText(Path.Combine(directory, "user.txt"))); Assert.True(Directory.Exists(directory));
    }
    [Fact]
    public async Task Artifact_root_ownership_links_and_integrity_match_the_shared_lifecycle()
    {
        using var folder = new Folder(); var id = Guid.NewGuid(); var token = Guid.NewGuid(); var directory = Path.Combine(folder.Root, "frames-v1", id.ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "user.txt"), "keep"); Assert.Equal("frame-artifact-directory-not-owned", (await Assert.ThrowsAsync<FrameException>(() => folder.Store.BeginAsync(id, token, default))).Code);
        var overlapping = new LocalFrameArtifacts(Options.Create(new FrameOptions { Root = folder.Root }), Options.Create(new MediaOptions { Folders = new() { ["source"] = folder.Root } }));
        Assert.Equal("frame-artifact-root-overlaps-media", (await Assert.ThrowsAsync<FrameException>(() => overlapping.BeginAsync(Guid.NewGuid(), token, default))).Code);
        id = Guid.NewGuid(); await using var attempt = await folder.Store.BeginAsync(id, token, default); await attempt.WriteAsync(0, BluePng, default);
        var result = await attempt.PrepareAsync("ffmpeg version test", [1.0], [Plan], 0, default);
        var file = Path.Combine(folder.Root, "frames-v1", id.ToString("N"), token.ToString("N") + ".tmp", "0000.png"); File.WriteAllBytes(file, BluePng[..^1]);
        await Assert.ThrowsAsync<FrameException>(() => attempt.PromoteAsync(result, default));
    }
    [Theory]
    [InlineData("frames-hang.mp4", "frame-timeout")]
    [InlineData("frames-flood.mp4", "frame-image-limit")]
    [InlineData("frames-malformed.mp4", "frame-invalid-png-stream")]
    [InlineData("frames-corrupt.mp4", "frame-ffmpeg-rejected-source")]
    public async Task Tool_faults_cannot_complete_a_batch(string name, string code)
    {
        using var folder = new Folder(); var source = Path.Combine(folder.Root, name); File.WriteAllText(source, "fault-only test input");
        await using var attempt = await folder.Store.BeginAsync(Guid.NewGuid(), Guid.NewGuid(), default);
        var extractor = new FfmpegFrameExtractor(Options.Create(new FrameOptions { FfmpegPath = FixtureExecutable(), TimeoutSeconds = 1 }));
        Assert.Equal(code, (await Assert.ThrowsAsync<FrameException>(() => extractor.ExtractAsync(source, Snapshot(), [Plan], 0, attempt, null, default))).Code); AssertStopped(source);
    }
    [Fact]
    public async Task Cancellation_terminates_the_owned_tool_and_releases_attempt_lock()
    {
        using var folder = new Folder(); var source = Path.Combine(folder.Root, "frames-hang.mp4"); File.WriteAllText(source, "fault-only input");
        var id = Guid.NewGuid(); using var cancellation = new CancellationTokenSource(); var extractor = new FfmpegFrameExtractor(Options.Create(new FrameOptions { FfmpegPath = FixtureExecutable() }));
        await using (var attempt = await folder.Store.BeginAsync(id, Guid.NewGuid(), default))
        {
            var task = extractor.ExtractAsync(source, Snapshot(), [Plan], 0, attempt, null, cancellation.Token); var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(source + ".pid") && DateTime.UtcNow < deadline) await Task.Delay(20); Assert.True(File.Exists(source + ".pid")); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); AssertStopped(source);
        }
        Assert.Equal(0, await folder.Store.CleanupAsync(id, null, default));
    }
    static SelectedVideoSnapshot Snapshot() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64), 100, 10);
    static void AssertStopped(string source) { if (!File.Exists(source + ".pid")) return; try { using var p = Process.GetProcessById(int.Parse(File.ReadAllText(source + ".pid"))); Assert.True(p.WaitForExit(3000)); } catch (ArgumentException) { } }
    static string FixtureExecutable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "MovieRecapPlatform.slnx"))) root = root.Parent;
        var artifacts = Environment.GetEnvironmentVariable("ArtifactsPath"); var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        return artifacts is null ? Path.Combine(root!.FullName, "tests/ProbeProcessFixture/bin/Debug/net10.0", "ProbeProcessFixture" + suffix) : Path.Combine(artifacts, "bin/ProbeProcessFixture/debug", "ProbeProcessFixture" + suffix);
    }
    sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "recap_frame_unit_" + Guid.NewGuid().ToString("N")); public LocalFrameArtifacts Store { get; }
        public Folder() { Directory.CreateDirectory(Root); Store = new(Options.Create(new FrameOptions { Root = Root }), Options.Create(new MediaOptions())); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
