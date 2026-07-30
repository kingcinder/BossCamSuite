using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure;
using BossCam.NativeBridge;
using Microsoft.Extensions.Options;

namespace BossCam.Service;

/// <summary>
/// Maps device CRUD, discovery, registration, probe, validation, settings, maintenance,
/// capabilities, control-points, endpoint-surface, and typed-setting endpoints.
/// </summary>
public static class ApiDevicesEndpoints
{
    public static WebApplication MapDevicesEndpoints(this WebApplication app)
    {
        app.MapGet("/api/devices", async (IApplicationStore store, CancellationToken ct) =>
        {
            var devices = await store.GetDevicesAsync(ct);
            var withIp = devices.Where(static device => !string.IsNullOrWhiteSpace(device.IpAddress))
                .GroupBy(device => device.IpAddress!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(static device => string.Equals(device.DeviceType, "IPC", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(static device => !string.IsNullOrWhiteSpace(device.LoginName))
                    .ThenByDescending(static device => !string.IsNullOrWhiteSpace(device.Password) || !string.IsNullOrWhiteSpace(device.PasswordCiphertext))
                    .ThenByDescending(static device => string.Equals(device.DisplayName, "5523-W", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(static device => !string.IsNullOrWhiteSpace(device.FirmwareVersion))
                    .ThenByDescending(static device => !string.IsNullOrWhiteSpace(device.HardwareModel))
                    .ThenByDescending(static device => device.DiscoveredAt)
                    .First())
                .ToList();
            var withoutIp = devices.Where(static device => string.IsNullOrWhiteSpace(device.IpAddress)).ToList();
            return Results.Ok(withIp.Concat(withoutIp).OrderByDescending(static device => device.DiscoveredAt).ToList());
        });

        app.MapPost("/api/devices/discover", async (DiscoveryCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.RunAsync(ct)));

        app.MapPost("/api/devices/register", async (DeviceRegisterRequest request, DeviceRegistrationService registrationService, CancellationToken ct) =>
            Results.Ok(await registrationService.RegisterAsync(request, ct)));

        app.MapPost("/api/devices/register-many", async (List<DeviceRegisterRequest> requests, DeviceRegistrationService registrationService, CancellationToken ct) =>
            Results.Ok(await registrationService.RegisterManyAsync(requests ?? [], ct)));

        app.MapPost("/api/devices/register-aegon-lan", async (AegonLanRegisterRequest? request, DeviceRegistrationService registrationService, CancellationToken ct) =>
            Results.Ok(await registrationService.RegisterAegonLanDefaultsAsync(request?.LorexPassword, request?.WvcPassword, ct)));

        app.MapPost("/api/devices/{id:guid}/probe", async (Guid id, CapabilityProbeService probeService, CancellationToken ct) =>
        {
            var result = await probeService.ProbeAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireRateLimiting("probe");

        app.MapPost("/api/devices/{id:guid}/validation/run", async (Guid id, ValidationRunOptions? options, ProtocolValidationService validationService, CancellationToken ct) =>
        {
            var result = await validationService.ValidateDeviceAsync(id, options, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/devices/{id:guid}/validation", async (Guid id, ProtocolValidationService validationService, CancellationToken ct) =>
            Results.Ok(await validationService.GetValidationResultsAsync(id, ct)));

        app.MapGet("/api/devices/{id:guid}/validation/transcripts", async (Guid id, int? limit, ProtocolValidationService validationService, CancellationToken ct) =>
            Results.Ok(await validationService.GetTranscriptsAsync(id, limit ?? 200, ct)));

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

        app.MapGet("/api/devices/{id:guid}/settings/typed", async (Guid id, TypedSettingsService typedSettingsService, CancellationToken ct) =>
            Results.Ok(await typedSettingsService.GetTypedSettingsAsync(id, ct)));

        app.MapPost("/api/devices/{id:guid}/settings/typed/refresh", async (Guid id, TypedSettingsService typedSettingsService, CancellationToken ct) =>
            Results.Ok(await typedSettingsService.NormalizeDeviceAsync(id, refreshFromDevice: true, ct)));

        app.MapPost("/api/devices/{id:guid}/settings/typed/apply", async (Guid id, TypedSettingApplyRequest request, TypedSettingsService typedSettingsService, CancellationToken ct) =>
        {
            var result = await typedSettingsService.ApplyTypedFieldAsync(id, request.FieldKey, request.Value, request.ExpertOverride, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/settings/typed/apply-batch", async (Guid id, TypedSettingBatchApplyRequest request, TypedSettingsService typedSettingsService, CancellationToken ct) =>
            Results.Ok(await typedSettingsService.ApplyTypedChangesAsync(id, request.Changes, request.ExpertOverride, ct)));

        app.MapGet("/api/devices/{id:guid}/control-points", async (Guid id, ControlPointInventoryService controlPointInventoryService, CancellationToken ct) =>
        {
            var result = await controlPointInventoryService.GetReportAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/devices/{id:guid}/endpoint-surface", async (Guid id, EndpointSurfaceService endpointSurfaceService, CancellationToken ct) =>
        {
            var result = await endpointSurfaceService.GetReportAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return app;
    }
}
