using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
using Xunit;
namespace MovieRecap.IntegrationTests;

public sealed class DiscoveryTests
{
    static readonly MediaRights Rights = new("Owned fixture", AuthorizationStatus.Authorized, "Synthetic fixture", "Local tests", "Local", "Fixture", null);
    static SubmitDiscovery Request(MediaDiscovery candidate) => new(candidate.Version, "Owned fixture source", "test-operator", Rights, 1);
    static PostgresDiscoveryStore Store(DatabaseFixture f) => new(f.Factory, new(f.Factory, f.Clock));
    static readonly TimeSpan Stability = TimeSpan.FromMilliseconds(100);
    static async Task<MediaDiscovery> Ready(DatabaseFixture f, Folder folder)
    {
        var store = Store(f); await store.ReconcileAsync(await folder.Files.ScanAsync("library", default), Stability, default);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await store.ReconcileAsync(await folder.Files.ScanAsync("library", default), Stability, default);
        return Assert.Single(await store.ListAsync(100, DiscoveryStatus.Ready, default));
    }
    [PostgresFact]
    public async Task Concurrent_scans_restart_reconciliation_and_dismissal_keep_one_current_revision()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(f.Clock); folder.Write("movie.mp4", [1]);
        var scan = await folder.Files.ScanAsync("library", default);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store(f).ReconcileAsync(scan, Stability, default)));
        var initial = Assert.Single(await Store(f).ListAsync(100, null, default)); Assert.Equal(DiscoveryStatus.Observing, initial.Status);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await Store(f).ReconcileAsync(await folder.Files.ScanAsync("library", default), Stability, default);
        var ready = Assert.Single(await Store(f).ListAsync(100, null, default)); Assert.Equal(initial.Id, ready.Id); Assert.Equal(DiscoveryStatus.Ready, ready.Status);
        await Store(f).DismissAsync(ready.Id, ready.Version, default);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await Store(f).ReconcileAsync(await folder.Files.ScanAsync("library", default), Stability, default);
        Assert.Equal(DiscoveryStatus.Dismissed, (await Store(f).GetAsync(ready.Id, default))!.Status);
        folder.Write("movie.mp4", [1, 2]); f.Clock.Advance(TimeSpan.FromSeconds(1)); await Store(f).ReconcileAsync(await folder.Files.ScanAsync("library", default), Stability, default);
        await using var db = await f.Factory.CreateDbContextAsync(); var history = await db.Discoveries.AsNoTracking().ToListAsync(); Assert.Equal(2, history.Count);
        Assert.Single(history, c => c.IsCurrent); Assert.False(history.Single(c => c.Id == initial.Id).IsCurrent); Assert.Equal(DiscoveryStatus.Dismissed, history.Single(c => c.Id == initial.Id).Status);
        Assert.Equal(0, await db.Jobs.CountAsync());
    }
    [PostgresFact]
    public async Task Stale_and_incomplete_scans_do_not_revert_newer_observations_or_invent_deletions()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(f.Clock); folder.Write("movie.mp4", [1]);
        var ready = await Ready(f, folder); var oldScan = await folder.Files.ScanAsync("library", default);
        folder.Write("movie.mp4", [1, 2]); f.Clock.Advance(TimeSpan.FromSeconds(2)); var freshScan = await folder.Files.ScanAsync("library", default);
        await Store(f).ReconcileAsync(freshScan, Stability, default); await Store(f).ReconcileAsync(oldScan, Stability, default);
        var latest = Assert.Single(await Store(f).ListAsync(100, null, default), c => c.IsCurrent); Assert.NotEqual(ready.Id, latest.Id); Assert.Equal(freshScan.Files[0].Fingerprint, latest.Fingerprint);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await Store(f).ReconcileAsync(new("library", f.Clock.GetUtcNow(), [], false, "overflow"), Stability, default);
        Assert.Equal(DiscoveryStatus.Observing, (await Store(f).GetAsync(latest.Id, default))!.Status);
        File.Delete(Path.Combine(folder.Path, "movie.mp4")); f.Clock.Advance(TimeSpan.FromSeconds(1));
        await Store(f).ReconcileAsync(await folder.Files.ScanAsync("library", default), Stability, default);
        Assert.Equal(DiscoveryStatus.Unavailable, (await Store(f).GetAsync(latest.Id, default))!.Status);
        await Store(f).ReconcileAsync(oldScan, Stability, default); Assert.Equal(DiscoveryStatus.Unavailable, (await Store(f).GetAsync(latest.Id, default))!.Status);
    }
    [PostgresFact]
    public async Task Concurrent_submission_and_replay_create_one_job_and_preserve_rights()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(f.Clock); folder.Write("movie.mp4", [1, 2]);
        var ready = await Ready(f, folder); var service = new DiscoveryService(Store(f), folder.Files); var request = Request(ready);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.SubmitAsync(ready.Id, request, default)));
        Assert.Single(results.Select(r => r!.Import.Id).Distinct()); Assert.Single(results.Select(r => r!.Job.Id).Distinct());
        var submitted = (await Store(f).GetAsync(ready.Id, default))!; Assert.Equal(DiscoveryStatus.Submitted, submitted.Status); Assert.Equal(ready.Version, submitted.SubmittedVersion);
        Assert.Equal(ready.Fingerprint, results[0]!.Import.ExpectedFingerprint);
        File.Delete(Path.Combine(folder.Path, "movie.mp4")); Assert.NotNull(await service.SubmitAsync(ready.Id, request, default));
        await Assert.ThrowsAsync<ImportConflictException>(() => service.SubmitAsync(ready.Id, request with { Source = "different" }, default));
        await Assert.ThrowsAsync<ImportConflictException>(() => service.SubmitAsync(ready.Id, request with { Version = submitted.Version }, default));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(1, await db.Jobs.CountAsync()); Assert.Equal(1, await db.Imports.CountAsync()); Assert.Equal(1, await db.ImportRights.CountAsync()); Assert.Equal(1, await db.Audits.CountAsync());
        Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details);
    }
    [PostgresFact]
    public async Task Submission_failure_and_cancellation_leave_no_partial_import_job_or_rights()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(f.Clock); folder.Write("movie.mp4", [1]);
        var ready = await Ready(f, folder); var service = new DiscoveryService(Store(f), folder.Files);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SubmitAsync(ready.Id, Request(ready), cancelled.Token));
        await using var db = await f.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION media.reject_submission() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Status" = 'Submitted' THEN RAISE EXCEPTION 'intentional test fault'; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER reject_submission BEFORE UPDATE ON media."MediaDiscoveries" FOR EACH ROW EXECUTE FUNCTION media.reject_submission();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.SubmitAsync(ready.Id, Request(ready), default));
        Assert.Equal(0, await db.Jobs.CountAsync()); Assert.Equal(0, await db.Imports.CountAsync()); Assert.Equal(0, await db.ImportRights.CountAsync()); Assert.Equal(0, await db.Audits.CountAsync());
        Assert.Equal(DiscoveryStatus.Ready, (await Store(f).GetAsync(ready.Id, default))!.Status);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_submission ON media.\"MediaDiscoveries\"; DROP FUNCTION media.reject_submission();");
        Assert.NotNull(await service.SubmitAsync(ready.Id, Request(ready), default));
    }
    [PostgresFact]
    public async Task Changed_unavailable_stale_and_dismissed_candidates_cannot_be_submitted()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(f.Clock); folder.Write("movie.mp4", [1]);
        var ready = await Ready(f, folder); var service = new DiscoveryService(Store(f), folder.Files);
        await Assert.ThrowsAsync<ImportConflictException>(() => service.SubmitAsync(ready.Id, Request(ready) with { Version = Guid.NewGuid() }, default));
        folder.Write("movie.mp4", [2, 3]); await Assert.ThrowsAsync<ImportConflictException>(() => service.SubmitAsync(ready.Id, Request(ready), default));
        File.Delete(Path.Combine(folder.Path, "movie.mp4")); await Assert.ThrowsAsync<ImportConflictException>(() => service.SubmitAsync(ready.Id, Request(ready), default));
        await Store(f).DismissAsync(ready.Id, ready.Version, default);
        await Assert.ThrowsAsync<ImportConflictException>(() => service.SubmitAsync(ready.Id, Request(ready), default));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(0, await db.Jobs.CountAsync());
    }
    [PostgresMediaFact]
    public async Task Discovery_import_uses_real_probe_and_replacement_after_submission_is_rejected()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(f.Clock); folder.Copy("owned-sample.mp4", "movie.mp4");
        var ready = await Ready(f, folder); var result = (await new DiscoveryService(Store(f), folder.Files).SubmitAsync(ready.Id, Request(ready), default))!;
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        var imports = new PostgresImportStore(f.Factory, f.Clock); await new ImportExecutor(imports, folder.Inspector).ExecuteAsync(lease, default);
        Assert.NotNull((await imports.GetAsync(result.Import.Id, default))!.Asset);
        folder.Copy("owned-sample.mp4", "second.mp4"); f.Clock.Advance(TimeSpan.FromSeconds(1)); var second = await Ready(f, folder);
        await new DiscoveryService(Store(f), folder.Files).SubmitAsync(second.Id, Request(second), default);
        lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; folder.Write("second.mp4", [1, 2, 3]);
        Assert.Equal("discovery-source-changed", (await Assert.ThrowsAsync<ImportException>(() => new ImportExecutor(imports, folder.Inspector).ExecuteAsync(lease, default))).Code);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(1, await db.Assets.CountAsync()); Assert.Null((await imports.GetWorkAsync(lease.JobId, default)).Import.AssetId);
    }
    [PostgresFact]
    public async Task Api_secures_discovery_inbox_validates_rights_and_versions_and_reserves_keys()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(f.Clock); folder.Write("movie.mp4", [1]); var ready = await Ready(f, folder);
        using var api = f.Api(new() { ["Media:Folders:library"] = folder.Path }); using var client = DatabaseFixture.Client(api);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/media/discoveries?status=Ready")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/media/discoveries/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/media/discoveries/{ready.Id}/import", Request(ready) with { Rights = Rights with { LicenseEvidence = "" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/media/discoveries/{ready.Id}/dismiss", new { Version = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/media/imports", new ManualImport("discovery:reserved", "library", "movie.mp4", "source", "operator", Rights))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/media/discoveries/{ready.Id}/import", Request(ready))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/media/discoveries/{ready.Id}/dismiss", new { Version = ready.Version })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key"); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/media/discoveries")).StatusCode);
    }
    [PostgresFact]
    public async Task Upgrade_preserves_existing_manual_import_and_rights_records()
    {
        await using var f = new DatabaseFixture(); await f.Initialize("20261012121403_ManualMediaImport");
        var job = await f.Store.CreateAsync(new("media.manual-import", "legacy-manual", 1), default); var id = Guid.NewGuid(); var now = f.Clock.GetUtcNow();
        await using var db = await f.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO media."MovieImports" ("Id","JobId","IdempotencyKey","Folder","RelativePath","Source","RequestedBy","MaxAttempts","CreatedAt","IsDuplicate")
            VALUES ({id},{job.Id},{"legacy"},{"library"},{"movie.mp4"},{"legacy source"},{"operator"},{1},{now},{false})
            """);
        db.ImportRights.Add(new() { ImportId = id, Details = Rights }); await db.SaveChangesAsync(); await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        var import = await db.Imports.AsNoTracking().SingleAsync(); Assert.Equal(id, import.Id); Assert.Equal("legacy source", import.Source); Assert.Null(import.ExpectedFingerprint);
        Assert.Equal(Rights, (await db.ImportRights.AsNoTracking().SingleAsync()).Details); Assert.Single(await f.Store.AuditAsync(job.Id, default));
        Assert.Empty(await Store(f).ListAsync(100, null, default));
    }
    sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "recap_inbox_" + Guid.NewGuid().ToString("N"));
        public MediaInspector Inspector { get; }
        public DiscoveryFiles Files { get; }
        public Folder(TimeProvider clock)
        {
            Directory.CreateDirectory(Path); var media = Options.Create(new MediaOptions { Folders = new() { ["library"] = Path }, StabilityMilliseconds = 100, FfprobePath = Environment.GetEnvironmentVariable("TEST_FFPROBE") ?? "ffprobe" });
            Inspector = new(media, new(media)); Files = new(Inspector, media, Options.Create(new DiscoveryOptions()), clock);
        }
        public void Write(string name, byte[] bytes) => File.WriteAllBytes(System.IO.Path.Combine(Path, name), bytes);
        public void Copy(string source, string name) => File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", source), System.IO.Path.Combine(Path, name));
        public void Dispose() => Directory.Delete(Path, true);
    }
}
