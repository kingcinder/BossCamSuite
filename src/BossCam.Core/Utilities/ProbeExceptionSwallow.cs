using Microsoft.Extensions.Logging;

namespace BossCam.Core.Utilities;

/// <summary>
/// Centralizes the silent-catch debug-log idiom used by LAN brand probes.
///
/// LAN brand probes are intentionally try-catch-tolerated because they're diagnostic
/// "try-many-ports" loops. Without centralized logging, the catch blocks grew into
/// bare <c>catch {}</c> that swallowed even connection-refused / timeout / 401
/// without a trace-level breadcrumb for operators diagnosing why a known-branded
/// camera came back Unhandled.
///
/// Use this helper for probe-shaped loops (multi-port brand scans, ONVIF device-info
/// scans, SOAP exchanges). Use ordinary <c>try/catch</c> with <c>logger.LogError</c>
/// for control-flow paths where failure has user-visible consequences.
///
/// All overloads catch <see cref="Exception"/> — that's the explicit design choice
/// for a probe-tolerance helper. The ILogger param keeps silent catches traceable
/// when an operator flips the log level to Debug.
/// </summary>
public static class ProbeExceptionSwallow
{
    /// <summary>Synchronous probe: returns <c>default</c> on any exception. The helper's
    /// purpose is to centralize the silent-tolerance + debug-log idiom for probe-shaped loops;
    /// <see cref="OperationCanceledException"/> is intentionally swallowed here so callers
    /// (which iterate many ports/sites) don't have to add per-call try/catches to recover
    /// tolerance. If a specific caller needs to surface cancellation, it can do so at the
    /// <c>await</c> site (this helper preserves the original semantics that the round-17
    /// consolidation replaced).
    /// </summary>
    public static T? Run<T>(Func<T?> probe, ILogger? logger = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            LogDebug(logger, ex, description);
            return default;
        }
    }

    /// <summary>Async probe returning a value: returns <c>default</c> on any exception.
    /// See <see cref="Run{T}"/> for the swallowed-OCE rationale.
    /// </summary>
    public static async Task<T?> RunAsync<T>(Func<Task<T?>> probe, ILogger? logger = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        try
        {
            return await probe().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDebug(logger, ex, description);
            return default;
        }
    }

    /// <summary>Async void-shaped probe (e.g. fire-and-forget port scans): logs but does not throw.
    /// See <see cref="Run{T}"/> for the swallowed-OCE rationale.
    /// </summary>
    public static async Task RunAsync(Func<Task> probe, ILogger? logger = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        try
        {
            await probe().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDebug(logger, ex, description);
        }
    }

    private static void LogDebug(ILogger? logger, Exception ex, string? description)
    {
        if (logger is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(description))
        {
            logger.LogDebug(ex, "ProbeExceptionSwallow swallowed (no description provided)");
        }
        else
        {
            logger.LogDebug(ex, "ProbeExceptionSwallow swallowed: {Description}", description);
        }
    }
}
