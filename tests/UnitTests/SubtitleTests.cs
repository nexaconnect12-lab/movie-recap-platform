using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
using Xunit;
namespace MovieRecap.UnitTests;

public sealed class SubtitleTests
{
    static readonly SubtitleCueData Cue = new(0, 5000, 6000, "မြန်မာ caption\nSecond line.");
    [Fact]
    public void Utf8_multiline_offset_and_overlapping_cues_roundtrip_without_translation_or_timeline_rebasing()
    {
        SubtitleCueData[] cues = [Cue, new(1, 5500, 7000, "Overlapping cue")];
        Assert.Equal(cues, SubRip.Parse(SubRip.Encode(cues))); Assert.Contains("00:00:05,000", Encoding.UTF8.GetString(SubRip.Encode(cues)));
        Assert.Equal(cues, SubRip.Parse(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(SubRip.Encode(cues)).Replace("\n", "\r\n"))).ToArray()));
    }
    [Theory]
    [InlineData("1\n00:00:01,000 --> 00:00:00,000\nBackwards\n\n")]
    [InlineData("1\n00:00:00,000 --> 00:00:00,000\nEmpty duration\n\n")]
    [InlineData("2\n00:00:00,000 --> 00:00:01,000\nWrong ordinal\n\n")]
    [InlineData("1\n00:60:00,000 --> 01:00:01,000\nBad minute\n\n")]
    [InlineData("1\n-00:00:01,000 --> 00:00:01,000\nNegative\n\n")]
    [InlineData("1\n00:00:00,000 --> 00:00:01,000\n\n")]
    public void Malformed_cues_are_explicit_failures(string text) => Assert.Equal("subtitle-invalid-srt", Assert.Throws<SubtitleException>(() => SubRip.Parse(Encoding.UTF8.GetBytes(text))).Code);
    [Fact]
    public void Invalid_utf8_controls_long_text_and_out_of_order_cues_are_rejected()
    {
        Assert.Throws<SubtitleException>(() => SubRip.Parse([0xff, 0xfe, 0]));
        Assert.Throws<ArgumentException>(() => (Cue with { Text = "caption\0" }).Validate());
        Assert.Throws<ArgumentException>(() => (Cue with { Text = new string('x', 4097) }).Validate());
        Assert.Throws<ArgumentException>(() => new ExtractedSubtitle([Cue, new(1, 1000, 2000, "Earlier")], "ffmpeg").Validate());
        Assert.Throws<ArgumentException>(() => new ExtractedSubtitle([], "ffmpeg").Validate());
    }
    [Fact]
    public void Selected_subtitle_requires_an_artifact_and_changes_preparation_fingerprint()
    {
        var binding = new PreparationBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, null, null, new string('a', 64), 100, Guid.NewGuid(), null, Guid.NewGuid());
        Assert.True(binding.IsReady); var selected = binding with { SubtitleIndex = 5 }; Assert.False(selected.IsReady); Assert.NotEqual(binding.Hash, selected.Hash);
        var legacy = selected with { SchemaVersion = 1 }; Assert.True(legacy.IsReady); Assert.NotEqual(legacy.Hash, selected.Hash); Assert.Equal(binding.Hash, (binding with { SchemaVersion = 1 }).Hash);
        selected = selected with { SubtitleId = Guid.NewGuid() }; Assert.True(selected.IsReady); Assert.NotEqual(selected.Hash, (selected with { SubtitleId = Guid.NewGuid() }).Hash);
        Assert.False(new CreateJobValidator().Validate(new CreateJob("foundation.database-check", "subtitle-extraction:reserved")).IsValid);
    }
    [Fact]
    public async Task Artifact_promotion_rechecks_cues_hash_and_preserves_referenced_or_active_files()
    {
        using var folder = new Folder(); var id = Guid.NewGuid(); var token = Guid.NewGuid(); SubtitleArtifact artifact;
        await using (var attempt = await folder.Store.BeginAsync(id, token, default))
        {
            await attempt.Output.WriteAsync(SubRip.Encode([Cue])); artifact = await attempt.PrepareAsync(new([Cue], "ffmpeg version test"), default);
            Assert.Equal(0, await folder.Store.CleanupAsync(id, null, default)); await attempt.PromoteAsync(artifact, default);
        }
        await using (var stream = await folder.Store.OpenAsync(id, artifact, default)) Assert.Equal(artifact.SizeBytes, stream.Length);
        Assert.Equal(0, await folder.Store.CleanupAsync(id, token, default)); Assert.Equal(1, await folder.Store.CleanupAsync(id, null, default));
        token = Guid.NewGuid();
        await using var changed = await folder.Store.BeginAsync(id, token, default); await changed.Output.WriteAsync(SubRip.Encode([Cue])); artifact = await changed.PrepareAsync(new([Cue], "ffmpeg version test"), default);
        var tmp = Path.Combine(folder.Root, "subtitle-v1", id.ToString("N"), token.ToString("N") + ".tmp"); File.WriteAllBytes(tmp, SubRip.Encode([Cue with { Text = "Changed text" }]));
        await Assert.ThrowsAsync<SubtitleException>(() => changed.PromoteAsync(artifact, default));
    }
    [Fact]
    public async Task Unowned_artifact_paths_and_overlapping_media_are_preserved_and_rejected()
    {
        using var folder = new Folder(); var id = Guid.NewGuid(); var dir = Path.Combine(folder.Root, "subtitle-v1", id.ToString("N")); Directory.CreateDirectory(dir); var file = Path.Combine(dir, "keep.txt"); File.WriteAllText(file, "original");
        await Assert.ThrowsAsync<SubtitleException>(() => folder.Store.BeginAsync(id, Guid.NewGuid(), default)); Assert.Equal("original", File.ReadAllText(file));
        var overlap = new LocalSubtitleArtifacts(Options.Create(new SubtitleOptions { Root = folder.Root }), Options.Create(new MediaOptions { Folders = new() { ["library"] = folder.Root } }));
        Assert.Equal("subtitle-artifact-root-overlaps-media", (await Assert.ThrowsAsync<SubtitleException>(() => overlap.BeginAsync(Guid.NewGuid(), Guid.NewGuid(), default))).Code);
    }
    [Theory]
    [InlineData("hang.mp4", "subtitle-timeout")]
    [InlineData("flood.mp4", "subtitle-output-limit")]
    [InlineData("corrupt.mp4", "subtitle-ffmpeg-rejected-source")]
    public async Task Fault_subprocesses_are_bounded_and_never_publish_subtitle(string name, string code)
    {
        using var folder = new Folder(); var source = Path.Combine(folder.Root, name); File.WriteAllText(source, "fault-only fixture");
        var extractor = new FfmpegSubtitleExtractor(Options.Create(new SubtitleOptions { FfmpegPath = FixtureExecutable(), TimeoutSeconds = 1, MaxBytes = 1024 })); using var output = new MemoryStream();
        Assert.Equal(code, (await Assert.ThrowsAsync<SubtitleException>(() => extractor.ExtractAsync(source, Snapshot(), output, null, default))).Code); AssertStopped(source);
    }
    [Fact]
    public async Task Cancellation_kills_the_owned_extraction_process()
    {
        using var folder = new Folder(); var source = Path.Combine(folder.Root, "hang.mp4"); File.WriteAllText(source, "fault-only fixture");
        var extractor = new FfmpegSubtitleExtractor(Options.Create(new SubtitleOptions { FfmpegPath = FixtureExecutable() })); using var output = new MemoryStream(); using var cts = new CancellationTokenSource();
        var task = extractor.ExtractAsync(source, Snapshot(), output, null, cts.Token); var until = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(source + ".pid") && DateTime.UtcNow < until) await Task.Delay(20); Assert.True(File.Exists(source + ".pid")); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); AssertStopped(source);
    }
    static void AssertStopped(string source)
    {
        if (!File.Exists(source + ".pid")) return;
        try { using var p = Process.GetProcessById(int.Parse(File.ReadAllText(source + ".pid"))); Assert.True(p.WaitForExit(3000)); } catch (ArgumentException) { }
    }
    static string FixtureExecutable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "MovieRecapPlatform.slnx"))) root = root.Parent;
        var artifacts = Environment.GetEnvironmentVariable("ArtifactsPath"); var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        return artifacts is null ? Path.Combine(root!.FullName, "tests/ProbeProcessFixture/bin/Debug/net10.0", "ProbeProcessFixture" + suffix) : Path.Combine(artifacts, "bin/ProbeProcessFixture/debug", "ProbeProcessFixture" + suffix);
    }
    static SelectedSubtitleSnapshot Snapshot() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, new string('a', 64), 100, "subrip");
    sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "recap_subtitle_unit_" + Guid.NewGuid().ToString("N"));
        public LocalSubtitleArtifacts Store { get; }
        public Folder() { Directory.CreateDirectory(Root); Store = new(Options.Create(new SubtitleOptions { Root = Root }), Options.Create(new MediaOptions())); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
