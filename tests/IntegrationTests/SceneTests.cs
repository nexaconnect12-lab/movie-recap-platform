using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Jobs.Domain;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
using Xunit;
namespace MovieRecap.IntegrationTests;

public sealed class SceneFactAttribute : FactAttribute
{
    public SceneFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_SCENE_PYTHON"))) Skip = "Set TEST_SCENE_PYTHON to a real pinned PySceneDetect/PyAV environment; scene detection is never emulated."; }
}
public sealed class PostgresSceneFactAttribute : FactAttribute
{
    public PostgresSceneFactAttribute()
    {
        if (new[] { "TEST_POSTGRES", "TEST_FFPROBE", "TEST_SCENE_PYTHON" }.Any(x => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(x))))
            Skip = "Set TEST_POSTGRES, TEST_FFPROBE and TEST_SCENE_PYTHON for real scene/database tests.";
    }
}
public sealed class SceneTests
{
    static readonly MediaRights Rights = new("Owned fixture", AuthorizationStatus.Authorized, "Synthetic fixture", "Local tests", "Local", "Fixture", null);
    static SelectedVideoAccess Media(DatabaseFixture f, Folder folder) => new(new PostgresStreamStore(f.Factory, f.Clock), folder.Verifier);
    static PostgresSceneStore Store(DatabaseFixture f, Folder folder) => new(f.Factory, Media(f, folder), f.Clock);
    static SceneService Service(DatabaseFixture f, Folder folder) => new(Store(f, folder), Media(f, folder));
    static PySceneDetectAdapter Detector(int maxFrames = 2_000_000) => new(Options.Create(new SceneOptions { PythonPath = Environment.GetEnvironmentVariable("TEST_SCENE_PYTHON")!, MaxFrames = maxFrames }));
    static SceneExecutor Executor(DatabaseFixture f, Folder folder) => new(Store(f, folder), Media(f, folder), Detector());
    static RequestScenes Request(SelectedVideoSnapshot source, string key = "scenes") => new(key, source.SelectionVersion, "operator", 27, 2, 3);
    static async Task<SelectedVideoSnapshot> ImportAndSelect(DatabaseFixture f, Folder folder, string name = "owned-scenes-multi.mkv", int video = 1)
    {
        folder.Copy(name); var store = new PostgresImportStore(f.Factory, f.Clock);
        var imported = await new ImportService(store, folder.Inspector).CreateAsync(new(name, "library", name, "Owned source", "operator", Rights, 1), default);
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await new ImportExecutor(store, folder.Inspector).ExecuteAsync(lease, default);
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var view = (await streams.GetAsync(imported.Import.Id, default))!;
        await new StreamService(streams, folder.Verifier).SelectAsync(imported.Import.Id, new(view.Selection!.Version, video, null, null, "operator"), default);
        return (await Media(f, folder).GetAsync(imported.Import.Id, default))!;
    }
    [SceneFact]
    public async Task Real_detector_handles_nonprimary_single_scene_vfr_and_shifted_pts_without_modifying_sources()
    {
        using var folder = new Folder(); folder.Copy("owned-scenes-multi.mkv"); folder.Copy("owned-scenes-vfr.mkv"); folder.Copy("owned-scenes-offset.mkv");
        var snapshot = new SelectedVideoSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64), 100, 10);
        var path = Path.Combine(folder.Root, "owned-scenes-multi.mkv"); var bytes = File.ReadAllBytes(path); var stamp = File.GetLastWriteTimeUtc(path); var progress = new List<int>();
        var cut = await Detector().DetectAsync(path, snapshot, 27, 2, progress.Add, default); Assert.Equal(60, cut.TotalFrames); Assert.Equal(new[] { 20, 40, 60 }, cut.Scenes.Select(x => x.EndFrame)); Assert.Equal(new[] { 2.0, 4.0, 6.0 }, cut.Scenes.Select(x => x.EndSeconds));
        Assert.Equal("0.7.1", cut.SceneDetectVersion); Assert.Equal("19.0.0", cut.PyAvVersion); Assert.Equal(progress.OrderBy(x => x), progress);
        var single = await Detector().DetectAsync(path, snapshot with { VideoIndex = 0 }, 27, 2, null, default); Assert.Single(single.Scenes); Assert.Equal(6, single.DurationSeconds);
        var vfr = await Detector().DetectAsync(Path.Combine(folder.Root, "owned-scenes-vfr.mkv"), snapshot with { VideoIndex = 0 }, 27, 2, null, default);
        Assert.True(vfr.VariableFrameRate); Assert.Equal(20, vfr.TotalFrames); Assert.Equal(1, vfr.Scenes[0].EndSeconds); Assert.Equal(2.9, vfr.DurationSeconds, 6); Assert.NotEqual(vfr.TotalFrames / 10.0, vfr.DurationSeconds);
        var shifted = await Detector().DetectAsync(Path.Combine(folder.Root, "owned-scenes-offset.mkv"), snapshot with { VideoIndex = 0 }, 27, 2, null, default);
        Assert.Equal(5, shifted.SourceOffsetSeconds); Assert.Equal(0, shifted.Scenes[0].StartSeconds); Assert.Equal(2, shifted.DurationSeconds);
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }
    [SceneFact]
    public async Task Real_resource_limit_and_unavailable_stream_fail_without_success()
    {
        using var folder = new Folder(); folder.Copy("owned-scenes-multi.mkv");
        var source = new SelectedVideoSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64), 100, 10);
        Assert.Equal("scene-resource-limit", (await Assert.ThrowsAsync<SceneException>(() => Detector(3).DetectAsync(Path.Combine(folder.Root, "owned-scenes-multi.mkv"), source, 27, 2, null, default))).Code);
        Assert.Equal("scene-video-stream-unavailable", (await Assert.ThrowsAsync<SceneException>(() => Detector().DetectAsync(Path.Combine(folder.Root, "owned-scenes-multi.mkv"), source with { VideoIndex = 99 }, 27, 2, null, default))).Code);
    }
    [PostgresSceneFact]
    public async Task Concurrent_requests_create_one_bound_job_and_real_ranges_commit_with_success_audit()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Service(f, folder).CreateAsync(source.ImportId, Request(source), default)));
        var analysis = results[0]!.Analysis; Assert.Single(results.Select(x => x!.Analysis.Id).Distinct()); Assert.Single(results.Select(x => x!.Job.Id).Distinct());
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).CreateAsync(source.ImportId, Request(source) with { Threshold = 20 }, default));
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var view = (await Store(f, folder).GetAsync(analysis.Id, default))!; Assert.Equal(JobStatus.Succeeded, view.Job.Status); Assert.Equal(3, view.Scenes.Count); Assert.True(view.IsCurrentSelection);
        Assert.Equal(source.SelectionVersion, view.Analysis.SelectionVersion); Assert.Equal(source.Sha256, view.Analysis.SourceSha256); Assert.Equal(1, view.Analysis.VideoIndex);
        Assert.Equal(new[] { "created", "started", "succeeded" }, (await f.Store.AuditAsync(lease.JobId, default)).Select(x => x.Action));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(1, await db.SceneAnalyses.CountAsync()); Assert.Equal(3, await db.MovieScenes.CountAsync()); Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details);
    }
    [PostgresSceneFact]
    public async Task Changed_selection_before_or_during_execution_never_commits_stale_ranges()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var created = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        var detection = await Detector().DetectAsync(Path.Combine(folder.Root, "owned-scenes-multi.mkv"), source, 27, 2, null, default);
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var selection = (await streams.GetAsync(source.ImportId, default))!.Selection!;
        await new StreamService(streams, folder.Verifier).SelectAsync(source.ImportId, new(selection.Version, 0, null, null, "changed"), default);
        await Assert.ThrowsAsync<ImportConflictException>(() => Store(f, folder).CompleteAsync(lease, detection, source, default));
        Assert.Equal("scene-selection-changed", (await Assert.ThrowsAsync<SceneException>(() => Executor(f, folder).ExecuteAsync(lease, default))).Code);
        var view = (await Store(f, folder).GetAsync(created.Analysis.Id, default))!; Assert.False(view.IsCurrentSelection); Assert.Null(view.Analysis.CompletedAt); Assert.Empty(view.Scenes); Assert.Equal(JobStatus.Running, view.Job.Status);
        Assert.DoesNotContain(await f.Store.AuditAsync(lease.JobId, default), x => x.Action == "succeeded");
        Assert.NotNull(await Service(f, folder).CreateAsync(source.ImportId, Request(source), default)); // Original request replays, rather than being rebound.
    }
    [PostgresSceneFact]
    public async Task Historical_success_becomes_noncurrent_after_a_selection_change()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var created = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var selection = (await streams.GetAsync(source.ImportId, default))!.Selection!;
        await new StreamService(streams, folder.Verifier).SelectAsync(source.ImportId, new(selection.Version, 0, null, null, "changed"), default);
        var old = (await Store(f, folder).GetAsync(created.Analysis.Id, default))!; Assert.Equal(JobStatus.Succeeded, old.Job.Status); Assert.False(old.IsCurrentSelection); Assert.Equal(3, old.Scenes.Count);
        var current = (await Media(f, folder).GetAsync(source.ImportId, default))!; var next = (await Service(f, folder).CreateAsync(source.ImportId, Request(current, "new-selection"), default))!;
        lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default); Assert.Single((await Store(f, folder).GetAsync(next.Analysis.Id, default))!.Scenes);
    }
    [PostgresSceneFact]
    public async Task Pause_cancel_expiry_recovery_and_cancellation_fence_scene_writes()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var analysis = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var stale = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        var detection = await Detector().DetectAsync(Path.Combine(folder.Root, "owned-scenes-multi.mkv"), source, 27, 2, null, default);
        var job = (await f.Store.GetAsync(stale.JobId, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Pause, job.Version, default);
        await Assert.ThrowsAsync<JobConflictException>(() => Store(f, folder).CompleteAsync(stale, detection, source, default));
        job = (await f.Store.GetAsync(job.Id, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Resume, job.Version, default);
        var expired = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!; f.Clock.Advance(TimeSpan.FromSeconds(16));
        await Assert.ThrowsAsync<JobConflictException>(() => Store(f, folder).CompleteAsync(expired, detection, source, default));
        Assert.Null(await f.Store.ClaimAsync("recover", TimeSpan.FromSeconds(15), default)); f.Clock.Advance(TimeSpan.FromSeconds(10));
        var current = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(f, folder).CompleteAsync(current, detection, source, cancelled.Token));
        job = (await f.Store.GetAsync(job.Id, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Cancel, job.Version, default);
        await Assert.ThrowsAsync<JobConflictException>(() => Store(f, folder).CompleteAsync(current, detection, source, default));
        Assert.Empty((await Store(f, folder).GetAsync(analysis.Analysis.Id, default))!.Scenes);
        await Service(f, folder).CreateAsync(source.ImportId, Request(source, "recovered"), default); current = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await Executor(f, folder).ExecuteAsync(current, default); Assert.Equal(JobStatus.Succeeded, (await f.Store.GetAsync(current.JobId, default))!.Status);
    }
    [PostgresSceneFact]
    public async Task Changed_source_and_persistence_faults_leave_no_partial_results()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var analysis = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        var path = Path.Combine(folder.Root, "owned-scenes-multi.mkv"); var original = File.ReadAllBytes(path); var stamp = File.GetLastWriteTimeUtc(path); var changed = original.ToArray(); changed[0] ^= 1;
        File.WriteAllBytes(path, changed); File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal("stream-source-content-changed", (await Assert.ThrowsAsync<ImportException>(() => Executor(f, folder).ExecuteAsync(lease, default))).Code);
        File.WriteAllBytes(path, original); File.SetLastWriteTimeUtc(path, stamp);
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION video.reject_scene() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'intentional test fault'; END; $$;
            CREATE TRIGGER reject_scene BEFORE INSERT ON video."MovieScenes" FOR EACH ROW EXECUTE FUNCTION video.reject_scene();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Executor(f, folder).ExecuteAsync(lease, default));
        Assert.Empty((await Store(f, folder).GetAsync(analysis.Analysis.Id, default))!.Scenes); Assert.Null((await Store(f, folder).GetAsync(analysis.Analysis.Id, default))!.Analysis.CompletedAt);
        Assert.Equal(JobStatus.Running, (await f.Store.GetAsync(lease.JobId, default))!.Status); Assert.DoesNotContain(await f.Store.AuditAsync(lease.JobId, default), x => x.Action == "succeeded");
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_scene ON video.\"MovieScenes\"; DROP FUNCTION video.reject_scene();");
        await Executor(f, folder).ExecuteAsync(lease, default); Assert.Equal(3, (await Store(f, folder).GetAsync(analysis.Analysis.Id, default))!.Scenes.Count);
    }
    [PostgresSceneFact]
    public async Task Additive_upgrade_preserves_import_stream_choices_and_creates_no_invented_scenes()
    {
        await using var f = new DatabaseFixture(); await f.Initialize("20261012203152_MediaStreamSelection"); using var folder = new Folder();
        // Before the new schema exists, populate the prior schema from actual file/probe data.
        folder.Copy("owned-scenes-multi.mkv"); var inspected = await folder.Inspector.InspectAsync("library", "owned-scenes-multi.mkv", default);
        var job = await f.Store.CreateAsync(new("media.manual-import", "legacy-scenes", 1), default); var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await f.Store.CompleteAsync(lease, default);
        var asset = MediaAsset.Create(inspected.Sha256, inspected.SizeBytes, inspected.Metadata, f.Clock.GetUtcNow()); var import = MovieImport.Create(job.Id, "legacy-scenes", "library", "owned-scenes-multi.mkv", "Owned source", "operator", 1, f.Clock.GetUtcNow()); import.Complete(asset.Id, false, f.Clock.GetUtcNow());
        var inventory = new StreamInventoryRecord { AssetId = asset.Id, InspectedAt = f.Clock.GetUtcNow() }; var selection = StreamSelection.Create(import.Id, asset.Id, inventory.Version); selection.Select(selection.Version, 1, null, null, "operator", f.Clock.GetUtcNow(), inspected.Streams!);
        await using var db = await f.Factory.CreateDbContextAsync(); db.Assets.Add(asset); db.Imports.Add(import); db.ImportRights.Add(new() { ImportId = import.Id, Details = Rights }); db.StreamInventories.Add(inventory);
        foreach (var s in inspected.Streams!) db.MediaStreams.Add(new() { AssetId = asset.Id, Index = s.Index, Details = s }); db.StreamSelections.Add(selection); await db.SaveChangesAsync();
        await db.Database.MigrateAsync(); await db.Database.MigrateAsync(); Assert.Empty(await db.SceneAnalyses.ToListAsync()); Assert.Empty(await db.MovieScenes.ToListAsync());
        var snapshot = (await Media(f, folder).GetAsync(import.Id, default))!; Assert.Equal(selection.Version, snapshot.SelectionVersion); Assert.Equal(asset.Sha256, snapshot.Sha256); Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details);
        var analysis = (await Service(f, folder).CreateAsync(import.Id, Request(snapshot), default))!; lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        Assert.Equal(3, (await Store(f, folder).GetAsync(analysis.Analysis.Id, default))!.Scenes.Count);
    }
    [PostgresSceneFact]
    public async Task Api_requires_selected_current_video_valid_settings_and_authentication()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        using var api = f.Api(new() { ["Media:Folders:library"] = folder.Root }); using var client = DatabaseFixture.Client(api);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/media/imports/{source.ImportId}/scene-analyses", Request(source) with { Threshold = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/media/imports/{source.ImportId}/scene-analyses", Request(source) with { SelectionVersion = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/media/imports/{source.ImportId}/scene-analyses", Request(source))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/media/imports/{source.ImportId}/scene-analyses")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/video/scene-analyses/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/jobs", new MovieRecap.Jobs.Application.CreateJob("video.scene-analysis", "orphan"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key"); Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/media/imports/{source.ImportId}/scene-analyses", Request(source))).StatusCode);
    }
    sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "recap_scenes_" + Guid.NewGuid().ToString("N"));
        public MediaInspector Inspector { get; }
        public MediaSourceVerifier Verifier { get; }
        public Folder()
        {
            Directory.CreateDirectory(Root); var options = Options.Create(new MediaOptions { Folders = new() { ["library"] = Root }, StabilityMilliseconds = 100, FfprobePath = Environment.GetEnvironmentVariable("TEST_FFPROBE") ?? "ffprobe" });
            Inspector = new(options, new(options)); Verifier = new(Inspector, options);
        }
        public void Copy(string name) => File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), Path.Combine(Root, name));
        public void Dispose() => Directory.Delete(Root, true);
    }
}
