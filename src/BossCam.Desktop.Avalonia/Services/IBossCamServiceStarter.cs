namespace BossCam.Desktop.Avalonia.Services;

/// <summary>
/// Attempts to bring the local BossCamService up when the startup health check
/// fails, so the desktop GUI can self-heal instead of leaving the user staring
/// at an offline status. Implementations own any spawned service process and
/// must stop it on dispose.
/// </summary>
public interface IBossCamServiceStarter : IDisposable
{
    /// <summary>
    /// Tries to start the service using the configured strategies (systemd unit,
    /// then direct spawn of the published install or the dev checkout) and polls
    /// <paramref name="isHealthy"/> until the service reports healthy or the
    /// attempt times out.
    /// </summary>
    /// <returns>True when <paramref name="isHealthy"/> returned true.</returns>
    Task<bool> TryStartAsync(Func<Task<bool>> isHealthy, CancellationToken cancellationToken = default);
}
