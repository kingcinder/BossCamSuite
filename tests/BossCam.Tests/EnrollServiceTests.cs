using System.Net;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class EnrollServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-enroll-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public EnrollServiceTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    private const string CannedDeviceInfoJson =
        """{"deviceName":"Front Door","model":"5523-W","firmwareVersion":"2.10.0","serialNumber":"SN123456","macAddress":"AA:BB:CC:DD:EE:01","eseeID":"ESEE-7"}""";

    [Fact]
    public async Task Enroll_Probes_Recorded_Port_Then_Falls_Back_To_80()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        // Recorded port 8899 (ONVIF) is dead for NetSDK REST; deviceInfo answers on :80.
        var factory = new CannedHttpClientFactory(request =>
        {
            if (request.RequestUri!.Port != 80)
            {
                return null; // transport failure on the recorded port
            }

            return request.RequestUri.AbsolutePath.Contains("deviceInfo", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(HttpStatusCode.OK, CannedDeviceInfoJson)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var enroll = BuildEnrollService(store, factory, []);
        var result = await enroll.EnrollDeviceAsync(new EnrollDeviceRequest
        {
            IpAddress = "10.0.0.7",
            Port = 8899,
            LoginName = "admin",
            Password = "pw"
        }, CancellationToken.None);

        Assert.True(result.Enrolled);
        Assert.Equal(80, result.HttpControlPort);
        Assert.Contains(result.Steps, step => step.Step == "netsdk-probe" && step.Success);
        var device = Assert.Single(await store.GetDevicesAsync(CancellationToken.None));
        Assert.Equal(80, device.HttpControlPort);
        Assert.Contains(":80/NetSDK/System/deviceInfo", device.LastGoodControlUrl, StringComparison.Ordinal);
        Assert.Equal("Front Door", device.Name);
        Assert.Equal("AA:BB:CC:DD:EE:01", device.MacAddress);
    }

    [Fact]
    public async Task Enroll_Reports_Clear_Auth_Failure_Without_Persisting()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var factory = new CannedHttpClientFactory(_ => JsonResponse(HttpStatusCode.Unauthorized, "{}"));

        var enroll = BuildEnrollService(store, factory, []);
        var result = await enroll.EnrollDeviceAsync(new EnrollDeviceRequest
        {
            IpAddress = "10.0.0.7",
            LoginName = "admin",
            Password = "wrong"
        }, CancellationToken.None);

        Assert.False(result.Enrolled);
        Assert.Contains(result.Steps, step => step.Step == "auth" && !step.Success);
        Assert.Empty(await store.GetDevicesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Enroll_Recorded_Port_401_Does_Not_Abort_Before_80_Fallback_Succeeds()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        // The recorded ONVIF port 8899 rejects Basic (401); the NetSDK REST surface on :80 accepts
        // the same credentials — enrollment must not declare auth failure without trying :80.
        var factory = new CannedHttpClientFactory(request => request.RequestUri!.Port switch
        {
            8899 => JsonResponse(HttpStatusCode.Unauthorized, "{}"),
            80 => JsonResponse(HttpStatusCode.OK, CannedDeviceInfoJson),
            _ => null
        });

        var enroll = BuildEnrollService(store, factory, []);
        var result = await enroll.EnrollDeviceAsync(new EnrollDeviceRequest
        {
            IpAddress = "10.0.0.7",
            Port = 8899,
            LoginName = "admin",
            Password = "pw"
        }, CancellationToken.None);

        Assert.True(result.Enrolled);
        Assert.Equal(80, result.HttpControlPort);
        Assert.DoesNotContain(result.Steps, step => step.Step == "auth");
        Assert.Contains(result.Steps, step => step.Step == "netsdk-probe" && step.Success);
    }

    [Fact]
    public async Task Enroll_Rejects_Foreign_Json_On_Fallback_Port()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        // Recorded port dead; :80 hosts an unrelated JSON service — must NOT be misattributed.
        var factory = new CannedHttpClientFactory(request => request.RequestUri!.Port switch
        {
            8899 => null,
            80 => JsonResponse(HttpStatusCode.OK, """{"service":"router-admin","uptime":"42d"}"""),
            _ => null
        });

        var enroll = BuildEnrollService(store, factory, []);
        var result = await enroll.EnrollDeviceAsync(new EnrollDeviceRequest
        {
            IpAddress = "10.0.0.7",
            Port = 8899,
            LoginName = "admin",
            Password = "pw"
        }, CancellationToken.None);

        Assert.True(result.Enrolled); // enroll still succeeds for an operator-initiated device
        Assert.Contains(result.Steps, step => step.Step == "netsdk-probe" && !step.Success);
        Assert.Equal(0, result.HttpControlPort); // control port NOT learned from foreign JSON
    }

    [Fact]
    public async Task Enroll_Resolves_Profile_Password_From_Environment()
    {
        Environment.SetEnvironmentVariable("BOSSCAM_CRED_DEFAULT_PASSWORD", "env-pw");
        try
        {
            var store = CreateStore();
            await store.InitializeAsync(CancellationToken.None);
            var factory = new CannedHttpClientFactory(request =>
                request.RequestUri!.AbsolutePath.Contains("deviceInfo", StringComparison.OrdinalIgnoreCase)
                    ? JsonResponse(HttpStatusCode.OK, CannedDeviceInfoJson)
                    : null);

            var enroll = BuildEnrollService(store, factory, []);
            var result = await enroll.EnrollDeviceAsync(new EnrollDeviceRequest
            {
                IpAddress = "10.0.0.7",
                CredentialProfile = "default"
            }, CancellationToken.None);

            Assert.True(result.Enrolled);
            Assert.Equal("default", result.CredentialProfile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOSSCAM_CRED_DEFAULT_PASSWORD", null);
        }
    }

    [Fact]
    public async Task Enroll_Merges_With_Existing_Identity_By_Mac_Keeping_Id_And_Flags()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var existing = new DeviceIdentity
        {
            IpAddress = "10.0.0.7",
            Port = 80,
            LoginName = "admin",
            Password = "old-pw",
            MacAddress = "AA:BB:CC:DD:EE:01",
            DeviceType = "IPC",
            ContinuousRecord = true,
            LinkHint = LinkHint.Lan,
            DiscoveredAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        await store.UpsertDevicesAsync([existing], CancellationToken.None);
        var factory = new CannedHttpClientFactory(request =>
            request.RequestUri!.AbsolutePath.Contains("deviceInfo", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(HttpStatusCode.OK, CannedDeviceInfoJson)
                : null);

        var enroll = BuildEnrollService(store, factory, []);
        var result = await enroll.EnrollDeviceAsync(new EnrollDeviceRequest
        {
            IpAddress = "10.0.0.7",
            Password = "pw"
        }, CancellationToken.None);

        Assert.True(result.Enrolled);
        var device = Assert.Single(await store.GetDevicesAsync(CancellationToken.None));
        Assert.Equal(existing.Id, device.Id); // identity continuity across re-enroll
        Assert.Equal(existing.Id, result.DeviceId);
        Assert.True(device.ContinuousRecord); // sticky
        Assert.Equal(LinkHint.Lan, device.LinkHint); // pre-existing hint survives
        Assert.Equal(80, device.HttpControlPort);
    }

    [Fact]
    public async Task Enroll_No_Playable_Rtsp_Degrades_To_Snapshot()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var factory = new CannedHttpClientFactory(request =>
            request.RequestUri!.AbsolutePath.Contains("deviceInfo", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(HttpStatusCode.OK, CannedDeviceInfoJson)
                : null);
        // 127.0.0.1:1 → connection refused instantly, so the bounded RTSP probe fails fast.
        var videoAdapters = new IVideoTransportAdapter[]
        {
            new StaticVideoAdapter(
            [
                new VideoSourceDescriptor { Kind = TransportKind.Rtsp, Url = "rtsp://127.0.0.1:1/ch0_0.264", Rank = 0, Metadata = new Dictionary<string, string> { ["stream"] = "main" } }
            ])
        };

        var enroll = BuildEnrollService(store, factory, videoAdapters);
        var result = await enroll.EnrollDeviceAsync(new EnrollDeviceRequest
        {
            IpAddress = "10.0.0.9",
            Password = "pw"
        }, CancellationToken.None);

        Assert.True(result.Enrolled);
        Assert.Equal("snapshot", result.SourceRole);
        Assert.NotNull(result.DegradedReason);
        Assert.Null(result.ChosenSourceUrl);
    }

    [Fact]
    public async Task Enroll_StartContinuousRecord_Reports_Step_Outcome_Without_Failing_Enroll()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var factory = new CannedHttpClientFactory(request =>
            request.RequestUri!.AbsolutePath.Contains("deviceInfo", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(HttpStatusCode.OK, CannedDeviceInfoJson)
                : null);

        var enroll = BuildEnrollService(store, factory, []);
        var result = await enroll.EnrollDeviceAsync(new EnrollDeviceRequest
        {
            IpAddress = "10.0.0.9",
            Password = "pw",
            StartContinuousRecord = true
        }, CancellationToken.None);

        Assert.True(result.Enrolled); // enroll itself succeeds even if the record start reports failure
        var recordStep = Assert.Single(result.Steps.Where(step => step.Step == "continuous-record"));
        // Outcome depends on ffmpeg presence and pipeline reachability (the snapshot pipeline can
        // start even when the source is unreachable, self-healing later). The invariant: the
        // reported jobId agrees with the step result and enroll never throws.
        Assert.Equal(recordStep.Success, result.ContinuousJobId is not null);
        Assert.False(string.IsNullOrEmpty(recordStep.Message));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch
            {
            }
        }
    }

    private SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));

    private static EnrollService BuildEnrollService(IApplicationStore store, IHttpClientFactory httpClientFactory, IEnumerable<IVideoTransportAdapter> videoAdapters)
    {
        var broker = new TransportBroker(videoAdapters, store, null, NullLogger<TransportBroker>.Instance);
        var recording = new RecordingService(store, broker, new TestRecordingPipelineResolver(), NullBossCamEventBroadcaster.Instance, httpClientFactory, NullLogger<RecordingService>.Instance);
        return new EnrollService(store, httpClientFactory, broker, recording, Options.Create(new BossCamRuntimeOptions()), NullLogger<EnrollService>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StaticVideoAdapter(IReadOnlyCollection<VideoSourceDescriptor> sources) : IVideoTransportAdapter
    {
        public string Name => "Static";
        public TransportKind TransportKind => TransportKind.Rtsp;
        public int Priority => 1;
        public Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult(sources);
    }

    private sealed class CannedHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage?> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new DelegatingHandlerResponder(responder)) { Timeout = TimeSpan.FromSeconds(5) };

        private sealed class DelegatingHandlerResponder(Func<HttpRequestMessage, HttpResponseMessage?> responder) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(responder(request) ?? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
