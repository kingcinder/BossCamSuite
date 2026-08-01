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
