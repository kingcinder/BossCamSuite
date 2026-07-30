using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Tests;

/// <summary>
/// No-op implementation of <see cref="IBossCamEventBroadcaster"/> for unit tests.
/// All methods silently succeed without sending real-time events.
/// Use <see cref="Instance"/> to avoid allocating per test.
/// </summary>
internal sealed class NullBossCamEventBroadcaster : IBossCamEventBroadcaster
{
    public static readonly NullBossCamEventBroadcaster Instance = new();

    public Task DevicesChangedAsync(IReadOnlyCollection<DeviceIdentity> devices, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RecordingJobStartedAsync(RecordingJob job, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RecordingJobStoppedAsync(RecordingJob job, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task HighlightStateChangedAsync(HighlightBoardState state, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SnapshotSavedAsync(Guid deviceId, string path, long bytes, CancellationToken ct = default)
        => Task.CompletedTask;
}
