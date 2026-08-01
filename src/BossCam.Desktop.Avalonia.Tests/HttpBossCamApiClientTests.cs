using System.Net;
using System.Net.Http;
using System.Text;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;

namespace BossCam.Desktop.Avalonia.Tests;

public sealed class HttpBossCamApiClientTests
{
    [Fact]
    public async Task LiveManifest_Relative_Media_Urls_Are_Absolute_For_Native_Player()
    {
        var deviceId = Guid.NewGuid();
        using var handler = new StaticResponseHandler("""
            {
              "deviceId": "00000000-0000-0000-0000-000000000001",
              "sourceCodec": "hevc",
              "preferredMode": "H264MpegTs",
              "mpegTsUrl": "/api/devices/00000000-0000-0000-0000-000000000001/live.ts?quality=sub",
              "mjpegUrl": "/api/devices/00000000-0000-0000-0000-000000000001/live.mjpeg?quality=sub",
              "h264Fmp4Url": "/api/devices/00000000-0000-0000-0000-000000000001/live.h264.mp4?quality=sub",
              "hevcFmp4Url": "/api/devices/00000000-0000-0000-0000-000000000001/live.mp4?quality=sub",
              "snapshotUrl": "/api/devices/00000000-0000-0000-0000-000000000001/snapshot"
            }
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5317") };
        using var client = new HttpBossCamApiClient(http);

        var manifest = await client.GetLiveManifestAsync(deviceId);

        Assert.NotNull(manifest);
        Assert.Equal("http://127.0.0.1:5317/api/devices/00000000-0000-0000-0000-000000000001/live.ts?quality=sub", manifest.MpegTsUrl);
        Assert.Equal("http://127.0.0.1:5317/api/devices/00000000-0000-0000-0000-000000000001/live.mjpeg?quality=sub", manifest.MjpegUrl);
    }

    private sealed class StaticResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
