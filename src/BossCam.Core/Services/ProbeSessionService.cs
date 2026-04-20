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
    SemanticTrustService semanticTrustService,
    IEndpointContractCatalog endpointContractCatalog,
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
                if (request.Mode is ProbeStageMode.SafeWriteVerify or ProbeStageMode.NetworkImpacting or ProbeStageMode.ExpertFull)
                {
                    _ = await semanticTrustService.DiscoverConstraintsAsync(new ConstraintDiscoveryRequest
                    {
                        DeviceId = device.Id,
                        FieldKeys = ["brightness", "contrast", "saturation", "sharpness", "bitrate", "frameRate"],
                        IncludeNetworkChanging = request.Mode is ProbeStageMode.NetworkImpacting or ProbeStageMode.ExpertFull,
                        ExpertOverride = request.Mode == ProbeStageMode.ExpertFull,
                        DelaySeconds = 2
                    }, cancellationToken);
                }
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

    public async Task<TruthSweepReport> BuildTruthSweepReportAsync(IReadOnlyCollection<string>? targetIps, CancellationToken cancellationToken)
    {
        var devices = await store.GetDevicesAsync(cancellationToken);
        if (targetIps is { Count: > 0 })
        {
            devices = devices
                .Where(device => !string.IsNullOrWhiteSpace(device.IpAddress) && targetIps.Contains(device.IpAddress, StringComparer.OrdinalIgnoreCase))
                .GroupBy(device => device.IpAddress!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(device => string.Equals(device.DeviceType, "IPC", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(device => string.Equals(device.DisplayName, "5523-W", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(device => device.DiscoveredAt)
                    .First())
                .ToList();
        }

        var profiles = new List<DeviceTruthProfile>();
        foreach (var device in devices)
        {
            var validations = await store.GetEndpointValidationResultsAsync(device.Id, cancellationToken);
            var fields = await store.GetNormalizedSettingFieldsAsync(device.Id, cancellationToken);
            var responsive = validations.Where(validation => validation.ReadVerified).Select(validation => validation.Endpoint).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var authModes = validations.Select(validation => validation.AuthMode).Where(static mode => !string.IsNullOrWhiteSpace(mode)).Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().ToList();
            var stream = validations.Where(validation => validation.Endpoint.Contains("/Stream/", StringComparison.OrdinalIgnoreCase) && validation.ReadVerified).Select(validation => validation.Endpoint).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var topGroup = fields.Where(field => field.GroupKind is TypedSettingGroupKind.VideoImage or TypedSettingGroupKind.NetworkWireless or TypedSettingGroupKind.UsersMaintenance).ToList();
            var contracts = await endpointContractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
            var expectedTopWritable = contracts
                .Where(contract => contract.GroupKind is TypedSettingGroupKind.VideoImage or TypedSettingGroupKind.NetworkWireless or TypedSettingGroupKind.UsersMaintenance)
                .SelectMany(contract => contract.Fields.Where(field => field.Writable).Select(field => field.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var supportedTop = topGroup.Count(field => field.SupportState == ContractSupportState.Supported);
            var uncertainTop = topGroup.Count(field => field.SupportState == ContractSupportState.Uncertain);
            var unsupportedTop = topGroup.Count(field => field.SupportState == ContractSupportState.Unsupported);
            if (topGroup.Count == 0 && expectedTopWritable > 0)
            {
                unsupportedTop = expectedTopWritable;
            }
            var fingerprint = fields.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
                ?? $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";
            var notes = new List<string>();
            if (validations.All(validation => !validation.WriteVerified))
            {
                notes.Add("No write-verified endpoints yet.");
            }
            if (responsive.Count == 0)
            {
                notes.Add("No read-verified endpoints discovered.");
            }

            profiles.Add(new DeviceTruthProfile
            {
                DeviceId = device.Id,
                DisplayName = device.DisplayName,
                IpAddress = device.IpAddress,
                FirmwareFingerprint = fingerprint,
                FirmwareVersion = device.FirmwareVersion,
                HardwareModel = device.HardwareModel,
                DeviceType = device.DeviceType,
                EndpointsObserved = validations.Count,
                EndpointsReadVerified = validations.Count(validation => validation.ReadVerified),
                EndpointsWriteVerified = validations.Count(validation => validation.WriteVerified),
                TopGroupFieldsSupported = supportedTop,
                TopGroupFieldsUncertain = uncertainTop,
                TopGroupFieldsUnsupported = unsupportedTop,
                ResponsiveEndpoints = responsive,
                AuthModesObserved = authModes,
                StreamDescriptorEndpoints = stream,
                Notes = notes
            });
        }

        var clusters = profiles
            .GroupBy(profile => profile.FirmwareFingerprint, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FirmwareTruthCluster
            {
                FirmwareFingerprint = group.Key,
                DeviceIds = group.Select(profile => profile.DeviceId).ToList(),
                Ips = group.Select(profile => profile.IpAddress).Where(static ip => !string.IsNullOrWhiteSpace(ip)).Cast<string>().ToList(),
                EndpointsReadVerified = group.Sum(profile => profile.EndpointsReadVerified),
                EndpointsWriteVerified = group.Sum(profile => profile.EndpointsWriteVerified),
                SupportedTopGroupFields = group.Sum(profile => profile.TopGroupFieldsSupported),
                UnsupportedTopGroupFields = group.Sum(profile => profile.TopGroupFieldsUnsupported)
            })
            .ToList();

        return new TruthSweepReport
        {
            Devices = profiles,
            Clusters = clusters
        };
    }
}
