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

public interface IEndpointContractCatalog
{
    Task<IReadOnlyCollection<EndpointContract>> GetContractsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EndpointContract>> GetContractsForDeviceAsync(DeviceIdentity device, CancellationToken cancellationToken);
    EndpointContract? MatchContract(string endpoint, string method, IEnumerable<EndpointContract> contracts);
}

public interface IContractEvidenceService
{
    Task<IReadOnlyCollection<EndpointContractFixture>> PromoteFromTranscriptsAsync(Guid deviceId, string exportRoot, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EndpointContractFixture>> GetFixturesAsync(Guid? deviceId, CancellationToken cancellationToken);
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
    Task SaveEndpointValidationResultsAsync(IEnumerable<EndpointValidationResult> results, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EndpointValidationResult>> GetEndpointValidationResultsAsync(Guid deviceId, CancellationToken cancellationToken);
    Task SaveEndpointTranscriptsAsync(IEnumerable<EndpointTranscript> transcripts, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EndpointTranscript>> GetEndpointTranscriptsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken);
    Task SaveProbeSessionAsync(ProbeSession session, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProbeSession>> GetProbeSessionsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken);
    Task SaveProbeStageResultsAsync(IEnumerable<ProbeStageResult> stages, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProbeStageResult>> GetProbeStageResultsAsync(Guid sessionId, CancellationToken cancellationToken);
    Task SaveNormalizedSettingFieldsAsync(IEnumerable<NormalizedSettingField> fields, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<NormalizedSettingField>> GetNormalizedSettingFieldsAsync(Guid deviceId, CancellationToken cancellationToken);
    Task SaveFirmwareCapabilityProfileAsync(FirmwareCapabilityProfile profile, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FirmwareCapabilityProfile>> GetFirmwareCapabilityProfilesAsync(CancellationToken cancellationToken);
    Task SavePersistenceVerificationResultAsync(PersistenceVerificationResult result, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<PersistenceVerificationResult>> GetPersistenceVerificationResultsAsync(Guid deviceId, int limit, CancellationToken cancellationToken);
    Task SaveEndpointContractsAsync(IEnumerable<EndpointContract> contracts, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EndpointContract>> GetEndpointContractsAsync(CancellationToken cancellationToken);
    Task SaveContractFixturesAsync(IEnumerable<EndpointContractFixture> fixtures, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EndpointContractFixture>> GetContractFixturesAsync(Guid? deviceId, int limit, CancellationToken cancellationToken);
    Task AddFirmwareArtifactAsync(FirmwareArtifact artifact, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FirmwareArtifact>> GetFirmwareArtifactsAsync(CancellationToken cancellationToken);
    Task SaveRecordingProfilesAsync(IEnumerable<RecordingProfile> profiles, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RecordingProfile>> GetRecordingProfilesAsync(Guid? deviceId, CancellationToken cancellationToken);
    Task SaveRecordingSegmentsAsync(IEnumerable<RecordingSegment> segments, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RecordingSegment>> GetRecordingSegmentsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken);
    Task SaveSemanticWriteObservationsAsync(IEnumerable<SemanticWriteObservation> observations, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<SemanticWriteObservation>> GetSemanticWriteObservationsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken);
    Task SaveFieldConstraintProfilesAsync(IEnumerable<FieldConstraintProfile> profiles, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FieldConstraintProfile>> GetFieldConstraintProfilesAsync(string? firmwareFingerprint, CancellationToken cancellationToken);
    Task SaveDependencyMatrixProfilesAsync(IEnumerable<DependencyMatrixProfile> profiles, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<DependencyMatrixProfile>> GetDependencyMatrixProfilesAsync(string? firmwareFingerprint, CancellationToken cancellationToken);
    Task SaveImageControlInventoryAsync(IEnumerable<ImageControlInventoryItem> items, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ImageControlInventoryItem>> GetImageControlInventoryAsync(Guid deviceId, CancellationToken cancellationToken);
    Task SaveImageBehaviorMapsAsync(IEnumerable<ImageFieldBehaviorMap> maps, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ImageFieldBehaviorMap>> GetImageBehaviorMapsAsync(Guid deviceId, CancellationToken cancellationToken);
    Task SaveImageWritableTestSetAsync(ImageWritableTestSetProfile profile, CancellationToken cancellationToken);
    Task<ImageWritableTestSetProfile?> GetImageWritableTestSetAsync(Guid deviceId, CancellationToken cancellationToken);
    Task SaveGroupedApplyProfilesAsync(IEnumerable<GroupedApplyProfile> profiles, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<GroupedApplyProfile>> GetGroupedApplyProfilesAsync(Guid? deviceId, string? firmwareFingerprint, CancellationToken cancellationToken);
    Task SaveGroupedRetestResultsAsync(IEnumerable<GroupedUnsupportedRetestResult> results, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<GroupedUnsupportedRetestResult>> GetGroupedRetestResultsAsync(Guid deviceId, int limit, CancellationToken cancellationToken);
}
