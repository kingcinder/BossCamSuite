using BossCam.Contracts;
using BossCam.Core;
using Microsoft.AspNetCore.SignalR;

namespace BossCam.Service.Hubs;

/// <summary>
/// Real-time event hub for the BossCamSuite operator console. Clients connect
/// from the Svelte SPA and receive push notifications for device discovery,
/// recording state, highlight board changes, and snapshot confirmations.
/// Inbound methods (from client → server) are minimal — most data flows
/// server → client via strongly-typed hub methods on <see cref="IBossCamHubClient"/>.
/// </summary>
public sealed class BossCamHub : Hub<IBossCamHubClient> { }

/// <summary>
/// Typed hub client interface — each method name is a SignalR event name
/// the SPA subscribes to with <c>hubConnection.on('DevicesChanged', ...)</c>.
/// Using a typed hub avoids magic-string invocation on the server side.
/// </summary>
public interface IBossCamHubClient
{
    Task DevicesChanged(IReadOnlyCollection<DeviceIdentity> devices);
    Task RecordingJobStarted(RecordingJob job);
    Task RecordingJobStopped(RecordingJob job);
    Task HighlightStateChanged(HighlightBoardState state);
    Task SnapshotSaved(string deviceId, string path, long bytes);
}
