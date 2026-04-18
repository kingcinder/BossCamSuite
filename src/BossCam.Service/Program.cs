using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure;
using BossCam.Service.Hosted;

var builder = WebApplication.CreateBuilder(args);

var localApiBaseUrl = builder.Configuration["BossCam:LocalApiBaseUrl"] ?? "http://127.0.0.1:5317";
builder.WebHost.UseUrls(localApiBaseUrl);
builder.Host.UseWindowsService();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddBossCamInfrastructure(builder.Configuration);
builder.Services.AddBossCamCore();
builder.Services.AddHostedService<BossCamBootstrapWorker>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow }));

app.MapGet("/api/devices", async (IApplicationStore store, CancellationToken ct) => Results.Ok(await store.GetDevicesAsync(ct)));
app.MapPost("/api/devices/discover", async (DiscoveryCoordinator coordinator, CancellationToken ct) => Results.Ok(await coordinator.RunAsync(ct)));
app.MapPost("/api/devices/{id:guid}/probe", async (Guid id, CapabilityProbeService probeService, CancellationToken ct) =>
{
    var result = await probeService.ProbeAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/capabilities", async (Guid id, IApplicationStore store, CancellationToken ct) =>
{
    var result = await store.GetCapabilityMapAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/settings", async (Guid id, SettingsService settingsService, CancellationToken ct) =>
{
    var result = await settingsService.ReadAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/settings/last", async (Guid id, SettingsService settingsService, CancellationToken ct) =>
{
    var result = await settingsService.GetLastSnapshotAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/settings/write", async (Guid id, WritePlan plan, SettingsService settingsService, CancellationToken ct) =>
{
    var result = await settingsService.WriteAsync(id, plan, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/maintenance/{operation}", async (Guid id, string operation, JsonObject? payload, SettingsService settingsService, CancellationToken ct) =>
{
    if (!Enum.TryParse<MaintenanceOperation>(operation, true, out var parsed))
    {
        return Results.BadRequest(new { error = $"Unknown operation '{operation}'." });
    }

    var result = await settingsService.ExecuteMaintenanceAsync(id, parsed, payload, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/sources", async (Guid id, TransportBroker transportBroker, CancellationToken ct) => Results.Ok(await transportBroker.GetSourcesAsync(id, ct)));
app.MapGet("/api/devices/{id:guid}/preview", async (Guid id, TransportBroker transportBroker, CancellationToken ct) =>
{
    var result = await transportBroker.StartPreviewAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/protocols", async (ProtocolCatalogService protocolCatalogService, CancellationToken ct) => Results.Ok(await protocolCatalogService.GetAsync(ct)));
app.MapPost("/api/protocols/refresh", async (ProtocolCatalogService protocolCatalogService, CancellationToken ct) => Results.Ok(await protocolCatalogService.RefreshAsync(ct)));
app.MapGet("/api/diagnostics/audit", async (Guid? deviceId, int? limit, IApplicationStore store, CancellationToken ct) => Results.Ok(await store.GetAuditEntriesAsync(deviceId, limit ?? 100, ct)));
app.MapGet("/api/recordings", async (Guid? deviceId, IApplicationStore store, CancellationToken ct) => Results.Ok(await store.GetRecordingProfilesAsync(deviceId, ct)));
app.MapPost("/api/recordings", async (IEnumerable<RecordingProfile> profiles, IApplicationStore store, CancellationToken ct) =>
{
    await store.SaveRecordingProfilesAsync(profiles, ct);
    return Results.Accepted();
});
app.MapPost("/api/firmware/register", async (FirmwareRegisterRequest request, FirmwareCatalogService service, CancellationToken ct) =>
{
    var result = await service.RegisterAsync(request.FilePath, ct);
    return Results.Ok(result);
});
app.MapGet("/api/firmware", async (FirmwareCatalogService service, CancellationToken ct) => Results.Ok(await service.GetAsync(ct)));

app.Run();

public sealed record FirmwareRegisterRequest(string FilePath);
