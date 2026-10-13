using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

public sealed class PostgresFrameFactAttribute : FactAttribute
{
    public PostgresFrameFactAttribute() { if (new[] { "TEST_POSTGRES", "TEST_FFPROBE", "TEST_FFMPEG", "TEST_SCENE_PYTHON" }.Any(x => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(x)))) Skip = "Set TEST_POSTGRES/TEST_FFPROBE/TEST_FFMPEG/TEST_SCENE_PYTHON; real scene frames are never emulated."; }
}
public sealed class FrameTests
{
    static readonly MediaRights Rights = new("Owned fixture", AuthorizationStatus.Authorized, "Synthetic fixture", "Local tests", "Local", "Fixture", null);
    static SelectedVideoAccess Media(DatabaseFixture f, Folder folder) => new(new PostgresStreamStore(f.Factory, f.Clock), folder.Verifier);
    static PostgresSceneStore Scenes(DatabaseFixture f, Folder folder) => new(f.Factory, Media(f, folder), f.Clock);
    static PostgresFrameStore Store(DatabaseFixture f, Folder folder) => new(f.Factory, Media(f, folder), folder.Artifacts, f.Clock);
    static FrameService Service(DatabaseFixture f, Folder folder) => new(Store(f, folder), Scenes(f, folder), Media(f, folder), folder.Artifacts);
    static FrameExecutor Executor(DatabaseFixture f, Folder folder) => new(Store(f, folder), Media(f, folder), folder.Artifacts, folder.Extractor);
    static RequestFrames Request(string key = "frames", int samples = 1) => new(key, "operator", samples, 3);
    static async Task<(SceneAnalysisView Scene, SelectedVideoSnapshot Source)> ImportAndScenes(DatabaseFixture f, Folder folder, int video = 1)
    {
        var store = new PostgresImportStore(f.Factory, f.Clock); var imported = await new ImportService(store, folder.Inspector).CreateAsync(new("owned", "library", "movie.mkv", "Owned source", "operator", Rights, 1), default);
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await new ImportExecutor(store, folder.Inspector).ExecuteAsync(lease, default);
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var view = (await streams.GetAsync(imported.Import.Id, default))!;
        await new StreamService(streams, folder.Verifier).SelectAsync(imported.Import.Id, new(view.Selection!.Version, video, null, null, "operator"), default);
        var source = (await Media(f, folder).GetAsync(imported.Import.Id, default))!;
        var created = (await new SceneService(Scenes(f, folder), Media(f, folder)).CreateAsync(source.ImportId, new("owned-scenes", source.SelectionVersion, "operator", 27, 2, 3), default))!;
        lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await new SceneExecutor(Scenes(f, folder), Media(f, folder), new PySceneDetectAdapter(Options.Create(new SceneOptions { PythonPath = Environment.GetEnvironmentVariable("TEST_SCENE_PYTHON")! }))).ExecuteAsync(lease, default);
        return ((await Scenes(f, folder).GetAsync(created.Analysis.Id, default))!, source);
    }
    [PostgresFrameFact]
    public async Task Real_nonprimary_vfr_and_offset_samples_have_correct_pixels_and_actual_source_timestamps()
    {
        foreach (var fixture in new[] { "owned-scenes-multi.mkv", "owned-scenes-vfr.mkv", "owned-scenes-offset.mkv" })
        {
            await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(fixture);
            var (scene, source) = await ImportAndScenes(f, folder, fixture.Contains("multi") ? 1 : 0); var original = File.ReadAllBytes(folder.Source); var stamp = File.GetLastWriteTimeUtc(folder.Source);
            var created = (await Service(f, folder).CreateAsync(scene.Analysis.Id, Request(), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
            var result = (await Store(f, folder).GetAsync(created.Sampling.Id, default))!; Assert.Equal(JobStatus.Succeeded, result.Job.Status); Assert.True(result.IsCurrentSelection);
            var expected = fixture.Contains("multi") ? new[] { 1.0, 3.0, 5.0 } : fixture.Contains("vfr") ? new[] { 0.5, 2.0 } : new[] { 1.0 };
            Assert.Equal(expected, result.Samples.Select(x => x.NormalizedSeconds!.Value));
            for (var i = 0; i < result.Samples.Count; i++)
            {
                await using var stream = (await Service(f, folder).DownloadAsync(result.Sampling.Id, i, default))!; using var png = new MemoryStream(); await stream.CopyToAsync(png); var bytes = png.ToArray();
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Samples[i].Sha256); var color = Center(bytes);
                if (fixture.Contains("multi")) { if (i == 0) Assert.True(color.R > 200 && color.G < 20 && color.B < 20); else if (i == 1) Assert.True(color.R > 200 && color.G > 200 && color.B > 200); else Assert.True(color.R < 20 && color.G < 20 && color.B < 20); }
                else if (fixture.Contains("vfr") && i == 0) Assert.True(color.R > 200 && color.B < 20); else Assert.True(color.B > 200 && color.R < 20);
                Assert.Equal(result.Samples[i].NormalizedSeconds + scene.Analysis.SourceOffsetSeconds, result.Samples[i].SourceSeconds);
            }
            if (fixture.Contains("offset")) Assert.Equal(6.0, result.Samples[0].SourceSeconds);
            if (fixture.Contains("vfr")) Assert.NotEqual(result.Samples[1].FrameIndex / 10.0, result.Samples[1].NormalizedSeconds);
            Assert.Equal(0, await Store(f, folder).CleanupAsync(result.Sampling.Id, default)); Assert.Equal(original, File.ReadAllBytes(folder.Source)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(folder.Source));
        }
    }
    [PostgresFrameFact]
    public async Task Concurrent_requests_commit_one_job_and_complete_batch_audit_with_multiple_samples_per_scene()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var (scene, _) = await ImportAndScenes(f, folder);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Service(f, folder).CreateAsync(scene.Analysis.Id, Request(samples: 3), default)));
        Assert.Single(results.Select(x => x!.Sampling.Id).Distinct()); Assert.Single(results.Select(x => x!.Job.Id).Distinct());
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).CreateAsync(scene.Analysis.Id, Request(samples: 2), default));
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var view = (await Store(f, folder).GetAsync(results[0]!.Sampling.Id, default))!; Assert.Equal(9, view.Samples.Count); Assert.All(view.Samples, x => Assert.NotNull(x.Sha256));
        Assert.Equal(new[] { 3, 10, 16, 23, 30, 36, 43, 50, 56 }, view.Samples.Select(x => x.FrameIndex)); Assert.Equal(new[] { "created", "started", "succeeded" }, (await f.Store.AuditAsync(lease.JobId, default)).Select(x => x.Action));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Single(await db.FrameSamplings.ToListAsync()); Assert.Equal(9, await db.FrameSamples.CountAsync()); Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details);
    }
    [PostgresFrameFact]
    public async Task Selection_changes_before_and_during_decode_reject_new_publication_and_preserve_historical_downloads()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var (scene, source) = await ImportAndScenes(f, folder);
        var completed = (await Service(f, folder).CreateAsync(scene.Analysis.Id, Request(), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var stale = (await Service(f, folder).CreateAsync(scene.Analysis.Id, Request("stale"), default))!; lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; var work = await Store(f, folder).WorkAsync(lease.JobId, default);
        await using (var attempt = await folder.Artifacts.BeginAsync(stale.Sampling.Id, lease.Token, default))
        {
            var result = await folder.Extractor.ExtractAsync(folder.Source, source, work.Plans, 0, attempt, null, default); await ChangeSelection(f, folder, source.ImportId);
            await Assert.ThrowsAsync<ImportConflictException>(() => Store(f, folder).CompleteAsync(lease, source, attempt, result, default));
        }
        await Store(f, folder).CleanupAsync(stale.Sampling.Id, default); await Assert.ThrowsAsync<ImportConflictException>(() => Executor(f, folder).ExecuteAsync(lease, default));
        var old = (await Store(f, folder).GetAsync(completed.Sampling.Id, default))!; Assert.False(old.IsCurrentSelection); Assert.Equal(JobStatus.Succeeded, old.Job.Status);
        await using var image = (await Service(f, folder).DownloadAsync(old.Sampling.Id, 0, default))!; Assert.True(image.Length > 0);
        Assert.Equal(old.Sampling.Id, (await Service(f, folder).CreateAsync(scene.Analysis.Id, Request(), default))!.Sampling.Id);
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).CreateAsync(scene.Analysis.Id, Request("changed"), default));
    }
    [PostgresFrameFact]
    public async Task Audit_failure_after_directory_promotion_rolls_back_all_image_references_and_cleans_batch()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var (scene, _) = await ImportAndScenes(f, folder);
        var created = (await Service(f, folder).CreateAsync(scene.Analysis.Id, Request(), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION jobs.reject_frames() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW."Action" = 'succeeded' THEN RAISE EXCEPTION 'intentional fault'; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER reject_frames BEFORE INSERT ON jobs."AuditLogs" FOR EACH ROW EXECUTE FUNCTION jobs.reject_frames();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Executor(f, folder).ExecuteAsync(lease, default));
        var view = (await Store(f, folder).GetAsync(created.Sampling.Id, default))!; Assert.Null(view.Sampling.CompletedAt); Assert.All(view.Samples, x => Assert.Null(x.Sha256)); Assert.Equal(JobStatus.Running, view.Job.Status);
        Assert.Empty(Directory.GetFiles(folder.ArtifactRoot, "*.png", SearchOption.AllDirectories)); Assert.DoesNotContain(await f.Store.AuditAsync(lease.JobId, default), x => x.Action == "succeeded");
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_frames ON jobs.\"AuditLogs\"; DROP FUNCTION jobs.reject_frames();"); await Executor(f, folder).ExecuteAsync(lease, default);
        Assert.Equal(JobStatus.Succeeded, (await Store(f, folder).GetAsync(created.Sampling.Id, default))!.Job.Status);
    }
    [PostgresFrameFact]
    public async Task Crash_orphan_active_attempt_pause_cancel_expiry_and_recovery_obey_batch_fences()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var (scene, source) = await ImportAndScenes(f, folder);
        var created = (await Service(f, folder).CreateAsync(scene.Analysis.Id, Request(), default))!; var stale = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!; var work = await Store(f, folder).WorkAsync(stale.JobId, default);
        await using (var attempt = await folder.Artifacts.BeginAsync(created.Sampling.Id, stale.Token, default))
        {
            var result = await folder.Extractor.ExtractAsync(folder.Source, source, work.Plans, 0, attempt, null, default);
            var job = (await f.Store.GetAsync(stale.JobId, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Pause, job.Version, default);
            await Assert.ThrowsAsync<JobConflictException>(() => Store(f, folder).CompleteAsync(stale, source, attempt, result, default));
            job = (await f.Store.GetAsync(job.Id, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Resume, job.Version, default);
            await attempt.PromoteAsync(result, default); Assert.Equal(0, await Store(f, folder).CleanupAsync(created.Sampling.Id, default)); // Crash window before reference; active lock protects batch.
        }
        Assert.Equal(3, await Store(f, folder).CleanupAsync(created.Sampling.Id, default)); var expired = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        await using (var attempt = await folder.Artifacts.BeginAsync(created.Sampling.Id, expired.Token, default))
        {
            var result = await folder.Extractor.ExtractAsync(folder.Source, source, work.Plans, 0, attempt, null, default);
            await Assert.ThrowsAsync<JobConflictException>(() => Store(f, folder).CompleteAsync(expired, source, new ExpireOnPromotion(attempt, f.Clock), result, default));
        }
        Assert.Equal(3, await Store(f, folder).CleanupAsync(created.Sampling.Id, default)); Assert.Null(await f.Store.ClaimAsync("recover", TimeSpan.FromSeconds(30), default)); f.Clock.Advance(TimeSpan.FromSeconds(10));
        var recovered = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; Assert.NotEqual(expired.Token, recovered.Token);
        await Executor(f, folder).ExecuteAsync(recovered, default); Assert.Equal(recovered.Token, (await Store(f, folder).GetAsync(created.Sampling.Id, default))!.Sampling.ArtifactToken);
        var cancel = (await Service(f, folder).CreateAsync(scene.Analysis.Id, Request("cancel"), default))!; var cancelledLease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        var current = (await f.Store.GetAsync(cancel.Job.Id, default))!; await f.Store.CommandAsync(current.Id, JobCommand.Cancel, current.Version, default);
        await Assert.ThrowsAsync<JobConflictException>(() => Executor(f, folder).ExecuteAsync(cancelledLease, default)); Assert.Null((await Store(f, folder).GetAsync(cancel.Sampling.Id, default))!.Sampling.CompletedAt);
    }
    [PostgresFrameFact]
    public async Task Source_mutation_artifact_corruption_and_image_caps_never_publish_partial_frames()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var (scene, _) = await ImportAndScenes(f, folder);
        var created = (await Service(f, folder).CreateAsync(scene.Analysis.Id, Request(), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        var original = File.ReadAllBytes(folder.Source); var stamp = File.GetLastWriteTimeUtc(folder.Source); var changed = original.ToArray(); changed[0] ^= 1; File.WriteAllBytes(folder.Source, changed); File.SetLastWriteTimeUtc(folder.Source, stamp);
        await Assert.ThrowsAsync<ImportException>(() => Executor(f, folder).ExecuteAsync(lease, default)); File.WriteAllBytes(folder.Source, original); File.SetLastWriteTimeUtc(folder.Source, stamp);
        // A total cap smaller than all three genuine PNGs must fail without silently dropping scenes.
        var limited = new FfmpegFrameExtractor(Options.Create(new FrameOptions { FfmpegPath = Environment.GetEnvironmentVariable("TEST_FFMPEG")!, MaxBatchBytes = 1024 }));
        Assert.Equal("frame-batch-limit", (await Assert.ThrowsAsync<FrameException>(() => new FrameExecutor(Store(f, folder), Media(f, folder), folder.Artifacts, limited).ExecuteAsync(lease, default))).Code);
        Assert.Empty(Directory.GetFiles(folder.ArtifactRoot, "*.png", SearchOption.AllDirectories)); await Executor(f, folder).ExecuteAsync(lease, default);
        var completed = (await Store(f, folder).GetAsync(created.Sampling.Id, default))!; var pngPath = Path.Combine(folder.ArtifactRoot, "frames-v1", completed.Sampling.Id.ToString("N"), completed.Sampling.ArtifactToken!.Value.ToString("N") + ".ready", "0000.png");
        File.WriteAllBytes(pngPath, File.ReadAllBytes(pngPath)[..^1]); await Assert.ThrowsAsync<FrameException>(() => Service(f, folder).DownloadAsync(completed.Sampling.Id, 0, default)); Assert.Equal(0, await Store(f, folder).CleanupAsync(completed.Sampling.Id, default));
    }
    [PostgresFrameFact]
    public async Task Api_validates_completed_scenes_auth_idempotency_and_serves_verified_pngs()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var (scene, source) = await ImportAndScenes(f, folder);
        var pending = (await new SceneService(Scenes(f, folder), Media(f, folder)).CreateAsync(source.ImportId, new("pending", source.SelectionVersion, "actor"), default))!;
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).CreateAsync(pending.Analysis.Id, Request(), default));
        var pendingJob = (await f.Store.GetAsync(pending.Job.Id, default))!; await f.Store.CommandAsync(pendingJob.Id, JobCommand.Cancel, pendingJob.Version, default);
        using var api = f.Api(new() { ["Media:Folders:library"] = folder.Library, ["Frames:Root"] = folder.ArtifactRoot }); using var client = DatabaseFixture.Client(api);
        var route = $"/api/video/scene-analyses/{scene.Analysis.Id}/frame-samplings"; Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, Request(samples: 0))).StatusCode);
        var response = await client.PostAsJsonAsync(route, Request()); Assert.Equal(HttpStatusCode.Created, response.StatusCode); var id = Guid.Parse(response.Headers.Location!.ToString().Split('/')[^1]);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/video/frame-samplings/{id}/samples/0")).StatusCode); f.Clock.Advance(TimeSpan.FromMinutes(1));
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var image = await client.GetAsync($"/api/video/frame-samplings/{id}/samples/0"); Assert.Equal(HttpStatusCode.OK, image.StatusCode); Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType); Assert.True(Center(await image.Content.ReadAsByteArrayAsync()).R > 200);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/video/frame-samplings/{id}/samples/999")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/video/frame-samplings/{id}/cleanup", null)).StatusCode); Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/jobs", new CreateJob("video.frame-sampling", "orphan"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key"); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/video/frame-samplings/{id}/samples/0")).StatusCode);
    }
    [PostgresFrameFact]
    public async Task Additive_upgrade_preserves_prior_scene_ranges_and_audio_operations_with_no_invented_samples()
    {
        await using var f = new DatabaseFixture(); await f.Initialize("20261012212737_VideoAudioExtraction"); using var folder = new Folder("owned-multistream.mkv"); var (scene, source) = await ImportAndScenes(f, folder, 0);
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var selection = (await streams.GetAsync(source.ImportId, default))!.Selection!;
        await new StreamService(streams, folder.Verifier).SelectAsync(source.ImportId, new(selection.Version, 0, 3, null, "audio-operator"), default);
        var audioMedia = new SelectedAudioAccess(streams, folder.Verifier); var selectedAudio = (await audioMedia.GetAsync(source.ImportId, default))!;
        var audioArtifacts = new LocalAudioArtifacts(Options.Create(new AudioOptions { Root = folder.ArtifactRoot }), Options.Create(new MediaOptions { Folders = new() { ["library"] = folder.Library } }));
        var audio = (await new AudioService(new PostgresAudioStore(f.Factory, audioMedia, audioArtifacts, f.Clock), audioMedia, audioArtifacts).CreateAsync(source.ImportId, new("historical-audio", selectedAudio.SelectionVersion, "actor", 1), default))!;
        // Actual prior-schema use case creates a queued job; no successful audio is fabricated.
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        Assert.Empty(await db.FrameSamplings.ToListAsync()); Assert.Empty(await db.FrameSamples.ToListAsync()); Assert.Equal(scene.Scenes.Count, await db.MovieScenes.CountAsync()); Assert.Equal(audio.Extraction.Id, (await db.AudioExtractions.SingleAsync()).Id);
        Assert.Equal(scene.Analysis.Id, (await db.SceneAnalyses.SingleAsync()).Id); Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details); Assert.Equal(8, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }
    public static (byte R, byte G, byte B) Center(byte[] png) { var pixels = PngFrames.DecodeRgb(png); var index = (90 * 320 + 160) * 3; return (pixels[index], pixels[index + 1], pixels[index + 2]); }
    static async Task ChangeSelection(DatabaseFixture f, Folder folder, Guid id)
    {
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var view = (await streams.GetAsync(id, default))!; await new StreamService(streams, folder.Verifier).SelectAsync(id, new(view.Selection!.Version, 0, null, null, "changed"), default);
    }
    sealed class ExpireOnPromotion(IFrameAttempt inner, ManualClock clock) : IFrameAttempt
    {
        public Guid SamplingId => inner.SamplingId; public Guid Token => inner.Token;
        public Task WriteAsync(int o, ReadOnlyMemory<byte> png, CancellationToken ct) => inner.WriteAsync(o, png, ct);
        public Task<FrameResult> PrepareAsync(string v, IReadOnlyList<double> times, IReadOnlyList<FramePlan> plans, double offset, CancellationToken ct) => inner.PrepareAsync(v, times, plans, offset, ct);
        public async Task PromoteAsync(FrameResult result, CancellationToken ct) { await inner.PromoteAsync(result, ct); clock.Advance(TimeSpan.FromSeconds(16)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "recap_frames_" + Guid.NewGuid().ToString("N")); public string Library => Path.Combine(Root, "library"); public string ArtifactRoot => Path.Combine(Root, "outputs"); public string Source => Path.Combine(Library, "movie.mkv");
        public MediaInspector Inspector { get; } public MediaSourceVerifier Verifier { get; } public LocalFrameArtifacts Artifacts { get; } public FfmpegFrameExtractor Extractor { get; }
        public Folder(string fixture = "owned-scenes-multi.mkv")
        {
            Directory.CreateDirectory(Library); Directory.CreateDirectory(ArtifactRoot); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), Source);
            var media = Options.Create(new MediaOptions { Folders = new() { ["library"] = Library }, StabilityMilliseconds = 100, FfprobePath = Environment.GetEnvironmentVariable("TEST_FFPROBE")! }); Inspector = new(media, new(media)); Verifier = new(Inspector, media);
            var frames = Options.Create(new FrameOptions { Root = ArtifactRoot, FfmpegPath = Environment.GetEnvironmentVariable("TEST_FFMPEG")! }); Artifacts = new(frames, media); Extractor = new(frames);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
