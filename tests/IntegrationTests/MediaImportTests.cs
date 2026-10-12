using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Jobs.Domain;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
using Xunit;
namespace MovieRecap.IntegrationTests;

public sealed class FfprobeFactAttribute : FactAttribute
{
    public FfprobeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_FFPROBE"))) Skip = "Set TEST_FFPROBE to a real FFprobe executable; media probing is not emulated.";
    }
}
public sealed class PostgresMediaFactAttribute : FactAttribute
{
    public PostgresMediaFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_POSTGRES")) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_FFPROBE")))
            Skip = "Set TEST_POSTGRES and TEST_FFPROBE for real database/media tests.";
    }
}
public sealed class MediaImportTests
{
    static readonly MediaRights Rights = new("Test fixture author", AuthorizationStatus.Authorized, "Synthetic lavfi fixture generated for this repository",
        "Local tests", "Local", "Movie Recap test fixture", null);
    static readonly InspectedMedia Inspected = new(new string('a', 64), 123, new("avi", 1, "mpeg4", 160, 90, 10, null, null, null));
    static ManualImport Request(string key, string path = "movie.mp4") => new(key, "library", path, "Owned synthetic test", "integration-test", Rights);
    static PostgresImportStore Store(DatabaseFixture f) => new(f.Factory, f.Clock);

    [PostgresFact]
    public async Task Migration_upgrades_existing_phase1_jobs_and_is_repeatable()
    {
        await using var f = new DatabaseFixture(); await f.Initialize("20261012114553_AuditOrdering");
        var job = await f.Store.CreateAsync(new("foundation.database-check", "before-upgrade"), default);
        var persistedBeforeUpgrade = await f.Store.GetAsync(job.Id, default);
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
            Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync()).Count());
        }
        Assert.Equal(persistedBeforeUpgrade, await f.Store.GetAsync(job.Id, default)); Assert.Single(await f.Store.AuditAsync(job.Id, default));
        var result = await Store(f).CreateAsync(Request("after-upgrade"), default); Assert.Equal(Rights, result.Rights);
    }

    [PostgresFact]
    public async Task Concurrent_request_creation_is_atomic_idempotent_and_keeps_rights()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); var store = Store(f);
        var request = Request(new string('x', 128));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.CreateAsync(request, default)));
        Assert.Single(results.Select(x => x.Import.Id).Distinct()); Assert.Single(results.Select(x => x.Job.Id).Distinct());
        Assert.All(results, r => Assert.Equal(Rights, r.Rights));
        await using var db = await f.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Imports.CountAsync()); Assert.Equal(1, await db.ImportRights.CountAsync()); Assert.Equal(1, await db.Jobs.CountAsync()); Assert.Equal(1, await db.Audits.CountAsync());
        await Assert.ThrowsAsync<ImportConflictException>(() => store.CreateAsync(request with { Source = "Changed source" }, default));
        await Assert.ThrowsAsync<ImportConflictException>(() => store.CreateAsync(request with { Rights = Rights with { Authorization = AuthorizationStatus.Denied } }, default));
        Assert.Equal(JobStatus.Queued, (await store.GetAsync(results[0].Import.Id, default))!.Job.Status);
    }
    [PostgresFact]
    public async Task Concurrent_duplicate_completion_has_one_asset_and_distinct_rights()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); var store = Store(f);
        var first = await store.CreateAsync(Request("first"), default);
        var denied = Rights with { Authorization = AuthorizationStatus.Denied, Owner = "Different source owner" };
        var second = await store.CreateAsync(Request("second", "copy.mp4") with { Rights = denied }, default);
        var leases = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => f.Store.ClaimAsync("worker" + i, TimeSpan.FromSeconds(30), default)));
        await Task.WhenAll(leases.Select(l => store.CompleteAsync(l!, Inspected, default)));
        var a = (await store.GetAsync(first.Import.Id, default))!; var b = (await store.GetAsync(second.Import.Id, default))!;
        Assert.Equal(a.Import.AssetId, b.Import.AssetId); Assert.NotEqual(a.Import.IsDuplicate, b.Import.IsDuplicate);
        Assert.Equal(Rights, a.Rights); Assert.Equal(denied, b.Rights);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(1, await db.Assets.CountAsync());
        Assert.All(new[] { a, b }, r => { Assert.Equal(JobStatus.Succeeded, r.Job.Status); Assert.Equal(100, r.Job.Progress); Assert.NotNull(r.Import.CompletedAt); });
        Assert.Equal(new[] { "created", "started", "succeeded" }, (await f.Store.AuditAsync(first.Job.Id, default)).Select(x => x.Action));
    }
    [PostgresFact]
    public async Task Duplicate_conflict_rolls_back_import_job_success_and_audit()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); var store = Store(f);
        await store.CreateAsync(Request("first"), default);
        var first = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await store.CompleteAsync(first, Inspected, default);
        var second = await store.CreateAsync(Request("second"), default);
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await Assert.ThrowsAsync<ImportException>(() => store.CompleteAsync(lease, Inspected with { SizeBytes = 456 }, default));
        var result = (await store.GetAsync(second.Import.Id, default))!; Assert.Null(result.Asset); Assert.Null(result.Import.CompletedAt); Assert.Equal(JobStatus.Running, result.Job.Status);
        Assert.DoesNotContain(await f.Store.AuditAsync(lease.JobId, default), a => a.Action == "succeeded");
    }
    [PostgresFact]
    public async Task Pause_cancel_expiry_and_old_tokens_cannot_commit_media()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); var store = Store(f);
        var item = await store.CreateAsync(Request("fenced"), default);
        var stale = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        var job = (await f.Store.GetAsync(item.Job.Id, default))!;
        await f.Store.CommandAsync(job.Id, JobCommand.Pause, job.Version, default);
        await Assert.ThrowsAsync<JobConflictException>(() => store.CompleteAsync(stale, Inspected, default));
        job = (await f.Store.GetAsync(job.Id, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Resume, job.Version, default);
        var expired = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        f.Clock.Advance(TimeSpan.FromSeconds(16));
        await Assert.ThrowsAsync<JobConflictException>(() => store.CompleteAsync(expired, Inspected, default));
        Assert.Null(await f.Store.ClaimAsync("recovery", TimeSpan.FromSeconds(15), default)); f.Clock.Advance(TimeSpan.FromSeconds(10));
        var current = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        await Assert.ThrowsAsync<JobConflictException>(() => store.CompleteAsync(stale, Inspected, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompleteAsync(current, Inspected, cancelled.Token));
        job = (await f.Store.GetAsync(job.Id, default))!; await f.Store.CommandAsync(job.Id, JobCommand.Cancel, job.Version, default);
        await Assert.ThrowsAsync<JobConflictException>(() => store.CompleteAsync(current, Inspected, default));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(0, await db.Assets.CountAsync());
        Assert.Null((await store.GetAsync(item.Import.Id, default))!.Import.AssetId);
    }
    [PostgresFact]
    public async Task Unknown_rights_can_be_recorded_without_becoming_authorized()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); var store = Store(f);
        var unknown = new MediaRights("Unknown", AuthorizationStatus.Unknown, "", "", "", "", null);
        var result = await store.CreateAsync(Request("unknown") with { Rights = unknown }, default);
        Assert.Equal(unknown, (await store.GetAsync(result.Import.Id, default))!.Rights);
    }
    [PostgresFact]
    public async Task Rights_expiry_uses_database_precision_for_idempotent_replay()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); var store = Store(f);
        var expiry = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var request = Request("expiry") with { Rights = Rights with { ExpiresAt = expiry } };
        var first = await store.CreateAsync(request, default); var replay = await store.CreateAsync(request, default);
        Assert.Equal(first.Import.Id, replay.Import.Id); Assert.Equal(first.Rights, replay.Rights);
        Assert.Equal(0, replay.Rights.ExpiresAt!.Value.Ticks % 10);
    }
    [PostgresFact]
    public async Task Api_validates_imports_security_and_does_not_enqueue_invalid_requests()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new MediaFolder();
        File.WriteAllBytes(Path.Combine(folder.Path, "movie.mp4"), [1, 2, 3]);
        using var api = f.Api(new() { ["Media:Folders:library"] = folder.Path }); using var client = DatabaseFixture.Client(api);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/media/imports", Request("unsafe", "../outside.mp4"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/media/imports", Request("rights") with { Rights = Rights with { LicenseEvidence = "" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/jobs", new CreateJob("media.manual-import", "orphan"))).StatusCode);
        var response = await client.PostAsJsonAsync("/api/media/imports", Request("api")); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportResponse>(JsonOptions);
        Assert.NotNull(result); Assert.Equal(JobStatus.Queued, result.Job.Status);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/media/imports/{result.Import.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/media/imports", Request("api") with { Source = "changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/media/imports/{Guid.NewGuid()}")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key"); Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/media/imports", Request("unauthorized"))).StatusCode);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(1, await db.Imports.CountAsync()); Assert.Equal(1, await db.Jobs.CountAsync());
    }
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    sealed record ImportIdentity(Guid Id);
    sealed record ImportResponse(ImportIdentity Import, MediaRights Rights, JobView Job);

    [FfprobeFact]
    public async Task Real_ffprobe_extracts_all_supported_containers_and_preserves_sources()
    {
        using var folder = new MediaFolder(); var inspector = folder.Inspector();
        foreach (var extension in new[] { "mp4", "mkv", "mov", "avi" })
        {
            var name = "owned-sample." + extension; folder.Copy(name); var path = Path.Combine(folder.Path, name);
            var before = File.ReadAllBytes(path); var stamp = File.GetLastWriteTimeUtc(path);
            var media = await inspector.InspectAsync("library", name, default);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(before)), media.Sha256); Assert.Equal(before.Length, media.SizeBytes);
            Assert.Equal(160, media.Metadata.Width); Assert.Equal(90, media.Metadata.Height); Assert.Equal(10, media.Metadata.FrameRate);
            Assert.InRange(media.Metadata.DurationSeconds, 0.9, 1.3); Assert.NotNull(media.Metadata.AudioCodec);
            Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        }
        folder.Copy("owned-silent.mp4"); var silent = await inspector.InspectAsync("library", "owned-silent.mp4", default); Assert.Null(silent.Metadata.AudioCodec);
    }
    [FfprobeFact]
    public async Task Real_probe_rejects_corrupt_and_mislabeled_media()
    {
        using var folder = new MediaFolder(); var inspector = folder.Inspector();
        File.WriteAllText(Path.Combine(folder.Path, "corrupt.mp4"), "this is not video");
        Assert.Equal("ffprobe-rejected-media", (await Assert.ThrowsAsync<ImportException>(() => inspector.InspectAsync("library", "corrupt.mp4", default))).Code);
        folder.Copy("owned-sample.mkv"); File.Copy(Path.Combine(folder.Path, "owned-sample.mkv"), Path.Combine(folder.Path, "mislabeled.mp4"));
        Assert.Equal("media-container-mismatch", (await Assert.ThrowsAsync<ImportException>(() => inspector.InspectAsync("library", "mislabeled.mp4", default))).Code);
    }
    [PostgresMediaFact]
    public async Task Real_import_executor_persists_probe_metadata_deduplicates_and_recovers_after_failure()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new MediaFolder(); folder.Copy("owned-sample.mp4");
        File.Copy(Path.Combine(folder.Path, "owned-sample.mp4"), Path.Combine(folder.Path, "copy.mp4"));
        var store = Store(f); var executor = new ImportExecutor(store, folder.Inspector());
        var first = await store.CreateAsync(Request("real", "owned-sample.mp4"), default);
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await executor.ExecuteAsync(lease, default);
        var second = await store.CreateAsync(Request("copy", "copy.mp4"), default);
        lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await executor.ExecuteAsync(lease, default);
        var result = (await store.GetAsync(second.Import.Id, default))!; Assert.True(result.Import.IsDuplicate);
        Assert.Equal((await store.GetAsync(first.Import.Id, default))!.Import.AssetId, result.Import.AssetId); Assert.Equal(JobStatus.Succeeded, result.Job.Status);
        File.WriteAllText(Path.Combine(folder.Path, "broken.mp4"), "not media");
        var broken = await store.CreateAsync(Request("broken", "broken.mp4") with { MaxAttempts = 1 }, default);
        lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        var failure = await Assert.ThrowsAsync<ImportException>(() => executor.ExecuteAsync(lease, default)); await f.Store.FailAsync(lease, failure.Code, default);
        var failed = (await store.GetAsync(broken.Import.Id, default))!; Assert.Equal(JobStatus.Failed, failed.Job.Status); Assert.Null(failed.Asset);
        File.Copy(Path.Combine(folder.Path, "owned-sample.mp4"), Path.Combine(folder.Path, "broken.mp4"), overwrite: true);
        await f.Store.CommandAsync(failed.Job.Id, JobCommand.Retry, failed.Job.Version, default);
        lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await executor.ExecuteAsync(lease, default);
        Assert.Equal(JobStatus.Succeeded, (await store.GetAsync(broken.Import.Id, default))!.Job.Status);
    }
    sealed class MediaFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "recap_owned_" + Guid.NewGuid().ToString("N"));
        public MediaFolder() => Directory.CreateDirectory(Path);
        public void Copy(string name) => File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name), System.IO.Path.Combine(Path, name));
        public MediaInspector Inspector()
        {
            var options = Options.Create(new MediaOptions { Folders = new() { ["library"] = Path }, StabilityMilliseconds = 100, FfprobePath = Environment.GetEnvironmentVariable("TEST_FFPROBE")! });
            return new(options, new(options));
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
