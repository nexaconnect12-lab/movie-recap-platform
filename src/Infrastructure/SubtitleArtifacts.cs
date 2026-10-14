using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class LocalSubtitleArtifacts(IOptions<SubtitleOptions> options, IOptions<MediaOptions> media) : ISubtitleArtifacts
{
    readonly SubtitleOptions settings = options.Value;
    static Exception PathError(string suffix) => new SubtitleException("subtitle-" + suffix);
    string DirectoryFor(Guid id, bool create = false) => ManagedArtifactPaths.Resolve(settings.Root, media.Value.Folders.Values, "subtitle-v1", id, create, PathError);
    static void CheckLinks(string path) => ManagedArtifactPaths.CheckLinks(path, PathError);
    public Task<ISubtitleAttempt> BeginAsync(Guid extractionId, Guid token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (token == Guid.Empty) throw new SubtitleException("subtitle-invalid-attempt");
        var directory = DirectoryFor(extractionId, true); var prefix = Path.Combine(directory, token.ToString("N"));
        CheckLinks(prefix + ".lock"); CheckLinks(prefix + ".tmp"); CheckLinks(prefix + ".srt");
        FileStream? gate = null;
        try
        {
            gate = new(prefix + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(prefix + ".srt")) throw new SubtitleException("subtitle-attempt-already-promoted");
            var output = new FileStream(prefix + ".tmp", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
            return Task.FromResult<ISubtitleAttempt>(new Attempt(extractionId, token, prefix, gate, output, settings.MaxBytes));
        }
        catch { gate?.Dispose(); throw; }
    }
    public async Task<Stream> OpenAsync(Guid extractionId, SubtitleArtifact artifact, CancellationToken ct)
    {
        artifact.Validate(); var path = Path.Combine(DirectoryFor(extractionId), artifact.AttemptToken.ToString("N") + ".srt"); CheckLinks(path);
        FileStream? stream = null;
        try
        {
            stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (stream.Length > settings.MaxBytes) throw new SubtitleException("subtitle-output-limit");
            await VerifyAsync(stream, artifact, ct); return stream;
        }
        catch (Exception e) { if (stream is not null) await stream.DisposeAsync(); if (e is IOException or UnauthorizedAccessException) throw new SubtitleException("subtitle-artifact-unavailable"); throw; }
    }
    static async Task VerifyAsync(Stream stream, SubtitleArtifact artifact, CancellationToken ct)
    {
        await SubRip.ValidateAsync(stream, artifact.Subtitle.Cues, 16777216, ct);
        if (stream.Length != artifact.SizeBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != artifact.Sha256) throw new SubtitleException("subtitle-artifact-content-changed"); stream.Position = 0;
    }
    public Task<int> CleanupAsync(Guid extractionId, Guid? referencedToken, CancellationToken ct)
    {
        var directory = DirectoryFor(extractionId); if (!Directory.Exists(directory)) return Task.FromResult(0);
        var removed = 0;
        var tokens = Directory.EnumerateFiles(directory).Select(Path.GetFileNameWithoutExtension).Where(x => Guid.TryParseExact(x, "N", out _)).Distinct().ToArray();
        foreach (var name in tokens)
        {
            ct.ThrowIfCancellationRequested(); var token = Guid.ParseExact(name!, "N"); if (token == referencedToken) continue;
            var prefix = Path.Combine(directory, name!); CheckLinks(prefix + ".lock");
            FileStream gate;
            try { gate = new(prefix + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); } catch (IOException) { continue; }
            using (gate)
            {
                foreach (var suffix in new[] { ".tmp", ".srt" })
                {
                    var path = prefix + suffix; CheckLinks(path); if (File.Exists(path)) { File.Delete(path); removed++; }
                }
            }
            // Retain the tiny lock file to avoid unlink/reopen races with another attempt.
        }
        return Task.FromResult(removed);
    }
    sealed class Attempt(Guid id, Guid token, string prefix, FileStream gate, FileStream output, long maxBytes) : ISubtitleAttempt
    {
        bool prepared;
        public Guid ExtractionId => id;
        public Guid Token => token;
        public Stream Output => output;
        public async Task<SubtitleArtifact> PrepareAsync(ExtractedSubtitle subtitle, CancellationToken ct)
        {
            subtitle.Validate(); if (prepared) throw new SubtitleException("subtitle-attempt-already-prepared");
            await SubRip.ValidateAsync(output, subtitle.Cues, maxBytes, ct); var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, ct));
            var artifact = new SubtitleArtifact(token, hash, output.Length, subtitle); artifact.Validate();
            await output.FlushAsync(ct); output.Flush(true); await output.DisposeAsync(); prepared = true; return artifact;
        }
        public async Task PromoteAsync(SubtitleArtifact artifact, CancellationToken ct)
        {
            if (!prepared || artifact.AttemptToken != token) throw new SubtitleException("subtitle-attempt-binding-mismatch");
            CheckLinks(prefix + ".tmp"); CheckLinks(prefix + ".srt");
            await using var stream = new FileStream(prefix + ".tmp", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
            await VerifyAsync(stream, artifact, ct); ct.ThrowIfCancellationRequested(); File.Move(prefix + ".tmp", prefix + ".srt", overwrite: false);
        }
        public async ValueTask DisposeAsync() { await output.DisposeAsync(); await gate.DisposeAsync(); }
    }
}

