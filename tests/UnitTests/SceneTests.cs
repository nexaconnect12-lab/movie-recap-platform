using System.Diagnostics;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
using Xunit;
namespace MovieRecap.UnitTests;

public sealed class SceneTests
{
    static SceneDetection Detection(IReadOnlyList<SceneBoundary>? scenes = null) => new(1, 30, 3, 5, false, "presentation-timestamps", "0.7.1", "19.0.0", "5.0.0", "3.12.14", "content/suppress", 27, 2,
        scenes ?? new[] { new SceneBoundary(0, 10, 0, 1), new SceneBoundary(10, 30, 1, 3) });
    [Fact]
    public void Scene_ranges_are_zero_based_end_exclusive_and_cover_the_actual_timeline()
    {
        Detection().Validate(); var a = SceneAnalysis.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64), 100, Guid.NewGuid(), "key", "operator", 27, 2, 1, DateTimeOffset.UtcNow);
        a.Complete(Detection(), DateTimeOffset.UtcNow); Assert.Equal(30, a.TotalFrames); Assert.Equal(5, a.SourceOffsetSeconds); Assert.Equal("0.7.1", a.SceneDetectVersion);
        Assert.Throws<InvalidOperationException>(() => a.Complete(Detection(), DateTimeOffset.UtcNow));
    }
    [Theory]
    [InlineData(11, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 0.9)]
    [InlineData(10, 1.1)]
    public void Gaps_overlaps_and_inconsistent_times_are_rejected(int frame, double seconds) =>
        Assert.Throws<ArgumentException>(() => Detection(new[] { new SceneBoundary(0, 10, 0, 1), new SceneBoundary(frame, 30, seconds, 3) }).Validate());
    [Fact]
    public void Partial_coverage_nonfinite_times_and_binding_mismatches_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Detection(new[] { new SceneBoundary(0, 20, 0, 2) }).Validate());
        Assert.Throws<ArgumentException>(() => (Detection() with { DurationSeconds = double.NaN }).Validate());
        var a = SceneAnalysis.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64), 100, Guid.NewGuid(), "key", "operator", 27, 2, 1, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => a.Complete(Detection() with { VideoIndex = 0 }, DateTimeOffset.UtcNow)); Assert.Null(a.CompletedAt);
        Assert.Throws<ArgumentException>(() => a.Complete(Detection() with { Threshold = 20 }, DateTimeOffset.UtcNow)); Assert.Null(a.CompletedAt);
    }
    [Fact]
    public void Generic_jobs_cannot_create_or_impersonate_scene_analysis()
    {
        Assert.False(new CreateJobValidator().Validate(new CreateJob("video.scene-analysis", "orphan")).IsValid);
        Assert.False(new CreateJobValidator().Validate(new CreateJob("foundation.database-check", "scene-analysis:reserved")).IsValid);
    }
    [Fact]
    public void Video_module_has_clean_domain_and_application_dependencies()
    {
        Assert.DoesNotContain(typeof(SceneAnalysis).Assembly.GetReferencedAssemblies(), x => x.Name!.StartsWith("Microsoft.") || x.Name.StartsWith("MovieRecap.") || x.Name.StartsWith("Npgsql"));
        Assert.DoesNotContain(typeof(SceneService).Assembly.GetReferencedAssemblies(), x => x.Name!.Contains("Infrastructure") || x.Name.Contains("EntityFramework") || x.Name.EndsWith(".Api") || x.Name.EndsWith(".Worker") || x.Name == "MovieRecap.Media.Domain");
    }
    [Fact]
    public async Task Missing_runner_and_interpreter_are_explicit_failures()
    {
        var missing = new PySceneDetectAdapter(Options.Create(new SceneOptions { RunnerPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".py") }));
        Assert.Equal("scene-runner-unavailable", (await Assert.ThrowsAsync<SceneException>(() => missing.DetectAsync("ignored", Snapshot(), 27, 2, null, default))).Code);
        using var fixture = new ProcessFault(); var unavailable = new PySceneDetectAdapter(Options.Create(new SceneOptions { RunnerPath = fixture.Script, PythonPath = Path.Combine(fixture.Root, "missing.exe") }));
        Assert.Equal("scene-python-unavailable", (await Assert.ThrowsAsync<SceneException>(() => unavailable.DetectAsync("ignored", Snapshot(), 27, 2, null, default))).Code);
    }
    [Theory]
    [InlineData("hang.py", "scene-timeout")]
    [InlineData("flood.py", "scene-output-limit")]
    [InlineData("malformed.py", "scene-invalid-result")]
    public async Task Subprocess_faults_are_bounded_and_never_complete(string mode, string code)
    {
        using var fixture = new ProcessFault(mode);
        var adapter = new PySceneDetectAdapter(Options.Create(new SceneOptions { PythonPath = FixtureExecutable(), RunnerPath = fixture.Script, TimeoutSeconds = 1, MaxOutputCharacters = 1024 }));
        Assert.Equal(code, (await Assert.ThrowsAsync<SceneException>(() => adapter.DetectAsync("ignored", Snapshot(), 27, 2, null, default))).Code);
        AssertStopped(fixture.Script);
    }
    [Fact]
    public async Task Subprocess_cancellation_terminates_the_owned_process()
    {
        using var fixture = new ProcessFault("hang.py"); using var cancellation = new CancellationTokenSource();
        var adapter = new PySceneDetectAdapter(Options.Create(new SceneOptions { PythonPath = FixtureExecutable(), RunnerPath = fixture.Script }));
        var task = adapter.DetectAsync("ignored", Snapshot(), 27, 2, null, cancellation.Token); var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(fixture.Script + ".pid") && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(File.Exists(fixture.Script + ".pid")); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); AssertStopped(fixture.Script);
    }
    static SelectedVideoSnapshot Snapshot() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64), 100, 10);
    static void AssertStopped(string script)
    {
        if (!File.Exists(script + ".pid")) return;
        try { using var process = Process.GetProcessById(int.Parse(File.ReadAllText(script + ".pid"))); Assert.True(process.WaitForExit(3000)); } catch (ArgumentException) { }
    }
    static string FixtureExecutable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "MovieRecapPlatform.slnx"))) root = root.Parent;
        Assert.NotNull(root); var artifacts = Environment.GetEnvironmentVariable("ArtifactsPath"); var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var path = artifacts is null ? Path.Combine(root.FullName, "tests/ProbeProcessFixture/bin/Debug/net10.0", "ProbeProcessFixture" + suffix) : Path.Combine(artifacts, "bin/ProbeProcessFixture/debug", "ProbeProcessFixture" + suffix);
        Assert.True(File.Exists(path)); return path;
    }
    sealed class ProcessFault : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "recap_scene_fault_" + Guid.NewGuid().ToString("N"));
        public string Script { get; }
        public ProcessFault(string mode = "hang.py") { Directory.CreateDirectory(Root); Script = Path.Combine(Root, mode); File.WriteAllText(Script, "test process fault; never Python detection"); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
