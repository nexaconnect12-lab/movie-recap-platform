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

public sealed class PreparationTests
{
    static readonly MediaRights Rights = new("Owned fixture", AuthorizationStatus.Authorized, "Synthetic fixture", "Local tests", "Local", "Fixture", null);
    static PostgresPreparationStore Store(DatabaseFixture f) => new(f.Factory, f.Clock);
    static PreparationService Service(DatabaseFixture f, Folder folder) => new(Store(f), folder.Media(f), folder.Checker(f));
    static PreparationExecutor Executor(DatabaseFixture f, Folder folder) => new(Store(f), folder.Media(f), folder.Checker(f));
    static RequestPreparationValidation Request(PreparationOverview view, string key = "validate") => new(key, view.Snapshot.SnapshotHash, "operator", 3);
    static DecidePreparation Decision(PreparationOverview view, PreparationStatus status = PreparationStatus.Approved) => new(view.Review?.Version ?? Guid.Empty, view.Snapshot.SnapshotHash, status, "operator", "Reviewed preparation");
    static async Task<Guid> Prepare(DatabaseFixture f, Folder folder, bool audio = true, bool artifacts = true)
    {
        var imports = new PostgresImportStore(f.Factory, f.Clock); var created = await new ImportService(imports, folder.Inspector).CreateAsync(new("owned", "library", "movie.mkv", "Owned source", "operator", Rights, 1), default);
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await new ImportExecutor(imports, folder.Inspector).ExecuteAsync(lease, default);
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var view = (await streams.GetAsync(created.Import.Id, default))!;
        await new StreamService(streams, folder.Verifier).SelectAsync(created.Import.Id, new(view.Selection!.Version, 1, audio ? 3 : null, null, "operator"), default);
        if (!artifacts) return created.Import.Id;
        var video = new SelectedVideoAccess(streams, folder.Verifier); var source = (await video.GetAsync(created.Import.Id, default))!; var scenes = new PostgresSceneStore(f.Factory, video, f.Clock);
        var scene = (await new SceneService(scenes, video).CreateAsync(source.ImportId, new("scenes", source.SelectionVersion, "operator", 27, 2, 1), default))!;
        lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await new SceneExecutor(scenes, video, new PySceneDetectAdapter(Options.Create(new SceneOptions { PythonPath = Environment.GetEnvironmentVariable("TEST_SCENE_PYTHON")! }))).ExecuteAsync(lease, default);
        if (audio)
        {
            var selected = new SelectedAudioAccess(streams, folder.Verifier); var audioSource = (await selected.GetAsync(source.ImportId, default))!; var store = new PostgresAudioStore(f.Factory, selected, folder.Audio, f.Clock);
            await new AudioService(store, selected, folder.Audio).CreateAsync(source.ImportId, new("audio", audioSource.SelectionVersion, "operator", 1), default);
            lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await new AudioExecutor(store, selected, folder.Audio, folder.AudioExtractor).ExecuteAsync(lease, default);
        }
        var frames = new PostgresFrameStore(f.Factory, video, folder.Frames, f.Clock); await new FrameService(frames, scenes, video, folder.Frames).CreateAsync(scene.Analysis.Id, new("frames", "operator", 1, 1), default);
        lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await new FrameExecutor(frames, video, folder.Frames, folder.FrameExtractor).ExecuteAsync(lease, default); return source.ImportId;
    }
    static async Task<PreparationOverview> Validate(DatabaseFixture f, Folder folder, Guid id, string key = "validate")
    {
        var view = (await Service(f, folder).GetAsync(id, 5, default))!; await Service(f, folder).ValidateAsync(id, Request(view, key), default);
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default); return (await Service(f, folder).GetAsync(id, 5, default))!;
    }
    [PostgresFrameFact]
    public async Task Combined_view_real_validation_approval_and_audit_preserve_source_and_import_rights()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var id = await Prepare(f, folder); var source = File.ReadAllBytes(folder.Source); var stamp = File.GetLastWriteTimeUtc(folder.Source);
        var view = (await Service(f, folder).GetAsync(id, 1, default))!; Assert.True(view.Snapshot.IsReady); Assert.True(view.NeedsValidation); Assert.Equal(6, view.Snapshot.Media.Streams.Count); Assert.Equal(3, view.Snapshot.Media.AudioIndex); Assert.Single(view.Snapshot.Samples);
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).DecideAsync(id, Decision(view), default));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Service(f, folder).ValidateAsync(id, Request(view), default))); Assert.Single(results.Select(x => x!.Validation.Id).Distinct());
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).ValidateAsync(id, Request(view) with { MaxAttempts = 1 }, default));
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        view = (await Service(f, folder).GetAsync(id, 1, default))!; Assert.True(view.LatestValidation!.Validation.Passed); Assert.Empty(view.LatestValidation.Issues); Assert.False(view.NeedsValidation);
        var approved = (await Service(f, folder).DecideAsync(id, Decision(view), default))!; Assert.Equal(PreparationStatus.Approved, approved.EffectiveStatus);
        Assert.Equal(new[] { "created", "validation-passed", "approved" }, (await Store(f).AuditAsync(id, 50, default)).OrderBy(x => x.Sequence).Select(x => x.Action));
        var approvalAudit = (await Store(f).AuditAsync(id, 50, default)).Single(x => x.Action == "approved"); Assert.Equal(view.Snapshot.Binding, approvalAudit.Binding); Assert.Equal(view.Snapshot.Binding, view.LatestValidation.Validation.Binding);
        Assert.Equal(new[] { "created", "started", "succeeded" }, (await f.Store.AuditAsync(lease.JobId, default)).Select(x => x.Action)); Assert.Equal(source, File.ReadAllBytes(folder.Source)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(folder.Source));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details);
    }
    [PostgresFrameFact]
    public async Task Incomplete_preparation_can_be_rejected_with_notes_and_no_audio_choice_can_be_validated()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var id = await Prepare(f, folder, artifacts: false); var view = (await Service(f, folder).GetAsync(id, 5, default))!;
        Assert.False(view.Snapshot.IsReady); Assert.Contains("scenes-required", view.Snapshot.ReadinessIssues); await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).ValidateAsync(id, Request(view), default));
        var rejected = (await Service(f, folder).DecideAsync(id, Decision(view, PreparationStatus.Rejected), default))!; Assert.Equal(PreparationStatus.Rejected, rejected.EffectiveStatus);
        await Assert.ThrowsAsync<PreparationException>(() => Service(f, folder).DecideAsync(id, Decision(rejected, PreparationStatus.Rejected) with { Notes = "" }, default));
        await using var silentF = new DatabaseFixture(); await silentF.Initialize(); using var noAudio = new Folder(); var other = await Prepare(silentF, noAudio, audio: false);
        var valid = await Validate(silentF, noAudio, other); Assert.Null(valid.Snapshot.Audio); Assert.True(valid.LatestValidation!.Validation.Passed); Assert.Equal(PreparationStatus.Approved, (await Service(silentF, noAudio).DecideAsync(other, Decision(valid), default))!.EffectiveStatus);
    }
    [PostgresFrameFact]
    public async Task Concurrent_decisions_conflict_and_trigger_faults_roll_back_state_and_audit()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var id = await Prepare(f, folder); var view = await Validate(f, folder, id);
        var command = Decision(view); var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => { try { await Service(f, folder).DecideAsync(id, command, default); return true; } catch (ImportConflictException) { return false; } })); Assert.Single(attempts, x => x);
        view = (await Service(f, folder).GetAsync(id, 5, default))!; var version = view.Review!.Version;
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION video.reject_review_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'intentional audit fault'; END; $$;
            CREATE TRIGGER reject_review_audit BEFORE INSERT ON video."PreparationAudits" FOR EACH ROW EXECUTE FUNCTION video.reject_review_audit();
            """);
        var auditCount = (await Store(f).AuditAsync(id, 100, default)).Count; await Assert.ThrowsAsync<DbUpdateException>(() => Service(f, folder).DecideAsync(id, Decision(view, PreparationStatus.Rejected), default));
        Assert.Equal(version, (await Service(f, folder).GetAsync(id, 5, default))!.Review!.Version); Assert.Equal(auditCount, (await Store(f).AuditAsync(id, 100, default)).Count);
    }
    [PostgresFrameFact]
    public async Task Missing_corrupt_artifacts_fail_real_validation_and_make_prior_approval_stale()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var id = await Prepare(f, folder); var valid = await Validate(f, folder, id); await Service(f, folder).DecideAsync(id, Decision(valid), default);
        var view = (await Service(f, folder).GetAsync(id, 5, default))!; var audio = view.Snapshot.Audio!; var wav = Path.Combine(folder.Output, "audio-v1", audio.Id.ToString("N"), audio.ArtifactToken!.Value.ToString("N") + ".wav"); var bytes = File.ReadAllBytes(wav); bytes[44] ^= 1; File.WriteAllBytes(wav, bytes);
        var frames = view.Snapshot.Frames!; var png = Path.Combine(folder.Output, "frames-v1", frames.Id.ToString("N"), frames.ArtifactToken!.Value.ToString("N") + ".ready", "0000.png"); File.Delete(png);
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).DecideAsync(id, Decision(view), default));
        var invalid = await Validate(f, folder, id, "invalid-files"); Assert.False(invalid.LatestValidation!.Validation.Passed); Assert.Equal(JobStatus.Succeeded, invalid.LatestValidation.Job.Status);
        Assert.Contains(invalid.LatestValidation.Issues, x => x.Target == "audio" && x.Code == "audio-artifact-content-changed"); Assert.Contains(invalid.LatestValidation.Issues, x => x.Target == "frame-0" && x.Code == "frame-artifact-unavailable");
        Assert.Equal(PreparationStatus.Stale, invalid.EffectiveStatus); Assert.True(invalid.NeedsValidation); Assert.DoesNotContain(folder.Root, invalid.LatestValidation.Validation.IssuesJson!);
    }
    [PostgresFrameFact]
    public async Task Selection_new_artifacts_and_source_changes_invalidate_bound_review_or_validation()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var id = await Prepare(f, folder); var view = await Validate(f, folder, id); await Service(f, folder).DecideAsync(id, Decision(view), default);
        var video = new SelectedVideoAccess(new PostgresStreamStore(f.Factory, f.Clock), folder.Verifier); var source = (await video.GetAsync(id, default))!;
        var frames = new PostgresFrameStore(f.Factory, video, folder.Frames, f.Clock); var scenes = new PostgresSceneStore(f.Factory, video, f.Clock); f.Clock.Advance(TimeSpan.FromSeconds(1));
        await new FrameService(frames, scenes, video, folder.Frames).CreateAsync(view.Snapshot.Scene!.Id, new("new-frame-batch", "operator", 3, 1), default); var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await new FrameExecutor(frames, video, folder.Frames, folder.FrameExtractor).ExecuteAsync(lease, default); var changed = (await Service(f, folder).GetAsync(id, 5, default))!;
        Assert.NotEqual(view.Snapshot.SnapshotHash, changed.Snapshot.SnapshotHash); Assert.Equal(PreparationStatus.Stale, changed.EffectiveStatus); Assert.True(changed.NeedsValidation);
        var original = File.ReadAllBytes(folder.Source); var stamp = File.GetLastWriteTimeUtc(folder.Source); var altered = original.ToArray(); altered[0] ^= 1; File.WriteAllBytes(folder.Source, altered); File.SetLastWriteTimeUtc(folder.Source, stamp);
        var failed = await Validate(f, folder, id, "source-changed"); Assert.False(failed.LatestValidation!.Validation.Passed); Assert.Contains(failed.LatestValidation.Issues, x => x.Target == "source" && x.Code == "stream-source-content-changed"); File.WriteAllBytes(folder.Source, original); File.SetLastWriteTimeUtc(folder.Source, stamp);
        var pending = (await Service(f, folder).ValidateAsync(id, Request(failed, "stale-choice"), default))!; lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var selection = (await streams.GetAsync(id, default))!.Selection!; await new StreamService(streams, folder.Verifier).SelectAsync(id, new(selection.Version, 0, 3, null, "changed"), default);
        await Assert.ThrowsAsync<ImportConflictException>(() => Executor(f, folder).ExecuteAsync(lease, default)); Assert.Null((await Store(f).ValidationAsync(pending.Validation.Id, default))!.Validation.CompletedAt);
        Assert.Equal(PreparationStatus.Stale, (await Service(f, folder).GetAsync(id, 5, default))!.EffectiveStatus);
    }
    [PostgresFrameFact]
    public async Task Held_files_cancellation_pause_expiry_recovery_and_report_faults_protect_decisions()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var id = await Prepare(f, folder); var view = (await Service(f, folder).GetAsync(id, 5, default))!;
        await Service(f, folder).ValidateAsync(id, Request(view), default); var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        await using (var check = await folder.Checker(f).CheckAsync(view.Snapshot, default))
        {
            Assert.Empty(check.Issues); if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => new FileStream(folder.Source, FileMode.Open, FileAccess.Write).Dispose());
            var job = (await f.Store.GetAsync(lease.JobId, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Pause, job.Version, default);
            await Assert.ThrowsAsync<JobConflictException>(() => Store(f).CompleteAsync(lease, view.Snapshot, check.Issues, default)); job = (await f.Store.GetAsync(job.Id, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Resume, job.Version, default);
        }
        var expired = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!; f.Clock.Advance(TimeSpan.FromSeconds(16)); await Assert.ThrowsAsync<JobConflictException>(() => Executor(f, folder).ExecuteAsync(expired, default));
        Assert.Null(await f.Store.ClaimAsync("recover", TimeSpan.FromSeconds(30), default)); f.Clock.Advance(TimeSpan.FromSeconds(10)); var current = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(f, folder).ExecuteAsync(current, cancelled.Token));
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION video.reject_validation_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW."Action" = 'validation-passed' THEN RAISE EXCEPTION 'intentional fault'; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER reject_validation_audit BEFORE INSERT ON video."PreparationAudits" FOR EACH ROW EXECUTE FUNCTION video.reject_validation_audit();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Executor(f, folder).ExecuteAsync(current, default)); Assert.Null((await Service(f, folder).GetAsync(id, 5, default))!.LatestValidation); Assert.Equal(JobStatus.Running, (await f.Store.GetAsync(current.JobId, default))!.Status);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_validation_audit ON video.\"PreparationAudits\"; DROP FUNCTION video.reject_validation_audit();"); await Executor(f, folder).ExecuteAsync(current, default);
        Assert.True((await Service(f, folder).GetAsync(id, 5, default))!.LatestValidation!.Validation.Passed);
    }
    [PostgresFrameFact]
    public async Task Changes_during_validation_and_approval_do_not_commit_stale_report_or_decision()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var id = await Prepare(f, folder); var view = (await Service(f, folder).GetAsync(id, 5, default))!;
        await Service(f, folder).ValidateAsync(id, Request(view), default); var validationLease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        async Task NewFrames(string key, int count)
        {
            var video = new SelectedVideoAccess(new PostgresStreamStore(f.Factory, f.Clock), folder.Verifier); var frames = new PostgresFrameStore(f.Factory, video, folder.Frames, f.Clock); var scenes = new PostgresSceneStore(f.Factory, video, f.Clock); f.Clock.Advance(TimeSpan.FromSeconds(1));
            await new FrameService(frames, scenes, video, folder.Frames).CreateAsync(view.Snapshot.Scene!.Id, new(key, "operator", count, 1), default); var lease = (await f.Store.ClaimAsync("other-worker", TimeSpan.FromSeconds(30), default))!;
            await new FrameExecutor(frames, video, folder.Frames, folder.FrameExtractor).ExecuteAsync(lease, default);
        }
        await using (var check = await folder.Checker(f).CheckAsync(view.Snapshot, default))
        {
            Assert.Empty(check.Issues); await NewFrames("during-validation", 3); await Assert.ThrowsAsync<ImportConflictException>(() => Store(f).CompleteAsync(validationLease, view.Snapshot, check.Issues, default));
        }
        var old = await Store(f).WorkAsync(validationLease.JobId, default); Assert.Null(old.CompletedAt); Assert.DoesNotContain(await f.Store.AuditAsync(validationLease.JobId, default), x => x.Action == "succeeded");
        view = await Validate(f, folder, id, "fresh"); var checker = new AfterCheck(folder.Checker(f), () => NewFrames("during-approval", 2));
        await Assert.ThrowsAsync<ImportConflictException>(() => new PreparationService(Store(f), folder.Media(f), checker).DecideAsync(id, Decision(view), default));
        Assert.DoesNotContain(await Store(f).AuditAsync(id, 100, default), x => x.Action == "approved"); Assert.Null((await Service(f, folder).GetAsync(id, 5, default))!.Review!.DecidedAt);
    }
    [PostgresFrameFact]
    public async Task Api_protects_reports_and_decisions_and_does_not_create_generic_validation_jobs()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var id = await Prepare(f, folder); var view = (await Service(f, folder).GetAsync(id, 5, default))!;
        using var api = f.Api(new() { ["Media:Folders:library"] = folder.Library, ["Media:StabilityMilliseconds"] = "100", ["Audio:Root"] = folder.Output, ["Frames:Root"] = folder.Output }); using var client = DatabaseFixture.Client(api);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/media/imports/{id}/preparation")).StatusCode); Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/media/imports/{id}/preparation/decision", Decision(view) with { Decision = PreparationStatus.Pending })).StatusCode);
        var response = await client.PostAsJsonAsync($"/api/media/imports/{id}/preparation/validations", Request(view)); Assert.Equal(HttpStatusCode.Created, response.StatusCode); var location = response.Headers.Location!; f.Clock.Advance(TimeSpan.FromMinutes(1)); var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(location)).StatusCode); view = (await Service(f, folder).GetAsync(id, 5, default))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/media/imports/{id}/preparation/decision", Decision(view))).StatusCode); Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/media/imports/{id}/preparation/decision", Decision(view))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/media/imports/{id}/preparation/audit")).StatusCode); Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/jobs", new CreateJob("video.preparation-validation", "orphan"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key"); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(location)).StatusCode);
    }
    [PostgresFrameFact]
    public async Task Additive_upgrade_preserves_completed_scene_audio_frame_artifacts_without_invented_reviews()
    {
        await using var f = new DatabaseFixture(); await f.Initialize("20261012215959_VideoFrameSampling"); using var folder = new Folder(); var id = await Prepare(f, folder);
        var fileHashes = Directory.GetFiles(folder.Output, "*", SearchOption.AllDirectories).ToDictionary(x => x, x => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(x))));
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.MigrateAsync(); await db.Database.MigrateAsync(); Assert.Empty(await db.PreparationReviews.ToListAsync()); Assert.Empty(await db.PreparationValidations.ToListAsync()); Assert.Empty(await db.PreparationAudits.ToListAsync());
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count()); Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details); Assert.True((await Service(f, folder).GetAsync(id, 5, default))!.Snapshot.IsReady);
        foreach (var entry in fileHashes) Assert.Equal(entry.Value, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry.Key)))); Assert.Single(await db.FrameSamples.ToListAsync()); Assert.Single(await db.AudioExtractions.ToListAsync());
    }
    sealed class AfterCheck(IPreparationChecker inner, Func<Task> change) : IPreparationChecker
    {
        public async Task<IPreparationCheck> CheckAsync(PreparationSnapshot snapshot, CancellationToken ct)
        {
            var check = await inner.CheckAsync(snapshot, ct); try { await change(); return check; } catch { await check.DisposeAsync(); throw; }
        }
    }
    sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "recap_preparation_" + Guid.NewGuid().ToString("N")); public string Library => Path.Combine(Root, "library"); public string Output => Path.Combine(Root, "outputs"); public string Source => Path.Combine(Library, "movie.mkv");
        public MediaInspector Inspector { get; } public MediaSourceVerifier Verifier { get; } public LocalAudioArtifacts Audio { get; } public LocalFrameArtifacts Frames { get; } public FfmpegAudioExtractor AudioExtractor { get; } public FfmpegFrameExtractor FrameExtractor { get; }
        public Folder()
        {
            Directory.CreateDirectory(Library); Directory.CreateDirectory(Output); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-multistream.mkv"), Source);
            var media = Options.Create(new MediaOptions { Folders = new() { ["library"] = Library }, StabilityMilliseconds = 100, FfprobePath = Environment.GetEnvironmentVariable("TEST_FFPROBE")! }); Inspector = new(media, new(media)); Verifier = new(Inspector, media);
            var audio = Options.Create(new AudioOptions { Root = Output, FfmpegPath = Environment.GetEnvironmentVariable("TEST_FFMPEG")! }); Audio = new(audio, media); AudioExtractor = new(audio);
            var frames = Options.Create(new FrameOptions { Root = Output, FfmpegPath = Environment.GetEnvironmentVariable("TEST_FFMPEG")! }); Frames = new(frames, media); FrameExtractor = new(frames);
        }
        public PreparationMediaAccess Media(DatabaseFixture f) => new(f.Factory, Verifier); public PreparationChecker Checker(DatabaseFixture f) => new(Media(f), Audio, Frames, Options.Create(new PreparationOptions())); public void Dispose() => Directory.Delete(Root, true);
    }
}

