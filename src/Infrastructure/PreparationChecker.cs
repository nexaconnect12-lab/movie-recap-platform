using Microsoft.Extensions.Options;
using MovieRecap.Media.Application;
using MovieRecap.VideoProcessing.Application;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class PreparationOptions { public int CheckTimeoutSeconds { get; set; } = 3600; public bool IsValid() => CheckTimeoutSeconds is >= 1 and <= 86400; }
public sealed class PreparationChecker(IPreparationMediaAccess media, IAudioArtifacts audio, IFrameArtifacts frames, IOptions<PreparationOptions> options) : IPreparationChecker
{
    public async Task<IPreparationCheck> CheckAsync(PreparationSnapshot snapshot, CancellationToken ct)
    {
        if (!snapshot.IsReady) throw new PreparationException("preparation-not-ready"); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.CheckTimeoutSeconds));
        var check = new Check();
        try
        {
            try { check.Source = await media.OpenAsync(snapshot.Media, deadline.Token); } catch (ImportException e) { check.Errors.Add(new("source", e.Code)); }
            if (snapshot.Audio is { } a)
            {
                try { check.Streams.Add(await audio.OpenAsync(a.Id, new(a.ArtifactToken!.Value, a.ArtifactSha256!, a.ArtifactSizeBytes!.Value, new(a.Samples!.Value, a.FfmpegVersion!)), deadline.Token)); }
                catch (AudioException e) { check.Errors.Add(new("audio", e.Code)); }
            }
            foreach (var frame in snapshot.Samples)
            {
                try { check.Streams.Add(await frames.OpenAsync(snapshot.Frames!.Id, snapshot.Frames.ArtifactToken!.Value, new(frame.Ordinal, frame.SourceSeconds!.Value, frame.NormalizedSeconds!.Value, frame.Sha256!, frame.SizeBytes!.Value), deadline.Token)); }
                catch (FrameException e) { check.Errors.Add(new("frame-" + frame.Ordinal, e.Code)); }
            }
            if (check.Source is not null) { try { await check.Source.VerifyUnchangedAsync(deadline.Token); } catch (ImportException e) { check.Errors.Add(new("source", e.Code)); } }
            return check;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await check.DisposeAsync(); throw new PreparationException("preparation-check-timeout"); }
        catch { await check.DisposeAsync(); throw; }
    }
    sealed class Check : IPreparationCheck
    {
        public IVerifiedMediaSource? Source { get; set; } public List<Stream> Streams { get; } = []; public List<PreparationIssue> Errors { get; } = []; public IReadOnlyList<PreparationIssue> Issues => Errors;
        public async ValueTask DisposeAsync() { foreach (var stream in Streams) await stream.DisposeAsync(); if (Source is not null) await Source.DisposeAsync(); }
    }
}
