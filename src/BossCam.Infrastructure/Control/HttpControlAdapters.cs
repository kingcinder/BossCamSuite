using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Control;

public sealed record HttpAdapterResponse(HttpStatusCode StatusCode, JsonNode? Json, string RawContent);

public abstract class HttpControlAdapterBase(IOptions<BossCamRuntimeOptions> options, ILogger logger)
{
    protected BossCamRuntimeOptions Options => options.Value;
    protected ILogger Logger => logger;

    protected Uri BuildDeviceUri(DeviceIdentity device, string endpoint)
    {
        var cleaned = endpoint.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("/ID", "/0", StringComparison.OrdinalIgnoreCase);
        return new Uri($"http://{device.IpAddress}:{device.Port}{cleaned}", UriKind.Absolute);
    }

    protected async Task<HttpAdapterResponse?> SendAsync(DeviceIdentity device, string endpoint, string method, JsonObject? payload, CancellationToken cancellationToken, string? mediaType = null)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return null;
        }

        using var handler = new HttpClientHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Options.HttpTimeoutSeconds) };
        using var request = new HttpRequestMessage(new HttpMethod(method), BuildDeviceUri(device, endpoint));
        ApplyBasicAuth(request, device);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        if (payload is not null)
        {
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, mediaType ?? "application/json");
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            return new HttpAdapterResponse(response.StatusCode, TryParseNode(raw), raw);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "HTTP call {Method} {Endpoint} failed for {Device}", method, endpoint, device.DisplayName);
            return null;
        }
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
}

public sealed class LanDirectNetSdkRestAdapter(
    IOptions<BossCamRuntimeOptions> options,
    IApplicationStore store,
    ILogger<LanDirectNetSdkRestAdapter> logger) : HttpControlAdapterBase(options, logger), IControlAdapter
{
    private static readonly Dictionary<string, string[]> ReadEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Device"] = ["/NetSDK/System/deviceInfo", "/NetSDK/System/time/localTime", "/NetSDK/System/time/ntp"],
        ["Network"] = ["/NetSDK/Network/interfaces", "/NetSDK/Network/Ports", "/NetSDK/Network/Dns", "/NetSDK/Network/Esee"],
        ["Audio"] = ["/NetSDK/Audio/input/channels", "/NetSDK/Audio/encode/channels"],
        ["Video"] = ["/NetSDK/Video/input/channel/1", "/NetSDK/Video/encode/channels", "/NetSDK/Video/encode/channel/101/properties", "/NetSDK/Video/encode/channel/102/properties"],
        ["Detection"] = ["/NetSDK/Video/motionDetection/channels", "/NetSDK/IO/alarmInput/channels", "/NetSDK/IO/alarmOutput/channels"],
        ["PTZ"] = ["/NetSDK/PTZ/channels"],
        ["Stream"] = ["/NetSDK/Stream/channles", "/NetSDK/Stream/channel/ID"],
        ["Storage"] = ["/NetSDK/SDCard/status", "/NetSDK/SDCard/media/search"],
        ["Image"] = ["/NetSDK/Image", "/NetSDK/Image/irCutFilter", "/NetSDK/Image/manualSharpness", "/NetSDK/Image/denoise3d", "/NetSDK/Image/wdr", "/NetSDK/Image/AF"]
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

    public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
        => Task.FromResult(new MaintenanceResult { Success = false, AdapterName = Name, Operation = operation, Message = "Maintenance operation is not mapped on the public NETSDK adapter." });
}

public sealed class LanPrivateVendorHttpAdapter(
    IOptions<BossCamRuntimeOptions> options,
    IApplicationStore store,
    ILogger<LanPrivateVendorHttpAdapter> logger) : HttpControlAdapterBase(options, logger), IControlAdapter
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
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new MaintenanceResult { Success = false, AdapterName = Name, Operation = MaintenanceOperation.FirmwareUpload, Message = "Payload must contain an existing filePath." };
        }

        using var content = new MultipartFormDataContent();
        var stream = File.OpenRead(filePath);
        content.Add(new StreamContent(stream), "file", Path.GetFileName(filePath));
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
