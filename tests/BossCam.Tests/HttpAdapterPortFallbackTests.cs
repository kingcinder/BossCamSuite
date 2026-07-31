using System.Net;
using System.Text;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using BossCam.Infrastructure.Control;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for the port-fallback behaviour in <see cref="HttpControlAdapterBase.SendAsync"/>:
/// discovery can record an ONVIF/media port (8888/8899) on a device while the NetSDK REST control
/// plane actually listens on 80 (live-verified on 5523-W units). The adapter must try the recorded
/// port first, then fall back to 80 — but ONLY when the first attempt fails at the TRANSPORT level
/// (no HTTP response). Any HTTP response — even an error status — is authoritative for that port and
/// must not cascade into a fallback probe.
/// </summary>
public sealed class HttpAdapterPortFallbackTests
{
    private const int RecordedOnvifPort = 8888;

    [Theory]
    [InlineData(8888, new[] { 8888, 80 })] // recorded ONVIF port → recorded first, then 80
    [InlineData(8899, new[] { 8899, 80 })] // alternate ONVIF/media port
    [InlineData(8080, new[] { 8080, 80 })] // any valid non-80 port
    [InlineData(80, new[] { 80 })]         // already the REST port → single probe
    [InlineData(0, new[] { 80 })]          // unset/zero → default 80
    [InlineData(-1, new[] { 80 })]         // bogus negative → default 80
    public void NetSdkPortCandidates_For_Returns_Recorded_First_Then_80_Fallback(int port, int[] expected)
        => Assert.Equal(expected, NetSdkPortCandidates.For(port));

    [Theory]
    [InlineData(8888, 8888, false)] // recorded port first — not a fallback
    [InlineData(8888, 80, true)]    // :80 is the fallback candidate
    [InlineData(8899, 80, true)]    // any valid non-80 recorded port
    [InlineData(80, 80, false)]     // single-element list — no fallback
    [InlineData(0, 80, false)]      // zero port → :80 is the default, not a fallback
    [InlineData(-1, 80, false)]     // bogus negative → :80 is the default
    [InlineData(8888, 8080, false)] // not a candidate at all
    public void NetSdkPortCandidates_IsFallback_Matches_For_Contract(int devicePort, int candidatePort, bool expected)
        => Assert.Equal(expected, NetSdkPortCandidates.IsFallback(devicePort, candidatePort));

    [Fact]
    public async Task Recorded_Port_Transport_Failure_Falls_Back_To_80()
    {
        // Recorded ONVIF port 8888 dies at the transport level (connection refused);
        // the NetSDK REST plane answers 200 on :80.
        var handler = new RecordingHandler(uri => uri.Port == RecordedOnvifPort
            ? throw new HttpRequestException($"connection refused on :{uri.Port}")
            : OkDeviceInfo());
        var adapter = NewAdapter(handler);
        var device = NewDevice(port: RecordedOnvifPort);

        var handled = await adapter.CanHandleAsync(device, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(
            new[] { RecordedOnvifPort, 80 },
            handler.RequestedUris.Select(uri => uri.Port));
        Assert.All(handler.RequestedUris, uri => Assert.Equal("/NetSDK/System/deviceInfo", uri.AbsolutePath));
    }

    [Fact]
    public async Task Device_Port_80_Tries_Single_Port_Once()
    {
        var handler = new RecordingHandler(_ => OkDeviceInfo());
        var adapter = NewAdapter(handler);
        var device = NewDevice(port: 80);

        var handled = await adapter.CanHandleAsync(device, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(new[] { 80 }, handler.RequestedUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task Device_Port_Zero_Defaults_To_80()
    {
        var handler = new RecordingHandler(_ => OkDeviceInfo());
        var adapter = NewAdapter(handler);
        var device = NewDevice(port: 0);

        var handled = await adapter.CanHandleAsync(device, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(new[] { 80 }, handler.RequestedUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task Http_Response_On_Recorded_Port_Is_Authoritative_No_Fallback_Probe()
    {
        // The recorded port answers 200 — a response is authoritative, so :80 must never
        // be probed even though it would have succeeded too.
        var handler = new RecordingHandler(_ => OkDeviceInfo());
        var adapter = NewAdapter(handler);
        var device = NewDevice(port: RecordedOnvifPort);

        var handled = await adapter.CanHandleAsync(device, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(new[] { RecordedOnvifPort }, handler.RequestedUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task Http_Error_Status_On_Recorded_Port_Is_Authoritative_No_Fallback_Probe()
    {
        // 404 is a real HTTP response for the recorded port — never fall back to :80.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found", Encoding.UTF8)
        });
        var adapter = NewAdapter(handler);
        var device = NewDevice(port: RecordedOnvifPort);

        var handled = await adapter.CanHandleAsync(device, CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(new[] { RecordedOnvifPort }, handler.RequestedUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task Http_401_On_Recorded_Port_Runs_Digest_Retry_No_Port_80_Fallback()
    {
        // A 401 is still an HTTP response for the recorded port, so it is authoritative: the
        // adapter must NOT fall back to :80. The digest/credential-cache retry builds a
        // per-request HttpClientHandler (bypassing the factory), so pointing the device at the
        // RFC 5737 TEST-NET-1 address 192.0.2.1 makes that real-network attempt fail fast and
        // deterministically instead of touching a bound local port.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var adapter = NewAdapter(handler, httpTimeoutSeconds: 2);
        var device = NewDevice(port: RecordedOnvifPort, ip: "192.0.2.1");

        var handled = await adapter.CanHandleAsync(device, CancellationToken.None);

        // The digest retry against a non-routable host yields no response → semantic failure.
        Assert.False(handled);
        // The fake handler only ever saw the single Basic attempt on the recorded port — the
        // 401 did not cascade into a :80 fallback probe.
        Assert.Equal(new[] { RecordedOnvifPort }, handler.RequestedUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task Both_Ports_Transport_Failure_Returns_No_Response()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        var adapter = NewAdapter(handler);
        var device = NewDevice(port: RecordedOnvifPort);

        var handled = await adapter.CanHandleAsync(device, CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(
            new[] { RecordedOnvifPort, 80 },
            handler.RequestedUris.Select(uri => uri.Port));
    }

    private static LanDirectNetSdkRestAdapter NewAdapter(RecordingHandler handler, int httpTimeoutSeconds = 8)
        => new(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = httpTimeoutSeconds }),
            new HandlerBackedFactory(handler),
            store: null!, // CanHandleAsync → SendAsync only; the IApplicationStore is never consulted on this path
            NullLogger<LanDirectNetSdkRestAdapter>.Instance);

    private static DeviceIdentity NewDevice(int port, string ip = "127.0.0.1") => new()
    {
        Id = Guid.NewGuid(),
        IpAddress = ip,
        Port = port,
        LoginName = "admin",
        Password = "secret",
        Name = "port-fallback-test",
        HardwareModel = "synthetic"
    };

    private static HttpResponseMessage OkDeviceInfo() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"statusCode\":0,\"deviceType\":\"test\",\"deviceSN\":\"PORT-FALLBACK-TEST\"}",
            Encoding.UTF8,
            "application/json")
    };

    /// <summary>Records every request URI and lets the test dictate the response (or throw).</summary>
    private sealed class RecordingHandler(Func<Uri, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            return Task.FromResult(responder(request.RequestUri!));
        }
    }

    private sealed class HandlerBackedFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
