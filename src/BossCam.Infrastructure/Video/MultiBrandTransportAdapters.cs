using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BossCam.Contracts;
using BossCam.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Video;

/// <summary>
/// Discovers high-resolution stream URLs for multi-brand cameras on the LAN.
/// Proven paths:
/// - Juan/GUANGZHOU 5523-W: rtsp://user:pass@ip:554/ch0_0.264 (main 2560x1920 HEVC), ch0_1.264 (sub)
/// - ONVIF PROFILE_000 / PROFILE_001 via port 8888 media service
/// - Dahua/Lorex: rtsp://.../cam/realmonitor?channel=1&amp;subtype=0 (main), subtype=1 (sub)
/// - WVC W5C / 631GA: ONVIF on :8899 + common RTSP candidates
/// </summary>
public sealed class MultiBrandHighResTransportAdapter(
    IOptions<BossCamRuntimeOptions> options,
    ILogger<MultiBrandHighResTransportAdapter> logger) : IVideoTransportAdapter
{
    public string Name => nameof(MultiBrandHighResTransportAdapter);
    public TransportKind TransportKind => TransportKind.Rtsp;
    public int Priority => 5; // ahead of generic StreamDescriptorAdapter

    public async Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return [];
        }

        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var auth = $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@";
        var sources = new List<VideoSourceDescriptor>();
        var brand = DetectBrand(device);

        // --- Juan / 5523-W high-res paths (live-proven HEVC 2560x1920) ---
        if (brand is CameraBrand.JuanNetSdk or CameraBrand.Unknown)
        {
            sources.Add(MainRtsp(device, auth, "/ch0_0.264", "Juan main HEVC (ch0_0.264)", rank: 0, "main", "2560x1920"));
            sources.Add(SubRtsp(device, auth, "/ch0_1.264", "Juan sub H264 (ch0_1.264)", rank: 50, "sub", "704x480"));
            // legacy path aliases
            sources.Add(MainRtsp(device, auth, "/11", "Juan RTSP /11 (alias)", rank: 3, "main", null));
            sources.Add(SubRtsp(device, auth, "/12", "Juan RTSP /12 (alias)", rank: 51, "sub", null));
        }

        // --- Dahua / Lorex / Amcrest ---
        if (brand is CameraBrand.DahuaLorex or CameraBrand.Unknown)
        {
            sources.Add(MainRtsp(device, auth, "/cam/realmonitor?channel=1&subtype=0", "Dahua/Lorex main", rank: 2, "main", null));
            sources.Add(SubRtsp(device, auth, "/cam/realmonitor?channel=1&subtype=1", "Dahua/Lorex sub", rank: 52, "sub", null));
        }

        // --- ONVIF discovery of stream URIs (high-res first) ---
        try
        {
            var onvifSources = await DiscoverOnvifStreamsAsync(device, user, password, cancellationToken);
            sources.AddRange(onvifSources);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ONVIF stream discovery failed for {Ip}", device.IpAddress);
        }

        // Prefer authenticated unique URLs, main rank lowest number wins.
        return sources
            .GroupBy(static s => s.Url, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.OrderBy(s => s.Rank).First())
            .OrderBy(static s => s.Rank)
            .ToList();
    }

    private async Task<IReadOnlyCollection<VideoSourceDescriptor>> DiscoverOnvifStreamsAsync(
        DeviceIdentity device,
        string user,
        string password,
        CancellationToken cancellationToken)
    {
        var results = new List<VideoSourceDescriptor>();
        var ports = new[] { 8888, 8899, device.Port <= 0 ? 80 : device.Port, 80 }
            .Distinct()
            .ToArray();

        foreach (var port in ports)
        {
            var mediaUrl = $"http://{device.IpAddress}:{port}/onvif/media";
            var altMedia = new[]
            {
                mediaUrl,
                $"http://{device.IpAddress}:{port}/onvif/media_service",
                $"http://{device.IpAddress}:{port}/onvif/Media"
            };

            foreach (var media in altMedia)
            {
                var profilesXml = await SoapAsync(media, """
                    <trt:GetProfiles xmlns:trt="http://www.onvif.org/ver10/media/wsdl"/>
                    """, user, password, cancellationToken);
                if (string.IsNullOrWhiteSpace(profilesXml) || profilesXml.Contains("Fault", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tokens = Regex.Matches(profilesXml, @"Profiles[^>]*token=""([^""]+)""", RegexOptions.IgnoreCase)
                    .Select(static m => m.Groups[1].Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (tokens.Count == 0)
                {
                    tokens = Regex.Matches(profilesXml, @"token=""([^""]+)""", RegexOptions.IgnoreCase)
                        .Select(static m => m.Groups[1].Value)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(4)
                        .ToList();
                }

                var resolutions = Regex.Matches(profilesXml, @"<(?:\w+:)?Width>(\d+)</(?:\w+:)?Width>\s*<(?:\w+:)?Height>(\d+)</(?:\w+:)?Height>")
                    .Select(static m => $"{m.Groups[1].Value}x{m.Groups[2].Value}")
                    .ToList();

                var index = 0;
                foreach (var token in tokens)
                {
                    var streamXml = await SoapAsync(media, $"""
                        <trt:GetStreamUri xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                          <trt:StreamSetup>
                            <tt:Stream>RTP-Unicast</tt:Stream>
                            <tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport>
                          </trt:StreamSetup>
                          <trt:ProfileToken>{token}</trt:ProfileToken>
                        </trt:GetStreamUri>
                        """, user, password, cancellationToken);
                    var uri = ExtractTag(streamXml, "Uri");
                    if (string.IsNullOrWhiteSpace(uri))
                    {
                        index++;
                        continue;
                    }

                    uri = InjectCredentials(uri, user, password);
                    var res = index < resolutions.Count ? resolutions[index] : null;
                    var isMain = index == 0 || (res is not null && IsHighRes(res));
                    results.Add(new VideoSourceDescriptor
                    {
                        Kind = TransportKind.OnvifRtsp,
                        Url = uri,
                        Rank = isMain ? 1 : 55 + index,
                        DisplayName = isMain
                            ? $"ONVIF main ({token}{(res is null ? "" : " " + res)})"
                            : $"ONVIF sub ({token}{(res is null ? "" : " " + res)})",
                        Metadata = new Dictionary<string, string>
                        {
                            ["stream"] = isMain ? "main" : "sub",
                            ["profileToken"] = token,
                            ["onvifMedia"] = media,
                            ["resolution"] = res ?? string.Empty,
                            ["highRes"] = isMain ? "true" : "false"
                        }
                    });

                    var snapXml = await SoapAsync(media, $"""
                        <trt:GetSnapshotUri xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
                          <trt:ProfileToken>{token}</trt:ProfileToken>
                        </trt:GetSnapshotUri>
                        """, user, password, cancellationToken);
                    var snap = ExtractTag(snapXml, "Uri");
                    if (!string.IsNullOrWhiteSpace(snap) && isMain)
                    {
                        results.Add(new VideoSourceDescriptor
                        {
                            Kind = TransportKind.LanRest,
                            Url = InjectCredentials(snap, user, password),
                            Rank = 20,
                            DisplayName = "ONVIF snapshot (main profile)",
                            Metadata = new Dictionary<string, string>
                            {
                                ["kind"] = "snapshot",
                                ["stream"] = "main",
                                ["highRes"] = "true",
                                ["profileToken"] = token
                            }
                        });
                    }

                    index++;
                }

                if (results.Count > 0)
                {
                    return results;
                }
            }
        }

        return results;
    }

    private async Task<string?> SoapAsync(string url, string bodyInner, string user, string password, CancellationToken cancellationToken)
    {
        var envelope = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>{bodyInner}</s:Body>
            </s:Envelope>
            """;
        try
        {
            using var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(user, password),
                PreAuthenticate = false
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(3, options.Value.HttpTimeoutSeconds)) };
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {token}");
            using var response = await client.SendAsync(request, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractTag(string? xml, string localName)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        var m = Regex.Match(xml, $@"<(?:\w+:)?{Regex.Escape(localName)}[^>]*>([^<]+)</(?:\w+:)?{Regex.Escape(localName)}>", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static bool IsHighRes(string resolution)
    {
        var parts = resolution.Split('x', 'X');
        return parts.Length == 2
            && int.TryParse(parts[0], out var w)
            && int.TryParse(parts[1], out var h)
            && (w >= 1280 || h >= 720);
    }

    private static string InjectCredentials(string url, string user, string password)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return url;
        }

        var builder = new UriBuilder(uri) { UserName = user, Password = password };
        return builder.Uri.ToString();
    }

    private static VideoSourceDescriptor MainRtsp(DeviceIdentity device, string auth, string path, string name, int rank, string stream, string? res)
        => new()
        {
            Kind = TransportKind.Rtsp,
            Url = $"rtsp://{auth}{device.IpAddress}:554{path}",
            Rank = rank,
            DisplayName = name,
            Metadata = new Dictionary<string, string>
            {
                ["stream"] = stream,
                ["path"] = path,
                ["highRes"] = stream == "main" ? "true" : "false",
                ["resolution"] = res ?? string.Empty,
                ["auth"] = "digest"
            }
        };

    private static VideoSourceDescriptor SubRtsp(DeviceIdentity device, string auth, string path, string name, int rank, string stream, string? res)
        => MainRtsp(device, auth, path, name, rank, stream, res);

    internal static CameraBrand DetectBrand(DeviceIdentity device)
    {
        var model = $"{device.HardwareModel} {device.Name} {device.DeviceType} {device.DeviceId}".ToLowerInvariant();
        if (model.Contains("5523") || model.Contains("juan") || model.Contains("guangzhou") || !string.IsNullOrWhiteSpace(device.EseeId))
        {
            return CameraBrand.JuanNetSdk;
        }

        if (model.Contains("lorex") || model.Contains("dahua") || model.Contains("amcrest") || model.Contains("flir"))
        {
            return CameraBrand.DahuaLorex;
        }

        if (model.Contains("wvc") || model.Contains("w5c") || model.Contains("631ga"))
        {
            return CameraBrand.WvcOnvif;
        }

        if (string.Equals(device.DeviceType, "ONVIF", StringComparison.OrdinalIgnoreCase))
        {
            return CameraBrand.GenericOnvif;
        }

        return CameraBrand.Unknown;
    }
}

public enum CameraBrand
{
    Unknown,
    JuanNetSdk,
    DahuaLorex,
    WvcOnvif,
    GenericOnvif
}

/// <summary>
/// Dahua/Lorex HTTP CGI control adapter (Digest). Settings map to configManager.cgi encode/main stream.
/// </summary>
public sealed class DahuaLorexControlAdapter(
    IOptions<BossCamRuntimeOptions> options,
    ILogger<DahuaLorexControlAdapter> logger) : BossCam.Infrastructure.Control.HttpControlAdapterBase(options, logger), IControlAdapter
{
    // options forwarded to HttpControlAdapterBase for timeout/config.
    public string Name => nameof(DahuaLorexControlAdapter);
    public int Priority => 25;
    public TransportKind TransportKind => TransportKind.LanPrivateHttp;

    public async Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return false;
        }

        _ = Options.HttpTimeoutSeconds; // ensure runtime options are bound on Linux hosts

        // Lorex web shell or magicBox endpoint presence.
        var response = await SendAsync(device, "/cgi-bin/magicBox.cgi?action=getDeviceType", "GET", null, cancellationToken);
        if (response is not null && (int)response.StatusCode is 200 or 401)
        {
            return true;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var html = await client.GetStringAsync($"http://{device.IpAddress}:{device.Port}/", cancellationToken);
            return html.Contains("flirLorex", StringComparison.OrdinalIgnoreCase)
                || html.Contains("WEB SERVICE", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return MultiBrandHighResTransportAdapter.DetectBrand(device) == CameraBrand.DahuaLorex;
        }
    }

    public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken)
        => Task.FromResult(new CapabilityMap
        {
            DeviceId = device.Id,
            PrimaryControlAdapter = Name,
            ControlAdapters = [Name],
            VideoTransportKinds = [TransportKind.Rtsp, TransportKind.LanPrivateHttp],
            SupportedSettingGroups = ["Device", "Video", "Image", "Network"],
            SupportedEndpointPaths =
            [
                "/cgi-bin/magicBox.cgi?action=getSystemInfo",
                "/cgi-bin/configManager.cgi?action=getConfig&name=Encode",
                "/cgi-bin/configManager.cgi?action=getConfig&name=VideoColor",
                "/cgi-bin/snapshot.cgi"
            ],
            SupportedMaintenanceOperations = [MaintenanceOperation.Reboot.ToString()],
            Notes = new Dictionary<string, string>
            {
                ["brand"] = "Dahua/Lorex CGI",
                ["highResEncode"] = "Encode[0].MainFormat (subtype=0)",
                ["auth"] = "HTTP Digest"
            }
        });

    public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
        => ReadAsync(device, cancellationToken);

    public async Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var endpoints = new Dictionary<string, string>
        {
            ["Device"] = "/cgi-bin/magicBox.cgi?action=getSystemInfo",
            ["EncodeMain"] = "/cgi-bin/configManager.cgi?action=getConfig&name=Encode",
            ["VideoColor"] = "/cgi-bin/configManager.cgi?action=getConfig&name=VideoColor",
            ["Network"] = "/cgi-bin/configManager.cgi?action=getConfig&name=Network"
        };

        var groups = new List<SettingGroup>();
        var responses = new Dictionary<string, BossCam.Infrastructure.Control.HttpAdapterResponse?>();
        foreach (var pair in endpoints)
        {
            responses[pair.Key] = await SendAsync(device, pair.Value, "GET", null, cancellationToken);
        }

        groups.Add(BuildGroup("DahuaLorex", responses));
        return new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name, Groups = groups };
    }

    public async Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
    {
        // Dahua setConfig uses query-string style: action=setConfig&Encode[0].MainFormat[0].Video.FPS=15
        var endpoint = plan.Endpoint;
        if (!endpoint.Contains("setConfig", StringComparison.OrdinalIgnoreCase)
            && plan.Payload is not null)
        {
            // Prefer main stream (channel 0 MainFormat) for high-res durable encode settings.
            endpoint = "/cgi-bin/configManager.cgi?action=setConfig";
        }

        var response = await SendAsync(device, endpoint, plan.Method, plan.Payload, cancellationToken);
        return new WriteResult
        {
            Success = IsSemanticSuccess(response) || (response?.RawContent?.Contains("OK", StringComparison.OrdinalIgnoreCase) ?? false),
            AdapterName = Name,
            StatusCode = response is null ? null : (int)response.StatusCode,
            Response = response?.Json,
            Message = response?.RawContent ?? "No HTTP response."
        };
    }

    public async Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, System.Text.Json.Nodes.JsonObject? payload, CancellationToken cancellationToken)
    {
        if (operation == MaintenanceOperation.Reboot)
        {
            var response = await SendAsync(device, "/cgi-bin/magicBox.cgi?action=reboot", "GET", null, cancellationToken);
            return new MaintenanceResult
            {
                Success = response is not null,
                AdapterName = Name,
                Operation = operation,
                Message = response?.RawContent ?? "No response"
            };
        }

        return new MaintenanceResult { Success = false, AdapterName = Name, Operation = operation, Message = "Unsupported on Dahua/Lorex adapter." };
    }
}

/// <summary>
/// Lightweight ONVIF imaging/device adapter for WVC W5C and other ONVIF-only brands.
/// </summary>
public sealed class OnvifImagingControlAdapter(
    IOptions<BossCamRuntimeOptions> options,
    ILogger<OnvifImagingControlAdapter> logger) : IControlAdapter
{
    public string Name => nameof(OnvifImagingControlAdapter);
    public int Priority => 35;
    public TransportKind TransportKind => TransportKind.OnvifRtsp;

    public async Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return false;
        }

        foreach (var port in new[] { 8899, 8888, device.Port <= 0 ? 80 : device.Port })
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                var xml = await PostSoapAsync(client, $"http://{device.IpAddress}:{port}/onvif/device_service",
                    """<tds:GetDeviceInformation xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>""",
                    device, cts.Token);
                if (xml?.Contains("Manufacturer", StringComparison.OrdinalIgnoreCase) == true)
                {
                    logger.LogDebug("ONVIF device service reachable on {Ip}:{Port}", device.IpAddress, port);
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "ONVIF probe miss {Ip}:{Port}", device.IpAddress, port);
            }
        }

        return MultiBrandHighResTransportAdapter.DetectBrand(device) is CameraBrand.WvcOnvif or CameraBrand.GenericOnvif;
    }

    public async Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        string? manufacturer = null, model = null, firmware = null, serial = null;
        foreach (var port in new[] { 8899, 8888, 80 })
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                var xml = await PostSoapAsync(client, $"http://{device.IpAddress}:{port}/onvif/device_service",
                    """<tds:GetDeviceInformation xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>""",
                    device, cancellationToken);
                if (xml is null)
                {
                    continue;
                }

                manufacturer = Extract(xml, "Manufacturer") ?? manufacturer;
                model = Extract(xml, "Model") ?? model;
                firmware = Extract(xml, "FirmwareVersion") ?? firmware;
                serial = Extract(xml, "SerialNumber") ?? serial;
                if (manufacturer is not null)
                {
                    break;
                }
            }
            catch
            {
            }
        }

        return new CapabilityMap
        {
            DeviceId = device.Id,
            PrimaryControlAdapter = Name,
            ControlAdapters = [Name],
            VideoTransportKinds = [TransportKind.OnvifRtsp, TransportKind.Rtsp],
            SupportedSettingGroups = ["Device", "Image"],
            SupportedEndpointPaths = ["/onvif/device_service", "/onvif/image_service", "/onvif/media"],
            SupportedMaintenanceOperations = [MaintenanceOperation.Reboot.ToString()],
            Notes = new Dictionary<string, string>
            {
                ["manufacturer"] = manufacturer ?? string.Empty,
                ["model"] = model ?? string.Empty,
                ["firmware"] = firmware ?? string.Empty,
                ["serial"] = serial ?? string.Empty,
                ["brand"] = "ONVIF"
            }
        };
    }

    public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
        => ReadAsync(device, cancellationToken);

    public async Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var map = await ProbeAsync(device, cancellationToken);
        var payload = new System.Text.Json.Nodes.JsonObject();
        foreach (var note in map.Notes)
        {
            payload[note.Key] = note.Value;
        }

        return new SettingsSnapshot
        {
            DeviceId = device.Id,
            AdapterName = Name,
            Groups =
            [
                new SettingGroup
                {
                    Name = "Device",
                    DisplayName = "ONVIF Device",
                    RawPayload = payload,
                    Values = map.Notes.ToDictionary(
                        static pair => pair.Key,
                        static pair => new SettingValue { Key = pair.Key, DisplayName = pair.Key, Value = pair.Value, ValueKind = SettingValueKind.String },
                        StringComparer.OrdinalIgnoreCase)
                }
            ]
        };
    }

    public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
        => Task.FromResult(new WriteResult
        {
            Success = false,
            AdapterName = Name,
            Message = "ONVIF imaging write mapping is brand-specific; use registered credentials and imaging service SetImagingSettings when authorized."
        });

    public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, System.Text.Json.Nodes.JsonObject? payload, CancellationToken cancellationToken)
        => Task.FromResult(new MaintenanceResult
        {
            Success = false,
            AdapterName = Name,
            Operation = operation,
            Message = "Use brand-specific reboot or ONVIF SystemReboot when credentials authorize it."
        });

    private static async Task<string?> PostSoapAsync(HttpClient client, string url, string bodyInner, DeviceIdentity device, CancellationToken cancellationToken)
    {
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var envelope = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>{bodyInner}</s:Body></s:Envelope>
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {token}");
        using var response = await client.SendAsync(request, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string? Extract(string xml, string tag)
    {
        var m = Regex.Match(xml, $@"<(?:\w+:)?{Regex.Escape(tag)}[^>]*>([^<]*)</(?:\w+:)?{Regex.Escape(tag)}>", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
