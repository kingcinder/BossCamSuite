namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Live connection state of the local BossCamService, shown as a color-coded
/// indicator in the top bar and status strip.
/// </summary>
public enum ServiceConnectionStatus
{
    /// <summary>Service is up and /api/health reports ok — green.</summary>
    Online,

    /// <summary>Checking, or an auto-start attempt is in progress — amber.</summary>
    Starting,

    /// <summary>Service is unreachable or unhealthy — red.</summary>
    Offline
}
