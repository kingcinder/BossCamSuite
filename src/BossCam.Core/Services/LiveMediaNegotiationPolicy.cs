namespace BossCam.Core;

/// <summary>Output modes the service can deliver to browser and native players.</summary>
public enum LiveMediaMode
{
    HevcFmp4,
    H264Fmp4,
    H264MpegTs,
    Mjpeg,
    Snapshot
}

/// <summary>Evidence collected before selecting a live output mode.</summary>
public sealed record LiveMediaSourceFacts
{
    public bool IsRtspPlayable { get; init; }
    public string? MainCodec { get; init; }
    public bool SnapshotAvailable { get; init; }
}

public sealed record LiveMediaDecision
{
    public LiveMediaMode PreferredMode { get; init; }
    public IReadOnlyList<LiveMediaMode> FallbackModes { get; init; } = [];
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Keeps codec and degradation decisions explicit and testable. It never advertises direct
/// HEVC playback when RTSP evidence is absent, and always retains the snapshot safety net.
/// </summary>
public static class LiveMediaNegotiationPolicy
{
    public static LiveMediaDecision Resolve(LiveMediaSourceFacts facts, bool browserSupportsHevc)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var modes = new List<LiveMediaMode>();
        var codec = facts.MainCodec?.Trim().ToLowerInvariant();

        if (facts.IsRtspPlayable)
        {
            if (codec is "hevc" or "h265" && browserSupportsHevc)
            {
                modes.Add(LiveMediaMode.HevcFmp4);
            }

            // H.264 is a bounded compatibility transcode. fMP4 is the browser-safe output;
            // native clients use the same bounded H.264 representation as MPEG-TS.
            modes.Add(LiveMediaMode.H264Fmp4);
            modes.Add(LiveMediaMode.H264MpegTs);
            modes.Add(LiveMediaMode.Mjpeg);
        }
        else
        {
            // No RTSP playability evidence means no direct or transcoded RTSP claim. MJPEG can
            // still be supplied by the authenticated snapshot/decoder path.
            modes.Add(LiveMediaMode.Mjpeg);
        }

        if (facts.SnapshotAvailable)
        {
            modes.Add(LiveMediaMode.Snapshot);
        }

        if (modes.Count == 0)
        {
            modes.Add(LiveMediaMode.Snapshot);
        }

        return new LiveMediaDecision
        {
            PreferredMode = modes[0],
            FallbackModes = modes,
            Reason = facts.IsRtspPlayable
                ? codec is "hevc" or "h265"
                    ? browserSupportsHevc ? "RTSP HEVC is playable; direct fMP4 selected." : "RTSP is playable but browser HEVC is unavailable; bounded H.264 compatibility selected."
                    : "RTSP is playable; bounded H.264 compatibility selected."
                : "RTSP playability is unavailable; using MJPEG/snapshot degradation."
        };
    }
}
