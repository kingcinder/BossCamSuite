using BossCam.Contracts;

namespace BossCam.Core;

/// <summary>
/// Persistence surface required by typed control normalization and write evidence.
/// This keeps the typed-control module independent of the broader application-store surface.
/// </summary>
public interface ITypedControlStore
{
    Task<DeviceIdentity?> GetDeviceAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EndpointValidationResult>> GetEndpointValidationResultsAsync(Guid deviceId, CancellationToken cancellationToken);
    Task SaveNormalizedSettingFieldsAsync(IEnumerable<NormalizedSettingField> fields, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<NormalizedSettingField>> GetNormalizedSettingFieldsAsync(Guid deviceId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FieldConstraintProfile>> GetFieldConstraintProfilesAsync(string? firmwareFingerprint, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<DependencyMatrixProfile>> GetDependencyMatrixProfilesAsync(string? firmwareFingerprint, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<GroupedUnsupportedRetestResult>> GetGroupedRetestResultsAsync(Guid deviceId, int limit, CancellationToken cancellationToken);
    Task AddAuditEntryAsync(WriteAuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>Compatibility adapter used while the SQLite implementation remains monolithic.</summary>
public sealed class ApplicationStoreTypedControlStore(IApplicationStore store) : ITypedControlStore
{
    public Task<DeviceIdentity?> GetDeviceAsync(Guid id, CancellationToken cancellationToken)
        => store.GetDeviceAsync(id, cancellationToken);

    public Task<IReadOnlyCollection<EndpointValidationResult>> GetEndpointValidationResultsAsync(Guid deviceId, CancellationToken cancellationToken)
        => store.GetEndpointValidationResultsAsync(deviceId, cancellationToken);

    public Task SaveNormalizedSettingFieldsAsync(IEnumerable<NormalizedSettingField> fields, CancellationToken cancellationToken)
        => store.SaveNormalizedSettingFieldsAsync(fields, cancellationToken);

    public Task<IReadOnlyCollection<NormalizedSettingField>> GetNormalizedSettingFieldsAsync(Guid deviceId, CancellationToken cancellationToken)
        => store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);

    public Task<IReadOnlyCollection<FieldConstraintProfile>> GetFieldConstraintProfilesAsync(string? firmwareFingerprint, CancellationToken cancellationToken)
        => store.GetFieldConstraintProfilesAsync(firmwareFingerprint, cancellationToken);

    public Task<IReadOnlyCollection<DependencyMatrixProfile>> GetDependencyMatrixProfilesAsync(string? firmwareFingerprint, CancellationToken cancellationToken)
        => store.GetDependencyMatrixProfilesAsync(firmwareFingerprint, cancellationToken);

    public Task<IReadOnlyCollection<GroupedUnsupportedRetestResult>> GetGroupedRetestResultsAsync(Guid deviceId, int limit, CancellationToken cancellationToken)
        => store.GetGroupedRetestResultsAsync(deviceId, limit, cancellationToken);

    public Task AddAuditEntryAsync(WriteAuditEntry entry, CancellationToken cancellationToken)
        => store.AddAuditEntryAsync(entry, cancellationToken);
}
