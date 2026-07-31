using BossCam.Contracts;

namespace BossCam.Core.Utilities;

/// <summary>
/// Centralizes the candidate HTTP ports used to reach a camera's NetSDK REST surface.
///
/// Discovery records the port a camera answered during brand detection (often an
/// ONVIF/media port such as 8888/8899), while the NetSDK REST control and snapshot
/// planes actually listen on 80 — live-verified on 5523-W units where deviceInfo and
/// snapShot return 200 on :80 but transport-fail on the recorded ONVIF port. Callers
/// (control adapters, snapshot pumps, storage/snapshot endpoints) therefore probe the
/// recorded port first and fall back to 80.
/// </summary>
public static class NetSdkPortCandidates
{
    /// <summary>
    /// Ordered ports to try for a device whose recorded port is <paramref name="port"/>:
    /// the recorded port first when it is a valid, non-80 port, then 80 as fallback.
    /// A non-positive or 80 recorded port yields a single-element list so the common
    /// cases (port 80, or an unset/zero port that defaults to 80) never probe twice.
    /// </summary>
    public static int[] For(int port)
        => port > 0 && port != 80 ? new[] { port, 80 } : new[] { port > 0 ? port : 80 };

    /// <summary>
    /// True when <paramref name="candidatePort"/> is the :80 fallback element of <see cref="For(int)"/>
    /// for a device whose recorded port is <paramref name="devicePort"/> — i.e. the recorded port is a
    /// valid, non-80 port and the candidate is exactly 80. Equivalent to "the candidate list has two
    /// elements and this is the second one", but also correctly returns false for ports that are not
    /// candidates at all. Callers use this to rank/name the :80 fallback descriptor below the
    /// recorded-port one.
    /// </summary>
    public static bool IsFallback(int devicePort, int candidatePort)
        => devicePort > 0 && devicePort != 80 && candidatePort == 80;

    /// <summary>
    /// Runs <paramref name="probe"/> for each candidate port from <see cref="For(int)"/> in order
    /// (recorded port first, then 80) and returns the first true result. Used by reachability
    /// probes (connectivity watchdog, diagnostics) so a device whose recorded port is the
    /// ONVIF/media port is still judged reachable when the NetSDK REST surface on :80 answers.
    /// Short-circuits on the first success and never probes the :80 fallback when the recorded
    /// port already answers. The probe delegate must swallow its own per-port exceptions (the
    /// helper treats a thrown probe as a failed candidate).
    /// </summary>
    public static async Task<bool> AnyPortSucceedsAsync(
        int recordedPort,
        Func<int, CancellationToken, Task<bool>> probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        foreach (var port in For(recordedPort))
        {
            if (await probe(port, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly TimeSpan DefaultSnapshotProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Returns the first snapshot-kind descriptor whose URL is reachable, probing candidates in
    /// ascending rank order — the adapters emit the recorded-port descriptor (rank 25) before
    /// the :80 fallback (rank 26), so a device whose recorded port is dead self-heals to the
    /// :80 NetSDK REST surface. Used by single-pick consumers
    /// (<see cref="BossCam.Core.RecordingService"/> snapshot selection, highlight-board tiles)
    /// so the rank-26 fallback descriptor is genuinely consumed instead of FirstOrDefault.
    /// Returns null when no snapshot descriptor answers (transport failure, non-success status,
    /// and — in JPEG mode — a non-JPEG body all move to the next candidate).
    /// <para>
    /// <paramref name="requireJpeg"/> selects the success criterion. <c>true</c> (default — the
    /// recording path, which feeds ffmpeg) requires an actual JPEG body (SOI magic + &gt;500 bytes).
    /// <c>false</c> (the highlight-board tile path) is a lightweight headers-only reachability
    /// check that accepts any 2xx without downloading the body, since tiles refresh repeatedly
    /// and only need a reachable URL — the full-body cost only matters where a real JPEG is
    /// consumed.
    /// </para>
    /// <para>
    /// <paramref name="probeTimeout"/> bounds each candidate probe; default 4s. Latency-sensitive
    /// consumers that run repeatedly (highlight-board tiles on every refresh) pass a shorter
    /// bound so a fully-offline camera cannot stall the whole call for two full timeouts.
    /// </para>
    /// </summary>
    public static async Task<VideoSourceDescriptor?> FirstReachableSnapshotAsync(
        IHttpClientFactory httpClientFactory,
        IEnumerable<VideoSourceDescriptor> sources,
        CancellationToken cancellationToken,
        TimeSpan? probeTimeout = null,
        bool requireJpeg = true)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        var candidates = sources
            .Where(static s => s.Metadata.TryGetValue("kind", out var kind)
                && kind.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static s => s.Rank)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (await SnapshotUrlAnswersAsync(
                    httpClientFactory, candidate.Url, cancellationToken,
                    probeTimeout ?? DefaultSnapshotProbeTimeout, requireJpeg))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<bool> SnapshotUrlAnswersAsync(
        IHttpClientFactory httpClientFactory,
        string url,
        CancellationToken cancellationToken,
        TimeSpan probeTimeout,
        bool requireJpeg)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(probeTimeout);
            using var client = httpClientFactory.CreateClient("probe");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            if (!requireJpeg)
            {
                return true; // headers-only reachability — don't download the JPEG body per refresh
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            return bytes.Length > 500 && bytes[0] == 0xFF && bytes[1] == 0xD8;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false; // transport failure — try the next candidate
        }
    }
}
