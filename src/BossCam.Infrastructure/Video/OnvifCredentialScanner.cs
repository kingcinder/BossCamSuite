using System.Net;
using System.Text;
using System.Xml.Linq;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Video;

/// <summary>
/// Probes a camera's ONVIF device service with known default credential pairs to
/// discover working credentials. Used when the web UI is locked but ONVIF may have
/// a separate (often default) credential set.
///
/// The credential list is vendor-aware: generic defaults (admin:admin, etc.) are
/// tried first, then vendor-specific pairs based on the manufacturer extracted
/// from an unauthenticated GetCapabilities heuristic, then a broader sweep.
/// </summary>
public sealed class OnvifCredentialScanner(
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    IApplicationStore store,
    ILogger<OnvifCredentialScanner> logger)
{
    private const string GetCapabilitiesBody =
        "<tds:GetCapabilities xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\">" +
        "<tds:Category>All</tds:Category>" +
        "</tds:GetCapabilities>";

    /// <summary>
    /// Credential pairs tried in order. Each entry is (username, password).
    /// Ordered from most-common to least-common.
    /// </summary>
    private static readonly (string Username, string Password)[] GenericCredentials =
    [
        ("admin", "admin"),
        ("admin", "12345"),
        ("admin", "123456"),
        ("admin", "password"),
        ("admin", ""),
        ("service", "service"),
        ("operator", "operator"),
        ("root", "root"),
        ("user", "user"),
        ("Admin", "Admin"),
        ("admin", "Admin123"),
    ];

    private static readonly Dictionary<string, (string Username, string Password)[]> VendorCredentials
        = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hikvision"] = [("admin", "12345"), ("admin", "admin12345"), ("admin", "hik12345")],
            ["dahua"] = [("admin", "admin"), ("admin", "admin123"), ("888888", "888888")],
            ["wansview"] = [("admin", "admin"), ("admin", ""), ("admin", "123456")],
            ["esee"] = [("admin", "admin"), ("admin", ""), ("admin", "123456")],
            ["foscam"] = [("admin", ""), ("admin", "admin"), ("visitor", "visitor")],
            ["lorex"] = [("admin", "admin"), ("admin", "000000")],
            ["amcrest"] = [("admin", "admin")],
            ["reolink"] = [("admin", ""), ("admin", "admin")],
            ["tp-link"] = [("admin", "admin")],
            ["bosch"] = [("service", "service"), ("admin", "admin")],
            ["axis"] = [("root", "pass"), ("root", "root")],
            ["sony"] = [("admin", "admin")],
            ["samsung"] = [("admin", "4321"), ("admin", "admin")],
            ["hanwha"] = [("admin", "4321"), ("admin", "admin")],
            ["panasonic"] = [("admin", "12345")],
            ["vivotek"] = [("root", ""), ("admin", "admin")],
            ["uniview"] = [("admin", "123456")],
            ["geovision"] = [("admin", "admin")],
            ["wanscam"] = [("admin", "admin"), ("admin", ""), ("admin", "123456")],
            ["iegeek"] = [("admin", ""), ("admin", "admin"), ("admin", "123456")],
            ["sv3c"] = [("admin", "123456"), ("admin", "admin")],
        };

    public async Task<OnvifCredentialScanResult> ScanAsync(
        OnvifCredentialScanRequest request,
        CancellationToken cancellationToken)
    {
        var device = await ResolveDeviceAsync(request, cancellationToken);
        if (device is null || string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return new OnvifCredentialScanResult
            {
                Success = false,
                Message = "No device resolved — supply DeviceId or IpAddress.",
                AttemptedCredentials = []
            };
        }

        var ip = device.IpAddress!;
        var timeout = TimeSpan.FromSeconds(Math.Max(3, options.Value.HttpTimeoutSeconds));
        var ports = BuildOnvifPorts(device);

        // Build credential list: generic first
        var credentialList = new List<OnvifCredentialPair>();
        foreach (var (user, pass) in GenericCredentials)
        {
            credentialList.Add(new OnvifCredentialPair { Username = user, Password = pass });
        }

        // Phase 1: unauthenticated probe to learn manufacturer
        string? detectedManufacturer = null;
        var sawOnvif = false;
        foreach (var port in ports)
        {
            var url = $"http://{ip}:{port}/onvif/device_service";
            var xml = await ProbeUnauthenticatedAsync(url, timeout, cancellationToken);
            if (xml is not null)
            {
                sawOnvif = true;
                detectedManufacturer = ExtractManufacturer(xml);
                break;
            }
        }

        if (!sawOnvif)
        {
            return new OnvifCredentialScanResult
            {
                Success = false,
                Message = "No ONVIF device service responded on any candidate port.",
                AttemptedCredentials = []
            };
        }

        // Phase 2: prepend vendor-specific creds if manufacturer detected
        if (!string.IsNullOrWhiteSpace(detectedManufacturer))
        {
            foreach (var (vendor, pairs) in VendorCredentials)
            {
                if (detectedManufacturer.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (user, pass) in pairs)
                    {
                        var pair = new OnvifCredentialPair { Username = user, Password = pass };
                        if (!credentialList.Any(c =>
                                c.Username == pair.Username && c.Password == pair.Password))
                        {
                            credentialList.Insert(0, pair);
                        }
                    }
                    break;
                }
            }
        }

        // Phase 3: try credentials across ports
        foreach (var cred in credentialList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var port in ports)
            {
                var url = $"http://{ip}:{port}/onvif/device_service";
                var xml = await PostSoapAuthenticatedAsync(
                    url, GetCapabilitiesBody, cred.Username, cred.Password, timeout, cancellationToken);

                if (xml is not null)
                {
                    var manufacturer = ExtractManufacturer(xml) ?? detectedManufacturer;
                    var model = ExtractModel(xml);

                    return new OnvifCredentialScanResult
                    {
                        Success = true,
                        DeviceServiceUrl = url,
                        Manufacturer = manufacturer,
                        Model = model,
                        FirmwareVersion = null, // GetDeviceInformation call would be needed for FW version
                        WorkingCredential = cred,
                        AttemptedCredentials = credentialList,
                        Message = $"ONVIF unlocked with {cred.Username}:{Mask(cred.Password)} on port {port}. "
                            + $"Manufacturer: {manufacturer ?? "unknown"}, Model: {model ?? "unknown"}."
                    };
                }
            }
        }

        return new OnvifCredentialScanResult
        {
            Success = false,
            Message = $"ONVIF device service responded but no credential pair worked "
                + $"(tried {credentialList.Count} pairs on {ports.Count} ports).",
            AttemptedCredentials = credentialList,
            Manufacturer = detectedManufacturer
        };
    }

    private List<int> BuildOnvifPorts(DeviceIdentity device)
    {
        var ports = new List<int>();
        if (device.OnvifMediaPort is > 0 && !ports.Contains(device.OnvifMediaPort.Value))
        {
            ports.Add(device.OnvifMediaPort.Value);
        }
        foreach (var p in options.Value.OnvifProbePorts)
        {
            if (p > 0 && !ports.Contains(p)) ports.Add(p);
        }
        if (device.HttpControlPort > 0 && !ports.Contains(device.HttpControlPort))
        {
            ports.Add(device.HttpControlPort);
        }
        if (device.Port > 0 && !ports.Contains(device.Port))
        {
            ports.Add(device.Port);
        }
        foreach (var p in new[] { 80, 8080, 8000, 8899, 8888 })
        {
            if (!ports.Contains(p)) ports.Add(p);
        }
        return ports;
    }

    private async Task<string?> ProbeUnauthenticatedAsync(
        string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await ProbeExceptionSwallow.RunAsync(async () =>
        {
            using var client = httpClientFactory.CreateClient("onvif");
            client.Timeout = timeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var envelope = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">"
                + "<s:Body>" + GetCapabilitiesBody + "</s:Body>"
                + "</s:Envelope>";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return "ONVIF_AUTH_REQUIRED";
            }
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(cts.Token);
        }, logger, $"ONVIF unauthenticated probe {url}");
    }

    private async Task<string?> PostSoapAuthenticatedAsync(
        string url, string bodyInner, string user, string password,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await ProbeExceptionSwallow.RunAsync(async () =>
        {
            using var client = httpClientFactory.CreateClient("onvif");
            client.Timeout = timeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var envelope = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">"
                + "<s:Header>" + OnvifWsse.BuildSecurityHeader(user, password) + "</s:Header>"
                + "<s:Body>" + bodyInner + "</s:Body>"
                + "</s:Envelope>";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
            var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basicToken}");

            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(cts.Token);
        }, logger, $"ONVIF auth probe {url} ({user}:***)");
    }

    internal static string? ExtractManufacturer(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml == "ONVIF_AUTH_REQUIRED") return null;
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();
        }
        catch (System.Xml.XmlException) { return null; }
    }

    internal static string? ExtractModel(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml == "ONVIF_AUTH_REQUIRED") return null;
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("Model", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private async Task<DeviceIdentity?> ResolveDeviceAsync(
        OnvifCredentialScanRequest request, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(request.DeviceId, out var id))
        {
            return await store.GetDeviceAsync(id, cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(request.IpAddress))
        {
            return new DeviceIdentity
            {
                Name = $"onvif-scan-{request.IpAddress}",
                IpAddress = request.IpAddress,
                Port = 80,
                DeviceType = "ONVIF"
            };
        }
        return null;
    }

    private static string Mask(string password)
    {
        if (string.IsNullOrEmpty(password)) return "(empty)";
        return password.Length <= 2 ? "**"
            : password[..1] + new string('*', password.Length - 2) + password[^1..];
    }
}
