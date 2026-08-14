using System.Text.RegularExpressions;
using BossCam.Contracts;

namespace BossCam.Core;

/// <summary>
/// Interprets transport candidates into one playable-source decision shared by recording,
/// live streaming, highlights, and failover. Adapters produce evidence; this module owns
/// the meaning of main, sub, and snapshot fallbacks.
/// </summary>
public static class PlayableSourcePolicy
{
    public static PlayableSourceDecision Resolve(
        IEnumerable<VideoSourceDescriptor> sources,
        string preferredStream = "main")
    {
        ArgumentNullException.ThrowIfNull(sources);
        var candidates = sources.ToList();
        var main = candidates
            .Where(IsRtsp)
            .Where(static source => !IsSub(source))
            .OrderBy(static source => source.Rank)
            .FirstOrDefault(IsMainHint)
            ?? candidates.Where(IsRtsp).Where(static source => !IsSub(source)).OrderBy(static source => source.Rank).FirstOrDefault()
            // Bubble FLV is an auth-free live stream (proven on 5523-W); when RTSP is
            // unavailable — e.g. locked cameras with wrong credentials — bubble provides
            // a passwordless H.265 stream that can replace the missing RTSP main.
            ?? candidates.Where(static s => s.Kind == TransportKind.BubbleFlv && !IsSub(s)).OrderBy(static s => s.Rank).FirstOrDefault();
        var sub = candidates
            .Where(IsRtsp)
            .Where(IsSub)
            .OrderBy(static source => source.Rank)
            .FirstOrDefault();
        var snapshot = candidates
            .Where(IsSnapshot)
            .OrderBy(static source => source.Rank)
            .FirstOrDefault();

        var normalizedPreference = NormalizePreference(preferredStream);
        var preferred = normalizedPreference switch
        {
            "sub" => sub ?? main ?? snapshot,
            "snapshot" => snapshot ?? main ?? sub,
            _ => main ?? sub ?? snapshot
        };

        var isDegraded = preferred is not null && (main is null || normalizedPreference != "main" || preferred != main);
        var reason = main is not null
            ? normalizedPreference switch
            {
                "sub" when sub is not null => "Sub stream selected by preference.",
                "snapshot" when snapshot is not null => "Snapshot selected by preference.",
                _ => "Main RTSP source selected."
            }
            : preferred switch
            {
                { Kind: TransportKind.LanRest } => "No main RTSP source is available; using snapshot fallback.",
                { } source when IsSub(source) => "No main RTSP source is available; using sub stream fallback.",
                null => "No playable source is available.",
                _ => "No main RTSP source is available; using the best remaining source."
            };

        return new PlayableSourceDecision
        {
            Preferred = preferred,
            Main = main,
            Sub = sub,
            Snapshot = snapshot,
            IsDegraded = isDegraded,
            Reason = reason
        };
    }

    /// <summary>Returns candidates in the shared order used by failover and diagnostics.</summary>
    public static IReadOnlyCollection<VideoSourceDescriptor> BuildProbeOrder(IEnumerable<VideoSourceDescriptor> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sources
            .Select((source, index) => (source, index, order: ProbeOrder(source)))
            .OrderBy(static item => item.order)
            .ThenBy(static item => item.source.Rank)
            .ThenBy(static item => item.index)
            .Select(static item => item.source)
            .ToList();
    }

    public static bool IsMain(VideoSourceDescriptor source)
        => IsRtsp(source) && !IsSub(source) && IsMainHint(source);

    public static bool IsSub(VideoSourceDescriptor source)
    {
        var url = source.Url ?? string.Empty;
        if (source.Metadata.TryGetValue("stream", out var stream)
            && stream.Equals("sub", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (source.Metadata.TryGetValue("highRes", out var highRes)
            && highRes.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return url.Contains("ch0_1", StringComparison.OrdinalIgnoreCase)
            || HasPathSegment12(url)
            || url.Contains("subtype=1", StringComparison.OrdinalIgnoreCase)
            || url.Contains("PROFILE_001", StringComparison.OrdinalIgnoreCase)
            // Word-boundary match, never a raw substring: a generic MAIN candidate like
            // "Generic main /cam/realmonitor?channel=1&subtype=0" contains "sub" inside
            // "subtype" and was being misclassified as a sub stream (live evidence from the
            // 5523-W at 10.0.0.169 — ffmpeg got fed the Dahua main URL and produced 0 bytes).
            || (source.DisplayName is not null
                && SubWordRegex.IsMatch(source.DisplayName));
    }

    /// <summary>
    /// Dahua-style sub-stream marker: a bare <c>/12</c> path segment (e.g.
    /// <c>rtsp://camera:554/12</c>). Must match the <em>path</em>, never a raw substring of the
    /// whole URL: <c>rtsp://12.0.0.5/…</c> contains "/12" inside the authority (<c>//12</c>) and
    /// was silently misclassifying cameras on a 12.x subnet as sub streams — the same false
    /// positive that trips on <c>rtsp://127.0.0.1/…</c> (via <c>//127</c>), which surfaced in the
    /// degraded-snapshot re-promotion unit test.
    /// </summary>
    private static bool HasPathSegment12(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(segment => segment.Equals("12", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex SubWordRegex = new(
        @"\bsub\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public static bool IsSnapshot(VideoSourceDescriptor source)
        => source.Metadata.TryGetValue("kind", out var kind)
           && kind.Equals("snapshot", StringComparison.OrdinalIgnoreCase);

    private static bool IsRtsp(VideoSourceDescriptor source)
        => source.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp;

    private static bool IsMainHint(VideoSourceDescriptor source)
    {
        var url = source.Url ?? string.Empty;
        if (source.Metadata.TryGetValue("highRes", out var highRes)
            && highRes.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (source.Metadata.TryGetValue("stream", out var stream)
            && stream.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return url.Contains("ch0_0", StringComparison.OrdinalIgnoreCase)
            || url.Contains("subtype=0", StringComparison.OrdinalIgnoreCase)
            || url.Contains("PROFILE_000", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/11", StringComparison.OrdinalIgnoreCase);
    }

    private static int ProbeOrder(VideoSourceDescriptor source)
    {
        if (IsMain(source)) return 0;
        if (IsSub(source) && source.Kind == TransportKind.Rtsp) return 1;
        if (source.Kind == TransportKind.OnvifRtsp) return 2;
        if (source.Kind is TransportKind.RtspOverHttp) return 3;
        if (source.Kind is TransportKind.FlvOverHttp or TransportKind.BubbleFlv) return 4;
        if (source.Kind is TransportKind.Rtmp) return 5;
        if (IsSnapshot(source)) return 6;
        if (source.Kind is TransportKind.EseeJuanP2P or TransportKind.Kp2p or TransportKind.LinkVision) return 7;
        return 8;
    }

    private static string NormalizePreference(string? preferredStream)
        => preferredStream?.Trim().ToLowerInvariant() switch
        {
            "sub" or "secondary" or "12" => "sub",
            "snapshot" or "jpeg" or "still" => "snapshot",
            _ => "main"
        };
}

public sealed record PlayableSourceDecision
{
    public VideoSourceDescriptor? Preferred { get; init; }
    public VideoSourceDescriptor? Main { get; init; }
    public VideoSourceDescriptor? Sub { get; init; }
    public VideoSourceDescriptor? Snapshot { get; init; }
    public bool IsDegraded { get; init; }
    public string Reason { get; init; } = string.Empty;
}
