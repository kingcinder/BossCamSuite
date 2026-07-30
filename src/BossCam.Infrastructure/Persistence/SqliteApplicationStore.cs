using System.Text.Json;
using System.Text.Json.Serialization;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Security;
using BossCam.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Persistence;

public sealed class SqliteApplicationStore : IApplicationStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IOptions<BossCamRuntimeOptions> _options;
    private readonly IPasswordCipher _cipher;
    private readonly JsonSerializerOptions _serializerOptions = CreateSerializerOptions();

    /// <summary>Constructor used in production (DI supplies both options and the cipher).</summary>
    public SqliteApplicationStore(IOptions<BossCamRuntimeOptions> options, IPasswordCipher cipher)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cipher);
        _options = options;
        _cipher = cipher;
    }

    /// <summary>Di-less constructor for tests that don't need encryption.</summary>
    public SqliteApplicationStore(IOptions<BossCamRuntimeOptions> options) : this(options, NoOpPasswordCipher.Instance) { }

    private string DatabasePath => _options.Value.DatabasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            var commands = new[]
            {
                "CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, dedupe_key TEXT NOT NULL UNIQUE, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS capability_maps (device_id TEXT PRIMARY KEY, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS settings_snapshots (device_id TEXT PRIMARY KEY, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS audit_entries (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, payload TEXT NOT NULL, timestamp TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS protocol_manifests (manifest_id TEXT PRIMARY KEY, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS endpoint_validation_results (result_key TEXT PRIMARY KEY, device_id TEXT NOT NULL, endpoint TEXT NOT NULL, method TEXT NOT NULL, adapter_name TEXT NOT NULL, payload TEXT NOT NULL, captured_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS endpoint_transcripts (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, endpoint TEXT NOT NULL, method TEXT NOT NULL, adapter_name TEXT NOT NULL, payload TEXT NOT NULL, timestamp TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS probe_sessions (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, payload TEXT NOT NULL, started_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS probe_stage_results (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, device_id TEXT NOT NULL, payload TEXT NOT NULL, captured_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS normalized_setting_fields (field_key TEXT PRIMARY KEY, device_id TEXT NOT NULL, payload TEXT NOT NULL, captured_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS firmware_capability_profiles (firmware_fingerprint TEXT PRIMARY KEY, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS persistence_verification_results (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, payload TEXT NOT NULL, timestamp TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS endpoint_contracts (contract_key TEXT PRIMARY KEY, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS contract_fixtures (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, contract_key TEXT NOT NULL, payload TEXT NOT NULL, captured_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS firmware_artifacts (id TEXT PRIMARY KEY, payload TEXT NOT NULL, analyzed_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS recording_profiles (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS recording_segments (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, profile_id TEXT NOT NULL, payload TEXT NOT NULL, indexed_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS semantic_write_observations (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, payload TEXT NOT NULL, timestamp TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS field_constraint_profiles (constraint_key TEXT PRIMARY KEY, firmware_fingerprint TEXT NOT NULL, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS dependency_matrix_profiles (matrix_key TEXT PRIMARY KEY, firmware_fingerprint TEXT NOT NULL, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS image_control_inventory (inventory_key TEXT PRIMARY KEY, device_id TEXT NOT NULL, firmware_fingerprint TEXT NOT NULL, payload TEXT NOT NULL, captured_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS image_behavior_maps (behavior_key TEXT PRIMARY KEY, device_id TEXT NOT NULL, firmware_fingerprint TEXT NOT NULL, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS image_writable_test_sets (device_id TEXT PRIMARY KEY, firmware_fingerprint TEXT NOT NULL, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS grouped_apply_profiles (profile_key TEXT PRIMARY KEY, device_id TEXT NOT NULL, firmware_fingerprint TEXT NOT NULL, payload TEXT NOT NULL, updated_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS grouped_retest_results (result_key TEXT PRIMARY KEY, device_id TEXT NOT NULL, firmware_fingerprint TEXT NOT NULL, payload TEXT NOT NULL, captured_at TEXT NOT NULL)"
            };

            foreach (var text in commands)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = text;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertDevicesAsync(IEnumerable<DeviceIdentity> devices, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var device in devices)
            {
                // Encrypt-at-rest: if the in-memory record carries a plaintext Password, mirror
                // it into PasswordCiphertext via the cipher. The on-disk JSON omits Password
                // because DeviceIdentity.Password is marked [JsonIgnore].
                var ciphered = string.IsNullOrEmpty(device.Password)
                    ? device
                    : device with { PasswordCiphertext = _cipher.Encrypt(device.Password) };
                var key = BuildDedupeKey(ciphered);
                var payload = JsonSerializer.Serialize(ciphered, _serializerOptions);
                var updated = DateTimeOffset.UtcNow.ToString("O");

                await using var updateById = connection.CreateCommand();
                updateById.CommandText = "UPDATE devices SET dedupe_key = $key, payload = $payload, updated_at = $updated WHERE id = $id";
                updateById.Parameters.AddWithValue("$id", ciphered.Id.ToString());
                updateById.Parameters.AddWithValue("$key", key);
                updateById.Parameters.AddWithValue("$payload", payload);
                updateById.Parameters.AddWithValue("$updated", updated);
                var updatedRows = await updateById.ExecuteNonQueryAsync(cancellationToken);
                if (updatedRows > 0)
                {
                    continue;
                }

                // Keep id and payload aligned. Previous ON CONFLICT only rewrote payload, which left
                // row id stale vs DeviceIdentity.Id inside JSON and broke GetDeviceAsync lookups.
                await using var upsertByKey = connection.CreateCommand();
                upsertByKey.CommandText = "INSERT INTO devices (id, dedupe_key, payload, updated_at) VALUES ($id, $key, $payload, $updated) ON CONFLICT(dedupe_key) DO UPDATE SET id = excluded.id, payload = excluded.payload, updated_at = excluded.updated_at";
                upsertByKey.Parameters.AddWithValue("$id", ciphered.Id.ToString());
                upsertByKey.Parameters.AddWithValue("$key", key);
                upsertByKey.Parameters.AddWithValue("$payload", payload);
                upsertByKey.Parameters.AddWithValue("$updated", updated);
                await upsertByKey.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<DeviceIdentity>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var loaded = await QueryPayloadListAsync<DeviceIdentity>("SELECT payload FROM devices ORDER BY updated_at DESC", null, cancellationToken);
        return loaded.Select(ResolvePlaintextPassword).ToList();
    }

    public async Task<DeviceIdentity?> GetDeviceAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await QuerySinglePayloadAsync<DeviceIdentity>("SELECT payload FROM devices WHERE id = $id", parameters => parameters.AddWithValue("$id", id.ToString()), cancellationToken);
        return loaded is null ? null : ResolvePlaintextPassword(loaded);
    }

    /// <summary>
    /// After deserialization, DeviceIdentity.Password is null (it's [JsonIgnore]d on disk) and
    /// PasswordCiphertext holds the encrypted blob. Resolve back to plaintext in-memory so
    /// legacy consumers keep reading <c>device.Password</c> without each one needing cipher
    /// injection. NoOpPasswordCipher is the identity function so tests remain roundtrip-clean.
    /// </summary>
    private DeviceIdentity ResolvePlaintextPassword(DeviceIdentity device)
        => string.IsNullOrEmpty(device.PasswordCiphertext)
            ? device
            : device with { Password = _cipher.Decrypt(device.PasswordCiphertext) };

    public async Task SaveCapabilityMapAsync(CapabilityMap capabilityMap, CancellationToken cancellationToken)
        => await UpsertPayloadAsync(StoreTable.CapabilityMaps, capabilityMap.DeviceId.ToString(), capabilityMap, capabilityMap.CapturedAt, cancellationToken);

    public async Task<CapabilityMap?> GetCapabilityMapAsync(Guid deviceId, CancellationToken cancellationToken)
        => await QuerySinglePayloadAsync<CapabilityMap>("SELECT payload FROM capability_maps WHERE device_id = $id", parameters => parameters.AddWithValue("$id", deviceId.ToString()), cancellationToken);

    public async Task SaveSettingsSnapshotAsync(SettingsSnapshot snapshot, CancellationToken cancellationToken)
        => await UpsertPayloadAsync(StoreTable.SettingsSnapshots, snapshot.DeviceId.ToString(), snapshot, snapshot.CapturedAt, cancellationToken);

    public async Task<SettingsSnapshot?> GetSettingsSnapshotAsync(Guid deviceId, CancellationToken cancellationToken)
        => await QuerySinglePayloadAsync<SettingsSnapshot>("SELECT payload FROM settings_snapshots WHERE device_id = $id", parameters => parameters.AddWithValue("$id", deviceId.ToString()), cancellationToken);

    public async Task AddAuditEntryAsync(WriteAuditEntry entry, CancellationToken cancellationToken)
        => await InsertPayloadAsync(StoreTable.AuditEntries, entry.Id.ToString(), entry, entry.Timestamp, cancellationToken, deviceId: entry.DeviceId.ToString());

    public async Task<IReadOnlyCollection<WriteAuditEntry>> GetAuditEntriesAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
    {
        if (deviceId is null)
        {
            return await QueryPayloadListAsync<WriteAuditEntry>($"SELECT payload FROM audit_entries ORDER BY timestamp DESC LIMIT {Math.Max(1, limit)}", null, cancellationToken);
        }

        return await QueryPayloadListAsync<WriteAuditEntry>($"SELECT payload FROM audit_entries WHERE device_id = $id ORDER BY timestamp DESC LIMIT {Math.Max(1, limit)}", parameters => parameters.AddWithValue("$id", deviceId.Value.ToString()), cancellationToken);
    }

    public async Task SaveProtocolManifestsAsync(IEnumerable<ProtocolManifest> manifests, CancellationToken cancellationToken)
    {
        foreach (var manifest in manifests)
        {
            await UpsertPayloadAsync(StoreTable.ProtocolManifests, manifest.ManifestId, manifest, DateTimeOffset.UtcNow, cancellationToken);
        }
    }

    public async Task<IReadOnlyCollection<ProtocolManifest>> GetProtocolManifestsAsync(CancellationToken cancellationToken)
        => await QueryPayloadListAsync<ProtocolManifest>("SELECT payload FROM protocol_manifests ORDER BY updated_at DESC", null, cancellationToken);

    public async Task SaveEndpointValidationResultsAsync(IEnumerable<EndpointValidationResult> results, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var result in results)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO endpoint_validation_results (result_key, device_id, endpoint, method, adapter_name, payload, captured_at) VALUES ($result_key, $device_id, $endpoint, $method, $adapter_name, $payload, $captured_at) ON CONFLICT(result_key) DO UPDATE SET payload = excluded.payload, captured_at = excluded.captured_at";
                command.Parameters.AddWithValue("$result_key", BuildValidationKey(result));
                command.Parameters.AddWithValue("$device_id", result.DeviceId.ToString());
                command.Parameters.AddWithValue("$endpoint", result.Endpoint);
                command.Parameters.AddWithValue("$method", result.Method);
                command.Parameters.AddWithValue("$adapter_name", result.AdapterName);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(result, _serializerOptions));
                command.Parameters.AddWithValue("$captured_at", result.CapturedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<EndpointValidationResult>> GetEndpointValidationResultsAsync(Guid deviceId, CancellationToken cancellationToken)
        => await QueryPayloadListAsync<EndpointValidationResult>("SELECT payload FROM endpoint_validation_results WHERE device_id = $id ORDER BY captured_at DESC", parameters => parameters.AddWithValue("$id", deviceId.ToString()), cancellationToken);

    public async Task SaveEndpointTranscriptsAsync(IEnumerable<EndpointTranscript> transcripts, CancellationToken cancellationToken)
    {
        foreach (var transcript in transcripts)
        {
            await InsertPayloadAsync(StoreTable.EndpointTranscripts, transcript.Id.ToString(), transcript, transcript.Timestamp, cancellationToken, deviceId: transcript.DeviceId.ToString(), endpoint: transcript.Endpoint, method: transcript.Method, adapterName: transcript.AdapterName);
        }
    }

    public async Task<IReadOnlyCollection<EndpointTranscript>> GetEndpointTranscriptsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
    {
        if (deviceId is null)
        {
            return await QueryPayloadListAsync<EndpointTranscript>($"SELECT payload FROM endpoint_transcripts ORDER BY timestamp DESC LIMIT {Math.Max(1, limit)}", null, cancellationToken);
        }

        return await QueryPayloadListAsync<EndpointTranscript>($"SELECT payload FROM endpoint_transcripts WHERE device_id = $id ORDER BY timestamp DESC LIMIT {Math.Max(1, limit)}", parameters => parameters.AddWithValue("$id", deviceId.Value.ToString()), cancellationToken);
    }

    public async Task SaveProbeSessionAsync(ProbeSession session, CancellationToken cancellationToken)
        => await UpsertPayloadAsync(StoreTable.ProbeSessions, session.Id.ToString(), session, session.StartedAt, cancellationToken, deviceId: session.DeviceId.ToString());

    public async Task<IReadOnlyCollection<ProbeSession>> GetProbeSessionsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
    {
        if (deviceId is null)
        {
            return await QueryPayloadListAsync<ProbeSession>($"SELECT payload FROM probe_sessions ORDER BY started_at DESC LIMIT {Math.Max(1, limit)}", null, cancellationToken);
        }

        return await QueryPayloadListAsync<ProbeSession>($"SELECT payload FROM probe_sessions WHERE device_id = $id ORDER BY started_at DESC LIMIT {Math.Max(1, limit)}", parameters => parameters.AddWithValue("$id", deviceId.Value.ToString()), cancellationToken);
    }

    public async Task SaveProbeStageResultsAsync(IEnumerable<ProbeStageResult> stages, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var stage in stages)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR REPLACE INTO probe_stage_results (id, session_id, device_id, payload, captured_at) VALUES ($id, $session_id, $device_id, $payload, $captured_at)";
                command.Parameters.AddWithValue("$id", stage.Id.ToString());
                command.Parameters.AddWithValue("$session_id", stage.SessionId.ToString());
                command.Parameters.AddWithValue("$device_id", stage.DeviceId.ToString());
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(stage, _serializerOptions));
                command.Parameters.AddWithValue("$captured_at", stage.CapturedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<ProbeStageResult>> GetProbeStageResultsAsync(Guid sessionId, CancellationToken cancellationToken)
        => await QueryPayloadListAsync<ProbeStageResult>("SELECT payload FROM probe_stage_results WHERE session_id = $id ORDER BY captured_at ASC", parameters => parameters.AddWithValue("$id", sessionId.ToString()), cancellationToken);

    public async Task SaveNormalizedSettingFieldsAsync(IEnumerable<NormalizedSettingField> fields, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var field in fields)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO normalized_setting_fields (field_key, device_id, payload, captured_at) VALUES ($field_key, $device_id, $payload, $captured_at) ON CONFLICT(field_key) DO UPDATE SET payload = excluded.payload, captured_at = excluded.captured_at";
                command.Parameters.AddWithValue("$field_key", BuildFieldKey(field));
                command.Parameters.AddWithValue("$device_id", field.DeviceId.ToString());
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(field, _serializerOptions));
                command.Parameters.AddWithValue("$captured_at", field.CapturedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<NormalizedSettingField>> GetNormalizedSettingFieldsAsync(Guid deviceId, CancellationToken cancellationToken)
        => await QueryPayloadListAsync<NormalizedSettingField>("SELECT payload FROM normalized_setting_fields WHERE device_id = $id ORDER BY captured_at DESC", parameters => parameters.AddWithValue("$id", deviceId.ToString()), cancellationToken);

    public async Task SaveFirmwareCapabilityProfileAsync(FirmwareCapabilityProfile profile, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO firmware_capability_profiles (firmware_fingerprint, payload, updated_at) VALUES ($id, $payload, $updated_at) ON CONFLICT(firmware_fingerprint) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at";
            command.Parameters.AddWithValue("$id", profile.FirmwareFingerprint);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(profile, _serializerOptions));
            command.Parameters.AddWithValue("$updated_at", profile.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<FirmwareCapabilityProfile>> GetFirmwareCapabilityProfilesAsync(CancellationToken cancellationToken)
        => await QueryPayloadListAsync<FirmwareCapabilityProfile>("SELECT payload FROM firmware_capability_profiles ORDER BY updated_at DESC", null, cancellationToken);

    public async Task SavePersistenceVerificationResultAsync(PersistenceVerificationResult result, CancellationToken cancellationToken)
        => await InsertPayloadAsync(StoreTable.PersistenceVerificationResults, result.Id.ToString(), result, result.Timestamp, cancellationToken, deviceId: result.DeviceId.ToString());

    public async Task<IReadOnlyCollection<PersistenceVerificationResult>> GetPersistenceVerificationResultsAsync(Guid deviceId, int limit, CancellationToken cancellationToken)
        => await QueryPayloadListAsync<PersistenceVerificationResult>($"SELECT payload FROM persistence_verification_results WHERE device_id = $id ORDER BY timestamp DESC LIMIT {Math.Max(1, limit)}", parameters => parameters.AddWithValue("$id", deviceId.ToString()), cancellationToken);

    public async Task SaveEndpointContractsAsync(IEnumerable<EndpointContract> contracts, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var contract in contracts)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO endpoint_contracts (contract_key, payload, updated_at) VALUES ($id, $payload, $updated_at) ON CONFLICT(contract_key) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at";
                command.Parameters.AddWithValue("$id", contract.ContractKey);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(contract, _serializerOptions));
                command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<EndpointContract>> GetEndpointContractsAsync(CancellationToken cancellationToken)
        => await QueryPayloadListAsync<EndpointContract>("SELECT payload FROM endpoint_contracts ORDER BY updated_at DESC", null, cancellationToken);

    public async Task SaveContractFixturesAsync(IEnumerable<EndpointContractFixture> fixtures, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var fixture in fixtures)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR REPLACE INTO contract_fixtures (id, device_id, contract_key, payload, captured_at) VALUES ($id, $device_id, $contract_key, $payload, $captured_at)";
                command.Parameters.AddWithValue("$id", fixture.Id.ToString());
                command.Parameters.AddWithValue("$device_id", fixture.DeviceId.ToString());
                command.Parameters.AddWithValue("$contract_key", fixture.ContractKey);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(fixture, _serializerOptions));
                command.Parameters.AddWithValue("$captured_at", fixture.CapturedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<EndpointContractFixture>> GetContractFixturesAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
    {
        if (deviceId is null)
        {
            return await QueryPayloadListAsync<EndpointContractFixture>($"SELECT payload FROM contract_fixtures ORDER BY captured_at DESC LIMIT {Math.Max(1, limit)}", null, cancellationToken);
        }

        return await QueryPayloadListAsync<EndpointContractFixture>($"SELECT payload FROM contract_fixtures WHERE device_id = $id ORDER BY captured_at DESC LIMIT {Math.Max(1, limit)}", parameters => parameters.AddWithValue("$id", deviceId.Value.ToString()), cancellationToken);
    }

    public async Task<int> DeleteContractFixturesAsync(Guid? deviceId, int olderThanDays, int maxPerDevice, int maxTotal, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays).ToString("O");
            var totalDeleted = 0;

            // Step 1: Delete fixtures older than the cutoff.
            await using var ageCmd = connection.CreateCommand();
            if (deviceId is not null)
            {
                ageCmd.CommandText = "DELETE FROM contract_fixtures WHERE device_id = $device_id AND captured_at < $cutoff";
                ageCmd.Parameters.AddWithValue("$device_id", deviceId.Value.ToString());
            }
            else
            {
                ageCmd.CommandText = "DELETE FROM contract_fixtures WHERE captured_at < $cutoff";
            }
            ageCmd.Parameters.AddWithValue("$cutoff", cutoff);
            totalDeleted += await ageCmd.ExecuteNonQueryAsync(cancellationToken);

            // Step 2: Enforce total limit (count oldest, delete excess).
            await using var countCmd = connection.CreateCommand();
            countCmd.CommandText = deviceId is null
                ? "SELECT COUNT(*) FROM contract_fixtures"
                : "SELECT COUNT(*) FROM contract_fixtures WHERE device_id = $device_id";
            if (deviceId is not null)
            {
                countCmd.Parameters.AddWithValue("$device_id", deviceId.Value.ToString());
            }
            var count = (long?)(await countCmd.ExecuteScalarAsync(cancellationToken)) ?? 0;
            var limit = deviceId is null ? maxTotal : maxPerDevice;
            if (count > limit)
            {
                var excess = count - limit;
                await using var excessCmd = connection.CreateCommand();
                if (deviceId is not null)
                {
                    excessCmd.CommandText = $"DELETE FROM contract_fixtures WHERE device_id = $device_id AND rowid IN (SELECT rowid FROM contract_fixtures WHERE device_id = $device_id ORDER BY captured_at ASC LIMIT {excess})";
                    excessCmd.Parameters.AddWithValue("$device_id", deviceId.Value.ToString());
                }
                else
                {
                    excessCmd.CommandText = $"DELETE FROM contract_fixtures WHERE rowid IN (SELECT rowid FROM contract_fixtures ORDER BY captured_at ASC LIMIT {excess})";
                }
                totalDeleted += await excessCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            return totalDeleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddFirmwareArtifactAsync(FirmwareArtifact artifact, CancellationToken cancellationToken)
        => await InsertPayloadAsync(StoreTable.FirmwareArtifacts, artifact.Id.ToString(), artifact, artifact.AnalyzedAt, cancellationToken);

    public async Task<IReadOnlyCollection<FirmwareArtifact>> GetFirmwareArtifactsAsync(CancellationToken cancellationToken)
        => await QueryPayloadListAsync<FirmwareArtifact>("SELECT payload FROM firmware_artifacts ORDER BY analyzed_at DESC", null, cancellationToken);

    public async Task SaveRecordingProfilesAsync(IEnumerable<RecordingProfile> profiles, CancellationToken cancellationToken)
    {
        foreach (var profile in profiles)
        {
            await UpsertPayloadAsync(StoreTable.RecordingProfiles, profile.Id.ToString(), profile, profile.UpdatedAt, cancellationToken, deviceId: profile.DeviceId.ToString());
        }
    }

    public async Task<IReadOnlyCollection<RecordingProfile>> GetRecordingProfilesAsync(Guid? deviceId, CancellationToken cancellationToken)
    {
        if (deviceId is null)
        {
            return await QueryPayloadListAsync<RecordingProfile>("SELECT payload FROM recording_profiles ORDER BY updated_at DESC", null, cancellationToken);
        }

        return await QueryPayloadListAsync<RecordingProfile>("SELECT payload FROM recording_profiles WHERE device_id = $id ORDER BY updated_at DESC", parameters => parameters.AddWithValue("$id", deviceId.Value.ToString()), cancellationToken);
    }

    public async Task SaveRecordingSegmentsAsync(IEnumerable<RecordingSegment> segments, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var segment in segments)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO recording_segments (id, device_id, profile_id, payload, indexed_at) VALUES ($id, $device_id, $profile_id, $payload, $indexed_at) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload, indexed_at = excluded.indexed_at";
                command.Parameters.AddWithValue("$id", segment.Id.ToString());
                command.Parameters.AddWithValue("$device_id", segment.DeviceId.ToString());
                command.Parameters.AddWithValue("$profile_id", segment.ProfileId.ToString());
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(segment, _serializerOptions));
                command.Parameters.AddWithValue("$indexed_at", segment.IndexedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<RecordingSegment>> GetRecordingSegmentsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
    {
        if (deviceId is null)
        {
            return await QueryPayloadListAsync<RecordingSegment>($"SELECT payload FROM recording_segments ORDER BY indexed_at DESC LIMIT {Math.Max(1, limit)}", null, cancellationToken);
        }

        return await QueryPayloadListAsync<RecordingSegment>($"SELECT payload FROM recording_segments WHERE device_id = $id ORDER BY indexed_at DESC LIMIT {Math.Max(1, limit)}", parameters => parameters.AddWithValue("$id", deviceId.Value.ToString()), cancellationToken);
    }

    public async Task SaveSemanticWriteObservationsAsync(IEnumerable<SemanticWriteObservation> observations, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var observation in observations)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO semantic_write_observations (id, device_id, payload, timestamp) VALUES ($id, $device_id, $payload, $timestamp) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload, timestamp = excluded.timestamp";
                command.Parameters.AddWithValue("$id", observation.Id.ToString());
                command.Parameters.AddWithValue("$device_id", observation.DeviceId.ToString());
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(observation, _serializerOptions));
                command.Parameters.AddWithValue("$timestamp", observation.Timestamp.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<SemanticWriteObservation>> GetSemanticWriteObservationsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
    {
        if (deviceId is null)
        {
            return await QueryPayloadListAsync<SemanticWriteObservation>($"SELECT payload FROM semantic_write_observations ORDER BY timestamp DESC LIMIT {Math.Max(1, limit)}", null, cancellationToken);
        }

        return await QueryPayloadListAsync<SemanticWriteObservation>($"SELECT payload FROM semantic_write_observations WHERE device_id = $id ORDER BY timestamp DESC LIMIT {Math.Max(1, limit)}", parameters => parameters.AddWithValue("$id", deviceId.Value.ToString()), cancellationToken);
    }

    public async Task SaveFieldConstraintProfilesAsync(IEnumerable<FieldConstraintProfile> profiles, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var profile in profiles)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO field_constraint_profiles (constraint_key, firmware_fingerprint, payload, updated_at) VALUES ($id, $firmware, $payload, $updated) ON CONFLICT(constraint_key) DO UPDATE SET firmware_fingerprint = excluded.firmware_fingerprint, payload = excluded.payload, updated_at = excluded.updated_at";
                command.Parameters.AddWithValue("$id", BuildConstraintProfileKey(profile));
                command.Parameters.AddWithValue("$firmware", profile.FirmwareFingerprint);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(profile, _serializerOptions));
                command.Parameters.AddWithValue("$updated", profile.UpdatedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<FieldConstraintProfile>> GetFieldConstraintProfilesAsync(string? firmwareFingerprint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(firmwareFingerprint))
        {
            return await QueryPayloadListAsync<FieldConstraintProfile>("SELECT payload FROM field_constraint_profiles ORDER BY updated_at DESC", null, cancellationToken);
        }

        return await QueryPayloadListAsync<FieldConstraintProfile>("SELECT payload FROM field_constraint_profiles WHERE firmware_fingerprint = $firmware ORDER BY updated_at DESC", parameters => parameters.AddWithValue("$firmware", firmwareFingerprint), cancellationToken);
    }

    public async Task SaveDependencyMatrixProfilesAsync(IEnumerable<DependencyMatrixProfile> profiles, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var profile in profiles)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO dependency_matrix_profiles (matrix_key, firmware_fingerprint, payload, updated_at) VALUES ($id, $firmware, $payload, $updated) ON CONFLICT(matrix_key) DO UPDATE SET firmware_fingerprint = excluded.firmware_fingerprint, payload = excluded.payload, updated_at = excluded.updated_at";
                command.Parameters.AddWithValue("$id", BuildDependencyMatrixKey(profile));
                command.Parameters.AddWithValue("$firmware", profile.FirmwareFingerprint);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(profile, _serializerOptions));
                command.Parameters.AddWithValue("$updated", profile.UpdatedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<DependencyMatrixProfile>> GetDependencyMatrixProfilesAsync(string? firmwareFingerprint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(firmwareFingerprint))
        {
            return await QueryPayloadListAsync<DependencyMatrixProfile>("SELECT payload FROM dependency_matrix_profiles ORDER BY updated_at DESC", null, cancellationToken);
        }

        return await QueryPayloadListAsync<DependencyMatrixProfile>("SELECT payload FROM dependency_matrix_profiles WHERE firmware_fingerprint = $firmware ORDER BY updated_at DESC", parameters => parameters.AddWithValue("$firmware", firmwareFingerprint), cancellationToken);
    }

    public async Task SaveImageControlInventoryAsync(IEnumerable<ImageControlInventoryItem> items, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var item in items)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO image_control_inventory (inventory_key, device_id, firmware_fingerprint, payload, captured_at) VALUES ($key, $device_id, $firmware, $payload, $captured_at) ON CONFLICT(inventory_key) DO UPDATE SET firmware_fingerprint = excluded.firmware_fingerprint, payload = excluded.payload, captured_at = excluded.captured_at";
                command.Parameters.AddWithValue("$key", BuildImageInventoryKey(item));
                command.Parameters.AddWithValue("$device_id", item.DeviceId.ToString());
                command.Parameters.AddWithValue("$firmware", item.FirmwareFingerprint);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item, _serializerOptions));
                command.Parameters.AddWithValue("$captured_at", item.CapturedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<ImageControlInventoryItem>> GetImageControlInventoryAsync(Guid deviceId, CancellationToken cancellationToken)
        => await QueryPayloadListAsync<ImageControlInventoryItem>("SELECT payload FROM image_control_inventory WHERE device_id = $id ORDER BY captured_at DESC", parameters => parameters.AddWithValue("$id", deviceId.ToString()), cancellationToken);

    public async Task SaveImageBehaviorMapsAsync(IEnumerable<ImageFieldBehaviorMap> maps, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var map in maps)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO image_behavior_maps (behavior_key, device_id, firmware_fingerprint, payload, updated_at) VALUES ($key, $device_id, $firmware, $payload, $updated_at) ON CONFLICT(behavior_key) DO UPDATE SET firmware_fingerprint = excluded.firmware_fingerprint, payload = excluded.payload, updated_at = excluded.updated_at";
                command.Parameters.AddWithValue("$key", BuildImageBehaviorKey(map));
                command.Parameters.AddWithValue("$device_id", map.DeviceId.ToString());
                command.Parameters.AddWithValue("$firmware", map.FirmwareFingerprint);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(map, _serializerOptions));
                command.Parameters.AddWithValue("$updated_at", map.UpdatedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<ImageFieldBehaviorMap>> GetImageBehaviorMapsAsync(Guid deviceId, CancellationToken cancellationToken)
        => await QueryPayloadListAsync<ImageFieldBehaviorMap>("SELECT payload FROM image_behavior_maps WHERE device_id = $id ORDER BY updated_at DESC", parameters => parameters.AddWithValue("$id", deviceId.ToString()), cancellationToken);

    public async Task SaveImageWritableTestSetAsync(ImageWritableTestSetProfile profile, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO image_writable_test_sets (device_id, firmware_fingerprint, payload, updated_at) VALUES ($id, $firmware, $payload, $updated_at) ON CONFLICT(device_id) DO UPDATE SET firmware_fingerprint = excluded.firmware_fingerprint, payload = excluded.payload, updated_at = excluded.updated_at";
            command.Parameters.AddWithValue("$id", profile.DeviceId.ToString());
            command.Parameters.AddWithValue("$firmware", profile.FirmwareFingerprint);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(profile, _serializerOptions));
            command.Parameters.AddWithValue("$updated_at", profile.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ImageWritableTestSetProfile?> GetImageWritableTestSetAsync(Guid deviceId, CancellationToken cancellationToken)
        => await QuerySinglePayloadAsync<ImageWritableTestSetProfile>("SELECT payload FROM image_writable_test_sets WHERE device_id = $id", parameters => parameters.AddWithValue("$id", deviceId.ToString()), cancellationToken);

    public async Task SaveGroupedApplyProfilesAsync(IEnumerable<GroupedApplyProfile> profiles, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var profile in profiles)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO grouped_apply_profiles (profile_key, device_id, firmware_fingerprint, payload, updated_at) VALUES ($key, $device_id, $firmware, $payload, $updated_at) ON CONFLICT(profile_key) DO UPDATE SET firmware_fingerprint = excluded.firmware_fingerprint, payload = excluded.payload, updated_at = excluded.updated_at";
                command.Parameters.AddWithValue("$key", BuildGroupedApplyProfileKey(profile));
                command.Parameters.AddWithValue("$device_id", profile.DeviceId.ToString());
                command.Parameters.AddWithValue("$firmware", profile.FirmwareFingerprint);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(profile, _serializerOptions));
                command.Parameters.AddWithValue("$updated_at", profile.UpdatedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<GroupedApplyProfile>> GetGroupedApplyProfilesAsync(Guid? deviceId, string? firmwareFingerprint, CancellationToken cancellationToken)
    {
        if (deviceId is null && string.IsNullOrWhiteSpace(firmwareFingerprint))
        {
            return await QueryPayloadListAsync<GroupedApplyProfile>("SELECT payload FROM grouped_apply_profiles ORDER BY updated_at DESC", null, cancellationToken);
        }

        if (deviceId is Guid id && !string.IsNullOrWhiteSpace(firmwareFingerprint))
        {
            return await QueryPayloadListAsync<GroupedApplyProfile>(
                "SELECT payload FROM grouped_apply_profiles WHERE device_id = $id AND firmware_fingerprint = $firmware ORDER BY updated_at DESC",
                parameters =>
                {
                    parameters.AddWithValue("$id", id.ToString());
                    parameters.AddWithValue("$firmware", firmwareFingerprint);
                },
                cancellationToken);
        }

        if (deviceId is Guid onlyDevice)
        {
            return await QueryPayloadListAsync<GroupedApplyProfile>(
                "SELECT payload FROM grouped_apply_profiles WHERE device_id = $id ORDER BY updated_at DESC",
                parameters => parameters.AddWithValue("$id", onlyDevice.ToString()),
                cancellationToken);
        }

        return await QueryPayloadListAsync<GroupedApplyProfile>(
            "SELECT payload FROM grouped_apply_profiles WHERE firmware_fingerprint = $firmware ORDER BY updated_at DESC",
            parameters => parameters.AddWithValue("$firmware", firmwareFingerprint),
            cancellationToken);
    }

    public async Task SaveGroupedRetestResultsAsync(IEnumerable<GroupedUnsupportedRetestResult> results, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var result in results)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO grouped_retest_results (result_key, device_id, firmware_fingerprint, payload, captured_at) VALUES ($key, $device_id, $firmware, $payload, $captured_at) ON CONFLICT(result_key) DO UPDATE SET firmware_fingerprint = excluded.firmware_fingerprint, payload = excluded.payload, captured_at = excluded.captured_at";
                command.Parameters.AddWithValue("$key", BuildGroupedRetestResultKey(result));
                command.Parameters.AddWithValue("$device_id", result.DeviceId.ToString());
                command.Parameters.AddWithValue("$firmware", result.FirmwareFingerprint);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(result, _serializerOptions));
                command.Parameters.AddWithValue("$captured_at", result.CapturedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<GroupedUnsupportedRetestResult>> GetGroupedRetestResultsAsync(Guid deviceId, int limit, CancellationToken cancellationToken)
        => await QueryPayloadListAsync<GroupedUnsupportedRetestResult>(
            $"SELECT payload FROM grouped_retest_results WHERE device_id = $id ORDER BY captured_at DESC LIMIT {Math.Max(1, limit)}",
            parameters => parameters.AddWithValue("$id", deviceId.ToString()),
            cancellationToken);

    private async Task UpsertPayloadAsync<T>(StoreTable table, string key, T payload, DateTimeOffset timestamp, CancellationToken cancellationToken, string? deviceId = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // CommandText is produced entirely from the closed StoreTable enum and a
            // caller-supplied flag (withDeviceId). No string interpolation of caller
            // data ends up in the SQL structure.
            command.CommandText = UpsertCommandText(table, withDeviceId: deviceId is not null);
            command.Parameters.AddWithValue("$key", key);
            if (deviceId is not null)
            {
                command.Parameters.AddWithValue("$device_id", deviceId);
            }
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload, _serializerOptions));
            command.Parameters.AddWithValue("$timestamp", timestamp.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Pre-written CommandText strings for every supported (StoreTable, deviceId-bearing)
    // combination. The dispatch table covers EXACTLY the (table, withDeviceId) pairs that
    // callers in this file actually use. Unsupported combinations throw — the punch list
    // ("structurally impossible for a future caller to pass a variable table name") requires
    // us to reject out-of-range enums loudly rather than silently composing unsafe SQL.
    private static string UpsertCommandText(StoreTable table, bool withDeviceId)
    {
        return (table, withDeviceId) switch
        {
            (StoreTable.CapabilityMaps, false) => "INSERT INTO capability_maps (device_id, payload, updated_at) VALUES ($key, $payload, $timestamp) ON CONFLICT(device_id) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at",
            (StoreTable.SettingsSnapshots, false) => "INSERT INTO settings_snapshots (device_id, payload, updated_at) VALUES ($key, $payload, $timestamp) ON CONFLICT(device_id) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at",
            (StoreTable.ProtocolManifests, false) => "INSERT INTO protocol_manifests (manifest_id, payload, updated_at) VALUES ($key, $payload, $timestamp) ON CONFLICT(manifest_id) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at",
            (StoreTable.RecordingProfiles, false) => "INSERT INTO recording_profiles (id, payload, updated_at) VALUES ($key, $payload, $timestamp) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at",
            (StoreTable.RecordingProfiles, true) => "INSERT INTO recording_profiles (id, device_id, payload, updated_at) VALUES ($key, $device_id, $payload, $timestamp) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at, device_id = excluded.device_id",
            (StoreTable.ProbeSessions, false) => "INSERT INTO probe_sessions (id, payload, started_at) VALUES ($key, $payload, $timestamp) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload, started_at = excluded.started_at",
            (StoreTable.ProbeSessions, true) => "INSERT INTO probe_sessions (id, device_id, payload, started_at) VALUES ($key, $device_id, $payload, $timestamp) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload, started_at = excluded.started_at, device_id = excluded.device_id",
            _ => throw new InvalidOperationException(
                $"UpsertCommandText has no (table={table}, withDeviceId={withDeviceId}) entry. " +
                "Add a hard-coded pre-written CommandText literal for the new shape — this dispatch " +
                "table is the structural guarantee that closes the P0 #5 SQL-identifier injection " +
                "vector. Do NOT compose the CommandText via string interpolation of caller-supplied " +
                "data; that reopens the injection path this enum was added to close.")
        };
    }

    private async Task InsertPayloadAsync<T>(StoreTable table, string id, T payload, DateTimeOffset timestamp, CancellationToken cancellationToken, string? deviceId = null, string? endpoint = null, string? method = null, string? adapterName = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var identifier = SqlIdentifierMap.For(table);
            var timestampColumn = identifier.TimestampColumn;
            if (table == StoreTable.EndpointTranscripts)
            {
                command.CommandText = "INSERT OR REPLACE INTO endpoint_transcripts (id, device_id, endpoint, method, adapter_name, payload, timestamp) VALUES ($id, $device_id, $endpoint, $method, $adapter_name, $payload, $timestamp)";
                command.Parameters.AddWithValue("$device_id", deviceId ?? string.Empty);
                command.Parameters.AddWithValue("$endpoint", endpoint ?? string.Empty);
                command.Parameters.AddWithValue("$method", method ?? "GET");
                command.Parameters.AddWithValue("$adapter_name", adapterName ?? string.Empty);
            }
            else if (deviceId is null)
            {
                command.CommandText = InsertCommandText(table, withDeviceId: false);
            }
            else
            {
                command.CommandText = InsertCommandText(table, withDeviceId: true);
                command.Parameters.AddWithValue("$device_id", deviceId);
            }
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload, _serializerOptions));
            command.Parameters.AddWithValue("$timestamp", timestamp.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Pre-written CommandText strings for every supported StoreTable / deviceId combination.
    private static string InsertCommandText(StoreTable table, bool withDeviceId)
    {
        return (table, withDeviceId) switch
        {
            (StoreTable.AuditEntries, false) => "INSERT OR REPLACE INTO audit_entries (id, payload, timestamp) VALUES ($id, $payload, $timestamp)",
            (StoreTable.AuditEntries, true) => "INSERT OR REPLACE INTO audit_entries (id, device_id, payload, timestamp) VALUES ($id, $device_id, $payload, $timestamp)",
            (StoreTable.FirmwareArtifacts, false) => "INSERT OR REPLACE INTO firmware_artifacts (id, payload, analyzed_at) VALUES ($id, $payload, $timestamp)",
            (StoreTable.FirmwareArtifacts, true) => "INSERT OR REPLACE INTO firmware_artifacts (id, device_id, payload, analyzed_at) VALUES ($id, $device_id, $payload, $timestamp)",
            (StoreTable.PersistenceVerificationResults, false) => "INSERT OR REPLACE INTO persistence_verification_results (id, payload, timestamp) VALUES ($id, $payload, $timestamp)",
            (StoreTable.PersistenceVerificationResults, true) => "INSERT OR REPLACE INTO persistence_verification_results (id, device_id, payload, timestamp) VALUES ($id, $device_id, $payload, $timestamp)",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "InsertCommandText has no entry for this StoreTable. Add it before using — endpoint_transcripts is handled separately in InsertPayloadAsync.")
        };
    }

    private async Task<IReadOnlyCollection<T>> QueryPayloadListAsync<T>(string sql, Action<SqliteParameterCollection>? bind, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind?.Invoke(command.Parameters);
            var items = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var payload = reader.GetString(0);
                var item = JsonSerializer.Deserialize<T>(payload, _serializerOptions);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T?> QuerySinglePayloadAsync<T>(string sql, Action<SqliteParameterCollection>? bind, CancellationToken cancellationToken)
    {
        var items = await QueryPayloadListAsync<T>(sql, bind, cancellationToken);
        return items.FirstOrDefault();
    }

    private SqliteConnection OpenConnection()
        => new($"Data Source={DatabasePath}");

    private static string BuildDedupeKey(DeviceIdentity device)
        => !string.IsNullOrWhiteSpace(device.IpAddress)
            ? device.IpAddress
            : device.DeviceId ?? device.EseeId ?? device.Id.ToString("N");

    private static string BuildValidationKey(EndpointValidationResult result)
        => $"{result.DeviceId:N}:{result.AdapterName}:{result.Method}:{result.Endpoint}:{result.FirmwareFingerprint}";

    private static string BuildFieldKey(NormalizedSettingField field)
        => $"{field.DeviceId:N}:{field.GroupKind}:{field.FieldKey}:{field.SourceEndpoint}:{field.FirmwareFingerprint}";

    private static string BuildConstraintProfileKey(FieldConstraintProfile profile)
        => $"{profile.FirmwareFingerprint}:{profile.ContractKey}:{profile.FieldKey}";

    private static string BuildDependencyMatrixKey(DependencyMatrixProfile profile)
        => $"{profile.FirmwareFingerprint}:{profile.GroupName}";

    private static string BuildImageInventoryKey(ImageControlInventoryItem item)
        => $"{item.DeviceId:N}:{item.FirmwareFingerprint}:{item.FieldKey}";

    private static string BuildImageBehaviorKey(ImageFieldBehaviorMap map)
        => $"{map.DeviceId:N}:{map.FirmwareFingerprint}:{map.FieldKey}:{map.ContractKey}";

    private static string BuildGroupedApplyProfileKey(GroupedApplyProfile profile)
        => $"{profile.DeviceId:N}:{profile.FirmwareFingerprint}:{profile.IpAddress}:{profile.GroupKind}";

    private static string BuildGroupedRetestResultKey(GroupedUnsupportedRetestResult result)
        => $"{result.DeviceId:N}:{result.FirmwareFingerprint}:{result.FieldKey}:{result.SourceEndpoint}";

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

