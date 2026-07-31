using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BossCam.E2E;

/// <summary>
/// Shared 127.0.0.1:80 JPEG fixture server for the port-fallback E2E tests. A real listener on
/// port 80 simulates the 5523-W NetSDK REST snapshot surface; a registered device carries a
/// <em>closed</em> ephemeral recorded port so the recorded-port candidate transport-fails and
/// consumers fall back to :80 (live-verified: deviceInfo/snapShot answer on :80 while the
/// recorded ONVIF/media port is dead).
/// </summary>
internal static class Port80JpegServer
{
    /// <summary>
    /// Real, decodable JPEG (ffmpeg testsrc, 64x48, ~1.9 KB) with a valid SOI (FF D8) and &gt;500
    /// bytes, so both the JPEG-validated probe (recording path) and the snapshot endpoint accept it,
    /// and the snapshot recording pipeline's ffmpeg can actually decode frames.
    /// </summary>
    public static byte[] JpegPayload { get; } = Convert.FromBase64String("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");

    /// <summary>Binds 127.0.0.1:80 and serves <see cref="JpegPayload"/> for every request path.</summary>
    public static TcpListener? TryStart(CancellationToken ct) => TryStart(JpegPayload, ct);

    public static TcpListener? TryStart(byte[] jpeg, CancellationToken ct)
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

    /// <summary>Allocates a free loopback TCP port and closes it, leaving it dead (connection-refused).</summary>
    public static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
}
