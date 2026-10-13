using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class FfmpegFrameExtractor(IOptions<FrameOptions> options) : IFrameExtractor
{
    static readonly Regex TimeBase = new(@"config in time_base:\s*(\d+)/(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    static readonly Regex Timestamp = new(@"\bn:\s*(\d+)\s+pts:\s*(-?\d+)\s+pts_time:", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public async Task<FrameResult> ExtractAsync(string sourcePath, SelectedVideoSnapshot source, IReadOnlyList<FramePlan> plans, double offset, IFrameAttempt attempt, Action<int>? progress, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.IsValid() || plans.Count is < 1 or > 300 || plans.Where((p, i) => p.Ordinal != i || p.FrameIndex < 0 || i > 0 && p.FrameIndex <= plans[i - 1].FrameIndex).Any()) throw new FrameException("invalid-frame-configuration-or-plan");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var version = await FfmpegAudioExtractor.VersionAsync(new AudioOptions { FfmpegPath = settings.FfmpegPath, MaxDiagnosticCharacters = settings.MaxDiagnosticCharacters }, deadline.Token);
            var expression = string.Join('+', plans.Select(p => "eq(n," + p.FrameIndex.ToString(CultureInfo.InvariantCulture) + ")"));
            var filter = "select='" + expression + "',showinfo@recap=checksum=0,scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2,setsar=1,format=rgb24";
            using var process = new Process { StartInfo = new(settings.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var a in new[] { "-hide_banner", "-nostdin", "-nostats", "-v", "info", "-xerror", "-copyts", "-protocol_whitelist", "file,pipe", "-threads", "1", "-noautorotate", "-i", sourcePath,
                "-map", "0:" + source.VideoIndex.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1", "-filter_threads", "1", "-vf", filter,
                "-fps_mode", "passthrough", "-frames:v", plans.Count.ToString(CultureInfo.InvariantCulture), "-c:v", "png", "-threads", "1", "-f", "image2pipe", "pipe:1" }) process.StartInfo.ArgumentList.Add(a);
            try { process.Start(); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { throw new FrameException("frame-ffmpeg-unavailable"); }
            using var cancellation = deadline.Token.Register(() => Kill(process));
            var images = ReadImagesAsync(process.StandardOutput.BaseStream, plans.Count, settings, attempt, process, progress, deadline.Token);
            var times = ReadTimesAsync(process.StandardError, plans.Count, settings.MaxDiagnosticCharacters, process, deadline.Token);
            try
            {
                await Task.WhenAll(images, times, process.WaitForExitAsync(deadline.Token));
                if (process.ExitCode != 0) throw new FrameException("frame-ffmpeg-rejected-source");
                if (await images != plans.Count || (await times).Count != plans.Count) throw new FrameException("frame-incomplete-decode");
                progress?.Invoke(80); return await attempt.PrepareAsync(version, await times, plans, offset, deadline.Token);
            }
            finally { Kill(process); try { await Task.WhenAll(images, times); } catch (Exception e) when (e is FrameException or OperationCanceledException or IOException) { } }
        }
        catch (AudioException e) { throw new FrameException(e.Code.Replace("audio-", "frame-", StringComparison.Ordinal)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new FrameException("frame-timeout"); }
        catch (IOException) { throw new FrameException("frame-process-io-failed"); }
    }
    static async Task<int> ReadImagesAsync(Stream input, int expected, FrameOptions settings, IFrameAttempt attempt, Process process, Action<int>? progress, CancellationToken ct)
    {
        var count = 0; long total = 0;
        try
        {
            while (true)
            {
                var signature = new byte[8]; var first = await input.ReadAsync(signature.AsMemory(0, 1), ct); if (first == 0) return count;
                if (count >= expected) throw new FrameException("frame-extra-images"); await input.ReadExactlyAsync(signature.AsMemory(1), ct);
                if (!signature.SequenceEqual(PngFrames.Signature)) throw new FrameException("frame-invalid-png-stream");
                using var png = new MemoryStream(); png.Write(signature);
                while (true)
                {
                    var chunk = new byte[8]; await input.ReadExactlyAsync(chunk, ct); var length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                    if (length > settings.MaxImageBytes || png.Length + length + 12 > settings.MaxImageBytes) throw new FrameException("frame-image-limit");
                    var payload = new byte[(int)length + 4]; await input.ReadExactlyAsync(payload, ct); png.Write(chunk); png.Write(payload);
                    if (chunk.AsSpan(4).SequenceEqual("IEND"u8)) break;
                }
                total += png.Length; if (total > settings.MaxBatchBytes) throw new FrameException("frame-batch-limit");
                await attempt.WriteAsync(count, png.ToArray(), ct); count++; progress?.Invoke(20 + count * 59 / expected);
            }
        }
        catch (EndOfStreamException) { Kill(process); throw new FrameException("frame-truncated-png-stream"); }
        catch { Kill(process); throw; }
    }
    static async Task<IReadOnlyList<double>> ReadTimesAsync(StreamReader reader, int expected, int max, Process process, CancellationToken ct)
    {
        var buffer = new char[4096]; var line = new StringBuilder(); var total = 0; var times = new List<double>(); long numerator = 0, denominator = 0;
        void Parse(string value)
        {
            if (!value.Contains("showinfo@recap", StringComparison.Ordinal)) return;
            var timeBase = TimeBase.Match(value);
            if (timeBase.Success)
            {
                var n = long.Parse(timeBase.Groups[1].Value, CultureInfo.InvariantCulture); var d = long.Parse(timeBase.Groups[2].Value, CultureInfo.InvariantCulture);
                if (n <= 0 || d <= 0 || denominator != 0 && (n != numerator || d != denominator)) throw new FrameException("frame-invalid-time-base"); numerator = n; denominator = d; return;
            }
            var stamp = Timestamp.Match(value); if (!stamp.Success) { if (value.Contains("pts_time:", StringComparison.Ordinal)) throw new FrameException("frame-timestamps-unavailable"); return; }
            if (denominator == 0 || int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture) != times.Count || times.Count >= expected) throw new FrameException("frame-timestamp-order");
            var pts = long.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture); var seconds = pts * ((double)numerator / denominator);
            if (!double.IsFinite(seconds) || times.Count > 0 && seconds <= times[^1]) throw new FrameException("frame-nonmonotonic-timestamps"); times.Add(seconds);
        }
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), ct); if (count == 0) { if (line.Length > 0) Parse(line.ToString()); return times; }
                total += count; if (total > max) throw new FrameException("frame-diagnostic-limit");
                for (var i = 0; i < count; i++) { if (buffer[i] == '\n' || buffer[i] == '\r') { if (line.Length > 0) Parse(line.ToString()); line.Clear(); } else line.Append(buffer[i]); }
            }
        }
        catch (Exception e) when (e is FormatException or OverflowException or RegexMatchTimeoutException) { Kill(process); throw new FrameException("frame-invalid-timestamps"); }
        catch { Kill(process); throw; }
    }
    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
}
