namespace BossCam.Core.Utilities;

/// <summary>
/// Directory allow-list for firmware upload paths. Both <c>POST /api/firmware/register</c> and
/// the LanPrivateVendorHttpAdapter firmware-upload maintenance path used to accept any existing
/// file the caller named; an attacker with API access could then read arbitrary host files into
/// a camera's upload CGI (or the artifact catalog) — exfiltration by proxy. This policy bounds
/// the source to <see cref="BossCamRuntimeOptions.FirmwareArtifactDirectory"/> plus any
/// operator-configured <see cref="BossCamRuntimeOptions.FirmwareAllowedDirectories"/>.
/// </summary>
public static class FirmwarePathPolicy
{
    /// <summary>
    /// Returns true when <paramref name="filePath"/> exists and resolves inside one of the
    /// configured firmware roots. <paramref name="reason"/> carries a human-readable rejection
    /// reason on failure (empty on success). Path containment is segment-aware, so a sibling
    /// like <c>/opt/firmware-evil</c> cannot masquerade as inside <c>/opt/firmware</c>.
    /// </summary>
    public static bool IsAllowed(string? filePath, BossCamRuntimeOptions options, out string reason)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            reason = "FilePath must point to an existing firmware file.";
            return false;
        }

        var roots = options.FirmwareAllowedDirectories is { Length: > 0 }
            ? options.FirmwareAllowedDirectories
            : new[] { options.FirmwareArtifactDirectory };
        var normalizedRoots = roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .ToList();
        if (normalizedRoots.Count == 0)
        {
            reason = "No firmware directory is configured. Set BossCam:FirmwareArtifactDirectory or BossCam:FirmwareAllowedDirectories.";
            return false;
        }

        var full = Path.GetFullPath(filePath);
        foreach (var root in normalizedRoots)
        {
            if (IsWithinRoot(root, full))
            {
                reason = string.Empty;
                return true;
            }
        }

        reason = "Firmware file must live inside a configured firmware directory.";
        return false;
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        try
        {
            // Path.GetRelativePath throws ArgumentException on Windows when the two paths are on
            // different drives (root C:\fw, candidate D:\x.bin). Treat that as not-contained rather
            // than letting an unhandled exception 500 the register/upload endpoints.
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
