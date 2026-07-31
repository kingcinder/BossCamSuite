using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Options;

namespace BossCam.Service;

/// <summary>
/// Maps firmware, contract, and protocol catalog endpoints.
/// All three domains share lightweight catalog/evidence patterns.
/// </summary>
public static class ApiFirmwareContractsProtocolsEndpoints
{
    public static WebApplication MapFirmwareContractsProtocolsEndpoints(this WebApplication app)
    {
        // ---- Firmware ----
        app.MapGet("/api/firmware/capabilities", async (CapabilityPromotionService capabilityPromotionService, CancellationToken ct) =>
            Results.Ok(await capabilityPromotionService.GetProfilesAsync(ct)));

        app.MapPost("/api/firmware/register", async (FirmwareRegisterRequest request, HttpContext http, FirmwareCatalogService service, IOptions<BossCamRuntimeOptions> options, ILogger<Program> logger, CancellationToken ct) =>
        {
            // Directory allow-list: only firmware inside the configured artifact directory (or
            // explicit extra roots) may be cataloged. This prevents an attacker with API access
            // from hashing/reading arbitrary host files by proxy.
            if (!FirmwarePathPolicy.IsAllowed(request.FilePath, options.Value, out var reason))
            {
                return Results.BadRequest(new { error = reason });
            }

            logger.LogInformation("firmware/register callerIP={IP} path={Path}", http.Connection.RemoteIpAddress, request.FilePath);
            var result = await service.RegisterAsync(request.FilePath, ct);
            return Results.Ok(result);
        }).RequireRateLimiting("firmware-register");

        app.MapGet("/api/firmware", async (FirmwareCatalogService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(ct)));

        // ---- Contracts ----
        app.MapGet("/api/contracts/endpoints", async (Guid? deviceId, IApplicationStore store, IEndpointContractCatalog catalog, CancellationToken ct) =>
        {
            if (deviceId is Guid id)
            {
                var device = await store.GetDeviceAsync(id, ct);
                if (device is null)
                {
                    return Results.NotFound();
                }

                return Results.Ok(await catalog.GetContractsForDeviceAsync(device, ct));
            }

            return Results.Ok(await catalog.GetContractsAsync(ct));
        });

        app.MapPost("/api/contracts/fixtures/promote/{deviceId:guid}", async (Guid deviceId, ContractFixturePromotionRequest request, IContractEvidenceService evidenceService, CancellationToken ct) =>
            Results.Ok(await evidenceService.PromoteFromTranscriptsAsync(deviceId, request.ExportRoot, ct)));

        app.MapGet("/api/contracts/fixtures", async (Guid? deviceId, IContractEvidenceService evidenceService, CancellationToken ct) =>
            Results.Ok(await evidenceService.GetFixturesAsync(deviceId, ct)));

        app.MapPost("/api/contracts/fixtures/cleanup", async (ContractFixtureCleanupRequest request, IContractEvidenceService evidenceService, CancellationToken ct) =>
            Results.Ok(await evidenceService.CleanupAsync(
                request.OlderThanDays,
                request.MaxPerDevice,
                request.MaxTotal,
                ct)));

        // ---- Protocols ----
        app.MapGet("/api/protocols", async (ProtocolCatalogService protocolCatalogService, CancellationToken ct) =>
            Results.Ok(await protocolCatalogService.GetAsync(ct)));

        app.MapPost("/api/protocols/refresh", async (ProtocolCatalogService protocolCatalogService, CancellationToken ct) =>
            Results.Ok(await protocolCatalogService.RefreshAsync(ct)));

        return app;
    }
}
