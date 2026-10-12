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
using MovieRecap.VideoProcessing.Application;
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
    static Process Start(string name, string connection, string folder, int port, bool discovery = false, string? audioRoot = null)
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
        start.Environment["Discovery__Enabled"] = discovery ? "true" : "false";
        start.Environment["Discovery__RescanSeconds"] = "1";
        if (Environment.GetEnvironmentVariable("TEST_SCENE_PYTHON") is { } scenePython) start.Environment["Scenes__PythonPath"] = scenePython;
        if (Environment.GetEnvironmentVariable("TEST_FFMPEG") is { } ffmpeg) start.Environment["Audio__FfmpegPath"] = ffmpeg;
        if (audioRoot is not null) start.Environment["Audio__Root"] = audioRoot; else start.Environment.Remove("Audio__Root");
        return Process.Start(start)!;
    }
    [PostgresMediaFact]
    public async Task Separate_hosts_discover_partial_files_require_rights_and_recover_missed_events_after_restart()
    {
        await using var database = new DatabaseFixture(); await database.Initialize();
        var folder = Path.Combine(Path.GetTempPath(), "recap_discovery_hosts_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var processes = new List<Process>(); var drains = new List<Task<string>>();
        using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        Process StartHost(string name)
        {
            var process = Start(name, database.Connection, folder, port, true); processes.Add(process);
            drains.Add(process.StandardOutput.ReadToEndAsync()); drains.Add(process.StandardError.ReadToEndAsync()); return process;
        }
        try
        {
            var api = StartHost("MovieRecap.Api"); var worker = StartHost("MovieRecap.Worker");
            using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-Api-Key", "integration-only-key-at-least-32-characters");
            var deadline = DateTime.UtcNow.AddSeconds(40); var ready = false;
            while (DateTime.UtcNow < deadline && !api.HasExited)
            {
                try { ready = (await client.GetAsync("/health/ready")).IsSuccessStatusCode; } catch (HttpRequestException) { } catch (TaskCanceledException) { }
                if (ready) break; await Task.Delay(100);
            }
            Assert.True(ready, "Discovery smoke API failed readiness.");
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-sample.mp4"));
            using (var copy = new FileStream(Path.Combine(folder, "movie.mp4"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await copy.WriteAsync(bytes.AsMemory(0, bytes.Length / 2)); await copy.FlushAsync();
                var partial = await WaitDiscovery(client, "movie.mp4", DiscoveryStatus.Observing); Assert.False(partial.IsAvailable);
                await copy.WriteAsync(bytes.AsMemory(bytes.Length / 2)); await copy.FlushAsync();
            }
            var discovered = await WaitDiscovery(client, "movie.mp4", DiscoveryStatus.Ready);
            Assert.Empty((await client.GetFromJsonAsync<List<JobView>>("/api/jobs", JsonOptions))!); // Discovery never fabricates rights/imports.
            var rights = new MediaRights("Fixture owner", AuthorizationStatus.Authorized, "Owned synthetic fixture", "Tests", "Local", "Fixture", null);
            var response = await client.PostAsJsonAsync($"/api/media/discoveries/{discovered.Id}/import", new SubmitDiscovery(discovered.Version, "Owned source", "smoke", rights, 1));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode); var imported = (await response.Content.ReadFromJsonAsync<ImportResponse>(JsonOptions))!;
            deadline = DateTime.UtcNow.AddSeconds(30);
            while (imported.Job.Status == JobStatus.Queued || imported.Job.Status == JobStatus.Running)
            {
                Assert.True(DateTime.UtcNow < deadline, "Discovered import did not finish."); await Task.Delay(100);
                imported = (await client.GetFromJsonAsync<ImportResponse>($"/api/media/imports/{imported.Import.Id}", JsonOptions))!;
            }
            Assert.Equal(JobStatus.Succeeded, imported.Job.Status); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(folder, "movie.mp4")));
            worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync();
            File.WriteAllBytes(Path.Combine(folder, "missed.mp4"), bytes); // No worker/watcher exists to receive this event.
            _ = StartHost("MovieRecap.Worker");
            var recovered = await WaitDiscovery(client, "missed.mp4", DiscoveryStatus.Ready);
            var original = (await client.GetFromJsonAsync<DiscoveryResponse>($"/api/media/discoveries/{discovered.Id}", JsonOptions))!;
            Assert.Equal(DiscoveryStatus.Submitted, original.Status); Assert.Equal(imported.Import.Id, original.ImportId);
            Assert.Single((await client.GetFromJsonAsync<List<JobView>>("/api/jobs", JsonOptions))!);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/media/discoveries/{recovered.Id}/dismiss", new { recovered.Version })).StatusCode);
            output.WriteLine($"Discovery smoke: partial-copy candidate {discovered.Id}, import {imported.Import.Id} succeeded; restart recovered missed candidate {recovered.Id}, original bytes preserved, no automatic jobs.");
        }
        finally
        {
            foreach (var process in processes) { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } process.Dispose(); }
            await Task.WhenAll(drains); foreach (var drain in drains) { var log = await drain; output.WriteLine(log[..Math.Min(log.Length, 8000)]); }
            Directory.Delete(folder, true);
        }
    }
    static async Task<DiscoveryResponse> WaitDiscovery(HttpClient client, string path, DiscoveryStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var candidates = (await client.GetFromJsonAsync<List<DiscoveryResponse>>("/api/media/discoveries?limit=100", JsonOptions))!;
            var result = candidates.SingleOrDefault(c => c.RelativePath == path && c.IsCurrent && c.Status == status);
            if (result is not null) return result;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Discovery smoke did not observe {path} as {status}.");
    }
    sealed record DiscoveryResponse(Guid Id, string RelativePath, DiscoveryStatus Status, bool IsCurrent, bool IsAvailable, Guid Version, Guid? ImportId);
    [PostgresMediaFact]
    public async Task Separate_hosts_inspect_select_audit_and_reinspect_real_streams()
    {
        await using var database = new DatabaseFixture(); await database.Initialize();
        var folder = Path.Combine(Path.GetTempPath(), "recap_stream_hosts_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var processes = new List<Process>(); var drains = new List<Task<string>>();
        try
        {
            var source = Path.Combine(folder, "movie.mkv"); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-multistream.mkv"), source);
            var original = File.ReadAllBytes(source); var stamp = File.GetLastWriteTimeUtc(source);
            using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            foreach (var name in new[] { "MovieRecap.Api", "MovieRecap.Worker" })
            {
                var process = Start(name, database.Connection, folder, port); processes.Add(process); drains.Add(process.StandardOutput.ReadToEndAsync()); drains.Add(process.StandardError.ReadToEndAsync());
            }
            using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-Api-Key", "integration-only-key-at-least-32-characters");
            var deadline = DateTime.UtcNow.AddSeconds(40); var ready = false;
            while (DateTime.UtcNow < deadline && !processes[0].HasExited)
            {
                try { ready = (await client.GetAsync("/health/ready")).IsSuccessStatusCode; } catch (HttpRequestException) { } catch (TaskCanceledException) { }
                if (ready) break; await Task.Delay(100);
            }
            Assert.True(ready, "Stream smoke API failed readiness."); var imported = await Import(client, "movie.mkv"); Assert.Equal(JobStatus.Succeeded, imported.Job.Status);
            var view = (await client.GetFromJsonAsync<StreamResponse>($"/api/media/imports/{imported.Import.Id}/streams", JsonOptions))!;
            Assert.Equal(6, view.Streams.Count); Assert.Equal("jpn", view.Streams.Single(x => x.Index == 3).Language); Assert.Null(view.Selection.VideoIndex);
            var selectedResponse = await client.PutAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-selection", new SelectStreams(view.Selection.Version, 1, 3, 5, "host-operator")); Assert.Equal(HttpStatusCode.OK, selectedResponse.StatusCode);
            var selected = (await selectedResponse.Content.ReadFromJsonAsync<SelectionResponse>(JsonOptions))!; Assert.Equal(1, selected.VideoIndex); Assert.Equal(3, selected.AudioIndex); Assert.Equal(5, selected.SubtitleIndex);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-selection", new SelectStreams(view.Selection.Version, 0, null, null, "stale"))).StatusCode);
            var audit = await client.GetFromJsonAsync<List<SelectionAuditView>>($"/api/media/imports/{imported.Import.Id}/stream-selection/audit", JsonOptions); Assert.Equal("host-operator", Assert.Single(audit!).RequestedBy);
            var queue = await client.PostAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-inspections", new ReinspectStreams("host-reprobe", 1)); Assert.Equal(HttpStatusCode.Created, queue.StatusCode);
            var inspection = (await queue.Content.ReadFromJsonAsync<StreamInspectionView>(JsonOptions))!; var job = inspection.Job; deadline = DateTime.UtcNow.AddSeconds(30);
            while (job.Status is JobStatus.Queued or JobStatus.Running)
            {
                Assert.True(DateTime.UtcNow < deadline, "Reinspection did not finish."); await Task.Delay(100); job = (await client.GetFromJsonAsync<JobView>($"/api/jobs/{job.Id}", JsonOptions))!;
            }
            Assert.Equal(JobStatus.Succeeded, job.Status);
            var reinspected = (await client.GetFromJsonAsync<StreamResponse>($"/api/media/imports/{imported.Import.Id}/streams", JsonOptions))!;
            Assert.Equal(view.InventoryVersion, reinspected.InventoryVersion); Assert.Equal(selected.Version, reinspected.Selection.Version);
            Assert.Equal(original, File.ReadAllBytes(source)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
            output.WriteLine($"Stream smoke: import {imported.Import.Id}, selected video 1/audio 3/subtitle 5, inspection job {job.Id} succeeded, immutable inventory {view.InventoryVersion}; original bytes/timestamp preserved.");
        }
        finally
        {
            foreach (var process in processes) { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } process.Dispose(); }
            await Task.WhenAll(drains); foreach (var drain in drains) { var log = await drain; output.WriteLine(log[..Math.Min(log.Length, 8000)]); }
            Directory.Delete(folder, true);
        }
    }
    sealed record StreamResponse(Guid InventoryVersion, List<MediaStreamInfo> Streams, SelectionResponse Selection);
    sealed record SelectionResponse(Guid Version, int? VideoIndex, int? AudioIndex, int? SubtitleIndex);
    [PostgresSceneFact]
    public async Task Separate_hosts_decode_selected_nonprimary_video_and_preserve_scene_history()
    {
        await using var database = new DatabaseFixture(); await database.Initialize();
        var folder = Path.Combine(Path.GetTempPath(), "recap_scene_hosts_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var processes = new List<Process>(); var drains = new List<Task<string>>();
        try
        {
            var path = Path.Combine(folder, "movie.mkv"); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-scenes-multi.mkv"), path);
            var bytes = File.ReadAllBytes(path); var stamp = File.GetLastWriteTimeUtc(path);
            using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            foreach (var name in new[] { "MovieRecap.Api", "MovieRecap.Worker" })
            {
                var process = Start(name, database.Connection, folder, port); processes.Add(process); drains.Add(process.StandardOutput.ReadToEndAsync()); drains.Add(process.StandardError.ReadToEndAsync());
            }
            using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-Api-Key", "integration-only-key-at-least-32-characters"); var deadline = DateTime.UtcNow.AddSeconds(40); var ready = false;
            while (DateTime.UtcNow < deadline && !processes[0].HasExited)
            {
                try { ready = (await client.GetAsync("/health/ready")).IsSuccessStatusCode; } catch (HttpRequestException) { } catch (TaskCanceledException) { }
                if (ready) break; await Task.Delay(100);
            }
            Assert.True(ready, "Scene smoke API failed readiness."); var imported = await Import(client, "movie.mkv"); Assert.Equal(JobStatus.Succeeded, imported.Job.Status);
            var inventory = (await client.GetFromJsonAsync<StreamResponse>($"/api/media/imports/{imported.Import.Id}/streams", JsonOptions))!;
            var selectionResponse = await client.PutAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-selection", new SelectStreams(inventory.Selection.Version, 1, null, null, "scene-operator")); Assert.Equal(HttpStatusCode.OK, selectionResponse.StatusCode);
            var selection = (await selectionResponse.Content.ReadFromJsonAsync<SelectionResponse>(JsonOptions))!;
            var response = await client.PostAsJsonAsync($"/api/media/imports/{imported.Import.Id}/scene-analyses", new RequestScenes("host-scenes", selection.Version, "scene-operator", 27, 2, 1)); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var analysis = (await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions))!; deadline = DateTime.UtcNow.AddSeconds(40);
            while (analysis.Job.Status is JobStatus.Queued or JobStatus.Running)
            {
                Assert.True(DateTime.UtcNow < deadline, "Scene analysis did not finish."); await Task.Delay(100);
                analysis = (await client.GetFromJsonAsync<SceneResponse>($"/api/video/scene-analyses/{analysis.Analysis.Id}", JsonOptions))!;
            }
            Assert.Equal(JobStatus.Succeeded, analysis.Job.Status); Assert.Equal(1, analysis.Analysis.VideoIndex); Assert.Equal(3, analysis.Scenes.Count); Assert.True(analysis.IsCurrentSelection);
            Assert.Equal(new[] { 2.0, 4.0, 6.0 }, analysis.Scenes.Select(x => x.EndSeconds)); Assert.Equal("0.7.1", analysis.Analysis.SceneDetectVersion);
            var audit = (await client.GetFromJsonAsync<List<AuditResponse>>($"/api/jobs/{analysis.Job.Id}/audit"))!; Assert.Equal(new[] { "created", "started", "succeeded" }, audit.Select(x => x.Action));
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-selection", new SelectStreams(selection.Version, 0, null, null, "new-selection"))).StatusCode);
            var old = (await client.GetFromJsonAsync<SceneResponse>($"/api/video/scene-analyses/{analysis.Analysis.Id}", JsonOptions))!; Assert.False(old.IsCurrentSelection); Assert.Equal(JobStatus.Succeeded, old.Job.Status); Assert.Equal(3, old.Scenes.Count);
            Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
            output.WriteLine($"Scene smoke: analysis {analysis.Analysis.Id}, job {analysis.Job.Id}, nonprimary stream 1 produced ranges [0,2),[2,4),[4,6) with PySceneDetect {analysis.Analysis.SceneDetectVersion}; historical result marked noncurrent after selection change; original bytes/timestamp preserved.");
        }
        finally
        {
            foreach (var process in processes) { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } process.Dispose(); }
            await Task.WhenAll(drains); foreach (var drain in drains) { var log = await drain; output.WriteLine(log[..Math.Min(log.Length, 8000)]); }
            Directory.Delete(folder, true);
        }
    }
    sealed record SceneIdentity(Guid Id, int VideoIndex, string? SceneDetectVersion);
    [PostgresAudioFact]
    public async Task Separate_hosts_extract_the_selected_audio_and_download_verified_artifact_with_history()
    {
        await using var database = new DatabaseFixture(); await database.Initialize();
        var root = Path.Combine(Path.GetTempPath(), "recap_audio_hosts_" + Guid.NewGuid().ToString("N")); var library = Path.Combine(root, "library"); var artifacts = Path.Combine(root, "outputs"); Directory.CreateDirectory(library);
        var processes = new List<Process>(); var drains = new List<Task<string>>();
        try
        {
            var path = Path.Combine(library, "movie.mkv"); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "owned-multistream.mkv"), path);
            var original = File.ReadAllBytes(path); var stamp = File.GetLastWriteTimeUtc(path);
            using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            foreach (var name in new[] { "MovieRecap.Api", "MovieRecap.Worker" })
            {
                var process = Start(name, database.Connection, library, port, audioRoot: artifacts); processes.Add(process); drains.Add(process.StandardOutput.ReadToEndAsync()); drains.Add(process.StandardError.ReadToEndAsync());
            }
            using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-Api-Key", "integration-only-key-at-least-32-characters"); var deadline = DateTime.UtcNow.AddSeconds(40); var ready = false;
            while (DateTime.UtcNow < deadline && !processes[0].HasExited)
            {
                try { ready = (await client.GetAsync("/health/ready")).IsSuccessStatusCode; } catch (HttpRequestException) { } catch (TaskCanceledException) { }
                if (ready) break; await Task.Delay(100);
            }
            Assert.True(ready, "Audio smoke API failed readiness."); var imported = await Import(client, "movie.mkv"); Assert.Equal(JobStatus.Succeeded, imported.Job.Status);
            var inventory = (await client.GetFromJsonAsync<StreamResponse>($"/api/media/imports/{imported.Import.Id}/streams", JsonOptions))!;
            var selectedResponse = await client.PutAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-selection", new SelectStreams(inventory.Selection.Version, 0, 3, null, "audio-operator")); Assert.Equal(HttpStatusCode.OK, selectedResponse.StatusCode);
            var selection = (await selectedResponse.Content.ReadFromJsonAsync<SelectionResponse>(JsonOptions))!;
            var response = await client.PostAsJsonAsync($"/api/media/imports/{imported.Import.Id}/audio-extractions", new RequestAudio("host-audio", selection.Version, "audio-operator", 1)); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var extraction = (await response.Content.ReadFromJsonAsync<AudioResponse>(JsonOptions))!; deadline = DateTime.UtcNow.AddSeconds(40);
            while (extraction.Job.Status is JobStatus.Queued or JobStatus.Running)
            {
                Assert.True(DateTime.UtcNow < deadline, "Audio extraction did not finish."); await Task.Delay(100);
                extraction = (await client.GetFromJsonAsync<AudioResponse>($"/api/video/audio-extractions/{extraction.Extraction.Id}", JsonOptions))!;
            }
            Assert.Equal(JobStatus.Succeeded, extraction.Job.Status); Assert.Equal(3, extraction.Extraction.AudioIndex); Assert.Equal(16000, extraction.Extraction.Samples); Assert.True(extraction.IsCurrentSelection);
            Assert.StartsWith("ffmpeg version 9.0.2", extraction.Extraction.FfmpegVersion);
            var download = await client.GetAsync($"/api/video/audio-extractions/{extraction.Extraction.Id}/artifact"); Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            var wav = await download.Content.ReadAsByteArrayAsync(); Assert.Equal(32044, wav.Length); Assert.True(AudioTests.Tone(wav, 880) > AudioTests.Tone(wav, 440) * 10);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(wav)), extraction.Extraction.ArtifactSha256);
            var audit = (await client.GetFromJsonAsync<List<AuditResponse>>($"/api/jobs/{extraction.Job.Id}/audit"))!; Assert.Equal(new[] { "created", "started", "succeeded" }, audit.Select(x => x.Action));
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/media/imports/{imported.Import.Id}/stream-selection", new SelectStreams(selection.Version, 0, 2, null, "changed"))).StatusCode);
            var old = (await client.GetFromJsonAsync<AudioResponse>($"/api/video/audio-extractions/{extraction.Extraction.Id}", JsonOptions))!; Assert.False(old.IsCurrentSelection); Assert.Equal(JobStatus.Succeeded, old.Job.Status);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/video/audio-extractions/{extraction.Extraction.Id}/artifact")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/video/audio-extractions/{extraction.Extraction.Id}/cleanup", null)).StatusCode); Assert.Single(Directory.GetFiles(artifacts, "*.wav", SearchOption.AllDirectories)); Assert.Empty(Directory.GetFiles(artifacts, "*.tmp", SearchOption.AllDirectories));
            Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
            output.WriteLine($"Audio smoke: extraction {extraction.Extraction.Id}, job {extraction.Job.Id}, selected stream 3 decoded 880 Hz into 16000 mono samples / 32044 WAV bytes, SHA-256 {extraction.Extraction.ArtifactSha256}, {extraction.Extraction.FfmpegVersion}; history/source preserved.");
        }
        finally
        {
            foreach (var process in processes) { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } process.Dispose(); }
            await Task.WhenAll(drains); foreach (var drain in drains) { var log = await drain; output.WriteLine(log[..Math.Min(log.Length, 8000)]); } Directory.Delete(root, true);
        }
    }
    sealed record AudioIdentity(Guid Id, int AudioIndex, long? Samples, string? ArtifactSha256, string? FfmpegVersion);
    sealed record AudioResponse(AudioIdentity Extraction, JobView Job, bool IsCurrentSelection);
    sealed record SceneRangeResponse(int StartFrame, int EndFrame, double StartSeconds, double EndSeconds);
    sealed record SceneResponse(SceneIdentity Analysis, JobView Job, bool IsCurrentSelection, List<SceneRangeResponse> Scenes);
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
