using System.Net;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for the recorded-port-first / :80-fallback behaviour in the video
/// transport adapters (<see cref="StreamDescriptorAdapter"/>, <see cref="BubbleFlvAdapter"/>):
/// discovery can record an ONVIF/media port (8888/8899) while the NetSDK REST and bubble HTTP
/// surfaces listen on 80. The adapters must emit a :80 fallback descriptor (ranked below the
/// recorded-port one) whenever the recorded port differs, and probe the /NetSDK/Stream surface
/// across candidate ports.
/// </summary>
public sealed class VideoAdapterPortFallbackTests
{
    private const int RecordedOnvifPort = 8888;

    [Fact]
    public async Task StreamDescriptorAdapter_Emits_Port80_Snapshot_Fallback_When_Recorded_Port_Is_NonDefault()
    {
        var adapter = new StreamDescriptorAdapter(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 5 }),
            new Static404HttpClientFactory());
        var device = NewDevice(port: RecordedOnvifPort);

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);
        var snapshots = SnapshotSources(sources).ToList();

        var recorded = Assert.Single(snapshots, s => s.Url.Contains($":{RecordedOnvifPort}/NetSDK/Video/encode/channel/101/snapShot", StringComparison.Ordinal));
        var fallback = Assert.Single(snapshots, s => s.Url.Contains(":80/NetSDK/Video/encode/channel/101/snapShot", StringComparison.Ordinal));
        // Recorded-port candidate ranks ahead so failover/tile consumers probe it first.
        Assert.True(recorded.Rank < fallback.Rank);
    }

    [Fact]
    public async Task StreamDescriptorAdapter_Port_80_Emits_Single_Snapshot_No_Fallback_Duplicate()
    {
        var adapter = new StreamDescriptorAdapter(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 5 }),
            new Static404HttpClientFactory());
        var device = NewDevice(port: 80);

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        Assert.Single(SnapshotSources(sources));
    }

    [Fact]
    public async Task StreamDescriptorAdapter_Stream_Discovery_Falls_Back_To_80_On_Transport_Failure()
    {
        var handler = new ScriptedHandler(uri => uri.Port == RecordedOnvifPort
            ? throw new HttpRequestException($"connection refused on :{uri.Port}")
            : OkStreamChannel());
        var adapter = new StreamDescriptorAdapter(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 5 }),
            new HandlerBackedFactory(handler));
        var device = NewDevice(port: RecordedOnvifPort);

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        // The RTSP URL extracted from the :80 /NetSDK/Stream/channel/0 response must be present.
        // The fake response uses a unique path (ch0_5.264) so it can never collide with the Juan
        // RTSP main descriptor (ch0_0.264) the adapter emits unconditionally for 5523-W devices.
        Assert.Contains(sources, s => s.Url.Contains(":554/ch0_5.264", StringComparison.Ordinal));
        Assert.Equal(
            new[] { RecordedOnvifPort, 80 },
            handler.RequestedUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task BubbleFlvAdapter_Emits_Port80_Fallback_When_Recorded_Port_Is_NonDefault()
    {
        var adapter = new BubbleFlvAdapter();
        var device = NewDevice(port: RecordedOnvifPort);

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        var mainRecorded = Assert.Single(sources, s => s.Url.Contains($":{RecordedOnvifPort}/bubble/live?ch=1&stream=0", StringComparison.Ordinal));
        var mainFallback = Assert.Single(sources, s => s.Url.Contains(":80/bubble/live?ch=1&stream=0", StringComparison.Ordinal));
        Assert.True(mainRecorded.Rank < mainFallback.Rank);
        Assert.Equal(4, sources.Count); // main+sub × (recorded + :80 fallback)
    }

    [Fact]
    public async Task BubbleFlvAdapter_Port_80_Emits_Single_Main_And_Sub()
    {
        var adapter = new BubbleFlvAdapter();
        var device = NewDevice(port: 80);

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.Contains(":80/bubble/live?ch=1&stream=", s.Url, StringComparison.Ordinal));
    }

    private static IEnumerable<VideoSourceDescriptor> SnapshotSources(IEnumerable<VideoSourceDescriptor> sources)
        => sources.Where(s => s.Metadata.TryGetValue("kind", out var kind)
            && kind.Equals("snapshot", StringComparison.OrdinalIgnoreCase));

    private static DeviceIdentity NewDevice(int port) => new()
    {
        Id = Guid.NewGuid(),
        IpAddress = "127.0.0.1",
        Port = port,
        LoginName = "admin",
        Password = "secret",
        Name = "video-fallback-test",
        HardwareModel = "5523-W"
    };

    private static HttpResponseMessage OkStreamChannel() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"channels\":[{\"url\":\"rtsp://admin:secret@127.0.0.1:554/ch0_5.264\"}]}",
            System.Text.Encoding.UTF8,
            "application/json")
    };

    /// <summary>Handler that answers 404 to everything — enough to keep StreamDescriptorAdapter's
    /// /NetSDK/Stream discovery from adding URLs without touching real network.</summary>
    private sealed class Static404HttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Static404Handler());
    }

    private sealed class Static404Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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
