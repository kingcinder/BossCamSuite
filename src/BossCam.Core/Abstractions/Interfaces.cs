using BossCam.Contracts;
using System.Text.Json.Nodes;

namespace BossCam.Core;

public interface IDiscoveryProvider
{
    string Name { get; }
    Task<IReadOnlyCollection<DeviceIdentity>> DiscoverAsync(CancellationToken cancellationToken);
}

public interface IDeviceImportProvider
{
    string Name { get; }
    Task<IReadOnlyCollection<DeviceIdentity>> ImportAsync(CancellationToken cancellationToken);
}

public interface IProtocolManifestProvider
{
    Task<IReadOnlyCollection<ProtocolManifest>> LoadAsync(CancellationToken cancellationToken);
}

public interface IFirmwareArtifactAnalyzer
{
    Task<FirmwareArtifact> AnalyzeAsync(string filePath, CancellationToken cancellationToken);
}

public interface IControlAdapter
{
    string Name { get; }
    int Priority { get; }
    TransportKind TransportKind { get; }
    Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken);
    Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken);
    Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken);
    Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken);
    Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken);
    Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken);
}

public interface IVideoTransportAdapter
{
    string Name { get; }
    TransportKind TransportKind { get; }
    int Priority { get; }
    Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken);
}

public interface IApplicationStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task UpsertDevicesAsync(IEnumerable<DeviceIdentity> devices, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<DeviceIdentity>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<DeviceIdentity?> GetDeviceAsync(Guid id, CancellationToken cancellationToken);
    Task SaveCapabilityMapAsync(CapabilityMap capabilityMap, CancellationToken cancellationToken);
    Task<CapabilityMap?> GetCapabilityMapAsync(Guid deviceId, CancellationToken cancellationToken);
    Task SaveSettingsSnapshotAsync(SettingsSnapshot snapshot, CancellationToken cancellationToken);
    Task<SettingsSnapshot?> GetSettingsSnapshotAsync(Guid deviceId, CancellationToken cancellationToken);
    Task AddAuditEntryAsync(WriteAuditEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<WriteAuditEntry>> GetAuditEntriesAsync(Guid? deviceId, int limit, CancellationToken cancellationToken);
    Task SaveProtocolManifestsAsync(IEnumerable<ProtocolManifest> manifests, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProtocolManifest>> GetProtocolManifestsAsync(CancellationToken cancellationToken);
    Task AddFirmwareArtifactAsync(FirmwareArtifact artifact, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FirmwareArtifact>> GetFirmwareArtifactsAsync(CancellationToken cancellationToken);
    Task SaveRecordingProfilesAsync(IEnumerable<RecordingProfile> profiles, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RecordingProfile>> GetRecordingProfilesAsync(Guid? deviceId, CancellationToken cancellationToken);
}
