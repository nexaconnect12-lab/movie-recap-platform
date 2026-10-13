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

public sealed class PostgresAudioFactAttribute : FactAttribute
{
    public PostgresAudioFactAttribute()
    {
        if (new[] { "TEST_POSTGRES", "TEST_FFPROBE", "TEST_FFMPEG" }.Any(x => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(x)))) Skip = "Set TEST_POSTGRES, TEST_FFPROBE, TEST_FFMPEG for real audio extraction/database verification.";
    }
}
public sealed class AudioTests
{
    static readonly MediaRights Rights = new("Owned fixture", AuthorizationStatus.Authorized, "Synthetic fixture", "Local tests", "Local", "Fixture", null);
    static SelectedAudioAccess Media(DatabaseFixture f, Folder folder) => new(new PostgresStreamStore(f.Factory, f.Clock), folder.Verifier);
    static PostgresAudioStore Store(DatabaseFixture f, Folder folder) => new(f.Factory, Media(f, folder), folder.Artifacts, f.Clock);
    static AudioService Service(DatabaseFixture f, Folder folder) => new(Store(f, folder), Media(f, folder), folder.Artifacts);
    static AudioExecutor Executor(DatabaseFixture f, Folder folder) => new(Store(f, folder), Media(f, folder), folder.Artifacts, folder.Extractor);
    static RequestAudio Request(SelectedAudioSnapshot source, string key = "audio") => new(key, source.SelectionVersion, "operator", 3);
    static async Task<SelectedAudioSnapshot> ImportAndSelect(DatabaseFixture f, Folder folder, int index = 3)
    {
        var store = new PostgresImportStore(f.Factory, f.Clock);
        var imported = await new ImportService(store, folder.Inspector).CreateAsync(new("owned", "library", "movie.mkv", "Owned source", "operator", Rights, 1), default);
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await new ImportExecutor(store, folder.Inspector).ExecuteAsync(lease, default);
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var view = (await streams.GetAsync(imported.Import.Id, default))!;
        await new StreamService(streams, folder.Verifier).SelectAsync(imported.Import.Id, new(view.Selection!.Version, 0, index, null, "operator"), default);
        return (await Media(f, folder).GetAsync(imported.Import.Id, default))!;
    }
    [PostgresAudioFact]
    public async Task Real_selected_nondefault_stereo_track_becomes_valid_mono_wav_without_source_changes()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var bytes = File.ReadAllBytes(folder.Source); var stamp = File.GetLastWriteTimeUtc(folder.Source);
        var requests = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Service(f, folder).CreateAsync(source.ImportId, Request(source), default)));
        Assert.Single(requests.Select(x => x!.Extraction.Id).Distinct()); var a = requests[0]!.Extraction;
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).CreateAsync(source.ImportId, Request(source) with { MaxAttempts = 1 }, default));
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var result = (await Store(f, folder).GetAsync(a.Id, default))!; Assert.Equal(JobStatus.Succeeded, result.Job.Status); Assert.True(result.IsCurrentSelection); Assert.Equal(3, result.Extraction.AudioIndex);
        await using var read = (await Service(f, folder).DownloadAsync(a.Id, default))!; using var wav = new MemoryStream(); await read.CopyToAsync(wav);
        var extracted = wav.ToArray(); Assert.InRange(result.Extraction.DurationSeconds!.Value, 0.99, 1.01); Assert.Equal(16000, result.Extraction.Samples);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(extracted)), result.Extraction.ArtifactSha256); Assert.True(Tone(extracted, 880) > Tone(extracted, 440) * 10, "Wrong audio stream decoded.");
        await ProbeWav(folder.PathFor(result.Extraction), result.Extraction.Samples!.Value);
        Assert.Equal(new[] { "created", "started", "succeeded" }, (await f.Store.AuditAsync(lease.JobId, default)).Select(x => x.Action));
        Assert.Equal(0, await Store(f, folder).CleanupAsync(a.Id, default)); Assert.Empty(Directory.GetFiles(folder.ArtifactRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(bytes, File.ReadAllBytes(folder.Source)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(folder.Source));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details);
    }
    [PostgresAudioFact]
    public async Task Selection_changes_reject_stale_completion_and_keep_successful_history_downloadable()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var first = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var second = (await Service(f, folder).CreateAsync(source.ImportId, Request(source, "stale"), default))!; lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await using (var attempt = await folder.Artifacts.BeginAsync(second.Extraction.Id, lease.Token, default))
        {
            var artifact = await attempt.PrepareAsync(await folder.Extractor.ExtractAsync(folder.Source, source, attempt.Output, null, default), default);
            await ChangeSelection(f, folder, source.ImportId, 2);
            await Assert.ThrowsAsync<ImportConflictException>(() => Store(f, folder).CompleteAsync(lease, source, attempt, artifact, default));
        }
        await Store(f, folder).CleanupAsync(second.Extraction.Id, default); Assert.Null((await Store(f, folder).GetAsync(second.Extraction.Id, default))!.Extraction.CompletedAt);
        Assert.Equal("audio-selection-changed", (await Assert.ThrowsAsync<AudioException>(() => Executor(f, folder).ExecuteAsync(lease, default))).Code);
        Assert.False((await Store(f, folder).GetAsync(first.Extraction.Id, default))!.IsCurrentSelection); await using var old = (await Service(f, folder).DownloadAsync(first.Extraction.Id, default))!; Assert.True(old.Length > 44);
        Assert.Equal(first.Extraction.Id, (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!.Extraction.Id);
    }
    [PostgresAudioFact]
    public async Task Audit_fault_after_promotion_rolls_back_success_and_cleanup_removes_only_unreferenced_output()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var a = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION jobs.reject_audio_success() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW."Action" = 'succeeded' THEN RAISE EXCEPTION 'intentional fault'; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER reject_audio_success BEFORE INSERT ON jobs."AuditLogs" FOR EACH ROW EXECUTE FUNCTION jobs.reject_audio_success();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Executor(f, folder).ExecuteAsync(lease, default));
        Assert.Null((await Store(f, folder).GetAsync(a.Extraction.Id, default))!.Extraction.CompletedAt); Assert.Equal(JobStatus.Running, (await f.Store.GetAsync(lease.JobId, default))!.Status);
        Assert.Empty(Directory.GetFiles(folder.ArtifactRoot, "*.wav", SearchOption.AllDirectories)); Assert.Empty(Directory.GetFiles(folder.ArtifactRoot, "*.tmp", SearchOption.AllDirectories));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_audio_success ON jobs.\"AuditLogs\"; DROP FUNCTION jobs.reject_audio_success();");
        await Executor(f, folder).ExecuteAsync(lease, default); Assert.Equal(JobStatus.Succeeded, (await Store(f, folder).GetAsync(a.Extraction.Id, default))!.Job.Status);
    }
    [PostgresAudioFact]
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
        Assert.Single(Directory.GetFiles(folder.ArtifactRoot, "*.wav", SearchOption.AllDirectories));
    }
    [PostgresAudioFact]
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
    [PostgresAudioFact]
    public async Task Changed_source_corrupt_artifact_and_resource_limits_never_publish_partial_audio()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var a = (await Service(f, folder).CreateAsync(source.ImportId, Request(source), default))!; var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        var original = File.ReadAllBytes(folder.Source); var stamp = File.GetLastWriteTimeUtc(folder.Source); var changed = original.ToArray(); changed[0] ^= 1; File.WriteAllBytes(folder.Source, changed); File.SetLastWriteTimeUtc(folder.Source, stamp);
        Assert.Equal("stream-source-content-changed", (await Assert.ThrowsAsync<ImportException>(() => Executor(f, folder).ExecuteAsync(lease, default))).Code);
        File.WriteAllBytes(folder.Source, original); File.SetLastWriteTimeUtc(folder.Source, stamp);
        var limit = new FfmpegAudioExtractor(Options.Create(new AudioOptions { FfmpegPath = Environment.GetEnvironmentVariable("TEST_FFMPEG")!, MaxPcmBytes = 1024 }));
        Assert.Equal("audio-output-limit", (await Assert.ThrowsAsync<AudioException>(() => new AudioExecutor(Store(f, folder), Media(f, folder), folder.Artifacts, limit).ExecuteAsync(lease, default))).Code);
        Assert.Empty(Directory.GetFiles(folder.ArtifactRoot, "*.tmp", SearchOption.AllDirectories)); await Executor(f, folder).ExecuteAsync(lease, default);
        var completed = (await Store(f, folder).GetAsync(a.Extraction.Id, default))!.Extraction; using (var change = new FileStream(folder.PathFor(completed), FileMode.Open, FileAccess.Write)) { change.Position = 44; var first = original[0]; change.WriteByte(first); }
        await Assert.ThrowsAsync<AudioException>(() => Service(f, folder).DownloadAsync(a.Extraction.Id, default)); Assert.Equal(0, await Store(f, folder).CleanupAsync(a.Extraction.Id, default));
    }
    [PostgresAudioFact]
    public async Task Api_validates_auth_selection_idempotency_and_serves_verified_artifacts_only()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        using var api = f.Api(new() { ["Media:Folders:library"] = folder.Library, ["Audio:Root"] = folder.ArtifactRoot }); using var client = DatabaseFixture.Client(api);
        var route = $"/api/media/imports/{source.ImportId}/audio-extractions";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, Request(source) with { MaxAttempts = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, Request(source) with { SelectionVersion = Guid.NewGuid() })).StatusCode);
        var response = await client.PostAsJsonAsync(route, Request(source)); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = Guid.Parse(response.Headers.Location!.ToString().Split('/')[^1]); Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/video/audio-extractions/{id}/artifact")).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); // API uses system time; bring the test worker clock beyond creation availability.
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await Executor(f, folder).ExecuteAsync(lease, default);
        var download = await client.GetAsync($"/api/video/audio-extractions/{id}/artifact"); Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal("audio/wav", download.Content.Headers.ContentType!.MediaType);
        var bytes = await download.Content.ReadAsByteArrayAsync(); Assert.True(Tone(bytes, 880) > Tone(bytes, 440) * 10); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/video/audio-extractions/{id}/cleanup", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/jobs", new CreateJob("video.audio-extraction", "orphan"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key"); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/video/audio-extractions/{id}/artifact")).StatusCode);
        await ChangeSelection(f, folder, source.ImportId, null); await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).CreateAsync(source.ImportId, Request(source, "none"), default));
    }
    [PostgresAudioFact]
    public async Task Additive_upgrade_preserves_previous_streams_and_scenes_without_invented_audio()
    {
        await using var f = new DatabaseFixture(); await f.Initialize("20261012210554_VideoSceneAnalysis"); using var folder = new Folder(); var source = await ImportAndSelect(f, folder);
        var scenes = new PostgresSceneStore(f.Factory, new SelectedVideoAccess(new PostgresStreamStore(f.Factory, f.Clock), folder.Verifier), f.Clock);
        var created = (await new SceneService(scenes, new SelectedVideoAccess(new PostgresStreamStore(f.Factory, f.Clock), folder.Verifier)).CreateAsync(source.ImportId, new("existing-scenes", source.SelectionVersion, "actor"), default))!;
        // Queued prior-scene records are preserved; no successful processing is fabricated.
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        Assert.Empty(await db.AudioExtractions.ToListAsync()); Assert.Equal(created.Analysis.Id, (await db.SceneAnalyses.SingleAsync()).Id); Assert.Equal(source.SelectionVersion, (await Media(f, folder).GetAsync(source.ImportId, default))!.SelectionVersion);
        Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details); Assert.Equal(8, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }
    static async Task ChangeSelection(DatabaseFixture f, Folder folder, Guid id, int? audio)
    {
        var streams = new PostgresStreamStore(f.Factory, f.Clock); var view = (await streams.GetAsync(id, default))!;
        await new StreamService(streams, folder.Verifier).SelectAsync(id, new(view.Selection!.Version, 0, audio, null, "changed"), default);
    }
    public static double Tone(byte[] wav, double frequency)
    {
        double re = 0, im = 0; var count = (wav.Length - 44) / 2;
        for (var i = 0; i < count; i++) { var sample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2, 2)); var phase = 2 * Math.PI * frequency * i / 16000; re += sample * Math.Cos(phase); im += sample * Math.Sin(phase); }
        return Math.Sqrt(re * re + im * im);
    }
    static async Task ProbeWav(string path, long samples)
    {
        using var process = new Process { StartInfo = new(Environment.GetEnvironmentVariable("TEST_FFPROBE")!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path }) process.StartInfo.ArgumentList.Add(argument);
        process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await Task.WhenAll(stdout, stderr, process.WaitForExitAsync()); Assert.Equal(0, process.ExitCode);
        using var doc = JsonDocument.Parse(await stdout); var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray(); Assert.Single(streams); var audio = streams[0];
        Assert.Equal("audio", audio.GetProperty("codec_type").GetString()); Assert.Equal("pcm_s16le", audio.GetProperty("codec_name").GetString()); Assert.Equal("16000", audio.GetProperty("sample_rate").GetString()); Assert.Equal(1, audio.GetProperty("channels").GetInt32()); Assert.Equal(samples, audio.GetProperty("duration_ts").GetInt64());
    }
    sealed class ExpireOnPromotion(IAudioAttempt inner, ManualClock clock) : IAudioAttempt
    {
        public Guid ExtractionId => inner.ExtractionId; public Guid Token => inner.Token; public Stream Output => inner.Output;
        public Task<AudioArtifact> PrepareAsync(ExtractedAudio a, CancellationToken ct) => inner.PrepareAsync(a, ct);
        public async Task PromoteAsync(AudioArtifact a, CancellationToken ct) { await inner.PromoteAsync(a, ct); clock.Advance(TimeSpan.FromSeconds(16)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "recap_audio_" + Guid.NewGuid().ToString("N"));
        public string Library => Path.Combine(Root, "library"); public string ArtifactRoot => Path.Combine(Root, "outputs"); public string Source => Path.Combine(Library, "movie.mkv");
        public MediaInspector Inspector { get; } public MediaSourceVerifier Verifier { get; } public LocalAudioArtifacts Artifacts { get; } public FfmpegAudioExtractor Extractor { get; }
        public Folder()
        {
            Directory.CreateDirectory(Library); Directory.CreateDirectory(ArtifactRoot); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-multistream.mkv"), Source);
            var media = Options.Create(new MediaOptions { Folders = new() { ["library"] = Library }, StabilityMilliseconds = 100, FfprobePath = Environment.GetEnvironmentVariable("TEST_FFPROBE")! });
            Inspector = new(media, new(media)); Verifier = new(Inspector, media); var audio = Options.Create(new AudioOptions { Root = ArtifactRoot, FfmpegPath = Environment.GetEnvironmentVariable("TEST_FFMPEG")! }); Artifacts = new(audio, media); Extractor = new(audio);
        }
        public string PathFor(AudioExtraction a) => Path.Combine(ArtifactRoot, "audio-v1", a.Id.ToString("N"), a.ArtifactToken!.Value.ToString("N") + ".wav");
        public void Dispose() => Directory.Delete(Root, true);
    }
}

