namespace BossCam.Core.Utilities;

/// <summary>
/// Write-side directory allow-list for <c>POST /api/recordings/export</c>. The old
/// <c>ExportClipAsync</c> accepted any caller-supplied <c>OutputPath</c> and would
/// <c>Directory.CreateDirectory</c> + write the clip anywhere the process could, then
/// interpolated that path unescaped into the ffmpeg command line (closed in tandem with the
/// ArgumentList conversion in <c>RecordingService</c>). Bounding the destination to an
/// operator-configured <see cref="BossCamRuntimeOptions.ExportAllowedDirectories"/> closes the
/// arbitrary-file-write; containment is segment-aware via <see cref="PathContainment.IsWithin"/>,
/// so a sibling like <c>/mnt/exports-evil</c> cannot masquerade as inside <c>/mnt/exports</c>.
/// </summary>
public static class ExportOutputPathPolicy
{
    /// <summary>
    /// Returns true when <paramref name="outputPath"/> resolves inside one of the configured
    /// export roots. <paramref name="reason"/> carries a human-readable rejection reason on
    /// failure (empty on success). Unlike <see cref="FirmwarePathPolicy.IsAllowed"/> this does
    /// NOT require the file to already exist — exports create the destination file.
    /// </summary>
    public static bool IsAllowed(string? outputPath, BossCamRuntimeOptions options, out string reason)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            reason = "OutputPath is required.";
            return false;
        }

        var roots = options.ExportAllowedDirectories
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .ToList();
        if (roots.Count == 0)
        {
            reason = "No export directory is configured. Set BossCam:ExportAllowedDirectories to allow clip exports.";
            return false;
        }

        var full = Path.GetFullPath(outputPath);
        foreach (var root in roots)
        {
            if (PathContainment.IsWithin(root, full))
            {
                reason = string.Empty;
                return true;
            }
        }

        reason = "OutputPath must resolve inside a configured export directory (BossCam:ExportAllowedDirectories).";
        return false;
    }
}
