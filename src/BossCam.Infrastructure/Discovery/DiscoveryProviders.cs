using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using BossCam.Contracts;
using BossCam.Core;
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

public sealed class OnvifDiscoveryProvider(IOptions<BossCamRuntimeOptions> options) : IDiscoveryProvider
{
    public string Name => "OnvifWsDiscovery";

    public async Task<IReadOnlyCollection<DeviceIdentity>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var devices = new List<DeviceIdentity>();
        var timeout = TimeSpan.FromSeconds(options.Value.DiscoveryTimeoutSeconds);
        const string envelope = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\"><e:Header><w:MessageID>uuid:00000000-0000-0000-0000-000000000001</w:MessageID><w:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To><w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action></e:Header><e:Body><d:Probe /></e:Body></e:Envelope>";
        var payload = Encoding.UTF8.GetBytes(envelope);

        using var client = new UdpClient(0);
        await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702));

        while (await DiscoveryHelpers.TryReceiveAsync(client, timeout, cancellationToken) is { } received)
        {
            var xml = Encoding.UTF8.GetString(received.Buffer);
            try
            {
                var doc = XDocument.Parse(xml);
                XNamespace d = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
                var xAddrs = doc.Descendants(d + "XAddrs").FirstOrDefault()?.Value;
                var endpoint = doc.Descendants().FirstOrDefault(element => element.Name.LocalName == "Address")?.Value;
                var uri = string.IsNullOrWhiteSpace(xAddrs) ? $"http://{received.RemoteEndPoint.Address}/onvif/device_service" : xAddrs.Split(' ', StringSplitOptions.RemoveEmptyEntries).First();
                var host = Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) ? parsedUri.Host : received.RemoteEndPoint.Address.ToString();

                devices.Add(new DeviceIdentity
                {
                    DeviceId = endpoint,
                    Name = $"ONVIF {host}",
                    IpAddress = host,
                    Port = parsedUri?.Port ?? 80,
                    DeviceType = "ONVIF",
                    TransportProfiles =
                    [
                        new TransportProfile { Kind = TransportKind.OnvifRtsp, Address = uri, Rank = 15 },
                        new TransportProfile { Kind = TransportKind.Rtsp, Address = $"rtsp://{host}:554", Rank = 16 }
                    ],
                    Metadata = new Dictionary<string, string> { ["xaddrs"] = uri }
                });
            }
            catch
            {
            }
        }

        return devices;
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

