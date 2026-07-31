namespace BossCam.Desktop.Avalonia.Models;

/// <summary>
/// Local mirror of the server's <c>HighlightBoardState</c> (which lives in
/// BossCam.Core, not BossCam.Contracts). The desktop app only references
/// BossCam.Contracts, so the /api/highlights JSON shape is re-declared here.
/// Property names use JSON web casing so <c>JsonSerializerOptions(Web)</c> maps
/// them directly.
/// </summary>
public sealed record HighlightBoardSnapshot
{
    public Guid? SelectedDeviceId { get; init; }
    public int SelectedIndex { get; init; }
    public string PreferredStream { get; init; } = "main";
    public HighlightTileSnapshot? Selected { get; init; }
    public IReadOnlyList<HighlightTileSnapshot> Tiles { get; init; } = [];
}

/// <summary>One camera tile on the highlight board.</summary>
public sealed record HighlightTileSnapshot
{
    public Guid DeviceId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public string? HardwareModel { get; init; }
    public string? ChannelName { get; init; }
    public string? LiveUrl { get; init; }
    public string? SnapshotUrl { get; init; }
    public string? RecordUrl { get; init; }
    public string? MainRtspUrl { get; init; }
    public string? SubRtspUrl { get; init; }
    public string? BubbleUrl { get; init; }
}
