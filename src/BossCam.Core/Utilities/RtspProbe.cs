using System.Net.Sockets;
using System.Text;

namespace BossCam.Core.Utilities;

/// <summary>
/// Probes RTSP playability on <c>host:port</c>. A bare TCP connect to :554 proves only that
/// <em>something</em> is listening — it does not prove a streamable/recordable RTSP source
/// (health semantics gap: "RTSP up" must not mean "port open"). This helper performs a minimal
/// RTSP <c>OPTIONS</c> handshake and returns true only when the peer answers with an
/// <c>RTSP/1.x</c> status line. Any status line counts — even <c>401</c> proves an RTSP server
/// is present; credentials are the consumer's concern. Closed ports, silent listeners, and
/// non-RTSP services on the port all return false.
/// </summary>
public static class RtspProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns true when the peer on <paramref name="host"/>:<paramref name="port"/> answers an
    /// RTSP OPTIONS request with an RTSP/1.x status line. <paramref name="timeout"/> bounds the
    /// whole handshake (connect + write + read); callers on latency-sensitive paths can pass a
    /// tighter bound. Thrown <see cref="OperationCanceledException"/> from the caller's token
    /// is propagated; transport failures and probe timeouts return false.
    /// </summary>
    public static async Task<bool> ProbeAsync(
        string host,
        int port,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var bound = timeout ?? DefaultTimeout;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(bound);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            if (!client.Connected)
            {
                return false;
            }

            await using var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes($"OPTIONS rtsp://{host}:{port}/ RTSP/1.0\r\nCSeq: 1\r\n\r\n");
            await stream.WriteAsync(request, cts.Token);
            await stream.FlushAsync(cts.Token);

            // Loop until we can decide: a single ReadAsync may return only a fragment of the status
            // line (TCP fragmentation), so we accumulate. Short-circuit success the moment the
            // accumulated bytes start with RTSP/1.; decide false once a complete (newline-terminated)
            // non-RTSP line is seen, on EOF, or when the buffer fills with no status line at all.
            var buffer = new byte[512];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cts.Token);
                if (read <= 0)
                {
                    return false; // EOF before any status line
                }

                total += read;
                var accumulated = Encoding.ASCII.GetString(buffer, 0, total);
                if (accumulated.StartsWith("RTSP/1.", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (accumulated.IndexOf('\n') >= 0 || total >= buffer.Length)
                {
                    return false; // complete non-RTSP status line, or a banner too large to be RTSP
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
