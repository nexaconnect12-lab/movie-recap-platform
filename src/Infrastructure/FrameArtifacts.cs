using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class FrameOptions
{
    public string? Root { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int TimeoutSeconds { get; set; } = 3600;
    public int MaxImageBytes { get; set; } = 1_048_576;
    public long MaxBatchBytes { get; set; } = 104_857_600;
    public int MaxDiagnosticCharacters { get; set; } = 1_048_576;
    public bool IsValid() => (Root is null || Path.IsPathFullyQualified(Root)) && !string.IsNullOrWhiteSpace(FfmpegPath) && TimeoutSeconds is >= 1 and <= 86400 &&
        MaxImageBytes is >= 1024 and <= 4_194_304 && MaxBatchBytes is >= 1024 and <= 1_258_291_200 && MaxDiagnosticCharacters is >= 1024 and <= 4_194_304;
}
public sealed class LocalFrameArtifacts(IOptions<FrameOptions> options, IOptions<MediaOptions> media) : IFrameArtifacts
{
    static Exception Error(string suffix) => new FrameException("frame-" + suffix);
    string DirectoryFor(Guid id, bool create = false) => ManagedArtifactPaths.Resolve(options.Value.Root, media.Value.Folders.Values, "frames-v1", id, create, Error);
    static void Check(string path) => ManagedArtifactPaths.CheckLinks(path, Error);
    static string Name(int ordinal) { if (ordinal is < 0 or >= 300) throw new FrameException("frame-invalid-ordinal"); return ordinal.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".png"; }
    public Task<IFrameAttempt> BeginAsync(Guid id, Guid token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (token == Guid.Empty) throw new FrameException("frame-invalid-attempt");
        var prefix = Path.Combine(DirectoryFor(id, true), token.ToString("N")); Check(prefix + ".lock"); Check(prefix + ".tmp"); Check(prefix + ".ready");
        FileStream? gate = null;
        try
        {
            gate = new(prefix + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (Directory.Exists(prefix + ".tmp") || Directory.Exists(prefix + ".ready")) throw new FrameException("frame-attempt-already-exists");
            Directory.CreateDirectory(prefix + ".tmp"); return Task.FromResult<IFrameAttempt>(new Attempt(id, token, prefix, gate, options.Value));
        }
        catch { gate?.Dispose(); throw; }
    }
    static async Task VerifyAsync(Stream stream, SampledFrame frame, CancellationToken ct)
    {
        if (stream.Length != frame.SizeBytes || frame.SizeBytes is < 1 or > 4_194_304) throw new FrameException("frame-artifact-invalid");
        var bytes = new byte[(int)stream.Length]; stream.Position = 0; await stream.ReadExactlyAsync(bytes, ct);
        try { PngFrames.DecodeRgb(bytes); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { throw new FrameException("frame-artifact-invalid"); }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != frame.Sha256) throw new FrameException("frame-artifact-content-changed"); stream.Position = 0;
    }
    public async Task<Stream> OpenAsync(Guid id, Guid token, SampledFrame frame, CancellationToken ct)
    {
        var path = Path.Combine(DirectoryFor(id), token.ToString("N") + ".ready", Name(frame.Ordinal)); Check(path); FileStream? stream = null;
        try { stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous); await VerifyAsync(stream, frame, ct); return stream; }
        catch (Exception e) { if (stream is not null) await stream.DisposeAsync(); if (e is IOException or UnauthorizedAccessException) throw new FrameException("frame-artifact-unavailable"); throw; }
    }
    public Task<int> CleanupAsync(Guid id, Guid? referencedToken, CancellationToken ct)
    {
        var directory = DirectoryFor(id); if (!Directory.Exists(directory)) return Task.FromResult(0); var removed = 0;
        foreach (var tokenName in Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileNameWithoutExtension).Where(x => Guid.TryParseExact(x, "N", out _)).Distinct().ToArray())
        {
            ct.ThrowIfCancellationRequested(); if (Guid.ParseExact(tokenName!, "N") == referencedToken) continue;
            var prefix = Path.Combine(directory, tokenName!); Check(prefix + ".lock"); FileStream gate;
            try { gate = new(prefix + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); } catch (IOException) { continue; }
            using (gate)
            {
                foreach (var suffix in new[] { ".tmp", ".ready" })
                {
                    var path = prefix + suffix; Check(path); if (!Directory.Exists(path)) continue;
                    foreach (var file in Directory.EnumerateFiles(path))
                    {
                        ct.ThrowIfCancellationRequested(); var name = Path.GetFileName(file);
                        if (name.Length != 8 || !int.TryParse(name[..4], out var ordinal) || ordinal is < 0 or >= 300 || name != Name(ordinal)) continue;
                        Check(file); File.Delete(file); removed++;
                    }
                    if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); // Nonrecursive; unknown entries remain untouched.
                }
            }
        }
        return Task.FromResult(removed);
    }
    sealed class Attempt(Guid id, Guid token, string prefix, FileStream gate, FrameOptions settings) : IFrameAttempt
    {
        readonly List<(string Hash, long Size)> written = []; bool prepared; long total;
        public Guid SamplingId => id; public Guid Token => token;
        public async Task WriteAsync(int ordinal, ReadOnlyMemory<byte> png, CancellationToken ct)
        {
            if (prepared || ordinal != written.Count || ordinal >= 300 || png.Length > settings.MaxImageBytes || total + png.Length > settings.MaxBatchBytes) throw new FrameException("frame-output-limit-or-order");
            try { PngFrames.DecodeRgb(png.Span); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { throw new FrameException("frame-invalid-png"); }
            var file = Path.Combine(prefix + ".tmp", Name(ordinal)); Check(file);
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            await output.WriteAsync(png, ct); await output.FlushAsync(ct); output.Flush(true);
            written.Add((Convert.ToHexStringLower(SHA256.HashData(png.Span)), png.Length)); total += png.Length;
        }
        public Task<FrameResult> PrepareAsync(string version, IReadOnlyList<double> seconds, IReadOnlyList<FramePlan> plans, double offset, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); if (prepared || written.Count != plans.Count || seconds.Count != plans.Count) throw new FrameException("frame-incomplete-batch");
            var frames = written.Select((x, i) => new SampledFrame(i, seconds[i], seconds[i] - offset, x.Hash, x.Size)).ToArray(); var result = new FrameResult(token, version, frames);
            try { result.Validate(plans, offset); } catch (ArgumentException) { throw new FrameException("frame-timeline-mismatch"); } prepared = true; return Task.FromResult(result);
        }
        public async Task PromoteAsync(FrameResult result, CancellationToken ct)
        {
            if (!prepared || result.Token != token || result.Frames.Count != written.Count) throw new FrameException("frame-attempt-binding-mismatch");
            Check(prefix + ".tmp"); Check(prefix + ".ready");
            foreach (var frame in result.Frames)
            {
                var path = Path.Combine(prefix + ".tmp", Name(frame.Ordinal)); Check(path);
                await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous); await VerifyAsync(read, frame, ct);
            }
            ct.ThrowIfCancellationRequested(); Directory.Move(prefix + ".tmp", prefix + ".ready");
        }
        public ValueTask DisposeAsync() => gate.DisposeAsync();
    }
}
