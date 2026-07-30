using BossCam.Contracts;
using BossCam.Core;
using Microsoft.AspNetCore.SignalR;

namespace BossCam.Service.Hubs;

/// <summary>
/// Concrete SignalR broadcaster that resolves the typed hub context and
/// invokes methods on all connected SPA clients. Registered as a singleton
/// in DI so core services can push events without depending on Hub types.
/// </summary>
public sealed class BossCamEventBroadcaster(
    IHubContext<BossCamHub, IBossCamHubClient> hubContext,
    ILogger<BossCamEventBroadcaster> logger) : IBossCamEventBroadcaster
{
    public async Task DevicesChangedAsync(IReadOnlyCollection<DeviceIdentity> devices, CancellationToken ct = default)
    {
        try
        {
            await hubContext.Clients.All.DevicesChanged(devices);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "SignalR broadcast DevicesChanged failed");
        }
    }

    public async Task RecordingJobStartedAsync(RecordingJob job, CancellationToken ct = default)
    {
        try
        {
            await hubContext.Clients.All.RecordingJobStarted(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "SignalR broadcast RecordingJobStarted failed");
        }
    }

    public async Task RecordingJobStoppedAsync(RecordingJob job, CancellationToken ct = default)
    {
        try
        {
            await hubContext.Clients.All.RecordingJobStopped(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "SignalR broadcast RecordingJobStopped failed");
        }
    }

    public async Task HighlightStateChangedAsync(HighlightBoardState state, CancellationToken ct = default)
    {
        try
        {
            await hubContext.Clients.All.HighlightStateChanged(state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "SignalR broadcast HighlightStateChanged failed");
        }
    }

    public async Task SnapshotSavedAsync(Guid deviceId, string path, long bytes, CancellationToken ct = default)
    {
        try
        {
            await hubContext.Clients.All.SnapshotSaved(deviceId.ToString("N"), path, bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "SignalR broadcast SnapshotSaved failed");
        }
    }

    public async Task DiscoveryProgressAsync(int devicesFound, string provider, bool complete, string? error, CancellationToken ct = default)
    {
        try
        {
            await hubContext.Clients.All.DiscoveryProgress(devicesFound, provider, complete, error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "SignalR broadcast DiscoveryProgress failed");
        }
    }

    public async Task ProbeProgressAsync(Guid deviceId, string stage, int endpointsVerified, bool complete, string? error, CancellationToken ct = default)
    {
        try
        {
            await hubContext.Clients.All.ProbeProgress(deviceId.ToString("N"), stage, endpointsVerified, complete, error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "SignalR broadcast ProbeProgress failed");
        }
    }

    public async Task ConnectivityChangedAsync(DeviceConnectivitySnapshot snapshot, CancellationToken ct = default)
    {
        try
        {
            await hubContext.Clients.All.ConnectivityChanged(
                snapshot.DeviceId.ToString("N"),
                snapshot.Status.ToString(),
                snapshot.TransportResults,
                snapshot.LastDiagnosticSummary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "SignalR broadcast ConnectivityChanged failed");
        }
    }
}
