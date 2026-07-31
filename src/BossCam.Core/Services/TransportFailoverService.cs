using BossCam.Contracts;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

/// <summary>
/// Aggressive transport failover — given a device, tries every known source URL
/// in priority order and returns the first one that actually responds.
///
/// Fallback chain (in order):
///   1. RTSP main stream (ch0_0.264 or /11)
///   2. RTSP sub stream (ch0_1.264 or /12)
///   3. ONVIF RTSP (discovered profiles)
///   4. Bubble FLV live HTTP (when available)
///   5. NetSDK snapshot JPEG pump (last resort)
///   6. P2P/eSee tunnel (when device has EseeId)
///
/// Each transport is tested with a short timeout so a dead camera doesn't
/// block the whole chain for more than a few seconds.
/// </summary>
public sealed class TransportFailoverService(
    IApplicationStore store,
    TransportBroker transportBroker,
    IHttpClientFactory httpClientFactory,
    ILogger<TransportFailoverService> logger)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Resolve the best working source URL for a device by probing transports in
    /// priority order. Returns null only if ALL transports fail.
    /// </summary>
    public async Task<VideoSourceDescriptor?> ResolveBestSourceAsync(
        Guid deviceId,
        string preferredStream = "main",
        CancellationToken cancellationToken = default)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null || string.IsNullOrWhiteSpace(device.IpAddress))
            return null;

        // Get all known sources from transport adapters
        var sources = await transportBroker.GetSourcesAsync(deviceId, cancellationToken);

        if (sources.Count == 0)
        {
            logger.LogWarning("No transport sources found for {Device}; trying direct probe", device.DisplayName);
            return await ProbeFallbackSourcesAsync(device, cancellationToken);
        }

        // Build prioritized probe list: RTSP main first, then sub, then HTTP, then snap
        var probeList = new List<(VideoSourceDescriptor Source, int Order)>();

        foreach (var source in sources)
        {
            var order = source.Kind switch
            {
                TransportKind.Rtsp when source.Metadata.TryGetValue("stream", out var s) && s == "main" => 0,
                TransportKind.Rtsp when source.Url.Contains("ch0_0", StringComparison.OrdinalIgnoreCase) => 0,
                TransportKind.Rtsp when source.Url.EndsWith("/11", StringComparison.Ordinal) => 0,
                TransportKind.OnvifRtsp when source.Metadata.TryGetValue("stream", out var s2) && s2 == "main" => 1,
                TransportKind.Rtsp when source.Metadata.TryGetValue("stream", out var s3) && s3 == "sub" => 2,
                TransportKind.Rtsp when source.Url.Contains("ch0_1", StringComparison.OrdinalIgnoreCase) => 2,
                TransportKind.OnvifRtsp => 3,
                TransportKind.RtspOverHttp => 4,
                TransportKind.FlvOverHttp or TransportKind.BubbleFlv => 5,
                TransportKind.Rtmp => 6,
                TransportKind.LanRest when source.Metadata.TryGetValue("kind", out var k) && k == "snapshot" => 7,
                TransportKind.EseeJuanP2P or TransportKind.Kp2p or TransportKind.LinkVision => 8,
                _ => 9
            };
            probeList.Add((source, order));
        }

        // Sort by order, probe until one works
        foreach (var (source, _) in probeList.OrderBy(p => p.Order))
        {
            if (cancellationToken.IsCancellationRequested) break;

            var working = await ProbeTransportAsync(device, source, cancellationToken);
            if (working != null)
            {
                logger.LogInformation(
                    "Transport failover: {Device} → {Kind} {Url}",
                    device.DisplayName, source.Kind, TruncateCredentials(source.Url));
                return working;
            }
        }

        // Last resort: try snapshot-only
        logger.LogWarning("All transports failed for {Device}; no fallback available", device.DisplayName);
        return null;
    }

    /// <summary>
    /// Check if a specific source URL is currently reachable.
    /// </summary>
    public async Task<bool> IsTransportReachableAsync(
        DeviceIdentity device,
        VideoSourceDescriptor source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);

            if (source.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp)
            {
                // RTSP playability: a TCP connect to :554 only proves something is listening.
                // Require an RTSP OPTIONS handshake so a non-RTSP service on :554 isn't
                // misreported as an up/recordable stream (see RtspProbe).
                var (host, port) = ResolveRtspTarget(device, source);
                return await RtspProbe.ProbeAsync(host, port, cts.Token);
            }

            if (source.Kind is TransportKind.LanRest or TransportKind.FlvOverHttp or TransportKind.BubbleFlv)
            {
                using var client = httpClientFactory.CreateClient("probe");
                using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
            }

            return false;
        }
        catch (Exception ex)
        {
            // Per-transport probe failures are expected during failover (a dead camera fails
            // several candidates) — Debug so the chain outcome stays traceable without
            // spamming at Warning on every miss.
            logger.LogDebug(ex, "Transport reachability probe failed for {Device} kind={Kind} url={Url}", device.DisplayName, source.Kind, TruncateCredentials(source.Url));
            return false;
        }
    }

    private async Task<VideoSourceDescriptor?> ProbeTransportAsync(
        DeviceIdentity device,
        VideoSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);

            if (source.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp)
            {
                var (host, port) = ResolveRtspTarget(device, source);
                var playable = await RtspProbe.ProbeAsync(host, port, cts.Token);
                return playable ? source : null;
            }

            if (source.Kind is TransportKind.LanRest or TransportKind.FlvOverHttp or TransportKind.BubbleFlv)
            {
                using var client = httpClientFactory.CreateClient("probe");
                using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return source;
                return null;
            }

            // P2P / other: assume potentially working if listed
            return source;
        }
        catch (Exception ex)
        {
            // Probe failures per transport are expected in the failover chain; Debug-log with
            // credentials truncated so the failing candidate is identifiable in the logs.
            logger.LogDebug(ex, "Transport probe failed for {Device} kind={Kind} url={Url}", device.DisplayName, source.Kind, TruncateCredentials(source.Url));
            return null;
        }
    }

    private async Task<VideoSourceDescriptor?> ProbeFallbackSourcesAsync(
        DeviceIdentity device,
        CancellationToken cancellationToken)
    {
        // Try known RTSP URL patterns directly
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var pass = device.Password ?? string.Empty;
        var auth = $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@";

        var fallbackUrls = new List<VideoSourceDescriptor>
        {
            new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp, Url = $"rtsp://{auth}{device.IpAddress}:554/ch0_0.264", Rank = 0,
                DisplayName = "RTSP main (ch0_0.264 fallback)",
                Metadata = new Dictionary<string, string> { ["stream"] = "main" }
            },
            new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp, Url = $"rtsp://{auth}{device.IpAddress}:554/ch0_1.264", Rank = 50,
                DisplayName = "RTSP sub (ch0_1.264 fallback)",
                Metadata = new Dictionary<string, string> { ["stream"] = "sub" }
            },
        };
        // Snapshot last resort — try the recorded port first, then :80 when discovery recorded an
        // ONVIF/media port. NetSdkPortCandidates.For also normalizes a non-positive port to 80,
        // which the old `ip:{Port}` string form did not (it emitted a broken `ip:0` URL).
        var snapshotPorts = NetSdkPortCandidates.For(device.Port);
        foreach (var candidatePort in snapshotPorts)
        {
            var isFallback = NetSdkPortCandidates.IsFallback(device.Port, candidatePort);
            fallbackUrls.Add(new VideoSourceDescriptor
            {
                Kind = TransportKind.LanRest,
                Url = $"http://{device.IpAddress}:{candidatePort}/NetSDK/Video/encode/channel/101/snapShot",
                Rank = isFallback ? 26 : 25,
                DisplayName = isFallback ? "JPEG snapshot (:80 fallback)" : "JPEG snapshot (fallback)",
                Metadata = new Dictionary<string, string> { ["kind"] = "snapshot" }
            });
        }

        foreach (var source in fallbackUrls)
        {
            var working = await ProbeTransportAsync(device, source, cancellationToken);
            if (working != null) return working;
        }

        return null;
    }

    /// <summary>
    /// Resolves the RTSP host/port to probe for an RTSP-kind source: the URL's host/port when
    /// present, otherwise the device IP on :554. Keeps the reachability check pointed at the
    /// exact stream endpoint instead of assuming :554 for every descriptor.
    /// </summary>
    private static (string Host, int Port) ResolveRtspTarget(DeviceIdentity device, VideoSourceDescriptor source)
    {
        if (Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return (uri.Host, uri.Port > 0 ? uri.Port : 554);
        }

        return (device.IpAddress ?? string.Empty, 554);
    }

    private static string TruncateCredentials(string url)
    {
        try
        {
            var uri = new UriBuilder(url);
            if (!string.IsNullOrEmpty(uri.UserName))
            {
                uri.Password = "***";
                return uri.Uri.ToString();
            }
        }
        catch { /* ignore */ }
        return url;
    }
}
