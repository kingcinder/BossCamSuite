using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using BossCam.Infrastructure.Control;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Native NetSDK-family stream adapter (probe-driven) + full catalog settings surface.
/// The real 5523-W answers GET /NetSDK/System/deviceInfo on :80; a bare-ONVIF record
/// (no model / Esee identity — exactly the live 0654e903 case) proves the family via
/// that probe, so the adapter emits the live-proven HEVC paths (ch0_0.264 main /
/// ch0_1.264 sub) at canonical ranks and stamps device.Metadata["nativeNetSdk"] so
/// MultiBrandHighResTransportAdapter suppresses its generic RTSP guesses.
/// </summary>
public sealed class NativeNetSdkAdapterTests
{
    [Fact]
    public async Task Native_Probe_Success_Emits_Proven_Sources_And_Marks_Family()
    {
        var device = NewDevice(port: 80);
        var adapter = NewStreamAdapter(request =>
        {
            if (request.RequestUri!.PathAndQuery.EndsWith("/NetSDK/System/deviceInfo", StringComparison.Ordinal))
            {
                return OkJson(DeviceInfoFixtureBody);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        Assert.Contains(sources, source => source.Url.EndsWith("/ch0_0.264", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Url.EndsWith("/ch0_1.264", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Metadata.TryGetValue("stream", out var stream) && stream == "main");
        Assert.Contains(sources, source => source.Metadata.TryGetValue("stream", out var stream) && stream == "sub");
        Assert.Contains(sources, source => source.Metadata.TryGetValue("kind", out var kind) && kind == "snapshot");
        // The family marker must propagate so MultiBrand suppresses its generic guesses.
        Assert.True(device.Metadata.ContainsKey("nativeNetSdk"));
    }

    [Fact]
    public async Task Native_Probe_Failure_Returns_Empty_And_Does_Not_Mark()
    {
        var device = NewDevice(port: 80);
        var adapter = NewStreamAdapter(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        Assert.Empty(sources);
        Assert.False(device.Metadata.ContainsKey("nativeNetSdk"));
    }

    [Fact]
    public async Task Native_Probe_Retries_With_Digest_When_Basic_Is_Challenged()
    {
        // The 5523-W's happytimesoft RTSP plane is Digest-auth (live-verified); some firmware
        // generations challenge the HTTP REST plane the same way. A Basic-only probe would
        // silently no-op (empty sources, no marker) and fall back to the generic RTSP guesses
        // the native adapter exists to replace. The probe must answer a 401 Digest challenge
        // with a computed Digest Authorization and retry once before giving up.
        var device = NewDevice(port: 80);
        var attempts = 0;
        string? retryAuthScheme = null;
        string? retryAuthParameter = null;
        var adapter = NewStreamAdapter(request =>
        {
            attempts++;
            if (attempts == 1)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(
                    new AuthenticationHeaderValue("Digest", "realm=\"test-realm\", qop=\"auth\", nonce=\"abcdef0123456789\", opaque=\"xyz\""));
                return challenge;
            }

            // The retry must carry a computed Digest Authorization header — a naive Basic
            // re-send would draw the same 401 from a real camera.
            retryAuthScheme = request.Headers.Authorization?.Scheme;
            retryAuthParameter = request.Headers.Authorization?.Parameter;
            return OkJson(DeviceInfoFixtureBody);
        });

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        Assert.NotEmpty(sources);
        Assert.True(device.Metadata.ContainsKey("nativeNetSdk"));
        Assert.True(attempts >= 2, "The Basic probe must be followed by a Digest retry after a 401 challenge.");
        Assert.Equal("Digest", retryAuthScheme);
        // The digest uri= directive and HA2 must use the HTTP/1.1 origin-form request-target
        // (path-only) — .NET HttpClient sends the path, not the absolute URL, so a strict server
        // validates HA2 against the path form. An absolute-form uri= would 401 the retry.
        Assert.Contains("uri=\"/NetSDK/System/deviceInfo\"", retryAuthParameter, StringComparison.Ordinal);
        Assert.DoesNotContain("uri=\"http://", retryAuthParameter, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_Challenge_With_Quoted_Qop_List_Picks_Auth_For_Response()
    {
        // RFC 2617 allows the challenge to advertise several qop values as a quoted list
        // (qop="auth,auth-int"). The client must choose ONE token — "auth" — for both the
        // MD5 response input and the outgoing qop= header; using the raw list verbatim
        // yields a wrong hash and an invalid header.
        var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Add(
            new AuthenticationHeaderValue("Digest", "realm=\"cam\", qop=\"auth,auth-int\", nonce=\"deadbeef\""));

        var ok = NativeNetSdkStreamAdapter.TryBuildDigestAuthorization(challenge, "/NetSDK/System/deviceInfo", "admin", string.Empty, out var header);

        Assert.True(ok);
        Assert.Contains(", qop=auth,", header, StringComparison.Ordinal);
        Assert.DoesNotContain("auth-int", header, StringComparison.Ordinal);
        // Response must use the single chosen token in the hash — recompute independently.
        var expectedResponse = Md5HexForTest($"{Md5HexForTest("admin:cam:")}:deadbeef:00000001:{ExtractCnonce(header)}:auth:{Md5HexForTest("GET:/NetSDK/System/deviceInfo")}");
        Assert.Contains($"response=\"{expectedResponse}\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_Challenge_With_Only_AuthInt_Bails()
    {
        // auth-int requires MD5 of the entity body in HA2, which this probe does not
        // implement — the adapter must refuse rather than compute a wrong response.
        var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Add(
            new AuthenticationHeaderValue("Digest", "realm=\"cam\", qop=\"auth-int\", nonce=\"deadbeef\""));

        var ok = NativeNetSdkStreamAdapter.TryBuildDigestAuthorization(challenge, "/NetSDK/System/deviceInfo", "admin", string.Empty, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Digest_Challenge_With_Unquoted_Qop_Answers_Auth_Not_Legacy_NoQop()
    {
        // RFC 2617 §3.2.1 allows directive values as either a quoted-string or a bare token.
        // Embedded HTTP servers (and happytimesoft-family RTSP/REST planes) commonly emit
        // qop=auth WITHOUT quotes. The old parser only matched key="...", so an unquoted qop
        // silently fell back to the legacy MD5(HA1:nonce:HA2) no-qop form — a server that
        // advertises qop is allowed to reject the no-qop response, silently no-oping the whole
        // native adapter. The hardened parser must read the unquoted token and answer qop=auth.
        var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Add(
            new AuthenticationHeaderValue("Digest", "realm=\"cam\", qop=auth, nonce=\"deadbeef\""));

        var ok = NativeNetSdkStreamAdapter.TryBuildDigestAuthorization(challenge, "/NetSDK/System/deviceInfo", "admin", string.Empty, out var header);

        Assert.True(ok);
        Assert.Contains(", qop=auth,", header, StringComparison.Ordinal);
        // The response must use the chosen qop token in the hash — recompute independently
        // over the SAME cnonce the adapter emitted.
        var expectedResponse = Md5HexForTest($"{Md5HexForTest("admin:cam:")}:deadbeef:00000001:{ExtractCnonce(header)}:auth:{Md5HexForTest("GET:/NetSDK/System/deviceInfo")}");
        Assert.Contains($"response=\"{expectedResponse}\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_Challenge_Guard_Skips_Needle_Inside_Another_Values_Quoted_String()
    {
        // The boundary guard must reject a needle match inside ANOTHER directive's value
        // (opaque="...nonce=...") and keep scanning until the real directive — otherwise a
        // hostile or sloppy opaque value could corrupt the parsed nonce and produce a wrong
        // digest response. The real nonce= is preceded by a separator; the impostor is
        // preceded by a quote and must be skipped.
        var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Add(
            new AuthenticationHeaderValue("Digest", "opaque=\"nonce=impostor\", realm=\"cam\", qop=\"auth\", nonce=\"deadbeef\""));

        var ok = NativeNetSdkStreamAdapter.TryBuildDigestAuthorization(challenge, "/NetSDK/System/deviceInfo", "admin", string.Empty, out var header);

        Assert.True(ok);
        // The nonce directive value must be the real one, not the impostor inside opaque.
        Assert.Contains("nonce=\"deadbeef\"", header, StringComparison.Ordinal);
        var expectedResponse = Md5HexForTest($"{Md5HexForTest("admin:cam:")}:deadbeef:00000001:{ExtractCnonce(header)}:auth:{Md5HexForTest("GET:/NetSDK/System/deviceInfo")}");
        Assert.Contains($"response=\"{expectedResponse}\"", header, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("algorithm=MD5-sess")]
    [InlineData("algorithm=\"MD5-sess\"")]
    [InlineData("algorithm=\"MD5-sess,MD5\"")]
    public void Digest_Challenge_With_Md5Sess_Algorithm_Bails(string algorithmDirective)
    {
        // RFC 2617 §3.2.2: MD5-sess derives a session key — HA1 = MD5(MD5(username:realm:password):nonce:cnonce) —
        // which this probe does not implement (same honesty rule as auth-int: never answer with a
        // wrong hash). A challenge advertising MD5-sess — quoted, unquoted, or inside a quoted
        // list — must be refused so the caller continues to the next candidate port instead of
        // retrying with a plain-MD5 response that a strict server would 401.
        var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Add(
            new AuthenticationHeaderValue("Digest", $"realm=\"cam\", qop=\"auth\", nonce=\"deadbeef\", {algorithmDirective}"));

        var ok = NativeNetSdkStreamAdapter.TryBuildDigestAuthorization(challenge, "/NetSDK/System/deviceInfo", "admin", string.Empty, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Digest_Challenge_With_Sha256_Algorithm_Answers_Sha256()
    {
        // RFC 7616: newer firmware may challenge with algorithm=SHA-256 (bare token form). The
        // probe must compute the response with SHA-256 — answering a SHA-256 challenge with a
        // plain-MD5 hash would be rejected by a strict server and silently no-op the adapter.
        var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Add(
            new AuthenticationHeaderValue("Digest", "realm=\"cam\", qop=\"auth\", nonce=\"deadbeef\", algorithm=SHA-256"));

        var ok = NativeNetSdkStreamAdapter.TryBuildDigestAuthorization(challenge, "/NetSDK/System/deviceInfo", "admin", string.Empty, out var header);

        Assert.True(ok);
        // The chosen algorithm must be echoed in the Authorization header (required by RFC 7616
        // when it is not the MD5 default) and the response must use SHA-256 throughout.
        Assert.Contains("algorithm=SHA-256", header, StringComparison.Ordinal);
        Assert.Contains(", qop=auth,", header, StringComparison.Ordinal);
        var expectedResponse = Sha256HexForTest($"{Sha256HexForTest("admin:cam:")}:deadbeef:00000001:{ExtractCnonce(header)}:auth:{Sha256HexForTest("GET:/NetSDK/System/deviceInfo")}");
        Assert.Contains($"response=\"{expectedResponse}\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_Challenge_With_Quoted_Sha256_Algorithm_Answers_Sha256()
    {
        // Same negotiation for the quoted-string form §3.2.1 allows.
        var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Add(
            new AuthenticationHeaderValue("Digest", "realm=\"cam\", qop=\"auth\", nonce=\"deadbeef\", algorithm=\"SHA-256\""));

        var ok = NativeNetSdkStreamAdapter.TryBuildDigestAuthorization(challenge, "/NetSDK/System/deviceInfo", "admin", string.Empty, out var header);

        Assert.True(ok);
        Assert.Contains("algorithm=SHA-256", header, StringComparison.Ordinal);
        var expectedResponse = Sha256HexForTest($"{Sha256HexForTest("admin:cam:")}:deadbeef:00000001:{ExtractCnonce(header)}:auth:{Sha256HexForTest("GET:/NetSDK/System/deviceInfo")}");
        Assert.Contains($"response=\"{expectedResponse}\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_Challenge_With_Algorithm_List_Prefers_Sha256()
    {
        // RFC 7616 allows the challenge to offer a quoted list (algorithm="SHA-256,MD5"). The
        // client must pick ONE token; the probe prefers the strongest supported (SHA-256) and
        // falls back to MD5 when that is all the server offers.
        var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Add(
            new AuthenticationHeaderValue("Digest", "realm=\"cam\", qop=\"auth\", nonce=\"deadbeef\", algorithm=\"SHA-256,MD5\""));

        var ok = NativeNetSdkStreamAdapter.TryBuildDigestAuthorization(challenge, "/NetSDK/System/deviceInfo", "admin", string.Empty, out var header);

        Assert.True(ok);
        Assert.Contains("algorithm=SHA-256", header, StringComparison.Ordinal);
        var expectedResponse = Sha256HexForTest($"{Sha256HexForTest("admin:cam:")}:deadbeef:00000001:{ExtractCnonce(header)}:auth:{Sha256HexForTest("GET:/NetSDK/System/deviceInfo")}");
        Assert.Contains($"response=\"{expectedResponse}\"", header, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("algorithm=SHA-512")]
    [InlineData("algorithm=\"SHA-256-sess\"")]
    [InlineData("algorithm=\"SHA-256-sess,SHA-256\"")]
    public void Digest_Challenge_With_Unsupported_Algorithm_Bails(string algorithmDirective)
    {
        // Same honesty rule as MD5-sess: an unknown algorithm (SHA-512) and the sess variants
        // (SHA-256-sess derives a session key this probe does not implement) are refused — even
        // when a list also offers SHA-256 — never answered with a wrong hash.
        var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Add(
            new AuthenticationHeaderValue("Digest", $"realm=\"cam\", qop=\"auth\", nonce=\"deadbeef\", {algorithmDirective}"));

        var ok = NativeNetSdkStreamAdapter.TryBuildDigestAuthorization(challenge, "/NetSDK/System/deviceInfo", "admin", string.Empty, out _);

        Assert.False(ok);
    }

    [Theory]
    [InlineData("userhash=true")]
    [InlineData("userhash=\"true\"")]
    public void Digest_Challenge_With_Userhash_Advertised_Bails(string userhashDirective)
    {
        // RFC 7616 §3.4.3: userhash=true means the username directive must carry
        // SHA-256(user:realm), not the plaintext — not implemented, so refuse rather than answer
        // with a plaintext username a userhash server would reject.
        var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Add(
            new AuthenticationHeaderValue("Digest", $"realm=\"cam\", qop=\"auth\", nonce=\"deadbeef\", {userhashDirective}"));

        var ok = NativeNetSdkStreamAdapter.TryBuildDigestAuthorization(challenge, "/NetSDK/System/deviceInfo", "admin", string.Empty, out _);

        Assert.False(ok);
    }

    [Fact]
    public async Task Native_Probe_Falls_Back_To_80_When_Recorded_Onvif_Port_Transport_Fails()
    {
        // Discovery records the ONVIF/media port (8888) while the NetSDK REST plane answers on 80
        // (live-verified on 5523-W). The probe must cascade recorded port → 80 exactly like the
        // control adapters, and still emit sources + mark the family.
        var device = NewDevice(port: 8888);
        var adapter = NewStreamAdapter(request => request.RequestUri!.Port == 8888
            ? throw new HttpRequestException("connection refused on :8888")
            : OkJson(DeviceInfoFixtureBody));

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        Assert.NotEmpty(sources);
        Assert.True(device.Metadata.ContainsKey("nativeNetSdk"));
    }

    [Fact]
    public async Task Generic_Rtsp_Tier_Skipped_For_Marked_Device_When_Onvif_Unavailable()
    {
        // A bare-ONVIF 5523-W (DeviceType=ONVIF, no model — would resolve to GenericOnvif and pull
        // in the generic RTSP guesses) must NOT receive /stream1 /live /h264 guesses once the native
        // probe has proven the NetSDK family. The native adapter's ch0 paths are canonical instead.
        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.169",
            DeviceType = "ONVIF",
            LoginName = "admin",
            Password = string.Empty,
            Metadata = new Dictionary<string, string> { ["nativeNetSdk"] = "true" }
        };

        var adapter = new MultiBrandHighResTransportAdapter(
            Options.Create(new BossCamRuntimeOptions()),
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            NullLogger<MultiBrandHighResTransportAdapter>.Instance);

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        // Strong pin: for a nativeNetSdk-marked GenericOnvif device whose ONVIF discovery fails
        // (503 stub — the real 5523-W answers ONVIF on :8888 in production, but this stub kills
        // it so the gate alone is under test), MultiBrand must emit NOTHING — not the generic
        // guesses, and not an empty shell either. The native NetSDK adapter owns the proven ch0
        // paths for marked devices; ONVIF-sourced URIs (when discovery succeeds) are unaffected.
        Assert.Empty(sources);
    }

    [Fact]
    public void DigestAuth_Computes_Rfc7616_Sha256_Reference_Response()
    {
        // RFC 7616 §3.9.1 worked example — the canonical SHA-256 digest test vector. A wrong
        // HA1/HA2/algorithm composition produces a different hash, so this pins the SHA-256
        // primitive AND that the same qop/nc/cnonce response composition as MD5 is used.
        var response = DigestAuth.ComputeResponse(
            username: "Mufasa",
            password: "Circle of Life",
            method: "GET",
            uri: "/dir/index.html",
            realm: "http-auth@example.org",
            nonce: "7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v",
            qop: "auth",
            cnonce: "f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ",
            nc: "00000001",
            algorithm: "SHA-256");

        Assert.Equal("753927fa0e85d155564e2e272a28d1802ca10daf4496794697cf8db5856cb6c1", response);
    }

    [Fact]
    public void DigestAuth_Computes_Rfc2617_Reference_Response()
    {
        // RFC 2617 §3.5 worked example — the canonical test vector for MD5-digest computation.
        // A wrong HA1/HA2/qop composition produces a different hash, so this pins the crypto.
        var response = DigestAuth.ComputeResponse(
            username: "Mufasa",
            password: "Circle Of Life",
            method: "GET",
            uri: "/dir/index.html",
            realm: "testrealm@host.com",
            nonce: "dcd98b7102dd2f0e8b11d0f600bfb0c093",
            qop: "auth",
            cnonce: "0a4f113b",
            nc: "00000001");

        Assert.Equal("6629fae49393a05397450978507c4ef1", response);
    }

    [Fact]
    public async Task Adapter_Gates_Unverified_Main_Path_But_Keeps_Verified_Sub()
    {
        // The RTSP digest handshake must confirm each ch0 path ACTUALLY accepts the computed
        // credentials BEFORE the adapter emits it. A main path the handshake cannot prove (e.g.
        // the RTSP plane rejects the credentials for that stream) must NOT be handed to the
        // player — the /11 alias (same stream) goes with it. The verified sub stream and its
        // /12 alias survive, as does the REST-plane snapshot (proven by the deviceInfo probe).
        var device = NewDevice(port: 80);
        var handshaken = new List<string>();
        var adapter = NewStreamAdapter(
            _ => OkJson(DeviceInfoFixtureBody),
            (host, port, path, user, password, ct) =>
            {
                handshaken.Add(path);
                return Task.FromResult(path == "ch0_1.264");
            });

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        // Both paths are handshaken once per probe.
        Assert.Contains(handshaken, p => p == "ch0_0.264");
        Assert.Contains(handshaken, p => p == "ch0_1.264");
        // Verified sub survives; unverified main and its alias are gated out.
        Assert.Contains(sources, s => s.Url.EndsWith("/ch0_1.264", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Url.EndsWith("/12", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, s => s.Url.EndsWith("/ch0_0.264", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, s => s.Url.EndsWith("/11", StringComparison.Ordinal));
        // The snapshot is a REST-plane artifact — unaffected by the RTSP handshake verdict.
        Assert.Contains(sources, s => s.Metadata.TryGetValue("kind", out var kind) && kind == "snapshot");
        // The family marker stays: the device IS NetSDK even when one RTSP path fails.
        Assert.True(device.Metadata.ContainsKey("nativeNetSdk"));
    }

    [Fact]
    public async Task Adapter_Gates_Unverified_Sub_Path_But_Keeps_Verified_Main()
    {
        var device = NewDevice(port: 80);
        var adapter = NewStreamAdapter(
            _ => OkJson(DeviceInfoFixtureBody),
            (host, port, path, user, password, ct) => Task.FromResult(path == "ch0_0.264"));

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        Assert.Contains(sources, s => s.Url.EndsWith("/ch0_0.264", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Url.EndsWith("/11", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, s => s.Url.EndsWith("/ch0_1.264", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, s => s.Url.EndsWith("/12", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adapter_Handshakes_Both_Paths_When_Probe_Used_Digest_Retry()
    {
        // The same gate applies when the REST plane itself needed a Digest retry to be proven.
        var device = NewDevice(port: 80);
        var attempts = 0;
        var handshaken = new List<string>();
        var adapter = NewStreamAdapter(
            request =>
            {
                attempts++;
                if (attempts == 1)
                {
                    var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    challenge.Headers.WwwAuthenticate.Add(
                        new AuthenticationHeaderValue("Digest", "realm=\"cam\", qop=\"auth\", nonce=\"deadbeef\""));
                    return challenge;
                }

                return OkJson(DeviceInfoFixtureBody);
            },
            (host, port, path, user, password, ct) =>
            {
                handshaken.Add(path);
                return Task.FromResult(true);
            });

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        Assert.Contains(handshaken, p => p == "ch0_0.264");
        Assert.Contains(handshaken, p => p == "ch0_1.264");
        Assert.NotEmpty(sources);
    }

    [Fact]
    public void LanDirectNetSdkRestAdapter_ReadEndpoints_Covers_Full_Catalog()
    {
        // The "full firmware settings surface": every catalog tag's read endpoints must be present
        // so the operator can see (and persist) all firmware toggles — not a hand-picked subset.
        var all = LanDirectNetSdkRestAdapter.ReadEndpoints.Values.SelectMany(static v => v).ToList();

        // System
        Assert.Contains(all, e => e.Contains("/NetSDK/System/deviceInfo", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/System/time/localTime", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/System/time/ntp", StringComparison.Ordinal));

        // Network — lan / pppoe / ddns / wireless / Ports / Port / Dns / Esee
        Assert.Contains(all, e => e.Contains("/NetSDK/Network/interfaces", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Network/interfaces/1/lan", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Network/interfaces/1/pppoe", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Network/interfaces/1/ddns", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Network/interfaces/1/wireless", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Network/Ports", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Network/Port/1", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Network/Dns", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Network/Esee", StringComparison.Ordinal));

        // Audio — input + encode channel surfaces
        Assert.Contains(all, e => e.Contains("/NetSDK/Audio/input/channels", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Audio/encode/channels", StringComparison.Ordinal));

        // Video — per-field image endpoints, overlays, privacy masks, motion status
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/input/channel/1/brightnessLevel", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/input/channel/1/contrastLevel", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/input/channel/1/saturationLevel", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/input/channel/1/sharpnessLevel", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/input/channel/1/hueLevel", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/input/channel/1/flip", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/input/channel/1/mirror", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/input/channel/1/privacyMask/1", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/encode/channel/101/deviceIDOverlays", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/encode/channel/101/textOverlay/1", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Video/motionDetection/channel/1/status", StringComparison.Ordinal));

        // IO — alarm input/output port status + trigger
        Assert.Contains(all, e => e.Contains("/NetSDK/IO/alarmInput/channel/1/portStatus", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/IO/alarmOutput/channel/1/portStatus", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/IO/alarmOutput/channel/1/trigger", StringComparison.Ordinal));

        // PTZ — channel config + control
        Assert.Contains(all, e => e.Contains("/NetSDK/PTZ/channel/1", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/PTZ/channel/1/control", StringComparison.Ordinal));

        // SDCard — status / search / playbackFLV / playbackByName / playbackControl / getFileByTime / getFileByName / captureFrame / format
        Assert.Contains(all, e => e.Contains("/NetSDK/SDCard/status", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/SDCard/media/search", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/SDCard/media/playbackFLV", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/SDCard/media/playbackByName", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/SDCard/media/playbackControl", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/SDCard/media/getFileByTime", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/SDCard/media/getFileByName", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/SDCard/media/captureFrame", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/SDCard/format", StringComparison.Ordinal));

        // Image — AF included (previously missing)
        Assert.Contains(all, e => e.Contains("/NetSDK/Image/AF", StringComparison.Ordinal));

        // Schedule — mined from HISISDK.h config commands 9/27 (HISI_DVR_GET/SET_SCHEDULECFG)
        Assert.Contains(all, e => e.Contains("/NetSDK/Schedule/channels", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Schedule/channel/1", StringComparison.Ordinal));

        // Wireless — mined from HISISDK.h HISI_WIRELESSINFO (HISI_ALARM_WIRELESS = 0x13)
        Assert.Contains(all, e => e.Contains("/NetSDK/Wireless/modules", StringComparison.Ordinal));

        // Alarm — mined from HISISDK.h alarm channel setup + message callback
        Assert.Contains(all, e => e.Contains("/NetSDK/Alarm/channels", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Alarm/channel/1", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Contains("/NetSDK/Alarm/messageCallback", StringComparison.Ordinal));
    }

    private const string DeviceInfoFixtureBody = "{\"serial\":\"SN123456\",\"model\":\"5523-w\",\"firmware\":\"v1.0.0\",\"mac\":\"AA:BB:CC:DD:EE:FF\",\"eseeId\":\"ESEE1234\"}";

    private static DeviceIdentity NewDevice(int port, string ip = "10.0.0.169")
        => new()
        {
            IpAddress = ip,
            Port = port,
            LoginName = "admin",
            Password = string.Empty,
            DeviceType = "ONVIF",
            Name = "5523-W"
        };

    private static NativeNetSdkStreamAdapter NewStreamAdapter(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        Func<string, int, string, string, string, CancellationToken, Task<bool>>? rtspHandshake = null)
        => new(
            Options.Create(new BossCamRuntimeOptions()),
            new StubHttpClientFactory(responder),
            NullLogger<NativeNetSdkStreamAdapter>.Instance,
            store: null,
            rtspHandshake: rtspHandshake ?? ((_, _, _, _, _, _) => Task.FromResult(true)));

    private static HttpResponseMessage OkJson(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static string ExtractCnonce(string header)
    {
        var marker = "cnonce=\"";
        var start = header.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return header.Substring(start, header.IndexOf('"', start) - start);
    }

    private static string Md5HexForTest(string input)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static string Sha256HexForTest(string input)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(responder)) { Timeout = TimeSpan.FromSeconds(5) };

        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(responder(request));
        }
    }
}
