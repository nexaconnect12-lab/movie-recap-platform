using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class FfmpegAudioExtractor(IOptions<AudioOptions> options) : IAudioExtractor
{
    public async Task<ExtractedAudio> ExtractAsync(string sourcePath, SelectedAudioSnapshot source, Stream output, Action<int>? progress, CancellationToken ct)
    {
        var settings = options.Value; if (!settings.IsValid() || !output.CanSeek || !output.CanWrite) throw new AudioException("invalid-audio-configuration");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var version = await VersionAsync(settings, deadline.Token);
            output.Position = 0; await output.WriteAsync(new byte[44], deadline.Token);
            using var process = Start(settings.FfmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-protocol_whitelist", "file,pipe", "-threads", "1", "-i", sourcePath,
                "-map", "0:" + source.AudioIndex, "-vn", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1", "-threads", "1", "-filter_threads", "1", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"]);
            using var cancellation = deadline.Token.Register(() => Kill(process));
            var copy = CopyPcmAsync(process.StandardOutput.BaseStream, output, settings.MaxPcmBytes, process, progress, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError, settings.MaxDiagnosticCharacters, process, deadline.Token, false);
            try
            {
                await Task.WhenAll(copy, error, process.WaitForExitAsync(deadline.Token));
                if (process.ExitCode != 0) throw new AudioException("audio-ffmpeg-rejected-source");
                var bytes = await copy; var header = PcmWave.Header(bytes); output.Position = 0; await output.WriteAsync(header, deadline.Token); await output.FlushAsync(deadline.Token);
                progress?.Invoke(80); return new(bytes / 2, version);
            }
            finally { Kill(process); try { await Task.WhenAll(copy, error); } catch (Exception e) when (e is AudioException or IOException or OperationCanceledException) { } }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AudioException("audio-timeout"); }
        catch (IOException) { throw new AudioException("audio-process-io-failed"); }
    }
    internal static async Task<string> VersionAsync(AudioOptions settings, CancellationToken ct)
    {
        using var process = Start(settings.FfmpegPath, ["-version"]); using var cancellation = ct.Register(() => Kill(process));
        var output = ReadBoundedAsync(process.StandardOutput, settings.MaxDiagnosticCharacters, process, ct, true);
        var error = ReadBoundedAsync(process.StandardError, settings.MaxDiagnosticCharacters, process, ct, false);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(ct));
            var first = (await output).Split('\n')[0].Trim();
            if (process.ExitCode != 0 || !first.StartsWith("ffmpeg version ", StringComparison.Ordinal) || first.Length > 256) throw new AudioException("audio-ffmpeg-version-invalid");
            return first;
        }
        finally { Kill(process); try { await Task.WhenAll(output, error); } catch (Exception e) when (e is AudioException or IOException or OperationCanceledException) { } }
    }
    static async Task<long> CopyPcmAsync(Stream input, Stream output, long max, Process process, Action<int>? progress, CancellationToken ct)
    {
        var buffer = new byte[65536]; long total = 0;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, ct); if (count == 0) return total;
                total += count; if (total > max) throw new AudioException("audio-output-limit");
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
            total += count; if (total > max) { Kill(process); throw new AudioException("audio-diagnostic-limit"); } if (retain) text.Append(buffer, 0, count);
        }
    }
    static Process Start(string executable, string[] arguments)
    {
        var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try { process.Start(); return process; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { process.Dispose(); throw new AudioException("audio-ffmpeg-unavailable"); }
    }
    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
}
