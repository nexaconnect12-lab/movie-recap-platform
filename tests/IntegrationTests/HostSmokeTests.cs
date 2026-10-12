using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MovieRecap.Jobs.Application;
using MovieRecap.Jobs.Domain;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
using Xunit;
using Xunit.Abstractions;
namespace MovieRecap.IntegrationTests;

public sealed class HostSmokeTests(ITestOutputHelper output)
{
    [PostgresMediaFact]
    public async Task Separate_api_and_worker_import_duplicate_and_report_probe_failure()
    {
        await using var database = new DatabaseFixture(); await database.Initialize();
        var folder = Path.Combine(Path.GetTempPath(), "recap_hosts_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-sample.mp4"), Path.Combine(folder, "movie.mp4"));
            File.Copy(Path.Combine(folder, "movie.mp4"), Path.Combine(folder, "copy.mp4"));
            File.WriteAllText(Path.Combine(folder, "corrupt.mp4"), "intentionally invalid test fixture");
            var original = File.ReadAllBytes(Path.Combine(folder, "movie.mp4"));
            using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            using var api = Start("MovieRecap.Api", database.Connection, folder, port);
            using var worker = Start("MovieRecap.Worker", database.Connection, folder, port);
            // Drain both pipes concurrently so child output cannot deadlock execution.
            var drains = new[] { api.StandardOutput.ReadToEndAsync(), api.StandardError.ReadToEndAsync(), worker.StandardOutput.ReadToEndAsync(), worker.StandardError.ReadToEndAsync() };
            try
            {
                using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Add("X-Api-Key", "integration-only-key-at-least-32-characters");
                var deadline = DateTime.UtcNow.AddSeconds(40); var ready = false;
                while (DateTime.UtcNow < deadline && !api.HasExited)
                {
                    try { ready = (await client.GetAsync("/health/ready")).IsSuccessStatusCode; } catch (HttpRequestException) { } catch (TaskCanceledException) { }
                    if (ready) break; await Task.Delay(100);
                }
                Assert.True(ready, "Separate API failed readiness.");
                var first = await Import(client, "movie.mp4"); var second = await Import(client, "copy.mp4");
                Assert.Equal(JobStatus.Succeeded, first.Job.Status); Assert.Equal(JobStatus.Succeeded, second.Job.Status);
                Assert.Equal(first.Import.AssetId, second.Import.AssetId); Assert.True(second.Import.IsDuplicate);
                var failure = await Import(client, "corrupt.mp4"); Assert.Equal(JobStatus.Failed, failure.Job.Status); Assert.Equal("ffprobe-rejected-media", failure.Job.ErrorCode); Assert.Null(failure.Import.AssetId);
                Assert.Equal(original, File.ReadAllBytes(Path.Combine(folder, "movie.mp4")));
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), first.Asset!.Sha256);
                var audit = await client.GetFromJsonAsync<List<AuditResponse>>($"/api/jobs/{first.Job.Id}/audit");
                Assert.Equal(new[] { "created", "started", "succeeded" }, audit!.Select(x => x.Action));
                output.WriteLine($"Separate-host smoke: import {first.Import.Id}, job {first.Job.Id}, duplicate {second.Import.Id}, corrupt failure {failure.Job.ErrorCode}; original bytes preserved.");
            }
            finally
            {
                foreach (var process in new[] { api, worker })
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await Task.WhenAll(drains);
                foreach (var drain in drains) output.WriteLine((await drain)[..Math.Min((await drain).Length, 12000)]);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
    static Process Start(string name, string connection, string folder, int port)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MovieRecapPlatform.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var artifacts = Environment.GetEnvironmentVariable("ArtifactsPath");
        var project = name == "MovieRecap.Api" ? "src/Api" : "src/Workers/ProcessingWorker";
        var dll = artifacts is null ? Path.Combine(root.FullName, project, "bin", "Debug", "net10.0", name + ".dll") : Path.Combine(artifacts, "bin", name, "debug", name + ".dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll);
        start.Environment["ConnectionStrings__Platform"] = connection;
        start.Environment["Security__ApiKey"] = "integration-only-key-at-least-32-characters";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["Media__Folders__library"] = folder;
        start.Environment["Media__FfprobePath"] = Environment.GetEnvironmentVariable("TEST_FFPROBE");
        start.Environment["Media__StabilityMilliseconds"] = "100"; start.Environment["Worker__PollSeconds"] = "1";
        start.Environment["Logging__FilePath"] = "";
        return Process.Start(start)!;
    }
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    static async Task<ImportResponse> Import(HttpClient client, string file)
    {
        var rights = new MediaRights("Fixture author", AuthorizationStatus.Authorized, "Synthetic local fixture", "Tests", "Local", "Test fixture", null);
        var response = await client.PostAsJsonAsync("/api/media/imports", new ManualImport(Guid.NewGuid().ToString("N"), "library", file, "Owned fixture", "host-smoke", rights, 1));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<ImportResponse>(JsonOptions))!; var deadline = DateTime.UtcNow.AddSeconds(30);
        while (item.Job.Status is not (JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100); item = (await client.GetFromJsonAsync<ImportResponse>($"/api/media/imports/{item.Import.Id}", JsonOptions))!;
        }
        return item;
    }
    sealed record ImportIdentity(Guid Id, Guid? AssetId, bool IsDuplicate);
    sealed record AssetResponse(string Sha256);
    sealed record ImportResponse(ImportIdentity Import, JobView Job, AssetResponse? Asset);
    sealed record AuditResponse(string Action);
}
