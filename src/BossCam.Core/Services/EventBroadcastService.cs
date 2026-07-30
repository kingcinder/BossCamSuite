using BossCam.Contracts;

namespace BossCam.Core;

/// <summary>
/// Abstraction over real-time event broadcasting (SignalR). Core services
/// inject this interface and call its methods to push UI updates to all
/// connected SPA clients without depending on ASP.NET Hub infrastructure.
/// </summary>
public interface IBossCamEventBroadcaster
{
    /// <summary>
    /// Broadcast the full device list after discovery/registration/upsert.
    /// Implementations should write a default no-op version that swallows
    /// the exception when no SignalR host is available.
    /// </summary>
    Task DevicesChangedAsync(IReadOnlyCollection<DeviceIdentity> devices, CancellationToken ct = default);

    /// <summary>
    /// Broadcast when a recording job starts.
    /// </summary>
    Task RecordingJobStartedAsync(RecordingJob job, CancellationToken ct = default);

    /// <summary>
    /// Broadcast when a recording job stops (gracefully or on process exit).
    /// </summary>
    Task RecordingJobStoppedAsync(RecordingJob job, CancellationToken ct = default);

    /// <summary>
    /// Broadcast highlight board state after selection/next/prev/stream changes.
    /// </summary>
    Task HighlightStateChangedAsync(HighlightBoardState state, CancellationToken ct = default);

    /// <summary>
    /// Broadcast when a snapshot JPEG is saved to disk.
    /// </summary>
    Task SnapshotSavedAsync(Guid deviceId, string path, long bytes, CancellationToken ct = default);

    /// <summary>
    /// Broadcast discovery progress updates.
    /// </summary>
    Task DiscoveryProgressAsync(int devicesFound, string provider, bool complete, string? error, CancellationToken ct = default);

    /// <summary>
    /// Broadcast probe progress updates for a device.
    /// </summary>
    Task ProbeProgressAsync(Guid deviceId, string stage, int endpointsVerified, bool complete, string? error, CancellationToken ct = default);

    /// <summary>
    /// Broadcast a device connectivity state change.
    /// </summary>
    Task ConnectivityChangedAsync(DeviceConnectivitySnapshot snapshot, CancellationToken ct = default);
}
