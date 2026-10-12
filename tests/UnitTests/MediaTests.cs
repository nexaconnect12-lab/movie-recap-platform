using System.Diagnostics;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
using Xunit;
namespace MovieRecap.UnitTests;

public sealed class MediaTests
{
    const string Valid = """
        {"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"1.25"},"streams":[
          {"codec_type":"video","codec_name":"mjpeg","width":8,"height":8,"avg_frame_rate":"0/0","disposition":{"attached_pic":1}},
          {"codec_type":"video","codec_name":"h264","width":160,"height":90,"avg_frame_rate":"30000/1001"},
          {"codec_type":"audio","codec_name":"aac","channels":1,"sample_rate":"48000"}]}
        """;
    [Fact]
    public void Parses_realistic_metadata_and_ignores_cover_art()
    {
        var metadata = FfprobeAdapter.Parse(Valid, ".MP4");
        Assert.Equal("h264", metadata.VideoCodec); Assert.Equal(160, metadata.Width); Assert.Equal(90, metadata.Height);
        Assert.Equal(29.97002997, metadata.FrameRate, 6); Assert.Equal(1.25, metadata.DurationSeconds);
        Assert.Equal("aac", metadata.AudioCodec); Assert.Equal(48000, metadata.AudioSampleRate);
    }
    [Theory]
    [InlineData("not json", "ffprobe-invalid-metadata")]
    [InlineData("{\"format\":{\"format_name\":\"avi\",\"duration\":\"1\"},\"streams\":[]}", "media-container-mismatch")]
    [InlineData("{\"format\":{\"format_name\":\"mp4\",\"duration\":\"1\"},\"streams\":[]}", "media-video-stream-required")]
    public void Rejects_invalid_probe_results(string json, string error) => Assert.Equal(error, Assert.Throws<ImportException>(() => FfprobeAdapter.Parse(json, ".mp4")).Code);
    [Theory]
    [InlineData("NaN")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Rejects_invalid_duration(string duration) =>
        Assert.Throws<ImportException>(() => FfprobeAdapter.Parse(Valid.Replace("1.25", duration), ".mp4"));
    [Fact]
    public void Rights_are_explicit_and_authorized_requires_evidence()
    {
        var unknown = new MediaRights("Unknown", AuthorizationStatus.Unknown, "", "", "", "", null);
        unknown.Validate();
        Assert.Throws<ArgumentException>(() => (unknown with { Authorization = AuthorizationStatus.Authorized }).Validate());
        (unknown with { Authorization = AuthorizationStatus.Authorized, LicenseEvidence = "Owned fixture", PermittedUses = "Test", PermittedPlatforms = "Local" }).Validate();
        Assert.Throws<ArgumentException>(() => (unknown with { Authorization = (AuthorizationStatus)20 }).Validate());
    }
    [Fact]
    public void Import_completion_and_fingerprint_invariants()
    {
        var import = MovieImport.Create(Guid.NewGuid(), "key", "library", "movie.mp4", "Owned", "tester", 3, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => import.Complete(Guid.Empty, false, DateTimeOffset.UtcNow));
        import.Complete(Guid.NewGuid(), true, DateTimeOffset.UtcNow);
        Assert.True(import.IsDuplicate);
        Assert.Throws<InvalidOperationException>(() => import.Complete(Guid.NewGuid(), false, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => MediaAsset.Create(new string('z', 64), 1, FfprobeAdapter.Parse(Valid, ".mp4"), DateTimeOffset.UtcNow));
    }
    [Theory]
    [InlineData("../escape.mp4")]
    [InlineData("sub/../../escape.mp4")]
    [InlineData("C:\\outside.mp4")]
    [InlineData("movie.mp4:stream")]
    [InlineData("movie.txt")]
    [InlineData("https://example.org/movie.mp4")]
    public void Rejects_unsafe_or_unsupported_paths(string path)
    {
        using var f = new Folder(); Assert.Throws<ImportException>(() => f.Inspector.ValidateLocation("library", path));
    }
    [Fact]
    public void Rejects_missing_folder_missing_file_and_directory()
    {
        using var f = new Folder();
        Assert.Throws<ImportException>(() => f.Inspector.ValidateLocation("other", "movie.mp4"));
        Assert.Throws<ImportException>(() => f.Inspector.ValidateLocation("library", "missing.mp4"));
        Directory.CreateDirectory(Path.Combine(f.Path, "directory.mp4"));
        Assert.Throws<ImportException>(() => f.Inspector.ValidateLocation("library", "directory.mp4"));
    }
    [Fact]
    public async Task Rejects_links_inside_folders_and_linked_folder_roots()
    {
        using var f = new Folder(); using var target = new Folder();
        File.WriteAllBytes(Path.Combine(target.Path, "movie.mp4"), [1, 2, 3]);
        var link = Path.Combine(f.Path, "linked");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target.Path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await stderr);
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        else Directory.CreateSymbolicLink(link, target.Path);
        try
        {
            Assert.Equal("unsafe-media-link", Assert.Throws<ImportException>(() => f.Inspector.ValidateLocation("library", "linked/movie.mp4")).Code);
            var options = Options.Create(new MediaOptions { Folders = new() { ["linked"] = link } });
            var inspector = new MediaInspector(options, new(options));
            Assert.Equal("unsafe-media-link", Assert.Throws<ImportException>(() => inspector.ValidateLocation("linked", "movie.mp4")).Code);
        }
        finally { Directory.Delete(link); }
    }
    [Fact]
    public async Task Empty_locked_and_cancelled_files_never_probe_or_change_originals()
    {
        using var f = new Folder(); var path = Path.Combine(f.Path, "movie.mp4"); File.WriteAllBytes(path, []);
        Assert.Equal("empty-media-file", (await Assert.ThrowsAsync<ImportException>(() => f.Inspector.InspectAsync("library", "movie.mp4", default))).Code);
        File.WriteAllBytes(path, [1, 2, 3]);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            await Assert.ThrowsAsync<ImportException>(() => f.Inspector.InspectAsync("library", "movie.mp4", default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Inspector.InspectAsync("library", "movie.mp4", cancelled.Token));
        using var duringStability = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Inspector.InspectAsync("library", "movie.mp4", duringStability.Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task Detects_stability_timestamp_change_without_writing_source()
    {
        using var f = new Folder(); var path = Path.Combine(f.Path, "movie.mp4"); File.WriteAllBytes(path, [1, 2, 3]);
        var inspection = f.Inspector.InspectAsync("library", "movie.mp4", default);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal("media-file-changed", (await Assert.ThrowsAsync<ImportException>(() => inspection)).Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task Missing_probe_is_an_explicit_failure()
    {
        using var f = new Folder(); File.WriteAllBytes(Path.Combine(f.Path, "movie.mp4"), [1, 2, 3]);
        Assert.Equal("ffprobe-unavailable", (await Assert.ThrowsAsync<ImportException>(() => f.Inspector.InspectAsync("library", "movie.mp4", default))).Code);
    }
    [Fact]
    public void Direct_jobs_api_cannot_create_orphan_import_jobs() =>
        Assert.False(new CreateJobValidator().Validate(new CreateJob("media.manual-import", "orphan")).IsValid);
    [Fact]
    public void Direct_jobs_api_cannot_use_the_reserved_import_key_namespace() =>
        Assert.False(new CreateJobValidator().Validate(new CreateJob("foundation.database-check", "media-import:reserved")).IsValid);
    [Fact]
    public void Media_has_clean_dependencies()
    {
        var domain = typeof(MediaAsset).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.DoesNotContain(domain, x => x.StartsWith("Microsoft.") || x.StartsWith("MovieRecap.") || x.StartsWith("Npgsql"));
        var application = typeof(ImportService).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.DoesNotContain(application, x => x.Contains("Infrastructure") || x.Contains("EntityFramework") || x.Contains("Worker") || x.Contains("Api"));
    }
    [Theory]
    [InlineData("flood.mp4", "ffprobe-output-too-large")]
    [InlineData("hang.mp4", "ffprobe-timeout")]
    [InlineData("corrupt.mp4", "ffprobe-rejected-media")]
    public async Task Subprocess_output_timeout_and_exit_failures_are_bounded(string name, string code)
    {
        using var f = new Folder(); var path = Path.Combine(f.Path, name); File.WriteAllBytes(path, [1]);
        var adapter = new FfprobeAdapter(Options.Create(new MediaOptions { FfprobePath = FixtureExecutable(), ProbeTimeoutSeconds = 1, MaxProbeOutputCharacters = 1024 }));
        Assert.Equal(code, (await Assert.ThrowsAsync<ImportException>(() => adapter.ProbeAsync(path, default))).Code);
        AssertStopped(path);
    }
    [Fact]
    public async Task Subprocess_cancellation_kills_the_process()
    {
        using var f = new Folder(); var path = Path.Combine(f.Path, "hang.mp4"); File.WriteAllBytes(path, [1]);
        var adapter = new FfprobeAdapter(Options.Create(new MediaOptions { FfprobePath = FixtureExecutable() }));
        using var cancellation = new CancellationTokenSource(); var task = adapter.ProbeAsync(path, cancellation.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(path + ".pid") && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(File.Exists(path + ".pid")); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); AssertStopped(path);
    }
    static void AssertStopped(string path)
    {
        if (!File.Exists(path + ".pid")) return;
        var pid = int.Parse(File.ReadAllText(path + ".pid"));
        try { using var process = Process.GetProcessById(pid); Assert.True(process.WaitForExit(3000)); }
        catch (ArgumentException) { /* process already exited */ }
    }
    static string FixtureExecutable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "MovieRecapPlatform.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var artifacts = Environment.GetEnvironmentVariable("ArtifactsPath");
        var path = artifacts is null ? System.IO.Path.Combine(root.FullName, "tests", "ProbeProcessFixture", "bin", "Debug", "net10.0", "ProbeProcessFixture" + suffix)
            : System.IO.Path.Combine(artifacts, "bin", "ProbeProcessFixture", "debug", "ProbeProcessFixture" + suffix);
        Assert.True(File.Exists(path), "Build the ProbeProcessFixture project before running the tests."); return path;
    }
    sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "recap_media_" + Guid.NewGuid().ToString("N"));
        public MediaInspector Inspector { get; }
        public Folder()
        {
            Directory.CreateDirectory(Path);
            var options = Options.Create(new MediaOptions { Folders = new() { ["library"] = Path }, StabilityMilliseconds = 100, FfprobePath = System.IO.Path.Combine(Path, "missing-ffprobe.exe") });
            Inspector = new(options, new(options));
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
