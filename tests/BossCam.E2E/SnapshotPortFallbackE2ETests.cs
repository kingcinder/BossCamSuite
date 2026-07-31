using System.Net;
using System.Net.Http.Json;
using BossCam.Contracts;

namespace BossCam.E2E;

/// <summary>
/// E2E coverage for <c>/api/devices/{id}/snapshot</c> port fallback: discovery can record an
/// ONVIF/media port while the NetSDK REST snapshot surface actually listens on 80 (live-verified
/// on 5523-W units). A real listener on 127.0.0.1:80 simulates the camera; the registered device
/// carries a <em>closed</em> ephemeral recorded port so the first candidate transport-fails.
///
/// The positive test requires binding :80, so it early-returns on unprivileged runners (same
/// convention as the live-camera E2E tests); run the suite elevated to exercise it. The
/// negative test (all candidates unreachable → 502) runs everywhere — loopback
/// connection-refused is instant and nothing can legitimately serve a JPEG at a NetSDK
/// snapshot path on a dev box's :80.
/// </summary>
[Collection("BossCamE2E")]
public sealed class SnapshotPortFallbackE2ETests : IClassFixture<BossCamWebAppFactory>
{
    private readonly HttpClient _client;

    public SnapshotPortFallbackE2ETests(BossCamWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Snapshot_Falls_Back_From_Recorded_Port_To_80()
    {
        using var cts = new CancellationTokenSource();
        using var server = Port80JpegServer.TryStart(cts.Token);
        if (server is null)
        {
            // Environment gate (same convention as LiveCameraExhaustiveTests): binding :80 needs
            // elevation or a free port. Early-return so unprivileged runners don't fail; log a
            // breadcrumb so the gating is visible in CI output instead of an unconditional pass.
            Console.WriteLine("[SnapshotPortFallbackE2ETests] SKIPPED (could not bind 127.0.0.1:80) — run elevated to exercise the port fallback.");
            return;
        }

        var recordedPort = Port80JpegServer.GetFreeTcpPort(); // closed — the recorded-port candidate transport-fails
        var device = await RegisterDeviceAsync(recordedPort);

        var res = await _client.GetAsync($"/api/devices/{device.Id}/snapshot");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/jpeg", res.Content.Headers.ContentType?.MediaType);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 500, "fallback snapshot payload too small");
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]); // JPEG SOI
    }

    [Fact]
    public async Task Snapshot_Returns_502_When_Recorded_Port_And_80_Are_Unreachable()
    {
        var device = await RegisterDeviceAsync(Port80JpegServer.GetFreeTcpPort());

        var res = await _client.GetAsync($"/api/devices/{device.Id}/snapshot");

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }

    private async Task<DeviceIdentity> RegisterDeviceAsync(int port)
    {
        var res = await _client.PostAsJsonAsync("/api/devices/register", new
        {
            ipAddress = "127.0.0.1",
            port,
            loginName = "admin",
            password = "",
            name = $"snapshot-fallback-e2e-{port}",
            hardwareModel = "5523-W"
        });
        await E2EHelpers.AssertOkAsync(res, $"register 127.0.0.1:{port}");
        return (await res.Content.ReadFromJsonAsync<DeviceIdentity>())!;
    }
}
