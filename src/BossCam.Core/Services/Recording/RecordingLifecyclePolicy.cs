using BossCam.Contracts;

namespace BossCam.Core;

/// <summary>
/// Pure decisions for recovering recording jobs after a host restart and detecting stale output.
/// Process inspection, persistence, and process control remain in the recording façade.
/// </summary>
public static class RecordingLifecyclePolicy
{
    public static RecordingReconciliationDecision DecideReconciliation(
        RecordingJob job,
        bool processAlive,
        DateTime processStartedAtUtc)
    {
        if (!job.IsRunning || job.ProcessId is not > 0)
        {
            return new RecordingReconciliationDecision(RecordingReconciliationAction.Stop, "Job has no adoptable running process id.");
        }

        if (!processAlive)
        {
            return new RecordingReconciliationDecision(RecordingReconciliationAction.Stop, "Recorded process is no longer alive.");
        }

        if (processStartedAtUtc > job.StartedAt.UtcDateTime)
        {
            return new RecordingReconciliationDecision(RecordingReconciliationAction.Stop, "Recorded process id appears recycled.");
        }

        return new RecordingReconciliationDecision(RecordingReconciliationAction.Attach, "Recorded process is alive and predates the job.");
    }

    public static bool IsStalled(DateTimeOffset now, DateTimeOffset latestSegmentWrite, int timeoutSeconds)
        => timeoutSeconds > 0 && now - latestSegmentWrite >= TimeSpan.FromSeconds(timeoutSeconds);
}

public enum RecordingReconciliationAction
{
    Attach,
    Stop
}

public sealed record RecordingReconciliationDecision(
    RecordingReconciliationAction Action,
    string Reason);
