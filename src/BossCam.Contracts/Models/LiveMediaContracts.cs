using System.Text.Json.Serialization;

namespace BossCam.Contracts;

/// <summary>Backend-selected live output options exposed to browser and native players.</summary>
public sealed record LiveMediaManifest
{
    public Guid DeviceId { get; init; }
    public string SourceCodec { get; init; } = "unknown";
    public string SourceRole { get; init; } = "main";
    public string DecisionReason { get; init; } = string.Empty;
    public LiveMediaModeContract PreferredMode { get; init; } = LiveMediaModeContract.Snapshot;
    public IReadOnlyList<LiveMediaModeContract> FallbackModes { get; init; } = [];
    public bool SnapshotAvailable { get; init; }
    public string MjpegUrl { get; init; } = string.Empty;
    public string H264Fmp4Url { get; init; } = string.Empty;
    public string HevcFmp4Url { get; init; } = string.Empty;
    public string MpegTsUrl { get; init; } = string.Empty;
    public string SnapshotUrl { get; init; } = string.Empty;

    /// <summary>
    /// Credentialed direct-RTSP URL for the negotiated source, populated only for native
    /// clients (client=native) whose RTSP probe succeeded. The desktop's local ffmpeg can
    /// connect straight to the camera — no server HTTP hop, no fragment-alignment delay —
    /// and the negotiated HTTP modes below remain the automatic fallback ladder.
    /// Empty for browser manifests.
    /// </summary>
    public string RtspUrl { get; init; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LiveMediaModeContract
{
    HevcFmp4,
    H264Fmp4,
    H264MpegTs,
    Mjpeg,
    Snapshot
}
