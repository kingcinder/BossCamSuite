using System.Text.Json;
using BossCam.Contracts;
using BossCam.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Imports;

public sealed class IpcamSuiteImportProvider(IOptions<BossCamRuntimeOptions> options) : IDeviceImportProvider
{
    public string Name => "IPCamSuite";

    public Task<IReadOnlyCollection<DeviceIdentity>> ImportAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(options.Value.IpcamSuiteDirectory, "MAINSET.INI");
        if (!File.Exists(path))
        {
            return Task.FromResult<IReadOnlyCollection<DeviceIdentity>>([]);
        }

        var ini = IniParser.Parse(File.ReadAllLines(path));
        ini.TryGetValue("Priview", out var previewSection);
        ini.TryGetValue("AdvancedOEM", out var oemSection);
        ini.TryGetValue("m_chiptype", out var chipSection);

        var devices = new List<DeviceIdentity>();
        foreach (var section in ini.Where(section => section.Key.StartsWith("ipc", StringComparison.OrdinalIgnoreCase)))
        {
            var ip = section.Value.GetValueOrDefault("ip");
            if (string.IsNullOrWhiteSpace(ip))
            {
                continue;
            }

            var port = int.TryParse(section.Value.GetValueOrDefault("port"), out var parsedPort) ? parsedPort : 80;
            var deviceId = section.Value.GetValueOrDefault("deviceid");
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "IPCamSuite",
                ["section"] = section.Key,
                ["chipType"] = chipSection?.GetValueOrDefault("type") ?? string.Empty,
                ["oemWhiteLight"] = oemSection?.GetValueOrDefault("bOemWhiteLight") ?? string.Empty,
                ["oemInfraRed"] = oemSection?.GetValueOrDefault("bOemInfraRed") ?? string.Empty,
                ["irCutModeConfig"] = oemSection?.GetValueOrDefault("bIRCutModeConfig") ?? string.Empty
            };

            devices.Add(new DeviceIdentity
            {
                Name = $"IPCamSuite {section.Key.ToUpperInvariant()}",
                DeviceId = deviceId,
                IpAddress = ip,
                Port = port,
                LoginName = previewSection?.GetValueOrDefault("csUsername"),
                Password = previewSection?.GetValueOrDefault("csPasswd"),
                HardwareModel = chipSection?.GetValueOrDefault("type"),
                DeviceType = "IPC",
                TransportProfiles =
                [
                    new TransportProfile { Kind = TransportKind.LanRest, Address = $"http://{ip}:{port}", Rank = 10 },
                    new TransportProfile { Kind = TransportKind.LanPrivateHttp, Address = $"http://{ip}:{port}", Rank = 20 },
                    new TransportProfile { Kind = TransportKind.BubbleFlv, Address = $"http://{ip}:{port}/bubble/live?ch=1&stream=0", Rank = 40 }
                ],
                Metadata = metadata
            });
        }

        return Task.FromResult<IReadOnlyCollection<DeviceIdentity>>(devices);
    }
}

public sealed class EseeCloudImportProvider(IOptions<BossCamRuntimeOptions> options) : IDeviceImportProvider
{
    public string Name => "EseeCloud";

    public async Task<IReadOnlyCollection<DeviceIdentity>> ImportAsync(CancellationToken cancellationToken)
    {
        var devices = new List<DeviceIdentity>();
        var dataDirectory = options.Value.EseeCloudDataDirectory;
        var confPath = Path.Combine(dataDirectory, "conf.json");
        var paramPath = Path.Combine(dataDirectory, "param.json");
        var dbPath = Path.Combine(dataDirectory, "cms_data.db");

        Dictionary<string, string> globalMetadata = new(StringComparer.OrdinalIgnoreCase);
        foreach (var jsonPath in new[] { confPath, paramPath })
        {
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, cancellationToken));
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                globalMetadata[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText();
            }
        }

        if (!File.Exists(dbPath))
        {
            return devices;
        }

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT eseeid, ip, name, port, login_name, pwd, connect_mode, type, ssid, ssid_pwd FROM t_device";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var eseeId = reader.IsDBNull(0) ? null : reader.GetString(0);
            var ip = reader.IsDBNull(1) ? null : reader.GetString(1);
            var name = reader.IsDBNull(2) ? null : reader.GetString(2);
            var port = reader.IsDBNull(3) ? 80 : reader.GetInt32(3);
            var loginName = reader.IsDBNull(4) ? null : reader.GetString(4);
            var passwordCiphertext = reader.IsDBNull(5) ? null : reader.GetString(5);
            var connectMode = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
            var type = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);
            var ssid = reader.IsDBNull(8) ? null : reader.GetString(8);
            var ssidPwd = reader.IsDBNull(9) ? null : reader.GetString(9);

            var metadata = new Dictionary<string, string>(globalMetadata, StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "EseeCloud",
                ["connect_mode"] = connectMode.ToString(),
                ["ssid"] = ssid ?? string.Empty,
                ["ssid_pwd"] = ssidPwd ?? string.Empty
            };

            var profiles = new List<TransportProfile>();
            if (!string.IsNullOrWhiteSpace(ip))
            {
                profiles.Add(new TransportProfile { Kind = TransportKind.LanRest, Address = $"http://{ip}:{port}", Rank = 10 });
                profiles.Add(new TransportProfile { Kind = TransportKind.LanPrivateHttp, Address = $"http://{ip}:{port}", Rank = 20 });
            }
            if (!string.IsNullOrWhiteSpace(eseeId))
            {
                profiles.Add(new TransportProfile { Kind = TransportKind.EseeJuanP2P, Address = $"esee://{eseeId}", Rank = 60, IsRemote = true });
                profiles.Add(new TransportProfile { Kind = TransportKind.RemoteCommand, Address = $"remote://{eseeId}", Rank = 65, IsRemote = true });
            }

            devices.Add(new DeviceIdentity
            {
                EseeId = eseeId,
                Name = name,
                IpAddress = ip,
                Port = port,
                LoginName = loginName,
                PasswordCiphertext = passwordCiphertext,
                DeviceType = MapDeviceType(type),
                Metadata = metadata,
                TransportProfiles = profiles
            });
        }

        return devices;
    }

    private static string MapDeviceType(int type)
        => type switch
        {
            0 => "IPC",
            1 => "DVR",
            2 => "NVR",
            4 => "VRCAM",
            _ => $"Type{type}"
        };
}

internal static class IniParser
{
    public static Dictionary<string, Dictionary<string, string>> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[line[1..^1]] = current;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            current[line[..index].Trim()] = line[(index + 1)..].Trim();
        }

        return result;
    }
}
