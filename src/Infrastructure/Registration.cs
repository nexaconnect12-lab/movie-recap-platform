using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MovieRecap.Jobs.Application;
using MovieRecap.Media.Application;
namespace MovieRecap.Infrastructure;

public static class Registration
{
    public static IServiceCollection AddPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Platform");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings__Platform is required.");
        services.AddDbContextFactory<PlatformDbContext>(o => o.UseNpgsql(connection));
        services.AddSingleton(TimeProvider.System); services.AddSingleton<IJobStore, PostgresJobStore>();
        services.AddSingleton<IValidator<CreateJob>, CreateJobValidator>(); services.AddSingleton<JobService>();
        services.AddOptions<MediaOptions>().Bind(configuration.GetSection("Media")).Validate(x => x.IsValid(), "Invalid media configuration").ValidateOnStart();
        services.AddSingleton<FfprobeAdapter>(); services.AddSingleton<MediaInspector>(); services.AddSingleton<IMediaInspector>(s => s.GetRequiredService<MediaInspector>());
        services.AddSingleton<PostgresImportStore>(); services.AddSingleton<IImportStore>(s => s.GetRequiredService<PostgresImportStore>());
        services.AddSingleton<ImportService>(); services.AddSingleton<ImportExecutor>();
        services.AddOptions<DiscoveryOptions>().Bind(configuration.GetSection("Discovery")).Validate(x => x.IsValid(), "Invalid discovery configuration").ValidateOnStart();
        services.AddSingleton<DiscoveryFiles>(); services.AddSingleton<IDiscoveryFiles>(s => s.GetRequiredService<DiscoveryFiles>());
        services.AddSingleton<IDiscoveryStore, PostgresDiscoveryStore>(); services.AddSingleton<DiscoveryService>();
        services.AddHealthChecks().AddCheck<DatabaseHealth>("postgresql", tags: ["ready"]);
        return services;
    }
}
public sealed class DatabaseHealth(IDbContextFactory<PlatformDbContext> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            // Connectivity alone is insufficient: verify the migrated job table exists.
            await db.Jobs.AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
            await db.Imports.AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
            await db.Assets.AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
            await db.ImportRights.AsNoTracking().Select(x => x.ImportId).Take(1).ToListAsync(ct);
            await db.Discoveries.AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return HealthCheckResult.Unhealthy("Database or schema unavailable."); }
    }
}

