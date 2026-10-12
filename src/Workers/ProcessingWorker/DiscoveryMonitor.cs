using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieRecap.Infrastructure;
using MovieRecap.Media.Application;
namespace MovieRecap.Worker;

public sealed class DiscoveryMonitor(DiscoveryFiles files, IDiscoveryStore store, IOptions<MediaOptions> media,
    IOptions<DiscoveryOptions> options, ILogger<DiscoveryMonitor> log) : BackgroundService
{
    readonly Channel<bool> hints = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.Ordinal);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) { log.LogInformation("Media discovery monitoring is disabled"); return; }
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (hints.Reader.TryRead(out _)) { }
                foreach (var folder in media.Value.Folders.Keys)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    EnsureWatcher(folder);
                    try
                    {
                        var scan = await files.ScanAsync(folder, stoppingToken);
                        await store.ReconcileAsync(scan, TimeSpan.FromMilliseconds(media.Value.StabilityMilliseconds), stoppingToken);
                        if (!scan.Complete) log.LogWarning("Media discovery scan incomplete: {ErrorCode}", scan.ErrorCode);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { log.LogWarning("Media discovery scan failed: {ErrorType}", ex.GetType().Name); }
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromSeconds(options.Value.RescanSeconds));
                try { await hints.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { foreach (var watcher in watchers.Values) watcher.Dispose(); watchers.Clear(); }
    }
    void EnsureWatcher(string folder)
    {
        try
        {
            var root = files.Root(folder);
            if (watchers.ContainsKey(folder)) return;
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            watcher.Created += Hint; watcher.Changed += Hint; watcher.Deleted += Hint; watcher.Renamed += Hint;
            watcher.Error += (_, _) => hints.Writer.TryWrite(true); // Overflow/failure: request reconciliation, retain periodic recovery.
            watchers.Add(folder, watcher); watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ImportException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (watchers.Remove(folder, out var old)) old.Dispose();
            // Missing/moved roots are retried on each periodic cycle. The scan records no false deletions.
        }
    }
    void Hint(object sender, FileSystemEventArgs args) => hints.Writer.TryWrite(true);
}
