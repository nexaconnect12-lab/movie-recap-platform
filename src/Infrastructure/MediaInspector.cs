using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MovieRecap.Media.Application;
namespace MovieRecap.Infrastructure;

public sealed class MediaOptions
{
    public Dictionary<string, string> Folders { get; set; } = new(StringComparer.Ordinal);
    public int StabilityMilliseconds { get; set; } = 2000;
    public string FfprobePath { get; set; } = "ffprobe";
    public int ProbeTimeoutSeconds { get; set; } = 30;
    public int MaxProbeOutputCharacters { get; set; } = 1_048_576;
    public bool IsValid() => StabilityMilliseconds is >= 100 and <= 60_000 && ProbeTimeoutSeconds is >= 1 and <= 300 &&
        MaxProbeOutputCharacters is >= 1024 and <= 4_194_304 && !string.IsNullOrWhiteSpace(FfprobePath) &&
        Folders.All(x => !string.IsNullOrWhiteSpace(x.Key) && x.Key.Length <= 64 && Path.IsPathFullyQualified(x.Value));
}
public sealed class MediaInspector(IOptions<MediaOptions> options, FfprobeAdapter probe) : IMediaInspector
{
    readonly MediaOptions settings = options.Value;
    public void ValidateLocation(string folder, string relativePath) => _ = Resolve(folder, relativePath);
    string Resolve(string folder, string relativePath)
    {
        if (folder is null || !settings.Folders.TryGetValue(folder, out var root)) throw new ImportException("media-folder-not-configured");
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':') ||
            relativePath.Split(['/', '\\']).Any(x => x is ".." or "." or "")) throw new ImportException("unsafe-media-path");
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var full = Path.GetFullPath(Path.Combine(root, relativePath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, comparison)) throw new ImportException("unsafe-media-path");
            var extension = Path.GetExtension(full).ToLowerInvariant();
            if (extension is not (".mp4" or ".mkv" or ".mov" or ".avi")) throw new ImportException("unsupported-media-format");
            // Check every existing component, including ancestors of the configured root.
            for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ImportException("unsafe-media-link");
            }
            if ((File.GetAttributes(full) & FileAttributes.Directory) != 0) throw new ImportException("media-file-not-found");
            return full;
        }
        catch (ImportException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new ImportException("media-file-unavailable"); }
    }
    public async Task<InspectedMedia> InspectAsync(string folder, string relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Resolve(folder, relativePath);
        try
        {
            // Read only, and deny concurrent writes/deletes for the whole inspection on Windows.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var before = Snapshot(path);
            if (before.Length <= 0) throw new ImportException("empty-media-file");
            await Task.Delay(settings.StabilityMilliseconds, ct);
            Check();
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            Check();
            var metadata = await probe.ProbeAsync(path, ct);
            // Rehash after the subprocess: also detects same-size, timestamp-preserving changes on systems without Windows share locks.
            stream.Position = 0;
            var afterHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            Check();
            if (hash != afterHash) throw new ImportException("media-file-changed");
            return new(hash, stream.Length, metadata);
            void Check()
            {
                if (Resolve(folder, relativePath) != path || Snapshot(path) != before || stream.Length != before.Length)
                    throw new ImportException("media-file-changed");
            }
        }
        catch (IOException) { throw new ImportException("media-file-busy-or-unavailable"); }
        catch (UnauthorizedAccessException) { throw new ImportException("media-file-unavailable"); }
    }
    static (long Length, DateTime LastWrite, DateTime Creation) Snapshot(string path)
    {
        var file = new FileInfo(path); return (file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
    }
}
