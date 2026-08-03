using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Video;

/// <summary>
/// Probe-driven native NetSDK stream adapter for the Juan/GUANGZHOU 5523-W family.
///
/// Mines the protocol surface exposed by the vendor HiSilicon DVR SDK (HISISDK.h: the
/// config-command taxonomy GET_DEVICECFG=1 / GET_ENCODECFG=2 / GET_NETCFG=4 /
/// GET_PTZCFG=6 / GET_DETECTIONCFG=8 + the modern HTTP-REST twin of the same plane)
/// into a **probe-first** video transport: instead of guessing RTSP paths, the adapter
/// GETs <c>/NetSDK/System/deviceInfo</c> on the candidate HTTP ports and — when the
/// camera answers — emits the live-proven 5523-W HEVC sources (<c>ch0_0.264</c> main /
/// <c>ch0_1.264</c> sub, Digest-auth RTSP) and stamps
/// <c>device.Metadata["nativeNetSdk"] = "true"</c> so
/// <see cref="MultiBrandHighResTransportAdapter"/> suppresses its generic RTSP-guess tier
/// for the device. A bare-ONVIF 5523-W record (no model / Esee identity, exactly the
/// live 0654e903 case) is therefore handled by proven paths, not guesses.
///
/// Priority 4 runs ahead of MultiBrand (5) and StreamDescriptorAdapter (10); when the
/// probe fails the adapter returns an empty list and the generic machinery takes over
/// unchanged, so non-NetSDK brands are unaffected.
/// </summary>
public sealed class NativeNetSdkStreamAdapter(
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<NativeNetSdkStreamAdapter> logger,
    IApplicationStore? store = null,
    Func<string, int, string, string, string, CancellationToken, Task<bool>>? rtspHandshake = null) : IVideoTransportAdapter
{
    /// <summary>
    /// RTSP digest handshake: proves a ch0 path actually accepts the computed credentials
    /// before the adapter emits it (5523-W happytimesoft RTSP plane is Digest-auth, live-verified).
    /// Injected for unit tests; production resolves to <see cref="RtspDigestHandshake.ProbePathAsync"/>.
    /// </summary>
    private readonly Func<string, int, string, string, string, CancellationToken, Task<bool>> _rtspHandshake =
        rtspHandshake ?? (static (host, port, path, user, password, ct) => RtspDigestHandshake.ProbePathAsync(host, port, path, user, password, ct));

    public string Name => nameof(NativeNetSdkStreamAdapter);
    public TransportKind TransportKind => TransportKind.LanRest;
    public int Priority => 4; // ahead of MultiBrandHighResTransportAdapter (5) and StreamDescriptorAdapter (10)

    /// <summary>
    /// Metadata key stamped on the shared <see cref="DeviceIdentity"/> when the NetSDK
    /// family probe succeeds. The transport broker passes the same device instance to
    /// every adapter, so a later adapter (MultiBrand) observes the marker and suppresses
    /// its generic RTSP guesses. Internal for unit tests.
    /// </summary>
    internal const string NativeNetSdkMarker = NetSdkProbeVerdictCache.MarkerKey;

    /// <summary>TTL for a persisted probe verdict; zero/negative falls back to 30 minutes.</summary>
    private TimeSpan VerdictTtl => TimeSpan.FromMinutes(
        options.Value.NetSdkProbeCacheTtlMinutes > 0 ? options.Value.NetSdkProbeCacheTtlMinutes : 30);

    public async Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return [];
        }

        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;

        // Persisted-verdict fast path: a successful probe within the TTL means the family is
        // already proven — emit the cached proven sources WITHOUT a network probe. Every stream
        // request (sources/live-info/manifest/live.*) resolves through TransportBroker, so this
        // is the difference between one probe per camera per TTL and one probe per request.
        // The marker is already in device.Metadata from the store round-trip, so MultiBrand's
        // suppression gate observes it unchanged.
        if (NetSdkProbeVerdictCache.TryGetFreshProbePort(device, VerdictTtl, DateTimeOffset.UtcNow, out var cachedPort))
        {
            var mainKnown = NetSdkProbeVerdictCache.TryGetRtspVerdict(device, "main", out var mainOk);
            var subKnown = NetSdkProbeVerdictCache.TryGetRtspVerdict(device, "sub", out var subOk);
            if (mainKnown && subKnown)
            {
                logger.LogDebug("NetSDK verdict cache hit (port {Port}, ttl {Ttl}) for {Device}; skipping probe", cachedPort, VerdictTtl, device.DisplayName);
                return BuildSources(device, user, password, cachedPort, mainOk, subOk);
            }

            // The verdict predates the RTSP credential-confirmation round (persisted by the
            // previous deployment): backfill the two bounded handshakes — the REST plane stays
            // trusted, so NO deviceInfo probe re-runs — and persist the flags so the next
            // resolution is a pure cache hit. Never emit sources from a verdict that has not
            // confirmed the RTSP credentials.
            logger.LogDebug("NetSDK verdict cache hit but RTSP handshake flags missing for {Device}; backfilling", device.DisplayName);
            mainOk = await RunRtspHandshakeAsync(device, user, password, "ch0_0.264", cancellationToken);
            subOk = await RunRtspHandshakeAsync(device, user, password, "ch0_1.264", cancellationToken);
            NetSdkProbeVerdictCache.StampRtspVerdict(device, "main", mainOk);
            NetSdkProbeVerdictCache.StampRtspVerdict(device, "sub", subOk);
            await PersistVerdictAsync(device, cachedPort, cancellationToken);
            return BuildSources(device, user, password, cachedPort, mainOk, subOk);
        }

        var ports = NetSdkPortCandidates.For(device.Port);
        foreach (var port in ports)
        {
            using var client = httpClientFactory.CreateClient("default");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(2, options.Value.HttpTimeoutSeconds));
            var endpoint = $"http://{device.IpAddress}:{port}/NetSDK/System/deviceInfo";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);
            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    device.Metadata[NativeNetSdkMarker] = "true";
                    logger.LogDebug("NetSDK family proven via deviceInfo probe on :{Port} for {Device}", port, device.DisplayName);
                    var rtspMainOk = await RunRtspHandshakeAsync(device, user, password, "ch0_0.264", cancellationToken);
                    var rtspSubOk = await RunRtspHandshakeAsync(device, user, password, "ch0_1.264", cancellationToken);
                    NetSdkProbeVerdictCache.StampRtspVerdict(device, "main", rtspMainOk);
                    NetSdkProbeVerdictCache.StampRtspVerdict(device, "sub", rtspSubOk);
                    await PersistVerdictAsync(device, port, cancellationToken);
                    return BuildSources(device, user, password, port, rtspMainOk, rtspSubOk);
                }

                // The 5523-W's happytimesoft RTSP plane is Digest-auth (live-verified); some firmware
                // generations challenge the HTTP REST plane the same way. Answer a 401 Digest challenge
                // with a computed RFC-2617 response and retry ONCE on the same port before moving on —
                // a Basic-only probe would silently no-op the whole native adapter on a digest-only unit
                // and fall back to the generic RTSP guesses it exists to replace.
                // The digest uri= directive and HA2 must use the HTTP/1.1 origin-form request-target
                // (path-only): .NET HttpClient sends the path, not the absolute URL, so a strict server
                // validates HA2 against the path form.
                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && TryBuildDigestAuthorization(response, new Uri(endpoint).PathAndQuery, user, password, out var digestHeader))
                {
                    using var digestRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    digestRequest.Headers.Authorization = AuthenticationHeaderValue.Parse(digestHeader);
                    using var digestResponse = await client.SendAsync(digestRequest, cancellationToken);
                    if (digestResponse.IsSuccessStatusCode)
                    {
                        device.Metadata[NativeNetSdkMarker] = "true";
                        logger.LogDebug("NetSDK family proven via digest-auth deviceInfo probe on :{Port} for {Device}", port, device.DisplayName);
                        var rtspMainOk = await RunRtspHandshakeAsync(device, user, password, "ch0_0.264", cancellationToken);
                        var rtspSubOk = await RunRtspHandshakeAsync(device, user, password, "ch0_1.264", cancellationToken);
                        NetSdkProbeVerdictCache.StampRtspVerdict(device, "main", rtspMainOk);
                        NetSdkProbeVerdictCache.StampRtspVerdict(device, "sub", rtspSubOk);
                        await PersistVerdictAsync(device, port, cancellationToken);
                        return BuildSources(device, user, password, port, rtspMainOk, rtspSubOk);
                    }
                }

                // A non-2xx HTTP response (e.g. 404 on the recorded ONVIF port, 401 on bad creds)
                // does NOT prove the REST plane is absent elsewhere — the 5523-W records its ONVIF
                // port while /NetSDK answers on :80. Continue to the next candidate port; the probe
                // succeeds only when deviceInfo returns 2xx on some candidate.
                logger.LogDebug("deviceInfo answered {Status} on :{Port} for {Device}; trying next candidate", (int)response.StatusCode, port, device.DisplayName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Transport-level failure on the recorded port → try the :80 fallback. Cancellation
                // is not swallowed (codebase rule: a probe must not hide a cancelled caller).
                logger.LogDebug(ex, "NetSDK deviceInfo probe failed on :{Port} for {Device}", port, device.DisplayName);
            }
        }

        // Probe exhausted every candidate. A stale persisted verdict would keep MultiBrand's
        // generic fallback suppressed after the probe just failed — invalidate it so the fallback
        // tiers run on the next resolution and the verdict refreshes when the plane recovers.
        await InvalidateStaleVerdictAsync(device, cancellationToken);
        return [];
    }

    /// <summary>
    /// Runs the RTSP digest handshake for one ch0 path through the injected probe. Transport
    /// errors resolve to false (the path is not confirmed → not emitted); caller cancellation
    /// always propagates.
    /// </summary>
    private async Task<bool> RunRtspHandshakeAsync(DeviceIdentity device, string user, string password, string path, CancellationToken cancellationToken)
    {
        try
        {
            var rtspPort = device.RtspPort is > 0 ? device.RtspPort.Value : 554;
            // IpAddress is guaranteed non-empty by the guard in GetSourcesAsync.
            var ok = await _rtspHandshake(device.IpAddress!, rtspPort, path, user, password, cancellationToken);
            logger.LogDebug("RTSP digest handshake for {Path} on {Device}:{RtspPort} {Result}", path, device.DisplayName, rtspPort, ok ? "accepted" : "rejected");
            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "RTSP digest handshake failed for {Path} on {Device}", path, device.DisplayName);
            return false;
        }
    }

    /// <summary>
    /// Persists the probe verdict (marker + proven port + timestamp) into the device's store
    /// metadata so later source resolutions skip the probe while the verdict is fresh. Best-effort:
    /// a store failure must not fail source resolution, and cancellation is never swallowed.
    /// </summary>
    private async Task PersistVerdictAsync(DeviceIdentity device, int probePort, CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return;
        }

        try
        {
            await NetSdkProbeVerdictCache.SaveVerdictAsync(store, device, probePort, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to persist NetSDK probe verdict for {Device}; probe result still returned", device.DisplayName);
        }
    }

    /// <summary>
    /// Removes a stale persisted verdict after the probe failed, so the generic fallback tier
    /// (MultiBrand) is not suppressed by knowledge the probe just disproved. Short-circuits on
    /// the in-memory device (whose Metadata mirrors the store — the broker passes the loaded
    /// instance) so non-NetSDK cameras that never had a verdict pay NO store read on every
    /// failed probe. Best-effort; a store failure must not change the empty result, and
    /// cancellation is never swallowed.
    /// </summary>
    private async Task InvalidateStaleVerdictAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        if (store is null || !NetSdkProbeVerdictCache.HasVerdict(device))
        {
            return;
        }

        try
        {
            await NetSdkProbeVerdictCache.InvalidateAsync(store, device.Id, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to invalidate stale NetSDK probe verdict for {Device}", device.DisplayName);
        }
    }

    /// <summary>
    /// Computes an RFC-2617 Digest <c>Authorization</c> header for the given 401 challenge and
    /// request-target, so a digest-only REST plane can be proven without a credential-cache
    /// HttpClientHandler (which bypasses the injected factory and cannot be unit-tested through
    /// the stub). Returns false when the challenge lacks a Digest scheme / realm / nonce — the
    /// caller then treats the 401 like any other non-2xx (continue to the next candidate port).
    /// The hardened parsing and response composition live in <see cref="DigestChallenge"/>,
    /// shared with the RTSP handshake. Internal for unit tests.
    /// </summary>
    internal static bool TryBuildDigestAuthorization(HttpResponseMessage challenge, string requestUri, string user, string password, out string authorization)
    {
        authorization = string.Empty;
        var header = challenge.Headers.WwwAuthenticate.FirstOrDefault(
            static candidate => candidate.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
        if (header?.Parameter is null)
        {
            return false;
        }

        // HTTP/1.1 origin-form request-target: .NET HttpClient sends the path, not the absolute
        // URL, so a strict server validates HA2 against the path form.
        return DigestChallenge.TryBuildAuthorization(header.Parameter, "GET", requestUri, user, password, out authorization);
    }

    /// <summary>
    /// Live-proven 5523-W source table. A stream path is emitted ONLY when the RTSP digest
    /// handshake confirmed it accepts the computed credentials (<paramref name="mainVerified"/>
    /// / <paramref name="subVerified"/>); the alias of a rejected stream is gated with it. The
    /// REST-plane snapshot is proven by the deviceInfo probe and always emitted. Internal for
    /// unit tests.
    /// </summary>
    internal static IReadOnlyCollection<VideoSourceDescriptor> BuildSources(DeviceIdentity device, string user, string password, int probePort, bool mainVerified, bool subVerified)
    {
        var auth = $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@";
        var rtspPort = device.RtspPort is > 0 ? device.RtspPort.Value : 554;
        var sources = new List<VideoSourceDescriptor>();
        if (mainVerified)
        {
            sources.Add(new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = $"rtsp://{auth}{device.IpAddress}:{rtspPort}/ch0_0.264",
                Rank = 0,
                DisplayName = "NetSDK main HEVC (ch0_0.264)",
                Metadata = new Dictionary<string, string>
                {
                    ["stream"] = "main",
                    ["path"] = "/ch0_0.264",
                    ["auth"] = "digest",
                    ["encodeChannel"] = "101",
                    ["highRes"] = "true",
                    ["resolution"] = "2560x1920",
                    ["codec"] = "hevc",
                    ["nativeNetSdk"] = "true"
                }
            });
            sources.Add(new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = $"rtsp://{auth}{device.IpAddress}:{rtspPort}/11",
                Rank = 3,
                DisplayName = "NetSDK RTSP /11 alias",
                Metadata = new Dictionary<string, string>
                {
                    ["stream"] = "main",
                    ["path"] = "/11",
                    ["auth"] = "digest",
                    ["encodeChannel"] = "101",
                    ["highRes"] = "true"
                }
            });
        }

        if (subVerified)
        {
            sources.Add(new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = $"rtsp://{auth}{device.IpAddress}:{rtspPort}/ch0_1.264",
                Rank = 50,
                DisplayName = "NetSDK sub HEVC (ch0_1.264)",
                Metadata = new Dictionary<string, string>
                {
                    ["stream"] = "sub",
                    ["path"] = "/ch0_1.264",
                    ["auth"] = "digest",
                    ["encodeChannel"] = "102",
                    ["highRes"] = "false",
                    ["resolution"] = "704x480",
                    ["codec"] = "hevc",
                    ["nativeNetSdk"] = "true"
                }
            });
            sources.Add(new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = $"rtsp://{auth}{device.IpAddress}:{rtspPort}/12",
                Rank = 51,
                DisplayName = "NetSDK RTSP /12 alias",
                Metadata = new Dictionary<string, string>
                {
                    ["stream"] = "sub",
                    ["path"] = "/12",
                    ["auth"] = "digest",
                    ["encodeChannel"] = "102",
                    ["highRes"] = "false"
                }
            });
        }

        // NetSDK snapshot is often sub-resolution; keep for tiles. When the probe fell back to
        // :80 from a recorded ONVIF/media port, mark the fallback so consumers can rank it lower.
        var isFallback = NetSdkPortCandidates.IsFallback(device.Port, probePort);
        sources.Add(new VideoSourceDescriptor
        {
            Kind = TransportKind.LanRest,
            Url = $"http://{auth}{device.IpAddress}:{probePort}/NetSDK/Video/encode/channel/101/snapShot",
            Rank = isFallback ? 26 : 25,
            DisplayName = isFallback ? "JPEG snapshot (NetSDK :80 fallback)" : "JPEG snapshot (NetSDK)",
            Metadata = new Dictionary<string, string>
            {
                ["kind"] = "snapshot",
                ["contentType"] = "image/jpg",
                ["endpoint"] = "/NetSDK/Video/encode/channel/101/snapShot",
                ["highRes"] = "false",
                ["port"] = probePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["nativeNetSdk"] = "true"
            }
        });

        return sources;
    }
}
