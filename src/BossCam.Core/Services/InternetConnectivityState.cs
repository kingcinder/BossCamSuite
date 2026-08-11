namespace BossCam.Core;

/// <summary>Current reachability of the optional internet/cloud plane.</summary>
public enum InternetConnectivityStatus
{
    /// <summary>No probe result has arrived yet. Optional cloud transports remain fail-open during startup.</summary>
    Unknown,
    /// <summary>The configured internet probe answered successfully.</summary>
    Online,
    /// <summary>The internet probe has failed enough consecutive times to gate cloud transports.</summary>
    Offline,
    /// <summary>Explicit BossCam:OfflineMode disabled the cloud plane by policy.</summary>
    Disabled
}

/// <summary>
/// Shared, process-local WAN state. LAN camera traffic deliberately does not consume this state.
/// </summary>
public interface IInternetConnectivityState
{
    InternetConnectivityStatus Status { get; }
    DateTimeOffset LastChangedAt { get; }

    /// <summary>
    /// True for Unknown/Online, false for Offline/Disabled. Unknown is fail-open only during
    /// startup so existing online deployments do not lose cloud sources before the first probe.
    /// </summary>
    bool AllowsInternetTransports { get; }
}

/// <summary>Mutable control surface used only by the background probe worker.</summary>
public interface IInternetConnectivityController : IInternetConnectivityState
{
    void SetDisabled();
    void ApplyProbeResult(bool reachable, int failureThreshold = 2);
}

/// <summary>
/// Hysteresis-backed state holder. Two consecutive failed probes are required before cloud
/// transports are gated; one successful probe restores them immediately.
/// </summary>
public sealed class InternetConnectivityState : IInternetConnectivityController
{
    private readonly object _gate = new();
    private InternetConnectivityStatus _status = InternetConnectivityStatus.Unknown;
    private DateTimeOffset _lastChangedAt = DateTimeOffset.UtcNow;
    private int _consecutiveFailures;

    public InternetConnectivityStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public DateTimeOffset LastChangedAt
    {
        get { lock (_gate) return _lastChangedAt; }
    }

    public bool AllowsInternetTransports
    {
        get
        {
            lock (_gate)
            {
                return _status is InternetConnectivityStatus.Unknown or InternetConnectivityStatus.Online;
            }
        }
    }

    public void SetDisabled()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            SetStatusNoLock(InternetConnectivityStatus.Disabled);
        }
    }

    public void ApplyProbeResult(bool reachable, int failureThreshold = 2)
    {
        lock (_gate)
        {
            if (reachable)
            {
                _consecutiveFailures = 0;
                SetStatusNoLock(InternetConnectivityStatus.Online);
                return;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures >= Math.Max(1, failureThreshold))
            {
                SetStatusNoLock(InternetConnectivityStatus.Offline);
            }
        }
    }

    private void SetStatusNoLock(InternetConnectivityStatus next)
    {
        if (_status == next)
        {
            return;
        }

        _status = next;
        _lastChangedAt = DateTimeOffset.UtcNow;
    }
}
