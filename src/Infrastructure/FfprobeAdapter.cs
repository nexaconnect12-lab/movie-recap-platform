using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Infrastructure;

public sealed class FfprobeAdapter(IOptions<MediaOptions> options)
{
    public async Task<MediaMetadata> ProbeAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ProbeTimeoutSeconds));
        using var process = new Process { StartInfo = new(settings.FfprobePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var argument in new[] { "-protocol_whitelist", "file,pipe", "-v", "error", "-show_format", "-show_streams", "-of", "json", path })
            process.StartInfo.ArgumentList.Add(argument);
        try { process.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new ImportException("ffprobe-unavailable"); }
        using var cancellation = timeout.Token.Register(() => Kill(process));
        var stdout = ReadBounded(process.StandardOutput, settings.MaxProbeOutputCharacters, process, timeout.Token);
        var stderr = ReadBounded(process.StandardError, settings.MaxProbeOutputCharacters, process, timeout.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new ImportException("ffprobe-rejected-media");
            return Parse(await stdout, Path.GetExtension(path));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ImportException("ffprobe-timeout"); }
        finally
        {
            Kill(process);
            // Always observe reader tasks, including the output-limit failure path.
            try { await Task.WhenAll(stdout, stderr); } catch (Exception e) when (e is OperationCanceledException or ImportException or IOException) { }
        }
    }
    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    static async Task<string> ReadBounded(StreamReader reader, int maximum, Process process, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return result.ToString();
            if (result.Length + count > maximum) { Kill(process); throw new ImportException("ffprobe-output-too-large"); }
            result.Append(buffer, 0, count);
        }
    }
    public static MediaMetadata Parse(string json, string extension)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement; var format = root.GetProperty("format");
            var container = format.GetProperty("format_name").GetString()!;
            var formats = container.Split(',');
            var valid = extension.ToLowerInvariant() switch
            {
                ".mp4" or ".mov" => formats.Contains("mov") || formats.Contains("mp4"),
                ".mkv" => formats.Contains("matroska"), ".avi" => formats.Contains("avi"), _ => false
            };
            if (!valid) throw new ImportException("media-container-mismatch");
            var streams = root.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video" && !AttachedPicture(s));
            if (video.ValueKind == JsonValueKind.Undefined) throw new ImportException("media-video-stream-required");
            var audio = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
            var fps = Number(video, "avg_frame_rate");
            if (!double.IsFinite(fps) || fps <= 0) fps = Number(video, "r_frame_rate");
            var metadata = new MediaMetadata(container, Number(format, "duration"), video.GetProperty("codec_name").GetString()!,
                video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(), fps,
                audio.ValueKind == JsonValueKind.Undefined ? null : audio.GetProperty("codec_name").GetString(),
                audio.ValueKind == JsonValueKind.Undefined ? null : audio.GetProperty("channels").GetInt32(),
                audio.ValueKind == JsonValueKind.Undefined ? null : checked((int)Number(audio, "sample_rate")));
            metadata.Validate(); return metadata;
        }
        catch (ImportException) { throw; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException or NullReferenceException)
        { throw new ImportException("ffprobe-invalid-metadata"); }
    }
    static bool AttachedPicture(JsonElement stream) => stream.TryGetProperty("disposition", out var disposition) &&
        disposition.TryGetProperty("attached_pic", out var attached) && attached.GetInt32() == 1;
    static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        var parts = (value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()).Split('/');
        var numerator = double.Parse(parts[0], CultureInfo.InvariantCulture);
        return parts.Length == 2 ? numerator / double.Parse(parts[1], CultureInfo.InvariantCulture) : numerator;
    }
}
