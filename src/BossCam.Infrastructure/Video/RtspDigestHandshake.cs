using System.Net.Sockets;
using System.Text;

namespace BossCam.Infrastructure.Video;

/// <summary>
/// RTSP-plane credential probe for the NetSDK family (the 5523-W's happytimesoft RTSP plane
/// is Digest-auth — live-verified: a bare OPTIONS returns 200 without proving credentials,
/// while a DESCRIBE without auth draws a 401 + Digest challenge).
///
/// <see cref="ProbePathAsync"/> connects to the camera's RTSP port and completes a DESCRIBE
/// handshake against one stream path (<c>ch0_0.264</c> main / <c>ch0_1.264</c> sub). When the
/// first DESCRIBE is answered 2xx outright (open or Basic-accepting plane) the path is proven
/// without a handshake. A 401 carrying a Digest <c>WWW-Authenticate</c> challenge is answered
/// with the computed RFC-2617 response — method <c>DESCRIBE</c>, HA2 over the RTSP
/// absolute-form request-target <c>rtsp://host:port/path</c> (RTSP uses absolute-form
/// request-URIs, unlike the origin-form HTTP REST probe) — and the retry must be answered 2xx
/// for the path to be emitted. Everything else — rejection, a 401 without a challenge,
/// a non-RTSP peer, a closed port, transport failure — returns false, and caller cancellation
/// always propagates. Bounded by a linked timeout (default 3s) so a silent RTSP plane cannot
/// hang source resolution: the internal timeout resolves to "path unproven" (false), never a
/// thrown cancellation.
/// <para>
/// Scope note: the handshake proves the DESCRIBE plane — a path that accepts an authenticated
/// DESCRIBE (or needs none) is emitted. A server that answers DESCRIBE anonymously but 401s on
/// SETUP/PLAY would pass this probe yet fail in the player; that deeper failure is covered by
/// the existing failed-playback verdict invalidation loop, which re-probes and re-handshakes
/// on the next resolution.
/// </para>
/// </summary>
internal static class RtspDigestHandshake
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Proves that <paramref name="path"/> on the camera's RTSP plane accepts the computed
    /// credentials (or needs none). See the class doc for the exact handshake flow.
    /// <paramref name="timeout"/> is the per-probe bound (default 3s); injectable so the
    /// silent-plane timeout path is testable without a 3-second wait.
    /// </summary>
    internal static async Task<bool> ProbePathAsync(string host, int port, string path, string user, string password, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? HandshakeTimeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            using var stream = client.GetStream();

            var requestTarget = $"rtsp://{host}:{port}/{path}";
            await WriteRequestAsync(stream, BuildDescribe(requestTarget, cseq: 1, authorization: null), cts.Token);
            var first = await ReadResponseAsync(stream, cts.Token);
            if (first is null || !TryParseStatus(first, out var firstCode))
            {
                return false; // non-RTSP peer or connection closed before a status line
            }

            if (firstCode is >= 200 and < 300)
            {
                return true; // open/Basic-accepting path — the DESCRIBE succeeded unauthenticated
            }

            // A 401 without an answerable Digest challenge is an auth refusal we cannot satisfy.
            var wwwAuthenticate = ExtractHeader(first, "WWW-Authenticate");
            if (wwwAuthenticate is null
                || !TryBuildDigestAuthorization(wwwAuthenticate, "DESCRIBE", requestTarget, user, password, out var authorization))
            {
                return false;
            }

            await WriteRequestAsync(stream, BuildDescribe(requestTarget, cseq: 2, authorization), cts.Token);
            var retry = await ReadResponseAsync(stream, cts.Token);
            return retry is not null && TryParseStatus(retry, out var retryCode) && retryCode is >= 200 and < 300;
        }
        // Caller cancellation must propagate (a cancelled source resolution must not be
        // mistaken for "path unproven"). The internal 3-second timeout cancels the SAME linked
        // token, so it is distinguished here: when the caller's token is NOT cancelled, the
        // operation was bounded by our own timeout — a silent RTSP plane is "not verified",
        // never a thrown cancellation that would abort the whole source resolution.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildDescribe(string requestTarget, int cseq, string? authorization)
    {
        var builder = new StringBuilder($"DESCRIBE {requestTarget} RTSP/1.0\r\nCSeq: {cseq}\r\nAccept: application/sdp\r\n");
        if (authorization is not null)
        {
            builder.Append($"Authorization: {authorization}\r\n");
        }

        builder.Append("\r\n");
        return builder.ToString();
    }

    /// <summary>
    /// Extracts the scheme token from a raw <c>WWW-Authenticate</c> header value and delegates
    /// the hardened parsing/composition to <see cref="DigestChallenge"/> (method DESCRIBE,
    /// RTSP absolute-form request-target).
    /// </summary>
    private static bool TryBuildDigestAuthorization(string wwwAuthenticate, string method, string requestTarget, string user, string password, out string authorization)
    {
        authorization = string.Empty;
        var schemeEnd = wwwAuthenticate.IndexOf(' ');
        if (schemeEnd < 0
            || !wwwAuthenticate[..schemeEnd].Equals("Digest", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parameter = wwwAuthenticate[(schemeEnd + 1)..].Trim();
        return DigestChallenge.TryBuildAuthorization(parameter, method, requestTarget, user, password, out authorization);
    }

    private static string? ExtractHeader(string response, string name)
        => response.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            ?.Substring(name.Length + 1)
            .Trim();

    private static bool TryParseStatus(string response, out int code)
    {
        code = 0;
        var lineEnd = response.IndexOf("\r\n", StringComparison.Ordinal);
        var statusLine = lineEnd < 0 ? response : response[..lineEnd];
        if (!statusLine.StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out code);
    }

    private static async Task<string?> ReadResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read <= 0)
            {
                return total == 0 ? null : Encoding.ASCII.GetString(buffer, 0, total);
            }

            total += read;
            if (HasHeaderTerminator(buffer, total))
            {
                return Encoding.ASCII.GetString(buffer, 0, total);
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
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

    private static async Task WriteRequestAsync(NetworkStream stream, string request, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
