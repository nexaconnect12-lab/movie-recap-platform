using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
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

public sealed class PostgresSubtitleFactAttribute : FactAttribute
{
    public PostgresSubtitleFactAttribute()
    {
        if (new[] { "TEST_POSTGRES", "TEST_FFPROBE", "TEST_FFMPEG" }.Any(x => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(x)))) Skip = "Set TEST_POSTGRES, TEST_FFPROBE, TEST_FFMPEG for real subtitle extraction/database verification.";
    }
}
public sealed class SubtitleTests
{
    static readonly MediaRights Rights = new("Owned fixture", AuthorizationStatus.Authorized, "Synthetic fixture", "Local tests", "Local", "Fixture", null);
    static SelectedSubtitleAccess Media(DatabaseFixture f, Folder folder) => new(new PostgresStreamStore(f.Factory, f.Clock), folder.Verifier);
    static PostgresSubtitleStore Store(DatabaseFixture f, Folder folder) => new(f.Factory, Media(f, folder), folder.Artifacts, f.Clock);
    static SubtitleService Service(DatabaseFixture f, Folder folder) => new(Store(f, folder), Media(f, folder), folder.Artifacts);
    static SubtitleExecutor Executor(DatabaseFixture f, Folder folder) => new(Store(f, folder), Media(f, folder), folder.Artifacts, folder.Extractor);
    static RequestSubtitle Request(SelectedSubtitleSnapshot source, string key = "subtitle") => new(key, source.SelectionVersion, "operator", 3);
    static async Task<SelectedSubtitleSnapshot> ImportAndSelect(DatabaseFixture f, Folder folder, int index = 5)
    {
        var store = new PostgresImportStore(f.Factory, f.Clock);
        var imported = await new ImportService(store, folder.Inspector).CreateAsync(new("owned", "library", Path.GetFileName(folder.Source), "Owned source", "operator", Rights, 1), default);
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await new ImportExecutor(store, folder.Inspector).ExecuteAsync(lease, default);
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var view = (await streams.GetAsync(imported.Import.Id, default))!;
        await new StreamService(streams, folder.Verifier).SelectAsync(imported.Import.Id, new(view.Selection!.Version, 0, null, index, "operator"), default);
        return (await Media(f, folder).GetAsync(imported.Import.Id, default))!;
    }
    [PostgresSubtitleFact]
    public async Task Real_selected_subrip_persists_cues_and_canonical_utf8_without_source_changes()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var bytes = File.ReadAllBytes(folder.Source); var stamp = File.GetLastWriteTimeUtc(folder.Source);
        var requests = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Service(f, folder).CreateAsync(source.ImportId, Request(source), default)));
        Assert.Single(requests.Select(x => x!.Extraction.Id).Distinct()); var a = requests[0]!.Extraction;
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).CreateAsync(source.ImportId, Request(source) with { MaxAttempts = 1 }, default));
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var result = (await Store(f, folder).GetAsync(a.Id, default))!; Assert.Equal(JobStatus.Succeeded, result.Job.Status); Assert.True(result.IsCurrentSelection); Assert.Equal(5, result.Extraction.SubtitleIndex);
        await using var read = (await Service(f, folder).DownloadAsync(a.Id, default))!; using var wav = new MemoryStream(); await read.CopyToAsync(wav);
        var extracted = wav.ToArray(); Assert.Equal(1, result.Extraction.CueCount);
        var cue = Assert.Single(result.Cues); Assert.Equal(0, cue.StartMilliseconds); Assert.Equal(1000, cue.EndMilliseconds); Assert.Equal("Synthetic fixture caption.", cue.Text);
        Assert.Equal(SubRip.Encode([cue.Data]), extracted); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(extracted)), result.Extraction.ArtifactSha256);
        Assert.Equal(new[] { "created", "started", "succeeded" }, (await f.Store.AuditAsync(lease.JobId, default)).Select(x => x.Action));
        Assert.Equal(0, await Store(f, folder).CleanupAsync(a.Id, default)); Assert.Empty(Directory.GetFiles(folder.ArtifactRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(bytes, File.ReadAllBytes(folder.Source)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(folder.Source));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details);
    }
    [PostgresSubtitleFact]
    public async Task Selection_changes_reject_stale_completion_and_keep_successful_history_downloadable()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var first = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var second = (await Service(f, folder).CreateAsync(source.ImportId, Request(source, "stale"), default))!; lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await using (var attempt = await folder.Artifacts.BeginAsync(second.Extraction.Id, lease.Token, default))
        {
            var artifact = await attempt.PrepareAsync(await folder.Extractor.ExtractAsync(folder.Source, source, attempt.Output, null, default), default);
            await ChangeSelection(f, folder, source.ImportId, 5);
            await Assert.ThrowsAsync<ImportConflictException>(() => Store(f, folder).CompleteAsync(lease, source, attempt, artifact, default));
        }
        await Store(f, folder).CleanupAsync(second.Extraction.Id, default); Assert.Null((await Store(f, folder).GetAsync(second.Extraction.Id, default))!.Extraction.CompletedAt);
        Assert.Equal("subtitle-selection-changed", (await Assert.ThrowsAsync<SubtitleException>(() => Executor(f, folder).ExecuteAsync(lease, default))).Code);
        Assert.False((await Store(f, folder).GetAsync(first.Extraction.Id, default))!.IsCurrentSelection); await using var old = (await Service(f, folder).DownloadAsync(first.Extraction.Id, default))!; Assert.True(old.Length > 44);
        Assert.Equal(first.Extraction.Id, (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!.Extraction.Id);
    }
    [PostgresSubtitleFact]
    public async Task Audit_fault_after_promotion_rolls_back_success_and_cleanup_removes_only_unreferenced_output()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var a = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION jobs.reject_subtitle_success() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW."Action" = 'succeeded' THEN RAISE EXCEPTION 'intentional fault'; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER reject_subtitle_success BEFORE INSERT ON jobs."AuditLogs" FOR EACH ROW EXECUTE FUNCTION jobs.reject_subtitle_success();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Executor(f, folder).ExecuteAsync(lease, default));
        Assert.Null((await Store(f, folder).GetAsync(a.Extraction.Id, default))!.Extraction.CompletedAt); Assert.Equal(JobStatus.Running, (await f.Store.GetAsync(lease.JobId, default))!.Status);
        Assert.Empty(Directory.GetFiles(folder.ArtifactRoot, "*.srt", SearchOption.AllDirectories)); Assert.Empty(Directory.GetFiles(folder.ArtifactRoot, "*.tmp", SearchOption.AllDirectories));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_subtitle_success ON jobs.\"AuditLogs\"; DROP FUNCTION jobs.reject_subtitle_success();");
        await Executor(f, folder).ExecuteAsync(lease, default); Assert.Equal(JobStatus.Succeeded, (await Store(f, folder).GetAsync(a.Extraction.Id, default))!.Job.Status);
    }
    [PostgresSubtitleFact]
    public async Task Orphan_after_crash_is_reconciled_while_active_attempt_and_committed_artifact_are_preserved()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var a = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        await using (var attempt = await folder.Artifacts.BeginAsync(a.Extraction.Id, lease.Token, default))
        {
            var artifact = await attempt.PrepareAsync(await folder.Extractor.ExtractAsync(folder.Source, source, attempt.Output, null, default), default); await attempt.PromoteAsync(artifact, default);
            Assert.Equal(0, await Store(f, folder).CleanupAsync(a.Extraction.Id, default)); // still active even without a DB reference
        }
        Assert.Equal(1, await Store(f, folder).CleanupAsync(a.Extraction.Id, default));
        f.Clock.Advance(TimeSpan.FromSeconds(16)); Assert.Null(await f.Store.ClaimAsync("recover", TimeSpan.FromSeconds(30), default)); f.Clock.Advance(TimeSpan.FromSeconds(10));
        var recovered = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; Assert.NotEqual(lease.Token, recovered.Token);
        await Executor(f, folder).ExecuteAsync(recovered, default); var completed = (await Store(f, folder).GetAsync(a.Extraction.Id, default))!;
        Assert.Equal(recovered.Token, completed.Extraction.ArtifactToken); Assert.Equal(0, await Store(f, folder).CleanupAsync(a.Extraction.Id, default));
        Assert.Single(Directory.GetFiles(folder.ArtifactRoot, "*.srt", SearchOption.AllDirectories));
    }
    [PostgresSubtitleFact]
    public async Task Pause_cancel_expiry_and_expiry_during_promotion_fence_artifact_publication()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var a = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        await using (var attempt = await folder.Artifacts.BeginAsync(a.Extraction.Id, lease.Token, default))
        {
            var artifact = await attempt.PrepareAsync(await folder.Extractor.ExtractAsync(folder.Source, source, attempt.Output, null, default), default);
            var job = (await f.Store.GetAsync(lease.JobId, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Pause, job.Version, default);
            await Assert.ThrowsAsync<JobConflictException>(() => Store(f, folder).CompleteAsync(lease, source, attempt, artifact, default));
            job = (await f.Store.GetAsync(job.Id, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Resume, job.Version, default);
        }
        await Store(f, folder).CleanupAsync(a.Extraction.Id, default); lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        await using (var attempt = await folder.Artifacts.BeginAsync(a.Extraction.Id, lease.Token, default))
        {
            var artifact = await attempt.PrepareAsync(await folder.Extractor.ExtractAsync(folder.Source, source, attempt.Output, null, default), default);
            await Assert.ThrowsAsync<JobConflictException>(() => Store(f, folder).CompleteAsync(lease, source, new ExpireOnPromotion(attempt, f.Clock), artifact, default));
        }
        Assert.Equal(1, await Store(f, folder).CleanupAsync(a.Extraction.Id, default)); Assert.Null((await Store(f, folder).GetAsync(a.Extraction.Id, default))!.Extraction.CompletedAt);
        var current = (await f.Store.GetAsync(lease.JobId, default))!; await f.Store.CommandAsync(current.Id, JobCommand.Cancel, current.Version, default);
        Assert.Equal(JobStatus.Cancelled, (await f.Store.GetAsync(lease.JobId, default))!.Status); Assert.DoesNotContain(await f.Store.AuditAsync(lease.JobId, default), x => x.Action == "succeeded");
    }
    [PostgresSubtitleFact]
    public async Task Changed_source_corrupt_artifact_and_resource_limits_never_publish_partial_subtitle()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var a = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        var original = File.ReadAllBytes(folder.Source); var stamp = File.GetLastWriteTimeUtc(folder.Source); var changed = original.ToArray(); changed[0] ^= 1; File.WriteAllBytes(folder.Source, changed); File.SetLastWriteTimeUtc(folder.Source, stamp);
        Assert.Equal("stream-source-content-changed", (await Assert.ThrowsAsync<ImportException>(() => Executor(f, folder).ExecuteAsync(lease, default))).Code);
        File.WriteAllBytes(folder.Source, original); File.SetLastWriteTimeUtc(folder.Source, stamp);
        var limit = new FfmpegSubtitleExtractor(Options.Create(new SubtitleOptions { FfmpegPath = Environment.GetEnvironmentVariable("TEST_FFMPEG")!, MaxBytes = 16 }));
        Assert.Equal("subtitle-output-limit", (await Assert.ThrowsAsync<SubtitleException>(() => new SubtitleExecutor(Store(f, folder), Media(f, folder), folder.Artifacts, limit).ExecuteAsync(lease, default))).Code);
        Assert.Empty(Directory.GetFiles(folder.ArtifactRoot, "*.tmp", SearchOption.AllDirectories)); await Executor(f, folder).ExecuteAsync(lease, default);
        var completed = (await Store(f, folder).GetAsync(a.Extraction.Id, default))!.Extraction; using (var change = new FileStream(folder.PathFor(completed), FileMode.Open, FileAccess.Write)) { change.Position = 40; var first = original[0]; change.WriteByte(first); }
        await Assert.ThrowsAsync<SubtitleException>(() => Service(f, folder).DownloadAsync(a.Extraction.Id, default)); Assert.Equal(0, await Store(f, folder).CleanupAsync(a.Extraction.Id, default));
    }
    [PostgresSubtitleFact]
    public async Task Api_validates_auth_selection_idempotency_and_serves_verified_artifacts_only()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        using var api = f.Api(new() { ["Media:Folders:library"] = folder.Library, ["Subtitles:Root"] = folder.ArtifactRoot }); using var client = DatabaseFixture.Client(api);
        var route = $"/api/media/imports/{source.ImportId}/subtitle-extractions";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, Request(source) with { MaxAttempts = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, Request(source) with { SelectionVersion = Guid.NewGuid() })).StatusCode);
        var response = await client.PostAsJsonAsync(route, Request(source)); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = Guid.Parse(response.Headers.Location!.ToString().Split('/')[^1]); Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/video/subtitle-extractions/{id}/artifact")).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); // API uses system time; bring the test worker clock beyond creation availability.
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var download = await client.GetAsync($"/api/video/subtitle-extractions/{id}/artifact"); Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal("application/x-subrip", download.Content.Headers.ContentType!.MediaType);
        var bytes = await download.Content.ReadAsByteArrayAsync(); Assert.Contains("Synthetic fixture caption.", System.Text.Encoding.UTF8.GetString(bytes)); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/video/subtitle-extractions/{id}/cleanup", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/jobs", new CreateJob("video.subtitle-extraction", "orphan"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key"); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/video/subtitle-extractions/{id}/artifact")).StatusCode);
        await ChangeSelection(f, folder, source.ImportId, null); await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).CreateAsync(source.ImportId, Request(source, "none"), default));
    }
    [PostgresSubtitleFact]
    public async Task Additive_upgrade_preserves_previous_streams_and_scenes_without_invented_subtitle()
    {
        await using var f = new DatabaseFixture(); await f.Initialize("20261013011015_VideoPreparationReview"); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var scenes = new PostgresSceneStore(f.Factory, new SelectedVideoAccess(new PostgresStreamStore(f.Factory, f.Clock), folder.Verifier), f.Clock);
        var created = (await new SceneService(scenes, new SelectedVideoAccess(new PostgresStreamStore(f.Factory, f.Clock), folder.Verifier)).CreateAsync(source.ImportId, new("existing-scenes", source.SelectionVersion, "actor"), default))!;
        // Queued prior-scene records are preserved; no successful processing is fabricated.
        await using var db = await f.Factory.CreateDbContextAsync();
        var binding = new PreparationBinding(source.ImportId, source.AssetId, source.InventoryVersion, source.SelectionVersion, 0, null, 5, source.Sha256, source.SizeBytes, null, null, null, SchemaVersion: 1);
        var review = PreparationReview.Create(source.ImportId, f.Clock.GetUtcNow()); var before = review.Version;
        review.Decide(before, binding.Hash, PreparationStatus.Rejected, "operator", "Derived artifacts are not prepared", f.Clock.GetUtcNow()); db.PreparationReviews.Add(review); await db.SaveChangesAsync();
        var auditId = Guid.NewGuid(); var action = "rejected"; var actor = "operator"; var notes = "Derived artifacts are not prepared"; var now = f.Clock.GetUtcNow();
        // Insert the real rejection's historical binding through the pre-upgrade column contract.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO video."PreparationAudits" ("Id","ImportId","Action","BeforeVersion","AfterVersion","SnapshotHash","RequestedBy","Notes","At","Binding_ImportId","Binding_AssetId","Binding_InventoryVersion","Binding_SelectionVersion","Binding_VideoIndex","Binding_SubtitleIndex","Binding_SourceSha256","Binding_SourceSizeBytes")
            VALUES ({auditId},{source.ImportId},{action},{before},{review.Version},{binding.Hash},{actor},{notes},{now},{source.ImportId},{source.AssetId},{source.InventoryVersion},{source.SelectionVersion},0,5,{source.Sha256},{source.SizeBytes})
            """);
        await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        var historical = await db.PreparationAudits.AsNoTracking().SingleAsync(x => x.Id == auditId);
        Assert.Equal(1, historical.Binding.SchemaVersion); Assert.Null(historical.Binding.SubtitleId); Assert.Equal(binding.Hash, historical.SnapshotHash); Assert.Equal(historical.SnapshotHash, historical.Binding.Hash);
        Assert.Equal(binding.Hash, (await db.PreparationReviews.AsNoTracking().SingleAsync()).DecisionSnapshotHash);
        Assert.Empty(await db.SubtitleExtractions.ToListAsync()); Assert.Equal(created.Analysis.Id, (await db.SceneAnalyses.SingleAsync()).Id); Assert.Equal(source.SelectionVersion, (await Media(f, folder).GetAsync(source.ImportId, default))!.SelectionVersion);
        Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details); Assert.Equal(10, (await db.Database.GetAppliedMigrationsAsync()).Count()); Assert.Empty(await db.SubtitleCues.ToListAsync());
    }
    static async Task ChangeSelection(DatabaseFixture f, Folder folder, Guid id, int? subtitle)
    {
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var view = (await streams.GetAsync(id, default))!;
        await new StreamService(streams, folder.Verifier).SelectAsync(id, new(view.Selection!.Version, 0, null, subtitle, "changed"), default);
    }
    sealed class ExpireOnPromotion(ISubtitleAttempt inner, ManualClock clock) : ISubtitleAttempt
    {
        public Guid ExtractionId => inner.ExtractionId; public Guid Token => inner.Token; public Stream Output => inner.Output;
        public Task<SubtitleArtifact> PrepareAsync(ExtractedSubtitle a, CancellationToken ct) => inner.PrepareAsync(a, ct);
        public async Task PromoteAsync(SubtitleArtifact a, CancellationToken ct) { await inner.PromoteAsync(a, ct); clock.Advance(TimeSpan.FromSeconds(16)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    static async Task RemuxOwnedCaption(Folder folder, string codec, string caption)
    {
        var textPath = Path.Combine(folder.Library, "caption.srt"); await File.WriteAllTextAsync(textPath, caption, new System.Text.UTF8Encoding(false));
        using var process = new Process { StartInfo = new(Environment.GetEnvironmentVariable("TEST_FFMPEG")!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-v", "error", "-n", "-i", Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-silent.mp4"), "-i", textPath, "-map", "0:v:0", "-map", "1:s:0", "-c:v", "copy", "-c:s", codec, folder.Source }) process.StartInfo.ArgumentList.Add(argument);
        process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync()); Assert.Equal(0, process.ExitCode);
    }
    [PostgresSubtitleFact]
    public async Task Real_offset_multiline_utf8_subrip_keeps_source_timestamps()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(false);
        await RemuxOwnedCaption(folder, "srt", "1\n00:00:05,000 --> 00:00:06,000\nမြန်မာ caption\nOwned second line.\n\n");
        var source = await ImportAndSelect(f, folder, 1); var created = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!;
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var cue = Assert.Single((await Store(f, folder).GetAsync(created.Extraction.Id, default))!.Cues);
        Assert.Equal(5000, cue.StartMilliseconds); Assert.Equal(6000, cue.EndMilliseconds); Assert.Equal("မြန်မာ caption\nOwned second line.", cue.Text);
    }
    [PostgresSubtitleFact]
    public async Task Real_mov_text_selection_is_rejected_explicitly_without_creating_an_extraction_job()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(false, "movie.mp4");
        await RemuxOwnedCaption(folder, "mov_text", "1\n00:00:00,000 --> 00:00:01,000\nOwned caption\n\n"); var source = await ImportAndSelect(f, folder, 1);
        Assert.Equal("mov_text", source.Codec);
        Assert.Equal("subtitle-codec-unsupported", (await Assert.ThrowsAsync<SubtitleException>(() => Service(f, folder).CreateAsync(source.ImportId, Request(source), default))).Code);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Empty(await db.SubtitleExtractions.ToListAsync()); Assert.DoesNotContain(await db.Jobs.ToListAsync(), x => x.Kind == "video.subtitle-extraction");
    }
    sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "recap_subtitle_" + Guid.NewGuid().ToString("N"));
        readonly string fileName;
        public string Library => Path.Combine(Root, "library"); public string ArtifactRoot => Path.Combine(Root, "outputs"); public string Source => Path.Combine(Library, fileName);
        public MediaInspector Inspector { get; } public MediaSourceVerifier Verifier { get; } public LocalSubtitleArtifacts Artifacts { get; } public FfmpegSubtitleExtractor Extractor { get; }
        public Folder(bool copyFixture = true, string fileName = "movie.mkv")
        {
            this.fileName = fileName;
            Directory.CreateDirectory(Library); Directory.CreateDirectory(ArtifactRoot); if (copyFixture) File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-multistream.mkv"), Source);
            var media = Options.Create(new MediaOptions { Folders = new() { ["library"] = Library }, StabilityMilliseconds = 100, FfprobePath = Environment.GetEnvironmentVariable("TEST_FFPROBE")! });
            Inspector = new(media, new(media)); Verifier = new(Inspector, media); var subtitle = Options.Create(new SubtitleOptions { Root = ArtifactRoot, FfmpegPath = Environment.GetEnvironmentVariable("TEST_FFMPEG")! }); Artifacts = new(subtitle, media); Extractor = new(subtitle);
        }
        public string PathFor(SubtitleExtraction a) => Path.Combine(ArtifactRoot, "subtitle-v1", a.Id.ToString("N"), a.ArtifactToken!.Value.ToString("N") + ".srt");
        public void Dispose() => Directory.Delete(Root, true);
    }
}


