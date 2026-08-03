using System.Net;
using System.Net.Sockets;
using System.Text;
using BossCam.Infrastructure.Video;

namespace BossCam.Tests;

/// <summary>
/// Fixture-driven coverage for <see cref="RtspDigestHandshake"/> — the RTSP-plane credential
/// probe that confirms a specific ch0 path (ch0_0.264 main / ch0_1.264 sub) ACTUALLY accepts
/// the computed credentials before <see cref="NativeNetSdkStreamAdapter"/> emits it as a source.
///
/// The 5523-W's happytimesoft RTSP plane is Digest-auth (live-verified): a bare OPTIONS returns
/// 200 without proving credentials, and a DESCRIBE without auth draws a 401 + WWW-Authenticate
/// Digest challenge. The probe must complete that handshake — computing an RFC-2617 response
/// over the RTSP absolute-form request-target (rtsp://host:port/path) — and only report true
/// when the challenged DESCRIBE succeeds. These tests drive a real loopback RTSP server.
/// </summary>
public sealed class RtspDigestHandshakeTests
{
    [Fact]
    public async Task Handshake_Succeeds_When_Path_Answers_200_Without_Challenge()
    {
        // A server that accepts the DESCRIBE outright (Basic-accepting or open) needs no handshake.
        using var listener = StartTcpListener(new RtspServerBehavior
        {
            FirstResponse = "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Type: application/sdp\r\n\r\n"
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ok = await RtspDigestHandshake.ProbePathAsync("127.0.0.1", port, "ch0_1.264", "admin", string.Empty, CancellationToken.None);

        Assert.True(ok);
    }

    [Fact]
    public async Task Handshake_Completes_Digest_Challenge_With_Absolute_Form_Uri()
    {
        // happytimesoft/Live555-style: DESCRIBE #1 → 401 + Digest challenge (no qop, the classic
        // Live555 form). The probe must compute MD5(HA1:nonce:HA2) with method=DESCRIBE and
        // HA2 over the ABSOLUTE request-target (rtsp://host:port/path) — RTSP uses absolute-form
        // request-URIs, unlike the origin-form HTTP REST probe. A strict server validates HA2
        // against the absolute URI, so an origin-form uri= would fail the handshake.
        var nonce = "deadbeef0123456789";
        var captured = new CapturedAuthorization();
        using var listener = StartTcpListener(new RtspServerBehavior
        {
            ChallengeNonce = nonce,
            Realm = "cam",
            FirstResponse = $"RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\nWWW-Authenticate: Digest realm=\"cam\", nonce=\"{nonce}\"\r\n\r\n",
            Capture = captured
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var path = "ch0_0.264";
        var ok = await RtspDigestHandshake.ProbePathAsync("127.0.0.1", port, path, "admin", string.Empty, CancellationToken.None);

        Assert.True(ok);
        Assert.NotNull(captured.AuthorizationHeader);
        // The uri= directive must be the RTSP absolute-form request-target, NOT origin-form.
        Assert.Contains($"uri=\"rtsp://127.0.0.1:{port}/{path}\"", captured.AuthorizationHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("uri=\"/", captured.AuthorizationHeader, StringComparison.Ordinal);
        // The response hash must match the independently-recomputed legacy (no-qop) digest over
        // the absolute target — recompute exactly what the probe must have computed.
        var expected = Md5Hex($"{Md5Hex("admin:cam:")}:{nonce}:{Md5Hex($"DESCRIBE:rtsp://127.0.0.1:{port}/{path}")}");
        Assert.Contains($"response=\"{expected}\"", captured.AuthorizationHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_Completes_Digest_Challenge_With_Qop_Auth()
    {
        // Some firmware generations advertise qop="auth"; the probe must answer with qop=auth
        // plus nc/cnonce, and the response must use the qop form of the digest computation.
        var nonce = "deadbeef0123456789";
        var captured = new CapturedAuthorization();
        using var listener = StartTcpListener(new RtspServerBehavior
        {
            ChallengeNonce = nonce,
            Realm = "cam",
            Qop = "auth",
            FirstResponse = $"RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\nWWW-Authenticate: Digest realm=\"cam\", qop=\"auth\", nonce=\"{nonce}\"\r\n\r\n",
            Capture = captured
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var path = "ch0_1.264";
        var ok = await RtspDigestHandshake.ProbePathAsync("127.0.0.1", port, path, "admin", string.Empty, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains(", qop=auth,", captured.AuthorizationHeader, StringComparison.Ordinal);
        var cnonce = ExtractCnonce(captured.AuthorizationHeader!);
        var expected = Md5Hex($"{Md5Hex("admin:cam:")}:{nonce}:00000001:{cnonce}:auth:{Md5Hex($"DESCRIBE:rtsp://127.0.0.1:{port}/{path}")}");
        Assert.Contains($"response=\"{expected}\"", captured.AuthorizationHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_Fails_When_Digest_Credentials_Rejected()
    {
        // A server that keeps 401ing even after the retry (wrong password) must return false —
        // emitting the source would hand the player a URL that never authenticates.
        using var listener = StartTcpListener(new RtspServerBehavior
        {
            ChallengeNonce = "deadbeef0123456789",
            Realm = "cam",
            FirstResponse = "RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\nWWW-Authenticate: Digest realm=\"cam\", nonce=\"deadbeef0123456789\"\r\n\r\n",
            AlwaysReject = true
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ok = await RtspDigestHandshake.ProbePathAsync("127.0.0.1", port, "ch0_0.264", "admin", "wrong-password", CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task Handshake_Fails_When_Challenge_Has_No_WwwAuthenticate_Header()
    {
        // A 401 without a Digest challenge is an auth refusal we cannot answer — return false.
        using var listener = StartTcpListener(new RtspServerBehavior
        {
            FirstResponse = "RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\n\r\n"
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ok = await RtspDigestHandshake.ProbePathAsync("127.0.0.1", port, "ch0_0.264", "admin", string.Empty, CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task Handshake_Fails_On_Non_Rtsp_Peer()
    {
        using var listener = StartTcpListener(new RtspServerBehavior
        {
            FirstResponse = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ok = await RtspDigestHandshake.ProbePathAsync("127.0.0.1", port, "ch0_0.264", "admin", string.Empty, CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task Handshake_Fails_When_Port_Closed()
    {
        var port = GetFreeTcpPort();

        var ok = await RtspDigestHandshake.ProbePathAsync("127.0.0.1", port, "ch0_0.264", "admin", string.Empty, CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task Handshake_Internal_Timeout_Returns_False_Not_Throws()
    {
        // A silent RTSP plane (TCP accepts the connection but never answers — a firewall that
        // accepts then drops, a wedged RTSP task) must resolve to "path unproven" (false), NOT a
        // thrown OperationCanceledException that would abort the whole source resolution. This
        // pins the timeout-vs-cancellation distinction: the internal bound is a verdict, only
        // caller cancellation propagates. Short injectable timeout keeps the test fast.
        using var listener = StartTcpListener(new RtspServerBehavior { Stall = true });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ok = await RtspDigestHandshake.ProbePathAsync(
            "127.0.0.1", port, "ch0_0.264", "admin", string.Empty,
            CancellationToken.None, TimeSpan.FromMilliseconds(200));

        Assert.False(ok);
    }

    [Fact]
    public async Task Handshake_Propagates_Caller_Cancellation()
    {
        using var listener = StartTcpListener(new RtspServerBehavior
        {
            FirstResponse = "RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\nWWW-Authenticate: Digest realm=\"cam\", nonce=\"deadbeef0123456789\"\r\n\r\n",
            Stall = true
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var task = RtspDigestHandshake.ProbePathAsync("127.0.0.1", port, "ch0_0.264", "admin", string.Empty, cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    // ── fixture server ────────────────────────────────────────────────

    private sealed class CapturedAuthorization
    {
        public string? AuthorizationHeader { get; set; }
    }

    private sealed class RtspServerBehavior
    {
        public string FirstResponse { get; init; } = string.Empty;
        public string? ChallengeNonce { get; init; }
        public string? Realm { get; init; }
        public string? Qop { get; init; }
        public bool AlwaysReject { get; init; }
        public bool Stall { get; init; }
        public CapturedAuthorization? Capture { get; init; }
    }

    private static TcpListener StartTcpListener(RtspServerBehavior behavior)
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
                            await ServeAsync(stream, behavior);
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

    private static async Task ServeAsync(NetworkStream stream, RtspServerBehavior behavior)
    {
        var first = await ReadRequestAsync(stream);
        if (first is null)
        {
            return;
        }

        if (behavior.Stall)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return;
        }

        if (behavior.AlwaysReject)
        {
            // Answer every request (including the digest retry) with the same 401 challenge.
            while (true)
            {
                var request = await ReadRequestAsync(stream);
                if (request is null)
                {
                    return;
                }

                var challenge = $"RTSP/1.0 401 Unauthorized\r\nCSeq: {ExtractCSeq(request)}\r\nWWW-Authenticate: Digest realm=\"{behavior.Realm}\", nonce=\"{behavior.ChallengeNonce}\"\r\n\r\n";
                await WriteAsync(stream, challenge);
                if (request.Contains("Authorization:", StringComparison.Ordinal))
                {
                    return; // we already rejected the retry — done
                }
            }
        }

        if (behavior.ChallengeNonce is not null)
        {
            var challenge = $"RTSP/1.0 401 Unauthorized\r\nCSeq: {ExtractCSeq(first)}\r\nWWW-Authenticate: Digest realm=\"{behavior.Realm}\", qop=\"{behavior.Qop}\", nonce=\"{behavior.ChallengeNonce}\"\r\n\r\n";
            await WriteAsync(stream, challenge);
            var retry = await ReadRequestAsync(stream);
            if (retry is null)
            {
                return;
            }

            var auth = ExtractHeader(retry, "Authorization");
            behavior.Capture!.AuthorizationHeader = auth;
            if (auth is null || !auth.StartsWith("Digest ", StringComparison.Ordinal))
            {
                return; // probe sent no auth → connection closes, probe sees EOF/failure
            }

            await WriteAsync(stream, "RTSP/1.0 200 OK\r\nCSeq: 2\r\nContent-Type: application/sdp\r\nContent-Length: 0\r\n\r\n");
            return;
        }

        await WriteAsync(stream, behavior.FirstResponse);
    }

    private static async Task<string?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[2048];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
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

    private static string? ExtractHeader(string request, string name)
        => request.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            ?.Substring(name.Length + 1)
            .Trim();

    private static string ExtractCSeq(string request)
        => ExtractHeader(request, "CSeq") ?? "1";

    private static async Task WriteAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ExtractCnonce(string header)
    {
        var marker = "cnonce=\"";
        var start = header.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return header.Substring(start, header.IndexOf('"', start) - start);
    }

    private static string Md5Hex(string input)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
