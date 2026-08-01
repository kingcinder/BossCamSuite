using System.Diagnostics;
using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Tests;

public sealed class RecordingProcessSupervisorTests
{
    [Fact]
    public void Tracks_and_removes_running_recording_entries()
    {
        using var process = Process.GetCurrentProcess();
        var job = new RecordingJob { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid(), IsRunning = true };
        var entry = new RecordingService.RunningRecording(job, process, null);
        var supervisor = new RecordingProcessSupervisor();

        supervisor.Add(entry);

        Assert.True(supervisor.TryGet(job.Id, out var tracked));
        Assert.Same(entry, tracked);
        Assert.True(supervisor.Remove(job.Id, out var removed));
        Assert.Same(entry, removed);
        Assert.False(supervisor.TryGet(job.Id, out _));
    }

    [Fact]
    public void Only_the_current_entry_can_be_removed_by_identity()
    {
        using var process = Process.GetCurrentProcess();
        var job = new RecordingJob { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid(), IsRunning = true };
        var current = new RecordingService.RunningRecording(job, process, null);
        var stale = new RecordingService.RunningRecording(job, process, null);
        var supervisor = new RecordingProcessSupervisor();
        supervisor.Add(current);

        Assert.False(supervisor.RemoveIfCurrent(stale, out _));
        Assert.True(supervisor.TryGet(job.Id, out var tracked));
        Assert.Same(current, tracked);
    }
}
