using System.Net;
using System.Text;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Control;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for MaintenanceOperation.TimeSync on the LAN NetSDK REST adapter.
/// The 5523-W time endpoints require BARE documents on the wire: a plain unix-seconds
/// number for /NetSDK/System/time/rtc and a bare GMT string for /timeZone. Object
/// wrappers are rejected with statusCode 6 'Invalid Document', so the write must go
/// through the raw-body path, not the JsonObject-typed WritePlan machinery.
/// </summary>
public sealed class TimeSyncMaintenanceTests
{
    [Fact]
    public async Task TimeSync_Sends_Bare_Scalar_Rtc_And_Bare_Timezone_String()
    {
        var handler = new BodyRecordingHandler(_ => OkStatusCodeZero());
        var adapter = NewAdapter(handler);
        var device = NewDevice(port: 80);

        var result = await adapter.ExecuteMaintenanceAsync(
            device, MaintenanceOperation.TimeSync, payload: null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(MaintenanceOperation.TimeSync, result.Operation);

        // Exactly two bare-scalar writes: RTC then timezone, both on :80.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { 80, 80 }, handler.Requests.Select(r => r.Uri.Port));
        Assert.Equal(
            new[] { "/NetSDK/System/time/rtc", "/NetSDK/System/time/timeZone" },
            handler.Requests.Select(r => r.Uri.AbsolutePath));

        // The RTC body must be a bare unix-seconds integer — NOT a JSON object. If it
        // were { "rtc": … } the 5523-W rejects it with statusCode 6 and the OSD clock
        // stays frozen in the future.
        var rtcBody = handler.Requests[0].Body;
        Assert.False(rtcBody.Trim().StartsWith('{'));
        Assert.True(long.TryParse(rtcBody, out var unixSeconds), $"rtc body was not a bare integer: '{rtcBody}'");
        Assert.InRange(unixSeconds, 1_600_000_000, 4_000_000_000);

        // The timezone body must be a bare JSON STRING document ("GMT-07:00" with quotes,
        // the proven 5523-W PUT form) — not an object and not an unquoted token.
        var tzBody = handler.Requests[1].Body;
        Assert.StartsWith("\"GMT", tzBody, StringComparison.Ordinal);
        Assert.EndsWith("\"", tzBody, StringComparison.Ordinal);
        Assert.DoesNotContain('{', tzBody);
        Assert.DoesNotContain('}', tzBody);
    }

    [Theory]
    [InlineData(7, 0, "GMT+07:00")]
    [InlineData(-7, 0, "GMT-07:00")]
    [InlineData(5, 30, "GMT+05:30")]
    [InlineData(-3, -30, "GMT-03:30")]
    [InlineData(0, 0, "GMT+00:00")]
    public void BuildGmtOffsetString_Formats_Host_Offset_For_Camera(int hours, int minutes, string expected)
        => Assert.Equal(expected, HttpControlAdapterBase.BuildGmtOffsetString(new TimeSpan(hours, minutes, 0)));

    [Fact]
    public async Task TimeSync_Reports_Failure_When_Camera_Rejects_Rtc()
    {
        // Camera answers the RTC write with statusCode 6 (Invalid Document) — the exact
        // failure an object-wrapped payload would produce. TimeSync must NOT claim success.
        var handler = new BodyRecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/time/rtc", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"statusCode\":6,\"message\":\"Invalid Document\"}", Encoding.UTF8, "application/json")
                }
                : OkStatusCodeZero());
        var adapter = NewAdapter(handler);
        var device = NewDevice(port: 80);

        var result = await adapter.ExecuteMaintenanceAsync(
            device, MaintenanceOperation.TimeSync, payload: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("statusCode", result.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage OkStatusCodeZero() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"statusCode\":0}", Encoding.UTF8, "application/json")
    };

    private static LanDirectNetSdkRestAdapter NewAdapter(BodyRecordingHandler handler)
        => new(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 8 }),
            new HandlerBackedFactory(handler),
            store: null!, // TimeSync → SendRawAsync only; the store is never consulted
            NullLogger<LanDirectNetSdkRestAdapter>.Instance);

    private static DeviceIdentity NewDevice(int port) => new()
    {
        Id = Guid.NewGuid(),
        IpAddress = "127.0.0.1",
        Port = port,
        LoginName = "admin",
        Password = "secret",
        Name = "time-sync-test",
        HardwareModel = "5523-W"
    };

    private sealed record RecordedRequest(Uri Uri, string Body);

    private sealed class BodyRecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri!, body));
            return responder(request);
        }
    }

    private sealed class HandlerBackedFactory(BodyRecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
