using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class SceneOptions
{
    public string PythonPath { get; set; } = "python";
    public string RunnerPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Scenes", "scene_runner.py");
    public int TimeoutSeconds { get; set; } = 3600;
    public int MaxFrames { get; set; } = 2_000_000;
    public int MaxScenes { get; set; } = 10000;
    public int MaxVideoSeconds { get; set; } = 86400;
    public int MaxFramePixels { get; set; } = 33_177_600;
    public int MaxOutputCharacters { get; set; } = 4_194_304;
    public bool IsValid() => !string.IsNullOrWhiteSpace(PythonPath) && !string.IsNullOrWhiteSpace(RunnerPath) && TimeoutSeconds is >= 1 and <= 86400 &&
        MaxFrames is >= 1 and <= 10_000_000 && MaxScenes is >= 1 and <= 10000 && MaxVideoSeconds is >= 1 and <= 86400 &&
        MaxFramePixels is >= 1 and <= 100_000_000 && MaxOutputCharacters is >= 1024 and <= 16_777_216;
}
public sealed class PySceneDetectAdapter(IOptions<SceneOptions> options) : ISceneDetector
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<SceneDetection> DetectAsync(string verifiedPath, SelectedVideoSnapshot source, double threshold, int minimumSceneFrames, Action<int>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var settings = options.Value;
        if (!settings.IsValid()) throw new SceneException("invalid-scene-configuration");
        if (!File.Exists(settings.RunnerPath)) throw new SceneException("scene-runner-unavailable");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var process = new Process { StartInfo = new(settings.PythonPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-I"); process.StartInfo.ArgumentList.Add(settings.RunnerPath);
        try { process.Start(); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { throw new SceneException("scene-python-unavailable"); }
        using var cancellation = timeout.Token.Register(() => Kill(process));
        var output = ReadProtocol(process.StandardOutput, settings.MaxOutputCharacters, settings, source, threshold, minimumSceneFrames, progress, process, timeout.Token);
        var error = DrainBounded(process.StandardError, settings.MaxOutputCharacters, process, timeout.Token);
        try
        {
            var request = JsonSerializer.Serialize(new { path = verifiedPath, videoIndex = source.VideoIndex, nominalFrameRate = source.NominalFrameRate, threshold, minimumSceneFrames,
                maxFrames = settings.MaxFrames, maxScenes = settings.MaxScenes, maxVideoSeconds = settings.MaxVideoSeconds, maxFramePixels = settings.MaxFramePixels }, JsonOptions);
            await process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token); process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            var result = await output;
            if (process.ExitCode != 0 || result is null) throw new SceneException("scene-process-failed");
            progress?.Invoke(80); return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SceneException("scene-timeout"); }
        catch (IOException) { throw new SceneException("scene-process-io-failed"); }
        finally
        {
            Kill(process); try { await Task.WhenAll(output, error); } catch (Exception e) when (e is SceneException or OperationCanceledException or IOException) { }
        }
    }
    static async Task<SceneDetection?> ReadProtocol(StreamReader reader, int max, SceneOptions settings, SelectedVideoSnapshot source,
        double threshold, int minimumSceneFrames, Action<int>? progress, Process process, CancellationToken ct)
    {
        var buffer = new char[4096]; var line = new StringBuilder(); var total = 0; SceneDetection? result = null; var priorProgress = 20;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct); if (count == 0) break;
            total += count; if (total > max) { Kill(process); throw new SceneException("scene-output-limit"); }
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n') { line.Append(buffer[i]); continue; }
                try
                {
                    using var doc = JsonDocument.Parse(line.ToString(), new JsonDocumentOptions { MaxDepth = 32 }); line.Clear();
                    var root = doc.RootElement; var kind = root.GetProperty("event").GetString();
                    if (result is not null) throw new SceneException("scene-invalid-protocol");
                    if (kind == "error")
                    {
                        var code = root.GetProperty("code").GetString();
                        if (code is null || code.Length > 128 || !System.Text.RegularExpressions.Regex.IsMatch(code, "^scene-[a-z-]+$")) throw new SceneException("scene-invalid-protocol");
                        throw new SceneException(code);
                    }
                    if (kind == "progress")
                    {
                        var frames = root.GetProperty("frames").GetInt32(); var seconds = root.GetProperty("seconds").GetDouble();
                        if (frames is < 1 || frames > settings.MaxFrames || !double.IsFinite(seconds) || seconds < 0 || seconds > settings.MaxVideoSeconds) throw new SceneException("scene-invalid-protocol");
                        // Coarse decoded-frame progress stays below completion; the frame cap is a bound, not an assumed video length.
                        var value = 20 + (int)Math.Min(59, (long)frames * 60 / settings.MaxFrames); priorProgress = Math.Max(priorProgress, value); progress?.Invoke(priorProgress);
                    }
                    else if (kind == "result")
                    {
                        result = root.GetProperty("result").Deserialize<SceneDetection>(JsonOptions) ?? throw new SceneException("scene-invalid-result");
                        result.Validate();
                        if (result.VideoIndex != source.VideoIndex || result.Threshold != threshold || result.MinimumSceneFrames != minimumSceneFrames || result.TotalFrames > settings.MaxFrames || result.DurationSeconds > settings.MaxVideoSeconds || result.Scenes.Count > settings.MaxScenes)
                            throw new SceneException("scene-result-binding-mismatch");
                    }
                    else throw new SceneException("scene-invalid-protocol");
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
                { Kill(process); throw new SceneException("scene-invalid-result"); }
                catch (SceneException) { Kill(process); throw; }
            }
        }
        if (line.Length != 0) throw new SceneException("scene-invalid-protocol"); return result;
    }
    static async Task DrainBounded(StreamReader reader, int max, Process process, CancellationToken ct)
    {
        var chars = new char[4096]; var total = 0;
        while (true)
        {
            var count = await reader.ReadAsync(chars.AsMemory(), ct); if (count == 0) return;
            total += count; if (total > max) { Kill(process); throw new SceneException("scene-output-limit"); }
        }
    }
    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
