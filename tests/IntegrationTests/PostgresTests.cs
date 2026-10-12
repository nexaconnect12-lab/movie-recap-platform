using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Jobs.Domain;
using Npgsql;
using Xunit;
namespace MovieRecap.IntegrationTests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_POSTGRES"))) Skip = "Set TEST_POSTGRES to a local PostgreSQL admin connection; real database tests are not emulated."; }
}
public sealed class DatabaseFixture : IAsyncDisposable
{
    const string Key = "integration-only-key-at-least-32-characters";
    readonly string admin;
    readonly string database = "recap_test_" + Guid.NewGuid().ToString("N");
    public string Connection { get; }
    public IDbContextFactory<PlatformDbContext> Factory { get; }
    public PostgresJobStore Store { get; }
    public ManualClock Clock { get; } = new();
    public DatabaseFixture()
    {
        admin = Environment.GetEnvironmentVariable("TEST_POSTGRES")!;
        var builder = new NpgsqlConnectionStringBuilder(admin) { Database = database };
        Connection = builder.ConnectionString;
        Factory = new PooledDbContextFactory(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(Connection).Options);
        Store = new(Factory, Clock);
    }
    public async Task Initialize(string? migration = null)
    {
        await using var db = new NpgsqlConnection(admin); await db.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", db); await cmd.ExecuteNonQueryAsync();
        await using var context = await Factory.CreateDbContextAsync(); await context.GetService<IMigrator>().MigrateAsync(migration);
    }
    public WebApplicationFactory<Program> Api(Dictionary<string, string?>? settings = null) => new TestApi(Connection, Key, settings);
    public static HttpClient Client(WebApplicationFactory<Program> app) { var c = app.CreateClient(); c.DefaultRequestHeaders.Add("X-Api-Key", Key); return c; }
    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var db = new NpgsqlConnection(admin); await db.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", db); await cmd.ExecuteNonQueryAsync();
    }
    sealed class TestApi(string connection, string key, Dictionary<string, string?>? settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Platform", connection).UseSetting("Security:ApiKey", key).UseSetting("AllowedHosts", "*");
            if (settings is not null) foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
        }
    }
    sealed class PooledDbContextFactory(DbContextOptions<PlatformDbContext> options) : IDbContextFactory<PlatformDbContext>
    { public PlatformDbContext CreateDbContext() => new(options); }
}
public sealed class ManualClock : TimeProvider
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan span) => now += span;
}
public class PostgresTests
{
    [PostgresFact]
    public async Task Migration_idempotency_and_concurrent_claims_are_real()
    {
        await using var f = new DatabaseFixture(); await f.Initialize();
        var command = new CreateJob("foundation.database-check", "duplicate");
        var jobs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Store.CreateAsync(command, default)));
        Assert.Single(jobs.Select(x => x.Id).Distinct());
        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => f.Store.ClaimAsync("worker" + i, TimeSpan.FromSeconds(30), default)));
        var lease = Assert.Single(leases, x => x is not null)!;
        await f.Store.CompleteAsync(lease, default);
        Assert.Equal(JobStatus.Succeeded, (await f.Store.GetAsync(lease.JobId, default))!.Status);
        Assert.Equal(new[] { "created", "started", "succeeded" }, (await f.Store.AuditAsync(lease.JobId, default)).Select(x => x.Action));
    }
    [PostgresFact]
    public async Task Lease_recovery_retry_exhaustion_and_fencing()
    {
        await using var f = new DatabaseFixture(); await f.Initialize();
        var j = await f.Store.CreateAsync(new("foundation.database-check", "recovery", 2), default);
        var first = (await f.Store.ClaimAsync("first", TimeSpan.FromSeconds(15), default))!;
        f.Clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Null(await f.Store.ClaimAsync("recovery", TimeSpan.FromSeconds(15), default));
        Assert.Equal(JobStatus.RetryWait, (await f.Store.GetAsync(j.Id, default))!.Status);
        await Assert.ThrowsAsync<JobConflictException>(() => f.Store.CompleteAsync(first, default));
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = (await f.Store.ClaimAsync("second", TimeSpan.FromSeconds(15), default))!;
        await f.Store.FailAsync(second, "test", default);
        Assert.Equal(JobStatus.Failed, (await f.Store.GetAsync(j.Id, default))!.Status);
    }
    [PostgresFact]
    public async Task Api_validates_secures_and_supports_versioned_commands()
    {
        await using var f = new DatabaseFixture(); await f.Initialize();
        using var app = f.Api(); using var client = DatabaseFixture.Client(app);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/jobs", new CreateJob("movie.processing", "test"))).StatusCode);
        var created = await client.PostAsJsonAsync("/api/jobs", new CreateJob("foundation.database-check", "api"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        // Default HttpClient JSON options don't parse string enums: use the shared explicit options.
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var job = (await created.Content.ReadFromJsonAsync<JobView>(json))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/jobs/{job.Id}/pause", new { job.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/jobs/{job.Id}/resume", new { job.Version })).StatusCode);
        var current = (await client.GetFromJsonAsync<JobView>($"/api/jobs/{job.Id}", json))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/jobs/{job.Id}/resume", new { current.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/jobs/{job.Id}/audit")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/jobs")).StatusCode);
    }
    [PostgresFact]
    public async Task Pause_revokes_a_claim_and_mismatched_idempotency_is_rejected()
    {
        await using var f = new DatabaseFixture(); await f.Initialize();
        var j = await f.Store.CreateAsync(new("foundation.database-check", "pause", 3), default);
        await Assert.ThrowsAsync<JobConflictException>(() => f.Store.CreateAsync(new("foundation.database-check", "pause", 4), default));
        var lease = (await f.Store.ClaimAsync("worker", TimeSpan.FromSeconds(30), default))!;
        var current = (await f.Store.GetAsync(j.Id, default))!;
        await f.Store.CommandAsync(j.Id, JobCommand.Pause, current.Version, default);
        await Assert.ThrowsAsync<JobConflictException>(() => f.Store.CompleteAsync(lease, default));
        Assert.Equal(JobStatus.Paused, (await f.Store.GetAsync(j.Id, default))!.Status);
    }
}

