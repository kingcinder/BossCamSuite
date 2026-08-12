using BossCam.Contracts;

namespace BossCam.Core;

/// <summary>
/// Pure, testable decision logic for the autonomous camera-recovery worker. Keeping the
/// selection rules here (instead of inline in the worker) lets unit tests pin the identity
/// normalization, enrolled-skip, cooldown, and strongest-signal ranking without spinning up
/// processes or the service host.
/// </summary>
public static class RecoveryAutoSelection
{
    /// <summary>
    /// Canonicalize a camera identity (serial / MAC / esee id) for comparison:
    /// strips a leading "JA" (AP SSID IPC… ↔ serial JA… ↔ deviceInfo Z7C… all map to the same
    /// unit), uppercases, and drops ":" / "-" separators. Examples:
    ///   "JAZ7C34781620744" → "Z7C34781620744"
    ///   "Z7C34781620744"   → "Z7C34781620744"
    ///   "9C:A3:A9:BC:6F:EC" → "9CA3A9BC6FEC"
    /// </summary>
    public static string NormalizeIdentity(string value)
    {
        var upper = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (upper.StartsWith("JA", StringComparison.Ordinal) && upper.Length > 2)
        {
            upper = upper[2..];
        }

        return upper.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Pick the AP to recover next: ANY visible camera AP not inside the per-serial cooldown,
    /// ranked by strongest signal. Returns null when every visible AP is cooling down.
    ///
    /// Deliberately NO enrolled-skip: a camera broadcasting its AP (IPCZ7C34…) is by definition
    /// NOT on the LAN — AP mode means it dropped off the network after a factory reset — so its
    /// existing Suite record (serial/MAC/esee) is stale evidence, not a reason to skip. Skipping
    /// enrolled identities would silently refuse to auto-recover exactly the cameras the operator
    /// reset (both live-verified 2026-08-11 units had pre-existing records, e.g. "Driveway").
    /// Cooldown + one-at-a-time serialization are the real safety rails.
    /// </summary>
    public static CameraApInfo? PickCandidate(
        IReadOnlyCollection<CameraApInfo> aps,
        IReadOnlyDictionary<string, DateTimeOffset> cooldown,
        DateTimeOffset now,
        TimeSpan cooldownWindow)
    {
        return aps
            .Where(ap => !cooldown.TryGetValue(NormalizeIdentity(ap.Serial), out var last) || now - last > cooldownWindow)
            .OrderByDescending(ap => int.TryParse(ap.Signal, out var s) ? s : 0)
            .FirstOrDefault();
    }
}
