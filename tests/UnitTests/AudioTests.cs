using System.Diagnostics;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
using Xunit;
namespace MovieRecap.UnitTests;

public sealed class AudioTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483630L)]
    public void Invalid_sample_counts_cannot_be_published(long samples) => Assert.Throws<ArgumentException>(() => new ExtractedAudio(samples, "ffmpeg version test").Validate());
    [Fact]
    public void Artifact_identity_checksum_size_and_once_only_completion_are_required()
    {
        var artifact = new AudioArtifact(Guid.NewGuid(), new string('a', 64), 48, new(2, "ffmpeg version test")); artifact.Validate();
        Assert.Throws<ArgumentException>(() => (artifact with { SizeBytes = 50 }).Validate()); Assert.Throws<ArgumentException>(() => (artifact with { Sha256 = "invalid" }).Validate());
        var extraction = AudioExtraction.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, new string('a', 64), 100, Guid.NewGuid(), "key", "actor", 3, DateTimeOffset.UtcNow);
        extraction.Complete(artifact, DateTimeOffset.UtcNow); Assert.Equal(ExtractedAudio.Profile, extraction.Profile); Assert.Equal(0.000125, extraction.DurationSeconds);
        Assert.Throws<InvalidOperationException>(() => extraction.Complete(artifact, DateTimeOffset.UtcNow));
        Assert.False(new CreateJobValidator().Validate(new CreateJob("video.audio-extraction", "orphan")).IsValid);
        Assert.False(new CreateJobValidator().Validate(new CreateJob("foundation.database-check", "audio-extraction:reserved")).IsValid);
    }
    [Fact]
    public async Task Artifact_cleanup_skips_active_and_referenced_files_and_preserves_unknown_files()
    {
        using var folder = new Artifacts(); var id = Guid.NewGuid(); var token = Guid.NewGuid();
        var attempt = await folder.Store.BeginAsync(id, token, default);
        await attempt.Output.WriteAsync(PcmWave.Header(4)); await attempt.Output.WriteAsync(new byte[4]);
        var artifact = await attempt.PrepareAsync(new(2, "ffmpeg version test"), default); await attempt.PromoteAsync(artifact, default);
        Assert.Equal(0, await folder.Store.CleanupAsync(id, null, default)); // OS lock protects a still-active attempt.
        await attempt.DisposeAsync(); Assert.Equal(0, await folder.Store.CleanupAsync(id, token, default));
        await using (var read = await folder.Store.OpenAsync(id, artifact, default)) { Assert.Equal(48, read.Length); }
        var dir = Path.Combine(folder.Root, "audio-v1", id.ToString("N")); File.WriteAllText(Path.Combine(dir, "user.txt"), "preserve");
        Assert.Equal(1, await folder.Store.CleanupAsync(id, null, default)); Assert.Equal("preserve", File.ReadAllText(Path.Combine(dir, "user.txt")));
    }
    [Fact]
    public async Task Truncated_pcm_and_changed_artifacts_are_rejected()
    {
        using var folder = new Artifacts(); var id = Guid.NewGuid(); var token = Guid.NewGuid();
        await using (var a = await folder.Store.BeginAsync(id, token, default))
        {
            await a.Output.WriteAsync(PcmWave.Header(4)); await a.Output.WriteAsync(new byte[2]);
            Assert.Equal("audio-artifact-invalid", (await Assert.ThrowsAsync<AudioException>(() => a.PrepareAsync(new(2, "ffmpeg version test"), default))).Code);
        }
        await folder.Store.CleanupAsync(id, null, default); token = Guid.NewGuid(); AudioArtifact artifact;
        await using (var a = await folder.Store.BeginAsync(id, token, default))
        {
            await a.Output.WriteAsync(PcmWave.Header(4)); await a.Output.WriteAsync(new byte[4]); artifact = await a.PrepareAsync(new(2, "ffmpeg version test"), default);
            var tmp = Path.Combine(folder.Root, "audio-v1", id.ToString("N"), token.ToString("N") + ".tmp");
            using (var change = new FileStream(tmp, FileMode.Open, FileAccess.Write)) { change.Position = 44; change.WriteByte(1); }
            Assert.Equal("audio-artifact-content-changed", (await Assert.ThrowsAsync<AudioException>(() => a.PromoteAsync(artifact, default))).Code);
        }
    }
    [Fact]
    public async Task Unowned_directories_overlapping_sources_and_links_are_rejected()
    {
        using var folder = new Artifacts(); var id = Guid.NewGuid(); var dir = Path.Combine(folder.Root, "audio-v1", id.ToString("N")); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "keep.txt"), "user data");
        Assert.Equal("audio-artifact-directory-not-owned", (await Assert.ThrowsAsync<AudioException>(() => folder.Store.BeginAsync(id, Guid.NewGuid(), default))).Code); Assert.True(File.Exists(Path.Combine(dir, "keep.txt")));
        var overlap = new LocalAudioArtifacts(Options.Create(new AudioOptions { Root = folder.Root }), Options.Create(new MediaOptions { Folders = new() { ["source"] = folder.Root } }));
        Assert.Equal("audio-artifact-root-overlaps-media", (await Assert.ThrowsAsync<AudioException>(() => overlap.BeginAsync(Guid.NewGuid(), Guid.NewGuid(), default))).Code);
        var driveRoot = new LocalAudioArtifacts(Options.Create(new AudioOptions { Root = Path.GetPathRoot(folder.Root) }), Options.Create(new MediaOptions { Folders = new() { ["source"] = folder.Root } }));
        Assert.Equal("audio-artifact-root-overlaps-media", (await Assert.ThrowsAsync<AudioException>(() => driveRoot.BeginAsync(Guid.NewGuid(), Guid.NewGuid(), default))).Code);
        var link = Path.Combine(folder.Root, "linked");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, dir }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, dir);
        var linked = new LocalAudioArtifacts(Options.Create(new AudioOptions { Root = link }), Options.Create(new MediaOptions()));
        Assert.Equal("audio-unsafe-artifact-link", (await Assert.ThrowsAsync<AudioException>(() => linked.BeginAsync(Guid.NewGuid(), Guid.NewGuid(), default))).Code); Directory.Delete(link);
    }
    [Theory]
    [InlineData("hang.mp4", "audio-timeout")]
    [InlineData("flood.mp4", "audio-output-limit")]
    [InlineData("corrupt.mp4", "audio-ffmpeg-rejected-source")]
    public async Task Fault_subprocesses_are_bounded_and_never_publish_audio(string name, string code)
    {
        using var folder = new Artifacts(); var source = Path.Combine(folder.Root, name); File.WriteAllText(source, "fault-only fixture");
        var extractor = new FfmpegAudioExtractor(Options.Create(new AudioOptions { FfmpegPath = FixtureExecutable(), TimeoutSeconds = 1, MaxPcmBytes = 1024 })); using var output = new MemoryStream();
        Assert.Equal(code, (await Assert.ThrowsAsync<AudioException>(() => extractor.ExtractAsync(source, Snapshot(), output, null, default))).Code); AssertStopped(source);
    }
    [Fact]
    public async Task Cancellation_kills_the_owned_extraction_process()
    {
        using var folder = new Artifacts(); var source = Path.Combine(folder.Root, "hang.mp4"); File.WriteAllText(source, "fault-only fixture");
        var extractor = new FfmpegAudioExtractor(Options.Create(new AudioOptions { FfmpegPath = FixtureExecutable() })); using var output = new MemoryStream(); using var cts = new CancellationTokenSource();
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
    static SelectedAudioSnapshot Snapshot() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, new string('a', 64), 100);
    sealed class Artifacts : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "recap_audio_unit_" + Guid.NewGuid().ToString("N"));
        public LocalAudioArtifacts Store { get; }
        public Artifacts() { Directory.CreateDirectory(Root); Store = new(Options.Create(new AudioOptions { Root = Root }), Options.Create(new MediaOptions())); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
