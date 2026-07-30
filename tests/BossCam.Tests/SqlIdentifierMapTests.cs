using BossCam.Core.Utilities;
using Xunit;

namespace BossCam.Tests;

/// <summary>
/// Regression tests for the closed <see cref="StoreTable"/> enum + <see cref="SqlIdentifierMap"/>
/// lookup that replaced string-interpolated table/key/timestamp columns in
/// <c>SqliteApplicationStore</c>. Three properties are pinned:
/// (a) every supported StoreTable has a pre-resolved tuple,
/// (b) unknown enum values throw rather than silently choosing "updated_at" by default,
/// (c) the on-disk identifiers match the SQL CREATE TABLE statements in
///     <c>SqliteApplicationStore.InitializeAsync</c> (single source of truth).
/// </summary>
public class SqlIdentifierMapTests
{
    [Theory]
    [InlineData(StoreTable.CapabilityMaps, "capability_maps", "device_id", "updated_at")]
    [InlineData(StoreTable.SettingsSnapshots, "settings_snapshots", "device_id", "updated_at")]
    [InlineData(StoreTable.ProtocolManifests, "protocol_manifests", "manifest_id", "updated_at")]
    [InlineData(StoreTable.RecordingProfiles, "recording_profiles", "id", "updated_at")]
    [InlineData(StoreTable.ProbeSessions, "probe_sessions", "id", "started_at")]
    [InlineData(StoreTable.AuditEntries, "audit_entries", "id", "timestamp")]
    [InlineData(StoreTable.FirmwareArtifacts, "firmware_artifacts", "id", "analyzed_at")]
    [InlineData(StoreTable.EndpointTranscripts, "endpoint_transcripts", "id", "timestamp")]
    [InlineData(StoreTable.PersistenceVerificationResults, "persistence_verification_results", "id", "timestamp")]
    public void Known_Tables_Resolve_To_Pre_Written_Identifiers(StoreTable table, string expectedTable, string expectedKey, string expectedTimestamp)
    {
        var identifier = SqlIdentifierMap.For(table);
        Assert.Equal(expectedTable, identifier.Table);
        Assert.Equal(expectedKey, identifier.KeyColumn);
        Assert.Equal(expectedTimestamp, identifier.TimestampColumn);
    }

    [Fact]
    public void Out_Of_Range_Enum_Value_Throws_Rather_Than_Defaulting()
    {
        // The _ => clause in SqlIdentifierMap.For must throw — never silently return
        // "updated_at" the way the previous string-dispatch helper did.
        var bogus = (StoreTable)9999;
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlIdentifierMap.For(bogus));
    }
}
