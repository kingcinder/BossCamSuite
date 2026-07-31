namespace BossCam.Desktop.Avalonia.Models;

/// <summary>
/// Mirrors the JSON returned by <c>POST /api/recordings/stall-check</c>:
/// <c>{ "checked": true, "stalled": n, "autoRestart": bool }</c>.
/// </summary>
public sealed record StallCheckResult
{
    public bool Checked { get; init; }
    public int Stalled { get; init; }
    public bool AutoRestart { get; init; }
}
