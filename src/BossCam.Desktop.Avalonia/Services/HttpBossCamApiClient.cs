using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Models;

namespace BossCam.Desktop.Avalonia.Services;

/// <summary>
/// Production implementation of <see cref="IBossCamApiClient"/> that talks to
/// the BossCamService HTTP API at a configurable base address.
///
/// Covers the complete API surface exposed by BossCam.Service: devices, discovery,
/// probe/validation, raw + typed settings, control points, image/grouped-config,
/// streaming/snapshot, recordings, highlights, SD playback, diagnostics, firmware/
/// contracts/protocols, connectivity and storage paths.
/// </summary>
public sealed class HttpBossCamApiClient : IBossCamApiClient
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>Creates a client pointing at the given <paramref name="baseAddress"/>.</summary>
    public HttpBossCamApiClient(string baseAddress = "http://127.0.0.1:5317")
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>Internal constructor that takes an existing <see cref="HttpClient"/>.</summary>
    internal HttpBossCamApiClient(HttpClient http) => _http = http;

    /// <summary>Optional LAN bearer token sent as X-LAN-Token on every request.</summary>
    public string? LanToken
    {
        get => _lanToken;
        set
        {
            _lanToken = value;
            _http.DefaultRequestHeaders.Remove("X-LAN-Token");
            if (!string.IsNullOrWhiteSpace(value))
            {
                _http.DefaultRequestHeaders.TryAddWithoutValidation("X-LAN-Token", value);
            }
        }
    }

    private string? _lanToken;

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return await ReadContentAsync<T>(res, ct).ConfigureAwait(false);
    }

    private async Task<T> PostJsonAsync<T>(string path, object? body = null, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync(path, body, _json, ct).ConfigureAwait(false);
        return await ReadContentAsync<T>(res, ct).ConfigureAwait(false);
    }

    private async Task<T> ReadContentAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{res.RequestMessage?.RequestUri?.PathAndQuery ?? "(request)"} -> {(int)res.StatusCode} {res.ReasonPhrase}: {Truncate(text, 400)}");
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return default!;
        }

        var parsed = JsonSerializer.Deserialize<T>(text, _json);
        return parsed ?? throw new HttpRequestException($"Empty body for {res.RequestMessage?.RequestUri?.PathAndQuery}");
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    // ── Health / connectivity ────────────────────────────────────────

    public Task<JsonElement?> GetHealthAsync() => GetJsonAsync<JsonElement?>("/api/health");

    public Task<List<DeviceConnectivitySnapshot>> GetConnectivityAllAsync()
        => GetJsonAsync<List<DeviceConnectivitySnapshot>>("/api/devices/connectivity");

    public Task<DeviceConnectivitySnapshot?> GetConnectivityAsync(Guid deviceId)
        => GetJsonAsync<DeviceConnectivitySnapshot?>($"/api/devices/{deviceId}/connectivity");

    public Task<JsonElement?> DiagnoseConnectivityAsync(Guid deviceId)
        => PostJsonAsync<JsonElement?>($"/api/devices/{deviceId}/connectivity/diagnose", new { });

    public Task<JsonElement?> ReconnectDeviceAsync(Guid deviceId)
        => PostJsonAsync<JsonElement?>($"/api/devices/{deviceId}/connectivity/reconnect", new { });

    // ── Devices / discovery / registration ───────────────────────────

    public async Task<List<DeviceIdentity>> GetDevicesAsync()
    {
        var devices = await GetJsonAsync<List<DeviceIdentity>>("/api/devices");
        return devices ?? [];
    }

    public Task<List<DeviceIdentity>> DiscoverAsync()
        => PostJsonAsync<List<DeviceIdentity>>("/api/devices/discover");

    public Task<DeviceIdentity?> RegisterAsync(
        string ipAddress, int port, string? loginName, string? password, string? name, string? hardwareModel)
        => PostJsonAsync<DeviceIdentity?>("/api/devices/register", new
        {
            ipAddress,
            port,
            loginName,
            password,
            name,
            hardwareModel
        });

    public Task<List<DeviceIdentity>> RegisterManyAsync(
        IEnumerable<(string IpAddress, int Port, string? LoginName, string? Password, string? HardwareModel)> requests)
        => PostJsonAsync<List<DeviceIdentity>>("/api/devices/register-many",
            requests.Select(r => new
            {
                ipAddress = r.IpAddress,
                port = r.Port,
                loginName = r.LoginName,
                password = r.Password,
                hardwareModel = r.HardwareModel
            }));

    public Task<List<DeviceIdentity>> RegisterAegonLanAsync(string? lorexPassword, string? wvcPassword)
        => PostJsonAsync<List<DeviceIdentity>>("/api/devices/register-aegon-lan", new { lorexPassword, wvcPassword });

    // ── Probe / validation / capabilities ────────────────────────────

    public Task<CapabilityMap?> ProbeAsync(Guid deviceId)
        => PostJsonAsync<CapabilityMap?>($"/api/devices/{deviceId}/probe");

    public Task<CapabilityProbeResult?> ValidateAsync(Guid deviceId, object? options = null)
        => PostJsonAsync<CapabilityProbeResult?>($"/api/devices/{deviceId}/validation/run", options);

    public Task<List<EndpointValidationResult>> GetValidationAsync(Guid deviceId)
        => GetJsonAsync<List<EndpointValidationResult>>($"/api/devices/{deviceId}/validation");

    public Task<List<EndpointTranscript>> GetValidationTranscriptsAsync(Guid deviceId, int limit = 50)
        => GetJsonAsync<List<EndpointTranscript>>($"/api/devices/{deviceId}/validation/transcripts?limit={limit}");

    public Task<CapabilityMap?> GetCapabilitiesAsync(Guid deviceId)
        => GetJsonAsync<CapabilityMap?>($"/api/devices/{deviceId}/capabilities");

    // ── Settings (raw) + maintenance ─────────────────────────────────

    public Task<SettingsSnapshot?> ReadSettingsAsync(Guid deviceId)
        => GetJsonAsync<SettingsSnapshot?>($"/api/devices/{deviceId}/settings");

    public Task<SettingsSnapshot?> GetLastSettingsAsync(Guid deviceId)
        => GetJsonAsync<SettingsSnapshot?>($"/api/devices/{deviceId}/settings/last");

    public Task<WriteResult> WriteSettingsAsync(Guid deviceId, WritePlan plan)
        => PostJsonAsync<WriteResult>($"/api/devices/{deviceId}/settings/write", plan);

    public Task<MaintenanceResult> ExecuteMaintenanceAsync(Guid deviceId, string operation, JsonObject? payload)
        => PostJsonAsync<MaintenanceResult>($"/api/devices/{deviceId}/maintenance/{operation}", payload);

    // ── Typed settings / control points / features ───────────────────

    public Task<List<TypedSettingGroupSnapshot>> GetTypedSettingsAsync(Guid deviceId)
        => GetJsonAsync<List<TypedSettingGroupSnapshot>>($"/api/devices/{deviceId}/settings/typed");

    public Task<List<TypedSettingGroupSnapshot>> NormalizeTypedSettingsAsync(Guid deviceId)
        => PostJsonAsync<List<TypedSettingGroupSnapshot>>($"/api/devices/{deviceId}/settings/typed/refresh");

    public Task<ControlPointInventoryReport?> GetControlPointsAsync(Guid deviceId)
        => GetJsonAsync<ControlPointInventoryReport?>($"/api/devices/{deviceId}/control-points");

    public Task<WriteResult> ApplyTypedFieldAsync(Guid deviceId, string fieldKey, JsonNode? value, bool expertOverride = false)
        => PostJsonAsync<WriteResult>($"/api/devices/{deviceId}/settings/typed/apply",
            new { fieldKey, value, expertOverride });

    public Task<List<WriteResult>> ApplyTypedBatchAsync(Guid deviceId, IEnumerable<TypedFieldChange> changes, bool expertOverride = false)
        => PostJsonAsync<List<WriteResult>>($"/api/devices/{deviceId}/settings/typed/apply-batch",
            new { changes, expertOverride });

    public Task<List<PersistenceEligibleField>> GetPersistenceEligibleFieldsAsync(Guid deviceId)
        => GetJsonAsync<List<PersistenceEligibleField>>($"/api/devices/{deviceId}/persistence/eligible-fields");

    public Task<List<PersistenceVerificationResult>> GetPersistenceResultsAsync(Guid deviceId, int limit = 20)
        => GetJsonAsync<List<PersistenceVerificationResult>>($"/api/devices/{deviceId}/persistence?limit={limit}");

    public Task<PersistenceVerificationResult> VerifyPersistenceAsync(Guid deviceId, object request)
        => PostJsonAsync<PersistenceVerificationResult>($"/api/devices/{deviceId}/persistence/verify", request);

    // ── Image / grouped-config / semantic insights ───────────────────

    public Task<List<ImageControlInventoryItem>> GetImageInventoryAsync(Guid deviceId)
        => GetJsonAsync<List<ImageControlInventoryItem>>($"/api/devices/{deviceId}/image/inventory");

    public Task<ImageWritableTestSetProfile?> GetImageWritableTestSetAsync(Guid deviceId)
        => GetJsonAsync<ImageWritableTestSetProfile?>($"/api/devices/{deviceId}/image/writable-test-set");

    public Task<List<ImageFieldBehaviorMap>> GetImageBehaviorMapsAsync(Guid deviceId)
        => GetJsonAsync<List<ImageFieldBehaviorMap>>($"/api/devices/{deviceId}/image/behavior-maps");

    public Task<JsonElement?> RunImageTruthSweepAsync(Guid deviceId, object? request = null)
        => PostJsonAsync<JsonElement?>($"/api/devices/{deviceId}/image/truth-sweep", request);

    public Task<JsonElement?> GetGroupedConfigSnapshotsAsync(Guid deviceId, bool? refreshFromDevice = null)
        => GetJsonAsync<JsonElement?>(
            $"/api/devices/{deviceId}/grouped-config/snapshots{(refreshFromDevice is bool b ? $"?refreshFromDevice={b.ToString().ToLowerInvariant()}" : "")}");

    public Task<JsonElement?> GetGroupedConfigProfilesAsync(Guid deviceId, string? firmwareFingerprint = null)
        => GetJsonAsync<JsonElement?>(
            $"/api/devices/{deviceId}/grouped-config/profiles{(string.IsNullOrWhiteSpace(firmwareFingerprint) ? "" : $"?firmwareFingerprint={Uri.EscapeDataString(firmwareFingerprint)}")}");

    public Task<JsonElement?> GetGroupedRetestResultsAsync(Guid deviceId, int limit = 50)
        => GetJsonAsync<JsonElement?>($"/api/devices/{deviceId}/grouped-config/retest-results?limit={limit}");

    public Task<JsonElement?> GetSdkFieldCatalogAsync()
        => GetJsonAsync<JsonElement?>("/api/grouped-config/sdk-field-catalog");

    public Task<List<SemanticWriteObservation>> GetSemanticHistoryAsync(Guid deviceId, int limit = 50)
        => GetJsonAsync<List<SemanticWriteObservation>>($"/api/devices/{deviceId}/semantic/history?limit={limit}");

    public Task<List<FieldConstraintProfile>> GetConstraintsAsync(Guid deviceId)
        => GetJsonAsync<List<FieldConstraintProfile>>($"/api/devices/{deviceId}/constraints");

    public Task<List<DependencyMatrixProfile>> GetDependenciesAsync(Guid deviceId)
        => GetJsonAsync<List<DependencyMatrixProfile>>($"/api/devices/{deviceId}/dependencies");

    public Task<JsonElement?> RunNetworkRecoveryAsync(Guid deviceId, object context)
        => PostJsonAsync<JsonElement?>($"/api/devices/{deviceId}/network/recovery", context);

    public Task<NativeFallbackAssessment?> GetNativeFallbackAssessmentAsync(Guid deviceId)
        => GetJsonAsync<NativeFallbackAssessment?>($"/api/devices/{deviceId}/native-fallback-assessment");

    // ── Streaming / snapshot ─────────────────────────────────────────

    public Task<List<VideoSourceDescriptor>> GetSourcesAsync(Guid deviceId)
        => GetJsonAsync<List<VideoSourceDescriptor>>($"/api/devices/{deviceId}/sources");

    public Task<PreviewSession?> GetPreviewAsync(Guid deviceId)
        => GetJsonAsync<PreviewSession?>($"/api/devices/{deviceId}/preview");

    public async Task<byte[]?> GetSnapshotAsync(Guid deviceId)
    {
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var res = await _http.GetAsync($"/api/devices/{deviceId}/snapshot?t={ts}").ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }
            var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return bytes.Length > 100 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<JsonElement?> GetLiveInfoAsync(Guid deviceId)
    {
        try
        {
            return await GetJsonAsync<JsonElement?>($"/api/devices/{deviceId}/live-info").ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public string GetLiveMjpegUrl(Guid deviceId, string quality = "sub")
        => BuildAbsolute($"/api/devices/{deviceId}/live.mjpeg?quality={Uri.EscapeDataString(quality)}&t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

    public string GetLiveTsUrl(Guid deviceId, string quality = "sub")
        => BuildAbsolute($"/api/devices/{deviceId}/live.ts?quality={Uri.EscapeDataString(quality)}&t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

    public async Task<bool> SaveSnapshotAsync(Guid deviceId)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync($"/api/storage/save-snapshot/{deviceId}", new { }, _json).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Recordings ───────────────────────────────────────────────────

    public Task<List<RecordingProfile>> GetRecordingProfilesAsync(Guid? deviceId = null)
        => GetJsonAsync<List<RecordingProfile>>($"/api/recordings{(deviceId is Guid id ? $"?deviceId={id}" : "")}");

    public Task SaveRecordingProfilesAsync(IEnumerable<RecordingProfile> profiles)
        => PostJsonAsync<object?>("/api/recordings", profiles);

    public Task<RecordingJob> StartRecordingAsync(Guid deviceId, string? outputDirectory = null, string? sourceUrl = null)
        => PostJsonAsync<RecordingJob>("/api/recordings/start", new { deviceId, outputDirectory, sourceUrl });

    public Task<List<RecordingJob>> StartAllRecordingsAsync(bool? preferSubStream = null)
        => PostJsonAsync<List<RecordingJob>>("/api/recordings/start-all", new { preferSubStream });

    public Task<RecordingJob> StopRecordingAsync(Guid jobId)
        => PostJsonAsync<RecordingJob>($"/api/recordings/stop/{jobId}");

    public Task<List<RecordingJob>> StopAllRecordingsAsync()
        => PostJsonAsync<List<RecordingJob>>("/api/recordings/stop-all");

    public Task<List<RecordingJob>> GetRecordingJobsAsync()
        => GetJsonAsync<List<RecordingJob>>("/api/recordings/jobs");

    public Task<List<RecordingSegment>> RefreshRecordingIndexAsync(Guid? deviceId = null)
        => PostJsonAsync<List<RecordingSegment>>($"/api/recordings/index/refresh{(deviceId is Guid id ? $"?deviceId={id}" : "")}");

    public Task<List<RecordingSegment>> GetRecordingIndexAsync(int limit = 40)
        => GetJsonAsync<List<RecordingSegment>>($"/api/recordings/index?limit={limit}");

    public Task<ClipExportResult> ExportClipAsync(ClipExportRequest request)
        => PostJsonAsync<ClipExportResult>("/api/recordings/export", request);

    public Task<RecordingHousekeepingResult> RunHousekeepingAsync(Guid? deviceId = null)
        => PostJsonAsync<RecordingHousekeepingResult>($"/api/recordings/housekeeping{(deviceId is Guid id ? $"?deviceId={id}" : "")}");

    public Task<List<RecordingJob>> ReconcileRecordingsAsync()
        => PostJsonAsync<List<RecordingJob>>("/api/recordings/reconcile");

    public Task<StallCheckResult> CheckStalledRecordingsAsync()
        => PostJsonAsync<StallCheckResult>("/api/recordings/stall-check");

    public string GetRecordingDownloadUrl(string path)
        => BuildAbsolute($"/api/recordings/download?path={Uri.EscapeDataString(path)}");

    // ── Highlights board ─────────────────────────────────────────────

    public Task<HighlightBoardSnapshot> GetHighlightsAsync()
        => GetJsonAsync<HighlightBoardSnapshot>("/api/highlights");

    public Task<HighlightBoardSnapshot> SelectHighlightAsync(Guid deviceId)
        => PostJsonAsync<HighlightBoardSnapshot>($"/api/highlights/select/{deviceId}");

    public Task<HighlightBoardSnapshot> HighlightNextAsync()
        => PostJsonAsync<HighlightBoardSnapshot>("/api/highlights/next");

    public Task<HighlightBoardSnapshot> HighlightPrevAsync()
        => PostJsonAsync<HighlightBoardSnapshot>("/api/highlights/prev");

    public Task<HighlightBoardSnapshot> HighlightStreamAsync(string mode)
        => PostJsonAsync<HighlightBoardSnapshot>($"/api/highlights/stream/{mode}");

    public Task<JsonElement?> RecordSelectedHighlightAsync()
        => PostJsonAsync<JsonElement?>("/api/highlights/record-selected");

    // ── SD / NVR playback ────────────────────────────────────────────

    public Task<NvrPlaybackCallResult> PlaybackFindFileAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end, string? cursor = null)
        => PostJsonAsync<NvrPlaybackCallResult>($"/api/devices/{deviceId}/playback/find-file",
            new { beginTime = begin, endTime = end, cursor });

    public Task<NvrPlaybackCallResult> PlaybackFindNextFileAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end, string? cursor = null)
        => PostJsonAsync<NvrPlaybackCallResult>($"/api/devices/{deviceId}/playback/find-next-file",
            new { beginTime = begin, endTime = end, cursor });

    public Task<NvrPlaybackCallResult> PlaybackGetFileByTimeAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end)
        => PostJsonAsync<NvrPlaybackCallResult>($"/api/devices/{deviceId}/playback/get-file-by-time",
            new { beginTime = begin, endTime = end });

    public Task<NvrPlaybackCallResult> PlaybackByTimeAsync(Guid deviceId, DateTimeOffset begin, DateTimeOffset end)
        => PostJsonAsync<NvrPlaybackCallResult>($"/api/devices/{deviceId}/playback/playback-by-time",
            new { beginTime = begin, endTime = end });

    public Task<NvrPlaybackCallResult> PlaybackFindCloseAsync(Guid deviceId)
        => PostJsonAsync<NvrPlaybackCallResult>($"/api/devices/{deviceId}/playback/find-close", new { });

    // ── Diagnostics ──────────────────────────────────────────────────

    public Task<List<WriteAuditEntry>> GetAuditEntriesAsync(Guid? deviceId = null, int limit = 50)
        => GetJsonAsync<List<WriteAuditEntry>>(BuildQuery("/api/diagnostics/audit", ("deviceId", deviceId), ("limit", limit)));

    public Task<List<EndpointTranscript>> GetTranscriptsAsync(Guid? deviceId = null, int limit = 50)
        => GetJsonAsync<List<EndpointTranscript>>(BuildQuery("/api/diagnostics/transcripts", ("deviceId", deviceId), ("limit", limit)));

    public Task<ProbeSession> StartProbeSessionAsync(ProbeSessionRequest request)
        => PostJsonAsync<ProbeSession>("/api/probe/sessions/start", request);

    public Task<List<ProbeSession>> GetProbeSessionsAsync(Guid? deviceId = null, int limit = 50)
        => GetJsonAsync<List<ProbeSession>>(BuildQuery("/api/probe/sessions", ("deviceId", deviceId), ("limit", limit)));

    public Task<List<ProbeStageResult>> GetProbeSessionStagesAsync(Guid sessionId)
        => GetJsonAsync<List<ProbeStageResult>>($"/api/probe/sessions/{sessionId}/stages");

    public Task<TruthSweepReport?> GetTruthSweepAsync(string? ips = null)
        => GetJsonAsync<TruthSweepReport?>($"/api/truth/sweep{(string.IsNullOrWhiteSpace(ips) ? "" : $"?ips={Uri.EscapeDataString(ips)}")}");

    // ── Firmware / contracts / protocols ─────────────────────────────

    public Task<List<FirmwareArtifact>> GetFirmwareArtifactsAsync()
        => GetJsonAsync<List<FirmwareArtifact>>("/api/firmware");

    public Task<FirmwareArtifact> RegisterFirmwareAsync(string filePath)
        => PostJsonAsync<FirmwareArtifact>("/api/firmware/register", new { filePath });

    public Task<List<FirmwareCapabilityProfile>> GetFirmwareCapabilitiesAsync()
        => GetJsonAsync<List<FirmwareCapabilityProfile>>("/api/firmware/capabilities");

    public Task<List<EndpointContract>> GetContractEndpointsAsync(Guid? deviceId = null)
        => GetJsonAsync<List<EndpointContract>>($"/api/contracts/endpoints{(deviceId is Guid id ? $"?deviceId={id}" : "")}");

    public Task<List<EndpointContractFixture>> GetContractFixturesAsync(Guid? deviceId = null)
        => GetJsonAsync<List<EndpointContractFixture>>($"/api/contracts/fixtures{(deviceId is Guid id ? $"?deviceId={id}" : "")}");

    public Task<JsonElement?> PromoteContractFixturesAsync(Guid deviceId, string exportRoot)
        => PostJsonAsync<JsonElement?>($"/api/contracts/fixtures/promote/{deviceId}", new { exportRoot });

    public Task<JsonElement?> CleanupContractFixturesAsync(int olderThanDays = 90, int maxPerDevice = 2000, int maxTotal = 10000)
        => PostJsonAsync<JsonElement?>("/api/contracts/fixtures/cleanup",
            new { olderThanDays, maxPerDevice, maxTotal });

    public Task<List<ProtocolManifest>> GetProtocolsAsync()
        => GetJsonAsync<List<ProtocolManifest>>("/api/protocols");

    public Task<List<ProtocolManifest>> RefreshProtocolsAsync()
        => PostJsonAsync<List<ProtocolManifest>>("/api/protocols/refresh");

    // ── Storage paths ────────────────────────────────────────────────

    public Task<MediaStoragePaths> GetStoragePathsAsync()
        => GetJsonAsync<MediaStoragePaths>("/api/storage/paths");

    public Task<MediaStoragePaths> SaveStoragePathsAsync(MediaStoragePaths paths)
        => PostJsonAsync<MediaStoragePaths>("/api/storage/paths", paths);

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Resolves a relative API path against the configured base address so the
    /// GUI can hand the result straight to image sources / external players.
    /// Falls back to the relative path when no base address is configured.
    /// </summary>
    private string BuildAbsolute(string relativePath)
        => _http.BaseAddress is not null
            ? new Uri(_http.BaseAddress, relativePath).ToString()
            : relativePath;

    private static string BuildQuery(string path, params (string Key, object? Value)[] pairs)
    {
        var parts = new List<string>();
        foreach (var (key, value) in pairs)
        {
            if (value is null)
            {
                continue;
            }
            parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value.ToString()!)}");
        }
        return parts.Count == 0 ? path : path + "?" + string.Join("&", parts);
    }
}
