using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using BossCam.Contracts;
using BossCam.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Discovery;

public sealed class HiChipMulticastDiscoveryProvider(IOptions<BossCamRuntimeOptions> options) : IDiscoveryProvider
{
    public string Name => "HiChipMulticast";

    public async Task<IReadOnlyCollection<DeviceIdentity>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var devices = new List<DeviceIdentity>();
        var timeout = TimeSpan.FromSeconds(options.Value.DiscoveryTimeoutSeconds);
        var request = Encoding.ASCII.GetBytes($"SEARCH * HDS/1.0\r\nCSeq:1\r\nClient-ID:BossCam{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\r\nAccept-Type:text/HDP\r\nContent-Length:0\r\n\r\n");

        foreach (var address in DiscoveryHelpers.GetLocalIpv4Addresses())
        {
            using var client = new UdpClient(new IPEndPoint(address, 0));
            client.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            client.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), address);
            foreach (var port in new[] { 8002, 18002 })
            {
                await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), port));
            }

            while (await DiscoveryHelpers.TryReceiveAsync(client, timeout, cancellationToken) is { } received)
            {
                var values = DiscoveryHelpers.ParseKeyValueResponse(Encoding.UTF8.GetString(received.Buffer));
                devices.Add(new DeviceIdentity
                {
                    DeviceId = DiscoveryHelpers.FirstValue(values, "Device-ID", "Device_ID", "device_id", "deviceid"),
                    Name = DiscoveryHelpers.FirstValue(values, "Device-Name", "device_name") ?? $"HiChip {received.RemoteEndPoint.Address}",
                    IpAddress = DiscoveryHelpers.FirstValue(values, "IP", "ipaddr", "ip") ?? received.RemoteEndPoint.Address.ToString(),
                    Port = int.TryParse(DiscoveryHelpers.FirstValue(values, "HttpPort", "httpport", "HTTP"), out var port) ? port : 80,
                    MacAddress = DiscoveryHelpers.FirstValue(values, "MAC", "hwaddr"),
                    FirmwareVersion = DiscoveryHelpers.FirstValue(values, "Version", "version"),
                    HardwareModel = DiscoveryHelpers.FirstValue(values, "Model", "Type", "type"),
                    DeviceType = "IPC",
                    TransportProfiles =
                    [
                        new TransportProfile { Kind = TransportKind.LanRest, Address = $"http://{received.RemoteEndPoint.Address}:80", Rank = 10 },
                        new TransportProfile { Kind = TransportKind.LanPrivateHttp, Address = $"http://{received.RemoteEndPoint.Address}:80", Rank = 20 }
                    ],
                    Metadata = values
                });
            }
        }

        return devices;
    }
}

public sealed class DvrBroadcastDiscoveryProvider(IOptions<BossCamRuntimeOptions> options) : IDiscoveryProvider
{
    public string Name => "DvrBroadcast";

    public async Task<IReadOnlyCollection<DeviceIdentity>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var devices = new List<DeviceIdentity>();
        var timeout = TimeSpan.FromSeconds(options.Value.DiscoveryTimeoutSeconds);
        var message = Encoding.ASCII.GetBytes("SEARCHDEV");

        foreach (var address in DiscoveryHelpers.GetLocalIpv4Addresses())
        {
            using var client = new UdpClient(new IPEndPoint(address, 0));
            client.EnableBroadcast = true;
            await client.SendAsync(message, message.Length, new IPEndPoint(IPAddress.Broadcast, 9013));

            while (await DiscoveryHelpers.TryReceiveAsync(client, timeout, cancellationToken) is { } received)
            {
                var parsed = DiscoveryHelpers.ParseDvrMessage(Encoding.UTF8.GetString(received.Buffer));
                devices.Add(new DeviceIdentity
                {
                    DeviceId = parsed.GetValueOrDefault("ID"),
                    Name = parsed.GetValueOrDefault("MODEL") ?? $"DVR {received.RemoteEndPoint.Address}",
                    IpAddress = parsed.GetValueOrDefault("JAIP") ?? received.RemoteEndPoint.Address.ToString(),
                    Port = int.TryParse(parsed.GetValueOrDefault("HTTP"), out var port) ? port : 80,
                    FirmwareVersion = parsed.GetValueOrDefault("PVER"),
                    DeviceType = "DVR/NVR",
                    TransportProfiles =
                    [
                        new TransportProfile { Kind = TransportKind.LanPrivateHttp, Address = $"http://{received.RemoteEndPoint.Address}:80", Rank = 20 },
                        new TransportProfile { Kind = TransportKind.OnvifRtsp, Address = $"onvif://{received.RemoteEndPoint.Address}", Rank = 35 }
                    ],
                    Metadata = parsed
                });
            }
        }

        return devices;
    }
}

public sealed class OnvifDiscoveryProvider(
    IOptions<BossCamRuntimeOptions> options,
    ILogger<OnvifDiscoveryProvider> logger) : IDiscoveryProvider
{
    public string Name => "OnvifWsDiscovery";

    public async Task<IReadOnlyCollection<DeviceIdentity>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var devices = new List<DeviceIdentity>();
        var timeout = TimeSpan.FromSeconds(options.Value.DiscoveryTimeoutSeconds);
        var multicastAddress = IPAddress.Parse("239.255.255.250");
        var multicastEndpoint = new IPEndPoint(multicastAddress, 3702);

        // WS-Discovery is interface-scoped: a single wildcard socket (new UdpClient(0)) sends
        // from whichever interface the OS picks for the default route, silently missing cameras
        // reachable only via another NIC (VPN, Docker bridge, second adapter). Send per local
        // interface like the other multicast providers so multi-homed hosts find everything.
        foreach (var address in DiscoveryHelpers.GetLocalIpv4Addresses())
        {
            using var client = new UdpClient(new IPEndPoint(address, 0));
            client.JoinMulticastGroup(multicastAddress, address);

            // Multicast UDP is lossy by design; the WS-Discovery convention is to fire the Probe
            // 2–3 times with jittered spacing rather than once, so a dropped frame or a slow
            // responder doesn't silently miss the device. A fresh MessageID per probe is also
            // required — relays/proxies and strict stacks dedupe by MessageID and would suppress
            // a repeat scan carrying the same constant UUID.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var payload = Encoding.UTF8.GetBytes(BuildProbeEnvelope($"urn:uuid:{Guid.NewGuid():D}"));
                await client.SendAsync(payload, payload.Length, multicastEndpoint);
                if (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(40 + Random.Shared.Next(40)), cancellationToken);
                }
            }

            while (await DiscoveryHelpers.TryReceiveAsync(client, timeout, cancellationToken) is { } received)
            {
                var xml = Encoding.UTF8.GetString(received.Buffer);
                if (TryParseProbeMatch(xml, received.RemoteEndPoint.Address, logger) is { } device)
                {
                    devices.Add(device);
                }
            }
        }

        return devices;
    }

    /// <summary>
    /// Parses and validates a WS-Discovery ProbeMatch response. Pure so it can be unit-tested
    /// without a socket: rejects anything that does not claim <c>NetworkVideoTransmitter</c> in
    /// its Types/Scopes, and tries every advertised XAddr instead of committing to the first.
    /// </summary>
    internal static DeviceIdentity? TryParseProbeMatch(string xml, IPAddress remoteAddress, ILogger logger)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace d = "http://schemas.xmlsoap.org/ws/2005/04/discovery";

            // Only accept responders that actually claim to be a network video transmitter —
            // printers/NAS boxes answering WS-Discovery are a classic false-positive source.
            var types = doc.Descendants(d + "Types").FirstOrDefault()?.Value ?? string.Empty;
            var scopes = doc.Descendants(d + "Scopes").FirstOrDefault()?.Value ?? string.Empty;
            if (!types.Contains("NetworkVideoTransmitter", StringComparison.OrdinalIgnoreCase)
                && !scopes.Contains("NetworkVideoTransmitter", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Skipping WS-Discovery response from {Endpoint}: Types/Scopes do not claim NetworkVideoTransmitter", remoteAddress);
                return null;
            }

            var endpoint = doc.Descendants().FirstOrDefault(element => element.Name.LocalName == "Address")?.Value;

            // Try every advertised XAddr in order; the first valid one wins. The first-listed
            // XAddr is not guaranteed reachable or on the response's subnet (IPv4/IPv6, multiple
            // NICs), so never commit to it blindly.
            var xAddrs = doc.Descendants(d + "XAddrs").FirstOrDefault()?.Value;
            var candidates = (xAddrs ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string? uri = null;
            string? host = null;
            int? port = null;
            foreach (var candidate in candidates)
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) && !string.IsNullOrWhiteSpace(parsed.Host))
                {
                    uri = candidate;
                    host = parsed.Host;
                    port = parsed.Port;
                    break;
                }
            }

            uri ??= $"http://{remoteAddress}/onvif/device_service";
            host ??= remoteAddress.ToString();
            port ??= 80;

            // WS-Discovery Scopes routinely carry onvif://www.onvif.org/mac/aa:bb:cc:dd:ee:ff —
            // extract it so MAC-first merge keying (coordinator + store) stays intact between the
            // ONVIF and HiChip/SubnetScan ingest paths. A camera found by both must collapse into
            // ONE identity, not fragment into mac: vs ip:/deviceId: keys.
            var mac = ExtractMacFromScopes(scopes);

            return new DeviceIdentity
            {
                DeviceId = endpoint,
                Name = $"ONVIF {host}",
                IpAddress = host,
                Port = port.Value,
                MacAddress = mac,
                DeviceType = "ONVIF",
                TransportProfiles =
                [
                    new TransportProfile { Kind = TransportKind.OnvifRtsp, Address = uri, Rank = 15 },
                    new TransportProfile { Kind = TransportKind.Rtsp, Address = $"rtsp://{host}:554", Rank = 16 }
                ],
                Metadata = new Dictionary<string, string>
                {
                    ["xaddrs"] = uri,
                    ["xaddrsAll"] = string.Join(" ", candidates),
                    ["types"] = types,
                    ["scopes"] = scopes
                }
            };
        }
        catch (Exception ex)
        {
            // Non-device / malformed multicast responses are common on a busy LAN; Debug so
            // a discovery pass that yields nothing isn't a black box.
            logger.LogDebug(ex, "ONVIF WS-Discovery response parse failed from {Endpoint}", remoteAddress);
            return null;
        }
    }

    private static string BuildProbeEnvelope(string messageId) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope" xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
          <e:Header>
            <w:MessageID>{messageId}</w:MessageID>
            <w:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
            <w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
          </e:Header>
          <e:Body><d:Probe /></e:Body>
        </e:Envelope>
        """;

    /// <summary>
    /// Extracts the MAC from WS-Discovery Scopes when present. Many devices advertise
    /// <c>onvif://www.onvif.org/mac/aa:bb:cc:dd:ee:ff</c> in Scopes; capturing it keeps MAC-first
    /// merge keying intact across the ONVIF, HiChip, and SubnetScan ingest paths.
    /// </summary>
    private static string? ExtractMacFromScopes(string scopes)
    {
        foreach (var token in scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var marker = "onvif.org/mac/";
            var index = token.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var mac = token[(index + marker.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(mac))
            {
                return mac;
            }
        }

        return null;
    }
}

/// <summary>
/// Subnet IP range scanner — probes common camera HTTP ports across /24 subnets of
/// local interfaces in parallel. Acts as a fallback when multicast discovery yields
/// no results, or can be triggered explicitly via the "Scan subnet" SPA button
/// (<see cref="ISubnetScanDiscoveryProvider.SubnetRangeOverride"/> set to a CIDR/"auto").
/// </summary>
/// <remarks>
/// Key design decisions:
/// - A device is only accepted when <c>GET /NetSDK/System/deviceInfo</c> returns <b>200</b> with a
///   body shaped like NetSDK device info. A bare HTTP response — a 404/401/403 from a printer,
///   NAS, router admin panel, or Docker web UI — is NOT a camera and is rejected. (Cameras that
///   reject unauthenticated requests with 401 would be missed, but NetSDK REST exposes deviceInfo
///   unauthenticated on the units this scanner targets.)
/// - Results are deduplicated by IP address; the first successful port wins (preferred
///   port order: 80, 8080, 554, 8000, 8899, 8888).
/// - Scanning uses Parallel.ForEachAsync with a concurrency limit of 50 to avoid
///   overwhelming the local NIC while still finishing in reasonable time (~30s for /24).
/// </remarks>
public sealed class SubnetScanDiscoveryProvider(
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    IBossCamEventBroadcaster? broadcaster = null) : ISubnetScanDiscoveryProvider
{
    public string Name => "SubnetScan";

    /// <summary>
    /// Transient per-pass override set by <see cref="DiscoveryCoordinator"/>: a CIDR such as
    /// <c>10.0.0.0/24</c> restricts the sweep to that subnet; "auto" or null scans all local /24s.
    /// </summary>
    public string? SubnetRangeOverride { get; set; }

    public async Task<IReadOnlyCollection<DeviceIdentity>> DiscoverAsync(CancellationToken cancellationToken)
    {
        // Collect unique /24 subnet prefixes from local IPv4 addresses (or the explicit override)
        var subnetPrefixes = ResolveSubnetPrefixes();
        if (subnetPrefixes.Count == 0)
        {
            return [];
        }

        // Scan a single IP:port, return DeviceIdentity if a device responded
        async Task<DeviceIdentity?> TryProbeAsync(string ip, int port, CancellationToken ct)
        {
            try
            {
                // Use a short per-request timeout so the whole scan doesn't drag
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.DiscoveryTimeoutSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ct);
                using var client = httpClientFactory.CreateClient("probe");
                using var response = await client.GetAsync($"http://{ip}:{port}/NetSDK/System/deviceInfo", linked.Token);

                // A NetSDK camera must answer deviceInfo with 200 AND a NetSDK-shaped JSON body.
                // Any other HTTP response (404/401/403 from a printer/NAS/router/Docker web UI)
                // means this host is not a camera — do not label it IPC and pull it into inventory.
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(linked.Token);
                if (!LooksLikeNetSdkDeviceInfo(body))
                {
                    return null;
                }

                // Parse the MAC from the accepted deviceInfo body. MAC-first merge keying means a
                // scanner result without MacAddress would fragment from the HiChip-discovered copy
                // of the same camera (mac:... vs ip:... keys → two identities in the same inventory).
                // ToString() (not GetValue<string>()) is used deliberately: a non-string value would
                // throw InvalidOperationException from GetValue<string> — escaping both catch blocks
                // and aborting the entire parallel sweep — whereas ToString() never throws.
                string? mac = null;
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(body) is System.Text.Json.Nodes.JsonObject node)
                    {
                        mac = node["mac"]?.ToString() ?? node["macAddress"]?.ToString();
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // LooksLikeNetSdkDeviceInfo already validated the JSON; defensive only.
                }

                return new DeviceIdentity
                {
                    IpAddress = ip,
                    Port = port,
                    MacAddress = string.IsNullOrWhiteSpace(mac) ? null : mac,
                    DeviceType = "IPC",
                    Name = $"Scanned {ip}:{port}",
                    TransportProfiles =
                    [
                        new TransportProfile { Kind = TransportKind.LanRest, Address = $"http://{ip}:{port}", Rank = 10 }
                    ],
                    Metadata = new Dictionary<string, string>
                    {
                        ["scanned"] = "true",
                        ["statusCode"] = $"{(int)response.StatusCode}"
                    }
                };
            }
            catch (HttpRequestException)
            {
                return null; // Connection refused or DNS failure
            }
            catch (TaskCanceledException)
            {
                return null; // Timeout
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        // Deduplication: try preferred ports in order, keep the first success per IP. The full
        // DeviceIdentity (incl. the parsed MacAddress) is kept so the final list survives
        // MAC-first merging with HiChip-discovered copies of the same camera.
        var probePorts = new[] { 80, 8080, 554, 8000, 8899, 8888 };
        var bestDevicePerIp = new ConcurrentDictionary<string, DeviceIdentity>(StringComparer.OrdinalIgnoreCase);
        var ipLock = new object();
        var totalProbes = subnetPrefixes.Count * 254 * probePorts.Length;
        var completed = 0;
        var lastReportedPct = 0;

        void ReportProgress()
        {
            if (broadcaster == null) return;
            var pct = totalProbes > 0 ? (int)(completed * 100.0 / totalProbes) : 0;
            if (pct - lastReportedPct >= 5 || pct == 100)
            {
                lastReportedPct = pct;
                _ = broadcaster.DiscoveryProgressAsync(bestDevicePerIp.Count, Name, pct == 100, null);
            }
        }

        foreach (var subnetPrefix in subnetPrefixes)
        {
            // Generate all IP:port combinations for this subnet
            var probes = new List<(string ip, int port)>();
            foreach (var port in probePorts)
                for (var host = 1; host <= 254; host++)
                    probes.Add(($"{subnetPrefix}{host}", port));

            // Scan in parallel with concurrency limit
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 50,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(probes, parallelOptions, async (probe, ct) =>
            {
                var (ip, port) = probe;
                var device = await TryProbeAsync(ip, port, ct);
                if (device != null)
                {
                    // Deduplicate: only keep the first (highest-priority port) response per IP
                    bestDevicePerIp.TryAdd(ip, device);
                }

                lock (ipLock)
                {
                    completed++;
                    if (completed % 50 == 0)
                        ReportProgress();
                }
            });

            // Final progress for this subnet
            if (broadcaster != null)
            {
                _ = broadcaster.DiscoveryProgressAsync(bestDevicePerIp.Count, Name, false, null);
            }
        }

        // Build final device list: one per unique IP, using the recorded best port (and the MAC
        // parsed from that port's deviceInfo body so MAC-first merging stays intact).
        return bestDevicePerIp.Values.ToList();
    }

    /// <summary>
    /// Resolves the /24 prefixes to sweep: the explicit CIDR override when set, otherwise every
    /// unique /24 of the local IPv4 interfaces. A bare IPv4 (no CIDR) is treated as its /24.
    /// </summary>
    private IReadOnlyList<string> ResolveSubnetPrefixes()
    {
        var range = SubnetRangeOverride?.Trim();
        if (!string.IsNullOrWhiteSpace(range)
            && !range.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && TryParseCidr(range, out var overridePrefix))
        {
            return [overridePrefix];
        }

        var subnetPrefixes = new List<string>();
        foreach (var localIp in DiscoveryHelpers.GetLocalIpv4Addresses())
        {
            var parts = localIp.ToString().Split('.');
            if (parts.Length != 4)
            {
                continue;
            }

            var prefix = $"{parts[0]}.{parts[1]}.{parts[2]}.";
            if (!subnetPrefixes.Contains(prefix))
            {
                subnetPrefixes.Add(prefix);
            }
        }

        return subnetPrefixes;
    }

    private static bool TryParseCidr(string range, out string prefix)
    {
        prefix = string.Empty;
        var slash = range.IndexOf('/');
        var ipPart = slash >= 0 ? range[..slash] : range;
        var mask = slash >= 0 ? range[(slash + 1)..] : "24";
        if (!int.TryParse(mask, out var bits) || bits != 24)
        {
            return false; // Only /24 sweeps are supported today.
        }

        var parts = ipPart.Split('.');
        if (parts.Length != 4 || parts.Any(static part => !byte.TryParse(part, out _)))
        {
            return false;
        }

        prefix = $"{parts[0]}.{parts[1]}.{parts[2]}.";
        return true;
    }

    /// <summary>
    /// True when <paramref name="body"/> parses as a JSON object carrying at least one field that
    /// the NetSDK deviceInfo response shape uses (serial/model/mac/firmware/deviceName/name). This
    /// is the acceptance bar that keeps unrelated web servers out of the camera inventory.
    /// </summary>
    internal static bool LooksLikeNetSdkDeviceInfo(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(body) is not System.Text.Json.Nodes.JsonObject obj)
            {
                return false;
            }

            // NetSDK-specific keys only — "name" is generic enough that a NAS/router/container API
            // responding with {"name":"..."} would pass the bar, defeating the acceptance filter.
            return new[] { "serial", "model", "deviceName", "deviceID", "deviceId", "mac", "macAddress", "firmware" }
                .Any(key => obj.TryGetPropertyValue(key, out var value) && value is not null);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

internal static class DiscoveryHelpers
{
    public static IReadOnlyList<IPAddress> GetLocalIpv4Addresses()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(static nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(static nic => nic.GetIPProperties().UnicastAddresses)
            .Where(static address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
            .Select(static address => address.Address)
            .Distinct()
            .ToList();

    public static async Task<UdpReceiveResult?> TryReceiveAsync(UdpClient client, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await client.ReceiveAsync().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public static Dictionary<string, string> ParseKeyValueResponse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.Contains('=') ? '=' : (line.Contains(':') ? ':' : '\0');
            if (separator == '\0')
            {
                continue;
            }

            var index = line.IndexOf(separator);
            if (index <= 0)
            {
                continue;
            }

            values[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        return values;
    }

    public static Dictionary<string, string> ParseDvrMessage(string text)
    {
        var keys = new[] { "JAIP", "ID", "PORT", "HTTP", "CH", "MODEL", "PVER" };
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remaining = text;
        foreach (var key in keys)
        {
            if (!remaining.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            remaining = remaining[key.Length..];
            var separator = remaining.IndexOf('&');
            if (separator < 0)
            {
                result[key] = remaining;
                break;
            }

            result[key] = remaining[..separator];
            remaining = remaining[(separator + 1)..];
        }
        return result;
    }

    public static string? FirstValue(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }
}
