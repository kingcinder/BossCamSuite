using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Models;
using BossCam.Desktop.Avalonia.Services;

namespace BossCam.Desktop.Avalonia.Tests;

/// <summary>
/// Test double for <see cref="IBossCamApiClient"/>. Each call returns the
/// configured result (or a benign default when unset), and key methods count
/// calls so ViewModel tests can assert the right API was hit.
/// </summary>
public sealed class TestBossCamApiClient : IBossCamApiClient
{
    public string? LanToken { get; set; }

    // ── Devices ───────────────────────────────────────────────────
    public List<DeviceIdentity>? DevicesResult { get; set; }
    public int GetDevicesCallCount { get; private set; }

    // ── Live / snapshot ───────────────────────────────────────────
    public JsonElement? LiveInfoResult { get; set; }
    public byte[]? SnapshotResult { get; set; }
    public bool SaveSnapshotResult { get; set; } = true;
    public bool ThrowOnLiveInfo { get; set; }
    public int GetLiveInfoCallCount { get; private set; }
    public int GetSnapshotCallCount { get; private set; }
    public int SaveSnapshotCallCount { get; private set; }

    // ── Features / typed apply ────────────────────────────────────
    public ControlPointInventoryReport? ControlPointsResult { get; set; }
    public List<TypedSettingGroupSnapshot>? TypedSettingsResult { get; set; }
    public WriteResult ApplyResult { get; set; } = new() { Success = true };
    public int ApplyCallCount { get; private set; }
    public string? LastAppliedFieldKey { get; private set; }
    public JsonNode? LastAppliedValue { get; private set; }
    public bool LastExpertOverride { get; private set; }
    public CapabilityMap? ProbeResult { get; set; }
    public int ProbeCallCount { get; private set; }
    public int NormalizeCallCount { get; private set; }

    // ── Recordings ────────────────────────────────────────────────
    public List<RecordingJob>? JobsResult { get; set; }
    public List<RecordingProfile>? ProfilesResult { get; set; }
    public List<RecordingSegment>? SegmentsResult { get; set; }
    public ClipExportResult? ExportResult { get; set; }
    public RecordingHousekeepingResult? HousekeepingResult { get; set; }
    public StallCheckResult? StallCheckResult { get; set; }
    public int StartRecordingCallCount { get; private set; }
    public int StopAllCallCount { get; private set; }
    public int ExportCallCount { get; private set; }
    public Guid? LastExportDeviceId { get; private set; }

    // ── Highlights / playback / diagnostics / firmware / storage ──
    public HighlightBoardSnapshot? HighlightsResult { get; set; }
    public NvrPlaybackCallResult? PlaybackResult { get; set; }
    public List<WriteAuditEntry>? AuditResult { get; set; }
    public List<EndpointTranscript>? TranscriptsResult { get; set; }
    public List<ProbeSession>? SessionsResult { get; set; }
    public ProbeSession? StartSessionResult { get; set; }
    public List<FirmwareArtifact>? FirmwareArtifactsResult { get; set; }
    public List<FirmwareCapabilityProfile>? FirmwareCapabilitiesResult { get; set; }
    public List<EndpointContract>? ContractsResult { get; set; }
    public List<ProtocolManifest>? ProtocolsResult { get; set; }
    public List<DeviceConnectivitySnapshot>? ConnectivityResult { get; set; }
    public JsonElement? ConnectivityActionResult { get; set; }
    public MediaStoragePaths? StoragePathsResult { get; set; }
    public MediaStoragePaths? SavedStoragePathsResult { get; set; }
    public List<DeviceIdentity>? DiscoverResult { get; set; }
    public DeviceIdentity? RegisterResult { get; set; }
    public List<DeviceIdentity>? RegisterAegonResult { get; set; }
    public JsonElement? HealthResult { get; set; }
    public bool ThrowOnHealth { get; set; }

    public Task<List<DeviceIdentity>> GetDevicesAsync()
    {
        GetDevicesCallCount++;
        return DevicesResult is null
            ? throw new HttpRequestException("Simulated API failure (DevicesResult was null)")
            : Task.FromResult(DevicesResult);
    }

    public Task<JsonElement?> GetLiveInfoAsync(Guid deviceId)
    {
        GetLiveInfoCallCount++;
        if (ThrowOnLiveInfo)
        {
            return Task.FromException<JsonElement?>(new HttpRequestException("Simulated live-info failure"));
        }
        return Task.FromResult(LiveInfoResult);
    }

    public Task<LiveMediaManifest?> GetLiveManifestAsync(Guid deviceId, string quality = "sub")
        => Task.FromResult<LiveMediaManifest?>(new LiveMediaManifest
        {
            DeviceId = deviceId,
            PreferredMode = LiveMediaModeContract.H264MpegTs,
            FallbackModes = [LiveMediaModeContract.H264MpegTs, LiveMediaModeContract.Mjpeg, LiveMediaModeContract.Snapshot],
            MpegTsUrl = GetLiveTsUrl(deviceId, quality),
            MjpegUrl = GetLiveMjpegUrl(deviceId, quality)
        });

    public Task<byte[]?> GetSnapshotAsync(Guid deviceId)
    {
        GetSnapshotCallCount++;
        return Task.FromResult(SnapshotResult);
    }

    public Task<bool> SaveSnapshotAsync(Guid deviceId)
    {
        SaveSnapshotCallCount++;
        return Task.FromResult(SaveSnapshotResult);
    }

    public Task<ControlPointInventoryReport?> GetControlPointsAsync(Guid deviceId)
        => Task.FromResult(ControlPointsResult);

    public Task<List<TypedSettingGroupSnapshot>> NormalizeTypedSettingsAsync(Guid deviceId)
    {
        NormalizeCallCount++;
        return Task.FromResult(TypedSettingsResult ?? []);
    }

    public Task<List<TypedSettingGroupSnapshot>> GetTypedSettingsAsync(Guid deviceId)
        => Task.FromResult(TypedSettingsResult ?? []);

    public Task<WriteResult> ApplyTypedFieldAsync(Guid deviceId, string fieldKey, JsonNode? value, bool expertOverride = false)
    {
        ApplyCallCount++;
        LastAppliedFieldKey = fieldKey;
        LastAppliedValue = value;
        LastExpertOverride = expertOverride;
        return Task.FromResult(ApplyResult);
    }

    public Task<CapabilityMap?> ProbeAsync(Guid deviceId)
    {
        ProbeCallCount++;
        return Task.FromResult(ProbeResult);
    }

    public Task<RecordingJob> StartRecordingAsync(Guid deviceId, string? outputDirectory = null, string? sourceUrl = null)
    {
        StartRecordingCallCount++;
        return Task.FromResult(new RecordingJob { DeviceId = deviceId, IsRunning = true, SourceRole = "main" });
    }

    public Task<List<RecordingJob>> GetRecordingJobsAsync() => Task.FromResult(JobsResult ?? []);
    public Task<List<RecordingProfile>> GetRecordingProfilesAsync(Guid? deviceId = null) => Task.FromResult(ProfilesResult ?? []);
    public Task<List<RecordingSegment>> GetRecordingIndexAsync(int limit = 40) => Task.FromResult(SegmentsResult ?? []);
    public Task<List<RecordingSegment>> RefreshRecordingIndexAsync(Guid? deviceId = null) => Task.FromResult(SegmentsResult ?? []);

    public Task<List<RecordingJob>> StartAllRecordingsAsync(bool? preferSubStream = null)
        => Task.FromResult(JobsResult ?? []);

    public Task<List<RecordingJob>> StopAllRecordingsAsync()
    {
        StopAllCallCount++;
        return Task.FromResult(JobsResult ?? []);
    }

    public Task<RecordingJob> StopRecordingAsync(Guid jobId)
        => Task.FromResult(new RecordingJob { Id = jobId, IsRunning = false });

    public Task<ClipExportResult> ExportClipAsync(ClipExportRequest request)
    {
        ExportCallCount++;
        LastExportDeviceId = request.DeviceId;
        return Task.FromResult(ExportResult ?? new ClipExportResult { Success = true, OutputPath = "/tmp/clip.mp4", Bytes = 1024 });
    }

    public Task<RecordingHousekeepingResult> RunHousekeepingAsync(Guid? deviceId = null)
        => Task.FromResult(HousekeepingResult ?? new RecordingHousekeepingResult { FilesDeleted = 2, BytesDeleted = 4096 });

    public Task<List<RecordingJob>> ReconcileRecordingsAsync() => Task.FromResult(JobsResult ?? []);
    public Task<StallCheckResult> CheckStalledRecordingsAsync() => Task.FromResult(StallCheckResult ?? new StallCheckResult { Checked = true, Stalled = 1 });

    public Task<HighlightBoardSnapshot> GetHighlightsAsync()
        => Task.FromResult(HighlightsResult ?? new HighlightBoardSnapshot());

    public Task<HighlightBoardSnapshot> SelectHighlightAsync(Guid deviceId)
        => Task.FromResult(HighlightsResult ?? new HighlightBoardSnapshot());

    public Task<HighlightBoardSnapshot> HighlightNextAsync() => Task.FromResult(HighlightsResult ?? new HighlightBoardSnapshot());
    public Task<HighlightBoardSnapshot> HighlightPrevAsync() => Task.FromResult(HighlightsResult ?? new HighlightBoardSnapshot());
    public Task<HighlightBoardSnapshot> HighlightStreamAsync(string mode) => Task.FromResult(HighlightsResult ?? new HighlightBoardSnapshot());
    public Task<JsonElement?> RecordSelectedHighlightAsync() => Task.FromResult<JsonElement?>(null);

    public Task<NvrPlaybackCallResult> PlaybackFindFileAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end, string? cursor = null)
        => Task.FromResult(PlaybackResult ?? new NvrPlaybackCallResult { Success = true, Operation = "find-file" });

    public Task<NvrPlaybackCallResult> PlaybackFindNextFileAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end, string? cursor = null)
        => Task.FromResult(PlaybackResult ?? new NvrPlaybackCallResult { Success = true, Operation = "find-next-file" });

    public Task<NvrPlaybackCallResult> PlaybackGetFileByTimeAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end)
        => Task.FromResult(PlaybackResult ?? new NvrPlaybackCallResult { Success = true, Operation = "get-file-by-time" });

    public Task<NvrPlaybackCallResult> PlaybackByTimeAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end)
        => Task.FromResult(PlaybackResult ?? new NvrPlaybackCallResult { Success = true, Operation = "playback-by-time" });

    public Task<NvrPlaybackCallResult> PlaybackFindCloseAsync(Guid deviceId)
        => Task.FromResult(PlaybackResult ?? new NvrPlaybackCallResult { Success = true, Operation = "find-close" });

    public Task<List<WriteAuditEntry>> GetAuditEntriesAsync(Guid? deviceId = null, int limit = 50) => Task.FromResult(AuditResult ?? []);
    public Task<List<EndpointTranscript>> GetTranscriptsAsync(Guid? deviceId = null, int limit = 50) => Task.FromResult(TranscriptsResult ?? []);
    public Task<List<ProbeSession>> GetProbeSessionsAsync(Guid? deviceId = null, int limit = 50) => Task.FromResult(SessionsResult ?? []);

    public Task<ProbeSession> StartProbeSessionAsync(ProbeSessionRequest request)
        => Task.FromResult(StartSessionResult ?? new ProbeSession { Id = Guid.NewGuid(), Status = ProbeSessionStatus.Pending });

    public Task<List<ProbeStageResult>> GetProbeSessionStagesAsync(Guid sessionId) => Task.FromResult<List<ProbeStageResult>>([]);

    public Task<List<FirmwareArtifact>> GetFirmwareArtifactsAsync() => Task.FromResult(FirmwareArtifactsResult ?? []);
    public Task<FirmwareArtifact> RegisterFirmwareAsync(string filePath) => Task.FromResult(new FirmwareArtifact { FileName = "firmware.bin" });
    public Task<List<FirmwareCapabilityProfile>> GetFirmwareCapabilitiesAsync() => Task.FromResult(FirmwareCapabilitiesResult ?? []);
    public Task<List<EndpointContract>> GetContractEndpointsAsync(Guid? deviceId = null) => Task.FromResult(ContractsResult ?? []);
    public Task<List<EndpointContractFixture>> GetContractFixturesAsync(Guid? deviceId = null) => Task.FromResult<List<EndpointContractFixture>>([]);
    public Task<List<ProtocolManifest>> GetProtocolsAsync() => Task.FromResult(ProtocolsResult ?? []);
    public Task<List<ProtocolManifest>> RefreshProtocolsAsync() => Task.FromResult(ProtocolsResult ?? []);

    public Task<List<DeviceConnectivitySnapshot>> GetConnectivityAllAsync() => Task.FromResult(ConnectivityResult ?? []);
    public Task<DeviceConnectivitySnapshot?> GetConnectivityAsync(Guid deviceId) => Task.FromResult(ConnectivityResult?.FirstOrDefault());
    public Task<JsonElement?> DiagnoseConnectivityAsync(Guid deviceId) => Task.FromResult(ConnectivityActionResult);
    public Task<JsonElement?> ReconnectDeviceAsync(Guid deviceId) => Task.FromResult(ConnectivityActionResult);

    public Task<MediaStoragePaths> GetStoragePathsAsync() => Task.FromResult(StoragePathsResult ?? new MediaStoragePaths());
    public Task<MediaStoragePaths> SaveStoragePathsAsync(MediaStoragePaths paths)
    {
        SavedStoragePathsResult = paths;
        return Task.FromResult(paths);
    }

    public Task<List<DeviceIdentity>> DiscoverAsync() => Task.FromResult(DiscoverResult ?? []);
    public Task<DeviceIdentity?> RegisterAsync(string ipAddress, int port, string? loginName, string? password, string? name, string? hardwareModel)
        => Task.FromResult(RegisterResult);
    public Task<List<DeviceIdentity>> RegisterAegonLanAsync(string? lorexPassword, string? wvcPassword) => Task.FromResult(RegisterAegonResult ?? []);
    public Task<List<DeviceIdentity>> RegisterManyAsync(IEnumerable<(string IpAddress, int Port, string? LoginName, string? Password, string? HardwareModel)> requests)
        => Task.FromResult(DiscoverResult ?? []);

    public Task<JsonElement?> GetHealthAsync()
    {
        if (ThrowOnHealth)
        {
            return Task.FromException<JsonElement?>(new HttpRequestException("Simulated health failure"));
        }
        return Task.FromResult(HealthResult);
    }

    // ── Unused by the GUI tests (kept for interface completeness) ──

    public Task<CapabilityProbeResult?> ValidateAsync(Guid deviceId, object? options = null) => Task.FromResult<CapabilityProbeResult?>(null);
    public Task<List<EndpointValidationResult>> GetValidationAsync(Guid deviceId) => Task.FromResult<List<EndpointValidationResult>>([]);
    public Task<List<EndpointTranscript>> GetValidationTranscriptsAsync(Guid deviceId, int limit = 50) => Task.FromResult<List<EndpointTranscript>>([]);
    public Task<CapabilityMap?> GetCapabilitiesAsync(Guid deviceId) => Task.FromResult<CapabilityMap?>(null);
    public Task<SettingsSnapshot?> ReadSettingsAsync(Guid deviceId) => Task.FromResult<SettingsSnapshot?>(null);
    public Task<SettingsSnapshot?> GetLastSettingsAsync(Guid deviceId) => Task.FromResult<SettingsSnapshot?>(null);
    public Task<WriteResult> WriteSettingsAsync(Guid deviceId, WritePlan plan) => Task.FromResult(new WriteResult());
    public Task<MaintenanceResult> ExecuteMaintenanceAsync(Guid deviceId, string operation, JsonObject? payload) => Task.FromResult(new MaintenanceResult { Success = true });
    public Task<List<WriteResult>> ApplyTypedBatchAsync(Guid deviceId, IEnumerable<TypedFieldChange> changes, bool expertOverride = false) => Task.FromResult<List<WriteResult>>([new() { Success = true }]);
    public Task<List<PersistenceEligibleField>> GetPersistenceEligibleFieldsAsync(Guid deviceId) => Task.FromResult<List<PersistenceEligibleField>>([]);
    public Task<List<PersistenceVerificationResult>> GetPersistenceResultsAsync(Guid deviceId, int limit = 20) => Task.FromResult<List<PersistenceVerificationResult>>([]);
    public Task<PersistenceVerificationResult> VerifyPersistenceAsync(Guid deviceId, object request) => Task.FromResult(new PersistenceVerificationResult());
    public Task<List<ImageControlInventoryItem>> GetImageInventoryAsync(Guid deviceId) => Task.FromResult<List<ImageControlInventoryItem>>([]);
    public Task<ImageWritableTestSetProfile?> GetImageWritableTestSetAsync(Guid deviceId) => Task.FromResult<ImageWritableTestSetProfile?>(null);
    public Task<List<ImageFieldBehaviorMap>> GetImageBehaviorMapsAsync(Guid deviceId) => Task.FromResult<List<ImageFieldBehaviorMap>>([]);
    public Task<JsonElement?> RunImageTruthSweepAsync(Guid deviceId, object? request = null) => Task.FromResult<JsonElement?>(null);
    public Task<JsonElement?> GetGroupedConfigSnapshotsAsync(Guid deviceId, bool? refreshFromDevice = null) => Task.FromResult<JsonElement?>(null);
    public Task<JsonElement?> GetGroupedConfigProfilesAsync(Guid deviceId, string? firmwareFingerprint = null) => Task.FromResult<JsonElement?>(null);
    public Task<JsonElement?> GetGroupedRetestResultsAsync(Guid deviceId, int limit = 50) => Task.FromResult<JsonElement?>(null);
    public Task<JsonElement?> GetSdkFieldCatalogAsync() => Task.FromResult<JsonElement?>(null);
    public Task<List<SemanticWriteObservation>> GetSemanticHistoryAsync(Guid deviceId, int limit = 50) => Task.FromResult<List<SemanticWriteObservation>>([]);
    public Task<List<FieldConstraintProfile>> GetConstraintsAsync(Guid deviceId) => Task.FromResult<List<FieldConstraintProfile>>([]);
    public Task<List<DependencyMatrixProfile>> GetDependenciesAsync(Guid deviceId) => Task.FromResult<List<DependencyMatrixProfile>>([]);
    public Task<JsonElement?> RunNetworkRecoveryAsync(Guid deviceId, object context) => Task.FromResult<JsonElement?>(null);
    public Task<NativeFallbackAssessment?> GetNativeFallbackAssessmentAsync(Guid deviceId) => Task.FromResult<NativeFallbackAssessment?>(null);
    public Task<List<VideoSourceDescriptor>> GetSourcesAsync(Guid deviceId) => Task.FromResult<List<VideoSourceDescriptor>>([]);
    public Task<PreviewSession?> GetPreviewAsync(Guid deviceId) => Task.FromResult<PreviewSession?>(null);
    public string GetLiveMjpegUrl(Guid deviceId, string quality = "sub") => $"http://127.0.0.1:5317/api/devices/{deviceId}/live.mjpeg?quality={quality}";
    public string GetLiveTsUrl(Guid deviceId, string quality = "sub") => $"http://127.0.0.1:5317/api/devices/{deviceId}/live.ts?quality={quality}";
    public Task SaveRecordingProfilesAsync(IEnumerable<RecordingProfile> profiles) => Task.CompletedTask;
    public string GetRecordingDownloadUrl(string path) => $"http://127.0.0.1:5317/api/recordings/download?path={Uri.EscapeDataString(path)}";
    public Task<JsonElement?> PromoteContractFixturesAsync(Guid deviceId, string exportRoot) => Task.FromResult<JsonElement?>(null);
    public Task<JsonElement?> CleanupContractFixturesAsync(int olderThanDays = 90, int maxPerDevice = 2000, int maxTotal = 10000) => Task.FromResult<JsonElement?>(null);
    public Task<TruthSweepReport?> GetTruthSweepAsync(string? ips = null) => Task.FromResult<TruthSweepReport?>(null);

    public void Dispose() { }
}
