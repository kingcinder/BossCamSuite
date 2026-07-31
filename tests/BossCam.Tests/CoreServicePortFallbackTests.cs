using System.Net;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using BossCam.Infrastructure.Persistence;
using BossCam.Service.Hosted;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for the recorded-port-first / :80-fallback behaviour in the remaining
/// Core/Service reachability paths: <see cref="NetSdkPortCandidates.AnyPortSucceedsAsync"/>,
/// <see cref="ConnectionDiagnosticService"/> (deviceInfo + snapshot probes),
/// <see cref="RecordingService.BuildSnapshotUrl"/> port derivation, and the
/// <see cref="ConnectivityWatchdogWorker"/> snapshot/HTTP probes — so a 5523-W whose discovery
/// recorded an ONVIF/media port is not misreported as unreachable when the :80 NetSDK REST
/// surface answers.
/// </summary>
public sealed class CoreServicePortFallbackTests
{
    private const int RecordedOnvifPort = 8888;

    [Fact]
    public async Task AnyPortSucceedsAsync_Tries_Recorded_Port_Then_80()
    {
        var called = new List<int>();
        var result = await NetSdkPortCandidates.AnyPortSucceedsAsync(RecordedOnvifPort, (port, _) =>
        {
            called.Add(port);
            return Task.FromResult(port == 80);
        }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(new[] { RecordedOnvifPort, 80 }, called);
    }

    [Fact]
    public async Task AnyPortSucceedsAsync_Port_80_Probes_Single_Port()
    {
        var called = new List<int>();
        var result = await NetSdkPortCandidates.AnyPortSucceedsAsync(80, (port, _) =>
        {
            called.Add(port);
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(new[] { 80 }, called);
    }

    [Fact]
    public async Task AnyPortSucceedsAsync_All_Ports_Fail_Returns_False()
    {
        var result = await NetSdkPortCandidates.AnyPortSucceedsAsync(
            RecordedOnvifPort, (_, _) => Task.FromResult(false), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ConnectionDiagnostics_DeviceInfo_And_Snapshot_Fall_Back_From_Recorded_Port_To_80()
    {
        using var harness = await CreateHarnessAsync(RecordedOnvifPort);

        var report = await harness.Diagnostics.DiagnoseAsync(harness.Device.Id, CancellationToken.None);

        Assert.True(report.ProbeResults["http:deviceInfo"].Success, "deviceInfo must fall back to :80");
        Assert.True(report.ProbeResults["snapshot"].Success, "snapshot must fall back to :80");

        var deviceInfoUris = harness.Handler.RequestedUris
            .Where(uri => uri.AbsolutePath == "/NetSDK/System/deviceInfo").ToList();
        Assert.Equal(new[] { RecordedOnvifPort, 80 }, deviceInfoUris.Select(uri => uri.Port));

        // The diagnostic battery's step 6 (transportBroker.GetSourcesAsync → TransportFailoverService
        // ProbeFallbackSourcesAsync) also probes the snapshot URL through this handler, so multiple
        // [8888, 80] pairs are expected. Assert the essential contract instead of an exact sequence:
        // the recorded port was attempted first and the :80 fallback was reached.
        var snapshotUris = harness.Handler.RequestedUris
            .Where(uri => uri.AbsolutePath == "/NetSDK/Video/encode/channel/101/snapShot").ToList();
        Assert.NotEmpty(snapshotUris);
        Assert.Equal(RecordedOnvifPort, snapshotUris[0].Port);
        Assert.Contains(snapshotUris, uri => uri.Port == 80);
    }

    [Fact]
    public async Task Watchdog_QuickHttpProbe_Falls_Back_From_Recorded_Port_To_80()
    {
        using var harness = await CreateHarnessAsync(RecordedOnvifPort);

        var ok = await harness.Watchdog.QuickHttpProbeAsync(
            harness.Device, "admin", "secret", CancellationToken.None);

        Assert.True(ok);
        var deviceInfoUris = harness.Handler.RequestedUris
            .Where(uri => uri.AbsolutePath == "/NetSDK/System/deviceInfo").ToList();
        Assert.Equal(new[] { RecordedOnvifPort, 80 }, deviceInfoUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task Watchdog_QuickSnapshotProbe_Falls_Back_From_Recorded_Port_To_80()
    {
        using var harness = await CreateHarnessAsync(RecordedOnvifPort);

        var ok = await harness.Watchdog.QuickSnapshotProbeAsync(harness.Device, CancellationToken.None);

        Assert.True(ok);
        var snapshotUris = harness.Handler.RequestedUris
            .Where(uri => uri.AbsolutePath == "/NetSDK/Video/encode/channel/101/snapShot").ToList();
        Assert.Equal(new[] { RecordedOnvifPort, 80 }, snapshotUris.Select(uri => uri.Port));
    }

    [Theory]
    [InlineData(8888, ":8888/NetSDK/Video/encode/channel/101/snapShot")]
    [InlineData(80, ":80/NetSDK/Video/encode/channel/101/snapShot")]
    [InlineData(0, ":80/NetSDK/Video/encode/channel/101/snapShot")]
    [InlineData(-1, ":80/NetSDK/Video/encode/channel/101/snapShot")]
    public void BuildSnapshotUrl_Always_Uses_A_Valid_Port(int port, string expectedPortSuffix)
    {
        var url = RecordingService.BuildSnapshotUrl(new DeviceIdentity
        {
            IpAddress = "10.0.0.5",
            Port = port,
            LoginName = "admin",
            Password = "secret"
        });

        Assert.Contains(expectedPortSuffix, url, StringComparison.Ordinal);
        Assert.StartsWith("http://admin:secret@10.0.0.5", url, StringComparison.Ordinal);
    }

    private static async Task<WatchdogProbeHarness> CreateHarnessAsync(int recordedPort)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-core-fallback-{Guid.NewGuid():N}.db");
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            IpAddress = "127.0.0.1",
            Port = recordedPort,
            LoginName = "admin",
            Password = "secret",
            Name = "core-fallback-test",
            HardwareModel = "5523-W"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var handler = new ScriptedHandler(uri => uri.Port == recordedPort
            ? throw new HttpRequestException($"connection refused on :{uri.Port}")
            : JpegOk());
        var factory = new HandlerBackedFactory(handler);

        TransportBroker? broker = null;
        TransportFailoverService? failover = null;
        broker = new TransportBroker([], store, new ServiceProviderStub(() => failover), NullLogger<TransportBroker>.Instance);
        failover = new TransportFailoverService(store, broker, factory, NullLogger<TransportFailoverService>.Instance);

        var diagnostics = new ConnectionDiagnosticService(store, factory, broker, NullLogger<ConnectionDiagnosticService>.Instance);
        var watchdog = new ConnectivityWatchdogWorker(
            store,
            diagnostics,
            failover,
            factory,
            NullBossCamEventBroadcaster.Instance,
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 5 }),
            NullLogger<ConnectivityWatchdogWorker>.Instance);

        return new WatchdogProbeHarness(dbPath, device, handler, diagnostics, watchdog);
    }

    private sealed record WatchdogProbeHarness(
        string DbPath,
        DeviceIdentity Device,
        ScriptedHandler Handler,
        ConnectionDiagnosticService Diagnostics,
        ConnectivityWatchdogWorker Watchdog) : IDisposable
    {
        public void Dispose()
        {
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }

    private static HttpResponseMessage JpegOk()
    {
        // Probe validators check JPEG SOI (FF D8) + size > 500, not full image decode.
        var payload = new byte[600];
        payload[0] = 0xFF;
        payload[1] = 0xD8;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
    }

    private sealed class ServiceProviderStub(Func<object?> resolver) : IServiceProvider
    {
        public object? GetService(Type serviceType) => resolver();
    }

    /// <summary>Records every request URI and lets the test dictate the response (or throw).</summary>
    private sealed class ScriptedHandler(Func<Uri, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            return Task.FromResult(responder(request.RequestUri!));
        }
    }

    private sealed class HandlerBackedFactory(ScriptedHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
