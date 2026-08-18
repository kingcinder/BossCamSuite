using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class CapabilityProbeService(
    IEnumerable<IControlAdapter> controlAdapters,
    IApplicationStore store,
    IBossCamEventBroadcaster broadcaster,
    ILogger<CapabilityProbeService> logger)
{
    public async Task<CapabilityMap?> ProbeAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        return device is null ? null : await ProbeAsync(device, cancellationToken);
    }

    public async Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        CapabilityMap? combined = null;
        var adapterList = controlAdapters.OrderBy(static adapter => adapter.Priority).ToList();
        var verifiedCount = 0;
        for (var i = 0; i < adapterList.Count; i++)
        {
            var adapter = adapterList[i];
            _ = broadcaster.ProbeProgressAsync(device.Id, adapter.Name, verifiedCount, false, null, cancellationToken);

            bool canHandle;
            try
            {
                canHandle = await adapter.CanHandleAsync(device, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Adapter {Adapter} capability check failed for {Device}", adapter.Name, device.DisplayName);
                _ = broadcaster.ProbeProgressAsync(device.Id, adapter.Name, verifiedCount, false, ex.Message, cancellationToken);
                continue;
            }

            if (!canHandle)
            {
                _ = broadcaster.ProbeProgressAsync(device.Id, adapter.Name, verifiedCount, false, "Cannot handle device", cancellationToken);
                continue;
            }

            try
            {
                var map = await adapter.ProbeAsync(device, cancellationToken);
                combined = Merge(combined, map);
                verifiedCount++;
                _ = broadcaster.ProbeProgressAsync(device.Id, adapter.Name, verifiedCount, false, null, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Adapter {Adapter} probe failed for {Device}", adapter.Name, device.DisplayName);
                _ = broadcaster.ProbeProgressAsync(device.Id, adapter.Name, verifiedCount, false, ex.Message, cancellationToken);
            }
        }

        _ = broadcaster.ProbeProgressAsync(device.Id, "Complete", verifiedCount, true, null, cancellationToken);

        combined ??= new CapabilityMap
        {
            DeviceId = device.Id,
            Notes = new Dictionary<string, string> { ["probe"] = "No adapter reported capabilities." }
        };

        await store.SaveCapabilityMapAsync(combined, cancellationToken);
        return combined;
    }

    private static CapabilityMap Merge(CapabilityMap? left, CapabilityMap right)
    {
        if (left is null)
        {
            return right;
        }

        return left with
        {
            PrimaryControlAdapter = left.PrimaryControlAdapter ?? right.PrimaryControlAdapter,
            ControlAdapters = left.ControlAdapters.Concat(right.ControlAdapters).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            VideoTransportKinds = left.VideoTransportKinds.Concat(right.VideoTransportKinds).Distinct().ToList(),
            SupportedSettingGroups = left.SupportedSettingGroups.Concat(right.SupportedSettingGroups).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SupportedEndpointPaths = left.SupportedEndpointPaths.Concat(right.SupportedEndpointPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SupportedMaintenanceOperations = left.SupportedMaintenanceOperations.Concat(right.SupportedMaintenanceOperations).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Notes = left.Notes.Concat(right.Notes).GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(static group => group.Key, static group => group.Last().Value, StringComparer.OrdinalIgnoreCase),
            CapturedAt = left.CapturedAt >= right.CapturedAt ? left.CapturedAt : right.CapturedAt
        };
    }
}

public sealed class SettingsService(
    IEnumerable<IControlAdapter> controlAdapters,
    IApplicationStore store,
    ProtocolValidationService protocolValidationService,
    ILogger<SettingsService> logger)
{
    // Per-device cooldown between automatic clock-sync attempts. NormalizeDeviceAsync is a hot
    // path (image-truth sweeps re-normalize per field read), so without this every sweep would
    // fire identical TimeSync writes (2 bare-scalar PUTs + an audit row + adapter-resolution
    // probes each). The timestamp is recorded on ATTEMPT (not only success): an offline camera
    // is not re-probed on every normalize call, at the cost that a camera recovering mid-window
    // waits at most AutoSyncClockCooldown (5 minutes) for its next sync — acceptable for keeping
    // the OSD clock correct without hammering the unit.
    private static readonly TimeSpan AutoSyncClockCooldown = TimeSpan.FromMinutes(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _lastAutoSyncClockAttempt = new();

    public async Task<SettingsSnapshot?> ReadAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var adapter = await ResolveAdapterAsync(device, null, cancellationToken);
        if (adapter is null)
        {
            return null;
        }

        var snapshot = await adapter.ReadAsync(device, cancellationToken);
        // Never persist or echo credentials: a custom adapter may embed secret-bearing fields
        // (e.g. the remote-command envelope used to carry device.Password) inside its groups.
        // Redact at this boundary so both the SQLite settings-snapshot table and the /settings
        // API response stay clean regardless of adapter behaviour.
        var redacted = RedactSnapshot(snapshot);
        await store.SaveSettingsSnapshotAsync(redacted, cancellationToken);
        return redacted;
    }

    public Task<SettingsSnapshot?> GetLastSnapshotAsync(Guid deviceId, CancellationToken cancellationToken)
        => store.GetSettingsSnapshotAsync(deviceId, cancellationToken);

    public async Task<WriteResult?> WriteAsync(Guid deviceId, WritePlan plan, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var adapter = await ResolveAdapterAsync(device, plan.AdapterName, cancellationToken);
        if (adapter is null)
        {
            logger.LogWarning(
                "Write routing failed: no adapter matched. device={Device} ip={Ip} requestedAdapter={RequestedAdapter} endpoint={Endpoint} method={Method} payload={Payload}",
                device.DisplayName,
                device.IpAddress,
                plan.AdapterName ?? "(none)",
                plan.Endpoint,
                plan.Method,
                plan.Payload?.ToJsonString() ?? string.Empty);
            return new WriteResult { Success = false, AdapterName = plan.AdapterName ?? string.Empty, Message = "No control adapter matched the device." };
        }

        if (plan.RequireWriteVerification)
        {
            var isWriteVerified = await protocolValidationService.IsEndpointWriteVerifiedAsync(device.Id, adapter.Name, plan.Endpoint, cancellationToken);
            if (!isWriteVerified && !await HasGroupedWritableFallbackAsync(device.Id, plan, cancellationToken))
            {
                return new WriteResult
                {
                    Success = false,
                    AdapterName = adapter.Name,
                    Message = $"Endpoint '{plan.Endpoint}' is not write-verified for adapter '{adapter.Name}'. Run protocol validation first."
                };
            }
        }

        SettingsSnapshot? beforeSnapshot = null;
        if (plan.SnapshotBeforeWrite)
        {
            beforeSnapshot = await adapter.SnapshotAsync(device, cancellationToken);
            // Defense-in-depth: the pre-write snapshot is persisted AND echoed back through
            // WriteResult.SnapshotBeforeWrite, so both boundaries must pass through the same
            // redaction. Not currently exploitable (the only adapter that embeds a password-shaped
            // field is OwnedRemoteCommandAdapter, fixed at source) but keeps the call sites uniform.
            beforeSnapshot = RedactSnapshot(beforeSnapshot);
            await store.SaveSettingsSnapshotAsync(beforeSnapshot, cancellationToken);
        }

        var preReadResult = await adapter.ApplyAsync(device, new WritePlan
        {
            AdapterName = adapter.Name,
            GroupName = plan.GroupName,
            Endpoint = plan.Endpoint,
            Method = "GET",
            SnapshotBeforeWrite = false,
            RequireWriteVerification = false
        }, cancellationToken);

        var preReadVerified = preReadResult.Success;
        var result = await adapter.ApplyAsync(device, plan, cancellationToken);
        var postReadResult = await adapter.ApplyAsync(device, new WritePlan
        {
            AdapterName = adapter.Name,
            GroupName = plan.GroupName,
            Endpoint = plan.Endpoint,
            Method = "GET",
            SnapshotBeforeWrite = false,
            RequireWriteVerification = false
        }, cancellationToken);

        var postReadVerified = postReadResult.Success;
        var rollbackAttempted = false;
        var rollbackSucceeded = false;

        if (plan.AllowRollback
            && result.Success
            && preReadResult.Response is JsonObject preObject
            && postReadResult.Response is JsonNode postNode
            && !JsonNode.DeepEquals(preReadResult.Response, postNode))
        {
            rollbackAttempted = true;
            var rollbackResult = await adapter.ApplyAsync(device, new WritePlan
            {
                AdapterName = adapter.Name,
                GroupName = plan.GroupName,
                Endpoint = plan.Endpoint,
                Method = plan.Method,
                Payload = preObject,
                SnapshotBeforeWrite = false,
                RequireWriteVerification = false,
                AllowRollback = false
            }, cancellationToken);
            rollbackSucceeded = rollbackResult.Success;
        }

        var finalResult = result with
        {
            SnapshotBeforeWrite = beforeSnapshot ?? result.SnapshotBeforeWrite,
            PreReadVerified = preReadVerified,
            PostReadVerified = postReadVerified,
            RollbackAttempted = rollbackAttempted,
            RollbackSucceeded = rollbackSucceeded,
            PreWriteValue = preReadResult.Response?.DeepClone(),
            PostWriteValue = postReadResult.Response?.DeepClone()
        };

        // The result is returned to the API caller as well as audited — redact any secret-bearing
        // fields in the response so a relay/adapter echoing credentials never leaks over the wire.
        finalResult = finalResult with
        {
            Response = SensitiveDataRedactor.Redact(finalResult.Response),
            PreWriteValue = SensitiveDataRedactor.Redact(finalResult.PreWriteValue),
            PostWriteValue = SensitiveDataRedactor.Redact(finalResult.PostWriteValue)
        };

        await store.AddAuditEntryAsync(new WriteAuditEntry
        {
            DeviceId = device.Id,
            AdapterName = adapter.Name,
            Operation = plan.Method,
            Endpoint = plan.Endpoint,
            RequestContent = SensitiveDataRedactor.Redact(RedactPayload(plan.Payload, plan.SensitivePaths))?.ToJsonString(),
            ResponseContent = SensitiveDataRedactor.Redact(new JsonObject
            {
                ["write"] = finalResult.Response?.DeepClone(),
                ["preRead"] = preReadResult.Response?.DeepClone(),
                ["postRead"] = postReadResult.Response?.DeepClone(),
                ["preReadVerified"] = preReadVerified,
                ["postReadVerified"] = postReadVerified,
                ["rollbackAttempted"] = rollbackAttempted,
                ["rollbackSucceeded"] = rollbackSucceeded
            })?.ToJsonString(),
            Success = finalResult.Success,
            SemanticStatus = finalResult.SemanticStatus,
            BlockReason = finalResult.Success ? null : finalResult.Message
        }, cancellationToken);

        return finalResult;
    }

    private async Task<bool> HasGroupedWritableFallbackAsync(Guid deviceId, WritePlan plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.Endpoint))
        {
            return false;
        }

        var groupedResults = await store.GetGroupedRetestResultsAsync(deviceId, 1000, cancellationToken);
        return groupedResults.Any(result =>
            result.SourceEndpoint.Equals(plan.Endpoint, StringComparison.OrdinalIgnoreCase)
            && result.Classification is ForcedFieldClassification.Writable
                or ForcedFieldClassification.WritableNeedsCommitTrigger
                or ForcedFieldClassification.DelayedApply);
    }

    public async Task<MaintenanceResult?> ExecuteMaintenanceAsync(Guid deviceId, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var adapter = await ResolveAdapterAsync(device, null, cancellationToken);
        if (adapter is null)
        {
            return new MaintenanceResult { Success = false, Operation = operation, Message = "No control adapter matched the device." };
        }

        var result = await adapter.ExecuteMaintenanceAsync(device, operation, payload, cancellationToken);
        await store.AddAuditEntryAsync(new WriteAuditEntry
        {
            DeviceId = device.Id,
            AdapterName = adapter.Name,
            Operation = operation.ToString(),
            Endpoint = operation.ToString(),
            RequestContent = SensitiveDataRedactor.Redact(payload)?.ToJsonString(),
            ResponseContent = SensitiveDataRedactor.Redact(result.Response)?.ToJsonString(),
            Success = result.Success,
            SemanticStatus = result.Success ? SemanticWriteStatus.AcceptedChanged : SemanticWriteStatus.Rejected,
            BlockReason = result.Success ? null : result.Message
        }, cancellationToken);

        return result with { Response = SensitiveDataRedactor.Redact(result.Response) };
    }

    /// <summary>
    /// Best-effort automatic clock sync for firmware-proven 5523-W units, invoked when a device
    /// registers or is normalized so the OSD clock is always correct without pressing the
    /// "Sync Camera Clock" button. Routes through <see cref="ExecuteMaintenanceAsync"/> so the
    /// exact proven bare-scalar RTC + timeZone writes are used and the attempt is audited.
    /// NEVER throws: a failure (offline camera, gated endpoint, no adapter) is logged and
    /// swallowed so registration/normalization always succeed.
    /// </summary>
    public async Task AutoSyncClockAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        if (!Is5523W(device))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var lastAttempt = _lastAutoSyncClockAttempt.GetValueOrDefault(device.Id);
        if (now - lastAttempt < AutoSyncClockCooldown)
        {
            return;
        }

        _lastAutoSyncClockAttempt[device.Id] = now;
        try
        {
            // Send an explicit empty object (mirrors the SPA/desktop '{}' maintenance body) so
            // the maintenance path always receives a well-formed JSON payload.
            var result = await ExecuteMaintenanceAsync(device.Id, MaintenanceOperation.TimeSync, new JsonObject(), cancellationToken);
            if (result?.Success == true)
            {
                logger.LogInformation("Auto clock sync OK for {Device} ({Ip})", device.DisplayName, device.IpAddress);
            }
            else
            {
                logger.LogWarning("Auto clock sync FAILED for {Device} ({Ip}): {Message}", device.DisplayName, device.IpAddress, result?.Message ?? "no maintenance response");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto clock sync skipped for {Device} ({Ip})", device.DisplayName, device.IpAddress);
        }
    }

    /// <summary>True for firmware-proven 5523-W / 5523-family IPC units (case-insensitive).</summary>
    public static bool Is5523W(DeviceIdentity device)
        => device.HardwareModel?.Contains("5523", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Runs a full clock-verification pass on one device: probe <c>/NetSDK/System/time/rtc</c>
    /// + <c>/timeZone</c>, run TimeSync, re-read both, and confirm the OSD epoch is within
    /// <c>BossCam:ClockVerifyToleranceSeconds</c> of the host epoch. Routes through
    /// <see cref="ExecuteMaintenanceAsync"/> (ClockVerify) so the exact proven bare-document
    /// wire forms are used and the attempt is audited. Returns null when the device is unknown;
    /// a failed pass returns a structured result with <c>Success=false</c>, never throws.
    /// </summary>
    public async Task<ClockVerificationResult?> VerifyClockAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        try
        {
            var result = await ExecuteMaintenanceAsync(deviceId, MaintenanceOperation.ClockVerify, new JsonObject(), cancellationToken);
            if (result is null)
            {
                return new ClockVerificationResult { DeviceId = deviceId, DeviceName = device.DisplayName, Success = false, Message = "No maintenance response." };
            }

            return new ClockVerificationResult
            {
                DeviceId = deviceId,
                DeviceName = device.DisplayName,
                Success = result.Success,
                AdapterName = result.AdapterName,
                Message = result.Message,
                RtcBefore = ReadReportLong(result, "rtcBefore"),
                TimeZoneBefore = ReadReportString(result, "timeZoneBefore"),
                HostEpoch = ReadReportLong(result, "hostEpoch") ?? 0,
                RtcAfter = ReadReportLong(result, "rtcAfter"),
                TimeZoneAfter = ReadReportString(result, "timeZoneAfter"),
                DriftSeconds = ReadReportLong(result, "driftSeconds"),
                ToleranceSeconds = (int)(ReadReportLong(result, "toleranceSeconds") ?? 30),
                TimeZoneMatchesHost = result.Response is JsonObject timeReport && timeReport["tzMatchesHost"] is JsonValue tzNode && tzNode.TryGetValue<bool>(out var tzMatches) && tzMatches
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Clock verify failed for {Device} ({Ip})", device.DisplayName, device.IpAddress);
            return new ClockVerificationResult { DeviceId = deviceId, DeviceName = device.DisplayName, Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Fleet-wide clock verification for every registered 5523-W: probe rtc + timeZone, run
    /// TimeSync, and confirm each OSD epoch matches the host. Best-effort — one unreachable
    /// camera is reported as a failed result, never aborts the sweep.
    /// </summary>
    public async Task<ClockFleetReport> VerifyAll5523ClocksAsync(CancellationToken cancellationToken)
    {
        var devices = await store.GetDevicesAsync(cancellationToken);
        var targets = devices.Where(Is5523W).ToList();
        var results = new List<ClockVerificationResult>(targets.Count);
        foreach (var device in targets)
        {
            var result = await VerifyClockAsync(device.Id, cancellationToken);
            if (result is not null)
            {
                results.Add(result);
            }
            else
            {
                // VerifyClockAsync only returns null when the device vanished from the store
                // mid-sweep — count it as failed so Checked = Verified + Failed stays invariant.
                results.Add(new ClockVerificationResult { DeviceId = device.Id, DeviceName = device.DisplayName, Success = false, Message = "Device disappeared during sweep." });
            }
        }

        return new ClockFleetReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            DevicesChecked = targets.Count,
            DevicesVerified = results.Count(static result => result.Success),
            DevicesFailed = results.Count(static result => !result.Success),
            Results = results
        };
    }

    private static long? ReadReportLong(MaintenanceResult result, string key)
        => result.Response is JsonObject report && report[key] is JsonValue value && value.TryGetValue<long>(out var number)
            ? number
            : null;

    private static string? ReadReportString(MaintenanceResult result, string key)
    {
        if (result.Response is not JsonObject report || report[key] is not JsonValue value)
        {
            return null;
        }

        var raw = value.ToJsonString().Trim('"');
        return raw;
    }

    private async Task<IControlAdapter?> ResolveAdapterAsync(DeviceIdentity device, string? requestedAdapterName, CancellationToken cancellationToken)
    {
        var ordered = controlAdapters.OrderBy(static adapter => adapter.Priority).ToList();
        var candidates = ordered
            .Where(adapter => string.IsNullOrWhiteSpace(requestedAdapterName) || adapter.Name.Equals(requestedAdapterName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            logger.LogWarning(
                "No adapter candidates after name filter. device={Device} ip={Ip} requested={RequestedAdapter} known={Known}",
                device.DisplayName,
                device.IpAddress,
                requestedAdapterName,
                string.Join(",", ordered.Select(static adapter => adapter.Name)));
            return null;
        }

        foreach (var adapter in candidates)
        {
            try
            {
                var canHandle = await adapter.CanHandleAsync(device, cancellationToken);
                logger.LogInformation(
                    "Adapter capability probe. device={Device} ip={Ip} requested={RequestedAdapter} adapter={Adapter} priority={Priority} canHandle={CanHandle}",
                    device.DisplayName,
                    device.IpAddress,
                    requestedAdapterName ?? "(none)",
                    adapter.Name,
                    adapter.Priority,
                    canHandle);
                if (canHandle)
                {
                    return adapter;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Adapter {Adapter} resolution failed for {Device}", adapter.Name, device.DisplayName);
            }
        }

        logger.LogWarning(
            "No adapter matched device after capability probes. device={Device} ip={Ip} requested={RequestedAdapter}",
            device.DisplayName,
            device.IpAddress,
            requestedAdapterName ?? "(none)");
        return null;
    }

    private static SettingsSnapshot RedactSnapshot(SettingsSnapshot snapshot)
    {
        var groups = snapshot.Groups
            .Select(group => group with
            {
                RawPayload = SensitiveDataRedactor.Redact(group.RawPayload),
                Values = group.Values.ToDictionary(
                    static pair => pair.Key,
                    pair => pair.Value with { Value = SensitiveDataRedactor.Redact(pair.Value.Value) },
                    StringComparer.OrdinalIgnoreCase)
            })
            .ToList();

        return snapshot with { Groups = groups };
    }

    private static JsonObject? RedactPayload(JsonObject? payload, IReadOnlyCollection<string> sensitivePaths)
    {
        if (payload is null)
        {
            return null;
        }

        var clone = (JsonObject)payload.DeepClone();
        foreach (var path in sensitivePaths)
        {
            SetPathValue(clone, path, JsonValue.Create("***REDACTED***"));
        }

        return clone;
    }

    private static void SetPathValue(JsonObject root, string path, JsonNode? value)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var cleaned = path.Trim().TrimStart('$').TrimStart('.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        JsonObject current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var isLeaf = index == parts.Length - 1;
            var key = parts[index];
            if (isLeaf)
            {
                current[key] = value?.DeepClone();
                return;
            }

            current[key] ??= new JsonObject();
            if (current[key] is JsonObject child)
            {
                current = child;
            }
            else
            {
                return;
            }
        }
    }
}

public sealed class TransportBroker(
    IEnumerable<IVideoTransportAdapter> transportAdapters,
    IApplicationStore store,
    IServiceProvider? serviceProvider,
    ILogger<TransportBroker> logger)
{
    // Reentrancy guard: TransportFailoverService.ResolveBestSourceAsync calls back into
    // GetSourcesAsync. Without this, a device with zero adapter sources would recurse
    // (broker -> failover -> broker -> failover -> ...) until the stack overflows.
    private readonly AsyncLocal<bool> _inFailoverFallback = new();

    public async Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return [];
        }

        var sources = new List<VideoSourceDescriptor>();
        foreach (var adapter in transportAdapters.OrderBy(static adapter => adapter.Priority))
        {
            try
            {
                sources.AddRange(await adapter.GetSourcesAsync(device, cancellationToken));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Transport adapter {Adapter} failed for {Device}", adapter.Name, device.DisplayName);
            }
        }

        var deduped = sources
            .OrderBy(static source => source.Rank)
            .GroupBy(static source => $"{source.Kind}:{source.Url}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();

        // When primary adapters yield nothing, try the aggressive failover fallback.
        // TransportFailoverService is resolved lazily from the container (not constructor
        // injected) to break the TransportBroker <-> TransportFailoverService singleton
        // cycle that made DI ValidateOnBuild (and host startup) fail.
        if (deduped.Count == 0 && serviceProvider is not null && device.IpAddress is not null && !_inFailoverFallback.Value)
        {
            var failoverService = serviceProvider.GetService<TransportFailoverService>();
            if (failoverService is not null)
            {
                logger.LogInformation("Primary transport adapters found no sources for {Device}; using TransportFailoverService", device.DisplayName);
                _inFailoverFallback.Value = true;
                try
                {
                    var fallback = await failoverService.ResolveBestSourceAsync(deviceId, "main", cancellationToken);
                    if (fallback is not null)
                    {
                        deduped.Add(fallback);
                    }
                }
                finally
                {
                    _inFailoverFallback.Value = false;
                }
            }
        }

        return deduped;
    }

    public async Task<PreviewSession?> StartPreviewAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var source = (await GetSourcesAsync(deviceId, cancellationToken)).FirstOrDefault();
        return source is null ? null : new PreviewSession { DeviceId = deviceId, Source = source };
    }
}

public sealed class FirmwareCatalogService(IFirmwareArtifactAnalyzer analyzer, IApplicationStore store)
{
    public async Task<FirmwareArtifact> RegisterAsync(string filePath, CancellationToken cancellationToken)
    {
        var artifact = await analyzer.AnalyzeAsync(filePath, cancellationToken);
        await store.AddFirmwareArtifactAsync(artifact, cancellationToken);
        return artifact;
    }

    public Task<IReadOnlyCollection<FirmwareArtifact>> GetAsync(CancellationToken cancellationToken)
        => store.GetFirmwareArtifactsAsync(cancellationToken);
}

