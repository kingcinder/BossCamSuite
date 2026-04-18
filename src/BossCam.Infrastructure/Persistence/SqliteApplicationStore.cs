using System.Text.Json;
using System.Text.Json.Serialization;
using BossCam.Contracts;
using BossCam.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Persistence;

public sealed class SqliteApplicationStore(IOptions<BossCamRuntimeOptions> options) : IApplicationStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = CreateSerializerOptions();

    private string DatabasePath => options.Value.DatabasePath;

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
                "CREATE TABLE IF NOT EXISTS firmware_artifacts (id TEXT PRIMARY KEY, payload TEXT NOT NULL, analyzed_at TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS recording_profiles (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, payload TEXT NOT NULL, updated_at TEXT NOT NULL)"
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
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO devices (id, dedupe_key, payload, updated_at) VALUES ($id, $key, $payload, $updated) ON CONFLICT(dedupe_key) DO UPDATE SET id = excluded.id, payload = excluded.payload, updated_at = excluded.updated_at";
                command.Parameters.AddWithValue("$id", device.Id.ToString());
                command.Parameters.AddWithValue("$key", BuildDedupeKey(device));
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(device, _serializerOptions));
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<DeviceIdentity>> GetDevicesAsync(CancellationToken cancellationToken)
        => await QueryPayloadListAsync<DeviceIdentity>("SELECT payload FROM devices ORDER BY updated_at DESC", null, cancellationToken);

    public async Task<DeviceIdentity?> GetDeviceAsync(Guid id, CancellationToken cancellationToken)
        => await QuerySinglePayloadAsync<DeviceIdentity>("SELECT payload FROM devices WHERE id = $id", parameters => parameters.AddWithValue("$id", id.ToString()), cancellationToken);

    public async Task SaveCapabilityMapAsync(CapabilityMap capabilityMap, CancellationToken cancellationToken)
        => await UpsertPayloadAsync("capability_maps", "device_id", capabilityMap.DeviceId.ToString(), capabilityMap, capabilityMap.CapturedAt, cancellationToken);

    public async Task<CapabilityMap?> GetCapabilityMapAsync(Guid deviceId, CancellationToken cancellationToken)
        => await QuerySinglePayloadAsync<CapabilityMap>("SELECT payload FROM capability_maps WHERE device_id = $id", parameters => parameters.AddWithValue("$id", deviceId.ToString()), cancellationToken);

    public async Task SaveSettingsSnapshotAsync(SettingsSnapshot snapshot, CancellationToken cancellationToken)
        => await UpsertPayloadAsync("settings_snapshots", "device_id", snapshot.DeviceId.ToString(), snapshot, snapshot.CapturedAt, cancellationToken);

    public async Task<SettingsSnapshot?> GetSettingsSnapshotAsync(Guid deviceId, CancellationToken cancellationToken)
        => await QuerySinglePayloadAsync<SettingsSnapshot>("SELECT payload FROM settings_snapshots WHERE device_id = $id", parameters => parameters.AddWithValue("$id", deviceId.ToString()), cancellationToken);

    public async Task AddAuditEntryAsync(WriteAuditEntry entry, CancellationToken cancellationToken)
        => await InsertPayloadAsync("audit_entries", entry.Id.ToString(), entry, entry.Timestamp, cancellationToken, deviceId: entry.DeviceId.ToString());

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
            await UpsertPayloadAsync("protocol_manifests", "manifest_id", manifest.ManifestId, manifest, DateTimeOffset.UtcNow, cancellationToken);
        }
    }

    public async Task<IReadOnlyCollection<ProtocolManifest>> GetProtocolManifestsAsync(CancellationToken cancellationToken)
        => await QueryPayloadListAsync<ProtocolManifest>("SELECT payload FROM protocol_manifests ORDER BY updated_at DESC", null, cancellationToken);

    public async Task AddFirmwareArtifactAsync(FirmwareArtifact artifact, CancellationToken cancellationToken)
        => await InsertPayloadAsync("firmware_artifacts", artifact.Id.ToString(), artifact, artifact.AnalyzedAt, cancellationToken);

    public async Task<IReadOnlyCollection<FirmwareArtifact>> GetFirmwareArtifactsAsync(CancellationToken cancellationToken)
        => await QueryPayloadListAsync<FirmwareArtifact>("SELECT payload FROM firmware_artifacts ORDER BY analyzed_at DESC", null, cancellationToken);

    public async Task SaveRecordingProfilesAsync(IEnumerable<RecordingProfile> profiles, CancellationToken cancellationToken)
    {
        foreach (var profile in profiles)
        {
            await UpsertPayloadAsync("recording_profiles", "id", profile.Id.ToString(), profile, profile.UpdatedAt, cancellationToken, deviceId: profile.DeviceId.ToString());
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

    private async Task UpsertPayloadAsync<T>(string tableName, string keyColumn, string key, T payload, DateTimeOffset timestamp, CancellationToken cancellationToken, string? deviceId = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var timestampColumn = tableName switch
            {
                "capability_maps" => "updated_at",
                "settings_snapshots" => "updated_at",
                "protocol_manifests" => "updated_at",
                "recording_profiles" => "updated_at",
                _ => "updated_at"
            };
            var deviceIdClause = deviceId is null ? string.Empty : ", device_id = excluded.device_id";
            var deviceIdInsert = deviceId is null ? string.Empty : ", device_id";
            var deviceIdValues = deviceId is null ? string.Empty : ", $device_id";
            command.CommandText = $"INSERT INTO {tableName} ({keyColumn}{deviceIdInsert}, payload, {timestampColumn}) VALUES ($key{deviceIdValues}, $payload, $timestamp) ON CONFLICT({keyColumn}) DO UPDATE SET payload = excluded.payload, {timestampColumn} = excluded.{timestampColumn}{deviceIdClause}";
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

    private async Task InsertPayloadAsync<T>(string tableName, string id, T payload, DateTimeOffset timestamp, CancellationToken cancellationToken, string? deviceId = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var timestampColumn = tableName switch
            {
                "audit_entries" => "timestamp",
                "firmware_artifacts" => "analyzed_at",
                _ => "updated_at"
            };
            if (deviceId is null)
            {
                command.CommandText = $"INSERT OR REPLACE INTO {tableName} (id, payload, {timestampColumn}) VALUES ($id, $payload, $timestamp)";
            }
            else
            {
                command.CommandText = $"INSERT OR REPLACE INTO {tableName} (id, device_id, payload, {timestampColumn}) VALUES ($id, $device_id, $payload, $timestamp)";
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
        => device.DeviceId ?? device.EseeId ?? device.IpAddress ?? device.Id.ToString("N");

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

