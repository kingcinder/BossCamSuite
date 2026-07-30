using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.NativeBridge;
using Microsoft.Extensions.Options;

namespace BossCam.Service;

/// <summary>
/// Maps device insight &amp; analysis endpoints: semantic trust, constraints, dependencies,
/// image control, grouped-config, persistence verification, network recovery, and native-fallback assessment.
/// </summary>
public static class ApiDevicesInsightsEndpoints
{
    public static WebApplication MapDevicesInsightsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/devices/{id:guid}/semantic/history", async (Guid id, int? limit, SemanticTrustService semanticTrustService, CancellationToken ct) =>
            Results.Ok(await semanticTrustService.GetSemanticHistoryAsync(id, limit ?? 300, ct)));

        app.MapGet("/api/devices/{id:guid}/constraints", async (Guid id, IApplicationStore store, SemanticTrustService semanticTrustService, CancellationToken ct) =>
        {
            var fields = await store.GetNormalizedSettingFieldsAsync(id, ct);
            var firmware = fields.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            return Results.Ok(await semanticTrustService.GetConstraintProfilesAsync(firmware, ct));
        });

        app.MapGet("/api/devices/{id:guid}/dependencies", async (Guid id, IApplicationStore store, SemanticTrustService semanticTrustService, CancellationToken ct) =>
        {
            var fields = await store.GetNormalizedSettingFieldsAsync(id, ct);
            var firmware = fields.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            return Results.Ok(await semanticTrustService.GetDependencyMatricesAsync(firmware, ct));
        });

        app.MapPost("/api/devices/{id:guid}/constraints/discover", async (Guid id, ConstraintDiscoveryRequest request, SemanticTrustService semanticTrustService, CancellationToken ct) =>
        {
            var result = await semanticTrustService.DiscoverConstraintsAsync(request with { DeviceId = id }, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/network/recovery", async (Guid id, NetworkRecoveryContext context, SemanticTrustService semanticTrustService, CancellationToken ct) =>
            Results.Ok(await semanticTrustService.RecoverNetworkAsync(context with { DeviceId = id }, ct)));

        app.MapPost("/api/devices/{id:guid}/image/truth-sweep", async (Guid id, ImageTruthSweepRequest? request, ImageTruthService imageTruthService, CancellationToken ct) =>
        {
            var result = await imageTruthService.RunImageTruthSweepAsync(
                id,
                request?.IncludeBehaviorMapping ?? true,
                request?.RefreshFromDevice ?? true,
                request?.ExportRoot,
                ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/devices/{id:guid}/image/inventory", async (Guid id, ImageTruthService imageTruthService, CancellationToken ct) =>
            Results.Ok(await imageTruthService.GetInventoryAsync(id, ct)));

        app.MapGet("/api/devices/{id:guid}/image/writable-test-set", async (Guid id, ImageTruthService imageTruthService, CancellationToken ct) =>
        {
            var result = await imageTruthService.GetWritableTestSetAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/devices/{id:guid}/image/behavior-maps", async (Guid id, ImageTruthService imageTruthService, CancellationToken ct) =>
            Results.Ok(await imageTruthService.GetBehaviorMapsAsync(id, ct)));

        app.MapGet("/api/devices/{id:guid}/grouped-config/snapshots", async (Guid id, bool? refreshFromDevice, GroupedConfigService groupedConfigService, CancellationToken ct) =>
            Results.Ok(await groupedConfigService.GetGroupedConfigSnapshotsAsync(id, refreshFromDevice ?? false, ct)));

        app.MapGet("/api/devices/{id:guid}/grouped-config/profiles", async (Guid id, string? firmwareFingerprint, GroupedConfigService groupedConfigService, CancellationToken ct) =>
            Results.Ok(await groupedConfigService.GetProfilesAsync(id, firmwareFingerprint, ct)));

        app.MapGet("/api/devices/{id:guid}/grouped-config/retest-results", async (Guid id, int? limit, GroupedConfigService groupedConfigService, CancellationToken ct) =>
            Results.Ok(await groupedConfigService.GetRetestResultsAsync(id, limit ?? 400, ct)));

        app.MapPost("/api/devices/{id:guid}/grouped-config/retest-unsupported", async (Guid id, GroupedRetestRequest? request, GroupedConfigService groupedConfigService, CancellationToken ct) =>
            Results.Ok(await groupedConfigService.RetestUnsupportedFieldsAsync(id, request ?? new GroupedRetestRequest(), ct)));

        app.MapPost("/api/devices/{id:guid}/grouped-config/probe-families", async (Guid id, GroupedFamilyProbeRequest? request, GroupedConfigService groupedConfigService, CancellationToken ct) =>
            Results.Ok(await groupedConfigService.ProbeGroupedFamiliesAsync(id, request ?? new GroupedFamilyProbeRequest(), ct)));

        app.MapPost("/api/devices/{id:guid}/grouped-config/probe-pipeline-ownership", async (Guid id, PipelineOwnershipProbeRequest? request, GroupedConfigService groupedConfigService, CancellationToken ct) =>
            Results.Ok(await groupedConfigService.ProbePipelineOwnershipAsync(id, request ?? new PipelineOwnershipProbeRequest(), ct)));

        app.MapGet("/api/grouped-config/sdk-field-catalog", (GroupedConfigService groupedConfigService) =>
            Results.Ok(groupedConfigService.GetSdkFieldCatalog()));

        app.MapPost("/api/devices/{id:guid}/grouped-config/force-enumerate-sdk-fields", async (Guid id, ForcedEnumerationRequest? request, GroupedConfigService groupedConfigService, CancellationToken ct) =>
            Results.Ok(await groupedConfigService.ForceEnumerateSdkFieldsAsync(id, request ?? new ForcedEnumerationRequest(), ct)));

        app.MapGet("/api/devices/{id:guid}/persistence", async (Guid id, int? limit, PersistenceVerificationService persistenceVerificationService, CancellationToken ct) =>
            Results.Ok(await persistenceVerificationService.GetResultsAsync(id, limit ?? 100, ct)));

        app.MapGet("/api/devices/{id:guid}/persistence/eligible-fields", async (Guid id, TypedSettingsService typedSettingsService, CancellationToken ct) =>
            Results.Ok(await typedSettingsService.GetPersistenceEligibleFieldsAsync(id, ct)));

        app.MapPost("/api/devices/{id:guid}/persistence/verify", async (Guid id, PersistenceVerificationRequest request, PersistenceVerificationService persistenceVerificationService, CancellationToken ct) =>
        {
            var result = await persistenceVerificationService.VerifyAsync(request with { DeviceId = id }, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/persistence/verify-field", async (Guid id, PersistenceFieldVerifyRequest request, TypedSettingsService typedSettingsService, CancellationToken ct) =>
        {
            var result = await typedSettingsService.VerifyPersistenceForFieldAsync(id, request.FieldKey, request.Value, request.RebootForVerification, request.ExpertOverride, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/devices/{id:guid}/native-fallback-assessment", async (Guid id, IApplicationStore store, IEndpointContractCatalog contractCatalog, IOptions<BossCamRuntimeOptions> runtime, CancellationToken ct) =>
        {
            var device = await store.GetDeviceAsync(id, ct);
            if (device is null)
            {
                return Results.NotFound();
            }

            var contracts = await contractCatalog.GetContractsForDeviceAsync(device, ct);
            var fields = await store.GetNormalizedSettingFieldsAsync(id, ct);
            var required = new List<NativeFallbackRequirement>();
            foreach (var contract in contracts.Where(static contract => contract.Surface == ContractSurface.NativeFallback))
            {
                foreach (var field in contract.Fields)
                {
                    required.Add(new NativeFallbackRequirement
                    {
                        FieldKey = field.Key,
                        ContractKey = contract.ContractKey,
                        Reason = "Contract explicitly marked NativeFallback surface.",
                        LibraryHint = field.Key.Contains("ptz", StringComparison.OrdinalIgnoreCase) ? "NetSdk.dll" : null
                    });
                }
            }

            foreach (var field in fields.Where(static field => field.SupportState == ContractSupportState.Unsupported && !string.IsNullOrWhiteSpace(field.ContractKey)))
            {
                if (required.Any(item => item.FieldKey.Equals(field.FieldKey, StringComparison.OrdinalIgnoreCase) && item.ContractKey.Equals(field.ContractKey, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                required.Add(new NativeFallbackRequirement
                {
                    FieldKey = field.FieldKey,
                    ContractKey = field.ContractKey ?? string.Empty,
                    Reason = "HTTP/CGI path marked unsupported for this firmware evidence scope."
                });
            }

            var availableLibraries = NativeInteropProbe.Probe(runtime.Value.IpcamSuiteDirectory, runtime.Value.EseeCloudDirectory)
                .Where(static entry => entry.Loaded)
                .Select(static entry => entry.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(new NativeFallbackAssessment
            {
                DeviceId = id,
                FirmwareFingerprint = fields.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                RequiredFields = required,
                AvailableLibraries = availableLibraries
            });
        });

        return app;
    }
}
