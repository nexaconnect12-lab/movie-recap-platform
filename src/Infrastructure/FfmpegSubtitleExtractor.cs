using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class FfmpegSubtitleExtractor(IOptions<SubtitleOptions> options) : ISubtitleExtractor
{
    public async Task<ExtractedSubtitle> ExtractAsync(string sourcePath, SelectedSubtitleSnapshot source, Stream output, Action<int>? progress, CancellationToken ct)
    {
        var settings = options.Value; if (!settings.IsValid() || !output.CanSeek || !output.CanWrite || !output.CanRead) throw new SubtitleException("invalid-subtitle-configuration");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var version = await VersionAsync(settings, deadline.Token);
            if (source.Codec != "subrip") throw new SubtitleException("subtitle-codec-unsupported");
            output.Position = 0; output.SetLength(0);
            using var process = Start(settings.FfmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-copyts", "-protocol_whitelist", "file,pipe", "-threads", "1", "-i", sourcePath,
                "-map", "0:" + source.SubtitleIndex, "-vn", "-an", "-dn", "-map_metadata", "-1", "-map_chapters", "-1", "-c:s", "srt", "-f", "srt", "pipe:1"]);
            using var cancellation = deadline.Token.Register(() => Kill(process));
            var copy = CopySrtAsync(process.StandardOutput.BaseStream, output, settings.MaxBytes, process, progress, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError, settings.MaxDiagnosticCharacters, process, deadline.Token, false);
            try
            {
                await Task.WhenAll(copy, error, process.WaitForExitAsync(deadline.Token));
                if (process.ExitCode != 0) throw new SubtitleException("subtitle-ffmpeg-rejected-source");
                var size = await copy; var bytes = new byte[checked((int)size)]; output.Position = 0; await output.ReadExactlyAsync(bytes, deadline.Token);
                var cues = SubRip.Parse(bytes); var canonical = SubRip.Encode(cues); if (canonical.Length > settings.MaxBytes) throw new SubtitleException("subtitle-output-limit");
                output.Position = 0; output.SetLength(0); await output.WriteAsync(canonical, deadline.Token); await output.FlushAsync(deadline.Token);
                progress?.Invoke(80); return new(cues, version);
            }
            finally { Kill(process); try { await Task.WhenAll(copy, error); } catch (Exception e) when (e is SubtitleException or IOException or OperationCanceledException) { } }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SubtitleException("subtitle-timeout"); }
        catch (IOException) { throw new SubtitleException("subtitle-process-io-failed"); }
    }
    internal static async Task<string> VersionAsync(SubtitleOptions settings, CancellationToken ct)
    {
        using var process = Start(settings.FfmpegPath, ["-version"]); using var cancellation = ct.Register(() => Kill(process));
        var output = ReadBoundedAsync(process.StandardOutput, settings.MaxDiagnosticCharacters, process, ct, true);
        var error = ReadBoundedAsync(process.StandardError, settings.MaxDiagnosticCharacters, process, ct, false);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(ct));
            var first = (await output).Split('\n')[0].Trim();
            if (process.ExitCode != 0 || !first.StartsWith("ffmpeg version ", StringComparison.Ordinal) || first.Length > 256) throw new SubtitleException("subtitle-ffmpeg-version-invalid");
            return first;
        }
        finally { Kill(process); try { await Task.WhenAll(output, error); } catch (Exception e) when (e is SubtitleException or IOException or OperationCanceledException) { } }
    }
    static async Task<long> CopySrtAsync(Stream input, Stream output, long max, Process process, Action<int>? progress, CancellationToken ct)
    {
        var buffer = new byte[65536]; long total = 0;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, ct); if (count == 0) return total;
                total += count; if (total > max) throw new SubtitleException("subtitle-output-limit");
                await output.WriteAsync(buffer.AsMemory(0, count), ct); progress?.Invoke(20 + (int)Math.Min(59, total * 60 / max));
            }
        }
        catch { Kill(process); throw; }
    }
    static async Task<string> ReadBoundedAsync(StreamReader reader, int max, Process process, CancellationToken ct, bool retain)
    {
        var buffer = new char[4096]; var text = new StringBuilder(); var total = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct); if (count == 0) return text.ToString();
            total += count; if (total > max) { Kill(process); throw new SubtitleException("subtitle-diagnostic-limit"); } if (retain) text.Append(buffer, 0, count);
        }
    }
    static Process Start(string executable, string[] arguments)
    {
        var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try { process.Start(); return process; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { process.Dispose(); throw new SubtitleException("subtitle-ffmpeg-unavailable"); }
    }
    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
}
