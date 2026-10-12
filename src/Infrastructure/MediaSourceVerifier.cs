using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MovieRecap.Media.Application;
namespace MovieRecap.Infrastructure;

public sealed class MediaSourceVerifier(MediaInspector inspector, IOptions<MediaOptions> options) : IMediaSourceVerifier
{
    public async Task<IVerifiedMediaSource> OpenAsync(StreamSource source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var path = inspector.Resolve(source.Import.Folder, source.Import.RelativePath);
        FileStream? stream = null;
        try
        {
            stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var snapshot = MediaInspector.Snapshot(path);
            if (source.Import.ExpectedFingerprint is { } expected && DiscoveryFiles.Fingerprint(path, snapshot) != expected)
                throw new ImportException("discovery-source-changed");
            var verified = new Verified(inspector, source, path, stream, snapshot);
            await Task.Delay(options.Value.StabilityMilliseconds, ct);
            await verified.VerifyUnchangedAsync(ct); return verified;
        }
        catch (Exception e)
        {
            if (stream is not null) await stream.DisposeAsync();
            if (e is IOException or UnauthorizedAccessException) throw new ImportException("media-file-busy-or-unavailable");
            throw;
        }
    }
    sealed class Verified(MediaInspector inspector, StreamSource source, string path, FileStream stream,
        (long Length, DateTime LastWrite, DateTime Creation) snapshot) : IVerifiedMediaSource
    {
        public string LocalPath => path;
        public async Task VerifyUnchangedAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Check(); stream.Position = 0;
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                Check(); if (hash != source.Asset.Sha256 || stream.Length != source.Asset.SizeBytes) throw new ImportException("stream-source-content-changed");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ImportException("media-file-unavailable"); }
        }
        void Check()
        {
            if (inspector.Resolve(source.Import.Folder, source.Import.RelativePath) != path || MediaInspector.Snapshot(path) != snapshot || stream.Length != snapshot.Length)
                throw new ImportException("media-file-changed");
        }
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
