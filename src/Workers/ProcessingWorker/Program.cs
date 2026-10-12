using Microsoft.Extensions.Hosting;
using MovieRecap.Infrastructure;
using MovieRecap.Worker;
using Serilog;
using Serilog.Formatting.Compact;
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddSerilog(config =>
{
    config.MinimumLevel.Information().Enrich.FromLogContext().WriteTo.Console(new CompactJsonFormatter());
    var file = builder.Configuration["Logging:FilePath"];
    if (!string.IsNullOrWhiteSpace(file)) config.WriteTo.File(new CompactJsonFormatter(), file,
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true);
});
builder.Services.AddWindowsService(options => options.ServiceName = "MovieRecap.Processing");
builder.Services.AddPlatform(builder.Configuration);
builder.Services.AddOptions<WorkerOptions>().Bind(builder.Configuration.GetSection("Worker"))
    .Validate(x => x.PollSeconds is >= 1 and <= 60 && x.LeaseSeconds is >= 15 and <= 600, "Invalid polling or lease configuration").ValidateOnStart();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddHostedService<DiscoveryMonitor>();
await builder.Build().RunAsync();

