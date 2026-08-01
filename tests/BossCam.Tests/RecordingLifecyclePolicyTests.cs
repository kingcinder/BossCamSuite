using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Tests;

public sealed class RecordingLifecyclePolicyTests
{
    [Fact]
    public void Persisted_Job_With_Live_Process_Started_Before_Job_Can_Be_Reattached()
    {
        var job = new RecordingJob
        {
            ProcessId = 42,
            IsRunning = true,
            StartedAt = DateTimeOffset.UtcNow
        };

        var decision = RecordingLifecyclePolicy.DecideReconciliation(
            job,
            processAlive: true,
            processStartedAtUtc: job.StartedAt.UtcDateTime.AddSeconds(-1));

        Assert.Equal(RecordingReconciliationAction.Attach, decision.Action);
    }

    [Fact]
    public void Persisted_Job_With_Recycled_Pid_Is_Stopped_Not_Attached()
    {
        var job = new RecordingJob
        {
            ProcessId = 42,
            IsRunning = true,
            StartedAt = DateTimeOffset.UtcNow
        };

        var decision = RecordingLifecyclePolicy.DecideReconciliation(
            job,
            processAlive: true,
            processStartedAtUtc: job.StartedAt.UtcDateTime.AddSeconds(1));

        Assert.Equal(RecordingReconciliationAction.Stop, decision.Action);
        Assert.Contains("recycled", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stalled_When_Latest_Segment_Is_Older_Than_Threshold()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(RecordingLifecyclePolicy.IsStalled(now, now.AddSeconds(-31), 30));
        Assert.False(RecordingLifecyclePolicy.IsStalled(now, now.AddSeconds(-29), 30));
    }

    [Fact]
    public void Stall_Check_Is_Disabled_For_Nonpositive_Threshold()
    {
        Assert.False(RecordingLifecyclePolicy.IsStalled(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), 0));
    }
}
