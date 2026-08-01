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
/// ONVIF PTZ capability capture probe. Resolves the device service (discovered XAddr first, then
/// brand-guessed per-port candidates), POSTs <c>GetCapabilities</c>, follows the advertised PTZ
/// service XAddr, POSTs <c>GetConfigurations</c>, and returns a verdict:
/// <list type="bullet">
/// <item><see cref="OnvifPtzVerdict.PtzReady"/> — ≥1 PTZConfiguration with a token → ONVIF PTZ is
/// real and <c>ContinuousMove</c>/<c>Stop</c>/<c>GotoPreset</c> are implementable.</item>
/// <item><see cref="OnvifPtzVerdict.NoPtzService"/> — Capabilities advertises no PTZ service; a
/// proprietary CGI (e.g. Foscam-style <c>/decoder_control.cgi</c>) is the likely next lead, not
/// ONVIF PTZ.</item>
/// <item><see cref="OnvifPtzVerdict.PtzAdvertisedNoConfigs"/> — PTZ service exists but has zero
/// configurations; treat as a non-functional stub.</item>
/// <item><see cref="OnvifPtzVerdict.DeviceUnreachable"/>/<see cref="OnvifPtzVerdict.AuthFailure"/> —
/// no evidence either way; fix credentials/reachability first.</item>
/// </list>
/// Raw SOAP responses are persisted as <see cref="EndpointContractFixture"/> evidence (the same
/// table the 5523-W fixture capture flows through) and echoed back on the result so the operator
/// can save them under <c>src/BossCam.Service/fixtures/&lt;brand&gt;/__ONVIF/</c>.
/// </summary>
public sealed class OnvifPtzCapabilityProbe(
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    IApplicationStore store,
    ILogger<OnvifPtzCapabilityProbe> logger)
{
    private const string GetCapabilitiesBody = """<tds:GetCapabilities xmlns:tds="http://www.onvif.org/ver10/device/wsdl"><tds:Category>All</tds:Category></tds:GetCapabilities>""";
    private const string GetConfigurationsBody = """<ptz:GetConfigurations xmlns:ptz="http://www.onvif.org/ver20/ptz/wsdl"/>""";

    public async Task<OnvifPtzCaptureResult> CaptureAsync(OnvifPtzCaptureRequest request, CancellationToken cancellationToken)
    {
        var device = await ResolveDeviceAsync(request, cancellationToken);
        if (device is null || string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return new OnvifPtzCaptureResult
            {
                Success = false,
                Verdict = OnvifPtzVerdict.NoDevice,
                Message = "No device resolved — supply DeviceId or IpAddress."
            };
        }

        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var timeout = TimeSpan.FromSeconds(Math.Max(2, options.Value.HttpTimeoutSeconds));

        // ── 1. GetCapabilities across device-service candidates (XAddr first) ──────────
        string? capabilitiesXml = null;
        string? deviceServiceUrl = null;
        var sawAuthFailure = false;
        foreach (var url in BuildDeviceServiceCandidates(device))
        {
            var (xml, status) = await PostSoapAsync(url, GetCapabilitiesBody, user, password, timeout, cancellationToken);
            if (xml is not null)
            {
                capabilitiesXml = xml;
                deviceServiceUrl = url;
                break;
            }

            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                sawAuthFailure = true;
            }
        }

        if (capabilitiesXml is null)
        {
            return new OnvifPtzCaptureResult
            {
                Success = false,
                Verdict = sawAuthFailure ? OnvifPtzVerdict.AuthFailure : OnvifPtzVerdict.DeviceUnreachable,
                Message = sawAuthFailure
                    ? "ONVIF device service answered 401/403 on every candidate — check credentials before PTZ scoping."
                    : "No ONVIF device service answered GetCapabilities on any candidate URL."
            };
        }

        // ── 2. PTZ service XAddr from Capabilities ─────────────────────────────────────
        var ptzServiceUrl = ExtractPtzServiceXAddr(capabilitiesXml);
        if (string.IsNullOrWhiteSpace(ptzServiceUrl))
        {
            var savedNoPtz = await SaveFixtureAsync(device.Id, deviceServiceUrl!, GetCapabilitiesBody, capabilitiesXml, "onvif.capture.GetCapabilities", cancellationToken);
            return new OnvifPtzCaptureResult
            {
                Success = true,
                Verdict = OnvifPtzVerdict.NoPtzService,
                DeviceServiceUrl = deviceServiceUrl,
                CapabilitiesXml = capabilitiesXml,
                SavedFixtureCount = savedNoPtz,
                Message = "GetCapabilities answered but advertises no PTZ service — ONVIF PTZ is not offered. Next lead: proprietary CGI, not ONVIF PTZ."
            };
        }

        // ── 3. GetConfigurations on the PTZ service ────────────────────────────────────
        var (configsXml, _) = await PostSoapAsync(ptzServiceUrl, GetConfigurationsBody, user, password, timeout, cancellationToken);
        var tokens = ExtractPtzConfigurationTokens(configsXml);
        var verdict = tokens.Count > 0 ? OnvifPtzVerdict.PtzReady : OnvifPtzVerdict.PtzAdvertisedNoConfigs;

        var saved = 0;
        saved += await SaveFixtureAsync(device.Id, deviceServiceUrl!, GetCapabilitiesBody, capabilitiesXml, "onvif.capture.GetCapabilities", cancellationToken);
        if (configsXml is not null)
        {
            saved += await SaveFixtureAsync(device.Id, ptzServiceUrl, GetConfigurationsBody, configsXml, "onvif.capture.ptz.GetConfigurations", cancellationToken);
        }

        return new OnvifPtzCaptureResult
        {
            Success = true,
            Verdict = verdict,
            DeviceServiceUrl = deviceServiceUrl,
            PtzServiceUrl = ptzServiceUrl,
            PtzConfigurationCount = tokens.Count,
            PtzConfigurationTokens = tokens,
            CapabilitiesXml = capabilitiesXml,
            ConfigurationsXml = configsXml,
            SavedFixtureCount = saved,
            Message = verdict == OnvifPtzVerdict.PtzReady
                ? $"ONVIF PTZ service is real: {tokens.Count} PTZConfiguration(s) — ContinuousMove/Stop/GotoPreset are implementable."
                : "PTZ service XAddr exists but GetConfigurations returned no PTZConfiguration — treat as a non-functional PTZ stub."
        };
    }

    // ── device resolution & candidate building ───────────────────────

    private async Task<DeviceIdentity?> ResolveDeviceAsync(OnvifPtzCaptureRequest request, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(request.DeviceId, out var id))
        {
            return await store.GetDeviceAsync(id, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(request.IpAddress))
        {
            return null;
        }

        return new DeviceIdentity
        {
            Name = $"onvif-capture-{request.IpAddress}",
            IpAddress = request.IpAddress,
            LoginName = request.LoginName,
            Password = request.Password,
            Port = 80,
            DeviceType = "ONVIF"
        };
    }

    /// <summary>
    /// Device-service URL candidates: the discovered WS-Discovery XAddr first (authoritative),
    /// then brand-guessed per-port <c>/onvif/device_service</c> paths — mirroring
    /// <see cref="OnvifImagingControlAdapter.BuildDeviceServiceCandidates"/>. Internal for unit
    /// tests (InternalsVisibleTo): pins XAddr-first ordering.
    /// </summary>
    internal IReadOnlyList<string> BuildDeviceServiceCandidates(DeviceIdentity device)
    {
        var urls = new List<string>();
        if (device.Metadata.TryGetValue("xaddrs", out var xaddr)
            && Uri.TryCreate(xaddr, UriKind.Absolute, out var parsed)
            && !string.IsNullOrWhiteSpace(parsed.Host))
        {
            urls.Add(xaddr);
        }

        var ports = options.Value.OnvifProbePorts
            .Concat(device.Port > 0 ? new[] { device.Port } : [])
            .Where(static p => p > 0)
            .Distinct();
        foreach (var port in ports)
        {
            urls.Add($"http://{device.IpAddress}:{port}/onvif/device_service");
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── SOAP transport (mirrors OnvifImagingControlAdapter.PostSoapAsync) ──

    private async Task<(string? Xml, HttpStatusCode? Status)> PostSoapAsync(
        string url,
        string bodyInner,
        string user,
        string password,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var envelope = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Header>{OnvifWsse.BuildSecurityHeader(user, password)}</s:Header>
              <s:Body>{bodyInner}</s:Body>
            </s:Envelope>
            """;

        // Capture the response status via a closure so the SOAP body can stay a plain
        // string? (matching the existing adapter's RunAsync<T> usage) while the caller still
        // distinguishes auth failures (401/403) from transport failures (null status).
        HttpStatusCode? status = null;
        var body = await ProbeExceptionSwallow.RunAsync(
            async () =>
            {
                using var client = httpClientFactory.CreateClient("onvif");
                client.Timeout = timeout;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
                request.Headers.TryAddWithoutValidation("Authorization", $"Basic {token}");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                status = response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cts.Token);
            },
            logger,
            $"ONVIF PTZ capture {url}");
        return (body, status);
    }

    // ── pure parsing helpers (internal for unit tests) ────────────────

    /// <summary>
    /// Extracts the PTZ service XAddr from a GetCapabilities response
    /// (<c>Capabilities/Ptz/XAddr</c>). Returns null when the capability is absent.
    /// </summary>
    internal static string? ExtractPtzServiceXAddr(string? capabilitiesXml)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesXml))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(capabilitiesXml);
            return doc.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("XAddr", StringComparison.OrdinalIgnoreCase)
                    && element.Ancestors().Any(ancestor => ancestor.Name.LocalName.Equals("Ptz", StringComparison.OrdinalIgnoreCase)
                        || ancestor.Name.LocalName.Equals("PTZ", StringComparison.OrdinalIgnoreCase)))
                ?.Value
                .Trim();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the distinct <c>token</c> attributes of every <c>PTZConfiguration</c> element in a
    /// GetConfigurations response. A non-empty result means ONVIF PTZ is actually configured.
    /// </summary>
    internal static IReadOnlyList<string> ExtractPtzConfigurationTokens(string? configurationsXml)
    {
        if (string.IsNullOrWhiteSpace(configurationsXml))
        {
            return [];
        }

        try
        {
            var doc = XDocument.Parse(configurationsXml);
            return doc.Descendants()
                .Where(element => element.Name.LocalName.Equals("PTZConfiguration", StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Attribute("token")?.Value)
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private async Task<int> SaveFixtureAsync(Guid deviceId, string endpoint, string requestBody, string responseBody, string contractKey, CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveContractFixturesAsync(
            [
                new EndpointContractFixture
                {
                    DeviceId = deviceId,
                    Endpoint = endpoint,
                    Method = "POST",
                    ContractKey = contractKey,
                    AuthMode = "wsse+basic",
                    RequestBody = System.Text.Json.Nodes.JsonValue.Create(requestBody),
                    ResponseBody = System.Text.Json.Nodes.JsonValue.Create(responseBody),
                    TruthState = ContractTruthState.Proven,
                    CapturedAt = DateTimeOffset.UtcNow
                }
            ], cancellationToken);
            return 1;
        }
        catch (Exception ex)
        {
            // Fixture persistence is best-effort evidence capture; a store failure must not
            // abort the verdict.
            logger.LogDebug(ex, "Failed to persist ONVIF PTZ capture fixture {ContractKey}", contractKey);
            return 0;
        }
    }
}
