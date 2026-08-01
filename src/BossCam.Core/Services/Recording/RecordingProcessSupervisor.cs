namespace BossCam.Core;

/// <summary>
/// Owns the in-memory association between recording jobs and their OS processes.
/// Process launch, persistence, and operator notifications remain in <see cref="RecordingService"/>;
/// this seam protects registration, replacement, identity-safe removal, and snapshots.
/// </summary>
public sealed class RecordingProcessSupervisor
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, RecordingService.RunningRecording> _entries = [];

    internal void Add(RecordingService.RunningRecording entry)
    {
        lock (_sync)
        {
            _entries[entry.Job.Id] = entry;
        }
    }

    internal bool TryGet(Guid jobId, out RecordingService.RunningRecording? entry)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(jobId, out entry);
        }
    }

    internal bool Remove(Guid jobId, out RecordingService.RunningRecording? entry)
    {
        lock (_sync)
        {
            return _entries.Remove(jobId, out entry);
        }
    }

    /// <summary>
    /// Removes an entry only when the tracked value is the same registration instance. A stale
    /// Process.Exited callback therefore cannot remove a newer process that reused the job id.
    /// </summary>
    internal bool RemoveIfCurrent(RecordingService.RunningRecording expected, out RecordingService.RunningRecording? removed)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(expected.Job.Id, out var current) || !ReferenceEquals(current, expected))
            {
                removed = null;
                return false;
            }

            _entries.Remove(expected.Job.Id);
            removed = expected;
            return true;
        }
    }

    internal IReadOnlyCollection<RecordingService.RunningRecording> Snapshot()
    {
        lock (_sync)
        {
            return _entries.Values.ToList();
        }
    }
}
