using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BossCam.Desktop.Avalonia.Services;

/// <summary>
/// Abstraction over the BossCamService HTTP API. Enables unit testing
/// of the ViewModels without a live server.
///
/// This is the single client surface the entire desktop GUI uses — it mirrors the
/// routes exposed by BossCam.Service so every feature has a corresponding call.
/// </summary>
public interface IBossCamApiClient : IDisposable
{
    /// <summary>Optional LAN gate token used by both HTTP requests and media consumers.</summary>
    string? LanToken { get; set; }

    // ── Health / connectivity ────────────────────────────────────────
    /// <summary>GET /api/health</summary>
    Task<JsonElement?> GetHealthAsync();

    /// <summary>GET /api/devices/connectivity — snapshots for all devices.</summary>
    Task<List<DeviceConnectivitySnapshot>> GetConnectivityAllAsync();

    /// <summary>GET /api/devices/{id}/connectivity</summary>
    Task<DeviceConnectivitySnapshot?> GetConnectivityAsync(Guid deviceId);

    /// <summary>POST /api/devices/{id}/connectivity/diagnose</summary>
    Task<JsonElement?> DiagnoseConnectivityAsync(Guid deviceId);

    /// <summary>POST /api/devices/{id}/connectivity/reconnect</summary>
    Task<JsonElement?> ReconnectDeviceAsync(Guid deviceId);

    // ── Devices / discovery / registration ───────────────────────────
    /// <summary>GET /api/devices → all registered devices.</summary>
    Task<List<DeviceIdentity>> GetDevicesAsync();

    /// <summary>POST /api/devices/discover</summary>
    Task<List<DeviceIdentity>> DiscoverAsync();

    /// <summary>POST /api/devices/register</summary>
    Task<DeviceIdentity?> RegisterAsync(string ipAddress, int port, string? loginName, string? password, string? name, string? hardwareModel);

    /// <summary>POST /api/devices/register-many</summary>
    Task<List<DeviceIdentity>> RegisterManyAsync(IEnumerable<(string IpAddress, int Port, string? LoginName, string? Password, string? HardwareModel)> requests);

    /// <summary>POST /api/devices/register-aegon-lan</summary>
    Task<List<DeviceIdentity>> RegisterAegonLanAsync(string? lorexPassword, string? wvcPassword);

    // ── Probe / validation / capabilities ────────────────────────────
    /// <summary>POST /api/devices/{id}/probe</summary>
    Task<CapabilityMap?> ProbeAsync(Guid deviceId);

    /// <summary>POST /api/devices/{id}/validation/run</summary>
    Task<CapabilityProbeResult?> ValidateAsync(Guid deviceId, object? options = null);

    /// <summary>GET /api/devices/{id}/validation — list of per-endpoint validation results.</summary>
    Task<List<EndpointValidationResult>> GetValidationAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/validation/transcripts</summary>
    Task<List<EndpointTranscript>> GetValidationTranscriptsAsync(Guid deviceId, int limit = 50);

    /// <summary>GET /api/devices/{id}/capabilities</summary>
    Task<CapabilityMap?> GetCapabilitiesAsync(Guid deviceId);

    // ── Settings (raw) + maintenance ──────────────────────────────────
    /// <summary>GET /api/devices/{id}/settings</summary>
    Task<SettingsSnapshot?> ReadSettingsAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/settings/last</summary>
    Task<SettingsSnapshot?> GetLastSettingsAsync(Guid deviceId);

    /// <summary>POST /api/devices/{id}/settings/write</summary>
    Task<WriteResult> WriteSettingsAsync(Guid deviceId, WritePlan plan);

    /// <summary>POST /api/devices/{id}/maintenance/{operation}</summary>
    Task<MaintenanceResult> ExecuteMaintenanceAsync(Guid deviceId, string operation, JsonObject? payload);

    // ── Typed settings / control points / features ───────────────────
    /// <summary>GET /api/devices/{id}/settings/typed</summary>
    Task<List<TypedSettingGroupSnapshot>> GetTypedSettingsAsync(Guid deviceId);

    /// <summary>POST /api/devices/{id}/settings/typed/refresh (normalize)</summary>
    Task<List<TypedSettingGroupSnapshot>> NormalizeTypedSettingsAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/control-points</summary>
    Task<ControlPointInventoryReport?> GetControlPointsAsync(Guid deviceId);

    /// <summary>POST /api/devices/{id}/settings/typed/apply</summary>
    Task<WriteResult> ApplyTypedFieldAsync(Guid deviceId, string fieldKey, JsonNode? value, bool expertOverride = false);

    /// <summary>POST /api/devices/{id}/settings/typed/apply-batch</summary>
    Task<List<WriteResult>> ApplyTypedBatchAsync(Guid deviceId, IEnumerable<TypedFieldChange> changes, bool expertOverride = false);

    /// <summary>GET /api/devices/{id}/persistence/eligible-fields</summary>
    Task<List<PersistenceEligibleField>> GetPersistenceEligibleFieldsAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/persistence</summary>
    Task<List<PersistenceVerificationResult>> GetPersistenceResultsAsync(Guid deviceId, int limit = 20);

    /// <summary>POST /api/devices/{id}/persistence/verify</summary>
    Task<PersistenceVerificationResult> VerifyPersistenceAsync(Guid deviceId, object request);

    // ── Image / grouped-config / semantic insights ───────────────────
    /// <summary>GET /api/devices/{id}/image/inventory</summary>
    Task<List<ImageControlInventoryItem>> GetImageInventoryAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/image/writable-test-set</summary>
    Task<ImageWritableTestSetProfile?> GetImageWritableTestSetAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/image/behavior-maps</summary>
    Task<List<ImageFieldBehaviorMap>> GetImageBehaviorMapsAsync(Guid deviceId);

    /// <summary>POST /api/devices/{id}/image/truth-sweep</summary>
    Task<JsonElement?> RunImageTruthSweepAsync(Guid deviceId, object? request = null);

    /// <summary>GET /api/devices/{id}/grouped-config/snapshots</summary>
    Task<JsonElement?> GetGroupedConfigSnapshotsAsync(Guid deviceId, bool? refreshFromDevice = null);

    /// <summary>GET /api/devices/{id}/grouped-config/profiles</summary>
    Task<JsonElement?> GetGroupedConfigProfilesAsync(Guid deviceId, string? firmwareFingerprint = null);

    /// <summary>GET /api/devices/{id}/grouped-config/retest-results</summary>
    Task<JsonElement?> GetGroupedRetestResultsAsync(Guid deviceId, int limit = 50);

    /// <summary>GET /api/grouped-config/sdk-field-catalog</summary>
    Task<JsonElement?> GetSdkFieldCatalogAsync();

    /// <summary>GET /api/devices/{id}/semantic/history</summary>
    Task<List<SemanticWriteObservation>> GetSemanticHistoryAsync(Guid deviceId, int limit = 50);

    /// <summary>GET /api/devices/{id}/constraints</summary>
    Task<List<FieldConstraintProfile>> GetConstraintsAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/dependencies</summary>
    Task<List<DependencyMatrixProfile>> GetDependenciesAsync(Guid deviceId);

    /// <summary>POST /api/devices/{id}/network/recovery</summary>
    Task<JsonElement?> RunNetworkRecoveryAsync(Guid deviceId, object context);

    /// <summary>GET /api/devices/{id}/native-fallback-assessment</summary>
    Task<NativeFallbackAssessment?> GetNativeFallbackAssessmentAsync(Guid deviceId);

    // ── Streaming / snapshot ─────────────────────────────────────────
    /// <summary>GET /api/devices/{id}/sources</summary>
    Task<List<VideoSourceDescriptor>> GetSourcesAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/preview</summary>
    Task<PreviewSession?> GetPreviewAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/snapshot → raw JPEG bytes (or null on failure).</summary>
    Task<byte[]?> GetSnapshotAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/live-info → optional extended device info.</summary>
    Task<JsonElement?> GetLiveInfoAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/live-manifest → negotiated live media modes.</summary>
    Task<LiveMediaManifest?> GetLiveManifestAsync(Guid deviceId, string quality = "sub");

    /// <summary>Build a live MJPEG URL for the given device/quality.</summary>
    string GetLiveMjpegUrl(Guid deviceId, string quality = "sub");

    /// <summary>Build a live MPEG-TS URL for the given device/quality.</summary>
    string GetLiveTsUrl(Guid deviceId, string quality = "sub");

    /// <summary>POST /api/storage/save-snapshot/{id} → true if saved.</summary>
    Task<bool> SaveSnapshotAsync(Guid deviceId);

    // ── Recordings ───────────────────────────────────────────────────
    /// <summary>GET /api/recordings — recording profiles.</summary>
    Task<List<RecordingProfile>> GetRecordingProfilesAsync(Guid? deviceId = null);

    /// <summary>POST /api/recordings — save recording profiles.</summary>
    Task SaveRecordingProfilesAsync(IEnumerable<RecordingProfile> profiles);

    /// <summary>POST /api/recordings/start</summary>
    Task<RecordingJob> StartRecordingAsync(Guid deviceId, string? outputDirectory = null, string? sourceUrl = null);

    /// <summary>POST /api/recordings/start-all</summary>
    Task<List<RecordingJob>> StartAllRecordingsAsync(bool? preferSubStream = null);

    /// <summary>POST /api/recordings/stop/{jobId}</summary>
    Task<RecordingJob> StopRecordingAsync(Guid jobId);

    /// <summary>POST /api/recordings/stop-all</summary>
    Task<List<RecordingJob>> StopAllRecordingsAsync();

    /// <summary>GET /api/recordings/jobs</summary>
    Task<List<RecordingJob>> GetRecordingJobsAsync();

    /// <summary>POST /api/recordings/index/refresh</summary>
    Task<List<RecordingSegment>> RefreshRecordingIndexAsync(Guid? deviceId = null);

    /// <summary>GET /api/recordings/index</summary>
    Task<List<RecordingSegment>> GetRecordingIndexAsync(int limit = 40);

    /// <summary>POST /api/recordings/export — clip export (copy-first concat).</summary>
    Task<ClipExportResult> ExportClipAsync(ClipExportRequest request);

    /// <summary>POST /api/recordings/housekeeping</summary>
    Task<RecordingHousekeepingResult> RunHousekeepingAsync(Guid? deviceId = null);

    /// <summary>POST /api/recordings/reconcile</summary>
    Task<List<RecordingJob>> ReconcileRecordingsAsync();

    /// <summary>POST /api/recordings/stall-check — returns { checked, stalled, autoRestart }.</summary>
    Task<StallCheckResult> CheckStalledRecordingsAsync();

    /// <summary>Build the clip download URL for a server path.</summary>
    string GetRecordingDownloadUrl(string path);

    // ── Highlights board ─────────────────────────────────────────────
    /// <summary>GET /api/highlights</summary>
    Task<HighlightBoardSnapshot> GetHighlightsAsync();

    /// <summary>POST /api/highlights/select/{deviceId}</summary>
    Task<HighlightBoardSnapshot> SelectHighlightAsync(Guid deviceId);

    /// <summary>POST /api/highlights/next</summary>
    Task<HighlightBoardSnapshot> HighlightNextAsync();

    /// <summary>POST /api/highlights/prev</summary>
    Task<HighlightBoardSnapshot> HighlightPrevAsync();

    /// <summary>POST /api/highlights/stream/{mode}</summary>
    Task<HighlightBoardSnapshot> HighlightStreamAsync(string mode);

    /// <summary>POST /api/highlights/record-selected</summary>
    Task<JsonElement?> RecordSelectedHighlightAsync();

    // ── SD / NVR playback (transport-only) ───────────────────────────
    /// <summary>POST /api/devices/{id}/playback/find-file</summary>
    Task<NvrPlaybackCallResult> PlaybackFindFileAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end, string? cursor = null);

    /// <summary>POST /api/devices/{id}/playback/find-next-file</summary>
    Task<NvrPlaybackCallResult> PlaybackFindNextFileAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end, string? cursor = null);

    /// <summary>POST /api/devices/{id}/playback/get-file-by-time</summary>
    Task<NvrPlaybackCallResult> PlaybackGetFileByTimeAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end);

    /// <summary>POST /api/devices/{id}/playback/playback-by-time</summary>
    Task<NvrPlaybackCallResult> PlaybackByTimeAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end);

    /// <summary>POST /api/devices/{id}/playback/find-close</summary>
    Task<NvrPlaybackCallResult> PlaybackFindCloseAsync(Guid deviceId);

    // ── Diagnostics / audit / transcripts / probe sessions ───────────
    /// <summary>GET /api/diagnostics/audit</summary>
    Task<List<WriteAuditEntry>> GetAuditEntriesAsync(Guid? deviceId = null, int limit = 50);

    /// <summary>GET /api/diagnostics/transcripts</summary>
    Task<List<EndpointTranscript>> GetTranscriptsAsync(Guid? deviceId = null, int limit = 50);

    /// <summary>POST /api/probe/sessions/start</summary>
    Task<ProbeSession> StartProbeSessionAsync(ProbeSessionRequest request);

    /// <summary>GET /api/probe/sessions</summary>
    Task<List<ProbeSession>> GetProbeSessionsAsync(Guid? deviceId = null, int limit = 50);

    /// <summary>GET /api/probe/sessions/{id}/stages</summary>
    Task<List<ProbeStageResult>> GetProbeSessionStagesAsync(Guid sessionId);

    /// <summary>GET /api/truth/sweep</summary>
    Task<TruthSweepReport?> GetTruthSweepAsync(string? ips = null);

    // ── Firmware / contracts / protocols ─────────────────────────────
    /// <summary>GET /api/firmware</summary>
    Task<List<FirmwareArtifact>> GetFirmwareArtifactsAsync();

    /// <summary>POST /api/firmware/register</summary>
    Task<FirmwareArtifact> RegisterFirmwareAsync(string filePath);

    /// <summary>GET /api/firmware/capabilities</summary>
    Task<List<FirmwareCapabilityProfile>> GetFirmwareCapabilitiesAsync();

    /// <summary>GET /api/contracts/endpoints</summary>
    Task<List<EndpointContract>> GetContractEndpointsAsync(Guid? deviceId = null);

    /// <summary>GET /api/contracts/fixtures</summary>
    Task<List<EndpointContractFixture>> GetContractFixturesAsync(Guid? deviceId = null);

    /// <summary>POST /api/contracts/fixtures/promote/{deviceId}</summary>
    Task<JsonElement?> PromoteContractFixturesAsync(Guid deviceId, string exportRoot);

    /// <summary>POST /api/contracts/fixtures/cleanup</summary>
    Task<JsonElement?> CleanupContractFixturesAsync(int olderThanDays = 90, int maxPerDevice = 2000, int maxTotal = 10000);

    /// <summary>GET /api/protocols</summary>
    Task<List<ProtocolManifest>> GetProtocolsAsync();

    /// <summary>POST /api/protocols/refresh</summary>
    Task<List<ProtocolManifest>> RefreshProtocolsAsync();

    // ── Storage paths ────────────────────────────────────────────────
    /// <summary>GET /api/storage/paths</summary>
    Task<MediaStoragePaths> GetStoragePathsAsync();

    /// <summary>POST /api/storage/paths</summary>
    Task<MediaStoragePaths> SaveStoragePathsAsync(MediaStoragePaths paths);
}
