namespace BossCam.Core.Utilities;

/// <summary>
/// Segment-aware path containment used by every operator-configured directory allow-list
/// (firmware uploads, clip exports, recordings download). A naive string
/// <c>StartsWith</c> against the root would accept a sibling like
/// <c>/mnt/recordings-evil/x.mp4</c> as "inside" <c>/mnt/recordings</c> — this helper
/// requires a real path-segment boundary via <see cref="Path.GetRelativePath"/> and rejects
/// <c>..</c> escapes and differently-rooted (absolute) relative results.
/// </summary>
public static class PathContainment
{
    /// <summary>
    /// Returns true when <paramref name="candidate"/> resolves inside <paramref name="root"/>
    /// (segment-aware). Cross-drive / unrooted-relative inputs return false rather than
    /// throwing, so callers never 500 on an attacker-crafted path.
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        try
        {
            // Path.GetRelativePath throws ArgumentException on Windows when the two paths are on
            // different drives (root C:\fw, candidate D:\x.bin). Treat that as not-contained.
            var relative = Path.GetRelativePath(root, candidate);
            return relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
