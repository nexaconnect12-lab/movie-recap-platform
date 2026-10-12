using System.Net;
using System.Net.Http.Json;
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

public sealed class StreamTests
{
    static readonly MediaRights Rights = new("Fixture owner", AuthorizationStatus.Authorized, "Owned synthetic fixture", "Tests", "Local", "Fixture", null);
    static PostgresStreamStore Store(DatabaseFixture f) => new(f.Factory, f.Clock);
    static StreamService Service(DatabaseFixture f, Folder folder) => new(Store(f), folder.Verifier);
    static async Task<ImportView> Import(DatabaseFixture f, Folder folder, string name)
    {
        var store = new PostgresImportStore(f.Factory, f.Clock); var view = await new ImportService(store, folder.Inspector).CreateAsync(new(name, "library", name, "Owned source", "operator", Rights, 1), default);
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await new ImportExecutor(store, folder.Inspector).ExecuteAsync(lease, default);
        return (await store.GetAsync(view.Import.Id, default))!;
    }
    // Writes the prior-phase schema representation using actual probe/hash data; it never fabricates inventory.
    static async Task<(MovieImport Import, InspectedMedia Media)> Legacy(DatabaseFixture f, Folder folder, string name)
    {
        var media = await folder.Inspector.InspectAsync("library", name, default);
        var job = await f.Store.CreateAsync(new("media.manual-import", "legacy:" + name, 1), default);
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await f.Store.CompleteAsync(lease, default);
        var asset = MediaAsset.Create(media.Sha256, media.SizeBytes, media.Metadata, f.Clock.GetUtcNow());
        var import = MovieImport.Create(job.Id, "legacy:" + name, "library", name, "Legacy source", "operator", 1, f.Clock.GetUtcNow()); import.Complete(asset.Id, false, f.Clock.GetUtcNow());
        await using var db = await f.Factory.CreateDbContextAsync(); db.Assets.Add(asset); db.Imports.Add(import); db.ImportRights.Add(new() { ImportId = import.Id, Details = Rights }); await db.SaveChangesAsync();
        return (import, media);
    }
    [FfprobeFact]
    public async Task Real_probe_preserves_multiple_videos_audio_languages_subtitles_and_cover_art()
    {
        using var folder = new Folder(); folder.Copy("owned-multistream.mkv"); folder.Copy("owned-cover.mp4"); folder.Copy("owned-silent.mp4");
        var media = await folder.Inspector.InspectAsync("library", "owned-multistream.mkv", default); var streams = media.Streams!;
        Assert.Equal(6, streams.Count); Assert.Equal(2, streams.Count(x => x.Kind == MediaStreamKind.Video)); Assert.Equal(3, streams.Count(x => x.Kind == MediaStreamKind.Audio));
        Assert.True(streams.Single(x => x.Index == 0).IsDefault); Assert.Equal("eng", streams.Single(x => x.Index == 2).Language); Assert.Equal("jpn", streams.Single(x => x.Index == 3).Language);
        Assert.Null(streams.Single(x => x.Index == 4).Language); Assert.Null(streams.Single(x => x.Index == 0).DurationSeconds); Assert.True(streams.Single(x => x.Index == 5).IsForced);
        var cover = (await folder.Inspector.InspectAsync("library", "owned-cover.mp4", default)).Streams!;
        Assert.Single(cover, x => x.IsAttachedPicture && !x.IsSelectable); Assert.Single(cover, x => x.Kind == MediaStreamKind.Video && x.IsSelectable);
        Assert.DoesNotContain((await folder.Inspector.InspectAsync("library", "owned-silent.mp4", default)).Streams!, x => x.Kind == MediaStreamKind.Audio);
    }
    [PostgresMediaFact]
    public async Task Duplicate_content_shares_inventory_but_selections_and_audits_are_per_import()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); folder.Copy("owned-multistream.mkv"); folder.Copy("owned-multistream.mkv", "copy.mkv");
        var first = await Import(f, folder, "owned-multistream.mkv"); var second = await Import(f, folder, "copy.mkv");
        var a = (await Store(f).GetAsync(first.Import.Id, default))!; var b = (await Store(f).GetAsync(second.Import.Id, default))!;
        Assert.Equal(a.InventoryVersion, b.InventoryVersion); Assert.Null(a.Selection!.VideoIndex); Assert.Null(b.Selection!.VideoIndex);
        await Service(f, folder).SelectAsync(first.Import.Id, new(a.Selection.Version, 0, 2, 5, "first-operator"), default);
        await Service(f, folder).SelectAsync(second.Import.Id, new(b.Selection.Version, 1, 3, null, "second-operator"), default);
        Assert.Equal(2, (await Store(f).GetAsync(first.Import.Id, default))!.Selection!.AudioIndex); Assert.Equal(3, (await Store(f).GetAsync(second.Import.Id, default))!.Selection!.AudioIndex);
        Assert.Equal("first-operator", Assert.Single(await Store(f).AuditAsync(first.Import.Id, default)).RequestedBy);
        Assert.Equal("second-operator", Assert.Single(await Store(f).AuditAsync(second.Import.Id, default)).RequestedBy);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(1, await db.StreamInventories.CountAsync()); Assert.Equal(6, await db.MediaStreams.CountAsync()); Assert.Equal(2, await db.StreamSelections.CountAsync());
        Assert.Equal(Rights, (await db.ImportRights.FirstAsync()).Details);
    }
    [PostgresMediaFact]
    public async Task Concurrent_selection_requires_current_version_and_creates_only_one_audit()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); folder.Copy("owned-multistream.mkv"); var imported = await Import(f, folder, "owned-multistream.mkv");
        var initial = (await Store(f).GetAsync(imported.Import.Id, default))!.Selection!;
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
        {
            try { await Service(f, folder).SelectAsync(imported.Import.Id, new(initial.Version, 0, 2 + i, null, "operator" + i), default); return true; }
            catch (ImportConflictException) { return false; }
        }));
        Assert.Single(results, x => x); Assert.Single(results, x => !x); var audit = Assert.Single(await Store(f).AuditAsync(imported.Import.Id, default));
        var latest = (await Store(f).GetAsync(imported.Import.Id, default))!.Selection!; Assert.Equal(initial.Version, audit.BeforeVersion); Assert.Equal(latest.Version, audit.AfterVersion);
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).SelectAsync(imported.Import.Id, new(initial.Version, 0, null, null, "stale"), default));
    }
    [PostgresMediaFact]
    public async Task Selection_audit_failure_rolls_back_state_and_cancelled_commands_have_no_effect()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); folder.Copy("owned-multistream.mkv"); var imported = await Import(f, folder, "owned-multistream.mkv");
        var initial = (await Store(f).GetAsync(imported.Import.Id, default))!.Selection!; var request = new SelectStreams(initial.Version, 0, 3, 5, "operator");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(f, folder).SelectAsync(imported.Import.Id, request, cancelled.Token));
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION media.reject_selection_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'intentional test fault'; END; $$;
            CREATE TRIGGER reject_selection_audit BEFORE INSERT ON media."StreamSelectionAudits" FOR EACH ROW EXECUTE FUNCTION media.reject_selection_audit();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Service(f, folder).SelectAsync(imported.Import.Id, request, default));
        var unchanged = (await Store(f).GetAsync(imported.Import.Id, default))!.Selection!; Assert.Equal(initial.Version, unchanged.Version); Assert.Null(unchanged.VideoIndex); Assert.Empty(await Store(f).AuditAsync(imported.Import.Id, default));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_selection_audit ON media.\"StreamSelectionAudits\"; DROP FUNCTION media.reject_selection_audit();");
        Assert.NotNull(await Service(f, folder).SelectAsync(imported.Import.Id, request, default));
    }
    [PostgresMediaFact]
    public async Task Changed_source_blocks_selection_even_with_same_size_and_timestamp()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); folder.Copy("owned-multistream.mkv"); var imported = await Import(f, folder, "owned-multistream.mkv");
        var view = (await Store(f).GetAsync(imported.Import.Id, default))!; var path = Path.Combine(folder.Root, "owned-multistream.mkv"); var stamp = File.GetLastWriteTimeUtc(path); var bytes = File.ReadAllBytes(path); bytes[0] ^= 1;
        File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal("stream-source-content-changed", (await Assert.ThrowsAsync<ImportException>(() => Service(f, folder).SelectAsync(imported.Import.Id, new(view.Selection!.Version, 0, 2, null, "operator"), default))).Code);
        Assert.Empty(await Store(f).AuditAsync(imported.Import.Id, default)); Assert.Null((await Store(f).GetAsync(imported.Import.Id, default))!.Selection!.VideoIndex);
        Assert.Equal(view.InventoryVersion, (await Store(f).GetAsync(imported.Import.Id, default))!.InventoryVersion);
    }
    [PostgresMediaFact]
    public async Task Legacy_reinspection_is_real_idempotent_and_does_not_change_import_rights_or_asset()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); folder.Copy("owned-multistream.mkv"); var legacy = await Legacy(f, folder, "owned-multistream.mkv");
        Assert.Equal("Uninspected", (await Store(f).GetAsync(legacy.Import.Id, default))!.Status);
        var inspections = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Service(f, folder).InspectAsync(legacy.Import.Id, new("legacy-probe", 2), default)));
        Assert.Single(inspections.Select(x => x!.Job.Id).Distinct()); await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).InspectAsync(legacy.Import.Id, new("legacy-probe", 3), default));
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!; await new StreamInspectionExecutor(Store(f), folder.Inspector).ExecuteAsync(lease, default);
        var view = (await Store(f).GetAsync(legacy.Import.Id, default))!; Assert.Equal("Inspected", view.Status); Assert.Equal(6, view.Streams.Count); Assert.NotNull(view.Selection);
        Assert.Equal(legacy.Import.AssetId, view.AssetId); Assert.Equal(new[] { "created", "started", "succeeded" }, (await f.Store.AuditAsync(lease.JobId, default)).Select(x => x.Action));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(1, await db.Assets.CountAsync()); Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details);
        Assert.Equal(legacy.Import.JobId, (await db.Imports.SingleAsync()).JobId);
    }
    [PostgresMediaFact]
    public async Task Reinspection_lease_expiry_pause_cancel_and_recovery_fence_inventory_writes()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); folder.Copy("owned-multistream.mkv"); var legacy = await Legacy(f, folder, "owned-multistream.mkv");
        var queued = (await Service(f, folder).InspectAsync(legacy.Import.Id, new("fence", 3), default))!; var stale = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        var running = (await f.Store.GetAsync(queued.Job.Id, default))!; await f.Store.CommandAsync(running.Id, JobCommand.Pause, running.Version, default);
        await Assert.ThrowsAsync<JobConflictException>(() => Store(f).CompleteInspectionAsync(stale, legacy.Media, default));
        var paused = (await f.Store.GetAsync(running.Id, default))!; await f.Store.CommandAsync(paused.Id, JobCommand.Resume, paused.Version, default);
        var expired = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!; f.Clock.Advance(TimeSpan.FromSeconds(16));
        await Assert.ThrowsAsync<JobConflictException>(() => Store(f).CompleteInspectionAsync(expired, legacy.Media, default));
        Assert.Null(await f.Store.ClaimAsync("recover", TimeSpan.FromSeconds(15), default)); f.Clock.Advance(TimeSpan.FromSeconds(10));
        var current = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        await Assert.ThrowsAsync<JobConflictException>(() => Store(f).CompleteInspectionAsync(stale, legacy.Media, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(f).CompleteInspectionAsync(current, legacy.Media, cancelled.Token));
        var latest = (await f.Store.GetAsync(running.Id, default))!; await f.Store.CommandAsync(latest.Id, JobCommand.Cancel, latest.Version, default);
        await Assert.ThrowsAsync<JobConflictException>(() => Store(f).CompleteInspectionAsync(current, legacy.Media, default));
        Assert.Equal("Uninspected", (await Store(f).GetAsync(legacy.Import.Id, default))!.Status);
        await Service(f, folder).InspectAsync(legacy.Import.Id, new("recovery", 1), default); current = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(15), default))!;
        await new StreamInspectionExecutor(Store(f), folder.Inspector).ExecuteAsync(current, default); Assert.Equal("Inspected", (await Store(f).GetAsync(legacy.Import.Id, default))!.Status);
    }
    [PostgresMediaFact]
    public async Task Changed_valid_media_fails_reinspection_without_fabricating_inventory()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); folder.Copy("owned-multistream.mkv"); var legacy = await Legacy(f, folder, "owned-multistream.mkv");
        await Service(f, folder).InspectAsync(legacy.Import.Id, new("changed", 1), default); var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-sample.mkv"), Path.Combine(folder.Root, "owned-multistream.mkv"), true);
        var error = await Assert.ThrowsAsync<ImportException>(() => new StreamInspectionExecutor(Store(f), folder.Inspector).ExecuteAsync(lease, default)); Assert.Equal("stream-source-content-changed", error.Code);
        await f.Store.FailAsync(lease, error.Code, default); Assert.Equal(JobStatus.Failed, (await f.Store.GetAsync(lease.JobId, default))!.Status);
        Assert.Equal("Uninspected", (await Store(f).GetAsync(legacy.Import.Id, default))!.Status);
    }
    [PostgresMediaFact]
    public async Task Additive_migration_preserves_legacy_data_and_marks_it_uninspected()
    {
        await using var f = new DatabaseFixture(); await f.Initialize("20261012131558_MediaDiscoveryInbox"); using var folder = new Folder(); folder.Copy("owned-multistream.mkv"); var legacy = await Legacy(f, folder, "owned-multistream.mkv");
        await using var db = await f.Factory.CreateDbContextAsync(); await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        Assert.Equal(legacy.Import.Id, (await db.Imports.SingleAsync()).Id); Assert.Equal(Rights, (await db.ImportRights.SingleAsync()).Details);
        var view = (await Store(f).GetAsync(legacy.Import.Id, default))!; Assert.Equal("Uninspected", view.Status); Assert.Empty(view.Streams); Assert.Null(view.Selection);
        await Assert.ThrowsAsync<ImportConflictException>(() => Service(f, folder).SelectAsync(legacy.Import.Id, new(Guid.NewGuid(), 0, 2, null, "operator"), default));
        await Service(f, folder).InspectAsync(legacy.Import.Id, new("upgrade", 1), default); var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        await new StreamInspectionExecutor(Store(f), folder.Inspector).ExecuteAsync(lease, default); Assert.Equal(6, (await Store(f).GetAsync(legacy.Import.Id, default))!.Streams.Count);
    }
    [PostgresMediaFact]
    public async Task Api_secures_inventory_selection_audit_and_inspection_and_rejects_invalid_choices()
    {
        await using var f = new DatabaseFixture(); await f.Initialize(); using var folder = new Folder(); folder.Copy("owned-cover.mp4"); var imported = await Import(f, folder, "owned-cover.mp4");
        using var api = f.Api(new() { ["Media:Folders:library"] = folder.Root, ["Media:StabilityMilliseconds"] = "100" }); using var client = DatabaseFixture.Client(api);
        var view = (await Store(f).GetAsync(imported.Import.Id, default))!; var main = Assert.Single(view.Streams, x => x.IsSelectable); var cover = Assert.Single(view.Streams, x => x.IsAttachedPicture);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/media/imports/{imported.Import.Id}/streams")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-selection", new { view.Selection!.Version, RequestedBy = "operator" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/media/imports/{Guid.NewGuid()}/streams")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-selection", new SelectStreams(view.Selection!.Version, cover.Index, null, null, "operator"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-selection", new SelectStreams(view.Selection!.Version, main.Index, null, null, "operator"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-selection", new SelectStreams(view.Selection!.Version, main.Index, null, null, "operator"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/media/imports/{imported.Import.Id}/stream-selection/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-inspections", new ReinspectStreams("api", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/jobs", new CreateJob("media.stream-inspection", "orphan"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key"); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/media/imports/{imported.Import.Id}/streams")).StatusCode);
    }
    sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "recap_streams_" + Guid.NewGuid().ToString("N"));
        public MediaInspector Inspector { get; }
        public MediaSourceVerifier Verifier { get; }
        public Folder()
        {
            Directory.CreateDirectory(Root); var options = Options.Create(new MediaOptions { Folders = new() { ["library"] = Root }, StabilityMilliseconds = 100, FfprobePath = Environment.GetEnvironmentVariable("TEST_FFPROBE") ?? "ffprobe" });
            Inspector = new(options, new(options)); Verifier = new(Inspector, options);
        }
        public void Copy(string name, string? destination = null) => File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), Path.Combine(Root, destination ?? name));
        public void Dispose() => Directory.Delete(Root, true);
    }
}
