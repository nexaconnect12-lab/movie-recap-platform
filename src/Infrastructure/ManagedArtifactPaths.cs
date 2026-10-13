namespace MovieRecap.Infrastructure;

internal static class ManagedArtifactPaths
{
    public static string Resolve(string? rootSetting, IEnumerable<string> mediaRoots, string scope, Guid id, bool create, Func<string, Exception> error)
    {
        if (id == Guid.Empty || rootSetting is null) throw error("artifact-root-not-configured");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootSetting)); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var folder in mediaRoots)
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var sourcePrefix = Path.EndsInDirectorySeparator(source) ? source : source + Path.DirectorySeparatorChar; var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (root.Equals(source, comparison) || root.StartsWith(sourcePrefix, comparison) || source.StartsWith(rootPrefix, comparison)) throw error("artifact-root-overlaps-media");
        }
        var directory = Path.Combine(root, scope, id.ToString("N")); CheckLinks(directory, error);
        var owner = Path.Combine(directory, ".owner"); var expected = "platform-" + scope + ":" + id.ToString("N");
        if (!Directory.Exists(directory))
        {
            if (!create) return directory;
            Directory.CreateDirectory(directory);
            try { using var marker = new FileStream(owner, FileMode.CreateNew, FileAccess.Write, FileShare.None); marker.Write(System.Text.Encoding.ASCII.GetBytes(expected)); marker.Flush(true); }
            catch (IOException) { /* A concurrent initialization must match the owner marker below. */ }
        }
        CheckLinks(owner, error);
        if (!File.Exists(owner) || new FileInfo(owner).Length > 128 || File.ReadAllText(owner) != expected) throw error("artifact-directory-not-owned"); return directory;
    }
    public static void CheckLinks(string path, Func<string, Exception> error)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw error("unsafe-artifact-link");
    }
}
