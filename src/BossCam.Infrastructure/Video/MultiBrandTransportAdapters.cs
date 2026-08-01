using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
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
// CS9113: 'httpClientFactory' stored for future use in SoapAsync (Digest auth requires per-call handler).
#pragma warning disable CS9113
public sealed class MultiBrandHighResTransportAdapter(
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<MultiBrandHighResTransportAdapter> logger) : IVideoTransportAdapter
#pragma warning restore CS9113
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

        // --- Generic / Wansview / Netview / Temu fallback paths --------------------------
        // Probe-playable guesses, NOT assumptions: they rank below the brand-proven paths and the
        // ONVIF GetStreamUri results (DiscoverOnvifStreamsAsync runs for every brand, so a Wansview
        // or Netview prefers its authoritative media URI when it answers), and the live/record path
        // probes reachability before use. Emitted only for non-Juan brands — never for a model that
        // looks like a 5523-W, whose Juan paths are live-proven and should stay canonical.
        if (brand is not CameraBrand.JuanNetSdk
            && !(device.HardwareModel ?? string.Empty).Contains("5523", StringComparison.OrdinalIgnoreCase))
        {
            var rtspPort = device.RtspPort is > 0 ? device.RtspPort.Value : 554;
            (string Path, bool Sub, int Rank)[] generic =
            [
                ("/stream1", false, 20),
                ("/live", false, 21),
                ("/h264", false, 22),
                ("/videoMain", false, 23),
                ("/cam/realmonitor?channel=1&subtype=0", false, 24),
                ("/ch0_0.264", false, 25),
                ("/main", false, 26),
                ("/live/ch0", false, 27),
                ("/stream2", true, 60),
                ("/sub", true, 61)
            ];
            foreach (var (path, sub, rank) in generic)
            {
                var stream = sub ? "sub" : "main";
                sources.Add(new VideoSourceDescriptor
                {
                    Kind = TransportKind.Rtsp,
                    Url = $"rtsp://{auth}{device.IpAddress}:{rtspPort}{path}",
                    Rank = rank,
                    DisplayName = $"Generic {stream} {path}",
                    Metadata = new Dictionary<string, string>
                    {
                        ["stream"] = stream,
                        ["path"] = path,
                        ["highRes"] = sub ? "false" : "true",
                        ["resolution"] = string.Empty,
                        ["auth"] = "digest",
                        ["generic"] = "true"
                    }
                });
            }
        }

        // --- ONVIF discovery of stream URIs (high-res first) ---
        // DiscoverOnvifStreamsAsync already swallows per-port failure internally via SoapAsync
        // (which uses ProbeExceptionSwallow underneath), so no outer wrap is needed here. Wrapping
        // again would have introduced CS8619 (nullable mismatch with the non-nullable
        // IReadOnlyCollection<...> return) and added a redundant log line per call.
        var onvifSources = await DiscoverOnvifStreamsAsync(device, user, password, cancellationToken);
        sources.AddRange(onvifSources);

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
        // Canonical ONVIF probe ports, ordered WVC (8899) → Dahua media (8888) → OEM HTTP (80),
        // with device.Port appended as a brand-specific tail when non-zero. Tuple form
        // avoids an extra allocation per Distinct.
        var devicePort = device.Port > 0 ? device.Port : 0;
        var ports = options.Value.OnvifProbePorts
            .Append(devicePort)
            .Where(p => p > 0)
            .Distinct()
            .ToArray();

        // Media-service URL candidates. Consume the discovered WS-Discovery XAddr first — it is
        // the authoritative device-service URL and compliant devices may expose Media at an
        // arbitrary path/port on that host. Brand-guessed per-port paths remain the fast-path
        // fallback for known brands.
        var mediaCandidates = new List<string>();
        if (device.Metadata.TryGetValue("xaddrs", out var xaddr)
            && Uri.TryCreate(xaddr, UriKind.Absolute, out var parsedXaddr)
            && !string.IsNullOrWhiteSpace(parsedXaddr.Host))
        {
            var baseUrl = $"{parsedXaddr.Scheme}://{parsedXaddr.Host}:{parsedXaddr.Port}";
            mediaCandidates.AddRange(new[]
            {
                $"{baseUrl}/onvif/media",
                $"{baseUrl}/onvif/media_service",
                $"{baseUrl}/onvif/Media"
            });
        }

        foreach (var port in ports)
        {
            mediaCandidates.AddRange(new[]
            {
                $"http://{device.IpAddress}:{port}/onvif/media",
                $"http://{device.IpAddress}:{port}/onvif/media_service",
                $"http://{device.IpAddress}:{port}/onvif/Media"
            });
        }

        foreach (var media in mediaCandidates)
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

        return results;
    }

    private Task<string?> SoapAsync(string url, string bodyInner, string user, string password, CancellationToken cancellationToken)
    {
        var envelope = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Header>{OnvifWsse.BuildSecurityHeader(user, password)}</s:Header>
              <s:Body>{bodyInner}</s:Body>
            </s:Envelope>
            """;
        return ProbeExceptionSwallow.RunAsync(
            async () =>
            {
                var handler = new HttpClientHandler
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
                if (!response.IsSuccessStatusCode)
                {
                    // SOAP faults are conventionally HTTP 500, but a 401/403 (bad credentials)
                    // arrives as an HTML/plain error page with no literal "Fault". Checking the
                    // status makes auth failures diagnosable instead of silently "no profiles".
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cancellationToken);
            },
            logger,
            $"SOAP POST {url}");
    }

    // Internal for unit tests (InternalsVisibleTo): pins the &amp; → & decode behavior that the
    // old regex-based parser broke for Dahua/Hikvision-style GetStreamUri query strings.
    internal static string? ExtractTag(string? xml, string localName)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        // XDocument (not regex): XML-escaped entities decode correctly. Dahua/Hikvision-style
        // GetStreamUri responses legally escape '&' as "&amp;" in query strings; a regex would
        // store the literal "&amp;" into the RTSP URL and break the parameter separator.
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
                ?.Value
                .Trim();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
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
// CS9107: primary-constructor parameter 'options' is captured into a hidden field for this
// derived class AND passed to HttpControlAdapterBase, which stores its own copy. The two
// references are the same IOptions<BossCamRuntimeOptions> instance, so there is no behavior
// cost to the duplication.
// CS9113: 'httpClientFactory' is passed to the base class but not directly read in this body.
#pragma warning disable CS9107, CS9113
public sealed class DahuaLorexControlAdapter(
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<DahuaLorexControlAdapter> logger) : BossCam.Infrastructure.Control.HttpControlAdapterBase(options, httpClientFactory, logger), IControlAdapter
{
#pragma warning restore CS9107, CS9113
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

        // Lorex web shell or magicBox endpoint presence.
        var response = await SendAsync(device, "/cgi-bin/magicBox.cgi?action=getDeviceType", "GET", null, cancellationToken);
        if (response is not null && (int)response.StatusCode is 200 or 401)
        {
            return true;
        }

        try
        {
            using var client = httpClientFactory.CreateClient("probe");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(2, options.Value.HttpTimeoutSeconds / 2));
            var html = await client.GetStringAsync($"http://{device.IpAddress}:{device.Port}/", cancellationToken);
            return html.Contains("flirLorex", StringComparison.OrdinalIgnoreCase)
                || html.Contains("WEB SERVICE", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Web-shell probe failure is an expected probe miss on non-Lorex cameras; Debug so
            // the brand-detection fallback is traceable without spamming at Warning.
            logger.LogDebug(ex, "Lorex web-shell probe failed for {Device} ({Ip}); falling back to brand detection", device.DisplayName, device.IpAddress);
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
/// ONVIF imaging/device adapter for WVC W5C and other ONVIF-only brands. Performs real reads
/// (GetDeviceInformation / GetProfiles / GetImagingSettings) and real writes (SetImagingSettings
/// with a per-field <c>tt:</c> mapping). A write whose field has no mapping fails loudly instead
/// of silently writing an unrelated setting. Consumes the discovered WS-Discovery XAddr first.
/// </summary>
public sealed class OnvifImagingControlAdapter(
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<OnvifImagingControlAdapter> logger) : IControlAdapter
{
    public string Name => nameof(OnvifImagingControlAdapter);
    public int Priority => 35;
    public TransportKind TransportKind => TransportKind.OnvifRtsp;

    private const string GetDeviceInformationBody = """<tds:GetDeviceInformation xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>""";
    private const string GetProfilesBody = """<trt:GetProfiles xmlns:trt="http://www.onvif.org/ver10/media/wsdl"/>""";

    public async Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return false;
        }

        // Fast brand-check across multiple candidate device-service URLs (discovered XAddr first,
        // then per-port guesses) — tighten to half of HttpTimeoutSeconds so the worst-case scan
        // stays bounded even at slow LANs.
        // NOTE: Do NOT 'tidy' the /2 divisor into a uniform per-class timeout; the tight bound is
        // intentional for this multi-port brand-probing scan (pinned by OnvifImagingControlAdapterTimeoutTests).
        var probeTimeout = TimeSpan.FromSeconds(Math.Max(2, options.Value.HttpTimeoutSeconds / 2));
        foreach (var deviceUrl in BuildDeviceServiceCandidates(device, appendDevicePort: true))
        {
            using var client = httpClientFactory.CreateClient("onvif");
            client.Timeout = probeTimeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(probeTimeout);
            var xml = await ProbeExceptionSwallow.RunAsync(
                () => PostSoapAsync(client, deviceUrl, GetDeviceInformationBody, device, cts.Token),
                logger,
                $"ONVIF probe miss {deviceUrl}");
            if (xml?.Contains("Manufacturer", StringComparison.OrdinalIgnoreCase) == true)
            {
                logger.LogDebug("ONVIF device service reachable on {Url}", deviceUrl);
                return true;
            }
        }

        return MultiBrandHighResTransportAdapter.DetectBrand(device) is CameraBrand.WvcOnvif or CameraBrand.GenericOnvif;
    }

    public async Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        string? manufacturer = null, model = null, firmware = null, serial = null;
        foreach (var deviceUrl in BuildDeviceServiceCandidates(device, appendDevicePort: false))
        {
            // Per-candidate ONVIF GetDeviceInformation query — use the full HttpTimeoutSeconds so
            // each attempt is properly patient (pinned by OnvifImagingControlAdapterTimeoutTests).
            using var client = httpClientFactory.CreateClient("onvif");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(2, options.Value.HttpTimeoutSeconds));
            var xml = await ProbeExceptionSwallow.RunAsync(
                () => PostSoapAsync(client, deviceUrl, GetDeviceInformationBody, device, cancellationToken),
                logger,
                $"ONVIF device-info probe miss {deviceUrl}");
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

        return new CapabilityMap
        {
            DeviceId = device.Id,
            PrimaryControlAdapter = Name,
            ControlAdapters = [Name],
            VideoTransportKinds = [TransportKind.OnvifRtsp, TransportKind.Rtsp],
            SupportedSettingGroups = ["Device", "Image", "Video"],
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
        var probeTimeout = TimeSpan.FromSeconds(Math.Max(2, options.Value.HttpTimeoutSeconds));
        var endpoints = await ResolveEndpointsAsync(device, probeTimeout, cancellationToken);
        var groups = new List<SettingGroup>();

        if (endpoints is not null)
        {
            // Device identity (GetDeviceInformation).
            var deviceInfoXml = await FirstSuccessfulSoapAsync([endpoints.DeviceService], GetDeviceInformationBody, device, probeTimeout, cancellationToken);
            if (BuildDeviceGroup(deviceInfoXml) is { } deviceGroup)
            {
                groups.Add(deviceGroup);
            }

            // Media profiles: profile tokens, resolution, frame rate.
            var profilesXml = await FirstSuccessfulSoapAsync(BuildMediaUrls(endpoints.BaseUrl), GetProfilesBody, device, probeTimeout, cancellationToken);
            if (BuildVideoGroup(profilesXml) is { } videoGroup)
            {
                groups.Add(videoGroup);
            }

            // Imaging settings: the actual toggle fields.
            var sourceToken = ResolveVideoSourceToken(profilesXml);
            if (sourceToken is not null)
            {
                var imagingXml = await FirstSuccessfulSoapAsync(BuildImagingUrls(endpoints.BaseUrl), GetImagingSettingsBody(sourceToken), device, probeTimeout, cancellationToken);
                if (BuildImageGroup(imagingXml) is { } imageGroup)
                {
                    groups.Add(imageGroup);
                }
            }
        }

        return new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name, Groups = groups };
    }

    public async Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
    {
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;

        // Resolve the requested field to a concrete ONVIF SOAP element BEFORE touching the network:
        // an unmapped field fails loudly instead of silently writing an unrelated setting (the old
        // stub always sent Exposure.Mode=MANUAL regardless of plan.Payload).
        var fieldKey = ResolveFieldKey(plan);
        var fieldValue = ExtractFieldValue(plan.Payload, fieldKey);
        var imagingBody = BuildImagingSettingsElement(fieldKey, fieldValue);
        if (string.IsNullOrWhiteSpace(imagingBody))
        {
            return new WriteResult
            {
                Success = false,
                AdapterName = Name,
                Message = $"ONVIF field '{fieldKey}' has no mapped SetImagingSettings element; refusing to write an unrelated setting."
            };
        }

        var probeTimeout = TimeSpan.FromSeconds(Math.Max(2, options.Value.HttpTimeoutSeconds));
        var endpoints = await ResolveEndpointsAsync(device, probeTimeout, cancellationToken);
        if (endpoints is null)
        {
            return new WriteResult
            {
                Success = false,
                AdapterName = Name,
                Message = "ONVIF device service unreachable on all probed XAddrs/ports."
            };
        }

        // SetImagingSettings requires a real VideoSourceToken.
        var profilesXml = await FirstSuccessfulSoapAsync(BuildMediaUrls(endpoints.BaseUrl), GetProfilesBody, device, probeTimeout, cancellationToken);
        var sourceToken = ResolveVideoSourceToken(profilesXml);
        if (sourceToken is null)
        {
            return new WriteResult
            {
                Success = false,
                AdapterName = Name,
                Message = "Could not resolve an ONVIF VideoSourceToken (GetProfiles returned none)."
            };
        }

        foreach (var imagingUrl in BuildImagingUrls(endpoints.BaseUrl))
        {
            var envelope = BuildSetImagingSettingsEnvelope(sourceToken, imagingBody, OnvifWsse.BuildSecurityHeader(user, password));
            var result = await ProbeExceptionSwallow.RunAsync(
                async () =>
                {
                    using var client = httpClientFactory.CreateClient("onvif");
                    client.Timeout = probeTimeout;
                    using var request = new HttpRequestMessage(HttpMethod.Post, imagingUrl);
                    request.Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
                    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
                    request.Headers.TryAddWithoutValidation("Authorization", $"Basic {token}");
                    using var response = await client.SendAsync(request, cancellationToken);
                    var xml = await response.Content.ReadAsStringAsync(cancellationToken);
                    var success = response.IsSuccessStatusCode && !xml.Contains("Fault", StringComparison.OrdinalIgnoreCase);
                    return new WriteResult
                    {
                        Success = success,
                        AdapterName = Name,
                        StatusCode = (int)response.StatusCode,
                        Message = success ? $"ONVIF SetImagingSettings executed for '{fieldKey}'." : $"ONVIF SetImagingSettings failed: {xml}",
                        Response = System.Text.Json.Nodes.JsonValue.Create(xml)
                    };
                },
                logger,
                $"ONVIF SetImagingSettings {imagingUrl}");

            if (result is not null)
            {
                return result;
            }
        }

        return new WriteResult
        {
            Success = false,
            AdapterName = Name,
            Message = "ONVIF imaging write failed on all URLs; use registered credentials and imaging service."
        };
    }

    public async Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, System.Text.Json.Nodes.JsonObject? payload, CancellationToken cancellationToken)
    {
        // ONVIF SystemReboot via device service on first reachable candidate.
        if (operation == MaintenanceOperation.Reboot)
        {
            var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
            var password = device.Password ?? string.Empty;
            foreach (var deviceUrl in BuildDeviceServiceCandidates(device, appendDevicePort: false))
            {
                var result = await ProbeExceptionSwallow.RunAsync(
                    async () =>
                    {
                        var envelope = $"""
                            <?xml version="1.0" encoding="UTF-8"?>
                            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
                              <s:Header>{OnvifWsse.BuildSecurityHeader(user, password)}</s:Header>
                              <s:Body>
                                <tds:SystemReboot xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
                              </s:Body>
                            </s:Envelope>
                            """;
                        using var client = httpClientFactory.CreateClient("onvif");
                        client.Timeout = TimeSpan.FromSeconds(Math.Max(2, options.Value.HttpTimeoutSeconds));
                        using var request = new HttpRequestMessage(HttpMethod.Post, deviceUrl);
                        request.Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
                        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
                        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {token}");
                        using var response = await client.SendAsync(request, cancellationToken);
                        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
                        return new MaintenanceResult
                        {
                            Success = response.IsSuccessStatusCode && !xml.Contains("Fault", StringComparison.OrdinalIgnoreCase),
                            AdapterName = Name,
                            Operation = operation,
                            Message = response.IsSuccessStatusCode ? "ONVIF SystemReboot accepted." : $"ONVIF reboot failed: {xml}"
                        };
                    },
                    logger,
                    $"ONVIF SystemReboot {deviceUrl}");

                if (result is not null && result.Success)
                {
                    return result;
                }
            }
        }

        return new MaintenanceResult
        {
            Success = false,
            AdapterName = Name,
            Operation = operation,
            Message = "Use brand-specific reboot or ONVIF SystemReboot when credentials authorize it."
        };
    }

    // ── endpoint resolution ─────────────────────────────────────────

    /// <summary>Resolved ONVIF device-service URL and its base (scheme://host:port).</summary>
    private sealed record ResolvedEndpoints(string DeviceService, string BaseUrl);

    private async Task<ResolvedEndpoints?> ResolveEndpointsAsync(DeviceIdentity device, TimeSpan probeTimeout, CancellationToken cancellationToken)
    {
        foreach (var deviceUrl in BuildDeviceServiceCandidates(device, appendDevicePort: true))
        {
            using var client = httpClientFactory.CreateClient("onvif");
            client.Timeout = probeTimeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(probeTimeout);
            var xml = await ProbeExceptionSwallow.RunAsync(
                () => PostSoapAsync(client, deviceUrl, GetDeviceInformationBody, device, cts.Token),
                logger,
                $"ONVIF service-resolution probe miss {deviceUrl}");
            if (xml?.Contains("Manufacturer", StringComparison.OrdinalIgnoreCase) == true)
            {
                var baseUrl = Uri.TryCreate(deviceUrl, UriKind.Absolute, out var uri)
                    ? $"{uri.Scheme}://{uri.Host}:{uri.Port}"
                    : $"http://{device.IpAddress}:{device.Port}";
                return new ResolvedEndpoints(deviceUrl, baseUrl);
            }
        }

        return null;
    }

    /// <summary>
    /// Device-service URL candidates: the discovered WS-Discovery XAddr first (authoritative), then
    /// brand-guessed per-port <c>/onvif/device_service</c> paths. XAddr consumption means a device
    /// exposing services at a non-standard path/port is reached without port guessing.
    /// Internal for unit tests (InternalsVisibleTo): pins the XAddr-first candidate ordering.
    /// </summary>
    internal IReadOnlyList<string> BuildDeviceServiceCandidates(DeviceIdentity device, bool appendDevicePort)
    {
        var urls = new List<string>();
        if (device.Metadata.TryGetValue("xaddrs", out var xaddr)
            && Uri.TryCreate(xaddr, UriKind.Absolute, out var parsed)
            && !string.IsNullOrWhiteSpace(parsed.Host))
        {
            urls.Add(xaddr);
        }

        var ports = options.Value.OnvifProbePorts
            .Concat(appendDevicePort && device.Port > 0 ? new[] { device.Port } : [])
            .Where(p => p > 0)
            .Distinct()
            .ToArray();
        foreach (var port in ports)
        {
            urls.Add($"http://{device.IpAddress}:{port}/onvif/device_service");
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> BuildMediaUrls(string baseUrl)
        => new[] { "/onvif/media", "/onvif/media_service", "/onvif/Media" }
            .Select(path => baseUrl + path)
            .ToList();

    private static IReadOnlyList<string> BuildImagingUrls(string baseUrl)
        => new[] { "/onvif/image_service", "/onvif/imaging_service" }
            .Select(path => baseUrl + path)
            .ToList();

    private static string GetImagingSettingsBody(string sourceToken) => $"""
        <img:GetImagingSettings xmlns:img="http://www.onvif.org/ver20/imaging/wsdl">
          <img:VideoSourceToken>{sourceToken}</img:VideoSourceToken>
        </img:GetImagingSettings>
        """;

    private static string BuildSetImagingSettingsEnvelope(string sourceToken, string imagingBody, string securityHeader) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Header>{securityHeader}</s:Header>
          <s:Body>
            <img:SetImagingSettings xmlns:img="http://www.onvif.org/ver20/imaging/wsdl">
              <img:VideoSourceToken>{sourceToken}</img:VideoSourceToken>
              <img:ImagingSettings>
                {imagingBody}
              </img:ImagingSettings>
              <img:ForcePersistence>true</img:ForcePersistence>
            </img:SetImagingSettings>
          </s:Body>
        </s:Envelope>
        """;

    private async Task<string?> FirstSuccessfulSoapAsync(
        IReadOnlyList<string> urls,
        string bodyInner,
        DeviceIdentity device,
        TimeSpan probeTimeout,
        CancellationToken cancellationToken)
    {
        foreach (var url in urls)
        {
            using var client = httpClientFactory.CreateClient("onvif");
            client.Timeout = probeTimeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(probeTimeout);
            var xml = await ProbeExceptionSwallow.RunAsync(
                () => PostSoapAsync(client, url, bodyInner, device, cts.Token),
                logger,
                $"ONVIF SOAP miss {url}");
            if (!string.IsNullOrWhiteSpace(xml) && !xml.Contains("Fault", StringComparison.OrdinalIgnoreCase))
            {
                return xml;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the VideoSourceToken used by GetImagingSettings/SetImagingSettings from a
    /// GetProfiles response (VideoSourceConfiguration/SourceToken, or the token attribute on a
    /// VideoSourceConfiguration element). Internal for unit tests.
    /// </summary>
    internal static string? ResolveVideoSourceToken(string? profilesXml)
    {
        if (string.IsNullOrWhiteSpace(profilesXml))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(profilesXml);
            var sourceToken = doc.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("SourceToken", StringComparison.OrdinalIgnoreCase))
                ?.Value
                .Trim();
            if (!string.IsNullOrWhiteSpace(sourceToken))
            {
                return sourceToken;
            }

            return doc.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("VideoSourceConfiguration", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("token")?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    // ── field → SOAP mapping ────────────────────────────────────────

    internal static string ResolveFieldKey(WritePlan plan)
    {
        // Preferred: trailing segment of the contract key ("image.brightness" → "brightness").
        if (!string.IsNullOrWhiteSpace(plan.ContractKey))
        {
            var segments = plan.ContractKey.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0)
            {
                return segments[^1];
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.Endpoint))
        {
            var cleaned = plan.Endpoint.TrimEnd('/');
            var lastSlash = cleaned.LastIndexOf('/');
            var segment = lastSlash >= 0 ? cleaned[(lastSlash + 1)..] : cleaned;
            if (!string.IsNullOrWhiteSpace(segment))
            {
                return segment;
            }
        }

        return plan.Payload?.FirstOrDefault(static pair => !string.IsNullOrWhiteSpace(pair.Key)).Key ?? string.Empty;
    }

    internal static System.Text.Json.Nodes.JsonNode? ExtractFieldValue(System.Text.Json.Nodes.JsonObject? payload, string fieldKey)
    {
        if (payload is null || string.IsNullOrWhiteSpace(fieldKey))
        {
            return null;
        }

        var variants = new[]
        {
            fieldKey,
            "$." + fieldKey,
            fieldKey + "Level",
            fieldKey + "Mode",
            fieldKey == "saturation" ? "colorSaturation" : null
        };
        foreach (var variant in variants)
        {
            if (variant is not null && payload.TryGetPropertyValue(variant, out var value) && value is not null)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Maps a BossCam field key + value to the ONVIF <c>tt:</c> imaging element(s) that actually
    /// carry it. Returns null for fields with no mapping — callers MUST treat null as a refusal
    /// rather than writing an unrelated setting.
    /// </summary>
    internal static string? BuildImagingSettingsElement(string fieldKey, System.Text.Json.Nodes.JsonNode? value)
    {
        switch (fieldKey?.ToLowerInvariant() ?? string.Empty)
        {
            case "brightness":
                return ScalarElement("Brightness", value);
            case "contrast":
                return ScalarElement("Contrast", value);
            case "saturation":
                return ScalarElement("ColorSaturation", value);
            case "sharpness":
                return ScalarElement("Sharpness", value);
            case "gamma":
                return ScalarElement("Gamma", value);
            case "exposure":
                return ExposureElement(value);
            case "awb":
            case "whitebalance":
                return ModeElement("WhiteBalance", value, ["MANUAL", "AUTO"]);
            case "wdr":
                return ModeElement("WideDynamicRange", value, ["AUTO", "ON", "OFF"]);
            case "daynight":
            case "ircut":
            case "irmode":
                return IrCutFilterElement(value);
            default:
                return null;
        }
    }

    private static string? ScalarElement(string element, System.Text.Json.Nodes.JsonNode? value)
    {
        if (value is not System.Text.Json.Nodes.JsonValue jsonValue)
        {
            return null;
        }

        if (!TryGetNumber(jsonValue, out var number))
        {
            return null;
        }

        // ONVIF imaging scalars (Brightness/Contrast/ColorSaturation/Sharpness/Gamma) are signed
        // -100..100 on the wire. Clamping to 0..100 would reject negative values that BuildImageGroup
        // legitimately reads back (e.g. a dimmed night image) and break write-read round-trips.
        var clamped = Math.Clamp(number, -100, 100);
        return $"<tt:{element} xmlns:tt=\"http://www.onvif.org/ver10/schema\">{clamped.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}</tt:{element}>";
    }

    private static bool TryGetNumber(System.Text.Json.Nodes.JsonValue value, out double number)
    {
        if (value.TryGetValue<double>(out var d))
        {
            number = d;
            return true;
        }

        if (value.TryGetValue<int>(out var i))
        {
            number = i;
            return true;
        }

        if (value.TryGetValue<string>(out var s)
            && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            number = parsed;
            return true;
        }

        number = 0;
        return false;
    }

    private static string? ExposureElement(System.Text.Json.Nodes.JsonNode? value)
        => value?.ToString()?.Trim() is { } mode
            && (mode.Equals("MANUAL", StringComparison.OrdinalIgnoreCase) || mode.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
            ? $"<tt:Exposure xmlns:tt=\"http://www.onvif.org/ver10/schema\"><tt:Mode>{mode.ToUpperInvariant()}</tt:Mode></tt:Exposure>"
            : null;

    private static string? ModeElement(string element, System.Text.Json.Nodes.JsonNode? value, string[] allowed)
    {
        if (value is null)
        {
            return null;
        }

        var raw = value.ToString().Trim();
        var match = allowed.FirstOrDefault(mode => raw.Equals(mode, StringComparison.OrdinalIgnoreCase));
        if (match is null && bool.TryParse(raw, out var boolean))
        {
            match = boolean ? "ON" : "OFF";
        }

        return match is null || !allowed.Contains(match, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"<tt:{element} xmlns:tt=\"http://www.onvif.org/ver10/schema\"><tt:Mode>{match}</tt:Mode></tt:{element}>";
    }

    private static string? IrCutFilterElement(System.Text.Json.Nodes.JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        var raw = value.ToString().Trim();
        // ONVIF IrCutFilter modes are AUTO/ON/OFF; map day/night vocabulary: day → OFF (filter
        // retracted), night/mono → ON (filter engaged), auto → AUTO.
        if (raw.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return ModeElement("IrCutFilter", System.Text.Json.Nodes.JsonValue.Create("AUTO"), ["AUTO", "ON", "OFF"]);
        }

        if (raw.Equals("ON", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("NIGHT", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("MONO", StringComparison.OrdinalIgnoreCase)
            || raw == "1")
        {
            return ModeElement("IrCutFilter", System.Text.Json.Nodes.JsonValue.Create("ON"), ["AUTO", "ON", "OFF"]);
        }

        if (raw.Equals("OFF", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("DAY", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("COLOR", StringComparison.OrdinalIgnoreCase)
            || raw == "0")
        {
            return ModeElement("IrCutFilter", System.Text.Json.Nodes.JsonValue.Create("OFF"), ["AUTO", "ON", "OFF"]);
        }

        return null;
    }

    // ── read-path group builders (XDocument) ────────────────────────

    private static SettingGroup? BuildDeviceGroup(string? xml)
    {
        var doc = Parse(xml);
        if (doc is null)
        {
            return null;
        }

        // Object-shaped per-endpoint payload so contract-driven normalization (TypedSettingsService)
        // can extract fields via their SourcePaths ("$.manufacturer" etc.) exactly like NetSDK adapters.
        var payload = new System.Text.Json.Nodes.JsonObject();
        foreach (var (fieldKey, localName) in new[]
        {
            ("manufacturer", "Manufacturer"),
            ("model", "Model"),
            ("firmware", "FirmwareVersion"),
            ("serial", "SerialNumber")
        })
        {
            var element = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
            if (element is not null)
            {
                payload[fieldKey] = System.Text.Json.Nodes.JsonValue.Create(element.Value.Trim());
            }
        }

        return payload.Count == 0 ? null : new SettingGroup
        {
            Name = "Device",
            DisplayName = "ONVIF Device",
            RawPayload = new System.Text.Json.Nodes.JsonObject { ["xml"] = xml },
            Values = new Dictionary<string, SettingValue>
            {
                ["deviceInfo"] = new()
                {
                    Key = "deviceInfo",
                    DisplayName = "Device Information",
                    Value = payload,
                    ValueKind = SettingValueKind.Object,
                    SourceEndpoint = "onvif:GetDeviceInformation"
                }
            }
        };
    }

    private static SettingGroup? BuildVideoGroup(string? xml)
    {
        var doc = Parse(xml);
        if (doc is null)
        {
            return null;
        }

        // Object-shaped per-endpoint payload (same model as NetSDK adapters) so the
        // video.onvif.profiles contract extracts profile/resolution/frameRate cleanly.
        var payload = new System.Text.Json.Nodes.JsonObject();
        var tokens = doc.Descendants()
            .Where(element => element.Name.LocalName.Equals("Profiles", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("token")?.Value)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();
        if (tokens.Count > 0)
        {
            payload["profile"] = System.Text.Json.Nodes.JsonValue.Create(string.Join(",", tokens));
        }

        var width = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Width", StringComparison.OrdinalIgnoreCase))?.Value;
        var height = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Height", StringComparison.OrdinalIgnoreCase))?.Value;
        if (width is not null && height is not null)
        {
            payload["resolution"] = System.Text.Json.Nodes.JsonValue.Create($"{width}x{height}");
        }

        var frameRate = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("FrameRate", StringComparison.OrdinalIgnoreCase))?.Value;
        if (frameRate is not null)
        {
            payload["frameRate"] = System.Text.Json.Nodes.JsonValue.Create(frameRate);
        }

        return payload.Count == 0 ? null : new SettingGroup
        {
            Name = "Video",
            DisplayName = "ONVIF Media",
            RawPayload = new System.Text.Json.Nodes.JsonObject { ["xml"] = xml },
            Values = new Dictionary<string, SettingValue>
            {
                ["mediaProfiles"] = new()
                {
                    Key = "mediaProfiles",
                    DisplayName = "Media Profiles",
                    Value = payload,
                    ValueKind = SettingValueKind.Object,
                    SourceEndpoint = "onvif:GetProfiles"
                }
            }
        };
    }

    private static SettingGroup? BuildImageGroup(string? xml)
    {
        var doc = Parse(xml);
        if (doc is null)
        {
            return null;
        }

        // Object-shaped per-endpoint payload: the imaging contracts (image.onvif.brightness etc.)
        // extract each field via its SourcePath, exactly like the NetSDK payload model. Keys are
        // lowercase to match the contract field keys and the SetImagingSettings mapping.
        var payload = new System.Text.Json.Nodes.JsonObject();
        AddScalar(doc, payload, "brightness", "Brightness");
        AddScalar(doc, payload, "contrast", "Contrast");
        AddScalar(doc, payload, "saturation", "ColorSaturation");
        AddScalar(doc, payload, "sharpness", "Sharpness");
        AddScalar(doc, payload, "gamma", "Gamma");
        AddMode(doc, payload, "exposure", "Exposure");
        AddMode(doc, payload, "wdr", "WideDynamicRange");
        AddMode(doc, payload, "daynight", "IrCutFilter");
        AddMode(doc, payload, "awb", "WhiteBalance");

        return payload.Count == 0 ? null : new SettingGroup
        {
            Name = "Image",
            DisplayName = "ONVIF Imaging",
            RawPayload = new System.Text.Json.Nodes.JsonObject { ["xml"] = xml },
            Values = new Dictionary<string, SettingValue>
            {
                ["imagingSettings"] = new()
                {
                    Key = "imagingSettings",
                    DisplayName = "Imaging Settings",
                    Value = payload,
                    ValueKind = SettingValueKind.Object,
                    SourceEndpoint = "onvif:GetImagingSettings"
                }
            }
        };
    }

    private static void AddScalar(XDocument doc, System.Text.Json.Nodes.JsonObject payload, string fieldKey, string localName)
    {
        var element = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        if (element is not null)
        {
            payload[fieldKey] = System.Text.Json.Nodes.JsonValue.Create(element.Value.Trim());
        }
    }

    private static void AddMode(XDocument doc, System.Text.Json.Nodes.JsonObject payload, string fieldKey, string parentLocalName)
    {
        var parent = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(parentLocalName, StringComparison.OrdinalIgnoreCase));
        var mode = parent?.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Mode", StringComparison.OrdinalIgnoreCase));
        if (mode is not null)
        {
            payload[fieldKey] = System.Text.Json.Nodes.JsonValue.Create(mode.Value.Trim());
        }
    }

    private static XDocument? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            return XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static async Task<string?> PostSoapAsync(HttpClient client, string url, string bodyInner, DeviceIdentity device, CancellationToken cancellationToken)
    {
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var envelope = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Header>{OnvifWsse.BuildSecurityHeader(user, password)}</s:Header>
              <s:Body>{bodyInner}</s:Body>
            </s:Envelope>
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {token}");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // SOAP faults are conventionally HTTP 500, but a 401/403 (bad credentials) arrives as
            // an HTML/plain error page with no literal "Fault" — treat it as no valid response so
            // auth failures are diagnosable instead of appearing as "zero profiles".
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string? Extract(string xml, string tag)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals(tag, StringComparison.OrdinalIgnoreCase))
                ?.Value
                .Trim();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}

/// <summary>
/// Builds the WS-Security UsernameToken SOAP header (nonce-salted PasswordDigest) that the ONVIF
/// spec mandates for SOAP-level authentication. Transport-level Basic is kept as a compatibility
/// fast-path for consumer cameras; the wsse header satisfies stricter/enterprise Profile S/T
/// stacks that reject or ignore SOAP calls carrying only Basic auth.
/// </summary>
internal static class OnvifWsse
{
    /// <summary>
    /// WS-Security UsernameToken header. PasswordDigest = Base64(SHA1(rawNonce + Created + Password))
    /// per the WS-Security UsernameToken profile; the digest uses the RAW nonce bytes concatenated
    /// with the created timestamp and password, NOT the base64 nonce string.
    /// </summary>
    public static string BuildSecurityHeader(string username, string password)
    {
        var nonceBytes = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonceBytes);
        var nonce = Convert.ToBase64String(nonceBytes);
        var created = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var digest = ComputePasswordDigest(nonceBytes, created, password);
        return $"""
            <wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd" xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
              <wsse:UsernameToken>
                <wsse:Username>{EscapeXml(username)}</wsse:Username>
                <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{digest}</wsse:Password>
                <wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{nonce}</wsse:Nonce>
                <wsu:Created>{created}</wsu:Created>
              </wsse:UsernameToken>
            </wsse:Security>
            """;
    }

    /// <summary>Internal for unit tests (InternalsVisibleTo): deterministic digest over caller-supplied
    /// nonce bytes, so tests pin the exact SHA1 wire format without randomness.</summary>
    internal static string ComputePasswordDigest(byte[] nonceBytes, string created, string password)
    {
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var input = new byte[nonceBytes.Length + createdBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(nonceBytes, 0, input, 0, nonceBytes.Length);
        Buffer.BlockCopy(createdBytes, 0, input, nonceBytes.Length, createdBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, input, nonceBytes.Length + createdBytes.Length, passwordBytes.Length);
        return Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(input));
    }

    private static string EscapeXml(string value)
        => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
