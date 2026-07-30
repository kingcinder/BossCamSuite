namespace BossCam.Core.Utilities;

/// <summary>
/// Closed enum of every table managed by <c>SqliteApplicationStore</c>'s generic
/// payload helpers. Replacing the string-dispatched table/key/timestamp parameters
/// with the enum + a static lookup table makes it structurally impossible for a
/// future caller to inject a variable identifier into a SQL command.
/// </summary>
public enum StoreTable
{
    CapabilityMaps,
    SettingsSnapshots,
    ProtocolManifests,
    RecordingProfiles,
    ProbeSessions,
    AuditEntries,
    FirmwareArtifacts,
    EndpointTranscripts,
    PersistenceVerificationResults
}

/// <summary>
/// Resolved (table, key column, timestamp column) tuple for a <see cref="StoreTable"/>.
/// </summary>
public sealed record SqlIdentifier(string Table, string KeyColumn, string TimestampColumn);

/// <summary>
/// Static lookup that maps each <see cref="StoreTable"/> to its pre-resolved SQL
/// identifiers. Callers MUST go through this helper — never compose SQL by string
/// interpolation against the raw table name.
/// </summary>
public static class SqlIdentifierMap
{
    public static SqlIdentifier For(StoreTable table) => table switch
    {
        StoreTable.CapabilityMaps => new("capability_maps", "device_id", "updated_at"),
        StoreTable.SettingsSnapshots => new("settings_snapshots", "device_id", "updated_at"),
        StoreTable.ProtocolManifests => new("protocol_manifests", "manifest_id", "updated_at"),
        StoreTable.RecordingProfiles => new("recording_profiles", "id", "updated_at"),
        StoreTable.ProbeSessions => new("probe_sessions", "id", "started_at"),
        StoreTable.AuditEntries => new("audit_entries", "id", "timestamp"),
        StoreTable.FirmwareArtifacts => new("firmware_artifacts", "id", "analyzed_at"),
        StoreTable.EndpointTranscripts => new("endpoint_transcripts", "id", "timestamp"),
        StoreTable.PersistenceVerificationResults => new("persistence_verification_results", "id", "timestamp"),
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unknown StoreTable — add it to SqlIdentifierMap before using.")
    };
}
