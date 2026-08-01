using System.Net;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Commit 4: generic RTSP fallback candidates in <see cref="MultiBrandHighResTransportAdapter"/>.
/// Wansview / Netview / Temu PTZ are not NetSDK units, so the adapter must emit probe-playable
/// generic paths (ranked below brand-proven + ONVIF GetStreamUri results) instead of assuming the
/// Juan tables — and it must never pollute a 5523-W with those guesses.
/// </summary>
public sealed class MultiBrandGenericRtspTests
{
    [Fact]
    public async Task Generic_Candidates_Emitted_For_Wansview_Like_Device()
    {
        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.6",
            // "Wansview" alone doesn't match a DetectBrand keyword (brand would resolve to Unknown,
            // pulling in the Juan tier); "WVC…" maps to WvcOnvif so only the generic tier is emitted.
            HardwareModel = "WVC631GA",
            Name = "Wansview cam",
            DeviceType = "IPC",
            LoginName = "admin",
            Password = "pw"
        };

        var adapter = new MultiBrandHighResTransportAdapter(
            Options.Create(new BossCamRuntimeOptions()),
            new StubHttpClientFactory(),
            NullLogger<MultiBrandHighResTransportAdapter>.Instance);

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        // Probe-playable generic paths present for a non-Juan brand…
        Assert.Contains(sources, source => source.Url.Contains("/stream1", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Url.Contains("/live", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Url.Contains("/h264", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Url.Contains("/videoMain", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Url.Contains("/cam/realmonitor?channel=1&subtype=0", StringComparison.Ordinal));
        // …ranked below the ONVIF/Juan/Dahua tiers (main ≤ 30, sub ≥ 60).
        var mains = sources.Where(source => source.Metadata.TryGetValue("stream", out var stream) && stream == "main").ToList();
        Assert.NotEmpty(mains);
        Assert.All(mains, source => Assert.InRange(source.Rank, 1, 30));
    }

    [Fact]
    public async Task No_Generic_Candidates_For_5523_W_Device()
    {
        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.4",
            HardwareModel = "5523-W",
            Name = "5523",
            DeviceType = "IPC",
            EseeId = "ESEE-1",
            LoginName = "admin",
            Password = "pw"
        };

        var adapter = new MultiBrandHighResTransportAdapter(
            Options.Create(new BossCamRuntimeOptions()),
            new StubHttpClientFactory(),
            NullLogger<MultiBrandHighResTransportAdapter>.Instance);

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        // Juan tables stay canonical; no generic guesses on a 5523-W.
        Assert.Contains(sources, source => source.Url.EndsWith("/ch0_0.264", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Url.Contains("/stream1", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Url.Contains("/videoMain", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Metadata.TryGetValue("generic", out var generic) && generic == "true");
    }

    [Fact]
    public async Task RtspPort_Hint_Is_Honored_For_Generic_Candidates()
    {
        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.8",
            HardwareModel = "Netview",
            Name = "Netview cam",
            DeviceType = "IPC",
            RtspPort = 8554,
            LoginName = "admin",
            Password = "pw"
        };

        var adapter = new MultiBrandHighResTransportAdapter(
            Options.Create(new BossCamRuntimeOptions()),
            new StubHttpClientFactory(),
            NullLogger<MultiBrandHighResTransportAdapter>.Instance);

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        var genericMain = sources.FirstOrDefault(source => source.Url.Contains("/stream1", StringComparison.Ordinal));
        Assert.NotNull(genericMain);
        Assert.StartsWith("rtsp://", genericMain.Url, StringComparison.Ordinal);
        Assert.Contains(":8554/stream1", genericMain.Url, StringComparison.Ordinal);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StaticHandler()) { Timeout = TimeSpan.FromSeconds(5) };

        private sealed class StaticHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
