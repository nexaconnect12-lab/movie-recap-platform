using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using MovieRecap.Infrastructure;
using MovieRecap.Jobs.Application;
using MovieRecap.Jobs.Domain;
using MovieRecap.Media.Application;
using MovieRecap.Media.Domain;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, config) => config.MinimumLevel.Information()
    .Enrich.FromLogContext().WriteTo.Console(new CompactJsonFormatter()));
builder.Services.AddPlatform(builder.Configuration);
var apiKey = builder.Configuration["Security:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 32) throw new InvalidOperationException("Security__ApiKey must contain at least 32 characters.");
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var app = builder.Build();
app.UseExceptionHandler(error => error.Run(async context =>
{
    var ex = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    var status = ex switch { BadHttpRequestException requestError => requestError.StatusCode, System.Text.Json.JsonException or ValidationException or ImportException => 400, JobConflictException or ImportConflictException or DbUpdateConcurrencyException => 409, _ => 500 };
    if (status == 500) app.Logger.LogError(ex, "Request failed {TraceId}", context.TraceIdentifier);
    await Results.Problem(statusCode: status, title: status == 500 ? "Internal server error" : ex is BadHttpRequestException or System.Text.Json.JsonException ? "Invalid request body" : ex.Message,
        extensions: ex is ValidationException v ? new Dictionary<string, object?> { ["errors"] = v.Errors.Select(e => new { e.PropertyName, e.ErrorMessage }) } : null).ExecuteAsync(context);
}));
app.UseSerilogRequestLogging();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health")) { await next(context); return; }
    var supplied = context.Request.Headers["X-Api-Key"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))))
    { context.Response.StatusCode = 401; return; }
    await next(context);
});
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = x => x.Tags.Contains("ready") });
app.MapOpenApi();
var imports = app.MapGroup("/api/media/imports").WithTags("Media imports");
imports.MapGet("/{id:guid}/streams", async (Guid id, IStreamStore store, CancellationToken ct) =>
{
    var result = await store.GetAsync(id, ct); return result is null ? Results.NotFound() : Results.Ok(result);
});
imports.MapPut("/{id:guid}/stream-selection", async (Guid id, SelectStreams request, StreamService service, CancellationToken ct) =>
{
    var result = await service.SelectAsync(id, request, ct); return result is null ? Results.NotFound() : Results.Ok(result);
});
imports.MapGet("/{id:guid}/stream-selection/audit", async (Guid id, IStreamStore store, CancellationToken ct) =>
    await store.GetAsync(id, ct) is null ? Results.NotFound() : Results.Ok(await store.AuditAsync(id, ct)));
imports.MapPost("/{id:guid}/stream-inspections", async (Guid id, ReinspectStreams request, StreamService service, CancellationToken ct) =>
{
    var result = await service.InspectAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Created($"/api/jobs/{result.Job.Id}", result);
});
var discoveries = app.MapGroup("/api/media/discoveries").WithTags("Media discovery inbox");
discoveries.MapGet("/", async (int? limit, DiscoveryStatus? status, IDiscoveryStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(limit ?? 50, status, ct)));
discoveries.MapGet("/{id:guid}", async (Guid id, IDiscoveryStore store, CancellationToken ct) =>
{
    var result = await store.GetAsync(id, ct); return result is null ? Results.NotFound() : Results.Ok(result);
});
discoveries.MapPost("/{id:guid}/dismiss", async (Guid id, VersionRequest request, IDiscoveryStore store, CancellationToken ct) =>
{
    var result = await store.DismissAsync(id, request.Version, ct); return result is null ? Results.NotFound() : Results.Ok(result);
});
discoveries.MapPost("/{id:guid}/import", async (Guid id, SubmitDiscovery request, DiscoveryService service, CancellationToken ct) =>
{
    var result = await service.SubmitAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Created($"/api/media/imports/{result.Import.Id}", result);
});
imports.MapPost("/", async (ManualImport request, ImportService service, CancellationToken ct) =>
{
    var result = await service.CreateAsync(request, ct);
    return Results.Created($"/api/media/imports/{result.Import.Id}", result);
});
imports.MapGet("/{id:guid}", async (Guid id, IImportStore store, CancellationToken ct) =>
{
    var result = await store.GetAsync(id, ct); return result is null ? Results.NotFound() : Results.Ok(result);
});
var jobs = app.MapGroup("/api/jobs").WithTags("Jobs");
jobs.MapPost("/", async (CreateJob command, JobService service, CancellationToken ct) =>
{
    var job = await service.CreateAsync(command, ct); return Results.Created($"/api/jobs/{job.Id}", job);
});
jobs.MapGet("/", async (int? limit, IJobStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(limit ?? 50, ct)));
jobs.MapGet("/{id:guid}", async (Guid id, IJobStore store, CancellationToken ct) =>
{
    var j = await store.GetAsync(id, ct); return j is null ? Results.NotFound() : Results.Ok(j);
});
jobs.MapGet("/{id:guid}/audit", async (Guid id, IJobStore store, CancellationToken ct) =>
    await store.GetAsync(id, ct) is null ? Results.NotFound() : Results.Ok(await store.AuditAsync(id, ct)));
foreach (var command in Enum.GetValues<JobCommand>())
{
    jobs.MapPost("/{id:guid}/" + command.ToString().ToLowerInvariant(), async (Guid id, VersionRequest request, IJobStore store, CancellationToken ct) =>
    {
        var j = await store.CommandAsync(id, command, request.Version, ct); return j is null ? Results.NotFound() : Results.Ok(j);
    });
}
await app.RunAsync();
public sealed record VersionRequest(Guid Version);
public partial class Program { }

