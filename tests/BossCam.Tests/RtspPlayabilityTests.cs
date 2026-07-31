using System.Net;
using System.Net.Sockets;
using System.Text;
using BossCam.Core.Utilities;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for <see cref="RtspProbe"/> — the RTSP <em>playability</em> probe that
/// distinguishes "something is listening on :554" (bare TCP) from "an RTSP server answered an
/// OPTIONS handshake" (recordable/live stream). The health-semantics gap this pins: a non-RTSP
/// service on :554 must not be reported as an up/recordable stream, so a TCP-open-only peer must
/// return false.
/// </summary>
public sealed class RtspPlayabilityTests
{
    [Fact]
    public async Task Probe_Returns_True_When_Peer_Answers_Rtsp_Options()
    {
        using var listener = StartTcpListener(async stream =>
        {
            // Consume the OPTIONS request headers, then answer with a valid RTSP status line.
            await ReadUntilBlankLineAsync(stream);
            var status = "RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(status));
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ok = await RtspProbe.ProbeAsync("127.0.0.1", port, CancellationToken.None);

        Assert.True(ok, "an RTSP/1.0 200 answer must count as playable");
    }

    [Fact]
    public async Task Probe_Returns_False_When_Peer_Speaks_Non_Rtsp()
    {
        using var listener = StartTcpListener(async stream =>
        {
            await ReadUntilBlankLineAsync(stream);
            // A web-ish or garbage banner that is NOT an RTSP/1.x status line.
            var banner = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(banner));
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ok = await RtspProbe.ProbeAsync("127.0.0.1", port, CancellationToken.None);

        Assert.False(ok, "a non-RTSP banner must not be reported as a playable RTSP stream");
    }

    [Fact]
    public async Task Probe_Returns_False_When_Peer_Silently_Accepts()
    {
        // Accepts the connection and keeps it OPEN without writing — the probe must hit its own
        // timeout (not EOF) and return false. A bare TCP-open peer is exactly the
        // health-semantics gap we are closing. The bounded delay keeps the handler alive past the
        // 500ms probe timeout, then completes so no task/connection lingers.
        using var listener = StartTcpListener(_ => Task.Delay(TimeSpan.FromSeconds(5)));

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ok = await RtspProbe.ProbeAsync(
            "127.0.0.1", port, CancellationToken.None, timeout: TimeSpan.FromMilliseconds(500));

        Assert.False(ok, "a silent TCP-accepting peer must not be reported as playable");
    }

    [Fact]
    public async Task Probe_Returns_False_When_Port_Closed()
    {
        var port = GetFreeTcpPort(); // closed — connect fails fast

        var ok = await RtspProbe.ProbeAsync("127.0.0.1", port, CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task Probe_Propagates_Caller_Cancellation()
    {
        // Keep the connection open so the probe is blocked in ReadAsync when the caller cancels;
        // a bounded delay (longer than the test) prevents a leaked forever-pending handler task.
        using var listener = StartTcpListener(_ => Task.Delay(TimeSpan.FromSeconds(5)));

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var task = RtspProbe.ProbeAsync("127.0.0.1", port, cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static TcpListener StartTcpListener(Func<NetworkStream, Task> respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return; // listener disposed
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            await respond(stream);
                        }
                    }
                    catch
                    {
                        // client disconnected early — nothing to do
                    }
                });
            }
        });
        return listener;
    }

    private static async Task ReadUntilBlankLineAsync(NetworkStream stream)
    {
        var buffer = new byte[1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (read <= 0)
            {
                return;
            }

            total += read;
            if (HasHeaderTerminator(buffer, total))
            {
                return;
            }
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

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
