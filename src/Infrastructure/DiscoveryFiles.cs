using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
namespace MovieRecap.Infrastructure;

public sealed class DiscoveryOptions
{
    public bool Enabled { get; set; }
    public int RescanSeconds { get; set; } = 30;
    public int MaxEntriesPerFolder { get; set; } = 10_000;
    public bool IsValid() => RescanSeconds is >= 1 and <= 3600 && MaxEntriesPerFolder is >= 1 and <= 100_000;
}
public sealed class DiscoveryFiles(MediaInspector inspector, IOptions<MediaOptions> media, IOptions<DiscoveryOptions> discovery, TimeProvider clock) : IDiscoveryFiles
{
    public string Root(string folder)
    {
        if (!media.Value.Folders.TryGetValue(folder, out var configured)) throw new ImportException("media-folder-not-configured");
        var root = Path.GetFullPath(configured);
        try
        {
            for (var component = root; component is not null; component = Path.GetDirectoryName(component))
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0) throw new ImportException("unsafe-media-link");
            if (!Directory.Exists(root)) throw new ImportException("discovery-folder-unavailable");
            return root;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ImportException("discovery-folder-unavailable"); }
    }
    public FileObservation Observe(string folder, string relativePath)
    {
        var path = inspector.Resolve(folder, relativePath);
        try
        {
            var snapshot = MediaInspector.Snapshot(path);
            var readable = snapshot.Length > 0; string? error = readable ? null : "empty-media-file";
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (MediaInspector.Snapshot(path) != snapshot || stream.Length != snapshot.Length) { readable = false; error = "media-file-changed"; }
            }
            catch (IOException) { readable = false; error = "media-file-busy-or-unavailable"; }
            catch (UnauthorizedAccessException) { readable = false; error = "media-file-unavailable"; }
            var relative = Path.GetRelativePath(Path.GetFullPath(media.Value.Folders[folder]), path).Replace('\\', '/');
            var pathKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? relative.ToUpperInvariant() : relative)));
            return new(folder, relative, pathKey,
                Fingerprint(path, snapshot), snapshot.Length, readable, error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ImportException("media-file-unavailable"); }
    }
    internal static string Fingerprint(string path, (long Length, DateTime LastWrite, DateTime Creation) snapshot)
    {
        var canonical = OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
        var text = string.Join('\n', canonical, snapshot.Length.ToString(CultureInfo.InvariantCulture), snapshot.LastWrite.Ticks.ToString(CultureInfo.InvariantCulture), snapshot.Creation.Ticks.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    public Task<FolderScan> ScanAsync(string folder, CancellationToken ct)
    {
        var started = clock.GetUtcNow(); var found = new List<FileObservation>(); var complete = true; string? error = null;
        try
        {
            var root = Root(folder); var pending = new Stack<string>(); pending.Push(root); var visited = 0;
            while (pending.TryPop(out var directory))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Recheck ancestry before traversing a directory (it could have been replaced since enumeration).
                    for (var component = directory; component is not null; component = Path.GetDirectoryName(component))
                        if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0) throw new ImportException("unsafe-media-link");
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++visited > discovery.Value.MaxEntriesPerFolder) return Task.FromResult(new FolderScan(folder, started, found, false, "discovery-scan-limit"));
                        try
                        {
                            var attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                            if (Path.GetExtension(path).ToLowerInvariant() is not (".mp4" or ".mkv" or ".mov" or ".avi")) continue;
                            var relative = Path.GetRelativePath(root, path);
                            if (relative.Length > 1024) { complete = false; error = "discovery-path-too-long"; continue; }
                            found.Add(Observe(folder, relative));
                        }
                        catch (Exception e) when (e is ImportException or IOException or UnauthorizedAccessException) { complete = false; error = "discovery-scan-incomplete"; }
                    }
                }
                catch (Exception e) when (e is ImportException or IOException or UnauthorizedAccessException) { complete = false; error = "discovery-scan-incomplete"; }
            }
        }
        catch (ImportException e) { complete = false; error = e.Code; }
        return Task.FromResult(new FolderScan(folder, started, found, complete, error));
    }
}
