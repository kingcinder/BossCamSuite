using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
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
        using var server = TryStartPort80JpegServer(BuildJpegPayload(), cts.Token);
        if (server is null)
        {
            // Environment gate (same convention as LiveCameraExhaustiveTests): binding :80 needs
            // elevation or a free port. Early-return so unprivileged runners don't fail; run the
            // suite as root/with sudo to actually exercise the fallback. Log a breadcrumb so the
            // gating is visible in CI output instead of looking like an unconditional pass.
            Console.WriteLine("[SnapshotPortFallbackE2ETests] SKIPPED (could not bind 127.0.0.1:80) — run elevated to exercise the port fallback.");
            return;
        }

        var recordedPort = GetFreeTcpPort(); // closed — the recorded-port candidate transport-fails
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
        var device = await RegisterDeviceAsync(GetFreeTcpPort());

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

    private static TcpListener? TryStartPort80JpegServer(byte[] jpeg, CancellationToken ct)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 80);
            // SO_REUSEADDR keeps a fresh bind workable across a TIME_WAIT 4-tuple from prior runs.
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
        }
        catch (SocketException)
        {
            return null; // unprivileged runner or :80 taken — caller skips
        }

        _ = Task.Run(() => ServeJpegLoop(listener, jpeg, ct));
        return listener;
    }

    private static void ServeJpegLoop(TcpListener listener, byte[] jpeg, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => RespondOnce(client, jpeg));
        }
    }

    private static void RespondOnce(TcpClient client, byte[] jpeg)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                // Consume request headers (GET + Basic auth; no body → no 100-continue dance).
                var buffer = new byte[4096];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = stream.Read(buffer, total, buffer.Length - total);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                    if (HasHeaderTerminator(buffer, total))
                    {
                        break;
                    }
                }

                var header = $"HTTP/1.1 200 OK\r\n" +
                             $"Content-Type: image/jpeg\r\n" +
                             $"Content-Length: {jpeg.Length}\r\n" +
                             $"Connection: close\r\n\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(header);
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(jpeg, 0, jpeg.Length);
                stream.Flush();
            }
        }
        catch
        {
            // client disconnected early — nothing to do
        }
    }

    private static bool HasHeaderTerminator(byte[] buffer, int length)
    {
        for (var i = 0; i <= length - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] BuildJpegPayload()
    {
        // The snapshot proxy validates JPEG SOI (FF D8) + size > 500, not full image decode,
        // so a synthetic payload is sufficient to prove the port-fallback routing.
        var payload = new byte[1024];
        payload[0] = 0xFF;
        payload[1] = 0xD8;
        for (var i = 2; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
