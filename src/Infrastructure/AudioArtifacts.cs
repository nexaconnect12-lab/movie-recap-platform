using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class AudioOptions
{
    public string? Root { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int TimeoutSeconds { get; set; } = 3600;
    public long MaxPcmBytes { get; set; } = 1_073_741_824;
    public int MaxDiagnosticCharacters { get; set; } = 65536;
    public bool IsValid() => (Root is null || Path.IsPathFullyQualified(Root)) && !string.IsNullOrWhiteSpace(FfmpegPath) && TimeoutSeconds is >= 1 and <= 86400 &&
        MaxPcmBytes is >= 2 and <= 4_294_967_258 && MaxDiagnosticCharacters is >= 1024 and <= 1_048_576;
}
public static class PcmWave
{
    public static byte[] Header(long pcmBytes)
    {
        if (pcmBytes <= 0 || pcmBytes % 2 != 0 || pcmBytes > uint.MaxValue - 36L) throw new AudioException("audio-invalid-pcm");
        var h = new byte[44]; "RIFF"u8.CopyTo(h); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)(pcmBytes + 36)); "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), 16000); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16); "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)pcmBytes); return h;
    }
    public static async Task ValidateAsync(Stream stream, long samples, CancellationToken ct)
    {
        if (stream.Length != samples * 2 + 44) throw new AudioException("audio-artifact-invalid");
        var h = new byte[44]; stream.Position = 0; await stream.ReadExactlyAsync(h, ct);
        if (!h.SequenceEqual(Header(samples * 2))) throw new AudioException("audio-artifact-invalid"); stream.Position = 0;
    }
}
public sealed class LocalAudioArtifacts(IOptions<AudioOptions> options, IOptions<MediaOptions> media) : IAudioArtifacts
{
    readonly AudioOptions settings = options.Value;
    string DirectoryFor(Guid id, bool create = false)
    {
        if (id == Guid.Empty || settings.Root is null) throw new AudioException("audio-artifact-root-not-configured");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.Root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var folder in media.Value.Folders.Values)
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var sourcePrefix = Path.EndsInDirectorySeparator(source) ? source : source + Path.DirectorySeparatorChar;
            var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (root.Equals(source, comparison) || root.StartsWith(sourcePrefix, comparison) || source.StartsWith(rootPrefix, comparison))
                throw new AudioException("audio-artifact-root-overlaps-media");
        }
        var directory = Path.Combine(root, "audio-v1", id.ToString("N")); CheckLinks(directory);
        var owner = Path.Combine(directory, ".owner"); var expected = "platform-audio-v1:" + id.ToString("N");
        if (!Directory.Exists(directory))
        {
            if (!create) return directory;
            Directory.CreateDirectory(directory);
            try { using var marker = new FileStream(owner, FileMode.CreateNew, FileAccess.Write, FileShare.None); marker.Write(System.Text.Encoding.ASCII.GetBytes(expected)); marker.Flush(true); }
            catch (IOException) { /* Concurrent initialization must match the owner marker below. */ }
        }
        CheckLinks(owner);
        if (!File.Exists(owner) || new FileInfo(owner).Length > 128 || File.ReadAllText(owner) != expected) throw new AudioException("audio-artifact-directory-not-owned");
        return directory;
    }
    static void CheckLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new AudioException("audio-unsafe-artifact-link");
    }
    public Task<IAudioAttempt> BeginAsync(Guid extractionId, Guid token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (token == Guid.Empty) throw new AudioException("audio-invalid-attempt");
        var directory = DirectoryFor(extractionId, true); var prefix = Path.Combine(directory, token.ToString("N"));
        CheckLinks(prefix + ".lock"); CheckLinks(prefix + ".tmp"); CheckLinks(prefix + ".wav");
        FileStream? gate = null;
        try
        {
            gate = new(prefix + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(prefix + ".wav")) throw new AudioException("audio-attempt-already-promoted");
            var output = new FileStream(prefix + ".tmp", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
            return Task.FromResult<IAudioAttempt>(new Attempt(extractionId, token, prefix, gate, output));
        }
        catch { gate?.Dispose(); throw; }
    }
    public async Task<Stream> OpenAsync(Guid extractionId, AudioArtifact artifact, CancellationToken ct)
    {
        artifact.Validate(); var path = Path.Combine(DirectoryFor(extractionId), artifact.AttemptToken.ToString("N") + ".wav"); CheckLinks(path);
        FileStream? stream = null;
        try
        {
            stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            await VerifyAsync(stream, artifact, ct); return stream;
        }
        catch (Exception e) { if (stream is not null) await stream.DisposeAsync(); if (e is IOException or UnauthorizedAccessException) throw new AudioException("audio-artifact-unavailable"); throw; }
    }
    static async Task VerifyAsync(Stream stream, AudioArtifact artifact, CancellationToken ct)
    {
        await PcmWave.ValidateAsync(stream, artifact.Audio.Samples, ct);
        if (stream.Length != artifact.SizeBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != artifact.Sha256) throw new AudioException("audio-artifact-content-changed"); stream.Position = 0;
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
                foreach (var suffix in new[] { ".tmp", ".wav" })
                {
                    var path = prefix + suffix; CheckLinks(path); if (File.Exists(path)) { File.Delete(path); removed++; }
                }
            }
            // Retain the tiny lock file to avoid unlink/reopen races with another attempt.
        }
        return Task.FromResult(removed);
    }
    sealed class Attempt(Guid id, Guid token, string prefix, FileStream gate, FileStream output) : IAudioAttempt
    {
        bool prepared;
        public Guid ExtractionId => id;
        public Guid Token => token;
        public Stream Output => output;
        public async Task<AudioArtifact> PrepareAsync(ExtractedAudio audio, CancellationToken ct)
        {
            audio.Validate(); if (prepared) throw new AudioException("audio-attempt-already-prepared");
            await PcmWave.ValidateAsync(output, audio.Samples, ct); var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, ct));
            var artifact = new AudioArtifact(token, hash, output.Length, audio); artifact.Validate();
            await output.FlushAsync(ct); output.Flush(true); await output.DisposeAsync(); prepared = true; return artifact;
        }
        public async Task PromoteAsync(AudioArtifact artifact, CancellationToken ct)
        {
            if (!prepared || artifact.AttemptToken != token) throw new AudioException("audio-attempt-binding-mismatch");
            CheckLinks(prefix + ".tmp"); CheckLinks(prefix + ".wav");
            await using var stream = new FileStream(prefix + ".tmp", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
            await VerifyAsync(stream, artifact, ct); ct.ThrowIfCancellationRequested(); File.Move(prefix + ".tmp", prefix + ".wav", overwrite: false);
        }
        public async ValueTask DisposeAsync() { await output.DisposeAsync(); await gate.DisposeAsync(); }
    }
}
