using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class ProbeSessionService(
    IApplicationStore store,
    DiscoveryCoordinator discoveryCoordinator,
    CapabilityProbeService capabilityProbeService,
    ProtocolValidationService protocolValidationService,
    TypedSettingsService typedSettingsService,
    IContractEvidenceService contractEvidenceService,
    CapabilityPromotionService capabilityPromotionService,
    ILogger<ProbeSessionService> logger)
{
    public async Task<ProbeSession?> StartSessionAsync(ProbeSessionRequest request, CancellationToken cancellationToken)
    {
        if (request.DiscoverIfMissing)
        {
            await discoveryCoordinator.RunAsync(cancellationToken);
        }

        var device = await ResolveDeviceAsync(request, cancellationToken);
        if (device is null)
        {
            return null;
        }

        if (request.ResumeIfExists)
        {
            var existing = (await store.GetProbeSessionsAsync(device.Id, 30, cancellationToken))
                .FirstOrDefault(session => session.Mode == request.Mode && session.Status is ProbeSessionStatus.Running or ProbeSessionStatus.Partial or ProbeSessionStatus.Failed);
            if (existing is not null)
            {
                return await RunSessionAsync(existing with { ResumeRequested = true, Status = ProbeSessionStatus.Running }, device, request, cancellationToken);
            }
        }

        var created = new ProbeSession
        {
            DeviceId = device.Id,
            DeviceDisplayName = device.DisplayName,
            DeviceIp = device.IpAddress,
            Mode = request.Mode,
            Status = ProbeSessionStatus.Running,
            IncludePersistenceChecks = request.IncludePersistenceChecks,
            IncludeRollbackChecks = request.IncludeRollbackChecks
        };

        return await RunSessionAsync(created, device, request, cancellationToken);
    }

    public Task<IReadOnlyCollection<ProbeSession>> GetSessionsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
        => store.GetProbeSessionsAsync(deviceId, limit, cancellationToken);

    public Task<IReadOnlyCollection<ProbeStageResult>> GetStagesAsync(Guid sessionId, CancellationToken cancellationToken)
        => store.GetProbeStageResultsAsync(sessionId, cancellationToken);

    private async Task<ProbeSession> RunSessionAsync(ProbeSession session, DeviceIdentity device, ProbeSessionRequest request, CancellationToken cancellationToken)
    {
        await store.SaveProbeSessionAsync(session, cancellationToken);
        try
        {
            if (request.Mode == ProbeStageMode.InventoryOnly)
            {
                _ = await capabilityProbeService.ProbeAsync(device, cancellationToken);
                _ = await typedSettingsService.NormalizeDeviceAsync(device.Id, refreshFromDevice: true, cancellationToken);
            }
            else
            {
                var options = MapRunOptions(request.Mode, request.IncludePersistenceChecks, request.IncludeRollbackChecks);
                _ = await protocolValidationService.ValidateDeviceAsync(device, options, cancellationToken);
                _ = await typedSettingsService.NormalizeDeviceAsync(device.Id, refreshFromDevice: true, cancellationToken);
            }

            var profile = await capabilityPromotionService.PromoteForDeviceAsync(device.Id, cancellationToken);
            var validations = await store.GetEndpointValidationResultsAsync(device.Id, cancellationToken);
            var stages = BuildStageSummary(session.Id, device.Id, request.Mode, validations);
            await store.SaveProbeStageResultsAsync(stages, cancellationToken);

            string? bundlePath = null;
            if (!string.IsNullOrWhiteSpace(request.TranscriptExportDirectory))
            {
                bundlePath = await ExportTranscriptBundleAsync(session.Id, device.Id, request.TranscriptExportDirectory, cancellationToken);
                _ = await contractEvidenceService.PromoteFromTranscriptsAsync(device.Id, request.TranscriptExportDirectory, cancellationToken);
            }

            var completed = session with
            {
                FirmwareFingerprint = profile?.FirmwareFingerprint,
                AuthMode = "basic-or-digest",
                Status = ProbeSessionStatus.Completed,
                TranscriptBundlePath = bundlePath,
                CompletedAt = DateTimeOffset.UtcNow
            };
            await store.SaveProbeSessionAsync(completed, cancellationToken);
            return completed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Probe session failed for {Device}", device.DisplayName);
            var failed = session with
            {
                Status = ProbeSessionStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string> { ["error"] = ex.Message }
            };
            await store.SaveProbeSessionAsync(failed, cancellationToken);
            return failed;
        }
    }

    private async Task<DeviceIdentity?> ResolveDeviceAsync(ProbeSessionRequest request, CancellationToken cancellationToken)
    {
        var devices = await store.GetDevicesAsync(cancellationToken);
        if (request.DeviceId is Guid id)
        {
            return devices.FirstOrDefault(device => device.Id == id);
        }

        if (!string.IsNullOrWhiteSpace(request.DeviceIp))
        {
            var known = devices.FirstOrDefault(device => string.Equals(device.IpAddress, request.DeviceIp, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                return known;
            }

            var discovered = new DeviceIdentity
            {
                Name = $"5523-w-{request.DeviceIp}",
                DeviceType = "5523-w",
                IpAddress = request.DeviceIp,
                TransportProfiles =
                [
                    new TransportProfile { Kind = TransportKind.LanRest, Address = $"http://{request.DeviceIp}:80", Rank = 1 }
                ]
            };
            await store.UpsertDevicesAsync([discovered], cancellationToken);
            return discovered;
        }

        if (!string.IsNullOrWhiteSpace(request.ProfileName))
        {
            return devices.FirstOrDefault(device => device.Metadata.TryGetValue("profile", out var profile) && profile.Equals(request.ProfileName, StringComparison.OrdinalIgnoreCase));
        }

        return devices.FirstOrDefault();
    }

    private async Task<string> ExportTranscriptBundleAsync(Guid sessionId, Guid deviceId, string exportDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(exportDirectory);
        var exportPath = Path.Combine(exportDirectory, $"probe-session-{sessionId:N}.json");
        var transcripts = await store.GetEndpointTranscriptsAsync(deviceId, 5000, cancellationToken);
        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var validations = await store.GetEndpointValidationResultsAsync(deviceId, cancellationToken);
        var stages = await store.GetProbeStageResultsAsync(sessionId, cancellationToken);
        var payload = new JsonObject
        {
            ["sessionId"] = sessionId.ToString(),
            ["exportedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["transcripts"] = JsonSerializer.SerializeToNode(transcripts),
            ["normalizedFields"] = JsonSerializer.SerializeToNode(fields),
            ["validations"] = JsonSerializer.SerializeToNode(validations),
            ["stageSummary"] = JsonSerializer.SerializeToNode(stages)
        };
        await File.WriteAllTextAsync(exportPath, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return exportPath;
    }

    private static IReadOnlyCollection<ProbeStageResult> BuildStageSummary(Guid sessionId, Guid deviceId, ProbeStageMode mode, IReadOnlyCollection<EndpointValidationResult> validations)
        => validations
            .GroupBy(result => MapGroup(result.Endpoint))
            .Select(group => new ProbeStageResult
            {
                SessionId = sessionId,
                DeviceId = deviceId,
                GroupName = group.Key,
                Mode = mode,
                EndpointsTotal = group.Count(),
                ReadVerifiedCount = group.Count(static result => result.ReadVerified),
                WriteVerifiedCount = group.Count(static result => result.WriteVerified),
                PersistenceVerifiedCount = group.Count(static result => result.PersistsAfterReboot),
                RollbackSupportedCount = group.Count(static result => result.RollbackSupported),
                RebootEncountered = group.Any(static result => result.DisruptionClass == DisruptionClass.Reboot),
                Summary = $"{group.Key}: read={group.Count(static result => result.ReadVerified)} write={group.Count(static result => result.WriteVerified)}"
            })
            .ToList();

    private static string MapGroup(string endpoint)
    {
        var value = endpoint.ToLowerInvariant();
        if (value.Contains("/video") || value.Contains("/image"))
        {
            return "Video / Image";
        }

        if (value.Contains("/network") || value.Contains("wireless") || value.Contains("esee"))
        {
            return "Network / Wireless";
        }

        if (value.Contains("/user") || value.Contains("reboot") || value.Contains("upgrade") || value.Contains("default"))
        {
            return "Users / Maintenance";
        }

        if (value.Contains("motion") || value.Contains("alarm") || value.Contains("privacy"))
        {
            return "Motion / Privacy / Alarms";
        }

        if (value.Contains("ptz"))
        {
            return "PTZ / Optics";
        }

        if (value.Contains("sdcard") || value.Contains("tfcard") || value.Contains("storage"))
        {
            return "Storage / Playback";
        }

        return "Diagnostics";
    }

    private static ValidationRunOptions MapRunOptions(ProbeStageMode mode, bool includePersistenceChecks, bool includeRollbackChecks)
        => mode switch
        {
            ProbeStageMode.SafeReadOnly => new ValidationRunOptions
            {
                AttemptWrites = false,
                IncludeRollbackChecks = false
            },
            ProbeStageMode.SafeWriteVerify => new ValidationRunOptions
            {
                AttemptWrites = true,
                IncludeRollbackChecks = includeRollbackChecks,
                IncludePersistenceChecks = includePersistenceChecks,
                AllowedDisruptionClasses = [DisruptionClass.Safe, DisruptionClass.Transient]
            },
            ProbeStageMode.NetworkImpacting => new ValidationRunOptions
            {
                AttemptWrites = true,
                IncludeUnsafeWrites = true,
                IncludeRollbackChecks = includeRollbackChecks,
                IncludePersistenceChecks = includePersistenceChecks,
                AllowedDisruptionClasses = [DisruptionClass.Safe, DisruptionClass.Transient, DisruptionClass.NetworkChanging]
            },
            ProbeStageMode.RebootRequired => new ValidationRunOptions
            {
                AttemptWrites = true,
                IncludeUnsafeWrites = true,
                IncludeRollbackChecks = includeRollbackChecks,
                IncludePersistenceChecks = includePersistenceChecks,
                AllowedDisruptionClasses = [DisruptionClass.Safe, DisruptionClass.Transient, DisruptionClass.Reboot]
            },
            ProbeStageMode.ExpertFull => new ValidationRunOptions
            {
                AttemptWrites = true,
                IncludeUnsafeWrites = true,
                IncludeRollbackChecks = includeRollbackChecks,
                IncludePersistenceChecks = includePersistenceChecks
            },
            _ => new ValidationRunOptions { AttemptWrites = false }
        };
}
