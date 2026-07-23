using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure;
using BossCam.NativeBridge;
using BossCam.Service.Hosted;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
if (OperatingSystem.IsLinux())
{
    builder.Configuration.AddJsonFile("appsettings.Linux.json", optional: true, reloadOnChange: true);
}

var localApiBaseUrl = builder.Configuration["BossCam:LocalApiBaseUrl"] ?? "http://127.0.0.1:5317";
builder.WebHost.UseUrls(localApiBaseUrl);
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService();
}
else if (OperatingSystem.IsLinux())
{
    builder.Host.UseSystemd();
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddBossCamInfrastructure(builder.Configuration);
builder.Services.AddBossCamCore();
builder.Services.AddHostedService<BossCamBootstrapWorker>();
builder.Services.AddHostedService<RecordingLifecycleWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    timestamp = DateTimeOffset.UtcNow,
    platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    processArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    contentRoot = app.Environment.ContentRootPath,
    ffmpeg = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH")
        ?? (File.Exists("/usr/bin/ffmpeg") ? "/usr/bin/ffmpeg" : null)
}));

app.MapGet("/api/devices", async (IApplicationStore store, CancellationToken ct) =>
{
    var devices = await store.GetDevicesAsync(ct);
    var withIp = devices.Where(static device => !string.IsNullOrWhiteSpace(device.IpAddress))
        .GroupBy(device => device.IpAddress!, StringComparer.OrdinalIgnoreCase)
        .Select(group => group
            .OrderByDescending(static device => string.Equals(device.DeviceType, "IPC", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static device => !string.IsNullOrWhiteSpace(device.LoginName))
            .ThenByDescending(static device => !string.IsNullOrWhiteSpace(device.Password) || !string.IsNullOrWhiteSpace(device.PasswordCiphertext))
            .ThenByDescending(static device => string.Equals(device.DisplayName, "5523-W", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static device => !string.IsNullOrWhiteSpace(device.FirmwareVersion))
            .ThenByDescending(static device => !string.IsNullOrWhiteSpace(device.HardwareModel))
            .ThenByDescending(static device => device.DiscoveredAt)
            .First())
        .ToList();
    var withoutIp = devices.Where(static device => string.IsNullOrWhiteSpace(device.IpAddress)).ToList();
    return Results.Ok(withIp.Concat(withoutIp).OrderByDescending(static device => device.DiscoveredAt).ToList());
});
app.MapPost("/api/devices/discover", async (DiscoveryCoordinator coordinator, CancellationToken ct) => Results.Ok(await coordinator.RunAsync(ct)));
app.MapPost("/api/devices/register", async (DeviceRegisterRequest request, DeviceRegistrationService registrationService, CancellationToken ct) =>
    Results.Ok(await registrationService.RegisterAsync(request, ct)));
app.MapPost("/api/devices/register-many", async (List<DeviceRegisterRequest> requests, DeviceRegistrationService registrationService, CancellationToken ct) =>
    Results.Ok(await registrationService.RegisterManyAsync(requests ?? [], ct)));
app.MapPost("/api/devices/register-aegon-lan", async (AegonLanRegisterRequest? request, DeviceRegistrationService registrationService, CancellationToken ct) =>
    Results.Ok(await registrationService.RegisterAegonLanDefaultsAsync(request?.LorexPassword, request?.WvcPassword, ct)));
app.MapPost("/api/devices/{id:guid}/probe", async (Guid id, CapabilityProbeService probeService, CancellationToken ct) =>
{
    var result = await probeService.ProbeAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/validation/run", async (Guid id, ValidationRunOptions? options, ProtocolValidationService validationService, CancellationToken ct) =>
{
    var result = await validationService.ValidateDeviceAsync(id, options, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/validation", async (Guid id, ProtocolValidationService validationService, CancellationToken ct) =>
    Results.Ok(await validationService.GetValidationResultsAsync(id, ct)));
app.MapGet("/api/devices/{id:guid}/validation/transcripts", async (Guid id, int? limit, ProtocolValidationService validationService, CancellationToken ct) =>
    Results.Ok(await validationService.GetTranscriptsAsync(id, limit ?? 200, ct)));
app.MapPost("/api/probe/sessions/start", async (ProbeSessionRequest request, ProbeSessionService probeSessionService, CancellationToken ct) =>
{
    var session = await probeSessionService.StartSessionAsync(request, ct);
    return session is null ? Results.NotFound() : Results.Ok(session);
});
app.MapGet("/api/probe/sessions", async (Guid? deviceId, int? limit, ProbeSessionService probeSessionService, CancellationToken ct) =>
    Results.Ok(await probeSessionService.GetSessionsAsync(deviceId, limit ?? 50, ct)));
app.MapGet("/api/probe/sessions/{id:guid}/stages", async (Guid id, ProbeSessionService probeSessionService, CancellationToken ct) =>
    Results.Ok(await probeSessionService.GetStagesAsync(id, ct)));
app.MapGet("/api/truth/sweep", async (string? ips, ProbeSessionService probeSessionService, CancellationToken ct) =>
{
    var targetIps = string.IsNullOrWhiteSpace(ips)
        ? null
        : ips.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return Results.Ok(await probeSessionService.BuildTruthSweepReportAsync(targetIps, ct));
});
app.MapGet("/api/devices/{id:guid}/capabilities", async (Guid id, IApplicationStore store, CancellationToken ct) =>
{
    var result = await store.GetCapabilityMapAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/settings", async (Guid id, SettingsService settingsService, CancellationToken ct) =>
{
    var result = await settingsService.ReadAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/settings/last", async (Guid id, SettingsService settingsService, CancellationToken ct) =>
{
    var result = await settingsService.GetLastSnapshotAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/settings/write", async (Guid id, WritePlan plan, SettingsService settingsService, CancellationToken ct) =>
{
    var result = await settingsService.WriteAsync(id, plan, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/settings/typed", async (Guid id, TypedSettingsService typedSettingsService, CancellationToken ct) =>
    Results.Ok(await typedSettingsService.GetTypedSettingsAsync(id, ct)));
app.MapGet("/api/devices/{id:guid}/control-points", async (Guid id, ControlPointInventoryService controlPointInventoryService, CancellationToken ct) =>
{
    var result = await controlPointInventoryService.GetReportAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/endpoint-surface", async (Guid id, EndpointSurfaceService endpointSurfaceService, CancellationToken ct) =>
{
    var result = await endpointSurfaceService.GetReportAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/settings/typed/refresh", async (Guid id, TypedSettingsService typedSettingsService, CancellationToken ct) =>
    Results.Ok(await typedSettingsService.NormalizeDeviceAsync(id, refreshFromDevice: true, ct)));
app.MapPost("/api/devices/{id:guid}/settings/typed/apply", async (Guid id, TypedSettingApplyRequest request, TypedSettingsService typedSettingsService, CancellationToken ct) =>
{
    var result = await typedSettingsService.ApplyTypedFieldAsync(id, request.FieldKey, request.Value, request.ExpertOverride, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/settings/typed/apply-batch", async (Guid id, TypedSettingBatchApplyRequest request, TypedSettingsService typedSettingsService, CancellationToken ct) =>
    Results.Ok(await typedSettingsService.ApplyTypedChangesAsync(id, request.Changes, request.ExpertOverride, ct)));
app.MapPost("/api/devices/{id:guid}/maintenance/{operation}", async (Guid id, string operation, JsonObject? payload, SettingsService settingsService, CancellationToken ct) =>
{
    if (!Enum.TryParse<MaintenanceOperation>(operation, true, out var parsed))
    {
        return Results.BadRequest(new { error = $"Unknown operation '{operation}'." });
    }

    var result = await settingsService.ExecuteMaintenanceAsync(id, parsed, payload, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/sources", async (Guid id, TransportBroker transportBroker, CancellationToken ct) => Results.Ok(await transportBroker.GetSourcesAsync(id, ct)));
app.MapGet("/api/devices/{id:guid}/preview", async (Guid id, TransportBroker transportBroker, CancellationToken ct) =>
{
    var result = await transportBroker.StartPreviewAsync(id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapGet("/api/devices/{id:guid}/snapshot", async (Guid id, IApplicationStore store, CancellationToken ct) =>
{
    var device = await store.GetDeviceAsync(id, ct);
    if (device is null || string.IsNullOrWhiteSpace(device.IpAddress))
    {
        return Results.NotFound();
    }

    var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
    var password = device.Password ?? string.Empty;
    var port = device.Port <= 0 ? 80 : device.Port;
    var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
    var candidatePaths = new[]
    {
        $"/NetSDK/Video/encode/channel/101/snapShot",
        $"/NetSDK/Video/encode/channel/102/snapShot",
        $"/NetSDK/Video/input/channel/1/snapShot",
        $"/cgi-bin/snapshot.cgi",
        $"/snapshot.jpg"
    };

    using var handler = new HttpClientHandler { Credentials = new System.Net.NetworkCredential(user, password) };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    foreach (var path in candidatePaths)
    {
        try
        {
            var url = $"http://{device.IpAddress}:{port}{path}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 500 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                return Results.File(bytes, "image/jpeg");
            }
        }
        catch
        {
            // try next candidate
        }
    }

    // Some Juan units return HTTP 403 on snapShot while RTSP main still works — grab one JPEG frame.
    var ffmpeg = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH")
        ?? (File.Exists("/usr/bin/ffmpeg") ? "/usr/bin/ffmpeg" : null);
    if (ffmpeg is not null)
    {
        var auth = $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@";
        var rtsp = $"rtsp://{auth}{device.IpAddress}:554/ch0_0.264";
        var tmp = Path.Combine(Path.GetTempPath(), $"bosscam-snap-{id:N}.jpg");
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -loglevel error -rtsp_transport tcp -i \"{rtsp}\" -map 0:v:0 -frames:v 1 -q:v 3 -y \"{tmp}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (proc is not null)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    await proc.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                }

                if (File.Exists(tmp))
                {
                    var bytes = await File.ReadAllBytesAsync(tmp, ct);
                    if (bytes.Length > 500 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                    {
                        return Results.File(bytes, "image/jpeg");
                    }
                }
            }
        }
        catch
        {
            // fall through to 502
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    return Results.StatusCode(StatusCodes.Status502BadGateway);
});
app.MapGet("/api/protocols", async (ProtocolCatalogService protocolCatalogService, CancellationToken ct) => Results.Ok(await protocolCatalogService.GetAsync(ct)));
app.MapPost("/api/protocols/refresh", async (ProtocolCatalogService protocolCatalogService, CancellationToken ct) => Results.Ok(await protocolCatalogService.RefreshAsync(ct)));
app.MapGet("/api/diagnostics/audit", async (Guid? deviceId, int? limit, IApplicationStore store, CancellationToken ct) => Results.Ok(await store.GetAuditEntriesAsync(deviceId, limit ?? 100, ct)));
app.MapGet("/api/diagnostics/transcripts", async (Guid? deviceId, int? limit, ProtocolValidationService validationService, CancellationToken ct) =>
    Results.Ok(await validationService.GetTranscriptsAsync(deviceId, limit ?? 200, ct)));
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
app.MapGet("/api/firmware/capabilities", async (CapabilityPromotionService capabilityPromotionService, CancellationToken ct) =>
    Results.Ok(await capabilityPromotionService.GetProfilesAsync(ct)));
app.MapGet("/api/contracts/endpoints", async (Guid? deviceId, IApplicationStore store, IEndpointContractCatalog catalog, CancellationToken ct) =>
{
    if (deviceId is Guid id)
    {
        var device = await store.GetDeviceAsync(id, ct);
        if (device is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(await catalog.GetContractsForDeviceAsync(device, ct));
    }

    return Results.Ok(await catalog.GetContractsAsync(ct));
});
app.MapPost("/api/contracts/fixtures/promote/{deviceId:guid}", async (Guid deviceId, ContractFixturePromotionRequest request, IContractEvidenceService evidenceService, CancellationToken ct) =>
    Results.Ok(await evidenceService.PromoteFromTranscriptsAsync(deviceId, request.ExportRoot, ct)));
app.MapGet("/api/contracts/fixtures", async (Guid? deviceId, IContractEvidenceService evidenceService, CancellationToken ct) =>
    Results.Ok(await evidenceService.GetFixturesAsync(deviceId, ct)));
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
app.MapPost("/api/devices/{id:guid}/network/recovery", async (Guid id, NetworkRecoveryContext context, SemanticTrustService semanticTrustService, CancellationToken ct) =>
    Results.Ok(await semanticTrustService.RecoverNetworkAsync(context with { DeviceId = id }, ct)));

// Operator media folders (continuous / highlights / snapshots)
static string MediaStorageConfigPath()
{
    var root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BossCamSuite");
    Directory.CreateDirectory(root);
    return Path.Combine(root, "media-storage.json");
}

static MediaStoragePaths DefaultMediaStoragePaths()
{
    var root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BossCamSuite");
    return new MediaStoragePaths
    {
        ContinuousRecordings = Path.Combine(root, "recordings", "continuous"),
        Highlights = Path.Combine(root, "recordings", "highlights"),
        Snapshots = Path.Combine(root, "snapshots")
    };
}

static MediaStoragePaths LoadMediaStoragePaths()
{
    var path = MediaStorageConfigPath();
    if (!File.Exists(path))
    {
        var defaults = DefaultMediaStoragePaths();
        Directory.CreateDirectory(defaults.ContinuousRecordings);
        Directory.CreateDirectory(defaults.Highlights);
        Directory.CreateDirectory(defaults.Snapshots);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(defaults, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return defaults;
    }

    try
    {
        var loaded = System.Text.Json.JsonSerializer.Deserialize<MediaStoragePaths>(File.ReadAllText(path));
        return loaded ?? DefaultMediaStoragePaths();
    }
    catch
    {
        return DefaultMediaStoragePaths();
    }
}

app.MapGet("/api/storage/paths", () => Results.Ok(LoadMediaStoragePaths()));
app.MapPost("/api/storage/paths", (MediaStoragePaths paths) =>
{
    if (string.IsNullOrWhiteSpace(paths.ContinuousRecordings)
        || string.IsNullOrWhiteSpace(paths.Highlights)
        || string.IsNullOrWhiteSpace(paths.Snapshots))
    {
        return Results.BadRequest(new { error = "ContinuousRecordings, Highlights, and Snapshots paths are required." });
    }

    var normalized = new MediaStoragePaths
    {
        ContinuousRecordings = Path.GetFullPath(paths.ContinuousRecordings.Trim()),
        Highlights = Path.GetFullPath(paths.Highlights.Trim()),
        Snapshots = Path.GetFullPath(paths.Snapshots.Trim())
    };
    Directory.CreateDirectory(normalized.ContinuousRecordings);
    Directory.CreateDirectory(normalized.Highlights);
    Directory.CreateDirectory(normalized.Snapshots);
    File.WriteAllText(MediaStorageConfigPath(), System.Text.Json.JsonSerializer.Serialize(normalized, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return Results.Ok(normalized);
});

app.MapPost("/api/storage/save-snapshot/{id:guid}", async (Guid id, IApplicationStore store, CancellationToken ct) =>
{
    var device = await store.GetDeviceAsync(id, ct);
    if (device is null || string.IsNullOrWhiteSpace(device.IpAddress))
    {
        return Results.NotFound(new { error = "Device not found." });
    }

    var paths = LoadMediaStoragePaths();
    Directory.CreateDirectory(paths.Snapshots);
    // Reuse proxy by fetching snapShot then writing
    var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
    var password = device.Password ?? string.Empty;
    var port = device.Port <= 0 ? 80 : device.Port;
    var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{device.IpAddress}:{port}/NetSDK/Video/encode/channel/101/snapShot");
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
    using var response = await client.SendAsync(request, ct);
    byte[] bytes;
    if (response.IsSuccessStatusCode)
    {
        bytes = await response.Content.ReadAsByteArrayAsync(ct);
    }
    else
    {
        return Results.StatusCode((int)response.StatusCode);
    }

    if (bytes.Length < 500)
    {
        return Results.BadRequest(new { error = "Snapshot payload too small." });
    }

    var name = $"{(device.IpAddress ?? id.ToString("N")).Replace('.', '_')}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.jpg";
    var filePath = Path.Combine(paths.Snapshots, name);
    await File.WriteAllBytesAsync(filePath, bytes, ct);
    return Results.Ok(new { path = filePath, bytes = bytes.Length });
});

app.MapGet("/api/recordings", async (Guid? deviceId, IApplicationStore store, CancellationToken ct) => Results.Ok(await store.GetRecordingProfilesAsync(deviceId, ct)));
app.MapPost("/api/recordings", async (IEnumerable<RecordingProfile> profiles, IApplicationStore store, CancellationToken ct) =>
{
    await store.SaveRecordingProfilesAsync(profiles, ct);
    return Results.Accepted();
});
app.MapPost("/api/recordings/start", async (RecordingStartRequest request, RecordingService recordingService, CancellationToken ct) =>
{
    try
    {
        var job = await recordingService.StartAsync(request, ct);
        return Results.Ok(job);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Device not found", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("No video source", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("ffmpeg not found", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapPost("/api/recordings/start-all", async (bool? preferSubStream, RecordingService recordingService, CancellationToken ct) =>
    Results.Ok(await recordingService.StartAllAsync(preferSubStream ?? false, ct)));
app.MapPost("/api/recordings/stop-all", async (RecordingService recordingService, CancellationToken ct) =>
    Results.Ok(await recordingService.StopAllAsync(ct)));
app.MapPost("/api/recordings/stop/{jobId:guid}", async (Guid jobId, RecordingService recordingService, CancellationToken ct) =>
{
    var job = await recordingService.StopAsync(jobId, ct);
    return job is null ? Results.NotFound() : Results.Ok(job);
});
app.MapGet("/api/recordings/jobs", async (RecordingService recordingService, CancellationToken ct) =>
    Results.Ok(await recordingService.GetJobsAsync(ct)));
app.MapGet("/api/highlights", async (HighlightBoardService highlights, CancellationToken ct) =>
    Results.Ok(await highlights.GetStateAsync(ct)));
app.MapPost("/api/highlights/select/{deviceId:guid}", async (Guid deviceId, HighlightBoardService highlights, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await highlights.SelectAsync(deviceId, ct));
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});
app.MapPost("/api/highlights/next", async (HighlightBoardService highlights, CancellationToken ct) =>
    Results.Ok(await highlights.FlipAsync(+1, ct)));
app.MapPost("/api/highlights/prev", async (HighlightBoardService highlights, CancellationToken ct) =>
    Results.Ok(await highlights.FlipAsync(-1, ct)));
app.MapPost("/api/highlights/stream/{mode}", async (string mode, HighlightBoardService highlights, CancellationToken ct) =>
    Results.Ok(await highlights.SetPreferredStreamAsync(mode, ct)));
app.MapPost("/api/highlights/record-selected", async (HighlightBoardService highlights, CancellationToken ct) =>
    Results.Ok(await highlights.RecordSelectedAsync(ct)));
app.MapPost("/api/recordings/index/refresh", async (Guid? deviceId, RecordingService recordingService, CancellationToken ct) =>
    Results.Ok(await recordingService.RefreshIndexAsync(deviceId, ct)));
app.MapGet("/api/recordings/index", async (Guid? deviceId, int? limit, RecordingService recordingService, CancellationToken ct) =>
    Results.Ok(await recordingService.GetIndexedSegmentsAsync(deviceId, limit ?? 500, ct)));
app.MapPost("/api/recordings/export", async (ClipExportRequest request, RecordingService recordingService, CancellationToken ct) =>
    Results.Ok(await recordingService.ExportClipAsync(request, ct)));
app.MapPost("/api/recordings/reconcile", async (RecordingService recordingService, CancellationToken ct) =>
    Results.Ok(await recordingService.ReconcileAutoStartAsync(ct)));
app.MapPost("/api/recordings/housekeeping", async (Guid? deviceId, RecordingService recordingService, CancellationToken ct) =>
    Results.Ok(await recordingService.RunHousekeepingAsync(deviceId, ct)));
app.MapPost("/api/devices/{id:guid}/playback/find-file", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
{
    var result = await playbackService.FindFileAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/playback/find-next-file", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
{
    var result = await playbackService.FindNextFileAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/playback/get-file-by-time", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
{
    var result = await playbackService.GetFileByTimeAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/playback/playback-by-time", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
{
    var result = await playbackService.PlayBackByTimeExAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/playback/find-close", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
{
    var result = await playbackService.FindCloseAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/playback/playback-by-name", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
{
    var result = await playbackService.PlayBackByNameAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/playback/get-file-by-name", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
{
    var result = await playbackService.GetFileByNameAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/playback/stop-get-file", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
{
    var result = await playbackService.StopGetFileAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/playback/playback-save-data", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
{
    var result = await playbackService.PlayBackSaveDataAsync(id, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/devices/{id:guid}/playback/stop-playback-save", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
{
    var result = await playbackService.StopPlayBackSaveAsync(id, request, ct);
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
app.MapPost("/api/firmware/register", async (FirmwareRegisterRequest request, FirmwareCatalogService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.FilePath) || !File.Exists(request.FilePath))
    {
        return Results.BadRequest(new { error = "FilePath must point to an existing firmware file." });
    }

    var result = await service.RegisterAsync(request.FilePath, ct);
    return Results.Ok(result);
});
app.MapGet("/api/firmware", async (FirmwareCatalogService service, CancellationToken ct) => Results.Ok(await service.GetAsync(ct)));

// SPA fallback for operator console. Never swallow /api or /swagger with index.html.
app.MapFallback(async context =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"error":"Not found"}""");
        return;
    }

    var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    var index = Path.Combine(webRoot, "index.html");
    if (!File.Exists(index))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("Operator UI not found.");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index);
});

app.Run();

public sealed record AegonLanRegisterRequest(string? LorexPassword, string? WvcPassword);
public sealed record FirmwareRegisterRequest(string FilePath);
public sealed record TypedSettingApplyRequest(string FieldKey, JsonNode? Value, bool ExpertOverride);
public sealed record TypedSettingBatchApplyRequest(IReadOnlyCollection<TypedFieldChange> Changes, bool ExpertOverride);
public sealed record PersistenceFieldVerifyRequest(string FieldKey, JsonNode? Value, bool RebootForVerification, bool ExpertOverride);
public sealed record ContractFixturePromotionRequest(string ExportRoot);
public sealed record ImageTruthSweepRequest(bool IncludeBehaviorMapping, bool RefreshFromDevice, string? ExportRoot);

// Expose entry point for WebApplicationFactory / E2E host.
public partial class Program;
