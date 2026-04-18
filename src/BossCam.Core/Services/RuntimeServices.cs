using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class CapabilityProbeService(
    IEnumerable<IControlAdapter> controlAdapters,
    IApplicationStore store,
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
        foreach (var adapter in controlAdapters.OrderBy(static adapter => adapter.Priority))
        {
            bool canHandle;
            try
            {
                canHandle = await adapter.CanHandleAsync(device, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Adapter {Adapter} capability check failed for {Device}", adapter.Name, device.DisplayName);
                continue;
            }

            if (!canHandle)
            {
                continue;
            }

            try
            {
                var map = await adapter.ProbeAsync(device, cancellationToken);
                combined = Merge(combined, map);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Adapter {Adapter} probe failed for {Device}", adapter.Name, device.DisplayName);
            }
        }

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
    ILogger<SettingsService> logger)
{
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
        await store.SaveSettingsSnapshotAsync(snapshot, cancellationToken);
        return snapshot;
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
            return new WriteResult { Success = false, AdapterName = plan.AdapterName ?? string.Empty, Message = "No control adapter matched the device." };
        }

        SettingsSnapshot? beforeSnapshot = null;
        if (plan.SnapshotBeforeWrite)
        {
            beforeSnapshot = await adapter.SnapshotAsync(device, cancellationToken);
            await store.SaveSettingsSnapshotAsync(beforeSnapshot, cancellationToken);
        }

        var result = await adapter.ApplyAsync(device, plan, cancellationToken);
        var finalResult = result with { SnapshotBeforeWrite = beforeSnapshot ?? result.SnapshotBeforeWrite };

        await store.AddAuditEntryAsync(new WriteAuditEntry
        {
            DeviceId = device.Id,
            AdapterName = adapter.Name,
            Operation = plan.Method,
            Endpoint = plan.Endpoint,
            RequestContent = plan.Payload?.ToJsonString(),
            ResponseContent = finalResult.Response?.ToJsonString(),
            Success = finalResult.Success
        }, cancellationToken);

        return finalResult;
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
            RequestContent = payload?.ToJsonString(),
            ResponseContent = result.Response?.ToJsonString(),
            Success = result.Success
        }, cancellationToken);

        return result;
    }

    private async Task<IControlAdapter?> ResolveAdapterAsync(DeviceIdentity device, string? requestedAdapterName, CancellationToken cancellationToken)
    {
        foreach (var adapter in controlAdapters.Where(adapter => string.IsNullOrWhiteSpace(requestedAdapterName) || adapter.Name.Equals(requestedAdapterName, StringComparison.OrdinalIgnoreCase)).OrderBy(static adapter => adapter.Priority))
        {
            try
            {
                if (await adapter.CanHandleAsync(device, cancellationToken))
                {
                    return adapter;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Adapter {Adapter} resolution failed for {Device}", adapter.Name, device.DisplayName);
            }
        }

        return null;
    }
}

public sealed class TransportBroker(
    IEnumerable<IVideoTransportAdapter> transportAdapters,
    IApplicationStore store,
    ILogger<TransportBroker> logger)
{
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

        return sources
            .OrderBy(static source => source.Rank)
            .GroupBy(static source => $"{source.Kind}:{source.Url}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
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
