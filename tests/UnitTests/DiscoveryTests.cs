using System.Diagnostics;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
using Xunit;
namespace MovieRecap.UnitTests;

public sealed class DiscoveryTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    static FileObservation Observation(bool readable = true) => new("library", "movie.mp4", new string('a', 64), new string('b', 64), 100, readable, readable ? null : "media-file-busy-or-unavailable");
    [Fact]
    public void Stability_requires_repeated_readable_observations_and_versions_do_not_churn()
    {
        var file = Observation(); var candidate = MediaDiscovery.Create(file, Now); var version = candidate.Version;
        candidate.Observe(file, Now.AddSeconds(1), TimeSpan.FromSeconds(2)); Assert.Equal(DiscoveryStatus.Observing, candidate.Status); Assert.Equal(version, candidate.Version);
        candidate.Observe(file, Now.AddSeconds(2), TimeSpan.FromSeconds(2)); Assert.Equal(DiscoveryStatus.Ready, candidate.Status); Assert.NotEqual(version, candidate.Version);
        version = candidate.Version; candidate.Observe(file, Now.AddSeconds(4), TimeSpan.FromSeconds(2)); Assert.Equal(version, candidate.Version);
    }
    [Fact]
    public void Busy_and_missing_files_reset_stability_and_old_observations_are_ignored()
    {
        var file = Observation(); var candidate = MediaDiscovery.Create(file, Now);
        candidate.Observe(Observation(false), Now.AddSeconds(2), TimeSpan.FromSeconds(2));
        candidate.Observe(file, Now.AddSeconds(4), TimeSpan.FromSeconds(2)); Assert.Equal(DiscoveryStatus.Observing, candidate.Status);
        candidate.Observe(file, Now.AddSeconds(6), TimeSpan.FromSeconds(2)); Assert.Equal(DiscoveryStatus.Ready, candidate.Status);
        candidate.Missing(Now.AddSeconds(5)); Assert.Equal(DiscoveryStatus.Ready, candidate.Status);
        candidate.Missing(Now.AddSeconds(7)); Assert.Equal(DiscoveryStatus.Unavailable, candidate.Status);
        candidate.Observe(file, Now.AddSeconds(8), TimeSpan.FromSeconds(2)); Assert.Equal(DiscoveryStatus.Observing, candidate.Status);
    }
    [Fact]
    public void Dismissal_and_submission_are_versioned_and_keep_history()
    {
        var file = Observation(); var candidate = MediaDiscovery.Create(file, Now);
        Assert.Throws<InvalidOperationException>(() => candidate.Submit(candidate.Version, Guid.NewGuid()));
        candidate.Observe(file, Now.AddSeconds(2), TimeSpan.FromSeconds(2)); var approved = candidate.Version;
        Assert.Throws<InvalidOperationException>(() => candidate.Submit(Guid.NewGuid(), Guid.NewGuid()));
        var importId = Guid.NewGuid(); candidate.Submit(approved, importId); candidate.Missing(Now.AddSeconds(3)); candidate.Supersede(Now.AddSeconds(4));
        Assert.Equal(DiscoveryStatus.Submitted, candidate.Status); Assert.Equal(approved, candidate.SubmittedVersion); Assert.Equal(importId, candidate.ImportId); Assert.False(candidate.IsCurrent);
        Assert.Throws<InvalidOperationException>(() => candidate.Dismiss(candidate.Version));
        var dismissed = MediaDiscovery.Create(file, Now); dismissed.Dismiss(dismissed.Version); dismissed.Observe(file, Now.AddSeconds(10), TimeSpan.FromSeconds(2)); Assert.Equal(DiscoveryStatus.Dismissed, dismissed.Status);
    }
    [Fact]
    public async Task Scanner_is_read_only_recursive_and_excludes_unsupported_files()
    {
        using var f = new Folder(); Directory.CreateDirectory(Path.Combine(f.Path, "sub"));
        File.WriteAllBytes(Path.Combine(f.Path, "sub", "movie.MP4"), [1, 2, 3]); File.WriteAllText(Path.Combine(f.Path, "readme.txt"), "unchanged");
        var before = File.GetLastWriteTimeUtc(Path.Combine(f.Path, "sub", "movie.MP4"));
        var scan = await f.Files.ScanAsync("library", default); Assert.True(scan.Complete); var item = Assert.Single(scan.Files);
        Assert.True(item.CanRead); Assert.Equal("sub/movie.MP4", item.RelativePath); Assert.Equal(64, item.PathKey.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(f.Path, "sub", "movie.MP4"))); Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(f.Path, "sub", "movie.MP4")));
        var fingerprint = item.Fingerprint; File.SetLastWriteTimeUtc(Path.Combine(f.Path, "sub", "movie.MP4"), before.AddSeconds(1));
        Assert.NotEqual(fingerprint, f.Files.Observe("library", "sub/movie.MP4").Fingerprint);
    }
    [Fact]
    public async Task Empty_busy_missing_cancelled_and_capped_scans_are_explicit()
    {
        using var f = new Folder(1); var path = Path.Combine(f.Path, "empty.mp4"); File.WriteAllBytes(path, []);
        Assert.False(f.Files.Observe("library", "empty.mp4").CanRead);
        File.WriteAllBytes(path, [1]);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) Assert.False(f.Files.Observe("library", "empty.mp4").CanRead);
        File.WriteAllBytes(Path.Combine(f.Path, "second.mp4"), [2]); var scan = await f.Files.ScanAsync("library", default); Assert.False(scan.Complete); Assert.Equal("discovery-scan-limit", scan.ErrorCode);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Files.ScanAsync("library", cancellation.Token));
        Assert.False((await f.Files.ScanAsync("missing", default)).Complete);
    }
    [Fact]
    public async Task Scanner_does_not_traverse_junctions_or_symlinks()
    {
        using var f = new Folder(); using var outside = new Folder(); File.WriteAllBytes(Path.Combine(outside.Path, "outside.mp4"), [1]);
        var link = Path.Combine(f.Path, "linked");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, outside.Path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(5000);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token); Assert.True(process.ExitCode == 0, await stderr); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        else Directory.CreateSymbolicLink(link, outside.Path);
        try { var scan = await f.Files.ScanAsync("library", default); Assert.True(scan.Complete); Assert.Empty(scan.Files); }
        finally { Directory.Delete(link); }
    }
    [Fact]
    public async Task Approved_fingerprint_is_checked_before_real_probe_or_any_source_write()
    {
        using var f = new Folder(); File.WriteAllBytes(Path.Combine(f.Path, "movie.mp4"), [1, 2, 3]);
        var stamp = f.Files.Observe("library", "movie.mp4").Fingerprint;
        File.WriteAllBytes(Path.Combine(f.Path, "movie.mp4"), [1, 2, 3, 4]);
        Assert.Equal("discovery-source-changed", (await Assert.ThrowsAsync<ImportException>(() => f.Inspector.InspectAsync("library", "movie.mp4", default, stamp))).Code);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(f.Path, "movie.mp4")));
    }
    sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "recap_discovery_" + Guid.NewGuid().ToString("N"));
        public DiscoveryFiles Files { get; }
        public MediaInspector Inspector { get; }
        public Folder(int limit = 10000)
        {
            Directory.CreateDirectory(Path); var media = Options.Create(new MediaOptions { Folders = new() { ["library"] = Path }, StabilityMilliseconds = 100, FfprobePath = System.IO.Path.Combine(Path, "absent-probe.exe") });
            Inspector = new(media, new(media)); Files = new(Inspector, media, Options.Create(new DiscoveryOptions { MaxEntriesPerFolder = limit }), TimeProvider.System);
        }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
