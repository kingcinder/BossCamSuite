using System.Net;
using System.Text;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// P4 PTZ scoping pass tests: pins the pure GetCapabilities/GetConfigurations parsing and the
/// full capture-flow verdicts (PtzReady / NoPtzService / AuthFailure / DeviceUnreachable) against
/// a stub HTTP factory — no network, deterministic.
/// </summary>
public sealed class OnvifPtzCapabilityProbeTests
{
    private const string CapabilitiesWithPtz = """
        <?xml version="1.0" encoding="UTF-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <tds:GetCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
              <tds:Capabilities>
                <tds:Device><tds:XAddr>http://192.168.1.50:8899/onvif/device_service</tds:XAddr></tds:Device>
                <tds:Media><tds:XAddr>http://192.168.1.50:8899/onvif/media_service</tds:XAddr></tds:Media>
                <tds:PTZ><tds:XAddr>http://192.168.1.50:8899/onvif/ptz_service</tds:XAddr></tds:PTZ>
              </tds:Capabilities>
            </tds:GetCapabilitiesResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string CapabilitiesWithoutPtz = """
        <?xml version="1.0" encoding="UTF-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <tds:GetCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
              <tds:Capabilities>
                <tds:Device><tds:XAddr>http://192.168.1.50:80/onvif/device_service</tds:XAddr></tds:Device>
                <tds:Media><tds:XAddr>http://192.168.1.50:80/onvif/media_service</tds:XAddr></tds:Media>
              </tds:Capabilities>
            </tds:GetCapabilitiesResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string ConfigurationsTwo = """
        <?xml version="1.0" encoding="UTF-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <ptz:GetConfigurationsResponse xmlns:ptz="http://www.onvif.org/ver20/ptz/wsdl">
              <ptz:PTZConfiguration token="ptz_main"><ptz:Name>Main</ptz:Name></ptz:PTZConfiguration>
              <ptz:PTZConfiguration token="ptz_aux"><ptz:Name>Aux</ptz:Name></ptz:PTZConfiguration>
            </ptz:GetConfigurationsResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string ConfigurationsEmpty = """
        <?xml version="1.0" encoding="UTF-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <ptz:GetConfigurationsResponse xmlns:ptz="http://www.onvif.org/ver20/ptz/wsdl"/>
          </s:Body>
        </s:Envelope>
        """;

    // ── pure parsing ─────────────────────────────────────────────────

    [Fact]
    public void ExtractPtzServiceXAddr_Returns_XAddr_When_Ptz_Capability_Present()
    {
        var xaddr = OnvifPtzCapabilityProbe.ExtractPtzServiceXAddr(CapabilitiesWithPtz);
        Assert.Equal("http://192.168.1.50:8899/onvif/ptz_service", xaddr);
    }

    [Fact]
    public void ExtractPtzServiceXAddr_Returns_Null_When_Ptz_Absent()
    {
        Assert.Null(OnvifPtzCapabilityProbe.ExtractPtzServiceXAddr(CapabilitiesWithoutPtz));
    }

    [Fact]
    public void ExtractPtzServiceXAddr_Returns_Null_On_Malformed_Xml()
    {
        Assert.Null(OnvifPtzCapabilityProbe.ExtractPtzServiceXAddr("not xml at all"));
        Assert.Null(OnvifPtzCapabilityProbe.ExtractPtzServiceXAddr(null));
    }

    [Fact]
    public void ExtractPtzConfigurationTokens_Reads_Distinct_Tokens()
    {
        var tokens = OnvifPtzCapabilityProbe.ExtractPtzConfigurationTokens(ConfigurationsTwo);
        Assert.Equal(new[] { "ptz_main", "ptz_aux" }, tokens);
    }

    [Fact]
    public void ExtractPtzConfigurationTokens_Empty_For_No_Configurations()
    {
        Assert.Empty(OnvifPtzCapabilityProbe.ExtractPtzConfigurationTokens(ConfigurationsEmpty));
        Assert.Empty(OnvifPtzCapabilityProbe.ExtractPtzConfigurationTokens(null));
    }

    [Fact]
    public void BuildDeviceServiceCandidates_Prefers_Discovered_XAddr()
    {
        using var db = TempDb.Create();
        var probe = CreateProbe(_ => throw new InvalidOperationException("no HTTP expected"), db.Path);
        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.99",
            Port = 8080,
            DeviceType = "ONVIF",
            Metadata = new Dictionary<string, string> { ["xaddrs"] = "http://10.0.0.99:8899/onvif/device_service" }
        };

        var candidates = probe.BuildDeviceServiceCandidates(device);

        Assert.Equal("http://10.0.0.99:8899/onvif/device_service", candidates[0]);
        // Brand-guessed candidates follow: OnvifProbePorts + device.Port, deduped.
        Assert.Contains("http://10.0.0.99:8899/onvif/device_service", candidates);
        Assert.Contains("http://10.0.0.99:8888/onvif/device_service", candidates);
        Assert.Contains("http://10.0.0.99:80/onvif/device_service", candidates);
        Assert.Contains("http://10.0.0.99:8080/onvif/device_service", candidates);
    }

    // ── capture-flow verdicts (stub HTTP) ────────────────────────────

    [Fact]
    public async Task Capture_Returns_PtzReady_When_Configurations_Exist()
    {
        using var db = TempDb.Create();
        var probe = CreateProbe(uri => uri.AbsolutePath.EndsWith("/ptz_service", StringComparison.OrdinalIgnoreCase)
            ? Ok(ConfigurationsTwo)
            : Ok(CapabilitiesWithPtz), db.Path);

        var result = await probe.CaptureAsync(new OnvifPtzCaptureRequest { IpAddress = "192.168.1.50" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(OnvifPtzVerdict.PtzReady, result.Verdict);
        Assert.Equal("http://192.168.1.50:8899/onvif/ptz_service", result.PtzServiceUrl);
        Assert.Equal(2, result.PtzConfigurationCount);
        Assert.Equal(new[] { "ptz_main", "ptz_aux" }, result.PtzConfigurationTokens);
        Assert.Equal(2, result.SavedFixtureCount);
    }

    [Fact]
    public async Task Capture_Returns_NoPtzService_When_Capabilities_Lack_Ptz()
    {
        using var db = TempDb.Create();
        var probe = CreateProbe(_ => Ok(CapabilitiesWithoutPtz), db.Path);

        var result = await probe.CaptureAsync(new OnvifPtzCaptureRequest { IpAddress = "192.168.1.50" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(OnvifPtzVerdict.NoPtzService, result.Verdict);
        Assert.Null(result.PtzServiceUrl);
        Assert.Equal(1, result.SavedFixtureCount);
    }

    [Fact]
    public async Task Capture_Returns_PtzAdvertisedNoConfigs_When_Configurations_Empty()
    {
        using var db = TempDb.Create();
        var probe = CreateProbe(uri => uri.AbsolutePath.EndsWith("/ptz_service", StringComparison.OrdinalIgnoreCase)
            ? Ok(ConfigurationsEmpty)
            : Ok(CapabilitiesWithPtz), db.Path);

        var result = await probe.CaptureAsync(new OnvifPtzCaptureRequest { IpAddress = "192.168.1.50" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(OnvifPtzVerdict.PtzAdvertisedNoConfigs, result.Verdict);
        Assert.Empty(result.PtzConfigurationTokens);
    }

    [Fact]
    public async Task Capture_Returns_AuthFailure_When_All_Candidates_401()
    {
        using var db = TempDb.Create();
        var probe = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), db.Path);

        var result = await probe.CaptureAsync(new OnvifPtzCaptureRequest { IpAddress = "192.168.1.50" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(OnvifPtzVerdict.AuthFailure, result.Verdict);
    }

    [Fact]
    public async Task Capture_Returns_DeviceUnreachable_When_All_Candidates_Transport_Fail()
    {
        using var db = TempDb.Create();
        var probe = CreateProbe(_ => throw new HttpRequestException("connection refused"), db.Path);

        var result = await probe.CaptureAsync(new OnvifPtzCaptureRequest { IpAddress = "192.168.1.50" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(OnvifPtzVerdict.DeviceUnreachable, result.Verdict);
    }

    [Fact]
    public async Task Capture_Returns_NoDevice_When_No_Ip_And_Unknown_DeviceId()
    {
        using var db = TempDb.Create();
        var probe = CreateProbe(_ => throw new InvalidOperationException("no HTTP expected"), db.Path);

        var result = await probe.CaptureAsync(new OnvifPtzCaptureRequest { DeviceId = Guid.NewGuid().ToString() }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(OnvifPtzVerdict.NoDevice, result.Verdict);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/soap+xml")
    };

    private static OnvifPtzCapabilityProbe CreateProbe(Func<Uri, HttpResponseMessage> responder, string dbPath)
    {
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions
        {
            DatabasePath = dbPath
        }));
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new OnvifPtzCapabilityProbe(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 2 }),
            new StubHttpClientFactory(responder),
            store,
            NullLogger<OnvifPtzCapabilityProbe>.Instance);
    }

    /// <summary>
    /// Temp SQLite DB that deletes its file on Dispose — matches the repo's temp-DB convention.
    /// </summary>
    private sealed class TempDb : IDisposable
    {
        public string Path { get; }

        private TempDb(string path) => Path = path;

        public static TempDb Create()
            => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bosscam-ptz-{Guid.NewGuid():N}.db"));

        public void Dispose()
        {
            if (File.Exists(Path)) { try { File.Delete(Path); } catch { } }
        }
    }

    /// <summary>
    /// Stub IHttpClientFactory whose handler routes every request through a deterministic
    /// responder keyed by the request URI. Throws inside the responder propagate as transport
    /// failures, exactly like a refused connection would. Nested (private) per repo convention —
    /// other test classes each have their own same-named stub.
    /// </summary>
    private sealed class StubHttpClientFactory(Func<Uri, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(responder));

        private sealed class StubHandler(Func<Uri, HttpResponseMessage> responder) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(responder(request.RequestUri!));
        }
    }
}
