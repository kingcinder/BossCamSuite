using BossCam.Contracts;

namespace BossCam.Core;

/// <summary>
/// Recording-domain persistence seam. Recording orchestration depends on recording concepts
/// rather than the full application-store surface; the existing store remains the durable
/// implementation behind this adapter during migration.
/// </summary>
public interface IRecordingStore
{
    Task SaveRecordingProfilesAsync(IEnumerable<RecordingProfile> profiles, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RecordingProfile>> GetRecordingProfilesAsync(Guid? deviceId, CancellationToken cancellationToken);
    Task SaveRecordingSegmentsAsync(IEnumerable<RecordingSegment> segments, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RecordingSegment>> GetRecordingSegmentsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken);
    Task<int> DeleteRecordingSegmentsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);
    Task SaveRecordingJobsAsync(IEnumerable<RecordingJob> jobs, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RecordingJob>> GetRecordingJobsAsync(Guid? deviceId, CancellationToken cancellationToken);
}

/// <summary>Compatibility adapter that exposes recording persistence from the legacy store.</summary>
public sealed class ApplicationStoreRecordingStore(IApplicationStore store) : IRecordingStore
{
    public Task SaveRecordingProfilesAsync(IEnumerable<RecordingProfile> profiles, CancellationToken cancellationToken)
        => store.SaveRecordingProfilesAsync(profiles, cancellationToken);

    public Task<IReadOnlyCollection<RecordingProfile>> GetRecordingProfilesAsync(Guid? deviceId, CancellationToken cancellationToken)
        => store.GetRecordingProfilesAsync(deviceId, cancellationToken);

    public Task SaveRecordingSegmentsAsync(IEnumerable<RecordingSegment> segments, CancellationToken cancellationToken)
        => store.SaveRecordingSegmentsAsync(segments, cancellationToken);

    public Task<IReadOnlyCollection<RecordingSegment>> GetRecordingSegmentsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
        => store.GetRecordingSegmentsAsync(deviceId, limit, cancellationToken);

    public Task<int> DeleteRecordingSegmentsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
        => store.DeleteRecordingSegmentsAsync(ids, cancellationToken);

    public Task SaveRecordingJobsAsync(IEnumerable<RecordingJob> jobs, CancellationToken cancellationToken)
        => store.SaveRecordingJobsAsync(jobs, cancellationToken);

    public Task<IReadOnlyCollection<RecordingJob>> GetRecordingJobsAsync(Guid? deviceId, CancellationToken cancellationToken)
        => store.GetRecordingJobsAsync(deviceId, cancellationToken);
}
