using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Control;

public sealed record HttpAdapterResponse(HttpStatusCode StatusCode, JsonNode? Json, string RawContent);

public abstract class HttpControlAdapterBase(IOptions<BossCamRuntimeOptions> options, IHttpClientFactory httpClientFactory, ILogger logger)
{
    protected BossCamRuntimeOptions Options => options.Value;
    protected ILogger Logger => logger;
    protected IHttpClientFactory HttpClientFactory => httpClientFactory;

    protected Uri BuildDeviceUri(DeviceIdentity device, string endpoint)
        => BuildDeviceUri(device, endpoint, device.Port > 0 ? device.Port : 80);

    protected Uri BuildDeviceUri(DeviceIdentity device, string endpoint, int port)
    {
        var cleaned = endpoint.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("/ID", "/0", StringComparison.OrdinalIgnoreCase);
        return new Uri($"http://{device.IpAddress}:{port}{cleaned}", UriKind.Absolute);
    }

    protected async Task<HttpAdapterResponse?> SendAsync(DeviceIdentity device, string endpoint, string method, JsonObject? payload, CancellationToken cancellationToken, string? mediaType = null)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return null;
        }

        var payloadRaw = payload?.ToJsonString();
        // Discovery can record an ONVIF/media port (8888/8899) on the device while the
        // NetSDK REST control plane actually listens on 80 — verified live on 5523-W
        // units where deviceInfo/properties return 200 on :80 but transport-fail on the
        // recorded ONVIF port. Fall back to 80 only when the recorded port differs and
        // the first attempt fails at the TRANSPORT level (null); never on an HTTP
        // response (auth/semantic results are authoritative for that port).
        var ports = NetSdkPortCandidates.For(device.Port);
        foreach (var port in ports)
        {
            var uri = BuildDeviceUri(device, endpoint, port);
            var basic = await SendOnceAsync(device, uri, endpoint, method, payloadRaw, mediaType, useBasicHeader: true, useCredentialCache: false, cancellationToken);
            if (basic is not null && basic.StatusCode != HttpStatusCode.Unauthorized)
            {
                return basic;
            }

            if (basic is not null)
            {
                Logger.LogInformation(
                    "HTTP auth retry with digest/credential-cache. adapter={Adapter} device={Device} ip={Ip} endpoint={Endpoint} method={Method} firstStatus={Status}",
                    GetType().Name,
                    device.DisplayName,
                    device.IpAddress,
                    endpoint,
                    method,
                    basic.StatusCode);
                return await SendOnceAsync(device, uri, endpoint, method, payloadRaw, mediaType, useBasicHeader: false, useCredentialCache: true, cancellationToken);
            }

            if (ports.Length > 1)
            {
                Logger.LogWarning(
                    "HTTP transport failure on port {Port} for adapter={Adapter} device={Device} endpoint={Endpoint}; trying fallback port 80",
                    port,
                    GetType().Name,
                    device.DisplayName,
                    endpoint);
            }
        }

        return null;
    }

    protected async Task<HttpAdapterResponse?> SendMultipartAsync(DeviceIdentity device, string endpoint, MultipartFormDataContent content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return null;
        }

        using var handler = new HttpClientHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildDeviceUri(device, endpoint));
        ApplyBasicAuth(request, device);
        request.Content = content;

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            return new HttpAdapterResponse(response.StatusCode, TryParseNode(raw), raw);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Multipart HTTP call {Endpoint} failed for {Device}", endpoint, device.DisplayName);
            return null;
        }
    }

    public static JsonNode? TryParseNode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch
            {
            }
        }

        return JsonValue.Create(raw);
    }

    protected static SettingGroup BuildGroup(string name, IReadOnlyDictionary<string, HttpAdapterResponse?> responses)
    {
        var payload = new JsonObject();
        var values = new Dictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var response in responses.Where(static pair => pair.Value is not null))
        {
            payload[response.Key] = response.Value!.Json?.DeepClone() ?? JsonValue.Create(response.Value.RawContent);
            values[response.Key] = new SettingValue
            {
                Key = response.Key,
                DisplayName = response.Key,
                Value = response.Value.Json?.DeepClone() ?? JsonValue.Create(response.Value.RawContent),
                SourceEndpoint = response.Key,
                ValueKind = response.Value.Json is JsonArray ? SettingValueKind.Array : response.Value.Json is JsonObject ? SettingValueKind.Object : SettingValueKind.String
            };
        }

        return new SettingGroup
        {
            Name = name,
            DisplayName = name,
            RawPayload = payload,
            Values = values
        };
    }

    protected static bool IsSemanticSuccess(HttpAdapterResponse? response)
    {
        if (response is null)
        {
            return false;
        }

        if ((int)response.StatusCode is < 200 or >= 300)
        {
            return false;
        }

        var raw = response.RawContent ?? string.Empty;
        if (raw.Contains("Invalid Operation", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("ret=\"sorry\"", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("check in falied", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (response.Json is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("statusCode", out var codeNode)
                && codeNode is not null
                && int.TryParse(codeNode.ToJsonString().Trim('"'), out var code)
                && code != 0)
            {
                return false;
            }

            if (obj.TryGetPropertyValue("ret", out var retNode)
                && retNode is not null
                && retNode.ToJsonString().Trim('"').Equals("sorry", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyBasicAuth(HttpRequestMessage request, DeviceIdentity device)
    {
        var login = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);
    }

    /// <summary>
    /// Wraps <see cref="SendAsync"/> in a <see cref="ControlResult{T}"/> so callers
    /// can pattern-match on success/failure with structured error codes instead of
    /// checking for null. Use this in adapter public methods that return a result.
    /// </summary>
    protected async Task<ControlResult<HttpAdapterResponse>> SendWithResultAsync(
        DeviceIdentity device, string endpoint, string method, System.Text.Json.Nodes.JsonObject? payload,
        CancellationToken cancellationToken, string? mediaType = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await SendAsync(device, endpoint, method, payload, cancellationToken, mediaType);
            sw.Stop();
            if (response is null)
            {
                return ControlResult<HttpAdapterResponse>.Fail(
                    "no-response",
                    $"No HTTP response from {endpoint} on {device.DisplayName}.",
                    response?.StatusCode is null ? null : (int)response.StatusCode)
                    with { DurationMs = sw.ElapsedMilliseconds };
            }

            if (!IsSemanticSuccess(response))
            {
                return ControlResult<HttpAdapterResponse>.Fail(
                    "semantic-failure",
                    $"Semantic failure from {endpoint} on {device.DisplayName}: {response.RawContent}",
                    (int)response.StatusCode)
                    with { DurationMs = sw.ElapsedMilliseconds };
            }

            return ControlResult<HttpAdapterResponse>.Ok(
                response,
                $"{endpoint} succeeded on {device.DisplayName}.",
                (int)response.StatusCode)
                with { DurationMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ControlResult<HttpAdapterResponse>.FromException(ex, "request-exception")
                with { DurationMs = sw.ElapsedMilliseconds };
        }
    }

    private async Task<HttpAdapterResponse?> SendOnceAsync(
        DeviceIdentity device,
        Uri uri,
        string endpoint,
        string method,
        string? payloadRaw,
        string? mediaType,
        bool useBasicHeader,
        bool useCredentialCache,
        CancellationToken cancellationToken)
    {
        // Use pooled client from IHttpClientFactory. For Digest auth we still create
        // a handler per-call (Credentials are per-device), but the default path (Basic
        // auth via header) reuses the pooled handler from the factory.
        HttpClient client;
        if (useCredentialCache)
        {
            var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(
                    string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName,
                    device.Password ?? string.Empty),
                PreAuthenticate = false
            };
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Options.HttpTimeoutSeconds) };
        }
        else
        {
            client = httpClientFactory.CreateClient("default");
            client.Timeout = TimeSpan.FromSeconds(Options.HttpTimeoutSeconds);
        }

        using (client)
        using (var request = new HttpRequestMessage(new HttpMethod(method), uri))
        {
            if (useBasicHeader)
            {
                ApplyBasicAuth(request, device);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            if (payloadRaw is not null)
            {
                request.Content = new StringContent(payloadRaw, Encoding.UTF8, mediaType ?? "application/json");
            }

            // Summary trace at Information is always logged (adapter, device, endpoint, method, status).
            // Full payload and response bodies are gated behind Debug to avoid noise and sensitive data
            // leakage in production. Toggle via Microsoft.Extensions.Logging configuration.
            Logger.LogInformation(
                "HTTP request. adapter={Adapter} device={Device} ip={Ip} url={Url} endpoint={Endpoint} method={Method} auth={Auth}",
                GetType().Name,
                device.DisplayName,
                device.IpAddress,
                uri,
                endpoint,
                method,
                useBasicHeader ? "Basic" : (useCredentialCache ? "CredentialCache" : "None"));

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                var headerSummary = string.Join("; ", request.Headers.Select(static header => $"{header.Key}={string.Join(",", header.Value)}"));
                Logger.LogDebug(
                    "HTTP request payload. adapter={Adapter} headers={Headers} payload={Payload}",
                    GetType().Name,
                    headerSummary,
                    payloadRaw ?? string.Empty);
            }

            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                Logger.LogInformation(
                    "HTTP response. adapter={Adapter} device={Device} ip={Ip} url={Url} endpoint={Endpoint} method={Method} status={Status}",
                    GetType().Name,
                    device.DisplayName,
                    device.IpAddress,
                    uri,
                    endpoint,
                    method,
                    (int)response.StatusCode);

                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(
                        "HTTP response body. adapter={Adapter} status={Status} response={Response}",
                        GetType().Name,
                        (int)response.StatusCode,
                        raw);
                }
                return new HttpAdapterResponse(response.StatusCode, TryParseNode(raw), raw);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "HTTP call failed. adapter={Adapter} device={Device} ip={Ip} url={Url} endpoint={Endpoint} method={Method}", GetType().Name, device.DisplayName, device.IpAddress, uri, endpoint, method);
                return null;
            }
        }
    }
}

public sealed class LanDirectNetSdkRestAdapter(
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    IApplicationStore store,
    ILogger<LanDirectNetSdkRestAdapter> logger) : HttpControlAdapterBase(options, httpClientFactory, logger), IControlAdapter
{
    // Live-proven on 5523-W firmware 3.6.103.5721106 (singular /Network/interface/N, not /interfaces).
    private static readonly Dictionary<string, string[]> ReadEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Device"] = ["/NetSDK/System/deviceInfo", "/NetSDK/System/time/localTime", "/NetSDK/System/time/ntp"],
        ["Network"] =
        [
            "/NetSDK/Network/interface",
            "/NetSDK/Network/interface/1",
            "/NetSDK/Network/interface/4",
            "/NetSDK/Network/Dns",
            "/NetSDK/Network/Esee"
        ],
        ["Audio"] = ["/NetSDK/Audio/input/channels", "/NetSDK/Audio/input/channel/1", "/NetSDK/Audio/encode/channels", "/NetSDK/Audio/encode/channel/1"],
        ["Video"] =
        [
            "/NetSDK/Video/input/channels",
            "/NetSDK/Video/input/channel/1",
            "/NetSDK/Video/encode/channels",
            "/NetSDK/Video/encode/channel/101",
            "/NetSDK/Video/encode/channel/101/properties",
            "/NetSDK/Video/encode/channel/102",
            "/NetSDK/Video/encode/channel/102/properties",
            "/NetSDK/Video/encode/channel/101/channelNameOverlay",
            "/NetSDK/Video/encode/channel/101/datetimeOverlay",
            "/NetSDK/Video/encode/channel/101/snapShot"
        ],
        ["Detection"] = ["/NetSDK/Video/motionDetection/channels", "/NetSDK/Video/motionDetection/channel/1", "/NetSDK/IO/alarmInput/channels", "/NetSDK/IO/alarmInput/channel/1", "/NetSDK/IO/alarmOutput/channels", "/NetSDK/IO/alarmOutput/channel/1"],
        ["PTZ"] = ["/NetSDK/PTZ/channels"],
        ["Stream"] =
        [
            "/NetSDK/Video/encode/channels",
            "/NetSDK/Video/encode/channel/101",
            "/NetSDK/Video/encode/channel/102"
        ],
        ["Image"] =
        [
            "/NetSDK/Image",
            "/NetSDK/Image/irCutFilter",
            "/NetSDK/Image/manualSharpness",
            "/NetSDK/Image/denoise3d",
            "/NetSDK/Image/wdr",
            "/NetSDK/Factory?cmd=WhiteLightCtrl",
            "/NetSDK/Factory?cmd=InfraRedCtrl",
            "/NetSDK/Video/input/channel/1/privacyMask/1"
        ],
        ["Storage"] = ["/NetSDK/SDCard/status", "/NetSDK/SDCard/media/search", "/NetSDK/SDCard/media/playbackFLV", "/NetSDK/SDCard/format"]
    };

    public string Name => nameof(LanDirectNetSdkRestAdapter);
    public int Priority => 10;
    public TransportKind TransportKind => TransportKind.LanRest;

    public async Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var response = await SendAsync(device, "/NetSDK/System/deviceInfo", "GET", null, cancellationToken);
        return IsSemanticSuccess(response);
    }

    public async Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var manifests = await store.GetProtocolManifestsAsync(cancellationToken);
        var endpoints = manifests.Where(static manifest => manifest.Family?.Contains("NETSDK", StringComparison.OrdinalIgnoreCase) == true || manifest.ManifestId.Contains("endpoint_catalog", StringComparison.OrdinalIgnoreCase) || manifest.ManifestId.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static manifest => manifest.Endpoints)
            .Where(static endpoint => endpoint.Path.StartsWith("/NetSDK/", StringComparison.OrdinalIgnoreCase))
            .Select(static endpoint => endpoint.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deviceInfo = await SendAsync(device, "/NetSDK/System/deviceInfo", "GET", null, cancellationToken);

        return new CapabilityMap
        {
            DeviceId = device.Id,
            PrimaryControlAdapter = Name,
            ControlAdapters = [Name],
            VideoTransportKinds = [TransportKind.LanRest, TransportKind.Rtsp, TransportKind.RtspOverHttp, TransportKind.FlvOverHttp, TransportKind.Rtmp],
            SupportedSettingGroups = ReadEndpoints.Keys.ToList(),
            SupportedEndpointPaths = endpoints,
            SupportedMaintenanceOperations = [],
            Notes = new Dictionary<string, string>
            {
                ["deviceInfo"] = deviceInfo?.RawContent ?? string.Empty,
                ["probe"] = "Public NETSDK REST reachable."
            }
        };
    }

    public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
        => ReadAsync(device, cancellationToken);

    public async Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var groups = new List<SettingGroup>();
        foreach (var group in ReadEndpoints)
        {
            var responses = new Dictionary<string, HttpAdapterResponse?>();
            foreach (var endpoint in group.Value)
            {
                responses[endpoint] = await SendAsync(device, endpoint, "GET", null, cancellationToken);
            }
            groups.Add(BuildGroup(group.Key, responses));
        }

        return new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name, Groups = groups };
    }

    public async Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
    {
        var response = await SendAsync(device, plan.Endpoint, plan.Method, plan.Payload, cancellationToken);
        return new WriteResult
        {
            Success = IsSemanticSuccess(response),
            AdapterName = Name,
            StatusCode = response is null ? null : (int)response.StatusCode,
            Response = response?.Json,
            Message = response?.RawContent ?? "No HTTP response."
        };
    }

    public async Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
    {
        return operation switch
        {
            MaintenanceOperation.Reboot => await ExecuteMaintenanceSimpleAsync(device, "/NetSDK/System/operation/reboot", "PUT", payload, operation, cancellationToken),
            MaintenanceOperation.FactoryReset => await ExecuteMaintenanceSimpleAsync(device, "/NetSDK/System/operation/default", "PUT", payload, operation, cancellationToken),
            _ => new MaintenanceResult { Success = false, AdapterName = Name, Operation = operation, Message = "Maintenance operation is not mapped on the public NETSDK adapter." }
        };
    }

    private async Task<MaintenanceResult> ExecuteMaintenanceSimpleAsync(DeviceIdentity device, string endpoint, string method, JsonObject? payload, MaintenanceOperation operation, CancellationToken cancellationToken)
    {
        var response = await SendAsync(device, endpoint, method, payload, cancellationToken);
        return new MaintenanceResult
        {
            Success = IsSemanticSuccess(response),
            AdapterName = Name,
            Operation = operation,
            Response = response?.Json,
            Message = response?.RawContent ?? "No HTTP response."
        };
    }
}

public sealed class LanPrivateVendorHttpAdapter(
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    IApplicationStore store,
    ILogger<LanPrivateVendorHttpAdapter> logger) : HttpControlAdapterBase(options, httpClientFactory, logger), IControlAdapter
{
    public string Name => nameof(LanPrivateVendorHttpAdapter);
    public int Priority => 20;
    public TransportKind TransportKind => TransportKind.LanPrivateHttp;

    public async Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var response = await SendAsync(device, "/NetSDK/Image/irCutfilter", "GET", null, cancellationToken);
        return response is not null && response.StatusCode != HttpStatusCode.NotFound;
    }

    public async Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var manifests = await GetPrivateManifestsAsync(cancellationToken);
        return new CapabilityMap
        {
            DeviceId = device.Id,
            PrimaryControlAdapter = Name,
            ControlAdapters = [Name],
            VideoTransportKinds = [TransportKind.LanPrivateHttp, TransportKind.BubbleFlv],
            SupportedSettingGroups = manifests.SelectMany(static manifest => manifest.Endpoints).Select(static endpoint => endpoint.Tag).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SupportedEndpointPaths = manifests.SelectMany(static manifest => manifest.Endpoints).Select(static endpoint => endpoint.Path).ToList(),
            SupportedMaintenanceOperations = [MaintenanceOperation.Reboot.ToString(), MaintenanceOperation.FactoryReset.ToString(), MaintenanceOperation.FirmwareUpload.ToString(), MaintenanceOperation.PasswordReset.ToString(), MaintenanceOperation.RefreshUsers.ToString()],
            Notes = new Dictionary<string, string> { ["probe"] = "Private IPCamSuite HTTP/CGI surface assumed from vendor binaries." }
        };
    }

    public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
        => ReadAsync(device, cancellationToken);

    public async Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var manifests = await GetPrivateManifestsAsync(cancellationToken);
        var groups = new List<SettingGroup>();
        foreach (var group in manifests.SelectMany(static manifest => manifest.Endpoints).Where(static endpoint => endpoint.Methods.Contains("GET", StringComparer.OrdinalIgnoreCase)).GroupBy(static endpoint => endpoint.Tag, StringComparer.OrdinalIgnoreCase))
        {
            var responses = new Dictionary<string, HttpAdapterResponse?>();
            foreach (var endpoint in group)
            {
                responses[endpoint.Path] = await SendAsync(device, endpoint.Path, "GET", null, cancellationToken);
            }
            groups.Add(BuildGroup(group.Key, responses));
        }

        return new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name, Groups = groups };
    }

    public async Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
    {
        var contentType = plan.Endpoint.Contains(".xml", StringComparison.OrdinalIgnoreCase) ? "application/xml" : null;
        var response = await SendAsync(device, plan.Endpoint, plan.Method, plan.Payload, cancellationToken, contentType);
        return new WriteResult
        {
            Success = IsSemanticSuccess(response),
            AdapterName = Name,
            StatusCode = response is null ? null : (int)response.StatusCode,
            Response = response?.Json,
            Message = response?.RawContent ?? "No HTTP response."
        };
    }

    public async Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
    {
        return operation switch
        {
            MaintenanceOperation.Reboot => await ExecuteSimpleAsync(device, "/NetSDK/System/operation/reboot", "PUT", payload, operation, cancellationToken),
            MaintenanceOperation.FactoryReset => await ExecuteSimpleAsync(device, "/NetSDK/System/operation/default", "PUT", payload, operation, cancellationToken),
            MaintenanceOperation.RefreshUsers => await ExecuteSimpleAsync(device, "/user/user_list.xml", "GET", null, operation, cancellationToken),
            MaintenanceOperation.PasswordReset => await ExecuteSimpleAsync(device, "/user/user_reset", "POST", payload, operation, cancellationToken),
            MaintenanceOperation.FirmwareUpload => await ExecuteFirmwareUploadAsync(device, payload, cancellationToken),
            _ => new MaintenanceResult { Success = false, AdapterName = Name, Operation = operation, Message = "Unsupported maintenance operation." }
        };
    }

    private async Task<MaintenanceResult> ExecuteSimpleAsync(DeviceIdentity device, string endpoint, string method, JsonObject? payload, MaintenanceOperation operation, CancellationToken cancellationToken)
    {
        var response = await SendAsync(device, endpoint, method, payload, cancellationToken);
        return new MaintenanceResult
        {
            Success = IsSemanticSuccess(response),
            AdapterName = Name,
            Operation = operation,
            Response = response?.Json,
            Message = response?.RawContent ?? "No HTTP response."
        };
    }

    private async Task<MaintenanceResult> ExecuteFirmwareUploadAsync(DeviceIdentity device, JsonObject? payload, CancellationToken cancellationToken)
    {
        var filePath = payload?["filePath"]?.GetValue<string>();
        // Directory allow-list: never upload an arbitrary caller-supplied path to the camera.
        if (!FirmwarePathPolicy.IsAllowed(filePath, Options, out var reason))
        {
            return new MaintenanceResult { Success = false, AdapterName = Name, Operation = MaintenanceOperation.FirmwareUpload, Message = $"Firmware upload rejected: {reason}" };
        }

        // IsAllowed returning true guarantees filePath is non-null and exists; the compiler can't
        // see the correlation, so forgive the null here.
        using var content = new MultipartFormDataContent();
        var stream = File.OpenRead(filePath!);
        content.Add(new StreamContent(stream), "file", Path.GetFileName(filePath!));
        var upload = await SendMultipartAsync(device, "/cgi-bin/upload.cgi", content, cancellationToken);
        var progress = await SendAsync(device, "/cgi-bin/upgrade_rate.cgi?cmd=upgrade_rate", "GET", null, cancellationToken);
        return new MaintenanceResult
        {
            Success = IsSemanticSuccess(upload),
            AdapterName = Name,
            Operation = MaintenanceOperation.FirmwareUpload,
            Response = progress?.Json ?? upload?.Json,
            Message = progress?.RawContent ?? upload?.RawContent ?? "No HTTP response."
        };
    }

    private async Task<IReadOnlyCollection<ProtocolManifest>> GetPrivateManifestsAsync(CancellationToken cancellationToken)
        => (await store.GetProtocolManifestsAsync(cancellationToken)).Where(static manifest => manifest.ManifestId.Contains("ipcamsuite_private", StringComparison.OrdinalIgnoreCase)).ToList();
}
