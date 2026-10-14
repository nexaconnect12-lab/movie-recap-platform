using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public static class SubRip
{
    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly Regex Timing = new(@"^(\d{2}):([0-5]\d):([0-5]\d),(\d{3}) --> (\d{2}):([0-5]\d):([0-5]\d),(\d{3})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static IReadOnlyList<SubtitleCueData> Parse(byte[] bytes)
    {
        try
        {
            if (bytes.Length is < 1 or > 16777216) throw new ArgumentException();
            var text = Utf8.GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n");
            if (text.Contains('\r')) throw new ArgumentException();
            var blocks = text.TrimEnd('\n').Split("\n\n", StringSplitOptions.None);
            if (blocks.Length is < 1 or > 100000) throw new ArgumentException();
            var cues = new List<SubtitleCueData>();
            foreach (var block in blocks)
            {
                var lines = block.Split('\n');
                if (lines.Length < 3 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number != cues.Count + 1) throw new ArgumentException();
                var match = Timing.Match(lines[1]); if (!match.Success) throw new ArgumentException();
                long At(int first) => long.Parse(match.Groups[first].Value, CultureInfo.InvariantCulture) * 3600000 + long.Parse(match.Groups[first + 1].Value, CultureInfo.InvariantCulture) * 60000 + long.Parse(match.Groups[first + 2].Value, CultureInfo.InvariantCulture) * 1000 + long.Parse(match.Groups[first + 3].Value, CultureInfo.InvariantCulture);
                cues.Add(new(cues.Count, At(1), At(5), string.Join('\n', lines.Skip(2))));
            }
            new ExtractedSubtitle(cues, "validated-parser").Validate(); return cues;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or RegexMatchTimeoutException) { throw new SubtitleException("subtitle-invalid-srt"); }
    }
    public static byte[] Encode(IReadOnlyList<SubtitleCueData> cues)
    {
        new ExtractedSubtitle(cues, "validated-encoder").Validate();
        static string Time(long value) => string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", value / 3600000, value / 60000 % 60, value / 1000 % 60, value % 1000);
        var text = new StringBuilder();
        foreach (var cue in cues)
        {
            text.Append((cue.Ordinal + 1).ToString(CultureInfo.InvariantCulture)).Append('\n').Append(Time(cue.StartMilliseconds)).Append(" --> ").Append(Time(cue.EndMilliseconds)).Append('\n').Append(cue.Text).Append("\n\n");
            if (text.Length > 16777216) throw new SubtitleException("subtitle-output-limit");
        }
        var bytes = Utf8.GetBytes(text.ToString()); if (bytes.Length > 16777216) throw new SubtitleException("subtitle-output-limit"); return bytes;
    }
    public static async Task ValidateAsync(Stream stream, IReadOnlyList<SubtitleCueData> expected, long maxBytes, CancellationToken ct)
    {
        if (stream.Length < 1 || stream.Length > maxBytes) throw new SubtitleException("subtitle-artifact-invalid");
        var bytes = new byte[checked((int)stream.Length)]; stream.Position = 0; await stream.ReadExactlyAsync(bytes, ct);
        var cues = Parse(bytes);
        if (!cues.SequenceEqual(expected) || !bytes.SequenceEqual(Encode(cues))) throw new SubtitleException("subtitle-artifact-content-changed"); stream.Position = 0;
    }
}
public sealed class SubtitleOptions
{
    public string? Root { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int TimeoutSeconds { get; set; } = 3600;
    public long MaxBytes { get; set; } = 8388608;
    public int MaxDiagnosticCharacters { get; set; } = 65536;
    public bool IsValid() => (Root is null || Path.IsPathFullyQualified(Root)) && !string.IsNullOrWhiteSpace(FfmpegPath) && TimeoutSeconds is >= 1 and <= 86400 && MaxBytes is >= 16 and <= 16777216 && MaxDiagnosticCharacters is >= 1024 and <= 1048576;
}
